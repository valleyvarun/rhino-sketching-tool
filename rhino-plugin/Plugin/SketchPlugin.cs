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
#if MAC
            // By the name of the logo carried inside the plug-in: Rhino 8 draws the SVG itself,
            // on a Mac as on Windows. (The Windows way below goes through a Windows icon handle.)
            try
            {
                Panels.RegisterPanel(this, typeof(SketchPanel), "Crayon", typeof(SketchPlugin).Assembly, "RhinoSketchingTool.crayon-mark.svg", PanelType.PerDoc);
            }
            catch (Exception)
            {
                Panels.RegisterPanel(this, typeof(SketchPanel), "Crayon", (System.Drawing.Icon)null, PanelType.PerDoc);
            }
            SayIfRhinoIsTooOld();
#else
            // The icon given here does not show up on the tab in Rhino 8; ShowTabIcon sees to that.
            Panels.RegisterPanel(this, typeof(SketchPanel), "Crayon", PanelIcon(), PanelType.System);
#endif
            Controller.StartPainting();
            Sidebar.Watch();
            RhinoApp.Idle += RhinoMcpConnection.OnStartup;
            RhinoDoc.CloseDocument += Agent.DocumentClosed;
#if MAC
            RhinoDoc.ActiveDocumentChanged += Agent.ActiveDocumentChanged;
#endif
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            RhinoApp.Idle -= RhinoMcpConnection.OnStartup;
            RhinoDoc.CloseDocument -= Agent.DocumentClosed;
#if MAC
            RhinoDoc.ActiveDocumentChanged -= Agent.ActiveDocumentChanged;
#endif
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
#if !MAC // on a Mac the panel is registered with the logo by name, and Rhino shows it without help
            Panels.ChangePanelIcon(typeof(SketchPanel), PanelIcon());
#endif
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

#if MAC
        /// <summary>
        /// The toolbar, its options and the comment labels are pictures laid over the viewport,
        /// and Rhino for Mac only draws such pictures from 8.17 on (McNeel's RH-81192). In an
        /// older Rhino they are simply not there, so this says why in the command history.
        /// </summary>
        internal static void SayIfRhinoIsTooOld()
        {
            Version rhino = RhinoApp.Version;
            if (rhino >= new Version(8, 17)) return;
            RhinoApp.WriteLine("Crayon: this is Rhino {0}.{1}. On a Mac, Crayon's toolbar and comment labels only show in Rhino 8.17 or later. Please update Rhino (Rhino > Check for Updates).", rhino.Major, rhino.Minor);
        }
#endif

#if !MAC
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr icon);

        static System.Drawing.Icon PanelIcon()
        {
            int size = Math.Max(16, Panels.IconSizeInPixels.Width);
            using (var bitmap = DrawingUtilities.BitmapFromSvg(BrandSvg, size, size))
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
#endif
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
#if MAC
            SketchPlugin.SayIfRhinoIsTooOld(); // here as well: this is where somebody looks for the toolbar
#endif
            return Result.Success;
        }
    }
}
