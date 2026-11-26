using System;
using System.Collections.Generic;
using System.Globalization;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
#if MAC // no WPF on a Mac: the same names, made with Eto (Mac/MacDrawing.cs)
using Media = RhinoSketchingTool.MacDrawing;
using ScreenPoint = System.Drawing.Point;
using Wpf = RhinoSketchingTool.MacDrawing;
#else
using Media = System.Windows.Media;
using ScreenPoint = System.Drawing.Point;
using Wpf = System.Windows;
#endif

namespace RhinoSketchingTool
{
    /// <summary>A comment pinned in the model: a number and a line of text at a point.</summary>
    internal sealed class Comment
    {
        public Guid Id;          // its Rhino object: empty for one that is still being written
        public int Number;
        public string Text = "";
        public Point3d Point;
        public Vector3d Normal;
        public Guid Sketch;      // the sketch it was put on, if any
        public Guid Object;      // or the object
        public bool Visible = true; // false while its layer is turned off
        public string Image = "";   // its picture, if it has one: the picture's id (see CommentImages.cs),
        public int ImageWidth, ImageHeight; // and its size in pixels
        public byte[] NewImage;     // a picture given while the text is being typed: not in the document yet
    }

    /// <summary>
    /// The Comment tool (the design calls it Marks). Click anywhere to pin a comment,
    /// then type: Return keeps it, Esc or leaving it empty removes it. Clicking a comment
    /// that is already there opens it to be changed. The eraser removes comments too.
    /// A comment can carry a picture, shown as a thumbnail above its label: see
    /// CommentImages.cs.
    ///
    /// Where a click lands, in this order: on a sketch line within 14 pixels of the
    /// pointer (the pin snaps onto the line), on the surface under the pointer, on the
    /// construction plane. Before the click, a ring shows where that would be (filled
    /// once it has snapped to a sketch), with a chip naming the sketch or the object's
    /// layer. Hovering a comment shows what it is on in the same way.
    ///
    /// A comment is a point object on its group's sublayer of the "Comments" layer (see
    /// AnnotationGroups), with its number, its text,
    /// what it is on and the view it was made in saved on it as user strings: so it is
    /// part of the 3dm file and of Rhino's undo. Like sketches, the points are hidden
    /// objects that only this plug-in paints, so nothing snaps to or selects them.
    /// </summary>
    public sealed partial class SketchController
    {
        public const string CommentLayerName = "Comments";
        public const string CommentKey = "SketchingTool.Comment"; // the comment's number
        const string CommentTextKey = "SketchingTool.CommentText";
        const string CommentNormalKey = "SketchingTool.CommentNormal";
        const string CommentOnKey = "SketchingTool.CommentOn";    // "sketch <id>" or "object <id>"
        const string CommentViewKey = "SketchingTool.CommentView"; // the view it was made in, and its camera

        const double CommentSnap = 14; // pixels: a sketch line this near the pointer takes the comment

        // The label: a white pill above the point, with the number in a violet disc.
        // (design/prototype: .mlabel, .mnum, .mlink, .cmt-ghost, .snapchip)
        const double PillHeight = 25, DiscSize = 19, PillGap = 6, PillEnd = 10, WritingWidth = 170, MaxTextWidth = 260;
        const double LabelLift = 10;  // from the pill down to the point it marks
        const double TagHeight = 19, TagGap = 5;
        const double PictureMargin = 8;
        static readonly Media.Color TagDark = Picture.Rgb(0x16, 0x17, 0x1b), ChipDark = Picture.Rgb(0x11, 0x11, 0x11), White = Picture.Rgb(255, 255, 255);
        internal static readonly System.Drawing.Color CommentLayerColor = System.Drawing.Color.FromArgb(0x97, 0x31, 0xb0);

        // What is known of one document, read again once it has changed.
        sealed class Scene
        {
            public bool CommentsStale = true, AimStale = true;
            public List<Comment> Comments = new List<Comment>();
            public List<Stroke> Strokes = new List<Stroke>();
            public List<Target> Targets = new List<Target>();
        }

        // Where a comment would land if the pointer were clicked now.
        sealed class Aim
        {
            public uint View;        // RhinoView.RuntimeSerialNumber
            public Point2d Screen;
            public Point3d Point;
            public Vector3d Normal;
            public Guid Sketch, Object;
            public string Chip = ""; // "Sketch · name" or "Layer · name": empty on the grid
        }

        // A label as painted: the picture, and where things are in it (pixels at 100 %).
        sealed class LabelPicture
        {
            public DisplayBitmap Bitmap;
            public double AnchorX, AnchorY; // the marked point
            public Wpf.Rect Pill;
            public double TextLeft;
            public Wpf.Rect AddImage; // the picture button at the end of the pill: no width unless it is there
        }

        readonly Dictionary<uint, Scene> scenes = new Dictionary<uint, Scene>(); // by RhinoDoc.RuntimeSerialNumber
        readonly Dictionary<string, LabelPicture> labels = new Dictionary<string, LabelPicture>();
        CommentMarks marks;
        bool sceneNotice;

        Aim aim;
        Guid overComment;        // the comment whose label the pointer is on
        Comment writing;         // the comment whose text is being typed,
        RhinoView writingView;   // in this view,
        CommentEditor editor;    // in this box
#if !MAC
        ScreenPoint editorAt;    // where the in-place box was last put, in the view's pixels
#endif

        /// <summary>Raised (once Rhino is idle) after sketches, comments or layers have changed.</summary>
        public event EventHandler SceneChanged;

        // Called once, when painting starts.
        void StartComments()
        {
            marks = new CommentMarks(this) { Enabled = true };
            RhinoDoc.AddRhinoObject += (s, e) => SceneStale();
            RhinoDoc.DeleteRhinoObject += (s, e) => SceneStale();
            RhinoDoc.UndeleteRhinoObject += (s, e) => SceneStale();
            RhinoDoc.ReplaceRhinoObject += (s, e) => SceneStale();
            RhinoDoc.ModifyObjectAttributes += (s, e) => SceneStale();
            RhinoDoc.LayerTableEvent += (s, e) => SceneStale();
            AnnotationGroups.Changed += doc => SceneStale();
            RhinoDoc.EndOpenDocument += (s, e) => SceneStale();
            RhinoDoc.CloseDocument += (s, e) => { scenes.Remove(e.Document.RuntimeSerialNumber); SceneStale(); };
            RhinoApp.Idle += (s, e) =>
            {
                if (!sceneNotice) return;
                sceneNotice = false;
                SceneChanged?.Invoke(this, EventArgs.Empty);
            };
            KeyHook.TextEntry.Add(() => editor != null && editor.IsOpen); // Esc there is for the comment
        }

        void StopComments()
        {
            editor?.Cancel();
            if (marks != null) marks.Enabled = false;
        }

        void SceneStale()
        {
            foreach (Scene scene in scenes.Values) scene.CommentsStale = scene.AimStale = true;
            sceneNotice = true;
        }

        Scene SceneOf(RhinoDoc doc)
        {
            if (!scenes.TryGetValue(doc.RuntimeSerialNumber, out Scene scene)) scenes[doc.RuntimeSerialNumber] = scene = new Scene();
            if (scene.CommentsStale)
            {
                scene.CommentsStale = false;
                scene.Comments = ReadComments(doc);
            }
            return scene;
        }

        // Comments are hidden objects, which Rhino's ordinary ways of listing objects skip.
        static readonly ObjectEnumeratorSettings AnyPoint = new ObjectEnumeratorSettings
        {
            ObjectTypeFilter = ObjectType.Point,
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            DeletedObjects = false,
            VisibleFilter = false,
        };

        static List<Comment> ReadComments(RhinoDoc doc)
        {
            var comments = new List<Comment>();
            foreach (RhinoObject obj in doc.Objects.FindByUserString(CommentKey, "*", false, false, true, AnyPoint) ?? new RhinoObject[0])
            {
                if (!(obj.Geometry is Point point)) continue;
                ObjectAttributes attributes = obj.Attributes;
                int.TryParse(attributes.GetUserString(CommentKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number);
                var comment = new Comment
                {
                    Id = obj.Id,
                    Number = number,
                    Text = attributes.GetUserString(CommentTextKey) ?? "",
                    Point = point.Location,
                    Normal = Vector(attributes.GetUserString(CommentNormalKey)),
                    Visible = OnVisibleLayer(doc, obj),
                };
                string[] on = (attributes.GetUserString(CommentOnKey) ?? "").Split(' ');
                if (on.Length == 2 && Guid.TryParse(on[1], out Guid id))
                {
                    if (on[0] == "sketch") comment.Sketch = id;
                    else comment.Object = id;
                }
                // Which picture it has and how big: the picture itself is only read when it is shown.
                string[] image = (attributes.GetUserString(CommentImageKey) ?? "").Split(';');
                if (image.Length >= 3 && image[0].Length > 0 &&
                    int.TryParse(image[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int imageWidth) && imageWidth > 0 &&
                    int.TryParse(image[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int imageHeight) && imageHeight > 0)
                {
                    comment.Image = image[0];
                    comment.ImageWidth = imageWidth;
                    comment.ImageHeight = imageHeight;
                }
                comments.Add(comment);
            }
            comments.Sort((a, b) => a.Number.CompareTo(b.Number));
            return comments;
        }

        static Vector3d Vector(string text)
        {
            string[] parts = (text ?? "").Split(',');
            if (parts.Length == 3 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) return new Vector3d(x, y, z);
            return Vector3d.ZAxis;
        }

        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The document's comments, in the order of their numbers.</summary>
        internal IReadOnlyList<Comment> Comments(RhinoDoc doc) => SceneOf(doc).Comments;

        // ------------------------------------------------------------- aiming

        Aim AimAt(RhinoView view, ScreenPoint point)
        {
            RhinoDoc doc = view.Document;
            RhinoViewport viewport = view.ActiveViewport;
            Scene scene = SceneOf(doc);
            if (scene.AimStale)
            {
                scene.AimStale = false;
                scene.Strokes = CollectStrokes(doc);
                scene.Targets = CollectTargets(doc);
            }

            Vector3d towardCamera = -viewport.CameraDirection;
            towardCamera.Unitize();
            var pointer = new Point2d(point.X, point.Y);

            // 1. A sketch line near the pointer: the comment snaps onto the line.
            Aim best = null;
            double nearest = CommentSnap * Toolbar.Scale(view);
            foreach (Stroke stroke in scene.Strokes)
            {
                Point2d? a = ToScreen(viewport, stroke.Points[0]);
                for (int i = 1; i < stroke.Points.Length; i++)
                {
                    Point2d? b = ToScreen(viewport, stroke.Points[i]);
                    if (a.HasValue && b.HasValue)
                    {
                        Vector2d ab = b.Value - a.Value, ap = pointer - a.Value;
                        double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
                        double t = lengthSquared > 0 ? Math.Max(0, Math.Min(1, (ap.X * ab.X + ap.Y * ab.Y) / lengthSquared)) : 0;
                        Point2d on = a.Value + ab * t;
                        double distance = on.DistanceTo(pointer);
                        if (distance < nearest)
                        {
                            nearest = distance;
                            best = new Aim
                            {
                                Screen = on,
                                Point = stroke.Points[i - 1] + (stroke.Points[i] - stroke.Points[i - 1]) * t,
                                Normal = towardCamera,
                                Sketch = stroke.Id,
                                Chip = "Sketch · " + SketchName(stroke.Attributes),
                            };
                        }
                    }
                    a = b;
                }
            }

            if (best == null && ViewLine(viewport, point, out Line line))
            {
                // 2. The surface under the pointer. 3. The construction plane.
                Hit hit = HitSurface(viewport, line, scene.Targets);
                if (hit != null)
                {
                    RhinoObject obj = doc.Objects.FindId(hit.ObjectId);
                    best = new Aim { Screen = pointer, Point = hit.Point, Normal = hit.Normal, Object = hit.ObjectId, Chip = obj != null ? "Layer · " + LayerPath(doc, obj) : "" };
                }
                else
                {
                    hit = HitPlane(viewport, line, viewport.ConstructionPlane());
                    if (hit != null) best = new Aim { Screen = pointer, Point = hit.Point, Normal = hit.Normal };
                }
            }
            if (best == null) return null;
            if (best.Normal * towardCamera < 0) best.Normal = -best.Normal; // it faces whoever made it
            best.View = view.RuntimeSerialNumber;
            return best;
        }

        static string SketchName(ObjectAttributes attributes) => string.IsNullOrEmpty(attributes.Name) ? "Sketch" : attributes.Name;

        static string LayerPath(RhinoDoc doc, RhinoObject obj)
        {
            int index = obj.Attributes.LayerIndex;
            return index >= 0 && index < doc.Layers.Count && doc.Layers[index] != null ? doc.Layers[index].FullPath : "";
        }

        // What a comment is on, as two words for its tag: ("Sketch", its name) or
        // ("Layer", the object's layer). Null once that sketch or object is gone.
        static string[] LinkOf(RhinoDoc doc, Comment comment)
        {
            if (comment.Sketch != Guid.Empty)
            {
                RhinoObject sketch = doc.Objects.FindId(comment.Sketch);
                return sketch != null && !sketch.IsDeleted ? new[] { "Sketch", SketchName(sketch.Attributes) } : null;
            }
            if (comment.Object != Guid.Empty)
            {
                RhinoObject obj = doc.Objects.FindId(comment.Object);
                string layer = obj != null && !obj.IsDeleted ? LayerPath(doc, obj) : "";
                return layer.Length > 0 ? new[] { "Layer", layer } : null;
            }
            return null;
        }

        // "Sketch 4" for the next sketch, going by the names already given.
        static string NextSketchName(RhinoDoc doc)
        {
            int highest = 0;
            foreach (RhinoObject obj in StrokeObjects(doc))
            {
                string name = obj.Attributes.Name ?? "";
                if ((name.StartsWith("Sketch ", StringComparison.Ordinal) || name.StartsWith("Stroke ", StringComparison.Ordinal)) && int.TryParse(name.Substring(7), out int number)) highest = Math.Max(highest, number);
            }
            return "Stroke " + (highest + 1);
        }

        // -------------------------------------------------------------- mouse

        // A click with the Comment tool.
        void CommentClick(RhinoView view, ScreenPoint point)
        {
            editor?.Commit(); // a comment still being written is kept as it stands
            Comment existing = LabelAt(view, point);
            if (existing != null)
            {
                // The picture button at the end of the label chooses a picture for the comment;
                // anywhere else on the label opens its text.
                if (PlaceLabel(view.ActiveViewport, view.Document, existing, Toolbar.Scale(view), out LabelPlace place) &&
                    place.AddImage.Width > 0 && place.AddImage.Contains(point))
                    PickCommentImage(view, existing);
                else
                    Write(view, existing);
                return;
            }
            Aim target = AimAt(view, point);
            if (target == null) return;
            int number = 1;
            foreach (Comment comment in SceneOf(view.Document).Comments) number = Math.Max(number, comment.Number + 1);
            Write(view, new Comment { Number = number, Point = target.Point, Normal = target.Normal, Sketch = target.Sketch, Object = target.Object });
        }

        /// <summary>The pointer has moved over a view with no button down.</summary>
        internal void Hover(RhinoView view, ScreenPoint point)
        {
            if (!active || view == null || Dragging) return;
            Comment over = writing == null ? LabelAt(view, point) : null;
            Aim next = tool == SketchTool.Comment && over == null && writing == null ? AimAt(view, point) : null;
            Guid overId = over?.Id ?? Guid.Empty;
            bool same = overId == overComment && (aim == null) == (next == null) &&
                        (aim == null || (aim.View == next.View && aim.Screen.DistanceTo(next.Screen) < 0.5 && aim.Chip == next.Chip && aim.Sketch == next.Sketch));
            overComment = overId;
            aim = next;
            if (!same) view.Redraw();
        }

        /// <summary>The pointer has left the views, or is on the toolbar.</summary>
        internal void HoverEnd()
        {
            if (aim == null && overComment == Guid.Empty) return;
            aim = null;
            overComment = Guid.Empty;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        // The comment whose label is under a point of the view.
        Comment LabelAt(RhinoView view, ScreenPoint point)
        {
            if (view?.Document == null) return null;
            RhinoDoc doc = view.Document;
            double scale = Toolbar.Scale(view);
            List<Comment> comments = SceneOf(doc).Comments;
            for (int i = comments.Count - 1; i >= 0; i--) // the last painted is on top
            {
                Comment comment = comments[i];
                if (!comment.Visible) continue;
                if (LabelBox(view.ActiveViewport, doc, comment, scale, out System.Drawing.RectangleF pill, out _, out _) && pill.Contains(point)) return comment;
            }
            return null;
        }

        // Where a comment's label is in a viewport, in the viewport's pixels: the pill,
        // and where the picture goes. False when its point is out of sight.
        bool LabelBox(RhinoViewport viewport, RhinoDoc doc, Comment comment, double scale, out System.Drawing.RectangleF pill, out ScreenPoint corner, out LabelPicture label)
        {
            bool there = PlaceLabel(viewport, doc, comment, scale, out LabelPlace place);
            pill = place.Pill;
            corner = place.Corner;
            label = place.Label;
            return there;
        }

        // The same, with the comment's picture and its buttons if it has one, and the picture
        // button at the end of the label while the pointer is on it with the Comment tool.
        bool PlaceLabel(RhinoViewport viewport, RhinoDoc doc, Comment comment, double scale, out LabelPlace place)
        {
            place = default(LabelPlace);
            Point2d? screen = ToScreen(viewport, comment.Point);
            System.Drawing.Size size = viewport.Size;
            if (!screen.HasValue || screen.Value.X < 0 || screen.Value.Y < 0 || screen.Value.X > size.Width || screen.Value.Y > size.Height) return false;

            bool beingWritten = writing != null && writing.Id == comment.Id && writingView != null && writingView.ActiveViewportID == viewport.Id;
            bool pointedAt = !beingWritten && comment.Id != Guid.Empty && comment.Id == overComment;
            string[] link = pointedAt ? LinkOf(doc, comment) : null;
            bool addImage = pointedAt && active && tool == SketchTool.Comment;

            // The picture goes between the pill and the tag that says what the comment is on.
            bool hasImage = comment.Image.Length > 0 && comment.ImageWidth > 0 && comment.ImageHeight > 0;
            double imageWidth = 0, imageHeight = 0, room = 0;
            if (hasImage)
            {
                // What there is between the pill and the top of the viewport, for a picture made larger.
                double above = screen.Value.Y / scale - LabelLift - PillHeight - ImageGap - 2 * ImagePad - 6;
                ImageSize(comment, enlarged.Contains(comment.Id), size.Width / scale - 16, above, out imageWidth, out imageHeight);
                room = imageHeight + 2 * ImagePad + ImageGap;
            }

            LabelPicture label = LabelFor(comment, link, beingWritten, scale, addImage, room);
            double left = Math.Round(screen.Value.X - label.AnchorX * scale), top = Math.Round(screen.Value.Y - label.AnchorY * scale);
            place.Label = label;
            place.Corner = new ScreenPoint((int)left, (int)top);
            place.Pill = new System.Drawing.RectangleF((float)(left + label.Pill.X * scale), (float)(top + label.Pill.Y * scale), (float)(label.Pill.Width * scale), (float)(label.Pill.Height * scale));
            if (label.AddImage.Width > 0)
                place.AddImage = new System.Drawing.RectangleF((float)(left + label.AddImage.X * scale), (float)(top + label.AddImage.Y * scale), (float)(label.AddImage.Width * scale), (float)(label.AddImage.Height * scale));
            if (hasImage) PlaceImage(ref place, imageWidth, imageHeight, screen.Value.X, size.Width, scale);
            return true;
        }

        // ------------------------------------------------------------ writing

        // Open the box for a comment's text: a new comment's, or one already there.
        void Write(RhinoView view, Comment comment)
        {
            writing = comment;
            writingView = view;
            aim = null;
            overComment = Guid.Empty;
            if (editor == null) editor = new CommentEditor { Finished = FinishWriting };
#if !MAC
            editor.PasteImage = PasteCommentImage; // Ctrl + V with a picture on the clipboard
#endif
#if MAC
            editor.Open(view.Document, comment.Text);
#else
            PlaceEditor(true);
#endif
            if (!editor.IsOpen)
            {
                // No box, so nothing can be typed: the comment is let go.
                writing = null;
                writingView = null;
            }
            view.Redraw();
        }

#if !MAC
        // Put the box over the label's text, which moves when the view does.
        void PlaceEditor(bool open)
        {
            if (writing == null || writingView?.Document == null) return;
            double scale = Toolbar.Scale(writingView);
            if (!LabelBox(writingView.ActiveViewport, writingView.Document, writing, scale, out System.Drawing.RectangleF pill, out ScreenPoint corner, out LabelPicture label))
            {
                if (!open) editor.Commit(); // its point has gone out of sight
                return;
            }
            var at = new ScreenPoint(corner.X + (int)Math.Round(label.TextLeft * scale), (int)Math.Round(pill.Y + 3 * scale));
            if (!open && at == editorAt) return;
            editorAt = at;
            ScreenPoint onScreen = writingView.ClientToScreen(at);
            if (open) editor.Open(onScreen, scale, WritingWidth, PillHeight - 6, writing.Text);
            else editor.MoveTo(onScreen, scale);
        }
#endif

        /// <summary>
        /// The text box has closed. Null means Esc: a new comment is dropped, an old one
        /// stays as it was. Text is kept; no text removes the comment, unless it has a
        /// picture, which is a comment in itself.
        /// </summary>
        internal void FinishWriting(string text)
        {
            Comment comment = writing;
            RhinoView view = writingView;
            writing = null;
            writingView = null;
            if (comment == null) return;
            RhinoDoc doc = view?.Document;
            if (doc != null)
            {
                text = text?.Trim();
                bool newImage = comment.NewImage != null; // pasted while the text was being typed
                if (comment.Id == Guid.Empty)
                {
                    if (!string.IsNullOrEmpty(text) || (text != null && newImage)) AddComment(doc, view, comment, text);
                }
                else if (text != null && (text != comment.Text || newImage))
                {
                    uint record = doc.BeginUndoRecord("Comment");
                    RhinoObject obj = doc.Objects.FindId(comment.Id);
                    if (obj != null && text.Length == 0 && comment.Image.Length == 0)
                    {
                        doc.Objects.Delete(obj, true, true);
                    }
                    else if (obj != null)
                    {
                        ObjectAttributes attributes = obj.Attributes.Duplicate();
                        attributes.SetUserString(CommentTextKey, text);
                        if (newImage) WriteImage(attributes, comment.Image, comment.ImageWidth, comment.ImageHeight, comment.NewImage);
                        doc.Objects.ModifyAttributes(obj, attributes, true);
                    }
                    doc.EndUndoRecord(record);
                }
                SceneStale();
                doc.Views.Redraw();
            }
            RhinoApp.SetFocusToMainWindow();
        }

        void AddComment(RhinoDoc doc, RhinoView view, Comment comment, string text)
        {
            // Its group's layer: a sublayer of "Comments", made here if it is not there yet.
            int layer = AnnotationGroups.LayerForNew(doc, true, out int group);
            var attributes = new ObjectAttributes();
            if (layer >= 0) attributes.LayerIndex = layer;
            attributes.Mode = ObjectMode.Hidden; // Rhino is to leave comments alone, as it does sketches
            attributes.Name = "Comment " + comment.Number;
            attributes.SetUserString(CommentKey, comment.Number.ToString(CultureInfo.InvariantCulture));
            attributes.SetUserString(AnnotationGroups.GroupKey, group.ToString(CultureInfo.InvariantCulture));
            attributes.SetUserString(CommentTextKey, text);
            attributes.SetUserString(CommentNormalKey, Number(comment.Normal.X) + "," + Number(comment.Normal.Y) + "," + Number(comment.Normal.Z));
            if (comment.Sketch != Guid.Empty) attributes.SetUserString(CommentOnKey, "sketch " + comment.Sketch);
            else if (comment.Object != Guid.Empty) attributes.SetUserString(CommentOnKey, "object " + comment.Object);
            if (comment.NewImage != null) WriteImage(attributes, comment.Image, comment.ImageWidth, comment.ImageHeight, comment.NewImage);
            RhinoViewport viewport = view.ActiveViewport;
            Point3d camera = viewport.CameraLocation, target = viewport.CameraTarget;
            attributes.SetUserString(CommentViewKey, string.Join(";", viewport.Name,
                Number(camera.X) + "," + Number(camera.Y) + "," + Number(camera.Z),
                Number(target.X) + "," + Number(target.Y) + "," + Number(target.Z)));

            uint record = doc.BeginUndoRecord("Comment");
            doc.Objects.AddPoint(comment.Point, attributes);
            doc.EndUndoRecord(record);
        }

        // The eraser passes over a view: comments whose label or point it touches go.
        bool EraseCommentsAt(RhinoView view, ScreenPoint point, double radius)
        {
            RhinoDoc doc = view.Document;
            double scale = Toolbar.Scale(view);
            bool erased = false;
            foreach (Comment comment in SceneOf(doc).Comments.ToArray())
            {
                if (!comment.Visible) continue;
                if (!LabelBox(view.ActiveViewport, doc, comment, scale, out System.Drawing.RectangleF pill, out _, out _)) continue;
                Point2d? at = ToScreen(view.ActiveViewport, comment.Point);
                bool touched = pill.Contains(point) || (at.HasValue && at.Value.DistanceTo(new Point2d(point.X, point.Y)) < radius);
                if (!touched) continue;
                RhinoObject obj = doc.Objects.FindId(comment.Id);
                if (obj == null) continue;
                BeginChange(doc);
                erased |= doc.Objects.Delete(obj, true, true);
            }
            if (erased) SceneStale();
            return erased;
        }

        // ----------------------------------------------------------- painting

        sealed class CommentMarks : DisplayConduit
        {
            readonly SketchController controller;

            public CommentMarks(SketchController controller) => this.controller = controller;

            // Over everything: a comment is read, not modelled with.
            protected override void DrawForeground(DrawEventArgs e)
            {
                try
                {
                    controller.PaintComments(e);
                }
                catch (Exception ex)
                {
                    Enabled = false;
                    RhinoApp.WriteLine("Crayon comments: " + ex.Message);
                }
            }
        }

        void PaintComments(DrawEventArgs e)
        {
            RhinoDoc doc = e.RhinoDoc;
            RhinoView view = e.Viewport.ParentView;
            if (doc == null || view == null) return;
#if MAC
            MacDisplay.Note(e); // how dense the display is where this view is being drawn
#endif
            double scale = Toolbar.Scale(view);

            bool writingHere = writing != null && writingView != null && writingView.ActiveViewportID == e.Viewport.Id && writingView.Document == doc;
            foreach (Comment comment in SceneOf(doc).Comments)
            {
                if (!comment.Visible) continue;
                Comment shown = writingHere && writing.Id == comment.Id ? writing : comment;
                if (PlaceLabel(e.Viewport, doc, shown, scale, out LabelPlace place))
                {
                    e.Display.DrawBitmap(place.Label.Bitmap, place.Corner.X, place.Corner.Y);
                    if (place.HasImage) PaintCommentImage(e, doc, shown, place, scale);
                }
            }
            if (writingHere)
            {
                // A new comment is not in the document until it has text.
                if (writing.Id == Guid.Empty && PlaceLabel(e.Viewport, doc, writing, scale, out LabelPlace place))
                {
                    e.Display.DrawBitmap(place.Label.Bitmap, place.Corner.X, place.Corner.Y);
                    if (place.HasImage) PaintCommentImage(e, doc, writing, place, scale);
                }
#if !MAC
                // The view may have moved under the in-place text box.
                Eto.Forms.Application.Instance.AsyncInvoke(() => PlaceEditor(false));
#endif
            }

            if (active && tool == SketchTool.Comment && aim != null && aim.View == view.RuntimeSerialNumber && view.ActiveViewportID == e.Viewport.Id)
            {
                LabelPicture ghost = GhostFor(aim.Sketch != Guid.Empty, aim.Chip, scale);
                e.Display.DrawBitmap(ghost.Bitmap, (int)Math.Round(aim.Screen.X - ghost.AnchorX * scale), (int)Math.Round(aim.Screen.Y - ghost.AnchorY * scale));
            }
        }

        LabelPicture Cached(string key, Func<LabelPicture> make)
        {
            if (labels.TryGetValue(key, out LabelPicture picture)) return picture;
            if (labels.Count > 400)
            {
                foreach (LabelPicture old in labels.Values) old.Bitmap.Dispose();
                labels.Clear();
            }
            return labels[key] = make();
        }

        // A comment's label. With a link, the dark tag above it says what the comment is
        // on. While it is being written, the text is left out: the text box sits there.
        // addImage puts the picture button at the end of the pill; imageRoom is the height
        // left clear above the pill for the comment's picture, which is painted separately.
        LabelPicture LabelFor(Comment comment, string[] link, bool beingWritten, double scale, bool addImage = false, double imageRoom = 0)
        {
            string key = string.Join("\n", comment.Number, beingWritten ? "" : comment.Text, link == null ? "" : link[0] + " " + link[1], beingWritten, scale, Theme.Dark, addImage, imageRoom);
            return Cached(key, () =>
            {
                string shown = comment.Text.Length > 0 ? comment.Text : "Comment";
                Media.FormattedText text = Picture.Text(shown, 11.5, Wpf.FontWeights.SemiBold, Picture.Ink, MaxTextWidth);
                Media.FormattedText number = Picture.Text(comment.Number.ToString(CultureInfo.InvariantCulture), 10.5, Wpf.FontWeights.Bold, White);
                double textWidth = beingWritten ? WritingWidth : Math.Ceiling(text.WidthIncludingTrailingWhitespace);
                // The picture button lengthens the pill to the right only: the words stay where they were.
                double plainWidth = 3 + DiscSize + PillGap + textWidth + PillEnd;
                double pillWidth = plainWidth + (addImage ? AddImageRoom : 0);

                Media.FormattedText kind = null, name = null;
                double tagWidth = 0;
                if (link != null)
                {
                    kind = Picture.Text(link[0], 10.5, Wpf.FontWeights.Medium, White);
                    name = Picture.Text(link[1], 10.5, Wpf.FontWeights.SemiBold, White, 220);
                    tagWidth = 7 + kind.WidthIncludingTrailingWhitespace + 5 + name.WidthIncludingTrailingWhitespace + 8;
                }
                double above = (link != null ? TagHeight + TagGap : 0) + imageRoom;
                double width = Math.Ceiling(Math.Max(plainWidth + 2 * (pillWidth - plainWidth), tagWidth)) + 2 * PictureMargin;
                double pillTop = PictureMargin + above, pillLeft = (width - plainWidth) / 2;
                double height = pillTop + PillHeight + LabelLift + 5 + PictureMargin;
                var pill = new Wpf.Rect(pillLeft, pillTop, pillWidth, PillHeight);
                var anchor = new Wpf.Point(width / 2, pillTop + PillHeight + LabelLift);
                var add = addImage ? new Wpf.Rect(pill.Right - PillEnd - AddImageRoom + 4, pillTop + 2, AddImageRoom, PillHeight - 4) : new Wpf.Rect(0, 0, 0, 0);

                System.Drawing.Bitmap bitmap = Picture.Render(width, height, scale, context =>
                {
                    // The point itself, then the pill above it.
                    context.DrawEllipse(Picture.Fill(White), null, anchor, 4.5, 4.5);
                    context.DrawEllipse(Picture.Fill(Picture.Accent), null, anchor, 3, 3);
                    Picture.Card(context, pill, PillHeight / 2);
                    var disc = new Wpf.Point(pillLeft + 3 + DiscSize / 2, pillTop + PillHeight / 2);
                    context.DrawEllipse(Picture.Fill(Picture.Accent), null, disc, DiscSize / 2, DiscSize / 2);
                    context.DrawText(number, new Wpf.Point(disc.X - number.Width / 2, disc.Y - number.Height / 2));
                    if (!beingWritten) context.DrawText(text, new Wpf.Point(pillLeft + 3 + DiscSize + PillGap, pillTop + (PillHeight - text.Height) / 2));
                    if (addImage) PaintAddImage(context, add);

                    if (link != null)
                    {
                        var tag = new Wpf.Rect((width - tagWidth) / 2, PictureMargin, tagWidth, TagHeight);
                        context.DrawRoundedRectangle(Picture.Fill(TagDark), null, tag, 8, 8);
                        context.PushOpacity(0.55);
                        context.DrawText(kind, new Wpf.Point(tag.X + 7, tag.Y + (TagHeight - kind.Height) / 2));
                        context.Pop();
                        context.DrawText(name, new Wpf.Point(tag.X + 7 + kind.WidthIncludingTrailingWhitespace + 5, tag.Y + (TagHeight - name.Height) / 2));
                    }
                });
                using (bitmap)
                    return new LabelPicture { Bitmap = new DisplayBitmap(bitmap), AnchorX = anchor.X, AnchorY = anchor.Y, Pill = pill, TextLeft = pillLeft + 3 + DiscSize + PillGap, AddImage = add };
            });
        }

        // Where a comment would land: a ring, filled once it has snapped onto a sketch
        // line, and beside it a dark chip naming the sketch or the layer.
        LabelPicture GhostFor(bool snapped, string chip, double scale)
        {
            return Cached(string.Join("\n", "ghost", snapped, chip, scale), () =>
            {
                Media.FormattedText words = chip.Length > 0 ? Picture.Text(chip, 10.5, Wpf.FontWeights.Medium, White, 260) : null;
                double chipWidth = words != null ? Math.Ceiling(words.WidthIncludingTrailingWhitespace) + 14 : 0;
                var centre = new Wpf.Point(PictureMargin + 10, PictureMargin + 10);
                double width = centre.X + 12 + chipWidth + PictureMargin, height = centre.Y + 10 + 18 + PictureMargin;
                System.Drawing.Bitmap bitmap = Picture.Render(width, height, scale, context =>
                {
                    if (snapped)
                    {
                        context.DrawEllipse(Picture.Fill(Picture.Accent), null, centre, 10, 10);
                        context.DrawEllipse(Picture.Fill(White), null, centre, 8.5, 8.5);
                        context.DrawEllipse(Picture.Fill(Picture.Accent), null, centre, 6, 6);
                    }
                    else
                    {
                        context.DrawEllipse(Picture.Fill(Picture.Accent), null, centre, 8, 8);
                        context.DrawEllipse(Picture.Fill(White), null, centre, 6, 6);
                    }
                    if (words != null)
                    {
                        var box = new Wpf.Rect(centre.X + 12, centre.Y + 10, chipWidth, 18);
                        context.DrawRoundedRectangle(Picture.Fill(ChipDark), null, box, 6, 6);
                        context.DrawText(words, new Wpf.Point(box.X + 7, box.Y + (18 - words.Height) / 2));
                    }
                });
                using (bitmap)
                    return new LabelPicture { Bitmap = new DisplayBitmap(bitmap), AnchorX = centre.X, AnchorY = centre.Y };
            });
        }
    }

#if MAC
    /// <summary>
    /// Where a comment's text is typed on a Mac: a small dialog. Return (or Save) keeps the
    /// text, Esc (or Cancel) gives up, and saving no text removes the comment.
    ///
    /// On Windows the text is typed in place, in a bare box floating over the label (the
    /// class below). Putting that box in the right spot takes the viewport's place on
    /// screen in the window system's own units, which nobody has been able to try on a Mac.
    /// A dialog needs none of it, and takes the same calls.
    /// </summary>
    internal sealed class CommentEditor
    {
        Eto.Forms.Dialog<string> dialog;
        Eto.Forms.TextBox box;
        string pendingText;
        int session;

        /// <summary>Called once when the dialog closes, with the text: null if it was given up.</summary>
        public Action<string> Finished;

        public bool IsOpen => pendingText != null;

        public void Open(RhinoDoc doc, string text)
        {
            Commit();
            pendingText = text ?? "";
            int current = ++session;
            // Once the click that asked for it has been dealt with: a dialog must not open in the
            // middle of Rhino's mouse handling.
            Eto.Forms.Application.Instance.AsyncInvoke(() => Ask(doc, current));
        }

        void Ask(RhinoDoc doc, int current)
        {
            if (!IsOpen || session != current) return;
            string result = null;
            try
            {
                var input = box = new Eto.Forms.TextBox { Text = pendingText, PlaceholderText = "Add a comment", Width = 320 };
                var save = new Eto.Forms.Button { Text = "Save" };
                var cancel = new Eto.Forms.Button { Text = "Cancel" };
                using var asking = dialog = new Eto.Forms.Dialog<string> { Title = "Comment", Padding = 16, Resizable = false };
                save.Click += (s, e) => asking.Close(input.Text ?? "");
                cancel.Click += (s, e) => asking.Close(null);
                asking.DefaultButton = save;
                asking.AbortButton = cancel;
                asking.Content = new Eto.Forms.StackLayout
                {
                    Spacing = 12,
                    Items = { input, new Eto.Forms.StackLayout { Orientation = Eto.Forms.Orientation.Horizontal, Spacing = 8, Items = { cancel, save } } },
                };
                asking.Shown += (s, e) => input.Focus();
                result = asking.ShowModal(Rhino.UI.RhinoEtoApp.MainWindowForDocument(doc));
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("Crayon comments: " + ex.Message);
            }
            Finish(current, result);
        }

        /// <summary>Close, keeping what has been typed.</summary>
        public void Commit()
        {
            if (IsOpen) Close(box?.Text ?? pendingText);
        }

        /// <summary>Close as Esc does.</summary>
        public void Cancel()
        {
            if (IsOpen) Close(null);
        }

        /// <summary>For tests: type into the box.</summary>
        internal void Type(string text)
        {
            if (box != null) box.Text = text;
            else if (IsOpen) pendingText = text ?? "";
        }

        void Close(string text)
        {
            int current = session;
            try
            {
                dialog?.Close(text);
            }
            finally { Finish(current, text); }
        }

        void Finish(int current, string text)
        {
            if (!IsOpen || session != current) return;
            pendingText = null;
            dialog = null;
            box = null;
            Finished?.Invoke(text);
        }
    }
#else
    /// <summary>
    /// The box a comment's text is typed in: a bare text field floating over the comment's
    /// label in the viewport. Return keeps the text, Esc gives up, and clicking anywhere
    /// else keeps it too.
    /// </summary>
    internal sealed class CommentEditor
    {
        Eto.Forms.Form form;
        Eto.Forms.TextBox box;
        bool done = true;

        /// <summary>Called once when the box closes, with the text: null if Esc was pressed.</summary>
        public Action<string> Finished;

        /// <summary>
        /// Ctrl + V was pressed. True if there was a picture on the clipboard and it has been
        /// taken for the comment; false leaves the paste to the box, which takes text.
        /// </summary>
        public Func<bool> PasteImage;

        public bool IsOpen => !done;

        /// <summary>Open at a place on screen (in pixels), sized in pixels at 100 % display scaling.</summary>
        public void Open(ScreenPoint screen, double scale, double width, double height, string text)
        {
            Commit();
            box = new Eto.Forms.TextBox
            {
                Text = text ?? "",
                PlaceholderText = "Add a comment, then Return",
                ShowBorder = false,
                BackgroundColor = Eto.Drawing.Colors.White,
                TextColor = Eto.Drawing.Color.FromArgb(0x15, 0x17, 0x1c),
                Font = new Eto.Drawing.Font("Segoe UI", 8.6f),
            };
            form = new Eto.Forms.Form
            {
                WindowStyle = Eto.Forms.WindowStyle.None,
                ShowInTaskbar = false,
                Resizable = false,
                Minimizable = false,
                Maximizable = false,
                BackgroundColor = Eto.Drawing.Colors.White,
                Padding = 0,
                Content = box,
                ClientSize = new Eto.Drawing.Size((int)width, (int)height),
                Owner = Rhino.UI.RhinoEtoApp.MainWindow,
            };
            done = false;
            // An earlier box can still report losing the focus after this one has opened:
            // each box only ever speaks for itself.
            Eto.Forms.TextBox mine = box;
            box.KeyDown += (s, e) =>
            {
                if (box != mine) return;
                if (e.Key == Eto.Forms.Keys.Enter)
                {
                    e.Handled = true;
                    Commit();
                }
                else if (e.Key == Eto.Forms.Keys.Escape)
                {
                    e.Handled = true;
                    Cancel();
                }
                else if (e.Key == Eto.Forms.Keys.V && e.Control && !e.Alt && !e.Shift && PasteImage != null && PasteImage())
                {
                    e.Handled = true; // a picture: there is no text to put in the box
                }
            };
            box.LostFocus += (s, e) => { if (box == mine) Commit(); };
            MoveTo(screen, scale);
            form.Show();
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }

        public void MoveTo(ScreenPoint screen, double scale)
        {
            if (form != null && !done) form.Location = new Eto.Drawing.Point((int)Math.Round(screen.X / scale), (int)Math.Round(screen.Y / scale));
        }

        /// <summary>Close, keeping what has been typed.</summary>
        public void Commit() => Finish(box?.Text ?? "");

        /// <summary>Close as Esc does.</summary>
        public void Cancel() => Finish(null);

        /// <summary>For tests: type into the box.</summary>
        internal void Type(string text)
        {
            if (!done) box.Text = text;
        }

        void Finish(string text)
        {
            if (done) return;
            done = true; // closing the box makes it lose the focus, which comes back here
            Eto.Forms.Form closing = form;
            form = null;
            try
            {
                closing?.Close();
            }
            catch (Exception)
            {
                // Already closing.
            }
            Finished?.Invoke(text);
        }
    }
#endif
}
