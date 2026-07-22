using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// Scheme 2: the cost-vs-IQ scatter is the signature visual, with a restrained information rail for
// the leader, quota/time/efficiency/token trends and lifecycle roster. All geometry is derived from
// the Spec-board footprint and real font metrics so high-DPI scaling cannot produce overlap.
internal sealed partial class CodexIqBoardForm
{
    private const int CompactMinimumLogicalWidth = 520;
    private const string CodexRadarAttributionText = "数据来源：Codex 雷达 codexradar.com";

    private bool IsCompactLayout
    {
        get { return this.CurrentSettings != null && this.CurrentSettings.SpecBoardWidth < CompactMinimumLogicalWidth; }
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawBoard(g);
    }

    private void DrawBoard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 242)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0.5f, 0.5f, this.Width - 1, this.Height - 1), Math.Max(3, S(10))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 104), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawPath(border, shell);
        }

        Font titleFont = this.fontCache.GetUi(S(12.0f), FontStyle.Bold);
        Font bodyBold = this.fontCache.GetUi(S(8.6f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(8.2f), FontStyle.Regular);
        Font smallFont = this.fontCache.GetUi(S(7.2f), FontStyle.Regular);
        Font smallBold = this.fontCache.GetUi(S(7.2f), FontStyle.Bold);
        Font monoFont = this.fontCache.GetMono(S(8.0f), FontStyle.Bold);
        Font leaderFont = this.fontCache.GetMono(S(18.0f), FontStyle.Bold);

        int pad = S(11);
        int headerHeight = MeasureLineHeight(g, titleFont, S(7));
        int footerHeight = MeasureLineHeight(g, smallFont, S(5));
        Rectangle content = new Rectangle(pad, pad, Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - pad * 2));
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, headerHeight);
        Rectangle footer = new Rectangle(content.Left, content.Bottom - footerHeight, content.Width, footerHeight);
        // Status band above the footer: the flattened refresh line plus the four upstream service
        // LEDs, relocated here from the network dock panel.
        int statusHeight = Math.Max(S(20), MeasureLineHeight(g, smallBold, S(12)));
        Rectangle statusBand = new Rectangle(content.Left, footer.Top - S(4) - statusHeight, content.Width, statusHeight);
        Rectangle body = new Rectangle(
            content.Left,
            header.Bottom + S(5),
            content.Width,
            Math.Max(1, statusBand.Top - S(5) - header.Bottom));

        DrawHeader(g, header, titleFont, bodyBold, smallFont);
        if (this.IsCompactLayout)
        {
            DrawScatter(g, body, bodyBold, smallFont, monoFont);
        }
        else
        {
            int leftWidth = Math.Max(S(280), (int)Math.Round(body.Width * 0.61));
            Rectangle scatter = new Rectangle(body.Left, body.Top, leftWidth, body.Height);
            Rectangle rail = new Rectangle(scatter.Right + S(9), body.Top, Math.Max(1, body.Right - scatter.Right - S(9)), body.Height);
            using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 112), Math.Max(1.0f, this.LayerScale)))
            {
                g.DrawLine(divider, scatter.Right + S(4), scatter.Top, scatter.Right + S(4), scatter.Bottom);
            }

            DrawScatter(g, scatter, bodyBold, smallFont, monoFont);
            DrawInformationRail(g, rail, bodyBold, bodyFont, smallBold, smallFont, monoFont, leaderFont);
        }

        DrawStatusBand(g, statusBand, smallBold, smallFont);
        DrawFooter(g, footer, smallFont, monoFont);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.CodexIq, this.LayerScale);
    }

    // Refresh line on the left, service LEDs on the right. Both are cache-only projections.
    private void DrawStatusBand(Graphics g, Rectangle bounds, Font smallBold, Font smallFont)
    {
        DrawPanelBackground(g, bounds, DesignTokens.Colors.GlyphMuted);
        int pad = S(7);
        int ledWidth = Math.Min((int)Math.Round(bounds.Width * 0.46), S(220));
        Rectangle refreshRect = new Rectangle(
            bounds.Left + pad, bounds.Top, Math.Max(1, bounds.Width - ledWidth - pad * 2), bounds.Height);
        Rectangle ledRect = new Rectangle(
            refreshRect.Right + pad, bounds.Top, Math.Max(1, bounds.Right - pad - refreshRect.Right - pad), bounds.Height);
        DrawRefreshLine(g, refreshRect, smallBold, smallFont);
        DrawServiceLeds(g, ledRect, smallFont);
    }

    // The Radar update-status ring, flattened: a track with the boundary tick at the left, the
    // last-refresh marker and the "now" head as dots, and the active segment coloured by phase.
    private void DrawRefreshLine(Graphics g, Rectangle bounds, Font smallBold, Font smallFont)
    {
        CodexIqBoardRefreshStatus refresh = this.snapshot.Refresh ?? new CodexIqBoardRefreshStatus();
        Color status = refresh.Known && refresh.StatusColor.A > 0
            ? refresh.StatusColor
            : DesignTokens.Colors.GlyphMuted;

        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.None))
        {
            g.DrawString("刷新", smallBold, labelBrush, new Rectangle(bounds.Left, bounds.Top, S(30), bounds.Height), near);
        }

        int trackLeft = bounds.Left + S(32);
        string phase = refresh.PhaseText ?? string.Empty;
        int phaseWidth = phase.Length > 0 ? (int)Math.Ceiling(g.MeasureString(phase, smallFont).Width) + S(5) : 0;
        int trackRight = Math.Max(trackLeft + S(20), bounds.Right - phaseWidth);
        Rectangle track = new Rectangle(
            trackLeft, bounds.Top + bounds.Height / 2 - S(2), Math.Max(1, trackRight - trackLeft), Math.Max(2, S(4)));

        using (GraphicsPath tp = RoundedRectangle(track, track.Height / 2.0f))
        using (SolidBrush tb = new SolidBrush(DesignTokens.White(40)))
        {
            g.FillPath(tb, tp);
        }

        if (refresh.Known)
        {
            if (refresh.Warning)
            {
                using (GraphicsPath wp = RoundedRectangle(track, track.Height / 2.0f))
                using (SolidBrush wb = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 74)))
                {
                    g.FillPath(wb, wp);
                }
            }

            if (refresh.ArcSweepFraction > 0.003f)
            {
                DrawRefreshSegment(g, track, refresh.ArcStartFraction, refresh.ArcSweepFraction, DesignTokens.WithAlpha(status, 235));
            }

            using (Pen tick = new Pen(DesignTokens.White(150), Math.Max(1.0f, this.LayerScale)))
            {
                g.DrawLine(tick, track.Left, track.Top - S(2), track.Left, track.Bottom + S(2));
            }

            if (refresh.MarkerVisible)
            {
                DrawRefreshDot(g, track, refresh.MarkerFraction, DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245), S(3));
            }

            DrawRefreshDot(g, track, refresh.CurrentFraction, DesignTokens.White(235), Math.Max(3.0f, S(3.6f)));
        }

        if (phaseWidth > 0)
        {
            using (SolidBrush phaseBrush = new SolidBrush(status))
            using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.None))
            {
                g.DrawString(phase, smallFont, phaseBrush, new RectangleF(bounds.Right - phaseWidth, bounds.Top, phaseWidth, bounds.Height), far);
            }
        }
    }

    private void DrawRefreshSegment(Graphics g, Rectangle track, float startFraction, float widthFraction, Color color)
    {
        float start = Math.Max(0.0f, Math.Min(1.0f, startFraction));
        float width = Math.Max(0.0f, Math.Min(1.0f, widthFraction));
        using (SolidBrush brush = new SolidBrush(color))
        {
            float firstWidth = Math.Min(width, 1.0f - start);
            if (firstWidth > 0.0f)
            {
                g.FillRectangle(brush, track.Left + start * track.Width, track.Top, firstWidth * track.Width, track.Height);
            }

            // The active arc can wrap past the boundary (marker just before, "now" just after); the
            // linear track shows the wrapped remainder from the left edge.
            float wrap = start + width - 1.0f;
            if (wrap > 0.0f)
            {
                g.FillRectangle(brush, track.Left, track.Top, wrap * track.Width, track.Height);
            }
        }
    }

    private void DrawRefreshDot(Graphics g, Rectangle track, float fraction, Color color, float diameter)
    {
        float f = Math.Max(0.0f, Math.Min(1.0f, fraction));
        float cx = track.Left + f * track.Width;
        float cy = track.Top + track.Height / 2.0f;
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.FillEllipse(brush, cx - diameter / 2.0f, cy - diameter / 2.0f, diameter, diameter);
        }
    }

    private void DrawServiceLeds(Graphics g, Rectangle bounds, Font smallFont)
    {
        List<RadarServiceHealthEntry> services = this.snapshot.Services;
        if (services == null || services.Count == 0 || bounds.Width <= 2)
        {
            return;
        }

        float cellWidth = bounds.Width / (float)services.Count;
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            for (int i = 0; i < services.Count; i++)
            {
                RadarServiceHealthEntry service = services[i];
                if (service == null)
                {
                    continue;
                }

                float cellLeft = bounds.Left + i * cellWidth;
                float dot = Math.Max(4.0f, S(6));
                float dotY = bounds.Top + bounds.Height / 2.0f - dot / 2.0f;
                Color color = service.Color.A > 0 ? service.Color : DesignTokens.Colors.GlyphMuted;
                using (SolidBrush dotBrush = new SolidBrush(service.Checking ? DesignTokens.WithAlpha(color, 130) : color))
                {
                    g.FillEllipse(dotBrush, cellLeft, dotY, dot, dot);
                }

                using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
                {
                    g.DrawString(
                        service.Label,
                        smallFont,
                        textBrush,
                        new RectangleF(cellLeft + dot + S(3), bounds.Top, Math.Max(1, cellWidth - dot - S(4)), bounds.Height),
                        near);
                }
            }
        }
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font titleFont, Font bodyBold, Font smallFont)
    {
        using (SolidBrush accent = new SolidBrush(DesignTokens.Colors.Accent))
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            float iconWidth = g.MeasureString("◆", titleFont).Width;
            g.DrawString("◆", titleFont, accent, new RectangleF(bounds.Left, bounds.Top, iconWidth + S(3), bounds.Height), near);
            float titleLeft = bounds.Left + iconWidth + S(2);
            float titleWidth = g.MeasureString("Model IQ 看板", titleFont).Width + S(8);
            g.DrawString("Model IQ 看板", titleFont, text, new RectangleF(titleLeft, bounds.Top, titleWidth, bounds.Height), near);

            int closeSize = Math.Max(S(17), bounds.Height - S(2));
            this.closeHitBounds = new Rectangle(bounds.Right - closeSize, bounds.Top, closeSize, closeSize);
            using (SolidBrush closeBack = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.DangerClose, 36)))
            using (Pen closePen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.DangerClose, 210), Math.Max(1.0f, this.LayerScale)))
            {
                g.FillEllipse(closeBack, this.closeHitBounds);
                float inset = closeSize * 0.32f;
                g.DrawLine(closePen, this.closeHitBounds.Left + inset, this.closeHitBounds.Top + inset, this.closeHitBounds.Right - inset, this.closeHitBounds.Bottom - inset);
                g.DrawLine(closePen, this.closeHitBounds.Right - inset, this.closeHitBounds.Top + inset, this.closeHitBounds.Left + inset, this.closeHitBounds.Bottom - inset);
            }

            Rectangle meta = new Rectangle(
                (int)Math.Round(titleLeft + titleWidth),
                bounds.Top,
                Math.Max(0, this.closeHitBounds.Left - S(7) - (int)Math.Round(titleLeft + titleWidth)),
                bounds.Height);
            string updated = this.snapshot.UpdatedKnown
                ? this.snapshot.UpdatedLocal.ToString("MM.dd HH:mm", CultureInfo.InvariantCulture)
                : "等待 Radar 数据";
            if (this.snapshot.SourceStale)
            {
                updated += " · " + (string.IsNullOrWhiteSpace(this.snapshot.SourceStatus)
                    ? "缓存数据"
                    : this.snapshot.SourceStatus);
            }
            g.DrawString(updated + "   SOL · TERRA · LUNA", smallFont, muted, meta, far);
        }
    }

    private void DrawScatter(Graphics g, Rectangle bounds, Font bodyBold, Font smallFont, Font monoFont)
    {
        using (SolidBrush panel = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 138)))
        using (Pen panelBorder = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 100), Math.Max(1.0f, this.LayerScale)))
        {
            using (GraphicsPath path = RoundedRectangle(bounds, Math.Max(2, S(6))))
            {
                g.FillPath(panel, path);
                g.DrawPath(panelBorder, path);
            }
        }

        int pad = S(9);
        int titleHeight = MeasureLineHeight(g, bodyBold, S(3));
        Rectangle title = new Rectangle(bounds.Left + pad, bounds.Top + S(2), bounds.Width - pad * 2, titleHeight);
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            g.DrawString("IQ × 单任务费用", bodyBold, text, title, near);
            g.DrawString("圆点大小 = 有效题数", smallFont, muted, title, far);
        }

        Rectangle plot = new Rectangle(
            bounds.Left + S(38),
            title.Bottom + S(7),
            Math.Max(1, bounds.Width - S(53)),
            Math.Max(1, bounds.Bottom - S(32) - title.Bottom));
        DrawScatterGrid(g, plot, smallFont, monoFont);

        if (this.snapshot.Models.Count == 0)
        {
            using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat center = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
            {
                center.LineAlignment = StringAlignment.Center;
                g.DrawString("正在等待 codexradar.com/current.json 的模型比较数据", smallFont, muted, plot, center);
            }

            return;
        }

        double maxCost = 1.0;
        double minIq = double.MaxValue;
        double maxIq = double.MinValue;
        List<RectangleF> usedLabels = new List<RectangleF>();
        for (int i = 0; i < this.snapshot.Models.Count; i++)
        {
            CodexIqBoardModelPoint point = this.snapshot.Models[i];
            if (point == null)
            {
                continue;
            }

            maxCost = Math.Max(maxCost, point.AverageCostUsd);
            minIq = Math.Min(minIq, point.Iq);
            maxIq = Math.Max(maxIq, point.Iq);
        }

        if (minIq == double.MaxValue)
        {
            minIq = 0.0;
            maxIq = 120.0;
        }

        minIq = Math.Max(0.0, Math.Floor((minIq - 5.0) / 10.0) * 10.0);
        maxIq = Math.Max(minIq + 20.0, Math.Ceiling((maxIq + 5.0) / 10.0) * 10.0);
        maxCost *= 1.12;

        for (int i = 0; i < this.snapshot.Models.Count; i++)
        {
            CodexIqBoardModelPoint point = this.snapshot.Models[i];
            if (point == null)
            {
                continue;
            }

            float x = plot.Left + (float)(Math.Max(0.0, point.AverageCostUsd) / maxCost * plot.Width);
            float y = plot.Bottom - (float)((point.Iq - minIq) / Math.Max(1.0, maxIq - minIq) * plot.Height);
            float radius = ResolvePointRadius(point.ValidTasks, this.LayerScale);
            Color color = ResolveFamilyColor(point.Family);
            bool hollow = string.Equals(point.Family, "Legacy", StringComparison.OrdinalIgnoreCase);
            RectangleF dot = new RectangleF(x - radius, y - radius, radius * 2.0f, radius * 2.0f);
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, hollow ? 24 : 190)))
            using (Pen outline = new Pen(DesignTokens.WithAlpha(color, point.Current ? 255 : 220), point.Current ? Math.Max(2.0f, this.LayerScale * 1.4f) : Math.Max(1.0f, this.LayerScale)))
            {
                if (!hollow)
                {
                    g.FillEllipse(fill, dot);
                }

                g.DrawEllipse(outline, dot);
            }

            string label = BuildPointLabel(point);
            RectangleF labelRect = new RectangleF(
                Math.Min(plot.Right - S(54), x + radius + S(2)),
                Math.Max(plot.Top, y - S(8)),
                S(56),
                S(17));
            labelRect = ResolveScatterLabelBounds(labelRect, usedLabels, plot, S(14));
            usedLabels.Add(labelRect);
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 238)))
            using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
            {
                g.DrawString(label, smallFont, brush, labelRect, near);
            }
        }

        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat center = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.DrawString("平均费用 / task (USD)", smallFont, muted, new RectangleF(plot.Left, plot.Bottom + S(4), plot.Width, S(18)), center);
        }
    }

    private static RectangleF ResolveScatterLabelBounds(
        RectangleF candidate,
        IList<RectangleF> used,
        Rectangle plot,
        int step)
    {
        RectangleF result = candidate;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            bool intersects = false;
            for (int i = 0; used != null && i < used.Count; i++)
            {
                if (used[i].IntersectsWith(result))
                {
                    intersects = true;
                    break;
                }
            }

            if (!intersects)
            {
                return result;
            }

            float direction = attempt % 2 == 0 ? -1.0f : 1.0f;
            float multiplier = attempt / 2 + 1;
            result.Y = candidate.Y + direction * step * multiplier;
            result.Y = Math.Max(plot.Top, Math.Min(result.Y, plot.Bottom - result.Height));
        }

        return result;
    }

    private void DrawScatterGrid(Graphics g, Rectangle plot, Font smallFont, Font monoFont)
    {
        using (Pen grid = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 82), Math.Max(1.0f, this.LayerScale)))
        using (Pen axis = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 128), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.None))
        {
            grid.DashStyle = DashStyle.Dot;
            for (int i = 0; i <= 4; i++)
            {
                float y = plot.Top + plot.Height * i / 4.0f;
                g.DrawLine(i == 4 ? axis : grid, plot.Left, y, plot.Right, y);
            }

            for (int i = 0; i <= 4; i++)
            {
                float x = plot.Left + plot.Width * i / 4.0f;
                g.DrawLine(i == 0 ? axis : grid, x, plot.Top, x, plot.Bottom);
            }

            g.DrawString("IQ", monoFont, muted, new RectangleF(plot.Left - S(34), plot.Top, S(30), S(18)), far);
        }
    }

    private void DrawInformationRail(
        Graphics g,
        Rectangle bounds,
        Font bodyBold,
        Font bodyFont,
        Font smallBold,
        Font smallFont,
        Font monoFont,
        Font leaderFont)
    {
        CodexIqBoardModelPoint leader = ResolveLeader(this.snapshot.Models);
        int gap = S(6);
        int leaderHeight = Math.Max(S(76), (int)Math.Round(bounds.Height * 0.27));
        int rosterHeight = Math.Max(S(47), (int)Math.Round(bounds.Height * 0.19));
        int curvesHeight = Math.Max(1, bounds.Height - leaderHeight - rosterHeight - gap * 2);

        Rectangle leaderBounds = new Rectangle(bounds.Left, bounds.Top, bounds.Width, leaderHeight);
        Rectangle curves = new Rectangle(bounds.Left, leaderBounds.Bottom + gap, bounds.Width, curvesHeight);
        Rectangle roster = new Rectangle(bounds.Left, curves.Bottom + gap, bounds.Width, Math.Max(1, bounds.Bottom - curves.Bottom - gap));
        DrawLeaderCard(g, leaderBounds, leader, bodyBold, smallFont, monoFont, leaderFont);
        DrawTrendStack(g, curves, smallBold, smallFont, monoFont);
        DrawRoster(g, roster, smallBold, smallFont, monoFont);
    }

    private void DrawLeaderCard(
        Graphics g,
        Rectangle bounds,
        CodexIqBoardModelPoint leader,
        Font bodyBold,
        Font smallFont,
        Font monoFont,
        Font leaderFont)
    {
        DrawPanelBackground(g, bounds, DesignTokens.Colors.Accent);
        int pad = S(8);
        if (leader == null)
        {
            using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat center = CreateFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
            {
                center.LineAlignment = StringAlignment.Center;
                g.DrawString("LEADER 等待数据", bodyBold, muted, bounds, center);
            }

            return;
        }

        Color family = ResolveFamilyColor(leader.Family);
        using (SolidBrush familyBrush = new SolidBrush(family))
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            Rectangle labelRow = new Rectangle(bounds.Left + pad, bounds.Top + S(4), bounds.Width - pad * 2, MeasureLineHeight(g, smallFont, S(2)));
            g.DrawString("LEADER · " + BuildPointLabel(leader), bodyBold, familyBrush, labelRow, near);

            Rectangle iqRect = new Rectangle(bounds.Left + pad, labelRow.Bottom, Math.Max(S(66), bounds.Width / 3), Math.Max(S(34), bounds.Height - labelRow.Height - S(10)));
            g.DrawString(leader.Iq.ToString("0.0", CultureInfo.InvariantCulture), leaderFont, text, iqRect, near);

            Rectangle details = new Rectangle(iqRect.Right + S(4), iqRect.Top, Math.Max(1, bounds.Right - pad - iqRect.Right - S(4)), iqRect.Height);
            string pass = leader.ValidTasks > 0.0
                ? leader.Passed.ToString("0", CultureInfo.InvariantCulture) + "/" + leader.ValidTasks.ToString("0", CultureInfo.InvariantCulture)
                : "—";
            string detail = "$" + leader.AverageCostUsd.ToString("0.0", CultureInfo.InvariantCulture) + " / task\n" +
                FormatDuration(leader.AverageTaskSeconds) + "  ·  " + pass;
            g.DrawString(detail, smallFont, muted, details, far);
        }
    }

    private void DrawTrendStack(Graphics g, Rectangle bounds, Font smallBold, Font smallFont, Font monoFont)
    {
        int gap = S(3);
        int rowHeight = Math.Max(1, (bounds.Height - gap * 3) / 4);
        DrawMiniCurve(g, new Rectangle(bounds.Left, bounds.Top, bounds.Width, rowHeight), "额度", this.snapshot.WeeklyQuotaRemaining, "%", DesignTokens.Colors.AccentAction, smallBold, smallFont, monoFont);

        List<double> time = new List<double>();
        List<double> efficiency = new List<double>();
        List<double> tokens = new List<double>();
        for (int i = 0; i < this.snapshot.Trends.Count; i++)
        {
            CodexIqBoardTrendPoint point = this.snapshot.Trends[i];
            if (point == null)
            {
                continue;
            }

            if (point.AverageTaskSeconds > 0.0)
            {
                time.Add(point.AverageTaskSeconds / 60.0);
            }

            if (point.EfficiencyKnown)
            {
                efficiency.Add(point.TokenEfficiencyPercent);
            }

            if (point.TotalTokens > 0.0)
            {
                tokens.Add(point.TotalTokens / 1000000.0);
            }
        }

        int y = bounds.Top + rowHeight + gap;
        DrawMiniCurve(g, new Rectangle(bounds.Left, y, bounds.Width, rowHeight), "耗时", time, "m", DesignTokens.Colors.WarningDeep, smallBold, smallFont, monoFont);
        y += rowHeight + gap;
        DrawMiniCurve(g, new Rectangle(bounds.Left, y, bounds.Width, rowHeight), "Token 效率", efficiency, "%", DesignTokens.Colors.SuccessSoft, smallBold, smallFont, monoFont);
        y += rowHeight + gap;
        DrawMiniCurve(g, new Rectangle(bounds.Left, y, bounds.Width, Math.Max(1, bounds.Bottom - y)), "总 Token", tokens, "M", DesignTokens.Colors.AccentAlt, smallBold, smallFont, monoFont);
    }

    private void DrawMiniCurve(
        Graphics g,
        Rectangle bounds,
        string label,
        IList<double> values,
        string suffix,
        Color color,
        Font smallBold,
        Font smallFont,
        Font monoFont)
    {
        DrawPanelBackground(g, bounds, color);
        int pad = S(5);
        int labelWidth = Math.Min(S(58), Math.Max(S(42), bounds.Width / 4));
        Rectangle labelRect = new Rectangle(bounds.Left + pad, bounds.Top, labelWidth, bounds.Height);
        Rectangle valueRect = new Rectangle(bounds.Right - S(47), bounds.Top, S(42), bounds.Height);
        Rectangle plot = new Rectangle(labelRect.Right + S(2), bounds.Top + S(5), Math.Max(1, valueRect.Left - S(3) - labelRect.Right), Math.Max(1, bounds.Height - S(10)));
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (SolidBrush valueBrush = new SolidBrush(color))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.None))
        {
            g.DrawString(label, smallBold, text, labelRect, near);
            string value = values != null && values.Count > 0
                ? values[values.Count - 1].ToString(values[values.Count - 1] >= 100.0 ? "0" : "0.0", CultureInfo.InvariantCulture) + suffix
                : "—";
            g.DrawString(value, monoFont, valueBrush, valueRect, far);
        }

        DrawSparkline(g, plot, values, color, suffix == "%");
    }

    private void DrawRoster(Graphics g, Rectangle bounds, Font smallBold, Font smallFont, Font monoFont)
    {
        DrawPanelBackground(g, bounds, DesignTokens.Colors.GlyphMuted);
        int active = 0;
        int intermittent = 0;
        int retired = 0;
        string issue = string.Empty;
        for (int i = 0; i < this.snapshot.Roster.Count; i++)
        {
            CodexIqBoardRosterEntry entry = this.snapshot.Roster[i];
            if (entry == null)
            {
                continue;
            }

            if (entry.State == CodexIqBoardRosterState.Active)
            {
                active++;
            }
            else if (entry.State == CodexIqBoardRosterState.Intermittent)
            {
                intermittent++;
                if (issue.Length == 0)
                {
                    issue = "间歇 · " + CompactRosterLabel(entry.Label);
                }
            }
            else
            {
                retired++;
                if (issue.Length == 0)
                {
                    issue = "退役 · " + CompactRosterLabel(entry.Label);
                }
            }
        }

        int pad = S(6);
        int row = Math.Max(1, bounds.Height / 2);
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (SolidBrush activeBrush = new SolidBrush(DesignTokens.Colors.Success))
        using (SolidBrush warn = new SolidBrush(DesignTokens.Colors.WarningDeep))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            g.DrawString("MODEL ROSTER", smallBold, text, new Rectangle(bounds.Left + pad, bounds.Top, bounds.Width / 2, row), near);
            string counts = active.ToString(CultureInfo.InvariantCulture) + " 活跃  ·  " +
                intermittent.ToString(CultureInfo.InvariantCulture) + " 间歇  ·  " +
                retired.ToString(CultureInfo.InvariantCulture) + " 退役";
            g.DrawString(counts, monoFont, activeBrush, new Rectangle(bounds.Left + bounds.Width / 3, bounds.Top, bounds.Width - bounds.Width / 3 - pad, row), far);
            g.DrawString(issue.Length > 0 ? issue : "当前目录无失联模型", smallFont, issue.Length > 0 ? warn : text,
                new Rectangle(bounds.Left + pad, bounds.Top + row, bounds.Width - pad * 2, Math.Max(1, bounds.Height - row)), near);
        }
    }

    private void DrawFooter(Graphics g, Rectangle bounds, Font smallFont, Font monoFont)
    {
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush accent = new SolidBrush(DesignTokens.Colors.Accent))
        using (StringFormat near = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            string selected = string.IsNullOrEmpty(this.snapshot.SelectedModelLabel)
                ? "当前模型未识别"
                : this.snapshot.SelectedModelLabel;
            g.DrawString("选中 · " + selected, smallFont, accent, new RectangleF(bounds.Left, bounds.Top, bounds.Width * 0.62f, bounds.Height), near);
            g.DrawString(CodexRadarAttributionText, smallFont, muted, new RectangleF(bounds.Left + bounds.Width * 0.48f, bounds.Top, bounds.Width * 0.52f, bounds.Height), far);
        }
    }

    private static void DrawPanelBackground(Graphics g, Rectangle bounds, Color accent)
    {
        using (GraphicsPath path = RoundedRectangle(bounds, Math.Max(2.0f, bounds.Height * 0.10f)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 126)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 62), 1.0f))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
    }

    private static void DrawSparkline(Graphics g, Rectangle bounds, IList<double> values, Color color)
    {
        DrawSparkline(g, bounds, values, color, false);
    }

    // A bare auto-scaled polyline stretches its own min..max across the full plot height, so the top
    // edge is the data peak, not any fixed level - "where is 100%?" has no answer. Every plot now gets
    // a frame so the value axis is bounded, and percentage series fold 100 into the drawn range and
    // mark it with a dashed reference line, so full scale is always locatable while the curve keeps
    // its own shape.
    private static void DrawSparkline(Graphics g, Rectangle bounds, IList<double> values, Color color, bool markHundredPercent)
    {
        DrawSparklineFrame(g, bounds);

        if (values == null || values.Count == 0 || bounds.Width <= 1 || bounds.Height <= 1)
        {
            using (Pen empty = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 80), 1.0f))
            {
                empty.DashStyle = DashStyle.Dot;
                g.DrawLine(empty, bounds.Left, bounds.Top + bounds.Height / 2.0f, bounds.Right, bounds.Top + bounds.Height / 2.0f);
            }

            return;
        }

        double min = values[0];
        double max = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            min = Math.Min(min, values[i]);
            max = Math.Max(max, values[i]);
        }

        if (markHundredPercent)
        {
            min = Math.Min(min, 100.0);
            max = Math.Max(max, 100.0);
        }

        if (Math.Abs(max - min) < 0.0001)
        {
            max = min + 1.0;
        }

        if (markHundredPercent && 100.0 >= min && 100.0 <= max)
        {
            float refY = bounds.Bottom - (float)((100.0 - min) / (max - min) * bounds.Height);
            using (Pen refPen = new Pen(DesignTokens.WithAlpha(color, 120), 1.0f))
            {
                refPen.DashStyle = DashStyle.Dash;
                g.DrawLine(refPen, bounds.Left, refY, bounds.Right, refY);
            }

            float tagSize = Math.Max(6.0f, bounds.Height * 0.32f);
            using (Font tag = new Font("Segoe UI", tagSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush tagBrush = new SolidBrush(DesignTokens.WithAlpha(color, 165)))
            using (StringFormat fmt = new StringFormat(StringFormatFlags.NoWrap))
            {
                fmt.Alignment = StringAlignment.Near;
                fmt.LineAlignment = StringAlignment.Center;
                g.DrawString("100", tag, tagBrush, new RectangleF(bounds.Left + 1.0f, refY - tagSize, tagSize * 2.6f, tagSize), fmt);
            }
        }

        PointF[] points = new PointF[Math.Max(2, values.Count)];
        for (int i = 0; i < points.Length; i++)
        {
            int source = values.Count == 1 ? 0 : i;
            float x = points.Length == 1 ? bounds.Left : bounds.Left + bounds.Width * i / (float)(points.Length - 1);
            float y = bounds.Bottom - (float)((values[source] - min) / (max - min) * bounds.Height);
            points[i] = new PointF(x, y);
        }

        using (Pen line = new Pen(DesignTokens.WithAlpha(color, 220), 1.6f))
        {
            line.LineJoin = LineJoin.Round;
            g.DrawLines(line, points);
        }
    }

    private static void DrawSparklineFrame(Graphics g, Rectangle bounds)
    {
        using (Pen frame = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 130), 1.0f))
        {
            g.DrawRectangle(frame, bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }
    }

    private static CodexIqBoardModelPoint ResolveLeader(IList<CodexIqBoardModelPoint> models)
    {
        CodexIqBoardModelPoint leader = null;
        for (int i = 0; models != null && i < models.Count; i++)
        {
            CodexIqBoardModelPoint point = models[i];
            if (point != null && (leader == null || point.Iq > leader.Iq))
            {
                leader = point;
            }
        }

        return leader;
    }

    private static Color ResolveFamilyColor(string family)
    {
        if (string.Equals(family, "Sol", StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.Warning;
        }

        if (string.Equals(family, "Terra", StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.Success;
        }

        if (string.Equals(family, "Luna", StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.AccentAlt;
        }

        return DesignTokens.Colors.GlyphMuted;
    }

    private static float ResolvePointRadius(double validTasks, float scale)
    {
        double normalized = validTasks <= 0.0 ? 0.0 : Math.Sqrt(Math.Min(160.0, validTasks) / 160.0);
        return (float)((3.2 + normalized * 3.8) * Math.Max(0.1f, scale));
    }

    private static string BuildPointLabel(CodexIqBoardModelPoint point)
    {
        if (point == null)
        {
            return string.Empty;
        }

        string family = string.IsNullOrEmpty(point.Family) ? "Model" : point.Family;
        string effort = string.IsNullOrEmpty(point.Effort) ? string.Empty : " " + point.Effort;
        return family + effort;
    }

    private static string CompactRosterLabel(string label)
    {
        string value = (label ?? string.Empty).Replace("GPT-", string.Empty).Trim();
        return value.Length <= 22 ? value : value.Substring(0, 21) + "…";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0.0)
        {
            return "—";
        }

        if (seconds >= 3600.0)
        {
            return (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + "h";
        }

        return Math.Max(1.0, seconds / 60.0).ToString("0", CultureInfo.InvariantCulture) + "m";
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

    internal static void RunSelfTest()
    {
        List<CodexIqBoardModelPoint> models = new List<CodexIqBoardModelPoint>
        {
            new CodexIqBoardModelPoint { Key = "sol", Family = "Sol", Iq = 95.0, ValidTasks = 100 },
            new CodexIqBoardModelPoint { Key = "terra", Family = "Terra", Iq = 111.0, ValidTasks = 112 },
            new CodexIqBoardModelPoint { Key = "luna", Family = "Luna", Iq = 84.0, ValidTasks = 80 }
        };
        if (ResolveLeader(models) != models[1] ||
            ResolveFamilyColor("Sol") == ResolveFamilyColor("Terra") ||
            ResolveFamilyColor("Terra") == ResolveFamilyColor("Luna") ||
            ResolvePointRadius(112, 1.0f) <= ResolvePointRadius(20, 1.0f))
        {
            throw new InvalidOperationException("Codex IQ board model encoding self-test failed.");
        }

        double[] flat = { 50.0 };
        using (Bitmap bitmap = new Bitmap(64, 24))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            DrawSparkline(g, new Rectangle(1, 1, 60, 20), flat, DesignTokens.Colors.Accent);
        }

        CodexIqBoardSnapshot signatureSample = CodexIqBoardSnapshot.CreateEmpty();
        signatureSample.SelectedModelKey = "sol";
        signatureSample.SelectedModelLabel = "SOL";
        signatureSample.Trends.Add(new CodexIqBoardTrendPoint
        {
            DateLocal = new DateTime(2026, 7, 22),
            AverageTaskSeconds = 12.0,
            TokenEfficiencyPercent = 81.0,
            TotalTokens = 2400.0,
            EfficiencyKnown = true
        });
        signatureSample.WeeklyQuotaRemaining.Add(64.0);
        signatureSample.Services.Add(new RadarServiceHealthEntry
        {
            Label = "Radar",
            Color = DesignTokens.Colors.Success,
            Checking = false
        });
        string baselineSignature = BuildSnapshotSignature(signatureSample);
        for (int i = 0; i < 20; i++)
        {
            if (!string.Equals(baselineSignature, BuildSnapshotSignature(signatureSample.Clone()), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex IQ board signature must be stable for identical snapshots.");
            }
        }

        CodexIqBoardSnapshot changed = signatureSample.Clone();
        changed.Trends[0].TotalTokens = 2401.0;
        if (string.Equals(baselineSignature, BuildSnapshotSignature(changed), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex IQ board signature must include trend values.");
        }

        changed = signatureSample.Clone();
        changed.WeeklyQuotaRemaining[0] = 63.0;
        if (string.Equals(baselineSignature, BuildSnapshotSignature(changed), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex IQ board signature must include weekly quota values.");
        }

        changed = signatureSample.Clone();
        changed.Services[0].Checking = true;
        if (string.Equals(baselineSignature, BuildSnapshotSignature(changed), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex IQ board signature must include service state.");
        }

        changed = signatureSample.Clone();
        changed.SourceStale = true;
        changed.SourceStatus = "缓存数据";
        if (string.Equals(baselineSignature, BuildSnapshotSignature(changed), StringComparison.Ordinal) ||
            CodexRadarAttributionText.IndexOf("codexradar.com", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Codex IQ board signature or attribution self-test failed.");
        }
    }
}
