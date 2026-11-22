#if MAC
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rhino.Display;

namespace RhinoSketchingTool
{
    /// <summary>
    /// How big the interface over a viewport is drawn on a Mac: how many of the viewport's
    /// own units make one pixel at 100 %. On a Retina display that is 2, and 1 on an
    /// ordinary one. (Toolbar.Scale's answer; on Windows that comes from the window's DPI.)
    ///
    /// Rhino says how dense the display is while it draws a view (DisplayPipeline.DpiScale),
    /// so the answer for each view is noted then, and used for the pointer afterwards.
    /// </summary>
    internal static class MacDisplay
    {
        static readonly Dictionary<uint, double> scales = new Dictionary<uint, double>(); // by RhinoView.RuntimeSerialNumber
        /// <summary>Viewport units per logical pixel, on the display that owns the view.</summary>
        public static double Scale(RhinoView view)
        {
            if (view == null) return 1;
            if (scales.TryGetValue(view.RuntimeSerialNumber, out double scale)) return scale;
            try
            {
                DisplayPipeline pipeline = view.DisplayPipeline;
                double display = pipeline != null ? pipeline.DpiScale : 0;
                if (display >= 0.5 && display <= 8) return display;
            }
            catch (Exception)
            {
                // No pipeline to ask yet.
            }
            return Window(view);
        }

        /// <summary>Call from a conduit, as a view is being drawn.</summary>
        public static void Note(DrawEventArgs e)
        {
            try
            {
                if (e.Display.IsPrinting || e.Display.IsInViewCapture) return; // not the screen
                RhinoView view = e.Viewport?.ParentView;
                if (view == null || e.Viewport.ViewportType == ViewportType.DetailViewport) return;

                // The pipeline belongs to this view, including a floating viewport moved
                // to a different monitor from its document. Use Eto only as a fallback.
                double display = e.Display.DpiScale;
                if (!(display >= 0.5 && display <= 8)) display = Window(view);

                // A viewport is measured in pixels of the display: its frame is exactly as wide.
                // Were it measured in the coarser units instead, the frame would be wider by the
                // display's own figure, and then one unit is one pixel at 100 % already.
                double units = FramePixelsPerUnit(e.Display.FrameSize, e.Viewport.Size, display);
                if (scales.Count > 200) scales.Clear();
                scales[view.RuntimeSerialNumber] = display / units;
            }
            catch (Exception)
            {
                // Whatever was noted before stands.
            }
        }

        // A frame and viewport that differ by the backing scale in both dimensions
        // describe the same view in physical pixels and logical points respectively.
        internal static double FramePixelsPerUnit(System.Drawing.Size frame, System.Drawing.Size port, double density)
        {
            if (density <= 1 || port.Width <= 0 || port.Height <= 0) return 1;
            return Math.Abs(frame.Width - port.Width * density) <= 1 &&
                   Math.Abs(frame.Height - port.Height * density) <= 1 ? density : 1;
        }

        static double Window(RhinoView view)
        {
            try
            {
                var owner = Rhino.UI.RhinoEtoApp.MainWindowForDocument(view.Document);
                return owner?.LogicalPixelSize ?? 1;
            }
            catch (Exception)
            {
                // No window to ask yet.
            }
            return 1;
        }
    }

    /// <summary>
    /// How hard a pen is pressing, on a Mac: PenHook.Pressure's answer there.
    ///
    /// A graphics tablet, or an iPad used as one (Sidecar), reaches a Mac app as mouse
    /// events that are marked as coming from a tablet and carry the pen's pressure. Rhino
    /// passes its mouse events on without it, so this asks the app for the event in hand.
    /// A mouse or a trackpad is not a tablet, and gets the pressure made up from speed, as
    /// on Windows.
    ///
    /// Set the environment variable CRAYON_NO_PEN to anything to leave pens out altogether.
    /// </summary>
    internal static class MacPen
    {
        const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

        // One entry point, declared once for each kind of answer it is asked for.
        [DllImport(ObjectiveC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjectiveC)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] static extern IntPtr SendForObject(IntPtr receiver, IntPtr selector);
        [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] static extern nint SendForNumber(IntPtr receiver, IntPtr selector);
        [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] static extern short SendForShort(IntPtr receiver, IntPtr selector);
        [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] static extern float SendForFloat(IntPtr receiver, IntPtr selector);

        const long LeftMouseDown = 1, LeftMouseDragged = 6, TabletPoint = 23;
        const short TabletPointSubtype = 1;

        static bool ready, off;
        static IntPtr application, currentEvent, type, subtype, pressure;

        /// <summary>
        /// The pressure of the pen that is on the tablet right now, from 0 to 1.
        /// Negative if there is none: the pointer is a mouse or a trackpad.
        /// </summary>
        public static double Pressure
        {
            get
            {
                if (off) return -1;
                try
                {
                    if (!ready)
                    {
                        ready = true;
                        if (Environment.GetEnvironmentVariable("CRAYON_NO_PEN") != null) { off = true; return -1; }
                        IntPtr appClass = objc_getClass("NSApplication");
                        if (appClass == IntPtr.Zero) { off = true; return -1; }
                        application = SendForObject(appClass, sel_registerName("sharedApplication"));
                        currentEvent = sel_registerName("currentEvent");
                        type = sel_registerName("type");
                        subtype = sel_registerName("subtype");
                        pressure = sel_registerName("pressure");
                        if (application == IntPtr.Zero) { off = true; return -1; }
                    }

                    IntPtr e = SendForObject(application, currentEvent);
                    if (e == IntPtr.Zero) return -1;
                    // AppKit can deliver a tablet point directly or as a mouse subtype.
                    // Only query subtype on mouse events and pressure on tablet input.
                    long kind = SendForNumber(e, type);
                    if (kind != TabletPoint)
                    {
                        if (kind != LeftMouseDown && kind != LeftMouseDragged) return -1;
                        if (SendForShort(e, subtype) != TabletPointSubtype) return -1;
                    }
                    float reading = SendForFloat(e, pressure);
                    return reading >= 0 && reading <= 1 ? reading : -1;
                }
                catch (Exception)
                {
                    off = true; // no pen pressure on this system: a mouse's, from here on
                    return -1;
                }
            }
        }
    }
}
#endif
