using System;
using System.Drawing;
using System.Globalization;

// Shared data extraction for the four OLED-safe restyle schemes (Typographic, AmberHud, WarmCard,
// Phosphor) added in 1.0.3.44. Reuses the exact same data-gathering calls as Classic/EvenGrid/EvenRow
// (GatherQuotaDisplayState, GetCodexRadarDisplaySnapshot, GetCodexConnectionDisplaySnapshot, the IQ
// baseline helpers) so severity/value only ever derives from real snapshot fields - never from a
// Classic color helper's specific accent hue, since those are not guaranteed blue-free. The quota
// radar trend chart is intentionally excluded here (see DrawCodexRadarModulesTypographic and its
// siblings): it keeps using DrawCodexQuotaRadarVerticalLine directly, with only its current-value dot
// recolored via the dotColorOverride parameter, because re-deriving that chart's own trend geometry as
// a text item would destroy the only genuinely time-series content this window has.
internal sealed partial class CodexRadarForm
{
    private struct OledRadarItem
    {
        public string Label;
        public string Value;
        public OledVariantPainting.Severity Severity;
    }

    private OledRadarItem[] GetCodexRadarOledItems()
    {
        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        QuotaDisplayState quotaState = GatherQuotaDisplayState();

        OledRadarItem[] items = new OledRadarItem[8];
        items[0] = BuildEfficiencyItem(radarSnapshot, true, "时间效率");
        items[1] = BuildEfficiencyItem(radarSnapshot, false, "令牌效率");
        items[2] = BuildQuotaItem(
            radarSnapshot,
            quotaState.Snapshot.FiveHourPercent,
            quotaState.Snapshot.FiveHourResetKnown ? quotaState.Snapshot.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "N/A",
            quotaState.FiveHourGold,
            quotaState.CodexRunning,
            false,
            "5h配额");
        items[3] = BuildQuotaItem(
            radarSnapshot,
            quotaState.Snapshot.WeeklyPercent,
            quotaState.Snapshot.WeeklyResetKnown ? quotaState.Snapshot.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture) : "N/A",
            quotaState.WeeklyGold,
            quotaState.CodexRunning,
            true,
            "周配额");
        items[4] = BuildIqItem(radarSnapshot);
        items[5] = BuildRatingItem(radarSnapshot);
        items[6] = BuildConnectionItem();
        items[7] = BuildIqUpdateItem(radarSnapshot);
        return items;
    }

    private OledRadarItem BuildEfficiencyItem(CodexRadarSnapshot snapshot, bool timeEfficiency, string label)
    {
        bool known = snapshot != null && snapshot.ModelIqEfficiencyKnown;
        int efficiency = known
            ? ClampEfficiencyPercent(timeEfficiency ? snapshot.ModelIqTimeEfficiencyPercent : snapshot.ModelIqTokenEfficiencyPercent)
            : 100;
        OledRadarItem item = new OledRadarItem();
        item.Label = label;
        item.Value = known ? efficiency.ToString(CultureInfo.InvariantCulture) + "%" : "--";
        item.Severity = !known
            ? OledVariantPainting.Severity.Neutral
            : (efficiency < 100 ? OledVariantPainting.Severity.Danger : (efficiency > 100 ? OledVariantPainting.Severity.Warn : OledVariantPainting.Severity.Good));
        return item;
    }

    private OledRadarItem BuildQuotaItem(CodexRadarSnapshot radarSnapshot, int percent, string resetText, bool quotaProtected, bool codexRunning, bool dateText, string label)
    {
        percent = ClampPercent(percent);
        string resetDisplayText;
        Color unusedColor;
        GetQuotaResetDisplayText(resetText, quotaProtected, radarSnapshot, dateText, out resetDisplayText, out unusedColor);

        OledRadarItem item = new OledRadarItem();
        item.Label = label;
        item.Value = percent.ToString(CultureInfo.InvariantCulture) + "% " + resetDisplayText;
        item.Severity = !codexRunning
            ? OledVariantPainting.Severity.Neutral
            : (percent >= 80 ? OledVariantPainting.Severity.Good : (percent >= 30 ? OledVariantPainting.Severity.Warn : OledVariantPainting.Severity.Danger));
        return item;
    }

    private OledRadarItem BuildIqItem(CodexRadarSnapshot snapshot)
    {
        bool known = snapshot != null && snapshot.ModelIqKnown;
        int passRatePercent = known ? Math.Max(0, Math.Min(MaxCodexModelIqScore, snapshot.ModelIqPassRatePercent)) : 0;
        int passed;
        int validTasks;
        bool scoreKnown = TryGetCodexModelIqPassed(snapshot, out passed, out validTasks);

        OledRadarItem item = new OledRadarItem();
        item.Label = "模型IQ";
        item.Value = known ? passRatePercent.ToString(CultureInfo.InvariantCulture) : "--";
        item.Severity = OledVariantPainting.Severity.Neutral;
        if (scoreKnown)
        {
            double delta = passed - GetCodexModelIqBaselinePassed(snapshot);
            if (delta < -0.05)
            {
                item.Severity = OledVariantPainting.Severity.Danger;
            }
            else if (delta > 0.05)
            {
                item.Severity = OledVariantPainting.Severity.Warn;
            }
            else
            {
                item.Severity = OledVariantPainting.Severity.Good;
            }
        }

        return item;
    }

    private OledRadarItem BuildRatingItem(CodexRadarSnapshot radarSnapshot)
    {
        OledRadarItem item = new OledRadarItem();
        item.Label = "社区评分";
        item.Value = GetCodexCommunityRatingDisplayText(radarSnapshot);
        item.Severity = OledVariantPainting.Severity.Neutral;
        return item;
    }

    private OledRadarItem BuildConnectionItem()
    {
        bool requestRunning;
        CodexConnectionSnapshot snapshot = GetCodexConnectionDisplaySnapshot(out requestRunning);
        string text;
        Color unusedColor;
        GetCodexConnectionStatusSummary(snapshot, requestRunning, out text, out unusedColor);

        OledRadarItem item = new OledRadarItem();
        item.Label = "连接";
        item.Value = text;
        if (snapshot == null || !snapshot.CheckedAtKnown || snapshot.Offline)
        {
            item.Severity = OledVariantPainting.Severity.Neutral;
        }
        else
        {
            item.Severity = text == "已通过" ? OledVariantPainting.Severity.Good : OledVariantPainting.Severity.Warn;
        }

        return item;
    }

    // Shared band/cell geometry for all four OLED-safe schemes - mirrors EvenGrid's split (a top band
    // of six cells, five text items plus the quota radar chart, followed by a thin three-segment
    // status footer) so switching between EvenGrid and an OLED scheme doesn't reflow the window.
    private void GetOledGridGeometry(RectangleF bounds, out RectangleF[] topCells, out RectangleF radarCellRect, out RectangleF[] statusCells)
    {
        float dividerGap = S(4);
        float statusBandHeight = Math.Max(S(15), Math.Min(S(22), bounds.Height * 0.18f));
        float topBandHeight = Math.Max(S(24), bounds.Height - statusBandHeight - dividerGap);
        RectangleF topBand = new RectangleF(bounds.Left, bounds.Top, bounds.Width, topBandHeight);
        RectangleF statusBand = new RectangleF(
            bounds.Left,
            topBand.Bottom + dividerGap,
            bounds.Width,
            Math.Max(0.0f, bounds.Bottom - topBand.Bottom - dividerGap));

        const int topCellCount = 6;
        float topGap = S(4);
        float topCellWidth = Math.Max(S(28), (topBand.Width - topGap * (topCellCount - 1)) / topCellCount);
        topCells = new RectangleF[5];
        float x = topBand.Left;
        for (int i = 0; i < 5; i++)
        {
            topCells[i] = new RectangleF(x, topBand.Top, topCellWidth, topBand.Height);
            x += topCellWidth + topGap;
        }

        radarCellRect = new RectangleF(x, topBand.Top, topCellWidth, topBand.Height);

        float statusSegmentWidth = statusBand.Width / 3.0f;
        statusCells = new RectangleF[3];
        for (int i = 0; i < 3; i++)
        {
            statusCells[i] = new RectangleF(statusBand.Left + statusSegmentWidth * i, statusBand.Top, statusSegmentWidth, statusBand.Height);
        }
    }

    private OledRadarItem BuildIqUpdateItem(CodexRadarSnapshot radarSnapshot)
    {
        bool radarRequestRunning;
        lock (this.codexRadarStatusLock)
        {
            radarRequestRunning = this.codexRadarStatusRequestRunning;
        }

        string updateText;
        Color unusedColor;
        GetCodexModelIqUpdateStatusText(radarSnapshot, radarRequestRunning, out updateText, out unusedColor);

        OledRadarItem item = new OledRadarItem();
        item.Label = "IQ更新";
        item.Value = updateText;
        item.Severity = OledVariantPainting.Severity.Neutral;
        return item;
    }
}
