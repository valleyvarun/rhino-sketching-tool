using System;
using System.Reflection;
using System.Net.Sockets;
using Rhino;
using Rhino.PlugIns;

namespace RhinoSketchingTool
{
    /// <summary>
    /// McNeel's Rhino MCP Platform, identified by its stable plug-in ID. No compile-time
    /// dependency: Crayon still loads and sketches when the optional package is absent.
    /// All entry points run on Rhino's UI thread, as required by RhinoMcpHost.
    /// </summary>
    internal static class RhinoMcpConnection
    {
        internal static readonly Guid PluginId = new Guid("2668d7ed-f507-4a68-8295-8172147a0e39");
        internal const string InstallHint = "Install RhinoMCP from Rhino's Package Manager: search for Rhino-MCP-Platform (by McNeel), install it, then restart Rhino. Crayon will connect automatically.";
        internal static string Message { get; private set; } = "";
        internal static string Endpoint { get; private set; } = "";
        internal static bool Missing { get; private set; }
        // A restarted server can reuse its port. Clients still need a fresh MCP transport.
        internal static int Generation { get; private set; }

        internal static bool CheckInstalled()
        {
            Missing = !PlugIn.PlugInExists(PluginId, out bool loaded, out bool protectedLoad);
            Message = Missing ? InstallHint : protectedLoad ? "RhinoMCP is disabled. Enable Rhino MCP Platform in Rhino Options > Plug-ins, then click Retry." : "";
            return !Missing && !protectedLoad;
        }

        internal static string PrepareEndpoint()
        {
            Endpoint = "";
            try
            {
                if (!CheckInstalled()) return "";
                RhinoDoc doc = RhinoDoc.ActiveDoc;
                if (doc == null) { Message = "Open a Rhino document to connect RhinoMCP."; return ""; }
                if (PlugIn.Find(PluginId) == null && !PlugIn.LoadPlugIn(PluginId, true, false))
                    throw new InvalidOperationException("Rhino could not load Rhino MCP Platform. Check Rhino's command history.");
                Assembly assembly = PlugIn.Find(PluginId)?.GetType().Assembly;
                // The package was renamed internally in 0.4; its plug-in ID and public host API stayed stable.
                Type host = assembly?.GetType("Rhino.AI.RhinoAIHost") ?? assembly?.GetType("RhMcp.RhinoMcpHost");
                if (host == null)
                    throw new InvalidOperationException("Update Rhino-MCP-Platform in Package Manager, then restart Rhino.");

                var portArgs = new object[] { doc, 0 };
                bool running = (bool)Invoke(host, "TryGetPortFor", portArgs);
                if (running && !Listening((int)portArgs[1]))
                {
                    Invoke(host, "Stop", new object[] { doc });
                    running = false;
                }
                if (!running)
                {
                    var next = new object[] { 0 };
                    if (!(bool)Invoke(host, "TryGetNextPort", next) || !(bool)Invoke(host, "Start", new object[] { doc, next[0] }))
                        throw new InvalidOperationException("RhinoMCP could not start its local server. Click Retry to reconnect.");
                    Generation++;
                    portArgs = new object[] { doc, 0 };
                    if (!(bool)Invoke(host, "TryGetPortFor", portArgs))
                        throw new InvalidOperationException("RhinoMCP did not return a local connection. Click Retry.");
                }
                int port = (int)portArgs[1];
                if (port < 1 || port > 65535) throw new InvalidOperationException("RhinoMCP returned an invalid port.");
                // Direct per-document HTTP avoids the router selecting another open Rhino.
                // /agent is the platform's endpoint specifically for in-Rhino assistants.
                Endpoint = "http://localhost:" + port + "/agent";
                Message = "";
            }
            catch (Exception ex)
            {
                Message = "RhinoMCP connection unavailable. " + (ex.InnerException ?? ex).Message;
            }
            return Endpoint;
        }

        static object Invoke(Type host, string method, object[] args)
        {
            MethodInfo member = host.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (member == null) throw new InvalidOperationException("Update Rhino-MCP-Platform in Package Manager, then restart Rhino.");
            return member.Invoke(null, args);
        }

        static bool Listening(int port)
        {
            if (port < 1 || port > 65535) return false;
            try
            {
                using (var client = new TcpClient())
                    return client.ConnectAsync("127.0.0.1", port).Wait(250) && client.Connected;
            }
            catch (Exception) { return false; }
        }

        internal static void OnStartup(object sender, EventArgs e)
        {
            RhinoApp.Idle -= OnStartup;
            if (!CheckInstalled()) RhinoApp.WriteLine("Crayon: " + Message);
        }

        internal static void OpenPackageManager() => RhinoApp.RunScript("_PackageManager", false);
    }
}
