using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace RhinoSketchingTool
{
    /// <summary>Rhino owns docking; the design-system views own the panel's presentation.</summary>
    [Guid("b7c1f3a2-58d4-4e0b-9c36-7a1e2f4d8b90")]
    public sealed class SketchPanel : Panel, IPanel
    {
        readonly SketchController controller = SketchPlugin.Instance.Controller;
        readonly Panel content = new Panel();
        readonly AgentView agentView = new AgentView();
        readonly SidebarView header, settings;
        string tab = "agent";
        bool DarkAppearance()
        {
            bool dark = Theme.Dark; // Crayon's own light / dark switch, never Rhino's
            var color = dark ? Color.FromArgb(0x23, 0x24, 0x28) : Color.FromArgb(0xf6, 0xf7, 0xf9);
            if (BackgroundColor != color) BackgroundColor = color;
            return dark;
        }

#if MAC
        readonly uint documentSerial;
        bool listening;
        public SketchPanel(uint documentSerialNumber)
#else
        public SketchPanel()
#endif
        {
#if MAC
            documentSerial = documentSerialNumber;
#endif
            BackgroundColor = Color.FromArgb(0xf6, 0xf7, 0xf9);
            try
            {
                header = new SidebarView(controller, true, () => tab, DarkAppearance, OnAction) { Height = 92 };
                settings = new SidebarView(controller, false, () => tab, DarkAppearance, OnAction);
                Content = new TableLayout
                {
                    Padding = 0,
                    Spacing = Size.Empty,
                    Rows = { new TableRow(header), new TableRow(content) { ScaleHeight = true } },
                };
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Crayon sidebar: " + ex.Message);
                var tabs = new SegmentedButton { SelectionMode = SegmentedSelectionMode.Single };
                foreach (string name in new[] { "Agent", "Sketch", "Layers" }) tabs.Items.Add(new ButtonSegmentedItem { Text = name });
                tabs.SelectedIndex = 0;
                tabs.SelectedIndexChanged += (s, e) => { tab = tabs.SelectedIndex == 0 ? "agent" : tabs.SelectedIndex == 1 ? "sketch" : "layers"; ShowTab(); };
                Content = new TableLayout { Rows = { new TableRow(tabs), new TableRow(content) { ScaleHeight = true } } };
            }
#if MAC
            Listen(true);
#else
            controller.Changed += (s, e) => { header?.RefreshState(); settings?.RefreshState(); };
            controller.SceneChanged += (s, e) => settings?.RefreshState();
            PenHook.PanelWindow = () => Loaded ? NativeHandle : IntPtr.Zero;
            Application.Instance.AsyncInvoke(SketchPlugin.ShowTabIcon);
#endif
            ShowTab();
        }

        void OnAction(string type, string value)
        {
            if (type == "tab" && (value == "sketch" || value == "layers" || value == "agent"))
            {
                tab = value;
                ShowTab();
            }
            else if (type == "chatHistory") agentView.SetHistory("toggle");
            else if (type == "chatNew") agentView.NewChat();
        }

        void ShowTab()
        {
            content.Content = tab == "agent" ? (Control)agentView : settings ?? Fallback();
            if (tab == "agent"
#if MAC
                && IsCurrent(this)
#endif
            ) Agent.Open();
            header?.RefreshState();
            settings?.RefreshState();
        }

        Control Fallback()
        {
            var toggle = new Button { Text = controller.Active ? "Turn Crayon off" : "Turn Crayon on" };
            toggle.Click += (s, e) => { controller.Active = !controller.Active; toggle.Text = controller.Active ? "Turn Crayon off" : "Turn Crayon on"; };
            var controls = new StackLayout { Padding = 12, Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            controls.Items.Add(toggle);
            if (tab == "layers")
            {
                RhinoDoc doc = RhinoDoc.ActiveDoc;
                controls.Items.Add(new Label { Text = doc == null ? "Open a model to see sketches and comments." : SketchController.StrokeObjects(doc).Length + " sketches · " + controller.Comments(doc).Count + " comments", Wrap = WrapMode.Word });
            }
            else
            {
                foreach (DrawMethod method in Enum.GetValues(typeof(DrawMethod)))
                {
                    var button = new Button { Text = method == DrawMethod.Surface ? "Object" : method == DrawMethod.View ? "Over view" : method.ToString() };
                    button.Click += (s, e) => { controller.Method = method; controller.Tool = SketchTool.Draw; };
                    controls.Items.Add(button);
                }
#if MAC
                controls.Items.Add(new Label { Text = "The detailed sidebar could not be shown. Drawing controls remain available in the viewport.", Wrap = WrapMode.Word });
#else
                controls.Items.Add(new Label { Text = "The detailed sidebar needs Microsoft Edge WebView2. Drawing controls remain available in the viewport.", Wrap = WrapMode.Word });
#endif
            }
            return controls;
        }

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
        {
#if MAC
            Listen(true);
            if (!IsCurrent(this)) return;
#endif
            Sidebar.Shown(reason);
            if (tab == "agent") Agent.Open();
        }
        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
        {
#if MAC
            bool wasCurrent = IsCurrent(this);
            Listen(false);
            if (!wasCurrent) return;
#endif
            if (onCloseDocument) return;
            controller.Active = false;
            Agent.Session.Close();
        }

#if MAC
        // Actions arrive asynchronously from WebKit. The user may have activated another
        // model or closed this inspector since clicking a button.
        internal static bool IsCurrent(Control control)
        {
            for (; control != null; control = control.Parent)
                if (control is SketchPanel panel)
                    return panel.listening && RhinoDoc.ActiveDoc?.RuntimeSerialNumber == panel.documentSerial;
            return false;
        }

        internal static void TrackTextFocus(Control control, Func<bool> focused)
        {
            control.Load += (s, e) => KeyHook.TextEntry.Add(focused);
            control.UnLoad += (s, e) => KeyHook.TextEntry.Remove(focused);
        }

        void RefreshControls(object sender, EventArgs e)
        {
            header?.RefreshState();
            settings?.RefreshState();
        }

        void Listen(bool on)
        {
            if (listening == on) return;
            listening = on;
            if (on)
            {
                controller.Changed += RefreshControls;
                controller.SceneChanged += RefreshControls;
            }
            else
            {
                controller.Changed -= RefreshControls;
                controller.SceneChanged -= RefreshControls;
            }
            agentView.Listen(on);
            header?.Listen(on);
            settings?.Listen(on);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Listen(false);
            base.Dispose(disposing);
        }
#endif
    }
    internal static class Sidebar
    {

        static readonly Guid Sketch = typeof(SketchPanel).GUID;

        static Guid before;    // the panel the Sketch panel took over from
        static Guid hidden;    // the panel Rhino hid last,
        static int hiddenAt;   // and when (Environment.TickCount)
        static bool settled;   // Rhino has finished starting up
        static bool arranging; // the tabs are being put in order: not the user going from panel to panel

        /// <summary>Call once, when the plug-in loads.</summary>
        public static void Watch()
        {
            Panels.Show += OnShow;
            RhinoApp.Idle += OnFirstIdle;
        }

        // Rhino is up, with its panels where they were last time.
        static void OnFirstIdle(object sender, EventArgs e)
        {
#if MAC
            // Rhino for Mac can sit idle at its start window with no model open. A panel belongs
            // to a model's window there, so the introduction waits for the first one.
            if (RhinoDoc.ActiveDoc == null) return;
#endif
            RhinoApp.Idle -= OnFirstIdle;
            SketchPlugin.ShowTabIcon();
            settled = true;
            // A fresh installation should be visible without requiring a command or panel setup.
            if (!SketchPlugin.Instance.Settings.GetBool("SidebarIntroduced", false))
            {
                Open();
                SketchPlugin.Instance.Settings.SetBool("SidebarIntroduced", true);
            }
        }

        // Rhino reports every panel it shows or hides. Switching tabs hides one panel and
        // shows another, so the panel hidden just before ours appears is the one it replaced.
        static void OnShow(object sender, ShowPanelEventArgs e)
        {
            if (e.Show || e.PanelId == Sketch) return;
            hidden = e.PanelId;
            hiddenAt = Environment.TickCount;
        }

        /// <summary>The Sketch panel has come into view.</summary>
        public static void Shown(ShowPanelReason reason)
        {
            if (reason != ShowPanelReason.Show) return; // only Rhino's window coming back to the front
            Application.Instance.AsyncInvoke(SketchPlugin.ShowTabIcon);
            if (arranging) return;

            if (hidden != Guid.Empty && unchecked(Environment.TickCount - hiddenAt) < 1000 && InSameContainer(hidden))
                before = hidden;

            // Coming to the panel switches sketching on. Not while Rhino is starting up,
            // though: then it is only putting back the panel that was showing last time.
            if (settled) SketchPlugin.Instance.Controller.Active = true;
        }

        /// <summary>Show the Sketch panel, adding it to the sidebar if it is not there.</summary>
        public static void Open()
        {
            if (Panels.PanelDockBar(Sketch) != Guid.Empty) Panels.OpenPanel(Sketch, true);
            else Place();
        }

        /// <summary>
        /// Sketching has been turned off with Esc Esc: show the panel that was there
        /// before the Sketch panel again. If the user has gone to another panel
        /// themselves in the meantime, that one stays.
        /// </summary>
        public static void Leave()
        {
            if (!Panels.IsPanelVisible(Sketch, true)) return;
            if (before == Guid.Empty || !InSameContainer(before)) return; // nothing to go back to
            Panels.OpenPanel(before, true);
        }

        // Adds the Sketch tab to the sidebar and shows it: where the Layers panel is, as
        // the first of the tabs there.
        static void Place()
        {
            Guid home = Panels.PanelDockBar(PanelIds.Layers);
            if (home == Guid.Empty)
            {
                Panels.OpenPanel(Sketch, true); // no sidebar to join: wherever Rhino puts it
                return;
            }

            var others = new List<Guid>(); // the tabs already there, in order
            foreach (Guid panel in Panels.GetOpenPanelIds())
                if (panel != Sketch && Panels.PanelDockBar(panel) == home) others.Add(panel);

            Panels.OpenPanel(home, Sketch, true); // comes to Shown above, as a click on the tab would
            if (others.Count == 0) return;

            // Rhino has put the new tab below the others, and has no call for moving one.
            // What does get it to the top is taking the others out and putting them back,
            // which lands them below it. (Only ever done here, on a command of the
            // user's: doing it while Rhino starts up brings Rhino down.)
            arranging = true;
            try
            {
                foreach (Guid panel in others) Panels.ClosePanel(panel);
                foreach (Guid panel in others)
                    if (Panels.OpenPanel(home, panel, false) == Guid.Empty) Panels.OpenPanel(panel, false);

                // That leaves the tab strip marking the wrong tab as the one showing.
                // Going to another tab and back puts it right.
                Panels.OpenPanel(others[0], true);
                Panels.OpenPanel(Sketch, true);
            }
            finally
            {
                arranging = false;
            }
        }

        static bool InSameContainer(Guid panel)
        {
            Guid container = Panels.PanelDockBar(Sketch);
            return container != Guid.Empty && Panels.PanelDockBar(panel) == container;
        }
    }
}
