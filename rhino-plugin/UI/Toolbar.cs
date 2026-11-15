using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Display;
using Imaging = System.Windows.Media.Imaging;
using Media = System.Windows.Media;
using ScreenPoint = System.Drawing.Point;
using Wpf = System.Windows;

namespace RhinoSketchingTool
{
    /// <summary>Where the toolbar sits over the viewport. On the two sides it stands upright.</summary>
    internal enum ToolbarDock { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight, Floating }

    /// <summary>
    /// The toolbar over the active viewport: Select, Pen, Eraser, Comments and Saved views,
    /// a grip to move it by and a chevron to fold it away. It is there only while Crayon
    /// is on. (Look and sizes follow design/specs/components.json, "toolbar", made lower.)
    ///
    /// It is not a window. It is a picture painted over the viewport, and SketchMouse
    /// hands it the clicks that land on it. So it is always exactly where the viewport is,
    /// never takes the keyboard from Rhino, and a pen works on it as it does for drawing.
    ///
    /// Dragging the grip moves it between eight places: the bar goes to whichever is
    /// nearest the pointer. Double-clicking the grip puts it back at the top centre.
    /// Where it is, and whether it is folded, is remembered between sessions.
    /// </summary>
    internal sealed partial class Toolbar : DisplayConduit
    {
        enum Part { None, Bar, Grip, Select, Pen, Eraser, Comments, Views, Separator, Collapse,
            Object, Plane, Cursor, View, ViewList, Color, Snap, Popup, Saturation, Hue, Hex, Swatch0, Swatch1, Swatch2, Swatch3, Swatch4,
            PickFace, ClearSketches, EraseWhole, ErasePart, SelectedCount, ClearSelection, Help, SaveView, SavedView, SyncViews }

        // Sizes, in pixels at 100 % display scaling.
        const double Thickness = 56;       // design toolbar height
        const double Padding = 6;
        const double ButtonLength = 44;    // design tool button
        const double GripLength = 14, SeparatorLength = 9, ChevronLength = 24;
        const double IconSize = 20, ChevronSize = 15;
        const double Radius = 16, ButtonRadius = 11;
        const double DotSize = 4;          // the dot that marks the tool in use
        const double Inset = 14;           // design edge inset
        const double Margin = 8;           // room around the bar, in its picture, for the shadow
        const double TitleClear = 24;      // top left: below the viewport's title
        const double AxesClear = 56;       // bottom left: beside the world axes icon

        // design/tokens/tokens.json
        static Media.Color Ink => Picture.Ink;
        static Media.Color Ink2 => Picture.Ink2;
        static Media.Color Muted => Picture.Muted;
        static Media.Color Glass => Picture.Glass;
        static Media.Color Shade => Picture.Overlay;
        static Media.Color Accent => Picture.Accent;
        const double HoverShade = 0.045, PressShade = 0.08, LineShade = 0.14;

        readonly SketchController controller;

        ToolbarDock dock = ToolbarDock.Bottom;
        double floatingX = .5, floatingY = .8;
        bool savedViewsTool;
        readonly Eto.Forms.UITimer motionTimer;
        Part lastAnimated = Part.None, previousAnimated = Part.None;
        DateTime motionStarted, cameraSaved;
        bool collapsed;
        bool remembered; // dock and collapsed have been read from the plug-in's settings
        bool broken;     // painting failed once: the bar stays away rather than fail every frame

        Part hover = Part.None, pressed = Part.None;
        bool dragging;

        DisplayBitmap picture; // the bar as it was last painted,
        Look painted;          // and what it showed

        // Everything the bar's picture depends on.
        struct Look
        {
            public bool Upright, Collapsed, Dark, RightDock;
            public Part Active, Hover, Pressed;
            public Part Previous;
            public int Motion, CameraFlash;
            public int Scale; // in thousandths
        }

        public Toolbar(SketchController controller)
        {
            this.controller = controller;
            motionTimer = new Eto.Forms.UITimer { Interval = .016 };
            motionTimer.Elapsed += (s,e) =>
            {
                Redraw();
                if((DateTime.UtcNow-motionStarted).TotalMilliseconds>300 && (DateTime.UtcNow-cameraSaved).TotalMilliseconds>300) motionTimer.Stop();
            };
        }

        public ToolbarDock Dock
        {
            get => dock;
            set
            {
                if (dock == value) return;
                dock = value;
                Settings?.SetInteger("ToolbarDock", (int)dock);
                Redraw();
            }
        }

        public bool Collapsed
        {
            get => collapsed;
            set
            {
                if (collapsed == value) return;
                collapsed = value;
                CloseOptionsPopup();
                Settings?.SetBool("ToolbarCollapsed", collapsed);
                Redraw();
            }
        }

        static PersistentSettings Settings => SketchPlugin.Instance?.Settings;

        protected override void OnEnable(bool enable)
        {
            if (enable)
            {
                if (!remembered && Settings != null)
                {
                    remembered = true;
                    int saved = Settings.GetInteger("ToolbarDock", (int)ToolbarDock.Bottom);
                    if (Enum.IsDefined(typeof(ToolbarDock), saved)) dock = (ToolbarDock)saved;
                    collapsed = Settings.GetBool("ToolbarCollapsed", false);
                    floatingX = Settings.GetDouble("ToolbarFloatX", .5);
                    floatingY = Settings.GetDouble("ToolbarFloatY", .8);
                }
                RhinoView.SetActive += OnActiveView;
            }
            else
            {
                motionTimer.Stop();
                RhinoView.SetActive -= OnActiveView;
                hover = pressed = Part.None;
                dragging = false;
                CloseOptionsPopup();
            }
        }

        // The bar goes with the active viewport: both the old and the new one need painting.
        void OnActiveView(object sender, ViewEventArgs e)
        {
            CloseOptionsPopup();
            Eto.Forms.Application.Instance.AsyncInvoke(Redraw);
        }

        static void Redraw() => RhinoDoc.ActiveDoc?.Views.Redraw();

        // ------------------------------------------------------------- layout

        bool Upright => dock == ToolbarDock.Left || dock == ToolbarDock.Right;

        Part ActivePart
        {
            get
            {
                if (savedViewsTool && controller.Tool == SketchTool.Select) return Part.Views;
                savedViewsTool = false;
                switch (controller.Tool)
                {
                    case SketchTool.Draw: return Part.Pen;
                    case SketchTool.Erase: return Part.Eraser;
                    case SketchTool.Comment: return Part.Comments;
                    default: return Part.Select;
                }
            }
        }

        static bool Works(Part part) => true;

        struct Item
        {
            public Part Part;
            public Wpf.Rect Box; // within the bar, at 100 %
            public string Value;
        }

        // The bar's parts in order, and (out) the size of the bar that holds them.
        List<Item> Layout(out Wpf.Size bar)
        {
            var parts = new List<Part> { Part.Grip };
            if (collapsed) parts.Add(ActivePart);
            else parts.AddRange(new[] { Part.Select, Part.Pen, Part.Eraser, Part.Comments, Part.Views });
            parts.Add(Part.Separator);
            parts.Add(Part.Collapse);

            bool upright = Upright;
            double across = Thickness - 2 * Padding, at = Padding;
            var items = new List<Item>();
            foreach (Part part in parts)
            {
                double length = part == Part.Grip ? GripLength
                    : part == Part.Separator ? SeparatorLength
                    : part == Part.Collapse ? ChevronLength
                    : ButtonLength;
                items.Add(new Item
                {
                    Part = part,
                    Box = upright ? new Wpf.Rect(Padding, at, across, length) : new Wpf.Rect(at, Padding, length, across),
                });
                at += length;
            }
            at += Padding;
            bar = upright ? new Wpf.Size(Thickness, at) : new Wpf.Size(at, Thickness);
            return items;
        }

        // The bar's top left corner in the viewport, in the viewport's own pixels.
        ScreenPoint Origin(System.Drawing.Size viewport, Wpf.Size bar, double scale)
        {
            double width = bar.Width * scale, height = bar.Height * scale, inset = Inset * scale;
            double x, y;
            if (dock == ToolbarDock.Floating)
                return new ScreenPoint((int)Math.Max(0, Math.Min(viewport.Width-width, floatingX*viewport.Width-width/2)),
                    (int)Math.Max(0, Math.Min(viewport.Height-height, floatingY*viewport.Height-height/2)));
            switch (dock)
            {
                case ToolbarDock.TopLeft: case ToolbarDock.Left: x = inset; break;
                case ToolbarDock.BottomLeft: x = inset + AxesClear * scale; break;
                case ToolbarDock.Top: case ToolbarDock.Bottom: x = (viewport.Width - width) / 2; break;
                default: x = viewport.Width - width - inset; break;
            }
            switch (dock)
            {
                case ToolbarDock.TopLeft: y = inset + TitleClear * scale; break;
                case ToolbarDock.Top: case ToolbarDock.TopRight: y = inset; break;
                case ToolbarDock.Left: case ToolbarDock.Right: y = (viewport.Height - height) / 2; break;
                default: y = viewport.Height - height - inset; break;
            }
            return new ScreenPoint((int)Math.Round(Math.Max(0, x)), (int)Math.Round(Math.Max(0, y)));
        }

        // Each place's spot on the viewport's edge, as shares of its width and height.
        static readonly double[][] Anchors =
        {
            new[] { 0.0, 0.0 }, new[] { 0.5, 0.0 }, new[] { 1.0, 0.0 },
            new[] { 0.0, 0.5 }, new[] { 1.0, 0.5 },
            new[] { 0.0, 1.0 }, new[] { 0.5, 1.0 }, new[] { 1.0, 1.0 },
        };

        static ToolbarDock Nearest(System.Drawing.Size viewport, ScreenPoint point)
        {
            double x = viewport.Width > 0 ? (double)point.X / viewport.Width : 0.5;
            double y = viewport.Height > 0 ? (double)point.Y / viewport.Height : 0;
            int nearest = (int)ToolbarDock.Top;
            double least = double.MaxValue;
            for (int i = 0; i < Anchors.Length; i++)
            {
                double dx = x - Anchors[i][0], dy = y - Anchors[i][1], distance = dx * dx + dy * dy;
                if (distance < least) { least = distance; nearest = i; }
            }
            return (ToolbarDock)nearest;
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action,uint parameter,[MarshalAs(UnmanagedType.Bool)] out bool value,uint flags);
        static bool MotionEnabled
        {
            get
            {
                try { return SystemParametersInfo(0x1042,0,out bool enabled,0) && enabled; }
                catch { return false; }
            }
        }

        // Windows' display scaling where this view is: 1 at 100 %, 1.5 at 150 %.
        internal static double Scale(RhinoView view)
        {
            try
            {
                uint dpi = Rhino.Runtime.HostUtils.RunningOnWindows ? GetDpiForWindow(view.Handle) : 0;
                if (dpi >= 96 && dpi <= 960) return dpi / 96.0;
            }
            catch
            {
                // An older Windows without the call: unscaled.
            }
            return 1;
        }

        // Only the active viewport has the bar.
        bool ShowsIn(RhinoView view)
        {
            if (!Enabled || broken || view == null) return false;
            RhinoView active = view.Document?.Views.ActiveView;
            return active != null && active.RuntimeSerialNumber == view.RuntimeSerialNumber;
        }

        // The part of the bar under a point of the view. None if the bar is not there.
        Part Hit(RhinoView view, ScreenPoint point)
        {
            if (!ShowsIn(view)) return Part.None;
            List<Item> items = Layout(out Wpf.Size bar);
            var frame = OptionsFrame(view);
            double scale = frame.Scale;
            Part option = HitOptions(frame, point);
            if (option != Part.None) return option;
            ScreenPoint origin = frame.ToolOrigin;
            var at = new Wpf.Point((point.X - origin.X) / scale, (point.Y - origin.Y) / scale);
            if (!new Wpf.Rect(bar).Contains(at)) return Part.None;
            foreach (Item item in items)
                if (item.Box.Contains(at)) return item.Part == Part.Separator ? Part.Bar : item.Part;
            return Part.Bar;
        }

        // -------------------------------------------------------------- mouse

        /// <summary>The pointer is on the bar.</summary>
        public bool Hovering => hover != Part.None;

        /// <summary>The left button is down on the bar, or the grip is being dragged.</summary>
        public bool Busy => pressed != Part.None || dragging;

        /// <summary>Left button pressed in a view. True if it was on the bar: the press is then the bar's.</summary>
        public bool Press(RhinoView view, ScreenPoint point, bool doubleClick)
        {
            Part part = Hit(view, point);
            if (part == Part.None && optionsPopup != OptionsPopup.None)
            {
                CloseOptionsPopup();
                pressed = Part.Bar;
                view?.Redraw();
                return true;
            }
            if (part == Part.None) return false;
            pressed = part;
            if (doubleClick && part == Part.SavedView)
            {
                var frame = OptionsFrame(view);
                var at = Local(point,frame.OptionOrigin,frame.Scale);
                RenameView(frame.Items.Find(i=>i.Part==Part.SavedView && i.Box.Contains(at)).Value);
                pressed = Part.Bar;
                return true;
            }
            if (part == Part.Saturation || part == Part.Hue) PickColor(view, point, part);
            if (part == Part.Grip)
            {
                if (doubleClick) Dock = ToolbarDock.Bottom;
                else dragging = true;
            }
            view.Redraw();
            return true;
        }

        public void Move(RhinoView view, ScreenPoint point, bool leftDown)
        {
            if (Busy && !leftDown)
            {
                Release(view, point); // let go somewhere the release was not seen
                return;
            }
            if (dragging)
            {
                if (ShowsIn(view))
                {
                    var size = view.ActiveViewport.Size;
                    double s = Scale(view);
                    bool edge = point.X < 72*s || point.X > size.Width-72*s || point.Y < 80*s || point.Y > size.Height-80*s;
                    if (edge) Dock = Nearest(size, point);
                    else
                    {
                        floatingX = (double)point.X / Math.Max(1,size.Width); floatingY = (double)point.Y / Math.Max(1,size.Height);
                        Dock = ToolbarDock.Floating;
                        Settings?.SetDouble("ToolbarFloatX",floatingX); Settings?.SetDouble("ToolbarFloatY",floatingY);
                        Redraw();
                    }
                }
                return;
            }
            if (leftDown && (pressed == Part.Saturation || pressed == Part.Hue))
            {
                PickColor(view, point, pressed);
                return;
            }
            Part over = Hit(view, point);
            if (over == hover) return;
            hover = over;
            view?.Redraw();
        }

        public void Release(RhinoView view, ScreenPoint point)
        {
            Part part = pressed;
            pressed = Part.None;
            bool dragged = dragging;
            dragging = false;
            if (!dragged && part != Part.None && Hit(view, point) == part)
            {
                if (!ClickOptions(part, view, point)) Click(part);
            }
            Redraw();
        }

        public void Leave()
        {
            if (hover == Part.None) return;
            hover = Part.None;
            Redraw();
        }

        void Click(Part part)
        {
            switch (part)
            {
                case Part.Collapse:
                    Collapsed = !collapsed;
                    break;
                case Part.Views:
                    clearAsked = false;
                    CloseOptionsPopup();
                    controller.Tool = SketchTool.Select;
                    savedViewsTool = true;
                    if (collapsed) Collapsed = false;
                    break;
                case Part.Select:
                case Part.Pen:
                case Part.Eraser:
                case Part.Comments:
                    clearAsked = false;
                    savedViewsTool = false;
                    CloseOptionsPopup();
                    if (collapsed)
                    {
                        Collapsed = false; // only the tool in use is showing: it opens the bar again
                        break;
                    }
                    controller.Tool = part == Part.Pen ? SketchTool.Draw
                        : part == Part.Eraser ? SketchTool.Erase
                        : part == Part.Comments ? SketchTool.Comment
                        : SketchTool.Select;
                    break;
            }
        }

        // ----------------------------------------------------------- painting

        // Over everything else in the viewport.
        protected override void DrawForeground(DrawEventArgs e)
        {
            if (broken || e.Display.IsPrinting || e.Display.IsInViewCapture) return;
            try
            {
                RhinoView view = e.RhinoDoc?.Views.ActiveView;
                if (view == null || view.ActiveViewportID != e.Viewport.Id) return;

                var frame = OptionsFrame(view);
                double scale = frame.Scale;
                List<Item> items = Layout(out Wpf.Size bar);
                var look = new Look
                {
                    Upright = Upright,
                    Collapsed = collapsed,
                    Active = ActivePart,
                    Hover = dragging ? Part.Grip : hover,
                    Pressed = pressed,
                    Scale = (int)Math.Round(scale * 1000),
                    Dark = CrayonAppearance.Dark,
                    RightDock = dock == ToolbarDock.Right,
                };
                if(look.Active != lastAnimated)
                {
                    previousAnimated = lastAnimated;
                    lastAnimated = look.Active;
                    motionStarted = DateTime.UtcNow;
                    if(MotionEnabled) motionTimer.Start();
                }
                look.Previous = previousAnimated;
                look.Motion = MotionEnabled ? (int)Math.Min(1000,(DateTime.UtcNow-motionStarted).TotalMilliseconds/260*1000) : 1000;
                look.CameraFlash = MotionEnabled ? (int)Math.Max(0,1000-(DateTime.UtcNow-cameraSaved).TotalMilliseconds/260*1000) : 0;
                if (picture == null || !look.Equals(painted))
                {
                    picture?.Dispose();
                    using (System.Drawing.Bitmap bitmap = Paint(items, bar, look, scale)) picture = new DisplayBitmap(bitmap);
                    painted = look;
                }

                ScreenPoint origin = frame.ToolOrigin;
                int margin = (int)Math.Round(Margin * scale);
                e.Display.DrawBitmap(picture, origin.X - margin, origin.Y - margin);
                DrawOptions(e, frame);
            }
            catch (Exception ex)
            {
                broken = true;
                RhinoApp.WriteLine("Crayon toolbar: " + ex.Message);
            }
        }

        // The whole bar as one picture, with Margin of clear space around it.
        static System.Drawing.Bitmap Paint(List<Item> items, Wpf.Size bar, Look look, double scale)
        {
            return Picture.Render(bar.Width + 2 * Margin, bar.Height + 2 * Margin, scale, context =>
            {
                context.PushTransform(new Media.TranslateTransform(Margin, Margin));
                var body = new Wpf.Rect(bar);
                Picture.Card(context, body, Radius); // the bar itself, on its shadow
                foreach (Item item in items) Paint(context, item, look);
                PaintActiveDot(context,items,look);
                context.Pop();
            });
        }

        /// <summary>The bar as it is now, as a picture file. For looking at it outside Rhino.</summary>
        internal void SavePicture(string path, double scale)
        {
            List<Item> items = Layout(out Wpf.Size bar);
            var look = new Look { Upright = Upright, Collapsed = collapsed, Active = ActivePart, Hover = hover, Pressed = pressed, Motion=1000, RightDock=dock==ToolbarDock.Right };
            using (System.Drawing.Bitmap bitmap = Paint(items, bar, look, scale)) bitmap.Save(path, ImageFormat.Png);
        }

        static void Paint(Media.DrawingContext context, Item item, Look look)
        {
            Wpf.Rect box = item.Box;
            var centre = new Wpf.Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            bool over = look.Hover == item.Part, down = look.Pressed == item.Part;

            switch (item.Part)
            {
                case Part.Grip:
                {
                    // Six dots: two across the bar's length, three across its width.
                    Media.Brush dots = Fill(Muted, over || down ? 1 : 0.6);
                    for (int i = 0; i < 2; i++)
                        for (int j = 0; j < 3; j++)
                        {
                            double along = (i - 0.5) * 4, across = (j - 1) * 5;
                            var dot = look.Upright ? new Wpf.Point(centre.X + across, centre.Y + along) : new Wpf.Point(centre.X + along, centre.Y + across);
                            context.DrawEllipse(dots, null, dot, 1.2, 1.2);
                        }
                    break;
                }
                case Part.Separator:
                {
                    var line = new Media.Pen(Fill(Shade, LineShade), 1);
                    if (look.Upright) context.DrawLine(line, new Wpf.Point(centre.X - 8, centre.Y), new Wpf.Point(centre.X + 8, centre.Y));
                    else context.DrawLine(line, new Wpf.Point(centre.X, centre.Y - 8), new Wpf.Point(centre.X, centre.Y + 8));
                    break;
                }
                case Part.Collapse:
                {
                    if (over || down) context.DrawRoundedRectangle(Fill(Shade, down ? PressShade : HoverShade), null, box, ButtonRadius, ButtonRadius);
                    // The chevron is drawn pointing down. Turned, it points the way the bar will fold: toward the grip.
                    double turn = look.Upright ? (look.Collapsed ? 0 : 180) : (look.Collapsed ? -90 : 90);
                    PaintIcon(context, Icons.Chevron, centre, ChevronSize, over || down ? Ink2 : Muted, 1, turn);
                    break;
                }
                default:
                {
                    bool works = Works(item.Part), active = look.Active == item.Part;
                    if (works && (over || down)) context.DrawRoundedRectangle(Fill(Shade, down ? PressShade : HoverShade), null, box, ButtonRadius, ButtonRadius);
                    double progress = look.Motion/1000.0;
                    double iconScale = down ? .94 : active ? .94+.06*progress : 1;
                    PaintIcon(context, Icons.For(item.Part), centre, IconSize*iconScale, active ? Ink : works ? Ink2 : Muted, works ? 1 : 0.55, 0);
                    if(item.Part==Part.Views && look.CameraFlash>0)
                        context.DrawEllipse(Fill(Accent,look.CameraFlash/1000.0),null,centre,3,3);
                    break;
                }
            }
        }

        static void PaintActiveDot(Media.DrawingContext c,List<Item> items,Look look)
        {
            var target=items.Find(i=>i.Part==look.Active);
            if(target.Box.IsEmpty || target.Box.Width==0) return;
            var source=items.Find(i=>i.Part==look.Previous);
            if(source.Box.IsEmpty || source.Box.Width==0) source=target;
            Func<Wpf.Rect,Wpf.Point> location = box => new Wpf.Point(box.X+box.Width/2+(look.Upright?(look.RightDock?1:-1)*(IconSize/2+DotSize/2+1.5):0),box.Y+box.Height/2+(look.Upright?0:IconSize/2+DotSize/2+1.5));
            var from=location(source.Box); var to=location(target.Box);
            double t=look.Motion/1000.0; t=1-Math.Pow(1-t,3);
            c.DrawEllipse(Fill(Accent),null,new Wpf.Point(from.X+(to.X-from.X)*t,from.Y+(to.Y-from.Y)*t),DotSize/2,DotSize/2);
        }

        static void PaintIcon(Media.DrawingContext context, Icons.Shape[] icon, Wpf.Point centre, double size, Media.Color colour, double opacity, double turn)
        {
            var pen = new Media.Pen(Fill(colour), Icons.Stroke)
            {
                StartLineCap = Media.PenLineCap.Round,
                EndLineCap = Media.PenLineCap.Round,
                LineJoin = Media.PenLineJoin.Round,
            };
            context.PushOpacity(opacity);
            context.PushTransform(new Media.TranslateTransform(centre.X - size / 2, centre.Y - size / 2));
            context.PushTransform(new Media.ScaleTransform(size / Icons.Grid, size / Icons.Grid));
            context.PushTransform(new Media.RotateTransform(turn, Icons.Grid / 2, Icons.Grid / 2));
            foreach (Icons.Shape shape in icon)
                context.DrawGeometry(shape.Fill > 0 ? Fill(colour, shape.Fill) : null, shape.Outline ? pen : null, shape.Geometry);
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }

        static Media.Color Rgb(byte red, byte green, byte blue) => Picture.Rgb(red, green, blue);

        static Media.Brush Fill(Media.Color colour, double opacity = 1) => Picture.Fill(colour, opacity);

        /// <summary>
        /// The icons, from design/icons/*.svg: drawn on a 24 x 24 grid with a 1.8 stroke,
        /// round caps and joins, in whatever colour they are given.
        /// </summary>
        static class Icons
        {
            public const double Grid = 24, Stroke = 1.8;

            public struct Shape
            {
                public Media.Geometry Geometry;
                public double Fill;   // how solid its inside is: 0 for none
                public bool Outline;
            }

            public static readonly Shape[] Select =
            {
                Line("M4.5 4.5l15 5.9-6.6 2.1-2.4 6.8z"),
            };

            public static readonly Shape[] Pen =
            {
                Line("M15.6 4.4a2 2 0 0 1 2.8 0l1.2 1.2a2 2 0 0 1 0 2.8L9.4 18.6 5 19.9l1.3-4.4z"),
                Line("M13.6 6.4l4 4"),
                Line("M2.5 21.3c2-1.4 3.6-1.4 5 0s3.2 1.4 5 0"),
            };

            public static readonly Shape[] Eraser =
            {
                Path("M8.4 10l6.1 6.1-3.4 3.4H8.7L4.2 15a1.7 1.7 0 0 1 0-2.4z", 0.28, false),
                Line("M8.7 19.5L4.2 15a1.7 1.7 0 0 1 0-2.4l8.4-8.4a1.7 1.7 0 0 1 2.4 0l4.8 4.8a1.7 1.7 0 0 1 0 2.4l-8 8.1z"),
                Line("M8.4 10l6.1 6.1M13.5 19.5H20"),
            };

            public static readonly Shape[] Comments =
            {
                Path("M12 3.8a8.2 8.2 0 1 1-3.9 15.4l-4.3 1 1-4.2A8.2 8.2 0 0 1 12 3.8z", 0.18, true),
                Line("M8.6 10.7h6.8"),
                Line("M8.6 13.8h4.2"),
            };

            public static readonly Shape[] Views =
            {
                Path("M4.6 7.8h2.6l1.6-2.3h6.4l1.6 2.3h2.6a1.6 1.6 0 0 1 1.6 1.6v8.1a1.6 1.6 0 0 1-1.6 1.6H4.6A1.6 1.6 0 0 1 3 17.5V9.4a1.6 1.6 0 0 1 1.6-1.6z", 0.18, true),
                Line("M8.7 13.3a3.3 3.3 0 1 0 6.6 0a3.3 3.3 0 1 0-6.6 0z"),
            };

            public static readonly Shape[] Chevron =
            {
                Line("M7 10l5 5 5-5"),
            };

            public static Shape[] For(Part part)
            {
                switch (part)
                {
                    case Part.Pen: return Pen;
                    case Part.Eraser: return Eraser;
                    case Part.Comments: return Comments;
                    case Part.Views: return Views;
                    default: return Select;
                }
            }

            static Shape Line(string path) => Path(path, 0, true);

            // WPF reads the same path text as SVG.
            static Shape Path(string path, double fill, bool outline)
            {
                Media.Geometry geometry = Media.Geometry.Parse(path);
                geometry.Freeze();
                return new Shape { Geometry = geometry, Fill = fill, Outline = outline };
            }
        }
    }
}
