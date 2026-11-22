# Run from Rhino 8 for Mac with RunPythonScript, in a new blank model.
import time

import Rhino
import System
from System.Reflection import BindingFlags


def wait_for(check, description):
    until = time.time() + 15
    while time.time() < until:
        Rhino.RhinoApp.Wait()
        value = check()
        if value:
            print("PASS " + description)
            return value
        time.sleep(0.05)
    raise RuntimeError("Timed out waiting for " + description)


def field(obj, name):
    return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj)


def main():
    doc = Rhino.RhinoDoc.ActiveDoc
    if not Rhino.Runtime.HostUtils.RunningOnOSX or doc is None or doc.Path or doc.Objects.Count:
        raise RuntimeError("Open a new blank model in Rhino 8 for Mac to run this check.")
    if not Rhino.RhinoApp.RunScript("_Crayon", False):
        raise RuntimeError("Rhino could not run Crayon. Check the installed package and restart Rhino.")
    plugin = Rhino.PlugIns.PlugIn.Find(System.Guid("299a696b-aeb1-4c25-bd67-f6c04f5bed05"))
    if plugin is None or plugin.GetType().Assembly.GetType("RhinoSketchingTool.MacDisplay") is None:
        raise RuntimeError("The loaded Crayon plug-in is not the Mac build.")
    print("Loaded " + plugin.GetType().Assembly.Location)
    panel_id = System.Guid("b7c1f3a2-58d4-4e0b-9c36-7a1e2f4d8b90")
    panel = wait_for(lambda: Rhino.UI.Panels.GetPanel(panel_id, doc), "document panel registered and opened")
    header = field(panel, "header")
    settings = field(panel, "settings")
    agent = field(panel, "agentView")
    if header is None or settings is None or field(agent, "web") is None:
        raise RuntimeError("Crayon opened the fallback controls. Check command history for a WebKit error.")
    wait_for(lambda: field(header, "ready"), "WebKit header loaded")

    # Click the actual HTML controls. The native panel changes only if JS -> Eto works.
    for tab in ("sketch", "layers", "agent"):
        field(header, "web").ExecuteScript("document.querySelector('[data-tab=%s]').click();" % tab)
        wait_for(lambda: field(panel, "tab") == tab, "header selected " + tab)
        if tab != "agent":
            wait_for(lambda: field(settings, "ready"), "settings page loaded")
            # The body learns the selected tab through the opposite direction of the bridge.
            script = "return document.getElementById('%s').hidden ? 'hidden' : 'shown';" % tab
            wait_for(lambda: field(settings, "web").ExecuteScript(script) == "shown", tab + " controls displayed")
        else:
            wait_for(lambda: field(agent, "ready"), "agent page loaded")

    toolbar = plugin.Controller.GetType().GetProperty("Toolbar", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin.Controller, None)
    doc.Views.Redraw()
    wait_for(lambda: field(toolbar, "picture") is not None and not field(toolbar, "broken"),
             "viewport toolbar bitmap created during redraw")
    print("Check the visible toolbar and draw a stroke to confirm the Metal output.")


main()
