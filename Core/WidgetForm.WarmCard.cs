using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

// WarmCard render variant (WidgetSettings.MainWidgetRenderVariant == WarmCard): OLED-safe, no-blue
// restyle. Low-luminance warm-gray filled card per metric wrapping its sparkline graph (recolored),
// status dot carries severity. Background stays the existing semi-transparent AppBackground.
internal sealed partial class WidgetForm
{
    private void DrawWidgetContentWarmCard(Graphics g)
    {
        ConfigureWidgetGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Widget)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledCard.Muted, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        int margin = S(13);
        int gap = S(14);
        int rowGap = S(8);
        List<MetricPanel> panels = BuildMetricPanels();
        if (panels.Count == 0)
        {
            DrawNoMetricsMessage(g, DesignTokens.OledCard.Muted);
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
            DrawWarmCardMetric(g, area, panels[i], suppressFill);
        }
    }

    private void DrawWarmCardMetric(Graphics g, RectangleF area, MetricPanel panel, bool suppressFill)
    {
        float cornerRadius = Math.Max(2.0f, area.Height * 0.12f);
        using (SolidBrush cardBrush = new SolidBrush(DesignTokens.OledCard.CardFill))
        using (GraphicsPath path = OledVariantPainting.RoundedRectangle(area, cornerRadius))
        {
            if (!suppressFill)
            {
                g.FillPath(cardBrush, path);
            }
        }

        Color dotColor = OledVariantPainting.PickSeverityColor(
            GetPanelSeverity(panel),
            DesignTokens.OledCard.DotGood,
            DesignTokens.OledCard.DotWarn,
            DesignTokens.OledCard.DotDanger,
            DesignTokens.OledCard.Muted);
        float dotDiameter = Math.Max(2.0f, area.Height * 0.09f);
        using (SolidBrush dotBrush = new SolidBrush(dotColor))
        {
            g.FillEllipse(dotBrush, area.Left + cornerRadius * 0.7f, area.Top + cornerRadius * 0.5f, dotDiameter, dotDiameter);
        }

        float inset = S(6);
        RectangleF inner = new RectangleF(area.X + inset, area.Y + inset, Math.Max(1.0f, area.Width - inset * 2.0f), Math.Max(1.0f, area.Height - inset * 1.6f));
        float graphW = Math.Min(S(50), Math.Max(S(36), inner.Width * 0.26f));
        float graphH = Math.Max(S(20), inner.Height - S(6));
        RectangleF graphRect = new RectangleF(inner.X, inner.Y + Math.Max(0, (inner.Height - graphH) / 2), graphW, graphH);
        Color[] graphColors = RemapGraphColors(panel.Colors, DesignTokens.OledCard.DotGood, DesignTokens.OledCard.Muted);
        DrawGraph(g, graphRect, graphColors, panel.Histories, panel.GraphMax, panel.AutoScale, panel.IsNetworkDisconnected, panel.CoreValues, panel.AlertPercent, panel.AlertIconVisible);
        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(7);
        RectangleF textRect = new RectangleF(textX, inner.Y, Math.Max(20, inner.Right - textX), inner.Height);
        string value = GetPanelHeadline(panel);
        float fittedSize = OledVariantPainting.FitFontSize(g, value, DesignTokens.UiFontFamily, FontStyle.Regular, 14.0f * this.LayerScale, 6.0f * this.LayerScale, textRect.Width * 0.94f);
        Font valueFont = GetCachedFont(fittedSize, FontStyle.Regular);
        using (SolidBrush brush = new SolidBrush(DesignTokens.OledCard.Text))
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
