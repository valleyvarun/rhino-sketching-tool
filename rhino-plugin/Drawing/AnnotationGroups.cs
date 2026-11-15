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
        public bool Visible = true;   // its eye: shown in the viewports
        public bool Context = true;   // its tick: sent to the agent with the next message
        public string Title; // set when the user renames the group
        public string Name => string.IsNullOrWhiteSpace(Title) ? "Group " + Id : Title;
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
                {
                    string name = item.At("name") as string;
                    list.Add(new AnnotationGroup
                    {
                        Id = (int)id, Visible = !(item.At("visible") is bool visible) || visible,
                        Context = !(item.At("context") is bool context) || context,
                        Title = string.IsNullOrWhiteSpace(name) || name == "Group " + (int)id ? null : name.Trim(),
                    });
                }
            if (list.Count == 0) list.Add(new AnnotationGroup { Id = 1 });
            return list;
        }

        static int Active(object value, List<AnnotationGroup> groups) => value is double id && groups.Any(g => g.Id == id) ? (int)id : groups[0].Id;

        internal static List<object> AsJson(List<AnnotationGroup> groups, int active) => groups.Select(g => (object)new JsonMap
        { ["id"] = g.Id, ["name"] = g.Name, ["visible"] = g.Visible, ["context"] = g.Context, ["active"] = g.Id == active }).ToList();

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

        internal static bool Visible(RhinoDoc doc, ObjectAttributes attributes) => Visible(Read(doc), attributes);

        /// <summary>
        /// Shown, and so part of what the agent is given: the item's own eye is on and so is
        /// its group's. The groups whose eye is on are the agent's context.
        /// </summary>
        internal static bool Visible(AnnotationGroupState state, ObjectAttributes attributes)
        {
            if (attributes.GetUserString(HiddenKey) == "1") return false;
            bool comment = attributes.GetUserString(SketchController.CommentKey) != null;
            return (comment ? state.Comments : state.Sketches).FirstOrDefault(g => g.Id == GroupOf(attributes))?.Visible ?? true;
        }

        /// <summary>
        /// Sent to the agent: the item's group is ticked and the item's own eye is on. The tick,
        /// not the group's eye, decides what the agent gets: the eye only shows or hides.
        /// </summary>
        internal static bool InContext(AnnotationGroupState state, ObjectAttributes attributes)
        {
            if (attributes.GetUserString(HiddenKey) == "1") return false;
            bool comment = attributes.GetUserString(SketchController.CommentKey) != null;
            return (comment ? state.Comments : state.Sketches).FirstOrDefault(g => g.Id == GroupOf(attributes))?.Context ?? true;
        }

        internal static void ToggleContext(RhinoDoc doc, bool comment, int id)
        {
            var state = Read(doc);
            var group = (comment ? state.Comments : state.Sketches).FirstOrDefault(g => g.Id == id);
            if (group == null) return;
            group.Context = !group.Context;
            Save(doc, state);
        }

        /// <summary>A new, empty group that new sketches (or comments) go into from now on.</summary>
        internal static void Create(RhinoDoc doc, bool comment)
        {
            if (doc == null) return;
            var state = Read(doc);
            var groups = comment ? state.Comments : state.Sketches;
            int id = groups.Max(g => g.Id) + 1;
            groups.Add(new AnnotationGroup { Id = id });
            if (comment) state.ActiveComment = id; else state.ActiveSketch = id;
            Save(doc, state);
        }

        /// <summary>
        /// Delete a group and the sketches (or comments) in it, as one Undo step for the
        /// objects. There is always at least one group: deleting the last one leaves a new,
        /// empty one. If the active group goes, the last remaining group becomes active.
        /// </summary>
        internal static void Delete(RhinoDoc doc, bool comment, int id)
        {
            if (doc == null) return;
            var state = Read(doc);
            var groups = comment ? state.Comments : state.Sketches;
            var group = groups.FirstOrDefault(g => g.Id == id);
            if (group == null) return;
            var members = (comment ? CommentObjects(doc) : SketchController.StrokeObjects(doc))
                .Where(obj => GroupOf(obj.Attributes) == id).ToList();
            uint record = doc.BeginUndoRecord(comment ? "Delete Crayon comment group" : "Delete Crayon sketch group");
            try { foreach (RhinoObject obj in members) doc.Objects.Delete(obj, true, true); }
            finally { if (record != 0) doc.EndUndoRecord(record); }
            groups.Remove(group);
            if (groups.Count == 0) groups.Add(new AnnotationGroup { Id = id + 1 });
            int active = comment ? state.ActiveComment : state.ActiveSketch;
            if (active == id || !groups.Any(g => g.Id == active)) active = groups[groups.Count - 1].Id;
            if (comment) state.ActiveComment = active; else state.ActiveSketch = active;
            Save(doc, state);
        }

        /// <summary>
        /// Groups as Read, after giving back any group that sketches or comments still name
        /// but the list no longer has (Rhino's Undo of a group delete brings the objects
        /// back, not the group). So nothing can be in the model without a row in Layers.
        /// </summary>
        internal static AnnotationGroupState Heal(RhinoDoc doc)
        {
            var state = Read(doc);
            if (doc == null) return state;
            bool changed = false;
            foreach (int id in SketchController.StrokeObjects(doc).Select(obj => GroupOf(obj.Attributes)).Distinct())
                if (!state.Sketches.Any(g => g.Id == id)) { state.Sketches.Add(new AnnotationGroup { Id = id }); changed = true; }
            foreach (int id in CommentObjects(doc).Select(obj => GroupOf(obj.Attributes)).Distinct())
                if (!state.Comments.Any(g => g.Id == id)) { state.Comments.Add(new AnnotationGroup { Id = id }); changed = true; }
            if (!changed) return state;
            state.Sketches.Sort((a, b) => a.Id.CompareTo(b.Id));
            state.Comments.Sort((a, b) => a.Id.CompareTo(b.Id));
            bool modified = doc.Modified;
            Save(doc, state);
            doc.Modified = modified;
            return state;
        }

        static IEnumerable<RhinoObject> CommentObjects(RhinoDoc doc)
        {
            var settings = new ObjectEnumeratorSettings
            {
                ObjectTypeFilter = ObjectType.Point, NormalObjects = true, HiddenObjects = true,
                LockedObjects = true, DeletedObjects = false, VisibleFilter = false
            };
            return doc.Objects.GetObjectList(settings).Where(obj => obj.Attributes.GetUserString(SketchController.CommentKey) != null);
        }

        internal static void Rename(RhinoDoc doc, bool comment, int id, string name)
        {
            if (doc == null) return;
            var state = Read(doc);
            var group = (comment ? state.Comments : state.Sketches).FirstOrDefault(g => g.Id == id);
            if (group == null) return;
            name = (name ?? "").Trim();
            group.Title = name.Length == 0 || name == "Group " + id ? null : name;
            Save(doc, state);
        }

        /// <summary>What a sketch is called in the panel and for the agent: its name, or Sketch N.</summary>
        internal static string SketchName(RhinoObject obj, int number)
        {
            string name = obj.Name;
            if (string.IsNullOrWhiteSpace(name)) return "Sketch " + number;
            if (name.StartsWith("Stroke ", StringComparison.Ordinal) && int.TryParse(name.Substring(7), out int legacy)) return "Sketch " + legacy;
            return name;
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
            foreach (var group in state.Sketches.Where(g => (g.Visible || g.Context) && sketches.Contains(g.Id)))
            { group.Visible = false; group.Context = false; changed = true; }
            foreach (var group in state.Comments.Where(g => (g.Visible || g.Context) && comments.Contains(g.Id)))
            { group.Visible = false; group.Context = false; changed = true; }
            if (changed) Save(doc, state);
        }
    }
}
