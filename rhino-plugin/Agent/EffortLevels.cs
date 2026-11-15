using System.Collections.Generic;
using System.Linq;

namespace RhinoSketchingTool
{
    internal static class EffortLevels
    {
        internal static ModelChoice Model(List<ModelChoice> models, string chosen, string current)
        {
            if (!string.IsNullOrEmpty(chosen) && chosen != "default")
                return models.FirstOrDefault(m => m.Value == chosen || m.Resolved == chosen);
            return models.FirstOrDefault(m => m.Value == "default" && m.Efforts.Count > 0)
                ?? models.FirstOrDefault(m => m.Value != "default" && (m.Value == current || m.Resolved == current))
                ?? models.FirstOrDefault(m => m.IsDefault);
        }

        internal static string Label(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "default") return "Default";
            if (value == "xhigh") return "Extra high";
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        internal static string ForTurn(List<ModelChoice> models, string chosen, string current, string effort)
        {
            var model = Model(models, chosen, current);
            return !string.IsNullOrEmpty(effort) && (model == null || model.Efforts.Contains(effort))
                ? effort : model?.DefaultEffort ?? "";
        }
    }
}
