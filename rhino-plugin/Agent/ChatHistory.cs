using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    /// <summary>
    /// A conversation kept in the Agent tab's History: the entries as the page showed them,
    /// and the provider's own id (Claude Code's session id, Codex's thread id) to carry on with it.
    /// </summary>
    internal sealed class SavedChat
    {
        public string Id = "", Provider = "claude", SessionId = "", Title = "";
        public long Updated;
        public List<JsonMap> Entries = new List<JsonMap>();
    }

    /// <summary>
    /// History on disk: one JSON file per conversation in %APPDATA%\Crayon\history.
    /// Nothing here needs Rhino, so the chat-protocol tests use it as it is.
    /// </summary>
    internal static class ChatHistory
    {
        /// <summary>Where conversations are kept. Tests point it at their own folder.</summary>
        public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crayon", "history");

        static List<JsonMap> listed; // the list, until something is saved

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static string NewId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

        // Ids name files: letters, digits and dashes only.
        static bool Valid(string id) => !string.IsNullOrEmpty(id) && Regex.IsMatch(id, "^[A-Za-z0-9-]{1,64}$");

        /// <summary>The first thing the user said, on one line and trimmed: what History calls the chat.</summary>
        public static string Title(IEnumerable<JsonMap> entries)
        {
            JsonMap first = entries.FirstOrDefault(entry => Json.Text(entry.At("kind")) == "user");
            string text = first == null ? "" : Json.Text(first.At("text"));
            int attached = text.IndexOf("\n\nAttached: ", StringComparison.Ordinal);
            if (attached >= 0) text = text.Substring(0, attached);
            text = string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0) text = "Conversation";
            return text.Length <= 80 ? text : text.Substring(0, 79).TrimEnd() + "…";
        }

        public static void Save(SavedChat chat)
        {
            if (chat == null || !Valid(chat.Id)) return;
            try
            {
                Directory.CreateDirectory(Folder);
                var map = new JsonMap
                {
                    ["id"] = chat.Id, ["provider"] = chat.Provider, ["sessionId"] = chat.SessionId ?? "",
                    ["title"] = chat.Title, ["updated"] = chat.Updated, ["entries"] = chat.Entries.Cast<object>().ToList(),
                };
                string path = Path.Combine(Folder, chat.Id + ".json"), temp = path + ".tmp";
                File.WriteAllText(temp, Json.Write(map));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception)
            {
                // A full disk or a locked file must not break the chat: History just misses this save.
            }
            listed = null;
        }

        public static SavedChat Load(string id)
        {
            if (!Valid(id)) return null;
            string path = Path.Combine(Folder, id + ".json");
            try
            {
                if (!File.Exists(path)) return null;
                JsonMap map = Json.Map(Json.Parse(File.ReadAllText(path)));
                if (Json.Text(map.At("id")) != id) return null;
                return new SavedChat
                {
                    Id = id,
                    Provider = Json.Text(map.At("provider")) == "codex" ? "codex" : "claude",
                    SessionId = Json.Text(map.At("sessionId")),
                    Title = Json.Text(map.At("title")),
                    Updated = map.At("updated") is double updated ? (long)updated : 0,
                    Entries = Json.List(map.At("entries")).Select(Json.Map).Where(entry => entry.Count > 0).ToList(),
                };
            }
            catch (Exception) { return null; }
        }

        /// <summary>Every kept conversation as {id, title, updated}, newest first.</summary>
        public static List<object> List()
        {
            if (listed == null)
            {
                listed = new List<JsonMap>();
                try
                {
                    if (Directory.Exists(Folder))
                        foreach (string path in Directory.GetFiles(Folder, "*.json"))
                        {
                            SavedChat chat = Load(Path.GetFileNameWithoutExtension(path));
                            if (chat == null) continue;
                            listed.Add(new JsonMap { ["id"] = chat.Id, ["title"] = chat.Title, ["updated"] = chat.Updated });
                        }
                }
                catch (Exception) { }
                listed = listed.OrderByDescending(item => item["updated"] is long time ? time : 0).ToList();
            }
            return listed.Cast<object>().ToList();
        }
    }
}
