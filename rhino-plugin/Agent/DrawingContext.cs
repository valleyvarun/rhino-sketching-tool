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

        internal static DrawingContextSnapshot Capture(RhinoDoc doc)
        {
            if (doc == null) return new DrawingContextSnapshot();
            var state = AnnotationGroups.Read(doc);
            var sketchGroups = new HashSet<int>();
            var commentGroups = new HashSet<int>();
            var sketches = new List<object>();
            var comments = new List<object>();
            foreach (RhinoObject obj in SketchController.StrokeObjects(doc))
            {
                int group = AnnotationGroups.GroupOf(obj.Attributes);
                if (state.ActiveOnly && group != state.ActiveSketch) continue;
                sketchGroups.Add(group);
                var points = obj.Geometry is Curve curve ? SketchController.StrokePoints(curve, doc.ModelAbsoluteTolerance) : null;
                var sampled = new List<object>();
                if (points != null)
                    for (int i = 0, count = System.Math.Min(points.Length, 128); i < count; i++)
                        sampled.Add(Point(points[(int)((long)i * (points.Length - 1) / System.Math.Max(1, count - 1))]));
                sketches.Add(new JsonMap { ["id"] = obj.Id.ToString(), ["name"] = obj.Name, ["group"] = group,
                    ["points"] = sampled, ["sampled"] = points != null && points.Length > 128,
                    ["view"] = obj.Attributes.GetUserString(SketchController.ViewStrokeKey) ?? "",
                    ["color"] = "#" + (obj.Attributes.ObjectColor.ToArgb() & 0xffffff).ToString("X6") });
            }
            var settings = new ObjectEnumeratorSettings { ObjectTypeFilter = ObjectType.Point, HiddenObjects = true, NormalObjects = true, LockedObjects = true, VisibleFilter = false };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                if (obj.Attributes.GetUserString(SketchController.CommentKey) == null) continue;
                int group = AnnotationGroups.GroupOf(obj.Attributes);
                if (state.ActiveOnly && group != state.ActiveComment) continue;
                commentGroups.Add(group);
                comments.Add(new JsonMap { ["id"] = obj.Id.ToString(), ["group"] = group,
                    ["text"] = obj.Attributes.GetUserString("SketchingTool.CommentText") ?? "",
                    ["point"] = obj.Geometry is Rhino.Geometry.Point point ? Point(point.Location) : null,
                    ["attachedTo"] = obj.Attributes.GetUserString("SketchingTool.CommentOn") ?? "" });
            }
            return new DrawingContextSnapshot { SketchGroups = sketchGroups.ToArray(), CommentGroups = commentGroups.ToArray(),
                Text = "\n\n<crayon_drawing_context>\n" + Json.Write(new JsonMap
            {
                ["scope"] = state.ActiveOnly ? "active groups only" : "all groups",
                ["activeSketchGroup"] = state.ActiveSketch, ["activeCommentGroup"] = state.ActiveComment,
                ["units"] = doc.ModelUnitSystem.ToString(), ["sketches"] = sketches, ["comments"] = comments
            }) + "\n</crayon_drawing_context>" };
        }

        static List<object> Point(Point3d point) => new List<object> { point.X, point.Y, point.Z };
    }
}
