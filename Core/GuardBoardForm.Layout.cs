using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

// Scheme D rendering for the GUARD board, drawn in the same visual family as SpecBoardForm, the
// Codex task board and NetworkMonitorForm.DockedLayout: AppBackground wash, DesignTokens.Border
// hairlines, UiFontCache pixel fonts on the S(13)/S(10)/S(9.5)/S(8.4) readability ladder.
//
// Every row height here is measured from the actual font via MeasureLineHeight — never a hand-typed
// pixel count. The root AGENTS.md rule about this exists because guessed heights silently overlap on
// the user's machine, where the rendered fonts are routinely taller than assumed.
internal sealed partial class GuardBoardForm
{
    // Below this logical width the ring column and the card column cannot both hold readable text,
    // so the ring is dropped and the cards take the full width. Mirrors the compact-mode thresholds
    // in SpecBoardForm and NetworkMonitorForm.DockedLayout.
    private const int CompactRingMinimumLogicalWidth = 460;

    private bool IsCompactLayout
    {
        get { return this.CurrentSettings != null && this.CurrentSettings.SpecBoardWidth < CompactRingMinimumLogicalWidth; }
    }

    private static int MeasureLineHeight(Graphics g, Font font, int padding)
    {
        return Math.Max(1, (int)Math.Ceiling(g.MeasureString("Ag国", font, int.MaxValue, StringFormat.GenericTypographic).Height) + padding);
    }

    private static StringFormat CreateFormat(StringAlignment alignment, StringTrimming trimming)
    {
        StringFormat format = new StringFormat(StringFormat.GenericTypographic);
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = trimming;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        return format;
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawBoard(g, true);
    }

    private void DrawBoard(Graphics g, bool recordHitTargets)
    {
        if (recordHitTargets)
        {
            this.hitTargets.Clear();
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 238)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0.5f, 0.5f, this.Width - 1, this.Height - 1), Math.Max(3, S(10))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 96), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawPath(border, shell);
        }

        Font headerFont = this.fontCache.GetUi(S(13.0f), FontStyle.Bold);
        Font monoFont = this.fontCache.GetMono(S(10.0f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(9.5f), FontStyle.Regular);
        Font bodyBold = this.fontCache.GetUi(S(10.0f), FontStyle.Bold);
        Font smallFont = this.fontCache.GetUi(S(8.4f), FontStyle.Regular);
        Font smallBold = this.fontCache.GetUi(S(8.4f), FontStyle.Bold);
        Font ringBigFont = this.fontCache.GetMono(S(20.0f), FontStyle.Bold);

        int pad = S(10);
        int headerHeight = MeasureLineHeight(g, headerFont, S(6));
        int footerHeight = MeasureLineHeight(g, smallFont, S(5));

        Rectangle content = new Rectangle(pad, pad, Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - pad * 2));
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, headerHeight);
        Rectangle footer = new Rectangle(content.Left, Math.Max(header.Bottom, content.Bottom - footerHeight), content.Width, footerHeight);
        int bodyTop = header.Bottom + S(5);
        Rectangle body = new Rectangle(content.Left, bodyTop, content.Width, Math.Max(1, footer.Top - S(3) - bodyTop));

        DateTime nowUtc = DateTime.UtcNow;
        DrawHeader(g, header, headerFont, monoFont, smallFont);

        if (this.IsCompactLayout)
        {
            DrawControlColumn(g, body, bodyBold, bodyFont, smallFont, smallBold, monoFont, nowUtc, recordHitTargets);
        }
        else
        {
            // The ring column gives up four points of width to the control column: the rows there now
            // carry a name and its live state on one line, which needs horizontal room, while the ring
            // only loses a few pixels of diameter.
            int gaugeWidth = Math.Max(S(150), (int)Math.Round(body.Width * 0.36));
            Rectangle gauge = new Rectangle(body.Left, body.Top, gaugeWidth, body.Height);
            Rectangle controls = new Rectangle(gauge.Right + S(7), body.Top, Math.Max(1, body.Right - gauge.Right - S(7)), body.Height);
            using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 112), Math.Max(1.0f, this.LayerScale)))
            {
                g.DrawLine(divider, gauge.Right + S(3), gauge.Top, gauge.Right + S(3), gauge.Bottom);
            }

            DrawGaugeColumn(g, gauge, ringBigFont, bodyBold, bodyFont, smallFont, smallBold, monoFont, nowUtc, recordHitTargets);
            DrawControlColumn(g, controls, bodyBold, bodyFont, smallFont, smallBold, monoFont, nowUtc, recordHitTargets);
        }

        DrawFooter(g, footer, smallFont, recordHitTargets);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.Guard, this.LayerScale);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font headerFont, Font monoFont, Font smallFont)
    {
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.None))
        {
            string title = "GUARD";
            float titleWidth = g.MeasureString(title, headerFont).Width + S(8);
            g.DrawString(title, headerFont, text, new RectangleF(bounds.Left, bounds.Top, titleWidth, bounds.Height), near);

            string time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            float timeWidth = g.MeasureString(time, monoFont).Width + 4;
            RectangleF timeRect = new RectangleF(bounds.Right - timeWidth, bounds.Top, timeWidth, bounds.Height);
            g.DrawString(time, monoFont, text, timeRect, far);

            // Count dots mirror the Spec board header: armed guards in green, the battery-care pause
            // in yellow (protection is off while it runs), everything still idle in muted grey.
            int armed = (this.runtime.SleepGuardEnabled ? 1 : 0) + (this.runtime.DisplayGuardActive ? 1 : 0);
            int pending = this.runtime.BatteryCarePauseActive ? 1 : 0;
            int idle = Math.Max(0, 3 - armed - pending);
            float x = timeRect.Left - S(8);
            using (SolidBrush green = new SolidBrush(DesignTokens.Colors.Success))
            using (SolidBrush yellow = new SolidBrush(DesignTokens.Colors.Warning))
            using (SolidBrush grey = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            {
                x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + idle.ToString(CultureInfo.InvariantCulture), monoFont, grey);
                x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + pending.ToString(CultureInfo.InvariantCulture), monoFont, yellow);
                x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + armed.ToString(CultureInfo.InvariantCulture), monoFont, green);
            }

            RectangleF summaryRect = new RectangleF(
                bounds.Left + titleWidth,
                bounds.Top,
                Math.Max(0.0f, x - S(6) - (bounds.Left + titleWidth)),
                bounds.Height);
            if (summaryRect.Width > S(30))
            {
                g.DrawString(BuildHeaderSummary(), smallFont, muted, summaryRect, near);
            }
        }
    }

    private string BuildHeaderSummary()
    {
        if (this.runtime.DisplayGuardActive)
        {
            return this.runtime.SleepGuardEnabled
                ? "亮屏保持中 · 系统不休眠"
                : "亮屏保持中 · 系统仍可休眠";
        }

        if (this.runtime.SleepGuardEnabled)
        {
            return "系统不休眠 · 屏幕按超时熄灭";
        }

        return "未守护 · 遵循 Windows 电源设置";
    }

    private static float DrawHeaderCount(Graphics g, float right, float top, float height, string text, Font font, Brush brush)
    {
        float width = g.MeasureString(text, font).Width + 4;
        RectangleF rect = new RectangleF(right - width, top, width, height);
        using (StringFormat format = CreateFormat(StringAlignment.Far, StringTrimming.None))
        {
            g.DrawString(text, font, brush, rect, format);
        }

        return rect.Left - 4;
    }

    // Left column: the countdown ring on top, then the two horizontal tracks. Both tracks answer the
    // same shape of question ("how far into a fixed window am I"), so they share one row renderer.
    private void DrawGaugeColumn(
        Graphics g,
        Rectangle bounds,
        Font ringBigFont,
        Font bodyBold,
        Font bodyFont,
        Font smallFont,
        Font smallBold,
        Font monoFont,
        DateTime nowUtc,
        bool recordHitTargets)
    {
        int labelRow = MeasureLineHeight(g, smallFont, S(2));
        int actionRow = MeasureLineHeight(g, smallBold, S(5));
        int barHeight = Math.Max(S(6), S(7));
        int trackHeight = Math.Max(labelRow, actionRow) + barHeight + labelRow + S(4);
        int trackGap = S(8);

        // Power-request status block, added under the battery track. It carries the Modern Standby
        // fix's visible state (which requests are held) plus the AC caveat, so it claims its measured
        // height the same way the two tracks claim theirs. A tighter gap sits between the battery
        // track and this block than between the ring and the tracks, grouping the two power-related
        // rows without stealing extra ring height.
        int infoChipRow = MeasureLineHeight(g, smallBold, S(4));
        int infoCaptionRow = MeasureLineHeight(g, smallFont, S(2));
        int infoHeight = infoChipRow + infoCaptionRow + S(3);
        int infoGap = S(6);

        // The ring takes whatever is left after both tracks and the info block have their measured
        // height, so adding rows shrinks the ring instead of pushing content off the bottom edge.
        int ringBudget = Math.Max(S(56), bounds.Height - trackHeight * 2 - trackGap * 2 - infoGap - infoHeight);
        int ringSize = Math.Min(Math.Min(bounds.Width, ringBudget), S(168));
        Rectangle ring = new Rectangle(
            bounds.Left + Math.Max(0, (bounds.Width - ringSize) / 2),
            bounds.Top,
            ringSize,
            ringSize);
        DrawCountdownRing(g, ring, ringBigFont, smallFont, monoFont, nowUtc);

        int y = ring.Bottom + trackGap;
        Rectangle offlineTrack = new Rectangle(bounds.Left, y, bounds.Width, trackHeight);
        DrawOfflineTrack(g, offlineTrack, labelRow, barHeight, smallFont, smallBold, monoFont, nowUtc);

        y = offlineTrack.Bottom + trackGap;
        Rectangle batteryTrack = new Rectangle(bounds.Left, y, bounds.Width, trackHeight);
        DrawBatteryTrack(g, batteryTrack, labelRow, actionRow, barHeight, smallFont, smallBold, monoFont, nowUtc, recordHitTargets);

        y = batteryTrack.Bottom + infoGap;
        Rectangle powerInfo = new Rectangle(bounds.Left, y, bounds.Width, infoHeight);
        DrawPowerRequestInfo(g, powerInfo, infoChipRow, infoCaptionRow, smallFont, smallBold);
    }

    private enum GuardAcState
    {
        Unknown = 0,
        OnAc = 1,
        OnBattery = 2
    }

    // AC status, shared by the power-request info block and the repaint signature. Queried at most
    // once per repaint, and the board only repaints when the signature changes, so this stays cheap.
    private static GuardAcState ResolveAcState()
    {
        bool onAc;
        if (!NativeMethods.TryGetOnAcPower(out onAc))
        {
            return GuardAcState.Unknown;
        }

        return onAc ? GuardAcState.OnAc : GuardAcState.OnBattery;
    }

    // Modern Standby power-request status, drawn under the battery track. The three chips report
    // which requests the guard currently holds — this is the mechanism that actually keeps an S0
    // machine awake, so the board shows it rather than leaving the fix invisible. The caption carries
    // the AC caveat: on battery Windows can still drop the requests after the sleep timeout, so a
    // guard armed while unplugged is warned in the warning colour.
    private void DrawPowerRequestInfo(
        Graphics g,
        Rectangle bounds,
        int chipRow,
        int captionRow,
        Font smallFont,
        Font smallBold)
    {
        bool armed = this.runtime.SleepGuardEnabled || this.runtime.DisplayGuardActive;

        using (SolidBrush label = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            float labelWidth = g.MeasureString("电源请求", smallBold).Width + S(4);
            g.DrawString("电源请求", smallBold, label, new RectangleF(bounds.Left, bounds.Top, labelWidth, chipRow), near);

            int right = bounds.Right;
            right = DrawStateChip(g, right, bounds.Top, chipRow, "显示", this.runtime.DisplayPowerRequestActive, smallBold);
            right = DrawStateChip(g, right, bounds.Top, chipRow, "执行", this.runtime.ExecutionPowerRequestActive, smallBold);
            DrawStateChip(g, right, bounds.Top, chipRow, "系统", this.runtime.SystemPowerRequestActive, smallBold);
        }

        GuardAcState ac = ResolveAcState();
        string caption;
        Color captionColor;
        if (!armed)
        {
            caption = "未守护 · 未持有电源请求";
            captionColor = DesignTokens.Colors.GlyphMuted;
        }
        else if (!this.runtime.SleepGuardEnabled)
        {
            caption = "仅保持亮屏 · 系统仍按 Windows 电源设置休眠";
            captionColor = DesignTokens.Colors.Warning;
        }
        else if (ac == GuardAcState.OnBattery)
        {
            caption = "电池供电 · 待机超时后系统可能中断守护";
            captionColor = DesignTokens.Colors.Warning;
        }
        else if (ac == GuardAcState.OnAc)
        {
            caption = "S0 待机感知 · 已接通电源，守护持续有效";
            captionColor = DesignTokens.Colors.Success;
        }
        else
        {
            caption = "S0 待机感知 · 建议接通电源以免待机中断";
            captionColor = DesignTokens.Colors.GlyphMuted;
        }

        using (SolidBrush captionBrush = new SolidBrush(captionColor))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            Rectangle captionBounds = new Rectangle(bounds.Left, bounds.Top + chipRow + S(1), bounds.Width, captionRow);
            g.DrawString(caption, smallFont, captionBrush, captionBounds, near);
        }
    }

    // Small right-aligned status pill used by the power-request row. Active chips read in the success
    // colour, inactive ones stay muted, so the held requests are legible at a glance. Returns the new
    // right edge so chips can be laid out right-to-left, mirroring the card-control convention.
    private int DrawStateChip(Graphics g, int right, int top, int height, string label, bool active, Font font)
    {
        Color accent = active ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted;
        int width = Math.Max(S(30), (int)Math.Ceiling(g.MeasureString(label, font).Width) + S(12));
        Rectangle bounds = new Rectangle(right - width, top, width, height);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(5)))
        using (SolidBrush fill = new SolidBrush(active
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Success, 46)
            : DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 210)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, active ? 200 : 140), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush text = new SolidBrush(accent))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(label, font, text, bounds, centered);
        }

        return bounds.Left - S(4);
    }

    // The outer arc always carries whichever guard is currently the story: the display countdown
    // when a timer runs, otherwise the sleep guard's elapsed time. An earlier version pinned the
    // outer arc to the display timer alone, which left the biggest element on the board sitting
    // empty in the most common state (sleep guard on, no display timer) — it read as broken rather
    // than idle. The inner arc only appears when both guards run and it has something to add.
    private void DrawCountdownRing(Graphics g, Rectangle bounds, Font bigFont, Font smallFont, Font monoFont, DateTime nowUtc)
    {
        float outerWidth = Math.Max(2.0f, S(6));
        float innerWidth = Math.Max(1.5f, S(3));
        float outerInset = outerWidth / 2.0f + 1.0f;
        RectangleF outer = new RectangleF(
            bounds.Left + outerInset,
            bounds.Top + outerInset,
            Math.Max(1.0f, bounds.Width - outerInset * 2.0f),
            Math.Max(1.0f, bounds.Height - outerInset * 2.0f));
        float innerInset = outerInset + outerWidth + S(4);
        RectangleF inner = new RectangleF(
            bounds.Left + innerInset,
            bounds.Top + innerInset,
            Math.Max(1.0f, bounds.Width - innerInset * 2.0f),
            Math.Max(1.0f, bounds.Height - innerInset * 2.0f));

        bool displayRunning = this.runtime.DisplayGuardActive;
        bool sleepRunning = this.runtime.SleepGuardEnabled;
        float displayProgress = this.runtime.GetDisplayGuardProgress(nowUtc);
        float sleepProgress = this.runtime.GetSleepGuardProgress(nowUtc);
        float outerProgress = displayRunning ? displayProgress : (sleepRunning ? sleepProgress : 0.0f);
        Color outerColor = displayRunning ? DesignTokens.Colors.Warning : DesignTokens.Colors.Success;
        bool showInner = displayRunning && sleepRunning;

        using (Pen track = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 140), outerWidth))
        {
            track.StartCap = LineCap.Round;
            track.EndCap = LineCap.Round;
            g.DrawArc(track, outer, 0, 360);
        }

        if (showInner)
        {
            using (Pen innerTrack = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 100), innerWidth))
            {
                g.DrawArc(innerTrack, inner, 0, 360);
            }
        }

        if (outerProgress > 0.0f)
        {
            using (Pen arc = new Pen(outerColor, outerWidth))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, outer, -90, Math.Max(1.0f, 360.0f * outerProgress));
            }
        }

        if (showInner && sleepProgress > 0.0f)
        {
            using (Pen arc = new Pen(DesignTokens.Colors.Success, innerWidth))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, inner, -90, Math.Max(1.0f, 360.0f * sleepProgress));
            }
        }

        string caption = displayRunning ? "亮屏计时剩余" : (sleepRunning ? "睡眠防护已持续" : "未启用守护");
        string big = displayRunning
            ? GuardRuntime.FormatCountdown(this.runtime.GetDisplayGuardRemaining(nowUtc))
            : (sleepRunning ? GuardRuntime.FormatCountdown(this.runtime.GetSleepGuardElapsed(nowUtc)) : "--:--:--");
        string sub = BuildRingSubCaption(displayRunning, nowUtc);
        Color bigColor = displayRunning
            ? DesignTokens.Colors.Warning
            : (sleepRunning ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted);

        int captionHeight = MeasureLineHeight(g, smallFont, S(1));
        int bigHeight = MeasureLineHeight(g, bigFont, S(1));
        int totalHeight = captionHeight + bigHeight + captionHeight;
        int top = bounds.Top + Math.Max(0, (bounds.Height - totalHeight) / 2);
        float textWidth = Math.Max(1.0f, inner.Width - S(6));
        float textLeft = inner.Left + S(3);

        using (SolidBrush captionBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush bigBrush = new SolidBrush(bigColor))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(caption, smallFont, captionBrush, new RectangleF(textLeft, top, textWidth, captionHeight), centered);
            g.DrawString(big, bigFont, bigBrush, new RectangleF(textLeft, top + captionHeight, textWidth, bigHeight), centered);
            g.DrawString(sub, smallFont, captionBrush, new RectangleF(textLeft, top + captionHeight + bigHeight, textWidth, captionHeight), centered);
        }
    }

    private string BuildRingSubCaption(bool displayRunning, DateTime nowUtc)
    {
        if (displayRunning)
        {
            DateTime localEnd = this.runtime.DisplayGuardUntilUtc.ToLocalTime();
            return FormatMinutesLabel(this.runtime.DisplayGuardMinutes) +
                " · " +
                localEnd.ToString("HH:mm", CultureInfo.InvariantCulture) +
                " 解除";
        }

        if (this.runtime.SleepGuardEnabled)
        {
            return "屏幕仍按 Windows 超时熄灭";
        }

        return "系统按 Windows 电源设置休眠";
    }

    private static string FormatMinutesLabel(int minutes)
    {
        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " 分";
        }

        int hours = minutes / 60;
        int rest = minutes % 60;
        string label = hours.ToString(CultureInfo.InvariantCulture) + " 小时";
        return rest == 0 ? label : label + rest.ToString(CultureInfo.InvariantCulture) + " 分";
    }

    // Offline track. The marker sits pinned at 0 while online and walks right as the outage runs
    // toward the auto-sleep deadline, so the question it answers is "how close am I to sleeping",
    // not merely "am I disconnected".
    private void DrawOfflineTrack(
        Graphics g,
        Rectangle bounds,
        int labelRow,
        int barHeight,
        Font smallFont,
        Font smallBold,
        Font monoFont,
        DateTime nowUtc)
    {
        bool online = this.runtime.Online;
        float progress = this.runtime.GetOfflineProgress(nowUtc);
        Color accent = online
            ? DesignTokens.Colors.Success
            : (progress > 0.6f ? DesignTokens.Colors.Danger : DesignTokens.Colors.Warning);
        string stateText = online
            ? "在线 · 0:00"
            : "离线 " + GuardRuntime.FormatCountdown(this.runtime.GetOfflineElapsed(nowUtc));

        DrawTrackRow(
            g,
            bounds,
            labelRow,
            barHeight,
            "断网自动睡眠",
            stateText,
            "0",
            "阈值 " + FormatMinutesLabel(this.runtime.OfflineThresholdMinutes),
            progress,
            accent,
            smallFont,
            smallBold,
            monoFont,
            Rectangle.Empty);
    }

    // Battery track, directly under the offline track. While MyASUS battery care is paused the 80%
    // ceiling is lifted, so the bar fills toward the automatic restore 24h later; the inline button
    // pauses or restores immediately.
    private void DrawBatteryTrack(
        Graphics g,
        Rectangle bounds,
        int labelRow,
        int actionRow,
        int barHeight,
        Font smallFont,
        Font smallBold,
        Font monoFont,
        DateTime nowUtc,
        bool recordHitTargets)
    {
        bool paused = this.runtime.BatteryCarePauseActive;
        float progress = this.runtime.GetBatteryCarePauseProgress(nowUtc);
        Color accent = paused ? DesignTokens.Colors.Warning : DesignTokens.Colors.Success;
        string stateText = paused
            ? "暂停约 " + GuardRuntime.FormatCountdown(this.runtime.GetBatteryCarePauseRemaining(nowUtc))
            : "按 80% 上限";
        string rightScale = paused
            ? this.runtime.BatteryCarePauseUntilUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " 自动恢复"
            : "24 小时暂停窗口";

        // The action button shares the label row's right edge; the state text is measured against
        // whatever the button leaves behind so the two can never overlap.
        string actionLabel = paused ? "恢复" : "暂停";
        int actionWidth = Math.Max(S(38), (int)Math.Ceiling(g.MeasureString(actionLabel, smallBold).Width) + S(14));
        int rowHeight = Math.Max(labelRow, actionRow);
        Rectangle action = new Rectangle(bounds.Right - actionWidth, bounds.Top + Math.Max(0, (rowHeight - actionRow) / 2), actionWidth, actionRow);

        DrawTrackRow(
            g,
            bounds,
            labelRow,
            barHeight,
            "电池保护",
            stateText,
            "0",
            rightScale,
            progress,
            accent,
            smallFont,
            smallBold,
            monoFont,
            action);

        DrawActionButton(g, action, actionLabel, paused ? DesignTokens.Colors.Success : DesignTokens.Colors.Warning, smallBold);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new GuardHitTarget { Bounds = action, Action = GuardHitAction.BatteryToggle });
        }
    }

    private void DrawTrackRow(
        Graphics g,
        Rectangle bounds,
        int labelRow,
        int barHeight,
        string label,
        string stateText,
        string leftScale,
        string rightScale,
        float progress,
        Color accent,
        Font smallFont,
        Font smallBold,
        Font monoFont,
        Rectangle reservedAction)
    {
        int rowHeight = reservedAction.IsEmpty ? labelRow : Math.Max(labelRow, reservedAction.Height);
        int stateRight = reservedAction.IsEmpty ? bounds.Right : reservedAction.Left - S(6);

        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush accentBrush = new SolidBrush(accent))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            float labelWidth = Math.Min(
                g.MeasureString(label, smallBold).Width + S(4),
                Math.Max(1.0f, stateRight - bounds.Left));
            g.DrawString(label, smallBold, text, new RectangleF(bounds.Left, bounds.Top, labelWidth, rowHeight), near);

            float stateLeft = bounds.Left + labelWidth;
            float stateWidth = Math.Max(0.0f, stateRight - stateLeft);
            if (stateWidth > S(10))
            {
                g.DrawString(stateText, smallFont, accentBrush, new RectangleF(stateLeft, bounds.Top, stateWidth, rowHeight), far);
            }

            int barTop = bounds.Top + rowHeight + S(2);
            Rectangle bar = new Rectangle(bounds.Left, barTop, bounds.Width, barHeight);
            DrawProgressBar(g, bar, progress, accent);

            int scaleTop = bar.Bottom + S(2);
            Rectangle scale = new Rectangle(bounds.Left, scaleTop, bounds.Width, labelRow);
            g.DrawString(leftScale, smallFont, muted, scale, near);
            g.DrawString(rightScale, smallFont, muted, scale, far);
        }
    }

    private void DrawProgressBar(Graphics g, Rectangle bounds, float progress, Color accent)
    {
        float radius = Math.Max(1.0f, bounds.Height / 2.0f);
        using (GraphicsPath track = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, Math.Max(1, bounds.Width), bounds.Height), radius))
        using (SolidBrush trackFill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 235)))
        using (Pen trackBorder = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 150), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(trackFill, track);
            g.DrawPath(trackBorder, track);
        }

        float clamped = Math.Max(0.0f, Math.Min(1.0f, progress));
        if (clamped <= 0.0f)
        {
            // At zero the bar still needs a visible origin marker, otherwise "online, nothing
            // happening" and "bar failed to draw" look identical.
            using (SolidBrush dot = new SolidBrush(DesignTokens.WithAlpha(accent, 220)))
            {
                float diameter = bounds.Height;
                g.FillEllipse(dot, bounds.Left, bounds.Top, diameter, diameter);
            }

            return;
        }

        // Never narrower than the bar is tall: a rounded fill thinner than its own corner radius
        // renders as a lopsided sliver rather than a readable minimum.
        int fillWidth = Math.Max(bounds.Height, (int)Math.Round(bounds.Width * clamped));
        using (GraphicsPath fill = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, fillWidth, bounds.Height), radius))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(accent, 220)))
        {
            g.FillPath(brush, fill);
        }
    }

    // Right column: three labelled groups, each a rounded panel of single-line control rows.
    //
    // Rows, not cards. Every setting used to get its own boxed card — bold title line, muted subtitle
    // line, one control floated to the right — so a single switch cost two text lines plus card
    // padding. The column's fixed height was permanently oversubscribed: each control added had to be
    // paid for by shrinking its neighbours, and at narrow widths the last card still collided with the
    // footer. A row carries the same three things (name, live state, control) on one line and costs
    // one line height, which is where this column's headroom comes from.
    //
    // Exactly one variable drives the height budget: rowHeight. Group headers, panel padding and gaps
    // are fixed and measured, so fitting the column is one clamp instead of a cascade of per-group
    // shrink rules.
    private void DrawControlColumn(
        Graphics g,
        Rectangle bounds,
        Font bodyBold,
        Font bodyFont,
        Font smallFont,
        Font smallBold,
        Font monoFont,
        DateTime nowUtc,
        bool recordHitTargets)
    {
        // Battery care normally lives in the ring column beside its 24h bar. The compact layout has no
        // ring column, so it moves here as a fourth power row — dropping it would silently make a
        // feature unreachable at narrow widths instead of merely rearranging it.
        bool includeBattery = this.IsCompactLayout;

        int sectionRow = MeasureLineHeight(g, smallBold, S(4));
        int toggleHeight = S(17);
        int controlHeight = MeasureLineHeight(g, smallBold, S(5));
        int naturalRowHeight = Math.Max(
            Math.Max(MeasureLineHeight(g, bodyBold, S(7)), controlHeight + S(5)),
            toggleHeight + S(7));
        // Floor: still taller than the tallest control, so a squeezed row never puts a toggle on top
        // of a separator. Rows stop shrinking here; the fixed parts give way first (below).
        int minRowHeight = Math.Max(
            Math.Max(MeasureLineHeight(g, bodyBold, S(1)), controlHeight + S(2)),
            toggleHeight + S(3));

        int panelPad = S(4);
        int headerGap = S(2);
        int groupGap = S(6);

        int powerRowCount = 3;
        int modeRowCount = includeBattery ? 4 : 3;
        int programRowCount = 5;
        int rowCount = powerRowCount + modeRowCount + programRowCount;

        int fixedHeight = sectionRow * 3 + headerGap * 3 + panelPad * 6 + groupGap * 2;
        if ((bounds.Height - fixedHeight) / rowCount < minRowHeight)
        {
            // Short board: the padding gives way before the rows do. Padding only buys air, while a
            // row below its floor stops being clickable — so this order is deliberate.
            panelPad = S(1);
            headerGap = S(1);
            groupGap = S(3);
            fixedHeight = sectionRow * 3 + headerGap * 3 + panelPad * 6 + groupGap * 2;
        }

        int rowHeight = Math.Max(minRowHeight, Math.Min(naturalRowHeight, (bounds.Height - fixedHeight) / rowCount));
        // Leftover height goes between groups, not into the rows: a row inflated well past its own
        // line height reads as a gap with a control floating in it.
        int slack = Math.Max(0, bounds.Height - (fixedHeight + rowHeight * rowCount));
        groupGap += Math.Min(S(16), slack / 2);

        int y = bounds.Top;

        // --- 电源守护 ---
        y = DrawSectionHeader(g, bounds, y, sectionRow, "电源守护 · CodexSleepGuard", DesignTokens.Colors.Success, smallBold) + headerGap;
        Rectangle panel = new Rectangle(bounds.Left, y, bounds.Width, panelPad * 2 + rowHeight * powerRowCount);
        DrawGroupPanel(g, panel);
        int rowY = panel.Top + panelPad;
        int nameColumn = MeasureNameColumn(g, bodyBold, panel.Width, new string[] { "睡眠防护", "亮屏计时", "断网自动睡眠" });

        bool sleepOn = this.runtime.SleepGuardEnabled;
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "睡眠防护",
                NameColumn = nameColumn,
                State = sleepOn ? "系统不休眠 · 屏幕仍按超时熄灭" : "系统按 Windows 电源设置休眠",
                StateColor = sleepOn ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted,
                Control = GuardCardControl.Toggle,
                On = sleepOn,
                Primary = GuardHitAction.SleepToggle
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        DrawRowSeparator(g, panel, rowY);
        bool displayOn = this.runtime.DisplayGuardActive;
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "亮屏计时",
                NameColumn = nameColumn,
                State = displayOn
                    ? "剩余 " + GuardRuntime.FormatCountdown(this.runtime.GetDisplayGuardRemaining(nowUtc)) + " · 到点自动解除"
                    : "保持亮屏 · 不会自动开启防睡眠",
                StateColor = displayOn ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted,
                Control = GuardCardControl.StepperWithAction,
                On = displayOn,
                Value = FormatMinutesLabel(this.runtime.DisplayGuardMinutes),
                ActionLabel = displayOn ? "停止" : "开始",
                ActionAccent = displayOn ? DesignTokens.Colors.Warning : DesignTokens.Colors.Accent,
                Primary = GuardHitAction.DisplayToggle,
                Minus = GuardHitAction.DisplayMinus,
                Plus = GuardHitAction.DisplayPlus
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        DrawRowSeparator(g, panel, rowY);
        DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "断网自动睡眠",
                NameColumn = nameColumn,
                State = !sleepOn
                    ? "未武装 · 开启睡眠防护后才会触发"
                    : (this.runtime.Online
                        ? "在线 · 离线满阈值即解除守护并睡眠"
                        : "离线 " + GuardRuntime.FormatCountdown(this.runtime.GetOfflineElapsed(nowUtc)) + " · 达阈值即请求睡眠"),
                StateColor = sleepOn && !this.runtime.Online ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted,
                Control = GuardCardControl.Stepper,
                Value = FormatMinutesLabel(this.runtime.OfflineThresholdMinutes),
                Minus = GuardHitAction.OfflineMinus,
                Plus = GuardHitAction.OfflinePlus
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);
        y = panel.Bottom + groupGap;

        // --- 电源模式 ---
        // The live tier and any pending schedule ride in the section header. The header is a line this
        // group already pays for, so the quick-switch row below it can be pure control.
        GuardPowerModeTier liveTier = GuardRuntime.GetLivePowerModeTier();
        bool scheduleActive = this.runtime.PowerModeOverrideActive;
        string modeHeader = "电源模式 · 当前" + GuardRuntime.DescribeTier(liveTier) +
            (scheduleActive ? " · 定时中" : " · 点击即切换，不自动还原");
        y = DrawSectionHeader(g, bounds, y, sectionRow, modeHeader, DesignTokens.Colors.Accent, smallBold) + headerGap;
        panel = new Rectangle(bounds.Left, y, bounds.Width, panelPad * 2 + rowHeight * modeRowCount);
        DrawGroupPanel(g, panel);
        rowY = panel.Top + panelPad;
        nameColumn = MeasureNameColumn(g, bodyBold, panel.Width, new string[] { "定时锁定", "省电模式", "电池保护" });

        int segmentInset = S(9);
        int segmentHeight = Math.Max(controlHeight, rowHeight - S(4));
        DrawSegmentRow(
            g,
            new Rectangle(
                panel.Left + segmentInset,
                rowY + Math.Max(0, (rowHeight - segmentHeight) / 2),
                Math.Max(S(60), panel.Width - segmentInset * 2),
                segmentHeight),
            new string[] { "省电", "平衡", "性能" },
            new bool[]
            {
                liveTier == GuardPowerModeTier.Saver,
                liveTier == GuardPowerModeTier.Balanced,
                liveTier == GuardPowerModeTier.Performance
            },
            DesignTokens.Colors.Accent,
            new GuardHitAction[]
            {
                GuardHitAction.PowerModeSaver,
                GuardHitAction.PowerModeBalanced,
                GuardHitAction.PowerModePerformance
            },
            smallBold,
            recordHitTargets);
        rowY += rowHeight;

        DrawRowSeparator(g, panel, rowY);
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "定时锁定",
                NameColumn = nameColumn,
                State = scheduleActive
                    ? "剩余 " + GuardRuntime.FormatCountdown(this.runtime.GetPowerModeOverrideRemaining(nowUtc)) + " 后恢复至平衡"
                    : "到点固定恢复至平衡",
                StateColor = scheduleActive ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted,
                Control = GuardCardControl.StepperWithAction,
                On = scheduleActive,
                Value = FormatHoursLabel(this.runtime.PowerModeOverrideHours),
                ActionLabel = scheduleActive ? "取消定时" : "定时锁定",
                ActionAccent = scheduleActive ? DesignTokens.Colors.Warning : DesignTokens.Colors.Accent,
                Primary = GuardHitAction.PowerScheduleToggle,
                Minus = GuardHitAction.PowerScheduleHoursMinus,
                Plus = GuardHitAction.PowerScheduleHoursPlus
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        DrawRowSeparator(g, panel, rowY);
        // The toggle stays bound to GUARD's own intent — it is the control for that flag, and making
        // it mirror the live state would snap back the moment the user flips it on AC power. The state
        // text carries what the system is actually doing, which is the part that answers "did it take".
        bool energySaverForced = this.runtime.EnergySaverForcedOn;
        bool energySaverLive;
        bool energySaverLiveKnown = NativeMethods.TryGetBatterySaverStatus(out energySaverLive);
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "省电模式",
                NameColumn = nameColumn,
                State = DescribeEnergySaverDetail(energySaverLiveKnown, energySaverLive, energySaverForced),
                StateColor = energySaverLiveKnown && energySaverLive
                    ? DesignTokens.Colors.Success
                    : (energySaverForced ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted),
                Control = GuardCardControl.Toggle,
                On = energySaverForced,
                Primary = GuardHitAction.EnergySaverToggle
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        if (includeBattery)
        {
            DrawRowSeparator(g, panel, rowY);
            bool paused = this.runtime.BatteryCarePauseActive;
            DrawSettingRow(
                g,
                new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
                new GuardRowSpec
                {
                    Name = "电池保护",
                    NameColumn = nameColumn,
                    State = paused
                        ? "暂停约 " + GuardRuntime.FormatCountdown(this.runtime.GetBatteryCarePauseRemaining(nowUtc)) + " 后恢复"
                        : "按 80% 充电上限",
                    StateColor = paused ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted,
                    Control = GuardCardControl.Button,
                    On = paused,
                    ActionLabel = paused ? "恢复" : "暂停",
                    ActionAccent = paused ? DesignTokens.Colors.Warning : DesignTokens.Colors.Accent,
                    Primary = GuardHitAction.BatteryToggle
                },
                bodyBold,
                smallFont,
                smallBold,
                recordHitTargets);
        }

        y = panel.Bottom + groupGap;

        // --- 程序守护 ---
        y = DrawSectionHeader(g, bounds, y, sectionRow, "程序守护 · 特殊设置", DesignTokens.Colors.AccentAlt, smallBold) + headerGap;
        panel = new Rectangle(bounds.Left, y, bounds.Width, panelPad * 2 + rowHeight * programRowCount);
        DrawGroupPanel(g, panel);
        rowY = panel.Top + panelPad;
        nameColumn = MeasureNameColumn(g, bodyBold, panel.Width, new string[] { "保活", "链接阻断", "额度计划", "CTF 重启" });

        bool translatorAlive = this.CurrentSettings != null && this.CurrentSettings.TranslatorKeepAliveEnabled;
        bool codexAlive = this.CurrentSettings != null && this.CurrentSettings.CodexAppKeepAliveEnabled;
        bool claudeAlive = this.CurrentSettings != null && this.CurrentSettings.ClaudeAppKeepAliveEnabled;
        int armedCount = (translatorAlive ? 1 : 0) + (codexAlive ? 1 : 0) + (claudeAlive ? 1 : 0);
        bool allArmed = armedCount == 3;
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "保活",
                NameColumn = nameColumn,
                State = armedCount == 0
                    ? "未守护任何程序"
                    : "已守护 " + armedCount.ToString(CultureInfo.InvariantCulture) + "/3 · 掉线每 30 秒补拉",
                StateColor = armedCount == 0 ? DesignTokens.Colors.GlyphMuted : DesignTokens.Colors.Success,
                Control = GuardCardControl.Button,
                On = allArmed,
                // One control both applies and undoes the default, so the label flips once all three
                // are armed rather than leaving a button that does nothing.
                ActionLabel = allArmed ? "全关" : "全开",
                ActionAccent = allArmed ? DesignTokens.Colors.Warning : DesignTokens.Colors.Accent,
                Primary = GuardHitAction.KeepAliveDefaults
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        // The three guards are peers of one setting, so they read as one segmented row rather than
        // three label+toggle pairs strung across a line — with pairs, a toggle sitting midway between
        // two labels visually attaches to the wrong one.
        //
        // Each segment carries its own ●/○ state dot, which is what separates this row from the
        // power-mode row above it: identical pills without the dots would read as one exclusive
        // choice, and these three are independent switches.
        DrawSegmentRow(
            g,
            new Rectangle(
                panel.Left + segmentInset,
                rowY + Math.Max(0, (rowHeight - segmentHeight) / 2),
                Math.Max(S(60), panel.Width - segmentInset * 2),
                segmentHeight),
            new string[]
            {
                (translatorAlive ? "● " : "○ ") + "翻译",
                (codexAlive ? "● " : "○ ") + "Codex",
                (claudeAlive ? "● " : "○ ") + "Claude"
            },
            new bool[] { translatorAlive, codexAlive, claudeAlive },
            DesignTokens.Colors.Success,
            new GuardHitAction[]
            {
                GuardHitAction.TranslatorKeepAliveToggle,
                GuardHitAction.CodexAppKeepAliveToggle,
                GuardHitAction.ClaudeAppKeepAliveToggle
            },
            smallBold,
            recordHitTargets);
        rowY += rowHeight;

        DrawRowSeparator(g, panel, rowY);
        bool aiBlocked = this.CurrentSettings != null && this.CurrentSettings.AiRequestProtectionManualBlockEnabled;
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "链接阻断",
                NameColumn = nameColumn,
                State = aiBlocked ? "已阻断本程序的 OpenAI / Claude 请求" : "阻断本程序的 OpenAI / Claude 请求",
                StateColor = aiBlocked ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted,
                Control = GuardCardControl.Toggle,
                On = aiBlocked,
                Primary = GuardHitAction.AiBlockToggle
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        DrawRowSeparator(g, panel, rowY);
        bool quotaPlan = this.CurrentSettings != null && this.CurrentSettings.CodexQuotaPlanEnabled;
        rowY = DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "额度计划",
                NameColumn = nameColumn,
                State = quotaPlan ? "已启用 · 阈值与 goal 见普通设置" : "未启用 · 阈值与 goal 见普通设置",
                StateColor = quotaPlan ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted,
                Control = GuardCardControl.Toggle,
                On = quotaPlan,
                Primary = GuardHitAction.QuotaPlanToggle
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);

        DrawRowSeparator(g, panel, rowY);
        DrawSettingRow(
            g,
            new Rectangle(panel.Left, rowY, panel.Width, rowHeight),
            new GuardRowSpec
            {
                Name = "CTF 重启",
                NameColumn = nameColumn,
                State = "提权重启当前会话的 ctfmon.exe",
                Control = GuardCardControl.Button,
                ActionLabel = "重启",
                ActionAccent = DesignTokens.Colors.Accent,
                Primary = GuardHitAction.CtfRestart
            },
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets);
    }

    private int DrawSectionHeader(Graphics g, Rectangle bounds, int y, int height, string label, Color accent, Font font)
    {
        using (SolidBrush brush = new SolidBrush(accent))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            float dot = S(6);
            g.FillEllipse(brush, bounds.Left, y + (height - dot) / 2.0f, dot, dot);
            g.DrawString(
                label,
                font,
                brush,
                new RectangleF(bounds.Left + dot + S(5), y, Math.Max(1, bounds.Width - dot - S(5)), height),
                near);
        }

        return y + height;
    }

    private enum GuardCardControl
    {
        Toggle,
        Stepper,
        StepperWithAction,
        Button
    }

    // One row of a group panel. Everything a row needs is named here rather than passed as eleven
    // positional arguments, because the rows differ in which three or four of these they use.
    private struct GuardRowSpec
    {
        public string Name;
        public string State;
        public Color StateColor;
        // Shared per panel so every state text in a group starts at the same x. Zero means "measure
        // this row's own name", which leaves the states ragged.
        public int NameColumn;
        public GuardCardControl Control;
        public bool On;
        public string Value;
        public string ActionLabel;
        public Color ActionAccent;
        public GuardHitAction Primary;
        public GuardHitAction Minus;
        public GuardHitAction Plus;
    }

    // Name, live state, control — on one line. The control is laid out right-to-left from the row's
    // right edge and the text is measured against whatever remains; doing it in the other order is how
    // text ends up running underneath a toggle at small widths.
    private int DrawSettingRow(
        Graphics g,
        Rectangle bounds,
        GuardRowSpec spec,
        Font nameFont,
        Font smallFont,
        Font smallBold,
        bool recordHitTargets)
    {
        int padX = S(9);
        int controlRight = bounds.Right - padX;

        if (spec.Control == GuardCardControl.Toggle)
        {
            int toggleWidth = S(32);
            int toggleHeight = S(17);
            Rectangle toggle = new Rectangle(
                controlRight - toggleWidth,
                bounds.Top + Math.Max(0, (bounds.Height - toggleHeight) / 2),
                toggleWidth,
                toggleHeight);
            DrawToggle(g, toggle, spec.On);
            RecordHitTarget(recordHitTargets, toggle, spec.Primary);
            controlRight = toggle.Left - S(6);
        }
        else if (spec.Control == GuardCardControl.Button)
        {
            int buttonHeight = MeasureLineHeight(g, smallBold, S(5));
            int buttonWidth = Math.Max(S(44), (int)Math.Ceiling(g.MeasureString(spec.ActionLabel, smallBold).Width) + S(16));
            Rectangle button = new Rectangle(
                controlRight - buttonWidth,
                bounds.Top + Math.Max(0, (bounds.Height - buttonHeight) / 2),
                buttonWidth,
                buttonHeight);
            DrawActionButton(g, button, spec.ActionLabel, spec.ActionAccent, smallBold);
            RecordHitTarget(recordHitTargets, button, spec.Primary);
            controlRight = button.Left - S(6);
        }
        else
        {
            int controlHeight = MeasureLineHeight(g, smallBold, S(5));
            int controlTop = bounds.Top + Math.Max(0, (bounds.Height - controlHeight) / 2);

            if (spec.Control == GuardCardControl.StepperWithAction)
            {
                int actionWidth = Math.Max(S(40), (int)Math.Ceiling(g.MeasureString(spec.ActionLabel, smallBold).Width) + S(14));
                Rectangle action = new Rectangle(controlRight - actionWidth, controlTop, actionWidth, controlHeight);
                DrawActionButton(g, action, spec.ActionLabel, spec.ActionAccent, smallBold);
                RecordHitTarget(recordHitTargets, action, spec.Primary);
                controlRight = action.Left - S(5);
            }

            Rectangle stepper = DrawStepper(
                g,
                controlRight,
                controlTop,
                controlHeight,
                spec.Value,
                smallBold,
                smallFont,
                spec.Minus,
                spec.Plus,
                recordHitTargets);
            controlRight = stepper.Left - S(6);
        }

        int textLeft = bounds.Left + padX;
        int textWidth = Math.Max(S(24), controlRight - textLeft);
        using (SolidBrush nameBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            // Names share one column per panel so the state texts line up; without it every row starts
            // its state at a different x and the group reads as unrelated lines rather than a table.
            int nameWidth = spec.NameColumn > 0
                ? Math.Min(textWidth, spec.NameColumn)
                : Math.Min(textWidth, (int)Math.Ceiling(g.MeasureString(spec.Name, nameFont).Width) + S(3));
            g.DrawString(spec.Name, nameFont, nameBrush, new RectangleF(textLeft, bounds.Top, nameWidth, bounds.Height), near);

            int stateLeft = textLeft + nameWidth + S(6);
            int stateWidth = controlRight - stateLeft;
            if (!string.IsNullOrEmpty(spec.State) && stateWidth > S(16))
            {
                Color stateColor = spec.StateColor.IsEmpty ? DesignTokens.Colors.GlyphMuted : spec.StateColor;
                using (SolidBrush stateBrush = new SolidBrush(stateColor))
                {
                    g.DrawString(spec.State, smallFont, stateBrush, new RectangleF(stateLeft, bounds.Top, stateWidth, bounds.Height), near);
                }
            }
        }

        return bounds.Bottom;
    }

    // Widest name in a group, used as that panel's name column. Capped so a long name ellipsizes
    // instead of pushing every state text in the group up against the controls.
    private int MeasureNameColumn(Graphics g, Font font, int panelWidth, string[] names)
    {
        int widest = 0;
        for (int i = 0; i < names.Length; i++)
        {
            widest = Math.Max(widest, (int)Math.Ceiling(g.MeasureString(names[i], font).Width));
        }

        return Math.Min(widest + S(3), Math.Max(S(40), (int)Math.Round(panelWidth * 0.38)));
    }

    private void RecordHitTarget(bool recordHitTargets, Rectangle bounds, GuardHitAction action)
    {
        if (recordHitTargets && action != GuardHitAction.None)
        {
            this.hitTargets.Add(new GuardHitTarget { Bounds = bounds, Action = action });
        }
    }

    // One surface per group instead of one per setting: the boxed-card-per-control look made eight
    // unrelated slabs compete for attention, and the section headers floating between them carried no
    // visual grouping of their own.
    private void DrawGroupPanel(Graphics g, Rectangle bounds)
    {
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -0.5f, -0.5f), S(6)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 220)))
        {
            g.FillPath(fill, path);
        }
    }

    // Hairline between two rows of the same panel, inset so it reads as an internal divider rather
    // than a second panel edge.
    private void DrawRowSeparator(Graphics g, Rectangle panel, int y)
    {
        int inset = S(9);
        using (Pen pen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 70), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(pen, panel.Left + inset, y, panel.Right - inset, y);
        }
    }

    // Live system state first, GUARD's intent second. Rendered as the 省电模式 row's state text, so
    // this returns the state alone — the row already carries the name.
    //
    // SetEnergySaverForced() does not switch Energy Saver on; it writes the battery threshold to 100%
    // and lets Windows decide, so on AC power forced-on legitimately means "not in effect". Reporting
    // the flag as "已强制开启" claimed an effect that had not happened.
    internal static string DescribeEnergySaverDetail(bool liveKnown, bool liveOn, bool forcedByGuard)
    {
        if (!liveKnown)
        {
            return forcedByGuard ? "未知（强制）" : "未知";
        }

        if (liveOn)
        {
            return forcedByGuard ? "开启（强制）" : "开启";
        }

        return forcedByGuard ? "强制未生效" : "关闭";
    }

    // Segmented row of mutually-visible peers, used by the power-mode tiers (accent) and the three
    // keep-alive guards (success). Segments are separated by a real gap, so their hit targets cannot
    // touch — two adjacent targets sharing pixels means the first one registered silently wins.
    private void DrawSegmentRow(
        Graphics g,
        Rectangle bounds,
        string[] labels,
        bool[] active,
        Color accent,
        GuardHitAction[] actions,
        Font font,
        bool recordHitTargets)
    {
        int gap = S(4);
        int count = labels.Length;
        int segmentWidth = (bounds.Width - gap * (count - 1)) / count;
        int x = bounds.Left;
        for (int i = 0; i < count; i++)
        {
            // The last segment takes the remainder rather than the computed width, so integer
            // division never leaves a ragged sliver against the panel's right edge.
            int width = i == count - 1 ? Math.Max(1, bounds.Right - x) : segmentWidth;
            DrawSegment(g, new Rectangle(x, bounds.Top, width, bounds.Height), labels[i], active[i], accent, actions[i], font, recordHitTargets);
            x += width + gap;
        }
    }

    private void DrawSegment(Graphics g, Rectangle bounds, string label, bool active, Color accent, GuardHitAction action, Font font, bool recordHitTargets)
    {
        Color outline = active ? accent : DesignTokens.Colors.Border;
        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(5)))
        using (SolidBrush fill = new SolidBrush(active
            ? DesignTokens.WithAlpha(accent, 56)
            : DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 220)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(outline, active ? 205 : 150), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush text = new SolidBrush(active ? accent : DesignTokens.Colors.GlyphMuted))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(label, font, text, bounds, centered);
        }

        RecordHitTarget(recordHitTargets, bounds, action);
    }

    private static string FormatHoursLabel(int hours)
    {
        return hours.ToString(CultureInfo.InvariantCulture) + " 小时";
    }

    private void DrawToggle(Graphics g, Rectangle bounds, bool on)
    {
        float radius = bounds.Height / 2.0f;
        Color accent = on ? DesignTokens.Colors.Success : DesignTokens.Colors.Border;
        using (GraphicsPath track = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), radius))
        using (SolidBrush fill = new SolidBrush(on
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Success, 56)
            : DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 230)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, on ? 205 : 150), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, track);
            g.DrawPath(border, track);
        }

        float knob = Math.Max(3.0f, bounds.Height - S(6));
        float knobTop = bounds.Top + (bounds.Height - knob) / 2.0f;
        float knobLeft = on ? bounds.Right - knob - S(3) : bounds.Left + S(3);
        using (SolidBrush knobBrush = new SolidBrush(on ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted))
        {
            g.FillEllipse(knobBrush, knobLeft, knobTop, knob, knob);
        }
    }

    private void DrawActionButton(Graphics g, Rectangle bounds, string label, Color accent, Font font)
    {
        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 230)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 190), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush text = new SolidBrush(accent))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(label, font, text, bounds, centered);
        }
    }

    // Stepper laid out right-to-left from `right`, returning its own bounds so the caller can keep
    // walking leftwards. Ends of the ladder render muted, matching StepValue's refusal to wrap.
    private Rectangle DrawStepper(
        Graphics g,
        int right,
        int top,
        int height,
        string value,
        Font valueFont,
        Font glyphFont,
        GuardHitAction minus,
        GuardHitAction plus,
        bool recordHitTargets)
    {
        int stepWidth = Math.Max(S(16), height);
        int valueWidth = Math.Max(S(46), (int)Math.Ceiling(g.MeasureString(value, valueFont).Width) + S(10));
        int totalWidth = stepWidth * 2 + valueWidth;
        Rectangle bounds = new Rectangle(right - totalWidth, top, totalWidth, height);

        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 220)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 170), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        Rectangle minusBounds = new Rectangle(bounds.Left, bounds.Top, stepWidth, height);
        Rectangle valueBounds = new Rectangle(minusBounds.Right, bounds.Top, valueWidth, height);
        Rectangle plusBounds = new Rectangle(valueBounds.Right, bounds.Top, stepWidth, height);

        using (SolidBrush glyph = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        using (StringFormat centeredClip = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.DrawString("−", glyphFont, glyph, minusBounds, centered);
            g.DrawString(value, valueFont, text, valueBounds, centeredClip);
            g.DrawString("+", glyphFont, glyph, plusBounds, centered);
        }

        if (recordHitTargets)
        {
            if (minus != GuardHitAction.None)
            {
                this.hitTargets.Add(new GuardHitTarget { Bounds = minusBounds, Action = minus });
            }

            if (plus != GuardHitAction.None)
            {
                this.hitTargets.Add(new GuardHitTarget { Bounds = plusBounds, Action = plus });
            }
        }

        return bounds;
    }

    private void DrawFooter(Graphics g, Rectangle bounds, Font smallFont, bool recordHitTargets)
    {
        int panelWidth = Math.Min(bounds.Width, Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("设置", smallFont).Width) + S(14)));
        int closeWidth = Math.Min(bounds.Width, Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("关闭", smallFont).Width) + S(14)));
        Rectangle panelBounds = new Rectangle(bounds.Left, bounds.Top, panelWidth, bounds.Height);
        Rectangle closeBounds = new Rectangle(panelBounds.Right + S(4), bounds.Top, closeWidth, bounds.Height);

        DrawFooterPill(g, panelBounds, "设置", DesignTokens.Colors.Success, smallFont);
        DrawFooterPill(g, closeBounds, "关闭", DesignTokens.Colors.Danger, smallFont);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new GuardHitTarget { Bounds = panelBounds, Action = GuardHitAction.Panel });
            this.hitTargets.Add(new GuardHitTarget { Bounds = closeBounds, Action = GuardHitAction.Close });
        }

        int noticeLeft = closeBounds.Right + S(5);
        int noticeWidth = Math.Max(0, bounds.Right - noticeLeft);
        if (noticeWidth <= S(20))
        {
            return;
        }

        string notice = string.IsNullOrEmpty(this.statusNotice) ? this.runtime.LastActionDetail : this.statusNotice;
        if (string.IsNullOrEmpty(notice))
        {
            notice = "睡眠防护允许屏幕熄灭，对 OLED 更友好。";
        }

        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(notice, smallFont, muted, new Rectangle(noticeLeft, bounds.Top, noticeWidth, bounds.Height), far);
        }
    }

    // Layout self-test. The two failures this is really guarding against are a control the user can
    // see but not click (a missing hit target) and two controls sharing pixels (overlapping targets,
    // where the first one registered silently wins) — both invisible in a screenshot.
    internal static void RunSelfTest()
    {
        int[] ladder = WidgetSettings.GuardDisplayMinuteSteps;
        AssertSelfTest(StepValue(ladder, 60, -1) == 60, "display ladder clamps at the low end");
        AssertSelfTest(StepValue(ladder, 60, 1) == 120, "display ladder steps up");
        AssertSelfTest(ladder.Length == 24 && ladder[0] == 60 && ladder[23] == 1440,
            "display ladder covers every hour from 1 through 24");
        AssertSelfTest(StepValue(ladder, 480, 1) == 540, "display ladder steps up one hour");
        AssertSelfTest(StepValue(ladder, 480, -1) == 420, "display ladder steps down one hour");
        AssertSelfTest(StepValue(ladder, 1440, 1) == 1440, "display ladder clamps at 24 hours");
        AssertSelfTest(StepValue(ladder, 999, 1) == ladder[0], "off-ladder value snaps to one hour");

        int[] offline = WidgetSettings.GuardOfflineThresholdMinuteSteps;
        AssertSelfTest(StepValue(offline, 1, -1) == 1, "offline ladder clamps at the low end");
        AssertSelfTest(StepValue(offline, 30, 1) == 30, "offline ladder clamps at the high end");

        // Both layouts have to register every control; the compact one drops the ring column, and
        // dropping the battery button with it would silently strip a feature at narrow widths.
        VerifyHitTargets(648, 400);
        VerifyHitTargets(320, 400);
        VerifyControlContract();
        VerifyEnergySaverLabelContract();

        Console.WriteLine("Guard board layout: PASS hit targets, hourly ladder, independent CLI controls, live energy-saver label");
    }

    // The label must report what the system is doing, not what GUARD asked for. Forcing writes the
    // battery threshold to 100% and lets Windows decide, so on AC power forced-on legitimately means
    // "not in effect" - the state the old wording claimed was "已强制开启".
    private static void VerifyEnergySaverLabelContract()
    {
        AssertSelfTest(
            DescribeEnergySaverDetail(true, false, true) == "强制未生效",
            "forced but system-off reports that the force has not taken effect");
        AssertSelfTest(
            DescribeEnergySaverDetail(true, true, true) == "开启（强制）",
            "forced and system-on reports both the live state and the force");
        AssertSelfTest(
            DescribeEnergySaverDetail(true, true, false) == "开启",
            "system-on without GUARD forcing still reports on, so an external change is visible");
        AssertSelfTest(
            DescribeEnergySaverDetail(true, false, false) == "关闭",
            "system-off without forcing reports off");
        AssertSelfTest(
            DescribeEnergySaverDetail(false, false, false) == "未知" &&
            DescribeEnergySaverDetail(false, true, true) == "未知（强制）",
            "an unreadable status reports unknown instead of claiming off");
    }

    private static void VerifyControlContract()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        using (GuardBoardForm form = new GuardBoardForm(null, settings, delegate { return (bool?)true; }))
        {
            GuardControlResponse response = form.ExecuteGuardControl(
                new GuardControlRequest { Action = "display_start", Hours = 6 });
            AssertSelfTest(response.Ok && response.State.DisplayActive && response.State.DisplayHours == 6,
                "CLI starts display protection with an explicit preset");
            AssertSelfTest(!response.State.SleepEnabled,
                "CLI display start does not silently enable sleep protection");

            response = form.ExecuteGuardControl(new GuardControlRequest { Action = "sleep_on" });
            AssertSelfTest(response.Ok && response.State.SleepEnabled && response.State.DisplayActive,
                "CLI sleep control composes with an active display timer");
            response = form.ExecuteGuardControl(new GuardControlRequest { Action = "sleep_off" });
            AssertSelfTest(response.Ok && !response.State.SleepEnabled && response.State.DisplayActive,
                "CLI sleep off preserves the independent display timer");

            response = form.ExecuteGuardControl(
                new GuardControlRequest { Action = "display_hours", Hours = 7 });
            AssertSelfTest(response.Ok && response.State.DisplayHours == 7 && response.State.DisplayActive,
                "CLI preset update applies while display protection is active");
            response = form.ExecuteGuardControl(new GuardControlRequest { Action = "display_stop" });
            AssertSelfTest(response.Ok && !response.State.DisplayActive && !response.State.SleepEnabled,
                "CLI stops display protection without changing sleep state");

            // power_mode/energy_saver_* exercise real Windows APIs through the CLI dispatch layer
            // (distinct code from GuardRuntime's own self-test), so the machine's prior state is
            // captured and restored the same way — --test-layout must not leave a different power
            // mode or Energy Saver threshold behind.
            // The ACTUAL overlay read, never the effective one -- see GuardRuntime.RunSelfTest for
            // why restoring a captured effective saver would itself corrupt the user's selection.
            Guid originalOverlayGuid;
            bool hadOriginalOverlay = NativeMethods.TryGetActualPowerOverlayScheme(out originalOverlayGuid);
            int originalEnergySaverThreshold;
            bool hadOriginalEnergySaverThreshold = NativeMethods.TryReadEnergySaverBatteryThresholdPercent(out originalEnergySaverThreshold);
            try
            {
                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "power_mode", Mode = "balanced" });
                // PowerSetActiveOverlayScheme is a request, not a guarantee, and PowerModeCurrent is
                // read back through the EFFECTIVE overlay, which Windows is free to override
                // (battery policy, a vendor service). Asserting that the write round-trips therefore
                // tests the environment rather than the code: the same build passed this on AC and
                // failed it on battery, where an engaged Energy Saver pins the effective overlay to
                // saver no matter what is written. What the code actually owns is that the call
                // succeeded, so that is asserted unconditionally; the round-trip is asserted only
                // while nothing is forcing an overlay.
                AssertSelfTest(response.Ok, "CLI power mode switch reports success");
                bool energySaverEngaged;
                bool energySaverKnown = NativeMethods.TryGetBatterySaverStatus(out energySaverEngaged);
                AssertSelfTest(
                    (energySaverKnown && energySaverEngaged) || response.State.PowerModeCurrent == "balanced",
                    "CLI power mode switch reports the resulting tier when the OS is not forcing an overlay");

                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "power_schedule_start", Hours = 2 });
                AssertSelfTest(response.Ok && response.State.PowerScheduleActive && response.State.PowerScheduleHours == 2,
                    "CLI power schedule start arms the override");

                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "power_mode", Mode = "saver" });
                AssertSelfTest(response.Ok && !response.State.PowerScheduleActive,
                    "CLI power mode switch cancels a pending schedule");

                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "power_schedule_start" });
                AssertSelfTest(response.Ok && response.State.PowerScheduleActive,
                    "CLI power schedule start reuses the saved hour preset when hours is omitted");
                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "power_schedule_stop" });
                AssertSelfTest(response.Ok && !response.State.PowerScheduleActive,
                    "CLI power schedule stop cancels without an error");

                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "energy_saver_on" });
                AssertSelfTest(response.Ok && response.State.EnergySaverForcedByGuard,
                    "CLI energy saver on reports forced");
                response = form.ExecuteGuardControl(new GuardControlRequest { Action = "energy_saver_off" });
                AssertSelfTest(response.Ok && !response.State.EnergySaverForcedByGuard,
                    "CLI energy saver off clears the forced flag");
            }
            finally
            {
                if (hadOriginalOverlay)
                {
                    string restoreOverlayDetail;
                    NativeMethods.TrySetActivePowerOverlayScheme(originalOverlayGuid, out restoreOverlayDetail);
                }

                if (hadOriginalEnergySaverThreshold)
                {
                    string restoreThresholdDetail;
                    NativeMethods.TryWriteEnergySaverBatteryThresholdPercent(originalEnergySaverThreshold, out restoreThresholdDetail);
                }
            }
        }
    }

    private static void VerifyHitTargets(int logicalWidth, int logicalHeight)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = logicalWidth;
        settings.SpecBoardHeight = logicalHeight;
        settings.Normalize();

        using (GuardBoardForm form = new GuardBoardForm(null, settings, delegate { return (bool?)true; }))
        using (Bitmap bitmap = new Bitmap(logicalWidth, logicalHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.Size = new Size(logicalWidth, logicalHeight);
            form.DrawBoard(g, true);

            GuardHitAction[] required =
            {
                GuardHitAction.SleepToggle,
                GuardHitAction.DisplayToggle,
                GuardHitAction.DisplayMinus,
                GuardHitAction.DisplayPlus,
                GuardHitAction.OfflineMinus,
                GuardHitAction.OfflinePlus,
                GuardHitAction.AiBlockToggle,
                GuardHitAction.QuotaPlanToggle,
                GuardHitAction.CtfRestart,
                GuardHitAction.Close,
                GuardHitAction.Panel,
                GuardHitAction.PowerModeSaver,
                GuardHitAction.PowerModeBalanced,
                GuardHitAction.PowerModePerformance,
                GuardHitAction.PowerScheduleHoursMinus,
                GuardHitAction.PowerScheduleHoursPlus,
                GuardHitAction.PowerScheduleToggle,
                GuardHitAction.EnergySaverToggle,
                GuardHitAction.TranslatorKeepAliveToggle,
                GuardHitAction.CodexAppKeepAliveToggle,
                GuardHitAction.ClaudeAppKeepAliveToggle,
                GuardHitAction.KeepAliveDefaults
            };

            for (int i = 0; i < required.Length; i++)
            {
                AssertSelfTest(
                    form.FindHitTarget(required[i]) != Rectangle.Empty,
                    "hit target missing at width " + logicalWidth.ToString(CultureInfo.InvariantCulture) + ": " + required[i].ToString());
            }

            // Battery care must stay reachable in both layouts: next to its bar in the ring column
            // when there is one, and as a power card when there is not.
            AssertSelfTest(
                form.FindHitTarget(GuardHitAction.BatteryToggle) != Rectangle.Empty,
                "battery toggle missing at width " + logicalWidth.ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < form.hitTargets.Count; i++)
            {
                Rectangle a = form.hitTargets[i].Bounds;
                AssertSelfTest(
                    a.Width > 0 && a.Height > 0,
                    "hit target has no area: " + form.hitTargets[i].Action.ToString());
                for (int j = i + 1; j < form.hitTargets.Count; j++)
                {
                    Rectangle b = form.hitTargets[j].Bounds;
                    AssertSelfTest(
                        !a.IntersectsWith(b),
                        "hit targets overlap at width " + logicalWidth.ToString(CultureInfo.InvariantCulture) + ": " +
                            form.hitTargets[i].Action.ToString() + " / " + form.hitTargets[j].Action.ToString());
                }
            }

            form.runtime.ReleaseAll();
        }
    }

    private Rectangle FindHitTarget(GuardHitAction action)
    {
        for (int i = 0; i < this.hitTargets.Count; i++)
        {
            if (this.hitTargets[i].Action == action)
            {
                return this.hitTargets[i].Bounds;
            }
        }

        return Rectangle.Empty;
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Guard board layout self-test failed: " + message);
        }
    }

    private void DrawFooterPill(Graphics g, Rectangle bounds, string label, Color semanticColor, Font font)
    {
        // Match the three established upper dock boards exactly: measured action width, 4px
        // corners, Control fill, semantic outline, neutral text, and no persistent hover fill.
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -1.0f, -1.0f), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(semanticColor, 170), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.Text))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(label, font, text, bounds, centered);
        }
    }
}
