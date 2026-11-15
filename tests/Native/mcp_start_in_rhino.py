# Checks the opening-time MCP startup path without any model requests.
import os
import traceback
import System
import Rhino
from System.Reflection import Assembly, BindingFlags

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
output = os.path.join(root, "tests", "Native", "bin")
System.IO.Directory.CreateDirectory(output)
log = open(os.path.join(output, "mcp-start.log"), "w")
try:
    assert "/scheme=crayonvalidation" in System.Environment.CommandLine.lower()
    doc = Rhino.RhinoDoc.ActiveDoc
    assert doc is not None and doc.Objects.Count == 0
    assembly = Assembly.LoadFile(os.path.join(root, "rhino-plugin", "bin", "Validation", "RhinoSketchingTool.rhp"))
    flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
    connection = assembly.GetType("RhinoSketchingTool.RhinoMcpConnection", True)
    agent = assembly.GetType("RhinoSketchingTool.Agent", True)
    def ensure(): agent.GetMethod("EnsureConnection", flags).Invoke(None, None)
    def prop(name): return connection.GetProperty(name, flags).GetValue(None, None)
    ensure()
    plugin = Rhino.PlugIns.PlugIn.Find(System.Guid("2668d7ed-f507-4a68-8295-8172147a0e39"))
    host = plugin.GetType().Assembly.GetType("Rhino.AI.RhinoAIHost") or plugin.GetType().Assembly.GetType("RhMcp.RhinoMcpHost")
    def stop(): host.GetMethod("Stop", flags).Invoke(None, System.Array[System.Object]([doc]))
    stop()
    before = prop("Generation")
    ensure()
    endpoint = prop("Endpoint")
    assert endpoint and prop("Generation") == before + 1
    log.write("PASS opening-time check starts a stopped RhinoMCP server\n")
    args = System.Array[System.Object]([doc, System.Int32(0)])
    assert host.GetMethod("TryGetPortFor", flags).Invoke(None, args)
    client = System.Net.Sockets.TcpClient()
    client.Connect("127.0.0.1", int(args[1]))
    client.Close()
    log.write("PASS server listens on the document-specific port\n")
    ensure()
    assert prop("Endpoint") == endpoint and prop("Generation") == before + 1
    log.write("PASS repeated opening reuses the healthy server\n")
    stop()
    ensure()
    assert prop("Endpoint") and prop("Generation") == before + 2
    log.write("PASS another stop/reopen triggers fresh transport generation\n")
except:
    log.write(traceback.format_exc())
finally:
    log.close()
