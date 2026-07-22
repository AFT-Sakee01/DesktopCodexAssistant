using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal enum WidgetVisibilityMode
{
    AlwaysVisible,
    HideWhenFullscreen,
    HideWhenMaximized,
    HideWhenOverlapped,
    DesktopOnly
}

internal enum ClickThroughMode
{
    Disabled,
    Auto,
    Enabled
}

internal enum ThermalTestMode
{
    Off,
    Simulate75,
    Simulate100
}

// Operation is permanently the RadialDial interaction model. Keep the persisted key and a
// single-member enum so old settings files remain schema-compatible without restoring dead skins.
internal enum OperationRenderVariant { RadialDial }

internal enum CodexQuotaPlanComparison
{
    LessThan,
    GreaterThan
}

internal enum CodexQuotaPlanResumeConditionMode
{
    Both,
    WeeklyOnly,
    FiveHourOnly
}

internal enum CodexRadarTestMode
{
    Off,
    None,
    Open,
    Closed
}

internal enum ServiceHealthTestMode
{
    Off,
    Normal,
    Offline,
    Unavailable,
    Unreachable
}

internal enum CleanIpBadgeTestMode
{
    Off,
    NativeResidential,
    BroadcastBusiness,
    UnannouncedIdc,
    ProxyRisk,
    ErrorHttp403,
    ErrorHttp429,
    ErrorTimeout,
    ErrorDns,
    ErrorConnect
}

internal enum NetworkStatusTestMode
{
    Off,
    Online,
    Offline,
    AdapterMissing,
    NeedsValidation
}

internal enum WidgetPerformanceMode
{
    WindowsPowerMode,
    Smooth,
    Balanced,
    BatterySaver
}

internal enum CodexRadarModelVersion
{
    Gpt55,
    Gpt55Medium,
    Gpt54
}

internal enum CodexRadarSoftwareMode
{
    Auto,
    Codex,
    Claude
}

internal enum DisplayTimeZoneMode
{
    Automatic,
    Manual
}

internal enum RadarClockTimeDisplayMode
{
    Utc,
    CurrentLocal,
    LastAttemptRefresh,
    LastActualRefresh
}

// Codex task board display modes. Table answers "who is waiting on me right now"; Timeline answers
// "what happened over the last N minutes". Both share one window and the same snapshot.
internal enum CodexTaskBoardView
{
    Table,
    Timeline
}

internal enum OperationPrimaryPanelMode
{
    Auto,
    WindowsButton,
    MemoryPie,
    Hidden
}

internal sealed class WidgetSettings
{
    public const int MinMetricTileExpandWidth = 130;
    public const int MaxMetricTileExpandWidth = 900;
    public const int MinMetricTileExpandHeight = 44;
    public const int MaxMetricTileExpandHeight = 240;
    public const int DefaultMetricTileExpandWidth = 522;
    public const int DefaultMetricTileExpandHeight = 120;
    public const int MinSpecBoardWidth = 240;
    public const int MaxSpecBoardWidth = 700;
    public const int MinSpecBoardHeight = 240;
    public const int MaxSpecBoardHeight = 800;
    public const int MinSpecBoardAutoHideSeconds = 0;
    public const int MaxSpecBoardAutoHideSeconds = 600;
    public const int DefaultSpecBoardAutoHideSeconds = 20;
    // Left-edge dock: -1 tab centers mean "auto", resolved at show time to a centered queue so the
    // tabs never overlap out of the box. Codex IQ is the fifth member and sits after GUARD.
    public const int AutoLeftDockTabCenterY = -1;
    // The persisted property names keep the historical Pixels suffix for settings.ini compatibility.
    // Values are distribution percentages: 0 touches and 100 consumes all available edge whitespace.
    public const int MinColumnButtonGapPixels = 0;
    public const int MaxColumnButtonGapPixels = 100;
    public const int MinColumnGroupOffsetY = -1000;
    public const int MaxColumnGroupOffsetY = 1000;
    public const int DefaultLeftDockButtonGapPixels = 10;
    public const int DefaultRightTileButtonGapPixels = 8;
    public const int DefaultColumnGroupOffsetY = 0;
    public static readonly string[] DefaultLeftDockButtonOrder = new string[]
    {
        "Network", "SpecBoard", "CodexTask", "Guard", "CodexIq"
    };
    // Guard board: the fourth dock member. It borrows the Spec board's footprint the same way the
    // docked network panel does, so it has no width/height settings of its own.
    public const int MinGuardBoardAutoHideSeconds = 0;
    public const int MaxGuardBoardAutoHideSeconds = 600;
    public const int DefaultGuardBoardAutoHideSeconds = 30;
    public const int MinCodexIqBoardAutoHideSeconds = 0;
    public const int MaxCodexIqBoardAutoHideSeconds = 600;
    public const int DefaultCodexIqBoardAutoHideSeconds = 30;
    // Display-guard steps mirror the CodexSleepGuard combo box (30 min / 1 / 2 / 5 / 8 hours) and
    // the offline steps mirror its threshold list (1 / 5 / 10 / 30 min). Both are snapped to these
    // ladders rather than clamped to a range, so the board's +/- stepper cannot land off-menu.
    public static readonly int[] GuardDisplayMinuteSteps = { 30, 60, 120, 300, 480 };
    public static readonly int[] GuardOfflineThresholdMinuteSteps = { 1, 5, 10, 30 };
    public const int DefaultGuardDisplayMinutes = 300;
    public const int DefaultGuardOfflineThresholdMinutes = 10;
    public const int MinLeftDockCollapseSeconds = 0;
    public const int MaxLeftDockCollapseSeconds = 30;
    public const int DefaultLeftDockCollapseSeconds = 1;
    // The task board now shares the Spec board's space budget. Below the compact threshold it falls
    // back to the narrow card list instead of squeezing the table into unreadable columns.
    public const int MinCodexTaskBoardWidth = 240;
    public const int MaxCodexTaskBoardWidth = 700;
    public const int DefaultCodexTaskBoardWidth = 648;
    public const int MinCodexTaskBoardHeight = 240;
    public const int MaxCodexTaskBoardHeight = 800;
    public const int DefaultCodexTaskBoardHeight = 400;
    public const int MinCodexTaskBoardTimelineMinutes = 15;
    public const int MaxCodexTaskBoardTimelineMinutes = 180;
    public const int DefaultCodexTaskBoardTimelineMinutes = 45;
    public const int MinSpecBoardAutoPopupSeconds = 1;
    public const int MaxSpecBoardAutoPopupSeconds = 120;
    public const int DefaultSpecBoardAutoPopupSeconds = 5;
    public const string DefaultSpecBoardLedgerPath = @"D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl";
    public const int MinSpecBoardManagerWidth = 560;
    public const int MaxSpecBoardManagerWidth = 1000;
    public const int MinSpecBoardManagerHeight = 400;
    public const int MaxSpecBoardManagerHeight = 900;
    public const int MinCodexTaskMonitorActiveWindowMinutes = 5;
    public const int MaxCodexTaskMonitorActiveWindowMinutes = 60;
    public const int MinCodexTaskMonitorActiveSeconds = 3;
    public const int MaxCodexTaskMonitorActiveSeconds = 60;
    public const int MinCodexTaskMonitorIdleSeconds = 30;
    public const int MaxCodexTaskMonitorIdleSeconds = 600;
    public const int MinCodexTaskMonitorTerminalHoldSeconds = 0;
    public const int MaxCodexTaskMonitorTerminalHoldSeconds = 1800;
    public const int MinCodexTaskMonitorErrorHoldSeconds = 5;
    public const int MaxCodexTaskMonitorErrorHoldSeconds = 300;
    public const int MinCodexTaskMonitorNumberCooldownSeconds = 0;
    public const int MaxCodexTaskMonitorNumberCooldownSeconds = 3600;
    public const int MinConnectionCheckIntervalSeconds = 15;
    public const int MaxConnectionCheckIntervalSeconds = 600;
    public const int DefaultConnectionCheckIntervalSeconds = 60;
    public const int MinGfwProbeIntervalMinutes = 15;
    public const int MaxGfwProbeIntervalMinutes = 240;
    public const int DefaultGfwProbeIntervalMinutes = 30;
    public const int CloudStatusRegionJapan = 1;
    public const int CloudStatusRegionAsiaPacific = 2;
    public const int CloudStatusRegionNorthAmerica = 4;
    public const int CloudStatusRegionEurope = 8;
    public const int CloudStatusRegionMaskAll = CloudStatusRegionJapan | CloudStatusRegionAsiaPacific | CloudStatusRegionNorthAmerica | CloudStatusRegionEurope;
    public const int DefaultCloudStatusRegionMask = CloudStatusRegionJapan;
    public const int MinPowerThermalManualEnergySaverThresholdPercent = 0;
    public const int MaxPowerThermalManualEnergySaverThresholdPercent = 100;
    public const int DefaultPowerThermalManualEnergySaverThresholdPercent = 30;
    public const int MinBackgroundTransparency = 0;
    public const int MaxBackgroundTransparency = 90;
    public const int MinOperationButtonSize = 36;
    public const int MaxOperationButtonSize = 120;
    public const int MinOperationOffset = 0;
    public const int MaxOperationOffset = 4000;
    public const int MinAutoHoverOpacityIdleSeconds = 1;
    public const int MaxAutoHoverOpacityIdleSeconds = 300;
    public const int DefaultAutoHoverOpacityIdleSeconds = 60;
    public const int MinBurnInLevelOneIdleSeconds = 1;
    public const int MaxBurnInLevelOneIdleSeconds = 300;
    public const int DefaultBurnInLevelOneIdleSeconds = 10;
    public const int MinBurnInLevelTwoDelaySeconds = 1;
    public const int MaxBurnInLevelTwoDelaySeconds = 600;
    public const int DefaultBurnInLevelTwoDelaySeconds = 30;
    public const int NeverOperationRadialIdleCollapseSeconds = 0;
    public const int MinOperationRadialIdleCollapseSeconds = 1;
    public const int MaxOperationRadialIdleCollapseSeconds = 60;
    public const int DefaultOperationRadialIdleCollapseSeconds = 10;
    public const int MinSensitiveMouseRangePixels = 10;
    public const int MaxSensitiveMouseRangePixels = 300;
    public const int DefaultSensitiveMouseRangePixels = 100;
    public const int MinReverseHoverOpacityRestoreDelaySeconds = 1;
    public const int MaxReverseHoverOpacityRestoreDelaySeconds = 30;
    public const int DefaultReverseHoverOpacityRestoreDelaySeconds = 5;
    public const double MinHoverOpacityRevealDelaySeconds = 1.0;
    public const double MaxHoverOpacityRevealDelaySeconds = 10.0;
    public const double DefaultHoverOpacityRevealDelaySeconds = 1.0;
    public const double MinHoverOpacityRevealResetSeconds = 0.1;
    public const double MaxHoverOpacityRevealResetSeconds = 5.0;
    public const double DefaultHoverOpacityRevealResetSeconds = 0.5;
    public const int MinResolutionCompatibilityScalePercent = 40;
    public const int MaxResolutionCompatibilityScalePercent = 200;
    public const int DefaultResolutionCompatibilityScalePercent = 100;
    public const int MinWindowTransparencyOverridePercent = -1;
    public const int MaxWindowTransparencyOverridePercent = 90;
    public const int MinNightScheduleMinutes = 0;
    public const int MaxNightScheduleMinutes = 1439;
    public const int MinNightDimLuminancePercent = 10;
    public const int MaxNightDimLuminancePercent = 100;
    public const int DefaultNightScheduleStartMinutes = 1380;
    public const int DefaultNightScheduleEndMinutes = 420;
    public const int DefaultNightDimLuminancePercent = 60;
    public const int MinWindowScaleOverridePercent = -1;
    public const int MaxWindowScaleOverridePercent = 200;
    private const int CurrentSettingsVersion = 90;
    private const int RetiredCanonicalSettingsCount = 98;
    private const int RetiredSettingsAliasCount = 11;
    private static readonly HashSet<string> RetiredSettingsInputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ClaudeRadarEnabled", "ClaudeRadarTransparencyPercent", "ClaudeRadarTransparencyOverridePercent",
        "ClaudeRadarScaleOverridePercent", "ClaudeRadarServiceProbeToken", "ClaudeRadarWidth",
        "ClaudeRadarHeight", "ClaudeRadarLeftX", "ClaudeRadarBottomY", "ClaudeRadarDisplayDeviceName",
        "ClaudeRadarRandomTestEnabled", "ClaudeRadarRandomTestAutoRefresh", "ClaudeRadarRandomTestRefreshToken",
        "ClaudeRadarLayoutWorkAreaLeft", "ClaudeRadarLayoutWorkAreaTop", "ClaudeRadarLayoutWorkAreaWidth",
        "ClaudeRadarLayoutWorkAreaHeight",

        "ConnectionCheckWidth", "ConnectionCheckHeight", "ConnectionCheckLeftX", "ConnectionCheckBottomY",
        "ConnectionCheckTransparencyPercent", "ConnectionCheckBorderTransparencyPercent",
        "ConnectionCheckTransparencyOverridePercent", "ConnectionCheckScaleOverridePercent",
        "ConnectionCheckDisplayDeviceName", "ConnectionCheckLayoutWorkAreaLeft",
        "ConnectionCheckLayoutWorkAreaTop", "ConnectionCheckLayoutWorkAreaWidth",
        "ConnectionCheckLayoutWorkAreaHeight", "ConnectionCheckRenderVariant",

        "Width", "Height", "LeftX", "BottomY", "BackgroundTransparencyPercent",
        "MainWidgetRenderVariant", "MainWidgetPresentationMode", "RadarPresentationMode", "MetricOrder",
        "ShowCpu", "ShowMemory", "ShowDisk", "ShowNetwork", "ShowGpu", "ShowNpu",
        "CpuCoreWarningPercent", "CpuCoreCriticalPercent",

        "CodexRadarEnabled", "CodexRadarWidth", "CodexRadarHeight", "CodexRadarLeftX",
        "CodexRadarBottomY", "CodexRadarTransparencyPercent", "CodexRadarTransparencyOverridePercent",
        "CodexRadarScaleOverridePercent", "CodexRadarDisplayDeviceName", "CodexRadarLayoutWorkAreaLeft",
        "CodexRadarLayoutWorkAreaTop", "CodexRadarLayoutWorkAreaWidth", "CodexRadarLayoutWorkAreaHeight",
        "CodexRadarRenderVariant",

        "PowerThermalWidth", "PowerThermalHeight", "PowerThermalLeftX", "PowerThermalBottomY",
        "PowerThermalTransparencyPercent", "PowerThermalTransparencyOverridePercent",
        "PowerThermalScaleOverridePercent", "PowerThermalDisplayDeviceName",
        "PowerThermalLayoutWorkAreaLeft", "PowerThermalLayoutWorkAreaTop",
        "PowerThermalLayoutWorkAreaWidth", "PowerThermalLayoutWorkAreaHeight",
        "PowerThermalRenderVariant", "PowerThermalAutoSizeEnabled", "PowerThermalAutoDirection",
        "PowerThermalVisibleAlertCount",

        "NetworkMonitorWidth", "NetworkMonitorHeight", "NetworkMonitorLeftX", "NetworkMonitorBottomY",
        "NetworkMonitorTransparencyPercent", "NetworkMonitorDisplayDeviceName",
        "NetworkMonitorLayoutWorkAreaLeft", "NetworkMonitorLayoutWorkAreaTop",
        "NetworkMonitorLayoutWorkAreaWidth", "NetworkMonitorLayoutWorkAreaHeight",
        "NetworkMonitorRenderVariant", "NetworkMonitorLeftDockEnabled",

        "BackgroundTransparency", "ClockWidth", "ClockHeight", "ClockLeftX", "ClockBottomY",
        "ClockTransparencyPercent", "PowerThermalBackgroundTransparencyPercent",
        "PowerThermalAutoSizeDirection", "PowerThermalVisibleAlerts",
        "NetworkMonitorBackgroundTransparencyPercent", "ConnectionCheckBackgroundTransparencyPercent",

        "DeepSeekApiKeyRevision", "AlertDeepSeekBalanceEnabled", "ClaudeRadarJsonEnabled",
        "ClaudeRadarHomepageFallbackEnabled", "ClaudeRadarCommunityRatingsEnabled",
        "ClaudeRadarLocalQuotaFallbackEnabled", "ClaudeRadarModelKey",

        // Version 88 removes the previous hidden-mode colour inversion implementation. Keep the
        // key retired permanently so a future redesign cannot inherit an old True value.
        "BurnInHiddenModeColorProtectionEnabled"
    };
    private const int EffectivePerformanceModeCacheMs = 2000;
    private static readonly object EffectivePerformanceModeSync = new object();
    private static DateTime effectivePerformanceModeCacheUtc = DateTime.MinValue;
    private static WidgetPerformanceMode effectivePerformanceModeCache = WidgetPerformanceMode.Balanced;
    public const int MinCodexModelIqPassed = 0;
    public const int MaxCodexModelIqPassed = 100;
    public const int MinCodexModelIqValidTasks = 1;
    public const int MaxCodexModelIqValidTasks = 100;
    public const int PreviousDefaultCodexModelIqBaselinePassed = 8;
    public const int PreviousTwelveTaskCodexModelIqBaselinePassed = 7;
    public const int PreviousTenTaskLocalCodexModelIqBaselinePassed = 6;
    public const int DefaultCodexModelIqBaselinePassed = 7;
    public const int DefaultCodexModelIqBaselineValidTasks = 10;
    public const int MinCodexModelEfficiencyPercent = 0;
    public const int MaxCodexModelEfficiencyPercent = 200;
    public const int DefaultCodexModelEfficiencyPercent = 100;
    public const int MinCodexModelEfficiencyBaselineValue = 0;
    public const int MaxCodexModelEfficiencyBaselineValue = 100000000;
    public const int DefaultCodexModelEfficiencyBaselineValue = 0;
    public const int MinCodexModelEfficiencyLowThresholdPercent = 0;
    public const int MaxCodexModelEfficiencyLowThresholdPercent = 200;
    public const int DefaultCodexModelEfficiencyLowThresholdPercent = 80;
    public const int MinCodexQuotaPlanThresholdPercent = 0;
    public const int MaxCodexQuotaPlanThresholdPercent = 100;
    public const int DefaultCodexQuotaPlanWeeklyThresholdPercent = 3;
    public const int DefaultCodexQuotaPlanFiveHourThresholdPercent = 90;
    public const string ModuleMain = "Main";
    public const string ModuleOperation = "Operation";

    public int ApplicationTransparencyPercent { get; set; }
    public int MainWidgetTransparencyOverridePercent { get; set; }
    public int NetworkMonitorTransparencyOverridePercent { get; set; }
    public int OperationTransparencyOverridePercent { get; set; }
    public int SpecBoardTransparencyOverridePercent { get; set; }
    public int CodexTaskBoardTransparencyOverridePercent { get; set; }
    public int GuardBoardTransparencyOverridePercent { get; set; }
    public int CodexIqBoardTransparencyOverridePercent { get; set; }
    public bool NightScheduleEnabled { get; set; }
    public int NightScheduleStartMinutes { get; set; }
    public int NightScheduleEndMinutes { get; set; }
    public int NightDimLuminancePercent { get; set; }
    public bool NightQuietHoursEnabled { get; set; }
    public bool AlertQuotaEnabled { get; set; }
    public bool AlertResetProtectionEnabled { get; set; }
    public bool AlertServiceHealthEnabled { get; set; }
    public bool AlertCodexTaskEnabled { get; set; }
    public string HotkeyToggleAllWindows { get; set; }
    public string HotkeyToggleHoverOpacity { get; set; }
    public string HotkeyOpenSettings { get; set; }
    public int MainWidgetScaleOverridePercent { get; set; }
    public int NetworkMonitorScaleOverridePercent { get; set; }
    public int OperationScaleOverridePercent { get; set; }
    public int SpecBoardScaleOverridePercent { get; set; }
    public int CodexTaskBoardScaleOverridePercent { get; set; }
    public int GuardBoardScaleOverridePercent { get; set; }
    public int CodexIqBoardScaleOverridePercent { get; set; }
    public int PowerThermalManualEnergySaverThresholdPercent { get; set; }
    // true: the power module is drawn as a strip at the bottom of the main widget and the
    // standalone window stays hidden. false: the standalone window is shown as before.
    public bool PowerThermalIntegratedEnabled { get; set; }
    public string NetworkMonitorAdapterId { get; set; }
    public NetworkStatusTestMode NetworkStatusTestMode { get; set; }
    public bool GfwProbeEnabled { get; set; }
    public int GfwProbeIntervalMinutes { get; set; }
    public int GfwProbeManualRefreshToken { get; set; }
    public int CloudEndpointTestSeed { get; set; }
    public int CloudStatusRegionMask { get; set; }
    public string[] CloudEndpointTargets { get; set; }
    public string[] FixedPingTargets { get; set; }
    public int ConnectionCheckIntervalSeconds { get; set; }
    public int ConnectionCheckManualRefreshToken { get; set; }
    public int SpecBoardWidth { get; set; }
    public int SpecBoardHeight { get; set; }
    public int SpecBoardLeftX { get; set; }
    public int SpecBoardBottomY { get; set; }
    public int SpecBoardAutoHideSeconds { get; set; }
    public bool SpecBoardAutoPopupEnabled { get; set; }
    public int SpecBoardAutoPopupSeconds { get; set; }
    public string SpecBoardLedgerPath { get; set; }
    public int SpecBoardManagerWidth { get; set; }
    public int SpecBoardManagerHeight { get; set; }
    public bool SpecBoardManagerDangerZoneRequiresTypedConfirm { get; set; }
    public bool LeftDockAutoArrangeEnabled { get; set; }
    public string[] LeftDockButtonOrder { get; set; }
    public int LeftDockButtonGapPixels { get; set; }
    public int LeftDockGroupOffsetY { get; set; }
    public bool SpecBoardLeftDockEnabled { get; set; }
    public int SpecBoardLeftDockTabCenterY { get; set; }
    public bool CodexTaskBoardLeftDockEnabled { get; set; }
    public int CodexTaskBoardLeftDockTabCenterY { get; set; }
    public int NetworkMonitorLeftDockTabCenterY { get; set; }
    public bool GuardBoardLeftDockEnabled { get; set; }
    public int GuardBoardLeftDockTabCenterY { get; set; }
    public int GuardBoardAutoHideSeconds { get; set; }
    public bool CodexIqBoardLeftDockEnabled { get; set; }
    public int CodexIqBoardLeftDockTabCenterY { get; set; }
    public int CodexIqBoardAutoHideSeconds { get; set; }
    // Guard state. GuardSleepEnabled and the two deadline ticks are live runtime state rather than
    // preferences: they are persisted so a restart during a long unattended run does not silently
    // drop the protection the board promises. Ticks are UTC; 0 means "not armed".
    public bool GuardSleepEnabled { get; set; }
    public long GuardSleepSinceUtcTicks { get; set; }
    public int GuardDisplayMinutes { get; set; }
    public int GuardOfflineThresholdMinutes { get; set; }
    public long GuardDisplayUntilUtcTicks { get; set; }
    public long GuardBatteryCarePauseUntilUtcTicks { get; set; }
    public int LeftDockCollapseSeconds { get; set; }
    public bool LeftDockOutsideClickCollapseEnabled { get; set; }
    public int CodexTaskBoardWidth { get; set; }
    public int CodexTaskBoardHeight { get; set; }
    public CodexTaskBoardView CodexTaskBoardView { get; set; }
    public int CodexTaskBoardTimelineMinutes { get; set; }
    public bool CodexTaskMonitorEnabled { get; set; }
    public int CodexTaskMonitorActiveWindowMinutes { get; set; }
    public int CodexTaskMonitorActiveSeconds { get; set; }
    public int CodexTaskMonitorIdleSeconds { get; set; }
    public int CodexTaskMonitorTerminalHoldSeconds { get; set; }
    public int CodexTaskMonitorErrorHoldSeconds { get; set; }
    public int CodexTaskMonitorNumberCooldownSeconds { get; set; }
    public int OperationButtonSize { get; set; }
    public int OperationLeftOffset { get; set; }
    public int OperationBottomOffset { get; set; }
    public int OperationBackgroundTransparencyPercent { get; set; }
    public OperationPrimaryPanelMode OperationPrimaryPanelMode { get; set; }
    public bool OperationWindowsButtonEnabled { get; set; }
    public bool OperationMemoryPieEnabled { get; set; }
    public bool ForceShowForegroundFpsEnabled { get; set; }
    public bool SeelenDockForegroundPulseEnabled { get; set; }
    public bool WinDRecoveryPulseEnabled { get; set; }
    public bool CodexPetZOrderProtectionEnabled { get; set; }
    public bool PowerResumeRestartEnabled { get; set; }
    public bool FallbackDisconnectedDisplaysEnabled { get; set; }
    public string MainDisplayDeviceName { get; set; }
    public string OperationDisplayDeviceName { get; set; }
    public bool ResolutionCompatibilityModeEnabled { get; set; }
    public int ResolutionCompatibilityScalePercent { get; set; }
    public int LayoutWorkAreaLeft { get; set; }
    public int LayoutWorkAreaTop { get; set; }
    public int LayoutWorkAreaWidth { get; set; }
    public int LayoutWorkAreaHeight { get; set; }
    public int OperationLayoutWorkAreaLeft { get; set; }
    public int OperationLayoutWorkAreaTop { get; set; }
    public int OperationLayoutWorkAreaWidth { get; set; }
    public int OperationLayoutWorkAreaHeight { get; set; }
    public WidgetVisibilityMode VisibilityMode { get; set; }
    public bool VisibilityOverlapIgnoresOperationPanelEnabled { get; set; }
    public ClickThroughMode ClickThroughMode { get; set; }
    public bool StartupEnabled { get; set; }
    public bool AlertTestEnabled { get; set; }
    public ThermalTestMode ThermalTestMode { get; set; }
    public CodexRadarTestMode CodexRadarTestMode { get; set; }
    public ServiceHealthTestMode ServiceHealthTestMode { get; set; }
    public bool CodexRadarRandomTestEnabled { get; set; }
    public bool CodexRadarRandomTestAutoRefresh { get; set; }
    public int CodexRadarRandomTestRefreshToken { get; set; }
    public bool MainWidgetTileLargeModeEnabled { get; set; }
    public int MetricTileExpandWidth { get; set; }
    public int MetricTileExpandHeight { get; set; }

    // Per-tile position for the edge-column presentation. Each of the ten metric/Radar tiles is its own
    // window and can be placed independently in the global layout editor, so each needs its own
    // LeftX/BottomY the way every other module has one.
    //
    // AutoTilePosition means "not placed yet": the tile falls back to its slot in the default
    // right-edge column, computed from the live work area. That keeps a fresh install tidy on any
    // resolution without hard-coding coordinates, and a tile only gains a stored position once the
    // user actually drags it.
    public const int AutoTilePosition = int.MinValue;

    public int[] MetricTileLeftX { get; set; }
    public int[] MetricTileBottomY { get; set; }
    public bool RightTileAutoArrangeEnabled { get; set; }
    public string[] RightTileButtonOrder { get; set; }
    public int RightTileButtonGapPixels { get; set; }
    public int RightTileGroupOffsetY { get; set; }

    // Indices 0-7 are the metric tiles, 8-9 the Radar quota tiles. One shared position array covers
    // both so the layout editor and settings storage do not need two parallel schemes; each group is
    // shown or hidden by its own presentation mode. The Radar IQ tiles were retired in favour of the
    // left-docked Codex IQ board, so only the two quota tiles remain.
    public static readonly string[] MetricTileIds = new string[]
    {
        "Cpu", "Memory", "Disk", "Network", "Gpu", "Npu", "Power", "Guard",
        "CodexQuota", "ClaudeQuota"
    };

    public static readonly string[] DefaultRightTileButtonOrder = new string[]
    {
        "Cpu", "Memory", "Disk", "Network", "Gpu", "Npu", "Power", "Guard",
        "CodexQuota", "ClaudeQuota"
    };

    public const int MetricTileCount = 10;

    public static int[] CreateAutoTileArray()
    {
        int[] values = new int[MetricTileCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = AutoTilePosition;
        }

        return values;
    }

    private static int[] NormalizeTileArray(int[] values)
    {
        if (values == null || values.Length != MetricTileCount)
        {
            int[] fixedUp = CreateAutoTileArray();
            if (values != null)
            {
                for (int i = 0; i < Math.Min(values.Length, MetricTileCount); i++)
                {
                    fixedUp[i] = values[i];
                }
            }

            return fixedUp;
        }

        return values;
    }

    private static int[] CloneTileArray(int[] values)
    {
        int[] normalized = NormalizeTileArray(values);
        int[] copy = new int[MetricTileCount];
        Array.Copy(normalized, copy, MetricTileCount);
        return copy;
    }

    private static string SerializeTileArray(int[] values)
    {
        int[] normalized = NormalizeTileArray(values);
        string[] parts = new string[MetricTileCount];
        for (int i = 0; i < MetricTileCount; i++)
        {
            parts[i] = normalized[i] == AutoTilePosition
                ? "auto"
                : normalized[i].ToString(CultureInfo.InvariantCulture);
        }

        return string.Join(",", parts);
    }

    private static int[] ParseTileArray(string value)
    {
        int[] result = CreateAutoTileArray();
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        string[] parts = value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(parts.Length, MetricTileCount); i++)
        {
            int parsed;
            string token = parts[i].Trim();
            if (!string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                result[i] = parsed;
            }
        }

        return result;
    }

    public static int IndexOfMetricTile(string tileId)
    {
        for (int i = 0; i < MetricTileIds.Length; i++)
        {
            if (string.Equals(MetricTileIds[i], tileId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public static string[] NormalizeLeftDockButtonOrder(string[] order)
    {
        return NormalizeColumnButtonOrder(order, DefaultLeftDockButtonOrder);
    }

    public static string[] NormalizeRightTileButtonOrder(string[] order)
    {
        return NormalizeColumnButtonOrder(order, DefaultRightTileButtonOrder);
    }

    private static string[] CloneLeftDockButtonOrder(string[] order)
    {
        return NormalizeLeftDockButtonOrder(order);
    }

    private static string[] CloneRightTileButtonOrder(string[] order)
    {
        return NormalizeRightTileButtonOrder(order);
    }

    private static string[] NormalizeColumnButtonOrder(string[] order, string[] defaultOrder)
    {
        List<string> normalized = new List<string>();
        if (order != null)
        {
            for (int i = 0; i < order.Length; i++)
            {
                string canonical = FindCanonicalColumnButtonId(order[i], defaultOrder);
                if (canonical.Length == 0 || ContainsColumnButtonId(normalized, canonical))
                {
                    continue;
                }

                normalized.Add(canonical);
            }
        }

        // A saved order is a preference over stable identities, not the identity registry itself.
        // Appending newly introduced or omitted IDs keeps upgrades forward-compatible without
        // discarding the user's relative ordering of the buttons they already arranged.
        for (int i = 0; i < defaultOrder.Length; i++)
        {
            if (!ContainsColumnButtonId(normalized, defaultOrder[i]))
            {
                normalized.Add(defaultOrder[i]);
            }
        }

        return normalized.ToArray();
    }

    private static string FindCanonicalColumnButtonId(string value, string[] allowed)
    {
        string candidate = (value ?? string.Empty).Trim();
        for (int i = 0; i < allowed.Length; i++)
        {
            if (string.Equals(candidate, allowed[i], StringComparison.OrdinalIgnoreCase))
            {
                return allowed[i];
            }
        }

        return string.Empty;
    }

    private static bool ContainsColumnButtonId(List<string> values, string candidate)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public int GetMetricTileLeftX(int index)
    {
        this.MetricTileLeftX = NormalizeTileArray(this.MetricTileLeftX);
        return index >= 0 && index < MetricTileCount ? this.MetricTileLeftX[index] : AutoTilePosition;
    }

    public int GetMetricTileBottomY(int index)
    {
        this.MetricTileBottomY = NormalizeTileArray(this.MetricTileBottomY);
        return index >= 0 && index < MetricTileCount ? this.MetricTileBottomY[index] : AutoTilePosition;
    }

    public void SetMetricTilePosition(int index, int leftX, int bottomY)
    {
        if (index < 0 || index >= MetricTileCount)
        {
            return;
        }

        this.MetricTileLeftX = NormalizeTileArray(this.MetricTileLeftX);
        this.MetricTileBottomY = NormalizeTileArray(this.MetricTileBottomY);
        this.MetricTileLeftX[index] = leftX;
        this.MetricTileBottomY[index] = bottomY;
    }
    public OperationRenderVariant OperationRenderVariant { get; set; }
    public bool CodexRadarPublicJsonEnabled { get; set; }
    public bool CodexRadarHtmlFallbackEnabled { get; set; }
    public bool CodexRadarRssFallbackEnabled { get; set; }
    public bool CodexQuotaDueResetProtectionEnabled { get; set; }
    public bool CodexQuotaRssResetProtectionEnabled { get; set; }
    public bool CodexQuotaProviderZeroDropProtectionEnabled { get; set; }
    public bool CodexQuotaDuplicateSameBalanceRingProtectionEnabled { get; set; }
    public bool CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled { get; set; }
    public bool CodexQuotaProviderWeeklySpikeProtectionEnabled { get; set; }
    public bool CodexQuotaStrictFiveHourResetBoundaryEnabled { get; set; }
    public bool CodexQuotaWeeklyBaselineAutoRepairEnabled { get; set; }
    public int CodexRadarServiceProbeToken { get; set; }
    public bool AiRequestProtectionAutoEnabled { get; set; }
    public bool AiRequestProtectionManualBlockEnabled { get; set; }
    // Fail-closed guard: when the egress IP is (or cannot be confirmed not to be) mainland China,
    // suppress this app's own Anthropic/OpenAI calls and raise a full-screen warning. Protects the
    // user from inadvertently accessing those providers from a mainland-China IP.
    public bool AiChinaEgressGuardEnabled { get; set; }
    public bool CodexQuotaPlanEnabled { get; set; }
    public CodexQuotaPlanComparison CodexQuotaPlanWeeklyComparison { get; set; }
    public int CodexQuotaPlanWeeklyThresholdPercent { get; set; }
    public CodexQuotaPlanComparison CodexQuotaPlanFiveHourComparison { get; set; }
    public int CodexQuotaPlanFiveHourThresholdPercent { get; set; }
    public CodexQuotaPlanResumeConditionMode CodexQuotaPlanResumeConditionMode { get; set; }
    public bool CodexQuotaPlanAutoResumePausedGoals { get; set; }
    public string CodexQuotaPlanPauseGoalIds { get; set; }
    public string CodexQuotaPlanResumeGoalIds { get; set; }
    public CleanIpBadgeTestMode CleanIpBadgeTestMode { get; set; }
    public bool CodexModelIqTestEnabled { get; set; }
    public int CodexModelIqTestPassed { get; set; }
    public bool CodexModelIqBaselineAutoEnabled { get; set; }
    public int CodexModelIqBaselinePassed { get; set; }
    public int CodexModelIqBaselineValidTasks { get; set; }
    public CodexModelBaselineMode CodexModelIqBaselineMode { get; set; }
    public bool CodexModelEfficiencyTestEnabled { get; set; }
    public int CodexModelTokenEfficiencyTestPercent { get; set; }
    public int CodexModelTimeEfficiencyTestPercent { get; set; }
    public int CodexModelTokenEfficiencyBaselinePassed { get; set; }
    public int CodexModelTokenEfficiencyBaselineTokens { get; set; }
    public CodexModelBaselineMode CodexModelTokenEfficiencyBaselineMode { get; set; }
    public int CodexModelTimeEfficiencyBaselinePassed { get; set; }
    public int CodexModelTimeEfficiencyBaselineSeconds { get; set; }
    public CodexModelBaselineMode CodexModelTimeEfficiencyBaselineMode { get; set; }
    public int CodexModelTokenEfficiencyLowThresholdPercent { get; set; }
    public int CodexModelTimeEfficiencyLowThresholdPercent { get; set; }
    public CodexRadarModelVersion CodexRadarModelVersion { get; set; }
    public string CodexRadarModelKey { get; set; }
    public CodexRadarSoftwareMode CodexRadarSoftwareMode { get; set; }
    public bool RadarClockAutoSwitchModelEnabled { get; set; }
    public RadarClockTimeDisplayMode RadarClockTimeDisplayMode { get; set; }
    public bool CodexRadarSpeedWindowCountdownEnabled { get; set; }
    public bool CodexRadarQuotaResetRainbowEnabled { get; set; }
    public DisplayTimeZoneMode DisplayTimeZoneMode { get; set; }
    public string DisplayTimeZoneId { get; set; }
    public WidgetPerformanceMode PerformanceMode { get; set; }
    public bool PowerSavingEnabled
    {
        get { return GetEffectivePerformanceMode(this.PerformanceMode) == WidgetPerformanceMode.BatterySaver; }
        set { this.PerformanceMode = value ? WidgetPerformanceMode.BatterySaver : WidgetPerformanceMode.Balanced; }
    }

    public bool HoverOpacityEnabled { get; set; }
    public bool ForceHoverOpacityActive { get; set; }
    public bool ManualHoverOpacityActive { get; set; }
    public bool SensitiveMouseModeEnabled { get; set; }
    public int SensitiveMouseRangePixels { get; set; }
    public bool HoverOpacityRevealDelayEnabled { get; set; }
    public double HoverOpacityRevealDelaySeconds { get; set; }
    public double HoverOpacityRevealResetSeconds { get; set; }
    public bool HoverOpacityCoverEnabled { get; set; }
    public bool ReverseHoverOpacityRevealEnabled { get; set; }
    public int ReverseHoverOpacityRestoreDelaySeconds { get; set; }
    public bool AutoHoverOpacityIdleEnabled { get; set; }
    public int AutoHoverOpacityIdleSeconds { get; set; }
    public bool AutoHoverOpacityMaximizedEnabled { get; set; }
    public bool BurnInProtectionEnabled { get; set; }
    public int BurnInLevelOneIdleSeconds { get; set; }
    public int BurnInLevelTwoDelaySeconds { get; set; }
    public bool OperationRadialCoreAutoHideKeepAliveEnabled { get; set; }
    public int OperationRadialIdleCollapseSeconds { get; set; }
    public bool OperationRadialIdleResetOnInteractionEnabled { get; set; }
    public bool OperationRadialKeepOpenAfterLeafClickEnabled { get; set; }
    public bool OperationDoubleClickSpecialMenuEnabled { get; set; }
    public bool OperationSettingsLogicExtensionEnabled { get; set; }

    public static string SettingsPath
    {
        get { return Path.Combine(Logger.DirectoryPath, "settings.ini"); }
    }

    public WidgetSettings()
    {
        WidgetSettings defaults = CreateDefaults();
        this.ApplicationTransparencyPercent = defaults.ApplicationTransparencyPercent;
        this.MainWidgetTransparencyOverridePercent = defaults.MainWidgetTransparencyOverridePercent;
        this.NetworkMonitorTransparencyOverridePercent = defaults.NetworkMonitorTransparencyOverridePercent;
        this.OperationTransparencyOverridePercent = defaults.OperationTransparencyOverridePercent;
        this.SpecBoardTransparencyOverridePercent = defaults.SpecBoardTransparencyOverridePercent;
        this.CodexTaskBoardTransparencyOverridePercent = defaults.CodexTaskBoardTransparencyOverridePercent;
        this.GuardBoardTransparencyOverridePercent = defaults.GuardBoardTransparencyOverridePercent;
        this.CodexIqBoardTransparencyOverridePercent = defaults.CodexIqBoardTransparencyOverridePercent;
        this.NightScheduleEnabled = defaults.NightScheduleEnabled;
        this.NightScheduleStartMinutes = defaults.NightScheduleStartMinutes;
        this.NightScheduleEndMinutes = defaults.NightScheduleEndMinutes;
        this.NightDimLuminancePercent = defaults.NightDimLuminancePercent;
        this.NightQuietHoursEnabled = defaults.NightQuietHoursEnabled;
        this.AlertQuotaEnabled = defaults.AlertQuotaEnabled;
        this.AlertResetProtectionEnabled = defaults.AlertResetProtectionEnabled;
        this.AlertServiceHealthEnabled = defaults.AlertServiceHealthEnabled;
        this.AlertCodexTaskEnabled = defaults.AlertCodexTaskEnabled;
        this.HotkeyToggleAllWindows = defaults.HotkeyToggleAllWindows;
        this.HotkeyToggleHoverOpacity = defaults.HotkeyToggleHoverOpacity;
        this.HotkeyOpenSettings = defaults.HotkeyOpenSettings;
        this.MainWidgetScaleOverridePercent = defaults.MainWidgetScaleOverridePercent;
        this.NetworkMonitorScaleOverridePercent = defaults.NetworkMonitorScaleOverridePercent;
        this.OperationScaleOverridePercent = defaults.OperationScaleOverridePercent;
        this.SpecBoardScaleOverridePercent = defaults.SpecBoardScaleOverridePercent;
        this.CodexTaskBoardScaleOverridePercent = defaults.CodexTaskBoardScaleOverridePercent;
        this.GuardBoardScaleOverridePercent = defaults.GuardBoardScaleOverridePercent;
        this.CodexIqBoardScaleOverridePercent = defaults.CodexIqBoardScaleOverridePercent;
        this.PowerThermalIntegratedEnabled = defaults.PowerThermalIntegratedEnabled;
        this.PowerThermalManualEnergySaverThresholdPercent = defaults.PowerThermalManualEnergySaverThresholdPercent;
        this.NetworkMonitorAdapterId = defaults.NetworkMonitorAdapterId;
        this.NetworkStatusTestMode = defaults.NetworkStatusTestMode;
        this.GfwProbeEnabled = defaults.GfwProbeEnabled;
        this.GfwProbeIntervalMinutes = defaults.GfwProbeIntervalMinutes;
        this.GfwProbeManualRefreshToken = 0;
        this.CloudEndpointTestSeed = defaults.CloudEndpointTestSeed;
        this.CloudStatusRegionMask = defaults.CloudStatusRegionMask;
        this.CloudEndpointTargets = NetworkProbeTargetSettings.CloneArray(defaults.CloudEndpointTargets);
        this.FixedPingTargets = NetworkProbeTargetSettings.CloneArray(defaults.FixedPingTargets);
        this.ConnectionCheckIntervalSeconds = defaults.ConnectionCheckIntervalSeconds;
        this.ConnectionCheckManualRefreshToken = 0;
        this.SpecBoardWidth = defaults.SpecBoardWidth;
        this.SpecBoardHeight = defaults.SpecBoardHeight;
        this.SpecBoardLeftX = defaults.SpecBoardLeftX;
        this.SpecBoardBottomY = defaults.SpecBoardBottomY;
        this.SpecBoardAutoHideSeconds = defaults.SpecBoardAutoHideSeconds;
        this.SpecBoardAutoPopupEnabled = defaults.SpecBoardAutoPopupEnabled;
        this.SpecBoardAutoPopupSeconds = defaults.SpecBoardAutoPopupSeconds;
        this.SpecBoardLedgerPath = defaults.SpecBoardLedgerPath;
        this.SpecBoardManagerWidth = defaults.SpecBoardManagerWidth;
        this.SpecBoardManagerHeight = defaults.SpecBoardManagerHeight;
        this.SpecBoardManagerDangerZoneRequiresTypedConfirm = defaults.SpecBoardManagerDangerZoneRequiresTypedConfirm;
        this.LeftDockAutoArrangeEnabled = defaults.LeftDockAutoArrangeEnabled;
        this.LeftDockButtonOrder = CloneLeftDockButtonOrder(defaults.LeftDockButtonOrder);
        this.LeftDockButtonGapPixels = defaults.LeftDockButtonGapPixels;
        this.LeftDockGroupOffsetY = defaults.LeftDockGroupOffsetY;
        this.SpecBoardLeftDockEnabled = defaults.SpecBoardLeftDockEnabled;
        this.SpecBoardLeftDockTabCenterY = defaults.SpecBoardLeftDockTabCenterY;
        this.CodexTaskBoardLeftDockEnabled = defaults.CodexTaskBoardLeftDockEnabled;
        this.CodexTaskBoardLeftDockTabCenterY = defaults.CodexTaskBoardLeftDockTabCenterY;
        this.NetworkMonitorLeftDockTabCenterY = defaults.NetworkMonitorLeftDockTabCenterY;
        this.GuardBoardLeftDockEnabled = defaults.GuardBoardLeftDockEnabled;
        this.GuardBoardLeftDockTabCenterY = defaults.GuardBoardLeftDockTabCenterY;
        this.GuardBoardAutoHideSeconds = defaults.GuardBoardAutoHideSeconds;
        this.CodexIqBoardLeftDockEnabled = defaults.CodexIqBoardLeftDockEnabled;
        this.CodexIqBoardLeftDockTabCenterY = defaults.CodexIqBoardLeftDockTabCenterY;
        this.CodexIqBoardAutoHideSeconds = defaults.CodexIqBoardAutoHideSeconds;
        this.GuardSleepEnabled = defaults.GuardSleepEnabled;
        this.GuardSleepSinceUtcTicks = defaults.GuardSleepSinceUtcTicks;
        this.GuardDisplayMinutes = defaults.GuardDisplayMinutes;
        this.GuardOfflineThresholdMinutes = defaults.GuardOfflineThresholdMinutes;
        this.GuardDisplayUntilUtcTicks = defaults.GuardDisplayUntilUtcTicks;
        this.GuardBatteryCarePauseUntilUtcTicks = defaults.GuardBatteryCarePauseUntilUtcTicks;
        this.LeftDockCollapseSeconds = defaults.LeftDockCollapseSeconds;
        this.LeftDockOutsideClickCollapseEnabled = defaults.LeftDockOutsideClickCollapseEnabled;
        this.CodexTaskBoardWidth = defaults.CodexTaskBoardWidth;
        this.CodexTaskBoardHeight = defaults.CodexTaskBoardHeight;
        this.CodexTaskBoardView = defaults.CodexTaskBoardView;
        this.CodexTaskBoardTimelineMinutes = defaults.CodexTaskBoardTimelineMinutes;
        this.CodexTaskMonitorEnabled = defaults.CodexTaskMonitorEnabled;
        this.CodexTaskMonitorActiveWindowMinutes = defaults.CodexTaskMonitorActiveWindowMinutes;
        this.CodexTaskMonitorActiveSeconds = defaults.CodexTaskMonitorActiveSeconds;
        this.CodexTaskMonitorIdleSeconds = defaults.CodexTaskMonitorIdleSeconds;
        this.CodexTaskMonitorTerminalHoldSeconds = defaults.CodexTaskMonitorTerminalHoldSeconds;
        this.CodexTaskMonitorErrorHoldSeconds = defaults.CodexTaskMonitorErrorHoldSeconds;
        this.CodexTaskMonitorNumberCooldownSeconds = defaults.CodexTaskMonitorNumberCooldownSeconds;
        this.OperationButtonSize = defaults.OperationButtonSize;
        this.OperationLeftOffset = defaults.OperationLeftOffset;
        this.OperationBottomOffset = defaults.OperationBottomOffset;
        this.OperationBackgroundTransparencyPercent = defaults.OperationBackgroundTransparencyPercent;
        this.OperationPrimaryPanelMode = defaults.OperationPrimaryPanelMode;
        this.OperationWindowsButtonEnabled = defaults.OperationWindowsButtonEnabled;
        this.OperationMemoryPieEnabled = defaults.OperationMemoryPieEnabled;
        this.ForceShowForegroundFpsEnabled = defaults.ForceShowForegroundFpsEnabled;
        this.SeelenDockForegroundPulseEnabled = defaults.SeelenDockForegroundPulseEnabled;
        this.WinDRecoveryPulseEnabled = defaults.WinDRecoveryPulseEnabled;
        this.CodexPetZOrderProtectionEnabled = defaults.CodexPetZOrderProtectionEnabled;
        this.PowerResumeRestartEnabled = defaults.PowerResumeRestartEnabled;
        this.FallbackDisconnectedDisplaysEnabled = defaults.FallbackDisconnectedDisplaysEnabled;
        this.MainDisplayDeviceName = defaults.MainDisplayDeviceName;
        this.OperationDisplayDeviceName = defaults.OperationDisplayDeviceName;
        this.ResolutionCompatibilityModeEnabled = defaults.ResolutionCompatibilityModeEnabled;
        this.ResolutionCompatibilityScalePercent = defaults.ResolutionCompatibilityScalePercent;
        this.LayoutWorkAreaLeft = defaults.LayoutWorkAreaLeft;
        this.LayoutWorkAreaTop = defaults.LayoutWorkAreaTop;
        this.LayoutWorkAreaWidth = defaults.LayoutWorkAreaWidth;
        this.LayoutWorkAreaHeight = defaults.LayoutWorkAreaHeight;
        this.OperationLayoutWorkAreaLeft = defaults.OperationLayoutWorkAreaLeft;
        this.OperationLayoutWorkAreaTop = defaults.OperationLayoutWorkAreaTop;
        this.OperationLayoutWorkAreaWidth = defaults.OperationLayoutWorkAreaWidth;
        this.OperationLayoutWorkAreaHeight = defaults.OperationLayoutWorkAreaHeight;
        this.VisibilityMode = defaults.VisibilityMode;
        this.VisibilityOverlapIgnoresOperationPanelEnabled = defaults.VisibilityOverlapIgnoresOperationPanelEnabled;
        this.ClickThroughMode = defaults.ClickThroughMode;
        this.StartupEnabled = Program.IsStartupEnabled();
        this.AlertTestEnabled = defaults.AlertTestEnabled;
        this.ThermalTestMode = defaults.ThermalTestMode;
        this.CodexRadarTestMode = defaults.CodexRadarTestMode;
        this.ServiceHealthTestMode = defaults.ServiceHealthTestMode;
        this.CodexRadarRandomTestEnabled = defaults.CodexRadarRandomTestEnabled;
        this.CodexRadarRandomTestAutoRefresh = defaults.CodexRadarRandomTestAutoRefresh;
        this.CodexRadarRandomTestRefreshToken = defaults.CodexRadarRandomTestRefreshToken;
        this.MetricTileExpandWidth = defaults.MetricTileExpandWidth;
        this.MetricTileExpandHeight = defaults.MetricTileExpandHeight;
        this.CodexRadarPublicJsonEnabled = defaults.CodexRadarPublicJsonEnabled;
        this.CodexRadarHtmlFallbackEnabled = defaults.CodexRadarHtmlFallbackEnabled;
        this.CodexRadarRssFallbackEnabled = defaults.CodexRadarRssFallbackEnabled;
        this.CodexQuotaDueResetProtectionEnabled = defaults.CodexQuotaDueResetProtectionEnabled;
        this.CodexQuotaRssResetProtectionEnabled = defaults.CodexQuotaRssResetProtectionEnabled;
        this.CodexQuotaProviderZeroDropProtectionEnabled = defaults.CodexQuotaProviderZeroDropProtectionEnabled;
        this.CodexQuotaDuplicateSameBalanceRingProtectionEnabled = defaults.CodexQuotaDuplicateSameBalanceRingProtectionEnabled;
        this.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled = defaults.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled;
        this.CodexQuotaProviderWeeklySpikeProtectionEnabled = defaults.CodexQuotaProviderWeeklySpikeProtectionEnabled;
        this.CodexQuotaStrictFiveHourResetBoundaryEnabled = defaults.CodexQuotaStrictFiveHourResetBoundaryEnabled;
        this.CodexQuotaWeeklyBaselineAutoRepairEnabled = defaults.CodexQuotaWeeklyBaselineAutoRepairEnabled;
        this.CodexRadarServiceProbeToken = defaults.CodexRadarServiceProbeToken;
        this.AiRequestProtectionAutoEnabled = defaults.AiRequestProtectionAutoEnabled;
        this.AiRequestProtectionManualBlockEnabled = defaults.AiRequestProtectionManualBlockEnabled;
        this.AiChinaEgressGuardEnabled = defaults.AiChinaEgressGuardEnabled;
        this.CodexQuotaPlanEnabled = defaults.CodexQuotaPlanEnabled;
        this.CodexQuotaPlanWeeklyComparison = defaults.CodexQuotaPlanWeeklyComparison;
        this.CodexQuotaPlanWeeklyThresholdPercent = defaults.CodexQuotaPlanWeeklyThresholdPercent;
        this.CodexQuotaPlanFiveHourComparison = defaults.CodexQuotaPlanFiveHourComparison;
        this.CodexQuotaPlanFiveHourThresholdPercent = defaults.CodexQuotaPlanFiveHourThresholdPercent;
        this.CodexQuotaPlanResumeConditionMode = defaults.CodexQuotaPlanResumeConditionMode;
        this.CodexQuotaPlanAutoResumePausedGoals = defaults.CodexQuotaPlanAutoResumePausedGoals;
        this.CodexQuotaPlanPauseGoalIds = defaults.CodexQuotaPlanPauseGoalIds;
        this.CodexQuotaPlanResumeGoalIds = defaults.CodexQuotaPlanResumeGoalIds;
        this.CleanIpBadgeTestMode = defaults.CleanIpBadgeTestMode;
        this.CodexModelIqTestEnabled = defaults.CodexModelIqTestEnabled;
        this.CodexModelIqTestPassed = defaults.CodexModelIqTestPassed;
        this.CodexModelIqBaselineAutoEnabled = defaults.CodexModelIqBaselineAutoEnabled;
        this.CodexModelIqBaselinePassed = defaults.CodexModelIqBaselinePassed;
        this.CodexModelIqBaselineValidTasks = defaults.CodexModelIqBaselineValidTasks;
        this.CodexModelIqBaselineMode = defaults.CodexModelIqBaselineMode;
        this.CodexModelEfficiencyTestEnabled = defaults.CodexModelEfficiencyTestEnabled;
        this.CodexModelTokenEfficiencyTestPercent = defaults.CodexModelTokenEfficiencyTestPercent;
        this.CodexModelTimeEfficiencyTestPercent = defaults.CodexModelTimeEfficiencyTestPercent;
        this.CodexModelTokenEfficiencyBaselinePassed = defaults.CodexModelTokenEfficiencyBaselinePassed;
        this.CodexModelTokenEfficiencyBaselineTokens = defaults.CodexModelTokenEfficiencyBaselineTokens;
        this.CodexModelTokenEfficiencyBaselineMode = defaults.CodexModelTokenEfficiencyBaselineMode;
        this.CodexModelTimeEfficiencyBaselinePassed = defaults.CodexModelTimeEfficiencyBaselinePassed;
        this.CodexModelTimeEfficiencyBaselineSeconds = defaults.CodexModelTimeEfficiencyBaselineSeconds;
        this.CodexModelTimeEfficiencyBaselineMode = defaults.CodexModelTimeEfficiencyBaselineMode;
        this.CodexModelTokenEfficiencyLowThresholdPercent = defaults.CodexModelTokenEfficiencyLowThresholdPercent;
        this.CodexModelTimeEfficiencyLowThresholdPercent = defaults.CodexModelTimeEfficiencyLowThresholdPercent;
        this.CodexRadarModelVersion = defaults.CodexRadarModelVersion;
        this.CodexRadarModelKey = defaults.CodexRadarModelKey;
        this.CodexRadarSoftwareMode = defaults.CodexRadarSoftwareMode;
        this.RadarClockAutoSwitchModelEnabled = defaults.RadarClockAutoSwitchModelEnabled;
        this.RadarClockTimeDisplayMode = defaults.RadarClockTimeDisplayMode;
        this.CodexRadarSpeedWindowCountdownEnabled = defaults.CodexRadarSpeedWindowCountdownEnabled;
        this.CodexRadarQuotaResetRainbowEnabled = defaults.CodexRadarQuotaResetRainbowEnabled;
        this.DisplayTimeZoneMode = defaults.DisplayTimeZoneMode;
        this.DisplayTimeZoneId = defaults.DisplayTimeZoneId;
        this.PerformanceMode = defaults.PerformanceMode;
        this.HoverOpacityEnabled = defaults.HoverOpacityEnabled;
        this.ManualHoverOpacityActive = defaults.ManualHoverOpacityActive;
        this.SensitiveMouseModeEnabled = defaults.SensitiveMouseModeEnabled;
        this.SensitiveMouseRangePixels = defaults.SensitiveMouseRangePixels;
        this.HoverOpacityRevealDelayEnabled = defaults.HoverOpacityRevealDelayEnabled;
        this.HoverOpacityRevealDelaySeconds = defaults.HoverOpacityRevealDelaySeconds;
        this.HoverOpacityRevealResetSeconds = defaults.HoverOpacityRevealResetSeconds;
        this.HoverOpacityCoverEnabled = defaults.HoverOpacityCoverEnabled;
        this.ReverseHoverOpacityRevealEnabled = defaults.ReverseHoverOpacityRevealEnabled;
        this.ReverseHoverOpacityRestoreDelaySeconds = defaults.ReverseHoverOpacityRestoreDelaySeconds;
        this.AutoHoverOpacityIdleEnabled = defaults.AutoHoverOpacityIdleEnabled;
        this.AutoHoverOpacityIdleSeconds = defaults.AutoHoverOpacityIdleSeconds;
        this.AutoHoverOpacityMaximizedEnabled = defaults.AutoHoverOpacityMaximizedEnabled;
        this.BurnInProtectionEnabled = defaults.BurnInProtectionEnabled;
        this.BurnInLevelOneIdleSeconds = defaults.BurnInLevelOneIdleSeconds;
        this.BurnInLevelTwoDelaySeconds = defaults.BurnInLevelTwoDelaySeconds;
        this.OperationRadialCoreAutoHideKeepAliveEnabled = defaults.OperationRadialCoreAutoHideKeepAliveEnabled;
        this.OperationRadialIdleCollapseSeconds = defaults.OperationRadialIdleCollapseSeconds;
        this.OperationRadialIdleResetOnInteractionEnabled = defaults.OperationRadialIdleResetOnInteractionEnabled;
        this.OperationDoubleClickSpecialMenuEnabled = defaults.OperationDoubleClickSpecialMenuEnabled;
        this.OperationSettingsLogicExtensionEnabled = defaults.OperationSettingsLogicExtensionEnabled;
        this.MetricTileLeftX = CloneTileArray(defaults.MetricTileLeftX);
        this.MetricTileBottomY = CloneTileArray(defaults.MetricTileBottomY);
        this.RightTileAutoArrangeEnabled = defaults.RightTileAutoArrangeEnabled;
        this.RightTileButtonOrder = CloneRightTileButtonOrder(defaults.RightTileButtonOrder);
        this.RightTileButtonGapPixels = defaults.RightTileButtonGapPixels;
        this.RightTileGroupOffsetY = defaults.RightTileGroupOffsetY;
    }

    private WidgetSettings(bool skipDefaults)
    {
    }

    public static WidgetSettings CreateDefaults()
    {
        WidgetSettings settings = new WidgetSettings(true);
        settings.ApplicationTransparencyPercent = 0;
        settings.MainWidgetTransparencyOverridePercent = -1;
        settings.NetworkMonitorTransparencyOverridePercent = -1;
        settings.OperationTransparencyOverridePercent = -1;
        settings.SpecBoardTransparencyOverridePercent = -1;
        settings.CodexTaskBoardTransparencyOverridePercent = -1;
        settings.GuardBoardTransparencyOverridePercent = -1;
        settings.CodexIqBoardTransparencyOverridePercent = -1;
        settings.NightScheduleEnabled = false;
        settings.NightScheduleStartMinutes = DefaultNightScheduleStartMinutes;
        settings.NightScheduleEndMinutes = DefaultNightScheduleEndMinutes;
        settings.NightDimLuminancePercent = DefaultNightDimLuminancePercent;
        settings.NightQuietHoursEnabled = true;
        settings.AlertQuotaEnabled = true;
        settings.AlertResetProtectionEnabled = true;
        settings.AlertServiceHealthEnabled = true;
        settings.AlertCodexTaskEnabled = true;
        settings.HotkeyToggleAllWindows = string.Empty;
        settings.HotkeyToggleHoverOpacity = string.Empty;
        settings.HotkeyOpenSettings = string.Empty;
        settings.MainWidgetScaleOverridePercent = -1;
        settings.NetworkMonitorScaleOverridePercent = -1;
        settings.OperationScaleOverridePercent = -1;
        settings.SpecBoardScaleOverridePercent = -1;
        settings.CodexTaskBoardScaleOverridePercent = -1;
        settings.GuardBoardScaleOverridePercent = -1;
        settings.CodexIqBoardScaleOverridePercent = -1;
        settings.PowerThermalIntegratedEnabled = true;
        settings.PowerThermalManualEnergySaverThresholdPercent = DefaultPowerThermalManualEnergySaverThresholdPercent;
        settings.NetworkMonitorAdapterId = string.Empty;
        settings.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        settings.GfwProbeEnabled = true;
        settings.GfwProbeIntervalMinutes = DefaultGfwProbeIntervalMinutes;
        settings.GfwProbeManualRefreshToken = 0;
        settings.CloudEndpointTestSeed = 0;
        settings.CloudStatusRegionMask = DefaultCloudStatusRegionMask;
        settings.CloudEndpointTargets = NetworkProbeTargetSettings.CloneArray(NetworkProbeTargetSettings.DefaultCloudEndpointTargets);
        settings.FixedPingTargets = NetworkProbeTargetSettings.CloneArray(NetworkProbeTargetSettings.DefaultFixedPingTargets);
        settings.ConnectionCheckIntervalSeconds = 600;
        settings.ConnectionCheckManualRefreshToken = 0;
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        settings.SpecBoardLeftX = -1;
        settings.SpecBoardBottomY = -1;
        settings.SpecBoardAutoHideSeconds = DefaultSpecBoardAutoHideSeconds;
        settings.SpecBoardAutoPopupEnabled = true;
        settings.SpecBoardAutoPopupSeconds = DefaultSpecBoardAutoPopupSeconds;
        settings.SpecBoardLedgerPath = DefaultSpecBoardLedgerPath;
        settings.SpecBoardManagerWidth = 720;
        settings.SpecBoardManagerHeight = 520;
        settings.SpecBoardManagerDangerZoneRequiresTypedConfirm = true;
        settings.LeftDockAutoArrangeEnabled = true;
        settings.LeftDockButtonOrder = CloneLeftDockButtonOrder(DefaultLeftDockButtonOrder);
        settings.LeftDockButtonGapPixels = DefaultLeftDockButtonGapPixels;
        settings.LeftDockGroupOffsetY = DefaultColumnGroupOffsetY;
        settings.SpecBoardLeftDockEnabled = true;
        settings.SpecBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.CodexTaskBoardLeftDockEnabled = true;
        settings.CodexTaskBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.NetworkMonitorLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.GuardBoardLeftDockEnabled = true;
        settings.GuardBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.GuardBoardAutoHideSeconds = DefaultGuardBoardAutoHideSeconds;
        settings.CodexIqBoardLeftDockEnabled = true;
        settings.CodexIqBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.CodexIqBoardAutoHideSeconds = DefaultCodexIqBoardAutoHideSeconds;
        settings.GuardSleepEnabled = false;
        settings.GuardSleepSinceUtcTicks = 0L;
        settings.GuardDisplayMinutes = DefaultGuardDisplayMinutes;
        settings.GuardOfflineThresholdMinutes = DefaultGuardOfflineThresholdMinutes;
        settings.GuardDisplayUntilUtcTicks = 0L;
        settings.GuardBatteryCarePauseUntilUtcTicks = 0L;
        settings.LeftDockCollapseSeconds = DefaultLeftDockCollapseSeconds;
        settings.LeftDockOutsideClickCollapseEnabled = true;
        settings.CodexTaskBoardWidth = DefaultCodexTaskBoardWidth;
        settings.CodexTaskBoardHeight = DefaultCodexTaskBoardHeight;
        settings.CodexTaskBoardView = CodexTaskBoardView.Table;
        settings.CodexTaskBoardTimelineMinutes = DefaultCodexTaskBoardTimelineMinutes;
        settings.CodexTaskMonitorEnabled = true;
        settings.CodexTaskMonitorActiveWindowMinutes = 30;
        settings.CodexTaskMonitorActiveSeconds = 12;
        settings.CodexTaskMonitorIdleSeconds = 90;
        settings.CodexTaskMonitorTerminalHoldSeconds = 120;
        settings.CodexTaskMonitorErrorHoldSeconds = 30;
        settings.CodexTaskMonitorNumberCooldownSeconds = 120;
        settings.OperationButtonSize = 86;
        settings.OperationLeftOffset = 0;
        settings.OperationBottomOffset = 0;
        settings.OperationBackgroundTransparencyPercent = 0;
        settings.OperationPrimaryPanelMode = OperationPrimaryPanelMode.Auto;
        settings.OperationWindowsButtonEnabled = true;
        settings.OperationMemoryPieEnabled = true;
        settings.ForceShowForegroundFpsEnabled = false;
        settings.SeelenDockForegroundPulseEnabled = true;
        settings.WinDRecoveryPulseEnabled = true;
        settings.CodexPetZOrderProtectionEnabled = true;
        settings.PowerResumeRestartEnabled = true;
        settings.FallbackDisconnectedDisplaysEnabled = true;
        settings.MainDisplayDeviceName = string.Empty;
        settings.OperationDisplayDeviceName = string.Empty;
        settings.ResolutionCompatibilityModeEnabled = false;
        settings.ResolutionCompatibilityScalePercent = DefaultResolutionCompatibilityScalePercent;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 60;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1740;
        settings.OperationLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.OperationLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.OperationLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.OperationLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.VisibilityMode = WidgetVisibilityMode.HideWhenFullscreen;
        settings.VisibilityOverlapIgnoresOperationPanelEnabled = true;
        settings.ClickThroughMode = ClickThroughMode.Auto;
        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.AlertTestEnabled = false;
        settings.ThermalTestMode = ThermalTestMode.Off;
        settings.CodexRadarTestMode = CodexRadarTestMode.Off;
        settings.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        settings.CodexRadarRandomTestEnabled = false;
        settings.CodexRadarRandomTestAutoRefresh = false;
        settings.CodexRadarRandomTestRefreshToken = 0;
        settings.MainWidgetTileLargeModeEnabled = false;
        settings.MetricTileExpandWidth = DefaultMetricTileExpandWidth;
        settings.MetricTileExpandHeight = DefaultMetricTileExpandHeight;
        settings.MetricTileLeftX = CreateAutoTileArray();
        settings.MetricTileBottomY = CreateAutoTileArray();
        settings.RightTileAutoArrangeEnabled = true;
        settings.RightTileButtonOrder = CloneRightTileButtonOrder(DefaultRightTileButtonOrder);
        settings.RightTileButtonGapPixels = DefaultRightTileButtonGapPixels;
        settings.RightTileGroupOffsetY = DefaultColumnGroupOffsetY;
        settings.OperationRenderVariant = OperationRenderVariant.RadialDial;
        settings.CodexRadarPublicJsonEnabled = true;
        settings.CodexRadarHtmlFallbackEnabled = true;
        settings.CodexRadarRssFallbackEnabled = true;
        settings.CodexQuotaDueResetProtectionEnabled = true;
        settings.CodexQuotaRssResetProtectionEnabled = true;
        settings.CodexQuotaProviderZeroDropProtectionEnabled = true;
        settings.CodexQuotaDuplicateSameBalanceRingProtectionEnabled = true;
        settings.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled = false;
        settings.CodexQuotaProviderWeeklySpikeProtectionEnabled = false;
        settings.CodexQuotaStrictFiveHourResetBoundaryEnabled = false;
        settings.CodexQuotaWeeklyBaselineAutoRepairEnabled = false;
        settings.CodexRadarServiceProbeToken = 0;
        settings.AiRequestProtectionAutoEnabled = true;
        settings.AiRequestProtectionManualBlockEnabled = false;
        settings.AiChinaEgressGuardEnabled = true;
        settings.CodexQuotaPlanEnabled = false;
        settings.CodexQuotaPlanWeeklyComparison = CodexQuotaPlanComparison.LessThan;
        settings.CodexQuotaPlanWeeklyThresholdPercent = DefaultCodexQuotaPlanWeeklyThresholdPercent;
        settings.CodexQuotaPlanFiveHourComparison = CodexQuotaPlanComparison.LessThan;
        settings.CodexQuotaPlanFiveHourThresholdPercent = DefaultCodexQuotaPlanFiveHourThresholdPercent;
        settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.Both;
        settings.CodexQuotaPlanAutoResumePausedGoals = true;
        settings.CodexQuotaPlanPauseGoalIds = string.Empty;
        settings.CodexQuotaPlanResumeGoalIds = string.Empty;
        settings.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        settings.CodexModelIqTestEnabled = false;
        settings.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        settings.CodexModelIqBaselineAutoEnabled = true;
        settings.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
        settings.CodexModelIqBaselineValidTasks = DefaultCodexModelIqBaselineValidTasks;
        settings.CodexModelIqBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
        settings.CodexModelEfficiencyTestEnabled = false;
        settings.CodexModelTokenEfficiencyTestPercent = DefaultCodexModelEfficiencyPercent;
        settings.CodexModelTimeEfficiencyTestPercent = DefaultCodexModelEfficiencyPercent;
        settings.CodexModelTokenEfficiencyBaselinePassed = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTokenEfficiencyBaselineTokens = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTokenEfficiencyBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
        settings.CodexModelTimeEfficiencyBaselinePassed = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTimeEfficiencyBaselineSeconds = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTimeEfficiencyBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
        settings.CodexModelTokenEfficiencyLowThresholdPercent = DefaultCodexModelEfficiencyLowThresholdPercent;
        settings.CodexModelTimeEfficiencyLowThresholdPercent = DefaultCodexModelEfficiencyLowThresholdPercent;
        settings.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55;
        settings.CodexRadarModelKey = CodexRadarModelCatalog.DefaultModelKey;
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Auto;
        settings.RadarClockAutoSwitchModelEnabled = true;
        settings.RadarClockTimeDisplayMode = RadarClockTimeDisplayMode.Utc;
        settings.CodexRadarSpeedWindowCountdownEnabled = true;
        settings.CodexRadarQuotaResetRainbowEnabled = true;
        settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Automatic;
        settings.DisplayTimeZoneId = TimeZoneInfo.Local.Id;
        settings.PerformanceMode = WidgetPerformanceMode.BatterySaver;
        settings.HoverOpacityEnabled = true;
        settings.ManualHoverOpacityActive = false;
        settings.SensitiveMouseModeEnabled = true;
        settings.SensitiveMouseRangePixels = DefaultSensitiveMouseRangePixels;
        settings.HoverOpacityRevealDelayEnabled = true;
        settings.HoverOpacityRevealDelaySeconds = DefaultHoverOpacityRevealDelaySeconds;
        settings.HoverOpacityRevealResetSeconds = DefaultHoverOpacityRevealResetSeconds;
        settings.HoverOpacityCoverEnabled = true;
        settings.ReverseHoverOpacityRevealEnabled = true;
        settings.ReverseHoverOpacityRestoreDelaySeconds = DefaultReverseHoverOpacityRestoreDelaySeconds;
        settings.AutoHoverOpacityIdleEnabled = false;
        settings.AutoHoverOpacityIdleSeconds = DefaultAutoHoverOpacityIdleSeconds;
        settings.AutoHoverOpacityMaximizedEnabled = false;
        settings.BurnInProtectionEnabled = true;
        settings.BurnInLevelOneIdleSeconds = DefaultBurnInLevelOneIdleSeconds;
        settings.BurnInLevelTwoDelaySeconds = DefaultBurnInLevelTwoDelaySeconds;
        settings.OperationRadialCoreAutoHideKeepAliveEnabled = true;
        settings.OperationRadialIdleCollapseSeconds = DefaultOperationRadialIdleCollapseSeconds;
        settings.OperationRadialIdleResetOnInteractionEnabled = true;
        settings.OperationRadialKeepOpenAfterLeafClickEnabled = true;
        settings.OperationDoubleClickSpecialMenuEnabled = false;
        settings.OperationSettingsLogicExtensionEnabled = false;
        ApplyUserDefaultSnapshot(settings);
        settings.Normalize();
        return settings;
    }

    private static void ApplyUserDefaultSnapshot(WidgetSettings settings)
    {
        // User-confirmed default snapshot captured from settings.ini on 2026-07-06.
        // Runtime refresh/probe tokens stay on their original zero defaults so a fresh profile
        // does not inherit stale manual-refresh counters or force network probe side effects.
        settings.ApplicationTransparencyPercent = 0;
        settings.MainWidgetTransparencyOverridePercent = -1;
        settings.NetworkMonitorTransparencyOverridePercent = -1;
        settings.OperationTransparencyOverridePercent = -1;
        settings.SpecBoardTransparencyOverridePercent = -1;
        settings.CodexTaskBoardTransparencyOverridePercent = -1;
        settings.GuardBoardTransparencyOverridePercent = -1;
        settings.CodexIqBoardTransparencyOverridePercent = -1;
        settings.NightScheduleEnabled = false;
        settings.NightScheduleStartMinutes = DefaultNightScheduleStartMinutes;
        settings.NightScheduleEndMinutes = DefaultNightScheduleEndMinutes;
        settings.NightDimLuminancePercent = DefaultNightDimLuminancePercent;
        settings.NightQuietHoursEnabled = true;
        settings.AlertQuotaEnabled = true;
        settings.AlertResetProtectionEnabled = true;
        settings.AlertServiceHealthEnabled = true;
        settings.AlertCodexTaskEnabled = true;
        settings.HotkeyToggleAllWindows = string.Empty;
        settings.HotkeyToggleHoverOpacity = string.Empty;
        settings.HotkeyOpenSettings = string.Empty;
        settings.MainWidgetScaleOverridePercent = -1;
        settings.NetworkMonitorScaleOverridePercent = -1;
        settings.OperationScaleOverridePercent = -1;
        settings.SpecBoardScaleOverridePercent = -1;
        settings.CodexTaskBoardScaleOverridePercent = -1;
        settings.GuardBoardScaleOverridePercent = -1;
        settings.CodexIqBoardScaleOverridePercent = -1;
        settings.PowerThermalIntegratedEnabled = true;
        settings.PowerThermalManualEnergySaverThresholdPercent = DefaultPowerThermalManualEnergySaverThresholdPercent;
        settings.NetworkMonitorAdapterId = "";
        settings.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        settings.GfwProbeEnabled = true;
        settings.GfwProbeIntervalMinutes = 30;
        settings.CloudEndpointTestSeed = 0;
        settings.CloudStatusRegionMask = 1;
        settings.CloudEndpointTargets = NetworkProbeTargetSettings.CloneArray(NetworkProbeTargetSettings.DefaultCloudEndpointTargets);
        settings.FixedPingTargets = NetworkProbeTargetSettings.CloneArray(NetworkProbeTargetSettings.DefaultFixedPingTargets);
        settings.ConnectionCheckIntervalSeconds = 600;
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        settings.SpecBoardLeftX = -1;
        settings.SpecBoardBottomY = -1;
        settings.SpecBoardAutoHideSeconds = DefaultSpecBoardAutoHideSeconds;
        settings.SpecBoardAutoPopupEnabled = true;
        settings.SpecBoardAutoPopupSeconds = DefaultSpecBoardAutoPopupSeconds;
        settings.SpecBoardLedgerPath = DefaultSpecBoardLedgerPath;
        settings.SpecBoardManagerWidth = 720;
        settings.SpecBoardManagerHeight = 520;
        settings.SpecBoardManagerDangerZoneRequiresTypedConfirm = true;
        settings.LeftDockAutoArrangeEnabled = true;
        settings.LeftDockButtonOrder = CloneLeftDockButtonOrder(DefaultLeftDockButtonOrder);
        settings.LeftDockButtonGapPixels = DefaultLeftDockButtonGapPixels;
        settings.LeftDockGroupOffsetY = DefaultColumnGroupOffsetY;
        settings.SpecBoardLeftDockEnabled = true;
        settings.SpecBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.CodexTaskBoardLeftDockEnabled = true;
        settings.CodexTaskBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.NetworkMonitorLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.GuardBoardLeftDockEnabled = true;
        settings.GuardBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.GuardBoardAutoHideSeconds = DefaultGuardBoardAutoHideSeconds;
        settings.CodexIqBoardLeftDockEnabled = true;
        settings.CodexIqBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
        settings.CodexIqBoardAutoHideSeconds = DefaultCodexIqBoardAutoHideSeconds;
        settings.GuardSleepEnabled = false;
        settings.GuardSleepSinceUtcTicks = 0L;
        settings.GuardDisplayMinutes = DefaultGuardDisplayMinutes;
        settings.GuardOfflineThresholdMinutes = DefaultGuardOfflineThresholdMinutes;
        settings.GuardDisplayUntilUtcTicks = 0L;
        settings.GuardBatteryCarePauseUntilUtcTicks = 0L;
        settings.LeftDockCollapseSeconds = DefaultLeftDockCollapseSeconds;
        settings.LeftDockOutsideClickCollapseEnabled = true;
        settings.CodexTaskBoardWidth = DefaultCodexTaskBoardWidth;
        settings.CodexTaskBoardHeight = DefaultCodexTaskBoardHeight;
        settings.CodexTaskBoardView = CodexTaskBoardView.Table;
        settings.CodexTaskBoardTimelineMinutes = DefaultCodexTaskBoardTimelineMinutes;
        settings.CodexTaskMonitorEnabled = true;
        settings.CodexTaskMonitorActiveWindowMinutes = 30;
        settings.CodexTaskMonitorActiveSeconds = 12;
        settings.CodexTaskMonitorIdleSeconds = 90;
        settings.CodexTaskMonitorTerminalHoldSeconds = 120;
        settings.CodexTaskMonitorErrorHoldSeconds = 30;
        settings.CodexTaskMonitorNumberCooldownSeconds = 120;
        settings.OperationButtonSize = 86;
        settings.OperationLeftOffset = 0;
        settings.OperationBottomOffset = 0;
        settings.OperationBackgroundTransparencyPercent = 0;
        settings.OperationPrimaryPanelMode = OperationPrimaryPanelMode.Auto;
        settings.OperationWindowsButtonEnabled = true;
        settings.OperationMemoryPieEnabled = true;
        settings.ForceShowForegroundFpsEnabled = false;
        settings.SeelenDockForegroundPulseEnabled = true;
        settings.WinDRecoveryPulseEnabled = true;
        settings.CodexPetZOrderProtectionEnabled = true;
        settings.PowerResumeRestartEnabled = true;
        settings.FallbackDisconnectedDisplaysEnabled = true;
        settings.MainDisplayDeviceName = "";
        settings.OperationDisplayDeviceName = "";
        settings.ResolutionCompatibilityModeEnabled = false;
        settings.ResolutionCompatibilityScalePercent = DefaultResolutionCompatibilityScalePercent;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 0;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1800;
        settings.OperationLayoutWorkAreaLeft = 0;
        settings.OperationLayoutWorkAreaTop = 0;
        settings.OperationLayoutWorkAreaWidth = 2880;
        settings.OperationLayoutWorkAreaHeight = 1800;
        settings.VisibilityMode = WidgetVisibilityMode.HideWhenFullscreen;
        settings.VisibilityOverlapIgnoresOperationPanelEnabled = true;
        settings.ClickThroughMode = ClickThroughMode.Auto;
        settings.StartupEnabled = true;
        settings.SensitiveMouseModeEnabled = true;
        settings.SensitiveMouseRangePixels = 200;
        settings.HoverOpacityRevealDelayEnabled = false;
        settings.HoverOpacityRevealDelaySeconds = 1.0;
        settings.HoverOpacityRevealResetSeconds = 0.5;
        settings.HoverOpacityCoverEnabled = true;
        settings.ReverseHoverOpacityRevealEnabled = false;
        settings.ReverseHoverOpacityRestoreDelaySeconds = 5;
        settings.OperationRadialCoreAutoHideKeepAliveEnabled = true;
        settings.OperationRadialIdleCollapseSeconds = DefaultOperationRadialIdleCollapseSeconds;
        settings.OperationRadialIdleResetOnInteractionEnabled = true;
        settings.OperationRadialKeepOpenAfterLeafClickEnabled = true;
        settings.OperationDoubleClickSpecialMenuEnabled = false;
        settings.OperationSettingsLogicExtensionEnabled = false;
        settings.AlertTestEnabled = false;
        settings.ThermalTestMode = ThermalTestMode.Off;
        settings.CodexRadarTestMode = CodexRadarTestMode.Off;
        settings.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        settings.CodexRadarRandomTestEnabled = false;
        settings.CodexRadarRandomTestAutoRefresh = false;
        settings.MainWidgetTileLargeModeEnabled = false;
        settings.MetricTileExpandWidth = DefaultMetricTileExpandWidth;
        settings.MetricTileExpandHeight = DefaultMetricTileExpandHeight;
        settings.MetricTileLeftX = CreateAutoTileArray();
        settings.MetricTileBottomY = CreateAutoTileArray();
        settings.RightTileAutoArrangeEnabled = true;
        settings.RightTileButtonOrder = CloneRightTileButtonOrder(DefaultRightTileButtonOrder);
        settings.RightTileButtonGapPixels = DefaultRightTileButtonGapPixels;
        settings.RightTileGroupOffsetY = DefaultColumnGroupOffsetY;
        settings.OperationRenderVariant = OperationRenderVariant.RadialDial;
        settings.CodexRadarPublicJsonEnabled = true;
        settings.CodexRadarHtmlFallbackEnabled = true;
        settings.CodexRadarRssFallbackEnabled = true;
        settings.CodexQuotaDueResetProtectionEnabled = true;
        settings.CodexQuotaRssResetProtectionEnabled = true;
        settings.CodexQuotaProviderZeroDropProtectionEnabled = true;
        settings.CodexQuotaDuplicateSameBalanceRingProtectionEnabled = true;
        settings.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled = false;
        settings.CodexQuotaProviderWeeklySpikeProtectionEnabled = false;
        settings.CodexQuotaStrictFiveHourResetBoundaryEnabled = false;
        settings.CodexQuotaWeeklyBaselineAutoRepairEnabled = false;
        settings.AiRequestProtectionAutoEnabled = true;
        settings.AiRequestProtectionManualBlockEnabled = false;
        settings.AiChinaEgressGuardEnabled = true;
        settings.CodexQuotaPlanEnabled = false;
        settings.CodexQuotaPlanWeeklyComparison = CodexQuotaPlanComparison.LessThan;
        settings.CodexQuotaPlanWeeklyThresholdPercent = 3;
        settings.CodexQuotaPlanFiveHourComparison = CodexQuotaPlanComparison.LessThan;
        settings.CodexQuotaPlanFiveHourThresholdPercent = 90;
        settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.Both;
        settings.CodexQuotaPlanAutoResumePausedGoals = true;
        settings.CodexQuotaPlanPauseGoalIds = "";
        settings.CodexQuotaPlanResumeGoalIds = "";
        settings.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        settings.CodexModelIqTestEnabled = false;
        settings.CodexModelIqTestPassed = 7;
        settings.CodexModelIqBaselineAutoEnabled = true;
        settings.CodexModelIqBaselinePassed = 7;
        settings.CodexModelIqBaselineValidTasks = DefaultCodexModelIqBaselineValidTasks;
        settings.CodexModelIqBaselineMode = CodexModelBaselineMode.Absolute;
        settings.CodexModelEfficiencyTestEnabled = false;
        settings.CodexModelTokenEfficiencyTestPercent = 100;
        settings.CodexModelTimeEfficiencyTestPercent = 100;
        settings.CodexModelTokenEfficiencyBaselinePassed = 0;
        settings.CodexModelTokenEfficiencyBaselineTokens = 0;
        settings.CodexModelTokenEfficiencyBaselineMode = CodexModelBaselineMode.Absolute;
        settings.CodexModelTimeEfficiencyBaselinePassed = 0;
        settings.CodexModelTimeEfficiencyBaselineSeconds = 0;
        settings.CodexModelTimeEfficiencyBaselineMode = CodexModelBaselineMode.Absolute;
        settings.CodexModelTokenEfficiencyLowThresholdPercent = 80;
        settings.CodexModelTimeEfficiencyLowThresholdPercent = 80;
        settings.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55;
        settings.CodexRadarModelKey = CodexRadarModelCatalog.DefaultModelKey;
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Auto;
        settings.RadarClockAutoSwitchModelEnabled = true;
        settings.RadarClockTimeDisplayMode = RadarClockTimeDisplayMode.Utc;
        settings.CodexRadarSpeedWindowCountdownEnabled = true;
        settings.CodexRadarQuotaResetRainbowEnabled = true;
        settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Automatic;
        settings.DisplayTimeZoneId = "Tokyo Standard Time";
        settings.PerformanceMode = WidgetPerformanceMode.BatterySaver;
        settings.HoverOpacityEnabled = true;
        settings.AutoHoverOpacityIdleEnabled = true;
        settings.AutoHoverOpacityIdleSeconds = 40;
        settings.AutoHoverOpacityMaximizedEnabled = false;
        settings.BurnInProtectionEnabled = true;
        settings.BurnInLevelOneIdleSeconds = DefaultBurnInLevelOneIdleSeconds;
        settings.BurnInLevelTwoDelaySeconds = DefaultBurnInLevelTwoDelaySeconds;
    }

    public WidgetSettings Clone()
    {
        return new WidgetSettings(true)
        {
            ApplicationTransparencyPercent = this.ApplicationTransparencyPercent,
            MainWidgetTransparencyOverridePercent = this.MainWidgetTransparencyOverridePercent,
            NetworkMonitorTransparencyOverridePercent = this.NetworkMonitorTransparencyOverridePercent,
            OperationTransparencyOverridePercent = this.OperationTransparencyOverridePercent,
            SpecBoardTransparencyOverridePercent = this.SpecBoardTransparencyOverridePercent,
            CodexTaskBoardTransparencyOverridePercent = this.CodexTaskBoardTransparencyOverridePercent,
            GuardBoardTransparencyOverridePercent = this.GuardBoardTransparencyOverridePercent,
            CodexIqBoardTransparencyOverridePercent = this.CodexIqBoardTransparencyOverridePercent,
            NightScheduleEnabled = this.NightScheduleEnabled,
            NightScheduleStartMinutes = this.NightScheduleStartMinutes,
            NightScheduleEndMinutes = this.NightScheduleEndMinutes,
            NightDimLuminancePercent = this.NightDimLuminancePercent,
            NightQuietHoursEnabled = this.NightQuietHoursEnabled,
            AlertQuotaEnabled = this.AlertQuotaEnabled,
            AlertResetProtectionEnabled = this.AlertResetProtectionEnabled,
            AlertServiceHealthEnabled = this.AlertServiceHealthEnabled,
            AlertCodexTaskEnabled = this.AlertCodexTaskEnabled,
            HotkeyToggleAllWindows = this.HotkeyToggleAllWindows,
            HotkeyToggleHoverOpacity = this.HotkeyToggleHoverOpacity,
            HotkeyOpenSettings = this.HotkeyOpenSettings,
            MainWidgetScaleOverridePercent = this.MainWidgetScaleOverridePercent,
            NetworkMonitorScaleOverridePercent = this.NetworkMonitorScaleOverridePercent,
            OperationScaleOverridePercent = this.OperationScaleOverridePercent,
            SpecBoardScaleOverridePercent = this.SpecBoardScaleOverridePercent,
            CodexTaskBoardScaleOverridePercent = this.CodexTaskBoardScaleOverridePercent,
            GuardBoardScaleOverridePercent = this.GuardBoardScaleOverridePercent,
            CodexIqBoardScaleOverridePercent = this.CodexIqBoardScaleOverridePercent,
            PowerThermalIntegratedEnabled = this.PowerThermalIntegratedEnabled,
            PowerThermalManualEnergySaverThresholdPercent = this.PowerThermalManualEnergySaverThresholdPercent,
            NetworkMonitorAdapterId = this.NetworkMonitorAdapterId,
            NetworkStatusTestMode = this.NetworkStatusTestMode,
            GfwProbeEnabled = this.GfwProbeEnabled,
            GfwProbeIntervalMinutes = this.GfwProbeIntervalMinutes,
            GfwProbeManualRefreshToken = this.GfwProbeManualRefreshToken,
            CloudEndpointTestSeed = this.CloudEndpointTestSeed,
            CloudStatusRegionMask = this.CloudStatusRegionMask,
            CloudEndpointTargets = NetworkProbeTargetSettings.CloneArray(this.CloudEndpointTargets),
            FixedPingTargets = NetworkProbeTargetSettings.CloneArray(this.FixedPingTargets),
            ConnectionCheckIntervalSeconds = this.ConnectionCheckIntervalSeconds,
            ConnectionCheckManualRefreshToken = this.ConnectionCheckManualRefreshToken,
            SpecBoardWidth = this.SpecBoardWidth,
            SpecBoardHeight = this.SpecBoardHeight,
            SpecBoardLeftX = this.SpecBoardLeftX,
            SpecBoardBottomY = this.SpecBoardBottomY,
            SpecBoardAutoHideSeconds = this.SpecBoardAutoHideSeconds,
            SpecBoardAutoPopupEnabled = this.SpecBoardAutoPopupEnabled,
            SpecBoardAutoPopupSeconds = this.SpecBoardAutoPopupSeconds,
            SpecBoardLedgerPath = this.SpecBoardLedgerPath,
            SpecBoardManagerWidth = this.SpecBoardManagerWidth,
            SpecBoardManagerHeight = this.SpecBoardManagerHeight,
            SpecBoardManagerDangerZoneRequiresTypedConfirm = this.SpecBoardManagerDangerZoneRequiresTypedConfirm,
            LeftDockAutoArrangeEnabled = this.LeftDockAutoArrangeEnabled,
            LeftDockButtonOrder = CloneLeftDockButtonOrder(this.LeftDockButtonOrder),
            LeftDockButtonGapPixels = this.LeftDockButtonGapPixels,
            LeftDockGroupOffsetY = this.LeftDockGroupOffsetY,
            SpecBoardLeftDockEnabled = this.SpecBoardLeftDockEnabled,
            SpecBoardLeftDockTabCenterY = this.SpecBoardLeftDockTabCenterY,
            CodexTaskBoardLeftDockEnabled = this.CodexTaskBoardLeftDockEnabled,
            CodexTaskBoardLeftDockTabCenterY = this.CodexTaskBoardLeftDockTabCenterY,
            NetworkMonitorLeftDockTabCenterY = this.NetworkMonitorLeftDockTabCenterY,
            GuardBoardLeftDockEnabled = this.GuardBoardLeftDockEnabled,
            GuardBoardLeftDockTabCenterY = this.GuardBoardLeftDockTabCenterY,
            GuardBoardAutoHideSeconds = this.GuardBoardAutoHideSeconds,
            CodexIqBoardLeftDockEnabled = this.CodexIqBoardLeftDockEnabled,
            CodexIqBoardLeftDockTabCenterY = this.CodexIqBoardLeftDockTabCenterY,
            CodexIqBoardAutoHideSeconds = this.CodexIqBoardAutoHideSeconds,
            GuardSleepEnabled = this.GuardSleepEnabled,
            GuardSleepSinceUtcTicks = this.GuardSleepSinceUtcTicks,
            GuardDisplayMinutes = this.GuardDisplayMinutes,
            GuardOfflineThresholdMinutes = this.GuardOfflineThresholdMinutes,
            GuardDisplayUntilUtcTicks = this.GuardDisplayUntilUtcTicks,
            GuardBatteryCarePauseUntilUtcTicks = this.GuardBatteryCarePauseUntilUtcTicks,
            LeftDockCollapseSeconds = this.LeftDockCollapseSeconds,
            LeftDockOutsideClickCollapseEnabled = this.LeftDockOutsideClickCollapseEnabled,
            CodexTaskBoardWidth = this.CodexTaskBoardWidth,
            CodexTaskBoardHeight = this.CodexTaskBoardHeight,
            CodexTaskBoardView = this.CodexTaskBoardView,
            CodexTaskBoardTimelineMinutes = this.CodexTaskBoardTimelineMinutes,
            CodexTaskMonitorEnabled = this.CodexTaskMonitorEnabled,
            CodexTaskMonitorActiveWindowMinutes = this.CodexTaskMonitorActiveWindowMinutes,
            CodexTaskMonitorActiveSeconds = this.CodexTaskMonitorActiveSeconds,
            CodexTaskMonitorIdleSeconds = this.CodexTaskMonitorIdleSeconds,
            CodexTaskMonitorTerminalHoldSeconds = this.CodexTaskMonitorTerminalHoldSeconds,
            CodexTaskMonitorErrorHoldSeconds = this.CodexTaskMonitorErrorHoldSeconds,
            CodexTaskMonitorNumberCooldownSeconds = this.CodexTaskMonitorNumberCooldownSeconds,
            OperationButtonSize = this.OperationButtonSize,
            OperationLeftOffset = this.OperationLeftOffset,
            OperationBottomOffset = this.OperationBottomOffset,
            OperationBackgroundTransparencyPercent = this.OperationBackgroundTransparencyPercent,
            OperationPrimaryPanelMode = this.OperationPrimaryPanelMode,
            OperationWindowsButtonEnabled = this.OperationWindowsButtonEnabled,
            OperationMemoryPieEnabled = this.OperationMemoryPieEnabled,
            ForceShowForegroundFpsEnabled = this.ForceShowForegroundFpsEnabled,
            SeelenDockForegroundPulseEnabled = this.SeelenDockForegroundPulseEnabled,
            WinDRecoveryPulseEnabled = this.WinDRecoveryPulseEnabled,
            CodexPetZOrderProtectionEnabled = this.CodexPetZOrderProtectionEnabled,
            PowerResumeRestartEnabled = this.PowerResumeRestartEnabled,
            FallbackDisconnectedDisplaysEnabled = this.FallbackDisconnectedDisplaysEnabled,
            MainDisplayDeviceName = this.MainDisplayDeviceName,
            OperationDisplayDeviceName = this.OperationDisplayDeviceName,
            ResolutionCompatibilityModeEnabled = this.ResolutionCompatibilityModeEnabled,
            ResolutionCompatibilityScalePercent = this.ResolutionCompatibilityScalePercent,
            LayoutWorkAreaLeft = this.LayoutWorkAreaLeft,
            LayoutWorkAreaTop = this.LayoutWorkAreaTop,
            LayoutWorkAreaWidth = this.LayoutWorkAreaWidth,
            LayoutWorkAreaHeight = this.LayoutWorkAreaHeight,
            OperationLayoutWorkAreaLeft = this.OperationLayoutWorkAreaLeft,
            OperationLayoutWorkAreaTop = this.OperationLayoutWorkAreaTop,
            OperationLayoutWorkAreaWidth = this.OperationLayoutWorkAreaWidth,
            OperationLayoutWorkAreaHeight = this.OperationLayoutWorkAreaHeight,
            VisibilityMode = this.VisibilityMode,
            VisibilityOverlapIgnoresOperationPanelEnabled = this.VisibilityOverlapIgnoresOperationPanelEnabled,
            ClickThroughMode = this.ClickThroughMode,
            StartupEnabled = this.StartupEnabled,
            AlertTestEnabled = this.AlertTestEnabled,
            ThermalTestMode = this.ThermalTestMode,
            CodexRadarTestMode = this.CodexRadarTestMode,
            ServiceHealthTestMode = this.ServiceHealthTestMode,
            CodexRadarRandomTestEnabled = this.CodexRadarRandomTestEnabled,
            CodexRadarRandomTestAutoRefresh = this.CodexRadarRandomTestAutoRefresh,
            CodexRadarRandomTestRefreshToken = this.CodexRadarRandomTestRefreshToken,
            MainWidgetTileLargeModeEnabled = this.MainWidgetTileLargeModeEnabled,
            MetricTileExpandWidth = this.MetricTileExpandWidth,
            MetricTileExpandHeight = this.MetricTileExpandHeight,
            OperationRenderVariant = this.OperationRenderVariant,
            CodexRadarPublicJsonEnabled = this.CodexRadarPublicJsonEnabled,
            CodexRadarHtmlFallbackEnabled = this.CodexRadarHtmlFallbackEnabled,
            CodexRadarRssFallbackEnabled = this.CodexRadarRssFallbackEnabled,
            CodexQuotaDueResetProtectionEnabled = this.CodexQuotaDueResetProtectionEnabled,
            CodexQuotaRssResetProtectionEnabled = this.CodexQuotaRssResetProtectionEnabled,
            CodexQuotaProviderZeroDropProtectionEnabled = this.CodexQuotaProviderZeroDropProtectionEnabled,
            CodexQuotaDuplicateSameBalanceRingProtectionEnabled = this.CodexQuotaDuplicateSameBalanceRingProtectionEnabled,
            CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled = this.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled,
            CodexQuotaProviderWeeklySpikeProtectionEnabled = this.CodexQuotaProviderWeeklySpikeProtectionEnabled,
            CodexQuotaStrictFiveHourResetBoundaryEnabled = this.CodexQuotaStrictFiveHourResetBoundaryEnabled,
            CodexQuotaWeeklyBaselineAutoRepairEnabled = this.CodexQuotaWeeklyBaselineAutoRepairEnabled,
            CodexRadarServiceProbeToken = this.CodexRadarServiceProbeToken,
            AiRequestProtectionAutoEnabled = this.AiRequestProtectionAutoEnabled,
            AiRequestProtectionManualBlockEnabled = this.AiRequestProtectionManualBlockEnabled,
            AiChinaEgressGuardEnabled = this.AiChinaEgressGuardEnabled,
            CodexQuotaPlanEnabled = this.CodexQuotaPlanEnabled,
            CodexQuotaPlanWeeklyComparison = this.CodexQuotaPlanWeeklyComparison,
            CodexQuotaPlanWeeklyThresholdPercent = this.CodexQuotaPlanWeeklyThresholdPercent,
            CodexQuotaPlanFiveHourComparison = this.CodexQuotaPlanFiveHourComparison,
            CodexQuotaPlanFiveHourThresholdPercent = this.CodexQuotaPlanFiveHourThresholdPercent,
            CodexQuotaPlanResumeConditionMode = this.CodexQuotaPlanResumeConditionMode,
            CodexQuotaPlanAutoResumePausedGoals = this.CodexQuotaPlanAutoResumePausedGoals,
            CodexQuotaPlanPauseGoalIds = this.CodexQuotaPlanPauseGoalIds,
            CodexQuotaPlanResumeGoalIds = this.CodexQuotaPlanResumeGoalIds,
            CleanIpBadgeTestMode = this.CleanIpBadgeTestMode,
            CodexModelIqTestEnabled = this.CodexModelIqTestEnabled,
            CodexModelIqTestPassed = this.CodexModelIqTestPassed,
            CodexModelIqBaselineAutoEnabled = this.CodexModelIqBaselineAutoEnabled,
            CodexModelIqBaselinePassed = this.CodexModelIqBaselinePassed,
            CodexModelIqBaselineValidTasks = this.CodexModelIqBaselineValidTasks,
            CodexModelIqBaselineMode = this.CodexModelIqBaselineMode,
            CodexModelEfficiencyTestEnabled = this.CodexModelEfficiencyTestEnabled,
            CodexModelTokenEfficiencyTestPercent = this.CodexModelTokenEfficiencyTestPercent,
            CodexModelTimeEfficiencyTestPercent = this.CodexModelTimeEfficiencyTestPercent,
            CodexModelTokenEfficiencyBaselinePassed = this.CodexModelTokenEfficiencyBaselinePassed,
            CodexModelTokenEfficiencyBaselineTokens = this.CodexModelTokenEfficiencyBaselineTokens,
            CodexModelTokenEfficiencyBaselineMode = this.CodexModelTokenEfficiencyBaselineMode,
            CodexModelTimeEfficiencyBaselinePassed = this.CodexModelTimeEfficiencyBaselinePassed,
            CodexModelTimeEfficiencyBaselineSeconds = this.CodexModelTimeEfficiencyBaselineSeconds,
            CodexModelTimeEfficiencyBaselineMode = this.CodexModelTimeEfficiencyBaselineMode,
            CodexModelTokenEfficiencyLowThresholdPercent = this.CodexModelTokenEfficiencyLowThresholdPercent,
            CodexModelTimeEfficiencyLowThresholdPercent = this.CodexModelTimeEfficiencyLowThresholdPercent,
            CodexRadarModelVersion = this.CodexRadarModelVersion,
            CodexRadarModelKey = this.CodexRadarModelKey,
            CodexRadarSoftwareMode = this.CodexRadarSoftwareMode,
            RadarClockAutoSwitchModelEnabled = this.RadarClockAutoSwitchModelEnabled,
            RadarClockTimeDisplayMode = this.RadarClockTimeDisplayMode,
            CodexRadarSpeedWindowCountdownEnabled = this.CodexRadarSpeedWindowCountdownEnabled,
            CodexRadarQuotaResetRainbowEnabled = this.CodexRadarQuotaResetRainbowEnabled,
            DisplayTimeZoneMode = this.DisplayTimeZoneMode,
            DisplayTimeZoneId = this.DisplayTimeZoneId,
            PerformanceMode = this.PerformanceMode,
            HoverOpacityEnabled = this.HoverOpacityEnabled,
            ForceHoverOpacityActive = this.ForceHoverOpacityActive,
            ManualHoverOpacityActive = this.ManualHoverOpacityActive,
            SensitiveMouseModeEnabled = this.SensitiveMouseModeEnabled,
            SensitiveMouseRangePixels = this.SensitiveMouseRangePixels,
            HoverOpacityRevealDelayEnabled = this.HoverOpacityRevealDelayEnabled,
            HoverOpacityRevealDelaySeconds = this.HoverOpacityRevealDelaySeconds,
            HoverOpacityRevealResetSeconds = this.HoverOpacityRevealResetSeconds,
            HoverOpacityCoverEnabled = this.HoverOpacityCoverEnabled,
            ReverseHoverOpacityRevealEnabled = this.ReverseHoverOpacityRevealEnabled,
            ReverseHoverOpacityRestoreDelaySeconds = this.ReverseHoverOpacityRestoreDelaySeconds,
            AutoHoverOpacityIdleEnabled = this.AutoHoverOpacityIdleEnabled,
            AutoHoverOpacityIdleSeconds = this.AutoHoverOpacityIdleSeconds,
            AutoHoverOpacityMaximizedEnabled = this.AutoHoverOpacityMaximizedEnabled,
            BurnInProtectionEnabled = this.BurnInProtectionEnabled,
            BurnInLevelOneIdleSeconds = this.BurnInLevelOneIdleSeconds,
            BurnInLevelTwoDelaySeconds = this.BurnInLevelTwoDelaySeconds,
            OperationRadialCoreAutoHideKeepAliveEnabled = this.OperationRadialCoreAutoHideKeepAliveEnabled,
            OperationRadialIdleCollapseSeconds = this.OperationRadialIdleCollapseSeconds,
            OperationRadialIdleResetOnInteractionEnabled = this.OperationRadialIdleResetOnInteractionEnabled,
            OperationRadialKeepOpenAfterLeafClickEnabled = this.OperationRadialKeepOpenAfterLeafClickEnabled,
            OperationDoubleClickSpecialMenuEnabled = this.OperationDoubleClickSpecialMenuEnabled,
            OperationSettingsLogicExtensionEnabled = this.OperationSettingsLogicExtensionEnabled,
            MetricTileLeftX = CloneTileArray(this.MetricTileLeftX),
            MetricTileBottomY = CloneTileArray(this.MetricTileBottomY),
            RightTileAutoArrangeEnabled = this.RightTileAutoArrangeEnabled,
            RightTileButtonOrder = CloneRightTileButtonOrder(this.RightTileButtonOrder),
            RightTileButtonGapPixels = this.RightTileButtonGapPixels,
            RightTileGroupOffsetY = this.RightTileGroupOffsetY
        };
    }

    public void Normalize()
    {
        this.ApplicationTransparencyPercent = Clamp(this.ApplicationTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.MainWidgetTransparencyOverridePercent = Clamp(this.MainWidgetTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.NetworkMonitorTransparencyOverridePercent = Clamp(this.NetworkMonitorTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.OperationTransparencyOverridePercent = Clamp(this.OperationTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.SpecBoardTransparencyOverridePercent = Clamp(this.SpecBoardTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.CodexTaskBoardTransparencyOverridePercent = Clamp(this.CodexTaskBoardTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.GuardBoardTransparencyOverridePercent = Clamp(this.GuardBoardTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.CodexIqBoardTransparencyOverridePercent = Clamp(this.CodexIqBoardTransparencyOverridePercent, MinWindowTransparencyOverridePercent, MaxWindowTransparencyOverridePercent);
        this.NightScheduleStartMinutes = Clamp(this.NightScheduleStartMinutes, MinNightScheduleMinutes, MaxNightScheduleMinutes);
        this.NightScheduleEndMinutes = Clamp(this.NightScheduleEndMinutes, MinNightScheduleMinutes, MaxNightScheduleMinutes);
        this.NightDimLuminancePercent = Clamp(this.NightDimLuminancePercent, MinNightDimLuminancePercent, MaxNightDimLuminancePercent);
        this.HotkeyToggleAllWindows = GlobalHotkeyParser.Normalize(this.HotkeyToggleAllWindows);
        this.HotkeyToggleHoverOpacity = GlobalHotkeyParser.Normalize(this.HotkeyToggleHoverOpacity);
        this.HotkeyOpenSettings = GlobalHotkeyParser.Normalize(this.HotkeyOpenSettings);
        this.MainWidgetScaleOverridePercent = NormalizeWindowScaleOverride(this.MainWidgetScaleOverridePercent);
        this.NetworkMonitorScaleOverridePercent = NormalizeWindowScaleOverride(this.NetworkMonitorScaleOverridePercent);
        this.OperationScaleOverridePercent = NormalizeWindowScaleOverride(this.OperationScaleOverridePercent);
        this.SpecBoardScaleOverridePercent = NormalizeWindowScaleOverride(this.SpecBoardScaleOverridePercent);
        this.CodexTaskBoardScaleOverridePercent = NormalizeWindowScaleOverride(this.CodexTaskBoardScaleOverridePercent);
        this.GuardBoardScaleOverridePercent = NormalizeWindowScaleOverride(this.GuardBoardScaleOverridePercent);
        this.CodexIqBoardScaleOverridePercent = NormalizeWindowScaleOverride(this.CodexIqBoardScaleOverridePercent);
        this.MetricTileExpandWidth = Clamp(this.MetricTileExpandWidth, MinMetricTileExpandWidth, MaxMetricTileExpandWidth);
        this.MetricTileExpandHeight = Clamp(this.MetricTileExpandHeight, MinMetricTileExpandHeight, MaxMetricTileExpandHeight);
        this.PowerThermalManualEnergySaverThresholdPercent = Clamp(
            this.PowerThermalManualEnergySaverThresholdPercent,
            MinPowerThermalManualEnergySaverThresholdPercent,
            MaxPowerThermalManualEnergySaverThresholdPercent);
        this.NetworkMonitorAdapterId = (this.NetworkMonitorAdapterId ?? string.Empty).Trim();
        this.GfwProbeIntervalMinutes = Clamp(this.GfwProbeIntervalMinutes, MinGfwProbeIntervalMinutes, MaxGfwProbeIntervalMinutes);
        this.CloudStatusRegionMask &= CloudStatusRegionMaskAll;
        if (this.CloudStatusRegionMask == 0)
        {
            this.CloudStatusRegionMask = DefaultCloudStatusRegionMask;
        }

        this.CloudEndpointTargets = NetworkProbeTargetSettings.NormalizeCloudTargets(this.CloudEndpointTargets);
        this.FixedPingTargets = NetworkProbeTargetSettings.NormalizeFixedPingTargets(this.FixedPingTargets);

        this.ConnectionCheckIntervalSeconds = Clamp(this.ConnectionCheckIntervalSeconds, MinConnectionCheckIntervalSeconds, MaxConnectionCheckIntervalSeconds);
        this.SpecBoardWidth = Clamp(this.SpecBoardWidth, MinSpecBoardWidth, MaxSpecBoardWidth);
        this.SpecBoardHeight = Clamp(this.SpecBoardHeight, MinSpecBoardHeight, MaxSpecBoardHeight);
        this.SpecBoardLeftX = NormalizeSpecBoardAnchor(this.SpecBoardLeftX);
        this.SpecBoardBottomY = NormalizeSpecBoardAnchor(this.SpecBoardBottomY);
        this.SpecBoardAutoHideSeconds = Clamp(this.SpecBoardAutoHideSeconds, MinSpecBoardAutoHideSeconds, MaxSpecBoardAutoHideSeconds);
        this.SpecBoardAutoPopupSeconds = Clamp(this.SpecBoardAutoPopupSeconds, MinSpecBoardAutoPopupSeconds, MaxSpecBoardAutoPopupSeconds);
        this.SpecBoardLedgerPath = NormalizeSpecBoardLedgerPath(this.SpecBoardLedgerPath);
        this.SpecBoardManagerWidth = Clamp(this.SpecBoardManagerWidth, MinSpecBoardManagerWidth, MaxSpecBoardManagerWidth);
        this.SpecBoardManagerHeight = Clamp(this.SpecBoardManagerHeight, MinSpecBoardManagerHeight, MaxSpecBoardManagerHeight);
        this.LeftDockButtonOrder = NormalizeLeftDockButtonOrder(this.LeftDockButtonOrder);
        this.LeftDockButtonGapPixels = Clamp(this.LeftDockButtonGapPixels, MinColumnButtonGapPixels, MaxColumnButtonGapPixels);
        this.LeftDockGroupOffsetY = Clamp(this.LeftDockGroupOffsetY, MinColumnGroupOffsetY, MaxColumnGroupOffsetY);
        // The visible topology is fixed at five left-edge tabs. These persisted flags remain only
        // so older settings files round-trip safely; false values must never resurrect undocked
        // board windows or make the layout editor disagree with the runtime surface set.
        this.SpecBoardLeftDockEnabled = true;
        this.CodexTaskBoardLeftDockEnabled = true;
        this.GuardBoardLeftDockEnabled = true;
        this.CodexIqBoardLeftDockEnabled = true;
        // Dock tab centers are screen coordinates; anything below zero other than the auto sentinel
        // is meaningless, and the windows clamp the resolved value into the work area anyway.
        this.SpecBoardLeftDockTabCenterY = NormalizeLeftDockTabCenterY(this.SpecBoardLeftDockTabCenterY);
        this.CodexTaskBoardLeftDockTabCenterY = NormalizeLeftDockTabCenterY(this.CodexTaskBoardLeftDockTabCenterY);
        this.NetworkMonitorLeftDockTabCenterY = NormalizeLeftDockTabCenterY(this.NetworkMonitorLeftDockTabCenterY);
        this.GuardBoardLeftDockTabCenterY = NormalizeLeftDockTabCenterY(this.GuardBoardLeftDockTabCenterY);
        this.GuardBoardAutoHideSeconds = Clamp(this.GuardBoardAutoHideSeconds, MinGuardBoardAutoHideSeconds, MaxGuardBoardAutoHideSeconds);
        this.CodexIqBoardLeftDockTabCenterY = NormalizeLeftDockTabCenterY(this.CodexIqBoardLeftDockTabCenterY);
        this.CodexIqBoardAutoHideSeconds = Clamp(this.CodexIqBoardAutoHideSeconds, MinCodexIqBoardAutoHideSeconds, MaxCodexIqBoardAutoHideSeconds);
        this.GuardDisplayMinutes = NormalizeGuardDisplayMinutes(this.GuardDisplayMinutes);
        this.GuardOfflineThresholdMinutes = NormalizeGuardOfflineThresholdMinutes(this.GuardOfflineThresholdMinutes);
        this.GuardSleepSinceUtcTicks = NormalizeUtcTicks(this.GuardSleepSinceUtcTicks);
        this.GuardDisplayUntilUtcTicks = NormalizeUtcTicks(this.GuardDisplayUntilUtcTicks);
        this.GuardBatteryCarePauseUntilUtcTicks = NormalizeUtcTicks(this.GuardBatteryCarePauseUntilUtcTicks);
        this.LeftDockCollapseSeconds = Clamp(this.LeftDockCollapseSeconds, MinLeftDockCollapseSeconds, MaxLeftDockCollapseSeconds);
        this.CodexTaskBoardWidth = Clamp(this.CodexTaskBoardWidth, MinCodexTaskBoardWidth, MaxCodexTaskBoardWidth);
        this.CodexTaskBoardHeight = Clamp(this.CodexTaskBoardHeight, MinCodexTaskBoardHeight, MaxCodexTaskBoardHeight);
        this.CodexTaskBoardTimelineMinutes = Clamp(this.CodexTaskBoardTimelineMinutes, MinCodexTaskBoardTimelineMinutes, MaxCodexTaskBoardTimelineMinutes);
        if (!Enum.IsDefined(typeof(CodexTaskBoardView), this.CodexTaskBoardView))
        {
            this.CodexTaskBoardView = CodexTaskBoardView.Table;
        }
        this.CodexTaskMonitorActiveWindowMinutes = Clamp(this.CodexTaskMonitorActiveWindowMinutes, MinCodexTaskMonitorActiveWindowMinutes, MaxCodexTaskMonitorActiveWindowMinutes);
        this.CodexTaskMonitorActiveSeconds = Clamp(this.CodexTaskMonitorActiveSeconds, MinCodexTaskMonitorActiveSeconds, MaxCodexTaskMonitorActiveSeconds);
        this.CodexTaskMonitorIdleSeconds = Clamp(this.CodexTaskMonitorIdleSeconds, MinCodexTaskMonitorIdleSeconds, MaxCodexTaskMonitorIdleSeconds);
        this.CodexTaskMonitorTerminalHoldSeconds = Clamp(this.CodexTaskMonitorTerminalHoldSeconds, MinCodexTaskMonitorTerminalHoldSeconds, MaxCodexTaskMonitorTerminalHoldSeconds);
        this.CodexTaskMonitorErrorHoldSeconds = Clamp(this.CodexTaskMonitorErrorHoldSeconds, MinCodexTaskMonitorErrorHoldSeconds, MaxCodexTaskMonitorErrorHoldSeconds);
        this.CodexTaskMonitorNumberCooldownSeconds = Clamp(this.CodexTaskMonitorNumberCooldownSeconds, MinCodexTaskMonitorNumberCooldownSeconds, MaxCodexTaskMonitorNumberCooldownSeconds);
        this.OperationButtonSize = Clamp(this.OperationButtonSize, MinOperationButtonSize, MaxOperationButtonSize);
        this.OperationBackgroundTransparencyPercent = Clamp(this.OperationBackgroundTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ResolutionCompatibilityScalePercent = Clamp(
            this.ResolutionCompatibilityScalePercent,
            MinResolutionCompatibilityScalePercent,
            MaxResolutionCompatibilityScalePercent);
        if (!Enum.IsDefined(typeof(OperationPrimaryPanelMode), this.OperationPrimaryPanelMode))
        {
            this.OperationPrimaryPanelMode = OperationPrimaryPanelMode.Auto;
        }
        if (!Enum.IsDefined(typeof(OperationRenderVariant), this.OperationRenderVariant))
        {
            this.OperationRenderVariant = OperationRenderVariant.RadialDial;
        }

        this.SensitiveMouseRangePixels = Clamp(
            this.SensitiveMouseRangePixels,
            MinSensitiveMouseRangePixels,
            MaxSensitiveMouseRangePixels);
        this.HoverOpacityRevealDelaySeconds = Clamp(
            this.HoverOpacityRevealDelaySeconds,
            MinHoverOpacityRevealDelaySeconds,
            MaxHoverOpacityRevealDelaySeconds);
        this.HoverOpacityRevealResetSeconds = Clamp(
            this.HoverOpacityRevealResetSeconds,
            MinHoverOpacityRevealResetSeconds,
            MaxHoverOpacityRevealResetSeconds);
        this.ReverseHoverOpacityRestoreDelaySeconds = Clamp(
            this.ReverseHoverOpacityRestoreDelaySeconds,
            MinReverseHoverOpacityRestoreDelaySeconds,
            MaxReverseHoverOpacityRestoreDelaySeconds);
        this.AutoHoverOpacityIdleSeconds = Clamp(
            this.AutoHoverOpacityIdleSeconds,
            MinAutoHoverOpacityIdleSeconds,
            MaxAutoHoverOpacityIdleSeconds);
        this.BurnInLevelOneIdleSeconds = Clamp(
            this.BurnInLevelOneIdleSeconds,
            MinBurnInLevelOneIdleSeconds,
            MaxBurnInLevelOneIdleSeconds);
        this.BurnInLevelTwoDelaySeconds = Clamp(
            this.BurnInLevelTwoDelaySeconds,
            MinBurnInLevelTwoDelaySeconds,
            MaxBurnInLevelTwoDelaySeconds);
        this.OperationRadialIdleCollapseSeconds = this.OperationRadialIdleCollapseSeconds <= NeverOperationRadialIdleCollapseSeconds
            ? NeverOperationRadialIdleCollapseSeconds
            : Clamp(
                this.OperationRadialIdleCollapseSeconds,
                MinOperationRadialIdleCollapseSeconds,
                MaxOperationRadialIdleCollapseSeconds);
        this.CodexModelIqTestPassed = Clamp(this.CodexModelIqTestPassed, MinCodexModelIqPassed, MaxCodexModelIqPassed);
        this.CodexModelIqBaselinePassed = Clamp(this.CodexModelIqBaselinePassed, MinCodexModelIqPassed, MaxCodexModelIqPassed);
        this.CodexModelIqBaselineValidTasks = Clamp(
            this.CodexModelIqBaselineValidTasks <= 0
                ? DefaultCodexModelIqBaselineValidTasks
                : this.CodexModelIqBaselineValidTasks,
            MinCodexModelIqValidTasks,
            MaxCodexModelIqValidTasks);
        if (this.CodexModelIqBaselinePassed > this.CodexModelIqBaselineValidTasks)
        {
            this.CodexModelIqBaselinePassed = this.CodexModelIqBaselineValidTasks;
        }
        this.CodexModelTokenEfficiencyTestPercent = Clamp(
            this.CodexModelTokenEfficiencyTestPercent,
            MinCodexModelEfficiencyPercent,
            MaxCodexModelEfficiencyPercent);
        this.CodexModelTimeEfficiencyTestPercent = Clamp(
            this.CodexModelTimeEfficiencyTestPercent,
            MinCodexModelEfficiencyPercent,
            MaxCodexModelEfficiencyPercent);
        this.CodexModelTokenEfficiencyBaselinePassed = Clamp(
            this.CodexModelTokenEfficiencyBaselinePassed,
            MinCodexModelIqPassed,
            MaxCodexModelIqPassed);
        this.CodexModelTokenEfficiencyBaselineTokens = Clamp(
            this.CodexModelTokenEfficiencyBaselineTokens,
            MinCodexModelEfficiencyBaselineValue,
            MaxCodexModelEfficiencyBaselineValue);
        this.CodexModelTimeEfficiencyBaselinePassed = Clamp(
            this.CodexModelTimeEfficiencyBaselinePassed,
            MinCodexModelIqPassed,
            MaxCodexModelIqPassed);
        this.CodexModelTimeEfficiencyBaselineSeconds = Clamp(
            this.CodexModelTimeEfficiencyBaselineSeconds,
            MinCodexModelEfficiencyBaselineValue,
            MaxCodexModelEfficiencyBaselineValue);
        this.CodexModelTokenEfficiencyLowThresholdPercent = Clamp(
            this.CodexModelTokenEfficiencyLowThresholdPercent,
            MinCodexModelEfficiencyLowThresholdPercent,
            MaxCodexModelEfficiencyLowThresholdPercent);
        this.CodexModelTimeEfficiencyLowThresholdPercent = Clamp(
            this.CodexModelTimeEfficiencyLowThresholdPercent,
            MinCodexModelEfficiencyLowThresholdPercent,
            MaxCodexModelEfficiencyLowThresholdPercent);
        if (!Enum.IsDefined(typeof(CodexModelBaselineMode), this.CodexModelIqBaselineMode))
        {
            this.CodexModelIqBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
        }

        if (!Enum.IsDefined(typeof(CodexModelBaselineMode), this.CodexModelTokenEfficiencyBaselineMode))
        {
            this.CodexModelTokenEfficiencyBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
        }

        if (!Enum.IsDefined(typeof(CodexModelBaselineMode), this.CodexModelTimeEfficiencyBaselineMode))
        {
            this.CodexModelTimeEfficiencyBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
        }

        if (!Enum.IsDefined(typeof(CodexRadarModelVersion), this.CodexRadarModelVersion))
        {
            this.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55;
        }

        this.CodexRadarModelKey = CodexRadarModelCatalog.NormalizeModelKey(this.CodexRadarModelKey);
        if (string.IsNullOrEmpty(this.CodexRadarModelKey))
        {
            this.CodexRadarModelKey = CodexRadarModelCatalog.LegacyKeyFromVersion(this.CodexRadarModelVersion);
        }

        this.CodexRadarModelVersion =
            CodexRadarModelCatalog.LegacyVersionFromKey(this.CodexRadarModelKey);

        if (!Enum.IsDefined(typeof(CodexRadarSoftwareMode), this.CodexRadarSoftwareMode))
        {
            this.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Auto;
        }

        if (!Enum.IsDefined(typeof(RadarClockTimeDisplayMode), this.RadarClockTimeDisplayMode))
        {
            this.RadarClockTimeDisplayMode = RadarClockTimeDisplayMode.Utc;
        }

        if (!Enum.IsDefined(typeof(DisplayTimeZoneMode), this.DisplayTimeZoneMode))
        {
            this.DisplayTimeZoneMode = DisplayTimeZoneMode.Automatic;
        }

        this.DisplayTimeZoneId = (this.DisplayTimeZoneId ?? string.Empty).Trim();
        if (this.DisplayTimeZoneMode == DisplayTimeZoneMode.Manual)
        {
            this.DisplayTimeZoneId =
                TimeZoneUtilities.ResolveTimeZone(this.DisplayTimeZoneId, TimeZoneInfo.Local).Id;
        }
        else if (this.DisplayTimeZoneId.Length == 0)
        {
            this.DisplayTimeZoneId = TimeZoneInfo.Local.Id;
        }

        if (!Enum.IsDefined(typeof(WidgetPerformanceMode), this.PerformanceMode))
        {
            this.PerformanceMode = WidgetPerformanceMode.Balanced;
        }

        if (!Enum.IsDefined(typeof(ClickThroughMode), this.ClickThroughMode))
        {
            this.ClickThroughMode = ClickThroughMode.Auto;
        }

        if (!Enum.IsDefined(typeof(WidgetVisibilityMode), this.VisibilityMode))
        {
            this.VisibilityMode = WidgetVisibilityMode.HideWhenFullscreen;
        }

        // The legacy per-module website tests no longer have UI controls.
        this.CodexRadarTestMode = CodexRadarTestMode.Off;
        this.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        this.CodexRadarServiceProbeToken = Clamp(this.CodexRadarServiceProbeToken, 0, int.MaxValue);
        if (!Enum.IsDefined(typeof(CodexQuotaPlanComparison), this.CodexQuotaPlanWeeklyComparison))
        {
            this.CodexQuotaPlanWeeklyComparison = CodexQuotaPlanComparison.LessThan;
        }

        if (!Enum.IsDefined(typeof(CodexQuotaPlanComparison), this.CodexQuotaPlanFiveHourComparison))
        {
            this.CodexQuotaPlanFiveHourComparison = CodexQuotaPlanComparison.LessThan;
        }

        if (!Enum.IsDefined(typeof(CodexQuotaPlanResumeConditionMode), this.CodexQuotaPlanResumeConditionMode))
        {
            this.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.Both;
        }

        this.CodexQuotaPlanWeeklyThresholdPercent = Clamp(
            this.CodexQuotaPlanWeeklyThresholdPercent,
            MinCodexQuotaPlanThresholdPercent,
            MaxCodexQuotaPlanThresholdPercent);
        this.CodexQuotaPlanFiveHourThresholdPercent = Clamp(
            this.CodexQuotaPlanFiveHourThresholdPercent,
            MinCodexQuotaPlanThresholdPercent,
            MaxCodexQuotaPlanThresholdPercent);
        this.CodexQuotaPlanPauseGoalIds = NormalizeGoalIdList(this.CodexQuotaPlanPauseGoalIds);
        this.CodexQuotaPlanResumeGoalIds = NormalizeGoalIdList(this.CodexQuotaPlanResumeGoalIds);

        if (!Enum.IsDefined(typeof(CleanIpBadgeTestMode), this.CleanIpBadgeTestMode))
        {
            this.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        }

        if (!Enum.IsDefined(typeof(NetworkStatusTestMode), this.NetworkStatusTestMode))
        {
            this.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        }

        this.MainDisplayDeviceName = NormalizeDisplayDeviceName(this.MainDisplayDeviceName);
        this.OperationDisplayDeviceName = NormalizeDisplayDeviceName(this.OperationDisplayDeviceName);
        this.MetricTileLeftX = NormalizeTileArray(this.MetricTileLeftX);
        this.MetricTileBottomY = NormalizeTileArray(this.MetricTileBottomY);
        this.RightTileButtonOrder = NormalizeRightTileButtonOrder(this.RightTileButtonOrder);
        this.RightTileButtonGapPixels = Clamp(this.RightTileButtonGapPixels, MinColumnButtonGapPixels, MaxColumnButtonGapPixels);
        this.RightTileGroupOffsetY = Clamp(this.RightTileGroupOffsetY, MinColumnGroupOffsetY, MaxColumnGroupOffsetY);
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        EnsureUsableWorkArea(ref workArea);
        EnsureAllLayoutWorkAreaReferences(workArea);
        ClampLayoutToTargetWorkAreas(GetPrimaryScale());
    }

    public static WidgetSettings Load()
    {
        return LoadFromPath(SettingsPath, true);
    }

    internal static WidgetSettings LoadFromPathForSelfTest(string path)
    {
        return LoadFromPath(path, false);
    }

    internal static WidgetSettings LoadFromPathAndSaveForSelfTest(string path)
    {
        return LoadFromPath(path, true);
    }

    private static WidgetSettings LoadFromPath(string path, bool saveAfterMigrationToSamePath)
    {
        WidgetSettings settings = new WidgetSettings();
        int settingsVersion = 0;
        bool sourceFileExists = File.Exists(path);
        bool saveAfterMigration = false;
        int legacyExpandWidth = 0;
        int legacyExpandHeight = 0;
        bool hasLegacyCodexWidth = false;
        bool hasLegacyCodexHeight = false;
        bool hasLegacyClockWidth = false;
        bool hasLegacyClockHeight = false;

        try
        {
            if (sourceFileExists)
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
                    if (string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(value, out settingsVersion);
                    }

                    int legacySize;
                    if (string.Equals(key, "CodexRadarWidth", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out legacySize))
                    {
                        legacyExpandWidth = legacySize;
                        hasLegacyCodexWidth = true;
                    }
                    else if (string.Equals(key, "ClockWidth", StringComparison.OrdinalIgnoreCase) &&
                        !hasLegacyCodexWidth &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out legacySize))
                    {
                        legacyExpandWidth = legacySize;
                        hasLegacyClockWidth = true;
                    }

                    if (string.Equals(key, "CodexRadarHeight", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out legacySize))
                    {
                        legacyExpandHeight = legacySize;
                        hasLegacyCodexHeight = true;
                    }
                    else if (string.Equals(key, "ClockHeight", StringComparison.OrdinalIgnoreCase) &&
                        !hasLegacyCodexHeight &&
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out legacySize))
                    {
                        legacyExpandHeight = legacySize;
                        hasLegacyClockHeight = true;
                    }

                    ApplyValue(settings, key, value);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (settingsVersion > 0 && settingsVersion < 27)
        {
            settings.OperationPrimaryPanelMode = InferOperationPrimaryPanelModeFromLegacyBooleans(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 40)
        {
            ApplyCodexModelIqBaselineMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 41)
        {
            settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Auto;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 43)
        {
            ApplyCodexModelIqTenTaskMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion >= 43 && settingsVersion < 44)
        {
            ApplyCodexModelIqWebsiteScoreMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 58)
        {
            settings.OperationRadialCoreAutoHideKeepAliveEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 59)
        {
            settings.OperationSettingsLogicExtensionEnabled = false;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 60)
        {
            settings.OperationRadialIdleCollapseSeconds = DefaultOperationRadialIdleCollapseSeconds;
            settings.OperationRadialIdleResetOnInteractionEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 61)
        {
            settings.OperationRadialKeepOpenAfterLeafClickEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 62)
        {
            ApplyCodexRadarDefaultModelMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 63)
        {
            settings.CodexRadarSpeedWindowCountdownEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 64)
        {
            settings.SpecBoardWidth = 432;
            settings.SpecBoardHeight = 400;
            settings.SpecBoardLeftX = -1;
            settings.SpecBoardBottomY = -1;
            settings.SpecBoardAutoHideSeconds = DefaultSpecBoardAutoHideSeconds;
            settings.SpecBoardLedgerPath = DefaultSpecBoardLedgerPath;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 65)
        {
            // Version 65 widens the board horizontally while preserving its left anchor. Apply
            // the factor once during migration so existing explicit widths expand with the UI.
            settings.SpecBoardWidth = Math.Min(
                MaxSpecBoardWidth,
                (int)Math.Round(settings.SpecBoardWidth * 1.5, MidpointRounding.AwayFromZero));
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 66)
        {
            settings.SpecBoardManagerWidth = 720;
            settings.SpecBoardManagerHeight = 520;
            settings.SpecBoardManagerDangerZoneRequiresTypedConfirm = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 67)
        {
            // Existing installations opt in once so upgrading preserves the new product default.
            // An explicit choice is then retained by the Version 67 persisted key.
            settings.CodexPetZOrderProtectionEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 68)
        {
            // Quota-reset rainbow celebration defaults on for existing installs; an explicit choice
            // is retained by the Version 68 persisted key.
            settings.CodexRadarQuotaResetRainbowEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 69)
        {
            // Auto-popup of the Spec board on newly registered specs defaults on for existing
            // installs; the 5-second dwell is retained by the Version 69 persisted keys.
            settings.SpecBoardAutoPopupEnabled = true;
            settings.SpecBoardAutoPopupSeconds = DefaultSpecBoardAutoPopupSeconds;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 70)
        {
            // Version 70 introduces the backend-only Codex task monitor. Existing installs
            // receive the documented defaults once; later explicit values remain untouched.
            settings.CodexTaskMonitorEnabled = true;
            settings.CodexTaskMonitorActiveWindowMinutes = 30;
            settings.CodexTaskMonitorActiveSeconds = 12;
            settings.CodexTaskMonitorIdleSeconds = 90;
            settings.CodexTaskMonitorTerminalHoldSeconds = 120;
            settings.CodexTaskMonitorErrorHoldSeconds = 30;
            settings.CodexTaskMonitorNumberCooldownSeconds = 120;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 71)
        {
            // Version 71 adds opt-in per-window transparency overrides. Existing installs follow
            // the global value until the user explicitly sets an override.
            settings.MainWidgetTransparencyOverridePercent = -1;
            settings.NetworkMonitorTransparencyOverridePercent = -1;
            settings.OperationTransparencyOverridePercent = -1;
            settings.SpecBoardTransparencyOverridePercent = -1;
            settings.CodexTaskBoardTransparencyOverridePercent = -1;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 72)
        {
            settings.NightScheduleEnabled = false;
            settings.NightScheduleStartMinutes = DefaultNightScheduleStartMinutes;
            settings.NightScheduleEndMinutes = DefaultNightScheduleEndMinutes;
            settings.NightDimLuminancePercent = DefaultNightDimLuminancePercent;
            settings.NightQuietHoursEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 73)
        {
            settings.MainWidgetScaleOverridePercent = -1;
            settings.NetworkMonitorScaleOverridePercent = -1;
            settings.OperationScaleOverridePercent = -1;
            settings.SpecBoardScaleOverridePercent = -1;
            settings.CodexTaskBoardScaleOverridePercent = -1;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 74)
        {
            // Upgrades retain every existing alert until the user opts out. These switches are
            // presentation policy only and never disable collection, debounce, or protection state.
            settings.AlertQuotaEnabled = true;
            settings.AlertResetProtectionEnabled = true;
            settings.AlertServiceHealthEnabled = true;
            settings.AlertCodexTaskEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 75)
        {
            settings.HotkeyToggleAllWindows = string.Empty;
            settings.HotkeyToggleHoverOpacity = string.Empty;
            settings.HotkeyOpenSettings = string.Empty;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 76)
        {
            settings.LeftDockOutsideClickCollapseEnabled = true;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 77)
        {
            // The launcher remains available as an explicit opt-in, but upgrades switch the core
            // double-click to the faster hidden-mode toggle requested for the default workflow.
            settings.OperationDoubleClickSpecialMenuEnabled = false;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 78)
        {
            // Version 78 makes cloud and fixed Ping targets user-configurable. Existing installs
            // receive the documented defaults once; later disabled rows remain explicit entries
            // so an unchecked service is excluded before any network request is scheduled.
            settings.CloudEndpointTargets = NetworkProbeTargetSettings.CloneArray(NetworkProbeTargetSettings.DefaultCloudEndpointTargets);
            settings.FixedPingTargets = NetworkProbeTargetSettings.CloneArray(NetworkProbeTargetSettings.DefaultFixedPingTargets);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 79)
        {
            // Version 79 adds the guard board as the fourth left-dock member. Existing installs get
            // the tab, but every guard starts disarmed: silently inheriting a sleep guard the user
            // never asked for could keep a laptop awake on battery overnight.
            settings.GuardBoardLeftDockEnabled = true;
            settings.GuardBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
            settings.GuardBoardAutoHideSeconds = DefaultGuardBoardAutoHideSeconds;
            settings.GuardSleepEnabled = false;
            settings.GuardSleepSinceUtcTicks = 0L;
            settings.GuardDisplayMinutes = DefaultGuardDisplayMinutes;
            settings.GuardOfflineThresholdMinutes = DefaultGuardOfflineThresholdMinutes;
            settings.GuardDisplayUntilUtcTicks = 0L;
            settings.GuardBatteryCarePauseUntilUtcTicks = 0L;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 80)
        {
            // GUARD previously shared the Spec Board override slots. Copy them once so upgrades
            // retain the exact visual state; the -1 sentinel naturally remains global-follow mode.
            settings.GuardBoardTransparencyOverridePercent = settings.SpecBoardTransparencyOverridePercent;
            settings.GuardBoardScaleOverridePercent = settings.SpecBoardScaleOverridePercent;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 81)
        {
            // Network dock visuals previously came from the Spec Board slots. Copy only into an
            // untouched sentinel so explicit Network choices survive the contract correction.
            if (settings.NetworkMonitorTransparencyOverridePercent == MinWindowTransparencyOverridePercent &&
                settings.SpecBoardTransparencyOverridePercent != MinWindowTransparencyOverridePercent)
            {
                settings.NetworkMonitorTransparencyOverridePercent = settings.SpecBoardTransparencyOverridePercent;
            }

            if (settings.NetworkMonitorScaleOverridePercent == MinWindowScaleOverridePercent &&
                settings.SpecBoardScaleOverridePercent != MinWindowScaleOverridePercent)
            {
                settings.NetworkMonitorScaleOverridePercent = settings.SpecBoardScaleOverridePercent;
            }

            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 82)
        {
            // The fifth dock member is presentation-only and safe to enable on upgrade. It receives
            // independent visual slots so changing the IQ board cannot restyle GUARD or Spec.
            settings.CodexIqBoardLeftDockEnabled = true;
            settings.CodexIqBoardLeftDockTabCenterY = AutoLeftDockTabCenterY;
            settings.CodexIqBoardAutoHideSeconds = DefaultCodexIqBoardAutoHideSeconds;
            settings.CodexIqBoardTransparencyOverridePercent = MinWindowTransparencyOverridePercent;
            settings.CodexIqBoardScaleOverridePercent = MinWindowScaleOverridePercent;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 83)
        {
            // Before v83, a non-auto coordinate meant the user had deliberately placed at least
            // one item in the global editor. Enabling grouped arrangement for that profile would
            // silently move it, so upgrades opt into grouping only when the whole legacy column
            // was still automatic. Turning grouping off later keeps those legacy coordinates as
            // the reversible fallback rather than destroying them during migration.
            settings.LeftDockAutoArrangeEnabled = AreAllLeftDockCentersAutomatic(settings);
            settings.RightTileAutoArrangeEnabled = AreAllMetricTilePositionsAutomatic(settings);
            settings.LeftDockButtonOrder = CloneLeftDockButtonOrder(DefaultLeftDockButtonOrder);
            settings.LeftDockButtonGapPixels = DefaultLeftDockButtonGapPixels;
            settings.LeftDockGroupOffsetY = DefaultColumnGroupOffsetY;
            settings.RightTileButtonOrder = CloneRightTileButtonOrder(DefaultRightTileButtonOrder);
            settings.RightTileButtonGapPixels = DefaultRightTileButtonGapPixels;
            settings.RightTileGroupOffsetY = DefaultColumnGroupOffsetY;
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 85)
        {
            // Version 85 removes every retired floating-window setting in one canonical rewrite.
            // Preserve the only legacy geometry that still has visible meaning: the hover-expanded
            // metric panel inherits the v84 Codex Radar size, with ClockWidth/Height as the older
            // fallback. Canonical Codex keys win regardless of file order.
            if (hasLegacyCodexWidth || hasLegacyClockWidth)
            {
                settings.MetricTileExpandWidth = legacyExpandWidth;
            }

            if (hasLegacyCodexHeight || hasLegacyClockHeight)
            {
                settings.MetricTileExpandHeight = legacyExpandHeight;
            }

            saveAfterMigration = true;
        }

        if (sourceFileExists && settingsVersion < 86)
        {
            // Version 86 retires settings whose DeepSeek-balance and Claude-community consumers
            // no longer exist. Versionless legacy files report schema 0 and must also be rewritten;
            // ApplyValue already ignores the retired inputs, while this atomic canonical save
            // preserves every unrelated recognized setting and prevents the old keys lingering.
            saveAfterMigration = true;
        }

        if (sourceFileExists && settingsVersion < 87)
        {
            // Version 87 introduces the China-egress guard. Rewrite existing profiles so the
            // fail-closed default becomes explicit on disk; otherwise startup would enforce the
            // in-memory default while settings.ini remained at v86 until the settings UI saved.
            saveAfterMigration = true;
        }

        if (sourceFileExists && settingsVersion < 88)
        {
            // Version 88 retires the old hidden-mode colour inversion. ApplyValue ignores retired
            // keys and this canonical rewrite removes the stale value without changing any other
            // hidden-opacity, night-luminance or pixel-migration setting.
            saveAfterMigration = true;
        }

        if (sourceFileExists && settingsVersion < 89)
        {
            // Version 89 introduces the rebuilt two-level burn-in policy. Defaults are already
            // present in memory; the canonical rewrite makes the new opt-out and both thresholds
            // explicit without ever consulting the permanently retired v88 colour-protection key.
            saveAfterMigration = true;
        }

        if (sourceFileExists && settingsVersion < 90)
        {
            // Version 90 reinterprets the legacy-named gap fields as 0-100 distribution values.
            // Existing numeric choices remain valid and are preserved; the canonical rewrite records
            // the new contract while the wider upper bound makes full-edge distribution selectable.
            saveAfterMigration = true;
        }

        settings.AdaptToCurrentWorkArea();
        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.Normalize();
        if (settingsVersion > 0 && settingsVersion < 62)
        {
            // Empty legacy keys are populated by Normalize, so the migration must run once
            // more after normalization to catch that otherwise invisible previous default.
            ApplyCodexRadarDefaultModelMigration(settings);
            settings.CodexRadarModelVersion =
                CodexRadarModelCatalog.LegacyVersionFromKey(settings.CodexRadarModelKey);
        }

        if (saveAfterMigration && saveAfterMigrationToSamePath)
        {
            try
            {
                settings.SaveToPath(path, true);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        return settings;
    }

    private static bool AreAllLeftDockCentersAutomatic(WidgetSettings settings)
    {
        return NormalizeLeftDockTabCenterY(settings.SpecBoardLeftDockTabCenterY) == AutoLeftDockTabCenterY &&
            NormalizeLeftDockTabCenterY(settings.CodexTaskBoardLeftDockTabCenterY) == AutoLeftDockTabCenterY &&
            NormalizeLeftDockTabCenterY(settings.NetworkMonitorLeftDockTabCenterY) == AutoLeftDockTabCenterY &&
            NormalizeLeftDockTabCenterY(settings.GuardBoardLeftDockTabCenterY) == AutoLeftDockTabCenterY &&
            NormalizeLeftDockTabCenterY(settings.CodexIqBoardLeftDockTabCenterY) == AutoLeftDockTabCenterY;
    }

    private static bool AreAllMetricTilePositionsAutomatic(WidgetSettings settings)
    {
        int[] left = NormalizeTileArray(settings.MetricTileLeftX);
        int[] bottom = NormalizeTileArray(settings.MetricTileBottomY);
        for (int i = 0; i < MetricTileCount; i++)
        {
            if (left[i] != AutoTilePosition || bottom[i] != AutoTilePosition)
            {
                return false;
            }
        }

        return true;
    }

    private static OperationPrimaryPanelMode InferOperationPrimaryPanelModeFromLegacyBooleans(WidgetSettings settings)
    {
        if (settings.OperationWindowsButtonEnabled && settings.OperationMemoryPieEnabled)
        {
            return OperationPrimaryPanelMode.Auto;
        }

        if (settings.OperationWindowsButtonEnabled)
        {
            return OperationPrimaryPanelMode.WindowsButton;
        }

        if (settings.OperationMemoryPieEnabled)
        {
            return OperationPrimaryPanelMode.MemoryPie;
        }

        return OperationPrimaryPanelMode.Hidden;
    }

    private static void ApplyCodexModelIqBaselineMigration(WidgetSettings settings)
    {
        if (settings.CodexModelIqBaselinePassed == PreviousDefaultCodexModelIqBaselinePassed)
        {
            settings.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
        }

        if (settings.CodexModelIqTestPassed == PreviousDefaultCodexModelIqBaselinePassed)
        {
            settings.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        }
    }

    private static void ApplyCodexModelIqTenTaskMigration(WidgetSettings settings)
    {
        if (settings.CodexModelIqBaselinePassed == PreviousTwelveTaskCodexModelIqBaselinePassed)
        {
            settings.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
        }

        if (settings.CodexModelIqTestPassed == PreviousTwelveTaskCodexModelIqBaselinePassed)
        {
            settings.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        }
    }

    private static void ApplyCodexModelIqWebsiteScoreMigration(WidgetSettings settings)
    {
        if (settings.CodexModelIqBaselinePassed == PreviousTenTaskLocalCodexModelIqBaselinePassed)
        {
            settings.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
        }

        if (settings.CodexModelIqTestPassed == PreviousTenTaskLocalCodexModelIqBaselinePassed)
        {
            settings.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        }
    }

    private static bool GetLegacyOperationWindowsButtonEnabled(OperationPrimaryPanelMode mode)
    {
        return mode == OperationPrimaryPanelMode.Auto || mode == OperationPrimaryPanelMode.WindowsButton;
    }

    private static bool GetLegacyOperationMemoryPieEnabled(OperationPrimaryPanelMode mode)
    {
        return mode == OperationPrimaryPanelMode.Auto || mode == OperationPrimaryPanelMode.MemoryPie;
    }

    public void Save()
    {
        SaveToPath(SettingsPath, true);
    }

    internal void SaveToPathForSelfTest(string path)
    {
        SaveToPath(path, false);
    }

    private void SaveToPath(string path, bool captureCurrentWorkArea)
    {
        this.Normalize();
        if (captureCurrentWorkArea)
        {
            this.CaptureCurrentWorkArea();
        }

        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string[] lines = new string[]
        {
            "Version=" + CurrentSettingsVersion.ToString(CultureInfo.InvariantCulture),
            "ContentTransparencyPercent=" + this.ApplicationTransparencyPercent,
            "ApplicationTransparencyPercent=" + this.ApplicationTransparencyPercent,
            "MainWidgetTransparencyOverridePercent=" + this.MainWidgetTransparencyOverridePercent,
            "NetworkMonitorTransparencyOverridePercent=" + this.NetworkMonitorTransparencyOverridePercent,
            "OperationTransparencyOverridePercent=" + this.OperationTransparencyOverridePercent,
            "SpecBoardTransparencyOverridePercent=" + this.SpecBoardTransparencyOverridePercent,
            "CodexTaskBoardTransparencyOverridePercent=" + this.CodexTaskBoardTransparencyOverridePercent,
            "GuardBoardTransparencyOverridePercent=" + this.GuardBoardTransparencyOverridePercent,
            "CodexIqBoardTransparencyOverridePercent=" + this.CodexIqBoardTransparencyOverridePercent,
            "NightScheduleEnabled=" + this.NightScheduleEnabled,
            "NightScheduleStartMinutes=" + this.NightScheduleStartMinutes,
            "NightScheduleEndMinutes=" + this.NightScheduleEndMinutes,
            "NightDimLuminancePercent=" + this.NightDimLuminancePercent,
            "NightQuietHoursEnabled=" + this.NightQuietHoursEnabled,
            "AlertQuotaEnabled=" + this.AlertQuotaEnabled,
            "AlertResetProtectionEnabled=" + this.AlertResetProtectionEnabled,
            "AlertServiceHealthEnabled=" + this.AlertServiceHealthEnabled,
            "AlertCodexTaskEnabled=" + this.AlertCodexTaskEnabled,
            "HotkeyToggleAllWindows=" + this.HotkeyToggleAllWindows,
            "HotkeyToggleHoverOpacity=" + this.HotkeyToggleHoverOpacity,
            "HotkeyOpenSettings=" + this.HotkeyOpenSettings,
            "MainWidgetScaleOverridePercent=" + this.MainWidgetScaleOverridePercent,
            "NetworkMonitorScaleOverridePercent=" + this.NetworkMonitorScaleOverridePercent,
            "OperationScaleOverridePercent=" + this.OperationScaleOverridePercent,
            "SpecBoardScaleOverridePercent=" + this.SpecBoardScaleOverridePercent,
            "CodexTaskBoardScaleOverridePercent=" + this.CodexTaskBoardScaleOverridePercent,
            "GuardBoardScaleOverridePercent=" + this.GuardBoardScaleOverridePercent,
            "CodexIqBoardScaleOverridePercent=" + this.CodexIqBoardScaleOverridePercent,
            "PowerThermalIntegratedEnabled=" + this.PowerThermalIntegratedEnabled,
            "PowerThermalManualEnergySaverThresholdPercent=" + this.PowerThermalManualEnergySaverThresholdPercent,
            "NetworkMonitorAdapterId=" + this.NetworkMonitorAdapterId,
            "NetworkStatusTestMode=" + this.NetworkStatusTestMode,
            "GfwProbeEnabled=" + this.GfwProbeEnabled,
            "GfwProbeIntervalMinutes=" + this.GfwProbeIntervalMinutes,
            "CloudEndpointTestSeed=" + this.CloudEndpointTestSeed,
            "CloudStatusRegionMask=" + this.CloudStatusRegionMask,
            "CloudEndpointTargets=" + NetworkProbeTargetSettings.SerializeArray(this.CloudEndpointTargets),
            "FixedPingTargets=" + NetworkProbeTargetSettings.SerializeArray(this.FixedPingTargets),
            "ConnectionCheckIntervalSeconds=" + this.ConnectionCheckIntervalSeconds,
            "SpecBoardWidth=" + this.SpecBoardWidth,
            "SpecBoardHeight=" + this.SpecBoardHeight,
            "SpecBoardLeftX=" + this.SpecBoardLeftX,
            "SpecBoardBottomY=" + this.SpecBoardBottomY,
            "SpecBoardAutoHideSeconds=" + this.SpecBoardAutoHideSeconds,
            "SpecBoardAutoPopupEnabled=" + this.SpecBoardAutoPopupEnabled,
            "SpecBoardAutoPopupSeconds=" + this.SpecBoardAutoPopupSeconds,
            "SpecBoardLedgerPath=" + this.SpecBoardLedgerPath,
            "SpecBoardManagerWidth=" + this.SpecBoardManagerWidth,
            "SpecBoardManagerHeight=" + this.SpecBoardManagerHeight,
            "SpecBoardManagerDangerZoneRequiresTypedConfirm=" + this.SpecBoardManagerDangerZoneRequiresTypedConfirm,
            "LeftDockAutoArrangeEnabled=" + this.LeftDockAutoArrangeEnabled,
            "LeftDockButtonOrder=" + string.Join(",", NormalizeLeftDockButtonOrder(this.LeftDockButtonOrder)),
            "LeftDockButtonGapPixels=" + this.LeftDockButtonGapPixels.ToString(CultureInfo.InvariantCulture),
            "LeftDockGroupOffsetY=" + this.LeftDockGroupOffsetY.ToString(CultureInfo.InvariantCulture),
            "SpecBoardLeftDockEnabled=" + this.SpecBoardLeftDockEnabled,
            "SpecBoardLeftDockTabCenterY=" + this.SpecBoardLeftDockTabCenterY.ToString(CultureInfo.InvariantCulture),
            "CodexTaskBoardLeftDockEnabled=" + this.CodexTaskBoardLeftDockEnabled,
            "CodexTaskBoardLeftDockTabCenterY=" + this.CodexTaskBoardLeftDockTabCenterY.ToString(CultureInfo.InvariantCulture),
            "NetworkMonitorLeftDockTabCenterY=" + this.NetworkMonitorLeftDockTabCenterY.ToString(CultureInfo.InvariantCulture),
            "GuardBoardLeftDockEnabled=" + this.GuardBoardLeftDockEnabled,
            "GuardBoardLeftDockTabCenterY=" + this.GuardBoardLeftDockTabCenterY.ToString(CultureInfo.InvariantCulture),
            "GuardBoardAutoHideSeconds=" + this.GuardBoardAutoHideSeconds.ToString(CultureInfo.InvariantCulture),
            "CodexIqBoardLeftDockEnabled=" + this.CodexIqBoardLeftDockEnabled,
            "CodexIqBoardLeftDockTabCenterY=" + this.CodexIqBoardLeftDockTabCenterY.ToString(CultureInfo.InvariantCulture),
            "CodexIqBoardAutoHideSeconds=" + this.CodexIqBoardAutoHideSeconds.ToString(CultureInfo.InvariantCulture),
            "GuardSleepEnabled=" + this.GuardSleepEnabled,
            "GuardSleepSinceUtcTicks=" + this.GuardSleepSinceUtcTicks.ToString(CultureInfo.InvariantCulture),
            "GuardDisplayMinutes=" + this.GuardDisplayMinutes.ToString(CultureInfo.InvariantCulture),
            "GuardOfflineThresholdMinutes=" + this.GuardOfflineThresholdMinutes.ToString(CultureInfo.InvariantCulture),
            "GuardDisplayUntilUtcTicks=" + this.GuardDisplayUntilUtcTicks.ToString(CultureInfo.InvariantCulture),
            "GuardBatteryCarePauseUntilUtcTicks=" + this.GuardBatteryCarePauseUntilUtcTicks.ToString(CultureInfo.InvariantCulture),
            "LeftDockCollapseSeconds=" + this.LeftDockCollapseSeconds.ToString(CultureInfo.InvariantCulture),
            "LeftDockOutsideClickCollapseEnabled=" + this.LeftDockOutsideClickCollapseEnabled,
            "CodexTaskBoardWidth=" + this.CodexTaskBoardWidth.ToString(CultureInfo.InvariantCulture),
            "CodexTaskBoardHeight=" + this.CodexTaskBoardHeight.ToString(CultureInfo.InvariantCulture),
            "CodexTaskBoardView=" + this.CodexTaskBoardView,
            "CodexTaskBoardTimelineMinutes=" + this.CodexTaskBoardTimelineMinutes.ToString(CultureInfo.InvariantCulture),
            "CodexTaskMonitorEnabled=" + this.CodexTaskMonitorEnabled,
            "CodexTaskMonitorActiveWindowMinutes=" + this.CodexTaskMonitorActiveWindowMinutes,
            "CodexTaskMonitorActiveSeconds=" + this.CodexTaskMonitorActiveSeconds,
            "CodexTaskMonitorIdleSeconds=" + this.CodexTaskMonitorIdleSeconds,
            "CodexTaskMonitorTerminalHoldSeconds=" + this.CodexTaskMonitorTerminalHoldSeconds,
            "CodexTaskMonitorErrorHoldSeconds=" + this.CodexTaskMonitorErrorHoldSeconds,
            "CodexTaskMonitorNumberCooldownSeconds=" + this.CodexTaskMonitorNumberCooldownSeconds,
            "OperationButtonSize=" + this.OperationButtonSize,
            "OperationLeftOffset=" + this.OperationLeftOffset,
            "OperationBottomOffset=" + this.OperationBottomOffset,
            "OperationBackgroundTransparencyPercent=" + this.OperationBackgroundTransparencyPercent,
            "OperationPrimaryPanelMode=" + this.OperationPrimaryPanelMode,
            "OperationWindowsButtonEnabled=" + GetLegacyOperationWindowsButtonEnabled(this.OperationPrimaryPanelMode),
            "OperationMemoryPieEnabled=" + GetLegacyOperationMemoryPieEnabled(this.OperationPrimaryPanelMode),
            "ForceShowForegroundFpsEnabled=" + this.ForceShowForegroundFpsEnabled,
            "SeelenDockForegroundPulseEnabled=" + this.SeelenDockForegroundPulseEnabled,
            "WinDRecoveryPulseEnabled=" + this.WinDRecoveryPulseEnabled,
            "CodexPetZOrderProtectionEnabled=" + this.CodexPetZOrderProtectionEnabled,
            "PowerResumeRestartEnabled=" + this.PowerResumeRestartEnabled,
            "FallbackDisconnectedDisplaysEnabled=" + this.FallbackDisconnectedDisplaysEnabled,
            "MainDisplayDeviceName=" + this.MainDisplayDeviceName,
            "OperationDisplayDeviceName=" + this.OperationDisplayDeviceName,
            "ResolutionCompatibilityModeEnabled=" + this.ResolutionCompatibilityModeEnabled,
            "ResolutionCompatibilityScalePercent=" + this.ResolutionCompatibilityScalePercent,
            "LayoutWorkAreaLeft=" + this.LayoutWorkAreaLeft,
            "LayoutWorkAreaTop=" + this.LayoutWorkAreaTop,
            "LayoutWorkAreaWidth=" + this.LayoutWorkAreaWidth,
            "LayoutWorkAreaHeight=" + this.LayoutWorkAreaHeight,
            "OperationLayoutWorkAreaLeft=" + this.OperationLayoutWorkAreaLeft,
            "OperationLayoutWorkAreaTop=" + this.OperationLayoutWorkAreaTop,
            "OperationLayoutWorkAreaWidth=" + this.OperationLayoutWorkAreaWidth,
            "OperationLayoutWorkAreaHeight=" + this.OperationLayoutWorkAreaHeight,
            "VisibilityMode=" + this.VisibilityMode,
            "VisibilityOverlapIgnoresOperationPanelEnabled=" + this.VisibilityOverlapIgnoresOperationPanelEnabled,
            "ClickThroughMode=" + this.ClickThroughMode,
            "StartupEnabled=" + this.StartupEnabled,
            "SensitiveMouseModeEnabled=" + this.SensitiveMouseModeEnabled,
            "SensitiveMouseRangePixels=" + this.SensitiveMouseRangePixels,
            "HoverOpacityRevealDelayEnabled=" + this.HoverOpacityRevealDelayEnabled,
            "HoverOpacityRevealDelaySeconds=" + FormatDouble(this.HoverOpacityRevealDelaySeconds),
            "HoverOpacityRevealResetSeconds=" + FormatDouble(this.HoverOpacityRevealResetSeconds),
            "HoverOpacityCoverEnabled=" + this.HoverOpacityCoverEnabled,
            "ReverseHoverOpacityRevealEnabled=" + this.ReverseHoverOpacityRevealEnabled,
            "ReverseHoverOpacityRestoreDelaySeconds=" + this.ReverseHoverOpacityRestoreDelaySeconds,
            "AlertTestEnabled=" + this.AlertTestEnabled,
            "ThermalTestMode=" + this.ThermalTestMode,
            "CodexRadarTestMode=" + this.CodexRadarTestMode,
            "ServiceHealthTestMode=" + this.ServiceHealthTestMode,
            "CodexRadarRandomTestEnabled=" + this.CodexRadarRandomTestEnabled,
            "CodexRadarRandomTestAutoRefresh=" + this.CodexRadarRandomTestAutoRefresh,
            "CodexRadarRandomTestRefreshToken=" + this.CodexRadarRandomTestRefreshToken,
            "MainWidgetTileLargeModeEnabled=" + this.MainWidgetTileLargeModeEnabled,
            "MetricTileExpandWidth=" + this.MetricTileExpandWidth.ToString(CultureInfo.InvariantCulture),
            "MetricTileExpandHeight=" + this.MetricTileExpandHeight.ToString(CultureInfo.InvariantCulture),
            "OperationRenderVariant=" + this.OperationRenderVariant,
            "CodexRadarPublicJsonEnabled=" + this.CodexRadarPublicJsonEnabled,
            "CodexRadarHtmlFallbackEnabled=" + this.CodexRadarHtmlFallbackEnabled,
            "CodexRadarRssFallbackEnabled=" + this.CodexRadarRssFallbackEnabled,
            "CodexQuotaDueResetProtectionEnabled=" + this.CodexQuotaDueResetProtectionEnabled,
            "CodexQuotaRssResetProtectionEnabled=" + this.CodexQuotaRssResetProtectionEnabled,
            "CodexQuotaProviderZeroDropProtectionEnabled=" + this.CodexQuotaProviderZeroDropProtectionEnabled,
            "CodexQuotaDuplicateSameBalanceRingProtectionEnabled=" + this.CodexQuotaDuplicateSameBalanceRingProtectionEnabled,
            "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled=" + this.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled,
            "CodexQuotaProviderWeeklySpikeProtectionEnabled=" + this.CodexQuotaProviderWeeklySpikeProtectionEnabled,
            "CodexQuotaStrictFiveHourResetBoundaryEnabled=" + this.CodexQuotaStrictFiveHourResetBoundaryEnabled,
            "CodexQuotaWeeklyBaselineAutoRepairEnabled=" + this.CodexQuotaWeeklyBaselineAutoRepairEnabled,
            "CodexRadarServiceProbeToken=" + this.CodexRadarServiceProbeToken,
            "AiRequestProtectionAutoEnabled=" + this.AiRequestProtectionAutoEnabled,
            "AiRequestProtectionManualBlockEnabled=" + this.AiRequestProtectionManualBlockEnabled,
            "AiChinaEgressGuardEnabled=" + this.AiChinaEgressGuardEnabled,
            "CodexQuotaPlanEnabled=" + this.CodexQuotaPlanEnabled,
            "CodexQuotaPlanWeeklyComparison=" + this.CodexQuotaPlanWeeklyComparison,
            "CodexQuotaPlanWeeklyThresholdPercent=" + this.CodexQuotaPlanWeeklyThresholdPercent,
            "CodexQuotaPlanFiveHourComparison=" + this.CodexQuotaPlanFiveHourComparison,
            "CodexQuotaPlanFiveHourThresholdPercent=" + this.CodexQuotaPlanFiveHourThresholdPercent,
            "CodexQuotaPlanResumeConditionMode=" + this.CodexQuotaPlanResumeConditionMode,
            "CodexQuotaPlanAutoResumePausedGoals=" + this.CodexQuotaPlanAutoResumePausedGoals,
            "CodexQuotaPlanPauseGoalIds=" + this.CodexQuotaPlanPauseGoalIds,
            "CodexQuotaPlanResumeGoalIds=" + this.CodexQuotaPlanResumeGoalIds,
            "CleanIpBadgeTestMode=" + this.CleanIpBadgeTestMode,
            "CodexModelIqTestEnabled=" + this.CodexModelIqTestEnabled,
            "CodexModelIqTestPassed=" + this.CodexModelIqTestPassed,
            "CodexModelIqBaselineAutoEnabled=" + this.CodexModelIqBaselineAutoEnabled,
            "CodexModelIqBaselinePassed=" + this.CodexModelIqBaselinePassed,
            "CodexModelIqBaselineValidTasks=" + this.CodexModelIqBaselineValidTasks,
            "CodexModelIqBaselineMode=" + this.CodexModelIqBaselineMode,
            "CodexModelEfficiencyTestEnabled=" + this.CodexModelEfficiencyTestEnabled,
            "CodexModelTokenEfficiencyTestPercent=" + this.CodexModelTokenEfficiencyTestPercent,
            "CodexModelTimeEfficiencyTestPercent=" + this.CodexModelTimeEfficiencyTestPercent,
            "CodexModelTokenEfficiencyBaselinePassed=" + this.CodexModelTokenEfficiencyBaselinePassed,
            "CodexModelTokenEfficiencyBaselineTokens=" + this.CodexModelTokenEfficiencyBaselineTokens,
            "CodexModelTokenEfficiencyBaselineMode=" + this.CodexModelTokenEfficiencyBaselineMode,
            "CodexModelTimeEfficiencyBaselinePassed=" + this.CodexModelTimeEfficiencyBaselinePassed,
            "CodexModelTimeEfficiencyBaselineSeconds=" + this.CodexModelTimeEfficiencyBaselineSeconds,
            "CodexModelTimeEfficiencyBaselineMode=" + this.CodexModelTimeEfficiencyBaselineMode,
            "CodexModelTokenEfficiencyLowThresholdPercent=" + this.CodexModelTokenEfficiencyLowThresholdPercent,
            "CodexModelTimeEfficiencyLowThresholdPercent=" + this.CodexModelTimeEfficiencyLowThresholdPercent,
            "CodexRadarModelVersion=" + this.CodexRadarModelVersion,
            "CodexRadarModelKey=" + this.CodexRadarModelKey,
            "CodexRadarSoftwareMode=" + this.CodexRadarSoftwareMode,
            "RadarClockAutoSwitchModelEnabled=" + this.RadarClockAutoSwitchModelEnabled,
            "RadarClockTimeDisplayMode=" + this.RadarClockTimeDisplayMode,
            "CodexRadarSpeedWindowCountdownEnabled=" + this.CodexRadarSpeedWindowCountdownEnabled,
            "CodexRadarQuotaResetRainbowEnabled=" + this.CodexRadarQuotaResetRainbowEnabled,
            "DisplayTimeZoneMode=" + this.DisplayTimeZoneMode,
            "DisplayTimeZoneId=" + this.DisplayTimeZoneId,
            "PowerSavingEnabled=" + this.PowerSavingEnabled,
            "PerformanceMode=" + this.PerformanceMode,
            "HoverOpacityEnabled=" + this.HoverOpacityEnabled,
            "AutoHoverOpacityIdleEnabled=" + this.AutoHoverOpacityIdleEnabled,
            "AutoHoverOpacityIdleSeconds=" + this.AutoHoverOpacityIdleSeconds,
            "AutoHoverOpacityMaximizedEnabled=" + this.AutoHoverOpacityMaximizedEnabled,
            "BurnInProtectionEnabled=" + this.BurnInProtectionEnabled,
            "BurnInLevelOneIdleSeconds=" + this.BurnInLevelOneIdleSeconds,
            "BurnInLevelTwoDelaySeconds=" + this.BurnInLevelTwoDelaySeconds,
            "OperationRadialCoreAutoHideKeepAliveEnabled=" + this.OperationRadialCoreAutoHideKeepAliveEnabled,
            "OperationRadialIdleCollapseSeconds=" + this.OperationRadialIdleCollapseSeconds,
            "OperationRadialIdleResetOnInteractionEnabled=" + this.OperationRadialIdleResetOnInteractionEnabled,
            "OperationRadialKeepOpenAfterLeafClickEnabled=" + this.OperationRadialKeepOpenAfterLeafClickEnabled,
            "OperationDoubleClickSpecialMenuEnabled=" + this.OperationDoubleClickSpecialMenuEnabled,
            "OperationSettingsLogicExtensionEnabled=" + this.OperationSettingsLogicExtensionEnabled,
            "MetricTileLeftX=" + SerializeTileArray(this.MetricTileLeftX),
            "MetricTileBottomY=" + SerializeTileArray(this.MetricTileBottomY),
            "RightTileAutoArrangeEnabled=" + this.RightTileAutoArrangeEnabled,
            "RightTileButtonOrder=" + string.Join(",", NormalizeRightTileButtonOrder(this.RightTileButtonOrder)),
            "RightTileButtonGapPixels=" + this.RightTileButtonGapPixels.ToString(CultureInfo.InvariantCulture),
            "RightTileGroupOffsetY=" + this.RightTileGroupOffsetY.ToString(CultureInfo.InvariantCulture)
        };
        string tempPath = path + ".tmp";
        try
        {
            // A stale temp file can remain after power loss. It is never authoritative and must not
            // prevent the next complete snapshot from replacing it.
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            File.WriteAllLines(tempPath, lines, SharedEncoding.Utf8NoBom);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            // Replacement failure must leave the old target intact; the incomplete temp snapshot
            // is best-effort cleanup and is never loaded as settings.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void ApplyValue(WidgetSettings settings, string key, string value)
    {
        int intValue;
        double doubleValue;
        bool boolValue;

        // Retired inputs are intentionally ignored even if a future refactor accidentally leaves
        // a legacy parser branch behind. Geometry values needed by schema 85 are captured before
        // this method is called, so this guard cannot discard the one supported migration payload.
        if (RetiredSettingsInputNames.Contains(key))
        {
            return;
        }

        if ((string.Equals(key, "ApplicationTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ContentTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.ApplicationTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "MainWidgetTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.MainWidgetTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "NetworkMonitorTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "OperationTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "SpecBoardTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "CodexTaskBoardTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskBoardTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "GuardBoardTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.GuardBoardTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "CodexIqBoardTransparencyOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexIqBoardTransparencyOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "NightScheduleEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.NightScheduleEnabled = boolValue;
            return;
        }
        if (string.Equals(key, "NightScheduleStartMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NightScheduleStartMinutes = intValue;
            return;
        }
        if (string.Equals(key, "NightScheduleEndMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NightScheduleEndMinutes = intValue;
            return;
        }
        if (string.Equals(key, "NightDimLuminancePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NightDimLuminancePercent = intValue;
            return;
        }
        if (string.Equals(key, "NightQuietHoursEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.NightQuietHoursEnabled = boolValue;
            return;
        }
        if (string.Equals(key, "AlertQuotaEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AlertQuotaEnabled = boolValue;
            return;
        }
        if (string.Equals(key, "AlertResetProtectionEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AlertResetProtectionEnabled = boolValue;
            return;
        }
        if (string.Equals(key, "AlertServiceHealthEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AlertServiceHealthEnabled = boolValue;
            return;
        }
        if (string.Equals(key, "AlertCodexTaskEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AlertCodexTaskEnabled = boolValue;
            return;
        }
        if (string.Equals(key, "HotkeyToggleAllWindows", StringComparison.OrdinalIgnoreCase))
        {
            settings.HotkeyToggleAllWindows = value;
            return;
        }
        if (string.Equals(key, "HotkeyToggleHoverOpacity", StringComparison.OrdinalIgnoreCase))
        {
            settings.HotkeyToggleHoverOpacity = value;
            return;
        }
        if (string.Equals(key, "HotkeyOpenSettings", StringComparison.OrdinalIgnoreCase))
        {
            settings.HotkeyOpenSettings = value;
            return;
        }
        if (string.Equals(key, "MainWidgetScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.MainWidgetScaleOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "NetworkMonitorScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorScaleOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "OperationScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationScaleOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "SpecBoardScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardScaleOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "CodexTaskBoardScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskBoardScaleOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "GuardBoardScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.GuardBoardScaleOverridePercent = intValue;
            return;
        }
        if (string.Equals(key, "CodexIqBoardScaleOverridePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexIqBoardScaleOverridePercent = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalIntegratedEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerThermalIntegratedEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "PowerThermalManualEnergySaverThresholdPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.PowerThermalManualEnergySaverThresholdPercent = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorAdapterId", StringComparison.OrdinalIgnoreCase))
        {
            settings.NetworkMonitorAdapterId = (value ?? string.Empty).Trim();
            return;
        }

        if (string.Equals(key, "NetworkStatusTestMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.NetworkStatusTestMode = (NetworkStatusTestMode)Enum.Parse(typeof(NetworkStatusTestMode), value, true);
            }
            catch
            {
                settings.NetworkStatusTestMode = NetworkStatusTestMode.Off;
            }

            return;
        }

        if (string.Equals(key, "GfwProbeEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.GfwProbeEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "GfwProbeIntervalMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.GfwProbeIntervalMinutes = intValue;
            return;
        }

        if (string.Equals(key, "CloudEndpointTestSeed", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CloudEndpointTestSeed = Math.Max(0, intValue);
            return;
        }

        if (string.Equals(key, "CloudStatusRegionMask", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CloudStatusRegionMask = intValue & CloudStatusRegionMaskAll;
            return;
        }

        if (string.Equals(key, "CloudEndpointTargets", StringComparison.OrdinalIgnoreCase))
        {
            settings.CloudEndpointTargets = NetworkProbeTargetSettings.DeserializeArray(value);
            return;
        }

        if (string.Equals(key, "FixedPingTargets", StringComparison.OrdinalIgnoreCase))
        {
            settings.FixedPingTargets = NetworkProbeTargetSettings.DeserializeArray(value);
            return;
        }

        if (string.Equals(key, "ConnectionCheckIntervalSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckIntervalSeconds = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckManualRefreshToken", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckManualRefreshToken = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardWidth = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardHeight = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardLeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardLeftX = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardBottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardBottomY = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardAutoHideSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardAutoHideSeconds = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardAutoPopupEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.SpecBoardAutoPopupEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "SpecBoardAutoPopupSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardAutoPopupSeconds = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardLedgerPath", StringComparison.OrdinalIgnoreCase))
        {
            settings.SpecBoardLedgerPath = value;
            return;
        }

        if (string.Equals(key, "SpecBoardManagerWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardManagerWidth = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardManagerHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SpecBoardManagerHeight = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardManagerDangerZoneRequiresTypedConfirm", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.SpecBoardManagerDangerZoneRequiresTypedConfirm = boolValue;
            return;
        }

        if (string.Equals(key, "LeftDockAutoArrangeEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.LeftDockAutoArrangeEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "LeftDockButtonOrder", StringComparison.OrdinalIgnoreCase))
        {
            settings.LeftDockButtonOrder = NormalizeLeftDockButtonOrder(
                value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        if (string.Equals(key, "LeftDockButtonGapPixels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.LeftDockButtonGapPixels = intValue;
            return;
        }

        if (string.Equals(key, "LeftDockGroupOffsetY", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.LeftDockGroupOffsetY = intValue;
            return;
        }

        if (string.Equals(key, "SpecBoardLeftDockEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.SpecBoardLeftDockEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "SpecBoardLeftDockTabCenterY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.SpecBoardLeftDockTabCenterY = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskBoardLeftDockEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexTaskBoardLeftDockEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexTaskBoardLeftDockTabCenterY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.CodexTaskBoardLeftDockTabCenterY = intValue;
            return;
        }

        if (string.Equals(key, "GuardBoardLeftDockEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.GuardBoardLeftDockEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "GuardBoardLeftDockTabCenterY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.GuardBoardLeftDockTabCenterY = intValue;
            return;
        }

        if (string.Equals(key, "GuardBoardAutoHideSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.GuardBoardAutoHideSeconds = intValue;
            return;
        }

        if (string.Equals(key, "CodexIqBoardLeftDockEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexIqBoardLeftDockEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexIqBoardLeftDockTabCenterY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.CodexIqBoardLeftDockTabCenterY = intValue;
            return;
        }

        if (string.Equals(key, "CodexIqBoardAutoHideSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.CodexIqBoardAutoHideSeconds = intValue;
            return;
        }

        if (string.Equals(key, "GuardSleepEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.GuardSleepEnabled = boolValue;
            return;
        }

        long guardSinceTicks;
        if (string.Equals(key, "GuardSleepSinceUtcTicks", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out guardSinceTicks))
        {
            settings.GuardSleepSinceUtcTicks = guardSinceTicks;
            return;
        }

        if (string.Equals(key, "GuardDisplayMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.GuardDisplayMinutes = intValue;
            return;
        }

        if (string.Equals(key, "GuardOfflineThresholdMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.GuardOfflineThresholdMinutes = intValue;
            return;
        }

        long guardTicks;
        if (string.Equals(key, "GuardDisplayUntilUtcTicks", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out guardTicks))
        {
            settings.GuardDisplayUntilUtcTicks = guardTicks;
            return;
        }

        if (string.Equals(key, "GuardBatteryCarePauseUntilUtcTicks", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out guardTicks))
        {
            settings.GuardBatteryCarePauseUntilUtcTicks = guardTicks;
            return;
        }

        if (string.Equals(key, "NetworkMonitorLeftDockTabCenterY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.NetworkMonitorLeftDockTabCenterY = intValue;
            return;
        }

        if (string.Equals(key, "LeftDockCollapseSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.LeftDockCollapseSeconds = intValue;
            return;
        }

        if (string.Equals(key, "LeftDockOutsideClickCollapseEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.LeftDockOutsideClickCollapseEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexTaskBoardWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.CodexTaskBoardWidth = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskBoardHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.CodexTaskBoardHeight = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskBoardTimelineMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.CodexTaskBoardTimelineMinutes = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskBoardView", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexTaskBoardView = (CodexTaskBoardView)Enum.Parse(typeof(CodexTaskBoardView), value, true);
            }
            catch
            {
                settings.CodexTaskBoardView = CodexTaskBoardView.Table;
            }

            return;
        }

        if (string.Equals(key, "CodexTaskMonitorEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexTaskMonitorEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexTaskMonitorActiveWindowMinutes", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskMonitorActiveWindowMinutes = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskMonitorActiveSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskMonitorActiveSeconds = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskMonitorIdleSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskMonitorIdleSeconds = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskMonitorTerminalHoldSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskMonitorTerminalHoldSeconds = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskMonitorErrorHoldSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskMonitorErrorHoldSeconds = intValue;
            return;
        }

        if (string.Equals(key, "CodexTaskMonitorNumberCooldownSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexTaskMonitorNumberCooldownSeconds = intValue;
            return;
        }

        if (string.Equals(key, "OperationButtonSize", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationButtonSize = intValue;
            return;
        }

        if (string.Equals(key, "OperationLeftOffset", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationLeftOffset = intValue;
            return;
        }

        if (string.Equals(key, "OperationBottomOffset", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationBottomOffset = intValue;
            return;
        }

        if ((string.Equals(key, "OperationBackgroundTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "OperationTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.OperationBackgroundTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "OperationPrimaryPanelMode", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
        {
            try
            {
                settings.OperationPrimaryPanelMode = (OperationPrimaryPanelMode)Enum.Parse(typeof(OperationPrimaryPanelMode), value, true);
            }
            catch
            {
                settings.OperationPrimaryPanelMode = OperationPrimaryPanelMode.Auto;
            }

            return;
        }

        if (string.Equals(key, "OperationWindowsButtonEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationWindowsButtonEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "OperationMemoryPieEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationMemoryPieEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ForceShowForegroundFpsEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ForceShowForegroundFpsEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "SeelenDockForegroundPulseEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.SeelenDockForegroundPulseEnabled = boolValue;
            return;
        }

        if ((string.Equals(key, "WinDRecoveryPulseEnabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "CtrlDRecoveryPulseEnabled", StringComparison.OrdinalIgnoreCase)) &&
            bool.TryParse(value, out boolValue))
        {
            // Accept the old Ctrl+D key so upgrades preserve the user's existing opt-out.
            settings.WinDRecoveryPulseEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "PowerResumeRestartEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerResumeRestartEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "FallbackDisconnectedDisplaysEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.FallbackDisconnectedDisplaysEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "MainDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.MainDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "OperationDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.OperationDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "CodexPetZOrderProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexPetZOrderProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ResolutionCompatibilityModeEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ResolutionCompatibilityModeEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ResolutionCompatibilityScalePercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ResolutionCompatibilityScalePercent = intValue;
            return;
        }

        if (string.Equals(key, "LayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.LayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "LayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.LayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "LayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.LayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "LayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.LayoutWorkAreaHeight = intValue;
            return;
        }

        if (string.Equals(key, "OperationLayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationLayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "OperationLayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationLayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "OperationLayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationLayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "OperationLayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationLayoutWorkAreaHeight = intValue;
            return;
        }

        if (string.Equals(key, "StartupEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.StartupEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "SensitiveMouseModeEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.SensitiveMouseModeEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "SensitiveMouseRangePixels", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.SensitiveMouseRangePixels = intValue;
            return;
        }

        if (string.Equals(key, "HoverOpacityRevealDelayEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.HoverOpacityRevealDelayEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "HoverOpacityRevealDelaySeconds", StringComparison.OrdinalIgnoreCase) &&
            TryParseDouble(value, out doubleValue))
        {
            settings.HoverOpacityRevealDelaySeconds = doubleValue;
            return;
        }

        if (string.Equals(key, "HoverOpacityRevealResetSeconds", StringComparison.OrdinalIgnoreCase) &&
            TryParseDouble(value, out doubleValue))
        {
            settings.HoverOpacityRevealResetSeconds = doubleValue;
            return;
        }

        if (string.Equals(key, "HoverOpacityCoverEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.HoverOpacityCoverEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ReverseHoverOpacityRevealEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ReverseHoverOpacityRevealEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ReverseHoverOpacityRestoreDelaySeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ReverseHoverOpacityRestoreDelaySeconds = intValue;
            return;
        }

        if (string.Equals(key, "AlertTestEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AlertTestEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ThermalTestMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.ThermalTestMode = (ThermalTestMode)Enum.Parse(typeof(ThermalTestMode), value, true);
            }
            catch
            {
                settings.ThermalTestMode = ThermalTestMode.Off;
            }

            return;
        }

        if (string.Equals(key, "CodexRadarTestMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexRadarTestMode = (CodexRadarTestMode)Enum.Parse(typeof(CodexRadarTestMode), value, true);
            }
            catch
            {
                settings.CodexRadarTestMode = CodexRadarTestMode.Off;
            }

            return;
        }

        if (string.Equals(key, "ServiceHealthTestMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.ServiceHealthTestMode = (ServiceHealthTestMode)Enum.Parse(typeof(ServiceHealthTestMode), value, true);
            }
            catch
            {
                settings.ServiceHealthTestMode = ServiceHealthTestMode.Off;
            }

            return;
        }

        if (string.Equals(key, "CodexRadarRandomTestEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarRandomTestEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "MainWidgetTileLargeModeEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.MainWidgetTileLargeModeEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "MetricTileExpandWidth", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.MetricTileExpandWidth = intValue;
            return;
        }

        if (string.Equals(key, "MetricTileExpandHeight", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.MetricTileExpandHeight = intValue;
            return;
        }

        if (string.Equals(key, "OperationRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            // Classic and the four historical OLED names intentionally fold to the only retained
            // interaction model. Unknown/numeric legacy values follow the same compatibility path.
            settings.OperationRenderVariant = OperationRenderVariant.RadialDial;
            return;
        }

        if (string.Equals(key, "CodexRadarRandomTestAutoRefresh", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarRandomTestAutoRefresh = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarRandomTestRefreshToken", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarRandomTestRefreshToken = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarPublicJsonEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarPublicJsonEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarHtmlFallbackEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarHtmlFallbackEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarRssFallbackEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarRssFallbackEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaDueResetProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaDueResetProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaRssResetProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaRssResetProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaProviderZeroDropProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaProviderZeroDropProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaDuplicateSameBalanceRingProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaDuplicateSameBalanceRingProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaProviderWeeklySpikeProtectionEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaProviderWeeklySpikeProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaStrictFiveHourResetBoundaryEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaStrictFiveHourResetBoundaryEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaWeeklyBaselineAutoRepairEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaWeeklyBaselineAutoRepairEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarServiceProbeToken", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarServiceProbeToken = intValue;
            return;
        }

        if (string.Equals(key, "AiRequestProtectionAutoEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.AiRequestProtectionAutoEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "AiRequestProtectionManualBlockEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.AiRequestProtectionManualBlockEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "AiChinaEgressGuardEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.AiChinaEgressGuardEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaPlanEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaPlanEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaPlanWeeklyComparison", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexQuotaPlanWeeklyComparison =
                    (CodexQuotaPlanComparison)Enum.Parse(typeof(CodexQuotaPlanComparison), value, true);
            }
            catch
            {
                settings.CodexQuotaPlanWeeklyComparison = CodexQuotaPlanComparison.LessThan;
            }

            return;
        }

        if (string.Equals(key, "CodexQuotaPlanWeeklyThresholdPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexQuotaPlanWeeklyThresholdPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaPlanFiveHourComparison", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexQuotaPlanFiveHourComparison =
                    (CodexQuotaPlanComparison)Enum.Parse(typeof(CodexQuotaPlanComparison), value, true);
            }
            catch
            {
                settings.CodexQuotaPlanFiveHourComparison = CodexQuotaPlanComparison.LessThan;
            }

            return;
        }

        if (string.Equals(key, "CodexQuotaPlanFiveHourThresholdPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexQuotaPlanFiveHourThresholdPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaPlanResumeConditionMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexQuotaPlanResumeConditionMode =
                    (CodexQuotaPlanResumeConditionMode)Enum.Parse(typeof(CodexQuotaPlanResumeConditionMode), value, true);
            }
            catch
            {
                settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.Both;
            }

            return;
        }

        if (string.Equals(key, "CodexQuotaPlanAutoResumePausedGoals", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexQuotaPlanAutoResumePausedGoals = boolValue;
            return;
        }

        if (string.Equals(key, "CodexQuotaPlanPauseGoalIds", StringComparison.OrdinalIgnoreCase))
        {
            settings.CodexQuotaPlanPauseGoalIds = value ?? string.Empty;
            return;
        }

        if (string.Equals(key, "CodexQuotaPlanResumeGoalIds", StringComparison.OrdinalIgnoreCase))
        {
            settings.CodexQuotaPlanResumeGoalIds = value ?? string.Empty;
            return;
        }

        if (string.Equals(key, "CleanIpBadgeTestMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CleanIpBadgeTestMode = (CleanIpBadgeTestMode)Enum.Parse(typeof(CleanIpBadgeTestMode), value, true);
            }
            catch
            {
                settings.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
            }

            return;
        }

        if (string.Equals(key, "CodexModelIqTestEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexModelIqTestEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexModelIqTestPassed", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelIqTestPassed = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelIqBaselineAutoEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexModelIqBaselineAutoEnabled = boolValue;
            return;
        }

        if ((string.Equals(key, "CodexModelIqBaselinePassed", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "CodexModelIqBaseline", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexModelIqBaselinePassed = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelIqBaselineValidTasks", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelIqBaselineValidTasks = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelIqBaselineMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexModelIqBaselineMode =
                    (CodexModelBaselineMode)Enum.Parse(typeof(CodexModelBaselineMode), value, true);
            }
            catch
            {
                settings.CodexModelIqBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
            }

            return;
        }

        if (string.Equals(key, "CodexModelEfficiencyTestEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexModelEfficiencyTestEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexModelTokenEfficiencyTestPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTokenEfficiencyTestPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTimeEfficiencyTestPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTimeEfficiencyTestPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTokenEfficiencyBaselinePassed", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTokenEfficiencyBaselinePassed = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTokenEfficiencyBaselineTokens", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTokenEfficiencyBaselineTokens = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTokenEfficiencyBaselineMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexModelTokenEfficiencyBaselineMode =
                    (CodexModelBaselineMode)Enum.Parse(typeof(CodexModelBaselineMode), value, true);
            }
            catch
            {
                settings.CodexModelTokenEfficiencyBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
            }

            return;
        }

        if (string.Equals(key, "CodexModelTimeEfficiencyBaselinePassed", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTimeEfficiencyBaselinePassed = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTimeEfficiencyBaselineSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTimeEfficiencyBaselineSeconds = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTimeEfficiencyBaselineMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexModelTimeEfficiencyBaselineMode =
                    (CodexModelBaselineMode)Enum.Parse(typeof(CodexModelBaselineMode), value, true);
            }
            catch
            {
                settings.CodexModelTimeEfficiencyBaselineMode = CodexModelBaselineMode.AllRecordsAverage;
            }

            return;
        }

        if (string.Equals(key, "CodexModelTokenEfficiencyLowThresholdPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTokenEfficiencyLowThresholdPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexModelTimeEfficiencyLowThresholdPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexModelTimeEfficiencyLowThresholdPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarModelVersion", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexRadarModelVersion =
                    (CodexRadarModelVersion)Enum.Parse(typeof(CodexRadarModelVersion), value, true);
            }
            catch
            {
                settings.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55;
            }

            return;
        }

        if (string.Equals(key, "CodexRadarModelKey", StringComparison.OrdinalIgnoreCase))
        {
            settings.CodexRadarModelKey = CodexRadarModelCatalog.NormalizeModelKey(value);
            return;
        }

        if (string.Equals(key, "CodexRadarSoftwareMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexRadarSoftwareMode =
                    (CodexRadarSoftwareMode)Enum.Parse(typeof(CodexRadarSoftwareMode), value, true);
            }
            catch
            {
                settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Auto;
            }

            return;
        }

        if (string.Equals(key, "RadarClockAutoSwitchModelEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.RadarClockAutoSwitchModelEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarSpeedWindowCountdownEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarSpeedWindowCountdownEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarQuotaResetRainbowEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarQuotaResetRainbowEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "RadarClockTimeDisplayMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.RadarClockTimeDisplayMode =
                    (RadarClockTimeDisplayMode)Enum.Parse(typeof(RadarClockTimeDisplayMode), value, true);
            }
            catch
            {
                settings.RadarClockTimeDisplayMode = RadarClockTimeDisplayMode.Utc;
            }

            return;
        }

        if (string.Equals(key, "DisplayTimeZoneMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.DisplayTimeZoneMode =
                    (DisplayTimeZoneMode)Enum.Parse(typeof(DisplayTimeZoneMode), value, true);
            }
            catch
            {
                settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Automatic;
            }

            return;
        }

        if (string.Equals(key, "DisplayTimeZoneId", StringComparison.OrdinalIgnoreCase))
        {
            settings.DisplayTimeZoneId = value;
            return;
        }

        if (string.Equals(key, "PowerSavingEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerSavingEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "PerformanceMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.PerformanceMode = (WidgetPerformanceMode)Enum.Parse(typeof(WidgetPerformanceMode), value, true);
            }
            catch
            {
                settings.PerformanceMode = WidgetPerformanceMode.Balanced;
            }

            return;
        }

        if (string.Equals(key, "HoverOpacityEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.HoverOpacityEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "AutoHoverOpacityIdleEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AutoHoverOpacityIdleEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "AutoHoverOpacityIdleSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.AutoHoverOpacityIdleSeconds = intValue;
            return;
        }

        if (string.Equals(key, "AutoHoverOpacityMaximizedEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AutoHoverOpacityMaximizedEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "BurnInProtectionEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.BurnInProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "BurnInLevelOneIdleSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.BurnInLevelOneIdleSeconds = intValue;
            return;
        }

        if (string.Equals(key, "BurnInLevelTwoDelaySeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.BurnInLevelTwoDelaySeconds = intValue;
            return;
        }

        if (string.Equals(key, "OperationRadialCoreAutoHideKeepAliveEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationRadialCoreAutoHideKeepAliveEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "OperationRadialIdleCollapseSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.OperationRadialIdleCollapseSeconds = intValue;
            return;
        }

        if (string.Equals(key, "OperationRadialIdleResetOnInteractionEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationRadialIdleResetOnInteractionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "OperationRadialKeepOpenAfterLeafClickEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationRadialKeepOpenAfterLeafClickEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "OperationDoubleClickSpecialMenuEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationDoubleClickSpecialMenuEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "OperationSettingsLogicExtensionEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationSettingsLogicExtensionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "MetricTileLeftX", StringComparison.OrdinalIgnoreCase))
        {
            settings.MetricTileLeftX = ParseTileArray(value);
            return;
        }

        if (string.Equals(key, "MetricTileBottomY", StringComparison.OrdinalIgnoreCase))
        {
            settings.MetricTileBottomY = ParseTileArray(value);
            return;
        }

        if (string.Equals(key, "RightTileAutoArrangeEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.RightTileAutoArrangeEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "RightTileButtonOrder", StringComparison.OrdinalIgnoreCase))
        {
            settings.RightTileButtonOrder = NormalizeRightTileButtonOrder(
                value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        if (string.Equals(key, "RightTileButtonGapPixels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.RightTileButtonGapPixels = intValue;
            return;
        }

        if (string.Equals(key, "RightTileGroupOffsetY", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            settings.RightTileGroupOffsetY = intValue;
            return;
        }

        if (string.Equals(key, "VisibilityMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.VisibilityMode = (WidgetVisibilityMode)Enum.Parse(typeof(WidgetVisibilityMode), value, true);
            }
            catch
            {
            }
        }

        if (string.Equals(key, "VisibilityOverlapIgnoresOperationPanelEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.VisibilityOverlapIgnoresOperationPanelEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClickThroughMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.ClickThroughMode = (ClickThroughMode)Enum.Parse(typeof(ClickThroughMode), value, true);
            }
            catch
            {
                settings.ClickThroughMode = ClickThroughMode.Auto;
            }
        }
    }

    public bool AdaptToCurrentWorkArea()
    {
        Rectangle primaryWorkArea = Screen.PrimaryScreen.WorkingArea;
        EnsureUsableWorkArea(ref primaryWorkArea);
        EnsureAllLayoutWorkAreaReferences(primaryWorkArea);

        Rectangle mainWorkArea = GetWorkAreaForModule(ModuleMain);
        Rectangle previousMainWorkArea = GetModuleLayoutWorkArea(ModuleMain);
        bool changed = !SameWorkArea(previousMainWorkArea, mainWorkArea);
        if (changed)
        {
            CaptureModuleLayoutWorkArea(ModuleMain, mainWorkArea);
        }

        changed |= AdaptOperationToWorkArea(GetWorkAreaForModule(ModuleOperation));
        return changed;
    }

    internal bool AdaptToWorkArea(Rectangle currentWorkArea)
    {
        EnsureUsableWorkArea(ref currentWorkArea);
        EnsureAllLayoutWorkAreaReferences(currentWorkArea);

        Rectangle previousWorkArea = new Rectangle(
            this.LayoutWorkAreaLeft,
            this.LayoutWorkAreaTop,
            this.LayoutWorkAreaWidth,
            this.LayoutWorkAreaHeight);
        EnsureUsableWorkArea(ref previousWorkArea);
        if (previousWorkArea.Left == currentWorkArea.Left &&
            previousWorkArea.Top == currentWorkArea.Top &&
            previousWorkArea.Width == currentWorkArea.Width &&
            previousWorkArea.Height == currentWorkArea.Height)
        {
            return false;
        }

        double scaleX = currentWorkArea.Width / (double)Math.Max(1, previousWorkArea.Width);
        double scaleY = currentWorkArea.Height / (double)Math.Max(1, previousWorkArea.Height);
        double uniformScale = Math.Min(scaleX, scaleY);

        this.OperationButtonSize = Clamp(
            RoundScaled(this.OperationButtonSize, uniformScale),
            MinOperationButtonSize,
            MaxOperationButtonSize);
        this.OperationLeftOffset = Clamp(RoundScaled(this.OperationLeftOffset, scaleX), MinOperationOffset, MaxOperationOffset);
        this.OperationBottomOffset = Clamp(RoundScaled(this.OperationBottomOffset, scaleY), MinOperationOffset, MaxOperationOffset);

        CaptureLayoutWorkArea(currentWorkArea);
        CaptureModuleLayoutWorkArea(ModuleOperation, currentWorkArea);
        ClampOperationLayoutToWorkArea(currentWorkArea, GetPrimaryScale());
        return true;
    }

    public Rectangle GetWorkAreaForModule(string moduleId)
    {
        Rectangle workArea;
        if (TryResolveModuleWorkArea(moduleId, out workArea))
        {
            return workArea;
        }

        workArea = GetModuleLayoutWorkArea(moduleId);
        EnsureUsableWorkArea(ref workArea);
        return workArea;
    }

    private static void ApplyCodexRadarDefaultModelMigration(WidgetSettings settings)
    {
        // Only move the previous product default. Explicit selections of any other archived
        // model remain untouched and continue through the dynamic availability policy.
        if (string.Equals(
            CodexRadarModelCatalog.NormalizeModelKey(settings.CodexRadarModelKey),
            CodexRadarModelCatalog.PreviousDefaultModelKey,
            StringComparison.OrdinalIgnoreCase))
        {
            settings.CodexRadarModelKey = CodexRadarModelCatalog.DefaultModelKey;
        }
    }

    public float GetResolutionCompatibilityScaleFactor()
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return 1.0f;
        }

        int percent = Clamp(
            this.ResolutionCompatibilityScalePercent,
            MinResolutionCompatibilityScalePercent,
            MaxResolutionCompatibilityScalePercent);
        return Math.Max(0.01f, percent / 100.0f);
    }

    public int ScaleResolutionCompatibilityPixels(int logicalPixels)
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return logicalPixels;
        }

        return Math.Max(1, RoundScaled(logicalPixels, GetResolutionCompatibilityScaleFactor()));
    }

    public int ScaleResolutionCompatibilityOffset(int logicalPixels)
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return logicalPixels;
        }

        return Math.Max(0, RoundScaled(logicalPixels, GetResolutionCompatibilityScaleFactor()));
    }

    public Size ScaleResolutionCompatibilitySize(Size logicalSize)
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return logicalSize;
        }

        return new Size(
            Math.Max(1, RoundScaled(logicalSize.Width, GetResolutionCompatibilityScaleFactor())),
            Math.Max(1, RoundScaled(logicalSize.Height, GetResolutionCompatibilityScaleFactor())));
    }

    public int MapResolutionCompatibilityLeft(string moduleId, Rectangle targetWorkArea, int logicalLeftX)
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return logicalLeftX;
        }

        // Saved coordinates are absolute in the frame of the module's captured reference work
        // area (the same base ScalePanelLayout re-bases from). Subtract the reference origin
        // before projecting so a left/top taskbar or a non-zero-origin target display does not
        // get counted twice.
        Rectangle reference = GetModuleLayoutWorkArea(moduleId);
        return targetWorkArea.Left + RoundScaled(logicalLeftX - reference.Left, GetResolutionCompatibilityScaleFactor());
    }

    public int MapResolutionCompatibilityBottom(string moduleId, Rectangle targetWorkArea, int logicalBottomY)
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return logicalBottomY;
        }

        Rectangle reference = GetModuleLayoutWorkArea(moduleId);
        return targetWorkArea.Top + RoundScaled(logicalBottomY - reference.Top, GetResolutionCompatibilityScaleFactor());
    }

    private bool AdaptOperationToWorkArea(Rectangle currentWorkArea)
    {
        EnsureUsableWorkArea(ref currentWorkArea);
        Rectangle previousWorkArea = GetModuleLayoutWorkArea(ModuleOperation);
        if (SameWorkArea(previousWorkArea, currentWorkArea))
        {
            ClampOperationLayoutToWorkArea(currentWorkArea, GetPrimaryScale());
            return false;
        }

        double scaleX = currentWorkArea.Width / (double)Math.Max(1, previousWorkArea.Width);
        double scaleY = currentWorkArea.Height / (double)Math.Max(1, previousWorkArea.Height);
        double uniformScale = Math.Min(scaleX, scaleY);
        this.OperationButtonSize = Clamp(
            RoundScaled(this.OperationButtonSize, uniformScale),
            MinOperationButtonSize,
            MaxOperationButtonSize);
        this.OperationLeftOffset = Clamp(RoundScaled(this.OperationLeftOffset, scaleX), MinOperationOffset, MaxOperationOffset);
        this.OperationBottomOffset = Clamp(RoundScaled(this.OperationBottomOffset, scaleY), MinOperationOffset, MaxOperationOffset);
        CaptureModuleLayoutWorkArea(ModuleOperation, currentWorkArea);
        ClampOperationLayoutToWorkArea(currentWorkArea, GetPrimaryScale());
        return true;
    }

    private void ClampLayoutToWorkArea(Rectangle workArea, float scale)
    {
        EnsureUsableWorkArea(ref workArea);
        ClampOperationLayoutToWorkArea(workArea, scale);
    }

    private void ClampLayoutToTargetWorkAreas(float scale)
    {
        ClampOperationLayoutToWorkArea(GetWorkAreaForModule(ModuleOperation), scale);
    }

    private void ClampOperationLayoutToWorkArea(Rectangle workArea, float scale)
    {
        EnsureUsableWorkArea(ref workArea);
        int operationMaxLeftOffset = Math.Max(0, workArea.Width - GetOperationWindowWidth(this.OperationButtonSize, scale));
        int operationMaxBottomOffset = Math.Max(0, workArea.Height - GetOperationWindowHeight(this.OperationButtonSize, scale));
        this.OperationLeftOffset = Clamp(this.OperationLeftOffset, MinOperationOffset, Math.Min(MaxOperationOffset, operationMaxLeftOffset));
        this.OperationBottomOffset = Clamp(this.OperationBottomOffset, MinOperationOffset, Math.Min(MaxOperationOffset, operationMaxBottomOffset));
    }

    private static int RoundScaled(int value, double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0)
        {
            return value;
        }

        return (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
    }

    private bool HasLayoutWorkAreaReference()
    {
        return this.LayoutWorkAreaWidth > 0 && this.LayoutWorkAreaHeight > 0;
    }

    private static bool SameWorkArea(Rectangle left, Rectangle right)
    {
        EnsureUsableWorkArea(ref left);
        EnsureUsableWorkArea(ref right);
        return left.Left == right.Left &&
            left.Top == right.Top &&
            left.Width == right.Width &&
            left.Height == right.Height;
    }

    private void EnsureLayoutWorkAreaReference(Rectangle workArea)
    {
        if (!HasLayoutWorkAreaReference())
        {
            CaptureLayoutWorkArea(workArea);
        }
    }

    private void EnsureAllLayoutWorkAreaReferences(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        EnsureLayoutWorkAreaReference(workArea);
        EnsureModuleLayoutWorkAreaReference(ModuleOperation, workArea);
    }

    private void EnsureModuleLayoutWorkAreaReference(string moduleId, Rectangle workArea)
    {
        Rectangle reference = GetModuleLayoutWorkArea(moduleId);
        if (reference.Width <= 0 || reference.Height <= 0)
        {
            CaptureModuleLayoutWorkArea(moduleId, workArea);
        }
    }

    private void CaptureCurrentWorkArea()
    {
        CaptureModuleLayoutWorkArea(ModuleMain, GetWorkAreaForModule(ModuleMain));
        CaptureModuleLayoutWorkArea(ModuleOperation, GetWorkAreaForModule(ModuleOperation));
    }

    private void CaptureLayoutWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.LayoutWorkAreaLeft = workArea.Left;
        this.LayoutWorkAreaTop = workArea.Top;
        this.LayoutWorkAreaWidth = Math.Max(1, workArea.Width);
        this.LayoutWorkAreaHeight = Math.Max(1, workArea.Height);
    }

    private void CaptureModuleLayoutWorkArea(string moduleId, Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        if (string.Equals(moduleId, ModuleMain, StringComparison.Ordinal))
        {
            CaptureLayoutWorkArea(workArea);
            return;
        }

        int left = workArea.Left;
        int top = workArea.Top;
        int width = Math.Max(1, workArea.Width);
        int height = Math.Max(1, workArea.Height);
        if (string.Equals(moduleId, ModuleOperation, StringComparison.Ordinal))
        {
            this.OperationLayoutWorkAreaLeft = left;
            this.OperationLayoutWorkAreaTop = top;
            this.OperationLayoutWorkAreaWidth = width;
            this.OperationLayoutWorkAreaHeight = height;
        }
    }

    private Rectangle GetModuleLayoutWorkArea(string moduleId)
    {
        Rectangle workArea;
        if (string.Equals(moduleId, ModuleOperation, StringComparison.Ordinal))
        {
            workArea = new Rectangle(
                this.OperationLayoutWorkAreaLeft,
                this.OperationLayoutWorkAreaTop,
                this.OperationLayoutWorkAreaWidth,
                this.OperationLayoutWorkAreaHeight);
        }
        else
        {
            workArea = new Rectangle(
                this.LayoutWorkAreaLeft,
                this.LayoutWorkAreaTop,
                this.LayoutWorkAreaWidth,
                this.LayoutWorkAreaHeight);
        }

        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            workArea = new Rectangle(
                this.LayoutWorkAreaLeft,
                this.LayoutWorkAreaTop,
                this.LayoutWorkAreaWidth,
                this.LayoutWorkAreaHeight);
        }

        EnsureUsableWorkArea(ref workArea);
        return workArea;
    }

    private bool TryResolveModuleWorkArea(string moduleId, out Rectangle workArea)
    {
        string displayDeviceName = GetModuleDisplayDeviceName(moduleId);
        return TryResolveDisplayWorkArea(displayDeviceName, this.FallbackDisconnectedDisplaysEnabled, out workArea);
    }

    private string GetModuleDisplayDeviceName(string moduleId)
    {
        if (string.Equals(moduleId, ModuleOperation, StringComparison.Ordinal))
        {
            return this.OperationDisplayDeviceName;
        }

        return this.MainDisplayDeviceName;
    }

    public static string NormalizeDisplayDeviceName(string displayDeviceName)
    {
        return (displayDeviceName ?? string.Empty).Trim();
    }

    public static string NormalizeGoalIdList(string goalIds)
    {
        if (string.IsNullOrWhiteSpace(goalIds))
        {
            return string.Empty;
        }

        string[] parts = goalIds.Replace("\r", "|").Replace("\n", "|").Split(new char[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> normalized = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < parts.Length; i++)
        {
            string value = (parts[i] ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 160)
            {
                continue;
            }

            bool safe = true;
            for (int c = 0; c < value.Length; c++)
            {
                char ch = value[c];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ':' || ch == '.'))
                {
                    safe = false;
                    break;
                }
            }

            if (!safe || seen.Contains(value))
            {
                continue;
            }

            seen.Add(value);
            normalized.Add(value);
        }

        return string.Join("|", normalized.ToArray());
    }

    private static bool TryResolveDisplayWorkArea(string displayDeviceName, bool fallbackToAvailableDisplay, out Rectangle workArea)
    {
        string normalized = NormalizeDisplayDeviceName(displayDeviceName);
        Screen[] screens = Screen.AllScreens;
        if (screens == null || screens.Length == 0)
        {
            workArea = Screen.PrimaryScreen.WorkingArea;
            EnsureUsableWorkArea(ref workArea);
            return true;
        }

        if (normalized.Length == 0)
        {
            workArea = Screen.PrimaryScreen.WorkingArea;
            EnsureUsableWorkArea(ref workArea);
            return true;
        }

        for (int i = 0; i < screens.Length; i++)
        {
            if (string.Equals(screens[i].DeviceName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                workArea = screens[i].WorkingArea;
                EnsureUsableWorkArea(ref workArea);
                return true;
            }
        }

        if (fallbackToAvailableDisplay)
        {
            workArea = Screen.PrimaryScreen.WorkingArea;
            EnsureUsableWorkArea(ref workArea);
            return true;
        }

        workArea = Rectangle.Empty;
        return false;
    }

    private static void EnsureUsableWorkArea(ref Rectangle workArea)
    {
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            return;
        }

        workArea = Screen.PrimaryScreen.Bounds;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            workArea = new Rectangle(0, 0, 1920, 1080);
        }
    }

    internal static void RunLayoutScalingSelfTest()
    {
        WidgetSettings settings = CreateDefaults();
        settings.OperationButtonSize = 64;
        settings.OperationLeftOffset = 12;
        settings.OperationBottomOffset = 14;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 0;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1700;
        settings.OperationLayoutWorkAreaLeft = 0;
        settings.OperationLayoutWorkAreaTop = 0;
        settings.OperationLayoutWorkAreaWidth = 2880;
        settings.OperationLayoutWorkAreaHeight = 1700;

        Rectangle current = new Rectangle(0, 0, 1920, 1080);
        AssertLayout(settings.AdaptToWorkArea(current), "first adaptation should change layout");
        AssertLayout(settings.LayoutWorkAreaWidth == 1920 && settings.LayoutWorkAreaHeight == 1080, "layout reference should update");
        AssertLayout(settings.OperationLayoutWorkAreaWidth == 1920 && settings.OperationLayoutWorkAreaHeight == 1080, "operation layout reference should update");
        AssertLayout(settings.OperationButtonSize == 41, "operation button uniform scale");
        AssertLayout(settings.OperationLeftOffset == 8, "operation left offset ratio");
        AssertLayout(settings.OperationBottomOffset == 9, "operation bottom offset ratio");
        AssertLayout(!settings.AdaptToWorkArea(current), "same work area should not change layout");

        Rectangle shifted = new Rectangle(100, 40, 1600, 900);
        AssertLayout(settings.AdaptToWorkArea(shifted), "shifted adaptation should change layout");
        AssertLayout(settings.LayoutWorkAreaLeft == 100 && settings.LayoutWorkAreaTop == 40, "shifted layout reference should update");
        AssertLayout(settings.OperationButtonSize == MinOperationButtonSize, "operation size should respect the minimum clamp");

        settings.MetricTileExpandWidth = 1;
        settings.MetricTileExpandHeight = int.MaxValue;
        settings.Normalize();
        AssertLayout(
            settings.MetricTileExpandWidth == MinMetricTileExpandWidth &&
            settings.MetricTileExpandHeight == MaxMetricTileExpandHeight,
            "metric expand size should normalize to its dedicated bounds");
    }

    internal static void RunCompatibilitySelfTest()
    {
        RunColumnArrangementSettingsSelfTest();
        RunRetiredSettingsSchema85MigrationSelfTest();
        RunRetiredSettingsSchema86MigrationSelfTest();
        RunChinaEgressGuardSchema87MigrationSelfTest();
        RunHiddenColorProtectionSchema88MigrationSelfTest();
        RunBurnInProtectionSchema89MigrationSelfTest();
        RunColumnSpacingSchema90MigrationSelfTest();
        RunCodexTaskMonitorSettingsSelfTest();
        RunSpecBoardSettingsSelfTest();
        RunCodexIqBoardSettingsSelfTest();
        RunWindowTransparencyOverrideSelfTest();
        RunWindowScaleOverrideSelfTest();
        RunGuardBoardOverrideMigrationSelfTest();
        RunNetworkDockOverrideMigrationSelfTest();
        GlobalHotkeyParser.RunSelfTest();
        WidgetSettings legacy = CreateDefaults();
        AssertLayout(!legacy.OperationDoubleClickSpecialMenuEnabled, "operation double-click special menu should default off");
        ApplyValue(legacy, "OperationDoubleClickSpecialMenuEnabled", "True");
        AssertLayout(legacy.OperationDoubleClickSpecialMenuEnabled, "operation double-click special menu should parse true");
        ApplyValue(legacy, "OperationDoubleClickSpecialMenuEnabled", "False");
        AssertLayout(!legacy.OperationDoubleClickSpecialMenuEnabled, "operation double-click special menu should parse false");
        string[] legacyOperationVariants = { "Classic", "Typographic", "AmberHud", "WarmCard", "Phosphor", "UnknownStyle", "999" };
        for (int i = 0; i < legacyOperationVariants.Length; i++)
        {
            ApplyValue(legacy, "OperationRenderVariant", legacyOperationVariants[i]);
            AssertLayout(
                legacy.OperationRenderVariant == OperationRenderVariant.RadialDial,
                "legacy Operation render variant should fold to RadialDial: " + legacyOperationVariants[i]);
        }

        string legacyOperationRoot = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-operation-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacyOperationRoot);
        try
        {
            string inputPath = Path.Combine(legacyOperationRoot, "settings.ini");
            string savedPath = Path.Combine(legacyOperationRoot, "saved.ini");
            File.WriteAllLines(inputPath, new string[]
            {
                "Version=" + CurrentSettingsVersion.ToString(CultureInfo.InvariantCulture),
                "OperationRenderVariant=Classic"
            });
            WidgetSettings loadedLegacyOperation = LoadFromPath(inputPath, false);
            AssertLayout(
                loadedLegacyOperation.OperationRenderVariant == OperationRenderVariant.RadialDial,
                "legacy Operation Classic settings file should load as RadialDial");
            loadedLegacyOperation.SaveToPath(savedPath, false);
            AssertLayout(
                Array.Exists(
                    File.ReadAllLines(savedPath),
                    line => string.Equals(line, "OperationRenderVariant=RadialDial", StringComparison.Ordinal)),
                "legacy Operation Classic settings file should save back as RadialDial");
        }
        finally
        {
            try { Directory.Delete(legacyOperationRoot, true); }
            catch { }
        }

        ApplyValue(legacy, "CtrlDRecoveryPulseEnabled", "False");
        AssertLayout(!legacy.WinDRecoveryPulseEnabled, "legacy Ctrl+D setting should migrate to Win+D");

        ApplyValue(legacy, "WinDRecoveryPulseEnabled", "True");
        AssertLayout(legacy.WinDRecoveryPulseEnabled, "Win+D setting should override the migrated value");

        AssertLayout(legacy.CodexPetZOrderProtectionEnabled, "Codex pet z-order protection should default enabled");
        ApplyValue(legacy, "CodexPetZOrderProtectionEnabled", "False");
        AssertLayout(!legacy.CodexPetZOrderProtectionEnabled, "Codex pet z-order protection should parse false");
        ApplyValue(legacy, "CodexPetZOrderProtectionEnabled", "True");
        AssertLayout(legacy.CodexPetZOrderProtectionEnabled, "Codex pet z-order protection should parse true");

        ApplyValue(legacy, "CodexRadarSoftwareMode", "Claude");
        AssertLayout(legacy.CodexRadarSoftwareMode == CodexRadarSoftwareMode.Claude, "Codex Radar software mode should parse Claude");
        ApplyValue(legacy, "CodexRadarSoftwareMode", "InvalidMode");
        AssertLayout(legacy.CodexRadarSoftwareMode == CodexRadarSoftwareMode.Auto, "invalid Codex Radar software mode should normalize to Auto");
        ApplyValue(legacy, "RadarClockAutoSwitchModelEnabled", "False");
        AssertLayout(!legacy.RadarClockAutoSwitchModelEnabled, "radar clock auto-switch setting should parse false");
        ApplyValue(legacy, "RadarClockAutoSwitchModelEnabled", "True");
        AssertLayout(legacy.RadarClockAutoSwitchModelEnabled, "radar clock auto-switch setting should parse true");
        ApplyValue(legacy, "RadarClockTimeDisplayMode", "LastAttemptRefresh");
        AssertLayout(legacy.RadarClockTimeDisplayMode == RadarClockTimeDisplayMode.LastAttemptRefresh, "radar clock time display mode should parse last attempt");
        ApplyValue(legacy, "RadarClockTimeDisplayMode", "InvalidMode");
        AssertLayout(legacy.RadarClockTimeDisplayMode == RadarClockTimeDisplayMode.Utc, "invalid radar clock time display mode should normalize to UTC");
        AssertLayout(legacy.CodexRadarSpeedWindowCountdownEnabled, "speed-window countdown should default enabled");
        ApplyValue(legacy, "CodexRadarSpeedWindowCountdownEnabled", "False");
        AssertLayout(!legacy.CodexRadarSpeedWindowCountdownEnabled, "speed-window countdown setting should parse false");
        ApplyValue(legacy, "CodexRadarSpeedWindowCountdownEnabled", "True");
        AssertLayout(legacy.CodexRadarSpeedWindowCountdownEnabled, "speed-window countdown setting should parse true");
        AssertLayout(legacy.CodexRadarQuotaResetRainbowEnabled, "quota-reset rainbow should default enabled");
        ApplyValue(legacy, "CodexRadarQuotaResetRainbowEnabled", "False");
        AssertLayout(!legacy.CodexRadarQuotaResetRainbowEnabled, "quota-reset rainbow setting should parse false");
        ApplyValue(legacy, "CodexRadarQuotaResetRainbowEnabled", "True");
        AssertLayout(legacy.CodexRadarQuotaResetRainbowEnabled, "quota-reset rainbow setting should parse true");
        ApplyValue(legacy, "AiRequestProtectionAutoEnabled", "False");
        ApplyValue(legacy, "AiRequestProtectionManualBlockEnabled", "True");
        AssertLayout(
            !legacy.AiRequestProtectionAutoEnabled && legacy.AiRequestProtectionManualBlockEnabled,
            "AI request protection switches should load");
        AssertLayout(legacy.AiChinaEgressGuardEnabled, "China-egress AI guard should default enabled");
        ApplyValue(legacy, "AiChinaEgressGuardEnabled", "False");
        AssertLayout(!legacy.AiChinaEgressGuardEnabled, "China-egress AI guard setting should parse false");
        ApplyValue(legacy, "AiChinaEgressGuardEnabled", "True");
        AssertLayout(legacy.AiChinaEgressGuardEnabled, "China-egress AI guard setting should parse true");
        ApplyValue(legacy, "CodexQuotaPlanEnabled", "True");
        ApplyValue(legacy, "CodexQuotaPlanWeeklyComparison", "GreaterThan");
        ApplyValue(legacy, "CodexQuotaPlanWeeklyThresholdPercent", "103");
        ApplyValue(legacy, "CodexQuotaPlanFiveHourComparison", "LessThan");
        ApplyValue(legacy, "CodexQuotaPlanFiveHourThresholdPercent", "-4");
        ApplyValue(legacy, "CodexQuotaPlanResumeConditionMode", "FiveHourOnly");
        ApplyValue(legacy, "CodexQuotaPlanAutoResumePausedGoals", "False");
        ApplyValue(legacy, "CodexQuotaPlanPauseGoalIds", "thr_1|bad id|thr_1|thr-2");
        legacy.Normalize();
        AssertLayout(
            legacy.CodexQuotaPlanEnabled &&
            legacy.CodexQuotaPlanWeeklyComparison == CodexQuotaPlanComparison.GreaterThan &&
            legacy.CodexQuotaPlanWeeklyThresholdPercent == MaxCodexQuotaPlanThresholdPercent &&
            legacy.CodexQuotaPlanFiveHourThresholdPercent == MinCodexQuotaPlanThresholdPercent &&
            legacy.CodexQuotaPlanResumeConditionMode == CodexQuotaPlanResumeConditionMode.FiveHourOnly &&
            !legacy.CodexQuotaPlanAutoResumePausedGoals &&
            string.Equals(legacy.CodexQuotaPlanPauseGoalIds, "thr_1|thr-2", StringComparison.Ordinal),
            "Codex quota plan settings should load and normalize");

        WidgetSettings quotaProtections = CreateDefaults();
        AssertLayout(
            quotaProtections.CodexQuotaDueResetProtectionEnabled &&
            quotaProtections.CodexQuotaRssResetProtectionEnabled &&
            quotaProtections.CodexQuotaProviderZeroDropProtectionEnabled &&
            quotaProtections.CodexQuotaDuplicateSameBalanceRingProtectionEnabled &&
            !quotaProtections.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled &&
            !quotaProtections.CodexQuotaProviderWeeklySpikeProtectionEnabled &&
            !quotaProtections.CodexQuotaStrictFiveHourResetBoundaryEnabled &&
            !quotaProtections.CodexQuotaWeeklyBaselineAutoRepairEnabled,
            "Codex quota protection defaults should match advanced-option policy");
        ApplyValue(quotaProtections, "CodexQuotaProviderWeeklySpikeProtectionEnabled", "True");
        ApplyValue(quotaProtections, "CodexQuotaDueResetProtectionEnabled", "False");
        AssertLayout(
            quotaProtections.CodexQuotaProviderWeeklySpikeProtectionEnabled &&
            !quotaProtections.CodexQuotaDueResetProtectionEnabled,
            "Codex quota protection switches should parse booleans");

        WidgetSettings iqTenTask = CreateDefaults();
        iqTenTask.CodexModelIqBaselinePassed = PreviousTwelveTaskCodexModelIqBaselinePassed;
        iqTenTask.CodexModelIqTestPassed = PreviousTwelveTaskCodexModelIqBaselinePassed;
        ApplyCodexModelIqTenTaskMigration(iqTenTask);
        AssertLayout(
            iqTenTask.CodexModelIqBaselinePassed == DefaultCodexModelIqBaselinePassed &&
            iqTenTask.CodexModelIqTestPassed == DefaultCodexModelIqBaselinePassed,
            "codex model iq twelve-task defaults should migrate to ten-task defaults");

        WidgetSettings iqWebsiteScore = CreateDefaults();
        iqWebsiteScore.CodexModelIqBaselinePassed = PreviousTenTaskLocalCodexModelIqBaselinePassed;
        iqWebsiteScore.CodexModelIqTestPassed = PreviousTenTaskLocalCodexModelIqBaselinePassed;
        ApplyCodexModelIqWebsiteScoreMigration(iqWebsiteScore);
        AssertLayout(
            iqWebsiteScore.CodexModelIqBaselinePassed == DefaultCodexModelIqBaselinePassed &&
            iqWebsiteScore.CodexModelIqTestPassed == DefaultCodexModelIqBaselinePassed,
            "codex model iq website-score defaults should migrate from 6/10 to 7/10");

        WidgetSettings codex56Default = CreateDefaults();
        codex56Default.CodexRadarModelKey = CodexRadarModelCatalog.PreviousDefaultModelKey;
        ApplyCodexRadarDefaultModelMigration(codex56Default);
        AssertLayout(
            string.Equals(
                codex56Default.CodexRadarModelKey,
                CodexRadarModelCatalog.DefaultModelKey,
                StringComparison.OrdinalIgnoreCase),
            "Codex Radar previous default model should migrate to GPT-5.6 Sol medium");

        WidgetSettings emptyKeyLegacy = CreateDefaults();
        emptyKeyLegacy.CodexRadarModelKey = string.Empty;
        emptyKeyLegacy.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55;
        emptyKeyLegacy.Normalize();
        ApplyCodexRadarDefaultModelMigration(emptyKeyLegacy);
        AssertLayout(
            string.Equals(
                emptyKeyLegacy.CodexRadarModelKey,
                CodexRadarModelCatalog.DefaultModelKey,
                StringComparison.OrdinalIgnoreCase),
            "empty legacy model key should reach GPT-5.6 default after normalize + migration re-run");

        WidgetSettings iqClamp = CreateDefaults();
        ApplyValue(iqClamp, "CodexModelIqBaselinePassed", "12");
        ApplyValue(iqClamp, "CodexModelIqBaselineValidTasks", "12");
        ApplyValue(iqClamp, "CodexModelIqTestPassed", "12");
        ApplyValue(iqClamp, "CodexModelTokenEfficiencyBaselinePassed", "12");
        ApplyValue(iqClamp, "CodexModelTimeEfficiencyBaselinePassed", "12");
        iqClamp.Normalize();
        AssertLayout(
            iqClamp.CodexModelIqBaselinePassed == 12 &&
            iqClamp.CodexModelIqBaselineValidTasks == 12 &&
            iqClamp.CodexModelIqTestPassed == 12 &&
            iqClamp.CodexModelTokenEfficiencyBaselinePassed == 12 &&
            iqClamp.CodexModelTimeEfficiencyBaselinePassed == 12,
            "codex model iq passed settings should preserve manually configured task counts");

        ApplyValue(legacy, "SensitiveMouseRangePixels", "500");
        ApplyValue(legacy, "HoverOpacityRevealDelayEnabled", "False");
        ApplyValue(legacy, "ReverseHoverOpacityRestoreDelaySeconds", "0");
        ApplyValue(legacy, "HoverOpacityRevealDelaySeconds", "20");
        ApplyValue(legacy, "HoverOpacityRevealResetSeconds", "0.01");
        ApplyValue(legacy, "ResolutionCompatibilityModeEnabled", "True");
        ApplyValue(legacy, "ResolutionCompatibilityScalePercent", "250");
        legacy.Normalize();
        AssertLayout(
            legacy.SensitiveMouseRangePixels == MaxSensitiveMouseRangePixels,
            "sensitive mouse range should clamp high");
        AssertLayout(
            !legacy.HoverOpacityRevealDelayEnabled,
            "hover reveal delay enabled should load");
        AssertLayout(
            legacy.ReverseHoverOpacityRestoreDelaySeconds == MinReverseHoverOpacityRestoreDelaySeconds,
            "reverse reveal delay should clamp low");
        AssertLayout(
            Math.Abs(legacy.HoverOpacityRevealDelaySeconds - MaxHoverOpacityRevealDelaySeconds) < 0.001,
            "hover reveal delay should clamp high");
        AssertLayout(
            Math.Abs(legacy.HoverOpacityRevealResetSeconds - MinHoverOpacityRevealResetSeconds) < 0.001,
            "hover reveal reset should clamp low");
        AssertLayout(
            legacy.ResolutionCompatibilityModeEnabled &&
            legacy.ResolutionCompatibilityScalePercent == MaxResolutionCompatibilityScalePercent,
            "resolution compatibility settings should load and clamp");

        legacy.ResolutionCompatibilityScalePercent = 50;
        Size scaledSize = legacy.ScaleResolutionCompatibilitySize(new Size(200, 100));
        Rectangle targetWorkArea = new Rectangle(100, 50, 1920, 1080);
        AssertLayout(
            scaledSize.Width == 100 &&
            scaledSize.Height == 50 &&
            legacy.MapResolutionCompatibilityLeft(ModuleMain, targetWorkArea, 400) == 300 &&
            legacy.MapResolutionCompatibilityBottom(ModuleMain, targetWorkArea, 800) == 450,
            "resolution compatibility runtime projection should scale from the 2880x1800 reference origin");

        legacy.LayoutWorkAreaLeft = 48;
        legacy.LayoutWorkAreaTop = 20;
        legacy.LayoutWorkAreaWidth = 2832;
        legacy.LayoutWorkAreaHeight = 1780;
        AssertLayout(
            legacy.MapResolutionCompatibilityLeft(ModuleMain, targetWorkArea, 448) == 300 &&
            legacy.MapResolutionCompatibilityBottom(ModuleMain, targetWorkArea, 820) == 450,
            "resolution compatibility projection should re-base absolute coordinates on a non-zero reference origin");

        WidgetSettings display = CreateDefaults();
        ApplyValue(display, "FallbackDisconnectedDisplaysEnabled", "False");
        display.MainDisplayDeviceName = "DISPLAY-DOES-NOT-EXIST";
        display.LayoutWorkAreaLeft = 40;
        display.LayoutWorkAreaTop = 80;
        display.LayoutWorkAreaWidth = 900;
        display.LayoutWorkAreaHeight = 600;
        Rectangle missingDisplayWorkArea = display.GetWorkAreaForModule(ModuleMain);
        AssertLayout(
            string.Equals(display.MainDisplayDeviceName, "DISPLAY-DOES-NOT-EXIST", StringComparison.Ordinal),
            "display device name should trim");
        AssertLayout(
            missingDisplayWorkArea.Left == 40 &&
            missingDisplayWorkArea.Top == 80 &&
            missingDisplayWorkArea.Width == 900 &&
            missingDisplayWorkArea.Height == 600,
            "missing display without fallback should keep module work area");
    }

    internal static void RunFullRoundTripSelfTest()
    {
        Dictionary<string, string> saveLoadExemptions = CreateFullRoundTripSaveLoadExemptions();
        PropertyInfo[] properties = GetFullRoundTripProperties();
        WidgetSettings settings = CreateDefaults();

        for (int i = 0; i < properties.Length; i++)
        {
            if (!saveLoadExemptions.ContainsKey(properties[i].Name))
            {
                AssignFullRoundTripSentinel(settings, properties[i], i);
            }
        }

        ApplyFullRoundTripCoherentSentinels(settings);

        WidgetSettings clone = settings.Clone();
        AssertFullRoundTripEqual(settings, clone, properties, null, "Clone");

        string tempRoot = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-settings-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string savedPath = Path.Combine(tempRoot, "settings.ini");
            settings.SaveToPath(savedPath, true);
            AssertLayout(!File.Exists(savedPath + ".tmp"), "atomic settings save should not leave a temp file");
            WidgetSettings loaded = LoadFromPath(savedPath, false);
            AssertFullRoundTripEqual(settings, loaded, properties, saveLoadExemptions, "Save/Load");
            AssertFullRoundTripSaveCoverage(savedPath, properties, saveLoadExemptions);

            File.WriteAllText(savedPath + ".tmp", "damaged stale snapshot", SharedEncoding.Utf8NoBom);
            settings.SaveToPath(savedPath, true);
            AssertLayout(!File.Exists(savedPath + ".tmp"), "atomic settings save should replace and remove a stale temp file");
            WidgetSettings staleRecovered = LoadFromPath(savedPath, false);
            AssertFullRoundTripEqual(settings, staleRecovered, properties, saveLoadExemptions, "Stale temp recovery");

            string hashBeforeLockedSave = ComputeFileSha256(savedPath);
            bool lockedSaveFailed = false;
            using (FileStream lockedTarget = new FileStream(savedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    settings.SaveToPath(savedPath, true);
                }
                catch (IOException)
                {
                    lockedSaveFailed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    lockedSaveFailed = true;
                }
            }

            AssertLayout(lockedSaveFailed, "atomic settings save should fail while the target is exclusively locked");
            AssertLayout(!File.Exists(savedPath + ".tmp"), "failed atomic settings save should clean its temp file");
            AssertLayout(
                string.Equals(hashBeforeLockedSave, ComputeFileSha256(savedPath), StringComparison.Ordinal),
                "failed atomic settings save must leave the target bytes unchanged");

            string fixturePath = Path.Combine(
                Environment.CurrentDirectory,
                "_build",
                "spec-baseline",
                "settings-fixture.ini");
            if (File.Exists(fixturePath))
            {
                WidgetSettings fixture = LoadFromPath(fixturePath, false);
                string fixtureSavedPath = Path.Combine(tempRoot, "settings-fixture-saved.ini");
                fixture.SaveToPath(fixtureSavedPath, true);
                WidgetSettings fixtureReloaded = LoadFromPath(fixtureSavedPath, false);
                AssertFullRoundTripEqual(fixture, fixtureReloaded, properties, saveLoadExemptions, "Fixture Save/Load");
                Console.WriteLine("Settings fixture round-trip: PASS");
            }

            int persistedPropertyCount = 0;
            for (int i = 0; i < properties.Length; i++)
            {
                if (!saveLoadExemptions.ContainsKey(properties[i].Name))
                {
                    persistedPropertyCount++;
                }
            }

            Console.WriteLine(
                "Settings full round-trip: PASS " +
                persistedPropertyCount.ToString(CultureInfo.InvariantCulture) +
                " persisted properties (" +
                properties.Length.ToString(CultureInfo.InvariantCulture) +
                " supported public properties, " +
                saveLoadExemptions.Count.ToString(CultureInfo.InvariantCulture) +
                " explicit exemptions)");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
            }
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using (SHA256 sha256 = SHA256.Create())
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }

    private static PropertyInfo[] GetFullRoundTripProperties()
    {
        PropertyInfo[] allProperties = typeof(WidgetSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        List<PropertyInfo> result = new List<PropertyInfo>();
        for (int i = 0; i < allProperties.Length; i++)
        {
            PropertyInfo property = allProperties[i];
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            if (IsFullRoundTripSupportedType(property.PropertyType))
            {
                result.Add(property);
            }
        }

        result.Sort(delegate(PropertyInfo left, PropertyInfo right)
        {
            return string.CompareOrdinal(left.Name, right.Name);
        });
        return result.ToArray();
    }

    private static bool IsFullRoundTripSupportedType(Type type)
    {
        return type == typeof(int) ||
            type == typeof(long) ||
            type == typeof(bool) ||
            type == typeof(double) ||
            type == typeof(string) ||
            type == typeof(string[]) ||
            type.IsEnum;
    }

    private static Dictionary<string, string> CreateFullRoundTripSaveLoadExemptions()
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Runtime state: Load() intentionally mirrors the current Windows startup registration.
        result["StartupEnabled"] = "external Windows startup state";
        // Derived compatibility alias: setter maps to PerformanceMode and Save writes both keys for legacy settings.
        result["PowerSavingEnabled"] = "derived from PerformanceMode";
        // Legacy compatibility booleans: Save derives them from OperationPrimaryPanelMode for old settings files.
        result["OperationWindowsButtonEnabled"] = "derived legacy OperationPrimaryPanelMode key";
        result["OperationMemoryPieEnabled"] = "derived legacy OperationPrimaryPanelMode key";
        // One-shot runtime tokens are intentionally not persisted to avoid replaying manual refreshes on restart.
        result["GfwProbeManualRefreshToken"] = "transient manual refresh token";
        result["ConnectionCheckManualRefreshToken"] = "transient manual refresh token";
        // Hover state flags are current interaction state, not durable user configuration.
        result["ForceHoverOpacityActive"] = "transient hover state";
        result["ManualHoverOpacityActive"] = "transient hover state";
        return result;
    }

    private static void AssignFullRoundTripSentinel(WidgetSettings settings, PropertyInfo property, int index)
    {
        Type type = property.PropertyType;
        if (type == typeof(int))
        {
            property.SetValue(settings, 37 + (index % 41), null);
            return;
        }

        // Long-typed settings are all UTC tick stamps today, and Normalize discards ticks outside
        // the DateTime range. Offsetting from a fixed valid instant keeps each sentinel distinct
        // without ever landing on a value normalization would reset to 0.
        if (type == typeof(long))
        {
            property.SetValue(settings, new DateTime(2031, 3, 7, 4, 5, 6, DateTimeKind.Utc).Ticks + index, null);
            return;
        }

        if (type == typeof(bool))
        {
            bool current = (bool)property.GetValue(settings, null);
            property.SetValue(settings, !current, null);
            return;
        }

        if (type == typeof(double))
        {
            property.SetValue(settings, 1.25d + ((double)(index % 7) / 10.0d), null);
            return;
        }

        if (type == typeof(string))
        {
            property.SetValue(settings, "rt-" + property.Name, null);
            return;
        }

        if (type == typeof(string[]))
        {
            property.SetValue(settings, new string[] { "rt-array-a", "rt-array-b" }, null);
            return;
        }

        if (type.IsEnum)
        {
            Array values = Enum.GetValues(type);
            if (values.Length > 1)
            {
                property.SetValue(settings, values.GetValue(1), null);
            }
            else if (values.Length == 1)
            {
                property.SetValue(settings, values.GetValue(0), null);
            }
        }
    }

    private static void ApplyFullRoundTripCoherentSentinels(WidgetSettings settings)
    {
        settings.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55Medium;
        settings.CodexRadarModelKey = CodexRadarModelCatalog.LegacyKeyFromVersion(settings.CodexRadarModelVersion);
        settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Manual;
        settings.DisplayTimeZoneId = "UTC";
        settings.OperationPrimaryPanelMode = OperationPrimaryPanelMode.Hidden;
        settings.ResolutionCompatibilityScalePercent = 125;
        // Guard durations snap to a fixed ladder, so the generic numeric sentinel would be rewritten
        // by Normalize on load and fail the comparison. These pick real off-default ladder steps.
        settings.GuardDisplayMinutes = 120;
        settings.GuardOfflineThresholdMinutes = 5;
        // Generic string-array sentinels are metric IDs and are invalid for the left dock (and
        // incomplete for the ten-tile column). Full legal permutations keep Clone/Save/Load testing
        // focused on persistence instead of intentionally triggering order repair.
        settings.LeftDockButtonOrder = new string[] { "CodexIq", "Guard", "CodexTask", "SpecBoard", "Network" };
        settings.RightTileButtonOrder = new string[]
        {
            "ClaudeQuota", "CodexQuota", "Guard", "Power", "Npu",
            "Gpu", "Network", "Disk", "Memory", "Cpu"
        };
        settings.CloudEndpointTargets = new string[]
        {
            "builtin|cloudflare|1",
            "builtin|akamai|0",
            "builtin|github|1",
            "builtin|aws|1",
            "builtin|azure|1",
            "builtin|google|1",
            "custom|RoundTrip Cloud|9.9.9.9|1"
        };
        settings.FixedPingTargets = new string[]
        {
            "target|RoundTrip Ping|1.0.0.1|1",
            "target|Disabled Ping|8.8.4.4|0"
        };
    }

    private static void RunColumnArrangementSettingsSelfTest()
    {
        WidgetSettings defaults = CreateDefaults();
        AssertLayout(
            defaults.LeftDockAutoArrangeEnabled &&
            defaults.RightTileAutoArrangeEnabled &&
            defaults.LeftDockButtonGapPixels == DefaultLeftDockButtonGapPixels &&
            defaults.RightTileButtonGapPixels == DefaultRightTileButtonGapPixels &&
            defaults.LeftDockGroupOffsetY == DefaultColumnGroupOffsetY &&
            defaults.RightTileGroupOffsetY == DefaultColumnGroupOffsetY &&
            ColumnButtonOrdersEqual(defaults.LeftDockButtonOrder, DefaultLeftDockButtonOrder) &&
            ColumnButtonOrdersEqual(defaults.RightTileButtonOrder, DefaultRightTileButtonOrder),
            "column arrangement defaults should be automatic, centered and in stable identity order");

        WidgetSettings clone = defaults.Clone();
        AssertLayout(
            !object.ReferenceEquals(defaults.LeftDockButtonOrder, clone.LeftDockButtonOrder) &&
            !object.ReferenceEquals(defaults.RightTileButtonOrder, clone.RightTileButtonOrder),
            "column order arrays must be deep-cloned");
        clone.LeftDockButtonOrder[0] = "mutated";
        clone.RightTileButtonOrder[0] = "mutated";
        AssertLayout(
            string.Equals(defaults.LeftDockButtonOrder[0], "Network", StringComparison.Ordinal) &&
            string.Equals(defaults.RightTileButtonOrder[0], "Cpu", StringComparison.Ordinal),
            "mutating a clone must not change the source column orders");

        WidgetSettings repaired = defaults.Clone();
        repaired.LeftDockButtonOrder = new string[] { "guard", "unknown", "GUARD", "network" };
        repaired.RightTileButtonOrder = new string[] { "claudequota", "CPU", "cpu", "unknown" };
        repaired.LeftDockButtonGapPixels = int.MinValue;
        repaired.RightTileButtonGapPixels = int.MaxValue;
        repaired.LeftDockGroupOffsetY = int.MinValue;
        repaired.RightTileGroupOffsetY = int.MaxValue;
        repaired.Normalize();
        AssertLayout(
            ColumnButtonOrdersEqual(
                repaired.LeftDockButtonOrder,
                new string[] { "Guard", "Network", "SpecBoard", "CodexTask", "CodexIq" }) &&
            ColumnButtonOrdersEqual(
                repaired.RightTileButtonOrder,
                new string[]
                {
                    "ClaudeQuota", "Cpu", "Memory", "Disk", "Network", "Gpu", "Npu",
                    "Power", "Guard", "CodexQuota"
                }) &&
            repaired.LeftDockButtonGapPixels == MinColumnButtonGapPixels &&
            repaired.RightTileButtonGapPixels == MaxColumnButtonGapPixels &&
            repaired.LeftDockGroupOffsetY == MinColumnGroupOffsetY &&
            repaired.RightTileGroupOffsetY == MaxColumnGroupOffsetY,
            "column arrangement normalization should canonicalize orders and clamp gap/offset ranges");

        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-column-arrangement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string roundTripPath = Path.Combine(root, "roundtrip.ini");
            WidgetSettings custom = defaults.Clone();
            custom.LeftDockAutoArrangeEnabled = false;
            custom.RightTileAutoArrangeEnabled = false;
            custom.LeftDockButtonOrder = new string[] { "CodexIq", "Guard", "CodexTask", "SpecBoard", "Network" };
            custom.RightTileButtonOrder = new string[]
            {
                "ClaudeQuota", "CodexQuota", "Guard", "Power", "Npu",
                "Gpu", "Network", "Disk", "Memory", "Cpu"
            };
            custom.LeftDockButtonGapPixels = 17;
            custom.RightTileButtonGapPixels = 19;
            custom.LeftDockGroupOffsetY = -321;
            custom.RightTileGroupOffsetY = 456;
            custom.SaveToPath(roundTripPath, false);
            WidgetSettings loaded = LoadFromPath(roundTripPath, false);
            AssertLayout(
                !loaded.LeftDockAutoArrangeEnabled &&
                !loaded.RightTileAutoArrangeEnabled &&
                loaded.LeftDockButtonGapPixels == 17 &&
                loaded.RightTileButtonGapPixels == 19 &&
                loaded.LeftDockGroupOffsetY == -321 &&
                loaded.RightTileGroupOffsetY == 456 &&
                ColumnButtonOrdersEqual(loaded.LeftDockButtonOrder, custom.LeftDockButtonOrder) &&
                ColumnButtonOrdersEqual(loaded.RightTileButtonOrder, custom.RightTileButtonOrder),
                "column arrangement settings should round-trip independently");

            string automaticMigrationPath = Path.Combine(root, "migration-auto.ini");
            File.WriteAllLines(
                automaticMigrationPath,
                new string[] { "Version=82" },
                SharedEncoding.Utf8NoBom);
            WidgetSettings automaticMigration = LoadFromPath(automaticMigrationPath, false);
            AssertLayout(
                automaticMigration.LeftDockAutoArrangeEnabled &&
                automaticMigration.RightTileAutoArrangeEnabled &&
                ColumnButtonOrdersEqual(automaticMigration.LeftDockButtonOrder, DefaultLeftDockButtonOrder) &&
                ColumnButtonOrdersEqual(automaticMigration.RightTileButtonOrder, DefaultRightTileButtonOrder),
                "v82 automatic columns should opt into v83 grouped arrangement");

            string manualMigrationPath = Path.Combine(root, "migration-manual.ini");
            File.WriteAllLines(
                manualMigrationPath,
                new string[]
                {
                    "Version=82",
                    "SpecBoardLeftDockTabCenterY=777",
                    "MetricTileLeftX=auto,auto,auto,auto,321,auto,auto,auto,auto,auto",
                    "MetricTileBottomY=auto,auto,auto,auto,auto,auto,auto,auto,auto,auto"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings manualMigration = LoadFromPath(manualMigrationPath, false);
            AssertLayout(
                !manualMigration.LeftDockAutoArrangeEnabled &&
                !manualMigration.RightTileAutoArrangeEnabled,
                "v82 columns with any manual coordinate must preserve manual placement on v83 upgrade");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Column arrangement settings: PASS defaults clone normalize round-trip migrate(v82->v83)");
    }

    private static bool ColumnButtonOrdersEqual(string[] left, string[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void RunWindowTransparencyOverrideSelfTest()
    {
        WidgetSettings defaults = CreateDefaults();
        int[] values =
        {
            defaults.MainWidgetTransparencyOverridePercent,
            defaults.NetworkMonitorTransparencyOverridePercent,
            defaults.OperationTransparencyOverridePercent,
            defaults.SpecBoardTransparencyOverridePercent,
            defaults.CodexTaskBoardTransparencyOverridePercent,
            defaults.GuardBoardTransparencyOverridePercent,
            defaults.CodexIqBoardTransparencyOverridePercent
        };
        AssertLayout(Array.TrueForAll(values, value => value == -1), "window transparency overrides should default to global follow mode");

        WidgetSettings clamped = defaults.Clone();
        clamped.MainWidgetTransparencyOverridePercent = int.MinValue;
        clamped.NetworkMonitorTransparencyOverridePercent = int.MaxValue;
        clamped.Normalize();
        AssertLayout(
            clamped.MainWidgetTransparencyOverridePercent == MinWindowTransparencyOverridePercent &&
            clamped.NetworkMonitorTransparencyOverridePercent == MaxWindowTransparencyOverridePercent,
            "window transparency overrides should clamp to -1..90");

        Console.WriteLine("Window transparency overrides: PASS defaults=-1 clamp=-1..90");
    }

    private static void RunWindowScaleOverrideSelfTest()
    {
        WidgetSettings defaults = CreateDefaults();
        int[] values =
        {
            defaults.MainWidgetScaleOverridePercent,
            defaults.NetworkMonitorScaleOverridePercent,
            defaults.OperationScaleOverridePercent,
            defaults.SpecBoardScaleOverridePercent,
            defaults.CodexTaskBoardScaleOverridePercent,
            defaults.GuardBoardScaleOverridePercent,
            defaults.CodexIqBoardScaleOverridePercent
        };
        AssertLayout(Array.TrueForAll(values, value => value == -1), "window scale overrides should default to global follow mode");

        WidgetSettings clamped = defaults.Clone();
        clamped.MainWidgetScaleOverridePercent = int.MinValue;
        clamped.NetworkMonitorScaleOverridePercent = int.MaxValue;
        clamped.Normalize();
        AssertLayout(
            clamped.MainWidgetScaleOverridePercent == MinWindowScaleOverridePercent &&
            clamped.NetworkMonitorScaleOverridePercent == MaxWindowScaleOverridePercent,
            "window scale overrides should preserve -1 and clamp explicit values to 40..200");

        Console.WriteLine("Window scale overrides: PASS defaults=-1 clamp=40..200");
    }

    private static void RunRetiredSettingsSchema85MigrationSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-retired-settings-v85-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.ini");
            AssertLayout(
                RetiredSettingsInputNames.Count == RetiredCanonicalSettingsCount + RetiredSettingsAliasCount,
                "retired-key registry should contain exactly 98 canonical names and 11 aliases");

            List<string> fixture = new List<string>
            {
                "Version=84",
                "ApplicationTransparencyPercent=37",
                "NetworkMonitorAdapterId=retained-adapter",
                "NetworkMonitorScaleOverridePercent=125",
                "ConnectionCheckIntervalSeconds=75",
                "PowerThermalIntegratedEnabled=False",
                "RadarClockAutoSwitchModelEnabled=False",
                "AlertServiceHealthEnabled=False"
            };
            foreach (string retiredName in RetiredSettingsInputNames)
            {
                string retiredValue = "1";
                if (string.Equals(retiredName, "CodexRadarWidth", StringComparison.OrdinalIgnoreCase))
                {
                    retiredValue = "777";
                }
                else if (string.Equals(retiredName, "CodexRadarHeight", StringComparison.OrdinalIgnoreCase))
                {
                    retiredValue = "199";
                }
                else if (string.Equals(retiredName, "ClockWidth", StringComparison.OrdinalIgnoreCase))
                {
                    retiredValue = "333";
                }
                else if (string.Equals(retiredName, "ClockHeight", StringComparison.OrdinalIgnoreCase))
                {
                    retiredValue = "88";
                }

                fixture.Add(retiredName + "=" + retiredValue);
            }

            File.WriteAllLines(path, fixture.ToArray(), SharedEncoding.Utf8NoBom);

            WidgetSettings migrated = LoadFromPathAndSaveForSelfTest(path);
            AssertLayout(
                migrated.MetricTileExpandWidth == 777 && migrated.MetricTileExpandHeight == 199,
                "schema 85 should migrate canonical v84 Codex Radar size into the metric expand panel");
            AssertLayout(
                migrated.ApplicationTransparencyPercent == 37 &&
                string.Equals(migrated.NetworkMonitorAdapterId, "retained-adapter", StringComparison.Ordinal) &&
                migrated.NetworkMonitorScaleOverridePercent == 125 &&
                migrated.ConnectionCheckIntervalSeconds == 75 &&
                !migrated.PowerThermalIntegratedEnabled &&
                !migrated.RadarClockAutoSwitchModelEnabled &&
                !migrated.AlertServiceHealthEnabled,
                "schema 85 migration should preserve retained setting sentinels");

            string[] migratedLines = File.ReadAllLines(path);
            AssertLayout(
                Array.Exists(
                    migratedLines,
                    delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }),
                "retired settings migration should atomically rewrite the current schema");
            AssertLayout(!File.Exists(path + ".tmp"), "retired settings migration should not leave a temp file");

            HashSet<string> savedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < migratedLines.Length; i++)
            {
                int separator = migratedLines[i].IndexOf('=');
                if (separator > 0)
                {
                    savedKeys.Add(migratedLines[i].Substring(0, separator).Trim());
                }
            }

            List<string> leakedKeys = new List<string>();
            foreach (string retiredName in RetiredSettingsInputNames)
            {
                if (savedKeys.Contains(retiredName))
                {
                    leakedKeys.Add(retiredName);
                }
            }

            AssertLayout(
                leakedKeys.Count == 0,
                "canonical save should remove all 109 retired input names: " +
                    string.Join(",", leakedKeys.ToArray()));

            WidgetSettings roundTrip = LoadFromPathForSelfTest(path);
            AssertLayout(
                roundTrip.MetricTileExpandWidth == 777 &&
                roundTrip.MetricTileExpandHeight == 199 &&
                roundTrip.ApplicationTransparencyPercent == 37 &&
                string.Equals(roundTrip.NetworkMonitorAdapterId, "retained-adapter", StringComparison.Ordinal) &&
                roundTrip.NetworkMonitorScaleOverridePercent == 125 &&
                roundTrip.ConnectionCheckIntervalSeconds == 75 &&
                !roundTrip.PowerThermalIntegratedEnabled &&
                !roundTrip.RadarClockAutoSwitchModelEnabled &&
                !roundTrip.AlertServiceHealthEnabled,
                "canonical settings should round-trip migrated size and retained sentinels");

            string fallbackPath = Path.Combine(root, "clock-fallback.ini");
            File.WriteAllLines(
                fallbackPath,
                new string[] { "Version=84", "ClockWidth=444", "ClockHeight=99" },
                SharedEncoding.Utf8NoBom);
            WidgetSettings fallback = LoadFromPathAndSaveForSelfTest(fallbackPath);
            AssertLayout(
                fallback.MetricTileExpandWidth == 444 && fallback.MetricTileExpandHeight == 99,
                "schema 85 should use legacy ClockWidth/Height only when Codex Radar size is absent");
            AssertLayout(!File.Exists(fallbackPath + ".tmp"), "clock fallback migration should not leave a temp file");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Retired settings migration: PASS current-schema=89 canonical=98 aliases=11 atomic round-trip");
    }

    private static void RunRetiredSettingsSchema86MigrationSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-retired-settings-v86-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] retiredSchema86Names =
            {
                "DeepSeekApiKeyRevision",
                "AlertDeepSeekBalanceEnabled",
                "ClaudeRadarJsonEnabled",
                "ClaudeRadarHomepageFallbackEnabled",
                "ClaudeRadarCommunityRatingsEnabled",
                "ClaudeRadarLocalQuotaFallbackEnabled",
                "ClaudeRadarModelKey"
            };
            for (int i = 0; i < retiredSchema86Names.Length; i++)
            {
                AssertLayout(
                    RetiredSettingsInputNames.Contains(retiredSchema86Names[i]),
                    "schema 86 retired-key registry is missing " + retiredSchema86Names[i]);
            }

            string path = Path.Combine(root, "settings.ini");
            File.WriteAllLines(
                path,
                new string[]
                {
                    "Version=85",
                    "ApplicationTransparencyPercent=43",
                    "NetworkMonitorAdapterId=schema86-retained-adapter",
                    "RadarClockAutoSwitchModelEnabled=False",
                    "AlertServiceHealthEnabled=False",
                    "DeepSeekApiKeyRevision=77",
                    "AlertDeepSeekBalanceEnabled=False",
                    "ClaudeRadarJsonEnabled=False",
                    "ClaudeRadarHomepageFallbackEnabled=False",
                    "ClaudeRadarCommunityRatingsEnabled=False",
                    "ClaudeRadarLocalQuotaFallbackEnabled=False",
                    "ClaudeRadarModelKey=m999"
                },
                SharedEncoding.Utf8NoBom);

            WidgetSettings migrated = LoadFromPathAndSaveForSelfTest(path);
            AssertLayout(
                migrated.ApplicationTransparencyPercent == 43 &&
                string.Equals(migrated.NetworkMonitorAdapterId, "schema86-retained-adapter", StringComparison.Ordinal) &&
                !migrated.RadarClockAutoSwitchModelEnabled &&
                !migrated.AlertServiceHealthEnabled,
                "schema 86 migration should preserve unrelated and retained Radar settings");

            string[] migratedLines = File.ReadAllLines(path);
            AssertLayout(
                Array.Exists(
                    migratedLines,
                    delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }),
                "schema 86 retired-key migration should atomically rewrite Version=85 input to the current schema");
            AssertLayout(!File.Exists(path + ".tmp"), "schema 86 migration should not leave a temp file");

            HashSet<string> savedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < migratedLines.Length; i++)
            {
                int separator = migratedLines[i].IndexOf('=');
                if (separator > 0)
                {
                    savedKeys.Add(migratedLines[i].Substring(0, separator).Trim());
                }
            }

            List<string> leakedKeys = new List<string>();
            for (int i = 0; i < retiredSchema86Names.Length; i++)
            {
                if (savedKeys.Contains(retiredSchema86Names[i]))
                {
                    leakedKeys.Add(retiredSchema86Names[i]);
                }
            }

            AssertLayout(
                leakedKeys.Count == 0,
                "schema 86 canonical save should remove all seven newly retired keys: " +
                    string.Join(",", leakedKeys.ToArray()));

            WidgetSettings roundTrip = LoadFromPathForSelfTest(path);
            AssertLayout(
                roundTrip.ApplicationTransparencyPercent == 43 &&
                string.Equals(roundTrip.NetworkMonitorAdapterId, "schema86-retained-adapter", StringComparison.Ordinal) &&
                !roundTrip.RadarClockAutoSwitchModelEnabled &&
                !roundTrip.AlertServiceHealthEnabled,
                "schema 86 canonical settings should round-trip retained sentinels");

            string versionlessPath = Path.Combine(root, "settings-versionless.ini");
            File.WriteAllLines(
                versionlessPath,
                new string[]
                {
                    "ApplicationTransparencyPercent=46",
                    "NetworkMonitorAdapterId=versionless-retained-adapter",
                    "DeepSeekApiKeyRevision=88",
                    "ClaudeRadarModelKey=legacy-model"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings versionless = LoadFromPathAndSaveForSelfTest(versionlessPath);
            string[] versionlessLines = File.ReadAllLines(versionlessPath);
            AssertLayout(
                versionless.ApplicationTransparencyPercent == 46 &&
                string.Equals(versionless.NetworkMonitorAdapterId, "versionless-retained-adapter", StringComparison.Ordinal) &&
                Array.Exists(
                    versionlessLines,
                    delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }) &&
                !Array.Exists(
                    versionlessLines,
                    delegate(string line)
                    {
                        return line.StartsWith("DeepSeekApiKeyRevision=", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("ClaudeRadarModelKey=", StringComparison.OrdinalIgnoreCase);
                    }) &&
                !File.Exists(versionlessPath + ".tmp"),
                "schema 86 retired-key migration should atomically canonicalize versionless input to the current schema");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Retired settings migration: PASS current-schema=89 previous=85 retired=7 atomic round-trip");
    }

    private static void RunChinaEgressGuardSchema87MigrationSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-china-egress-v87-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string defaultPath = Path.Combine(root, "settings-default.ini");
            File.WriteAllLines(
                defaultPath,
                new string[] { "Version=86", "ApplicationTransparencyPercent=44" },
                SharedEncoding.Utf8NoBom);
            WidgetSettings migratedDefault = LoadFromPathAndSaveForSelfTest(defaultPath);
            string[] defaultLines = File.ReadAllLines(defaultPath);
            AssertLayout(
                migratedDefault.AiChinaEgressGuardEnabled &&
                Array.Exists(defaultLines, delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }) &&
                Array.Exists(defaultLines, delegate(string line) { return string.Equals(line, "AiChinaEgressGuardEnabled=True", StringComparison.Ordinal); }) &&
                !File.Exists(defaultPath + ".tmp"),
                "schema 87 migration should atomically persist the enabled guard default");

            string disabledPath = Path.Combine(root, "settings-disabled.ini");
            File.WriteAllLines(
                disabledPath,
                new string[] { "Version=86", "AiChinaEgressGuardEnabled=False" },
                SharedEncoding.Utf8NoBom);
            WidgetSettings migratedDisabled = LoadFromPathAndSaveForSelfTest(disabledPath);
            WidgetSettings roundTrip = LoadFromPathForSelfTest(disabledPath);
            string[] disabledLines = File.ReadAllLines(disabledPath);
            AssertLayout(
                !migratedDisabled.AiChinaEgressGuardEnabled &&
                !roundTrip.AiChinaEgressGuardEnabled &&
                Array.Exists(disabledLines, delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }) &&
                Array.Exists(disabledLines, delegate(string line) { return string.Equals(line, "AiChinaEgressGuardEnabled=False", StringComparison.Ordinal); }) &&
                !File.Exists(disabledPath + ".tmp"),
                "schema 87 migration should preserve an explicit disabled guard");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("China-egress guard migration: PASS source=86 feature=87 target=90 default-on explicit-off atomic round-trip");
    }

    private static void RunHiddenColorProtectionSchema88MigrationSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-hidden-color-v88-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.ini");
            File.WriteAllLines(
                path,
                new string[]
                {
                    "Version=87",
                    "ApplicationTransparencyPercent=41",
                    "HoverOpacityEnabled=False",
                    "BurnInHiddenModeColorProtectionEnabled=True"
                },
                SharedEncoding.Utf8NoBom);

            WidgetSettings migrated = LoadFromPathAndSaveForSelfTest(path);
            WidgetSettings roundTrip = LoadFromPathForSelfTest(path);
            string[] lines = File.ReadAllLines(path);
            AssertLayout(
                migrated.ApplicationTransparencyPercent == 41 &&
                !migrated.HoverOpacityEnabled &&
                roundTrip.ApplicationTransparencyPercent == 41 &&
                !roundTrip.HoverOpacityEnabled,
                "schema 88 migration should preserve unrelated hidden-opacity settings");
            AssertLayout(
                Array.Exists(lines, delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }) &&
                !Array.Exists(
                    lines,
                    delegate(string line)
                    {
                        return line.StartsWith("BurnInHiddenModeColorProtectionEnabled=", StringComparison.OrdinalIgnoreCase);
                    }) &&
                !File.Exists(path + ".tmp"),
                "schema 88 migration should atomically remove the retired hidden-colour key");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Hidden colour protection migration: PASS source=87 feature=88 target=90 retired-key removed");
    }

    private static void RunBurnInProtectionSchema89MigrationSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-burn-in-v89-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string defaultPath = Path.Combine(root, "settings-default.ini");
            File.WriteAllLines(
                defaultPath,
                new string[] { "Version=88", "ApplicationTransparencyPercent=42" },
                SharedEncoding.Utf8NoBom);
            WidgetSettings migrated = LoadFromPathAndSaveForSelfTest(defaultPath);
            string[] lines = File.ReadAllLines(defaultPath);
            AssertLayout(
                migrated.BurnInProtectionEnabled &&
                migrated.BurnInLevelOneIdleSeconds == DefaultBurnInLevelOneIdleSeconds &&
                migrated.BurnInLevelTwoDelaySeconds == DefaultBurnInLevelTwoDelaySeconds &&
                migrated.ApplicationTransparencyPercent == 42 &&
                Array.Exists(lines, delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }) &&
                Array.Exists(lines, delegate(string line) { return string.Equals(line, "BurnInProtectionEnabled=True", StringComparison.Ordinal); }) &&
                Array.Exists(lines, delegate(string line) { return string.Equals(line, "BurnInLevelOneIdleSeconds=10", StringComparison.Ordinal); }) &&
                Array.Exists(lines, delegate(string line) { return string.Equals(line, "BurnInLevelTwoDelaySeconds=30", StringComparison.Ordinal); }) &&
                !Array.Exists(lines, delegate(string line) { return line.StartsWith("BurnInHiddenModeColorProtectionEnabled=", StringComparison.OrdinalIgnoreCase); }) &&
                !File.Exists(defaultPath + ".tmp"),
                "schema 89 migration should persist the rebuilt burn-in defaults without resurrecting the retired key");

            string customPath = Path.Combine(root, "settings-custom.ini");
            File.WriteAllLines(
                customPath,
                new string[]
                {
                    "Version=89",
                    "BurnInProtectionEnabled=False",
                    "BurnInLevelOneIdleSeconds=27",
                    "BurnInLevelTwoDelaySeconds=91"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings custom = LoadFromPathForSelfTest(customPath);
            AssertLayout(
                !custom.BurnInProtectionEnabled &&
                custom.BurnInLevelOneIdleSeconds == 27 &&
                custom.BurnInLevelTwoDelaySeconds == 91,
                "schema 89 should preserve explicit burn-in opt-out and thresholds");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Burn-in protection migration: PASS source=88 target=90 defaults=10/+30 explicit-opt-out");
    }

    private static void RunColumnSpacingSchema90MigrationSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-column-spacing-v90-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string migratedPath = Path.Combine(root, "settings-v89.ini");
            File.WriteAllLines(
                migratedPath,
                new string[]
                {
                    "Version=89",
                    "LeftDockButtonGapPixels=0",
                    "RightTileButtonGapPixels=80",
                    "ApplicationTransparencyPercent=43"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings migrated = LoadFromPathAndSaveForSelfTest(migratedPath);
            string[] migratedLines = File.ReadAllLines(migratedPath);
            AssertLayout(
                migrated.LeftDockButtonGapPixels == 0 &&
                migrated.RightTileButtonGapPixels == 80 &&
                migrated.ApplicationTransparencyPercent == 43 &&
                Array.Exists(migratedLines, delegate(string line) { return string.Equals(line, "Version=90", StringComparison.Ordinal); }) &&
                !File.Exists(migratedPath + ".tmp"),
                "schema 90 migration should preserve existing spacing values and atomically record the new contract");

            string fullDistributionPath = Path.Combine(root, "settings-v90.ini");
            File.WriteAllLines(
                fullDistributionPath,
                new string[]
                {
                    "Version=90",
                    "LeftDockButtonGapPixels=100",
                    "RightTileButtonGapPixels=100"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings fullDistribution = LoadFromPathForSelfTest(fullDistributionPath);
            AssertLayout(
                fullDistribution.LeftDockButtonGapPixels == MaxColumnButtonGapPixels &&
                fullDistribution.RightTileButtonGapPixels == MaxColumnButtonGapPixels,
                "schema 90 should accept 100 as the full-edge distribution value");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Column spacing migration: PASS source=89 target=90 range=0..100 values-preserved");
    }

    private static void RunGuardBoardOverrideMigrationSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-guard-override-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string configuredPath = Path.Combine(root, "configured.ini");
            File.WriteAllLines(
                configuredPath,
                new string[]
                {
                    "Version=79",
                    "SpecBoardTransparencyOverridePercent=60",
                    "SpecBoardScaleOverridePercent=60"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings configured = LoadFromPath(configuredPath, false);
            AssertLayout(
                configured.GuardBoardTransparencyOverridePercent == 60 &&
                configured.GuardBoardScaleOverridePercent == 60,
                "GUARD override migration should preserve the old Spec Board-owned visual values");

            string followingPath = Path.Combine(root, "following.ini");
            File.WriteAllLines(followingPath, new string[] { "Version=79" }, SharedEncoding.Utf8NoBom);
            WidgetSettings following = LoadFromPath(followingPath, false);
            AssertLayout(
                following.GuardBoardTransparencyOverridePercent == MinWindowTransparencyOverridePercent &&
                following.GuardBoardScaleOverridePercent == MinWindowScaleOverridePercent,
                "GUARD override migration should preserve global-follow sentinels");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("GUARD override migration: PASS v79 spec=60 and global-follow sentinel");
    }

    private static void RunNetworkDockOverrideMigrationSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-network-dock-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string inheritedPath = Path.Combine(root, "inherited.ini");
            File.WriteAllLines(
                inheritedPath,
                new string[]
                {
                    "Version=80",
                    "SpecBoardTransparencyOverridePercent=60",
                    "SpecBoardScaleOverridePercent=60"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings inherited = LoadFromPath(inheritedPath, false);
            AssertLayout(
                inherited.NetworkMonitorTransparencyOverridePercent == 60 &&
                inherited.NetworkMonitorScaleOverridePercent == 60,
                "Network dock migration should copy the old Spec-owned visual values into untouched slots");

            string explicitPath = Path.Combine(root, "explicit.ini");
            File.WriteAllLines(
                explicitPath,
                new string[]
                {
                    "Version=80",
                    "SpecBoardTransparencyOverridePercent=60",
                    "SpecBoardScaleOverridePercent=60",
                    "NetworkMonitorTransparencyOverridePercent=35",
                    "NetworkMonitorScaleOverridePercent=125"
                },
                SharedEncoding.Utf8NoBom);
            WidgetSettings explicitValues = LoadFromPath(explicitPath, false);
            AssertLayout(
                explicitValues.NetworkMonitorTransparencyOverridePercent == 35 &&
                explicitValues.NetworkMonitorScaleOverridePercent == 125,
                "Network dock migration must preserve explicit Network-owned visual values");

            string sentinelPath = Path.Combine(root, "sentinel.ini");
            File.WriteAllLines(sentinelPath, new string[] { "Version=80" }, SharedEncoding.Utf8NoBom);
            WidgetSettings sentinel = LoadFromPath(sentinelPath, false);
            AssertLayout(
                sentinel.NetworkMonitorTransparencyOverridePercent == MinWindowTransparencyOverridePercent &&
                sentinel.NetworkMonitorScaleOverridePercent == MinWindowScaleOverridePercent,
                "Network dock migration should preserve global-follow sentinels");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { }
        }

        Console.WriteLine("Network dock override migration: PASS v80 inheritance, explicit values and sentinels");
    }

    private static void RunCodexTaskMonitorSettingsSelfTest()
    {
        WidgetSettings defaults = CreateDefaults();
        AssertLayout(
            defaults.CodexTaskMonitorEnabled &&
            defaults.CodexTaskMonitorActiveWindowMinutes == 30 &&
            defaults.CodexTaskMonitorActiveSeconds == 12 &&
            defaults.CodexTaskMonitorIdleSeconds == 90 &&
            defaults.CodexTaskMonitorTerminalHoldSeconds == 120 &&
            defaults.CodexTaskMonitorErrorHoldSeconds == 30 &&
            defaults.CodexTaskMonitorNumberCooldownSeconds == 120,
            "Codex task monitor defaults");

        WidgetSettings low = defaults.Clone();
        low.CodexTaskMonitorActiveWindowMinutes = -1;
        low.CodexTaskMonitorActiveSeconds = -1;
        low.CodexTaskMonitorIdleSeconds = -1;
        low.CodexTaskMonitorTerminalHoldSeconds = -1;
        low.CodexTaskMonitorErrorHoldSeconds = -1;
        low.CodexTaskMonitorNumberCooldownSeconds = -1;
        low.Normalize();
        AssertLayout(
            low.CodexTaskMonitorActiveWindowMinutes == 5 &&
            low.CodexTaskMonitorActiveSeconds == 3 &&
            low.CodexTaskMonitorIdleSeconds == 30 &&
            low.CodexTaskMonitorTerminalHoldSeconds == 0 &&
            low.CodexTaskMonitorErrorHoldSeconds == 5 &&
            low.CodexTaskMonitorNumberCooldownSeconds == 0,
            "Codex task monitor low clamps");

        WidgetSettings high = defaults.Clone();
        high.CodexTaskMonitorActiveWindowMinutes = int.MaxValue;
        high.CodexTaskMonitorActiveSeconds = int.MaxValue;
        high.CodexTaskMonitorIdleSeconds = int.MaxValue;
        high.CodexTaskMonitorTerminalHoldSeconds = int.MaxValue;
        high.CodexTaskMonitorErrorHoldSeconds = int.MaxValue;
        high.CodexTaskMonitorNumberCooldownSeconds = int.MaxValue;
        high.Normalize();
        AssertLayout(
            high.CodexTaskMonitorActiveWindowMinutes == 60 &&
            high.CodexTaskMonitorActiveSeconds == 60 &&
            high.CodexTaskMonitorIdleSeconds == 600 &&
            high.CodexTaskMonitorTerminalHoldSeconds == 1800 &&
            high.CodexTaskMonitorErrorHoldSeconds == 300 &&
            high.CodexTaskMonitorNumberCooldownSeconds == 3600,
            "Codex task monitor high clamps");

        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-task-monitor-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.ini");
            WidgetSettings saved = defaults.Clone();
            saved.CodexTaskMonitorEnabled = false;
            saved.CodexTaskMonitorActiveWindowMinutes = 45;
            saved.CodexTaskMonitorActiveSeconds = 24;
            saved.CodexTaskMonitorIdleSeconds = 240;
            saved.CodexTaskMonitorTerminalHoldSeconds = 600;
            saved.CodexTaskMonitorErrorHoldSeconds = 75;
            saved.CodexTaskMonitorNumberCooldownSeconds = 900;
            saved.SaveToPath(path, true);
            WidgetSettings loaded = LoadFromPath(path, false);
            AssertLayout(
                !loaded.CodexTaskMonitorEnabled &&
                loaded.CodexTaskMonitorActiveWindowMinutes == 45 &&
                loaded.CodexTaskMonitorActiveSeconds == 24 &&
                loaded.CodexTaskMonitorIdleSeconds == 240 &&
                loaded.CodexTaskMonitorTerminalHoldSeconds == 600 &&
                loaded.CodexTaskMonitorErrorHoldSeconds == 75 &&
                loaded.CodexTaskMonitorNumberCooldownSeconds == 900,
                "Codex task monitor save/load round-trip");

            File.WriteAllLines(path, new string[] { "Version=69" }, SharedEncoding.Utf8NoBom);
            WidgetSettings migrated = LoadFromPath(path, false);
            AssertLayout(
                migrated.CodexTaskMonitorEnabled &&
                migrated.CodexTaskMonitorActiveWindowMinutes == 30 &&
                migrated.CodexTaskMonitorNumberCooldownSeconds == 120,
                "Codex task monitor Version 69 migration");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine("Codex task monitor settings: PASS defaults clone normalize save/load migrate(v69->v70)");
    }

    private static void RunCodexIqBoardSettingsSelfTest()
    {
        WidgetSettings defaults = CreateDefaults();
        AssertLayout(
            defaults.CodexIqBoardLeftDockEnabled &&
            defaults.CodexIqBoardLeftDockTabCenterY == AutoLeftDockTabCenterY &&
            defaults.CodexIqBoardAutoHideSeconds == DefaultCodexIqBoardAutoHideSeconds &&
            defaults.CodexIqBoardTransparencyOverridePercent == MinWindowTransparencyOverridePercent &&
            defaults.CodexIqBoardScaleOverridePercent == MinWindowScaleOverridePercent,
            "Codex IQ board defaults should enable an independent fifth dock slot.");

        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-codex-iq-board-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.ini");
            defaults.CodexIqBoardLeftDockEnabled = false;
            defaults.CodexIqBoardLeftDockTabCenterY = 777;
            defaults.CodexIqBoardAutoHideSeconds = 41;
            defaults.CodexIqBoardTransparencyOverridePercent = 38;
            defaults.CodexIqBoardScaleOverridePercent = 125;
            defaults.SaveToPath(path, true);
            WidgetSettings loaded = LoadFromPath(path, false);
            AssertLayout(
                loaded.CodexIqBoardLeftDockEnabled &&
                loaded.CodexIqBoardLeftDockTabCenterY == 777 &&
                loaded.CodexIqBoardAutoHideSeconds == 41 &&
                loaded.CodexIqBoardTransparencyOverridePercent == 38 &&
                loaded.CodexIqBoardScaleOverridePercent == 125,
                "Codex IQ board settings should preserve visual slots while forcing the fifth dock on.");

            File.WriteAllLines(path, new string[] { "Version=81" }, SharedEncoding.Utf8NoBom);
            WidgetSettings migrated = LoadFromPath(path, false);
            AssertLayout(
                migrated.CodexIqBoardLeftDockEnabled &&
                migrated.CodexIqBoardLeftDockTabCenterY == AutoLeftDockTabCenterY &&
                migrated.CodexIqBoardAutoHideSeconds == DefaultCodexIqBoardAutoHideSeconds,
                "Codex IQ board v81 to v82 migration failed.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine("Codex IQ board settings: PASS fixed dock, independent overrides, save/load, migrate(v81->v82)");
    }

    private static void RunSpecBoardSettingsSelfTest()
    {
        WidgetSettings defaults = CreateDefaults();
        AssertLayout(
            defaults.SpecBoardWidth == 648 && defaults.SpecBoardHeight == 400 &&
            defaults.SpecBoardLeftX == -1 && defaults.SpecBoardBottomY == -1 &&
            defaults.SpecBoardAutoHideSeconds == 20 &&
            defaults.LeftDockOutsideClickCollapseEnabled &&
            defaults.SpecBoardAutoPopupEnabled && defaults.SpecBoardAutoPopupSeconds == 5 &&
            string.Equals(defaults.SpecBoardLedgerPath, DefaultSpecBoardLedgerPath, StringComparison.Ordinal) &&
            defaults.SpecBoardManagerWidth == 720 && defaults.SpecBoardManagerHeight == 520 &&
            defaults.SpecBoardManagerDangerZoneRequiresTypedConfirm,
            "Spec Board defaults should cover board and manager settings");

        WidgetSettings clone = defaults.Clone();
        AssertLayout(
            clone.SpecBoardWidth == defaults.SpecBoardWidth &&
            clone.SpecBoardHeight == defaults.SpecBoardHeight &&
            clone.SpecBoardLeftX == defaults.SpecBoardLeftX &&
            clone.SpecBoardBottomY == defaults.SpecBoardBottomY &&
            clone.SpecBoardAutoHideSeconds == defaults.SpecBoardAutoHideSeconds &&
            clone.LeftDockOutsideClickCollapseEnabled == defaults.LeftDockOutsideClickCollapseEnabled &&
            clone.SpecBoardAutoPopupEnabled == defaults.SpecBoardAutoPopupEnabled &&
            clone.SpecBoardAutoPopupSeconds == defaults.SpecBoardAutoPopupSeconds &&
            string.Equals(clone.SpecBoardLedgerPath, defaults.SpecBoardLedgerPath, StringComparison.Ordinal) &&
            clone.SpecBoardManagerWidth == defaults.SpecBoardManagerWidth &&
            clone.SpecBoardManagerHeight == defaults.SpecBoardManagerHeight &&
            clone.SpecBoardManagerDangerZoneRequiresTypedConfirm == defaults.SpecBoardManagerDangerZoneRequiresTypedConfirm,
            "Spec Board clone should preserve board and manager settings");

        WidgetSettings low = defaults.Clone();
        low.SpecBoardAutoHideSeconds = -5;
        low.SpecBoardAutoPopupSeconds = -5;
        low.SpecBoardLeftX = -2;
        low.SpecBoardLedgerPath = "  \"\"  ";
        low.SpecBoardManagerWidth = -1;
        low.SpecBoardManagerHeight = -1;
        low.Normalize();
        AssertLayout(low.SpecBoardAutoHideSeconds == 0 && low.SpecBoardAutoPopupSeconds == 1 && low.SpecBoardLeftX == -1 &&
            low.SpecBoardManagerWidth == 560 && low.SpecBoardManagerHeight == 400 &&
            string.Equals(low.SpecBoardLedgerPath, DefaultSpecBoardLedgerPath, StringComparison.Ordinal), "Spec Board low normalization");

        WidgetSettings high = defaults.Clone();
        high.SpecBoardWidth = 9999;
        high.SpecBoardHeight = 9999;
        high.SpecBoardAutoHideSeconds = 9999;
        high.SpecBoardAutoPopupSeconds = 9999;
        high.SpecBoardBottomY = int.MaxValue;
        high.SpecBoardManagerWidth = 9999;
        high.SpecBoardManagerHeight = 9999;
        high.Normalize();
        AssertLayout(high.SpecBoardWidth == 700 && high.SpecBoardHeight == 800 && high.SpecBoardAutoHideSeconds == 600 && high.SpecBoardAutoPopupSeconds == 120 && high.SpecBoardBottomY == -1 &&
            high.SpecBoardManagerWidth == 1000 && high.SpecBoardManagerHeight == 900, "Spec Board high normalization");

        string tempRoot = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string path = Path.Combine(tempRoot, "settings.ini");
            defaults.SpecBoardWidth = 521;
            defaults.SpecBoardHeight = 477;
            defaults.SpecBoardLeftX = 111;
            defaults.SpecBoardBottomY = 777;
            defaults.SpecBoardAutoHideSeconds = 45;
            defaults.LeftDockOutsideClickCollapseEnabled = false;
            defaults.SpecBoardAutoPopupEnabled = false;
            defaults.SpecBoardAutoPopupSeconds = 17;
            defaults.SpecBoardLedgerPath = Path.Combine(tempRoot, "ledger.jsonl");
            defaults.SpecBoardManagerWidth = 811;
            defaults.SpecBoardManagerHeight = 633;
            defaults.SpecBoardManagerDangerZoneRequiresTypedConfirm = false;
            defaults.SaveToPath(path, true);
            WidgetSettings loaded = LoadFromPath(path, false);
            AssertLayout(
                loaded.SpecBoardWidth == defaults.SpecBoardWidth && loaded.SpecBoardHeight == defaults.SpecBoardHeight &&
                loaded.SpecBoardLeftX == defaults.SpecBoardLeftX && loaded.SpecBoardBottomY == defaults.SpecBoardBottomY &&
                loaded.SpecBoardAutoHideSeconds == defaults.SpecBoardAutoHideSeconds &&
                loaded.LeftDockOutsideClickCollapseEnabled == defaults.LeftDockOutsideClickCollapseEnabled &&
                loaded.SpecBoardAutoPopupEnabled == defaults.SpecBoardAutoPopupEnabled &&
                loaded.SpecBoardAutoPopupSeconds == defaults.SpecBoardAutoPopupSeconds &&
                string.Equals(loaded.SpecBoardLedgerPath, defaults.SpecBoardLedgerPath, StringComparison.Ordinal) &&
                loaded.SpecBoardManagerWidth == defaults.SpecBoardManagerWidth &&
                loaded.SpecBoardManagerHeight == defaults.SpecBoardManagerHeight &&
                loaded.SpecBoardManagerDangerZoneRequiresTypedConfirm == defaults.SpecBoardManagerDangerZoneRequiresTypedConfirm,
                "Spec Board save/load round-trip should preserve board and manager settings");

            File.WriteAllLines(
                path,
                new string[] { "Version=64", "SpecBoardWidth=432", "SpecBoardHeight=400" },
                SharedEncoding.Utf8NoBom);
            WidgetSettings migrated = LoadFromPath(path, false);
            AssertLayout(
                migrated.SpecBoardWidth == 648 && migrated.SpecBoardHeight == 400 &&
                migrated.SpecBoardManagerWidth == 720 && migrated.SpecBoardManagerHeight == 520 &&
                migrated.SpecBoardManagerDangerZoneRequiresTypedConfirm,
                "Spec Board Version 64 width and Version 65 manager settings migration failed");

            File.WriteAllLines(path, new string[] { "Version=75" }, SharedEncoding.Utf8NoBom);
            WidgetSettings outsideClickMigrated = LoadFromPath(path, false);
            AssertLayout(
                outsideClickMigrated.LeftDockOutsideClickCollapseEnabled,
                "Spec Board Version 75 outside-click migration failed");
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }

        Console.WriteLine("SpecBoard settings: PASS board+manager+outside-click keys migrate(v64/v65/v75->v76) clone save/load normalize");
    }

    private static void AssertFullRoundTripEqual(
        WidgetSettings expected,
        WidgetSettings actual,
        PropertyInfo[] properties,
        Dictionary<string, string> exemptions,
        string scope)
    {
        List<string> mismatches = new List<string>();
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            if (exemptions != null && exemptions.ContainsKey(property.Name))
            {
                continue;
            }

            object expectedValue = property.GetValue(expected, null);
            object actualValue = property.GetValue(actual, null);
            if (!AreFullRoundTripValuesEqual(expectedValue, actualValue, property.PropertyType))
            {
                mismatches.Add(property.Name + " expected=" + FormatFullRoundTripValue(expectedValue) + " actual=" + FormatFullRoundTripValue(actualValue));
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                "Settings full round-trip self-test failed in " + scope + ": " +
                string.Join("; ", mismatches.ToArray()));
        }
    }

    private static bool AreFullRoundTripValuesEqual(object expected, object actual, Type type)
    {
        if (type == typeof(double))
        {
            return Math.Abs((double)expected - (double)actual) < 0.0005d;
        }

        if (type == typeof(string[]))
        {
            string[] expectedArray = expected as string[];
            string[] actualArray = actual as string[];
            if (expectedArray == null || actualArray == null || expectedArray.Length != actualArray.Length)
            {
                return false;
            }

            for (int i = 0; i < expectedArray.Length; i++)
            {
                if (!string.Equals(expectedArray[i], actualArray[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        return object.Equals(expected, actual);
    }

    private static string FormatFullRoundTripValue(object value)
    {
        string[] array = value as string[];
        if (array != null)
        {
            return "[" + string.Join(",", array) + "]";
        }

        return value == null ? "<null>" : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void AssertFullRoundTripSaveCoverage(
        string path,
        PropertyInfo[] properties,
        Dictionary<string, string> exemptions)
    {
        Dictionary<string, int> keyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            int split = lines[i].IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            string key = lines[i].Substring(0, split).Trim();
            int count;
            keyCounts.TryGetValue(key, out count);
            keyCounts[key] = count + 1;
        }

        List<string> missing = new List<string>();
        for (int i = 0; i < properties.Length; i++)
        {
            string name = properties[i].Name;
            if (exemptions.ContainsKey(name))
            {
                continue;
            }

            int count;
            keyCounts.TryGetValue(name, out count);
            if (count != 1)
            {
                missing.Add(name + " count=" + count.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Settings full round-trip self-test failed in Save coverage: " +
                string.Join("; ", missing.ToArray()));
        }
    }

    private static void AssertLayout(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Layout scaling self-test failed: " + message);
        }
    }

    public static bool ShouldEnableProcessPowerSaving(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        return mode == WidgetPerformanceMode.BatterySaver;
    }

    public static int GetWidgetSampleIntervalMs(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        // Controls the main PDH hardware sampler, not the power/thermal WMI sampler.
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 500;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 2500;
        }

        return 1000;
    }

    public static int GetPanelRenderIntervalMs(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        // Secondary panels use this as their idle redraw/check cadence.
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 500;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 3000;
        }

        return 1000;
    }

    public static int GetExpensiveHardwareSampleIntervalMs(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        // GPU Engine can expand to hundreds of counters; CPU and disk remain on the
        // faster widget cadence while GPU/NPU use this independent interval.
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 1000;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 5000;
        }

        return 2000;
    }

    public static int GetHoverAnimationIntervalMs(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        // Used only while opacity is actively moving toward its target.
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 16;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 100;
        }

        return 33;
    }

    public static int GetInteractionIdlePollingIntervalMs(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        // Cursor/click-through checks can run much slower once animation is settled.
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 30;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 250;
        }

        return 100;
    }

    // Network-specific policy is centralized here so the form and reader cannot drift.
    // These values are scheduling ceilings; unchanged snapshots still skip redraw.
    public static int GetNetworkLocalRefreshIntervalMs(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 2000;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            // Battery Saver relies on NetworkChange events instead of periodic adapter enumeration.
            return int.MaxValue;
        }

        return 5000;
    }

    public static int GetNetworkConnectivityIntervalMs(WidgetPerformanceMode mode, NetworkAccessState state)
    {
        mode = GetEffectivePerformanceMode(mode);
        if (state == NetworkAccessState.AdapterMissing)
        {
            return int.MaxValue;
        }

        if (state == NetworkAccessState.Unknown)
        {
            return 0;
        }

        if (mode == WidgetPerformanceMode.Smooth)
        {
            return state == NetworkAccessState.Online ? 10000 :
                (state == NetworkAccessState.NeedsValidation ? 5000 : 3000);
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return state == NetworkAccessState.Online ? 60000 :
                (state == NetworkAccessState.NeedsValidation ? 30000 : 10000);
        }

        return state == NetworkAccessState.Online ? 30000 :
            (state == NetworkAccessState.NeedsValidation ? 10000 : 5000);
    }

    public static int GetNetworkPublicIpRefreshIntervalMinutes(WidgetPerformanceMode mode)
    {
        mode = GetEffectivePerformanceMode(mode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 5;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 15;
        }

        return 10;
    }

    public static int GetNetworkDnsProbeIntervalMs(WidgetPerformanceMode mode)
    {
        return GetNetworkDnsProbeIntervalMs(mode, DnsServerStatus.Problem);
    }

    public static int GetNetworkDnsProbeIntervalMs(WidgetPerformanceMode mode, DnsServerStatus worstStatus)
    {
        mode = GetEffectivePerformanceMode(mode);
        if (worstStatus == DnsServerStatus.Normal)
        {
            if (mode == WidgetPerformanceMode.Smooth)
            {
                return 300000;
            }

            if (mode == WidgetPerformanceMode.BatterySaver)
            {
                return 900000;
            }

            return 600000;
        }

        if (worstStatus == DnsServerStatus.Unknown)
        {
            if (mode == WidgetPerformanceMode.Smooth)
            {
                return 15000;
            }

            if (mode == WidgetPerformanceMode.BatterySaver)
            {
                return 60000;
            }

            return 30000;
        }

        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 30000;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 120000;
        }

        return 60000;
    }

    public static int GetNetworkIdlePollingIntervalMs(WidgetPerformanceMode mode)
    {
        return GetInteractionIdlePollingIntervalMs(mode);
    }

    public static WidgetPerformanceMode GetEffectivePerformanceMode(WidgetPerformanceMode mode)
    {
        if (mode != WidgetPerformanceMode.WindowsPowerMode)
        {
            return mode;
        }

        DateTime nowUtc = DateTime.UtcNow;
        lock (EffectivePerformanceModeSync)
        {
            if (effectivePerformanceModeCacheUtc != DateTime.MinValue &&
                (nowUtc - effectivePerformanceModeCacheUtc).TotalMilliseconds < EffectivePerformanceModeCacheMs)
            {
                return effectivePerformanceModeCache;
            }

            WidgetPerformanceMode resolved = MapSystemPowerModeTextToPerformanceMode(
                PowerThermalForm.ReadCurrentSystemPowerModeText());
            effectivePerformanceModeCache = resolved;
            effectivePerformanceModeCacheUtc = nowUtc;
            return resolved;
        }
    }

    public static void InvalidateEffectivePerformanceModeCache()
    {
        lock (EffectivePerformanceModeSync)
        {
            effectivePerformanceModeCacheUtc = DateTime.MinValue;
        }
    }

    private static WidgetPerformanceMode MapSystemPowerModeTextToPerformanceMode(string powerModeText)
    {
        if (string.IsNullOrEmpty(powerModeText))
        {
            return WidgetPerformanceMode.Balanced;
        }

        if (powerModeText.IndexOf("性能", StringComparison.Ordinal) >= 0)
        {
            return WidgetPerformanceMode.Smooth;
        }

        if (powerModeText.IndexOf("省电", StringComparison.Ordinal) >= 0 ||
            powerModeText.IndexOf("节能", StringComparison.Ordinal) >= 0)
        {
            return WidgetPerformanceMode.BatterySaver;
        }

        return WidgetPerformanceMode.Balanced;
    }

    public static bool ShouldEnableClickThrough(ClickThroughMode mode, WidgetVisibilityMode visibilityMode)
    {
        if (mode == ClickThroughMode.Enabled)
        {
            return true;
        }

        if (mode == ClickThroughMode.Disabled)
        {
            return false;
        }

        // Auto mode keeps the desktop-attached widget interactive and passes clicks through top-level modes.
        return visibilityMode != WidgetVisibilityMode.DesktopOnly;
    }

    public static int GetOperationWindowWidth(int buttonSize, float scale)
    {
        int margin = Math.Max(1, (int)Math.Round(3.0f * Math.Max(1.0f, scale)));
        int smallSize = Math.Max(
            Math.Max(1, (int)Math.Round(18.0f * Math.Max(1.0f, scale))),
            (int)Math.Round(buttonSize / 2.0f));
        return margin * 2 + buttonSize + smallSize * 6;
    }

    public static int GetOperationWindowHeight(int buttonSize, float scale)
    {
        int margin = Math.Max(1, (int)Math.Round(3.0f * Math.Max(1.0f, scale)));
        return margin * 2 + buttonSize;
    }

    private static float GetPrimaryScale()
    {
        try
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return Math.Max(1.0f, g.DpiX / 96.0f);
            }
        }
        catch
        {
            return 1.0f;
        }
    }

    private static int NormalizeSpecBoardAnchor(int value)
    {
        return value == -1 || (value >= 0 && value <= 1000000) ? value : -1;
    }

    private static int NormalizeLeftDockTabCenterY(int value)
    {
        return value == AutoLeftDockTabCenterY || (value >= 0 && value <= 1000000) ? value : AutoLeftDockTabCenterY;
    }

    // Guard steppers walk a fixed ladder, so an out-of-menu value from a hand-edited settings.ini
    // snaps to the nearest legal step instead of being clamped into a duration the UI cannot show.
    public static int NormalizeGuardDisplayMinutes(int value)
    {
        return SnapToNearestStep(value, GuardDisplayMinuteSteps, DefaultGuardDisplayMinutes);
    }

    public static int NormalizeGuardOfflineThresholdMinutes(int value)
    {
        return SnapToNearestStep(value, GuardOfflineThresholdMinuteSteps, DefaultGuardOfflineThresholdMinutes);
    }

    private static int SnapToNearestStep(int value, int[] steps, int fallback)
    {
        if (steps == null || steps.Length == 0)
        {
            return fallback;
        }

        int best = steps[0];
        int bestDistance = Math.Abs(value - best);
        for (int i = 1; i < steps.Length; i++)
        {
            int distance = Math.Abs(value - steps[i]);
            if (distance < bestDistance)
            {
                best = steps[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    // A tick value that cannot be a UTC DateTime (negative, or past DateTime.MaxValue) would throw
    // inside the DateTime constructor at load time, so it is discarded here as "not armed".
    private static long NormalizeUtcTicks(long value)
    {
        return value > 0L && value <= DateTime.MaxValue.Ticks ? value : 0L;
    }

    private static string NormalizeSpecBoardLedgerPath(string value)
    {
        string normalized = (value ?? string.Empty).Trim().Trim('"');
        return normalized.Length == 0 ? DefaultSpecBoardLedgerPath : normalized;
    }

    private static int NormalizeWindowScaleOverride(int value)
    {
        // Every negative value means "follow global" so malformed legacy input cannot
        // accidentally shrink a single window; explicit overrides use the supported scale floor.
        return value < 0
            ? MinWindowScaleOverridePercent
            : Clamp(value, MinResolutionCompatibilityScalePercent, MaxWindowScaleOverridePercent);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static bool TryParseDouble(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
            double.TryParse(value, out result);
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

}
