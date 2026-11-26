# Run only in a NEW Rhino process with /scheme=CrayonValidation /notemplate.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags
from Rhino.Geometry import Box, Interval, Plane, Point3d, Vector3d, PolylineCurve
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
    staged = os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp")
    assembly = Assembly.LoadFile(staged)
    check(os.path.normcase(assembly.Location) == os.path.normcase(staged), "Testing the staged Crayon assembly")
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
    if not viewport.IsPerspectiveProjection:
        viewport.SetProjection(Rhino.Display.DefinedViewportProjection.Perspective, "Perspective", False)
    def key():
        return call("ViewKeyFor", doc, viewport)
    def points(obj):
        ok, polyline = obj.Geometry.TryGetPolyline()
        if not ok:
            raise Exception("Expected a polyline sketch")
        return list(polyline)

    def same_points(obj, expected):
        if obj is None:
            return False
        actual = points(obj)
        return len(actual) == len(expected) and all(a.DistanceTo(b) < 1e-8 for a, b in zip(actual, expected))

    original_count = doc.NamedViews.Count
    first = call("CaptureView", "QA view")
    check(first == "QA view" and doc.NamedViews.Count == original_count + 1, "Capture creates native named view")
    first_key = key()
    check(bool(first_key), "Captured camera resolves to named view GUID")
    duplicate = call("CaptureView", "QA view")
    check(duplicate != first and doc.NamedViews.Count == original_count + 2, "Duplicate name does not overwrite")
    duplicate_key = key()
    check(duplicate_key != first_key, "Identical-camera named views have distinct drawing references")
    call("ForgetViewBindings", doc)
    check(key() == duplicate_key, "Persisted binding survives cleared runtime cache")
    check(call("RestoreView", first), "Named view restores")
    check(key() == first_key, "Restoring the first camera restores its drawing reference")
    check(doc.NamedViews.Rename(doc.NamedViews.FindByName(first), "Renamed QA view"), "Native view renames")
    check(key() == first_key, "A renamed view retains its GUID")
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
        for docking in ["Top", "Left", "Bottom", "Right", "Floating"]:
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
    set_property("Active", True)
    # The pen's "Over view": nothing is drawn until a saved view is picked; picking one goes to it.
    set_property("Tool", enum_value("SketchTool", "Draw"))
    set_property("Method", enum_value("DrawMethod", "View"))
    check(controller_type.GetProperty("OverViewName").GetValue(controller, None) is None, "Over view starts with no view picked")
    call("PointerDown", view, System.Drawing.Point(80, 60), False, False, System.Double(-1))
    call("PointerMove", view, System.Drawing.Point(120, 60), System.Double(-1))
    call("PointerUp")
    check(len(list(call("StrokeObjects", doc))) == 0, "Over view draws nothing until a view is picked")
    popup_field.SetValue(toolbar, original_popup)  # that press opened the list of views: close it again
    call("RestoreView", duplicate)
    check(call("DrawOverView", "Renamed QA view"), "Picking a view to draw over")
    check(key() == first_key and controller_type.GetProperty("OverViewName").GetValue(controller, None) == "Renamed QA view", "... goes to that view")
    check(str(controller_type.GetProperty("Method").GetValue(controller, None)) == "View", "... with the pen on Over view")
    moved = ViewportInfo(viewport)
    moved.SetCameraLocation(moved.CameraLocation + Vector3d(1, 0, 0))
    viewport.SetViewProjection(moved, True)
    moved.Dispose()
    call("PointerDown", view, System.Drawing.Point(80, 60), False, False, System.Double(-1))
    call("PointerMove", view, System.Drawing.Point(120, 60), System.Double(-1))
    call("PointerUp")
    check(key() == first_key and len(list(call("StrokeObjects", doc))) == 0, "With the camera moved away, the first press only goes back to the view")
    blue = System.Drawing.Color.FromArgb(12, 90, 180)
    set_property("StrokeColor", blue)
    brush = controller_type.GetProperty("Brush").GetValue(controller, None)
    brush.GetType().GetProperty("HardnessByPressure").SetValue(brush, True, None)
    start = System.Drawing.Point(80, 100)
    camera = viewport.CameraLocation
    direction = viewport.CameraDirection
    direction.Unitize()
    saved_camera = doc.NamedViews[doc.NamedViews.FindByName("Renamed QA view")].Viewport
    near = saved_camera.FrustumNear
    check(call("PointerDown", view, start, False, False, System.Double(.6)), "View drawing accepts pointer press")
    for x in range(86, 207, 6):
        call("PointerMove", view, System.Drawing.Point(x, 100), System.Double((x - 80) / 130.0))
    call("PointerUp")
    drawn = list(call("StrokeObjects", doc))
    check(len(drawn) == 1 and drawn[0].Attributes.GetUserString("Crayon.NamedView") == first_key, "Over view creates a model curve and records its source camera")
    check(drawn[0].IsHidden and drawn[0].Attributes.ObjectColor.ToArgb() == blue.ToArgb(), "Drawn sketch is painted by Crayon (hidden curve) with the selected color")
    drawn_id = drawn[0].Id
    original = points(drawn[0])
    depths = [(p - camera) * direction for p in original]
    check(min(depths) > 0 and max(depths) < near / 2, "The new sketch lies much closer to the camera than its saved near clipping plane")
    check(max(depths) - min(depths) < 1e-8, "All samples lie on one camera-facing plane")
    screen_start = viewport.WorldToClient(original[0])
    check(abs(screen_start.X - start.X) < 1 and abs(screen_start.Y - start.Y) < 1, "World coordinates project back to the original pen position")
    hardness_data = drawn[0].Attributes.GetUserString("Crayon.Hardness")
    check(bool(hardness_data) and len(set(hardness_data[i:i+2] for i in range(0, len(hardness_data), 2))) > 1, "Pressure varies saved hardness along sketch")
    paint_type = assembly.GetType("RhinoSketchingTool.StrokePaint")
    paint = paint_type.GetMethod("Read", flags).Invoke(None, System.Array[System.Object]([drawn[0].Attributes, System.Int32(len(hardness_data) // 2)]))
    check(paint_type.GetField("Hardnesses", flags).GetValue(paint) is not None, "Saved pressure hardness decodes")

    # A shaded box covers two sample pixels. The new stroke crosses its front;
    # a second curve stands in for older Over view ink stored behind the box.
    def point_at_depth(x, y, depth):
        ray = viewport.ClientToWorld(System.Drawing.Point(x, y))
        t = (depth - (ray.From - camera) * direction) / (ray.Direction * direction)
        return ray.PointAt(t)

    box_depth = near * .75
    front = point_at_depth(165, 115, box_depth)
    ok, scale = viewport.GetWorldToScreenScale(front)
    box = Box(Plane(front, viewport.CameraX, viewport.CameraY),
              Interval(-40 / scale, 40 / scale), Interval(-35 / scale, 35 / scale), Interval(-box_depth * .05, 0))
    solid = doc.Objects.AddBox(box)
    check(solid != System.Guid.Empty, "Added a shaded solid beneath the new Over view sketch")
    legacy = [point_at_depth(x, 132, near * 2) for x in (140, 190)]
    ok, scale = viewport.GetWorldToScreenScale(legacy[0])
    attrs = call("StrokeAttributes", doc)
    attrs.ColorSource = ObjectColorSource.ColorFromObject
    attrs.ObjectColor = blue
    attrs.SetUserString("SketchingTool.Brush", "1;1;{0};{1}".format(12 / scale, scale))
    attrs.SetUserString("SketchingTool.Points", "FFFFFFFF")
    behind = doc.Objects.AddCurve(PolylineCurve(System.Array[Point3d](legacy)), attrs)
    conduit = controller_type.GetField("conduit", flags).GetValue(controller)
    conduit.Enabled = True
    toolbar.Enabled = False  # the tool option row can cover the sample pixels on a small viewport
    shaded = Rhino.Display.DisplayModeDescription.GetDisplayMode(Rhino.Display.DisplayModeDescription.ShadedId)
    viewport.DisplayMode = shaded

    def blue_samples():
        view.Redraw()
        image = view.CaptureToBitmap(viewport.Size, shaded)
        try:
            image.Save(os.path.join(output, "over-view-shaded.png"), System.Drawing.Imaging.ImageFormat.Png)
            def has_ink(y):
                for row in range(y - 3, y + 4):
                    for x in range(157, 174):
                        p = image.GetPixel(x, row)
                        if p.B - p.R > 35 and p.B - p.G > 15:
                            return True
                return False
            return has_ink(100), has_ink(132)
        finally:
            image.Dispose()

    new_visible, ordinary_visible = blue_samples()
    check(new_visible, "Pixels of a new Over view sketch are visible over a shaded solid")
    check(not ordinary_visible, "The same solid occludes an ordinary 3D sketch behind it")
    doc.Objects.Hide(solid, True)
    check(blue_samples()[1], "Removing the solid reveals the ordinary sketch at the same pixel")
    doc.Objects.Show(solid, True)
    attrs.SetUserString("Crayon.NamedView", first_key)
    check(doc.Objects.ModifyAttributes(behind, attrs, True), "Apply a legacy saved-camera tag to the occluded sketch")
    check(blue_samples()[1], "Pixels of a legacy Over view sketch are visible across the solid in its source view")
    panned = ViewportInfo(viewport)
    panned.SetCameraLocation(camera + viewport.CameraX * (4 / scale))
    viewport.SetViewProjection(panned, True)
    panned.Dispose()
    check(key() != first_key and not blue_samples()[1], "From another camera the solid occludes the legacy sketch in world space")
    check(same_points(doc.Objects.FindId(behind), legacy), "The foreground drawing pass preserves the stored geometry")
    call("RestoreView", "Renamed QA view")
    doc.Objects.Delete(doc.Objects.FindId(behind), True, True)
    doc.Objects.Delete(doc.Objects.FindId(solid), True, True)
    conduit.Enabled = False
    resized = ViewportInfo(viewport)
    resized.FrustumAspect = .6
    viewport.SetViewProjection(resized, True)
    resized.Dispose()
    check(same_points(doc.Objects.FindId(drawn_id), original), "Changing the viewport aspect ratio leaves the sketch fixed in world space")
    set_property("Tool", enum_value("SketchTool", "Erase"))
    set_property("Erase", enum_value("EraseMode", "Part"))
    call("RestoreView", duplicate)
    check(str(controller_type.GetProperty("Method").GetValue(controller, None)) == "View", "Going to a saved view changes only the camera, not the drawing method")
    moved = ViewportInfo(viewport)
    moved.SetCameraLocation(moved.CameraLocation + (original[-1] - original[0]) * .25)
    viewport.SetViewProjection(moved, True)
    moved.Dispose()
    check(key() != first_key and same_points(doc.Objects.FindId(drawn_id), original), "Moving the camera leaves every sketch point fixed in world space")
    shifted = viewport.WorldToClient(original[0])
    check(abs(shifted.X - screen_start.X) > 2, "The anchored sketch moves across the screen as the camera moves")
    middle = viewport.WorldToClient(original[len(original) // 2])
    call("PointerDown", view, System.Drawing.Point(int(round(middle.X)), int(round(middle.Y))), False, False, System.Double(-1))
    call("PointerUp")
    pieces = list(call("StrokeObjects", doc))
    check(len(pieces) == 2 and doc.Objects.FindId(drawn_id) is None, "Part eraser splits the anchored sketch at its new screen position in another view")
    saved_points = dict((p.Id, points(p)) for p in pieces)
    check(all(p.Attributes.GetUserString("Crayon.NamedView") == first_key and p.Attributes.ObjectColor.ToArgb() == blue.ToArgb() and p.Attributes.GetUserString("SketchingTool.Brush") for p in pieces), "Erased pieces keep source view, color and brush metadata")
    check(all(p.Attributes.GetUserString("Crayon.Hardness") for p in pieces), "Partial erase preserves hardness variation")
    set_property("Active", False)

    attributes = ObjectAttributes()
    attributes.SetUserString("Crayon.NamedView", first_key)
    attributes.SetUserString("SketchingTool.Stroke", "1")
    attributes.ColorSource = ObjectColorSource.ColorFromObject
    attributes.ObjectColor = System.Drawing.Color.FromArgb(12, 90, 180)
    attributes.Mode = ObjectMode.Hidden
    stroke = doc.Objects.AddCurve(PolylineCurve(System.Array[Point3d]([Point3d(0, 0, 0), Point3d(1, 0, 0)])), attributes)
    check(stroke != System.Guid.Empty, "Legacy sketch with a saved-view tag added")
    call("RestoreView", "Renamed QA view")
    path = os.path.join(output, "native-validation.3dm")
    options = Rhino.FileIO.FileWriteOptions()
    options.SuppressAllInput = True
    options.SuppressDialogBoxes = True
    check(doc.WriteFile(path, options), "Validation 3dm saves")
    reopened = Rhino.RhinoDoc.OpenHeadless(path)
    try:
        check(reopened is not None and reopened.NamedViews.Count == original_count + 2, "Named views survive reopen")
        saved = reopened.Objects.FindId(stroke)
        check(same_points(saved, [Point3d(0, 0, 0), Point3d(1, 0, 0)]), "A legacy view sketch keeps its original geometry after reopen")
        check(saved.Attributes.ObjectColor.ToArgb() == attributes.ObjectColor.ToArgb(), "Legacy sketch color survives reopen")
        check(all(same_points(reopened.Objects.FindId(id), pts) for id, pts in saved_points.items()), "Anchored sketch points retain their world coordinates after reopen")
        check(all(reopened.Objects.FindId(p.Id).Attributes.GetUserString("Crayon.Hardness") == p.Attributes.GetUserString("Crayon.Hardness") for p in pieces), "Pressure hardness survives save/reopen")
        check(reopened.Strings.GetValue("Crayon.ViewBindings", viewport.Id.ToString("D")) == first_key, "Viewport binding (the last saved view gone to) survives reopen")
    finally:
        reopened.Dispose()
    check(call("DeleteView", "Renamed QA view"), "Deleting a saved view")
    check(all(same_points(doc.Objects.FindId(id), pts) for id, pts in saved_points.items()), "Deleting the saved camera preserves all sketches drawn on its plane")
    check(doc.Objects.FindId(stroke) is not None, "Deleting the camera preserves legacy view sketches too")
    viewport.SetProjection(Rhino.Display.DefinedViewportProjection.Top, "Top", False)
    parallel_name = call("CaptureView", "QA parallel")
    set_property("Active", True)
    check(call("DrawOverView", parallel_name), "Choose a parallel saved camera")
    parallel = doc.NamedViews[doc.NamedViews.FindByName(parallel_name)].Viewport
    check(parallel.IsParallelProjection, "Parallel-camera fixture restored")
    call("PointerDown", view, System.Drawing.Point(100, 110), False, False, System.Double(.8))
    call("PointerMove", view, System.Drawing.Point(160, 110), System.Double(.8))
    call("PointerUp")
    parallel_stroke = next(s for s in call("StrokeObjects", doc) if s.Attributes.GetUserString("Crayon.NamedView") == key())
    parallel_points = points(parallel_stroke)
    shift = parallel.FrustumNearPlane.DistanceTo(parallel_points[0])
    check(0 < shift <= parallel.FrustumMinimumDiameter / 4 + 1e-8, "Parallel placement moves toward its virtual camera within a quarter of the saved frame")
    call("RestoreView", duplicate)
    check(same_points(doc.Objects.FindId(parallel_stroke.Id), parallel_points), "Parallel Over view sketches retain their world coordinates in perspective")
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
