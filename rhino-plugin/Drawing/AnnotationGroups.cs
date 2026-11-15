using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    internal sealed class AnnotationGroup
    {
        public int Id;
        public bool Visible = true;
        public string Name => "Group " + Id;
    }

    internal sealed class AnnotationGroupState
    {
        public List<AnnotationGroup> Sketches = new List<AnnotationGroup> { new AnnotationGroup { Id = 1 } };
        public List<AnnotationGroup> Comments = new List<AnnotationGroup> { new AnnotationGroup { Id = 1 } };
        public int ActiveSketch = 1, ActiveComment = 1;
        public bool ActiveOnly = true, NewGroup = true, HideUsed = true;
    }

    /// <summary>Document-owned annotation groups; untagged legacy annotations belong to Group 1.</summary>
    internal static class AnnotationGroups
    {
        internal const string GroupKey = "Crayon.Group", HiddenKey = "Crayon.Hidden";
        const string Section = "Crayon.Groups", Entry = "State";
        public static event Action<RhinoDoc> Changed;

        internal static int GroupOf(ObjectAttributes attributes) =>
            int.TryParse(attributes.GetUserString(GroupKey), out int id) && id > 0 ? id : 1;

        internal static AnnotationGroupState Read(RhinoDoc doc)
        {
            var state = new AnnotationGroupState();
            var json = Json.Map(Json.Parse(doc?.Strings.GetValue(Section, Entry) ?? ""));
            state.Sketches = Groups(json.At("sketches"));
            state.Comments = Groups(json.At("comments"));
            state.ActiveSketch = Active(json.At("activeSketch"), state.Sketches);
            state.ActiveComment = Active(json.At("activeComment"), state.Comments);
            state.ActiveOnly = !(json.At("activeOnly") is bool activeOnly) || activeOnly;
            state.NewGroup = !(json.At("newGroup") is bool newGroup) || newGroup;
            state.HideUsed = !(json.At("hideUsed") is bool hideUsed) || hideUsed;
            return state;
        }

        static List<AnnotationGroup> Groups(object source)
        {
            var list = new List<AnnotationGroup>();
            foreach (var item in Json.List(source).Select(Json.Map))
                if (item.At("id") is double id && id >= 1 && id <= int.MaxValue && id == Math.Floor(id) && !list.Any(g => g.Id == (int)id))
                    list.Add(new AnnotationGroup { Id = (int)id, Visible = !(item.At("visible") is bool visible) || visible });
            if (!list.Any(g => g.Id == 1)) list.Insert(0, new AnnotationGroup { Id = 1 });
            return list;
        }

        static int Active(object value, List<AnnotationGroup> groups) => value is double id && groups.Any(g => g.Id == id) ? (int)id : 1;

        internal static List<object> AsJson(List<AnnotationGroup> groups, int active) => groups.Select(g => (object)new JsonMap
        { ["id"] = g.Id, ["name"] = g.Name, ["visible"] = g.Visible, ["active"] = g.Id == active }).ToList();

        static void Save(RhinoDoc doc, AnnotationGroupState state)
        {
            if (doc == null) return;
            doc.Strings.SetString(Section, Entry, Json.Write(new JsonMap
            {
                ["sketches"] = AsJson(state.Sketches, state.ActiveSketch), ["comments"] = AsJson(state.Comments, state.ActiveComment),
                ["activeSketch"] = state.ActiveSketch, ["activeComment"] = state.ActiveComment,
                ["activeOnly"] = state.ActiveOnly, ["newGroup"] = state.NewGroup, ["hideUsed"] = state.HideUsed
            }));
            doc.Modified = true;
            Changed?.Invoke(doc);
            doc.Views.Redraw();
        }

        internal static void TagNew(RhinoDoc doc, ObjectAttributes attributes, bool comment)
        {
            var state = Read(doc);
            attributes.SetUserString(GroupKey, (comment ? state.ActiveComment : state.ActiveSketch).ToString());
        }

        internal static bool Visible(RhinoDoc doc, ObjectAttributes attributes)
        {
            if (attributes.GetUserString(HiddenKey) == "1") return false;
            var state = Read(doc);
            bool comment = attributes.GetUserString(SketchController.CommentKey) != null;
            return (comment ? state.Comments : state.Sketches).FirstOrDefault(g => g.Id == GroupOf(attributes))?.Visible ?? true;
        }

        internal static void SetActive(RhinoDoc doc, bool comment, int id)
        {
            var state = Read(doc);
            if (!(comment ? state.Comments : state.Sketches).Any(g => g.Id == id)) return;
            if (comment) state.ActiveComment = id; else state.ActiveSketch = id;
            Save(doc, state);
        }

        internal static void ToggleGroup(RhinoDoc doc, bool comment, int id)
        {
            var state = Read(doc);
            var group = (comment ? state.Comments : state.Sketches).FirstOrDefault(g => g.Id == id);
            if (group == null) return;
            group.Visible = !group.Visible;
            Save(doc, state);
        }

        internal static void ToggleOption(RhinoDoc doc, bool context)
        {
            var state = Read(doc);
            if (context) state.ActiveOnly = !state.ActiveOnly; else state.NewGroup = !state.NewGroup;
            Save(doc, state);
        }

        internal static void Advance(RhinoDoc doc)
        {
            if (doc == null) return;
            var state = Read(doc);
            bool sketches = SketchController.StrokeObjects(doc).Any(obj => GroupOf(obj.Attributes) == state.ActiveSketch);
            var settings = new ObjectEnumeratorSettings
            {
                ObjectTypeFilter = ObjectType.Point, NormalObjects = true, HiddenObjects = true,
                LockedObjects = true, DeletedObjects = false, VisibleFilter = false
            };
            bool comments = doc.Objects.GetObjectList(settings).Any(obj =>
                obj.Attributes.GetUserString(SketchController.CommentKey) != null && GroupOf(obj.Attributes) == state.ActiveComment);
            if (!sketches && !comments) return;
            if (sketches)
            {
                state.ActiveSketch = state.Sketches.Max(g => g.Id) + 1;
                state.Sketches.Add(new AnnotationGroup { Id = state.ActiveSketch });
            }
            if (comments)
            {
                state.ActiveComment = state.Comments.Max(g => g.Id) + 1;
                state.Comments.Add(new AnnotationGroup { Id = state.ActiveComment });
            }
            Save(doc, state);
        }

        internal static void ToggleHideUsed(RhinoDoc doc)
        {
            var state = Read(doc);
            state.HideUsed = !state.HideUsed;
            Save(doc, state);
        }

        internal static void HideUsedGroups(RhinoDoc doc, int[] sketches, int[] comments)
        {
            if (doc == null) return;
            var state = Read(doc);
            bool changed = false;
            foreach (var group in state.Sketches.Where(g => g.Visible && sketches.Contains(g.Id)))
            { group.Visible = false; changed = true; }
            foreach (var group in state.Comments.Where(g => g.Visible && comments.Contains(g.Id)))
            { group.Visible = false; changed = true; }
            if (changed) Save(doc, state);
        }
    }
}
