using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

internal sealed partial class SystemDayBoardForm
{
    private const int MetricRowCount = 8;
    private const float SummaryTitleFontPixels = 8.8f;
    private const float SummaryDetailFontPixels = 8.8f;
    private const float SummaryNameFontPixels = 9.2f;
    private const float SummaryValueFontPixels = 11.0f;

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
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0.5f, 0.5f, this.Width - 1, this.Height - 1), Math.Max(3, S(10))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 116), Math.Max(1.0f, this.LayerScale)))
            g.DrawPath(border, shell);

        Font titleFont = this.fontCache.GetUi(S(13.0f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(8.2f), FontStyle.Regular);
        Font smallFont = this.fontCache.GetUi(S(7.3f), FontStyle.Regular);
        Font labelFont = this.fontCache.GetMono(S(8.0f), FontStyle.Bold);
        Font valueFont = this.fontCache.GetMono(S(8.0f), FontStyle.Bold);
        Font summaryTitleFont = this.fontCache.GetUi(S(SummaryTitleFontPixels), FontStyle.Bold);
        Font summaryDetailFont = this.fontCache.GetUi(S(SummaryDetailFontPixels), FontStyle.Regular);
        Font summaryNameFont = this.fontCache.GetUi(S(SummaryNameFontPixels), FontStyle.Bold);
        Font summaryValueFont = this.fontCache.GetMono(S(SummaryValueFontPixels), FontStyle.Bold);

        int pad = S(11);
        Rectangle content = new Rectangle(pad, S(8), Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - S(16)));
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, S(31));
        Rectangle summary = new Rectangle(content.Left, header.Bottom + S(2), content.Width, S(49));
        Rectangle chart = new Rectangle(content.Left, summary.Bottom + S(5), content.Width, S(250));
        Rectangle footer = new Rectangle(content.Left, chart.Bottom + S(3), content.Width, Math.Max(S(26), content.Bottom - chart.Bottom - S(3)));
        DrawHeader(g, header, titleFont, bodyFont, valueFont);
        DrawSummary(g, summary, summaryTitleFont, summaryDetailFont, summaryNameFont, summaryValueFont);
        DrawUnifiedChart(g, chart, smallFont, labelFont, valueFont);
        DrawFooter(g, footer, bodyFont, smallFont);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.SystemDay, this.LayerScale);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font titleFont, Font bodyFont, Font monoFont)
    {
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay);
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush accentBrush = new SolidBrush(accent))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("SYSTEM DAY", titleFont, title, new Rectangle(bounds.Left, bounds.Top, S(130), S(20)), near);
            g.DrawString("工作 · 负载 · 电量 · 热区", bodyFont, accentBrush,
                new Rectangle(bounds.Left + S(132), bounds.Top + S(3), S(190), S(17)), near);
            string updated = this.snapshot.UpdatedLocal == DateTime.MinValue
                ? "--:--"
                : this.snapshot.UpdatedLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
            g.DrawString(updated, monoFont, muted, new Rectangle(bounds.Right - S(104), bounds.Top + S(2), S(84), S(18)), far);
        }
        DrawLegend(g, new Rectangle(bounds.Left, bounds.Bottom - S(12), S(210), S(12)), bodyFont);
    }

    private void DrawLegend(Graphics g, Rectangle bounds, Font font)
    {
        DrawLegendItem(g, new Rectangle(bounds.Left, bounds.Top, S(58), bounds.Height), "工作", DesignTokens.Colors.Success, font);
        DrawLegendItem(g, new Rectangle(bounds.Left + S(62), bounds.Top, S(58), bounds.Height), "空闲", DesignTokens.Colors.Warning, font);
        DrawLegendItem(g, new Rectangle(bounds.Left + S(124), bounds.Top, S(58), bounds.Height), "睡眠", DesignTokens.Colors.AccentAlt, font);
    }

    private void DrawLegendItem(Graphics g, Rectangle bounds, string text, Color color, Font font)
    {
        using (SolidBrush dot = new SolidBrush(color))
        using (SolidBrush label = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.FillEllipse(dot, bounds.Left, bounds.Top + S(3), S(5), S(5));
            g.DrawString(text, font, label, new Rectangle(bounds.Left + S(9), bounds.Top, bounds.Width - S(9), bounds.Height), near);
        }
    }

    private void DrawSummary(
        Graphics g,
        Rectangle bounds,
        Font titleFont,
        Font detailFont,
        Font nameFont,
        Font valueFont)
    {
        int gap = S(4);
        int durationWidth = S(62);
        int powerWidth = Math.Max(S(150), bounds.Width - durationWidth * 4 - gap * 5 - S(142));
        int x = bounds.Left;
        DrawSummaryCard(g, new Rectangle(x, bounds.Top, durationWidth, bounds.Height), "记录", FormatDuration(this.snapshot.RecordedMinutes), DesignTokens.Colors.TextMuted, titleFont, valueFont); x += durationWidth + gap;
        DrawSummaryCard(g, new Rectangle(x, bounds.Top, durationWidth, bounds.Height), "工作", FormatDuration(this.snapshot.ActiveMinutes), DesignTokens.Colors.Success, titleFont, valueFont); x += durationWidth + gap;
        DrawSummaryCard(g, new Rectangle(x, bounds.Top, durationWidth, bounds.Height), "空闲", FormatDuration(this.snapshot.IdleMinutes), DesignTokens.Colors.Warning, titleFont, valueFont); x += durationWidth + gap;
        DrawSummaryCard(g, new Rectangle(x, bounds.Top, durationWidth, bounds.Height), "睡眠", FormatDuration(this.snapshot.SleepMinutes), DesignTokens.Colors.AccentAlt, titleFont, valueFont); x += durationWidth + gap;
        DrawPowerSummary(g, new Rectangle(x, bounds.Top, powerWidth, bounds.Height), titleFont, detailFont, valueFont); x += powerWidth + gap;
        DrawThermalSummary(g, new Rectangle(x, bounds.Top, Math.Max(1, bounds.Right - x), bounds.Height), titleFont, nameFont, valueFont);
    }

    private void DrawSummaryCard(Graphics g, Rectangle bounds, string titleText, string valueText, Color color, Font titleFont, Font valueFont)
    {
        DrawPanel(g, bounds, color);
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush value = new SolidBrush(color))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            g.DrawString(titleText, titleFont, title, new Rectangle(bounds.Left, bounds.Top + S(3), bounds.Width, S(16)), center);
            g.DrawString(valueText, valueFont, value, new Rectangle(bounds.Left, bounds.Top + S(19), bounds.Width, S(25)), center);
        }
    }

    private void DrawPowerSummary(Graphics g, Rectangle bounds, Font titleFont, Font detailFont, Font valueFont)
    {
        DrawPanel(g, bounds, DesignTokens.Colors.Danger);
        string battery = this.snapshot.CurrentBatteryKnown
            ? this.snapshot.CurrentBatteryPercent.ToString(CultureInfo.InvariantCulture) + "%"
            : "--%";
        string watts = this.snapshot.CurrentWattsKnown
            ? this.snapshot.CurrentWatts.ToString("0.0", CultureInfo.InvariantCulture) + "W"
            : "--W";
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush batteryBrush = new SolidBrush(this.snapshot.CurrentCharging ? DesignTokens.Colors.Danger : DesignTokens.Colors.Accent))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("电量 / 功耗", titleFont, title, new Rectangle(bounds.Left + S(7), bounds.Top + S(3), bounds.Width - S(14), S(16)), near);
            g.DrawString(battery, valueFont, batteryBrush, new Rectangle(bounds.Left + S(7), bounds.Top + S(18), S(52), S(25)), near);
            g.DrawString(watts, valueFont, muted, new Rectangle(bounds.Right - S(58), bounds.Top + S(18), S(51), S(25)), far);
            g.DrawString(this.snapshot.BatteryEtaText ?? "等待电量趋势", detailFont, muted,
                new Rectangle(bounds.Left + S(62), bounds.Top + S(18), Math.Max(1, bounds.Width - S(124)), S(25)), near);
        }
    }

    private void DrawThermalSummary(Graphics g, Rectangle bounds, Font titleFont, Font nameFont, Font valueFont)
    {
        DrawPanel(g, bounds, DesignTokens.Colors.WarningDeep);
        string temp = this.snapshot.CurrentTemperatureKnown
            ? this.snapshot.CurrentMaxCelsius.ToString("0.0", CultureInfo.InvariantCulture) + "°"
            : "--°";
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush hot = new SolidBrush(ResolveTemperatureColor(this.snapshot.CurrentMaxCelsius)))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            g.DrawString("当前最热区", titleFont, title, new Rectangle(bounds.Left + S(7), bounds.Top + S(3), bounds.Width - S(14), S(16)), near);
            g.DrawString(this.snapshot.CurrentHotZoneName ?? "--", nameFont, muted,
                new Rectangle(bounds.Left + S(7), bounds.Top + S(18), Math.Max(1, bounds.Width - S(65)), S(25)), near);
            g.DrawString(temp, valueFont, hot, new Rectangle(bounds.Right - S(58), bounds.Top + S(18), S(51), S(25)), far);
        }
    }

    private void DrawUnifiedChart(Graphics g, Rectangle bounds, Font smallFont, Font labelFont, Font valueFont)
    {
        DrawPanel(g, bounds, EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay));
        Rectangle plot = new Rectangle(bounds.Left + S(48), bounds.Top + S(8), Math.Max(1, bounds.Width - S(57)), Math.Max(1, bounds.Height - S(31)));
        int rowHeight = Math.Max(S(19), plot.Height / MetricRowCount);
        Rectangle rows = new Rectangle(plot.Left, plot.Top, plot.Width, rowHeight * MetricRowCount);
        Rectangle axis = new Rectangle(plot.Left, rows.Bottom, plot.Width, Math.Max(S(17), bounds.Bottom - rows.Bottom - S(4)));
        DrawTimeTicks(g, rows, axis, smallFont);
        string[] labels = { "状态", "CPU", "GPU", "NPU", "MEM", "NET", "电量", "温度" };
        for (int i = 0; i < MetricRowCount; i++)
        {
            Rectangle row = new Rectangle(rows.Left, rows.Top + rowHeight * i, rows.Width, rowHeight);
            using (SolidBrush alt = new SolidBrush(i % 2 == 0 ? DesignTokens.White(5) : DesignTokens.White(1)))
                g.FillRectangle(alt, row);
            using (Pen divider = new Pen(DesignTokens.White(18), Math.Max(1.0f, this.LayerScale)))
                g.DrawLine(divider, row.Left, row.Bottom - 1, row.Right, row.Bottom - 1);
            using (SolidBrush label = new SolidBrush(GetRowColor(i)))
            using (StringFormat far = CreateFormat(StringAlignment.Far))
                g.DrawString(labels[i], labelFont, label, new Rectangle(bounds.Left + S(3), row.Top, S(39), row.Height), far);
            if (i == 0) DrawStateRow(g, row);
            else DrawMetricRow(g, row, i, smallFont);
        }
    }

    private void DrawTimeTicks(Graphics g, Rectangle rows, Rectangle axis, Font font)
    {
        const int tickCount = 7;
        using (Pen grid = new Pen(DesignTokens.White(22), Math.Max(1.0f, this.LayerScale)))
        using (Pen tick = new Pen(DesignTokens.White(78), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush label = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            for (int i = 0; i < tickCount; i++)
            {
                double ratio = i / (double)(tickCount - 1);
                int x = rows.Left + (int)Math.Round(rows.Width * ratio);
                g.DrawLine(grid, x, rows.Top, x, rows.Bottom);
                g.DrawLine(tick, x, rows.Bottom, x, rows.Bottom + S(4));
                DateTime time = this.snapshot.StartLocal + TimeSpan.FromTicks(
                    (long)((this.snapshot.EndLocal - this.snapshot.StartLocal).Ticks * ratio));
                string text = this.selectedRange == SystemDayRange.LastWeek
                    ? time.ToString("MM/dd", CultureInfo.InvariantCulture)
                    : time.ToString("HH:mm", CultureInfo.InvariantCulture);
                g.DrawString(text, font, label, new Rectangle(x - S(25), axis.Top + S(3), S(50), Math.Max(S(13), axis.Height - S(3))), center);
            }
        }
    }

    private void DrawStateRow(Graphics g, Rectangle row)
    {
        for (int i = 0; i < this.snapshot.WorkSegments.Count; i++)
        {
            SystemDayWorkSegment segment = this.snapshot.WorkSegments[i];
            if (segment == null) continue;
            int left = ResolveTimeX(row, segment.StartLocal);
            int right = ResolveTimeX(row, segment.EndLocal);
            if (right <= left) right = left + Math.Max(1, S(1));
            Color color = segment.State == SystemDayWorkState.Active
                ? DesignTokens.Colors.Success
                : segment.State == SystemDayWorkState.Idle
                    ? DesignTokens.Colors.Warning
                    : DesignTokens.Colors.AccentAlt;
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 184)))
                g.FillRectangle(fill, left, row.Top + S(5), Math.Max(1, right - left), Math.Max(S(5), row.Height - S(10)));
        }
    }

    private void DrawMetricRow(Graphics g, Rectangle row, int rowIndex, Font smallFont)
    {
        SystemDayMetricPeak peak = GetPeakForRow(rowIndex);
        double scaleMax = ResolveScaleMaximum(rowIndex, peak);
        PointF? previous = null;
        DateTime previousTime = DateTime.MinValue;
        for (int i = 0; i < this.snapshot.Points.Count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            if (point == null) continue;
            bool known = rowIndex != 6 || point.BatteryKnown;
            if (rowIndex == 7) known = point.TemperatureKnown;
            if (!known) { previous = null; continue; }
            double value = GetPointValue(point, rowIndex);
            float x = ResolveTimeX(row, point.TimestampLocal);
            float y = ResolveValueY(row, value, rowIndex == 7 ? 20.0 : 0.0, scaleMax);
            PointF current = new PointF(x, y);
            if (previous.HasValue && !IsPlotGap(previousTime, point.TimestampLocal))
            {
                Color color = rowIndex == 6
                    ? ResolveBatteryDirectionColor(point.BatteryDirection)
                    : rowIndex == 7
                        ? ResolveTemperatureColor(value)
                        : GetRowColor(rowIndex);
                using (Pen line = new Pen(color, Math.Max(1.35f, S(1.05f))))
                {
                    line.StartCap = LineCap.Round;
                    line.EndCap = LineCap.Round;
                    g.DrawLine(line, previous.Value, current);
                }
            }
            previous = current;
            previousTime = point.TimestampLocal;
        }
        string peakText = FormatPeak(rowIndex, peak);
        using (SolidBrush text = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 218)))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
            g.DrawString(peakText, smallFont, text, new Rectangle(row.Right - S(150), row.Top + S(1), S(146), row.Height - S(2)), far);
    }

    private bool IsPlotGap(DateTime previous, DateTime current)
    {
        if (previous == DateTime.MinValue || current <= previous) return true;
        double rangeMinutes = Math.Max(1.0, (this.snapshot.EndLocal - this.snapshot.StartLocal).TotalMinutes);
        return (current - previous).TotalMinutes > Math.Max(5.0, rangeMinutes / 24.0);
    }

    internal static Color ResolveBatteryDirectionColor(SystemDayBatteryDirection direction)
    {
        // Product requirement: charge growth is explicitly red. Discharge is cyan so the two
        // directions remain distinguishable even when plotted over the same battery percentage.
        if (direction == SystemDayBatteryDirection.Rising) return DesignTokens.Colors.DangerStrong;
        if (direction == SystemDayBatteryDirection.Falling) return DesignTokens.Colors.Accent;
        return DesignTokens.Colors.GlyphMuted;
    }

    private static Color ResolveTemperatureColor(double celsius)
    {
        if (celsius >= 85.0) return DesignTokens.Colors.DangerStrong;
        if (celsius >= 70.0) return DesignTokens.Colors.WarningDeep;
        return DesignTokens.Colors.Warning;
    }

    private static Color GetRowColor(int rowIndex)
    {
        switch (rowIndex)
        {
            case 1: return DesignTokens.Colors.Accent;
            case 2: return DesignTokens.Colors.AccentSoft;
            case 3: return DesignTokens.Colors.TextMuted;
            case 4: return DesignTokens.Colors.AccentAlt;
            case 5: return DesignTokens.Colors.Success;
            case 6: return DesignTokens.Colors.Danger;
            case 7: return DesignTokens.Colors.Warning;
            default: return DesignTokens.Colors.TextMuted;
        }
    }

    private SystemDayMetricPeak GetPeakForRow(int rowIndex)
    {
        switch (rowIndex)
        {
            case 1: return this.snapshot.FindPeak("cpu");
            case 2: return this.snapshot.FindPeak("gpu");
            case 3: return this.snapshot.FindPeak("npu");
            case 4: return this.snapshot.FindPeak("memory");
            case 5: return this.snapshot.FindPeak("network");
            case 6: return null;
            case 7: return this.snapshot.FindPeak("temperature");
            default: return null;
        }
    }

    private static double ResolveScaleMaximum(int rowIndex, SystemDayMetricPeak peak)
    {
        if (rowIndex >= 1 && rowIndex <= 4) return 100.0;
        if (rowIndex == 6) return 100.0;
        if (rowIndex == 7) return Math.Max(70.0, peak == null ? 70.0 : Math.Ceiling(peak.Value / 10.0) * 10.0);
        return Math.Max(1.0, peak == null ? 1.0 : peak.Value * 1.08);
    }

    private static double GetPointValue(SystemDayBoardPoint point, int rowIndex)
    {
        switch (rowIndex)
        {
            case 1: return point.CpuPercent;
            case 2: return point.GpuPercent;
            case 3: return point.NpuPercent;
            case 4: return point.MemoryPercent;
            case 5: return point.NetworkBytesPerSecond;
            case 6: return point.BatteryPercent;
            case 7: return point.MaxCelsius;
            default: return 0.0;
        }
    }

    private string FormatPeak(int rowIndex, SystemDayMetricPeak peak)
    {
        if (rowIndex == 6)
        {
            if (!this.snapshot.CurrentBatteryKnown) return "--";
            return "当前 " + this.snapshot.CurrentBatteryPercent.ToString(CultureInfo.InvariantCulture) + "%";
        }
        if (peak == null || peak.TimestampLocal == DateTime.MinValue) return "峰 --";
        string value;
        if (rowIndex == 5) value = NetworkRateFormatter.Format(peak.Value);
        else if (rowIndex == 7)
            value = (string.IsNullOrEmpty(peak.ZoneName) ? "TZ" : peak.ZoneName) + " " + peak.Value.ToString("0.0", CultureInfo.InvariantCulture) + "°";
        else value = peak.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
        string time = this.selectedRange == SystemDayRange.LastWeek
            ? peak.TimestampLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
            : peak.TimestampLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        return "峰 " + value + " · " + time;
    }

    private int ResolveTimeX(Rectangle row, DateTime timestampLocal)
    {
        double total = Math.Max(1.0, (this.snapshot.EndLocal - this.snapshot.StartLocal).TotalSeconds);
        double ratio = (timestampLocal - this.snapshot.StartLocal).TotalSeconds / total;
        ratio = Math.Max(0.0, Math.Min(1.0, ratio));
        return row.Left + (int)Math.Round(row.Width * ratio);
    }

    private static float ResolveValueY(Rectangle row, double value, double minimum, double maximum)
    {
        double range = Math.Max(0.001, maximum - minimum);
        double ratio = Math.Max(0.0, Math.Min(1.0, (value - minimum) / range));
        int inset = Math.Max(2, row.Height / 7);
        return row.Bottom - inset - (float)((row.Height - inset * 2) * ratio);
    }

    private void DrawFooter(Graphics g, Rectangle bounds, Font bodyFont, Font smallFont)
    {
        string rangeText = this.selectedRange == SystemDayRange.Today
            ? "今天 ›"
            : this.selectedRange == SystemDayRange.Last24Hours
                ? "24小时 ›"
                : "近一周 ›";
        DrawFooterAction(g, GetRangeActionBounds(), rangeText, DesignTokens.Colors.Success, bodyFont);
        DrawFooterAction(g, GetCloseBounds(), "关闭", DesignTokens.Colors.Danger, bodyFont);
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            string log = "点击切换范围 · 1 分钟采样 · 完整热区 JSONL";
            int logLeft = Math.Min(bounds.Right, GetCloseBounds().Right + S(5));
            g.DrawString(log, smallFont, muted, new Rectangle(logLeft, bounds.Top, Math.Max(0, bounds.Right - logLeft), bounds.Height), far);
        }
    }

    private Rectangle GetCloseBounds()
    {
        Rectangle rangeAction = GetRangeActionBounds();
        return new Rectangle(rangeAction.Right + S(4), rangeAction.Top, S(42), rangeAction.Height);
    }

    private Rectangle GetRangeActionBounds()
    {
        return new Rectangle(S(11), this.Height - S(37), S(76), S(24));
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
        using (GraphicsPath path = RoundedRectangle(bounds, S(6)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 204)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 48), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
    }

    private static string FormatDuration(double minutes)
    {
        int total = Math.Max(0, (int)Math.Round(minutes));
        if (total >= 60) return (total / 60).ToString(CultureInfo.InvariantCulture) + "h" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        return total.ToString(CultureInfo.InvariantCulture) + "m";
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
