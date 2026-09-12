using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

internal sealed partial class SystemDayBoardForm
{
    internal static void RenderSample(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        SystemDayBoardSnapshot fixture = CreateFixtureSnapshot(SystemDayRange.Today);
        using (SystemDayBoardForm form = new SystemDayBoardForm(null, settings, delegate(SystemDayRange range)
        {
            return CreateFixtureSnapshot(range);
        }))
        {
            form.Size = form.GetDesiredSize();
            form.CurrentSettings.SystemDayBoardSmoothingEnabled = false;
            string path = Path.Combine(outputDir, "system-day-board.png");
            RenderSampleSupport.SaveComposited(outputDir, Path.GetFileName(path), form.Width, form.Height, 255, form.DrawWindowContent);
            Console.WriteLine("System Day board -> " + path + " (" + form.Width + "x" + form.Height + ")");

            form.CurrentSettings.SystemDayBoardSmoothingEnabled = true;
            string smoothedPath = Path.Combine(outputDir, "system-day-board-smoothed.png");
            RenderSampleSupport.SaveComposited(outputDir, Path.GetFileName(smoothedPath), form.Width, form.Height, 255, form.DrawWindowContent);
            Console.WriteLine("System Day board (smoothed) -> " + smoothedPath + " (" + form.Width + "x" + form.Height + ")");
            form.CurrentSettings.SystemDayBoardSmoothingEnabled = false;
        }
    }

    private static SystemDayBoardSnapshot CreateFixtureSnapshot(SystemDayRange range)
    {
        DateTime now = new DateTime(2026, 7, 23, 0, 40, 0, DateTimeKind.Local);
        SystemDayBoardSnapshot snapshot = SystemDayBoardSnapshot.CreateEmpty(range, now);
        snapshot.StartLocal = range == SystemDayRange.Today ? now.Date :
            range == SystemDayRange.Last24Hours ? now.AddHours(-24) : now.AddDays(-7);
        snapshot.UpdatedLocal = now;
        snapshot.ActiveMinutes = 408;
        snapshot.IdleMinutes = 96;
        snapshot.SleepMinutes = 424;
        snapshot.RecordedMinutes = 928;
        snapshot.RawSampleCount = 928;
        snapshot.CurrentBatteryKnown = true;
        snapshot.CurrentBatteryPercent = 76;
        snapshot.CurrentCharging = true;
        snapshot.CurrentPluggedIn = true;
        snapshot.CurrentWattsKnown = true;
        snapshot.CurrentWatts = 31.6;
        snapshot.BatteryEtaKnown = true;
        snapshot.BatteryEtaMinutes = 42;
        snapshot.BatteryEtaTargetPercent = 80;
        snapshot.BatteryEtaText = "约 42分 到 80%";
        snapshot.CurrentPowerModeText = "平衡";
        snapshot.CurrentTemperatureKnown = true;
        snapshot.CurrentMaxCelsius = 73.4;
        snapshot.CurrentHotZoneName = "TZ99";

        DateTime rangeStart = snapshot.StartLocal;
        double totalMinutes = Math.Max(1.0, (now - rangeStart).TotalMinutes);
        const int count = 120;
        for (int i = 0; i < count; i++)
        {
            double ratio = i / (double)(count - 1);
            DateTime time = rangeStart.AddMinutes(totalMinutes * ratio);
            double wave = (Math.Sin(i * 0.27) + 1.0) * 0.5;
            int battery = i < 45 ? 82 - i / 4 : i < 75 ? 71 + (i - 45) / 3 : 81 - (i - 75) / 5;
            SystemDayBatteryDirection direction = i == 0 ? SystemDayBatteryDirection.Unknown :
                i < 45 ? SystemDayBatteryDirection.Falling :
                i < 75 ? SystemDayBatteryDirection.Rising : SystemDayBatteryDirection.Falling;
            snapshot.Points.Add(new SystemDayBoardPoint
            {
                TimestampLocal = time,
                WorkState = i > 26 && i < 44 ? SystemDayWorkState.Sleep : i % 10 < 7 ? SystemDayWorkState.Active : SystemDayWorkState.Idle,
                CpuPercent = 16 + wave * 69,
                GpuPercent = 8 + (Math.Sin(i * 0.19 + 1.1) + 1.0) * 31,
                NpuPercent = i > 80 && i < 96 ? 64 : 2 + wave * 12,
                MemoryPercent = 44 + ratio * 22 + wave * 5,
                NetworkBytesPerSecond = (i % 17 == 0 ? 21 : 1 + wave * 4) * 1000000,
                BatteryKnown = true,
                BatteryPercent = Math.Max(0, Math.Min(100, battery)),
                BatteryDirection = direction,
                Charging = direction == SystemDayBatteryDirection.Rising,
                PluggedIn = direction == SystemDayBatteryDirection.Rising,
                WattsKnown = true,
                Watts = direction == SystemDayBatteryDirection.Rising ? 30.0 + wave * 8.0 : 8.0 + wave * 9.0,
                TemperatureKnown = true,
                MaxCelsius = 37 + wave * 37,
                AvgCelsius = 34 + wave * 21,
                HotZoneName = i % 3 == 0 ? "TZ99" : i % 3 == 1 ? "TZ2" : "TZ1"
            });
        }
        snapshot.WorkSegments.Add(new SystemDayWorkSegment { StartLocal = rangeStart, EndLocal = rangeStart.AddMinutes(totalMinutes * 0.22), State = SystemDayWorkState.Active });
        snapshot.WorkSegments.Add(new SystemDayWorkSegment { StartLocal = rangeStart.AddMinutes(totalMinutes * 0.22), EndLocal = rangeStart.AddMinutes(totalMinutes * 0.36), State = SystemDayWorkState.Sleep });
        snapshot.WorkSegments.Add(new SystemDayWorkSegment { StartLocal = rangeStart.AddMinutes(totalMinutes * 0.36), EndLocal = rangeStart.AddMinutes(totalMinutes * 0.72), State = SystemDayWorkState.Active });
        snapshot.WorkSegments.Add(new SystemDayWorkSegment { StartLocal = rangeStart.AddMinutes(totalMinutes * 0.72), EndLocal = rangeStart.AddMinutes(totalMinutes * 0.79), State = SystemDayWorkState.Idle });
        snapshot.WorkSegments.Add(new SystemDayWorkSegment { StartLocal = rangeStart.AddMinutes(totalMinutes * 0.79), EndLocal = now, State = SystemDayWorkState.Active });
        // 峰值直接从生成的点里实算，时间戳必然落在范围内，峰值标记才能真正压在曲线上；
        // 写死的时刻会落到范围之外，共享坐标系下的标记就永远画不出来。
        AddComputedPeak(snapshot, "cpu", "%", false, delegate(SystemDayBoardPoint p) { return p.CpuPercent; });
        AddComputedPeak(snapshot, "gpu", "%", false, delegate(SystemDayBoardPoint p) { return p.GpuPercent; });
        AddComputedPeak(snapshot, "npu", "%", false, delegate(SystemDayBoardPoint p) { return p.NpuPercent; });
        AddComputedPeak(snapshot, "memory", "%", false, delegate(SystemDayBoardPoint p) { return p.MemoryPercent; });
        AddComputedPeak(snapshot, "network", "B/s", false, delegate(SystemDayBoardPoint p) { return p.NetworkBytesPerSecond; });
        AddComputedPeak(snapshot, "power", "W", false, delegate(SystemDayBoardPoint p) { return p.Watts; });
        AddComputedPeak(snapshot, "temperature", "°C", true, delegate(SystemDayBoardPoint p) { return p.MaxCelsius; });
        return snapshot;
    }

    private static void AddComputedPeak(
        SystemDayBoardSnapshot snapshot,
        string id,
        string unit,
        bool useZone,
        Func<SystemDayBoardPoint, double> selector)
    {
        SystemDayBoardPoint best = null;
        for (int i = 0; i < snapshot.Points.Count; i++)
        {
            SystemDayBoardPoint point = snapshot.Points[i];
            if (point == null) continue;
            if (best == null || selector(point) > selector(best)) best = point;
        }
        if (best == null) return;
        AddFixturePeak(snapshot, id, selector(best), best.TimestampLocal, unit, useZone ? best.HotZoneName : "");
    }

    private static void AddFixturePeak(
        SystemDayBoardSnapshot snapshot,
        string id,
        double value,
        DateTime time,
        string unit,
        string zone)
    {
        snapshot.Peaks.Add(new SystemDayMetricPeak
        {
            MetricId = id,
            Value = value,
            TimestampLocal = time,
            Unit = unit,
            ZoneName = zone
        });
    }

    internal static void RunSelfTest()
    {
        if (ResolveBatteryDirectionColor(SystemDayBatteryDirection.Rising) != DesignTokens.Colors.DangerStrong ||
            ResolveBatteryDirectionColor(SystemDayBatteryDirection.Falling) != DesignTokens.Colors.Accent ||
            ResolveBatteryDirectionColor(SystemDayBatteryDirection.Flat) == DesignTokens.Colors.DangerStrong)
            throw new InvalidOperationException("System Day battery direction colors must keep rising red and falling cyan.");

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        SystemDayBoardSnapshot fixture = CreateFixtureSnapshot(SystemDayRange.Today);
        using (SystemDayBoardForm form = new SystemDayBoardForm(null, settings, delegate(SystemDayRange range) { return fixture; }))
        {
            form.Size = form.GetDesiredSize();
            if (form.Width != 648 || form.Height != 400)
                throw new InvalidOperationException("System Day board must preserve the established 648x400 footprint.");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                form.DrawWindowContent(g);
                Color center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                if (center.A == 0) throw new InvalidOperationException("System Day renderer produced transparent output.");
            }
            Rectangle timeRow = new Rectangle(form.S(59), form.S(103), form.S(569), form.S(27));
            if (form.ResolveTimeX(timeRow, fixture.StartLocal) != timeRow.Left ||
                form.ResolveTimeX(timeRow, fixture.EndLocal) != timeRow.Right)
                throw new InvalidOperationException("System Day time-axis ticks must span the full shared chart width.");

            // 统一坐标系的核心不变量：0 贴底、100 贴顶、50 在正中，所有曲线共用这一把尺。
            Rectangle plot = new Rectangle(form.S(46), form.S(92), form.Width - form.S(46) - form.S(50), form.S(228));
            if (Math.Abs(ResolvePlotY(plot, 0.0) - plot.Bottom) > 0.01f ||
                Math.Abs(ResolvePlotY(plot, 100.0) - plot.Top) > 0.01f ||
                Math.Abs(ResolvePlotY(plot, 50.0) - (plot.Top + plot.Bottom) / 2.0f) > 1.0f)
                throw new InvalidOperationException("System Day series must share one 0-100% vertical axis without per-row insets.");

            SystemDayBoardPoint probe = new SystemDayBoardPoint { MaxCelsius = 60.0, NetworkBytesPerSecond = 500.0, CpuPercent = 42.0 };
            if (Math.Abs(ResolveNormalizedValue(probe, 7, 0.0) - 50.0) > 0.001 ||
                Math.Abs(ResolveNormalizedValue(probe, 5, 1000.0) - 50.0) > 0.001 ||
                Math.Abs(ResolveNormalizedValue(probe, 1, 0.0) - 42.0) > 0.001)
                throw new InvalidOperationException("System Day must normalize temperature by 20-100C and network by the range peak.");

            if (GetSeriesColor(1) == GetSeriesColor(2))
                throw new InvalidOperationException("System Day CPU and GPU must stay visually separable inside the shared plot.");

            SystemDayMetricPeak cpuPeak = fixture.FindPeak("cpu");
            if (cpuPeak == null || cpuPeak.TimestampLocal < fixture.StartLocal || cpuPeak.TimestampLocal > fixture.EndLocal)
                throw new InvalidOperationException("System Day fixture peaks must fall inside the rendered range.");

            // 平滑窗口随点数走，点太少时必须退化成不平滑，否则曲线会被抹成直线。
            if (ResolveSmoothingWindow(8) != 0 ||
                ResolveSmoothingWindow(120) != 5 ||
                ResolveSmoothingWindow(4000) != 11 ||
                ResolveSmoothingWindow(120) % 2 == 0)
                throw new InvalidOperationException("System Day smoothing window must stay odd, bounded and disabled on short series.");

            int[] segments = form.BuildSegmentIds();
            double networkScale = form.ResolveNetworkScale();
            form.CurrentSettings.SystemDayBoardSmoothingEnabled = false;
            double[] rawNetwork = form.BuildSeriesValues(5, networkScale, segments);
            string rawCurrentTemperature = form.FormatCurrentValue(7);
            string rawPeakSuffix = form.FormatPeakSuffix(1);
            form.CurrentSettings.SystemDayBoardSmoothingEnabled = true;
            double[] smoothNetwork = form.BuildSeriesValues(5, networkScale, segments);

            int spike = 0;
            for (int i = 1; i < rawNetwork.Length; i++) if (rawNetwork[i] > rawNetwork[spike]) spike = i;
            if (!(smoothNetwork[spike] < rawNetwork[spike] - 1.0))
                throw new InvalidOperationException("System Day smoothing must flatten network spikes instead of redrawing them.");
            for (int i = 0; i < smoothNetwork.Length; i++)
                if (double.IsNaN(smoothNetwork[i]) != double.IsNaN(rawNetwork[i]))
                    throw new InvalidOperationException("System Day smoothing must not invent or drop samples.");

            // 平滑只改画线：摘要与图例读数必须仍然来自原始采样。
            if (!string.Equals(form.FormatCurrentValue(7), rawCurrentTemperature, StringComparison.Ordinal) ||
                !string.Equals(form.FormatPeakSuffix(1), rawPeakSuffix, StringComparison.Ordinal))
                throw new InvalidOperationException("System Day smoothing must not change summary or legend readouts.");

            if (form.GetSmoothingActionBounds().IntersectsWith(form.GetRangeActionBounds()) ||
                form.GetSmoothingActionBounds().IntersectsWith(form.GetCloseBounds()))
                throw new InvalidOperationException("System Day footer actions must not overlap.");

            // 弧线连点的张力必须留在温和区间：调高会让方波型数据在台阶处明显过冲，
            // 调到 0 就退化成折线、平滑态和原始态看不出区别。
            if (!(SmoothingCurveTension > 0.1f && SmoothingCurveTension <= 0.6f))
                throw new InvalidOperationException("System Day smoothing curve tension must stay in the gentle range.");
            form.CurrentSettings.SystemDayBoardSmoothingEnabled = false;
        }
        Console.WriteLine("System Day board: PASS 648x400, shared 0-100% axis, unified ticks, rise=red, fall=cyan, in-range peaks, gap-safe smoothing drawn as tensioned arcs with raw readouts, three non-overlapping footer actions");
    }
}
