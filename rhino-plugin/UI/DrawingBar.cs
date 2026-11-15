using System;
using System.Collections.Generic;
using System.Globalization;
using Eto.Forms;
using Rhino;
using Rhino.Display;
using Media = System.Windows.Media;
using Wpf = System.Windows;
using ScreenPoint = System.Drawing.Point;
using Color = System.Drawing.Color;

namespace RhinoSketchingTool
{
    // The option row under the tool dock: it changes with the tool, as the prototype's
    // #subbar (design/prototype/index.html, renderSubbar):
    //   Pen       Object | Plane    colour    Snap shapes (hand-drawn tick)
    //   Eraser    Whole sketch | Part
    //   Comments  one line of help
    //   Views     + Save view    a chip per Rhino Named View (click: go there, x: delete)
    //   Select    no row
    // It uses the same viewport coordinates for painting and hit testing, including when
    // DPI, viewport size, or dock orientation changes.
    internal sealed partial class Toolbar
    {
        enum OptionsPopup { None, Colors }
        OptionsPopup optionsPopup;
        DisplayBitmap optionsPicture, popupPicture;
        string optionsSignature, popupSignature;
        double hue = 220, saturation = .15, value = .15;
        int pickerArgb;
        int chipPage;                  // which page of saved views the row shows
        int armedChip = -1;            // the view chip whose x was clicked once: a second click deletes
        int armedAt;                   // when (Environment.TickCount)
        const double BarGap = 8, RowHeight = 44, PopupWidth = 284, MaxChips = 8;
        const double SegPad = 3, GroupGap = 6, ButtonHeight = 30, SegButtonHeight = 28;
        static readonly Color[] Swatches = { Color.FromArgb(35,38,44), Color.FromArgb(151,49,176), Color.FromArgb(211,46,39), Color.FromArgb(17,132,91), Color.FromArgb(32,94,211) };

        sealed class Frame
        {
            public double Scale;
            public ScreenPoint ToolOrigin, OptionOrigin, PopupOrigin;
            public Wpf.Size OptionsSize, PopupSize;
            public List<Item> Items;
            public List<Wpf.Rect> Wells;     // the grey wells behind segmented buttons
            public IReadOnlyList<string> Views;
            public bool HasOptions;
        }

        // What the row holds for the tool in use. Each group is laid out together; a
        // segmented group sits in one grey well.
        sealed class Group { public bool Seg; public List<Part> Parts = new List<Part>(); }

        List<Group> OptionGroups(IReadOnlyList<string> views)
        {
            var groups = new List<Group>();
            switch (controller.Tool)
            {
                case SketchTool.Draw:
                    groups.Add(new Group { Seg = true, Parts = { Part.Object, Part.Plane } });
                    groups.Add(new Group { Parts = { Part.Color } });
                    groups.Add(new Group { Parts = { Part.Snap } });
                    break;
                case SketchTool.Erase:
                    groups.Add(new Group { Seg = true, Parts = { Part.EraseWhole, Part.ErasePart } });
                    break;
                case SketchTool.Comment:
                    groups.Add(new Group { Parts = { Part.Hint } });
                    break;
                case SketchTool.Views:
                    groups.Add(new Group { Parts = { Part.SaveView } });
                    var chips = new Group();
                    int per = ChipsPerPage(views);
                    for (int i = 0; i < per && ChipIndex(Part.Chip0 + i, views) < views.Count; i++) chips.Parts.Add(Part.Chip0 + i);
                    if (views.Count > MaxChips) chips.Parts.Add(Part.ChipMore);
                    if (chips.Parts.Count > 0) groups.Add(chips);
                    break;
            }
            return groups;
        }

        string HintText => "Click anywhere to comment";

        // Up to eight chips; with more views, seven a page and a "More" chip that turns the page.
        static int ChipsPerPage(IReadOnlyList<string> views) => views.Count > MaxChips ? (int)MaxChips - 1 : (int)MaxChips;
        int ChipIndex(Part chip, IReadOnlyList<string> views)
        {
            int per = ChipsPerPage(views), pages = Math.Max(1, (views.Count + per - 1) / per);
            if (chipPage >= pages) chipPage = 0;
            return chipPage * per + (chip - Part.Chip0);
        }

        double OptionWidth(Part part, IReadOnlyList<string> views)
        {
            switch (part)
            {
                case Part.Object: return 9 + 16 + 6 + LabelWidth("Object") + 11;
                case Part.Plane: return 9 + 16 + 6 + LabelWidth("Plane") + 11;
                case Part.EraseWhole: return 11 + LabelWidth("Whole sketch") + 11;
                case Part.ErasePart: return 11 + LabelWidth("Part") + 11;
                case Part.Color: return 30;
                case Part.Snap: return 11 + 16 + 7 + LabelWidth("Snap shapes") + 11;
                case Part.Hint: return 8 + LabelWidth(HintText) + 6;
                case Part.SaveView: return 11 + 15 + 6 + LabelWidth("Save view") + 11;
                default:
                    if (part >= Part.Chip0 && part <= Part.Chip7)
                    {
                        int index = ChipIndex(part, views);
                        string name = index < views.Count ? views[index] : "";
                        return Math.Min(160, 10 + LabelWidth(name) + 4) + 24;
                    }
                    if (part == Part.ChipMore) return 11 + LabelWidth("More") + 11;
                    return 0;
            }
        }

        Frame OptionsFrame(RhinoView view, double fittedScale = 0)
        {
            double scale = fittedScale > 0 ? fittedScale : Scale(view);
            var viewport = view.ActiveViewport.Size;
            Layout(out Wpf.Size toolSize);
            double availableWidth = Math.Max(100, viewport.Width / scale - 2 * Inset);
            IReadOnlyList<string> views = controller.NamedViewNames;
            var groups = collapsed ? new List<Group>() : OptionGroups(views);

            double preferred = 10;
            foreach (Group g in groups)
            {
                if (preferred > 10) preferred += GroupGap;
                if (g.Seg) preferred += 2 * SegPad + (g.Parts.Count - 1) * 2;
                else preferred += (g.Parts.Count - 1) * GroupGap;
                foreach (Part p in g.Parts) preferred += OptionWidth(p, views);
            }
            double width = Upright ? Math.Min(preferred, availableWidth) : Math.Min(preferred, availableWidth);

            var items = new List<Item>();
            var wells = new List<Wpf.Rect>();
            double x = 5, y = 5;
            foreach (Group g in groups)
            {
                double groupWidth = g.Seg ? 2 * SegPad + (g.Parts.Count - 1) * 2 : (g.Parts.Count - 1) * GroupGap;
                foreach (Part p in g.Parts) groupWidth += OptionWidth(p, views);
                if (x > 5) x += GroupGap;
                if (x > 5 && x + groupWidth > width - 5) { x = 5; y += RowHeight - 5; }
                if (g.Seg)
                {
                    wells.Add(new Wpf.Rect(x, y, Math.Min(groupWidth, width - 10), SegButtonHeight + 2 * SegPad));
                    double bx = x + SegPad;
                    foreach (Part p in g.Parts)
                    {
                        double w = OptionWidth(p, views);
                        items.Add(new Item { Part = p, Box = new Wpf.Rect(bx, y + SegPad, w, SegButtonHeight) });
                        bx += w + 2;
                    }
                }
                else
                {
                    double bx = x;
                    foreach (Part p in g.Parts)
                    {
                        double w = Math.Min(OptionWidth(p, views), width - 10);
                        if (bx > x && bx + w > width - 5) { bx = 5; y += RowHeight - 5; }
                        items.Add(new Item { Part = p, Box = new Wpf.Rect(bx, y + 2, w, ButtonHeight) });
                        bx += w + GroupGap;
                    }
                    groupWidth = bx - GroupGap - x;
                }
                x += groupWidth;
            }
            bool hasOptions = items.Count > 0;
            var optionSize = hasOptions ? new Wpf.Size(width, y + SegButtonHeight + 2 * SegPad + 5) : new Wpf.Size(0, 0);
            var pair = collapsed || !hasOptions ? toolSize
                : Upright ? new Wpf.Size(toolSize.Width + BarGap + optionSize.Width, Math.Max(toolSize.Height, optionSize.Height))
                : new Wpf.Size(Math.Max(toolSize.Width, width), toolSize.Height + BarGap + optionSize.Height);
            // Rhino's four-view layout can be quite short at high DPI. Fit the entire
            // dock pair, then use the same scale for rendering and pointer coordinates.
            double neededHeight = Math.Max(pair.Height, optionsPopup == OptionsPopup.Colors ? 180 : 0);
            double fitScale = Math.Min(viewport.Width / (pair.Width + Inset * 2), viewport.Height / (neededHeight + Inset * 2));
            if (fitScale + .001 < scale && scale > .1)
                return OptionsFrame(view, Math.Max(.1, fitScale));
            var origin = Origin(viewport, pair, scale);
            origin.X = Math.Max(0, Math.Min(origin.X, (int)Math.Max(0, viewport.Width - pair.Width * scale - Inset * scale)));
            origin.Y = Math.Max(0, Math.Min(origin.Y, (int)Math.Max(0, viewport.Height - pair.Height * scale - Inset * scale)));
            bool bottomDock = dock == ToolbarDock.Bottom || dock == ToolbarDock.BottomLeft || dock == ToolbarDock.BottomRight;
            // Centre the narrower of the two over the other, as the prototype does.
            double toolX = (pair.Width - toolSize.Width) / 2, optionX = (pair.Width - optionSize.Width) / 2;
            if (dock == ToolbarDock.TopLeft || dock == ToolbarDock.BottomLeft) { toolX = 0; optionX = 0; }
            if (dock == ToolbarDock.TopRight || dock == ToolbarDock.BottomRight) { toolX = pair.Width - toolSize.Width; optionX = pair.Width - optionSize.Width; }
            var frame = new Frame
            {
                Scale = scale, Items = items, Wells = wells, OptionsSize = optionSize, HasOptions = hasOptions, Views = views,
                // Docked at the bottom, the option row sits above the toolbar.
                ToolOrigin = new ScreenPoint(origin.X + (int)(toolX * scale), origin.Y + (int)((bottomDock && hasOptions ? optionSize.Height + BarGap : 0) * scale)),
                OptionOrigin = new ScreenPoint(origin.X + (int)(optionX * scale), origin.Y + (int)((bottomDock ? 0 : toolSize.Height + BarGap) * scale)),
            };
            if (Upright)
            {
                // On a side dock the row stands level with the tool that opened it.
                double toolY = Math.Max(Inset * scale, (viewport.Height - toolSize.Height * scale) / 2);
                frame.ToolOrigin = new ScreenPoint(
                    (int)Math.Round(dock == ToolbarDock.Right ? viewport.Width - (Inset + toolSize.Width) * scale : Inset * scale),
                    (int)Math.Round(toolY));
                double activeY = 0;
                foreach (Item item in Layout(out _)) if (item.Part == ActivePart) activeY = item.Box.Top + item.Box.Height / 2;
                double rowY = toolY + (activeY - optionSize.Height / 2) * scale;
                rowY = Math.Max(Inset * scale, Math.Min(rowY, viewport.Height - (Inset + optionSize.Height) * scale));
                double rowX = dock == ToolbarDock.Right
                    ? frame.ToolOrigin.X - (BarGap + optionSize.Width) * scale
                    : frame.ToolOrigin.X + (toolSize.Width + BarGap) * scale;
                frame.OptionOrigin = new ScreenPoint((int)Math.Round(Math.Max(0, rowX)), (int)Math.Round(rowY));
            }
            double popupWidth = Math.Min(PopupWidth, availableWidth);
            double popupHeight = Math.Min(302, Math.Max(180, viewport.Height / scale - 2 * Inset));
            frame.PopupSize = new Wpf.Size(popupWidth, popupHeight);
            var anchor = items.Find(i => i.Part == Part.Color).Box;
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
            if (collapsed) return Part.None;
            var p = Local(point, f.PopupOrigin, f.Scale);
            if (optionsPopup != OptionsPopup.None && new Wpf.Rect(f.PopupSize).Contains(p))
            {
                if (SvBox(f).Contains(p)) return Part.Saturation;
                if (HueBox(f).Contains(p)) return Part.Hue;
                if (p.Y >= f.PopupSize.Height - 38) return Part.Hex;
                if (p.Y < 48)
                    for (int i = 0; i < 5; i++)
                        if (Math.Abs(p.X - (27 + i * (f.PopupSize.Width - 54) / 4)) <= 18) return Part.Swatch0 + i;
                return Part.Popup;
            }
            if (!f.HasOptions) return Part.None;
            p = Local(point, f.OptionOrigin, f.Scale);
            if (!new Wpf.Rect(f.OptionsSize).Contains(p)) return Part.None;
            foreach (var item in f.Items)
                if (item.Box.Contains(p))
                {
                    if (item.Part >= Part.Chip0 && item.Part <= Part.Chip7 && p.X >= item.Box.Right - 24)
                        return Part.ChipX0 + (item.Part - Part.Chip0);
                    return item.Part == Part.Hint ? Part.Bar : item.Part;
                }
            return Part.Bar;
        }

        void CloseOptionsPopup() { optionsPopup = OptionsPopup.None; popupSignature = null; }

        bool ClickOptions(Part part, RhinoView view, ScreenPoint point)
        {
            if (part >= Part.Chip0 && part <= Part.Chip7)
            {
                var views = controller.NamedViewNames;
                int i = ChipIndex(part, views);
                armedChip = -1;
                if (i < views.Count) controller.RestoreView(views[i]); // the camera only
                return true;
            }
            if (part >= Part.ChipX0 && part <= Part.ChipX7)
            {
                // Deleting a Rhino Named View takes two clicks, as deleting does in Layers.
                int slot = part - Part.ChipX0;
                if (armedChip != slot || unchecked(Environment.TickCount - armedAt) > 2500) { armedChip = slot; armedAt = Environment.TickCount; return true; }
                armedChip = -1;
                var views = controller.NamedViewNames;
                int i = ChipIndex(Part.Chip0 + slot, views);
                if (i < views.Count) controller.DeleteView(views[i]);
                return true;
            }
            if (part == Part.ChipMore) { chipPage++; armedChip = -1; ChipIndex(Part.Chip0, controller.NamedViewNames); return true; }
            switch (part)
            {
                case Part.Object: SetDrawMethod(DrawMethod.Surface); break;
                case Part.Plane: SetDrawMethod(DrawMethod.Plane); break;
                case Part.EraseWhole: controller.Erase = EraseMode.Stroke; break;
                case Part.ErasePart: controller.Erase = EraseMode.Part; break;
                case Part.Snap: controller.SnapShapes = !controller.SnapShapes; break;
                case Part.SaveView: CaptureNamedView(); break;
                case Part.Color:
                    optionsPopup = optionsPopup == OptionsPopup.Colors ? OptionsPopup.None : OptionsPopup.Colors;
                    ReadColor(); break;
                case Part.Saturation: case Part.Hue: break;
                case Part.Hex: EditHex(); break;
                case Part.Swatch0: case Part.Swatch1: case Part.Swatch2: case Part.Swatch3: case Part.Swatch4:
                    controller.StrokeColor = Swatches[part - Part.Swatch0]; ReadColor(); break;
                case Part.Popup: break;
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

        // Saves the camera as a Rhino Named View. Nothing else changes: not the tool, not the sketches.
        void CaptureNamedView()
        {
            CloseOptionsPopup();
            // Dialogs are opened after the viewport mouse callback returns.
            Application.Instance.AsyncInvoke(() =>
            {
                string name = AskText("Save view", "Name this view", "View " + (controller.NamedViewNames.Count + 1));
                if (name == null) return;
                controller.CaptureView(name);
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
                string text = AskText("Sketch colour", "Hex colour (#RRGGBB)", Hex);
                if (text == null) return;
                text = text.TrimStart('#');
                if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                { controller.StrokeColor = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); ReadColor(); }
                else MessageBox.Show("Enter six hexadecimal digits, for example #23262C.", "Sketch colour");
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
        static double LabelWidth(string label) => Math.Ceiling(Picture.Text(label, 12, Wpf.FontWeights.Medium, Ink2).WidthIncludingTrailingWhitespace);
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
            if (collapsed || !f.HasOptions) return;
            if (optionsPopup == OptionsPopup.Colors && pickerArgb != controller.StrokeColor.ToArgb()) ReadColor();
            if (armedChip >= 0 && unchecked(Environment.TickCount - armedAt) > 2500) armedChip = -1;
            string signature = f.OptionsSize + "|" + f.Scale + "|" + chipPage + "|" + armedChip + "|" + controller.Tool + "|" + controller.Method + "|" + controller.Erase + "|" + controller.CurrentViewName + "|" + Hex + "|" + controller.SnapShapes + "|" + hover + "|" + pressed + "|" + Theme.Dark + "|" + string.Join("\n", f.Views);
            if (optionsPicture == null || signature != optionsSignature)
            {
                optionsPicture?.Dispose();
                using (var bitmap = RenderOptions(f)) optionsPicture = new DisplayBitmap(bitmap);
                optionsSignature = signature;
            }
            int margin = (int)(Margin * f.Scale);
            e.Display.DrawBitmap(optionsPicture, f.OptionOrigin.X - margin, f.OptionOrigin.Y - margin);
            if (optionsPopup == OptionsPopup.None || controller.Tool != SketchTool.Draw) return;
            signature = optionsPopup + "|" + f.PopupSize + "|" + f.Scale + "|" + Hex + "|" + hue + "|" + saturation + "|" + value + "|" + Theme.Dark;
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
            foreach (var well in f.Wells) c.DrawRoundedRectangle(Fill(Well), null, well, 12, 12);
            foreach (var item in f.Items) PaintOption(c, item, f);
            c.Pop();
        });

        System.Drawing.Bitmap RenderPopup(Frame f) => Picture.Render(f.PopupSize.Width + Margin * 2, f.PopupSize.Height + Margin * 2, f.Scale, c =>
        {
            c.PushTransform(new Media.TranslateTransform(Margin, Margin));
            Picture.Card(c, new Wpf.Rect(f.PopupSize), 14);
            PaintColors(c, f);
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
                if (frame.HasOptions)
                    using (var bitmap = RenderOptions(frame)) bitmap.Save(System.IO.Path.Combine(folder, "drawing-bar.png"), System.Drawing.Imaging.ImageFormat.Png);
                using (var bitmap = RenderPopup(frame)) bitmap.Save(System.IO.Path.Combine(folder, "color-picker.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            finally { optionsPopup = previous; }
        }

        bool Selected(Part part)
        {
            switch (part)
            {
                case Part.Object: return controller.Method == DrawMethod.Surface;
                case Part.Plane: return controller.Method != DrawMethod.Surface;
                case Part.EraseWhole: return controller.Erase == EraseMode.Stroke;
                case Part.ErasePart: return controller.Erase == EraseMode.Part;
                default: return false;
            }
        }

        void PaintOption(Media.DrawingContext c, Item item, Frame f)
        {
            var box = item.Box;
            Part part = item.Part;
            bool over = hover == part || pressed == part, down = pressed == part;
            var mid = new Wpf.Point(box.X + box.Width / 2, box.Y + box.Height / 2);

            if (part == Part.Color)
            {
                var color = controller.StrokeColor;
                if (over) c.DrawRoundedRectangle(Fill(Shade, down ? PressShade : HoverShade), null, box, 10, 10);
                c.DrawEllipse(Fill(Rgb(color.R, color.G, color.B)), new Media.Pen(Fill(Glass), 2), mid, 9, 9);
                c.DrawEllipse(null, new Media.Pen(Fill(Shade, LineShade), 1), mid, 10.5, 10.5);
                return;
            }
            if (part == Part.Hint)
            {
                var hint = Picture.Text(HintText, 12, Wpf.FontWeights.Medium, Muted, box.Width - 8);
                c.DrawText(hint, new Wpf.Point(box.X + 8, mid.Y - hint.Height / 2));
                return;
            }

            bool seg = part == Part.Object || part == Part.Plane || part == Part.EraseWhole || part == Part.ErasePart;
            bool chip = part >= Part.Chip0 && part <= Part.Chip7;
            if (part == Part.ChipMore)
            {
                c.DrawRoundedRectangle(Fill(Well), null, box, 10, 10);
                if (over) c.DrawRoundedRectangle(Fill(Shade, down ? PressShade : HoverShade), null, box, 10, 10);
                var more = Picture.Text("More", 12, Wpf.FontWeights.Medium, Ink2);
                c.DrawText(more, new Wpf.Point(box.X + 11, mid.Y - more.Height / 2));
                return;
            }
            if (seg)
            {
                // .seg button: the selected one gets the soft grey fill; hover only darkens the text.
                if (Selected(part)) c.DrawRoundedRectangle(Fill(SoftSel), null, box, 9, 9);
                else if (down) c.DrawRoundedRectangle(Fill(Shade, PressShade), null, box, 9, 9);
                var colour = Selected(part) || over ? Ink : Ink2;
                double tx = box.X + 11;
                if (part == Part.Object || part == Part.Plane)
                {
                    PaintIcon(c, part == Part.Object ? Icons.OnObject : Icons.OnPlane, new Wpf.Point(box.X + 9 + 8, mid.Y), 16, colour, 1, 0);
                    tx = box.X + 9 + 16 + 6;
                }
                string label = part == Part.Object ? "Object" : part == Part.Plane ? "Plane" : part == Part.EraseWhole ? "Whole sketch" : "Part";
                var text = Picture.Text(label, 12, Wpf.FontWeights.Medium, colour);
                c.DrawText(text, new Wpf.Point(tx, mid.Y - text.Height / 2));
                return;
            }

            // .sb-t: a well-coloured button, darker on hover. A view chip is the same, soft grey when it is the view on screen.
            int slot = chip ? part - Part.Chip0 : -1;
            int chipIndex = chip ? ChipIndex(part, f.Views) : -1;
            bool current = chip && chipIndex < f.Views.Count && f.Views[chipIndex] == controller.CurrentViewName;
            bool overX = chip && (hover == Part.ChipX0 + slot || pressed == Part.ChipX0 + slot);
            bool armed = chip && armedChip == slot;
            c.DrawRoundedRectangle(Fill(current ? SoftSel : Well), null, box, 10, 10);
            if (over || overX) c.DrawRoundedRectangle(Fill(Shade, down ? PressShade : HoverShade), null, box, 10, 10);

            if (part == Part.Snap)
            {
                Picture.Tick(c, new Wpf.Point(box.X + 11, mid.Y - 8), controller.SnapShapes);
                var text = Picture.Text("Snap shapes", 12, Wpf.FontWeights.Medium, Ink);
                c.DrawText(text, new Wpf.Point(box.X + 11 + 16 + 7, mid.Y - text.Height / 2));
                return;
            }
            if (part == Part.SaveView)
            {
                PaintIcon(c, Icons.Plus, new Wpf.Point(box.X + 11 + 7.5, mid.Y), 15, Ink, 1, 0);
                var text = Picture.Text("Save view", 12, Wpf.FontWeights.Medium, Ink);
                c.DrawText(text, new Wpf.Point(box.X + 11 + 15 + 6, mid.Y - text.Height / 2));
                return;
            }
            if (chip && chipIndex < f.Views.Count)
            {
                var text = Picture.Text(f.Views[chipIndex], 12, Wpf.FontWeights.Medium, Ink, box.Width - 34);
                c.DrawText(text, new Wpf.Point(box.X + 10, mid.Y - text.Height / 2));
                var x = new Wpf.Rect(box.Right - 26, box.Y + 3, 24, 24);
                if (armed) c.DrawRoundedRectangle(Fill(Picture.Danger, 0.14), null, x, 7, 7);
                else if (overX) c.DrawRoundedRectangle(Fill(Shade, HoverShade), null, x, 7, 7);
                PaintIcon(c, Icons.Close, new Wpf.Point(x.X + 12, x.Y + 12), 13, armed ? Picture.Danger : overX ? Ink : Muted, 1, 0);
            }
        }

        void PaintColors(Media.DrawingContext c, Frame f)
        {
            for (int i = 0; i < Swatches.Length; i++)
            {
                var color = Swatches[i]; var p = new Wpf.Point(27 + i * (f.PopupSize.Width - 54) / 4, 27);
                c.DrawEllipse(Fill(Rgb(color.R,color.G,color.B)), new Media.Pen(Fill(Glass), 3), p, 13, 13);
                c.DrawEllipse(null, new Media.Pen(color.ToArgb() == controller.StrokeColor.ToArgb() ? Fill(Accent) : Fill(Shade, LineShade), 1.6), p, 15.5, 15.5);
            }
            var sv = SvBox(f);
            c.PushClip(new Media.RectangleGeometry(sv, 11, 11));
            c.DrawRectangle(new Media.LinearGradientBrush(Media.Colors.White, Hsv(hue,1,1), 0), null, sv);
            c.DrawRectangle(new Media.LinearGradientBrush(Media.Colors.Transparent, Media.Colors.Black, 90), null, sv);
            c.Pop();
            var marker = new Wpf.Point(sv.X + saturation * sv.Width, sv.Y + (1-value)*sv.Height);
            c.DrawEllipse(null, new Media.Pen(Fill(Rgb(16,20,28), .45), 4), marker, 7, 7);
            c.DrawEllipse(null, new Media.Pen(Fill(Rgb(255,255,255)), 2), marker, 7, 7);
            var hueBox = HueBox(f); var gradient = new Media.LinearGradientBrush { StartPoint = new Wpf.Point(0,0), EndPoint = new Wpf.Point(1,0) };
            for (int i = 0; i <= 6; i++) gradient.GradientStops.Add(new Media.GradientStop(Hsv(i*60,1,1), i/6.0));
            c.DrawRoundedRectangle(gradient, null, hueBox, 8, 8);
            c.DrawEllipse(Fill(Rgb(255,255,255)), new Media.Pen(Fill(Rgb(16,20,28), .2), 1), new Wpf.Point(hueBox.X + hue / 360 * hueBox.Width, hueBox.Y + 8), 9, 9);
            var colorNow = controller.StrokeColor;
            double hexY = f.PopupSize.Height - 38;
            c.DrawRoundedRectangle(Fill(Rgb(colorNow.R,colorNow.G,colorNow.B)), null, new Wpf.Rect(12,hexY,32,28), 8,8);
            c.DrawRoundedRectangle(Fill(Well), null, new Wpf.Rect(53,hexY,f.PopupSize.Width-65,28), 8,8);
            c.DrawText(Picture.Text(Hex,13,Wpf.FontWeights.SemiBold,Ink), new Wpf.Point(64,hexY+5));
        }
    }
}
