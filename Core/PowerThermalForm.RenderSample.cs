using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Test-only render harness for --render-powerthermal: paints one representative frame of each
// PowerThermalRenderVariant to a PNG for
// visual review, mirroring the CodexRadar/ConnectionCheck/NetworkMonitor render harnesses.
internal sealed partial class PowerThermalForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        PowerThermalRenderVariant[] variants =
        {
            PowerThermalRenderVariant.Classic
        };

        foreach (PowerThermalRenderVariant variant in variants)
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.PowerThermalRenderVariant = variant;
            settings.Normalize();

            using (PowerThermalForm form = new PowerThermalForm(settings))
            {
                form.SetLayerScale(2.0f);
                form.MaximumSize = new Size(4000, 4000);
                // PowerThermalWidth/Height are already the real physical pixel size; an earlier *2
                // here rendered a double-size canvas that hid true-width truncation. Same fix as
                // CodexRadarForm.RenderSample.cs.
                form.cachedPowerReading = BuildSamplePowerReading();
                form.cachedThermalReadings = BuildSampleThermalReadings();
                form.thermalAlertNames.Add("CPU");
                form.thermalAlertNames.Add("GPU");
                form.Size = form.GetDesiredSize();

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawContent(g);
                    string path = Path.Combine(outputDir, "powerthermal-" + variant.ToString().ToLowerInvariant() + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(variant.ToString() + " -> " + path);
                }
            }
        }
    }

    // Current-mode sample: real settings.ini (size/variant/transparency/alert rows). Live power and
    // thermal readings are runtime sampler state with no disk cache, so the frame reuses the
    // synthetic readings while geometry and styling stay the user's real configuration.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (PowerThermalForm form = new PowerThermalForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.cachedPowerReading = BuildSamplePowerReading();
            form.cachedThermalReadings = BuildSampleThermalReadings();
            form.Size = form.GetDesiredSize();
            RenderSampleSupport.SaveComposited(
                outputDir,
                "powerthermal-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawPowerThermalWindow);
        }
    }

    private static PowerReading BuildSamplePowerReading()
    {
        PowerReading reading = new PowerReading();
        reading.StatusKnown = true;
        reading.IsCharging = false;
        reading.PluggedInKnown = true;
        reading.IsPluggedIn = false;
        reading.WattsKnown = true;
        reading.Watts = 8.6;
        reading.BatteryPercentKnown = true;
        reading.BatteryPercent = 23;
        reading.SystemPowerModeKnown = true;
        reading.SystemPowerModeText = "平衡";
        reading.EnergySaverKnown = false;
        reading.EnergySaverEnabled = false;
        return reading;
    }

    private static List<ThermalReading> BuildSampleThermalReadings()
    {
        return new List<ThermalReading>
        {
            new ThermalReading { Name = "CPU", Celsius = 82.0, CriticalActive = true },
            new ThermalReading { Name = "GPU", Celsius = 71.0, CriticalActive = false }
        };
    }
}
