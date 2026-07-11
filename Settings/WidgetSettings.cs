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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class MetricPanel
{
    public MetricPanel(string[] textLines, Color[] colors, List<double>[] histories, double graphMax, bool autoScale)
    {
        this.TextLines = textLines;
        this.Colors = colors;
        this.Histories = histories;
        this.GraphMax = graphMax;
        this.AutoScale = autoScale;
    }

    public string[] TextLines { get; private set; }
    public Color[] Colors { get; private set; }
    public List<double>[] Histories { get; private set; }
    public double GraphMax { get; private set; }
    public bool AutoScale { get; private set; }
    public bool UseCompactValueFont { get; set; }
    public bool UseHardwareStackText { get; set; }
    public bool IsNetworkDisconnected { get; set; }
    public double[] CoreValues { get; set; }
    public double AlertPercent { get; set; }
    public bool AlertIconVisible { get; set; }
}

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

// CodexRadar's render path is hard-coded to EvenRow (Core/CodexRadarForm.EvenRow.cs: single row of
// 7 equal-width cells); the other former variants (Classic widget tree, EvenGrid, and the four
// OLED-safe restyle schemes) were deleted. This enum is kept single-member so the persisted
// settings property and existing settings.ini key remain valid without a migration.
internal enum CodexRadarRenderVariant
{
    EvenRow
}

// Per-window render-variant switches, mirroring CodexRadarRenderVariant. Each window has its own
// enum so a variant added to one window never appears in another window's dropdown. Classic is the
// original layout; add a member plus a sibling partial file (e.g. WidgetForm.<Name>.cs) and a case in
// that window's Draw*Content dispatcher to introduce an alternate layout. Paint-only switch.
//
// Typographic/AmberHud/WarmCard/Phosphor are the four OLED-safe restyle schemes added in 1.0.3.44:
// no blue-dominant hues, no peak-white/saturated fills, background stays the existing semi-transparent
// AppBackground. Burn-in mitigation for all four relies on the existing BurnInProtection.ApplyRuntimeOffset
// periodic whole-window position shift (uniform screen wear), not on any per-scheme trick.
// MainWidget's render path is hard-coded to Classic; the four OLED-safe restyle schemes were
// deleted. Kept single-member so the persisted settings property/settings.ini key stay valid.
internal enum MainWidgetRenderVariant { Classic }

// NetworkMonitor's render path is hard-coded to Classic; GroupedCards and the four OLED-safe
// restyle schemes were deleted. Kept single-member so the persisted settings property/settings.ini
// key stay valid.
internal enum NetworkMonitorRenderVariant { Classic }

// PowerThermal's render path is hard-coded to Classic; the four OLED-safe restyle schemes were
// deleted. Kept single-member so the persisted settings property/settings.ini key stay valid.
internal enum PowerThermalRenderVariant { Classic }

// ConnectionCheck's render path is hard-coded to Classic; the four OLED-safe restyle schemes were
// deleted. Kept single-member so the persisted settings property/settings.ini key stay valid.
internal enum ConnectionCheckRenderVariant { Classic }

// RadialDial (added 1.0.4.57) is not a paint-only skin like the other four members: it replaces the
// flat button grid with an expandable fan menu and therefore also changes hit-testing and window
// sizing (see Core/OperationForm.RadialDial.cs). Keep it last so existing settings.ini values for
// the paint-only variants are unaffected.
internal enum OperationRenderVariant { Classic, Typographic, AmberHud, WarmCard, Phosphor, RadialDial }

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

internal enum PowerThermalAutoDirection
{
    Left,
    Down
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

internal enum OperationPrimaryPanelMode
{
    Auto,
    WindowsButton,
    MemoryPie,
    Hidden
}

internal sealed class WidgetSettings
{
    public const string MetricCpu = "CPU";
    public const string MetricMemory = "Memory";
    public const string MetricDisk = "Disk";
    public const string MetricNetwork = "Network";
    public const string MetricGpu = "GPU";
    public const string MetricNpu = "NPU";
    public const int MinWidth = 260;
    public const int MaxWidth = 1800;
    public const int MinHeight = 86;
    public const int MaxHeight = 700;
    public const int MinCodexRadarWidth = 130;
    public const int MaxCodexRadarWidth = 900;
    public const int MinCodexRadarHeight = 44;
    public const int MaxCodexRadarHeight = 240;
    public const int DefaultCodexRadarWidth = 580;
    private const int PreviousDefaultCodexRadarWidth = 620;
    private const int CodexRadarEvenRowWidthReduction = 40;
    public const int CodexRadarHiddenServiceHealthPanelWidthReduction = 76;
    public const int CodexRadarCompactQuotaWidthReduction = 122;
    public const int MinPowerThermalWidth = 90;
    public const int MaxPowerThermalWidth = 900;
    public const int MinPowerThermalHeight = 44;
    public const int MaxPowerThermalHeight = 240;
    public const int MinNetworkMonitorWidth = 260;
    public const int MaxNetworkMonitorWidth = 1000;
    public const int MinNetworkMonitorHeight = 112;
    public const int MaxNetworkMonitorHeight = 300;
    public const int MinConnectionCheckWidth = 130;
    public const int MaxConnectionCheckWidth = 1000;
    public const int MinConnectionCheckHeight = 56;
    public const int MaxConnectionCheckHeight = 320;
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
    public const int MaxPowerThermalAutoHeight = 900;
    public const int MinPowerThermalVisibleAlerts = 1;
    public const int MaxPowerThermalVisibleAlerts = 8;
    public const int DefaultPowerThermalVisibleAlerts = 3;
    public const int MinPowerThermalManualEnergySaverThresholdPercent = 0;
    public const int MaxPowerThermalManualEnergySaverThresholdPercent = 100;
    public const int DefaultPowerThermalManualEnergySaverThresholdPercent = 30;
    public const int MinBackgroundTransparency = 0;
    public const int MaxBackgroundTransparency = 90;
    public const int DefaultBackgroundTransparency = 9;
    public const int MinBorderTransparency = 0;
    public const int MaxBorderTransparency = 100;
    public const int DefaultConnectionCheckBorderTransparency = 65;
    public const int MinOperationButtonSize = 36;
    public const int MaxOperationButtonSize = 120;
    public const int MinOperationOffset = 0;
    public const int MaxOperationOffset = 4000;
    public const int MinAutoHoverOpacityIdleSeconds = 1;
    public const int MaxAutoHoverOpacityIdleSeconds = 300;
    public const int DefaultAutoHoverOpacityIdleSeconds = 60;
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
    public const int ResolutionCompatibilityReferenceWidth = 2880;
    public const int ResolutionCompatibilityReferenceHeight = 1800;
    public const int MinResolutionCompatibilityScalePercent = 40;
    public const int MaxResolutionCompatibilityScalePercent = 200;
    public const int DefaultResolutionCompatibilityScalePercent = 100;
    private const int CurrentSettingsVersion = 63;
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
    public const int MinCodexRadarManualLeftPercent = 35;
    public const int MaxCodexRadarManualLeftPercent = 78;
    public const int DefaultCodexRadarManualLeftPercent = 53;
    public const int MinCodexRadarManualGapPixels = 0;
    public const int MaxCodexRadarManualGapPixels = 18;
    public const int DefaultCodexRadarManualGapPixels = 4;
    public const int MinCodexRadarManualEfficiencyTextWidthPixels = 32;
    public const int MaxCodexRadarManualEfficiencyTextWidthPixels = 86;
    public const int DefaultCodexRadarManualEfficiencyTextWidthPixels = 48;
    public const int MinCodexRadarManualQuotaRowsWidthPixels = 88;
    public const int MaxCodexRadarManualQuotaRowsWidthPixels = 190;
    public const int DefaultCodexRadarManualQuotaRowsWidthPixels = 108;
    public const int MinCodexRadarManualIqStatusWidthPixels = 36;
    public const int MaxCodexRadarManualIqStatusWidthPixels = 92;
    public const int DefaultCodexRadarManualIqStatusWidthPixels = 52;
    public const int MinCodexRadarManualTextScalePercent = 70;
    public const int MaxCodexRadarManualTextScalePercent = 140;
    public const int DefaultCodexRadarManualTextScalePercent = 100;
    public const int MinCodexRadarManualRingScalePercent = 70;
    public const int MaxCodexRadarManualRingScalePercent = 135;
    public const int DefaultCodexRadarManualRingScalePercent = 100;
    public const int MinCodexRadarManualElementOffsetPixels = -240;
    public const int MaxCodexRadarManualElementOffsetPixels = 240;
    public static readonly string[] DefaultMetricOrder = new string[]
    {
        MetricCpu,
        MetricMemory,
        MetricDisk,
        MetricNetwork,
        MetricGpu,
        MetricNpu
    };
    public const string ModuleMain = "Main";
    public const string ModuleCodexRadar = "CodexRadar";
    public const string ModuleClaudeRadar = "ClaudeRadar";
    public const string ModulePowerThermal = "PowerThermal";
    public const string ModuleNetworkMonitor = "NetworkMonitor";
    public const string ModuleConnectionCheck = "ConnectionCheck";
    public const string ModuleOperation = "Operation";

    public int Width { get; set; }
    public int Height { get; set; }
    public int LeftX { get; set; }
    public int BottomY { get; set; }
    public int BackgroundTransparencyPercent { get; set; }
    public int ApplicationTransparencyPercent { get; set; }
    public int CodexRadarWidth { get; set; }
    public int CodexRadarHeight { get; set; }
    public int CodexRadarLeftX { get; set; }
    public int CodexRadarBottomY { get; set; }
    public int CodexRadarTransparencyPercent { get; set; }
    public bool CodexRadarEnabled { get; set; }
    public bool ClaudeRadarEnabled { get; set; }
    public int ClaudeRadarWidth { get; set; }
    public int ClaudeRadarHeight { get; set; }
    public int ClaudeRadarLeftX { get; set; }
    public int ClaudeRadarBottomY { get; set; }
    public int ClaudeRadarTransparencyPercent { get; set; }
    public bool CodexRadarManualLayoutEnabled { get; set; }
    public int CodexRadarManualLeftPercent { get; set; }
    public int CodexRadarManualGapPixels { get; set; }
    public int CodexRadarManualEfficiencyTextWidthPixels { get; set; }
    public int CodexRadarManualQuotaRowsWidthPixels { get; set; }
    public int CodexRadarManualIqStatusWidthPixels { get; set; }
    public int CodexRadarManualTextScalePercent { get; set; }
    public int CodexRadarManualRingScalePercent { get; set; }
    public int CodexRadarTimeEfficiencyRingOffsetX { get; set; }
    public int CodexRadarTimeEfficiencyRingOffsetY { get; set; }
    public int CodexRadarTimeEfficiencyTextOffsetX { get; set; }
    public int CodexRadarTimeEfficiencyTextOffsetY { get; set; }
    public int CodexRadarTokenEfficiencyRingOffsetX { get; set; }
    public int CodexRadarTokenEfficiencyRingOffsetY { get; set; }
    public int CodexRadarTokenEfficiencyTextOffsetX { get; set; }
    public int CodexRadarTokenEfficiencyTextOffsetY { get; set; }
    public int CodexRadarConnectionTopTextOffsetX { get; set; }
    public int CodexRadarConnectionTopTextOffsetY { get; set; }
    public int CodexRadarConnectionLineOffsetX { get; set; }
    public int CodexRadarConnectionLineOffsetY { get; set; }
    public int CodexRadarConnectionBottomTextOffsetX { get; set; }
    public int CodexRadarConnectionBottomTextOffsetY { get; set; }
    public int CodexRadarFiveHourQuotaRingOffsetX { get; set; }
    public int CodexRadarFiveHourQuotaRingOffsetY { get; set; }
    public int CodexRadarFiveHourQuotaTextOffsetX { get; set; }
    public int CodexRadarFiveHourQuotaTextOffsetY { get; set; }
    public int CodexRadarWeeklyQuotaRingOffsetX { get; set; }
    public int CodexRadarWeeklyQuotaRingOffsetY { get; set; }
    public int CodexRadarWeeklyQuotaTextOffsetX { get; set; }
    public int CodexRadarWeeklyQuotaTextOffsetY { get; set; }
    public int CodexRadarQuotaRadarLineOffsetX { get; set; }
    public int CodexRadarQuotaRadarLineOffsetY { get; set; }
    public int CodexRadarIqRingOffsetX { get; set; }
    public int CodexRadarIqRingOffsetY { get; set; }
    public int CodexRadarIqTextOffsetX { get; set; }
    public int CodexRadarIqTextOffsetY { get; set; }
    public int PowerThermalWidth { get; set; }
    public int PowerThermalHeight { get; set; }
    public int PowerThermalLeftX { get; set; }
    public int PowerThermalBottomY { get; set; }
    public int PowerThermalTransparencyPercent { get; set; }
    public bool PowerThermalAutoSizeEnabled { get; set; }
    public PowerThermalAutoDirection PowerThermalAutoDirection { get; set; }
    public int PowerThermalVisibleAlertCount { get; set; }
    public int PowerThermalManualEnergySaverThresholdPercent { get; set; }
    public int NetworkMonitorWidth { get; set; }
    public int NetworkMonitorHeight { get; set; }
    public int NetworkMonitorLeftX { get; set; }
    public int NetworkMonitorBottomY { get; set; }
    public int NetworkMonitorTransparencyPercent { get; set; }
    public string NetworkMonitorAdapterId { get; set; }
    public NetworkStatusTestMode NetworkStatusTestMode { get; set; }
    public bool GfwProbeEnabled { get; set; }
    public int GfwProbeIntervalMinutes { get; set; }
    public int GfwProbeManualRefreshToken { get; set; }
    public int CloudEndpointTestSeed { get; set; }
    public int CloudStatusRegionMask { get; set; }
    public int ConnectionCheckWidth { get; set; }
    public int ConnectionCheckHeight { get; set; }
    public int ConnectionCheckLeftX { get; set; }
    public int ConnectionCheckBottomY { get; set; }
    public int ConnectionCheckTransparencyPercent { get; set; }
    public int ConnectionCheckBorderTransparencyPercent { get; set; }
    public int ConnectionCheckIntervalSeconds { get; set; }
    public int ConnectionCheckManualRefreshToken { get; set; }
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
    public bool PowerResumeRestartEnabled { get; set; }
    public bool FallbackDisconnectedDisplaysEnabled { get; set; }
    public string MainDisplayDeviceName { get; set; }
    public string CodexRadarDisplayDeviceName { get; set; }
    public string ClaudeRadarDisplayDeviceName { get; set; }
    public string PowerThermalDisplayDeviceName { get; set; }
    public string NetworkMonitorDisplayDeviceName { get; set; }
    public string ConnectionCheckDisplayDeviceName { get; set; }
    public string OperationDisplayDeviceName { get; set; }
    public bool ResolutionCompatibilityModeEnabled { get; set; }
    public int ResolutionCompatibilityScalePercent { get; set; }
    public int LayoutWorkAreaLeft { get; set; }
    public int LayoutWorkAreaTop { get; set; }
    public int LayoutWorkAreaWidth { get; set; }
    public int LayoutWorkAreaHeight { get; set; }
    public int CodexRadarLayoutWorkAreaLeft { get; set; }
    public int CodexRadarLayoutWorkAreaTop { get; set; }
    public int CodexRadarLayoutWorkAreaWidth { get; set; }
    public int CodexRadarLayoutWorkAreaHeight { get; set; }
    public int ClaudeRadarLayoutWorkAreaLeft { get; set; }
    public int ClaudeRadarLayoutWorkAreaTop { get; set; }
    public int ClaudeRadarLayoutWorkAreaWidth { get; set; }
    public int ClaudeRadarLayoutWorkAreaHeight { get; set; }
    public int PowerThermalLayoutWorkAreaLeft { get; set; }
    public int PowerThermalLayoutWorkAreaTop { get; set; }
    public int PowerThermalLayoutWorkAreaWidth { get; set; }
    public int PowerThermalLayoutWorkAreaHeight { get; set; }
    public int NetworkMonitorLayoutWorkAreaLeft { get; set; }
    public int NetworkMonitorLayoutWorkAreaTop { get; set; }
    public int NetworkMonitorLayoutWorkAreaWidth { get; set; }
    public int NetworkMonitorLayoutWorkAreaHeight { get; set; }
    public int ConnectionCheckLayoutWorkAreaLeft { get; set; }
    public int ConnectionCheckLayoutWorkAreaTop { get; set; }
    public int ConnectionCheckLayoutWorkAreaWidth { get; set; }
    public int ConnectionCheckLayoutWorkAreaHeight { get; set; }
    public int OperationLayoutWorkAreaLeft { get; set; }
    public int OperationLayoutWorkAreaTop { get; set; }
    public int OperationLayoutWorkAreaWidth { get; set; }
    public int OperationLayoutWorkAreaHeight { get; set; }
    public WidgetVisibilityMode VisibilityMode { get; set; }
    public bool VisibilityOverlapIgnoresOperationPanelEnabled { get; set; }
    public ClickThroughMode ClickThroughMode { get; set; }
    public bool StartupEnabled { get; set; }
    public bool ShowCpu { get; set; }
    public bool ShowMemory { get; set; }
    public bool ShowDisk { get; set; }
    public bool ShowNetwork { get; set; }
    public bool ShowGpu { get; set; }
    public bool ShowNpu { get; set; }
    public bool AlertTestEnabled { get; set; }
    public ThermalTestMode ThermalTestMode { get; set; }
    public CodexRadarTestMode CodexRadarTestMode { get; set; }
    public ServiceHealthTestMode ServiceHealthTestMode { get; set; }
    public bool CodexRadarRandomTestEnabled { get; set; }
    public bool CodexRadarRandomTestAutoRefresh { get; set; }
    public int CodexRadarRandomTestRefreshToken { get; set; }
    public CodexRadarRenderVariant CodexRadarRenderVariant { get; set; }
    public MainWidgetRenderVariant MainWidgetRenderVariant { get; set; }
    public NetworkMonitorRenderVariant NetworkMonitorRenderVariant { get; set; }
    public PowerThermalRenderVariant PowerThermalRenderVariant { get; set; }
    public ConnectionCheckRenderVariant ConnectionCheckRenderVariant { get; set; }
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
    public bool ClaudeRadarJsonEnabled { get; set; }
    public bool ClaudeRadarHomepageFallbackEnabled { get; set; }
    public bool ClaudeRadarCommunityRatingsEnabled { get; set; }
    public bool ClaudeRadarLocalQuotaFallbackEnabled { get; set; }
    public bool ClaudeRadarRandomTestEnabled { get; set; }
    public bool ClaudeRadarRandomTestAutoRefresh { get; set; }
    public int ClaudeRadarRandomTestRefreshToken { get; set; }
    public int ClaudeRadarServiceProbeToken { get; set; }
    public int DeepSeekApiKeyRevision { get; set; }
    public bool AiRequestProtectionAutoEnabled { get; set; }
    public bool AiRequestProtectionManualBlockEnabled { get; set; }
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
    public string ClaudeRadarModelKey { get; set; }
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
    public bool OperationRadialCoreAutoHideKeepAliveEnabled { get; set; }
    public int OperationRadialIdleCollapseSeconds { get; set; }
    public bool OperationRadialIdleResetOnInteractionEnabled { get; set; }
    public bool OperationRadialKeepOpenAfterLeafClickEnabled { get; set; }
    public bool OperationSettingsLogicExtensionEnabled { get; set; }
    public bool BurnInHiddenModeColorProtectionEnabled { get; set; }
    public string[] MetricOrder { get; set; }

    public static string SettingsPath
    {
        get { return Path.Combine(Logger.DirectoryPath, "settings.ini"); }
    }

    public WidgetSettings()
    {
        WidgetSettings defaults = CreateDefaults();
        this.Width = defaults.Width;
        this.Height = defaults.Height;
        this.LeftX = defaults.LeftX;
        this.BottomY = defaults.BottomY;
        this.BackgroundTransparencyPercent = defaults.BackgroundTransparencyPercent;
        this.ApplicationTransparencyPercent = defaults.ApplicationTransparencyPercent;
        this.CodexRadarWidth = defaults.CodexRadarWidth;
        this.CodexRadarHeight = defaults.CodexRadarHeight;
        this.CodexRadarLeftX = defaults.CodexRadarLeftX;
        this.CodexRadarBottomY = defaults.CodexRadarBottomY;
        this.CodexRadarTransparencyPercent = defaults.CodexRadarTransparencyPercent;
        this.CodexRadarEnabled = defaults.CodexRadarEnabled;
        this.ClaudeRadarEnabled = defaults.ClaudeRadarEnabled;
        this.ClaudeRadarWidth = defaults.ClaudeRadarWidth;
        this.ClaudeRadarHeight = defaults.ClaudeRadarHeight;
        this.ClaudeRadarLeftX = defaults.ClaudeRadarLeftX;
        this.ClaudeRadarBottomY = defaults.ClaudeRadarBottomY;
        this.ClaudeRadarTransparencyPercent = defaults.ClaudeRadarTransparencyPercent;
        this.CodexRadarManualLayoutEnabled = defaults.CodexRadarManualLayoutEnabled;
        this.CodexRadarManualLeftPercent = defaults.CodexRadarManualLeftPercent;
        this.CodexRadarManualGapPixels = defaults.CodexRadarManualGapPixels;
        this.CodexRadarManualEfficiencyTextWidthPixels = defaults.CodexRadarManualEfficiencyTextWidthPixels;
        this.CodexRadarManualQuotaRowsWidthPixels = defaults.CodexRadarManualQuotaRowsWidthPixels;
        this.CodexRadarManualIqStatusWidthPixels = defaults.CodexRadarManualIqStatusWidthPixels;
        this.CodexRadarManualTextScalePercent = defaults.CodexRadarManualTextScalePercent;
        this.CodexRadarManualRingScalePercent = defaults.CodexRadarManualRingScalePercent;
        this.CodexRadarTimeEfficiencyRingOffsetX = defaults.CodexRadarTimeEfficiencyRingOffsetX;
        this.CodexRadarTimeEfficiencyRingOffsetY = defaults.CodexRadarTimeEfficiencyRingOffsetY;
        this.CodexRadarTimeEfficiencyTextOffsetX = defaults.CodexRadarTimeEfficiencyTextOffsetX;
        this.CodexRadarTimeEfficiencyTextOffsetY = defaults.CodexRadarTimeEfficiencyTextOffsetY;
        this.CodexRadarTokenEfficiencyRingOffsetX = defaults.CodexRadarTokenEfficiencyRingOffsetX;
        this.CodexRadarTokenEfficiencyRingOffsetY = defaults.CodexRadarTokenEfficiencyRingOffsetY;
        this.CodexRadarTokenEfficiencyTextOffsetX = defaults.CodexRadarTokenEfficiencyTextOffsetX;
        this.CodexRadarTokenEfficiencyTextOffsetY = defaults.CodexRadarTokenEfficiencyTextOffsetY;
        this.CodexRadarConnectionTopTextOffsetX = defaults.CodexRadarConnectionTopTextOffsetX;
        this.CodexRadarConnectionTopTextOffsetY = defaults.CodexRadarConnectionTopTextOffsetY;
        this.CodexRadarConnectionLineOffsetX = defaults.CodexRadarConnectionLineOffsetX;
        this.CodexRadarConnectionLineOffsetY = defaults.CodexRadarConnectionLineOffsetY;
        this.CodexRadarConnectionBottomTextOffsetX = defaults.CodexRadarConnectionBottomTextOffsetX;
        this.CodexRadarConnectionBottomTextOffsetY = defaults.CodexRadarConnectionBottomTextOffsetY;
        this.CodexRadarFiveHourQuotaRingOffsetX = defaults.CodexRadarFiveHourQuotaRingOffsetX;
        this.CodexRadarFiveHourQuotaRingOffsetY = defaults.CodexRadarFiveHourQuotaRingOffsetY;
        this.CodexRadarFiveHourQuotaTextOffsetX = defaults.CodexRadarFiveHourQuotaTextOffsetX;
        this.CodexRadarFiveHourQuotaTextOffsetY = defaults.CodexRadarFiveHourQuotaTextOffsetY;
        this.CodexRadarWeeklyQuotaRingOffsetX = defaults.CodexRadarWeeklyQuotaRingOffsetX;
        this.CodexRadarWeeklyQuotaRingOffsetY = defaults.CodexRadarWeeklyQuotaRingOffsetY;
        this.CodexRadarWeeklyQuotaTextOffsetX = defaults.CodexRadarWeeklyQuotaTextOffsetX;
        this.CodexRadarWeeklyQuotaTextOffsetY = defaults.CodexRadarWeeklyQuotaTextOffsetY;
        this.CodexRadarQuotaRadarLineOffsetX = defaults.CodexRadarQuotaRadarLineOffsetX;
        this.CodexRadarQuotaRadarLineOffsetY = defaults.CodexRadarQuotaRadarLineOffsetY;
        this.CodexRadarIqRingOffsetX = defaults.CodexRadarIqRingOffsetX;
        this.CodexRadarIqRingOffsetY = defaults.CodexRadarIqRingOffsetY;
        this.CodexRadarIqTextOffsetX = defaults.CodexRadarIqTextOffsetX;
        this.CodexRadarIqTextOffsetY = defaults.CodexRadarIqTextOffsetY;
        this.PowerThermalWidth = defaults.PowerThermalWidth;
        this.PowerThermalHeight = defaults.PowerThermalHeight;
        this.PowerThermalLeftX = defaults.PowerThermalLeftX;
        this.PowerThermalBottomY = defaults.PowerThermalBottomY;
        this.PowerThermalTransparencyPercent = defaults.PowerThermalTransparencyPercent;
        this.PowerThermalAutoSizeEnabled = defaults.PowerThermalAutoSizeEnabled;
        this.PowerThermalAutoDirection = defaults.PowerThermalAutoDirection;
        this.PowerThermalVisibleAlertCount = defaults.PowerThermalVisibleAlertCount;
        this.PowerThermalManualEnergySaverThresholdPercent = defaults.PowerThermalManualEnergySaverThresholdPercent;
        this.NetworkMonitorWidth = defaults.NetworkMonitorWidth;
        this.NetworkMonitorHeight = defaults.NetworkMonitorHeight;
        this.NetworkMonitorLeftX = defaults.NetworkMonitorLeftX;
        this.NetworkMonitorBottomY = defaults.NetworkMonitorBottomY;
        this.NetworkMonitorTransparencyPercent = defaults.NetworkMonitorTransparencyPercent;
        this.NetworkMonitorAdapterId = defaults.NetworkMonitorAdapterId;
        this.NetworkStatusTestMode = defaults.NetworkStatusTestMode;
        this.GfwProbeEnabled = defaults.GfwProbeEnabled;
        this.GfwProbeIntervalMinutes = defaults.GfwProbeIntervalMinutes;
        this.GfwProbeManualRefreshToken = 0;
        this.CloudEndpointTestSeed = defaults.CloudEndpointTestSeed;
        this.CloudStatusRegionMask = defaults.CloudStatusRegionMask;
        this.ConnectionCheckWidth = defaults.ConnectionCheckWidth;
        this.ConnectionCheckHeight = defaults.ConnectionCheckHeight;
        this.ConnectionCheckLeftX = defaults.ConnectionCheckLeftX;
        this.ConnectionCheckBottomY = defaults.ConnectionCheckBottomY;
        this.ConnectionCheckTransparencyPercent = defaults.ConnectionCheckTransparencyPercent;
        this.ConnectionCheckBorderTransparencyPercent = defaults.ConnectionCheckBorderTransparencyPercent;
        this.ConnectionCheckIntervalSeconds = defaults.ConnectionCheckIntervalSeconds;
        this.ConnectionCheckManualRefreshToken = 0;
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
        this.PowerResumeRestartEnabled = defaults.PowerResumeRestartEnabled;
        this.FallbackDisconnectedDisplaysEnabled = defaults.FallbackDisconnectedDisplaysEnabled;
        this.MainDisplayDeviceName = defaults.MainDisplayDeviceName;
        this.CodexRadarDisplayDeviceName = defaults.CodexRadarDisplayDeviceName;
        this.ClaudeRadarDisplayDeviceName = defaults.ClaudeRadarDisplayDeviceName;
        this.PowerThermalDisplayDeviceName = defaults.PowerThermalDisplayDeviceName;
        this.NetworkMonitorDisplayDeviceName = defaults.NetworkMonitorDisplayDeviceName;
        this.ConnectionCheckDisplayDeviceName = defaults.ConnectionCheckDisplayDeviceName;
        this.OperationDisplayDeviceName = defaults.OperationDisplayDeviceName;
        this.ResolutionCompatibilityModeEnabled = defaults.ResolutionCompatibilityModeEnabled;
        this.ResolutionCompatibilityScalePercent = defaults.ResolutionCompatibilityScalePercent;
        this.LayoutWorkAreaLeft = defaults.LayoutWorkAreaLeft;
        this.LayoutWorkAreaTop = defaults.LayoutWorkAreaTop;
        this.LayoutWorkAreaWidth = defaults.LayoutWorkAreaWidth;
        this.LayoutWorkAreaHeight = defaults.LayoutWorkAreaHeight;
        this.CodexRadarLayoutWorkAreaLeft = defaults.CodexRadarLayoutWorkAreaLeft;
        this.CodexRadarLayoutWorkAreaTop = defaults.CodexRadarLayoutWorkAreaTop;
        this.CodexRadarLayoutWorkAreaWidth = defaults.CodexRadarLayoutWorkAreaWidth;
        this.CodexRadarLayoutWorkAreaHeight = defaults.CodexRadarLayoutWorkAreaHeight;
        this.ClaudeRadarLayoutWorkAreaLeft = defaults.ClaudeRadarLayoutWorkAreaLeft;
        this.ClaudeRadarLayoutWorkAreaTop = defaults.ClaudeRadarLayoutWorkAreaTop;
        this.ClaudeRadarLayoutWorkAreaWidth = defaults.ClaudeRadarLayoutWorkAreaWidth;
        this.ClaudeRadarLayoutWorkAreaHeight = defaults.ClaudeRadarLayoutWorkAreaHeight;
        this.PowerThermalLayoutWorkAreaLeft = defaults.PowerThermalLayoutWorkAreaLeft;
        this.PowerThermalLayoutWorkAreaTop = defaults.PowerThermalLayoutWorkAreaTop;
        this.PowerThermalLayoutWorkAreaWidth = defaults.PowerThermalLayoutWorkAreaWidth;
        this.PowerThermalLayoutWorkAreaHeight = defaults.PowerThermalLayoutWorkAreaHeight;
        this.NetworkMonitorLayoutWorkAreaLeft = defaults.NetworkMonitorLayoutWorkAreaLeft;
        this.NetworkMonitorLayoutWorkAreaTop = defaults.NetworkMonitorLayoutWorkAreaTop;
        this.NetworkMonitorLayoutWorkAreaWidth = defaults.NetworkMonitorLayoutWorkAreaWidth;
        this.NetworkMonitorLayoutWorkAreaHeight = defaults.NetworkMonitorLayoutWorkAreaHeight;
        this.ConnectionCheckLayoutWorkAreaLeft = defaults.ConnectionCheckLayoutWorkAreaLeft;
        this.ConnectionCheckLayoutWorkAreaTop = defaults.ConnectionCheckLayoutWorkAreaTop;
        this.ConnectionCheckLayoutWorkAreaWidth = defaults.ConnectionCheckLayoutWorkAreaWidth;
        this.ConnectionCheckLayoutWorkAreaHeight = defaults.ConnectionCheckLayoutWorkAreaHeight;
        this.OperationLayoutWorkAreaLeft = defaults.OperationLayoutWorkAreaLeft;
        this.OperationLayoutWorkAreaTop = defaults.OperationLayoutWorkAreaTop;
        this.OperationLayoutWorkAreaWidth = defaults.OperationLayoutWorkAreaWidth;
        this.OperationLayoutWorkAreaHeight = defaults.OperationLayoutWorkAreaHeight;
        this.VisibilityMode = defaults.VisibilityMode;
        this.VisibilityOverlapIgnoresOperationPanelEnabled = defaults.VisibilityOverlapIgnoresOperationPanelEnabled;
        this.ClickThroughMode = defaults.ClickThroughMode;
        this.StartupEnabled = Program.IsStartupEnabled();
        this.ShowCpu = true;
        this.ShowMemory = true;
        this.ShowDisk = true;
        this.ShowNetwork = true;
        this.ShowGpu = true;
        this.ShowNpu = true;
        this.AlertTestEnabled = defaults.AlertTestEnabled;
        this.ThermalTestMode = defaults.ThermalTestMode;
        this.CodexRadarTestMode = defaults.CodexRadarTestMode;
        this.ServiceHealthTestMode = defaults.ServiceHealthTestMode;
        this.CodexRadarRandomTestEnabled = defaults.CodexRadarRandomTestEnabled;
        this.CodexRadarRandomTestAutoRefresh = defaults.CodexRadarRandomTestAutoRefresh;
        this.CodexRadarRandomTestRefreshToken = defaults.CodexRadarRandomTestRefreshToken;
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
        this.ClaudeRadarJsonEnabled = defaults.ClaudeRadarJsonEnabled;
        this.ClaudeRadarHomepageFallbackEnabled = defaults.ClaudeRadarHomepageFallbackEnabled;
        this.ClaudeRadarCommunityRatingsEnabled = defaults.ClaudeRadarCommunityRatingsEnabled;
        this.ClaudeRadarLocalQuotaFallbackEnabled = defaults.ClaudeRadarLocalQuotaFallbackEnabled;
        this.ClaudeRadarRandomTestEnabled = defaults.ClaudeRadarRandomTestEnabled;
        this.ClaudeRadarRandomTestAutoRefresh = defaults.ClaudeRadarRandomTestAutoRefresh;
        this.ClaudeRadarRandomTestRefreshToken = defaults.ClaudeRadarRandomTestRefreshToken;
        this.ClaudeRadarServiceProbeToken = defaults.ClaudeRadarServiceProbeToken;
        this.DeepSeekApiKeyRevision = defaults.DeepSeekApiKeyRevision;
        this.AiRequestProtectionAutoEnabled = defaults.AiRequestProtectionAutoEnabled;
        this.AiRequestProtectionManualBlockEnabled = defaults.AiRequestProtectionManualBlockEnabled;
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
        this.ClaudeRadarModelKey = defaults.ClaudeRadarModelKey;
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
        this.OperationRadialCoreAutoHideKeepAliveEnabled = defaults.OperationRadialCoreAutoHideKeepAliveEnabled;
        this.OperationRadialIdleCollapseSeconds = defaults.OperationRadialIdleCollapseSeconds;
        this.OperationRadialIdleResetOnInteractionEnabled = defaults.OperationRadialIdleResetOnInteractionEnabled;
        this.OperationSettingsLogicExtensionEnabled = defaults.OperationSettingsLogicExtensionEnabled;
        this.BurnInHiddenModeColorProtectionEnabled = defaults.BurnInHiddenModeColorProtectionEnabled;
        this.MetricOrder = CloneMetricOrder(defaults.MetricOrder);
    }

    private WidgetSettings(bool skipDefaults)
    {
    }

    public static WidgetSettings CreateDefaults()
    {
        WidgetSettings settings = new WidgetSettings(true);
        settings.Width = 628;
        settings.Height = 400;
        settings.LeftX = 2252;
        settings.BottomY = 1557;
        settings.BackgroundTransparencyPercent = 40;
        settings.ApplicationTransparencyPercent = 0;
        settings.CodexRadarWidth = DefaultCodexRadarWidth;
        settings.CodexRadarHeight = 116;
        settings.CodexRadarLeftX = 2252;
        settings.CodexRadarBottomY = 470;
        settings.CodexRadarTransparencyPercent = 30;
        settings.CodexRadarEnabled = true;
        settings.ClaudeRadarEnabled = false;
        settings.ClaudeRadarWidth = DefaultCodexRadarWidth;
        settings.ClaudeRadarHeight = 116;
        settings.ClaudeRadarLeftX = 2252;
        settings.ClaudeRadarBottomY = 340;
        settings.ClaudeRadarTransparencyPercent = 30;
        settings.CodexRadarManualLayoutEnabled = false;
        settings.CodexRadarManualLeftPercent = DefaultCodexRadarManualLeftPercent;
        settings.CodexRadarManualGapPixels = DefaultCodexRadarManualGapPixels;
        settings.CodexRadarManualEfficiencyTextWidthPixels = DefaultCodexRadarManualEfficiencyTextWidthPixels;
        settings.CodexRadarManualQuotaRowsWidthPixels = DefaultCodexRadarManualQuotaRowsWidthPixels;
        settings.CodexRadarManualIqStatusWidthPixels = DefaultCodexRadarManualIqStatusWidthPixels;
        settings.CodexRadarManualTextScalePercent = DefaultCodexRadarManualTextScalePercent;
        settings.CodexRadarManualRingScalePercent = DefaultCodexRadarManualRingScalePercent;
        settings.PowerThermalWidth = 120;
        settings.PowerThermalHeight = 110;
        settings.PowerThermalLeftX = 2760;
        settings.PowerThermalBottomY = 582;
        settings.PowerThermalTransparencyPercent = 30;
        settings.PowerThermalAutoSizeEnabled = true;
        settings.PowerThermalAutoDirection = PowerThermalAutoDirection.Down;
        settings.PowerThermalVisibleAlertCount = 8;
        settings.PowerThermalManualEnergySaverThresholdPercent = DefaultPowerThermalManualEnergySaverThresholdPercent;
        settings.NetworkMonitorWidth = 520;
        settings.NetworkMonitorHeight = 250;
        settings.NetworkMonitorLeftX = 2360;
        settings.NetworkMonitorBottomY = 1799;
        settings.NetworkMonitorTransparencyPercent = 40;
        settings.NetworkMonitorAdapterId = string.Empty;
        settings.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        settings.GfwProbeEnabled = true;
        settings.GfwProbeIntervalMinutes = DefaultGfwProbeIntervalMinutes;
        settings.GfwProbeManualRefreshToken = 0;
        settings.CloudEndpointTestSeed = 0;
        settings.CloudStatusRegionMask = DefaultCloudStatusRegionMask;
        settings.ConnectionCheckWidth = 292;
        settings.ConnectionCheckHeight = 95;
        settings.ConnectionCheckLeftX = 2588;
        settings.ConnectionCheckBottomY = 355;
        settings.ConnectionCheckTransparencyPercent = 20;
        settings.ConnectionCheckBorderTransparencyPercent = 100;
        settings.ConnectionCheckIntervalSeconds = 600;
        settings.ConnectionCheckManualRefreshToken = 0;
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
        settings.PowerResumeRestartEnabled = true;
        settings.FallbackDisconnectedDisplaysEnabled = true;
        settings.MainDisplayDeviceName = string.Empty;
        settings.CodexRadarDisplayDeviceName = string.Empty;
        settings.ClaudeRadarDisplayDeviceName = string.Empty;
        settings.PowerThermalDisplayDeviceName = string.Empty;
        settings.NetworkMonitorDisplayDeviceName = string.Empty;
        settings.ConnectionCheckDisplayDeviceName = string.Empty;
        settings.OperationDisplayDeviceName = string.Empty;
        settings.ResolutionCompatibilityModeEnabled = false;
        settings.ResolutionCompatibilityScalePercent = DefaultResolutionCompatibilityScalePercent;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 60;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1740;
        settings.CodexRadarLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.CodexRadarLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.CodexRadarLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.CodexRadarLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.ClaudeRadarLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.ClaudeRadarLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.ClaudeRadarLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.ClaudeRadarLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.PowerThermalLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.PowerThermalLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.PowerThermalLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.PowerThermalLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.NetworkMonitorLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.NetworkMonitorLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.NetworkMonitorLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.NetworkMonitorLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.ConnectionCheckLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.ConnectionCheckLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.ConnectionCheckLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.ConnectionCheckLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.OperationLayoutWorkAreaLeft = settings.LayoutWorkAreaLeft;
        settings.OperationLayoutWorkAreaTop = settings.LayoutWorkAreaTop;
        settings.OperationLayoutWorkAreaWidth = settings.LayoutWorkAreaWidth;
        settings.OperationLayoutWorkAreaHeight = settings.LayoutWorkAreaHeight;
        settings.VisibilityMode = WidgetVisibilityMode.HideWhenFullscreen;
        settings.VisibilityOverlapIgnoresOperationPanelEnabled = true;
        settings.ClickThroughMode = ClickThroughMode.Auto;
        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.ShowCpu = true;
        settings.ShowMemory = true;
        settings.ShowDisk = true;
        settings.ShowNetwork = true;
        settings.ShowGpu = true;
        settings.ShowNpu = true;
        settings.AlertTestEnabled = false;
        settings.ThermalTestMode = ThermalTestMode.Off;
        settings.CodexRadarTestMode = CodexRadarTestMode.Off;
        settings.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        settings.CodexRadarRandomTestEnabled = false;
        settings.CodexRadarRandomTestAutoRefresh = false;
        settings.CodexRadarRandomTestRefreshToken = 0;
        settings.CodexRadarRenderVariant = CodexRadarRenderVariant.EvenRow;
        settings.MainWidgetRenderVariant = MainWidgetRenderVariant.Classic;
        settings.NetworkMonitorRenderVariant = NetworkMonitorRenderVariant.Classic;
        settings.PowerThermalRenderVariant = PowerThermalRenderVariant.Classic;
        settings.ConnectionCheckRenderVariant = ConnectionCheckRenderVariant.Classic;
        settings.OperationRenderVariant = OperationRenderVariant.Classic;
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
        settings.ClaudeRadarJsonEnabled = true;
        settings.ClaudeRadarHomepageFallbackEnabled = true;
        settings.ClaudeRadarCommunityRatingsEnabled = true;
        settings.ClaudeRadarLocalQuotaFallbackEnabled = true;
        settings.ClaudeRadarRandomTestEnabled = false;
        settings.ClaudeRadarRandomTestAutoRefresh = false;
        settings.ClaudeRadarRandomTestRefreshToken = 0;
        settings.ClaudeRadarServiceProbeToken = 0;
        settings.DeepSeekApiKeyRevision = 0;
        settings.AiRequestProtectionAutoEnabled = true;
        settings.AiRequestProtectionManualBlockEnabled = false;
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
        settings.ClaudeRadarModelKey = string.Empty;
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
        settings.OperationRadialCoreAutoHideKeepAliveEnabled = true;
        settings.OperationRadialIdleCollapseSeconds = DefaultOperationRadialIdleCollapseSeconds;
        settings.OperationRadialIdleResetOnInteractionEnabled = true;
        settings.OperationRadialKeepOpenAfterLeafClickEnabled = true;
        settings.OperationSettingsLogicExtensionEnabled = false;
        settings.BurnInHiddenModeColorProtectionEnabled = false;
        settings.MetricOrder = CloneMetricOrder(DefaultMetricOrder);
        ApplyUserDefaultSnapshot(settings);
        settings.Normalize();
        return settings;
    }

    private static void ApplyUserDefaultSnapshot(WidgetSettings settings)
    {
        // User-confirmed default snapshot captured from settings.ini on 2026-07-06.
        // Runtime refresh/probe tokens stay on their original zero defaults so a fresh profile
        // does not inherit stale manual-refresh counters or force network probe side effects.
        settings.Width = 628;
        settings.Height = 414;
        settings.LeftX = 2252;
        settings.BottomY = 1549;
        settings.BackgroundTransparencyPercent = 40;
        settings.ApplicationTransparencyPercent = 0;
        settings.CodexRadarWidth = 522;
        settings.CodexRadarHeight = 120;
        settings.CodexRadarLeftX = 2358;
        settings.CodexRadarBottomY = 425;
        settings.CodexRadarTransparencyPercent = 30;
        settings.CodexRadarEnabled = true;
        settings.ClaudeRadarEnabled = false;
        settings.ClaudeRadarWidth = settings.CodexRadarWidth;
        settings.ClaudeRadarHeight = 120;
        settings.ClaudeRadarLeftX = 2252;
        settings.ClaudeRadarBottomY = 290;
        settings.ClaudeRadarTransparencyPercent = 30;
        settings.CodexRadarManualLayoutEnabled = false;
        settings.CodexRadarManualLeftPercent = 53;
        settings.CodexRadarManualGapPixels = 4;
        settings.CodexRadarManualEfficiencyTextWidthPixels = 48;
        settings.CodexRadarManualQuotaRowsWidthPixels = 108;
        settings.CodexRadarManualIqStatusWidthPixels = 52;
        settings.CodexRadarManualTextScalePercent = 100;
        settings.CodexRadarManualRingScalePercent = 100;
        settings.CodexRadarTimeEfficiencyRingOffsetX = 0;
        settings.CodexRadarTimeEfficiencyRingOffsetY = 0;
        settings.CodexRadarTimeEfficiencyTextOffsetX = 0;
        settings.CodexRadarTimeEfficiencyTextOffsetY = 0;
        settings.CodexRadarTokenEfficiencyRingOffsetX = 0;
        settings.CodexRadarTokenEfficiencyRingOffsetY = 0;
        settings.CodexRadarTokenEfficiencyTextOffsetX = 0;
        settings.CodexRadarTokenEfficiencyTextOffsetY = 0;
        settings.CodexRadarConnectionTopTextOffsetX = 0;
        settings.CodexRadarConnectionTopTextOffsetY = 0;
        settings.CodexRadarConnectionLineOffsetX = 0;
        settings.CodexRadarConnectionLineOffsetY = 0;
        settings.CodexRadarConnectionBottomTextOffsetX = 0;
        settings.CodexRadarConnectionBottomTextOffsetY = 0;
        settings.CodexRadarFiveHourQuotaRingOffsetX = 0;
        settings.CodexRadarFiveHourQuotaRingOffsetY = 0;
        settings.CodexRadarFiveHourQuotaTextOffsetX = 0;
        settings.CodexRadarFiveHourQuotaTextOffsetY = 0;
        settings.CodexRadarWeeklyQuotaRingOffsetX = 0;
        settings.CodexRadarWeeklyQuotaRingOffsetY = 0;
        settings.CodexRadarWeeklyQuotaTextOffsetX = 0;
        settings.CodexRadarWeeklyQuotaTextOffsetY = 0;
        settings.CodexRadarQuotaRadarLineOffsetX = 0;
        settings.CodexRadarQuotaRadarLineOffsetY = 0;
        settings.CodexRadarIqRingOffsetX = 0;
        settings.CodexRadarIqRingOffsetY = 0;
        settings.CodexRadarIqTextOffsetX = 0;
        settings.CodexRadarIqTextOffsetY = 0;
        settings.PowerThermalWidth = 120;
        settings.PowerThermalHeight = 114;
        settings.PowerThermalLeftX = 2760;
        settings.PowerThermalBottomY = 540;
        settings.PowerThermalTransparencyPercent = 30;
        settings.PowerThermalAutoSizeEnabled = true;
        settings.PowerThermalAutoDirection = PowerThermalAutoDirection.Down;
        settings.PowerThermalVisibleAlertCount = 8;
        settings.PowerThermalManualEnergySaverThresholdPercent = DefaultPowerThermalManualEnergySaverThresholdPercent;
        settings.NetworkMonitorWidth = 520;
        settings.NetworkMonitorHeight = 250;
        settings.NetworkMonitorLeftX = 2360;
        settings.NetworkMonitorBottomY = 1799;
        settings.NetworkMonitorTransparencyPercent = 40;
        settings.NetworkMonitorAdapterId = "";
        settings.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        settings.GfwProbeEnabled = true;
        settings.GfwProbeIntervalMinutes = 30;
        settings.CloudEndpointTestSeed = 0;
        settings.CloudStatusRegionMask = 1;
        settings.ConnectionCheckWidth = 292;
        settings.ConnectionCheckHeight = 98;
        settings.ConnectionCheckLeftX = 2588;
        settings.ConnectionCheckBottomY = 305;
        settings.ConnectionCheckTransparencyPercent = 20;
        settings.ConnectionCheckBorderTransparencyPercent = 100;
        settings.ConnectionCheckIntervalSeconds = 600;
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
        settings.PowerResumeRestartEnabled = true;
        settings.FallbackDisconnectedDisplaysEnabled = true;
        settings.MainDisplayDeviceName = "";
        settings.CodexRadarDisplayDeviceName = "";
        settings.ClaudeRadarDisplayDeviceName = "";
        settings.PowerThermalDisplayDeviceName = "";
        settings.NetworkMonitorDisplayDeviceName = "";
        settings.ConnectionCheckDisplayDeviceName = "";
        settings.OperationDisplayDeviceName = "";
        settings.ResolutionCompatibilityModeEnabled = false;
        settings.ResolutionCompatibilityScalePercent = DefaultResolutionCompatibilityScalePercent;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 0;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1800;
        settings.CodexRadarLayoutWorkAreaLeft = 0;
        settings.CodexRadarLayoutWorkAreaTop = 0;
        settings.CodexRadarLayoutWorkAreaWidth = 2880;
        settings.CodexRadarLayoutWorkAreaHeight = 1800;
        settings.ClaudeRadarLayoutWorkAreaLeft = 0;
        settings.ClaudeRadarLayoutWorkAreaTop = 0;
        settings.ClaudeRadarLayoutWorkAreaWidth = 2880;
        settings.ClaudeRadarLayoutWorkAreaHeight = 1800;
        settings.PowerThermalLayoutWorkAreaLeft = 0;
        settings.PowerThermalLayoutWorkAreaTop = 0;
        settings.PowerThermalLayoutWorkAreaWidth = 2880;
        settings.PowerThermalLayoutWorkAreaHeight = 1800;
        settings.NetworkMonitorLayoutWorkAreaLeft = 0;
        settings.NetworkMonitorLayoutWorkAreaTop = 0;
        settings.NetworkMonitorLayoutWorkAreaWidth = 2880;
        settings.NetworkMonitorLayoutWorkAreaHeight = 1800;
        settings.ConnectionCheckLayoutWorkAreaLeft = 0;
        settings.ConnectionCheckLayoutWorkAreaTop = 0;
        settings.ConnectionCheckLayoutWorkAreaWidth = 2880;
        settings.ConnectionCheckLayoutWorkAreaHeight = 1800;
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
        settings.OperationSettingsLogicExtensionEnabled = false;
        settings.ShowCpu = true;
        settings.ShowMemory = true;
        settings.ShowDisk = true;
        settings.ShowNetwork = true;
        settings.ShowGpu = true;
        settings.ShowNpu = true;
        settings.AlertTestEnabled = false;
        settings.ThermalTestMode = ThermalTestMode.Off;
        settings.CodexRadarTestMode = CodexRadarTestMode.Off;
        settings.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        settings.CodexRadarRandomTestEnabled = false;
        settings.CodexRadarRandomTestAutoRefresh = false;
        settings.CodexRadarRenderVariant = CodexRadarRenderVariant.EvenRow;
        settings.MainWidgetRenderVariant = MainWidgetRenderVariant.Classic;
        settings.NetworkMonitorRenderVariant = NetworkMonitorRenderVariant.Classic;
        settings.PowerThermalRenderVariant = PowerThermalRenderVariant.Classic;
        settings.ConnectionCheckRenderVariant = ConnectionCheckRenderVariant.Classic;
        settings.OperationRenderVariant = OperationRenderVariant.Classic;
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
        settings.ClaudeRadarJsonEnabled = true;
        settings.ClaudeRadarHomepageFallbackEnabled = true;
        settings.ClaudeRadarCommunityRatingsEnabled = true;
        settings.ClaudeRadarLocalQuotaFallbackEnabled = true;
        settings.ClaudeRadarRandomTestEnabled = false;
        settings.ClaudeRadarRandomTestAutoRefresh = false;
        settings.AiRequestProtectionAutoEnabled = true;
        settings.AiRequestProtectionManualBlockEnabled = false;
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
        settings.ClaudeRadarModelKey = "";
        settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Automatic;
        settings.DisplayTimeZoneId = "Tokyo Standard Time";
        settings.PerformanceMode = WidgetPerformanceMode.BatterySaver;
        settings.HoverOpacityEnabled = true;
        settings.AutoHoverOpacityIdleEnabled = true;
        settings.AutoHoverOpacityIdleSeconds = 40;
        settings.AutoHoverOpacityMaximizedEnabled = false;
        settings.BurnInHiddenModeColorProtectionEnabled = true;
        settings.MetricOrder = new[] { "CPU", "Memory", "Disk", "Network", "GPU", "NPU" };
    }

    public WidgetSettings Clone()
    {
        return new WidgetSettings(true)
        {
            Width = this.Width,
            Height = this.Height,
            LeftX = this.LeftX,
            BottomY = this.BottomY,
            BackgroundTransparencyPercent = this.BackgroundTransparencyPercent,
            ApplicationTransparencyPercent = this.ApplicationTransparencyPercent,
            CodexRadarWidth = this.CodexRadarWidth,
            CodexRadarHeight = this.CodexRadarHeight,
            CodexRadarLeftX = this.CodexRadarLeftX,
            CodexRadarBottomY = this.CodexRadarBottomY,
            CodexRadarTransparencyPercent = this.CodexRadarTransparencyPercent,
            CodexRadarEnabled = this.CodexRadarEnabled,
            ClaudeRadarEnabled = this.ClaudeRadarEnabled,
            ClaudeRadarWidth = this.ClaudeRadarWidth,
            ClaudeRadarHeight = this.ClaudeRadarHeight,
            ClaudeRadarLeftX = this.ClaudeRadarLeftX,
            ClaudeRadarBottomY = this.ClaudeRadarBottomY,
            ClaudeRadarTransparencyPercent = this.ClaudeRadarTransparencyPercent,
            CodexRadarManualLayoutEnabled = this.CodexRadarManualLayoutEnabled,
            CodexRadarManualLeftPercent = this.CodexRadarManualLeftPercent,
            CodexRadarManualGapPixels = this.CodexRadarManualGapPixels,
            CodexRadarManualEfficiencyTextWidthPixels = this.CodexRadarManualEfficiencyTextWidthPixels,
            CodexRadarManualQuotaRowsWidthPixels = this.CodexRadarManualQuotaRowsWidthPixels,
            CodexRadarManualIqStatusWidthPixels = this.CodexRadarManualIqStatusWidthPixels,
            CodexRadarManualTextScalePercent = this.CodexRadarManualTextScalePercent,
            CodexRadarManualRingScalePercent = this.CodexRadarManualRingScalePercent,
            CodexRadarTimeEfficiencyRingOffsetX = this.CodexRadarTimeEfficiencyRingOffsetX,
            CodexRadarTimeEfficiencyRingOffsetY = this.CodexRadarTimeEfficiencyRingOffsetY,
            CodexRadarTimeEfficiencyTextOffsetX = this.CodexRadarTimeEfficiencyTextOffsetX,
            CodexRadarTimeEfficiencyTextOffsetY = this.CodexRadarTimeEfficiencyTextOffsetY,
            CodexRadarTokenEfficiencyRingOffsetX = this.CodexRadarTokenEfficiencyRingOffsetX,
            CodexRadarTokenEfficiencyRingOffsetY = this.CodexRadarTokenEfficiencyRingOffsetY,
            CodexRadarTokenEfficiencyTextOffsetX = this.CodexRadarTokenEfficiencyTextOffsetX,
            CodexRadarTokenEfficiencyTextOffsetY = this.CodexRadarTokenEfficiencyTextOffsetY,
            CodexRadarConnectionTopTextOffsetX = this.CodexRadarConnectionTopTextOffsetX,
            CodexRadarConnectionTopTextOffsetY = this.CodexRadarConnectionTopTextOffsetY,
            CodexRadarConnectionLineOffsetX = this.CodexRadarConnectionLineOffsetX,
            CodexRadarConnectionLineOffsetY = this.CodexRadarConnectionLineOffsetY,
            CodexRadarConnectionBottomTextOffsetX = this.CodexRadarConnectionBottomTextOffsetX,
            CodexRadarConnectionBottomTextOffsetY = this.CodexRadarConnectionBottomTextOffsetY,
            CodexRadarFiveHourQuotaRingOffsetX = this.CodexRadarFiveHourQuotaRingOffsetX,
            CodexRadarFiveHourQuotaRingOffsetY = this.CodexRadarFiveHourQuotaRingOffsetY,
            CodexRadarFiveHourQuotaTextOffsetX = this.CodexRadarFiveHourQuotaTextOffsetX,
            CodexRadarFiveHourQuotaTextOffsetY = this.CodexRadarFiveHourQuotaTextOffsetY,
            CodexRadarWeeklyQuotaRingOffsetX = this.CodexRadarWeeklyQuotaRingOffsetX,
            CodexRadarWeeklyQuotaRingOffsetY = this.CodexRadarWeeklyQuotaRingOffsetY,
            CodexRadarWeeklyQuotaTextOffsetX = this.CodexRadarWeeklyQuotaTextOffsetX,
            CodexRadarWeeklyQuotaTextOffsetY = this.CodexRadarWeeklyQuotaTextOffsetY,
            CodexRadarQuotaRadarLineOffsetX = this.CodexRadarQuotaRadarLineOffsetX,
            CodexRadarQuotaRadarLineOffsetY = this.CodexRadarQuotaRadarLineOffsetY,
            CodexRadarIqRingOffsetX = this.CodexRadarIqRingOffsetX,
            CodexRadarIqRingOffsetY = this.CodexRadarIqRingOffsetY,
            CodexRadarIqTextOffsetX = this.CodexRadarIqTextOffsetX,
            CodexRadarIqTextOffsetY = this.CodexRadarIqTextOffsetY,
            PowerThermalWidth = this.PowerThermalWidth,
            PowerThermalHeight = this.PowerThermalHeight,
            PowerThermalLeftX = this.PowerThermalLeftX,
            PowerThermalBottomY = this.PowerThermalBottomY,
            PowerThermalTransparencyPercent = this.PowerThermalTransparencyPercent,
            PowerThermalAutoSizeEnabled = this.PowerThermalAutoSizeEnabled,
            PowerThermalAutoDirection = this.PowerThermalAutoDirection,
            PowerThermalVisibleAlertCount = this.PowerThermalVisibleAlertCount,
            PowerThermalManualEnergySaverThresholdPercent = this.PowerThermalManualEnergySaverThresholdPercent,
            NetworkMonitorWidth = this.NetworkMonitorWidth,
            NetworkMonitorHeight = this.NetworkMonitorHeight,
            NetworkMonitorLeftX = this.NetworkMonitorLeftX,
            NetworkMonitorBottomY = this.NetworkMonitorBottomY,
            NetworkMonitorTransparencyPercent = this.NetworkMonitorTransparencyPercent,
            NetworkMonitorAdapterId = this.NetworkMonitorAdapterId,
            NetworkStatusTestMode = this.NetworkStatusTestMode,
            GfwProbeEnabled = this.GfwProbeEnabled,
            GfwProbeIntervalMinutes = this.GfwProbeIntervalMinutes,
            GfwProbeManualRefreshToken = this.GfwProbeManualRefreshToken,
            CloudEndpointTestSeed = this.CloudEndpointTestSeed,
            CloudStatusRegionMask = this.CloudStatusRegionMask,
            ConnectionCheckWidth = this.ConnectionCheckWidth,
            ConnectionCheckHeight = this.ConnectionCheckHeight,
            ConnectionCheckLeftX = this.ConnectionCheckLeftX,
            ConnectionCheckBottomY = this.ConnectionCheckBottomY,
            ConnectionCheckTransparencyPercent = this.ConnectionCheckTransparencyPercent,
            ConnectionCheckBorderTransparencyPercent = this.ConnectionCheckBorderTransparencyPercent,
            ConnectionCheckIntervalSeconds = this.ConnectionCheckIntervalSeconds,
            ConnectionCheckManualRefreshToken = this.ConnectionCheckManualRefreshToken,
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
            PowerResumeRestartEnabled = this.PowerResumeRestartEnabled,
            FallbackDisconnectedDisplaysEnabled = this.FallbackDisconnectedDisplaysEnabled,
            MainDisplayDeviceName = this.MainDisplayDeviceName,
            CodexRadarDisplayDeviceName = this.CodexRadarDisplayDeviceName,
            ClaudeRadarDisplayDeviceName = this.ClaudeRadarDisplayDeviceName,
            PowerThermalDisplayDeviceName = this.PowerThermalDisplayDeviceName,
            NetworkMonitorDisplayDeviceName = this.NetworkMonitorDisplayDeviceName,
            ConnectionCheckDisplayDeviceName = this.ConnectionCheckDisplayDeviceName,
            OperationDisplayDeviceName = this.OperationDisplayDeviceName,
            ResolutionCompatibilityModeEnabled = this.ResolutionCompatibilityModeEnabled,
            ResolutionCompatibilityScalePercent = this.ResolutionCompatibilityScalePercent,
            LayoutWorkAreaLeft = this.LayoutWorkAreaLeft,
            LayoutWorkAreaTop = this.LayoutWorkAreaTop,
            LayoutWorkAreaWidth = this.LayoutWorkAreaWidth,
            LayoutWorkAreaHeight = this.LayoutWorkAreaHeight,
            CodexRadarLayoutWorkAreaLeft = this.CodexRadarLayoutWorkAreaLeft,
            CodexRadarLayoutWorkAreaTop = this.CodexRadarLayoutWorkAreaTop,
            CodexRadarLayoutWorkAreaWidth = this.CodexRadarLayoutWorkAreaWidth,
            CodexRadarLayoutWorkAreaHeight = this.CodexRadarLayoutWorkAreaHeight,
            ClaudeRadarLayoutWorkAreaLeft = this.ClaudeRadarLayoutWorkAreaLeft,
            ClaudeRadarLayoutWorkAreaTop = this.ClaudeRadarLayoutWorkAreaTop,
            ClaudeRadarLayoutWorkAreaWidth = this.ClaudeRadarLayoutWorkAreaWidth,
            ClaudeRadarLayoutWorkAreaHeight = this.ClaudeRadarLayoutWorkAreaHeight,
            PowerThermalLayoutWorkAreaLeft = this.PowerThermalLayoutWorkAreaLeft,
            PowerThermalLayoutWorkAreaTop = this.PowerThermalLayoutWorkAreaTop,
            PowerThermalLayoutWorkAreaWidth = this.PowerThermalLayoutWorkAreaWidth,
            PowerThermalLayoutWorkAreaHeight = this.PowerThermalLayoutWorkAreaHeight,
            NetworkMonitorLayoutWorkAreaLeft = this.NetworkMonitorLayoutWorkAreaLeft,
            NetworkMonitorLayoutWorkAreaTop = this.NetworkMonitorLayoutWorkAreaTop,
            NetworkMonitorLayoutWorkAreaWidth = this.NetworkMonitorLayoutWorkAreaWidth,
            NetworkMonitorLayoutWorkAreaHeight = this.NetworkMonitorLayoutWorkAreaHeight,
            ConnectionCheckLayoutWorkAreaLeft = this.ConnectionCheckLayoutWorkAreaLeft,
            ConnectionCheckLayoutWorkAreaTop = this.ConnectionCheckLayoutWorkAreaTop,
            ConnectionCheckLayoutWorkAreaWidth = this.ConnectionCheckLayoutWorkAreaWidth,
            ConnectionCheckLayoutWorkAreaHeight = this.ConnectionCheckLayoutWorkAreaHeight,
            OperationLayoutWorkAreaLeft = this.OperationLayoutWorkAreaLeft,
            OperationLayoutWorkAreaTop = this.OperationLayoutWorkAreaTop,
            OperationLayoutWorkAreaWidth = this.OperationLayoutWorkAreaWidth,
            OperationLayoutWorkAreaHeight = this.OperationLayoutWorkAreaHeight,
            VisibilityMode = this.VisibilityMode,
            VisibilityOverlapIgnoresOperationPanelEnabled = this.VisibilityOverlapIgnoresOperationPanelEnabled,
            ClickThroughMode = this.ClickThroughMode,
            StartupEnabled = this.StartupEnabled,
            ShowCpu = this.ShowCpu,
            ShowMemory = this.ShowMemory,
            ShowDisk = this.ShowDisk,
            ShowNetwork = this.ShowNetwork,
            ShowGpu = this.ShowGpu,
            ShowNpu = this.ShowNpu,
            AlertTestEnabled = this.AlertTestEnabled,
            ThermalTestMode = this.ThermalTestMode,
            CodexRadarTestMode = this.CodexRadarTestMode,
            ServiceHealthTestMode = this.ServiceHealthTestMode,
            CodexRadarRandomTestEnabled = this.CodexRadarRandomTestEnabled,
            CodexRadarRandomTestAutoRefresh = this.CodexRadarRandomTestAutoRefresh,
            CodexRadarRandomTestRefreshToken = this.CodexRadarRandomTestRefreshToken,
            CodexRadarRenderVariant = this.CodexRadarRenderVariant,
            MainWidgetRenderVariant = this.MainWidgetRenderVariant,
            NetworkMonitorRenderVariant = this.NetworkMonitorRenderVariant,
            PowerThermalRenderVariant = this.PowerThermalRenderVariant,
            ConnectionCheckRenderVariant = this.ConnectionCheckRenderVariant,
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
            ClaudeRadarJsonEnabled = this.ClaudeRadarJsonEnabled,
            ClaudeRadarHomepageFallbackEnabled = this.ClaudeRadarHomepageFallbackEnabled,
            ClaudeRadarCommunityRatingsEnabled = this.ClaudeRadarCommunityRatingsEnabled,
            ClaudeRadarLocalQuotaFallbackEnabled = this.ClaudeRadarLocalQuotaFallbackEnabled,
            ClaudeRadarRandomTestEnabled = this.ClaudeRadarRandomTestEnabled,
            ClaudeRadarRandomTestAutoRefresh = this.ClaudeRadarRandomTestAutoRefresh,
            ClaudeRadarRandomTestRefreshToken = this.ClaudeRadarRandomTestRefreshToken,
            ClaudeRadarServiceProbeToken = this.ClaudeRadarServiceProbeToken,
            DeepSeekApiKeyRevision = this.DeepSeekApiKeyRevision,
            AiRequestProtectionAutoEnabled = this.AiRequestProtectionAutoEnabled,
            AiRequestProtectionManualBlockEnabled = this.AiRequestProtectionManualBlockEnabled,
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
            ClaudeRadarModelKey = this.ClaudeRadarModelKey,
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
            OperationRadialCoreAutoHideKeepAliveEnabled = this.OperationRadialCoreAutoHideKeepAliveEnabled,
            OperationRadialIdleCollapseSeconds = this.OperationRadialIdleCollapseSeconds,
            OperationRadialIdleResetOnInteractionEnabled = this.OperationRadialIdleResetOnInteractionEnabled,
            OperationRadialKeepOpenAfterLeafClickEnabled = this.OperationRadialKeepOpenAfterLeafClickEnabled,
            OperationSettingsLogicExtensionEnabled = this.OperationSettingsLogicExtensionEnabled,
            BurnInHiddenModeColorProtectionEnabled = this.BurnInHiddenModeColorProtectionEnabled,
            MetricOrder = CloneMetricOrder(this.MetricOrder)
        };
    }

    public void Normalize()
    {
        this.Width = Clamp(this.Width, MinWidth, MaxWidth);
        this.Height = Clamp(this.Height, MinHeight, MaxHeight);
        this.BackgroundTransparencyPercent = Clamp(this.BackgroundTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ApplicationTransparencyPercent = Clamp(this.ApplicationTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.CodexRadarWidth = Clamp(this.CodexRadarWidth, MinCodexRadarWidth, MaxCodexRadarWidth);
        this.CodexRadarHeight = Clamp(this.CodexRadarHeight, MinCodexRadarHeight, MaxCodexRadarHeight);
        this.CodexRadarTransparencyPercent = Clamp(this.CodexRadarTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ClaudeRadarWidth = Clamp(this.ClaudeRadarWidth, MinCodexRadarWidth, MaxCodexRadarWidth);
        this.ClaudeRadarHeight = Clamp(this.ClaudeRadarHeight, MinCodexRadarHeight, MaxCodexRadarHeight);
        this.ClaudeRadarTransparencyPercent = Clamp(this.ClaudeRadarTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.CodexRadarManualLeftPercent = Clamp(this.CodexRadarManualLeftPercent, MinCodexRadarManualLeftPercent, MaxCodexRadarManualLeftPercent);
        this.CodexRadarManualGapPixels = Clamp(this.CodexRadarManualGapPixels, MinCodexRadarManualGapPixels, MaxCodexRadarManualGapPixels);
        this.CodexRadarManualEfficiencyTextWidthPixels = Clamp(
            this.CodexRadarManualEfficiencyTextWidthPixels,
            MinCodexRadarManualEfficiencyTextWidthPixels,
            MaxCodexRadarManualEfficiencyTextWidthPixels);
        this.CodexRadarManualQuotaRowsWidthPixels = Clamp(
            this.CodexRadarManualQuotaRowsWidthPixels,
            MinCodexRadarManualQuotaRowsWidthPixels,
            MaxCodexRadarManualQuotaRowsWidthPixels);
        this.CodexRadarManualIqStatusWidthPixels = Clamp(
            this.CodexRadarManualIqStatusWidthPixels,
            MinCodexRadarManualIqStatusWidthPixels,
            MaxCodexRadarManualIqStatusWidthPixels);
        this.CodexRadarManualTextScalePercent = Clamp(
            this.CodexRadarManualTextScalePercent,
            MinCodexRadarManualTextScalePercent,
            MaxCodexRadarManualTextScalePercent);
        this.CodexRadarManualRingScalePercent = Clamp(
            this.CodexRadarManualRingScalePercent,
            MinCodexRadarManualRingScalePercent,
            MaxCodexRadarManualRingScalePercent);
        ClampCodexRadarManualElementOffsets(this);
        this.PowerThermalWidth = Clamp(this.PowerThermalWidth, MinPowerThermalWidth, MaxPowerThermalWidth);
        this.PowerThermalHeight = Clamp(this.PowerThermalHeight, MinPowerThermalHeight, MaxPowerThermalHeight);
        this.PowerThermalTransparencyPercent = Clamp(this.PowerThermalTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.PowerThermalVisibleAlertCount = Clamp(this.PowerThermalVisibleAlertCount, MinPowerThermalVisibleAlerts, MaxPowerThermalVisibleAlerts);
        this.PowerThermalManualEnergySaverThresholdPercent = Clamp(
            this.PowerThermalManualEnergySaverThresholdPercent,
            MinPowerThermalManualEnergySaverThresholdPercent,
            MaxPowerThermalManualEnergySaverThresholdPercent);
        this.NetworkMonitorWidth = Clamp(this.NetworkMonitorWidth, MinNetworkMonitorWidth, MaxNetworkMonitorWidth);
        this.NetworkMonitorHeight = Clamp(this.NetworkMonitorHeight, MinNetworkMonitorHeight, MaxNetworkMonitorHeight);
        this.NetworkMonitorTransparencyPercent = Clamp(this.NetworkMonitorTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.NetworkMonitorAdapterId = (this.NetworkMonitorAdapterId ?? string.Empty).Trim();
        this.GfwProbeIntervalMinutes = Clamp(this.GfwProbeIntervalMinutes, MinGfwProbeIntervalMinutes, MaxGfwProbeIntervalMinutes);
        this.CloudStatusRegionMask &= CloudStatusRegionMaskAll;
        if (this.CloudStatusRegionMask == 0)
        {
            this.CloudStatusRegionMask = DefaultCloudStatusRegionMask;
        }

        this.ConnectionCheckWidth = Clamp(this.ConnectionCheckWidth, MinConnectionCheckWidth, MaxConnectionCheckWidth);
        this.ConnectionCheckHeight = Clamp(this.ConnectionCheckHeight, MinConnectionCheckHeight, MaxConnectionCheckHeight);
        this.ConnectionCheckTransparencyPercent = Clamp(this.ConnectionCheckTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ConnectionCheckBorderTransparencyPercent = Clamp(this.ConnectionCheckBorderTransparencyPercent, MinBorderTransparency, MaxBorderTransparency);
        this.ConnectionCheckIntervalSeconds = Clamp(this.ConnectionCheckIntervalSeconds, MinConnectionCheckIntervalSeconds, MaxConnectionCheckIntervalSeconds);
        if (!Enum.IsDefined(typeof(PowerThermalAutoDirection), this.PowerThermalAutoDirection))
        {
            this.PowerThermalAutoDirection = PowerThermalAutoDirection.Left;
        }

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

        this.ClaudeRadarModelKey = NormalizeClaudeRadarModelKey(this.ClaudeRadarModelKey);

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
        this.DeepSeekApiKeyRevision = Clamp(this.DeepSeekApiKeyRevision, 0, int.MaxValue);
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
        this.CodexRadarDisplayDeviceName = NormalizeDisplayDeviceName(this.CodexRadarDisplayDeviceName);
        this.ClaudeRadarDisplayDeviceName = NormalizeDisplayDeviceName(this.ClaudeRadarDisplayDeviceName);
        this.PowerThermalDisplayDeviceName = NormalizeDisplayDeviceName(this.PowerThermalDisplayDeviceName);
        this.NetworkMonitorDisplayDeviceName = NormalizeDisplayDeviceName(this.NetworkMonitorDisplayDeviceName);
        this.ConnectionCheckDisplayDeviceName = NormalizeDisplayDeviceName(this.ConnectionCheckDisplayDeviceName);
        this.OperationDisplayDeviceName = NormalizeDisplayDeviceName(this.OperationDisplayDeviceName);
        this.MetricOrder = NormalizeMetricOrder(this.MetricOrder);
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        EnsureUsableWorkArea(ref workArea);
        EnsureAllLayoutWorkAreaReferences(workArea);
        ClampLayoutToTargetWorkAreas(GetPrimaryScale());
    }

    public static WidgetSettings Load()
    {
        return LoadFromPath(SettingsPath, true);
    }

    private static WidgetSettings LoadFromPath(string path, bool saveAfterMigrationToSamePath)
    {
        WidgetSettings settings = new WidgetSettings();
        bool hasPixelPosition = false;
        int settingsVersion = 0;
        bool saveAfterMigration = false;

        try
        {
            if (File.Exists(path))
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

                    if (string.Equals(key, "LeftX", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "BottomY", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPixelPosition = true;
                    }

                    ApplyValue(settings, key, value);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (!hasPixelPosition)
        {
            WidgetSettings defaults = CreateDefaults();
            settings.Width = defaults.Width;
            settings.Height = defaults.Height;
            settings.LeftX = defaults.LeftX;
            settings.BottomY = defaults.BottomY;
        }

        if (settingsVersion > 0 && settingsVersion < 6)
        {
            ApplyCleanIpBadgeSizeMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 27)
        {
            settings.OperationPrimaryPanelMode = InferOperationPrimaryPanelModeFromLegacyBooleans(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion < 28)
        {
            ApplyMultiDisplayLayoutReferenceMigration(settings);
            saveAfterMigration = settingsVersion > 0;
        }

        if (settingsVersion > 0 && settingsVersion < 29)
        {
            ApplyCodexRadarServiceHealthPanelMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 30)
        {
            ApplyCodexRadarCompactQuotaWidthMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 31)
        {
            ApplyCodexRadarBalancedWidthMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 32)
        {
            ApplyCodexRadarBalancedWidthMigration(settings);
            saveAfterMigration = true;
        }

        if (settingsVersion > 0 && settingsVersion < 39)
        {
            ApplyCodexRadarEvenRowWidthMigration(settings);
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

        if (settingsVersion > 0 && settingsVersion < 57)
        {
            ApplyClaudeRadarWidthParityMigration(settings);
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

    private static void ApplyCleanIpBadgeSizeMigration(WidgetSettings settings)
    {
        int oldRight = settings.ConnectionCheckLeftX + settings.ConnectionCheckWidth;
        settings.ConnectionCheckWidth = Clamp((int)Math.Round(settings.NetworkMonitorWidth * 0.5f), MinConnectionCheckWidth, MaxConnectionCheckWidth);
        settings.ConnectionCheckHeight = Clamp((int)Math.Round(settings.NetworkMonitorHeight * 0.5f), MinConnectionCheckHeight, MaxConnectionCheckHeight);
        settings.ConnectionCheckLeftX = oldRight - settings.ConnectionCheckWidth;
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

    private static void ApplyMultiDisplayLayoutReferenceMigration(WidgetSettings settings)
    {
        Rectangle workArea = new Rectangle(
            settings.LayoutWorkAreaLeft,
            settings.LayoutWorkAreaTop,
            settings.LayoutWorkAreaWidth,
            settings.LayoutWorkAreaHeight);
        EnsureUsableWorkArea(ref workArea);
        settings.CaptureAllModuleLayoutWorkAreas(workArea);
    }

    private static void ApplyCodexRadarServiceHealthPanelMigration(WidgetSettings settings)
    {
        // The Rader/Claude/ChatGPT panel is hidden but retained for rollback; reclaim its
        // previous strip exactly once for saved configs so the window does not keep dead space.
        settings.CodexRadarWidth = Clamp(
            settings.CodexRadarWidth - CodexRadarHiddenServiceHealthPanelWidthReduction,
            MinCodexRadarWidth,
            MaxCodexRadarWidth);
    }

    private static void ApplyCodexRadarCompactQuotaWidthMigration(WidgetSettings settings)
    {
        // After the service panel is hidden, the quota rows only need their ring, reset text
        // and IQ status column. Reclaim the remaining blank strip once for saved configs.
        settings.CodexRadarWidth = Clamp(
            settings.CodexRadarWidth - CodexRadarCompactQuotaWidthReduction,
            MinCodexRadarWidth,
            MaxCodexRadarWidth);
    }

    private static void ApplyCodexRadarBalancedWidthMigration(WidgetSettings settings)
    {
        // The compact 430 px width clipped the quota module on high-DPI screens. Keep larger
        // user widths, but raise compact migrated configs back to the balanced default.
        settings.CodexRadarWidth = Clamp(
            Math.Max(settings.CodexRadarWidth, DefaultCodexRadarWidth),
            MinCodexRadarWidth,
            MaxCodexRadarWidth);
    }

    private static void ApplyCodexRadarEvenRowWidthMigration(WidgetSettings settings)
    {
        // Only compress the current EvenRow default-width layout once. Smaller user-tuned
        // windows are left untouched so a manual compact layout does not get squeezed again.
        if (settings.CodexRadarRenderVariant != CodexRadarRenderVariant.EvenRow ||
            settings.CodexRadarWidth < PreviousDefaultCodexRadarWidth)
        {
            return;
        }

        settings.CodexRadarWidth = Clamp(
            settings.CodexRadarWidth - CodexRadarEvenRowWidthReduction,
            MinCodexRadarWidth,
            MaxCodexRadarWidth);
    }

    private static void ApplyClaudeRadarWidthParityMigration(WidgetSettings settings)
    {
        // Claude Radar inherited the old 580 px snapshot after the shared radar had already
        // been compacted. Only migrate that untouched over-wide default; later manual widths
        // remain independent through the existing ClaudeRadarWidth setting.
        if (settings.ClaudeRadarWidth != DefaultCodexRadarWidth ||
            settings.ClaudeRadarWidth <= settings.CodexRadarWidth)
        {
            return;
        }

        settings.ClaudeRadarWidth = Clamp(settings.CodexRadarWidth, MinCodexRadarWidth, MaxCodexRadarWidth);
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
            "Width=" + this.Width,
            "Height=" + this.Height,
            "LeftX=" + this.LeftX,
            "BottomY=" + this.BottomY,
            "BackgroundTransparencyPercent=" + this.BackgroundTransparencyPercent,
            "ContentTransparencyPercent=" + this.ApplicationTransparencyPercent,
            "ApplicationTransparencyPercent=" + this.ApplicationTransparencyPercent,
            "CodexRadarWidth=" + this.CodexRadarWidth,
            "CodexRadarHeight=" + this.CodexRadarHeight,
            "CodexRadarLeftX=" + this.CodexRadarLeftX,
            "CodexRadarBottomY=" + this.CodexRadarBottomY,
            "CodexRadarTransparencyPercent=" + this.CodexRadarTransparencyPercent,
            "CodexRadarEnabled=" + this.CodexRadarEnabled,
            "ClaudeRadarEnabled=" + this.ClaudeRadarEnabled,
            "ClaudeRadarWidth=" + this.ClaudeRadarWidth,
            "ClaudeRadarHeight=" + this.ClaudeRadarHeight,
            "ClaudeRadarLeftX=" + this.ClaudeRadarLeftX,
            "ClaudeRadarBottomY=" + this.ClaudeRadarBottomY,
            "ClaudeRadarTransparencyPercent=" + this.ClaudeRadarTransparencyPercent,
            "CodexRadarManualLayoutEnabled=" + this.CodexRadarManualLayoutEnabled,
            "CodexRadarManualLeftPercent=" + this.CodexRadarManualLeftPercent,
            "CodexRadarManualGapPixels=" + this.CodexRadarManualGapPixels,
            "CodexRadarManualEfficiencyTextWidthPixels=" + this.CodexRadarManualEfficiencyTextWidthPixels,
            "CodexRadarManualQuotaRowsWidthPixels=" + this.CodexRadarManualQuotaRowsWidthPixels,
            "CodexRadarManualIqStatusWidthPixels=" + this.CodexRadarManualIqStatusWidthPixels,
            "CodexRadarManualTextScalePercent=" + this.CodexRadarManualTextScalePercent,
            "CodexRadarManualRingScalePercent=" + this.CodexRadarManualRingScalePercent,
            "CodexRadarTimeEfficiencyRingOffsetX=" + this.CodexRadarTimeEfficiencyRingOffsetX,
            "CodexRadarTimeEfficiencyRingOffsetY=" + this.CodexRadarTimeEfficiencyRingOffsetY,
            "CodexRadarTimeEfficiencyTextOffsetX=" + this.CodexRadarTimeEfficiencyTextOffsetX,
            "CodexRadarTimeEfficiencyTextOffsetY=" + this.CodexRadarTimeEfficiencyTextOffsetY,
            "CodexRadarTokenEfficiencyRingOffsetX=" + this.CodexRadarTokenEfficiencyRingOffsetX,
            "CodexRadarTokenEfficiencyRingOffsetY=" + this.CodexRadarTokenEfficiencyRingOffsetY,
            "CodexRadarTokenEfficiencyTextOffsetX=" + this.CodexRadarTokenEfficiencyTextOffsetX,
            "CodexRadarTokenEfficiencyTextOffsetY=" + this.CodexRadarTokenEfficiencyTextOffsetY,
            "CodexRadarConnectionTopTextOffsetX=" + this.CodexRadarConnectionTopTextOffsetX,
            "CodexRadarConnectionTopTextOffsetY=" + this.CodexRadarConnectionTopTextOffsetY,
            "CodexRadarConnectionLineOffsetX=" + this.CodexRadarConnectionLineOffsetX,
            "CodexRadarConnectionLineOffsetY=" + this.CodexRadarConnectionLineOffsetY,
            "CodexRadarConnectionBottomTextOffsetX=" + this.CodexRadarConnectionBottomTextOffsetX,
            "CodexRadarConnectionBottomTextOffsetY=" + this.CodexRadarConnectionBottomTextOffsetY,
            "CodexRadarFiveHourQuotaRingOffsetX=" + this.CodexRadarFiveHourQuotaRingOffsetX,
            "CodexRadarFiveHourQuotaRingOffsetY=" + this.CodexRadarFiveHourQuotaRingOffsetY,
            "CodexRadarFiveHourQuotaTextOffsetX=" + this.CodexRadarFiveHourQuotaTextOffsetX,
            "CodexRadarFiveHourQuotaTextOffsetY=" + this.CodexRadarFiveHourQuotaTextOffsetY,
            "CodexRadarWeeklyQuotaRingOffsetX=" + this.CodexRadarWeeklyQuotaRingOffsetX,
            "CodexRadarWeeklyQuotaRingOffsetY=" + this.CodexRadarWeeklyQuotaRingOffsetY,
            "CodexRadarWeeklyQuotaTextOffsetX=" + this.CodexRadarWeeklyQuotaTextOffsetX,
            "CodexRadarWeeklyQuotaTextOffsetY=" + this.CodexRadarWeeklyQuotaTextOffsetY,
            "CodexRadarQuotaRadarLineOffsetX=" + this.CodexRadarQuotaRadarLineOffsetX,
            "CodexRadarQuotaRadarLineOffsetY=" + this.CodexRadarQuotaRadarLineOffsetY,
            "CodexRadarIqRingOffsetX=" + this.CodexRadarIqRingOffsetX,
            "CodexRadarIqRingOffsetY=" + this.CodexRadarIqRingOffsetY,
            "CodexRadarIqTextOffsetX=" + this.CodexRadarIqTextOffsetX,
            "CodexRadarIqTextOffsetY=" + this.CodexRadarIqTextOffsetY,
            "PowerThermalWidth=" + this.PowerThermalWidth,
            "PowerThermalHeight=" + this.PowerThermalHeight,
            "PowerThermalLeftX=" + this.PowerThermalLeftX,
            "PowerThermalBottomY=" + this.PowerThermalBottomY,
            "PowerThermalTransparencyPercent=" + this.PowerThermalTransparencyPercent,
            "PowerThermalAutoSizeEnabled=" + this.PowerThermalAutoSizeEnabled,
            "PowerThermalAutoDirection=" + this.PowerThermalAutoDirection,
            "PowerThermalVisibleAlertCount=" + this.PowerThermalVisibleAlertCount,
            "PowerThermalManualEnergySaverThresholdPercent=" + this.PowerThermalManualEnergySaverThresholdPercent,
            "NetworkMonitorWidth=" + this.NetworkMonitorWidth,
            "NetworkMonitorHeight=" + this.NetworkMonitorHeight,
            "NetworkMonitorLeftX=" + this.NetworkMonitorLeftX,
            "NetworkMonitorBottomY=" + this.NetworkMonitorBottomY,
            "NetworkMonitorTransparencyPercent=" + this.NetworkMonitorTransparencyPercent,
            "NetworkMonitorAdapterId=" + this.NetworkMonitorAdapterId,
            "NetworkStatusTestMode=" + this.NetworkStatusTestMode,
            "GfwProbeEnabled=" + this.GfwProbeEnabled,
            "GfwProbeIntervalMinutes=" + this.GfwProbeIntervalMinutes,
            "CloudEndpointTestSeed=" + this.CloudEndpointTestSeed,
            "CloudStatusRegionMask=" + this.CloudStatusRegionMask,
            "ConnectionCheckWidth=" + this.ConnectionCheckWidth,
            "ConnectionCheckHeight=" + this.ConnectionCheckHeight,
            "ConnectionCheckLeftX=" + this.ConnectionCheckLeftX,
            "ConnectionCheckBottomY=" + this.ConnectionCheckBottomY,
            "ConnectionCheckTransparencyPercent=" + this.ConnectionCheckTransparencyPercent,
            "ConnectionCheckBorderTransparencyPercent=" + this.ConnectionCheckBorderTransparencyPercent,
            "ConnectionCheckIntervalSeconds=" + this.ConnectionCheckIntervalSeconds,
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
            "PowerResumeRestartEnabled=" + this.PowerResumeRestartEnabled,
            "FallbackDisconnectedDisplaysEnabled=" + this.FallbackDisconnectedDisplaysEnabled,
            "MainDisplayDeviceName=" + this.MainDisplayDeviceName,
            "CodexRadarDisplayDeviceName=" + this.CodexRadarDisplayDeviceName,
            "ClaudeRadarDisplayDeviceName=" + this.ClaudeRadarDisplayDeviceName,
            "PowerThermalDisplayDeviceName=" + this.PowerThermalDisplayDeviceName,
            "NetworkMonitorDisplayDeviceName=" + this.NetworkMonitorDisplayDeviceName,
            "ConnectionCheckDisplayDeviceName=" + this.ConnectionCheckDisplayDeviceName,
            "OperationDisplayDeviceName=" + this.OperationDisplayDeviceName,
            "ResolutionCompatibilityModeEnabled=" + this.ResolutionCompatibilityModeEnabled,
            "ResolutionCompatibilityScalePercent=" + this.ResolutionCompatibilityScalePercent,
            "LayoutWorkAreaLeft=" + this.LayoutWorkAreaLeft,
            "LayoutWorkAreaTop=" + this.LayoutWorkAreaTop,
            "LayoutWorkAreaWidth=" + this.LayoutWorkAreaWidth,
            "LayoutWorkAreaHeight=" + this.LayoutWorkAreaHeight,
            "CodexRadarLayoutWorkAreaLeft=" + this.CodexRadarLayoutWorkAreaLeft,
            "CodexRadarLayoutWorkAreaTop=" + this.CodexRadarLayoutWorkAreaTop,
            "CodexRadarLayoutWorkAreaWidth=" + this.CodexRadarLayoutWorkAreaWidth,
            "CodexRadarLayoutWorkAreaHeight=" + this.CodexRadarLayoutWorkAreaHeight,
            "ClaudeRadarLayoutWorkAreaLeft=" + this.ClaudeRadarLayoutWorkAreaLeft,
            "ClaudeRadarLayoutWorkAreaTop=" + this.ClaudeRadarLayoutWorkAreaTop,
            "ClaudeRadarLayoutWorkAreaWidth=" + this.ClaudeRadarLayoutWorkAreaWidth,
            "ClaudeRadarLayoutWorkAreaHeight=" + this.ClaudeRadarLayoutWorkAreaHeight,
            "PowerThermalLayoutWorkAreaLeft=" + this.PowerThermalLayoutWorkAreaLeft,
            "PowerThermalLayoutWorkAreaTop=" + this.PowerThermalLayoutWorkAreaTop,
            "PowerThermalLayoutWorkAreaWidth=" + this.PowerThermalLayoutWorkAreaWidth,
            "PowerThermalLayoutWorkAreaHeight=" + this.PowerThermalLayoutWorkAreaHeight,
            "NetworkMonitorLayoutWorkAreaLeft=" + this.NetworkMonitorLayoutWorkAreaLeft,
            "NetworkMonitorLayoutWorkAreaTop=" + this.NetworkMonitorLayoutWorkAreaTop,
            "NetworkMonitorLayoutWorkAreaWidth=" + this.NetworkMonitorLayoutWorkAreaWidth,
            "NetworkMonitorLayoutWorkAreaHeight=" + this.NetworkMonitorLayoutWorkAreaHeight,
            "ConnectionCheckLayoutWorkAreaLeft=" + this.ConnectionCheckLayoutWorkAreaLeft,
            "ConnectionCheckLayoutWorkAreaTop=" + this.ConnectionCheckLayoutWorkAreaTop,
            "ConnectionCheckLayoutWorkAreaWidth=" + this.ConnectionCheckLayoutWorkAreaWidth,
            "ConnectionCheckLayoutWorkAreaHeight=" + this.ConnectionCheckLayoutWorkAreaHeight,
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
            "ShowCpu=" + this.ShowCpu,
            "ShowMemory=" + this.ShowMemory,
            "ShowDisk=" + this.ShowDisk,
            "ShowNetwork=" + this.ShowNetwork,
            "ShowGpu=" + this.ShowGpu,
            "ShowNpu=" + this.ShowNpu,
            "AlertTestEnabled=" + this.AlertTestEnabled,
            "ThermalTestMode=" + this.ThermalTestMode,
            "CodexRadarTestMode=" + this.CodexRadarTestMode,
            "ServiceHealthTestMode=" + this.ServiceHealthTestMode,
            "CodexRadarRandomTestEnabled=" + this.CodexRadarRandomTestEnabled,
            "CodexRadarRandomTestAutoRefresh=" + this.CodexRadarRandomTestAutoRefresh,
            "CodexRadarRandomTestRefreshToken=" + this.CodexRadarRandomTestRefreshToken,
            "CodexRadarRenderVariant=" + this.CodexRadarRenderVariant,
            "MainWidgetRenderVariant=" + this.MainWidgetRenderVariant,
            "NetworkMonitorRenderVariant=" + this.NetworkMonitorRenderVariant,
            "PowerThermalRenderVariant=" + this.PowerThermalRenderVariant,
            "ConnectionCheckRenderVariant=" + this.ConnectionCheckRenderVariant,
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
            "ClaudeRadarJsonEnabled=" + this.ClaudeRadarJsonEnabled,
            "ClaudeRadarHomepageFallbackEnabled=" + this.ClaudeRadarHomepageFallbackEnabled,
            "ClaudeRadarCommunityRatingsEnabled=" + this.ClaudeRadarCommunityRatingsEnabled,
            "ClaudeRadarLocalQuotaFallbackEnabled=" + this.ClaudeRadarLocalQuotaFallbackEnabled,
            "ClaudeRadarRandomTestEnabled=" + this.ClaudeRadarRandomTestEnabled,
            "ClaudeRadarRandomTestAutoRefresh=" + this.ClaudeRadarRandomTestAutoRefresh,
            "ClaudeRadarRandomTestRefreshToken=" + this.ClaudeRadarRandomTestRefreshToken,
            "ClaudeRadarServiceProbeToken=" + this.ClaudeRadarServiceProbeToken,
            "DeepSeekApiKeyRevision=" + this.DeepSeekApiKeyRevision,
            "AiRequestProtectionAutoEnabled=" + this.AiRequestProtectionAutoEnabled,
            "AiRequestProtectionManualBlockEnabled=" + this.AiRequestProtectionManualBlockEnabled,
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
            "ClaudeRadarModelKey=" + this.ClaudeRadarModelKey,
            "DisplayTimeZoneMode=" + this.DisplayTimeZoneMode,
            "DisplayTimeZoneId=" + this.DisplayTimeZoneId,
            "PowerSavingEnabled=" + this.PowerSavingEnabled,
            "PerformanceMode=" + this.PerformanceMode,
            "HoverOpacityEnabled=" + this.HoverOpacityEnabled,
            "AutoHoverOpacityIdleEnabled=" + this.AutoHoverOpacityIdleEnabled,
            "AutoHoverOpacityIdleSeconds=" + this.AutoHoverOpacityIdleSeconds,
            "AutoHoverOpacityMaximizedEnabled=" + this.AutoHoverOpacityMaximizedEnabled,
            "OperationRadialCoreAutoHideKeepAliveEnabled=" + this.OperationRadialCoreAutoHideKeepAliveEnabled,
            "OperationRadialIdleCollapseSeconds=" + this.OperationRadialIdleCollapseSeconds,
            "OperationRadialIdleResetOnInteractionEnabled=" + this.OperationRadialIdleResetOnInteractionEnabled,
            "OperationRadialKeepOpenAfterLeafClickEnabled=" + this.OperationRadialKeepOpenAfterLeafClickEnabled,
            "OperationSettingsLogicExtensionEnabled=" + this.OperationSettingsLogicExtensionEnabled,
            "BurnInHiddenModeColorProtectionEnabled=" + this.BurnInHiddenModeColorProtectionEnabled,
            "MetricOrder=" + string.Join(",", NormalizeMetricOrder(this.MetricOrder))
        };
        File.WriteAllLines(path, lines);
    }

    private static void ApplyValue(WidgetSettings settings, string key, string value)
    {
        int intValue;
        double doubleValue;
        bool boolValue;

        if (string.Equals(key, "Width", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.Width = intValue;
            return;
        }

        if (string.Equals(key, "Height", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.Height = intValue;
            return;
        }

        if (string.Equals(key, "LeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.LeftX = intValue;
            return;
        }

        if (string.Equals(key, "BottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.BottomY = intValue;
            return;
        }

        if ((string.Equals(key, "BackgroundTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "BackgroundTransparency", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.BackgroundTransparencyPercent = intValue;
            return;
        }

        if ((string.Equals(key, "ApplicationTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ContentTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.ApplicationTransparencyPercent = intValue;
            return;
        }

        if ((string.Equals(key, "CodexRadarWidth", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ClockWidth", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarWidth = intValue;
            return;
        }

        if ((string.Equals(key, "CodexRadarHeight", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ClockHeight", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarHeight = intValue;
            return;
        }

        if ((string.Equals(key, "CodexRadarLeftX", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ClockLeftX", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarLeftX = intValue;
            return;
        }

        if ((string.Equals(key, "CodexRadarBottomY", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ClockBottomY", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarBottomY = intValue;
            return;
        }

        if ((string.Equals(key, "CodexRadarTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ClockTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarWidth", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarWidth = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarHeight", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarHeight = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarLeftX", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarLeftX = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarBottomY", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarBottomY = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarTransparencyPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualLayoutEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.CodexRadarManualLayoutEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualLeftPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualLeftPercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualGapPixels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualGapPixels = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualEfficiencyTextWidthPixels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualEfficiencyTextWidthPixels = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualQuotaRowsWidthPixels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualQuotaRowsWidthPixels = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualIqStatusWidthPixels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualIqStatusWidthPixels = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualTextScalePercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualTextScalePercent = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarManualRingScalePercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexRadarManualRingScalePercent = intValue;
            return;
        }

        if (IsCodexRadarManualElementOffsetKey(key) && int.TryParse(value, out intValue))
        {
            System.Reflection.PropertyInfo property = typeof(WidgetSettings).GetProperty(key);
            if (property != null && property.PropertyType == typeof(int) && property.CanWrite)
            {
                property.SetValue(settings, intValue, null);
                return;
            }
        }

        if (string.Equals(key, "PowerThermalWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalWidth = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalHeight = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalLeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalLeftX = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalBottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalBottomY = intValue;
            return;
        }

        if ((string.Equals(key, "PowerThermalTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "PowerThermalBackgroundTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.PowerThermalTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalAutoSizeEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerThermalAutoSizeEnabled = boolValue;
            return;
        }

        if ((string.Equals(key, "PowerThermalAutoDirection", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "PowerThermalAutoSizeDirection", StringComparison.OrdinalIgnoreCase)) &&
            value.Length > 0)
        {
            try
            {
                settings.PowerThermalAutoDirection = (PowerThermalAutoDirection)Enum.Parse(typeof(PowerThermalAutoDirection), value, true);
            }
            catch
            {
                settings.PowerThermalAutoDirection = PowerThermalAutoDirection.Left;
            }

            return;
        }

        if ((string.Equals(key, "PowerThermalVisibleAlertCount", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "PowerThermalVisibleAlerts", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.PowerThermalVisibleAlertCount = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalManualEnergySaverThresholdPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.PowerThermalManualEnergySaverThresholdPercent = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorWidth = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorHeight = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorLeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorLeftX = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorBottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorBottomY = intValue;
            return;
        }

        if ((string.Equals(key, "NetworkMonitorTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "NetworkMonitorBackgroundTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorTransparencyPercent = intValue;
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

        if (string.Equals(key, "ConnectionCheckWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckWidth = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckHeight = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckLeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckLeftX = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckBottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckBottomY = intValue;
            return;
        }

        if ((string.Equals(key, "ConnectionCheckTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ConnectionCheckBackgroundTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckBorderTransparencyPercent", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckBorderTransparencyPercent = intValue;
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

        if (string.Equals(key, "CodexRadarDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.CodexRadarDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "ClaudeRadarDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.ClaudeRadarDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "PowerThermalDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.PowerThermalDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "NetworkMonitorDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.NetworkMonitorDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "ConnectionCheckDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.ConnectionCheckDisplayDeviceName = NormalizeDisplayDeviceName(value);
            return;
        }

        if (string.Equals(key, "OperationDisplayDeviceName", StringComparison.OrdinalIgnoreCase))
        {
            settings.OperationDisplayDeviceName = NormalizeDisplayDeviceName(value);
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

        if (string.Equals(key, "CodexRadarLayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexRadarLayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarLayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexRadarLayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarLayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexRadarLayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "CodexRadarLayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.CodexRadarLayoutWorkAreaHeight = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarLayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarLayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarLayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarLayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarLayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarLayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarLayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarLayoutWorkAreaHeight = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalLayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalLayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalLayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalLayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalLayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalLayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "PowerThermalLayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.PowerThermalLayoutWorkAreaHeight = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorLayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorLayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorLayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorLayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorLayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorLayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "NetworkMonitorLayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.NetworkMonitorLayoutWorkAreaHeight = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckLayoutWorkAreaLeft", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckLayoutWorkAreaLeft = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckLayoutWorkAreaTop", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckLayoutWorkAreaTop = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckLayoutWorkAreaWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckLayoutWorkAreaWidth = intValue;
            return;
        }

        if (string.Equals(key, "ConnectionCheckLayoutWorkAreaHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ConnectionCheckLayoutWorkAreaHeight = intValue;
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

        if (string.Equals(key, "ShowCpu", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowCpu = boolValue;
            return;
        }

        if (string.Equals(key, "ShowMemory", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowMemory = boolValue;
            return;
        }

        if (string.Equals(key, "ShowDisk", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowDisk = boolValue;
            return;
        }

        if (string.Equals(key, "ShowNetwork", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowNetwork = boolValue;
            return;
        }

        if (string.Equals(key, "ShowGpu", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowGpu = boolValue;
            return;
        }

        if (string.Equals(key, "ShowNpu", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowNpu = boolValue;
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

        if (string.Equals(key, "CodexRadarRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.CodexRadarRenderVariant = (CodexRadarRenderVariant)Enum.Parse(typeof(CodexRadarRenderVariant), value, true);
            }
            catch
            {
                settings.CodexRadarRenderVariant = CodexRadarRenderVariant.EvenRow;
            }

            return;
        }

        if (string.Equals(key, "MainWidgetRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            try { settings.MainWidgetRenderVariant = (MainWidgetRenderVariant)Enum.Parse(typeof(MainWidgetRenderVariant), value, true); }
            catch { settings.MainWidgetRenderVariant = MainWidgetRenderVariant.Classic; }
            return;
        }

        if (string.Equals(key, "NetworkMonitorRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            try { settings.NetworkMonitorRenderVariant = (NetworkMonitorRenderVariant)Enum.Parse(typeof(NetworkMonitorRenderVariant), value, true); }
            catch { settings.NetworkMonitorRenderVariant = NetworkMonitorRenderVariant.Classic; }
            return;
        }

        if (string.Equals(key, "PowerThermalRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            try { settings.PowerThermalRenderVariant = (PowerThermalRenderVariant)Enum.Parse(typeof(PowerThermalRenderVariant), value, true); }
            catch { settings.PowerThermalRenderVariant = PowerThermalRenderVariant.Classic; }
            return;
        }

        if (string.Equals(key, "ConnectionCheckRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            try { settings.ConnectionCheckRenderVariant = (ConnectionCheckRenderVariant)Enum.Parse(typeof(ConnectionCheckRenderVariant), value, true); }
            catch { settings.ConnectionCheckRenderVariant = ConnectionCheckRenderVariant.Classic; }
            return;
        }

        if (string.Equals(key, "OperationRenderVariant", StringComparison.OrdinalIgnoreCase))
        {
            try { settings.OperationRenderVariant = (OperationRenderVariant)Enum.Parse(typeof(OperationRenderVariant), value, true); }
            catch { settings.OperationRenderVariant = OperationRenderVariant.Classic; }
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

        if (string.Equals(key, "ClaudeRadarJsonEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarJsonEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarHomepageFallbackEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarHomepageFallbackEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarCommunityRatingsEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarCommunityRatingsEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarLocalQuotaFallbackEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarLocalQuotaFallbackEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarRandomTestEnabled", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarRandomTestEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarRandomTestAutoRefresh", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(value, out boolValue))
        {
            settings.ClaudeRadarRandomTestAutoRefresh = boolValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarRandomTestRefreshToken", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarRandomTestRefreshToken = intValue;
            return;
        }

        if (string.Equals(key, "ClaudeRadarServiceProbeToken", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.ClaudeRadarServiceProbeToken = intValue;
            return;
        }

        if (string.Equals(key, "DeepSeekApiKeyRevision", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out intValue))
        {
            settings.DeepSeekApiKeyRevision = intValue;
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

        if (string.Equals(key, "ClaudeRadarModelKey", StringComparison.OrdinalIgnoreCase))
        {
            settings.ClaudeRadarModelKey = NormalizeClaudeRadarModelKey(value);
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

        if (string.Equals(key, "OperationSettingsLogicExtensionEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.OperationSettingsLogicExtensionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "BurnInHiddenModeColorProtectionEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.BurnInHiddenModeColorProtectionEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "MetricOrder", StringComparison.OrdinalIgnoreCase))
        {
            settings.MetricOrder = NormalizeMetricOrder(value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
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

        bool changed = false;
        changed |= AdaptMainToWorkArea(GetWorkAreaForModule(ModuleMain));
        changed |= AdaptCodexRadarToWorkArea(GetWorkAreaForModule(ModuleCodexRadar));
        changed |= AdaptClaudeRadarToWorkArea(GetWorkAreaForModule(ModuleClaudeRadar));
        changed |= AdaptPowerThermalToWorkArea(GetWorkAreaForModule(ModulePowerThermal));
        changed |= AdaptNetworkMonitorToWorkArea(GetWorkAreaForModule(ModuleNetworkMonitor));
        changed |= AdaptConnectionCheckToWorkArea(GetWorkAreaForModule(ModuleConnectionCheck));
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

        PanelLayout scaledLayout = ScalePanelLayout(
            this.Width,
            this.Height,
            this.LeftX,
            this.BottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            MinWidth,
            MaxWidth,
            MinHeight,
            MaxHeight);
        this.Width = scaledLayout.Width;
        this.Height = scaledLayout.Height;
        this.LeftX = scaledLayout.LeftX;
        this.BottomY = scaledLayout.BottomY;

        scaledLayout = ScalePanelLayout(
            this.CodexRadarWidth,
            this.CodexRadarHeight,
            this.CodexRadarLeftX,
            this.CodexRadarBottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            MinCodexRadarWidth,
            MaxCodexRadarWidth,
            MinCodexRadarHeight,
            MaxCodexRadarHeight);
        this.CodexRadarWidth = scaledLayout.Width;
        this.CodexRadarHeight = scaledLayout.Height;
        this.CodexRadarLeftX = scaledLayout.LeftX;
        this.CodexRadarBottomY = scaledLayout.BottomY;

        scaledLayout = ScalePanelLayout(
            this.PowerThermalWidth,
            this.PowerThermalHeight,
            this.PowerThermalLeftX,
            this.PowerThermalBottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            MinPowerThermalWidth,
            MaxPowerThermalWidth,
            MinPowerThermalHeight,
            MaxPowerThermalHeight);
        this.PowerThermalWidth = scaledLayout.Width;
        this.PowerThermalHeight = scaledLayout.Height;
        this.PowerThermalLeftX = scaledLayout.LeftX;
        this.PowerThermalBottomY = scaledLayout.BottomY;

        scaledLayout = ScalePanelLayout(
            this.NetworkMonitorWidth,
            this.NetworkMonitorHeight,
            this.NetworkMonitorLeftX,
            this.NetworkMonitorBottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            MinNetworkMonitorWidth,
            MaxNetworkMonitorWidth,
            MinNetworkMonitorHeight,
            MaxNetworkMonitorHeight);
        this.NetworkMonitorWidth = scaledLayout.Width;
        this.NetworkMonitorHeight = scaledLayout.Height;
        this.NetworkMonitorLeftX = scaledLayout.LeftX;
        this.NetworkMonitorBottomY = scaledLayout.BottomY;

        scaledLayout = ScalePanelLayout(
            this.ConnectionCheckWidth,
            this.ConnectionCheckHeight,
            this.ConnectionCheckLeftX,
            this.ConnectionCheckBottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            MinConnectionCheckWidth,
            MaxConnectionCheckWidth,
            MinConnectionCheckHeight,
            MaxConnectionCheckHeight);
        this.ConnectionCheckWidth = scaledLayout.Width;
        this.ConnectionCheckHeight = scaledLayout.Height;
        this.ConnectionCheckLeftX = scaledLayout.LeftX;
        this.ConnectionCheckBottomY = scaledLayout.BottomY;

        this.OperationButtonSize = Clamp(
            RoundScaled(this.OperationButtonSize, uniformScale),
            MinOperationButtonSize,
            MaxOperationButtonSize);
        this.OperationLeftOffset = Clamp(RoundScaled(this.OperationLeftOffset, scaleX), MinOperationOffset, MaxOperationOffset);
        this.OperationBottomOffset = Clamp(RoundScaled(this.OperationBottomOffset, scaleY), MinOperationOffset, MaxOperationOffset);

        CaptureLayoutWorkArea(currentWorkArea);
        CaptureAllModuleLayoutWorkAreas(currentWorkArea);
        ClampLayoutToWorkArea(currentWorkArea, GetPrimaryScale());
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

        return targetWorkArea.Left + RoundScaled(logicalLeftX, GetResolutionCompatibilityScaleFactor());
    }

    public int MapResolutionCompatibilityBottom(string moduleId, Rectangle targetWorkArea, int logicalBottomY)
    {
        if (!this.ResolutionCompatibilityModeEnabled)
        {
            return logicalBottomY;
        }

        return targetWorkArea.Top + RoundScaled(logicalBottomY, GetResolutionCompatibilityScaleFactor());
    }

    private bool AdaptMainToWorkArea(Rectangle currentWorkArea)
    {
        EnsureUsableWorkArea(ref currentWorkArea);
        Rectangle previousWorkArea = GetModuleLayoutWorkArea(ModuleMain);
        if (SameWorkArea(previousWorkArea, currentWorkArea))
        {
            ClampMainLayoutToWorkArea(currentWorkArea);
            return false;
        }

        double scaleX = currentWorkArea.Width / (double)Math.Max(1, previousWorkArea.Width);
        double scaleY = currentWorkArea.Height / (double)Math.Max(1, previousWorkArea.Height);
        PanelLayout scaledLayout = ScalePanelLayout(
            this.Width,
            this.Height,
            this.LeftX,
            this.BottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            MinWidth,
            MaxWidth,
            MinHeight,
            MaxHeight);
        this.Width = scaledLayout.Width;
        this.Height = scaledLayout.Height;
        this.LeftX = scaledLayout.LeftX;
        this.BottomY = scaledLayout.BottomY;
        CaptureModuleLayoutWorkArea(ModuleMain, currentWorkArea);
        ClampMainLayoutToWorkArea(currentWorkArea);
        return true;
    }

    private bool AdaptCodexRadarToWorkArea(Rectangle currentWorkArea)
    {
        PanelLayout scaledLayout;
        if (!AdaptPanelToWorkArea(
            ModuleCodexRadar,
            this.CodexRadarWidth,
            this.CodexRadarHeight,
            this.CodexRadarLeftX,
            this.CodexRadarBottomY,
            currentWorkArea,
            MinCodexRadarWidth,
            MaxCodexRadarWidth,
            MinCodexRadarHeight,
            MaxCodexRadarHeight,
            out scaledLayout))
        {
            ClampCodexRadarLayoutToWorkArea(currentWorkArea);
            return false;
        }

        this.CodexRadarWidth = scaledLayout.Width;
        this.CodexRadarHeight = scaledLayout.Height;
        this.CodexRadarLeftX = scaledLayout.LeftX;
        this.CodexRadarBottomY = scaledLayout.BottomY;
        ClampCodexRadarLayoutToWorkArea(currentWorkArea);
        return true;
    }

    private bool AdaptClaudeRadarToWorkArea(Rectangle currentWorkArea)
    {
        PanelLayout scaledLayout;
        if (!AdaptPanelToWorkArea(
            ModuleClaudeRadar,
            this.ClaudeRadarWidth,
            this.ClaudeRadarHeight,
            this.ClaudeRadarLeftX,
            this.ClaudeRadarBottomY,
            currentWorkArea,
            MinCodexRadarWidth,
            MaxCodexRadarWidth,
            MinCodexRadarHeight,
            MaxCodexRadarHeight,
            out scaledLayout))
        {
            ClampClaudeRadarLayoutToWorkArea(currentWorkArea);
            return false;
        }

        this.ClaudeRadarWidth = scaledLayout.Width;
        this.ClaudeRadarHeight = scaledLayout.Height;
        this.ClaudeRadarLeftX = scaledLayout.LeftX;
        this.ClaudeRadarBottomY = scaledLayout.BottomY;
        ClampClaudeRadarLayoutToWorkArea(currentWorkArea);
        return true;
    }

    private bool AdaptPowerThermalToWorkArea(Rectangle currentWorkArea)
    {
        PanelLayout scaledLayout;
        if (!AdaptPanelToWorkArea(
            ModulePowerThermal,
            this.PowerThermalWidth,
            this.PowerThermalHeight,
            this.PowerThermalLeftX,
            this.PowerThermalBottomY,
            currentWorkArea,
            MinPowerThermalWidth,
            MaxPowerThermalWidth,
            MinPowerThermalHeight,
            MaxPowerThermalHeight,
            out scaledLayout))
        {
            ClampPowerThermalLayoutToWorkArea(currentWorkArea);
            return false;
        }

        this.PowerThermalWidth = scaledLayout.Width;
        this.PowerThermalHeight = scaledLayout.Height;
        this.PowerThermalLeftX = scaledLayout.LeftX;
        this.PowerThermalBottomY = scaledLayout.BottomY;
        ClampPowerThermalLayoutToWorkArea(currentWorkArea);
        return true;
    }

    private bool AdaptNetworkMonitorToWorkArea(Rectangle currentWorkArea)
    {
        PanelLayout scaledLayout;
        if (!AdaptPanelToWorkArea(
            ModuleNetworkMonitor,
            this.NetworkMonitorWidth,
            this.NetworkMonitorHeight,
            this.NetworkMonitorLeftX,
            this.NetworkMonitorBottomY,
            currentWorkArea,
            MinNetworkMonitorWidth,
            MaxNetworkMonitorWidth,
            MinNetworkMonitorHeight,
            MaxNetworkMonitorHeight,
            out scaledLayout))
        {
            ClampNetworkMonitorLayoutToWorkArea(currentWorkArea);
            return false;
        }

        this.NetworkMonitorWidth = scaledLayout.Width;
        this.NetworkMonitorHeight = scaledLayout.Height;
        this.NetworkMonitorLeftX = scaledLayout.LeftX;
        this.NetworkMonitorBottomY = scaledLayout.BottomY;
        ClampNetworkMonitorLayoutToWorkArea(currentWorkArea);
        return true;
    }

    private bool AdaptConnectionCheckToWorkArea(Rectangle currentWorkArea)
    {
        PanelLayout scaledLayout;
        if (!AdaptPanelToWorkArea(
            ModuleConnectionCheck,
            this.ConnectionCheckWidth,
            this.ConnectionCheckHeight,
            this.ConnectionCheckLeftX,
            this.ConnectionCheckBottomY,
            currentWorkArea,
            MinConnectionCheckWidth,
            MaxConnectionCheckWidth,
            MinConnectionCheckHeight,
            MaxConnectionCheckHeight,
            out scaledLayout))
        {
            ClampConnectionCheckLayoutToWorkArea(currentWorkArea);
            return false;
        }

        this.ConnectionCheckWidth = scaledLayout.Width;
        this.ConnectionCheckHeight = scaledLayout.Height;
        this.ConnectionCheckLeftX = scaledLayout.LeftX;
        this.ConnectionCheckBottomY = scaledLayout.BottomY;
        ClampConnectionCheckLayoutToWorkArea(currentWorkArea);
        return true;
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

    private bool AdaptPanelToWorkArea(
        string moduleId,
        int width,
        int height,
        int leftX,
        int bottomY,
        Rectangle currentWorkArea,
        int minWidth,
        int maxWidth,
        int minHeight,
        int maxHeight,
        out PanelLayout scaledLayout)
    {
        EnsureUsableWorkArea(ref currentWorkArea);
        Rectangle previousWorkArea = GetModuleLayoutWorkArea(moduleId);
        if (SameWorkArea(previousWorkArea, currentWorkArea))
        {
            scaledLayout = new PanelLayout();
            return false;
        }

        double scaleX = currentWorkArea.Width / (double)Math.Max(1, previousWorkArea.Width);
        double scaleY = currentWorkArea.Height / (double)Math.Max(1, previousWorkArea.Height);
        scaledLayout = ScalePanelLayout(
            width,
            height,
            leftX,
            bottomY,
            previousWorkArea,
            currentWorkArea,
            scaleX,
            scaleY,
            minWidth,
            maxWidth,
            minHeight,
            maxHeight);
        CaptureModuleLayoutWorkArea(moduleId, currentWorkArea);
        return true;
    }

    private void ClampLayoutToWorkArea(Rectangle workArea, float scale)
    {
        EnsureUsableWorkArea(ref workArea);
        ClampMainLayoutToWorkArea(workArea);
        ClampCodexRadarLayoutToWorkArea(workArea);
        ClampClaudeRadarLayoutToWorkArea(workArea);
        ClampPowerThermalLayoutToWorkArea(workArea);
        ClampNetworkMonitorLayoutToWorkArea(workArea);
        ClampConnectionCheckLayoutToWorkArea(workArea);
        ClampOperationLayoutToWorkArea(workArea, scale);
    }

    private void ClampLayoutToTargetWorkAreas(float scale)
    {
        ClampMainLayoutToWorkArea(GetWorkAreaForModule(ModuleMain));
        ClampCodexRadarLayoutToWorkArea(GetWorkAreaForModule(ModuleCodexRadar));
        ClampClaudeRadarLayoutToWorkArea(GetWorkAreaForModule(ModuleClaudeRadar));
        ClampPowerThermalLayoutToWorkArea(GetWorkAreaForModule(ModulePowerThermal));
        ClampNetworkMonitorLayoutToWorkArea(GetWorkAreaForModule(ModuleNetworkMonitor));
        ClampConnectionCheckLayoutToWorkArea(GetWorkAreaForModule(ModuleConnectionCheck));
        ClampOperationLayoutToWorkArea(GetWorkAreaForModule(ModuleOperation), scale);
    }

    private void ClampMainLayoutToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.LeftX = Clamp(this.LeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.Width));
        this.BottomY = Clamp(this.BottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.Height - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
    }

    private void ClampCodexRadarLayoutToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.CodexRadarLeftX = Clamp(this.CodexRadarLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.CodexRadarWidth));
        this.CodexRadarBottomY = Clamp(this.CodexRadarBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.CodexRadarHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
    }

    private void ClampClaudeRadarLayoutToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.ClaudeRadarLeftX = Clamp(this.ClaudeRadarLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.ClaudeRadarWidth));
        this.ClaudeRadarBottomY = Clamp(this.ClaudeRadarBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.ClaudeRadarHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
    }

    private void ClampPowerThermalLayoutToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.PowerThermalLeftX = Clamp(this.PowerThermalLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.PowerThermalWidth));
        this.PowerThermalBottomY = Clamp(this.PowerThermalBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.PowerThermalHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
    }

    private void ClampNetworkMonitorLayoutToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.NetworkMonitorLeftX = Clamp(this.NetworkMonitorLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.NetworkMonitorWidth));
        this.NetworkMonitorBottomY = Clamp(this.NetworkMonitorBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.NetworkMonitorHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
    }

    private void ClampConnectionCheckLayoutToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.ConnectionCheckLeftX = Clamp(this.ConnectionCheckLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.ConnectionCheckWidth));
        this.ConnectionCheckBottomY = Clamp(this.ConnectionCheckBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.ConnectionCheckHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
    }

    private void ClampOperationLayoutToWorkArea(Rectangle workArea, float scale)
    {
        EnsureUsableWorkArea(ref workArea);
        int operationMaxLeftOffset = Math.Max(0, workArea.Width - GetOperationWindowWidth(this.OperationButtonSize, scale));
        int operationMaxBottomOffset = Math.Max(0, workArea.Height - GetOperationWindowHeight(this.OperationButtonSize, scale));
        this.OperationLeftOffset = Clamp(this.OperationLeftOffset, MinOperationOffset, Math.Min(MaxOperationOffset, operationMaxLeftOffset));
        this.OperationBottomOffset = Clamp(this.OperationBottomOffset, MinOperationOffset, Math.Min(MaxOperationOffset, operationMaxBottomOffset));
    }

    private struct PanelLayout
    {
        public int Width;
        public int Height;
        public int LeftX;
        public int BottomY;
    }

    private static PanelLayout ScalePanelLayout(
        int width,
        int height,
        int leftX,
        int bottomY,
        Rectangle previousWorkArea,
        Rectangle currentWorkArea,
        double scaleX,
        double scaleY,
        int minWidth,
        int maxWidth,
        int minHeight,
        int maxHeight)
    {
        PanelLayout layout = new PanelLayout();
        layout.Width = Clamp(RoundScaled(width, scaleX), minWidth, maxWidth);
        layout.Height = Clamp(RoundScaled(height, scaleY), minHeight, maxHeight);
        layout.LeftX = currentWorkArea.Left + RoundScaled(leftX - previousWorkArea.Left, scaleX);
        layout.BottomY = currentWorkArea.Top + RoundScaled(bottomY - previousWorkArea.Top, scaleY);
        return layout;
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
        EnsureModuleLayoutWorkAreaReference(ModuleCodexRadar, workArea);
        EnsureModuleLayoutWorkAreaReference(ModuleClaudeRadar, workArea);
        EnsureModuleLayoutWorkAreaReference(ModulePowerThermal, workArea);
        EnsureModuleLayoutWorkAreaReference(ModuleNetworkMonitor, workArea);
        EnsureModuleLayoutWorkAreaReference(ModuleConnectionCheck, workArea);
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
        CaptureModuleLayoutWorkArea(ModuleCodexRadar, GetWorkAreaForModule(ModuleCodexRadar));
        CaptureModuleLayoutWorkArea(ModuleClaudeRadar, GetWorkAreaForModule(ModuleClaudeRadar));
        CaptureModuleLayoutWorkArea(ModulePowerThermal, GetWorkAreaForModule(ModulePowerThermal));
        CaptureModuleLayoutWorkArea(ModuleNetworkMonitor, GetWorkAreaForModule(ModuleNetworkMonitor));
        CaptureModuleLayoutWorkArea(ModuleConnectionCheck, GetWorkAreaForModule(ModuleConnectionCheck));
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

    private void CaptureAllModuleLayoutWorkAreas(Rectangle workArea)
    {
        CaptureModuleLayoutWorkArea(ModuleCodexRadar, workArea);
        CaptureModuleLayoutWorkArea(ModuleClaudeRadar, workArea);
        CaptureModuleLayoutWorkArea(ModulePowerThermal, workArea);
        CaptureModuleLayoutWorkArea(ModuleNetworkMonitor, workArea);
        CaptureModuleLayoutWorkArea(ModuleConnectionCheck, workArea);
        CaptureModuleLayoutWorkArea(ModuleOperation, workArea);
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
        if (string.Equals(moduleId, ModuleCodexRadar, StringComparison.Ordinal))
        {
            this.CodexRadarLayoutWorkAreaLeft = left;
            this.CodexRadarLayoutWorkAreaTop = top;
            this.CodexRadarLayoutWorkAreaWidth = width;
            this.CodexRadarLayoutWorkAreaHeight = height;
            return;
        }

        if (string.Equals(moduleId, ModuleClaudeRadar, StringComparison.Ordinal))
        {
            this.ClaudeRadarLayoutWorkAreaLeft = left;
            this.ClaudeRadarLayoutWorkAreaTop = top;
            this.ClaudeRadarLayoutWorkAreaWidth = width;
            this.ClaudeRadarLayoutWorkAreaHeight = height;
            return;
        }

        if (string.Equals(moduleId, ModulePowerThermal, StringComparison.Ordinal))
        {
            this.PowerThermalLayoutWorkAreaLeft = left;
            this.PowerThermalLayoutWorkAreaTop = top;
            this.PowerThermalLayoutWorkAreaWidth = width;
            this.PowerThermalLayoutWorkAreaHeight = height;
            return;
        }

        if (string.Equals(moduleId, ModuleNetworkMonitor, StringComparison.Ordinal))
        {
            this.NetworkMonitorLayoutWorkAreaLeft = left;
            this.NetworkMonitorLayoutWorkAreaTop = top;
            this.NetworkMonitorLayoutWorkAreaWidth = width;
            this.NetworkMonitorLayoutWorkAreaHeight = height;
            return;
        }

        if (string.Equals(moduleId, ModuleConnectionCheck, StringComparison.Ordinal))
        {
            this.ConnectionCheckLayoutWorkAreaLeft = left;
            this.ConnectionCheckLayoutWorkAreaTop = top;
            this.ConnectionCheckLayoutWorkAreaWidth = width;
            this.ConnectionCheckLayoutWorkAreaHeight = height;
            return;
        }

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
        if (string.Equals(moduleId, ModuleCodexRadar, StringComparison.Ordinal))
        {
            workArea = new Rectangle(
                this.CodexRadarLayoutWorkAreaLeft,
                this.CodexRadarLayoutWorkAreaTop,
                this.CodexRadarLayoutWorkAreaWidth,
                this.CodexRadarLayoutWorkAreaHeight);
        }
        else if (string.Equals(moduleId, ModuleClaudeRadar, StringComparison.Ordinal))
        {
            workArea = new Rectangle(
                this.ClaudeRadarLayoutWorkAreaLeft,
                this.ClaudeRadarLayoutWorkAreaTop,
                this.ClaudeRadarLayoutWorkAreaWidth,
                this.ClaudeRadarLayoutWorkAreaHeight);
        }
        else if (string.Equals(moduleId, ModulePowerThermal, StringComparison.Ordinal))
        {
            workArea = new Rectangle(
                this.PowerThermalLayoutWorkAreaLeft,
                this.PowerThermalLayoutWorkAreaTop,
                this.PowerThermalLayoutWorkAreaWidth,
                this.PowerThermalLayoutWorkAreaHeight);
        }
        else if (string.Equals(moduleId, ModuleNetworkMonitor, StringComparison.Ordinal))
        {
            workArea = new Rectangle(
                this.NetworkMonitorLayoutWorkAreaLeft,
                this.NetworkMonitorLayoutWorkAreaTop,
                this.NetworkMonitorLayoutWorkAreaWidth,
                this.NetworkMonitorLayoutWorkAreaHeight);
        }
        else if (string.Equals(moduleId, ModuleConnectionCheck, StringComparison.Ordinal))
        {
            workArea = new Rectangle(
                this.ConnectionCheckLayoutWorkAreaLeft,
                this.ConnectionCheckLayoutWorkAreaTop,
                this.ConnectionCheckLayoutWorkAreaWidth,
                this.ConnectionCheckLayoutWorkAreaHeight);
        }
        else if (string.Equals(moduleId, ModuleOperation, StringComparison.Ordinal))
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
        if (string.Equals(moduleId, ModuleCodexRadar, StringComparison.Ordinal))
        {
            return this.CodexRadarDisplayDeviceName;
        }

        if (string.Equals(moduleId, ModuleClaudeRadar, StringComparison.Ordinal))
        {
            return this.ClaudeRadarDisplayDeviceName;
        }

        if (string.Equals(moduleId, ModulePowerThermal, StringComparison.Ordinal))
        {
            return this.PowerThermalDisplayDeviceName;
        }

        if (string.Equals(moduleId, ModuleNetworkMonitor, StringComparison.Ordinal))
        {
            return this.NetworkMonitorDisplayDeviceName;
        }

        if (string.Equals(moduleId, ModuleConnectionCheck, StringComparison.Ordinal))
        {
            return this.ConnectionCheckDisplayDeviceName;
        }

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

    public static string NormalizeClaudeRadarModelKey(string modelKey)
    {
        string key = (modelKey ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0)
        {
            return string.Empty;
        }

        if (key[0] != 'm')
        {
            return string.Empty;
        }

        for (int i = 1; i < key.Length; i++)
        {
            if (key[i] < '0' || key[i] > '9')
            {
                return string.Empty;
            }
        }

        return key;
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
        WidgetSettings settings = new WidgetSettings(true);
        settings.Width = 432;
        settings.Height = 120;
        settings.LeftX = 2400;
        settings.BottomY = 1680;
        settings.CodexRadarWidth = 210;
        settings.CodexRadarHeight = 64;
        settings.CodexRadarLeftX = 2550;
        settings.CodexRadarBottomY = 1520;
        settings.PowerThermalWidth = 100;
        settings.PowerThermalHeight = 64;
        settings.PowerThermalLeftX = 2438;
        settings.PowerThermalBottomY = 1520;
        settings.NetworkMonitorWidth = 480;
        settings.NetworkMonitorHeight = 170;
        settings.NetworkMonitorLeftX = 2384;
        settings.NetworkMonitorBottomY = 1438;
        settings.ConnectionCheckWidth = 240;
        settings.ConnectionCheckHeight = 85;
        settings.ConnectionCheckLeftX = 2624;
        settings.ConnectionCheckBottomY = 1240;
        settings.OperationButtonSize = 64;
        settings.OperationLeftOffset = 12;
        settings.OperationBottomOffset = 14;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 0;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1700;

        Rectangle current = new Rectangle(0, 0, 1920, 1080);
        AssertLayout(settings.AdaptToWorkArea(current), "first adaptation should change layout");
        AssertLayout(settings.LayoutWorkAreaWidth == 1920 && settings.LayoutWorkAreaHeight == 1080, "layout reference should update");
        AssertLayout(settings.CodexRadarLayoutWorkAreaWidth == 1920 && settings.CodexRadarLayoutWorkAreaHeight == 1080, "codex radar layout reference should update");
        AssertLayout(settings.OperationLayoutWorkAreaWidth == 1920 && settings.OperationLayoutWorkAreaHeight == 1080, "operation layout reference should update");
        AssertLayout(settings.Width == 288, "widget width ratio");
        AssertLayout(settings.Height == 86, "widget height minimum clamp");
        AssertLayout(settings.LeftX == 1600, "widget left ratio");
        AssertLayout(settings.BottomY == 1067, "widget bottom ratio");
        AssertLayout(settings.NetworkMonitorWidth == 320, "network width ratio");
        AssertLayout(settings.NetworkMonitorHeight == 112, "network height minimum clamp");
        AssertLayout(settings.ConnectionCheckWidth == 160, "connection width ratio");
        AssertLayout(settings.ConnectionCheckHeight == 56, "connection height minimum clamp");
        AssertLayout(settings.OperationButtonSize == 41, "operation button uniform scale");
        AssertLayout(settings.OperationLeftOffset == 8, "operation left offset ratio");
        AssertLayout(settings.OperationBottomOffset == 9, "operation bottom offset ratio");
        AssertLayout(!settings.AdaptToWorkArea(current), "same work area should not change layout");

        Rectangle shifted = new Rectangle(100, 40, 1600, 900);
        AssertLayout(settings.AdaptToWorkArea(shifted), "shifted adaptation should change layout");
        AssertLayout(settings.LayoutWorkAreaLeft == 100 && settings.LayoutWorkAreaTop == 40, "shifted layout reference should update");
        AssertLayout(settings.LeftX >= shifted.Left && settings.LeftX <= shifted.Right - settings.Width, "shifted left clamp");
        AssertLayout(settings.BottomY >= shifted.Top + settings.Height - 1 && settings.BottomY <= shifted.Bottom - 1, "shifted bottom clamp");
    }

    internal static void RunCompatibilitySelfTest()
    {
        WidgetSettings legacy = CreateDefaults();
        ApplyValue(legacy, "CtrlDRecoveryPulseEnabled", "False");
        AssertLayout(!legacy.WinDRecoveryPulseEnabled, "legacy Ctrl+D setting should migrate to Win+D");

        ApplyValue(legacy, "WinDRecoveryPulseEnabled", "True");
        AssertLayout(legacy.WinDRecoveryPulseEnabled, "Win+D setting should override the migrated value");

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
        ApplyValue(legacy, "CodexRadarEnabled", "False");
        AssertLayout(!legacy.CodexRadarEnabled, "codex radar enabled setting should parse false");
        ApplyValue(legacy, "CodexRadarEnabled", "True");
        AssertLayout(legacy.CodexRadarEnabled, "codex radar enabled setting should parse true");
        ApplyValue(legacy, "AiRequestProtectionAutoEnabled", "False");
        ApplyValue(legacy, "AiRequestProtectionManualBlockEnabled", "True");
        AssertLayout(
            !legacy.AiRequestProtectionAutoEnabled && legacy.AiRequestProtectionManualBlockEnabled,
            "AI request protection switches should load");
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
        ApplyValue(legacy, "CodexRadarManualLayoutEnabled", "True");
        ApplyValue(legacy, "CodexRadarManualLeftPercent", "500");
        ApplyValue(legacy, "CodexRadarManualTextScalePercent", "10");
        ApplyValue(legacy, "CodexRadarConnectionLineOffsetX", "-999");
        ApplyValue(legacy, "CodexRadarIqTextOffsetY", "999");
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
            legacy.CodexRadarManualLayoutEnabled &&
            legacy.CodexRadarManualLeftPercent == MaxCodexRadarManualLeftPercent &&
            legacy.CodexRadarManualTextScalePercent == MinCodexRadarManualTextScalePercent,
            "codex radar manual layout settings should load and clamp");
        AssertLayout(
            legacy.CodexRadarConnectionLineOffsetX == MinCodexRadarManualElementOffsetPixels &&
            legacy.CodexRadarIqTextOffsetY == MaxCodexRadarManualElementOffsetPixels,
            "codex radar manual element offsets should load and clamp");
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

        WidgetSettings compactRadar = CreateDefaults();
        compactRadar.CodexRadarWidth = 628;
        ApplyCodexRadarServiceHealthPanelMigration(compactRadar);
        AssertLayout(
            compactRadar.CodexRadarWidth == 552,
            "codex radar hidden service panel migration should reclaim width once");
        ApplyCodexRadarCompactQuotaWidthMigration(compactRadar);
        AssertLayout(
            compactRadar.CodexRadarWidth == 430,
            "codex radar compact quota migration should reclaim right-side blank once");
        ApplyCodexRadarBalancedWidthMigration(compactRadar);
        AssertLayout(
            compactRadar.CodexRadarWidth == DefaultCodexRadarWidth,
            "codex radar balanced width migration should restore quota visibility");

        WidgetSettings claudeWidth = CreateDefaults();
        claudeWidth.CodexRadarWidth = 522;
        claudeWidth.ClaudeRadarWidth = DefaultCodexRadarWidth;
        ApplyClaudeRadarWidthParityMigration(claudeWidth);
        AssertLayout(
            claudeWidth.ClaudeRadarWidth == claudeWidth.CodexRadarWidth,
            "claude radar width migration should align the old untouched 580px snapshot to the shared radar width");

        WidgetSettings manualClaudeWidth = CreateDefaults();
        manualClaudeWidth.CodexRadarWidth = 522;
        manualClaudeWidth.ClaudeRadarWidth = 640;
        ApplyClaudeRadarWidthParityMigration(manualClaudeWidth);
        AssertLayout(
            manualClaudeWidth.ClaudeRadarWidth == 640,
            "claude radar width migration should not override a non-default manual width");

        WidgetSettings display = CreateDefaults();
        ApplyValue(display, "FallbackDisconnectedDisplaysEnabled", "False");
        ApplyValue(display, "CodexRadarDisplayDeviceName", "  \\\\.\\DISPLAY-DOES-NOT-EXIST  ");
        display.CodexRadarLayoutWorkAreaLeft = 40;
        display.CodexRadarLayoutWorkAreaTop = 80;
        display.CodexRadarLayoutWorkAreaWidth = 900;
        display.CodexRadarLayoutWorkAreaHeight = 600;
        Rectangle missingDisplayWorkArea = display.GetWorkAreaForModule(ModuleCodexRadar);
        AssertLayout(
            string.Equals(display.CodexRadarDisplayDeviceName, "\\\\.\\DISPLAY-DOES-NOT-EXIST", StringComparison.Ordinal),
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
            WidgetSettings loaded = LoadFromPath(savedPath, false);
            AssertFullRoundTripEqual(settings, loaded, properties, saveLoadExemptions, "Save/Load");
            AssertFullRoundTripSaveCoverage(savedPath, properties, saveLoadExemptions);

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
            property.SetValue(settings, new string[] { MetricNpu, MetricGpu, MetricNetwork, MetricDisk, MetricMemory, MetricCpu }, null);
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
        settings.ClaudeRadarModelKey = "m999";
        settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Manual;
        settings.DisplayTimeZoneId = "UTC";
        settings.OperationPrimaryPanelMode = OperationPrimaryPanelMode.Hidden;
        settings.ResolutionCompatibilityScalePercent = 125;
        settings.MetricOrder = new string[] { MetricNpu, MetricGpu, MetricNetwork, MetricDisk, MetricMemory, MetricCpu };
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

    private static bool IsCodexRadarManualElementOffsetKey(string key)
    {
        return key != null &&
            key.StartsWith("CodexRadar", StringComparison.OrdinalIgnoreCase) &&
            (key.EndsWith("OffsetX", StringComparison.OrdinalIgnoreCase) ||
             key.EndsWith("OffsetY", StringComparison.OrdinalIgnoreCase));
    }

    private static void ClampCodexRadarManualElementOffsets(WidgetSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        System.Reflection.PropertyInfo[] properties = typeof(WidgetSettings).GetProperties();
        for (int i = 0; i < properties.Length; i++)
        {
            System.Reflection.PropertyInfo property = properties[i];
            if (property.PropertyType != typeof(int) ||
                !property.Name.StartsWith("CodexRadar", StringComparison.Ordinal) ||
                !(property.Name.EndsWith("OffsetX", StringComparison.Ordinal) ||
                  property.Name.EndsWith("OffsetY", StringComparison.Ordinal)))
            {
                continue;
            }

            int value = (int)property.GetValue(settings, null);
            property.SetValue(
                settings,
                Clamp(value, MinCodexRadarManualElementOffsetPixels, MaxCodexRadarManualElementOffsetPixels),
                null);
        }
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

    public static string MetricDisplayName(string metricId)
    {
        if (string.Equals(metricId, MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        if (string.Equals(metricId, MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return "Memory";
        }

        if (string.Equals(metricId, MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return "Disk";
        }

        if (string.Equals(metricId, MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return "Network";
        }

        if (string.Equals(metricId, MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        if (string.Equals(metricId, MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return "NPU";
        }

        return metricId;
    }

    private static string[] CloneMetricOrder(string[] order)
    {
        string[] normalized = NormalizeMetricOrder(order);
        string[] clone = new string[normalized.Length];
        Array.Copy(normalized, clone, normalized.Length);
        return clone;
    }

    private static string[] NormalizeMetricOrder(string[] order)
    {
        List<string> normalized = new List<string>();
        if (order != null)
        {
            for (int i = 0; i < order.Length; i++)
            {
                string canonical = CanonicalMetricId(order[i]);
                if (canonical.Length == 0 || ContainsMetric(normalized, canonical))
                {
                    continue;
                }

                normalized.Add(canonical);
            }
        }

        for (int i = 0; i < DefaultMetricOrder.Length; i++)
        {
            if (!ContainsMetric(normalized, DefaultMetricOrder[i]))
            {
                normalized.Add(DefaultMetricOrder[i]);
            }
        }

        return normalized.ToArray();
    }

    private static bool ContainsMetric(List<string> metrics, string metricId)
    {
        for (int i = 0; i < metrics.Count; i++)
        {
            if (string.Equals(metrics[i], metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CanonicalMetricId(string metricId)
    {
        if (string.IsNullOrEmpty(metricId))
        {
            return string.Empty;
        }

        string value = metricId.Trim();
        if (string.Equals(value, MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return MetricCpu;
        }

        if (string.Equals(value, MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return MetricMemory;
        }

        if (string.Equals(value, MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return MetricDisk;
        }

        if (string.Equals(value, MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return MetricNetwork;
        }

        if (string.Equals(value, MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return MetricGpu;
        }

        if (string.Equals(value, MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return MetricNpu;
        }

        return string.Empty;
    }
}
