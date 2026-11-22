using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;
using Color = System.Drawing.Color;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Hands left-button presses and drags in a viewport to the sketch tools. Anything the
    /// tools use is hidden from Rhino (e.Cancel), so it does not also select objects.
    /// Everything else, including all right-button navigation, passes straight through.
    /// </summary>
    internal sealed class SketchMouse : MouseCallback
    {
        readonly SketchController controller;
        bool ownsClick; // the tools used the current press, so Rhino must not see its release either

        public SketchMouse(SketchController controller) => this.controller = controller;

        protected override void OnMouseDown(MouseCallbackEventArgs e) => Press(e, false);

        // The second press of a double-click comes here instead of to OnMouseDown.
        // Only the toolbar is interested: elsewhere a double-click is left to Rhino, as it was.
        protected override void OnMouseDoubleClick(MouseCallbackEventArgs e) => Press(e, true);

        void Press(MouseCallbackEventArgs e, bool doubleClick)
        {
            if (e.MouseButton != MouseButton.Left) return;
            ownsClick = false;
            Guard(() =>
            {
                // The toolbar first: a click on it is never for the viewport underneath.
                if (controller.Toolbar.Press(e.View, e.ViewportPoint, doubleClick))
                {
                    ownsClick = true;
                    e.Cancel = true;
                    return;
                }
                if (doubleClick) return;
                if (Command.InCommand()) return; // a Rhino command is asking for points; stay out of its way
                if (!controller.PointerDown(e.View, e.ViewportPoint, e.ShiftKeyDown, e.CtrlKeyDown, PenHook.Pressure)) return;
                ownsClick = true;
                e.Cancel = true;
            });
        }

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            Guard(() =>
            {
                bool leftDown = e.MouseButton == MouseButton.Left ||
                                Eto.Forms.Mouse.Buttons.HasFlag(Eto.Forms.MouseButtons.Primary);
                if (!controller.Dragging)
                {
                    controller.Toolbar.Move(e.View, e.ViewportPoint, leftDown);
                    if (controller.Toolbar.Hovering || controller.Toolbar.Busy) controller.HoverEnd();
                    else if (!leftDown) controller.Hover(e.View, e.ViewportPoint); // where a comment would land, and what the pointer is on
                    return;
                }
                if (leftDown) controller.PointerMove(e.View, e.ViewportPoint, PenHook.Pressure);
                else controller.PointerUp(); // released somewhere this callback could not see
            });
        }

        protected override void OnMouseUp(MouseCallbackEventArgs e)
        {
            if (e.MouseButton != MouseButton.Left || !ownsClick) return;
            ownsClick = false;
            e.Cancel = true;
            if (controller.Toolbar.Busy) Guard(() => controller.Toolbar.Release(e.View, e.ViewportPoint));
            else Guard(controller.PointerUp);
        }

        protected override void OnMouseLeave(MouseCallbackEventArgs e)
        {
            if (controller.Dragging) Guard(controller.PointerUp);
            Guard(controller.Toolbar.Leave);
            Guard(controller.HoverEnd);
        }

        // A bug in a mouse handler must never take Rhino down with it.
        static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Sketch tools: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Paints the sketch: every stroke in the open documents and the one being drawn, as
    /// brush dabs (see StrokeDabs), and draws the 3D cursor.
    ///
    /// Strokes are hidden objects (except while the Select tool is on; see
    /// StrokesSelectable), so Rhino never asks to have them drawn. The conduit
    /// keeps its own list of them for each document, and reads it again whenever a curve
    /// or a layer in that document changes.
    /// </summary>
    internal sealed class SketchConduit : DisplayConduit
    {
        const double CursorPixels = 28; // length of each cursor axis on screen

        static readonly Color AxisX = Color.FromArgb(214, 60, 48);
        static readonly Color AxisY = Color.FromArgb(52, 160, 88);
        static readonly Color AxisZ = Color.FromArgb(46, 110, 224);

        readonly SketchController controller;
        readonly Dictionary<uint, Sheet> sheets = new Dictionary<uint, Sheet>(); // by RhinoDoc.RuntimeSerialNumber

        int frameNumber;
        int redrawFrame = -100; // the last frame that asked for another one

        StrokeDabs live; // the stroke being drawn
        int liveVersion = -1;

        // The painted strokes of one document.
        sealed class Sheet
        {
            public bool Stale = true; // the document has changed since this was read
            public readonly List<Painted> Strokes = new List<Painted>();

            // By RhinoObject.RuntimeSerialNumber, which Rhino changes whenever an object
            // does: a stroke found here again needs no new dabs.
            public Dictionary<uint, StrokeDabs> Dabs = new Dictionary<uint, StrokeDabs>();

            public readonly List<Guid> Misplaced = new List<Guid>();     // strokes hidden when they should show, or the reverse
            public readonly HashSet<Guid> Refused = new HashSet<Guid>(); // ... and would not be changed
        }

        struct Painted
        {
            public StrokeDabs Dabs;
            public int Layer;
            public string ViewKey; // drawn over this saved view: painted only there, over everything. Null for a 3D sketch
            public bool Visible;
        }

        readonly Hairlines hairlines;
        bool selectable;

        public SketchConduit(SketchController controller)
        {
            this.controller = controller;
            hairlines = new Hairlines(this);
        }

        protected override void OnEnable(bool enable)
        {
            if (enable)
            {
                RhinoDoc.AddRhinoObject += OnObject;
                RhinoDoc.DeleteRhinoObject += OnObject;
                RhinoDoc.UndeleteRhinoObject += OnObject;
                RhinoDoc.PurgeRhinoObject += OnObject;
                RhinoDoc.ReplaceRhinoObject += OnReplaced;
                RhinoDoc.ModifyObjectAttributes += OnAttributes;
                RhinoDoc.LayerTableEvent += OnLayers;
                RhinoDoc.EndOpenDocument += OnOpened;
                RhinoDoc.CloseDocument += OnClosed;
                AnnotationGroups.Changed += Changed;
            }
            else
            {
                RhinoDoc.AddRhinoObject -= OnObject;
                RhinoDoc.DeleteRhinoObject -= OnObject;
                RhinoDoc.UndeleteRhinoObject -= OnObject;
                RhinoDoc.PurgeRhinoObject -= OnObject;
                RhinoDoc.ReplaceRhinoObject -= OnReplaced;
                RhinoDoc.ModifyObjectAttributes -= OnAttributes;
                RhinoDoc.LayerTableEvent -= OnLayers;
                RhinoDoc.EndOpenDocument -= OnOpened;
                RhinoDoc.CloseDocument -= OnClosed;
                AnnotationGroups.Changed -= Changed;
                sheets.Clear();
                hairlines.Enabled = false;
            }
        }

        // ------------------------------------------------- the list of strokes

        // Something changed in a document. Only curves and layers matter here: a stroke
        // is a curve, and takes its colour and visibility from its layer.
        void OnObject(object sender, RhinoObjectEventArgs e) => Changed(e.TheObject);
        void OnReplaced(object sender, RhinoReplaceObjectEventArgs e) => Changed(e.NewRhinoObject);
        void OnAttributes(object sender, RhinoModifyObjectAttributesEventArgs e) => Changed(e.RhinoObject);
        void OnLayers(object sender, Rhino.DocObjects.Tables.LayerTableEventArgs e) => Changed(e.Document);
        void OnOpened(object sender, DocumentOpenEventArgs e)
        {
            // Have a (stale) sheet for the document at once, so that Tidy looks at it
            // even before any of its views has been drawn.
            if (e.Document != null && !sheets.ContainsKey(e.Document.RuntimeSerialNumber))
                sheets[e.Document.RuntimeSerialNumber] = new Sheet();
            Changed(e.Document);
        }

        void OnClosed(object sender, DocumentEventArgs e)
        {
            if (e.Document != null) sheets.Remove(e.Document.RuntimeSerialNumber);
            SketchController.ForgetViewBindings(e.Document);
        }

        void Changed(RhinoObject obj)
        {
            if (obj != null && obj.ObjectType == ObjectType.Curve) Changed(obj.Document);
        }

        void Changed(RhinoDoc doc)
        {
            if (doc != null && sheets.TryGetValue(doc.RuntimeSerialNumber, out Sheet sheet)) sheet.Stale = true;
        }

        Sheet Read(RhinoDoc doc)
        {
            if (!sheets.TryGetValue(doc.RuntimeSerialNumber, out Sheet sheet))
                sheets[doc.RuntimeSerialNumber] = sheet = new Sheet();
            if (!sheet.Stale) return sheet;
            sheet.Stale = false;

            Dictionary<uint, StrokeDabs> before = sheet.Dabs;
            sheet.Dabs = new Dictionary<uint, StrokeDabs>();
            sheet.Strokes.Clear();
            sheet.Misplaced.Clear();
            foreach (RhinoObject obj in SketchController.StrokeObjects(doc))
            {
                // The ink is the curve's display colour, which follows its layer's.
                Color color = obj.Attributes.DrawColor(doc);
                uint serial = obj.RuntimeSerialNumber;
                if (!before.TryGetValue(serial, out StrokeDabs dabs) || dabs.Color.ToArgb() != color.ToArgb())
                    dabs = Build(obj, color);
                if (dabs == null) continue; // drawn before there were brushes: an ordinary curve, left to Rhino

                sheet.Dabs[serial] = dabs;
                bool groupVisible = AnnotationGroups.Visible(doc, obj.Attributes);
                string viewKey = obj.Attributes.GetUserString(SketchController.ViewStrokeKey);
                sheet.Strokes.Add(new Painted { Dabs = dabs, Layer = obj.Attributes.LayerIndex, ViewKey = viewKey, Visible = groupVisible });
                // A sketch over a view must never become native geometry visible/selectable in
                // every viewport. It remains available to Crayon's scoped eraser.
                bool shouldSelect = selectable && groupVisible && string.IsNullOrEmpty(viewKey);
                if (obj.IsHidden == shouldSelect && !sheet.Refused.Contains(obj.Id)) sheet.Misplaced.Add(obj.Id);
            }
            return sheet;
        }

        static StrokeDabs Build(RhinoObject obj, Color color)
        {
            if (obj.Attributes.GetUserString(StrokePaint.BrushKey) == null) return null;
            if (!(obj.Geometry is Curve curve)) return null;
            double tolerance = obj.Document != null ? obj.Document.ModelAbsoluteTolerance : 0.01;
            Point3d[] points = SketchController.StrokePoints(curve, tolerance);
            if (points == null) return null;
            return StrokeDabs.Build(points, StrokePaint.Read(obj.Attributes, points.Length), color);
        }

        // The strokes to paint in a view: all of the document's whose layer is on. The 3D
        // ones, which every view shows, or (viewOnly) the ones drawn over the saved view
        // this viewport is in.
        IEnumerable<StrokeDabs> Strokes(DrawEventArgs e, bool viewOnly = false)
        {
            RhinoDoc doc = e.RhinoDoc;
            // Strokes belong to the model. A layout page shows them through its details.
            if (doc == null || e.Viewport.ViewportType == ViewportType.PageViewMainViewport) yield break;

            int layer = -1;
            bool visible = true;
            string currentView = null;
            bool asked = false; // which saved view the viewport is in is only worked out if a sketch needs it
            foreach (Painted stroke in Read(doc).Strokes)
            {
                if (!stroke.Visible) continue;
                bool viewStroke = !string.IsNullOrEmpty(stroke.ViewKey);
                if (viewStroke != viewOnly) continue;
                if (viewStroke)
                {
                    if (!asked) { asked = true; currentView = SketchController.ViewKeyFor(doc, e.Viewport); }
                    if (stroke.ViewKey != currentView) continue;
                }
                if (stroke.Layer != layer) // strokes mostly share one layer: ask about it once
                {
                    layer = stroke.Layer;
                    Layer found = layer >= 0 && layer < doc.Layers.Count ? doc.Layers[layer] : null;
                    visible = found == null || found.IsVisible;
                }
                if (visible) yield return stroke.Dabs;
            }
        }

        /// <summary>
        /// Whether strokes are, for now, ordinary curves that Rhino can select, rather
        /// than hidden objects. That is so only while the Select tool is on: selecting,
        /// and then moving, copying or deleting, is something Rhino does to objects it
        /// can see. The catch is that object snaps can see them then, too.
        /// </summary>
        public bool StrokesSelectable
        {
            get => selectable;
            set
            {
                if (selectable == value) return;
                selectable = value;
                hairlines.Enabled = value;
                foreach (Sheet sheet in sheets.Values)
                {
                    sheet.Refused.Clear();
                    sheet.Stale = true; // every stroke is now the wrong way round
                }
            }
        }

        /// <summary>
        /// Called whenever Rhino has nothing else to do, and when the tool changes. Looks
        /// again at the documents that have changed, and puts right any stroke that is
        /// not as it should be: showing as a curve when strokes should be hidden (it was
        /// drawn by an earlier version, or the Show command has unhidden everything),
        /// or hidden when they should be selectable. Changing them waits (mayChange)
        /// until no command is running.
        /// </summary>
        public void Tidy(bool mayChange)
        {
            bool misplaced = false;
            foreach (var entry in sheets)
            {
                Sheet sheet = entry.Value;
                if (sheet.Stale)
                {
                    RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(entry.Key);
                    if (doc != null) Read(doc); // fills in the sheet that is already there
                }
                misplaced |= sheet.Misplaced.Count > 0;
            }
            if (misplaced && mayChange) HideOrShowStrokes();
        }

        void HideOrShowStrokes()
        {
            foreach (var entry in sheets)
            {
                Sheet sheet = entry.Value;
                if (sheet.Misplaced.Count == 0) continue;
                Guid[] ids = sheet.Misplaced.ToArray();
                sheet.Misplaced.Clear();
                RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(entry.Key);
                if (doc == null) continue;

                // This is housekeeping, not something the user did: it must not become a
                // step for Undo, nor make an untouched file ask to be saved.
                bool modified = doc.Modified, recording = doc.UndoRecordingEnabled;
                doc.UndoRecordingEnabled = false;
                try
                {
                    foreach (Guid id in ids)
                    {
                        var obj = doc.Objects.FindId(id);
                        bool shouldSelect = selectable && obj != null &&
                            AnnotationGroups.Visible(doc, obj.Attributes) &&
                            string.IsNullOrEmpty(obj.Attributes.GetUserString(SketchController.ViewStrokeKey));
                        bool done = shouldSelect ? doc.Objects.Show(id, true) : doc.Objects.Hide(id, true);
                        if (!done) sheet.Refused.Add(id);
                    }
                }
                finally
                {
                    doc.UndoRecordingEnabled = recording;
                    doc.Modified = modified;
                }
                sheet.Stale = true;
                doc.Views.Redraw();
            }
        }

        bool IsPainted(RhinoObject obj)
        {
            RhinoDoc doc = obj.Document;
            return doc != null && sheets.TryGetValue(doc.RuntimeSerialNumber, out Sheet sheet) &&
                   sheet.Dabs.ContainsKey(obj.RuntimeSerialNumber);
        }

        // While strokes are selectable, Rhino draws them, as hairline curves under
        // the paint. This keeps it from doing so, except for a stroke that is selected:
        // there the curve is what shows that it is. (A conduit of its own, on only while
        // it is needed, because it is asked about every object Rhino draws.)
        sealed class Hairlines : DisplayConduit
        {
            readonly SketchConduit strokes;

            public Hairlines(SketchConduit strokes) => this.strokes = strokes;

            protected override void PreDrawObject(DrawObjectEventArgs e)
            {
                RhinoObject obj = e.RhinoObject;
                if (obj == null || obj.ObjectType != ObjectType.Curve || !strokes.IsPainted(obj)) return;
                bool showCurve = obj.IsSelected(false) != 0 || obj.IsHighlighted(false) != 0 || obj.GripsOn;
                if (!showCurve) e.DrawObject = false;
            }
        }

        // ------------------------------------------------------------- drawing

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            // Rhino sizes the view volume to the objects it draws. It draws no strokes,
            // so tell it where they are, or they would be cut off. (They are painted a
            // little nearer the camera than their curves; PaintedBox allows for that.)
            foreach (StrokeDabs stroke in Strokes(e)) e.IncludeBoundingBox(stroke.PaintedBox(e.Viewport));
            foreach (StrokeDabs stroke in Strokes(e, true)) e.IncludeBoundingBox(stroke.PaintedBox(e.Viewport));
            StrokeDabs drawing = Live();
            if (drawing != null && controller.LiveVisibleInView(e.RhinoDoc, e.Viewport)) e.IncludeBoundingBox(drawing.PaintedBox(e.Viewport));

            if (controller.CursorVisible)
            {
                double length = AxisLength(e.Viewport);
                var reach = new Vector3d(length, length, length);
                e.IncludeBoundingBox(new BoundingBox(controller.Cursor - reach, controller.Cursor + reach));
            }
        }

        // Zoom Extents should take in the sketch as well.
        protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e)
        {
            foreach (StrokeDabs stroke in Strokes(e)) e.IncludeBoundingBox(stroke.PaintedBox(e.Viewport));
        }

        StrokeDabs Live()
        {
            int version = controller.LiveVersion;
            if (version != liveVersion)
            {
                liveVersion = version;
                var points = controller.LivePoints;
                live = points != null && points.Count >= 2
                    ? StrokeDabs.Build(points, controller.LivePaint, controller.LiveColor)
                    : null;
            }
            return live;
        }

        // The objects are on screen by now, so the paint can go on top of them.
        protected override void PostDrawObjects(DrawEventArgs e)
        {
            frameNumber++;
            foreach (StrokeDabs stroke in Strokes(e)) Paint(e.Display, e.Viewport, stroke);
            StrokeDabs drawing = Live();
            if (drawing != null && !controller.LiveIsView && controller.LiveVisibleInView(e.RhinoDoc, e.Viewport)) Paint(e.Display, e.Viewport, drawing);
        }

        void Paint(DisplayPipeline display, RhinoViewport viewport, StrokeDabs stroke)
        {
            // Objects in front still hide the paint, but dabs must not hide each other:
            // they overlap, and their corners are clear.
            bool cramped;
            display.PushDepthWriting(false);
            try
            {
                cramped = stroke.Paint(display, viewport);
            }
            finally
            {
                display.PopDepthWriting();
            }

            // The view volume turned out too shallow for the stroke to be brought fully
            // forward (see StrokeDabs.Lift). Ask for another frame, which usually has the
            // room. Not twice in a row, in case room is never to be had.
            if (cramped && frameNumber - redrawFrame > 8)
            {
                redrawFrame = frameNumber;
                Eto.Forms.Application.Instance.AsyncInvoke(() => RhinoDoc.ActiveDoc?.Views.Redraw());
            }
        }

        // The foreground is drawn over everything, so the cursor shows through objects.
        protected override void DrawForeground(DrawEventArgs e)
        {
            // Sketches over a view are notes on the picture, not part of the model: nothing
            // in the model hides them, and only the viewport that is in their view has them.
            var over = new List<StrokeDabs>(Strokes(e, true));
            StrokeDabs drawing = Live();
            if (drawing != null && controller.LiveIsView && controller.LiveVisibleInView(e.RhinoDoc, e.Viewport)) over.Add(drawing);
            if (over.Count > 0) // the pipeline is left alone when there is nothing of the kind to paint
            {
                e.Display.PushDepthTesting(false);
                try
                {
                    foreach (StrokeDabs stroke in over) Paint(e.Display, e.Viewport, stroke);
                }
                finally { e.Display.PopDepthTesting(); }
            }

            if (!controller.CursorVisible) return;
            Point3d origin = controller.Cursor;
            double length = AxisLength(e.Viewport);
            e.Display.DrawLine(origin, origin + Vector3d.XAxis * length, AxisX, 2);
            e.Display.DrawLine(origin, origin + Vector3d.YAxis * length, AxisY, 2);
            e.Display.DrawLine(origin, origin + Vector3d.ZAxis * length, AxisZ, 2);
            e.Display.DrawPoint(origin, PointStyle.RoundControlPoint, 3, Color.FromArgb(31, 35, 41));
        }

        // World length that covers CursorPixels on screen at the cursor, so it never changes size.
        double AxisLength(RhinoViewport viewport)
        {
            if (viewport.GetWorldToScreenScale(controller.Cursor, out double pixelsPerUnit) && pixelsPerUnit > 0)
#if MAC
                return CursorPixels * MacDisplay.Scale(viewport.ParentView) / pixelsPerUnit; // as long on a Retina display as on any other
#else
                return CursorPixels / pixelsPerUnit;
#endif
            return 1;
        }
    }

    /// <summary>
    /// While the sketch tools are on, makes the plain number keys 0-3 switch views, and
    /// passes on Esc: one press goes back to the Select tool, two in a row turn the
    /// tools off.
    ///
    /// Rhino sends every typed key to its command line and offers no way to claim one, so
    /// this watches the keyboard directly (Windows only). A number key is taken only when
    /// all of these hold, to stay out of the way of normal typing:
    ///   - no command is running (so typed coordinates and distances are untouched),
    ///   - the viewports have the keyboard focus (not a panel, dialog or text box),
    ///   - no modifier key is held,
    ///   - nothing has been typed since the last Enter / Esc (so "Sweep1" still works).
    ///
    /// Esc is never taken. Rhino gets every press, and cancels its command or clears the
    /// selection as usual. The tools hear of it as well.
    /// </summary>
    internal static class KeyHook
    {
        // Escape belongs to a focused text editor before it belongs to the drawing tools.
        public static readonly List<Func<bool>> TextEntry = new List<Func<bool>>();

        static bool Typing()
        {
            foreach (Func<bool> box in TextEntry)
                if (box()) return true;
            return false;
        }

#if MAC
        static Action<bool> onEscape;

        public static void Install(Action<int> digitHandler, Action<bool> escapeHandler, Action escapeTwiceHandler)
        {
            if (onEscape == null) RhinoApp.EscapeKeyPressed += Escape;
            onEscape = escapeHandler;
        }

        static void Escape(object sender, EventArgs e)
        {
            if (Typing()) return;
            bool running = Command.InCommand();
            Eto.Forms.Application.Instance.AsyncInvoke(() => onEscape?.Invoke(running));
        }

        public static void Remove()
        {
            RhinoApp.EscapeKeyPressed -= Escape;
            onEscape = null;
        }
#else
        const int WH_KEYBOARD = 2;
        const int HC_ACTION = 0;
        const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        const int DoublePress = 500; // milliseconds: two presses of Esc this close together are "twice"

        static readonly HookProc callback = OnKey; // held in a field so it is never garbage collected
        static IntPtr hook;
        static Action<int> onDigit;
        static Action<bool> onEscape; // told whether a Rhino command was running
        static Action onEscapeTwice;
        static bool typing;  // something is (probably) sitting on the command line
        static bool escaped; // the last key pressed was Esc,
        static int escapedAt; // at this time (Environment.TickCount)

        /// <summary>
        /// Call on the UI thread. digitHandler receives 0, 1, 2 or 3. escapeHandler is
        /// called for a press of Esc on its own, and told whether a Rhino command was
        /// running at the time (which that press will have cancelled);
        /// escapeTwiceHandler for the second of two presses in a row.
        /// </summary>
        public static void Install(Action<int> digitHandler, Action<bool> escapeHandler, Action escapeTwiceHandler)
        {
            onDigit = digitHandler;
            onEscape = escapeHandler;
            onEscapeTwice = escapeTwiceHandler;
            if (hook != IntPtr.Zero || !Rhino.Runtime.HostUtils.RunningOnWindows) return;
            typing = false;
            escaped = false;
            hook = SetWindowsHookEx(WH_KEYBOARD, callback, IntPtr.Zero, GetCurrentThreadId());
            Command.EndCommand += OnEndCommand;
        }

        public static void Remove()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            Command.EndCommand -= OnEndCommand;
        }

        static void OnEndCommand(object sender, CommandEventArgs e) => typing = false;

        static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                bool keyDown = (lParam.ToInt64() & 0x80000000L) == 0;
                bool repeat = (lParam.ToInt64() & 0x40000000L) != 0;
                if (code == HC_ACTION && keyDown)
                {
                    int key = wParam.ToInt32();
                    if (Typing())
                    {
                        escaped = false;
                        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                    }
                    if (!repeat)
                    {
                        // Act once the key message is out of the way, not from inside the
                        // hook. But note now whether a command is running: by then Rhino
                        // will have cancelled it.
                        bool twice = EscapedTwice(key, System.Environment.TickCount);
                        if (twice)
                        {
                            Eto.Forms.Application.Instance.AsyncInvoke(() => onEscapeTwice?.Invoke());
                        }
                        else if (key == VK_ESCAPE)
                        {
                            bool inCommand = Command.InCommand();
                            Eto.Forms.Application.Instance.AsyncInvoke(() => onEscape?.Invoke(inCommand));
                        }
                    }
                    if (Claim(key, repeat)) return (IntPtr)1;
                }
            }
            catch
            {
                // Never let a problem here disturb Rhino's keyboard handling.
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // True on the second of two presses of Esc in a row. Any other key in between,
        // or too long a pause, and the count starts again.
        static bool EscapedTwice(int key, int now)
        {
            if (key != VK_ESCAPE)
            {
                escaped = false;
                return false;
            }
            bool twice = escaped && unchecked(now - escapedAt) <= DoublePress;
            escaped = !twice; // after a pair, a third press starts a new one
            escapedAt = now;
            return twice;
        }

        // True if the key was one of ours and Rhino should not see it.
        static bool Claim(int key, bool repeat)
        {
            if (Command.InCommand() || !ViewportHasFocus())
            {
                typing = false;
                return false;
            }
            if (key == VK_RETURN || key == VK_ESCAPE || key == VK_SPACE)
            {
                typing = false;
                return false;
            }

            int digit = key >= 0x30 && key <= 0x33 ? key - 0x30  // top-row 0-3
                      : key >= 0x60 && key <= 0x63 ? key - 0x60  // number pad 0-3
                      : -1;
            bool modifier = Down(VK_SHIFT) || Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN);

            if (digit >= 0 && !modifier && !typing)
            {
                // Act once the key message is out of the way, not from inside the hook.
                if (!repeat) Eto.Forms.Application.Instance.AsyncInvoke(() => onDigit?.Invoke(digit));
                return true;
            }

            if (IsTextKey(key)) typing = true;
            return false;
        }

        static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;

        static bool IsTextKey(int key) =>
            (key >= 0x30 && key <= 0x39) || // digits
            (key >= 0x41 && key <= 0x5A) || // letters
            (key >= 0x60 && key <= 0x6F) || // number pad
            (key >= 0xBA && key <= 0xC0) || // punctuation
            (key >= 0xDB && key <= 0xDF) ||
            key == 0xE2;

        // Rhino keeps the keyboard focus on its main window while you work in the viewports
        // (keys typed there go to the command line). So "the viewports have the keyboard"
        // means the focus is on the main window, on a viewport, or on a frame between the
        // two - and not on a panel, a dialog or the command line's own text box.
        static bool ViewportHasFocus()
        {
            IntPtr focus = GetFocus();
            if (focus == IntPtr.Zero) return false;
            if (focus == RhinoApp.MainWindowHandle()) return true;
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return false;
            foreach (var view in doc.Views)
            {
                IntPtr handle = view.Handle;
                if (handle == focus || IsChild(handle, focus) || IsChild(focus, handle)) return true;
            }
            return false;
        }
#endif
    }

    /// <summary>
    /// Reads how hard a pen is pressing.
    ///
    /// Rhino's mouse events say nothing about pressure. But Windows first sends every pen
    /// movement to the window as a "pointer" message, which does carry it, and only then
    /// turns it into the mouse message Rhino acts on. So this watches the pointer messages
    /// go by (Windows only) and remembers the pressure in the latest one.
    ///
    /// That covers pens Windows knows as pens: tablet PCs, pen displays, and graphics
    /// tablets with "Windows Ink" on in their driver. Anything else is a mouse to us.
    /// </summary>
    internal static class PenHook
    {
#if MAC
        // AppKit supplies pressure on the current event without installing a hook.
        public static double Pressure => MacPen.Pressure;
        public static void Install() { }
        public static void Remove() { }
#else
        const int WH_GETMESSAGE = 3;
        const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERCAPTURECHANGED = 0x024C;
        const int WM_MOUSEFIRST = 0x0200, WM_MOUSELAST = 0x020E;
        const long FromPenMask = 0xFFFFFF00, FromPen = 0xFF515700; // how Windows marks a mouse message it made from a pen
        const int PT_PEN = 3;
        const uint POINTER_FLAG_INCONTACT = 0x0004;
        const uint PEN_MASK_PRESSURE = 0x0001;
        const double FullPressure = 1024;
        const int FreshMilliseconds = 250; // a reading older than this says nothing about now

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINTER_INFO
        {
            public int pointerType;
            public uint pointerId;
            public uint frameId;
            public uint pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int inputData;
            public uint dwKeyStates;
            public ulong performanceCount;
            public int buttonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINTER_PEN_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint penFlags;
            public uint penMask;
            public uint pressure; // 0 to 1024
            public uint rotation;
            public int tiltX, tiltY;
        }

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool GetPointerType(uint pointerId, out int pointerType);
        [DllImport("user32.dll")] static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        static readonly HookProc callback = OnMessage; // held in a field so it is never garbage collected
        static IntPtr hook;
        static double pressure = -1;
        static int readAt;

        /// <summary>
        /// The pressure of the pen that is on the surface right now, from 0 to 1.
        /// Negative if there is none: the pointer is a mouse, or a pen with no pressure sensing.
        /// </summary>
        public static double Pressure =>
            hook != IntPtr.Zero && pressure >= 0 && unchecked(System.Environment.TickCount - readAt) < FreshMilliseconds
                ? pressure
                : -1;

        /// <summary>Call on the UI thread.</summary>
        public static void Install()
        {
            if (hook != IntPtr.Zero || !Rhino.Runtime.HostUtils.RunningOnWindows) return;
            pressure = -1;
            hook = SetWindowsHookEx(WH_GETMESSAGE, callback, IntPtr.Zero, GetCurrentThreadId());
        }

        public static void Remove()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        // Called for every message the thread takes off its queue. lParam points at the
        // MSG: the window handle, then the message number, then its wParam.
        static IntPtr OnMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && lParam != IntPtr.Zero)
                {
                    int message = Marshal.ReadInt32(lParam, IntPtr.Size);
                    if (message >= WM_MOUSEFIRST && message <= WM_MOUSELAST)
                        LetPanelHavePenClick(Marshal.ReadIntPtr(lParam));
                    else if (message >= WM_POINTERUPDATE && message <= WM_POINTERCAPTURECHANGED)
                        Read(message, Marshal.ReadIntPtr(lParam, 2 * IntPtr.Size));
                }
            }
            catch
            {
                // Never let a problem here disturb Rhino's message handling.
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        /// <summary>The window the Sketch panel is in, or zero while it is not showing. Set by the panel.</summary>
        public static Func<IntPtr> PanelWindow;

        [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
        [DllImport("user32.dll")] static extern IntPtr SetMessageExtraInfo(IntPtr lParam);

        // A pen tap reaches a window as a mouse click that Windows marks as "made from a
        // pen". Rhino's panels (WPF) throw such clicks away, expecting to hear from the
        // pen directly, and in Rhino they do not: so a pen does nothing on the panel's
        // buttons and sliders. Taking the mark off, for the Sketch panel's window only,
        // makes the click an ordinary one.
        static void LetPanelHavePenClick(IntPtr window)
        {
            if ((GetMessageExtraInfo().ToInt64() & FromPenMask) != FromPen) return;
            if (PanelWindow != null && window == PanelWindow()) SetMessageExtraInfo(IntPtr.Zero);
        }

        static void Read(int message, IntPtr wParam)
        {
            uint pointer = (uint)(wParam.ToInt64() & 0xFFFF); // the pointer's id is the low word
            if (!GetPointerType(pointer, out int type) || type != PT_PEN) return;

            // Pressed down or moving, and touching: there is a pressure to read. Any other
            // pen message (lifted, left, hovering) means it is not pressing at all.
            double reading = -1;
            if ((message == WM_POINTERUPDATE || message == WM_POINTERDOWN) &&
                GetPointerPenInfo(pointer, out POINTER_PEN_INFO pen) &&
                (pen.pointerInfo.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                ((pen.penMask & PEN_MASK_PRESSURE) != 0 || pen.pressure > 0)) // some drivers leave the mask empty
            {
                reading = Math.Min(1, pen.pressure / FullPressure);
            }
            pressure = reading;
            readAt = System.Environment.TickCount;
        }
#endif
    }
}
