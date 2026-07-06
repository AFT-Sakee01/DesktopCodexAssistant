using System;
using System.Drawing;

// AmberHud render variant (WidgetSettings.CodexRadarRenderVariant == AmberHud): OLED-safe, no-blue
// restyle. Single amber hue, thin hairline chips, mono labels - same six-plus-three band split as
// EvenGrid. Background stays the existing semi-transparent AppBackground.
internal sealed partial class CodexRadarForm
{
    private void DrawCodexRadarModulesAmberHud(Graphics g, RectangleF bounds)
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

        float cornerRadius = Math.Max(S(1), topCells[0].Height * 0.05f);
        float borderWidth = Math.Max(1.0f, S(1));
        float topMaxSize = Math.Max(9.0f, topCells[0].Height * 0.20f);
        float topMinSize = Math.Max(6.5f, topCells[0].Height * 0.11f);
        float topFitted = FitCommonSize(g, items, 0, 5, topCells[0].Width * 0.88f, topMaxSize, topMinSize);
        Font topFont = this.fontCache.GetMono(topFitted, FontStyle.Bold);
        for (int i = 0; i < 5; i++)
        {
            DrawAmberChip(g, topCells[i], items[i].Label + " " + items[i].Value, items[i].Severity, topFont, cornerRadius, borderWidth, suppressFill);
        }

        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        float lineWidth = Math.Max(S(6), Math.Min(S(10), radarCellRect.Width * 0.30f));
        RectangleF radarLineRect = new RectangleF(
            radarCellRect.Left + (radarCellRect.Width - lineWidth) / 2.0f,
            radarCellRect.Top + S(3),
            lineWidth,
            Math.Max(1.0f, radarCellRect.Height - S(6)));
        DrawCodexQuotaRadarVerticalLine(g, radarLineRect, radarSnapshot == null ? null : radarSnapshot.QuotaRadar, 1.0f, DesignTokens.OledAmber.Bright);

        float statusMaxSize = Math.Max(9.0f, statusCells[0].Height * 0.30f);
        float statusMinSize = Math.Max(6.5f, statusCells[0].Height * 0.16f);
        float statusFitted = FitCommonSize(g, items, 5, 3, statusCells[0].Width * 0.90f, statusMaxSize, statusMinSize);
        Font statusFont = this.fontCache.GetMono(statusFitted, FontStyle.Bold);
        for (int i = 0; i < 3; i++)
        {
            DrawAmberChip(g, statusCells[i], items[5 + i].Value, items[5 + i].Severity, statusFont, cornerRadius, borderWidth, suppressFill);
        }
    }

    private float FitCommonSize(Graphics g, OledRadarItem[] items, int start, int count, float maxWidth, float maxSize, float minSize)
    {
        // Always measure the combined "label value" text, even for cells the caller draws as
        // value-only (the status band): that guarantees the fitted size is never wider than what
        // was actually measured, so it can only end up more conservative, never truncated.
        float size = maxSize;
        for (int i = start; i < start + count; i++)
        {
            string text = items[i].Label + " " + items[i].Value;
            float fitted = OledVariantPainting.FitFontSize(g, text, DesignTokens.MonoFontFamily, FontStyle.Bold, maxSize, minSize, maxWidth);
            size = Math.Min(size, fitted);
        }

        return size;
    }

    private void DrawAmberChip(Graphics g, RectangleF rect, string text, OledVariantPainting.Severity severity, Font font, float cornerRadius, float borderWidth, bool suppressFill)
    {
        Color lineColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledAmber.Bright,
            DesignTokens.OledAmber.Base,
            DesignTokens.OledAmber.Danger,
            DesignTokens.OledAmber.Dim);
        Color fillColor = DesignTokens.WithAlpha(lineColor, 20);
        OledVariantPainting.DrawHollowChip(g, rect, text, lineColor, lineColor, fillColor, suppressFill, font, cornerRadius, borderWidth);
    }
}
