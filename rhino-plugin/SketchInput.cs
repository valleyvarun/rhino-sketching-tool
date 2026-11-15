using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
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

        protected override void OnMouseDown(MouseCallbackEventArgs e)
        {
            if (e.MouseButton != MouseButton.Left) return;
            ownsClick = false;
            if (Command.InCommand()) return; // a Rhino command is asking for points; stay out of its way
            Guard(() =>
            {
                if (!controller.PointerDown(e.View, e.ViewportPoint, e.ShiftKeyDown, e.CtrlKeyDown)) return;
                ownsClick = true;
                e.Cancel = true;
            });
        }

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            if (!controller.Dragging) return;
            Guard(() =>
            {
                bool leftDown = e.MouseButton == MouseButton.Left ||
                                Eto.Forms.Mouse.Buttons.HasFlag(Eto.Forms.MouseButtons.Primary);
                if (leftDown) controller.PointerMove(e.View, e.ViewportPoint);
                else controller.PointerUp(); // released somewhere this callback could not see
            });
        }

        protected override void OnMouseUp(MouseCallbackEventArgs e)
        {
            if (e.MouseButton != MouseButton.Left || !ownsClick) return;
            ownsClick = false;
            e.Cancel = true;
            Guard(controller.PointerUp);
        }

        protected override void OnMouseLeave(MouseCallbackEventArgs e)
        {
            if (controller.Dragging) Guard(controller.PointerUp);
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

    /// <summary>Draws what is not in the document: the stroke in progress and the 3D cursor.</summary>
    internal sealed class SketchConduit : DisplayConduit
    {
        const double CursorPixels = 28; // length of each cursor axis on screen

        static readonly Color AxisX = Color.FromArgb(214, 60, 48);
        static readonly Color AxisY = Color.FromArgb(52, 160, 88);
        static readonly Color AxisZ = Color.FromArgb(46, 110, 224);

        readonly SketchController controller;

        public SketchConduit(SketchController controller) => this.controller = controller;

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            var points = controller.LivePoints;
            if (points != null && points.Count > 0) e.IncludeBoundingBox(new BoundingBox(points));

            if (controller.CursorVisible)
            {
                double length = AxisLength(e.Viewport);
                var reach = new Vector3d(length, length, length);
                e.IncludeBoundingBox(new BoundingBox(controller.Cursor - reach, controller.Cursor + reach));
            }
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            var points = controller.LivePoints;
            if (points != null && points.Count >= 2) e.Display.DrawPolyline(points, SketchController.Ink, 2);
        }

        // The foreground is drawn over everything, so the cursor shows through objects.
        protected override void DrawForeground(DrawEventArgs e)
        {
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
                return CursorPixels / pixelsPerUnit;
            return 1;
        }
    }

    /// <summary>
    /// Makes the plain number keys 0-3 switch views while the tool window is open.
    ///
    /// Rhino sends every typed key to its command line and offers no way to claim one, so
    /// this watches the keyboard directly (Windows only) and takes a key only when all of
    /// these hold, to stay out of the way of normal typing:
    ///   - no command is running (so typed coordinates and distances are untouched),
    ///   - the viewports have the keyboard focus (not a panel, dialog or text box),
    ///   - no modifier key is held,
    ///   - nothing has been typed since the last Enter / Esc (so "Sweep1" still works).
    /// </summary>
    internal static class KeyHook
    {
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

        static readonly HookProc callback = OnKey; // held in a field so it is never garbage collected
        static IntPtr hook;
        static Action<int> onDigit;
        static bool typing; // something is (probably) sitting on the command line

        /// <summary>Call on the UI thread. onDigit receives 0, 1, 2 or 3.</summary>
        public static void Install(Action<int> handler)
        {
            onDigit = handler;
            if (hook != IntPtr.Zero || !Rhino.Runtime.HostUtils.RunningOnWindows) return;
            typing = false;
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
                if (code == HC_ACTION && keyDown && Claim(wParam.ToInt32(), repeat)) return (IntPtr)1;
            }
            catch
            {
                // Never let a problem here disturb Rhino's keyboard handling.
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
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
    }
}
