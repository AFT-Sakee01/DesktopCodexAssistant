using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

// Phosphor render variant (WidgetSettings.MainWidgetRenderVariant == Phosphor): OLED-safe, no-blue
// restyle. Single dim-green terminal text, zero panel shapes - each metric keeps its sparkline graph
// (recolored) with a value line beside it. Background stays the existing semi-transparent AppBackground.
internal sealed partial class WidgetForm
{
    private void DrawWidgetContentPhosphor(Graphics g)
    {
        ConfigureWidgetGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Widget)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Faint, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        int margin = S(13);
        int gap = S(14);
        int rowGap = S(8);
        List<MetricPanel> panels = BuildMetricPanels();
        if (panels.Count == 0)
        {
            DrawNoMetricsMessage(g, DesignTokens.OledPhosphor.Dim);
            return;
        }

        int columns = panels.Count == 1 ? 1 : 2;
        int rows = (panels.Count + columns - 1) / columns;
        int colWidth = (this.ClientSize.Width - margin * 2 - gap * (columns - 1)) / columns;
        int rowHeight = (this.ClientSize.Height - margin * 2 - rowGap * (rows - 1)) / rows;

        for (int i = 0; i < panels.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            RectangleF area = new RectangleF(
                margin + column * (colWidth + gap),
                margin + row * (rowHeight + rowGap),
                colWidth,
                rowHeight);
            DrawPhosphorMetric(g, area, panels[i]);
        }
    }

    private void DrawPhosphorMetric(Graphics g, RectangleF area, MetricPanel panel)
    {
        float graphW = Math.Min(S(62), Math.Max(S(40), area.Width * 0.28f));
        float graphH = Math.Max(S(20), area.Height - S(8));
        RectangleF graphRect = new RectangleF(area.X, area.Y + Math.Max(0, (area.Height - graphH) / 2), graphW, graphH);
        Color[] graphColors = RemapGraphColors(panel.Colors, DesignTokens.OledPhosphor.Bright, DesignTokens.OledPhosphor.Dim);
        DrawGraph(g, graphRect, graphColors, panel.Histories, panel.GraphMax, panel.AutoScale, panel.IsNetworkDisconnected, panel.CoreValues, panel.AlertPercent, panel.AlertIconVisible);
        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(7);
        RectangleF textRect = new RectangleF(textX, area.Y, Math.Max(20, area.Right - textX), area.Height);
        Color valueColor = OledVariantPainting.PickSeverityColor(
            GetPanelSeverity(panel),
            DesignTokens.OledPhosphor.Bright,
            DesignTokens.OledPhosphor.Warn,
            DesignTokens.OledPhosphor.Danger,
            DesignTokens.OledPhosphor.Base);
        string value = GetPanelHeadline(panel);
        float fittedSize = OledVariantPainting.FitFontSize(g, value, DesignTokens.MonoFontFamily, FontStyle.Regular, 12.5f * this.scale, 7.0f * this.scale, textRect.Width * 0.96f);
        Font valueFont = GetCachedMonoFont(fittedSize, FontStyle.Regular);
        using (SolidBrush brush = new SolidBrush(valueColor))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(value, valueFont, brush, textRect, format);
        }
    }
}
