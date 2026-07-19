using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

// Test-only render harness for --render-widget: paints one representative frame of each
// MainWidgetRenderVariant (Classic plus the four OLED-safe schemes added in 1.0.3.44) to a PNG for
// visual review, mirroring the other windows' render harnesses. Only CPU and Memory are enabled so
// the harness does not need to fabricate disk/network/GPU/NPU history data.
internal sealed partial class WidgetForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        MainWidgetRenderVariant[] variants =
        {
            MainWidgetRenderVariant.Classic
        };

        using (PdhSampler sampler = new PdhSampler())
        using (EventWaitHandle stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset))
        {
            foreach (MainWidgetRenderVariant variant in variants)
            {
                WidgetSettings settings = WidgetSettings.CreateDefaults();
                settings.MainWidgetRenderVariant = variant;
                settings.ShowDisk = false;
                settings.ShowNetwork = false;
                settings.Normalize();

                using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, false))
                {
                    form.SetLayerScale(2.0f);
                    form.MaximumSize = new Size(4000, 4000);
                    form.Size = new Size(220 * 2, 120 * 2);
                    form.snapshot = BuildSampleSnapshot();
                    form.cpuHistory.Clear();
                    form.cpuHistory.AddRange(new double[] { 20, 35, 42, 55, 48, 61, 58 });
                    form.memoryHistory.Clear();
                    form.memoryHistory.AddRange(new double[] { 40, 42, 45, 44, 47, 46, 48 });

                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(DesignTokens.Colors.AppBackground);
                        form.DrawWidgetContent(g);
                        string path = Path.Combine(outputDir, "widget-" + variant.ToString().ToLowerInvariant() + ".png");
                        bitmap.Save(path, ImageFormat.Png);
                        Console.WriteLine(variant.ToString() + " -> " + path);
                    }
                }
            }
        }
    }

    internal static void RenderAlertPolicySamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        using (PdhSampler sampler = new PdhSampler())
        using (EventWaitHandle stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset))
        {
            bool[] enabledStates = { true, false };
            for (int i = 0; i < enabledStates.Length; i++)
            {
                WidgetSettings settings = WidgetSettings.CreateDefaults();
                settings.ShowDisk = false;
                settings.ShowNetwork = false;
                settings.AlertTestEnabled = true;
                settings.AlertQuotaEnabled = enabledStates[i];
                settings.Normalize();
                using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, false))
                {
                    form.SetLayerScale(2.0f);
                    form.MaximumSize = new Size(4000, 4000);
                    form.Size = new Size(220 * 2, 120 * 2);
                    form.snapshot = BuildSampleSnapshot();
                    form.cpuHistory.AddRange(new double[] { 20, 35, 42, 55, 48, 61, 58 });
                    form.memoryHistory.AddRange(new double[] { 40, 42, 45, 44, 47, 46, 48 });
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(DesignTokens.Colors.AppBackground);
                        form.DrawWidgetContent(g);
                        string suffix = enabledStates[i] ? "on" : "off";
                        string path = Path.Combine(outputDir, "widget-alert-quota-" + suffix + ".png");
                        bitmap.Save(path, ImageFormat.Png);
                        Console.WriteLine("Quota alert " + suffix + " -> " + path);
                    }
                }
            }
        }
    }

    // Current-mode sample: real settings.ini (size/variant/transparency/enabled rows) with one
    // live PDH sample so the frame shows genuine hardware numbers, drawn through the real
    // DrawWidget pipeline and composited like the on-screen layered window.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (PdhSampler sampler = new PdhSampler())
        using (EventWaitHandle stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset))
        using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, false))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.Width, settings.Height);
            try
            {
                Thread.Sleep(1100);
                form.snapshot = sampler.Sample();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                form.snapshot = BuildSampleSnapshot();
            }

            if (form.cpuHistory.Count == 0)
            {
                form.cpuHistory.Add(form.snapshot.CpuPercent);
            }

            if (form.memoryHistory.Count == 0)
            {
                form.memoryHistory.Add(form.snapshot.MemoryPercent);
            }

            RenderSampleSupport.SaveComposited(
                outputDir,
                "widget-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawWidget);
        }
    }

    // Preview for the integrated layout (--render-widget --integrated): the metric grid with the
    // power strip merged into the bottom, at the proposed window height. The child PowerThermalForm
    // is normally created in OnShown, which the harness never reaches, so one is constructed and
    // seeded here — its constructor is headless-safe (timers only start on show).
    // Test-only: when set, the integrated preview is put through the hidden-mode inversion so the
    // burn-in result can be inspected element by element.
    internal static bool RenderHiddenModeProof;

    internal static void RenderIntegratedPreview(string outputDir, int previewHeight)
    {
        RenderIntegratedPreview(outputDir, previewHeight, "widget-integrated.png");
    }

    internal static void RenderIntegratedPreview(string outputDir, int previewHeight, string fileName)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.Load();
        settings.Normalize();

        using (PdhSampler sampler = new PdhSampler())
        using (EventWaitHandle stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset))
        using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, false))
        using (PowerThermalForm power = new PowerThermalForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.Width, previewHeight);
            form.snapshot = BuildDensitySampleSnapshot();
            SeedDensityHistories(form);

            power.SeedPreviewReadings();
            form.powerThermalForm = power;

            // Arm two guards so the badge row renders in its "something is held open" state; the
            // all-clear state is what a run without this seeding produces.
            GuardRuntime guardRuntime = new GuardRuntime(delegate { return (bool?)true; });
            guardRuntime.BackdateSleepGuardForRenderSample(DateTime.UtcNow.AddMinutes(-134));
            guardRuntime.StartDisplayGuard(DateTime.UtcNow);
            form.GuardRuntimeOverrideForRenderSample = guardRuntime;

            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                form.DrawWidgetContent(g);
                if (RenderHiddenModeProof)
                {
                    // Same post-process the layered window runs in hidden mode, so the proof shows
                    // what burn-in protection actually turns each element into.
                    BurnInProtection.ApplyHiddenModeColorProtection(bitmap);
                }

                string path = Path.Combine(outputDir, fileName);
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine("integrated -> " + path + " (" + form.Width + "x" + form.Height + ")");
            }
        }
    }

    private static void SeedDensityHistories(WidgetForm form)
    {
        form.cpuHistory.Clear();
        form.cpuHistory.AddRange(new double[] { 18, 24, 46, 38, 52, 61, 44, 33, 29, 41, 55, 32 });
        form.memoryHistory.Clear();
        form.memoryHistory.AddRange(new double[] { 58, 59, 61, 60, 62, 63, 63, 62, 64, 63, 63, 63 });
        form.diskWriteHistory.Clear();
        form.diskWriteHistory.AddRange(new double[] { 2, 44, 12, 5, 61, 88, 23, 9, 4, 31, 17, 7 });
        form.networkHistory.Clear();
        form.networkHistory.AddRange(new double[] { 12, 38, 91, 64, 22, 15, 47, 83, 55, 28, 19, 36 });
        form.gpuHistory.Clear();
        form.gpuHistory.AddRange(new double[] { 6, 9, 14, 11, 22, 18, 13, 9, 7, 16, 12, 11 });
        form.npuHistory.Clear();
        form.npuHistory.AddRange(new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    }

    private static PerfSnapshot BuildDensitySampleSnapshot()
    {
        PerfSnapshot s = new PerfSnapshot();
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
        s.DiskWriteBytesPerSecond = 4.8 * 1024 * 1024 / 8;
        s.DiskReadBytesPerSecond = 1.0 * 1024 / 8;
        s.NetworkConnected = true;
        s.NetworkIsWifi = true;
        s.NetworkName = "SSID-9A9449";
        s.NetworkSentBytesPerSecond = 17.0 * 1024 / 8;
        s.NetworkReceivedBytesPerSecond = 57.0 * 1024 / 8;
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
        return s;
    }

    private static PerfSnapshot BuildSampleSnapshot()
    {
        PerfSnapshot snapshot = new PerfSnapshot();
        snapshot.CpuName = "Snapdragon X Elite";
        snapshot.CpuPercent = 58.0;
        snapshot.CpuFrequencyGhz = 3.4;
        snapshot.CpuBaseFrequencyGhz = 3.8;
        snapshot.MemoryUsedGb = 15.4;
        snapshot.MemoryTotalGb = 32.0;
        snapshot.MemoryPercent = 48.0;
        return snapshot;
    }
}
