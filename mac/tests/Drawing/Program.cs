// Draws the viewport interface (toolbar, option row, colour picker, tooltip, tick, icons)
// with the plug-in's own painting code and saves the pictures, or compares two folders of
// them. Built twice from this one file (see run.ps1): once as the plug-in is on Windows,
// painting with WPF, and once as it is on a Mac (MAC defined), painting with Eto through
// rhino-plugin/Mac/MacDrawing.cs. Both run on Windows and outside Rhino, so the Mac's
// painting can be looked at, and held against the Windows one, without a Mac.
//
// The painting code is private to the plug-in: it is reached by reflection, and nothing
// in the plug-in is changed for the test. Rhino itself is never started; only RhinoCommon's
// types are loaded, from the installed Rhino 8.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Bitmap = System.Drawing.Bitmap;
#if MAC
using Media = RhinoSketchingTool.MacDrawing;
using Wpf = RhinoSketchingTool.MacDrawing;
#else
using Media = System.Windows.Media;
using Wpf = System.Windows;
#endif

namespace RhinoSketchingTool.DrawingTest
{
    static class Program
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        static string folder;
        static readonly Type toolbarType = typeof(Toolbar);
        static Type Nested(string name) => toolbarType.GetNestedType(name, BindingFlags.NonPublic) ?? throw new MissingMemberException("Toolbar." + name);
        static object Part(string name) => Enum.Parse(Nested("Part"), name);

        [STAThread]
        static int Main(string[] args)
        {
            // RhinoCommon and Eto come from the installed Rhino, wherever that is.
            string rhino = Environment.GetEnvironmentVariable("RHINO_SYSTEM") ?? @"C:\Program Files\Rhino 8\System";
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                foreach (string place in new[] { Path.Combine(rhino, "netcore"), rhino })
                {
                    string file = Path.Combine(place, name.Name + ".dll");
                    if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
                }
                return null;
            };
            try
            {
                if (args.Length >= 3 && args[0] == "--compare")
                {
                    // --compare first second [folder for side-by-side sheets] [--alike name,name,... or *]
                    int alike = Array.IndexOf(args, "--alike");
                    string sheets = args.Length > 3 && args[3] != "--alike" ? args[3] : null;
                    return Compare(args[1], args[2], sheets, alike >= 0 && alike + 1 < args.Length ? args[alike + 1].Split(',') : new string[0]);
                }
                folder = Path.GetFullPath(args.Length > 0 ? args[0] : "out");
                Directory.CreateDirectory(folder);
#if MAC
                // "pixels" as a second argument: hand the pictures over the other of the two ways (see Canvas.Render).
                if (args.Length > 1 && args[1] == "pixels") RhinoSketchingTool.MacDrawing.Canvas.PixelByPixel = true;
#endif
                return Draw();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException.ToString() : ex.ToString());
                return 1;
            }
        }

        static int Draw()
        {
#if MAC
            new Eto.Forms.Application(new Eto.Wpf.Platform()); // Eto draws nothing until it has a platform
#endif
            foreach (bool dark in new[] { false, true })
            {
                typeof(Theme).GetField("dark", Any).SetValue(null, (bool?)dark);
                string look = dark ? "-dark" : "";
                foreach (double scale in dark ? new[] { 2.0 } : new[] { 1.0, 1.5, 2.0 })
                {
                    string at = look + "@" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Icons("icons" + at, scale);
                    CommentPicture("icons-comment" + at, scale);
                    Bar("toolbar" + at, scale, "Pen", "Select", "None", false, false);
                    Options("options-pen" + at, scale, "Draw", "Plane", new[] { "Object", "Plane", "Cursor", "View" }, new[] { "PickFace" }, new[] { "Color" }, new[] { "Snap" });
                    Colours("colours" + at, scale);
                }
                Bar("toolbar-upright" + look, 1.5, "Eraser", "None", "Collapse", true, false);
                Bar("toolbar-folded" + look, 1.5, "Comments", "Collapse", "None", false, true);
                Options("options-pen-face" + look, 1.5, "Draw", "Plane", new[] { "Object", "Plane", "Cursor", "View" }, new[] { "FaceChip" }, new[] { "Color" }, new[] { "Snap" });
                Options("options-pen-cursor" + look, 1.5, "Draw", "Cursor", new[] { "Object", "Plane", "Cursor", "View" }, new[] { "PlaceCursor" }, new[] { "Color" }, new[] { "Snap" });
                Options("options-pen-object" + look, 1.5, "Draw", "Surface", new[] { "Object", "Plane", "Cursor", "View" }, new[] { "Color" }, new[] { "Snap" });
                Options("options-eraser" + look, 1.5, "Erase", "Plane", new[] { "EraseWhole", "ErasePart" });
                Options("options-comment" + look, 1.5, "Comment", "Plane", null, new[] { "Hint" });
                Options("options-views" + look, 1.5, "Views", "Plane", null, new[] { "SaveView" });
                Pieces("pieces" + look, 2);
            }
#if MAC
            var port = new System.Drawing.Size(800, 600);
            if (MacDisplay.FramePixelsPerUnit(port, port, 2) != 1 ||
                MacDisplay.FramePixelsPerUnit(new System.Drawing.Size(1600, 1200), port, 2) != 2 ||
                MacDisplay.FramePixelsPerUnit(new System.Drawing.Size(1600, 600), port, 2) != 1)
                throw new Exception("Retina viewport and framebuffer dimensions were converted incorrectly.");

            // A rapid second click or a tool switch can commit before the dialog opens.
            var results = new List<string>();
            var editor = new CommentEditor { Finished = results.Add };
            editor.Open(null, "Existing comment");
            editor.Commit();
            editor.Open(null, "Next comment");
            editor.Cancel();
            if (!results.SequenceEqual(new[] { "Existing comment", null }) || editor.IsOpen)
                throw new Exception("Closing a pending comment dialog lost text or completed twice.");

            // A comment's picture (CommentImages.cs): made no larger than it is kept, read back
            // from the kept bytes, and handed over as the bitmap Rhino takes, colours intact.
            using (var large = new Eto.Drawing.Bitmap(2000, 1000, Eto.Drawing.PixelFormat.Format32bppRgba))
            {
                using (var graphics = new Eto.Drawing.Graphics(large)) graphics.Clear(Eto.Drawing.Color.FromArgb(100, 149, 237));
                object[] prepared = { large, null, 0, 0 };
                bool kept = (bool)typeof(SketchController).GetMethod("PrepareImage", Any).Invoke(null, prepared);
                var bytes = (byte[])prepared[1];
                if (!kept || (int)prepared[2] != 1280 || (int)prepared[3] != 640 || bytes == null || bytes.Length < 100)
                    throw new Exception("A comment's picture was not made ready for keeping.");
                using (var read = new Eto.Drawing.Bitmap(bytes))
                using (var handed = (Bitmap)typeof(SketchController).GetMethod("ToSystemBitmap", Any).Invoke(null, new object[] { read }))
                {
                    System.Drawing.Color pixel = handed.GetPixel(640, 320);
                    if (handed.Width != 1280 || handed.Height != 640 || Math.Abs(pixel.R - 100) > 3 || Math.Abs(pixel.G - 149) > 3 || Math.Abs(pixel.B - 237) > 3 || pixel.A != 255)
                        throw new Exception("A comment's picture did not come back as it was kept: " + handed.Width + " x " + handed.Height + ", " + pixel);
                }
                Console.WriteLine("A comment's picture: 2000 x 1000 kept as 1280 x 640 (" + bytes.Length + " bytes), read back and handed over intact.");
            }

            bool strokes = Strokes("strokes");
#else
            bool strokes = true; // on Windows a stroke is painted with Rhino's sprites, which only Rhino draws
#endif
            Console.WriteLine("Pictures are in " + folder);
            return strokes ? 0 : 1;
        }

        static void Save(string name, Bitmap bitmap)
        {
            using (bitmap) bitmap.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        }

        // ---------------------------------------------------------- the scenes

        // Every icon the toolbar has, large, and the chevron turned each way.
        static void Icons(string name, double scale)
        {
            Type icons = Nested("Icons");
            MethodInfo paintIcon = toolbarType.GetMethod("PaintIcon", Any);
            FieldInfo[] all = icons.GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType.IsArray).OrderBy(field => field.Name).ToArray();
            const int Across = 6, Cell = 76;
            double width = Across * Cell, height = ((all.Length + Across - 1) / Across + 2) * Cell;
            Save(name, Picture.Render(width, height, scale, context =>
            {
                context.DrawRectangle(Picture.Fill(Picture.Glass), null, new Wpf.Rect(0, 0, width, height));
                for (int i = 0; i < all.Length; i++)
                {
                    var centre = new Wpf.Point(Cell / 2 + i % Across * Cell, Cell / 2 + i / Across * Cell);
                    paintIcon.Invoke(null, new object[] { context, all[i].GetValue(null), centre, 60.0, Picture.Ink, 1.0, 0.0 });
                }
                // As the toolbar shows them: small, and the chevron pointing each way it can.
                object chevron = icons.GetField("Chevron").GetValue(null);
                double row = ((all.Length + Across - 1) / Across) * Cell + Cell / 2;
                double[] turns = { 0, 90, 180, -90 };
                for (int i = 0; i < turns.Length; i++)
                    paintIcon.Invoke(null, new object[] { context, chevron, new Wpf.Point(Cell / 2 + i * Cell, row), 18.0, Picture.Ink2, 1.0, turns[i] });
                for (int i = 0; i < all.Length; i++)
                    paintIcon.Invoke(null, new object[] { context, all[i].GetValue(null), new Wpf.Point(20 + i * 34, row + Cell), 20.0, i % 2 == 0 ? Picture.Ink : Picture.Accent, i % 3 == 0 ? 0.55 : 1.0, 0.0 });
            }));
        }

        // The signs on a comment's picture (larger, smaller, take it off) and the picture button
        // on its label, painted by the plug-in's own code (CommentImages.cs): large, and then
        // as they show, white on a dark square 18 across, and the picture button at 14.
        static void CommentPicture(string name, double scale)
        {
            Type controller = typeof(SketchController);
            MethodInfo glyph = controller.GetMethod("PaintGlyph", Any), add = controller.GetMethod("PaintAddImage", Any);
            string[] signs = { "LargerGlyph", "SmallerGlyph", "RemoveGlyph" };
            var picture = (string[])controller.GetField("PictureGlyph", Any).GetValue(null);
            const int Cell = 76;
            double width = 4 * Cell, height = 2 * Cell;
            Save(name, Picture.Render(width, height, scale, context =>
            {
                context.DrawRectangle(Picture.Fill(Picture.Glass), null, new Wpf.Rect(0, 0, width, height));
                for (int i = 0; i < signs.Length; i++)
                {
                    var path = (string)controller.GetField(signs[i], Any).GetRawConstantValue();
                    glyph.Invoke(null, new object[] { context, path, i * Cell + 8.0, 8.0, 60.0, Picture.Ink, 1.8 });
                    var box = new Wpf.Rect(i * Cell + 29, Cell + 29, 18, 18);
                    context.DrawRoundedRectangle(Picture.Fill(Picture.TipBack, 0.8), null, box, 5.5, 5.5);
                    glyph.Invoke(null, new object[] { context, path, box.X + 4, box.Y + 4, 10.0, Picture.Rgb(255, 255, 255), 1.7 });
                }
                foreach (string part in picture)
                    glyph.Invoke(null, new object[] { context, part, 3 * Cell + 8.0, 8.0, 60.0, Picture.Ink, 1.8 });
                add.Invoke(null, new object[] { context, new Wpf.Rect(3 * Cell + 27, Cell + 27, 22, 21) });
            }));
        }

        // The toolbar itself, exactly as Toolbar.DrawForeground has it painted.
        static void Bar(string name, double scale, string active, string hover, string pressed, bool upright, bool collapsed)
        {
            var controller = Controller(active == "Pen" ? "Draw" : active == "Eraser" ? "Erase" : active == "Comments" ? "Comment" : active == "Views" ? "Views" : "Select", "Plane");
            Toolbar bar = controller.Toolbar;
            toolbarType.GetField("dock", Any).SetValue(bar, upright ? ToolbarDock.Left : ToolbarDock.Top);
            toolbarType.GetField("collapsed", Any).SetValue(bar, collapsed);

            var arguments = new object[] { null };
            object items = toolbarType.GetMethod("Layout", Any).Invoke(bar, arguments);
            object size = arguments[0];

            Type lookType = Nested("Look");
            object look = Activator.CreateInstance(lookType);
            lookType.GetField("Upright").SetValue(look, upright);
            lookType.GetField("Collapsed").SetValue(look, collapsed);
            lookType.GetField("Dark").SetValue(look, Theme.Dark);
            lookType.GetField("Active").SetValue(look, Part(active));
            lookType.GetField("Hover").SetValue(look, Part(hover));
            lookType.GetField("Pressed").SetValue(look, Part(pressed));
            lookType.GetField("Scale").SetValue(look, (int)Math.Round(scale * 1000));

            MethodInfo paint = toolbarType.GetMethods(Any).Single(method => method.Name == "Paint" && method.GetParameters().Length == 4);
            Save(name, (Bitmap)paint.Invoke(null, new object[] { items, size, look, scale }));
        }

        // The option row under the toolbar. The parts are set out here the way Toolbar.OptionsFrame
        // sets them out in a wide viewport (that needs a Rhino view to ask); the painting is the plug-in's.
        static void Options(string name, double scale, string tool, string method, string[] segmented, params string[][] groups)
        {
            var controller = Controller(tool, method);
            Toolbar bar = controller.Toolbar;
            Type itemType = Nested("Item"), frameType = Nested("Frame");
            MethodInfo optionWidth = toolbarType.GetMethod("OptionWidth", Any);
            var noViews = new List<string>();

            var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
            var wells = new List<Wpf.Rect>();
            var dividers = new List<Wpf.Point>();
            const double GroupGap = 6, SegPad = 3, ButtonHeight = 30, SegButtonHeight = 28;
            double sectionGap = tool == "Draw" ? (double)toolbarType.GetField("PenSectionGap", Any).GetRawConstantValue() : GroupGap;
            double x = 5, y = 5;
            Action<string, Wpf.Rect> add = (part, box) =>
            {
                object item = Activator.CreateInstance(itemType);
                itemType.GetField("Part").SetValue(item, Part(part));
                itemType.GetField("Box").SetValue(item, box);
                items.Add(item);
            };
            Func<string, double> widthOf = part => (double)optionWidth.Invoke(bar, new object[] { Part(part), noViews });

            if (segmented != null)
            {
                double groupWidth = 2 * SegPad + (segmented.Length - 1) * 2 + segmented.Sum(widthOf);
                wells.Add(new Wpf.Rect(x, y, groupWidth, SegButtonHeight + 2 * SegPad));
                double bx = x + SegPad;
                foreach (string part in segmented)
                {
                    double w = widthOf(part);
                    add(part, new Wpf.Rect(bx, y + SegPad, w, SegButtonHeight));
                    bx += w + 2;
                }
                x += groupWidth;
            }
            foreach (string[] group in groups)
            {
                if (x > 5)
                {
                    if (tool == "Draw") dividers.Add(new Wpf.Point(x + sectionGap / 2, y + SegPad + SegButtonHeight / 2));
                    x += sectionGap;
                }
                double bx = x;
                foreach (string part in group)
                {
                    double w = widthOf(part);
                    add(part, new Wpf.Rect(bx, y + 2, w, ButtonHeight));
                    bx += w + GroupGap;
                }
                x = bx - GroupGap;
            }

            object frame = Activator.CreateInstance(frameType);
            frameType.GetField("Scale").SetValue(frame, scale);
            frameType.GetField("Items").SetValue(frame, items);
            frameType.GetField("Wells").SetValue(frame, wells);
            frameType.GetField("SectionDividers").SetValue(frame, dividers);
            frameType.GetField("OptionsSize").SetValue(frame, new Wpf.Size(x + 5, y + SegButtonHeight + 2 * SegPad + 5));
            frameType.GetField("HasOptions").SetValue(frame, true);
            frameType.GetField("Views").SetValue(frame, noViews);

            toolbarType.GetField("hover", Any).SetValue(bar, Part(groups.Length > 0 ? groups[groups.Length - 1][0] : "None")); // the last one, as under the pointer
            Save(name, (Bitmap)toolbarType.GetMethod("RenderOptions", Any).Invoke(bar, new object[] { frame }));
        }

        // The colour picker: swatches, the saturation and value square, the hue bar, the hex field.
        static void Colours(string name, double scale)
        {
            var controller = Controller("Draw", "Plane");
            Toolbar bar = controller.Toolbar;
            Type frameType = Nested("Frame"), popupType = Nested("OptionsPopup");
            toolbarType.GetField("optionsPopup", Any).SetValue(bar, Enum.Parse(popupType, "Colors"));
            toolbarType.GetField("hue", Any).SetValue(bar, 285.0);
            toolbarType.GetField("saturation", Any).SetValue(bar, 0.72);
            toolbarType.GetField("value", Any).SetValue(bar, 0.69);

            object frame = Activator.CreateInstance(frameType);
            frameType.GetField("Scale").SetValue(frame, scale);
            frameType.GetField("PopupSize").SetValue(frame, new Wpf.Size(284, 302));
            Save(name, (Bitmap)toolbarType.GetMethod("RenderPopup", Any).Invoke(bar, new object[] { frame }));
        }

        // The smaller things Picture paints: tooltips, the tick, cards, and text of every kind used.
        static void Pieces(string name, double scale)
        {
            const double Width = 420, Height = 330;
            Save(name, Picture.Render(Width, Height, scale, context =>
            {
                context.DrawRectangle(Picture.Fill(Picture.Well), null, new Wpf.Rect(0, 0, Width, Height));
                Picture.Tip(context, "Drag to move · double-click to reset", new Wpf.Point(140, 12), false);
                Picture.Tip(context, "Pen colour", new Wpf.Point(330, 40), true);
                Picture.Tick(context, new Wpf.Point(20, 56), true);
                Picture.Tick(context, new Wpf.Point(50, 56), false);

                // A comment's label, as SketchController.LabelFor paints one (that needs Rhino for its bitmap).
                var pill = new Wpf.Rect(90, 52, 190, 25);
                var anchor = new Wpf.Point(185, 52 + 25 + 10);
                context.DrawEllipse(Picture.Fill(Picture.Rgb(255, 255, 255)), null, anchor, 4.5, 4.5);
                context.DrawEllipse(Picture.Fill(Picture.Accent), null, anchor, 3, 3);
                Picture.Card(context, pill, 12.5);
                var disc = new Wpf.Point(pill.X + 3 + 9.5, pill.Y + 12.5);
                context.DrawEllipse(Picture.Fill(Picture.Accent), null, disc, 9.5, 9.5);
                var number = Picture.Text("12", 10.5, Wpf.FontWeights.Bold, Picture.Rgb(255, 255, 255));
                context.DrawText(number, new Wpf.Point(disc.X - number.Width / 2, disc.Y - number.Height / 2));
                var words = Picture.Text("Move this wall out by a metre", 11.5, Wpf.FontWeights.SemiBold, Picture.Ink, 150);
                context.DrawText(words, new Wpf.Point(pill.X + 3 + 19 + 6, pill.Y + (25 - words.Height) / 2));
                var tag = new Wpf.Rect(300, 55, 100, 19);
                context.DrawRoundedRectangle(Picture.Fill(Picture.Rgb(0x16, 0x17, 0x1b)), null, tag, 8, 8);
                var kind = Picture.Text("Layer", 10.5, Wpf.FontWeights.Medium, Picture.Rgb(255, 255, 255));
                context.PushOpacity(0.55);
                context.DrawText(kind, new Wpf.Point(tag.X + 7, tag.Y + (19 - kind.Height) / 2));
                context.Pop();
                var layer = Picture.Text("Walls", 10.5, Wpf.FontWeights.SemiBold, Picture.Rgb(255, 255, 255), 220);
                context.DrawText(layer, new Wpf.Point(tag.X + 7 + kind.WidthIncludingTrailingWhitespace + 5, tag.Y + (19 - layer.Height) / 2));

                // Text: each weight and size in use, with the box its measure says it fills, and one cut short.
                double y = 110;
                foreach (var sample in new[]
                {
                    Tuple.Create("Object  Plane  3D cursor  Over view", 12.0, Wpf.FontWeights.Medium, 0.0),
                    Tuple.Create("Snap shapes · Whole sketch · Part", 12.0, Wpf.FontWeights.Normal, 0.0),
                    Tuple.Create("On a face  #9731B0", 13.0, Wpf.FontWeights.SemiBold, 0.0),
                    Tuple.Create("A saved view with a name far too long to fit the list", 12.0, Wpf.FontWeights.Medium, 180.0),
                    Tuple.Create("Comment 12", 10.5, Wpf.FontWeights.Bold, 0.0),
                })
                {
                    var text = Picture.Text(sample.Item1, sample.Item2, sample.Item3, Picture.Ink, sample.Item4);
                    context.DrawRectangle(Picture.Fill(Picture.Accent, 0.12), null, new Wpf.Rect(20, y, text.WidthIncludingTrailingWhitespace, text.Height));
                    context.DrawText(text, new Wpf.Point(20, y));
                    y += text.Height + 8;
                }

                // Rounded boxes whose corners are rounder than the box is tall, hairlines, and a clipped fade.
                context.DrawRoundedRectangle(Picture.Fill(Picture.Glass), new Media.Pen(Picture.Fill(Picture.Shade, 0.3), 1), new Wpf.Rect(20.5, y + 0.5, 120, 18), 30, 30);
                context.DrawLine(new Media.Pen(Picture.Fill(Picture.Shade, 0.5), 1), new Wpf.Point(160, y + 9.5), new Wpf.Point(260, y + 9.5));
                context.DrawLine(new Media.Pen(Picture.Fill(Picture.Accent), 3) { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round }, new Wpf.Point(280, y + 9), new Wpf.Point(380, y + 9));
                var fade = new Wpf.Rect(20, y + 30, 380, 40);
                context.PushClip(new Media.RectangleGeometry(fade, 12, 12));
                context.DrawRectangle(new Media.LinearGradientBrush(Media.Colors.White, Picture.Accent, 0), null, fade);
                context.DrawRectangle(new Media.LinearGradientBrush(Media.Colors.Transparent, Media.Colors.Black, 90), null, fade);
                context.Pop();
                context.DrawEllipse(null, new Media.Pen(Picture.Fill(Picture.Rgb(255, 255, 255)), 2), new Wpf.Point(300, y + 50), 7, 7);
            }));
        }

        // A controller that has never met Rhino: its fields are set directly, because its
        // properties tell Rhino's views to redraw.
        static SketchController Controller(string tool, string method)
        {
            var controller = new SketchController();
            Type type = typeof(SketchController);
            type.GetField("tool", Any).SetValue(controller, Enum.Parse(typeof(SketchTool), tool));
            type.GetField("method", Any).SetValue(controller, Enum.Parse(typeof(DrawMethod), method));
            type.GetField("snapShapes", Any).SetValue(controller, true);
            return controller;
        }

#if MAC
        // --------------------------------------------------------- strokes

        // On a Mac a stroke is painted with round points, "discs": Rhino for Mac draws none of the
        // sprites the Windows build paints with (see StrokeDabs in Brush.cs). Rhino lays the discs
        // down there. Here a stand-in does: it takes the discs the plug-in builds a stroke into and
        // paints each the way Rhino's display is asked to, a disc of that ink and that width over
        // what is there already. So what is checked is the plug-in's part: that its discs add up
        // to the stroke the brush asked for, at the size it was drawn and from further off.
        static bool Strokes(string name)
        {
            const double Drawn = 20; // the pixels a model unit covered when these strokes were drawn
            var black = System.Drawing.Color.FromArgb(24, 26, 31);
            bool good = true;

            Console.WriteLine();
            Console.WriteLine("Strokes as the Mac build paints them. How solid the middle of a stroke comes out, against what was asked:");
            Console.WriteLine("{0,7} {1,7} {2,7} {3,7} {4,9} {5,8}", "width", "asked", "kinds", "discs", "painted", "uneven");
            foreach (double width in new[] { 2.0, 4, 8, 16, 40 })
                foreach (double opacity in new[] { 0.1, 0.3, 0.6, 0.8, 1.0 })
                {
                    StrokeDabs dabs = Stroke(81, t => new Wpf.Point(20 + 320 * t, 0), t => width, t => 1, opacity, 0.6, black, Drawn);
                    var sheet = new Sheet(360, (int)width + 21 | 1); // an odd height: the stroke's middle is the middle of a row of pixels
                    Paint(sheet, dabs, Drawn, 0, sheet.Height / 2 + 0.5);
                    double sum = 0, squares = 0;
                    int from = 100, to = 260, row = sheet.Height / 2; // clear of the stroke's ends
                    for (int x = from; x < to; x++)
                    {
                        double ink = sheet.Ink[row * sheet.Width + x];
                        sum += ink;
                        squares += ink * ink;
                    }
                    double painted = sum / (to - from), uneven = Math.Sqrt(Math.Max(0, squares / (to - from) - painted * painted));
                    double asked = Math.Min(0.995, opacity);
                    // A stroke only a few pixels wide is not as solid across a whole pixel as along its very middle; wider ones are held to it.
                    bool held = width >= 8, right = Math.Abs(painted - asked) <= 0.05 && uneven <= 0.05;
                    if (held && !right) good = false;
                    Console.WriteLine("{0,7:0.#} {1,7:0.000} {2,7} {3,7} {4,9:0.000} {5,8:0.000}{6}", width, asked, Kinds(dabs).Length, Discs(dabs), painted, uneven, held ? (right ? "  as asked" : "  NOT AS ASKED") : "");
                }

            Console.WriteLine();
            Console.WriteLine("The same from further off. The ink a stroke leaves for each pixel of its length, against a stroke that wide and that solid:");
            Console.WriteLine("{0,7} {1,7} {2,10} {3,9} {4,9} {5,7}", "width", "asked", "on screen", "ink", "ideal", "ratio");
            foreach (double width in new[] { 2.0, 8, 24 })
                foreach (double opacity in new[] { 0.3, 0.8, 1.0 })
                    foreach (double zoom in new[] { 1, 0.5, 0.25, 0.1 })
                    {
                        // A long stroke, so that from far off there are still discs enough to count.
                        StrokeDabs dabs = Stroke(601, t => new Wpf.Point(20 + 2400 * t, 0), t => width, t => 1, opacity, 0.6, black, Drawn);
                        var sheet = new Sheet((int)(2440 * zoom) + 8, (int)(width * zoom) + 21 | 1);
                        Paint(sheet, dabs, Drawn * zoom, 4, sheet.Height / 2 + 0.5);
                        double ink = sheet.Ink.Sum() / (2400 * zoom), ideal = Math.Min(0.995, opacity) * width * zoom, ratio = ink / ideal;
                        // Discs leave a little less ink than a band with square ends and edges would; far off, where they
                        // cannot be drawn small enough, somewhat more. Neither nothing, nor a solid line whatever was asked.
                        bool right = ratio >= 0.5 && ratio <= 3.5;
                        if (!right) good = false;
                        Console.WriteLine("{0,7:0.#} {1,7:0.000} {2,10:0.00} {3,9:0.000} {4,9:0.000} {5,7:0.00}{6}", width, Math.Min(0.995, opacity), width * zoom, ink, ideal, ratio, right ? "" : "  OUT OF PROPORTION");
                    }

            // One stroke changes from soft to hard. Its centre should keep the same
            // opacity, while the hard end lays down visibly more ink near the edge.
            var changing = new Sheet(360, 51);
            Paint(changing, Stroke(81, t => new Wpf.Point(20 + 320 * t, 0), t => 40, t => 1,
                0.8, 1, black, Drawn, t => t < 0.5 ? 0 : 1), Drawn, 0, 25.5);
            double RowMean(int x, int y) => changing.Ink.Skip(y * changing.Width + x).Take(64).Average();
            double softCentre = RowMean(52, 25), hardCentre = RowMean(244, 25);
            double softEdge = RowMean(52, 40), hardEdge = RowMean(244, 40);
            bool hardnessWorks = Math.Abs(softCentre - 0.8) < 0.05 && Math.Abs(hardCentre - 0.8) < 0.05 && hardEdge - softEdge > 0.25;
            Console.WriteLine("Brush hardness, soft to hard: centre {0:0.000}/{1:0.000}, edge {2:0.000}/{3:0.000}: {4}",
                softCentre, hardCentre, softEdge, hardEdge, hardnessWorks ? "pass" : "FAIL");
            good &= hardnessWorks;

            // Each kind of disc in a stroke is one call to Rhino for every frame: how many a stroke drawn by hand takes.
            Console.WriteLine();
            Console.WriteLine("Strokes pressed harder in the middle, as a hand draws them (each kind is one call to Rhino per frame):");
            Console.WriteLine("{0,7} {1,7} {2,7}", "width", "kinds", "discs");
            foreach (double size in new[] { 2.0, 5, 10, 22, 40 })
            {
                StrokeDabs dabs = Pressed(size, 0, black, Drawn);
                Console.WriteLine("{0,7:0.#} {1,7} {2,7}", size, Kinds(dabs).Length, Discs(dabs));
                if (Kinds(dabs).Length > 150) { good = false; Console.WriteLine("        TOO MANY KINDS"); }
            }

            // And a page of them to look at: at the size they were drawn, and from further off.
            var page = new Sheet(960, 560);
            Page(page, Drawn, 0, 0);
            Page(page, Drawn * 0.4, 600, 10);
            Page(page, Drawn * 0.15, 600, 260);
            Page(page, Drawn * 0.05, 760, 260);
            Save(name, page.Picture());
            Console.WriteLine();
            Console.WriteLine(good ? "The Mac build's strokes come out as asked." : "The Mac build's strokes do NOT come out as asked: see the lines marked above.");
            return good;
        }

        // How hard a stroke drawn by hand is pressed, from its start (0) to its end (1): hardest in the middle.
        static double Press(double t) => Math.Pow(Math.Sin(Math.PI * t), 0.7);

        // A stroke as a hand draws it, 500 pixels long: wider and stronger where it is pressed harder.
        static StrokeDabs Pressed(double size, double row, System.Drawing.Color ink, double drawn) =>
            Stroke(121, t => new Wpf.Point(30 + 500 * t, row + 12 * Math.Sin(t * 7)), t => size * (0.25 + 0.75 * Press(t)), t => 0.3 + 0.7 * Press(t), 0.8, 0.6, ink, drawn);

        static void Page(Sheet sheet, double pixels, double left, double top)
        {
            const double Drawn = 20;
            Func<double, double> press = Press;
            var inks = new[] { System.Drawing.Color.FromArgb(24, 26, 31), System.Drawing.Color.FromArgb(0x97, 0x31, 0xB0), System.Drawing.Color.FromArgb(0x1F, 0x6F, 0xEB), System.Drawing.Color.FromArgb(0xD9, 0x3A, 0x26) };
            double[] sizes = { 2, 5, 10, 22, 40 };
            double y = 30;
            for (int i = 0; i < sizes.Length; i++)
            {
                Paint(sheet, Pressed(sizes[i], y, inks[i % inks.Length], Drawn), pixels, left, top);
                y += sizes[i] + 34;
            }
            // Even strokes, from faint to solid, and a wide faint one laid across them.
            double[] strengths = { 0.1, 0.3, 0.6, 1.0 };
            for (int i = 0; i < strengths.Length; i++)
            {
                double row = y + i * 26;
                Paint(sheet, Stroke(61, t => new Wpf.Point(30 + 500 * t, row), t => 14, t => 1, strengths[i], 0.6, inks[0], Drawn), pixels, left, top);
            }
            Paint(sheet, Stroke(61, t => new Wpf.Point(120 + 300 * t, y - 20 + 130 * t), t => 30, t => 1, 0.45, 0.6, inks[3], Drawn), pixels, left, top);
            // A quick loop, as a pen makes it.
            double centreY = y + 170;
            Paint(sheet, Stroke(241, t => new Wpf.Point(120 + 330 * t + 46 * Math.Cos(t * 22), centreY + 40 * Math.Sin(t * 22)), t => 6 * (0.35 + 0.65 * press(t)), t => 0.4 + 0.6 * press(t), 0.8, 0.6, inks[2], Drawn), pixels, left, top);
        }

        // A stroke through "count" points. Where it goes and how wide it is are given in pixels as it was
        // drawn, for t from 0 at its start to 1 at its end, with "drawn" pixels to a model unit.
        static StrokeDabs Stroke(int count, Func<double, Wpf.Point> place, Func<double, double> width, Func<double, double> flow, double opacity, double hardness, System.Drawing.Color ink, double drawn, Func<double, double> hardnessAt = null)
        {
            var points = new Rhino.Geometry.Point3d[count];
            var paint = new StrokePaint { Opacity = opacity, Hardness = hardness, PixelsPerUnit = drawn, Widths = new double[count], Flows = new double[count] };
            if (hardnessAt != null) paint.Hardnesses = new double[count];
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / (count - 1);
                Wpf.Point at = place(t);
                points[i] = new Rhino.Geometry.Point3d(at.X / drawn, at.Y / drawn, 0);
                paint.Widths[i] = width(t) / drawn;
                paint.Flows[i] = flow(t);
                if (hardnessAt != null) paint.Hardnesses[i] = hardnessAt(t);
            }
            return StrokeDabs.Build(points, paint, ink);
        }

        static Array Kinds(StrokeDabs dabs) => dabs == null ? new object[0] : (Array)typeof(StrokeDabs).GetField("kinds", Any).GetValue(dabs);

        static int Discs(StrokeDabs dabs)
        {
            int discs = 0;
            foreach (object kind in Kinds(dabs)) discs += ((Rhino.Geometry.Point3d[])kind.GetType().GetField("Places").GetValue(kind)).Length;
            return discs;
        }

        // Paints a stroke's discs as StrokeDabs.PaintDiscs has Rhino paint them in a parallel view, where
        // "pixels" is what a model unit covers on screen: each kind at its width, and where that is under
        // a pixel, a pixel wide and thinned out by the plug-in's own rule (StrokeDabs.Kept).
        static void Paint(Sheet sheet, StrokeDabs dabs, double pixels, double left, double top)
        {
            var kept = (Func<double, double, bool>)typeof(StrokeDabs).GetMethod("Kept", Any).CreateDelegate(typeof(Func<double, double, bool>));
            foreach (object kind in Kinds(dabs))
            {
                var places = (Rhino.Geometry.Point3d[])kind.GetType().GetField("Places").GetValue(kind);
                var turns = (float[])kind.GetType().GetField("Turns").GetValue(kind);
                var ink = (System.Drawing.Color)kind.GetType().GetField("Ink").GetValue(kind);
                double width = (float)kind.GetType().GetField("Width").GetValue(kind) * pixels;
                for (int i = 0; i < places.Length; i++)
                    if (kept(turns[i], width)) sheet.Disc(left + places[i].X * pixels, top + places[i].Y * pixels, Math.Max(1, width), ink);
            }
        }

        // Paper, and discs of ink laid on it one over another.
        sealed class Sheet
        {
            public readonly int Width, Height;
            public readonly double[] Ink;       // how much of each pixel is covered, 0 to 1
            readonly double[] red, green, blue; // what it looks like, on white

            public Sheet(int width, int height)
            {
                Width = width;
                Height = height;
                Ink = new double[width * height];
                red = new double[width * height];
                green = new double[width * height];
                blue = new double[width * height];
                for (int i = 0; i < red.Length; i++) red[i] = green[i] = blue[i] = 1;
            }

            public void Disc(double x, double y, double diameter, System.Drawing.Color ink)
            {
                double radius = diameter / 2, alpha = ink.A / 255.0;
                int left = Math.Max(0, (int)Math.Floor(x - radius)), right = Math.Min(Width - 1, (int)Math.Floor(x + radius));
                int top = Math.Max(0, (int)Math.Floor(y - radius)), bottom = Math.Min(Height - 1, (int)Math.Floor(y + radius));
                for (int py = top; py <= bottom; py++)
                    for (int px = left; px <= right; px++)
                    {
                        // The share of this pixel that is inside the disc: all, none, or counted out at the disc's edge.
                        double dx = px + 0.5 - x, dy = py + 0.5 - y, distance = Math.Sqrt(dx * dx + dy * dy), share = 1;
                        if (distance >= radius + 0.71) continue;
                        if (distance > radius - 0.71)
                        {
                            int inside = 0;
                            for (int sy = 0; sy < 4; sy++)
                                for (int sx = 0; sx < 4; sx++)
                                {
                                    double ex = px + (sx + 0.5) / 4 - x, ey = py + (sy + 0.5) / 4 - y;
                                    if (ex * ex + ey * ey <= radius * radius) inside++;
                                }
                            if (inside == 0) continue;
                            share = inside / 16.0;
                        }
                        double a = alpha * share;
                        int i = py * Width + px;
                        Ink[i] += (1 - Ink[i]) * a;
                        red[i] += (ink.R / 255.0 - red[i]) * a;
                        green[i] += (ink.G / 255.0 - green[i]) * a;
                        blue[i] += (ink.B / 255.0 - blue[i]) * a;
                    }
            }

            public Bitmap Picture()
            {
                var pixels = new int[Width * Height];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = unchecked((int)0xFF000000) | (Byte(red[i]) << 16) | (Byte(green[i]) << 8) | Byte(blue[i]);
                return Bitmap32(pixels, Width, Height);
            }

            static int Byte(double value) => Math.Max(0, Math.Min(255, (int)Math.Round(value * 255)));
        }
#endif

        // ------------------------------------------------------- comparing

        // Holds each picture in one folder against its namesake in another: sizes, and how far
        // the pixels are apart. With a third folder, writes the two side by side with their difference.
        // Pictures whose names begin with one of "alike" (or all, for "*") have to be the same picture
        // to the eye: the same size, and no more than a trace of difference. The others are only reported:
        // text is set in another font on a Mac, so pictures with text in them are expected to differ.
        static int Compare(string first, string second, string sideBySide, string[] alike)
        {
            if (sideBySide != null) Directory.CreateDirectory(sideBySide);
            int failures = 0;
            Console.WriteLine("{0,-28} {1,-11} {2,-11} {3,8} {4,8} {5,8}", "picture", "first", "second", "mean", "over 32", "over 96");
            foreach (string path in Directory.GetFiles(first, "*.png").OrderBy(p => p))
            {
                string name = Path.GetFileName(path), other = Path.Combine(second, name);
                if (!File.Exists(other)) { Console.WriteLine("{0,-28} missing from the second folder", name); failures++; continue; }
                using (var a = new Bitmap(path))
                using (var b = new Bitmap(other))
                {
                    int width = Math.Min(a.Width, b.Width), height = Math.Min(a.Height, b.Height);
                    int[] one = Pixels(a), two = Pixels(b);
                    double total = 0;
                    int over32 = 0, over96 = 0;
                    var difference = new int[width * height];
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            int p = one[y * a.Width + x], q = two[y * b.Width + x];
                            // As they would look over white: a clear pixel and a white one are the same picture.
                            int worst = 0, sum = 0;
                            for (int shift = 0; shift < 24; shift += 8)
                            {
                                int pa = (p >> 24) & 255, qa = (q >> 24) & 255;
                                int pc = 255 - pa + ((p >> shift) & 255) * pa / 255, qc = 255 - qa + ((q >> shift) & 255) * qa / 255;
                                int d = Math.Abs(pc - qc);
                                worst = Math.Max(worst, d);
                                sum += d;
                            }
                            total += sum / 3.0;
                            if (worst > 32) over32++;
                            if (worst > 96) over96++;
                            int shade = 255 - Math.Min(255, worst * 3);
                            difference[y * width + x] = unchecked((int)0xFF000000) | (255 << 16) | (shade << 8) | shade;
                        }
                    double mean = total / (width * height), share32 = 100.0 * over32 / (width * height), share96 = 100.0 * over96 / (width * height);
                    bool sameSize = a.Width == b.Width && a.Height == b.Height;
                    bool held = alike.Any(prefix => prefix == "*" || name.StartsWith(prefix, StringComparison.Ordinal));
                    bool same = sameSize && mean < 0.5 && share96 < 0.1;
                    if (held && !same) failures++;
                    Console.WriteLine("{0,-28} {1,-11} {2,-11} {3,8:0.00} {4,7:0.00}% {5,7:0.00}%{6}{7}", name, a.Width + "x" + a.Height, b.Width + "x" + b.Height, mean, share32, share96,
                        sameSize ? "" : "  sizes differ", held ? (same ? "  same" : "  SHOULD BE THE SAME") : "");
                    if (sideBySide != null)
                    {
                        using (var sheet = new Bitmap(a.Width + b.Width + width + 40, Math.Max(a.Height, b.Height) + 20, PixelFormat.Format32bppArgb))
                        using (var graphics = System.Drawing.Graphics.FromImage(sheet))
                        using (var gap = Bitmap32(difference, width, height))
                        {
                            graphics.Clear(System.Drawing.Color.FromArgb(200, 203, 208));
                            // Pixel for pixel, whatever resolution each file says it has.
                            graphics.DrawImage(a, new System.Drawing.Rectangle(10, 10, a.Width, a.Height));
                            graphics.DrawImage(b, new System.Drawing.Rectangle(a.Width + 20, 10, b.Width, b.Height));
                            graphics.DrawImage(gap, new System.Drawing.Rectangle(a.Width + b.Width + 30, 10, width, height));
                            sheet.Save(Path.Combine(sideBySide, name), ImageFormat.Png);
                        }
                    }
                }
            }
            return failures == 0 ? 0 : 1;
        }

        static int[] Pixels(Bitmap bitmap)
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int row = 0; row < bitmap.Height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * bitmap.Width, bitmap.Width);
            bitmap.UnlockBits(data);
            return pixels;
        }

        static Bitmap Bitmap32(int[] pixels, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int row = 0; row < height; row++) Marshal.Copy(pixels, row * width, data.Scan0 + row * data.Stride, width);
            bitmap.UnlockBits(data);
            return bitmap;
        }
    }
}
