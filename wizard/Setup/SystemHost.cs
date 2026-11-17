using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RhinoSketchingTool;

namespace CrayonSetup
{
    internal sealed class SystemHost : ISetupHost, IDisposable
    {
        public readonly string Rhino;
        public readonly string Work;
        public readonly string Log;
        readonly CrayonPackage package;
        string Yak => Path.Combine(Path.GetDirectoryName(Rhino), "Yak.exe");
        public SystemHost(string rhino)
        {
            Rhino = rhino;
            Work = Path.Combine(Path.GetTempPath(), "CrayonSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Work);
            string logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crayon", "Setup", "Logs");
            Directory.CreateDirectory(logRoot);
            Log = Path.Combine(logRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
            var info = ResourceText("package-info.txt").Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
            string packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McNeel", "Rhinoceros", "packages", "8.0");
            package = new CrayonPackage(packageRoot, CrayonPackage.Rhino8, info[0], info[1], text => File.AppendAllText(Log, text + "\r\n"));
        }
        public static string FindRhino()
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(@"SOFTWARE\McNeel\Rhinoceros\8.0\Install"))
            {
                var path = key?.GetValue("ExePath") as string;
                if (ValidRhino(path)) return path;
                var folder = key?.GetValue("Path") as string;
                if (folder != null && ValidRhino(Path.Combine(folder, "Rhino.exe"))) return Path.Combine(folder, "Rhino.exe");
            }
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rhino 8", "System", "Rhino.exe");
            return ValidRhino(fallback) ? fallback : null;
        }
        public static bool ValidRhino(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) && Path.GetFileName(path).Equals("Rhino.exe", StringComparison.OrdinalIgnoreCase)
                && FileVersionInfo.GetVersionInfo(path).FileMajorPart == 8 && File.Exists(Path.Combine(Path.GetDirectoryName(path), "Yak.exe"));
        }
        public bool RhinoRunning
        {
            get { var all = Process.GetProcessesByName("Rhino"); try { return all.Length > 0; } finally { foreach (var p in all) p.Dispose(); } }
        }
        // Rhino goes on running for up to a minute after its last window has closed.
        public bool RhinoClosing
        {
            get
            {
                var all = Process.GetProcessesByName("Rhino");
                try { return all.Length > 0 && all.All(p => { try { return p.MainWindowHandle == IntPtr.Zero; } catch (InvalidOperationException) { return true; } }); }
                finally { foreach (var p in all) p.Dispose(); }
            }
        }
        public static string[] AgentCommand(string provider) => provider == "claude" ? ClaudeCli.Find("") : CodexCli.Find("");
        public static bool HasWebView()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var clients = root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients"))
            {
                if (clients == null) continue;
                foreach (string name in clients.GetSubKeyNames())
                using (var key = clients.OpenSubKey(name))
                    if ((key?.GetValue("name") as string ?? "").IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0
                        && Version.TryParse(key.GetValue("pv") as string, out Version v) && v.Major > 0) return true;
            }
            return false;
        }
        public async Task EnsureWebView(CancellationToken token)
        {
            if (HasWebView()) return;
            string file = Path.Combine(Work, "MicrosoftEdgeWebview2Setup.exe");
            await Download("https://go.microsoft.com/fwlink/p/?LinkId=2124703", file, token);
            // Verify the downloaded bootstrapper's Authenticode signature before execution.
            string verify = "$s=Get-AuthenticodeSignature -LiteralPath '" + file.Replace("'", "''") + "'; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){exit 1}";
            await Run(new[] { PowerShell, "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(verify)) }, token);
            await Run(new[] { file, "/silent", "/install" }, token);
            if (!HasWebView()) throw new InvalidOperationException("WebView2 could not be verified. Restart Windows if an update is pending, then retry setup.");
        }
        public async Task EnsureMcp(CancellationToken token)
        {
            string installed = await Run(new[] { Yak, "list" }, token);
            if (!HasPackage(installed, "Rhino-MCP-Platform", new Version(0,4,0)))
                await Run(new[] { Yak, "install", "--source=https://yak.rhino3d.com", "Rhino-MCP-Platform" }, token);
            if (!HasPackage(await Run(new[] { Yak, "list" }, token), "Rhino-MCP-Platform", new Version(0,4,0)))
                throw new InvalidOperationException("RhinoMCP was not installed successfully. Check your internet connection and retry.");
        }
        internal static bool HasPackage(string list, string name, Version minimum)
        {
            var match = System.Text.RegularExpressions.Regex.Match(list, @"(?im)^" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s+\((\d+\.\d+\.\d+)(?:[^)]*)\)");
            return match.Success && Version.TryParse(match.Groups[1].Value, out Version v) && v >= minimum;
        }
        static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        public async Task EnsureAgent(string provider, CancellationToken token)
        {
            string[] command = AgentCommand(provider);
            if (command == null)
            {
                string script = Path.Combine(Work, provider + "-install.ps1");
                await Download(provider == "claude" ? "https://claude.ai/install.ps1" : "https://chatgpt.com/codex/install.ps1", script, token);
                await Run(new[] { PowerShell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }, token);
                command = AgentCommand(provider);
            }
            if (command == null) throw new InvalidOperationException("The " + provider + " installation could not be found. Retry setup after checking your internet connection.");
            await Run(command.Concat(new[] { "--version" }).ToArray(), token);
        }
        // Whatever Crayon is already there is replaced; see CrayonPackage. Off the window's thread.
        public Task InstallPlugin(CancellationToken token) => Task.Run(() =>
        {
            try { package.Install(() => Resource("plugin.yak")); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
                File.AppendAllText(Log, e + "\r\n");
                throw new InvalidOperationException("Crayon could not be put in Rhino's package folder. Close Rhino and anything else using Crayon's files, then click Retry. Details: " + Log, e);
            }
        });
        public void VerifyPlugin() => package.Verify();
        internal static string Hash(string path) => CrayonPackage.Hash(path);
        internal static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream("CrayonSetup." + name) ?? throw new InvalidOperationException("Setup is missing " + name + ". Download it again.");
        internal static string ResourceText(string name) { using (var s = Resource(name)) using (var r = new StreamReader(s)) return r.ReadToEnd(); }
        async Task Download(string url, string destination, CancellationToken token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(8) })
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage.RequestUri.Scheme != "https") throw new InvalidOperationException("The download did not use a secure connection.");
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(destination)) await input.CopyToAsync(output, 81920, token);
            }
        }
        public async Task<string> Run(string[] command, CancellationToken token)
        {
            var start = new ProcessStartInfo(command[0], string.Join(" ", command.Skip(1).Select(ClaudeCli.Quote)))
            { WorkingDirectory = Work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var process = new Process { StartInfo = start })
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                process.Start(); process.StandardInput.Close();
                using (timeout.Token.Register(() => ChildProcess.Stop(process)))
                {
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit());
                    string result = await stdout, error = await stderr;
                    timeout.Token.ThrowIfCancellationRequested();
                    File.AppendAllText(Log, Path.GetFileName(command[0]) + " (exit " + process.ExitCode + ")\r\n" + result + error + "\r\n");
                    if (process.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(command[0]) + " did not finish successfully. Check your connection and retry. Details: " + Log);
                    return result + error;
                }
            }
        }
        public void Dispose()
        {
            // Only this process's unique temporary directory, never the installed packages.
            try { if (Directory.Exists(Work)) Directory.Delete(Work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
