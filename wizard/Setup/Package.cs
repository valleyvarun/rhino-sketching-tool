using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace CrayonSetup
{
    /// <summary>
    /// Puts the bundled Crayon package in Rhino's package folder so that it is the one Crayon
    /// Rhino loads, whatever was there before: an older or a newer version, the same version
    /// with other contents, the remains of an interrupted install, or a Crayon that Rhino has
    /// registered from some other folder (a developer build, a hand-installed .rhp).
    ///
    /// What Rhino 8 goes by when it starts (observed in Rhino 8.32), all of it set here:
    ///  - packages\8.0\crayon-rhino\manifest.txt names the active version folder. Rhino loads
    ///    the plug-in from there, ignores every other folder beside it, and removes those
    ///    the next time it starts.
    ///  - HKCU\…\8.0\Global Options\Plug-ins\{id}\LoadProtection. "2" means never load: it is
    ///    what Rhino records once it has been told to stop loading a plug-in (RhinoCommon's
    ///    PlugIn.SetLoadProtection). Rhino then skips Crayon for good, however often it is
    ///    installed again.
    ///  - HKCU\…\8.0\Plug-Ins\{id} is Rhino's record of the build it loaded last: its file and
    ///    when to load it. Rhino corrects the file itself when a package is installed, but
    ///    setup does not leave that to chance.
    /// Rhino must be closed: it holds the installed plug-in file open while it runs.
    /// </summary>
    internal sealed class CrayonPackage
    {
        public const string Name = "crayon-rhino";
        public const string PluginFile = "RhinoSketchingTool.rhp";
        public const string PluginId = "299a696b-aeb1-4c25-bd67-f6c04f5bed05";
        public const string Rhino8 = @"Software\McNeel\Rhinoceros\8.0";

        readonly string folder, version, hash, registration, options;
        readonly Action<string> log;

        /// <param name="packageRoot">Rhino's package folder for this user (…\packages\8.0).</param>
        /// <param name="rhinoKey">Rhino 8's settings, a key under HKEY_CURRENT_USER.</param>
        /// <param name="hash">SHA-256 of the plug-in file inside the bundled package.</param>
        public CrayonPackage(string packageRoot, string rhinoKey, string version, string hash, Action<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(version) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Trim('.').Length == 0)
                throw new ArgumentException("Not a package version: " + version);
            folder = Path.Combine(packageRoot, Name);
            registration = rhinoKey + @"\Plug-Ins\" + PluginId;
            options = rhinoKey + @"\Global Options\Plug-ins\" + PluginId;
            this.version = version; this.hash = hash;
            this.log = log ?? (text => { });
        }

        string VersionFolder => Path.Combine(folder, version);
        string Manifest => Path.Combine(folder, "manifest.txt");
        /// <summary>The plug-in file Rhino loads once this package is installed.</summary>
        public string Plugin => Path.Combine(VersionFolder, PluginFile);

        /// <summary>
        /// Replaces any installed Crayon with the package read from <paramref name="payload"/>.
        /// Until the new version is complete on disk the previous one stays in place and
        /// active, so a failure here never leaves Rhino without a Crayon it had before.
        /// </summary>
        public void Install(Func<Stream> payload)
        {
            Directory.CreateDirectory(folder);
            string run = Guid.NewGuid().ToString("N").Substring(0, 8);
            // Both sit beside the installed versions, so putting the new one in place is a
            // rename on the same drive. To Rhino they are inactive versions: never loaded.
            string incoming = Path.Combine(folder, ".incoming-" + run), outgoing = Path.Combine(folder, ".outgoing-" + run);
            try
            {
                Unpack(payload, incoming);
                string unpacked = Path.Combine(incoming, PluginFile);
                if (!File.Exists(unpacked) || Hash(unpacked) != hash)
                    throw new InvalidOperationException("This copy of Crayon Setup is damaged. Download it again.");

                bool sameVersion = Directory.Exists(VersionFolder);
                if (sameVersion)
                {
                    log("Replacing the contents of installed version " + version);
                    try { Retry(() => Directory.Move(VersionFolder, outgoing)); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException("The installed Crayon is still in use. Close Rhino (and anything else that has Crayon's files open), then click Retry.", e);
                    }
                }
                try
                {
                    Retry(() => Directory.Move(incoming, VersionFolder));
                    Retry(() => File.WriteAllText(Manifest, version + "\n"));
                }
                catch
                {
                    // Put things back the way Rhino last saw them.
                    if (sameVersion && Directory.Exists(outgoing))
                    {
                        Quietly(() => { if (Directory.Exists(VersionFolder)) Directory.Delete(VersionFolder, true); });
                        Quietly(() => Directory.Move(outgoing, VersionFolder));
                    }
                    else if (!sameVersion) Quietly(() => { if (Directory.Exists(VersionFolder)) Directory.Delete(VersionFolder, true); });
                    throw;
                }
            }
            finally { Quietly(() => { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); }); }

            // The new version is active from here on. What is left is tidying up, and none of
            // it can make Rhino load anything else, so none of it is allowed to fail the install.
            foreach (string other in Directory.GetDirectories(folder))
            {
                if (string.Equals(Path.GetFileName(other), version, StringComparison.OrdinalIgnoreCase)) continue;
                try { Retry(() => Directory.Delete(other, true), 3); log("Removed " + Path.GetFileName(other)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log("Left " + Path.GetFileName(other) + " for Rhino to remove (" + e.Message + ")");
                }
            }
            Register();
            log("Crayon " + version + " is installed: " + Plugin);
        }

        /// <summary>
        /// Makes what Rhino has recorded about the plug-in agree with the package. Rhino writes
        /// these keys itself, so on a computer that never had Crayon there is nothing to do.
        /// </summary>
        void Register()
        {
            // "2" is never load. Without the value Rhino loads the plug-in as usual, which is
            // what installing it again asks for. "1" (load without asking) can stay.
            bool empty = false;
            using (var key = Registry.CurrentUser.OpenSubKey(options, true))
            {
                string protection = key?.GetValue("LoadProtection")?.ToString();
                if (protection != null && protection != LoadSilently)
                {
                    log("Rhino had been told not to load Crayon (load protection " + protection + "); cleared");
                    key.DeleteValue("LoadProtection", false);
                    empty = key.ValueCount == 0 && key.SubKeyCount == 0;
                }
            }
            if (empty) Registry.CurrentUser.DeleteSubKey(options, false);

            using (var key = Registry.CurrentUser.OpenSubKey(registration, true))
            {
                if (key == null) return;
                using (var file = key.CreateSubKey("PlugIn"))
                {
                    string registered = file.GetValue("FileName") as string;
                    if (!SamePath(registered, Plugin))
                    {
                        log("Rhino had Crayon registered at " + (registered ?? "(nothing)") + "; now " + Plugin);
                        file.SetValue("FileName", Plugin, RegistryValueKind.String);
                    }
                }
                // Crayon loads when Rhino starts (SketchPlugin.LoadTime, AtStartup = 1). Rhino
                // goes by what it recorded for the build it loaded last: with an early build's
                // load-on-demand (2) still recorded, it does not load this one at start-up.
                if (!(key.GetValue("LoadMode") is int mode) || mode != LoadAtStartup)
                {
                    log("Rhino had Crayon's load mode as " + (key.GetValue("LoadMode") ?? "(nothing)") + "; now " + LoadAtStartup);
                    key.SetValue("LoadMode", LoadAtStartup, RegistryValueKind.DWord);
                }
            }
        }
        const int LoadAtStartup = 1;
        const string LoadSilently = "1";

        /// <summary>Throws unless Rhino's next start will load exactly the bundled plug-in.</summary>
        public void Verify()
        {
            string problem = Problem();
            if (problem == null) return;
            log("Verification failed: " + problem);
            throw new InvalidOperationException("Crayon's installed files did not pass verification. Click Retry.");
        }

        /// <summary>Why Rhino would not load the bundled plug-in, or null when it will.</summary>
        internal string Problem()
        {
            if (!File.Exists(Manifest)) return "no manifest.txt";
            string active = File.ReadAllText(Manifest).Trim();
            if (!string.Equals(active, version, StringComparison.OrdinalIgnoreCase)) return "the active version is " + active;
            if (!File.Exists(Plugin)) return "the plug-in file is missing";
            if (Hash(Plugin) != hash) return "the plug-in file has other contents";
            using (var file = Registry.CurrentUser.OpenSubKey(registration + "\\PlugIn"))
            {
                string registered = file?.GetValue("FileName") as string;
                if (registered != null && !SamePath(registered, Plugin)) return "Rhino has Crayon registered at " + registered;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(options))
            {
                string protection = key?.GetValue("LoadProtection")?.ToString();
                if (protection != null && protection != LoadSilently) return "Rhino has been told not to load Crayon (load protection " + protection + ")";
            }
            return null;
        }

        static void Unpack(Func<Stream> payload, string destination)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            using (var stream = payload())
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This copy of Crayon Setup is damaged. Download it again.");
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                }
        }

        static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return false; }
        }

        internal static string Hash(string path)
        {
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }

        // A virus scanner or the search indexer can hold a new file for a moment.
        static void Retry(Action action, int attempts = 8)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { action(); return; }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < attempts) { Thread.Sleep(250); }
            }
        }

        static void Quietly(Action action)
        {
            try { action(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
