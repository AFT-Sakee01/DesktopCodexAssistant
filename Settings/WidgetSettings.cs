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
    Smooth,
    Balanced,
    BatterySaver
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
    public NetworkStatusTestMode NetworkStatusTestMode { get; set; }
    public bool GfwProbeEnabled { get; set; }
    public int GfwProbeIntervalMinutes { get; set; }
    public int GfwProbeManualRefreshToken { get; set; }
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
    public CleanIpBadgeTestMode CleanIpBadgeTestMode { get; set; }
    public bool CodexModelIqTestEnabled { get; set; }
    public int CodexModelIqTestPassed { get; set; }
    public int CodexModelIqBaselinePassed { get; set; }
    public bool CodexModelEfficiencyTestEnabled { get; set; }
    public int CodexModelTokenEfficiencyTestPercent { get; set; }
    public int CodexModelTimeEfficiencyTestPercent { get; set; }
    public int CodexModelTokenEfficiencyBaselinePassed { get; set; }
    public int CodexModelTokenEfficiencyBaselineTokens { get; set; }
    public int CodexModelTimeEfficiencyBaselinePassed { get; set; }
    public int CodexModelTimeEfficiencyBaselineSeconds { get; set; }
    public int CodexModelTokenEfficiencyLowThresholdPercent { get; set; }
    public int CodexModelTimeEfficiencyLowThresholdPercent { get; set; }
    public WidgetPerformanceMode PerformanceMode { get; set; }
    public bool PowerSavingEnabled
    {
        get { return this.PerformanceMode == WidgetPerformanceMode.BatterySaver; }
        set { this.PerformanceMode = value ? WidgetPerformanceMode.BatterySaver : WidgetPerformanceMode.Balanced; }
    }

    public bool HoverOpacityEnabled { get; set; }
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
        this.BackgroundTransparencyPercent = DefaultBackgroundTransparency;
        this.ApplicationTransparencyPercent = 0;
        this.CodexRadarWidth = defaults.CodexRadarWidth;
        this.CodexRadarHeight = defaults.CodexRadarHeight;
        this.CodexRadarLeftX = defaults.CodexRadarLeftX;
        this.CodexRadarBottomY = defaults.CodexRadarBottomY;
        this.CodexRadarTransparencyPercent = DefaultBackgroundTransparency;
        this.PowerThermalWidth = defaults.PowerThermalWidth;
        this.PowerThermalHeight = defaults.PowerThermalHeight;
        this.PowerThermalLeftX = defaults.PowerThermalLeftX;
        this.PowerThermalBottomY = defaults.PowerThermalBottomY;
        this.PowerThermalTransparencyPercent = DefaultBackgroundTransparency;
        this.PowerThermalAutoSizeEnabled = true;
        this.PowerThermalAutoDirection = PowerThermalAutoDirection.Left;
        this.PowerThermalVisibleAlertCount = DefaultPowerThermalVisibleAlerts;
        this.NetworkMonitorWidth = defaults.NetworkMonitorWidth;
        this.NetworkMonitorHeight = defaults.NetworkMonitorHeight;
        this.NetworkMonitorLeftX = defaults.NetworkMonitorLeftX;
        this.NetworkMonitorBottomY = defaults.NetworkMonitorBottomY;
        this.NetworkMonitorTransparencyPercent = DefaultBackgroundTransparency;
        this.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        this.GfwProbeEnabled = false;
        this.GfwProbeIntervalMinutes = DefaultGfwProbeIntervalMinutes;
        this.GfwProbeManualRefreshToken = 0;
        this.ConnectionCheckWidth = defaults.ConnectionCheckWidth;
        this.ConnectionCheckHeight = defaults.ConnectionCheckHeight;
        this.ConnectionCheckLeftX = defaults.ConnectionCheckLeftX;
        this.ConnectionCheckBottomY = defaults.ConnectionCheckBottomY;
        this.ConnectionCheckTransparencyPercent = DefaultBackgroundTransparency;
        this.ConnectionCheckBorderTransparencyPercent = DefaultConnectionCheckBorderTransparency;
        this.ConnectionCheckIntervalSeconds = DefaultConnectionCheckIntervalSeconds;
        this.ConnectionCheckManualRefreshToken = 0;
        this.OperationButtonSize = defaults.OperationButtonSize;
        this.OperationLeftOffset = defaults.OperationLeftOffset;
        this.OperationBottomOffset = defaults.OperationBottomOffset;
        this.OperationBackgroundTransparencyPercent = 0;
        this.VisibilityMode = WidgetVisibilityMode.DesktopOnly;
        this.ClickThroughMode = ClickThroughMode.Auto;
        this.StartupEnabled = Program.IsStartupEnabled();
        this.ShowCpu = true;
        this.ShowMemory = true;
        this.ShowDisk = true;
        this.ShowNetwork = true;
        this.ShowGpu = true;
        this.ShowNpu = true;
        this.AlertTestEnabled = false;
        this.ThermalTestMode = ThermalTestMode.Off;
        this.CodexRadarTestMode = CodexRadarTestMode.Off;
        this.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        this.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        this.CodexModelIqTestEnabled = false;
        this.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        this.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
        this.CodexModelEfficiencyTestEnabled = false;
        this.CodexModelTokenEfficiencyTestPercent = DefaultCodexModelEfficiencyPercent;
        this.CodexModelTimeEfficiencyTestPercent = DefaultCodexModelEfficiencyPercent;
        this.CodexModelTokenEfficiencyBaselinePassed = DefaultCodexModelEfficiencyBaselineValue;
        this.CodexModelTokenEfficiencyBaselineTokens = DefaultCodexModelEfficiencyBaselineValue;
        this.CodexModelTimeEfficiencyBaselinePassed = DefaultCodexModelEfficiencyBaselineValue;
        this.CodexModelTimeEfficiencyBaselineSeconds = DefaultCodexModelEfficiencyBaselineValue;
        this.CodexModelTokenEfficiencyLowThresholdPercent = DefaultCodexModelEfficiencyLowThresholdPercent;
        this.CodexModelTimeEfficiencyLowThresholdPercent = DefaultCodexModelEfficiencyLowThresholdPercent;
        this.PerformanceMode = WidgetPerformanceMode.Balanced;
        this.HoverOpacityEnabled = false;
        this.MetricOrder = CloneMetricOrder(DefaultMetricOrder);
    }

    private WidgetSettings(bool skipDefaults)
    {
    }

    public static WidgetSettings CreateDefaults()
    {
        WidgetSettings settings = new WidgetSettings(true);
        float scale = GetPrimaryScale();
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int margin = (int)Math.Round(16.0f * scale);

        settings.Width = Clamp((int)Math.Round(392.0f * scale), MinWidth, MaxWidth);
        settings.Height = Clamp((int)Math.Round(116.0f * scale), MinHeight, MaxHeight);
        settings.LeftX = workArea.Right - settings.Width - margin;
        settings.BottomY = workArea.Bottom - margin - 1;
        settings.BackgroundTransparencyPercent = DefaultBackgroundTransparency;
        settings.ApplicationTransparencyPercent = 0;
        settings.CodexRadarWidth = Clamp((int)Math.Round(192.0f * scale), MinCodexRadarWidth, MaxCodexRadarWidth);
        settings.CodexRadarHeight = Clamp((int)Math.Round(58.0f * scale), MinCodexRadarHeight, MaxCodexRadarHeight);
        settings.CodexRadarLeftX = workArea.Right - settings.CodexRadarWidth - margin;
        settings.CodexRadarBottomY = settings.BottomY - settings.Height - margin;
        settings.CodexRadarTransparencyPercent = DefaultBackgroundTransparency;
        settings.PowerThermalWidth = Clamp((int)Math.Round(settings.CodexRadarWidth * 0.34f), MinPowerThermalWidth, MaxPowerThermalWidth);
        settings.PowerThermalHeight = settings.CodexRadarHeight;
        settings.PowerThermalLeftX = Math.Max(workArea.Left, settings.CodexRadarLeftX - settings.PowerThermalWidth - (int)Math.Round(6.0f * scale));
        settings.PowerThermalBottomY = settings.CodexRadarBottomY;
        settings.PowerThermalTransparencyPercent = DefaultBackgroundTransparency;
        settings.PowerThermalAutoSizeEnabled = true;
        settings.PowerThermalAutoDirection = PowerThermalAutoDirection.Left;
        settings.PowerThermalVisibleAlertCount = DefaultPowerThermalVisibleAlerts;
        settings.NetworkMonitorWidth = Clamp((int)Math.Round(420.0f * scale), MinNetworkMonitorWidth, MaxNetworkMonitorWidth);
        settings.NetworkMonitorHeight = Clamp((int)Math.Round(150.0f * scale), MinNetworkMonitorHeight, MaxNetworkMonitorHeight);
        settings.NetworkMonitorLeftX = workArea.Right - settings.NetworkMonitorWidth - margin;
        settings.NetworkMonitorBottomY = settings.CodexRadarBottomY - settings.CodexRadarHeight - margin;
        settings.NetworkMonitorTransparencyPercent = DefaultBackgroundTransparency;
        settings.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        settings.GfwProbeEnabled = false;
        settings.GfwProbeIntervalMinutes = DefaultGfwProbeIntervalMinutes;
        settings.GfwProbeManualRefreshToken = 0;
        settings.ConnectionCheckWidth = Clamp((int)Math.Round(settings.NetworkMonitorWidth * 0.5f), MinConnectionCheckWidth, MaxConnectionCheckWidth);
        settings.ConnectionCheckHeight = Clamp((int)Math.Round(settings.NetworkMonitorHeight * 0.5f), MinConnectionCheckHeight, MaxConnectionCheckHeight);
        settings.ConnectionCheckLeftX = workArea.Right - settings.ConnectionCheckWidth - margin;
        settings.ConnectionCheckBottomY = settings.NetworkMonitorBottomY - settings.NetworkMonitorHeight - margin;
        settings.ConnectionCheckTransparencyPercent = DefaultBackgroundTransparency;
        settings.ConnectionCheckBorderTransparencyPercent = DefaultConnectionCheckBorderTransparency;
        settings.ConnectionCheckIntervalSeconds = DefaultConnectionCheckIntervalSeconds;
        settings.ConnectionCheckManualRefreshToken = 0;
        settings.OperationButtonSize = Clamp((int)Math.Round(56.0f * scale), MinOperationButtonSize, MaxOperationButtonSize);
        settings.OperationLeftOffset = Math.Max(0, (int)Math.Round(8.0f * scale));
        settings.OperationBottomOffset = Math.Max(0, (int)Math.Round(8.0f * scale));
        settings.OperationBackgroundTransparencyPercent = 0;
        settings.VisibilityMode = WidgetVisibilityMode.DesktopOnly;
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
        settings.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        settings.CodexModelIqTestEnabled = false;
        settings.CodexModelIqTestPassed = DefaultCodexModelIqBaselinePassed;
        settings.CodexModelIqBaselinePassed = DefaultCodexModelIqBaselinePassed;
        settings.CodexModelEfficiencyTestEnabled = false;
        settings.CodexModelTokenEfficiencyTestPercent = DefaultCodexModelEfficiencyPercent;
        settings.CodexModelTimeEfficiencyTestPercent = DefaultCodexModelEfficiencyPercent;
        settings.CodexModelTokenEfficiencyBaselinePassed = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTokenEfficiencyBaselineTokens = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTimeEfficiencyBaselinePassed = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTimeEfficiencyBaselineSeconds = DefaultCodexModelEfficiencyBaselineValue;
        settings.CodexModelTokenEfficiencyLowThresholdPercent = DefaultCodexModelEfficiencyLowThresholdPercent;
        settings.CodexModelTimeEfficiencyLowThresholdPercent = DefaultCodexModelEfficiencyLowThresholdPercent;
        settings.PerformanceMode = WidgetPerformanceMode.Balanced;
        settings.HoverOpacityEnabled = false;
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
            NetworkStatusTestMode = this.NetworkStatusTestMode,
            GfwProbeEnabled = this.GfwProbeEnabled,
            GfwProbeIntervalMinutes = this.GfwProbeIntervalMinutes,
            GfwProbeManualRefreshToken = this.GfwProbeManualRefreshToken,
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
            CleanIpBadgeTestMode = this.CleanIpBadgeTestMode,
            CodexModelIqTestEnabled = this.CodexModelIqTestEnabled,
            CodexModelIqTestPassed = this.CodexModelIqTestPassed,
            CodexModelIqBaselinePassed = this.CodexModelIqBaselinePassed,
            CodexModelEfficiencyTestEnabled = this.CodexModelEfficiencyTestEnabled,
            CodexModelTokenEfficiencyTestPercent = this.CodexModelTokenEfficiencyTestPercent,
            CodexModelTimeEfficiencyTestPercent = this.CodexModelTimeEfficiencyTestPercent,
            CodexModelTokenEfficiencyBaselinePassed = this.CodexModelTokenEfficiencyBaselinePassed,
            CodexModelTokenEfficiencyBaselineTokens = this.CodexModelTokenEfficiencyBaselineTokens,
            CodexModelTimeEfficiencyBaselinePassed = this.CodexModelTimeEfficiencyBaselinePassed,
            CodexModelTimeEfficiencyBaselineSeconds = this.CodexModelTimeEfficiencyBaselineSeconds,
            CodexModelTokenEfficiencyLowThresholdPercent = this.CodexModelTokenEfficiencyLowThresholdPercent,
            CodexModelTimeEfficiencyLowThresholdPercent = this.CodexModelTimeEfficiencyLowThresholdPercent,
            PerformanceMode = this.PerformanceMode,
            HoverOpacityEnabled = this.HoverOpacityEnabled,
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
        this.GfwProbeIntervalMinutes = Clamp(this.GfwProbeIntervalMinutes, MinGfwProbeIntervalMinutes, MaxGfwProbeIntervalMinutes);
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
        if (!Enum.IsDefined(typeof(WidgetPerformanceMode), this.PerformanceMode))
        {
            this.PerformanceMode = WidgetPerformanceMode.Balanced;
        }

        if (!Enum.IsDefined(typeof(ClickThroughMode), this.ClickThroughMode))
        {
            this.ClickThroughMode = ClickThroughMode.Auto;
        }

        if (!Enum.IsDefined(typeof(CodexRadarTestMode), this.CodexRadarTestMode))
        {
            this.CodexRadarTestMode = CodexRadarTestMode.Off;
        }

        if (!Enum.IsDefined(typeof(ServiceHealthTestMode), this.ServiceHealthTestMode))
        {
            this.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        }

        if (!Enum.IsDefined(typeof(CleanIpBadgeTestMode), this.CleanIpBadgeTestMode))
        {
            this.CleanIpBadgeTestMode = CleanIpBadgeTestMode.Off;
        }

        if (!Enum.IsDefined(typeof(NetworkStatusTestMode), this.NetworkStatusTestMode))
        {
            this.NetworkStatusTestMode = NetworkStatusTestMode.Off;
        }

        this.MetricOrder = NormalizeMetricOrder(this.MetricOrder);
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        this.LeftX = Clamp(this.LeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.Width));
        this.BottomY = Clamp(this.BottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.Height - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
        this.CodexRadarLeftX = Clamp(this.CodexRadarLeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.CodexRadarWidth));
        this.CodexRadarBottomY = Clamp(this.CodexRadarBottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.CodexRadarHeight - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
        this.PowerThermalLeftX = Clamp(this.PowerThermalLeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.PowerThermalWidth));
        this.PowerThermalBottomY = Clamp(this.PowerThermalBottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.PowerThermalHeight - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
        this.NetworkMonitorLeftX = Clamp(this.NetworkMonitorLeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.NetworkMonitorWidth));
        this.NetworkMonitorBottomY = Clamp(this.NetworkMonitorBottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.NetworkMonitorHeight - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
        this.ConnectionCheckLeftX = Clamp(this.ConnectionCheckLeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.ConnectionCheckWidth));
        this.ConnectionCheckBottomY = Clamp(this.ConnectionCheckBottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.ConnectionCheckHeight - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
        float scale = GetPrimaryScale();
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int operationMaxLeftOffset = Math.Max(0, workArea.Width - GetOperationWindowWidth(this.OperationButtonSize, scale));
        int operationMaxBottomOffset = Math.Max(0, workArea.Height - GetOperationWindowHeight(this.OperationButtonSize, scale));
        this.OperationLeftOffset = Clamp(this.OperationLeftOffset, MinOperationOffset, Math.Min(MaxOperationOffset, operationMaxLeftOffset));
        this.OperationBottomOffset = Clamp(this.OperationBottomOffset, MinOperationOffset, Math.Min(MaxOperationOffset, operationMaxBottomOffset));
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
        Directory.CreateDirectory(Logger.DirectoryPath);
        string[] lines = new string[]
        {
            "Version=8",
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
            "NetworkStatusTestMode=" + this.NetworkStatusTestMode,
            "GfwProbeEnabled=" + this.GfwProbeEnabled,
            "GfwProbeIntervalMinutes=" + this.GfwProbeIntervalMinutes,
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
            "CleanIpBadgeTestMode=" + this.CleanIpBadgeTestMode,
            "CodexModelIqTestEnabled=" + this.CodexModelIqTestEnabled,
            "CodexModelIqTestPassed=" + this.CodexModelIqTestPassed,
            "CodexModelIqBaselinePassed=" + this.CodexModelIqBaselinePassed,
            "CodexModelEfficiencyTestEnabled=" + this.CodexModelEfficiencyTestEnabled,
            "CodexModelTokenEfficiencyTestPercent=" + this.CodexModelTokenEfficiencyTestPercent,
            "CodexModelTimeEfficiencyTestPercent=" + this.CodexModelTimeEfficiencyTestPercent,
            "CodexModelTokenEfficiencyBaselinePassed=" + this.CodexModelTokenEfficiencyBaselinePassed,
            "CodexModelTokenEfficiencyBaselineTokens=" + this.CodexModelTokenEfficiencyBaselineTokens,
            "CodexModelTimeEfficiencyBaselinePassed=" + this.CodexModelTimeEfficiencyBaselinePassed,
            "CodexModelTimeEfficiencyBaselineSeconds=" + this.CodexModelTimeEfficiencyBaselineSeconds,
            "CodexModelTokenEfficiencyLowThresholdPercent=" + this.CodexModelTokenEfficiencyLowThresholdPercent,
            "CodexModelTimeEfficiencyLowThresholdPercent=" + this.CodexModelTimeEfficiencyLowThresholdPercent,
            "PowerSavingEnabled=" + this.PowerSavingEnabled,
            "PerformanceMode=" + this.PerformanceMode,
            "HoverOpacityEnabled=" + this.HoverOpacityEnabled,
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

    public static bool ShouldEnableProcessPowerSaving(WidgetPerformanceMode mode)
    {
        return mode == WidgetPerformanceMode.BatterySaver;
    }

    public static int GetWidgetSampleIntervalMs(WidgetPerformanceMode mode)
    {
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

    public static int GetNetworkIdlePollingIntervalMs(WidgetPerformanceMode mode)
    {
        return GetInteractionIdlePollingIntervalMs(mode);
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
        return margin * 2 + buttonSize + smallSize * 4;
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
