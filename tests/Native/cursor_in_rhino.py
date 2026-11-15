# Run in a NEW Rhino with /scheme=CrayonValidation /notemplate only.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.Geometry import Point3d, Vector3d, Plane, BoundingBox

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
System.IO.Directory.CreateDirectory(output)
log = open(os.path.join(output, "cursor-rhino.log"), "w")
controller = None
doc = None
checks = 0

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
    kind = assembly.GetType("RhinoSketchingTool.SketchController", True)
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
    controller = System.Activator.CreateInstance(kind)
    def call(name, *args):
        method = kind.GetMethod(name, flags)
        return method.Invoke(None if method.IsStatic else controller, System.Array[System.Object](list(args)))
    def get(name): return kind.GetProperty(name, flags).GetValue(controller, None)
    def put(name, value): kind.GetProperty(name, flags).SetValue(controller, value, None)
    def screen(point):
        at = viewport.WorldToClient(point)
        return System.Drawing.Point(int(round(at.X)), int(round(at.Y)))
    def down(point): return call("PointerDown", view, point, False, False, System.Double(.6))
    view = doc.Views.ActiveView
    viewport = view.ActiveViewport
    put("Active", True)
    put("CursorSectionVisible", True)
    check(get("CursorVisible"), "Expanded cursor section shows cursor independently of draw method")
    call("SetCursor", Point3d(3, 4, 5))
    call("SetCursorPlaneMode", "front")
    frame = get("CursorDrawPlane")
    check(abs(frame.Normal * Vector3d.YAxis) > .999 and frame.Origin.DistanceTo(Point3d(3,4,5)) < 1e-8,
          "Front cursor plane passes through typed coordinates")
    fresh = System.Activator.CreateInstance(kind)
    check(kind.GetProperty("CursorPlaneMode").GetValue(fresh, None) == "front" and
          kind.GetProperty("Cursor").GetValue(fresh, None).DistanceTo(Point3d(3,4,5)) < 1e-8,
          "Cursor position and plane survive a new controller via document settings")
    before = viewport.ConstructionPlane()
    call("LookAtCursor")
    direction = viewport.CameraDirection
    direction.Unitize()
    check(direction * frame.Normal < -.999 and viewport.CameraTarget.DistanceTo(frame.Origin) < 1e-8,
          "Look at cursor aligns camera head-on and centres its plane")
    check(viewport.ConstructionPlane().Origin.DistanceTo(before.Origin) < 1e-8 and
          viewport.ConstructionPlane().Normal * before.Normal > .999,
          "Look at cursor leaves Rhino construction plane intact")
    call("SetView", System.Int32(3))
    call("DrawOnCursorPlane")
    check(get("CursorPlaneMode") == "top", "Top view selects a matching cursor drawing plane")
    centre = screen(get("Cursor"))
    down(System.Drawing.Point(centre.X - 60, centre.Y))
    call("PointerMove", view, System.Drawing.Point(centre.X + 60, centre.Y + 20), System.Double(.8))
    points = list(get("LivePoints"))
    check(len(points) >= 2 and all(abs(p.Z - 5) < 1e-6 for p in points), "Actual pointer stroke stays on selected cursor plane")
    call("PointerUp")
    call("BeginPlaceCursor")
    tool_before = get("Tool")
    call("BackToSelect", False)
    check(not get("PlacingCursor") and get("Tool") == tool_before, "Esc cancels cursor placement without changing tools")
    call("BeginPickPlane")
    call("BackToSelect", False)
    check(not get("PickingPlane") and get("Tool") == tool_before, "Esc cancels face picking without drawing")
    call("ResetCursor")
    check(get("Cursor").DistanceTo(Point3d.Origin) < 1e-8, "Reset returns cursor to origin")
    box = Rhino.Geometry.Brep.CreateFromBox(BoundingBox(Point3d(-5,-5,0), Point3d(5,5,10)))
    doc.Objects.AddBrep(box)
    call("SetView", System.Int32(3))
    point = screen(Point3d(0,0,10))
    call("BeginPlaceCursor")
    down(point)
    check(not get("PlacingCursor") and abs(get("Cursor").Z - 10) < 1e-5,
          "Place cursor consumes one click and lands on actual box face")
    call("SetCursorPlaneMode", "surface")
    check(get("CursorDrawPlane").Normal * Vector3d.ZAxis > .999, "Surface cursor plane follows picked face normal")
    original_plane = viewport.ConstructionPlane()
    call("BeginPickPlane")
    down(point)
    check(get("HasPickedPlane") and get("PickedPlaneLabel").startswith("On ") and not get("PickingPlane"),
          "Pick a face starts the real borrowed construction plane and layer label")
    call("ResetPickedPlane")
    check(not get("HasPickedPlane") and viewport.ConstructionPlane().Origin.DistanceTo(original_plane.Origin) < 1e-8,
          "Clearing picked face restores Rhino's previous plane")
    hook = assembly.GetType("RhinoSketchingTool.KeyHook", True)
    check(hook.GetField("onDigit", flags).GetValue(None) is None, "Crayon does not register digit shortcuts")
    log.write("PASS all " + str(checks) + " cursor checks\n")
except:
    log.write(traceback.format_exc())
finally:
    if controller is not None:
        try: kind.GetMethod("Shutdown", flags).Invoke(controller, None)
        except: pass
    if doc is not None: doc.Modified = False
    log.close()
