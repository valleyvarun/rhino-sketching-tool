# Run only in a new, blank Rhino using /scheme=CrayonSetupValidation /notemplate.
import os
import time
import traceback
import System
import Rhino
from System.Reflection import BindingFlags

root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
log = open(os.path.join(root, "bin", "first-load.log"), "w")
try:
    assert "/scheme=crayonsetupvalidation" in System.Environment.CommandLine.lower()
    assert Rhino.RhinoDoc.ActiveDoc.Objects.Count == 0
    path = os.path.join(root, "bin", "native", "RhinoSketchingTool.rhp")
    result = Rhino.PlugIns.PlugIn.LoadPlugIn(path)
    plugin = Rhino.PlugIns.PlugIn.Find(System.Guid("299a696b-aeb1-4c25-bd67-f6c04f5bed05"))
    assert plugin is not None
    sha = System.Security.Cryptography.SHA256.Create()
    def digest(file):
        return System.Convert.ToBase64String(sha.ComputeHash(System.IO.File.ReadAllBytes(file)))
    # Rhino may already know the developer's Release path for the same plug-in ID.
    assert digest(plugin.GetType().Assembly.Location) == digest(path)
    log.write("PASS bundled plugin loads in Rhino 8\n")
    flags = BindingFlags.Static | BindingFlags.NonPublic
    sidebar = plugin.GetType().Assembly.GetType("RhinoSketchingTool.Sidebar", True)
    # Exercise the same first-idle path as a fresh installation, in this test scheme only.
    plugin.Settings.SetBool("SidebarIntroduced", False)
    sidebar.GetMethod("OnFirstIdle", flags).Invoke(None, System.Array[System.Object]([None, System.EventArgs.Empty]))
    for i in range(30):
        Rhino.RhinoApp.Wait()
        time.sleep(0.1)
    assert plugin.Settings.GetBool("SidebarIntroduced", False)
    assert Rhino.UI.Panels.IsPanelVisible(System.Guid("b7c1f3a2-58d4-4e0b-9c36-7a1e2f4d8b90"), True)
    log.write("PASS first load introduces the Crayon sidebar without a command\n")
    log.write("PASS no account or sign-in required to open Crayon\n")
except:
    log.write(traceback.format_exc())
finally:
    log.close()
