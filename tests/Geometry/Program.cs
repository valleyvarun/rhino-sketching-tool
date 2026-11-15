using System;
using System.Collections.Generic;
using Rhino.Geometry;
using RhinoSketchingTool;

static class Program
{
    static int checks;
    static void Check(bool pass, string description)
    {
        if (!pass) throw new Exception(description);
        checks++;
        Console.WriteLine("PASS " + description);
    }

    static int Main()
    {
        try
        {
            var line = new Point2d[41];
            for (int i = 0; i < line.Length; i++) line[i] = new Point2d(i * 4, i * 2 + Math.Sin(i) * 1.2);
            var snapped = ShapeRecognition.Recognize(line);
            Check(snapped != null && snapped.Length == line.Length, "Noisy line is recognized");
            Check(snapped[0] == line[0] && snapped[snapped.Length - 1] == line[line.Length - 1], "Line endpoints are preserved");
            var direction = snapped[snapped.Length - 1] - snapped[0];
            foreach (var point in snapped)
                if (Math.Abs(direction.X * (point.Y - snapped[0].Y) - direction.Y * (point.X - snapped[0].X)) > 1e-6)
                    throw new Exception("Snapped line is not straight");
            Check(true, "Line output is collinear");

            foreach (int winding in new[] { -1, 1 })
            {
                var ellipse = new Point2d[101];
                for (int i = 0; i < ellipse.Length; i++)
                {
                    double angle = winding * i * Math.PI * 2 / 100;
                    double noise = 1 + Math.Sin(i * 2.7) * .025;
                    ellipse[i] = new Point2d(170 + 95 * Math.Cos(angle) * noise, 120 + 52 * Math.Sin(angle) * noise);
                }
                snapped = ShapeRecognition.Recognize(ellipse);
                Check(snapped != null && snapped.Length == 65, "Noisy ellipse is recognized in winding " + winding);
                Check(ShapeRecognition.Distance(snapped[0], snapped[64]) < 1e-6, "Ellipse closes in winding " + winding);
                double area = 0;
                for (int i = 1; i < snapped.Length; i++) area += snapped[i - 1].X * snapped[i].Y - snapped[i].X * snapped[i - 1].Y;
                Check(Math.Sign(area) == winding, "Ellipse winding is preserved " + winding);
            }

            var rectangle = new List<Point2d>();
            var corners = new[] { new Point2d(10, 20), new Point2d(150, 20), new Point2d(150, 110), new Point2d(10, 110), new Point2d(10, 20) };
            for (int edge = 0; edge < 4; edge++)
                for (int j = 0; j < 20; j++) rectangle.Add(corners[edge] + (corners[edge + 1] - corners[edge]) * (j / 20.0));
            rectangle.Add(corners[0]);
            snapped = ShapeRecognition.Recognize(rectangle.ToArray());
            Check(snapped != null && snapped.Length == 65, "Rectangle is recognized");
            Check(snapped[0] == snapped[64] && snapped[16] == corners[1] && snapped[32] == corners[2] && snapped[48] == corners[3], "Rectangle retains four corners and closure");

            var wave = new Point2d[101];
            for (int i = 0; i < wave.Length; i++) wave[i] = new Point2d(i * 2, 45 * Math.Sin(i * .15));
            Check(ShapeRecognition.Recognize(wave) == null, "Freehand wave is left unchanged");
            Check(ShapeRecognition.Recognize(new[] { new Point2d(0, 0), new Point2d(2, 1), new Point2d(4, 0) }) == null, "Tiny gesture is left unchanged");
            Check(ShapeRecognition.Recognize(new[] { new Point2d(5, 5), new Point2d(5, 5), new Point2d(5, 5) }) == null, "Coincident points are safe");
            Console.WriteLine(checks + " geometry checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
