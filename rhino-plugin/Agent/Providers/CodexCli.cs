using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if !MAC
using Microsoft.Win32;
#endif

namespace RhinoSketchingTool
{
    internal static class CodexCli
    {
        public const string InstallHint = "Codex was not found. Install Codex CLI or the Codex extension for VS Code, then use Locate Codex in the chat settings.";

        public static string[] Find(string chosen)
        {
            if (string.IsNullOrEmpty(chosen)) chosen = Environment.GetEnvironmentVariable("CRAYON_CODEX_PATH");
            var paths = new List<string>();
            if (!string.IsNullOrWhiteSpace(chosen)) paths.Add(chosen);
#if MAC
            // PATH as Rhino has it (only the system's folders, when started from the Dock or Finder),
            // the usual places, Codex's own folder, and the copy the Codex extension of VS Code brings.
            string home = MacProcess.Home;
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                if (folder.Length > 0) paths.Add(Path.Combine(folder, "codex"));
            foreach (string folder in MacProcess.ProgramFolders()) paths.Add(Path.Combine(folder, "codex"));
            paths.Add(Path.Combine(home, ".codex", "bin", "codex"));
            // An Apple silicon Mac runs either build; an Intel Mac only its own.
            bool silicon = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            string[] builds = silicon ? new[] { "macos-aarch64", "darwin-arm64", "macos-x86_64", "darwin-x64" } : new[] { "macos-x86_64", "darwin-x64" };
            foreach (string editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            {
                try
                {
                    string extensions = Path.Combine(home, editor, "extensions");
                    if (!Directory.Exists(extensions)) continue;
                    foreach (string extension in Directory.GetDirectories(extensions, "openai.chatgpt-*").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                        foreach (string build in builds) paths.Add(Path.Combine(extension, "bin", build, "codex"));
                }
                catch (Exception) { }
            }
            foreach (string path in paths)
            {
                string[] command = ClaudeCli.Command(path);
                if (command != null) return command;
            }
            // Last, PATH as the user's own shell has it: asking the shell takes a moment.
            string found = MacProcess.OnPath("codex", MacProcess.ShellPath);
            return found != null ? new[] { found } : null;
#else
            var searches = new List<string> { Environment.GetEnvironmentVariable("PATH") ?? "" };
            try
            {
                using (var user = Registry.CurrentUser.OpenSubKey("Environment")) searches.Add(user?.GetValue("Path") as string ?? "");
                using (var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")) searches.Add(machine?.GetValue("Path") as string ?? "");
            }
            catch (Exception) { }
            foreach (string search in searches)
                foreach (string folder in search.Split(Path.PathSeparator).Where(part => !string.IsNullOrWhiteSpace(part)))
                {
                    try
                    {
                        paths.Add(Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')), "codex.exe"));
                        paths.Add(Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')), "codex.cmd"));
                    }
                    catch (ArgumentException) { }
                }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));
            paths.Add(Path.Combine(home, ".local", "bin", "codex.exe"));
            foreach (string editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            {
                try
                {
                    string extensions = Path.Combine(home, editor, "extensions");
                    if (!Directory.Exists(extensions)) continue;
                    foreach (string extension in Directory.GetDirectories(extensions, "openai.chatgpt-*").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(Path.Combine(extension, "bin", "windows-x86_64", "codex.exe"));
                        paths.Add(Path.Combine(extension, "bin", "windows-aarch64", "codex.exe"));
                    }
                }
                catch (Exception) { }
            }
            foreach (string path in paths)
            {
                string[] command = ClaudeCli.Command(path);
                if (command != null) return command;
            }
            return null;
#endif
        }
    }
}
