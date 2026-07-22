using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

// Scheme D rendering for the GUARD board, drawn in the same visual family as SpecBoardForm, the
// Codex task board and NetworkMonitorForm.DockedLayout: AppBackground wash, DesignTokens.Border
// hairlines, UiFontCache pixel fonts on the S(12)/S(9.2)/S(9)/S(7.8) ladder.
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

        Font headerFont = this.fontCache.GetUi(S(12.0f), FontStyle.Bold);
        Font monoFont = this.fontCache.GetMono(S(9.0f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(9.0f), FontStyle.Regular);
        Font bodyBold = this.fontCache.GetUi(S(9.2f), FontStyle.Bold);
        Font smallFont = this.fontCache.GetUi(S(7.8f), FontStyle.Regular);
        Font smallBold = this.fontCache.GetUi(S(7.8f), FontStyle.Bold);
        Font ringBigFont = this.fontCache.GetMono(S(19.0f), FontStyle.Bold);

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
            int gaugeWidth = Math.Max(S(150), (int)Math.Round(body.Width * 0.40));
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
            return "亮屏保持中 · 系统不休眠";
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
            ? "已暂停 " + GuardRuntime.FormatCountdown(this.runtime.GetBatteryCarePauseRemaining(nowUtc))
            : "80% 上限生效";
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

    // Right column: two labelled sections of control cards. Card heights are measured and the
    // leftover space is split between the sections, so the column fills the board at any scale
    // instead of leaving a ragged gap under the last card.
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
        int sectionRow = MeasureLineHeight(g, smallBold, S(4));
        int cardTitleHeight = MeasureLineHeight(g, bodyBold, S(1));
        int cardSubtitleHeight = MeasureLineHeight(g, smallFont, S(1));
        int cardHeight = cardTitleHeight + cardSubtitleHeight + S(8);
        int cardGap = S(4);
        int sectionGap = S(6);

        // Battery care normally lives in the ring column next to its 24h bar. The compact layout has
        // no ring column, so it moves here as a fourth power card — dropping it would silently make
        // a feature unreachable at narrow widths instead of merely rearranging it.
        bool includeBattery = this.IsCompactLayout;
        int cardCount = includeBattery ? 7 : 6;

        int required = sectionRow * 2 + cardHeight * cardCount + cardGap * (cardCount - 2) + sectionGap;
        int slack = Math.Max(0, bounds.Height - required);
        // Spread leftover height across the cards rather than inflating one of them.
        cardHeight += slack / cardCount;

        int y = bounds.Top;
        y = DrawSectionHeader(g, bounds, y, sectionRow, "电源守护 · CodexSleepGuard", DesignTokens.Colors.Success, smallBold);

        y = DrawControlCard(
            g,
            new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
            "睡眠防护",
            this.runtime.SleepGuardEnabled ? "系统不休眠 · 屏幕仍按超时熄灭" : "系统按 Windows 电源设置休眠",
            GuardCardControl.Toggle,
            this.runtime.SleepGuardEnabled,
            string.Empty,
            GuardHitAction.SleepToggle,
            GuardHitAction.None,
            GuardHitAction.None,
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets) + cardGap;

        y = DrawControlCard(
            g,
            new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
            "亮屏计时",
            this.runtime.DisplayGuardActive
                ? "剩余 " + GuardRuntime.FormatCountdown(this.runtime.GetDisplayGuardRemaining(nowUtc)) + " · 到点自动解除"
                : "限时保持屏幕常亮，到点自动解除",
            GuardCardControl.StepperWithAction,
            this.runtime.DisplayGuardActive,
            FormatMinutesLabel(this.runtime.DisplayGuardMinutes),
            GuardHitAction.DisplayToggle,
            GuardHitAction.DisplayMinus,
            GuardHitAction.DisplayPlus,
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets) + cardGap;

        y = DrawControlCard(
            g,
            new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
            "断网自动睡眠",
            !this.runtime.SleepGuardEnabled
                ? "未武装 · 开启睡眠防护后才会触发"
                : (this.runtime.Online
                    ? "在线 · 离线满阈值即解除守护并睡眠"
                    : "离线 " + GuardRuntime.FormatCountdown(this.runtime.GetOfflineElapsed(nowUtc)) + " · 达阈值即请求睡眠"),
            GuardCardControl.Stepper,
            false,
            FormatMinutesLabel(this.runtime.OfflineThresholdMinutes),
            GuardHitAction.None,
            GuardHitAction.OfflineMinus,
            GuardHitAction.OfflinePlus,
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets) + (includeBattery ? cardGap : sectionGap);

        if (includeBattery)
        {
            bool paused = this.runtime.BatteryCarePauseActive;
            y = DrawControlCard(
                g,
                new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
                "电池保护",
                paused
                    ? "已暂停 " + GuardRuntime.FormatCountdown(this.runtime.GetBatteryCarePauseRemaining(nowUtc)) + " 后自动恢复"
                    : "80% 充电上限生效中",
                GuardCardControl.Button,
                paused,
                paused ? "恢复" : "暂停",
                GuardHitAction.BatteryToggle,
                GuardHitAction.None,
                GuardHitAction.None,
                bodyBold,
                smallFont,
                smallBold,
                recordHitTargets) + sectionGap;
        }

        y = DrawSectionHeader(g, bounds, y, sectionRow, "程序守护 · 特殊设置", DesignTokens.Colors.AccentAlt, smallBold);

        bool aiBlocked = this.CurrentSettings != null && this.CurrentSettings.AiRequestProtectionManualBlockEnabled;
        y = DrawControlCard(
            g,
            new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
            "链接阻断",
            aiBlocked ? "已阻断本程序的 OpenAI / Claude 请求" : "阻断本程序的 OpenAI / Claude 请求",
            GuardCardControl.Toggle,
            aiBlocked,
            string.Empty,
            GuardHitAction.AiBlockToggle,
            GuardHitAction.None,
            GuardHitAction.None,
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets) + cardGap;

        bool quotaPlan = this.CurrentSettings != null && this.CurrentSettings.CodexQuotaPlanEnabled;
        y = DrawControlCard(
            g,
            new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
            "额度计划",
            "阈值与 goal 列表在普通设置中调整",
            GuardCardControl.Toggle,
            quotaPlan,
            string.Empty,
            GuardHitAction.QuotaPlanToggle,
            GuardHitAction.None,
            GuardHitAction.None,
            bodyBold,
            smallFont,
            smallBold,
            recordHitTargets) + cardGap;

        DrawControlCard(
            g,
            new Rectangle(bounds.Left, y, bounds.Width, cardHeight),
            "CTF 重启",
            "提权重启当前会话的 ctfmon.exe",
            GuardCardControl.Button,
            false,
            "重启",
            GuardHitAction.CtfRestart,
            GuardHitAction.None,
            GuardHitAction.None,
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

    private int DrawControlCard(
        Graphics g,
        Rectangle bounds,
        string title,
        string subtitle,
        GuardCardControl control,
        bool state,
        string value,
        GuardHitAction primary,
        GuardHitAction minus,
        GuardHitAction plus,
        Font titleFont,
        Font smallFont,
        Font smallBold,
        bool recordHitTargets)
    {
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -0.5f, -0.5f), S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 220)))
        {
            g.FillPath(fill, path);
        }

        int padX = S(9);
        int controlRight = bounds.Right - padX;
        int controlTop;

        // Controls are laid out right-to-left from the card's right edge, and the text block is
        // measured against whatever remains. Doing it in the other order is how text ends up
        // running underneath a toggle at small widths.
        if (control == GuardCardControl.Toggle)
        {
            int toggleWidth = S(32);
            int toggleHeight = S(17);
            controlTop = bounds.Top + Math.Max(0, (bounds.Height - toggleHeight) / 2);
            Rectangle toggle = new Rectangle(controlRight - toggleWidth, controlTop, toggleWidth, toggleHeight);
            DrawToggle(g, toggle, state);
            if (recordHitTargets && primary != GuardHitAction.None)
            {
                this.hitTargets.Add(new GuardHitTarget { Bounds = toggle, Action = primary });
            }

            controlRight = toggle.Left - S(6);
        }
        else if (control == GuardCardControl.Button)
        {
            int buttonHeight = MeasureLineHeight(g, smallBold, S(5));
            int buttonWidth = Math.Max(S(44), (int)Math.Ceiling(g.MeasureString(value, smallBold).Width) + S(16));
            controlTop = bounds.Top + Math.Max(0, (bounds.Height - buttonHeight) / 2);
            Rectangle button = new Rectangle(controlRight - buttonWidth, controlTop, buttonWidth, buttonHeight);
            DrawActionButton(g, button, value, DesignTokens.Colors.Accent, smallBold);
            if (recordHitTargets && primary != GuardHitAction.None)
            {
                this.hitTargets.Add(new GuardHitTarget { Bounds = button, Action = primary });
            }

            controlRight = button.Left - S(6);
        }
        else
        {
            int rowHeight = MeasureLineHeight(g, smallBold, S(5));
            controlTop = bounds.Top + Math.Max(0, (bounds.Height - rowHeight) / 2);

            if (control == GuardCardControl.StepperWithAction)
            {
                string actionLabel = state ? "停止" : "开始";
                int actionWidth = Math.Max(S(40), (int)Math.Ceiling(g.MeasureString(actionLabel, smallBold).Width) + S(14));
                Rectangle action = new Rectangle(controlRight - actionWidth, controlTop, actionWidth, rowHeight);
                DrawActionButton(g, action, actionLabel, state ? DesignTokens.Colors.Warning : DesignTokens.Colors.Accent, smallBold);
                if (recordHitTargets && primary != GuardHitAction.None)
                {
                    this.hitTargets.Add(new GuardHitTarget { Bounds = action, Action = primary });
                }

                controlRight = action.Left - S(5);
            }

            Rectangle stepper = DrawStepper(g, controlRight, controlTop, rowHeight, value, smallBold, smallFont, minus, plus, recordHitTargets);
            controlRight = stepper.Left - S(6);
        }

        int textWidth = Math.Max(S(30), controlRight - (bounds.Left + padX));
        int titleHeight = MeasureLineHeight(g, titleFont, S(1));
        int subtitleHeight = MeasureLineHeight(g, smallFont, S(1));
        int textTop = bounds.Top + Math.Max(0, (bounds.Height - titleHeight - subtitleHeight) / 2);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush subtitleBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(title, titleFont, titleBrush, new RectangleF(bounds.Left + padX, textTop, textWidth, titleHeight), near);
            g.DrawString(subtitle, smallFont, subtitleBrush, new RectangleF(bounds.Left + padX, textTop + titleHeight, textWidth, subtitleHeight), near);
        }

        return bounds.Bottom;
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

        DrawFooterPill(g, panelBounds, "设置", smallFont);
        DrawFooterPill(g, closeBounds, "关闭", smallFont);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new GuardHitTarget { Bounds = panelBounds, Action = GuardHitAction.Panel });
            this.hitTargets.Add(new GuardHitTarget { Bounds = closeBounds, Action = GuardHitAction.Close });
        }

        int noticeLeft = closeBounds.Right + S(8);
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
        AssertSelfTest(StepValue(ladder, 30, -1) == 30, "display ladder clamps at the low end");
        AssertSelfTest(StepValue(ladder, 30, 1) == 60, "display ladder steps up");
        AssertSelfTest(StepValue(ladder, 480, 1) == 480, "display ladder clamps at the high end");
        AssertSelfTest(StepValue(ladder, 480, -1) == 300, "display ladder steps down");
        AssertSelfTest(StepValue(ladder, 999, 1) == ladder[0], "off-ladder value snaps to the first step");

        int[] offline = WidgetSettings.GuardOfflineThresholdMinuteSteps;
        AssertSelfTest(StepValue(offline, 1, -1) == 1, "offline ladder clamps at the low end");
        AssertSelfTest(StepValue(offline, 30, 1) == 30, "offline ladder clamps at the high end");

        // Both layouts have to register every control; the compact one drops the ring column, and
        // dropping the battery button with it would silently strip a feature at narrow widths.
        VerifyHitTargets(648, 400);
        VerifyHitTargets(320, 400);

        Console.WriteLine("Guard board layout: PASS hit targets wide+compact, ladder clamping");
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
                GuardHitAction.Panel
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

    private void DrawFooterPill(Graphics g, Rectangle bounds, string label, Font font)
    {
        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 220)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 180), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(label, font, text, bounds, centered);
        }
    }
}
