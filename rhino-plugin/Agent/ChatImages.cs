using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Pictures in the chat: the small thumbnail an attached image shows as, and pictures that
    /// arrive without a file of their own (pasted from the clipboard), which are given one in
    /// Crayon's own folder so that they attach like any other file. The page never names a
    /// path: it can only hand over a picture's bytes, and where they are kept is decided here.
    /// </summary>
    internal static class ChatImages
    {
        const int ThumbSide = 96;                // pixels: shown at about 44, still sharp on a dense display
        const long MaxBytes = 5 * 1024 * 1024;   // as ChatAttachment has it for images
        const int KeptDays = 14;

        static readonly Dictionary<string, string> thumbs = new Dictionary<string, string>(); // by attachment id; null for one that cannot be shown
        static bool cleared;

        /// <summary>An image attachment as a small picture for the page (a data URL). Null if this system cannot read it: it then shows by its name.</summary>
        public static string Thumb(ChatAttachment file)
        {
            if (file == null || !file.IsImage) return null;
            if (thumbs.TryGetValue(file.Id, out string made)) return made;
            string thumb = null;
            try
            {
                using (var image = new Eto.Drawing.Bitmap(file.Path))
                {
                    double fit = Math.Min(1, (double)ThumbSide / Math.Max(image.Width, image.Height));
                    int width = Math.Max(1, (int)Math.Round(image.Width * fit)), height = Math.Max(1, (int)Math.Round(image.Height * fit));
                    using (var small = new Eto.Drawing.Bitmap(image, width, height, Eto.Drawing.ImageInterpolation.High))
                        thumb = "data:image/png;base64," + Convert.ToBase64String(small.ToByteArray(Eto.Drawing.ImageFormat.Png));
                }
            }
            catch (Exception)
            {
                // A kind of picture Eto does not read here (WebP, say).
            }
            if (thumbs.Count > 200) thumbs.Clear();
            return thumbs[file.Id] = thumb;
        }

        /// <summary>A folder of Crayon's own, beside the one its chats are kept in. Made if it is not there.</summary>
        public static string Folder(string name)
        {
#if MAC
            string root = Path.Combine(MacProcess.ApplicationSupport, "Crayon");
#else
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crayon");
#endif
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>A picture from the clipboard, kept as a file: a PNG, made smaller (or a JPEG) if it would be over the limit for images.</summary>
        public static string Save(Eto.Drawing.Image image)
        {
            if (image == null || image.Width < 1 || image.Height < 1) throw new IOException("There is no picture on the clipboard.");
            byte[] bytes = null;
            string extension = ".png";
            double fit = Math.Min(1, 4000.0 / Math.Max(image.Width, image.Height));
            for (int attempt = 0; attempt < 5; attempt++, fit *= 0.7)
            {
                int width = Math.Max(1, (int)Math.Round(image.Width * fit)), height = Math.Max(1, (int)Math.Round(image.Height * fit));
                using (var sized = new Eto.Drawing.Bitmap(image, width, height, Eto.Drawing.ImageInterpolation.High))
                {
                    bytes = sized.ToByteArray(Eto.Drawing.ImageFormat.Png);
                    extension = ".png";
                    if (bytes.Length <= MaxBytes) break;
                    bytes = sized.ToByteArray(Eto.Drawing.ImageFormat.Jpeg);
                    extension = ".jpg";
                    if (bytes.Length <= MaxBytes) break;
                }
            }
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new IOException("That picture is too large to attach.");
            return Write(bytes, extension);
        }

        /// <summary>A picture the page was given (pasted there), kept as a file. Only what really is a picture is taken.</summary>
        public static string Save(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) throw new IOException("That is not a picture.");
            if (bytes.Length > MaxBytes) throw new IOException("Images must be 5 MB or smaller.");
            string extension =
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 ? ".png"
                : bytes[0] == 0xff && bytes[1] == 0xd8 ? ".jpg"
                : bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 ? ".gif"
                : bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50 ? ".webp"
                : null;
            if (extension == null) throw new IOException("That is not a picture Crayon can attach.");
            return Write(bytes, extension);
        }

        static string Write(byte[] bytes, string extension)
        {
            string folder = Folder("pasted");
            ClearOld(folder);
            string name = "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, name + extension);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, name + "-" + n + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // A pasted picture is only needed until its message has gone. Once a session, those older than two weeks are cleared away.
        static void ClearOld(string folder)
        {
            if (cleared) return;
            cleared = true;
            try
            {
                foreach (string old in Directory.GetFiles(folder, "pasted-*"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-KeptDays)) File.Delete(old);
            }
            catch (Exception)
            {
                // In use, or not ours to delete: left where it is.
            }
        }
    }
}
