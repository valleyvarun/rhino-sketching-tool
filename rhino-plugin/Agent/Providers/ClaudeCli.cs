using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Finds Claude Code on this computer. The Agent tab runs the user's own copy, with
    /// the user's own sign-in. It looks, in order, at: a file the user chose, PATH, the
    /// standalone installs, the Claude desktop app, and the Claude Code extension of
    /// VS Code and its relatives (newest first).
    /// </summary>
    internal static class ClaudeCli
    {
        public const string InstallHint =
            "Claude Code was not found. Install it from https://claude.com/claude-code (the Claude desktop app and the " +
            "Claude Code extension for VS Code also include it), then try again, or choose claude.exe with Locate Claude Code.";

        /// <summary>
        /// The command that starts Claude Code: the program, then any arguments that must
        /// come first. Null when it cannot be found.
        /// </summary>
        public static string[] Find(string chosen)
        {
            if (string.IsNullOrEmpty(chosen)) chosen = Environment.GetEnvironmentVariable("CRAYON_CLAUDE_PATH");
            string[] command = string.IsNullOrEmpty(chosen) ? null : Command(chosen);
            if (command != null) return command;

            // A chosen copy can disappear, as when the desktop app updates: then the usual places.
            foreach (string candidate in Candidates())
            {
                command = Command(candidate);
                if (command != null) return command;
            }
            return null;
        }

        /// <summary>How to start Claude Code from this file. Null when the file cannot start it.</summary>
        public static string[] Command(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".exe") return new[] { path };
                if (extension == ".cmd" || extension == ".bat") return FromNpmShim(path);
            }
            catch (Exception)
            {
                // A path that cannot even be looked at is not Claude Code.
            }
            return null;
        }

        // npm installs a .cmd shim. Its target is run directly: cmd.exe would mangle the arguments.
        static string[] FromNpmShim(string shim)
        {
            string folder = Path.GetDirectoryName(shim) ?? "";
            string package = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code");
            var targets = new List<string>();
            try
            {
                // The last path the shim names is what it runs.
                MatchCollection named = Regex.Matches(File.ReadAllText(shim), "\"%~?dp0%?\\\\([^\"]+)\"");
                for (int i = named.Count - 1; i >= 0; i--)
                {
                    string target = named[i].Groups[1].Value;
                    if (Path.GetFileName(target).Equals("node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(Path.Combine(folder, target));
                    break;
                }
            }
            catch (IOException)
            {
            }
            targets.Add(Path.Combine(package, "bin", "claude.exe"));
            targets.Add(Path.Combine(package, "claude.exe"));
            targets.Add(Path.Combine(package, "cli.js"));

            string node = Path.Combine(folder, "node.exe");
            if (!File.Exists(node)) node = OnPath("node.exe", Environment.GetEnvironmentVariable("PATH"));
            foreach (string target in targets)
            {
                if (!File.Exists(target)) continue;
                string extension = Path.GetExtension(target).ToLowerInvariant();
                if (extension == ".exe") return new[] { target };
                if (node != null && (extension == ".js" || extension == ".mjs" || extension == ".cjs")) return new[] { node, target };
            }
            return null;
        }

        static IEnumerable<string> Candidates()
        {
            // PATH as this process has it, then as Windows has it saved (Claude Code may
            // have been installed after Rhino started).
            foreach (string search in new[] { Environment.GetEnvironmentVariable("PATH"), SavedPath() })
            {
                string found = OnPath("claude.exe", search) ?? OnPath("claude.cmd", search);
                if (found != null) yield return found;
            }

            // The native installer, npm's global folder, and WinGet's links.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(home, ".local", "bin", "claude.exe");
            yield return Path.Combine(roaming, "npm", "claude.cmd");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe");

            // Copies that come with other apps, newest version first.
            var bundled = new List<KeyValuePair<Version, string>>();
            bundled.AddRange(DesktopCopies(local, roaming));
            bundled.AddRange(EditorCopies(home));
            foreach (KeyValuePair<Version, string> copy in bundled.OrderByDescending(item => item.Key))
                yield return copy.Value;
        }

        static string OnPath(string name, string search)
        {
            if (string.IsNullOrEmpty(search)) return null;
            foreach (string folder in search.Split(Path.PathSeparator))
            {
                try
                {
                    string trimmed = folder.Trim().Trim('"');
                    if (trimmed.Length == 0) continue;
                    string candidate = Path.Combine(trimmed, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A PATH entry that is not a path.
                }
            }
            return null;
        }

        static string SavedPath()
        {
            var entries = new List<string>();
            try
            {
                using (RegistryKey user = Registry.CurrentUser.OpenSubKey("Environment"))
                    if (user?.GetValue("Path") is string value) entries.Add(value);
                using (RegistryKey machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                    if (machine?.GetValue("Path") is string value) entries.Add(value);
            }
            catch (Exception)
            {
                // No access to the registry: PATH as the process has it will have to do.
            }
            return string.Join(Path.PathSeparator.ToString(), entries);
        }

        // The Claude desktop app keeps copies such as Claude-Data\claude_...\claude-code\2.1.321\<id>\claude.exe.
        static IEnumerable<KeyValuePair<Version, string>> DesktopCopies(string local, string roaming)
        {
            var roots = new List<string>();
            try
            {
                string data = Path.Combine(local, "Claude-Data");
                if (Directory.Exists(data))
                    foreach (string package in Directory.GetDirectories(data)) roots.Add(Path.Combine(package, "claude-code"));
            }
            catch (Exception)
            {
            }
            roots.Add(Path.Combine(roaming, "Claude", "claude-code"));

            var found = new List<KeyValuePair<Version, string>>();
            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string version in Directory.GetDirectories(root))
                    {
                        var places = new List<string> { version };
                        places.AddRange(Directory.GetDirectories(version));
                        foreach (string place in places)
                        {
                            string candidate = Path.Combine(place, "claude.exe");
                            if (File.Exists(candidate)) found.Add(new KeyValuePair<Version, string>(VersionIn(Path.GetFileName(version)), candidate));
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return found;
        }

        // The Claude Code extension of VS Code and its relatives brings its own claude.exe.
        static IEnumerable<KeyValuePair<Version, string>> EditorCopies(string home)
        {
            var found = new List<KeyValuePair<Version, string>>();
            foreach (string editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            {
                try
                {
                    string root = Path.Combine(home, editor, "extensions");
                    if (!Directory.Exists(root)) continue;
                    foreach (string folder in Directory.GetDirectories(root, "anthropic.claude-code-*"))
                    {
                        string candidate = Path.Combine(folder, "resources", "native-binary", "claude.exe");
                        if (File.Exists(candidate)) found.Add(new KeyValuePair<Version, string>(VersionIn(Path.GetFileName(folder)), candidate));
                    }
                }
                catch (Exception)
                {
                }
            }
            return found;
        }

        // The version in a folder name: anthropic.claude-code-2.1.319-win32-x64, or 2.1.321 alone.
        static Version VersionIn(string name)
        {
            Match match = Regex.Match(name, @"(?:^|claude-code-)(\d+(?:\.\d+){1,3})");
            return match.Success && Version.TryParse(match.Groups[1].Value, out Version version) ? version : new Version(0, 0);
        }

        /// <summary>
        /// One argument as Windows wants it on a command line, so that it arrives whole
        /// whatever spaces, quotes and backslashes it holds.
        /// </summary>
        public static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '\v', '"' }) < 0) return argument;
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"') quoted.Append('\\', backslashes * 2 + 1);
                else quoted.Append('\\', backslashes);
                quoted.Append(c);
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
