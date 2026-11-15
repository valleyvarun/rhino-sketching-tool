# Run only in a new /scheme=CrayonValidation /notemplate Rhino process.
# Tests the actual installed McNeel package and both local CLIs without a model turn.
import os
import time
import json
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
log = open(os.path.join(output, "mcp-rhino.log"), "w")
child = None
try:
    assert "/scheme=crayonvalidation" in System.Environment.CommandLine.lower()
    doc = Rhino.RhinoDoc.ActiveDoc
    assert doc is not None and doc.Objects.Count == 0
    assembly = Assembly.LoadFile(os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp"))
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
    def invoke(type_name, method, *args):
        return assembly.GetType("RhinoSketchingTool." + type_name, True).GetMethod(method, flags).Invoke(None, System.Array[System.Object](list(args)))
    assert invoke("RhinoMcpConnection", "CheckInstalled")
    endpoint = invoke("RhinoMcpConnection", "PrepareEndpoint")
    if not endpoint:
        plugin = Rhino.PlugIns.PlugIn.Find(System.Guid("2668d7ed-f507-4a68-8295-8172147a0e39"))
        log.write("Plugin type: " + plugin.GetType().FullName + "\nAssembly: " + plugin.GetType().Assembly.Location + "\n")
        for t in plugin.GetType().Assembly.GetTypes():
            if "Host" in t.Name or "Server" in t.Name:
                log.write(t.FullName + ": " + ", ".join(str(m) for m in t.GetMethods(flags)) + "\n")
        raise Exception(assembly.GetType("RhinoSketchingTool.RhinoMcpConnection").GetProperty("Message", flags).GetValue(None, None))
    assert invoke("RhinoMcpConnection", "PrepareEndpoint") == endpoint
    log.write("PASS installed dependency detected; local server starts automatically and is reused\n")
    config = {"url": str(endpoint), "claude": list(invoke("ClaudeCli", "Find", "") or []), "codex": list(invoke("CodexCli", "Find", "") or [])}
    config["claudeArgs"] = list(invoke("McpConfiguration", "ClaudeArguments", endpoint))
    config["codexArgs"] = list(invoke("McpConfiguration", "CodexArguments", endpoint))
    with open(os.path.join(output, "mcp-fixture.json"), "w") as f:
        json.dump(config, f)
    log.flush()
    info = System.Diagnostics.ProcessStartInfo("C:\\Program Files\\nodejs\\node.exe")
    info.Arguments = '"' + os.path.join(root, "tests", "Native", "verify-mcp.mjs") + '"'
    info.UseShellExecute = False
    info.CreateNoWindow = True
    child = System.Diagnostics.Process.Start(info)
    deadline = time.time() + 80
    while not child.HasExited and time.time() < deadline:
        Rhino.RhinoApp.Wait()
        time.sleep(.01)
    assert child.HasExited, "MCP test timed out"
    assert child.ExitCode == 0, "MCP client test failed; see mcp-clients.log"
    log.write("PASS actual HTTP MCP and assistant discovery\n")
    plugin = Rhino.PlugIns.PlugIn.Find(System.Guid("2668d7ed-f507-4a68-8295-8172147a0e39"))
    host = plugin.GetType().Assembly.GetType("Rhino.AI.RhinoAIHost") or plugin.GetType().Assembly.GetType("RhMcp.RhinoMcpHost")
    host.GetMethod("Stop", flags).Invoke(None, System.Array[System.Object]([doc]))
    endpoint = invoke("RhinoMcpConnection", "PrepareEndpoint")
    assert endpoint, "Stopped RhinoMCP did not restart automatically"
    config["url"] = str(endpoint)
    with open(os.path.join(output, "mcp-fixture.json"), "w") as f:
        json.dump(config, f)
    log.write("PASS automatic restart after RhinoMCP stops\n")
    log.flush()
    info = System.Diagnostics.ProcessStartInfo(os.path.join(root, "tests", "chat-protocol", "bin", "Release", "net48", "ChatProtocol.exe"))
    info.Arguments = '--live-mcp "' + os.path.join(output, "mcp-fixture.json") + '"'
    info.UseShellExecute = False
    info.CreateNoWindow = True
    child = System.Diagnostics.Process.Start(info)
    deadline = time.time() + 135
    while not child.HasExited and time.time() < deadline:
        Rhino.RhinoApp.Wait()
        time.sleep(.01)
    assert child.HasExited and child.ExitCode == 0, "Live model test failed; see mcp-model.log"
    assert doc.Objects.Count == 10, "Expected ten real boxes, got " + str(doc.Objects.Count)
    log.write("PASS ten actual boxes created through Codex and production approval handling\n")
    doc.Modified = False
except:
    log.write(traceback.format_exc())
finally:
    if child is not None and not child.HasExited:
        child.Kill()
    log.close()
