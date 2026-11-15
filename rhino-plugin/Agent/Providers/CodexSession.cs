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
    /// <summary>
    /// Codex's local app-server over stdio JSON-RPC. Authentication stays in Codex;
    /// model discovery, streaming, conversation resume and approvals use its v2 protocol.
    /// No API key or access token is read by Crayon.
    /// </summary>
    internal sealed class CodexSession : IChatSession
    {
        readonly Func<Launch> prepare;
        public Transcript Transcript { get; private set; } = new Transcript();
        public string Model { get; private set; } = "";
        public string Version { get; private set; } = "";
        public bool MissingCli { get; set; }
        public Account Account { get; private set; } = new Account();
        public List<ModelChoice> Models { get; private set; } = new List<ModelChoice>();
        public string Mode { get; private set; } = "default";
        public bool SigningIn { get; private set; }
        public string ConversationId => thread;
        public string Status => stopping ? "stopping" : Transcript.Pending ? "waiting" : pendingInput != null ? "starting" : turn.Length > 0 ? "working" : "idle";

        Process process;
        Launch launch;
        ConcurrentQueue<object> queue = new ConcurrentQueue<object>();
        readonly Dictionary<string, string> requests = new Dictionary<string, string>();
        readonly Dictionary<string, object> approvals = new Dictionary<string, object>();
        readonly Dictionary<string, JsonMap> mcpApprovals = new Dictionary<string, JsonMap>();
        readonly Dictionary<string, AssistantText> texts = new Dictionary<string, AssistantText>();
        readonly Dictionary<string, ToolCall> calls = new Dictionary<string, ToolCall>();
        readonly Queue<string> errors = new Queue<string>();
        List<object> pendingInput;
        string thread = "", turn = "";
        bool initialized, threadReady, threadStarting, stopping, loginPending;
        int nextRequest;
        DateTime? stopTime;

        public CodexSession(Func<Launch> prepare) { this.prepare = prepare; }

        public void Warm() { Start(true); }

        public event Action<bool> TurnCompleted;

        public bool Send(string text, IReadOnlyList<ChatAttachment> attachments = null, string context = null)
        {
            text = (text ?? "").Trim();
            if (Status != "idle" || (text.Length == 0 && (attachments == null || attachments.Count == 0))) return false;
            var input = new List<object>();
            try
            {
                if (attachments != null)
                    foreach (ChatAttachment file in attachments)
                    {
                        file.Validate();
                        if (file.IsImage) input.Add(new JsonMap { ["type"] = "localImage", ["path"] = file.Path });
                    }
            }
            catch (Exception ex) { Transcript.AddNotice("Could not attach file: " + ex.Message, true); return false; }
            input.Insert(0, new JsonMap { ["type"] = "text", ["text"] = ChatAttachment.Prompt(text, attachments) + (context ?? ""), ["text_elements"] = new List<object>() });
            if (!Start(false)) return false;
            Transcript.AddUser(ChatAttachment.Display(text, attachments));
            pendingInput = input;
            ContinueSend();
            return true;
        }

        bool Start(bool quiet)
        {
            if (process != null) return true;
            try
            {
                launch = prepare();
                Mode = string.IsNullOrEmpty(launch.Mode) ? "default" : launch.Mode;
                MissingCli = false;
                Directory.CreateDirectory(launch.Folder);
                var args = launch.Command.Skip(1).Concat(new[] { "app-server", "--listen", "stdio://" })
                    .Concat(McpConfiguration.CodexArguments(launch.McpUrl));
                var info = new ProcessStartInfo(launch.Command[0], string.Join(" ", args.Select(ClaudeCli.Quote)))
                {
                    WorkingDirectory = launch.Folder, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                Process started = process = Process.Start(info);
                ConcurrentQueue<object> target = queue = new ConcurrentQueue<object>();
                lock (errors) errors.Clear();
                new Thread(() => Read(started, target)) { IsBackground = true, Name = "crayon-codex" }.Start();
                new Thread(() => ReadErrors(started)) { IsBackground = true, Name = "crayon-codex-errors" }.Start();
                Request("initialize", new JsonMap
                {
                    ["clientInfo"] = new JsonMap { ["name"] = "crayon_rhino", ["title"] = "Crayon for Rhino", ["version"] = "1.0.0" },
                    ["capabilities"] = new JsonMap { ["experimentalApi"] = false, ["requestAttestation"] = false },
                });
                return true;
            }
            catch (Exception ex)
            {
                MissingCli = ex is LaunchException;
                if (!quiet) Transcript.AddNotice("Codex could not start: " + ex.Message, true);
                Close();
                return false;
            }
        }

        void ContinueSend()
        {
            if (!initialized || pendingInput == null || threadStarting) return;
            if (!threadReady)
            {
                threadStarting = true;
                var options = new JsonMap
                {
                    ["cwd"] = launch.Folder, ["approvalPolicy"] = "on-request",
                    ["sandbox"] = Mode == "acceptEdits" ? "workspace-write" : "read-only",
                    ["developerInstructions"] = launch.SystemPrompt,
                };
                if (launch.Model.Length > 0) options["model"] = launch.Model;
                if (thread.Length > 0) options["threadId"] = thread;
                Request(thread.Length > 0 ? "thread/resume" : "thread/start", options);
                return;
            }
            var parameters = new JsonMap
            {
                ["threadId"] = thread, ["input"] = pendingInput,
                ["approvalPolicy"] = "on-request",
                ["sandboxPolicy"] = Mode == "acceptEdits"
                    ? new JsonMap { ["type"] = "workspaceWrite", ["writableRoots"] = new List<object> { launch.Folder }, ["networkAccess"] = false, ["excludeTmpdirEnvVar"] = false, ["excludeSlashTmp"] = false }
                    : new JsonMap { ["type"] = "readOnly", ["networkAccess"] = false },
            };
            if (launch.Model.Length > 0) parameters["model"] = launch.Model;
            string effort = EffortLevels.ForTurn(Models, launch.Model, Model, launch.Effort);
            if (effort.Length > 0) parameters["effort"] = effort;
            Request("turn/start", parameters);
            // Keep pendingInput until the turn response so Send cannot race another turn.
        }

        public void Interrupt()
        {
            if (Status == "idle" || stopping) return;
            if (turn.Length == 0) { Close(); Transcript.AddNotice("Stopped."); return; }
            stopping = true;
            stopTime = DateTime.UtcNow;
            Request("turn/interrupt", new JsonMap { ["threadId"] = thread, ["turnId"] = turn });
        }

        public void SetModel(string value) { if (launch != null) launch.Model = value == "default" ? "" : value; }
        public void SetMode(string value) { Mode = value; if (launch != null) launch.Mode = value; }
        public void SetEffort(string value) { if (Status == "idle" && launch != null) launch.Effort = value == "default" ? "" : value; }

        public void Answer(string requestId, string decision)
        {
            Approval approval = Transcript.Approval(requestId);
            if (approval == null || approval.Status != "pending" || !approvals.TryGetValue(requestId, out object id)) return;
            JsonMap result;
            if (mcpApprovals.Remove(requestId))
            {
                result = new JsonMap { ["action"] = decision == "deny" ? "decline" : "accept", ["content"] = null };
                if (decision == "always") result["_meta"] = new JsonMap { ["persist"] = "session" };
            }
            else result = new JsonMap { ["decision"] = decision == "deny" ? "decline" : decision == "always" ? "acceptForSession" : "accept" };
            Write(new JsonMap { ["id"] = id, ["result"] = result });
            approval.Status = decision == "deny" ? "denied" : "allowed";
            approvals.Remove(requestId);
        }

        public void Reset()
        {
            Close();
            Transcript = new Transcript();
            thread = "";
            Model = "";
            texts.Clear(); calls.Clear();
        }

        /// <summary>Carry on a thread from History: the next message sends thread/resume with its id.</summary>
        public void Resume(string id)
        {
            Reset();
            thread = id ?? "";
        }

        public void Close()
        {
            Process old = process;
            process = null;
            initialized = threadReady = threadStarting = stopping = loginPending = SigningIn = false;
            pendingInput = null; turn = ""; stopTime = null;
            requests.Clear(); approvals.Clear(); mcpApprovals.Clear();
            queue = new ConcurrentQueue<object>();
            Transcript.FinishTurn();
            ChildProcess.Stop(old);
        }

        public void SignIn()
        {
            if (SigningIn || !Start(false)) return;
            SigningIn = loginPending = true;
            if (initialized) BeginLogin();
        }

        void BeginLogin() { loginPending = false; Request("account/login/start", new JsonMap { ["type"] = "chatgpt" }); }

        public void SignOut()
        {
            if (!initialized) { Transcript.AddNotice("Wait for Codex to connect before signing out."); return; }
            if (Status != "idle") { Transcript.AddNotice("Stop the current reply before signing out."); return; }
            Request("account/logout", new JsonMap());
        }

        string Request(string method, JsonMap parameters)
        {
            string id = "crayon_" + ++nextRequest;
            requests[id] = method;
            Write(new JsonMap { ["id"] = id, ["method"] = method, ["params"] = parameters });
            return id;
        }

        void Write(JsonMap message)
        {
            try
            {
                if (process == null) return;
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(message) + "\n");
                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                process.StandardInput.BaseStream.Flush();
            }
            catch (Exception ex) { Transcript.AddNotice("Codex stopped responding: " + ex.Message, true); Close(); }
        }

        static void Read(Process source, ConcurrentQueue<object> target)
        {
            try
            {
                string line;
                while ((line = source.StandardOutput.ReadLine()) != null)
                    if (Json.Parse(line) is JsonMap message) target.Enqueue(message);
                source.WaitForExit(); target.Enqueue(source.ExitCode);
            }
            catch (Exception) { target.Enqueue(-1); }
        }

        void ReadErrors(Process source)
        {
            try
            {
                string line;
                while ((line = source.StandardError.ReadLine()) != null)
                    lock (errors) { errors.Enqueue(line); while (errors.Count > 12) errors.Dequeue(); }
            }
            catch (Exception) { }
        }

        public bool Poll(int budget = 400)
        {
            bool changed = false;
            ConcurrentQueue<object> reading = queue;
            for (int i = 0; i < budget && reading == queue && reading.TryDequeue(out object item); i++)
            {
                changed = true;
                if (item is JsonMap message) Handle(message);
                else
                {
                    string detail;
                    lock (errors) detail = string.Join("\n", errors.Skip(Math.Max(0, errors.Count - 4)));
                    Close();
                    Transcript.AddNotice("Codex exited (code " + item + ")." + (detail.Length > 0 ? "\n" + detail : ""), true);
                }
            }
            if (stopTime.HasValue && (DateTime.UtcNow - stopTime.Value).TotalSeconds > 6)
            { Close(); Transcript.AddNotice("Stopped."); changed = true; }
            return changed;
        }

        void Handle(JsonMap message)
        {
            string method = Json.Text(message.At("method"));
            if (method.Length > 0)
            {
                JsonMap data = Json.Map(message.At("params"));
                if (message.ContainsKey("id")) ServerRequest(message.At("id"), method, data);
                else Notification(method, data);
                return;
            }
            string id = Json.Text(message.At("id"));
            if (!requests.TryGetValue(id, out method)) return;
            requests.Remove(id);
            if (message.At("error") is JsonMap error)
            {
                Transcript.AddNotice("Codex: " + Json.Text(error.At("message")), true);
                if (method == "initialize") { Close(); return; }
                if (method == "account/login/start") SigningIn = false;
                if (method == "turn/start" || method.StartsWith("thread/")) { pendingInput = null; turn = ""; threadStarting = false; }
                return;
            }
            JsonMap result = Json.Map(message.At("result"));
            switch (method)
            {
                case "initialize":
                    initialized = true;
                    Version = Json.Text(result.At("userAgent"));
                    Write(new JsonMap { ["method"] = "initialized", ["params"] = new JsonMap() });
                    Request("account/read", new JsonMap { ["refreshToken"] = false });
                    Request("model/list", new JsonMap());
                    if (loginPending) BeginLogin();
                    ContinueSend();
                    break;
                case "account/read":
                    JsonMap account = Json.Map(result.At("account"));
                    Account = new Account { SignedIn = result.At("account") != null, Email = Json.Text(account.At("email")), Plan = Json.Text(account.At("type")) == "apiKey" ? "API key" : Json.Text(account.At("planType")) };
                    break;
                case "model/list":
                    Models = Json.List(result.At("data")).Select(Json.Map).Where(m => !(m.At("hidden") is bool hidden && hidden)).Select(m => new ModelChoice { Value = Json.Text(m.At("model")), Label = Json.Text(m.At("displayName")), Description = Json.Text(m.At("description")), Resolved = Json.Text(m.At("model")),
                        IsDefault = m.At("isDefault") is bool isDefault && isDefault,
                        DefaultEffort = Json.Text(m.At("defaultReasoningEffort")),
                        Efforts = Json.List(m.At("supportedReasoningEfforts")).Select(Json.Map).Select(option => Json.Text(option.At("reasoningEffort"))).Where(effort => effort.Length > 0).Distinct().ToList() }).ToList();
                    Models.Insert(0, new ModelChoice { Value = "default", Label = "Default", Description = "Use your Codex settings" });
                    break;
                case "thread/start": case "thread/resume":
                    thread = Json.Text(Json.Map(result.At("thread")).At("id"));
                    Model = Json.Text(result.At("model"));
                    threadReady = thread.Length > 0;
                    threadStarting = false;
                    if (threadReady) ContinueSend(); else { pendingInput = null; Transcript.AddNotice("Codex did not return a conversation ID.", true); }
                    break;
                case "turn/start":
                    pendingInput = null;
                    turn = Json.Text(Json.Map(result.At("turn")).At("id"));
                    break;
                case "account/login/start":
                    string url = Json.Text(result.At("authUrl"));
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
                    {
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception) { Transcript.AddNotice("Open this link to sign in: " + uri.AbsoluteUri); }
                        Transcript.AddNotice("Finish signing in to ChatGPT in your browser. This chat updates when you are done.");
                    }
                    break;
                case "account/logout":
                    Account = new Account { SignedIn = false };
                    threadReady = false; thread = "";
                    Transcript.AddNotice("Signed out of Codex.");
                    break;
            }
        }

        void ServerRequest(object id, string method, JsonMap data)
        {
            JsonMap meta = Json.Map(data.At("_meta"));
            if (method == "mcpServer/elicitation/request" && Json.Text(meta.At("codex_approval_kind")) == "mcp_tool_call")
            {
                string key = Json.Write(id);
                approvals[key] = id;
                mcpApprovals[key] = data;
                string tool = Json.Text(meta.At("tool_name"));
                if (tool.Length == 0) tool = Json.Text(meta.At("tool_title"));
                JsonMap input = Json.Map(meta.At("tool_params"));
                Transcript.AddApproval(key, tool.Length > 0 ? tool : "Rhino tool",
                    input, Json.Text(data.At("message")) + "\n" + Transcript.ToolSummary(input, 4000));
                return;
            }
            if (method == "item/commandExecution/requestApproval" || method == "item/fileChange/requestApproval")
            {
                string key = Json.Write(id);
                approvals[key] = id;
                Transcript.AddApproval(key, method.Contains("commandExecution") ? "Run command" : "Edit files", data,
                    Json.Text(data.At("reason")) + "\n" + Json.Text(data.At("command")));
                return;
            }
            // Requests that need a different form are explicitly declined, never silently approved.
            if (method == "item/tool/requestUserInput")
                Write(new JsonMap { ["id"] = id, ["result"] = new JsonMap { ["answers"] = new JsonMap() } });
            else if (method == "mcpServer/elicitation/request")
                Write(new JsonMap { ["id"] = id, ["result"] = new JsonMap { ["action"] = "decline", ["content"] = null } });
            else if (method == "item/permissions/requestApproval")
                Write(new JsonMap { ["id"] = id, ["result"] = new JsonMap { ["permissions"] = new JsonMap(), ["scope"] = "turn" } });
            else
                Write(new JsonMap { ["id"] = id, ["error"] = new JsonMap { ["code"] = -32601, ["message"] = "This request is not supported by Crayon. Ask the user in chat instead." } });
            Transcript.AddNotice("Codex requested an interaction that is not available in this panel. Ask it to continue in chat.");
        }

        void Notification(string method, JsonMap data)
        {
            string threadId = Json.Text(data.At("threadId"));
            if (threadId.Length > 0 && thread.Length > 0 && threadId != thread) return;
            switch (method)
            {
                case "account/login/completed":
                    SigningIn = false;
                    if (data.At("success") is bool success && !success) Transcript.AddNotice("Sign-in did not finish: " + Json.Text(data.At("error")), true);
                    Request("account/read", new JsonMap { ["refreshToken"] = false });
                    Request("model/list", new JsonMap());
                    break;
                case "account/updated": Request("account/read", new JsonMap { ["refreshToken"] = false }); break;
                case "turn/started": pendingInput = null; turn = Json.Text(Json.Map(data.At("turn")).At("id")); break;
                case "turn/completed":
                    JsonMap finished = Json.Map(data.At("turn"));
                    string status = Json.Text(finished.At("status"));
                    if (status == "failed") Transcript.AddNotice("Codex: " + Json.Text(Json.Map(finished.At("error")).At("message")), true);
                    else if (status == "interrupted") Transcript.AddNotice("Stopped.");
                    pendingInput = null; turn = ""; stopping = false; stopTime = null;
                    Transcript.FinishTurn(); approvals.Clear(); mcpApprovals.Clear();
                    TurnCompleted?.Invoke(status == "completed");
                    break;
                case "item/agentMessage/delta":
                    Text(Json.Text(data.At("itemId"))).Text += Json.Text(data.At("delta"));
                    break;
                case "item/started": case "item/completed": Item(Json.Map(data.At("item")), method == "item/completed"); break;
                case "error":
                    Transcript.AddNotice("Codex: " + Json.Text(Json.Map(data.At("error")).At("message")), true);
                    break;
            }
        }

        AssistantText Text(string id)
        {
            if (!texts.TryGetValue(id, out AssistantText text)) texts[id] = text = Transcript.Add(new AssistantText { Text = "", Streaming = true });
            Transcript.Thinking = false;
            return text;
        }

        void Item(JsonMap item, bool completed)
        {
            string kind = Json.Text(item.At("type")), id = Json.Text(item.At("id"));
            if (kind == "agentMessage" || kind == "plan")
            {
                AssistantText text = Text(id);
                text.Text = Json.Text(item.At("text")); text.Streaming = !completed;
                return;
            }
            if (kind == "reasoning") { Transcript.Thinking = !completed; return; }
            if (kind == "userMessage" || kind == "hookPrompt") return;
            if (!calls.TryGetValue(id, out ToolCall call))
                calls[id] = call = Transcript.Add(new ToolCall { ToolId = id, Name = kind == "commandExecution" ? "Run command" : kind == "fileChange" ? "Edit files" : kind == "mcpToolCall" ? Json.Text(item.At("tool")) : kind });
            string command = Json.Text(item.At("command"));
            if (command.Length > 0) call.Input = new JsonMap { ["command"] = command };
            else if (item.At("arguments") is JsonMap arguments) call.Input = arguments;
            else if (item.At("changes") != null) call.Input = new JsonMap { ["description"] = Json.Write(item.At("changes")) };
            string status = Json.Text(item.At("status"));
            call.Status = status == "failed" || status == "declined" ? "error" : completed ? "done" : "running";
            call.Result = Json.Text(item.At("aggregatedOutput"));
            if (kind == "mcpToolCall")
            {
                JsonMap result = Json.Map(item.At("result"));
                string error = Json.Text(Json.Map(item.At("error")).At("message"));
                call.Result = error.Length > 0 ? error : Transcript.ResultText(result.At("content"));
                if (error.Length > 0 || result.At("isError") is bool failed && failed) call.Status = "error";
                if (call.Status == "error" && Json.Text(item.At("server")) == McpConfiguration.ServerName
                    && McpConfiguration.IsConnectionFailure(call.Result)) Transcript.McpConnectionFailed = true;
            }
        }
    }
}
