using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace RhinoSketchingTool
{
    /// <summary>
    /// The Agent tab: a chat with Claude Code. It shows who is signed in and offers
    /// Claude Code's own sign-in, lists the chat as it is written, asks the user when
    /// Claude wants to use a tool, and has the model and permission mode to choose.
    /// </summary>
    internal sealed class ChatView : Panel
    {
        static readonly Color Muted = Color.FromArgb(135, 141, 153);
        static readonly Color Danger = Color.FromArgb(229, 72, 77);
        static readonly Color Bubble = new Color(0.5f, 0.5f, 0.5f, 0.22f);

        // Model names to offer before Claude Code has sent its own list.
        static readonly string[][] FallbackModels =
        {
            new[] { "default", "Default (recommended)" }, new[] { "opus", "Opus" }, new[] { "sonnet", "Sonnet" }, new[] { "haiku", "Haiku" },
        };

        const int Gutter = 4;      // around the tab's contents
        const int ScrollBar = 22;  // the room the list's scroll bar takes
        const int Indent = 24;     // the user's own messages start this far in

        IChatSession session => Agent.Session;

        // Sidebars are narrow: the top row is one line of words and two small buttons.
        readonly Label status = new Label { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.None, ToolTip = "Claude Code" };
        readonly Button newChat = new Button { Text = "New", ToolTip = "Start a new chat", MinimumSize = new Size(44, 0) };
        readonly Button more = new Button { Text = "⋯", ToolTip = "Account, permission mode and settings", MinimumSize = new Size(30, 0) };

        readonly StackLayout list = new StackLayout { Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Padding(0, 4) };
        readonly Scrollable scroll;
        readonly Label activity = new Label { TextColor = Muted };

        readonly DropDown models = new DropDown { ToolTip = "Which model answers" };
        readonly DropDown effort = new DropDown { ToolTip = "Reasoning effort for the next prompt" };
        readonly TextArea input = new TextArea { Wrap = true, AcceptsReturn = true, AcceptsTab = false, Height = 62, ToolTip = "Return sends. Shift + Return starts a new line." };
        readonly Button send = new Button { Text = "Send", MinimumSize = new Size(56, 0) };
        readonly DropDown providers = new DropDown { ToolTip = "Choose your assistant" };
        readonly Button attach = new Button { Text = "+", ToolTip = "Add files", MinimumSize = new Size(30, 0) };
        readonly Button attachments = new Button { Text = "No files", ToolTip = "Remove an attached file" };
        readonly Label mcpMessage = new Label { Wrap = WrapMode.Word };
        readonly Button mcpInstall = new Button { Text = "Open Package Manager" };
        readonly Button mcpRetry = new Button { Text = "Retry RhinoMCP" };
        readonly StackLayout mcpNotice = new StackLayout { Spacing = 4 };

        readonly UITimer timer = new UITimer { Interval = 0.1 };

        // What is on screen for each entry of the chat, and the text it was made from.
        sealed class Row
        {
            public Control Control;
            public string Shown;
            public Action<ChatEntry> Update;
        }

        readonly Dictionary<int, Row> rows = new Dictionary<int, Row>();
        readonly List<Label> wrapped = new List<Label>(); // labels that have to be told how wide they may be
        Transcript listed;      // the chat the rows are of
        string emptyShown;      // what the empty chat's welcome was made from
        bool syncing, warmed;
        int tick;

        public ChatView()
        {
            RhinoMcpConnection.CheckInstalled();
            mcpNotice.Items.Add(mcpMessage);
            mcpNotice.Items.Add(mcpInstall);
            mcpNotice.Items.Add(mcpRetry);
            mcpInstall.Click += (s, e) => RhinoMcpConnection.OpenPackageManager();
            mcpRetry.Click += (s, e) => { Agent.RetryConnection(); Refresh(); };
            providers.Items.Add("Choose your assistant", "");
            providers.Items.Add("Claude Code", "claude");
            providers.Items.Add("Codex", "codex");
            providers.SelectedKey = Agent.ProviderChosen ? Agent.Provider : "";
            providers.SelectedIndexChanged += (s, e) =>
            {
                if (syncing || string.IsNullOrEmpty(providers.SelectedKey)) return;
                Agent.Choose(providers.SelectedKey);
                Refresh();
            };
            scroll = new Scrollable { Border = BorderType.None, ExpandContentWidth = true, ExpandContentHeight = false, Content = list };

            var header = new TableLayout
            {
                Spacing = new Size(6, 0),
                Rows = { new TableRow(new TableCell(status, true), new TableCell(newChat), new TableCell(more)) },
            };
            var sending = new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(new TableCell(models, true), new TableCell(effort), new TableCell(send)) } };

            Content = new TableLayout
            {
                Padding = Gutter,
                Spacing = new Size(0, 6),
                Rows =
                {
                    new TableRow(providers),
                    new TableRow(mcpNotice),
                    new TableRow(header),
                    new TableRow(scroll) { ScaleHeight = true },
                    new TableRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(attach, new TableCell(attachments, true)) } }),
                    new TableRow(input),
                    new TableRow(sending),
                },
            };

            newChat.Click += (s, e) => { session.Reset(); Agent.Attachments.Clear(); session.Warm(); Refresh(); };
            attach.Click += (s, e) => { Agent.Attach(this); Refresh(); };
            attachments.Click += (s, e) =>
            {
                var menu = new ContextMenu();
                foreach (ChatAttachment file in Agent.Attachments.ToArray())
                    menu.Items.Add(Item("Remove " + file.Name, () => Agent.Attachments.Remove(file)));
                if (menu.Items.Count > 0) menu.Show(attachments);
            };
            more.Click += (s, e) => ShowMenu();
            send.Click += (s, e) => SendOrStop();
            input.KeyDown += (s, e) =>
            {
                if (e.Key != Keys.Enter || e.Shift) return;
                e.Handled = true; // Return sends: only Shift + Return starts a new line
                if (session.Status == "idle") SendOrStop();
            };
            models.SelectedIndexChanged += (s, e) =>
            {
                if (syncing || models.SelectedKey == null || session.Status != "idle") return;
                Agent.Model = models.SelectedKey == "default" ? "" : models.SelectedKey;
                session.SetModel(models.SelectedKey);
                Agent.ValidateEffort();
                Refresh();
            };
            effort.SelectedIndexChanged += (s, e) => { if (!syncing && effort.SelectedKey != null) Agent.SetEffort(effort.SelectedKey); };

            // Esc in the message box is for the message, not for putting the pen down.
            KeyHook.TextEntry.Add(() => input.HasFocus);

            SizeChanged += (s, e) => Fit();
            timer.Elapsed += (s, e) => Tick();
            timer.Start();
            Refresh();
        }

        // Ten times a second: take in what Claude Code has said, and show it.
        void Tick()
        {
            try
            {
                Agent.CheckDocument();
                mcpMessage.Text = RhinoMcpConnection.Message;
                mcpNotice.Visible = mcpMessage.Text.Length > 0;
                mcpInstall.Visible = RhinoMcpConnection.Missing;
                mcpRetry.Enabled = session.Status == "idle";
                // Claude Code is started the first time the tab is looked at. That calls no
                // model: it only says who is signed in and which models there are.
                if (Agent.ProviderChosen && !warmed && Loaded && Visible && Width > 0)
                {
                    warmed = true;
                    Agent.Open();
                }
                bool changed = session.Poll();
                bool busy = session.Status != "idle" || session.SigningIn;
                tick++;
                if (changed || busy || listed != session.Transcript) Refresh();
            }
            catch (Exception ex)
            {
                timer.Stop(); // a fault here must not come back ten times a second
                RhinoApp.WriteLine("Crayon agent: " + ex.Message);
            }
        }

        void SendOrStop()
        {
            if (session.Status != "idle")
            {
                session.Interrupt();
            }
            else
            {
                string text = input.Text ?? "";
                if (text.Trim().Length == 0 && Agent.Attachments.Count == 0) return;
                if (Agent.Send(text)) input.Text = "";
            }
            Refresh();
            ScrollToEnd();
        }

        // ------------------------------------------------------------ menu

        void ShowMenu()
        {
            var menu = new ContextMenu();
            Account account = session.Account;
            if (account.Email.Length > 0)
            {
                menu.Items.Add(new ButtonMenuItem { Text = account.Email, Enabled = false });
                if (account.Plan.Length > 0) menu.Items.Add(new ButtonMenuItem { Text = account.Plan, Enabled = false });
                menu.Items.Add(Item("Sign out", () => session.SignOut()));
            }
            else
            {
                menu.Items.Add(Item(session.SigningIn ? "Signing in…" : "Sign in to " + (Agent.Provider == "codex" ? "ChatGPT" : "Claude") + "...", () => session.SignIn()));
            }
            // How Claude asks before acting: the one in force is ticked.
            menu.Items.Add(new SeparatorMenuItem());
            string current = CurrentMode();
            foreach (string[] mode in Agent.Modes)
            {
                string key = mode[0];
                menu.Items.Add(Item((key == current ? "✓ " : "    ") + mode[1], () => { Agent.Mode = key; session.SetMode(key); }));
            }
            menu.Items.Add(new SeparatorMenuItem());
            menu.Items.Add(Item("Locate " + Agent.Label + "...", Locate));
            menu.Show(more);
        }

        ButtonMenuItem Item(string text, Action action)
        {
            var item = new ButtonMenuItem { Text = text };
            item.Click += (s, e) => { action(); Refresh(); };
            return item;
        }

        void Locate() => Agent.Locate(this);

        // ------------------------------------------------------------ show

        void Refresh()
        {
            Agent.ValidateEffort();
            syncing = true;
            try
            {
                string state = session.Status;
                providers.SelectedKey = Agent.ProviderChosen ? Agent.Provider : "";
                status.ToolTip = Agent.Label;
                attach.Enabled = Agent.ProviderChosen && state == "idle";
                attachments.Text = Agent.Attachments.Count == 0 ? "No files" : string.Join(", ", Agent.Attachments.Select(file => file.Name));
                attachments.Enabled = Agent.Attachments.Count > 0;
                input.Enabled = models.Enabled = newChat.Enabled = more.Enabled = Agent.ProviderChosen;
                models.Enabled = Agent.ProviderChosen && state == "idle";
                effort.Enabled = models.Enabled && Agent.Efforts.Length > 1;
                status.Text = (state == "starting" ? "Starting…"
                    : state == "working" ? "Working…"
                    : state == "waiting" ? "Needs approval"
                    : state == "stopping" ? "Stopping…"
                    : session.MissingCli ? "Not found"
                    : session.Account.SignedIn == false ? "Signed out"
                    : "Ready");
                send.Text = state == "idle" ? "Send" : "Stop";
                send.Enabled = Agent.ProviderChosen && state != "stopping";

                ShowChoices();
                ShowList(state);
            }
            finally
            {
                syncing = false;
            }
        }

        void ShowChoices()
        {
            var levels = Agent.Efforts;
            if (!levels.Select(level => level[0]).SequenceEqual(effort.Items.Select(item => item.Key)))
            {
                effort.Items.Clear();
                foreach (string[] level in levels) effort.Items.Add("Effort: " + level[1], level[0]);
            }
            effort.SelectedKey = Agent.Effort.Length > 0 ? Agent.Effort : "default";
            // The models Claude Code offers this account, once it has said.
            var offered = session.Models.Count > 0
                ? session.Models.Select(model => new[] { model.Value, model.Label }).ToList()
                : (Agent.Provider == "codex" ? new[] { new[] { "default", "Default" } } : FallbackModels).ToList();
            if (models.Items.Count != offered.Count || !offered.Select(model => model[0]).SequenceEqual(models.Items.Select(item => item.Key)))
            {
                models.Items.Clear();
                foreach (string[] model in offered) models.Items.Add(model[1], model[0]);
            }
            string chosen = Agent.Model.Length > 0 ? Agent.Model : "default";
            if (models.SelectedKey != chosen) models.SelectedKey = offered.Any(model => model[0] == chosen) ? chosen : "default";
        }

        string CurrentMode() => session.Mode.Length > 0 ? session.Mode : (Agent.Mode.Length > 0 ? Agent.Mode : "default");

        void ShowList(string state)
        {
            Transcript transcript = session.Transcript;
            bool added = false;
            if (transcript != listed)
            {
                // A new chat: start the list again.
                listed = transcript;
                Clear();
            }

            if (transcript.Entries.Count == 0)
            {
                ShowWelcome();
                return;
            }
            if (emptyShown != null)
            {
                emptyShown = null;
                Clear();
            }

            if (activity.Parent != null) list.Items.RemoveAt(list.Items.Count - 1); // it goes back on at the end
            foreach (ChatEntry entry in transcript.Entries)
            {
                string shown = Signature(entry);
                if (!rows.TryGetValue(entry.Id, out Row row))
                {
                    rows[entry.Id] = row = Make(entry);
                    list.Items.Add(new StackLayoutItem(row.Control, HorizontalAlignment.Stretch));
                    row.Shown = shown;
                    added = true;
                }
                else if (row.Shown != shown)
                {
                    row.Update(entry);
                    row.Shown = shown;
                    added = true;
                }
            }

            // A line that says something is going on, while there is nothing of Claude's to see yet.
            ChatEntry last = transcript.Entries[transcript.Entries.Count - 1];
            bool writing = last is AssistantText text && text.Streaming;
            if ((state == "starting" || state == "working") && !writing)
            {
                string doing = state == "starting" ? "Starting " + Agent.Label : transcript.Thinking ? "Thinking" : "Working";
                activity.Text = doing + new string('.', 1 + tick / 3 % 3);
                list.Items.Add(new StackLayoutItem(activity, HorizontalAlignment.Stretch));
            }

            if (added)
            {
                Fit();
                ScrollToEnd();
            }
        }

        void Clear()
        {
            list.Items.Clear();
            rows.Clear();
            wrapped.Clear();
        }

        // An empty chat says what to do first: find Claude Code, sign in, or just start.
        void ShowWelcome()
        {
            if (!Agent.ProviderChosen)
            {
                if (emptyShown == "choose") return;
                emptyShown = "choose";
                Clear();
                list.Items.Add(Wrapped("Choose Claude Code or Codex to start a conversation alongside your sketches.", null));
                return;
            }
            Account account = session.Account;
            string kind = session.MissingCli ? "missing"
                : account.SignedIn == false ? (session.SigningIn ? "signing" : "signedout")
                : "ready:" + account.Email + account.Plan;
            if (emptyShown == kind) return;
            emptyShown = kind;
            Clear();

            if (session.MissingCli)
            {
                list.Items.Add(Wrapped(Agent.Label + " was not found on this computer.", Muted));
                list.Items.Add(new StackLayoutItem(ActionButton("Locate " + Agent.Label + "...", Locate), HorizontalAlignment.Left));
            }
            else if (account.SignedIn == false)
            {
                list.Items.Add(Wrapped("Sign in to " + (Agent.Provider == "codex" ? "ChatGPT" : "Claude") + "...", Muted));
                Button signIn = ActionButton(session.SigningIn ? "Signing in…" : "Sign in to " + (Agent.Provider == "codex" ? "ChatGPT" : "Claude") + "...", () => session.SignIn());
                signIn.Enabled = !session.SigningIn;
                list.Items.Add(new StackLayoutItem(signIn, HorizontalAlignment.Left));
            }
            else
            {
                list.Items.Add(Wrapped("What are we working on?", null));
                if (account.Email.Length > 0)
                    list.Items.Add(Wrapped("Signed in as " + account.Email + (account.Plan.Length > 0 ? " · " + account.Plan : ""), Muted));
            }
            Fit();
        }

        Button ActionButton(string text, Action action)
        {
            var button = new Button { Text = text };
            button.Click += (s, e) => { action(); Refresh(); };
            return button;
        }

        // Everything about an entry that shows: when this changes, its row is brought up to date.
        static string Signature(ChatEntry entry)
        {
            switch (entry)
            {
                case UserMessage user: return user.Text;
                case AssistantText text: return text.Text;
                case ToolCall call: return call.Name + "\n" + call.Status + "\n" + Transcript.ToolSummary(call.Input);
                case Approval approval: return approval.Status;
                case Notice notice: return notice.Text;
                default: return "";
            }
        }

        Row Make(ChatEntry entry)
        {
            switch (entry)
            {
                case UserMessage user:
                {
                    // The user's own words: in a soft box, set in from the left.
                    Label label = Wrap(user.Text, null, Indent + 16);
                    var box = new Panel { BackgroundColor = Bubble, Padding = new Padding(8, 5), Content = label };
                    return new Row { Control = new Panel { Padding = new Padding(Indent, 0, 0, 0), Content = box }, Update = e => label.Text = ((UserMessage)e).Text };
                }
                case AssistantText text:
                {
                    Label label = Wrap(text.Text, null, 0);
                    return new Row { Control = label, Update = e => label.Text = ((AssistantText)e).Text };
                }
                case ToolCall call:
                {
                    Label label = Wrap(ToolLine(call), Muted, 0);
                    return new Row { Control = label, Update = e => label.Text = ToolLine((ToolCall)e) };
                }
                case Approval approval:
                    return MakeApproval(approval);
                default:
                {
                    var notice = (Notice)entry;
                    Label label = Wrap(notice.Text, notice.Error ? Danger : Muted, 0);
                    return new Row { Control = label, Update = e => label.Text = ((Notice)e).Text };
                }
            }
        }

        static string ToolLine(ToolCall call)
        {
            string mark = call.Status == "running" ? "…" : call.Status == "done" ? "✓" : call.Status == "error" ? "✕" : "–";
            string summary = Transcript.ToolSummary(call.Input);
            return mark + " " + Transcript.ToolLabel(call.Name) + (summary.Length > 0 ? "  " + summary : "");
        }

        // Claude asks before it uses a tool: what it wants to do, and three answers.
        Row MakeApproval(Approval approval)
        {
            string name = Transcript.ToolLabel(approval.ToolName);
            Label question = Wrap("Allow " + name + "?", null, 16);
            question.Font = SystemFonts.Bold();
            string detail = approval.Description.Length > 0 ? approval.Description : Transcript.ToolSummary(approval.Input, 400);
            Label what = Wrap(detail, Muted, 16);
            var answered = new Label { TextColor = Muted, Visible = false };

            var buttons = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (string[] choice in new[] { new[] { "Allow", "allow", "Allow this once" }, new[] { "For chat", "always", "Allow " + name + " for the rest of this chat" }, new[] { "Deny", "deny", "Do not allow it" } })
            {
                string decision = choice[1];
                var button = new Button { Text = choice[0], ToolTip = choice[2] };
                button.Click += (s, e) => { session.Answer(approval.RequestId, decision); Refresh(); };
                buttons.Items.Add(button);
            }

            var box = new Panel
            {
                BackgroundColor = Bubble,
                Padding = new Padding(8, 6),
                Content = new StackLayout { Spacing = 5, HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { question, what, buttons, answered } },
            };
            return new Row
            {
                Control = box,
                Update = e =>
                {
                    string state = ((Approval)e).Status;
                    buttons.Visible = state == "pending";
                    answered.Visible = state != "pending";
                    answered.Text = state == "allowed" ? "Allowed" : state == "denied" ? "Denied" : "No longer needed";
                },
            };
        }

        // ---------------------------------------------------------- layout

        StackLayoutItem Wrapped(string text, Color? colour) => new StackLayoutItem(Wrap(text, colour, 0), HorizontalAlignment.Stretch);

        // A label whose text wraps. It only does if it is told how wide it may be: the
        // list's width, less whatever stands beside the label.
        Label Wrap(string text, Color? colour, int beside)
        {
            var label = new Label { Text = text, Wrap = WrapMode.Word, Tag = beside };
            if (colour != null) label.TextColor = colour.Value;
            wrapped.Add(label);
            return label;
        }

        void Fit()
        {
            int width = Math.Max(120, Width - 2 * Gutter - ScrollBar);
            foreach (Label label in wrapped) label.Width = Math.Max(60, width - (int)label.Tag);
            activity.Width = width;
        }

        void ScrollToEnd()
        {
            // Once the new rows have been laid out.
            Application.Instance.AsyncInvoke(() => scroll.ScrollPosition = new Point(0, 1000000));
        }
    }
}
