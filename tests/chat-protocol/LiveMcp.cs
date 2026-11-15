using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using RhinoSketchingTool;

internal static class LiveMcp
{
    // Deliberately uses the production session adapter, including approval responses.
    // Run only with the fixture created by mcp_in_rhino.py in its isolated blank Rhino.
    internal static int Run(string fixture)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(fixture));
        string log = Path.Combine(folder, "mcp-model.log");
        File.WriteAllText(log, "");
        Action<string> report = line => File.AppendAllText(log, line + Environment.NewLine);
        var config = Json.Map(Json.Parse(File.ReadAllText(fixture)));
        string macro = string.Join(" ", Enumerable.Range(0,10).Select(i => "_Box " + (i*20) + ",0,0 " + (i*20+10) + ",10,0 10"));
        var launch = new Launch { Command = Json.List(config.At("codex")).Select(Json.Text).ToArray(),
            Folder = folder, McpUrl = Json.Text(config.At("url")),
            SystemPrompt = "You are testing Crayon in an isolated blank Rhino document. Only use crayon_rhino tools. No shell tools, other servers or subagents. Execute the requested exact macro once and stop. Do not retry mutations." };
        var session = new CodexSession(() => launch);
        try
        {
            if (!session.Send("Use the crayon_rhino run_command tool once with this exact command to create ten test boxes: " + macro)) throw new Exception("Send failed");
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            bool approved = false;
            while (DateTime.UtcNow < deadline)
            {
                session.Poll();
                foreach (var approval in session.Transcript.Entries.OfType<Approval>().Where(a=>a.Status == "pending").ToArray())
                {
                    // The harness is authorized only to create these boxes in its own blank test document.
                    bool exact = (approval.ToolName.Contains("run_command") || approval.ToolName == "Run Rhino Command")
                        && Json.Text(approval.Input.At("command")) == macro;
                    if (!exact)
                    {
                        var field = typeof(CodexSession).GetField("mcpApprovals", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var pending = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string,object>>)field.GetValue(session);
                        if (pending.TryGetValue(approval.RequestId, out var raw)) report("REQUEST " + Json.Write(raw));
                    }
                    session.Answer(approval.RequestId, exact ? "allow" : "deny");
                    report((exact ? "PASS approved exact test macro: " : "DENIED unexpected request: ") + approval.ToolName);
                    if (exact) approved = true;
                }
                if (session.Status == "idle") break;
                Thread.Sleep(15);
            }
            foreach (var item in session.Transcript.Entries)
            {
                if (item is ToolCall call) report(call.Name + " " + call.Status + ": " + call.Result);
                if (item is Notice notice) report(notice.Text);
                if (item is AssistantText text) report(text.Text);
            }
            if (session.Status != "idle" || !approved || !session.Transcript.Entries.OfType<ToolCall>().Any(c=>c.Name == "run_command" && c.Status == "done"))
                throw new Exception("Expected a successful, explicitly approved modeling tool call");
            report("PASS real Codex model turn through production adapter and MCP approval");
            return 0;
        }
        catch (Exception ex) { report(ex.ToString()); return 1; }
        finally { session.Close(); }
    }
}
