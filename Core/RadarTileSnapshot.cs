using System;
using System.Collections.Generic;

internal enum QuotaForecastConfidence
{
    None,
    Low,
    Medium,
    High
}

// Read-only view of one service family's Radar state, for the Radar tiles and their expand panels
// (canonical edge Radar tiles, 1.0.6.20).
//
// CodexRadarForm keeps a separate RadarFamilyRuntimeState for Codex and Claude, so both families
// can be reported regardless of which one the shared window is currently displaying. This DTO is
// the only thing the tile windows see: they never reach into the Radar form's private state, and
// building it must not trigger sampling — same contract as PowerThermalForm.BuildStripSnapshot.
internal sealed class RadarTileSnapshot
{
    public CodexRadarSoftwareMode Family;
    public string FamilyLabel = string.Empty;
    public string ModelName = string.Empty;

    // Quota. Percent values are REMAINING, matching what the Radar rings show.
    public bool QuotaKnown;
    public bool QuotaSourceUpdatedKnown;
    public DateTime QuotaSourceUpdatedUtc;
    public int FiveHourPercent;
    public bool FiveHourResetKnown;
    public DateTime FiveHourResetLocal;
    public bool FiveHourLimitAbsent;
    public int WeeklyPercent;
    public bool WeeklyResetKnown;
    public DateTime WeeklyResetLocal;

    // Measured burn-down: accepted weekly remaining-% readings on this machine, oldest first, on an
    // active-time axis. Present only after enough samples accumulate — a fresh process honestly has
    // none, and the panel must say so rather than draw a flat line.
    public List<double> WeeklyBurnRemaining = new List<double>();
    public bool BurnRateKnown;
    public double BurnPercentPerHour;
    public double RunwayHours;
    public double HoursToReset;
    public double BurnObservedHours;
    public QuotaForecastConfidence BurnConfidence;
    public bool CalendarRunwayKnown;
    public double CalendarBurnPercentPerHour;
    public double CalendarRunwayHours;
    public QuotaForecastConfidence CalendarConfidence;

    // The short 5-hour pool is estimated independently from the weekly pool. Sharing a rate would
    // make a bursty short window inherit the week's slower pace and produce a dangerously optimistic
    // "可撑到重置" conclusion.
    public bool FiveHourBurnRateKnown;
    public double FiveHourBurnPercentPerHour;
    public double FiveHourRunwayHours;
    public double FiveHourHoursToReset;
    public QuotaForecastConfidence FiveHourBurnConfidence;

    // Model quality.
    public bool IqKnown;
    public int Iq;
    public bool IqUpdatedKnown;
    public DateTime IqUpdatedLocal;
    public bool EfficiencyKnown;
    public int TokenEfficiencyPercent;
    public int TimeEfficiencyPercent;

    public static RadarTileSnapshot CreateEmpty(CodexRadarSoftwareMode family)
    {
        return new RadarTileSnapshot
        {
            Family = family,
            FamilyLabel = family == CodexRadarSoftwareMode.Claude ? "CLAUDE" : "CODEX"
        };
    }

    // True when the burn-down has enough shape to plot. One sample is a dot, not a trend.
    public bool HasBurnCurve
    {
        get { return this.WeeklyBurnRemaining != null && this.WeeklyBurnRemaining.Count >= 2; }
    }
}
