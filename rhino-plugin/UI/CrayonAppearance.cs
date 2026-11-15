using System;

namespace RhinoSketchingTool
{
    /// <summary>Crayon's remembered theme, independent of Rhino's viewport and application theme.</summary>
    internal static class CrayonAppearance
    {
        internal static event EventHandler Changed;
        internal static bool Dark
        {
            get
            {
                string choice = SketchPlugin.Instance?.Settings.GetString("CrayonTheme", "system") ?? "system";
                return choice == "dark" || (choice != "light" && Rhino.Runtime.HostUtils.RunningInDarkMode);
            }
        }
        internal static void Toggle()
        {
            SketchPlugin.Instance.Settings.SetString("CrayonTheme", Dark ? "light" : "dark");
            SketchPlugin.ShowTabIcon();
            Changed?.Invoke(null, EventArgs.Empty);
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
        }
    }
}
