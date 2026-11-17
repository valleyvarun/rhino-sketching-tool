using System;
using System.Collections.Generic;
using Rhino.Geometry;
using ScreenPoint = System.Drawing.Point;

namespace RhinoSketchingTool
{
    public sealed partial class SketchController
    {
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
