using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
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
    /// <summary>
    /// A picture on a comment. It shows as a small thumbnail above the comment's label,
    /// with a button in its corner (two diagonal arrows) that makes it larger, and smaller
    /// again. That button answers whatever tool is in use, for as long as Crayon is on.
    ///
    /// Putting one on, with the Comment tool:
    ///   - point at a comment: a picture button appears at the end of its label, which
    ///     opens a file to choose;
    ///   - while a comment's text is being typed, Ctrl + V with a picture on the clipboard
    ///     (Windows).
    /// Taking it off: make it larger, then the x in its other corner (Comment tool).
    ///
    /// The picture is kept on the comment's own Rhino object, as user text beside the
    /// comment's words, so it is in the 3dm file, goes with the comment when it is copied,
    /// and comes back with it on Undo. It is made no larger than 1280 pixels first. A second,
    /// short entry says which picture it is and how big, so that listing the comments never
    /// has to read the pictures themselves.
    /// </summary>
    public sealed partial class SketchController
    {
        const string CommentImageKey = "SketchingTool.CommentImage";         // "<id>;<width>;<height>;<png or jpg>"
        const string CommentImageDataKey = "SketchingTool.CommentImageData"; // the picture file itself, as base64

        const double ThumbWidth = 76, ThumbHeight = 52;    // the picture fits in this while it is small
        const double LargeWidth = 360, LargeHeight = 270;  // and in this once it has been made larger
        const double ImagePad = 3, ImageGap = 6;           // its frame, and the space between frame and label
        const double ImageButton = 18;                     // the buttons in its corners
        const double AddImageRoom = 22;                    // what the picture button adds to a label's length
        const int ImageStoredSide = 1280;                  // the longer side of a picture as it is kept
        const int ImageStoredBytes = 700 * 1024;           // a PNG larger than this is kept as a JPEG if that is smaller

        // design/icons style: a 24 grid, round caps.
        const string LargerGlyph = "M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7 -7";
        const string SmallerGlyph = "M4 14h6v6M20 10h-6V4M14 10l7 -7M3 21l7 -7";
        const string RemoveGlyph = "M6 6l12 12M18 6L6 18";
        static readonly string[] PictureGlyph =
        {
            "M5.5 4.5h13a2 2 0 0 1 2 2v11a2 2 0 0 1 -2 2h-13a2 2 0 0 1 -2 -2v-11a2 2 0 0 1 2 -2z",
            "M3.9 16.6l4.6 -4.6 4 4 2.6 -2.6 5 5",
            "M8.3 9.3a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0z",
        };

        // A picture as it has been read, and as it has been handed to Rhino at the sizes asked for.
        sealed class CommentPicture
        {
            public Eto.Drawing.Bitmap Source;
            public readonly Dictionary<long, DisplayBitmap> Sized = new Dictionary<long, DisplayBitmap>(); // by width and height in pixels
        }

        // A comment's label as it lies in a viewport, in the viewport's pixels.
        struct LabelPlace
        {
            public LabelPicture Label;
            public ScreenPoint Corner;                  // where the label's picture goes
            public System.Drawing.RectangleF Pill;
            public System.Drawing.RectangleF AddImage;  // the picture button at the end of the pill: empty unless it shows
            public bool HasImage;
            public System.Drawing.Rectangle Card, Image; // the picture's frame, and the picture in it
            public System.Drawing.Rectangle Expand, Remove; // its buttons: larger or smaller, and take it off
        }

        readonly Dictionary<string, CommentPicture> pictures = new Dictionary<string, CommentPicture>(); // by picture id; null for one that cannot be read
        readonly HashSet<Guid> enlarged = new HashSet<Guid>(); // the comments whose picture is showing large (not kept in the file)
        readonly HashSet<string> unpaintable = new HashSet<string>(); // pictures (by id) that failed to paint: left out from then on

        // ------------------------------------------------------------- layout

        // How big a comment's picture shows, in pixels at 100 %: within the thumbnail's box,
        // or the large one, which gives way to what room there is in the viewport.
        static void ImageSize(Comment comment, bool large, double roomWidth, double roomHeight, out double width, out double height)
        {
            double maxWidth = large ? Math.Min(LargeWidth, Math.Max(ThumbWidth, roomWidth)) : ThumbWidth;
            double maxHeight = large ? Math.Min(LargeHeight, Math.Max(ThumbHeight, roomHeight)) : ThumbHeight;
            double fit = Math.Min(maxWidth / comment.ImageWidth, maxHeight / comment.ImageHeight);
            if (large) fit = Math.Min(fit, 2); // a small picture is not blown up beyond recognition
            width = Math.Max(ImageButton + 6, Math.Round(comment.ImageWidth * fit));
            height = Math.Max(ImageButton + 6, Math.Round(comment.ImageHeight * fit));
        }

        // The picture's frame sits above the pill, centred on the marked point but kept inside the viewport.
        static void PlaceImage(ref LabelPlace place, double width, double height, double anchorX, int viewportWidth, double scale)
        {
            int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
            int pad = (int)Math.Round(ImagePad * scale);
            int cardWidth = w + 2 * pad, cardHeight = h + 2 * pad;
            int bottom = (int)Math.Round(place.Pill.Top - ImageGap * scale);
            int left = (int)Math.Round(anchorX - cardWidth / 2.0);
            left = Math.Max(0, Math.Min(left, viewportWidth - cardWidth));
            place.HasImage = true;
            place.Card = new System.Drawing.Rectangle(left, bottom - cardHeight, cardWidth, cardHeight);
            place.Image = new System.Drawing.Rectangle(left + pad, bottom - cardHeight + pad, w, h);
            int button = (int)Math.Round(ImageButton * scale), inset = (int)Math.Round(3 * scale);
            place.Expand = new System.Drawing.Rectangle(place.Image.Right - button - inset, place.Image.Top + inset, button, button);
            place.Remove = new System.Drawing.Rectangle(place.Image.Left + inset, place.Image.Top + inset, button, button);
        }

        // ----------------------------------------------------------- painting

        // Nothing that goes wrong with a picture may take the comments' labels with it: the
        // conduit that paints them gives up for good on an error (CommentMarks).
        void PaintCommentImage(DrawEventArgs e, RhinoDoc doc, Comment comment, LabelPlace place, double scale)
        {
            if (unpaintable.Contains(comment.Image)) return;
            try
            {
                int margin = (int)Math.Round(PictureMargin * scale);
                e.Display.DrawBitmap(ImageCard(place.Card.Width, place.Card.Height, scale).Bitmap, place.Card.X - margin, place.Card.Y - margin);
                CommentPicture picture = PictureOf(doc, comment);
                if (picture != null) e.Display.DrawBitmap(Sized(picture, place.Image.Width, place.Image.Height), place.Image.X, place.Image.Y);
                // The buttons only answer while Crayon is on, so they only show then. A comment
                // that is not in the document yet has nothing to make larger.
                if (!active || comment.Id == Guid.Empty) return;
                bool large = enlarged.Contains(comment.Id);
                e.Display.DrawBitmap(ImageButtonPicture(large ? "smaller" : "larger", scale).Bitmap, place.Expand.X, place.Expand.Y);
                if (large && tool == SketchTool.Comment)
                    e.Display.DrawBitmap(ImageButtonPicture("remove", scale).Bitmap, place.Remove.X, place.Remove.Y);
            }
            catch (Exception ex)
            {
                unpaintable.Add(comment.Image); // said once, and not tried again on every frame
                RhinoApp.WriteLine("Crayon comments: a comment's picture could not be shown (" + ex.Message + ").");
            }
        }

        // The frame: a white card, as the label is.
        LabelPicture ImageCard(int width, int height, double scale)
        {
            return Cached(string.Join("\n", "imagecard", width, height, scale, Theme.Dark), () =>
            {
                double w = width / scale, h = height / scale;
                System.Drawing.Bitmap bitmap = Picture.Render(w + 2 * PictureMargin, h + 2 * PictureMargin, scale, context =>
                    Picture.Card(context, new Wpf.Rect(PictureMargin, PictureMargin, w, h), 7));
                using (bitmap) return new LabelPicture { Bitmap = new DisplayBitmap(bitmap) };
            });
        }

        // A button on the picture: a dark square with a white sign, to show on any picture.
        LabelPicture ImageButtonPicture(string kind, double scale)
        {
            return Cached(string.Join("\n", "imagebutton", kind, scale), () =>
            {
                System.Drawing.Bitmap bitmap = Picture.Render(ImageButton, ImageButton, scale, context =>
                {
                    context.DrawRoundedRectangle(Picture.Fill(Picture.TipBack, 0.8), null, new Wpf.Rect(0, 0, ImageButton, ImageButton), 5.5, 5.5);
                    string glyph = kind == "remove" ? RemoveGlyph : kind == "smaller" ? SmallerGlyph : LargerGlyph;
                    PaintGlyph(context, glyph, 4, 4, ImageButton - 8, White, 1.7);
                });
                using (bitmap) return new LabelPicture { Bitmap = new DisplayBitmap(bitmap) };
            });
        }

        // An icon drawn on a 24 grid, put down at the size and line width asked for.
        static void PaintGlyph(Media.DrawingContext context, string path, double left, double top, double size, Media.Color colour, double stroke)
        {
            var pen = new Media.Pen(Picture.Fill(colour), stroke * 24 / size)
            {
                StartLineCap = Media.PenLineCap.Round,
                EndLineCap = Media.PenLineCap.Round,
                LineJoin = Media.PenLineJoin.Round,
            };
            context.PushTransform(new Media.TranslateTransform(left, top));
            context.PushTransform(new Media.ScaleTransform(size / 24, size / 24));
            context.DrawGeometry(null, pen, Media.Geometry.Parse(path));
            context.Pop();
            context.Pop();
        }

        // The picture button at the end of a label that the pointer is on.
        static void PaintAddImage(Media.DrawingContext context, Wpf.Rect box)
        {
            const double size = 14;
            double left = box.X + (box.Width - size) / 2, top = box.Y + (box.Height - size) / 2;
            foreach (string part in PictureGlyph) PaintGlyph(context, part, left, top, size, Picture.Ink2, 1.5);
        }

        // ------------------------------------------------------- the pictures

        CommentPicture PictureOf(RhinoDoc doc, Comment comment)
        {
            if (pictures.TryGetValue(comment.Image, out CommentPicture picture)) return picture;
            picture = null;
            try
            {
                byte[] bytes = comment.NewImage ?? StoredImage(doc, comment.Id);
                if (bytes != null) picture = new CommentPicture { Source = new Eto.Drawing.Bitmap(bytes) };
            }
            catch (Exception)
            {
                // Not a picture this system can read.
            }
            if (pictures.Count > 80) ForgetPictures();
            return pictures[comment.Image] = picture;
        }

        void ForgetPictures()
        {
            foreach (CommentPicture old in pictures.Values)
            {
                if (old == null) continue;
                foreach (DisplayBitmap sized in old.Sized.Values) sized.Dispose();
                old.Source?.Dispose();
            }
            pictures.Clear();
        }

        static byte[] StoredImage(RhinoDoc doc, Guid commentId)
        {
            if (doc == null || commentId == Guid.Empty) return null;
            string data = doc.Objects.FindId(commentId)?.Attributes.GetUserString(CommentImageDataKey);
            return string.IsNullOrEmpty(data) ? null : Convert.FromBase64String(data);
        }

        // The picture at a size on screen, as Rhino draws it.
        static DisplayBitmap Sized(CommentPicture picture, int width, int height)
        {
            long key = ((long)width << 32) | (uint)height;
            if (picture.Sized.TryGetValue(key, out DisplayBitmap made)) return made;
            if (picture.Sized.Count >= 6)
            {
                foreach (DisplayBitmap old in picture.Sized.Values) old.Dispose();
                picture.Sized.Clear();
            }
            using (var scaled = new Eto.Drawing.Bitmap(picture.Source, width, height, Eto.Drawing.ImageInterpolation.High))
            using (System.Drawing.Bitmap bitmap = ToSystemBitmap(scaled))
                return picture.Sized[key] = new DisplayBitmap(bitmap);
        }

        static System.Drawing.Bitmap ToSystemBitmap(Eto.Drawing.Bitmap picture)
        {
#if MAC
            return Media.Canvas.ToSystem(picture);
#else
            // As a PNG, which says for itself how its pixels are kept. The stream is left
            // open: the bitmap may read from it for as long as it lives.
            return new System.Drawing.Bitmap(new MemoryStream(picture.ToByteArray(Eto.Drawing.ImageFormat.Png)));
#endif
        }

        /// <summary>
        /// A picture as it is kept on a comment: no larger than it need be, as a PNG, or as a
        /// JPEG where that is the smaller file.
        /// </summary>
        internal static bool PrepareImage(Eto.Drawing.Image image, out byte[] bytes, out int width, out int height)
        {
            bytes = null;
            width = height = 0;
            if (image == null || image.Width < 1 || image.Height < 1) return false;
            double fit = Math.Min(1, (double)ImageStoredSide / Math.Max(image.Width, image.Height));
            width = Math.Max(1, (int)Math.Round(image.Width * fit));
            height = Math.Max(1, (int)Math.Round(image.Height * fit));
            using (var kept = new Eto.Drawing.Bitmap(image, width, height, Eto.Drawing.ImageInterpolation.High))
            {
                bytes = kept.ToByteArray(Eto.Drawing.ImageFormat.Png);
                if (bytes.Length > ImageStoredBytes)
                {
                    byte[] smaller = kept.ToByteArray(Eto.Drawing.ImageFormat.Jpeg);
                    if (smaller != null && smaller.Length > 0 && smaller.Length < bytes.Length) bytes = smaller;
                }
            }
            return bytes != null && bytes.Length > 0;
        }

        static bool IsPng(byte[] bytes) => bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47;

        static void WriteImage(ObjectAttributes attributes, string id, int width, int height, byte[] bytes)
        {
            attributes.SetUserString(CommentImageKey, string.Join(";", id,
                width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture), IsPng(bytes) ? "png" : "jpg"));
            attributes.SetUserString(CommentImageDataKey, Convert.ToBase64String(bytes));
        }

        static string NewImageId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        // A picture for a comment whose text is being typed: it goes into the document with the text.
        void GiveImage(Comment comment, byte[] bytes, int width, int height)
        {
            comment.Image = NewImageId();
            comment.ImageWidth = width;
            comment.ImageHeight = height;
            comment.NewImage = bytes;
        }

        // Put a picture on a comment that is in the document, or (no bytes) take its picture off. One step for Undo.
        void StoreImage(RhinoDoc doc, Guid commentId, byte[] bytes, int width, int height)
        {
            RhinoObject obj = doc.Objects.FindId(commentId);
            if (obj == null) return;
            uint record = doc.BeginUndoRecord("Comment picture");
            try
            {
                ObjectAttributes attributes = obj.Attributes.Duplicate();
                if (bytes == null)
                {
                    attributes.SetUserString(CommentImageKey, null);
                    attributes.SetUserString(CommentImageDataKey, null);
                    enlarged.Remove(commentId);
                }
                else WriteImage(attributes, NewImageId(), width, height, bytes);
                doc.Objects.ModifyAttributes(obj, attributes, true);
            }
            finally { if (record != 0) doc.EndUndoRecord(record); }
            SceneStale();
            doc.Views.Redraw();
        }

        // ------------------------------------------------------------- adding

        static readonly string[] PictureFiles = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" };

        // The picture button on a comment's label: choose a file.
        void PickCommentImage(RhinoView view, Comment comment)
        {
            Guid id = comment.Id;
            RhinoDoc doc = view.Document;
            // Once the click has been dealt with: a dialog must not open in the middle of Rhino's mouse handling.
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                try
                {
                    var dialog = new Eto.Forms.OpenFileDialog { Title = "Add a picture to the comment" };
                    dialog.Filters.Add(new Eto.Forms.FileFilter("Pictures", PictureFiles));
                    Func<bool> textEntry = () => true; // Esc is the dialog's while it is open
                    KeyHook.TextEntry.Add(textEntry);
                    Eto.Forms.DialogResult result;
#if MAC
                    try { result = dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindowForDocument(doc)); }
#else
                    try { result = dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow); }
#endif
                    finally { KeyHook.TextEntry.Remove(textEntry); }
                    if (result != Eto.Forms.DialogResult.Ok || string.IsNullOrEmpty(dialog.FileName)) return;
                    using (var image = new Eto.Drawing.Bitmap(dialog.FileName))
                    {
                        if (PrepareImage(image, out byte[] bytes, out int width, out int height)) StoreImage(doc, id, bytes, width, height);
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("Crayon comments: that picture could not be added (" + ex.Message + ").");
                }
            });
        }

        // Ctrl + V in a comment's text box: a picture on the clipboard (or a picture file copied
        // in Explorer) goes onto the comment. False when there is none: text is the box's own to paste.
        bool PasteCommentImage()
        {
            if (writing == null) return false;
            try
            {
                Eto.Forms.Clipboard clipboard = Eto.Forms.Clipboard.Instance;
                Eto.Drawing.Image image = null;
                if (clipboard.ContainsUris)
                {
                    foreach (Uri uri in clipboard.Uris ?? new Uri[0])
                    {
                        if (!uri.IsFile || Array.IndexOf(PictureFiles, Path.GetExtension(uri.LocalPath).ToLowerInvariant()) < 0) continue;
                        image = new Eto.Drawing.Bitmap(uri.LocalPath);
                        break;
                    }
                }
                else if (!clipboard.ContainsText && clipboard.ContainsImage) image = clipboard.Image;
                if (image == null) return false;
                using (image)
                {
                    if (!PrepareImage(image, out byte[] bytes, out int width, out int height)) return false;
                    GiveImage(writing, bytes, width, height);
                }
                writingView?.Redraw();
                return true;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Crayon comments: that picture could not be pasted (" + ex.Message + ").");
                return false;
            }
        }

        // ------------------------------------------------------------- clicks

        // A click on a button of a comment's picture: larger or smaller with any tool, and
        // (Comment tool, picture large) take it off. True if the click was one of those.
        bool CommentImageClick(RhinoView view, ScreenPoint point)
        {
            RhinoDoc doc = view.Document;
            if (doc == null) return false;
            List<Comment> comments = SceneOf(doc).Comments;
            double scale = 0;
            for (int i = comments.Count - 1; i >= 0; i--) // the last painted is on top
            {
                Comment comment = comments[i];
                if (!comment.Visible || comment.Image.Length == 0) continue;
                if (scale == 0) scale = Toolbar.Scale(view);
                if (!PlaceLabel(view.ActiveViewport, doc, comment, scale, out LabelPlace place) || !place.HasImage) continue;
                if (place.Expand.Contains(point))
                {
                    EndDrag();
                    if (!enlarged.Remove(comment.Id)) enlarged.Add(comment.Id);
                    view.Redraw();
                    return true;
                }
                if (tool == SketchTool.Comment && enlarged.Contains(comment.Id) && place.Remove.Contains(point))
                {
                    EndDrag();
                    editor?.Commit(); // a comment still being written is kept as it stands
                    StoreImage(doc, comment.Id, null, 0, 0);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------ for the agent

        /// <summary>A comment's picture that goes to the agent with a prompt.</summary>
        internal sealed class CommentImageRef
        {
            public Guid ObjectId;
            public string ImageId, FileName, Label;
        }

        /// <summary>
        /// What is said of a comment's picture in the context a prompt carries: null when
        /// the comment has none. The file is only written when the prompt is sent.
        /// </summary>
        internal static CommentImageRef ImageRefOf(RhinoObject obj)
        {
            string[] parts = (obj.Attributes.GetUserString(CommentImageKey) ?? "").Split(';');
            if (parts.Length < 3 || parts[0].Length == 0) return null;
            string number = obj.Attributes.GetUserString(CommentKey) ?? "";
            return new CommentImageRef
            {
                ObjectId = obj.Id,
                ImageId = parts[0],
                FileName = "comment-" + (number.Length > 0 ? number : parts[0]) + (parts.Length > 3 && parts[3] == "jpg" ? ".jpg" : ".png"),
                Label = "Comment " + number + " picture",
            };
        }

        /// <summary>The picture as a file, for the assistants: written once, into Crayon's own folder.</summary>
        internal static string ExportImage(RhinoDoc doc, CommentImageRef image, string folder)
        {
            string data = doc.Objects.FindId(image.ObjectId)?.Attributes.GetUserString(CommentImageDataKey);
            if (string.IsNullOrEmpty(data)) return null;
            string home = Path.Combine(folder, image.ImageId);
            Directory.CreateDirectory(home);
            string path = Path.Combine(home, image.FileName);
            if (!File.Exists(path)) File.WriteAllBytes(path, Convert.FromBase64String(data));
            return path;
        }
    }
}
