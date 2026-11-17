# Renders the viewport toolbar and every tool's option row, light and dark, with the
# plug-in's own WPF renderers, into rhino-plugin/bin/UiValidation/bars. Also checks that
# saved views are cameras only: restoring one keeps the drawing method, and sketches
# tied to a view by older builds still show in every view.
# Run only in a NEW Rhino process with /scheme=CrayonValidation /notemplate.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.DocObjects import ObjectAttributes

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
out = os.path.join(root, "rhino-plugin", "bin", "UiValidation", "bars")
if not os.path.isdir(out):
    os.makedirs(out)
log = open(os.path.join(out, "render.log"), "w")

def write(message):
    log.write(str(message) + "\n")
    log.flush()

try:
    assembly = Assembly.LoadFile(os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp"))
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
    controller_type = assembly.GetType("RhinoSketchingTool.SketchController", True)
    controller = System.Activator.CreateInstance(controller_type)
    theme = assembly.GetType("RhinoSketchingTool.Theme", True)
    tool_type = assembly.GetType("RhinoSketchingTool.SketchTool", True)
    method_type = assembly.GetType("RhinoSketchingTool.DrawMethod", True)
    doc = Rhino.RhinoDoc.ActiveDoc
    view = doc.Views.ActiveView
    view.ActiveViewport.Size = System.Drawing.Size(1200, 700)

    def call(name, *args):
        methods = [m for m in controller_type.GetMethods(flags) if m.Name == name and len(m.GetParameters()) == len(args)]
        method = methods[0]
        return method.Invoke(None if method.IsStatic else controller, System.Array[System.Object](list(args)))

    controller_type.GetProperty("Method").SetValue(controller, System.Enum.Parse(method_type, "Plane"), None)
    call("CaptureView", "Entrance")
    call("CaptureView", "Courtyard")
    call("RestoreView", "Entrance")
    method_now = str(controller_type.GetProperty("Method").GetValue(controller, None))
    write(("PASS" if method_now == "Plane" else "FAIL") + " restoring a saved view keeps the drawing method (" + method_now + ")")
    attributes = ObjectAttributes()
    attributes.SetUserString("Crayon.NamedView", "some-old-view")
    write(("PASS" if call("StrokeVisibleInView", doc, view.ActiveViewport, attributes) else "FAIL") + " a sketch tied to a view by an older build shows in every view")

    toolbar = controller_type.GetProperty("Toolbar", flags).GetValue(controller, None)
    dark_prop = theme.GetProperty("Dark", flags)
    for dark in [False, True]:
        dark_prop.SetValue(None, dark, None)
        name = "dark" if dark else "light"
        for tool in ["Select", "Draw", "Erase", "Comment", "Views"]:
            controller_type.GetProperty("Tool").SetValue(controller, System.Enum.Parse(tool_type, tool), None)
            folder = os.path.join(out, name + "-" + tool)
            toolbar.GetType().GetMethod("SaveDrawingPictures", flags).Invoke(toolbar, System.Array[System.Object]([folder]))
            toolbar.GetType().GetMethod("SavePicture", flags).Invoke(toolbar, System.Array[System.Object]([os.path.join(folder, "tools-bar.png"), System.Double(1)]))
            write("rendered " + folder)
    dark_prop.SetValue(None, False, None)

    # The pen row: Object | Plane | 3D cursor, with Pick a face (Plane) and Place cursor (3D cursor).
    pick_type = assembly.GetType("RhinoSketchingTool.PickMode", True)
    def setp(name, value): controller_type.GetProperty(name).SetValue(controller, value, None)
    def getp(name): return controller_type.GetProperty(name).GetValue(controller, None)
    setp("Tool", System.Enum.Parse(tool_type, "Draw"))
    for method, pick in [("Plane", "None"), ("Plane", "Face"), ("Cursor", "None"), ("Cursor", "Cursor")]:
        setp("Method", System.Enum.Parse(method_type, method))
        setp("Picking", System.Enum.Parse(pick_type, pick))
        folder = os.path.join(out, "pen-" + method + "-" + pick)
        toolbar.GetType().GetMethod("SaveDrawingPictures", flags).Invoke(toolbar, System.Array[System.Object]([folder]))
    write(("PASS" if str(getp("Picking")) == "Cursor" else "FAIL") + " Place cursor arms a one-click pick")
    setp("Method", System.Enum.Parse(method_type, "Plane"))
    write(("PASS" if str(getp("Picking")) == "None" else "FAIL") + " changing the method disarms a pick")

    # One click does the pick, then drawing carries on as normal.
    setp("Active", True)
    viewport = view.ActiveViewport
    centre = System.Drawing.Point(viewport.Size.Width // 2, viewport.Size.Height // 2)
    setp("Method", System.Enum.Parse(method_type, "Cursor"))
    setp("Picking", System.Enum.Parse(pick_type, "Cursor"))
    before = getp("Cursor")
    aside = System.Drawing.Point(viewport.Size.Width // 3, viewport.Size.Height * 2 // 3)  # not the centre: that is the origin, where the cursor starts
    call("PointerDown", view, aside, False, False, System.Double(-1)); call("PointerUp")
    moved = getp("Cursor")
    write(("PASS" if moved.DistanceTo(before) > 1e-6 and str(getp("Picking")) == "None" and len(list(call("StrokeObjects", doc))) == 0 else "FAIL") + " a click after Place cursor moves the 3D cursor, draws nothing, and disarms (moved " + str(round(moved.DistanceTo(before), 3)) + ", picking " + str(getp("Picking")) + ")")
    surface = doc.Objects.AddSurface(Rhino.Geometry.PlaneSurface(Rhino.Geometry.Plane(Rhino.Geometry.Point3d(0, 0, 5), Rhino.Geometry.Vector3d(0, 0, 1)), Rhino.Geometry.Interval(-1000, 1000), Rhino.Geometry.Interval(-1000, 1000)))
    doc.Views.Redraw()
    setp("Method", System.Enum.Parse(method_type, "Plane"))
    setp("Picking", System.Enum.Parse(pick_type, "Face"))
    call("PointerDown", view, centre, False, False, System.Double(-1)); call("PointerUp")
    write(("PASS" if getp("OnPickedFace") and str(getp("Picking")) == "None" and abs(viewport.ConstructionPlane().Origin.Z - 5) < 1e-6 else "FAIL") + " a click after Pick a face puts the drawing plane on that face, and disarms")
    folder = os.path.join(out, "pen-Plane-OnFace")
    toolbar.GetType().GetMethod("SaveDrawingPictures", flags).Invoke(toolbar, System.Array[System.Object]([folder]))
    controller_type.GetMethod("BackToGrid").Invoke(controller, None)
    write(("PASS" if not getp("OnPickedFace") and abs(viewport.ConstructionPlane().Origin.Z) < 1e-6 else "FAIL") + " the chip's x goes back to the construction plane")
    setp("Active", False)
    write("DONE")
except Exception:
    write(traceback.format_exc())
finally:
    log.close()
