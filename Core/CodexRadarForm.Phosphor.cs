using System;
using System.Drawing;

// Phosphor render variant (WidgetSettings.CodexRadarRenderVariant == Phosphor): OLED-safe, no-blue
// restyle. Single dim-green terminal text, zero shapes - same six-plus-three band split as EvenGrid.
// Background stays the existing semi-transparent AppBackground.
internal sealed partial class CodexRadarForm
{
    private static readonly string[] PhosphorPrefixes =
    {
        "time", "token", "5h", "week", "iq", "rc", "conn", "upd"
    };

    private void DrawCodexRadarModulesPhosphor(Graphics g, RectangleF bounds)
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

        float topMaxSize = Math.Max(7.0f, topCells[0].Height * 0.18f);
        float topMinSize = Math.Max(6.0f, topCells[0].Height * 0.10f);
        float topFitted = FitPhosphorCommonSize(g, items, PhosphorPrefixes, 0, 5, topCells[0].Width * 0.92f, topMaxSize, topMinSize);
        Font topFont = this.fontCache.GetMono(topFitted, FontStyle.Regular);
        for (int i = 0; i < 5; i++)
        {
            RectangleF row = new RectangleF(topCells[i].Left, topCells[i].Top, topCells[i].Width, topCells[i].Height / 2.0f);
            DrawPhosphorRadarRow(g, row, PhosphorPrefixes[i], items[i].Value, items[i].Severity, topFont);
        }

        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        float lineWidth = Math.Max(S(6), Math.Min(S(10), radarCellRect.Width * 0.30f));
        RectangleF radarLineRect = new RectangleF(
            radarCellRect.Left + (radarCellRect.Width - lineWidth) / 2.0f,
            radarCellRect.Top + S(3),
            lineWidth,
            Math.Max(1.0f, radarCellRect.Height - S(6)));
        DrawCodexQuotaRadarVerticalLine(g, radarLineRect, radarSnapshot == null ? null : radarSnapshot.QuotaRadar, 1.0f, DesignTokens.OledPhosphor.Bright);

        float statusMaxSize = Math.Max(7.0f, statusCells[0].Height * 0.30f);
        float statusMinSize = Math.Max(6.0f, statusCells[0].Height * 0.16f);
        float statusFitted = FitPhosphorCommonSize(g, items, PhosphorPrefixes, 5, 3, statusCells[0].Width * 0.94f, statusMaxSize, statusMinSize);
        Font statusFont = this.fontCache.GetMono(statusFitted, FontStyle.Regular);
        for (int i = 0; i < 3; i++)
        {
            DrawPhosphorRadarRow(g, statusCells[i], PhosphorPrefixes[5 + i], items[5 + i].Value, items[5 + i].Severity, statusFont);
        }
    }

    // Measures "prefix: value" for every item in the band (even though the row itself draws prefix
    // and value as two separate DrawString calls) so the fitted size can never be wider than what
    // was actually measured - mirrors the WarmCard/AmberHud fit helpers' conservative-by-construction approach.
    private float FitPhosphorCommonSize(Graphics g, OledRadarItem[] items, string[] prefixes, int start, int count, float maxWidth, float maxSize, float minSize)
    {
        float size = maxSize;
        for (int i = start; i < start + count; i++)
        {
            string text = prefixes[i] + ": " + items[i].Value;
            float fitted = OledVariantPainting.FitFontSize(g, text, DesignTokens.MonoFontFamily, FontStyle.Regular, maxSize, minSize, maxWidth);
            size = Math.Min(size, fitted);
        }

        return size;
    }

    private void DrawPhosphorRadarRow(Graphics g, RectangleF rect, string prefix, string value, OledVariantPainting.Severity severity, Font font)
    {
        Color valueColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledPhosphor.Bright,
            DesignTokens.OledPhosphor.Warn,
            DesignTokens.OledPhosphor.Danger,
            DesignTokens.OledPhosphor.Base);
        OledVariantPainting.DrawTerminalRow(g, rect, prefix, value, DesignTokens.OledPhosphor.Dim, valueColor, font);
    }
}
