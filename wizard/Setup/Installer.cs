using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CrayonSetup
{
    internal sealed class SetupOptions { public bool Claude = true, Codex = true; }
    internal interface ISetupHost
    {
        bool RhinoRunning { get; }
        /// <summary>Rhino's windows are gone but its process has not ended yet.</summary>
        bool RhinoClosing { get; }
        Task EnsureWebView(CancellationToken token);
        Task EnsureMcp(CancellationToken token);
        Task EnsureAgent(string provider, CancellationToken token);
        Task InstallPlugin(CancellationToken token);
        void VerifyPlugin();
    }
    internal sealed class Installer
    {
        readonly ISetupHost host;
        /// <summary>How long to wait for a Rhino that is already closing, and how often to look.</summary>
        internal TimeSpan ClosingWait = TimeSpan.FromMinutes(2), ClosingPoll = TimeSpan.FromMilliseconds(500);
        public Installer(ISetupHost host) { this.host = host; }
        public async Task Install(SetupOptions options, Action<int, string> progress, CancellationToken token)
        {
            await RequireRhinoClosed(2, progress, token);
            progress(8, "Preparing the Crayon interface");
            await host.EnsureWebView(token);
            progress(25, "Connecting Rhino to your agents");
            await host.EnsureMcp(token);
            if (options.Claude) { progress(45, "Setting up Claude Code"); await host.EnsureAgent("claude", token); }
            if (options.Codex) { progress(65, "Setting up Codex"); await host.EnsureAgent("codex", token); }
            token.ThrowIfCancellationRequested();
            await RequireRhinoClosed(80, progress, token); // It may have been opened during a download.
            progress(85, "Installing Crayon in Rhino 8");
            // Once the package is being replaced, let that finish before allowing the window to close.
            await host.InstallPlugin(CancellationToken.None);
            host.VerifyPlugin();
            progress(100, "Crayon is installed");
        }
        async Task RequireRhinoClosed(int percent, Action<int, string> progress, CancellationToken token)
        {
            // Someone who closes Rhino and starts setup straight away should not be told to close Rhino.
            if (host.RhinoRunning && host.RhinoClosing)
            {
                progress(percent, "Waiting for Rhino to finish closing");
                var until = DateTime.UtcNow + ClosingWait;
                while (host.RhinoRunning && host.RhinoClosing && DateTime.UtcNow < until) await Task.Delay(ClosingPoll, token);
            }
            if (host.RhinoRunning) throw new InvalidOperationException("Save your work and close Rhino, then click Retry. Setup will never close your models for you.");
        }
    }
}
