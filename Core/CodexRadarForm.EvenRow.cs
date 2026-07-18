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
        if (quotaState.Snapshot.FiveHourLimitAbsent)
        {
            // 5-hour limit removed: repurpose this cell as the measured weekly burn-rate ring.
            DrawEvenLayoutWeeklyBurnRateCell(g, cellRect, quotaState);
        }
        else
        {
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
                quotaState.Snapshot.SourceKind,
                radarSnapshot,
                quotaState.ForceDangerRing,
                quotaState.QuotaResetRainbow,
                quotaState.QuotaResetCreditSubDay);
        }
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
            quotaState.Snapshot.SourceKind,
            radarSnapshot,
            quotaState.ForceDangerRing,
            quotaState.QuotaResetRainbow,
            quotaState.QuotaResetCreditSubDay);
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

    // The task ring belongs to Codex sessions, so it is assembled here rather than inside the shared
    // dial: the Claude radar draws the same dial and must not inherit Codex task state.
    private CodexTaskRingModel GetEvenRowCodexTaskRing()
    {
        if (this.CurrentSettings == null || !this.CurrentSettings.CodexTaskMonitorEnabled)
        {
            return null;
        }

        if (GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Codex)
        {
            return null;
        }

        if (!AlertPresentationPolicy.ShouldPresent(
            this.CurrentSettings,
            AlertPresentationCategory.CodexTask))
        {
            return null;
        }

        return CodexTaskPresentation.BuildRing(CodexTaskPresentation.GetSnapshot());
    }

    // Snapshot access remains window-owned; shared state/geometry/drawing lives in RadarClockDial.
    private void DrawEvenRowBatchDial(Graphics g, RectangleF rect, CodexRadarSnapshot radarSnapshot)
    {
        bool radarRequestRunning;
        lock (this.codexRadarStatusLock)
        {
            radarRequestRunning = this.codexRadarStatusRequestRunning;
        }

        DateTime nowLocal = DateTime.Now;
        if (this.CurrentSettings != null &&
            this.CurrentSettings.CodexRadarSpeedWindowCountdownEnabled &&
            DrawEvenRowSpeedWindowCountdownDial(g, rect, radarSnapshot, nowLocal))
        {
            return;
        }

        double cycleHours = GetEvenRowDialCycleHours();
        bool batchKnown = radarSnapshot != null && radarSnapshot.ModelIqDataDateKnown;
        DateTime batchTime = batchKnown
            ? radarSnapshot.ModelIqDataDateLocal.Date.AddHours(radarSnapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
            : DateTime.MinValue;
        bool localKnown = radarSnapshot != null && radarSnapshot.ModelIqRefreshedAtKnown;
        DateTime localTime = localKnown ? radarSnapshot.ModelIqRefreshedAtLocal : DateTime.MinValue;
        DateTime lastAttemptLocal;
        bool lastAttemptKnown = TryGetEvenRowLastAttemptRefreshLocal(radarSnapshot, out lastAttemptLocal);
        RadarClockTimeDisplayMode timeMode = this.CurrentSettings == null
            ? RadarClockTimeDisplayMode.Utc
            : this.CurrentSettings.RadarClockTimeDisplayMode;
        RadarClockDialState state = RadarClockDial.ComputeState(new RadarClockDialInput
        {
            BatchKnown = batchKnown,
            BatchTimeLocal = batchTime,
            LocalKnown = localKnown,
            RefreshMarkerTimeLocal = localTime,
            CycleHours = cycleHours,
            NowLocal = nowLocal,
            NowUtc = DateTime.UtcNow,
            RequestRunning = radarRequestRunning,
            RenderTick = this.renderTickCount,
            DataLabelText = GetCodexModelIqDataLabelDisplayText(radarSnapshot),
            TimeDisplayMode = timeMode,
            LastAttemptKnown = lastAttemptKnown,
            LastAttemptLocal = lastAttemptLocal,
            LastActualKnown = localKnown,
            LastActualLocal = localTime,
            TaskRing = GetEvenRowCodexTaskRing()
        });

        Font dayFont = this.fontCache.GetUi(Math.Max(9.0f, 11.5f * this.LayerScale), FontStyle.Bold);
        Font timeFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.LayerScale), FontStyle.Bold);
        Font modeFont = this.fontCache.GetUi(Math.Max(5.0f, 5.4f * this.LayerScale), FontStyle.Bold);
        Font badgeFont = this.fontCache.GetUi(Math.Max(6.5f, 7.0f * this.LayerScale), FontStyle.Bold);
        RadarClockDial.Draw(
            g,
            rect,
            state,
            new RadarClockDialDrawContext
            {
                LayerScale = this.LayerScale,
                DayFont = dayFont,
                TimeFont = timeFont,
                ModeFont = modeFont,
                BadgeFont = badgeFont,
                DrawFittedText = delegate(
                    Graphics target,
                    string text,
                    Font font,
                    Brush brush,
                    RectangleF textRect,
                    StringAlignment alignment,
                    float minSizeUnits)
                {
                    DrawCodexRadarFittedText(target, text, font, brush, textRect, alignment, minSizeUnits);
                }
            });
    }

    private bool DrawEvenRowSpeedWindowCountdownDial(
        Graphics g,
        RectangleF rect,
        CodexRadarSnapshot snapshot,
        DateTime nowLocal)
    {
        int remainingMinutes;
        float remainingRatio;
        if (!TryGetCodexRadarSpeedWindowCountdown(
            snapshot,
            nowLocal,
            out remainingMinutes,
            out remainingRatio))
        {
            return false;
        }

        float dialDiameter = Math.Max(S(20), Math.Min(rect.Width, rect.Height) - S(1));
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
        Color countdownColor = DesignTokens.WithAlpha(DesignTokens.Colors.SpeedWindowCountdown, 245);
        using (Pen trackPen = new Pen(DesignTokens.White(46), stroke))
        using (Pen countdownPen = new Pen(countdownColor, stroke))
        {
            countdownPen.StartCap = LineCap.Round;
            countdownPen.EndCap = LineCap.Round;
            g.DrawArc(trackPen, arcRect, -90.0f, 360.0f);
            float sweep = Math.Max(0.0f, Math.Min(360.0f, remainingRatio * 360.0f));
            if (sweep > 0.5f)
            {
                g.DrawArc(countdownPen, arcRect, -90.0f, sweep);
            }
        }

        RadarClockDial.DrawBoundaryTick(
            g,
            arcRect,
            Math.Max(1.0f, stroke * 0.74f),
            countdownColor);

        string dataLabelText = GetCodexModelIqDataLabelDisplayText(snapshot);
        string heroMain;
        string heroSuffix;
        RadarClockDial.SplitDataLabel(dataLabelText, out heroMain, out heroSuffix);
        float innerWidth = dial.Width * 0.72f;
        Font dayFont = this.fontCache.GetUi(Math.Max(9.0f, 11.5f * this.LayerScale), FontStyle.Bold);
        Font timeFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.LayerScale), FontStyle.Bold);
        Font modeFont = this.fontCache.GetUi(Math.Max(5.0f, 5.4f * this.LayerScale), FontStyle.Bold);
        float centerX = dial.Left + dial.Width / 2.0f;
        float centerY = dial.Top + dial.Height / 2.0f;
        RectangleF dayRect = new RectangleF(centerX - innerWidth / 2.0f, centerY - S(11), innerWidth, S(11));
        RectangleF timeRect = new RectangleF(centerX - innerWidth / 2.0f, centerY + S(1), innerWidth, S(8));
        RectangleF modeRect = new RectangleF(centerX - innerWidth / 2.0f, centerY + S(9), innerWidth, S(6));
        using (SolidBrush dayBrush = new SolidBrush(DesignTokens.White(235)))
        using (SolidBrush countdownBrush = new SolidBrush(countdownColor))
        {
            DrawCodexRadarFittedText(g, RadarClockDial.FormatDate(heroMain), dayFont, dayBrush, dayRect, StringAlignment.Center, 6.0f);
            DrawCodexRadarFittedText(g, FormatSpeedWindowCountdownTime(remainingMinutes), timeFont, countdownBrush, timeRect, StringAlignment.Center, 6.0f);
            DrawCodexRadarFittedText(g, "RST", modeFont, countdownBrush, modeRect, StringAlignment.Center, 4.5f);
        }

        return true;
    }

    private static bool TryGetCodexRadarSpeedWindowCountdown(
        CodexRadarSnapshot snapshot,
        DateTime nowLocal,
        out int remainingMinutes,
        out float remainingRatio)
    {
        const int maximumMinutes = 100 * 60;
        remainingMinutes = 0;
        remainingRatio = 0.0f;
        if (!IsCodexRadarSpeedWindowCurrentlyOpen(snapshot, nowLocal) ||
            snapshot == null ||
            !snapshot.SpeedWindowClosedAtKnown ||
            snapshot.SpeedWindowClosedAtLocal == DateTime.MinValue)
        {
            return false;
        }

        DateTime closedAt = snapshot.SpeedWindowClosedAtLocal;
        double remainingRawMinutes = (closedAt - nowLocal).TotalMinutes;
        if (remainingRawMinutes <= 0.0)
        {
            return false;
        }

        DateTime effectiveStart = closedAt.AddMinutes(-maximumMinutes);
        if (snapshot.SpeedWindowOpenedAtKnown &&
            snapshot.SpeedWindowOpenedAtLocal != DateTime.MinValue &&
            snapshot.SpeedWindowOpenedAtLocal < closedAt &&
            snapshot.SpeedWindowOpenedAtLocal > effectiveStart)
        {
            effectiveStart = snapshot.SpeedWindowOpenedAtLocal;
        }

        double totalMinutes = Math.Max(1.0, (closedAt - effectiveStart).TotalMinutes);
        remainingRatio = (float)Math.Max(0.0, Math.Min(1.0, remainingRawMinutes / totalMinutes));
        remainingMinutes = Math.Max(1, Math.Min(maximumMinutes, (int)Math.Ceiling(remainingRawMinutes)));
        return true;
    }

    private static string FormatSpeedWindowCountdownTime(int totalMinutes)
    {
        int clamped = Math.Max(0, Math.Min(100 * 60, totalMinutes));
        int hours = clamped / 60;
        int minutes = clamped % 60;
        return hours.ToString(hours >= 100 ? "000" : "00", CultureInfo.InvariantCulture) +
            ":" + minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private bool TryGetEvenRowLastAttemptRefreshLocal(CodexRadarSnapshot snapshot, out DateTime localTime)
    {
        if (this.lastCodexRadarStatusAttemptLocal != DateTime.MinValue)
        {
            localTime = this.lastCodexRadarStatusAttemptLocal;
            return true;
        }

        // No fallback to snapshot.CheckedAtLocal: that is the site quota-radar monitored_at, not a
        // local request attempt. Reporting it as LAST REF showed a site time after a cold restart.
        localTime = DateTime.MinValue;
        return false;
    }

    private double GetEvenRowDialCycleHours()
    {
        return GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude ? 24.0 : 12.0;
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
                RadarBottomInfoTextRenderer.DrawInkCenteredText(g, texts[i], itemFont, brush, itemRect);
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
        string key = this.CurrentSettings == null
            ? CodexRadarModelCatalog.DefaultModelKey
            : this.CurrentSettings.CodexRadarModelKey;
        string label = CodexRadarModelCatalog.GetDisplayLabel(string.Empty, key);
        string shortLabel = FormatCodexRadarCurrentModelShortLabel(key, label);
        return "LLM:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private static string FormatCodexRadarCurrentModelShortLabel(string key, string label)
    {
        string normalizedKey = CodexRadarModelCatalog.NormalizeModelKey(key);
        Match keyed = Regex.Match(
            normalizedKey,
            "^gpt_([0-9])([0-9]+)(?:_([a-z0-9]+))?_(xhigh|ultra|high|medium|low)$",
            RegexOptions.IgnoreCase);
        if (keyed.Success)
        {
            string family = keyed.Groups[3].Value;
            string effort = keyed.Groups[4].Value;
            string familyShort = family.Length == 0
                ? string.Empty
                : family.Substring(0, 1).ToUpperInvariant();
            string effortShort = effort == "xhigh" ? "XH" :
                effort == "ultra" ? "U" :
                effort == "high" ? "H" :
                effort == "medium" ? "M" : "L";
            return keyed.Groups[1].Value + "." + keyed.Groups[2].Value + familyShort + effortShort;
        }

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
        string key = this.CurrentSettings == null
            ? string.Empty
            : (this.CurrentSettings.ClaudeRadarModelKey ?? string.Empty);
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

}
