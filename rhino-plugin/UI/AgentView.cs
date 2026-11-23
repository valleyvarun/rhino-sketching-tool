using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Eto.Forms;
using Rhino;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    /// <summary>
    /// The Agent tab: the chat with Claude Code, shown as a web page (agent.html) made to
    /// look like Claude Code's own window. This class only carries things between the
    /// page and the chat (ChatSession): ten times a second it takes in what Claude Code
    /// has said, hands the page whatever changed, and collects what the user did there.
    ///
    /// If Rhino has no web view to give (the Edge WebView2 runtime is missing), the
    /// plain chat (ChatView) is shown in its place.
    /// </summary>
    internal sealed class AgentView : Panel
    {
        IChatSession session => Agent.Session;
        readonly UITimer timer = new UITimer { Interval = 0.1 };
        readonly WebView web;

        bool ready;          // the page has loaded and can be talked to
        bool dirty = true;   // something has changed that the page has not been told
        bool? lastDark;
        int contextTick;
        int[] context = { 0, 0 }; // sketch groups, comment groups the next prompt carries
        string lastMcpMessage;
        bool warmed;
        bool taking;         // the page is being asked what the user did
        int takingSince;

        // What the page has been told: the chat it was told about, each entry as it was
        // sent, and everything else.
        Transcript told;
        readonly Dictionary<int, string> toldEntries = new Dictionary<int, string>();
        string toldRest;
        int toldEpoch;

        // ------------------------------------------------------ History
        // The conversation on screen, per provider: the live transcript, and, when it was
        // opened from History, the saved entries shown before it (ids -1, -2, ...). Saved
        // to %APPDATA%\Crayon\history on New chat, on opening another, and after each turn.
        sealed class Conversation
        {
            public string Id = "";          // its History id, once saved
            public string SessionId = "";   // the provider's own id, as last seen
            public Transcript Transcript;   // the live part
            public readonly List<JsonMap> Replayed = new List<JsonMap>();
            public readonly Dictionary<int, List<object>> Chips = new Dictionary<int, List<object>>(); // user entry id -> what went with it
            public readonly Dictionary<int, long> Times = new Dictionary<int, long>();                 // entry id -> when it was first seen
            public int SavedCount = -1;     // entries in the last save, so an unchanged chat is not saved again
        }
#if MAC
        // Rhino for Mac makes a panel, and so one of these views, for every open document
        // (Windows has the one). They all show the same chat, so they share what is known of
        // it: the conversations, when the one on screen was swapped, and that there is news.
        static readonly Dictionary<string, Conversation> conversations = new Dictionary<string, Conversation>();
        static bool historyOpen;
        static int epoch;        // goes up when the conversation on screen is swapped
        static int heard;        // goes up whenever any of the views has taken in something new
        int toldHeard;           // ... and how far this one has got
        string lastStatus = "idle";

        /// <summary>Start or stop asking the page for news: a view whose document has closed is not coming back.</summary>
        public void Listen(bool on)
        {
            if (Content is ChatView fallback) { fallback.Listen(on); return; }
            if (on && !timer.Started) timer.Start();
            else if (!on && timer.Started) timer.Stop();
        }
#else
        readonly Dictionary<string, Conversation> conversations = new Dictionary<string, Conversation>();
        static bool historyOpen;
        int epoch;               // goes up when the conversation on screen is swapped
        string lastStatus = "idle";
#endif

        /// <summary>The History list is showing (the header's History button is pressed).</summary>
        public static bool HistoryOpen => historyOpen;

        public AgentView()
        {
            RhinoMcpConnection.CheckInstalled();
            try
            {
#if MAC
                web = new WebView();
#else
                web = new WebView { BrowserContextMenuEnabled = false };
#endif
                web.DocumentLoaded += (s, e) =>
                {
#if MAC
                    // Eto's WebKit handler changes the context menu by scripting document.body.
                    web.BrowserContextMenuEnabled = false;
#endif
                    // A page just loaded knows nothing: it is told everything again.
                    ready = true;
                    told = null;
                    dirty = true;
                };
                // The page is the chat and nothing else: anything it would browse to opens in the browser.
                web.DocumentLoading += (s, e) =>
                {
                    if (e.Uri == null || (e.Uri.Scheme != "http" && e.Uri.Scheme != "https")) return;
                    e.Cancel = true;
                    OpenLink(e.Uri.ToString());
                };
                web.OpenNewWindow += (s, e) =>
                {
                    e.Cancel = true;
                    if (e.Uri != null) OpenLink(e.Uri.ToString());
                };
                Content = web;
                web.LoadHtml(Page());
#if MAC
                SketchPanel.TrackTextFocus(this, () => Loaded && Visible && web.HasFocus);
#else
                KeyHook.TextEntry.Add(() => Loaded && Visible && web.HasFocus);
                WatchPaste();
#endif
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Crayon agent: no web view (" + ex.Message + "), showing the plain chat.");
                web = null;
                Content = new ChatView();
                return;
            }
            timer.Elapsed += (s, e) => Tick();
            timer.Start();
        }

        static string Page()
        {
            using (Stream stream = typeof(AgentView).Assembly.GetManifestResourceStream("RhinoSketchingTool.agent.html"))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

#if !MAC
        // ---------------------------------------------------------- Ctrl + V
        // The web view is another program's window inside Rhino's. Before it acts on a
        // shortcut key (anything with Ctrl) it offers the key to its host, as a WPF key event
        // on the control, and if anything there takes it the page never hears of it: which is
        // what became of Ctrl + V in Rhino's panel. So the offer is taken here, always, and
        // the page is handed the clipboard instead: text into the message box, pictures and
        // copied files as attachments (see Agent.PasteClipboard).
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        int pastedAt;

        void WatchPaste()
        {
            try
            {
                // The control passes the offer on twice, as WPF does keys: on the way down (preview)
                // and on the way back up. Heard at both, on the control itself, which is before
                // anything above it on the way up, and (handledEventsToo) even if something has
                // already claimed the key.
                if (web.ControlObject is System.Windows.UIElement element)
                {
                    var handler = new System.Windows.Input.KeyEventHandler(OnWebKey);
                    element.AddHandler(System.Windows.UIElement.KeyDownEvent, handler, true);
                    element.AddHandler(System.Windows.UIElement.PreviewKeyDownEvent, handler, true);
                }
            }
            catch (Exception)
            {
                // Not the web view this was written for: pasting is left to it.
            }
        }

        void OnWebKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.V) return;
            // The keys as they are held right now: Ctrl alone (Ctrl + Alt is AltGr, which types a letter).
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0, alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0, shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if (!ctrl || alt || shift) return;
            e.Handled = true; // the page gets the clipboard from here: the browser's own paste would be a second one
            int now = Environment.TickCount;
            if (unchecked(now - pastedAt) < 150) return; // the key held down: once is enough
            pastedAt = now;
            Application.Instance.AsyncInvoke(Paste); // once the key is out of the way
        }

        void Paste()
        {
            if (!ready || !Agent.ProviderChosen) return;
            string text = Agent.PasteClipboard();
            if (!string.IsNullOrEmpty(text)) web.ExecuteScriptAsync("crayon.paste(" + Json.Write(text) + ");");
            dirty = true; // an attachment may have been added
        }
#endif

        void Tick()
        {
            Agent.CheckDocument();
            Conv(); // notices a chat the session replaced by itself, and keeps it in History
            string status = session.Status;
            if (status == "idle" && lastStatus != "idle") Save(Conv(), Agent.Provider); // a turn has ended
            lastStatus = status;
            if (told != session.Transcript || epoch != toldEpoch || lastMcpMessage != RhinoMcpConnection.Message)
            {
                lastMcpMessage = RhinoMcpConnection.Message;
                dirty = true;
            }
            bool appearance = Theme.Dark;
            if (lastDark != appearance) { lastDark = appearance; dirty = true; }
            // The groups the agent will get, for the counter in the chat header. Counted
            // from the same snapshot a prompt sends, about once a second while on show.
            if (Loaded && Visible && ++contextTick >= 10)
            {
                contextTick = 0;
                try
                {
                    int[] counts = DrawingContext.GroupCounts(RhinoDoc.ActiveDoc);
                    if (counts[0] != context[0] || counts[1] != context[1]) { context = counts; dirty = true; }
                }
                catch { /* no document: nothing to count */ }
            }
            try
            {
                // Claude Code is started the first time the tab is looked at. That calls no
                // model: it only says who is signed in and which models there are.
                if (Agent.ProviderChosen && !warmed && Loaded && Visible && Width > 0
#if MAC
                    && SketchPanel.IsCurrent(this)
#endif
                )
                {
                    warmed = true;
                    Agent.Open();
                }
#if MAC
                // Whichever view asks first is the one that hears it: it passes the word on to the rest.
                if (session.Poll()) heard++;
                if (toldHeard != heard) { toldHeard = heard; dirty = true; }
                if (!SketchPanel.IsCurrent(this)) return;
#else
                if (session.Poll()) dirty = true;
#endif
                if (!ready || !Loaded) return; // the page is not there, or not on show: it is told when it is
                Take();
                if (dirty) Tell();
            }
            catch (Exception ex)
            {
                timer.Stop(); // a fault here must not come back ten times a second
                RhinoApp.WriteLine("Crayon agent: " + ex.Message);
            }
        }

        // ------------------------------------------------------ to the page

        void Tell()
        {
            Agent.ValidateEffort();
            dirty = false;
            Conversation conv = Conv();
            Transcript transcript = session.Transcript;
            var state = new JsonMap();
            if (transcript != told || epoch != toldEpoch)
            {
                told = transcript;
                toldEpoch = epoch;
                toldEntries.Clear();
                toldRest = null;
                state["reset"] = true;
            }

            // Only the entries that are new, or no longer as the page has them. A chat
            // opened from History shows its saved entries first.
            var changed = new List<object>();
            var ids = new List<object>();
            foreach (JsonMap shown in ShownEntries(conv))
            {
                int id = Convert.ToInt32(shown["id"]);
                ids.Add(id);
                string text = Json.Write(shown);
                if (toldEntries.TryGetValue(id, out string before) && before == text) continue;
                toldEntries[id] = text;
                changed.Add(shown);
            }

            Account account = session.Account;
            string folder = Agent.Folder();
            var rest = new JsonMap
            {
                ["provider"] = Agent.Provider,
                ["mcpMessage"] = RhinoMcpConnection.Message,
                ["mcpMissing"] = RhinoMcpConnection.Missing,
                ["dark"] = Theme.Dark,
                ["contextSketchGroups"] = context[0],
                ["contextCommentGroups"] = context[1],
                ["providerChosen"] = Agent.ProviderChosen,
                ["providerLabel"] = Agent.Label,
                // An image goes with a small picture of itself: the page shows that instead of its name.
                ["attachments"] = Agent.Attachments.Select(file => (object)new JsonMap { ["id"] = file.Id, ["name"] = file.Name, ["size"] = file.Size, ["image"] = file.IsImage, ["thumb"] = ChatImages.Thumb(file) ?? "" }).ToList(),
                ["status"] = session.Status,
                ["thinking"] = transcript.Thinking,
                ["missing"] = session.MissingCli,
                ["signedIn"] = account.SignedIn,
                ["signingIn"] = session.SigningIn,
                ["email"] = account.Email,
                ["plan"] = account.Plan,
                ["version"] = session.Version,
                ["model"] = session.Model,
                ["chosen"] = Agent.Model.Length > 0 ? Agent.Model : "default",
                ["effort"] = Agent.Effort.Length > 0 ? Agent.Effort : "default",
                ["efforts"] = Agent.Efforts,
                ["models"] = session.Models.Select(model => (object)new JsonMap
                {
                    ["value"] = model.Value, ["label"] = model.Label, ["description"] = model.Description, ["resolved"] = model.Resolved,
                }).ToList(),
                ["mode"] = session.Mode.Length > 0 ? session.Mode : (Agent.Mode.Length > 0 ? Agent.Mode : "default"),
                ["modes"] = Agent.Modes.Select(mode => (object)new List<object> { mode[0], mode[1] }).ToList(),
                ["folder"] = Path.GetFileName(folder.TrimEnd('\\', '/')),
                ["folderPath"] = folder,
                ["ids"] = ids,
                ["history"] = ChatHistory.List(),
                ["current"] = conv.Id,
                ["historyOpen"] = historyOpen,
            };
            string restText = Json.Write(rest);
            if (changed.Count == 0 && restText == toldRest && !state.ContainsKey("reset")) return;
            toldRest = restText;
            foreach (KeyValuePair<string, object> member in rest) state[member.Key] = member.Value;
            state["entries"] = changed;
            web.ExecuteScriptAsync("crayon.state(" + Json.Write(state) + ");");
        }

        // An entry of the chat as the page is given it.
        static JsonMap AsShown(ChatEntry entry)
        {
            var shown = new JsonMap { ["id"] = entry.Id };
            switch (entry)
            {
                case UserMessage user:
                    shown["kind"] = "user";
                    shown["text"] = user.Text;
                    break;
                case AssistantText text:
                    shown["kind"] = "text";
                    shown["text"] = text.Text;
                    shown["streaming"] = text.Streaming;
                    break;
                case ToolCall call:
                    shown["kind"] = "tool";
                    shown["name"] = Transcript.ToolLabel(call.Name);
                    shown["summary"] = Transcript.ToolSummary(call.Input, 200);
                    shown["status"] = call.Status;
                    shown["result"] = call.Status == "error" ? call.Result : "";
                    break;
                case Approval approval:
                    shown["kind"] = "ask";
                    shown["name"] = Transcript.ToolLabel(approval.ToolName);
                    shown["text"] = approval.Description.Length > 0 ? approval.Description : Transcript.ToolSummary(approval.Input, 1200);
                    shown["status"] = approval.Status;
                    shown["request"] = approval.RequestId;
                    break;
                case Notice notice:
                    shown["kind"] = "notice";
                    shown["text"] = notice.Text;
                    shown["error"] = notice.Error;
                    break;
            }
            return shown;
        }

        // ---------------------------------------------------- History

        // The conversation for the provider in use. If the session has started a new
        // chat by itself (another document, a sign-out), the old one is kept in History first.
        Conversation Conv()
        {
            string provider = Agent.Provider;
            if (!conversations.TryGetValue(provider, out Conversation conv))
                conversations[provider] = conv = new Conversation { Transcript = session.Transcript };
            if (conv.Transcript != session.Transcript)
            {
                Save(conv, provider);
                conversations[provider] = conv = new Conversation { Transcript = session.Transcript };
                epoch++;
                dirty = true;
            }
            string id = session.ConversationId ?? "";
            if (id.Length > 0) conv.SessionId = id;
            return conv;
        }

        // Every entry of the conversation as the page is given it, saved ones first.
        List<JsonMap> ShownEntries(Conversation conv)
        {
            var list = new List<JsonMap>(conv.Replayed);
            foreach (ChatEntry entry in conv.Transcript.Entries) list.Add(AsShown(entry, conv));
            return list;
        }

        // As AsShown, plus when it was said and, for the user's messages, what went with them.
        static JsonMap AsShown(ChatEntry entry, Conversation conv)
        {
            JsonMap shown = AsShown(entry);
            if (entry is UserMessage || entry is AssistantText)
            {
                if (!conv.Times.TryGetValue(entry.Id, out long time)) conv.Times[entry.Id] = time = ChatHistory.Now;
                shown["time"] = time;
            }
            if (entry is UserMessage user)
            {
                List<object> chips = conv.Chips.TryGetValue(entry.Id, out List<object> sent) ? sent : new List<object>();
                shown["chips"] = chips;
                // Attached files show as chips, so the "Attached: ..." line is not repeated under them.
                int attached = user.Text.IndexOf("\n\nAttached: ", StringComparison.Ordinal);
                if (attached >= 0 && chips.Any(chip => Json.Text(Json.Map(chip).At("kind")) == "file")) shown["text"] = user.Text.Substring(0, attached);
            }
            return shown;
        }

        // Keep the conversation in History, if it has anything of the user's and has changed.
        void Save(Conversation conv, string provider)
        {
            if (conv?.Transcript == null) return;
            var entries = ShownEntries(conv).Select(Settled).ToList();
            if (!entries.Any(entry => Json.Text(entry.At("kind")) == "user")) return;
            if (conv.Id.Length > 0 && entries.Count == conv.SavedCount) return;
            if (conv.Id.Length == 0) conv.Id = ChatHistory.NewId();
            ChatHistory.Save(new SavedChat
            {
                Id = conv.Id, Provider = provider, SessionId = conv.SessionId,
                Title = ChatHistory.Title(entries), Updated = ChatHistory.Now, Entries = entries,
            });
            conv.SavedCount = entries.Count;
            dirty = true;
#if MAC
            heard++; // History has changed for every view
#endif
        }

        // An entry as it is kept: nothing still running or waiting, and no id (ids are given on opening).
        static JsonMap Settled(JsonMap shown)
        {
            var copy = new JsonMap(shown);
            copy.Remove("id");
            if (copy.ContainsKey("streaming")) copy["streaming"] = false;
            string kind = Json.Text(copy.At("kind")), status = Json.Text(copy.At("status"));
            if (kind == "tool" && status == "running") copy["status"] = "stopped";
            if (kind == "ask" && status == "pending") copy["status"] = "cancelled";
            return copy;
        }

        // The sketch and comment groups the next message carries (as Layers names them),
        // and the files attached to it: the chips over the user's message.
        static List<object> SendChips()
        {
            var chips = new List<object>();
            try
            {
                RhinoDoc doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                {
                    DrawingContextSnapshot snapshot = DrawingContext.Capture(doc);
                    AnnotationGroupState groups = AnnotationGroups.Read(doc);
                    foreach (int id in snapshot.SketchGroups)
                        chips.Add(new JsonMap { ["kind"] = "sketch", ["label"] = groups.Sketches.FirstOrDefault(g => g.Id == id)?.Name ?? "Group " + id });
                    foreach (int id in snapshot.CommentGroups)
                        chips.Add(new JsonMap { ["kind"] = "comment", ["label"] = groups.Comments.FirstOrDefault(g => g.Id == id)?.Name ?? "Group " + id });
                    // The pictures on those comments go along as files too.
                    foreach (SketchController.CommentImageRef image in snapshot.Images)
                        chips.Add(new JsonMap { ["kind"] = "file", ["label"] = image.Label });
                }
            }
            catch (Exception) { /* no context to show */ }
            foreach (ChatAttachment file in Agent.Attachments)
            {
                var chip = new JsonMap { ["kind"] = "file", ["label"] = file.Name };
                string thumb = ChatImages.Thumb(file);
                if (thumb != null) chip["thumb"] = thumb; // an image shows as its thumbnail in the message too
                chips.Add(chip);
            }
            return chips;
        }

        /// <summary>New chat: keep this one in History, then start an empty one.</summary>
        public void NewChat()
        {
            if (session.Status != "idle") return;
            Save(Conv(), Agent.Provider);
            session.Reset();
            Agent.Attachments.Clear();
            session.Warm();
            conversations[Agent.Provider] = new Conversation { Transcript = session.Transcript };
            historyOpen = false;
            epoch++;
            dirty = true;
        }

        /// <summary>Show or hide the History list: "open", "close", or anything else to toggle.</summary>
        public void SetHistory(string value)
        {
            historyOpen = value == "open" || (value != "close" && !historyOpen);
#if MAC
            heard++;
#endif
            dirty = true;
        }

        /// <summary>
        /// Open a kept conversation: this one is kept first, the saved entries are shown
        /// again, and the next message continues the provider's own conversation.
        /// </summary>
        public void OpenChat(string id)
        {
            if (session.Status != "idle") return;
            SavedChat saved = ChatHistory.Load(id);
            if (saved == null) return;
            Save(Conv(), Agent.Provider);
            if (saved.Provider != Agent.Provider)
            {
                Agent.Choose(saved.Provider);
                Save(Conv(), Agent.Provider);
                if (session.Status != "idle") return;
            }
            session.Resume(saved.SessionId);
            Agent.Attachments.Clear();
            var conv = new Conversation { Id = saved.Id, SessionId = saved.SessionId, Transcript = session.Transcript };
            for (int i = 0; i < saved.Entries.Count; i++)
            {
                var entry = new JsonMap(saved.Entries[i]) { ["id"] = -(i + 1) };
                conv.Replayed.Add(entry);
            }
            conv.SavedCount = conv.Replayed.Count;
            conversations[Agent.Provider] = conv;
            historyOpen = false;
            epoch++;
            dirty = true;
            Agent.Open(); // starts the CLI on that conversation; no model is called
        }

        // ---------------------------------------------------- from the page

        // Ask the page what the user has done since the last time.
        async void Take()
        {
            int now = Environment.TickCount;
            if (taking && unchecked(now - takingSince) < 3000) return; // the last asking is still out
            taking = true;
            takingSince = now;
            try
            {
                string answer = await web.ExecuteScriptAsync("return crayon.take();");
                taking = false;
                // The answer may come wrapped in quotes. Inside, it is percent-encoded JSON.
                answer = (answer ?? "").Trim().Trim('"');
                if (answer.Length == 0 || answer == "null") return;
                foreach (object item in Json.List(Json.Parse(Uri.UnescapeDataString(answer))))
                {
#if MAC
                    if (!SketchPanel.IsCurrent(this))
                    {
                        if (Json.Text(Json.Map(item).At("type")) == "send" && Loaded)
                            await web.ExecuteScriptAsync("crayon.sent(false);");
                        continue;
                    }
#endif
                    Act(Json.Map(item));
                }
#if MAC
                heard++;
#endif
                dirty = true;
            }
            catch (Exception)
            {
                taking = false; // the page was not there to ask: next time
            }
        }

        void Act(JsonMap action)
        {
            string value = Json.Text(action.At("value"));
            switch (Json.Text(action.At("type")))
            {
                case "send":
                {
                    List<object> chips = SendChips();
                    bool ok = Agent.Send(Json.Text(action.At("text")));
                    if (ok)
                    {
                        UserMessage user = session.Transcript.Entries.OfType<UserMessage>().LastOrDefault();
                        if (user != null) Conv().Chips[user.Id] = chips;
                    }
                    web.ExecuteScriptAsync("crayon.sent(" + (ok ? "true" : "false") + ");");
                    break;
                }
                case "provider":
                    Agent.Choose(value);
                    told = null;
                    break;
                case "attach":
                    Agent.Attach(this);
                    break;
                case "mcpInstall":
                    RhinoMcpConnection.OpenPackageManager();
                    break;
                case "mcpRetry":
                    Agent.RetryConnection();
                    break;
                case "removeAttachment":
                    Agent.Attachments.RemoveAll(file => file.Id == value);
                    break;
                case "pasteImage":
                    // A picture pasted into the page itself (Shift + Insert, or a Mac's own paste).
                    Agent.AttachImageData(Json.Text(action.At("data")));
                    break;
                case "stop":
                    session.Interrupt();
                    break;
                case "new":
                    NewChat();
                    break;
                case "history":
                    SetHistory(value);
                    break;
                case "open":
                    OpenChat(value);
                    break;
                case "signin":
                    session.SignIn();
                    break;
                case "signout":
                    session.SignOut();
                    break;
                case "locate":
                    Agent.Locate(this);
                    break;
                case "model":
                    if (session.Status != "idle") break;
                    Agent.Model = value == "default" ? "" : value;
                    session.SetModel(value);
                    Agent.ValidateEffort();
                    break;
                case "effort":
                    Agent.SetEffort(value);
                    break;
                case "mode":
                    Agent.Mode = value;
                    session.SetMode(value);
                    break;
                case "answer":
                    session.Answer(Json.Text(action.At("request")), Json.Text(action.At("decision")));
                    break;
                case "link":
                    OpenLink(Json.Text(action.At("url")));
                    break;
            }
        }

        // A link in a reply opens in the user's browser, never in the panel. Web links only.
        static void OpenLink(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri link) || (link.Scheme != "http" && link.Scheme != "https")) return;
            try
            {
                Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No browser to open it with.
            }
        }
    }
}
