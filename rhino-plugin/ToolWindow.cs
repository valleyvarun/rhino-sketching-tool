using System;
using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace RhinoSketchingTool
{
    /// <summary>
    /// The little floating tool bar. It only shows and changes the controller's state;
    /// the tools are live for as long as this window is open.
    /// </summary>
    internal sealed class ToolWindow : Form
    {
        static ToolWindow instance;

        readonly SketchController controller;
        readonly SegmentedButton tools = Segments(SegmentedSelectionMode.Single, "Draw", "Erase", "Select");
        readonly SegmentedButton methods = Segments(SegmentedSelectionMode.Single, "Surface", "Plane", "3D cursor");
        readonly SegmentedButton erasers = Segments(SegmentedSelectionMode.Single, "Whole stroke", "Part");
        readonly SegmentedButton views = Segments(SegmentedSelectionMode.None, "Right  1", "Front  2", "Top  3", "Perspective  0");
        readonly Label hint = new Label { Wrap = WrapMode.Word, Width = 330, Height = 84 };
        bool syncing;

        public static void ShowWindow()
        {
            if (instance == null)
            {
                var controller = SketchPlugin.Instance.Controller;
                instance = new ToolWindow(controller);
                instance.Show();
                controller.Active = true; // the tools are live from the moment the window is up
            }
            else
            {
                instance.BringToFront();
            }
        }

        public static void CloseWindow() => instance?.Close();

        ToolWindow(SketchController controller)
        {
            this.controller = controller;

            Title = "Sketch Tools";
            WindowStyle = WindowStyle.Utility;
            Resizable = false;
            Maximizable = false;
            Minimizable = false;
            ShowInTaskbar = false;
            Owner = RhinoEtoApp.MainWindow; // stay above Rhino without being always-on-top

            var table = new TableLayout { Spacing = new Size(10, 8) };
            table.Rows.Add(Row("Tool", tools));
            table.Rows.Add(Row("Draw on", methods));
            table.Rows.Add(Row("Erase", erasers));
            table.Rows.Add(Row("View", views));
            Content = new StackLayout
            {
                Padding = 12,
                Spacing = 10,
                Items = { table, hint },
            };

            tools.SelectedIndexChanged += (s, e) => Apply(() => controller.Tool = (SketchTool)tools.SelectedIndex, tools);
            methods.SelectedIndexChanged += (s, e) => Apply(() => controller.Method = (DrawMethod)methods.SelectedIndex, methods);
            erasers.SelectedIndexChanged += (s, e) => Apply(() => controller.Erase = (EraseMode)erasers.SelectedIndex, erasers);

            // Right / Front / Top / Perspective are the number keys 1 / 2 / 3 / 0.
            int[] keys = { 1, 2, 3, 0 };
            for (int i = 0; i < keys.Length; i++)
            {
                int key = keys[i];
                views.Items[i].Click += (s, e) => controller.SetView(key);
            }

            // The same keys while this window, rather than a viewport, has the focus.
            KeyDown += (s, e) =>
            {
                int key = e.Modifiers != Keys.None ? -1
                    : e.Key == Keys.D0 || e.Key == Keys.Keypad0 ? 0
                    : e.Key == Keys.D1 || e.Key == Keys.Keypad1 ? 1
                    : e.Key == Keys.D2 || e.Key == Keys.Keypad2 ? 2
                    : e.Key == Keys.D3 || e.Key == Keys.Keypad3 ? 3
                    : -1;
                if (key < 0) return;
                controller.SetView(key);
                e.Handled = true;
            };

            controller.Changed += OnControllerChanged;
            Closed += (s, e) =>
            {
                controller.Changed -= OnControllerChanged;
                controller.Active = false;
                instance = null;
            };

            Sync();
        }

        void OnControllerChanged(object sender, EventArgs e) => Sync();

        // A click on a segmented button: push it to the controller, unless the change came
        // from the controller in the first place.
        void Apply(Action change, SegmentedButton source)
        {
            if (syncing) return;
            if (source.SelectedIndex >= 0) change();
            Sync(); // also re-selects the segment if the click cleared it
        }

        void Sync()
        {
            syncing = true;
            try
            {
                tools.SelectedIndex = (int)controller.Tool;
                methods.SelectedIndex = (int)controller.Method;
                erasers.SelectedIndex = (int)controller.Erase;
                methods.Enabled = controller.Tool == SketchTool.Draw;
                erasers.Enabled = controller.Tool == SketchTool.Erase;
                hint.Text = Hint();
            }
            finally
            {
                syncing = false;
            }
        }

        string Hint()
        {
            const string views = "\n1 / 2 / 3: Right / Front / Top view at the 3D cursor.  0: perspective.";
            switch (controller.Tool)
            {
                case SketchTool.Select:
                    return "Sketching is paused: the left mouse button selects as usual in Rhino.";
                case SketchTool.Erase:
                    return controller.Erase == EraseMode.Stroke
                        ? "Drag over strokes to delete them whole."
                        : "Drag to rub out the parts of strokes under the pointer.";
            }
            switch (controller.Method)
            {
                case DrawMethod.Surface:
                    return "Drag on an object to draw on its surface." + views;
                case DrawMethod.Plane:
                    return "Drag to draw on the construction plane. Shift + Ctrl + click a surface to move the plane onto it; on empty space to reset it." + views;
                default:
                    return "Drag to draw at the 3D cursor. Shift + click moves the cursor." + views;
            }
        }

        static SegmentedButton Segments(SegmentedSelectionMode mode, params string[] labels)
        {
            var button = new SegmentedButton { SelectionMode = mode };
            foreach (string label in labels) button.Items.Add(new ButtonSegmentedItem { Text = label });
            return button;
        }

        // Label, then the control at its natural size against the left of its column.
        static TableRow Row(string label, Control control) =>
            new TableRow(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center }, TableLayout.AutoSized(control));
    }
}
