# Checks groups as Rhino sublayers: adding, renaming and deleting them (with their layer),
# a deleted group coming back with its layer, sublayers made or renamed in Rhino, a file
# from before groups were layers, and the agent's context groups, in a NEW Rhino process
# with /scheme=CrayonValidation /notemplate.
# Results go to tests/Native/bin/groups-delete.log. Makes no model requests.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.Geometry import Point3d, PolylineCurve
from Rhino.DocObjects import ObjectAttributes, Layer

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
if not os.path.isdir(output):
    os.makedirs(output)
log = open(os.path.join(output, "groups-delete.log"), "w")
passed = [0]

def write(message):
    log.write(str(message) + "\n")
    log.flush()

def check(ok, description):
    if not ok:
        raise Exception("FAIL " + description)
    passed[0] += 1
    write("PASS " + description)

doc = None
try:
    assembly = Assembly.LoadFile(os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp"))
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
    groups_type = assembly.GetType("RhinoSketchingTool.AnnotationGroups", True)
    context_type = assembly.GetType("RhinoSketchingTool.DrawingContext", True)
    controller_type = assembly.GetType("RhinoSketchingTool.SketchController", True)

    def call(t, name, *args):
        method = [m for m in t.GetMethods(flags) if m.Name == name and len(m.GetParameters()) == len(args)][0]
        return method.Invoke(None, System.Array[System.Object](list(args)))

    doc = Rhino.RhinoDoc.ActiveDoc
    check(doc is not None and doc.Objects.Count == 0, "New blank document")

    def curve(x):
        return PolylineCurve([Point3d(x, 0, 0), Point3d(x + 1, 1, 0), Point3d(x + 2, 0, 0)])

    def add_sketch(x):
        # The way Crayon adds one: on the layer of the group being drawn in.
        return doc.Objects.AddCurve(curve(x), call(controller_type, "StrokeAttributes", doc))

    def old_sketch(group, x):
        # The way a build from before groups were layers left one: on the Sketch layer itself, tagged.
        attributes = ObjectAttributes()
        attributes.LayerIndex = doc.Layers.FindByFullPath("Sketch", -1)
        attributes.SetUserString("SketchingTool.Stroke", "1")
        if group:
            attributes.SetUserString("Crayon.Group", str(group))
        return doc.Objects.AddCurve(curve(x), attributes)

    def get(obj, name):
        t = obj.GetType()
        f = t.GetField(name, flags)
        return f.GetValue(obj) if f is not None else t.GetProperty(name, flags).GetValue(obj, None)

    class State(object):
        def __init__(self, raw):
            self.Sketches = [Group(g) for g in get(raw, "Sketches")]
            self.Comments = [Group(g) for g in get(raw, "Comments")]
            self.ActiveSketch = get(raw, "ActiveSketch")

    class Group(object):
        def __init__(self, raw):
            self.Id = get(raw, "Id")
            self.Name = str(get(raw, "Name"))
            self.Visible = get(raw, "Visible")
            self.Context = get(raw, "Context")

    def state():
        return State(call(groups_type, "Read", doc))

    def sketch_ids():
        return sorted([g.Id for g in state().Sketches])

    def names():
        return [g.Name for g in state().Sketches]

    def strokes():
        return len(call(controller_type, "StrokeObjects", doc))

    def layer(path):
        index = doc.Layers.FindByFullPath(path, -1)
        return doc.Layers[index] if index >= 0 else None

    def layer_of(id):
        return doc.Layers[doc.Objects.FindId(id).Attributes.LayerIndex].FullPath

    check(sketch_ids() == [] and state().ActiveSketch == 0, "A new document has no groups")
    check(layer("Sketch") is None, "... and no Sketch layer")
    call(groups_type, "Create", doc, False)          # Group 1
    check(sketch_ids() == [1] and state().ActiveSketch == 1, "A new group is added and is the one drawn in")
    check(layer("Sketch::Group 1") is not None, "A group is a sublayer of the Sketch layer")
    first = add_sketch(0)
    check(layer_of(first) == "Sketch::Group 1", "A sketch is on its group's layer")
    call(groups_type, "Create", doc, False)          # Group 2, now drawn in
    check(sketch_ids() == [1, 2] and state().ActiveSketch == 2, "Another group is added and drawn in")
    add_sketch(5)
    add_sketch(9)
    call(groups_type, "Create", doc, False)          # Group 3
    call(groups_type, "Rename", doc, False, 3, "Roof ideas")
    check(names() == ["Group 1", "Group 2", "Roof ideas"], "Group renames")
    check(layer("Sketch::Roof ideas") is not None and layer("Sketch::Group 3") is None, "... and so does its Rhino layer")

    snapshot = call(context_type, "Capture", doc)
    check(sorted(get(snapshot, "SketchGroups")) == [1, 2], "Agent context = ticked groups that have sketches")
    call(groups_type, "ToggleContext", doc, False, 1)
    snapshot = call(context_type, "Capture", doc)
    check(sorted(get(snapshot, "SketchGroups")) == [2], "Unticking a group takes it out of the agent's context")
    call(groups_type, "ToggleContext", doc, False, 1)
    call(groups_type, "ToggleGroup", doc, False, 1)
    check(not layer("Sketch::Group 1").IsVisible, "A group's eye turns its Rhino layer off")
    snapshot = call(context_type, "Capture", doc)
    check(sorted(get(snapshot, "SketchGroups")) == [1, 2], "Hiding a group with its eye keeps it in the agent's context while ticked")
    call(groups_type, "ToggleGroup", doc, False, 1)
    check(layer("Sketch::Group 1").IsVisible, "... and on again")

    # Deleting: the sketches and the layer go.
    call(groups_type, "SetActive", doc, False, 2)
    second = layer("Sketch::Group 2").Index
    call(groups_type, "Delete", doc, False, 2)
    check(sketch_ids() == [1, 3] and strokes() == 1, "Deleting a group removes it and its two sketches")
    check(layer("Sketch::Group 2") is None, "... and its Rhino layer")
    check(state().ActiveSketch == 3, "The last remaining group is drawn in when the one drawn in goes")
    # Rhino's Undo brings the layer back; so does this.
    check(doc.Layers.Undelete(second), "The deleted layer can be brought back")
    check(sketch_ids() == [1, 2, 3], "A group whose layer comes back (Undo) is a group again, with its number")
    call(groups_type, "Delete", doc, False, 2)
    check(sketch_ids() == [1, 3], "... and can be deleted again")

    # What is done to the layers in Rhino is done to the groups.
    made = Layer.GetDefaultLayerProperties()
    made.Name = "Site notes"
    made.ParentLayerId = layer("Sketch").Id
    check(doc.Layers.Add(made) >= 0, "A sublayer is added to Sketch in Rhino")
    check(names() == ["Group 1", "Roof ideas", "Site notes"], "A sublayer made in Rhino is a group")
    layer("Sketch::Site notes").Name = "Site sketches"
    check(names() == ["Group 1", "Roof ideas", "Site sketches"], "A layer renamed in Rhino renames its group")
    check(doc.Layers.Delete(layer("Sketch::Site sketches").Index, True), "The sublayer is deleted in Rhino")
    check(names() == ["Group 1", "Roof ideas"], "A layer deleted in Rhino deletes its group")

    call(groups_type, "Delete", doc, False, 1)
    check(sketch_ids() == [3] and strokes() == 0, "Group 1 can be deleted too")
    call(groups_type, "Delete", doc, False, 3)
    check(sketch_ids() == [] and state().ActiveSketch == 0, "Deleting the last group leaves none")
    late = add_sketch(20)
    check(len(sketch_ids()) == 1 and layer_of(late).startswith("Sketch::Group "), "The next sketch starts a new group")
    call(groups_type, "Delete", doc, False, sketch_ids()[0])

    # A file from before groups were layers: sketches on the Sketch layer itself, tagged, and the old record.
    doc.Strings.SetString("Crayon.Groups", "State",
        '{"sketches":[{"id":1,"name":"Group 1","visible":true,"context":true,"active":false},'
        '{"id":2,"name":"Old two","visible":false,"context":false,"active":true}],'
        '"comments":[{"id":1,"name":"Group 1","visible":true,"context":true,"active":true}],'
        '"activeSketch":2,"activeComment":1,"activeOnly":true,"newGroup":true,"hideUsed":true}')
    a = old_sketch(1, 30)
    b = old_sketch(2, 40)
    c = old_sketch(0, 50)   # untagged: Group 1
    d = old_sketch(7, 60)   # tagged with a group the record has lost
    check(names() == ["Group 1", "Old two"] and state().ActiveSketch == 2, "An old file's groups show as they were saved")
    call(groups_type, "Tidy", doc)
    check(layer_of(a) == "Sketch::Group 1" and layer_of(b) == "Sketch::Old two", "Each old group gets its sublayer, and its sketches are moved onto it")
    check(layer_of(c) == "Sketch::Group 1", "An untagged sketch goes to the first group")
    check(layer_of(d) == "Sketch::Group 7" and sketch_ids() == [1, 2, 7], "A sketch tagged with a lost group gets that group back")
    check(not layer("Sketch::Old two").IsVisible, "A group that was hidden has its layer turned off")
    old_two = [g for g in state().Sketches if g.Id == 2][0]
    check(not old_two.Visible and not old_two.Context and state().ActiveSketch == 2, "Eye, tick and the group drawn in carry over")
    check(len(state().Comments) == 1 and layer("Comments") is None, "A group with nothing in it, of a kind never used here, gets no layer yet")
    write(str(passed[0]) + " checks passed.")
except Exception:
    write(traceback.format_exc())
finally:
    if doc is not None:
        doc.Modified = False
    log.close()
