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
    // Full calibrated ACPI zone set for the System Day history. HotZones remains the alert-only
    // compatibility projection for diagnostics and older snapshot consumers; the PWR presentation
    // no longer displays temperature. RawName is retained because the short display label alone is
    // insufficient for later peak/zone correlation and calibration.
    public List<PowerStripZone> ThermalZones = new List<PowerStripZone>();
    public List<PowerStripZone> HotZones = new List<PowerStripZone>();
}

internal sealed class PowerStripZone
{
    public string RawName = string.Empty;
    public string Name = string.Empty;
    public double Celsius;

    public PowerStripZone Clone()
    {
        return (PowerStripZone)this.MemberwiseClone();
    }
}

internal sealed partial class PowerThermalForm
{
    private struct ThermalSummary
    {
        public int ZoneCount;
        public int AlertCount;
        public double MaxCelsius;
        public double AvgCelsius;
    }

    // Snapshot projection reads only sampler-owned memory. Zero-valued ACPI zones are exposed by
    // WMI on this device family but are not backed by a physical sensor, so they do not contribute
    // to the aggregate or its denominator.
    private ThermalSummary BuildThermalSummary(List<ThermalReading> alerts)
    {
        ThermalSummary summary = new ThermalSummary();
        summary.AlertCount = alerts == null ? 0 : alerts.Count;

        double sum = 0.0;
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            ThermalReading reading = this.cachedThermalReadings[i];
            if (reading == null || string.IsNullOrEmpty(reading.Name) || reading.Celsius <= 0.0)
            {
                continue;
            }

            summary.ZoneCount++;
            sum += reading.Celsius;
            summary.MaxCelsius = Math.Max(summary.MaxCelsius, reading.Celsius);
        }

        summary.AvgCelsius = summary.ZoneCount > 0 ? sum / summary.ZoneCount : 0.0;
        return summary;
    }

    private bool IsEnergySaverDisplayActive(PowerReading reading)
    {
        return (reading.EnergySaverKnown && reading.EnergySaverEnabled) ||
            IsManualEnergySaverThresholdActive(reading);
    }

    private bool IsManualEnergySaverThresholdActive(PowerReading reading)
    {
        // Snapshot-only fallback: some Windows builds keep EnergySaverStatus/SystemStatusFlag off
        // even when the configured low-battery threshold should be represented as energy saver.
        int threshold = this.CurrentSettings == null
            ? WidgetSettings.DefaultPowerThermalManualEnergySaverThresholdPercent
            : this.CurrentSettings.PowerThermalManualEnergySaverThresholdPercent;
        if (threshold <= 0 || !reading.BatteryPercentKnown)
        {
            return false;
        }

        int percent = Math.Max(0, Math.Min(100, reading.BatteryPercent));
        return percent < threshold;
    }

    private static string FormatSystemPowerModeDisplayText(PowerReading reading, bool energySaverActive)
    {
        string text = reading.SystemPowerModeKnown
            ? NormalizeDisplaySystemPowerModeText(reading.SystemPowerModeText)
            : "--";

        return energySaverActive ? AppendEnergySaverSuffix(text) : text;
    }

    private static string NormalizeDisplaySystemPowerModeText(string text)
    {
        if (string.Equals(text, "均衡", StringComparison.Ordinal))
        {
            return "平衡";
        }

        return string.IsNullOrEmpty(text) ? "--" : text;
    }

    private static string AppendEnergySaverSuffix(string text)
    {
        if (string.IsNullOrEmpty(text) || string.Equals(text, "--", StringComparison.Ordinal))
        {
            return "节能";
        }

        if (text.IndexOf("节能", StringComparison.Ordinal) >= 0)
        {
            return text;
        }

        return text + "（节能）";
    }

    private static string FormatThermalSensorName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "TZ";
        }

        string trimmed = name.Trim();
        int slash = trimmed.LastIndexOf('\\');
        int dot = trimmed.LastIndexOf('.');
        int start = Math.Max(slash, dot);
        if (start >= 0 && start < trimmed.Length - 1)
        {
            trimmed = trimmed.Substring(start + 1);
        }

        return trimmed.Length == 0 ? "TZ" : trimmed;
    }

    internal bool IsSamplingAllowedForSelfTest()
    {
        return IsSamplingAllowed();
    }

    internal int ResumePrimeCountForSelfTest
    {
        get { return this.displayResumePrimeCountForSelfTest; }
    }

    // Narrow lifecycle seams for the existing command-line self-tests. They expose no sampled
    // values and let tests prove that the hidden owner has a notification HWND/main timer while
    // all presentation and interaction machinery stays off.
    internal bool IsHeadlessDataOwnerRunningForSelfTest()
    {
        return this.headlessDataOwner &&
            this.dataOwnerRuntimeStarted &&
            !this.ownedRuntimeResourcesDisposed &&
            this.IsHandleCreated &&
            this.timer.Enabled &&
            !this.Visible &&
            !CanRenderLayeredWindow();
    }

    internal bool IsHeadlessDataOwnerStoppedForSelfTest()
    {
        return this.headlessDataOwner &&
            !this.dataOwnerRuntimeStarted &&
            this.ownedRuntimeResourcesDisposed;
    }

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

        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            ThermalReading reading = this.cachedThermalReadings[i];
            if (reading == null || string.IsNullOrWhiteSpace(reading.Name) || reading.Celsius <= 0.0)
            {
                continue;
            }

            s.ThermalZones.Add(new PowerStripZone
            {
                RawName = reading.Name.Trim(),
                Name = FormatThermalSensorName(reading.Name),
                Celsius = reading.Celsius
            });
        }
        s.ThermalZones.Sort(delegate(PowerStripZone left, PowerStripZone right)
        {
            return right.Celsius.CompareTo(left.Celsius);
        });

        for (int i = 0; i < alerts.Count; i++)
        {
            s.HotZones.Add(new PowerStripZone
            {
                RawName = alerts[i].Name == null ? string.Empty : alerts[i].Name.Trim(),
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
