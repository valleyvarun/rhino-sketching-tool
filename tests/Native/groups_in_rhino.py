# Isolated native tests: /scheme=CrayonValidation /notemplate only.
# Groups are sublayers of Rhino's "Sketch" and "Comments" layers. Checks where new sketches
# and comments go, the eyes, what the agent gets, a new group as a prompt is sent, hiding
# groups once sent, and save/reopen. Makes no model requests.
import os
import json
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.Geometry import Point3d, Vector3d, LineCurve

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
System.IO.Directory.CreateDirectory(output)
log = open(os.path.join(output, "groups-rhino.log"), "w")
checks = 0
controller = None
doc = None
def check(value, description):
    global checks
    assert value, description
    checks += 1
    log.write("PASS " + description + "\n")
    log.flush()
try:
    assert "/scheme=crayonvalidation" in System.Environment.CommandLine.lower()
    doc = Rhino.RhinoDoc.ActiveDoc
    assert doc is not None and doc.Objects.Count == 0
    assembly = Assembly.LoadFile(os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp"))
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
    def typ(name): return assembly.GetType("RhinoSketchingTool." + name, True)
    def call(name, method, *args):
        return typ(name).GetMethod(method, flags).Invoke(None, System.Array[System.Object](list(args)))
    def field(obj, name): return obj.GetType().GetField(name, flags).GetValue(obj)
    def setfield(obj, name, value): obj.GetType().GetField(name, flags).SetValue(obj, value)
    def state(target=None): return call("AnnotationGroups", "Read", target or doc)
    def actives(): return (field(state(), "ActiveSketch"), field(state(), "ActiveComment"))
    def groups(comment, target=None): return list(field(state(target), "Comments" if comment else "Sketches"))
    def name(g): return str(g.GetType().GetProperty("Name", flags).GetValue(g, None))  # the group's type is internal: by reflection
    def active(comment, group): call("AnnotationGroups", "SetActive", doc, comment, System.Int32(group))
    def visible(comment, group):
        return next(field(g, "Visible") for g in groups(comment) if field(g, "Id") == group)
    def ticked(comment, group):
        return next(field(g, "Context") for g in groups(comment) if field(g, "Id") == group)
    def layer(path, target=None):
        index = (target or doc).Layers.FindByFullPath(path, -1)
        return (target or doc).Layers[index] if index >= 0 else None
    def layer_of(id, target=None):
        obj = (target or doc).Objects.FindId(id)
        return (target or doc).Layers[obj.Attributes.LayerIndex].FullPath
    def shows(id): return call("SketchController", "OnVisibleLayer", doc, doc.Objects.FindId(id))
    def own_eye(id, on):
        attributes = doc.Objects.FindId(id).Attributes.Duplicate()
        attributes.SetUserString("Crayon.Hidden", None if on else "1")
        doc.Objects.ModifyAttributes(id, attributes, True)
    def context(target=None):
        text = str(call("DrawingContext", "Build", target or doc))
        return json.loads(text.split("<crayon_drawing_context>\n")[1].split("\n</crayon_drawing_context>")[0])
    def stroke(x):
        attributes = call("SketchController", "StrokeAttributes", doc)
        return doc.Objects.AddCurve(LineCurve(Point3d(x, 0, 0), Point3d(x, 10, 0)), attributes)
    controller = System.Activator.CreateInstance(typ("SketchController"))
    typ("SketchController").GetMethod("StartPainting", flags).Invoke(controller, None)
    def comment(number, text):
        mark = System.Activator.CreateInstance(typ("Comment"))
        setfield(mark, "Number", System.Int32(number))
        setfield(mark, "Point", Point3d(number, 0, 0))
        setfield(mark, "Normal", Vector3d.ZAxis)
        typ("SketchController").GetMethod("AddComment", flags).Invoke(controller, System.Array[System.Object]([doc, doc.Views.ActiveView, mark, text]))

    check(field(state(), "NewGroup"), "New group after each prompt defaults on")
    check(field(state(), "HideUsed"), "Hide groups once sent defaults on")
    check(actives() == (0, 0) and len(groups(False)) == 0 and len(groups(True)) == 0, "A new document has no groups")
    check(layer("Sketch") is None and layer("Comments") is None, "... and no Crayon layers until something is drawn")
    call("AnnotationGroups", "Advance", doc)
    check(actives() == (0, 0), "Nothing to move on from while there are no groups")

    # The first sketch and comment make their group, and its layer.
    old = stroke(0)
    comment(1, "previous comment")
    check(actives() == (1, 1), "The first sketch and the first comment each start Group 1")
    check(layer_of(old) == "Sketch::Group 1", "A sketch is on its group's sublayer of Sketch")
    check(layer("Comments::Group 1") is not None, "A comment's group is a sublayer of Comments")
    check(doc.Objects.FindId(old).Attributes.GetUserString("Crayon.Group") == "1", "New sketches are also tagged with their group")
    call("AnnotationGroups", "Advance", doc)
    new = stroke(20)
    comment(2, "current comment")
    check(actives() == (2, 2), "Moving on starts a new group in both collections")
    check(layer_of(new) == "Sketch::Group 2" and layer("Comments::Group 2") is not None, "The new groups are new sublayers")

    # What the agent gets: every ticked group; the group being drawn in and the eyes do not decide it.
    snapshot = context()
    check(len(snapshot["sketches"]) == 2 and len(snapshot["comments"]) == 2, "Every ticked group is the agent's context")
    active(True, 1)
    check(actives() == (2, 1), "The sketch group and the comment group being drawn in are chosen independently")
    check(len(context()["comments"]) == 2, "The group being drawn in is where new items go, not what the agent gets")
    active(True, 2)
    call("AnnotationGroups", "ToggleContext", doc, False, System.Int32(1))
    unticked = context()
    check(len(unticked["sketches"]) == 1 and unticked["sketches"][0]["id"] == str(new), "Unticking a group takes it out of the agent's context")
    call("AnnotationGroups", "ToggleContext", doc, False, System.Int32(1))

    # A group's eye is its layer's light bulb, both ways.
    call("AnnotationGroups", "ToggleGroup", doc, False, System.Int32(1))
    check(not visible(False, 1) and not layer("Sketch::Group 1").IsVisible, "A group's eye turns its Rhino layer off")
    check(not shows(old), "... which hides its sketches")
    check(len(context()["sketches"]) == 2, "The eye only hides: the tick decides what the agent gets")
    check(actives()[0] == 2, "The eye never changes the group being drawn in")
    call("AnnotationGroups", "ToggleGroup", doc, False, System.Int32(1))
    check(visible(False, 1) and layer("Sketch::Group 1").IsVisible and shows(old), "The eye turns the layer back on")
    layer("Sketch::Group 1").IsVisible = False
    check(not visible(False, 1) and not shows(old), "Turning the layer off in Rhino turns the group's eye off")
    layer("Sketch::Group 1").IsVisible = True
    check(visible(False, 1), "... and on again")
    own_eye(new, False)
    check(not shows(new), "A sketch's own eye hides it")
    call("AnnotationGroups", "ToggleGroup", doc, False, System.Int32(2))
    call("AnnotationGroups", "ToggleGroup", doc, False, System.Int32(2))
    check(not shows(new), "Hiding and showing its group leaves a sketch's own eye as it was")
    own_eye(new, True)

    # A prompt, through the production handlers. The new group starts as the prompt is sent.
    agent = typ("Agent")
    prompts = agent.GetField("prompts", flags).GetValue(None)
    def pending(failed=False):
        prompt = System.Activator.CreateInstance(agent.GetNestedType("PromptGroup", flags), True)
        transcript = System.Activator.CreateInstance(typ("Transcript"), True)
        if failed:
            notice = System.Activator.CreateInstance(typ("Notice"), True)
            setfield(notice, "Error", True)
            setfield(notice, "Text", "Tool failed")
            entries = field(transcript, "Entries")
            entries.GetType().GetMethod("Add").Invoke(entries, System.Array[System.Object]([notice]))
        setfield(prompt, "Document", System.UInt32(doc.RuntimeSerialNumber))
        setfield(prompt, "Transcript", transcript)
        setfield(prompt, "Entry", System.Int32(0))
        setfield(prompt, "HideUsed", field(state(), "HideUsed"))
        setfield(prompt, "Context", call("DrawingContext", "Capture", doc))
        prompts.GetType().GetProperty("Item").SetValue(prompts, prompt, System.Array[System.Object](["codex"]))
    def sent(): call("Agent", "Sent", doc)
    def completed(success): call("Agent", "Completed", "codex", success)

    pending()
    sent()
    check(actives() == (3, 3), "Sending a prompt starts a new group in both collections at once")
    completed(False)
    check(visible(False, 2) and visible(True, 2), "A failed or stopped prompt leaves the groups it took along showing")
    check(actives() == (3, 3), "... and the new groups stay: nothing drawn since is lost")
    pending(True)
    completed(True)
    check(visible(False, 2) and visible(True, 2), "A turn in which a tool went wrong leaves them showing too")
    pending()
    completed(True)
    completed(True)
    check(not visible(False, 1) and not visible(False, 2) and not visible(True, 1) and not visible(True, 2), "A prompt that went through hides the groups it took along")
    check(not layer("Sketch::Group 2").IsVisible and not layer("Comments::Group 2").IsVisible, "... by turning their Rhino layers off")
    check(not ticked(False, 2) and not ticked(True, 2), "... and they are no longer ticked for the agent")
    check(visible(False, 3) and visible(True, 3), "The new, empty groups stay visible")

    sent()
    check(actives() == (3, 3), "An empty group is the new group already: sending again adds none")
    stroke(40)
    sent()
    check(actives() == (4, 3), "Only the collection with something new in it gets a new group")
    comment(3, "third comment")
    sent()
    check(actives() == (4, 4), "... and so for comments")
    deleted = stroke(60)
    check(doc.Objects.Delete(doc.Objects.FindId(deleted), True, True), "Delete a sketch the way Crayon does")
    sent()
    check(actives() == (4, 4), "A deleted sketch does not make its group count as used")
    call("AnnotationGroups", "ToggleOption", doc, False)
    stroke(70)
    sent()
    check(actives() == (4, 4), "With New group after each prompt off, sending starts no new group")

    # With that option off, the group being drawn in is itself hidden once sent.
    pending()
    completed(True)
    check(not visible(False, 3) and not visible(False, 4) and not visible(True, 3), "Hide groups once sent works with new groups off")
    late = stroke(80)
    check(actives()[0] == 5 and layer_of(late) == "Sketch::Group 5", "A group that is turned off is not drawn into: the next sketch starts a new one")
    check(shows(late), "... so what is drawn always shows")
    call("AnnotationGroups", "ToggleHideUsed", doc)
    pending()
    completed(True)
    check(visible(False, 5), "With Hide groups once sent off, a used group keeps showing")

    # Save and reopen: the groups are the file's layers.
    active(False, 2)
    active(True, 2)
    own_eye(new, False)  # an item's own eye, off, to check it survives reopen
    names = [field(g, "Id") for g in groups(False)], [name(g) for g in groups(False)], [field(g, "Visible") for g in groups(False)]
    before = context()
    path = os.path.join(output, "groups.3dm")
    options = Rhino.FileIO.FileWriteOptions()
    options.SuppressDialogBoxes = True
    check(doc.WriteFile(path, options), "Grouped model saves")
    reopened = Rhino.RhinoDoc.OpenHeadless(path)
    try:
        saved = state(reopened)
        check(len(groups(False, reopened)) == 5 and len(groups(True, reopened)) == 4, "Every group is there when the file is opened")
        check(([field(g, "Id") for g in groups(False, reopened)], [name(g) for g in groups(False, reopened)], [field(g, "Visible") for g in groups(False, reopened)]) == names, "... with its number, name and eye")
        check(field(saved, "ActiveSketch") == 2 and field(saved, "ActiveComment") == 2, "The groups being drawn in survive reopen")
        check(not field(saved, "NewGroup") and not field(saved, "HideUsed"), "The two Agent options survive reopen")
        after = context(reopened)
        check(len(after["sketches"]) == len(before["sketches"]) and len(after["comments"]) == len(before["comments"]), "What the agent gets is the same after save and reopen")
        check(layer_of(new, reopened) == "Sketch::Group 2", "A sketch is still on its group's layer")
        check(reopened.Objects.FindId(new).Attributes.GetUserString("Crayon.Hidden") == "1", "Item eye state survives reopen")
        reopened.Strings.Delete("Crayon.Groups", "State")
        check([name(g) for g in groups(False, reopened)] == names[1] and len(groups(True, reopened)) == 4, "Even without Crayon's own record, the layers alone give the groups back")
    finally: reopened.Dispose()
    log.write(str(checks) + " group checks passed.\n")
except:
    log.write(traceback.format_exc())
finally:
    if controller is not None:
        typ("SketchController").GetMethod("Shutdown", flags).Invoke(controller, None)
    if doc is not None: doc.Modified = False
    log.close()
