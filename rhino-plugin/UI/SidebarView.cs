using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    /// <summary>The shared design-token page for the panel header, brush controls and model list.</summary>
    internal sealed class SidebarView : Panel
    {
        readonly SketchController controller;
        readonly bool header;
        readonly Func<string> tab;
        readonly Func<bool> dark;
        readonly Action<string, string> shellAction;
        readonly WebView web = new WebView { BrowserContextMenuEnabled = false };
        readonly UITimer timer = new UITimer { Interval = 0.2 };
        bool ready, taking, dirty = true;
        string told;
        uint document;
        bool? lastDark;

        public SidebarView(SketchController controller, bool header, Func<string> tab, Func<bool> dark, Action<string, string> shellAction)
        {
            this.controller = controller;
            this.header = header;
            this.tab = tab;
            this.dark = dark;
            this.shellAction = shellAction;
            Content = web;
            web.DocumentLoaded += (s, e) => { ready = true; told = null; dirty = true; };
            web.DocumentLoading += (s, e) => { if (e.Uri != null && (e.Uri.Scheme == "http" || e.Uri.Scheme == "https")) e.Cancel = true; };
            web.OpenNewWindow += (s, e) => e.Cancel = true;
            using (Stream stream = GetType().Assembly.GetManifestResourceStream("RhinoSketchingTool.sidebar.html"))
            using (var reader = new StreamReader(stream))
                web.LoadHtml(reader.ReadToEnd().Replace("{{MODE}}", header ? "header" : "body").Replace("{{LOGO}}", SketchPlugin.BrandSvg));
            timer.Elapsed += (s, e) => Tick();
            timer.Start();
            KeyHook.TextEntry.Add(() => Loaded && Visible && web.HasFocus);
        }

        public void RefreshState() => dirty = true;

        void Tick()
        {
            if (!ready || !Loaded || !Visible) return;
            bool appearance = dark();
            if (lastDark != appearance) { lastDark = appearance; dirty = true; }
            if (document != (RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0))
            {
                document = RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0;
                dirty = true;
            }
            Take();
            // Header is tiny; the brush page also follows changes made in the viewport.
            if (dirty || header || tab() == "sketch") Tell();
        }

        void Tell()
        {
            dirty = false;
            var state = new JsonMap { ["active"] = controller.Active, ["tab"] = tab(), ["dark"] = dark() };
            if (!header)
            {
                Brush brush = controller.Brush;
                state["tool"] = (int)controller.Tool;
                state["method"] = (int)controller.Method;
                state["erase"] = (int)controller.Erase;
                state["color"] = "#" + (controller.StrokeColor.ToArgb() & 0xffffff).ToString("X6");
                state["size"] = brush.Size;
                state["opacity"] = brush.Opacity * 100;
                state["hardness"] = brush.Hardness * 100;
                state["pressure"] = brush.Pressure * 100;
                state["sizePressure"] = brush.SizeByPressure;
                state["opacityPressure"] = brush.OpacityByPressure;
                state["hardnessPressure"] = brush.HardnessByPressure;
                state["preset"] = brush.Preset;
                state["snap"] = controller.SnapShapes;
                state["cursor"] = new List<object> { controller.Cursor.X, controller.Cursor.Y, controller.Cursor.Z };
                state["views"] = new List<object>();
                foreach (string view in controller.NamedViewNames) ((List<object>)state["views"]).Add(view);
                state["view"] = controller.CurrentViewName ?? "";
                if (tab() == "layers") AddLayers(state);
            }
            string json = Json.Write(state);
            if (json == told) return;
            told = json;
            web.ExecuteScriptAsync("crayonSidebar.state(" + json + ");");
        }

        void AddLayers(JsonMap state)
        {
            var sketches = new List<object>();
            var comments = new List<object>();
            state["sketches"] = sketches;
            state["comments"] = comments;
            RhinoDoc doc = RhinoDoc.ActiveDoc;
            state["document"] = doc?.Name ?? "";
            state["documentId"] = doc?.RuntimeSerialNumber ?? 0;
            if (doc == null) return;
            var groups = AnnotationGroups.Read(doc);
            state["sketchGroups"] = AnnotationGroups.AsJson(groups.Sketches, groups.ActiveSketch);
            state["commentGroups"] = AnnotationGroups.AsJson(groups.Comments, groups.ActiveComment);
            state["activeOnly"] = groups.ActiveOnly;
            state["newGroup"] = groups.NewGroup;
            state["hideUsed"] = groups.HideUsed;
            int number = 0;
            foreach (RhinoObject obj in SketchController.StrokeObjects(doc))
            {
                number++;
                string name = obj.Name;
                if (string.IsNullOrWhiteSpace(name)) name = "Stroke " + number;
                else if (name.StartsWith("Sketch ", StringComparison.Ordinal) && int.TryParse(name.Substring(7), out int legacyNumber)) name = "Stroke " + legacyNumber;
                sketches.Add(new JsonMap { ["id"] = obj.Id.ToString(), ["name"] = name,
                    ["group"] = AnnotationGroups.GroupOf(obj.Attributes), ["ownVisible"] = obj.Attributes.GetUserString(AnnotationGroups.HiddenKey) != "1",
                    ["layer"] = LayerName(doc, obj), ["visible"] = SketchController.OnVisibleLayer(doc, obj),
                    ["overlay"] = !string.IsNullOrEmpty(obj.Attributes.GetUserString(SketchController.ViewStrokeKey)),
                    ["color"] = "#" + (obj.Attributes.ObjectColor.ToArgb() & 0xffffff).ToString("X6") });
            }
            foreach (Comment comment in controller.Comments(doc))
            {
                var attributes = doc.Objects.FindId(comment.Id)?.Attributes;
                if (attributes == null) continue;
                comments.Add(new JsonMap { ["id"] = comment.Id.ToString(), ["number"] = comment.Number, ["text"] = comment.Text, ["visible"] = comment.Visible,
                    ["group"] = AnnotationGroups.GroupOf(attributes), ["ownVisible"] = attributes.GetUserString(AnnotationGroups.HiddenKey) != "1" });
            }
        }

        static string LayerName(RhinoDoc doc, RhinoObject obj)
        {
            int index = obj.Attributes.LayerIndex;
            return index >= 0 && index < doc.Layers.Count ? doc.Layers[index].FullPath : "";
        }

        async void Take()
        {
            if (taking) return;
            taking = true;
            try
            {
                string answer = (await web.ExecuteScriptAsync("return crayonSidebar.take();") ?? "").Trim().Trim('"');
                if (answer.Length == 0 || answer == "null") return;
                foreach (object item in Json.List(Json.Parse(Uri.UnescapeDataString(answer)))) Act(Json.Map(item));
                dirty = true;
            }
            catch (Exception ex) { RhinoApp.WriteLine("Crayon sidebar: " + ex.Message); }
            finally { taking = false; }
        }

        void Act(JsonMap action)
        {
            string type = Json.Text(action.At("type")), value = Json.Text(action.At("value"));
            if (type == "tab") { shellAction(type, value); return; }
            if (type == "power") { controller.Active = !controller.Active; return; }
            if (type == "activeOnly" || type == "newGroup") { AnnotationGroups.ToggleOption(RhinoDoc.ActiveDoc, type == "activeOnly"); return; }
            if (type == "hideUsed") { AnnotationGroups.ToggleHideUsed(RhinoDoc.ActiveDoc); return; }
            if (type == "groupActive" || type == "groupVisible")
            {
                string[] parts = value.Split(':');
                if (parts.Length == 2 && (parts[0] == "sketch" || parts[0] == "comment") && int.TryParse(parts[1], out int group))
                {
                    if (type == "groupActive") AnnotationGroups.SetActive(RhinoDoc.ActiveDoc, parts[0] == "comment", group);
                    else AnnotationGroups.ToggleGroup(RhinoDoc.ActiveDoc, parts[0] == "comment", group);
                }
                return;
            }
            if (type == "tool" && int.TryParse(value, out int tool) && Enum.IsDefined(typeof(SketchTool), tool)) controller.Tool = (SketchTool)tool;
            else if (type == "method" && int.TryParse(value, out int method) && Enum.IsDefined(typeof(DrawMethod), method)) controller.Method = (DrawMethod)method;
            else if (type == "erase" && int.TryParse(value, out int erase) && Enum.IsDefined(typeof(EraseMode), erase)) controller.Erase = (EraseMode)erase;
            else if (type == "snap") controller.SnapShapes = !controller.SnapShapes;
            else if (type == "sizePressure") controller.Brush.SizeByPressure = !controller.Brush.SizeByPressure;
            else if (type == "opacityPressure") controller.Brush.OpacityByPressure = !controller.Brush.OpacityByPressure;
            else if (type == "hardnessPressure") controller.Brush.HardnessByPressure = !controller.Brush.HardnessByPressure;
            else if (type == "color" && value.Length == 7 && value[0] == '#' && int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                controller.StrokeColor = System.Drawing.Color.FromArgb(255, rgb >> 16, (rgb >> 8) & 255, rgb & 255);
            else if (type == "view") controller.RestoreView(value);
            else if (type == "capture") controller.CaptureView();
            else if (type == "look" && int.TryParse(value, out int look) && look >= 0 && look <= 3) controller.SetView(look);
            else if (type == "cursor")
            {
                var xyz = Json.List(action.At("xyz"));
                if (xyz.Count == 3 && Number(xyz[0], out double x) && Number(xyz[1], out double y) && Number(xyz[2], out double z)) controller.SetCursor(new Point3d(x, y, z));
            }
            else if (type == "brush" && Number(action.At("number"), out double number))
            {
                switch (value)
                {
                    case "size": controller.Brush.Size = number; break;
                    case "opacity": controller.Brush.Opacity = number / 100; break;
                    case "pressure": controller.Brush.Pressure = number / 100; break;
                    case "hardness": controller.Brush.Hardness = number / 100; break;
                }
            }
            else if (type == "preset") ApplyPreset(value);
            else if (type == "visible" || type == "layer" || type == "delete" || type == "rename" || type == "select") EditModel(type, value, Json.Text(action.At("name")));
        }

        static bool Number(object value, out double number)
        {
            bool valid = double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            return valid && !double.IsInfinity(number) && !double.IsNaN(number);
        }

        void ApplyPreset(string value)
        {
            if (value != "crayon" && value != "pencil" && value != "marker" && value != "soft") return;
            Brush brush = controller.Brush;
            brush.Preset = value;
            brush.HardnessByPressure = false;
            if (value == "crayon")
            {
                brush.Size = 6; brush.Opacity = 0.8; brush.Hardness = 0.6; brush.Pressure = 0.7;
                brush.SizeByPressure = brush.OpacityByPressure = true;
                controller.StrokeColor = System.Drawing.Color.FromArgb(151, 49, 176);
            }
            else if (value == "pencil") { brush.Size = 3; brush.Opacity = 0.8; brush.Hardness = 0.8; brush.Pressure = 0.7; brush.SizeByPressure = brush.OpacityByPressure = true; }
            else if (value == "marker") { brush.Size = 12; brush.Opacity = 0.55; brush.Hardness = 0.9; brush.Pressure = 0.2; brush.SizeByPressure = brush.OpacityByPressure = false; }
            else if (value == "soft") { brush.Size = 24; brush.Opacity = 0.45; brush.Hardness = 0.15; brush.Pressure = 0.8; brush.SizeByPressure = brush.OpacityByPressure = true; }
            controller.Tool = SketchTool.Draw;
        }

        void EditModel(string type, string value, string name)
        {
            RhinoDoc doc = RhinoDoc.ActiveDoc;
            if (doc == null || !Guid.TryParse(value, out Guid id)) return;
            if (type == "layer")
            {
                Layer layer = doc.Layers.FindId(id);
                if (layer == null || layer.IsDeleted) return;
                uint record = doc.BeginUndoRecord("Crayon layer visibility");
                try { layer.IsVisible = !layer.IsVisible; doc.Layers.Modify(layer, layer.Index, true); }
                finally { if (record != 0) doc.EndUndoRecord(record); }
            }
            else
            {
                RhinoObject obj = doc.Objects.FindId(id);
                if (obj == null || obj.IsDeleted || (obj.Attributes.GetUserString(SketchController.StrokeKey) == null && obj.Attributes.GetUserString(SketchController.CommentKey) == null)) return;
                if (type == "visible")
                {
                    uint record = doc.BeginUndoRecord("Crayon visibility");
                    try
                    {
                        var attributes = obj.Attributes.Duplicate();
                        attributes.SetUserString(AnnotationGroups.HiddenKey, attributes.GetUserString(AnnotationGroups.HiddenKey) == "1" ? null : "1");
                        doc.Objects.ModifyAttributes(id, attributes, true);
                    }
                    finally { if (record != 0) doc.EndUndoRecord(record); }
                    doc.Views.Redraw();
                    return;
                }
                if (type == "select")
                {
                    // View sketches stay hidden Rhino objects: ordinary selection would
                    // expose their proxy geometry in every viewport. Rename/delete are
                    // available here; the eraser edits them in their associated view.
                    if (!string.IsNullOrEmpty(obj.Attributes.GetUserString(SketchController.ViewStrokeKey))) return;
                    controller.Tool = SketchTool.Select;
                    doc.Objects.UnselectAll();
                    doc.Objects.Select(id);
                }
                else
                {
                    uint record = doc.BeginUndoRecord(type == "delete" ? "Delete Crayon sketch or comment" : "Rename Crayon sketch");
                    try
                    {
                        if (type == "delete") doc.Objects.Delete(obj, true, true);
                        else if (!string.IsNullOrWhiteSpace(name))
                        {
                            ObjectAttributes attributes = obj.Attributes.Duplicate();
                            attributes.Name = name.Trim();
                            doc.Objects.ModifyAttributes(id, attributes, true);
                        }
                    }
                    finally { if (record != 0) doc.EndUndoRecord(record); }
                }
            }
            doc.Views.Redraw();
        }
    }
}
