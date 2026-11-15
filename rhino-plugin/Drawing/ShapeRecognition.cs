using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace RhinoSketchingTool
{
    // Pure screen-space recognition, independent of Rhino documents, viewports and UI.
    internal static class ShapeRecognition
    {
        internal static double Distance(Point2d a, Point2d b)
        {
            double x = a.X - b.X, y = a.Y - b.Y;
            return Math.Sqrt(x * x + y * y);
        }

        internal static Point2d[] Recognize(Point2d[] screen)
        {
            if (screen == null || screen.Length < 3) return null;
            double length = 0;
            for (int i = 1; i < screen.Length; i++) length += Distance(screen[i], screen[i - 1]);
            Point2d first = screen[0], last = screen[screen.Length - 1];
            double chord = Distance(first, last);
            if (length < 14) return null;
            var snapped = new List<Point2d>();
            if (chord > length * 0.88)
            {
                Vector2d direction = last - first;
                double deviation = 0;
                foreach (var point in screen)
                    deviation = Math.Max(deviation, Math.Abs(direction.X * (point.Y - first.Y) - direction.Y * (point.X - first.X)) / chord);
                if (deviation > Math.Max(3, chord * 0.035)) return null;
                for (int i = 0; i < screen.Length; i++) snapped.Add(first + direction * ((double)i / (screen.Length - 1)));
            }
            else
            {
                double x0 = first.X, x1 = first.X, y0 = first.Y, y1 = first.Y;
                foreach (var point in screen)
                {
                    x0 = Math.Min(x0, point.X); x1 = Math.Max(x1, point.X);
                    y0 = Math.Min(y0, point.Y); y1 = Math.Max(y1, point.Y);
                }
                double width = x1 - x0, height = y1 - y0, smallest = Math.Min(width, height);
                if (smallest < 14 || chord > Math.Max(12, smallest * 0.25)) return null;
                double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
                double ellipseError = 0, rectangleError = 0, area = 0;
                var corners = new[] { new Point2d(x0, y0), new Point2d(x1, y0), new Point2d(x1, y1), new Point2d(x0, y1) };
                var cornerDistance = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
                for (int i = 0; i < screen.Length; i++)
                {
                    Point2d point = screen[i], next = screen[(i + 1) % screen.Length];
                    double x = (point.X - cx) / (width / 2), y = (point.Y - cy) / (height / 2);
                    ellipseError += Math.Abs(Math.Sqrt(x * x + y * y) - 1);
                    rectangleError += Math.Min(Math.Min(point.X - x0, x1 - point.X), Math.Min(point.Y - y0, y1 - point.Y)) / smallest;
                    area += point.X * next.Y - next.X * point.Y;
                    for (int c = 0; c < 4; c++) cornerDistance[c] = Math.Min(cornerDistance[c], Distance(point, corners[c]));
                }
                ellipseError /= screen.Length;
                rectangleError /= screen.Length;
                bool rectangle = rectangleError < 0.055;
                foreach (double distance in cornerDistance) rectangle &= distance < Math.Max(8, smallest * 0.2);
                int winding = area < 0 ? -1 : 1;
                const int steps = 64;
                if (rectangle)
                {
                    int start = 0;
                    for (int c = 1; c < 4; c++) if (Distance(first, corners[c]) < Distance(first, corners[start])) start = c;
                    for (int i = 0; i <= steps; i++)
                    {
                        double place = i * 4.0 / steps;
                        int segment = Math.Min(3, (int)place);
                        Point2d a = corners[(start + winding * segment + 8) % 4];
                        Point2d b = corners[(start + winding * (segment + 1) + 8) % 4];
                        snapped.Add(a + (b - a) * (place - segment));
                    }
                }
                else if (ellipseError < 0.12 && Math.Abs(area) > width * height)
                {
                    double start = Math.Atan2((first.Y - cy) / height, (first.X - cx) / width);
                    for (int i = 0; i <= steps; i++)
                    {
                        double angle = start + winding * Math.PI * 2 * i / steps;
                        snapped.Add(new Point2d(cx + width / 2 * Math.Cos(angle), cy + height / 2 * Math.Sin(angle)));
                    }
                }
                else return null;
            }

            return snapped.ToArray();
        }
    }
}