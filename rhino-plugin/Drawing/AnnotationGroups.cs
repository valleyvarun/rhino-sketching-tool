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
        public Guid LayerId;          // its sublayer in Rhino: empty until it has one (a group saved before groups were layers)
        public int LayerIndex = -1;
        public bool Visible = true;   // its eye: the sublayer is on
        public bool Context = true;   // its tick: sent to the agent with the next message
        public string Title;          // the sublayer's name
        public string Name => string.IsNullOrWhiteSpace(Title) ? "Group " + Id : Title;
    }

    internal sealed class AnnotationGroupState
    {
        public List<AnnotationGroup> Sketches = new List<AnnotationGroup>();
        public List<AnnotationGroup> Comments = new List<AnnotationGroup>();
        public int ActiveSketch, ActiveComment; // the group new sketches (or comments) go into: 0 while there is none
        public bool ActiveOnly = true, NewGroup = true, HideUsed = true;

        internal Guid SketchParent, CommentParent;    // the "Sketch" and "Comments" layers
        internal int NextSketch = 1, NextComment = 1; // an id is never given twice
        internal bool Pending;                        // found in the layers, but not in the saved record yet
        internal readonly Dictionary<int, int> SketchLayers = new Dictionary<int, int>(), CommentLayers = new Dictionary<int, int>(); // layer index to group id
        internal readonly List<JsonMap> GoneSketches = new List<JsonMap>(), GoneComments = new List<JsonMap>(); // their layer is deleted: Undo can bring one back

        internal List<AnnotationGroup> Groups(bool comment) => comment ? Comments : Sketches;
        internal Dictionary<int, int> Layers(bool comment) => comment ? CommentLayers : SketchLayers;
        internal List<JsonMap> Gone(bool comment) => comment ? GoneComments : GoneSketches;
        internal int Active(bool comment) => comment ? ActiveComment : ActiveSketch;
        internal void SetActive(bool comment, int id) { if (comment) ActiveComment = id; else ActiveSketch = id; }
        internal AnnotationGroup Find(bool comment, int id) => Groups(comment).FirstOrDefault(g => g.Id == id);
    }

    /// <summary>
    /// The groups sketches and comments are kept in. A group IS a Rhino layer: a sublayer of
    /// the "Sketch" layer (or of "Comments"), and a sketch belongs to the group whose layer it
    /// is on. So the groups are in the 3dm file, show in Rhino's own Layers panel, and what is
    /// done to them there (rename, turn off, delete, add a sublayer) is done to the group.
    /// The group's name is the layer's name and its eye is the layer's light bulb.
    ///
    /// What Rhino has no place for is kept as document text: each group's number, its tick
    /// for the agent, which group is being drawn in, and the Agent options. Reading never
    /// changes the document; Tidy does the housekeeping once Rhino is idle.
    ///
    /// A file from before groups were layers has its sketches on the "Sketch" layer itself,
    /// each tagged with its group's number: Tidy gives every such group its sublayer and
    /// moves the sketches onto it. The tag is still written, and still says which group a
    /// sketch is in if it is moved to a layer that is not a group's.
    /// </summary>
    internal static class AnnotationGroups
    {
        internal const string GroupKey = "Crayon.Group", HiddenKey = "Crayon.Hidden";
        const string Section = "Crayon.Groups", Entry = "State";
        const int GoneKept = 64;
        public static event Action<RhinoDoc> Changed;

        // ------------------------------------------------------------ reading

        internal static AnnotationGroupState Read(RhinoDoc doc)
        {
            var state = new AnnotationGroupState();
            if (doc == null) return state;
            var json = Json.Map(Json.Parse(doc.Strings.GetValue(Section, Entry) ?? ""));
            state.ActiveOnly = !(json.At("activeOnly") is bool activeOnly) || activeOnly;
            state.NewGroup = !(json.At("newGroup") is bool newGroup) || newGroup;
            state.HideUsed = !(json.At("hideUsed") is bool hideUsed) || hideUsed;
            Resolve(doc, state, false, json);
            Resolve(doc, state, true, json);
            state.ActiveSketch = Active(json.At("activeSketch"), state.Sketches);
            state.ActiveComment = Active(json.At("activeComment"), state.Comments);
            return state;
        }

        // The groups of one kind: the sublayers of its layer, each with the number and tick
        // saved for it. A sublayer nobody has a number for yet (made in Rhino's Layers
        // panel) is a group too, and gets the next number.
        static void Resolve(RhinoDoc doc, AnnotationGroupState state, bool comment, JsonMap json)
        {
            var groups = state.Groups(comment);
            var gone = state.Gone(comment);
            Guid saved = Guid.TryParse(json.At(comment ? "commentLayer" : "sketchLayer") as string, out Guid savedId) ? savedId : Guid.Empty;
            Layer parent = Parent(doc, comment, saved);
            Guid parentId = parent != null ? parent.Id : saved;
            if (comment) state.CommentParent = parentId; else state.SketchParent = parentId;
            if (parentId != saved) state.Pending = true;

            bool parentShown = parent != null && Shown(doc, parent.Index);
            var children = new Dictionary<Guid, Layer>();
            if (parent != null)
                foreach (Layer child in parent.GetChildren() ?? new Layer[0])
                    if (child != null && !child.IsDeleted) children[child.Id] = child;

            int next = 1;
            var claimed = new HashSet<Guid>();
            var numbers = new HashSet<int>();
            foreach (JsonMap item in Json.List(json.At(comment ? "comments" : "sketches")).Select(Json.Map))
            {
                if (!(item.At("id") is double number) || number < 1 || number >= int.MaxValue || number != Math.Floor(number)) continue;
                int id = (int)number;
                next = Math.Max(next, id + 1);
                if (!numbers.Add(id)) continue;
                bool context = !(item.At("context") is bool ticked) || ticked;
                if (Guid.TryParse(item.At("layer") as string, out Guid layerId) && layerId != Guid.Empty)
                {
                    if (children.TryGetValue(layerId, out Layer layer) && claimed.Add(layerId))
                        groups.Add(new AnnotationGroup { Id = id, LayerId = layerId, LayerIndex = layer.Index, Title = layer.Name, Visible = parentShown && layer.IsVisible, Context = context });
                    else
                        gone.Add(new JsonMap { ["id"] = id, ["layer"] = layerId.ToString(), ["context"] = context });
                }
                else
                {
                    // Saved before groups were layers: shown as it was saved until Tidy gives it its layer.
                    string name = item.At("name") as string;
                    groups.Add(new AnnotationGroup
                    {
                        Id = id, Visible = !(item.At("visible") is bool visible) || visible, Context = context,
                        Title = string.IsNullOrWhiteSpace(name) || name == "Group " + id ? null : name.Trim(),
                    });
                }
            }
            if (json.At(comment ? "nextComment" : "nextSketch") is double following && following > next && following < int.MaxValue) next = (int)following;
            foreach (Layer child in children.Values.Where(layer => !claimed.Contains(layer.Id)).OrderBy(layer => layer.Index))
            {
                groups.Add(new AnnotationGroup { Id = next++, LayerId = child.Id, LayerIndex = child.Index, Title = child.Name, Visible = parentShown && child.IsVisible });
                state.Pending = true;
            }
            groups.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (comment) state.NextComment = next; else state.NextSketch = next;
            foreach (AnnotationGroup group in groups)
                if (group.LayerIndex >= 0) state.Layers(comment)[group.LayerIndex] = group.Id;
        }

        static int Active(object value, List<AnnotationGroup> groups) =>
            value is double id && groups.Any(g => g.Id == id) ? (int)id : groups.Count > 0 ? groups[groups.Count - 1].Id : 0;

        // The "Sketch" (or "Comments") layer: the one remembered, so that renaming it in
        // Rhino loses nothing, or else the one of that name.
        static Layer Parent(RhinoDoc doc, bool comment, Guid saved)
        {
            Layer layer = saved != Guid.Empty ? LayerOf(doc, saved) : null;
            if (layer != null) return layer;
            int index = doc.Layers.FindByFullPath(comment ? SketchController.CommentLayerName : SketchController.LayerName, -1);
            layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index] : null;
            return layer != null && !layer.IsDeleted ? layer : null;
        }

        // On, and so is every layer it is inside of: Rhino shows nothing of a layer whose parent is off.
        static bool Shown(RhinoDoc doc, int index)
        {
            for (int depth = 0; depth < 32; depth++)
            {
                Layer layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index] : null;
                if (layer == null) return true;
                if (!layer.IsVisible) return false;
                if (layer.ParentLayerId == Guid.Empty) return true;
                index = doc.Layers.Find(layer.ParentLayerId, true, RhinoMath.UnsetIntIndex);
            }
            return true;
        }

        static Layer LayerOf(RhinoDoc doc, Guid id)
        {
            if (id == Guid.Empty) return null;
            int index = doc.Layers.Find(id, true, RhinoMath.UnsetIntIndex);
            Layer layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index] : null;
            return layer != null && !layer.IsDeleted ? layer : null;
        }

        /// <summary>
        /// The group a sketch or comment is in: the one whose layer it is on. One that is on
        /// some other layer is in the group it is tagged with, or else in the first. 0 when
        /// there are no groups.
        /// </summary>
        internal static int GroupOf(AnnotationGroupState state, ObjectAttributes attributes)
        {
            bool comment = attributes.GetUserString(SketchController.CommentKey) != null;
            if (state.Layers(comment).TryGetValue(attributes.LayerIndex, out int id)) return id;
            var groups = state.Groups(comment);
            if (int.TryParse(attributes.GetUserString(GroupKey), out int tag) && groups.Any(g => g.Id == tag)) return tag;
            return groups.Count > 0 ? groups[0].Id : 0;
        }

        /// <summary>The groups as the panel's page shows them.</summary>
        internal static List<object> AsJson(List<AnnotationGroup> groups, int active) => groups.Select(g => (object)new JsonMap
        { ["id"] = g.Id, ["name"] = g.Name, ["visible"] = g.Visible, ["context"] = g.Context, ["active"] = g.Id == active }).ToList();

        /// <summary>
        /// Shown: the item's own eye is on, and so is its layer, which for a sketch in a
        /// group is the group's eye.
        /// </summary>
        internal static bool Visible(RhinoDoc doc, ObjectAttributes attributes)
        {
            if (attributes.GetUserString(HiddenKey) == "1") return false;
            return doc == null || Shown(doc, attributes.LayerIndex);
        }

        /// <summary>
        /// Sent to the agent: the item's group is ticked and the item's own eye is on. The tick,
        /// not the group's eye, decides what the agent gets: the eye only shows or hides.
        /// </summary>
        internal static bool InContext(AnnotationGroupState state, ObjectAttributes attributes)
        {
            if (attributes.GetUserString(HiddenKey) == "1") return false;
            bool comment = attributes.GetUserString(SketchController.CommentKey) != null;
            return state.Find(comment, GroupOf(state, attributes))?.Context ?? true;
        }

        /// <summary>What a sketch is called in the panel and for the agent: its name, or Sketch N.</summary>
        internal static string SketchName(RhinoObject obj, int number)
        {
            string name = obj.Name;
            if (string.IsNullOrWhiteSpace(name)) return "Sketch " + number;
            if (name.StartsWith("Stroke ", StringComparison.Ordinal) && int.TryParse(name.Substring(7), out int legacy)) return "Sketch " + legacy;
            return name;
        }

        static List<RhinoObject> Items(RhinoDoc doc, bool comment)
        {
            if (!comment) return SketchController.StrokeObjects(doc).ToList();
            var settings = new ObjectEnumeratorSettings
            {
                ObjectTypeFilter = ObjectType.Point, NormalObjects = true, HiddenObjects = true,
                LockedObjects = true, DeletedObjects = false, VisibleFilter = false
            };
            return doc.Objects.GetObjectList(settings).Where(obj => obj.Attributes.GetUserString(SketchController.CommentKey) != null).ToList();
        }

        // ------------------------------------------------------------- saving

        static void Save(RhinoDoc doc, AnnotationGroupState state, bool quiet = false)
        {
            if (doc == null) return;
            string text = Json.Write(new JsonMap
            {
                ["sketches"] = Saved(state, false), ["comments"] = Saved(state, true),
                ["activeSketch"] = state.ActiveSketch, ["activeComment"] = state.ActiveComment,
                ["activeOnly"] = state.ActiveOnly, ["newGroup"] = state.NewGroup, ["hideUsed"] = state.HideUsed,
                ["nextSketch"] = state.NextSketch, ["nextComment"] = state.NextComment,
                ["sketchLayer"] = state.SketchParent.ToString(), ["commentLayer"] = state.CommentParent.ToString(),
            });
            if (text != doc.Strings.GetValue(Section, Entry))
            {
                bool modified = doc.Modified;
                doc.Strings.SetString(Section, Entry, text);
                doc.Modified = !quiet || modified; // housekeeping does not make an untouched file ask to be saved
            }
            Changed?.Invoke(doc);
            doc.Views.Redraw();
        }

        // Name and eye are the layer's; they are written too, for a build from before groups were layers.
        static List<object> Saved(AnnotationGroupState state, bool comment)
        {
            var list = new List<object>();
            foreach (AnnotationGroup group in state.Groups(comment))
            {
                var item = new JsonMap { ["id"] = group.Id, ["name"] = group.Name, ["visible"] = group.Visible, ["context"] = group.Context };
                if (group.LayerId != Guid.Empty) item["layer"] = group.LayerId.ToString();
                list.Add(item);
            }
            var gone = state.Gone(comment);
            for (int i = Math.Max(0, gone.Count - GoneKept); i < gone.Count; i++) list.Add(gone[i]);
            return list;
        }

        // ------------------------------------------------------------- layers

        static AnnotationGroup Add(AnnotationGroupState state, bool comment)
        {
            int id = comment ? state.NextComment++ : state.NextSketch++;
            var group = new AnnotationGroup { Id = id };
            state.Groups(comment).Add(group);
            state.SetActive(comment, id);
            return group;
        }

        // The "Sketch" (or "Comments") layer, and under it a sublayer for every group that has none.
        static void Materialize(RhinoDoc doc, AnnotationGroupState state, bool comment)
        {
            var groups = state.Groups(comment);
            if (!groups.Any(g => g.LayerId == Guid.Empty)) return;
            Layer parent = Parent(doc, comment, comment ? state.CommentParent : state.SketchParent);
            if (parent == null)
            {
                int made = comment
                    ? doc.Layers.Add(SketchController.CommentLayerName, SketchController.CommentLayerColor)
                    : doc.Layers.Add(SketchController.LayerName, SketchController.Ink);
                if (made < 0) return;
                parent = doc.Layers[made];
            }
            if (comment) state.CommentParent = parent.Id; else state.SketchParent = parent.Id;
            foreach (AnnotationGroup group in groups.Where(g => g.LayerId == Guid.Empty))
            {
                Layer layer = Layer.GetDefaultLayerProperties();
                layer.ParentLayerId = parent.Id;
                layer.Name = FreeName(parent, group.Name, Guid.Empty);
                layer.Color = parent.Color; // a sketch whose colour is "by layer" keeps the colour it had on the parent
                layer.IsVisible = group.Visible;
                int index = doc.Layers.Add(layer);
                if (index < 0) continue;
                Layer added = doc.Layers[index];
                group.LayerId = added.Id;
                group.LayerIndex = index;
                group.Title = added.Name;
                state.Layers(comment)[index] = group.Id;
            }
        }

        // A name no other sublayer of the parent has: Rhino wants the names in one layer to differ.
        static string FreeName(Layer parent, string wanted, Guid self)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Layer child in parent.GetChildren() ?? new Layer[0])
                if (child != null && !child.IsDeleted && child.Id != self) taken.Add(child.Name ?? "");
            string name = wanted;
            for (int n = 2; taken.Contains(name); n++) name = wanted + " (" + n + ")";
            return name;
        }

        // As Rhino's own scripting does it: a sublayer turned off also remembers that for when
        // its parent is turned off and on, and one turned on takes its parents with it.
        static void Show(RhinoDoc doc, Layer layer, bool visible)
        {
            if (visible)
            {
                layer.IsVisible = true;
                if (!Shown(doc, layer.Index)) doc.Layers.ForceLayerVisible(layer.Id);
            }
            else
            {
                LeaveCurrent(doc, layer); // Rhino never hides, or deletes, the current layer
                layer.IsVisible = false;
                if (layer.ParentLayerId != Guid.Empty) layer.SetPersistentVisibility(false);
            }
        }

        static void LeaveCurrent(RhinoDoc doc, Layer layer)
        {
            if (doc.Layers.CurrentLayerIndex != layer.Index) return;
            Layer other = null;
            foreach (Layer candidate in doc.Layers)
            {
                if (candidate == null || candidate.IsDeleted || candidate.IsReference || candidate.Index == layer.Index) continue;
                if (!candidate.IsVisible || candidate.IsLocked || candidate.IsChildOf(layer.Index)) continue;
                if (other == null) other = candidate;
                if (candidate.ParentLayerId == Guid.Empty && candidate.Id != layer.ParentLayerId) { other = candidate; break; } // rather one of the model's own
            }
            if (other != null) doc.Layers.SetCurrentLayerIndex(other.Index, true);
        }

        // The layer goes, with whatever else was put on it, as when it is deleted in Rhino's Layers panel.
        static bool DeleteLayer(RhinoDoc doc, Layer layer)
        {
            int index = layer.Index;
            LeaveCurrent(doc, layer);
            var settings = new ObjectEnumeratorSettings
            {
                LayerIndexFilter = index, NormalObjects = true, HiddenObjects = true, LockedObjects = true,
                DeletedObjects = false, VisibleFilter = false, IncludeLights = true
            };
            foreach (RhinoObject obj in doc.Objects.GetObjectList(settings).ToList()) doc.Objects.Delete(obj, true, true);
            return doc.Layers.Delete(index, true) || doc.Layers.Purge(index, true);
        }

        // --------------------------------------------------- what the panel does

        /// <summary>
        /// The layer a new sketch (or comment) goes on, which is its group's, and that group's
        /// number. Makes the group, and the layers, if they are not there yet. A group that is
        /// turned off is not drawn into (the ink would not show): a new one is started.
        /// </summary>
        internal static int LayerForNew(RhinoDoc doc, bool comment, out int groupId)
        {
            var state = Read(doc);
            bool changed = state.Pending;
            AnnotationGroup group = state.Find(comment, state.Active(comment));
            if (group != null && !group.Visible)
            {
                // Off because the whole "Sketch" layer is off: then nothing shows anyway, as ever.
                Layer parent = Parent(doc, comment, comment ? state.CommentParent : state.SketchParent);
                if (group.LayerId == Guid.Empty || parent == null || Shown(doc, parent.Index)) group = null;
            }
            if (group == null)
            {
                group = Add(state, comment);
                changed = true;
            }
            if (state.Groups(comment).Any(g => g.LayerId == Guid.Empty))
            {
                Materialize(doc, state, comment);
                changed = true;
            }
            if (changed) Save(doc, state);
            groupId = group.Id;
            if (group.LayerIndex >= 0) return group.LayerIndex;
            Layer fallback = Parent(doc, comment, comment ? state.CommentParent : state.SketchParent);
            return fallback != null ? fallback.Index : -1;
        }

        /// <summary>A new, empty group that new sketches (or comments) go into from now on.</summary>
        internal static void Create(RhinoDoc doc, bool comment)
        {
            if (doc == null) return;
            var state = Read(doc);
            Add(state, comment);
            Materialize(doc, state, comment);
            Save(doc, state);
        }

        /// <summary>
        /// Delete a group: its sketches (or comments) and its layer, as one step for Rhino's
        /// Undo, which brings all of it back. If the group being drawn in goes, the last one
        /// left is drawn in; when none is left, the next sketch starts a new one.
        /// </summary>
        internal static void Delete(RhinoDoc doc, bool comment, int id)
        {
            if (doc == null) return;
            var state = Read(doc);
            var groups = state.Groups(comment);
            AnnotationGroup group = state.Find(comment, id);
            if (group == null) return;
            Layer layer = LayerOf(doc, group.LayerId);
            var members = Items(doc, comment).Where(obj => GroupOf(state, obj.Attributes) == id).ToList();
            bool gone = true;
            uint record = doc.BeginUndoRecord(comment ? "Delete Crayon comment group" : "Delete Crayon sketch group");
            try
            {
                foreach (RhinoObject obj in members) doc.Objects.Delete(obj, true, true);
                if (layer != null) gone = DeleteLayer(doc, layer);
            }
            finally { if (record != 0) doc.EndUndoRecord(record); }
            if (gone)
            {
                groups.Remove(group);
                // Kept on record, so that Undo brings the group back as it was.
                if (layer != null) state.Gone(comment).Add(new JsonMap { ["id"] = group.Id, ["layer"] = group.LayerId.ToString(), ["context"] = group.Context });
            }
            else RhinoApp.WriteLine("Crayon: Rhino would not delete the layer " + layer.FullPath + ". What was in the group is deleted.");
            if (state.Find(comment, state.Active(comment)) == null) state.SetActive(comment, groups.Count > 0 ? groups[groups.Count - 1].Id : 0);
            Save(doc, state);
        }

        internal static void Rename(RhinoDoc doc, bool comment, int id, string name)
        {
            if (doc == null) return;
            var state = Read(doc);
            AnnotationGroup group = state.Find(comment, id);
            if (group == null) return;
            name = (name ?? "").Replace("::", ":").Trim(); // "::" is how Rhino writes a layer's path
            if (name.Length == 0) name = "Group " + id;
            Layer layer = LayerOf(doc, group.LayerId);
            if (layer != null)
            {
                Layer parent = LayerOf(doc, layer.ParentLayerId);
                if (parent != null) name = FreeName(parent, name, layer.Id);
                if (layer.Name != name) layer.Name = name;
                group.Title = name;
            }
            else group.Title = name == "Group " + id ? null : name;
            Save(doc, state);
        }

        internal static void SetActive(RhinoDoc doc, bool comment, int id)
        {
            if (doc == null) return;
            var state = Read(doc);
            if (state.Find(comment, id) == null) return;
            state.SetActive(comment, id);
            Save(doc, state);
        }

        /// <summary>A group's eye: its layer's light bulb.</summary>
        internal static void ToggleGroup(RhinoDoc doc, bool comment, int id)
        {
            if (doc == null) return;
            var state = Read(doc);
            AnnotationGroup group = state.Find(comment, id);
            if (group == null) return;
            Layer layer = LayerOf(doc, group.LayerId);
            if (layer != null)
            {
                Show(doc, layer, !group.Visible);
                group.Visible = Shown(doc, layer.Index);
            }
            else group.Visible = !group.Visible;
            Save(doc, state);
        }

        internal static void ToggleContext(RhinoDoc doc, bool comment, int id)
        {
            if (doc == null) return;
            var state = Read(doc);
            AnnotationGroup group = state.Find(comment, id);
            if (group == null) return;
            group.Context = !group.Context;
            Save(doc, state);
        }

        internal static void ToggleOption(RhinoDoc doc, bool context)
        {
            if (doc == null) return;
            var state = Read(doc);
            if (context) state.ActiveOnly = !state.ActiveOnly; else state.NewGroup = !state.NewGroup;
            Save(doc, state);
        }

        internal static void ToggleHideUsed(RhinoDoc doc)
        {
            if (doc == null) return;
            var state = Read(doc);
            state.HideUsed = !state.HideUsed;
            Save(doc, state);
        }

        // --------------------------------------------------- what a prompt does

        /// <summary>
        /// A prompt has been sent: what is drawn from now on belongs to the next one, so it
        /// goes into a new group. Only where the group being drawn in has something in it:
        /// an empty group is the new group already.
        /// </summary>
        internal static void Advance(RhinoDoc doc)
        {
            if (doc == null) return;
            var state = Read(doc);
            bool sketches = state.ActiveSketch != 0 && Items(doc, false).Any(obj => GroupOf(state, obj.Attributes) == state.ActiveSketch);
            bool comments = state.ActiveComment != 0 && Items(doc, true).Any(obj => GroupOf(state, obj.Attributes) == state.ActiveComment);
            if (!sketches && !comments) return;
            if (sketches) { Add(state, false); Materialize(doc, state, false); }
            if (comments) { Add(state, true); Materialize(doc, state, true); }
            Save(doc, state);
        }

        /// <summary>The groups a prompt took along are turned off, and unticked for the agent.</summary>
        internal static void HideUsedGroups(RhinoDoc doc, int[] sketches, int[] comments)
        {
            if (doc == null) return;
            var state = Read(doc);
            bool changed = false;
            foreach (bool comment in new[] { false, true })
                foreach (AnnotationGroup group in state.Groups(comment).Where(g => (g.Visible || g.Context) && (comment ? comments : sketches).Contains(g.Id)))
                {
                    Layer layer = LayerOf(doc, group.LayerId);
                    if (layer != null && layer.IsVisible) Show(doc, layer, false); // its own light bulb: off
                    group.Visible = false;
                    group.Context = false;
                    changed = true;
                }
            if (changed) Save(doc, state);
        }

        // -------------------------------------------------------- housekeeping

        static readonly HashSet<uint> stale = new HashSet<uint>();                              // documents to look at again, by RuntimeSerialNumber
        static readonly Dictionary<uint, DateTime> opening = new Dictionary<uint, DateTime>(); // documents still being read from a file
        static bool watching, tidying;

        /// <summary>Call once, when the plug-in loads.</summary>
        internal static void Watch()
        {
            if (watching) return;
            watching = true;
            RhinoDoc.BeginOpenDocument += (s, e) => opening[e.DocumentSerialNumber] = DateTime.UtcNow;
            RhinoDoc.EndOpenDocument += (s, e) => { opening.Remove(e.DocumentSerialNumber); stale.Add(e.DocumentSerialNumber); };
            RhinoDoc.CloseDocument += (s, e) => { opening.Remove(e.DocumentSerialNumber); stale.Remove(e.DocumentSerialNumber); };
            RhinoDoc.AddRhinoObject += (s, e) => Touched(e.TheObject);
            RhinoDoc.UndeleteRhinoObject += (s, e) => Touched(e.TheObject);
            RhinoDoc.ModifyObjectAttributes += (s, e) => Touched(e.RhinoObject);
            RhinoDoc.LayerTableEvent += (s, e) => { if (e.Document != null) stale.Add(e.Document.RuntimeSerialNumber); };
            foreach (RhinoDoc doc in RhinoDoc.OpenDocuments()) stale.Add(doc.RuntimeSerialNumber);
        }

        static void Touched(RhinoObject obj)
        {
            RhinoDoc doc = obj?.Document;
            if (doc == null || opening.ContainsKey(doc.RuntimeSerialNumber)) return; // a file being read is looked at once it is read
            ObjectAttributes attributes = obj.Attributes;
            if (attributes.GetUserString(SketchController.StrokeKey) != null || attributes.GetUserString(SketchController.CommentKey) != null)
                stale.Add(doc.RuntimeSerialNumber);
        }

        /// <summary>
        /// Called when Rhino has nothing else to do and no command is running. Puts right, in
        /// the documents that have changed, what the layers do not show yet: gives the groups
        /// of a file from before groups were layers their sublayers and moves their sketches
        /// onto them, and takes note of sublayers made in Rhino. Nothing here is a step for
        /// Undo, nor makes an untouched file ask to be saved.
        /// </summary>
        internal static void Tidy()
        {
            if (stale.Count == 0 || tidying) return;
            tidying = true;
            try
            {
                foreach (uint serial in stale.ToArray())
                {
                    // Not while its file is still being read (that is given ten minutes at most).
                    if (opening.TryGetValue(serial, out DateTime since) && (DateTime.UtcNow - since).TotalMinutes < 10) continue;
                    opening.Remove(serial);
                    stale.Remove(serial);
                    RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(serial);
                    if (doc == null) continue;
                    try { Tidy(doc); }
                    catch (Exception ex) { RhinoApp.WriteLine("Crayon groups: " + ex.Message); }
                }
            }
            finally { tidying = false; }
        }

        static void Tidy(RhinoDoc doc)
        {
            var state = Read(doc);
            var items = new[] { Items(doc, false), Items(doc, true) };
            bool work = state.Pending;
            foreach (bool comment in new[] { false, true })
            {
                var mine = items[comment ? 1 : 0];
                var groups = state.Groups(comment);
                Layer parent = Parent(doc, comment, comment ? state.CommentParent : state.SketchParent);
                if (parent != null && mine.Any(obj => obj.Attributes.LayerIndex == parent.Index)) work = true;                // not on a group's layer yet
                else if (groups.Any(g => g.LayerId == Guid.Empty) && (parent != null || mine.Count > 0)) work = true;        // a group with no layer yet
                else if (mine.Count > 0 && groups.Count == 0) work = true;                                                   // sketches, and no group at all
            }
            if (!work) return;
            // A document Crayon has never been used in is left alone, whatever its layers are called.
            if (doc.Strings.GetValue(Section, Entry) == null && items[0].Count == 0 && items[1].Count == 0) return;

            bool modified = doc.Modified, recording = doc.UndoRecordingEnabled;
            doc.UndoRecordingEnabled = false;
            try
            {
                foreach (bool comment in new[] { false, true })
                {
                    var mine = items[comment ? 1 : 0];
                    var groups = state.Groups(comment);
                    Layer parent = Parent(doc, comment, comment ? state.CommentParent : state.SketchParent);
                    if (parent == null && mine.Count == 0) continue; // Crayon has not been used here for these: leave the document alone

                    // A sketch tagged with a group that is no longer listed gets that group back.
                    foreach (RhinoObject obj in mine)
                    {
                        if (state.Layers(comment).ContainsKey(obj.Attributes.LayerIndex)) continue;
                        if (!int.TryParse(obj.Attributes.GetUserString(GroupKey), out int tag) || tag < 1 || groups.Any(g => g.Id == tag)) continue;
                        groups.Add(new AnnotationGroup { Id = tag });
                        if (comment) state.NextComment = Math.Max(state.NextComment, tag + 1); else state.NextSketch = Math.Max(state.NextSketch, tag + 1);
                    }
                    groups.Sort((a, b) => a.Id.CompareTo(b.Id));
                    if (mine.Count > 0 && groups.Count == 0) Add(state, comment);
                    if (state.Find(comment, state.Active(comment)) == null) state.SetActive(comment, groups.Count > 0 ? groups[groups.Count - 1].Id : 0);
                    Materialize(doc, state, comment);

                    parent = Parent(doc, comment, comment ? state.CommentParent : state.SketchParent);
                    if (parent == null) continue;
                    foreach (RhinoObject obj in mine)
                    {
                        if (obj.Attributes.LayerIndex != parent.Index) continue;
                        AnnotationGroup group = state.Find(comment, GroupOf(state, obj.Attributes));
                        if (group == null || group.LayerIndex < 0) continue;
                        ObjectAttributes attributes = obj.Attributes.Duplicate();
                        attributes.LayerIndex = group.LayerIndex;
                        doc.Objects.ModifyAttributes(obj, attributes, true);
                    }
                }
            }
            finally
            {
                doc.UndoRecordingEnabled = recording;
                doc.Modified = modified;
            }
            Save(doc, state, true);
        }
    }
}
