using System;
using Rhino;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Crayon's light / dark look. It is Crayon's own choice, made with the switch in the
    /// panel header, and it changes Crayon only (panel, agent, toolbar and option row),
    /// never Rhino's viewport or theme. Remembered between sessions.
    /// </summary>
    internal static class Theme
    {
        static bool? dark;

        /// <summary>Raised on the UI thread after the look changes.</summary>
        public static event EventHandler Changed;

        public static bool Dark
        {
            get
            {
                if (dark == null)
                {
                    var settings = SketchPlugin.Instance?.Settings;
                    if (settings == null) return false; // asked again once the settings are there
                    dark = settings.GetBool("ThemeDark", false);
                }
                return dark.Value;
            }
            set
            {
                if (Dark == value) return;
                dark = value;
                SketchPlugin.Instance?.Settings?.SetBool("ThemeDark", value);
                Changed?.Invoke(null, EventArgs.Empty);
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
        }
    }
}
