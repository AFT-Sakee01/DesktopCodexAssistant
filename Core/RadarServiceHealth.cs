using System;
using System.Collections.Generic;
using System.Drawing;

// The Radar service-health LEDs (Radar site / OpenAI / Claude / DeepSeek), lifted out of the Radar
// window so the network dock panel can show them next to the cloud-endpoint probes (1.0.6.20).
//
// They belong together: both answer "is the upstream service reachable and healthy", and the
// network panel is already where the user looks for that. Keeping them as a Radar tile would have
// meant two places to check the same class of question, so the Radar tile set is quota and model
// quality only.
//
// The Radar window remains the owner of the underlying probe state; this is a read-only projection
// pushed through WidgetForm, the same way the tile feed is.
internal sealed class RadarServiceHealthEntry
{
    public string Label = string.Empty;
    public Color Color;
    public bool Checking;
}

internal static class RadarServiceHealth
{
    // Display names for the four LED slots the Radar window tracks, in its own order.
    internal static readonly string[] Labels = { "Radar", "OpenAI", "Claude", "DeepSeek" };
    internal static readonly string[] KeyPrefixes = { "rader", "openai", "claude", "deepseek" };

    internal static List<RadarServiceHealthEntry> CreateUnknown()
    {
        List<RadarServiceHealthEntry> list = new List<RadarServiceHealthEntry>();
        for (int i = 0; i < Labels.Length; i++)
        {
            list.Add(new RadarServiceHealthEntry
            {
                Label = Labels[i],
                Color = DesignTokens.Colors.GlyphMuted,
                Checking = false
            });
        }

        return list;
    }
}
