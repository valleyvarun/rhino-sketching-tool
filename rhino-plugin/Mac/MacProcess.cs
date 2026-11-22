#if MAC
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Where things are on a Mac, and how the assistants' command line programs are found
    /// and started there. (The Windows ways are in ClaudeCli, CodexCli and ChildProcess.)
    /// </summary>
    internal static class MacProcess
    {
        public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>
        /// ~/Library/Application Support, where a Mac app keeps its own files: Crayon's chat
        /// history and its scratch folder go in "Crayon" there. Spelled out, because what .NET
        /// calls the application data folder on a Mac differs from one .NET to the next.
        /// </summary>
        public static string ApplicationSupport => Path.Combine(Home, "Library", "Application Support");

        [DllImport("libc", SetLastError = true)] static extern int access(string path, int mode);
        const int MayExecute = 1; // X_OK

        /// <summary>True for a file this user may run. Programs have no telltale extension on a Mac.</summary>
        public static bool IsProgram(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false; // a folder is not a file
                return access(path, MayExecute) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The folders command line programs are usually installed to, likeliest first: the
        /// native installers' ~/.local/bin, Homebrew's (Apple silicon, then Intel), and those
        /// of npm and its relatives (the newest Node of nvm first).
        /// </summary>
        public static IEnumerable<string> ProgramFolders()
        {
            string home = Home;
            yield return Path.Combine(home, ".local", "bin");
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return Path.Combine(home, ".npm-global", "bin");
            yield return Path.Combine(home, ".volta", "bin");
            yield return Path.Combine(home, ".bun", "bin");

            var nodes = new List<KeyValuePair<Version, string>>();
            try
            {
                string versions = Path.Combine(home, ".nvm", "versions", "node");
                if (Directory.Exists(versions))
                    foreach (string folder in Directory.GetDirectories(versions))
                    {
                        Version.TryParse(Path.GetFileName(folder).TrimStart('v'), out Version version);
                        nodes.Add(new KeyValuePair<Version, string>(version ?? new Version(0, 0), Path.Combine(folder, "bin")));
                    }
            }
            catch (Exception)
            {
                // No nvm, or no looking into it.
            }
            foreach (KeyValuePair<Version, string> node in nodes.OrderByDescending(item => item.Key)) yield return node.Value;
            yield return Path.Combine(home, "bin");
        }

        /// <summary>The first of the folders of a PATH that holds a program of this name, or null.</summary>
        public static string OnPath(string name, string search)
        {
            if (string.IsNullOrEmpty(search)) return null;
            foreach (string folder in search.Split(':'))
            {
                if (folder.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(folder, name);
                    if (IsProgram(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A PATH entry that is not a path.
                }
            }
            return null;
        }

        static string shellPath;
        static bool shellAsked;

        /// <summary>
        /// PATH as the user's own shell has it, or "" if the shell would not say.
        ///
        /// A Mac app started from the Dock or Finder gets only the system's folders in its
        /// PATH: what the user's shell profile adds (Homebrew, a Node manager, ~/.local/bin)
        /// is not there. Asking the shell starts it once, which can take a moment, so this is
        /// only done when a program is not in any of the usual places, and the answer is kept.
        /// </summary>
        public static string ShellPath
        {
            get
            {
                if (shellAsked) return shellPath ?? "";
                shellAsked = true;
                try
                {
                    string shell = Environment.GetEnvironmentVariable("SHELL");
                    if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "/bin/zsh";
                    // As a login and interactive shell, so that it reads the same profile files a Terminal
                    // window does. printenv also works with shells whose PATH is a list.
                    var info = new ProcessStartInfo(shell)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    info.ArgumentList.Add("-ilc");
                    info.ArgumentList.Add("/usr/bin/printf '\\nCRAYON_PATH='; /usr/bin/printenv PATH");
                    using (Process asked = Process.Start(info))
                    {
                        asked.StandardInput.Close(); // a profile that asks a question gets no answer, and carries on
                        string found = null;
                        var reading = new Thread(() =>
                        {
                            try
                            {
                                string line;
                                while ((line = asked.StandardOutput.ReadLine()) != null)
                                    if (line.StartsWith("CRAYON_PATH=", StringComparison.Ordinal)) found = line.Substring("CRAYON_PATH=".Length);
                            }
                            catch (Exception) { }
                        }) { IsBackground = true, Name = "crayon-shell-path" };
                        var errors = new Thread(() => { try { asked.StandardError.ReadToEnd(); } catch (Exception) { } }) { IsBackground = true, Name = "crayon-shell-errors" };
                        reading.Start();
                        errors.Start();
                        if (!asked.WaitForExit(5000))
                        {
                            try { asked.Kill(true); } catch (Exception) { }
                        }
                        if (reading.Join(1000)) shellPath = found;
                    }
                }
                catch (Exception)
                {
                    // No shell to ask: the usual places will have to do.
                }
                return shellPath ?? "";
            }
        }

        /// <summary>
        /// Gives a program about to be started a PATH it can work with: its own folder first
        /// (a program installed with npm is a script that starts "node", which sits beside it),
        /// then what Rhino has, the usual places, and the shell's own if that has been asked for.
        /// </summary>
        static string SearchPath(string program)
        {
            return string.Join(":", new[] { Path.GetDirectoryName(program) }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                .Concat(ProgramFolders()).Concat((shellPath ?? "").Split(':'))
                .Where(folder => !string.IsNullOrEmpty(folder)).Distinct(StringComparer.Ordinal));
        }

        public static void Configure(ProcessStartInfo info, IEnumerable<string> arguments)
        {
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            info.Environment["PATH"] = SearchPath(info.FileName);
        }

        // One argument as a shell wants it, whatever it holds.
        static string Quoted(string argument) => "'" + argument.Replace("'", "'\\''") + "'";

        /// <summary>
        /// Terminal runs this script interactively and leaves the command's exit code for Rhino.
        /// </summary>
        internal static string TerminalScript(string[] command, string folder, string heading, string done)
        {
            var text = new StringBuilder();
            text.Append("#!/bin/sh\n");
            // However the script ends, it says so: closing the window half way counts as not finishing.
            text.Append("done_file=" + Quoted(done) + "\n");
            text.Append("code=130\n");
            text.Append("trap 'printf \"%s\" \"$code\" > \"$done_file.tmp\" && /bin/mv -f \"$done_file.tmp\" \"$done_file\"' EXIT\n");
            text.Append("trap 'exit 129' HUP INT TERM\n");
            text.Append("clear\n");
            text.Append("echo " + Quoted(heading) + "\n");
            text.Append("echo\n");
            text.Append("unset CLAUDECODE\n");
            text.Append("export PATH=" + Quoted(SearchPath(command[0])) + ":\"$PATH\"\n");
            if (!string.IsNullOrEmpty(folder)) text.Append("cd " + Quoted(folder) + " || exit 1\n");
            text.Append(string.Join(" ", command.Select(Quoted)) + "\n");
            text.Append("code=$?\n");
            text.Append("echo\n");
            text.Append("if [ \"$code\" = 0 ]; then echo 'Done. You can close this window and go back to Rhino.'; ");
            text.Append("else echo \"That did not finish (code $code). Close this window and try again from Crayon.\"; fi\n");
            return text.ToString();
        }

        public static Func<int> RunInTerminal(string[] command, string folder, string heading)
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Claude sign-in requires macOS Terminal.");
            string script = Path.Combine(Path.GetTempPath(), "crayon-" + Guid.NewGuid().ToString("N") + ".command");
            string done = script + ".done";
            try
            {
                File.WriteAllText(script, TerminalScript(command, folder, heading, done), new UTF8Encoding(false));
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                open.ArgumentList.Add("-b");
                open.ArgumentList.Add("com.apple.Terminal");
                open.ArgumentList.Add(script);
                using (Process started = Process.Start(open))
                {
                    if (!started.WaitForExit(10000) || started.ExitCode != 0)
                        throw new InvalidOperationException("Terminal could not be opened.");
                }
            }
            catch
            {
                try { File.Delete(script); } catch (IOException) { }
                throw;
            }

            return () =>
            {
                try
                {
                    // Until the script has said how it ended. Signing in can take a while; an hour is giving up.
                    for (int waited = 0; waited < 3600 * 2 && !File.Exists(done); waited++) Thread.Sleep(500);
                    if (!File.Exists(done)) return -1;
                    return int.TryParse(File.ReadAllText(done).Trim(), out int code) ? code : -1;
                }
                catch (Exception)
                {
                    return -1;
                }
                finally
                {
                    try { File.Delete(script); } catch (Exception) { }
                    try { File.Delete(done); } catch (Exception) { }
                }
            };
        }
    }
}
#endif
