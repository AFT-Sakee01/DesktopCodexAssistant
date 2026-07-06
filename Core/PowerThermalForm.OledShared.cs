using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

// Shared data extraction for the four OLED-safe restyle schemes (Typographic, AmberHud, WarmCard,
// Phosphor) added in 1.0.3.44. Severity is derived directly from the underlying percent/charging/
// critical fields rather than reusing a Classic color helper's specific accent hue - GetBatteryPercentColor
// in particular returns a hardcoded blue for a >=97% full battery in normal (non-burn-in) mode, which
// these no-blue schemes must not inherit.
internal sealed partial class PowerThermalForm
{
    private struct OledPowerItem
    {
        public string Label;
        public string Value;
        public OledVariantPainting.Severity Severity;
    }

    private OledPowerItem GetOledPowerItem()
    {
        PowerReading reading = GetPowerReading();
        bool charging = reading.StatusKnown && reading.IsCharging;
        OledPowerItem item = new OledPowerItem();
        item.Label = charging ? "充电中" : "功耗";
        // This window's default size (120x110) is the tightest of the six, and its OLED-safe schemes
        // split it into two side-by-side cells - so the compact value drops FormatWatts' one-decimal
        // form ("45.0 W") for a bare rounded watt count ("45W") to leave room at a readable font size.
        item.Value = reading.WattsKnown ? Math.Round(reading.Watts).ToString(CultureInfo.InvariantCulture) + "W" : "--W";
        item.Severity = charging ? OledVariantPainting.Severity.Good : OledVariantPainting.Severity.Neutral;
        return item;
    }

    private OledPowerItem GetOledBatteryItem()
    {
        PowerReading reading = GetPowerReading();
        bool known = reading.BatteryPercentKnown;
        int percent = known ? Math.Max(0, Math.Min(100, reading.BatteryPercent)) : 0;
        bool pluggedIn = reading.PluggedInKnown && reading.IsPluggedIn;

        OledPowerItem item = new OledPowerItem();
        // System power mode text ("均衡"/"节能"/...) and a plugged-in word are dropped from the
        // compact value for the same width-budget reason as GetOledPowerItem - plugged-in state is
        // still conveyed (Good severity floor below), just not spelled out as text in this tiny window.
        item.Label = "电池";
        item.Value = known ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "--";
        item.Severity = !known
            ? OledVariantPainting.Severity.Neutral
            : (pluggedIn || percent >= 50 ? OledVariantPainting.Severity.Good : (percent >= 20 ? OledVariantPainting.Severity.Warn : OledVariantPainting.Severity.Danger));
        return item;
    }

    private OledPowerItem[] GetOledThermalItems()
    {
        List<ThermalReading> alerts = GetThermalAlerts();
        OledPowerItem[] items = new OledPowerItem[alerts.Count];
        for (int i = 0; i < alerts.Count; i++)
        {
            ThermalReading reading = alerts[i];
            items[i] = new OledPowerItem
            {
                Label = reading.Name,
                Value = reading.Celsius.ToString("0", CultureInfo.InvariantCulture) + "°C",
                Severity = reading.CriticalActive ? OledVariantPainting.Severity.Danger : OledVariantPainting.Severity.Warn
            };
        }

        return items;
    }
}
