using System;
using System.Collections.Generic;

// Read-only DTO for the sixth left-dock board. The presentation surface receives a clone of this
// object and must never reach back into CodexRadarForm, the quota provider, or the history store.
internal sealed class ResetSpeedBoardSnapshot
{
    public bool QuotaKnown { get; set; }
    public int FiveHourRemainingPercent { get; set; }
    public bool FiveHourLimitAbsent { get; set; }
    public bool FiveHourResetKnown { get; set; }
    public DateTime FiveHourResetLocal { get; set; }
    public int WeeklyRemainingPercent { get; set; }
    public bool WeeklyResetKnown { get; set; }
    public DateTime WeeklyResetLocal { get; set; }
    public bool UpdatedKnown { get; set; }
    public DateTime UpdatedLocal { get; set; }

    public bool SpeedWindowKnown { get; set; }
    public bool SpeedWindowOpen { get; set; }
    public bool SpeedWindowOpenedAtKnown { get; set; }
    public DateTime SpeedWindowOpenedAtLocal { get; set; }
    public bool SpeedWindowClosedAtKnown { get; set; }
    public DateTime SpeedWindowClosedAtLocal { get; set; }
    public int SpeedWindowRemainingMinutes { get; set; }
    public float SpeedWindowRemainingRatio { get; set; }

    public bool ResetCreditsKnown { get; set; }
    public bool ResetCreditsRequestRunning { get; set; }
    public int ResetCreditCount { get; set; }
    public bool ResetCreditExpirationKnown { get; set; }
    public DateTime ResetCreditExpirationLocal { get; set; }

    public List<ResetSpeedQuotaPoint> QuotaHistory { get; private set; }
    public List<ResetSpeedResetEvent> ResetEvents { get; private set; }

    public static ResetSpeedBoardSnapshot CreateEmpty()
    {
        return new ResetSpeedBoardSnapshot
        {
            FiveHourRemainingPercent = 100,
            WeeklyRemainingPercent = 100,
            QuotaHistory = new List<ResetSpeedQuotaPoint>(),
            ResetEvents = new List<ResetSpeedResetEvent>()
        };
    }

    public ResetSpeedBoardSnapshot Clone()
    {
        ResetSpeedBoardSnapshot clone = CreateEmpty();
        clone.QuotaKnown = this.QuotaKnown;
        clone.FiveHourRemainingPercent = this.FiveHourRemainingPercent;
        clone.FiveHourLimitAbsent = this.FiveHourLimitAbsent;
        clone.FiveHourResetKnown = this.FiveHourResetKnown;
        clone.FiveHourResetLocal = this.FiveHourResetLocal;
        clone.WeeklyRemainingPercent = this.WeeklyRemainingPercent;
        clone.WeeklyResetKnown = this.WeeklyResetKnown;
        clone.WeeklyResetLocal = this.WeeklyResetLocal;
        clone.UpdatedKnown = this.UpdatedKnown;
        clone.UpdatedLocal = this.UpdatedLocal;
        clone.SpeedWindowKnown = this.SpeedWindowKnown;
        clone.SpeedWindowOpen = this.SpeedWindowOpen;
        clone.SpeedWindowOpenedAtKnown = this.SpeedWindowOpenedAtKnown;
        clone.SpeedWindowOpenedAtLocal = this.SpeedWindowOpenedAtLocal;
        clone.SpeedWindowClosedAtKnown = this.SpeedWindowClosedAtKnown;
        clone.SpeedWindowClosedAtLocal = this.SpeedWindowClosedAtLocal;
        clone.SpeedWindowRemainingMinutes = this.SpeedWindowRemainingMinutes;
        clone.SpeedWindowRemainingRatio = this.SpeedWindowRemainingRatio;
        clone.ResetCreditsKnown = this.ResetCreditsKnown;
        clone.ResetCreditsRequestRunning = this.ResetCreditsRequestRunning;
        clone.ResetCreditCount = this.ResetCreditCount;
        clone.ResetCreditExpirationKnown = this.ResetCreditExpirationKnown;
        clone.ResetCreditExpirationLocal = this.ResetCreditExpirationLocal;
        for (int i = 0; i < this.QuotaHistory.Count; i++)
        {
            if (this.QuotaHistory[i] != null) clone.QuotaHistory.Add(this.QuotaHistory[i].Clone());
        }
        for (int i = 0; i < this.ResetEvents.Count; i++)
        {
            if (this.ResetEvents[i] != null) clone.ResetEvents.Add(this.ResetEvents[i].Clone());
        }
        return clone;
    }
}

internal sealed class ResetSpeedQuotaPoint
{
    public DateTime DateLocal { get; set; }
    public bool Known { get; set; }
    public int WeeklyRemainingPercent { get; set; }

    public ResetSpeedQuotaPoint Clone()
    {
        return (ResetSpeedQuotaPoint)this.MemberwiseClone();
    }
}

internal enum ResetSpeedResetKind
{
    Natural,
    Hard,
    Credit
}

internal sealed class ResetSpeedResetEvent
{
    public DateTime TimestampLocal { get; set; }
    public ResetSpeedResetKind Kind { get; set; }
    public int WeeklyRemainingPercent { get; set; }

    public ResetSpeedResetEvent Clone()
    {
        return (ResetSpeedResetEvent)this.MemberwiseClone();
    }
}
