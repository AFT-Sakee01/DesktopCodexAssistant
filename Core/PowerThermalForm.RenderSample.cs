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

    // Test-only comparison harness for the 419x120 wide-bar redesign: renders layout options A/B/C
    // under three alert scenarios so the user can pick a scheme from real pixels. Remove (together
    // with the option draw methods below) once a final layout is chosen.
    internal static void RenderWideBarOptionSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string[] scenarioTags = { "noalerts", "alerts2", "alerts5" };
        List<ThermalReading>[] scenarioAlerts =
        {
            new List<ThermalReading>(),
            BuildSampleThermalReadings(),
            new List<ThermalReading>
            {
                new ThermalReading { Name = "CPU", Celsius = 88.0, CriticalActive = true },
                new ThermalReading { Name = "GPU", Celsius = 79.0, CriticalActive = false },
                new ThermalReading { Name = "SSD", Celsius = 71.0, CriticalActive = false },
                new ThermalReading { Name = "主板", Celsius = 66.0, CriticalActive = false },
                new ThermalReading { Name = "WiFi", Celsius = 62.0, CriticalActive = false }
            }
        };

        for (int scenarioIndex = 0; scenarioIndex < scenarioTags.Length; scenarioIndex++)
        {
            string scenarioTag = scenarioTags[scenarioIndex];
            List<ThermalReading> alerts = scenarioAlerts[scenarioIndex];
            for (char option = 'a'; option <= 'c'; option++)
            {
                WidgetSettings settings = WidgetSettings.CreateDefaults();
                settings.PowerThermalWidth = 419;
                settings.PowerThermalHeight = 120;
                settings.PowerThermalAutoSizeEnabled = false;
                settings.Normalize();

                using (PowerThermalForm form = new PowerThermalForm(settings))
                {
                    form.SetLayerScale(2.0f);
                    form.MaximumSize = new Size(4000, 4000);
                    form.cachedPowerReading = BuildSamplePowerReading();
                    form.cachedThermalReadings = alerts;
                    foreach (ThermalReading reading in alerts)
                    {
                        form.thermalAlertNames.Add(reading.Name);
                    }

                    form.Size = new Size(419, 120);

                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(DesignTokens.Colors.AppBackground);
                        form.DrawWideBarOptionFrame(g, option, alerts);
                        string path = Path.Combine(
                            outputDir,
                            "powerthermal-wide-" + option + "-" + scenarioTag + ".png");
                        bitmap.Save(path, ImageFormat.Png);
                        Console.WriteLine("option " + option + " " + scenarioTag + " -> " + path);
                    }
                }
            }
        }
    }

    private void DrawWideBarOptionFrame(Graphics g, char option, List<ThermalReading> alerts)
    {
        ConfigureGraphics(g);
        using (System.Drawing.Drawing2D.GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float contentTop = S(5);
        float contentHeight = Math.Max(10, this.Height - S(10));
        switch (option)
        {
            case 'b':
                DrawWideBarOptionB(g, alerts, contentTop, contentHeight);
                break;
            case 'c':
                DrawWideBarOptionC(g, alerts, contentTop, contentHeight);
                break;
            default:
                DrawWideBarContent(g, alerts, contentTop, contentHeight);
                break;
        }
    }

    // Option B: top band = power + battery, bottom band = full-width single chip row (left-aligned).
    private void DrawWideBarOptionB(Graphics g, List<ThermalReading> alerts, float contentTop, float contentHeight)
    {
        float pad = S(10);
        RectangleF contentRect = new RectangleF(pad, contentTop, Math.Max(10.0f, this.Width - pad * 2.0f), contentHeight);
        float chipRowHeight = S(20);
        float topHeight = Math.Max(S(24), contentRect.Height - chipRowHeight - S(4));
        RectangleF powerRect = new RectangleF(contentRect.Left, contentRect.Top, Math.Min(S(70), contentRect.Width * 0.4f), topHeight);
        float batteryWidth = Math.Min(S(60), contentRect.Width * 0.35f);
        RectangleF batteryRect = new RectangleF(contentRect.Right - batteryWidth, contentRect.Top, batteryWidth, topHeight);
        DrawPowerModule(g, powerRect);
        DrawBatteryModule(g, batteryRect);
        if (alerts.Count > 0)
        {
            RectangleF chipsRect = new RectangleF(
                contentRect.Left,
                contentRect.Top + topHeight + S(4),
                contentRect.Width,
                Math.Max(S(13), contentRect.Bottom - (contentRect.Top + topHeight + S(4))));
            DrawThermalAlertsRowLeft(g, chipsRect, alerts);
        }
    }

    // Option C: everything on a single row - power | battery | right-aligned single chip row.
    private void DrawWideBarOptionC(Graphics g, List<ThermalReading> alerts, float contentTop, float contentHeight)
    {
        float pad = S(10);
        RectangleF contentRect = new RectangleF(pad, contentTop, Math.Max(10.0f, this.Width - pad * 2.0f), contentHeight);
        float powerWidth = Math.Min(S(56), contentRect.Width * 0.28f);
        RectangleF powerRect = new RectangleF(contentRect.Left, contentRect.Top, powerWidth, contentRect.Height);
        float batteryWidth = Math.Min(S(60), contentRect.Width * 0.30f);
        RectangleF batteryRect = new RectangleF(powerRect.Right + S(6), contentRect.Top, batteryWidth, contentRect.Height);
        DrawPowerModule(g, powerRect);
        DrawBatteryModule(g, batteryRect);
        if (alerts.Count > 0)
        {
            float chipHeight = GetThermalVerticalChipHeight();
            float chipsLeft = batteryRect.Right + S(8);
            if (contentRect.Right - chipsLeft >= S(30))
            {
                RectangleF chipsRect = new RectangleF(
                    chipsLeft,
                    contentRect.Top + Math.Max(0.0f, (contentRect.Height - chipHeight) / 2.0f),
                    contentRect.Right - chipsLeft,
                    chipHeight);
                DrawThermalAlertsWrapped(g, chipsRect, alerts);
            }
        }
    }

    // Left-aligned single-row chip flow with a trailing "+N" chip for hidden alerts.
    private void DrawThermalAlertsRowLeft(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int maxVisible = Math.Min(GetMaxVisibleThermalAlerts(), total);
        float gap = S(4);
        float chipHeight = Math.Min(GetThermalVerticalChipHeight(), bounds.Height);
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height - chipHeight) / 2.0f);
        using (Font chipFont = CreateThermalChipFont())
        {
            List<float> lefts = new List<float>();
            List<float> widths = new List<float>();
            float nextLeft = bounds.Left;
            int placed = 0;
            while (placed < maxVisible)
            {
                string text = FormatThermalSensorName(alerts[placed].Name);
                float width = Math.Min(bounds.Width, MeasureThermalChipWidth(text, alerts[placed].CriticalActive, chipFont));
                if (nextLeft + width > bounds.Right && nextLeft > bounds.Left)
                {
                    break;
                }

                lefts.Add(nextLeft);
                widths.Add(width);
                nextLeft += width + gap;
                placed++;
            }

            bool hasMore = total > placed;
            float moreLeft = bounds.Left;
            float moreWidth = 0.0f;
            if (hasMore)
            {
                while (true)
                {
                    string moreText = "+" + (total - placed).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    moreWidth = Math.Min(bounds.Width, MeasureThermalChipWidth(moreText, false, chipFont));
                    if (nextLeft + moreWidth <= bounds.Right || placed <= 0)
                    {
                        moreLeft = Math.Min(nextLeft, bounds.Right - moreWidth);
                        break;
                    }

                    placed--;
                    nextLeft = lefts[placed];
                    lefts.RemoveAt(placed);
                    widths.RemoveAt(placed);
                }
            }

            for (int i = 0; i < placed; i++)
            {
                RectangleF chipRect = new RectangleF(lefts[i], chipTop, widths[i], chipHeight);
                DrawThermalChip(g, chipRect, FormatThermalSensorName(alerts[i].Name), alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
            }

            if (hasMore)
            {
                double hiddenMaxTemp = 0.0;
                for (int i = placed; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                string finalMoreText = "+" + (total - placed).ToString(System.Globalization.CultureInfo.InvariantCulture);
                DrawThermalChip(g, new RectangleF(moreLeft, chipTop, moreWidth, chipHeight), finalMoreText, hiddenMaxTemp, false, chipFont);
            }
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
