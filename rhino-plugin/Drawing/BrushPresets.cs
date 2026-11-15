using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace RhinoSketchingTool
{
    /// <summary>User brush library. Presets include each independent pressure channel.</summary>
    internal static class BrushPresets
    {
        static List<JsonMap> presets;
        static List<JsonMap> Read()
        {
            if (presets != null) return presets;
            try
            {
                presets = Json.List(Json.Parse(SketchPlugin.Instance.Settings.GetString("BrushPresetsV1", "[]")))
                    .Select(Json.Map).Where(p => Json.Text(p.At("id")).Length > 0 && Json.Text(p.At("name")).Length > 0).ToList();
            }
            catch { presets = new List<JsonMap>(); }
            if (presets.Count == 0)
            {
                presets.Add(Default("pencil", "Pencil", "#23262C", 3, 1, .8, true, true));
                presets.Add(Default("marker", "Marker", "#1F5FD1", 6, .8, .9, false, true));
                presets.Add(Default("crayon", "Crayon", "#9731B0", 6, .8, .6, true, false));
                presets.Add(Default("redline", "Redline", "#D0352B", 2, 1, .9, false, true));
            }
            return presets;
        }
        static JsonMap Default(string id, string name, string color, double size, double opacity, double hardness, bool pressure, bool snap) => new JsonMap
        { ["id"] = id, ["name"] = name, ["color"] = color, ["size"] = size, ["opacity"] = opacity, ["hardness"] = hardness,
            ["pressure"] = .7, ["sizePressure"] = pressure, ["opacityPressure"] = pressure, ["hardnessPressure"] = false, ["snap"] = snap };
        internal static List<object> AsJson() => Read().Select(p => (object)p).ToList();
        static JsonMap Current(SketchController c, string id, string name) => new JsonMap
        { ["id"] = id, ["name"] = name, ["color"] = "#" + (c.StrokeColor.ToArgb() & 0xffffff).ToString("X6"), ["size"] = c.Brush.Size,
            ["opacity"] = c.Brush.Opacity, ["hardness"] = c.Brush.Hardness, ["pressure"] = c.Brush.Pressure,
            ["sizePressure"] = c.Brush.SizeByPressure, ["opacityPressure"] = c.Brush.OpacityByPressure,
            ["hardnessPressure"] = c.Brush.HardnessByPressure, ["snap"] = c.SnapShapes };
        internal static bool Edited(SketchController c)
        {
            var preset = Read().FirstOrDefault(p => Json.Text(p.At("id")) == c.Brush.Preset);
            if (preset == null) return true;
            var current = Current(c, c.Brush.Preset, Json.Text(preset.At("name")));
            return current.Any(pair => pair.Key != "name" && Json.Write(pair.Value) != Json.Write(preset.At(pair.Key)));
        }
        static void Save() => SketchPlugin.Instance.Settings.SetString("BrushPresetsV1", Json.Write(Read()));
        internal static void Apply(SketchController c, string id)
        {
            var p = Read().FirstOrDefault(item => Json.Text(item.At("id")) == id);
            if (p == null) return;
            c.Brush.Preset = id;
            c.Brush.Size = Number(p, "size", 6); c.Brush.Opacity = Number(p, "opacity", .8);
            c.Brush.Hardness = Number(p, "hardness", .6); c.Brush.Pressure = Number(p, "pressure", .7);
            c.Brush.SizeByPressure = Flag(p, "sizePressure"); c.Brush.OpacityByPressure = Flag(p, "opacityPressure");
            c.Brush.HardnessByPressure = Flag(p, "hardnessPressure"); c.SnapShapes = Flag(p, "snap");
            string color = Json.Text(p.At("color"));
            if (color.Length == 7 && int.TryParse(color.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                c.StrokeColor = Color.FromArgb(255, rgb >> 16, (rgb >> 8) & 255, rgb & 255);
            c.Tool = SketchTool.Draw;
        }
        static bool Flag(JsonMap p, string key) => p.At(key) is bool value && value;
        static double Number(JsonMap p, string key, double fallback) => double.TryParse(Convert.ToString(p.At(key), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsInfinity(v) && !double.IsNaN(v) ? v : fallback;
        internal static void Edit(SketchController c, string action, string id, string name)
        {
            var list = Read(); int index = list.FindIndex(p => Json.Text(p.At("id")) == id);
            if (action == "presetNew")
            {
                id = Guid.NewGuid().ToString("N"); c.Brush.Preset = id;
                list.Add(Current(c, id, string.IsNullOrWhiteSpace(name) ? "Brush " + (list.Count + 1) : name.Trim()));
            }
            else if (index >= 0)
            {
                if (action == "presetSave") list[index] = Current(c, id, Json.Text(list[index].At("name")));
                else if (action == "presetRename" && !string.IsNullOrWhiteSpace(name)) list[index]["name"] = name.Trim();
                else if (action == "presetDelete" && list.Count > 1)
                {
                    list.RemoveAt(index);
                    if (c.Brush.Preset == id) Apply(c, Json.Text(list[0].At("id")));
                }
            }
            Save();
        }
    }
}
