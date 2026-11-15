# Checks adding, renaming and deleting annotation groups, and the agent's context
# groups, in a NEW Rhino process with /scheme=CrayonValidation /notemplate.
# Results go to tests/Native/bin/groups-delete.log. Makes no model requests.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.Geometry import Point3d, PolylineCurve
from Rhino.DocObjects import ObjectAttributes

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

    def add_sketch(group, x):
        attributes = ObjectAttributes()
        attributes.SetUserString("SketchingTool.Stroke", "1")
        attributes.SetUserString("Crayon.Group", str(group))
        curve = PolylineCurve([Point3d(x, 0, 0), Point3d(x + 1, 1, 0), Point3d(x + 2, 0, 0)])
        return doc.Objects.AddCurve(curve, attributes)

    def get(obj, name):
        t = obj.GetType()
        f = t.GetField(name, flags)
        return f.GetValue(obj) if f is not None else t.GetProperty(name, flags).GetValue(obj, None)

    class State(object):
        def __init__(self, raw):
            self.Sketches = [Group(g) for g in get(raw, "Sketches")]
            self.ActiveSketch = get(raw, "ActiveSketch")

    class Group(object):
        def __init__(self, raw):
            self.Id = get(raw, "Id")
            self.Name = get(raw, "Name")

    def state():
        return State(call(groups_type, "Read", doc))

    def sketch_ids():
        return sorted([g.Id for g in state().Sketches])

    def strokes():
        return len(call(controller_type, "StrokeObjects", doc))

    add_sketch(1, 0)
    call(groups_type, "Create", doc, False)          # Group 2, now active
    check(sketch_ids() == [1, 2] and state().ActiveSketch == 2, "New group is added and made active")
    add_sketch(2, 5)
    add_sketch(2, 9)
    call(groups_type, "Create", doc, False)          # Group 3
    call(groups_type, "Rename", doc, False, 3, "Roof ideas")
    check([g.Name for g in state().Sketches if g.Id == 3] == ["Roof ideas"], "Group renames")

    snapshot = call(context_type, "Capture", doc)
    check(sorted(get(snapshot, "SketchGroups")) == [1, 2], "Agent context = visible groups that have sketches")
    call(groups_type, "ToggleContext", doc, False, 1)
    snapshot = call(context_type, "Capture", doc)
    check(sorted(get(snapshot, "SketchGroups")) == [2], "Unticking a group takes it out of the agent's context")
    call(groups_type, "ToggleContext", doc, False, 1)
    call(groups_type, "ToggleGroup", doc, False, 1)
    snapshot = call(context_type, "Capture", doc)
    check(sorted(get(snapshot, "SketchGroups")) == [1, 2], "Hiding a group with its eye keeps it in the agent's context while ticked")
    call(groups_type, "ToggleGroup", doc, False, 1)

    call(groups_type, "SetActive", doc, False, 2)
    call(groups_type, "Delete", doc, False, 2)
    check(sketch_ids() == [1, 3] and strokes() == 1, "Deleting a group removes it and its two sketches")
    check(state().ActiveSketch == 3, "The last remaining group becomes active when the active one goes")
    call(groups_type, "Delete", doc, False, 1)
    check(sketch_ids() == [3] and strokes() == 0, "Group 1 can be deleted too")
    call(groups_type, "Delete", doc, False, 3)
    ids = sketch_ids()
    check(len(ids) == 1 and ids[0] == 4 and state().ActiveSketch == 4, "Deleting the last group leaves a new empty one")
    # Rhino's Undo of a delete brings sketches back without their group: simulate it.
    add_sketch(2, 20)
    call(groups_type, "Heal", doc)
    check(2 in sketch_ids(), "A group that sketches still name comes back in Layers")
    write(str(passed[0]) + " checks passed.")
except Exception:
    write(traceback.format_exc())
finally:
    log.close()
