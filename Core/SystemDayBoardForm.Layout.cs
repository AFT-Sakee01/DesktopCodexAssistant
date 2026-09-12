using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

internal sealed partial class SystemDayBoardForm
{
    // 统一坐标系：序号 0 是工作状态带（并入同一画框底沿，不再单占一行），1..7 是共享
    // 0–100% 纵轴上的曲线。温度与网络在进入该纵轴前各自归一，换算关系由右副轴和图例交代。
    private const int SeriesCount = 8;
    private const int ValueTickCount = 5;
    private const int TimeTickCount = 7;
    // 平滑态用基数样条连点，采样点之间是弧线而不是折线。张力压到 0.4：默认的 0.5 会让
    // 方波型数据（NPU 在固定占用上的跳变）在台阶处明显过冲，0.4 仍是弧线但收得住。
    private const float SmoothingCurveTension = 0.4f;
    private const double TemperatureAxisMinCelsius = 20.0;
    private const double TemperatureAxisMaxCelsius = 100.0;
    private const float TitleFontPixels = 13.0f;
    private const float StripLabelFontPixels = 8.2f;
    private const float StripValueFontPixels = 8.6f;
    private const float AxisFontPixels = 7.2f;

    private static readonly string[] SeriesLabels = { "状态", "CPU", "GPU", "NPU", "MEM", "NET", "电量", "温度" };
    private static readonly int[] ValueTicks = { 0, 25, 50, 75, 100 };

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

        Font titleFont = this.fontCache.GetUi(S(TitleFontPixels), FontStyle.Bold);
        Font labelFont = this.fontCache.GetUi(S(StripLabelFontPixels), FontStyle.Regular);
        Font valueFont = this.fontCache.GetMono(S(StripValueFontPixels), FontStyle.Bold);
        Font axisFont = this.fontCache.GetMono(S(AxisFontPixels), FontStyle.Regular);

        int pad = S(11);
        int contentWidth = Math.Max(1, this.Width - pad * 2);
        Rectangle plot = new Rectangle(S(46), S(92), Math.Max(1, this.Width - S(46) - S(50)), S(228));

        DrawHeader(g, new Rectangle(pad, S(14), contentWidth, S(20)), titleFont, labelFont, axisFont);
        DrawSummaryStrip(g, new Rectangle(pad, S(40), contentWidth, S(16)), labelFont, valueFont);
        DrawLegendStrip(g, new Rectangle(pad, S(62), contentWidth, S(16)), labelFont, valueFont, axisFont);
        DrawSharedPlot(g, plot, axisFont);
        DrawStateRibbon(g, new Rectangle(plot.Left, plot.Bottom + S(4), plot.Width, S(9)));
        DrawTimeLabels(g, plot, new Rectangle(plot.Left, plot.Bottom + S(16), plot.Width, S(14)), axisFont);
        DrawFooter(g, new Rectangle(pad, this.Height - S(37), contentWidth, S(24)), labelFont, axisFont);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.SystemDay, this.LayerScale);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font titleFont, Font subFont, Font monoFont)
    {
        using (StringFormat inline = CreateInlineFormat())
        using (SolidBrush title = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush accent = new SolidBrush(EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay)))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        {
            int titleWidth = MeasureInline(g, "SYSTEM DAY", titleFont, inline);
            g.DrawString("SYSTEM DAY", titleFont, title, new Rectangle(bounds.Left, bounds.Top, titleWidth, bounds.Height), inline);
            string updated = this.snapshot.UpdatedLocal == DateTime.MinValue
                ? "--:--"
                : this.snapshot.UpdatedLocal.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
            int updatedWidth = MeasureInline(g, updated, monoFont, inline);
            g.DrawString(updated, monoFont, muted, new Rectangle(bounds.Right - updatedWidth, bounds.Top, updatedWidth, bounds.Height), inline);
            int subLeft = bounds.Left + titleWidth + S(10);
            int subWidth = Math.Max(0, bounds.Right - updatedWidth - S(10) - subLeft);
            if (subWidth > S(24))
                g.DrawString("统一 0–100% 坐标系 · 右副轴 °C", subFont, accent, new Rectangle(subLeft, bounds.Top, subWidth, bounds.Height), inline);
        }
    }

    // 摘要折进标题带：时长前的色块同时充当工作/空闲/睡眠的状态图例，看板里不再出现第二份状态说明。
    private void DrawSummaryStrip(Graphics g, Rectangle bounds, Font labelFont, Font valueFont)
    {
        using (StringFormat inline = CreateInlineFormat())
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.TextMuted))
        {
            string temperature = this.snapshot.CurrentTemperatureKnown
                ? this.snapshot.CurrentMaxCelsius.ToString("0.0", CultureInfo.InvariantCulture) + "°"
                : "--°";
            int temperatureWidth = MeasureInline(g, temperature, valueFont, inline);
            using (SolidBrush hot = new SolidBrush(ResolveTemperatureColor(this.snapshot.CurrentMaxCelsius)))
                g.DrawString(temperature, valueFont, hot,
                    new Rectangle(bounds.Right - temperatureWidth, bounds.Top, temperatureWidth, bounds.Height), inline);

            string zone = string.IsNullOrEmpty(this.snapshot.CurrentHotZoneName) ? "--" : this.snapshot.CurrentHotZoneName;
            int zoneWidth = MeasureInline(g, zone, labelFont, inline);
            int zoneLeft = bounds.Right - temperatureWidth - S(6) - zoneWidth;
            g.DrawString(zone, labelFont, muted, new Rectangle(zoneLeft, bounds.Top, zoneWidth, bounds.Height), inline);
            int rightLimit = zoneLeft - S(10);

            int x = bounds.Left;
            x += DrawStripPair(g, bounds, x, "记录", FormatDuration(this.snapshot.RecordedMinutes),
                DesignTokens.Colors.TextMuted, false, Color.Empty, labelFont, valueFont, inline);
            x += DrawStripPair(g, bounds, x, "工作", FormatDuration(this.snapshot.ActiveMinutes),
                DesignTokens.Colors.Success, true, DesignTokens.Colors.Success, labelFont, valueFont, inline);
            x += DrawStripPair(g, bounds, x, "空闲", FormatDuration(this.snapshot.IdleMinutes),
                DesignTokens.Colors.Warning, true, DesignTokens.Colors.Warning, labelFont, valueFont, inline);
            x += DrawStripPair(g, bounds, x, "睡眠", FormatDuration(this.snapshot.SleepMinutes),
                DesignTokens.Colors.AccentAlt, true, DesignTokens.Colors.AccentAlt, labelFont, valueFont, inline);

            DrawStripDivider(g, bounds, x);
            x += S(9);

            string battery = this.snapshot.CurrentBatteryKnown
                ? this.snapshot.CurrentBatteryPercent.ToString(CultureInfo.InvariantCulture) + "%"
                : "--%";
            Color batteryColor = this.snapshot.CurrentCharging ? DesignTokens.Colors.DangerStrong : DesignTokens.Colors.Accent;
            x += DrawStripPair(g, bounds, x, "电量", battery, batteryColor, false, Color.Empty, labelFont, valueFont, inline);

            string watts = this.snapshot.CurrentWattsKnown
                ? this.snapshot.CurrentWatts.ToString("0.0", CultureInfo.InvariantCulture) + "W"
                : "--W";
            int wattsWidth = MeasureInline(g, watts, valueFont, inline);
            g.DrawString(watts, valueFont, muted, new Rectangle(x, bounds.Top, wattsWidth, bounds.Height), inline);
            x += wattsWidth + S(10);

            // 续航估算文本长度不可控，只给它剩余宽度并允许省略，避免顶到右侧热区读数。
            int etaWidth = Math.Max(0, rightLimit - x);
            if (etaWidth > S(24))
            {
                using (SolidBrush dim = new SolidBrush(DesignTokens.Colors.GlyphMuted))
                    g.DrawString(this.snapshot.BatteryEtaText ?? "等待电量趋势", labelFont, dim,
                        new Rectangle(x, bounds.Top, etaWidth, bounds.Height), inline);
            }
        }
    }

    private int DrawStripPair(
        Graphics g,
        Rectangle row,
        int x,
        string label,
        string value,
        Color valueColor,
        bool swatch,
        Color swatchColor,
        Font labelFont,
        Font valueFont,
        StringFormat inline)
    {
        int start = x;
        if (swatch)
        {
            int size = S(6);
            using (SolidBrush dot = new SolidBrush(swatchColor))
                g.FillRectangle(dot, x, row.Top + (row.Height - size) / 2, size, size);
            x += size + S(4);
        }
        int labelWidth = MeasureInline(g, label, labelFont, inline);
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            g.DrawString(label, labelFont, muted, new Rectangle(x, row.Top, labelWidth, row.Height), inline);
        x += labelWidth + S(4);
        int valueWidth = MeasureInline(g, value, valueFont, inline);
        using (SolidBrush brush = new SolidBrush(valueColor))
            g.DrawString(value, valueFont, brush, new Rectangle(x, row.Top, valueWidth, row.Height), inline);
        x += valueWidth + S(11);
        return x - start;
    }

    private void DrawStripDivider(Graphics g, Rectangle row, int x)
    {
        using (Pen pen = new Pen(DesignTokens.White(30), Math.Max(1.0f, this.LayerScale)))
            g.DrawLine(pen, x, row.Top + S(2), x, row.Bottom - S(2));
    }

    // 图例行接管原先漂浮在曲线右端的峰值文字：名称 + 当前值 + /峰值，横向按实测宽度流式排布。
    private void DrawLegendStrip(Graphics g, Rectangle row, Font labelFont, Font valueFont, Font peakFont)
    {
        using (StringFormat inline = CreateInlineFormat())
        {
            bool withPeaks = MeasureLegendWidth(g, labelFont, valueFont, peakFont, inline, true) <= row.Width;
            int x = row.Left;
            int centerY = row.Top + row.Height / 2;
            for (int i = 1; i < SeriesCount; i++)
            {
                string current = FormatCurrentValue(i);
                string peak = withPeaks ? FormatPeakSuffix(i) : string.Empty;
                int need = MeasureLegendItemWidth(g, i, current, peak, labelFont, valueFont, peakFont, inline);
                if (x + need > row.Right) break;

                Color color = GetSeriesColor(i);
                int sample = S(12);
                if (i == 6) DrawBatteryLegendSample(g, x, centerY, sample);
                else
                    using (Pen pen = new Pen(color, Math.Max(1.4f, S(1.8f))))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawLine(pen, x, centerY, x + sample, centerY);
                    }
                int cursor = x + sample + S(4);
                int labelWidth = MeasureInline(g, SeriesLabels[i], labelFont, inline);
                using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.TextMuted))
                    g.DrawString(SeriesLabels[i], labelFont, muted, new Rectangle(cursor, row.Top, labelWidth, row.Height), inline);
                cursor += labelWidth + S(3);
                int currentWidth = MeasureInline(g, current, valueFont, inline);
                using (SolidBrush brush = new SolidBrush(color))
                    g.DrawString(current, valueFont, brush, new Rectangle(cursor, row.Top, currentWidth, row.Height), inline);
                cursor += currentWidth;
                if (peak.Length > 0)
                {
                    cursor += S(2);
                    int peakWidth = MeasureInline(g, peak, peakFont, inline);
                    using (SolidBrush dim = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 214)))
                        g.DrawString(peak, peakFont, dim, new Rectangle(cursor, row.Top, peakWidth, row.Height), inline);
                }
                x += need;
            }
        }
    }

    // 电量图例同时交代三件事：虚线是电量、左半红为充电上升、右半青为放电下降。
    private void DrawBatteryLegendSample(Graphics g, int x, int centerY, int sample)
    {
        int half = Math.Max(1, sample / 2);
        using (Pen rising = new Pen(DesignTokens.Colors.DangerStrong, Math.Max(1.4f, S(1.8f))))
        using (Pen falling = new Pen(DesignTokens.Colors.Accent, Math.Max(1.4f, S(1.8f))))
        {
            rising.DashStyle = DashStyle.Dash;
            rising.DashPattern = new float[] { 2.4f, 1.6f };
            falling.DashStyle = DashStyle.Dash;
            falling.DashPattern = new float[] { 2.4f, 1.6f };
            g.DrawLine(rising, x, centerY, x + half, centerY);
            g.DrawLine(falling, x + half, centerY, x + sample, centerY);
        }
    }

    private int MeasureLegendWidth(Graphics g, Font labelFont, Font valueFont, Font peakFont, StringFormat inline, bool withPeaks)
    {
        int total = 0;
        for (int i = 1; i < SeriesCount; i++)
        {
            string current = FormatCurrentValue(i);
            string peak = withPeaks ? FormatPeakSuffix(i) : string.Empty;
            total += MeasureLegendItemWidth(g, i, current, peak, labelFont, valueFont, peakFont, inline);
        }
        return total;
    }

    private int MeasureLegendItemWidth(
        Graphics g,
        int seriesIndex,
        string current,
        string peak,
        Font labelFont,
        Font valueFont,
        Font peakFont,
        StringFormat inline)
    {
        int width = S(12) + S(4)
            + MeasureInline(g, SeriesLabels[seriesIndex], labelFont, inline) + S(3)
            + MeasureInline(g, current, valueFont, inline);
        if (peak.Length > 0) width += S(2) + MeasureInline(g, peak, peakFont, inline);
        return width + S(11);
    }

    private void DrawSharedPlot(Graphics g, Rectangle plot, Font axisFont)
    {
        DrawPanel(g, plot, EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay));
        DrawValueGrid(g, plot, axisFont);
        DrawTimeGrid(g, plot);

        double networkScale = ResolveNetworkScale();
        int[] segments = BuildSegmentIds();
        double[][] values = new double[SeriesCount][];
        for (int i = 1; i < SeriesCount; i++) values[i] = BuildSeriesValues(i, networkScale, segments);

        // 基数样条会在拐点处小幅过冲到 0% 以下或 100% 以上，这里把曲线夹在画框内，
        // 免得弧线溢出到网格外面去。
        GraphicsState plotClip = g.Save();
        g.SetClip(plot);
        DrawSeries(g, plot, 5, values[5], 64, 1.0f);
        DrawSeriesArea(g, plot, 1, values[1]);
        DrawSeries(g, plot, 3, values[3], 206, 1.2f);
        DrawSeries(g, plot, 4, values[4], 220, 1.3f);
        DrawSeries(g, plot, 2, values[2], 234, 1.3f);
        DrawSeries(g, plot, 1, values[1], 255, 1.8f);
        DrawBatterySeries(g, plot, values[6]);
        DrawTemperatureSeries(g, plot, values[7]);
        g.Restore(plotClip);
        DrawTemperatureAxis(g, plot, axisFont);
        DrawPeakMarker(g, plot, 1, values[1], axisFont, false);
        DrawPeakMarker(g, plot, 7, values[7], axisFont, true);
    }

    internal bool SmoothingEnabled
    {
        get { return this.CurrentSettings != null && this.CurrentSettings.SystemDayBoardSmoothingEnabled; }
    }

    // 采样断档两侧属于不同段，平滑时不能互相取平均，否则会在缺口上凭空造出一段连续曲线。
    private int[] BuildSegmentIds()
    {
        int count = this.snapshot.Points.Count;
        int[] ids = new int[count];
        int id = 0;
        DateTime previous = DateTime.MinValue;
        for (int i = 0; i < count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            if (point == null) { ids[i] = -1; previous = DateTime.MinValue; continue; }
            if (IsPlotGap(previous, point.TimestampLocal)) id++;
            ids[i] = id;
            previous = point.TimestampLocal;
        }
        return ids;
    }

    // 窗口随点数走：点太少时平滑只会把曲线抹成直线，所以低于 9 点直接不平滑；
    // 上限 11 是为了让“近一周”也保留得住真实的形状转折。
    internal static int ResolveSmoothingWindow(int pointCount)
    {
        if (pointCount < 9) return 0;
        int window = pointCount / 26;
        if (window < 3) window = 3;
        if (window > 11) window = 11;
        if (window % 2 == 0) window++;
        return window;
    }

    // 平滑只改画线。摘要、图例当前值与峰值读数始终取原始采样，不经过这里。
    private double[] BuildSeriesValues(int seriesIndex, double networkScale, int[] segments)
    {
        int count = this.snapshot.Points.Count;
        double[] raw = new double[count];
        for (int i = 0; i < count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            raw[i] = (point == null || !IsPointKnown(point, seriesIndex))
                ? double.NaN
                : ResolveNormalizedValue(point, seriesIndex, networkScale);
        }

        int window = this.SmoothingEnabled ? ResolveSmoothingWindow(count) : 0;
        if (window < 3) return raw;

        double[] smoothed = new double[count];
        int half = window / 2;
        for (int i = 0; i < count; i++)
        {
            if (double.IsNaN(raw[i])) { smoothed[i] = double.NaN; continue; }
            double sum = 0.0;
            int used = 0;
            for (int k = i - half; k <= i + half; k++)
            {
                if (k < 0 || k >= count || double.IsNaN(raw[k])) continue;
                if (segments[k] != segments[i]) continue;
                sum += raw[k];
                used++;
            }
            smoothed[i] = used > 0 ? sum / used : raw[i];
        }
        return smoothed;
    }

    private static double ResolveCelsiusFromAxis(double normalizedValue)
    {
        return TemperatureAxisMinCelsius
            + (TemperatureAxisMaxCelsius - TemperatureAxisMinCelsius) * normalizedValue / 100.0;
    }

    private void DrawValueGrid(Graphics g, Rectangle plot, Font axisFont)
    {
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        using (SolidBrush label = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        {
            for (int i = 0; i < ValueTickCount; i++)
            {
                int value = ValueTicks[i];
                int y = (int)Math.Round(ResolvePlotY(plot, value));
                using (Pen grid = new Pen(DesignTokens.White(value == 0 ? 42 : 22), Math.Max(1.0f, this.LayerScale)))
                {
                    if (value != 0) grid.DashStyle = DashStyle.Dash;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
                g.DrawString(value.ToString(CultureInfo.InvariantCulture), axisFont, label,
                    new Rectangle(plot.Left - S(34), y - S(7), S(27), S(14)), far);
            }
            g.DrawString("%", axisFont, label, new Rectangle(plot.Left - S(34), plot.Top - S(15), S(27), S(12)), far);
        }
    }

    private void DrawTimeGrid(Graphics g, Rectangle plot)
    {
        using (Pen grid = new Pen(DesignTokens.White(20), Math.Max(1.0f, this.LayerScale)))
        {
            for (int i = 0; i < TimeTickCount; i++)
            {
                int x = ResolveTickX(plot, i);
                g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            }
        }
    }

    private void DrawTimeLabels(Graphics g, Rectangle plot, Rectangle row, Font axisFont)
    {
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        using (SolidBrush label = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        {
            int width = S(50);
            for (int i = 0; i < TimeTickCount; i++)
            {
                int x = ResolveTickX(plot, i);
                string text = FormatTickTime(i);
                if (i == 0)
                    g.DrawString(text, axisFont, label, new Rectangle(x, row.Top, width, row.Height), near);
                else if (i == TimeTickCount - 1)
                    g.DrawString(text, axisFont, label, new Rectangle(x - width, row.Top, width, row.Height), far);
                else
                    g.DrawString(text, axisFont, label, new Rectangle(x - width / 2, row.Top, width, row.Height), center);
            }
        }
    }

    private int ResolveTickX(Rectangle plot, int tickIndex)
    {
        double ratio = tickIndex / (double)(TimeTickCount - 1);
        return plot.Left + (int)Math.Round(plot.Width * ratio);
    }

    private string FormatTickTime(int tickIndex)
    {
        double ratio = tickIndex / (double)(TimeTickCount - 1);
        DateTime time = this.snapshot.StartLocal + TimeSpan.FromTicks(
            (long)((this.snapshot.EndLocal - this.snapshot.StartLocal).Ticks * ratio));
        return this.selectedRange == SystemDayRange.LastWeek
            ? time.ToString("MM/dd", CultureInfo.InvariantCulture)
            : time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // 工作状态并入同一画框底沿，与曲线共用一条时间轴，因此不再需要独立的状态行和刻度。
    private void DrawStateRibbon(Graphics g, Rectangle bounds)
    {
        using (GraphicsPath track = RoundedRectangle(bounds, S(2)))
        {
            using (SolidBrush back = new SolidBrush(DesignTokens.White(12)))
                g.FillPath(back, track);
            GraphicsState state = g.Save();
            g.SetClip(track);
            for (int i = 0; i < this.snapshot.WorkSegments.Count; i++)
            {
                SystemDayWorkSegment segment = this.snapshot.WorkSegments[i];
                if (segment == null) continue;
                int left = ResolveTimeX(bounds, segment.StartLocal);
                int right = ResolveTimeX(bounds, segment.EndLocal);
                if (right <= left) right = left + Math.Max(1, S(1));
                Color color = segment.State == SystemDayWorkState.Active
                    ? DesignTokens.Colors.Success
                    : segment.State == SystemDayWorkState.Idle
                        ? DesignTokens.Colors.Warning
                        : DesignTokens.Colors.AccentAlt;
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 216)))
                    g.FillRectangle(fill, left, bounds.Top, Math.Max(1, right - left), bounds.Height);
            }
            g.Restore(state);
        }
    }

    private void DrawSeriesArea(Graphics g, Rectangle plot, int seriesIndex, double[] values)
    {
        Color color = GetSeriesColor(seriesIndex);
        Rectangle brushBounds = new Rectangle(plot.Left, plot.Top - 1, Math.Max(1, plot.Width), Math.Max(2, plot.Height + 2));
        using (LinearGradientBrush fill = new LinearGradientBrush(
            brushBounds,
            DesignTokens.WithAlpha(color, 86),
            DesignTokens.WithAlpha(color, 0),
            LinearGradientMode.Vertical))
        {
            List<PointF> run = new List<PointF>();
            DateTime previousTime = DateTime.MinValue;
            for (int i = 0; i < this.snapshot.Points.Count; i++)
            {
                SystemDayBoardPoint point = this.snapshot.Points[i];
                if (point == null || double.IsNaN(values[i])) { FillAreaRun(g, plot, run, fill); previousTime = DateTime.MinValue; continue; }
                if (run.Count > 0 && IsPlotGap(previousTime, point.TimestampLocal)) FillAreaRun(g, plot, run, fill);
                run.Add(new PointF(ResolveTimeX(plot, point.TimestampLocal), ResolvePlotY(plot, values[i])));
                previousTime = point.TimestampLocal;
            }
            FillAreaRun(g, plot, run, fill);
        }
    }

    private void FillAreaRun(Graphics g, Rectangle plot, List<PointF> run, Brush fill)
    {
        if (run.Count < 2) { run.Clear(); return; }
        using (GraphicsPath path = new GraphicsPath())
        {
            // 填充上沿必须和曲线用同一种连点方式，否则平滑态下填充边界会和线错开。
            if (this.SmoothingEnabled && run.Count >= 3) path.AddCurve(run.ToArray(), SmoothingCurveTension);
            else path.AddLines(run.ToArray());
            path.AddLine(run[run.Count - 1].X, plot.Bottom, run[0].X, plot.Bottom);
            path.CloseFigure();
            g.FillPath(fill, path);
        }
        run.Clear();
    }

    // 平滑开启时整段走 DrawCurve，关闭时仍是老老实实的折线——原始态必须是采样点之间的直连，
    // 不能让样条替数据编造中间形状。两点及以下不成弧，退回直线。
    private void StrokeRun(Graphics g, Pen pen, List<PointF> run)
    {
        if (run.Count < 2) return;
        PointF[] points = run.ToArray();
        if (this.SmoothingEnabled && points.Length >= 3) g.DrawCurve(pen, points, SmoothingCurveTension);
        else g.DrawLines(pen, points);
    }

    // 把一条曲线按“断档”切成连续段，逐段交给 StrokeRun。
    private void BuildRuns(Rectangle plot, double[] values, Action<List<PointF>> stroke)
    {
        List<PointF> run = new List<PointF>();
        DateTime previousTime = DateTime.MinValue;
        for (int i = 0; i < this.snapshot.Points.Count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            if (point == null || double.IsNaN(values[i]))
            {
                if (run.Count > 0) { stroke(run); run.Clear(); }
                previousTime = DateTime.MinValue;
                continue;
            }
            if (run.Count > 0 && IsPlotGap(previousTime, point.TimestampLocal))
            {
                stroke(run);
                run.Clear();
            }
            run.Add(new PointF(ResolveTimeX(plot, point.TimestampLocal), ResolvePlotY(plot, values[i])));
            previousTime = point.TimestampLocal;
        }
        if (run.Count > 0) { stroke(run); run.Clear(); }
    }

    private void DrawSeries(Graphics g, Rectangle plot, int seriesIndex, double[] values, int alpha, float width)
    {
        using (Pen pen = new Pen(DesignTokens.WithAlpha(GetSeriesColor(seriesIndex), alpha), Math.Max(1.1f, width * this.LayerScale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            Pen stroked = pen;
            BuildRuns(plot, values, delegate(List<PointF> run) { StrokeRun(g, stroked, run); });
        }
    }

    // 电量与温度的线色随数据变化（红升青降、按温区变暖），只能按“同色连续段”分批落笔。
    private void DrawBatterySeries(Graphics g, Rectangle plot, double[] values)
    {
        // 必须整段一次 DrawLines：逐段 DrawLine 会在每段起点重置虚线相位，
        // 而单段宽度小于一个 dash 周期时，画出来会是完全实线。
        List<PointF> run = new List<PointF>();
        SystemDayBatteryDirection runDirection = SystemDayBatteryDirection.Unknown;
        DateTime previousTime = DateTime.MinValue;
        PointF previous = PointF.Empty;
        bool hasPrevious = false;
        for (int i = 0; i < this.snapshot.Points.Count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            if (point == null || double.IsNaN(values[i]))
            {
                FlushBatteryRun(g, run, runDirection);
                hasPrevious = false;
                continue;
            }
            PointF current = new PointF(ResolveTimeX(plot, point.TimestampLocal), ResolvePlotY(plot, values[i]));
            if (!hasPrevious || IsPlotGap(previousTime, point.TimestampLocal))
            {
                FlushBatteryRun(g, run, runDirection);
            }
            else
            {
                if (run.Count > 0 && point.BatteryDirection != runDirection)
                {
                    // 方向切换时让上一段收在当前点，两段首尾相接不留缺口。
                    run.Add(current);
                    FlushBatteryRun(g, run, runDirection);
                }
                if (run.Count == 0) run.Add(previous);
                run.Add(current);
                runDirection = point.BatteryDirection;
            }
            previous = current;
            previousTime = point.TimestampLocal;
            hasPrevious = true;
        }
        FlushBatteryRun(g, run, runDirection);
    }

    private void FlushBatteryRun(Graphics g, List<PointF> run, SystemDayBatteryDirection direction)
    {
        if (run.Count < 2) { run.Clear(); return; }
        using (Pen pen = new Pen(ResolveBatteryDirectionColor(direction), Math.Max(1.3f, 2.0f * this.LayerScale)))
        {
            // 放电色 Accent 与 CPU 同色，同框后只靠颜色分不开；虚线是不占用
            // “红升青降”语义的第二个区分维度。
            pen.DashStyle = DashStyle.Dash;
            pen.DashPattern = new float[] { 3.2f, 2.0f };
            pen.LineJoin = LineJoin.Round;
            StrokeRun(g, pen, run);
        }
        run.Clear();
    }

    // 温度线色随温区变化，所以按“同色带连续段”切；段内必须一次落笔，
    // 逐段 DrawLine 画不出弧线，只会退化成折线。
    private void DrawTemperatureSeries(Graphics g, Rectangle plot, double[] values)
    {
        List<PointF> run = new List<PointF>();
        Color runColor = Color.Empty;
        DateTime previousTime = DateTime.MinValue;
        PointF previous = PointF.Empty;
        bool hasPrevious = false;
        for (int i = 0; i < this.snapshot.Points.Count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            if (point == null || double.IsNaN(values[i]))
            {
                FlushTemperatureRun(g, run, runColor);
                hasPrevious = false;
                continue;
            }
            PointF current = new PointF(ResolveTimeX(plot, point.TimestampLocal), ResolvePlotY(plot, values[i]));
            // 线色取实际画出的高度所对应的温度，平滑后才不会被原始尖峰的颜色闪一下。
            Color color = ResolveTemperatureColor(ResolveCelsiusFromAxis(values[i]));
            if (!hasPrevious || IsPlotGap(previousTime, point.TimestampLocal))
            {
                FlushTemperatureRun(g, run, runColor);
            }
            else
            {
                if (run.Count > 0 && color != runColor)
                {
                    // 换色时让上一段收在当前点，两段首尾相接不留缺口。
                    run.Add(current);
                    FlushTemperatureRun(g, run, runColor);
                }
                if (run.Count == 0) run.Add(previous);
                run.Add(current);
                runColor = color;
            }
            previous = current;
            previousTime = point.TimestampLocal;
            hasPrevious = true;
        }
        FlushTemperatureRun(g, run, runColor);
    }

    private void FlushTemperatureRun(Graphics g, List<PointF> run, Color color)
    {
        if (run.Count < 2 || color.IsEmpty) { run.Clear(); return; }
        using (Pen pen = new Pen(color, Math.Max(1.3f, 1.8f * this.LayerScale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            StrokeRun(g, pen, run);
        }
        run.Clear();
    }

    private void DrawTemperatureAxis(Graphics g, Rectangle plot, Font axisFont)
    {
        int x = plot.Right + S(7);
        Color color = DesignTokens.Colors.Warning;
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (SolidBrush label = new SolidBrush(DesignTokens.WithAlpha(color, 212)))
        using (Pen tick = new Pen(DesignTokens.WithAlpha(color, 120), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawString("°C", axisFont, label, new Rectangle(x, plot.Top - S(15), S(28), S(12)), near);
            for (int i = 0; i < ValueTickCount; i++)
            {
                int value = ValueTicks[i];
                int y = (int)Math.Round(ResolvePlotY(plot, value));
                double celsius = TemperatureAxisMinCelsius
                    + (TemperatureAxisMaxCelsius - TemperatureAxisMinCelsius) * value / 100.0;
                g.DrawLine(tick, x - S(5), y, x - S(1), y);
                g.DrawString(celsius.ToString("0", CultureInfo.InvariantCulture), axisFont, label,
                    new Rectangle(x + S(1), y - S(7), S(26), S(14)), near);
            }
        }
    }

    private void DrawPeakMarker(Graphics g, Rectangle plot, int seriesIndex, double[] values, Font font, bool below)
    {
        SystemDayMetricPeak peak = GetPeakForRow(seriesIndex);
        if (peak == null || peak.TimestampLocal == DateTime.MinValue) return;
        // 峰值可能来自被裁剪掉的区间，落在当前范围外时不画标记，避免把标签钉在画框边缘。
        if (peak.TimestampLocal < this.snapshot.StartLocal || peak.TimestampLocal > this.snapshot.EndLocal) return;

        // 标记钉在实际画出来的那条线上（开了平滑就是平滑值），标签仍报原始峰值与时刻；
        // 否则开启平滑后圆点会浮在曲线上方，看起来像画错了。
        int index = ResolveNearestPointIndex(peak.TimestampLocal, values);
        if (index < 0) return;
        int x = ResolveTimeX(plot, this.snapshot.Points[index].TimestampLocal);
        int y = (int)Math.Round(ResolvePlotY(plot, values[index]));
        Color color = seriesIndex == 7 ? ResolveTemperatureColor(peak.Value) : GetSeriesColor(seriesIndex);

        int radius = Math.Max(2, S(3));
        using (SolidBrush dot = new SolidBrush(color))
        using (Pen halo = new Pen(DesignTokens.Colors.AppBackground, Math.Max(1.0f, 1.6f * this.LayerScale)))
        {
            g.FillEllipse(dot, x - radius, y - radius, radius * 2, radius * 2);
            g.DrawEllipse(halo, x - radius, y - radius, radius * 2, radius * 2);
        }

        string text = FormatPeak(seriesIndex, peak);
        int width = S(118);
        bool flip = x + S(6) + width > plot.Right;
        int top = below ? y + S(7) : y - S(17);
        top = Math.Max(plot.Top + S(1), Math.Min(plot.Bottom - S(14), top));
        Rectangle rect = flip
            ? new Rectangle(x - S(6) - width, top, width, S(13))
            : new Rectangle(x + S(6), top, width, S(13));
        using (StringFormat format = CreateFormat(flip ? StringAlignment.Far : StringAlignment.Near))
        using (SolidBrush brush = new SolidBrush(color))
            g.DrawString(text, font, brush, rect, format);
    }

    private int ResolveNearestPointIndex(DateTime timestampLocal, double[] values)
    {
        int best = -1;
        double bestDelta = double.MaxValue;
        for (int i = 0; i < this.snapshot.Points.Count; i++)
        {
            SystemDayBoardPoint point = this.snapshot.Points[i];
            if (point == null || double.IsNaN(values[i])) continue;
            double delta = Math.Abs((point.TimestampLocal - timestampLocal).TotalSeconds);
            if (delta < bestDelta) { bestDelta = delta; best = i; }
        }
        return best;
    }

    private void DrawFooter(Graphics g, Rectangle bounds, Font bodyFont, Font smallFont)
    {
        string rangeText = this.selectedRange == SystemDayRange.Today
            ? "今天 ›"
            : this.selectedRange == SystemDayRange.Last24Hours
                ? "24小时 ›"
                : "近一周 ›";
        bool smoothing = this.SmoothingEnabled;
        DrawFooterAction(g, GetRangeActionBounds(), rangeText, DesignTokens.Colors.Success, bodyFont);
        DrawFooterAction(
            g,
            GetSmoothingActionBounds(),
            smoothing ? "平滑 开" : "平滑 关",
            smoothing ? DesignTokens.Colors.Accent : DesignTokens.Colors.GlyphMuted,
            bodyFont);
        DrawFooterAction(g, GetCloseBounds(), "关闭", DesignTokens.Colors.Danger, bodyFont);
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            int window = ResolveSmoothingWindow(this.snapshot.Points.Count);
            string hint = smoothing && window >= 3
                ? "曲线已平滑（" + window.ToString(CultureInfo.InvariantCulture) + " 点滑动平均 · 弧线连点）· 摘要与峰值仍为原始采样"
                : "共享 0–100% 轴 · 右轴 °C · NET 按峰值归一";
            int logLeft = Math.Min(bounds.Right, GetCloseBounds().Right + S(5));
            g.DrawString(hint, smallFont, muted, new Rectangle(logLeft, bounds.Top, Math.Max(0, bounds.Right - logLeft), bounds.Height), far);
        }
    }

    private Rectangle GetSmoothingActionBounds()
    {
        Rectangle rangeAction = GetRangeActionBounds();
        return new Rectangle(rangeAction.Right + S(4), rangeAction.Top, S(62), rangeAction.Height);
    }

    private Rectangle GetCloseBounds()
    {
        Rectangle smoothingAction = GetSmoothingActionBounds();
        return new Rectangle(smoothingAction.Right + S(4), smoothingAction.Top, S(42), smoothingAction.Height);
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

    // 七条曲线同框，CPU 的青与 GPU 原用的 AccentSoft 在深色底上几乎分不开，
    // GPU 改用同为既有 token 的 AccentGradientEnd 拉开色相距离。
    internal static Color GetSeriesColor(int seriesIndex)
    {
        switch (seriesIndex)
        {
            case 1: return DesignTokens.Colors.Accent;
            case 2: return DesignTokens.Colors.AccentGradientEnd;
            case 3: return DesignTokens.Colors.TextMuted;
            case 4: return DesignTokens.Colors.AccentAlt;
            case 5: return DesignTokens.Colors.Success;
            case 6: return DesignTokens.Colors.Danger;
            case 7: return DesignTokens.Colors.Warning;
            default: return DesignTokens.Colors.TextMuted;
        }
    }

    private SystemDayMetricPeak GetPeakForRow(int seriesIndex)
    {
        switch (seriesIndex)
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

    private double ResolveNetworkScale()
    {
        SystemDayMetricPeak peak = this.snapshot.FindPeak("network");
        return peak == null ? 0.0 : Math.Max(1.0, peak.Value);
    }

    // 所有曲线在进入共享纵轴前统一归一到 0–100：温度按 20–100°C，网络按本范围峰值。
    internal static double ResolveNormalizedValue(SystemDayBoardPoint point, int seriesIndex, double networkScale)
    {
        if (point == null) return 0.0;
        if (seriesIndex == 5)
            return networkScale <= 0.0 ? 0.0 : point.NetworkBytesPerSecond / networkScale * 100.0;
        if (seriesIndex == 7)
            return (point.MaxCelsius - TemperatureAxisMinCelsius)
                / (TemperatureAxisMaxCelsius - TemperatureAxisMinCelsius) * 100.0;
        return GetPointValue(point, seriesIndex);
    }

    private static bool IsPointKnown(SystemDayBoardPoint point, int seriesIndex)
    {
        if (seriesIndex == 6) return point.BatteryKnown;
        if (seriesIndex == 7) return point.TemperatureKnown;
        return true;
    }

    private static double GetPointValue(SystemDayBoardPoint point, int seriesIndex)
    {
        switch (seriesIndex)
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

    private SystemDayBoardPoint GetLatestPoint()
    {
        for (int i = this.snapshot.Points.Count - 1; i >= 0; i--)
            if (this.snapshot.Points[i] != null) return this.snapshot.Points[i];
        return null;
    }

    private string FormatCurrentValue(int seriesIndex)
    {
        SystemDayBoardPoint last = GetLatestPoint();
        switch (seriesIndex)
        {
            case 5:
                return last == null ? "--" : FormatCompactRate(last.NetworkBytesPerSecond);
            case 6:
                return this.snapshot.CurrentBatteryKnown
                    ? this.snapshot.CurrentBatteryPercent.ToString(CultureInfo.InvariantCulture) + "%"
                    : "--%";
            case 7:
                return this.snapshot.CurrentTemperatureKnown
                    ? this.snapshot.CurrentMaxCelsius.ToString("0.0", CultureInfo.InvariantCulture) + "°"
                    : "--°";
            default:
                return last == null
                    ? "--"
                    : Math.Round(GetPointValue(last, seriesIndex)).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }

    private string FormatPeakSuffix(int seriesIndex)
    {
        if (seriesIndex == 6) return string.Empty;
        SystemDayMetricPeak peak = GetPeakForRow(seriesIndex);
        if (peak == null || peak.TimestampLocal == DateTime.MinValue) return string.Empty;
        if (seriesIndex == 5) return "/" + FormatCompactRate(peak.Value);
        if (seriesIndex == 7) return "/" + peak.Value.ToString("0.0", CultureInfo.InvariantCulture) + "°";
        return "/" + peak.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    // 图例行宽度有限，把 "176 Mbps" 压成 "176M"；完整单位仍由峰值标记和架构文档给出。
    private static string FormatCompactRate(double bytesPerSecond)
    {
        string text = NetworkRateFormatter.Format(bytesPerSecond);
        int space = text.IndexOf(' ');
        if (space <= 0 || space + 1 >= text.Length) return text;
        return text.Substring(0, space) + text.Substring(space + 1, 1);
    }

    private string FormatPeak(int seriesIndex, SystemDayMetricPeak peak)
    {
        if (seriesIndex == 6)
        {
            if (!this.snapshot.CurrentBatteryKnown) return "--";
            return "当前 " + this.snapshot.CurrentBatteryPercent.ToString(CultureInfo.InvariantCulture) + "%";
        }
        if (peak == null || peak.TimestampLocal == DateTime.MinValue) return "峰 --";
        string value;
        if (seriesIndex == 5) value = NetworkRateFormatter.Format(peak.Value);
        else if (seriesIndex == 7)
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

    // 共享纵轴：0 贴画框底沿，100 贴顶沿，没有 per-row inset，两条曲线的高低可以直接比。
    internal static float ResolvePlotY(Rectangle plot, double normalizedValue)
    {
        double ratio = Math.Max(0.0, Math.Min(1.0, normalizedValue / 100.0));
        return plot.Bottom - (float)(plot.Height * ratio);
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

    // 流式排版必须用与绘制同一套排版规则去量宽，否则实测宽度和落笔宽度会对不上。
    private static StringFormat CreateInlineFormat()
    {
        // GenericTypographic 每次访问都会新建一个 GDI+ 对象，复制后必须释放原件，
        // 否则这块每次重绘都会漏一批句柄。
        using (StringFormat generic = StringFormat.GenericTypographic)
        {
            StringFormat format = new StringFormat(generic);
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags |= StringFormatFlags.NoWrap;
            return format;
        }
    }

    private int MeasureInline(Graphics g, string text, Font font, StringFormat format)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(g.MeasureString(text, font, int.MaxValue, format).Width) + 1;
    }
}
