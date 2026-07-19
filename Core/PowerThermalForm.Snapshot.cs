using System;
using System.Collections.Generic;

// Read-only snapshot of the power/thermal sampler, published for the main widget's integrated
// power strip (1.0.5.83). PowerThermalForm owns the sampling; consumers get a cloned value so they
// can never mutate sampler-owned state, matching the reader/snapshot rule the background readers
// follow.
internal sealed class PowerStripSnapshot
{
    public bool PowerKnown;
    public bool Charging;
    public bool PluggedIn;
    public bool WattsKnown;
    public double Watts;
    public bool BatteryPercentKnown;
    public int BatteryPercent;
    public bool RuntimeSecondsKnown;
    public int RuntimeSeconds;
    public bool EnergySaverActive;
    public bool BatteryCarePauseActive;
    public string PowerModeText = string.Empty;

    // Every thermal zone that reports a real reading, plus the subset past the alert threshold.
    public int ZoneCount;
    public int AlertCount;
    public double MaxCelsius;
    public double AvgCelsius;
    public List<PowerStripZone> HotZones = new List<PowerStripZone>();
}

internal sealed class PowerStripZone
{
    public string Name = string.Empty;
    public double Celsius;
}

internal sealed partial class PowerThermalForm
{
    // Called from the UI thread by WidgetForm while painting; reads only cached sampler output and
    // never triggers sampling itself.
    internal PowerStripSnapshot BuildStripSnapshot()
    {
        PowerReading power = GetPowerReading();
        List<ThermalReading> alerts = GetThermalAlerts();
        bool energySaver = IsEnergySaverDisplayActive(power);

        PowerStripSnapshot s = new PowerStripSnapshot();
        s.PowerKnown = power.StatusKnown;
        s.Charging = power.StatusKnown && power.IsCharging;
        s.PluggedIn = power.PluggedInKnown && power.IsPluggedIn;
        s.WattsKnown = power.WattsKnown;
        s.Watts = power.Watts;
        s.BatteryPercentKnown = power.BatteryPercentKnown;
        s.BatteryPercent = power.BatteryPercent;
        s.RuntimeSecondsKnown = power.RuntimeSecondsKnown;
        s.RuntimeSeconds = power.RuntimeSeconds;
        s.EnergySaverActive = energySaver;
        s.BatteryCarePauseActive = power.BatteryCarePauseKnown && power.BatteryCarePauseActive;
        s.PowerModeText = FormatSystemPowerModeDisplayText(power, energySaver);

        ThermalSummary summary = BuildThermalSummary(alerts);
        s.ZoneCount = summary.ZoneCount;
        s.AlertCount = summary.AlertCount;
        s.MaxCelsius = summary.MaxCelsius;
        s.AvgCelsius = summary.AvgCelsius;

        for (int i = 0; i < alerts.Count; i++)
        {
            s.HotZones.Add(new PowerStripZone
            {
                Name = FormatThermalSensorName(alerts[i].Name),
                Celsius = alerts[i].Celsius
            });
        }

        return s;
    }

    // Test-only: seeds the cached readings so the integrated-strip preview has something to draw
    // without running the sampler.
    internal void SeedPreviewReadings()
    {
        PowerReading reading = new PowerReading();
        reading.StatusKnown = true;
        reading.IsCharging = false;
        reading.PluggedInKnown = true;
        reading.IsPluggedIn = false;
        reading.WattsKnown = true;
        reading.Watts = 12.4;
        reading.BatteryPercentKnown = true;
        reading.BatteryPercent = 79;
        reading.SystemPowerModeKnown = true;
        reading.SystemPowerModeText = "平衡";
        reading.EnergySaverKnown = true;
        reading.EnergySaverEnabled = false;
        reading.RuntimeSecondsKnown = true;
        reading.RuntimeSeconds = 13320;
        this.cachedPowerReading = reading;

        double[] temps = { 33.1, 32.9, 32.6, 32.6, 32.3, 32.2, 32.2, 32.1, 32.0, 31.9, 30.5, 30.2, 29.8, 28.4 };
        string[] names = { "TZ99", "TZ2", "TZ1", "TZ5", "TZ4", "TZ0", "TZ11", "TZ3", "TZ6", "TZ33", "TZ37", "TZ91", "TZ7", "TZ8" };
        List<ThermalReading> list = new List<ThermalReading>();
        for (int i = 0; i < temps.Length; i++)
        {
            list.Add(new ThermalReading { Name = names[i], Celsius = temps[i], CriticalActive = false });
        }

        this.cachedThermalReadings = list;
    }
}
