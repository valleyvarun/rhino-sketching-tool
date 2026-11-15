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
        Task EnsureWebView(CancellationToken token);
        Task EnsureMcp(CancellationToken token);
        Task EnsureAgent(string provider, CancellationToken token);
        Task InstallPlugin(CancellationToken token);
        void VerifyPlugin();
    }
    internal sealed class Installer
    {
        readonly ISetupHost host;
        public Installer(ISetupHost host) { this.host = host; }
        public async Task Install(SetupOptions options, Action<int, string> progress, CancellationToken token)
        {
            RequireRhinoClosed();
            progress(8, "Preparing the Crayon interface");
            await host.EnsureWebView(token);
            progress(25, "Connecting Rhino to your agents");
            await host.EnsureMcp(token);
            if (options.Claude) { progress(45, "Setting up Claude Code"); await host.EnsureAgent("claude", token); }
            if (options.Codex) { progress(65, "Setting up Codex"); await host.EnsureAgent("codex", token); }
            token.ThrowIfCancellationRequested();
            RequireRhinoClosed(); // It may have been opened during a download.
            progress(85, "Installing Crayon in Rhino 8");
            // Once Yak is writing a package, let it finish before allowing the window to close.
            await host.InstallPlugin(CancellationToken.None);
            host.VerifyPlugin();
            progress(100, "Crayon is installed");
        }
        void RequireRhinoClosed()
        {
            if (host.RhinoRunning) throw new InvalidOperationException("Save your work and close Rhino, then click Retry. Setup will never close your models for you.");
        }
    }
}
