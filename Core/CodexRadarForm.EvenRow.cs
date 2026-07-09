using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

// EvenRow render variant (WidgetSettings.CodexRadarRenderVariant == EvenRow): a single row of
// seven weighted cells - time/token efficiency, 5h/weekly quota, IQ, quota radar, and a status
// cell with API/update lines. The left bottom third exposed by the compact ring cells now hosts
// compact software-family/RC/auxiliary/LLM metadata; the hidden five-stage connection flow is not painted here. Ignores
// CodexRadarManualLayoutEnabled and all CodexRadar*Offset* settings by design - see
// Docs/Interfaces/INTERFACE_INDEX.jsonl internal_api.codex_radar_render_variant. Only paint code
// belongs here; data gathering lives in Core/CodexRadarForm.cs and is shared with EvenGrid.
internal sealed partial class CodexRadarForm
{
    private void DrawCodexRadarModulesEvenRow(Graphics g, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        QuotaDisplayState quotaState = GatherQuotaDisplayState();

        // EvenRow v3 - a three-zone grid: [5 rings | radar bar] over one shared info band on the
        // left, and a content-sized status column on the right. The radar bar shares the rings'
        // compact height (both end at the same divider line), which lets the bottom brand/RC/aux/LLM
        // band span the rings AND the radar bar - that extra width is what buys the band a larger,
        // never-truncated font. The status column is measured from its real three lines but capped
        // at 36% of the window, so the big green text stays dominant without starving the left side.
        float elementGap = S(5);
        float ringGap = S(4);
        float leftInset = S(2);
        float rightInset = S(2);
        float compactCellHeight = Math.Max(S(32), bounds.Height * (2.0f / 3.0f));

        // Ring diameter from the compact cell height. Ring size is decoupled from column width via
        // evenLayoutRingFillFactor, so column width changes do not shrink the rings.
        float ringTextHeight = Math.Max(S(13), compactCellHeight * 0.18f);
        float ringAreaHeight = Math.Max(S(20), compactCellHeight - S(3) - S(2) - ringTextHeight);
        float ringDiameterCap = Math.Max(S(18), ringAreaHeight);

        float radarCellWidth = Math.Max(S(20), Math.Min(S(28), ringDiameterCap * 0.42f));
        // Tightened twice on request: base 10px -> 5px -> 3px per side of the radar bar at the
        // default 200% scale.
        float radarGap = elementGap * 0.3f;
        float statusWidth = GetEvenRowStatusZoneWidth(g, bounds, radarSnapshot);
        // +S(6) slack (was +S(8)): trims the empty shoulder each ring cell carries, which is also
        // what sat between the 5th ring and the radar bar.
        float ringCellWidthCap = ringDiameterCap + S(6);
        float ringsAvailable = Math.Max(S(60), bounds.Width - leftInset - rightInset - statusWidth - radarCellWidth - radarGap * 2.0f - ringGap * 4.0f);
        float ringCellWidth = Math.Max(S(20), Math.Min(ringCellWidthCap, ringsAvailable / 5.0f));
        this.evenLayoutRingFillFactor = Math.Max(0.6f, Math.Min(0.99f, ringDiameterCap / ringCellWidthCap));

        float x = bounds.Left + leftInset;
        // Every ring cell has identical width/height, so the divider height is the same for all
        // five - compute it once so the radar bar can share the rings' exact compact height.
        float dividerY = GetEvenRowCompactDividerY(
            GetEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight));
        RectangleF cellRect;

        cellRect = GetEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawEvenLayoutEfficiencyCell(g, cellRect, radarSnapshot, true);
        x += ringCellWidth + ringGap;

        cellRect = GetEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawEvenLayoutEfficiencyCell(g, cellRect, radarSnapshot, false);
        x += ringCellWidth + ringGap;

        cellRect = GetEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawEvenLayoutQuotaCell(
            g,
            cellRect,
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
            false,
            quotaState.ForceDangerRing);
        x += ringCellWidth + ringGap;

        cellRect = GetEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawEvenLayoutQuotaCell(
            g,
            cellRect,
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
            true,
            quotaState.ForceDangerRing);
        x += ringCellWidth + ringGap;

        cellRect = GetEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawEvenLayoutIqCell(g, cellRect, radarSnapshot);
        x += ringCellWidth;

        this.evenLayoutRingFillFactor = 0.86f;

        // The divider spans only the ring block, so the radar bar can run the full window height
        // ("额度线顶到底"). The info band below the divider therefore also spans only the rings -
        // it holds three items (brand, RC, LLM) since the DeepSeek entry was removed.
        float ringBlockRight = x;
        DrawEvenRowCompactDivider(g, bounds.Left + leftInset, ringBlockRight, dividerY);
        RectangleF bottomInfoRect = RectangleF.FromLTRB(
            bounds.Left + leftInset,
            dividerY,
            Math.Max(bounds.Left + leftInset + 1.0f, ringBlockRight),
            bounds.Bottom);
        DrawEvenRowBottomInfoPanel(g, bottomInfoRect, radarSnapshot);

        float radarLeft = ringBlockRight + radarGap;
        float radarRight = radarLeft + radarCellWidth;
        DrawEvenLayoutRadarCell(g, new RectangleF(radarLeft, bounds.Top, radarCellWidth, bounds.Height), radarSnapshot);

        float statusLeft = radarRight + radarGap;
        float statusCellWidth = Math.Max(S(40), bounds.Right - rightInset - statusLeft);
        DrawEvenRowStatusCell(g, new RectangleF(statusLeft, bounds.Top, statusCellWidth, bounds.Height), radarSnapshot);
    }

    // Width of the right status zone: the 24h dial is sized by the window HEIGHT (it's a circle),
    // plus the vertical LED column at the right edge. Capped at 36% of the window as a safety net
    // for unusually short/wide custom sizes (the sketch's "≈1/3" is naturally approached at the
    // default geometry).
    private float GetEvenRowStatusZoneWidth(Graphics g, RectangleF bounds, CodexRadarSnapshot radarSnapshot)
    {
        // dial + gap + LED column + slim left/right pads (S(1)/S(2)) - matches
        // DrawEvenRowStatusCell's real layout so the zone is exactly content-sized.
        float dialDiameter = Math.Max(S(24), bounds.Height - S(2));
        float desired = dialDiameter + S(3) + S(14) + S(1) + S(2);
        return Math.Max(S(52), Math.Min(bounds.Width * 0.36f, desired));
    }

    // "7.5_pm" -> hero "7.5" + suffix "pm" (the claudecoderadar.com daily-batch identity);
    // "7/6 10:59" (date + time) also splits so the time can become the dial's batch marker.
    // Labels in any other shape stay whole in the hero slot.
    private static void SplitEvenRowStatusHeroLabel(string dataLabelText, out string heroMain, out string heroSuffix)
    {
        string raw = (dataLabelText ?? string.Empty).Trim();
        int underscore = raw.IndexOf('_');
        if (underscore > 0 && underscore < raw.Length - 1)
        {
            heroMain = raw.Substring(0, underscore);
            heroSuffix = raw.Substring(underscore + 1);
            return;
        }

        int space = raw.LastIndexOf(' ');
        if (space > 0 && space < raw.Length - 1 && raw.IndexOf(':', space) > space)
        {
            heroMain = raw.Substring(0, space);
            heroSuffix = raw.Substring(space + 1);
            return;
        }

        heroMain = raw;
        heroSuffix = string.Empty;
    }


    private RectangleF GetEvenRowCompactRingCellRect(RectangleF fullCellRect, float compactHeight)
    {
        // The compact ring cell occupies the top region only. Do NOT use the old full-cell Y-shift
        // (it pushed the ring low, leaving a big gap above - "圆环上部空余太大"). Instead nudge the
        // cell down just enough that the ring is visually centered between the cell TOP and its
        // label glyph, so the space above and below the ring reads as equal.
        RectangleF compactCellRect = new RectangleF(
            fullCellRect.Left,
            fullCellRect.Top,
            fullCellRect.Width,
            Math.Max(S(24), compactHeight));

        RectangleF ringRect;
        RectangleF textRect;
        GetEvenLayoutCellRects(compactCellRect, out ringRect, out textRect);
        float topGap = ringRect.Top - compactCellRect.Top;
        // Approximate the label glyph position (glyphs sit around the middle of textRect).
        float labelGlyphRef = textRect.Top + textRect.Height * 0.4f;
        float bottomGap = labelGlyphRef - ringRect.Bottom;
        float shift = (bottomGap - topGap) * 0.5f;
        if (shift > 0.0f)
        {
            compactCellRect.Y += Math.Min(shift, S(10));
        }

        return compactCellRect;
    }

    private float GetEvenRowCompactDividerY(RectangleF compactCellRect)
    {
        RectangleF ringRect;
        RectangleF textRect;
        GetEvenLayoutCellRects(compactCellRect, out ringRect, out textRect);
        return textRect.Bottom + S(1);
    }

    private void DrawEvenRowCompactDivider(Graphics g, float left, float right, float y)
    {
        if (right <= left)
        {
            return;
        }

        // Draw exactly at y so the caller can center the bottom panel between this line and the
        // window bottom edge using the same value.
        using (Pen dividerPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 118), Math.Max(1.0f, S(1))))
        {
            g.DrawLine(dividerPen, left + S(2), y, right - S(2), y);
        }
    }

    // Graphical status card, per the user's sketch (proposal 2 + LED column): a period dial as
    // the main element. The small green dot marks the latest actual IQ refresh that is still inside
    // the retention window, and the visible arc connects that refresh point to "now". The 12
    // o'clock green tick is only the cycle boundary marker. A vertical per-service LED column
    // (R/O/C/D) sits at the right edge.
    private void DrawEvenRowStatusCell(Graphics g, RectangleF cellRect, CodexRadarSnapshot radarSnapshot)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        // The LED column is anchored to the cell's RIGHT edge (its dots must not move), while the
        // dial is shifted ~3px further left (dialShift) so it reads as optically centered between
        // the radar bar and the LED dots.
        float leftPad = S(1);
        float rightPad = S(2);
        float ledColumnWidth = S(14);
        float dialShift = S(3) * 0.5f;
        float ledLeft = cellRect.Right - rightPad - ledColumnWidth;
        RectangleF ledArea = new RectangleF(ledLeft, cellRect.Top, ledColumnWidth, cellRect.Height);
        float dialLeft = cellRect.Left + leftPad - dialShift;
        RectangleF dialArea = new RectangleF(
            dialLeft,
            cellRect.Top,
            Math.Max(1.0f, ledLeft - S(3) - dialLeft),
            cellRect.Height);

        DrawEvenRowBatchDial(g, dialArea, radarSnapshot);
        DrawEvenRowServiceLedColumn(g, ledArea);
    }

    // Proposal 1 (vertical variant per the sketch): one LED per service stacked at the right edge.
    // D is always visible and represents the public DeepSeek API service probe; the Claude-mode
    // bottom DS text is only the optional balance display. Healthy services show a green dot;
    // alert candidates recolor their dot; ":checking" blinks.
    private void DrawEvenRowServiceLedColumn(Graphics g, RectangleF rect)
    {
        CodexConnectionAlertCandidate[] candidates = GetCodexApiServiceAlertCandidates();
        string[] labels = new string[] { "R", "O", "C", "D" };
        string[] prefixes = new string[] { "rader", "openai", "claude", "deepseek" };

        float rowHeight = rect.Height / labels.Length;
        float dotDiameter = S(5);
        Font letterFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.LayerScale), FontStyle.Bold);
        for (int i = 0; i < labels.Length; i++)
        {
            Color ledColor = DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
            bool checking = false;
            for (int c = 0; candidates != null && c < candidates.Length; c++)
            {
                string key = candidates[c].Key ?? string.Empty;
                if (!key.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ledColor = candidates[c].Color;
                checking = key.IndexOf(":checking", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!checking)
                {
                    break;
                }
            }

            if (checking && (this.renderTickCount & 1) == 0)
            {
                ledColor = DesignTokens.WithAlpha(ledColor, 104);
            }

            float rowTop = rect.Top + rowHeight * i;
            float centerY = rowTop + rowHeight / 2.0f;
            using (SolidBrush dotBrush = new SolidBrush(ledColor))
            {
                g.FillEllipse(dotBrush, rect.Left, centerY - dotDiameter / 2.0f, dotDiameter, dotDiameter);
            }

            RectangleF letterRect = new RectangleF(
                rect.Left + dotDiameter + S(2),
                rowTop,
                Math.Max(1.0f, rect.Width - dotDiameter - S(2)),
                rowHeight);
            using (SolidBrush letterBrush = new SolidBrush(DesignTokens.White(170)))
            {
                DrawEvenRowStatusText(g, labels[i], letterFont, letterBrush, letterRect);
            }
        }
    }

    // Period clock for the current radar software: Codex is a 12h circle (00:00 and 12:00
    // boundaries); Claude-in-shared-window is a 24h circle. The pointer advances clockwise from
    // the top boundary. Green means this model has IQ data for the current period, yellow means the
    // current period is still waiting, and red overlays the yellow base after the previous full
    // period was missed.
    private void DrawEvenRowBatchDial(Graphics g, RectangleF rect, CodexRadarSnapshot radarSnapshot)
    {
        bool radarRequestRunning;
        lock (this.codexRadarStatusLock)
        {
            radarRequestRunning = this.codexRadarStatusRequestRunning;
        }

        string updateText;
        Color legacyUpdateColor;
        GetCodexModelIqUpdateStatusText(radarSnapshot, radarRequestRunning, out updateText, out legacyUpdateColor);

        DateTime nowLocal = DateTime.Now;
        double cycleHours = GetEvenRowDialCycleHours();
        DateTime cycleBoundaryLocal = GetEvenRowDialCycleBoundaryLocal(nowLocal, cycleHours);
        bool batchKnown = radarSnapshot != null && radarSnapshot.ModelIqDataDateKnown;
        DateTime batchTime = batchKnown
            ? radarSnapshot.ModelIqDataDateLocal.Date.AddHours(radarSnapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
            : DateTime.MinValue;
        bool localKnown = radarSnapshot != null && radarSnapshot.ModelIqRefreshedAtKnown;
        DateTime localTime = localKnown ? radarSnapshot.ModelIqRefreshedAtLocal : DateTime.MinValue;
        bool overdue = IsEvenRowDialOverdue(batchKnown, batchTime, cycleHours, nowLocal);
        Color updateColor = ComputeEvenRowDialStatusColor(batchKnown, batchTime, localKnown, localTime, cycleHours, nowLocal);
        if (radarRequestRunning && (this.renderTickCount & 1) == 0)
        {
            updateColor = DesignTokens.WithAlpha(updateColor, 104);
        }

        string timeText = GetEvenRowDialTimeText(radarSnapshot, nowLocal);
        string modeText = GetEvenRowDialModeLabel();

        string dataLabelText = GetCodexModelIqDataLabelDisplayText(radarSnapshot);
        string heroMain;
        string heroSuffix;
        SplitEvenRowStatusHeroLabel(dataLabelText, out heroMain, out heroSuffix);
        bool phaseKnown;
        bool night;
        bool secondRun;
        string suffixTimeText;
        ParseEvenRowBatchSuffix(heroSuffix, out phaseKnown, out night, out secondRun, out suffixTimeText);

        float dialDiameter = Math.Min(rect.Width, rect.Height) - S(1);
        dialDiameter = Math.Max(S(20), dialDiameter);
        // Left-aligned (not centered): at the ideal window width there is no slack anyway, and at
        // wider widths the leftover collects toward the LED column instead of around the dial.
        RectangleF dial = new RectangleF(
            rect.Left,
            rect.Top + (rect.Height - dialDiameter) / 2.0f,
            dialDiameter,
            dialDiameter);
        float stroke = Math.Max(2.0f, S(2));
        RectangleF arcRect = new RectangleF(
            dial.Left + stroke / 2.0f,
            dial.Top + stroke / 2.0f,
            dial.Width - stroke,
            dial.Height - stroke);

        using (Pen trackPen = new Pen(DesignTokens.White(46), stroke))
        {
            g.DrawArc(trackPen, arcRect, -90.0f, 360.0f);
        }

        float boundaryAngle = -90.0f;
        double elapsedHours = (nowLocal - cycleBoundaryLocal).TotalHours;
        if (elapsedHours < 0.0)
        {
            elapsedHours = 0.0;
        }

        if (elapsedHours > cycleHours)
        {
            elapsedHours = cycleHours;
        }

        float elapsedSweep = (float)(elapsedHours / cycleHours * 360.0);
        float currentAngle = boundaryAngle + elapsedSweep;
        float refreshMarkerAngle;
        bool refreshMarkerVisible = TryGetEvenRowClockMarkerAngle(
            localTime,
            nowLocal,
            cycleBoundaryLocal,
            cycleHours,
            out refreshMarkerAngle);
        float arcStartAngle = refreshMarkerVisible ? refreshMarkerAngle : boundaryAngle;
        float drawSweep = refreshMarkerVisible
            ? ComputeEvenRowClockSweep(refreshMarkerAngle, currentAngle)
            : (overdue ? elapsedSweep : 0.0f);
        if (overdue)
        {
            using (Pen basePen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 220), stroke))
            {
                basePen.StartCap = LineCap.Round;
                basePen.EndCap = LineCap.Round;
                g.DrawArc(basePen, arcRect, boundaryAngle, 360.0f);
            }

            drawSweep = Math.Max(2.0f, drawSweep);
        }

        if (drawSweep > 1.0f)
        {
            using (Pen arcPen = new Pen(updateColor, stroke))
            {
                arcPen.StartCap = LineCap.Round;
                arcPen.EndCap = LineCap.Round;
                g.DrawArc(arcPen, arcRect, arcStartAngle, drawSweep);
            }
        }

        DrawEvenRowClockBoundaryTick(
            g,
            arcRect,
            Math.Max(1.0f, stroke * 0.74f),
            DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245));

        if (refreshMarkerVisible)
        {
            DrawEvenRowClockDot(
                g,
                arcRect,
                refreshMarkerAngle,
                Math.Max(2.5f, S(3)),
                DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245));
        }

        DrawEvenRowClockDot(
            g,
            arcRect,
            currentAngle,
            Math.Max(3.0f, S(4)),
                DesignTokens.White(235));

        double markerRadians = boundaryAngle * Math.PI / 180.0;
        float radius = arcRect.Width / 2.0f;
        float markerX = arcRect.Left + radius + (float)Math.Cos(markerRadians) * radius;
        float markerY = arcRect.Top + radius + (float)Math.Sin(markerRadians) * radius;
        float markerDiameter = Math.Max(3.0f, S(4));
        if (secondRun)
        {
            // Second evening test: a small warning-colored "2" just inside the marker.
            Font badgeFont = this.fontCache.GetUi(Math.Max(6.5f, 7.0f * this.LayerScale), FontStyle.Bold);
            float badgeOffset = markerDiameter + S(2);
            RectangleF badgeRect = new RectangleF(
                markerX - (float)Math.Cos(markerRadians) * badgeOffset - S(4),
                markerY - (float)Math.Sin(markerRadians) * badgeOffset - S(4),
                S(8),
                S(8));
            using (SolidBrush badgeBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245)))
            {
                DrawEvenRowStatusText(g, "2", badgeFont, badgeBrush, badgeRect);
            }
        }

        // Center: keep the established date/time rectangles stable; the mode label gets its own
        // narrow slot below them so adding UTC/LAST/REF/NOW does not shift existing elements.
        float innerWidth = dial.Width * 0.72f;
        Font dayFont = this.fontCache.GetUi(Math.Max(9.0f, 11.5f * this.LayerScale), FontStyle.Bold);
        Font timeFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.LayerScale), FontStyle.Bold);
        Font modeFont = this.fontCache.GetUi(Math.Max(5.0f, 5.4f * this.LayerScale), FontStyle.Bold);
        float centerX2 = dial.Left + dial.Width / 2.0f;
        float centerY2 = dial.Top + dial.Height / 2.0f;
        RectangleF dayRect = new RectangleF(centerX2 - innerWidth / 2.0f, centerY2 - S(11), innerWidth, S(11));
        RectangleF timeRect = new RectangleF(centerX2 - innerWidth / 2.0f, centerY2 + S(1), innerWidth, S(8));
        RectangleF modeRect = new RectangleF(centerX2 - innerWidth / 2.0f, centerY2 + S(9), innerWidth, S(6));
        using (SolidBrush dayBrush = new SolidBrush(DesignTokens.White(235)))
        using (SolidBrush timeBrush = new SolidBrush(updateColor))
        {
            DrawCodexRadarFittedText(g, FormatEvenRowDialDate(heroMain), dayFont, dayBrush, dayRect, StringAlignment.Center, 6.0f);
            DrawCodexRadarFittedText(g, timeText, timeFont, timeBrush, timeRect, StringAlignment.Center, 6.0f);
            DrawCodexRadarFittedText(g, modeText, modeFont, timeBrush, modeRect, StringAlignment.Center, 4.5f);
        }
    }

    private string GetEvenRowDialModeLabel()
    {
        RadarClockTimeDisplayMode mode = this.currentSettings == null
            ? RadarClockTimeDisplayMode.Utc
            : this.currentSettings.RadarClockTimeDisplayMode;
        return GetRadarClockTimeDisplayModeShortLabel(mode);
    }

    private static string GetRadarClockTimeDisplayModeShortLabel(RadarClockTimeDisplayMode mode)
    {
        switch (mode)
        {
            case RadarClockTimeDisplayMode.CurrentLocal:
                return "NOW";
            case RadarClockTimeDisplayMode.LastAttemptRefresh:
                return "LAST";
            case RadarClockTimeDisplayMode.LastActualRefresh:
                return "REF";
            case RadarClockTimeDisplayMode.Utc:
            default:
                return "UTC";
        }
    }

    private string GetEvenRowDialTimeText(
        CodexRadarSnapshot snapshot,
        DateTime nowLocal)
    {
        RadarClockTimeDisplayMode mode = this.currentSettings == null
            ? RadarClockTimeDisplayMode.Utc
            : this.currentSettings.RadarClockTimeDisplayMode;
        DateTime candidate;
        bool known;
        switch (mode)
        {
            case RadarClockTimeDisplayMode.CurrentLocal:
                return nowLocal.ToString("HH:mm", CultureInfo.CurrentCulture);
            case RadarClockTimeDisplayMode.LastAttemptRefresh:
                known = TryGetEvenRowLastAttemptRefreshLocal(snapshot, out candidate);
                return known ? candidate.ToString("HH:mm", CultureInfo.CurrentCulture) : "--:--";
            case RadarClockTimeDisplayMode.LastActualRefresh:
                known = snapshot != null &&
                    snapshot.ModelIqRefreshedAtKnown &&
                    snapshot.ModelIqRefreshedAtLocal != DateTime.MinValue;
                return known ? snapshot.ModelIqRefreshedAtLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "--:--";
            case RadarClockTimeDisplayMode.Utc:
            default:
                return DateTime.UtcNow.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private bool TryGetEvenRowLastAttemptRefreshLocal(CodexRadarSnapshot snapshot, out DateTime localTime)
    {
        if (this.lastCodexRadarStatusAttemptLocal != DateTime.MinValue)
        {
            localTime = this.lastCodexRadarStatusAttemptLocal;
            return true;
        }

        if (snapshot != null &&
            snapshot.CheckedAtKnown &&
            snapshot.CheckedAtLocal != DateTime.MinValue)
        {
            localTime = snapshot.CheckedAtLocal;
            return true;
        }

        localTime = DateTime.MinValue;
        return false;
    }

    // Boundary-based freshness color. The local fetch timestamp is intentionally not treated as
    // proof of IQ freshness: only the model's own IQ data window can turn the clock green.
    private static Color ComputeEvenRowDialStatusColor(
        bool batchKnown,
        DateTime batchTime,
        bool localKnown,
        DateTime localTime,
        double cycleHours,
        DateTime now)
    {
        if (!batchKnown && !localKnown)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        DateTime boundary = GetEvenRowDialCycleBoundaryLocal(now, cycleHours);
        DateTime previousBoundary = boundary.AddHours(-cycleHours);
        if (batchTime >= boundary)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
        }

        if (batchTime >= previousBoundary)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
    }

    private double GetEvenRowDialCycleHours()
    {
        return GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude ? 24.0 : 12.0;
    }

    private static bool IsEvenRowDialOverdue(
        bool batchKnown,
        DateTime batchTime,
        double cycleHours,
        DateTime now)
    {
        if (!batchKnown)
        {
            return false;
        }

        DateTime boundary = GetEvenRowDialCycleBoundaryLocal(now, cycleHours);
        DateTime previousBoundary = boundary.AddHours(-cycleHours);
        return batchTime < previousBoundary;
    }

    private static DateTime GetEvenRowDialCycleBoundaryLocal(DateTime now, double cycleHours)
    {
        if (cycleHours >= 23.5)
        {
            return now.Date;
        }

        int cycle = Math.Max(1, (int)Math.Round(cycleHours));
        int hour = (now.Hour / cycle) * cycle;
        return now.Date.AddHours(hour);
    }

    private static bool TryGetEvenRowClockMarkerAngle(
        DateTime markerTime,
        DateTime now,
        DateTime cycleBoundary,
        double cycleHours,
        out float angle)
    {
        angle = -90.0f;
        if (markerTime == DateTime.MinValue || cycleHours <= 0.0)
        {
            return false;
        }

        double ageHours = (now - markerTime).TotalHours;
        if (ageHours < 0.0 || ageHours >= cycleHours)
        {
            return false;
        }

        double elapsedHours = (markerTime - cycleBoundary).TotalHours;
        while (elapsedHours < 0.0)
        {
            elapsedHours += cycleHours;
        }

        while (elapsedHours >= cycleHours)
        {
            elapsedHours -= cycleHours;
        }

        angle = -90.0f + (float)(elapsedHours / cycleHours * 360.0);
        return true;
    }

    private static float ComputeEvenRowClockSweep(float startAngle, float endAngle)
    {
        float sweep = endAngle - startAngle;
        while (sweep < 0.0f)
        {
            sweep += 360.0f;
        }

        while (sweep > 360.0f)
        {
            sweep -= 360.0f;
        }

        return sweep;
    }

    private static void DrawEvenRowClockDot(
        Graphics g,
        RectangleF arcRect,
        float angle,
        float diameter,
        Color color)
    {
        double radians = angle * Math.PI / 180.0;
        float radius = arcRect.Width / 2.0f;
        float x = arcRect.Left + radius + (float)Math.Cos(radians) * radius;
        float y = arcRect.Top + radius + (float)Math.Sin(radians) * radius;
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.FillEllipse(brush, x - diameter / 2.0f, y - diameter / 2.0f, diameter, diameter);
        }
    }

    private static void DrawEvenRowClockBoundaryTick(
        Graphics g,
        RectangleF arcRect,
        float stroke,
        Color color)
    {
        if (arcRect.Width <= 0.0f || arcRect.Height <= 0.0f)
        {
            return;
        }

        float x = arcRect.Left + arcRect.Width / 2.0f;
        float y = arcRect.Top;
        float length = Math.Max(3.0f, arcRect.Height * 0.18f);
        using (Pen pen = new Pen(color, stroke))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, x, y - length * 0.35f, x, y + length * 0.65f);
        }
    }

    // "7.6" or "7/6" -> "7月6日"; anything unparseable (or already containing 月) stays as-is.
    private static string FormatEvenRowDialDate(string heroMain)
    {
        string raw = (heroMain ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.IndexOf('月') >= 0)
        {
            return raw;
        }

        char[] separators = new char[] { '.', '/' };
        int sep = raw.IndexOfAny(separators);
        int month;
        int day;
        if (sep > 0 && sep < raw.Length - 1 &&
            int.TryParse(raw.Substring(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out month) &&
            int.TryParse(raw.Substring(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out day) &&
            month >= 1 && month <= 12 && day >= 1 && day <= 31)
        {
            return month.ToString(CultureInfo.InvariantCulture) + "月" + day.ToString(CultureInfo.InvariantCulture) + "日";
        }

        return raw;
    }

    // Batch marker hour on the 24h dial: am -> 0:00, pm -> 12:00, "HH:mm" suffix -> that time.
    private static bool TryGetEvenRowBatchHour(bool phaseKnown, bool night, string suffixTimeText, out float batchHour)
    {
        if (phaseKnown)
        {
            batchHour = night ? 12.0f : 0.0f;
            return true;
        }

        if (!string.IsNullOrEmpty(suffixTimeText))
        {
            int colon = suffixTimeText.IndexOf(':');
            int hour;
            int minute;
            if (colon > 0 &&
                int.TryParse(suffixTimeText.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) &&
                int.TryParse(suffixTimeText.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute) &&
                hour >= 0 && hour < 24 && minute >= 0 && minute < 60)
            {
                batchHour = hour + minute / 60.0f;
                return true;
            }
        }

        batchHour = 0.0f;
        return false;
    }

    // Suffix shapes: "am" -> day; "pm" -> night; "pm2"/"pm_2"/"pm-2" -> second evening test;
    // "01:01"-style -> small time text; anything else is ignored.
    private static void ParseEvenRowBatchSuffix(
        string suffix,
        out bool phaseKnown,
        out bool night,
        out bool secondRun,
        out string suffixTimeText)
    {
        string raw = (suffix ?? string.Empty).Trim().ToLowerInvariant();
        phaseKnown = false;
        night = false;
        secondRun = false;
        suffixTimeText = string.Empty;
        if (raw.Length == 0)
        {
            return;
        }

        if (raw.StartsWith("am", StringComparison.Ordinal))
        {
            phaseKnown = true;
            return;
        }

        if (raw.StartsWith("pm", StringComparison.Ordinal))
        {
            phaseKnown = true;
            night = true;
            string rest = raw.Substring(2).TrimStart('_', '-', ' ');
            int run;
            secondRun = rest.Length > 0 && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out run) && run >= 2;
            return;
        }

        if (raw.IndexOf(':') > 0)
        {
            suffixTimeText = raw;
        }
    }

    private void DrawEvenRowBottomInfoPanel(Graphics g, RectangleF rect, CodexRadarSnapshot radarSnapshot)
    {
        if (rect.Width <= S(24) || rect.Height <= S(8))
        {
            return;
        }

        // Brand first (leftmost), then RC, a mode-specific auxiliary value and LLM sharing the
        // freed space. The auxiliary value uses cached snapshots only; painting must not trigger
        // token reads or HTTP requests.
        string ratingText = GetEvenRowBottomRatingDisplayText(radarSnapshot);
        string auxiliaryText = GetEvenRowBottomAuxiliaryDisplayText();
        string modelText = GetEvenRowBottomModelDisplayText(radarSnapshot);
        string serviceFamilyText = GetCodexRadarServiceFamilyDisplayText();
        string[] texts = new string[] { serviceFamilyText, ratingText, auxiliaryText, modelText };
        Color bottomTextColor = DesignTokens.White(206);

        // One shared size keeps the bottom items aligned, but the brand item (index 0) carries its
        // own style so Codex/Claude can be distinguished without the old "SF:" prefix.
        float gap = S(6);
        float available = Math.Max(1.0f, rect.Width - gap * (texts.Length - 1));
        Font font = GetEvenRowBottomInfoSharedFont(g, texts, available);
        Font serviceFamilyFont = this.fontCache.GetUi(font.Size, GetCodexRadarServiceFamilyFontStyle());

        float measuredTotal = 0.0f;
        float[] widths = new float[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            Font itemFont = i == 0 ? serviceFamilyFont : font;
            widths[i] = g.MeasureString(texts[i] ?? string.Empty, itemFont).Width;
            measuredTotal += widths[i];
        }

        // Distribute any leftover width evenly so items spread across the row instead of packing
        // to the left; each item's rect is at least its measured width, so nothing truncates.
        float leftover = Math.Max(0.0f, available - measuredTotal);
        float share = leftover / texts.Length;
        float x = rect.Left;
        for (int i = 0; i < texts.Length; i++)
        {
            float width = i == texts.Length - 1
                ? Math.Max(1.0f, rect.Right - x)
                : widths[i] + share;
            RectangleF itemRect = new RectangleF(x, rect.Top, Math.Max(1.0f, width), rect.Height);
            Font itemFont = i == 0 ? serviceFamilyFont : font;
            Color itemColor = i == 0 ? GetCodexRadarServiceFamilyDisplayColor() : bottomTextColor;
            using (SolidBrush brush = new SolidBrush(itemColor))
            {
                DrawEvenRowBottomInfoText(g, texts[i], itemFont, brush, itemRect);
            }

            x += width + gap;
        }
    }

    private string GetEvenRowBottomAuxiliaryDisplayText()
    {
        return GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude
            ? GetDeepSeekBalanceDisplayText()
            : GetCodexResetCreditsDisplayText();
    }

    private string GetDeepSeekBalanceDisplayText()
    {
        return DeepSeekBalanceMonitor.FormatDisplayText(DeepSeekBalanceMonitor.GetSnapshot());
    }

    private FontStyle GetCodexRadarServiceFamilyFontStyle()
    {
        return GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude
            ? FontStyle.Bold
            : FontStyle.Italic;
    }

    private Color GetCodexRadarServiceFamilyDisplayColor()
    {
        return GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude
            ? GetCodexRadarSoftwareChromeColor(CodexRadarSoftwareMode.Claude)
            : DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 245);
    }

    // Largest shared font (down to a low floor) at which the four bottom-info items fit their
    // total width in the available row width. Guarantees no per-item ellipsis for typical data.
    private Font GetEvenRowBottomInfoSharedFont(Graphics g, string[] texts, float available)
    {
        float baseSize = Math.Max(8.5f, 10.5f * this.LayerScale * 1.56f);
        float minSize = Math.Max(4.5f, 4.5f * this.LayerScale);
        float step = Math.Max(0.3f, 0.3f * this.LayerScale);
        float size = baseSize;
        while (size > minSize)
        {
            Font candidate = this.fontCache.GetUi(size, FontStyle.Bold);
            float total = 0.0f;
            for (int i = 0; i < texts.Length; i++)
            {
                total += g.MeasureString(texts[i] ?? string.Empty, candidate).Width;
            }

            if (total <= available * 0.99f)
            {
                return candidate;
            }

            size -= step;
        }

        return this.fontCache.GetUi(Math.Max(minSize, size), FontStyle.Bold);
    }

    private string GetCodexRadarCurrentModelDisplayText()
    {
        string key = this.currentSettings == null
            ? CodexRadarModelCatalog.DefaultModelKey
            : this.currentSettings.CodexRadarModelKey;
        string label = CodexRadarModelCatalog.GetDisplayLabel(string.Empty, key);
        string shortLabel = FormatCodexRadarCurrentModelShortLabel(key, label);
        return "LLM:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private static string FormatCodexRadarCurrentModelShortLabel(string key, string label)
    {
        string raw = !string.IsNullOrWhiteSpace(label) ? label : key;
        string lower = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.Length == 0)
        {
            return string.Empty;
        }

        Match gpt = Regex.Match(lower, "gpt[-\\s_]*([0-9]+(?:\\.[0-9]+)?)\\s*[-\\s_]*(xhigh|high|medium|low)?");
        if (gpt.Success)
        {
            string suffix = gpt.Groups[2].Value;
            string effort = string.Empty;
            if (suffix == "xhigh") effort = "XH";
            else if (suffix == "high") effort = "H";
            else if (suffix == "medium") effort = "M";
            else if (suffix == "low") effort = "L";
            return gpt.Groups[1].Value + effort;
        }

        string compact = Regex.Replace(raw, "[^A-Za-z0-9.]+", string.Empty);
        return compact.Length <= 8 ? compact : compact.Substring(0, 8);
    }

    private string GetEvenRowBottomRatingDisplayText(CodexRadarSnapshot radarSnapshot)
    {
        if (GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude)
        {
            return GetClaudeCommunityRatingDisplayText(radarSnapshot);
        }

        return GetCodexCommunityRatingDisplayText(radarSnapshot);
    }

    private string GetEvenRowBottomModelDisplayText(CodexRadarSnapshot radarSnapshot)
    {
        if (GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude)
        {
            return GetClaudeRadarCurrentModelDisplayText(radarSnapshot);
        }

        return GetCodexRadarCurrentModelDisplayText();
    }

    private static string GetClaudeCommunityRatingDisplayText(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.CommunityRatingKnown)
        {
            return "RC:--";
        }

        string shortLabel = FormatCodexCommunityRatingShortLabel(snapshot.CommunityRatingModelId, snapshot.CommunityRatingLabel);
        return "RC:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private string GetClaudeRadarCurrentModelDisplayText(CodexRadarSnapshot radarSnapshot)
    {
        string key = this.currentSettings == null
            ? string.Empty
            : (this.currentSettings.ClaudeRadarModelKey ?? string.Empty);
        string label = string.Empty;
        try
        {
            List<ClaudeRadarModelEntry> entries = ClaudeRadarReader.LoadModelMap();
            for (int i = 0; entries != null && i < entries.Count; i++)
            {
                ClaudeRadarModelEntry entry = entries[i];
                if (entry != null &&
                    string.Equals(entry.SourceKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    label = entry.DisplayName;
                    break;
                }
            }
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(label) &&
            radarSnapshot != null &&
            !string.IsNullOrWhiteSpace(radarSnapshot.CommunityRatingLabel))
        {
            label = radarSnapshot.CommunityRatingLabel;
        }

        string shortLabel = FormatCodexCommunityRatingShortLabel(key, label);
        return "LLM:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private Font GetEvenRowStatusSharedFont(Graphics g, string[] texts, RectangleF[] rects)
    {
        float baseSize = Math.Max(9.0f, 12.5f * this.LayerScale * 1.50f);
        // Lowered from 7.6 so the status column can shrink enough to actually fit once the radar bar
        // is pinned at its right-quarter target (which leaves this column less room than before).
        float minSize = Math.Max(4.5f, 4.5f * this.LayerScale);
        float step = Math.Max(0.35f, 0.35f * this.LayerScale);
        return GetEvenRowSharedFont(g, texts, rects, baseSize, minSize, step);
    }

    private Font GetEvenRowSharedFont(Graphics g, string[] texts, RectangleF[] rects, float baseSize, float minSize, float step)
    {
        float size = baseSize;
        while (size > minSize)
        {
            Font candidate = this.fontCache.GetUi(size, FontStyle.Bold);
            if (EvenRowStatusTextFits(g, candidate, texts, rects))
            {
                return candidate;
            }

            size -= step;
        }

        return this.fontCache.GetUi(Math.Max(minSize, size), FontStyle.Bold);
    }

    private static bool EvenRowStatusTextFits(Graphics g, Font font, string[] texts, RectangleF[] rects)
    {
        if (texts == null || rects == null)
        {
            return true;
        }

        int count = Math.Min(texts.Length, rects.Length);
        for (int i = 0; i < count; i++)
        {
            string text = texts[i] ?? string.Empty;
            RectangleF rect = rects[i];
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            SizeF measured = g.MeasureString(text, font);
            if (measured.Width > rect.Width * 0.98f || measured.Height > rect.Height * 1.18f)
            {
                return false;
            }
        }

        return true;
    }

    private static void DrawEvenRowStatusText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(text ?? string.Empty, font, brush, rect, format);
        }
    }

    private static void DrawEvenRowBottomInfoText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        string value = text ?? string.Empty;
        if (value.Length == 0)
        {
            return;
        }

        float offsetX;
        float offsetY;
        if (TryMeasureEvenRowDrawnTextCenterOffset(g, value, font, rect.Size, out offsetX, out offsetY))
        {
            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(offsetX, offsetY);
                DrawEvenRowStatusText(g, value, font, brush, rect);
            }
            finally
            {
                g.Restore(state);
            }

            return;
        }

        DrawEvenRowStatusText(g, value, font, brush, rect);
    }

    private static bool TryMeasureEvenRowDrawnTextCenterOffset(
        Graphics sourceGraphics,
        string text,
        Font font,
        SizeF rectSize,
        out float offsetX,
        out float offsetY)
    {
        offsetX = 0.0f;
        offsetY = 0.0f;
        if (sourceGraphics == null || string.IsNullOrEmpty(text) || font == null ||
            rectSize.Width <= 1.0f || rectSize.Height <= 1.0f)
        {
            return false;
        }

        const int AlphaThreshold = 8;
        float margin = Math.Max(2.0f, font.Size * 0.25f);
        int width = Math.Max(4, (int)Math.Ceiling(rectSize.Width + margin * 2.0f));
        int height = Math.Max(4, (int)Math.Ceiling(rectSize.Height + margin * 2.0f));

        using (Bitmap bitmap = new Bitmap(width, height))
        using (Graphics measureGraphics = Graphics.FromImage(bitmap))
        using (SolidBrush measureBrush = new SolidBrush(Color.White))
        {
            measureGraphics.Clear(Color.Transparent);
            measureGraphics.SmoothingMode = sourceGraphics.SmoothingMode;
            measureGraphics.PixelOffsetMode = sourceGraphics.PixelOffsetMode;
            measureGraphics.TextRenderingHint = sourceGraphics.TextRenderingHint;
            measureGraphics.CompositingQuality = sourceGraphics.CompositingQuality;
            DrawEvenRowStatusText(
                measureGraphics,
                text,
                font,
                measureBrush,
                new RectangleF(margin, margin, rectSize.Width, rectSize.Height));

            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= AlphaThreshold)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return false;
            }

            float desiredCenterX = margin + rectSize.Width * 0.5f;
            float desiredCenterY = margin + rectSize.Height * 0.5f;
            float actualCenterX = (minX + maxX + 1.0f) * 0.5f;
            float actualCenterY = (minY + maxY + 1.0f) * 0.5f;
            offsetX = desiredCenterX - actualCenterX;
            offsetY = desiredCenterY - actualCenterY;
            if (Math.Abs(offsetX) > Math.Max(1.0f, rectSize.Width * 0.16f) ||
                Math.Abs(offsetY) > Math.Max(1.0f, rectSize.Height * 0.35f))
            {
                offsetX = 0.0f;
                offsetY = 0.0f;
                return false;
            }

            if (Math.Abs(offsetX) < 0.2f) offsetX = 0.0f;
            if (Math.Abs(offsetY) < 0.2f) offsetY = 0.0f;
            return true;
        }
    }
}
