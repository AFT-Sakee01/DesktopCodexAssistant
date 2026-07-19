using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// Three-pane layout for the power/thermal bar (1.0.5.80). Replaces the previous left-watts /
// middle-chips / right-battery arrangement, which showed only three figures and left the middle of
// the bar empty whenever no sensor exceeded the 70 C alert threshold — i.e. most of the time.
//
// Each pane is one stat block: label, headline value, gauge, supporting line. The thermal pane
// summarises every reporting zone (max / average / count / how many are over threshold) instead of
// drawing per-sensor chips, and the battery pane surfaces Windows' remaining-runtime estimate,
// which the widget already read but never displayed.
internal sealed partial class PowerThermalForm
{
    private struct ThermalSummary
    {
        public int ZoneCount;
        public int AlertCount;
        public double MaxCelsius;
        public double AvgCelsius;
    }

    private ThermalSummary BuildThermalSummary(List<ThermalReading> alerts)
    {
        ThermalSummary s = new ThermalSummary();
        s.AlertCount = alerts == null ? 0 : alerts.Count;

        double sum = 0.0;
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            ThermalReading r = this.cachedThermalReadings[i];
            // Zones that report 0 are present in WMI but not wired to a sensor on this device.
            if (r == null || string.IsNullOrEmpty(r.Name) || r.Celsius <= 0.0)
            {
                continue;
            }

            s.ZoneCount++;
            sum += r.Celsius;
            s.MaxCelsius = Math.Max(s.MaxCelsius, r.Celsius);
        }

        s.AvgCelsius = s.ZoneCount > 0 ? sum / s.ZoneCount : 0.0;
        return s;
    }

    private void DrawThreePaneContent(Graphics g, List<ThermalReading> thermalAlerts)
    {
        PowerReading power = GetPowerReading();
        ThermalSummary thermal = BuildThermalSummary(thermalAlerts);
        bool energySaver = IsEnergySaverDisplayActive(power);
        string modeText = FormatSystemPowerModeDisplayText(power, energySaver);
        bool charging = power.StatusKnown && power.IsCharging;

        float pad = S(8);
        float gap = S(8);
        float paneW = (this.Width - pad * 2.0f - gap * 2.0f) / 3.0f;
        float paneH = this.Height - pad * 2.0f;

        for (int i = 0; i < 3; i++)
        {
            RectangleF pane = new RectangleF(pad + i * (paneW + gap), pad, paneW, paneH);
            using (GraphicsPath p = RoundedRectangle(pane, S(4)))
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 88)))
            {
                g.FillPath(fill, p);
            }

            // Bands accumulate; the bar is only ~120px tall, so fixed offsets would let the value,
            // gauge and sub-line overlap.
            float x = pane.Left + S(7);
            float w = pane.Width - S(14);
            float y = pane.Top + S(2);
            RectangleF labelRect = new RectangleF(x, y, w, S(7));
            y += S(8);
            RectangleF valueRect = new RectangleF(x, y, w, S(19));
            y += S(21);
            RectangleF gaugeRect = new RectangleF(x, y, w, S(3));
            y += S(5);
            RectangleF subRect = new RectangleF(x, y, w, S(8));

            Font labelFont = GetPaneFont(8.5f);
            Font valueFont = GetPaneFont(21.0f);
            Font subFont = GetPaneFont(9.0f);

            if (i == 0)
            {
                DrawPaneText(g, charging ? "CHARGING" : "POWER DRAW", labelFont, DesignTokens.Colors.TextMuted, labelRect);
                DrawPaneText(g, FormatPaneWatts(power), valueFont, GetPowerModuleTextColor(charging), valueRect);
                DrawPaneGauge(g, gaugeRect, power.WattsKnown ? Math.Min(100.0, power.Watts / PaneWattsFullScale * 100.0) : 0.0,
                    charging ? DesignTokens.Colors.Success : DesignTokens.Colors.DangerStrong);
                DrawPaneText(g, modeText, subFont, GetSystemPowerModeColor(modeText, energySaver), subRect);
            }
            else if (i == 1)
            {
                int percent = power.BatteryPercentKnown ? Math.Max(0, Math.Min(100, power.BatteryPercent)) : 0;
                DrawPaneText(g, power.PluggedInKnown && power.IsPluggedIn ? "BATTERY · AC" : "BATTERY",
                    labelFont, DesignTokens.Colors.TextMuted, labelRect);
                DrawPaneText(g, power.BatteryPercentKnown ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "--",
                    valueFont, GetHiddenSafeNeutralColor(DesignTokens.Colors.TextStrong, DesignTokens.Colors.AccentAlt), valueRect);
                DrawPaneGauge(g, gaugeRect, percent, GetBatteryPercentColor(power.BatteryPercentKnown, percent));
                DrawPaneText(g, FormatPaneBatterySub(power, energySaver), subFont,
                    energySaver ? GetSystemPowerModeColor(modeText, true) : DesignTokens.Colors.Text, subRect);
            }
            else if (thermal.AlertCount > 0)
            {
                // Something is actually hot: the summary number is no longer the useful answer, so
                // this pane switches to naming the offending zones instead.
                DrawThermalRankingPane(g, pane, thermalAlerts, thermal, labelFont, subFont);
            }
            else
            {
                DrawPaneText(g, "MAX TEMP", labelFont, DesignTokens.Colors.TextMuted, labelRect);
                DrawPaneText(g, thermal.MaxCelsius > 0.0
                        ? thermal.MaxCelsius.ToString("0", CultureInfo.InvariantCulture) + "°C"
                        : "--",
                    valueFont, GetPaneTempColor(thermal.MaxCelsius), valueRect);
                DrawPaneGauge(g, gaugeRect, PaneTempPercent(thermal.MaxCelsius), GetPaneTempColor(thermal.MaxCelsius));
                DrawPaneText(g, FormatPaneThermalSub(thermal), subFont, DesignTokens.Colors.Text, subRect);
            }
        }
    }

    // Alert mode for the thermal pane: header line plus one row per over-threshold zone, hottest
    // first (GetThermalAlerts already sorts descending). Rows that do not fit collapse into a
    // trailing "+N" line so the count is never lost.
    private void DrawThermalRankingPane(
        Graphics g,
        RectangleF pane,
        List<ThermalReading> alerts,
        ThermalSummary thermal,
        Font labelFont,
        Font rowFont)
    {
        float x = pane.Left + S(7);
        float w = pane.Width - S(14);
        float y = pane.Top + S(2);

        DrawPaneText(
            g,
            thermal.AlertCount.ToString(CultureInfo.InvariantCulture) + " 区超 70°C",
            labelFont,
            DesignTokens.Colors.Warning,
            new RectangleF(x, y, w, S(7)));
        y += S(9);

        float rowH = S(9);
        int capacity = Math.Max(1, (int)Math.Floor((pane.Bottom - S(2) - y) / rowH));
        int shown = Math.Min(alerts.Count, capacity);
        bool overflow = alerts.Count > shown;
        if (overflow && shown > 0)
        {
            // Give the last slot to the "+N" line rather than one more zone.
            shown--;
        }

        float nameW = S(26);
        float tempW = S(24);
        for (int i = 0; i < shown; i++)
        {
            ThermalReading r = alerts[i];
            Color c = GetPaneTempColor(r.Celsius);
            RectangleF row = new RectangleF(x, y + rowH * i, w, rowH);
            DrawPaneText(g, FormatThermalSensorName(r.Name), rowFont, DesignTokens.Colors.Text,
                new RectangleF(row.Left, row.Top, nameW, row.Height));

            float barLeft = row.Left + nameW + S(2);
            float barRight = row.Right - tempW;
            if (barRight - barLeft > S(6))
            {
                DrawPaneGauge(
                    g,
                    new RectangleF(barLeft, row.Top + row.Height * 0.38f, barRight - barLeft, S(2.5f)),
                    PaneTempPercent(r.Celsius),
                    c);
            }

            using (SolidBrush b = new SolidBrush(c))
            {
                DrawFittedText(g, r.Celsius.ToString("0", CultureInfo.InvariantCulture) + "°", rowFont, b,
                    new RectangleF(row.Right - tempW, row.Top, tempW, row.Height), StringAlignment.Far);
            }
        }

        if (overflow)
        {
            // Range of the zones that did not fit, not the all-zone average — the average includes
            // the cool zones and would read as if the hidden ones were fine.
            int hidden = alerts.Count - shown;
            double hiddenMax = alerts[shown].Celsius;
            double hiddenMin = alerts[alerts.Count - 1].Celsius;
            string span = hiddenMax - hiddenMin >= 1.0
                ? hiddenMin.ToString("0", CultureInfo.InvariantCulture) + "-" + hiddenMax.ToString("0", CultureInfo.InvariantCulture) + "°"
                : hiddenMax.ToString("0", CultureInfo.InvariantCulture) + "°";
            DrawPaneText(
                g,
                "+" + hidden.ToString(CultureInfo.InvariantCulture) + " 区 " + span,
                rowFont,
                GetPaneTempColor(hiddenMax),
                new RectangleF(x, y + rowH * shown, w, rowH));
        }
    }

    // Typical sustained draw on this device family; the gauge is a relative sense of load, not a
    // calibrated maximum.
    private const double PaneWattsFullScale = 60.0;

    private static double PaneTempPercent(double celsius)
    {
        // 20-90 C mapped across the gauge: a raw 0-100 scale leaves every idle reading looking the
        // same, since the zones sit around 30 C.
        if (celsius <= 0.0)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(100.0, (celsius - 20.0) / 70.0 * 100.0));
    }

    private Color GetPaneTempColor(double celsius)
    {
        if (IsBurnInColorProtectionActive())
        {
            if (celsius >= 85.0) return DesignTokens.Colors.DangerStrong;
            if (celsius >= 70.0) return DesignTokens.Colors.Warning;
            return DesignTokens.Colors.Accent;
        }

        if (celsius >= 85.0) return DesignTokens.Colors.DangerStrong;
        if (celsius >= 70.0) return DesignTokens.Colors.Warning;
        if (celsius >= 55.0) return Color.FromArgb(176, 246, 152);
        if (celsius >= 40.0) return Color.FromArgb(120, 214, 168);
        return Color.FromArgb(96, 176, 222);
    }

    private static string FormatPaneWatts(PowerReading r)
    {
        return r.WattsKnown ? FormatWatts(r.Watts) : "-- W";
    }

    private static string FormatPaneBatterySub(PowerReading power, bool energySaver)
    {
        if (power.BatteryCarePauseKnown && power.BatteryCarePauseActive)
        {
            return "养护暂停";
        }

        if (power.RuntimeSecondsKnown)
        {
            int minutes = Math.Max(0, power.RuntimeSeconds / 60);
            int h = minutes / 60;
            int m = minutes % 60;
            string span = h > 0
                ? h.ToString(CultureInfo.InvariantCulture) + "h" + m.ToString("00", CultureInfo.InvariantCulture)
                : m.ToString(CultureInfo.InvariantCulture) + "m";
            return span + " 剩余";
        }

        if (power.StatusKnown && power.IsCharging)
        {
            return "充电中";
        }

        if (power.PluggedInKnown && power.IsPluggedIn)
        {
            return "外接供电";
        }

        return energySaver ? "节能" : "--";
    }

    private static string FormatPaneThermalSub(ThermalSummary s)
    {
        if (s.ZoneCount <= 0)
        {
            return "无传感器";
        }

        if (s.AlertCount > 0)
        {
            return s.AlertCount.ToString(CultureInfo.InvariantCulture) + " 区超 70°C";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "均 {0:0}° · {1} 区",
            s.AvgCelsius,
            s.ZoneCount);
    }

    private Font GetPaneFont(float logicalSize)
    {
        return this.fontCache.GetUi(Math.Max(6.0f, logicalSize * this.LayerScale), FontStyle.Bold);
    }

    private void DrawPaneText(Graphics g, string text, Font font, Color color, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using (SolidBrush b = new SolidBrush(color))
        {
            DrawFittedText(g, text, font, b, rect, StringAlignment.Near);
        }
    }

    private void DrawPaneGauge(Graphics g, RectangleF rect, double percent, Color fill)
    {
        if (rect.Width <= 1.0f)
        {
            return;
        }

        using (GraphicsPath track = RoundedRectangle(rect, rect.Height / 2.0f))
        using (SolidBrush tb = new SolidBrush(DesignTokens.White(30)))
        {
            g.FillPath(tb, track);
        }

        float w = (float)(rect.Width * Math.Max(0.0, Math.Min(100.0, percent)) / 100.0);
        if (w > 1.0f)
        {
            using (GraphicsPath p = RoundedRectangle(new RectangleF(rect.Left, rect.Top, w, rect.Height), rect.Height / 2.0f))
            using (SolidBrush b = new SolidBrush(DesignTokens.WithAlpha(fill, 235)))
            {
                g.FillPath(b, p);
            }
        }
    }
}
