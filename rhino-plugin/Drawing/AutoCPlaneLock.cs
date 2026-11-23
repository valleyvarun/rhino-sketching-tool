using System;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Runtime;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Keeps Rhino's Auto CPlane out of the way while Crayon is on, and gives it back,
    /// working, when Crayon is off.
    ///
    /// Auto CPlane moves a viewport's construction plane onto whatever is selected, and puts
    /// "the plane from before" back when the selection goes. Crayon's Plane method moves that
    /// same plane onto a picked face, and puts its own "plane from before" back when it is
    /// done. Running together, each takes the other's plane for the one to go back to: a
    /// picked face is lost at the next Esc, or the plane is left on a face after Crayon has
    /// been turned off and Auto CPlane keeps returning to it. Strokes that are hidden while
    /// they are selected also leave Auto CPlane believing something is still selected, and
    /// it only acts on a first selection.
    ///
    /// So while Crayon is on, Auto CPlane is off, and the drawing plane changes only when it
    /// is picked in Crayon. It is turned off the way Rhino's own status bar does it, which
    /// has Rhino put back the plane it had moved and forget the selection it was following,
    /// and turned on again the same way once Crayon is off: Rhino then starts afresh from
    /// what is selected. An Auto CPlane that was off to begin with is never touched.
    ///
    /// Both happen only while no command is running. Auto CPlane notes commands starting
    /// and ending and ignores selections made during one; turned off in the middle of a
    /// command it would never hear that command end, and go on ignoring them afterwards.
    /// </summary>
    internal static class AutoCPlaneLock
    {
        // Kept in the plug-in's settings as well: Rhino may be closed, or fall over, while Crayon is on.
        const string OwedKey = "AutoCPlaneSuspended";
        const string Switch = "Rhino.AutoCPlane.EnableAutoAlignCPlane"; // what the status bar's Auto CPlane pane calls

        static bool? owed;
        static bool failed;

        // Crayon has turned Auto CPlane off, and owes turning it on again.
        static bool Owed
        {
            get
            {
                if (owed == null) owed = SketchPlugin.Instance?.Settings.GetBool(OwedKey, false) ?? false;
                return owed.Value;
            }
            set
            {
                if (owed == value) return;
                owed = value;
                try
                {
                    SketchPlugin.Instance.Settings.SetBool(OwedKey, value);
                    SketchPlugin.Instance.SaveSettings();
                }
                catch (Exception)
                {
                    // Remembered for this session at least.
                }
            }
        }

        /// <summary>
        /// Call whenever Rhino is idle with no command running. Off while Crayon is on (also
        /// if it is turned on again from the status bar meanwhile); back on once Crayon is off.
        /// </summary>
        public static void Sync(bool crayonOn)
        {
            if (failed) return;
            try
            {
                if (crayonOn)
                {
                    if (!ModelAidSettings.AutoAlignCPlane) return;
                    Owed = true; // noted first: whatever happens next, it is given back
                    Set(false);
                }
                else if (Owed)
                {
                    Set(true);
                    Owed = false;
                }
            }
            catch (Exception ex)
            {
                failed = true; // not again every time Rhino is idle
                RhinoApp.WriteLine("Crayon: Rhino's Auto CPlane could not be switched (" + ex.Message + ").");
            }
        }

        /// <summary>Rhino is closing: its setting goes back as it was, for the next session.</summary>
        public static void Release()
        {
            if (!Owed) return;
            try
            {
                if (!ModelAidSettings.AutoAlignCPlane) ModelAidSettings.AutoAlignCPlane = true;
                Owed = false;
            }
            catch (Exception)
            {
                // Still owed: it is put right the first time Rhino is idle in the next session.
            }
        }

        static void Set(bool on)
        {
            RhinoDoc doc = RhinoDoc.ActiveDoc;
            if (doc != null)
            {
                using (var args = new NamedParametersEventArgs())
                {
                    args.Set("enable", on);
                    args.Set("docSerial", doc.RuntimeSerialNumber);
                    HostUtils.ExecuteNamedCallback(Switch, args);
                }
            }
            // No document, or an early Rhino 8 without that call: the setting alone.
            if (ModelAidSettings.AutoAlignCPlane != on) ModelAidSettings.AutoAlignCPlane = on;
            if (ModelAidSettings.AutoAlignCPlane != on) throw new InvalidOperationException("the setting did not change");
        }
    }
}
