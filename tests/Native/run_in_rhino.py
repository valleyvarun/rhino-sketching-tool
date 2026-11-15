# Run only in a NEW Rhino process with /scheme=CrayonValidation /notemplate.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.Geometry import Point3d, Vector3d, PolylineCurve
from Rhino.DocObjects import ObjectAttributes, ObjectColorSource, ObjectMode, ViewportInfo

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
log = open(os.path.join(output, "rhino-script.log"), "w")
checks = 0

def write(message):
    log.write(str(message) + "\n")
    log.flush()

def check(passed, description):
    global checks
    if not passed:
        raise Exception(description)
    checks += 1
    write("PASS " + description)

doc = None
try:
    write("Rhino process " + str(System.Diagnostics.Process.GetCurrentProcess().Id))
    assembly = Assembly.LoadFile(os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp"))
    controller_type = assembly.GetType("RhinoSketchingTool.SketchController", True)
    controller = System.Activator.CreateInstance(controller_type)
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
    def call(name, *args):
        methods = [m for m in controller_type.GetMethods(flags) if m.Name == name and len(m.GetParameters()) == len(args)]
        if len(methods) != 1:
            raise Exception("Ambiguous or missing method " + name)
        method = methods[0]
        return method.Invoke(None if method.IsStatic else controller, System.Array[System.Object](list(args)))

    candidate = Rhino.RhinoDoc.ActiveDoc
    check("/scheme=crayonvalidation" in System.Environment.CommandLine.lower(), "Dedicated validation process scheme")
    check(candidate is not None and candidate.Objects.Count == 0, "New isolated blank document")
    doc = candidate
    view = doc.Views.ActiveView
    check(view is not None, "Isolated viewport available")
    viewport = view.ActiveViewport
    def key():
        return call("ViewKeyFor", doc, viewport)
    def shown(attributes):
        return call("StrokeVisibleInView", doc, viewport, attributes)

    original_count = doc.NamedViews.Count
    first = call("CaptureView", "QA view")
    check(first == "QA view" and doc.NamedViews.Count == original_count + 1, "Capture creates native named view")
    first_key = key()
    check(bool(first_key), "Captured camera resolves to named view GUID")
    attributes = ObjectAttributes()
    attributes.SetUserString("Crayon.NamedView", first_key)
    check(shown(attributes), "Own view overlay is visible")

    duplicate = call("CaptureView", "QA view")
    check(duplicate != first and doc.NamedViews.Count == original_count + 2, "Duplicate name does not overwrite")
    duplicate_key = key()
    check(duplicate_key != first_key and not shown(attributes), "Identical-camera named views isolate overlays")
    call("ForgetViewBindings", doc)
    check(key() == duplicate_key and not shown(attributes), "Persisted binding survives cleared runtime cache")
    check(call("RestoreView", first), "Named view restores")
    check(key() == first_key and shown(attributes), "Restoring first view restores its own overlay")
    moved = ViewportInfo(viewport)
    moved.SetCameraLocation(moved.CameraLocation + Vector3d(1, 0, 0))
    viewport.SetViewProjection(moved, True)
    moved.Dispose()
    check(not shown(attributes), "Camera navigation hides overlay")
    call("RestoreView", first)
    resized = ViewportInfo(viewport)
    resized.FrustumAspect = .6
    viewport.SetViewProjection(resized, True)
    resized.Dispose()
    check(shown(attributes), "Aspect resize retains overlay")
    call("RestoreView", first)
    check(doc.NamedViews.Rename(doc.NamedViews.FindByName(first), "Renamed QA view"), "Native view renames")
    check(shown(attributes), "Overlay follows renamed view GUID")
    toolbar = controller_type.GetProperty("Toolbar", flags).GetValue(controller, None)
    render_folder = os.path.join(root, "rhino-plugin", "bin", "UiValidation")
    original_size = viewport.Size
    viewport.Size = System.Drawing.Size(1200, 700)
    toolbar.GetType().GetMethod("SaveDrawingPictures", flags).Invoke(toolbar, System.Array[System.Object]([render_folder]))
    toolbar.GetType().GetMethod("SavePicture", flags).Invoke(toolbar, System.Array[System.Object]([os.path.join(render_folder, "tools-bar.png"), System.Double(1)]))
    check(os.path.exists(os.path.join(render_folder, "drawing-bar.png")) and os.path.exists(os.path.join(render_folder, "color-picker.png")), "Exact runtime toolbar and picker rendered for QA")
    dock_field = toolbar.GetType().GetField("dock", flags)
    popup_field = toolbar.GetType().GetField("optionsPopup", flags)
    frame_method = toolbar.GetType().GetMethod("OptionsFrame", flags)
    original_dock = dock_field.GetValue(toolbar)
    original_popup = popup_field.GetValue(toolbar)
    popup_field.SetValue(toolbar, System.Enum.Parse(popup_field.FieldType, "Colors"))
    def frame_value(frame, name):
        return frame.GetType().GetField(name, flags).GetValue(frame)
    for width, height in [(1200, 700), (280, 180), (200, 140)]:
        viewport.Size = System.Drawing.Size(width, height)
        for docking in ["Top", "Left", "Right"]:
            dock_field.SetValue(toolbar, System.Enum.Parse(dock_field.FieldType, docking))
            frame = frame_method.Invoke(toolbar, System.Array[System.Object]([view, System.Double(0)]))
            scale = frame_value(frame, "Scale")
            for prefix in ["Option", "Popup"]:
                origin = frame_value(frame, prefix + "Origin")
                size = frame_value(frame, "OptionsSize" if prefix == "Option" else "PopupSize")
                check(origin.X >= 0 and origin.Y >= 0 and origin.X + size.Width * scale <= width + 1 and origin.Y + size.Height * scale <= height + 1, prefix + " bounds fit " + docking + " " + str(width) + "x" + str(height))
    dock_field.SetValue(toolbar, original_dock)
    popup_field.SetValue(toolbar, original_popup)
    viewport.Size = original_size
    call("RestoreView", "Renamed QA view")

    def set_property(name, value):
        controller_type.GetProperty(name).SetValue(controller, value, None)
    def enum_value(name, value):
        return System.Enum.Parse(assembly.GetType("RhinoSketchingTool." + name), value)
    # The options belong to the selected tool, rather than always showing drawing modes.
    def option_parts():
        return list(toolbar.GetType().GetMethod("OptionParts", flags).Invoke(toolbar, System.Array[System.Object]([view])))
    set_property("Tool", enum_value("SketchTool", "Select"))
    doc.Objects.UnselectAll()
    check(len(option_parts()) == 0, "Select has no option row when nothing is selected")
    set_property("Tool", enum_value("SketchTool", "Erase"))
    check("ClearSketches" in option_parts() and "Object" not in option_parts(), "Eraser shows its own actions")
    set_property("Tool", enum_value("SketchTool", "Comment"))
    check(option_parts() == ["Help"], "Comments show the anchored-comment hint")
    toolbar_type = toolbar.GetType()
    part_type = toolbar_type.GetNestedType("Part", BindingFlags.NonPublic)
    toolbar_type.GetMethod("Click", flags).Invoke(toolbar, System.Array[System.Object]([System.Enum.Parse(part_type,"Views")]))
    check("SaveView" in option_parts() and "SyncViews" in option_parts(), "Camera opens native named-view actions")
    set_property("Tool", enum_value("SketchTool", "Draw"))
    check(all(p in option_parts() for p in ["Object","Plane","Cursor","Color","Snap"]) and "View" not in option_parts(), "Pen shows compact drawing controls without the View selector")
    set_property("Active", True)
    set_property("Method", enum_value("DrawMethod", "View"))
    set_property("Tool", enum_value("SketchTool", "Draw"))
    blue = System.Drawing.Color.FromArgb(12, 90, 180)
    set_property("StrokeColor", blue)
    brush = controller_type.GetProperty("Brush").GetValue(controller, None)
    brush.GetType().GetProperty("HardnessByPressure").SetValue(brush, True, None)
    start = System.Drawing.Point(80, 100)
    check(call("PointerDown", view, start, False, False, System.Double(.6)), "View drawing accepts pointer press")
    for x in range(86, 207, 6):
        call("PointerMove", view, System.Drawing.Point(x, 100), System.Double((x - 80) / 130.0))
    call("PointerUp")
    drawn = list(call("StrokeObjects", doc))
    check(len(drawn) == 1 and drawn[0].Attributes.GetUserString("Crayon.NamedView") == first_key, "Pointer gesture creates view-scoped sketch")
    check(drawn[0].IsHidden and drawn[0].Attributes.ObjectColor.ToArgb() == blue.ToArgb(), "Drawn overlay stays hidden with selected color")
    drawn_id = drawn[0].Id
    hardness_data = drawn[0].Attributes.GetUserString("Crayon.Hardness")
    check(bool(hardness_data) and len(set(hardness_data[i:i+2] for i in range(0, len(hardness_data), 2))) > 1, "Pressure varies saved hardness along sketch")
    paint_type = assembly.GetType("RhinoSketchingTool.StrokePaint")
    paint = paint_type.GetMethod("Read", flags).Invoke(None, System.Array[System.Object]([drawn[0].Attributes, System.Int32(len(hardness_data) // 2)]))
    check(paint_type.GetField("Hardnesses", flags).GetValue(paint) is not None, "Saved pressure hardness decodes")
    set_property("Tool", enum_value("SketchTool", "Erase"))
    set_property("Erase", enum_value("EraseMode", "Part"))
    call("RestoreView", duplicate)
    call("PointerDown", view, System.Drawing.Point(140, 100), False, False, System.Double(-1))
    call("PointerUp")
    check(doc.Objects.FindId(drawn_id) is not None, "Eraser in identical-camera other view leaves hidden overlay intact")
    call("RestoreView", "Renamed QA view")
    call("PointerDown", view, System.Drawing.Point(140, 100), False, False, System.Double(-1))
    call("PointerUp")
    pieces = list(call("StrokeObjects", doc))
    check(len(pieces) == 2 and doc.Objects.FindId(drawn_id) is None, "Part eraser splits visible overlay")
    check(all(p.Attributes.GetUserString("Crayon.NamedView") == first_key and p.Attributes.ObjectColor.ToArgb() == blue.ToArgb() and p.Attributes.GetUserString("SketchingTool.Brush") for p in pieces), "Erased pieces preserve view, color and brush metadata")
    check(all(p.Attributes.GetUserString("Crayon.Hardness") for p in pieces), "Partial erase preserves hardness variation")
    set_property("Active", False)

    attributes.SetUserString("SketchingTool.Stroke", "1")
    attributes.ColorSource = ObjectColorSource.ColorFromObject
    attributes.ObjectColor = System.Drawing.Color.FromArgb(12, 90, 180)
    attributes.Mode = ObjectMode.Hidden
    stroke = doc.Objects.AddCurve(PolylineCurve(System.Array[Point3d]([Point3d(0, 0, 0), Point3d(1, 0, 0)])), attributes)
    check(stroke != System.Guid.Empty, "Tagged colored overlay geometry added")
    path = os.path.join(output, "native-validation.3dm")
    options = Rhino.FileIO.FileWriteOptions()
    options.SuppressAllInput = True
    options.SuppressDialogBoxes = True
    check(doc.WriteFile(path, options), "Validation 3dm saves")
    reopened = Rhino.RhinoDoc.OpenHeadless(path)
    try:
        check(reopened is not None and reopened.NamedViews.Count == original_count + 2, "Named views survive reopen")
        saved = reopened.Objects.FindId(stroke)
        check(saved is not None and saved.Attributes.GetUserString("Crayon.NamedView") == first_key, "Overlay association survives reopen")
        check(saved.Attributes.ObjectColor.ToArgb() == attributes.ObjectColor.ToArgb(), "Overlay color survives reopen")
        check(all(reopened.Objects.FindId(p.Id).Attributes.GetUserString("Crayon.Hardness") == p.Attributes.GetUserString("Crayon.Hardness") for p in pieces), "Pressure hardness survives save/reopen")
        check(reopened.Strings.GetValue("Crayon.ViewBindings", viewport.Id.ToString("D")) == first_key, "Viewport binding survives reopen")
    finally:
        reopened.Dispose()
    check(doc.NamedViews.Delete("Renamed QA view") and not shown(attributes), "Deleted view hides orphaned overlay")
    # Destructive toolbar action is deliberate and never touches ordinary model curves.
    ordinary = doc.Objects.AddCurve(PolylineCurve(System.Array[Point3d]([Point3d(0, 2, 0), Point3d(1, 2, 0)])))
    set_property("Tool", enum_value("SketchTool", "Erase"))
    erase_action = toolbar_type.GetMethod("ClickOptions", flags)
    clear_args = System.Array[System.Object]([System.Enum.Parse(part_type,"ClearSketches"), view, System.Drawing.Point(0,0)])
    count_before = len(list(call("StrokeObjects",doc)))
    erase_action.Invoke(toolbar, clear_args)
    check(len(list(call("StrokeObjects",doc))) == count_before, "Clear sketches first click requests confirmation")
    erase_action.Invoke(toolbar, clear_args)
    check(len(list(call("StrokeObjects",doc))) == 0 and doc.Objects.FindId(ordinary) is not None, "Clear sketches confirmation preserves ordinary model geometry")
    write(str(checks) + " native Rhino checks passed.")
except:
    write(traceback.format_exc())
finally:
    try:
        set_property("Active", False)
    except:
        pass
    if doc is not None:
        doc.Modified = False
    log.close()
