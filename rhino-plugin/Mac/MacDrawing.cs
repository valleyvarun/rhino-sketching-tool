#if MAC
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using E = Eto.Drawing;

// Rhino for Mac has no WPF, which on Windows paints the pictures of the interface that sit
// over the viewport: the toolbar, its option row and lists, and the comment labels
// (Picture.cs, Toolbar.cs, DrawingBar.cs, Comments.cs).
//
// This is the small part of WPF's drawing that those files use, made with Eto.Drawing,
// which Rhino has on both systems. On a Mac the four files name these types through the
// same two aliases they name WPF's by on Windows (Media and Wpf), so the painting code
// itself is one and the same: only what is underneath differs. Names, arguments and
// behaviour follow WPF's wherever a file relies on them, and nothing here is meant to be
// more of WPF than that.
namespace RhinoSketchingTool.MacDrawing
{
    internal struct Point
    {
        public double X, Y;

        public Point(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    internal struct Size
    {
        public double Width, Height;

        public Size(double width, double height)
        {
            Width = width;
            Height = height;
        }

        // Pictures are remembered by what they show, their size included.
        public override string ToString() =>
            Width.ToString("R", CultureInfo.InvariantCulture) + "," + Height.ToString("R", CultureInfo.InvariantCulture);
    }

    internal struct Rect
    {
        public double X, Y, Width, Height;

        public Rect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public Rect(Size size) : this(0, 0, size.Width, size.Height) { }

        /// <summary>No rectangle at all: it holds no point.</summary>
        public static Rect Empty => new Rect(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

        public bool IsEmpty => Width < 0;

        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;

        /// <summary>Its edges count as inside.</summary>
        public bool Contains(Point point) =>
            !IsEmpty && point.X >= X && point.X <= X + Width && point.Y >= Y && point.Y <= Y + Height;

        /// <summary>Grows on every side: by width to the left and right, by height above and below.</summary>
        public void Inflate(double width, double height)
        {
            X -= width;
            Y -= height;
            Width += 2 * width;
            Height += 2 * height;
        }

        public void Offset(double x, double y)
        {
            X += x;
            Y += y;
        }
    }

    internal struct Color
    {
        public byte A, R, G, B;

        public static Color FromRgb(byte red, byte green, byte blue) => new Color { A = 255, R = red, G = green, B = blue };

        public static Color FromArgb(byte alpha, byte red, byte green, byte blue) => new Color { A = alpha, R = red, G = green, B = blue };

        internal E.Color ToEto(double opacity = 1) =>
            new E.Color(R / 255f, G / 255f, B / 255f, (float)Math.Max(0, Math.Min(1, A / 255.0 * opacity)));
    }

    internal static class Colors
    {
        public static readonly Color White = Color.FromRgb(255, 255, 255);
        public static readonly Color Black = Color.FromRgb(0, 0, 0);

        // Clear black, where WPF's is clear white. The one use is the colour picker's fade
        // to black, and fading from clear black is black all the way: what the picker means.
        public static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    }

    internal abstract class Brush
    {
    }

    internal sealed class SolidColorBrush : Brush
    {
        public readonly Color Color;

        public SolidColorBrush(Color color) => Color = color;
    }

    internal sealed class GradientStop
    {
        public readonly Color Color;
        public readonly double Offset; // 0 at the start of the gradient, 1 at its end

        public GradientStop(Color color, double offset)
        {
            Color = color;
            Offset = offset;
        }
    }

    /// <summary>A fade along a line. Its two ends are given as shares of the painted shape's box, as WPF has them.</summary>
    internal sealed class LinearGradientBrush : Brush
    {
        public Point StartPoint = new Point(0, 0), EndPoint = new Point(1, 0);
        public readonly List<GradientStop> GradientStops = new List<GradientStop>();

        public LinearGradientBrush() { }

        /// <summary>From one colour to another: left to right at 0 degrees, top to bottom at 90.</summary>
        public LinearGradientBrush(Color start, Color end, double angle)
        {
            double radians = angle * Math.PI / 180;
            EndPoint = new Point(Math.Cos(radians), Math.Sin(radians));
            GradientStops.Add(new GradientStop(start, 0));
            GradientStops.Add(new GradientStop(end, 1));
        }
    }

    internal enum PenLineCap { Flat, Square, Round }

    internal enum PenLineJoin { Miter, Bevel, Round }

    internal sealed class Pen
    {
        public readonly Brush Brush;
        public readonly double Thickness;
        public PenLineCap StartLineCap, EndLineCap; // one cap for both ends here: the start's
        public PenLineJoin LineJoin;

        public Pen(Brush brush, double thickness)
        {
            Brush = brush;
            Thickness = thickness;
        }
    }

    internal struct FontWeight
    {
        internal int Weight; // 400 is regular, 700 bold
    }

    internal static class FontWeights
    {
        public static FontWeight Normal => new FontWeight { Weight = 400 };
        public static FontWeight Medium => new FontWeight { Weight = 500 };
        public static FontWeight SemiBold => new FontWeight { Weight = 600 };
        public static FontWeight Bold => new FontWeight { Weight = 700 };
    }

    internal abstract class Transform
    {
        internal abstract void Apply(E.Graphics graphics);
    }

    internal sealed class TranslateTransform : Transform
    {
        readonly double x, y;

        public TranslateTransform(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        internal override void Apply(E.Graphics graphics) => graphics.TranslateTransform((float)x, (float)y);
    }

    internal sealed class ScaleTransform : Transform
    {
        readonly double x, y;

        public ScaleTransform(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        internal override void Apply(E.Graphics graphics) => graphics.ScaleTransform((float)x, (float)y);
    }

    /// <summary>A turn, clockwise in degrees, about a point.</summary>
    internal sealed class RotateTransform : Transform
    {
        readonly double angle, x, y;

        public RotateTransform(double angle, double centerX, double centerY)
        {
            this.angle = angle;
            x = centerX;
            y = centerY;
        }

        internal override void Apply(E.Graphics graphics)
        {
            if (angle == 0) return;
            graphics.TranslateTransform((float)x, (float)y);
            graphics.RotateTransform((float)angle);
            graphics.TranslateTransform((float)-x, (float)-y);
        }
    }

    /// <summary>An outline: the icons' paths, which are written as SVG writes them.</summary>
    internal class Geometry
    {
        internal enum Kind { Move, Line, Curve, Close }

        // One step of an outline. A curve goes to (X, Y) by way of two control points.
        internal struct Step
        {
            public Kind Kind;
            public double X1, Y1, X2, Y2, X, Y;
        }

        static readonly Dictionary<string, Geometry> parsed = new Dictionary<string, Geometry>();

        readonly List<Step> steps;
        E.GraphicsPath path;

        internal Geometry(List<Step> steps) => this.steps = steps;

        /// <summary>The outline that SVG path text describes ("M4 4l15 6z").</summary>
        public static Geometry Parse(string data)
        {
            if (!parsed.TryGetValue(data, out Geometry geometry)) parsed[data] = geometry = new Geometry(PathText.Read(data));
            return geometry;
        }

        /// <summary>Nothing to do: an outline here never changes once it is made.</summary>
        public void Freeze() { }

        // Made on first use and kept: an outline is drawn many times over.
        internal E.GraphicsPath Path => path ?? (path = Shapes.Build(steps));

        internal Rect Bounds
        {
            get
            {
                E.RectangleF box = Path.Bounds;
                return new Rect(box.X, box.Y, box.Width, box.Height);
            }
        }
    }

    /// <summary>A rectangle with rounded corners, as an outline: for clipping to.</summary>
    internal sealed class RectangleGeometry : Geometry
    {
        public RectangleGeometry(Rect box, double radiusX, double radiusY) : base(Shapes.RoundedRectangle(box, radiusX, radiusY)) { }
    }

    /// <summary>Reads SVG path text: every command of it, absolute and relative.</summary>
    internal static class PathText
    {
        const string Commands = "MmLlHhVvCcSsQqTtAaZz";

        public static List<Geometry.Step> Read(string text)
        {
            var steps = new List<Geometry.Step>();
            int at = 0;
            char command = '\0', before = '\0'; // this command, and the one before it, in capitals
            double x = 0, y = 0;                // where the outline has got to
            double startX = 0, startY = 0;      // where its present figure began
            double controlX = 0, controlY = 0;  // the last control point, which S and T mirror
            bool unmoved = false;               // a figure was closed, and nothing has said where the next begins

            while (More(text, ref at))
            {
                char next = text[at];
                if (Commands.IndexOf(next) >= 0)
                {
                    command = next;
                    at++;
                }
                else if (command == 'M') command = 'L'; // further pairs after a move are lines
                else if (command == 'm') command = 'l';
                else if (command == '\0' || command == 'Z' || command == 'z') throw new FormatException("Not path text: " + text);

                bool relative = char.IsLower(command);
                char kind = char.ToUpperInvariant(command);
                double fromX = relative ? x : 0, fromY = relative ? y : 0;

                if (kind == 'Z')
                {
                    steps.Add(new Geometry.Step { Kind = Geometry.Kind.Close });
                    x = startX;
                    y = startY;
                    unmoved = true;
                    before = kind;
                    continue;
                }
                if (kind == 'M')
                {
                    x = fromX + Number(text, ref at);
                    y = fromY + Number(text, ref at);
                    startX = x;
                    startY = y;
                    steps.Add(new Geometry.Step { Kind = Geometry.Kind.Move, X = x, Y = y });
                    unmoved = false;
                    before = kind;
                    continue;
                }

                // Anything drawn straight after a closed figure starts where that figure started.
                if (unmoved)
                {
                    steps.Add(new Geometry.Step { Kind = Geometry.Kind.Move, X = x, Y = y });
                    unmoved = false;
                }

                switch (kind)
                {
                    case 'L':
                        x = fromX + Number(text, ref at);
                        y = fromY + Number(text, ref at);
                        steps.Add(new Geometry.Step { Kind = Geometry.Kind.Line, X = x, Y = y });
                        break;
                    case 'H':
                        x = fromX + Number(text, ref at);
                        steps.Add(new Geometry.Step { Kind = Geometry.Kind.Line, X = x, Y = y });
                        break;
                    case 'V':
                        y = fromY + Number(text, ref at);
                        steps.Add(new Geometry.Step { Kind = Geometry.Kind.Line, X = x, Y = y });
                        break;
                    case 'C':
                    case 'S':
                    {
                        double x1, y1;
                        if (kind == 'C')
                        {
                            x1 = fromX + Number(text, ref at);
                            y1 = fromY + Number(text, ref at);
                        }
                        else
                        {
                            // The first control point mirrors the last one of the curve before.
                            bool curved = before == 'C' || before == 'S';
                            x1 = curved ? 2 * x - controlX : x;
                            y1 = curved ? 2 * y - controlY : y;
                        }
                        double x2 = fromX + Number(text, ref at), y2 = fromY + Number(text, ref at);
                        x = fromX + Number(text, ref at);
                        y = fromY + Number(text, ref at);
                        steps.Add(new Geometry.Step { Kind = Geometry.Kind.Curve, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X = x, Y = y });
                        controlX = x2;
                        controlY = y2;
                        break;
                    }
                    case 'Q':
                    case 'T':
                    {
                        // A curve with one control point, as the same curve with two.
                        double qx, qy;
                        if (kind == 'Q')
                        {
                            qx = fromX + Number(text, ref at);
                            qy = fromY + Number(text, ref at);
                        }
                        else
                        {
                            bool curved = before == 'Q' || before == 'T';
                            qx = curved ? 2 * x - controlX : x;
                            qy = curved ? 2 * y - controlY : y;
                        }
                        double endX = fromX + Number(text, ref at), endY = fromY + Number(text, ref at);
                        steps.Add(new Geometry.Step
                        {
                            Kind = Geometry.Kind.Curve,
                            X1 = x + 2.0 / 3 * (qx - x), Y1 = y + 2.0 / 3 * (qy - y),
                            X2 = endX + 2.0 / 3 * (qx - endX), Y2 = endY + 2.0 / 3 * (qy - endY),
                            X = endX, Y = endY,
                        });
                        x = endX;
                        y = endY;
                        controlX = qx;
                        controlY = qy;
                        break;
                    }
                    case 'A':
                    {
                        double radiusX = Number(text, ref at), radiusY = Number(text, ref at), turn = Number(text, ref at);
                        bool large = Flag(text, ref at), sweep = Flag(text, ref at);
                        double endX = fromX + Number(text, ref at), endY = fromY + Number(text, ref at);
                        Arc(steps, x, y, radiusX, radiusY, turn, large, sweep, endX, endY);
                        x = endX;
                        y = endY;
                        break;
                    }
                }
                before = kind;
            }
            return steps;
        }

        // True if there is more to read, having passed over any blanks and commas.
        static bool More(string text, ref int at)
        {
            while (at < text.Length && (char.IsWhiteSpace(text[at]) || text[at] == ',')) at++;
            return at < text.Length;
        }

        // Numbers may follow one another with nothing in between: "4.5.4-1" is 4.5, .4 and -1.
        static double Number(string text, ref int at)
        {
            if (!More(text, ref at)) throw new FormatException("Path text ends early: " + text);
            int start = at;
            if (text[at] == '+' || text[at] == '-') at++;
            bool digits = false, point = false;
            while (at < text.Length)
            {
                char c = text[at];
                if (c >= '0' && c <= '9') digits = true;
                else if (c == '.' && !point) point = true;
                else break;
                at++;
            }
            if (!digits) throw new FormatException("Not path text: " + text);
            if (at < text.Length && (text[at] == 'e' || text[at] == 'E'))
            {
                int end = at + 1;
                if (end < text.Length && (text[end] == '+' || text[end] == '-')) end++;
                if (end < text.Length && text[end] >= '0' && text[end] <= '9')
                {
                    while (end < text.Length && text[end] >= '0' && text[end] <= '9') end++;
                    at = end;
                }
            }
            return double.Parse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // An arc's two flags are single digits, which need not be set apart from what follows.
        static bool Flag(string text, ref int at)
        {
            if (!More(text, ref at) || (text[at] != '0' && text[at] != '1')) throw new FormatException("Not path text: " + text);
            return text[at++] == '1';
        }

        // Part of an ellipse from one point to another, as curves of no more than a quarter turn each.
        // (The arithmetic is the SVG specification's, "Conversion from endpoint to center parameterization".)
        static void Arc(List<Geometry.Step> steps, double x1, double y1, double radiusX, double radiusY, double turn, bool large, bool sweep, double x2, double y2)
        {
            if (x1 == x2 && y1 == y2) return;
            radiusX = Math.Abs(radiusX);
            radiusY = Math.Abs(radiusY);
            if (radiusX == 0 || radiusY == 0)
            {
                steps.Add(new Geometry.Step { Kind = Geometry.Kind.Line, X = x2, Y = y2 });
                return;
            }

            double phi = turn * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double halfX = (x1 - x2) / 2, halfY = (y1 - y2) / 2;
            double px = cos * halfX + sin * halfY, py = -sin * halfX + cos * halfY;

            // Radii too small to reach are grown until they just do.
            double reach = px * px / (radiusX * radiusX) + py * py / (radiusY * radiusY);
            if (reach > 1)
            {
                radiusX *= Math.Sqrt(reach);
                radiusY *= Math.Sqrt(reach);
            }

            double rx2 = radiusX * radiusX, ry2 = radiusY * radiusY;
            double under = rx2 * py * py + ry2 * px * px;
            double factor = under == 0 ? 0 : Math.Sqrt(Math.Max(0, (rx2 * ry2 - under) / under));
            if (large == sweep) factor = -factor;
            double centreXp = factor * radiusX * py / radiusY, centreYp = -factor * radiusY * px / radiusX;
            double centreX = cos * centreXp - sin * centreYp + (x1 + x2) / 2;
            double centreY = sin * centreXp + cos * centreYp + (y1 + y2) / 2;

            double start = Math.Atan2((py - centreYp) / radiusY, (px - centreXp) / radiusX);
            double end = Math.Atan2((-py - centreYp) / radiusY, (-px - centreXp) / radiusX);
            double sweepAngle = end - start;
            if (sweep && sweepAngle < 0) sweepAngle += 2 * Math.PI;
            else if (!sweep && sweepAngle > 0) sweepAngle -= 2 * Math.PI;

            int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / (Math.PI / 2) - 1e-9));
            double piece = sweepAngle / pieces, handle = 4.0 / 3 * Math.Tan(piece / 4);
            double angle = start;
            for (int i = 0; i < pieces; i++, angle += piece)
            {
                double cosA = Math.Cos(angle), sinA = Math.Sin(angle), cosB = Math.Cos(angle + piece), sinB = Math.Sin(angle + piece);
                // On a circle of radius one, then stretched, turned and moved into place.
                double ux1 = cosA - handle * sinA, uy1 = sinA + handle * cosA;
                double ux2 = cosB + handle * sinB, uy2 = sinB - handle * cosB;
                steps.Add(new Geometry.Step
                {
                    Kind = Geometry.Kind.Curve,
                    X1 = centreX + cos * radiusX * ux1 - sin * radiusY * uy1, Y1 = centreY + sin * radiusX * ux1 + cos * radiusY * uy1,
                    X2 = centreX + cos * radiusX * ux2 - sin * radiusY * uy2, Y2 = centreY + sin * radiusX * ux2 + cos * radiusY * uy2,
                    X = i == pieces - 1 ? x2 : centreX + cos * radiusX * cosB - sin * radiusY * sinB,
                    Y = i == pieces - 1 ? y2 : centreY + sin * radiusX * cosB + cos * radiusY * sinB,
                });
            }
        }
    }

    /// <summary>The plain shapes, as the same steps an icon's path is made of.</summary>
    internal static class Shapes
    {
        const double Quarter = 0.5522847498307936; // how far a curve's handles reach to round a quarter circle

        public static List<Geometry.Step> RoundedRectangle(Rect box, double radiusX, double radiusY)
        {
            double left = box.X, top = box.Y, right = box.X + box.Width, bottom = box.Y + box.Height;
            // A corner can be no rounder than half a side.
            double rx = Math.Max(0, Math.Min(radiusX, box.Width / 2)), ry = Math.Max(0, Math.Min(radiusY, box.Height / 2));
            var steps = new List<Geometry.Step>();
            if (rx <= 0 || ry <= 0)
            {
                steps.Add(Move(left, top));
                steps.Add(Line(right, top));
                steps.Add(Line(right, bottom));
                steps.Add(Line(left, bottom));
            }
            else
            {
                double hx = rx * Quarter, hy = ry * Quarter;
                steps.Add(Move(left + rx, top));
                steps.Add(Line(right - rx, top));
                steps.Add(Curve(right - rx + hx, top, right, top + ry - hy, right, top + ry));
                steps.Add(Line(right, bottom - ry));
                steps.Add(Curve(right, bottom - ry + hy, right - rx + hx, bottom, right - rx, bottom));
                steps.Add(Line(left + rx, bottom));
                steps.Add(Curve(left + rx - hx, bottom, left, bottom - ry + hy, left, bottom - ry));
                steps.Add(Line(left, top + ry));
                steps.Add(Curve(left, top + ry - hy, left + rx - hx, top, left + rx, top));
            }
            steps.Add(new Geometry.Step { Kind = Geometry.Kind.Close });
            return steps;
        }

        public static List<Geometry.Step> Ellipse(Point centre, double radiusX, double radiusY)
        {
            double x = centre.X, y = centre.Y, hx = radiusX * Quarter, hy = radiusY * Quarter;
            return new List<Geometry.Step>
            {
                Move(x + radiusX, y),
                Curve(x + radiusX, y + hy, x + hx, y + radiusY, x, y + radiusY),
                Curve(x - hx, y + radiusY, x - radiusX, y + hy, x - radiusX, y),
                Curve(x - radiusX, y - hy, x - hx, y - radiusY, x, y - radiusY),
                Curve(x + hx, y - radiusY, x + radiusX, y - hy, x + radiusX, y),
                new Geometry.Step { Kind = Geometry.Kind.Close },
            };
        }

        static Geometry.Step Move(double x, double y) => new Geometry.Step { Kind = Geometry.Kind.Move, X = x, Y = y };

        static Geometry.Step Line(double x, double y) => new Geometry.Step { Kind = Geometry.Kind.Line, X = x, Y = y };

        static Geometry.Step Curve(double x1, double y1, double x2, double y2, double x, double y) =>
            new Geometry.Step { Kind = Geometry.Kind.Curve, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X = x, Y = y };

        /// <summary>The steps as a path Eto can fill and stroke.</summary>
        public static E.GraphicsPath Build(List<Geometry.Step> steps)
        {
            var path = new E.GraphicsPath();
            float x = 0, y = 0;
            foreach (Geometry.Step step in steps)
            {
                switch (step.Kind)
                {
                    case Geometry.Kind.Move:
                        path.StartFigure();
                        x = (float)step.X;
                        y = (float)step.Y;
                        path.MoveTo(x, y);
                        break;
                    case Geometry.Kind.Line:
                        x = (float)step.X;
                        y = (float)step.Y;
                        path.LineTo(x, y);
                        break;
                    case Geometry.Kind.Curve:
                    {
                        var from = new E.PointF(x, y);
                        x = (float)step.X;
                        y = (float)step.Y;
                        path.AddBezier(from, new E.PointF((float)step.X1, (float)step.Y1), new E.PointF((float)step.X2, (float)step.Y2), new E.PointF(x, y));
                        break;
                    }
                    case Geometry.Kind.Close:
                        path.CloseFigure();
                        break;
                }
            }
            return path;
        }
    }

    /// <summary>The interface's font: the system's own, regular or bold.</summary>
    internal static class Fonts
    {
        static readonly Dictionary<long, E.Font> made = new Dictionary<long, E.Font>();

        // Sizes are asked for in pixels at 100 %. Eto sizes a font in points, which is a pixel
        // each on a Mac and three quarters of one on Windows (where this runs only in tests).
        static double PointsToAPixel => Eto.Platform.Instance != null && Eto.Platform.Instance.IsMac ? 1 : 0.75;

        public static E.Font Get(double pixels, int weight)
        {
            bool bold = weight >= 600; // WPF's SemiBold and Bold. Medium, between the two, is regular here.
            long key = (long)Math.Round(pixels * 100) * 2 + (bold ? 1 : 0);
            if (!made.TryGetValue(key, out E.Font font))
            {
                float points = (float)(pixels * PointsToAPixel);
                // Only ever the two fonts the system hands out itself. A Mac's system font must not
                // be asked for by name, weight by weight: it answers with a different typeface.
                made[key] = font = bold ? E.SystemFonts.Bold(points) : E.SystemFonts.Default(points);
            }
            return font;
        }
    }

    /// <summary>One line of text, measured as it is made, and cut short with an ellipsis if it is wider than allowed.</summary>
    internal sealed class FormattedText
    {
        static readonly Dictionary<string, E.SizeF> measured = new Dictionary<string, E.SizeF>();

        readonly string text;
        readonly double size, maxWidth;
        readonly int weight;
        readonly Color colour;

        public double Width { get; }
        public double WidthIncludingTrailingWhitespace => Width;
        public double Height { get; }

        public FormattedText(string text, double size, FontWeight weight, Color colour, double maxWidth = 0)
        {
            this.text = text ?? "";
            this.size = size;
            this.weight = weight.Weight;
            this.colour = colour;
            this.maxWidth = maxWidth;

            // The same few labels are measured again and again while the pointer moves.
            string key = this.weight + "|" + size.ToString("R", CultureInfo.InvariantCulture) + "|" + maxWidth.ToString("R", CultureInfo.InvariantCulture) + "|" + this.text;
            if (!measured.TryGetValue(key, out E.SizeF needed))
            {
                if (measured.Count > 2000) measured.Clear();
                measured[key] = needed = Make(E.Colors.Black).Measure();
            }
            Width = maxWidth > 0 ? Math.Min(needed.Width, maxWidth) : needed.Width;
            Height = needed.Height;
        }

        E.FormattedText Make(E.Color color)
        {
            var formatted = new E.FormattedText
            {
                Text = text,
                Font = Fonts.Get(size, weight),
                ForegroundBrush = new E.SolidBrush(color),
                Wrap = E.FormattedTextWrapMode.None,
                Trimming = maxWidth > 0 ? E.FormattedTextTrimming.CharacterEllipsis : E.FormattedTextTrimming.None,
            };
            if (maxWidth > 0) formatted.MaximumWidth = (float)maxWidth;
            return formatted;
        }

        internal void Draw(E.Graphics graphics, Point at, double opacity) =>
            graphics.DrawText(Make(colour.ToEto(opacity)), new E.PointF((float)at.X, (float)at.Y));
    }

    /// <summary>
    /// What a picture is drawn on. Push a transform, an opacity or a clip, draw, and Pop:
    /// each Pop takes back the latest Push, whichever kind it was.
    /// </summary>
    internal sealed class DrawingContext
    {
        readonly E.Graphics graphics;
        readonly Stack<Action> pushed = new Stack<Action>(); // how to take back each Push
        double opacity = 1;
        E.IGraphicsPath clip;

        internal DrawingContext(E.Graphics graphics) => this.graphics = graphics;

        public void PushTransform(Transform transform)
        {
            graphics.SaveTransform();
            transform.Apply(graphics);
            pushed.Push(graphics.RestoreTransform);
        }

        /// <summary>Everything drawn until the Pop is this much fainter.</summary>
        public void PushOpacity(double value)
        {
            double before = opacity;
            opacity *= value;
            pushed.Push(() => opacity = before);
        }

        /// <summary>Nothing is drawn outside the outline until the Pop. One clip at a time: a second replaces the first.</summary>
        public void PushClip(Geometry geometry)
        {
            E.IGraphicsPath before = clip;
            Clip(geometry.Path);
            pushed.Push(() => Clip(before));
        }

        public void Pop()
        {
            if (pushed.Count > 0) pushed.Pop()();
        }

        void Clip(E.IGraphicsPath path)
        {
            clip = path;
            if (path == null) graphics.ResetClip();
            else graphics.SetClip(path);
        }

        // ------------------------------------------------------------ shapes

        public void DrawRoundedRectangle(Brush brush, Pen pen, Rect box, double radiusX, double radiusY) =>
            Draw(brush, pen, Shapes.RoundedRectangle(box, radiusX, radiusY), box);

        public void DrawRectangle(Brush brush, Pen pen, Rect box) => Draw(brush, pen, Shapes.RoundedRectangle(box, 0, 0), box);

        public void DrawEllipse(Brush brush, Pen pen, Point centre, double radiusX, double radiusY) =>
            Draw(brush, pen, Shapes.Ellipse(centre, radiusX, radiusY), new Rect(centre.X - radiusX, centre.Y - radiusY, 2 * radiusX, 2 * radiusY));

        public void DrawLine(Pen pen, Point from, Point to)
        {
            if (pen == null) return;
            using (E.Pen stroke = Stroke(pen))
                if (stroke != null) graphics.DrawLine(stroke, (float)from.X, (float)from.Y, (float)to.X, (float)to.Y);
        }

        /// <summary>Its inside filled with the brush, then its outline drawn with the pen. Either may be null.</summary>
        public void DrawGeometry(Brush brush, Pen pen, Geometry geometry) => Draw(brush, pen, geometry.Path, geometry.Bounds);

        public void DrawText(FormattedText text, Point at) => text.Draw(graphics, at, opacity);

        void Draw(Brush brush, Pen pen, List<Geometry.Step> steps, Rect box)
        {
            using (E.GraphicsPath path = Shapes.Build(steps)) Draw(brush, pen, path, box);
        }

        void Draw(Brush brush, Pen pen, E.IGraphicsPath path, Rect box)
        {
            if (brush != null) Fill(brush, path, box);
            if (pen == null) return;
            using (E.Pen stroke = Stroke(pen))
                if (stroke != null) graphics.DrawPath(stroke, path);
        }

        // Pens here are always one plain colour.
        E.Pen Stroke(Pen pen)
        {
            if (!(pen.Brush is SolidColorBrush solid) || !(pen.Thickness > 0)) return null;
            return new E.Pen(solid.Color.ToEto(opacity), (float)pen.Thickness)
            {
                LineCap = pen.StartLineCap == PenLineCap.Round ? E.PenLineCap.Round : pen.StartLineCap == PenLineCap.Square ? E.PenLineCap.Square : E.PenLineCap.Butt,
                LineJoin = pen.LineJoin == PenLineJoin.Round ? E.PenLineJoin.Round : pen.LineJoin == PenLineJoin.Bevel ? E.PenLineJoin.Bevel : E.PenLineJoin.Miter,
            };
        }

        void Fill(Brush brush, E.IGraphicsPath path, Rect box)
        {
            if (brush is SolidColorBrush solid)
            {
                using (var fill = new E.SolidBrush(solid.Color.ToEto(opacity))) graphics.FillPath(fill, path);
                return;
            }
            if (!(brush is LinearGradientBrush gradient) || gradient.GradientStops.Count == 0) return;

            var stops = new List<GradientStop>(gradient.GradientStops);
            stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            double fromX = box.X + gradient.StartPoint.X * box.Width, fromY = box.Y + gradient.StartPoint.Y * box.Height;
            double alongX = box.X + gradient.EndPoint.X * box.Width - fromX, alongY = box.Y + gradient.EndPoint.Y * box.Height - fromY;
            Func<double, E.PointF> at = offset => new E.PointF((float)(fromX + alongX * offset), (float)(fromY + alongY * offset));

            bool level = Math.Abs(alongY) < 1e-6, upright = Math.Abs(alongX) < 1e-6;
            if (stops.Count <= 2 || !(level || upright))
            {
                GradientStop first = stops[0], last = stops[stops.Count - 1];
                using (var fade = new E.LinearGradientBrush(first.Color.ToEto(opacity), last.Color.ToEto(opacity), at(first.Offset), at(last.Offset)))
                    graphics.FillPath(fade, path);
                return;
            }

            // Eto fades between two colours. More than two (the colour picker's rainbow) are a row
            // of two-colour bands, side by side inside the shape.
            E.IGraphicsPath before = clip;
            Clip(path);
            for (int i = 0; i + 1 < stops.Count; i++)
            {
                GradientStop a = stops[i], b = stops[i + 1];
                if (!(b.Offset > a.Offset)) continue;
                E.PointF start = at(a.Offset), end = at(b.Offset);
                const float Overlap = 0.5f; // so that no hairline shows between one band and the next
                E.RectangleF band = level
                    ? new E.RectangleF(Math.Min(start.X, end.X), (float)box.Y, Math.Abs(end.X - start.X) + Overlap, (float)box.Height)
                    : new E.RectangleF((float)box.X, Math.Min(start.Y, end.Y), (float)box.Width, Math.Abs(end.Y - start.Y) + Overlap);
                using (var fade = new E.LinearGradientBrush(a.Color.ToEto(opacity), b.Color.ToEto(opacity), start, end))
                    graphics.FillRectangle(fade, band);
            }
            Clip(before);
        }
    }

    /// <summary>Makes the pictures.</summary>
    internal static class Canvas
    {
        /// <summary>
        /// Hand pictures to Rhino pixel by pixel instead of as a PNG (see Render). Switched on
        /// for good the first time the PNG way fails, and by the drawing test to try this way too.
        /// </summary>
        internal static bool PixelByPixel;

        /// <summary>
        /// A picture of the given size (in pixels at 100 % display scaling), drawn by the
        /// caller in those same units, and made at the scale asked for: Picture.Render's work.
        /// </summary>
        public static System.Drawing.Bitmap Render(double width, double height, double scale, Action<DrawingContext> draw)
        {
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
            using (var picture = new E.Bitmap(pixelWidth, pixelHeight, E.PixelFormat.Format32bppRgba))
            {
                using (var graphics = new E.Graphics(picture))
                {
                    // Niceties first, each on its own: a system that will not have one still gets its picture.
                    try { graphics.Clear(); } catch (Exception) { } // to nothing: a new bitmap need not start out clear
                    try { graphics.AntiAlias = true; } catch (Exception) { }
                    // Whole numbers are the edges between pixels, as they are to WPF: a hairline is drawn on a half.
                    try { graphics.PixelOffsetMode = E.PixelOffsetMode.Half; } catch (Exception) { }
                    graphics.ScaleTransform((float)scale, (float)scale);
                    draw(new DrawingContext(graphics));
                }

                // Rhino takes a System.Drawing bitmap, and Eto keeps its pixels in whatever way
                // suits the system (their order, and whether colour is multiplied by alpha). A PNG
                // says all of that for itself, so the picture is handed over as one.
                if (!PixelByPixel)
                {
                    try
                    {
                        var stream = new MemoryStream();
                        picture.Save(stream, E.ImageFormat.Png);
                        stream.Position = 0;
                        return new System.Drawing.Bitmap(stream); // the stream is left open: the bitmap may read from it for as long as it lives
                    }
                    catch (Exception)
                    {
                        PixelByPixel = true; // this system will not do it that way: the other way from now on
                    }
                }
                return Copy(picture);
            }
        }

        /// <summary>
        /// A picture Eto has read (a comment's picture, CommentImages.cs) as the bitmap Rhino
        /// takes: handed over the same two ways as the pictures made here.
        /// </summary>
        internal static System.Drawing.Bitmap ToSystem(E.Bitmap picture)
        {
            if (!PixelByPixel)
            {
                try
                {
                    var stream = new MemoryStream();
                    picture.Save(stream, E.ImageFormat.Png);
                    stream.Position = 0;
                    return new System.Drawing.Bitmap(stream); // the stream is left open, as in Render
                }
                catch (Exception)
                {
                    PixelByPixel = true;
                }
            }
            return Copy(picture);
        }

        // The same picture, pixel by pixel: Eto says how each of its pixels reads as plain
        // alpha, red, green and blue, which is what a System.Drawing bitmap keeps (and how
        // the brush's dabs are handed to Rhino, in Brush.cs).
        static System.Drawing.Bitmap Copy(E.Bitmap picture)
        {
            int width = picture.Width, height = picture.Height;
            var pixels = new int[width * height];
            using (E.BitmapData data = picture.Lock())
            {
                if (data.BytesPerPixel == 4)
                {
                    var row = new int[width];
                    for (int y = 0; y < height; y++)
                    {
                        int from = data.Flipped ? height - 1 - y : y;
                        System.Runtime.InteropServices.Marshal.Copy(data.Data + from * data.ScanWidth, row, 0, width);
                        for (int x = 0; x < width; x++) pixels[y * width + x] = data.TranslateDataToArgb(row[x]);
                    }
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++) pixels[y * width + x] = data.GetPixel(x, y).ToArgb();
                }
            }
            var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData target = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * width, target.Scan0 + y * target.Stride, width);
            bitmap.UnlockBits(target);
            return bitmap;
        }
    }
}
#endif
