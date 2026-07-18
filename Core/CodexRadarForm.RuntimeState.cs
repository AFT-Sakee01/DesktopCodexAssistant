using System;
using System.Collections.Generic;

internal sealed partial class CodexRadarForm
{
    private readonly RadarSoftwareModeController radarSoftwareModeController = new RadarSoftwareModeController();
    private readonly RadarFamilyRuntimeState codexRuntimeState = new RadarFamilyRuntimeState(CodexRadarSoftwareMode.Codex);
    private readonly RadarFamilyRuntimeState claudeRuntimeState = new RadarFamilyRuntimeState(CodexRadarSoftwareMode.Claude);

    // Codex and Claude share the same rendered window, but their volatile data cannot share
    // fields. Quota baselines, source-known flags, Radar health, request state, and debounce
    // state live here per family so a late request or mode switch cannot poison the other side.
    private sealed class RadarFamilyRuntimeState
    {
        public RadarFamilyRuntimeState(CodexRadarSoftwareMode family)
        {
            this.Family = RadarSoftwareModeController.NormalizeEffectiveSoftwareMode(family);
            this.ModelKey = string.Empty;
            this.RadarSnapshot = CodexRadarSnapshot.CreateDefault();
            this.Quota = new QuotaRuntimeState();
            this.RadarSiteHealth = ServiceHealthState.Unknown;
            this.ApiAlertDebounce = new ApiAlertDebounceRuntimeState();
            this.LastRadarStatusRefreshUtc = DateTime.MinValue;
            this.NextRadarStatusRefreshUtc = DateTime.MinValue;
            this.RadarStatusRefreshTrigger = "启动刷新";
        }

        public CodexRadarSoftwareMode Family { get; private set; }
        public string ModelKey { get; set; }
        public CodexRadarSnapshot RadarSnapshot { get; set; }
        public QuotaRuntimeState Quota { get; private set; }
        public ServiceHealthState RadarSiteHealth { get; set; }
        public ApiAlertDebounceRuntimeState ApiAlertDebounce { get; private set; }
        public bool RadarStatusRequestRunning { get; set; }
        public DateTime LastRadarStatusRefreshUtc { get; set; }
        public DateTime LastRadarStatusAttemptLocal { get; set; }
        public DateTime NextRadarStatusRefreshUtc { get; set; }
        public string RadarStatusRefreshTrigger { get; set; }
        public long Revision { get; private set; }

        public void Touch()
        {
            unchecked
            {
                this.Revision++;
            }
        }
    }

    private sealed class QuotaRuntimeState
    {
        public QuotaRuntimeState()
        {
            this.Snapshot = CodexQuotaSnapshot.CreateDefault();
            this.SourceKnown = false;
            this.LastFiveHourReadPercent = -1;
            this.LastWeeklyReadPercent = -1;
            this.LastReadSourceUtc = DateTime.MinValue;
            this.LastRefreshUtc = DateTime.MinValue;
            this.NextInactiveRefreshUtc = DateTime.MinValue;
            this.FiveHourConsumptionRingBaselinePercent = -1;
            this.TrackedFiveHourResetLocal = DateTime.MinValue;
            this.TrackedWeeklyResetLocal = DateTime.MinValue;
            this.FiveHourRejectedIdentity = new RejectedIdentityPersistenceState();
            this.WeeklyRejectedIdentity = new RejectedIdentityPersistenceState();
            this.WeeklyQuotaAtFiveHourWindowStartPercent = -1;
            this.FiveHourLastConsumingAcceptUtc = DateTime.MinValue;
            this.WeeklyLastConsumingAcceptUtc = DateTime.MinValue;
            this.FiveHourLastNewbornAcceptUtc = DateTime.MinValue;
            this.WeeklyLastNewbornAcceptUtc = DateTime.MinValue;
            this.WeeklyResetRainbowActive = false;
            this.WeeklyBurnSamples = new List<WeeklyBurnSample>();
            this.WeeklyBurnTrackedResetLocal = DateTime.MinValue;
            this.WeeklyBurnClockUtc = DateTime.MinValue;
            this.Protection = new QuotaProtectionState();
        }

        public CodexQuotaSnapshot Snapshot { get; set; }
        public bool SourceKnown { get; set; }
        public int LastFiveHourReadPercent { get; set; }
        public int LastWeeklyReadPercent { get; set; }
        public DateTime LastReadSourceUtc { get; set; }
        public DateTime LastRefreshUtc { get; set; }
        public DateTime NextInactiveRefreshUtc { get; set; }
        public int FiveHourConsumptionRingBaselinePercent { get; set; }
        public DateTime TrackedFiveHourResetLocal { get; set; }
        public DateTime TrackedWeeklyResetLocal { get; set; }
        public RejectedIdentityPersistenceState FiveHourRejectedIdentity { get; private set; }
        public RejectedIdentityPersistenceState WeeklyRejectedIdentity { get; private set; }
        public int WeeklyQuotaAtFiveHourWindowStartPercent { get; set; }
        public DateTime FiveHourLastConsumingAcceptUtc { get; set; }
        public DateTime WeeklyLastConsumingAcceptUtc { get; set; }
        public DateTime FiveHourLastNewbornAcceptUtc { get; set; }
        public DateTime WeeklyLastNewbornAcceptUtc { get; set; }
        // Latched true when the accepted weekly balance crosses up into the reset band (>=95) and
        // held until it falls below the exit band (<90). Drives the rainbow quota-reset celebration.
        // In-memory only: it can only be set by a live accepted upward crossing, so it never
        // false-fires on startup/cache load.
        public bool WeeklyResetRainbowActive { get; set; }
        // Accepted (post-interference-filter) weekly remaining-% readings observed by THIS machine,
        // oldest first, pruned to the burn-rate window. Feeds the measured-rate weekly budget ring;
        // in-memory only, so a restart honestly re-accumulates before showing a rate.
        public List<WeeklyBurnSample> WeeklyBurnSamples { get; private set; }
        public DateTime WeeklyBurnTrackedResetLocal { get; set; }
        // The sampling clock advances only while Codex is running. Keeping an active-time axis
        // prevents overnight/closed-app gaps from making the measured burn rate look artificially low.
        public double WeeklyBurnActiveHours { get; set; }
        public DateTime WeeklyBurnClockUtc { get; set; }
        public bool WeeklyBurnClockActive { get; set; }
        public QuotaProtectionState Protection { get; private set; }
    }

    // One accepted weekly-quota reading projected onto the active-time clock.
    private sealed class WeeklyBurnSample
    {
        public DateTime Utc { get; set; }
        public double ActiveHours { get; set; }
        public int RemainingPercent { get; set; }
    }

    // A provider can temporarily project an additional quota pool into the top-level fields.
    // Track rejected identities only in memory so a repeatedly observed real pool can repair a
    // wrong runtime anchor without making that tentative state survive a process restart.
    private sealed class RejectedIdentityPersistenceState
    {
        public DateTime ResetLocal { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int Count { get; set; }

        public void Reset()
        {
            this.ResetLocal = DateTime.MinValue;
            this.FirstSeenUtc = DateTime.MinValue;
            this.LastSeenUtc = DateTime.MinValue;
            this.Count = 0;
        }
    }

    private sealed class QuotaProtectionState
    {
        public DateTime FiveHourProtectionUtc { get; set; }
        public DateTime WeeklyProtectionUtc { get; set; }
        public bool FiveHourProtectionGold { get; set; }
        public bool WeeklyProtectionGold { get; set; }
        public string LastRadarResetEventId { get; set; }
        public DateTime LastRadarResetEventUtc { get; set; }
        public string LastRadarProtectedResetEventId { get; set; }
        public string LastRadarOpenEventId { get; set; }
        public DateTime LastRadarOpenEventUtc { get; set; }
    }

    private sealed class ApiAlertDebounceRuntimeState
    {
        public ApiAlertDebounceRuntimeState()
        {
            this.Signature = string.Empty;
            this.Index = 0;
            this.NamePhase = true;
            this.States = new Dictionary<string, ServiceAlertDebounceState>(StringComparer.OrdinalIgnoreCase);
        }

        public string Signature { get; set; }
        public int Index { get; set; }
        public bool NamePhase { get; set; }
        public Dictionary<string, ServiceAlertDebounceState> States { get; private set; }
    }

    private RadarFamilyRuntimeState GetRadarFamilyState(CodexRadarSoftwareMode family)
    {
        return RadarSoftwareModeController.NormalizeEffectiveSoftwareMode(family) == CodexRadarSoftwareMode.Claude
            ? this.claudeRuntimeState
            : this.codexRuntimeState;
    }

    private RadarFamilyRuntimeState GetActiveRadarFamilyState()
    {
        return GetRadarFamilyState(GetEffectiveCodexRadarSoftwareMode());
    }

    private QuotaRuntimeState GetQuotaRuntimeState(CodexRadarSoftwareMode family)
    {
        return GetRadarFamilyState(family).Quota;
    }

    private QuotaRuntimeState GetActiveQuotaRuntimeState()
    {
        return GetActiveRadarFamilyState().Quota;
    }

    private QuotaProtectionState GetCodexQuotaProtectionState()
    {
        return this.codexRuntimeState.Quota.Protection;
    }

    private CodexQuotaSnapshot quotaSnapshot
    {
        get { return GetActiveQuotaRuntimeState().Snapshot; }
        set
        {
            GetActiveQuotaRuntimeState().Snapshot = value;
            GetActiveRadarFamilyState().Touch();
        }
    }

    private CodexRadarSnapshot codexRadarSnapshot
    {
        get { return GetActiveRadarFamilyState().RadarSnapshot; }
        set
        {
            RadarFamilyRuntimeState state = GetActiveRadarFamilyState();
            state.RadarSnapshot = value;
            state.ModelKey = this.CurrentSettings == null
                ? string.Empty
                : GetSelectedRadarModelKeyForSoftwareMode(state.Family);
            state.Touch();
        }
    }

    private bool quotaSourceKnown
    {
        get { return GetActiveQuotaRuntimeState().SourceKnown; }
        set
        {
            GetActiveQuotaRuntimeState().SourceKnown = value;
            GetActiveRadarFamilyState().Touch();
        }
    }

    private int lastFiveHourQuotaReadPercent
    {
        get { return GetActiveQuotaRuntimeState().LastFiveHourReadPercent; }
        set { GetActiveQuotaRuntimeState().LastFiveHourReadPercent = value; }
    }

    private int lastWeeklyQuotaReadPercent
    {
        get { return GetActiveQuotaRuntimeState().LastWeeklyReadPercent; }
        set { GetActiveQuotaRuntimeState().LastWeeklyReadPercent = value; }
    }

    private DateTime lastQuotaReadDeltaSourceUtc
    {
        get { return GetActiveQuotaRuntimeState().LastReadSourceUtc; }
        set { GetActiveQuotaRuntimeState().LastReadSourceUtc = value; }
    }

    private DateTime lastQuotaRefreshUtc
    {
        get { return GetActiveQuotaRuntimeState().LastRefreshUtc; }
        set { GetActiveQuotaRuntimeState().LastRefreshUtc = value; }
    }

    private DateTime nextQuotaInactiveRefreshUtc
    {
        get { return GetActiveQuotaRuntimeState().NextInactiveRefreshUtc; }
        set { GetActiveQuotaRuntimeState().NextInactiveRefreshUtc = value; }
    }

    private int fiveHourConsumptionRingBaselinePercent
    {
        get { return GetActiveQuotaRuntimeState().FiveHourConsumptionRingBaselinePercent; }
        set
        {
            GetActiveQuotaRuntimeState().FiveHourConsumptionRingBaselinePercent = value;
            GetActiveRadarFamilyState().Touch();
        }
    }

    private DateTime trackedFiveHourResetLocal
    {
        get { return GetActiveQuotaRuntimeState().TrackedFiveHourResetLocal; }
        set { GetActiveQuotaRuntimeState().TrackedFiveHourResetLocal = value; }
    }

    private int weeklyQuotaAtFiveHourWindowStartPercent
    {
        get { return GetActiveQuotaRuntimeState().WeeklyQuotaAtFiveHourWindowStartPercent; }
        set
        {
            GetActiveQuotaRuntimeState().WeeklyQuotaAtFiveHourWindowStartPercent = value;
            GetActiveRadarFamilyState().Touch();
        }
    }

    private bool weeklyResetRainbowActive
    {
        get { return GetActiveQuotaRuntimeState().WeeklyResetRainbowActive; }
    }

    private DateTime fiveHourQuotaProtectionUtc
    {
        get { return GetCodexQuotaProtectionState().FiveHourProtectionUtc; }
        set { GetCodexQuotaProtectionState().FiveHourProtectionUtc = value; }
    }

    private DateTime weeklyQuotaProtectionUtc
    {
        get { return GetCodexQuotaProtectionState().WeeklyProtectionUtc; }
        set { GetCodexQuotaProtectionState().WeeklyProtectionUtc = value; }
    }

    private bool fiveHourQuotaProtectionGold
    {
        get { return GetCodexQuotaProtectionState().FiveHourProtectionGold; }
        set { GetCodexQuotaProtectionState().FiveHourProtectionGold = value; }
    }

    private bool weeklyQuotaProtectionGold
    {
        get { return GetCodexQuotaProtectionState().WeeklyProtectionGold; }
        set { GetCodexQuotaProtectionState().WeeklyProtectionGold = value; }
    }

    private string lastRadarResetEventId
    {
        get { return GetCodexQuotaProtectionState().LastRadarResetEventId ?? string.Empty; }
        set { GetCodexQuotaProtectionState().LastRadarResetEventId = value ?? string.Empty; }
    }

    private DateTime lastRadarResetEventUtc
    {
        get { return GetCodexQuotaProtectionState().LastRadarResetEventUtc; }
        set { GetCodexQuotaProtectionState().LastRadarResetEventUtc = value; }
    }

    private string lastRadarProtectedResetEventId
    {
        get { return GetCodexQuotaProtectionState().LastRadarProtectedResetEventId ?? string.Empty; }
        set { GetCodexQuotaProtectionState().LastRadarProtectedResetEventId = value ?? string.Empty; }
    }

    private string lastRadarOpenEventId
    {
        get { return GetCodexQuotaProtectionState().LastRadarOpenEventId ?? string.Empty; }
        set { GetCodexQuotaProtectionState().LastRadarOpenEventId = value ?? string.Empty; }
    }

    private DateTime lastRadarOpenEventUtc
    {
        get { return GetCodexQuotaProtectionState().LastRadarOpenEventUtc; }
        set { GetCodexQuotaProtectionState().LastRadarOpenEventUtc = value; }
    }

    private ServiceHealthState radarServiceHealth
    {
        get { return GetActiveRadarFamilyState().RadarSiteHealth; }
        set
        {
            GetActiveRadarFamilyState().RadarSiteHealth = value;
            GetActiveRadarFamilyState().Touch();
        }
    }

    private bool codexRadarStatusRequestRunning
    {
        get { return GetActiveRadarFamilyState().RadarStatusRequestRunning; }
        set { GetActiveRadarFamilyState().RadarStatusRequestRunning = value; }
    }

    private DateTime nextCodexRadarStatusRefreshUtc
    {
        get { return GetActiveRadarFamilyState().NextRadarStatusRefreshUtc; }
        set { GetActiveRadarFamilyState().NextRadarStatusRefreshUtc = value; }
    }

    private string codexRadarStatusRefreshTrigger
    {
        get { return GetActiveRadarFamilyState().RadarStatusRefreshTrigger ?? string.Empty; }
        set { GetActiveRadarFamilyState().RadarStatusRefreshTrigger = value ?? string.Empty; }
    }

    private DateTime lastCodexRadarStatusAttemptLocal
    {
        get { return GetActiveRadarFamilyState().LastRadarStatusAttemptLocal; }
        set { GetActiveRadarFamilyState().LastRadarStatusAttemptLocal = value; }
    }

    private string codexApiServiceAlertSignature
    {
        get { return GetActiveRadarFamilyState().ApiAlertDebounce.Signature; }
        set { GetActiveRadarFamilyState().ApiAlertDebounce.Signature = value ?? string.Empty; }
    }

    private int codexApiServiceAlertIndex
    {
        get { return GetActiveRadarFamilyState().ApiAlertDebounce.Index; }
        set { GetActiveRadarFamilyState().ApiAlertDebounce.Index = value; }
    }

    private bool codexApiServiceAlertNamePhase
    {
        get { return GetActiveRadarFamilyState().ApiAlertDebounce.NamePhase; }
        set { GetActiveRadarFamilyState().ApiAlertDebounce.NamePhase = value; }
    }

    private Dictionary<string, ServiceAlertDebounceState> codexApiServiceAlertDebounceStates
    {
        get { return GetActiveRadarFamilyState().ApiAlertDebounce.States; }
    }
}
