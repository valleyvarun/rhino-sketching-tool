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
        internal List<SketchController.CommentImageRef> Images = new List<SketchController.CommentImageRef>(); // the pictures on those comments
    }

    internal static class DrawingContext
    {
        const int MaxImages = 6; // comment pictures one prompt takes along

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
                if (AnnotationGroups.InContext(state, obj.Attributes)) sketchGroups.Add(AnnotationGroups.GroupOf(state, obj.Attributes));
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
            var images = new List<SketchController.CommentImageRef>();
            int number = 0;
            bool overViews = false;
            // The agent gets every sketch and comment group ticked in Layers, without the
            // items whose own eye is off.
            foreach (RhinoObject obj in SketchController.StrokeObjects(doc))
            {
                number++;
                int group = AnnotationGroups.GroupOf(state, obj.Attributes);
                if (!AnnotationGroups.InContext(state, obj.Attributes)) continue;
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
                var comment = new JsonMap { ["id"] = obj.Id.ToString(), ["group"] = group,
                    ["text"] = obj.Attributes.GetUserString("SketchingTool.CommentText") ?? "",
                    ["point"] = obj.Geometry is Rhino.Geometry.Point point ? Point(point.Location) : null,
                    ["attachedTo"] = obj.Attributes.GetUserString("SketchingTool.CommentOn") ?? "" };
                // A picture on the comment goes with the prompt as an image file: here, its name. Six at most.
                SketchController.CommentImageRef image = images.Count < MaxImages ? SketchController.ImageRefOf(obj) : null;
                if (image != null)
                {
                    if (images.Any(other => other.FileName == image.FileName)) image.FileName = image.ImageId + "-" + image.FileName; // two comments with one number
                    comment["image"] = image.FileName;
                    images.Add(image);
                }
                comments.Add(comment);
            }
            var context = new JsonMap { ["scope"] = "the sketch and comment groups checked for the agent in Crayon's Layers tab" };
            if (images.Count > 0)
                context["imageNote"] = "A comment with image has a picture the user put on it. That picture is attached to this prompt as the image file of that name.";
            if (overViews)
                context["note"] = "overView identifies the saved Rhino Named View used for drawing. The sketch points are world coordinates on a fixed plane facing the original camera.";
            context["sketchGroups"] = state.Sketches.Where(g => sketchGroups.Contains(g.Id)).Select(g => (object)new JsonMap { ["id"] = g.Id, ["name"] = g.Name }).ToList();
            context["commentGroups"] = state.Comments.Where(g => commentGroups.Contains(g.Id)).Select(g => (object)new JsonMap { ["id"] = g.Id, ["name"] = g.Name }).ToList();
            context["units"] = doc.ModelUnitSystem.ToString();
            context["sketches"] = sketches;
            context["comments"] = comments;
            return new DrawingContextSnapshot { SketchGroups = sketchGroups.ToArray(), CommentGroups = commentGroups.ToArray(), Images = images,
                Text = "\n\n<crayon_drawing_context>\n" + Json.Write(context) + "\n</crayon_drawing_context>" };
        }

        static List<object> Point(Point3d point) => new List<object> { point.X, point.Y, point.Z };
    }
}
