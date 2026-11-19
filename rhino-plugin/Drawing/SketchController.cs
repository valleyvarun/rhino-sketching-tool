using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Color = System.Drawing.Color;
using ScreenPoint = System.Drawing.Point;

namespace RhinoSketchingTool
{
    public enum SketchTool { Draw, Erase, Select, Comment, Views }
    public enum DrawMethod { Surface, Plane, Cursor, View }
    public enum EraseMode { Stroke, Part }
    /// <summary>A one-click pick armed from the pen's option row: a face to draw on, or where the 3D cursor goes.</summary>
    public enum PickMode { None, Face, Cursor }

    /// <summary>
    /// The sketching tools. Left-drag in a viewport draws a freehand stroke or erases,
    /// depending on the active tool; the right button is never touched, so Rhino's own
    /// navigation keeps working.
    ///
    /// Where a stroke lands in 3D depends on the drawing method:
    ///   Surface  on whatever surface, polysurface, mesh or SubD is under the pointer
    ///   Plane    on the viewport's construction plane
    ///   Cursor   on a plane through the 3D cursor, facing the camera
    ///
    /// "Pick a face" in the pen's option row, then a click on a surface, moves the
    /// construction plane onto it (on empty space it puts the plane back); "Place cursor",
    /// then a click, moves the 3D cursor. No modifier keys: they belong to Rhino.
    ///
    /// Moving the plane onto a surface is the one thing here that changes Rhino's
    /// construction plane, and it is only a loan: the plane goes back to where it was as
    /// soon as another drawing method is chosen or sketching is turned off. The 3D cursor,
    /// and the views at it, never touch the plane.
    ///
    /// Strokes are polyline curves on the "Sketch" layer, each on the sublayer of the group
    /// it is in (see AnnotationGroups), tagged with a user string so
    /// the eraser only ever touches curves this tool drew. How a stroke is painted (its
    /// width and strength along the way) is saved on the curve too; see StrokePaint.
    ///
    /// The curves are hidden objects. Rhino itself therefore does nothing with them:
    /// it does not draw them, select them, or let object snaps catch on them, which is
    /// the point. They are a sketch to model over, not geometry to model with. What you
    /// see of a stroke is painted by SketchConduit, and only this tool's eraser removes
    /// one. Turning the Sketch layer off hides them like anything else.
    ///
    /// The exception is this tool's own Select: while it is on, the strokes are
    /// ordinary curves, so that they can be selected, moved, copied and deleted along
    /// with the rest of the model. Esc goes to Select; Esc twice turns the tools off.
    ///
    /// A stroke is wider and stronger where it was drawn with more pressure. A pen
    /// supplies that; with a mouse it is made up from speed, slow being firm.
    /// </summary>
    public sealed partial class SketchController
    {
        public const string LayerName = "Sketch";
        public const string StrokeKey = "SketchingTool.Stroke";
        public static readonly Color Ink = Color.FromArgb(118, 28, 140);      // the colour a new Sketch layer gets
        static readonly Color[] FormerInks = { Color.FromArgb(36, 87, 214), Color.FromArgb(168, 33, 144) }; // what that used to be; see InkColor

        const double MinPointSpacing = 2; // pixels the pointer must move before a point is added
        const double EraseRadius = 10;    // pixels around the pointer that the eraser reaches
        const double EraseStep = 3;       // pixels between points after resampling a touched stroke
        const double GrazingLimit = 0.02; // ignore planes seen this close to edge-on (near the horizon)

        // Pressure runs from 0 to 1. These shape the pressure a mouse stroke is given.
        const double StartPressure = 0.2;   // every stroke starts, and ends, this light
        const double FastPressure = 0.3;    // the lightest a fast stroke gets
        const double SlowSpeed = 300;       // pixels a second: at or below this the stroke is at full pressure
        const double FastSpeed = 3000;      // at or above this it is at FastPressure
        const double MouseSmoothing = 16;   // pixels of travel over which pressure eases toward a new value
        const double LiftOffPixels = 14;    // the last stretch of a stroke fades out over this many pixels,
        const double LiftOffSizes = 2;      // or this many brush sizes if that is longer

        const double PenSmoothing = 3;      // a pen needs only its jitter taken off
        const double PenCurve = 0.6;        // lifts the middle of a pen's range: writing pressure is well below full
        const double FlowFloor = 0.35;      // the share of the brush's opacity left at no pressure
        const double MinStrokePixels = 1;   // thinner than this, a stroke is drawn fainter rather than narrower

        readonly SketchMouse mouse;
        readonly SketchConduit conduit;
        readonly Dictionary<Guid, SavedView> savedViews = new Dictionary<Guid, SavedView>();
        readonly Dictionary<Guid, BorrowedPlane> borrowed = new Dictionary<Guid, BorrowedPlane>(); // by viewport id

        SketchTool tool = SketchTool.Draw;
        DrawMethod method = DrawMethod.Plane;
        EraseMode erase = EraseMode.Stroke;
        Point3d cursor = Point3d.Origin;
        bool active;

        DrawDrag drawing;  // set while a draw drag is in progress
        EraseDrag erasing; // set while an erase drag is in progress
        int liveVersion;   // goes up whenever the stroke being drawn changes
        uint undoRecord;
        RhinoDoc undoDoc;

        public SketchController()
        {
            mouse = new SketchMouse(this);
            conduit = new SketchConduit(this);
            Toolbar = new Toolbar(this);
        }

        /// <summary>The bar of tools over the active viewport, there while sketching is on.</summary>
        internal Toolbar Toolbar { get; }

        /// <summary>Raised when the tool, method, erase mode or active state changes.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// True while sketching is on: the left mouse button draws and erases in the
        /// viewports, and the view keys and Esc Esc are listened for. Nothing is
        /// intercepted otherwise. (Strokes are painted either way; see StartPainting.)
        /// </summary>
        public bool Active
        {
            get => active;
            set
            {
                if (active == value) return;
                EndDrag();
                active = value;
                mouse.Enabled = value;
                Toolbar.Enabled = value;
                if (value) KeyHook.Install(SetView, BackToSelect, LeaveByEscape);
                else KeyHook.Remove();
                if (value)
                {
                    RhinoDoc.BeginSaveDocument += OnBeginSave;
                    RhinoDoc.EndSaveDocument += OnEndSave;
                }
                else
                {
                    RhinoDoc.BeginSaveDocument -= OnBeginSave;
                    RhinoDoc.EndSaveDocument -= OnEndSave;
                    ReturnPlanes();
                }
                ShowOrHideStrokes();
                RhinoApp.WriteLine(value ? "Crayon on." : "Crayon off.");
                Refresh();
            }
        }

        // Esc pressed once: put the pen or eraser down, and go back to selecting. Unless
        // a Rhino command was running: then the Esc was for that, and has cancelled it.
        void BackToSelect(bool commandRunning)
        {
            if (active && !commandRunning) Tool = SketchTool.Select;
        }

        // Esc pressed twice in a row. One Esc is left to Rhino, to cancel whatever
        // command is running, so that other tools can be used while sketching.
        void LeaveByEscape()
        {
            // Crayon is turned off only with its switch in the panel header, never by
            // keys: Esc twice is left to Rhino.
        }

        /// <summary>
        /// Paint the strokes of every open document, from now until Rhino closes. This
        /// has to be on whenever the plug-in is loaded, tools active or not: strokes are
        /// hidden objects, so nothing else would show them.
        /// </summary>
        public void StartPainting()
        {
            if (conduit.Enabled) return;
            conduit.Enabled = true;
            AnnotationGroups.Watch();
            RhinoApp.Idle += OnIdle;
            PenHook.Install(); // for pen pressure, and for pen taps on the Sketch panel
            StartComments();
        }

        // Strokes that are not as they should be are put right here, once no command is
        // running: one Rhino is showing as an ordinary curve (it was drawn by an earlier
        // version, or the Show command has just unhidden everything) is hidden again,
        // and while the Select tool is on, a hidden one is shown.
        void OnIdle(object sender, EventArgs e)
        {
            // The groups' layers first: a file from before groups were layers gets them here.
            if (!Dragging && !Rhino.Commands.Command.InCommand()) AnnotationGroups.Tidy();
            conduit.Tidy(!Rhino.Commands.Command.InCommand());
        }

        /// <summary>Stop everything, painting included. For when Rhino closes.</summary>
        public void Shutdown()
        {
            Active = false;
            StopComments();
            RhinoApp.Idle -= OnIdle;
            PenHook.Remove();
            conduit.Enabled = false;
        }

        /// <summary>The brush new strokes are drawn with.</summary>
        public Brush Brush { get; } = new Brush();

        /// <summary>
        /// Whether the last stroke was started by a pen that reports how hard it presses.
        /// Null until a stroke has been drawn.
        /// </summary>
        public bool? PenPressure { get; private set; }

        public SketchTool Tool
        {
            get => tool;
            set { if (tool != value) { EndDrag(); SetTool(value); Refresh(); } }
        }

        void SetTool(SketchTool value)
        {
            if (tool == value) return;
            editor?.Commit(); // a comment being written is kept as it stands
            aim = null;
            picking = PickMode.None;
            tool = value;
            ShowOrHideStrokes();
        }

        // The Select tool is for selecting strokes as well as everything else, and Rhino
        // can only select what is not hidden. So while Select is on (and the tools are),
        // the strokes are made ordinary curves; at all other times they are hidden.
        void ShowOrHideStrokes()
        {
            conduit.StrokesSelectable = active && tool == SketchTool.Select;
            conduit.Tidy(!Rhino.Commands.Command.InCommand());
        }

        public DrawMethod Method
        {
            get => method;
            set { if (method != value) { EndDrag(); SetMethod(value); Refresh(); } }
        }

        // Leaving the Plane method gives back any construction plane it borrowed.
        void SetMethod(DrawMethod value)
        {
            if (method == DrawMethod.Plane && value != DrawMethod.Plane) ReturnPlanes();
            method = value == DrawMethod.View ? DrawMethod.Plane : value;
            picking = PickMode.None;
        }

        public EraseMode Erase
        {
            get => erase;
            set { if (erase != value) { EndDrag(); erase = value; Refresh(); } }
        }

        public Point3d Cursor => cursor;

        PickMode picking;

        /// <summary>
        /// Armed from the option row under the pen ("Pick a face", "Place cursor"): the next
        /// click in a viewport does it, instead of drawing. Changing tool or method disarms it.
        /// </summary>
        public PickMode Picking
        {
            get => picking;
            set { if (picking != value) { EndDrag(); picking = value; Refresh(); } }
        }

        /// <summary>The active viewport draws on a face picked with "Pick a face".</summary>
        public bool OnPickedFace
        {
            get
            {
                var viewport = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport;
                return viewport != null && borrowed.ContainsKey(viewport.Id);
            }
        }

        /// <summary>Back from a picked face to the viewport's own construction plane.</summary>
        public void BackToGrid()
        {
            var viewport = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport;
            if (viewport != null && ReturnPlane(viewport)) Refresh();
        }

        /// <summary>The 3D cursor is only drawn while it is the thing being drawn on.</summary>
        public bool CursorVisible => active && tool == SketchTool.Draw && method == DrawMethod.Cursor;

        /// <summary>Points of the stroke being drawn right now, for the live preview.</summary>
        public IReadOnlyList<Point3d> LivePoints => drawing?.Points;

        /// <summary>How the stroke being drawn is painted so far. Null when there is none.</summary>
        internal StrokePaint LivePaint => drawing != null && drawing.Points.Count > 0 ? Paint(drawing, false) : null;

        internal Color LiveColor => drawing != null ? drawing.Color : Ink;

        /// <summary>Changes whenever the stroke being drawn does.</summary>
        internal int LiveVersion => liveVersion;

        public bool Dragging => drawing != null || erasing != null;

        void Refresh()
        {
            RhinoDoc.ActiveDoc?.Views.Redraw();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------- pointer

        /// <summary>
        /// Left button pressed in a viewport. Returns true if the tool used the click.
        /// Pressure is the pen's, from 0 to 1, or negative when the pointer has none (a mouse).
        /// </summary>
        public bool PointerDown(RhinoView view, ScreenPoint point, bool shift, bool ctrl, double pressure = -1)
        {
            if (!active || tool == SketchTool.Select || tool == SketchTool.Views || view == null) return false;
            EndDrag();

            if (tool == SketchTool.Comment)
            {
                CommentClick(view, point);
                return true;
            }
            if (picking != PickMode.None)
            {
                // "Pick a face" or "Place cursor" was armed: this click does that, once.
                PickMode pick = picking;
                picking = PickMode.None;
                if (pick == PickMode.Face) PickDrawPlane(view, point);
                else { MoveCursor(view, point); Refresh(); }
                return true;
            }
            if (tool == SketchTool.Erase)
            {
                erasing = new EraseDrag { View = view, Strokes = CollectStrokes(view.Document, view.ActiveViewport) };
                EraseAt(point);
                return true;
            }

            var viewport = view.ActiveViewport;
            // Saved views are cameras only (Rhino Named Views). A sketch is always 3D model
            // content that every view shows; it is never tied to, or captured as, a view.
            drawing = new DrawDrag
            {
                View = view,
                Plane = method == DrawMethod.Cursor
                    ? new Plane(cursor, viewport.CameraDirection)
                    : method == DrawMethod.View
                        ? new Plane(viewport.CameraTarget, viewport.CameraDirection)
                        : viewport.ConstructionPlane(),
                Color = StrokeColor,
                Method = method,
            };
            ExtendStroke(point, pressure);
            if (PenPressure != pressure >= 0)
            {
                PenPressure = pressure >= 0;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }

        public void PointerMove(RhinoView view, ScreenPoint point, double pressure = -1)
        {
            var dragView = drawing?.View ?? erasing?.View;
            if (dragView == null) return;
            if (view == null || view.RuntimeSerialNumber != dragView.RuntimeSerialNumber)
            {
                EndDrag(); // wandered into another viewport
                return;
            }
            if (drawing != null) ExtendStroke(point, pressure);
            else EraseAt(point);
        }

        public void PointerUp() => EndDrag();

        void EndDrag()
        {
            if (drawing != null) FinishRun();
            var view = drawing?.View ?? erasing?.View;
            drawing = null;
            erasing = null;
            liveVersion++;
            if (undoRecord != 0)
            {
                undoDoc.EndUndoRecord(undoRecord);
                undoRecord = 0;
                undoDoc = null;
            }
            view?.Document?.Views.Redraw();
        }

        // Everything one drag changes is a single step for Rhino's Undo.
        void BeginChange(RhinoDoc doc)
        {
            if (undoRecord != 0) return;
            undoRecord = doc.BeginUndoRecord("Sketch");
            undoDoc = doc;
        }

        // ------------------------------------------------------------- drawing

        void ExtendStroke(ScreenPoint point, double penPressure)
        {
            var viewport = drawing.View.ActiveViewport;
            Hit hit = null;
            if (ViewLine(viewport, point, out Line line))
            {
                if (method == DrawMethod.Surface)
                {
                    if (drawing.Targets == null) drawing.Targets = CollectTargets(drawing.View.Document);
                    hit = HitSurface(viewport, line, drawing.Targets);
                }
                else
                {
                    hit = HitPlane(viewport, line, drawing.Plane);
                }
            }

            // Running off a surface, or onto a different object, ends the current stroke;
            // the next hit starts a new one.
            if (hit == null || hit.ObjectId != drawing.ObjectId) FinishRun();
            if (hit == null) return;

            double moved = 0; // pixels since the last point
            if (drawing.Points.Count == 0)
            {
                drawing.ObjectId = hit.ObjectId;
            }
            else
            {
                // Skip pointer jitter: only add a point after moving a couple of pixels.
                double dx = point.X - drawing.Last.X, dy = point.Y - drawing.Last.Y;
                moved = Math.Sqrt(dx * dx + dy * dy);
                if (moved < MinPointSpacing) return;
            }

            // The brush size is in pixels, the stroke's width in model units: note the
            // scale between the two where the stroke is right now.
            if (!viewport.GetWorldToScreenScale(hit.Point, out double scale) || !(scale > 0))
                scale = drawing.Scales.Count > 0 ? drawing.Scales[drawing.Scales.Count - 1] : 1;

            double pressure = NextPressure(moved, penPressure);
            drawing.Travel.Add(drawing.Points.Count == 0 ? 0 : drawing.Travel[drawing.Travel.Count - 1] + moved);
            drawing.Points.Add(hit.Point);
            drawing.Pressures.Add(pressure);
            drawing.Scales.Add(scale);
            drawing.Last = point;
            liveVersion++;
            drawing.View.Redraw();
        }

        // The pressure at the next point of the stroke. A pen reports its own. A mouse has
        // none, so it is made up from how fast the pointer is moving: slow is a firm line,
        // fast a light one, and every stroke starts light.
        double NextPressure(double moved, double penPressure)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (double)(now - drawing.Time) / Stopwatch.Frequency;
            drawing.Time = now;
            bool first = drawing.Points.Count == 0;

            double target, smoothing;
            if (penPressure >= 0)
            {
                drawing.Pen = true;
                target = Math.Pow(Math.Min(1, penPressure), PenCurve);
                smoothing = PenSmoothing;
                if (first) drawing.Pressure = target;
            }
            else
            {
                double speed = seconds > 0 ? moved / seconds : FastSpeed;
                double fast = Math.Max(0, Math.Min(1, (speed - SlowSpeed) / (FastSpeed - SlowSpeed)));
                target = 1 - (1 - FastPressure) * fast * fast * (3 - 2 * fast);
                smoothing = MouseSmoothing;
                if (first) drawing.Pressure = StartPressure;
            }
            if (!first) drawing.Pressure += (target - drawing.Pressure) * (1 - Math.Exp(-moved / smoothing));
            return drawing.Pressure;
        }

        // How the points gathered so far are painted with the current brush.
        StrokePaint Paint(DrawDrag stroke, bool finished)
        {
            int count = stroke.Points.Count;
            var paint = new StrokePaint
            {
                Opacity = Brush.Opacity,
                Hardness = Brush.Hardness,
                Hardnesses = Brush.HardnessByPressure ? new double[count] : null,
                PixelsPerUnit = stroke.Scales[0],
                Widths = new double[count],
                Flows = new double[count],
            };

            // A pen eases off the paper at the end of a stroke. A mouse button just lets
            // go, so its strokes are faded out over their last stretch once they are done.
            double liftOff = finished && !stroke.Pen ? Math.Max(LiftOffPixels, LiftOffSizes * Brush.Size) : 0;
            double length = stroke.Travel[count - 1];

            for (int i = 0; i < count; i++)
            {
                double pressure = stroke.Pressures[i];
                if (liftOff > 0) pressure *= Math.Min(1, StartPressure + (length - stroke.Travel[i]) / liftOff);
                pressure = 1 + (pressure - 1) * Brush.Pressure; // the slider: at 0, pressure changes nothing

                double pixels = Brush.SizeByPressure ? Brush.Size * pressure : Brush.Size;
                double flow = Brush.OpacityByPressure ? FlowFloor + (1 - FlowFloor) * pressure : 1;
                if (pixels < MinStrokePixels)
                {
                    flow *= pixels / MinStrokePixels;
                    pixels = MinStrokePixels;
                }
                paint.Widths[i] = pixels / stroke.Scales[i];
                paint.Flows[i] = flow;
                if (paint.Hardnesses != null) paint.Hardnesses[i] = Brush.Hardness * pressure;
            }
            return paint;
        }

        // Turn the points gathered so far into a curve in the document.
        void FinishRun()
        {
            if (drawing.Points.Count >= 2)
            {
                var doc = drawing.View.Document;
                // Before the Undo step begins: undoing the stroke must not take its group's layer away.
                var attributes = StrokeAttributes(doc);
                BeginChange(doc);
                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                attributes.ObjectColor = drawing.Color;
                var points = drawing.Points.ToArray();
                var paint = Paint(drawing, true);
                if (SnapShapes && drawing.Method != DrawMethod.Surface)
                    SnapStroke(drawing, ref points, ref paint);
                paint.Write(attributes);
                doc.Objects.AddCurve(new PolylineCurve(points), attributes);
            }
            drawing.Points.Clear();
            drawing.Pressures.Clear();
            drawing.Scales.Clear();
            drawing.Travel.Clear();
            drawing.ObjectId = Guid.Empty;
            liveVersion++;
        }

        static ObjectAttributes StrokeAttributes(RhinoDoc doc)
        {
            // Its group's layer: a sublayer of "Sketch", made here if it is not there yet.
            int layer = AnnotationGroups.LayerForNew(doc, false, out int group);
            var attributes = new ObjectAttributes();
            if (layer >= 0) attributes.LayerIndex = layer;
            attributes.Mode = ObjectMode.Hidden; // Rhino is to leave strokes alone: see the note on this class
            attributes.SetUserString(StrokeKey, "1");
            attributes.SetUserString(AnnotationGroups.GroupKey, group.ToString());
            attributes.Name = NextSketchName(doc); // Sketch 1, Sketch 2, ...: what comments on it call it
            return attributes;
        }

        // The colour a new stroke will have: the Sketch layer's, which the user may have changed.
        // A layer still in a colour that used to be the default was never given a colour by
        // anyone, so it is brought up to the present default, and its strokes with it.
        static Color InkColor(RhinoDoc doc)
        {
            int index = doc.Layers.FindByFullPath(LayerName, -1);
            if (index < 0) return Ink;
            Layer layer = doc.Layers[index];
            foreach (Color former in FormerInks)
                if (layer.Color.ToArgb() == former.ToArgb()) layer.Color = Ink;
            return layer.Color;
        }

        // ------------------------------------------------------------- erasing

        // Erasing works on screen: anything within a few pixels of the pointer is hit,
        // whatever plane or surface it was drawn on.
        //   Stroke  remove every stroke the pointer touches, whole
        //   Part    rub out only the bits under the pointer, splitting strokes
        void EraseAt(ScreenPoint point)
        {
            var doc = erasing.View.Document;
            var viewport = erasing.View.ActiveViewport;
            var pointer = new Point2d(point.X, point.Y);
            bool changed = EraseCommentsAt(erasing.View, point);

            for (int i = erasing.Strokes.Count - 1; i >= 0; i--)
            {
                var stroke = erasing.Strokes[i];
                if (!StrokeVisibleInView(doc, viewport, stroke.Attributes)) continue;
                var screen = new Point2d?[stroke.Points.Length];
                for (int k = 0; k < screen.Length; k++) screen[k] = ToScreen(viewport, stroke.Points[k]);
                if (!Touches(screen, pointer)) continue;

                BeginChange(doc);
                RhinoObject gone = doc.Objects.FindId(stroke.Id);
                bool deleted = gone != null && doc.Objects.Delete(gone, true, true); // quietly, and although it is hidden
                erasing.Strokes.RemoveAt(i);
                if (!deleted) continue;
                changed = true;
                if (erase == EraseMode.Stroke) continue;

                // Resample so long straight segments have points to cut between, then
                // keep the runs of points that lie outside the eraser. Each new point
                // remembers its place along the stroke (as a point index, with a
                // fraction), so the pieces can be painted the way the stroke was there.
                var dense = new List<Point3d> { stroke.Points[0] };
                var places = new List<double> { 0 };
                for (int k = 1; k < stroke.Points.Length; k++)
                {
                    Point3d a = stroke.Points[k - 1], b = stroke.Points[k];
                    double pixels = screen[k - 1].HasValue && screen[k].HasValue
                        ? screen[k - 1].Value.DistanceTo(screen[k].Value)
                        : 0;
                    int steps = Math.Max(1, (int)Math.Ceiling(pixels / EraseStep));
                    for (int s = 1; s <= steps; s++)
                    {
                        double t = (double)s / steps;
                        dense.Add(a + (b - a) * t);
                        places.Add(k - 1 + t);
                    }
                }

                var run = new List<Point3d>();
                var runPlaces = new List<double>();
                for (int k = 0; k < dense.Count; k++)
                {
                    var s = ToScreen(viewport, dense[k]);
                    if (s.HasValue && s.Value.DistanceTo(pointer) < EraseRadius)
                    {
                        KeepRun(doc, stroke, run, runPlaces);
                    }
                    else
                    {
                        run.Add(dense[k]);
                        runPlaces.Add(places[k]);
                    }
                }
                KeepRun(doc, stroke, run, runPlaces);
            }

            if (changed) doc.Views.Redraw();
        }

        // What is left of a partly erased stroke goes back as a new, shorter stroke.
        void KeepRun(RhinoDoc doc, Stroke source, List<Point3d> run, List<double> places)
        {
            if (run.Count >= 2)
            {
                var points = run.ToArray();
                var attributes = source.Attributes.Duplicate();
                attributes.ObjectId = Guid.Empty; // a new object: it must not take over the erased stroke's id
                StrokePaint paint = source.Paint?.At(places);
                paint?.Write(attributes);
                Guid id = doc.Objects.AddCurve(new PolylineCurve(points), attributes);
                if (id != Guid.Empty) erasing.Strokes.Add(new Stroke { Id = id, Points = points, Attributes = attributes, Paint = paint });
            }
            run.Clear();
            places.Clear();
        }

        static bool Touches(Point2d?[] screen, Point2d pointer)
        {
            for (int i = 0; i + 1 < screen.Length; i++)
            {
                if (!screen[i].HasValue || !screen[i + 1].HasValue) continue;
                Point2d a = screen[i].Value, b = screen[i + 1].Value;
                Vector2d ab = b - a, ap = pointer - a;
                double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
                double t = lengthSquared > 0 ? (ap.X * ab.X + ap.Y * ab.Y) / lengthSquared : 0;
                t = Math.Max(0, Math.Min(1, t));
                if ((a + ab * t).DistanceTo(pointer) < EraseRadius) return true;
            }
            return false;
        }

        static Point2d? ToScreen(RhinoViewport viewport, Point3d point)
        {
            // Points behind a perspective camera have no meaningful screen position.
            if (viewport.IsPerspectiveProjection &&
                (point - viewport.CameraLocation) * viewport.CameraDirection <= 0) return null;
            return viewport.WorldToClient(point);
        }

        // The strokes the eraser can reach: those that can be seen.
        static List<Stroke> CollectStrokes(RhinoDoc doc, RhinoViewport viewport = null)
        {
            var strokes = new List<Stroke>();
            foreach (RhinoObject obj in StrokeObjects(doc))
            {
                if (!OnVisibleLayer(doc, obj)) continue;
                if (viewport != null && !StrokeVisibleInView(doc, viewport, obj.Attributes)) continue;
                if (!(obj.Geometry is Curve curve)) continue;
                Point3d[] points = StrokePoints(curve, doc.ModelAbsoluteTolerance);
                if (points == null) continue;
                strokes.Add(new Stroke
                {
                    Id = obj.Id,
                    Points = points,
                    Attributes = obj.Attributes,
                    Paint = StrokePaint.Read(obj.Attributes, points.Length),
                });
            }
            return strokes;
        }

        // Strokes are hidden objects, which Rhino's ordinary ways of listing objects skip.
        static readonly ObjectEnumeratorSettings AnyCurve = new ObjectEnumeratorSettings
        {
            ObjectTypeFilter = ObjectType.Curve,
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            DeletedObjects = false,
            VisibleFilter = false, // layers are checked by OnVisibleLayer, where it matters
        };

        /// <summary>Every stroke in the document, whatever layer it is on.</summary>
        internal static RhinoObject[] StrokeObjects(RhinoDoc doc) =>
            doc.Objects.FindByUserString(StrokeKey, "*", false, false, true, AnyCurve) ?? new RhinoObject[0];

        /// <summary>False for a stroke whose own eye is off, or whose layer (its group's) is turned off.</summary>
        internal static bool OnVisibleLayer(RhinoDoc doc, RhinoObject obj) => AnnotationGroups.Visible(doc, obj.Attributes);

        /// <summary>
        /// The points of a stroke's curve, or null if it has fewer than two. Strokes are
        /// polylines; one that has since been edited into something smoother is
        /// approximated by a polyline.
        /// </summary>
        internal static Point3d[] StrokePoints(Curve curve, double tolerance)
        {
            if (!curve.TryGetPolyline(out Polyline polyline))
            {
                var approximation = curve.ToPolyline(tolerance, 0.05, 0, 0);
                if (approximation == null || !approximation.TryGetPolyline(out polyline)) return null;
            }
            return polyline.Count >= 2 ? polyline.ToArray() : null;
        }

        // ------------------------------------------- drawing plane and 3D cursor

        // "Pick a face", then a click: put the viewport's construction plane on the surface under
        // the pointer (tangent at the clicked point), or back where it was on empty space.
        void PickDrawPlane(RhinoView view, ScreenPoint point)
        {
            var viewport = view.ActiveViewport;
            Hit hit = null;
            if (ViewLine(viewport, point, out Line line))
                hit = HitSurface(viewport, line, CollectTargets(view.Document));

            if (hit != null)
            {
                Vector3d normal = hit.Normal;
                if (normal * viewport.CameraDirection > 0) normal = -normal; // face the viewer
                // Keep the plane's X axis horizontal where that is possible.
                Vector3d x = Vector3d.CrossProduct(Vector3d.ZAxis, normal);
                if (x.Length < 1e-6) x = Vector3d.XAxis;
                x.Unitize();
                BorrowPlane(viewport, new Plane(hit.Point, x, Vector3d.CrossProduct(normal, x)));
            }
            else if (!ReturnPlane(viewport))
            {
                // Nothing of ours to put back: the plane is wherever the user (or an
                // earlier version of this tool) left it. Reset it to Rhino's default.
                // Pushed, so that Rhino's own "CPlane Undo" can take this back.
                ConstructionPlane reset = viewport.GetConstructionPlane();
                reset.Plane = DefaultPlane(viewport);
                viewport.PushConstructionPlane(reset);
            }

            SetTool(SketchTool.Draw);
            method = DrawMethod.Plane;
            Refresh();
        }

        // To draw on a surface with the Plane method, the viewport's construction plane is
        // moved onto it. That is a loan. The plane the viewport had before is kept, and
        // put back when the Plane method is left or sketching is turned off.
        void BorrowPlane(RhinoViewport viewport, Plane plane)
        {
            // A loan stands until the plane is given back. But if the plane is no longer
            // where the tool put it, the user has moved it since, and that is the plane
            // to go back to.
            if (!borrowed.TryGetValue(viewport.Id, out BorrowedPlane loan) || !Same(viewport.ConstructionPlane(), loan.Moved))
                borrowed[viewport.Id] = loan = new BorrowedPlane { Before = viewport.GetConstructionPlane() };
            loan.Moved = plane;
            loan.Parked = false;
            MovePlane(viewport, plane);
        }

        // False if the tool had not moved this viewport's plane.
        bool ReturnPlane(RhinoViewport viewport)
        {
            if (!borrowed.TryGetValue(viewport.Id, out BorrowedPlane loan)) return false;
            borrowed.Remove(viewport.Id);
            // If the user has moved the plane themselves since, theirs stands.
            if (Same(viewport.ConstructionPlane(), loan.Moved)) viewport.SetConstructionPlane(loan.Before);
            return true;
        }

        void ReturnPlanes()
        {
            if (borrowed.Count == 0) return;
            foreach (RhinoViewport viewport in Viewports(null)) ReturnPlane(viewport);
            borrowed.Clear(); // what is left belonged to viewports that are gone
        }

        // A file saved while a plane is on loan must get the viewport's own plane, or the
        // moved one would be there for good the next time the file is opened. So the
        // planes are put back for the length of the save.
        void OnBeginSave(object sender, DocumentSaveEventArgs e) => ParkPlanes(e.Document, true);
        void OnEndSave(object sender, DocumentSaveEventArgs e) => ParkPlanes(e.Document, false);

        void ParkPlanes(RhinoDoc doc, bool park)
        {
            if (borrowed.Count == 0 || doc == null) return;
            foreach (RhinoViewport viewport in Viewports(doc))
            {
                if (!borrowed.TryGetValue(viewport.Id, out BorrowedPlane loan) || loan.Parked == park) continue;
                if (park)
                {
                    if (!Same(viewport.ConstructionPlane(), loan.Moved)) continue;
                    viewport.SetConstructionPlane(loan.Before);
                }
                else
                {
                    MovePlane(viewport, loan.Moved);
                }
                loan.Parked = park;
            }
        }

        // Unlike a push, this leaves nothing behind on the viewport's stack of planes.
        static void MovePlane(RhinoViewport viewport, Plane plane)
        {
            ConstructionPlane moved = viewport.GetConstructionPlane(); // keeps its grid settings
            moved.Plane = plane;
            viewport.SetConstructionPlane(moved);
        }

        static bool Same(Plane a, Plane b)
        {
            double tolerance = 1e-6 * (1 + Math.Abs(a.Origin.X) + Math.Abs(a.Origin.Y) + Math.Abs(a.Origin.Z));
            return a.Origin.EpsilonEquals(b.Origin, tolerance) &&
                   a.XAxis.EpsilonEquals(b.XAxis, 1e-6) &&
                   a.YAxis.EpsilonEquals(b.YAxis, 1e-6);
        }

        // The active viewport of every view, in one document or (null) in all that are open.
        static IEnumerable<RhinoViewport> Viewports(RhinoDoc only)
        {
            foreach (RhinoDoc doc in RhinoDoc.OpenDocuments())
            {
                if (only != null && doc.RuntimeSerialNumber != only.RuntimeSerialNumber) continue;
                foreach (RhinoView view in doc.Views) yield return view.ActiveViewport;
            }
        }

        // The plane Rhino gives a viewport to begin with: world XY, or the plane facing
        // the camera in the standard parallel views (Front, Right, ...).
        static Plane DefaultPlane(RhinoViewport viewport)
        {
            Vector3d direction = viewport.CameraDirection;
            direction.Unitize();
            bool axisAligned = Math.Abs(direction.X) > 0.9999 || Math.Abs(direction.Y) > 0.9999;
            if (viewport.IsParallelProjection && axisAligned)
                return new Plane(Point3d.Origin, viewport.CameraX, viewport.CameraY);
            return Plane.WorldXY;
        }

        // "Place cursor", then a click: move the 3D cursor to the surface under the
        // pointer. Where there is none, a perspective view puts it on the construction
        // plane; a parallel view slides it across the screen, keeping its depth.
        void MoveCursor(RhinoView view, ScreenPoint point)
        {
            var viewport = view.ActiveViewport;
            if (!ViewLine(viewport, point, out Line line)) return;
            Plane fallback = viewport.IsParallelProjection
                ? new Plane(cursor, viewport.CameraDirection)
                : viewport.ConstructionPlane();
            Hit hit = HitSurface(viewport, line, CollectTargets(view.Document))
                      ?? HitPlane(viewport, line, fallback);
            if (hit == null) return;
            cursor = hit.Point;
            view.Document.Views.Redraw();
        }

        // ------------------------------------------------------------- views

        /// <summary>
        /// Keys 1 / 2 / 3: switch the active viewport to a parallel Right / Front / Top view
        /// centred on the 3D cursor, and to drawing at the cursor, which in such a view is
        /// drawing on the plane through it that faces the camera. 0: back to the
        /// perspective view as it was. Only the view changes, never the construction plane.
        /// </summary>
        public void SetView(int key)
        {
            var doc = RhinoDoc.ActiveDoc;
            var view = doc?.Views.ActiveView;
            if (view == null || key < 0 || key > 3) return;
            EndDrag();

            var viewport = view.ActiveViewport;
            if (key == 0)
            {
                RestorePerspective(viewport);
            }
            else
            {
                ShowAxisView(doc, viewport, key);
                SetTool(SketchTool.Draw);
                SetMethod(DrawMethod.Cursor);
            }
            Refresh();
        }

        void ShowAxisView(RhinoDoc doc, RhinoViewport viewport, int key)
        {
            // from: where the camera sits relative to the cursor.
            Vector3d from, up;
            string name;
            switch (key)
            {
                case 1: name = "Right"; from = Vector3d.XAxis; up = Vector3d.ZAxis; break;
                case 2: name = "Front"; from = -Vector3d.YAxis; up = Vector3d.ZAxis; break;
                default: name = "Top"; from = Vector3d.ZAxis; up = Vector3d.YAxis; break;
            }

            var info = new ViewportInfo(viewport);
            if (viewport.IsPerspectiveProjection)
            {
                savedViews[viewport.Id] = new SavedView
                {
                    Info = new ViewportInfo(viewport),
                    Target = viewport.CameraTarget,
                    Name = viewport.Name,
                };

                // Size the parallel view so things at the cursor's depth keep the size
                // they had in perspective.
                info.GetFrustum(out double left, out double right, out double bottom, out double top, out double near, out double far);
                Vector3d forward = viewport.CameraDirection; // not unit length: it reaches to the camera target
                forward.Unitize();
                double depth = (cursor - viewport.CameraLocation) * forward;
                if (depth <= near) depth = (viewport.CameraTarget - viewport.CameraLocation) * forward;
                if (depth <= near) depth = near;
                double halfWidth = (right - left) / 2 * depth / near;
                double halfHeight = (top - bottom) / 2 * depth / near;
                info.ChangeToParallelProjection(true);
                info.GetFrustum(out left, out right, out bottom, out top, out near, out far);
                info.SetFrustum(-halfWidth, halfWidth, -halfHeight, halfHeight, near, far);
            }

            // Stand far enough back that the whole model is in front of the camera.
            double distance = viewport.CameraLocation.DistanceTo(cursor);
            BoundingBox everything = doc.Objects.BoundingBox;
            if (everything.IsValid) distance = Math.Max(distance, everything.Diagonal.Length * 2);
            if (distance < 1) distance = 1;

            info.SetCameraLocation(cursor + from * distance);
            info.SetCameraDirection(-from);
            info.SetCameraUp(up);
            info.TargetPoint = cursor;
            viewport.SetViewProjection(info, true);
            viewport.SetCameraTarget(cursor, false);
            viewport.Name = name;
        }

        void RestorePerspective(RhinoViewport viewport)
        {
            if (savedViews.TryGetValue(viewport.Id, out SavedView saved))
            {
                savedViews.Remove(viewport.Id);
                // The viewport may have been resized since the view was saved.
                var size = viewport.Size;
                if (size.Width > 0 && size.Height > 0) saved.Info.FrustumAspect = (double)size.Width / size.Height;
                viewport.SetViewProjection(saved.Info, true);
                viewport.SetCameraTarget(saved.Target, false);
                viewport.Name = saved.Name;
            }
            else if (viewport.IsParallelProjection)
            {
                viewport.SetProjection(DefinedViewportProjection.Perspective, "Perspective", false);
            }
        }

        // ------------------------------------------------------- hit testing

        // The line through the scene that sits under a screen point, pointing away from the camera.
        static bool ViewLine(RhinoViewport viewport, ScreenPoint point, out Line line)
        {
            line = viewport.ClientToWorld(point);
            if (!line.IsValid || line.Length <= 0) return false;
            if ((line.To - line.From) * viewport.CameraDirection < 0) line.Flip();
            return true;
        }

        static Hit HitPlane(RhinoViewport viewport, Line line, Plane plane)
        {
            Vector3d direction = line.Direction;
            direction.Unitize();
            if (Math.Abs(direction * plane.Normal) < GrazingLimit) return null;
            if (!Intersection.LinePlane(line, plane, out double t)) return null;
            Point3d point = line.PointAt(t);
            if (viewport.IsPerspectiveProjection &&
                (point - viewport.CameraLocation) * viewport.CameraDirection <= 0) return null; // behind the camera
            return new Hit { Point = point, Normal = plane.Normal };
        }

        // Nearest point along the line on any visible surface-like object. The line is shot
        // at the objects' display meshes (fast, and it is what you see on screen), then the
        // result is pulled onto the true surface.
        static Hit HitSurface(RhinoViewport viewport, Line line, List<Target> targets)
        {
            var ray = new Ray3d(line.From, line.Direction);
            Target nearest = null;
            double nearestT = double.MaxValue;

            foreach (var target in targets)
            {
                if (!Intersection.LineBox(line, target.Box, 0, out Interval _)) continue;
                target.Prepare();
                if (target.Meshes == null) continue;
                foreach (var mesh in target.Meshes)
                {
                    if (mesh == null) continue;
                    double t = Intersection.MeshRay(mesh, ray);
                    if (t >= 0 && t < nearestT)
                    {
                        nearestT = t;
                        nearest = target;
                    }
                }
            }
            if (nearest == null) return null;

            var hit = new Hit { Point = ray.PointAt(nearestT), ObjectId = nearest.Id, Normal = Vector3d.Unset };
            if (nearest.Brep != null)
            {
                double reach = Math.Max(nearest.Box.Diagonal.Length * 0.05, 1e-6);
                if (nearest.Brep.ClosestPoint(hit.Point, out Point3d onSurface, out ComponentIndex _, out double _, out double _, reach, out Vector3d normal))
                {
                    hit.Point = onSurface;
                    hit.Normal = normal;
                }
            }
            if (!hit.Normal.IsValid || hit.Normal.IsTiny())
            {
                foreach (var mesh in nearest.Meshes)
                {
                    if (mesh == null) continue;
                    if (mesh.ClosestPoint(hit.Point, out Point3d _, out Vector3d meshNormal, nearest.Box.Diagonal.Length * 0.01) >= 0)
                    {
                        hit.Normal = meshNormal;
                        break;
                    }
                }
            }
            if (!hit.Normal.IsValid || hit.Normal.IsTiny()) hit.Normal = -viewport.CameraDirection;
            hit.Normal.Unitize();
            return hit;
        }

        static List<Target> CollectTargets(RhinoDoc doc)
        {
            var targets = new List<Target>();
            var settings = new ObjectEnumeratorSettings
            {
                ObjectTypeFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD,
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = false,
                DeletedObjects = false,
                VisibleFilter = true,
            };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                BoundingBox box = obj.Geometry.GetBoundingBox(false);
                if (!box.IsValid) continue;
                box.Inflate(Math.Max(box.Diagonal.Length * 1e-4, doc.ModelAbsoluteTolerance)); // flat objects have no thickness
                targets.Add(new Target { Id = obj.Id, Object = obj, Box = box });
            }
            return targets;
        }

        // ------------------------------------------------------------- data

        sealed class Hit
        {
            public Point3d Point;
            public Vector3d Normal;
            public Guid ObjectId; // Guid.Empty for a plane
        }

        // An object strokes can be drawn on. Its meshes are only fetched once the pointer
        // actually passes over its bounding box.
        sealed class Target
        {
            public Guid Id;
            public RhinoObject Object;
            public BoundingBox Box;
            public Mesh[] Meshes;
            public Brep Brep;
            bool prepared;

            public void Prepare()
            {
                if (prepared) return;
                prepared = true;

                GeometryBase geometry = Object.Geometry;
                if (geometry is Mesh mesh)
                {
                    Meshes = new[] { mesh };
                    return;
                }

                if (geometry is Brep brep) Brep = brep;
                else if (geometry is Surface surface) Brep = surface.ToBrep(); // includes extrusions

                Meshes = Object.GetMeshes(MeshType.Render);
                if (Meshes != null && Meshes.Length > 0) return;

                // Nothing cached (the object has never been shaded): mesh it ourselves.
                if (Brep != null) Meshes = Mesh.CreateFromBrep(Brep, MeshingParameters.FastRenderMesh);
                else if (geometry is SubD subd) Meshes = new[] { Mesh.CreateFromSubD(subd, 3) };
            }
        }

        sealed class DrawDrag
        {
            public RhinoView View;
            public Plane Plane;
            public List<Target> Targets;
            public Color Color;
            public DrawMethod Method;
            public Guid ObjectId;
            public ScreenPoint Last;

            // The stroke so far, one entry per point.
            public readonly List<Point3d> Points = new List<Point3d>();
            public readonly List<double> Pressures = new List<double>(); // 0 to 1
            public readonly List<double> Scales = new List<double>();    // screen pixels per model unit there
            public readonly List<double> Travel = new List<double>();    // pixels drawn to get there

            public double Pressure; // at the last point
            public long Time;       // when the last point was added, in Stopwatch ticks
            public bool Pen;        // the pressure is a pen's own, not made up
        }

        sealed class EraseDrag
        {
            public RhinoView View;
            public List<Stroke> Strokes;
        }

        sealed class Stroke
        {
            public Guid Id;
            public Point3d[] Points;
            public ObjectAttributes Attributes;
            public StrokePaint Paint; // null for a stroke drawn before there were brushes
        }

        // A construction plane the tool has moved onto a surface, to be given back.
        sealed class BorrowedPlane
        {
            public ConstructionPlane Before; // the plane as the viewport had it
            public Plane Moved;              // where the tool put it
            public bool Parked;              // back in place for the length of a save
        }

        sealed class SavedView
        {
            public ViewportInfo Info;
            public Point3d Target;
            public string Name;
        }
    }
}
