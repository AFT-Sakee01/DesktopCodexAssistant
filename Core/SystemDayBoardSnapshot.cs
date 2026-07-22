using System;
using System.Collections.Generic;

internal enum SystemDayRange
{
    Today,
    Last24Hours,
    LastWeek
}

internal enum SystemDayWorkState
{
    Unknown,
    Active,
    Idle,
    Sleep
}

internal enum SystemDayBatteryDirection
{
    Unknown,
    Flat,
    Rising,
    Falling
}

// Cache-only DTO for the seventh left-dock board. The persistent store performs all file I/O;
// presentation receives a deep clone containing at most a few hundred plotting points.
internal sealed class SystemDayBoardSnapshot
{
    public SystemDayRange Range { get; set; }
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    public DateTime UpdatedLocal { get; set; }
    public double ActiveMinutes { get; set; }
    public double IdleMinutes { get; set; }
    public double SleepMinutes { get; set; }
    public double RecordedMinutes { get; set; }
    public bool CurrentBatteryKnown { get; set; }
    public int CurrentBatteryPercent { get; set; }
    public bool CurrentCharging { get; set; }
    public bool CurrentPluggedIn { get; set; }
    public bool CurrentWattsKnown { get; set; }
    public double CurrentWatts { get; set; }
    public bool BatteryEtaKnown { get; set; }
    public int BatteryEtaMinutes { get; set; }
    public int BatteryEtaTargetPercent { get; set; }
    public string BatteryEtaText { get; set; }
    public string CurrentPowerModeText { get; set; }
    public bool CurrentTemperatureKnown { get; set; }
    public double CurrentMaxCelsius { get; set; }
    public string CurrentHotZoneName { get; set; }
    public int RawSampleCount { get; set; }
    public List<SystemDayBoardPoint> Points { get; private set; }
    public List<SystemDayWorkSegment> WorkSegments { get; private set; }
    public List<SystemDayMetricPeak> Peaks { get; private set; }

    public static SystemDayBoardSnapshot CreateEmpty(SystemDayRange range, DateTime nowLocal)
    {
        DateTime start = ResolveStartLocal(range, nowLocal);
        return new SystemDayBoardSnapshot
        {
            Range = range,
            StartLocal = start,
            EndLocal = nowLocal,
            UpdatedLocal = DateTime.MinValue,
            BatteryEtaText = "等待电量趋势",
            CurrentPowerModeText = "--",
            CurrentHotZoneName = "--",
            Points = new List<SystemDayBoardPoint>(),
            WorkSegments = new List<SystemDayWorkSegment>(),
            Peaks = new List<SystemDayMetricPeak>()
        };
    }

    internal static DateTime ResolveStartLocal(SystemDayRange range, DateTime nowLocal)
    {
        switch (range)
        {
            case SystemDayRange.Today:
                return nowLocal.Date;
            case SystemDayRange.Last24Hours:
                return nowLocal.AddHours(-24.0);
            default:
                return nowLocal.AddDays(-7.0);
        }
    }

    public SystemDayBoardSnapshot Clone()
    {
        SystemDayBoardSnapshot clone = CreateEmpty(this.Range, this.EndLocal);
        clone.StartLocal = this.StartLocal;
        clone.EndLocal = this.EndLocal;
        clone.UpdatedLocal = this.UpdatedLocal;
        clone.ActiveMinutes = this.ActiveMinutes;
        clone.IdleMinutes = this.IdleMinutes;
        clone.SleepMinutes = this.SleepMinutes;
        clone.RecordedMinutes = this.RecordedMinutes;
        clone.CurrentBatteryKnown = this.CurrentBatteryKnown;
        clone.CurrentBatteryPercent = this.CurrentBatteryPercent;
        clone.CurrentCharging = this.CurrentCharging;
        clone.CurrentPluggedIn = this.CurrentPluggedIn;
        clone.CurrentWattsKnown = this.CurrentWattsKnown;
        clone.CurrentWatts = this.CurrentWatts;
        clone.BatteryEtaKnown = this.BatteryEtaKnown;
        clone.BatteryEtaMinutes = this.BatteryEtaMinutes;
        clone.BatteryEtaTargetPercent = this.BatteryEtaTargetPercent;
        clone.BatteryEtaText = this.BatteryEtaText;
        clone.CurrentPowerModeText = this.CurrentPowerModeText;
        clone.CurrentTemperatureKnown = this.CurrentTemperatureKnown;
        clone.CurrentMaxCelsius = this.CurrentMaxCelsius;
        clone.CurrentHotZoneName = this.CurrentHotZoneName;
        clone.RawSampleCount = this.RawSampleCount;
        for (int i = 0; i < this.Points.Count; i++) clone.Points.Add(this.Points[i].Clone());
        for (int i = 0; i < this.WorkSegments.Count; i++) clone.WorkSegments.Add(this.WorkSegments[i].Clone());
        for (int i = 0; i < this.Peaks.Count; i++) clone.Peaks.Add(this.Peaks[i].Clone());
        return clone;
    }

    public SystemDayMetricPeak FindPeak(string metricId)
    {
        for (int i = 0; i < this.Peaks.Count; i++)
        {
            if (string.Equals(this.Peaks[i].MetricId, metricId, StringComparison.OrdinalIgnoreCase))
                return this.Peaks[i];
        }
        return null;
    }
}

internal sealed class SystemDayBoardPoint
{
    public DateTime TimestampLocal { get; set; }
    public SystemDayWorkState WorkState { get; set; }
    public double CpuPercent { get; set; }
    public double GpuPercent { get; set; }
    public double NpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public double NetworkBytesPerSecond { get; set; }
    public bool BatteryKnown { get; set; }
    public int BatteryPercent { get; set; }
    public SystemDayBatteryDirection BatteryDirection { get; set; }
    public bool Charging { get; set; }
    public bool PluggedIn { get; set; }
    public bool WattsKnown { get; set; }
    public double Watts { get; set; }
    public bool TemperatureKnown { get; set; }
    public double MaxCelsius { get; set; }
    public double AvgCelsius { get; set; }
    public string HotZoneName { get; set; }

    public SystemDayBoardPoint Clone()
    {
        return (SystemDayBoardPoint)this.MemberwiseClone();
    }
}

internal sealed class SystemDayWorkSegment
{
    public DateTime StartLocal { get; set; }
    public DateTime EndLocal { get; set; }
    public SystemDayWorkState State { get; set; }

    public SystemDayWorkSegment Clone()
    {
        return (SystemDayWorkSegment)this.MemberwiseClone();
    }
}

internal sealed class SystemDayMetricPeak
{
    public string MetricId { get; set; }
    public double Value { get; set; }
    public DateTime TimestampLocal { get; set; }
    public string Unit { get; set; }
    public string ZoneName { get; set; }

    public SystemDayMetricPeak Clone()
    {
        return (SystemDayMetricPeak)this.MemberwiseClone();
    }
}

