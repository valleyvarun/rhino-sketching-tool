using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace RhinoSketchingTool
{
    /// <summary>The brush the next stroke is drawn with: what the sliders in the Sketch panel set.</summary>
    public sealed class Brush
    {
        public const double MinSize = 1, MaxSize = 40;
        public const double MinOpacity = 0.1;

        double size = 6, pressure = 0.7, opacity = 0.8, hardness = 0.6;

        /// <summary>Width of the stroke at full pressure, in screen pixels at the moment it is drawn.</summary>
        public double Size
        {
            get => size;
            set => size = Math.Max(MinSize, Math.Min(MaxSize, value));
        }

        /// <summary>How much pressure thins and lightens the stroke, 0 to 1. At 0 a stroke is even from end to end.</summary>
        public double Pressure
        {
            get => pressure;
            set => pressure = Math.Max(0, Math.Min(1, value));
        }

        /// <summary>How solid the stroke is, up to 1.</summary>
        public double Opacity
        {
            get => opacity;
            set => opacity = Math.Max(MinOpacity, Math.Min(1, value));
        }

        /// <summary>How crisp the stroke's edge is, from 0 (soft all the way across) to 1 (sharp).</summary>
        public double Hardness
        {
            get => hardness;
            set => hardness = Math.Max(0, Math.Min(1, value));
        }

        /// <summary>Pressing harder makes the stroke wider. Off, it is Size wide all along.</summary>
        public bool SizeByPressure { get; set; } = true;

        /// <summary>Pressing harder makes the stroke more solid. Off, it is at Opacity all along.</summary>
        public bool OpacityByPressure { get; set; } = true;
        public bool HardnessByPressure { get; set; }
        public string Preset { get; set; } = "crayon";
    }

    /// <summary>
    /// How one stroke is painted: its width and strength at every point of its curve, and the
    /// opacity and hardness it was drawn with. Widths are in model units, so a stroke keeps
    /// its place in the model like paint would: zoom in and it gets bigger on screen.
    ///
    /// This is saved on the curve as two user strings. It is therefore part of the 3dm file,
    /// and follows the curve when Rhino copies or moves it.
    /// </summary>
    internal sealed class StrokePaint
    {
        public const string BrushKey = "SketchingTool.Brush";   // opacity;hardness;widest width;pixels per unit
        public const string PointsKey = "SketchingTool.Points"; // four hex digits per point: width, then flow

        public double Opacity;
        public double Hardness;
        public double PixelsPerUnit; // the screen scale the stroke was drawn at
        public double[] Widths;      // model units, one per point of the curve
        public double[] Flows;       // the share of Opacity that reaches each point, 0 to 1
        public double[] Hardnesses; // optional per-point hardness; old sketches use Hardness
        const string HardnessKey = "Crayon.Hardness";

        public double Widest
        {
            get
            {
                double widest = 0;
                foreach (double width in Widths) widest = Math.Max(widest, width);
                return widest;
            }
        }

        public void Write(ObjectAttributes attributes)
        {
            double widest = Widest;
            attributes.SetUserString(BrushKey, string.Join(";", Text(Opacity), Text(Hardness), Text(widest), Text(PixelsPerUnit)));

            // Per point, one byte each for the width (as a share of the widest) and the flow.
            var text = new StringBuilder(Widths.Length * 4);
            for (int i = 0; i < Widths.Length; i++)
            {
                text.Append(Share(widest > 0 ? Widths[i] / widest : 1).ToString("X2"));
                text.Append(Share(Flows[i]).ToString("X2"));
            }
            attributes.SetUserString(PointsKey, text.ToString());
            if (Hardnesses != null)
            {
                var values = new StringBuilder(Hardnesses.Length * 2);
                foreach (double hardness in Hardnesses) values.Append(((int)Math.Round(Math.Max(0, Math.Min(1, hardness)) * 255)).ToString("X2"));
                attributes.SetUserString(HardnessKey, values.ToString());
            }
            else attributes.SetUserString(HardnessKey, null);
        }

        /// <summary>
        /// The paint saved on a stroke, or null if it has none (it was drawn before there
        /// were brushes). The curve may have been edited since and have a different number
        /// of points, so the paint is spread over the points it has now.
        /// </summary>
        public static StrokePaint Read(ObjectAttributes attributes, int pointCount)
        {
            string brush = attributes.GetUserString(BrushKey);
            string points = attributes.GetUserString(PointsKey);
            if (brush == null || points == null || pointCount < 1) return null;

            string[] parts = brush.Split(';');
            if (parts.Length < 4 || points.Length < 4 || points.Length % 4 != 0) return null;
            if (!Number(parts[0], out double opacity) || !Number(parts[1], out double hardness) ||
                !Number(parts[2], out double widest) || !Number(parts[3], out double pixelsPerUnit)) return null;
            if (!(widest > 0) || !(pixelsPerUnit > 0)) return null;

            int count = points.Length / 4;
            var paint = new StrokePaint
            {
                Opacity = Math.Max(0, Math.Min(1, opacity)),
                Hardness = Math.Max(0, Math.Min(1, hardness)),
                PixelsPerUnit = pixelsPerUnit,
                Widths = new double[count],
                Flows = new double[count],
            };
            for (int i = 0; i < count; i++)
            {
                if (!Hex(points, i * 4, out int width) || !Hex(points, i * 4 + 2, out int flow)) return null;
                paint.Widths[i] = widest * width / 255.0;
                paint.Flows[i] = flow / 255.0;
            }
            string hardnessPoints = attributes.GetUserString(HardnessKey);
            if (hardnessPoints != null && hardnessPoints.Length == count * 2)
            {
                paint.Hardnesses = new double[count];
                for (int i = 0; i < count; i++)
                {
                    if (!Hex(hardnessPoints, i * 2, out int h)) { paint.Hardnesses = null; break; }
                    paint.Hardnesses[i] = h / 255.0;
                }
            }
            if (count == pointCount) return paint;

            var places = new double[pointCount];
            for (int i = 1; i < pointCount; i++) places[i] = (double)i * (count - 1) / (pointCount - 1);
            return paint.At(places);
        }

        /// <summary>
        /// The paint at the given places along the stroke. A place is a point index, and may
        /// fall between two points: 2.5 is half way from point 2 to point 3.
        /// </summary>
        public StrokePaint At(IList<double> places)
        {
            var paint = new StrokePaint
            {
                Opacity = Opacity,
                Hardness = Hardness,
                PixelsPerUnit = PixelsPerUnit,
                Widths = new double[places.Count],
                Flows = new double[places.Count],
                Hardnesses = Hardnesses == null ? null : new double[places.Count],
            };
            int last = Widths.Length - 1;
            for (int i = 0; i < places.Count; i++)
            {
                double place = Math.Max(0, Math.Min(last, places[i]));
                int a = (int)Math.Floor(place), b = Math.Min(a + 1, last);
                double t = place - a;
                paint.Widths[i] = Widths[a] + (Widths[b] - Widths[a]) * t;
                paint.Flows[i] = Flows[a] + (Flows[b] - Flows[a]) * t;
                if (Hardnesses != null) paint.Hardnesses[i] = Hardnesses[a] + (Hardnesses[b] - Hardnesses[a]) * t;
            }
            return paint;
        }

        // Numbers are written the same way whatever the computer's language is set to.
        static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        static bool Number(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !double.IsNaN(value) && !double.IsInfinity(value);

        // 0-1 as a byte. Never 0: every point of a stroke has some width and some paint.
        static int Share(double value) => Math.Max(1, Math.Min(255, (int)Math.Round(value * 255)));

        static bool Hex(string text, int start, out int value) =>
            int.TryParse(text.Substring(start, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A stroke as the brush dabs that paint it: round dots laid close together along the
    /// curve, each as wide and as strong as the stroke is there. The dots always face the
    /// camera, so a stroke looks the same from any side.
    ///
    /// The dots are Rhino "sprites", and two facts about those shape everything here:
    ///
    ///   - A list of sprites is drawn at one size, with one picture. So the dabs are
    ///     sorted into kinds, by size level and by how much of a dab is left at its rim,
    ///     and each kind is drawn in one go.
    ///
    ///   - Rhino leaves out any sprite pixel fainter than about 5%. So a dab cannot fade
    ///     to nothing at its rim, only down to that limit; and a faint stretch of stroke
    ///     is made of fewer dabs, not of fainter ones.
    /// </summary>
    internal sealed class StrokeDabs
    {
        const double SoftCover = 4, HardCover = 8; // how many dabs overlap at any spot of a soft / a hard stroke
        const double MinSpacing = 0.75; // dabs are never closer than this many pixels (at the scale the stroke was drawn at)
        const double MaxSpacing = 0.8;  // nor further apart than this share of their width
        const int MaxDabs = 6000;       // per stroke; a very long thin stroke spaces its dabs wider to stay under this

        const double Faintest = 0.055;  // Rhino draws a sprite pixel at 13/255 but not at 12/255; stay above
        const double FaintDab = 0.055;  // so that is the weakest dab used,
        const double StrongDab = 0.85;  // and this the strongest: past it, overlapping dabs stop adding up smoothly

        // What is left of a dab at its rim, as a share of its centre. A dab gets the lowest
        // of these that keeps its rim above Faintest: the softest edge Rhino will draw.
        static readonly double[] Rims = { 0.06, 0.12, 0.2, 0.35, 0.6, 1 };

        const double LevelStep = 0.85;  // each size level is this much smaller than the one before
        const int MaxLevels = 28;       // the smallest level is about 1/80 of the widest
        const double EdgePixels = 1.5;  // a dab keeps at least this much soft edge (across both sides), or it looks jagged
        const double SpriteSize = 0.5;  // Rhino draws a sprite twice as wide as the size it is given
        const double LiftSlope = 3;     // strokes clear a surface seen at up to atan(3) = 72 degrees off straight-on
        const double MinLift = 0.002, MaxLift = 0.5; // limits of the lift, as a share of the distance to the camera

        struct Kind
        {
            public DisplayBitmap Dab;
            public DisplayBitmapDrawList Places;
            public float Width; // model units
        }

        Kind[] kinds;
        Point3d[] points;  // the stroke's own points
        BoundingBox box;
        double widest;

        /// <summary>The ink colour the dabs were made with.</summary>
        public Color Color { get; private set; }

        /// <summary>Null if there is nothing to paint.</summary>
        public static StrokeDabs Build(IReadOnlyList<Point3d> points, StrokePaint paint, Color color)
        {
            int count = points.Count;
            double widest = paint == null ? 0 : paint.Widest;
            if (count == 0 || !(widest > 0) || paint.Widths.Length != count) return null;

            // What each size level looks like. Small dabs are softened: a 3 pixel dot with a
            // sharp edge is just a jagged blob.
            var widths = new double[MaxLevels];
            var hardness = new double[MaxLevels];
            for (int i = 0; i < MaxLevels; i++)
            {
                widths[i] = widest * Math.Pow(LevelStep, i);
                double limit = 1 - EdgePixels / (widths[i] * paint.PixelsPerUnit);
                hardness[i] = DabBitmap.Nearest(Math.Max(0, Math.Min(paint.Hardness, limit)));
            }

            double smallestStep = Math.Max(MinSpacing / paint.PixelsPerUnit, widest * 0.01);
            double dabs = 1;
            for (int i = 1; i < count; i++)
            {
                double width = (paint.Widths[i - 1] + paint.Widths[i]) / 2;
                dabs += points[i - 1].DistanceTo(points[i]) / Math.Max(width / Cover(paint.Hardness), smallestStep);
            }
            double stretch = Math.Max(1, dabs / MaxDabs);

            const int HardnessLevels = 21;
            var places = new List<Point3d>[MaxLevels * HardnessLevels * Rims.Length];
            var colors = new List<Color>[MaxLevels * HardnessLevels * Rims.Length];
            int segment = 0, index = 0;
            double along = 0; // distance into the current segment
            double length = count > 1 ? points[0].DistanceTo(points[1]) : 0;
            while (true)
            {
                int next = Math.Min(segment + 1, count - 1);
                double t = length > 0 ? along / length : 0;
                Point3d place = points[segment] + (points[next] - points[segment]) * t;
                double width = paint.Widths[segment] + (paint.Widths[next] - paint.Widths[segment]) * t;
                double flow = paint.Flows[segment] + (paint.Flows[next] - paint.Flows[segment]) * t;

                // The width usually falls between two size levels. Which of the two the dab
                // gets is a coin toss weighted toward the closer one, so along a stroke
                // the levels blend into each other instead of meeting at a visible step.
                double exact = Math.Log(width / widest) / Math.Log(LevelStep);
                int level = (int)Math.Floor(exact + Toss(-1 - index));
                level = Math.Max(0, Math.Min(MaxLevels - 1, level));

                double wanted = Math.Min(0.995, paint.Opacity * flow); // how opaque the stroke should be here
                double localHardness = paint.Hardnesses == null ? paint.Hardness : paint.Hardnesses[segment] + (paint.Hardnesses[next] - paint.Hardnesses[segment]) * t;
                localHardness = DabBitmap.Nearest(Math.Max(0, Math.Min(localHardness, 1 - EdgePixels / (widths[level] * paint.PixelsPerUnit))));
                int hardnessLevel = (int)Math.Round(localHardness * (HardnessLevels - 1));
                double step = Math.Max(widths[level] / Cover(localHardness), smallestStep) * stretch;
                if (Dab(wanted, widths[level], localHardness, ref step, out double alpha, out int rim))
                {
                    // A colour's alpha has only 255 steps. Rounding up or down by a steady
                    // coin toss keeps the average right.
                    int alphaByte = Math.Min(255, (int)Math.Floor(alpha * 255 + Toss(index)));
                    int kind = (level * HardnessLevels + hardnessLevel) * Rims.Length + rim;
                    if (places[kind] == null)
                    {
                        places[kind] = new List<Point3d>();
                        colors[kind] = new List<Color>();
                    }
                    places[kind].Add(place);
                    colors[kind].Add(Color.FromArgb(alphaByte, color));
                }
                index++;

                along += step;
                while (segment < count - 1 && along > length)
                {
                    along -= length;
                    segment++;
                    length = segment < count - 1 ? points[segment].DistanceTo(points[segment + 1]) : 0;
                }
                if (segment >= count - 1) break;
            }

            var kinds = new List<Kind>();
            for (int i = 0; i < places.Length; i++)
            {
                if (places[i] == null) continue;
                int level = i / (Rims.Length * HardnessLevels), rim = i % Rims.Length;
                double localHardness = (i / Rims.Length % HardnessLevels) / (double)(HardnessLevels - 1);
                var list = new DisplayBitmapDrawList();
                list.SetPoints(places[i], colors[i]);
                kinds.Add(new Kind { Dab = DabBitmap.For(localHardness, rim), Places = list, Width = (float)widths[level] });
            }
            if (kinds.Count == 0) return null;

            var all = new Point3d[count];
            for (int i = 0; i < count; i++) all[i] = points[i];
            return new StrokeDabs
            {
                kinds = kinds.ToArray(),
                points = all,
                box = new BoundingBox(all),
                widest = widest,
                Color = color,
            };
        }

        // How many dabs overlap at any spot of a stroke. More gives a smoother outline,
        // which a hard brush needs; fewer lets each dab be stronger, and a strong dab can
        // have a softer rim before the rim gets too faint for Rhino to draw.
        static double Cover(double hardness) => SoftCover + (HardCover - SoftCover) * hardness;

        // The share of a dab's centre strength it has on average across its width.
        static double Mean(double hardness, double rim) => hardness + (1 - hardness) * (1 + rim) / 2;

        /// <summary>
        /// Works out one dab so that the dabs overlapping at a spot add up to "wanted"
        /// opacity: how strong the dab is at its centre (alpha), which rim it has, and how
        /// far on the next dab goes (step, which comes in as the usual spacing).
        /// False if the stroke is too faint here to draw anything.
        /// </summary>
        static bool Dab(double wanted, double width, double hardness, ref double step, out double alpha, out int rim)
        {
            for (int attempt = 0; ; attempt++)
            {
                // What each of the dabs covering a spot must add, were it the same all over.
                double each = 1 - Math.Pow(1 - wanted, 1 / Math.Max(1, width / step));

                // The softest rim Rhino will still draw. A dab with a rim is weaker on
                // average, so its centre is made that much stronger.
                alpha = each;
                for (rim = 0; rim < Rims.Length; rim++)
                {
                    alpha = each / Mean(hardness, Rims[rim]);
                    if (alpha * Rims[rim] >= Faintest) break;
                }

                if (rim == Rims.Length)
                {
                    // Too faint even for dabs with no soft rim at all. Use the weakest
                    // dab there is, and space the dabs out until they add up to little enough.
                    rim = Rims.Length - 1;
                    alpha = FaintDab;
                    double cover = Math.Log(1 - wanted) / Math.Log(1 - FaintDab);
                    step = Math.Min(width / cover, MaxSpacing * width);
                    return cover >= 0.5;
                }
                if (alpha <= StrongDab || attempt == 4) break;
                step /= 1.5; // too strong: let more dabs share the work
            }
            alpha = Math.Min(1, alpha);
            return true;
        }

        /// <summary>
        /// Call with depth writing off: dabs overlap, and must not hide one another.
        /// True if the stroke could not be brought as far forward as it should be (see Lift).
        /// </summary>
        public bool Paint(DisplayPipeline display, RhinoViewport viewport)
        {
            Transform lift = Lift(viewport, true, out double scale, out bool cramped);
            display.PushModelTransform(lift);
            foreach (Kind kind in kinds)
                display.DrawSprites(kind.Dab, kind.Places, (float)(kind.Width * SpriteSize * scale), true);
            display.PopModelTransform();
            return cramped;
        }

        /// <summary>The space the painted stroke wants in a viewport.</summary>
        public BoundingBox PaintedBox(RhinoViewport viewport)
        {
            BoundingBox painted = box;
            painted.Inflate(widest / 2);
            painted.Transform(Lift(viewport, false, out double _, out bool _));
            return painted;
        }

        // A dab faces the camera, so where its stroke lies on a surface seen at an angle,
        // the far half of the dab would be behind that surface and hidden. So a stroke is
        // painted slightly nearer the camera than it really is: far enough to clear the
        // surface, close enough that anything properly in front of it still covers it.
        //
        // In a perspective view "nearer" is a scaling toward the camera, which leaves the
        // stroke exactly where it was on screen. Dab sizes do not follow that scaling, so
        // they are given the same factor (scale) by hand.
        //
        // When painting (inView), the lift stops short of the front of the view volume,
        // where the stroke would be cut away, and "cramped" says it had to. That should
        // only happen on the first frame a stroke is seen in: after that the conduit has
        // Rhino leave room for it.
        Transform Lift(RhinoViewport viewport, bool inView, out double scale, out bool cramped)
        {
            scale = 1;
            cramped = false;
            Vector3d direction = viewport.CameraDirection; // not unit length
            if (!direction.Unitize()) return Transform.Identity;
            bool parallel = viewport.IsParallelProjection;

            Point3d camera = viewport.CameraLocation;
            double nearest = double.MaxValue; // how far in front of the camera the stroke starts
            foreach (Point3d point in points)
            {
                double depth = (point - camera) * direction;
                if ((parallel || depth > 0) && depth < nearest) nearest = depth;
            }
            if (nearest == double.MaxValue) return Transform.Identity; // all of it is behind the camera

            double distance = LiftSlope * widest / 2;
            if (!parallel) distance = Math.Min(MaxLift * nearest, MinLift * nearest + distance);
            if (inView && viewport.GetFrustum(out double _, out double _, out double _, out double _, out double front, out double _))
            {
                double room = Math.Max(0, nearest - front * 1.001);
                cramped = room < distance * 0.999;
                distance = Math.Min(distance, room);
            }

            if (parallel) return Transform.Translation(-direction * distance);
            scale = 1 - distance / nearest;
            return Transform.Scale(camera, scale);
        }

        // A number from 0 to 1 that looks random but is always the same for the same dab,
        // so a stroke does not shimmer as it grows.
        static double Toss(int index)
        {
            uint hash = (uint)index * 2654435761u;
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            return (hash & 0xFFFF) / 65536.0;
        }

        /// <summary>
        /// The picture of one dab: a white disc, tinted with the ink colour when it is
        /// drawn. It is solid out to "hardness" of the way from centre to rim, then fades,
        /// reaching Rims[rim] of its centre strength at the rim itself.
        /// </summary>
        static class DabBitmap
        {
            const int Steps = 20;   // one picture per 5% of hardness
            const int Pixels = 128;

            static readonly DisplayBitmap[,] made = new DisplayBitmap[Steps + 1, Rims.Length];
            static readonly List<Bitmap> sources = new List<Bitmap>(); // kept alive for as long as Rhino may use them

            /// <summary>The closest hardness there is a picture for.</summary>
            public static double Nearest(double hardness) => Math.Round(hardness * Steps) / Steps;

            public static DisplayBitmap For(double hardness, int rim)
            {
                int step = Math.Max(0, Math.Min(Steps, (int)Math.Round(hardness * Steps)));
                if (made[step, rim] == null)
                {
                    Bitmap source = Draw((double)step / Steps, Rims[rim]);
                    sources.Add(source);
                    made[step, rim] = new DisplayBitmap(source);
                }
                return made[step, rim];
            }

            static Bitmap Draw(double hardness, double rim)
            {
                var pixels = new int[Pixels * Pixels];
                double half = Pixels / 2.0;
                for (int y = 0; y < Pixels; y++)
                {
                    for (int x = 0; x < Pixels; x++)
                    {
                        double dx = x + 0.5 - half, dy = y + 0.5 - half;
                        double radius = Math.Sqrt(dx * dx + dy * dy) / half; // 0 at the centre, 1 at the rim
                        double alpha = radius >= 1 ? 0
                            : radius <= hardness ? 1
                            : rim + (1 - rim) * (0.5 + 0.5 * Math.Cos(Math.PI * (radius - hardness) / (1 - hardness)));
                        alpha *= Math.Max(0, Math.Min(1, (1 - radius) * half)); // smooth the outermost pixel
                        pixels[y * Pixels + x] = ((int)Math.Round(255 * alpha) << 24) | 0xFFFFFF;
                    }
                }

                var bitmap = new Bitmap(Pixels, Pixels, PixelFormat.Format32bppArgb);
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Pixels, Pixels), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                bitmap.UnlockBits(data);
                return bitmap;
            }
        }
    }
}
