using System;
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
            MainWidgetRenderVariant.Classic,
            MainWidgetRenderVariant.Typographic,
            MainWidgetRenderVariant.AmberHud,
            MainWidgetRenderVariant.WarmCard,
            MainWidgetRenderVariant.Phosphor
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
