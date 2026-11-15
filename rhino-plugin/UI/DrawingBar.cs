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

        Frame OptionsFrame(RhinoView view, double fittedScale = 0)
        {
            double scale = fittedScale > 0 ? fittedScale : Scale(view);
            var viewport = view.ActiveViewport.Size;
            Layout(out Wpf.Size toolSize);
            double availableWidth = Math.Max(100, viewport.Width / scale - 2 * Inset);
            double viewWidth = Math.Min(180, LabelWidth(ViewLabel) + 35);
            double[] widths = { LabelWidth("Object") + 10, LabelWidth("Plane") + 10,
                LabelWidth("3D cursor") + 10, viewWidth, 28, LabelWidth("Shapes") + 31 };
            double preferred = 20; // outer padding and five compact gaps
            foreach (double itemWidth in widths) preferred += itemWidth;
            // Side docks use a separate horizontal strip at the viewport bottom.
            // Fit its scale on narrow viewports instead of wrapping it into a panel.
            double width = Upright ? preferred : Math.Min(preferred, availableWidth);
            var items = new List<Item>();
            double x = 5, y = 5;
            Part[] parts = { Part.Object, Part.Plane, Part.Cursor, Part.View, Part.Color, Part.Snap };
            for (int i = 0; i < parts.Length; i++)
            {
                double length = Math.Min(width - 10, widths[i]);
                if (x > 5 && x + length > width - 5) { x = 5; y += RowHeight; }
                items.Add(new Item { Part = parts[i], Box = new Wpf.Rect(x, y, length, 28) });
                x += length + 2;
            }
            var optionSize = new Wpf.Size(width, y + 33);
            bool beside = !Upright && toolSize.Width + BarGap + width <= availableWidth;
            var pair = collapsed ? toolSize : beside
                ? new Wpf.Size(toolSize.Width + BarGap + width, Math.Max(toolSize.Height, optionSize.Height))
                : new Wpf.Size(Math.Max(toolSize.Width, width), toolSize.Height + BarGap + optionSize.Height);
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
            if (Upright)
            {
                double bottomSpace = collapsed ? 0 : (optionSize.Height + BarGap) * scale;
                frame.ToolOrigin = new ScreenPoint(
                    (int)Math.Round(dock == ToolbarDock.Right ? viewport.Width - (Inset + toolSize.Width) * scale : Inset * scale),
                    (int)Math.Round(Math.Max(Inset * scale, (viewport.Height - bottomSpace - toolSize.Height * scale) / 2)));
                frame.OptionOrigin = new ScreenPoint(
                    (int)Math.Round((viewport.Width - optionSize.Width * scale) / 2),
                    (int)Math.Round(viewport.Height - (Inset + optionSize.Height) * scale));
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
            if (collapsed) return Part.None;
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
                            controller.RestoreView(f.Views[viewPage * f.PageSize + row]);
                            SetDrawMethod(DrawMethod.View);
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
                if (saved != null) SetDrawMethod(DrawMethod.View);
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
        string ViewLabel => "View: " + (controller.CurrentViewName ?? "Current");
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
            if (collapsed) return;
            if (optionsPopup == OptionsPopup.Colors && pickerArgb != controller.StrokeColor.ToArgb()) ReadColor();
            string signature = f.OptionsSize + "|" + f.Scale + "|" + controller.Method + "|" + controller.CurrentViewName + "|" + Hex + "|" + controller.SnapShapes + "|" + hover + "|" + pressed;
            if (optionsPicture == null || signature != optionsSignature)
            {
                optionsPicture?.Dispose();
                using (var bitmap = RenderOptions(f)) optionsPicture = new DisplayBitmap(bitmap);
                optionsSignature = signature;
            }
            int margin = (int)(Margin * f.Scale);
            e.Display.DrawBitmap(optionsPicture, f.OptionOrigin.X - margin, f.OptionOrigin.Y - margin);
            if (optionsPopup == OptionsPopup.None) return;
            signature = optionsPopup + "|" + f.PopupSize + "|" + f.Scale + "|" + Hex + "|" + hue + "|" + saturation + "|" + value + "|" + viewPage + "|" + controller.CurrentViewName + "|" + string.Join("\n", f.Views);
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
            Picture.Card(c, new Wpf.Rect(f.OptionsSize), 11);
            foreach (var item in f.Items) PaintOption(c, item);
            c.Pop();
        });

        System.Drawing.Bitmap RenderPopup(Frame f) => Picture.Render(f.PopupSize.Width + Margin * 2, f.PopupSize.Height + Margin * 2, f.Scale, c =>
        {
            c.PushTransform(new Media.TranslateTransform(Margin, Margin));
            Picture.Card(c, new Wpf.Rect(f.PopupSize), 14);
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
            bool selected = item.Part == Part.Object && controller.Method == DrawMethod.Surface || item.Part == Part.Plane && controller.Method == DrawMethod.Plane || item.Part == Part.Cursor && controller.Method == DrawMethod.Cursor || item.Part == Part.View && controller.Method == DrawMethod.View;
            c.DrawRoundedRectangle(Fill(selected ? Rgb(0xd9,0xdd,0xe4) : Rgb(0xf1,0xf2,0xf5)), null, box, 8, 8);
            bool arrowHover = item.Part == Part.View && (hover == Part.ViewList || pressed == Part.ViewList);
            if (hover == item.Part || pressed == item.Part || arrowHover) c.DrawRoundedRectangle(Fill(Shade, pressed == item.Part || pressed == Part.ViewList && item.Part == Part.View ? PressShade : HoverShade), null, box, 8, 8);
            string label = item.Part == Part.Object ? "Object" : item.Part == Part.Plane ? "Plane" : item.Part == Part.Cursor ? "3D cursor" : item.Part == Part.View ? ViewLabel : item.Part == Part.Snap ? "Shapes" : "";
            double textX = box.X + 5;
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
            if (item.Part == Part.View) PaintIcon(c, Icons.Chevron, new Wpf.Point(box.Right - 14, box.Y+14), 20, arrowHover ? Ink : Ink2, 1, 0);
            var text = Picture.Text(label, 11.5, Wpf.FontWeights.Medium, selected ? Ink : Ink2, Math.Max(12, box.Right - textX - (item.Part == Part.View ? 30 : 5)));
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
            c.DrawRoundedRectangle(Fill(Rgb(0xf1,0xf2,0xf5)), null, new Wpf.Rect(53,hexY,f.PopupSize.Width-65,28), 8,8);
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
                if (selected) c.DrawRoundedRectangle(Fill(Rgb(0xd9,0xdd,0xe4)),null,new Wpf.Rect(5,39+i*30,f.PopupSize.Width-10,28),8,8);
                c.DrawText(Picture.Text(name,12,Wpf.FontWeights.Medium,Ink2,f.PopupSize.Width-28),new Wpf.Point(13,45+i*30));
            }
            if (f.Views.Count > f.PageSize) c.DrawText(Picture.Text("More views  →",12,Wpf.FontWeights.Medium,Accent),new Wpf.Point(13,45+count*30));
        }
    }
}
