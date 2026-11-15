# Isolated native test: /scheme=CrayonValidation /notemplate only.
import os
import math
import time
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
System.IO.Directory.CreateDirectory(output)
log = open(os.path.join(output, "shapes-rhino.log"), "w")
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
    def field(obj, name): return obj.GetType().GetField(name, flags).GetValue(obj)
    def drag(): return kind.GetField("drawing", flags).GetValue(controller)
    def point(x,y): return System.Drawing.Point(int(round(x)), int(round(y)))
    def down(x,y): call("PointerDown", view, point(x,y), False, False, System.Double(.15))
    def move(x,y,p=.8): call("PointerMove", view, point(x,y), System.Double(p))
    def hold(): time.sleep(.7); call("TryHoldShape")
    def screen_points(): return [viewport.WorldToClient(p) for p in get("LivePoints")]
    view = doc.Views.ActiveView
    viewport = view.ActiveViewport
    put("Active", True)
    put("SnapShapes", True)
    call("SetView", System.Int32(3))
    call("DrawOnCursorPlane")
    down(100,100)
    for i in range(1,16): move(100+i*8, 100+(i%3-1)*2, .15+i*.05)
    call("TryHoldShape")
    check(field(drag(),"SnapKind") is None, "A moving stroke does not snap before hold delay")
    hold()
    check(field(drag(),"SnapKind") == "line", "Stationary pressed pen previews a straight line after 650ms")
    source_count = field(drag(),"Points").Count
    move(270,160)
    points = screen_points()
    check(points[-1].DistanceTo(Rhino.Geometry.Point2d(270,160)) < 1.5 and field(drag(),"Points").Count == source_count,
          "Held line endpoint follows drag without adding freehand points")
    original = field(drag(),"Points")
    paint = get("LivePaint")
    widths = list(field(paint,"Widths"))
    check(len(widths) == len(points) and max(widths) > min(widths)*1.1,
          "Held shape preview preserves varying pen-pressure widths")
    call("PointerUp")
    objects = list(call("StrokeObjects",doc))
    check(len(objects)==1, "Releasing held shape commits exactly one sketch")
    pressure_type = assembly.GetType("RhinoSketchingTool.StrokePaint", True)
    curve = objects[0].Geometry
    points_saved = call("StrokePoints",curve,System.Double(doc.ModelAbsoluteTolerance))
    saved = pressure_type.GetMethod("Read",flags).Invoke(None,System.Array[System.Object]([objects[0].Attributes,System.Int32(len(points_saved))]))
    check(saved is not None and len(field(saved,"Widths"))==len(points_saved), "Committed held shape retains aligned pressure metadata")
    down(300,200)
    for i in range(1,49):
        angle=2*math.pi*i/48
        move(250+50*math.cos(angle),200+50*math.sin(angle),.5)
    hold()
    check(field(drag(),"SnapKind")=="circle", "Holding a closed round gesture previews a circle")
    centre=field(drag(),"SnapCentre")
    move(centre.X+80,centre.Y)
    points=screen_points()
    radii=[p.DistanceTo(centre) for p in points]
    check(max(radii)-min(radii)<2 and abs(sum(radii)/len(radii)-80)<1,
          "Dragging a held circle adjusts radius around its fixed centre")
    check(points[0].DistanceTo(points[-1])<1e-5, "Adjusted held circle remains closed")
    call("PointerUp")
    put("SnapShapes",False)
    down(100,280)
    for i in range(1,9): move(100+i*12,280)
    hold()
    check(field(drag(),"SnapKind") is None, "Disabled Shapes never activates hold recognition")
    call("PointerUp")
    log.write("PASS all " + str(checks) + " shape hold checks\n")
except:
    log.write(traceback.format_exc())
finally:
    if controller is not None:
        try: kind.GetMethod("Shutdown",flags).Invoke(controller,None)
        except: pass
    if doc is not None: doc.Modified=False
    log.close()
