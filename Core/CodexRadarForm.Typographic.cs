using System;
using System.Drawing;

// Typographic render variant (WidgetSettings.CodexRadarRenderVariant == Typographic): OLED-safe,
// no-blue restyle. No borders, no fills - stacked value/label cells, same six-plus-three band split
// as EvenGrid. Background stays the existing semi-transparent AppBackground.
internal sealed partial class CodexRadarForm
{
    private void DrawCodexRadarModulesTypographic(Graphics g, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        RectangleF[] topCells;
        RectangleF radarCellRect;
        RectangleF[] statusCells;
        GetOledGridGeometry(bounds, out topCells, out radarCellRect, out statusCells);
        OledRadarItem[] items = GetCodexRadarOledItems();

        Font valueFont = this.fontCache.GetUi(Math.Max(9.0f, topCells[0].Height * 0.30f), FontStyle.Regular);
        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, topCells[0].Height * 0.13f), FontStyle.Regular);
        for (int i = 0; i < 5; i++)
        {
            DrawTypographicRadarItem(g, topCells[i], items[i], valueFont, labelFont);
        }

        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        float lineWidth = Math.Max(S(6), Math.Min(S(10), radarCellRect.Width * 0.30f));
        RectangleF radarLineRect = new RectangleF(
            radarCellRect.Left + (radarCellRect.Width - lineWidth) / 2.0f,
            radarCellRect.Top + S(3),
            lineWidth,
            Math.Max(1.0f, radarCellRect.Height - S(6)));
        DrawCodexQuotaRadarVerticalLine(g, radarLineRect, radarSnapshot == null ? null : radarSnapshot.QuotaRadar, 1.0f, DesignTokens.OledTypographic.Primary);

        Font statusFont = this.fontCache.GetUi(Math.Max(7.0f, statusCells[0].Height * 0.30f), FontStyle.Regular);
        Font statusLabelFont = this.fontCache.GetUi(Math.Max(6.5f, statusCells[0].Height * 0.16f), FontStyle.Regular);
        for (int i = 0; i < 3; i++)
        {
            DrawTypographicRadarItem(g, statusCells[i], items[5 + i], statusFont, statusLabelFont);
        }

        if (!IsBurnInColorProtectionActive())
        {
            Color hairline = DesignTokens.OledTypographic.Hairline;
            using (Pen pen = new Pen(hairline, Math.Max(1.0f, S(1))))
            {
                for (int i = 1; i < 5; i++)
                {
                    g.DrawLine(pen, topCells[i].Left, topCells[i].Top + topCells[i].Height * 0.15f, topCells[i].Left, topCells[i].Bottom - topCells[i].Height * 0.15f);
                }

                g.DrawLine(pen, radarCellRect.Left, radarCellRect.Top + radarCellRect.Height * 0.15f, radarCellRect.Left, radarCellRect.Bottom - radarCellRect.Height * 0.15f);
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(pen, statusCells[i].Left, statusCells[i].Top + statusCells[i].Height * 0.2f, statusCells[i].Left, statusCells[i].Bottom - statusCells[i].Height * 0.2f);
                }
            }
        }
    }

    private void DrawTypographicRadarItem(Graphics g, RectangleF rect, OledRadarItem item, Font valueFont, Font labelFont)
    {
        Color valueColor = OledVariantPainting.PickSeverityColor(
            item.Severity,
            DesignTokens.OledTypographic.AccentGood,
            DesignTokens.OledTypographic.AccentWarn,
            DesignTokens.OledTypographic.AccentDanger,
            DesignTokens.OledTypographic.Primary);
        float fittedSize = OledVariantPainting.FitFontSize(g, item.Value, DesignTokens.UiFontFamily, FontStyle.Regular, valueFont.Size, Math.Max(7.0f, valueFont.Size * 0.55f), rect.Width * 0.92f);
        Font fittedFont = Math.Abs(fittedSize - valueFont.Size) < 0.5f ? valueFont : this.fontCache.GetUi(fittedSize, FontStyle.Regular);
        OledVariantPainting.DrawStackedMetric(g, rect, item.Value, item.Label, valueColor, DesignTokens.OledTypographic.Muted, fittedFont, labelFont);
    }
}
