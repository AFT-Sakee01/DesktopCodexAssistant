using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// The signature visual is a seven-day quota trace interrupted by reset gates. The right rail keeps
// speed-window and reset-card state visible without turning the board into a generic dashboard grid.
internal sealed partial class ResetSpeedBoardForm
{
    private const string AttributionText = "Codex Radar · ChatGPT usage";

    protected override void DrawWindowContent(Graphics g)
    {
        DrawBoard(g);
    }

    private void DrawBoard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 246)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0.5f, 0.5f, this.Width - 1, this.Height - 1), Math.Max(3, S(10))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 116), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawPath(border, shell);
        }

        Font titleFont = this.fontCache.GetUi(S(13.0f), FontStyle.Bold);
        Font sectionFont = this.fontCache.GetUi(S(9.2f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(8.5f), FontStyle.Regular);
        Font smallFont = this.fontCache.GetUi(S(7.8f), FontStyle.Regular);
        Font monoFont = this.fontCache.GetMono(S(8.8f), FontStyle.Bold);
        Font heroFont = this.fontCache.GetMono(S(20.0f), FontStyle.Bold);

        int pad = S(11);
        Rectangle content = new Rectangle(pad, pad, Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - pad * 2));
        int headerHeight = S(42);
        int footerHeight = MeasureLineHeight(g, smallFont, S(5));
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, headerHeight);
        Rectangle footer = new Rectangle(content.Left, content.Bottom - footerHeight, content.Width, footerHeight);
        Rectangle body = new Rectangle(content.Left, header.Bottom + S(4), content.Width, Math.Max(1, footer.Top - header.Bottom - S(8)));
        int rightWidth = Math.Max(S(180), (int)Math.Round(body.Width * 0.31));
        Rectangle left = new Rectangle(body.Left, body.Top, Math.Max(1, body.Width - rightWidth - S(8)), body.Height);
        Rectangle right = new Rectangle(left.Right + S(8), body.Top, Math.Max(1, body.Right - left.Right - S(8)), body.Height);
        int resetListHeight = S(96);
        Rectangle trend = new Rectangle(left.Left, left.Top, left.Width, Math.Max(1, left.Height - resetListHeight - S(7)));
        Rectangle resetRow = new Rectangle(left.Left, trend.Bottom + S(7), left.Width, Math.Max(1, left.Bottom - trend.Bottom - S(7)));
        int resetPanelGap = S(7);
        int recentWidth = Math.Max(1, (resetRow.Width - resetPanelGap) / 2);
        Rectangle recent = new Rectangle(resetRow.Left, resetRow.Top, recentWidth, resetRow.Height);
        Rectangle probability = new Rectangle(recent.Right + resetPanelGap, resetRow.Top,
            Math.Max(1, resetRow.Right - recent.Right - resetPanelGap), resetRow.Height);
        int cardHeight = S(80);
        Rectangle speed = new Rectangle(right.Left, right.Top, right.Width, Math.Max(1, right.Height - cardHeight - S(7)));
        Rectangle credits = new Rectangle(right.Left, speed.Bottom + S(7), right.Width, Math.Max(1, right.Bottom - speed.Bottom - S(7)));

        DrawHeader(g, header, titleFont, bodyFont, monoFont);
        DrawTrendPanel(g, trend, sectionFont, bodyFont, smallFont, monoFont);
        DrawRecentResetPanel(g, recent, sectionFont, smallFont, monoFont);
        DrawResetProbabilityPanel(g, probability, sectionFont, smallFont, monoFont);
        DrawSpeedPanel(g, speed, sectionFont, smallFont, monoFont, heroFont);
        DrawCreditsPanel(g, credits, sectionFont, bodyFont, monoFont);
        DrawFooter(g, footer, smallFont);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.ResetSpeed, this.LayerScale);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font titleFont, Font bodyFont, Font monoFont)
    {
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.ResetSpeed);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush accentBrush = new SolidBrush(accent))
        using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("RESET / SPEED", titleFont, titleBrush, new Rectangle(bounds.Left, bounds.Top, S(142), S(21)), near);
            g.DrawString("重置与速蹬", bodyFont, accentBrush, new Rectangle(bounds.Left, bounds.Top + S(22), S(142), S(16)), near);

            int used = ComputeSevenDayUsedPercent(this.snapshot.QuotaHistory);
            string sevenDay = used >= 0 ? "7D  " + used.ToString(CultureInfo.InvariantCulture) + "%" : "7D  --";
            string cards = this.snapshot.ResetCreditsKnown
                ? "RS  " + this.snapshot.ResetCreditCount.ToString("00", CultureInfo.InvariantCulture)
                : this.snapshot.ResetCreditsRequestRunning ? "RS  .." : "RS  --";
            int cardWidth = S(76);
            Rectangle sevenRect = new Rectangle(bounds.Right - cardWidth * 2 - S(62), bounds.Top + S(2), cardWidth, S(28));
            Rectangle rsRect = new Rectangle(sevenRect.Right + S(5), sevenRect.Top, cardWidth, sevenRect.Height);
            DrawHeaderChip(g, sevenRect, sevenDay, monoFont, DesignTokens.Colors.QuotaGood);
            DrawHeaderChip(g, rsRect, cards, monoFont, accent);
            string updated = this.snapshot.UpdatedKnown ? this.snapshot.UpdatedLocal.ToString("HH:mm", CultureInfo.InvariantCulture) : "--:--";
            g.DrawString(updated, monoFont, mutedBrush, new Rectangle(rsRect.Right + S(5), bounds.Top + S(7), S(50), S(18)), far);
        }
    }

    private void DrawHeaderChip(Graphics g, Rectangle bounds, string text, Font font, Color accent)
    {
        using (GraphicsPath path = RoundedRectangle(bounds, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(accent, 24)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 90), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush textBrush = new SolidBrush(accent))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(text, font, textBrush, bounds, center);
        }
    }

    private void DrawTrendPanel(Graphics g, Rectangle bounds, Font sectionFont, Font bodyFont, Font smallFont, Font monoFont)
    {
        DrawPanel(g, bounds, DesignTokens.Colors.QuotaGood);
        int pad = S(8);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush currentBrush = new SolidBrush(DesignTokens.Colors.QuotaGood))
        using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("过去 7 天 · 周额度余量", sectionFont, titleBrush, new Rectangle(bounds.Left + pad, bounds.Top + S(5), bounds.Width - pad * 2, S(17)), near);
            string current = this.snapshot.QuotaKnown ? this.snapshot.WeeklyRemainingPercent.ToString(CultureInfo.InvariantCulture) + "%" : "--";
            g.DrawString(current, monoFont, currentBrush, new Rectangle(bounds.Right - S(54), bounds.Top + S(5), S(46), S(17)), far);
        }

        Rectangle chart = new Rectangle(bounds.Left + S(30), bounds.Top + S(29), Math.Max(1, bounds.Width - S(40)), Math.Max(1, bounds.Height - S(52)));
        int[] ticks = { 100, 50, 0 };
        using (Pen grid = new Pen(DesignTokens.White(25), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            for (int i = 0; i < ticks.Length; i++)
            {
                int y = ResolveChartY(chart, ticks[i]);
                g.DrawLine(grid, chart.Left, y, chart.Right, y);
                g.DrawString(ticks[i].ToString(CultureInfo.InvariantCulture), smallFont, labelBrush, new Rectangle(bounds.Left + S(2), y - S(7), S(24), S(14)), far);
            }
        }

        IList<ResetSpeedQuotaPoint> points = this.snapshot.QuotaHistory;
        PointF? previous = null;
        int knownCount = 0;
        using (Pen line = new Pen(DesignTokens.Colors.QuotaGood, Math.Max(1.8f, S(1.4f))))
        using (SolidBrush dot = new SolidBrush(DesignTokens.Colors.Warning))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            int count = Math.Max(1, points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                ResetSpeedQuotaPoint point = points[i];
                float x = chart.Left + (count <= 1 ? 0.0f : chart.Width * i / (float)(count - 1));
                if (point != null)
                {
                    g.DrawString(point.DateLocal.ToString("MM/dd", CultureInfo.InvariantCulture), smallFont, labelBrush,
                        new RectangleF(x - S(20), chart.Bottom + S(3), S(40), S(14)), center);
                    if (point.Known)
                    {
                        PointF current = new PointF(x, ResolveChartY(chart, point.WeeklyRemainingPercent));
                        if (previous.HasValue) g.DrawLine(line, previous.Value, current);
                        g.FillEllipse(dot, current.X - S(2.5f), current.Y - S(2.5f), S(5), S(5));
                        previous = current;
                        knownCount++;
                    }
                    else
                    {
                        previous = null;
                    }
                }
            }
        }

        DrawResetGates(g, chart, smallFont);
        if (knownCount == 0)
        {
            using (SolidBrush empty = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat center = CreateFormat(StringAlignment.Center))
            {
                g.DrawString("正在积累 7 天历史", bodyFont, empty, chart, center);
            }
        }
    }

    private void DrawResetGates(Graphics g, Rectangle chart, Font smallFont)
    {
        DateTime start = DateTime.Now.Date.AddDays(-6.0);
        DateTime end = DateTime.Now.Date.AddDays(1.0);
        for (int i = 0; i < this.snapshot.ResetEvents.Count; i++)
        {
            ResetSpeedResetEvent item = this.snapshot.ResetEvents[i];
            if (item == null || item.TimestampLocal < start || item.TimestampLocal >= end) continue;
            float ratio = (float)((item.TimestampLocal - start).TotalHours / Math.Max(1.0, (end - start).TotalHours));
            float x = chart.Left + chart.Width * Math.Max(0.0f, Math.Min(1.0f, ratio));
            Color color = GetResetKindColor(item.Kind);
            using (Pen gate = new Pen(DesignTokens.WithAlpha(color, 190), Math.Max(1.0f, this.LayerScale)))
            using (SolidBrush label = new SolidBrush(color))
            using (StringFormat center = CreateFormat(StringAlignment.Center))
            {
                gate.DashStyle = DashStyle.Dash;
                g.DrawLine(gate, x, chart.Top, x, chart.Bottom);
                g.DrawString(GetResetKindShortLabel(item.Kind), smallFont, label, new RectangleF(x - S(16), chart.Top + S(2), S(32), S(13)), center);
            }
        }
    }

    private void DrawRecentResetPanel(Graphics g, Rectangle bounds, Font sectionFont, Font smallFont, Font monoFont)
    {
        DrawPanel(g, bounds, DesignTokens.Colors.Warning);
        int pad = S(8);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString("近期重置", sectionFont, titleBrush, new Rectangle(bounds.Left + pad, bounds.Top + S(5), bounds.Width - pad * 2, S(17)), near);
        }
        int rowTop = bounds.Top + S(27);
        int rowHeight = Math.Max(S(18), (bounds.Bottom - S(5) - rowTop) / 2);
        int count = Math.Min(2, this.snapshot.ResetEvents.Count);
        if (count == 0)
        {
            using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            {
                g.DrawString("近 7 天暂无可确认的重置事件", smallFont, muted,
                    new Rectangle(bounds.Left + pad, rowTop, bounds.Width - pad * 2, rowHeight), near);
            }
            return;
        }
        for (int i = 0; i < count; i++)
        {
            ResetSpeedResetEvent item = this.snapshot.ResetEvents[i];
            Rectangle row = new Rectangle(bounds.Left + pad, rowTop + rowHeight * i, bounds.Width - pad * 2, rowHeight);
            Color color = GetResetKindColor(item.Kind);
            using (SolidBrush dot = new SolidBrush(color))
            using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextMuted))
            using (SolidBrush strong = new SolidBrush(color))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            using (StringFormat far = CreateFormat(StringAlignment.Far))
            {
                g.FillEllipse(dot, row.Left, row.Top + row.Height / 2 - S(2), S(4), S(4));
                g.DrawString(item.TimestampLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture), monoFont, text,
                    new Rectangle(row.Left + S(10), row.Top, S(76), row.Height), near);
                g.DrawString(GetResetKindLabel(item.Kind), smallFont, strong,
                    new Rectangle(row.Left + S(89), row.Top, S(45), row.Height), near);
                g.DrawString("→ " + item.WeeklyRemainingPercent.ToString(CultureInfo.InvariantCulture) + "%", monoFont, text,
                    new Rectangle(row.Right - S(46), row.Top, S(46), row.Height), far);
            }
        }
    }

    private void DrawResetProbabilityPanel(Graphics g, Rectangle bounds, Font sectionFont, Font smallFont, Font monoFont)
    {
        DrawPanel(g, bounds, DesignTokens.Colors.WarningDeep);
        int pad = S(8);
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush meta = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("重置概率", sectionFont, title,
                new Rectangle(bounds.Left + pad, bounds.Top + S(5), S(74), S(17)), near);
            string updated = this.snapshot.ResetRadarUpdatedAtKnown
                ? "RADAR · " + this.snapshot.ResetRadarUpdatedAtLocal.ToString("MM/dd", CultureInfo.InvariantCulture)
                : "RADAR";
            g.DrawString(updated, smallFont, meta,
                new Rectangle(bounds.Left + S(82), bounds.Top + S(6), Math.Max(1, bounds.Width - S(90)), S(16)), far);
        }

        int rowTop = bounds.Top + S(26);
        int rowHeight = Math.Max(S(25), (bounds.Bottom - S(5) - rowTop) / 2);
        if (!this.snapshot.ResetRadarKnown)
        {
            using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            {
                g.DrawString("等待 Codex Radar 判断", smallFont, muted,
                    new Rectangle(bounds.Left + pad, rowTop + S(7), bounds.Width - pad * 2, rowHeight), near);
            }
            return;
        }

        DrawResetProbabilityRow(g, new Rectangle(bounds.Left + pad, rowTop, bounds.Width - pad * 2, rowHeight),
            "发重置卡", this.snapshot.ResetCardStatus, this.snapshot.ResetCardDescription,
            DesignTokens.Colors.Warning, true, smallFont, monoFont);
        DrawResetProbabilityRow(g, new Rectangle(bounds.Left + pad, rowTop + rowHeight, bounds.Width - pad * 2, rowHeight),
            "硬重置", this.snapshot.HardResetStatus, this.snapshot.HardResetDescription,
            DesignTokens.Colors.QuotaGood, false, smallFont, monoFont);
    }

    private void DrawResetProbabilityRow(
        Graphics g,
        Rectangle row,
        string label,
        string status,
        string description,
        Color statusColor,
        bool drawDivider,
        Font smallFont,
        Font monoFont)
    {
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (SolidBrush descriptionBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (Pen divider = new Pen(DesignTokens.White(22), Math.Max(1.0f, this.LayerScale)))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString(label, smallFont, labelBrush,
                new Rectangle(row.Left, row.Top, Math.Max(1, row.Width - S(72)), S(15)), near);
            g.DrawString(status ?? string.Empty, monoFont, statusBrush,
                new Rectangle(row.Right - S(70), row.Top, S(70), S(15)), far);
            g.DrawString(description ?? string.Empty, smallFont, descriptionBrush,
                new Rectangle(row.Left, row.Top + S(14), row.Width, Math.Max(S(13), row.Height - S(14))), near);
            if (drawDivider)
            {
                g.DrawLine(divider, row.Left, row.Bottom - 1, row.Right, row.Bottom - 1);
            }
        }
    }

    private void DrawSpeedPanel(Graphics g, Rectangle bounds, Font sectionFont, Font smallFont, Font monoFont, Font heroFont)
    {
        DrawPanel(g, bounds, DesignTokens.Colors.SpeedWindowCountdown);
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            g.DrawString("速蹬窗口", sectionFont, title, new Rectangle(bounds.Left, bounds.Top + S(6), bounds.Width, S(18)), center);
        }
        int diameter = Math.Min(bounds.Width - S(36), bounds.Height - S(67));
        diameter = Math.Max(S(54), diameter);
        Rectangle dial = new Rectangle(bounds.Left + (bounds.Width - diameter) / 2, bounds.Top + S(29), diameter, diameter);
        float stroke = Math.Max(2.0f, S(5.0f));
        using (Pen track = new Pen(DesignTokens.White(34), stroke))
        {
            track.StartCap = LineCap.Round;
            track.EndCap = LineCap.Round;
            g.DrawArc(track, dial, -90.0f, 359.0f);
        }
        Color stateColor = this.snapshot.SpeedWindowOpen ? DesignTokens.Colors.SpeedWindowCountdown : DesignTokens.Colors.GlyphMuted;
        if (this.snapshot.SpeedWindowOpen && this.snapshot.SpeedWindowRemainingRatio > 0.001f)
        {
            using (Pen arc = new Pen(stateColor, stroke))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, dial, -90.0f, Math.Max(4.0f, 359.0f * this.snapshot.SpeedWindowRemainingRatio));
            }
        }
        string hero = this.snapshot.SpeedWindowOpen
            ? FormatCountdown(this.snapshot.SpeedWindowRemainingMinutes)
            : this.snapshot.SpeedWindowKnown ? "CLOSED" : "--:--";
        using (SolidBrush heroBrush = new SolidBrush(stateColor))
        using (SolidBrush detailBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            g.DrawString(hero, heroFont, heroBrush, new Rectangle(dial.Left, dial.Top + dial.Height / 2 - S(18), dial.Width, S(27)), center);
            string detail = this.snapshot.SpeedWindowOpen ? "开放 · 剩余" : "当前待机";
            g.DrawString(detail, smallFont, detailBrush, new Rectangle(dial.Left, dial.Top + dial.Height / 2 + S(10), dial.Width, S(16)), center);
        }
        string range = this.snapshot.SpeedWindowOpenedAtKnown && this.snapshot.SpeedWindowClosedAtKnown
            ? this.snapshot.SpeedWindowOpenedAtLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) + " → " +
              this.snapshot.SpeedWindowClosedAtLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
            : "等待 Codex Radar 窗口数据";
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            g.DrawString(range, smallFont, muted, new Rectangle(bounds.Left + S(5), bounds.Bottom - S(22), bounds.Width - S(10), S(16)), center);
        }
    }

    private void DrawCreditsPanel(Graphics g, Rectangle bounds, Font sectionFont, Font bodyFont, Font monoFont)
    {
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.ResetSpeed);
        DrawPanel(g, bounds, accent);
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (SolidBrush accentBrush = new SolidBrush(accent))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("重置卡", sectionFont, title, new Rectangle(bounds.Left + S(8), bounds.Top + S(7), bounds.Width / 2, S(18)), near);
            string count = this.snapshot.ResetCreditsKnown
                ? this.snapshot.ResetCreditCount.ToString("00", CultureInfo.InvariantCulture)
                : this.snapshot.ResetCreditsRequestRunning ? ".." : "--";
            g.DrawString(count, monoFont, accentBrush, new Rectangle(bounds.Right - S(48), bounds.Top + S(7), S(40), S(18)), far);
            string expiry = this.snapshot.ResetCreditExpirationKnown
                ? "最近到期 " + FormatRemaining(DateTime.Now, this.snapshot.ResetCreditExpirationLocal)
                : this.snapshot.ResetCreditsKnown ? "暂无有效到期时间" : "等待账户额度数据";
            g.DrawString(expiry, bodyFont, muted, new Rectangle(bounds.Left + S(8), bounds.Top + S(34), bounds.Width - S(16), S(18)), near);
            if (this.snapshot.ResetCreditExpirationKnown)
            {
                g.DrawString(this.snapshot.ResetCreditExpirationLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture), monoFont, muted,
                    new Rectangle(bounds.Left + S(8), bounds.Bottom - S(21), bounds.Width - S(16), S(16)), near);
            }
        }
    }

    private void DrawFooter(Graphics g, Rectangle bounds, Font font)
    {
        int refreshWidth = Math.Min(
            bounds.Width,
            Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("刷新", font).Width) + S(14)));
        Rectangle refreshBounds = new Rectangle(bounds.Left, bounds.Top, refreshWidth, bounds.Height);
        int closeLeft = Math.Min(bounds.Right, refreshBounds.Right + S(4));
        int closeWidth = Math.Min(
            Math.Max(0, bounds.Right - closeLeft),
            Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("关闭", font).Width) + S(14)));
        Rectangle closeBounds = new Rectangle(closeLeft, bounds.Top, closeWidth, bounds.Height);
        int detailsLeft = Math.Min(bounds.Right, closeBounds.Right + S(5));
        Rectangle details = new Rectangle(detailsLeft, bounds.Top, Math.Max(0, bounds.Right - detailsLeft), bounds.Height);

        this.refreshHitBounds = refreshBounds;
        this.closeHitBounds = closeBounds;
        DrawFooterAction(g, refreshBounds, "刷新", DesignTokens.Colors.Success, font);
        DrawFooterAction(g, closeBounds, "关闭", DesignTokens.Colors.Danger, font);

        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            Rectangle sourceBounds = new Rectangle(
                details.Left,
                details.Top,
                Math.Max(0, (int)Math.Floor(details.Width * 0.55)),
                details.Height);
            Rectangle resetBounds = new Rectangle(
                sourceBounds.Right,
                details.Top,
                Math.Max(0, details.Right - sourceBounds.Right),
                details.Height);
            g.DrawString(AttributionText, font, muted, sourceBounds, near);
            string reset = this.snapshot.WeeklyResetKnown
                ? "周重置 " + this.snapshot.WeeklyResetLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
                : "周重置 --";
            g.DrawString(reset, font, muted, resetBounds, far);
        }
    }

    private void DrawFooterAction(Graphics g, Rectangle bounds, string text, Color semanticColor, Font font)
    {
        using (GraphicsPath action = RoundedRectangle(RectangleF.Inflate(bounds, -1.0f, -1.0f), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(semanticColor, 170), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.Text))
        using (StringFormat centered = CreateFormat(StringAlignment.Center))
        {
            g.FillPath(fill, action);
            g.DrawPath(border, action);
            g.DrawString(text, font, textBrush, bounds, centered);
        }
    }

    private void DrawPanel(Graphics g, Rectangle bounds, Color accent)
    {
        using (GraphicsPath path = RoundedRectangle(bounds, S(7)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 212)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 58), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
    }

    private static int ComputeSevenDayUsedPercent(IList<ResetSpeedQuotaPoint> points)
    {
        int total = 0;
        int previous = -1;
        for (int i = 0; points != null && i < points.Count; i++)
        {
            ResetSpeedQuotaPoint point = points[i];
            if (point == null || !point.Known) continue;
            if (previous >= 0 && point.WeeklyRemainingPercent < previous) total += previous - point.WeeklyRemainingPercent;
            previous = point.WeeklyRemainingPercent;
        }
        return previous < 0 ? -1 : Math.Min(999, total);
    }

    private static int ResolveChartY(Rectangle chart, int percent)
    {
        int value = Math.Max(0, Math.Min(100, percent));
        return chart.Bottom - (int)Math.Round(chart.Height * value / 100.0);
    }

    private static Color GetResetKindColor(ResetSpeedResetKind kind)
    {
        switch (kind)
        {
            case ResetSpeedResetKind.Credit: return DesignTokens.Colors.WarningDeep;
            case ResetSpeedResetKind.Hard: return DesignTokens.Colors.SpeedWindowCountdown;
            default: return DesignTokens.Colors.Warning;
        }
    }

    private static int MeasureLineHeight(Graphics g, Font font, int padding)
    {
        return Math.Max(
            1,
            (int)Math.Ceiling(
                g.MeasureString("Ag国", font, int.MaxValue, StringFormat.GenericTypographic).Height) + padding);
    }

    private static string GetResetKindLabel(ResetSpeedResetKind kind)
    {
        switch (kind)
        {
            case ResetSpeedResetKind.Credit: return "重置卡";
            case ResetSpeedResetKind.Hard: return "硬重置";
            default: return "自然重置";
        }
    }

    private static string GetResetKindShortLabel(ResetSpeedResetKind kind)
    {
        switch (kind)
        {
            case ResetSpeedResetKind.Credit: return "CARD";
            case ResetSpeedResetKind.Hard: return "HARD";
            default: return "RESET";
        }
    }

    private static string FormatCountdown(int totalMinutes)
    {
        int value = Math.Max(0, Math.Min(100 * 60, totalMinutes));
        return (value / 60).ToString(value >= 6000 ? "000" : "00", CultureInfo.InvariantCulture) + ":" +
            (value % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    private static string FormatRemaining(DateTime nowLocal, DateTime expirationLocal)
    {
        TimeSpan span = expirationLocal - nowLocal;
        if (span.TotalHours <= 24.0) return Math.Max(0, (int)Math.Ceiling(span.TotalHours)).ToString(CultureInfo.InvariantCulture) + "h";
        return Math.Max(1, (int)Math.Ceiling(span.TotalDays)).ToString(CultureInfo.InvariantCulture) + "d";
    }

    private static StringFormat CreateFormat(StringAlignment alignment)
    {
        return new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
    }
}
