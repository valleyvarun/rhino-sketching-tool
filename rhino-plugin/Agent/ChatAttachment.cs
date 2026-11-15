using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    /// <summary>Only native-picker paths become attachments; web content cannot nominate arbitrary files.</summary>
    internal sealed class ChatAttachment
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public long Size { get; }
        public string MediaType { get; }
        public bool IsImage => MediaType != null;

        public ChatAttachment(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            var file = new FileInfo(Path);
            if (!file.Exists) throw new IOException("This file no longer exists.");
            Size = file.Length;
            string extension = file.Extension.ToLowerInvariant();
            MediaType = extension == ".png" ? "image/png" : extension == ".jpg" || extension == ".jpeg" ? "image/jpeg"
                : extension == ".gif" ? "image/gif" : extension == ".webp" ? "image/webp" : null;
            if (IsImage && Size > 5 * 1024 * 1024) throw new IOException("Images must be 5 MB or smaller.");
        }

        public void Validate()
        {
            var current = new FileInfo(Path);
            if (!current.Exists) throw new IOException(Name + " no longer exists. Remove it or attach it again.");
            if (IsImage && current.Length > 5 * 1024 * 1024) throw new IOException(Name + " is now larger than 5 MB.");
        }

        public JsonMap ClaudeImage() => new JsonMap
        {
            ["type"] = "image",
            ["source"] = new JsonMap { ["type"] = "base64", ["media_type"] = MediaType, ["data"] = Convert.ToBase64String(File.ReadAllBytes(Path)) },
        };

        public static string Prompt(string text, IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0) return text;
            string files = string.Join("\n", attachments.Select(file => "- " + file.Path));
            return (string.IsNullOrWhiteSpace(text) ? "Please review the attached files." : text.Trim()) +
                "\n\nFiles attached by the user (use your file-reading tools for non-image files):\n" + files;
        }

        public static string Display(string text, IReadOnlyList<ChatAttachment> attachments) => text.Trim() +
            (attachments == null || attachments.Count == 0 ? "" : "\n\nAttached: " + string.Join(", ", attachments.Select(file => file.Name)));
    }
}
