using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

// Typographic render variant (WidgetSettings.MainWidgetRenderVariant == Typographic): OLED-safe,
// no-blue restyle. No panel borders/fills - each metric keeps its sparkline graph (recolored) with a
// single value line beside it. Background stays the existing semi-transparent AppBackground.
internal sealed partial class WidgetForm
{
    private void DrawWidgetContentTypographic(Graphics g)
    {
        ConfigureWidgetGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Widget)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledTypographic.Muted, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        int margin = S(13);
        int gap = S(14);
        int rowGap = S(8);
        List<MetricPanel> panels = BuildMetricPanels();
        if (panels.Count == 0)
        {
            DrawNoMetricsMessage(g, DesignTokens.OledTypographic.Muted);
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
            DrawTypographicMetric(g, area, panels[i]);
        }
    }

    private void DrawTypographicMetric(Graphics g, RectangleF area, MetricPanel panel)
    {
        float graphW = Math.Min(S(56), Math.Max(S(40), area.Width * 0.26f));
        float graphH = Math.Max(S(24), area.Height - S(8));
        RectangleF graphRect = new RectangleF(area.X, area.Y + Math.Max(0, (area.Height - graphH) / 2), graphW, graphH);
        Color[] graphColors = RemapGraphColors(panel.Colors, DesignTokens.OledTypographic.Primary, DesignTokens.OledTypographic.Secondary);
        DrawGraph(g, graphRect, graphColors, panel.Histories, panel.GraphMax, panel.AutoScale, panel.IsNetworkDisconnected, panel.CoreValues, panel.AlertPercent, panel.AlertIconVisible);
        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(9);
        RectangleF textRect = new RectangleF(textX, area.Y, Math.Max(20, area.Right - textX), area.Height);
        Color valueColor = OledVariantPainting.PickSeverityColor(
            GetPanelSeverity(panel),
            DesignTokens.OledTypographic.AccentGood,
            DesignTokens.OledTypographic.AccentWarn,
            DesignTokens.OledTypographic.AccentDanger,
            DesignTokens.OledTypographic.Primary);
        string value = GetPanelHeadline(panel);
        float fittedSize = OledVariantPainting.FitFontSize(g, value, DesignTokens.UiFontFamily, FontStyle.Regular, 15.0f * this.scale, 6.0f * this.scale, textRect.Width * 0.94f);
        Font valueFont = GetCachedFont(fittedSize, FontStyle.Regular);
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

    private void DrawNoMetricsMessage(Graphics g, Color color)
    {
        Font font = GetCachedFont(13.0f * this.scale, FontStyle.Regular);
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("No metrics enabled", font, brush, this.ClientRectangle, format);
        }
    }
}
