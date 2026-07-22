using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Test-only render harness for --render-tilecolumn. Paints the ten tiles (normal and hovered
// states) and every one of the ten expanded panels to PNGs so the layout can be reviewed
// without running the app: none of these windows is reachable from the Start menu and the panel only
// exists while the cursor is on its tile.
//
// The tiles are separate windows now, so the "column" image is composited here from the ten tile
// bitmaps at their auto-column offsets — that is what the default placement actually looks like.
internal sealed partial class MetricTileForm
{
    internal static void RenderSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        MetricTileFeed feed = BuildSampleFeed();

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();

        RenderColumn(outputDir, settings, feed, "tilecolumn.png", -1, BurnInVisualLevel.Normal, false);
        RenderColumn(outputDir, settings, feed, "tilecolumn-hover.png", 0, BurnInVisualLevel.Normal, false);
        RenderColumn(outputDir, settings, feed, "tilecolumn-burnin-level1.png", -1, BurnInVisualLevel.LevelOne, false);
        RenderColumn(outputDir, settings, feed, "tilecolumn-burnin-level1-hover.png", 0, BurnInVisualLevel.LevelOne, true);
        RenderColumn(outputDir, settings, feed, "tilecolumn-burnin-level2.png", -1, BurnInVisualLevel.LevelTwo, false);
        RenderColumn(outputDir, settings, feed, "tilecolumn-burnin-level2-hover.png", 0, BurnInVisualLevel.LevelTwo, true);

        WidgetSettings large = WidgetSettings.CreateDefaults();
        large.MainWidgetTileLargeModeEnabled = true;
        large.Normalize();
        RenderColumn(outputDir, large, feed, "tilecolumn-large.png", -1, BurnInVisualLevel.Normal, false);

        for (int i = 0; i < MetricTileModel.AllOrder.Length; i++)
        {
            MetricTileId id = MetricTileModel.AllOrder[i];
            using (MetricTileExpandForm panel = new MetricTileExpandForm(settings))
            {
                panel.MaximumSize = new Size(4000, 4000);
                panel.Size = panel.GetDesiredSize();
                panel.PrepareForRenderSample(id, feed);
                using (Bitmap bitmap = new Bitmap(panel.Width, panel.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    panel.DrawPanel(g);
                    string path = Path.Combine(outputDir, "tileexpand-" + id.ToString().ToLowerInvariant() + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(id + " -> " + path + " (" + panel.Width + "x" + panel.Height + ")");
                }

                if (id == MetricTileId.Cpu)
                {
                    panel.SetBurnInVisualState(BurnInVisualLevel.LevelTwo, false);
                    using (Bitmap bitmap = new Bitmap(panel.Width, panel.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(DesignTokens.Colors.AppBackground);
                        panel.DrawPanel(g);
                        BurnInProtection.ApplyLuminance(bitmap, BurnInProtection.LevelOneLuminancePercent);
                        string path = Path.Combine(outputDir, "tileexpand-cpu-burnin-level2.png");
                        bitmap.Save(path, ImageFormat.Png);
                        Console.WriteLine("CPU level two -> " + path + " (" + panel.Width + "x" + panel.Height + ")");
                    }
                }
            }
        }
    }

    private static void RenderColumn(
        string outputDir,
        WidgetSettings settings,
        MetricTileFeed feed,
        string fileName,
        int hoveredIndex,
        BurnInVisualLevel burnInVisualLevel,
        bool restoreRightGroupBrightness)
    {
        int px = settings.MainWidgetTileLargeModeEnabled ? TileLargePixels : TileCompactPixels;
        int gap = GetTileGapPixels(settings);
        int stride = px + gap;
        int height = px * MetricTileModel.AllTileCount + gap * (MetricTileModel.AllTileCount - 1);

        using (Bitmap bitmap = new Bitmap(px, height, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(DesignTokens.Colors.AppBackground);
            for (int i = 0; i < MetricTileModel.AllTileCount; i++)
            {
                using (MetricTileForm tile = new MetricTileForm(settings, i))
                {
                    tile.MaximumSize = new Size(4000, 4000);
                    tile.Size = tile.GetDesiredSize();
                    tile.UpdateFeedForRenderSample(feed);
                    tile.SetHoveredForRenderSample(i == hoveredIndex);
                    tile.SetBurnInVisualStateForRenderSample(burnInVisualLevel, restoreRightGroupBrightness);
                    using (Bitmap one = new Bitmap(px, px, PixelFormat.Format32bppPArgb))
                    using (Graphics tg = Graphics.FromImage(one))
                    {
                        tg.Clear(Color.Transparent);
                        tile.DrawTileContent(tg);
                        if (burnInVisualLevel != BurnInVisualLevel.Normal && !restoreRightGroupBrightness)
                        {
                            BurnInProtection.ApplyLuminance(one, BurnInProtection.LevelOneLuminancePercent);
                        }

                        g.DrawImage(one, 0, i * stride);
                    }
                }
            }

            string path = Path.Combine(outputDir, fileName);
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine(fileName + " -> " + path + " (" + px + "x" + height + ")");
        }
    }

    // Same figures the design proposal used, so the rendered PNGs can be compared against it
    // directly. Deliberately fixed rather than sampled: a harness that reads live hardware produces
    // a different picture on every run.
    private static MetricTileFeed BuildSampleFeed()
    {
        MetricTileFeed feed = new MetricTileFeed();
        PerfSnapshot s = feed.Snapshot;
        s.CpuName = "Snapdragon(R) X2 Elite Extreme - X2E94100";
        s.CpuPercent = 32.0;
        s.CpuFrequencyGhz = 4.15;
        s.CpuBaseFrequencyGhz = 4.45;
        s.CpuCorePercents = new double[] { 34, 12, 88, 21, 45, 9, 62, 17, 28, 71, 15, 39, 8, 52, 24, 11, 96, 19 };
        s.MemoryManufacturer = "SAMSUNG";
        s.MemorySpeedMtps = 9523;
        s.MemoryUsedGb = 30.2;
        s.MemoryTotalGb = 47.6;
        s.MemoryPercent = 63.0;
        s.MemoryHardwareReservedPercent = 3.6;
        s.PageFileUsedGb = 0.09;
        s.PageFileTotalGb = 3.0;
        s.DiskVolumeLabel = "C/D";
        s.DiskUsedGb = 283.0;
        s.DiskTotalGb = 954.0;
        s.DiskPercent = 29.7;
        s.DiskWriteBytesPerSecond = 5.0 * 1000.0 * 1000.0 / 8.0;
        s.DiskReadBytesPerSecond = 1.0 * 1000.0 / 8.0;
        s.NetworkConnected = true;
        s.NetworkIsWifi = true;
        s.NetworkName = "SSID-9A9449";
        s.NetworkSentBytesPerSecond = 17.0 * 1000.0 / 8.0;
        s.NetworkReceivedBytesPerSecond = 58.0 * 1000.0 / 8.0;
        s.NetworkRssiKnown = true;
        s.NetworkRssiDbm = -39;
        s.GpuName = "Qualcomm(R) Adreno(TM) X2-90 GPU";
        s.GpuPercent = 11.0;
        s.GpuMemoryUsedGb = 1.3;
        s.GpuMemoryTotalGb = 47.6;
        s.GpuMemoryPercent = 2.7;
        s.NpuName = "Snapdragon(R) X2 Elite Extreme NPU";
        s.NpuPercent = 0.0;
        s.NpuMemoryUsedGb = 0.7;
        s.NpuMemoryTotalGb = 47.6;
        s.NpuMemoryPercent = 1.5;

        feed.CpuHistory = Hist(18, 24, 46, 38, 52, 61, 44, 33, 29, 41, 55, 48, 36, 30, 44, 58, 51, 39, 33, 32);
        feed.MemoryHistory = Hist(58, 59, 61, 60, 62, 63, 63, 62, 64, 63, 63, 63, 62, 63, 64, 63, 63, 62, 63, 63);
        feed.DiskWriteHistory = Hist(200, 4400, 1200, 500, 6100, 8800, 2300, 900, 400, 3100, 1700, 700, 2600, 1200, 900, 3300, 2100, 800, 1100, 5000);
        feed.DiskReadHistory = Hist(5, 10, 5, 5, 20, 30, 10, 5, 5, 10, 5, 5, 10, 5, 5, 10, 5, 5, 5, 1);
        feed.NetworkReceivedHistory = Hist(12, 38, 91, 64, 22, 15, 47, 83, 55, 28, 19, 36, 42, 58, 49, 61, 44, 37, 52, 58);
        feed.NetworkSentHistory = Hist(4, 8, 22, 15, 6, 3, 11, 25, 14, 7, 5, 9, 12, 17, 13, 19, 11, 8, 14, 17);
        feed.GpuHistory = Hist(6, 9, 14, 11, 22, 18, 13, 9, 7, 16, 12, 11, 10, 13, 12, 15, 11, 9, 12, 11);
        feed.GpuMemoryHistory = Hist(2.7, 2.7, 2.8, 2.7, 2.9, 2.8, 2.7, 2.7, 2.6, 2.8, 2.7, 2.7, 2.7, 2.8, 2.7, 2.7, 2.7, 2.6, 2.7, 2.7);
        feed.NpuHistory = Hist(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        feed.NpuMemoryHistory = Hist(1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5);

        PowerStripSnapshot power = new PowerStripSnapshot();
        power.PowerKnown = true;
        power.WattsKnown = true;
        power.Watts = 12.4;
        power.BatteryPercentKnown = true;
        power.BatteryPercent = 79;
        power.RuntimeSecondsKnown = true;
        power.RuntimeSeconds = 3 * 3600 + 42 * 60;
        power.PowerModeText = "平衡";
        power.ZoneCount = 14;
        power.AlertCount = 0;
        power.MaxCelsius = 33.0;
        power.AvgCelsius = 32.0;
        power.HotZones = new List<PowerStripZone>();
        double[] zoneTemps = { 33, 32, 31, 33, 32, 31, 30, 32, 33, 32, 31, 32, 31, 32 };
        for (int i = 0; i < zoneTemps.Length; i++)
        {
            power.HotZones.Add(new PowerStripZone
            {
                Name = "Zone" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Celsius = zoneTemps[i]
            });
        }

        feed.Power = power;

        // Two guards armed, matching the proposal's "something is held open" state.
        GuardRuntime runtime = new GuardRuntime(delegate { return (bool?)true; });
        runtime.BackdateSleepGuardForRenderSample(DateTime.UtcNow.AddMinutes(-134));
        runtime.StartDisplayGuard(DateTime.UtcNow);
        feed.Guards = MetricTileModel.BuildGuardEntries(runtime, DateTime.UtcNow);
        feed.CodexRadar = BuildSampleRadar(CodexRadarSoftwareMode.Codex);
        feed.ClaudeRadar = BuildSampleRadar(CodexRadarSoftwareMode.Claude);
        return feed;
    }

    private static RadarTileSnapshot BuildSampleRadar(CodexRadarSoftwareMode family)
    {
        bool claude = family == CodexRadarSoftwareMode.Claude;
        RadarTileSnapshot r = RadarTileSnapshot.CreateEmpty(family);
        r.ModelName = claude ? "Claude" : "GPT-5.6";
        r.QuotaKnown = true;
        r.FiveHourPercent = claude ? 41 : 88;
        r.FiveHourResetKnown = true;
        r.FiveHourResetLocal = DateTime.Now.AddHours(claude ? 1.4 : 3.2);
        r.WeeklyPercent = claude ? 77 : 62;
        r.WeeklyResetKnown = true;
        r.WeeklyResetLocal = DateTime.Now.AddHours(claude ? 54 : 31);
        double[] burn = claude
            ? new double[] { 100, 99, 97, 95, 94, 92, 90, 88, 86, 85, 83, 81, 80, 79, 78, 77 }
            : new double[] { 100, 96, 92, 89, 85, 81, 78, 74, 71, 69, 68, 66, 65, 64, 63, 62 };
        for (int i = 0; i < burn.Length; i++)
        {
            r.WeeklyBurnRemaining.Add(burn[i]);
        }

        r.BurnRateKnown = true;
        r.BurnPercentPerHour = claude ? 1.4 : 2.4;
        r.RunwayHours = claude ? 55 : 26;
        r.HoursToReset = claude ? 54 : 31;
        if (!claude)
        {
            r.IqKnown = true;
            r.Iq = 141;
            r.IqUpdatedKnown = true;
            r.IqUpdatedLocal = DateTime.Now.AddMinutes(-42);
            r.EfficiencyKnown = true;
            r.TokenEfficiencyPercent = 78;
            r.TimeEfficiencyPercent = 64;
        }
        return r;
    }

    private static List<double> Hist(params double[] values)
    {
        return new List<double>(values);
    }
}
