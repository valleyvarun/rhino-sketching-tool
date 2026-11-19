using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    internal sealed class DrawingContextSnapshot
    {
        internal string Text = "";
        internal int[] SketchGroups = new int[0], CommentGroups = new int[0];
    }

    internal static class DrawingContext
    {
        internal static string Build(RhinoDoc doc) => Capture(doc).Text;

        /// <summary>
        /// Which groups Capture would send, without building the sketches' point lists: for
        /// the Agent tab's counter, which asks about once a second.
        /// </summary>
        internal static int[] GroupCounts(RhinoDoc doc)
        {
            if (doc == null) return new[] { 0, 0 };
            var state = AnnotationGroups.Read(doc);
            var sketchGroups = new HashSet<int>();
            var commentGroups = new HashSet<int>();
            foreach (RhinoObject obj in SketchController.StrokeObjects(doc))
                if (AnnotationGroups.InContext(state, obj.Attributes) && !SketchController.ViewGone(doc, obj.Attributes)) sketchGroups.Add(AnnotationGroups.GroupOf(state, obj.Attributes));
            var settings = new ObjectEnumeratorSettings { ObjectTypeFilter = ObjectType.Point, HiddenObjects = true, NormalObjects = true, LockedObjects = true, VisibleFilter = false };
            foreach (var obj in doc.Objects.GetObjectList(settings))
                if (obj.Attributes.GetUserString(SketchController.CommentKey) != null && AnnotationGroups.InContext(state, obj.Attributes))
                    commentGroups.Add(AnnotationGroups.GroupOf(state, obj.Attributes));
            return new[] { sketchGroups.Count, commentGroups.Count };
        }

        internal static DrawingContextSnapshot Capture(RhinoDoc doc)
        {
            if (doc == null) return new DrawingContextSnapshot();
            var state = AnnotationGroups.Read(doc);
            var sketchGroups = new HashSet<int>();
            var commentGroups = new HashSet<int>();
            var sketches = new List<object>();
            var comments = new List<object>();
            int number = 0;
            bool overViews = false;
            // The agent gets every sketch and comment group ticked in Layers, without the
            // items whose own eye is off. A sketch drawn over a saved view is not 3D: it is
            // named with its view, and left out once that view is gone (it shows nowhere).
            foreach (RhinoObject obj in SketchController.StrokeObjects(doc))
            {
                number++;
                int group = AnnotationGroups.GroupOf(state, obj.Attributes);
                if (!AnnotationGroups.InContext(state, obj.Attributes) || SketchController.ViewGone(doc, obj.Attributes)) continue;
                sketchGroups.Add(group);
                var points = obj.Geometry is Curve curve ? SketchController.StrokePoints(curve, doc.ModelAbsoluteTolerance) : null;
                var sampled = new List<object>();
                if (points != null)
                    for (int i = 0, count = System.Math.Min(points.Length, 128); i < count; i++)
                        sampled.Add(Point(points[(int)((long)i * (points.Length - 1) / System.Math.Max(1, count - 1))]));
                var sketch = new JsonMap { ["id"] = obj.Id.ToString(), ["name"] = AnnotationGroups.SketchName(obj, number), ["group"] = group,
                    ["points"] = sampled, ["sampled"] = points != null && points.Length > 128,
                    ["color"] = "#" + (obj.Attributes.ObjectColor.ToArgb() & 0xffffff).ToString("X6") };
                string overView = SketchController.ViewNameOf(doc, obj.Attributes);
                if (overView != null) { sketch["overView"] = overView; overViews = true; }
                sketches.Add(sketch);
            }
            var settings = new ObjectEnumeratorSettings { ObjectTypeFilter = ObjectType.Point, HiddenObjects = true, NormalObjects = true, LockedObjects = true, VisibleFilter = false };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                if (obj.Attributes.GetUserString(SketchController.CommentKey) == null) continue;
                int group = AnnotationGroups.GroupOf(state, obj.Attributes);
                if (!AnnotationGroups.InContext(state, obj.Attributes)) continue;
                commentGroups.Add(group);
                comments.Add(new JsonMap { ["id"] = obj.Id.ToString(), ["group"] = group,
                    ["text"] = obj.Attributes.GetUserString("SketchingTool.CommentText") ?? "",
                    ["point"] = obj.Geometry is Rhino.Geometry.Point point ? Point(point.Location) : null,
                    ["attachedTo"] = obj.Attributes.GetUserString("SketchingTool.CommentOn") ?? "" });
            }
            var context = new JsonMap { ["scope"] = "the sketch and comment groups checked for the agent in Crayon's Layers tab" };
            if (overViews)
                context["note"] = "A sketch with overView was drawn over that saved Rhino Named View, like a note on a print of it: it is flat, on a plane facing that view's camera, and only means something seen from that view. Its points are not positions in the model.";
            context["sketchGroups"] = state.Sketches.Where(g => sketchGroups.Contains(g.Id)).Select(g => (object)new JsonMap { ["id"] = g.Id, ["name"] = g.Name }).ToList();
            context["commentGroups"] = state.Comments.Where(g => commentGroups.Contains(g.Id)).Select(g => (object)new JsonMap { ["id"] = g.Id, ["name"] = g.Name }).ToList();
            context["units"] = doc.ModelUnitSystem.ToString();
            context["sketches"] = sketches;
            context["comments"] = comments;
            return new DrawingContextSnapshot { SketchGroups = sketchGroups.ToArray(), CommentGroups = commentGroups.ToArray(),
                Text = "\n\n<crayon_drawing_context>\n" + Json.Write(context) + "\n</crayon_drawing_context>" };
        }

        static List<object> Point(Point3d point) => new List<object> { point.X, point.Y, point.Z };
    }
}
