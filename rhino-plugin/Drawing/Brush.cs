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

#if MAC
        // Rhino for Mac draws no sprites: its Metal display has yet to be given them (McNeel's
        // RH-81538, "an OpenGL routine that we need to rewrite for metal"). Points it does
        // draw. So on a Mac a dab is a round point: a disc of the ink, as wide as the stroke
        // is there and as strong as the dab. A disc is solid right across, with no soft rim,
        // but a faint stroke still fades toward its edges, where fewer discs overlap.
        // Discs of one size and one strength are drawn together.
        //
        // The rim of a disc is a step, where the rim of a dab is a fade. So there are more discs
        // to a spot than dabs, each fainter, which makes each step a small one; and which of two
        // sizes or two strengths a disc gets goes by a steady rhythm along the stroke rather
        // than by a toss, so that no few alike come together and show as a lump.
        const double DiscCover = 20;    // how many discs overlap at any spot of a stroke, where they may be faint enough for that
        const int Strengths = 24;       // how many strengths of ink the discs of a stroke come in, from Faintest up to solid
        const double DepthStep = 1.15;  // in a perspective view, discs are sized together while their distances are within this ratio
        const int MaxRuns = 48;         // and into no more groups than this, however deep the stroke runs
        const double MaxDisc = 16384;   // pixels: no screen is wider, so no disc need be

        struct Kind
        {
            public Point3d[] Places;
            public float[] Turns; // for each disc a number from 0 to 1, spread evenly along the stroke: see Kept
            public Color Ink;     // the ink, at this kind's strength
            public float Width;   // model units
        }
#else
        struct Kind
        {
            public DisplayBitmap Dab;
            public DisplayBitmapDrawList Places;
            public float Width; // model units
        }
#endif

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
            for (int i = 0; i < MaxLevels; i++)
                widths[i] = widest * Math.Pow(LevelStep, i);

            double smallestStep = Math.Max(MinSpacing / paint.PixelsPerUnit, widest * 0.01);
            double dabs = 1;
            for (int i = 1; i < count; i++)
            {
                double width = (paint.Widths[i - 1] + paint.Widths[i]) / 2;
#if MAC
                dabs += points[i - 1].DistanceTo(points[i]) / Math.Max(width / DiscCover, smallestStep);
#else
                dabs += points[i - 1].DistanceTo(points[i]) / Math.Max(width / Cover(paint.Hardness), smallestStep);
#endif
            }
            double stretch = Math.Max(1, dabs / MaxDabs);

#if MAC
            var places = new List<Point3d>[MaxLevels * Strengths];
            var turns = new List<float>[MaxLevels * Strengths];
            double phase = Phase(points[0]); // where in its round this stroke's rhythm starts: no two strokes thin out alike
#else
            const int HardnessLevels = 21;
            var places = new List<Point3d>[MaxLevels * HardnessLevels * Rims.Length];
            var colors = new List<Color>[places.Length];
#endif
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
                double localHardness = paint.Hardnesses == null ? paint.Hardness : paint.Hardnesses[segment] + (paint.Hardnesses[next] - paint.Hardnesses[segment]) * t;

                // The width usually falls between two size levels. Which of the two the dab
                // gets is a coin toss weighted toward the closer one, so along a stroke
                // the levels blend into each other instead of meeting at a visible step.
#if MAC
                // A set of solid discs of uniformly varying diameter approximates a dab
                // with a solid centre and a linear fade towards its rim. At hardness 1
                // every disc has full diameter. Use their mean diameter for ink coverage.
                double minimum = Math.Max(0, Math.Min(1, localHardness));
                double fraction = minimum + (1 - minimum) * Rhythm(index, 0.414213562373095, phase);
                double diameter = Math.Max(width * fraction, widths[MaxLevels - 1]);
                double exact = Math.Log(diameter / widest) / Math.Log(LevelStep);
                int level = (int)Math.Floor(exact + Rhythm(index, 0.7548776662466927));
#else
                double exact = Math.Log(width / widest) / Math.Log(LevelStep);
                int level = (int)Math.Floor(exact + Toss(-1 - index));
#endif
                level = Math.Max(0, Math.Min(MaxLevels - 1, level));

                double wanted = Math.Min(0.995, paint.Opacity * flow); // how opaque the stroke should be here
#if MAC
                double coverage = width * (1 + minimum) / 2;
                double step = Math.Max(width / DiscCover, smallestStep) * stretch;
                if (Dab(wanted, coverage, 1, ref step, out double alpha, out _))
#else
                localHardness = DabBitmap.Nearest(Math.Max(0, Math.Min(localHardness, 1 - EdgePixels / (widths[level] * paint.PixelsPerUnit))));
                int hardnessLevel = (int)Math.Round(localHardness * (HardnessLevels - 1));
                double step = Math.Max(widths[level] / Cover(localHardness), smallestStep) * stretch;
                if (Dab(wanted, widths[level], localHardness, ref step, out double alpha, out int rim))
#endif
                {
#if MAC
                    // Which of the strengths a disc gets: the two either side of what it should be, turn and
                    // turn about, so that they average out right. The weakest of them is the faintest dab
                    // there is (alpha is never under that), so no disc is fainter than Rhino is known to
                    // draw: whether it leaves out faint points, as it does faint sprite pixels, then does
                    // not matter.
                    int strength = (int)Math.Floor((alpha - Faintest) / (1 - Faintest) * (Strengths - 1) + Rhythm(index, 0.5698402909980532));
                    int kind = level * Strengths + Math.Max(0, Math.Min(Strengths - 1, strength));
#else
                    int kind = (level * HardnessLevels + hardnessLevel) * Rims.Length + rim;
#endif
                    if (places[kind] == null) places[kind] = new List<Point3d>();
                    places[kind].Add(place);
#if MAC
                    if (turns[kind] == null) turns[kind] = new List<float>();
                    turns[kind].Add((float)Rhythm(index, 0.6180339887498949, phase));
#else
                    // Dither between adjacent alpha values to preserve the average.
                    int alphaByte = Math.Min(255, (int)Math.Floor(alpha * 255 + Toss(index)));
                    if (colors[kind] == null) colors[kind] = new List<Color>();
                    colors[kind].Add(Color.FromArgb(alphaByte, color));
#endif
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
#if MAC
                int level = i / Strengths, strength = i % Strengths;
                double ink = Faintest + (1 - Faintest) * strength / (Strengths - 1);
                kinds.Add(new Kind { Places = places[i].ToArray(), Turns = turns[i].ToArray(), Ink = Color.FromArgb((int)Math.Round(255 * ink), color), Width = (float)widths[level] });
#else
                int level = i / (Rims.Length * HardnessLevels), rim = i % Rims.Length;
                double localHardness = (i / Rims.Length % HardnessLevels) / (double)(HardnessLevels - 1);
                var list = new DisplayBitmapDrawList();
                list.SetPoints(places[i], colors[i]);
                kinds.Add(new Kind { Dab = DabBitmap.For(localHardness, rim), Places = list, Width = (float)widths[level] });
#endif
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
            try
            {
#if MAC
                PaintDiscs(display, viewport);
#else
                foreach (Kind kind in kinds)
                    display.DrawSprites(kind.Dab, kind.Places, (float)(kind.Width * SpriteSize * scale), true);
#endif
            }
            finally { display.PopModelTransform(); }
            return cramped;
        }

#if MAC
        // The discs of one kind, sorted by how far off they are; and how much smaller each such run is drawn.
        [ThreadStatic] static List<Point3d>[] runs;
        [ThreadStatic] static double[] shrinks;

        // A point is sized in pixels, not in model units as a sprite can be. So the size is worked
        // out for the view as it is now: the stroke's width, times the pixels a model unit covers
        // where the stroke is. In a parallel view that is the same everywhere. In a perspective
        // view it shrinks with distance, so a stroke that runs off into the distance has its discs
        // sized run by run, by how far away they are.
        void PaintDiscs(DisplayPipeline display, RhinoViewport viewport)
        {
            if (runs == null)
            {
                runs = new List<Point3d>[MaxRuns];
                shrinks = new double[MaxRuns];
            }
            double density = display.DpiScale > 0 ? display.DpiScale : 1;

            // "pixels" is what a model unit covers where the stroke is: all of it, or (with more
            // runs than one) the nearest of it, each run being "step" times as far off as the last.
            double pixels, nearest = 1, step = 1;
            int count = 1;
            Vector3d direction = Vector3d.Zero;
            Point3d camera = Point3d.Origin;
            if (viewport.IsParallelProjection)
            {
                if (!viewport.GetWorldToScreenScale(box.Center, out pixels) || !(pixels > 0)) return;
            }
            else
            {
                direction = viewport.CameraDirection; // not unit length
                if (!direction.Unitize()) return;
                camera = viewport.CameraLocation;
                double furthest = 0; // how far in front of the camera the stroke begins (nearest) and ends
                nearest = double.MaxValue;
                foreach (Point3d point in points)
                {
                    double depth = (point - camera) * direction;
                    if (!(depth > 0)) continue;
                    if (depth < nearest) nearest = depth;
                    if (depth > furthest) furthest = depth;
                }
                if (!(furthest > 0)) return; // all of it is behind the camera

                // Nothing nearer than the front of the view volume is drawn, so no size is wanted for
                // there: with the camera in among a stroke, the runs are kept for what can be seen.
                if (viewport.GetFrustum(out double _, out double _, out double _, out double _, out double front, out double _) && front > nearest)
                    nearest = Math.Min(front, furthest);
                nearest = Math.Max(nearest, furthest * 1e-9);

                if (!viewport.GetWorldToScreenScale(camera + direction * nearest, out pixels) || !(pixels > 0)) return;
                step = DepthStep;
                count = 1 + (int)Math.Floor(Math.Log(furthest / nearest) / Math.Log(step));
                if (count > MaxRuns)
                {
                    // Deeper than that many runs reach: as many as there are, each deeper.
                    count = MaxRuns;
                    step = Math.Pow(furthest / nearest, 1.0 / count);
                }
                if (count <= 1)
                {
                    // One size for all of it: that of half way (as ratios go) from its near end to its far end.
                    count = 1;
                    pixels *= Math.Sqrt(nearest / furthest);
                }
            }

            // Projected widths are in viewport units. The minimum point and the
            // subpixel thinning rule below both use physical framebuffer pixels.
            var port = viewport.ViewportType == ViewportType.DetailViewport ? viewport.ParentView.MainViewport : viewport;
            pixels *= MacDisplay.FramePixelsPerUnit(display.FrameSize, port.Size, density);
            if (count == 1)
            {
                foreach (Kind kind in kinds)
                {
                    double width = kind.Width * pixels;
                    if (width >= 1)
                    {
                        Discs(display, kind.Places, width, density, kind.Ink);
                        continue;
                    }
                    List<Point3d> few = Run(0);
                    for (int i = 0; i < kind.Places.Length; i++)
                        if (Kept(kind.Turns[i], width)) few.Add(kind.Places[i]);
                    Discs(display, few, width, density, kind.Ink);
                }
                return;
            }

            // A run is drawn at the size of its middle (as ratios go): so much smaller than the nearest of the stroke.
            double spread = Math.Log(step);
            for (int i = 0; i < count; i++) shrinks[i] = 1 / Math.Pow(step, i + 0.5);
            foreach (Kind kind in kinds)
            {
                for (int i = 0; i < count; i++) Run(i);
                for (int i = 0; i < kind.Places.Length; i++)
                {
                    Point3d place = kind.Places[i];
                    double depth = (place - camera) * direction;
                    if (!(depth > 0)) continue;
                    int run = depth > nearest ? Math.Min(count - 1, (int)(Math.Log(depth / nearest) / spread)) : 0;
                    if (Kept(kind.Turns[i], kind.Width * pixels * shrinks[run])) runs[run].Add(place);
                }
                for (int i = 0; i < count; i++)
                    Discs(display, runs[i], kind.Width * pixels * shrinks[i], density, kind.Ink);
            }
        }

        static List<Point3d> Run(int index)
        {
            List<Point3d> run = runs[index] ?? (runs[index] = new List<Point3d>());
            run.Clear();
            return run;
        }

        // A disc cannot be drawn thinner than a pixel. Where a stroke is thinner than that its discs
        // are a pixel wide, and fewer: as much fewer as they are too big, by area. So a stroke fades
        // as it shrinks into the distance, as on Windows, instead of hardening into a solid line.
        // Which of the discs stay goes by each one's "turn" (see Rhythm), so those that stay are
        // spread evenly along the stroke, and are the same ones every time it is painted.
        static bool Kept(double turn, double width) => width >= 1 || turn < width * width;

        // A number from 0 to 1 for each dab in turn, like Toss, but one that comes round evenly: of
        // any few dabs in a row, as near as can be the same share get a number under any given one.
        // ("by" is an irrational step; with a different one, or another start, the rhythm is another.)
        static double Rhythm(int index, double by, double from = 0)
        {
            double at = from + index * by;
            return at - Math.Floor(at);
        }

        // A number from 0 to 1 that goes by where a point is: the same for the same point, and
        // unlike for any two that differ.
        static double Phase(Point3d point)
        {
            unchecked
            {
                ulong mixed = (ulong)BitConverter.DoubleToInt64Bits(point.X) * 0x9E3779B97F4A7C15UL;
                mixed = (mixed ^ (mixed >> 29)) + (ulong)BitConverter.DoubleToInt64Bits(point.Y) * 0xC2B2AE3D27D4EB4FUL;
                mixed = (mixed ^ (mixed >> 31)) + (ulong)BitConverter.DoubleToInt64Bits(point.Z) * 0x165667B19E3779F9UL;
                return Toss((int)(mixed ^ (mixed >> 32)));
            }
        }

        // Rhino multiplies the size of a point by the display's density (2 on a Retina display),
        // so that is divided out first: the width asked for is in the viewport's own pixels,
        // and is never under one of those.
        static void Discs(DisplayPipeline display, IReadOnlyCollection<Point3d> places, double width, double density, Color ink)
        {
            if (places.Count == 0) return;
            float radius = (float)(Math.Min(MaxDisc, Math.Max(1, width)) / 2 / density);
            display.DrawPoints(places, PointStyle.RoundSimple, radius, ink);
        }
#endif

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
