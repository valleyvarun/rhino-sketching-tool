using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;
using Rhino.UI;

// Rhino identifies the plug-in by this id. Never change it once the plug-in is in use.
[assembly: Guid("299a696b-aeb1-4c25-bd67-f6c04f5bed05")]

namespace RhinoSketchingTool
{
    public class SketchPlugin : PlugIn
    {
        public SketchPlugin()
        {
            Instance = this;
            Controller = new SketchController();
        }

        public static SketchPlugin Instance { get; private set; }

        /// <summary>All tool state and behaviour. The Sketch panel is only a view onto this.</summary>
        public SketchController Controller { get; }

        // Loaded with Rhino, not on first use: the Sketch tab has to be in the sidebar from
        // the start, and strokes are hidden objects that nothing but this plug-in shows.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // The icon given here does not show up on the tab in Rhino 8; ShowTabIcon sees to that.
            Panels.RegisterPanel(this, typeof(SketchPanel), "Crayon", PanelIcon(), PanelType.System);
            Controller.StartPainting();
            Controller.EscapedTwice += (s, e) => Sidebar.Leave();
            Sidebar.Watch();
            RhinoApp.Idle += RhinoMcpConnection.OnStartup;
            RhinoDoc.CloseDocument += Agent.DocumentClosed;
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            Settings.SetBool("CrayonEnabled", Controller.Active);
            RhinoApp.Idle -= RhinoMcpConnection.OnStartup;
            RhinoDoc.CloseDocument -= Agent.DocumentClosed;
            Agent.Shutdown();
            Controller.Shutdown();
        }

        /// <summary>
        /// Puts the icon on the Sketch panel's tab. Rhino 8 leaves the tab blank when it is
        /// only given the icon as the panel is registered, but shows one set afterwards
        /// (a new one each time: it ignores an icon it has been handed before).
        /// </summary>
        internal static void ShowTabIcon()
        {
            Panels.ChangePanelIcon(typeof(SketchPanel), PanelIcon());
        }

        // Both the panel header and Rhino tab use the official design asset.
        static string brandSvg;
        internal static string BrandSvg
        {
            get
            {
                if (brandSvg != null) return brandSvg;
                using (var stream = typeof(SketchPlugin).Assembly.GetManifestResourceStream("RhinoSketchingTool.crayon-mark.svg"))
                using (var reader = new System.IO.StreamReader(stream))
                    return brandSvg = reader.ReadToEnd();
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr icon);

        static System.Drawing.Icon PanelIcon()
        {
            int size = Math.Max(16, Panels.IconSizeInPixels.Width);
            string logo = CrayonAppearance.Dark ? BrandSvg.Replace("#9731b0", "#b165c4").Replace("#6d237f", "#9731b0").Replace("#1d2025", "#f2f1ed") : BrandSvg;
            using (var bitmap = DrawingUtilities.BitmapFromSvg(logo, size, size))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (var temporary = System.Drawing.Icon.FromHandle(handle))
                        return (System.Drawing.Icon)temporary.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }
    }
    /// <summary>
    /// The one command: shows the Sketch panel and switches sketching on. Clicking the
    /// panel's tab in the sidebar does the same. (Rhino already has a command called
    /// "Sketch", hence the name.)
    /// </summary>
    public class CrayonCommand : Command
    {
        public override string EnglishName => "Crayon";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Sidebar.Open();
            SketchPlugin.Instance.Controller.Active = true;
            return Result.Success;
        }
    }
}
