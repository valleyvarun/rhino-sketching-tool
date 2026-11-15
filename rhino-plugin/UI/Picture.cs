using System;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Imaging = System.Windows.Media.Imaging;
using Media = System.Windows.Media;
using Wpf = System.Windows;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Pictures of the interface that is painted over the viewport (the toolbar, the
    /// comment labels): drawn with WPF, which has the shapes and the text for it, and
    /// handed to Rhino as bitmaps. Colours are those of design/tokens/tokens.json.
    /// </summary>
    internal static class Picture
    {
        public static readonly Media.Color Ink = Rgb(0x15, 0x17, 0x1c), Ink2 = Rgb(0x4a, 0x50, 0x5d), Muted = Rgb(0x87, 0x8d, 0x99);
        public static readonly Media.Color Glass = Rgb(0xff, 0xff, 0xff), Accent = Rgb(0x97, 0x31, 0xb0), Shade = Rgb(16, 20, 28);
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
        }

        /// <summary>A white card with rounded corners: a soft shadow underneath, a hairline round it.</summary>
        public static void Card(Media.DrawingContext context, Wpf.Rect body, double radius)
        {
            for (int i = 5; i >= 1; i--)
            {
                Wpf.Rect shadow = body;
                shadow.Inflate(i, i * 0.8);
                shadow.Offset(0, 2.5);
                context.DrawRoundedRectangle(Fill(Shade, 0.028), null, shadow, radius + i, radius + i);
            }
            context.DrawRoundedRectangle(Fill(Glass), new Media.Pen(Fill(Shade, LineShade), 1), body, radius, radius);
        }

        /// <summary>One line of text in the interface's font, cut short with an ellipsis if it is wider than allowed.</summary>
        public static Media.FormattedText Text(string text, double size, Wpf.FontWeight weight, Media.Color colour, double maxWidth = 0)
        {
            var typeface = new Media.Typeface(new Media.FontFamily("Segoe UI"), Wpf.FontStyles.Normal, weight, Wpf.FontStretches.Normal);
            var formatted = new Media.FormattedText(text, CultureInfo.CurrentUICulture, Wpf.FlowDirection.LeftToRight, typeface, size, Fill(colour), 1.0);
            if (maxWidth > 0)
            {
                formatted.MaxTextWidth = maxWidth;
                formatted.MaxLineCount = 1;
                formatted.Trimming = Wpf.TextTrimming.CharacterEllipsis;
            }
            return formatted;
        }
    }
}
