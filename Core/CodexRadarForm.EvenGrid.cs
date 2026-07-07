using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// EvenGrid render variant (WidgetSettings.CodexRadarRenderVariant == EvenGrid): a top band of six
// equal-width ring cells (time/token efficiency, 5h/weekly quota, IQ, quota radar) followed by a
// full-width status band split into three equal segments (RC rating, connection summary, IQ update
// time). Ignores CodexRadarManualLayoutEnabled and all CodexRadar*Offset* settings by design - see
// Docs/Interfaces/INTERFACE_INDEX.jsonl internal_api.codex_radar_render_variant. Only paint code
// belongs here; data gathering lives in Core/CodexRadarForm.cs and is shared with EvenRow.
internal sealed partial class CodexRadarForm
{
    private void DrawCodexRadarModulesEvenGrid(Graphics g, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        QuotaDisplayState quotaState = GatherQuotaDisplayState();

        // The window is wide and short, so the status band is a THIN single-line footer, not a
        // half-height band. Splitting the height evenly used to halve the ring size and cram
        // everything together; giving the rings ~80% of the height keeps them legible.
        float dividerGap = S(4);
        float statusBandHeight = Math.Max(S(15), Math.Min(S(22), bounds.Height * 0.18f));
        float topBandHeight = Math.Max(S(24), bounds.Height - statusBandHeight - dividerGap);

        RectangleF topBand = new RectangleF(bounds.Left, bounds.Top, bounds.Width, topBandHeight);
        RectangleF statusBand = new RectangleF(
            bounds.Left,
            topBand.Bottom + dividerGap,
            bounds.Width,
            Math.Max(0.0f, bounds.Bottom - topBand.Bottom - dividerGap));

        DrawEvenGridTopBand(g, topBand, radarSnapshot, quotaState);

        using (Pen dividerPen = new Pen(DesignTokens.White(26), Math.Max(1.0f, S(1))))
        {
            float dividerY = topBand.Bottom + dividerGap / 2.0f;
            g.DrawLine(dividerPen, topBand.Left + S(12), dividerY, topBand.Right - S(12), dividerY);
        }

        DrawEvenGridStatusBand(g, statusBand, radarSnapshot);
    }

    private void DrawEvenGridTopBand(
        Graphics g,
        RectangleF band,
        CodexRadarSnapshot radarSnapshot,
        QuotaDisplayState quotaState)
    {
        if (band.Width <= 0 || band.Height <= 0)
        {
            return;
        }

        const int cellCount = 6;
        float cellGap = S(4);
        float cellWidth = Math.Max(S(28), (band.Width - cellGap * (cellCount - 1)) / cellCount);
        float x = band.Left;

        DrawEvenLayoutEfficiencyCell(g, new RectangleF(x, band.Top, cellWidth, band.Height), radarSnapshot, true);
        x += cellWidth + cellGap;

        DrawEvenLayoutEfficiencyCell(g, new RectangleF(x, band.Top, cellWidth, band.Height), radarSnapshot, false);
        x += cellWidth + cellGap;

        DrawEvenLayoutQuotaCell(
            g,
            new RectangleF(x, band.Top, cellWidth, band.Height),
            quotaState.Snapshot.FiveHourPercent,
            quotaState.Snapshot.FiveHourResetKnown
                ? quotaState.Snapshot.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture)
                : "N/A",
            quotaState.CodexRunning,
            quotaState.AnySupportedAppRunning,
            quotaState.QuotaValueKnown,
            quotaState.FiveHourGold,
            quotaState.FiveHourConsumptionRingPercent,
            radarSnapshot,
            false);
        x += cellWidth + cellGap;

        DrawEvenLayoutQuotaCell(
            g,
            new RectangleF(x, band.Top, cellWidth, band.Height),
            quotaState.Snapshot.WeeklyPercent,
            quotaState.Snapshot.WeeklyResetKnown
                ? quotaState.Snapshot.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture)
                : "N/A",
            quotaState.CodexRunning,
            quotaState.AnySupportedAppRunning,
            quotaState.QuotaValueKnown,
            quotaState.WeeklyGold,
            quotaState.WeeklyConsumptionRingBlocked ? 0 : quotaState.WeeklyConsumptionRingPercent,
            radarSnapshot,
            true);
        x += cellWidth + cellGap;

        DrawEvenLayoutIqCell(g, new RectangleF(x, band.Top, cellWidth, band.Height), radarSnapshot);
        x += cellWidth + cellGap;

        DrawEvenLayoutRadarCell(g, new RectangleF(x, band.Top, cellWidth, band.Height), radarSnapshot);
    }

    private void DrawEvenGridStatusBand(Graphics g, RectangleF band, CodexRadarSnapshot radarSnapshot)
    {
        if (band.Width <= 0 || band.Height <= 0)
        {
            return;
        }

        float segmentWidth = band.Width / 3.0f;
        float padding = S(8);
        Font font = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.LayerScale), FontStyle.Bold);

        RectangleF ratingRect = new RectangleF(band.Left + padding, band.Top, Math.Max(1.0f, segmentWidth - padding * 2.0f), band.Height);
        using (SolidBrush ratingBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 235)))
        {
            DrawCodexRadarFittedText(g, GetCodexCommunityRatingDisplayText(radarSnapshot), font, ratingBrush, ratingRect, StringAlignment.Center);
        }

        RectangleF updateRect = new RectangleF(
            band.Left + segmentWidth * 2.0f + padding,
            band.Top,
            Math.Max(1.0f, segmentWidth - padding * 2.0f),
            band.Height);
        bool radarRequestRunning;
        lock (this.codexRadarStatusLock)
        {
            radarRequestRunning = this.codexRadarStatusRequestRunning;
        }

        string updateText;
        Color updateColor;
        GetCodexModelIqUpdateStatusText(radarSnapshot, radarRequestRunning, out updateText, out updateColor);
        if (radarRequestRunning && (this.renderTickCount & 1) == 0)
        {
            updateColor = DesignTokens.WithAlpha(updateColor, 104);
        }

        using (SolidBrush updateBrush = new SolidBrush(updateColor))
        {
            DrawCodexRadarFittedText(g, updateText, font, updateBrush, updateRect, StringAlignment.Center);
        }
    }
}
