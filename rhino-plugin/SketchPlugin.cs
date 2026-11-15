using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;

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

        /// <summary>All tool state and behaviour. The tool window is only a view onto this.</summary>
        public SketchController Controller { get; }

        protected override void OnShutdown()
        {
            ToolWindow.CloseWindow();
            Controller.Active = false;
        }
    }

    /// <summary>
    /// The one command: opens the little tool window. Sketching is live while that window
    /// is open. (Rhino already has a command called "Sketch", hence the name.)
    /// </summary>
    public class SketchToolsCommand : Command
    {
        public override string EnglishName => "SketchTools";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            ToolWindow.ShowWindow();
            return Result.Success;
        }
    }
}
