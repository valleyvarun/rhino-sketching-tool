using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eto.Forms;
using Rhino;
using Rhino.Display;
using Media = System.Windows.Media;
using Wpf = System.Windows;
using ScreenPoint = System.Drawing.Point;
using Color = System.Drawing.Color;

namespace RhinoSketchingTool
{
    // Companion to the tool dock. It uses the same viewport coordinates for painting
    // and hit testing, including when DPI, viewport size, or dock orientation changes.
    internal sealed partial class Toolbar
    {
        enum OptionsPopup { None, Colors, Views }
        OptionsPopup optionsPopup;
        DisplayBitmap optionsPicture, popupPicture;
        string optionsSignature, popupSignature;
        double hue = 220, saturation = .15, value = .15;
        int pickerArgb;
        int viewPage;
        bool clearAsked;
        const double BarGap = 8, RowHeight = 36, PopupWidth = 284;
        static readonly Color[] Swatches = { Color.FromArgb(35,38,44), Color.FromArgb(32,94,211), Color.FromArgb(211,46,39), Color.FromArgb(17,132,91), Color.FromArgb(139,86,245) };

        sealed class Frame
        {
            public double Scale;
            public ScreenPoint ToolOrigin, OptionOrigin, PopupOrigin;
            public Wpf.Size OptionsSize, PopupSize;
            public List<Item> Items;
            public IReadOnlyList<string> Views;
            public int PageSize;
        }

        static double OptionWidth(Item item) => item.Part == Part.Color ? 30 :
            Math.Min(item.Part == Part.Help ? 330 : 190, LabelWidth(item.Value ?? "") +
            (item.Part == Part.View ? 42 : item.Part == Part.Snap ? 38 : item.Part == Part.SavedView ? 42 : 22));

        // Native QA reads the hit-test layout without requiring IronPython to unbox private enum fields.
        internal string[] OptionParts(RhinoView view) => OptionsFrame(view).Items.Select(item => item.Part.ToString()).ToArray();

        Frame OptionsFrame(RhinoView view, double fittedScale = 0)
        {
            double scale = fittedScale > 0 ? fittedScale : Scale(view);
            var viewport = view.ActiveViewport.Size;
            Layout(out Wpf.Size toolSize);
            double availableWidth = Math.Max(100, viewport.Width / scale - 2 * Inset);
            var specs = new List<Item>();
            var current = ActivePart;
            if (current == Part.Pen)
            {
                specs.AddRange(new[] { new Item { Part=Part.Object, Value="Object" }, new Item { Part=Part.Plane, Value="Plane" },
                    new Item { Part=Part.Cursor, Value="3D cursor" } });
                if (controller.Method == DrawMethod.Plane) specs.Add(new Item { Part=Part.PickFace, DividerBefore=true, Value=controller.PickingPlane ? "Pick a face…" : controller.HasPickedPlane ? controller.PickedPlaneLabel + " ×" : "Pick a face" });
                else if (controller.Method == DrawMethod.Cursor) specs.Add(new Item { Part=Part.PlaceCursor, DividerBefore=true, Value="Place 3D cursor" });
                specs.AddRange(new[] { new Item { Part=Part.Color, DividerBefore=true }, new Item { Part=Part.Snap, Value="Shapes", DividerBefore=true } });
            }
            else if (current == Part.Eraser)
                specs.AddRange(new[] { new Item {Part=Part.EraseWhole,Value="Stroke"},new Item {Part=Part.ErasePart,Value="Part"},
                    new Item {Part=Part.ClearSketches,Value=clearAsked ? "Click again to clear" : "Clear all sketches"} });
            else if (current == Part.Comments)
                specs.Add(new Item {Part=Part.Help,Value="Click a surface or sketch to add a comment."});
            else if (current == Part.Views)
            {
                specs.Add(new Item {Part=Part.SaveView,Value="+ Save view"});
                foreach (var name in controller.NamedViewNames)
                {
                    if (specs.Count >= 5) break;
                    specs.Add(new Item {Part=Part.SavedView,Value=name});
                }
                specs.Add(new Item {Part=Part.SyncViews,Value="Named Views ↻"});
            }
            else
            {
                int selected = RhinoDoc.ActiveDoc?.Objects.GetSelectedObjects(false,false).Count() ?? 0;
                if (selected > 0) specs.AddRange(new[] {new Item {Part=Part.SelectedCount,Value=selected+" selected"},new Item {Part=Part.ClearSelection,Value="Clear"}});
            }
            if (current != Part.Pen && optionsPopup == OptionsPopup.Colors) CloseOptionsPopup();
            double preferred = 10;
            foreach (var spec in specs) preferred += OptionWidth(spec) + 6 + (spec.DividerBefore ? 12 : 0);
            double width = Math.Min(preferred,availableWidth);
            var items = new List<Item>();
            double x = 5, y = 7;
            foreach (var spec in specs)
            {
                double length = Math.Min(width-10,OptionWidth(spec));
                double gap = spec.DividerBefore && x > 5 ? 12 : 0;
                if(x>5 && x+gap+length>width-5) { x=5; y+=RowHeight; gap=0; }
                x += gap;
                items.Add(new Item {Part=spec.Part,Value=spec.Value,DividerBefore=gap>0,Box=new Wpf.Rect(x,y,length,30)});
                x+=length+6;
            }
            var optionSize = specs.Count == 0 ? new Wpf.Size(0,0) : new Wpf.Size(width,y+37);
            bool beside = false;
            bool showOptions = !collapsed && specs.Count > 0;
            var pair = !showOptions ? toolSize : Upright
                ? new Wpf.Size(toolSize.Width+BarGap+width,Math.Max(toolSize.Height,optionSize.Height))
                : new Wpf.Size(Math.Max(toolSize.Width,width),toolSize.Height+BarGap+optionSize.Height);
            // Rhino's four-view layout can be quite short at high DPI. Fit the entire
            // dock pair, then use the same scale for rendering and pointer coordinates.
            double neededHeight = Math.Max(pair.Height, optionsPopup == OptionsPopup.Colors ? 180 : 0);
            double fitScale = Math.Min(viewport.Width / (pair.Width + Inset * 2), viewport.Height / (neededHeight + Inset * 2));
            if (fitScale + .001 < scale && scale > .1)
                return OptionsFrame(view, Math.Max(.1, fitScale));
            var origin = Origin(viewport, pair, scale);
            // Bottom-left axis clearance must not push the pair beyond the right edge.
            origin.X = Math.Max(0, Math.Min(origin.X, (int)Math.Max(0, viewport.Width - pair.Width * scale - Inset * scale)));
            origin.Y = Math.Max(0, Math.Min(origin.Y, (int)Math.Max(0, viewport.Height - pair.Height * scale - Inset * scale)));
            bool rightDock = dock == ToolbarDock.Right || dock == ToolbarDock.TopRight || dock == ToolbarDock.BottomRight;
            double toolX = rightDock && !beside && !collapsed ? pair.Width - toolSize.Width : 0;
            var frame = new Frame
            {
                Scale = scale, Items = items, OptionsSize = optionSize,
                ToolOrigin = new ScreenPoint(origin.X + (int)(toolX * scale), origin.Y),
                OptionOrigin = beside
                    ? new ScreenPoint(origin.X + (int)((toolSize.Width + BarGap) * scale), origin.Y)
                    : new ScreenPoint(origin.X, origin.Y + (int)((toolSize.Height + BarGap) * scale)),
            };
            bool bottomDock = dock == ToolbarDock.Bottom || dock == ToolbarDock.BottomLeft || dock == ToolbarDock.BottomRight;
            if (!Upright)
            {
                frame.ToolOrigin = new ScreenPoint(origin.X+(int)((pair.Width-toolSize.Width)*scale/2), origin.Y+(int)((showOptions && bottomDock ? optionSize.Height+BarGap : 0)*scale));
                frame.OptionOrigin = new ScreenPoint(origin.X+(int)((pair.Width-optionSize.Width)*scale/2),origin.Y+(int)((bottomDock ? 0 : toolSize.Height+BarGap)*scale));
            }
            else
            {
                frame.ToolOrigin = new ScreenPoint((int)(dock==ToolbarDock.Right ? viewport.Width-(Inset+toolSize.Width)*scale : Inset*scale), (int)Math.Max(0,(viewport.Height-toolSize.Height*scale)/2));
                frame.OptionOrigin = new ScreenPoint((int)(dock==ToolbarDock.Right ? frame.ToolOrigin.X-(BarGap+optionSize.Width)*scale : frame.ToolOrigin.X+(toolSize.Width+BarGap)*scale),
                    (int)Math.Max(Inset*scale,Math.Min(viewport.Height-(optionSize.Height+Inset)*scale,frame.ToolOrigin.Y+ButtonLength*scale)));
            }
            frame.Views = controller.NamedViewNames;
            frame.PageSize = Math.Max(1, Math.Min(8, (int)(viewport.Height / scale - 100) / 30));
            viewPage = Math.Max(0, Math.Min(viewPage, Math.Max(0, (frame.Views.Count - 1) / frame.PageSize)));
            double popupWidth = Math.Min(PopupWidth, availableWidth);
            double popupHeight = optionsPopup == OptionsPopup.Colors ? Math.Min(302, Math.Max(180, viewport.Height / scale - 2 * Inset)) : 48 + Math.Min(frame.PageSize, Math.Max(1, frame.Views.Count - viewPage * frame.PageSize)) * 30 + (frame.Views.Count > frame.PageSize ? 30 : 0);
            frame.PopupSize = new Wpf.Size(popupWidth, popupHeight);
            var anchor = items.Find(i => i.Part == (optionsPopup == OptionsPopup.Colors ? Part.Color : Part.View)).Box;
            double px = frame.OptionOrigin.X + anchor.X * scale;
            double py = frame.OptionOrigin.Y + (anchor.Bottom + 8) * scale;
            if (py + popupHeight * scale > viewport.Height - Inset * scale)
                py = frame.OptionOrigin.Y + (anchor.Top - popupHeight - 8) * scale;
            frame.PopupOrigin = new ScreenPoint((int)Math.Max(0, Math.Min(px, viewport.Width - popupWidth * scale - Inset * scale)),
                (int)Math.Max(0, Math.Min(py, viewport.Height - popupHeight * scale - Inset * scale)));
            return frame;
        }

        static Wpf.Point Local(ScreenPoint point, ScreenPoint origin, double scale) => new Wpf.Point((point.X - origin.X) / scale, (point.Y - origin.Y) / scale);
        static Wpf.Rect SvBox(Frame f) => new Wpf.Rect(12, 54, f.PopupSize.Width - 24, f.PopupSize.Height - 136);
        static Wpf.Rect HueBox(Frame f) => new Wpf.Rect(12, f.PopupSize.Height - 68, f.PopupSize.Width - 24, 16);

        Part HitOptions(Frame f, ScreenPoint point)
        {
            if (collapsed || f.Items.Count == 0) return Part.None;
            var p = Local(point, f.PopupOrigin, f.Scale);
            if (optionsPopup != OptionsPopup.None && new Wpf.Rect(f.PopupSize).Contains(p))
            {
                if (optionsPopup == OptionsPopup.Colors)
                {
                    if (SvBox(f).Contains(p)) return Part.Saturation;
                    if (HueBox(f).Contains(p)) return Part.Hue;
                    if (p.Y >= f.PopupSize.Height - 38) return Part.Hex;
                    if (p.Y < 48)
                        for (int i = 0; i < 5; i++)
                            if (Math.Abs(p.X - (27 + i * (f.PopupSize.Width - 54) / 4)) <= 18) return Part.Swatch0 + i;
                }
                return Part.Popup;
            }
            p = Local(point, f.OptionOrigin, f.Scale);
            if (!new Wpf.Rect(f.OptionsSize).Contains(p)) return Part.None;
            foreach (var item in f.Items)
                if (item.Box.Contains(p))
                    return item.Part == Part.View && p.X >= item.Box.Right - 28 ? Part.ViewList : item.Part;
            return Part.Bar;
        }

        void CloseOptionsPopup() { optionsPopup = OptionsPopup.None; popupSignature = null; }

        bool ClickOptions(Part part, RhinoView view, ScreenPoint point)
        {
            switch (part)
            {
                case Part.Object: SetDrawMethod(DrawMethod.Surface); break;
                case Part.Plane: SetDrawMethod(DrawMethod.Plane); break;
                case Part.Cursor: SetDrawMethod(DrawMethod.Cursor); break;
                case Part.View: SetDrawMethod(DrawMethod.View); break;
                case Part.Snap: controller.SnapShapes = !controller.SnapShapes; break;
                case Part.Color:
                    optionsPopup = optionsPopup == OptionsPopup.Colors ? OptionsPopup.None : OptionsPopup.Colors;
                    ReadColor(); break;
                case Part.ViewList:
                    optionsPopup = optionsPopup == OptionsPopup.Views ? OptionsPopup.None : OptionsPopup.Views;
                    viewPage = 0; break;
                case Part.PlaceCursor: controller.BeginPlaceCursor(); break;
                case Part.PickFace:
                    if (controller.HasPickedPlane) controller.ResetPickedPlane(); else controller.BeginPickPlane();
                    break;
                case Part.EraseWhole: controller.Erase = EraseMode.Stroke; break;
                case Part.ErasePart: controller.Erase = EraseMode.Part; break;
                case Part.ClearSelection: RhinoDoc.ActiveDoc?.Objects.UnselectAll(); break;
                case Part.Help: case Part.SelectedCount: break;
                case Part.ClearSketches:
                    if (!clearAsked) { clearAsked = true; break; }
                    clearAsked = false;
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                    {
                        uint undo = doc.BeginUndoRecord("Clear Crayon sketches");
                        try { foreach(var stroke in SketchController.StrokeObjects(doc)) if (!stroke.IsLocked) doc.Objects.Delete(stroke,true,true); }
                        finally { if (undo != 0) doc.EndUndoRecord(undo); }
                    }
                    break;
                case Part.SaveView: CaptureNamedView(); break;
                case Part.SyncViews:
                    optionsPopup = optionsPopup == OptionsPopup.Views ? OptionsPopup.None : OptionsPopup.Views;
                    viewPage = 0; break;
                case Part.SavedView:
                    var frame = OptionsFrame(view);
                    var local = Local(point,frame.OptionOrigin,frame.Scale);
                    var chosen = frame.Items.Find(i=>i.Part==Part.SavedView && i.Box.Contains(local));
                    if (local.X > chosen.Box.Right-22) DeleteView(chosen.Value);
                    else controller.RestoreView(chosen.Value);
                    break;
                case Part.Saturation: case Part.Hue: break;
                case Part.Hex: EditHex(); break;
                case Part.Swatch0: case Part.Swatch1: case Part.Swatch2: case Part.Swatch3: case Part.Swatch4:
                    controller.StrokeColor = Swatches[part - Part.Swatch0]; ReadColor(); break;
                case Part.Popup:
                    if (optionsPopup == OptionsPopup.Views)
                    {
                        var f = OptionsFrame(view);
                        var p = Local(point, f.PopupOrigin, f.Scale);
                        int row = (int)((p.Y - 38) / 30);
                        int count = Math.Min(f.PageSize, f.Views.Count - viewPage * f.PageSize);
                        if (p.Y < 38) { CloseOptionsPopup(); CaptureNamedView(); }
                        else if (row < count && row >= 0)
                        {
                            string chosenView = f.Views[viewPage * f.PageSize + row];
                            if(p.X > f.PopupSize.Width-32) DeleteView(chosenView);
                            else if(p.X > f.PopupSize.Width-62) RenameView(chosenView);
                            else { controller.RestoreView(chosenView); if (!savedViewsTool) SetDrawMethod(DrawMethod.View); else CloseOptionsPopup(); }
                        }
                        else if (f.Views.Count > f.PageSize && row >= count)
                            viewPage = (viewPage + 1) % ((f.Views.Count + f.PageSize - 1) / f.PageSize);
                    }
                    break;
                default: return false;
            }
            return true;
        }

        void SetDrawMethod(DrawMethod method)
        {
            CloseOptionsPopup();
            controller.Method = method;
            controller.Tool = SketchTool.Draw;
        }

        void CaptureNamedView()
        {
            CloseOptionsPopup();
            // Dialogs are opened after the viewport mouse callback returns.
            Application.Instance.AsyncInvoke(() =>
            {
                string name = AskText("Capture view", "Name this Rhino view", "View " + (controller.NamedViewNames.Count + 1));
                if (name == null) return;
                string saved = controller.CaptureView(name);
                if(saved != null) { cameraSaved=DateTime.UtcNow; if(MotionEnabled) motionTimer.Start(); }
                if (saved != null && !savedViewsTool) SetDrawMethod(DrawMethod.View);
                Redraw();
            });
        }

        static string AskText(string title, string prompt, string initial)
        {
            using (var dialog = new Dialog<string> { Title = title, Padding = 16, Resizable = false })
            {
                var input = new TextBox { Text = initial, Width = 270 };
                var save = new Button { Text = "Save" };
                var cancel = new Button { Text = "Cancel" };
                save.Click += (s, e) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(input.Text.Trim()); };
                cancel.Click += (s, e) => dialog.Close(null);
                dialog.DefaultButton = save; dialog.AbortButton = cancel;
                dialog.Content = new StackLayout { Spacing = 12, Items = { new Label { Text = prompt }, input,
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { cancel, save } } } };
                Func<bool> textEntry = () => dialog.Visible;
                KeyHook.TextEntry.Add(textEntry);
                try { return dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); }
                finally { KeyHook.TextEntry.Remove(textEntry); }
            }
        }

        string Hex => "#" + controller.StrokeColor.R.ToString("X2") + controller.StrokeColor.G.ToString("X2") + controller.StrokeColor.B.ToString("X2");
        void EditHex()
        {
            Application.Instance.AsyncInvoke(() =>
            {
                string text = AskText("Sketch color", "Hex color (#RRGGBB)", Hex);
                if (text == null) return;
                text = text.TrimStart('#');
                if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                { controller.StrokeColor = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); ReadColor(); }
                else MessageBox.Show("Enter six hexadecimal digits, for example #23262C.", "Sketch color");
                Redraw();
            });
        }

        void ReadColor()
        {
            Color c = controller.StrokeColor;
            pickerArgb = c.ToArgb();
            double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            if (max != min) hue = c.GetHue();
            value = max / 255; saturation = max == 0 ? 0 : 1 - min / max;
        }

        void PickColor(RhinoView view, ScreenPoint point, Part part)
        {
            var f = OptionsFrame(view); var p = Local(point, f.PopupOrigin, f.Scale);
            if (part == Part.Hue) { var r = HueBox(f); hue = 360 * Clamp((p.X - r.X) / r.Width); }
            else { var r = SvBox(f); saturation = Clamp((p.X - r.X) / r.Width); value = 1 - Clamp((p.Y - r.Y) / r.Height); }
            var c = Hsv(hue, saturation, value);
            pickerArgb = Color.FromArgb(c.R, c.G, c.B).ToArgb();
            controller.StrokeColor = Color.FromArgb(c.R, c.G, c.B);
            Redraw();
        }
        static double Clamp(double n) => Math.Max(0, Math.Min(1, n));
        static double LabelWidth(string label) => Math.Ceiling(Picture.Text(label, 11.5, Wpf.FontWeights.Medium, Ink2).WidthIncludingTrailingWhitespace);
        static Media.Color Hsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; } else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        void DrawOptions(DrawEventArgs e, Frame f)
        {
            if (collapsed || f.Items.Count == 0) return;
            if (optionsPopup == OptionsPopup.Colors && pickerArgb != controller.StrokeColor.ToArgb()) ReadColor();
            string signature = CrayonAppearance.Dark + "|" + ActivePart + "|" + controller.Erase + "|" + string.Join("|",f.Items.Select(i=>i.Value)) + "|" + f.OptionsSize + "|" + f.Scale + "|" + controller.Method + "|" + controller.CurrentViewName + "|" + Hex + "|" + controller.SnapShapes + "|" + hover + "|" + pressed;
            if (optionsPicture == null || signature != optionsSignature)
            {
                optionsPicture?.Dispose();
                using (var bitmap = RenderOptions(f)) optionsPicture = new DisplayBitmap(bitmap);
                optionsSignature = signature;
            }
            int margin = (int)(Margin * f.Scale);
            e.Display.DrawBitmap(optionsPicture, f.OptionOrigin.X - margin, f.OptionOrigin.Y - margin);
            if (optionsPopup == OptionsPopup.None) return;
            signature = CrayonAppearance.Dark + "|" + optionsPopup + "|" + f.PopupSize + "|" + f.Scale + "|" + Hex + "|" + hue + "|" + saturation + "|" + value + "|" + viewPage + "|" + controller.CurrentViewName + "|" + string.Join("\n", f.Views);
            if (popupPicture == null || signature != popupSignature)
            {
                popupPicture?.Dispose();
                using (var bitmap = RenderPopup(f)) popupPicture = new DisplayBitmap(bitmap);
                popupSignature = signature;
            }
            e.Display.DrawBitmap(popupPicture, f.PopupOrigin.X - margin, f.PopupOrigin.Y - margin);
        }

        System.Drawing.Bitmap RenderOptions(Frame f) => Picture.Render(f.OptionsSize.Width + Margin * 2, f.OptionsSize.Height + Margin * 2, f.Scale, c =>
        {
            c.PushTransform(new Media.TranslateTransform(Margin, Margin));
            Picture.Card(c, new Wpf.Rect(f.OptionsSize), 15);
            foreach (var item in f.Items) PaintOption(c, item);
            c.Pop();
        });

        void RenameView(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            Application.Instance.AsyncInvoke(() =>
            {
                string next = AskText("Rename view","View name",name);
                var doc = RhinoDoc.ActiveDoc;
                if (next == null || doc == null || next == name) return;
                if (doc.NamedViews.FindByName(next)>=0) { MessageBox.Show("A view with that name already exists.","Rename view"); return; }
                uint undo = doc.BeginUndoRecord("Rename Crayon view");
                try { doc.NamedViews.Rename(name,next); }
                finally { if(undo!=0)doc.EndUndoRecord(undo); }
                Redraw();
            });
        }

        void DeleteView(string name)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || string.IsNullOrEmpty(name)) return;
            uint undo = doc.BeginUndoRecord("Delete Crayon view");
            try { doc.NamedViews.Delete(name); }
            finally { if(undo!=0)doc.EndUndoRecord(undo); }
            Redraw();
        }

        System.Drawing.Bitmap RenderPopup(Frame f) => Picture.Render(f.PopupSize.Width + Margin * 2, f.PopupSize.Height + Margin * 2, f.Scale, c =>
        {
            c.PushTransform(new Media.TranslateTransform(Margin, Margin));
            Picture.Card(c, new Wpf.Rect(f.PopupSize), 12);
            if (optionsPopup == OptionsPopup.Colors) PaintColors(c, f); else PaintViews(c, f);
            c.Pop();
        });

        // Visual QA uses the exact same renderer as the viewport, like SavePicture.
        internal void SaveDrawingPictures(string folder)
        {
            var view = RhinoDoc.ActiveDoc?.Views.ActiveView;
            if (view == null) return;
            System.IO.Directory.CreateDirectory(folder);
            var previous = optionsPopup;
            try
            {
                optionsPopup = OptionsPopup.Colors;
                ReadColor();
                var frame = OptionsFrame(view);
                using (var bitmap = RenderOptions(frame)) bitmap.Save(System.IO.Path.Combine(folder, "drawing-bar.png"), System.Drawing.Imaging.ImageFormat.Png);
                using (var bitmap = RenderPopup(frame)) bitmap.Save(System.IO.Path.Combine(folder, "color-picker.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            finally { optionsPopup = previous; }
        }

        void PaintOption(Media.DrawingContext c, Item item)
        {
            var box = item.Box;
            if (item.DividerBefore) c.DrawLine(new Media.Pen(Fill(Shade, .16), 1), new Wpf.Point(box.X-9,box.Y+5), new Wpf.Point(box.X-9,box.Bottom-5));
            bool selected = item.Part == Part.Object && controller.Method == DrawMethod.Surface || item.Part == Part.Plane && controller.Method == DrawMethod.Plane || item.Part == Part.Cursor && controller.Method == DrawMethod.Cursor || item.Part == Part.View && controller.Method == DrawMethod.View
                || item.Part==Part.PlaceCursor && controller.PlacingCursor || item.Part==Part.PickFace && controller.PickingPlane || item.Part==Part.SavedView && item.Value==controller.CurrentViewName || item.Part==Part.EraseWhole && controller.Erase==EraseMode.Stroke || item.Part==Part.ErasePart && controller.Erase==EraseMode.Part;
            bool passive = item.Part==Part.Help || item.Part==Part.SelectedCount || item.Part==Part.Color || item.Part==Part.Snap || item.Part==Part.SyncViews;
            if(!passive) c.DrawRoundedRectangle(Fill(selected ? Picture.Selected : Picture.Well), null, box, 10, 10);
            bool arrowHover = item.Part == Part.View && (hover == Part.ViewList || pressed == Part.ViewList);
            if (hover == item.Part || pressed == item.Part || arrowHover) c.DrawRoundedRectangle(Fill(Shade, pressed == item.Part || pressed == Part.ViewList && item.Part == Part.View ? PressShade : HoverShade), null, box, 8, 8);
            string label = item.Value ?? "";
            double textX = box.X + 11;
            if (item.Part == Part.Color)
            {
                var color = controller.StrokeColor;
                var p = new Wpf.Point(box.X + box.Width / 2, box.Y + box.Height / 2);
                c.DrawEllipse(Fill(Rgb(color.R,color.G,color.B)), new Media.Pen(Fill(Glass), 2), p, 10, 10);
                c.DrawEllipse(null, new Media.Pen(Fill(Accent), 1.5), p, 11.5, 11.5);
                return;
            }
            if (item.Part == Part.Snap)
            {
                var check = new Wpf.Rect(box.X + 5, box.Y + 7, 14, 14);
                c.DrawRoundedRectangle(Fill(Glass), new Media.Pen(Fill(Shade, .14), 1), check, 4, 4);
                if (controller.SnapShapes)
                {
                    var pen = new Media.Pen(Fill(Accent), 2.2) { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round };
                    c.DrawLine(pen, new Wpf.Point(check.X+3,check.Y+7), new Wpf.Point(check.X+7,check.Y+11));
                    c.DrawLine(pen, new Wpf.Point(check.X+7,check.Y+11), new Wpf.Point(check.X+17,check.Y-3));
                }
                textX = box.X + 26;
            }
            if (item.Part == Part.View) PaintIcon(c, Icons.Chevron, new Wpf.Point(box.Right - 14, box.Y+15), 18, arrowHover ? Ink : Ink2, 1, 0);
            if (item.Part == Part.SavedView) c.DrawText(Picture.Text("×",16,Wpf.FontWeights.Normal,Muted), new Wpf.Point(box.Right-20,box.Y+3));
            var text = Picture.Text(label, 12, Wpf.FontWeights.Medium, item.Part==Part.ClearSketches ? Rgb(0xe5,0x48,0x4d) : passive ? Muted : selected ? Ink : Ink2, Math.Max(12, box.Right - textX - (item.Part == Part.View || item.Part==Part.SavedView ? 30 : 8)));
            c.DrawText(text, new Wpf.Point(textX, box.Y + (box.Height - text.Height) / 2));
        }

        void PaintColors(Media.DrawingContext c, Frame f)
        {
            for (int i = 0; i < Swatches.Length; i++)
            {
                var color = Swatches[i]; var p = new Wpf.Point(27 + i * (f.PopupSize.Width - 54) / 4, 27);
                c.DrawEllipse(Fill(Rgb(color.R,color.G,color.B)), new Media.Pen(Fill(Glass), 3), p, 13, 13);
                c.DrawEllipse(null, new Media.Pen(Fill(color.ToArgb() == controller.StrokeColor.ToArgb() ? Accent : Rgb(0xdd,0xdf,0xe3)), 1.6), p, 15.5, 15.5);
            }
            var sv = SvBox(f);
            c.PushClip(new Media.RectangleGeometry(sv, 11, 11));
            c.DrawRectangle(new Media.LinearGradientBrush(Media.Colors.White, Hsv(hue,1,1), 0), null, sv);
            c.DrawRectangle(new Media.LinearGradientBrush(Media.Colors.Transparent, Media.Colors.Black, 90), null, sv);
            c.Pop();
            var marker = new Wpf.Point(sv.X + saturation * sv.Width, sv.Y + (1-value)*sv.Height);
            c.DrawEllipse(null, new Media.Pen(Fill(Shade, .45), 4), marker, 7, 7);
            c.DrawEllipse(null, new Media.Pen(Fill(Glass), 2), marker, 7, 7);
            var hueBox = HueBox(f); var gradient = new Media.LinearGradientBrush { StartPoint = new Wpf.Point(0,0), EndPoint = new Wpf.Point(1,0) };
            for (int i = 0; i <= 6; i++) gradient.GradientStops.Add(new Media.GradientStop(Hsv(i*60,1,1), i/6.0));
            c.DrawRoundedRectangle(gradient, null, hueBox, 8, 8);
            c.DrawEllipse(Fill(Glass), null, new Wpf.Point(hueBox.X + hue / 360 * hueBox.Width, hueBox.Y + 8), 9, 9);
            var colorNow = controller.StrokeColor;
            double hexY = f.PopupSize.Height - 38;
            c.DrawRoundedRectangle(Fill(Rgb(colorNow.R,colorNow.G,colorNow.B)), null, new Wpf.Rect(12,hexY,32,28), 8,8);
            c.DrawRoundedRectangle(Fill(Picture.Well), null, new Wpf.Rect(53,hexY,f.PopupSize.Width-65,28), 8,8);
            c.DrawText(Picture.Text(Hex,14,Wpf.FontWeights.SemiBold,Ink), new Wpf.Point(64,hexY+4));
        }

        void PaintViews(Media.DrawingContext c, Frame f)
        {
            c.DrawText(Picture.Text("+ Capture view…",12,Wpf.FontWeights.SemiBold,Ink),new Wpf.Point(13,12));
            c.DrawLine(new Media.Pen(Fill(Shade,.08),1), new Wpf.Point(10,36),new Wpf.Point(f.PopupSize.Width-10,36));
            int start = viewPage * f.PageSize, count = Math.Min(f.PageSize, f.Views.Count-start);
            if (count == 0) c.DrawText(Picture.Text("No named views yet",12,Wpf.FontWeights.Normal,Muted),new Wpf.Point(13,47));
            for (int i = 0; i < count; i++)
            {
                string name = f.Views[start+i]; bool selected = name == controller.CurrentViewName;
                if (selected) c.DrawRoundedRectangle(Fill(Picture.Selected),null,new Wpf.Rect(5,39+i*30,f.PopupSize.Width-10,28),8,8);
                c.DrawText(Picture.Text(name,12,Wpf.FontWeights.Medium,Ink2,f.PopupSize.Width-80),new Wpf.Point(13,45+i*30));
                c.DrawText(Picture.Text("✎",14,Wpf.FontWeights.Normal,Muted),new Wpf.Point(f.PopupSize.Width-54,44+i*30));
                c.DrawText(Picture.Text("×",16,Wpf.FontWeights.Normal,Muted),new Wpf.Point(f.PopupSize.Width-25,42+i*30));
            }
            if (f.Views.Count > f.PageSize) c.DrawText(Picture.Text("More views  →",12,Wpf.FontWeights.Medium,Accent),new Wpf.Point(13,45+count*30));
        }
    }
}
