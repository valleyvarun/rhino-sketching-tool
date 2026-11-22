using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    // ------------------------------------------------------------ transcript

    /// <summary>One thing in the chat. Ids stay the same for the life of the chat.</summary>
    internal abstract class ChatEntry
    {
        public int Id;
    }

    internal sealed class UserMessage : ChatEntry
    {
        public string Text;
    }

    internal sealed class AssistantText : ChatEntry
    {
        public string Text;
        public bool Streaming;
    }

    internal sealed class ToolCall : ChatEntry
    {
        public string ToolId, Name;
        public JsonMap Input = new JsonMap();
        public string Status = "running"; // running, done, error, stopped
        public string Result = "";
    }

    /// <summary>Claude Code asking whether it may use a tool. Answered from the chat.</summary>
    internal sealed class Approval : ChatEntry
    {
        public string RequestId, ToolName, Description;
        public JsonMap Input;
        public string Status = "pending"; // pending, allowed, denied, cancelled
    }

    internal sealed class Notice : ChatEntry
    {
        public string Text;
        public bool Error;
    }

    /// <summary>
    /// The chat, built from what Claude Code sends. While a reply is being written it
    /// sends the text in pieces ("stream_event"), then each finished block once more whole
    /// ("assistant"), and the results of tools as "user" events. Text is shown as it
    /// arrives and replaced by the whole block, matched by message id and order.
    /// </summary>
    internal sealed class Transcript
    {
        public readonly List<ChatEntry> Entries = new List<ChatEntry>();

        /// <summary>Claude is reasoning before its next visible block.</summary>
        public bool Thinking;
        internal bool McpConnectionFailed;

        int nextId = 1;
        string message = "";
        readonly Dictionary<string, AssistantText> blocks = new Dictionary<string, AssistantText>();      // "message id|index" -> the text being streamed
        readonly Dictionary<string, List<AssistantText>> streams = new Dictionary<string, List<AssistantText>>(); // message id -> its streamed texts, in order
        readonly Dictionary<string, int> finals = new Dictionary<string, int>();                           // message id -> whole text blocks seen so far
        readonly Dictionary<string, ToolCall> tools = new Dictionary<string, ToolCall>();

        internal T Add<T>(T entry) where T : ChatEntry
        {
            entry.Id = nextId++;
            Entries.Add(entry);
            return entry;
        }

        public void AddUser(string text) => Add(new UserMessage { Text = text });

        public void AddNotice(string text, bool error = false) => Add(new Notice { Text = text, Error = error });

        public void AddApproval(string requestId, string toolName, JsonMap input, string description) =>
            Add(new Approval { RequestId = requestId, ToolName = toolName, Input = input, Description = description });

        public Approval Approval(string requestId) =>
            Entries.OfType<Approval>().LastOrDefault(entry => entry.RequestId == requestId);

        public bool Pending => Entries.OfType<Approval>().Any(entry => entry.Status == "pending");

        /// <summary>Take in one event. Kinds this does not know are ignored.</summary>
        public void Apply(JsonMap e)
        {
            if (e.At("parent_tool_use_id") != null) return; // what a subagent does stays inside its tool call
            switch (Json.Text(e.At("type")))
            {
                case "stream_event": Stream(Json.Map(e.At("event")), Json.Text(e.At("api_message_id"))); break;
                case "assistant": Assistant(Json.Map(e.At("message"))); break;
                case "user": Results(Json.Map(e.At("message"))); break;
            }
        }

        void Stream(JsonMap e, string messageId)
        {
            string kind = Json.Text(e.At("type"));
            if (kind == "message_start")
            {
                message = Json.Text(Json.Map(e.At("message")).At("id"));
                return;
            }
            string id = messageId.Length > 0 ? messageId : message;
            string key = id + "|" + (e.At("index") is double index ? (int)index : -1);
            switch (kind)
            {
                case "content_block_start":
                {
                    JsonMap block = Json.Map(e.At("content_block"));
                    switch (Json.Text(block.At("type")))
                    {
                        case "text":
                            AssistantText text = Add(new AssistantText { Text = Json.Text(block.At("text")), Streaming = true });
                            blocks[key] = text;
                            if (!streams.TryGetValue(id, out List<AssistantText> list)) streams[id] = list = new List<AssistantText>();
                            list.Add(text);
                            Thinking = false;
                            break;
                        case "thinking":
                            Thinking = true;
                            break;
                        case "tool_use":
                            Tool(Json.Text(block.At("id")), Json.Text(block.At("name")));
                            Thinking = false;
                            break;
                    }
                    break;
                }
                case "content_block_delta":
                {
                    JsonMap delta = Json.Map(e.At("delta"));
                    string type = Json.Text(delta.At("type"));
                    if (type == "text_delta" && blocks.TryGetValue(key, out AssistantText streamed)) streamed.Text += Json.Text(delta.At("text"));
                    else if (type == "thinking_delta") Thinking = true;
                    break;
                }
                case "content_block_stop":
                    if (blocks.TryGetValue(key, out AssistantText finished))
                    {
                        finished.Streaming = false;
                        blocks.Remove(key);
                    }
                    break;
                case "message_stop":
                    Thinking = false;
                    break;
            }
        }

        ToolCall Tool(string toolId, string name)
        {
            if (toolId.Length > 0 && tools.TryGetValue(toolId, out ToolCall call)) return call;
            call = Add(new ToolCall { ToolId = toolId, Name = name });
            if (toolId.Length > 0) tools[toolId] = call;
            return call;
        }

        void Assistant(JsonMap whole)
        {
            string id = Json.Text(whole.At("id"));
            foreach (object item in Json.List(whole.At("content")))
            {
                JsonMap block = Json.Map(item);
                switch (Json.Text(block.At("type")))
                {
                    case "text":
                    {
                        string value = Json.Text(block.At("text"));
                        if (!streams.TryGetValue(id, out List<AssistantText> streamed)) streams[id] = streamed = new List<AssistantText>();
                        finals.TryGetValue(id, out int count);
                        finals[id] = count + 1;
                        if (count < streamed.Count)
                        {
                            streamed[count].Text = value;
                            streamed[count].Streaming = false;
                        }
                        else if (value.Trim().Length > 0)
                        {
                            streamed.Add(Add(new AssistantText { Text = value }));
                        }
                        break;
                    }
                    case "tool_use":
                    {
                        ToolCall call = Tool(Json.Text(block.At("id")), Json.Text(block.At("name")));
                        string name = Json.Text(block.At("name"));
                        if (name.Length > 0) call.Name = name;
                        call.Input = Json.Map(block.At("input"));
                        break;
                    }
                }
            }
            Thinking = false;
        }

        void Results(JsonMap whole)
        {
            foreach (object item in Json.List(whole.At("content")))
            {
                JsonMap block = Json.Map(item);
                if (Json.Text(block.At("type")) != "tool_result") continue;
                if (!tools.TryGetValue(Json.Text(block.At("tool_use_id")), out ToolCall call)) continue;
                call.Status = block.At("is_error") is bool failed && failed ? "error" : "done";
                call.Result = ResultText(block.At("content"));
                if (call.Status == "error" && call.Name.StartsWith("mcp__" + McpConfiguration.ServerName + "__")
                    && McpConfiguration.IsConnectionFailure(call.Result)) McpConnectionFailed = true;
            }
        }

        internal static string ResultText(object content)
        {
            if (content is string text) return text;
            var parts = new List<string>();
            foreach (object item in Json.List(content))
            {
                JsonMap block = Json.Map(item);
                string type = Json.Text(block.At("type"));
                if (type == "text") parts.Add(Json.Text(block.At("text")));
                else if (type == "image") parts.Add("[image]");
            }
            return string.Join("\n", parts);
        }

        /// <summary>A turn ended: nothing is being written any more, and unanswered questions lapse.</summary>
        public void FinishTurn()
        {
            Thinking = false;
            foreach (ChatEntry entry in Entries)
            {
                if (entry is AssistantText text) text.Streaming = false;
                else if (entry is ToolCall call && call.Status == "running") call.Status = "stopped";
                else if (entry is Approval approval && approval.Status == "pending") approval.Status = "cancelled";
            }
            blocks.Clear();
        }

        /// <summary>A tool's own name and the MCP server it belongs to: ("draw", "rhino") for mcp__rhino__draw.</summary>
        public static string ToolLabel(string name)
        {
            if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
            string[] parts = name.Split(new[] { "__" }, 3, StringSplitOptions.None);
            return parts.Length == 3 ? parts[2] : name;
        }

        /// <summary>One line saying what a tool call does, from its most telling input.</summary>
        public static string ToolSummary(JsonMap input, int limit = 90)
        {
            string value = "";
            foreach (string key in new[] { "command", "file_path", "path", "pattern", "url", "query", "description", "name", "prompt" })
            {
                if (input.At(key) is string found)
                {
                    value = found;
                    break;
                }
            }
            if (value.Length == 0 && input.Count > 0) value = Json.Write(input);
            value = string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";
        }
    }

    // --------------------------------------------------------------- session

    /// <summary>How to start Claude Code for this plug-in.</summary>
    internal sealed class Launch
    {
        public string[] Command;
        public string Folder;          // Claude Code's working folder
        public string SystemPrompt = "";
        public string Model = "";      // empty leaves Claude Code's own settings in charge
        public string Mode = "";
        public string McpUrl = "";
        public string Effort = "";
        public readonly List<string> DisallowedTools = new List<string>();
    }

    /// <summary>The Claude account Claude Code is signed in with. SignedIn is null until known.</summary>
    internal sealed class Account
    {
        public string Email = "", Plan = "";
        public bool? SignedIn;
    }

    /// <summary>A model from Claude Code's own list.</summary>
    internal sealed class ModelChoice
    {
        public string Value, Label, Description;
        public string Resolved; // the model this choice stands for, as Claude Code names it
        public string DefaultEffort = "";
        public bool IsDefault;
        public List<string> Efforts = new List<string>();
    }

    /// <summary>Claude Code cannot be started. The message tells the user what to do.</summary>
    internal sealed class LaunchException : Exception
    {
        public LaunchException(string message) : base(message) { }
    }

    /// <summary>
    /// One chat with Claude Code, run as a hidden process the way its VS Code extension
    /// runs it, with the user's own sign-in and settings.
    ///
    /// The two talk in lines of JSON. To Claude Code go the user's messages and "control
    /// requests": initialize, interrupt, and changes of model and permission mode. From it
    /// come the reply as it is written, requests for permission to use a tool
    /// ("can_use_tool"), answers to control requests, and the result of each turn. The
    /// answer to initialize says who is signed in and which models there are. Signing in
    /// is Claude Code's own ("claude auth login"), shared with the editor.
    ///
    /// Threads that read the process only queue what they read. Everything else happens
    /// in Poll, which the chat view calls on Rhino's own thread.
    /// </summary>
    internal sealed class ChatSession : IChatSession
    {
        const double StopGrace = 6; // seconds Claude Code gets to stop a turn before it is shut down

        /// <summary>The permission modes offered, as in the VS Code extension.</summary>
        public static readonly string[][] Modes =
        {
            new[] { "default", "Ask before edits" },
            new[] { "acceptEdits", "Edit automatically" },
            new[] { "plan", "Plan mode" },
        };

        readonly Func<Launch> prepare;

        public Transcript Transcript { get; private set; } = new Transcript();
        public string Model { get; private set; } = "";       // the model answering, as Claude Code names it
        public string SessionId = "";
        public string ConversationId => SessionId;
        public string Version { get; private set; } = "";     // Claude Code's own version, once it has said
        public bool MissingCli { get; set; }
        public Account Account { get; private set; } = new Account();
        public List<ModelChoice> Models { get; private set; } = new List<ModelChoice>();
        public string Mode { get; private set; } = "";
        public bool SigningIn { get; private set; }

        Process process;
        ConcurrentQueue<object> queue = new ConcurrentQueue<object>(); // a JsonMap for each line read, then the exit code
        readonly ConcurrentQueue<KeyValuePair<string, object>> accounts = new ConcurrentQueue<KeyValuePair<string, object>>();
        readonly Queue<string> errors = new Queue<string>();
        readonly Dictionary<string, string> pending = new Dictionary<string, string>(); // control request id -> its kind
        readonly HashSet<string> allowed = new HashSet<string>();                       // tools allowed for the rest of this chat
        int turns, requests;
        bool starting;
        DateTime? stopAsked;
        string resume = ""; // the conversation to carry on with when Claude Code is started again

        public ChatSession(Func<Launch> prepare) => this.prepare = prepare;

        public bool Running => process != null && !process.HasExited;

        /// <summary>idle, starting, working, waiting (for an answer to a permission question) or stopping.</summary>
        public string Status =>
            stopAsked != null ? "stopping"
            : Transcript.Pending ? "waiting"
            : turns > 0 ? (starting ? "starting" : "working")
            : "idle";

        /// <summary>
        /// Start Claude Code ahead of the first message. Nothing is sent, so no model is
        /// called: its answer only says who is signed in and which models there are.
        /// </summary>
        public void Warm()
        {
            if (!Running) Start(true);
        }

        public event Action<bool> TurnCompleted;

        public bool Send(string text, IReadOnlyList<ChatAttachment> attachments = null, string context = null)
        {
            text = (text ?? "").Trim();
            if (Status != "idle" || (text.Length == 0 && (attachments == null || attachments.Count == 0))) return false;
            var content = new List<object>();
            try
            {
                if (attachments != null)
                    foreach (ChatAttachment file in attachments)
                    {
                        file.Validate();
                        if (file.IsImage) content.Add(file.ClaudeImage());
                    }
            }
            catch (Exception ex) { Transcript.AddNotice("Could not attach file: " + ex.Message, true); return false; }
            content.Add(new JsonMap { ["type"] = "text", ["text"] = ChatAttachment.Prompt(text, attachments) + (context ?? "") });
            if (!Running && !Start(false)) return false;
            Transcript.AddUser(ChatAttachment.Display(text, attachments));
            turns++;
            Write(new JsonMap
            {
                ["type"] = "user",
                ["message"] = new JsonMap { ["role"] = "user", ["content"] = content },
                ["parent_tool_use_id"] = null,
                ["session_id"] = SessionId.Length > 0 ? SessionId : "default",
            });
            return true;
        }

        /// <summary>Stop the turn in hand. The chat carries on with the next message.</summary>
        public void Interrupt()
        {
            if (!Running || turns == 0 || stopAsked != null) return;
            stopAsked = DateTime.UtcNow;
            Control(new JsonMap { ["subtype"] = "interrupt" });
        }

        public void SetModel(string value)
        {
            if (Running) Control(new JsonMap { ["subtype"] = "set_model", ["model"] = value.Length > 0 ? value : "default" });
        }

        public void SetMode(string mode)
        {
            if (!Running) return;
            Mode = mode;
            Control(new JsonMap { ["subtype"] = "set_permission_mode", ["mode"] = mode.Length > 0 ? mode : "default" });
        }

        // Effort is a CLI launch option. Resume the same conversation with the saved
        // choice on the next send; do not interrupt a reply or discard its transcript.
        public void SetEffort(string value) { if (Status == "idle") Close(); }

        /// <summary>Answer a permission question shown in the chat: "allow", "always" or "deny".</summary>
        public void Answer(string requestId, string decision)
        {
            Approval approval = Transcript.Approval(requestId);
            if (approval == null || approval.Status != "pending") return;
            if (decision == "deny")
            {
                approval.Status = "denied";
                Permit(requestId, new JsonMap { ["behavior"] = "deny", ["message"] = "The user declined this in Rhino." });
                return;
            }
            approval.Status = "allowed";
            if (decision == "always") allowed.Add(approval.ToolName);
            Permit(requestId, new JsonMap { ["behavior"] = "allow", ["updatedInput"] = approval.Input });
        }

        /// <summary>A new chat: stop Claude Code and forget the conversation.</summary>
        public void Reset()
        {
            Close();
            Transcript = new Transcript();
            Model = "";
            SessionId = "";
            resume = "";
            allowed.Clear();
        }

        /// <summary>
        /// Carry on a conversation from History: an empty transcript (the page replays the
        /// saved one), and the next start of Claude Code uses --resume with its session id.
        /// </summary>
        public void Resume(string id)
        {
            Reset();
            SessionId = id ?? "";
            resume = SessionId;
        }

        /// <summary>Stop Claude Code. The next message starts it again and carries on the conversation.</summary>
        public void Close()
        {
            Process old = process;
            process = null;
            queue = new ConcurrentQueue<object>(); // whatever the old process still had to say goes with its queue
            pending.Clear();
            turns = 0;
            starting = false;
            stopAsked = null;
            ChildProcess.Stop(old);
            Transcript.FinishTurn();
        }

        // --------------------------------------------------------- account

        /// <summary>Ask Claude Code who is signed in ("claude auth status"), in the background.</summary>
        public void RefreshAccount()
        {
            Launch launch = Tool(true);
            if (launch == null) return;
            Background("claude-auth", () =>
            {
                var data = new JsonMap();
                try
                {
                    using (Process status = Process.Start(StartInfo(launch, new[] { "auth", "status", "--json" }, true)))
                    {
                        status.StandardInput.Close(); // it is not to wait for anything from here
                        string output = status.StandardOutput.ReadToEnd();
                        if (!status.WaitForExit(30000)) status.Kill();
                        data = Json.Map(Json.Parse(output.Trim()));
                    }
                }
                catch (Exception)
                {
                    // Left unknown.
                }
                accounts.Enqueue(new KeyValuePair<string, object>("status", data));
            });
        }

        /// <summary>Run Claude Code's own sign-in in a console window. It opens the browser.</summary>
        public void SignIn()
        {
            if (SigningIn) return;
            Launch launch = Tool(false);
            if (launch == null) return;
#if MAC
            // A Mac app's child has no console window to show itself in: the sign-in runs in Terminal.
            Func<int> ended;
            try
            {
                ended = MacProcess.RunInTerminal(launch.Command.Concat(new[] { "auth", "login" }).ToArray(), launch.Folder, "Crayon: signing in to Claude Code");
            }
            catch (Exception ex)
            {
                Transcript.AddNotice("Claude Code could not start its sign-in: " + ex.Message, true);
                return;
            }
            SigningIn = true;
            Transcript.AddNotice("Finish signing in to Claude in the Terminal window that has opened, and in your browser. This chat updates when you are done.");
            Background("claude-login", () => accounts.Enqueue(new KeyValuePair<string, object>("signed_in", ended())));
#else
            Process login;
            try
            {
                login = Process.Start(StartInfo(launch, new[] { "auth", "login" }, false));
            }
            catch (Exception ex)
            {
                Transcript.AddNotice("Claude Code could not start its sign-in: " + ex.Message, true);
                return;
            }
            SigningIn = true;
            Transcript.AddNotice("Finish signing in to Claude in your browser. This chat updates when you are done.");
            Background("claude-login", () => accounts.Enqueue(new KeyValuePair<string, object>("signed_in", Wait(login))));
#endif
        }

        /// <summary>Sign Claude Code out ("claude auth logout"). Editors using Claude Code are signed out too.</summary>
        public void SignOut()
        {
            Launch launch = Tool(false);
            if (launch == null) return;
            Process logout;
            try
            {
                logout = Process.Start(StartInfo(launch, new[] { "auth", "logout" }, true));
            }
            catch (Exception ex)
            {
                Transcript.AddNotice("Claude Code could not sign out: " + ex.Message, true);
                return;
            }
            Background("claude-logout", () =>
            {
                try
                {
                    logout.StandardInput.Close();
                    logout.StandardOutput.ReadToEnd();
                }
                catch (Exception)
                {
                }
                accounts.Enqueue(new KeyValuePair<string, object>("signed_out", Wait(logout)));
            });
        }

        static int Wait(Process waited)
        {
            try
            {
                waited.WaitForExit();
                return waited.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                waited.Dispose();
            }
        }

        static void Background(string name, ThreadStart work) => new Thread(work) { IsBackground = true, Name = name }.Start();

        void AccountResult(KeyValuePair<string, object> result)
        {
            if (result.Key == "status")
            {
                JsonMap data = Json.Map(result.Value);
                string plan = Json.Text(data.At("subscriptionType"));
                if (plan.Length == 0) plan = Json.Text(data.At("planTier"));
                string method = Json.Text(data.At("authMethod"));
                if (plan.Length == 0 && method.Length > 0 && method != "claude.ai") plan = "API key";
                Account = new Account { Email = Json.Text(data.At("email")), Plan = PlanLabel(plan), SignedIn = data.At("loggedIn") as bool? };
                return;
            }
            if (result.Key == "signed_in") SigningIn = false;
            // Claude Code reads the sign-in when it starts: so it is started afresh.
            Close();
            RefreshAccount();
            if (result.Key == "signed_out") Transcript.AddNotice("Signed out of Claude.");
            else if ((int)result.Value != 0) Transcript.AddNotice("Sign-in did not finish. Try again from the ⋯ menu.", true);
            else Warm();
        }

        // "Claude Pro" from either the initialize reply ("Claude Pro") or auth status ("pro").
        static string PlanLabel(string value)
        {
            if (value.Length == 0 || value.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return value;
            string words = value.Replace('_', ' ');
            return "Claude " + char.ToUpperInvariant(words[0]) + words.Substring(1);
        }

        // ------------------------------------------------------------ poll

        /// <summary>Take in what Claude Code has said since the last call. True if anything changed.</summary>
        public bool Poll(int budget = 400)
        {
            bool changed = false;
            ConcurrentQueue<object> reading = queue;
            for (int i = 0; i < budget && reading == queue && reading.TryDequeue(out object item); i++)
            {
                changed = true;
                if (item is JsonMap e) Handle(e);
                else Exited((int)item);
            }
            while (accounts.TryDequeue(out KeyValuePair<string, object> result))
            {
                changed = true;
                AccountResult(result);
            }
            if (stopAsked != null && (DateTime.UtcNow - stopAsked.Value).TotalSeconds > StopGrace && Running)
            {
                // Claude Code took no notice: it is shut down, and the next message carries the chat on.
                Close();
                Transcript.AddNotice("Stopped.");
                changed = true;
            }
            return changed;
        }

        // How to run Claude Code's own commands, such as its sign-in. Null when it cannot
        // be found: unless quiet, the chat then says why, so that a click never does nothing.
        Launch Tool(bool quiet)
        {
            try
            {
                Launch launch = prepare();
                MissingCli = false;
                Directory.CreateDirectory(launch.Folder);
                return launch;
            }
            catch (LaunchException ex)
            {
                MissingCli = true;
                if (!quiet) Transcript.AddNotice(ex.Message, true);
            }
            catch (Exception ex)
            {
                if (!quiet) Transcript.AddNotice("Claude Code could not be started: " + ex.Message, true);
            }
            return null;
        }

        // Every Claude Code command is started the same way, so that sign-in and chat use one account.
        static ProcessStartInfo StartInfo(Launch launch, IEnumerable<string> arguments, bool hidden)
        {
            IEnumerable<string> all = launch.Command.Skip(1).Concat(arguments);
#if MAC
            var info = new ProcessStartInfo(launch.Command[0])
#else
            var info = new ProcessStartInfo(launch.Command[0], string.Join(" ", all.Select(ClaudeCli.Quote)))
#endif
            {
                WorkingDirectory = launch.Folder,
                UseShellExecute = false,
                CreateNoWindow = hidden,
            };
            if (hidden)
            {
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
            }
            // Were Rhino itself started from a Claude Code session, this one must not think it is nested in it.
            info.EnvironmentVariables.Remove("CLAUDECODE");
#if MAC
            MacProcess.Configure(info, all);
#endif
            return info;
        }

        bool Start(bool quiet)
        {
            Launch launch = Tool(quiet);
            if (launch == null) return false;

            var arguments = new List<string>
            {
                "-p",
                "--input-format", "stream-json",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
                "--permission-prompt-tool", "stdio",
            };
            if (launch.DisallowedTools.Count > 0) arguments.AddRange(new[] { "--disallowedTools", string.Join(",", launch.DisallowedTools) });
            arguments.AddRange(McpConfiguration.ClaudeArguments(launch.McpUrl));
            if (launch.SystemPrompt.Length > 0) arguments.AddRange(new[] { "--append-system-prompt", launch.SystemPrompt });
            if (launch.Model.Length > 0) arguments.AddRange(new[] { "--model", launch.Model });
            if (launch.Effort.Length > 0) arguments.AddRange(new[] { "--effort", launch.Effort });
            if (launch.Mode.Length > 0 && launch.Mode != "default") arguments.AddRange(new[] { "--permission-mode", launch.Mode });
            if (resume.Length > 0) arguments.AddRange(new[] { "--resume", resume });

            Process started;
            try
            {
                started = Process.Start(StartInfo(launch, arguments, true));
            }
            catch (Exception ex)
            {
                if (!quiet) Transcript.AddNotice("Claude Code could not start: " + ex.Message, true);
                return false;
            }
            process = started;
            ConcurrentQueue<object> target = queue = new ConcurrentQueue<object>();
            lock (errors) errors.Clear();
            starting = true;
            Background("claude-stdout", () => Read(started, target));
            Background("claude-stderr", () => ReadErrors(started));
            Control(new JsonMap { ["subtype"] = "initialize", ["hooks"] = null });
            return true;
        }

        static void Read(Process source, ConcurrentQueue<object> target)
        {
            try
            {
                string line;
                while ((line = source.StandardOutput.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("{", StringComparison.Ordinal) && Json.Parse(line) is JsonMap e) target.Enqueue(e);
                }
                source.WaitForExit();
                target.Enqueue(source.ExitCode);
            }
            catch (Exception)
            {
                target.Enqueue(-1);
            }
        }

        void ReadErrors(Process source)
        {
            try
            {
                string line;
                while ((line = source.StandardError.ReadLine()) != null)
                {
                    lock (errors)
                    {
                        errors.Enqueue(line.TrimEnd());
                        while (errors.Count > 40) errors.Dequeue();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        void Write(JsonMap message)
        {
            Process target = process;
            if (target == null) return;
            try
            {
                // Bytes, not text: Claude Code reads UTF-8, whatever the console's code page is.
                byte[] line = Encoding.UTF8.GetBytes(Json.Write(message) + "\n");
                Stream input = target.StandardInput.BaseStream;
                input.Write(line, 0, line.Length);
                input.Flush();
            }
            catch (Exception ex)
            {
                Transcript.AddNotice("Claude Code stopped responding: " + ex.Message, true);
                Close();
            }
        }

        void Control(JsonMap request)
        {
            string id = "req_" + ++requests + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            pending[id] = Json.Text(request.At("subtype"));
            Write(new JsonMap { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
        }

        void Permit(string requestId, JsonMap response)
        {
            Write(new JsonMap
            {
                ["type"] = "control_response",
                ["response"] = new JsonMap { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response },
            });
        }

        void Handle(JsonMap e)
        {
            string kind = Json.Text(e.At("type"));
            if (kind == "system" && Json.Text(e.At("subtype")) == "init")
            {
                starting = false;
                Model = Either(Json.Text(e.At("model")), Model);
                SessionId = Either(Json.Text(e.At("session_id")), SessionId);
                Mode = Either(Json.Text(e.At("permissionMode")), Mode);
            }
            else if (kind == "control_request")
            {
                Request(Json.Text(e.At("request_id")), Json.Map(e.At("request")));
            }
            else if (kind == "control_cancel_request")
            {
                Approval approval = Transcript.Approval(Json.Text(e.At("request_id")));
                if (approval != null && approval.Status == "pending") approval.Status = "cancelled";
            }
            else if (kind == "control_response")
            {
                Reply(Json.Map(e.At("response")));
            }
            else if (kind == "result")
            {
                Result(e);
            }
            else
            {
                if (kind == "stream_event" || kind == "assistant") starting = false;
                Transcript.Apply(e);
            }
        }

        static string Either(string value, string otherwise) => value.Length > 0 ? value : otherwise;

        // The answer to one of this session's control requests.
        void Reply(JsonMap response)
        {
            string id = Json.Text(response.At("request_id"));
            pending.TryGetValue(id, out string subtype);
            pending.Remove(id);
            if (Json.Text(response.At("subtype")) == "error")
            {
                Transcript.AddNotice("Claude Code: " + Json.Text(response.At("error")), true);
                return;
            }
            JsonMap data = Json.Map(response.At("response"));
            switch (subtype)
            {
                case "initialize":
                {
                    starting = false;
                    JsonMap account = Json.Map(data.At("account"));
                    string email = Json.Text(account.At("email"));
                    if (email.Length > 0) Account = new Account { Email = email, Plan = PlanLabel(Json.Text(account.At("subscriptionType"))), SignedIn = true };
                    else RefreshAccount(); // nobody named in the answer: ask whether anyone is signed in
                    var models = new List<ModelChoice>();
                    foreach (object item in Json.List(data.At("models")))
                    {
                        JsonMap model = Json.Map(item);
                        string value = Json.Text(model.At("value"));
                        if (value.Length == 0) continue;
                        models.Add(new ModelChoice { Value = value, Label = Either(Json.Text(model.At("displayName")), value), Description = Json.Text(model.At("description")), Resolved = Json.Text(model.At("resolvedModel")),
                            IsDefault = value == "default", Efforts = Json.List(model.At("supportedEffortLevels")).Select(Json.Text).Where(effort => effort.Length > 0).Distinct().ToList() });
                    }
                    if (models.Count > 0) Models = models;
                    Version = Either(Json.Text(data.At("claude_code_version")), Version);
                    Mode = Either(Json.Text(data.At("current_permission_mode")), Mode);
                    break;
                }
                case "set_model":
                    Model = Either(Json.Text(data.At("model")), Model);
                    break;
                case "set_permission_mode":
                    Mode = Either(Json.Text(data.At("mode")), Mode);
                    break;
            }
        }

        void Request(string requestId, JsonMap request)
        {
            if (Json.Text(request.At("subtype")) != "can_use_tool")
            {
                // Hooks and the like are not used here.
                Write(new JsonMap
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonMap { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = "Not supported by this app." },
                });
                return;
            }
            string name = Json.Text(request.At("tool_name"));
            JsonMap input = Json.Map(request.At("input"));
            if (allowed.Contains(name))
            {
                Permit(requestId, new JsonMap { ["behavior"] = "allow", ["updatedInput"] = input });
                return;
            }
            Transcript.AddApproval(requestId, name, input, Either(Json.Text(request.At("description")), Json.Text(request.At("title"))));
        }

        void Result(JsonMap e)
        {
            turns = Math.Max(0, turns - 1);
            starting = false;
            SessionId = Either(Json.Text(e.At("session_id")), SessionId);
            resume = SessionId;
            bool stopped = stopAsked != null;
            if (turns == 0)
            {
                stopAsked = null;
                Transcript.FinishTurn();
            }
            string subtype = Json.Text(e.At("subtype"));
            TurnCompleted?.Invoke(!stopped && !(e.At("is_error") is bool isError && isError) && subtype == "success");
            if (stopped)
            {
                Transcript.AddNotice("Stopped.");
            }
            else if ((e.At("is_error") is bool failed && failed) || (subtype.Length > 0 && subtype != "success"))
            {
                string detail = Either(Json.Text(e.At("result")), subtype.Replace('_', ' '));
                Transcript.AddNotice("Claude Code: " + Either(detail, "the turn ended with an error"), true);
                string lowered = detail.ToLowerInvariant();
                if (lowered.Contains("login") || lowered.Contains("log in") || lowered.Contains("authenticat") || lowered.Contains("401"))
                    RefreshAccount(); // signed out, or the sign-in has run out: the view then offers to sign in
            }
        }

        // Each process has its own queue, so an exit read here is the present process's.
        void Exited(int code)
        {
            process = null;
            pending.Clear();
            bool busy = turns > 0;
            turns = 0;
            starting = false;
            stopAsked = null;
            Transcript.FinishTurn();
            if (!busy && code == 0) return;
            string details;
            lock (errors) details = string.Join("\n", errors.Skip(Math.Max(0, errors.Count - 6))).Trim();
            Transcript.AddNotice("Claude Code exited (code " + code + ")." + (details.Length > 0 ? "\n" + details : ""), true);
        }
    }
}
