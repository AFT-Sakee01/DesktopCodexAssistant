using System;
using System.Drawing;

// WarmCard render variant (WidgetSettings.CodexRadarRenderVariant == WarmCard): OLED-safe, no-blue
// restyle. Low-luminance warm-gray filled cards, status dot carries severity - same six-plus-three
// band split as EvenGrid. Background stays the existing semi-transparent AppBackground.
internal sealed partial class CodexRadarForm
{
    private void DrawCodexRadarModulesWarmCard(Graphics g, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        bool suppressFill = IsBurnInColorProtectionActive();
        RectangleF[] topCells;
        RectangleF radarCellRect;
        RectangleF[] statusCells;
        GetOledGridGeometry(bounds, out topCells, out radarCellRect, out statusCells);
        OledRadarItem[] items = GetCodexRadarOledItems();

        // Top band values (e.g. "0% 23:16") are too long to share one line with their label at a
        // readable size once the dot and card padding eat into a six-way-split cell's width, so this
        // band stacks value-over-label (DrawDotCardStacked) instead of forcing both onto one row like
        // the status band below does.
        float topCornerRadius = Math.Max(S(3), topCells[0].Height * 0.14f);
        float topDotDiameter = Math.Max(S(2), topCells[0].Height * 0.09f);
        float topTextWidth = topCells[0].Width - topDotDiameter - topCornerRadius * 2.0f;
        float topValueMaxSize = Math.Max(9.0f, topCells[0].Height * 0.15f);
        float topValueMinSize = Math.Max(7.0f, topCells[0].Height * 0.09f);
        float topValueFitted = FitValueOnlySize(g, items, 0, 5, topTextWidth, topValueMaxSize, topValueMinSize);
        Font topValueFont = this.fontCache.GetUi(topValueFitted, FontStyle.Regular);
        Font topLabelFont = this.fontCache.GetUi(Math.Max(6.5f, topCells[0].Height * 0.09f), FontStyle.Regular);
        for (int i = 0; i < 5; i++)
        {
            Color dotColor = OledVariantPainting.PickSeverityColor(
                items[i].Severity,
                DesignTokens.OledCard.DotGood,
                DesignTokens.OledCard.DotWarn,
                DesignTokens.OledCard.DotDanger,
                DesignTokens.OledCard.Muted);
            OledVariantPainting.DrawDotCardStacked(g, topCells[i], items[i].Value, items[i].Label, dotColor, DesignTokens.OledCard.Text, DesignTokens.OledCard.Muted, DesignTokens.OledCard.CardFill, suppressFill, topValueFont, topLabelFont, topCornerRadius, topDotDiameter);
        }

        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        float lineWidth = Math.Max(S(6), Math.Min(S(10), radarCellRect.Width * 0.30f));
        RectangleF radarLineRect = new RectangleF(
            radarCellRect.Left + (radarCellRect.Width - lineWidth) / 2.0f,
            radarCellRect.Top + S(3),
            lineWidth,
            Math.Max(1.0f, radarCellRect.Height - S(6)));
        DrawCodexQuotaRadarVerticalLine(g, radarLineRect, radarSnapshot == null ? null : radarSnapshot.QuotaRadar, 1.0f, DesignTokens.OledCard.DotGood);

        float statusCornerRadius = Math.Max(S(4), statusCells[0].Height * 0.20f);
        float statusDotDiameter = Math.Max(S(3), statusCells[0].Height * 0.13f);
        float statusTextWidth = statusCells[0].Width - statusDotDiameter - statusCornerRadius * 2.0f;
        float statusMaxSize = Math.Max(9.0f, statusCells[0].Height * 0.30f);
        float statusMinSize = Math.Max(6.5f, statusCells[0].Height * 0.16f);
        float statusFitted = FitCardCommonSize(g, items, 5, 3, statusTextWidth, statusMaxSize, statusMinSize);
        Font statusFont = this.fontCache.GetUi(statusFitted, FontStyle.Regular);
        for (int i = 0; i < 3; i++)
        {
            DrawWarmRadarCard(g, statusCells[i], items[5 + i].Value, items[5 + i].Severity, statusFont, statusCornerRadius, statusDotDiameter, suppressFill);
        }
    }

    private float FitCardCommonSize(Graphics g, OledRadarItem[] items, int start, int count, float maxWidth, float maxSize, float minSize)
    {
        // Always measure the combined "value label" text, even for cells the caller draws as
        // value-only (the status band): that guarantees the fitted size is never wider than what
        // was actually measured, so it can only end up more conservative, never truncated.
        float size = maxSize;
        for (int i = start; i < start + count; i++)
        {
            string text = items[i].Value + " " + items[i].Label;
            float fitted = OledVariantPainting.FitFontSize(g, text, DesignTokens.UiFontFamily, FontStyle.Regular, maxSize, minSize, maxWidth);
            size = Math.Min(size, fitted);
        }

        return size;
    }

    private float FitValueOnlySize(Graphics g, OledRadarItem[] items, int start, int count, float maxWidth, float maxSize, float minSize)
    {
        float size = maxSize;
        for (int i = start; i < start + count; i++)
        {
            float fitted = OledVariantPainting.FitFontSize(g, items[i].Value, DesignTokens.UiFontFamily, FontStyle.Regular, maxSize, minSize, maxWidth);
            size = Math.Min(size, fitted);
        }

        return size;
    }

    private void DrawWarmRadarCard(Graphics g, RectangleF rect, string text, OledVariantPainting.Severity severity, Font font, float cornerRadius, float dotDiameter, bool suppressFill)
    {
        Color dotColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledCard.DotGood,
            DesignTokens.OledCard.DotWarn,
            DesignTokens.OledCard.DotDanger,
            DesignTokens.OledCard.Muted);
        OledVariantPainting.DrawDotCard(g, rect, text, dotColor, DesignTokens.OledCard.Text, DesignTokens.OledCard.CardFill, suppressFill, font, cornerRadius, dotDiameter);
    }
}
