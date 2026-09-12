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

    public bool ResetRadarKnown { get; set; }
    public bool ResetRadarUpdatedAtKnown { get; set; }
    public DateTime ResetRadarUpdatedAtLocal { get; set; }
    public string ResetCardStatus { get; set; }
    public string ResetCardDescription { get; set; }
    public string HardResetStatus { get; set; }
    public string HardResetDescription { get; set; }

    // Codex account context. Every number above belongs to ActiveAccountKey; the roster exists so
    // the board can offer a switch and say which account the seven-day curve is actually about.
    public bool ActiveAccountKnown { get; set; }
    public string ActiveAccountKey { get; set; }
    public string ActiveAccountLabel { get; set; }
    public string ActiveAccountLetter { get; set; }
    public string ActiveAccountPlan { get; set; }
    public string AccountNotice { get; set; }

    public List<ResetSpeedQuotaPoint> QuotaHistory { get; private set; }
    public List<ResetSpeedResetEvent> ResetEvents { get; private set; }
    public List<ResetSpeedAccountEntry> Accounts { get; private set; }

    public static ResetSpeedBoardSnapshot CreateEmpty()
    {
        return new ResetSpeedBoardSnapshot
        {
            FiveHourRemainingPercent = 100,
            WeeklyRemainingPercent = 100,
            ResetCardStatus = string.Empty,
            ResetCardDescription = string.Empty,
            HardResetStatus = string.Empty,
            HardResetDescription = string.Empty,
            ActiveAccountKey = string.Empty,
            ActiveAccountLabel = string.Empty,
            ActiveAccountLetter = string.Empty,
            ActiveAccountPlan = string.Empty,
            AccountNotice = string.Empty,
            QuotaHistory = new List<ResetSpeedQuotaPoint>(),
            ResetEvents = new List<ResetSpeedResetEvent>(),
            Accounts = new List<ResetSpeedAccountEntry>()
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
        clone.ResetRadarKnown = this.ResetRadarKnown;
        clone.ResetRadarUpdatedAtKnown = this.ResetRadarUpdatedAtKnown;
        clone.ResetRadarUpdatedAtLocal = this.ResetRadarUpdatedAtLocal;
        clone.ResetCardStatus = this.ResetCardStatus;
        clone.ResetCardDescription = this.ResetCardDescription;
        clone.HardResetStatus = this.HardResetStatus;
        clone.HardResetDescription = this.HardResetDescription;
        clone.ActiveAccountKnown = this.ActiveAccountKnown;
        clone.ActiveAccountKey = this.ActiveAccountKey;
        clone.ActiveAccountLabel = this.ActiveAccountLabel;
        clone.ActiveAccountLetter = this.ActiveAccountLetter;
        clone.ActiveAccountPlan = this.ActiveAccountPlan;
        clone.AccountNotice = this.AccountNotice;
        for (int i = 0; i < this.Accounts.Count; i++)
        {
            if (this.Accounts[i] != null) clone.Accounts.Add(this.Accounts[i].Clone());
        }
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

// One switchable Codex account as shown on the board. Carries no credential: the DPAPI blob stays
// in CodexAccountStore and is only touched by an explicit switch.
internal sealed class ResetSpeedAccountEntry
{
    public string AccountKey { get; set; }
    // Stable A/B/C/D badge, used as the whole name when no address could be read.
    public string Letter { get; set; }
    public string Label { get; set; }
    public string PlanType { get; set; }
    public bool IsActive { get; set; }
    // False when this machine has no usable credential snapshot, so the row renders un-clickable
    // instead of failing at click time.
    public bool CanSwitch { get; set; }
    public bool LastSeenKnown { get; set; }
    public DateTime LastSeenLocal { get; set; }

    public ResetSpeedAccountEntry Clone()
    {
        return (ResetSpeedAccountEntry)this.MemberwiseClone();
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

// Outcome of a board-initiated Codex account switch. Carries only a human message; the board never
// sees a credential and never learns why a switch was refused beyond this text.
internal sealed class CodexAccountSwitchResult
{
    public bool Success { get; set; }
    public string Message { get; set; }

    public static CodexAccountSwitchResult CreateSuccess(string message)
    {
        return new CodexAccountSwitchResult { Success = true, Message = message ?? string.Empty };
    }

    public static CodexAccountSwitchResult CreateFailure(string message)
    {
        return new CodexAccountSwitchResult { Success = false, Message = message ?? string.Empty };
    }
}
