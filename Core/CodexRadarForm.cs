using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Owns the hidden Codex/Claude data scheduler and publishes cache-only snapshots to the visible
/// metric tiles and Codex IQ board.
/// </summary>
// The retired visible render variant has no runtime setting; this form remains only as the
// message-pump-backed owner for scheduling, caches and immutable projection publication.
internal sealed partial class CodexRadarForm : LayeredWidgetFormBase
{
    private const int CodexRadarSecondBoundaryOffsetMs = 30;
    private const int QuotaTailChunkBytes = 1024 * 1024;
    private const int MaxQuotaRolloutFilesToScan = 80;
    private const string ClaudeStatusUrl = "https://status.claude.com/api/v2/status.json";
    private const int ClaudeStatusTimeoutMs = 10000;
    private const string OpenAiStatusUrl = "https://status.openai.com/api/v2/summary.json";
    private const int OpenAiStatusTimeoutMs = 10000;
    private const string CodexRadarStatusUrl = "https://codexradar.com/current.json";
    private const string CodexRadarHomeUrl = "https://codexradar.com/";
    private const string CodexRadarFullApiUrl = "https://codexradar.com/api/v1/current";
    // Keep probes enabled because the compact one-line API summary consumes their states.
    private static readonly bool ServiceHealthProbeEnabled = true;
    private const int CodexModelIqNominalTasks = WidgetSettings.DefaultCodexModelIqBaselineValidTasks;
    // The public distributed Radar can grow beyond the legacy manual-test setting cap (currently
    // 112 tasks per cell). Keep source counts bounded for cache safety without truncating them to
    // the old 100-task UI/test limit.
    private const int MaxCodexModelIqSourceTasks = 10000;
    private const int MaxCodexModelIqScore = 1000;
    private const double CodexModelIqWebsiteScoreScale = 150.0;
    private const int CodexModelIqWebsiteNormalLowScore = 90;
    private const int CodexModelIqWebsiteNormalHighScore = 110;
    private const int CodexRadarStatusTimeoutMs = 10000;
    private const int CodexModelHistoryDays = 366;
    private const int CodexModelCacheRetentionDays = 7;
    private const double QuotaIdentityToleranceMinutes = 2.0;
    private const double QuotaNewbornToleranceMinutes = 8.0;
    private const double QuotaResetEventCorroborationHours = 6.0;
    private const double QuotaGapRebaselineMinutes = 30.0;
    private const int QuotaRejectedPersistenceMinSamples = 3;
    private const double QuotaRejectedPersistenceMinMinutes = 10.0;
    // An idle provider pool reports balance=100 with reset=now+window on every sample, which is
    // indistinguishable from a genuine newborn window in a single reading. These two suppressions
    // encode the difference over time: a real newborn happens at most once per window, and never
    // minutes after the displayed pool was seen actively consuming.
    private const double QuotaNewbornSuppressAfterConsumptionMinutes = 30.0;
    private const double QuotaRejectedPersistenceStaleGapMinutes = 15.0;
    // Each quota window owns an active-time trend and a recent wall-clock trend. The short window
    // reacts to bursts; the weekly window smooths integer-percent provider steps. Both remain
    // in-memory and are invalidated by reset identity or balance increases.
    private const double FiveHourBurnRateWindowActiveHours = 1.5;
    private const double WeeklyBurnRateWindowActiveHours = 6.0;
    private const double FiveHourBurnRateWindowWallHours = 5.0;
    private const double WeeklyBurnRateWindowWallHours = 24.0;
    private const double WeeklyBurnRateMinimumActiveMinutes = 10.0;
    private const double BurnRateMinimumWallMinutes = 30.0;
    private const double WeeklyBurnClockMaximumGapSeconds = 90.0;
    private const double WeeklyBurnSampleMinimumSpacingSeconds = 60.0;
    private const int WeeklyBurnSampleLimit = 256;
    private readonly System.Windows.Forms.Timer timer;
    private readonly Action<string, string, ToolTipIcon> notificationAction;
    // Cache the newest rollout result while its identity and append-sensitive metadata stay unchanged.
    private static readonly object codexQuotaSnapshotCacheLock = new object();
    private static readonly object codexRadarDiskCacheLock = new object();
    private static string codexQuotaSnapshotCachePath = string.Empty;
    private static DateTime codexQuotaSnapshotCacheWriteUtc;
    private static long codexQuotaSnapshotCacheLength = -1;
    private static CodexQuotaSnapshot codexQuotaSnapshotCache;
    private static DateTime codexQuotaSnapshotNewestVerifyUtc;
    private readonly object claudeStatusLock = new object();
    private readonly object openAiStatusLock = new object();
    private readonly object codexRadarStatusLock = new object();
    private readonly object quotaResetStateLock = new object();
    private readonly object serviceHealthLock = new object();
    private readonly object codexRadarNotificationStateLock = new object();
    private readonly object codexIqCatalogSnapshotLock = new object();
    private readonly OwnerOperationGeneration ownerOperationGeneration = new OwnerOperationGeneration();
    private List<CodexRadarModelInfo> codexIqCatalogSnapshot = new List<CodexRadarModelInfo>();
    private long codexIqCatalogRevision;
    private readonly Dictionary<string, string> codexRadarNotificationState =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // This type is now permanently a data owner. Keeping the invariant readonly prevents a stale
    // caller from resurrecting the retired layered window before StartHeadlessDataOwner runs.
    private readonly bool headlessDataOwner = true;
    private bool backendSchedulerStarted;
    private bool backendResourcesDisposed;
    private DateTime lastQuotaProcessCheckUtc;
    private bool quotaCodexProcessRunning;
    private SoftwareRuntimePresenceSnapshot softwareRuntimePresenceSnapshot = SoftwareRuntimePresenceSnapshot.Empty();
    private DateTime nextClaudeStatusRefreshUtc;
    private bool claudeStatusRequestRunning;
    private string claudeStatusRefreshTrigger = "启动刷新";
    private DateTime nextOpenAiStatusRefreshUtc;
    private bool openAiStatusRequestRunning;
    private string openAiStatusRefreshTrigger = "启动刷新";
    private readonly object codexRadarDisplayModeCacheLock = new object();
    private readonly Dictionary<CodexRadarSoftwareMode, CodexRadarDisplayModeCache> codexRadarDisplayModeCache =
        new Dictionary<CodexRadarSoftwareMode, CodexRadarDisplayModeCache>();
    private int codexRadarServiceProbeToken = int.MinValue;
    private bool codexRadarServiceProbeRunning;
    private int codexRadarRandomTestRefreshToken = int.MinValue;
    private DateTime nextCodexRadarRandomTestRefreshUtc;
    private CodexRadarRandomTestSnapshot codexRadarRandomTestSnapshot;
    private IntPtr displayPowerNotificationHandle;
    private bool codexDisplayActive = true;
    private bool codexSessionActive = true;
    private bool codexPowerSuspended;
    private bool codexResumePrimePending;
    private int codexResumePrimeCountForSelfTest;
    private bool serviceNetworkAvailable = true;
    private ServiceHealthState openAiServiceHealth = ServiceHealthState.Unknown;
    private ServiceHealthState claudeServiceHealth = ServiceHealthState.Unknown;
    private bool serviceNetworkRefreshRequested = true;
    private string lastRadarClockAutoSwitchSignature = string.Empty;
    private FileSystemWatcher quotaSessionWatcher;
    private string quotaSessionsPath = string.Empty;
    private int quotaSessionFilesChanged = 1;
    private CodexTaskMonitorReader codexTaskMonitorReader;
    private int codexTaskMonitorReconcileRequested = 1;
    private int codexTaskMonitorReconcileRunning;
    private DateTime nextCodexTaskMonitorReconcileUtc = DateTime.MinValue;
    private DateTime nextCodexTaskMonitorStatusRefreshUtc = DateTime.MinValue;

    private enum ServiceHealthState
    {
        Unknown,
        Normal,
        Degraded,
        Incomplete,
        Offline,
        Unavailable,
        Unreachable
    }

    private sealed class CodexRadarProbeResponse
    {
        public bool TransportSucceeded { get; set; }
        public int StatusCode { get; set; }
        public string ContentType { get; set; }
        public string Content { get; set; }
        public string Error { get; set; }
    }

    private sealed class CodexRadarResetEvent
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime EventUtc { get; set; }
        public bool EventUtcKnown { get; set; }
    }

    private sealed class CodexRadarRandomTestSnapshot
    {
        public CodexRadarSnapshot Radar { get; set; }
        public CodexQuotaSnapshot Quota { get; set; }
        public ServiceHealthState RadarHealth { get; set; }
        public ServiceHealthState ClaudeHealth { get; set; }
        public ServiceHealthState OpenAiHealth { get; set; }
        public bool NetworkAvailable { get; set; }
        public bool CodexRunning { get; set; }
        public bool FiveHourGold { get; set; }
        public bool WeeklyGold { get; set; }
        public int FiveHourDropPercent { get; set; }
        public int WeeklyUsedSinceFiveHourResetPercent { get; set; }
    }

    private sealed class CodexQuotaSnapshot
    {
        public int FiveHourPercent { get; set; }
        public int WeeklyPercent { get; set; }
        public DateTime FiveHourResetLocal { get; set; }
        public DateTime WeeklyResetLocal { get; set; }
        public bool FiveHourResetKnown { get; set; }
        public bool WeeklyResetKnown { get; set; }
        public DateTime SourceUpdatedUtc { get; set; }
        public bool SourceUpdatedKnown { get; set; }
        public string SourceKind { get; set; }
        public string FiveHourUsedFieldName { get; set; }
        public string WeeklyUsedFieldName { get; set; }
        public double FiveHourRawUsedValue { get; set; }
        public double WeeklyRawUsedValue { get; set; }
        public double FiveHourNormalizedUsedPercent { get; set; }
        public double WeeklyNormalizedUsedPercent { get; set; }
        public bool FiveHourUsageDiagnosticKnown { get; set; }
        public bool WeeklyUsageDiagnosticKnown { get; set; }
        // True when the source reported a weekly window but no short (~5h) window at all - the
        // provider temporarily lifted the 5h limit. The five-hour ring then shows a full "无限"
        // state instead of inheriting whatever landed in the first payload slot.
        public bool FiveHourLimitAbsent { get; set; }
        public int ProviderHttpStatus { get; set; }
        public int ProviderResponseBytes { get; set; }
        public string ProviderResponseBodySha256 { get; set; }
        public string ProviderPlan { get; set; }
        public string ProviderPool { get; set; }
        public string ProviderCorrelationId { get; set; }

        public static CodexQuotaSnapshot CreateDefault()
        {
            return new CodexQuotaSnapshot
            {
                FiveHourPercent = 100,
                WeeklyPercent = 100,
                FiveHourResetLocal = DateTime.MinValue,
                WeeklyResetLocal = DateTime.MinValue,
                FiveHourResetKnown = false,
                WeeklyResetKnown = false,
                SourceUpdatedUtc = DateTime.MinValue,
                SourceUpdatedKnown = false,
                SourceKind = "default",
                FiveHourUsedFieldName = string.Empty,
                WeeklyUsedFieldName = string.Empty,
                FiveHourRawUsedValue = 0.0,
                WeeklyRawUsedValue = 0.0,
                FiveHourNormalizedUsedPercent = 0.0,
                WeeklyNormalizedUsedPercent = 0.0,
                FiveHourUsageDiagnosticKnown = false,
                WeeklyUsageDiagnosticKnown = false,
                FiveHourLimitAbsent = false,
                ProviderHttpStatus = 0,
                ProviderResponseBytes = 0,
                ProviderResponseBodySha256 = string.Empty,
                ProviderPlan = "unknown",
                ProviderPool = "unknown",
                ProviderCorrelationId = string.Empty
            };
        }

        public CodexQuotaSnapshot Clone()
        {
            return new CodexQuotaSnapshot
            {
                FiveHourPercent = this.FiveHourPercent,
                WeeklyPercent = this.WeeklyPercent,
                FiveHourResetLocal = this.FiveHourResetLocal,
                WeeklyResetLocal = this.WeeklyResetLocal,
                FiveHourResetKnown = this.FiveHourResetKnown,
                WeeklyResetKnown = this.WeeklyResetKnown,
                SourceUpdatedUtc = this.SourceUpdatedUtc,
                SourceUpdatedKnown = this.SourceUpdatedKnown,
                SourceKind = this.SourceKind,
                FiveHourUsedFieldName = this.FiveHourUsedFieldName,
                WeeklyUsedFieldName = this.WeeklyUsedFieldName,
                FiveHourRawUsedValue = this.FiveHourRawUsedValue,
                WeeklyRawUsedValue = this.WeeklyRawUsedValue,
                FiveHourNormalizedUsedPercent = this.FiveHourNormalizedUsedPercent,
                WeeklyNormalizedUsedPercent = this.WeeklyNormalizedUsedPercent,
                FiveHourUsageDiagnosticKnown = this.FiveHourUsageDiagnosticKnown,
                WeeklyUsageDiagnosticKnown = this.WeeklyUsageDiagnosticKnown,
                FiveHourLimitAbsent = this.FiveHourLimitAbsent,
                ProviderHttpStatus = this.ProviderHttpStatus,
                ProviderResponseBytes = this.ProviderResponseBytes,
                ProviderResponseBodySha256 = this.ProviderResponseBodySha256,
                ProviderPlan = this.ProviderPlan,
                ProviderPool = this.ProviderPool,
                ProviderCorrelationId = this.ProviderCorrelationId
            };
        }
    }

    private sealed class CodexRadarDisplayModeCache
    {
        public string ModelKey { get; set; }
        public CodexRadarSnapshot RadarSnapshot { get; set; }
        public CodexQuotaSnapshot QuotaSnapshot { get; set; }
        public bool QuotaSourceKnown { get; set; }
        public ServiceHealthState RadarHealth { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class CodexQuotaEvent
    {
        public CodexQuotaSnapshot Snapshot { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class QuotaRingDecisionInfo
    {
        public string Reason { get; set; }
        public bool SourceKnown { get; set; }
        public bool SnapshotKnown { get; set; }
        public string SourceKind { get; set; }
        public int RawFiveHourPercent { get; set; } = -1;
        public int RawWeeklyPercent { get; set; } = -1;
        public string RawFiveHourUsedFieldName { get; set; }
        public string RawWeeklyUsedFieldName { get; set; }
        public double RawFiveHourUsedValue { get; set; }
        public double RawWeeklyUsedValue { get; set; }
        public double RawFiveHourNormalizedUsedPercent { get; set; }
        public double RawWeeklyNormalizedUsedPercent { get; set; }
        public bool RawFiveHourUsageDiagnosticKnown { get; set; }
        public bool RawWeeklyUsageDiagnosticKnown { get; set; }
        public DateTime RawSourceUpdatedUtc { get; set; }
        public bool RawSourceUpdatedKnown { get; set; }
        public DateTime RawFiveHourResetLocal { get; set; }
        public DateTime RawWeeklyResetLocal { get; set; }
        public string IdentityDecisionReason { get; set; }
        public double? FiveHourAnchorAgeMinutes { get; set; }
        public double? WeeklyAnchorAgeMinutes { get; set; }
        public bool IdentitySampleRejected { get; set; }
        public int FiveHourRejectedPersistenceCount { get; set; }
        public DateTime FiveHourRejectedPersistenceFirstSeenUtc { get; set; }
        public int WeeklyRejectedPersistenceCount { get; set; }
        public DateTime WeeklyRejectedPersistenceFirstSeenUtc { get; set; }
        public int PreviousFiveHourPercent { get; set; } = -1;
        public int PreviousWeeklyPercent { get; set; } = -1;
        public DateTime PreviousSourceUpdatedUtc { get; set; }
        public int PreviousFiveHourBaselinePercent { get; set; } = -1;
        public int PreviousWeeklyBaselinePercent { get; set; } = -1;
        public DateTime PreviousTrackedFiveHourResetLocal { get; set; }
        public DateTime PreviousTrackedWeeklyResetLocal { get; set; }
        public int NextFiveHourBaselinePercent { get; set; } = -1;
        public int NextWeeklyBaselinePercent { get; set; } = -1;
        public DateTime NextTrackedFiveHourResetLocal { get; set; }
        public DateTime NextTrackedWeeklyResetLocal { get; set; }
        public DateTime NextSourceUpdatedUtc { get; set; }
    }

    private sealed class QuotaWindowIdentityDecision
    {
        public bool IdentitySame { get; set; }
        public bool Accepted { get; set; }
        public string Reason { get; set; }
        public double? AnchorAgeMinutes { get; set; }
        public int RejectedPersistenceCount { get; set; }
        public DateTime RejectedPersistenceFirstSeenUtc { get; set; }
    }

    private sealed class QuotaProtectionOptions
    {
        public bool DueResetProtectionEnabled { get; set; }
        public bool RssResetProtectionEnabled { get; set; }
        public bool ProviderZeroDropProtectionEnabled { get; set; }
        public bool DuplicateSameBalanceRingProtectionEnabled { get; set; }
        public bool ProviderFiveHourEarlyResetSpikeProtectionEnabled { get; set; }
        public bool ProviderWeeklySpikeProtectionEnabled { get; set; }
        public bool StrictFiveHourResetBoundaryEnabled { get; set; }
        public bool WeeklyBaselineAutoRepairEnabled { get; set; }

        public static QuotaProtectionOptions FromSettings(WidgetSettings settings)
        {
            return new QuotaProtectionOptions
            {
                DueResetProtectionEnabled = settings == null || settings.CodexQuotaDueResetProtectionEnabled,
                RssResetProtectionEnabled = settings == null || settings.CodexQuotaRssResetProtectionEnabled,
                ProviderZeroDropProtectionEnabled = settings == null || settings.CodexQuotaProviderZeroDropProtectionEnabled,
                DuplicateSameBalanceRingProtectionEnabled = settings == null || settings.CodexQuotaDuplicateSameBalanceRingProtectionEnabled,
                ProviderFiveHourEarlyResetSpikeProtectionEnabled = settings != null && settings.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled,
                ProviderWeeklySpikeProtectionEnabled = settings != null && settings.CodexQuotaProviderWeeklySpikeProtectionEnabled,
                StrictFiveHourResetBoundaryEnabled = settings != null && settings.CodexQuotaStrictFiveHourResetBoundaryEnabled,
                WeeklyBaselineAutoRepairEnabled = settings != null && settings.CodexQuotaWeeklyBaselineAutoRepairEnabled
            };
        }

        public static QuotaProtectionOptions LegacyRuntimeDefaults()
        {
            return new QuotaProtectionOptions
            {
                DueResetProtectionEnabled = true,
                RssResetProtectionEnabled = true,
                ProviderZeroDropProtectionEnabled = true,
                DuplicateSameBalanceRingProtectionEnabled = true,
                ProviderFiveHourEarlyResetSpikeProtectionEnabled = true,
                ProviderWeeklySpikeProtectionEnabled = true,
                StrictFiveHourResetBoundaryEnabled = true,
                WeeklyBaselineAutoRepairEnabled = true
            };
        }
    }

    private sealed class CodexRadarSnapshot
    {
        public DateTime CheckedAtLocal { get; set; }
        public bool CheckedAtKnown { get; set; }
        public DateTime FetchedAtLocal { get; set; }
        public bool FetchedAtKnown { get; set; }
        public DateTime ModelIqSourceUpdatedAtLocal { get; set; }
        public bool ModelIqSourceUpdatedAtKnown { get; set; }
        // Local first-seen marker used by the refresh dial. It is intentionally distinct from
        // ModelIqSourceUpdatedAtLocal so transport time can never masquerade as source time.
        public DateTime ModelIqRefreshedAtLocal { get; set; }
        public string ModelIqCachedContentSignature { get; set; }
        public DateTime ModelIqDataDateLocal { get; set; }
        public int ModelIqDataWindowStartHourLocal { get; set; }
        public string ModelIqDataLabel { get; set; }
        public bool ModelIqRefreshedAtKnown { get; set; }
        public bool ModelIqDataDateKnown { get; set; }
        public bool ModelIqDataWindowKnown { get; set; }
        public bool ModelIqDataLabelKnown { get; set; }
        public bool ModelIqRefreshSucceeded { get; set; }
        public bool SpeedWindowKnown { get; set; }
        public bool SpeedWindowOpen { get; set; }
        public string SpeedWindowStatus { get; set; }
        public string SpeedWindowEventId { get; set; }
        public DateTime SpeedWindowOpenedAtLocal { get; set; }
        public DateTime SpeedWindowClosedAtLocal { get; set; }
        public bool SpeedWindowOpenedAtKnown { get; set; }
        public bool SpeedWindowClosedAtKnown { get; set; }
        public bool ResetRadarKnown { get; set; }
        public DateTime ResetRadarUpdatedAtLocal { get; set; }
        public bool ResetRadarUpdatedAtKnown { get; set; }
        public string ResetCardStatus { get; set; }
        public string ResetCardDescription { get; set; }
        public string HardResetStatus { get; set; }
        public string HardResetDescription { get; set; }
        public bool ResetEventKnown { get; set; }
        public string ResetEventId { get; set; }
        public string ResetEventTitle { get; set; }
        public DateTime ResetEventUtc { get; set; }
        public string ModelIqStatus { get; set; }
        public int ModelIqPassRatePercent { get; set; }
        public int ModelIqPassed { get; set; }
        public int ModelIqValidTasks { get; set; }
        public int ModelIqTokenEfficiencyPercent { get; set; }
        public int ModelIqTimeEfficiencyPercent { get; set; }
        public int ModelIqNormalLowScore { get; set; }
        public int ModelIqNormalHighScore { get; set; }
        public bool ModelIqNormalRangeKnown { get; set; }
        public double ModelIqDisplayMaxScore { get; set; }
        public bool ModelIqDisplayMaxScoreKnown { get; set; }
        public double ModelIqEfficiencyPassed { get; set; }
        public double ModelIqEfficiencyTotalTokens { get; set; }
        public double ModelIqEfficiencySerialSeconds { get; set; }
        public bool ModelIqPassedKnown { get; set; }
        public bool ModelIqEfficiencyInputKnown { get; set; }
        public bool ModelIqEfficiencyKnown { get; set; }
        public bool ModelIqKnown { get; set; }
        public List<CodexModelHistoryPoint> ModelIqHistory { get; set; }
        public List<CodexIqBoardModelPoint> CodexIqModels { get; set; }
        public List<RadarClockModelCandidate> ClockModelCandidates { get; set; }

        public static CodexRadarSnapshot CreateDefault()
        {
            return new CodexRadarSnapshot
            {
                CheckedAtLocal = DateTime.MinValue,
                CheckedAtKnown = false,
                FetchedAtLocal = DateTime.MinValue,
                FetchedAtKnown = false,
                ModelIqSourceUpdatedAtLocal = DateTime.MinValue,
                ModelIqSourceUpdatedAtKnown = false,
                ModelIqRefreshedAtLocal = DateTime.MinValue,
                ModelIqCachedContentSignature = string.Empty,
                ModelIqDataDateLocal = DateTime.MinValue,
                ModelIqDataWindowStartHourLocal = 0,
                ModelIqDataLabel = string.Empty,
                ModelIqRefreshedAtKnown = false,
                ModelIqDataDateKnown = false,
                ModelIqDataWindowKnown = false,
                ModelIqDataLabelKnown = false,
                ModelIqRefreshSucceeded = false,
                SpeedWindowKnown = false,
                SpeedWindowOpen = false,
                SpeedWindowStatus = string.Empty,
                SpeedWindowEventId = string.Empty,
                SpeedWindowOpenedAtLocal = DateTime.MinValue,
                SpeedWindowClosedAtLocal = DateTime.MinValue,
                SpeedWindowOpenedAtKnown = false,
                SpeedWindowClosedAtKnown = false,
                ResetRadarKnown = false,
                ResetRadarUpdatedAtLocal = DateTime.MinValue,
                ResetRadarUpdatedAtKnown = false,
                ResetCardStatus = string.Empty,
                ResetCardDescription = string.Empty,
                HardResetStatus = string.Empty,
                HardResetDescription = string.Empty,
                ResetEventKnown = false,
                ResetEventId = string.Empty,
                ResetEventTitle = string.Empty,
                ResetEventUtc = DateTime.MinValue,
                ModelIqStatus = "invalid",
                ModelIqPassRatePercent = 0,
                ModelIqPassed = 0,
                ModelIqValidTasks = CodexModelIqNominalTasks,
                ModelIqTokenEfficiencyPercent = 100,
                ModelIqTimeEfficiencyPercent = 100,
                ModelIqNormalLowScore = 90,
                ModelIqNormalHighScore = 110,
                ModelIqNormalRangeKnown = false,
                ModelIqDisplayMaxScore = 0.0,
                ModelIqDisplayMaxScoreKnown = false,
                ModelIqEfficiencyPassed = 0.0,
                ModelIqEfficiencyTotalTokens = 0.0,
                ModelIqEfficiencySerialSeconds = 0.0,
                ModelIqPassedKnown = false,
                ModelIqEfficiencyInputKnown = false,
                ModelIqEfficiencyKnown = false,
                ModelIqKnown = false,
                ModelIqHistory = new List<CodexModelHistoryPoint>(),
                CodexIqModels = new List<CodexIqBoardModelPoint>(),
                ClockModelCandidates = new List<RadarClockModelCandidate>()
            };
        }

        public CodexRadarSnapshot Clone()
        {
            return new CodexRadarSnapshot
            {
                CheckedAtLocal = this.CheckedAtLocal,
                CheckedAtKnown = this.CheckedAtKnown,
                FetchedAtLocal = this.FetchedAtLocal,
                FetchedAtKnown = this.FetchedAtKnown,
                ModelIqSourceUpdatedAtLocal = this.ModelIqSourceUpdatedAtLocal,
                ModelIqSourceUpdatedAtKnown = this.ModelIqSourceUpdatedAtKnown,
                ModelIqRefreshedAtLocal = this.ModelIqRefreshedAtLocal,
                ModelIqCachedContentSignature = this.ModelIqCachedContentSignature,
                ModelIqDataDateLocal = this.ModelIqDataDateLocal,
                ModelIqDataWindowStartHourLocal = this.ModelIqDataWindowStartHourLocal,
                ModelIqDataLabel = this.ModelIqDataLabel,
                ModelIqRefreshedAtKnown = this.ModelIqRefreshedAtKnown,
                ModelIqDataDateKnown = this.ModelIqDataDateKnown,
                ModelIqDataWindowKnown = this.ModelIqDataWindowKnown,
                ModelIqDataLabelKnown = this.ModelIqDataLabelKnown,
                ModelIqRefreshSucceeded = this.ModelIqRefreshSucceeded,
                SpeedWindowKnown = this.SpeedWindowKnown,
                SpeedWindowOpen = this.SpeedWindowOpen,
                SpeedWindowStatus = this.SpeedWindowStatus,
                SpeedWindowEventId = this.SpeedWindowEventId,
                SpeedWindowOpenedAtLocal = this.SpeedWindowOpenedAtLocal,
                SpeedWindowClosedAtLocal = this.SpeedWindowClosedAtLocal,
                SpeedWindowOpenedAtKnown = this.SpeedWindowOpenedAtKnown,
                SpeedWindowClosedAtKnown = this.SpeedWindowClosedAtKnown,
                ResetRadarKnown = this.ResetRadarKnown,
                ResetRadarUpdatedAtLocal = this.ResetRadarUpdatedAtLocal,
                ResetRadarUpdatedAtKnown = this.ResetRadarUpdatedAtKnown,
                ResetCardStatus = this.ResetCardStatus,
                ResetCardDescription = this.ResetCardDescription,
                HardResetStatus = this.HardResetStatus,
                HardResetDescription = this.HardResetDescription,
                ResetEventKnown = this.ResetEventKnown,
                ResetEventId = this.ResetEventId,
                ResetEventTitle = this.ResetEventTitle,
                ResetEventUtc = this.ResetEventUtc,
                ModelIqStatus = this.ModelIqStatus,
                ModelIqPassRatePercent = this.ModelIqPassRatePercent,
                ModelIqPassed = this.ModelIqPassed,
                ModelIqValidTasks = this.ModelIqValidTasks,
                ModelIqTokenEfficiencyPercent = this.ModelIqTokenEfficiencyPercent,
                ModelIqTimeEfficiencyPercent = this.ModelIqTimeEfficiencyPercent,
                ModelIqNormalLowScore = this.ModelIqNormalLowScore,
                ModelIqNormalHighScore = this.ModelIqNormalHighScore,
                ModelIqNormalRangeKnown = this.ModelIqNormalRangeKnown,
                ModelIqDisplayMaxScore = this.ModelIqDisplayMaxScore,
                ModelIqDisplayMaxScoreKnown = this.ModelIqDisplayMaxScoreKnown,
                ModelIqEfficiencyPassed = this.ModelIqEfficiencyPassed,
                ModelIqEfficiencyTotalTokens = this.ModelIqEfficiencyTotalTokens,
                ModelIqEfficiencySerialSeconds = this.ModelIqEfficiencySerialSeconds,
                ModelIqPassedKnown = this.ModelIqPassedKnown,
                ModelIqEfficiencyInputKnown = this.ModelIqEfficiencyInputKnown,
                ModelIqEfficiencyKnown = this.ModelIqEfficiencyKnown,
                ModelIqKnown = this.ModelIqKnown,
                ModelIqHistory = CloneCodexModelHistory(this.ModelIqHistory),
                CodexIqModels = CloneCodexIqBoardModels(this.CodexIqModels),
                ClockModelCandidates = CloneRadarClockModelCandidates(this.ClockModelCandidates)
            };
        }
    }

    private sealed class RadarClockModelCandidate
    {
        public string Key { get; set; }
        public DateTime LatestLocal { get; set; }
        public bool LatestKnown { get; set; }
        public bool HistoricalOnly { get; set; }

        public RadarClockModelCandidate Clone()
        {
            return new RadarClockModelCandidate
            {
                Key = this.Key ?? string.Empty,
                LatestLocal = this.LatestLocal,
                LatestKnown = this.LatestKnown,
                HistoricalOnly = this.HistoricalOnly
            };
        }
    }

    private sealed class CodexModelHistoryPoint
    {
        public DateTime DateLocal { get; set; }
        public double Score { get; set; }
        public double Passed { get; set; }
        public double TotalTokens { get; set; }
        public double SerialSeconds { get; set; }
        public double CachedInputTokens { get; set; }
        public double InputTokens { get; set; }
        public double Tasks { get; set; }
        public double InvalidTasks { get; set; }
        public double TokenEfficiencyPercent { get; set; }
        public double TimeEfficiencyPercent { get; set; }
        public bool EfficiencyKnown { get; set; }
        public bool CacheRateKnown { get; set; }
        public bool ValidityKnown { get; set; }

        public CodexModelHistoryPoint Clone()
        {
            return new CodexModelHistoryPoint
            {
                DateLocal = this.DateLocal,
                Score = this.Score,
                Passed = this.Passed,
                TotalTokens = this.TotalTokens,
                SerialSeconds = this.SerialSeconds,
                CachedInputTokens = this.CachedInputTokens,
                InputTokens = this.InputTokens,
                Tasks = this.Tasks,
                InvalidTasks = this.InvalidTasks,
                TokenEfficiencyPercent = this.TokenEfficiencyPercent,
                TimeEfficiencyPercent = this.TimeEfficiencyPercent,
                EfficiencyKnown = this.EfficiencyKnown,
                CacheRateKnown = this.CacheRateKnown,
                ValidityKnown = this.ValidityKnown
            };
        }
    }

    public CodexRadarForm(
        WidgetSettings settings,
        Action<string, string, ToolTipIcon> notificationAction,
        string quotaHistoryPathOverride = null)
    {
        this.notificationAction = notificationAction;
        this.codexQuotaHistoryStore = new CodexQuotaHistoryStore(quotaHistoryPathOverride);
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        UpdateEffectiveCodexRadarSoftwareMode(true);
        HydrateAllRadarFamilyCaches();
        ReloadCodexIqCatalogSnapshot();
        // LAST REF means the last local IQ request attempt this process made. The website's
        // checked_at/monitored_at is source metadata, not a local attempt, so the
        // marker stays unknown until the first real request instead of borrowing the site time.
        this.lastCodexRadarStatusAttemptLocal = DateTime.MinValue;
        LoadCodexRadarNotificationState();
        LoadQuotaResetState();
        InitializeQuotaSessionWatcher();
        this.codexTaskMonitorReader = new CodexTaskMonitorReader(this.CurrentSettings);
        RequestCodexTaskMonitorReconcile();
        // BACKEND SEAM: this window owns the reader, so it publishes the snapshot for every frontend
        // surface (radar clock task ring, operation launcher badge and task flyout). Reads go through
        // the provider on each paint; a push path (SnapshotChanged -> invalidate, AttentionRaised ->
        // routing) can replace the pull without touching any drawing code.
        CodexTaskPresentation.SnapshotProvider = delegate
        {
            CodexTaskMonitorReader reader = this.codexTaskMonitorReader;
            return reader == null ? CodexTaskMonitorSnapshot.Empty : reader.GetSnapshot();
        };

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.MinimumSize = new Size(1, 1);
        this.MaximumSize = new Size(1, 1);
        this.Size = new Size(1, 1);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextCodexRadarTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.CurrentSettings);
        StartBackendSchedulerIfNeeded();
        if (this.Visible)
        {
            this.Hide();
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        // The visible Codex Radar surface is retired; snapshots remain available to tiles/boards.
        base.SetVisibleCore(false);
    }

    internal void StartHeadlessDataOwner()
    {
        if (this.backendResourcesDisposed || this.IsDisposed)
        {
            throw new ObjectDisposedException("CodexRadarForm");
        }

        StartOrResumeOwnerOperations();

        // A message-only hidden owner still needs a Win32 handle for display-power notifications
        // and BeginInvoke continuations. Creating it here also makes startup independent of Show().
        IntPtr ignoredHandle = this.Handle;
        ApplyRuntimeSettings(this.CurrentSettings);
        StartBackendSchedulerIfNeeded();
        if (this.Visible)
        {
            this.Hide();
        }
    }

    internal void StopHeadlessDataOwner()
    {
        StopOrSuspendOwnerOperations();
        if (this.Visible)
        {
            this.Hide();
        }

        CleanupBackendResources();
    }

    internal bool IsHeadlessDataOwner
    {
        get { return this.headlessDataOwner; }
    }

    internal bool IsBackendSchedulerRunning
    {
        get { return this.backendSchedulerStarted && this.timer.Enabled; }
    }

    private void StartBackendSchedulerIfNeeded()
    {
        if (this.backendResourcesDisposed || this.backendSchedulerStarted)
        {
            return;
        }

        ScheduleNextCodexRadarTick();
        this.timer.Start();
        this.backendSchedulerStarted = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (this.displayPowerNotificationHandle == IntPtr.Zero)
        {
            this.displayPowerNotificationHandle = NativeMethods.RegisterConsoleDisplayStateNotification(this.Handle);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (this.displayPowerNotificationHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerNotification(this.displayPowerNotificationHandle);
            this.displayPowerNotificationHandle = IntPtr.Zero;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CleanupBackendResources();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CleanupBackendResources();
        }

        base.Dispose(disposing);
    }

    private void CleanupBackendResources()
    {
        if (this.backendResourcesDisposed)
        {
            return;
        }

        this.ownerOperationGeneration.Dispose();
        ResetOwnerSingleFlightFlags();
        this.backendResourcesDisposed = true;
        this.backendSchedulerStarted = false;
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        DisposeQuotaSessionWatcher();
        if (this.codexTaskMonitorReader != null)
        {
            CodexTaskPresentation.SnapshotProvider = null;
            this.codexTaskMonitorReader.Dispose();
            this.codexTaskMonitorReader = null;
        }
        this.codexQuotaHistoryStore.Dispose();
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
    }

    private void StartOrResumeOwnerOperations()
    {
        if (this.ownerOperationGeneration.StartOrResume())
        {
            ResetOwnerSingleFlightFlags();
        }
    }

    private void StopOrSuspendOwnerOperations()
    {
        this.ownerOperationGeneration.StopOrSuspend();
        ResetOwnerSingleFlightFlags();
    }

    private OwnerOperationLease CaptureOwnerOperation()
    {
        return this.ownerOperationGeneration.Capture();
    }

    private bool IsOwnerOperationCurrent(OwnerOperationLease lease)
    {
        return this.ownerOperationGeneration.IsCurrent(lease);
    }

    private bool TryExecuteOwnerCurrent(OwnerOperationLease lease, Action action)
    {
        return this.ownerOperationGeneration.TryExecuteCurrent(lease, action);
    }

    private void TryBeginInvokeOwnerCurrent(OwnerOperationLease lease, MethodInvoker callback)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            TryExecuteOwnerCurrent(lease, delegate
            {
                if (this.IsDisposed || !this.IsHandleCreated)
                {
                    return;
                }

                this.BeginInvoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed && this.IsHandleCreated && IsOwnerOperationCurrent(lease))
                    {
                        callback();
                    }
                });
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ResetOwnerSingleFlightFlags()
    {
        lock (this.codexRadarStatusLock)
        {
            this.codexRuntimeState.RadarStatusRequestRunning = false;
            this.claudeRuntimeState.RadarStatusRequestRunning = false;
            this.codexRadarServiceProbeRunning = false;
        }

        lock (this.codexProviderUsageLock)
        {
            this.codexProviderUsageRequestRunning = false;
        }

        lock (this.codexResetCreditsLock)
        {
            this.codexResetCreditsRequestRunning = false;
            CodexResetCreditsSnapshot resetSnapshot = this.codexResetCreditsSnapshot.Clone();
            resetSnapshot.RequestRunning = false;
            this.codexResetCreditsSnapshot = resetSnapshot;
        }

        lock (this.claudeStatusLock)
        {
            this.claudeStatusRequestRunning = false;
        }

        lock (this.openAiStatusLock)
        {
            this.openAiStatusRequestRunning = false;
        }

        PublishProjectionStateFromOwner();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        try
        {
            if (!IsCodexPollingAllowed())
            {
                return;
            }

            // This timer is only a lightweight scheduler. Each data source owns its business
            // interval and single-flight guard, so a faster UI mode does not multiply web traffic.
            RefreshCodexTaskMonitorIfNeeded();
            UpdateEffectiveCodexRadarSoftwareModeIfNeeded();
            UpdateCodexRadarRandomTestIfNeeded();
            if (!this.CurrentSettings.CodexRadarRandomTestEnabled)
            {
                if (ServiceHealthProbeEnabled)
                {
                    UpdateServiceConnectivityHealth();
                }

                RefreshSelectedQuotaInfoIfNeeded();
                RefreshCodexResetCreditsIfNeeded();
                RefreshCodexRadarStatusIfNeeded();
                RefreshDeepSeekServiceIfNeeded();
                if (ServiceHealthProbeEnabled)
                {
                    RefreshClaudeStatusIfNeeded();
                    RefreshOpenAiStatusIfNeeded();
                }

                ApplyRadarClockAutoSwitchIfNeeded();
            }
            PublishProjectionStateFromOwner();
        }
        finally
        {
            ScheduleNextCodexRadarTick();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_POWERBROADCAST)
        {
            HandlePowerBroadcast(m.WParam, m.LParam);
        }

    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        CodexRadarTestMode oldCodexRadarTestMode = this.CurrentSettings.CodexRadarTestMode;
        ServiceHealthTestMode oldServiceHealthTestMode = this.CurrentSettings.ServiceHealthTestMode;
        string oldModelKey = this.CurrentSettings.CodexRadarModelKey;
        CodexRadarSoftwareMode oldConfiguredSoftwareMode = this.CurrentSettings.CodexRadarSoftwareMode;
        CodexRadarSoftwareMode oldEffectiveSoftwareMode = GetEffectiveCodexRadarSoftwareMode();
        bool oldRandomTestEnabled = this.CurrentSettings.CodexRadarRandomTestEnabled;
        int oldRandomTestToken = this.CurrentSettings.CodexRadarRandomTestRefreshToken;
        bool oldPublicJsonEnabled = this.CurrentSettings.CodexRadarPublicJsonEnabled;
        bool oldHtmlFallbackEnabled = this.CurrentSettings.CodexRadarHtmlFallbackEnabled;
        bool oldRssFallbackEnabled = this.CurrentSettings.CodexRadarRssFallbackEnabled;
        int oldServiceProbeToken = this.CurrentSettings.CodexRadarServiceProbeToken;
        CacheCodexRadarDisplayMode(oldEffectiveSoftwareMode);
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        if (this.codexTaskMonitorReader != null)
        {
            this.codexTaskMonitorReader.UpdateSettings(this.CurrentSettings);
            RequestCodexTaskMonitorReconcile();
        }
        bool effectiveSoftwareChanged = UpdateEffectiveCodexRadarSoftwareMode(true);
        ApplyPerformanceTimerIntervals();

        if (this.CurrentSettings.CodexRadarRandomTestEnabled &&
            (!oldRandomTestEnabled ||
             oldRandomTestToken != this.CurrentSettings.CodexRadarRandomTestRefreshToken ||
             this.codexRadarRandomTestSnapshot == null))
        {
            GenerateCodexRadarRandomTestSnapshot();
        }
        else if (oldRandomTestEnabled && !this.CurrentSettings.CodexRadarRandomTestEnabled)
        {
            this.codexRadarRandomTestSnapshot = null;
            PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            RequestServiceNetworkRefresh();
        }

        bool softwareSettingChanged = oldConfiguredSoftwareMode != this.CurrentSettings.CodexRadarSoftwareMode ||
            oldEffectiveSoftwareMode != GetEffectiveCodexRadarSoftwareMode() ||
            effectiveSoftwareChanged;
        if (!string.Equals(oldModelKey, this.CurrentSettings.CodexRadarModelKey, StringComparison.OrdinalIgnoreCase) ||
            softwareSettingChanged)
        {
            if (softwareSettingChanged)
            {
                SwitchCodexRadarSoftwareFamily("软件切换");
            }
            else
            {
                RestoreCodexRadarDisplayForCurrentMode("模型切换");
                RequestSelectedQuotaUsageRefresh("软件切换");
            }
        }

        if (oldPublicJsonEnabled != this.CurrentSettings.CodexRadarPublicJsonEnabled ||
            oldHtmlFallbackEnabled != this.CurrentSettings.CodexRadarHtmlFallbackEnabled ||
            oldRssFallbackEnabled != this.CurrentSettings.CodexRadarRssFallbackEnabled)
        {
            lock (this.codexRadarStatusLock)
            {
                this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
                this.codexRadarStatusRefreshTrigger = "数据源设置变更";
            }

            SetRadarServiceHealth(ServiceHealthState.Unknown);
        }

        if (oldServiceProbeToken != this.CurrentSettings.CodexRadarServiceProbeToken &&
            this.CurrentSettings.CodexRadarServiceProbeToken > 0)
        {
            StartCodexRadarServiceProbe();
        }

        if (oldCodexRadarTestMode != this.CurrentSettings.CodexRadarTestMode)
        {
            if (this.CurrentSettings.CodexRadarTestMode == CodexRadarTestMode.Off)
            {
                PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            }

            PublishProjectionStateFromOwner();
        }

        if (ServiceHealthProbeEnabled &&
            oldServiceHealthTestMode != this.CurrentSettings.ServiceHealthTestMode)
        {
            if (this.CurrentSettings.ServiceHealthTestMode == ServiceHealthTestMode.Off)
            {
                ResetServiceHealthAfterTestMode();
            }
            else
            {
                ApplyServiceHealthTestMode();
            }

            PublishProjectionStateFromOwner();
        }
        else if (ServiceHealthProbeEnabled &&
            this.CurrentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
        }

        PublishProjectionStateFromOwner();
        if (this.Visible)
        {
            this.Hide();
        }
    }

    public void ForceRefresh()
    {
        this.lastQuotaRefreshUtc = DateTime.MinValue;
        this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;
        if (ServiceHealthProbeEnabled)
        {
            RequestServiceNetworkRefresh();
        }

        if (ServiceHealthProbeEnabled)
        {
            lock (this.claudeStatusLock)
            {
                this.nextClaudeStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
                this.claudeStatusRefreshTrigger = "操作面板刷新";
            }
            StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, "操作面板刷新");

            lock (this.openAiStatusLock)
            {
                this.nextOpenAiStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
                this.openAiStatusRefreshTrigger = "操作面板刷新";
            }
            StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, "操作面板刷新");
        }

        lock (this.codexRadarStatusLock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            this.nextCodexRadarStatusRefreshUtc = nowUtc.AddSeconds(4.0);
            this.codexRadarStatusRefreshTrigger = "操作面板刷新";
        }

        RequestDeepSeekServiceRefresh("操作面板刷新");
        RequestSelectedQuotaUsageRefresh("操作面板刷新");
        RequestCodexResetCreditsRefresh("操作面板刷新");

        OnTimerTick(this, EventArgs.Empty);
    }

    // A cold-start fail-closed decision can put the sensitive endpoint schedulers into their
    // normal error backoff. When a fresh non-mainland egress is later confirmed, wake only those
    // existing AI consumers; do not refresh public Radar, DeepSeek, or unrelated network probes.
    public void RequestSensitiveAiRefreshAfterEgressAuthorization(string trigger)
    {
        trigger = string.IsNullOrWhiteSpace(trigger) ? "出口确认境外" : trigger.Trim();
        RequestSelectedQuotaUsageRefresh(trigger);
        RequestCodexResetCreditsRefresh(trigger);

        if (ServiceHealthProbeEnabled)
        {
            lock (this.claudeStatusLock)
            {
                this.nextClaudeStatusRefreshUtc = DateTime.UtcNow;
                this.claudeStatusRefreshTrigger = trigger;
            }

            lock (this.openAiStatusLock)
            {
                this.nextOpenAiStatusRefreshUtc = DateTime.UtcNow;
                this.openAiStatusRefreshTrigger = trigger;
            }

            StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, trigger);
            StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, trigger);
        }

        Program.LogInfo("Sensitive AI schedulers released after egress authorization. Trigger=" + trigger);
    }

    private void StartCodexRadarServiceProbe()
    {
        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        int token = this.CurrentSettings.CodexRadarServiceProbeToken;
        lock (this.codexRadarStatusLock)
        {
            if (token == this.codexRadarServiceProbeToken ||
                this.codexRadarServiceProbeRunning)
            {
                return;
            }

            this.codexRadarServiceProbeToken = token;
            this.codexRadarServiceProbeRunning = true;
        }

        PublishProjectionStateFromOwner();

        string modelKey = this.CurrentSettings.CodexRadarModelKey;
        bool publicJsonEnabled = this.CurrentSettings.CodexRadarPublicJsonEnabled;
        bool htmlFallbackEnabled = this.CurrentSettings.CodexRadarHtmlFallbackEnabled;
        bool rssFallbackEnabled = this.CurrentSettings.CodexRadarRssFallbackEnabled;
        Task.Run((Action)delegate
        {
            string path = string.Empty;
            bool success = false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                string report = BuildCodexRadarServiceProbeReport(
                    modelKey,
                    publicJsonEnabled,
                    htmlFallbackEnabled,
                    rssFallbackEnabled,
                    lease.CancellationToken);
                TryExecuteOwnerCurrent(lease, delegate
                {
                    Directory.CreateDirectory(Logger.DirectoryPath);
                    path = Path.Combine(Logger.DirectoryPath, "codex-radar-service-probe.txt");
                    File.WriteAllText(path, report, SharedEncoding.Utf8NoBom);
                    success = true;
                    if (this.notificationAction != null)
                    {
                        this.notificationAction(
                            "Codex Radar 服务检测完成",
                            "结果已写入 " + path,
                            ToolTipIcon.Info);
                    }
                });
            }
            catch (Exception ex)
            {
                TryExecuteOwnerCurrent(lease, delegate
                {
                    Program.LogException(ex);
                    if (this.notificationAction != null)
                    {
                        this.notificationAction(
                            "Codex Radar 服务检测失败",
                            ex.GetType().Name,
                            ToolTipIcon.Warning);
                    }
                });
            }
            finally
            {
                stopwatch.Stop();
                TryExecuteOwnerCurrent(lease, delegate
                {
                    NetworkCheckHistoryLogger.LogCompleted(
                        "codex_radar",
                        "service_probe",
                        "设置页检测",
                        success ? "完成" : "失败",
                        success,
                        (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                        new Dictionary<string, object>
                        {
                            { "path", path },
                            { "public_json_enabled", publicJsonEnabled },
                            { "html_fallback_enabled", htmlFallbackEnabled },
                            { "rss_fallback_enabled", rssFallbackEnabled }
                        });
                    lock (this.codexRadarStatusLock)
                    {
                        this.codexRadarServiceProbeRunning = false;
                    }

                    PublishProjectionStateFromOwner();
                });
            }
        });
    }

    public void RecoverAfterDisplayResume()
    {
        this.codexPowerSuspended = false;
        this.codexDisplayActive = true;
        this.codexSessionActive = true;
        StartOrResumeOwnerOperations();
        TryPrimeCodexResumeOnce();
        ScheduleNextCodexRadarTick();
    }

    public void PrepareForDisplaySuspend()
    {
        this.codexPowerSuspended = true;
        this.codexDisplayActive = false;
        this.codexResumePrimePending = true;
        StopOrSuspendOwnerOperations();
    }

    private void PrimeCodexWebRefreshSchedule(DateTime nowUtc)
    {
        lock (this.claudeStatusLock)
        {
            this.nextClaudeStatusRefreshUtc = nowUtc.AddSeconds(1.0);
            this.claudeStatusRefreshTrigger = "启动或恢复刷新";
        }
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, "启动或恢复刷新");

        lock (this.openAiStatusLock)
        {
            this.nextOpenAiStatusRefreshUtc = nowUtc.AddSeconds(1.0);
            this.openAiStatusRefreshTrigger = "启动或恢复刷新";
        }
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, "启动或恢复刷新");

        lock (this.codexRadarStatusLock)
        {
            this.nextCodexRadarStatusRefreshUtc = nowUtc.AddSeconds(4.0);
            this.codexRadarStatusRefreshTrigger = "启动或恢复刷新";
        }

        RequestSelectedQuotaUsageRefresh("启动或恢复刷新");
        RequestCodexResetCreditsRefresh("启动或恢复刷新");
    }

    private bool IsCodexPollingAllowed()
    {
        return this.codexDisplayActive && this.codexSessionActive && !this.codexPowerSuspended;
    }

    private void ResumeCodexPollingSoon()
    {
        this.lastQuotaRefreshUtc = DateTime.MinValue;
        this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        PublishProjectionStateFromOwner();
    }

    private void TryPrimeCodexResumeOnce()
    {
        if (!this.codexResumePrimePending || !IsCodexPollingAllowed())
        {
            return;
        }

        this.codexResumePrimePending = false;
        unchecked
        {
            this.codexResumePrimeCountForSelfTest++;
        }

        ResumeCodexPollingSoon();
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    OnSystemSessionSwitch(sender, e);
                });
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            this.codexSessionActive = false;
            this.codexResumePrimePending = true;
            StopOrSuspendOwnerOperations();
            return;
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            this.codexSessionActive = true;
            StartOrResumeOwnerOperations();
            TryPrimeCodexResumeOnce();
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.codexPowerSuspended = true;
            this.codexResumePrimePending = true;
            StopOrSuspendOwnerOperations();
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            this.codexPowerSuspended = false;
            this.codexDisplayActive = true;
            StartOrResumeOwnerOperations();
            TryPrimeCodexResumeOnce();
            return;
        }

        if (eventType == NativeMethods.PBT_POWERSETTINGCHANGE && dataPtr != IntPtr.Zero)
        {
            NativeMethods.POWERBROADCAST_SETTING setting =
                (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                    dataPtr,
                    typeof(NativeMethods.POWERBROADCAST_SETTING));
            if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
            {
                bool active = setting.Data != 0;
                if (this.codexDisplayActive != active)
                {
                    this.codexDisplayActive = active;
                    if (active)
                    {
                        StartOrResumeOwnerOperations();
                        TryPrimeCodexResumeOnce();
                    }
                    else
                    {
                        this.codexResumePrimePending = true;
                        StopOrSuspendOwnerOperations();
                    }
                }
            }
        }
    }

    private void ApplyPerformanceTimerIntervals()
    {
        ScheduleNextCodexRadarTick();
    }

    private void ScheduleNextCodexRadarTick()
    {
        int interval = GetNextCodexRadarTickIntervalMs();
        if (this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private int GetNextCodexRadarTickIntervalMs()
    {
        // Boundary alignment keeps the clock stable and groups wakeups with the other panels.
        DateTime now = DateTime.Now;
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.CurrentSettings.PerformanceMode);
        if (this.CurrentSettings.CodexRadarRandomTestEnabled &&
            this.CurrentSettings.CodexRadarRandomTestAutoRefresh)
        {
            targetInterval = Math.Min(targetInterval, 1000);
        }

        int elapsedInInterval = (int)(now.TimeOfDay.TotalMilliseconds % targetInterval);
        int interval = targetInterval - elapsedInInterval + CodexRadarSecondBoundaryOffsetMs;
        if (interval <= CodexRadarSecondBoundaryOffsetMs)
        {
            interval += targetInterval;
        }

        return Math.Max(50, Math.Min(targetInterval + 100, interval));
    }

    private void UpdateCodexRadarRandomTestIfNeeded()
    {
        if (!this.CurrentSettings.CodexRadarRandomTestEnabled)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool tokenChanged =
            this.codexRadarRandomTestRefreshToken !=
            this.CurrentSettings.CodexRadarRandomTestRefreshToken;
        bool automaticDue =
            this.CurrentSettings.CodexRadarRandomTestAutoRefresh &&
            (this.nextCodexRadarRandomTestRefreshUtc == DateTime.MinValue ||
             nowUtc >= this.nextCodexRadarRandomTestRefreshUtc);
        if (this.codexRadarRandomTestSnapshot == null || tokenChanged || automaticDue)
        {
            GenerateCodexRadarRandomTestSnapshot();
        }
    }

    private void GenerateCodexRadarRandomTestSnapshot()
    {
        int seed = unchecked(
            this.CurrentSettings.CodexRadarRandomTestRefreshToken * 397 ^
            DateTime.UtcNow.Ticks.GetHashCode());
        Random random = new Random(seed);
        CodexRadarRandomTestSnapshot test = new CodexRadarRandomTestSnapshot();

        CodexRadarSnapshot radar = CodexRadarSnapshot.CreateDefault();
        int passed = random.Next(0, CodexModelIqNominalTasks + 1);
        radar.CheckedAtLocal = DateTime.Now;
        radar.CheckedAtKnown = true;
        radar.ModelIqRefreshedAtLocal = DateTime.Now;
        radar.ModelIqRefreshedAtKnown = true;
        DateTime randomBeijingWindow = TimeZoneUtilities.GetCurrentBeijingHalfDayStart(DateTime.UtcNow).AddDays(-random.Next(0, 3));
        radar.ModelIqDataDateLocal = randomBeijingWindow.Date;
        radar.ModelIqDataWindowStartHourLocal = randomBeijingWindow.Hour >= 12 ? 12 : 0;
        radar.ModelIqDataDateKnown = true;
        radar.ModelIqDataWindowKnown = true;
        radar.ModelIqDataLabel = FormatCodexModelIqDataLabel(
            string.Empty,
            radar.ModelIqDataDateLocal,
            radar.ModelIqDataWindowStartHourLocal,
            radar.ModelIqDataWindowKnown);
        radar.ModelIqDataLabelKnown = radar.ModelIqDataLabel.Length > 0;
        radar.ModelIqRefreshSucceeded = true;
        radar.ModelIqKnown = true;
        radar.ModelIqPassedKnown = true;
        radar.ModelIqPassed = passed;
        radar.ModelIqValidTasks = CodexModelIqNominalTasks;
        radar.ModelIqPassRatePercent = CalculateCodexModelIqScore(passed, CodexModelIqNominalTasks);
        radar.ModelIqStatus = InferCodexModelIqStatusFromScore(radar.ModelIqPassRatePercent);
        ApplyCodexModelIqNormalRange(
            radar,
            CodexModelIqWebsiteNormalLowScore,
            CodexModelIqWebsiteNormalHighScore);
        radar.ModelIqTokenEfficiencyPercent = random.Next(0, 201);
        radar.ModelIqTimeEfficiencyPercent = random.Next(0, 201);
        radar.ModelIqEfficiencyPassed = Math.Max(1, passed);
        radar.ModelIqEfficiencyTotalTokens = random.Next(18000000, 60000001);
        radar.ModelIqEfficiencySerialSeconds = random.Next(1200, 4801);
        radar.ModelIqEfficiencyInputKnown = true;
        radar.ModelIqEfficiencyKnown = true;
        radar.SpeedWindowKnown = true;
        radar.SpeedWindowOpen = random.Next(0, 4) == 0;
        radar.SpeedWindowStatus = radar.SpeedWindowOpen ? "open" : "none";
        radar.SpeedWindowEventId = radar.SpeedWindowOpen
            ? "random-speed-window-" + seed.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        radar.SpeedWindowOpenedAtLocal = DateTime.Now.AddMinutes(-random.Next(5, 181));
        radar.SpeedWindowOpenedAtKnown = radar.SpeedWindowOpen;
        radar.SpeedWindowClosedAtLocal = DateTime.Now.AddMinutes(random.Next(5, 181));
        radar.SpeedWindowClosedAtKnown = radar.SpeedWindowOpen && random.Next(0, 2) == 0;
        radar.ResetEventKnown = random.Next(0, 6) == 0;
        radar.ResetEventId = radar.ResetEventKnown
            ? "random-reset-" + seed.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        radar.ResetEventTitle = radar.ResetEventKnown ? "测试重置" : string.Empty;
        radar.ResetEventUtc = radar.ResetEventKnown ? DateTime.UtcNow : DateTime.MinValue;
        test.Radar = radar;

        CodexQuotaSnapshot quota = CodexQuotaSnapshot.CreateDefault();
        quota.FiveHourPercent = random.Next(0, 101);
        quota.WeeklyPercent = random.Next(0, 101);
        quota.FiveHourResetLocal = DateTime.Now.AddMinutes(random.Next(5, 301));
        quota.WeeklyResetLocal = DateTime.Now.AddDays(random.Next(1, 8));
        quota.FiveHourResetKnown = true;
        quota.WeeklyResetKnown = true;
        quota.SourceUpdatedUtc = DateTime.UtcNow;
        quota.SourceUpdatedKnown = true;
        test.Quota = quota;
        test.CodexRunning = random.Next(0, 5) != 0;
        test.FiveHourGold = random.Next(0, 5) == 0;
        test.WeeklyGold = random.Next(0, 7) == 0;
        test.FiveHourDropPercent = test.FiveHourGold ? 0 : random.Next(0, Math.Min(18, quota.FiveHourPercent) + 1);
        test.WeeklyUsedSinceFiveHourResetPercent = test.WeeklyGold
            ? 0
            : random.Next(0, Math.Min(28, 100 - quota.WeeklyPercent) + 1);

        test.NetworkAvailable = random.Next(0, 8) != 0;
        if (!test.NetworkAvailable)
        {
            test.RadarHealth = ServiceHealthState.Offline;
            test.ClaudeHealth = ServiceHealthState.Offline;
            test.OpenAiHealth = ServiceHealthState.Offline;
        }
        else
        {
            test.RadarHealth = GetRandomServiceHealth(random);
            test.ClaudeHealth = GetRandomServiceHealth(random);
            test.OpenAiHealth = GetRandomServiceHealth(random);
        }

        this.codexRadarRandomTestSnapshot = test;
        this.codexRadarRandomTestRefreshToken =
            this.CurrentSettings.CodexRadarRandomTestRefreshToken;
        this.nextCodexRadarRandomTestRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
    }

    private static ServiceHealthState GetRandomServiceHealth(Random random)
    {
        ServiceHealthState[] states = new ServiceHealthState[]
        {
            ServiceHealthState.Normal,
            ServiceHealthState.Normal,
            ServiceHealthState.Degraded,
            ServiceHealthState.Incomplete,
            ServiceHealthState.Unavailable,
            ServiceHealthState.Unreachable
        };
        return states[random.Next(0, states.Length)];
    }

    // WidgetForm still calls this compatibility seam while applying global fullscreen state.
    // The retired headless owner has no visible surface, so the operation is intentionally inert.
    public void SetHiddenForFullscreen(bool hidden)
    {
    }

    protected override void DrawWindowContent(Graphics g)
    {
    }

    protected override bool CanRenderLayeredWindow()
    {
        return false;
    }

    private static bool TryComputeQuotaBurnRate(
        IList<WeeklyBurnSample> samples,
        bool useWallClock,
        double windowHours,
        double minimumMinutes,
        int remainingPercent,
        bool resetKnown,
        DateTime resetLocal,
        DateTime nowLocal,
        out double burnPercentPerHour,
        out double runwayHours,
        out double hoursToReset,
        out double observedHours,
        out QuotaForecastConfidence confidence)
    {
        burnPercentPerHour = 0.0;
        runwayHours = 0.0;
        hoursToReset = 0.0;
        observedHours = 0.0;
        confidence = QuotaForecastConfidence.None;
        if (samples == null || samples.Count < 2)
        {
            return false;
        }

        if (resetKnown && resetLocal != DateTime.MinValue)
        {
            hoursToReset = (resetLocal - nowLocal).TotalHours;
        }

        WeeklyBurnSample end = samples[samples.Count - 1];
        double endAxis = GetBurnSampleAxisHours(end, useWallClock);
        double cutoff = endAxis - Math.Max(0.1, windowHours);
        int startIndex = 0;
        while (startIndex < samples.Count - 1 &&
               GetBurnSampleAxisHours(samples[startIndex + 1], useWallClock) <= cutoff)
        {
            startIndex++;
        }

        WeeklyBurnSample start = samples[startIndex];
        observedHours = endAxis - GetBurnSampleAxisHours(start, useWallClock);
        if (observedHours * 60.0 < minimumMinutes)
        {
            return false;
        }

        int consumedPercent = start.RemainingPercent - end.RemainingPercent;
        if (consumedPercent <= 0)
        {
            // Integer-percent sources cannot prove a positive rate until at least one accepted drop.
            // Showing "采样中" is more honest than treating a quantized plateau as infinite life.
            return false;
        }

        double endpointRate = consumedPercent / observedHours;
        List<double> pairwiseRates = new List<double>();
        for (int i = startIndex; i < samples.Count - 1; i++)
        {
            double fromAxis = GetBurnSampleAxisHours(samples[i], useWallClock);
            for (int j = i + 1; j < samples.Count; j++)
            {
                double span = GetBurnSampleAxisHours(samples[j], useWallClock) - fromAxis;
                if (span < 1.0 / 60.0)
                {
                    continue;
                }

                int consumed = samples[i].RemainingPercent - samples[j].RemainingPercent;
                if (consumed >= 0)
                {
                    pairwiseRates.Add(consumed / span);
                }
            }
        }

        double medianRate = Median(pairwiseRates);
        // The endpoint rate retains the full-window budget meaning; a smaller Theil-Sen component
        // damps single integer-percent jumps without letting long flat plateaus erase a real trend.
        burnPercentPerHour = medianRate > 0.0
            ? endpointRate * 0.65 + medianRate * 0.35
            : endpointRate;
        if (double.IsNaN(burnPercentPerHour) ||
            double.IsInfinity(burnPercentPerHour) ||
            burnPercentPerHour <= 0.0)
        {
            return false;
        }

        runwayHours = ClampPercent(remainingPercent) / burnPercentPerHour;
        confidence = ResolveQuotaForecastConfidence(
            observedHours,
            consumedPercent,
            samples.Count - startIndex);
        return !double.IsNaN(runwayHours) && !double.IsInfinity(runwayHours) && runwayHours >= 0.0;
    }

    private static double Median(List<double> values)
    {
        if (values == null || values.Count == 0)
        {
            return 0.0;
        }

        values.Sort();
        int middle = values.Count / 2;
        return (values.Count & 1) == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    private static QuotaForecastConfidence ResolveQuotaForecastConfidence(
        double observedHours,
        int consumedPercent,
        int sampleCount)
    {
        if (observedHours >= 2.0 && consumedPercent >= 5 && sampleCount >= 5)
        {
            return QuotaForecastConfidence.High;
        }

        if (observedHours >= 0.5 && consumedPercent >= 2 && sampleCount >= 3)
        {
            return QuotaForecastConfidence.Medium;
        }

        return QuotaForecastConfidence.Low;
    }

    private static double GetBurnSampleAxisHours(WeeklyBurnSample sample, bool useWallClock)
    {
        if (sample == null)
        {
            return 0.0;
        }

        if (!useWallClock)
        {
            return sample.ActiveHours;
        }

        DateTime utc = sample.Utc.Kind == DateTimeKind.Utc ? sample.Utc : sample.Utc.ToUniversalTime();
        return utc.Ticks / (double)TimeSpan.TicksPerHour;
    }

    private static bool UpdateQuotaBurnObservationClock(QuotaRuntimeState quotaState, bool active, DateTime nowUtc)
    {
        if (quotaState == null)
        {
            return false;
        }

        bool historiesReset = false;
        DateTime normalizedUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        DateTime previousUtc = quotaState.WeeklyBurnClockUtc;
        bool wasActive = quotaState.WeeklyBurnClockActive;
        if (previousUtc != DateTime.MinValue && normalizedUtc > previousUtc)
        {
            double elapsedSeconds = (normalizedUtc - previousUtc).TotalSeconds;
            if (wasActive && elapsedSeconds <= WeeklyBurnClockMaximumGapSeconds)
            {
                quotaState.WeeklyBurnActiveHours += elapsedSeconds / 3600.0;
            }
            else if (wasActive && elapsedSeconds > WeeklyBurnClockMaximumGapSeconds)
            {
                // A suspend or stalled owner tick provides no evidence that the family was active
                // throughout the gap. Restart active estimates; wall-clock rhythm remains valid.
                historiesReset = quotaState.FiveHourBurnSamples.Count > 0 ||
                    quotaState.WeeklyBurnSamples.Count > 0;
                quotaState.FiveHourBurnSamples.Clear();
                quotaState.WeeklyBurnSamples.Clear();
            }

            if (!wasActive && active)
            {
                // A new active session gets a fresh baseline. This also prevents usage performed on
                // another device while the app was closed from being charged to local active time.
                historiesReset = historiesReset ||
                    quotaState.FiveHourBurnSamples.Count > 0 ||
                    quotaState.WeeklyBurnSamples.Count > 0;
                quotaState.FiveHourBurnSamples.Clear();
                quotaState.WeeklyBurnSamples.Clear();
            }
        }

        quotaState.WeeklyBurnClockUtc = normalizedUtc;
        quotaState.WeeklyBurnClockActive = active;
        return historiesReset;
    }

    private static void RecordQuotaBurnSamples(
        QuotaRuntimeState quotaState,
        CodexQuotaSnapshot snapshot,
        DateTime nowUtc)
    {
        if (quotaState == null || snapshot == null)
        {
            return;
        }

        DateTime normalizedUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        quotaState.WeeklyBurnTrackedResetLocal = RecordQuotaBurnWindow(
            quotaState.WeeklyBurnSamples,
            quotaState.WeeklyWallBurnSamples,
            quotaState.WeeklyBurnTrackedResetLocal,
            snapshot.WeeklyResetKnown,
            snapshot.WeeklyResetLocal,
            snapshot.WeeklyPercent,
            quotaState.WeeklyBurnClockActive,
            quotaState.WeeklyBurnActiveHours,
            normalizedUtc,
            WeeklyBurnRateWindowActiveHours,
            WeeklyBurnRateWindowWallHours);

        if (snapshot.FiveHourLimitAbsent)
        {
            quotaState.FiveHourBurnSamples.Clear();
            quotaState.FiveHourWallBurnSamples.Clear();
            quotaState.FiveHourBurnTrackedResetLocal = DateTime.MinValue;
            return;
        }

        quotaState.FiveHourBurnTrackedResetLocal = RecordQuotaBurnWindow(
            quotaState.FiveHourBurnSamples,
            quotaState.FiveHourWallBurnSamples,
            quotaState.FiveHourBurnTrackedResetLocal,
            snapshot.FiveHourResetKnown,
            snapshot.FiveHourResetLocal,
            snapshot.FiveHourPercent,
            quotaState.WeeklyBurnClockActive,
            quotaState.WeeklyBurnActiveHours,
            normalizedUtc,
            FiveHourBurnRateWindowActiveHours,
            FiveHourBurnRateWindowWallHours);
    }

    private static DateTime RecordQuotaBurnWindow(
        List<WeeklyBurnSample> activeSamples,
        List<WeeklyBurnSample> wallSamples,
        DateTime trackedResetLocal,
        bool resetKnown,
        DateTime resetLocal,
        int remainingPercent,
        bool active,
        double activeHours,
        DateTime nowUtc,
        double activeWindowHours,
        double wallWindowHours)
    {
        // A temporarily missing reset timestamp must not wipe accumulated history. Reset identity
        // changes and balance increases are the two independent boundaries that start a new pool.
        if (resetKnown && resetLocal != DateTime.MinValue)
        {
            bool resetChanged = trackedResetLocal != DateTime.MinValue &&
                Math.Abs((resetLocal - trackedResetLocal).TotalMinutes) > QuotaIdentityToleranceMinutes;
            if (resetChanged)
            {
                activeSamples.Clear();
                wallSamples.Clear();
            }
            trackedResetLocal = resetLocal;
        }

        int remaining = ClampPercent(remainingPercent);
        if (HasBalanceIncrease(activeSamples, remaining) || HasBalanceIncrease(wallSamples, remaining))
        {
            activeSamples.Clear();
            wallSamples.Clear();
        }

        AppendQuotaBurnSample(wallSamples, nowUtc, activeHours, remaining);
        PruneQuotaBurnSamples(wallSamples, true, wallWindowHours);

        // Inactive reads contribute to the recent wall-clock rhythm but never to continuous-use
        // runway. The next active transition starts a new active baseline in the clock helper.
        if (active)
        {
            AppendQuotaBurnSample(activeSamples, nowUtc, activeHours, remaining);
            PruneQuotaBurnSamples(activeSamples, false, activeWindowHours);
        }

        return trackedResetLocal;
    }

    private static bool HasBalanceIncrease(List<WeeklyBurnSample> samples, int remaining)
    {
        return samples != null &&
            samples.Count > 0 &&
            remaining > samples[samples.Count - 1].RemainingPercent;
    }

    private static void AppendQuotaBurnSample(
        List<WeeklyBurnSample> samples,
        DateTime normalizedUtc,
        double activeHours,
        int remaining)
    {
        WeeklyBurnSample next = new WeeklyBurnSample
        {
            Utc = normalizedUtc,
            ActiveHours = activeHours,
            RemainingPercent = remaining
        };

        if (samples.Count == 0)
        {
            samples.Add(next);
        }
        else
        {
            WeeklyBurnSample last = samples[samples.Count - 1];
            if (remaining == last.RemainingPercent)
            {
                if ((normalizedUtc - last.Utc).TotalSeconds < WeeklyBurnSampleMinimumSpacingSeconds)
                {
                    return;
                }

                // Retain the first point at this value and move only the plateau endpoint. That keeps
                // quantized 1%-step sources useful without accumulating one point every refresh tick.
                if (samples.Count >= 2 && samples[samples.Count - 2].RemainingPercent == remaining)
                {
                    samples[samples.Count - 1] = next;
                }
                else
                {
                    samples.Add(next);
                }
            }
            else
            {
                samples.Add(next);
            }
        }
    }

    private static void PruneQuotaBurnSamples(
        List<WeeklyBurnSample> samples,
        bool useWallClock,
        double windowHours)
    {
        if (samples == null || samples.Count == 0)
        {
            return;
        }

        double cutoff = GetBurnSampleAxisHours(samples[samples.Count - 1], useWallClock) - windowHours;
        while (samples.Count > 2 && GetBurnSampleAxisHours(samples[1], useWallClock) < cutoff)
        {
            samples.RemoveAt(0);
        }
        while (samples.Count > WeeklyBurnSampleLimit)
        {
            samples.RemoveAt(0);
        }
    }

    private static void RunWeeklyBurnRateSelfTest()
    {
        DateTime nowLocal = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Local);
        DateTime nowUtc = nowLocal.ToUniversalTime();
        List<WeeklyBurnSample> measured = new List<WeeklyBurnSample>
        {
            new WeeklyBurnSample { Utc = nowUtc.AddHours(-2.0), ActiveHours = 0.0, RemainingPercent = 50 },
            new WeeklyBurnSample { Utc = nowUtc, ActiveHours = 2.0, RemainingPercent = 40 }
        };
        double burn;
        double runway;
        double resetHours;
        double observedHours;
        QuotaForecastConfidence confidence;
        if (!TryComputeQuotaBurnRate(
                measured, false, WeeklyBurnRateWindowActiveHours, WeeklyBurnRateMinimumActiveMinutes,
                40, true, nowLocal.AddHours(10.0), nowLocal,
                out burn, out runway, out resetHours, out observedHours, out confidence) ||
            Math.Abs(burn - 5.0) > 0.001 || Math.Abs(runway - 8.0) > 0.001 || Math.Abs(resetHours - 10.0) > 0.001)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: measured active rate/runway.");
        }

        // Reset distance unknown: the runway must STILL resolve from local samples; only the
        // coverage comparison loses its reference (hoursToReset stays 0).
        if (!TryComputeQuotaBurnRate(
                measured, false, WeeklyBurnRateWindowActiveHours, WeeklyBurnRateMinimumActiveMinutes,
                40, false, DateTime.MinValue, nowLocal,
                out burn, out runway, out resetHours, out observedHours, out confidence) ||
            Math.Abs(runway - 8.0) > 0.001 || resetHours > 0.0)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: unknown reset must still yield a measured runway.");
        }

        if (!TryComputeQuotaBurnRate(
                measured, true, WeeklyBurnRateWindowWallHours, BurnRateMinimumWallMinutes,
                40, true, nowLocal.AddHours(10.0), nowLocal,
                out burn, out runway, out resetHours, out observedHours, out confidence) ||
            Math.Abs(burn - 5.0) > 0.001 || Math.Abs(runway - 8.0) > 0.001)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: recent-rhythm rate/runway.");
        }

        List<WeeklyBurnSample> insufficient = new List<WeeklyBurnSample>
        {
            new WeeklyBurnSample { Utc = nowUtc.AddMinutes(-6.0), ActiveHours = 0.0, RemainingPercent = 50 },
            new WeeklyBurnSample { Utc = nowUtc, ActiveHours = 0.1, RemainingPercent = 49 }
        };
        List<WeeklyBurnSample> flat = new List<WeeklyBurnSample>
        {
            new WeeklyBurnSample { Utc = nowUtc.AddHours(-1.0), ActiveHours = 0.0, RemainingPercent = 50 },
            new WeeklyBurnSample { Utc = nowUtc, ActiveHours = 1.0, RemainingPercent = 50 }
        };
        if (TryComputeQuotaBurnRate(
                insufficient, false, WeeklyBurnRateWindowActiveHours, WeeklyBurnRateMinimumActiveMinutes,
                49, true, nowLocal.AddHours(10.0), nowLocal,
                out burn, out runway, out resetHours, out observedHours, out confidence) ||
            TryComputeQuotaBurnRate(
                flat, false, WeeklyBurnRateWindowActiveHours, WeeklyBurnRateMinimumActiveMinutes,
                50, true, nowLocal.AddHours(10.0), nowLocal,
                out burn, out runway, out resetHours, out observedHours, out confidence))
        {
            throw new InvalidOperationException("Quota forecast self-test failed: insufficient/flat samples must stay in sampling state.");
        }

        QuotaRuntimeState clockState = new QuotaRuntimeState();
        UpdateQuotaBurnObservationClock(clockState, true, nowUtc);
        UpdateQuotaBurnObservationClock(clockState, true, nowUtc.AddSeconds(30.0));
        UpdateQuotaBurnObservationClock(clockState, false, nowUtc.AddSeconds(60.0));
        UpdateQuotaBurnObservationClock(clockState, false, nowUtc.AddSeconds(90.0));
        UpdateQuotaBurnObservationClock(clockState, true, nowUtc.AddSeconds(120.0));
        UpdateQuotaBurnObservationClock(clockState, true, nowUtc.AddSeconds(150.0));
        if (Math.Abs(clockState.WeeklyBurnActiveHours - (90.0 / 3600.0)) > 0.0001)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: inactive time leaked into active clock.");
        }
        clockState.FiveHourBurnSamples.Add(new WeeklyBurnSample());
        clockState.WeeklyBurnSamples.Add(new WeeklyBurnSample());
        clockState.WeeklyWallBurnSamples.Add(new WeeklyBurnSample());
        UpdateQuotaBurnObservationClock(clockState, true, nowUtc.AddMinutes(8.0));
        if (clockState.FiveHourBurnSamples.Count != 0 ||
            clockState.WeeklyBurnSamples.Count != 0 ||
            clockState.WeeklyWallBurnSamples.Count != 1)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: long gap did not isolate active and wall histories.");
        }

        QuotaRuntimeState recordState = new QuotaRuntimeState();
        recordState.WeeklyBurnClockActive = true;
        CodexQuotaSnapshot snapshot = CodexQuotaSnapshot.CreateDefault();
        snapshot.FiveHourLimitAbsent = false;
        snapshot.FiveHourResetKnown = true;
        snapshot.FiveHourResetLocal = nowLocal.AddHours(5.0);
        snapshot.FiveHourPercent = 70;
        snapshot.WeeklyResetKnown = true;
        snapshot.WeeklyResetLocal = nowLocal.AddDays(7.0);
        snapshot.WeeklyPercent = 80;
        RecordQuotaBurnSamples(recordState, snapshot, nowUtc);
        recordState.WeeklyBurnActiveHours = 0.5;
        snapshot.FiveHourPercent = 66;
        snapshot.WeeklyPercent = 78;
        RecordQuotaBurnSamples(recordState, snapshot, nowUtc.AddMinutes(30.0));

        if (recordState.FiveHourBurnSamples.Count != 2 ||
            recordState.WeeklyBurnSamples.Count != 2 ||
            recordState.FiveHourWallBurnSamples.Count != 2 ||
            recordState.WeeklyWallBurnSamples.Count != 2)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: dual-window histories were not recorded.");
        }

        // A read that momentarily lacks the reset timestamp must keep the history and keep sampling.
        snapshot.WeeklyResetKnown = false;
        snapshot.WeeklyResetLocal = DateTime.MinValue;
        snapshot.FiveHourPercent = 65;
        snapshot.WeeklyPercent = 77;
        recordState.WeeklyBurnActiveHours = 0.55;
        RecordQuotaBurnSamples(recordState, snapshot, nowUtc.AddMinutes(33.0));
        if (recordState.WeeklyBurnSamples.Count != 3)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: unknown reset timestamp wiped the sample history.");
        }

        snapshot.WeeklyResetKnown = true;
        snapshot.WeeklyResetLocal = nowLocal.AddDays(14.0);
        snapshot.WeeklyPercent = 100;
        recordState.WeeklyBurnActiveHours = 0.6;
        RecordQuotaBurnSamples(recordState, snapshot, nowUtc.AddMinutes(36.0));
        if (recordState.WeeklyBurnSamples.Count != 1 ||
            recordState.WeeklyBurnSamples[0].RemainingPercent != 100 ||
            recordState.FiveHourBurnSamples.Count != 4)
        {
            throw new InvalidOperationException("Quota forecast self-test failed: weekly reset crossed into the 5-hour history.");
        }
    }

    private static Color GetDeepSeekApiAlertColor(DeepSeekServiceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        if (snapshot.RequestRunning && !snapshot.Known)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
        }

        string errorCode = snapshot.ErrorCode ?? string.Empty;
        if (string.Equals(errorCode, "NET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
        }

        if (string.Equals(errorCode, "PARSE", StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        if (string.Equals(errorCode, "429", StringComparison.OrdinalIgnoreCase) ||
            IsDeepSeekServerErrorCode(errorCode))
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 245);
        }

        return DesignTokens.Colors.Warning;
    }

    private static bool IsDeepSeekServerErrorCode(string errorCode)
    {
        int statusCode;
        return int.TryParse(errorCode ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out statusCode) &&
            statusCode >= 500;
    }

    private static Color GetServiceHealthAlertColor(ServiceHealthState state)
    {
        if (state == ServiceHealthState.Degraded || state == ServiceHealthState.Incomplete)
        {
            return DesignTokens.Colors.Warning;
        }

        if (state == ServiceHealthState.Unavailable)
        {
            return DesignTokens.Colors.WarningDeep;
        }

        if (state == ServiceHealthState.Unreachable)
        {
            return DesignTokens.Colors.DangerStrong;
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
    }


    private static bool IsCodexRadarSpeedWindowCurrentlyOpen(CodexRadarSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null || !snapshot.SpeedWindowKnown || !snapshot.SpeedWindowOpen)
        {
            return false;
        }

        if (snapshot.SpeedWindowClosedAtKnown &&
            snapshot.SpeedWindowClosedAtLocal != DateTime.MinValue &&
            snapshot.SpeedWindowClosedAtLocal <= nowLocal)
        {
            return false;
        }

        return true;
    }

    private static void ExpireCodexRadarSpeedWindowIfClosed(CodexRadarSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null ||
            !snapshot.SpeedWindowOpen ||
            !snapshot.SpeedWindowClosedAtKnown ||
            snapshot.SpeedWindowClosedAtLocal == DateTime.MinValue ||
            snapshot.SpeedWindowClosedAtLocal > nowLocal)
        {
            return;
        }

        // Codex Radar may deliver the open flag and close target through different
        // channels; a past close time is authoritative at display/notification boundaries.
        snapshot.SpeedWindowKnown = true;
        snapshot.SpeedWindowOpen = false;
        snapshot.SpeedWindowStatus = "closed";
    }

    private static int NormalizeCodexModelIqValidTaskCount(double validTasks)
    {
        if (double.IsNaN(validTasks) || double.IsInfinity(validTasks) || validTasks <= 0.0)
        {
            return CodexModelIqNominalTasks;
        }

        int rounded = (int)Math.Round(validTasks, MidpointRounding.AwayFromZero);
        return Math.Max(WidgetSettings.MinCodexModelIqValidTasks, Math.Min(MaxCodexModelIqSourceTasks, rounded));
    }

    private static int NormalizeCodexModelIqPassedCount(double passed, double validTasks)
    {
        int normalizedValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
        if (double.IsNaN(passed) || double.IsInfinity(passed) || passed <= 0.0)
        {
            return 0;
        }

        int rounded = (int)Math.Round(passed, MidpointRounding.AwayFromZero);
        return Math.Max(0, Math.Min(normalizedValidTasks, rounded));
    }

    private static double NormalizeCodexModelIqPassedValue(double passed, double validTasks)
    {
        int normalizedValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
        if (double.IsNaN(passed) || double.IsInfinity(passed) || passed <= 0.0)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(normalizedValidTasks, passed));
    }

    private static int CalculateCodexModelIqScore(double passed, double validTasks)
    {
        int normalizedValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
        double normalizedPassed = NormalizeCodexModelIqPassedValue(passed, validTasks);
        return NormalizePassRatePercent(normalizedPassed / normalizedValidTasks);
    }

    private static int EstimateCodexModelIqPassedFromScore(double score, double validTasks)
    {
        int normalizedValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
        double normalizedScore = NormalizePassRateValue(score);
        int passed = (int)Math.Round(
            normalizedScore / CodexModelIqWebsiteScoreScale * normalizedValidTasks,
            MidpointRounding.AwayFromZero);
        return Math.Max(0, Math.Min(normalizedValidTasks, passed));
    }

    private static string InferCodexModelIqStatusFromScore(double score)
    {
        double normalizedScore = NormalizePassRateValue(score);
        if (normalizedScore < CodexModelIqWebsiteNormalLowScore)
        {
            return "red";
        }

        if (normalizedScore <= CodexModelIqWebsiteNormalLowScore)
        {
            return "yellow";
        }

        if (normalizedScore <= CodexModelIqWebsiteNormalHighScore)
        {
            return "green";
        }

        return "yellow";
    }

    private static double GetCodexModelIqDisplayMaxScore(CodexRadarSnapshot snapshot, double baselineScore)
    {
        double maxScore = Math.Max(0.0, baselineScore);
        if (snapshot != null)
        {
            if (snapshot.ModelIqDisplayMaxScoreKnown && snapshot.ModelIqDisplayMaxScore > maxScore)
            {
                maxScore = snapshot.ModelIqDisplayMaxScore;
            }

            if (snapshot.ModelIqKnown && snapshot.ModelIqPassRatePercent > maxScore)
            {
                maxScore = snapshot.ModelIqPassRatePercent;
            }

            if (snapshot.ModelIqNormalRangeKnown && snapshot.ModelIqNormalHighScore > maxScore)
            {
                maxScore = snapshot.ModelIqNormalHighScore;
            }

            List<CodexModelHistoryPoint> points = GetRecentCodexModelHistory(snapshot);
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] != null && points[i].Score > maxScore)
                {
                    maxScore = points[i].Score;
                }
            }
        }

        if (maxScore <= 0.0)
        {
            maxScore = CodexModelIqWebsiteNormalHighScore;
        }

        return NormalizeCodexModelIqDisplayMaxScore(maxScore);
    }

    private static double NormalizeCodexModelIqDisplayMaxScore(double score)
    {
        if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0.0)
        {
            return CodexModelIqWebsiteNormalHighScore;
        }

        return Math.Max(1.0, Math.Min(MaxCodexModelIqScore, score));
    }

    private bool RefreshQuotaInfoIfNeeded()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime nowLocal = DateTime.Now;
        QuotaRuntimeState quotaState = GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex);
        bool codexProcessChanged;
        bool codexRunning = UpdateCodexProcessRunningStatus(nowUtc, out codexProcessChanged);
        UpdateQuotaBurnObservationClock(quotaState, codexRunning, nowUtc);
        bool resetDue = IsQuotaResetDue(quotaState.Snapshot, nowLocal);
        // Active Codex sessions need prompt quota updates; inactive sessions use a much slower
        // schedule unless a reset boundary or process transition requires an immediate read.
        bool refreshDue =
            resetDue ||
            quotaState.LastRefreshUtc == DateTime.MinValue ||
            (codexProcessChanged && codexRunning);

        if (!refreshDue)
        {
            if (codexRunning)
            {
                refreshDue = (nowUtc - quotaState.LastRefreshUtc).TotalSeconds >= GetQuotaActiveRefreshSeconds();
            }
            else
            {
                refreshDue = IsInactiveQuotaRefreshDue(quotaState, nowUtc);
            }
        }

        if (!refreshDue)
        {
            return codexProcessChanged;
        }

        if (!codexRunning)
        {
            MarkInactiveQuotaRefresh(quotaState, nowUtc);
        }

        if (resetDue)
        {
            ActivateDueQuotaResetProtections(quotaState.Snapshot, nowLocal, nowUtc);
        }

        quotaState.LastRefreshUtc = nowUtc;
        bool quotaKnown = false;
        string quotaSourceKind = "unknown";
        CodexQuotaSnapshot nextSnapshot;
        long quotaReadStart = TimingStats.StartTimestamp();
        try
        {
            nextSnapshot = ReadQuotaSnapshot(out quotaKnown, out quotaSourceKind);
        }
        finally
        {
            TimingStats.RecordElapsed("codex.quota_read", quotaReadStart);
        }

        ApplyQuotaSnapshot(CodexRadarSoftwareMode.Codex, nextSnapshot, quotaKnown, codexRunning, nowLocal, nowUtc, quotaSourceKind);
        return true;
    }

    private QuotaRingDecisionInfo ApplyQuotaSnapshot(
        CodexRadarSoftwareMode family,
        CodexQuotaSnapshot nextSnapshot,
        bool quotaKnown,
        bool appRunning,
        DateTime nowLocal,
        DateTime detectedUtc,
        string sourceKind)
    {
        return ApplyQuotaSnapshot(family, nextSnapshot, quotaKnown, appRunning, nowLocal, detectedUtc, sourceKind, true);
    }

    private QuotaRingDecisionInfo ApplyQuotaSnapshot(
        CodexRadarSoftwareMode family,
        CodexQuotaSnapshot nextSnapshot,
        bool quotaKnown,
        bool appRunning,
        DateTime nowLocal,
        DateTime detectedUtc,
        string sourceKind,
        bool logDecision)
    {
        family = NormalizeEffectiveSoftwareMode(family);
        QuotaRuntimeState quotaState = GetQuotaRuntimeState(family);
        MarkQuotaSnapshotSource(nextSnapshot, sourceKind);
        if (family == CodexRadarSoftwareMode.Codex && IsQuotaResetDue(nextSnapshot, nowLocal))
        {
            ActivateDueQuotaResetProtections(nextSnapshot, nowLocal, detectedUtc);
        }

        QuotaRingDecisionInfo quotaDecision = UpdateQuotaReadDeltaTrackingWithSettings(quotaState, nextSnapshot, quotaKnown);
        CodexQuotaSnapshot displaySnapshot = family == CodexRadarSoftwareMode.Codex
            ? ApplyQuotaResetProtections(family, nextSnapshot)
            : NormalizeQuotaSnapshot(nextSnapshot);
        quotaState.Snapshot = displaySnapshot;
        quotaState.SourceKnown = quotaKnown;
        UpdateQuotaBurnObservationClock(quotaState, appRunning, detectedUtc);
        if (quotaKnown && displaySnapshot != null)
        {
            RecordQuotaBurnSamples(quotaState, displaySnapshot, detectedUtc);
            if (family == CodexRadarSoftwareMode.Codex && logDecision)
            {
                CodexResetCreditsSnapshot credits = GetCodexResetCreditsDisplaySnapshot();
                int activeCredits = credits != null && credits.Known
                    ? credits.GetActiveCount(detectedUtc.Kind == DateTimeKind.Utc ? detectedUtc : detectedUtc.ToUniversalTime())
                    : 0;
                this.codexQuotaHistoryStore.Record(
                    displaySnapshot.FiveHourPercent,
                    displaySnapshot.WeeklyPercent,
                    displaySnapshot.WeeklyResetKnown,
                    displaySnapshot.WeeklyResetLocal,
                    credits != null && credits.Known,
                    activeCredits,
                    detectedUtc);
            }
        }
        GetRadarFamilyState(family).Touch();
        PublishProjectionStateFromOwner();
        if (logDecision)
        {
            LogQuotaRingDecision(family, quotaDecision, displaySnapshot, quotaKnown, appRunning);
        }

        return quotaDecision;
    }

    private void InitializeQuotaReadDeltaTracking(CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        InitializeQuotaReadDeltaTracking(GetActiveQuotaRuntimeState(), snapshot, sourceKnown);
    }

    private static void InitializeQuotaReadDeltaTracking(QuotaRuntimeState quotaState, CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        if (!sourceKnown || snapshot == null)
        {
            ResetQuotaReadDeltaTracking(quotaState);
            return;
        }

        quotaState.LastFiveHourReadPercent = ClampPercent(snapshot.FiveHourPercent);
        quotaState.LastWeeklyReadPercent = ClampPercent(snapshot.WeeklyPercent);
        quotaState.LastReadSourceUtc = snapshot.SourceUpdatedKnown
            ? snapshot.SourceUpdatedUtc
            : DateTime.MinValue;
        quotaState.FiveHourConsumptionRingBaselinePercent = -1;
        quotaState.TrackedFiveHourResetLocal = snapshot.FiveHourResetKnown
            ? snapshot.FiveHourResetLocal
            : DateTime.MinValue;
        quotaState.TrackedWeeklyResetLocal = snapshot.WeeklyResetKnown
            ? snapshot.WeeklyResetLocal
            : DateTime.MinValue;
        ResetRejectedIdentityPersistence(quotaState.FiveHourRejectedIdentity);
        ResetRejectedIdentityPersistence(quotaState.WeeklyRejectedIdentity);
        quotaState.WeeklyQuotaAtFiveHourWindowStartPercent = ClampPercent(snapshot.WeeklyPercent);

        // A consuming baseline arms the idle-pool newborn suppression from the first sample, so a
        // restart into the alternating-pool regime cannot re-adopt the phantom before the real
        // pool's evidence accumulates.
        DateTime baselineUtc = snapshot.SourceUpdatedKnown ? snapshot.SourceUpdatedUtc : DateTime.UtcNow;
        if (quotaState.LastFiveHourReadPercent >= 0 && quotaState.LastFiveHourReadPercent <= 98)
        {
            quotaState.FiveHourLastConsumingAcceptUtc = baselineUtc;
        }

        if (quotaState.LastWeeklyReadPercent >= 0 && quotaState.LastWeeklyReadPercent <= 98)
        {
            quotaState.WeeklyLastConsumingAcceptUtc = baselineUtc;
        }
    }

    private QuotaProtectionOptions GetQuotaProtectionOptions()
    {
        return QuotaProtectionOptions.FromSettings(this.CurrentSettings);
    }

    private QuotaRingDecisionInfo UpdateQuotaReadDeltaTrackingWithSettings(
        QuotaRuntimeState quotaState,
        CodexQuotaSnapshot snapshot,
        bool sourceKnown)
    {
        bool codexIdentityHardening = object.ReferenceEquals(quotaState, this.codexRuntimeState.Quota);
        DateTime corroboratingResetEventUtc = codexIdentityHardening
            ? this.codexRuntimeState.Quota.Protection.LastRadarResetEventUtc
            : DateTime.MinValue;
        return UpdateQuotaReadDeltaTracking(
            quotaState,
            snapshot,
            sourceKnown,
            GetQuotaProtectionOptions(),
            codexIdentityHardening,
            corroboratingResetEventUtc);
    }

    private static QuotaRingDecisionInfo UpdateQuotaReadDeltaTracking(QuotaRuntimeState quotaState, CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        return UpdateQuotaReadDeltaTracking(
            quotaState,
            snapshot,
            sourceKnown,
            QuotaProtectionOptions.LegacyRuntimeDefaults(),
            false,
            DateTime.MinValue);
    }

    private static QuotaRingDecisionInfo UpdateQuotaReadDeltaTracking(
        QuotaRuntimeState quotaState,
        CodexQuotaSnapshot snapshot,
        bool sourceKnown,
        QuotaProtectionOptions protectionOptions,
        bool codexIdentityHardening,
        DateTime corroboratingResetEventUtc)
    {
        if (protectionOptions == null)
        {
            protectionOptions = QuotaProtectionOptions.LegacyRuntimeDefaults();
        }

        QuotaRingDecisionInfo decision = CreateQuotaRingDecisionInfo(quotaState, snapshot, sourceKnown);
        if (!sourceKnown || snapshot == null)
        {
            ResetQuotaReadDeltaTracking(quotaState);
            return CompleteQuotaRingDecisionInfo(
                quotaState,
                decision,
                snapshot == null ? "snapshot_null_reset_tracking" : "source_unknown_reset_tracking");
        }

        int fiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
        int weeklyPercent = ClampPercent(snapshot.WeeklyPercent);
        DateTime sourceUtc = snapshot.SourceUpdatedKnown
            ? snapshot.SourceUpdatedUtc
            : DateTime.MinValue;
        DateTime fiveHourResetLocal = snapshot.FiveHourResetKnown
            ? snapshot.FiveHourResetLocal
            : DateTime.MinValue;
        DateTime weeklyResetLocal = snapshot.WeeklyResetKnown
            ? snapshot.WeeklyResetLocal
            : DateTime.MinValue;
        bool weeklyConsumptionBaselineRepaired = false;
        if (quotaState.LastFiveHourReadPercent < 0 || quotaState.LastWeeklyReadPercent < 0)
        {
            InitializeQuotaReadDeltaTracking(quotaState, snapshot, true);
            return CompleteQuotaRingDecisionInfo(quotaState, decision, "initial_sample_set_tracking_baseline");
        }

        if (sourceUtc != DateTime.MinValue &&
            quotaState.LastReadSourceUtc != DateTime.MinValue &&
            sourceUtc < quotaState.LastReadSourceUtc)
        {
            return CompleteQuotaRingDecisionInfo(quotaState, decision, "stale_source_ignored");
        }

        if (codexIdentityHardening)
        {
            DateTime sampleUtc = sourceUtc == DateTime.MinValue ? DateTime.UtcNow : sourceUtc;
            QuotaWindowIdentityDecision fiveHourIdentity = EvaluateQuotaWindowIdentity(
                quotaState.TrackedFiveHourResetLocal,
                fiveHourResetLocal,
                fiveHourPercent,
                snapshot.SourceKind,
                sampleUtc,
                quotaState.LastReadSourceUtc,
                corroboratingResetEventUtc,
                TimeSpan.FromHours(5.0),
                quotaState.FiveHourRejectedIdentity,
                quotaState.FiveHourLastConsumingAcceptUtc,
                quotaState.FiveHourLastNewbornAcceptUtc);
            QuotaWindowIdentityDecision weeklyIdentity = EvaluateQuotaWindowIdentity(
                quotaState.TrackedWeeklyResetLocal,
                weeklyResetLocal,
                weeklyPercent,
                snapshot.SourceKind,
                sampleUtc,
                quotaState.LastReadSourceUtc,
                corroboratingResetEventUtc,
                TimeSpan.FromDays(7.0),
                quotaState.WeeklyRejectedIdentity,
                quotaState.WeeklyLastConsumingAcceptUtc,
                quotaState.WeeklyLastNewbornAcceptUtc);
            decision.FiveHourAnchorAgeMinutes = fiveHourIdentity.AnchorAgeMinutes;
            decision.WeeklyAnchorAgeMinutes = weeklyIdentity.AnchorAgeMinutes;
            decision.FiveHourRejectedPersistenceCount = fiveHourIdentity.RejectedPersistenceCount;
            decision.FiveHourRejectedPersistenceFirstSeenUtc = fiveHourIdentity.RejectedPersistenceFirstSeenUtc;
            decision.WeeklyRejectedPersistenceCount = weeklyIdentity.RejectedPersistenceCount;
            decision.WeeklyRejectedPersistenceFirstSeenUtc = weeklyIdentity.RejectedPersistenceFirstSeenUtc;
            decision.IdentityDecisionReason = CombineQuotaIdentityReasons(fiveHourIdentity, weeklyIdentity);

            // Evidence feeding the newborn suppressions: remember when the displayed pool was last
            // seen actively consuming, and when a newborn re-anchor was last granted.
            if (fiveHourIdentity.Accepted)
            {
                if (ClampPercent(fiveHourPercent) <= 98)
                {
                    quotaState.FiveHourLastConsumingAcceptUtc = sampleUtc;
                }

                if (string.Equals(fiveHourIdentity.Reason, "reset_confirmed_by_newborn", StringComparison.Ordinal))
                {
                    quotaState.FiveHourLastNewbornAcceptUtc = sampleUtc;
                }
            }

            if (weeklyIdentity.Accepted)
            {
                if (ClampPercent(weeklyPercent) <= 98)
                {
                    quotaState.WeeklyLastConsumingAcceptUtc = sampleUtc;
                }

                if (string.Equals(weeklyIdentity.Reason, "reset_confirmed_by_newborn", StringComparison.Ordinal))
                {
                    quotaState.WeeklyLastNewbornAcceptUtc = sampleUtc;
                }
            }

            // A rejected provider identity must not reach the display snapshot. Replacing only
            // that ring preserves the independent accepted ring while eliminating visible
            // phantom-pool jumps before the ordinary delta tracker runs.
            if (!fiveHourIdentity.Accepted)
            {
                fiveHourPercent = quotaState.LastFiveHourReadPercent;
                snapshot.FiveHourPercent = fiveHourPercent;
                fiveHourResetLocal = quotaState.TrackedFiveHourResetLocal;
                snapshot.FiveHourResetLocal = fiveHourResetLocal;
                snapshot.FiveHourResetKnown = fiveHourResetLocal != DateTime.MinValue;
            }

            if (!weeklyIdentity.Accepted)
            {
                weeklyPercent = quotaState.LastWeeklyReadPercent;
                snapshot.WeeklyPercent = weeklyPercent;
                weeklyResetLocal = quotaState.TrackedWeeklyResetLocal;
                snapshot.WeeklyResetLocal = weeklyResetLocal;
                snapshot.WeeklyResetKnown = weeklyResetLocal != DateTime.MinValue;
            }

            if (!fiveHourIdentity.Accepted && !weeklyIdentity.Accepted)
            {
                decision.IdentitySampleRejected = true;
                return CompleteQuotaRingDecisionInfo(
                    quotaState,
                    decision,
                    "interference_pool_sample_ignored");
            }
        }

        if (protectionOptions.WeeklyBaselineAutoRepairEnabled &&
            IsSuspiciousWeeklyConsumptionBaseline(
            quotaState.WeeklyQuotaAtFiveHourWindowStartPercent,
            quotaState.LastWeeklyReadPercent,
            weeklyPercent,
            quotaState.TrackedFiveHourResetLocal,
            fiveHourResetLocal))
        {
            // Provider cache jitter can briefly report weekly=100 and poison the "weekly at
            // five-hour-window start" baseline. Once stable low weekly data returns, repair the
            // baseline before duplicate-sample handling so the ring does not keep showing a
            // fictitious 100-to-current consumption tail.
            quotaState.WeeklyQuotaAtFiveHourWindowStartPercent = weeklyPercent;
            weeklyConsumptionBaselineRepaired = true;
        }

        bool duplicateSameBalance =
            sourceUtc != DateTime.MinValue &&
            quotaState.LastReadSourceUtc != DateTime.MinValue &&
            sourceUtc == quotaState.LastReadSourceUtc &&
            fiveHourPercent == quotaState.LastFiveHourReadPercent &&
            weeklyPercent == quotaState.LastWeeklyReadPercent &&
            (fiveHourResetLocal == DateTime.MinValue || fiveHourResetLocal == quotaState.TrackedFiveHourResetLocal);
        if (duplicateSameBalance && protectionOptions.DuplicateSameBalanceRingProtectionEnabled)
        {
            return CompleteQuotaRingDecisionInfo(
                quotaState,
                decision,
                weeklyConsumptionBaselineRepaired
                    ? "suspicious_weekly_consumption_baseline_repaired;duplicate_source_same_balance_keep_existing_rings"
                    : "duplicate_source_same_balance_keep_existing_rings");
        }

        bool fiveHourChanged = fiveHourPercent != quotaState.LastFiveHourReadPercent;
        bool weeklyChanged = weeklyPercent != quotaState.LastWeeklyReadPercent;
        int nextFiveHourConsumptionRingBaseline = GetNextFiveHourConsumptionRingBaseline(
            quotaState.FiveHourConsumptionRingBaselinePercent,
            quotaState.LastFiveHourReadPercent,
            fiveHourPercent);
        DateTime nowLocal = DateTime.Now;
        bool previousFiveHourResetDue =
            quotaState.TrackedFiveHourResetLocal == DateTime.MinValue ||
            quotaState.TrackedFiveHourResetLocal <= nowLocal.AddMinutes(30.0);
        // Strict boundary mode keeps the old natural-reset rule. The default lets a clear
        // balance increase advance the five-hour window so manual reset cards are not hidden.
        bool fiveHourResetMoved =
            fiveHourResetLocal != DateTime.MinValue &&
            quotaState.TrackedFiveHourResetLocal != DateTime.MinValue &&
            fiveHourResetLocal > quotaState.TrackedFiveHourResetLocal.AddMinutes(1.0) &&
            (!protectionOptions.StrictFiveHourResetBoundaryEnabled || previousFiveHourResetDue);
        bool fiveHourResetBecameKnown =
            fiveHourResetLocal != DateTime.MinValue &&
            quotaState.TrackedFiveHourResetLocal == DateTime.MinValue;
        bool fiveHourWindowAdvanced =
            fiveHourResetMoved ||
            (fiveHourChanged &&
                fiveHourPercent > quotaState.LastFiveHourReadPercent &&
                fiveHourResetLocal == DateTime.MinValue);
        bool weeklyWindowAdvanced = weeklyChanged && weeklyPercent > quotaState.LastWeeklyReadPercent;
        if (!fiveHourChanged && !weeklyChanged)
        {
            // A newer log can repeat the same rounded balance. Keep the previous visible
            // consumption baseline until a real decrease or reset/increase changes it.
            quotaState.FiveHourConsumptionRingBaselinePercent = protectionOptions.DuplicateSameBalanceRingProtectionEnabled
                ? nextFiveHourConsumptionRingBaseline
                : -1;
            if (fiveHourResetMoved || fiveHourResetBecameKnown)
            {
                quotaState.TrackedFiveHourResetLocal = fiveHourResetLocal;
                quotaState.WeeklyQuotaAtFiveHourWindowStartPercent = weeklyPercent;
            }

            if (weeklyResetLocal != DateTime.MinValue)
            {
                quotaState.TrackedWeeklyResetLocal = weeklyResetLocal;
            }

            if (sourceUtc != DateTime.MinValue)
            {
                quotaState.LastReadSourceUtc = sourceUtc;
            }

            string sameBalanceReason = fiveHourResetMoved
                ? "same_balance_five_hour_window_advanced_reset_weekly_baseline"
                : (fiveHourResetBecameKnown
                    ? "same_balance_five_hour_reset_became_known"
                    : (protectionOptions.DuplicateSameBalanceRingProtectionEnabled
                        ? "newer_source_same_balance_keep_existing_rings"
                        : "same_balance_protection_disabled_clear_consumption_ring"));
            if (weeklyConsumptionBaselineRepaired)
            {
                sameBalanceReason = "suspicious_weekly_consumption_baseline_repaired;" + sameBalanceReason;
            }

            return CompleteQuotaRingDecisionInfo(quotaState, decision, sameBalanceReason);
        }

        if (fiveHourWindowAdvanced || weeklyWindowAdvanced || quotaState.WeeklyQuotaAtFiveHourWindowStartPercent < 0)
        {
            quotaState.WeeklyQuotaAtFiveHourWindowStartPercent = weeklyPercent;
        }

        if (fiveHourResetLocal != DateTime.MinValue)
        {
            quotaState.TrackedFiveHourResetLocal = fiveHourResetLocal;
        }

        if (weeklyResetLocal != DateTime.MinValue)
        {
            quotaState.TrackedWeeklyResetLocal = weeklyResetLocal;
        }

        if (fiveHourChanged)
        {
            quotaState.FiveHourConsumptionRingBaselinePercent = nextFiveHourConsumptionRingBaseline;
            quotaState.LastFiveHourReadPercent = fiveHourPercent;
        }

        if (weeklyChanged)
        {
            quotaState.LastWeeklyReadPercent = weeklyPercent;
        }

        if (sourceUtc != DateTime.MinValue)
        {
            quotaState.LastReadSourceUtc = sourceUtc;
        }

        return CompleteQuotaRingDecisionInfo(
            quotaState,
            decision,
            GetQuotaRingDecisionReason(
                fiveHourChanged,
                weeklyChanged,
                fiveHourPercent,
                decision.PreviousFiveHourPercent,
                weeklyPercent,
                decision.PreviousWeeklyPercent,
                fiveHourWindowAdvanced,
                weeklyWindowAdvanced,
                weeklyConsumptionBaselineRepaired));
    }

    private static QuotaWindowIdentityDecision EvaluateQuotaWindowIdentity(
        DateTime trackedResetLocal,
        DateTime incomingResetLocal,
        int incomingBalancePercent,
        string sourceKind,
        DateTime sampleUtc,
        DateTime lastAcceptedUtc,
        DateTime corroboratingResetEventUtc,
        TimeSpan windowLength,
        RejectedIdentityPersistenceState rejectedPersistence = null,
        DateTime lastConsumingAcceptUtc = default(DateTime),
        DateTime lastNewbornAcceptUtc = default(DateTime))
    {
        QuotaWindowIdentityDecision decision = new QuotaWindowIdentityDecision
        {
            IdentitySame = true,
            Accepted = true,
            Reason = "identity_same",
            AnchorAgeMinutes = null,
            RejectedPersistenceCount = 0,
            RejectedPersistenceFirstSeenUtc = DateTime.MinValue
        };
        if (trackedResetLocal == DateTime.MinValue || incomingResetLocal == DateTime.MinValue)
        {
            ResetRejectedIdentityPersistence(rejectedPersistence);
            return decision;
        }

        double identityDeltaMinutes = Math.Abs((incomingResetLocal - trackedResetLocal).TotalMinutes);
        if (identityDeltaMinutes <= QuotaIdentityToleranceMinutes)
        {
            // Same-identity samples keep the display as-is but must NOT clear the rejected-identity
            // tracker: with two alternating provider pools, every accepted phantom sample would wipe
            // the real pool's rejection streak and the count>=3 repair could never fire.
            return decision;
        }

        decision.IdentitySame = false;
        DateTime sampleLocal = sampleUtc.ToLocalTime();
        double remainingMinutes = (incomingResetLocal - sampleLocal).TotalMinutes;
        decision.AnchorAgeMinutes = windowLength.TotalMinutes - remainingMinutes;
        if (trackedResetLocal <= sampleLocal)
        {
            decision.Accepted = true;
            decision.Reason = "reset_confirmed_by_expiry";
            ResetRejectedIdentityPersistence(rejectedPersistence);
            return decision;
        }

        // Provider reset anchors can be a few seconds beyond the nominal window length.
        // Reuse the two-minute identity tolerance as clock-skew allowance so the observed
        // +1s/+33s newborn samples remain immediate resets, while mid-window identities fail.
        bool newbornShaped =
            decision.AnchorAgeMinutes.Value >= -QuotaIdentityToleranceMinutes &&
            decision.AnchorAgeMinutes.Value <= QuotaNewbornToleranceMinutes &&
            ClampPercent(incomingBalancePercent) >= 99;
        if (newbornShaped)
        {
            // A genuinely born window can only appear when the tracked window is about to expire
            // (clock skew around the boundary). An idle pool reproduces the newborn shape on every
            // sample with a sliding anchor while the tracked window still has hours to run - that
            // shape must never re-anchor the display, no matter which source carried it.
            double trackedRemainingMinutes = (trackedResetLocal - sampleLocal).TotalMinutes;
            bool nearExpiry = trackedRemainingMinutes <= QuotaIdentityToleranceMinutes;
            DateTime normalizedSample = NormalizeStateUtc(sampleUtc == DateTime.MinValue ? DateTime.UtcNow : sampleUtc);
            bool newbornSuppressed =
                !nearExpiry ||
                (lastConsumingAcceptUtc != DateTime.MinValue &&
                 (normalizedSample - NormalizeStateUtc(lastConsumingAcceptUtc)).TotalMinutes <= QuotaNewbornSuppressAfterConsumptionMinutes) ||
                (lastNewbornAcceptUtc != DateTime.MinValue &&
                 normalizedSample - NormalizeStateUtc(lastNewbornAcceptUtc) < windowLength);
            if (!newbornSuppressed)
            {
                decision.Accepted = true;
                decision.Reason = "reset_confirmed_by_newborn";
                ResetRejectedIdentityPersistence(rejectedPersistence);
                return decision;
            }
        }

        if (corroboratingResetEventUtc != DateTime.MinValue)
        {
            double eventAgeHours = (sampleUtc - NormalizeStateUtc(corroboratingResetEventUtc)).TotalHours;
            if (eventAgeHours >= 0.0 && eventAgeHours <= QuotaResetEventCorroborationHours)
            {
                decision.Accepted = true;
                decision.Reason = "reset_confirmed_by_event";
                ResetRejectedIdentityPersistence(rejectedPersistence);
                return decision;
            }
        }

        // The session file and the long-gap rebaseline alternate between the same provider pools as
        // the public source, so neither may accept a newborn-shaped identity change: at 19:59 the
        // session path carried the idle pool (100, anchor=now+5h) while the real pool still had 4
        // minutes to run, and pinned the ring at 100. Real early resets are covered by the reset
        // event corroboration branch above.
        if (!newbornShaped && string.Equals(sourceKind, "session", StringComparison.OrdinalIgnoreCase))
        {
            decision.Accepted = true;
            decision.Reason = "reset_confirmed_by_session";
            ResetRejectedIdentityPersistence(rejectedPersistence);
            return decision;
        }

        if (!newbornShaped &&
            lastAcceptedUtc != DateTime.MinValue &&
            (sampleUtc - lastAcceptedUtc).TotalMinutes > QuotaGapRebaselineMinutes)
        {
            decision.Accepted = true;
            decision.Reason = "gap_rebaseline";
            ResetRejectedIdentityPersistence(rejectedPersistence);
            return decision;
        }

        decision.Accepted = false;
        decision.Reason = newbornShaped ? "idle_pool_newborn_suppressed" : "interference_pool_sample_ignored";

        // Newborn-shaped rejections stay out of the persistence tracker entirely: their anchor
        // slides every sample so they can never be a legitimate adoption target, and letting them
        // occupy the single tracker slot would keep resetting the real pool's rejection streak.
        if (!newbornShaped &&
            TrackRejectedIdentityPersistence(rejectedPersistence, incomingResetLocal, sampleUtc, decision))
        {
            decision.Accepted = true;
            decision.Reason = "reset_confirmed_by_rejected_persistence";
            ResetRejectedIdentityPersistence(rejectedPersistence);
        }
        return decision;
    }

    private static bool TrackRejectedIdentityPersistence(
        RejectedIdentityPersistenceState state,
        DateTime incomingResetLocal,
        DateTime sampleUtc,
        QuotaWindowIdentityDecision decision)
    {
        if (state == null || decision == null || incomingResetLocal == DateTime.MinValue)
        {
            return false;
        }

        DateTime normalizedSampleUtc = NormalizeStateUtc(sampleUtc == DateTime.MinValue ? DateTime.UtcNow : sampleUtc);
        bool sameRejectedIdentity = state.ResetLocal != DateTime.MinValue &&
            Math.Abs((incomingResetLocal - state.ResetLocal).TotalMinutes) <= QuotaIdentityToleranceMinutes;
        // A long-dormant streak is a different episode: without this guard a stale count from hours
        // ago could combine with one fresh rejection and adopt a pool on thin evidence.
        bool staleStreak = state.LastSeenUtc != DateTime.MinValue &&
            (normalizedSampleUtc - state.LastSeenUtc).TotalMinutes > QuotaRejectedPersistenceStaleGapMinutes;
        if (!sameRejectedIdentity || staleStreak || state.FirstSeenUtc == DateTime.MinValue || normalizedSampleUtc < state.FirstSeenUtc)
        {
            state.Reset();
            state.ResetLocal = incomingResetLocal;
            state.FirstSeenUtc = normalizedSampleUtc;
            state.Count = 1;
        }
        else
        {
            state.Count++;
        }

        state.LastSeenUtc = normalizedSampleUtc;
        decision.RejectedPersistenceCount = state.Count;
        decision.RejectedPersistenceFirstSeenUtc = state.FirstSeenUtc;
        return state.Count >= QuotaRejectedPersistenceMinSamples &&
            (normalizedSampleUtc - state.FirstSeenUtc).TotalMinutes >= QuotaRejectedPersistenceMinMinutes;
    }

    private static void ResetRejectedIdentityPersistence(RejectedIdentityPersistenceState state)
    {
        if (state != null)
        {
            state.Reset();
        }
    }

    private static string CombineQuotaIdentityReasons(
        QuotaWindowIdentityDecision fiveHour,
        QuotaWindowIdentityDecision weekly)
    {
        List<string> reasons = new List<string>();
        AddQuotaIdentityReason(reasons, fiveHour);
        AddQuotaIdentityReason(reasons, weekly);
        return string.Join(";", reasons.ToArray());
    }

    private static void AddQuotaIdentityReason(
        List<string> reasons,
        QuotaWindowIdentityDecision decision)
    {
        if (decision == null || decision.IdentitySame || string.IsNullOrEmpty(decision.Reason))
        {
            return;
        }

        if (!reasons.Contains(decision.Reason))
        {
            reasons.Add(decision.Reason);
        }
    }

    private static int GetNextFiveHourConsumptionRingBaseline(
        int currentBaselinePercent,
        int previousBalancePercent,
        int currentBalancePercent)
    {
        previousBalancePercent = ClampPercent(previousBalancePercent);
        currentBalancePercent = ClampPercent(currentBalancePercent);
        if (currentBalancePercent == previousBalancePercent)
        {
            return currentBaselinePercent >= 0
                ? ClampPercent(currentBaselinePercent)
                : -1;
        }

        return previousBalancePercent > currentBalancePercent
            ? previousBalancePercent
            : -1;
    }

    private static bool IsSuspiciousWeeklyConsumptionBaseline(
        int baselinePercent,
        int previousWeeklyBalancePercent,
        int currentWeeklyBalancePercent,
        DateTime trackedFiveHourResetLocal,
        DateTime currentFiveHourResetLocal)
    {
        baselinePercent = ClampPercent(baselinePercent);
        previousWeeklyBalancePercent = ClampPercent(previousWeeklyBalancePercent);
        currentWeeklyBalancePercent = ClampPercent(currentWeeklyBalancePercent);
        if (baselinePercent < 95 ||
            currentWeeklyBalancePercent > 50 ||
            baselinePercent - currentWeeklyBalancePercent < 50)
        {
            return false;
        }

        if (previousWeeklyBalancePercent >= 95 &&
            currentWeeklyBalancePercent < previousWeeklyBalancePercent)
        {
            return true;
        }

        if (previousWeeklyBalancePercent <= 50 &&
            currentWeeklyBalancePercent <= 50)
        {
            return true;
        }

        return trackedFiveHourResetLocal != DateTime.MinValue &&
            currentFiveHourResetLocal != DateTime.MinValue &&
            currentFiveHourResetLocal < trackedFiveHourResetLocal.AddMinutes(-1.0);
    }

    private void ResetQuotaReadDeltaTracking()
    {
        ResetQuotaReadDeltaTracking(GetActiveQuotaRuntimeState());
    }

    private static void ResetQuotaReadDeltaTracking(QuotaRuntimeState quotaState)
    {
        quotaState.LastFiveHourReadPercent = -1;
        quotaState.LastWeeklyReadPercent = -1;
        quotaState.LastReadSourceUtc = DateTime.MinValue;
        quotaState.FiveHourConsumptionRingBaselinePercent = -1;
        quotaState.TrackedFiveHourResetLocal = DateTime.MinValue;
        quotaState.TrackedWeeklyResetLocal = DateTime.MinValue;
        ResetRejectedIdentityPersistence(quotaState.FiveHourRejectedIdentity);
        ResetRejectedIdentityPersistence(quotaState.WeeklyRejectedIdentity);
        quotaState.WeeklyQuotaAtFiveHourWindowStartPercent = -1;
    }

    private static QuotaRingDecisionInfo CreateQuotaRingDecisionInfo(QuotaRuntimeState quotaState, CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        QuotaRingDecisionInfo decision = new QuotaRingDecisionInfo
        {
            SourceKnown = sourceKnown,
            SnapshotKnown = snapshot != null,
            PreviousFiveHourPercent = quotaState.LastFiveHourReadPercent,
            PreviousWeeklyPercent = quotaState.LastWeeklyReadPercent,
            PreviousSourceUpdatedUtc = quotaState.LastReadSourceUtc,
            PreviousFiveHourBaselinePercent = quotaState.FiveHourConsumptionRingBaselinePercent,
            PreviousWeeklyBaselinePercent = quotaState.WeeklyQuotaAtFiveHourWindowStartPercent,
            PreviousTrackedFiveHourResetLocal = quotaState.TrackedFiveHourResetLocal,
            PreviousTrackedWeeklyResetLocal = quotaState.TrackedWeeklyResetLocal,
            NextFiveHourBaselinePercent = quotaState.FiveHourConsumptionRingBaselinePercent,
            NextWeeklyBaselinePercent = quotaState.WeeklyQuotaAtFiveHourWindowStartPercent,
            NextTrackedFiveHourResetLocal = quotaState.TrackedFiveHourResetLocal,
            NextTrackedWeeklyResetLocal = quotaState.TrackedWeeklyResetLocal,
            NextSourceUpdatedUtc = quotaState.LastReadSourceUtc
        };

        if (snapshot != null)
        {
            decision.SourceKind = string.IsNullOrWhiteSpace(snapshot.SourceKind)
                ? "unknown"
                : snapshot.SourceKind;
            decision.RawFiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
            decision.RawWeeklyPercent = ClampPercent(snapshot.WeeklyPercent);
            decision.RawFiveHourUsedFieldName = snapshot.FiveHourUsedFieldName ?? string.Empty;
            decision.RawWeeklyUsedFieldName = snapshot.WeeklyUsedFieldName ?? string.Empty;
            decision.RawFiveHourUsedValue = snapshot.FiveHourRawUsedValue;
            decision.RawWeeklyUsedValue = snapshot.WeeklyRawUsedValue;
            decision.RawFiveHourNormalizedUsedPercent = snapshot.FiveHourNormalizedUsedPercent;
            decision.RawWeeklyNormalizedUsedPercent = snapshot.WeeklyNormalizedUsedPercent;
            decision.RawFiveHourUsageDiagnosticKnown = snapshot.FiveHourUsageDiagnosticKnown;
            decision.RawWeeklyUsageDiagnosticKnown = snapshot.WeeklyUsageDiagnosticKnown;
            decision.RawSourceUpdatedKnown = snapshot.SourceUpdatedKnown;
            decision.RawSourceUpdatedUtc = snapshot.SourceUpdatedKnown
                ? snapshot.SourceUpdatedUtc
                : DateTime.MinValue;
            decision.RawFiveHourResetLocal = snapshot.FiveHourResetKnown
                ? snapshot.FiveHourResetLocal
                : DateTime.MinValue;
            decision.RawWeeklyResetLocal = snapshot.WeeklyResetKnown
                ? snapshot.WeeklyResetLocal
                : DateTime.MinValue;
        }

        return decision;
    }

    private static QuotaRingDecisionInfo CompleteQuotaRingDecisionInfo(
        QuotaRuntimeState quotaState,
        QuotaRingDecisionInfo decision,
        string reason)
    {
        if (decision == null)
        {
            decision = new QuotaRingDecisionInfo();
        }

        decision.Reason = string.IsNullOrEmpty(decision.IdentityDecisionReason)
            ? reason
            : (string.IsNullOrEmpty(reason) || string.Equals(decision.IdentityDecisionReason, reason, StringComparison.Ordinal)
                ? decision.IdentityDecisionReason
                : decision.IdentityDecisionReason + ";" + reason);
        decision.NextFiveHourBaselinePercent = quotaState.FiveHourConsumptionRingBaselinePercent;
        decision.NextWeeklyBaselinePercent = quotaState.WeeklyQuotaAtFiveHourWindowStartPercent;
        decision.NextTrackedFiveHourResetLocal = quotaState.TrackedFiveHourResetLocal;
        decision.NextTrackedWeeklyResetLocal = quotaState.TrackedWeeklyResetLocal;
        decision.NextSourceUpdatedUtc = quotaState.LastReadSourceUtc;
        return decision;
    }

    private static string GetQuotaRingDecisionReason(
        bool fiveHourChanged,
        bool weeklyChanged,
        int fiveHourPercent,
        int previousFiveHourPercent,
        int weeklyPercent,
        int previousWeeklyPercent,
        bool fiveHourWindowAdvanced,
        bool weeklyWindowAdvanced,
        bool weeklyConsumptionBaselineRepaired)
    {
        List<string> reasons = new List<string>();
        if (weeklyConsumptionBaselineRepaired)
        {
            reasons.Add("suspicious_weekly_consumption_baseline_repaired");
        }

        if (fiveHourChanged)
        {
            if (fiveHourPercent < previousFiveHourPercent)
            {
                reasons.Add("five_hour_balance_decreased_set_previous_balance_as_consumption_baseline");
            }
            else if (fiveHourWindowAdvanced)
            {
                reasons.Add("five_hour_balance_increased_or_reset_clear_consumption_baseline");
            }
            else
            {
                reasons.Add("five_hour_balance_changed");
            }
        }

        if (weeklyChanged)
        {
            if (weeklyWindowAdvanced)
            {
                reasons.Add("weekly_balance_increased_reset_window_baseline");
            }
            else if (weeklyPercent < previousWeeklyPercent)
            {
                reasons.Add("weekly_balance_decreased_keep_five_hour_window_baseline");
            }
            else
            {
                reasons.Add("weekly_balance_changed");
            }
        }

        return reasons.Count == 0
            ? "sample_changed_without_visible_ring_change"
            : string.Join(";", reasons.ToArray());
    }

    private void LogQuotaRingDecision(
        CodexRadarSoftwareMode family,
        QuotaRingDecisionInfo decision,
        CodexQuotaSnapshot displaySnapshot,
        bool sourceKnown,
        bool appRunning)
    {
        if (decision == null)
        {
            return;
        }

        bool fiveHourProtected;
        bool weeklyProtected;
        bool fiveHourGold;
        bool weeklyGold;
        lock (this.quotaResetStateLock)
        {
            bool isCodex = NormalizeEffectiveSoftwareMode(family) == CodexRadarSoftwareMode.Codex;
            fiveHourProtected = isCodex && this.fiveHourQuotaProtectionUtc != DateTime.MinValue;
            weeklyProtected = isCodex && this.weeklyQuotaProtectionUtc != DateTime.MinValue;
            fiveHourGold = isCodex && this.fiveHourQuotaProtectionGold;
            weeklyGold = isCodex && this.weeklyQuotaProtectionGold;
        }

        int displayFiveHourPercent = displaySnapshot == null
            ? decision.RawFiveHourPercent
            : ClampPercent(displaySnapshot.FiveHourPercent);
        int displayWeeklyPercent = displaySnapshot == null
            ? decision.RawWeeklyPercent
            : ClampPercent(displaySnapshot.WeeklyPercent);
        int fiveHourDisplayBaseline = fiveHourProtected
            ? -1
            : decision.NextFiveHourBaselinePercent;
        int weeklyDisplayBaseline = sourceKnown && !fiveHourProtected && !weeklyProtected
            ? decision.NextWeeklyBaselinePercent
            : -1;

        // The persisted "consumption_ring_percent" is the visible consumed tail.
        // The baseline field records the complete arc that is drawn under the live balance.
        int fiveHourConsumptionPercent = GetVisibleQuotaConsumptionPercent(
            displayFiveHourPercent,
            fiveHourDisplayBaseline);
        int weeklyConsumptionPercent = GetVisibleQuotaConsumptionPercent(
            displayWeeklyPercent,
            weeklyDisplayBaseline);

        QuotaDecisionHistoryLogger.LogDecision(
            decision.Reason ?? "quota_ring_decision",
            sourceKnown,
            appRunning,
            new Dictionary<string, object>
            {
                { "software_family", NormalizeEffectiveSoftwareMode(family).ToString() },
                { "five_hour_balance_percent", displayFiveHourPercent },
                { "five_hour_raw_balance_percent", decision.RawFiveHourPercent },
                { "five_hour_source_used_field", EmptyFallback(decision.RawFiveHourUsedFieldName, string.Empty) },
                { "five_hour_source_raw_used_value", decision.RawFiveHourUsageDiagnosticKnown ? (object)decision.RawFiveHourUsedValue : null },
                { "five_hour_source_normalized_used_percent", decision.RawFiveHourUsageDiagnosticKnown ? (object)decision.RawFiveHourNormalizedUsedPercent : null },
                { "five_hour_consumption_ring_percent", fiveHourConsumptionPercent },
                { "five_hour_consumption_baseline_percent", PercentOrNull(fiveHourDisplayBaseline) },
                { "five_hour_previous_balance_percent", PercentOrNull(decision.PreviousFiveHourPercent) },
                { "five_hour_previous_baseline_percent", PercentOrNull(decision.PreviousFiveHourBaselinePercent) },
                { "five_hour_protected", fiveHourProtected },
                { "five_hour_gold", fiveHourGold },
                { "five_hour_reset_local", decision.RawFiveHourResetLocal },
                { "tracked_five_hour_reset_before", decision.PreviousTrackedFiveHourResetLocal },
                { "tracked_five_hour_reset_after", decision.NextTrackedFiveHourResetLocal },
                { "five_hour_anchor_age_minutes", decision.FiveHourAnchorAgeMinutes.HasValue ? (object)Math.Round(decision.FiveHourAnchorAgeMinutes.Value, 3) : null },
                { "five_hour_rejected_persistence_count", decision.FiveHourRejectedPersistenceCount },
                { "five_hour_rejected_persistence_first_seen_utc", decision.FiveHourRejectedPersistenceFirstSeenUtc == DateTime.MinValue ? null : (object)decision.FiveHourRejectedPersistenceFirstSeenUtc },
                { "weekly_balance_percent", displayWeeklyPercent },
                { "weekly_raw_balance_percent", decision.RawWeeklyPercent },
                { "weekly_source_used_field", EmptyFallback(decision.RawWeeklyUsedFieldName, string.Empty) },
                { "weekly_source_raw_used_value", decision.RawWeeklyUsageDiagnosticKnown ? (object)decision.RawWeeklyUsedValue : null },
                { "weekly_source_normalized_used_percent", decision.RawWeeklyUsageDiagnosticKnown ? (object)decision.RawWeeklyNormalizedUsedPercent : null },
                { "weekly_consumption_ring_percent", weeklyConsumptionPercent },
                { "weekly_consumption_baseline_percent", PercentOrNull(weeklyDisplayBaseline) },
                { "weekly_previous_balance_percent", PercentOrNull(decision.PreviousWeeklyPercent) },
                { "weekly_previous_baseline_percent", PercentOrNull(decision.PreviousWeeklyBaselinePercent) },
                { "weekly_protected", weeklyProtected },
                { "weekly_gold", weeklyGold },
                { "weekly_reset_local", decision.RawWeeklyResetLocal },
                { "tracked_weekly_reset_before", decision.PreviousTrackedWeeklyResetLocal },
                { "tracked_weekly_reset_after", decision.NextTrackedWeeklyResetLocal },
                { "weekly_anchor_age_minutes", decision.WeeklyAnchorAgeMinutes.HasValue ? (object)Math.Round(decision.WeeklyAnchorAgeMinutes.Value, 3) : null },
                { "weekly_rejected_persistence_count", decision.WeeklyRejectedPersistenceCount },
                { "weekly_rejected_persistence_first_seen_utc", decision.WeeklyRejectedPersistenceFirstSeenUtc == DateTime.MinValue ? null : (object)decision.WeeklyRejectedPersistenceFirstSeenUtc },
                { "source_kind", EmptyFallback(decision.SourceKind, "unknown") },
                { "detail", (decision.Reason ?? string.Empty).IndexOf("source_switch", StringComparison.OrdinalIgnoreCase) >= 0 ? "source_switch" : string.Empty },
                { "source_updated_utc", decision.RawSourceUpdatedUtc },
                { "source_updated_known", decision.RawSourceUpdatedKnown },
                { "tracked_source_updated_before_utc", decision.PreviousSourceUpdatedUtc },
                { "tracked_source_updated_after_utc", decision.NextSourceUpdatedUtc }
            });
    }

    private static int GetVisibleQuotaConsumptionPercent(int balancePercent, int baselinePercent)
    {
        if (baselinePercent < 0)
        {
            return 0;
        }

        return Math.Max(0, ClampPercent(baselinePercent) - ClampPercent(balancePercent));
    }

    private static object PercentOrNull(int percent)
    {
        return percent < 0 ? null : (object)ClampPercent(percent);
    }

    private void UpdateServiceConnectivityHealth()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.CurrentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            // NetworkChange callbacks only set this flag; the UI scheduler performs the actual query.
            if (!this.serviceNetworkRefreshRequested)
            {
                return;
            }

            this.serviceNetworkRefreshRequested = false;
        }

        bool networkAvailable = IsNetworkAvailable();
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = networkAvailable;
            if (!networkAvailable)
            {
                this.codexRuntimeState.RadarSiteHealth = ServiceHealthState.Offline;
                this.codexRuntimeState.Touch();
                this.openAiServiceHealth = ServiceHealthState.Offline;
                this.claudeServiceHealth = ServiceHealthState.Offline;
                SetCodexProviderUsageHealth(ServiceHealthState.Offline, "OFFLINE", "无网络");
                SetClaudeCodeUsageHealth(ServiceHealthState.Offline, "OFFLINE", "无网络");
                return;
            }

            if (this.codexRuntimeState.RadarSiteHealth == ServiceHealthState.Offline)
            {
                this.codexRuntimeState.RadarSiteHealth = ServiceHealthState.Unknown;
                this.codexRuntimeState.Touch();
            }

            if (this.claudeServiceHealth == ServiceHealthState.Offline)
            {
                this.claudeServiceHealth = ServiceHealthState.Unknown;
            }

            if (this.openAiServiceHealth == ServiceHealthState.Offline)
            {
                this.openAiServiceHealth = ServiceHealthState.Unknown;
            }
        }
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        RequestServiceNetworkRefresh();
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, "网络变化");
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, "网络变化");
        RequestDeepSeekServiceRefresh("网络变化");
        RequestSelectedQuotaUsageRefresh("网络变化");
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        RequestServiceNetworkRefresh();
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, "网络变化");
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, "网络变化");
        RequestDeepSeekServiceRefresh("网络变化");
        RequestSelectedQuotaUsageRefresh("网络变化");
    }

    private void RequestServiceNetworkRefresh()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.serviceNetworkRefreshRequested = true;
        }
    }

    private void RequestDeepSeekServiceRefresh(string trigger)
    {
        DeepSeekServiceMonitor.RequestRefresh(trigger);
        DeepSeekBalanceMonitor.RequestRefresh(trigger);
    }

    private void RefreshDeepSeekServiceIfNeeded()
    {
        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        DeepSeekServiceMonitor.RefreshIfNeeded(
            "codex_radar",
            "定时间隔",
            delegate
            {
                RequestCodexRadarRenderFromAnyThread(lease);
            });
        DeepSeekBalanceMonitor.RefreshIfNeeded(
            "codex_radar",
            "定时间隔",
            delegate
            {
                RequestCodexRadarRenderFromAnyThread(lease);
            });
    }

    private void RequestCodexRadarRenderFromAnyThread(OwnerOperationLease lease)
    {
        if (!IsOwnerOperationCurrent(lease))
        {
            return;
        }

        PublishProjectionStateFromOwner();
        try
        {
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }

            if (this.InvokeRequired)
            {
                TryBeginInvokeOwnerCurrent(lease, delegate
                {
                    PublishProjectionStateFromOwner();
                });
                return;
            }

            PublishProjectionStateFromOwner();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool IsServiceNetworkAvailable()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return true;
        }

        lock (this.serviceHealthLock)
        {
            return this.serviceNetworkAvailable;
        }
    }

    private bool ShouldForceServiceHealthRefresh(ServiceHealthState state)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return false;
        }

        lock (this.serviceHealthLock)
        {
            return state == ServiceHealthState.Unknown || state == ServiceHealthState.Offline;
        }
    }

    private void SetRadarServiceHealth(ServiceHealthState health)
    {
        SetRadarServiceHealth(CodexRadarSoftwareMode.Codex, health);
    }

    private void SetRadarServiceHealth(
        CodexRadarSoftwareMode family,
        ServiceHealthState health,
        bool publish = true)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.CurrentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            RadarFamilyRuntimeState state = GetRadarFamilyState(family);
            state.RadarSiteHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
            state.Touch();
        }

        if (publish)
        {
            PublishProjectionStateFromOwner();
        }
    }

    private void SetAllRadarServiceHealth(ServiceHealthState health)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        lock (this.serviceHealthLock)
        {
            ServiceHealthState effective = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
            this.codexRuntimeState.RadarSiteHealth = effective;
            this.codexRuntimeState.Touch();
        }

        PublishProjectionStateFromOwner();
    }

    private void SetClaudeServiceHealth(ServiceHealthState health)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.CurrentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.claudeServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }

        PublishProjectionStateFromOwner();
    }

    private void SetOpenAiServiceHealth(ServiceHealthState health)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.CurrentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.openAiServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }

        PublishProjectionStateFromOwner();
    }

    private void ApplyCodexStatuspageSnapshot(string serviceKey, StatuspageSnapshot snapshot)
    {
        StatuspageSnapshot local = snapshot == null
            ? StatuspageSnapshot.CreateDefault(serviceKey)
            : snapshot.Clone();
        ServiceHealthState health = ConvertStatuspageHealthState(local.State);
        if (string.Equals(serviceKey, StatuspageMonitor.ClaudeServiceKey, StringComparison.OrdinalIgnoreCase))
        {
            lock (this.claudeStatusLock)
            {
                this.claudeStatusRequestRunning = local.RequestRunning;
                this.nextClaudeStatusRefreshUtc = DateTime.UtcNow.AddMinutes(
                    health == ServiceHealthState.Normal ? 15.0 : 2.0);
                this.claudeStatusRefreshTrigger = health == ServiceHealthState.Normal ? "定时间隔" : "异常状态重试";
            }

            SetClaudeServiceHealth(health);
            return;
        }

        if (string.Equals(serviceKey, StatuspageMonitor.OpenAiServiceKey, StringComparison.OrdinalIgnoreCase))
        {
            lock (this.openAiStatusLock)
            {
                this.openAiStatusRequestRunning = local.RequestRunning;
                this.nextOpenAiStatusRefreshUtc = DateTime.UtcNow.AddMinutes(
                    health == ServiceHealthState.Normal ? 15.0 : 2.0);
                this.openAiStatusRefreshTrigger = health == ServiceHealthState.Normal ? "定时间隔" : "异常状态重试";
            }

            SetOpenAiServiceHealth(health);
        }
    }

    private static ServiceHealthState ConvertStatuspageHealthState(StatuspageHealthState state)
    {
        switch (state)
        {
            case StatuspageHealthState.Normal:
                return ServiceHealthState.Normal;
            case StatuspageHealthState.Degraded:
                return ServiceHealthState.Degraded;
            case StatuspageHealthState.Incomplete:
                return ServiceHealthState.Incomplete;
            case StatuspageHealthState.Offline:
                return ServiceHealthState.Offline;
            case StatuspageHealthState.Unavailable:
                return ServiceHealthState.Unavailable;
            case StatuspageHealthState.Unreachable:
                return ServiceHealthState.Unreachable;
            default:
                return ServiceHealthState.Unknown;
        }
    }

    private void ApplyServiceHealthTestMode()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        ServiceHealthTestMode mode = this.CurrentSettings.ServiceHealthTestMode;
        if (mode == ServiceHealthTestMode.Off)
        {
            return;
        }

        ServiceHealthState state = ConvertServiceHealthTestMode(mode);
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = mode != ServiceHealthTestMode.Offline;
            this.codexRuntimeState.RadarSiteHealth = state;
            this.codexRuntimeState.Touch();
            this.openAiServiceHealth = state;
            this.claudeServiceHealth = state;
        }

        PublishProjectionStateFromOwner();
    }

    private void ResetServiceHealthAfterTestMode()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        bool networkAvailable = IsNetworkAvailable();
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkRefreshRequested = false;
            this.serviceNetworkAvailable = networkAvailable;
            this.codexRuntimeState.RadarSiteHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
            this.codexRuntimeState.Touch();
            this.openAiServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
            this.claudeServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
        }

        PublishProjectionStateFromOwner();

        lock (this.claudeStatusLock)
        {
            this.nextClaudeStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
        }
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, "测试模式恢复");

        lock (this.openAiStatusLock)
        {
            this.nextOpenAiStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
        }
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, "测试模式恢复");

        RequestSelectedQuotaUsageRefresh("测试模式恢复");

        lock (this.codexRadarStatusLock)
        {
            this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(4.0);
        }
    }

    private static ServiceHealthState ConvertServiceHealthTestMode(ServiceHealthTestMode mode)
    {
        if (mode == ServiceHealthTestMode.Normal)
        {
            return ServiceHealthState.Normal;
        }

        if (mode == ServiceHealthTestMode.Offline)
        {
            return ServiceHealthState.Offline;
        }

        if (mode == ServiceHealthTestMode.Unavailable)
        {
            return ServiceHealthState.Unavailable;
        }

        if (mode == ServiceHealthTestMode.Unreachable)
        {
            return ServiceHealthState.Unreachable;
        }

        return ServiceHealthState.Unknown;
    }

    private static bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static DateTime GetNextCodexRadarScheduledRefreshUtc(
        DateTime nowUtc,
        CodexRadarSnapshot snapshot,
        ServiceHealthState health)
    {
        if (health != ServiceHealthState.Normal)
        {
            return nowUtc.AddMinutes(10.0);
        }

        return TimeZoneUtilities.GetNextBeijingHourUtc(nowUtc);
    }

    private void RefreshClaudeStatusIfNeeded()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (!IsServiceNetworkAvailable())
        {
            SetClaudeServiceHealth(ServiceHealthState.Offline);
            return;
        }

        WidgetSettings settings = this.CurrentSettings == null ? null : this.CurrentSettings.Clone();
        if (settings == null)
        {
            return;
        }

        string trigger;
        lock (this.claudeStatusLock)
        {
            trigger = EmptyFallback(this.claudeStatusRefreshTrigger, "定时间隔");
            this.claudeStatusRefreshTrigger = "定时间隔";
        }

        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        Task<StatuspageRefreshOutcome> task;
        if (!StatuspageMonitor.TryStartOrJoin(
            StatuspageMonitor.ClaudeServiceKey,
            "codex_radar",
            settings,
            trigger,
            out task))
        {
            ApplyCodexStatuspageSnapshot(StatuspageMonitor.ClaudeServiceKey, StatuspageMonitor.GetSnapshot(StatuspageMonitor.ClaudeServiceKey));
            return;
        }

        ApplyCodexStatuspageSnapshot(StatuspageMonitor.ClaudeServiceKey, StatuspageMonitor.GetSnapshot(StatuspageMonitor.ClaudeServiceKey));
        task.ContinueWith(delegate(Task<StatuspageRefreshOutcome> completed)
        {
            Exception observed = completed.Exception == null
                ? null
                : completed.Exception.GetBaseException();
            if (!IsOwnerOperationCurrent(lease))
            {
                return;
            }

            TryExecuteOwnerCurrent(lease, delegate
            {
                if (observed != null)
                {
                    Program.LogException(observed);
                }

                StatuspageSnapshot snapshot = completed.Status == TaskStatus.RanToCompletion && completed.Result != null
                    ? completed.Result.Snapshot
                    : StatuspageMonitor.GetSnapshot(StatuspageMonitor.ClaudeServiceKey);
                ApplyCodexStatuspageSnapshot(StatuspageMonitor.ClaudeServiceKey, snapshot);
            });
            RequestCodexRadarRenderFromAnyThread(lease);
        });
    }

    private void RefreshOpenAiStatusIfNeeded()
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (!IsServiceNetworkAvailable())
        {
            SetOpenAiServiceHealth(ServiceHealthState.Offline);
            return;
        }

        WidgetSettings settings = this.CurrentSettings == null ? null : this.CurrentSettings.Clone();
        if (settings == null)
        {
            return;
        }

        string trigger;
        lock (this.openAiStatusLock)
        {
            trigger = EmptyFallback(this.openAiStatusRefreshTrigger, "定时间隔");
            this.openAiStatusRefreshTrigger = "定时间隔";
        }

        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        Task<StatuspageRefreshOutcome> task;
        if (!StatuspageMonitor.TryStartOrJoin(
            StatuspageMonitor.OpenAiServiceKey,
            "codex_radar",
            settings,
            trigger,
            out task))
        {
            ApplyCodexStatuspageSnapshot(StatuspageMonitor.OpenAiServiceKey, StatuspageMonitor.GetSnapshot(StatuspageMonitor.OpenAiServiceKey));
            return;
        }

        ApplyCodexStatuspageSnapshot(StatuspageMonitor.OpenAiServiceKey, StatuspageMonitor.GetSnapshot(StatuspageMonitor.OpenAiServiceKey));
        task.ContinueWith(delegate(Task<StatuspageRefreshOutcome> completed)
        {
            Exception observed = completed.Exception == null
                ? null
                : completed.Exception.GetBaseException();
            if (!IsOwnerOperationCurrent(lease))
            {
                return;
            }

            TryExecuteOwnerCurrent(lease, delegate
            {
                if (observed != null)
                {
                    Program.LogException(observed);
                }

                StatuspageSnapshot snapshot = completed.Status == TaskStatus.RanToCompletion && completed.Result != null
                    ? completed.Result.Snapshot
                    : StatuspageMonitor.GetSnapshot(StatuspageMonitor.OpenAiServiceKey);
                ApplyCodexStatuspageSnapshot(StatuspageMonitor.OpenAiServiceKey, snapshot);
            });
            RequestCodexRadarRenderFromAnyThread(lease);
        });
    }

    private void RefreshCodexRadarStatusIfNeeded()
    {
        if (this.CurrentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            return;
        }

        if (!IsServiceNetworkAvailable())
        {
            SetAllRadarServiceHealth(ServiceHealthState.Offline);
            return;
        }

        RefreshRadarFamilyStatusIfNeeded(CodexRadarSoftwareMode.Codex);
    }

    private void RefreshRadarFamilyStatusIfNeeded(CodexRadarSoftwareMode requestedSoftwareMode)
    {
        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        DateTime attemptLocal = DateTime.Now;
        requestedSoftwareMode = NormalizeEffectiveSoftwareMode(requestedSoftwareMode);
        RadarFamilyRuntimeState requestedState = GetRadarFamilyState(requestedSoftwareMode);
        string requestedModelKey = GetSelectedRadarModelKeyForSoftwareMode(requestedSoftwareMode);
        bool shouldStart = false;
        bool forceRefresh = ShouldForceServiceHealthRefresh(requestedState.RadarSiteHealth);
        string trigger = "定时间隔";
        TryExecuteOwnerCurrent(lease, delegate
        {
            lock (this.codexRadarStatusLock)
            {
                bool scheduledRefreshDue = requestedState.NextRadarStatusRefreshUtc == DateTime.MinValue ||
                    nowUtc >= requestedState.NextRadarStatusRefreshUtc;
                if (!requestedState.RadarStatusRequestRunning &&
                    (scheduledRefreshDue || forceRefresh))
                {
                    requestedState.RadarStatusRequestRunning = true;
                    requestedState.LastRadarStatusAttemptLocal = attemptLocal;
                    trigger = forceRefresh
                        ? "异常状态重试"
                        : (requestedState.NextRadarStatusRefreshUtc == DateTime.MinValue
                            ? "首次刷新"
                            : EmptyFallback(requestedState.RadarStatusRefreshTrigger, "定时间隔"));
                    requestedState.RadarStatusRefreshTrigger = "定时间隔";
                    shouldStart = true;
                }
            }
        });

        if (!shouldStart)
        {
            return;
        }

        PublishProjectionStateFromOwner();

        WidgetSettings requestSettings = this.CurrentSettings.Clone();
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool publicJsonEnabled = requestSettings.CodexRadarPublicJsonEnabled;
            bool htmlFallbackEnabled = requestSettings.CodexRadarHtmlFallbackEnabled;
            bool rssFallbackEnabled = requestSettings.CodexRadarRssFallbackEnabled;
            CodexRadarSnapshot snapshot;
            bool known = false;
            ServiceHealthState health = ServiceHealthState.Unknown;
            CodexRadarModelCatalogUpdate catalogUpdate = null;
            try
            {
                known = TryReadCodexRadarStatus(
                    requestedModelKey,
                    publicJsonEnabled,
                    htmlFallbackEnabled,
                    rssFallbackEnabled,
                    out snapshot,
                    out health,
                    out catalogUpdate,
                    lease.CancellationToken);
            }
            catch (Exception ex)
            {
                snapshot = null;
                health = ServiceHealthState.Unreachable;
                TryExecuteOwnerCurrent(lease, delegate { Program.LogException(ex); });
            }

            stopwatch.Stop();
            TryExecuteOwnerCurrent(lease, delegate
            {
                CodexRadarSnapshot snapshotToCache = null;
                bool modelStillSelected;
                lock (this.codexRadarStatusLock)
                {
                    modelStillSelected = string.Equals(
                        requestedModelKey,
                        GetSelectedRadarModelKeyForSoftwareMode(requestedSoftwareMode),
                        StringComparison.OrdinalIgnoreCase);
                    if (!modelStillSelected)
                    {
                        requestedState.NextRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
                        requestedState.RadarStatusRefreshTrigger = "模型或软件切换";
                    }
                    else if (known && snapshot != null)
                    {
                        CodexRadarSnapshot previousSnapshot = requestedState.RadarSnapshot;
                        MergeCodexModelIqHistory(snapshot, previousSnapshot);
                        ApplyCodexModelIqEfficiencyFromHistory(snapshot);
                        PreserveCodexModelIqSnapshot(snapshot, previousSnapshot);
                        PreserveCodexRadarResetJudgement(snapshot, previousSnapshot);
                        PreserveCodexModelIqRefreshTimeIfContentUnchanged(snapshot, previousSnapshot);
                        requestedState.RadarSnapshot = snapshot;
                        requestedState.ModelKey = requestedModelKey;
                        requestedState.Touch();
                        snapshotToCache = snapshot.Clone();
                    }
                    else if (requestedState.RadarSnapshot != null)
                    {
                        requestedState.RadarSnapshot.ModelIqRefreshSucceeded = false;
                        requestedState.Touch();
                    }

                    if (modelStillSelected)
                    {
                        requestedState.NextRadarStatusRefreshUtc = GetNextCodexRadarScheduledRefreshUtc(
                            DateTime.UtcNow,
                            snapshot,
                            health);
                        requestedState.RadarStatusRefreshTrigger = health == ServiceHealthState.Normal ? "定时间隔" : "异常状态重试";
                    }

                    requestedState.RadarStatusRequestRunning = false;
                }

                if (snapshotToCache != null)
                {
                    SaveCodexRadarCache(requestedSoftwareMode, requestedModelKey, snapshotToCache);
                    if (requestedSoftwareMode == CodexRadarSoftwareMode.Codex)
                    {
                        HandleCodexRadarWindowAndResetEvents(snapshotToCache);
                    }
                }

                if (catalogUpdate != null)
                {
                    ReloadCodexIqCatalogSnapshot(false);
                }

                SetRadarServiceHealth(requestedSoftwareMode, health, false);
                PublishProjectionStateFromOwner();
                ShowCodexRadarModelCatalogNotifications(catalogUpdate, lease);
                NetworkCheckHistoryLogger.LogCompleted(
                    "codex_radar",
                    "codex_radar_status",
                    trigger,
                    known ? health.ToString() : "未知 " + health.ToString(),
                    known && health == ServiceHealthState.Normal,
                    (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                    new Dictionary<string, object>
                    {
                        { "health", health.ToString() },
                        { "known", known },
                        { "model_still_selected", modelStillSelected },
                        { "software_mode", requestedSoftwareMode.ToString() }
                    });

                TryBeginInvokeOwnerCurrent(lease, delegate { PublishProjectionStateFromOwner(); });
            });
        });
    }

    private string GetSelectedRadarModelKeyForSoftwareMode(CodexRadarSoftwareMode softwareMode)
    {
        if (softwareMode == CodexRadarSoftwareMode.Claude)
        {
            return string.Empty;
        }

        return this.CurrentSettings == null
            ? CodexRadarModelCatalog.DefaultModelKey
            : (this.CurrentSettings.CodexRadarModelKey ?? CodexRadarModelCatalog.DefaultModelKey);
    }

    private void ApplyRadarClockAutoSwitchIfNeeded(bool forceCurrentSelectionUnavailable = false)
    {
        if (this.CurrentSettings == null ||
            !this.CurrentSettings.RadarClockAutoSwitchModelEnabled ||
            this.CurrentSettings.CodexRadarRandomTestEnabled)
        {
            return;
        }

        bool requestRunning;
        CodexRadarSnapshot snapshot;
        lock (this.codexRadarStatusLock)
        {
            requestRunning = this.codexRadarStatusRequestRunning;
            snapshot = this.codexRadarSnapshot == null ? null : this.codexRadarSnapshot.Clone();
        }

        if (requestRunning)
        {
            return;
        }

        CodexRadarSoftwareMode softwareMode = GetEffectiveCodexRadarSoftwareMode();
        // Claude is quota-only and has no model catalog to auto-select.
        if (softwareMode == CodexRadarSoftwareMode.Claude)
        {
            return;
        }

        double cycleHours = 12.0;
        DateTime nowLocal = DateTime.Now;
        DateTime boundary = RadarClockDial.GetCycleBoundaryLocal(nowLocal, cycleHours);
        DateTime previousBoundary = boundary.AddHours(-cycleHours);
        DateTime currentDataLocal;
        bool currentKnown = TryGetRadarClockDataTime(snapshot, softwareMode, out currentDataLocal);
        if (!forceCurrentSelectionUnavailable && currentKnown && currentDataLocal >= previousBoundary)
        {
            return;
        }

        string currentKey = GetSelectedRadarModelKeyForSoftwareMode(softwareMode);
        string targetKey;
        DateTime targetDataLocal;
        if (!TryFindRadarClockAutoSwitchTarget(
            softwareMode,
            currentKey,
            previousBoundary,
            snapshot,
            out targetKey,
            out targetDataLocal))
        {
            return;
        }

        string signature = softwareMode.ToString() + "|" +
            boundary.Ticks.ToString(CultureInfo.InvariantCulture) + "|" +
            currentKey + "|" +
            targetKey + "|" +
            targetDataLocal.Ticks.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(this.lastRadarClockAutoSwitchSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        this.lastRadarClockAutoSwitchSignature = signature;
        try
        {
            WidgetSettings settings = WidgetSettings.Load();
            settings.CodexRadarModelKey = CodexRadarModelCatalog.NormalizeModelKey(targetKey);

            settings.Save();
            ApplyRuntimeSettings(settings);
            ShowCodexNotification(
                "Radar 时钟自动切换",
                softwareMode.ToString() + " 模型切换到 " + targetKey + "。",
                ToolTipIcon.Info);
            Program.LogInfo("Radar clock auto-switched model. Mode=" + softwareMode + ", From=" + currentKey + ", To=" + targetKey + ", PreviousBoundary=" + previousBoundary.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static bool TryGetRadarClockDataTime(
        CodexRadarSnapshot snapshot,
        CodexRadarSoftwareMode softwareMode,
        out DateTime dataLocal)
    {
        dataLocal = DateTime.MinValue;
        if (snapshot == null)
        {
            return false;
        }

        if (snapshot.ModelIqDataDateKnown)
        {
            dataLocal = snapshot.ModelIqDataDateLocal.Date.AddHours(
                snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0);
            return true;
        }

        return false;
    }

    private bool TryFindRadarClockAutoSwitchTarget(
        CodexRadarSoftwareMode softwareMode,
        string currentKey,
        DateTime minimumDataLocal,
        CodexRadarSnapshot snapshot,
        out string targetKey,
        out DateTime targetDataLocal)
    {
        targetKey = string.Empty;
        targetDataLocal = DateTime.MinValue;
        List<CodexRadarModelInfo> models = CodexRadarModelCatalog.LoadModels();
        for (int i = 0; i < models.Count; i++)
        {
            CodexRadarModelInfo model = models[i];
            if (model == null || !model.Available || string.IsNullOrWhiteSpace(model.Key))
            {
                continue;
            }

            string key = CodexRadarModelCatalog.NormalizeModelKey(model.Key);
            if (string.Equals(key, CodexRadarModelCatalog.NormalizeModelKey(currentKey), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CodexRadarSnapshot candidate = LoadCodexRadarCache(CodexRadarSoftwareMode.Codex, key);
            DateTime candidateDataLocal;
            if (!TryGetRadarClockDataTime(candidate, CodexRadarSoftwareMode.Codex, out candidateDataLocal) ||
                candidateDataLocal < minimumDataLocal)
            {
                continue;
            }

            if (candidateDataLocal > targetDataLocal)
            {
                targetKey = key;
                targetDataLocal = candidateDataLocal;
            }
        }

        return targetKey.Length > 0;
    }

    private static void PreserveCodexModelIqSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || target.ModelIqKnown || !source.ModelIqKnown)
        {
            return;
        }

        // current.json may temporarily omit model_iq; preserve the last known IQ fields then.
        bool refreshSucceeded = target.ModelIqRefreshSucceeded;
        CopyCodexModelIqSnapshot(target, source);
        target.ModelIqRefreshSucceeded = refreshSucceeded;
    }

    private static void PreserveCodexModelIqRefreshTimeIfContentUnchanged(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null ||
            source == null ||
            !target.ModelIqKnown ||
            !source.ModelIqKnown ||
            !source.ModelIqRefreshedAtKnown)
        {
            return;
        }

        string targetSignature = BuildCodexModelIqContentSignature(target);
        string sourceSignature = !string.IsNullOrEmpty(source.ModelIqCachedContentSignature)
            ? source.ModelIqCachedContentSignature
            : BuildCodexModelIqContentSignature(source);
        if (targetSignature.Length == 0 ||
            sourceSignature.Length == 0 ||
            !string.Equals(targetSignature, sourceSignature, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(source.ModelIqCachedContentSignature) &&
                targetSignature.Length > 0 &&
                !string.Equals(targetSignature, sourceSignature, StringComparison.Ordinal))
            {
                Program.LogInfo("Codex IQ cached content changed. SourceSignature=" + sourceSignature +
                    ", TargetSignature=" + targetSignature);
            }

            return;
        }

        // RefreshedUtc drives the small clock marker. Reusing it for identical IQ content prevents
        // hourly same-data reads from moving the marker away from the true first-seen time.
        target.ModelIqRefreshedAtLocal = source.ModelIqRefreshedAtLocal;
        target.ModelIqCachedContentSignature = source.ModelIqCachedContentSignature;
        target.ModelIqRefreshedAtKnown = true;
    }

    // Signature that decides whether the small clock marker (first-seen time) may be preserved.
    // It must contain ONLY the site-provided batch identity and its core result. Derived/presentation
    // fields (efficiency percents, raw efficiency inputs, normal range, display-max) are excluded on
    // purpose: JSON, HTML and history-merge paths normalize, round and back-fill those differently, so
    // including them let the same 7.xx batch look "changed" and moved the marker to the request time.
    private static string BuildCodexModelIqContentSignature(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqKnown)
        {
            return string.Empty;
        }

        StringBuilder key = new StringBuilder(128);
        key.Append(snapshot.ModelIqDataDateKnown ? snapshot.ModelIqDataDateLocal.Date.Ticks : 0L).Append('|');
        key.Append(snapshot.ModelIqDataWindowKnown ? snapshot.ModelIqDataWindowStartHourLocal : -1).Append('|');
        key.Append(snapshot.ModelIqDataLabelKnown ? (snapshot.ModelIqDataLabel ?? string.Empty).Trim() : string.Empty).Append('|');
        key.Append(snapshot.ModelIqPassedKnown ? snapshot.ModelIqPassed : -1).Append('|');
        key.Append(snapshot.ModelIqValidTasks).Append('|');
        key.Append(snapshot.ModelIqPassRatePercent).Append('|');
        key.Append(snapshot.ModelIqStatus ?? string.Empty);
        return key.ToString();
    }

    private static void CopyCodexModelIqSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || !source.ModelIqKnown)
        {
            return;
        }

        target.ModelIqStatus = source.ModelIqStatus;
        target.ModelIqPassRatePercent = source.ModelIqPassRatePercent;
        target.ModelIqPassed = source.ModelIqPassed;
        target.ModelIqValidTasks = source.ModelIqValidTasks;
        target.ModelIqTokenEfficiencyPercent = source.ModelIqTokenEfficiencyPercent;
        target.ModelIqTimeEfficiencyPercent = source.ModelIqTimeEfficiencyPercent;
        target.ModelIqEfficiencyPassed = source.ModelIqEfficiencyPassed;
        target.ModelIqEfficiencyTotalTokens = source.ModelIqEfficiencyTotalTokens;
        target.ModelIqEfficiencySerialSeconds = source.ModelIqEfficiencySerialSeconds;
        target.ModelIqPassedKnown = source.ModelIqPassedKnown;
        target.ModelIqEfficiencyInputKnown = source.ModelIqEfficiencyInputKnown;
        target.ModelIqEfficiencyKnown = source.ModelIqEfficiencyKnown;
        target.ModelIqRefreshedAtLocal = source.ModelIqRefreshedAtLocal;
        target.ModelIqCachedContentSignature = source.ModelIqCachedContentSignature;
        target.ModelIqDataDateLocal = source.ModelIqDataDateLocal;
        target.ModelIqDataWindowStartHourLocal = source.ModelIqDataWindowStartHourLocal;
        target.ModelIqDataLabel = source.ModelIqDataLabel;
        target.ModelIqRefreshedAtKnown = source.ModelIqRefreshedAtKnown;
        target.ModelIqDataDateKnown = source.ModelIqDataDateKnown;
        target.ModelIqDataWindowKnown = source.ModelIqDataWindowKnown;
        target.ModelIqDataLabelKnown = source.ModelIqDataLabelKnown;
        target.ModelIqNormalLowScore = source.ModelIqNormalLowScore;
        target.ModelIqNormalHighScore = source.ModelIqNormalHighScore;
        target.ModelIqNormalRangeKnown = source.ModelIqNormalRangeKnown;
        target.ModelIqDisplayMaxScore = source.ModelIqDisplayMaxScore;
        target.ModelIqDisplayMaxScoreKnown = source.ModelIqDisplayMaxScoreKnown;
        target.ModelIqRefreshSucceeded = source.ModelIqRefreshSucceeded;
        target.ModelIqKnown = source.ModelIqKnown;
        target.ModelIqHistory = CloneCodexModelHistory(source.ModelIqHistory);
        target.CodexIqModels = CloneCodexIqBoardModels(source.CodexIqModels);
        target.ClockModelCandidates = CloneRadarClockModelCandidates(source.ClockModelCandidates);
    }

    private static void CopyCodexRadarWindowSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || !source.SpeedWindowKnown)
        {
            return;
        }

        bool sourceHasExplicitWindowState =
            source.SpeedWindowOpen ||
            string.Equals(source.SpeedWindowStatus, "open", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.SpeedWindowStatus, "closed", StringComparison.OrdinalIgnoreCase);
        if (!target.SpeedWindowKnown || sourceHasExplicitWindowState)
        {
            target.SpeedWindowKnown = true;
            target.SpeedWindowOpen = source.SpeedWindowOpen;
            target.SpeedWindowStatus = source.SpeedWindowStatus;
            target.SpeedWindowEventId = source.SpeedWindowEventId;
            target.SpeedWindowOpenedAtLocal = source.SpeedWindowOpenedAtLocal;
            target.SpeedWindowOpenedAtKnown = source.SpeedWindowOpenedAtKnown;
            target.SpeedWindowClosedAtLocal = source.SpeedWindowClosedAtLocal;
            target.SpeedWindowClosedAtKnown = source.SpeedWindowClosedAtKnown;
        }
        else if (source.SpeedWindowClosedAtKnown)
        {
            target.SpeedWindowClosedAtLocal = source.SpeedWindowClosedAtLocal;
            target.SpeedWindowClosedAtKnown = true;
        }

        ExpireCodexRadarSpeedWindowIfClosed(target, DateTime.Now);
    }

    private static bool TryReadCodexRadarStatus(
        string modelKey,
        bool publicJsonEnabled,
        bool htmlFallbackEnabled,
        bool rssFallbackEnabled,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        snapshot = null;
        health = ServiceHealthState.Unreachable;
        catalogUpdate = null;
        ServiceHealthState primaryHealth = ServiceHealthState.Unavailable;
        bool parsed = false;

        if (publicJsonEnabled)
        {
            string content;
            if (TryReadCodexRadarUrlText(
                AddCacheBuster(CodexRadarStatusUrl),
                "application/json,text/plain,*/*",
                out content,
                out primaryHealth,
                cancellationToken))
            {
                parsed = TryParseCodexRadarStatus(
                    content,
                    modelKey,
                    rssFallbackEnabled,
                    out snapshot,
                    out catalogUpdate,
                    cancellationToken);
                if (!parsed)
                {
                    primaryHealth = ServiceHealthState.Unavailable;
                }
            }
        }

        if (htmlFallbackEnabled && ShouldRequestCodexRadarHtmlFallback(parsed, snapshot))
        {
            CodexRadarSnapshot htmlSnapshot;
            CodexRadarModelCatalogUpdate htmlCatalogUpdate;
            ServiceHealthState htmlHealth;
            if (TryReadCodexRadarHomeHtmlStatus(
                modelKey,
                out htmlSnapshot,
                out htmlHealth,
                out htmlCatalogUpdate,
                cancellationToken))
            {
                if (snapshot == null)
                {
                    snapshot = CodexRadarSnapshot.CreateDefault();
                }

                // Homepage HTML is not an IQ source. It may only fill speed-window and Reset Radar
                // judgement fields which remained unknown after the schema-checked JSON adapter.
                bool fallbackContributed = FillUnknownCodexRadarFields(snapshot, htmlSnapshot);
                catalogUpdate = MergeCodexRadarModelCatalogUpdates(catalogUpdate, htmlCatalogUpdate);
                parsed = parsed || fallbackContributed;
            }
            else if (!parsed)
            {
                health = htmlHealth;
                return false;
            }
        }

        if (!parsed)
        {
            health = primaryHealth;
            return false;
        }

        health = GetCodexRadarSnapshotHealth(snapshot);
        return true;
    }

    private static bool ShouldRequestCodexRadarHtmlFallback(bool jsonParsed, CodexRadarSnapshot snapshot)
    {
        return !jsonParsed ||
            snapshot == null ||
            !snapshot.SpeedWindowKnown ||
            !snapshot.ResetRadarKnown;
    }

    private static bool FillUnknownCodexRadarFields(
        CodexRadarSnapshot target,
        CodexRadarSnapshot fallback)
    {
        if (target == null || fallback == null)
        {
            return false;
        }

        bool contributed = false;
        if (!target.SpeedWindowKnown && fallback.SpeedWindowKnown)
        {
            CopyCodexRadarWindowSnapshot(target, fallback);
            contributed = true;
        }
        else if (target.SpeedWindowKnown && fallback.SpeedWindowKnown)
        {
            if (!target.SpeedWindowOpenedAtKnown && fallback.SpeedWindowOpenedAtKnown)
            {
                target.SpeedWindowOpenedAtLocal = fallback.SpeedWindowOpenedAtLocal;
                target.SpeedWindowOpenedAtKnown = true;
                contributed = true;
            }

            if (!target.SpeedWindowClosedAtKnown && fallback.SpeedWindowClosedAtKnown)
            {
                target.SpeedWindowClosedAtLocal = fallback.SpeedWindowClosedAtLocal;
                target.SpeedWindowClosedAtKnown = true;
                contributed = true;
            }
        }

        if (!target.ResetRadarKnown && fallback.ResetRadarKnown)
        {
            CopyCodexRadarResetJudgementSnapshot(target, fallback);
            contributed = true;
        }

        return contributed;
    }

    private static void CopyCodexRadarResetJudgementSnapshot(
        CodexRadarSnapshot target,
        CodexRadarSnapshot source)
    {
        if (target == null || source == null)
        {
            return;
        }

        target.ResetRadarKnown = source.ResetRadarKnown;
        target.ResetRadarUpdatedAtLocal = source.ResetRadarUpdatedAtLocal;
        target.ResetRadarUpdatedAtKnown = source.ResetRadarUpdatedAtKnown;
        target.ResetCardStatus = source.ResetCardStatus;
        target.ResetCardDescription = source.ResetCardDescription;
        target.HardResetStatus = source.HardResetStatus;
        target.HardResetDescription = source.HardResetDescription;
    }

    private static void PreserveCodexRadarResetJudgement(
        CodexRadarSnapshot target,
        CodexRadarSnapshot previous)
    {
        if (target == null || target.ResetRadarKnown ||
            previous == null || !previous.ResetRadarKnown)
        {
            return;
        }

        CopyCodexRadarResetJudgementSnapshot(target, previous);
    }

    private static bool TryReadCodexRadarHomeHtmlStatus(
        string modelKey,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate,
        CancellationToken cancellationToken)
    {
        snapshot = null;
        catalogUpdate = null;
        string content;
        if (!TryReadCodexRadarUrlText(
            AddCacheBuster(CodexRadarHomeUrl),
            "text/html,application/xhtml+xml,*/*",
            out content,
            out health,
            cancellationToken))
        {
            return false;
        }

        if (!TryParseCodexRadarHtmlFallbackStatus(content, out snapshot))
        {
            health = ServiceHealthState.Unavailable;
            return false;
        }

        catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
            ExtractCodexRadarHtmlModelCatalog(content),
            false);
        health = GetCodexRadarSnapshotHealth(snapshot);
        return true;
    }

    private static bool TryParseCodexRadarHtmlFallbackStatus(
        string content,
        out CodexRadarSnapshot snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        CodexRadarSnapshot candidate = CodexRadarSnapshot.CreateDefault();
        candidate.FetchedAtLocal = DateTime.Now;
        candidate.FetchedAtKnown = true;
        ApplyCodexRadarHtmlWindowStatus(content, candidate);
        ApplyCodexRadarHtmlResetJudgement(content, candidate);

        // Dynamic placeholders and decorative HTML must not be reported as usable fallback data.
        if (!candidate.SpeedWindowKnown && !candidate.ResetRadarKnown)
        {
            return false;
        }

        snapshot = candidate;
        return true;
    }

    private static bool TryReadCodexRadarUrlText(
        string url,
        string accept,
        out string content,
        out ServiceHealthState health,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        content = string.Empty;
        health = ServiceHealthState.Unreachable;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = accept;
            request.UserAgent = ProductIdentity.UserAgent;
            request.Timeout = CodexRadarStatusTimeoutMs;
            request.ReadWriteTimeout = CodexRadarStatusTimeoutMs;
            request.AllowAutoRedirect = false;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";

            int maxBytes = accept != null &&
                accept.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0
                    ? BoundedHttpTextReader.HtmlMaxBytes
                    : BoundedHttpTextReader.PublicJsonMaxBytes;
            BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
                request,
                maxBytes,
                CodexRadarStatusTimeoutMs,
                cancellationToken);
            if (!response.Success)
            {
                health = response.StatusCode > 0
                    ? ServiceHealthState.Unavailable
                    : ServiceHealthState.Unreachable;
                return false;
            }

            content = response.Content;
            health = ServiceHealthState.Normal;
            return true;
        }
        catch (WebException ex)
        {
            health = ClassifyWebException(ex);
            return false;
        }
        catch
        {
            health = ServiceHealthState.Unreachable;
            return false;
        }
    }

    private static string AddCacheBuster(string url)
    {
        return url + (url.IndexOf("?", StringComparison.Ordinal) >= 0 ? "&" : "?") +
            "t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static CodexRadarModelCatalogUpdate MergeCodexRadarModelCatalogUpdates(
        CodexRadarModelCatalogUpdate primary,
        CodexRadarModelCatalogUpdate fallback)
    {
        if (primary == null)
        {
            return fallback;
        }

        if (fallback == null)
        {
            return primary;
        }

        primary.Added.AddRange(fallback.Added);
        primary.Unavailable.AddRange(fallback.Unavailable);
        primary.Deleted.AddRange(fallback.Deleted);
        return primary;
    }

    private static string BuildCodexRadarServiceProbeReport(
        string modelKey,
        bool publicJsonEnabled,
        bool htmlFallbackEnabled,
        bool rssFallbackEnabled,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Codex Radar service probe");
        builder.AppendLine("LocalTime=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        builder.AppendLine("UtcTime=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        builder.AppendLine("ModelKey=" + CodexRadarModelCatalog.NormalizeModelKey(modelKey));
        builder.AppendLine("ConfiguredLayers=public_json:" + FormatProbeEnabled(publicJsonEnabled) +
            ", html_fallback:" + FormatProbeEnabled(htmlFallbackEnabled) +
            ", rss:" + FormatProbeEnabled(rssFallbackEnabled));
        builder.AppendLine();

        string fullApiUrl = CodexRadarFullApiUrl;
        CodexRadarProbeResponse current = ReadCodexRadarProbeEndpoint(
            AddCacheBuster(CodexRadarStatusUrl),
            "application/json,text/plain,*/*",
            cancellationToken);
        builder.AppendLine(FormatCodexRadarCurrentProbe(current, ref fullApiUrl));
        builder.AppendLine();

        CodexRadarProbeResponse fullApi = ReadCodexRadarFullApiProbeEndpoint(
            string.IsNullOrWhiteSpace(fullApiUrl) ? CodexRadarFullApiUrl : fullApiUrl,
            "application/json,text/plain,*/*",
            cancellationToken);
        builder.AppendLine(FormatCodexRadarFullApiProbe(fullApi));
        builder.AppendLine();

        CodexRadarProbeResponse home = ReadCodexRadarProbeEndpoint(
            AddCacheBuster(CodexRadarHomeUrl),
            "text/html,application/xhtml+xml,*/*",
            cancellationToken);
        builder.AppendLine(FormatCodexRadarHomeProbe(home, modelKey));
        builder.AppendLine();

        CodexRadarProbeResponse rss = ReadCodexRadarProbeEndpoint(
            AddCacheBuster(NormalizeCodexRadarFeedUrl(string.Empty)),
            "application/rss+xml,application/xml,text/xml,*/*",
            cancellationToken);
        builder.AppendLine(FormatCodexRadarRssProbe(rss));
        return builder.ToString();
    }

    private static string FormatProbeEnabled(bool enabled)
    {
        return enabled ? "on" : "off";
    }

    private static string FormatCodexRadarCurrentProbe(
        CodexRadarProbeResponse response,
        ref string fullApiUrl)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Public current.json:");
        AppendProbeTransportLine(builder, response);
        if (!response.TransportSucceeded || response.StatusCode < 200 || response.StatusCode >= 300)
        {
            return builder.ToString().TrimEnd();
        }

        try
        {
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(
                BoundedHttpTextReader.PublicJsonMaxBytes);
            Dictionary<string, object> root = serializer.DeserializeObject(response.Content ?? string.Empty) as Dictionary<string, object>;
            Dictionary<string, object> modelIq = GetQuotaObject(root, "model_iq");
            Dictionary<string, object> links = GetQuotaObject(root, "links");
            Dictionary<string, object> apiAccess = GetQuotaObject(root, "api_access");
            string discoveredFullApi = GetQuotaString(links, "full_api");
            if (!string.IsNullOrWhiteSpace(discoveredFullApi))
            {
                Uri normalizedFullApi;
                string validationError;
                if (CodexRadarUrlPolicy.TryNormalizeFullApiUrl(
                    discoveredFullApi,
                    out normalizedFullApi,
                    out validationError))
                {
                    fullApiUrl = normalizedFullApi.AbsoluteUri;
                }
            }

            builder.AppendLine("  parse=json");
            builder.AppendLine("  type=" + EmptyFallback(GetQuotaString(root, "type"), "unknown"));
            builder.AppendLine("  monitored_at=" + EmptyFallback(GetQuotaString(root, "monitored_at"), "missing"));
            builder.AppendLine("  model_iq=" + (modelIq != null ? "present" : "missing"));
            builder.AppendLine("  api_access=" + EmptyFallback(GetQuotaString(apiAccess, "status"), "missing") +
                "/" + EmptyFallback(GetQuotaString(apiAccess, "full_api_status"), "missing"));
            builder.AppendLine("  full_api=" + EmptyFallback(fullApiUrl, "missing"));
        }
        catch (Exception ex)
        {
            builder.AppendLine("  parse=failed " + ex.GetType().Name);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCodexRadarFullApiProbe(CodexRadarProbeResponse response)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Full API:");
        AppendProbeTransportLine(builder, response);
        if (response.StatusCode == 401 || response.StatusCode == 403)
        {
            builder.AppendLine("  availability=reachable_authorization_required");
        }
        else if (response.TransportSucceeded && response.StatusCode >= 200 && response.StatusCode < 300)
        {
            builder.AppendLine("  availability=reachable");
            builder.AppendLine("  model_iq=" + ((response.Content ?? string.Empty).IndexOf("model_iq", StringComparison.OrdinalIgnoreCase) >= 0 ? "present" : "missing"));
        }
        else
        {
            builder.AppendLine("  availability=unavailable");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCodexRadarHomeProbe(
        CodexRadarProbeResponse response,
        string modelKey)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Home HTML:");
        AppendProbeTransportLine(builder, response);
        if (!response.TransportSucceeded || response.StatusCode < 200 || response.StatusCode >= 300)
        {
            return builder.ToString().TrimEnd();
        }

        CodexRadarSnapshot snapshot;
        bool parsed = TryParseCodexRadarHtmlStatus(response.Content, modelKey, out snapshot);
        List<CodexRadarModelInfo> models = ExtractCodexRadarHtmlModelCatalog(response.Content);
        builder.AppendLine("  selected_model_parse=" + (parsed && snapshot != null && snapshot.ModelIqKnown ? "ok" : "failed"));
        builder.AppendLine("  discovered_models=" + models.Count.ToString(CultureInfo.InvariantCulture));
        if (parsed && snapshot != null)
        {
            builder.AppendLine("  iq=" + snapshot.ModelIqPassRatePercent.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("  data_window=" + (snapshot.ModelIqDataDateKnown
                ? snapshot.ModelIqDataDateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " +
                    (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? "pm" : "am")
                : "missing"));
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCodexRadarRssProbe(CodexRadarProbeResponse response)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("RSS feed:");
        AppendProbeTransportLine(builder, response);
        if (response.TransportSucceeded && response.StatusCode >= 200 && response.StatusCode < 300)
        {
            MatchCollection items = Regex.Matches(response.Content ?? string.Empty, "<item\\b", RegexOptions.IgnoreCase);
            builder.AppendLine("  parse=" + (((response.Content ?? string.Empty).IndexOf("<rss", StringComparison.OrdinalIgnoreCase) >= 0) ? "rss" : "unknown"));
            builder.AppendLine("  item_count=" + items.Count.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("  latest_title=" + EmptyFallback(ExtractXmlTagText(response.Content ?? string.Empty, "title"), "missing"));
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendProbeTransportLine(
        StringBuilder builder,
        CodexRadarProbeResponse response)
    {
        if (response == null)
        {
            builder.AppendLine("  transport=failed no_response");
            return;
        }

        builder.AppendLine("  transport=" + (response.TransportSucceeded ? "reachable" : "failed") +
            " http=" + response.StatusCode.ToString(CultureInfo.InvariantCulture) +
            " content_type=" + EmptyFallback(response.ContentType, "unknown") +
            (string.IsNullOrWhiteSpace(response.Error) ? string.Empty : " error=" + response.Error));
    }

    private static CodexRadarProbeResponse ReadCodexRadarProbeEndpoint(
        string url,
        string accept,
        CancellationToken cancellationToken)
    {
        CodexRadarProbeResponse result = new CodexRadarProbeResponse
        {
            Content = string.Empty,
            ContentType = string.Empty,
            Error = string.Empty
        };

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = accept;
            request.UserAgent = ProductIdentity.UserAgent;
            request.Timeout = CodexRadarStatusTimeoutMs;
            request.ReadWriteTimeout = CodexRadarStatusTimeoutMs;
            request.AllowAutoRedirect = false;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";

            int maxBytes = accept != null && accept.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0
                ? BoundedHttpTextReader.HtmlMaxBytes
                : (accept != null && (accept.IndexOf("rss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    accept.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? BoundedHttpTextReader.RssMaxBytes
                    : BoundedHttpTextReader.PublicJsonMaxBytes);
            BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
                request,
                maxBytes,
                CodexRadarStatusTimeoutMs,
                cancellationToken);
            result.TransportSucceeded = response.StatusCode > 0;
            result.StatusCode = response.StatusCode;
            result.ContentType = response.ContentType ?? string.Empty;
            result.Content = response.Content ?? string.Empty;
            result.Error = response.ErrorCode ?? string.Empty;
        }
        catch (Exception ex)
        {
            result.TransportSucceeded = false;
            result.Error = ex.GetType().Name;
        }

        return result;
    }

    private static CodexRadarProbeResponse ReadCodexRadarFullApiProbeEndpoint(
        string url,
        string accept,
        CancellationToken cancellationToken)
    {
        CodexRadarProbeResponse result;
        string errorCode;
        bool executed = CodexRadarUrlPolicy.TryExecuteFullApi<CodexRadarProbeResponse>(
            url,
            Dns.GetHostAddresses,
            delegate(Uri normalized)
            {
                return ReadCodexRadarProbeEndpoint(
                    AddCacheBuster(normalized.AbsoluteUri),
                    accept,
                    cancellationToken);
            },
            out result,
            out errorCode);
        if (executed && result != null)
        {
            return result;
        }

        return new CodexRadarProbeResponse
        {
            Content = string.Empty,
            ContentType = string.Empty,
            Error = string.IsNullOrWhiteSpace(errorCode) ? "URL_REJECTED" : errorCode,
            TransportSucceeded = false,
            StatusCode = 0
        };
    }

    private static ServiceHealthState ClassifyWebException(WebException ex)
    {
        if (ex != null &&
            ex.Status == WebExceptionStatus.ProtocolError &&
            ex.Response != null)
        {
            return ServiceHealthState.Unavailable;
        }

        return ServiceHealthState.Unreachable;
    }

    private static ServiceHealthState GetCodexRadarSnapshotHealth(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return ServiceHealthState.Unavailable;
        }

        return snapshot.ModelIqKnown
                ? ServiceHealthState.Normal
                : ServiceHealthState.Incomplete;
    }

    private static void ApplyCodexRadarWindowStatus(
        Dictionary<string, object> root,
        CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return;
        }

        Dictionary<string, object> window = GetQuotaObject(root, "window");
        bool open;
        bool openKnown = TryGetJsonBool(root, "window_open", out open) ||
            TryGetJsonBool(window, "open", out open);
        string status = GetQuotaString(window, "status");
        if (string.IsNullOrWhiteSpace(status))
        {
            status = GetQuotaString(root, "status");
        }

        if (!openKnown && !string.IsNullOrWhiteSpace(status))
        {
            open = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
            openKnown = true;
        }

        snapshot.SpeedWindowKnown = openKnown || !string.IsNullOrWhiteSpace(status);
        snapshot.SpeedWindowOpen = openKnown && open;
        snapshot.SpeedWindowStatus = status ?? string.Empty;
        snapshot.SpeedWindowEventId = BuildCodexRadarSpeedWindowEventId(root, window, snapshot);

        DateTime openedAt;
        if (TryGetQuotaDate(window, "opened_at", out openedAt))
        {
            snapshot.SpeedWindowOpenedAtLocal = openedAt;
            snapshot.SpeedWindowOpenedAtKnown = true;
        }

        DateTime closedAt;
        if (TryGetQuotaDate(window, "closed_at", out closedAt))
        {
            snapshot.SpeedWindowClosedAtLocal = closedAt;
            snapshot.SpeedWindowClosedAtKnown = true;
        }

        ExpireCodexRadarSpeedWindowIfClosed(snapshot, DateTime.Now);
    }

    private static string BuildCodexRadarSpeedWindowEventId(
        Dictionary<string, object> root,
        Dictionary<string, object> window,
        CodexRadarSnapshot snapshot)
    {
        string id = GetQuotaString(window, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }

        string sourceUrl = GetQuotaString(window, "source_url");
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl.Trim();
        }

        string status = snapshot == null ? string.Empty : snapshot.SpeedWindowStatus;
        string monitored = GetQuotaString(root, "monitored_at");
        if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(monitored))
        {
            return (status ?? string.Empty).Trim() + ":" + (monitored ?? string.Empty).Trim();
        }

        return string.Empty;
    }

    private static void ApplyCodexRadarFeedResetStatus(
        Dictionary<string, object> root,
        CodexRadarSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (root == null || snapshot == null)
        {
            return;
        }

        Dictionary<string, object> links = GetQuotaObject(root, "links");
        string rssUrl = GetQuotaString(links, "rss");
        CodexRadarResetEvent resetEvent;
        if (!TryReadCodexRadarFeedReset(rssUrl, out resetEvent, cancellationToken))
        {
            return;
        }

        snapshot.ResetEventKnown = true;
        snapshot.ResetEventId = resetEvent.Id ?? string.Empty;
        snapshot.ResetEventTitle = resetEvent.Title ?? string.Empty;
        snapshot.ResetEventUtc = resetEvent.EventUtcKnown
            ? resetEvent.EventUtc
            : DateTime.MinValue;
    }

    private static bool TryReadCodexRadarFeedReset(
        string rssUrl,
        out CodexRadarResetEvent resetEvent,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        resetEvent = null;
        string url = NormalizeCodexRadarFeedUrl(rssUrl);
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/rss+xml,application/xml,text/xml,*/*";
            request.UserAgent = ProductIdentity.UserAgent;
            request.Timeout = CodexRadarStatusTimeoutMs;
            request.ReadWriteTimeout = CodexRadarStatusTimeoutMs;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";
            BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
                request,
                BoundedHttpTextReader.RssMaxBytes,
                CodexRadarStatusTimeoutMs,
                cancellationToken);
            if (!response.Success)
            {
                return false;
            }

            return TryParseCodexRadarFeedReset(response.Content, out resetEvent);
        }
        catch
        {
            resetEvent = null;
            return false;
        }
    }

    private static string NormalizeCodexRadarFeedUrl(string rssUrl)
    {
        string value = (rssUrl ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "https://codexradar.com/feed.xml";
        }

        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            Uri baseUri = new Uri("https://codexradar.com/");
            if (!Uri.TryCreate(baseUri, value, out uri))
            {
                return string.Empty;
            }
        }

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "codexradar.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return uri.ToString();
    }

    private static bool TryParseCodexRadarFeedReset(
        string content,
        out CodexRadarResetEvent resetEvent)
    {
        resetEvent = null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        MatchCollection items = Regex.Matches(
            content,
            "<item\\b[^>]*>(.*?)</item>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i].Groups[1].Value;
            string title = ExtractXmlTagText(item, "title");
            string description = ExtractXmlTagText(item, "description");
            if (!IsCodexRadarResetFeedItem(title, description))
            {
                continue;
            }

            DateTime eventUtc = DateTime.MinValue;
            bool eventUtcKnown = false;
            string pubDate = ExtractXmlTagText(item, "pubDate");
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(
                pubDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
            {
                eventUtc = parsed.UtcDateTime;
                eventUtcKnown = true;
            }

            string guid = ExtractXmlTagText(item, "guid");
            if (string.IsNullOrWhiteSpace(guid))
            {
                guid = ExtractXmlTagText(item, "link");
            }

            resetEvent = new CodexRadarResetEvent
            {
                Id = (guid ?? string.Empty).Trim(),
                Title = (title ?? string.Empty).Trim(),
                EventUtc = eventUtc,
                EventUtcKnown = eventUtcKnown
            };
            return true;
        }

        return false;
    }

    private static bool IsCodexRadarResetFeedItem(string title, string description)
    {
        string combined = ((title ?? string.Empty) + "\n" + (description ?? string.Empty)).Trim();
        return combined.IndexOf("已重置", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("用量限制重置", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("恢复到 100", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("恢复至 100", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ExtractXmlTagText(string xml, string tagName)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(tagName))
        {
            return string.Empty;
        }

        Match match = Regex.Match(
            xml,
            "<" + Regex.Escape(tagName) + "\\b[^>]*>(.*?)</" + Regex.Escape(tagName) + ">",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return string.Empty;
        }

        string value = match.Groups[1].Value;
        value = Regex.Replace(value, "^\\s*<!\\[CDATA\\[(.*)\\]\\]>\\s*$", "$1", RegexOptions.Singleline);
        value = Regex.Replace(value, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(value).Trim();
    }

    private static bool TryParseCodexRadarStatus(
        string content,
        string modelKey,
        bool rssFallbackEnabled,
        out CodexRadarSnapshot snapshot,
        out CodexRadarModelCatalogUpdate catalogUpdate,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        snapshot = null;
        catalogUpdate = null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(
                BoundedHttpTextReader.PublicJsonMaxBytes);
            Dictionary<string, object> root = serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (root == null)
            {
                return false;
            }

            string schemaVersion;
            int schemaMajor;
            if (!TryReadCodexRadarSchemaVersion(root, out schemaVersion, out schemaMajor) || schemaMajor != 2)
            {
                Program.LogInfo(
                    "codex_radar_schema_incompatible schema_version=" +
                    (string.IsNullOrEmpty(schemaVersion) ? "missing" : schemaVersion));
                return false;
            }

            Dictionary<string, object> rootModelIq = GetQuotaObject(root, "model_iq");
            List<CodexRadarModelInfo> discoveredModels = ExtractCodexRadarModelCatalog(rootModelIq);
            catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
                discoveredModels,
                IsCodexRadarCompleteCatalog(rootModelIq, discoveredModels));
            snapshot = CodexRadarSnapshot.CreateDefault();
            snapshot.FetchedAtLocal = DateTime.Now;
            snapshot.FetchedAtKnown = true;
            snapshot.CodexIqModels = ExtractCodexIqBoardModels(rootModelIq, modelKey);

            DateTime checkedAt;
            if (TryGetQuotaDate(root, "checked_at", out checkedAt) ||
                TryGetQuotaDate(root, "monitored_at", out checkedAt))
            {
                snapshot.CheckedAtLocal = checkedAt;
                snapshot.CheckedAtKnown = true;
            }

            ApplyCodexRadarWindowStatus(root, snapshot);
            if (rssFallbackEnabled)
            {
                ApplyCodexRadarFeedResetStatus(root, snapshot, cancellationToken);
            }

            Dictionary<string, object> modelIq = SelectCodexModelIqRoot(
                rootModelIq,
                modelKey);
            if (TryApplyCodexModelIqStatus(modelIq, snapshot))
            {
                DateTime sourceUpdatedAt;
                if (TryGetQuotaDate(rootModelIq, "updated_at", out sourceUpdatedAt) ||
                    TryGetQuotaDate(modelIq, "updated_at", out sourceUpdatedAt) ||
                    (snapshot.CheckedAtKnown && TryAssignDate(snapshot.CheckedAtLocal, out sourceUpdatedAt)))
                {
                    snapshot.ModelIqSourceUpdatedAtLocal = sourceUpdatedAt;
                    snapshot.ModelIqSourceUpdatedAtKnown = true;
                }

                // The dial's first-seen marker remains a transport-time concept; the visible IQ
                // timestamp uses ModelIqSourceUpdatedAtLocal instead.
                snapshot.ModelIqRefreshedAtLocal = snapshot.FetchedAtLocal;
                snapshot.ModelIqRefreshedAtKnown = true;
                snapshot.ModelIqRefreshSucceeded = true;
            }

            ApplyCodexModelIqDisplayMaxFromSource(rootModelIq, snapshot);
            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static bool TryReadCodexRadarSchemaVersion(
        Dictionary<string, object> root,
        out string schemaVersion,
        out int schemaMajor)
    {
        schemaVersion = string.Empty;
        schemaMajor = -1;
        object raw;
        if (root == null || !root.TryGetValue("schema_version", out raw) || raw == null)
        {
            return false;
        }

        schemaVersion = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim();
        if (schemaVersion.Length == 0 || schemaVersion.Length > 32)
        {
            schemaVersion = schemaVersion.Length > 32 ? schemaVersion.Substring(0, 32) : string.Empty;
            return false;
        }

        int separator = schemaVersion.IndexOf('.');
        string majorText = separator >= 0 ? schemaVersion.Substring(0, separator) : schemaVersion;
        return int.TryParse(
            majorText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out schemaMajor);
    }

    private static bool TryAssignDate(DateTime value, out DateTime result)
    {
        result = value;
        return value != DateTime.MinValue;
    }

    private static bool TryParseCodexRadarHtmlStatus(
        string content,
        string modelKey,
        out CodexRadarSnapshot snapshot)
    {
        snapshot = null;
        if (string.IsNullOrEmpty(content) ||
            content.IndexOf("codex-radar:summary:start", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        try
        {
            string passedText = GetCodexRadarHtmlCompareValue(content, "通过数", modelKey);
            string scoreText = GetCodexRadarHtmlCompareValue(content, "IQ", modelKey);
            string durationText = GetCodexRadarHtmlCompareValue(content, "耗时", modelKey);
            string tokensText = GetCodexRadarHtmlCompareValue(content, "总tokens", modelKey);
            Match passedMatch = Regex.Match(passedText, "(\\d+)\\s*/\\s*(\\d+)");
            double score;
            double durationSeconds;
            double totalTokens;
            if (!passedMatch.Success ||
                !double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out score) ||
                !TryParseCodexRadarHtmlDurationSeconds(durationText, out durationSeconds) ||
                !TryParseCodexRadarHtmlNumber(tokensText, out totalTokens))
            {
                return false;
            }

            int passed;
            int validTasks;
            if (!int.TryParse(passedMatch.Groups[1].Value, out passed) ||
                !int.TryParse(passedMatch.Groups[2].Value, out validTasks) ||
                validTasks <= 0)
            {
                return false;
            }

            snapshot = CodexRadarSnapshot.CreateDefault();
            snapshot.CheckedAtLocal = DateTime.Now;
            snapshot.CheckedAtKnown = true;
            ApplyCodexRadarHtmlWindowStatus(content, snapshot);
            ApplyCodexRadarHtmlResetJudgement(content, snapshot);
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
            snapshot.ModelIqRefreshSucceeded = true;
            snapshot.ModelIqKnown = true;
            snapshot.ModelIqPassedKnown = true;
            int normalizedValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
            snapshot.ModelIqValidTasks = normalizedValidTasks;
            snapshot.ModelIqPassed = NormalizeCodexModelIqPassedCount(passed, validTasks);
            snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(score);
            snapshot.ModelIqEfficiencyPassed = snapshot.ModelIqPassed;
            snapshot.ModelIqEfficiencyTotalTokens = Math.Max(0.0, totalTokens);
            snapshot.ModelIqEfficiencySerialSeconds = Math.Max(0.0, durationSeconds);
            snapshot.ModelIqEfficiencyInputKnown =
                snapshot.ModelIqEfficiencyPassed > 0.0 &&
                snapshot.ModelIqEfficiencyTotalTokens > 0.0 &&
                snapshot.ModelIqEfficiencySerialSeconds > 0.0;
            ApplyCodexRadarHtmlNormalRange(content, snapshot);

            Match statusMatch = Regex.Match(
                content,
                "<section\\s+class=\"[^\"]*model-iq-([a-z]+)[^\"]*\"",
                RegexOptions.IgnoreCase);
            snapshot.ModelIqStatus = statusMatch.Success
                ? NormalizeCodexModelIqStatus(statusMatch.Groups[1].Value)
                : InferCodexModelIqStatusFromScore(snapshot.ModelIqPassRatePercent);

            ApplyCodexRadarHtmlUpdateTime(content, snapshot);
            ApplyCodexRadarHtmlDataLabel(content, modelKey, snapshot);

            snapshot.ModelIqHistory = ParseCodexRadarHtmlHistory(
                content,
                modelKey,
                snapshot.ModelIqDataDateKnown ? snapshot.ModelIqDataDateLocal : DateTime.Today,
                totalTokens);
            ApplyCodexRadarHtmlModelIqDisplayMax(content, snapshot);
            if (snapshot.ModelIqDataDateKnown)
            {
                CodexModelHistoryPoint latestPoint = new CodexModelHistoryPoint
                {
                    DateLocal = snapshot.ModelIqDataDateLocal.Date.AddHours(
                        snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0),
                    Score = snapshot.ModelIqPassRatePercent,
                    Passed = snapshot.ModelIqPassed,
                    Tasks = snapshot.ModelIqValidTasks,
                    TotalTokens = snapshot.ModelIqEfficiencyTotalTokens,
                    SerialSeconds = snapshot.ModelIqEfficiencySerialSeconds,
                    ValidityKnown = true
                };
                UpsertCodexModelHistoryPoint(snapshot.ModelIqHistory, latestPoint);
                snapshot.ModelIqHistory = NormalizeCodexModelHistory(snapshot.ModelIqHistory);
            }

            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static void ApplyCodexRadarHtmlWindowStatus(string content, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(content))
        {
            return;
        }

        string html = WebUtility.HtmlDecode(content);
        bool open =
            html.IndexOf("速蹬窗口开启", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("当前速蹬窗口开启", StringComparison.OrdinalIgnoreCase) >= 0 ||
            Regex.IsMatch(
                html,
                "window-source-kicker[^>]*>\\s*速蹬窗口开启",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (open)
        {
            snapshot.SpeedWindowKnown = true;
            snapshot.SpeedWindowOpen = true;
            snapshot.SpeedWindowStatus = "open";
        }
        else if (html.IndexOf("速蹬窗口关闭", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            snapshot.SpeedWindowKnown = true;
            snapshot.SpeedWindowOpen = false;
            snapshot.SpeedWindowStatus = "closed";
        }

        DateTime closesAt;
        DateTime opensAt;
        if (TryGetCodexRadarHtmlDateAttribute(html, "data-window-opened-at", out opensAt))
        {
            snapshot.SpeedWindowOpenedAtLocal = opensAt;
            snapshot.SpeedWindowOpenedAtKnown = true;
        }

        if (TryGetCodexRadarHtmlDateAttribute(html, "data-window-closes-at", out closesAt))
        {
            snapshot.SpeedWindowKnown = true;
            snapshot.SpeedWindowClosedAtLocal = closesAt;
            snapshot.SpeedWindowClosedAtKnown = true;
            if (string.IsNullOrWhiteSpace(snapshot.SpeedWindowStatus))
            {
                snapshot.SpeedWindowStatus = open ? "open" : "target";
            }
        }

        ExpireCodexRadarSpeedWindowIfClosed(snapshot, DateTime.Now);
    }

    private static void ApplyCodexRadarHtmlResetJudgement(string content, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(content))
        {
            return;
        }

        // Keep parsing bounded to the public Reset Radar section. This prevents unrelated page
        // labels from being mistaken for status data if the surrounding homepage evolves.
        Match sectionMatch = Regex.Match(
            content,
            "<section\\s+class=\"[^\"]*\\breset-judgement\\b[^\"]*\"[^>]*>(.*?)</section>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!sectionMatch.Success)
        {
            return;
        }

        string section = sectionMatch.Groups[1].Value;
        string resetCardStatus = string.Empty;
        string resetCardDescription = string.Empty;
        string hardResetStatus = string.Empty;
        string hardResetDescription = string.Empty;
        MatchCollection cards = Regex.Matches(
            section,
            "<article\\s+class=\"[^\"]*\\breset-judgement-card\\b[^\"]*\"[^>]*>(.*?)</article>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < cards.Count; i++)
        {
            string card = cards[i].Groups[1].Value;
            Match labelMatch = Regex.Match(
                card,
                "<span[^>]*>(.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Match judgementMatch = Regex.Match(
                card,
                "<strong[^>]*>(.*?)</strong>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!labelMatch.Success || !judgementMatch.Success)
            {
                continue;
            }

            string label = NormalizeCodexRadarHtmlText(labelMatch.Groups[1].Value);
            string status;
            string description;
            SplitCodexRadarResetJudgement(
                NormalizeCodexRadarHtmlText(judgementMatch.Groups[1].Value),
                out status,
                out description);
            if (description.Length == 0)
            {
                Match paragraphMatch = Regex.Match(
                    card,
                    "<p[^>]*>(.*?)</p>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (paragraphMatch.Success)
                {
                    description = NormalizeCodexRadarHtmlText(paragraphMatch.Groups[1].Value);
                }
            }

            status = ClampCodexRadarResetText(status, 20);
            description = ClampCodexRadarResetText(description, 96);
            if (string.Equals(label, "发重置卡", StringComparison.Ordinal))
            {
                resetCardStatus = status;
                resetCardDescription = description;
            }
            else if (string.Equals(label, "硬重置", StringComparison.Ordinal))
            {
                hardResetStatus = status;
                hardResetDescription = description;
            }
        }

        // Publish the pair atomically so the board never combines one fresh row with one stale row.
        if (resetCardStatus.Length == 0 || resetCardDescription.Length == 0 ||
            hardResetStatus.Length == 0 || hardResetDescription.Length == 0)
        {
            return;
        }

        snapshot.ResetRadarKnown = true;
        snapshot.ResetCardStatus = resetCardStatus;
        snapshot.ResetCardDescription = resetCardDescription;
        snapshot.HardResetStatus = hardResetStatus;
        snapshot.HardResetDescription = hardResetDescription;

        Match updatedMatch = Regex.Match(
            section,
            "重置雷达\\s*<em[^>]*>\\s*(\\d{1,2})月(\\d{1,2})日(\\d{1,2}):(\\d{2})更新",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        DateTime updatedLocal;
        if (updatedMatch.Success &&
            TryBuildCodexRadarResetUpdatedAtLocal(updatedMatch, out updatedLocal))
        {
            snapshot.ResetRadarUpdatedAtLocal = updatedLocal;
            snapshot.ResetRadarUpdatedAtKnown = true;
        }
    }

    private static void SplitCodexRadarResetJudgement(
        string judgement,
        out string status,
        out string description)
    {
        status = string.Empty;
        description = string.Empty;
        string value = (judgement ?? string.Empty).Trim();
        int separator = value.IndexOf('·');
        if (separator < 0)
        {
            status = value;
            return;
        }

        status = value.Substring(0, separator).Trim();
        description = value.Substring(separator + 1).Trim();
    }

    private static string ClampCodexRadarResetText(string value, int maxLength)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (maxLength <= 0 || normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, maxLength);
    }

    private static bool TryBuildCodexRadarResetUpdatedAtLocal(Match match, out DateTime updatedLocal)
    {
        updatedLocal = DateTime.MinValue;
        int month;
        int day;
        int hour;
        int minute;
        if (match == null || !match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out month) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out day) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) ||
            !int.TryParse(match.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
        {
            return false;
        }

        try
        {
            TimeZoneInfo beijingZone = TimeZoneUtilities.GetBeijingTimeZone();
            DateTime beijingNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, beijingZone);
            DateTime candidate = new DateTime(
                beijingNow.Year,
                month,
                day,
                hour,
                minute,
                0,
                DateTimeKind.Unspecified);
            // A December page observed in early January belongs to the previous year.
            if (candidate > beijingNow.AddDays(7.0))
            {
                candidate = candidate.AddYears(-1);
            }

            updatedLocal = TimeZoneInfo.ConvertTimeToUtc(candidate, beijingZone).ToLocalTime();
            return true;
        }
        catch
        {
            updatedLocal = DateTime.MinValue;
            return false;
        }
    }

    private static bool TryGetCodexRadarHtmlDateAttribute(
        string content,
        string attributeName,
        out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(attributeName))
        {
            return false;
        }

        Match match = Regex.Match(
            content,
            Regex.Escape(attributeName) + "\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }

        return TryReadQuotaDate(WebUtility.HtmlDecode(match.Groups[1].Value), out localDate);
    }

    private static string GetCodexRadarHtmlCompareValue(
        string content,
        string rowLabel,
        string modelKey)
    {
        Match rowMatch = Regex.Match(
            content,
            "<div\\s+class=\"[^\"]*\\bmodel-iq-compare-row\\b[^\"]*\"[^>]*>\\s*<span>\\s*" +
                Regex.Escape(rowLabel) +
                "\\s*</span>(.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!rowMatch.Success)
        {
            return string.Empty;
        }

        MatchCollection values = Regex.Matches(
            rowMatch.Groups[1].Value,
            "<strong\\s+class=\"([^\"]*)\"[^>]*>(.*?)</strong>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string expectedClass = "model-iq-column-" +
            CodexRadarModelCatalog.NormalizeModelKey(modelKey);

        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].Groups[1].Value.IndexOf(expectedClass, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NormalizeCodexRadarHtmlText(values[i].Groups[2].Value);
            }
        }

        return string.Empty;
    }

    private static string NormalizeCodexRadarHtmlText(string value)
    {
        string withoutTags = Regex.Replace(value ?? string.Empty, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static bool TryParseCodexRadarHtmlDurationSeconds(string value, out double seconds)
    {
        seconds = 0.0;
        string raw = value ?? string.Empty;

        // The homepage compare row currently uses compact values such as "3.4h",
        // while SVG/history titles still use "204分钟"; keep duration units isolated
        // from the generic K/M/B parser so "min" is never treated as mega.
        MatchCollection unitMatches = Regex.Matches(
            raw,
            "(-?(?:\\d+(?:\\.\\d+)?|\\.\\d+))\\s*(小时|小時|分钟|分鐘|分|秒|hours?|hrs?|hr|h|minutes?|mins?|min|m|seconds?|secs?|sec|s)",
            RegexOptions.IgnoreCase);
        bool foundUnit = false;
        for (int i = 0; i < unitMatches.Count; i++)
        {
            double amount;
            if (!double.TryParse(
                unitMatches[i].Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out amount))
            {
                continue;
            }

            double unitSeconds = GetCodexRadarHtmlDurationUnitSeconds(unitMatches[i].Groups[2].Value);
            if (unitSeconds <= 0.0)
            {
                continue;
            }

            seconds += amount * unitSeconds;
            foundUnit = true;
        }

        if (foundUnit)
        {
            seconds = Math.Max(0.0, seconds);
            return true;
        }

        Match numberMatch = Regex.Match(raw, "-?(?:\\d+(?:\\.\\d+)?|\\.\\d+)");
        double minutes;
        if (!numberMatch.Success ||
            !double.TryParse(
                numberMatch.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out minutes))
        {
            return false;
        }

        seconds = Math.Max(0.0, minutes * 60.0);
        return true;
    }

    private static double GetCodexRadarHtmlDurationUnitSeconds(string unit)
    {
        string normalized = (unit ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "小时" ||
            normalized == "小時" ||
            normalized == "h" ||
            normalized == "hr" ||
            normalized == "hrs" ||
            normalized == "hour" ||
            normalized == "hours")
        {
            return 3600.0;
        }

        if (normalized == "分钟" ||
            normalized == "分鐘" ||
            normalized == "分" ||
            normalized == "m" ||
            normalized == "min" ||
            normalized == "mins" ||
            normalized == "minute" ||
            normalized == "minutes")
        {
            return 60.0;
        }

        if (normalized == "秒" ||
            normalized == "s" ||
            normalized == "sec" ||
            normalized == "secs" ||
            normalized == "second" ||
            normalized == "seconds")
        {
            return 1.0;
        }

        return 0.0;
    }

    private static bool TryParseCodexRadarHtmlNumber(string value, out double number)
    {
        string raw = value ?? string.Empty;
        string normalized = Regex.Replace(raw, "[^0-9.\\-]", string.Empty);
        if (!double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number))
        {
            return false;
        }

        if (raw.IndexOf("亿", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            number *= 100000000.0;
        }
        else if (raw.IndexOf("万", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            number *= 10000.0;
        }
        else if (Regex.IsMatch(raw, "\\bB\\b|[0-9]\\s*B", RegexOptions.IgnoreCase))
        {
            number *= 1000000000.0;
        }
        else if (Regex.IsMatch(raw, "\\bM\\b|[0-9]\\s*M", RegexOptions.IgnoreCase))
        {
            number *= 1000000.0;
        }
        else if (Regex.IsMatch(raw, "\\bK\\b|[0-9]\\s*K", RegexOptions.IgnoreCase))
        {
            number *= 1000.0;
        }

        return true;
    }

    private static void ApplyCodexRadarHtmlUpdateTime(
        string content,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        Match timeMatch = Regex.Match(
            content ?? string.Empty,
            "<time\\s+datetime=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        DateTimeOffset updatedAt;
        if (timeMatch.Success &&
            DateTimeOffset.TryParse(
                WebUtility.HtmlDecode(timeMatch.Groups[1].Value),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out updatedAt))
        {
            DateTime updatedBeijing = TimeZoneInfo.ConvertTime(
                updatedAt,
                TimeZoneUtilities.GetBeijingTimeZone()).DateTime;
            ApplyCodexRadarHtmlUpdateTime(snapshot, updatedBeijing);
            return;
        }

        Match textMatch = Regex.Match(
            content ?? string.Empty,
            "降智雷达\\s*<span>\\s*(\\d{1,2})月(\\d{1,2})日\\s*(\\d{1,2})[:：](\\d{2})更新\\s*</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!textMatch.Success)
        {
            textMatch = Regex.Match(
                content ?? string.Empty,
                "(\\d{1,2})月(\\d{1,2})日\\s*(\\d{1,2})[:：](\\d{2})更新",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        int month;
        int day;
        int hour;
        int minute;
        DateTime date;
        DateTime beijingNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneUtilities.GetBeijingTimeZone());
        if (textMatch.Success &&
            int.TryParse(textMatch.Groups[1].Value, out month) &&
            int.TryParse(textMatch.Groups[2].Value, out day) &&
            int.TryParse(textMatch.Groups[3].Value, out hour) &&
            int.TryParse(textMatch.Groups[4].Value, out minute) &&
            TryResolveCodexRadarHistoryDate(beijingNow, month, day, out date))
        {
            ApplyCodexRadarHtmlUpdateTime(
                snapshot,
                date.Date.AddHours(Math.Max(0, Math.Min(23, hour))).AddMinutes(Math.Max(0, Math.Min(59, minute))));
        }
    }

    private static void ApplyCodexRadarHtmlUpdateTime(
        CodexRadarSnapshot snapshot,
        DateTime updatedBeijing)
    {
        snapshot.ModelIqDataDateLocal = updatedBeijing.Date;
        snapshot.ModelIqDataWindowStartHourLocal = updatedBeijing.Hour >= 12 ? 12 : 0;
        snapshot.ModelIqDataDateKnown = true;
        snapshot.ModelIqDataWindowKnown = true;
        if (!snapshot.ModelIqDataLabelKnown)
        {
            snapshot.ModelIqDataLabel = FormatCodexModelIqDataLabel(
                string.Empty,
                snapshot.ModelIqDataDateLocal,
                snapshot.ModelIqDataWindowStartHourLocal,
                snapshot.ModelIqDataWindowKnown);
            snapshot.ModelIqDataLabelKnown = snapshot.ModelIqDataLabel.Length > 0;
        }
    }

    private static void ApplyCodexRadarHtmlNormalRange(string content, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(content))
        {
            return;
        }

        MatchCollection matches = Regex.Matches(
            WebUtility.HtmlDecode(content),
            "<text[^>]*class=\"[^\"]*\\bmodel-iq-band-label\\b[^\"]*\"[^>]*>(.*?)</text>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < matches.Count; i++)
        {
            int low;
            int high;
            if (TryParseCodexModelIqNormalRangeText(
                NormalizeCodexRadarHtmlText(matches[i].Groups[1].Value),
                out low,
                out high))
            {
                ApplyCodexModelIqNormalRange(snapshot, low, high);
                return;
            }
        }
    }

    private static void ApplyCodexRadarHtmlDataLabel(
        string content,
        string modelKey,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(content))
        {
            return;
        }

        string expectedKey = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        string latestLabel = string.Empty;
        MatchCollection matches = Regex.Matches(
            WebUtility.HtmlDecode(content),
            "<title>\\s*([0-9]{1,2}\\.[0-9]{1,2}(?:_(?:am|pm)(?:_[0-9]+)?|_n)?)\\s+GPT-5\\.([0-9]+)\\s+([a-z0-9_-]+(?:\\s+[a-z0-9_-]+)?):",
            RegexOptions.IgnoreCase);
        for (int i = 0; i < matches.Count; i++)
        {
            string candidateKey = CodexRadarModelCatalog.BuildModelKey(
                "gpt-5." + matches[i].Groups[2].Value,
                Regex.Replace(matches[i].Groups[3].Value.Trim(), "\\s+", "_"),
                string.Empty);
            if (string.Equals(candidateKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                latestLabel = matches[i].Groups[1].Value;
            }
        }

        if (latestLabel.Length > 0)
        {
            snapshot.ModelIqDataLabel = latestLabel;
            snapshot.ModelIqDataLabelKnown = true;
            return;
        }

        if (snapshot.ModelIqDataDateKnown)
        {
            snapshot.ModelIqDataLabel = FormatCodexModelIqDataLabel(
                string.Empty,
                snapshot.ModelIqDataDateLocal,
                snapshot.ModelIqDataWindowStartHourLocal,
                snapshot.ModelIqDataWindowKnown);
            snapshot.ModelIqDataLabelKnown = snapshot.ModelIqDataLabel.Length > 0;
        }
    }

    private static List<CodexModelHistoryPoint> ParseCodexRadarHtmlHistory(
        string content,
        string modelKey,
        DateTime referenceDate,
        double latestTotalTokens)
    {
        List<CodexModelHistoryPoint> history = new List<CodexModelHistoryPoint>();
        MatchCollection matches = Regex.Matches(
            WebUtility.HtmlDecode(content),
            "(?:(\\d{1,2})月(\\d{1,2})日|(\\d{1,2})\\.(\\d{1,2})(?:_(am|pm|n)(?:_[0-9]+)?)?)\\s+GPT-5\\.(\\d+)\\s+([a-z0-9_-]+(?:\\s+[a-z0-9_-]+)?):\\s*" +
                "IQ指数\\s*([0-9.]+),\\s*(\\d+)\\s*/\\s*(\\d+),\\s*" +
                "费用\\s*\\$[0-9.]+,\\s*耗时\\s*(\\d+)分钟,\\s*" +
                "cache命中率\\s*([0-9.]+)%",
            RegexOptions.IgnoreCase);
        string expectedKey = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        for (int i = 0; i < matches.Count; i++)
        {
            Match match = matches[i];
            string candidateKey = CodexRadarModelCatalog.BuildModelKey(
                "gpt-5." + match.Groups[6].Value,
                Regex.Replace(match.Groups[7].Value.Trim(), "\\s+", "_"),
                string.Empty);
            if (!string.Equals(candidateKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int month;
            int day;
            int passed;
            int tasks;
            double score;
            double minutes;
            double cacheRate;
            if (!TryParseCodexRadarHtmlHistoryMonthDay(match, out month, out day) ||
                !double.TryParse(match.Groups[8].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out score) ||
                !int.TryParse(match.Groups[9].Value, out passed) ||
                !int.TryParse(match.Groups[10].Value, out tasks) ||
                !double.TryParse(match.Groups[11].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) ||
                !double.TryParse(match.Groups[12].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out cacheRate))
            {
                continue;
            }

            DateTime date;
            if (!TryResolveCodexRadarHistoryDate(referenceDate, month, day, out date))
            {
                continue;
            }

            string suffix = match.Groups[5].Value;
            int windowHour =
                string.Equals(suffix, "pm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(suffix, "n", StringComparison.OrdinalIgnoreCase)
                    ? 12
                    : 0;
            CodexModelHistoryPoint point = new CodexModelHistoryPoint
            {
                DateLocal = date.Date.AddHours(windowHour),
                Score = score,
                Passed = passed,
                Tasks = tasks,
                SerialSeconds = minutes * 60.0,
                InputTokens = 100.0,
                CachedInputTokens = cacheRate,
                CacheRateKnown = true,
                ValidityKnown = tasks > 0
            };
            if (date.Date == referenceDate.Date && latestTotalTokens > 0.0)
            {
                point.TotalTokens = latestTotalTokens;
            }

            UpsertCodexModelHistoryPoint(history, point);
        }

        return NormalizeCodexModelHistory(history);
    }

    private static bool TryParseCodexRadarHtmlHistoryMonthDay(
        Match match,
        out int month,
        out int day)
    {
        month = 0;
        day = 0;
        if (match == null || !match.Success)
        {
            return false;
        }

        string monthText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        string dayText = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
        return int.TryParse(monthText, out month) &&
            int.TryParse(dayText, out day);
    }

    private static bool TryResolveCodexRadarHistoryDate(
        DateTime referenceDate,
        int month,
        int day,
        out DateTime date)
    {
        date = DateTime.MinValue;
        int year = referenceDate.Year;
        try
        {
            DateTime candidate = new DateTime(year, month, day);
            if (candidate > referenceDate.AddMonths(6))
            {
                candidate = candidate.AddYears(-1);
            }
            else if (candidate < referenceDate.AddMonths(-6))
            {
                candidate = candidate.AddYears(1);
            }

            date = candidate.Date;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object> SelectCodexModelIqRoot(
        Dictionary<string, object> modelIq,
        string modelKey)
    {
        if (modelIq == null)
        {
            return null;
        }

        string normalizedKey = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        string latestKey = GetCodexRadarModelKeyFromNode(
            GetQuotaObject(modelIq, "latest") ?? modelIq,
            CodexRadarModelCatalog.DefaultModelKey);
        if (normalizedKey.Length == 0 ||
            string.Equals(normalizedKey, latestKey, StringComparison.OrdinalIgnoreCase))
        {
            return modelIq;
        }

        Dictionary<string, object> comparisons = GetQuotaObject(modelIq, "comparisons");
        Dictionary<string, object> direct = GetQuotaObject(comparisons, normalizedKey);
        if (direct != null)
        {
            return direct;
        }

        if (comparisons == null)
        {
            return null;
        }

        Dictionary<string, object> matched = null;
        foreach (KeyValuePair<string, object> pair in comparisons)
        {
            Dictionary<string, object> comparison = pair.Value as Dictionary<string, object>;
            if (comparison == null)
            {
                continue;
            }

            Dictionary<string, object> comparisonLatest =
                GetQuotaObject(comparison, "latest") ?? comparison;
            string comparisonKey = GetCodexRadarModelKeyFromNode(
                comparisonLatest,
                CodexRadarModelCatalog.NormalizeModelKey(pair.Key));
            if (!string.Equals(comparisonKey, normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The website may append transport/source qualifiers such as "_distributed" to the
            // dictionary key while the model node retains the stable catalog identity. Do not pick
            // an arbitrary node if a future payload maps two entries onto the same stable key.
            if (matched != null && !object.ReferenceEquals(matched, comparison))
            {
                return null;
            }

            matched = comparison;
        }

        return matched;
    }

    private static List<CodexRadarModelInfo> ExtractCodexRadarModelCatalog(
        Dictionary<string, object> modelIq)
    {
        List<CodexRadarModelInfo> models = new List<CodexRadarModelInfo>();
        if (modelIq == null)
        {
            return models;
        }

        Dictionary<string, object> latest = GetQuotaObject(modelIq, "latest") ?? modelIq;
        string latestKey = GetCodexRadarModelKeyFromNode(latest, CodexRadarModelCatalog.DefaultModelKey);
        AddCodexRadarModelInfo(models, latestKey, GetCodexRadarModelLabel(modelIq, latest, latestKey));

        Dictionary<string, object> comparisons = GetQuotaObject(modelIq, "comparisons");
        if (comparisons != null)
        {
            foreach (KeyValuePair<string, object> pair in comparisons)
            {
                Dictionary<string, object> comparison = pair.Value as Dictionary<string, object>;
                if (comparison == null)
                {
                    continue;
                }

                Dictionary<string, object> comparisonLatest =
                    GetQuotaObject(comparison, "latest") ?? comparison;
                string key = GetCodexRadarModelKeyFromNode(
                    comparisonLatest,
                    CodexRadarModelCatalog.NormalizeModelKey(pair.Key));
                AddCodexRadarModelInfo(models, key, GetCodexRadarModelLabel(comparison, comparisonLatest, key));
            }
        }

        return models;
    }

    private static List<CodexIqBoardModelPoint> ExtractCodexIqBoardModels(
        Dictionary<string, object> modelIq,
        string selectedModelKey)
    {
        List<CodexIqBoardModelPoint> points = new List<CodexIqBoardModelPoint>();
        if (modelIq == null)
        {
            return points;
        }

        string normalizedSelectedKey = CodexRadarModelCatalog.NormalizeModelKey(selectedModelKey);
        Dictionary<string, object> latest = GetQuotaObject(modelIq, "latest") ?? modelIq;
        string latestKey = GetCodexRadarModelKeyFromNode(
            latest,
            CodexRadarModelCatalog.DefaultModelKey);
        if (normalizedSelectedKey.Length == 0)
        {
            normalizedSelectedKey = latestKey;
        }

        AddCodexIqBoardModelPoint(
            points,
            modelIq,
            latest,
            latestKey,
            string.Equals(latestKey, normalizedSelectedKey, StringComparison.OrdinalIgnoreCase));

        Dictionary<string, object> comparisons = GetQuotaObject(modelIq, "comparisons");
        if (comparisons != null)
        {
            foreach (KeyValuePair<string, object> pair in comparisons)
            {
                Dictionary<string, object> comparison = pair.Value as Dictionary<string, object>;
                if (comparison == null)
                {
                    continue;
                }

                Dictionary<string, object> comparisonLatest =
                    GetQuotaObject(comparison, "latest") ?? comparison;
                string comparisonKey = GetCodexRadarModelKeyFromNode(
                    comparisonLatest,
                    CodexRadarModelCatalog.NormalizeModelKey(pair.Key));
                AddCodexIqBoardModelPoint(
                    points,
                    comparison,
                    comparisonLatest,
                    comparisonKey,
                    string.Equals(
                        comparisonKey,
                        normalizedSelectedKey,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        return points;
    }

    private static void AddCodexIqBoardModelPoint(
        List<CodexIqBoardModelPoint> points,
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        string key,
        bool current)
    {
        key = CodexRadarModelCatalog.NormalizeModelKey(key);
        if (points == null || latest == null || key.Length == 0)
        {
            return;
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (string.Equals(points[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                points[i].Current = points[i].Current || current;
                return;
            }
        }

        double score;
        if (!TryGetQuotaNumber(latest, "score", out score) &&
            !TryGetQuotaNumber(latest, "pass_rate", out score))
        {
            return;
        }

        double averageCost;
        if (!TryGetQuotaNumber(latest, "average_cost_usd", out averageCost) &&
            !TryGetQuotaNumber(latest, "avg_cost_usd", out averageCost))
        {
            averageCost = 0.0;
        }

        double averageSeconds;
        if (!TryGetQuotaNumber(latest, "average_task_seconds", out averageSeconds))
        {
            double wallSeconds;
            double validTasks;
            if (TryGetQuotaNumber(latest, "wall_seconds", out wallSeconds) &&
                (TryGetQuotaNumber(latest, "valid_tasks", out validTasks) ||
                 TryGetQuotaNumber(latest, "tasks", out validTasks)) &&
                validTasks > 0.0)
            {
                averageSeconds = wallSeconds / validTasks;
            }
            else
            {
                averageSeconds = 0.0;
            }
        }

        double totalTokens;
        if (!TryGetQuotaNumber(latest, "total_tokens", out totalTokens))
        {
            totalTokens = 0.0;
        }

        double passed;
        if (!TryGetQuotaNumber(latest, "passed", out passed))
        {
            passed = 0.0;
        }

        double tasks;
        if (!TryGetQuotaNumber(latest, "valid_tasks", out tasks) &&
            !TryGetQuotaNumber(latest, "tasks", out tasks))
        {
            tasks = 0.0;
        }

        DateTime dataLocal;
        int windowHour;
        bool dataKnown =
            TryGetCodexModelIqDataWindow(latest, "date", out dataLocal, out windowHour) ||
            TryGetCodexModelIqDataWindow(root, "date", out dataLocal, out windowHour);

        string model = GetQuotaString(latest, "model");
        string effort = GetQuotaString(latest, "reasoning_effort");
        points.Add(new CodexIqBoardModelPoint
        {
            Key = key,
            Label = GetCodexRadarModelLabel(root, latest, key),
            Family = ResolveCodexIqFamily(model, key),
            Effort = ResolveCodexIqEffort(effort, key),
            Status = NormalizeCodexModelIqStatus(GetQuotaString(latest, "status")),
            DataLocal = dataKnown ? dataLocal : DateTime.MinValue,
            DataKnown = dataKnown,
            Iq = Math.Max(0.0, score),
            AverageCostUsd = Math.Max(0.0, averageCost),
            AverageTaskSeconds = Math.Max(0.0, averageSeconds),
            TotalTokens = Math.Max(0.0, totalTokens),
            Passed = Math.Max(0.0, passed),
            ValidTasks = Math.Max(0.0, tasks),
            Current = current
        });
    }

    private static string ResolveCodexIqFamily(string model, string key)
    {
        string value = ((model ?? string.Empty) + " " + (key ?? string.Empty)).ToLowerInvariant();
        if (value.IndexOf("terra", StringComparison.Ordinal) >= 0)
        {
            return "Terra";
        }

        if (value.IndexOf("luna", StringComparison.Ordinal) >= 0)
        {
            return "Luna";
        }

        if (value.IndexOf("sol", StringComparison.Ordinal) >= 0)
        {
            return "Sol";
        }

        return "Legacy";
    }

    private static string ResolveCodexIqEffort(string effort, string key)
    {
        string normalized = (effort ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        string[] known = { "max", "xhigh", "high", "medium", "low" };
        string value = (key ?? string.Empty).ToLowerInvariant();
        for (int i = 0; i < known.Length; i++)
        {
            if (value.EndsWith("_" + known[i], StringComparison.Ordinal) ||
                value.IndexOf("_" + known[i] + "_", StringComparison.Ordinal) >= 0)
            {
                return known[i];
            }
        }

        return string.Empty;
    }

    private static bool IsCodexRadarCompleteCatalog(
        Dictionary<string, object> modelIq,
        IList<CodexRadarModelInfo> extracted)
    {
        if (modelIq == null || extracted == null || extracted.Count == 0)
        {
            return false;
        }

        int sourceModelCount = 1;
        Dictionary<string, object> comparisons = GetQuotaObject(modelIq, "comparisons");
        if (comparisons != null)
        {
            foreach (KeyValuePair<string, object> pair in comparisons)
            {
                if (pair.Value is Dictionary<string, object>)
                {
                    sourceModelCount++;
                }
            }
        }

        // Extraction de-duplicates normalized keys. Equality proves the source was readable
        // and that normalization did not collapse two distinct catalog entries.
        return extracted.Count == sourceModelCount;
    }

    private static List<CodexRadarModelInfo> ExtractCodexRadarHtmlModelCatalog(string content)
    {
        List<CodexRadarModelInfo> models = new List<CodexRadarModelInfo>();
        if (string.IsNullOrEmpty(content))
        {
            return models;
        }

        MatchCollection chips = Regex.Matches(
            content,
            "<div\\s+class=\"[^\"]*model-iq-score-chip[^\"]*\"[^>]*data-model-key=\"([^\"]+)\"[^>]*>(.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < chips.Count; i++)
        {
            string key = chips[i].Groups[1].Value;
            string label = string.Empty;
            Match labelMatch = Regex.Match(
                chips[i].Groups[2].Value,
                "<span[^>]*>(.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (labelMatch.Success)
            {
                label = NormalizeCodexRadarHtmlText(labelMatch.Groups[1].Value);
                label = Regex.Replace(label, "-(xhigh|high|medium|low)$", " $1", RegexOptions.IgnoreCase);
            }

            AddCodexRadarModelInfo(models, key, label);
        }

        if (models.Count == 0)
        {
            MatchCollection keys = Regex.Matches(
                content,
                "data-model-key=\"([^\"]+)\"",
                RegexOptions.IgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                AddCodexRadarModelInfo(models, keys[i].Groups[1].Value, string.Empty);
            }
        }

        return models;
    }

    private static void AddCodexRadarModelInfo(
        List<CodexRadarModelInfo> models,
        string key,
        string label)
    {
        key = CodexRadarModelCatalog.NormalizeModelKey(key);
        if (key.Length == 0)
        {
            return;
        }

        for (int i = 0; i < models.Count; i++)
        {
            if (string.Equals(models[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        models.Add(new CodexRadarModelInfo
        {
            Key = key,
            Label = CodexRadarModelCatalog.GetDisplayLabel(label, key),
            Available = true,
            LastSeenUtc = DateTime.UtcNow
        });
    }

    private static string GetCodexRadarModelKeyFromNode(
        Dictionary<string, object> node,
        string fallback)
    {
        if (node == null)
        {
            return CodexRadarModelCatalog.NormalizeModelKey(fallback);
        }

        return CodexRadarModelCatalog.BuildModelKey(
            GetQuotaString(node, "model"),
            GetQuotaString(node, "reasoning_effort"),
            fallback);
    }

    private static string GetCodexRadarModelLabel(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        string key)
    {
        string label = GetQuotaString(root, "label");
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        string model = GetQuotaString(latest, "model").Trim();
        string effort = GetQuotaString(latest, "reasoning_effort").Trim();
        if (model.Length > 0 || effort.Length > 0)
        {
            return (model.Length > 0 ? model.ToUpperInvariant() : string.Empty) +
                (effort.Length > 0 ? " " + effort : string.Empty);
        }

        return CodexRadarModelCatalog.GetDisplayLabel(string.Empty, key);
    }

    private static bool TryApplyCodexModelIqStatus(Dictionary<string, object> root, CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return false;
        }

        try
        {
            Dictionary<string, object> latest = GetQuotaObject(root, "latest") ?? root;
            DateTime dataDate;
            int dataWindowHour;
            string rawDataLabel = GetQuotaString(latest, "date");
            if (string.IsNullOrEmpty(rawDataLabel))
            {
                rawDataLabel = GetQuotaString(root, "date");
            }

            if (TryGetCodexModelIqDataWindow(latest, "date", out dataDate, out dataWindowHour) ||
                TryGetCodexModelIqDataWindow(root, "date", out dataDate, out dataWindowHour))
            {
                snapshot.ModelIqDataDateLocal = dataDate.Date;
                snapshot.ModelIqDataWindowStartHourLocal = dataWindowHour >= 12 ? 12 : 0;
                snapshot.ModelIqDataDateKnown = true;
                snapshot.ModelIqDataWindowKnown = true;
                snapshot.ModelIqDataLabel = FormatCodexModelIqDataLabel(
                    rawDataLabel,
                    snapshot.ModelIqDataDateLocal,
                    snapshot.ModelIqDataWindowStartHourLocal,
                    snapshot.ModelIqDataWindowKnown);
                snapshot.ModelIqDataLabelKnown = snapshot.ModelIqDataLabel.Length > 0;
            }

            string status = GetQuotaString(latest, "status");
            if (string.IsNullOrEmpty(status))
            {
                status = GetQuotaString(root, "status");
            }

            double passRate;
            bool hasPassRate =
                TryGetQuotaNumber(latest, "score", out passRate) ||
                TryGetQuotaNumber(latest, "pass_rate", out passRate) ||
                TryGetQuotaNumber(latest, "passrate", out passRate) ||
                TryGetQuotaNumber(latest, "passRate", out passRate);
            double passed;
            double validTasks;
            bool hasPassed = TryGetQuotaNumber(latest, "passed", out passed);
            bool hasValidTasks =
                TryGetQuotaNumber(latest, "valid_tasks", out validTasks) ||
                TryGetQuotaNumber(latest, "validTasks", out validTasks) ||
                TryGetQuotaNumber(latest, "tasks", out validTasks);
            if (!hasPassRate)
            {
                if (hasPassed && hasValidTasks && validTasks > 0.0)
                {
                    passRate = passed / validTasks;
                    hasPassRate = true;
                }
            }

            if (string.IsNullOrEmpty(status) && !hasPassRate)
            {
                return false;
            }

            string normalizedStatus = NormalizeCodexModelIqStatus(status);
            if (hasPassRate)
            {
                snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(passRate);
            }

            snapshot.ModelIqStatus = normalizedStatus != "invalid"
                ? normalizedStatus
                : (hasPassRate ? InferCodexModelIqStatusFromScore(snapshot.ModelIqPassRatePercent) : "invalid");

            if (hasPassed)
            {
                int validTaskCount = hasValidTasks && validTasks > 0.0
                    ? NormalizeCodexModelIqValidTaskCount(validTasks)
                    : CodexModelIqNominalTasks;
                snapshot.ModelIqValidTasks = validTaskCount;
                snapshot.ModelIqPassed = NormalizeCodexModelIqPassedCount(
                    passed,
                    hasValidTasks && validTasks > 0.0 ? validTasks : validTaskCount);
                snapshot.ModelIqPassedKnown = true;
            }
            else if (hasPassRate)
            {
                int validTaskCount = hasValidTasks && validTasks > 0.0
                    ? NormalizeCodexModelIqValidTaskCount(validTasks)
                    : CodexModelIqNominalTasks;
                snapshot.ModelIqValidTasks = validTaskCount;
                snapshot.ModelIqPassed = EstimateCodexModelIqPassedFromScore(passRate, validTaskCount);
                snapshot.ModelIqPassedKnown = true;
            }

            ApplyCodexModelIqEfficiency(root, latest, snapshot);
            ApplyCodexModelIqHistory(root, latest, snapshot);
            TryApplyCodexModelIqNormalRange(root, latest, snapshot);
            snapshot.ModelIqKnown = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryApplyCodexModelIqNormalRange(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        int low;
        int high;
        if (TryGetCodexModelIqNormalRange(latest, out low, out high) ||
            TryGetCodexModelIqNormalRange(root, out low, out high))
        {
            ApplyCodexModelIqNormalRange(snapshot, low, high);
            return true;
        }

        return false;
    }

    private static bool TryGetCodexModelIqNormalRange(Dictionary<string, object> values, out int low, out int high)
    {
        low = 0;
        high = 0;
        if (values == null)
        {
            return false;
        }

        Dictionary<string, object> range =
            GetQuotaObject(values, "normal_range") ??
            GetQuotaObject(values, "normalRange") ??
            GetQuotaObject(values, "normal_band") ??
            GetQuotaObject(values, "normalBand");
        double lowValue;
        double highValue;
        if (range != null &&
            (TryGetQuotaNumber(range, "low", out lowValue) ||
             TryGetQuotaNumber(range, "min", out lowValue) ||
             TryGetQuotaNumber(range, "start", out lowValue)) &&
            (TryGetQuotaNumber(range, "high", out highValue) ||
             TryGetQuotaNumber(range, "max", out highValue) ||
             TryGetQuotaNumber(range, "end", out highValue)))
        {
            low = (int)Math.Round(lowValue, MidpointRounding.AwayFromZero);
            high = (int)Math.Round(highValue, MidpointRounding.AwayFromZero);
            return NormalizeCodexModelIqNormalRange(ref low, ref high);
        }

        string text =
            GetQuotaString(values, "normal_range");
        if (string.IsNullOrWhiteSpace(text))
        {
            text = GetQuotaString(values, "normalRange");
        }

        return TryParseCodexModelIqNormalRangeText(text, out low, out high);
    }

    private static bool TryParseCodexModelIqNormalRangeText(string text, out int low, out int high)
    {
        low = 0;
        high = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = Regex.Match(
            WebUtility.HtmlDecode(text),
            "([0-9]+(?:\\.[0-9]+)?)\\s*[-~–—]\\s*([0-9]+(?:\\.[0-9]+)?)",
            RegexOptions.IgnoreCase);
        double lowValue;
        double highValue;
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lowValue) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out highValue))
        {
            return false;
        }

        low = (int)Math.Round(lowValue, MidpointRounding.AwayFromZero);
        high = (int)Math.Round(highValue, MidpointRounding.AwayFromZero);
        return NormalizeCodexModelIqNormalRange(ref low, ref high);
    }

    private static bool NormalizeCodexModelIqNormalRange(ref int low, ref int high)
    {
        low = Math.Max(0, Math.Min(MaxCodexModelIqScore, low));
        high = Math.Max(0, Math.Min(MaxCodexModelIqScore, high));
        if (high < low)
        {
            int temp = low;
            low = high;
            high = temp;
        }

        return high > low;
    }

    private static void ApplyCodexModelIqNormalRange(CodexRadarSnapshot snapshot, int low, int high)
    {
        if (snapshot == null || !NormalizeCodexModelIqNormalRange(ref low, ref high))
        {
            return;
        }

        snapshot.ModelIqNormalLowScore = low;
        snapshot.ModelIqNormalHighScore = high;
        snapshot.ModelIqNormalRangeKnown = true;
    }

    private static void ApplyCodexModelIqDisplayMaxFromSource(
        Dictionary<string, object> modelIqRoot,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || modelIqRoot == null)
        {
            return;
        }

        double maxScore = 0.0;
        FindCodexModelIqMaxScoreInSource(modelIqRoot, ref maxScore);
        ApplyCodexModelIqDisplayMax(snapshot, maxScore);
    }

    private static void ApplyCodexRadarHtmlModelIqDisplayMax(string content, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(content))
        {
            return;
        }

        double maxScore = 0.0;
        MatchCollection matches = Regex.Matches(
            WebUtility.HtmlDecode(content),
            "IQ指数\\s*([0-9]+(?:\\.[0-9]+)?)",
            RegexOptions.IgnoreCase);
        for (int i = 0; i < matches.Count; i++)
        {
            double score;
            if (double.TryParse(matches[i].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out score) &&
                score > maxScore)
            {
                maxScore = score;
            }
        }

        ApplyCodexModelIqDisplayMax(snapshot, maxScore);
    }

    private static void FindCodexModelIqMaxScoreInSource(object value, ref double maxScore)
    {
        Dictionary<string, object> dict = value as Dictionary<string, object>;
        if (dict != null)
        {
            double score;
            if (TryGetCodexModelIqScore(dict, out score) && score > maxScore)
            {
                maxScore = score;
            }

            foreach (KeyValuePair<string, object> pair in dict)
            {
                FindCodexModelIqMaxScoreInSource(pair.Value, ref maxScore);
            }

            return;
        }

        object[] array = value as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                FindCodexModelIqMaxScoreInSource(array[i], ref maxScore);
            }

            return;
        }

        IEnumerable<object> enumerable = value as IEnumerable<object>;
        if (enumerable != null && !(value is string))
        {
            foreach (object item in enumerable)
            {
                FindCodexModelIqMaxScoreInSource(item, ref maxScore);
            }
        }
    }

    private static void ApplyCodexModelIqDisplayMax(CodexRadarSnapshot snapshot, double maxScore)
    {
        if (snapshot == null || maxScore <= 0.0)
        {
            return;
        }

        snapshot.ModelIqDisplayMaxScore = NormalizeCodexModelIqDisplayMaxScore(maxScore);
        snapshot.ModelIqDisplayMaxScoreKnown = true;
    }

    private static string FormatCodexModelIqDataLabel(
        string rawLabel,
        DateTime localDate,
        int windowStartHour,
        bool windowKnown)
    {
        string compact = FormatCodexModelIqRawDataLabel(rawLabel);
        if (compact.Length > 0)
        {
            return compact;
        }

        if (localDate == DateTime.MinValue)
        {
            return string.Empty;
        }

        string suffix = windowKnown
            ? (windowStartHour >= 12 ? "_pm" : "_am")
            : string.Empty;
        return localDate.Month.ToString(CultureInfo.InvariantCulture) +
            "." +
            localDate.Day.ToString(CultureInfo.InvariantCulture) +
            suffix;
    }

    private static string FormatCodexModelIqRawDataLabel(string rawLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel))
        {
            return string.Empty;
        }

        string text = WebUtility.HtmlDecode(rawLabel).Trim();
        if (text.IndexOf('T') > 0)
        {
            DateTime exactLocal;
            if (TryReadQuotaDate(text, out exactLocal))
            {
                return exactLocal.ToString("M.d HH:mm", CultureInfo.InvariantCulture);
            }
        }

        Match match = Regex.Match(
            text,
            "^(\\d{4})[-/.](\\d{1,2})[-/.](\\d{1,2})(?:[-_\\s]*(.*?))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return text.Length <= 16 ? text : text.Substring(0, 16);
        }

        string suffix = NormalizeCodexModelIqDataLabelSuffix(match.Groups[4].Value);
        int month;
        int day;
        if (!int.TryParse(match.Groups[2].Value, out month) ||
            !int.TryParse(match.Groups[3].Value, out day))
        {
            return text.Length <= 16 ? text : text.Substring(0, 16);
        }

        return month.ToString(CultureInfo.InvariantCulture) +
            "." +
            day.ToString(CultureInfo.InvariantCulture) +
            suffix;
    }

    private static string NormalizeCodexModelIqDataLabelSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        string normalized = Regex.Replace(suffix.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return normalized.Length == 0 ? string.Empty : "_" + normalized;
    }

    private static void ApplyCodexModelIqHistory(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        List<CodexModelHistoryPoint> history = new List<CodexModelHistoryPoint>();
        List<Dictionary<string, object>> entries = GetQuotaObjectsFromArray(root, "recent_days");
        if (entries.Count == 0)
        {
            entries = GetQuotaObjectsFromArray(root, "history");
        }

        CodexModelHistoryPoint baselinePoint = null;
        if (entries.Count > 0)
        {
            TryReadCodexModelHistoryPoint(entries[0], out baselinePoint);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            CodexModelHistoryPoint point;
            if (TryReadCodexModelHistoryPoint(entries[i], out point))
            {
                ApplyCodexModelHistoryEfficiencies(point, baselinePoint);
                UpsertCodexModelHistoryPoint(history, point);
            }
        }

        CodexModelHistoryPoint latestPoint;
        if (TryReadCodexModelHistoryPoint(latest, out latestPoint))
        {
            ApplyCodexModelHistoryEfficiencies(latestPoint, baselinePoint);
            UpsertCodexModelHistoryPoint(history, latestPoint);
        }

        snapshot.ModelIqHistory = NormalizeCodexModelHistory(history);
    }

    private static bool TryReadCodexModelHistoryPoint(
        Dictionary<string, object> values,
        out CodexModelHistoryPoint point)
    {
        point = null;
        DateTime date;
        int dataWindowHour;
        double score;
        if (!TryGetCodexModelIqDataWindow(values, "date", out date, out dataWindowHour) ||
            !TryGetCodexModelIqScore(values, out score))
        {
            return false;
        }

        point = new CodexModelHistoryPoint
        {
            DateLocal = NormalizeCodexModelHistoryKey(date),
            Score = score
        };
        double passed;
        double totalTokens;
        double serialSeconds;
        double inputTokens;
        double cachedInputTokens;
        double tasks;
        double invalidTasks;
        TryGetQuotaNumber(values, "passed", out passed);
        TryGetModelIqTotalTokens(values, out totalTokens);
        if (!TryGetQuotaNumber(values, "serial_task_seconds", out serialSeconds) &&
            !TryGetQuotaNumber(values, "serialTaskSeconds", out serialSeconds))
        {
            TryGetQuotaNumber(values, "wall_seconds", out serialSeconds);
        }

        bool hasInput =
            TryGetQuotaNumber(values, "input_tokens", out inputTokens) ||
            TryGetQuotaNumber(values, "n_input_tokens", out inputTokens);
        bool hasCached =
            TryGetQuotaNumber(values, "cached_input_tokens", out cachedInputTokens) ||
            TryGetQuotaNumber(values, "cachedInputTokens", out cachedInputTokens);

        bool hasTasks =
            TryGetQuotaNumber(values, "tasks", out tasks) ||
            TryGetQuotaNumber(values, "valid_tasks", out tasks) ||
            TryGetQuotaNumber(values, "validTasks", out tasks);
        TryGetQuotaNumber(values, "invalid", out invalidTasks);
        point.Passed = passed;
        point.TotalTokens = totalTokens;
        point.SerialSeconds = serialSeconds;
        point.InputTokens = inputTokens;
        point.CachedInputTokens = cachedInputTokens;
        point.Tasks = tasks;
        point.InvalidTasks = invalidTasks;
        point.CacheRateKnown = hasInput && hasCached && inputTokens > 0.0;
        point.ValidityKnown = hasTasks && tasks > 0.0;
        return true;
    }

    private static void ApplyCodexModelHistoryEfficiencies(
        CodexModelHistoryPoint point,
        CodexModelHistoryPoint baseline)
    {
        if (point == null ||
            baseline == null ||
            point.Passed <= 0.0 ||
            baseline.Passed <= 0.0 ||
            point.TotalTokens <= 0.0 ||
            baseline.TotalTokens <= 0.0 ||
            point.SerialSeconds <= 0.0 ||
            baseline.SerialSeconds <= 0.0)
        {
            return;
        }

        double baselineTokenRate = baseline.Passed / baseline.TotalTokens;
        double baselineTimeRate = baseline.Passed / baseline.SerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        point.TokenEfficiencyPercent = Math.Max(
            0.0,
            Math.Min(200.0, (point.Passed / point.TotalTokens) / baselineTokenRate * 100.0));
        point.TimeEfficiencyPercent = Math.Max(
            0.0,
            Math.Min(200.0, (point.Passed / point.SerialSeconds) / baselineTimeRate * 100.0));
        point.EfficiencyKnown = true;
    }

    private static bool TryGetCodexModelIqScore(Dictionary<string, object> values, out double score)
    {
        score = 0.0;
        if (values == null)
        {
            return false;
        }

        double rawScore;
        if (TryGetQuotaNumber(values, "score", out rawScore) ||
            TryGetQuotaNumber(values, "pass_rate", out rawScore) ||
            TryGetQuotaNumber(values, "passrate", out rawScore) ||
            TryGetQuotaNumber(values, "passRate", out rawScore))
        {
            score = NormalizePassRateValue(rawScore);
            return true;
        }

        double passed;
        double validTasks;
        if (TryGetQuotaNumber(values, "passed", out passed) &&
            (TryGetQuotaNumber(values, "valid_tasks", out validTasks) ||
             TryGetQuotaNumber(values, "validTasks", out validTasks) ||
             TryGetQuotaNumber(values, "tasks", out validTasks)) &&
            validTasks > 0.0)
        {
            int normalizedValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
            int normalizedPassed = NormalizeCodexModelIqPassedCount(passed, validTasks);
            score = NormalizePassRateValue(normalizedPassed / (double)normalizedValidTasks);
            return true;
        }

        return false;
    }

    private static List<CodexModelHistoryPoint> GetRecentCodexModelHistory(CodexRadarSnapshot snapshot)
    {
        return snapshot == null
            ? new List<CodexModelHistoryPoint>()
            : NormalizeCodexModelHistory(snapshot.ModelIqHistory);
    }

    private static List<CodexModelHistoryPoint> NormalizeCodexModelHistory(
        IEnumerable<CodexModelHistoryPoint> source)
    {
        SortedDictionary<DateTime, CodexModelHistoryPoint> byDate =
            new SortedDictionary<DateTime, CodexModelHistoryPoint>();
        if (source != null)
        {
            foreach (CodexModelHistoryPoint point in source)
            {
                if (point == null || point.DateLocal == DateTime.MinValue)
                {
                    continue;
                }

                DateTime date = NormalizeCodexModelHistoryKey(point.DateLocal);
                CodexModelHistoryPoint normalized = point.Clone();
                normalized.DateLocal = date;
                normalized.Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, point.Score));
                double sourceTasks = point.Tasks > 0.0 ? point.Tasks : CodexModelIqNominalTasks;
                normalized.Tasks = point.Tasks > 0.0 ? NormalizeCodexModelIqValidTaskCount(point.Tasks) : 0.0;
                normalized.Passed = NormalizeCodexModelIqPassedValue(point.Passed, sourceTasks);
                normalized.InvalidTasks = Math.Max(
                    0.0,
                    Math.Min(CodexModelIqNominalTasks, point.InvalidTasks));
                normalized.ValidityKnown = point.ValidityKnown && normalized.Tasks > 0.0;
                normalized.TokenEfficiencyPercent = Math.Max(
                    0.0,
                    Math.Min(200.0, point.TokenEfficiencyPercent));
                normalized.TimeEfficiencyPercent = Math.Max(
                    0.0,
                    Math.Min(200.0, point.TimeEfficiencyPercent));
                byDate[date] = normalized;
            }
        }

        List<CodexModelHistoryPoint> result = new List<CodexModelHistoryPoint>(byDate.Values);
        if (result.Count > CodexModelHistoryDays)
        {
            result.RemoveRange(0, result.Count - CodexModelHistoryDays);
        }

        return result;
    }

    private static DateTime NormalizeCodexModelHistoryKey(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
    }

    private static List<CodexModelHistoryPoint> CloneCodexModelHistory(
        IEnumerable<CodexModelHistoryPoint> source)
    {
        List<CodexModelHistoryPoint> result = new List<CodexModelHistoryPoint>();
        if (source == null)
        {
            return result;
        }

        foreach (CodexModelHistoryPoint point in source)
        {
            if (point != null)
            {
                result.Add(point.Clone());
            }
        }

        return result;
    }

    private static List<CodexIqBoardModelPoint> CloneCodexIqBoardModels(
        IEnumerable<CodexIqBoardModelPoint> source)
    {
        List<CodexIqBoardModelPoint> result = new List<CodexIqBoardModelPoint>();
        if (source == null)
        {
            return result;
        }

        foreach (CodexIqBoardModelPoint point in source)
        {
            if (point != null)
            {
                result.Add(point.Clone());
            }
        }

        return result;
    }

    private static List<RadarClockModelCandidate> CloneRadarClockModelCandidates(
        IEnumerable<RadarClockModelCandidate> source)
    {
        List<RadarClockModelCandidate> result = new List<RadarClockModelCandidate>();
        if (source == null)
        {
            return result;
        }

        foreach (RadarClockModelCandidate candidate in source)
        {
            if (candidate != null)
            {
                result.Add(candidate.Clone());
            }
        }

        return result;
    }

    private static void UpsertCodexModelHistoryPoint(
        List<CodexModelHistoryPoint> history,
        DateTime date,
        double score)
    {
        if (history == null || date == DateTime.MinValue)
        {
            return;
        }

        DateTime day = NormalizeCodexModelHistoryKey(date);
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] != null && NormalizeCodexModelHistoryKey(history[i].DateLocal) == day)
            {
                history[i].Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, score));
                return;
            }
        }

        history.Add(new CodexModelHistoryPoint
        {
            DateLocal = day,
            Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, score))
        });
    }

    private static void UpsertCodexModelHistoryPoint(
        List<CodexModelHistoryPoint> history,
        CodexModelHistoryPoint point)
    {
        if (history == null || point == null || point.DateLocal == DateTime.MinValue)
        {
            return;
        }

        DateTime day = NormalizeCodexModelHistoryKey(point.DateLocal);
        CodexModelHistoryPoint normalized = point.Clone();
        normalized.DateLocal = day;
        normalized.Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, point.Score));
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] != null && NormalizeCodexModelHistoryKey(history[i].DateLocal) == day)
            {
                CodexModelHistoryPoint existing = history[i];
                if (normalized.TotalTokens <= 0.0)
                {
                    normalized.TotalTokens = existing.TotalTokens;
                }

                if (normalized.SerialSeconds <= 0.0)
                {
                    normalized.SerialSeconds = existing.SerialSeconds;
                }

                if (!normalized.CacheRateKnown && existing.CacheRateKnown)
                {
                    normalized.InputTokens = existing.InputTokens;
                    normalized.CachedInputTokens = existing.CachedInputTokens;
                    normalized.CacheRateKnown = true;
                }

                if (!normalized.EfficiencyKnown && existing.EfficiencyKnown)
                {
                    normalized.TokenEfficiencyPercent = existing.TokenEfficiencyPercent;
                    normalized.TimeEfficiencyPercent = existing.TimeEfficiencyPercent;
                    normalized.EfficiencyKnown = true;
                }

                history[i] = normalized;
                return;
            }
        }

        history.Add(normalized);
    }

    private static void MergeCodexModelIqHistory(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null)
        {
            return;
        }

        List<CodexModelHistoryPoint> merged = CloneCodexModelHistory(
            source != null ? source.ModelIqHistory : null);
        if (target.ModelIqHistory != null)
        {
            for (int i = 0; i < target.ModelIqHistory.Count; i++)
            {
                CodexModelHistoryPoint point = target.ModelIqHistory[i];
                if (point != null)
                {
                    UpsertCodexModelHistoryPoint(merged, point);
                }
            }
        }

        target.ModelIqHistory = NormalizeCodexModelHistory(merged);
    }

    private static void ApplyCodexModelIqEfficiencyFromHistory(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null ||
            snapshot.ModelIqEfficiencyKnown ||
            !snapshot.ModelIqEfficiencyInputKnown ||
            snapshot.ModelIqHistory == null)
        {
            return;
        }

        CodexModelHistoryPoint baseline = null;
        for (int i = 0; i < snapshot.ModelIqHistory.Count; i++)
        {
            CodexModelHistoryPoint point = snapshot.ModelIqHistory[i];
            if (point != null &&
                point.Passed > 0.0 &&
                point.TotalTokens > 0.0 &&
                point.SerialSeconds > 0.0)
            {
                baseline = point;
                break;
            }
        }

        if (baseline == null)
        {
            return;
        }

        double baselineTokenRate = baseline.Passed / baseline.TotalTokens;
        double baselineTimeRate = baseline.Passed / baseline.SerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round(
                (snapshot.ModelIqEfficiencyPassed / snapshot.ModelIqEfficiencyTotalTokens) /
                    baselineTokenRate * 100.0,
                MidpointRounding.AwayFromZero));
        snapshot.ModelIqTimeEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round(
                (snapshot.ModelIqEfficiencyPassed / snapshot.ModelIqEfficiencySerialSeconds) /
                    baselineTimeRate * 100.0,
                MidpointRounding.AwayFromZero));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private static void ApplyCodexModelIqEfficiency(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        CodexRadarSnapshot snapshot)
    {
        double currentPassed;
        double currentTotalTokens;
        double currentSerialSeconds;
        if (!TryReadModelIqEfficiencyInput(latest, out currentPassed, out currentTotalTokens, out currentSerialSeconds))
        {
            return;
        }

        snapshot.ModelIqEfficiencyPassed = currentPassed;
        snapshot.ModelIqEfficiencyTotalTokens = currentTotalTokens;
        snapshot.ModelIqEfficiencySerialSeconds = currentSerialSeconds;
        snapshot.ModelIqEfficiencyInputKnown = true;

        Dictionary<string, object> baseline =
            GetFirstQuotaObjectFromArray(root, "history") ??
            GetFirstQuotaObjectFromArray(root, "recent_days");
        if (baseline == null)
        {
            return;
        }

        double baselinePassed;
        double baselineTotalTokens;
        double baselineSerialSeconds;
        if (!TryReadModelIqEfficiencyInput(baseline, out baselinePassed, out baselineTotalTokens, out baselineSerialSeconds))
        {
            return;
        }

        double baselineTokenRate = baselinePassed / baselineTotalTokens;
        double baselineTimeRate = baselinePassed / baselineSerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentTotalTokens) / baselineTokenRate * 100.0, MidpointRounding.AwayFromZero));
        snapshot.ModelIqTimeEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentSerialSeconds) / baselineTimeRate * 100.0, MidpointRounding.AwayFromZero));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private static bool TryReadModelIqEfficiencyInput(
        Dictionary<string, object> values,
        out double passed,
        out double totalTokens,
        out double serialSeconds)
    {
        passed = 0.0;
        totalTokens = 0.0;
        serialSeconds = 0.0;
        if (values == null || !TryGetQuotaNumber(values, "passed", out passed) || passed <= 0.0)
        {
            return false;
        }

        if (!TryGetModelIqTotalTokens(values, out totalTokens) || totalTokens <= 0.0)
        {
            return false;
        }

        return (TryGetQuotaNumber(values, "serial_task_seconds", out serialSeconds) ||
                TryGetQuotaNumber(values, "serialTaskSeconds", out serialSeconds) ||
                TryGetQuotaNumber(values, "wall_seconds", out serialSeconds)) &&
            serialSeconds > 0.0;
    }

    private static bool TryGetModelIqTotalTokens(Dictionary<string, object> values, out double totalTokens)
    {
        totalTokens = 0.0;
        if (TryGetQuotaNumber(values, "total_tokens", out totalTokens) ||
            TryGetQuotaNumber(values, "totalTokens", out totalTokens) ||
            TryGetQuotaNumber(values, "n_total_tokens", out totalTokens))
        {
            return totalTokens > 0.0;
        }

        double inputTokens;
        double outputTokens;
        if (!TryGetQuotaNumber(values, "n_input_tokens", out inputTokens) &&
            !TryGetQuotaNumber(values, "input_tokens", out inputTokens))
        {
            return false;
        }

        if (!TryGetQuotaNumber(values, "n_output_tokens", out outputTokens) &&
            !TryGetQuotaNumber(values, "output_tokens", out outputTokens))
        {
            outputTokens = 0.0;
        }

        totalTokens = inputTokens + Math.Max(0.0, outputTokens);
        return totalTokens > 0.0;
    }

    private static string NormalizeCodexModelIqStatus(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return "invalid";
        }

        string normalized = status.Trim().ToLowerInvariant();
        if (normalized == "green" ||
            normalized == "ok" ||
            normalized == "normal" ||
            normalized == "stable")
        {
            return "green";
        }

        if (normalized == "yellow" ||
            normalized == "amber" ||
            normalized == "warning")
        {
            return "yellow";
        }

        if (normalized == "orange")
        {
            return "orange";
        }

        if (normalized == "red" ||
            normalized == "danger" ||
            normalized == "critical")
        {
            return "red";
        }

        return "invalid";
    }

    private static int NormalizePassRatePercent(double value)
    {
        return Math.Max(
            0,
            Math.Min(
                MaxCodexModelIqScore,
                (int)Math.Round(NormalizePassRateValue(value), MidpointRounding.AwayFromZero)));
    }

    private static double NormalizePassRateValue(double value)
    {
        if (value <= 1.0)
        {
            value *= CodexModelIqWebsiteScoreScale;
        }

        return Math.Max(0.0, Math.Min(MaxCodexModelIqScore, value));
    }

    private double GetQuotaActiveRefreshSeconds()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 10.0;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 30.0;
        }

        return 15.0;
    }

    private TimeSpan GetQuotaInactiveRefreshInterval()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return TimeSpan.FromMinutes(10.0);
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return TimeSpan.FromMinutes(60.0);
        }

        return TimeSpan.FromMinutes(20.0);
    }

    private bool UpdateCodexProcessRunningStatus(DateTime nowUtc, out bool changed)
    {
        if (this.lastQuotaProcessCheckUtc != DateTime.MinValue &&
            (nowUtc - this.lastQuotaProcessCheckUtc).TotalSeconds < SoftwareRuntimePresence.GetPresenceRefreshSeconds(
                WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode)))
        {
            changed = false;
            return this.quotaCodexProcessRunning;
        }

        SoftwareRuntimePresenceSnapshot presence = RefreshSoftwareRuntimePresenceSnapshot(false);
        bool running = presence.CodexRunning;
        changed = running != this.quotaCodexProcessRunning;
        this.quotaCodexProcessRunning = running;
        this.lastQuotaProcessCheckUtc = nowUtc;
        return running;
    }

    private bool IsInactiveQuotaRefreshDue(QuotaRuntimeState quotaState, DateTime nowUtc)
    {
        return quotaState == null ||
            quotaState.NextInactiveRefreshUtc == DateTime.MinValue ||
            nowUtc >= quotaState.NextInactiveRefreshUtc;
    }

    private void MarkInactiveQuotaRefresh(QuotaRuntimeState quotaState, DateTime nowUtc)
    {
        if (quotaState != null)
        {
            quotaState.NextInactiveRefreshUtc = nowUtc + GetQuotaInactiveRefreshInterval();
        }
    }

    private static bool IsQuotaResetDue(CodexQuotaSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null)
        {
            return true;
        }

        return (snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal) ||
            (snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal);
    }

    private void ShowCodexNotification(string title, string message, ToolTipIcon icon)
    {
        if (this.notificationAction == null)
        {
            return;
        }

        try
        {
            this.notificationAction(title, message, icon);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void ShowCodexRadarModelCatalogNotifications(
        CodexRadarModelCatalogUpdate update,
        OwnerOperationLease lease)
    {
        if (update == null)
        {
            return;
        }

        CodexRadarModelCatalogUpdate emitted;
        lock (this.codexRadarNotificationStateLock)
        {
            emitted = ApplyCodexRadarModelCatalogNotificationState(update, this.codexRadarNotificationState);
            if (HasCodexRadarModelCatalogNotifications(emitted))
            {
                SaveCodexRadarNotificationState();
            }
        }

        string selectedKey = CodexRadarModelCatalog.NormalizeModelKey(
            this.CurrentSettings == null ? string.Empty : this.CurrentSettings.CodexRadarModelKey);
        bool selectedUnavailable = ContainsCodexRadarModelKey(emitted.Unavailable, selectedKey);
        bool selectedDeleted = ContainsCodexRadarModelKey(emitted.Deleted, selectedKey);
        CodexRadarModelCatalogNotificationSummary summary =
            CodexRadarModelCatalog.BuildNotificationSummary(emitted);
        if (summary.TotalCount >= CodexRadarModelCatalog.ConsolidatedNotificationThreshold)
        {
            string representatives = summary.RepresentativeLabels.Count == 0
                ? string.Empty
                : "；代表模型：" + string.Join("、", summary.RepresentativeLabels.ToArray());
            string selectedNotice = selectedDeleted
                ? "；当前选中模型已删除"
                : (selectedUnavailable ? "；当前选中模型已暂不可用" : string.Empty);
            ShowCodexNotification(
                "Codex Radar 模型目录换代",
                "新增 " + summary.AddedCount.ToString(CultureInfo.InvariantCulture) +
                "，暂不可用 " + summary.UnavailableCount.ToString(CultureInfo.InvariantCulture) +
                "，删除 " + summary.DeletedCount.ToString(CultureInfo.InvariantCulture) + representatives + selectedNotice + "。",
                summary.UnavailableCount > 0 || summary.DeletedCount > 0
                    ? ToolTipIcon.Warning
                    : ToolTipIcon.Info);
            HandleSelectedCodexRadarModelCatalogChange(emitted, selectedKey, lease);
            return;
        }

        for (int i = 0; i < emitted.Added.Count; i++)
        {
            CodexRadarModelInfo model = emitted.Added[i];
            ShowCodexNotification(
                "Codex Radar 新模型",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) + " 已加入检测列表。",
                ToolTipIcon.Info);
        }

        for (int i = 0; i < emitted.Unavailable.Count; i++)
        {
            CodexRadarModelInfo model = emitted.Unavailable[i];
            ShowCodexNotification(
                "Codex Radar 模型暂不可用",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) +
                    (IsSameCodexRadarModel(model, selectedKey) ? "（当前选中模型）" : string.Empty) +
                    " 本次没有出现在网站模型列表中，暂时保留但不可选。",
                ToolTipIcon.Warning);
        }

        for (int i = 0; i < emitted.Deleted.Count; i++)
        {
            CodexRadarModelInfo model = emitted.Deleted[i];
            ShowCodexNotification(
                "Codex Radar 模型已删除",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) +
                    (IsSameCodexRadarModel(model, selectedKey) ? "（当前选中模型）" : string.Empty) +
                    " 连续多次未出现在网站模型列表中，已从检测列表移除。",
                ToolTipIcon.Warning);
        }

        HandleSelectedCodexRadarModelCatalogChange(emitted, selectedKey, lease);
    }

    private void HandleSelectedCodexRadarModelCatalogChange(
        CodexRadarModelCatalogUpdate update,
        string selectedKey,
        OwnerOperationLease lease)
    {
        if (!ContainsCodexRadarModelKey(update == null ? null : update.Deleted, selectedKey) ||
            this.CurrentSettings == null ||
            !this.CurrentSettings.RadarClockAutoSwitchModelEnabled)
        {
            return;
        }

        this.lastRadarClockAutoSwitchSignature = string.Empty;
        TryBeginInvokeOwnerCurrent(lease, delegate
        {
            ApplyRadarClockAutoSwitchIfNeeded(true);
        });
    }

    private static bool ContainsCodexRadarModelKey(
        IList<CodexRadarModelInfo> models,
        string normalizedKey)
    {
        for (int i = 0; models != null && i < models.Count; i++)
        {
            if (IsSameCodexRadarModel(models[i], normalizedKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameCodexRadarModel(CodexRadarModelInfo model, string normalizedKey)
    {
        return model != null && normalizedKey.Length > 0 &&
            string.Equals(
                CodexRadarModelCatalog.NormalizeModelKey(model.Key),
                normalizedKey,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CodexRadarNotificationStatePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "codex-radar-notification-state.ini"); }
    }

    private void LoadCodexRadarNotificationState()
    {
        lock (this.codexRadarNotificationStateLock)
        {
            this.codexRadarNotificationState.Clear();
            try
            {
                if (!File.Exists(CodexRadarNotificationStatePath))
                {
                    return;
                }

                string[] lines = File.ReadAllLines(CodexRadarNotificationStatePath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string key = NormalizeCodexRadarNotificationStateKey(line.Substring(0, equals));
                    string value = line.Substring(equals + 1).Trim();
                    if (key.Length > 0)
                    {
                        this.codexRadarNotificationState[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }
    }

    private void SaveCodexRadarNotificationState()
    {
        try
        {
            Directory.CreateDirectory(Logger.DirectoryPath);
            List<string> lines = new List<string>();
            lines.Add("# model_key=event_state");
            foreach (KeyValuePair<string, string> pair in this.codexRadarNotificationState)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    lines.Add(pair.Key.Trim() + "=" + (pair.Value ?? string.Empty).Trim());
                }
            }

            File.WriteAllLines(CodexRadarNotificationStatePath, lines.ToArray(), SharedEncoding.Utf8NoBom);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static CodexRadarModelCatalogUpdate ApplyCodexRadarModelCatalogNotificationState(
        CodexRadarModelCatalogUpdate update,
        Dictionary<string, string> state)
    {
        // Website JSON and homepage HTML can expose slightly different Codex model catalogs,
        // so a single refresh can contain conflicting
        // events for one key. Coalesce that batch first, then emit only model-key state
        // changes. This suppresses repeated Windows toasts without changing the catalog
        // or hiding real add/missing/delete transitions.
        CodexRadarModelCatalogUpdate emitted = new CodexRadarModelCatalogUpdate();
        if (update == null || state == null)
        {
            return emitted;
        }

        Dictionary<string, CodexRadarModelInfo> modelsByKey =
            new Dictionary<string, CodexRadarModelInfo>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> kindByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> statusByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> priorityByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        CollectCodexRadarModelCatalogNotifications(update.Deleted, modelsByKey, kindByKey, statusByKey, priorityByKey, "Deleted", "deleted", 1);
        CollectCodexRadarModelCatalogNotifications(update.Unavailable, modelsByKey, kindByKey, statusByKey, priorityByKey, "Unavailable", "temporarily_missing", 2);
        CollectCodexRadarModelCatalogNotifications(update.Added, modelsByKey, kindByKey, statusByKey, priorityByKey, "Added", "available", 3);

        foreach (KeyValuePair<string, CodexRadarModelInfo> pair in modelsByKey)
        {
            string key = pair.Key;
            string eventKind = kindByKey[key];
            string eventState = eventKind + "|" + statusByKey[key];
            string previous;
            if (state.TryGetValue(key, out previous) &&
                string.Equals(previous, eventState, StringComparison.Ordinal))
            {
                continue;
            }

            state[key] = eventState;
            if (string.Equals(eventKind, "Added", StringComparison.Ordinal))
            {
                emitted.Added.Add(pair.Value.Clone());
            }
            else if (string.Equals(eventKind, "Unavailable", StringComparison.Ordinal))
            {
                emitted.Unavailable.Add(pair.Value.Clone());
            }
            else if (string.Equals(eventKind, "Deleted", StringComparison.Ordinal))
            {
                emitted.Deleted.Add(pair.Value.Clone());
            }
        }

        return emitted;
    }

    private static void CollectCodexRadarModelCatalogNotifications(
        List<CodexRadarModelInfo> source,
        Dictionary<string, CodexRadarModelInfo> modelsByKey,
        Dictionary<string, string> kindByKey,
        Dictionary<string, string> statusByKey,
        Dictionary<string, int> priorityByKey,
        string eventKind,
        string status,
        int priority)
    {
        if (source == null || modelsByKey == null || kindByKey == null || statusByKey == null || priorityByKey == null)
        {
            return;
        }

        for (int i = 0; i < source.Count; i++)
        {
            CodexRadarModelInfo model = source[i];
            if (model == null)
            {
                continue;
            }

            string key = NormalizeCodexRadarNotificationStateKey(model.Key);
            if (key.Length == 0)
            {
                continue;
            }

            int existingPriority;
            if (priorityByKey.TryGetValue(key, out existingPriority) && existingPriority > priority)
            {
                continue;
            }

            priorityByKey[key] = priority;
            modelsByKey[key] = model.Clone();
            kindByKey[key] = eventKind;
            statusByKey[key] = status;
        }
    }

    private static bool HasCodexRadarModelCatalogNotifications(CodexRadarModelCatalogUpdate update)
    {
        return update != null &&
            (update.Added.Count > 0 || update.Unavailable.Count > 0 || update.Deleted.Count > 0);
    }

    private static string NormalizeCodexRadarNotificationStateKey(string key)
    {
        return CodexRadarModelCatalog.NormalizeModelKey(key);
    }

    private void HandleCodexRadarWindowAndResetEvents(CodexRadarSnapshot snapshot)
    {
        HandleCodexRadarOpenEvent(snapshot);
        HandleCodexRadarResetEvent(snapshot);
    }

    private void HandleCodexRadarOpenEvent(CodexRadarSnapshot snapshot)
    {
        if (!IsCodexRadarSpeedWindowCurrentlyOpen(snapshot, DateTime.Now))
        {
            return;
        }

        string eventId = (snapshot.SpeedWindowEventId ?? string.Empty).Trim();
        DateTime openedUtc = snapshot.SpeedWindowOpenedAtKnown
            ? snapshot.SpeedWindowOpenedAtLocal.ToUniversalTime()
            : DateTime.MinValue;
        if (eventId.Length == 0 && openedUtc == DateTime.MinValue)
        {
            return;
        }

        bool stateChanged = false;
        bool isNewOpen = false;
        lock (this.quotaResetStateLock)
        {
            bool firstOpen = this.lastRadarOpenEventId.Length == 0 &&
                this.lastRadarOpenEventUtc == DateTime.MinValue;
            bool newerOpen = openedUtc != DateTime.MinValue && openedUtc > this.lastRadarOpenEventUtc;
            bool differentIdWithoutTime = openedUtc == DateTime.MinValue &&
                eventId.Length > 0 &&
                !string.Equals(eventId, this.lastRadarOpenEventId, StringComparison.Ordinal);
            if (firstOpen || newerOpen || differentIdWithoutTime)
            {
                this.lastRadarOpenEventId = eventId;
                this.lastRadarOpenEventUtc = openedUtc;
                stateChanged = true;
                isNewOpen = true;
            }
            else if (openedUtc == this.lastRadarOpenEventUtc &&
                eventId.Length > 0 &&
                !string.Equals(eventId, this.lastRadarOpenEventId, StringComparison.Ordinal))
            {
                this.lastRadarOpenEventId = eventId;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
        }

        if (isNewOpen && AlertPresentationPolicy.ShouldPresent(
            this.CurrentSettings,
            AlertPresentationCategory.Quota))
        {
            ShowCodexNotification(
                "Codex 速蹬窗口开启",
                "检测到速蹬窗口已开启。",
                ToolTipIcon.Info);
        }
    }

    private void HandleCodexRadarResetEvent(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ResetEventKnown)
        {
            return;
        }

        string eventId = (snapshot.ResetEventId ?? string.Empty).Trim();
        DateTime eventUtc = NormalizeStateUtc(snapshot.ResetEventUtc);
        if (eventId.Length == 0 && eventUtc == DateTime.MinValue)
        {
            return;
        }

        bool stateChanged = false;
        bool isNewReset = false;
        string eventKey = GetRadarResetEventKey(eventId, eventUtc);
        lock (this.quotaResetStateLock)
        {
            bool sameEventId = eventId.Length > 0 &&
                string.Equals(eventId, this.lastRadarResetEventId, StringComparison.Ordinal);
            bool alreadyProtected = eventKey.Length > 0 &&
                string.Equals(eventKey, this.lastRadarProtectedResetEventId, StringComparison.Ordinal);
            bool newerEvent = eventUtc != DateTime.MinValue && eventUtc > this.lastRadarResetEventUtc;
            bool firstRecentEvent = this.lastRadarResetEventUtc == DateTime.MinValue &&
                eventUtc != DateTime.MinValue &&
                DateTime.UtcNow - eventUtc <= TimeSpan.FromHours(36.0);
            bool sameRecentEventNotProtected = sameEventId &&
                !alreadyProtected &&
                eventUtc != DateTime.MinValue &&
                DateTime.UtcNow - eventUtc <= TimeSpan.FromHours(36.0);
            bool differentIdWithoutTime = eventUtc == DateTime.MinValue &&
                eventId.Length > 0 &&
                !sameEventId;
            if (!alreadyProtected &&
                (!sameEventId && (newerEvent || firstRecentEvent || differentIdWithoutTime) ||
                 sameRecentEventNotProtected))
            {
                this.lastRadarResetEventId = eventId;
                this.lastRadarResetEventUtc = eventUtc;
                stateChanged = true;
                isNewReset = true;
            }
            else if (eventUtc == this.lastRadarResetEventUtc &&
                eventId.Length > 0 &&
                !sameEventId)
            {
                this.lastRadarResetEventId = eventId;
                stateChanged = true;
            }
        }

        if (isNewReset)
        {
            QuotaProtectionOptions protectionOptions = GetQuotaProtectionOptions();
            if (!protectionOptions.RssResetProtectionEnabled)
            {
                lock (this.quotaResetStateLock)
                {
                    this.lastRadarProtectedResetEventId = eventKey;
                }

                SaveQuotaResetState();
                return;
            }

            DateTime detectedUtc = DateTime.UtcNow;
            bool protectionSaved = ActivateQuotaResetProtections(
                true,
                detectedUtc,
                true,
                detectedUtc,
                "CodexRadar RSS reset event " + eventId,
                true);
            lock (this.quotaResetStateLock)
            {
                this.lastRadarProtectedResetEventId = eventKey;
            }

            if (!protectionSaved)
            {
                SaveQuotaResetState();
            }
            else
            {
                SaveQuotaResetState();
            }

            if (AlertPresentationPolicy.ShouldPresent(
                this.CurrentSettings,
                AlertPresentationCategory.ResetProtection))
            {
                ShowCodexNotification(
                    "Codex 额外重置",
                    "检测到新的 Codex 重置记录，余额已恢复至 100。",
                    ToolTipIcon.Warning);
            }
            this.codexRuntimeState.Quota.LastRefreshUtc = DateTime.MinValue;
            this.codexRuntimeState.Quota.NextInactiveRefreshUtc = DateTime.MinValue;
            this.codexRuntimeState.Touch();
            PublishProjectionStateFromOwner();
            return;
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
        }
    }

    private static string GetRadarResetEventKey(string eventId, DateTime eventUtc)
    {
        string key = (eventId ?? string.Empty).Trim();
        if (key.Length > 0)
        {
            return key;
        }

        DateTime normalized = NormalizeStateUtc(eventUtc);
        return normalized == DateTime.MinValue
            ? string.Empty
            : normalized.ToString("o", CultureInfo.InvariantCulture);
    }

    private void ActivateDueQuotaResetProtections(CodexQuotaSnapshot snapshot, DateTime nowLocal, DateTime detectedUtc)
    {
        if (snapshot == null || !GetQuotaProtectionOptions().DueResetProtectionEnabled)
        {
            return;
        }

        bool fiveHourDue = snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal;
        bool weeklyDue = snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal;
        lock (this.quotaResetStateLock)
        {
            fiveHourDue = fiveHourDue && this.fiveHourQuotaProtectionUtc == DateTime.MinValue;
            weeklyDue = weeklyDue && this.weeklyQuotaProtectionUtc == DateTime.MinValue;
        }

        if (!fiveHourDue && !weeklyDue)
        {
            return;
        }

        ActivateQuotaResetProtections(
            fiveHourDue,
            detectedUtc,
            weeklyDue,
            detectedUtc,
            "local resets_at reached",
            false);
    }

    private bool ActivateQuotaResetProtections(
        bool protectFiveHour,
        DateTime fiveHourProtectionUtc,
        bool protectWeekly,
        DateTime weeklyProtectionUtc,
        string reason,
        bool gold)
    {
        bool stateChanged = false;
        QuotaProtectionOptions protectionOptions = GetQuotaProtectionOptions();
        if (gold)
        {
            protectFiveHour = protectFiveHour && protectionOptions.RssResetProtectionEnabled;
            protectWeekly = protectWeekly && protectionOptions.RssResetProtectionEnabled;
        }
        else
        {
            protectFiveHour = protectFiveHour && protectionOptions.DueResetProtectionEnabled;
            protectWeekly = protectWeekly && protectionOptions.DueResetProtectionEnabled;
        }

        if (!protectFiveHour && !protectWeekly)
        {
            return false;
        }

        lock (this.quotaResetStateLock)
        {
            if (protectFiveHour)
            {
                DateTime normalized = NormalizeStateUtc(fiveHourProtectionUtc);
                if (normalized == DateTime.MinValue)
                {
                    normalized = DateTime.UtcNow;
                }

                if (normalized > this.fiveHourQuotaProtectionUtc)
                {
                    this.fiveHourQuotaProtectionUtc = normalized;
                    this.fiveHourQuotaProtectionGold = gold;
                    stateChanged = true;
                }
                else if (gold && !this.fiveHourQuotaProtectionGold)
                {
                    this.fiveHourQuotaProtectionGold = true;
                    stateChanged = true;
                }
            }

            if (protectWeekly)
            {
                DateTime normalized = NormalizeStateUtc(weeklyProtectionUtc);
                if (normalized == DateTime.MinValue)
                {
                    normalized = DateTime.UtcNow;
                }

                if (normalized > this.weeklyQuotaProtectionUtc)
                {
                    this.weeklyQuotaProtectionUtc = normalized;
                    this.weeklyQuotaProtectionGold = gold;
                    stateChanged = true;
                }
                else if (gold && !this.weeklyQuotaProtectionGold)
                {
                    this.weeklyQuotaProtectionGold = true;
                    stateChanged = true;
                }
            }
        }

        QuotaRuntimeState codexQuotaState = GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex);
        if (codexQuotaState.Snapshot == null)
        {
            codexQuotaState.Snapshot = CodexQuotaSnapshot.CreateDefault();
        }

        if (protectFiveHour)
        {
            ForceFiveHourQuotaToFull(codexQuotaState.Snapshot);
        }

        if (protectWeekly)
        {
            ForceWeeklyQuotaToFull(codexQuotaState.Snapshot);
        }

        this.codexRuntimeState.Touch();

        if (stateChanged)
        {
            Program.LogInfo("Quota reset protection activated. Reason=" + reason);
            SaveQuotaResetState();
        }

        return stateChanged;
    }

    private CodexQuotaSnapshot ApplyQuotaResetProtections(CodexRadarSoftwareMode family, CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            snapshot = CodexQuotaSnapshot.CreateDefault();
        }

        if (NormalizeEffectiveSoftwareMode(family) != CodexRadarSoftwareMode.Codex)
        {
            return snapshot;
        }

        bool stateChanged = false;
        bool fiveHourReleased = false;
        bool weeklyReleased = false;
        bool fiveHourDisabled = false;
        bool weeklyDisabled = false;
        QuotaProtectionOptions protectionOptions = GetQuotaProtectionOptions();
        lock (this.quotaResetStateLock)
        {
            if (this.fiveHourQuotaProtectionUtc != DateTime.MinValue)
            {
                bool enabledForSource = this.fiveHourQuotaProtectionGold
                    ? protectionOptions.RssResetProtectionEnabled
                    : protectionOptions.DueResetProtectionEnabled;
                if (!enabledForSource)
                {
                    this.fiveHourQuotaProtectionUtc = DateTime.MinValue;
                    this.fiveHourQuotaProtectionGold = false;
                    stateChanged = true;
                    fiveHourDisabled = true;
                }
                else if (IsQuotaProtectionReleaseSample(
                    snapshot,
                    this.fiveHourQuotaProtectionUtc,
                    snapshot.FiveHourResetKnown,
                    snapshot.FiveHourResetLocal))
                {
                    this.fiveHourQuotaProtectionUtc = DateTime.MinValue;
                    this.fiveHourQuotaProtectionGold = false;
                    stateChanged = true;
                    fiveHourReleased = true;
                }
                else
                {
                    ForceFiveHourQuotaToFull(snapshot);
                }
            }

            if (this.weeklyQuotaProtectionUtc != DateTime.MinValue)
            {
                bool enabledForSource = this.weeklyQuotaProtectionGold
                    ? protectionOptions.RssResetProtectionEnabled
                    : protectionOptions.DueResetProtectionEnabled;
                if (!enabledForSource)
                {
                    this.weeklyQuotaProtectionUtc = DateTime.MinValue;
                    this.weeklyQuotaProtectionGold = false;
                    stateChanged = true;
                    weeklyDisabled = true;
                }
                else if (IsQuotaProtectionReleaseSample(
                    snapshot,
                    this.weeklyQuotaProtectionUtc,
                    snapshot.WeeklyResetKnown,
                    snapshot.WeeklyResetLocal))
                {
                    this.weeklyQuotaProtectionUtc = DateTime.MinValue;
                    this.weeklyQuotaProtectionGold = false;
                    stateChanged = true;
                    weeklyReleased = true;
                }
                else
                {
                    ForceWeeklyQuotaToFull(snapshot);
                }
            }
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
            if (fiveHourReleased)
            {
                Program.LogInfo("Five-hour quota reset protection released by a newer quota sample.");
            }
            else if (fiveHourDisabled)
            {
                Program.LogInfo("Five-hour quota reset protection disabled by settings.");
            }

            if (weeklyReleased)
            {
                Program.LogInfo("Weekly quota reset protection released by a newer quota sample.");
            }
            else if (weeklyDisabled)
            {
                Program.LogInfo("Weekly quota reset protection disabled by settings.");
            }
        }

        return snapshot;
    }

    private static bool IsQuotaProtectionReleaseSample(
        CodexQuotaSnapshot snapshot,
        DateTime protectionUtc,
        bool resetKnown,
        DateTime resetLocal)
    {
        // Keep 100 visible until a post-protection sample proves the next quota window exists.
        return snapshot.SourceUpdatedKnown &&
            snapshot.SourceUpdatedUtc > protectionUtc &&
            (!resetKnown || resetLocal > DateTime.Now);
    }

    private static void ForceFiveHourQuotaToFull(CodexQuotaSnapshot snapshot)
    {
        snapshot.FiveHourPercent = 100;
        snapshot.FiveHourResetLocal = DateTime.MinValue;
        snapshot.FiveHourResetKnown = false;
    }

    private static void ForceWeeklyQuotaToFull(CodexQuotaSnapshot snapshot)
    {
        snapshot.WeeklyPercent = 100;
        snapshot.WeeklyResetLocal = DateTime.MinValue;
        snapshot.WeeklyResetKnown = false;
    }

    private static string CodexRadarCachePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "codex-radar-cache.ini"); }
    }

    private static CodexRadarSnapshot LoadCodexRadarCache(CodexRadarSoftwareMode softwareMode, string modelKey)
    {
        if (NormalizeEffectiveSoftwareMode(softwareMode) != CodexRadarSoftwareMode.Codex)
        {
            // Never hydrate retired Claude community records from a shared legacy cache.
            return null;
        }

        lock (codexRadarDiskCacheLock)
        {
            string path = CodexRadarCachePath;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                Dictionary<string, string> values = ReadSimpleKeyValueFile(path);
                string prefix = GetCodexRadarCachePrefix(modelKey);
                DateTime savedUtc;
                if (!TryReadCacheUtc(values, prefix + "SavedUtc", out savedUtc) ||
                    savedUtc == DateTime.MinValue ||
                    DateTime.UtcNow - savedUtc > TimeSpan.FromDays(CodexModelCacheRetentionDays))
                {
                    if (softwareMode != CodexRadarSoftwareMode.Codex)
                    {
                        return null;
                    }

                    prefix = GetLegacyCodexRadarCachePrefix(modelKey);
                    if (!TryReadCacheUtc(values, prefix + "SavedUtc", out savedUtc) ||
                        savedUtc == DateTime.MinValue ||
                        DateTime.UtcNow - savedUtc > TimeSpan.FromDays(CodexModelCacheRetentionDays))
                    {
                        return null;
                    }
                }

                DateTime dataDate;
                int passRate;
                if (!TryReadCacheDate(values, prefix + "DataDate", out dataDate) ||
                    !TryReadCacheInt(values, prefix + "PassRate", out passRate))
                {
                    return null;
                }

                CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
                ApplyCodexRadarCacheHardeningValues(values, prefix, snapshot);
                snapshot.ModelIqDataDateLocal = dataDate.Date;
                snapshot.ModelIqDataDateKnown = true;

                snapshot.ModelIqDataLabel = GetCacheValue(values, prefix + "DataLabel", string.Empty);
                if (string.IsNullOrWhiteSpace(snapshot.ModelIqDataLabel))
                {
                    snapshot.ModelIqDataLabel = FormatCodexModelIqDataLabel(
                        string.Empty,
                        snapshot.ModelIqDataDateLocal,
                        snapshot.ModelIqDataWindowStartHourLocal,
                        snapshot.ModelIqDataWindowKnown);
                }

                snapshot.ModelIqDataLabelKnown = snapshot.ModelIqDataLabel.Length > 0;
                snapshot.ModelIqPassRatePercent = Math.Max(
                    0,
                    Math.Min(MaxCodexModelIqScore, passRate));
                snapshot.ModelIqStatus = GetCacheValue(values, prefix + "Status", "invalid");
                int normalLow;
                int normalHigh;
                if (TryReadCacheInt(values, prefix + "NormalLow", out normalLow) &&
                    TryReadCacheInt(values, prefix + "NormalHigh", out normalHigh))
                {
                    ApplyCodexModelIqNormalRange(snapshot, normalLow, normalHigh);
                }

                double displayMaxScore;
                if (TryReadCacheDouble(values, prefix + "DisplayMaxScore", out displayMaxScore))
                {
                    ApplyCodexModelIqDisplayMax(snapshot, displayMaxScore);
                }

                int passed;
                bool cacheHasPassed = TryReadCacheInt(values, prefix + "Passed", out passed);
                if (cacheHasPassed)
                {
                    snapshot.ModelIqPassed = passed;
                }

                int validTasks;
                if (!TryReadCacheInt(values, prefix + "ValidTasks", out validTasks) ||
                    validTasks <= 0)
                {
                    validTasks = CodexModelIqNominalTasks;
                }

                snapshot.ModelIqValidTasks = NormalizeCodexModelIqValidTaskCount(validTasks);
                snapshot.ModelIqPassed = cacheHasPassed
                    ? NormalizeCodexModelIqPassedCount(snapshot.ModelIqPassed, validTasks)
                    : EstimateCodexModelIqPassedFromScore(snapshot.ModelIqPassRatePercent, snapshot.ModelIqValidTasks);
                snapshot.ModelIqPassedKnown = true;
                if (NormalizeCodexModelIqStatus(snapshot.ModelIqStatus) == "invalid")
                {
                    snapshot.ModelIqStatus = InferCodexModelIqStatusFromScore(snapshot.ModelIqPassRatePercent);
                }

                int tokenEfficiency;
                int timeEfficiency;
                if (TryReadCacheInt(values, prefix + "TokenEfficiency", out tokenEfficiency))
                {
                    snapshot.ModelIqTokenEfficiencyPercent = tokenEfficiency;
                }

                if (TryReadCacheInt(values, prefix + "TimeEfficiency", out timeEfficiency))
                {
                    snapshot.ModelIqTimeEfficiencyPercent = timeEfficiency;
                }

                snapshot.ModelIqEfficiencyKnown =
                    snapshot.ModelIqTokenEfficiencyPercent > 0 ||
                    snapshot.ModelIqTimeEfficiencyPercent > 0;
                double efficiencyPassed;
                double efficiencyTokens;
                double efficiencySeconds;
                if (TryReadCacheDouble(values, prefix + "EfficiencyPassed", out efficiencyPassed))
                {
                    snapshot.ModelIqEfficiencyPassed = efficiencyPassed;
                }

                if (TryReadCacheDouble(values, prefix + "EfficiencyTokens", out efficiencyTokens))
                {
                    snapshot.ModelIqEfficiencyTotalTokens = efficiencyTokens;
                }

                if (TryReadCacheDouble(values, prefix + "EfficiencySeconds", out efficiencySeconds))
                {
                    snapshot.ModelIqEfficiencySerialSeconds = efficiencySeconds;
                }

                snapshot.ModelIqEfficiencyInputKnown =
                    snapshot.ModelIqEfficiencyPassed > 0.0 &&
                    snapshot.ModelIqEfficiencyTotalTokens > 0.0 &&
                    snapshot.ModelIqEfficiencySerialSeconds > 0.0;

                DateTime refreshedUtc;
                if (TryReadCacheUtc(values, prefix + "RefreshedUtc", out refreshedUtc))
                {
                    snapshot.ModelIqRefreshedAtLocal = refreshedUtc.ToLocalTime();
                    snapshot.ModelIqRefreshedAtKnown = true;
                }

                snapshot.ModelIqHistory = ParseCodexModelHistory(
                    GetCacheValue(values, prefix + "History", string.Empty));
                snapshot.CodexIqModels = ParseCodexIqBoardModels(
                    GetCacheValue(values, prefix + "IqBoardModels", string.Empty));
                UpsertCodexModelHistoryPoint(
                    snapshot.ModelIqHistory,
                    snapshot.ModelIqDataDateLocal.Date.AddHours(snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0),
                    snapshot.ModelIqPassRatePercent);
                snapshot.ModelIqHistory = NormalizeCodexModelHistory(snapshot.ModelIqHistory);
                snapshot.ModelIqRefreshSucceeded = false;
                snapshot.ModelIqKnown = true;
                return snapshot;
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                return null;
            }
        }
    }

    private static void SaveCodexRadarCache(
        CodexRadarSoftwareMode softwareMode,
        string modelKey,
        CodexRadarSnapshot snapshot)
    {
        if (NormalizeEffectiveSoftwareMode(softwareMode) != CodexRadarSoftwareMode.Codex)
        {
            return;
        }

        if (snapshot == null || !snapshot.ModelIqKnown || !snapshot.ModelIqDataDateKnown)
        {
            return;
        }

        lock (codexRadarDiskCacheLock)
        {
            try
            {
                Directory.CreateDirectory(Logger.DirectoryPath);
                string path = CodexRadarCachePath;
                Dictionary<string, string> values = File.Exists(path)
                    ? ReadSimpleKeyValueFile(path)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                RemoveExpiredCodexRadarCacheModels(values);

                string prefix = GetCodexRadarCachePrefix(modelKey);
                values[prefix + "SavedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                values[prefix + "RefreshedUtc"] = snapshot.ModelIqRefreshedAtKnown
                    ? snapshot.ModelIqRefreshedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : string.Empty;
                WriteCodexRadarCacheHardeningValues(values, prefix, snapshot);
                values[prefix + "DataDate"] = snapshot.ModelIqDataDateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                values[prefix + "DataLabel"] = snapshot.ModelIqDataLabelKnown
                    ? snapshot.ModelIqDataLabel
                    : FormatCodexModelIqDataLabel(
                        string.Empty,
                        snapshot.ModelIqDataDateLocal,
                        snapshot.ModelIqDataWindowStartHourLocal,
                        snapshot.ModelIqDataWindowKnown);
                values[prefix + "Status"] = snapshot.ModelIqStatus ?? "invalid";
                values[prefix + "PassRate"] = snapshot.ModelIqPassRatePercent.ToString(CultureInfo.InvariantCulture);
                values[prefix + "NormalLow"] = snapshot.ModelIqNormalLowScore.ToString(CultureInfo.InvariantCulture);
                values[prefix + "NormalHigh"] = snapshot.ModelIqNormalHighScore.ToString(CultureInfo.InvariantCulture);
                values[prefix + "DisplayMaxScore"] = (snapshot.ModelIqDisplayMaxScoreKnown
                    ? snapshot.ModelIqDisplayMaxScore
                    : GetCodexModelIqDisplayMaxScore(snapshot, 0.0)).ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "Passed"] = snapshot.ModelIqPassed.ToString(CultureInfo.InvariantCulture);
                values[prefix + "ValidTasks"] = snapshot.ModelIqValidTasks.ToString(CultureInfo.InvariantCulture);
                values[prefix + "TokenEfficiency"] = snapshot.ModelIqTokenEfficiencyPercent.ToString(CultureInfo.InvariantCulture);
                values[prefix + "TimeEfficiency"] = snapshot.ModelIqTimeEfficiencyPercent.ToString(CultureInfo.InvariantCulture);
                values[prefix + "EfficiencyPassed"] = snapshot.ModelIqEfficiencyPassed.ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "EfficiencyTokens"] = snapshot.ModelIqEfficiencyTotalTokens.ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "EfficiencySeconds"] = snapshot.ModelIqEfficiencySerialSeconds.ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "History"] = FormatCodexModelHistory(snapshot.ModelIqHistory);
                values[prefix + "IqBoardModels"] = FormatCodexIqBoardModels(snapshot.CodexIqModels);

                string tempPath = path + ".tmp";
                List<string> lines = new List<string>();
                lines.Add("Version=1");
                foreach (KeyValuePair<string, string> pair in values)
                {
                    if (!string.Equals(pair.Key, "Version", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add(pair.Key + "=" + (pair.Value ?? string.Empty));
                    }
                }

                File.WriteAllLines(tempPath, lines.ToArray(), SharedEncoding.Utf8NoBom);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }
    }

    private static void WriteCodexRadarCacheHardeningValues(
        Dictionary<string, string> values,
        string prefix,
        CodexRadarSnapshot snapshot)
    {
        values[prefix + "ContentSignature"] = BuildCodexModelIqContentSignature(snapshot);
        values[prefix + "CheckedAtUtc"] = snapshot.CheckedAtKnown
            ? snapshot.CheckedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            : string.Empty;
        values[prefix + "FetchedAtUtc"] = snapshot.FetchedAtKnown
            ? snapshot.FetchedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            : string.Empty;
        values[prefix + "ModelIqSourceUpdatedUtc"] = snapshot.ModelIqSourceUpdatedAtKnown
            ? snapshot.ModelIqSourceUpdatedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            : string.Empty;
        values[prefix + "DataWindowHour"] = snapshot.ModelIqDataWindowKnown
            ? (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        values[prefix + "ResetRadarUpdatedAtUtc"] = snapshot.ResetRadarUpdatedAtKnown
            ? snapshot.ResetRadarUpdatedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            : string.Empty;
        values[prefix + "ResetCardStatus"] = snapshot.ResetRadarKnown ? snapshot.ResetCardStatus : string.Empty;
        values[prefix + "ResetCardDescription"] = snapshot.ResetRadarKnown ? snapshot.ResetCardDescription : string.Empty;
        values[prefix + "HardResetStatus"] = snapshot.ResetRadarKnown ? snapshot.HardResetStatus : string.Empty;
        values[prefix + "HardResetDescription"] = snapshot.ResetRadarKnown ? snapshot.HardResetDescription : string.Empty;
    }

    private static void ApplyCodexRadarCacheHardeningValues(
        Dictionary<string, string> values,
        string prefix,
        CodexRadarSnapshot snapshot)
    {
        snapshot.ModelIqCachedContentSignature =
            GetCacheValue(values, prefix + "ContentSignature", string.Empty);
        DateTime checkedAtUtc;
        if (TryReadCacheUtc(values, prefix + "CheckedAtUtc", out checkedAtUtc))
        {
            snapshot.CheckedAtLocal = checkedAtUtc.ToLocalTime();
            snapshot.CheckedAtKnown = true;
        }

        DateTime fetchedAtUtc;
        if (TryReadCacheUtc(values, prefix + "FetchedAtUtc", out fetchedAtUtc))
        {
            snapshot.FetchedAtLocal = fetchedAtUtc.ToLocalTime();
            snapshot.FetchedAtKnown = true;
        }

        DateTime modelIqSourceUpdatedUtc;
        if (TryReadCacheUtc(values, prefix + "ModelIqSourceUpdatedUtc", out modelIqSourceUpdatedUtc))
        {
            snapshot.ModelIqSourceUpdatedAtLocal = modelIqSourceUpdatedUtc.ToLocalTime();
            snapshot.ModelIqSourceUpdatedAtKnown = true;
        }

        int dataWindowHour;
        if (TryReadCacheInt(values, prefix + "DataWindowHour", out dataWindowHour))
        {
            snapshot.ModelIqDataWindowStartHourLocal = dataWindowHour >= 12 ? 12 : 0;
            snapshot.ModelIqDataWindowKnown = true;
        }

        string resetCardStatus = ClampCodexRadarResetText(
            GetCacheValue(values, prefix + "ResetCardStatus", string.Empty),
            20);
        string resetCardDescription = ClampCodexRadarResetText(
            GetCacheValue(values, prefix + "ResetCardDescription", string.Empty),
            96);
        string hardResetStatus = ClampCodexRadarResetText(
            GetCacheValue(values, prefix + "HardResetStatus", string.Empty),
            20);
        string hardResetDescription = ClampCodexRadarResetText(
            GetCacheValue(values, prefix + "HardResetDescription", string.Empty),
            96);
        if (resetCardStatus.Length > 0 && resetCardDescription.Length > 0 &&
            hardResetStatus.Length > 0 && hardResetDescription.Length > 0)
        {
            snapshot.ResetRadarKnown = true;
            snapshot.ResetCardStatus = resetCardStatus;
            snapshot.ResetCardDescription = resetCardDescription;
            snapshot.HardResetStatus = hardResetStatus;
            snapshot.HardResetDescription = hardResetDescription;
        }

        DateTime resetRadarUpdatedAtUtc;
        if (TryReadCacheUtc(values, prefix + "ResetRadarUpdatedAtUtc", out resetRadarUpdatedAtUtc))
        {
            snapshot.ResetRadarUpdatedAtLocal = resetRadarUpdatedAtUtc.ToLocalTime();
            snapshot.ResetRadarUpdatedAtKnown = true;
        }
    }

    private static Dictionary<string, string> ReadSimpleKeyValueFile(string path)
    {
        Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
        }

        return values;
    }

    private static string GetCodexRadarCachePrefix(string modelKey)
    {
        return "Codex." + GetLegacyCodexRadarCachePrefix(modelKey);
    }

    private static string GetLegacyCodexRadarCachePrefix(string modelKey)
    {
        string key = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        if (key.Length == 0)
        {
            return "Model.default.";
        }

        if (string.Equals(key, "gpt_55_medium", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt55Medium.";
        }

        if (string.Equals(key, "gpt_54_xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt54.";
        }

        if (string.Equals(key, CodexRadarModelCatalog.PreviousDefaultModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt55.";
        }

        return "Model." + key + ".";
    }

    private static void RemoveExpiredCodexRadarCacheModels(Dictionary<string, string> values)
    {
        List<string> prefixes = new List<string>();
        foreach (string key in values.Keys)
        {
            int split = key.LastIndexOf('.');
            if (split > 0)
            {
                string prefix = key.Substring(0, split + 1);
                if (!prefixes.Contains(prefix))
                {
                    prefixes.Add(prefix);
                }
            }
        }

        List<string> keys = new List<string>();
        for (int i = 0; i < prefixes.Count; i++)
        {
            string prefix = prefixes[i];
            DateTime savedUtc;
            if (TryReadCacheUtc(values, prefix + "SavedUtc", out savedUtc) &&
                DateTime.UtcNow - savedUtc <= TimeSpan.FromDays(CodexModelCacheRetentionDays))
            {
                continue;
            }

            foreach (string key in values.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }
        }

        for (int i = 0; i < keys.Count; i++)
        {
            values.Remove(keys[i]);
        }
    }

    private static string GetCacheValue(
        Dictionary<string, string> values,
        string key,
        string fallback)
    {
        string value;
        return values != null && values.TryGetValue(key, out value) ? value : fallback;
    }

    private static bool TryReadCacheUtc(
        Dictionary<string, string> values,
        string key,
        out DateTime utc)
    {
        utc = DateTime.MinValue;
        string text = GetCacheValue(values, key, string.Empty);
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    private static bool TryReadCacheDate(
        Dictionary<string, string> values,
        string key,
        out DateTime date)
    {
        return DateTime.TryParseExact(
            GetCacheValue(values, key, string.Empty),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool TryReadCacheInt(
        Dictionary<string, string> values,
        string key,
        out int number)
    {
        return int.TryParse(
            GetCacheValue(values, key, string.Empty),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static bool TryReadCacheDouble(
        Dictionary<string, string> values,
        string key,
        out double number)
    {
        return double.TryParse(
            GetCacheValue(values, key, string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static string FormatCodexIqBoardModels(IEnumerable<CodexIqBoardModelPoint> models)
    {
        StringBuilder builder = new StringBuilder();
        if (models == null)
        {
            return string.Empty;
        }

        foreach (CodexIqBoardModelPoint point in models)
        {
            if (point == null || string.IsNullOrEmpty(point.Key))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            string label = Convert.ToBase64String(Encoding.UTF8.GetBytes(point.Label ?? string.Empty));
            builder.Append(CodexRadarModelCatalog.NormalizeModelKey(point.Key)).Append(',');
            builder.Append(label).Append(',');
            builder.Append((point.Family ?? string.Empty).Replace(",", string.Empty)).Append(',');
            builder.Append((point.Effort ?? string.Empty).Replace(",", string.Empty)).Append(',');
            builder.Append((point.Status ?? string.Empty).Replace(",", string.Empty)).Append(',');
            builder.Append(point.DataKnown ? point.DataLocal.Ticks : 0L).Append(',');
            builder.Append(point.Current ? '1' : '0').Append(',');
            builder.Append(point.Iq.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.AverageCostUsd.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.AverageTaskSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.TotalTokens.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.Passed.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.ValidTasks.ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static List<CodexIqBoardModelPoint> ParseCodexIqBoardModels(string text)
    {
        List<CodexIqBoardModelPoint> models = new List<CodexIqBoardModelPoint>();
        if (string.IsNullOrEmpty(text))
        {
            return models;
        }

        string[] entries = text.Split(';');
        for (int i = 0; i < entries.Length; i++)
        {
            string[] fields = entries[i].Split(',');
            if (fields.Length != 13)
            {
                continue;
            }

            long ticks;
            double iq;
            double cost;
            double seconds;
            double tokens;
            double passed;
            double tasks;
            if (!long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                !double.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out iq) ||
                !double.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture, out cost) ||
                !double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                !double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out tokens) ||
                !double.TryParse(fields[11], NumberStyles.Float, CultureInfo.InvariantCulture, out passed) ||
                !double.TryParse(fields[12], NumberStyles.Float, CultureInfo.InvariantCulture, out tasks))
            {
                continue;
            }

            string key = CodexRadarModelCatalog.NormalizeModelKey(fields[0]);
            if (key.Length == 0)
            {
                continue;
            }

            string label;
            try
            {
                label = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
            }
            catch
            {
                label = CodexRadarModelCatalog.GetDisplayLabel(string.Empty, key);
            }

            bool dataKnown = ticks > 0L && ticks <= DateTime.MaxValue.Ticks;
            models.Add(new CodexIqBoardModelPoint
            {
                Key = key,
                Label = label,
                Family = fields[2],
                Effort = fields[3],
                Status = fields[4],
                DataLocal = dataKnown ? new DateTime(ticks, DateTimeKind.Local) : DateTime.MinValue,
                DataKnown = dataKnown,
                Current = string.Equals(fields[6], "1", StringComparison.Ordinal),
                Iq = Math.Max(0.0, iq),
                AverageCostUsd = Math.Max(0.0, cost),
                AverageTaskSeconds = Math.Max(0.0, seconds),
                TotalTokens = Math.Max(0.0, tokens),
                Passed = Math.Max(0.0, passed),
                ValidTasks = Math.Max(0.0, tasks)
            });
        }

        return models;
    }

    private static string FormatCodexModelHistory(IEnumerable<CodexModelHistoryPoint> history)
    {
        List<CodexModelHistoryPoint> points = NormalizeCodexModelHistory(history);
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < points.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            CodexModelHistoryPoint point = points[i];
            builder.Append(FormatCodexModelHistoryDate(point.DateLocal));
            builder.Append(',');
            builder.Append(point.Score.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Passed.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.TotalTokens.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.SerialSeconds.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.CachedInputTokens.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.InputTokens.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Tasks.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.InvalidTasks.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.TokenEfficiencyPercent.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.TimeEfficiencyPercent.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string FormatCodexModelHistoryDate(DateTime value)
    {
        DateTime key = NormalizeCodexModelHistoryKey(value);
        return key.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool TryParseCodexModelHistoryDate(string value, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        DateTime parsed;
        if (DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
        {
            date = NormalizeCodexModelHistoryKey(parsed);
            return true;
        }

        int windowHour;
        if (TryReadCodexModelIqDataWindow(value.Trim(), out parsed, out windowHour))
        {
            date = NormalizeCodexModelHistoryKey(parsed);
            return true;
        }

        return false;
    }

    private static List<CodexModelHistoryPoint> ParseCodexModelHistory(string text)
    {
        List<CodexModelHistoryPoint> history = new List<CodexModelHistoryPoint>();
        if (string.IsNullOrEmpty(text))
        {
            return history;
        }

        string[] entries = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            string[] fields = entries[i].Split(',');
            if (fields.Length >= 11)
            {
                DateTime richDate;
                double[] numbers = new double[10];
                bool valid = TryParseCodexModelHistoryDate(fields[0], out richDate);
                for (int field = 1; field < fields.Length && field <= numbers.Length; field++)
                {
                    double number;
                    valid &= double.TryParse(
                        fields[field],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out number);
                    numbers[field - 1] = number;
                }

                if (valid)
                {
                    UpsertCodexModelHistoryPoint(
                        history,
                        new CodexModelHistoryPoint
                        {
                            DateLocal = richDate,
                            Score = numbers[0],
                            Passed = numbers[1],
                            TotalTokens = numbers[2],
                            SerialSeconds = numbers[3],
                            CachedInputTokens = numbers[4],
                            InputTokens = numbers[5],
                            Tasks = numbers[6],
                            InvalidTasks = numbers[7],
                            TokenEfficiencyPercent = numbers[8],
                            TimeEfficiencyPercent = numbers[9],
                            EfficiencyKnown = numbers[8] > 0.0 || numbers[9] > 0.0,
                            CacheRateKnown = numbers[5] > 0.0,
                            ValidityKnown = numbers[6] > 0.0
                        });
                    continue;
                }
            }

            int split = entries[i].LastIndexOf(':');
            DateTime date;
            double score;
            if (split > 0 &&
                TryParseCodexModelHistoryDate(entries[i].Substring(0, split), out date) &&
                double.TryParse(
                    entries[i].Substring(split + 1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out score))
            {
                UpsertCodexModelHistoryPoint(history, date, score);
            }
        }

        return NormalizeCodexModelHistory(history);
    }

    private static string QuotaResetStatePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "quota-reset-state.ini"); }
    }

    private void LoadQuotaResetState()
    {
        string path = QuotaResetStatePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            lock (this.quotaResetStateLock)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    bool boolValue;
                    DateTime utcValue;
                    if ((string.Equals(key, "LastRadarResetEventId", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "LastRadarEventId", StringComparison.OrdinalIgnoreCase)) &&
                        value.Length > 0)
                    {
                        this.lastRadarResetEventId = value;
                    }
                    else if ((string.Equals(key, "LastRadarResetEventUtc", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(key, "LastRadarEventClosedUtc", StringComparison.OrdinalIgnoreCase)) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.lastRadarResetEventUtc = utcValue;
                    }
                    else if (string.Equals(key, "LastRadarProtectedResetEventId", StringComparison.OrdinalIgnoreCase) &&
                        value.Length > 0)
                    {
                        this.lastRadarProtectedResetEventId = value;
                    }
                    else if (string.Equals(key, "LastRadarOpenEventId", StringComparison.OrdinalIgnoreCase) &&
                        value.Length > 0)
                    {
                        this.lastRadarOpenEventId = value;
                    }
                    else if (string.Equals(key, "LastRadarOpenEventUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.lastRadarOpenEventUtc = utcValue;
                    }
                    else if (string.Equals(key, "FiveHourProtectionUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.fiveHourQuotaProtectionUtc = utcValue;
                    }
                    else if (string.Equals(key, "WeeklyProtectionUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.weeklyQuotaProtectionUtc = utcValue;
                    }
                    else if (string.Equals(key, "FiveHourProtectionGold", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.fiveHourQuotaProtectionGold = boolValue;
                    }
                    else if (string.Equals(key, "WeeklyProtectionGold", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.weeklyQuotaProtectionGold = boolValue;
                    }
                }

                if (this.fiveHourQuotaProtectionUtc == DateTime.MinValue)
                {
                    this.fiveHourQuotaProtectionGold = false;
                }

                if (this.weeklyQuotaProtectionUtc == DateTime.MinValue)
                {
                    this.weeklyQuotaProtectionGold = false;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void SaveQuotaResetState()
    {
        try
        {
            lock (this.quotaResetStateLock)
            {
                Directory.CreateDirectory(Logger.DirectoryPath);
                string resetEventId = SanitizeStateValue(this.lastRadarResetEventId);
                string protectedResetEventId = SanitizeStateValue(this.lastRadarProtectedResetEventId);
                string openEventId = SanitizeStateValue(this.lastRadarOpenEventId);
                File.WriteAllLines(
                    QuotaResetStatePath,
                    new[]
                    {
                        "Version=5",
                        "LastRadarResetEventId=" + resetEventId,
                        "LastRadarResetEventUtc=" + FormatStateUtc(this.lastRadarResetEventUtc),
                        "LastRadarProtectedResetEventId=" + protectedResetEventId,
                        "LastRadarOpenEventId=" + openEventId,
                        "LastRadarOpenEventUtc=" + FormatStateUtc(this.lastRadarOpenEventUtc),
                        "FiveHourProtectionUtc=" + FormatStateUtc(this.fiveHourQuotaProtectionUtc),
                        "WeeklyProtectionUtc=" + FormatStateUtc(this.weeklyQuotaProtectionUtc),
                        "FiveHourProtectionGold=" + this.fiveHourQuotaProtectionGold.ToString(CultureInfo.InvariantCulture),
                        "WeeklyProtectionGold=" + this.weeklyQuotaProtectionGold.ToString(CultureInfo.InvariantCulture)
                    },
                    Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static string SanitizeStateValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();
    }

    private static string FormatStateUtc(DateTime value)
    {
        DateTime normalized = NormalizeStateUtc(value);
        return normalized == DateTime.MinValue
            ? string.Empty
            : normalized.ToString("o", CultureInfo.InvariantCulture);
    }

    private static bool TryParseStateUtc(string text, out DateTime value)
    {
        value = DateTime.MinValue;
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed))
        {
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }

    private static DateTime NormalizeStateUtc(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private CodexQuotaSnapshot ReadQuotaSnapshot(out bool sourceKnown, out string sourceKind)
    {
        CodexQuotaSnapshot snapshot;
        if (TryGetCodexProviderQuotaSnapshot(out snapshot))
        {
            sourceKnown = true;
            sourceKind = "provider";
            MarkQuotaSnapshotSource(snapshot, sourceKind);
            return NormalizeQuotaSnapshot(snapshot);
        }

        if (TryReadCodexSessionQuota(out snapshot))
        {
            sourceKnown = true;
            sourceKind = "session";
            MarkQuotaSnapshotSource(snapshot, sourceKind);
            snapshot = NormalizeQuotaSnapshot(snapshot);
            TryWriteQuotaIniSnapshot(snapshot);
            return snapshot;
        }

        if (TryReadQuotaIniSnapshot(out snapshot))
        {
            sourceKnown = true;
            sourceKind = "cache";
            MarkQuotaSnapshotSource(snapshot, sourceKind);
            return NormalizeQuotaSnapshot(snapshot);
        }

        sourceKnown = false;
        sourceKind = "default";
        return CodexQuotaSnapshot.CreateDefault();
    }

    private static CodexQuotaSnapshot NormalizeQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return CodexQuotaSnapshot.CreateDefault();
        }

        snapshot.FiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
        snapshot.WeeklyPercent = ClampPercent(snapshot.WeeklyPercent);
        return snapshot;
    }

    private static void MarkQuotaSnapshotSource(CodexQuotaSnapshot snapshot, string sourceKind)
    {
        if (snapshot == null)
        {
            return;
        }

        snapshot.SourceKind = string.IsNullOrWhiteSpace(sourceKind)
            ? "unknown"
            : sourceKind.Trim();
    }

    private static void SetQuotaUsageDiagnostics(
        CodexQuotaSnapshot snapshot,
        bool fiveHour,
        string sourceKind,
        string fieldName,
        double rawValue,
        double normalizedUsedPercent)
    {
        if (snapshot == null)
        {
            return;
        }

        MarkQuotaSnapshotSource(snapshot, sourceKind);
        if (fiveHour)
        {
            snapshot.FiveHourUsedFieldName = fieldName ?? string.Empty;
            snapshot.FiveHourRawUsedValue = rawValue;
            snapshot.FiveHourNormalizedUsedPercent = normalizedUsedPercent;
            snapshot.FiveHourUsageDiagnosticKnown = true;
        }
        else
        {
            snapshot.WeeklyUsedFieldName = fieldName ?? string.Empty;
            snapshot.WeeklyRawUsedValue = rawValue;
            snapshot.WeeklyNormalizedUsedPercent = normalizedUsedPercent;
            snapshot.WeeklyUsageDiagnosticKnown = true;
        }
    }

    private bool ShouldRejectSuspiciousProviderQuotaSnapshot(CodexQuotaSnapshot incoming, out string reason)
    {
        reason = string.Empty;
        if (incoming == null ||
            !string.Equals(incoming.SourceKind, "provider", StringComparison.OrdinalIgnoreCase) ||
            !this.quotaSourceKnown ||
            this.quotaSnapshot == null)
        {
            return false;
        }

        CodexQuotaSnapshot current = this.quotaSnapshot;
        QuotaProtectionOptions protectionOptions = GetQuotaProtectionOptions();
        if (protectionOptions.ProviderZeroDropProtectionEnabled &&
            IsSuspiciousProviderZeroDrop(
            current.FiveHourPercent,
            incoming.FiveHourPercent,
            current.FiveHourResetKnown ? current.FiveHourResetLocal : DateTime.MinValue,
            incoming.FiveHourResetKnown ? incoming.FiveHourResetLocal : DateTime.MinValue,
            TimeSpan.FromMinutes(30.0)))
        {
            reason = "provider_five_hour_zero_drop_ignored_keep_previous_snapshot";
            return true;
        }

        if (protectionOptions.ProviderZeroDropProtectionEnabled &&
            IsSuspiciousProviderZeroDrop(
            current.WeeklyPercent,
            incoming.WeeklyPercent,
            current.WeeklyResetKnown ? current.WeeklyResetLocal : DateTime.MinValue,
            incoming.WeeklyResetKnown ? incoming.WeeklyResetLocal : DateTime.MinValue,
            TimeSpan.FromHours(6.0)))
        {
            reason = "provider_weekly_zero_drop_ignored_keep_previous_snapshot";
            return true;
        }

        DateTime nowLocal = DateTime.Now;
        if (protectionOptions.ProviderFiveHourEarlyResetSpikeProtectionEnabled &&
            IsSuspiciousProviderFiveHourEarlyResetSpike(
            current.FiveHourPercent,
            incoming.FiveHourPercent,
            current.FiveHourResetKnown ? current.FiveHourResetLocal : DateTime.MinValue,
            incoming.FiveHourResetKnown ? incoming.FiveHourResetLocal : DateTime.MinValue,
            nowLocal,
            TimeSpan.FromMinutes(30.0)))
        {
            reason = "provider_five_hour_early_reset_spike_ignored_keep_previous_snapshot";
            return true;
        }

        if (protectionOptions.ProviderWeeklySpikeProtectionEnabled &&
            IsSuspiciousProviderWeeklySpike(
            current.WeeklyPercent,
            incoming.WeeklyPercent,
            current.WeeklyResetKnown ? current.WeeklyResetLocal : DateTime.MinValue,
            incoming.WeeklyResetKnown ? incoming.WeeklyResetLocal : DateTime.MinValue,
            TimeSpan.FromHours(6.0),
            nowLocal,
            TimeSpan.FromMinutes(30.0)))
        {
            reason = "provider_weekly_spike_ignored_keep_previous_snapshot";
            return true;
        }

        return false;
    }

    private static bool IsSuspiciousProviderZeroDrop(
        int previousBalancePercent,
        int incomingBalancePercent,
        DateTime previousResetLocal,
        DateTime incomingResetLocal,
        TimeSpan materialResetAdvance)
    {
        previousBalancePercent = ClampPercent(previousBalancePercent);
        incomingBalancePercent = ClampPercent(incomingBalancePercent);
        if (incomingBalancePercent != 0 || previousBalancePercent < 20)
        {
            return false;
        }

        if (previousResetLocal == DateTime.MinValue ||
            incomingResetLocal == DateTime.MinValue)
        {
            return true;
        }

        return incomingResetLocal <= previousResetLocal.Add(materialResetAdvance);
    }

    private static bool IsSuspiciousProviderWeeklySpike(
        int previousBalancePercent,
        int incomingBalancePercent,
        DateTime previousResetLocal,
        DateTime incomingResetLocal,
        TimeSpan materialResetAdvance,
        DateTime nowLocal,
        TimeSpan resetDueGrace)
    {
        previousBalancePercent = ClampPercent(previousBalancePercent);
        incomingBalancePercent = ClampPercent(incomingBalancePercent);
        if (previousBalancePercent > 50 ||
            incomingBalancePercent < 95 ||
            incomingBalancePercent - previousBalancePercent < 50)
        {
            return false;
        }

        if (previousResetLocal != DateTime.MinValue &&
            nowLocal != DateTime.MinValue &&
            previousResetLocal > nowLocal.Add(resetDueGrace))
        {
            return true;
        }

        if (previousResetLocal == DateTime.MinValue ||
            incomingResetLocal == DateTime.MinValue)
        {
            return true;
        }

        // A real weekly reset should move the weekly reset boundary forward. A lone provider sample
        // that jumps low weekly balance to near-full without that boundary is treated as cache jitter.
        return incomingResetLocal <= previousResetLocal.Add(materialResetAdvance);
    }

    private static bool IsSuspiciousProviderFiveHourEarlyResetSpike(
        int previousBalancePercent,
        int incomingBalancePercent,
        DateTime previousResetLocal,
        DateTime incomingResetLocal,
        DateTime nowLocal,
        TimeSpan resetDueGrace)
    {
        previousBalancePercent = ClampPercent(previousBalancePercent);
        incomingBalancePercent = ClampPercent(incomingBalancePercent);
        if (incomingBalancePercent < 95 ||
            incomingBalancePercent - previousBalancePercent < 10)
        {
            return false;
        }

        if (previousResetLocal == DateTime.MinValue ||
            incomingResetLocal == DateTime.MinValue ||
            nowLocal == DateTime.MinValue)
        {
            return false;
        }

        return previousResetLocal > nowLocal.Add(resetDueGrace) &&
            incomingResetLocal > previousResetLocal.AddMinutes(1.0);
    }

    private void LogRejectedProviderQuotaSnapshot(
        CodexQuotaSnapshot rejectedSnapshot,
        string reason,
        bool codexRunning)
    {
        QuotaRuntimeState quotaState = GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex);
        QuotaRingDecisionInfo decision = CreateQuotaRingDecisionInfo(quotaState, rejectedSnapshot, true);
        LogQuotaRingDecision(
            CodexRadarSoftwareMode.Codex,
            CompleteQuotaRingDecisionInfo(quotaState, decision, string.IsNullOrWhiteSpace(reason) ? "provider_quota_snapshot_ignored" : reason),
            quotaState.Snapshot,
            true,
            codexRunning);
    }

    private static bool IsCachedQuotaSnapshotCurrent(
        string sessionsPath,
        string cachedPath,
        DateTime cachedWriteUtc)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (codexQuotaSnapshotNewestVerifyUtc != DateTime.MinValue &&
            (nowUtc - codexQuotaSnapshotNewestVerifyUtc).TotalSeconds < 30.0)
        {
            return true;
        }

        codexQuotaSnapshotNewestVerifyUtc = nowUtc;
        string newestPath;
        DateTime newestWriteUtc;
        if (!TryFindNewestQuotaRolloutFile(sessionsPath, out newestPath, out newestWriteUtc))
        {
            return true;
        }

        return string.Equals(newestPath, cachedPath, StringComparison.OrdinalIgnoreCase) &&
            newestWriteUtc <= cachedWriteUtc;
    }

    private static bool TryFindNewestQuotaRolloutFile(
        string sessionsPath,
        out string newestPath,
        out DateTime newestWriteUtc)
    {
        newestPath = string.Empty;
        newestWriteUtc = DateTime.MinValue;
        if (string.IsNullOrEmpty(sessionsPath) || !Directory.Exists(sessionsPath))
        {
            return false;
        }

        try
        {
            List<string> rolloutFiles = EnumerateCodexRolloutFiles(sessionsPath);
            if (rolloutFiles.Count > 0)
            {
                newestPath = rolloutFiles[0];
                newestWriteUtc = SafeGetLastWriteTimeUtc(newestPath);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        return !string.IsNullOrEmpty(newestPath);
    }

    private bool TryReadCodexSessionQuota(out CodexQuotaSnapshot snapshot)
    {
        snapshot = null;
        string sessionsPath = this.quotaSessionsPath;
        if (string.IsNullOrEmpty(sessionsPath))
        {
            return false;
        }

        if (!Directory.Exists(sessionsPath))
        {
            return false;
        }

        // Clear the watcher hint before scanning so an append that happens during the scan
        // sets it again and is observed on the next quota refresh.
        bool filesChanged = this.quotaSessionWatcher == null ||
            Interlocked.Exchange(ref this.quotaSessionFilesChanged, 0) != 0;
        if (!filesChanged)
        {
            lock (codexQuotaSnapshotCacheLock)
            {
                // The watcher is only an invalidation hint; metadata still verifies append-only changes.
                if (codexQuotaSnapshotCache != null &&
                    File.Exists(codexQuotaSnapshotCachePath) &&
                    codexQuotaSnapshotCacheWriteUtc == SafeGetLastWriteTimeUtc(codexQuotaSnapshotCachePath) &&
                    codexQuotaSnapshotCacheLength == SafeGetFileLength(codexQuotaSnapshotCachePath) &&
                    IsCachedQuotaSnapshotCurrent(sessionsPath, codexQuotaSnapshotCachePath, codexQuotaSnapshotCacheWriteUtc))
                {
                    snapshot = codexQuotaSnapshotCache.Clone();
                    return true;
                }
            }
        }

        List<string> rolloutFiles;
        try
        {
            rolloutFiles = EnumerateCodexRolloutFiles(sessionsPath);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
            Program.LogException(ex);
            return false;
        }

        if (rolloutFiles.Count == 0)
        {
            return false;
        }

        string newestPath = rolloutFiles[0];
        DateTime newestWriteUtc = SafeGetLastWriteTimeUtc(newestPath);
        long newestLength = SafeGetFileLength(newestPath);
        lock (codexQuotaSnapshotCacheLock)
        {
            // Length participates in the key because the active JSONL file is append-only.
            if (codexQuotaSnapshotCache != null &&
                string.Equals(codexQuotaSnapshotCachePath, newestPath, StringComparison.OrdinalIgnoreCase) &&
                codexQuotaSnapshotCacheWriteUtc == newestWriteUtc &&
                codexQuotaSnapshotCacheLength == newestLength)
            {
                snapshot = codexQuotaSnapshotCache.Clone();
                return true;
            }
        }

        JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(
            BoundedHttpTextReader.AuthenticatedJsonMaxBytes);

        CodexQuotaEvent latestEvent = null;
        int count = Math.Min(rolloutFiles.Count, MaxQuotaRolloutFilesToScan);
        for (int i = 0; i < count; i++)
        {
            string file = rolloutFiles[i];
            if (latestEvent != null && SafeGetLastWriteTimeUtc(file) < latestEvent.UpdatedUtc)
            {
                break;
            }

            CodexQuotaEvent quotaEvent;
            if (TryParseLatestQuotaEventFromFile(file, serializer, out quotaEvent) &&
                (latestEvent == null || quotaEvent.UpdatedUtc > latestEvent.UpdatedUtc))
            {
                latestEvent = quotaEvent;
            }
        }

        if (latestEvent == null)
        {
            return false;
        }

        snapshot = latestEvent.Snapshot;
        if (snapshot != null)
        {
            snapshot.SourceUpdatedUtc = latestEvent.UpdatedUtc;
            snapshot.SourceUpdatedKnown = latestEvent.UpdatedUtc != DateTime.MinValue;
        }

        if (snapshot != null)
        {
            lock (codexQuotaSnapshotCacheLock)
            {
                codexQuotaSnapshotCachePath = newestPath;
                codexQuotaSnapshotCacheWriteUtc = newestWriteUtc;
                codexQuotaSnapshotCacheLength = newestLength;
                codexQuotaSnapshotCache = snapshot.Clone();
            }

        }

        return snapshot != null;
    }

    internal static List<string> EnumerateCodexRolloutFiles(string sessionsPath)
    {
        List<string> rolloutFiles = new List<string>();
        if (string.IsNullOrWhiteSpace(sessionsPath) || !Directory.Exists(sessionsPath))
        {
            return rolloutFiles;
        }

        // Creation-date folders do not move when an old conversation resumes, so every caller
        // shares this one recursive discovery rule instead of creating a second watcher or scan.
        foreach (string file in Directory.EnumerateFiles(
            sessionsPath,
            "rollout-*.jsonl",
            SearchOption.AllDirectories))
        {
            rolloutFiles.Add(file);
        }

        rolloutFiles.Sort(delegate(string left, string right)
        {
            return SafeGetLastWriteTimeUtc(right).CompareTo(SafeGetLastWriteTimeUtc(left));
        });
        return rolloutFiles;
    }

    internal CodexTaskMonitorReader CodexTaskMonitor
    {
        get { return this.codexTaskMonitorReader; }
    }

    private void RequestCodexTaskMonitorReconcile()
    {
        Interlocked.Exchange(ref this.codexTaskMonitorReconcileRequested, 1);
        this.nextCodexTaskMonitorReconcileUtc = DateTime.MinValue;
    }

    private void RefreshCodexTaskMonitorIfNeeded()
    {
        CodexTaskMonitorReader reader = this.codexTaskMonitorReader;
        if (reader == null)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc >= this.nextCodexTaskMonitorStatusRefreshUtc)
        {
            this.nextCodexTaskMonitorStatusRefreshUtc = nowUtc.AddSeconds(1.0);
            reader.RequestStatusRefresh();
        }
        if (!this.CurrentSettings.CodexTaskMonitorEnabled)
        {
            return;
        }

        bool requested = Interlocked.Exchange(ref this.codexTaskMonitorReconcileRequested, 0) != 0;
        if (!requested && nowUtc < this.nextCodexTaskMonitorReconcileUtc)
        {
            return;
        }

        this.nextCodexTaskMonitorReconcileUtc = nowUtc.AddSeconds(30.0);
        if (Interlocked.CompareExchange(ref this.codexTaskMonitorReconcileRunning, 1, 0) != 0)
        {
            Interlocked.Exchange(ref this.codexTaskMonitorReconcileRequested, 1);
            return;
        }

        // The existing WinForms tick supplies fallback timing, while traversal and parsing stay
        // off the UI thread. No independent timer family is introduced for this backend reader.
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                reader.RequestReconcile(EnumerateCodexRolloutFiles(this.quotaSessionsPath));
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref this.codexTaskMonitorReconcileRequested, 1);
                Program.LogException(ex);
            }
            finally
            {
                Interlocked.Exchange(ref this.codexTaskMonitorReconcileRunning, 0);
            }
        });
    }

    private void InitializeQuotaSessionWatcher()
    {
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profilePath))
        {
            return;
        }

        this.quotaSessionsPath = Path.Combine(Path.Combine(profilePath, ".codex"), "sessions");
        if (!Directory.Exists(this.quotaSessionsPath))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new FileSystemWatcher(this.quotaSessionsPath, "rollout-*.jsonl");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size;
            watcher.Changed += OnQuotaSessionFileChanged;
            watcher.Created += OnQuotaSessionFileChanged;
            watcher.Deleted += OnQuotaSessionFileChanged;
            watcher.Renamed += OnQuotaSessionFileRenamed;
            watcher.Error += OnQuotaSessionWatcherError;
            watcher.EnableRaisingEvents = true;
            this.quotaSessionWatcher = watcher;
        }
        catch (Exception ex)
        {
            // Without a watcher the changed flag remains set, preserving the original polling behavior.
            Program.LogException(ex);
        }
    }

    private void OnQuotaSessionFileChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
        if (e.ChangeType == WatcherChangeTypes.Created)
        {
            Interlocked.Exchange(ref this.codexTaskMonitorReconcileRequested, 1);
        }
        CodexTaskMonitorReader reader = this.codexTaskMonitorReader;
        if (reader != null)
        {
            reader.NotifyFileChanged(e.FullPath, e.ChangeType);
        }
    }

    private void OnQuotaSessionFileRenamed(object sender, RenamedEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
        Interlocked.Exchange(ref this.codexTaskMonitorReconcileRequested, 1);
        CodexTaskMonitorReader reader = this.codexTaskMonitorReader;
        if (reader != null)
        {
            reader.NotifyFileChanged(e.OldFullPath, WatcherChangeTypes.Deleted);
            reader.NotifyFileChanged(e.FullPath, WatcherChangeTypes.Renamed);
        }
    }

    private void OnQuotaSessionWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
        RequestCodexTaskMonitorReconcile();
    }

    private void DisposeQuotaSessionWatcher()
    {
        FileSystemWatcher watcher = this.quotaSessionWatcher;
        this.quotaSessionWatcher = null;
        if (watcher == null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnQuotaSessionFileChanged;
        watcher.Created -= OnQuotaSessionFileChanged;
        watcher.Deleted -= OnQuotaSessionFileChanged;
        watcher.Renamed -= OnQuotaSessionFileRenamed;
        watcher.Error -= OnQuotaSessionWatcherError;
        watcher.Dispose();
    }

    private static bool TryParseLatestQuotaEventFromFile(string path, JavaScriptSerializer serializer, out CodexQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // Quota events are near the end of rollout files. Read backwards in bounded
                // chunks instead of deserializing the complete session history on every refresh.
                long offset = stream.Length;
                byte[] tail = new byte[0];
                while (offset > 0)
                {
                    int readSize = (int)Math.Min(QuotaTailChunkBytes, offset);
                    offset -= readSize;
                    stream.Seek(offset, SeekOrigin.Begin);

                    byte[] chunk = new byte[readSize];
                    int read = stream.Read(chunk, 0, readSize);
                    if (read <= 0)
                    {
                        continue;
                    }

                    byte[] expandedTail = new byte[read + tail.Length];
                    Buffer.BlockCopy(chunk, 0, expandedTail, 0, read);
                    if (tail.Length > 0)
                    {
                        Buffer.BlockCopy(tail, 0, expandedTail, read, tail.Length);
                    }

                    tail = expandedTail;
                    string text = Encoding.UTF8.GetString(tail, 0, tail.Length);
                    if (TryParseLatestQuotaEventFromText(text, path, serializer, out quotaEvent))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryParseLatestQuotaEventFromText(string text, string path, JavaScriptSerializer serializer, out CodexQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 ||
                line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                continue;
            }

            if (root == null ||
                !string.Equals(GetQuotaString(root, "type"), "event_msg", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> payload = GetQuotaObject(root, "payload");
            if (payload == null ||
                !string.Equals(GetQuotaString(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> rateLimits = GetQuotaObject(payload, "rate_limits");
            CodexQuotaSnapshot snapshot;
            if (rateLimits == null || !TryBuildQuotaSnapshot(rateLimits, out snapshot))
            {
                continue;
            }

            DateTime updatedLocal;
            DateTime updatedUtc = SafeGetLastWriteTimeUtc(path);
            if (TryGetQuotaDate(root, "timestamp", out updatedLocal))
            {
                updatedUtc = updatedLocal.ToUniversalTime();
            }
            else if (updatedUtc == DateTime.MinValue)
            {
                updatedUtc = DateTime.UtcNow;
            }

            quotaEvent = new CodexQuotaEvent();
            quotaEvent.Snapshot = snapshot;
            quotaEvent.UpdatedUtc = updatedUtc;
            return true;
        }

        return false;
    }

    // Windows at or under this duration belong to the five-hour ring; anything longer is weekly.
    // 24h leaves room for the provider re-shaping the short window without misrouting the 7d one.
    private const double FiveHourWindowRouteMaxSeconds = 24.0 * 3600.0;

    private static bool TryBuildQuotaSnapshot(Dictionary<string, object> rateLimits, out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        bool found = false;
        found = ApplyQuotaSlot(rateLimits, "primary", snapshot) || found;
        found = ApplyQuotaSlot(rateLimits, "secondary", snapshot) || found;
        if (found)
        {
            ApplyFiveHourLimitAbsence(snapshot);
        }

        return found;
    }

    // When a read carries a weekly window but no short (~5h) window at all, the provider has
    // (temporarily) lifted the 5h limit: show the five-hour ring as a full unlimited state instead
    // of leaving stale or misrouted values in it.
    private static void ApplyFiveHourLimitAbsence(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null ||
            !snapshot.WeeklyUsageDiagnosticKnown ||
            snapshot.FiveHourUsageDiagnosticKnown)
        {
            return;
        }

        snapshot.FiveHourLimitAbsent = true;
        snapshot.FiveHourPercent = 100;
        snapshot.FiveHourResetLocal = DateTime.MinValue;
        snapshot.FiveHourResetKnown = false;
    }

    private static bool ApplyQuotaSlot(Dictionary<string, object> rateLimits, string key, CodexQuotaSnapshot snapshot)
    {
        Dictionary<string, object> slot = GetQuotaObject(rateLimits, key);
        if (slot == null)
        {
            return false;
        }

        double usedPercent;
        string usedFieldName;
        if (TryGetQuotaNumber(slot, "used_percent", out usedPercent))
        {
            usedFieldName = "used_percent";
        }
        else if (TryGetQuotaNumber(slot, "used_percentage", out usedPercent))
        {
            usedFieldName = "used_percentage";
        }
        else
        {
            return false;
        }

        double windowMinutes;
        bool hasWindowMinutes = TryGetQuotaNumber(slot, "window_minutes", out windowMinutes);
        bool isFiveHour = string.Equals(key, "primary", StringComparison.OrdinalIgnoreCase);
        if (hasWindowMinutes)
        {
            // Same duration routing threshold as the provider parser: while the 5h limit is lifted,
            // the CLI can report the weekly window in "primary", which must land on the weekly ring.
            isFiveHour = windowMinutes * 60.0 <= FiveHourWindowRouteMaxSeconds;
        }

        int remainingPercent = ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool hasReset = TryGetQuotaDate(slot, "resets_at", out resetLocal);
        if (isFiveHour)
        {
            snapshot.FiveHourPercent = remainingPercent;
            SetQuotaUsageDiagnostics(snapshot, true, "session", usedFieldName, usedPercent, usedPercent);
            if (hasReset)
            {
                snapshot.FiveHourResetLocal = resetLocal;
                snapshot.FiveHourResetKnown = true;
            }
        }
        else
        {
            snapshot.WeeklyPercent = remainingPercent;
            SetQuotaUsageDiagnostics(snapshot, false, "session", usedFieldName, usedPercent, usedPercent);
            if (hasReset)
            {
                snapshot.WeeklyResetLocal = resetLocal;
                snapshot.WeeklyResetKnown = true;
            }
        }

        return true;
    }

    private bool LoadSelectedQuotaCacheIntoDisplay()
    {
        return LoadSelectedQuotaCacheIntoDisplay(false);
    }

    private bool LoadSelectedQuotaCacheIntoDisplay(bool preserveExistingOnMiss)
    {
        CodexQuotaSnapshot cachedQuotaSnapshot;
        if (TryReadQuotaIniSnapshot(GetEffectiveCodexRadarSoftwareMode(), out cachedQuotaSnapshot))
        {
            this.quotaSnapshot = NormalizeQuotaSnapshot(cachedQuotaSnapshot);
            this.quotaSourceKnown = true;
            return true;
        }
        if (!preserveExistingOnMiss)
        {
            this.quotaSnapshot = CodexQuotaSnapshot.CreateDefault();
            this.quotaSourceKnown = false;
        }

        return false;
    }

    private static bool TryReadQuotaIniSnapshot(out CodexQuotaSnapshot snapshot)
    {
        return TryReadQuotaIniSnapshot(CodexRadarSoftwareMode.Codex, out snapshot);
    }

    private static bool TryReadQuotaIniSnapshot(CodexRadarSoftwareMode softwareMode, out CodexQuotaSnapshot snapshot)
    {
        return TryReadQuotaIniSnapshot(
            softwareMode,
            GetQuotaIniPath(softwareMode),
            DateTime.UtcNow,
            out snapshot);
    }

    private static bool TryReadQuotaIniSnapshot(
        CodexRadarSoftwareMode softwareMode,
        string path,
        DateTime nowUtc,
        out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        bool found = false;
        bool foundFiveHourPercent = false;
        bool foundWeeklyPercent = false;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.FiveHourPercent = ClampPercent(percent);
                    found = true;
                    foundFiveHourPercent = true;
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                    found = true;
                    foundWeeklyPercent = true;
                }
                else if (string.Equals(key, "FiveHourLimitAbsent", StringComparison.OrdinalIgnoreCase))
                {
                    bool limitAbsent;
                    if (bool.TryParse(value, out limitAbsent) && limitAbsent)
                    {
                        snapshot.FiveHourLimitAbsent = true;
                        found = true;
                    }
                }
                else if (string.Equals(key, "FiveHourReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.FiveHourResetLocal = dateTime;
                    snapshot.FiveHourResetKnown = true;
                    found = true;
                }
                else if (string.Equals(key, "WeeklyReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.WeeklyResetLocal = dateTime;
                    snapshot.WeeklyResetKnown = true;
                    found = true;
                }
                else if (string.Equals(key, "SourceUpdatedUtc", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out dateTime))
                {
                    snapshot.SourceUpdatedUtc = dateTime.ToUniversalTime();
                    snapshot.SourceUpdatedKnown = true;
                    found = true;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        if (softwareMode == CodexRadarSoftwareMode.Claude &&
            !IsCompleteClaudeQuotaCacheSnapshot(
                snapshot,
                foundFiveHourPercent,
                foundWeeklyPercent,
                nowUtc))
        {
            return false;
        }

        if (found)
        {
            MarkQuotaSnapshotSource(
                snapshot,
                softwareMode == CodexRadarSoftwareMode.Claude
                    ? "claude_personal_cache"
                    : "cache");
            if (softwareMode != CodexRadarSoftwareMode.Claude && !snapshot.SourceUpdatedKnown)
            {
                snapshot.SourceUpdatedUtc = SafeGetLastWriteTimeUtc(path);
                snapshot.SourceUpdatedKnown = snapshot.SourceUpdatedUtc != DateTime.MinValue;
            }
        }

        return found;
    }

    private static bool IsCompleteClaudeQuotaCacheSnapshot(
        CodexQuotaSnapshot snapshot,
        bool fiveHourPercentKnown,
        bool weeklyPercentKnown,
        DateTime nowUtc)
    {
        if (snapshot == null)
        {
            return false;
        }

        return ClaudeCodeUsageReader.IsCompleteQuotaSnapshot(
            new ClaudeCodeUsageSnapshot
            {
                FiveHourPercent = snapshot.FiveHourPercent,
                FiveHourPercentKnown = fiveHourPercentKnown,
                WeeklyPercent = snapshot.WeeklyPercent,
                WeeklyPercentKnown = weeklyPercentKnown,
                FiveHourResetLocal = snapshot.FiveHourResetLocal,
                FiveHourResetKnown = snapshot.FiveHourResetKnown,
                WeeklyResetLocal = snapshot.WeeklyResetLocal,
                WeeklyResetKnown = snapshot.WeeklyResetKnown,
                SourceUpdatedUtc = snapshot.SourceUpdatedUtc,
                SourceUpdatedKnown = snapshot.SourceUpdatedKnown
            },
            nowUtc);
    }

    private static void TryWriteQuotaIniSnapshot(CodexQuotaSnapshot snapshot)
    {
        TryWriteQuotaIniSnapshot(CodexRadarSoftwareMode.Codex, snapshot);
    }

    private static void TryWriteQuotaIniSnapshot(CodexRadarSoftwareMode softwareMode, CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        if (softwareMode == CodexRadarSoftwareMode.Claude)
        {
            ClaudeCodeUsageReader.TryWriteQuotaCache(new ClaudeCodeUsageSnapshot
            {
                FiveHourPercent = snapshot.FiveHourPercent,
                FiveHourPercentKnown = true,
                WeeklyPercent = snapshot.WeeklyPercent,
                WeeklyPercentKnown = true,
                FiveHourResetLocal = snapshot.FiveHourResetLocal,
                FiveHourResetKnown = snapshot.FiveHourResetKnown,
                WeeklyResetLocal = snapshot.WeeklyResetLocal,
                WeeklyResetKnown = snapshot.WeeklyResetKnown,
                SourceUpdatedUtc = snapshot.SourceUpdatedUtc,
                SourceUpdatedKnown = snapshot.SourceUpdatedKnown
            });
            return;
        }

        try
        {
            string path = GetQuotaIniPath(softwareMode);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            List<string> lines = new List<string>();
            lines.Add("Version=1");
            lines.Add("FiveHourPercent=" + ClampPercent(snapshot.FiveHourPercent).ToString(CultureInfo.InvariantCulture));
            lines.Add("WeeklyPercent=" + ClampPercent(snapshot.WeeklyPercent).ToString(CultureInfo.InvariantCulture));
            if (snapshot.FiveHourLimitAbsent)
            {
                lines.Add("FiveHourLimitAbsent=True");
            }

            if (snapshot.FiveHourResetKnown)
            {
                lines.Add("FiveHourReset=" + snapshot.FiveHourResetLocal.ToString("o", CultureInfo.InvariantCulture));
            }

            if (snapshot.WeeklyResetKnown)
            {
                lines.Add("WeeklyReset=" + snapshot.WeeklyResetLocal.ToString("o", CultureInfo.InvariantCulture));
            }

            if (snapshot.SourceUpdatedKnown)
            {
                lines.Add("SourceUpdatedUtc=" + snapshot.SourceUpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
            }

            string next = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), next, StringComparison.Ordinal))
            {
                return;
            }

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, next, SharedEncoding.Utf8NoBom);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static string GetQuotaIniPath(CodexRadarSoftwareMode softwareMode)
    {
        return softwareMode == CodexRadarSoftwareMode.Claude
            ? ClaudeCodeUsageReader.QuotaCachePath
            : Path.Combine(Logger.DirectoryPath, "quota.ini");
    }

    private static Dictionary<string, object> GetQuotaObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static Dictionary<string, object> GetFirstQuotaObjectFromArray(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return null;
        }

        object[] array = value as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                Dictionary<string, object> item = array[i] as Dictionary<string, object>;
                if (item != null)
                {
                    return item;
                }
            }
        }

        System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null || value is string)
        {
            return null;
        }

        foreach (object entry in enumerable)
        {
            Dictionary<string, object> item = entry as Dictionary<string, object>;
            if (item != null)
            {
                return item;
            }
        }

        return null;
    }

    private static List<Dictionary<string, object>> GetQuotaObjectsFromArray(
        Dictionary<string, object> values,
        string key)
    {
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return result;
        }

        System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null || value is string)
        {
            return result;
        }

        foreach (object entry in enumerable)
        {
            if (result.Count >= 2048)
            {
                break;
            }

            Dictionary<string, object> item = entry as Dictionary<string, object>;
            if (item != null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string GetQuotaString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGetJsonBool(Dictionary<string, object> values, string key, out bool result)
    {
        result = false;
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return false;
        }

        if (value is bool)
        {
            result = (bool)value;
            return true;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return bool.TryParse(text, out result);
    }

    private static bool TryGetQuotaNumber(Dictionary<string, object> values, string key, out double number)
    {
        number = 0.0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaNumber(value, out number);
    }

    private static bool TryReadQuotaNumber(object value, out double number)
    {
        number = 0.0;
        if (value == null)
        {
            return false;
        }

        string text = value as string;
        if (text != null)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            number = 0.0;
            return false;
        }
    }

    private static bool TryGetQuotaDate(Dictionary<string, object> values, string key, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaDate(value, out localDate);
    }

    private static bool TryGetCodexModelIqDataWindow(
        Dictionary<string, object> values,
        string key,
        out DateTime localDate,
        out int windowStartHour)
    {
        localDate = DateTime.MinValue;
        windowStartHour = 0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadCodexModelIqDataWindow(value, out localDate, out windowStartHour);
    }

    private static bool TryReadCodexModelIqDataWindow(
        object value,
        out DateTime localDate,
        out int windowStartHour)
    {
        localDate = DateTime.MinValue;
        windowStartHour = 0;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Match match = Regex.Match(
                text.Trim(),
                "^(\\d{4}-\\d{2}-\\d{2})(?:[-_\\s]*(am|pm|n)(?:[-_\\s]*\\d+)?)?$",
                RegexOptions.IgnoreCase);
            DateTime date;
            if (match.Success &&
                DateTime.TryParseExact(
                    match.Groups[1].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
            {
                localDate = date.Date;
                string suffix = match.Groups[2].Value;
                windowStartHour =
                    string.Equals(suffix, "pm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(suffix, "n", StringComparison.OrdinalIgnoreCase)
                    ? 12
                    : 0;
                return true;
            }
        }

        if (TryReadQuotaDate(value, out localDate))
        {
            windowStartHour = localDate.Hour >= 12 ? 12 : 0;
            return true;
        }

        return false;
    }

    private static bool TryReadQuotaDate(object value, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        double seconds;
        if (TryReadQuotaNumber(value, out seconds))
        {
            if (seconds > 10000000000.0)
            {
                seconds /= 1000.0;
            }

            try
            {
                DateTimeOffset epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
                localDate = epoch.AddSeconds(seconds).LocalDateTime;
                return true;
            }
            catch
            {
                localDate = DateTime.MinValue;
                return false;
            }
        }

        string text = value as string;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        DateTimeOffset offsetDate;
        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out offsetDate))
        {
            localDate = offsetDate.LocalDateTime;
            return true;
        }

        DateTime dateTime;
        if (DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dateTime))
        {
            localDate = dateTime.ToLocalTime();
            return true;
        }

        return false;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long SafeGetFileLength(string path)
    {
        try
        {
            FileInfo info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static int ClampEfficiencyPercent(int value)
    {
        return Math.Max(0, Math.Min(999, value));
    }

    internal static void RunStatusAndQuotaSelfTest()
    {
        RadarClockDial.RunSelfTest();
        RunCodexModelIqRefreshMarkerSelfTest();
        RunCodexRadarCacheHardeningSelfTest();
        RunClaudeQuotaCacheCompletenessSelfTest();
        RunCodexQuotaIdentityHardeningSelfTest();
        RunCodexModelIqDynamicScaleSelfTest();
        RunServiceHealthProjectionSelfTest();
        RunRadarFamilyRuntimeIsolationSelfTest();
        RunCodexRadarNotificationStateSelfTest();
        RunCodexRadarCatalogCompletenessSelfTest();
        RunCodexResetCreditsSelfTest();
        CodexQuotaHistoryStore.RunSelfTest();
        RunCodexAuthJsonSelfTest();
        RunWeeklyBurnRateSelfTest();

        int baseline = GetNextFiveHourConsumptionRingBaseline(-1, 67, 57);
        if (baseline != 67)
        {
            throw new InvalidOperationException("Five-hour consumption decrease baseline failed.");
        }

        baseline = GetNextFiveHourConsumptionRingBaseline(baseline, 57, 57);
        if (baseline != 67)
        {
            throw new InvalidOperationException("Equal five-hour balances cleared the consumption ring.");
        }

        baseline = GetNextFiveHourConsumptionRingBaseline(baseline, 57, 72);
        if (baseline != -1)
        {
            throw new InvalidOperationException("Five-hour reset/increase did not clear the old baseline.");
        }

        DateTime guardNow = new DateTime(2026, 7, 8, 13, 0, 0, DateTimeKind.Local);
        DateTime weeklyReset = new DateTime(2026, 7, 12, 16, 17, 16, DateTimeKind.Local);
        if (!IsSuspiciousProviderWeeklySpike(
            13,
            100,
            weeklyReset,
            weeklyReset,
            TimeSpan.FromHours(6.0),
            guardNow,
            TimeSpan.FromMinutes(30.0)))
        {
            throw new InvalidOperationException("Codex provider weekly spike guard missed a low-to-full single sample.");
        }

        if (!IsSuspiciousProviderWeeklySpike(
            13,
            100,
            weeklyReset,
            weeklyReset.AddDays(7.0),
            TimeSpan.FromHours(6.0),
            guardNow,
            TimeSpan.FromMinutes(30.0)))
        {
            throw new InvalidOperationException("Codex provider weekly spike guard allowed an early weekly reset boundary jump.");
        }

        DateTime dueWeeklyReset = guardNow.AddMinutes(-5.0);
        if (IsSuspiciousProviderWeeklySpike(
            13,
            100,
            dueWeeklyReset,
            dueWeeklyReset.AddDays(7.0),
            TimeSpan.FromHours(6.0),
            guardNow,
            TimeSpan.FromMinutes(30.0)))
        {
            throw new InvalidOperationException("Codex provider weekly spike guard rejected a material weekly reset advance.");
        }

        if (!IsSuspiciousProviderFiveHourEarlyResetSpike(
            79,
            99,
            guardNow.AddHours(3.0),
            guardNow.AddHours(5.0),
            guardNow,
            TimeSpan.FromMinutes(30.0)))
        {
            throw new InvalidOperationException("Codex provider five-hour early reset spike guard missed a future reset jump.");
        }

        if (IsSuspiciousProviderFiveHourEarlyResetSpike(
            79,
            99,
            guardNow.AddMinutes(-1.0),
            guardNow.AddHours(5.0),
            guardNow,
            TimeSpan.FromMinutes(30.0)))
        {
            throw new InvalidOperationException("Codex provider five-hour early reset spike guard rejected a due reset.");
        }

        if (!IsSuspiciousWeeklyConsumptionBaseline(
            100,
            12,
            12,
            guardNow.AddHours(3.0),
            guardNow.AddHours(3.0)))
        {
            throw new InvalidOperationException("Suspicious weekly consumption baseline repair did not catch a stuck 100 baseline.");
        }

        string percentPayload =
            "{\"plan_type\":\"plus\",\"pool\":\"additional\"," +
            "\"primary\":{\"used_percent\":1,\"resets_at\":\"2026-07-07T04:00:00+09:00\"}," +
            "\"secondary\":{\"used_percentage\":2,\"resets_at\":\"2026-07-12T16:00:00+09:00\"}}";
        CodexProviderUsageResult percentResult = ParseCodexProviderUsageResponse(
            percentPayload,
            true,
            200);
        if (percentResult == null ||
            !percentResult.Success ||
            percentResult.Snapshot == null ||
            percentResult.Snapshot.FiveHourPercent != 99 ||
            percentResult.Snapshot.WeeklyPercent != 98 ||
            !string.Equals(percentResult.Snapshot.FiveHourUsedFieldName, "used_percent", StringComparison.Ordinal) ||
            Math.Abs(percentResult.Snapshot.FiveHourNormalizedUsedPercent - 1.0) > 0.001 ||
            percentResult.Snapshot.ProviderHttpStatus != 200 ||
            percentResult.Snapshot.ProviderResponseBytes != SharedEncoding.Utf8NoBom.GetByteCount(percentPayload) ||
            percentResult.Snapshot.ProviderResponseBodySha256.Length != 64 ||
            percentResult.Snapshot.ProviderCorrelationId.Length != 32 ||
            !string.Equals(percentResult.Snapshot.ProviderPlan, "plus", StringComparison.Ordinal) ||
            !string.Equals(percentResult.Snapshot.ProviderPool, "additional", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex provider parsing or bounded diagnostic metadata failed.");
        }

        CodexProviderUsageResult utilizationResult = ParseCodexProviderUsageResponse(
            "{\"primary_window\":{\"utilization\":0.01,\"reset_at\":\"2026-07-07T04:00:00+09:00\"}," +
            "\"secondary_window\":{\"utilization\":0.44,\"reset_at\":\"2026-07-12T16:00:00+09:00\"}}",
            true,
            200);
        if (utilizationResult == null ||
            !utilizationResult.Success ||
            utilizationResult.Snapshot == null ||
            utilizationResult.Snapshot.FiveHourPercent != 99 ||
            utilizationResult.Snapshot.WeeklyPercent != 56 ||
            !string.Equals(utilizationResult.Snapshot.FiveHourUsedFieldName, "utilization", StringComparison.Ordinal) ||
            Math.Abs(utilizationResult.Snapshot.FiveHourNormalizedUsedPercent - 1.0) > 0.001)
        {
            throw new InvalidOperationException("Codex provider utilization parsing failed.");
        }

        // 5h limit lifted: primary_window IS the weekly window (limit_window_seconds=604800) and
        // secondary_window is null. Duration routing must land it on the weekly ring and flag the
        // five-hour ring as an unlimited state instead of inheriting the weekly numbers.
        CodexProviderUsageResult weeklyOnlyResult = ParseCodexProviderUsageResponse(
            "{\"rate_limit\":{\"allowed\":true,\"limit_reached\":false," +
            "\"primary_window\":{\"used_percent\":1,\"limit_window_seconds\":604800,\"reset_after_seconds\":603890,\"reset_at\":1784510243}," +
            "\"secondary_window\":null}}",
            true,
            200);
        if (weeklyOnlyResult == null ||
            !weeklyOnlyResult.Success ||
            weeklyOnlyResult.Snapshot == null ||
            weeklyOnlyResult.Snapshot.WeeklyPercent != 99 ||
            !weeklyOnlyResult.Snapshot.WeeklyUsageDiagnosticKnown ||
            weeklyOnlyResult.Snapshot.FiveHourUsageDiagnosticKnown ||
            !weeklyOnlyResult.Snapshot.FiveHourLimitAbsent ||
            weeklyOnlyResult.Snapshot.FiveHourPercent != 100 ||
            weeklyOnlyResult.Snapshot.FiveHourResetKnown)
        {
            throw new InvalidOperationException("Codex provider weekly-only payload routed into the five-hour ring instead of the unlimited state.");
        }

        // Both windows present but swapped across slots: duration must win over slot position.
        CodexProviderUsageResult swappedResult = ParseCodexProviderUsageResponse(
            "{\"rate_limit\":{" +
            "\"primary_window\":{\"used_percent\":10,\"limit_window_seconds\":604800,\"reset_at\":1784510243}," +
            "\"secondary_window\":{\"used_percent\":20,\"limit_window_seconds\":18000,\"reset_at\":1784000000}}}",
            true,
            200);
        if (swappedResult == null ||
            !swappedResult.Success ||
            swappedResult.Snapshot == null ||
            swappedResult.Snapshot.WeeklyPercent != 90 ||
            swappedResult.Snapshot.FiveHourPercent != 80 ||
            swappedResult.Snapshot.FiveHourLimitAbsent)
        {
            throw new InvalidOperationException("Codex provider duration routing failed for slot-swapped windows.");
        }

        // Session token_count shape while the 5h limit is lifted: only a weekly-length primary.
        Dictionary<string, object> weeklyOnlySessionRateLimits = new JavaScriptSerializer().DeserializeObject(
            "{\"primary\":{\"used_percent\":30,\"window_minutes\":10080,\"resets_at\":\"2026-07-19T16:00:00+09:00\"}}") as Dictionary<string, object>;
        CodexQuotaSnapshot weeklyOnlySession;
        if (!TryBuildQuotaSnapshot(weeklyOnlySessionRateLimits, out weeklyOnlySession) ||
            weeklyOnlySession.WeeklyPercent != 70 ||
            !weeklyOnlySession.WeeklyResetKnown ||
            weeklyOnlySession.FiveHourUsageDiagnosticKnown ||
            !weeklyOnlySession.FiveHourLimitAbsent ||
            weeklyOnlySession.FiveHourPercent != 100)
        {
            throw new InvalidOperationException("Codex session weekly-only rate limits routed into the five-hour ring instead of the unlimited state.");
        }

        // Regression: a normal two-window session payload must still fill both rings and not raise
        // the unlimited flag.
        Dictionary<string, object> normalSessionRateLimits = new JavaScriptSerializer().DeserializeObject(
            "{\"primary\":{\"used_percent\":40,\"window_minutes\":300,\"resets_at\":\"2026-07-13T16:00:00+09:00\"}," +
            "\"secondary\":{\"used_percent\":15,\"window_minutes\":10080,\"resets_at\":\"2026-07-19T16:00:00+09:00\"}}") as Dictionary<string, object>;
        CodexQuotaSnapshot normalSession;
        if (!TryBuildQuotaSnapshot(normalSessionRateLimits, out normalSession) ||
            normalSession.FiveHourPercent != 60 ||
            normalSession.WeeklyPercent != 85 ||
            normalSession.FiveHourLimitAbsent)
        {
            throw new InvalidOperationException("Codex session two-window rate limits regression: rings or unlimited flag changed.");
        }

        DateTime resetBase = new DateTime(2026, 7, 7, 4, 0, 0);
        if (!IsSuspiciousProviderZeroDrop(100, 0, resetBase, resetBase.AddMinutes(2.0), TimeSpan.FromMinutes(30.0)) ||
            IsSuspiciousProviderZeroDrop(5, 0, resetBase, resetBase.AddMinutes(2.0), TimeSpan.FromMinutes(30.0)) ||
            IsSuspiciousProviderZeroDrop(100, 0, resetBase, resetBase.AddMinutes(45.0), TimeSpan.FromMinutes(30.0)))
        {
            throw new InvalidOperationException("Codex provider zero-drop guard boundary failed.");
        }

        CodexRadarSnapshot publicSummarySnapshot;
        CodexRadarModelCatalogUpdate publicSummaryUpdate;
        if (!TryParseCodexRadarStatus(
            "{\"schema_version\":\"2.0\",\"type\":\"public_summary\",\"monitored_at\":\"2026-06-29T23:14:33+08:00\"}",
            CodexRadarModelCatalog.DefaultModelKey,
            false,
            out publicSummarySnapshot,
            out publicSummaryUpdate) ||
            GetCodexRadarSnapshotHealth(publicSummarySnapshot) != ServiceHealthState.Incomplete)
        {
            throw new InvalidOperationException("Public summary without model_iq should parse as incomplete.");
        }

        CodexRadarSnapshot incompatibleSchemaSnapshot;
        CodexRadarModelCatalogUpdate incompatibleSchemaUpdate;
        if (TryParseCodexRadarStatus(
                "{\"schema_version\":\"3.0\",\"model_iq\":{}}",
                CodexRadarModelCatalog.DefaultModelKey,
                false,
                out incompatibleSchemaSnapshot,
                out incompatibleSchemaUpdate) ||
            TryParseCodexRadarStatus(
                "{broken-json",
                CodexRadarModelCatalog.DefaultModelKey,
                false,
                out incompatibleSchemaSnapshot,
                out incompatibleSchemaUpdate))
        {
            throw new InvalidOperationException("Codex Radar unknown schema or damaged JSON must fail closed.");
        }

        CodexRadarSnapshot dynamicPlaceholder;
        if (TryParseCodexRadarHtmlFallbackStatus(
                "<html><body><div id=\"app\"></div><script>render()</script></body></html>",
                out dynamicPlaceholder) ||
            !ShouldRequestCodexRadarHtmlFallback(false, null))
        {
            throw new InvalidOperationException("Codex Radar dynamic HTML placeholder or fallback routing self-test failed.");
        }

        CodexRadarResetEvent rssReset;
        string validRss =
            "<rss><channel><item><title>Codex 用量限制重置</title>" +
            "<guid>reset-20260722</guid><pubDate>Wed, 22 Jul 2026 01:00:00 GMT</pubDate>" +
            "</item></channel></rss>";
        if (!TryParseCodexRadarFeedReset(validRss, out rssReset) ||
            rssReset == null ||
            !rssReset.EventUtcKnown ||
            !string.Equals(rssReset.Id, "reset-20260722", StringComparison.Ordinal) ||
            TryParseCodexRadarFeedReset("<rss><item>", out rssReset))
        {
            throw new InvalidOperationException("Codex Radar RSS normal/damaged parser self-test failed.");
        }

        if (!string.Equals(
                CodexRadarModelCatalog.BuildModelKey("gpt-5.6-sol", "low", string.Empty),
                "gpt_56_sol_low",
                StringComparison.Ordinal) ||
            !string.Equals(
                CodexRadarModelCatalog.NormalizeModelKey("gpt_5_6_luna_high"),
                "gpt_56_luna_high",
                StringComparison.Ordinal) ||
            !string.Equals(
                FormatCodexRadarCurrentModelShortLabel("gpt_56_sol_low", "GPT-5.6 Sol low"),
                "5.6SL",
                StringComparison.Ordinal) ||
            !string.Equals(
                FormatCodexRadarCurrentModelShortLabel("gpt_56_luna_high", "GPT-5.6 Luna high"),
                "5.6LH",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex Radar GPT-5.6 model identity or short-label formatting failed.");
        }

        CodexRadarSnapshot codex56Snapshot;
        CodexRadarModelCatalogUpdate codex56Update;
        string codex56Json =
            "{\"schema_version\":\"2.0\",\"model_iq\":{\"updated_at\":\"2026-07-10T10:15:00+08:00\",\"latest\":{\"date\":\"2026-07-10-am\",\"score\":116.7,\"passed\":7,\"tasks\":10," +
            "\"valid_tasks\":9,\"average_cost_usd\":9.5,\"average_task_seconds\":2220,\"total_tokens\":1200000," +
            "\"model\":\"gpt-5.6-sol\",\"reasoning_effort\":\"ultra\"}," +
            "\"comparisons\":{\"gpt_56_sol_medium\":{\"label\":\"GPT-5.6 Sol medium\",\"latest\":{" +
            "\"date\":\"2026-07-10-am\",\"score\":120.0,\"passed\":8,\"tasks\":10,\"valid_tasks\":10," +
            "\"average_cost_usd\":3.5,\"average_task_seconds\":1080,\"total_tokens\":500000," +
            "\"model\":\"gpt-5.6-sol\",\"reasoning_effort\":\"medium\"}}}}}";
        if (!TryParseCodexRadarStatus(
                codex56Json,
                CodexRadarModelCatalog.DefaultModelKey,
                false,
                out codex56Snapshot,
                out codex56Update) ||
            codex56Snapshot == null ||
            !codex56Snapshot.ModelIqKnown ||
            Math.Abs(codex56Snapshot.ModelIqPassRatePercent - 120.0) > 0.001 ||
            codex56Snapshot.ModelIqPassed != 8 ||
            codex56Snapshot.ModelIqValidTasks != 10 ||
            codex56Snapshot.CodexIqModels == null ||
            codex56Snapshot.CodexIqModels.Count != 2 ||
            !codex56Snapshot.ModelIqSourceUpdatedAtKnown ||
            codex56Snapshot.ModelIqSourceUpdatedAtLocal.ToUniversalTime() !=
                new DateTime(2026, 7, 10, 2, 15, 0, DateTimeKind.Utc) ||
            !codex56Snapshot.FetchedAtKnown ||
            codex56Snapshot.CodexIqModels[0].Current ||
            !codex56Snapshot.CodexIqModels[1].Current ||
            Math.Abs(codex56Snapshot.CodexIqModels[1].AverageCostUsd - 3.5) > 0.001)
        {
            throw new InvalidOperationException("Codex Radar GPT-5.6 comparison selection did not use Sol medium data.");
        }

        CodexRadarSnapshot distributedSnapshot;
        CodexRadarModelCatalogUpdate distributedUpdate;
        string distributedJson =
            "{\"schema_version\":\"2.0\",\"model_iq\":{\"updated_at\":\"2026-07-24T03:27:02+09:00\"," +
            "\"latest\":{\"date\":\"2026-07-24T03:27:02+09:00\",\"score\":95.5,\"passed\":71,\"tasks\":112," +
            "\"valid_tasks\":112,\"average_cost_usd\":9.82,\"average_task_seconds\":2159.7,\"total_tokens\":1517670145," +
            "\"model\":\"gpt-5.6-sol\",\"reasoning_effort\":\"max\"}," +
            "\"comparisons\":{\"gpt_56_terra_xhigh_distributed\":{\"label\":\"GPT-5.6 Terra xhigh\"," +
            "\"latest\":{\"date\":\"2026-07-24T03:27:02+09:00\",\"score\":88.8,\"passed\":66,\"tasks\":112," +
            "\"valid_tasks\":112,\"average_cost_usd\":2.42,\"average_task_seconds\":1152.3,\"total_tokens\":656210760," +
            "\"model\":\"gpt-5.6-terra\",\"reasoning_effort\":\"xhigh\"}," +
            "\"recent_days\":[" +
            "{\"date\":\"2026-07-24T01:27:02+09:00\",\"score\":87.4,\"passed\":65,\"tasks\":112,\"wall_seconds\":130000,\"total_tokens\":650000000}," +
            "{\"date\":\"2026-07-24T02:27:02+09:00\",\"score\":88.1,\"passed\":65,\"tasks\":112,\"wall_seconds\":129000,\"total_tokens\":653000000}]}}}}";
        bool distributedParsed = TryParseCodexRadarStatus(
                distributedJson,
                "gpt_56_terra_xhigh",
                false,
                out distributedSnapshot,
                out distributedUpdate);
        if (!distributedParsed ||
            distributedSnapshot == null ||
            !distributedSnapshot.ModelIqKnown ||
            distributedSnapshot.ModelIqPassed != 66 ||
            distributedSnapshot.ModelIqValidTasks != 112 ||
            distributedSnapshot.ModelIqHistory == null ||
            distributedSnapshot.ModelIqHistory.Count != 3 ||
            distributedSnapshot.CodexIqModels == null ||
            distributedSnapshot.CodexIqModels.Count != 2 ||
            distributedSnapshot.CodexIqModels[0].Current ||
            !distributedSnapshot.CodexIqModels[1].Current ||
            !string.Equals(
                distributedSnapshot.CodexIqModels[1].Key,
                "gpt_56_terra_xhigh",
                StringComparison.Ordinal) ||
            distributedSnapshot.ModelIqDataLabel.IndexOf("_t", StringComparison.OrdinalIgnoreCase) >= 0 ||
            distributedSnapshot.ModelIqDataLabel.IndexOf(':') < 0)
        {
            throw new InvalidOperationException(
                "Codex Radar distributed-model alias, precise history, or source task-count parsing failed." +
                " parsed=" + distributedParsed.ToString(CultureInfo.InvariantCulture) +
                " snapshot=" + (distributedSnapshot != null ? "present" : "null") +
                " known=" + (distributedSnapshot != null && distributedSnapshot.ModelIqKnown).ToString(CultureInfo.InvariantCulture) +
                " passed=" + (distributedSnapshot != null ? distributedSnapshot.ModelIqPassed : -1).ToString(CultureInfo.InvariantCulture) +
                " tasks=" + (distributedSnapshot != null ? distributedSnapshot.ModelIqValidTasks : -1).ToString(CultureInfo.InvariantCulture) +
                " history=" + (distributedSnapshot != null && distributedSnapshot.ModelIqHistory != null ? distributedSnapshot.ModelIqHistory.Count : -1).ToString(CultureInfo.InvariantCulture) +
                " models=" + (distributedSnapshot != null && distributedSnapshot.CodexIqModels != null ? distributedSnapshot.CodexIqModels.Count : -1).ToString(CultureInfo.InvariantCulture) +
                " label=" + (distributedSnapshot != null ? distributedSnapshot.ModelIqDataLabel ?? string.Empty : string.Empty));
        }

        string distributedHistoryCache = FormatCodexModelHistory(distributedSnapshot.ModelIqHistory);
        List<CodexModelHistoryPoint> distributedHistoryRoundTrip =
            ParseCodexModelHistory(distributedHistoryCache);
        if (distributedHistoryRoundTrip.Count != distributedSnapshot.ModelIqHistory.Count)
        {
            throw new InvalidOperationException("Codex Radar precise history cache round-trip collapsed timestamped samples.");
        }

        for (int i = 0; i < distributedHistoryRoundTrip.Count; i++)
        {
            if (distributedHistoryRoundTrip[i].DateLocal != distributedSnapshot.ModelIqHistory[i].DateLocal)
            {
                throw new InvalidOperationException("Codex Radar precise history cache round-trip changed a sample timestamp.");
            }
        }

        DateTime beijingNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneUtilities.GetBeijingTimeZone());
        string monthText = beijingNow.Month.ToString(CultureInfo.InvariantCulture);
        string dayText = beijingNow.Day.ToString(CultureInfo.InvariantCulture);
        string windowClosesAtText = beijingNow.AddHours(1.0).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+08:00";
        string windowOpenedAtText = beijingNow.AddHours(-1.0).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+08:00";
        string html =
            "<!-- codex-radar:summary:start -->" +
            "<span class=\"window-source-kicker\">速蹬窗口开启</span>" +
            "<div data-window-clock data-window-opened-at=\"" + windowOpenedAtText + "\" data-window-closes-at=\"" + windowClosesAtText + "\"></div>" +
            "<div class=\"model-iq-score-chip\" data-model-key=\"gpt_56_sol_medium\"><span>GPT-5.6-Sol-medium</span></div>" +
            "<h2>降智雷达 <span>" + monthText + "月" + dayText + "日13:59更新</span></h2>" +
            "<div class=\"model-iq-compare-row\"><span>通过数</span><strong class=\"model-iq-column-gpt_56_sol_medium\">6/10</strong></div>" +
            "<div class=\"model-iq-compare-row\"><span>IQ</span><strong class=\"model-iq-column-gpt_56_sol_medium\">90.0</strong></div>" +
            "<div class=\"model-iq-compare-row\"><span>耗时</span><strong class=\"model-iq-column-gpt_56_sol_medium\">3.4h</strong></div>" +
            "<div class=\"model-iq-compare-row\"><span>总tokens</span><strong class=\"model-iq-column-gpt_56_sol_medium\">42.3M</strong></div>" +
            "<title>" + monthText + "." + dayText + "_pm GPT-5.6 Sol medium: IQ指数 90.0, 6/10, 费用 $42.00, 耗时 204分钟, cache命中率 95.2%</title>" +
            "<svg><text class=\"model-iq-band-label\">90-110常态区</text></svg>" +
            "<section class=\"reset-judgement\" aria-label=\"重置雷达\">" +
            "<div><h2>重置雷达 <em>" + monthText + "月" + dayText + "日10:24更新</em></h2></div>" +
            "<article class=\"reset-judgement-card\"><span>发重置卡</span>" +
            "<strong>低 · 本轮是直接重置，不是发新卡</strong><p>重置卡说明。</p></article>" +
            "<article class=\"reset-judgement-card\"><span>硬重置</span>" +
            "<strong>已落地 · 10M 里程碑重置完成 · 下一次低</strong><p>硬重置说明。</p></article>" +
            "</section>";
        CodexRadarSnapshot htmlSnapshot;
        List<string> htmlFailures = new List<string>();
        if (!TryParseCodexRadarHtmlStatus(html, CodexRadarModelCatalog.DefaultModelKey, out htmlSnapshot) ||
            htmlSnapshot == null)
        {
            htmlFailures.Add("parse=false");
        }
        else
        {
            if (!htmlSnapshot.ModelIqKnown) htmlFailures.Add("ModelIqKnown=false");
            if (!htmlSnapshot.SpeedWindowKnown) htmlFailures.Add("SpeedWindowKnown=false");
            if (!htmlSnapshot.SpeedWindowOpen) htmlFailures.Add("SpeedWindowOpen=false");
            if (!htmlSnapshot.SpeedWindowOpenedAtKnown) htmlFailures.Add("SpeedWindowOpenedAtKnown=false");
            if (!htmlSnapshot.SpeedWindowClosedAtKnown) htmlFailures.Add("SpeedWindowClosedAtKnown=false");
            if (!htmlSnapshot.ResetRadarKnown) htmlFailures.Add("ResetRadarKnown=false");
            if (!htmlSnapshot.ResetRadarUpdatedAtKnown) htmlFailures.Add("ResetRadarUpdatedAtKnown=false");
            if (!string.Equals(htmlSnapshot.ResetCardStatus, "低", StringComparison.Ordinal))
            {
                htmlFailures.Add("ResetCardStatus=" + htmlSnapshot.ResetCardStatus);
            }

            if (!string.Equals(htmlSnapshot.HardResetStatus, "已落地", StringComparison.Ordinal))
            {
                htmlFailures.Add("HardResetStatus=" + htmlSnapshot.HardResetStatus);
            }
            if (Math.Abs(htmlSnapshot.ModelIqEfficiencyTotalTokens - 42300000.0) > 1.0)
            {
                htmlFailures.Add("tokens=" + htmlSnapshot.ModelIqEfficiencyTotalTokens.ToString(CultureInfo.InvariantCulture));
            }

            if (Math.Abs(htmlSnapshot.ModelIqEfficiencySerialSeconds - 12240.0) > 1.0)
            {
                htmlFailures.Add("seconds=" + htmlSnapshot.ModelIqEfficiencySerialSeconds.ToString(CultureInfo.InvariantCulture));
            }

            if (!htmlSnapshot.ModelIqDataWindowKnown)
            {
                htmlFailures.Add("DataWindowKnown=false");
            }
            else if (htmlSnapshot.ModelIqDataWindowStartHourLocal != 12)
            {
                htmlFailures.Add("DataWindowStartHour=" + htmlSnapshot.ModelIqDataWindowStartHourLocal.ToString(CultureInfo.InvariantCulture));
            }

            if (!htmlSnapshot.ModelIqNormalRangeKnown ||
                htmlSnapshot.ModelIqNormalLowScore != 90 ||
                htmlSnapshot.ModelIqNormalHighScore != 110)
            {
                htmlFailures.Add("NormalRange=" +
                    htmlSnapshot.ModelIqNormalLowScore.ToString(CultureInfo.InvariantCulture) +
                    "-" +
                    htmlSnapshot.ModelIqNormalHighScore.ToString(CultureInfo.InvariantCulture));
            }

        }

        if (htmlFailures.Count > 0)
        {
            throw new InvalidOperationException("Codex Radar HTML fallback parsing failed: " + string.Join(", ", htmlFailures.ToArray()));
        }

        CodexRadarSnapshot structuredTarget = CodexRadarSnapshot.CreateDefault();
        structuredTarget.SpeedWindowKnown = true;
        structuredTarget.SpeedWindowOpen = true;
        structuredTarget.SpeedWindowStatus = "open";
        CodexRadarSnapshot speedFallback = CodexRadarSnapshot.CreateDefault();
        speedFallback.SpeedWindowKnown = true;
        speedFallback.SpeedWindowOpen = false;
        speedFallback.SpeedWindowStatus = "closed";
        if (FillUnknownCodexRadarFields(structuredTarget, speedFallback) ||
            !structuredTarget.SpeedWindowOpen)
        {
            throw new InvalidOperationException("Codex Radar HTML fallback overwrote a known speed window.");
        }

        CodexRadarSnapshot emptyFallbackTarget = CodexRadarSnapshot.CreateDefault();
        if (!FillUnknownCodexRadarFields(emptyFallbackTarget, speedFallback) ||
            !emptyFallbackTarget.SpeedWindowKnown ||
            emptyFallbackTarget.SpeedWindowOpen ||
            !ShouldRequestCodexRadarHtmlFallback(true, emptyFallbackTarget))
        {
            throw new InvalidOperationException("Codex Radar HTML fallback must keep requesting missing Reset Radar judgement.");
        }

        CodexRadarSnapshot judgementFallback;
        if (!TryParseCodexRadarHtmlFallbackStatus(html, out judgementFallback) ||
            judgementFallback == null ||
            !judgementFallback.ResetRadarKnown ||
            !FillUnknownCodexRadarFields(emptyFallbackTarget, judgementFallback) ||
            !emptyFallbackTarget.ResetRadarKnown ||
            ShouldRequestCodexRadarHtmlFallback(true, emptyFallbackTarget))
        {
            throw new InvalidOperationException("Codex Radar reset-judgement parsing or fallback routing failed.");
        }

        CodexRadarSnapshot preservedJudgement = CodexRadarSnapshot.CreateDefault();
        PreserveCodexRadarResetJudgement(preservedJudgement, judgementFallback);
        if (!preservedJudgement.ResetRadarKnown ||
            !string.Equals(preservedJudgement.HardResetDescription, judgementFallback.HardResetDescription, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex Radar last-known reset judgement was not preserved.");
        }

        CodexRadarSnapshot staleSpeedWindow = CodexRadarSnapshot.CreateDefault();
        staleSpeedWindow.SpeedWindowKnown = true;
        staleSpeedWindow.SpeedWindowOpen = true;
        staleSpeedWindow.SpeedWindowStatus = "open";
        staleSpeedWindow.SpeedWindowClosedAtLocal = DateTime.Now.AddMinutes(-1.0);
        staleSpeedWindow.SpeedWindowClosedAtKnown = true;
        if (IsCodexRadarSpeedWindowCurrentlyOpen(staleSpeedWindow, DateTime.Now))
        {
            throw new InvalidOperationException("Expired speed window should not be treated as open.");
        }

        ExpireCodexRadarSpeedWindowIfClosed(staleSpeedWindow, DateTime.Now);
        if (staleSpeedWindow.SpeedWindowOpen ||
            !string.Equals(staleSpeedWindow.SpeedWindowStatus, "closed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expired speed window should normalize to closed.");
        }

        DateTime countdownNow = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Local);
        CodexRadarSnapshot countdownWindow = CodexRadarSnapshot.CreateDefault();
        countdownWindow.SpeedWindowKnown = true;
        countdownWindow.SpeedWindowOpen = true;
        countdownWindow.SpeedWindowStatus = "open";
        countdownWindow.SpeedWindowOpenedAtLocal = countdownNow.AddHours(-20.0);
        countdownWindow.SpeedWindowOpenedAtKnown = true;
        countdownWindow.SpeedWindowClosedAtLocal = countdownNow.AddHours(20.0);
        countdownWindow.SpeedWindowClosedAtKnown = true;
        int countdownMinutes;
        float countdownRatio;
        if (!TryGetCodexRadarSpeedWindowCountdown(
                countdownWindow,
                countdownNow,
                out countdownMinutes,
                out countdownRatio) ||
            countdownMinutes != 1200 ||
            Math.Abs(countdownRatio - 0.5f) > 0.001f ||
            !string.Equals(FormatSpeedWindowCountdownTime(countdownMinutes), "20:00", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Speed-window countdown should use opened_at/closed_at progress.");
        }

        countdownWindow.SpeedWindowOpenedAtLocal = countdownNow.AddHours(-30.0);
        countdownWindow.SpeedWindowClosedAtLocal = countdownNow.AddHours(120.0);
        if (!TryGetCodexRadarSpeedWindowCountdown(
                countdownWindow,
                countdownNow,
                out countdownMinutes,
                out countdownRatio) ||
            countdownMinutes != 6000 ||
            Math.Abs(countdownRatio - 1.0f) > 0.001f ||
            !string.Equals(FormatSpeedWindowCountdownTime(countdownMinutes), "100:00", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Speed-window countdown should clamp display and ring basis to 100 hours.");
        }

        CodexRadarSnapshot mergedWindow = countdownWindow.Clone();
        CodexRadarSnapshot sourceWithoutClose = CodexRadarSnapshot.CreateDefault();
        sourceWithoutClose.SpeedWindowKnown = true;
        sourceWithoutClose.SpeedWindowOpen = true;
        sourceWithoutClose.SpeedWindowStatus = "open";
        CopyCodexRadarWindowSnapshot(mergedWindow, sourceWithoutClose);
        if (mergedWindow.SpeedWindowClosedAtKnown ||
            TryGetCodexRadarSpeedWindowCountdown(
                mergedWindow,
                countdownNow,
                out countdownMinutes,
                out countdownRatio))
        {
            throw new InvalidOperationException("Explicit open snapshot without closed_at should clear an older countdown target.");
        }
    }

    private static void RunRadarFamilyRuntimeIsolationSelfTest()
    {
        QuotaRuntimeState codexQuota = new QuotaRuntimeState();
        QuotaRuntimeState claudeQuota = new QuotaRuntimeState();
        DateTime sourceUtc = new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc);
        CodexQuotaSnapshot codexInitial = CreateRuntimeIsolationQuotaSnapshot(67, 13, sourceUtc);
        CodexQuotaSnapshot claudeInitial = CreateRuntimeIsolationQuotaSnapshot(88, 44, sourceUtc.AddMinutes(1.0));

        QuotaRingDecisionInfo codexInitialDecision = UpdateQuotaReadDeltaTracking(codexQuota, codexInitial, true);
        codexQuota.Snapshot = codexInitial.Clone();
        codexQuota.SourceKnown = true;
        if (codexQuota.LastFiveHourReadPercent != 67 ||
            codexQuota.LastWeeklyReadPercent != 13 ||
            claudeQuota.LastFiveHourReadPercent != -1 ||
            !string.Equals(codexInitialDecision.Reason, "initial_sample_set_tracking_baseline", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: Codex initial quota wrote into Claude state.");
        }

        QuotaRingDecisionInfo claudeInitialDecision = UpdateQuotaReadDeltaTracking(claudeQuota, claudeInitial, true);
        claudeQuota.Snapshot = claudeInitial.Clone();
        claudeQuota.SourceKnown = true;
        if (claudeQuota.LastFiveHourReadPercent != 88 ||
            claudeQuota.LastWeeklyReadPercent != 44 ||
            codexQuota.LastFiveHourReadPercent != 67 ||
            codexQuota.LastWeeklyReadPercent != 13 ||
            !string.Equals(claudeInitialDecision.Reason, "initial_sample_set_tracking_baseline", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: Claude initial quota changed Codex state.");
        }

        codexQuota.LastRefreshUtc = sourceUtc.AddMinutes(10.0);
        codexQuota.NextInactiveRefreshUtc = sourceUtc.AddMinutes(20.0);
        claudeQuota.LastRefreshUtc = sourceUtc.AddMinutes(30.0);
        claudeQuota.NextInactiveRefreshUtc = sourceUtc.AddMinutes(40.0);
        if (codexQuota.LastRefreshUtc == claudeQuota.LastRefreshUtc ||
            codexQuota.NextInactiveRefreshUtc == claudeQuota.NextInactiveRefreshUtc)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: quota refresh schedule is shared across families.");
        }

        CodexQuotaSnapshot claudeDrop = CreateRuntimeIsolationQuotaSnapshot(78, 44, sourceUtc.AddMinutes(2.0));
        QuotaRingDecisionInfo claudeDropDecision = UpdateQuotaReadDeltaTracking(claudeQuota, claudeDrop, true);
        if (claudeQuota.FiveHourConsumptionRingBaselinePercent != 88 ||
            codexQuota.FiveHourConsumptionRingBaselinePercent != -1 ||
            claudeDropDecision.NextFiveHourBaselinePercent != 88)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: Claude consumption baseline leaked or was not retained.");
        }

        CodexQuotaSnapshot claudeDuplicate = CreateRuntimeIsolationQuotaSnapshot(78, 44, sourceUtc.AddMinutes(2.0));
        QuotaRingDecisionInfo claudeDuplicateDecision = UpdateQuotaReadDeltaTracking(claudeQuota, claudeDuplicate, true);
        if (claudeQuota.FiveHourConsumptionRingBaselinePercent != 88 ||
            !string.Equals(claudeDuplicateDecision.Reason, "duplicate_source_same_balance_keep_existing_rings", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: duplicate Claude sample did not preserve consumption ring.");
        }

        if (LoadCodexRadarCache(CodexRadarSoftwareMode.Claude, "legacy-community-model") != null)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: retired Claude Radar cache was accepted.");
        }

        CodexQuotaSnapshot codexNearFull = CreateRuntimeIsolationQuotaSnapshot(99, 13, sourceUtc.AddMinutes(3.0));
        UpdateQuotaReadDeltaTracking(claudeQuota, codexNearFull, true);
        if (codexQuota.LastFiveHourReadPercent != 67 ||
            codexQuota.FiveHourConsumptionRingBaselinePercent != -1)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: non-Codex near-full sample affected Codex quota state.");
        }

        RadarFamilyRuntimeState codexState = new RadarFamilyRuntimeState(CodexRadarSoftwareMode.Codex);
        RadarFamilyRuntimeState claudeState = new RadarFamilyRuntimeState(CodexRadarSoftwareMode.Claude);
        codexState.RadarSiteHealth = ServiceHealthState.Unavailable;
        if (codexState.RadarSiteHealth != ServiceHealthState.Unavailable ||
            claudeState.RadarSiteHealth != ServiceHealthState.Unknown)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: Claude acquired public Radar health state.");
        }

    }

    private static CodexQuotaSnapshot CreateRuntimeIsolationQuotaSnapshot(int fiveHourPercent, int weeklyPercent, DateTime sourceUtc)
    {
        CodexQuotaSnapshot snapshot = CodexQuotaSnapshot.CreateDefault();
        snapshot.FiveHourPercent = ClampPercent(fiveHourPercent);
        snapshot.WeeklyPercent = ClampPercent(weeklyPercent);
        snapshot.FiveHourResetKnown = true;
        snapshot.WeeklyResetKnown = true;
        snapshot.FiveHourResetLocal = new DateTime(2026, 7, 8, 15, 0, 0, DateTimeKind.Local);
        snapshot.WeeklyResetLocal = new DateTime(2026, 7, 12, 15, 0, 0, DateTimeKind.Local);
        snapshot.SourceUpdatedKnown = true;
        snapshot.SourceUpdatedUtc = sourceUtc;
        MarkQuotaSnapshotSource(snapshot, "self_test");
        return snapshot;
    }

    private static void RunCodexModelIqDynamicScaleSelfTest()
    {
        string json =
            "{\"schema_version\":\"2.0\",\"model_iq\":{\"latest\":{\"date\":\"2026-07-07-am\",\"score\":90,\"status\":\"yellow\",\"passed\":6,\"tasks\":10,\"valid_tasks\":10}," +
            "\"comparisons\":{\"gpt_55_high\":{\"latest\":{\"date\":\"2026-07-07-am\",\"score\":90,\"passed\":6,\"tasks\":10,\"valid_tasks\":10}," +
            "\"recent_days\":[{\"date\":\"2026-07-06-pm\",\"score\":120,\"passed\":8,\"tasks\":10,\"valid_tasks\":10}]}}}}";
        CodexRadarSnapshot snapshot;
        CodexRadarModelCatalogUpdate update;
        if (!TryParseCodexRadarStatus(json, CodexRadarModelCatalog.DefaultModelKey, false, out snapshot, out update) ||
            snapshot == null ||
            !snapshot.ModelIqDisplayMaxScoreKnown ||
            Math.Abs(snapshot.ModelIqDisplayMaxScore - 120.0) > 0.001)
        {
            throw new InvalidOperationException("Codex IQ display maximum should follow the website model_iq scores.");
        }

        double displayMax = GetCodexModelIqDisplayMaxScore(snapshot, 105.0);
        if (Math.Abs(displayMax - 120.0) > 0.001)
        {
            throw new InvalidOperationException("Codex IQ ring should use the website display maximum instead of a fixed scale.");
        }
    }

    private static void RunCodexRadarNotificationStateSelfTest()
    {
        Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CodexRadarModelCatalogUpdate update = new CodexRadarModelCatalogUpdate();
        update.Added.Add(new CodexRadarModelInfo { Key = "GPT-5.5-high", Label = "GPT-5.5 high", Available = true });
        update.Added.Add(new CodexRadarModelInfo { Key = "gpt_55_high", Label = "GPT-5.5 high", Available = true });

        CodexRadarModelCatalogUpdate emitted = ApplyCodexRadarModelCatalogNotificationState(update, state);
        if (emitted.Added.Count != 1)
        {
            throw new InvalidOperationException("Codex model notification state did not de-duplicate same-batch additions.");
        }

        if (ApplyCodexRadarModelCatalogNotificationState(update, state).Added.Count != 0)
        {
            throw new InvalidOperationException("Codex model notification state repeated an unchanged added event.");
        }

        Dictionary<string, string> restartedState = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
        if (ApplyCodexRadarModelCatalogNotificationState(update, restartedState).Added.Count != 0)
        {
            throw new InvalidOperationException("Codex model notification state repeated after restart.");
        }

        CodexRadarModelCatalogUpdate deleted = new CodexRadarModelCatalogUpdate();
        deleted.Deleted.Add(new CodexRadarModelInfo { Key = "gpt_55_high", Label = "GPT-5.5 high", Available = false });
        if (ApplyCodexRadarModelCatalogNotificationState(deleted, restartedState).Deleted.Count != 1)
        {
            throw new InvalidOperationException("Codex model notification state did not emit a deleted state change.");
        }

        if (ApplyCodexRadarModelCatalogNotificationState(deleted, restartedState).Deleted.Count != 0)
        {
            throw new InvalidOperationException("Codex model notification state repeated an unchanged deleted event.");
        }

        if (ApplyCodexRadarModelCatalogNotificationState(update, restartedState).Added.Count != 1)
        {
            throw new InvalidOperationException("Codex model notification state did not emit deleted then re-added state change.");
        }

        Dictionary<string, string> conflictState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CodexRadarModelCatalogUpdate conflict = new CodexRadarModelCatalogUpdate();
        conflict.Deleted.Add(new CodexRadarModelInfo { Key = "gpt_55_low", Label = "GPT-5.5 low", Available = false });
        conflict.Added.Add(new CodexRadarModelInfo { Key = "gpt-5.5-low", Label = "GPT-5.5 low", Available = true });
        CodexRadarModelCatalogUpdate conflictEmitted = ApplyCodexRadarModelCatalogNotificationState(conflict, conflictState);
        if (conflictEmitted.Added.Count != 1 ||
            conflictEmitted.Deleted.Count != 0 ||
            ApplyCodexRadarModelCatalogNotificationState(conflict, conflictState).Added.Count != 0)
        {
            throw new InvalidOperationException("Codex model notification state did not coalesce same-batch conflicting events.");
        }
    }

    private static void RunCodexRadarCatalogCompletenessSelfTest()
    {
        Dictionary<string, object> comparisons = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "gpt_56_terra_medium", new Dictionary<string, object>() },
            { "gpt_57_nova_high", new Dictionary<string, object>() }
        };
        Dictionary<string, object> modelIq = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "latest", new Dictionary<string, object>() },
            { "comparisons", comparisons }
        };
        List<CodexRadarModelInfo> extracted = ExtractCodexRadarModelCatalog(modelIq);
        if (!IsCodexRadarCompleteCatalog(modelIq, extracted))
        {
            throw new InvalidOperationException("Complete Codex Radar JSON catalog was rejected by the completeness gate.");
        }

        comparisons["gpt_5_7_nova_high"] = new Dictionary<string, object>();
        extracted = ExtractCodexRadarModelCatalog(modelIq);
        if (IsCodexRadarCompleteCatalog(modelIq, extracted))
        {
            throw new InvalidOperationException("Normalized Codex Radar catalog key collision was accepted as complete.");
        }
    }

    private static void RunCodexModelIqRefreshMarkerSelfTest()
    {
        CodexRadarSnapshot source = BuildModelIqRefreshMarkerTestSnapshot(88, new DateTime(2026, 7, 7, 12, 10, 0));
        CodexRadarSnapshot sameContent = BuildModelIqRefreshMarkerTestSnapshot(88, new DateTime(2026, 7, 7, 13, 10, 0));
        PreserveCodexModelIqRefreshTimeIfContentUnchanged(sameContent, source);
        if (sameContent.ModelIqRefreshedAtLocal != source.ModelIqRefreshedAtLocal)
        {
            throw new InvalidOperationException("Identical Codex IQ content should preserve the first-seen refresh marker time.");
        }

        CodexRadarSnapshot changedContent = BuildModelIqRefreshMarkerTestSnapshot(89, new DateTime(2026, 7, 7, 13, 10, 0));
        PreserveCodexModelIqRefreshTimeIfContentUnchanged(changedContent, source);
        if (changedContent.ModelIqRefreshedAtLocal == source.ModelIqRefreshedAtLocal)
        {
            throw new InvalidOperationException("Changed Codex IQ content should keep the new refresh marker time.");
        }

        // RDR-02: the same batch fetched through JSON vs HTML/history normalizes derived efficiency,
        // normal range and display-max differently. Those must not move the first-seen marker.
        CodexRadarSnapshot derivedOnlyDifference = BuildModelIqRefreshMarkerTestSnapshot(88, new DateTime(2026, 7, 7, 16, 10, 0));
        derivedOnlyDifference.ModelIqTokenEfficiencyPercent = source.ModelIqTokenEfficiencyPercent + 7;
        derivedOnlyDifference.ModelIqTimeEfficiencyPercent = source.ModelIqTimeEfficiencyPercent - 5;
        derivedOnlyDifference.ModelIqEfficiencyTotalTokens = source.ModelIqEfficiencyTotalTokens + 1234567.0;
        derivedOnlyDifference.ModelIqEfficiencySerialSeconds = source.ModelIqEfficiencySerialSeconds + 42.0;
        derivedOnlyDifference.ModelIqNormalLowScore = source.ModelIqNormalLowScore - 3;
        derivedOnlyDifference.ModelIqNormalHighScore = source.ModelIqNormalHighScore + 3;
        derivedOnlyDifference.ModelIqNormalRangeKnown = true;
        ApplyCodexModelIqDisplayMax(derivedOnlyDifference, 135.0);
        PreserveCodexModelIqRefreshTimeIfContentUnchanged(derivedOnlyDifference, source);
        if (derivedOnlyDifference.ModelIqRefreshedAtLocal != source.ModelIqRefreshedAtLocal)
        {
            throw new InvalidOperationException("Derived-only Codex IQ differences must not move the first-seen refresh marker.");
        }

        CodexRadarSnapshot enrichedCached = source.Clone();
        enrichedCached.ModelIqCachedContentSignature = BuildCodexModelIqContentSignature(source);
        enrichedCached.ModelIqPassedKnown = false;
        CodexRadarSnapshot sameAfterRestart = BuildModelIqRefreshMarkerTestSnapshot(88, new DateTime(2026, 7, 7, 14, 10, 0));
        PreserveCodexModelIqRefreshTimeIfContentUnchanged(sameAfterRestart, enrichedCached);
        if (sameAfterRestart.ModelIqRefreshedAtLocal != source.ModelIqRefreshedAtLocal)
        {
            throw new InvalidOperationException("Persisted Codex IQ signature did not survive cache enrichment across restart.");
        }

        CodexRadarSnapshot changedAfterRestart = BuildModelIqRefreshMarkerTestSnapshot(89, new DateTime(2026, 7, 7, 14, 10, 0));
        PreserveCodexModelIqRefreshTimeIfContentUnchanged(changedAfterRestart, enrichedCached);
        if (changedAfterRestart.ModelIqRefreshedAtLocal == source.ModelIqRefreshedAtLocal)
        {
            throw new InvalidOperationException("Persisted Codex IQ signature hid a real content change.");
        }

        enrichedCached.ModelIqCachedContentSignature = string.Empty;
        CodexRadarSnapshot legacyCacheTarget = BuildModelIqRefreshMarkerTestSnapshot(88, new DateTime(2026, 7, 7, 15, 10, 0));
        PreserveCodexModelIqRefreshTimeIfContentUnchanged(legacyCacheTarget, enrichedCached);
        if (legacyCacheTarget.ModelIqRefreshedAtLocal == source.ModelIqRefreshedAtLocal)
        {
            throw new InvalidOperationException("Legacy cache without a signature did not use the computed fallback.");
        }
    }

    private static void RunCodexRadarCacheHardeningSelfTest()
    {
        if (!string.Equals(GetLegacyCodexRadarCachePrefix(string.Empty), "Model.default.", StringComparison.Ordinal) ||
            !string.Equals(GetLegacyCodexRadarCachePrefix(null), "Model.default.", StringComparison.Ordinal) ||
            GetCodexRadarCachePrefix(string.Empty).IndexOf("Model." + ".", StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("Codex Radar empty cache key did not use the default sentinel.");
        }

        CodexRadarSnapshot source = BuildModelIqRefreshMarkerTestSnapshot(88, new DateTime(2026, 7, 7, 12, 10, 0));
        source.CheckedAtKnown = true;
        source.CheckedAtLocal = new DateTime(2026, 7, 7, 12, 5, 0, DateTimeKind.Local);
        source.ModelIqDataWindowKnown = false;
        source.ResetRadarKnown = true;
        source.ResetRadarUpdatedAtKnown = true;
        source.ResetRadarUpdatedAtLocal = new DateTime(2026, 7, 7, 10, 24, 0, DateTimeKind.Local);
        source.ResetCardStatus = "低";
        source.ResetCardDescription = "本轮是直接重置，不是发新卡";
        source.HardResetStatus = "已落地";
        source.HardResetDescription = "10M 里程碑重置完成 · 下一次低";
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        WriteCodexRadarCacheHardeningValues(values, "Test.", source);
        CodexRadarSnapshot loaded = CodexRadarSnapshot.CreateDefault();
        ApplyCodexRadarCacheHardeningValues(values, "Test.", loaded);
        if (loaded.ModelIqDataWindowKnown ||
            !loaded.CheckedAtKnown ||
            loaded.CheckedAtLocal != source.CheckedAtLocal ||
            !loaded.ResetRadarKnown ||
            !loaded.ResetRadarUpdatedAtKnown ||
            loaded.ResetRadarUpdatedAtLocal != source.ResetRadarUpdatedAtLocal ||
            !string.Equals(loaded.ResetCardDescription, source.ResetCardDescription, StringComparison.Ordinal) ||
            !string.Equals(loaded.HardResetDescription, source.HardResetDescription, StringComparison.Ordinal) ||
            !string.Equals(loaded.ModelIqCachedContentSignature, BuildCodexModelIqContentSignature(source), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex Radar cache hardening values did not round-trip faithfully.");
        }
    }

    private static void RunClaudeQuotaCacheCompletenessSelfTest()
    {
        if (!IsClaudeSetupTokenMissing("NO_SETUP_TOKEN") ||
            !IsClaudeSetupTokenMissing("no_token") ||
            IsClaudeSetupTokenMissing("429") ||
            IsClaudeSetupTokenMissing(string.Empty))
        {
            throw new InvalidOperationException("Claude setup-token error classification changed.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "desktopcodex-claude-quota-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, ClaudeCodeUsageReader.QuotaCacheFileName);
            DateTime nowUtc = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
            string prefix =
                "Version=1\n" +
                "FiveHourPercent=45\n" +
                "WeeklyPercent=67\n" +
                "FiveHourReset=2026-07-22T10:00:00+09:00\n";
            File.WriteAllText(
                path,
                prefix + "SourceUpdatedUtc=" + nowUtc.ToString("o", CultureInfo.InvariantCulture) + "\n",
                SharedEncoding.Utf8NoBom);

            CodexQuotaSnapshot restored;
            if (TryReadQuotaIniSnapshot(
                    CodexRadarSoftwareMode.Claude,
                    path,
                    nowUtc,
                    out restored))
            {
                throw new InvalidOperationException("Claude partial quota cache with a missing reset was restored.");
            }

            File.WriteAllText(
                path,
                prefix +
                "WeeklyReset=2026-07-28T12:30:00+09:00\n" +
                "SourceUpdatedUtc=" + nowUtc.ToString("o", CultureInfo.InvariantCulture) + "\n",
                SharedEncoding.Utf8NoBom);
            if (!TryReadQuotaIniSnapshot(
                    CodexRadarSoftwareMode.Claude,
                    path,
                    nowUtc,
                    out restored) ||
                restored == null ||
                !restored.FiveHourResetKnown ||
                !restored.WeeklyResetKnown ||
                !restored.SourceUpdatedKnown)
            {
                throw new InvalidOperationException("Claude complete quota cache restore self-test failed.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static void RunCodexQuotaIdentityHardeningSelfTest()
    {
        DateTime sampleLocal = new DateTime(2026, 7, 11, 0, 50, 6, DateTimeKind.Local);
        DateTime sampleUtc = sampleLocal.ToUniversalTime();
        DateTime tracked = new DateTime(2026, 7, 11, 3, 4, 22, DateTimeKind.Local);
        QuotaWindowIdentityDecision phantom = EvaluateQuotaWindowIdentity(
            tracked,
            new DateTime(2026, 7, 11, 4, 11, 51, DateTimeKind.Local),
            99,
            "provider",
            sampleUtc,
            sampleUtc.AddMinutes(-3),
            DateTime.MinValue,
            TimeSpan.FromHours(5));
        AssertQuotaIdentity(!phantom.Accepted && phantom.Reason == "interference_pool_sample_ignored", "phantom pool sample");

        QuotaRuntimeState phantomState = new QuotaRuntimeState();
        phantomState.LastFiveHourReadPercent = 8;
        phantomState.LastWeeklyReadPercent = 60;
        phantomState.LastReadSourceUtc = sampleUtc.AddMinutes(-3);
        phantomState.TrackedFiveHourResetLocal = tracked;
        phantomState.TrackedWeeklyResetLocal = sampleLocal.AddDays(2);
        CodexQuotaSnapshot phantomSnapshot = CodexQuotaSnapshot.CreateDefault();
        phantomSnapshot.FiveHourPercent = 97;
        phantomSnapshot.WeeklyPercent = 99;
        phantomSnapshot.FiveHourResetKnown = true;
        phantomSnapshot.FiveHourResetLocal = new DateTime(2026, 7, 11, 4, 11, 51, DateTimeKind.Local);
        phantomSnapshot.WeeklyResetKnown = true;
        phantomSnapshot.WeeklyResetLocal = sampleLocal.AddDays(3);
        phantomSnapshot.SourceUpdatedKnown = true;
        phantomSnapshot.SourceUpdatedUtc = sampleUtc;
        MarkQuotaSnapshotSource(phantomSnapshot, "provider");
        QuotaRingDecisionInfo phantomDecision = UpdateQuotaReadDeltaTracking(
            phantomState, phantomSnapshot, true, QuotaProtectionOptions.LegacyRuntimeDefaults(), true, DateTime.MinValue);
        AssertQuotaIdentity(
            phantomDecision.IdentitySampleRejected &&
            phantomDecision.Reason == "interference_pool_sample_ignored" &&
            phantomState.LastFiveHourReadPercent == 8 &&
            phantomState.LastWeeklyReadPercent == 60 &&
            phantomSnapshot.FiveHourPercent == 8 &&
            phantomSnapshot.WeeklyPercent == 60,
            "phantom sample full-state rejection");

        // Legitimate newborns only appear when the tracked window is about to expire; both cases
        // model the observed +1s/+33s provider clock skew right at the boundary.
        DateTime resetLocal = new DateTime(2026, 7, 11, 8, 4, 30, DateTimeKind.Local);
        DateTime resetUtc = resetLocal.ToUniversalTime();
        QuotaWindowIdentityDecision newbornFive = EvaluateQuotaWindowIdentity(
            resetLocal.AddMinutes(1),
            resetLocal.AddHours(5).AddSeconds(1),
            100,
            "provider",
            resetUtc,
            resetUtc.AddMinutes(-3),
            DateTime.MinValue,
            TimeSpan.FromHours(5));
        QuotaWindowIdentityDecision newbornWeek = EvaluateQuotaWindowIdentity(
            resetLocal.AddMinutes(1),
            resetLocal.AddDays(7).AddSeconds(33),
            100,
            "provider",
            resetUtc,
            resetUtc.AddMinutes(-3),
            DateTime.MinValue,
            TimeSpan.FromDays(7));
        AssertQuotaIdentity(newbornFive.Accepted && newbornFive.Reason == "reset_confirmed_by_newborn" &&
            newbornWeek.Accepted && newbornWeek.Reason == "reset_confirmed_by_newborn", "newborn reset");

        // A newborn-shaped sample while the tracked window still has hours to run is the idle pool,
        // regardless of source; the session and gap paths must not re-anchor to it either.
        QuotaWindowIdentityDecision farFromExpiry = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(5).AddSeconds(1), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-3), DateTime.MinValue, TimeSpan.FromHours(5));
        QuotaWindowIdentityDecision sessionNewborn = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(5).AddSeconds(1), 100, "session", sampleUtc,
            sampleUtc.AddMinutes(-3), DateTime.MinValue, TimeSpan.FromHours(5));
        QuotaWindowIdentityDecision gapNewborn = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(5).AddSeconds(1), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-40), DateTime.MinValue, TimeSpan.FromHours(5));
        AssertQuotaIdentity(
            !farFromExpiry.Accepted && farFromExpiry.Reason == "idle_pool_newborn_suppressed" &&
            !sessionNewborn.Accepted && sessionNewborn.Reason == "idle_pool_newborn_suppressed" &&
            !gapNewborn.Accepted && gapNewborn.Reason == "idle_pool_newborn_suppressed",
            "far-from-expiry newborn blocked on all sources");

        DateTime nearDueTracked = sampleLocal.AddMinutes(1);
        QuotaWindowIdentityDecision boundarySeven = EvaluateQuotaWindowIdentity(
            nearDueTracked, sampleLocal.AddHours(5).AddMinutes(-7), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-3), DateTime.MinValue, TimeSpan.FromHours(5));
        QuotaWindowIdentityDecision boundaryNine = EvaluateQuotaWindowIdentity(
            nearDueTracked, sampleLocal.AddHours(5).AddMinutes(-9), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-3), DateTime.MinValue, TimeSpan.FromHours(5));
        AssertQuotaIdentity(boundarySeven.Accepted && !boundaryNine.Accepted, "newborn tolerance boundary");

        QuotaWindowIdentityDecision expired = EvaluateQuotaWindowIdentity(
            sampleLocal.AddMinutes(-1), sampleLocal.AddHours(4), 90, "provider", sampleUtc,
            sampleUtc.AddMinutes(-3), DateTime.MinValue, TimeSpan.FromHours(5));
        AssertQuotaIdentity(expired.Accepted && expired.Reason == "reset_confirmed_by_expiry", "expired window");

        QuotaWindowIdentityDecision eventConfirmed = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(4), 90, "provider", sampleUtc,
            sampleUtc.AddMinutes(-3), sampleUtc.AddHours(-1), TimeSpan.FromHours(5));
        AssertQuotaIdentity(eventConfirmed.Accepted && eventConfirmed.Reason == "reset_confirmed_by_event", "reset event");

        QuotaWindowIdentityDecision sessionConfirmed = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(4), 90, "session", sampleUtc,
            sampleUtc.AddMinutes(-3), DateTime.MinValue, TimeSpan.FromHours(5));
        AssertQuotaIdentity(sessionConfirmed.Accepted && sessionConfirmed.Reason == "reset_confirmed_by_session", "session source");

        QuotaWindowIdentityDecision gapForty = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(4), 90, "provider", sampleUtc,
            sampleUtc.AddMinutes(-40), DateTime.MinValue, TimeSpan.FromHours(5));
        QuotaWindowIdentityDecision gapTwenty = EvaluateQuotaWindowIdentity(
            tracked, sampleLocal.AddHours(4), 90, "provider", sampleUtc,
            sampleUtc.AddMinutes(-20), DateTime.MinValue, TimeSpan.FromHours(5));
        AssertQuotaIdentity(gapForty.Accepted && gapForty.Reason == "gap_rebaseline" && !gapTwenty.Accepted,
            "gap rebaseline boundary");

        DateTime wrongTracked = sampleLocal.AddHours(2.0);
        DateTime realReset = sampleLocal.AddHours(4.0);
        RejectedIdentityPersistenceState persistentReal = new RejectedIdentityPersistenceState();
        QuotaWindowIdentityDecision persistentFirst = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 22, "provider", sampleUtc, sampleUtc.AddMinutes(-3),
            DateTime.MinValue, TimeSpan.FromHours(5), persistentReal);
        QuotaWindowIdentityDecision persistentSecond = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 21, "provider", sampleUtc.AddMinutes(5), sampleUtc.AddMinutes(2),
            DateTime.MinValue, TimeSpan.FromHours(5), persistentReal);
        QuotaWindowIdentityDecision persistentThird = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 20, "provider", sampleUtc.AddMinutes(10), sampleUtc.AddMinutes(7),
            DateTime.MinValue, TimeSpan.FromHours(5), persistentReal);
        AssertQuotaIdentity(
            !persistentFirst.Accepted && !persistentSecond.Accepted &&
            persistentThird.Accepted &&
            persistentThird.Reason == "reset_confirmed_by_rejected_persistence" &&
            persistentThird.RejectedPersistenceCount == QuotaRejectedPersistenceMinSamples &&
            persistentThird.RejectedPersistenceFirstSeenUtc == sampleUtc,
            "rejected persistence repairs wrong tracked identity");

        RejectedIdentityPersistenceState interrupted = new RejectedIdentityPersistenceState();
        QuotaWindowIdentityDecision interruptedPhantom = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 99, "provider", sampleUtc, sampleUtc.AddMinutes(-3),
            DateTime.MinValue, TimeSpan.FromHours(5), interrupted);
        QuotaWindowIdentityDecision acceptedSame = EvaluateQuotaWindowIdentity(
            wrongTracked, wrongTracked, 20, "provider", sampleUtc.AddMinutes(5), sampleUtc.AddMinutes(2),
            DateTime.MinValue, TimeSpan.FromHours(5), interrupted);
        QuotaWindowIdentityDecision interruptedAgain = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 99, "provider", sampleUtc.AddMinutes(10), sampleUtc.AddMinutes(7),
            DateTime.MinValue, TimeSpan.FromHours(5), interrupted);
        // Same-identity accepts must PRESERVE the rejection streak: with two alternating provider
        // pools the phantom's accepted samples used to wipe the real pool's count on every read,
        // which is why the count>=3 repair never fired in production.
        AssertQuotaIdentity(
            !interruptedPhantom.Accepted && acceptedSame.Accepted && !interruptedAgain.Accepted &&
            interruptedAgain.RejectedPersistenceCount == 2,
            "identity-same accept preserves rejected persistence");

        RejectedIdentityPersistenceState alternating = new RejectedIdentityPersistenceState();
        DateTime alternateReset = realReset.AddMinutes(8.0);
        QuotaWindowIdentityDecision alternateA1 = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 90, "provider", sampleUtc, sampleUtc.AddMinutes(-3),
            DateTime.MinValue, TimeSpan.FromHours(5), alternating);
        QuotaWindowIdentityDecision alternateB = EvaluateQuotaWindowIdentity(
            wrongTracked, alternateReset, 90, "provider", sampleUtc.AddMinutes(5), sampleUtc.AddMinutes(2),
            DateTime.MinValue, TimeSpan.FromHours(5), alternating);
        QuotaWindowIdentityDecision alternateA2 = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 90, "provider", sampleUtc.AddMinutes(10), sampleUtc.AddMinutes(7),
            DateTime.MinValue, TimeSpan.FromHours(5), alternating);
        QuotaWindowIdentityDecision alternateGap = EvaluateQuotaWindowIdentity(
            wrongTracked, alternateReset, 90, "provider", sampleUtc.AddMinutes(40), sampleUtc,
            DateTime.MinValue, TimeSpan.FromHours(5), alternating);
        AssertQuotaIdentity(
            !alternateA1.Accepted && !alternateB.Accepted && !alternateA2.Accepted &&
            alternateA2.RejectedPersistenceCount == 1 &&
            alternateGap.Accepted && alternateGap.Reason == "gap_rebaseline",
            "alternating rejected identities require gap rebaseline");

        // Idle-pool suppression: a newborn-shaped sample minutes after the displayed pool was seen
        // consuming is the sliding idle pool, not a real reset.
        DateTime midWindowTracked = sampleLocal.AddHours(3);
        QuotaWindowIdentityDecision idleAfterConsumption = EvaluateQuotaWindowIdentity(
            midWindowTracked, sampleLocal.AddHours(5).AddSeconds(20), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-2), DateTime.MinValue, TimeSpan.FromHours(5), null,
            sampleUtc.AddMinutes(-10), DateTime.MinValue);
        QuotaWindowIdentityDecision idleReconfirm = EvaluateQuotaWindowIdentity(
            midWindowTracked, sampleLocal.AddHours(5).AddSeconds(20), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-2), DateTime.MinValue, TimeSpan.FromHours(5), null,
            DateTime.MinValue, sampleUtc.AddMinutes(-4));
        // Even a full window after the last newborn accept, a mid-window tracked anchor still
        // blocks the newborn shape: only near-expiry samples qualify (covered by "newborn reset").
        QuotaWindowIdentityDecision newbornAfterFullWindow = EvaluateQuotaWindowIdentity(
            midWindowTracked, sampleLocal.AddHours(5).AddSeconds(20), 100, "provider", sampleUtc,
            sampleUtc.AddMinutes(-2), DateTime.MinValue, TimeSpan.FromHours(5), null,
            DateTime.MinValue, sampleUtc.AddHours(-6));
        AssertQuotaIdentity(
            !idleAfterConsumption.Accepted && idleAfterConsumption.Reason == "idle_pool_newborn_suppressed" &&
            !idleReconfirm.Accepted && idleReconfirm.Reason == "idle_pool_newborn_suppressed" &&
            !newbornAfterFullWindow.Accepted && newbornAfterFullWindow.Reason == "idle_pool_newborn_suppressed",
            "idle-pool newborn suppression");

        // Newborn-shaped rejections must not clobber the real pool's persistence streak: the real
        // mid-window pool has to reach count>=3 and be adopted even with idle samples interleaved.
        RejectedIdentityPersistenceState mixed = new RejectedIdentityPersistenceState();
        QuotaWindowIdentityDecision mixedRealFirst = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 70, "provider", sampleUtc, sampleUtc.AddMinutes(-3),
            DateTime.MinValue, TimeSpan.FromHours(5), mixed);
        QuotaWindowIdentityDecision mixedIdle = EvaluateQuotaWindowIdentity(
            wrongTracked, sampleLocal.AddMinutes(5).AddHours(5), 100, "provider", sampleUtc.AddMinutes(5), sampleUtc.AddMinutes(2),
            DateTime.MinValue, TimeSpan.FromHours(5), mixed, sampleUtc, DateTime.MinValue);
        QuotaWindowIdentityDecision mixedRealSecond = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 69, "provider", sampleUtc.AddMinutes(7), sampleUtc.AddMinutes(5),
            DateTime.MinValue, TimeSpan.FromHours(5), mixed);
        QuotaWindowIdentityDecision mixedRealThird = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 68, "provider", sampleUtc.AddMinutes(11), sampleUtc.AddMinutes(7),
            DateTime.MinValue, TimeSpan.FromHours(5), mixed);
        AssertQuotaIdentity(
            !mixedRealFirst.Accepted && !mixedIdle.Accepted && !mixedRealSecond.Accepted &&
            mixedRealSecond.RejectedPersistenceCount == 2 &&
            mixedRealThird.Accepted &&
            mixedRealThird.Reason == "reset_confirmed_by_rejected_persistence",
            "idle newborn rejection preserves real pool persistence");

        // A dormant rejection streak restarts instead of combining with hours-old evidence.
        RejectedIdentityPersistenceState staleStreak = new RejectedIdentityPersistenceState();
        EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 70, "provider", sampleUtc, sampleUtc.AddMinutes(-3),
            DateTime.MinValue, TimeSpan.FromHours(5), staleStreak);
        QuotaWindowIdentityDecision staleSecond = EvaluateQuotaWindowIdentity(
            wrongTracked, realReset, 69, "provider", sampleUtc.AddMinutes(20), sampleUtc.AddMinutes(18),
            DateTime.MinValue, TimeSpan.FromHours(5), staleStreak);
        AssertQuotaIdentity(
            !staleSecond.Accepted && staleSecond.RejectedPersistenceCount == 1,
            "stale rejected persistence restarts");

        QuotaRuntimeState state = new QuotaRuntimeState();
        state.LastFiveHourReadPercent = 20;
        state.LastWeeklyReadPercent = 60;
        state.LastReadSourceUtc = sampleUtc.AddMinutes(-3);
        // Near-due tracked anchor so the newborn five-hour ring is legitimately accepted while the
        // unchanged weekly ring stays independent.
        state.TrackedFiveHourResetLocal = sampleLocal.AddMinutes(1);
        state.TrackedWeeklyResetLocal = sampleLocal.AddDays(3);
        CodexQuotaSnapshot partial = CodexQuotaSnapshot.CreateDefault();
        partial.FiveHourPercent = 100;
        partial.WeeklyPercent = 60;
        partial.FiveHourResetKnown = true;
        partial.FiveHourResetLocal = sampleLocal.AddHours(5).AddMinutes(-2);
        partial.WeeklyResetKnown = true;
        partial.WeeklyResetLocal = state.TrackedWeeklyResetLocal;
        partial.SourceUpdatedKnown = true;
        partial.SourceUpdatedUtc = sampleUtc;
        MarkQuotaSnapshotSource(partial, "provider");
        QuotaRingDecisionInfo partialDecision = UpdateQuotaReadDeltaTracking(
            state, partial, true, QuotaProtectionOptions.LegacyRuntimeDefaults(), true, DateTime.MinValue);
        AssertQuotaIdentity(state.LastFiveHourReadPercent == 100 && state.LastWeeklyReadPercent == 60 &&
            partialDecision.Reason.IndexOf("reset_confirmed_by_newborn", StringComparison.Ordinal) >= 0,
            "independent ring decision");
    }

    private static void AssertQuotaIdentity(bool condition, string scenario)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Codex quota identity hardening self-test failed: " + scenario + ".");
        }
    }

    private static CodexRadarSnapshot BuildModelIqRefreshMarkerTestSnapshot(int passRate, DateTime refreshedAtLocal)
    {
        CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
        snapshot.ModelIqKnown = true;
        snapshot.ModelIqPassedKnown = true;
        snapshot.ModelIqPassed = 6;
        snapshot.ModelIqValidTasks = CodexModelIqNominalTasks;
        snapshot.ModelIqPassRatePercent = passRate;
        snapshot.ModelIqStatus = "green";
        snapshot.ModelIqEfficiencyKnown = true;
        snapshot.ModelIqTokenEfficiencyPercent = 101;
        snapshot.ModelIqTimeEfficiencyPercent = 99;
        snapshot.ModelIqEfficiencyInputKnown = true;
        snapshot.ModelIqEfficiencyPassed = 6.0;
        snapshot.ModelIqEfficiencyTotalTokens = 42000000.0;
        snapshot.ModelIqEfficiencySerialSeconds = 204.0 * 60.0;
        snapshot.ModelIqDataDateLocal = new DateTime(2026, 7, 7);
        snapshot.ModelIqDataDateKnown = true;
        snapshot.ModelIqDataWindowStartHourLocal = 12;
        snapshot.ModelIqDataWindowKnown = true;
        snapshot.ModelIqDataLabel = "7.7_pm";
        snapshot.ModelIqDataLabelKnown = true;
        snapshot.ModelIqNormalLowScore = 90;
        snapshot.ModelIqNormalHighScore = 110;
        snapshot.ModelIqNormalRangeKnown = true;
        snapshot.ModelIqRefreshedAtLocal = refreshedAtLocal;
        snapshot.ModelIqRefreshedAtKnown = true;
        return snapshot;
    }

}
