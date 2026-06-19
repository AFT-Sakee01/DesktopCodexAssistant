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
    DesktopOnly,
    AlwaysVisible,
    HideWhenFullscreen
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

internal enum DisplayTimeZoneMode
{
    Automatic,
    Manual
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
    private const int CurrentSettingsVersion = 21;
    private const int EffectivePerformanceModeCacheMs = 2000;
    private static readonly object EffectivePerformanceModeSync = new object();
    private static DateTime effectivePerformanceModeCacheUtc = DateTime.MinValue;
    private static WidgetPerformanceMode effectivePerformanceModeCache = WidgetPerformanceMode.Balanced;
    public const int MinCodexModelIqPassed = 0;
    public const int MaxCodexModelIqPassed = 12;
    public const int DefaultCodexModelIqBaselinePassed = 8;
    public const int MinCodexModelEfficiencyPercent = 0;
    public const int MaxCodexModelEfficiencyPercent = 200;
    public const int DefaultCodexModelEfficiencyPercent = 100;
    public const int MinCodexModelEfficiencyBaselineValue = 0;
    public const int MaxCodexModelEfficiencyBaselineValue = 100000000;
    public const int DefaultCodexModelEfficiencyBaselineValue = 0;
    public const int MinCodexModelEfficiencyLowThresholdPercent = 0;
    public const int MaxCodexModelEfficiencyLowThresholdPercent = 200;
    public const int DefaultCodexModelEfficiencyLowThresholdPercent = 80;
    public static readonly string[] DefaultMetricOrder = new string[]
    {
        MetricCpu,
        MetricMemory,
        MetricDisk,
        MetricNetwork,
        MetricGpu,
        MetricNpu
    };

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
    public int PowerThermalWidth { get; set; }
    public int PowerThermalHeight { get; set; }
    public int PowerThermalLeftX { get; set; }
    public int PowerThermalBottomY { get; set; }
    public int PowerThermalTransparencyPercent { get; set; }
    public bool PowerThermalAutoSizeEnabled { get; set; }
    public PowerThermalAutoDirection PowerThermalAutoDirection { get; set; }
    public int PowerThermalVisibleAlertCount { get; set; }
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
    public bool ForceShowForegroundFpsEnabled { get; set; }
    public bool SeelenDockForegroundPulseEnabled { get; set; }
    public bool CtrlDRecoveryPulseEnabled { get; set; }
    public bool PowerResumeRestartEnabled { get; set; }
    public int LayoutWorkAreaLeft { get; set; }
    public int LayoutWorkAreaTop { get; set; }
    public int LayoutWorkAreaWidth { get; set; }
    public int LayoutWorkAreaHeight { get; set; }
    public WidgetVisibilityMode VisibilityMode { get; set; }
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
    public CleanIpBadgeTestMode CleanIpBadgeTestMode { get; set; }
    public bool CodexModelIqTestEnabled { get; set; }
    public int CodexModelIqTestPassed { get; set; }
    public int CodexModelIqBaselinePassed { get; set; }
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
    public bool AutoHoverOpacityIdleEnabled { get; set; }
    public int AutoHoverOpacityIdleSeconds { get; set; }
    public bool AutoHoverOpacityMaximizedEnabled { get; set; }
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
        this.PowerThermalWidth = defaults.PowerThermalWidth;
        this.PowerThermalHeight = defaults.PowerThermalHeight;
        this.PowerThermalLeftX = defaults.PowerThermalLeftX;
        this.PowerThermalBottomY = defaults.PowerThermalBottomY;
        this.PowerThermalTransparencyPercent = defaults.PowerThermalTransparencyPercent;
        this.PowerThermalAutoSizeEnabled = defaults.PowerThermalAutoSizeEnabled;
        this.PowerThermalAutoDirection = defaults.PowerThermalAutoDirection;
        this.PowerThermalVisibleAlertCount = defaults.PowerThermalVisibleAlertCount;
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
        this.ForceShowForegroundFpsEnabled = defaults.ForceShowForegroundFpsEnabled;
        this.SeelenDockForegroundPulseEnabled = defaults.SeelenDockForegroundPulseEnabled;
        this.CtrlDRecoveryPulseEnabled = defaults.CtrlDRecoveryPulseEnabled;
        this.PowerResumeRestartEnabled = defaults.PowerResumeRestartEnabled;
        this.LayoutWorkAreaLeft = defaults.LayoutWorkAreaLeft;
        this.LayoutWorkAreaTop = defaults.LayoutWorkAreaTop;
        this.LayoutWorkAreaWidth = defaults.LayoutWorkAreaWidth;
        this.LayoutWorkAreaHeight = defaults.LayoutWorkAreaHeight;
        this.VisibilityMode = defaults.VisibilityMode;
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
        this.CleanIpBadgeTestMode = defaults.CleanIpBadgeTestMode;
        this.CodexModelIqTestEnabled = defaults.CodexModelIqTestEnabled;
        this.CodexModelIqTestPassed = defaults.CodexModelIqTestPassed;
        this.CodexModelIqBaselinePassed = defaults.CodexModelIqBaselinePassed;
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
        this.DisplayTimeZoneMode = defaults.DisplayTimeZoneMode;
        this.DisplayTimeZoneId = defaults.DisplayTimeZoneId;
        this.PerformanceMode = defaults.PerformanceMode;
        this.HoverOpacityEnabled = defaults.HoverOpacityEnabled;
        this.AutoHoverOpacityIdleEnabled = defaults.AutoHoverOpacityIdleEnabled;
        this.AutoHoverOpacityIdleSeconds = defaults.AutoHoverOpacityIdleSeconds;
        this.AutoHoverOpacityMaximizedEnabled = defaults.AutoHoverOpacityMaximizedEnabled;
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
        settings.CodexRadarWidth = 628;
        settings.CodexRadarHeight = 116;
        settings.CodexRadarLeftX = 2252;
        settings.CodexRadarBottomY = 470;
        settings.CodexRadarTransparencyPercent = 30;
        settings.PowerThermalWidth = 120;
        settings.PowerThermalHeight = 110;
        settings.PowerThermalLeftX = 2760;
        settings.PowerThermalBottomY = 582;
        settings.PowerThermalTransparencyPercent = 30;
        settings.PowerThermalAutoSizeEnabled = true;
        settings.PowerThermalAutoDirection = PowerThermalAutoDirection.Down;
        settings.PowerThermalVisibleAlertCount = 8;
        settings.NetworkMonitorWidth = 583;
        settings.NetworkMonitorHeight = 239;
        settings.NetworkMonitorLeftX = 2297;
        settings.NetworkMonitorBottomY = 1799;
        settings.NetworkMonitorTransparencyPercent = 20;
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
        settings.ForceShowForegroundFpsEnabled = false;
        settings.SeelenDockForegroundPulseEnabled = true;
        settings.CtrlDRecoveryPulseEnabled = true;
        settings.PowerResumeRestartEnabled = true;
        settings.LayoutWorkAreaLeft = 0;
        settings.LayoutWorkAreaTop = 60;
        settings.LayoutWorkAreaWidth = 2880;
        settings.LayoutWorkAreaHeight = 1740;
        settings.VisibilityMode = WidgetVisibilityMode.AlwaysVisible;
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
        settings.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        settings.CodexModelIqTestEnabled = false;
        settings.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        settings.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
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
        settings.DisplayTimeZoneMode = DisplayTimeZoneMode.Automatic;
        settings.DisplayTimeZoneId = TimeZoneInfo.Local.Id;
        settings.PerformanceMode = WidgetPerformanceMode.BatterySaver;
        settings.HoverOpacityEnabled = true;
        settings.AutoHoverOpacityIdleEnabled = false;
        settings.AutoHoverOpacityIdleSeconds = DefaultAutoHoverOpacityIdleSeconds;
        settings.AutoHoverOpacityMaximizedEnabled = false;
        settings.BurnInHiddenModeColorProtectionEnabled = false;
        settings.MetricOrder = CloneMetricOrder(DefaultMetricOrder);
        settings.Normalize();
        return settings;
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
            PowerThermalWidth = this.PowerThermalWidth,
            PowerThermalHeight = this.PowerThermalHeight,
            PowerThermalLeftX = this.PowerThermalLeftX,
            PowerThermalBottomY = this.PowerThermalBottomY,
            PowerThermalTransparencyPercent = this.PowerThermalTransparencyPercent,
            PowerThermalAutoSizeEnabled = this.PowerThermalAutoSizeEnabled,
            PowerThermalAutoDirection = this.PowerThermalAutoDirection,
            PowerThermalVisibleAlertCount = this.PowerThermalVisibleAlertCount,
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
            ForceShowForegroundFpsEnabled = this.ForceShowForegroundFpsEnabled,
            SeelenDockForegroundPulseEnabled = this.SeelenDockForegroundPulseEnabled,
            CtrlDRecoveryPulseEnabled = this.CtrlDRecoveryPulseEnabled,
            PowerResumeRestartEnabled = this.PowerResumeRestartEnabled,
            LayoutWorkAreaLeft = this.LayoutWorkAreaLeft,
            LayoutWorkAreaTop = this.LayoutWorkAreaTop,
            LayoutWorkAreaWidth = this.LayoutWorkAreaWidth,
            LayoutWorkAreaHeight = this.LayoutWorkAreaHeight,
            VisibilityMode = this.VisibilityMode,
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
            CleanIpBadgeTestMode = this.CleanIpBadgeTestMode,
            CodexModelIqTestEnabled = this.CodexModelIqTestEnabled,
            CodexModelIqTestPassed = this.CodexModelIqTestPassed,
            CodexModelIqBaselinePassed = this.CodexModelIqBaselinePassed,
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
            DisplayTimeZoneMode = this.DisplayTimeZoneMode,
            DisplayTimeZoneId = this.DisplayTimeZoneId,
            PerformanceMode = this.PerformanceMode,
            HoverOpacityEnabled = this.HoverOpacityEnabled,
            ForceHoverOpacityActive = this.ForceHoverOpacityActive,
            AutoHoverOpacityIdleEnabled = this.AutoHoverOpacityIdleEnabled,
            AutoHoverOpacityIdleSeconds = this.AutoHoverOpacityIdleSeconds,
            AutoHoverOpacityMaximizedEnabled = this.AutoHoverOpacityMaximizedEnabled,
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
        this.PowerThermalWidth = Clamp(this.PowerThermalWidth, MinPowerThermalWidth, MaxPowerThermalWidth);
        this.PowerThermalHeight = Clamp(this.PowerThermalHeight, MinPowerThermalHeight, MaxPowerThermalHeight);
        this.PowerThermalTransparencyPercent = Clamp(this.PowerThermalTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.PowerThermalVisibleAlertCount = Clamp(this.PowerThermalVisibleAlertCount, MinPowerThermalVisibleAlerts, MaxPowerThermalVisibleAlerts);
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
        this.AutoHoverOpacityIdleSeconds = Clamp(
            this.AutoHoverOpacityIdleSeconds,
            MinAutoHoverOpacityIdleSeconds,
            MaxAutoHoverOpacityIdleSeconds);
        this.CodexModelIqTestPassed = Clamp(this.CodexModelIqTestPassed, MinCodexModelIqPassed, MaxCodexModelIqPassed);
        this.CodexModelIqBaselinePassed = Clamp(this.CodexModelIqBaselinePassed, MinCodexModelIqPassed, MaxCodexModelIqPassed);
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

        // The legacy per-module website tests no longer have UI controls.
        this.CodexRadarTestMode = CodexRadarTestMode.Off;
        this.ServiceHealthTestMode = ServiceHealthTestMode.Off;

        if (!Enum.IsDefined(typeof(CleanIpBadgeTestMode), this.CleanIpBadgeTestMode))
        {
            this.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        }

        if (!Enum.IsDefined(typeof(NetworkStatusTestMode), this.NetworkStatusTestMode))
        {
            this.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        }

        this.MetricOrder = NormalizeMetricOrder(this.MetricOrder);
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        EnsureUsableWorkArea(ref workArea);
        EnsureLayoutWorkAreaReference(workArea);
        ClampLayoutToWorkArea(workArea, GetPrimaryScale());
    }

    public static WidgetSettings Load()
    {
        WidgetSettings settings = new WidgetSettings();
        bool hasPixelPosition = false;
        int settingsVersion = 0;

        try
        {
            if (File.Exists(SettingsPath))
            {
                string[] lines = File.ReadAllLines(SettingsPath);
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
        }

        settings.AdaptToCurrentWorkArea();
        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.Normalize();
        return settings;
    }

    private static void ApplyCleanIpBadgeSizeMigration(WidgetSettings settings)
    {
        int oldRight = settings.ConnectionCheckLeftX + settings.ConnectionCheckWidth;
        settings.ConnectionCheckWidth = Clamp((int)Math.Round(settings.NetworkMonitorWidth * 0.5f), MinConnectionCheckWidth, MaxConnectionCheckWidth);
        settings.ConnectionCheckHeight = Clamp((int)Math.Round(settings.NetworkMonitorHeight * 0.5f), MinConnectionCheckHeight, MaxConnectionCheckHeight);
        settings.ConnectionCheckLeftX = oldRight - settings.ConnectionCheckWidth;
    }

    public void Save()
    {
        this.Normalize();
        this.CaptureCurrentWorkArea();
        Directory.CreateDirectory(Logger.DirectoryPath);
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
            "PowerThermalWidth=" + this.PowerThermalWidth,
            "PowerThermalHeight=" + this.PowerThermalHeight,
            "PowerThermalLeftX=" + this.PowerThermalLeftX,
            "PowerThermalBottomY=" + this.PowerThermalBottomY,
            "PowerThermalTransparencyPercent=" + this.PowerThermalTransparencyPercent,
            "PowerThermalAutoSizeEnabled=" + this.PowerThermalAutoSizeEnabled,
            "PowerThermalAutoDirection=" + this.PowerThermalAutoDirection,
            "PowerThermalVisibleAlertCount=" + this.PowerThermalVisibleAlertCount,
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
            "ForceShowForegroundFpsEnabled=" + this.ForceShowForegroundFpsEnabled,
            "SeelenDockForegroundPulseEnabled=" + this.SeelenDockForegroundPulseEnabled,
            "CtrlDRecoveryPulseEnabled=" + this.CtrlDRecoveryPulseEnabled,
            "PowerResumeRestartEnabled=" + this.PowerResumeRestartEnabled,
            "LayoutWorkAreaLeft=" + this.LayoutWorkAreaLeft,
            "LayoutWorkAreaTop=" + this.LayoutWorkAreaTop,
            "LayoutWorkAreaWidth=" + this.LayoutWorkAreaWidth,
            "LayoutWorkAreaHeight=" + this.LayoutWorkAreaHeight,
            "VisibilityMode=" + this.VisibilityMode,
            "ClickThroughMode=" + this.ClickThroughMode,
            "StartupEnabled=" + this.StartupEnabled,
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
            "CleanIpBadgeTestMode=" + this.CleanIpBadgeTestMode,
            "CodexModelIqTestEnabled=" + this.CodexModelIqTestEnabled,
            "CodexModelIqTestPassed=" + this.CodexModelIqTestPassed,
            "CodexModelIqBaselinePassed=" + this.CodexModelIqBaselinePassed,
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
            "DisplayTimeZoneMode=" + this.DisplayTimeZoneMode,
            "DisplayTimeZoneId=" + this.DisplayTimeZoneId,
            "PowerSavingEnabled=" + this.PowerSavingEnabled,
            "PerformanceMode=" + this.PerformanceMode,
            "HoverOpacityEnabled=" + this.HoverOpacityEnabled,
            "AutoHoverOpacityIdleEnabled=" + this.AutoHoverOpacityIdleEnabled,
            "AutoHoverOpacityIdleSeconds=" + this.AutoHoverOpacityIdleSeconds,
            "AutoHoverOpacityMaximizedEnabled=" + this.AutoHoverOpacityMaximizedEnabled,
            "BurnInHiddenModeColorProtectionEnabled=" + this.BurnInHiddenModeColorProtectionEnabled,
            "MetricOrder=" + string.Join(",", NormalizeMetricOrder(this.MetricOrder))
        };
        File.WriteAllLines(SettingsPath, lines);
    }

    private static void ApplyValue(WidgetSettings settings, string key, string value)
    {
        int intValue;
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

        if (string.Equals(key, "CtrlDRecoveryPulseEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.CtrlDRecoveryPulseEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "PowerResumeRestartEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerResumeRestartEnabled = boolValue;
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

        if (string.Equals(key, "StartupEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.StartupEnabled = boolValue;
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

        if ((string.Equals(key, "CodexModelIqBaselinePassed", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "CodexModelIqBaseline", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.CodexModelIqBaselinePassed = intValue;
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
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        EnsureUsableWorkArea(ref workArea);
        return AdaptToWorkArea(workArea);
    }

    internal bool AdaptToWorkArea(Rectangle currentWorkArea)
    {
        EnsureUsableWorkArea(ref currentWorkArea);
        if (!HasLayoutWorkAreaReference())
        {
            CaptureLayoutWorkArea(currentWorkArea);
            return false;
        }

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
        ClampLayoutToWorkArea(currentWorkArea, GetPrimaryScale());
        return true;
    }

    private void ClampLayoutToWorkArea(Rectangle workArea, float scale)
    {
        EnsureUsableWorkArea(ref workArea);
        this.LeftX = Clamp(this.LeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.Width));
        this.BottomY = Clamp(this.BottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.Height - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
        this.CodexRadarLeftX = Clamp(this.CodexRadarLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.CodexRadarWidth));
        this.CodexRadarBottomY = Clamp(this.CodexRadarBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.CodexRadarHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
        this.PowerThermalLeftX = Clamp(this.PowerThermalLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.PowerThermalWidth));
        this.PowerThermalBottomY = Clamp(this.PowerThermalBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.PowerThermalHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
        this.NetworkMonitorLeftX = Clamp(this.NetworkMonitorLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.NetworkMonitorWidth));
        this.NetworkMonitorBottomY = Clamp(this.NetworkMonitorBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.NetworkMonitorHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));
        this.ConnectionCheckLeftX = Clamp(this.ConnectionCheckLeftX, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.ConnectionCheckWidth));
        this.ConnectionCheckBottomY = Clamp(this.ConnectionCheckBottomY, Math.Min(workArea.Bottom - 1, workArea.Top + this.ConnectionCheckHeight - 1), Math.Max(workArea.Top, workArea.Bottom - 1));

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

    private void EnsureLayoutWorkAreaReference(Rectangle workArea)
    {
        if (!HasLayoutWorkAreaReference())
        {
            CaptureLayoutWorkArea(workArea);
        }
    }

    private void CaptureCurrentWorkArea()
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        EnsureUsableWorkArea(ref workArea);
        CaptureLayoutWorkArea(workArea);
    }

    private void CaptureLayoutWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        this.LayoutWorkAreaLeft = workArea.Left;
        this.LayoutWorkAreaTop = workArea.Top;
        this.LayoutWorkAreaWidth = Math.Max(1, workArea.Width);
        this.LayoutWorkAreaHeight = Math.Max(1, workArea.Height);
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
