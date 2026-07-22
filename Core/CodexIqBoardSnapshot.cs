using System;
using System.Collections.Generic;
using System.Drawing;

// Read-only, cache-only projection consumed by the left-dock Codex IQ board. The board must never
// fetch independently: CodexRadarForm owns provider I/O, filtering and cache fallback, while this
// DTO carries only values that are safe to paint on the UI thread.
internal sealed class CodexIqBoardSnapshot
{
    public DateTime UpdatedLocal { get; set; }
    public bool UpdatedKnown { get; set; }
    public string SelectedModelKey { get; set; }
    public string SelectedModelLabel { get; set; }
    public bool SourceStale { get; set; }
    public string SourceStatus { get; set; }
    public readonly List<CodexIqBoardModelPoint> Models = new List<CodexIqBoardModelPoint>();
    public readonly List<CodexIqBoardTrendPoint> Trends = new List<CodexIqBoardTrendPoint>();
    public readonly List<double> WeeklyQuotaRemaining = new List<double>();
    public readonly List<CodexIqBoardRosterEntry> Roster = new List<CodexIqBoardRosterEntry>();
    // Upstream service-health LEDs (Radar / OpenAI / Claude / DeepSeek), relocated here from the
    // network dock panel. Same read-only projection contract as everything else on this DTO.
    public readonly List<RadarServiceHealthEntry> Services = new List<RadarServiceHealthEntry>();
    // The Radar refresh-cycle status, flattened from the circular RadarClockDial into fractions a
    // horizontal track can paint.
    public CodexIqBoardRefreshStatus Refresh = new CodexIqBoardRefreshStatus();

    public static CodexIqBoardSnapshot CreateEmpty()
    {
        return new CodexIqBoardSnapshot
        {
            UpdatedLocal = DateTime.MinValue,
            UpdatedKnown = false,
            SelectedModelKey = string.Empty,
            SelectedModelLabel = string.Empty,
            SourceStale = false,
            SourceStatus = string.Empty
        };
    }

    public CodexIqBoardSnapshot Clone()
    {
        CodexIqBoardSnapshot clone = CreateEmpty();
        clone.UpdatedLocal = this.UpdatedLocal;
        clone.UpdatedKnown = this.UpdatedKnown;
        clone.SelectedModelKey = this.SelectedModelKey ?? string.Empty;
        clone.SelectedModelLabel = this.SelectedModelLabel ?? string.Empty;
        clone.SourceStale = this.SourceStale;
        clone.SourceStatus = this.SourceStatus ?? string.Empty;
        for (int i = 0; i < this.Models.Count; i++)
        {
            if (this.Models[i] != null)
            {
                clone.Models.Add(this.Models[i].Clone());
            }
        }

        for (int i = 0; i < this.Trends.Count; i++)
        {
            if (this.Trends[i] != null)
            {
                clone.Trends.Add(this.Trends[i].Clone());
            }
        }

        clone.WeeklyQuotaRemaining.AddRange(this.WeeklyQuotaRemaining);
        for (int i = 0; i < this.Roster.Count; i++)
        {
            if (this.Roster[i] != null)
            {
                clone.Roster.Add(this.Roster[i].Clone());
            }
        }

        for (int i = 0; i < this.Services.Count; i++)
        {
            RadarServiceHealthEntry service = this.Services[i];
            if (service != null)
            {
                clone.Services.Add(new RadarServiceHealthEntry
                {
                    Label = service.Label,
                    Color = service.Color,
                    Checking = service.Checking
                });
            }
        }

        clone.Refresh = this.Refresh != null ? this.Refresh.Clone() : new CodexIqBoardRefreshStatus();
        return clone;
    }
}

// The Radar refresh ring restored as a horizontal track: the circular arc angles are pre-flattened
// into 0..1 fractions across the width, so the board only paints marks and a segment. Phase colour
// and warning state carry over unchanged from RadarClockDial.
internal sealed class CodexIqBoardRefreshStatus
{
    public bool Known { get; set; }
    public Color StatusColor { get; set; }
    public bool RequestRunning { get; set; }
    public bool Warning { get; set; }
    // "Now" within the cycle, and the last-refresh marker, both as 0..1 across the track.
    public float CurrentFraction { get; set; }
    public bool MarkerVisible { get; set; }
    public float MarkerFraction { get; set; }
    // The active segment (marker -> now, or boundary -> now for a late cycle).
    public float ArcStartFraction { get; set; }
    public float ArcSweepFraction { get; set; }
    public string PhaseText { get; set; }
    public string DetailText { get; set; }

    public CodexIqBoardRefreshStatus Clone()
    {
        return (CodexIqBoardRefreshStatus)this.MemberwiseClone();
    }
}

internal sealed class CodexIqBoardModelPoint
{
    public string Key { get; set; }
    public string Label { get; set; }
    public string Family { get; set; }
    public string Effort { get; set; }
    public string Status { get; set; }
    public DateTime DataLocal { get; set; }
    public bool DataKnown { get; set; }
    public double Iq { get; set; }
    public double AverageCostUsd { get; set; }
    public double AverageTaskSeconds { get; set; }
    public double TotalTokens { get; set; }
    public double Passed { get; set; }
    public double ValidTasks { get; set; }
    public bool Current { get; set; }

    public CodexIqBoardModelPoint Clone()
    {
        return (CodexIqBoardModelPoint)this.MemberwiseClone();
    }
}

internal sealed class CodexIqBoardTrendPoint
{
    public DateTime DateLocal { get; set; }
    public double AverageTaskSeconds { get; set; }
    public double TokenEfficiencyPercent { get; set; }
    public double TotalTokens { get; set; }
    public bool EfficiencyKnown { get; set; }

    public CodexIqBoardTrendPoint Clone()
    {
        return (CodexIqBoardTrendPoint)this.MemberwiseClone();
    }
}

internal enum CodexIqBoardRosterState
{
    Active,
    Intermittent,
    Retired
}

internal sealed class CodexIqBoardRosterEntry
{
    public string Key { get; set; }
    public string Label { get; set; }
    public CodexIqBoardRosterState State { get; set; }

    public CodexIqBoardRosterEntry Clone()
    {
        return (CodexIqBoardRosterEntry)this.MemberwiseClone();
    }
}
