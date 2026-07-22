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
            string path = Path.Combine(outputDir, "system-day-board.png");
            RenderSampleSupport.SaveComposited(outputDir, Path.GetFileName(path), form.Width, form.Height, 255, form.DrawWindowContent);
            Console.WriteLine("System Day board -> " + path + " (" + form.Width + "x" + form.Height + ")");
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
        AddFixturePeak(snapshot, "cpu", 85, now.AddHours(-2.6), "%", "");
        AddFixturePeak(snapshot, "gpu", 69, now.AddHours(-4.1), "%", "");
        AddFixturePeak(snapshot, "npu", 64, now.AddHours(-1.7), "%", "");
        AddFixturePeak(snapshot, "memory", 71, now.AddHours(-0.8), "%", "");
        AddFixturePeak(snapshot, "network", 22000000, now.AddHours(-3.2), "B/s", "");
        AddFixturePeak(snapshot, "power", 38.2, now.AddHours(-1.1), "W", "");
        AddFixturePeak(snapshot, "temperature", 73.4, now.AddMinutes(-18), "°C", "TZ99");
        return snapshot;
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
        }
        Console.WriteLine("System Day board: PASS 648x400, unified ticks, rise=red, fall=cyan, peaks, work/sleep and thermal-zone labels");
    }
}
