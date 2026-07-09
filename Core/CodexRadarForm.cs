using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Owns the layered Codex monitor and schedules local quota reads plus selected
/// current.json model snapshots without performing blocking work in paint code.
/// </summary>
// Render-path variants live in sibling partial files (CodexRadarForm.Variant*.cs) so an
// experimental redesign can be reviewed, tested, and deleted without touching this file.
// WidgetSettings.CodexRadarRenderVariant selects which one DrawCodexRadarModules calls.
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
    private const string CodexRadarModelRatingsUrl = "https://codexradar.com/api/model-ratings?history=14";
    private const string CodexRadarFullApiUrl = "https://codexradar.com/api/v1/current";
    // Keep probes enabled because the compact one-line API summary consumes their states.
    private static readonly bool ServiceHealthProbeEnabled = true;
    private const int CodexApiServiceAlertDebounceSeconds = 10;
    private const int CodexModelIqNominalTasks = WidgetSettings.DefaultCodexModelIqBaselineValidTasks;
    private const int MaxCodexModelIqScore = 1000;
    private const double CodexModelIqWebsiteScoreScale = 150.0;
    private const int CodexModelIqWebsiteNormalLowScore = 90;
    private const int CodexModelIqWebsiteNormalHighScore = 110;
    private const int CodexRadarStatusTimeoutMs = 10000;
    private const int CodexModelHistoryDays = 366;
    private const int CodexModelCacheRetentionDays = 7;
    private const string QuotaRadarTierPlus = "plus";
    private const string QuotaRadarTierPro5x = "pro5x";
    private const string QuotaRadarTierPro20x = "pro20x";
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
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
    private readonly object codexApiServiceAlertDebounceLock = new object();
    private readonly object codexRadarNotificationStateLock = new object();
    private readonly Dictionary<string, string> codexRadarNotificationState =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private WidgetSettings currentSettings;
    private bool hiddenForFullscreen;
    private int renderTickCount;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime reverseHoverRevealUntilUtc;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool autoHideKeepAliveActive;
    private bool sharedInteractionPolling;
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
    private bool serviceNetworkAvailable = true;
    private ServiceHealthState openAiServiceHealth = ServiceHealthState.Unknown;
    private ServiceHealthState claudeServiceHealth = ServiceHealthState.Unknown;
    private bool serviceNetworkRefreshRequested = true;
    private string lastRadarClockAutoSwitchSignature = string.Empty;
    private FileSystemWatcher quotaSessionWatcher;
    private string quotaSessionsPath = string.Empty;
    private int quotaSessionFilesChanged = 1;
    private const int MaxCodexRadarSceneBitmapCacheEntries = 6;
    private readonly Dictionary<string, Bitmap> renderSceneBitmapCache = new Dictionary<string, Bitmap>();
    private readonly Queue<string> renderSceneBitmapCacheOrder = new Queue<string>();
    private int renderSceneSettingsRevision;
    private long burnInShiftSlot = long.MinValue;
    private readonly UiFontCache fontCache = new UiFontCache();
    private DateTime lastRenderedClockSecondLocal;

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

    private sealed class CodexConnectionAlertCandidate
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }
        public Color Color { get; set; }
    }

    private sealed class CodexConnectionAlertDebounceState
    {
        public string PendingSignature { get; set; }
        public DateTime PendingSinceUtc { get; set; }
        public string ActiveSignature { get; set; }
        public CodexConnectionAlertCandidate ActiveCandidate { get; set; }
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
                WeeklyUsageDiagnosticKnown = false
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
                WeeklyUsageDiagnosticKnown = this.WeeklyUsageDiagnosticKnown
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
        public int PreviousFiveHourPercent { get; set; } = -1;
        public int PreviousWeeklyPercent { get; set; } = -1;
        public DateTime PreviousSourceUpdatedUtc { get; set; }
        public int PreviousFiveHourBaselinePercent { get; set; } = -1;
        public int PreviousWeeklyBaselinePercent { get; set; } = -1;
        public DateTime PreviousTrackedFiveHourResetLocal { get; set; }
        public int NextFiveHourBaselinePercent { get; set; } = -1;
        public int NextWeeklyBaselinePercent { get; set; } = -1;
        public DateTime NextTrackedFiveHourResetLocal { get; set; }
        public DateTime NextSourceUpdatedUtc { get; set; }
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
        public DateTime ModelIqRefreshedAtLocal { get; set; }
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
        public List<RadarClockModelCandidate> ClockModelCandidates { get; set; }
        public CodexQuotaRadarSnapshot QuotaRadar { get; set; }
        public bool CommunityRatingKnown { get; set; }
        public string CommunityRatingModelId { get; set; }
        public string CommunityRatingLabel { get; set; }
        public double CommunityRatingAverage { get; set; }
        public int CommunityRatingCount { get; set; }
        public DateTime CommunityRatingUpdatedAtLocal { get; set; }

        public static CodexRadarSnapshot CreateDefault()
        {
            return new CodexRadarSnapshot
            {
                CheckedAtLocal = DateTime.MinValue,
                CheckedAtKnown = false,
                ModelIqRefreshedAtLocal = DateTime.MinValue,
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
                ClockModelCandidates = new List<RadarClockModelCandidate>(),
                QuotaRadar = CodexQuotaRadarSnapshot.CreateDefault(),
                CommunityRatingKnown = false,
                CommunityRatingModelId = string.Empty,
                CommunityRatingLabel = string.Empty,
                CommunityRatingAverage = 0.0,
                CommunityRatingCount = 0,
                CommunityRatingUpdatedAtLocal = DateTime.MinValue
            };
        }

        public CodexRadarSnapshot Clone()
        {
            return new CodexRadarSnapshot
            {
                CheckedAtLocal = this.CheckedAtLocal,
                CheckedAtKnown = this.CheckedAtKnown,
                ModelIqRefreshedAtLocal = this.ModelIqRefreshedAtLocal,
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
                ClockModelCandidates = CloneRadarClockModelCandidates(this.ClockModelCandidates),
                QuotaRadar = this.QuotaRadar == null
                    ? CodexQuotaRadarSnapshot.CreateDefault()
                    : this.QuotaRadar.Clone(),
                CommunityRatingKnown = this.CommunityRatingKnown,
                CommunityRatingModelId = this.CommunityRatingModelId,
                CommunityRatingLabel = this.CommunityRatingLabel,
                CommunityRatingAverage = this.CommunityRatingAverage,
                CommunityRatingCount = this.CommunityRatingCount,
                CommunityRatingUpdatedAtLocal = this.CommunityRatingUpdatedAtLocal
            };
        }
    }

    private sealed class RadarClockModelCandidate
    {
        public string Key { get; set; }
        public DateTime LatestLocal { get; set; }
        public bool LatestKnown { get; set; }

        public RadarClockModelCandidate Clone()
        {
            return new RadarClockModelCandidate
            {
                Key = this.Key ?? string.Empty,
                LatestLocal = this.LatestLocal,
                LatestKnown = this.LatestKnown
            };
        }
    }

    private sealed class CodexQuotaRadarSnapshot
    {
        public bool Known { get; set; }
        public DateTime UpdatedAtLocal { get; set; }
        public bool UpdatedAtKnown { get; set; }
        public CodexQuotaRadarTier[] Tiers { get; set; }

        public static CodexQuotaRadarSnapshot CreateDefault()
        {
            return new CodexQuotaRadarSnapshot
            {
                Known = false,
                UpdatedAtLocal = DateTime.MinValue,
                UpdatedAtKnown = false,
                Tiers = CreateDefaultCodexQuotaRadarTiers()
            };
        }

        public CodexQuotaRadarSnapshot Clone()
        {
            CodexQuotaRadarSnapshot clone = new CodexQuotaRadarSnapshot
            {
                Known = this.Known,
                UpdatedAtLocal = this.UpdatedAtLocal,
                UpdatedAtKnown = this.UpdatedAtKnown,
                Tiers = CreateDefaultCodexQuotaRadarTiers()
            };
            if (this.Tiers != null)
            {
                int count = Math.Min(clone.Tiers.Length, this.Tiers.Length);
                for (int i = 0; i < count; i++)
                {
                    if (this.Tiers[i] != null)
                    {
                        CodexQuotaRadarTier replacement = this.Tiers[i].Clone();
                        int index = GetCodexQuotaRadarTierIndex(replacement.Key);
                        if (index >= 0 && index < clone.Tiers.Length)
                        {
                            clone.Tiers[index] = replacement;
                        }
                        else
                        {
                            clone.Tiers[i] = replacement;
                        }
                    }
                }
            }

            return clone;
        }
    }

    private sealed class CodexQuotaRadarTier
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Source { get; set; }
        public double FiveHourUsd { get; set; }
        public double SevenDayUsd { get; set; }
        public double PreviousSevenDayUsd { get; set; }
        public double AverageSevenDayUsd { get; set; }
        public double TrendMinSevenDayUsd { get; set; }
        public double TrendMaxSevenDayUsd { get; set; }
        public double PriorTrendMinSevenDayUsd { get; set; }
        public double PriorTrendMaxSevenDayUsd { get; set; }
        public bool CurrentKnown { get; set; }
        public bool PreviousKnown { get; set; }
        public bool AverageKnown { get; set; }
        public bool TrendRangeKnown { get; set; }
        public bool PriorTrendRangeKnown { get; set; }

        public CodexQuotaRadarTier Clone()
        {
            return new CodexQuotaRadarTier
            {
                Key = this.Key,
                Label = this.Label,
                Source = this.Source,
                FiveHourUsd = this.FiveHourUsd,
                SevenDayUsd = this.SevenDayUsd,
                PreviousSevenDayUsd = this.PreviousSevenDayUsd,
                AverageSevenDayUsd = this.AverageSevenDayUsd,
                TrendMinSevenDayUsd = this.TrendMinSevenDayUsd,
                TrendMaxSevenDayUsd = this.TrendMaxSevenDayUsd,
                PriorTrendMinSevenDayUsd = this.PriorTrendMinSevenDayUsd,
                PriorTrendMaxSevenDayUsd = this.PriorTrendMaxSevenDayUsd,
                CurrentKnown = this.CurrentKnown,
                PreviousKnown = this.PreviousKnown,
                AverageKnown = this.AverageKnown,
                TrendRangeKnown = this.TrendRangeKnown,
                PriorTrendRangeKnown = this.PriorTrendRangeKnown
            };
        }
    }

    private static CodexQuotaRadarTier[] CreateDefaultCodexQuotaRadarTiers()
    {
        return new CodexQuotaRadarTier[]
        {
            new CodexQuotaRadarTier
            {
                Key = QuotaRadarTierPlus,
                Label = "Plus",
                Source = string.Empty
            },
            new CodexQuotaRadarTier
            {
                Key = QuotaRadarTierPro5x,
                Label = "Pro5x",
                Source = string.Empty
            },
            new CodexQuotaRadarTier
            {
                Key = QuotaRadarTierPro20x,
                Label = "Pro20x",
                Source = string.Empty
            }
        };
    }

    private static int GetCodexQuotaRadarTierIndex(string key)
    {
        if (string.Equals(key, QuotaRadarTierPlus, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(key, QuotaRadarTierPro5x, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(key, QuotaRadarTierPro20x, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return -1;
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

    public CodexRadarForm(WidgetSettings settings, Action<string, string, ToolTipIcon> notificationAction)
    {
        this.notificationAction = notificationAction;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        UpdateEffectiveCodexRadarSoftwareMode(true);
        LoadSelectedQuotaCacheIntoDisplay();
        InitializeQuotaReadDeltaTracking(this.quotaSnapshot, this.quotaSourceKnown);
        this.codexRadarSnapshot = LoadCodexRadarCache(
                GetEffectiveCodexRadarSoftwareMode(),
                GetSelectedRadarModelKeyForSoftwareMode(GetEffectiveCodexRadarSoftwareMode())) ??
            CodexRadarSnapshot.CreateDefault();
        this.lastCodexRadarStatusAttemptLocal = this.codexRadarSnapshot.CheckedAtKnown
            ? this.codexRadarSnapshot.CheckedAtLocal
            : DateTime.MinValue;
        LoadCodexRadarNotificationState();
        LoadQuotaResetState();
        InitializeQuotaSessionWatcher();

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.currentSettings);

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.MinimumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MinCodexRadarHeight));
        this.MaximumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MaxCodexRadarWidth, WidgetSettings.MaxCodexRadarHeight + S(32)));
        this.Size = GetDesiredCodexRadarSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextCodexRadarTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        ScheduleNextCodexRadarTick();
        this.timer.Start();
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
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        DisposeQuotaSessionWatcher();
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        base.OnFormClosed(e);
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        try
        {
            this.renderTickCount++;
            if (!IsCodexPollingAllowed())
            {
                return;
            }

            // This timer is only a lightweight scheduler. Each data source owns its business
            // interval and single-flight guard, so a faster UI mode does not multiply web traffic.
            UpdateEffectiveCodexRadarSoftwareModeIfNeeded();
            UpdateCodexRadarRandomTestIfNeeded();
            if (!this.currentSettings.CodexRadarRandomTestEnabled)
            {
                if (ServiceHealthProbeEnabled)
                {
                    UpdateServiceConnectivityHealth();
                }

                RefreshSelectedQuotaInfoIfNeeded();
                RefreshCodexResetCreditsIfNeeded();
                RefreshCodexRadarStatusIfNeeded();
                RefreshDeepSeekBalanceIfNeeded();
                if (ServiceHealthProbeEnabled)
                {
                    RefreshClaudeStatusIfNeeded();
                    RefreshOpenAiStatusIfNeeded();
                }

                ApplyRadarClockAutoSwitchIfNeeded();
            }
            bool alertChanged = AdvanceCodexApiServiceAlertRotation();
            Size desiredSize = GetDesiredCodexRadarSize();
            bool sizeChanged = false;
            if (this.Size != desiredSize)
            {
                this.Size = desiredSize;
                PositionCodexRadar();
                sizeChanged = true;
            }

            bool positionChanged = false;
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot))
            {
                PositionCodexRadar();
                positionChanged = true;
            }

            DateTime renderSecond = TruncateToSecond(DateTime.Now);
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                (sizeChanged || positionChanged || alertChanged || this.lastRenderedClockSecondLocal != renderSecond))
            {
                RenderLayeredWindow();
            }
        }
        finally
        {
            ScheduleNextCodexRadarTick();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(12)))
        {
            Region oldRegion = this.Region;
            this.Region = new Region(path);
            if (oldRegion != null)
            {
                oldRegion.Dispose();
            }
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_POWERBROADCAST)
        {
            HandlePowerBroadcast(m.WParam, m.LParam);
        }

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionCodexRadar();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        CodexRadarTestMode oldCodexRadarTestMode = this.currentSettings.CodexRadarTestMode;
        ServiceHealthTestMode oldServiceHealthTestMode = this.currentSettings.ServiceHealthTestMode;
        string oldModelKey = this.currentSettings.CodexRadarModelKey;
        CodexRadarSoftwareMode oldConfiguredSoftwareMode = this.currentSettings.CodexRadarSoftwareMode;
        CodexRadarSoftwareMode oldEffectiveSoftwareMode = GetEffectiveCodexRadarSoftwareMode();
        bool oldRandomTestEnabled = this.currentSettings.CodexRadarRandomTestEnabled;
        int oldRandomTestToken = this.currentSettings.CodexRadarRandomTestRefreshToken;
        bool oldPublicJsonEnabled = this.currentSettings.CodexRadarPublicJsonEnabled;
        bool oldHtmlFallbackEnabled = this.currentSettings.CodexRadarHtmlFallbackEnabled;
        bool oldRssFallbackEnabled = this.currentSettings.CodexRadarRssFallbackEnabled;
        int oldServiceProbeToken = this.currentSettings.CodexRadarServiceProbeToken;
        int oldDeepSeekApiKeyRevision = this.currentSettings.DeepSeekApiKeyRevision;
        CacheCodexRadarDisplayMode(oldEffectiveSoftwareMode);
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.currentSettings);
        this.MinimumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MinCodexRadarHeight));
        this.MaximumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MaxCodexRadarWidth, WidgetSettings.MaxCodexRadarHeight + S(32)));
        unchecked
        {
            this.renderSceneSettingsRevision++;
        }

        bool effectiveSoftwareChanged = UpdateEffectiveCodexRadarSoftwareMode(true);
        ApplyPerformanceTimerIntervals();

        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            (!oldRandomTestEnabled ||
             oldRandomTestToken != this.currentSettings.CodexRadarRandomTestRefreshToken ||
             this.codexRadarRandomTestSnapshot == null))
        {
            GenerateCodexRadarRandomTestSnapshot();
        }
        else if (oldRandomTestEnabled && !this.currentSettings.CodexRadarRandomTestEnabled)
        {
            this.codexRadarRandomTestSnapshot = null;
            PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            RequestServiceNetworkRefresh();
        }

        bool softwareSettingChanged = oldConfiguredSoftwareMode != this.currentSettings.CodexRadarSoftwareMode ||
            oldEffectiveSoftwareMode != GetEffectiveCodexRadarSoftwareMode() ||
            effectiveSoftwareChanged;
        if (!string.Equals(oldModelKey, this.currentSettings.CodexRadarModelKey, StringComparison.OrdinalIgnoreCase) ||
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

        if (oldPublicJsonEnabled != this.currentSettings.CodexRadarPublicJsonEnabled ||
            oldHtmlFallbackEnabled != this.currentSettings.CodexRadarHtmlFallbackEnabled ||
            oldRssFallbackEnabled != this.currentSettings.CodexRadarRssFallbackEnabled)
        {
            lock (this.codexRadarStatusLock)
            {
                this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
                this.codexRadarStatusRefreshTrigger = "数据源设置变更";
            }

            SetRadarServiceHealth(ServiceHealthState.Unknown);
        }

        if (oldServiceProbeToken != this.currentSettings.CodexRadarServiceProbeToken &&
            this.currentSettings.CodexRadarServiceProbeToken > 0)
        {
            StartCodexRadarServiceProbe();
        }

        if (oldDeepSeekApiKeyRevision != this.currentSettings.DeepSeekApiKeyRevision)
        {
            RequestDeepSeekBalanceRefresh("DeepSeek 配置");
            RefreshDeepSeekBalanceIfNeeded();
        }

        if (oldCodexRadarTestMode != this.currentSettings.CodexRadarTestMode)
        {
            if (this.currentSettings.CodexRadarTestMode == CodexRadarTestMode.Off)
            {
                PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            }

            RenderLayeredWindow();
        }

        if (ServiceHealthProbeEnabled &&
            oldServiceHealthTestMode != this.currentSettings.ServiceHealthTestMode)
        {
            if (this.currentSettings.ServiceHealthTestMode == ServiceHealthTestMode.Off)
            {
                ResetServiceHealthAfterTestMode();
            }
            else
            {
                ApplyServiceHealthTestMode();
            }

            RenderLayeredWindow();
        }
        else if (ServiceHealthProbeEnabled &&
            this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
        }

        Size desiredSize = GetDesiredCodexRadarSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();
        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(shouldBeTopMost),
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionCodexRadar();
        RenderLayeredWindow();
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

        RequestDeepSeekBalanceRefresh("操作面板刷新");
        RequestSelectedQuotaUsageRefresh("操作面板刷新");
        RequestCodexResetCreditsRefresh("操作面板刷新");

        OnTimerTick(this, EventArgs.Empty);
    }

    private void StartCodexRadarServiceProbe()
    {
        int token = this.currentSettings.CodexRadarServiceProbeToken;
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

        string modelKey = this.currentSettings.CodexRadarModelKey;
        bool publicJsonEnabled = this.currentSettings.CodexRadarPublicJsonEnabled;
        bool htmlFallbackEnabled = this.currentSettings.CodexRadarHtmlFallbackEnabled;
        bool rssFallbackEnabled = this.currentSettings.CodexRadarRssFallbackEnabled;
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
                    rssFallbackEnabled);
                Directory.CreateDirectory(Logger.DirectoryPath);
                path = Path.Combine(Logger.DirectoryPath, "codex-radar-service-probe.txt");
                File.WriteAllText(path, report, new UTF8Encoding(false));
                success = true;
                if (this.notificationAction != null)
                {
                    this.notificationAction(
                        "Codex Radar 服务检测完成",
                        "结果已写入 " + path,
                        ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                if (this.notificationAction != null)
                {
                    this.notificationAction(
                        "Codex Radar 服务检测失败",
                        ex.GetType().Name,
                        ToolTipIcon.Warning);
                }
            }
            finally
            {
                stopwatch.Stop();
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
            }
        });
    }

    public void RecoverAfterDisplayResume()
    {
        this.codexPowerSuspended = false;
        this.codexDisplayActive = true;
        this.codexSessionActive = true;
        ResetDisplayRenderResources();
        PositionCodexRadar();
        ResumeCodexPollingSoon();
        ScheduleNextCodexRadarTick();
    }

    public void PrepareForDisplaySuspend()
    {
        ResetDisplayRenderResources();
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
        RenderLayeredWindow();
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
            return;
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            this.codexSessionActive = true;
            ResumeCodexPollingSoon();
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.codexPowerSuspended = true;
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            this.codexPowerSuspended = false;
            this.codexDisplayActive = true;
            ResumeCodexPollingSoon();
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
                        ResumeCodexPollingSoon();
                    }
                }
            }
        }
    }

    private void ApplyPerformanceTimerIntervals()
    {
        ScheduleNextCodexRadarTick();

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
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
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.currentSettings.PerformanceMode);
        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.currentSettings.CodexRadarRandomTestAutoRefresh)
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
        if (!this.currentSettings.CodexRadarRandomTestEnabled)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool tokenChanged =
            this.codexRadarRandomTestRefreshToken !=
            this.currentSettings.CodexRadarRandomTestRefreshToken;
        bool automaticDue =
            this.currentSettings.CodexRadarRandomTestAutoRefresh &&
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
            this.currentSettings.CodexRadarRandomTestRefreshToken * 397 ^
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
        radar.QuotaRadar = BuildRandomCodexQuotaRadarSnapshot(random);
        string[] ratingIds = new string[] { "gpt-5.5-xhigh", "gpt-5.5-high", "gpt-5.5-medium", "gpt-5.4-xhigh", "gpt-5.4-high" };
        string ratingId = ratingIds[random.Next(0, ratingIds.Length)];
        ApplyCodexCommunityRatingSnapshot(
            radar,
            ratingId,
            FormatCodexCommunityRatingLabel(ratingId),
            Math.Round(4.0 + random.NextDouble() * 5.9, 1),
            random.Next(8, 240),
            DateTime.Now);
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
            this.currentSettings.CodexRadarRandomTestRefreshToken;
        this.nextCodexRadarRandomTestRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
    }

    private static CodexQuotaRadarSnapshot BuildRandomCodexQuotaRadarSnapshot(Random random)
    {
        double current20x = random.Next(140000, 230001) / 100.0;
        double previous20x = Math.Max(100.0, current20x + random.Next(-22000, 22001) / 100.0);
        return BuildCodexQuotaRadarTestSnapshot(current20x, previous20x);
    }

    private static CodexQuotaRadarSnapshot BuildCodexQuotaRadarTestSnapshot(
        double current20xSevenDay,
        double previous20xSevenDay)
    {
        CodexQuotaRadarSnapshot radar = CodexQuotaRadarSnapshot.CreateDefault();
        radar.Known = true;
        radar.UpdatedAtLocal = DateTime.Now;
        radar.UpdatedAtKnown = true;

        ApplyCodexQuotaRadarTierValues(
            radar,
            QuotaRadarTierPlus,
            current20xSevenDay / 20.0,
            previous20xSevenDay / 20.0,
            (current20xSevenDay + previous20xSevenDay) / 40.0,
            "推测");
        ApplyCodexQuotaRadarTierValues(
            radar,
            QuotaRadarTierPro5x,
            current20xSevenDay / 4.0,
            previous20xSevenDay / 4.0,
            (current20xSevenDay + previous20xSevenDay) / 8.0,
            "推测");
        ApplyCodexQuotaRadarTierValues(
            radar,
            QuotaRadarTierPro20x,
            current20xSevenDay,
            previous20xSevenDay,
            (current20xSevenDay + previous20xSevenDay) / 2.0,
            "实测");
        ApplyCodexQuotaRadarTierTrendRange(
            radar,
            QuotaRadarTierPlus,
            Math.Min(current20xSevenDay, previous20xSevenDay) / 20.0,
            Math.Max(current20xSevenDay, previous20xSevenDay) / 20.0);
        ApplyCodexQuotaRadarTierTrendRange(
            radar,
            QuotaRadarTierPro5x,
            Math.Min(current20xSevenDay, previous20xSevenDay) / 4.0,
            Math.Max(current20xSevenDay, previous20xSevenDay) / 4.0);
        ApplyCodexQuotaRadarTierTrendRange(
            radar,
            QuotaRadarTierPro20x,
            Math.Min(current20xSevenDay, previous20xSevenDay),
            Math.Max(current20xSevenDay, previous20xSevenDay));
        ApplyCodexQuotaRadarTierPriorTrendRange(
            radar,
            QuotaRadarTierPlus,
            previous20xSevenDay / 20.0,
            previous20xSevenDay / 20.0);
        ApplyCodexQuotaRadarTierPriorTrendRange(
            radar,
            QuotaRadarTierPro5x,
            previous20xSevenDay / 4.0,
            previous20xSevenDay / 4.0);
        ApplyCodexQuotaRadarTierPriorTrendRange(
            radar,
            QuotaRadarTierPro20x,
            previous20xSevenDay,
            previous20xSevenDay);
        return radar;
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

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        bool animationActive = ProcessInteractionTick();
        int desiredInterval = animationActive
            ? WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode)
            : WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != desiredInterval)
        {
            this.hoverTimer.Interval = desiredInterval;
        }
    }

    private bool ProcessInteractionTick()
    {
        ApplyClickThroughStyle();
        bool opacityChanged = UpdateHoverOpacityAnimation();
        bool hoverTarget = IsHoverOpacityTargetActive();
        bool animationActive = Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
        if (opacityChanged && !this.hiddenForFullscreen && this.Visible)
        {
            RenderLayeredWindow(false);
        }

        return animationActive;
    }

    public void SetSharedInteractionPolling(bool shared)
    {
        this.sharedInteractionPolling = shared;
        this.hoverOpacityLastUtc = DateTime.UtcNow;
        UpdateHoverAnimationTimer();
    }

    public void SetAutoHideKeepAliveActive(bool active)
    {
        if (this.autoHideKeepAliveActive == active)
        {
            return;
        }

        this.autoHideKeepAliveActive = active;
        if (active)
        {
            this.hoverOpacityDelayState.Reset();
            this.reverseHoverRevealUntilUtc = DateTime.MinValue;
        }
    }

    public bool ProcessSharedInteractionTick()
    {
        if (!this.sharedInteractionPolling ||
            this.hiddenForFullscreen ||
            (!IsHoverOpacityRuntimeEnabled() && !NeedsClickThroughPolling()))
        {
            return false;
        }

        return ProcessInteractionTick();
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (IsHoverOpacityRuntimeEnabled() || NeedsClickThroughPolling()))
        {
            if (this.sharedInteractionPolling)
            {
                this.hoverTimer.Stop();
                return;
            }

            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow(false);
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered = IsHoverOpacityTargetActive();

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    private bool IsHoverOpacityTargetActive()
    {
        return HoverInteractionPolicy.IsHoverOpacityTargetActive(
            this.currentSettings,
            this.Bounds,
            this.hiddenForFullscreen,
            this.Visible,
            ref this.reverseHoverRevealUntilUtc,
            this.hoverOpacityDelayState,
            this.autoHideKeepAliveActive);
    }

    private bool IsHoverOpacityRuntimeEnabled()
    {
        return this.currentSettings.HoverOpacityEnabled || this.currentSettings.ForceHoverOpacityActive;
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (this.hiddenForFullscreen == hidden &&
            ((hidden && !this.Visible) || (!hidden && this.Visible)))
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            if (this.Visible)
            {
                this.Hide();
            }

            UpdateHoverAnimationTimer();
            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionCodexRadar();
        RenderLayeredWindow();
        UpdateHoverAnimationTimer();
    }

    private void PositionCodexRadar()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = this.currentSettings.GetWorkAreaForModule(WidgetSettings.ModuleCodexRadar);
        Size desiredSize = GetDesiredCodexRadarSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int mappedLeft = this.currentSettings.MapResolutionCompatibilityLeft(WidgetSettings.ModuleCodexRadar, workArea, this.currentSettings.CodexRadarLeftX);
        int mappedBottom = this.currentSettings.MapResolutionCompatibilityBottom(WidgetSettings.ModuleCodexRadar, workArea, this.currentSettings.CodexRadarBottomY);
        int left = Math.Max(workArea.Left, Math.Min(mappedLeft, workArea.Right - this.Width));
        int top = mappedBottom - this.Height + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        Point shiftedLocation = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.CodexRadarSalt);
        left = shiftedLocation.X;
        top = shiftedLocation.Y;
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(this.currentSettings.VisibilityMode),
            left,
            top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredCodexRadarSize()
    {
        return this.currentSettings.ScaleResolutionCompatibilitySize(new Size(this.currentSettings.CodexRadarWidth, this.currentSettings.CodexRadarHeight));
    }

    private int GetThermalAlertExtraHeight()
    {
        return Math.Max(S(24), Math.Min(S(32), (int)Math.Round(this.currentSettings.CodexRadarHeight * 0.42f)));
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = ShouldClickThroughNow();
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private bool ShouldClickThroughNow()
    {
        if (NativeMethods.IsClickThroughModifierDown())
        {
            return false;
        }

        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    private bool NeedsClickThroughPolling()
    {
        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawCodexRadar(e.Graphics);
    }

    private void DrawCodexRadar(Graphics g)
    {
        DrawCodexRadarBackground(g);
        DrawCodexRadarContentLayer(g);
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawCodexRadar(g);
    }

    protected override bool IsLayeredBurnInColorProtectionActive()
    {
        return IsBurnInColorProtectionActive();
    }

    protected override bool TryDrawCachedWindowContent(Graphics g, bool burnInColorProtectionActive)
    {
        string sceneCacheKey = BuildCodexRadarRenderSceneCacheKey(burnInColorProtectionActive);
        Bitmap cached;
        if (!this.renderSceneBitmapCache.TryGetValue(sceneCacheKey, out cached) ||
            cached == null ||
            cached.Width != this.Width ||
            cached.Height != this.Height)
        {
            return false;
        }

        g.DrawImageUnscaled(cached, 0, 0);
        return true;
    }

    protected override void OnLayeredBitmapPrepared(Bitmap bitmap, bool burnInColorProtectionActive)
    {
        StoreRenderSceneBitmap(BuildCodexRadarRenderSceneCacheKey(burnInColorProtectionActive));
    }

    protected override void OnLayeredNativeBitmapRefreshed(bool burnInColorProtectionActive)
    {
        this.lastRenderedClockSecondLocal = TruncateToSecond(DateTime.Now);
    }

    protected override void DisposeAdditionalRenderBuffers()
    {
        DisposeRenderSceneBitmapCache();
    }

    private void ConfigureCodexRadarGraphics(Graphics g)
    {
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
    }

    private void DrawCodexRadarBackground(Graphics g)
    {
        ConfigureCodexRadarGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, alpha)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawCodexRadarContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawCodexRadarContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawCodexRadarContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawCodexRadarContent(Graphics g)
    {
        ConfigureCodexRadarGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        RectangleF textRect = new RectangleF(
            S(8),
            S(3),
            Math.Max(10, this.Width - S(16)),
            Math.Max(10, this.Height - S(6)));

        DrawCodexRadarModules(g, textRect);
        DrawCodexRadarSoftwareInnerBorder(g);
    }

    private void DrawCodexRadarSoftwareInnerBorder(Graphics g)
    {
        // Hidden-mode color protection inverts colored pixels after painting. The blue Codex
        // border becomes yellow/orange after inversion, so suppress the software chrome before
        // the bitmap pass instead of trying to remove the inverted pixels afterwards.
        if (IsBurnInColorProtectionActive())
        {
            return;
        }

        float stroke = Math.Max(1.0f, S(3));
        float inset = stroke / 2.0f;
        RectangleF rect = new RectangleF(
            inset,
            inset,
            Math.Max(1.0f, this.Width - stroke - 1.0f),
            Math.Max(1.0f, this.Height - stroke - 1.0f));
        float radius = Math.Max(1.0f, S(DesignTokens.Radius.Panel) - inset);
        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (Pen pen = new Pen(GetCodexRadarSoftwareChromeColor(GetEffectiveCodexRadarSoftwareMode()), stroke))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, path);
        }
    }

    private static Color GetCodexRadarSoftwareChromeColor(CodexRadarSoftwareMode mode)
    {
        return mode == CodexRadarSoftwareMode.Claude
            ? Color.FromArgb(240, 232, 128, 54)
            : Color.FromArgb(238, 16, 58, 143);
    }

    private void DrawCodexRadarModules(Graphics g, RectangleF bounds)
    {
        DrawCodexRadarModulesEvenRow(g, bounds);
    }

    // Shared by the classic widget tree and the EvenGrid/EvenRow variants so quota gold
    // protection, consumption-ring baselines and random-test overrides are computed in exactly
    // one place instead of once per variant file.
    private sealed class QuotaDisplayState
    {
        public CodexQuotaSnapshot Snapshot;
        public bool FiveHourGold;
        public bool WeeklyGold;
        public bool CodexRunning;
        public bool AnySupportedAppRunning;
        public bool QuotaValueKnown;
        public int FiveHourConsumptionRingPercent;
        public int WeeklyConsumptionRingPercent;
        public bool WeeklyConsumptionRingBlocked;
        public bool ForceDangerRing;
    }

    private QuotaDisplayState GatherQuotaDisplayState()
    {
        bool randomTest =
            this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null;
        CodexQuotaSnapshot snapshot = randomTest
            ? this.codexRadarRandomTestSnapshot.Quota
            : (this.quotaSnapshot ?? CodexQuotaSnapshot.CreateDefault());
        QuotaDisplayState state = new QuotaDisplayState { Snapshot = snapshot };
        if (randomTest)
        {
            state.FiveHourGold = this.codexRadarRandomTestSnapshot.FiveHourGold;
            state.WeeklyGold = this.codexRadarRandomTestSnapshot.WeeklyGold;
            state.CodexRunning = this.codexRadarRandomTestSnapshot.CodexRunning;
            state.AnySupportedAppRunning = this.codexRadarRandomTestSnapshot.CodexRunning;
            state.QuotaValueKnown = true;
            state.FiveHourConsumptionRingPercent = state.FiveHourGold
                ? 0
                : ClampPercent(snapshot.FiveHourPercent + this.codexRadarRandomTestSnapshot.FiveHourDropPercent);
            state.WeeklyConsumptionRingPercent = state.WeeklyGold
                ? 0
                : ClampPercent(snapshot.WeeklyPercent + this.codexRadarRandomTestSnapshot.WeeklyUsedSinceFiveHourResetPercent);
            state.WeeklyConsumptionRingBlocked = state.WeeklyGold;
            return state;
        }

        bool isClaude = GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude;
        SoftwareRuntimePresenceSnapshot presence = GetLastSoftwareRuntimePresenceSnapshot();
        bool fiveHourProtected;
        bool weeklyProtected;
        lock (this.quotaResetStateLock)
        {
            state.FiveHourGold = !isClaude && this.fiveHourQuotaProtectionGold;
            state.WeeklyGold = !isClaude && this.weeklyQuotaProtectionGold;
            fiveHourProtected = !isClaude && this.fiveHourQuotaProtectionUtc != DateTime.MinValue;
            weeklyProtected = !isClaude && this.weeklyQuotaProtectionUtc != DateTime.MinValue;
        }

        state.CodexRunning = isClaude ? IsClaudeQuotaDisplayAvailable() : this.quotaCodexProcessRunning;
        state.AnySupportedAppRunning = presence.AnySupportedAppRunning;
        state.QuotaValueKnown = IsSelectedQuotaValueKnown(isClaude);
        state.ForceDangerRing = isClaude &&
            !state.QuotaValueKnown &&
            QuotaRingPresentation.IsSetupTokenMissing(this.claudeCodeUsageErrorCode);
        state.FiveHourConsumptionRingPercent = fiveHourProtected
            ? 0
            : (this.fiveHourConsumptionRingBaselinePercent >= 0
                ? ClampPercent(this.fiveHourConsumptionRingBaselinePercent)
                : 0);
        state.WeeklyConsumptionRingPercent = this.quotaSourceKnown &&
            !fiveHourProtected &&
            this.weeklyQuotaAtFiveHourWindowStartPercent >= 0
            ? ClampPercent(this.weeklyQuotaAtFiveHourWindowStartPercent)
            : 0;
        state.WeeklyConsumptionRingBlocked = weeklyProtected;
        return state;
    }

    private bool IsSelectedQuotaValueKnown(bool isClaude)
    {
        if (this.quotaSourceKnown)
        {
            return true;
        }

        if (isClaude)
        {
            return this.claudeQuotaSourceKnown;
        }

        lock (this.codexProviderUsageLock)
        {
            return this.codexProviderQuotaSourceKnown;
        }
    }

    // Ring-plus-label cell drawing shared by the EvenGrid/EvenRow variants. Deliberately does not
    // call OffsetCodexRadarElementRect: those per-variant grids must stay unaffected by the manual
    // pixel offsets that only make sense for the classic layout's fixed left/right split.
    private void DrawEvenLayoutQuotaCell(
        Graphics g,
        RectangleF cellRect,
        int percent,
        string resetText,
        bool codexRunning,
        bool anySupportedAppRunning,
        bool quotaValueKnown,
        bool quotaProtected,
        int consumptionRingPercent,
        CodexRadarSnapshot radarSnapshot,
        bool dateText,
        bool forceDangerFullRing)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        RectangleF ringRect;
        RectangleF textRect;
        GetEvenLayoutCellRects(cellRect, out ringRect, out textRect);

        string displayText;
        Color displayColor;
        GetQuotaResetDisplayText(resetText, quotaProtected, radarSnapshot, dateText, out displayText, out displayColor);

        QuotaRingDrawSpec spec = new QuotaRingDrawSpec
        {
            Percent = percent,
            ConsumptionRingPercent = consumptionRingPercent,
            ResetDisplayText = displayText,
            ResetDisplayColor = displayColor,
            Running = codexRunning,
            AnySupportedAppRunning = anySupportedAppRunning,
            QuotaValueKnown = quotaValueKnown,
            ForceDangerFullRing = forceDangerFullRing,
            NumberFont = this.fontCache.GetUi(Math.Max(7.0f, ringRect.Width * 0.342f), FontStyle.Bold),
            LabelFont = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.LayerScale), FontStyle.Bold),
            DrawFittedLabel = delegate(Graphics graphics, string text, Font font, Brush brush, RectangleF rect)
            {
                DrawCodexRadarFittedText(graphics, text, font, brush, rect, StringAlignment.Center, 6.0f);
            }
        };
        QuotaRingPresentation.DrawQuotaRing(g, ringRect, textRect, spec);
    }

    private void DrawEvenLayoutIqCell(Graphics g, RectangleF cellRect, CodexRadarSnapshot snapshot)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        RectangleF ringRect;
        RectangleF textRect;
        GetEvenLayoutCellRects(cellRect, out ringRect, out textRect);

        bool known = snapshot != null && snapshot.ModelIqKnown;
        int passRatePercent = known ? Math.Max(0, Math.Min(MaxCodexModelIqScore, snapshot.ModelIqPassRatePercent)) : 0;
        string centerText = known ? passRatePercent.ToString(CultureInfo.InvariantCulture) : "-";
        bool scoreKnown = known;
        double baselineScore = GetCodexModelIqBaselineScore(snapshot);
        double displayMaxScore = GetCodexModelIqDisplayMaxScore(snapshot, baselineScore);

        float stroke = Math.Max(2.0f, ringRect.Width * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);
        using (Pen backgroundPen = new Pen(DesignTokens.White(72), stroke))
        using (Pen baselinePen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235), stroke))
        using (Pen deficitPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 242), stroke))
        using (Pen surplusPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245), stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            baselinePen.StartCap = LineCap.Round;
            baselinePen.EndCap = LineCap.Round;
            deficitPen.StartCap = LineCap.Round;
            deficitPen.EndCap = LineCap.Round;
            surplusPen.StartCap = LineCap.Round;
            surplusPen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (scoreKnown)
            {
                DrawCodexModelIqBaselineArcs(
                    g,
                    arcRect,
                    baselinePen,
                    deficitPen,
                    surplusPen,
                    passRatePercent,
                    baselineScore,
                    displayMaxScore);
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(7.0f, ringRect.Width * 0.36f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(centerText, font, brush, ringRect, center);
        }

        string labelText = "-";
        Color labelColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (scoreKnown)
        {
            GetCodexModelIqNormalRangeLabel(
                snapshot,
                passRatePercent,
                out labelText,
                out labelColor);
        }

        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.LayerScale), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(labelColor))
        {
            DrawCodexRadarFittedText(g, labelText, labelFont, labelBrush, textRect, StringAlignment.Center);
        }
    }

    private void DrawEvenLayoutEfficiencyCell(Graphics g, RectangleF cellRect, CodexRadarSnapshot snapshot, bool timeEfficiency)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        RectangleF ringRect;
        RectangleF textRect;
        GetEvenLayoutCellRects(cellRect, out ringRect, out textRect);

        bool known = snapshot != null && snapshot.ModelIqEfficiencyKnown;
        int efficiency = known
            ? (timeEfficiency ? snapshot.ModelIqTimeEfficiencyPercent : snapshot.ModelIqTokenEfficiencyPercent)
            : 100;
        string centerText = known ? ClampEfficiencyPercent(efficiency).ToString(CultureInfo.InvariantCulture) : "-";

        float stroke = Math.Max(2.0f, ringRect.Width * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);
        using (Pen basePen = new Pen(DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 242), stroke))
        using (Pen lowPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 244), stroke))
        using (Pen highPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245), stroke))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            lowPen.StartCap = LineCap.Round;
            lowPen.EndCap = LineCap.Round;
            highPen.StartCap = LineCap.Round;
            highPen.EndCap = LineCap.Round;
            g.DrawArc(basePen, arcRect, -90.0f, 360.0f);
            if (known)
            {
                int clamped = Math.Max(0, Math.Min(200, efficiency));
                if (clamped < 100)
                {
                    g.DrawArc(lowPen, arcRect, -90.0f, -360.0f * ((100 - clamped) / 100.0f));
                }
                else if (clamped > 100)
                {
                    g.DrawArc(highPen, arcRect, -90.0f, 360.0f * ((clamped - 100) / 100.0f));
                }
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(7.0f, ringRect.Width * 0.342f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(centerText, font, brush, ringRect, center);
        }

        string labelText = "-";
        Color labelColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (known)
        {
            GetCodexModelSingleEfficiencyLabelAndColor(ClampEfficiencyPercent(efficiency), timeEfficiency, out labelText, out labelColor);
        }

        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.LayerScale), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(labelColor))
        {
            DrawCodexRadarFittedText(g, labelText, labelFont, labelBrush, textRect, StringAlignment.Center);
        }
    }

    // Common ring-on-top, label-below split used by every EvenGrid/EvenRow cell so ring size,
    // stroke weight and label baseline line up across all six/seven cells in a row.
    // Ring fill factor inside an even-layout cell. EvenGrid keeps the default 0.86 (rings centered
    // with comfortable side margins). EvenRow raises it while packing rings so the ring size is
    // driven by the cell HEIGHT, not its width - this decouples ring size from horizontal packing
    // so the columns can be tightened to ~5px gaps without shrinking the rings.
    private float evenLayoutRingFillFactor = 0.86f;

    private void GetEvenLayoutCellRects(RectangleF cellRect, out RectangleF ringRect, out RectangleF textRect)
    {
        float ringTopInset = S(3);
        float textGap = S(2);
        float textHeight = Math.Max(S(13), cellRect.Height * 0.18f);
        float ringAreaHeight = Math.Max(S(20), cellRect.Height - ringTopInset - textGap - textHeight);
        float ringSize = Math.Max(S(18), Math.Min(cellRect.Width * this.evenLayoutRingFillFactor, ringAreaHeight));
        ringRect = new RectangleF(
            cellRect.Left + (cellRect.Width - ringSize) / 2.0f,
            cellRect.Top + ringTopInset + (ringAreaHeight - ringSize) / 2.0f,
            ringSize,
            ringSize);
        textRect = new RectangleF(cellRect.Left, ringRect.Bottom + textGap, cellRect.Width, textHeight);
    }

    // The quota radar is a full-height vertical diagnostic bar (average line, colored trend
    // segment, blue current dot, trend chevrons) - it needs the whole cell height to be legible,
    // exactly like the classic layout's radar overlay. Do NOT box it into a ring-square or add a
    // text label under it: that squashes the trend into noise, which is what "额度雷达条状态被破坏"
    // was. The bar spans the full cell height so it reads at the same scale as classic.
    private void DrawEvenLayoutRadarCell(Graphics g, RectangleF cellRect, CodexRadarSnapshot radarSnapshot)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        // 50% thicker than classic: widen the line column and pass strokeScale 1.5 so the bar,
        // average tick, blue dot and chevrons all scale up together.
        float lineWidth = Math.Max(S(8), Math.Min(S(14), cellRect.Width * 0.42f));
        RectangleF radarLineRect = new RectangleF(
            cellRect.Left + (cellRect.Width - lineWidth) / 2.0f,
            cellRect.Top + S(3),
            lineWidth,
            Math.Max(1.0f, cellRect.Height - S(6)));
        DrawCodexQuotaRadarVerticalLine(g, radarLineRect, radarSnapshot == null ? null : radarSnapshot.QuotaRadar, 1.5f);
    }

    // The pre-1.0.3.34 widget tree (DrawCodexRadarWidget/DrawQuotaWidget). Kept as its own
    // method, rather than inlined in DrawCodexRadarModules, so variant scaffolds in sibling
    // partial files can fall back to it instead of rendering a blank window.

    private static string BuildCodexConnectionAlertSignature(CodexConnectionAlertCandidate[] candidates)
    {
        if (candidates == null || candidates.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("|");
            }

            builder.Append(candidates[i].Key);
            builder.Append(":");
            builder.Append(candidates[i].Name);
            builder.Append(":");
            builder.Append(candidates[i].Reason);
        }

        return builder.ToString();
    }

    private void GetCodexModelIqUpdateStatusText(
        CodexRadarSnapshot snapshot,
        bool requestRunning,
        out string text,
        out Color color)
    {
        if (snapshot == null || !snapshot.ModelIqRefreshedAtKnown)
        {
            text = requestRunning ? "更新中/--:--" : "等待/--:--";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
            return;
        }

        string time = TimeZoneUtilities.ConvertToDisplayTime(
                snapshot.ModelIqRefreshedAtLocal,
                this.currentSettings)
            .ToString("HH:mm", CultureInfo.CurrentCulture);
        if (IsCodexModelIqCurrentForBeijingWindow(snapshot, DateTime.UtcNow))
        {
            text = "已更新/" + time;
            color = DesignTokens.Colors.QuotaGood;
            return;
        }

        text = "未更新/" + time;
        color = DesignTokens.Colors.Warning;
    }

    private static string GetCodexModelIqDataLabelDisplayText(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return "--";
        }

        if (snapshot.ModelIqDataLabelKnown && !string.IsNullOrWhiteSpace(snapshot.ModelIqDataLabel))
        {
            return snapshot.ModelIqDataLabel.Trim();
        }

        if (snapshot.ModelIqDataDateKnown)
        {
            return FormatCodexModelIqDataLabel(
                string.Empty,
                snapshot.ModelIqDataDateLocal,
                snapshot.ModelIqDataWindowStartHourLocal,
                snapshot.ModelIqDataWindowKnown);
        }

        return "--";
    }

    private static string GetCodexCommunityRatingDisplayText(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.CommunityRatingKnown)
        {
            return "RC:--";
        }

        string shortLabel = FormatCodexCommunityRatingShortLabel(snapshot.CommunityRatingModelId, snapshot.CommunityRatingLabel);
        return "RC:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private static string FormatCodexCommunityRatingShortLabel(string modelId, string label)
    {
        string raw = !string.IsNullOrEmpty(label) ? label : FormatCodexCommunityRatingLabel(modelId);
        string lower = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.Length == 0)
        {
            return string.Empty;
        }

        Match gpt = Regex.Match(lower, "gpt[-\\s]*([0-9]+(?:\\.[0-9]+)?)\\s*[-\\s]*(xhigh|high|medium|low)?");
        if (gpt.Success)
        {
            string suffix = gpt.Groups[2].Value;
            string effort = string.Empty;
            if (suffix == "xhigh") effort = "X";
            else if (suffix == "high") effort = "H";
            else if (suffix == "medium") effort = "M";
            else if (suffix == "low") effort = "L";
            return gpt.Groups[1].Value + effort;
        }

        Match family = Regex.Match(lower, "(?:claude\\s*[-_\\s]*)?(opus|sonnet|haiku|fable)\\s*[-_\\s]*([0-9]+(?:\\.[0-9]+)?)?\\s*[-_\\s]*(xhigh|high|medium|low|max|ultra)?");
        if (family.Success)
        {
            string name = family.Groups[1].Value;
            string prefix = char.ToUpperInvariant(name[0]) + name.Substring(1, 1);
            string version = family.Groups[2].Success ? family.Groups[2].Value : string.Empty;
            string suffix = family.Groups[3].Value;
            string effort = FormatCodexCommunityRatingClaudeEffortSuffix(suffix);
            return prefix + version + effort;
        }

        string compact = Regex.Replace(raw, "[^A-Za-z0-9.]+", string.Empty);
        return compact.Length <= 10 ? compact : compact.Substring(0, 10);
    }

    private static string FormatCodexCommunityRatingClaudeEffortSuffix(string suffix)
    {
        suffix = (suffix ?? string.Empty).Trim().ToLowerInvariant();
        if (suffix == "xhigh") return "X";
        if (suffix == "high") return "H";
        if (suffix == "medium") return "M";
        if (suffix == "low") return "L";
        if (suffix == "max") return "MAX";
        if (suffix == "ultra") return "Ult";
        return string.Empty;
    }

    private static string FormatCodexCommunityRatingLabel(string modelId)
    {
        string raw = (modelId ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        string normalized = raw.Replace("_", "-");
        normalized = Regex.Replace(normalized, "^gpt-", "GPT-", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "^claude-", "Claude ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "-xhigh\\b", " xhigh", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "-high\\b", " high", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "-medium\\b", " medium", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "-low\\b", " low", RegexOptions.IgnoreCase);
        return normalized;
    }

    private static bool IsCodexModelIqCurrentForBeijingWindow(
        CodexRadarSnapshot snapshot,
        DateTime nowUtc)
    {
        if (snapshot == null || !snapshot.ModelIqDataDateKnown)
        {
            return false;
        }

        DateTime requiredWindow = TimeZoneUtilities.GetCurrentBeijingHalfDayStart(nowUtc);
        DateTime snapshotWindow = snapshot.ModelIqDataDateLocal.Date.AddHours(
            snapshot.ModelIqDataWindowKnown
                ? (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
                : 0);
        return snapshotWindow >= requiredWindow;
    }


    private void GetCodexModelSingleEfficiencyLabelAndColor(int efficiency, bool timeEfficiency, out string text, out Color color)
    {
        int lowThreshold = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent,
                timeEfficiency
                    ? this.currentSettings.CodexModelTimeEfficiencyLowThresholdPercent
                    : this.currentSettings.CodexModelTokenEfficiencyLowThresholdPercent));
        if (efficiency < lowThreshold)
        {
            text = timeEfficiency ? "耗时" : "低效";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            return;
        }

        if (efficiency > 100)
        {
            text = timeEfficiency ? "省时" : "高效";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            return;
        }

        text = "普通";
        color = DesignTokens.White(245);
    }


    private static Color GetCodexRadarLightGreen()
    {
        return Color.FromArgb(142, 242, 185);
    }


    private static void GetCodexModelIqNormalScoreRange(CodexRadarSnapshot snapshot, out int low, out int high)
    {
        low = 90;
        high = 110;
        if (snapshot != null && snapshot.ModelIqNormalRangeKnown)
        {
            low = snapshot.ModelIqNormalLowScore;
            high = snapshot.ModelIqNormalHighScore;
        }

        if (!NormalizeCodexModelIqNormalRange(ref low, ref high))
        {
            low = 90;
            high = 110;
        }
    }

    private static void GetCodexModelIqNormalRangeLabel(
        CodexRadarSnapshot snapshot,
        int score,
        out string text,
        out Color color)
    {
        int low;
        int high;
        GetCodexModelIqNormalScoreRange(snapshot, out low, out high);
        if (score < low)
        {
            text = "降智";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            return;
        }

        if (score > high)
        {
            text = "增智";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            return;
        }

        text = "常态";
        color = DesignTokens.White(245);
    }

    private void DrawCodexModelIqBaselineArcs(
        Graphics g,
        RectangleF arcRect,
        Pen normalPen,
        Pen deficitPen,
        Pen surplusPen,
        int score,
        double baselineScore,
        double displayMaxScore)
    {
        if (g == null || normalPen == null || deficitPen == null || surplusPen == null)
        {
            return;
        }

        displayMaxScore = Math.Max(1.0, displayMaxScore);
        double currentScore = Math.Max(0.0, Math.Min(displayMaxScore, score));
        baselineScore = Math.Max(0.0, Math.Min(displayMaxScore, baselineScore));
        double currentSweep = CodexModelIqScoreToArcSweep(currentScore, displayMaxScore);
        double baselineSweep = CodexModelIqScoreToArcSweep(baselineScore, displayMaxScore);
        g.DrawArc(normalPen, arcRect, -90.0f, 360.0f);

        if (currentSweep < baselineSweep - 0.5)
        {
            g.DrawArc(
                deficitPen,
                arcRect,
                -90.0f,
                -(float)(baselineSweep - currentSweep));
        }
        else if (currentSweep > baselineSweep + 0.5)
        {
            g.DrawArc(
                surplusPen,
                arcRect,
                -90.0f,
                (float)(currentSweep - baselineSweep));
        }
    }

    private static float CodexModelIqScoreToArcSweep(double score, double displayMaxScore)
    {
        if (double.IsNaN(score) ||
            double.IsInfinity(score) ||
            double.IsNaN(displayMaxScore) ||
            double.IsInfinity(displayMaxScore) ||
            displayMaxScore <= 0.0)
        {
            return 0.0f;
        }

        return (float)(360.0 * Math.Max(0.0, Math.Min(displayMaxScore, score)) / displayMaxScore);
    }


    // strokeScale > 1 thickens the whole bar - line, colored segment, average tick, current-value dot and
    // trend chevrons all derive from stroke, so they scale together. The even layouts pass 1.5.
    private void DrawCodexQuotaRadarVerticalLine(
        Graphics g,
        RectangleF rect,
        CodexQuotaRadarSnapshot quotaRadar,
        float strokeScale)
    {
        DrawCodexQuotaRadarVerticalLine(g, rect, quotaRadar, strokeScale, null);
    }

    // dotColorOverride lets the four OLED-safe restyle schemes (added in 1.0.3.44) swap the
    // current-value marker away from its classic cyan-blue - null keeps the original color for
    // Classic/EvenGrid/EvenRow, which are unaffected by this overload.
    private void DrawCodexQuotaRadarVerticalLine(
        Graphics g,
        RectangleF rect,
        CodexQuotaRadarSnapshot quotaRadar,
        float strokeScale,
        Color? dotColorOverride)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        float stroke = Math.Max(1.0f, Math.Min(S(3), rect.Width * 0.42f)) * Math.Max(0.1f, strokeScale);
        float x = rect.Left + rect.Width / 2.0f;
        float top = rect.Top + S(1);
        float bottom = rect.Bottom - S(1);
        if (bottom <= top)
        {
            return;
        }

        CodexQuotaRadarTier tier = GetCodexQuotaRadarRepresentativeTier(quotaRadar);
        if (tier == null || !tier.CurrentKnown)
        {
            using (Pen emptyPen = new Pen(DesignTokens.White(34), stroke))
            {
                emptyPen.StartCap = LineCap.Round;
                emptyPen.EndCap = LineCap.Round;
                g.DrawLine(emptyPen, x, top, x, bottom);
            }

            return;
        }

        double minValue = tier.TrendRangeKnown
            ? Math.Max(0.0, tier.TrendMinSevenDayUsd)
            : Math.Max(0.0, tier.SevenDayUsd);
        double maxValue = tier.TrendRangeKnown
            ? Math.Max(minValue, tier.TrendMaxSevenDayUsd)
            : Math.Max(tier.SevenDayUsd, 0.0);
        if (tier.PreviousKnown)
        {
            maxValue = Math.Max(maxValue, tier.PreviousSevenDayUsd);
            if (!tier.TrendRangeKnown)
            {
                minValue = Math.Min(minValue, Math.Max(0.0, tier.PreviousSevenDayUsd));
            }
        }

        if (tier.AverageKnown)
        {
            maxValue = Math.Max(maxValue, tier.AverageSevenDayUsd);
            if (!tier.TrendRangeKnown)
            {
                minValue = Math.Min(minValue, Math.Max(0.0, tier.AverageSevenDayUsd));
            }
        }

        if (!tier.TrendRangeKnown && minValue <= 0.0)
        {
            maxValue = Math.Max(1.0, maxValue * 1.08);
        }

        if (maxValue - minValue < 0.005)
        {
            double padding = Math.Max(1.0, Math.Abs(maxValue) * 0.04);
            minValue = Math.Max(0.0, minValue - padding);
            maxValue += padding;
        }

        float currentY = GetCodexQuotaRadarLineY(top, bottom, tier.SevenDayUsd, minValue, maxValue);
        float previousY = tier.PreviousKnown
            ? GetCodexQuotaRadarLineY(top, bottom, tier.PreviousSevenDayUsd, minValue, maxValue)
            : currentY;
        float averageY = tier.AverageKnown
            ? GetCodexQuotaRadarLineY(top, bottom, tier.AverageSevenDayUsd, minValue, maxValue)
            : float.NaN;
        Color segmentColor = GetCodexQuotaRadarVerticalSegmentColor(tier, currentY, averageY, top, bottom);

        using (Pen basePen = new Pen(Color.FromArgb(136, 128, 134, 142), stroke))
        using (Pen segmentPen = new Pen(segmentColor, stroke))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            segmentPen.StartCap = LineCap.Round;
            segmentPen.EndCap = LineCap.Round;

            g.DrawLine(basePen, x, bottom, x, top);

            if (tier.PreviousKnown && Math.Abs(tier.SevenDayUsd - tier.PreviousSevenDayUsd) > 0.005)
            {
                DrawCodexQuotaRadarVerticalSegment(g, segmentPen, x, currentY, previousY, top, bottom);
            }
        }

        if (!float.IsNaN(averageY))
        {
            using (Pen averagePen = new Pen(DesignTokens.White(214), Math.Max(1.0f, S(1))))
            {
                averagePen.StartCap = LineCap.Round;
                averagePen.EndCap = LineCap.Round;
                averageY = Math.Max(top, Math.Min(bottom, averageY));
                float half = Math.Min(rect.Width * 0.48f, Math.Max(S(2), stroke * 1.15f));
                g.DrawLine(
                    averagePen,
                    Math.Max(rect.Left, x - half),
                    averageY,
                    Math.Min(rect.Right, x + half),
                    averageY);
            }

            DrawCodexQuotaRadarTrendArrows(g, x, top, bottom, currentY, averageY, tier, stroke);
        }

        DrawCodexQuotaRadarCurrentPoint(g, x, currentY, stroke, top, bottom, dotColorOverride);
    }

    private void DrawCodexQuotaRadarTrendArrows(
        Graphics g,
        float x,
        float top,
        float bottom,
        float currentY,
        float averageY,
        CodexQuotaRadarTier tier,
        float stroke)
    {
        if (tier == null || !tier.PreviousKnown || bottom <= top)
        {
            return;
        }

        const double epsilon = 0.005;
        bool up = tier.SevenDayUsd > tier.PreviousSevenDayUsd + epsilon;
        bool down = tier.SevenDayUsd < tier.PreviousSevenDayUsd - epsilon;
        if (!up && !down)
        {
            return;
        }

        averageY = Math.Max(top, Math.Min(bottom, averageY));
        currentY = Math.Max(top, Math.Min(bottom, currentY));
        float zoneStart;
        float zoneEnd;
        if (currentY < averageY)
        {
            zoneStart = averageY;
            zoneEnd = bottom;
        }
        else
        {
            zoneStart = top;
            zoneEnd = averageY;
        }

        if (Math.Abs(zoneEnd - zoneStart) < S(10))
        {
            return;
        }

        Color color = up
            ? Color.FromArgb(224, 142, 242, 185)
            : Color.FromArgb(224, 255, 152, 152);
        float lineWidth = Math.Max(1.0f, stroke * 0.22f);
        using (Pen arrowPen = new Pen(color, lineWidth))
        {
            arrowPen.StartCap = LineCap.Round;
            arrowPen.EndCap = LineCap.Round;
            arrowPen.LineJoin = LineJoin.Round;
            DrawCodexQuotaRadarChevronLine(g, arrowPen, x, zoneStart + (zoneEnd - zoneStart) / 3.0f, up, stroke);
            DrawCodexQuotaRadarChevronLine(g, arrowPen, x, zoneStart + (zoneEnd - zoneStart) * 2.0f / 3.0f, up, stroke);
        }
    }

    private void DrawCodexQuotaRadarChevronLine(Graphics g, Pen pen, float x, float y, bool up, float stroke)
    {
        float dotDiameter = Math.Max(S(1), stroke * 0.55f);
        float width = Math.Max(S(2), dotDiameter * 1.35f);
        // 120° apex angle: half-apex 60°, so height = width / (2 * tan(60°)) = width / 3.4641.
        // Widening the angle from the old ~44° flattens each chevron into a shallow, wide arrow.
        float height = Math.Max(S(1), width / 3.4641f);
        PointF left = up
            ? new PointF(x - width, y + height)
            : new PointF(x - width, y - height);
        PointF tip = up
            ? new PointF(x, y - height)
            : new PointF(x, y + height);
        PointF right = up
            ? new PointF(x + width, y + height)
            : new PointF(x + width, y - height);
        g.DrawLine(pen, left, tip);
        g.DrawLine(pen, tip, right);
    }

    private static Color GetCodexQuotaRadarVerticalSegmentColor(
        CodexQuotaRadarTier tier,
        float currentY,
        float averageY,
        float top,
        float bottom)
    {
        if (tier == null)
        {
            return DesignTokens.White(180);
        }

        if (float.IsNaN(averageY) || bottom <= top)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 238);
        }

        currentY = Math.Max(top, Math.Min(bottom, currentY));
        averageY = Math.Max(top, Math.Min(bottom, averageY));
        if (currentY <= averageY)
        {
            float span = Math.Max(1.0f, averageY - top);
            float progressTowardTop = (averageY - currentY) / span;
            return progressTowardTop >= 0.5f
                ? DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 238)
                : Color.FromArgb(238, 142, 242, 185);
        }

        float lowerSpan = Math.Max(1.0f, bottom - averageY);
        float progressTowardBottom = (currentY - averageY) / lowerSpan;
        return progressTowardBottom < 0.5f
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 238)
            : DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
    }

    private static void DrawCodexQuotaRadarCurrentPoint(
        Graphics g,
        float x,
        float y,
        float stroke,
        float top,
        float bottom,
        Color? dotColorOverride)
    {
        float diameter = Math.Max(1.0f, stroke);
        float radius = diameter / 2.0f;
        y = Math.Max(top + radius, Math.Min(bottom - radius, y));
        Color dotColor = dotColorOverride.HasValue ? dotColorOverride.Value : Color.FromArgb(246, 56, 189, 248);
        using (SolidBrush brush = new SolidBrush(dotColor))
        {
            g.FillEllipse(brush, x - radius, y - radius, diameter, diameter);
        }
    }

    private static float GetCodexQuotaRadarLineY(
        float top,
        float bottom,
        double value,
        double minValue,
        double maxValue)
    {
        double span = Math.Max(1.0, maxValue - minValue);
        double ratio = Math.Max(0.0, Math.Min(1.0, (value - minValue) / span));
        return bottom - (float)((bottom - top) * ratio);
    }

    private void DrawCodexQuotaRadarVerticalSegment(
        Graphics g,
        Pen pen,
        float x,
        float y1,
        float y2,
        float top,
        float bottom)
    {
        float segmentTop = Math.Max(top, Math.Min(y1, y2));
        float segmentBottom = Math.Min(bottom, Math.Max(y1, y2));
        if (segmentBottom <= segmentTop)
        {
            return;
        }

        g.DrawLine(pen, x, segmentBottom, x, segmentTop);
    }

    private static CodexQuotaRadarTier GetCodexQuotaRadarRepresentativeTier(
        CodexQuotaRadarSnapshot quotaRadar)
    {
        CodexQuotaRadarTier pro20x = FindCodexQuotaRadarTier(quotaRadar, QuotaRadarTierPro20x);
        if (pro20x != null && pro20x.CurrentKnown)
        {
            return pro20x;
        }

        CodexQuotaRadarTier[] tiers = quotaRadar == null ? null : quotaRadar.Tiers;
        if (tiers == null)
        {
            return null;
        }

        for (int i = 0; i < tiers.Length; i++)
        {
            if (tiers[i] != null && tiers[i].CurrentKnown)
            {
                return tiers[i];
            }
        }

        return null;
    }


    private bool AdvanceCodexApiServiceAlertRotation()
    {
        CodexConnectionAlertCandidate[] candidates = GetCodexApiServiceAlertCandidates();
        if (candidates.Length == 0)
        {
            bool hadAlert = !string.IsNullOrEmpty(this.codexApiServiceAlertSignature);
            this.codexApiServiceAlertSignature = string.Empty;
            this.codexApiServiceAlertIndex = 0;
            this.codexApiServiceAlertNamePhase = true;
            return hadAlert;
        }

        string signature = BuildCodexConnectionAlertSignature(candidates);
        if (!string.Equals(signature, this.codexApiServiceAlertSignature, StringComparison.Ordinal))
        {
            this.codexApiServiceAlertSignature = signature;
            this.codexApiServiceAlertIndex = 0;
            this.codexApiServiceAlertNamePhase = true;
            return true;
        }

        if (this.codexApiServiceAlertNamePhase)
        {
            this.codexApiServiceAlertNamePhase = false;
        }
        else
        {
            this.codexApiServiceAlertNamePhase = true;
            this.codexApiServiceAlertIndex = (this.codexApiServiceAlertIndex + 1) % candidates.Length;
        }

        return true;
    }

    private CodexConnectionAlertCandidate[] GetCodexApiServiceAlertCandidates()
    {
        List<CodexConnectionAlertCandidate> candidates = new List<CodexConnectionAlertCandidate>();
        bool online;
        ServiceHealthState radarHealth;
        ServiceHealthState claudeHealth;
        ServiceHealthState openAiHealth;
        bool radarChecking = false;
        bool claudeChecking = false;
        bool openAiChecking = false;

        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null)
        {
            online = this.codexRadarRandomTestSnapshot.NetworkAvailable;
            radarHealth = this.codexRadarRandomTestSnapshot.RadarHealth;
            claudeHealth = this.codexRadarRandomTestSnapshot.ClaudeHealth;
            openAiHealth = this.codexRadarRandomTestSnapshot.OpenAiHealth;
        }
        else
        {
            lock (this.serviceHealthLock)
            {
                online = this.serviceNetworkAvailable;
                radarHealth = online ? this.radarServiceHealth : ServiceHealthState.Offline;
                claudeHealth = online ? this.claudeServiceHealth : ServiceHealthState.Offline;
                openAiHealth = online ? this.openAiServiceHealth : ServiceHealthState.Offline;
            }

            lock (this.codexRadarStatusLock)
            {
                radarChecking = this.codexRadarStatusRequestRunning || this.codexRadarServiceProbeRunning;
            }

            lock (this.claudeStatusLock)
            {
                claudeChecking = this.claudeStatusRequestRunning;
            }

            lock (this.openAiStatusLock)
            {
                openAiChecking = this.openAiStatusRequestRunning;
            }
        }

        if (!online)
        {
            radarHealth = ServiceHealthState.Offline;
            claudeHealth = ServiceHealthState.Offline;
            AddServiceHealthAlertCandidate(candidates, "rader", "Radar", radarHealth, false);
            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = "openai:offline",
                Name = "OpenAI",
                Reason = "无网络",
                Color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)
            });
            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = "deepseek:offline",
                Name = "DeepSeek",
                Reason = "无网络",
                Color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)
            });

            AddServiceHealthAlertCandidate(candidates, "claude", "Claude", claudeHealth, false);
            AddClaudeCodeUsageAlertCandidate(candidates);
            return GetDebouncedCodexApiServiceAlertCandidates(candidates.ToArray());
        }

        AddServiceHealthAlertCandidate(candidates, "rader", "Radar", radarHealth, radarChecking);
        AddServiceHealthAlertCandidate(candidates, "openai", "OpenAI", openAiHealth, openAiChecking);
        AddDeepSeekServiceAlertCandidate(candidates);
        AddServiceHealthAlertCandidate(candidates, "claude", "Claude", claudeHealth, claudeChecking);
        AddClaudeCodeUsageAlertCandidate(candidates);
        return GetDebouncedCodexApiServiceAlertCandidates(candidates.ToArray());
    }

    private CodexConnectionAlertCandidate[] GetDebouncedCodexApiServiceAlertCandidates(
        CodexConnectionAlertCandidate[] candidates)
    {
        bool bypass = this.currentSettings == null ||
            this.currentSettings.CodexRadarRandomTestEnabled ||
            this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off;
        lock (this.codexApiServiceAlertDebounceLock)
        {
            return ApplyCodexApiServiceAlertDebounce(
                this.codexApiServiceAlertDebounceStates,
                candidates,
                DateTime.UtcNow,
                TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
                bypass);
        }
    }

    private void ResetCodexApiServiceAlertDebounceForDisplayContextSwitch()
    {
        // Software switches restore cached provider health, but alert stability belongs to the
        // visible context. Start a fresh debounce window so old cached failures do not flash in.
        lock (this.codexApiServiceAlertDebounceLock)
        {
            ClearCodexApiServiceAlertDebounceStates(this.codexApiServiceAlertDebounceStates);
            this.codexApiServiceAlertSignature = string.Empty;
            this.codexApiServiceAlertIndex = 0;
            this.codexApiServiceAlertNamePhase = true;
        }
    }

    private static void ClearCodexApiServiceAlertDebounceStates(
        Dictionary<string, ServiceAlertDebounceState> states)
    {
        if (states != null)
        {
            states.Clear();
        }
    }

    private static CodexConnectionAlertCandidate[] ApplyCodexApiServiceAlertDebounce(
        Dictionary<string, ServiceAlertDebounceState> states,
        CodexConnectionAlertCandidate[] candidates,
        DateTime nowUtc,
        TimeSpan debounceWindow,
        bool bypass)
    {
        List<ServiceAlertCandidate> sharedCandidates = new List<ServiceAlertCandidate>();
        if (candidates != null)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                CodexConnectionAlertCandidate candidate = candidates[i];
                if (candidate != null)
                {
                    sharedCandidates.Add(new ServiceAlertCandidate
                    {
                        Key = candidate.Key ?? string.Empty,
                        Name = candidate.Name ?? string.Empty,
                        Reason = candidate.Reason ?? string.Empty,
                        State = string.Empty,
                        Color = candidate.Color,
                        Checking = IsCodexConnectionAlertChecking(candidate)
                    });
                }
            }
        }

        List<ServiceAlertCandidate> debounced = ServiceAlertDebouncer.Apply(
            states,
            sharedCandidates,
            nowUtc,
            debounceWindow,
            bypass);
        List<CodexConnectionAlertCandidate> result = new List<CodexConnectionAlertCandidate>();
        for (int i = 0; i < debounced.Count; i++)
        {
            ServiceAlertCandidate candidate = debounced[i];
            if (candidate != null)
            {
                result.Add(new CodexConnectionAlertCandidate
                {
                    Key = candidate.Key ?? string.Empty,
                    Name = candidate.Name ?? string.Empty,
                    Reason = candidate.Reason ?? string.Empty,
                    Color = candidate.Color
                });
            }
        }

        return result.ToArray();
    }

    private static bool IsCodexConnectionAlertChecking(CodexConnectionAlertCandidate candidate)
    {
        return candidate != null &&
            (candidate.Key ?? string.Empty).IndexOf(":checking", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetCodexConnectionAlertServiceKey(string key)
    {
        string raw = (key ?? string.Empty).Trim();
        int colon = raw.IndexOf(':');
        return colon <= 0 ? raw : raw.Substring(0, colon);
    }

    private static string BuildSingleCodexConnectionAlertSignature(CodexConnectionAlertCandidate candidate)
    {
        if (candidate == null)
        {
            return string.Empty;
        }

        return (candidate.Key ?? string.Empty) +
            ":" +
            (candidate.Name ?? string.Empty) +
            ":" +
            (candidate.Reason ?? string.Empty) +
            ":" +
            candidate.Color.ToArgb().ToString(CultureInfo.InvariantCulture);
    }

    private static CodexConnectionAlertCandidate[] CloneCodexConnectionAlertCandidates(
        CodexConnectionAlertCandidate[] candidates)
    {
        if (candidates == null || candidates.Length == 0)
        {
            return new CodexConnectionAlertCandidate[0];
        }

        CodexConnectionAlertCandidate[] clone = new CodexConnectionAlertCandidate[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            clone[i] = CloneCodexConnectionAlertCandidate(candidates[i]);
        }

        return clone;
    }

    private static CodexConnectionAlertCandidate CloneCodexConnectionAlertCandidate(
        CodexConnectionAlertCandidate candidate)
    {
        if (candidate == null)
        {
            return null;
        }

        return new CodexConnectionAlertCandidate
        {
            Key = candidate.Key,
            Name = candidate.Name,
            Reason = candidate.Reason,
            Color = candidate.Color
        };
    }

    private static void AddServiceHealthAlertCandidate(
        List<CodexConnectionAlertCandidate> candidates,
        string key,
        string name,
        ServiceHealthState state,
        bool checking)
    {
        if (checking)
        {
            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = key + ":checking",
                Name = name,
                Reason = "检测中",
                Color = DesignTokens.Colors.Warning
            });
            return;
        }

        if (state == ServiceHealthState.Normal || state == ServiceHealthState.Unknown)
        {
            return;
        }

        candidates.Add(new CodexConnectionAlertCandidate
        {
            Key = key + ":" + state.ToString(),
            Name = name,
            Reason = GetServiceHealthAlertReason(state),
            Color = GetServiceHealthAlertColor(state)
        });
    }

    private void AddDeepSeekServiceAlertCandidate(List<CodexConnectionAlertCandidate> candidates)
    {
        ColorlessDeepSeekAlert alert = DeepSeekBalanceMonitor.BuildAlert();
        if (alert != null)
        {
            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = alert.Key,
                Name = alert.Name,
                Reason = alert.Reason,
                Color = GetDeepSeekApiAlertColor(alert.Snapshot)
            });
        }
    }

    private static Color GetDeepSeekApiAlertColor(DeepSeekBalanceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        if (snapshot.ServiceRequestRunning && !snapshot.ServiceKnown)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
        }

        string errorCode = snapshot.ServiceErrorCode ?? string.Empty;
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

    private static string GetServiceHealthAlertReason(ServiceHealthState state)
    {
        if (state == ServiceHealthState.Degraded)
        {
            return "服务降级";
        }

        if (state == ServiceHealthState.Incomplete)
        {
            return "数据不完整";
        }

        if (state == ServiceHealthState.Offline)
        {
            return "无网络";
        }

        if (state == ServiceHealthState.Unavailable)
        {
            return "服务不可用";
        }

        if (state == ServiceHealthState.Unreachable)
        {
            return "无法连接";
        }

        return "状态未知";
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


    private void GetQuotaResetDisplayText(
        string resetText,
        bool quotaProtected,
        CodexRadarSnapshot radarSnapshot,
        bool dateText,
        out string displayText,
        out Color displayColor)
    {
        if (IsCodexRadarSpeedWindowCurrentlyOpen(radarSnapshot, DateTime.Now))
        {
            int phase = Math.Abs(this.renderTickCount % 3);
            if (phase == 0)
            {
                displayText = "速蹬！";
                displayColor = GetCodexRadarSpeedWindowGoldColor();
                return;
            }

            if (phase == 1)
            {
                displayText = resetText;
                displayColor = DesignTokens.TextStrong(226);
                return;
            }

            if (phase == 2)
            {
                if (TryGetCodexRadarExtraResetTargetText(radarSnapshot, dateText, out displayText))
                {
                    displayColor = DesignTokens.Colors.Warning;
                    return;
                }
            }
        }

        if (quotaProtected)
        {
            displayText = "已重置";
            displayColor = GetCodexRadarSpeedWindowGoldColor();
            return;
        }

        displayText = resetText;
        displayColor = DesignTokens.TextStrong(226);
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

    private bool TryGetCodexRadarExtraResetTargetText(
        CodexRadarSnapshot snapshot,
        bool dateText,
        out string text)
    {
        text = string.Empty;
        if (snapshot == null)
        {
            return false;
        }

        DateTime local = DateTime.MinValue;
        if (snapshot.SpeedWindowClosedAtKnown)
        {
            local = snapshot.SpeedWindowClosedAtLocal;
        }

        if (local == DateTime.MinValue)
        {
            return false;
        }

        text = TimeZoneUtilities.ConvertToDisplayTime(local, this.currentSettings)
            .ToString(dateText ? "MM/dd" : "HH:mm", CultureInfo.CurrentCulture);
        return true;
    }

    private static Color GetCodexRadarSpeedWindowGoldColor()
    {
        return Color.FromArgb(255, 194, 72);
    }

    private CodexRadarSnapshot GetCodexRadarDisplaySnapshot()
    {
        CodexRadarSnapshot snapshot;
        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null)
        {
            return this.codexRadarRandomTestSnapshot.Radar.Clone();
        }

        if (this.currentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            snapshot = BuildTestCodexRadarSnapshot(this.currentSettings.CodexRadarTestMode);
            ApplyCodexModelEfficiencyBaselineOverride(snapshot);
            ApplyCodexModelIqTestOverride(snapshot);
            ApplyCodexModelEfficiencyTestOverride(snapshot);
            return snapshot;
        }

        lock (this.codexRadarStatusLock)
        {
            snapshot = this.codexRadarSnapshot != null
                ? this.codexRadarSnapshot.Clone()
                : CodexRadarSnapshot.CreateDefault();
        }

        ApplyCodexModelEfficiencyBaselineOverride(snapshot);
        ApplyCodexModelIqTestOverride(snapshot);
        ApplyCodexModelEfficiencyTestOverride(snapshot);
        return snapshot;
    }

    private CodexRadarSnapshot BuildTestCodexRadarSnapshot(CodexRadarTestMode mode)
    {
        DateTime now = DateTime.Now;
        CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
        snapshot.CheckedAtLocal = now;
        snapshot.CheckedAtKnown = true;
        snapshot.ModelIqKnown = true;
        snapshot.QuotaRadar = BuildCodexQuotaRadarTestSnapshot(1716.90, 1614.09);
        ApplyCodexCommunityRatingSnapshot(
            snapshot,
            "gpt-5.5-medium",
            "GPT-5.5 medium",
            7.8,
            88,
            now);
        if (mode == CodexRadarTestMode.Open)
        {
            ApplyCodexModelIqScore(snapshot, 9);
            return snapshot;
        }

        if (mode == CodexRadarTestMode.Closed)
        {
            ApplyCodexModelIqScore(snapshot, 6);
            return snapshot;
        }

            ApplyCodexModelIqScore(snapshot, GetCodexModelIqAbsoluteBaselinePassed());
        return snapshot;
    }

    private void ApplyCodexModelIqTestOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !this.currentSettings.CodexModelIqTestEnabled)
        {
            return;
        }

        ApplyCodexModelIqScore(snapshot, this.currentSettings.CodexModelIqTestPassed);
    }

    private void ApplyCodexModelEfficiencyBaselineOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqEfficiencyInputKnown)
        {
            return;
        }

        bool changed = false;
        int tokenEfficiency;
        if (TryCalculateCodexModelEfficiencyPercentForMode(
            snapshot,
            true,
            this.currentSettings.CodexModelTokenEfficiencyBaselineMode,
            out tokenEfficiency))
        {
            snapshot.ModelIqTokenEfficiencyPercent = tokenEfficiency;
            changed = true;
        }

        int timeEfficiency;
        if (TryCalculateCodexModelEfficiencyPercentForMode(
            snapshot,
            false,
            this.currentSettings.CodexModelTimeEfficiencyBaselineMode,
            out timeEfficiency))
        {
            snapshot.ModelIqTimeEfficiencyPercent = timeEfficiency;
            changed = true;
        }

        if (changed)
        {
            snapshot.ModelIqEfficiencyKnown = true;
        }
    }

    private void ApplyCodexModelEfficiencyTestOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !this.currentSettings.CodexModelEfficiencyTestEnabled)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyPercent,
                this.currentSettings.CodexModelTokenEfficiencyTestPercent));
        snapshot.ModelIqTimeEfficiencyPercent = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyPercent,
                this.currentSettings.CodexModelTimeEfficiencyTestPercent));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private bool TryCalculateCodexModelEfficiencyPercentForMode(
        CodexRadarSnapshot snapshot,
        bool tokenEfficiency,
        CodexModelBaselineMode mode,
        out int efficiencyPercent)
    {
        efficiencyPercent = 100;
        if (snapshot == null || !snapshot.ModelIqEfficiencyInputKnown)
        {
            return false;
        }

        if (mode == CodexModelBaselineMode.Absolute)
        {
            return TryCalculateCodexModelEfficiencyPercent(
                snapshot.ModelIqEfficiencyPassed,
                tokenEfficiency ? snapshot.ModelIqEfficiencyTotalTokens : snapshot.ModelIqEfficiencySerialSeconds,
                tokenEfficiency
                    ? this.currentSettings.CodexModelTokenEfficiencyBaselinePassed
                    : this.currentSettings.CodexModelTimeEfficiencyBaselinePassed,
                tokenEfficiency
                    ? this.currentSettings.CodexModelTokenEfficiencyBaselineTokens
                    : this.currentSettings.CodexModelTimeEfficiencyBaselineSeconds,
                out efficiencyPercent);
        }

        double baselinePassed;
        double baselineValue;
        if (!TryGetCodexModelEfficiencyBaseline(
            snapshot,
            tokenEfficiency,
            mode,
            out baselinePassed,
            out baselineValue))
        {
            return false;
        }

        return TryCalculateCodexModelEfficiencyPercent(
            snapshot.ModelIqEfficiencyPassed,
            tokenEfficiency ? snapshot.ModelIqEfficiencyTotalTokens : snapshot.ModelIqEfficiencySerialSeconds,
            baselinePassed,
            baselineValue,
            out efficiencyPercent);
    }

    private static bool TryCalculateCodexModelEfficiencyPercent(
        double currentPassed,
        double currentValue,
        double baselinePassed,
        double baselineValue,
        out int efficiencyPercent)
    {
        efficiencyPercent = 100;
        if (currentPassed <= 0.0 || currentValue <= 0.0 || baselinePassed <= 0.0 || baselineValue <= 0.0)
        {
            return false;
        }

        double baselineRate = baselinePassed / baselineValue;
        if (baselineRate <= 0.0)
        {
            return false;
        }

        efficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentValue) / baselineRate * 100.0, MidpointRounding.AwayFromZero));
        return true;
    }

    private static int NormalizeCodexModelIqValidTaskCount(double validTasks)
    {
        if (double.IsNaN(validTasks) || double.IsInfinity(validTasks) || validTasks <= 0.0)
        {
            return CodexModelIqNominalTasks;
        }

        int rounded = (int)Math.Round(validTasks, MidpointRounding.AwayFromZero);
        return Math.Max(WidgetSettings.MinCodexModelIqValidTasks, Math.Min(WidgetSettings.MaxCodexModelIqValidTasks, rounded));
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

    private void ApplyCodexModelIqScore(CodexRadarSnapshot snapshot, int passed)
    {
        if (snapshot == null)
        {
            return;
        }

        int validTasks = CodexModelIqNominalTasks;
        passed = Math.Max(WidgetSettings.MinCodexModelIqPassed, Math.Min(validTasks, passed));
        snapshot.ModelIqKnown = true;
        snapshot.ModelIqPassedKnown = true;
        snapshot.ModelIqPassed = passed;
        snapshot.ModelIqValidTasks = validTasks;
        snapshot.ModelIqPassRatePercent = CalculateCodexModelIqScore(passed, validTasks);
        snapshot.ModelIqTokenEfficiencyPercent = 100;
        snapshot.ModelIqTimeEfficiencyPercent = 100;
        snapshot.ModelIqEfficiencyKnown = true;
        if (!snapshot.ModelIqDataDateKnown)
        {
            DateTime beijingWindow = TimeZoneUtilities.GetCurrentBeijingHalfDayStart(DateTime.UtcNow);
            snapshot.ModelIqDataDateLocal = beijingWindow.Date;
            snapshot.ModelIqDataWindowStartHourLocal = beijingWindow.Hour >= 12 ? 12 : 0;
            snapshot.ModelIqDataDateKnown = true;
            snapshot.ModelIqDataWindowKnown = true;
        }

        if (!snapshot.ModelIqDataLabelKnown)
        {
            snapshot.ModelIqDataLabel = FormatCodexModelIqDataLabel(
                string.Empty,
                snapshot.ModelIqDataDateLocal,
                snapshot.ModelIqDataWindowStartHourLocal,
                snapshot.ModelIqDataWindowKnown);
            snapshot.ModelIqDataLabelKnown = snapshot.ModelIqDataLabel.Length > 0;
        }

        if (!snapshot.ModelIqRefreshedAtKnown)
        {
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
        }

        snapshot.ModelIqRefreshSucceeded = true;
        ApplyCodexModelIqNormalRange(
            snapshot,
            CodexModelIqWebsiteNormalLowScore,
            CodexModelIqWebsiteNormalHighScore);
        snapshot.ModelIqStatus = InferCodexModelIqStatusFromScore(snapshot.ModelIqPassRatePercent);
        UpsertCodexModelHistoryPoint(
            snapshot.ModelIqHistory,
            snapshot.ModelIqDataDateLocal.Date.AddHours(snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0),
            snapshot.ModelIqPassRatePercent);
        snapshot.ModelIqHistory = NormalizeCodexModelHistory(snapshot.ModelIqHistory);
    }

    private int GetCodexModelIqAbsoluteBaselinePassed()
    {
        int validTasks = GetCodexModelIqManualBaselineValidTasks();
        return Math.Max(
            WidgetSettings.MinCodexModelIqPassed,
            Math.Min(validTasks, this.currentSettings.CodexModelIqBaselinePassed));
    }

    private double GetCodexModelIqBaselinePassed(CodexRadarSnapshot snapshot)
    {
        double passed;
        int validTasks;
        if (TryGetCodexModelIqBaselineRatio(snapshot, out passed, out validTasks))
        {
            return Math.Max(
                WidgetSettings.MinCodexModelIqPassed,
                Math.Min(validTasks, passed));
        }

        return GetCodexModelIqAbsoluteBaselinePassed();
    }

    private double GetCodexModelIqBaselineScore(CodexRadarSnapshot snapshot)
    {
        double passed;
        int validTasks;
        if (TryGetCodexModelIqBaselineRatio(snapshot, out passed, out validTasks))
        {
            double scale = InferCodexModelIqScoreScale(snapshot);
            return NormalizePassRateValue(passed / Math.Max(1.0, validTasks) * scale);
        }

        return NormalizePassRateValue(GetCodexModelIqAbsoluteBaselinePassed() /
            (double)Math.Max(1, GetCodexModelIqManualBaselineValidTasks()) *
            InferCodexModelIqScoreScale(snapshot));
    }

    private bool TryGetCodexModelIqBaselineRatio(CodexRadarSnapshot snapshot, out double passed, out int validTasks)
    {
        if (this.currentSettings.CodexModelIqBaselineAutoEnabled &&
            TryGetCodexModelIqAutoBaselineRatio(snapshot, out passed, out validTasks))
        {
            return true;
        }

        validTasks = GetCodexModelIqManualBaselineValidTasks();
        passed = Math.Max(
            WidgetSettings.MinCodexModelIqPassed,
            Math.Min(validTasks, this.currentSettings.CodexModelIqBaselinePassed));
        return validTasks > 0;
    }

    private bool TryGetCodexModelIqAutoBaselineRatio(CodexRadarSnapshot snapshot, out double passed, out int validTasks)
    {
        validTasks = GetCodexModelIqWebsiteValidTasks(snapshot);
        passed = 0.0;
        if (validTasks <= 0)
        {
            return false;
        }

        double scale = InferCodexModelIqScoreScale(snapshot);
        double targetScore = 0.0;
        int normalLow = 0;
        int normalHigh = 0;
        if (snapshot != null &&
            snapshot.ModelIqNormalRangeKnown)
        {
            normalLow = snapshot.ModelIqNormalLowScore;
            normalHigh = snapshot.ModelIqNormalHighScore;
            if (NormalizeCodexModelIqNormalRange(ref normalLow, ref normalHigh))
            {
                targetScore = (normalLow + normalHigh) / 2.0;
            }
        }

        if (targetScore <= 0.0)
        {
            targetScore = GetCodexModelIqManualBaselineScoreFallback(snapshot);
        }

        if (scale <= 0.0 || targetScore <= 0.0)
        {
            return false;
        }

        passed = Math.Round(targetScore / scale * validTasks, MidpointRounding.AwayFromZero);
        passed = Math.Max(WidgetSettings.MinCodexModelIqPassed, Math.Min(validTasks, passed));
        return true;
    }

    private double GetCodexModelIqManualBaselineScoreFallback(CodexRadarSnapshot snapshot)
    {
        int validTasks = GetCodexModelIqManualBaselineValidTasks();
        int passed = Math.Max(
            WidgetSettings.MinCodexModelIqPassed,
            Math.Min(validTasks, this.currentSettings.CodexModelIqBaselinePassed));
        return passed / (double)Math.Max(1, validTasks) * InferCodexModelIqScoreScale(snapshot);
    }

    private int GetCodexModelIqManualBaselineValidTasks()
    {
        return NormalizeCodexModelIqValidTaskCount(
            this.currentSettings.CodexModelIqBaselineValidTasks <= 0
                ? WidgetSettings.DefaultCodexModelIqBaselineValidTasks
                : this.currentSettings.CodexModelIqBaselineValidTasks);
    }

    private static int GetCodexModelIqWebsiteValidTasks(CodexRadarSnapshot snapshot)
    {
        if (snapshot != null)
        {
            if (snapshot.ModelIqValidTasks > 0)
            {
                return NormalizeCodexModelIqValidTaskCount(snapshot.ModelIqValidTasks);
            }

            List<CodexModelHistoryPoint> points = GetRecentCodexModelHistory(snapshot);
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (points[i] != null && points[i].Tasks > 0.0)
                {
                    return NormalizeCodexModelIqValidTaskCount(points[i].Tasks);
                }
            }
        }

        return WidgetSettings.DefaultCodexModelIqBaselineValidTasks;
    }

    private static double InferCodexModelIqScoreScale(CodexRadarSnapshot snapshot)
    {
        double total = 0.0;
        int count = 0;
        AddCodexModelIqScoreScaleCandidate(
            snapshot != null ? snapshot.ModelIqPassRatePercent : 0.0,
            snapshot != null && snapshot.ModelIqPassedKnown ? snapshot.ModelIqPassed : 0.0,
            snapshot != null ? snapshot.ModelIqValidTasks : 0.0,
            ref total,
            ref count);

        List<CodexModelHistoryPoint> points = GetRecentCodexModelHistory(snapshot);
        for (int i = 0; i < points.Count; i++)
        {
            CodexModelHistoryPoint point = points[i];
            if (point != null)
            {
                AddCodexModelIqScoreScaleCandidate(point.Score, point.Passed, point.Tasks, ref total, ref count);
            }
        }

        if (count > 0)
        {
            return Math.Max(1.0, total / count);
        }

        return CodexModelIqWebsiteScoreScale;
    }

    private static void AddCodexModelIqScoreScaleCandidate(
        double score,
        double passed,
        double validTasks,
        ref double total,
        ref int count)
    {
        if (double.IsNaN(score) ||
            double.IsInfinity(score) ||
            double.IsNaN(passed) ||
            double.IsInfinity(passed) ||
            double.IsNaN(validTasks) ||
            double.IsInfinity(validTasks) ||
            score <= 0.0 ||
            passed <= 0.0 ||
            validTasks <= 0.0)
        {
            return;
        }

        double scale = score * validTasks / passed;
        if (scale <= 0.0 || scale > MaxCodexModelIqScore)
        {
            return;
        }

        total += scale;
        count++;
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

    private static bool TryGetAverageCodexModelPassed(
        CodexRadarSnapshot snapshot,
        CodexModelBaselineMode mode,
        out double average)
    {
        average = 0.0;
        List<CodexModelHistoryPoint> points = SelectCodexModelBaselinePoints(snapshot, mode);
        double total = 0.0;
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            CodexModelHistoryPoint point = points[i];
            if (point != null && point.Passed > 0.0)
            {
                total += point.Passed;
                count++;
            }
        }

        if (count == 0)
        {
            return false;
        }

        average = total / count;
        return true;
    }

    private static bool TryGetCodexModelEfficiencyBaseline(
        CodexRadarSnapshot snapshot,
        bool tokenEfficiency,
        CodexModelBaselineMode mode,
        out double baselinePassed,
        out double baselineValue)
    {
        baselinePassed = 0.0;
        baselineValue = 0.0;
        List<CodexModelHistoryPoint> points = SelectCodexModelBaselinePoints(snapshot, mode);
        for (int i = 0; i < points.Count; i++)
        {
            CodexModelHistoryPoint point = points[i];
            if (point == null || point.Passed <= 0.0)
            {
                continue;
            }

            double value = tokenEfficiency ? point.TotalTokens : point.SerialSeconds;
            if (value <= 0.0)
            {
                continue;
            }

            baselinePassed += point.Passed;
            baselineValue += value;
        }

        return baselinePassed > 0.0 && baselineValue > 0.0;
    }

    private static List<CodexModelHistoryPoint> SelectCodexModelBaselinePoints(
        CodexRadarSnapshot snapshot,
        CodexModelBaselineMode mode)
    {
        List<CodexModelHistoryPoint> all = GetRecentCodexModelHistory(snapshot);
        if (all.Count == 0)
        {
            return all;
        }

        int requestedCount = 0;
        if (mode == CodexModelBaselineMode.Recent7Average)
        {
            requestedCount = 7;
        }
        else if (mode == CodexModelBaselineMode.Recent30Average)
        {
            requestedCount = 30;
        }

        if (requestedCount <= 0)
        {
            return all;
        }

        if (all.Count < requestedCount)
        {
            return all;
        }

        return all.GetRange(all.Count - requestedCount, requestedCount);
    }

    private static bool TryGetCodexModelIqPassed(CodexRadarSnapshot snapshot, out int passed, out int validTasks)
    {
        passed = 0;
        validTasks = CodexModelIqNominalTasks;
        if (snapshot == null || !snapshot.ModelIqKnown)
        {
            return false;
        }

        double sourceValidTasks = snapshot.ModelIqValidTasks > 0
            ? snapshot.ModelIqValidTasks
            : CodexModelIqNominalTasks;
        validTasks = NormalizeCodexModelIqValidTaskCount(sourceValidTasks);
        if (snapshot.ModelIqPassedKnown)
        {
            passed = NormalizeCodexModelIqPassedCount(snapshot.ModelIqPassed, sourceValidTasks);
            return true;
        }

        passed = EstimateCodexModelIqPassedFromScore(snapshot.ModelIqPassRatePercent, validTasks);
        return true;
    }

    private static Color GetCodexModelIqStatusColor(string status, bool known)
    {
        if (!known)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 235);
        }

        string normalized = NormalizeCodexModelIqStatus(status);
        if (normalized == "green")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235);
        }

        if (normalized == "yellow")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 238);
        }

        if (normalized == "orange")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 238);
    }

    private bool RefreshQuotaInfoIfNeeded()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime nowLocal = DateTime.Now;
        bool codexProcessChanged;
        bool codexRunning = UpdateCodexProcessRunningStatus(nowUtc, out codexProcessChanged);
        bool resetDue = IsQuotaResetDue(this.quotaSnapshot, nowLocal);
        // Active Codex sessions need prompt quota updates; inactive sessions use a much slower
        // schedule unless a reset boundary or process transition requires an immediate read.
        bool refreshDue =
            resetDue ||
            this.lastQuotaRefreshUtc == DateTime.MinValue ||
            (codexProcessChanged && codexRunning);

        if (!refreshDue)
        {
            if (codexRunning)
            {
                refreshDue = (nowUtc - this.lastQuotaRefreshUtc).TotalSeconds >= GetQuotaActiveRefreshSeconds();
            }
            else
            {
                refreshDue = IsInactiveQuotaRefreshDue(nowUtc);
            }
        }

        if (!refreshDue)
        {
            return codexProcessChanged;
        }

        if (!codexRunning)
        {
            MarkInactiveQuotaRefresh(nowUtc);
        }

        if (resetDue)
        {
            ActivateDueQuotaResetProtections(this.quotaSnapshot, nowLocal, nowUtc);
        }

        this.lastQuotaRefreshUtc = nowUtc;
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

    private void ApplyQuotaSnapshot(
        CodexRadarSoftwareMode family,
        CodexQuotaSnapshot nextSnapshot,
        bool quotaKnown,
        bool appRunning,
        DateTime nowLocal,
        DateTime detectedUtc,
        string sourceKind)
    {
        ApplyQuotaSnapshot(family, nextSnapshot, quotaKnown, appRunning, nowLocal, detectedUtc, sourceKind, true);
    }

    private void ApplyQuotaSnapshot(
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
        GetRadarFamilyState(family).Touch();
        if (logDecision)
        {
            LogQuotaRingDecision(family, quotaDecision, displaySnapshot, quotaKnown, appRunning);
        }
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
        quotaState.WeeklyQuotaAtFiveHourWindowStartPercent = ClampPercent(snapshot.WeeklyPercent);
    }

    private QuotaProtectionOptions GetQuotaProtectionOptions()
    {
        return QuotaProtectionOptions.FromSettings(this.currentSettings);
    }

    private QuotaRingDecisionInfo UpdateQuotaReadDeltaTrackingWithSettings(
        QuotaRuntimeState quotaState,
        CodexQuotaSnapshot snapshot,
        bool sourceKnown)
    {
        return UpdateQuotaReadDeltaTracking(quotaState, snapshot, sourceKnown, GetQuotaProtectionOptions());
    }

    private static QuotaRingDecisionInfo UpdateQuotaReadDeltaTracking(QuotaRuntimeState quotaState, CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        return UpdateQuotaReadDeltaTracking(
            quotaState,
            snapshot,
            sourceKnown,
            QuotaProtectionOptions.LegacyRuntimeDefaults());
    }

    private static QuotaRingDecisionInfo UpdateQuotaReadDeltaTracking(
        QuotaRuntimeState quotaState,
        CodexQuotaSnapshot snapshot,
        bool sourceKnown,
        QuotaProtectionOptions protectionOptions)
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
            NextFiveHourBaselinePercent = quotaState.FiveHourConsumptionRingBaselinePercent,
            NextWeeklyBaselinePercent = quotaState.WeeklyQuotaAtFiveHourWindowStartPercent,
            NextTrackedFiveHourResetLocal = quotaState.TrackedFiveHourResetLocal,
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

        decision.Reason = reason;
        decision.NextFiveHourBaselinePercent = quotaState.FiveHourConsumptionRingBaselinePercent;
        decision.NextWeeklyBaselinePercent = quotaState.WeeklyQuotaAtFiveHourWindowStartPercent;
        decision.NextTrackedFiveHourResetLocal = quotaState.TrackedFiveHourResetLocal;
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
                { "source_kind", EmptyFallback(decision.SourceKind, "unknown") },
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

        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
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
                this.claudeRuntimeState.RadarSiteHealth = ServiceHealthState.Offline;
                this.codexRuntimeState.Touch();
                this.claudeRuntimeState.Touch();
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

            if (this.claudeRuntimeState.RadarSiteHealth == ServiceHealthState.Offline)
            {
                this.claudeRuntimeState.RadarSiteHealth = ServiceHealthState.Unknown;
                this.claudeRuntimeState.Touch();
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
        RequestDeepSeekBalanceRefresh("网络变化");
        RequestSelectedQuotaUsageRefresh("网络变化");
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        RequestServiceNetworkRefresh();
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.ClaudeServiceKey, "网络变化");
        StatuspageMonitor.RequestRefresh(StatuspageMonitor.OpenAiServiceKey, "网络变化");
        RequestDeepSeekBalanceRefresh("网络变化");
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

    private void RequestDeepSeekBalanceRefresh()
    {
        RequestDeepSeekBalanceRefresh("强制刷新");
    }

    private void RequestDeepSeekBalanceRefresh(string trigger)
    {
        DeepSeekBalanceMonitor.RequestRefresh(trigger);
    }

    private void RefreshDeepSeekBalanceIfNeeded()
    {
        DeepSeekBalanceMonitor.RefreshIfNeeded(
            "codex_radar",
            "定时间隔",
            RequestCodexRadarRenderFromAnyThread);
    }

    private void RequestCodexRadarRenderFromAnyThread()
    {
        try
        {
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }

            if (this.InvokeRequired)
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        RenderLayeredWindow();
                    }
                });
                return;
            }

            RenderLayeredWindow();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private DeepSeekBalanceSnapshot GetDeepSeekBalanceDisplaySnapshot()
    {
        return DeepSeekBalanceMonitor.GetSnapshot();
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
        SetRadarServiceHealth(GetEffectiveCodexRadarSoftwareMode(), health);
    }

    private void SetRadarServiceHealth(CodexRadarSoftwareMode family, ServiceHealthState health)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
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
            this.claudeRuntimeState.RadarSiteHealth = effective;
            this.codexRuntimeState.Touch();
            this.claudeRuntimeState.Touch();
        }
    }

    private void SetClaudeServiceHealth(ServiceHealthState health)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.claudeServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }
    }

    private void SetOpenAiServiceHealth(ServiceHealthState health)
    {
        if (!ServiceHealthProbeEnabled)
        {
            return;
        }

        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.openAiServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }
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

        ServiceHealthTestMode mode = this.currentSettings.ServiceHealthTestMode;
        if (mode == ServiceHealthTestMode.Off)
        {
            return;
        }

        ServiceHealthState state = ConvertServiceHealthTestMode(mode);
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = mode != ServiceHealthTestMode.Offline;
            this.codexRuntimeState.RadarSiteHealth = state;
            this.claudeRuntimeState.RadarSiteHealth = state;
            this.codexRuntimeState.Touch();
            this.claudeRuntimeState.Touch();
            this.openAiServiceHealth = state;
            this.claudeServiceHealth = state;
        }
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
            this.claudeRuntimeState.RadarSiteHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
            this.codexRuntimeState.Touch();
            this.claudeRuntimeState.Touch();
            this.openAiServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
            this.claudeServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
        }

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

    private TimeSpan GetServiceStatusRefreshInterval()
    {
        return TimeSpan.FromMinutes(15.0);
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

    private TimeSpan GetCodexWebRetryDelay()
    {
        return TimeSpan.FromMinutes(2.0);
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

        WidgetSettings settings = this.currentSettings == null ? null : this.currentSettings.Clone();
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
            if (completed.Exception != null)
            {
                Program.LogException(completed.Exception.GetBaseException());
            }

            StatuspageSnapshot snapshot = completed.Status == TaskStatus.RanToCompletion && completed.Result != null
                ? completed.Result.Snapshot
                : StatuspageMonitor.GetSnapshot(StatuspageMonitor.ClaudeServiceKey);
            ApplyCodexStatuspageSnapshot(StatuspageMonitor.ClaudeServiceKey, snapshot);
            RequestCodexRadarRenderFromAnyThread();
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

        WidgetSettings settings = this.currentSettings == null ? null : this.currentSettings.Clone();
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
            if (completed.Exception != null)
            {
                Program.LogException(completed.Exception.GetBaseException());
            }

            StatuspageSnapshot snapshot = completed.Status == TaskStatus.RanToCompletion && completed.Result != null
                ? completed.Result.Snapshot
                : StatuspageMonitor.GetSnapshot(StatuspageMonitor.OpenAiServiceKey);
            ApplyCodexStatuspageSnapshot(StatuspageMonitor.OpenAiServiceKey, snapshot);
            RequestCodexRadarRenderFromAnyThread();
        });
    }

    private void RefreshCodexRadarStatusIfNeeded()
    {
        if (this.currentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            return;
        }

        if (!IsServiceNetworkAvailable())
        {
            SetAllRadarServiceHealth(ServiceHealthState.Offline);
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        DateTime attemptLocal = DateTime.Now;
        CodexRadarSoftwareMode requestedSoftwareMode = GetEffectiveCodexRadarSoftwareMode();
        RadarFamilyRuntimeState requestedState = GetRadarFamilyState(requestedSoftwareMode);
        string requestedModelKey = GetSelectedRadarModelKeyForSoftwareMode(requestedSoftwareMode);
        bool shouldStart = false;
        bool forceRefresh = ShouldForceServiceHealthRefresh(requestedState.RadarSiteHealth);
        string trigger = "定时间隔";
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

        if (!shouldStart)
        {
            return;
        }

        WidgetSettings requestSettings = this.currentSettings.Clone();
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool publicJsonEnabled = requestSettings.CodexRadarPublicJsonEnabled;
            bool htmlFallbackEnabled = requestSettings.CodexRadarHtmlFallbackEnabled;
            bool rssFallbackEnabled = requestSettings.CodexRadarRssFallbackEnabled;
            bool claudeJsonEnabled = requestSettings.ClaudeRadarJsonEnabled;
            bool claudeHomepageFallbackEnabled = requestSettings.ClaudeRadarHomepageFallbackEnabled;
            bool claudeRatingsEnabled = requestSettings.ClaudeRadarCommunityRatingsEnabled;
            bool claudeLocalQuotaFallbackEnabled = requestSettings.ClaudeRadarLocalQuotaFallbackEnabled;
            CodexRadarSnapshot snapshot;
            bool known = false;
            ServiceHealthState health = ServiceHealthState.Unknown;
            CodexRadarModelCatalogUpdate catalogUpdate = null;
            try
            {
                if (requestedSoftwareMode == CodexRadarSoftwareMode.Claude)
                {
                    known = TryReadClaudeRadarStatusForSharedWindow(
                        requestedModelKey,
                        claudeJsonEnabled,
                        claudeHomepageFallbackEnabled,
                        claudeRatingsEnabled,
                        claudeLocalQuotaFallbackEnabled,
                        trigger,
                        out snapshot,
                        out health,
                        out catalogUpdate);
                }
                else
                {
                    known = TryReadCodexRadarStatus(
                        requestedModelKey,
                        publicJsonEnabled,
                        htmlFallbackEnabled,
                        rssFallbackEnabled,
                        out snapshot,
                        out health,
                        out catalogUpdate);
                }
            }
            catch (Exception ex)
            {
                snapshot = null;
                health = ServiceHealthState.Unreachable;
                Program.LogException(ex);
            }

            stopwatch.Stop();
            CodexRadarSnapshot snapshotToCache = null;
            bool modelStillSelected;
            lock (this.codexRadarStatusLock)
            {
                modelStillSelected = string.Equals(
                    requestedModelKey,
                    GetSelectedRadarModelKeyForSoftwareMode(GetEffectiveCodexRadarSoftwareMode()),
                    StringComparison.OrdinalIgnoreCase) &&
                    requestedSoftwareMode == GetEffectiveCodexRadarSoftwareMode();
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
                    PreserveCodexModelIqRefreshTimeIfContentUnchanged(snapshot, previousSnapshot);
                    PreserveCodexQuotaRadarSnapshot(snapshot, previousSnapshot);
                    PreserveCodexCommunityRatingSnapshot(snapshot, previousSnapshot);
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

            ShowCodexRadarModelCatalogNotifications(catalogUpdate);
            SetRadarServiceHealth(requestedSoftwareMode, health);
            if (requestedSoftwareMode == CodexRadarSoftwareMode.Codex)
            {
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
            }
            else
            {
                Program.LogInfo("Shared Claude Radar applied backend snapshot. Trigger=" + trigger +
                    ", Known=" + known.ToString(CultureInfo.InvariantCulture) +
                    ", Health=" + health.ToString());
            }

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            RenderLayeredWindow();
                        }
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private string GetSelectedRadarModelKeyForSoftwareMode(CodexRadarSoftwareMode softwareMode)
    {
        if (softwareMode == CodexRadarSoftwareMode.Claude)
        {
            return this.currentSettings == null ? string.Empty : (this.currentSettings.ClaudeRadarModelKey ?? string.Empty);
        }

        return this.currentSettings == null
            ? CodexRadarModelCatalog.DefaultModelKey
            : (this.currentSettings.CodexRadarModelKey ?? CodexRadarModelCatalog.DefaultModelKey);
    }

    private void ApplyRadarClockAutoSwitchIfNeeded()
    {
        if (this.currentSettings == null ||
            !this.currentSettings.RadarClockAutoSwitchModelEnabled ||
            this.currentSettings.CodexRadarRandomTestEnabled)
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
        double cycleHours = softwareMode == CodexRadarSoftwareMode.Claude ? 24.0 : 12.0;
        DateTime nowLocal = DateTime.Now;
        DateTime boundary = GetEvenRowDialCycleBoundaryLocal(nowLocal, cycleHours);
        DateTime previousBoundary = boundary.AddHours(-cycleHours);
        DateTime currentDataLocal;
        bool currentKnown = TryGetRadarClockDataTime(snapshot, softwareMode, out currentDataLocal);
        if (currentKnown && currentDataLocal >= previousBoundary)
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
            if (softwareMode == CodexRadarSoftwareMode.Claude)
            {
                settings.ClaudeRadarModelKey = WidgetSettings.NormalizeClaudeRadarModelKey(targetKey);
            }
            else
            {
                settings.CodexRadarModelKey = CodexRadarModelCatalog.NormalizeModelKey(targetKey);
            }

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

        if (softwareMode == CodexRadarSoftwareMode.Claude &&
            snapshot.ModelIqRefreshedAtKnown &&
            snapshot.ModelIqRefreshedAtLocal != DateTime.MinValue)
        {
            dataLocal = snapshot.ModelIqRefreshedAtLocal;
            return true;
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
        if (softwareMode == CodexRadarSoftwareMode.Claude)
        {
            return TryFindClaudeSharedClockAutoSwitchTarget(
                currentKey,
                minimumDataLocal,
                snapshot,
                out targetKey,
                out targetDataLocal);
        }

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

    private static bool TryFindClaudeSharedClockAutoSwitchTarget(
        string currentKey,
        DateTime minimumDataLocal,
        CodexRadarSnapshot snapshot,
        out string targetKey,
        out DateTime targetDataLocal)
    {
        targetKey = string.Empty;
        targetDataLocal = DateTime.MinValue;
        if (snapshot == null || snapshot.ClockModelCandidates == null)
        {
            return false;
        }

        string normalizedCurrent = WidgetSettings.NormalizeClaudeRadarModelKey(currentKey);
        for (int i = 0; i < snapshot.ClockModelCandidates.Count; i++)
        {
            RadarClockModelCandidate candidate = snapshot.ClockModelCandidates[i];
            if (candidate == null || !candidate.LatestKnown || candidate.LatestLocal == DateTime.MinValue)
            {
                continue;
            }

            string key = WidgetSettings.NormalizeClaudeRadarModelKey(candidate.Key);
            if (key.Length == 0 ||
                string.Equals(key, normalizedCurrent, StringComparison.OrdinalIgnoreCase) ||
                candidate.LatestLocal < minimumDataLocal)
            {
                continue;
            }

            if (candidate.LatestLocal > targetDataLocal)
            {
                targetKey = key;
                targetDataLocal = candidate.LatestLocal;
            }
        }

        return targetKey.Length > 0;
    }

    private static bool TryReadClaudeRadarStatusForSharedWindow(
        string selectedModelKey,
        bool jsonEnabled,
        bool homepageFallbackEnabled,
        bool ratingsEnabled,
        bool localQuotaFallbackEnabled,
        string trigger,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        catalogUpdate = null;
        WidgetSettings schedulerSettings = new WidgetSettings();
        schedulerSettings.Normalize();
        schedulerSettings.ClaudeRadarModelKey = WidgetSettings.NormalizeClaudeRadarModelKey(selectedModelKey);
        schedulerSettings.ClaudeRadarJsonEnabled = jsonEnabled;
        schedulerSettings.ClaudeRadarHomepageFallbackEnabled = homepageFallbackEnabled;
        schedulerSettings.ClaudeRadarCommunityRatingsEnabled = ratingsEnabled;
        schedulerSettings.ClaudeRadarLocalQuotaFallbackEnabled = localQuotaFallbackEnabled;

        Task<ClaudeRadarSnapshotSchedulerOutcome> task;
        if (!ClaudeRadarSnapshotScheduler.TryStartOrJoin(
            "codex_radar",
            schedulerSettings,
            trigger,
            out task))
        {
            ClaudeRadarSnapshot lastGood = ClaudeRadarSnapshotScheduler.GetLastGoodSnapshot(schedulerSettings);
            snapshot = ConvertClaudeRadarSnapshotForSharedWindow(lastGood);
            health = ConvertClaudeRadarServiceState(lastGood == null
                ? ClaudeRadarServiceState.Unknown
                : lastGood.DataState);
            return IsSharedClaudeRadarSnapshotUsable(snapshot);
        }

        ClaudeRadarSnapshot claudeSnapshot = null;
        ClaudeRadarServiceState claudeHealth = ClaudeRadarServiceState.Unknown;
        try
        {
            task.Wait();
            ClaudeRadarSnapshotSchedulerOutcome outcome = task.Result;
            if (outcome != null)
            {
                claudeSnapshot = outcome.Snapshot;
                claudeHealth = outcome.Health;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            claudeHealth = ClaudeRadarServiceState.Unreachable;
        }

        snapshot = ConvertClaudeRadarSnapshotForSharedWindow(claudeSnapshot);
        health = ConvertClaudeRadarServiceState(claudeHealth == ClaudeRadarServiceState.Unknown && claudeSnapshot != null
            ? claudeSnapshot.DataState
            : claudeHealth);
        return IsSharedClaudeRadarSnapshotUsable(snapshot);
    }

    private static CodexRadarSnapshot ConvertClaudeRadarSnapshotForSharedWindow(ClaudeRadarSnapshot claudeSnapshot)
    {
        CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
        if (claudeSnapshot == null)
        {
            return snapshot;
        }

        snapshot.CheckedAtLocal = claudeSnapshot.CheckedAtLocal;
        snapshot.CheckedAtKnown = claudeSnapshot.CheckedAtLocal != DateTime.MinValue;

        ClaudeRadarModelMetric metric = claudeSnapshot.SelectedModel ?? ClaudeRadarModelMetric.CreateDefault();
        if (claudeSnapshot.ModelMetrics != null)
        {
            for (int i = 0; i < claudeSnapshot.ModelMetrics.Count; i++)
            {
                ClaudeRadarModelMetric candidateMetric = claudeSnapshot.ModelMetrics[i];
                if (candidateMetric != null &&
                    !string.IsNullOrWhiteSpace(candidateMetric.SourceKey) &&
                    candidateMetric.LatestAtKnown &&
                    candidateMetric.LatestAtUtc != DateTime.MinValue)
                {
                    snapshot.ClockModelCandidates.Add(new RadarClockModelCandidate
                    {
                        Key = candidateMetric.SourceKey,
                        LatestLocal = candidateMetric.LatestAtUtc.ToLocalTime(),
                        LatestKnown = true
                    });
                }
            }
        }

        if (metric.Known)
        {
            snapshot.ModelIqKnown = true;
            snapshot.ModelIqRefreshSucceeded = claudeSnapshot.DataState == ClaudeRadarServiceState.Normal;
            snapshot.ModelIqPassRatePercent = Math.Max(0, Math.Min(MaxCodexModelIqScore, metric.IqScore));
            snapshot.ModelIqPassed = Math.Max(0, metric.Passed);
            snapshot.ModelIqValidTasks = NormalizeCodexModelIqValidTaskCount(metric.ValidTasks <= 0 ? 10 : metric.ValidTasks);
            snapshot.ModelIqPassedKnown = true;
            snapshot.ModelIqTokenEfficiencyPercent = Math.Max(0, metric.TokenEfficiencyPercent);
            snapshot.ModelIqTimeEfficiencyPercent = Math.Max(0, metric.TimeEfficiencyPercent);
            snapshot.ModelIqEfficiencyKnown = true;
            snapshot.ModelIqEfficiencyPassed = Math.Max(0.0, metric.Passed);
            snapshot.ModelIqEfficiencyTotalTokens = Math.Max(0.0, metric.TotalTokens);
            snapshot.ModelIqEfficiencySerialSeconds = Math.Max(0.0, metric.Hours * 3600.0);
            snapshot.ModelIqEfficiencyInputKnown =
                snapshot.ModelIqEfficiencyPassed > 0.0 &&
                snapshot.ModelIqEfficiencyTotalTokens > 0.0 &&
                snapshot.ModelIqEfficiencySerialSeconds > 0.0;
            snapshot.ModelIqNormalLowScore = metric.NormalLow > 0 ? metric.NormalLow : CodexModelIqWebsiteNormalLowScore;
            snapshot.ModelIqNormalHighScore = metric.NormalHigh > 0 ? metric.NormalHigh : CodexModelIqWebsiteNormalHighScore;
            snapshot.ModelIqNormalRangeKnown = true;
            snapshot.ModelIqStatus = ConvertClaudeModelStatusText(metric.StatusText, snapshot.ModelIqPassRatePercent);

            DateTime labelLocal = DateTime.MinValue;
            if (metric.LatestAtKnown)
            {
                labelLocal = metric.LatestAtUtc.ToLocalTime();
            }

            if (labelLocal != DateTime.MinValue)
            {
                snapshot.ModelIqRefreshedAtLocal = labelLocal;
                snapshot.ModelIqRefreshedAtKnown = true;
                snapshot.ModelIqDataDateLocal = labelLocal.Date;
                snapshot.ModelIqDataWindowStartHourLocal = labelLocal.Hour >= 12 ? 12 : 0;
                snapshot.ModelIqDataDateKnown = true;
                snapshot.ModelIqDataWindowKnown = true;
            }

            snapshot.ModelIqDataLabel = EmptyFallback(metric.LatestLabel, claudeSnapshot.SiteUpdatedAtText);
            snapshot.ModelIqDataLabelKnown = !string.IsNullOrWhiteSpace(snapshot.ModelIqDataLabel);
        }

        ClaudeRadarCommunitySnapshot community = claudeSnapshot.Community ?? ClaudeRadarCommunitySnapshot.CreateDefault();
        if (community.Known)
        {
            snapshot.CommunityRatingKnown = true;
            snapshot.CommunityRatingModelId = EmptyFallback(community.RatingKey, claudeSnapshot.SelectedModelKey);
            snapshot.CommunityRatingLabel = EmptyFallback(community.Label, claudeSnapshot.SelectedModelName);
            snapshot.CommunityRatingAverage = community.Average;
            snapshot.CommunityRatingCount = Math.Max(0, community.Count);
            snapshot.CommunityRatingUpdatedAtLocal = community.UpdatedAtUtc == DateTime.MinValue
                ? DateTime.MinValue
                : community.UpdatedAtUtc.ToLocalTime();
        }
        else if (!string.IsNullOrWhiteSpace(claudeSnapshot.SelectedModelName))
        {
            snapshot.CommunityRatingModelId = claudeSnapshot.SelectedModelKey;
            snapshot.CommunityRatingLabel = claudeSnapshot.SelectedModelName;
        }

        CodexQuotaRadarSnapshot quotaRadar = ConvertClaudeQuotaLineForSharedWindow(claudeSnapshot);
        if (quotaRadar.Known)
        {
            snapshot.QuotaRadar = quotaRadar;
        }

        return snapshot;
    }

    private static bool IsSharedClaudeRadarSnapshotUsable(CodexRadarSnapshot snapshot)
    {
        return snapshot != null &&
            (snapshot.ModelIqKnown ||
             IsCodexQuotaRadarKnown(snapshot) ||
             snapshot.CommunityRatingKnown);
    }

    private static CodexQuotaRadarSnapshot ConvertClaudeQuotaLineForSharedWindow(ClaudeRadarSnapshot claudeSnapshot)
    {
        CodexQuotaRadarSnapshot radar = CodexQuotaRadarSnapshot.CreateDefault();
        ClaudeRadarQuotaLineSnapshot line = claudeSnapshot == null ? null : claudeSnapshot.QuotaLine;
        if (line == null || !line.Known || line.CurrentValue <= 0.0)
        {
            return radar;
        }

        double current20x = Math.Max(0.0, line.CurrentValue);
        bool previousKnown = line.PreviousKnown && line.PreviousValue > 0.0;
        double previous20x = previousKnown ? Math.Max(0.0, line.PreviousValue) : current20x;
        bool averageKnown = line.AverageKnown && line.AverageValue > 0.0;
        double average20x = averageKnown ? Math.Max(0.0, line.AverageValue) : current20x;
        double min20x = line.MinValue > 0.0 ? line.MinValue : Math.Min(current20x, previous20x);
        double max20x = line.MaxValue > 0.0 ? line.MaxValue : Math.Max(current20x, previous20x);
        if (max20x < min20x)
        {
            double temp = max20x;
            max20x = min20x;
            min20x = temp;
        }

        radar.Known = true;
        DateTime updatedLocal = GetClaudeQuotaLineUpdatedLocal(claudeSnapshot);
        if (updatedLocal != DateTime.MinValue)
        {
            radar.UpdatedAtLocal = updatedLocal;
            radar.UpdatedAtKnown = true;
        }

        string source = EmptyFallback(line.SourceMode, "Claude");
        ApplyCodexQuotaRadarTierValues(
            radar,
            QuotaRadarTierPlus,
            current20x / 120.0,
            current20x / 20.0,
            previous20x / 20.0,
            average20x / 20.0,
            source,
            previousKnown,
            averageKnown);
        ApplyCodexQuotaRadarTierValues(
            radar,
            QuotaRadarTierPro5x,
            current20x / 24.0,
            current20x / 4.0,
            previous20x / 4.0,
            average20x / 4.0,
            source,
            previousKnown,
            averageKnown);
        ApplyCodexQuotaRadarTierValues(
            radar,
            QuotaRadarTierPro20x,
            current20x / 6.0,
            current20x,
            previous20x,
            average20x,
            source,
            previousKnown,
            averageKnown);

        ApplyCodexQuotaRadarTierTrendRange(radar, QuotaRadarTierPlus, min20x / 20.0, max20x / 20.0);
        ApplyCodexQuotaRadarTierTrendRange(radar, QuotaRadarTierPro5x, min20x / 4.0, max20x / 4.0);
        ApplyCodexQuotaRadarTierTrendRange(radar, QuotaRadarTierPro20x, min20x, max20x);
        if (previousKnown)
        {
            ApplyCodexQuotaRadarTierPriorTrendRange(radar, QuotaRadarTierPlus, previous20x / 20.0, previous20x / 20.0);
            ApplyCodexQuotaRadarTierPriorTrendRange(radar, QuotaRadarTierPro5x, previous20x / 4.0, previous20x / 4.0);
            ApplyCodexQuotaRadarTierPriorTrendRange(radar, QuotaRadarTierPro20x, previous20x, previous20x);
        }

        return radar;
    }

    private static DateTime GetClaudeQuotaLineUpdatedLocal(ClaudeRadarSnapshot claudeSnapshot)
    {
        if (claudeSnapshot == null)
        {
            return DateTime.MinValue;
        }

        ClaudeRadarQuotaSnapshot quota = claudeSnapshot.Quota;
        if (quota != null && quota.UpdatedAtKnown && quota.UpdatedAtUtc != DateTime.MinValue)
        {
            return quota.UpdatedAtUtc.ToLocalTime();
        }

        if (claudeSnapshot.SiteUpdatedAtKnown && claudeSnapshot.SiteUpdatedAtUtc != DateTime.MinValue)
        {
            return claudeSnapshot.SiteUpdatedAtUtc.ToLocalTime();
        }

        return claudeSnapshot.CheckedAtLocal;
    }

    private static string ConvertClaudeModelStatusText(string statusText, int score)
    {
        string text = (statusText ?? string.Empty).Trim();
        if (text.IndexOf("降", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "red";
        }

        if (text.IndexOf("增", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "yellow";
        }

        if (text.IndexOf("常", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "green";
        }

        return InferCodexModelIqStatusFromScore(score);
    }

    private static ServiceHealthState ConvertClaudeRadarServiceState(ClaudeRadarServiceState state)
    {
        switch (state)
        {
            case ClaudeRadarServiceState.Normal:
                return ServiceHealthState.Normal;
            case ClaudeRadarServiceState.Offline:
                return ServiceHealthState.Offline;
            case ClaudeRadarServiceState.Incomplete:
                return ServiceHealthState.Incomplete;
            case ClaudeRadarServiceState.Unavailable:
                return ServiceHealthState.Unavailable;
            case ClaudeRadarServiceState.Unreachable:
                return ServiceHealthState.Unreachable;
            default:
                return ServiceHealthState.Unknown;
        }
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
        string sourceSignature = BuildCodexModelIqContentSignature(source);
        if (targetSignature.Length == 0 ||
            sourceSignature.Length == 0 ||
            !string.Equals(targetSignature, sourceSignature, StringComparison.Ordinal))
        {
            return;
        }

        // RefreshedUtc drives the small clock marker. Reusing it for identical IQ content prevents
        // hourly same-data reads from moving the marker away from the true first-seen time.
        target.ModelIqRefreshedAtLocal = source.ModelIqRefreshedAtLocal;
        target.ModelIqRefreshedAtKnown = true;
    }

    private static string BuildCodexModelIqContentSignature(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqKnown)
        {
            return string.Empty;
        }

        StringBuilder key = new StringBuilder(256);
        key.Append(snapshot.ModelIqDataDateKnown ? snapshot.ModelIqDataDateLocal.Date.Ticks : 0L).Append('|');
        key.Append(snapshot.ModelIqDataWindowKnown ? snapshot.ModelIqDataWindowStartHourLocal : -1).Append('|');
        key.Append(snapshot.ModelIqDataLabelKnown ? (snapshot.ModelIqDataLabel ?? string.Empty).Trim() : string.Empty).Append('|');
        key.Append(snapshot.ModelIqPassedKnown ? snapshot.ModelIqPassed : -1).Append('|');
        key.Append(snapshot.ModelIqValidTasks).Append('|');
        key.Append(snapshot.ModelIqPassRatePercent).Append('|');
        key.Append(snapshot.ModelIqStatus ?? string.Empty).Append('|');
        key.Append(snapshot.ModelIqEfficiencyKnown ? snapshot.ModelIqTokenEfficiencyPercent : -1).Append('|');
        key.Append(snapshot.ModelIqEfficiencyKnown ? snapshot.ModelIqTimeEfficiencyPercent : -1).Append('|');
        key.Append(snapshot.ModelIqEfficiencyInputKnown ? snapshot.ModelIqEfficiencyPassed.ToString("R", CultureInfo.InvariantCulture) : string.Empty).Append('|');
        key.Append(snapshot.ModelIqEfficiencyInputKnown ? snapshot.ModelIqEfficiencyTotalTokens.ToString("R", CultureInfo.InvariantCulture) : string.Empty).Append('|');
        key.Append(snapshot.ModelIqEfficiencyInputKnown ? snapshot.ModelIqEfficiencySerialSeconds.ToString("R", CultureInfo.InvariantCulture) : string.Empty).Append('|');
        key.Append(snapshot.ModelIqNormalRangeKnown ? snapshot.ModelIqNormalLowScore : -1).Append('|');
        key.Append(snapshot.ModelIqNormalRangeKnown ? snapshot.ModelIqNormalHighScore : -1).Append('|');
        key.Append(snapshot.ModelIqDisplayMaxScoreKnown ? snapshot.ModelIqDisplayMaxScore.ToString("R", CultureInfo.InvariantCulture) : string.Empty);
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
        target.ClockModelCandidates = CloneRadarClockModelCandidates(source.ClockModelCandidates);
    }

    private static void CopyCodexModelIqPresentationSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null)
        {
            return;
        }

        if (source.ModelIqDataLabelKnown)
        {
            target.ModelIqDataLabel = source.ModelIqDataLabel;
            target.ModelIqDataLabelKnown = true;
        }

        if (source.ModelIqNormalRangeKnown)
        {
            target.ModelIqNormalLowScore = source.ModelIqNormalLowScore;
            target.ModelIqNormalHighScore = source.ModelIqNormalHighScore;
            target.ModelIqNormalRangeKnown = true;
        }

        if (source.ModelIqDisplayMaxScoreKnown)
        {
            target.ModelIqDisplayMaxScore = source.ModelIqDisplayMaxScore;
            target.ModelIqDisplayMaxScoreKnown = true;
        }
    }

    private static void PreserveCodexQuotaRadarSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null ||
            source == null ||
            IsCodexQuotaRadarKnown(target) ||
            !IsCodexQuotaRadarKnown(source))
        {
            return;
        }

        CopyCodexQuotaRadarSnapshot(target, source);
    }

    private static void CopyCodexQuotaRadarSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || !IsCodexQuotaRadarKnown(source))
        {
            return;
        }

        target.QuotaRadar = source.QuotaRadar.Clone();
    }

    private static void PreserveCodexCommunityRatingSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null ||
            source == null ||
            target.CommunityRatingKnown ||
            !source.CommunityRatingKnown)
        {
            return;
        }

        CopyCodexCommunityRatingSnapshot(target, source);
    }

    private static void CopyCodexCommunityRatingSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || !source.CommunityRatingKnown)
        {
            return;
        }

        target.CommunityRatingKnown = true;
        target.CommunityRatingModelId = source.CommunityRatingModelId;
        target.CommunityRatingLabel = source.CommunityRatingLabel;
        target.CommunityRatingAverage = source.CommunityRatingAverage;
        target.CommunityRatingCount = source.CommunityRatingCount;
        target.CommunityRatingUpdatedAtLocal = source.CommunityRatingUpdatedAtLocal;
    }

    private static bool IsCodexQuotaRadarKnown(CodexRadarSnapshot snapshot)
    {
        return snapshot != null &&
            snapshot.QuotaRadar != null &&
            snapshot.QuotaRadar.Known;
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
        }

        if (source.SpeedWindowClosedAtKnown)
        {
            target.SpeedWindowClosedAtLocal = source.SpeedWindowClosedAtLocal;
            target.SpeedWindowClosedAtKnown = true;
        }

        ExpireCodexRadarSpeedWindowIfClosed(target, DateTime.Now);
    }

    private static ServiceHealthState TryReadClaudeStatus(WidgetSettings settings)
    {
        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, ClaudeStatusUrl, out aiBlockReason))
        {
            return ServiceHealthState.Unavailable;
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        string url = ClaudeStatusUrl + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = ClaudeStatusTimeoutMs;
        request.ReadWriteTimeout = ClaudeStatusTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    return ServiceHealthState.Unavailable;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return ServiceHealthState.Unavailable;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        Dictionary<string, object> root =
                            serializer.DeserializeObject(content) as Dictionary<string, object>;
                        Dictionary<string, object> status = GetQuotaObject(root, "status");
                        string indicator = GetQuotaString(status, "indicator").Trim();
                        if (string.Equals(indicator, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Normal;
                        }

                        if (string.Equals(indicator, "minor", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Degraded;
                        }

                        if (string.Equals(indicator, "major", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(indicator, "critical", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Unavailable;
                        }

                        return ServiceHealthState.Unavailable;
                    }
                }
            }
        }
        catch (WebException ex)
        {
            return ClassifyWebException(ex);
        }
        catch
        {
            return ServiceHealthState.Unreachable;
        }
    }

    private static ServiceHealthState TryReadOpenAiStatus(WidgetSettings settings)
    {
        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, OpenAiStatusUrl, out aiBlockReason))
        {
            return ServiceHealthState.Unavailable;
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        string url = OpenAiStatusUrl + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = OpenAiStatusTimeoutMs;
        request.ReadWriteTimeout = OpenAiStatusTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    return ServiceHealthState.Unavailable;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return ServiceHealthState.Unavailable;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        Dictionary<string, object> root =
                            serializer.DeserializeObject(content) as Dictionary<string, object>;
                        Dictionary<string, object> status = GetQuotaObject(root, "status");
                        string indicator = GetQuotaString(status, "indicator").Trim();
                        if (string.Equals(indicator, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Normal;
                        }

                        if (string.Equals(indicator, "minor", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Degraded;
                        }

                        if (string.Equals(indicator, "major", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(indicator, "critical", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Unavailable;
                        }

                        return ServiceHealthState.Unavailable;
                    }
                }
            }
        }
        catch (WebException ex)
        {
            return ClassifyWebException(ex);
        }
        catch
        {
            return ServiceHealthState.Unreachable;
        }
    }

    private static bool TryReadCodexRadarStatus(
        string modelKey,
        bool publicJsonEnabled,
        bool htmlFallbackEnabled,
        bool rssFallbackEnabled,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        snapshot = null;
        health = ServiceHealthState.Unreachable;
        catalogUpdate = null;
        ServiceHealthState primaryHealth = ServiceHealthState.Unavailable;
        bool parsed = false;

        if (publicJsonEnabled)
        {
            string content;
            if (!TryReadCodexRadarUrlText(
                AddCacheBuster(CodexRadarStatusUrl),
                "application/json,text/plain,*/*",
                out content,
                out primaryHealth))
            {
                health = primaryHealth;
                return false;
            }

            parsed =
                TryParseCodexRadarStatus(
                    content,
                    modelKey,
                    rssFallbackEnabled,
                    out snapshot,
                    out catalogUpdate);
            if (!parsed && htmlFallbackEnabled)
            {
                parsed = TryParseCodexRadarHtmlStatus(content, modelKey, out snapshot);
                if (parsed)
                {
                    catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
                        ExtractCodexRadarHtmlModelCatalog(content));
                }
            }

            if (!parsed)
            {
                health = ServiceHealthState.Unavailable;
                return false;
            }
        }

        if (htmlFallbackEnabled &&
            (!parsed || snapshot == null || !snapshot.ModelIqKnown || !IsCodexQuotaRadarKnown(snapshot)))
        {
            CodexRadarSnapshot htmlSnapshot;
            CodexRadarModelCatalogUpdate htmlCatalogUpdate;
            ServiceHealthState htmlHealth;
            if (TryReadCodexRadarHomeHtmlStatus(
                modelKey,
                out htmlSnapshot,
                out htmlHealth,
                out htmlCatalogUpdate))
            {
                if (snapshot == null)
                {
                    snapshot = htmlSnapshot;
                }
                else
                {
                    CopyCodexModelIqSnapshot(snapshot, htmlSnapshot);
                    CopyCodexRadarWindowSnapshot(snapshot, htmlSnapshot);
                    CopyCodexQuotaRadarSnapshot(snapshot, htmlSnapshot);
                }

                catalogUpdate = MergeCodexRadarModelCatalogUpdates(catalogUpdate, htmlCatalogUpdate);
                parsed = true;
            }
            else if (!parsed)
            {
                health = htmlHealth;
                return false;
            }
        }
        else if (htmlFallbackEnabled &&
            parsed &&
            snapshot != null &&
            (!snapshot.ModelIqNormalRangeKnown || !snapshot.ModelIqDataLabelKnown))
        {
            CodexRadarSnapshot htmlSnapshot;
            CodexRadarModelCatalogUpdate htmlCatalogUpdate;
            ServiceHealthState htmlHealth;
            if (TryReadCodexRadarHomeHtmlStatus(
                modelKey,
                out htmlSnapshot,
                out htmlHealth,
                out htmlCatalogUpdate))
            {
                // The homepage exposes presentation-only fields that current.json does not:
                // the chart's "normal band" label and compact date labels such as 7.2_pm_2.
                // Merge only those fields so a decorative HTML parsing failure or stale page
                // cannot overwrite fresher structured JSON values.
                CopyCodexModelIqPresentationSnapshot(snapshot, htmlSnapshot);
                catalogUpdate = MergeCodexRadarModelCatalogUpdates(catalogUpdate, htmlCatalogUpdate);
            }
        }

        if (!parsed)
        {
            health = ServiceHealthState.Unavailable;
            return false;
        }

        TryApplyCodexCommunityRatings(snapshot);
        health = GetCodexRadarSnapshotHealth(snapshot);
        return true;
    }

    private static bool TryApplyCodexCommunityRatings(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        string content;
        ServiceHealthState health;
        if (!TryReadCodexRadarUrlText(
            AddCacheBuster(CodexRadarModelRatingsUrl),
            "application/json,text/plain,*/*",
            out content,
            out health))
        {
            return false;
        }

        return TryParseCodexCommunityRatings(content, snapshot);
    }

    private static bool TryParseCodexCommunityRatings(string content, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (root == null)
            {
                return false;
            }

            List<Dictionary<string, object>> models = GetQuotaObjectsFromArray(root, "models");
            string bestId = string.Empty;
            string bestLabel = string.Empty;
            double bestAverage = double.MinValue;
            int bestCount = 0;
            for (int i = 0; i < models.Count; i++)
            {
                Dictionary<string, object> model = models[i];
                double average;
                if (!TryGetQuotaNumber(model, "average", out average))
                {
                    continue;
                }

                double countDouble;
                int count = TryGetQuotaNumber(model, "count", out countDouble)
                    ? Math.Max(0, (int)Math.Round(countDouble, MidpointRounding.AwayFromZero))
                    : 0;
                if (count <= 0)
                {
                    continue;
                }

                if (average > bestAverage + 0.0001 ||
                    (Math.Abs(average - bestAverage) <= 0.0001 && count > bestCount))
                {
                    bestAverage = average;
                    bestCount = count;
                    bestId = GetQuotaString(model, "id");
                    bestLabel = GetQuotaString(model, "label");
                }
            }

            if (string.IsNullOrEmpty(bestId) && string.IsNullOrEmpty(bestLabel))
            {
                return false;
            }

            DateTime updatedAt;
            if (!TryGetQuotaDate(root, "updated_at", out updatedAt))
            {
                updatedAt = DateTime.Now;
            }

            ApplyCodexCommunityRatingSnapshot(
                snapshot,
                bestId,
                bestLabel,
                bestAverage,
                bestCount,
                updatedAt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyCodexCommunityRatingSnapshot(
        CodexRadarSnapshot snapshot,
        string modelId,
        string label,
        double average,
        int count,
        DateTime updatedAtLocal)
    {
        if (snapshot == null)
        {
            return;
        }

        snapshot.CommunityRatingKnown = true;
        snapshot.CommunityRatingModelId = modelId ?? string.Empty;
        snapshot.CommunityRatingLabel = string.IsNullOrEmpty(label)
            ? FormatCodexCommunityRatingLabel(modelId)
            : label;
        snapshot.CommunityRatingAverage = average;
        snapshot.CommunityRatingCount = Math.Max(0, count);
        snapshot.CommunityRatingUpdatedAtLocal = updatedAtLocal;
    }

    private static bool TryReadCodexRadarHomeHtmlStatus(
        string modelKey,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        snapshot = null;
        catalogUpdate = null;
        string content;
        if (!TryReadCodexRadarUrlText(
            AddCacheBuster(CodexRadarHomeUrl),
            "text/html,application/xhtml+xml,*/*",
            out content,
            out health))
        {
            return false;
        }

        if (!TryParseCodexRadarHtmlStatus(content, modelKey, out snapshot))
        {
            CodexQuotaRadarSnapshot quotaRadar;
            if (!TryParseCodexRadarHtmlQuotaRadar(content, out quotaRadar))
            {
                health = ServiceHealthState.Unavailable;
                return false;
            }

            snapshot = CodexRadarSnapshot.CreateDefault();
            snapshot.CheckedAtLocal = DateTime.Now;
            snapshot.CheckedAtKnown = true;
            snapshot.QuotaRadar = quotaRadar;
            ApplyCodexRadarHtmlWindowStatus(content, snapshot);
        }

        catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
            ExtractCodexRadarHtmlModelCatalog(content));
        health = GetCodexRadarSnapshotHealth(snapshot);
        return true;
    }

    private static bool TryReadCodexRadarUrlText(
        string url,
        string accept,
        out string content,
        out ServiceHealthState health)
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
            request.AllowAutoRedirect = true;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";

            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    health = ServiceHealthState.Unavailable;
                    return false;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        health = ServiceHealthState.Unavailable;
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        content = reader.ReadToEnd();
                    }
                }
            }

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
        bool rssFallbackEnabled)
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
            "application/json,text/plain,*/*");
        builder.AppendLine(FormatCodexRadarCurrentProbe(current, ref fullApiUrl));
        builder.AppendLine();

        CodexRadarProbeResponse fullApi = ReadCodexRadarProbeEndpoint(
            AddCacheBuster(string.IsNullOrWhiteSpace(fullApiUrl) ? CodexRadarFullApiUrl : fullApiUrl),
            "application/json,text/plain,*/*");
        builder.AppendLine(FormatCodexRadarFullApiProbe(fullApi));
        builder.AppendLine();

        CodexRadarProbeResponse home = ReadCodexRadarProbeEndpoint(
            AddCacheBuster(CodexRadarHomeUrl),
            "text/html,application/xhtml+xml,*/*");
        builder.AppendLine(FormatCodexRadarHomeProbe(home, modelKey));
        builder.AppendLine();

        CodexRadarProbeResponse rss = ReadCodexRadarProbeEndpoint(
            AddCacheBuster(NormalizeCodexRadarFeedUrl(string.Empty)),
            "application/rss+xml,application/xml,text/xml,*/*");
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
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.DeserializeObject(response.Content ?? string.Empty) as Dictionary<string, object>;
            Dictionary<string, object> modelIq = GetQuotaObject(root, "model_iq");
            Dictionary<string, object> links = GetQuotaObject(root, "links");
            Dictionary<string, object> apiAccess = GetQuotaObject(root, "api_access");
            string discoveredFullApi = GetQuotaString(links, "full_api");
            if (!string.IsNullOrWhiteSpace(discoveredFullApi))
            {
                fullApiUrl = discoveredFullApi.Trim();
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
        string accept)
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
            request.AllowAutoRedirect = true;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                result.TransportSucceeded = true;
                result.StatusCode = (int)response.StatusCode;
                result.ContentType = response.ContentType ?? string.Empty;
                result.Content = ReadResponseText(response);
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    result.TransportSucceeded = true;
                    result.StatusCode = (int)response.StatusCode;
                    result.ContentType = response.ContentType ?? string.Empty;
                    result.Content = ReadResponseText(response);
                    result.Error = ex.Status.ToString();
                }
            }
            else
            {
                result.TransportSucceeded = false;
                result.Error = ex.Status.ToString();
            }
        }
        catch (Exception ex)
        {
            result.TransportSucceeded = false;
            result.Error = ex.GetType().Name;
        }

        return result;
    }

    private static string ReadResponseText(WebResponse response)
    {
        if (response == null)
        {
            return string.Empty;
        }

        using (Stream stream = response.GetResponseStream())
        {
            if (stream == null)
            {
                return string.Empty;
            }

            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
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
        CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return;
        }

        Dictionary<string, object> links = GetQuotaObject(root, "links");
        string rssUrl = GetQuotaString(links, "rss");
        CodexRadarResetEvent resetEvent;
        if (!TryReadCodexRadarFeedReset(rssUrl, out resetEvent))
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
        out CodexRadarResetEvent resetEvent)
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
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    return false;
                }

                if (stream == null)
                {
                    return false;
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return TryParseCodexRadarFeedReset(reader.ReadToEnd(), out resetEvent);
                }
            }
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
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        snapshot = null;
        catalogUpdate = null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (root == null)
            {
                return false;
            }

            Dictionary<string, object> rootModelIq = GetQuotaObject(root, "model_iq");
            catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
                ExtractCodexRadarModelCatalog(rootModelIq));
            snapshot = CodexRadarSnapshot.CreateDefault();
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
                ApplyCodexRadarFeedResetStatus(root, snapshot);
            }

            Dictionary<string, object> modelIq = SelectCodexModelIqRoot(
                rootModelIq,
                modelKey);
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
            if (TryApplyCodexModelIqStatus(modelIq, snapshot))
            {
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
            ApplyCodexRadarHtmlQuotaRadar(content, snapshot);

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

    private static void ApplyCodexRadarHtmlQuotaRadar(string content, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        CodexQuotaRadarSnapshot quotaRadar;
        if (TryParseCodexRadarHtmlQuotaRadar(content, out quotaRadar))
        {
            snapshot.QuotaRadar = quotaRadar;
        }
    }

    private static bool TryParseCodexRadarHtmlQuotaRadar(
        string content,
        out CodexQuotaRadarSnapshot quotaRadar)
    {
        quotaRadar = CodexQuotaRadarSnapshot.CreateDefault();
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        Match sectionMatch = Regex.Match(
            content,
            "<section\\s+class=\"[^\"]*quota-radar[^\"]*\"[^>]*>(.*?)</section>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!sectionMatch.Success)
        {
            return false;
        }

        string section = sectionMatch.Groups[1].Value;
        ApplyCodexRadarHtmlQuotaRadarUpdateTime(section, quotaRadar);

        MatchCollection rows = Regex.Matches(
            section,
            "<div\\s+class=\"[^\"]*quota-radar-row[^\"]*\"[^>]*>\\s*" +
                "<strong[^>]*>(.*?)</strong>\\s*" +
                "<span[^>]*>(.*?)</span>\\s*" +
                "<span[^>]*>(.*?)</span>\\s*" +
                "<em[^>]*>(.*?)</em>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        bool anyTier = false;
        for (int i = 0; i < rows.Count; i++)
        {
            string label = NormalizeCodexRadarHtmlText(rows[i].Groups[1].Value);
            string key = NormalizeCodexQuotaRadarTierKey(label);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            double fiveHour;
            double sevenDay;
            if (!TryParseCodexRadarUsd(rows[i].Groups[2].Value, out fiveHour) ||
                !TryParseCodexRadarUsd(rows[i].Groups[3].Value, out sevenDay))
            {
                continue;
            }

            ApplyCodexQuotaRadarTierValues(
                quotaRadar,
                key,
                fiveHour,
                sevenDay,
                sevenDay,
                sevenDay,
                NormalizeCodexRadarHtmlText(rows[i].Groups[4].Value),
                false,
                false);
            anyTier = true;
        }

        if (!anyTier)
        {
            return false;
        }

        double current20x = GetCodexQuotaRadarTierSevenDay(quotaRadar, QuotaRadarTierPro20x);
        List<double> trend20x = ParseCodexQuotaRadarTrendValues(section);
        double previous20x = current20x;
        double average20x = current20x;
        double min20x = current20x;
        double max20x = current20x;
        double priorMin20x = current20x;
        double priorMax20x = current20x;
        bool previousKnown = false;
        bool averageKnown = false;
        bool trendRangeKnown = false;
        bool priorRangeKnown = false;
        if (trend20x.Count > 0)
        {
            average20x = AverageCodexQuotaRadarTrendValues(trend20x);
            if (!TryParseCodexQuotaRadarAxisRange(section, out min20x, out max20x))
            {
                GetCodexQuotaRadarTrendRange(trend20x, out min20x, out max20x);
            }

            averageKnown = true;
            trendRangeKnown = true;
            if (trend20x.Count >= 2)
            {
                previous20x = trend20x[trend20x.Count - 2];
                previousKnown = true;
                GetCodexQuotaRadarPriorTrendRange(trend20x, out priorMin20x, out priorMax20x);
                priorRangeKnown = true;
            }
            else
            {
                previous20x = trend20x[0];
                previousKnown = true;
            }

            if (current20x <= 0.0)
            {
                current20x = trend20x[trend20x.Count - 1];
            }
        }

        ApplyCodexQuotaRadarTrendScale(
            quotaRadar,
            QuotaRadarTierPlus,
            current20x,
            previous20x,
            average20x,
            min20x,
            max20x,
            priorMin20x,
            priorMax20x,
            previousKnown,
            averageKnown,
            trendRangeKnown,
            priorRangeKnown);
        ApplyCodexQuotaRadarTrendScale(
            quotaRadar,
            QuotaRadarTierPro5x,
            current20x,
            previous20x,
            average20x,
            min20x,
            max20x,
            priorMin20x,
            priorMax20x,
            previousKnown,
            averageKnown,
            trendRangeKnown,
            priorRangeKnown);
        ApplyCodexQuotaRadarTrendScale(
            quotaRadar,
            QuotaRadarTierPro20x,
            current20x,
            previous20x,
            average20x,
            min20x,
            max20x,
            priorMin20x,
            priorMax20x,
            previousKnown,
            averageKnown,
            trendRangeKnown,
            priorRangeKnown);

        quotaRadar.Known = true;
        return true;
    }

    private static void ApplyCodexRadarHtmlQuotaRadarUpdateTime(
        string section,
        CodexQuotaRadarSnapshot quotaRadar)
    {
        if (quotaRadar == null || string.IsNullOrEmpty(section))
        {
            return;
        }

        Match match = Regex.Match(
            NormalizeCodexRadarHtmlText(section),
            "(\\d{1,2})月(\\d{1,2})日\\s*(\\d{1,2}):(\\d{2})更新",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return;
        }

        int month;
        int day;
        int hour;
        int minute;
        if (!int.TryParse(match.Groups[1].Value, out month) ||
            !int.TryParse(match.Groups[2].Value, out day) ||
            !int.TryParse(match.Groups[3].Value, out hour) ||
            !int.TryParse(match.Groups[4].Value, out minute))
        {
            return;
        }

        try
        {
            DateTime now = DateTime.Now;
            DateTime updated = new DateTime(now.Year, month, day, hour, minute, 0);
            if (updated > now.AddDays(14))
            {
                updated = updated.AddYears(-1);
            }

            quotaRadar.UpdatedAtLocal = updated;
            quotaRadar.UpdatedAtKnown = true;
        }
        catch
        {
        }
    }

    private static List<double> ParseCodexQuotaRadarTrendValues(string section)
    {
        List<double> values = new List<double>();
        if (string.IsNullOrEmpty(section))
        {
            return values;
        }

        MatchCollection matches = Regex.Matches(
            section,
            "<title>\\s*[^<]*20x\\s*Pro\\s*7d\\s*\\$\\s*([0-9][0-9,]*(?:\\.[0-9]+)?)\\s*</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < matches.Count; i++)
        {
            double value;
            if (TryParseCodexRadarUsd(matches[i].Groups[1].Value, out value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool TryParseCodexQuotaRadarAxisRange(
        string section,
        out double minValue,
        out double maxValue)
    {
        minValue = 0.0;
        maxValue = 0.0;
        if (string.IsNullOrEmpty(section))
        {
            return false;
        }

        MatchCollection matches = Regex.Matches(
            section,
            "<text\\b[^>]*>\\s*\\$\\s*([0-9][0-9,]*(?:\\.[0-9]+)?)\\s*</text>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (matches.Count < 2)
        {
            return false;
        }

        bool any = false;
        for (int i = 0; i < matches.Count; i++)
        {
            double value;
            if (!TryParseCodexRadarUsd(matches[i].Groups[1].Value, out value))
            {
                continue;
            }

            if (!any)
            {
                minValue = value;
                maxValue = value;
                any = true;
            }
            else
            {
                minValue = Math.Min(minValue, value);
                maxValue = Math.Max(maxValue, value);
            }
        }

        return any && maxValue > minValue;
    }

    private static double AverageCodexQuotaRadarTrendValues(List<double> values)
    {
        if (values == null || values.Count == 0)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            sum += values[i];
        }

        return sum / values.Count;
    }

    private static void GetCodexQuotaRadarTrendRange(
        List<double> values,
        out double minValue,
        out double maxValue)
    {
        minValue = 0.0;
        maxValue = 0.0;
        if (values == null || values.Count == 0)
        {
            return;
        }

        minValue = values[0];
        maxValue = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            minValue = Math.Min(minValue, values[i]);
            maxValue = Math.Max(maxValue, values[i]);
        }
    }

    private static void GetCodexQuotaRadarPriorTrendRange(
        List<double> values,
        out double minValue,
        out double maxValue)
    {
        minValue = 0.0;
        maxValue = 0.0;
        if (values == null || values.Count < 2)
        {
            return;
        }

        minValue = values[0];
        maxValue = values[0];
        for (int i = 1; i < values.Count - 1; i++)
        {
            minValue = Math.Min(minValue, values[i]);
            maxValue = Math.Max(maxValue, values[i]);
        }
    }

    private static void ApplyCodexQuotaRadarTrendScale(
        CodexQuotaRadarSnapshot quotaRadar,
        string key,
        double current20x,
        double previous20x,
        double average20x,
        double min20x,
        double max20x,
        double priorMin20x,
        double priorMax20x,
        bool previousKnown,
        bool averageKnown,
        bool trendRangeKnown,
        bool priorRangeKnown)
    {
        CodexQuotaRadarTier tier = FindCodexQuotaRadarTier(quotaRadar, key);
        if (tier == null || !tier.CurrentKnown || current20x <= 0.0)
        {
            return;
        }

        double scale = tier.SevenDayUsd / current20x;
        if (previousKnown)
        {
            tier.PreviousSevenDayUsd = previous20x * scale;
            tier.PreviousKnown = true;
        }

        if (averageKnown)
        {
            tier.AverageSevenDayUsd = average20x * scale;
            tier.AverageKnown = true;
        }

        if (trendRangeKnown)
        {
            ApplyCodexQuotaRadarTierTrendRange(
                quotaRadar,
                key,
                min20x * scale,
                max20x * scale);
        }

        if (priorRangeKnown)
        {
            ApplyCodexQuotaRadarTierPriorTrendRange(
                quotaRadar,
                key,
                priorMin20x * scale,
                priorMax20x * scale);
        }
    }

    private static void ApplyCodexQuotaRadarTierTrendRange(
        CodexQuotaRadarSnapshot quotaRadar,
        string key,
        double minSevenDayUsd,
        double maxSevenDayUsd)
    {
        CodexQuotaRadarTier tier = FindCodexQuotaRadarTier(quotaRadar, key);
        if (tier == null)
        {
            return;
        }

        tier.TrendMinSevenDayUsd = Math.Max(0.0, Math.Min(minSevenDayUsd, maxSevenDayUsd));
        tier.TrendMaxSevenDayUsd = Math.Max(tier.TrendMinSevenDayUsd, Math.Max(minSevenDayUsd, maxSevenDayUsd));
        tier.TrendRangeKnown = true;
    }

    private static void ApplyCodexQuotaRadarTierPriorTrendRange(
        CodexQuotaRadarSnapshot quotaRadar,
        string key,
        double minSevenDayUsd,
        double maxSevenDayUsd)
    {
        CodexQuotaRadarTier tier = FindCodexQuotaRadarTier(quotaRadar, key);
        if (tier == null)
        {
            return;
        }

        tier.PriorTrendMinSevenDayUsd = Math.Max(0.0, Math.Min(minSevenDayUsd, maxSevenDayUsd));
        tier.PriorTrendMaxSevenDayUsd = Math.Max(tier.PriorTrendMinSevenDayUsd, Math.Max(minSevenDayUsd, maxSevenDayUsd));
        tier.PriorTrendRangeKnown = true;
    }

    private static void ApplyCodexQuotaRadarTierValues(
        CodexQuotaRadarSnapshot quotaRadar,
        string key,
        double sevenDayUsd,
        double previousSevenDayUsd,
        double averageSevenDayUsd,
        string source)
    {
        ApplyCodexQuotaRadarTierValues(
            quotaRadar,
            key,
            sevenDayUsd / 6.0,
            sevenDayUsd,
            previousSevenDayUsd,
            averageSevenDayUsd,
            source,
            true,
            true);
    }

    private static void ApplyCodexQuotaRadarTierValues(
        CodexQuotaRadarSnapshot quotaRadar,
        string key,
        double fiveHourUsd,
        double sevenDayUsd,
        double previousSevenDayUsd,
        double averageSevenDayUsd,
        string source,
        bool previousKnown,
        bool averageKnown)
    {
        CodexQuotaRadarTier tier = FindCodexQuotaRadarTier(quotaRadar, key);
        if (tier == null)
        {
            return;
        }

        tier.FiveHourUsd = Math.Max(0.0, fiveHourUsd);
        tier.SevenDayUsd = Math.Max(0.0, sevenDayUsd);
        tier.PreviousSevenDayUsd = Math.Max(0.0, previousSevenDayUsd);
        tier.AverageSevenDayUsd = Math.Max(0.0, averageSevenDayUsd);
        tier.Source = source ?? string.Empty;
        tier.CurrentKnown = true;
        tier.PreviousKnown = previousKnown;
        tier.AverageKnown = averageKnown;
    }

    private static CodexQuotaRadarTier FindCodexQuotaRadarTier(
        CodexQuotaRadarSnapshot quotaRadar,
        string key)
    {
        if (quotaRadar == null || quotaRadar.Tiers == null)
        {
            return null;
        }

        for (int i = 0; i < quotaRadar.Tiers.Length; i++)
        {
            CodexQuotaRadarTier tier = quotaRadar.Tiers[i];
            if (tier != null && string.Equals(tier.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return tier;
            }
        }

        return null;
    }

    private static double GetCodexQuotaRadarTierSevenDay(
        CodexQuotaRadarSnapshot quotaRadar,
        string key)
    {
        CodexQuotaRadarTier tier = FindCodexQuotaRadarTier(quotaRadar, key);
        return tier == null || !tier.CurrentKnown ? 0.0 : tier.SevenDayUsd;
    }

    private static string NormalizeCodexQuotaRadarTierKey(string label)
    {
        string compact = Regex.Replace(label ?? string.Empty, "\\s+", string.Empty).ToLowerInvariant();
        if (compact.IndexOf("plus", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return QuotaRadarTierPlus;
        }

        if (compact.IndexOf("5x", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return QuotaRadarTierPro5x;
        }

        if (compact.IndexOf("20x", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return QuotaRadarTierPro20x;
        }

        return string.Empty;
    }

    private static bool TryParseCodexRadarUsd(string value, out double amount)
    {
        amount = 0.0;
        string text = NormalizeCodexRadarHtmlText(value);
        Match match = Regex.Match(text, "-?\\$?\\s*([0-9][0-9,]*(?:\\.[0-9]+)?)");
        if (!match.Success)
        {
            return false;
        }

        return double.TryParse(
            match.Groups[1].Value.Replace(",", string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out amount);
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
            "<title>\\s*([0-9]{1,2}\\.[0-9]{1,2}(?:_(?:am|pm)(?:_[0-9]+)?|_n)?)\\s+GPT-5\\.([0-9]+)\\s+([a-z0-9_-]+):",
            RegexOptions.IgnoreCase);
        for (int i = 0; i < matches.Count; i++)
        {
            string candidateKey = CodexRadarModelCatalog.BuildModelKey(
                "gpt-5." + matches[i].Groups[2].Value,
                matches[i].Groups[3].Value,
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
            "(?:(\\d{1,2})月(\\d{1,2})日|(\\d{1,2})\\.(\\d{1,2})(?:_(am|pm|n)(?:_[0-9]+)?)?)\\s+GPT-5\\.(\\d+)\\s+([a-z0-9_-]+):\\s*" +
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
                match.Groups[7].Value,
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
        return GetQuotaObject(comparisons, normalizedKey);
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
            DateLocal = NormalizeCodexModelHistoryKey(date.Date.AddHours(dataWindowHour >= 12 ? 12 : 0)),
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

        return value.Date.AddHours(value.Hour >= 12 ? 12 : 0);
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
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
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

    private double GetQuotaProcessCheckSeconds()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 3.0;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 10.0;
        }

        return 5.0;
    }

    private TimeSpan GetQuotaInactiveRefreshInterval()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
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
                WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode)))
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

    private static bool IsCodexProcessRunning()
    {
        Process[] processes = null;
        try
        {
            // Query only the required executable name instead of opening every process on the machine.
            processes = Process.GetProcessesByName("codex");
            return processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            if (processes != null)
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    if (processes[i] != null)
                    {
                        processes[i].Dispose();
                    }
                }
            }
        }

        return false;
    }

    private bool IsInactiveQuotaRefreshDue(DateTime nowUtc)
    {
        return this.nextQuotaInactiveRefreshUtc == DateTime.MinValue ||
            nowUtc >= this.nextQuotaInactiveRefreshUtc;
    }

    private void MarkInactiveQuotaRefresh(DateTime nowUtc)
    {
        this.nextQuotaInactiveRefreshUtc = nowUtc + GetQuotaInactiveRefreshInterval();
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

    private void ShowCodexRadarModelCatalogNotifications(CodexRadarModelCatalogUpdate update)
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
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) + " 本次没有出现在网站模型列表中，暂时保留但不可选。",
                ToolTipIcon.Warning);
        }

        for (int i = 0; i < emitted.Deleted.Count; i++)
        {
            CodexRadarModelInfo model = emitted.Deleted[i];
            ShowCodexNotification(
                "Codex Radar 模型已删除",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) + " 连续多次未出现在网站模型列表中，已从检测列表移除。",
                ToolTipIcon.Warning);
        }
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

            File.WriteAllLines(CodexRadarNotificationStatePath, lines.ToArray(), new UTF8Encoding(false));
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
        // Persisted de-dup mirrors Claude Radar: website JSON and homepage HTML can expose
        // slightly different model catalogs, so a single refresh can contain conflicting
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

        if (isNewOpen)
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

            ShowCodexNotification(
                "Codex 额外重置",
                "检测到新的 Codex 重置记录，余额已恢复至 100。",
                ToolTipIcon.Warning);
            this.codexRuntimeState.Quota.LastRefreshUtc = DateTime.MinValue;
            this.codexRuntimeState.Quota.NextInactiveRefreshUtc = DateTime.MinValue;
            this.codexRuntimeState.Touch();
            RenderLayeredWindow();
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
                string prefix = GetCodexRadarCachePrefix(softwareMode, modelKey);
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
                snapshot.ModelIqDataDateLocal = dataDate.Date;
                snapshot.ModelIqDataDateKnown = true;
                int dataWindowHour;
                if (TryReadCacheInt(values, prefix + "DataWindowHour", out dataWindowHour))
                {
                    snapshot.ModelIqDataWindowStartHourLocal = dataWindowHour >= 12 ? 12 : 0;
                    snapshot.ModelIqDataWindowKnown = true;
                }

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

                string prefix = GetCodexRadarCachePrefix(softwareMode, modelKey);
                values[prefix + "SavedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                values[prefix + "RefreshedUtc"] = snapshot.ModelIqRefreshedAtKnown
                    ? snapshot.ModelIqRefreshedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : string.Empty;
                values[prefix + "DataDate"] = snapshot.ModelIqDataDateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                values[prefix + "DataWindowHour"] = (snapshot.ModelIqDataWindowKnown
                    ? (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
                    : 0).ToString(CultureInfo.InvariantCulture);
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

                File.WriteAllLines(tempPath, lines.ToArray(), new UTF8Encoding(false));
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

    private static string GetCodexRadarCachePrefix(CodexRadarSoftwareMode softwareMode, string modelKey)
    {
        string family = softwareMode == CodexRadarSoftwareMode.Claude ? "Claude." : "Codex.";
        return family + GetLegacyCodexRadarCachePrefix(modelKey);
    }

    private static string GetLegacyCodexRadarCachePrefix(string modelKey)
    {
        string key = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        if (string.Equals(key, "gpt_55_medium", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt55Medium.";
        }

        if (string.Equals(key, "gpt_54_xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt54.";
        }

        if (string.Equals(key, CodexRadarModelCatalog.DefaultModelKey, StringComparison.OrdinalIgnoreCase))
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
        string suffix = key.Hour >= 12 ? "-pm" : "-am";
        return key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + suffix;
    }

    private static bool TryParseCodexModelHistoryDate(string value, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        DateTime parsed;
        int windowHour;
        if (TryReadCodexModelIqDataWindow(value.Trim(), out parsed, out windowHour))
        {
            date = NormalizeCodexModelHistoryKey(parsed.Date.AddHours(windowHour));
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
            foreach (string file in Directory.EnumerateFiles(
                sessionsPath,
                "rollout-*.jsonl",
                SearchOption.AllDirectories))
            {
                DateTime writeUtc = SafeGetLastWriteTimeUtc(file);
                if (writeUtc > newestWriteUtc)
                {
                    newestPath = file;
                    newestWriteUtc = writeUtc;
                }
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

        List<string> rolloutFiles = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name != null && name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                {
                    rolloutFiles.Add(file);
                }
            }
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

        rolloutFiles.Sort(delegate(string left, string right)
        {
            return SafeGetLastWriteTimeUtc(right).CompareTo(SafeGetLastWriteTimeUtc(left));
        });

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

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;

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
    }

    private void OnQuotaSessionFileRenamed(object sender, RenamedEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
    }

    private void OnQuotaSessionWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
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

    private static bool TryBuildQuotaSnapshot(Dictionary<string, object> rateLimits, out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        bool found = false;
        found = ApplyQuotaSlot(rateLimits, "primary", snapshot) || found;
        found = ApplyQuotaSlot(rateLimits, "secondary", snapshot) || found;
        return found;
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
            isFiveHour = windowMinutes <= 300.0;
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
        else if (GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude &&
            TryReadClaudeRadarPublicQuotaSnapshot(out cachedQuotaSnapshot))
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
        snapshot = CodexQuotaSnapshot.CreateDefault();
        string path = GetQuotaIniPath(softwareMode);
        if (!File.Exists(path))
        {
            return false;
        }

        bool found = false;
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
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                    found = true;
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

        if (found)
        {
            MarkQuotaSnapshotSource(snapshot, "cache");
            if (!snapshot.SourceUpdatedKnown)
            {
                snapshot.SourceUpdatedUtc = SafeGetLastWriteTimeUtc(path);
                snapshot.SourceUpdatedKnown = snapshot.SourceUpdatedUtc != DateTime.MinValue;
            }
        }

        return found;
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
            ClaudeRadarReader.TryWriteClaudeCodeQuotaCache(new ClaudeCodeUsageSnapshot
            {
                FiveHourPercent = snapshot.FiveHourPercent,
                WeeklyPercent = snapshot.WeeklyPercent,
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
            File.WriteAllText(tempPath, next, new UTF8Encoding(false));
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
        return Path.Combine(
            Logger.DirectoryPath,
            softwareMode == CodexRadarSoftwareMode.Claude ? "claude-quota.ini" : "quota.ini");
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
            localDate = localDate.Date;
            windowStartHour = 0;
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
        RunEvenRowDialFreshnessSelfTest();
        RunCodexModelIqRefreshMarkerSelfTest();
        RunCodexModelIqDynamicScaleSelfTest();
        RunCodexApiServiceAlertDebounceSelfTest();
        RunRadarFamilyRuntimeIsolationSelfTest();
        RunCodexRadarNotificationStateSelfTest();
        RunClaudeSharedQuotaLineSelfTest();
        RunCodexResetCreditsSelfTest();
        QuotaRingPresentation.RunSelfTest();

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

        CodexProviderUsageResult percentResult = ParseCodexProviderUsageResponse(
            "{\"primary\":{\"used_percent\":1,\"resets_at\":\"2026-07-07T04:00:00+09:00\"}," +
            "\"secondary\":{\"used_percentage\":2,\"resets_at\":\"2026-07-12T16:00:00+09:00\"}}",
            true,
            200);
        if (percentResult == null ||
            !percentResult.Success ||
            percentResult.Snapshot == null ||
            percentResult.Snapshot.FiveHourPercent != 99 ||
            percentResult.Snapshot.WeeklyPercent != 98 ||
            !string.Equals(percentResult.Snapshot.FiveHourUsedFieldName, "used_percent", StringComparison.Ordinal) ||
            Math.Abs(percentResult.Snapshot.FiveHourNormalizedUsedPercent - 1.0) > 0.001)
        {
            throw new InvalidOperationException("Codex provider used_percent parsing treated percent units as fractions.");
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
            "{\"type\":\"public_summary\",\"monitored_at\":\"2026-06-29T23:14:33+08:00\"}",
            CodexRadarModelCatalog.DefaultModelKey,
            false,
            out publicSummarySnapshot,
            out publicSummaryUpdate) ||
            GetCodexRadarSnapshotHealth(publicSummarySnapshot) != ServiceHealthState.Incomplete)
        {
            throw new InvalidOperationException("Public summary without model_iq should parse as incomplete.");
        }

        CodexRadarSnapshot ratingSnapshot = CodexRadarSnapshot.CreateDefault();
        if (!TryParseCodexCommunityRatings(
            "{\"ok\":true,\"updated_at\":\"2026-07-01T03:32:07Z\",\"models\":[" +
            "{\"id\":\"gpt-5.5-xhigh\",\"label\":\"GPT-5.5 xhigh\",\"average\":5.7,\"count\":213}," +
            "{\"id\":\"gpt-5.4-high\",\"label\":\"GPT-5.4 high\",\"average\":6.5,\"count\":51}," +
            "{\"id\":\"gpt-5.5-high\",\"label\":\"GPT-5.5 high\",\"average\":6.4,\"count\":117}]}",
            ratingSnapshot) ||
            !ratingSnapshot.CommunityRatingKnown ||
            !string.Equals(GetCodexCommunityRatingDisplayText(ratingSnapshot), "RC:5.4H", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex Radar community rating parsing failed.");
        }

        if (!string.Equals(FormatCodexCommunityRatingShortLabel(string.Empty, "Opus4.8High"), "Op4.8H", StringComparison.Ordinal) ||
            !string.Equals(FormatCodexCommunityRatingShortLabel(string.Empty, "Fable5max"), "Fa5MAX", StringComparison.Ordinal) ||
            !string.Equals(FormatCodexCommunityRatingShortLabel(string.Empty, "Sonnet 5 ultra"), "So5Ult", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex Radar Claude community label formatting failed.");
        }

        DateTime beijingNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneUtilities.GetBeijingTimeZone());
        string monthText = beijingNow.Month.ToString(CultureInfo.InvariantCulture);
        string dayText = beijingNow.Day.ToString(CultureInfo.InvariantCulture);
        string windowClosesAtText = beijingNow.AddHours(1.0).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+08:00";
        string html =
            "<!-- codex-radar:summary:start -->" +
            "<span class=\"window-source-kicker\">速蹬窗口开启</span>" +
            "<div data-window-clock data-window-closes-at=\"" + windowClosesAtText + "\"></div>" +
            "<div class=\"model-iq-score-chip\" data-model-key=\"gpt_55_xhigh\"><span>GPT-5.5-xhigh</span></div>" +
            "<h2>降智雷达 <span>" + monthText + "月" + dayText + "日13:59更新</span></h2>" +
            "<div class=\"model-iq-compare-row\"><span>通过数</span><strong class=\"model-iq-column-gpt_55_xhigh\">6/10</strong></div>" +
            "<div class=\"model-iq-compare-row\"><span>IQ</span><strong class=\"model-iq-column-gpt_55_xhigh\">90.0</strong></div>" +
            "<div class=\"model-iq-compare-row\"><span>耗时</span><strong class=\"model-iq-column-gpt_55_xhigh\">3.4h</strong></div>" +
            "<div class=\"model-iq-compare-row\"><span>总tokens</span><strong class=\"model-iq-column-gpt_55_xhigh\">42.3M</strong></div>" +
            "<title>" + monthText + "." + dayText + "_pm GPT-5.5 xhigh: IQ指数 90.0, 6/10, 费用 $42.00, 耗时 204分钟, cache命中率 95.2%</title>" +
            "<svg><text class=\"model-iq-band-label\">90-110常态区</text></svg>" +
            "<section class=\"quota-radar\" aria-label=\"额度雷达\">" +
            "<h2>额度雷达 <span>" + monthText + "月" + dayText + "日10:14更新</span></h2>" +
            "<div class=\"quota-radar-row\"><strong>20x Pro</strong><span>$286.15</span><span>$1,716.90</span><em>实测</em></div>" +
            "<div class=\"quota-radar-row\"><strong>5x Pro</strong><span>$71.54</span><span>$429.23</span><em>推测</em></div>" +
            "<div class=\"quota-radar-row\"><strong>Plus</strong><span>$14.31</span><span>$85.85</span><em>推测</em></div>" +
            "<svg><g class=\"quota-radar-trend-grid\">" +
            "<text>$1,506</text><text>$1,736</text><text>$1,967</text>" +
            "</g><title>2026-06-29-pm 20x Pro 7d $1,614.09</title><title>2026-06-30-am 20x Pro 7d $1,716.90</title></svg>" +
            "</section>";
        CodexRadarSnapshot htmlSnapshot;
        CodexQuotaRadarTier htmlQuota20x;
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
            if (!htmlSnapshot.SpeedWindowClosedAtKnown) htmlFailures.Add("SpeedWindowClosedAtKnown=false");
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

            if (!IsCodexQuotaRadarKnown(htmlSnapshot))
            {
                htmlFailures.Add("QuotaRadarKnown=false");
            }
            else
            {
                double plusSevenDay = GetCodexQuotaRadarTierSevenDay(htmlSnapshot.QuotaRadar, QuotaRadarTierPlus);
                if (Math.Abs(plusSevenDay - 85.85) > 0.01)
                {
                    htmlFailures.Add("Plus7d=" + plusSevenDay.ToString(CultureInfo.InvariantCulture));
                }

                htmlQuota20x = FindCodexQuotaRadarTier(htmlSnapshot.QuotaRadar, QuotaRadarTierPro20x);
                if (htmlQuota20x == null)
                {
                    htmlFailures.Add("20xTier=null");
                }
                else
                {
                    if (!htmlQuota20x.PreviousKnown ||
                        Math.Abs(htmlQuota20x.PreviousSevenDayUsd - 1614.09) > 0.01)
                    {
                        htmlFailures.Add("20xPrevious=" + htmlQuota20x.PreviousSevenDayUsd.ToString(CultureInfo.InvariantCulture));
                    }

                    if (!htmlQuota20x.AverageKnown ||
                        Math.Abs(htmlQuota20x.AverageSevenDayUsd - 1665.495) > 0.01)
                    {
                        htmlFailures.Add("20xAverage=" + htmlQuota20x.AverageSevenDayUsd.ToString(CultureInfo.InvariantCulture));
                    }

                    if (!htmlQuota20x.TrendRangeKnown ||
                        Math.Abs(htmlQuota20x.TrendMinSevenDayUsd - 1506.0) > 0.01 ||
                        Math.Abs(htmlQuota20x.TrendMaxSevenDayUsd - 1967.0) > 0.01)
                    {
                        htmlFailures.Add(
                            "20xTrendRange=" +
                            htmlQuota20x.TrendMinSevenDayUsd.ToString(CultureInfo.InvariantCulture) +
                            "/" +
                            htmlQuota20x.TrendMaxSevenDayUsd.ToString(CultureInfo.InvariantCulture));
                    }

                    if (!htmlQuota20x.PriorTrendRangeKnown ||
                        Math.Abs(htmlQuota20x.PriorTrendMinSevenDayUsd - 1614.09) > 0.01 ||
                        Math.Abs(htmlQuota20x.PriorTrendMaxSevenDayUsd - 1614.09) > 0.01)
                    {
                        htmlFailures.Add(
                            "20xPriorRange=" +
                            htmlQuota20x.PriorTrendMinSevenDayUsd.ToString(CultureInfo.InvariantCulture) +
                            "/" +
                            htmlQuota20x.PriorTrendMaxSevenDayUsd.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        if (htmlFailures.Count > 0)
        {
            throw new InvalidOperationException("Codex Radar HTML fallback parsing failed: " + string.Join(", ", htmlFailures.ToArray()));
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
        claudeState.RadarSiteHealth = ServiceHealthState.Normal;
        if (codexState.RadarSiteHealth != ServiceHealthState.Unavailable ||
            claudeState.RadarSiteHealth != ServiceHealthState.Normal)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: Radar health states are not independent.");
        }

        CodexConnectionAlertCandidate[] error = new[]
        {
            new CodexConnectionAlertCandidate
            {
                Key = "rader:unreachable",
                Name = "Radar",
                Reason = "无法连接",
                Color = DesignTokens.Colors.DangerStrong
            }
        };
        DateTime nowUtc = new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc);
        ApplyCodexApiServiceAlertDebounce(codexState.ApiAlertDebounce.States, error, nowUtc, TimeSpan.FromSeconds(10.0), false);
        CodexConnectionAlertCandidate[] codexVisible = ApplyCodexApiServiceAlertDebounce(
            codexState.ApiAlertDebounce.States,
            error,
            nowUtc.AddSeconds(11.0),
            TimeSpan.FromSeconds(10.0),
            false);
        CodexConnectionAlertCandidate[] claudeVisible = ApplyCodexApiServiceAlertDebounce(
            claudeState.ApiAlertDebounce.States,
            error,
            nowUtc.AddSeconds(11.0),
            TimeSpan.FromSeconds(10.0),
            false);
        if (codexVisible.Length != 1 || claudeVisible.Length != 0)
        {
            throw new InvalidOperationException("Radar runtime isolation self-test failed: API debounce state leaked across families.");
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
            "{\"model_iq\":{\"latest\":{\"date\":\"2026-07-07-am\",\"score\":90,\"status\":\"yellow\",\"passed\":6,\"tasks\":10,\"valid_tasks\":10}," +
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

    private static void RunCodexApiServiceAlertDebounceSelfTest()
    {
        Dictionary<string, ServiceAlertDebounceState> states =
            new Dictionary<string, ServiceAlertDebounceState>(StringComparer.OrdinalIgnoreCase);
        DateTime start = new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);
        CodexConnectionAlertCandidate[] raw = new CodexConnectionAlertCandidate[]
        {
            new CodexConnectionAlertCandidate
            {
                Key = "openai:Unreachable",
                Name = "OpenAI",
                Reason = "无法连接",
                Color = DesignTokens.Colors.DangerStrong
            }
        };

        if (ApplyCodexApiServiceAlertDebounce(
                states,
                raw,
                start,
                TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
                false).Length != 0)
        {
            throw new InvalidOperationException("Codex API service alert debounce allowed an immediate transient error.");
        }

        if (ApplyCodexApiServiceAlertDebounce(
                states,
                raw,
                start.AddSeconds(CodexApiServiceAlertDebounceSeconds - 1),
                TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
                false).Length != 0)
        {
            throw new InvalidOperationException("Codex API service alert debounce released an error before the window.");
        }

        CodexConnectionAlertCandidate[] stable = ApplyCodexApiServiceAlertDebounce(
            states,
            raw,
            start.AddSeconds(CodexApiServiceAlertDebounceSeconds),
            TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
            false);
        if (stable.Length != 1 || !string.Equals(stable[0].Key, "openai:Unreachable", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex API service alert debounce did not release the stable error.");
        }

        if (ApplyCodexApiServiceAlertDebounce(
                states,
                new CodexConnectionAlertCandidate[0],
                start.AddSeconds(CodexApiServiceAlertDebounceSeconds + 1),
                TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
                false).Length != 0 ||
            states.Count != 0)
        {
            throw new InvalidOperationException("Codex API service alert debounce did not clear after recovery.");
        }

        CodexConnectionAlertCandidate[] radarRaw = new CodexConnectionAlertCandidate[]
        {
            new CodexConnectionAlertCandidate
            {
                Key = "rader:Unreachable",
                Name = "Radar",
                Reason = "无法连接",
                Color = DesignTokens.Colors.DangerStrong
            }
        };
        DateTime switchStart = start.AddMinutes(1);
        CodexConnectionAlertCandidate[] radarStable = ApplyCodexApiServiceAlertDebounce(
            states,
            radarRaw,
            switchStart.AddSeconds(CodexApiServiceAlertDebounceSeconds),
            TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
            false);
        if (radarStable.Length != 0)
        {
            throw new InvalidOperationException("Codex API service alert debounce skipped the initial radar pending state.");
        }

        radarStable = ApplyCodexApiServiceAlertDebounce(
            states,
            radarRaw,
            switchStart.AddSeconds(CodexApiServiceAlertDebounceSeconds * 2),
            TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
            false);
        if (radarStable.Length != 1 || !string.Equals(radarStable[0].Key, "rader:Unreachable", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex API service alert debounce did not stabilize the radar error.");
        }

        ClearCodexApiServiceAlertDebounceStates(states);
        if (ApplyCodexApiServiceAlertDebounce(
                states,
                radarRaw,
                switchStart.AddSeconds(CodexApiServiceAlertDebounceSeconds * 2 + 1),
                TimeSpan.FromSeconds(CodexApiServiceAlertDebounceSeconds),
                false).Length != 0)
        {
            throw new InvalidOperationException("Codex API service alert debounce reused a pre-switch stable radar error.");
        }
    }

    private static void RunClaudeSharedQuotaLineSelfTest()
    {
        ClaudeRadarSnapshot claude = ClaudeRadarSnapshot.CreateDefault();
        claude.CheckedAtLocal = new DateTime(2026, 7, 8, 10, 59, 0);
        claude.Quota = ClaudeRadarQuotaSnapshot.CreateDefault();
        claude.Quota.UpdatedAtKnown = true;
        claude.Quota.UpdatedAtUtc = new DateTime(2026, 7, 6, 2, 59, 0, DateTimeKind.Utc);
        claude.QuotaLine = new ClaudeRadarQuotaLineSnapshot
        {
            Known = true,
            CurrentValue = 2470.0,
            PreviousKnown = true,
            PreviousValue = 2270.63,
            MinValue = 2270.63,
            MaxValue = 2470.0,
            AverageValue = 2370.315,
            AverageKnown = true,
            Metric = "base_d7",
            SourceMode = "site_chart"
        };

        CodexRadarSnapshot shared = ConvertClaudeRadarSnapshotForSharedWindow(claude);
        if (!IsSharedClaudeRadarSnapshotUsable(shared) || !IsCodexQuotaRadarKnown(shared))
        {
            throw new InvalidOperationException("Claude shared quota line did not produce a usable quota radar snapshot.");
        }

        CodexQuotaRadarTier pro20x = FindCodexQuotaRadarTier(shared.QuotaRadar, QuotaRadarTierPro20x);
        if (pro20x == null ||
            !pro20x.CurrentKnown ||
            !pro20x.PreviousKnown ||
            !pro20x.AverageKnown ||
            !pro20x.TrendRangeKnown ||
            Math.Abs(pro20x.SevenDayUsd - 2470.0) > 0.001 ||
            Math.Abs(pro20x.PreviousSevenDayUsd - 2270.63) > 0.001 ||
            Math.Abs(pro20x.AverageSevenDayUsd - 2370.315) > 0.001 ||
            Math.Abs(pro20x.TrendMinSevenDayUsd - 2270.63) > 0.001 ||
            Math.Abs(pro20x.TrendMaxSevenDayUsd - 2470.0) > 0.001)
        {
            throw new InvalidOperationException("Claude shared quota line mapping lost the 20x trend values.");
        }

        double plusSevenDay = GetCodexQuotaRadarTierSevenDay(shared.QuotaRadar, QuotaRadarTierPlus);
        if (Math.Abs(plusSevenDay - 123.5) > 0.001)
        {
            throw new InvalidOperationException("Claude shared quota line mapping did not scale the Plus tier.");
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

    private static void RunEvenRowDialFreshnessSelfTest()
    {
        DateTime codexNow = new DateTime(2026, 7, 7, 13, 30, 0);
        AssertCodexRadarColor(
            ComputeEvenRowDialStatusColor(true, new DateTime(2026, 7, 7, 12, 0, 0), false, DateTime.MinValue, 12.0, codexNow),
            DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245),
            "Codex 12h current period should be green");
        AssertCodexRadarColor(
            ComputeEvenRowDialStatusColor(true, new DateTime(2026, 7, 7, 0, 0, 0), false, DateTime.MinValue, 12.0, codexNow),
            DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245),
            "Codex 12h previous period should be yellow");
        AssertCodexRadarColor(
            ComputeEvenRowDialStatusColor(true, new DateTime(2026, 7, 6, 12, 0, 0), false, DateTime.MinValue, 12.0, codexNow),
            DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245),
            "Codex 12h missed full period should be red");

        DateTime claudeNow = new DateTime(2026, 7, 7, 13, 30, 0);
        AssertCodexRadarColor(
            ComputeEvenRowDialStatusColor(true, new DateTime(2026, 7, 7, 0, 0, 0), false, DateTime.MinValue, 24.0, claudeNow),
            DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245),
            "Claude 24h current day should be green in shared window");
        AssertCodexRadarColor(
            ComputeEvenRowDialStatusColor(true, new DateTime(2026, 7, 6, 0, 0, 0), false, DateTime.MinValue, 24.0, claudeNow),
            DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245),
            "Claude 24h previous day should be yellow in shared window");
        AssertCodexRadarColor(
            ComputeEvenRowDialStatusColor(true, new DateTime(2026, 7, 5, 0, 0, 0), false, DateTime.MinValue, 24.0, claudeNow),
            DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245),
            "Claude 24h missed full day should be red in shared window");

        float markerAngle;
        DateTime codexBoundary = GetEvenRowDialCycleBoundaryLocal(codexNow, 12.0);
        if (!TryGetEvenRowClockMarkerAngle(new DateTime(2026, 7, 7, 12, 15, 0), codexNow, codexBoundary, 12.0, out markerAngle) ||
            Math.Abs(markerAngle - (-82.5f)) > 0.01f)
        {
            throw new InvalidOperationException("Codex 12h refresh marker angle should advance clockwise from the top boundary.");
        }

        if (!TryGetEvenRowClockMarkerAngle(new DateTime(2026, 7, 7, 11, 0, 0), codexNow, codexBoundary, 12.0, out markerAngle) ||
            Math.Abs(markerAngle - 240.0f) > 0.01f)
        {
            throw new InvalidOperationException("Codex 12h refresh marker should remain visible across the boundary until one full lap.");
        }

        float codexCurrentAngle = -90.0f + (float)((codexNow - codexBoundary).TotalHours / 12.0 * 360.0);
        if (Math.Abs(ComputeEvenRowClockSweep(markerAngle, codexCurrentAngle) - 75.0f) > 0.01f)
        {
            throw new InvalidOperationException("Codex 12h clock arc should connect the previous refresh marker to the current pointer.");
        }

        if (TryGetEvenRowClockMarkerAngle(codexNow.AddHours(-12.0), codexNow, codexBoundary, 12.0, out markerAngle))
        {
            throw new InvalidOperationException("Codex 12h refresh marker should expire after one full lap.");
        }

        DateTime claudeBoundary = GetEvenRowDialCycleBoundaryLocal(claudeNow, 24.0);
        if (!TryGetEvenRowClockMarkerAngle(claudeNow.AddHours(-23.9), claudeNow, claudeBoundary, 24.0, out markerAngle))
        {
            throw new InvalidOperationException("Claude shared 24h refresh marker should remain before one full lap.");
        }

        float claudeCurrentAngle = -90.0f + (float)((claudeNow - claudeBoundary).TotalHours / 24.0 * 360.0);
        if (Math.Abs(ComputeEvenRowClockSweep(markerAngle, claudeCurrentAngle) - 358.5f) > 0.01f)
        {
            throw new InvalidOperationException("Claude shared 24h clock arc should connect the previous refresh marker to the current pointer.");
        }

        if (TryGetEvenRowClockMarkerAngle(claudeNow.AddHours(-24.0), claudeNow, claudeBoundary, 24.0, out markerAngle))
        {
            throw new InvalidOperationException("Claude shared 24h refresh marker should expire at one full lap.");
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

    private static void AssertCodexRadarColor(Color actual, Color expected, string message)
    {
        if (actual.ToArgb() != expected.ToArgb())
        {
            throw new InvalidOperationException(message);
        }
    }

    // Legacy power/thermal UI is retained only as reference; PowerThermalForm owns that workload.
#if false
    private void DrawThermalAlerts(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int visibleSensors = Math.Min(3, total);
        bool hasMore = total > 3;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(6);
        float chipHeight = Math.Max(S(16), bounds.Height - S(2));
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height - chipHeight) / 2.0f);

        using (Font chipFont = DesignTokens.CreateUIFont(Math.Max(8.0f, 9.5f * this.LayerScale), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float moreWidth = 0.0f;
            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString();
                moreWidth = Math.Max(S(30), g.MeasureString(moreText, chipFont).Width + S(18));
                moreWidth = Math.Min(moreWidth, bounds.Width * 0.28f);
                RectangleF moreRect = new RectangleF(bounds.Right - moreWidth, chipTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }

            float sensorAreaRight = hasMore ? bounds.Right - moreWidth - gap : bounds.Right;
            float sensorAreaWidth = Math.Max(S(30), sensorAreaRight - bounds.Left);
            float slotWidth = Math.Max(S(30), (sensorAreaWidth - gap * 2.0f) / 3.0f);
            float x = bounds.Left;
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float desiredWidth = g.MeasureString(text, chipFont).Width + S(alerts[i].CriticalActive ? 32 : 20);
                float width = Math.Min(slotWidth, Math.Max(S(30), desiredWidth));
                RectangleF chipRect = new RectangleF(x, chipTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                x += slotWidth + gap;
            }
        }
    }

    private void DrawThermalChip(Graphics g, RectangleF rect, string text, double celsius, bool criticalActive, Font font)
    {
        float radius = Math.Min(rect.Height / 2.0f, S(11));
        int redAlpha = GetThermalRedAlpha(celsius);
        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush baseBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.ThermalChipSurface, 160)))
        using (SolidBrush redBrush = new SolidBrush(DesignTokens.DangerStrong(redAlpha)))
        using (Pen border = new Pen(DesignTokens.White(45), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(baseBrush, path);
            g.FillPath(redBrush, path);
            g.DrawPath(border, path);
        }

        RectangleF textRect = rect;
        if (criticalActive)
        {
            float iconSize = Math.Max(S(12), Math.Min(rect.Height * 0.70f, S(17)));
            RectangleF iconRect = new RectangleF(rect.Right - iconSize - S(7), rect.Top + (rect.Height - iconSize) / 2.0f, iconSize, iconSize);
            DrawSmallWarningIcon(g, iconRect);
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - iconSize - S(18)), rect.Height);
        }
        else
        {
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - S(16)), rect.Height);
        }

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        {
            DrawCodexRadarFittedText(g, text, font, textBrush, textRect, StringAlignment.Near);
        }
    }

    private void DrawSmallWarningIcon(Graphics g, RectangleF rect)
    {
        int warningAlpha = (this.renderTickCount % 2 == 0) ? 77 : 179;
        float centerX = rect.Left + rect.Width / 2.0f;
        float centerY = rect.Top + rect.Height / 2.0f;
        float size = Math.Min(rect.Width, rect.Height);
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.46f),
            new PointF(centerX - size * 0.48f, centerY + size * 0.42f),
            new PointF(centerX + size * 0.48f, centerY + size * 0.42f)
        };

        using (Pen pen = new Pen(DesignTokens.Warning(warningAlpha), Math.Max(1.0f, 2.0f * this.LayerScale)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPolygon(pen, triangle);
        }

        using (Font markFont = DesignTokens.CreateUIFont(Math.Max(7.0f, size * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush markBrush = new SolidBrush(DesignTokens.Warning(warningAlpha)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("!", markFont, markBrush, rect, format);
        }
    }

    private static int GetThermalRedAlpha(double celsius)
    {
        double progress = (celsius - 70.0) / 30.0;
        if (progress < 0.0)
        {
            progress = 0.0;
        }
        else if (progress > 1.0)
        {
            progress = 1.0;
        }

        double alpha = 0.30 + progress * (0.85 - 0.30);
        return (int)Math.Round(alpha * 255.0);
    }

    private static string FormatThermalSensorName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "TZ";
        }

        return name.Trim();
    }

#endif

    private void DrawCodexRadarFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        DrawCodexRadarFittedText(g, text, baseFont, brush, rect, alignment, 8.0f);
    }

    // minSizeUnits lets short, ASCII-heavy labels (e.g. "13:00"/"07/04" reset dates) shrink further
    // than the default floor before falling back to ellipsis-trimming: those glyphs stay legible much
    // smaller than CJK labels do, and at the real (unscaled) EvenRow window width the ring cells are
    // narrow enough that the default floor was truncating them (e.g. "13:00" -> "13...").
    private void DrawCodexRadarFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment, float minSizeUnits)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > minSizeUnits * this.LayerScale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.8f * this.LayerScale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

#if false
    private PowerReading GetPowerReading()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedPowerReadingUtc).TotalSeconds < 2.0)
        {
            return this.cachedPowerReading;
        }

        this.cachedPowerReading = ReadPowerReading();
        this.cachedPowerReadingUtc = now;
        return this.cachedPowerReading;
    }

    private List<ThermalReading> GetThermalAlerts()
    {
        DateTime now = DateTime.UtcNow;
        if (this.currentSettings.ThermalTestMode != ThermalTestMode.Off)
        {
            List<ThermalReading> simulated = BuildSimulatedThermalReadings(this.currentSettings.ThermalTestMode);
            UpdateThermalCriticalStates(simulated, now, true);
            simulated.Sort(CompareThermalReading);
            return simulated;
        }

        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
            UpdateThermalCriticalStates(this.cachedThermalReadings, now, false);
        }

        List<ThermalReading> alerts = new List<ThermalReading>();
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            if (this.cachedThermalReadings[i].Celsius >= 70.0)
            {
                alerts.Add(this.cachedThermalReadings[i]);
            }
        }

        alerts.Sort(CompareThermalReading);
        return alerts;
    }

    private void UpdateThermalCriticalStates(List<ThermalReading> readings, DateTime now, bool instantCritical)
    {
        if (readings == null)
        {
            return;
        }

        HashSet<string> activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < readings.Count; i++)
        {
            ThermalReading reading = readings[i];
            if (reading == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(reading.Name))
            {
                continue;
            }

            activeNames.Add(reading.Name);
            if (reading.Celsius >= 95.0)
            {
                DateTime since;
                if (!this.thermalCriticalSinceUtc.TryGetValue(reading.Name, out since))
                {
                    since = instantCritical ? now.AddSeconds(-3.0) : now;
                    this.thermalCriticalSinceUtc[reading.Name] = since;
                }

                reading.CriticalActive = (now - since).TotalSeconds >= 3.0;
            }
            else
            {
                this.thermalCriticalSinceUtc.Remove(reading.Name);
                reading.CriticalActive = false;
            }
        }

        List<string> stale = new List<string>();
        foreach (string name in this.thermalCriticalSinceUtc.Keys)
        {
            if (!activeNames.Contains(name))
            {
                stale.Add(name);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            this.thermalCriticalSinceUtc.Remove(stale[i]);
        }
    }

    private static int CompareThermalReading(ThermalReading left, ThermalReading right)
    {
        int value = right.Celsius.CompareTo(left.Celsius);
        if (value != 0)
        {
            return value;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ThermalReading> ReadThermalReadings()
    {
        List<ThermalReading> readings = new List<ThermalReading>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(GetManagementValue(item, "Name"));
                    double celsius = ConvertThermalZoneCelsius(
                        GetManagementValue(item, "Temperature"),
                        GetManagementValue(item, "HighPrecisionTemperature"));
                    if (string.IsNullOrEmpty(name) || celsius <= 0.0)
                    {
                        continue;
                    }

                    readings.Add(new ThermalReading
                    {
                        Name = name.Trim(),
                        Celsius = celsius,
                        CriticalActive = false
                    });
                }
            }
        }
        catch
        {
        }

        return readings;
    }

    private List<ThermalReading> BuildSimulatedThermalReadings(ThermalTestMode mode)
    {
        double celsius = mode == ThermalTestMode.Simulate100 ? 100.0 : 75.0;
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0 || this.cachedThermalReadings.Count == 0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
        }

        List<ThermalReading> readings = new List<ThermalReading>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            string name = this.cachedThermalReadings[i].Name;
            if (string.IsNullOrEmpty(name) || !usedNames.Add(name))
            {
                continue;
            }

            readings.Add(new ThermalReading
            {
                Name = name,
                Celsius = celsius,
                CriticalActive = false
            });
        }

        if (readings.Count > 0)
        {
            return readings;
        }

        for (int i = 0; i < 6; i++)
        {
            readings.Add(new ThermalReading
            {
                Name = @"\_SB.TZ" + i.ToString(),
                Celsius = celsius,
                CriticalActive = false
            });
        }

        return readings;
    }

    private static double ConvertThermalZoneCelsius(object temperature, object highPrecisionTemperature)
    {
        double highPrecision = ToPositiveDouble(highPrecisionTemperature);
        if (highPrecision > 0.0)
        {
            return highPrecision / 10.0 - 273.15;
        }

        double standard = ToPositiveDouble(temperature);
        if (standard > 0.0)
        {
            return standard - 273.15;
        }

        return 0.0;
    }

    private static PowerReading ReadPowerReading()
    {
        PowerReading reading = new PowerReading();
        try
        {
            PowerLineStatus lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                reading.StatusKnown = true;
                reading.IsCharging = lineStatus == PowerLineStatus.Online;
            }
        }
        catch
        {
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    double chargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "ChargeRate"));
                    double dischargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "DischargeRate"));
                    object charging = GetManagementValue(item, "Charging");
                    object discharging = GetManagementValue(item, "Discharging");
                    object powerOnline = GetManagementValue(item, "PowerOnline");

                    if (chargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = true;
                        reading.WattsKnown = true;
                        reading.Watts = chargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (dischargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        reading.WattsKnown = true;
                        reading.Watts = dischargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (charging != null)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = Convert.ToBoolean(charging);
                    }

                    if (discharging != null && Convert.ToBoolean(discharging))
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                    }

                    if (powerOnline != null)
                    {
                        reading.StatusKnown = true;
                        if (!Convert.ToBoolean(powerOnline))
                        {
                            reading.IsCharging = false;
                        }
                    }

                    return reading;
                }
            }
        }
        catch
        {
        }

        return reading;
    }

    private static object GetManagementValue(ManagementBaseObject item, string name)
    {
        try
        {
            PropertyData property = item.Properties[name];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static double ToPositiveDouble(object value)
    {
        if (value == null)
        {
            return 0.0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            return number > 0.0 ? number : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static double ToPositiveMilliwatts(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            if (number <= 0 || number >= 4294967294.0)
            {
                return 0;
            }

            return number;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatWatts(double watts)
    {
        if (watts >= 100.0)
        {
            return watts.ToString("0") + " W";
        }

        return watts.ToString("0.0") + " W";
    }

#endif

    private static string GetOrdinalSuffix(int day)
    {
        int lastTwo = day % 100;
        if (lastTwo >= 11 && lastTwo <= 13)
        {
            return "th";
        }

        switch (day % 10)
        {
            case 1:
                return "st";
            case 2:
                return "nd";
            case 3:
                return "rd";
            default:
                return "th";
        }
    }

    private static bool TryReadClaudeRadarPublicQuotaSnapshot(out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        ClaudeRadarSnapshot claudeSnapshot = ClaudeRadarReader.LoadCache(string.Empty);
        ClaudeRadarQuotaSnapshot quota = claudeSnapshot == null ? null : claudeSnapshot.Quota;
        if (quota == null || !quota.Known)
        {
            return false;
        }

        snapshot.FiveHourPercent = ClampPercent(quota.FiveHourPercent);
        snapshot.WeeklyPercent = ClampPercent(quota.WeeklyPercent);
        DateTime resetLocal;
        if (ClaudeRadarResetTextFormatter.TryParseResetText(quota.FiveHourResetText, true, out resetLocal))
        {
            snapshot.FiveHourResetLocal = resetLocal;
            snapshot.FiveHourResetKnown = true;
        }

        if (ClaudeRadarResetTextFormatter.TryParseResetText(quota.WeeklyResetText, false, out resetLocal))
        {
            snapshot.WeeklyResetLocal = resetLocal;
            snapshot.WeeklyResetKnown = true;
        }

        snapshot.SourceUpdatedUtc = quota.UpdatedAtUtc;
        snapshot.SourceUpdatedKnown = quota.UpdatedAtKnown;
        return true;
    }

    private void StoreRenderSceneBitmap(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey) || this.LayeredRenderBitmap == null)
        {
            return;
        }

        if (this.renderSceneBitmapCache.ContainsKey(cacheKey))
        {
            return;
        }

        while (this.renderSceneBitmapCache.Count >= MaxCodexRadarSceneBitmapCacheEntries &&
            this.renderSceneBitmapCacheOrder.Count > 0)
        {
            string oldKey = this.renderSceneBitmapCacheOrder.Dequeue();
            Bitmap oldBitmap;
            if (this.renderSceneBitmapCache.TryGetValue(oldKey, out oldBitmap))
            {
                this.renderSceneBitmapCache.Remove(oldKey);
                if (oldBitmap != null)
                {
                    oldBitmap.Dispose();
                }
            }
        }

        this.renderSceneBitmapCache[cacheKey] = (Bitmap)this.LayeredRenderBitmap.Clone();
        this.renderSceneBitmapCacheOrder.Enqueue(cacheKey);
    }

    private string BuildCodexRadarRenderSceneCacheKey(bool burnInColorProtectionActive)
    {
        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();
        QuotaDisplayState quotaState = GatherQuotaDisplayState();
        DeepSeekBalanceSnapshot deepSeekSnapshot = GetDeepSeekBalanceDisplaySnapshot();
        CodexResetCreditsSnapshot resetCreditsSnapshot = GetCodexResetCreditsDisplaySnapshot();
        bool radarRequestRunning;
        bool claudeRequestRunning;
        bool openAiRequestRunning;
        bool deepSeekRequestRunning = deepSeekSnapshot != null && deepSeekSnapshot.RequestRunning;
        lock (this.codexRadarStatusLock)
        {
            radarRequestRunning = this.codexRadarStatusRequestRunning || this.codexRadarServiceProbeRunning;
        }

        lock (this.claudeStatusLock)
        {
            claudeRequestRunning = this.claudeStatusRequestRunning;
        }

        lock (this.openAiStatusLock)
        {
            openAiRequestRunning = this.openAiStatusRequestRunning;
        }

        RadarFamilyRuntimeState activeFamilyState = GetActiveRadarFamilyState();
        StringBuilder key = new StringBuilder(512);
        key.Append(this.Width).Append('x').Append(this.Height).Append('|');
        key.Append(this.currentSettings.CodexRadarRenderVariant).Append('|');
        key.Append(GetEffectiveCodexRadarSoftwareMode()).Append('|');
        key.Append(activeFamilyState.Revision).Append('|');
        key.Append(GetBackgroundOpacityAlpha()).Append('|');
        key.Append(GetContentOpacityAlpha()).Append('|');
        key.Append(burnInColorProtectionActive ? 'B' : 'N').Append('|');
        key.Append(this.renderTickCount & 1).Append('|');
        key.Append(GetSelectedRadarModelKeyForSoftwareMode(activeFamilyState.Family)).Append('|');
        key.Append(this.renderSceneSettingsRevision).Append('|');
        key.Append(this.currentSettings.DisplayTimeZoneMode).Append('|');
        key.Append(this.currentSettings.DisplayTimeZoneId ?? string.Empty).Append('|');
        key.Append(this.currentSettings.RadarClockTimeDisplayMode).Append('|');
        key.Append(DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)).Append('|');
        key.Append(this.lastCodexRadarStatusAttemptLocal == DateTime.MinValue ? 0L : this.lastCodexRadarStatusAttemptLocal.Ticks).Append('|');
        key.Append(this.currentSettings.CodexRadarRandomTestEnabled ? 'T' : 'R').Append('|');
        key.Append(this.currentSettings.CodexRadarRandomTestRefreshToken).Append('|');

        AppendRadarSnapshotCacheSignature(key, radarSnapshot);
        AppendQuotaSnapshotCacheSignature(key, quotaState);
        AppendDeepSeekSnapshotCacheSignature(key, deepSeekSnapshot);
        AppendCodexResetCreditsSnapshotCacheSignature(key, resetCreditsSnapshot);

        bool networkAvailable;
        ServiceHealthState radarHealth;
        ServiceHealthState openAiHealth;
        ServiceHealthState claudeHealth;
        lock (this.serviceHealthLock)
        {
            networkAvailable = this.serviceNetworkAvailable;
            radarHealth = this.radarServiceHealth;
            openAiHealth = this.openAiServiceHealth;
            claudeHealth = this.claudeServiceHealth;
        }

        key.Append(networkAvailable ? '1' : '0').Append('|');
        key.Append(radarHealth).Append('|');
        key.Append(openAiHealth).Append('|');
        key.Append(claudeHealth).Append('|');
        key.Append(radarRequestRunning ? '1' : '0').Append('|');
        key.Append(claudeRequestRunning ? '1' : '0').Append('|');
        key.Append(openAiRequestRunning ? '1' : '0').Append('|');
        key.Append(deepSeekRequestRunning ? '1' : '0').Append('|');
        key.Append(this.codexApiServiceAlertSignature ?? string.Empty).Append('|');
        key.Append(this.codexApiServiceAlertIndex).Append('|');
        key.Append(this.codexApiServiceAlertNamePhase ? '1' : '0');
        return key.ToString();
    }

    private static void AppendRadarSnapshotCacheSignature(StringBuilder key, CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            key.Append("radar:null|");
            return;
        }

        key.Append("radar:");
        key.Append(snapshot.CheckedAtKnown ? snapshot.CheckedAtLocal.Ticks : 0).Append(',');
        key.Append(snapshot.ModelIqKnown ? '1' : '0').Append(',');
        key.Append(snapshot.ModelIqPassRatePercent).Append(',');
        key.Append(snapshot.ModelIqTokenEfficiencyPercent).Append(',');
        key.Append(snapshot.ModelIqTimeEfficiencyPercent).Append(',');
        key.Append(snapshot.ModelIqNormalLowScore).Append(',');
        key.Append(snapshot.ModelIqNormalHighScore).Append(',');
        key.Append(snapshot.ModelIqDisplayMaxScoreKnown ? snapshot.ModelIqDisplayMaxScore.ToString("R", CultureInfo.InvariantCulture) : string.Empty).Append(',');
        key.Append(snapshot.ModelIqRefreshedAtKnown ? snapshot.ModelIqRefreshedAtLocal.Ticks : 0).Append(',');
        key.Append(snapshot.ModelIqDataDateKnown ? snapshot.ModelIqDataDateLocal.Ticks : 0).Append(',');
        key.Append(snapshot.ModelIqDataWindowStartHourLocal).Append(',');
        key.Append(snapshot.ModelIqDataLabel ?? string.Empty).Append(',');
        key.Append(snapshot.ModelIqRefreshSucceeded ? '1' : '0').Append(',');
        key.Append(snapshot.SpeedWindowKnown ? '1' : '0').Append(',');
        key.Append(snapshot.SpeedWindowOpen ? '1' : '0').Append(',');
        key.Append(snapshot.SpeedWindowClosedAtKnown ? snapshot.SpeedWindowClosedAtLocal.Ticks : 0).Append(',');
        key.Append(snapshot.ResetEventKnown ? '1' : '0').Append(',');
        key.Append(snapshot.CommunityRatingKnown ? '1' : '0').Append(',');
        key.Append(snapshot.CommunityRatingLabel ?? string.Empty).Append(',');
        key.Append(snapshot.CommunityRatingAverage.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
        AppendQuotaRadarCacheSignature(key, snapshot.QuotaRadar);
        key.Append('|');
    }

    private static void AppendQuotaRadarCacheSignature(StringBuilder key, CodexQuotaRadarSnapshot quotaRadar)
    {
        if (quotaRadar == null || quotaRadar.Tiers == null)
        {
            key.Append("qr:null");
            return;
        }

        key.Append("qr:");
        key.Append(quotaRadar.Known ? '1' : '0').Append(',');
        key.Append(quotaRadar.UpdatedAtKnown ? quotaRadar.UpdatedAtLocal.Ticks : 0);
        for (int i = 0; i < quotaRadar.Tiers.Length; i++)
        {
            CodexQuotaRadarTier tier = quotaRadar.Tiers[i];
            if (tier == null)
            {
                continue;
            }

            key.Append(';').Append(tier.Key ?? string.Empty).Append(':');
            key.Append(tier.CurrentKnown ? '1' : '0').Append(',');
            key.Append(tier.PreviousKnown ? '1' : '0').Append(',');
            key.Append(tier.AverageKnown ? '1' : '0').Append(',');
            key.Append(tier.TrendRangeKnown ? '1' : '0').Append(',');
            key.Append(tier.SevenDayUsd.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            key.Append(tier.PreviousSevenDayUsd.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            key.Append(tier.AverageSevenDayUsd.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            key.Append(tier.TrendMinSevenDayUsd.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            key.Append(tier.TrendMaxSevenDayUsd.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendQuotaSnapshotCacheSignature(StringBuilder key, QuotaDisplayState quotaState)
    {
        if (quotaState == null || quotaState.Snapshot == null)
        {
            key.Append("quota:null|");
            return;
        }

        CodexQuotaSnapshot snapshot = quotaState.Snapshot;
        key.Append("quota:");
        key.Append(snapshot.FiveHourPercent).Append(',');
        key.Append(snapshot.WeeklyPercent).Append(',');
        key.Append(snapshot.FiveHourResetKnown ? snapshot.FiveHourResetLocal.Ticks : 0).Append(',');
        key.Append(snapshot.WeeklyResetKnown ? snapshot.WeeklyResetLocal.Ticks : 0).Append(',');
        key.Append(quotaState.CodexRunning ? '1' : '0').Append(',');
        key.Append(quotaState.AnySupportedAppRunning ? '1' : '0').Append(',');
        key.Append(quotaState.QuotaValueKnown ? '1' : '0').Append(',');
        key.Append(quotaState.FiveHourGold ? '1' : '0').Append(',');
        key.Append(quotaState.WeeklyGold ? '1' : '0').Append(',');
        key.Append(quotaState.FiveHourConsumptionRingPercent).Append(',');
        key.Append(quotaState.WeeklyConsumptionRingPercent).Append(',');
        key.Append(quotaState.WeeklyConsumptionRingBlocked ? '1' : '0').Append(',');
        key.Append(quotaState.ForceDangerRing ? '1' : '0').Append('|');
    }

    private static void AppendDeepSeekSnapshotCacheSignature(StringBuilder key, DeepSeekBalanceSnapshot snapshot)
    {
        key.Append("ds:");
        key.Append(DeepSeekBalanceMonitor.BuildCacheSignature(snapshot));
        key.Append('|');
    }

    private static void AppendCodexResetCreditsSnapshotCacheSignature(StringBuilder key, CodexResetCreditsSnapshot snapshot)
    {
        if (snapshot == null)
        {
            key.Append("rs:null|");
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        DateTime earliestUtc;
        bool earliestKnown = snapshot.TryGetEarliestActiveExpirationUtc(nowUtc, out earliestUtc);
        key.Append("rs:");
        key.Append(snapshot.Known ? '1' : '0').Append(',');
        key.Append(snapshot.RequestRunning ? '1' : '0').Append(',');
        key.Append(snapshot.GetActiveCount(nowUtc)).Append(',');
        key.Append(earliestKnown ? earliestUtc.Ticks : 0L).Append(',');
        key.Append(snapshot.ErrorCode ?? string.Empty).Append('|');
    }

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(
            this.currentSettings,
            IsHoverOpacityTargetActive());
    }

    private void DisposeRenderSceneBitmapCache()
    {
        foreach (Bitmap bitmap in this.renderSceneBitmapCache.Values)
        {
            if (bitmap != null)
            {
                bitmap.Dispose();
            }
        }

        this.renderSceneBitmapCache.Clear();
        this.renderSceneBitmapCacheOrder.Clear();
    }

    private static DateTime TruncateToSecond(DateTime value)
    {
        return new DateTime(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Kind);
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.CodexRadarTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    protected override byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!IsHoverOpacityRuntimeEnabled() || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

}
