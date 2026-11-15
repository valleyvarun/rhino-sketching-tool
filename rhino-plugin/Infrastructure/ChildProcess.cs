using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RhinoSketchingTool
{
    /// <summary>Stops a process started by Crayon, including an npm wrapper's children.</summary>
    internal static class ChildProcess
    {
        // Call only with the Process returned by this session's Process.Start. Keeping
        // its handle open until taskkill finishes also prevents recycling its process ID.
        public static void Stop(Process owned)
        {
            if (owned == null) return;
            try
            {
                if (owned.HasExited) return;
                IntPtr heldHandle = owned.Handle;
                int id = owned.Id;
                var info = new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    "/PID " + id.ToString(CultureInfo.InvariantCulture) + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // Do not close stdin first: the wrapper could exit and orphan its
                // children before Windows has traversed the owned process tree.
                using (Process killer = Process.Start(info))
                {
                    killer.BeginOutputReadLine();
                    killer.BeginErrorReadLine();
                    if (!killer.WaitForExit(3000) || killer.ExitCode != 0)
                        throw new InvalidOperationException("Windows could not stop the process tree.");
                }
                GC.KeepAlive(heldHandle);
            }
            catch (Exception)
            {
                // taskkill may be unavailable under an organization policy. The
                // fallback still addresses only the exact process this session owns.
                try { if (!owned.HasExited) owned.Kill(); } catch (Exception) { }
            }
            finally
            {
                owned.Dispose();
            }
        }
    }
}
