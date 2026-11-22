using System;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
#if MAC // no WPF on a Mac: the same names, made with Eto (Mac/MacDrawing.cs)
using Media = RhinoSketchingTool.MacDrawing;
using Wpf = RhinoSketchingTool.MacDrawing;
#else
using Imaging = System.Windows.Media.Imaging;
using Media = System.Windows.Media;
using Wpf = System.Windows;
#endif

namespace RhinoSketchingTool
{
    /// <summary>
    /// Pictures of the interface that is painted over the viewport (the toolbar, the
    /// comment labels): drawn with WPF, which has the shapes and the text for it, and
    /// handed to Rhino as bitmaps. Colours are those of design/tokens/tokens.json.
    /// </summary>
    internal static class Picture
    {
        // Light and dark, as tokens.css and the prototype's data-ui="dark". Theme.Dark picks.
        public static Media.Color Ink => Theme.Dark ? Rgb(0xee, 0xf0, 0xf3) : Rgb(0x15, 0x17, 0x1c);
        public static Media.Color Ink2 => Theme.Dark ? Rgb(0xb9, 0xbe, 0xc8) : Rgb(0x4a, 0x50, 0x5d);
        public static Media.Color Muted => Theme.Dark ? Rgb(0x86, 0x8c, 0x97) : Rgb(0x87, 0x8d, 0x99);
        public static Media.Color Glass => Theme.Dark ? Rgb(0x2a, 0x2b, 0x30) : Rgb(0xff, 0xff, 0xff);   // --surface
        public static Media.Color Well => Theme.Dark ? Rgb(0x34, 0x35, 0x3b) : Rgb(0xf1, 0xf2, 0xf5);    // --well
        public static Media.Color SoftSel => Theme.Dark ? Rgb(0x45, 0x47, 0x4f) : Rgb(0xd9, 0xdd, 0xe4); // --soft-sel
        public static Media.Color Shade => Theme.Dark ? Rgb(255, 255, 255) : Rgb(16, 20, 28);          // hover / press / hairlines
        public static readonly Media.Color Accent = Rgb(0x97, 0x31, 0xb0);
        public static readonly Media.Color TipBack = Rgb(18, 20, 25);
        public static readonly Media.Color Danger = Rgb(0xe5, 0x48, 0x4d);  // --danger
        public const double LineShade = 0.14;

        public static Media.Color Rgb(byte red, byte green, byte blue) => Media.Color.FromRgb(red, green, blue);

        public static Media.Brush Fill(Media.Color colour, double opacity = 1) =>
            new Media.SolidColorBrush(Media.Color.FromArgb((byte)Math.Round(255 * opacity), colour.R, colour.G, colour.B));

        /// <summary>
        /// A picture of the given size (in pixels at 100 % display scaling), drawn by
        /// the caller in those same units, and made at the scale asked for.
        /// </summary>
        public static System.Drawing.Bitmap Render(double width, double height, double scale, Action<Media.DrawingContext> draw)
        {
#if MAC
            return Media.Canvas.Render(width, height, scale, draw);
#else
            var visual = new Media.DrawingVisual();
            using (Media.DrawingContext context = visual.RenderOpen())
            {
                context.PushTransform(new Media.ScaleTransform(scale, scale));
                draw(context);
            }
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
            var target = new Imaging.RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, Media.PixelFormats.Pbgra32);
            target.Render(visual);

            // WPF's pixels have their colour multiplied by their alpha. Rhino wants it plain.
            var pixels = new byte[pixelWidth * pixelHeight * 4];
            target.CopyPixels(pixels, pixelWidth * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                if (alpha == 0 || alpha == 255) continue;
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }
            var bitmap = new System.Drawing.Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, pixelWidth, pixelHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int row = 0; row < pixelHeight; row++)
                Marshal.Copy(pixels, row * pixelWidth * 4, data.Scan0 + row * data.Stride, pixelWidth * 4);
            bitmap.UnlockBits(data);
            return bitmap;
#endif
        }

        /// <summary>A white card with rounded corners: a soft shadow underneath, a hairline round it.</summary>
        public static void Card(Media.DrawingContext context, Wpf.Rect body, double radius)
        {
            for (int i = 5; i >= 1; i--)
            {
                Wpf.Rect shadow = body;
                shadow.Inflate(i, i * 0.8);
                shadow.Offset(0, 2.5);
                context.DrawRoundedRectangle(Fill(Rgb(16, 20, 28), Theme.Dark ? 0.09 : 0.028), null, shadow, radius + i, radius + i);
            }
            context.DrawRoundedRectangle(Fill(Glass), new Media.Pen(Fill(Shade, Theme.Dark ? 0.1 : 0.08), 1), body, radius, radius);
        }

        /// <summary>A tooltip: the prototype's .tip-pop, a dark label with white text.</summary>
        public static void Tip(Media.DrawingContext context, string text, Wpf.Point anchor, bool above)
        {
            var label = Text(text, 11.5, Wpf.FontWeights.Medium, Rgb(255, 255, 255));
            double width = label.WidthIncludingTrailingWhitespace + 18, height = label.Height + 10;
            var box = new Wpf.Rect(anchor.X - width / 2, above ? anchor.Y - height : anchor.Y, width, height);
            context.DrawRoundedRectangle(Fill(Rgb(0, 0, 0), 0.12), null, new Wpf.Rect(box.X, box.Y + 2, box.Width, box.Height), 8, 8);
            context.DrawRoundedRectangle(Fill(TipBack, 0.92), null, box, 8, 8);
            context.DrawText(label, new Wpf.Point(box.X + 9, box.Y + 5));
        }

        /// <summary>
        /// The prototype's hand-drawn tick checkbox (.ck): a 16 px box, and when on, a
        /// graphite-and-violet tick that sweeps out past its corner, with a lighter second pass.
        /// </summary>
        public static void Tick(Media.DrawingContext context, Wpf.Point topLeft, bool on)
        {
            var box = new Wpf.Rect(topLeft.X, topLeft.Y, 16, 16);
            context.DrawRoundedRectangle(Fill(Glass), new Media.Pen(Fill(Shade, LineShade), 1.5), box, 4, 4);
            if (!on) return;
            // The SVG is 25 px square, placed at (-3, -9) from the box.
            context.PushTransform(new Media.TranslateTransform(topLeft.X - 3, topLeft.Y - 9));
            context.PushTransform(new Media.ScaleTransform(25 / 24.0, 25 / 24.0));
            var a = new Media.Pen(Fill(Accent), 2.3) { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round, LineJoin = Media.PenLineJoin.Round };
            var b = new Media.Pen(Fill(Accent, 0.35), 1.1) { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round, LineJoin = Media.PenLineJoin.Round };
            context.DrawGeometry(null, a, Media.Geometry.Parse("M4.2 13.4c1.5 1 2.7 2.5 3.7 4.5.4.7 1 .7 1.3 0 2.4-5.3 6.3-10 11.9-13.9"));
            context.DrawGeometry(null, b, Media.Geometry.Parse("M5 12.8c1.3 1.2 2.4 2.6 3.2 4.3 2.6-5.2 6.4-9.7 11.6-13.3"));
            context.Pop();
            context.Pop();
        }

        /// <summary>One line of text in the interface's font, cut short with an ellipsis if it is wider than allowed.</summary>
        public static Media.FormattedText Text(string text, double size, Wpf.FontWeight weight, Media.Color colour, double maxWidth = 0)
        {
#if MAC
            return new Media.FormattedText(text, size, weight, colour, maxWidth); // in the Mac's own system font
#else
            var typeface = new Media.Typeface(new Media.FontFamily("Segoe UI"), Wpf.FontStyles.Normal, weight, Wpf.FontStretches.Normal);
            var formatted = new Media.FormattedText(text, CultureInfo.CurrentUICulture, Wpf.FlowDirection.LeftToRight, typeface, size, Fill(colour), 1.0);
            if (maxWidth > 0)
            {
                formatted.MaxTextWidth = maxWidth;
                formatted.MaxLineCount = 1;
                formatted.Trimming = Wpf.TextTrimming.CharacterEllipsis;
            }
            return formatted;
#endif
        }
    }
}
