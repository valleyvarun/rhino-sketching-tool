using System.Collections.Generic;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    // Per-process configuration: never edits the user's Claude or Codex config files.
    internal static class McpConfiguration
    {
        internal static bool IsConnectionFailure(string message)
        {
            string lower = (message ?? "").ToLowerInvariant();
            return lower.Contains("transport send error") || lower.Contains("connection refused")
                || lower.Contains("error sending request for url") || lower.Contains("not connected")
                || lower.Contains("fetch failed") || lower.Contains("connection closed");
        }

        internal const string ServerName = "crayon_rhino";

        internal static IEnumerable<string> ClaudeArguments(string url)
        {
            if (string.IsNullOrEmpty(url)) yield break;
            yield return "--mcp-config";
            yield return Json.Write(new JsonMap { ["mcpServers"] = new JsonMap
            {
                [ServerName] = new JsonMap { ["type"] = "http", ["url"] = url }
            }});
        }

        internal static IEnumerable<string> CodexArguments(string url)
        {
            if (string.IsNullOrEmpty(url)) yield break;
            yield return "-c";
            // JSON strings are also TOML basic strings; the endpoint is a loopback URL.
            yield return "mcp_servers." + ServerName + "={url=" + Json.Write(url) + ",enabled=true,startup_timeout_sec=30}";
        }
    }
}
