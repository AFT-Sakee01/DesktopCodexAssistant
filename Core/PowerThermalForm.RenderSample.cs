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

    // Scenario harness (--render-powerthermal --scenarios): renders the fixed-size bar in the three
    // states that matter for the three-pane layout — an idle machine (the common case, where the
    // old layout showed nothing), a couple of hot zones, and a heavy alert load. Thermal sets
    // mirror this device's real ~14 reporting zones so the summary pane's counts are realistic.
    internal static void RenderWideBarScenarioSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string[] scenarioTags = { "idle", "alerts2", "alerts5" };
        List<ThermalReading>[] scenarioReadings =
        {
            BuildDeviceThermalZones(0),
            BuildDeviceThermalZones(2),
            BuildDeviceThermalZones(5)
        };
        bool[] onBattery = { true, true, false };

        for (int i = 0; i < scenarioTags.Length; i++)
        {
            List<ThermalReading> readings = scenarioReadings[i];
            WidgetSettings settings = WidgetSettings.Load();
            settings.Normalize();

            using (PowerThermalForm form = new PowerThermalForm(settings))
            {
                form.SetLayerScale(2.0f);
                form.MaximumSize = new Size(4000, 4000);
                form.cachedPowerReading = BuildScenarioPowerReading(onBattery[i]);
                form.cachedThermalReadings = readings;
                for (int r = 0; r < readings.Count; r++)
                {
                    if (readings[r].Celsius >= 70.0)
                    {
                        form.thermalAlertNames.Add(readings[r].Name);
                    }
                }

                form.Size = form.GetDesiredSize();

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawContent(g);
                    string path = Path.Combine(outputDir, "powerthermal-" + scenarioTags[i] + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(scenarioTags[i] + " -> " + path + " (" + form.Width + "x" + form.Height + ")");
                }
            }
        }
    }

    // 14 reporting zones like the real device, with the requested number pushed past the 70 C alert
    // threshold so the summary pane's "N 区超 70°C" line can be checked.
    private static List<ThermalReading> BuildDeviceThermalZones(int hotCount)
    {
        double[] idle = { 33.1, 32.9, 32.6, 32.6, 32.3, 32.2, 32.2, 32.1, 32.0, 31.9, 30.5, 30.2, 29.8, 28.4 };
        string[] names = { "TZ99", "TZ2", "TZ1", "TZ5", "TZ4", "TZ0", "TZ11", "TZ3", "TZ6", "TZ33", "TZ37", "TZ91", "TZ7", "TZ8" };
        double[] hot = { 88.0, 79.0, 74.5, 72.0, 70.5 };

        List<ThermalReading> list = new List<ThermalReading>();
        for (int i = 0; i < idle.Length; i++)
        {
            double c = i < hotCount ? hot[Math.Min(i, hot.Length - 1)] : idle[i];
            list.Add(new ThermalReading { Name = names[i], Celsius = c, CriticalActive = c >= 85.0 });
        }

        return list;
    }

    private static PowerReading BuildScenarioPowerReading(bool onBattery)
    {
        PowerReading reading = new PowerReading();
        reading.StatusKnown = true;
        reading.IsCharging = !onBattery;
        reading.PluggedInKnown = true;
        reading.IsPluggedIn = !onBattery;
        reading.WattsKnown = true;
        reading.Watts = onBattery ? 12.4 : 34.7;
        reading.BatteryPercentKnown = true;
        reading.BatteryPercent = onBattery ? 79 : 46;
        reading.SystemPowerModeKnown = true;
        reading.SystemPowerModeText = onBattery ? "平衡" : "性能";
        reading.EnergySaverKnown = true;
        reading.EnergySaverEnabled = false;
        // Windows only estimates runtime while discharging; on AC this stays unknown and the pane
        // falls back to a charging/AC label.
        reading.RuntimeSecondsKnown = onBattery;
        reading.RuntimeSeconds = onBattery ? 13320 : 0;
        return reading;
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
