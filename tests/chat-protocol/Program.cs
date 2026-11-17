using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using RhinoSketchingTool;

internal static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    static void Until(IChatSession session, Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        do { session.Poll(); if (condition()) return; Thread.Sleep(15); } while (DateTime.UtcNow < deadline);
        throw new Exception("Timed out: " + session.Status + " / " + string.Join("; ", session.Transcript.Entries.OfType<Notice>().Select(n => n.Text)));
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--live-mcp") return LiveMcp.Run(args[1]);
        if (args.Length != 2) { Console.Error.WriteLine("Usage: ChatProtocol.exe <node.exe> <mock-cli.js>"); return 2; }
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "bin", "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string picture = Path.Combine(folder, "reference image.png"), document = Path.Combine(folder, "notes.txt");
        File.WriteAllBytes(picture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB1sAAAAASUVORK5CYII="));
        File.WriteAllText(document, "Reference notes");
        var files = new List<ChatAttachment> { new ChatAttachment(picture), new ChatAttachment(document) };
        const string endpoint = "http://localhost:10542/agent";
        string chosenEffort = "high";
        Func<string, Launch> launch = provider => new Launch { Command = new[] { args[0], Path.GetFullPath(args[1]), provider }, Folder = folder, McpUrl = endpoint, Effort = chosenEffort };
        var codex = new CodexSession(() => launch("codex"));
        var claude = new ChatSession(() => launch("claude"));
        int codexCompleted = 0, claudeCompleted = 0;
        codex.TurnCompleted += success => { if (success) codexCompleted++; };
        claude.TurnCompleted += success => { if (success) claudeCompleted++; };
        Process codexWorker = null, claudeWorker = null;
        try
        {
            codex.Warm();
            Until(codex, () => codex.Account.SignedIn == true && codex.Models.Count > 1);
            codexWorker = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(folder, "codex-child.pid"))));
            Check(codex.Models.Any(model => model.Value == "fixture-model"), "Codex account and model discovery");
            Check(EffortLevels.Model(codex.Models, "", "").Efforts.Contains("ultra"), "Codex effort choices follow discovered model capabilities");
            var codexArgs = Json.List(Json.Parse(File.ReadAllText(Path.Combine(folder, "codex-arguments.json")))).Select(Json.Text).ToList();
            Check(codexArgs.Contains("-c") && codexArgs.Any(a => a.StartsWith("mcp_servers.crayon_rhino=") && a.Contains(endpoint)), "Codex receives document-specific MCP config as a single argument");
            Check(codex.Send("Review this", files, "\nACTIVE_GROUP_CONTEXT"), "Codex accepts message and attachments");
            Check(!codex.Send("Duplicate"), "Codex blocks overlapping sends");
            Until(codex, () => codex.Transcript.Pending);
            Check(codex.Transcript.Entries.OfType<AssistantText>().Single().Text == "Codex fixture reply", "Codex streaming text");
            var wire = Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder, "codex-request.json"))));
            var input = Json.List(Json.Map(wire.At("params")).At("input"));
            Check(Json.Text(Json.Map(wire.At("params")).At("effort")) == "high", "Codex sends chosen effort on the turn");
            Check(input.Select(Json.Map).Any(item => Json.Text(item.At("type")) == "localImage" && Json.Text(item.At("path")) == picture), "Codex image attachment payload");
            Check(Json.Text(Json.Map(input[0]).At("text")).Contains(document), "Codex document path reaches model");
            Check(Json.Text(Json.Map(input[0]).At("text")).Contains("ACTIVE_GROUP_CONTEXT") && !codex.Transcript.Entries.OfType<UserMessage>().Last().Text.Contains("ACTIVE_GROUP_CONTEXT"), "Codex receives scoped context without expanding user chat bubble");
            Approval approval = codex.Transcript.Entries.OfType<Approval>().Single();
            codex.Answer(approval.RequestId, "deny");
            Until(codex, () => codex.Status == "idle");
            var response = Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder, "approval-response.json"))));
            Check(Json.Text(Json.Map(response.At("result")).At("decision")) == "decline", "Codex denial round trip");
            Check(codex.Transcript.Entries.OfType<AssistantText>().Count() == 1, "Codex final message replaces stream without duplicates");
            Check(codexCompleted == 1, "Codex emits one completed-turn signal");
            chosenEffort = "";
            codex.SetEffort("default");
            codex.Close();
            Check(codexWorker.WaitForExit(2000), "Codex closes its wrapper child process");
            Check(codex.Send("Continue"), "Codex accepts a resumed turn");
            Until(codex, () => codex.Transcript.Pending);
            var resumed = Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder, "thread-request.json"))));
            Check(Json.Text(resumed.At("method")) == "thread/resume", "Codex resumes its own conversation after closing");
            var defaultWire = Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder, "codex-request.json"))));
            Check(Json.Text(Json.Map(defaultWire.At("params")).At("effort")) == "medium", "Codex Default explicitly resets prior effort to model default");
            codex.Interrupt(); Until(codex, () => codex.Status == "idle");
            Check(codex.Transcript.Entries.OfType<Approval>().All(a => a.Status != "pending"), "Codex stop clears pending approvals");
            chosenEffort = "high";
            claude.Warm(); Until(claude, () => claude.Account.SignedIn == true);
            var claudeArgs = Json.List(Json.Parse(File.ReadAllText(Path.Combine(folder, "claude-arguments.json")))).Select(Json.Text).ToList();
            int configAt = claudeArgs.IndexOf("--mcp-config");
            Check(claudeArgs.IndexOf("--effort") >= 0 && claudeArgs[claudeArgs.IndexOf("--effort") + 1] == "high", "Claude launches with chosen effort");
            Check(EffortLevels.Model(claude.Models, "", "").Efforts.SequenceEqual(new[] { "low", "medium", "high", "max" }) && EffortLevels.Model(claude.Models, "legacy", "").Efforts.Count == 0, "Claude effort choices respect model support");
            var mcpServers = Json.Map(Json.Map(Json.Parse(claudeArgs[configAt + 1])).At("mcpServers"));
            var server = Json.Map(mcpServers.At("crayon_rhino"));
            Check(configAt >= 0 && Json.Text(server.At("url")) == endpoint && Json.Text(server.At("type")) == "http", "Claude receives valid HTTP MCP config without shell quoting corruption");
            Check(!McpConfiguration.ClaudeArguments("").Any() && !McpConfiguration.CodexArguments("").Any(), "Missing dependency launches neither agent with a broken MCP entry");
            claudeWorker = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(folder, "claude-child.pid"))));
            Check(claude.Send("Review this", files, "\nACTIVE_GROUP_CONTEXT"), "Claude accepts attachments");
            Until(claude, () => claude.Status == "idle");
            var claudeWire = Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder, "claude-request.json"))));
            var content = Json.List(Json.Map(claudeWire.At("message")).At("content"));
            Check(content.Select(Json.Map).Any(item => Json.Text(item.At("type")) == "image" && Json.Text(Json.Map(item.At("source")).At("data")) == Convert.ToBase64String(File.ReadAllBytes(picture))), "Claude sends actual image bytes");
            Check(content.Select(Json.Map).Any(item => Json.Text(item.At("text")).Contains(document)), "Claude document path reaches model");
            Check(content.Select(Json.Map).Any(item => Json.Text(item.At("text")).Contains("ACTIVE_GROUP_CONTEXT")) && !claude.Transcript.Entries.OfType<UserMessage>().Last().Text.Contains("ACTIVE_GROUP_CONTEXT"), "Claude receives scoped context without expanding user chat bubble");
            Check(claudeCompleted == 1 && codexCompleted == 1, "Completed-turn signals exclude interruptions");
            Check(claude.Transcript.Entries.OfType<AssistantText>().Single().Text == "Claude fixture reply", "Claude streaming protocol preserved");
            chosenEffort = "low";
            claude.SetEffort("low");
            Check(claudeWorker.WaitForExit(2000), "Claude closes its wrapper child process");
            Check(claude.Send("Continue with less effort"), "Claude accepts turn after effort change");
            Until(claude, () => claude.Status == "idle");
            var resumedArgs = Json.List(Json.Parse(File.ReadAllText(Path.Combine(folder, "claude-arguments.json")))).Select(Json.Text).ToList();
            Check(resumedArgs[resumedArgs.IndexOf("--effort") + 1] == "low" && resumedArgs[resumedArgs.IndexOf("--resume") + 1] == "claude-session" && claude.Transcript.Entries.OfType<UserMessage>().Count() == 2, "Claude changes effort while preserving and resuming conversation");
            chosenEffort = "";
            claude.SetEffort("default");
            claude.Warm(); Until(claude, () => File.ReadAllText(Path.Combine(folder, "claude-arguments.json")).IndexOf("--effort") < 0);
            Check(!File.ReadAllText(Path.Combine(folder, "claude-arguments.json")).Contains("--effort"), "Claude Default removes explicit effort override");

            // History: a kept conversation is listed by its first message and continues through the provider's own id.
            ChatHistory.Folder = Path.Combine(folder, "history");
            var firstMessage = new Dictionary<string, object> { ["kind"] = "user", ["text"] = "Make the roof steeper\n\nAttached: notes.txt", ["time"] = 1.0 };
            var kept = new SavedChat
            {
                Id = ChatHistory.NewId(), Provider = "claude", SessionId = "claude-from-history",
                Title = ChatHistory.Title(new[] { firstMessage }), Updated = ChatHistory.Now,
                Entries = new List<Dictionary<string, object>> { firstMessage, new Dictionary<string, object> { ["kind"] = "text", ["text"] = "Done", ["streaming"] = false } },
            };
            ChatHistory.Save(kept);
            var listed = ChatHistory.List();
            Check(listed.Count == 1 && Json.Text(Json.Map(listed[0]).At("title")) == "Make the roof steeper" && Json.Text(Json.Map(listed[0]).At("id")) == kept.Id, "History lists a kept conversation by its first message");
            var reopened = ChatHistory.Load(kept.Id);
            Check(reopened != null && reopened.Provider == "claude" && reopened.SessionId == "claude-from-history" && reopened.Entries.Count == 2 && Json.Text(reopened.Entries[1].At("text")) == "Done", "History keeps the shown entries and the provider's own id");
            Check(ChatHistory.Load("..\\escape") == null, "History ids cannot name files outside its folder");
            claude.Resume(reopened.SessionId);
            Check(claude.Transcript.Entries.Count == 0 && claude.ConversationId == "claude-from-history" && claude.Status == "idle", "Resume opens an empty transcript on the saved conversation");
            Check(claude.Send("Continue from history"), "Claude accepts a message in a conversation reopened from History");
            Until(claude, () => claude.Status == "idle" && claude.Transcript.Entries.OfType<AssistantText>().Any());
            var historyArgs = Json.List(Json.Parse(File.ReadAllText(Path.Combine(folder, "claude-arguments.json")))).Select(Json.Text).ToList();
            Check(historyArgs.IndexOf("--resume") >= 0 && historyArgs[historyArgs.IndexOf("--resume") + 1] == "claude-from-history", "Claude continues a History conversation with --resume and its session id");
            claude.Close();
            File.Delete(picture);
            Check(!codex.Send("Missing image", files), "Deleted attachment is rejected before send");
            Check(codex.Transcript.Entries.OfType<UserMessage>().Count() == 2, "Rejected attachments do not create phantom user messages");
            codex.Reset();
            Check(codex.Transcript.Entries.Count == 0 && claude.Transcript.Entries.Count > 0, "Provider transcripts remain isolated");
            Check(codex.Send("MCP approval fixture"), "MCP fixture starts");
            Until(codex, () => codex.Transcript.Pending);
            var mcpApproval = codex.Transcript.Entries.OfType<Approval>().Single();
            Check(mcpApproval.ToolName == "Run Rhino Command" && mcpApproval.Description.Contains("_Box") && Json.Text(mcpApproval.Input.At("command")).StartsWith("_Box"), "MCP approval shows requested Rhino command using actual installed CLI metadata");
            codex.Answer(mcpApproval.RequestId, "deny");
            Until(codex, () => codex.Status == "idle");
            var denied = Json.Map(Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder,"mcp-approval-response.json")))).At("result"));
            Check(Json.Text(denied.At("action")) == "decline" && codex.Transcript.Entries.OfType<ToolCall>().Last().Result.Contains("rejected"), "MCP denial preserves actual error details");
            Check(!codex.Transcript.McpConnectionFailed, "User denial is not mistaken for a broken connection");
            codex.Send("MCP approval fixture"); Until(codex, () => codex.Transcript.Pending);
            codex.Answer(codex.Transcript.Entries.OfType<Approval>().Last().RequestId, "always");
            Until(codex, () => codex.Status == "idle");
            var accepted = Json.Map(Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder,"mcp-approval-response.json")))).At("result"));
            Check(Json.Text(accepted.At("action")) == "accept" && Json.Text(Json.Map(accepted.At("_meta")).At("persist")) == "session", "MCP allow-for-chat uses elicitation response and session-only persistence");
            Check(codex.Transcript.Entries.OfType<ToolCall>().Last().Result == "Box created", "Successful MCP results remain visible");
            codex.Send("MCP transport failure"); Until(codex, () => codex.Status == "idle");
            Check(codex.Transcript.McpConnectionFailed, "MCP transport failure triggers reconnect on next send");
            codex.Resume("codex-from-history");
            Check(codex.Transcript.Entries.Count == 0 && codex.ConversationId == "codex-from-history", "Codex Resume opens an empty transcript on the saved thread");
            Check(codex.Send("Continue Codex from history"), "Codex accepts a message in a conversation reopened from History");
            Until(codex, () => codex.Status == "waiting" || (codex.Status == "idle" && codex.Transcript.Entries.OfType<AssistantText>().Any()));
            foreach (Approval asked in codex.Transcript.Entries.OfType<Approval>().Where(a => a.Status == "pending").ToList()) codex.Answer(asked.RequestId, "deny");
            Until(codex, () => codex.Status == "idle");
            var historyThread = Json.Map(Json.Parse(File.ReadAllText(Path.Combine(folder, "thread-request.json"))));
            Check(Json.Text(historyThread.At("method")) == "thread/resume" && Json.Text(Json.Map(historyThread.At("params")).At("threadId")) == "codex-from-history", "Codex continues a History conversation with thread/resume and its thread id");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { codex.Close(); claude.Close(); codexWorker?.Dispose(); claudeWorker?.Dispose(); }
    }
}
