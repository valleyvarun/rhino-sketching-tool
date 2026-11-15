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
        string lastMcpMessage;
        bool warmed;
        bool taking;         // the page is being asked what the user did
        int takingSince;

        // What the page has been told: the chat it was told about, each entry as it was
        // sent, and everything else.
        Transcript told;
        readonly Dictionary<int, string> toldEntries = new Dictionary<int, string>();
        string toldRest;

        public AgentView()
        {
            RhinoMcpConnection.CheckInstalled();
            try
            {
                web = new WebView { BrowserContextMenuEnabled = false };
                web.DocumentLoaded += (s, e) =>
                {
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
                KeyHook.TextEntry.Add(() => Loaded && Visible && web.HasFocus);
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

        void Tick()
        {
            Agent.CheckDocument();
            if (told != session.Transcript || lastMcpMessage != RhinoMcpConnection.Message)
            {
                lastMcpMessage = RhinoMcpConnection.Message;
                dirty = true;
            }
            bool appearance = CrayonAppearance.Dark;
            if (lastDark != appearance) { lastDark = appearance; dirty = true; }
            try
            {
                // Claude Code is started the first time the tab is looked at. That calls no
                // model: it only says who is signed in and which models there are.
                if (Agent.ProviderChosen && !warmed && Loaded && Visible && Width > 0)
                {
                    warmed = true;
                    Agent.Open();
                }
                if (session.Poll()) dirty = true;
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
            Transcript transcript = session.Transcript;
            var state = new JsonMap();
            if (transcript != told)
            {
                told = transcript;
                toldEntries.Clear();
                toldRest = null;
                state["reset"] = true;
            }

            // Only the entries that are new, or no longer as the page has them.
            var changed = new List<object>();
            var ids = new List<object>();
            foreach (ChatEntry entry in transcript.Entries)
            {
                ids.Add(entry.Id);
                JsonMap shown = AsShown(entry);
                string text = Json.Write(shown);
                if (toldEntries.TryGetValue(entry.Id, out string before) && before == text) continue;
                toldEntries[entry.Id] = text;
                changed.Add(shown);
            }

            Account account = session.Account;
            string folder = Agent.Folder();
            var rest = new JsonMap
            {
                ["provider"] = Agent.Provider,
                ["mcpMessage"] = RhinoMcpConnection.Message,
                ["mcpMissing"] = RhinoMcpConnection.Missing,
                ["dark"] = CrayonAppearance.Dark,
                ["providerChosen"] = Agent.ProviderChosen,
                ["providerLabel"] = Agent.Label,
                ["attachments"] = Agent.Attachments.Select(file => (object)new JsonMap { ["id"] = file.Id, ["name"] = file.Name, ["size"] = file.Size, ["image"] = file.IsImage }).ToList(),
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
                foreach (object item in Json.List(Json.Parse(Uri.UnescapeDataString(answer)))) Act(Json.Map(item));
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
                    web.ExecuteScriptAsync("crayon.sent(" + (Agent.Send(Json.Text(action.At("text"))) ? "true" : "false") + ");");
                    break;
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
                case "stop":
                    session.Interrupt();
                    break;
                case "new":
                    session.Reset();
                    Agent.Attachments.Clear();
                    session.Warm();
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
