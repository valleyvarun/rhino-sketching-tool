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
    write("DONE")
except Exception:
    write(traceback.format_exc())
finally:
    log.close()
