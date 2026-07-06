using System.Drawing;

// Shared helpers for the four OLED-safe restyle schemes (Typographic, AmberHud, WarmCard, Phosphor)
// added in 1.0.3.44. BuildMetricPanels() and DrawGraph() are reused as-is (same data, same sparkline
// geometry) - only the panel chrome and the graph's own accent colors change, because
// AddMetricPanel hardcodes DesignTokens.Colors.Accent (blue) for the CPU panel and similar per-metric
// accents for others, none of which are guaranteed blue-free.
internal sealed partial class WidgetForm
{
    private static Color[] RemapGraphColors(Color[] original, Color primary, Color secondary)
    {
        if (original == null || original.Length == 0)
        {
            return new Color[] { primary };
        }

        Color[] remapped = new Color[original.Length];
        for (int i = 0; i < original.Length; i++)
        {
            remapped[i] = i == 0 ? primary : secondary;
        }

        return remapped;
    }

    // Panel text lines are ["hardware name", "METRIC NN%", "detail", optional 4th line]; the middle
    // line is the compact always-present headline every metric panel has, so it is what the OLED
    // schemes show as their single value line instead of all 3-4 Classic lines.
    private static string GetPanelHeadline(MetricPanel panel)
    {
        if (panel.TextLines == null || panel.TextLines.Length == 0)
        {
            return "--";
        }

        int index = panel.TextLines.Length > 1 ? 1 : 0;
        return string.IsNullOrEmpty(panel.TextLines[index]) ? "--" : panel.TextLines[index];
    }

    private static OledVariantPainting.Severity GetPanelSeverity(MetricPanel panel)
    {
        if (panel.AlertIconVisible)
        {
            return OledVariantPainting.Severity.Danger;
        }

        return panel.AlertPercent > 0.0 ? OledVariantPainting.Severity.Warn : OledVariantPainting.Severity.Neutral;
    }
}
