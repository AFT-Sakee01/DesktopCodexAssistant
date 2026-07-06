using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

// AmberHud render variant (WidgetSettings.MainWidgetRenderVariant == AmberHud): OLED-safe, no-blue
// restyle. Single amber hue, thin hairline chip per metric wrapping its sparkline graph (recolored)
// and a value line. Background stays the existing semi-transparent AppBackground.
internal sealed partial class WidgetForm
{
    private void DrawWidgetContentAmberHud(Graphics g)
    {
        ConfigureWidgetGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Widget)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledAmber.Dim, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        int margin = S(13);
        int gap = S(14);
        int rowGap = S(8);
        List<MetricPanel> panels = BuildMetricPanels();
        if (panels.Count == 0)
        {
            DrawNoMetricsMessage(g, DesignTokens.OledAmber.Dim);
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
            DrawAmberMetric(g, area, panels[i], suppressFill);
        }
    }

    private void DrawAmberMetric(Graphics g, RectangleF area, MetricPanel panel, bool suppressFill)
    {
        Color lineColor = OledVariantPainting.PickSeverityColor(
            GetPanelSeverity(panel),
            DesignTokens.OledAmber.Bright,
            DesignTokens.OledAmber.Base,
            DesignTokens.OledAmber.Danger,
            DesignTokens.OledAmber.Dim);
        float cornerRadius = Math.Max(1.0f, area.Height * 0.08f);
        using (Pen chipPen = new Pen(lineColor, Math.Max(1.0f, S(1))))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(lineColor, 16)))
        using (GraphicsPath path = OledVariantPainting.RoundedRectangle(area, cornerRadius))
        {
            if (!suppressFill)
            {
                g.FillPath(fill, path);
            }

            g.DrawPath(chipPen, path);
        }

        float inset = S(6);
        RectangleF inner = new RectangleF(area.X + inset, area.Y + inset * 0.6f, Math.Max(1.0f, area.Width - inset * 2.0f), Math.Max(1.0f, area.Height - inset * 1.2f));
        float graphW = Math.Min(S(50), Math.Max(S(36), inner.Width * 0.26f));
        float graphH = Math.Max(S(20), inner.Height - S(6));
        RectangleF graphRect = new RectangleF(inner.X, inner.Y + Math.Max(0, (inner.Height - graphH) / 2), graphW, graphH);
        Color[] graphColors = RemapGraphColors(panel.Colors, DesignTokens.OledAmber.Bright, DesignTokens.OledAmber.Base);
        DrawGraph(g, graphRect, graphColors, panel.Histories, panel.GraphMax, panel.AutoScale, panel.IsNetworkDisconnected, panel.CoreValues, panel.AlertPercent, panel.AlertIconVisible);
        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(7);
        RectangleF textRect = new RectangleF(textX, inner.Y, Math.Max(20, inner.Right - textX), inner.Height);
        string value = GetPanelHeadline(panel);
        float fittedSize = OledVariantPainting.FitFontSize(g, value, DesignTokens.MonoFontFamily, FontStyle.Bold, 13.0f * this.LayerScale, 5.5f * this.LayerScale, textRect.Width * 0.94f);
        Font valueFont = GetCachedMonoFont(fittedSize, FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(lineColor))
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
