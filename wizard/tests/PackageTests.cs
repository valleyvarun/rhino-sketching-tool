using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CrayonSetup;
using Microsoft.Win32;

// Replacing an installed Crayon, against a scratch package folder and a scratch registry key.
// Nothing here touches Rhino's real packages or its real plug-in registrations.
static class PackageTests
{
    const string Version = "1.2.3";
    static readonly byte[] NewPlugin = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
    static string scratch, rhinoKey, hash;
    static int scenario;
    // The two places Rhino keeps what it knows about the plug-in, under the scratch key.
    static string Registration => rhinoKey + @"\Plug-Ins\" + CrayonPackage.PluginId;
    static string Options => rhinoKey + @"\Global Options\Plug-ins\" + CrayonPackage.PluginId;

    public static void Run(Action<bool, string> check)
    {
        scratch = Path.Combine(Path.GetTempPath(), "CrayonSetupTests-" + Guid.NewGuid().ToString("N"));
        string registryScratch = @"Software\CrayonSetupTests-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(scratch);
        string reference = Path.Combine(scratch, "reference.rhp");
        File.WriteAllBytes(reference, NewPlugin);
        hash = CrayonPackage.Hash(reference);
        try
        {
            // 1. A computer that never had Crayon.
            var p = Fresh(registryScratch, out string root);
            p.Install(Payload);
            check(p.Problem() == null && Folders(root).SequenceEqual(new[] { Version }) && File.ReadAllText(Manifest(root)) == Version + "\n",
                "First install leaves one active version, in Rhino's own layout");
            check(Registered() == null, "First install leaves registering the plug-in to Rhino");

            // 2. An older version, which Rhino has loaded before.
            p = Fresh(registryScratch, out root);
            Register(Existing(root, "0.9.0", "old", active: true));
            p.Install(Payload);
            check(p.Problem() == null && Folders(root).SequenceEqual(new[] { Version }), "An older version is replaced and removed");
            check(Same(Registered(), p.Plugin), "Rhino's registration follows the new version");

            // 3. A newer version: installing an earlier download must still win.
            p = Fresh(registryScratch, out root);
            Register(Existing(root, "9.0.0", "newer", active: true));
            p.Install(Payload);
            check(p.Problem() == null && Folders(root).SequenceEqual(new[] { Version }) && Same(Registered(), p.Plugin), "A newer version is replaced by the one being installed");

            // 4. The same version number with other contents (a rebuilt release).
            p = Fresh(registryScratch, out root);
            Register(Existing(root, Version, "same number, other build", active: true));
            File.WriteAllText(Path.Combine(root, CrayonPackage.Name, Version, "left-over.txt"), "from the other build");
            p.Install(Payload);
            check(p.Problem() == null && Folders(root).SequenceEqual(new[] { Version }), "The same version with other contents is replaced");
            check(!File.Exists(Path.Combine(root, CrayonPackage.Name, Version, "left-over.txt")), "Files of the replaced build do not linger in the version folder");

            // 5. Running the same setup again.
            p.Install(Payload); p.Install(Payload);
            check(p.Problem() == null && Folders(root).SequenceEqual(new[] { Version }), "Installing the same setup again changes nothing");

            // 6. The remains of interrupted installs and several stale versions.
            p = Fresh(registryScratch, out root);
            Existing(root, "0.1.0", "a", active: false); Existing(root, "0.2.0", "b", active: false);
            Existing(root, ".incoming-dead", "c", active: false); Existing(root, ".outgoing-dead", "d", active: false);
            File.WriteAllText(Manifest(root), "0.3.0\n"); // names a version that is not there
            p.Install(Payload);
            check(p.Problem() == null && Folders(root).SequenceEqual(new[] { Version }), "Stale versions and interrupted installs are cleared away");

            // 7. Crayon registered from another folder: a developer build, or a hand-installed .rhp.
            p = Fresh(registryScratch, out root);
            string elsewhere = Path.Combine(scratch, "dev-build", CrayonPackage.PluginFile);
            Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)); File.WriteAllText(elsewhere, "developer build");
            Register(elsewhere);
            Existing(root, "0.9.0", "old", active: true);
            p.Install(Payload);
            check(p.Problem() == null && Same(Registered(), p.Plugin), "A Crayon registered from another folder gives way to the package");
            check(File.Exists(elsewhere), "The other folder's file is left alone");
            using (var key = Registry.CurrentUser.OpenSubKey(Registration))
                check((string)key.GetValue("Name") == "RhinoSketchingTool" && (int)key.GetValue("LoadMode") == 1, "The rest of Rhino's registration is kept");

            // 7b. Rhino was told never to load Crayon (what it records is "2", as text).
            p = Fresh(registryScratch, out root);
            Register(Existing(root, "0.9.0", "old", active: true));
            using (var key = Registry.CurrentUser.CreateSubKey(Options)) key.SetValue("LoadProtection", "2");
            p.Install(Payload);
            check(Protection() == null && p.Problem() == null, "A Crayon that Rhino was told never to load is loaded again");
            using (var key = Registry.CurrentUser.OpenSubKey(Options)) check(key == null, "Nothing is left behind of that instruction");

            // 7c. Other things Rhino keeps beside it stay, and so does "load without asking".
            p = Fresh(registryScratch, out root);
            using (var key = Registry.CurrentUser.CreateSubKey(Options)) { key.SetValue("LoadProtection", "2"); key.SetValue("Something else", "kept"); }
            p.Install(Payload);
            using (var key = Registry.CurrentUser.OpenSubKey(Options))
                check(key != null && (string)key.GetValue("Something else") == "kept" && key.GetValue("LoadProtection") == null, "Rhino's other options for the plug-in are kept");
            using (var key = Registry.CurrentUser.CreateSubKey(Options)) key.SetValue("LoadProtection", "1");
            p.Install(Payload);
            check(Protection() == "1" && p.Problem() == null, "Load without asking is left as it is");

            // 7d. A registration left by an early build that loaded on demand.
            p = Fresh(registryScratch, out root);
            Register(Existing(root, "0.9.0", "old", active: true));
            using (var key = Registry.CurrentUser.OpenSubKey(Registration, true)) key.SetValue("LoadMode", 2, RegistryValueKind.DWord);
            p.Install(Payload);
            using (var key = Registry.CurrentUser.OpenSubKey(Registration))
                check((int)key.GetValue("LoadMode") == 1 && p.Problem() == null, "Crayon loads when Rhino starts, whatever the previous build did");

            // 8. A stale version that cannot be deleted right now does not stop the install.
            p = Fresh(registryScratch, out root);
            string stale = Existing(root, "0.9.0", "old", active: true);
            using (File.Open(stale, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                p.Install(Payload);
                check(p.Problem() == null && Directory.Exists(Path.GetDirectoryName(stale)), "A stale version that is in use is left for Rhino to remove");
            }
            p.Install(Payload);
            check(Folders(root).SequenceEqual(new[] { Version }), "It is removed by the next install once it is free");

            // 9. The same version still in use: nothing is replaced, and nothing is broken.
            p = Fresh(registryScratch, out root);
            string busy = Existing(root, Version, "in use", active: true);
            bool refused = false;
            using (File.Open(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { p.Install(Payload); } catch (InvalidOperationException e) { refused = e.Message.Contains("still in use"); }
            }
            check(refused, "A version that is still in use is reported, with what to do");
            check(File.ReadAllText(busy) == "in use" && Folders(root).SequenceEqual(new[] { Version }) && File.ReadAllText(Manifest(root)).Trim() == Version,
                "After that failure the previous Crayon is intact and still active");
            p.Install(Payload);
            check(p.Problem() == null, "Retry succeeds once it is free");

            // 10. A damaged download never replaces a working Crayon.
            p = Fresh(registryScratch, out root);
            string working = Existing(root, "0.9.0", "working", active: true);
            bool damaged = false;
            try { p.Install(() => Package(new byte[] { 1, 2, 3 })); } catch (InvalidOperationException) { damaged = true; }
            check(damaged && File.ReadAllText(working) == "working" && File.ReadAllText(Manifest(root)).Trim() == "0.9.0" && Folders(root).SequenceEqual(new[] { "0.9.0" }),
                "A damaged setup leaves the installed Crayon untouched");
            bool escaped = false;
            try { p.Install(() => Package(NewPlugin, "../outside.txt")); } catch (InvalidOperationException) { escaped = true; }
            check(escaped && !File.Exists(Path.Combine(root, CrayonPackage.Name, "outside.txt")) && Folders(root).SequenceEqual(new[] { "0.9.0" }), "A package cannot write outside its own folder");

            // 11. Verification notices anything that would make Rhino load something else.
            p = Fresh(registryScratch, out root);
            p.Install(Payload);
            File.WriteAllText(p.Plugin, "tampered");
            check(Throws(p.Verify), "Verification fails when the installed plug-in has other contents");
            p.Install(Payload);
            File.WriteAllText(Manifest(root), "0.9.0\n");
            check(Throws(p.Verify), "Verification fails when another version is the active one");
            p.Install(Payload);
            Register(elsewhere);
            check(Throws(p.Verify), "Verification fails when Rhino has Crayon registered elsewhere");
            p.Install(Payload);
            using (var key = Registry.CurrentUser.CreateSubKey(Options)) key.SetValue("LoadProtection", "2");
            check(Throws(p.Verify), "Verification fails when Rhino has been told never to load Crayon");
            p.Install(Payload);
            check(!Throws(p.Verify), "Verification passes after a clean install");
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
            Registry.CurrentUser.DeleteSubKeyTree(registryScratch, false);
        }
    }

    // Each scenario gets its own empty package folder and its own empty registry key.
    static CrayonPackage Fresh(string registryScratch, out string root)
    {
        scenario++;
        root = Path.Combine(scratch, "packages-" + scenario);
        Directory.CreateDirectory(root);
        rhinoKey = registryScratch + @"\Rhino-" + scenario;
        return new CrayonPackage(root, rhinoKey, Version, hash);
    }
    static Stream Payload() => Package(NewPlugin);
    static Stream Package(byte[] plugin, string extraEntry = null)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Add(zip, "manifest.yml", System.Text.Encoding.UTF8.GetBytes("name: crayon-rhino\nversion: " + Version + "\n"));
            Add(zip, "crayon-mark.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            Add(zip, CrayonPackage.PluginFile, plugin);
            if (extraEntry != null) Add(zip, extraEntry, new byte[] { 1 });
        }
        memory.Position = 0;
        return memory;
    }
    static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using (var entry = zip.CreateEntry(name).Open()) entry.Write(bytes, 0, bytes.Length);
    }
    static string Existing(string root, string version, string contents, bool active)
    {
        string folder = Path.Combine(root, CrayonPackage.Name, version);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, CrayonPackage.PluginFile);
        File.WriteAllText(file, contents);
        if (active) File.WriteAllText(Manifest(root), version + "\n");
        return file;
    }
    static string Manifest(string root) => Path.Combine(root, CrayonPackage.Name, "manifest.txt");
    static string[] Folders(string root) => Directory.GetDirectories(Path.Combine(root, CrayonPackage.Name)).Select(Path.GetFileName).OrderBy(n => n).ToArray();
    // The way Rhino records a plug-in it has loaded.
    static void Register(string file)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Registration))
        {
            key.SetValue("Name", "RhinoSketchingTool"); key.SetValue("LoadMode", 1, RegistryValueKind.DWord);
            using (var plugin = key.CreateSubKey("PlugIn")) plugin.SetValue("FileName", file);
        }
    }
    static string Registered()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Registration + "\\PlugIn")) return key?.GetValue("FileName") as string;
    }
    static string Protection()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Options)) return key?.GetValue("LoadProtection") as string;
    }
    static bool Same(string a, string b) => a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
}
