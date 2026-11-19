using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Color = System.Drawing.Color;

namespace RhinoSketchingTool
{
    public sealed partial class SketchController
    {
        internal const string ViewStrokeKey = "Crayon.NamedView";
        const string ViewBindingsSection = "Crayon.ViewBindings";
        Color strokeColor = Color.FromArgb(151, 49, 176); // Crayon violet, #9731B0
        bool snapShapes;

        /// <summary>The colour stored on each new sketch, independent of older sketches.</summary>
        public Color StrokeColor
        {
            get => strokeColor;
            set
            {
                if (strokeColor.ToArgb() == value.ToArgb()) return;
                EndDrag();
                strokeColor = Color.FromArgb(255, value.R, value.G, value.B);
                Refresh();
            }
        }

        /// <summary>Recognize straight lines, rectangles and ellipses on planar drawing methods.</summary>
        public bool SnapShapes
        {
            get => snapShapes;
            set { if (snapShapes != value) { EndDrag(); snapShapes = value; Refresh(); } }
        }

        public void SetCursor(Point3d value)
        {
            if (!value.IsValid || cursor == value) return;
            EndDrag();
            cursor = value;
            Refresh();
        }

        public IReadOnlyList<string> NamedViewNames
        {
            get
            {
                var names = new List<string>();
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                    for (int i = 0; i < doc.NamedViews.Count; i++) names.Add(doc.NamedViews[i].Name);
                return names;
            }
        }

        public string CurrentViewName
        {
            get
            {
                var doc = RhinoDoc.ActiveDoc;
                var viewport = doc?.Views.ActiveView?.ActiveViewport;
                if (viewport == null) return null;
                string key = ViewKeyFor(doc, viewport);
                int index = NamedViewIndex(doc, key);
                return index < 0 ? null : doc.NamedViews[index].Name;
            }
        }

        /// <summary>Save the active camera in Rhino's native Named Views table, without overwriting a view.</summary>
        public string CaptureView(string name = null) => CaptureView(name, RhinoDoc.ActiveDoc?.Views.ActiveView);

        string CaptureView(string name, RhinoView view)
        {
            var doc = view?.Document;
            if (doc == null || view.ActiveViewport.ViewportType == ViewportType.PageViewMainViewport) return null;
            EndDrag();
            string basis = string.IsNullOrWhiteSpace(name) ? "View" : name.Trim();
            string chosen = basis;
            int number = 1;
            if (string.IsNullOrWhiteSpace(name)) chosen = basis + " " + number++;
            while (doc.NamedViews.FindByName(chosen) >= 0) chosen = basis + " " + number++;
            uint undo = doc.BeginUndoRecord("Capture Crayon view");
            int index;
            try { index = doc.NamedViews.Add(chosen, view.ActiveViewport.Id); }
            finally { if (undo != 0) doc.EndUndoRecord(undo); }
            if (index < 0) return null;
            BindView(doc, view.ActiveViewport, NamedViewKey(doc.NamedViews[index]), true);
            Refresh();
            return chosen;
        }

        public bool RestoreView(string name) => RestoreView(name, RhinoDoc.ActiveDoc?.Views.ActiveView);

        bool RestoreView(string name, RhinoView view)
        {
            var doc = view?.Document;
            if (doc == null || string.IsNullOrEmpty(name)) return false;
            int index = doc.NamedViews.FindByName(name);
            if (index < 0) return false;
            EndDrag();
            if (!doc.NamedViews.Restore(index, view.ActiveViewport)) return false;
            BindView(doc, view.ActiveViewport, NamedViewKey(doc.NamedViews[index]), true);
            // Only the camera changes: the tool and every sketch stay as they are.
            Refresh();
            return true;
        }

        // ------------------------------------------------- drawing over a view

        // The pen's "Over view" method draws over one saved view, as on a print of it: the
        // sketch is not part of the 3D model, and shows only while a viewport is in that
        // view. The view is picked first (as a face is for Plane); the camera goes there.
        string overView;   // the picked view's key (NamedViewKey),
        uint overViewDoc;  // in this document

        /// <summary>The saved view "Over view" draws over. Null until one is picked, or once it is deleted.</summary>
        public string OverViewName
        {
            get
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null || overView == null || overViewDoc != doc.RuntimeSerialNumber) return null;
                int index = NamedViewIndex(doc, overView);
                return index < 0 ? null : doc.NamedViews[index].Name;
            }
        }

        /// <summary>Pick the saved view to draw over: the active viewport goes to it, and the pen to "Over view".</summary>
        public bool DrawOverView(string name)
        {
            var view = RhinoDoc.ActiveDoc?.Views.ActiveView;
            if (!RestoreView(name, view)) return false;
            var doc = view.Document;
            overView = NamedViewKey(doc.NamedViews[doc.NamedViews.FindByName(name)]);
            overViewDoc = doc.RuntimeSerialNumber;
            SetTool(SketchTool.Draw);
            SetMethod(DrawMethod.View);
            Refresh();
            return true;
        }

        /// <summary>The name of the saved view a sketch was drawn over. Null for an ordinary sketch, or once that view is gone.</summary>
        internal static string ViewNameOf(RhinoDoc doc, ObjectAttributes attributes)
        {
            int index = NamedViewIndex(doc, attributes.GetUserString(ViewStrokeKey));
            return index < 0 ? null : doc.NamedViews[index].Name;
        }

        /// <summary>Drawn over a view that has since been deleted (in Rhino's Named Views panel): it shows nowhere.</summary>
        internal static bool ViewGone(RhinoDoc doc, ObjectAttributes attributes)
        {
            string key = attributes.GetUserString(ViewStrokeKey);
            return !string.IsNullOrEmpty(key) && NamedViewIndex(doc, key) < 0;
        }

        /// <summary>
        /// Delete a Rhino Named View, and with it the sketches drawn over it (they show
        /// nowhere else), as one step for Undo. Ordinary sketches are never touched.
        /// </summary>
        public bool DeleteView(string name)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || string.IsNullOrEmpty(name)) return false;
            int index = doc.NamedViews.FindByName(name);
            if (index < 0) return false;
            EndDrag();
            string key = NamedViewKey(doc.NamedViews[index]);
            uint undo = doc.BeginUndoRecord("Delete Crayon view");
            bool done;
            try
            {
                done = doc.NamedViews.Delete(index);
                if (done)
                    foreach (RhinoObject obj in StrokeObjects(doc))
                        if (obj.Attributes.GetUserString(ViewStrokeKey) == key) doc.Objects.Delete(obj, true, true);
            }
            finally { if (undo != 0) doc.EndUndoRecord(undo); }
            Refresh();
            return done;
        }

        // A binding distinguishes two saved views that happen to have identical cameras.
        // Camera matching also recognizes views restored from Rhino's own Named Views panel,
        // and reconstructs the binding after reopening a 3dm file. Native view GUIDs survive
        // renaming; a view deleted in Rhino's own panel hides what was drawn over it.
        sealed class ViewBinding
        {
            public string Key;
            public ViewportInfo Camera;
        }

        static readonly Dictionary<uint, Dictionary<Guid, ViewBinding>> viewBindings =
            new Dictionary<uint, Dictionary<Guid, ViewBinding>>();

        static string NamedViewKey(ViewInfo info) => info.NamedViewId != Guid.Empty
            ? info.NamedViewId.ToString("D") : "name:" + info.Name;

        static int NamedViewIndex(RhinoDoc doc, string key)
        {
            if (doc == null || string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < doc.NamedViews.Count; i++)
                if (NamedViewKey(doc.NamedViews[i]) == key) return i;
            return -1;
        }

        static void BindView(RhinoDoc doc, RhinoViewport viewport, string key, bool persist = false)
        {
            if (!viewBindings.TryGetValue(doc.RuntimeSerialNumber, out var views))
                viewBindings[doc.RuntimeSerialNumber] = views = new Dictionary<Guid, ViewBinding>();
            if (views.TryGetValue(viewport.Id, out var old)) old.Camera.Dispose();
            views[viewport.Id] = new ViewBinding { Key = key, Camera = new ViewportInfo(viewport) };
            if (persist) doc.Strings.SetString(ViewBindingsSection, viewport.Id.ToString("D"), key);
        }

        internal static void ForgetViewBindings(RhinoDoc doc)
        {
            if (doc == null || !viewBindings.TryGetValue(doc.RuntimeSerialNumber, out var views)) return;
            foreach (var binding in views.Values) binding.Camera.Dispose();
            viewBindings.Remove(doc.RuntimeSerialNumber);
        }

        internal static string ViewKeyFor(RhinoDoc doc, RhinoViewport viewport)
        {
            if (doc == null || viewport == null) return null;
            if (viewBindings.TryGetValue(doc.RuntimeSerialNumber, out var views) &&
                views.TryGetValue(viewport.Id, out var binding) &&
                NamedViewIndex(doc, binding.Key) >= 0 && SameCamera(viewport, binding.Camera)) return binding.Key;
            string remembered = doc.Strings.GetValue(ViewBindingsSection, viewport.Id.ToString("D"));
            int rememberedIndex = NamedViewIndex(doc, remembered);
            if (rememberedIndex >= 0 && SameCamera(viewport, doc.NamedViews[rememberedIndex].Viewport))
            {
                BindView(doc, viewport, remembered);
                return remembered;
            }
            for (int i = 0; i < doc.NamedViews.Count; i++)
            {
                ViewInfo info = doc.NamedViews[i];
                if (!SameCamera(viewport, info.Viewport)) continue;
                string key = NamedViewKey(info);
                BindView(doc, viewport, key);
                return key;
            }
            return null;
        }

        static bool SameCamera(RhinoViewport viewport, ViewportInfo saved)
        {
            if (viewport.IsParallelProjection != saved.IsParallelProjection) return false;
            double span = Math.Max(1, viewport.CameraLocation.DistanceTo(viewport.CameraTarget));
            if (viewport.CameraLocation.DistanceTo(saved.CameraLocation) > span * 1e-7) return false;
            Vector3d direction = viewport.CameraDirection, savedDirection = saved.CameraDirection;
            Vector3d up = viewport.CameraUp, savedUp = saved.CameraUp;
            direction.Unitize(); savedDirection.Unitize(); up.Unitize(); savedUp.Unitize();
            if ((direction - savedDirection).Length > 1e-7 || (up - savedUp).Length > 1e-7) return false;
            if (!viewport.GetFrustum(out double left, out double right, out double bottom, out double top, out double near, out double far)) return false;
            double scale = viewport.IsParallelProjection ? 1 : near;
            double savedScale = saved.IsParallelProjection ? 1 : saved.FrustumNear;
            if (!(scale > 0) || !(savedScale > 0)) return false;
            // Compare the smaller dimension to tolerate aspect-ratio changes when docking a
            // sidebar. Zooming or changing the lens still leaves the saved camera.
            double diameter = Math.Min(right - left, top - bottom) / scale;
            double savedDiameter = saved.FrustumMinimumDiameter / savedScale;
            double tolerance = Math.Max(1e-8, Math.Abs(savedDiameter) * 1e-6);
            return Math.Abs(diameter - savedDiameter) <= tolerance &&
                Math.Abs((left + right) / scale - (saved.FrustumLeft + saved.FrustumRight) / savedScale) <= tolerance &&
                Math.Abs((bottom + top) / scale - (saved.FrustumBottom + saved.FrustumTop) / savedScale) <= tolerance;
        }

        // An ordinary sketch is 3D and shows in every view. One drawn over a view shows only
        // while the viewport is in that view; the eraser and comment snapping reach only what shows.
        internal static bool StrokeVisibleInView(RhinoDoc doc, RhinoViewport viewport, ObjectAttributes attributes)
        {
            string key = attributes.GetUserString(ViewStrokeKey);
            return string.IsNullOrEmpty(key) || key == ViewKeyFor(doc, viewport);
        }

        internal bool LiveVisibleInView(RhinoDoc doc, RhinoViewport viewport)
        {
            if (drawing == null || drawing.View.Document != doc) return false;
            if (drawing.ViewKey == null) return true; // 3D, like every ordinary sketch: every view of its document shows it
            return drawing.View.ActiveViewport.Id == viewport.Id && drawing.ViewKey == ViewKeyFor(doc, viewport);
        }

        /// <summary>The sketch being drawn is over a view: painted over everything, in that viewport only.</summary>
        internal bool LiveIsView => drawing?.ViewKey != null;
    }
}
