using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rhino.Geometry;
using ScreenPoint = System.Drawing.Point;

namespace RhinoSketchingTool
{
    public sealed partial class SketchController
    {
        // Run from Rhino's UI idle event so a stationary pressed pen can snap without
        // needing another mouse move. Freehand samples remain intact as pressure data.
        void TryHoldShape()
        {
            var stroke = drawing;
            if (stroke == null || !SnapShapes || stroke.Method == DrawMethod.Surface || stroke.SnapKind != null || stroke.Points.Count < 4 || stroke.HoldAt == 0) return;
            if ((double)(Stopwatch.GetTimestamp() - stroke.HoldAt) / Stopwatch.Frequency < .65) return;
            if (stroke.ViewKey != null && !LiveVisibleInView(stroke.View.Document, stroke.View.ActiveViewport)) return;
            var screen = new Point2d[stroke.Points.Count];
            double length = 0;
            for (int i = 0; i < screen.Length; i++)
            {
                screen[i] = stroke.View.ActiveViewport.WorldToClient(stroke.Points[i]);
                if (i > 0) length += screen[i].DistanceTo(screen[i - 1]);
            }
            if (length < 30) return;
            Point2d first = screen[0], last = screen[screen.Length - 1];
            double chord = first.DistanceTo(last);
            stroke.SnapFirst = first;
            if (chord / length > .75) stroke.SnapKind = "line";
            else if (chord < length * .3)
            {
                double x = 0, y = 0;
                foreach (Point2d p in screen) { x += p.X; y += p.Y; }
                var centre = new Point2d(x / screen.Length, y / screen.Length);
                double radius = 0;
                foreach (Point2d p in screen) radius += p.DistanceTo(centre);
                radius /= screen.Length;
                if (radius < 6) return;
                double variance = 0;
                foreach (Point2d p in screen) { double delta = p.DistanceTo(centre) - radius; variance += delta * delta; }
                if (Math.Sqrt(variance / screen.Length) / radius >= .22) return;
                stroke.SnapKind = "circle"; stroke.SnapCentre = centre;
            }
            if (stroke.SnapKind == null) return;
            AdjustHeldShape(stroke.Last);
            if (stroke.SnapPoints == null) stroke.SnapKind = null;
        }

        void AdjustHeldShape(ScreenPoint pointer)
        {
            var stroke = drawing;
            var end = new Point2d(pointer.X, pointer.Y);
            var shape = new List<Point2d>();
            if (stroke.SnapKind == "line")
            {
                double length = stroke.SnapFirst.DistanceTo(end);
                if (length < 4) return;
                int count = Math.Max(3, Math.Min(512, (int)Math.Ceiling(length / 6)));
                for (int i = 0; i <= count; i++) shape.Add(stroke.SnapFirst + (end - stroke.SnapFirst) * ((double)i / count));
            }
            else
            {
                double radius = Math.Max(6, stroke.SnapCentre.DistanceTo(end));
                double start = Math.Atan2(end.Y - stroke.SnapCentre.Y, end.X - stroke.SnapCentre.X);
                int count = Math.Max(24, Math.Min(512, (int)Math.Ceiling(2 * Math.PI * radius / 6)));
                for (int i = 0; i <= count; i++)
                {
                    double angle = start + 2 * Math.PI * i / count;
                    shape.Add(new Point2d(stroke.SnapCentre.X + radius * Math.Cos(angle), stroke.SnapCentre.Y + radius * Math.Sin(angle)));
                }
            }
            var result = new Point3d[shape.Count];
            var places = new double[shape.Count];
            for (int i = 0; i < shape.Count; i++)
            {
                var point = new ScreenPoint((int)Math.Round(shape[i].X), (int)Math.Round(shape[i].Y));
                if (!ViewLine(stroke.View.ActiveViewport, point, out Line line)) return;
                Hit hit = HitPlane(stroke.View.ActiveViewport, line, stroke.Plane);
                if (hit == null) return;
                result[i] = hit.Point;
                places[i] = (double)i * (stroke.Points.Count - 1) / (shape.Count - 1);
            }
            stroke.SnapPoints = result;
            stroke.SnapPaint = Paint(stroke, false).At(places);
            liveVersion++;
            stroke.View.Redraw();
        }

        // Conservative recognition: an unrelated freehand gesture is left alone. Surface
        // strokes are deliberately excluded so snapping cannot lift a sketch off its object.
        static void SnapStroke(DrawDrag stroke, ref Point3d[] points, ref StrokePaint paint)
        {
            if (points.Length < 3) return;
            var viewport = stroke.View.ActiveViewport;
            var screen = new Point2d[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                screen[i] = viewport.WorldToClient(points[i]);
            }
            Point2d[] snapped = ShapeRecognition.Recognize(screen);
            if (snapped == null) return;
            var result = new Point3d[snapped.Length];
            var places = new double[snapped.Length];
            for (int i = 0; i < snapped.Length; i++)
            {
                var pixel = new ScreenPoint((int)Math.Round(snapped[i].X), (int)Math.Round(snapped[i].Y));
                if (!ViewLine(viewport, pixel, out Line line)) return;
                Hit hit = HitPlane(viewport, line, stroke.Plane);
                if (hit == null) return;
                result[i] = hit.Point;
                places[i] = (double)i * (points.Length - 1) / (snapped.Length - 1);
            }
            points = result;
            paint = paint.At(places);
        }
    }
}
