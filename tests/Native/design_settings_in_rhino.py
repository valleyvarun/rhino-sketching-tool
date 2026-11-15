# Run in a new blank /scheme=CrayonValidation Rhino; test settings are restored.
import os
import traceback
import System
import Rhino
from System.Reflection import BindingFlags

root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
log = open(os.path.join(root, "tests", "Native", "bin", "design-settings.log"), "w")
plugin = None
try:
    assert "/scheme=crayonvalidation" in System.Environment.CommandLine.lower()
    plugin = Rhino.PlugIns.PlugIn.Find(System.Guid("299a696b-aeb1-4c25-bd67-f6c04f5bed05"))
    assert plugin is not None
    assembly = plugin.GetType().Assembly
    flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
    settings = plugin.Settings
    old_theme = settings.GetString("CrayonTheme", "system")
    old_presets = settings.GetString("BrushPresetsV1", "[]")
    def call(type_name, method, *args):
        return assembly.GetType("RhinoSketchingTool." + type_name, True).GetMethod(method, flags).Invoke(None, System.Array[System.Object](list(args)))
    theme = assembly.GetType("RhinoSketchingTool.CrayonAppearance", True)
    preset_type = assembly.GetType("RhinoSketchingTool.BrushPresets", True)
    cache = preset_type.GetField("presets", flags)
    def check(value, message):
        assert value, message
        log.write("PASS " + message + "\n")
        log.flush()
    settings.SetString("CrayonTheme", "light")
    rhino_dark = Rhino.Runtime.HostUtils.RunningInDarkMode
    call("CrayonAppearance", "Toggle")
    check(theme.GetProperty("Dark", flags).GetValue(None, None) and settings.GetString("CrayonTheme", "") == "dark", "Theme toggle persists dark mode")
    check(Rhino.Runtime.HostUtils.RunningInDarkMode == rhino_dark, "Crayon theme leaves Rhino appearance unchanged")
    call("CrayonAppearance", "Toggle")
    check(not theme.GetProperty("Dark", flags).GetValue(None, None), "Theme toggle returns to light mode")
    settings.SetString("BrushPresetsV1", "[]")
    cache.SetValue(None, None)
    controller = plugin.Controller
    call("BrushPresets", "Apply", controller, "crayon")
    check(not call("BrushPresets", "Edited", controller), "Selecting Crayon restores its stored brush")
    controller.Brush.Size = 9
    controller.Brush.SizeByPressure = False
    controller.Brush.OpacityByPressure = True
    controller.Brush.HardnessByPressure = True
    check(call("BrushPresets", "Edited", controller), "Changing brush or pressure marks preset edited")
    call("BrushPresets", "Edit", controller, "presetNew", "", "QA pressure brush")
    chosen = controller.Brush.Preset
    cache.SetValue(None, None)
    controller.Brush.Size = 2
    controller.Brush.HardnessByPressure = False
    call("BrushPresets", "Apply", controller, chosen)
    check(controller.Brush.Size == 9 and not controller.Brush.SizeByPressure and controller.Brush.OpacityByPressure and controller.Brush.HardnessByPressure,
          "Reloaded preset retains size and three independent pressure settings")
    call("BrushPresets", "Edit", controller, "presetRename", chosen, "Renamed QA")
    check("Renamed QA" in settings.GetString("BrushPresetsV1", ""), "Preset rename persists")
    call("BrushPresets", "Edit", controller, "presetDelete", chosen, "")
    check(chosen not in settings.GetString("BrushPresetsV1", ""), "Preset delete persists")
except:
    log.write(traceback.format_exc())
finally:
    if plugin is not None:
        try:
            settings.SetString("CrayonTheme", old_theme)
            settings.SetString("BrushPresetsV1", old_presets)
            cache.SetValue(None, None)
            call("BrushPresets", "Apply", controller, "crayon")
        except: pass
    log.close()
