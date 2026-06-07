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

internal sealed class SettingsForm : Form
{
    private const int PreviewDebounceMs = 75;
    private readonly WidgetForm owner;
    private readonly System.Windows.Forms.Timer previewTimer;
    private WidgetSettings baseline;
    private NumericUpDown widthBox;
    private NumericUpDown heightBox;
    private NumericUpDown leftXBox;
    private NumericUpDown bottomYBox;
    private NumericUpDown backgroundTransparencyBox;
    private NumericUpDown applicationTransparencyBox;
    private NumericUpDown codexRadarWidthBox;
    private NumericUpDown codexRadarHeightBox;
    private NumericUpDown codexRadarLeftXBox;
    private NumericUpDown codexRadarBottomYBox;
    private NumericUpDown codexRadarTransparencyBox;
    private NumericUpDown powerThermalWidthBox;
    private NumericUpDown powerThermalHeightBox;
    private NumericUpDown powerThermalLeftXBox;
    private NumericUpDown powerThermalBottomYBox;
    private NumericUpDown powerThermalTransparencyBox;
    private NumericUpDown powerThermalVisibleAlertCountBox;
    private NumericUpDown networkMonitorWidthBox;
    private NumericUpDown networkMonitorHeightBox;
    private NumericUpDown networkMonitorLeftXBox;
    private NumericUpDown networkMonitorBottomYBox;
    private NumericUpDown networkMonitorTransparencyBox;
    private NumericUpDown gfwProbeIntervalBox;
    private NumericUpDown connectionCheckWidthBox;
    private NumericUpDown connectionCheckHeightBox;
    private NumericUpDown connectionCheckLeftXBox;
    private NumericUpDown connectionCheckBottomYBox;
    private NumericUpDown connectionCheckTransparencyBox;
    private NumericUpDown connectionCheckBorderTransparencyBox;
    private NumericUpDown connectionCheckIntervalBox;
    private CheckBox powerThermalAutoSizeCheck;
    private ComboBox powerThermalAutoDirectionCombo;
    private NumericUpDown operationButtonSizeBox;
    private NumericUpDown operationLeftOffsetBox;
    private NumericUpDown operationBottomOffsetBox;
    private NumericUpDown operationTransparencyBox;
    private NumericUpDown codexModelIqTestPassedBox;
    private NumericUpDown codexModelIqBaselineBox;
    private NumericUpDown codexModelTokenEfficiencyTestBox;
    private NumericUpDown codexModelTimeEfficiencyTestBox;
    private NumericUpDown codexModelTokenEfficiencyBaselinePassedBox;
    private NumericUpDown codexModelTokenEfficiencyBaselineTokensBox;
    private NumericUpDown codexModelTimeEfficiencyBaselinePassedBox;
    private NumericUpDown codexModelTimeEfficiencyBaselineSecondsBox;
    private NumericUpDown codexModelTokenEfficiencyLowThresholdBox;
    private NumericUpDown codexModelTimeEfficiencyLowThresholdBox;
    private TrackBar widthSlider;
    private TrackBar heightSlider;
    private TrackBar leftXSlider;
    private TrackBar bottomYSlider;
    private TrackBar backgroundTransparencySlider;
    private TrackBar applicationTransparencySlider;
    private TrackBar codexRadarWidthSlider;
    private TrackBar codexRadarHeightSlider;
    private TrackBar codexRadarLeftXSlider;
    private TrackBar codexRadarBottomYSlider;
    private TrackBar codexRadarTransparencySlider;
    private TrackBar powerThermalWidthSlider;
    private TrackBar powerThermalHeightSlider;
    private TrackBar powerThermalLeftXSlider;
    private TrackBar powerThermalBottomYSlider;
    private TrackBar powerThermalTransparencySlider;
    private TrackBar powerThermalVisibleAlertCountSlider;
    private TrackBar networkMonitorWidthSlider;
    private TrackBar networkMonitorHeightSlider;
    private TrackBar networkMonitorLeftXSlider;
    private TrackBar networkMonitorBottomYSlider;
    private TrackBar networkMonitorTransparencySlider;
    private TrackBar gfwProbeIntervalSlider;
    private TrackBar connectionCheckWidthSlider;
    private TrackBar connectionCheckHeightSlider;
    private TrackBar connectionCheckLeftXSlider;
    private TrackBar connectionCheckBottomYSlider;
    private TrackBar connectionCheckTransparencySlider;
    private TrackBar connectionCheckBorderTransparencySlider;
    private TrackBar connectionCheckIntervalSlider;
    private TrackBar operationButtonSizeSlider;
    private TrackBar operationLeftOffsetSlider;
    private TrackBar operationBottomOffsetSlider;
    private TrackBar operationTransparencySlider;
    private TrackBar codexModelIqTestPassedSlider;
    private TrackBar codexModelIqBaselineSlider;
    private TrackBar codexModelTokenEfficiencyTestSlider;
    private TrackBar codexModelTimeEfficiencyTestSlider;
    private TrackBar codexModelTokenEfficiencyLowThresholdSlider;
    private TrackBar codexModelTimeEfficiencyLowThresholdSlider;
    private ComboBox visibilityCombo;
    private ComboBox thermalTestCombo;
    private ComboBox performanceModeCombo;
    private ComboBox clickThroughCombo;
    private Button alertTestButton;
    private Button codexRadarTestButton;
    private Button serviceHealthTestButton;
    private Button cleanIpBadgeTestButton;
    private Button connectionCheckManualRefreshButton;
    private Button networkStatusTestButton;
    private Button gfwProbeTestButton;
    private Button extraResetNotificationTestButton;
    private Button radarOpenNotificationTestButton;
    private CheckBox startupCheck;
    private CheckBox hoverOpacityCheck;
    private CheckBox codexModelIqTestCheck;
    private CheckBox codexModelEfficiencyTestCheck;
    private CheckBox gfwProbeCheck;
    private FlowLayoutPanel availableMetricsPanel;
    private TableLayoutPanel metricSlotsPanel;
    private Panel[] metricSlotPanels;
    private string draggedMetricId;
    private int draggedSourceSlotIndex;
    private int gfwProbeManualRefreshToken;
    private int connectionCheckManualRefreshToken;
    private bool initializing;
    private bool saved;

    public bool OwnerFormClosing { get; set; }

    public SettingsForm(WidgetForm owner, WidgetSettings baseline)
    {
        this.owner = owner;
        this.baseline = baseline.Clone();
        this.baseline.Normalize();
        this.previewTimer = new System.Windows.Forms.Timer();
        this.previewTimer.Interval = PreviewDebounceMs;
        this.previewTimer.Tick += OnPreviewTimerTick;

        this.Text = "性能小窗设置";
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = false;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.AutoScroll = false;
        this.AutoScrollMinSize = GetDesiredClientSize();
        this.ClientSize = FitClientSizeToScreen(GetDesiredClientSize());
        this.Font = DesignTokens.CreateUIFont(10.0f);
        this.BackColor = DesignTokens.Colors.Window;
        this.ForeColor = DesignTokens.Colors.Text;

        BuildControls();
        LoadControls(this.baseline);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        this.previewTimer.Stop();
        if (!this.saved && !this.OwnerFormClosing)
        {
            this.owner.RevertSettings(this.baseline);
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.previewTimer.Tick -= OnPreviewTimerTick;
        this.previewTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildControls()
    {
        InitializeControls();
        PopulateComboOptions();
        WireControlPairs();

        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(DesignTokens.Spacing.SettingsRootX, DesignTokens.Spacing.SettingsRootY, DesignTokens.Spacing.SettingsRootX, DesignTokens.Spacing.SettingsRootY);
        root.BackColor = DesignTokens.Colors.Window;
        root.ColumnCount = 1;
        root.RowCount = 3;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        this.Controls.Add(root);

        Label title = new Label();
        title.Text = "性能小窗设置";
        title.Font = DesignTokens.CreateUIFont(16.0f, FontStyle.Bold);
        title.ForeColor = DesignTokens.Colors.Text;
        title.BackColor = DesignTokens.Colors.Window;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(title, 0, 0);

        TabControl tabs = new TabControl();
        tabs.Dock = DockStyle.Fill;
        tabs.Font = DesignTokens.CreateUIFont(10.0f);
        tabs.Controls.Add(BuildRuntimeTab());
        tabs.Controls.Add(BuildWidgetTab());
        tabs.Controls.Add(BuildCodexRadarTab());
        tabs.Controls.Add(BuildPowerTab());
        tabs.Controls.Add(BuildNetworkMonitorTab());
        tabs.Controls.Add(BuildConnectionCheckTab());
        tabs.Controls.Add(BuildOperationTab());
        tabs.Controls.Add(BuildMetricsTab());
        root.Controls.Add(tabs, 0, 1);

        root.Controls.Add(BuildFooterButtons(), 0, 2);
    }

    private void InitializeControls()
    {
        this.widthBox = BuildNumberBox(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightBox = BuildNumberBox(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.backgroundTransparencyBox.Increment = 1;
        this.applicationTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.applicationTransparencyBox.Increment = 1;
        this.codexRadarWidthBox = BuildNumberBox(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MaxCodexRadarWidth);
        this.codexRadarHeightBox = BuildNumberBox(WidgetSettings.MinCodexRadarHeight, WidgetSettings.MaxCodexRadarHeight);
        this.codexRadarLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.codexRadarBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.codexRadarTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.codexRadarTransparencyBox.Increment = 1;
        this.powerThermalWidthBox = BuildNumberBox(WidgetSettings.MinPowerThermalWidth, WidgetSettings.MaxPowerThermalWidth);
        this.powerThermalHeightBox = BuildNumberBox(WidgetSettings.MinPowerThermalHeight, WidgetSettings.MaxPowerThermalHeight);
        this.powerThermalLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.powerThermalBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.powerThermalTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.powerThermalTransparencyBox.Increment = 1;
        this.powerThermalVisibleAlertCountBox = BuildNumberBox(WidgetSettings.MinPowerThermalVisibleAlerts, WidgetSettings.MaxPowerThermalVisibleAlerts);
        this.powerThermalVisibleAlertCountBox.Increment = 1;
        this.networkMonitorWidthBox = BuildNumberBox(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorWidth);
        this.networkMonitorHeightBox = BuildNumberBox(WidgetSettings.MinNetworkMonitorHeight, WidgetSettings.MaxNetworkMonitorHeight);
        this.networkMonitorLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.networkMonitorBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.networkMonitorTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.networkMonitorTransparencyBox.Increment = 1;
        this.gfwProbeIntervalBox = BuildNumberBox(WidgetSettings.MinGfwProbeIntervalMinutes, WidgetSettings.MaxGfwProbeIntervalMinutes);
        this.gfwProbeIntervalBox.Increment = 5;
        this.connectionCheckWidthBox = BuildNumberBox(WidgetSettings.MinConnectionCheckWidth, WidgetSettings.MaxConnectionCheckWidth);
        this.connectionCheckHeightBox = BuildNumberBox(WidgetSettings.MinConnectionCheckHeight, WidgetSettings.MaxConnectionCheckHeight);
        this.connectionCheckLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.connectionCheckBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.connectionCheckTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.connectionCheckTransparencyBox.Increment = 1;
        this.connectionCheckBorderTransparencyBox = BuildNumberBox(WidgetSettings.MinBorderTransparency, WidgetSettings.MaxBorderTransparency);
        this.connectionCheckBorderTransparencyBox.Increment = 1;
        this.connectionCheckIntervalBox = BuildNumberBox(WidgetSettings.MinConnectionCheckIntervalSeconds, WidgetSettings.MaxConnectionCheckIntervalSeconds);
        this.connectionCheckIntervalBox.Increment = 5;
        this.operationButtonSizeBox = BuildNumberBox(WidgetSettings.MinOperationButtonSize, WidgetSettings.MaxOperationButtonSize);
        this.operationLeftOffsetBox = BuildNumberBox(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset);
        this.operationBottomOffsetBox = BuildNumberBox(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset);
        this.operationTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.operationTransparencyBox.Increment = 1;
        this.codexModelIqTestPassedBox = BuildNumberBox(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed);
        this.codexModelIqTestPassedBox.Increment = 1;
        this.codexModelIqBaselineBox = BuildNumberBox(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed);
        this.codexModelIqBaselineBox.Increment = 1;
        this.codexModelTokenEfficiencyTestBox = BuildNumberBox(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            WidgetSettings.MaxCodexModelEfficiencyPercent);
        this.codexModelTokenEfficiencyTestBox.Increment = 1;
        this.codexModelTimeEfficiencyTestBox = BuildNumberBox(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            WidgetSettings.MaxCodexModelEfficiencyPercent);
        this.codexModelTimeEfficiencyTestBox.Increment = 1;
        this.codexModelTokenEfficiencyBaselinePassedBox = BuildNumberBox(
            WidgetSettings.MinCodexModelIqPassed,
            WidgetSettings.MaxCodexModelIqPassed);
        this.codexModelTokenEfficiencyBaselinePassedBox.Increment = 1;
        this.codexModelTokenEfficiencyBaselineTokensBox = BuildNumberBox(
            WidgetSettings.MinCodexModelEfficiencyBaselineValue,
            WidgetSettings.MaxCodexModelEfficiencyBaselineValue);
        this.codexModelTokenEfficiencyBaselineTokensBox.Increment = 1000;
        this.codexModelTimeEfficiencyBaselinePassedBox = BuildNumberBox(
            WidgetSettings.MinCodexModelIqPassed,
            WidgetSettings.MaxCodexModelIqPassed);
        this.codexModelTimeEfficiencyBaselinePassedBox.Increment = 1;
        this.codexModelTimeEfficiencyBaselineSecondsBox = BuildNumberBox(
            WidgetSettings.MinCodexModelEfficiencyBaselineValue,
            WidgetSettings.MaxCodexModelEfficiencyBaselineValue);
        this.codexModelTimeEfficiencyBaselineSecondsBox.Increment = 10;
        this.codexModelTokenEfficiencyLowThresholdBox = BuildNumberBox(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent);
        this.codexModelTokenEfficiencyLowThresholdBox.Increment = 1;
        this.codexModelTimeEfficiencyLowThresholdBox = BuildNumberBox(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent);
        this.codexModelTimeEfficiencyLowThresholdBox.Increment = 1;

        this.widthSlider = BuildSlider(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightSlider = BuildSlider(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.applicationTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.codexRadarWidthSlider = BuildSlider(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MaxCodexRadarWidth);
        this.codexRadarHeightSlider = BuildSlider(WidgetSettings.MinCodexRadarHeight, WidgetSettings.MaxCodexRadarHeight);
        this.codexRadarLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.codexRadarBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.codexRadarTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.powerThermalWidthSlider = BuildSlider(WidgetSettings.MinPowerThermalWidth, WidgetSettings.MaxPowerThermalWidth);
        this.powerThermalHeightSlider = BuildSlider(WidgetSettings.MinPowerThermalHeight, WidgetSettings.MaxPowerThermalHeight);
        this.powerThermalLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.powerThermalBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.powerThermalTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.powerThermalVisibleAlertCountSlider = BuildSlider(WidgetSettings.MinPowerThermalVisibleAlerts, WidgetSettings.MaxPowerThermalVisibleAlerts);
        this.networkMonitorWidthSlider = BuildSlider(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorWidth);
        this.networkMonitorHeightSlider = BuildSlider(WidgetSettings.MinNetworkMonitorHeight, WidgetSettings.MaxNetworkMonitorHeight);
        this.networkMonitorLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.networkMonitorBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.networkMonitorTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.gfwProbeIntervalSlider = BuildSlider(WidgetSettings.MinGfwProbeIntervalMinutes, WidgetSettings.MaxGfwProbeIntervalMinutes);
        this.connectionCheckWidthSlider = BuildSlider(WidgetSettings.MinConnectionCheckWidth, WidgetSettings.MaxConnectionCheckWidth);
        this.connectionCheckHeightSlider = BuildSlider(WidgetSettings.MinConnectionCheckHeight, WidgetSettings.MaxConnectionCheckHeight);
        this.connectionCheckLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.connectionCheckBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.connectionCheckTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.connectionCheckBorderTransparencySlider = BuildSlider(WidgetSettings.MinBorderTransparency, WidgetSettings.MaxBorderTransparency);
        this.connectionCheckIntervalSlider = BuildSlider(WidgetSettings.MinConnectionCheckIntervalSeconds, WidgetSettings.MaxConnectionCheckIntervalSeconds);
        this.operationButtonSizeSlider = BuildSlider(WidgetSettings.MinOperationButtonSize, WidgetSettings.MaxOperationButtonSize);
        this.operationLeftOffsetSlider = BuildSlider(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset);
        this.operationBottomOffsetSlider = BuildSlider(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset);
        this.operationTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.codexModelIqTestPassedSlider = BuildSlider(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed);
        this.codexModelIqBaselineSlider = BuildSlider(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed);
        this.codexModelTokenEfficiencyTestSlider = BuildSlider(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            WidgetSettings.MaxCodexModelEfficiencyPercent);
        this.codexModelTimeEfficiencyTestSlider = BuildSlider(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            WidgetSettings.MaxCodexModelEfficiencyPercent);
        this.codexModelTokenEfficiencyLowThresholdSlider = BuildSlider(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent);
        this.codexModelTimeEfficiencyLowThresholdSlider = BuildSlider(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent);

        this.visibilityCombo = BuildCombo();
        this.thermalTestCombo = BuildCombo();
        this.powerThermalAutoDirectionCombo = BuildCombo();
        this.performanceModeCombo = BuildCombo();
        this.clickThroughCombo = BuildCombo();
        this.alertTestButton = BuildToggleButton();
        this.codexRadarTestButton = BuildCodexRadarTestButton();
        this.serviceHealthTestButton = BuildServiceHealthTestButton();
        this.cleanIpBadgeTestButton = BuildCleanIpBadgeTestButton();
        this.connectionCheckManualRefreshButton = BuildConnectionCheckManualRefreshButton();
        this.networkStatusTestButton = BuildNetworkStatusTestButton();
        this.gfwProbeTestButton = BuildGfwProbeTestButton();
        this.extraResetNotificationTestButton = BuildNotificationTestButton(
            "额外重置",
            delegate { this.owner.TestCodexExtraResetNotification(); });
        this.radarOpenNotificationTestButton = BuildNotificationTestButton(
            "速蹬开启",
            delegate { this.owner.TestCodexRadarOpenNotification(); });

        this.startupCheck = BuildCheckBox("开机自动启动");
        this.hoverOpacityCheck = BuildCheckBox("悬停透明 95%");
        this.powerThermalAutoSizeCheck = BuildCheckBox("启用自动大小");
        this.codexModelIqTestCheck = BuildCheckBox("覆盖实时 IQ 数据");
        this.codexModelEfficiencyTestCheck = BuildCheckBox("覆盖实时效率数据");
        this.gfwProbeCheck = BuildCheckBox("启用 GFW 检测");

        this.metricSlotPanels = new Panel[WidgetSettings.DefaultMetricOrder.Length];
    }

    private void PopulateComboOptions()
    {
        this.visibilityCombo.Items.Add(new ComboOption("仅桌面可见", WidgetVisibilityMode.DesktopOnly));
        this.visibilityCombo.Items.Add(new ComboOption("一直可见", WidgetVisibilityMode.AlwaysVisible));
        this.visibilityCombo.Items.Add(new ComboOption("仅全屏不可见", WidgetVisibilityMode.HideWhenFullscreen));
        this.performanceModeCombo.Items.Add(new ComboOption("性能", WidgetPerformanceMode.Smooth));
        this.performanceModeCombo.Items.Add(new ComboOption("均衡", WidgetPerformanceMode.Balanced));
        this.performanceModeCombo.Items.Add(new ComboOption("省电", WidgetPerformanceMode.BatterySaver));
        this.clickThroughCombo.Items.Add(new ComboOption("穿透关闭", ClickThroughMode.Disabled));
        this.clickThroughCombo.Items.Add(new ComboOption("穿透自动", ClickThroughMode.Auto));
        this.clickThroughCombo.Items.Add(new ComboOption("穿透开启", ClickThroughMode.Enabled));
        this.powerThermalAutoDirectionCombo.Items.Add(new ComboOption("向左延伸", PowerThermalAutoDirection.Left));
        this.powerThermalAutoDirectionCombo.Items.Add(new ComboOption("向下延伸", PowerThermalAutoDirection.Down));
        this.thermalTestCombo.Items.Add(new ComboOption("关闭", ThermalTestMode.Off));
        this.thermalTestCombo.Items.Add(new ComboOption("模拟 75 度", ThermalTestMode.Simulate75));
        this.thermalTestCombo.Items.Add(new ComboOption("模拟 100 度", ThermalTestMode.Simulate100));
    }

    private void WireControlPairs()
    {
        WirePair(this.widthBox, this.widthSlider);
        WirePair(this.heightBox, this.heightSlider);
        WirePair(this.leftXBox, this.leftXSlider);
        WirePair(this.bottomYBox, this.bottomYSlider);
        WirePair(this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        WirePair(this.applicationTransparencyBox, this.applicationTransparencySlider);
        WirePair(this.codexRadarWidthBox, this.codexRadarWidthSlider);
        WirePair(this.codexRadarHeightBox, this.codexRadarHeightSlider);
        WirePair(this.codexRadarLeftXBox, this.codexRadarLeftXSlider);
        WirePair(this.codexRadarBottomYBox, this.codexRadarBottomYSlider);
        WirePair(this.codexRadarTransparencyBox, this.codexRadarTransparencySlider);
        WirePair(this.powerThermalWidthBox, this.powerThermalWidthSlider);
        WirePair(this.powerThermalHeightBox, this.powerThermalHeightSlider);
        WirePair(this.powerThermalLeftXBox, this.powerThermalLeftXSlider);
        WirePair(this.powerThermalBottomYBox, this.powerThermalBottomYSlider);
        WirePair(this.powerThermalTransparencyBox, this.powerThermalTransparencySlider);
        WirePair(this.powerThermalVisibleAlertCountBox, this.powerThermalVisibleAlertCountSlider);
        WirePair(this.networkMonitorWidthBox, this.networkMonitorWidthSlider);
        WirePair(this.networkMonitorHeightBox, this.networkMonitorHeightSlider);
        WirePair(this.networkMonitorLeftXBox, this.networkMonitorLeftXSlider);
        WirePair(this.networkMonitorBottomYBox, this.networkMonitorBottomYSlider);
        WirePair(this.networkMonitorTransparencyBox, this.networkMonitorTransparencySlider);
        WirePair(this.gfwProbeIntervalBox, this.gfwProbeIntervalSlider);
        WirePair(this.connectionCheckWidthBox, this.connectionCheckWidthSlider);
        WirePair(this.connectionCheckHeightBox, this.connectionCheckHeightSlider);
        WirePair(this.connectionCheckLeftXBox, this.connectionCheckLeftXSlider);
        WirePair(this.connectionCheckBottomYBox, this.connectionCheckBottomYSlider);
        WirePair(this.connectionCheckTransparencyBox, this.connectionCheckTransparencySlider);
        WirePair(this.connectionCheckBorderTransparencyBox, this.connectionCheckBorderTransparencySlider);
        WirePair(this.connectionCheckIntervalBox, this.connectionCheckIntervalSlider);
        WirePair(this.operationButtonSizeBox, this.operationButtonSizeSlider);
        WirePair(this.operationLeftOffsetBox, this.operationLeftOffsetSlider);
        WirePair(this.operationBottomOffsetBox, this.operationBottomOffsetSlider);
        WirePair(this.operationTransparencyBox, this.operationTransparencySlider);
        WirePair(this.codexModelIqTestPassedBox, this.codexModelIqTestPassedSlider);
        WirePair(this.codexModelIqBaselineBox, this.codexModelIqBaselineSlider);
        WirePair(this.codexModelTokenEfficiencyTestBox, this.codexModelTokenEfficiencyTestSlider);
        WirePair(this.codexModelTimeEfficiencyTestBox, this.codexModelTimeEfficiencyTestSlider);
        WirePair(this.codexModelTokenEfficiencyLowThresholdBox, this.codexModelTokenEfficiencyLowThresholdSlider);
        WirePair(this.codexModelTimeEfficiencyLowThresholdBox, this.codexModelTimeEfficiencyLowThresholdSlider);
    }

    private TabPage BuildRuntimeTab()
    {
        TabPage page = BuildTabPage("运行");
        TableLayoutPanel section = BuildSettingsSection("性能和交互", 6);
        AddEditorRow(section, 1, "性能模式", this.performanceModeCombo);
        AddEditorRow(section, 2, "可见性", this.visibilityCombo);
        AddEditorRow(section, 3, "点击穿透", this.clickThroughCombo);
        AddCheckRow(section, 4, "启动", this.startupCheck);
        AddCheckRow(section, 5, "透明交互", this.hoverOpacityCheck);
        AddLabel(section, 6, "告警测试");
        Control alertEditor = BuildButtonEditor(this.alertTestButton);
        section.SetColumnSpan(alertEditor, 2);
        section.Controls.Add(alertEditor, 1, 6);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildWidgetTab()
    {
        TabPage page = BuildTabPage("主窗口");
        TableLayoutPanel section = BuildSettingsSection("主窗口布局", 6);
        AddSliderRow(section, 1, "窗口宽度", this.widthBox, this.widthSlider);
        AddSliderRow(section, 2, "窗口高度", this.heightBox, this.heightSlider);
        AddSliderRow(section, 3, "位置 X", this.leftXBox, this.leftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.bottomYBox, this.bottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        AddSliderRow(section, 6, "内容透明度", this.applicationTransparencyBox, this.applicationTransparencySlider);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildCodexRadarTab()
    {
        TabPage page = BuildTabPage("CodexRadar");
        TableLayoutPanel section = BuildSettingsSection("CodexRadar 模块", 20);
        AddSliderRow(section, 1, "模块宽度", this.codexRadarWidthBox, this.codexRadarWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.codexRadarHeightBox, this.codexRadarHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.codexRadarLeftXBox, this.codexRadarLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.codexRadarBottomYBox, this.codexRadarBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.codexRadarTransparencyBox, this.codexRadarTransparencySlider);
        AddLabel(section, 6, "Radar测试");
        Control radarEditor = BuildButtonEditor(this.codexRadarTestButton);
        section.SetColumnSpan(radarEditor, 2);
        section.Controls.Add(radarEditor, 1, 6);
        AddLabel(section, 7, "网站检测测试");
        Control healthEditor = BuildButtonEditor(this.serviceHealthTestButton);
        section.SetColumnSpan(healthEditor, 2);
        section.Controls.Add(healthEditor, 1, 7);
        AddCheckRow(section, 8, "IQ测试启用", this.codexModelIqTestCheck);
        AddSliderRow(section, 9, "IQ测试通过数", this.codexModelIqTestPassedBox, this.codexModelIqTestPassedSlider);
        AddSliderRow(section, 10, "IQ正常基准", this.codexModelIqBaselineBox, this.codexModelIqBaselineSlider);
        AddEditorRow(section, 11, "Token基线通过", this.codexModelTokenEfficiencyBaselinePassedBox);
        AddEditorRow(section, 12, "Token基线Token", this.codexModelTokenEfficiencyBaselineTokensBox);
        AddEditorRow(section, 13, "时间基线通过", this.codexModelTimeEfficiencyBaselinePassedBox);
        AddEditorRow(section, 14, "时间基线秒", this.codexModelTimeEfficiencyBaselineSecondsBox);
        AddSliderRow(section, 15, "Token低效阈值", this.codexModelTokenEfficiencyLowThresholdBox, this.codexModelTokenEfficiencyLowThresholdSlider);
        AddSliderRow(section, 16, "时间低效阈值", this.codexModelTimeEfficiencyLowThresholdBox, this.codexModelTimeEfficiencyLowThresholdSlider);
        AddCheckRow(section, 17, "效率测试启用", this.codexModelEfficiencyTestCheck);
        AddSliderRow(section, 18, "Token效率测试", this.codexModelTokenEfficiencyTestBox, this.codexModelTokenEfficiencyTestSlider);
        AddSliderRow(section, 19, "时间效率测试", this.codexModelTimeEfficiencyTestBox, this.codexModelTimeEfficiencyTestSlider);
        AddCheckRow(section, 20, "通知测试", this.extraResetNotificationTestButton, this.radarOpenNotificationTestButton);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildPowerTab()
    {
        TabPage page = BuildTabPage("功耗模块");
        TableLayoutPanel section = BuildSettingsSection("功耗模块", 9);
        AddSliderRow(section, 1, "模块宽度", this.powerThermalWidthBox, this.powerThermalWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.powerThermalHeightBox, this.powerThermalHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.powerThermalLeftXBox, this.powerThermalLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.powerThermalBottomYBox, this.powerThermalBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.powerThermalTransparencyBox, this.powerThermalTransparencySlider);
        AddCheckRow(section, 6, "自动延展", this.powerThermalAutoSizeCheck);
        AddEditorRow(section, 7, "扩展方向", this.powerThermalAutoDirectionCombo);
        AddSliderRow(section, 8, "显示告警数", this.powerThermalVisibleAlertCountBox, this.powerThermalVisibleAlertCountSlider);
        AddEditorRow(section, 9, "温度测试", this.thermalTestCombo);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildNetworkMonitorTab()
    {
        TabPage page = BuildTabPage("网络监控");
        TableLayoutPanel section = BuildSettingsSection("网络监控模块", 9);
        AddSliderRow(section, 1, "模块宽度", this.networkMonitorWidthBox, this.networkMonitorWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.networkMonitorHeightBox, this.networkMonitorHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.networkMonitorLeftXBox, this.networkMonitorLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.networkMonitorBottomYBox, this.networkMonitorBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.networkMonitorTransparencyBox, this.networkMonitorTransparencySlider);
        AddLabel(section, 6, "网络状态测试");
        Control statusEditor = BuildButtonEditor(this.networkStatusTestButton);
        section.SetColumnSpan(statusEditor, 2);
        section.Controls.Add(statusEditor, 1, 6);
        AddCheckRow(section, 7, "GFW检测", this.gfwProbeCheck);
        AddSliderRow(section, 8, "检测间隔分钟", this.gfwProbeIntervalBox, this.gfwProbeIntervalSlider);
        AddLabel(section, 9, "立即测试");
        Control gfwEditor = BuildButtonEditor(this.gfwProbeTestButton);
        section.SetColumnSpan(gfwEditor, 2);
        section.Controls.Add(gfwEditor, 1, 9);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildConnectionCheckTab()
    {
        TabPage page = BuildTabPage("连接检测");
        TableLayoutPanel section = BuildSettingsSection("CleanIP徽标模块", 8);
        AddSliderRow(section, 1, "模块宽度", this.connectionCheckWidthBox, this.connectionCheckWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.connectionCheckHeightBox, this.connectionCheckHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.connectionCheckLeftXBox, this.connectionCheckLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.connectionCheckBottomYBox, this.connectionCheckBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.connectionCheckTransparencyBox, this.connectionCheckTransparencySlider);
        AddSliderRow(section, 6, "白色边框透明度", this.connectionCheckBorderTransparencyBox, this.connectionCheckBorderTransparencySlider);
        AddLabel(section, 7, "手动刷新");
        Control manualEditor = BuildButtonEditor(this.connectionCheckManualRefreshButton);
        section.SetColumnSpan(manualEditor, 2);
        section.Controls.Add(manualEditor, 1, 7);
        AddLabel(section, 8, "强制测试");
        Control cleanIpEditor = BuildButtonEditor(this.cleanIpBadgeTestButton);
        section.SetColumnSpan(cleanIpEditor, 2);
        section.Controls.Add(cleanIpEditor, 1, 8);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildOperationTab()
    {
        TabPage page = BuildTabPage("操作模块");
        TableLayoutPanel section = BuildSettingsSection("操作模块", 4);
        AddSliderRow(section, 1, "按钮大小", this.operationButtonSizeBox, this.operationButtonSizeSlider);
        AddSliderRow(section, 2, "距左边缘", this.operationLeftOffsetBox, this.operationLeftOffsetSlider);
        AddSliderRow(section, 3, "距底边缘", this.operationBottomOffsetBox, this.operationBottomOffsetSlider);
        AddSliderRow(section, 4, "背景透明度", this.operationTransparencyBox, this.operationTransparencySlider);
        page.Controls.Add(section);
        return page;
    }

    private TabPage BuildMetricsTab()
    {
        TabPage page = BuildTabPage("栏目");
        Control metrics = BuildMetricLayoutSidePanel();
        metrics.Dock = DockStyle.Fill;
        page.Controls.Add(metrics);
        return page;
    }

    private TabPage BuildTabPage(string text)
    {
        TabPage page = new TabPage(text);
        page.BackColor = DesignTokens.Colors.Window;
        page.ForeColor = DesignTokens.Colors.Text;
        page.Padding = new Padding(DesignTokens.Spacing.SettingsPagePadding);
        page.AutoScroll = true;
        return page;
    }

    private TableLayoutPanel BuildSettingsSection(string title, int contentRows)
    {
        TableLayoutPanel section = new TableLayoutPanel();
        section.Dock = DockStyle.Top;
        section.AutoSize = true;
        section.BackColor = DesignTokens.Colors.Window;
        section.ColumnCount = 3;
        section.RowCount = contentRows + 1;
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        for (int i = 0; i < contentRows; i++)
        {
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        }

        Label label = new Label();
        label.Text = title;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = DesignTokens.CreateUIFont(12.0f, FontStyle.Bold);
        label.ForeColor = DesignTokens.Colors.Text;
        label.BackColor = DesignTokens.Colors.Window;
        section.SetColumnSpan(label, 3);
        section.Controls.Add(label, 0, 0);
        return section;
    }

    private CheckBox BuildCheckBox(string text)
    {
        CheckBox checkBox = new CheckBox();
        checkBox.Text = text;
        checkBox.AutoSize = true;
        checkBox.ForeColor = DesignTokens.Colors.Text;
        checkBox.BackColor = DesignTokens.Colors.Window;
        checkBox.Margin = new Padding(0, 0, DesignTokens.Spacing.SettingsCheckGap, 0);
        checkBox.CheckedChanged += OnSettingChanged;
        return checkBox;
    }

    private Button BuildToggleButton()
    {
        Button button = new Button();
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetAlertTestButtonState(!GetAlertTestButtonState());
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildCodexRadarTestButton()
    {
        Button button = new Button();
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetCodexRadarTestButtonMode(GetNextCodexRadarTestMode(GetCodexRadarTestButtonMode()));
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildServiceHealthTestButton()
    {
        Button button = new Button();
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetServiceHealthTestButtonMode(GetNextServiceHealthTestMode(GetServiceHealthTestButtonMode()));
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildCleanIpBadgeTestButton()
    {
        Button button = new Button();
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetCleanIpBadgeTestButtonMode(GetNextCleanIpBadgeTestMode(GetCleanIpBadgeTestButtonMode()));
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildConnectionCheckManualRefreshButton()
    {
        Button button = new Button();
        button.Text = "立即刷新";
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            this.connectionCheckManualRefreshToken++;
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildGfwProbeTestButton()
    {
        Button button = new Button();
        button.Text = "立即测试";
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            this.gfwProbeManualRefreshToken++;
            if (this.gfwProbeCheck != null)
            {
                this.gfwProbeCheck.Checked = true;
            }

            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildNetworkStatusTestButton()
    {
        Button button = new Button();
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetNetworkStatusTestButtonMode(GetNextNetworkStatusTestMode(GetNetworkStatusTestButtonMode()));
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildNotificationTestButton(string text, Action action)
    {
        Button button = new Button();
        button.Text = text;
        button.Width = DesignTokens.Sizes.SettingsButtonWidth;
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(DesignTokens.Sizes.SettingsButtonWidth, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 5, 14, 5);
        button.Click += delegate
        {
            if (!this.initializing && action != null)
            {
                action();
            }
        };
        StyleButton(button, false);
        return button;
    }

    private Control BuildFooterButtons()
    {
        Button saveButton = new Button();
        saveButton.Text = "保存";
        saveButton.Width = DesignTokens.Sizes.SettingsPrimaryButtonWidth;
        saveButton.Height = DesignTokens.Sizes.SettingsButtonHeight;
        saveButton.Click += delegate
        {
            try
            {
                WidgetSettings settings = ReadControls();
                this.owner.SaveSettings(settings);
                this.baseline = settings.Clone();
                this.baseline.Normalize();
                this.saved = true;
                MessageBox.Show(this, "保存完成。", "设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                MessageBox.Show(
                    this,
                    "保存失败。\r\n错误码: 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "\r\n" + ex.Message,
                    "设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        Button cancelButton = new Button();
        cancelButton.Text = "取消";
        cancelButton.Width = DesignTokens.Sizes.SettingsButtonWidth;
        cancelButton.Height = DesignTokens.Sizes.SettingsButtonHeight;
        cancelButton.Click += delegate { this.Close(); };

        Button resetButton = new Button();
        resetButton.Text = "重置";
        resetButton.Width = DesignTokens.Sizes.SettingsButtonWidth;
        resetButton.Height = DesignTokens.Sizes.SettingsButtonHeight;
        resetButton.Click += delegate
        {
            WidgetSettings defaults = WidgetSettings.CreateDefaults();
            defaults.StartupEnabled = Program.IsStartupEnabled();
            LoadControls(defaults);
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };

        Button fullExitButton = new Button();
        fullExitButton.Text = "完全退出";
        fullExitButton.Width = DesignTokens.Sizes.SettingsPrimaryButtonWidth;
        fullExitButton.Height = DesignTokens.Sizes.SettingsButtonHeight;
        fullExitButton.Click += delegate
        {
            this.OwnerFormClosing = true;
            this.saved = true;
            this.owner.FullyExitApplication();
        };

        Button exitButton = new Button();
        exitButton.Text = "退出软件";
        exitButton.Width = DesignTokens.Sizes.SettingsPrimaryButtonWidth;
        exitButton.Height = DesignTokens.Sizes.SettingsButtonHeight;
        exitButton.Click += delegate
        {
            this.OwnerFormClosing = true;
            this.saved = true;
            this.owner.ExitCurrentProcess();
        };

        StyleButton(saveButton, true);
        StyleButton(cancelButton, false);
        StyleButton(resetButton, false);
        StyleButton(fullExitButton, false);
        StyleButton(exitButton, false);

        TableLayoutPanel footer = new TableLayoutPanel();
        footer.Dock = DockStyle.Fill;
        footer.BackColor = DesignTokens.Colors.Window;
        footer.ColumnCount = 2;
        footer.RowCount = 1;
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        FlowLayoutPanel leftButtons = new FlowLayoutPanel();
        leftButtons.FlowDirection = FlowDirection.LeftToRight;
        leftButtons.Dock = DockStyle.Fill;
        leftButtons.BackColor = DesignTokens.Colors.Window;
        leftButtons.Padding = new Padding(0, 12, 0, 0);
        leftButtons.Controls.Add(fullExitButton);
        leftButtons.Controls.Add(exitButton);

        FlowLayoutPanel rightButtons = new FlowLayoutPanel();
        rightButtons.FlowDirection = FlowDirection.RightToLeft;
        rightButtons.Dock = DockStyle.Fill;
        rightButtons.BackColor = DesignTokens.Colors.Window;
        rightButtons.Padding = new Padding(0, 12, 0, 0);
        rightButtons.Controls.Add(saveButton);
        rightButtons.Controls.Add(cancelButton);
        rightButtons.Controls.Add(resetButton);

        footer.Controls.Add(leftButtons, 0, 0);
        footer.Controls.Add(rightButtons, 1, 0);
        return footer;
    }

    private static Size GetDesiredClientSize()
    {
        return new Size(1080, 760);
    }

    private static Size FitClientSizeToScreen(Size desiredSize)
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int margin = 64;
        int maxWidth = Math.Max(640, workArea.Width - margin);
        int maxHeight = Math.Max(520, workArea.Height - margin);
        int width = Math.Min(desiredSize.Width + SystemInformation.VerticalScrollBarWidth, maxWidth);
        int height = Math.Min(desiredSize.Height, maxHeight);
        return new Size(width, height);
    }

    private NumericUpDown BuildNumberBox(int min, int max)
    {
        NumericUpDown box = new NumericUpDown();
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = 10;
        box.Dock = DockStyle.Fill;
        box.Font = DesignTokens.CreateUIFont(10.5f);
        box.BackColor = DesignTokens.Colors.Control;
        box.ForeColor = DesignTokens.Colors.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.ValueChanged += OnSettingChanged;
        return box;
    }

    private TrackBar BuildSlider(int min, int max)
    {
        TrackBar slider = new TrackBar();
        slider.Minimum = min;
        slider.Maximum = max;
        slider.TickStyle = TickStyle.None;
        slider.AutoSize = false;
        slider.Height = 34;
        slider.Dock = DockStyle.Fill;
        slider.BackColor = DesignTokens.Colors.Window;
        slider.SmallChange = 1;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
        slider.ValueChanged += OnSettingChanged;
        return slider;
    }

    private ComboBox BuildCombo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.Font = DesignTokens.CreateUIFont(10.5f);
        combo.BackColor = DesignTokens.Colors.Control;
        combo.ForeColor = DesignTokens.Colors.Text;
        combo.SelectedIndexChanged += OnSettingChanged;
        return combo;
    }

    private Control BuildMetricLayoutSidePanel()
    {
        TableLayoutPanel panel = new TableLayoutPanel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DesignTokens.Colors.Window;
        panel.ColumnCount = 1;
        panel.RowCount = 2;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Padding = new Padding(12, 0, 0, 0);

        Label label = new Label();
        label.Text = "栏目排序";
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = DesignTokens.CreateUIFont(10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DesignTokens.Colors.SubtleText;
        label.BackColor = DesignTokens.Colors.Window;

        Control editor = BuildMetricLayoutEditor();
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(editor, 0, 1);
        return panel;
    }

    private Control BuildMetricLayoutEditor()
    {
        TableLayoutPanel editor = new TableLayoutPanel();
        editor.Dock = DockStyle.Fill;
        editor.BackColor = DesignTokens.Colors.Window;
        editor.ColumnCount = 1;
        editor.RowCount = 2;
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        this.availableMetricsPanel = new FlowLayoutPanel();
        this.availableMetricsPanel.Dock = DockStyle.Fill;
        this.availableMetricsPanel.BackColor = DesignTokens.Colors.Control;
        this.availableMetricsPanel.Padding = new Padding(10, 10, 10, 8);
        this.availableMetricsPanel.AllowDrop = true;
        this.availableMetricsPanel.DragEnter += OnMetricDragEnter;
        this.availableMetricsPanel.DragDrop += delegate
        {
            if (this.draggedSourceSlotIndex >= 0)
            {
                SetSlotMetric(this.draggedSourceSlotIndex, string.Empty);
                RefreshMetricLayoutEditor();
                this.saved = false;
                this.owner.PreviewSettings(ReadControls());
            }
        };

        this.metricSlotsPanel = new TableLayoutPanel();
        this.metricSlotsPanel.Dock = DockStyle.Fill;
        this.metricSlotsPanel.BackColor = DesignTokens.Colors.Window;
        int slotColumns = 2;
        int slotRows = (WidgetSettings.DefaultMetricOrder.Length + slotColumns - 1) / slotColumns;
        this.metricSlotsPanel.ColumnCount = slotColumns;
        this.metricSlotsPanel.RowCount = slotRows;
        for (int row = 0; row < slotRows; row++)
        {
            this.metricSlotsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f / slotRows));
        }

        for (int column = 0; column < slotColumns; column++)
        {
            this.metricSlotsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f / slotColumns));
        }

        for (int i = 0; i < WidgetSettings.DefaultMetricOrder.Length; i++)
        {
            Panel slot = BuildMetricSlot(i);
            this.metricSlotPanels[i] = slot;
            this.metricSlotsPanel.Controls.Add(slot, i % slotColumns, i / slotColumns);
        }

        editor.Controls.Add(this.availableMetricsPanel, 0, 0);
        editor.Controls.Add(this.metricSlotsPanel, 0, 1);
        return editor;
    }

    private Label BuildMetricChip(string metricId)
    {
        Label chip = new Label();
        chip.Text = WidgetSettings.MetricDisplayName(metricId);
        chip.Tag = metricId;
        chip.Width = 108;
        chip.Height = 34;
        chip.Margin = new Padding(0, 0, 8, 6);
        chip.TextAlign = ContentAlignment.MiddleCenter;
        chip.BackColor = DesignTokens.Colors.ControlActive;
        chip.ForeColor = DesignTokens.Colors.Text;
        chip.BorderStyle = BorderStyle.FixedSingle;
        chip.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        chip.Cursor = Cursors.Hand;
        chip.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                BeginMetricDrag(metricId, -1, chip);
            }
        };
        return chip;
    }

    private Panel BuildMetricSlot(int index)
    {
        Panel slot = new Panel();
        slot.Dock = DockStyle.Fill;
        slot.Margin = new Padding(0, 6, index % 2 == 0 ? 10 : 0, 0);
        slot.BackColor = DesignTokens.Colors.Control;
        slot.BorderStyle = BorderStyle.FixedSingle;
        slot.AllowDrop = true;
        slot.Tag = string.Empty;
        slot.DragEnter += OnMetricDragEnter;
        slot.DragDrop += delegate { DropMetricToSlot(index); };
        slot.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                string metricId = GetSlotMetric(index);
                if (metricId.Length > 0)
                {
                    BeginMetricDrag(metricId, index, slot);
                }
            }
        };

        Label number = new Label();
        number.Name = "slotNumber";
        number.Text = (index + 1).ToString();
        number.AutoSize = true;
        number.Location = new Point(14, 12);
        number.TextAlign = ContentAlignment.TopLeft;
        number.ForeColor = DesignTokens.Colors.SubtleText;
        number.BackColor = Color.Transparent;
        number.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        number.UseCompatibleTextRendering = true;

        Label content = new Label();
        content.Name = "slotContent";
        content.AutoSize = false;
        content.TextAlign = ContentAlignment.MiddleCenter;
        content.ForeColor = DesignTokens.Colors.Text;
        content.BackColor = Color.Transparent;
        content.Font = DesignTokens.CreateUIFont(10.0f, FontStyle.Bold);
        content.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                string metricId = GetSlotMetric(index);
                if (metricId.Length > 0)
                {
                    BeginMetricDrag(metricId, index, content);
                }
            }
        };

        Button remove = new Button();
        remove.Name = "slotRemove";
        remove.Text = "x";
        remove.Width = 24;
        remove.Height = 22;
        remove.Visible = false;
        remove.FlatStyle = FlatStyle.Flat;
        remove.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
        remove.FlatAppearance.BorderSize = 1;
        remove.BackColor = DesignTokens.Colors.ControlPressed;
        remove.ForeColor = DesignTokens.Colors.Text;
        remove.Font = DesignTokens.CreateUIFont(7.5f, FontStyle.Bold);
        remove.Click += delegate
        {
            SetSlotMetric(index, string.Empty);
            RefreshMetricLayoutEditor();
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };

        slot.Controls.Add(number);
        slot.Controls.Add(content);
        slot.Controls.Add(remove);
        slot.Resize += delegate
        {
            content.Location = new Point(30, 8);
            content.Size = new Size(Math.Max(10, slot.ClientSize.Width - 60), Math.Max(20, slot.ClientSize.Height - 22));
            remove.Location = new Point(Math.Max(0, slot.ClientSize.Width - remove.Width - 7), Math.Max(0, slot.ClientSize.Height - remove.Height - 7));
        };

        return slot;
    }

    private void AddSliderRow(TableLayoutPanel root, int row, string labelText, NumericUpDown editor, TrackBar slider)
    {
        AddLabel(root, row, labelText);
        root.Controls.Add(editor, 1, row);
        root.Controls.Add(slider, 2, row);
    }

    private void AddEditorRow(TableLayoutPanel root, int row, string labelText, Control editor)
    {
        AddLabel(root, row, labelText);
        root.Controls.Add(editor, 1, row);
        root.SetColumnSpan(editor, 2);
    }

    private void AddCheckRow(TableLayoutPanel root, int row, string labelText, params Control[] controls)
    {
        AddLabel(root, row, labelText);
        FlowLayoutPanel panel = new FlowLayoutPanel();
        panel.Dock = DockStyle.Fill;
        panel.FlowDirection = FlowDirection.LeftToRight;
        panel.WrapContents = false;
        panel.BackColor = DesignTokens.Colors.Window;
        panel.Padding = new Padding(0, 13, 0, 0);
        for (int i = 0; i < controls.Length; i++)
        {
            panel.Controls.Add(controls[i]);
        }

        root.SetColumnSpan(panel, 2);
        root.Controls.Add(panel, 1, row);
    }

    private Control BuildButtonEditor(Button button)
    {
        Panel panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DesignTokens.Colors.Window;
        button.Anchor = AnchorStyles.Left;
        button.Location = new Point(0, 8);
        panel.Controls.Add(button);
        panel.Resize += delegate
        {
            button.Location = new Point(0, Math.Max(0, (panel.ClientSize.Height - button.Height) / 2));
        };
        return panel;
    }

    private void AddLabel(TableLayoutPanel root, int row, string labelText)
    {
        Label label = new Label();
        label.Text = labelText;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = DesignTokens.CreateUIFont(10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DesignTokens.Colors.SubtleText;
        label.BackColor = DesignTokens.Colors.Window;
        root.Controls.Add(label, 0, row);
    }

    private void WirePair(NumericUpDown box, TrackBar slider)
    {
        box.ValueChanged += delegate
        {
            if (this.initializing)
            {
                return;
            }

            int value = (int)box.Value;
            if (slider.Value != value)
            {
                slider.Value = value;
            }
        };

        slider.ValueChanged += delegate
        {
            if (this.initializing)
            {
                return;
            }

            if ((int)box.Value != slider.Value)
            {
                box.Value = slider.Value;
            }
        };
    }

    private void LoadControls(WidgetSettings settings)
    {
        this.initializing = true;
        try
        {
            this.widthBox.Value = settings.Width;
            this.heightBox.Value = settings.Height;
            UpdatePositionRanges(settings.Width, settings.Height);
            UpdateCodexRadarPositionRanges(settings.CodexRadarWidth, settings.CodexRadarHeight);
            UpdatePowerThermalPositionRanges(settings.PowerThermalWidth, settings.PowerThermalHeight);
            UpdateNetworkMonitorPositionRanges(settings.NetworkMonitorWidth, settings.NetworkMonitorHeight);
            UpdateConnectionCheckPositionRanges(settings.ConnectionCheckWidth, settings.ConnectionCheckHeight);
            UpdateOperationPositionRanges(settings.OperationButtonSize);
            this.leftXBox.Value = settings.LeftX;
            this.bottomYBox.Value = settings.BottomY;
            this.codexRadarWidthBox.Value = settings.CodexRadarWidth;
            this.codexRadarHeightBox.Value = settings.CodexRadarHeight;
            this.codexRadarLeftXBox.Value = settings.CodexRadarLeftX;
            this.codexRadarBottomYBox.Value = settings.CodexRadarBottomY;
            this.powerThermalWidthBox.Value = settings.PowerThermalWidth;
            this.powerThermalHeightBox.Value = settings.PowerThermalHeight;
            this.powerThermalLeftXBox.Value = settings.PowerThermalLeftX;
            this.powerThermalBottomYBox.Value = settings.PowerThermalBottomY;
            this.powerThermalVisibleAlertCountBox.Value = settings.PowerThermalVisibleAlertCount;
            this.networkMonitorWidthBox.Value = settings.NetworkMonitorWidth;
            this.networkMonitorHeightBox.Value = settings.NetworkMonitorHeight;
            this.networkMonitorLeftXBox.Value = settings.NetworkMonitorLeftX;
            this.networkMonitorBottomYBox.Value = settings.NetworkMonitorBottomY;
            this.gfwProbeIntervalBox.Value = settings.GfwProbeIntervalMinutes;
            this.connectionCheckWidthBox.Value = settings.ConnectionCheckWidth;
            this.connectionCheckHeightBox.Value = settings.ConnectionCheckHeight;
            this.connectionCheckLeftXBox.Value = settings.ConnectionCheckLeftX;
            this.connectionCheckBottomYBox.Value = settings.ConnectionCheckBottomY;
            this.connectionCheckBorderTransparencyBox.Value = settings.ConnectionCheckBorderTransparencyPercent;
            this.connectionCheckIntervalBox.Value = settings.ConnectionCheckIntervalSeconds;
            this.operationButtonSizeBox.Value = settings.OperationButtonSize;
            this.operationLeftOffsetBox.Value = settings.OperationLeftOffset;
            this.operationBottomOffsetBox.Value = settings.OperationBottomOffset;
            this.codexModelIqTestPassedBox.Value = settings.CodexModelIqTestPassed;
            this.codexModelIqBaselineBox.Value = settings.CodexModelIqBaselinePassed;
            this.codexModelTokenEfficiencyTestBox.Value = settings.CodexModelTokenEfficiencyTestPercent;
            this.codexModelTimeEfficiencyTestBox.Value = settings.CodexModelTimeEfficiencyTestPercent;
            this.codexModelTokenEfficiencyBaselinePassedBox.Value = settings.CodexModelTokenEfficiencyBaselinePassed;
            this.codexModelTokenEfficiencyBaselineTokensBox.Value = settings.CodexModelTokenEfficiencyBaselineTokens;
            this.codexModelTimeEfficiencyBaselinePassedBox.Value = settings.CodexModelTimeEfficiencyBaselinePassed;
            this.codexModelTimeEfficiencyBaselineSecondsBox.Value = settings.CodexModelTimeEfficiencyBaselineSeconds;
            this.codexModelTokenEfficiencyLowThresholdBox.Value = settings.CodexModelTokenEfficiencyLowThresholdPercent;
            this.codexModelTimeEfficiencyLowThresholdBox.Value = settings.CodexModelTimeEfficiencyLowThresholdPercent;
            this.widthSlider.Value = settings.Width;
            this.heightSlider.Value = settings.Height;
            this.leftXSlider.Value = settings.LeftX;
            this.bottomYSlider.Value = settings.BottomY;
            this.codexRadarWidthSlider.Value = settings.CodexRadarWidth;
            this.codexRadarHeightSlider.Value = settings.CodexRadarHeight;
            this.codexRadarLeftXSlider.Value = settings.CodexRadarLeftX;
            this.codexRadarBottomYSlider.Value = settings.CodexRadarBottomY;
            this.powerThermalWidthSlider.Value = settings.PowerThermalWidth;
            this.powerThermalHeightSlider.Value = settings.PowerThermalHeight;
            this.powerThermalLeftXSlider.Value = settings.PowerThermalLeftX;
            this.powerThermalBottomYSlider.Value = settings.PowerThermalBottomY;
            this.powerThermalVisibleAlertCountSlider.Value = settings.PowerThermalVisibleAlertCount;
            this.networkMonitorWidthSlider.Value = settings.NetworkMonitorWidth;
            this.networkMonitorHeightSlider.Value = settings.NetworkMonitorHeight;
            this.networkMonitorLeftXSlider.Value = settings.NetworkMonitorLeftX;
            this.networkMonitorBottomYSlider.Value = settings.NetworkMonitorBottomY;
            this.gfwProbeIntervalSlider.Value = settings.GfwProbeIntervalMinutes;
            this.connectionCheckWidthSlider.Value = settings.ConnectionCheckWidth;
            this.connectionCheckHeightSlider.Value = settings.ConnectionCheckHeight;
            this.connectionCheckLeftXSlider.Value = settings.ConnectionCheckLeftX;
            this.connectionCheckBottomYSlider.Value = settings.ConnectionCheckBottomY;
            this.connectionCheckBorderTransparencySlider.Value = settings.ConnectionCheckBorderTransparencyPercent;
            this.connectionCheckIntervalSlider.Value = settings.ConnectionCheckIntervalSeconds;
            this.operationButtonSizeSlider.Value = settings.OperationButtonSize;
            this.operationLeftOffsetSlider.Value = settings.OperationLeftOffset;
            this.operationBottomOffsetSlider.Value = settings.OperationBottomOffset;
            this.codexModelIqTestPassedSlider.Value = settings.CodexModelIqTestPassed;
            this.codexModelIqBaselineSlider.Value = settings.CodexModelIqBaselinePassed;
            this.codexModelTokenEfficiencyTestSlider.Value = settings.CodexModelTokenEfficiencyTestPercent;
            this.codexModelTimeEfficiencyTestSlider.Value = settings.CodexModelTimeEfficiencyTestPercent;
            this.codexModelTokenEfficiencyLowThresholdSlider.Value = settings.CodexModelTokenEfficiencyLowThresholdPercent;
            this.codexModelTimeEfficiencyLowThresholdSlider.Value = settings.CodexModelTimeEfficiencyLowThresholdPercent;
            this.backgroundTransparencyBox.Value = settings.BackgroundTransparencyPercent;
            this.backgroundTransparencySlider.Value = settings.BackgroundTransparencyPercent;
            this.applicationTransparencyBox.Value = settings.ApplicationTransparencyPercent;
            this.applicationTransparencySlider.Value = settings.ApplicationTransparencyPercent;
            this.codexRadarTransparencyBox.Value = settings.CodexRadarTransparencyPercent;
            this.codexRadarTransparencySlider.Value = settings.CodexRadarTransparencyPercent;
            this.powerThermalTransparencyBox.Value = settings.PowerThermalTransparencyPercent;
            this.powerThermalTransparencySlider.Value = settings.PowerThermalTransparencyPercent;
            this.networkMonitorTransparencyBox.Value = settings.NetworkMonitorTransparencyPercent;
            this.networkMonitorTransparencySlider.Value = settings.NetworkMonitorTransparencyPercent;
            this.connectionCheckTransparencyBox.Value = settings.ConnectionCheckTransparencyPercent;
            this.connectionCheckTransparencySlider.Value = settings.ConnectionCheckTransparencyPercent;
            this.operationTransparencyBox.Value = settings.OperationBackgroundTransparencyPercent;
            this.operationTransparencySlider.Value = settings.OperationBackgroundTransparencyPercent;
            SelectComboValue(this.visibilityCombo, settings.VisibilityMode);
            SelectComboValue(this.performanceModeCombo, settings.PerformanceMode);
            SelectComboValue(this.clickThroughCombo, settings.ClickThroughMode);
            this.startupCheck.Checked = settings.StartupEnabled;
            this.hoverOpacityCheck.Checked = settings.HoverOpacityEnabled;
            this.powerThermalAutoSizeCheck.Checked = settings.PowerThermalAutoSizeEnabled;
            SelectComboValue(this.powerThermalAutoDirectionCombo, settings.PowerThermalAutoDirection);
            this.codexModelIqTestCheck.Checked = settings.CodexModelIqTestEnabled;
            this.codexModelEfficiencyTestCheck.Checked = settings.CodexModelEfficiencyTestEnabled;
            this.gfwProbeCheck.Checked = settings.GfwProbeEnabled;
            this.gfwProbeManualRefreshToken = settings.GfwProbeManualRefreshToken;
            this.connectionCheckManualRefreshToken = settings.ConnectionCheckManualRefreshToken;
            SelectComboValue(this.thermalTestCombo, settings.ThermalTestMode);
            SetAlertTestButtonState(settings.AlertTestEnabled);
            SetCodexRadarTestButtonMode(settings.CodexRadarTestMode);
            SetServiceHealthTestButtonMode(settings.ServiceHealthTestMode);
            SetCleanIpBadgeTestButtonMode(settings.CleanIpBadgeTestMode);
            SetNetworkStatusTestButtonMode(settings.NetworkStatusTestMode);
            LoadMetricLayout(settings);
            UpdatePowerThermalAutoControls();
            UpdateGfwProbeControls();
        }
        finally
        {
            this.initializing = false;
        }
    }

    private void OnSettingChanged(object sender, EventArgs e)
    {
        if (this.initializing)
        {
            return;
        }

        this.saved = false;
        UpdatePositionRanges((int)this.widthBox.Value, (int)this.heightBox.Value);
        UpdateCodexRadarPositionRanges((int)this.codexRadarWidthBox.Value, (int)this.codexRadarHeightBox.Value);
        UpdatePowerThermalPositionRanges((int)this.powerThermalWidthBox.Value, (int)this.powerThermalHeightBox.Value);
        UpdateNetworkMonitorPositionRanges((int)this.networkMonitorWidthBox.Value, (int)this.networkMonitorHeightBox.Value);
        UpdateConnectionCheckPositionRanges((int)this.connectionCheckWidthBox.Value, (int)this.connectionCheckHeightBox.Value);
        UpdateOperationPositionRanges((int)this.operationButtonSizeBox.Value);
        UpdatePowerThermalAutoControls();
        UpdateGfwProbeControls();
        QueuePreviewSettings();
    }

    private void QueuePreviewSettings()
    {
        // Coalesce paired NumericUpDown/TrackBar events while preserving responsive live preview.
        this.previewTimer.Stop();
        this.previewTimer.Start();
    }

    private void OnPreviewTimerTick(object sender, EventArgs e)
    {
        this.previewTimer.Stop();
        if (!this.IsDisposed && !this.OwnerFormClosing)
        {
            this.owner.PreviewSettings(ReadControls());
        }
    }

    private void UpdatePowerThermalAutoControls()
    {
        bool enabled = this.powerThermalAutoSizeCheck != null && this.powerThermalAutoSizeCheck.Checked;
        if (this.powerThermalAutoDirectionCombo != null)
        {
            this.powerThermalAutoDirectionCombo.Enabled = enabled;
        }

        if (this.powerThermalVisibleAlertCountBox != null)
        {
            this.powerThermalVisibleAlertCountBox.Enabled = enabled;
        }

        if (this.powerThermalVisibleAlertCountSlider != null)
        {
            this.powerThermalVisibleAlertCountSlider.Enabled = enabled;
        }
    }

    private void UpdateGfwProbeControls()
    {
        bool enabled = this.gfwProbeCheck != null && this.gfwProbeCheck.Checked;
        if (this.gfwProbeIntervalBox != null)
        {
            this.gfwProbeIntervalBox.Enabled = enabled;
        }

        if (this.gfwProbeIntervalSlider != null)
        {
            this.gfwProbeIntervalSlider.Enabled = enabled;
        }

        if (this.gfwProbeTestButton != null)
        {
            this.gfwProbeTestButton.Enabled = true;
        }
    }

    private void UpdatePositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.leftXBox, leftMin, leftMax);
            SetTrackRange(this.leftXSlider, leftMin, leftMax);
            SetNumericRange(this.bottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.bottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdateCodexRadarPositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.codexRadarLeftXBox, leftMin, leftMax);
            SetTrackRange(this.codexRadarLeftXSlider, leftMin, leftMax);
            SetNumericRange(this.codexRadarBottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.codexRadarBottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdatePowerThermalPositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.powerThermalLeftXBox, leftMin, leftMax);
            SetTrackRange(this.powerThermalLeftXSlider, leftMin, leftMax);
            SetNumericRange(this.powerThermalBottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.powerThermalBottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdateNetworkMonitorPositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.networkMonitorLeftXBox, leftMin, leftMax);
            SetTrackRange(this.networkMonitorLeftXSlider, leftMin, leftMax);
            SetNumericRange(this.networkMonitorBottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.networkMonitorBottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdateConnectionCheckPositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.connectionCheckLeftXBox, leftMin, leftMax);
            SetTrackRange(this.connectionCheckLeftXSlider, leftMin, leftMax);
            SetNumericRange(this.connectionCheckBottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.connectionCheckBottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdateOperationPositionRanges(int buttonSize)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            using (Graphics g = this.CreateGraphics())
            {
                float scale = Math.Max(1.0f, g.DpiX / 96.0f);
                Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                int maxLeftOffset = Math.Max(0, workArea.Width - WidgetSettings.GetOperationWindowWidth(buttonSize, scale));
                int maxBottomOffset = Math.Max(0, workArea.Height - WidgetSettings.GetOperationWindowHeight(buttonSize, scale));

                SetNumericRange(this.operationLeftOffsetBox, WidgetSettings.MinOperationOffset, Math.Min(WidgetSettings.MaxOperationOffset, maxLeftOffset));
                SetTrackRange(this.operationLeftOffsetSlider, WidgetSettings.MinOperationOffset, Math.Min(WidgetSettings.MaxOperationOffset, maxLeftOffset));
                SetNumericRange(this.operationBottomOffsetBox, WidgetSettings.MinOperationOffset, Math.Min(WidgetSettings.MaxOperationOffset, maxBottomOffset));
                SetTrackRange(this.operationBottomOffsetSlider, WidgetSettings.MinOperationOffset, Math.Min(WidgetSettings.MaxOperationOffset, maxBottomOffset));
            }
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private static void SetNumericRange(NumericUpDown box, int min, int max)
    {
        if (box.Value < min)
        {
            box.Value = min;
        }
        else if (box.Value > max)
        {
            box.Value = max;
        }

        box.Minimum = min;
        box.Maximum = max;
    }

    private static void SetTrackRange(TrackBar slider, int min, int max)
    {
        if (slider.Value < min)
        {
            slider.Value = min;
        }
        else if (slider.Value > max)
        {
            slider.Value = max;
        }

        slider.Minimum = min;
        slider.Maximum = max;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
    }

    private WidgetSettings ReadControls()
    {
        WidgetSettings settings = this.baseline.Clone();
        settings.Width = (int)this.widthBox.Value;
        settings.Height = (int)this.heightBox.Value;
        settings.LeftX = (int)this.leftXBox.Value;
        settings.BottomY = (int)this.bottomYBox.Value;
        settings.BackgroundTransparencyPercent = (int)this.backgroundTransparencyBox.Value;
        settings.ApplicationTransparencyPercent = (int)this.applicationTransparencyBox.Value;
        settings.CodexRadarWidth = (int)this.codexRadarWidthBox.Value;
        settings.CodexRadarHeight = (int)this.codexRadarHeightBox.Value;
        settings.CodexRadarLeftX = (int)this.codexRadarLeftXBox.Value;
        settings.CodexRadarBottomY = (int)this.codexRadarBottomYBox.Value;
        settings.CodexRadarTransparencyPercent = (int)this.codexRadarTransparencyBox.Value;
        settings.PowerThermalWidth = (int)this.powerThermalWidthBox.Value;
        settings.PowerThermalHeight = (int)this.powerThermalHeightBox.Value;
        settings.PowerThermalLeftX = (int)this.powerThermalLeftXBox.Value;
        settings.PowerThermalBottomY = (int)this.powerThermalBottomYBox.Value;
        settings.PowerThermalTransparencyPercent = (int)this.powerThermalTransparencyBox.Value;
        settings.PowerThermalAutoSizeEnabled = this.powerThermalAutoSizeCheck.Checked;
        settings.PowerThermalAutoDirection = (PowerThermalAutoDirection)GetComboValue(this.powerThermalAutoDirectionCombo, PowerThermalAutoDirection.Left);
        settings.PowerThermalVisibleAlertCount = (int)this.powerThermalVisibleAlertCountBox.Value;
        settings.NetworkMonitorWidth = (int)this.networkMonitorWidthBox.Value;
        settings.NetworkMonitorHeight = (int)this.networkMonitorHeightBox.Value;
        settings.NetworkMonitorLeftX = (int)this.networkMonitorLeftXBox.Value;
        settings.NetworkMonitorBottomY = (int)this.networkMonitorBottomYBox.Value;
        settings.NetworkMonitorTransparencyPercent = (int)this.networkMonitorTransparencyBox.Value;
        settings.NetworkStatusTestMode = GetNetworkStatusTestButtonMode();
        settings.GfwProbeEnabled = this.gfwProbeCheck.Checked;
        settings.GfwProbeIntervalMinutes = (int)this.gfwProbeIntervalBox.Value;
        settings.GfwProbeManualRefreshToken = this.gfwProbeManualRefreshToken;
        settings.ConnectionCheckWidth = (int)this.connectionCheckWidthBox.Value;
        settings.ConnectionCheckHeight = (int)this.connectionCheckHeightBox.Value;
        settings.ConnectionCheckLeftX = (int)this.connectionCheckLeftXBox.Value;
        settings.ConnectionCheckBottomY = (int)this.connectionCheckBottomYBox.Value;
        settings.ConnectionCheckTransparencyPercent = (int)this.connectionCheckTransparencyBox.Value;
        settings.ConnectionCheckBorderTransparencyPercent = (int)this.connectionCheckBorderTransparencyBox.Value;
        settings.ConnectionCheckIntervalSeconds = (int)this.connectionCheckIntervalBox.Value;
        settings.ConnectionCheckManualRefreshToken = this.connectionCheckManualRefreshToken;
        settings.OperationButtonSize = (int)this.operationButtonSizeBox.Value;
        settings.OperationLeftOffset = (int)this.operationLeftOffsetBox.Value;
        settings.OperationBottomOffset = (int)this.operationBottomOffsetBox.Value;
        settings.OperationBackgroundTransparencyPercent = (int)this.operationTransparencyBox.Value;
        settings.ThermalTestMode = (ThermalTestMode)GetComboValue(this.thermalTestCombo, ThermalTestMode.Off);
        settings.CodexRadarTestMode = GetCodexRadarTestButtonMode();
        settings.ServiceHealthTestMode = GetServiceHealthTestButtonMode();
        settings.CleanIpBadgeTestMode = GetCleanIpBadgeTestButtonMode();
        settings.CodexModelIqTestEnabled = this.codexModelIqTestCheck.Checked;
        settings.CodexModelIqTestPassed = (int)this.codexModelIqTestPassedBox.Value;
        settings.CodexModelIqBaselinePassed = (int)this.codexModelIqBaselineBox.Value;
        settings.CodexModelEfficiencyTestEnabled = this.codexModelEfficiencyTestCheck.Checked;
        settings.CodexModelTokenEfficiencyTestPercent = (int)this.codexModelTokenEfficiencyTestBox.Value;
        settings.CodexModelTimeEfficiencyTestPercent = (int)this.codexModelTimeEfficiencyTestBox.Value;
        settings.CodexModelTokenEfficiencyBaselinePassed = (int)this.codexModelTokenEfficiencyBaselinePassedBox.Value;
        settings.CodexModelTokenEfficiencyBaselineTokens = (int)this.codexModelTokenEfficiencyBaselineTokensBox.Value;
        settings.CodexModelTimeEfficiencyBaselinePassed = (int)this.codexModelTimeEfficiencyBaselinePassedBox.Value;
        settings.CodexModelTimeEfficiencyBaselineSeconds = (int)this.codexModelTimeEfficiencyBaselineSecondsBox.Value;
        settings.CodexModelTokenEfficiencyLowThresholdPercent = (int)this.codexModelTokenEfficiencyLowThresholdBox.Value;
        settings.CodexModelTimeEfficiencyLowThresholdPercent = (int)this.codexModelTimeEfficiencyLowThresholdBox.Value;
        settings.VisibilityMode = (WidgetVisibilityMode)GetComboValue(this.visibilityCombo, WidgetVisibilityMode.DesktopOnly);
        settings.StartupEnabled = this.startupCheck.Checked;
        settings.PerformanceMode = (WidgetPerformanceMode)GetComboValue(this.performanceModeCombo, WidgetPerformanceMode.Balanced);
        settings.ClickThroughMode = (ClickThroughMode)GetComboValue(this.clickThroughCombo, ClickThroughMode.Auto);
        settings.HoverOpacityEnabled = this.hoverOpacityCheck.Checked;
        string[] selectedMetrics = ReadMetricSlots(false);
        settings.ShowCpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricCpu);
        settings.ShowMemory = ContainsMetricId(selectedMetrics, WidgetSettings.MetricMemory);
        settings.ShowDisk = ContainsMetricId(selectedMetrics, WidgetSettings.MetricDisk);
        settings.ShowNetwork = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNetwork);
        settings.ShowGpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricGpu);
        settings.ShowNpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNpu);
        settings.AlertTestEnabled = GetAlertTestButtonState();
        settings.MetricOrder = selectedMetrics;
        settings.Normalize();
        return settings;
    }

    private void LoadMetricLayout(WidgetSettings settings)
    {
        if (this.metricSlotPanels == null)
        {
            return;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            SetSlotMetric(i, string.Empty);
        }

        string[] order = settings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        int slotIndex = 0;
        for (int i = 0; i < order.Length && slotIndex < this.metricSlotPanels.Length; i++)
        {
            string metricId = order[i];
            if (!IsMetricShown(settings, metricId))
            {
                continue;
            }

            SetSlotMetric(slotIndex, metricId);
            slotIndex++;
        }

        RefreshMetricLayoutEditor();
    }

    private static bool IsMetricShown(WidgetSettings settings, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowCpu;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowMemory;
        }

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowDisk;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowNetwork;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowGpu;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowNpu;
        }

        return false;
    }

    private void RefreshMetricLayoutEditor()
    {
        if (this.metricSlotPanels != null)
        {
            for (int i = 0; i < this.metricSlotPanels.Length; i++)
            {
                RefreshMetricSlot(i);
            }
        }

        if (this.availableMetricsPanel == null)
        {
            return;
        }

        this.availableMetricsPanel.Controls.Clear();
        for (int i = 0; i < WidgetSettings.DefaultMetricOrder.Length; i++)
        {
            string metricId = WidgetSettings.DefaultMetricOrder[i];
            if (!IsMetricInAnySlot(metricId))
            {
                this.availableMetricsPanel.Controls.Add(BuildMetricChip(metricId));
            }
        }
    }

    private void RefreshMetricSlot(int index)
    {
        Panel slot = this.metricSlotPanels[index];
        string metricId = GetSlotMetric(index);
        Label content = FindChildLabel(slot, "slotContent");
        Button remove = FindChildButton(slot, "slotRemove");
        if (content != null)
        {
            content.Text = metricId.Length > 0 ? WidgetSettings.MetricDisplayName(metricId) : string.Empty;
        }

        if (remove != null)
        {
            remove.Visible = metricId.Length > 0;
        }

        slot.BackColor = metricId.Length > 0 ? DesignTokens.Colors.ControlActive : DesignTokens.Colors.Control;
    }

    private static Label FindChildLabel(Control parent, string name)
    {
        Control[] found = parent.Controls.Find(name, false);
        return found.Length > 0 ? found[0] as Label : null;
    }

    private static Button FindChildButton(Control parent, string name)
    {
        Control[] found = parent.Controls.Find(name, false);
        return found.Length > 0 ? found[0] as Button : null;
    }

    private string GetSlotMetric(int index)
    {
        if (this.metricSlotPanels == null || index < 0 || index >= this.metricSlotPanels.Length)
        {
            return string.Empty;
        }

        return this.metricSlotPanels[index].Tag as string ?? string.Empty;
    }

    private void SetSlotMetric(int index, string metricId)
    {
        if (this.metricSlotPanels == null || index < 0 || index >= this.metricSlotPanels.Length)
        {
            return;
        }

        this.metricSlotPanels[index].Tag = metricId ?? string.Empty;
    }

    private bool IsMetricInAnySlot(string metricId)
    {
        if (this.metricSlotPanels == null)
        {
            return false;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            if (string.Equals(GetSlotMetric(i), metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int FindMetricSlot(string metricId)
    {
        if (this.metricSlotPanels == null)
        {
            return -1;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            if (string.Equals(GetSlotMetric(i), metricId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private string[] ReadMetricSlots(bool includeEmpty)
    {
        List<string> selected = new List<string>();
        if (this.metricSlotPanels != null)
        {
            for (int i = 0; i < this.metricSlotPanels.Length; i++)
            {
                string metricId = GetSlotMetric(i);
                if (metricId.Length > 0 || includeEmpty)
                {
                    selected.Add(metricId);
                }
            }
        }

        return selected.ToArray();
    }

    private static bool ContainsMetricId(string[] metrics, string metricId)
    {
        for (int i = 0; i < metrics.Length; i++)
        {
            if (string.Equals(metrics[i], metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void BeginMetricDrag(string metricId, int sourceSlotIndex, Control source)
    {
        if (string.IsNullOrEmpty(metricId))
        {
            return;
        }

        this.draggedMetricId = metricId;
        this.draggedSourceSlotIndex = sourceSlotIndex;
        source.DoDragDrop(metricId, DragDropEffects.Move);
        this.draggedMetricId = null;
        this.draggedSourceSlotIndex = -1;
    }

    private void OnMetricDragEnter(object sender, DragEventArgs e)
    {
        e.Effect = string.IsNullOrEmpty(this.draggedMetricId) ? DragDropEffects.None : DragDropEffects.Move;
    }

    private void DropMetricToSlot(int targetSlotIndex)
    {
        if (string.IsNullOrEmpty(this.draggedMetricId))
        {
            return;
        }

        int existingSlot = FindMetricSlot(this.draggedMetricId);
        if (existingSlot >= 0 && existingSlot != this.draggedSourceSlotIndex)
        {
            SetSlotMetric(existingSlot, string.Empty);
        }

        string targetMetric = GetSlotMetric(targetSlotIndex);
        if (this.draggedSourceSlotIndex >= 0 && this.draggedSourceSlotIndex != targetSlotIndex)
        {
            SetSlotMetric(this.draggedSourceSlotIndex, targetMetric);
        }

        SetSlotMetric(targetSlotIndex, this.draggedMetricId);
        RefreshMetricLayoutEditor();
        this.saved = false;
        this.owner.PreviewSettings(ReadControls());
    }

    private bool GetAlertTestButtonState()
    {
        return this.alertTestButton != null &&
            this.alertTestButton.Tag is bool &&
            (bool)this.alertTestButton.Tag;
    }

    private void SetAlertTestButtonState(bool enabled)
    {
        if (this.alertTestButton == null)
        {
            return;
        }

        this.alertTestButton.Tag = enabled;
        this.alertTestButton.Text = enabled ? "开启" : "关闭";
        this.alertTestButton.BackColor = enabled ? DesignTokens.Colors.Danger : DesignTokens.Colors.Control;
        this.alertTestButton.ForeColor = enabled ? DesignTokens.Colors.TextOnDanger : DesignTokens.Colors.Text;
        this.alertTestButton.FlatAppearance.BorderColor = enabled ? DesignTokens.Colors.DangerBorder : DesignTokens.Colors.Border;
    }

    private CodexRadarTestMode GetCodexRadarTestButtonMode()
    {
        return this.codexRadarTestButton != null &&
            this.codexRadarTestButton.Tag is CodexRadarTestMode
                ? (CodexRadarTestMode)this.codexRadarTestButton.Tag
                : CodexRadarTestMode.Off;
    }

    private void SetCodexRadarTestButtonMode(CodexRadarTestMode mode)
    {
        if (this.codexRadarTestButton == null)
        {
            return;
        }

        this.codexRadarTestButton.Tag = mode;
        this.codexRadarTestButton.Text = GetCodexRadarTestButtonText(mode);
        if (mode == CodexRadarTestMode.Open)
        {
            this.codexRadarTestButton.BackColor = DesignTokens.Colors.WarningSoft;
            this.codexRadarTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.codexRadarTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Warning;
            return;
        }

        if (mode == CodexRadarTestMode.Closed)
        {
            this.codexRadarTestButton.BackColor = DesignTokens.Colors.Danger;
            this.codexRadarTestButton.ForeColor = DesignTokens.Colors.TextOnDanger;
            this.codexRadarTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.DangerBorder;
            return;
        }

        if (mode == CodexRadarTestMode.None)
        {
            this.codexRadarTestButton.BackColor = DesignTokens.Colors.ControlActive;
            this.codexRadarTestButton.ForeColor = DesignTokens.Colors.Text;
            this.codexRadarTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Badge;
            return;
        }

        this.codexRadarTestButton.BackColor = DesignTokens.Colors.Control;
        this.codexRadarTestButton.ForeColor = DesignTokens.Colors.Text;
        this.codexRadarTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
    }

    private static CodexRadarTestMode GetNextCodexRadarTestMode(CodexRadarTestMode mode)
    {
        if (mode == CodexRadarTestMode.Off)
        {
            return CodexRadarTestMode.None;
        }

        if (mode == CodexRadarTestMode.None)
        {
            return CodexRadarTestMode.Open;
        }

        if (mode == CodexRadarTestMode.Open)
        {
            return CodexRadarTestMode.Closed;
        }

        return CodexRadarTestMode.Off;
    }

    private static string GetCodexRadarTestButtonText(CodexRadarTestMode mode)
    {
        if (mode == CodexRadarTestMode.None)
        {
            return "None";
        }

        if (mode == CodexRadarTestMode.Open)
        {
            return "Open";
        }

        if (mode == CodexRadarTestMode.Closed)
        {
            return "Closed";
        }

        return "实时";
    }

    private ServiceHealthTestMode GetServiceHealthTestButtonMode()
    {
        return this.serviceHealthTestButton != null &&
            this.serviceHealthTestButton.Tag is ServiceHealthTestMode
                ? (ServiceHealthTestMode)this.serviceHealthTestButton.Tag
                : ServiceHealthTestMode.Off;
    }

    private void SetServiceHealthTestButtonMode(ServiceHealthTestMode mode)
    {
        if (this.serviceHealthTestButton == null)
        {
            return;
        }

        this.serviceHealthTestButton.Tag = mode;
        this.serviceHealthTestButton.Text = GetServiceHealthTestButtonText(mode);
        if (mode == ServiceHealthTestMode.Normal)
        {
            this.serviceHealthTestButton.BackColor = DesignTokens.Colors.SuccessSoft;
            this.serviceHealthTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.serviceHealthTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.QuotaGood;
            return;
        }

        if (mode == ServiceHealthTestMode.Offline)
        {
            this.serviceHealthTestButton.BackColor = DesignTokens.Colors.ControlActive;
            this.serviceHealthTestButton.ForeColor = DesignTokens.Colors.Text;
            this.serviceHealthTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Badge;
            return;
        }

        if (mode == ServiceHealthTestMode.Unavailable)
        {
            this.serviceHealthTestButton.BackColor = DesignTokens.Colors.WarningSoft;
            this.serviceHealthTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.serviceHealthTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Warning;
            return;
        }

        if (mode == ServiceHealthTestMode.Unreachable)
        {
            this.serviceHealthTestButton.BackColor = DesignTokens.Colors.Danger;
            this.serviceHealthTestButton.ForeColor = DesignTokens.Colors.TextOnDanger;
            this.serviceHealthTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.DangerBorder;
            return;
        }

        this.serviceHealthTestButton.BackColor = DesignTokens.Colors.Control;
        this.serviceHealthTestButton.ForeColor = DesignTokens.Colors.Text;
        this.serviceHealthTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
    }

    private static ServiceHealthTestMode GetNextServiceHealthTestMode(ServiceHealthTestMode mode)
    {
        if (mode == ServiceHealthTestMode.Off)
        {
            return ServiceHealthTestMode.Normal;
        }

        if (mode == ServiceHealthTestMode.Normal)
        {
            return ServiceHealthTestMode.Offline;
        }

        if (mode == ServiceHealthTestMode.Offline)
        {
            return ServiceHealthTestMode.Unavailable;
        }

        if (mode == ServiceHealthTestMode.Unavailable)
        {
            return ServiceHealthTestMode.Unreachable;
        }

        return ServiceHealthTestMode.Off;
    }

    private static string GetServiceHealthTestButtonText(ServiceHealthTestMode mode)
    {
        if (mode == ServiceHealthTestMode.Normal)
        {
            return "正常";
        }

        if (mode == ServiceHealthTestMode.Offline)
        {
            return "断网";
        }

        if (mode == ServiceHealthTestMode.Unavailable)
        {
            return "服务不可用";
        }

        if (mode == ServiceHealthTestMode.Unreachable)
        {
            return "无法连接";
        }

        return "实时";
    }

    private NetworkStatusTestMode GetNetworkStatusTestButtonMode()
    {
        return this.networkStatusTestButton != null &&
            this.networkStatusTestButton.Tag is NetworkStatusTestMode
                ? (NetworkStatusTestMode)this.networkStatusTestButton.Tag
                : NetworkStatusTestMode.Off;
    }

    private void SetNetworkStatusTestButtonMode(NetworkStatusTestMode mode)
    {
        if (this.networkStatusTestButton == null)
        {
            return;
        }

        this.networkStatusTestButton.Tag = mode;
        this.networkStatusTestButton.Text = GetNetworkStatusTestButtonText(mode);
        if (mode == NetworkStatusTestMode.Online)
        {
            this.networkStatusTestButton.BackColor = DesignTokens.Colors.SuccessSoft;
            this.networkStatusTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.networkStatusTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.QuotaGood;
            return;
        }

        if (mode == NetworkStatusTestMode.NeedsValidation)
        {
            this.networkStatusTestButton.BackColor = DesignTokens.Colors.WarningSoft;
            this.networkStatusTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.networkStatusTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Warning;
            return;
        }

        if (mode == NetworkStatusTestMode.Offline)
        {
            this.networkStatusTestButton.BackColor = DesignTokens.Colors.Danger;
            this.networkStatusTestButton.ForeColor = DesignTokens.Colors.TextOnDanger;
            this.networkStatusTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.DangerBorder;
            return;
        }

        if (mode == NetworkStatusTestMode.AdapterMissing)
        {
            this.networkStatusTestButton.BackColor = DesignTokens.Colors.DangerMuted;
            this.networkStatusTestButton.ForeColor = DesignTokens.Colors.TextOnDanger;
            this.networkStatusTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.DangerBorder;
            return;
        }

        this.networkStatusTestButton.BackColor = DesignTokens.Colors.Control;
        this.networkStatusTestButton.ForeColor = DesignTokens.Colors.Text;
        this.networkStatusTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
    }

    private static NetworkStatusTestMode GetNextNetworkStatusTestMode(NetworkStatusTestMode mode)
    {
        if (mode == NetworkStatusTestMode.Off)
        {
            return NetworkStatusTestMode.Online;
        }

        if (mode == NetworkStatusTestMode.Online)
        {
            return NetworkStatusTestMode.Offline;
        }

        if (mode == NetworkStatusTestMode.Offline)
        {
            return NetworkStatusTestMode.AdapterMissing;
        }

        if (mode == NetworkStatusTestMode.AdapterMissing)
        {
            return NetworkStatusTestMode.NeedsValidation;
        }

        return NetworkStatusTestMode.Off;
    }

    private static string GetNetworkStatusTestButtonText(NetworkStatusTestMode mode)
    {
        if (mode == NetworkStatusTestMode.Online)
        {
            return "在线";
        }

        if (mode == NetworkStatusTestMode.Offline)
        {
            return "断网";
        }

        if (mode == NetworkStatusTestMode.AdapterMissing)
        {
            return "网卡未识别";
        }

        if (mode == NetworkStatusTestMode.NeedsValidation)
        {
            return "需要验证";
        }

        return "实时";
    }

    private CleanIpBadgeTestMode GetCleanIpBadgeTestButtonMode()
    {
        return this.cleanIpBadgeTestButton != null &&
            this.cleanIpBadgeTestButton.Tag is CleanIpBadgeTestMode
                ? (CleanIpBadgeTestMode)this.cleanIpBadgeTestButton.Tag
                : CleanIpBadgeTestMode.Off;
    }

    private void SetCleanIpBadgeTestButtonMode(CleanIpBadgeTestMode mode)
    {
        if (this.cleanIpBadgeTestButton == null)
        {
            return;
        }

        this.cleanIpBadgeTestButton.Tag = mode;
        this.cleanIpBadgeTestButton.Text = GetCleanIpBadgeTestButtonText(mode);
        if (mode == CleanIpBadgeTestMode.NativeResidential)
        {
            this.cleanIpBadgeTestButton.BackColor = DesignTokens.Colors.SuccessSoft;
            this.cleanIpBadgeTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.cleanIpBadgeTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.QuotaGood;
            return;
        }

        if (mode == CleanIpBadgeTestMode.BroadcastBusiness || mode == CleanIpBadgeTestMode.UnannouncedIdc)
        {
            this.cleanIpBadgeTestButton.BackColor = DesignTokens.Colors.WarningSoft;
            this.cleanIpBadgeTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.cleanIpBadgeTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Warning;
            return;
        }

        if (mode == CleanIpBadgeTestMode.ProxyRisk ||
            mode == CleanIpBadgeTestMode.ErrorHttp403 ||
            mode == CleanIpBadgeTestMode.ErrorHttp429 ||
            mode == CleanIpBadgeTestMode.ErrorTimeout ||
            mode == CleanIpBadgeTestMode.ErrorDns ||
            mode == CleanIpBadgeTestMode.ErrorConnect)
        {
            this.cleanIpBadgeTestButton.BackColor = DesignTokens.Colors.Danger;
            this.cleanIpBadgeTestButton.ForeColor = DesignTokens.Colors.TextOnDanger;
            this.cleanIpBadgeTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.DangerBorder;
            return;
        }

        this.cleanIpBadgeTestButton.BackColor = DesignTokens.Colors.Control;
        this.cleanIpBadgeTestButton.ForeColor = DesignTokens.Colors.Text;
        this.cleanIpBadgeTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
    }

    private static CleanIpBadgeTestMode GetNextCleanIpBadgeTestMode(CleanIpBadgeTestMode mode)
    {
        if (mode == CleanIpBadgeTestMode.Off)
        {
            return CleanIpBadgeTestMode.NativeResidential;
        }

        if (mode == CleanIpBadgeTestMode.NativeResidential)
        {
            return CleanIpBadgeTestMode.BroadcastBusiness;
        }

        if (mode == CleanIpBadgeTestMode.BroadcastBusiness)
        {
            return CleanIpBadgeTestMode.UnannouncedIdc;
        }

        if (mode == CleanIpBadgeTestMode.UnannouncedIdc)
        {
            return CleanIpBadgeTestMode.ProxyRisk;
        }

        if (mode == CleanIpBadgeTestMode.ProxyRisk)
        {
            return CleanIpBadgeTestMode.ErrorHttp403;
        }

        if (mode == CleanIpBadgeTestMode.ErrorHttp403)
        {
            return CleanIpBadgeTestMode.ErrorHttp429;
        }

        if (mode == CleanIpBadgeTestMode.ErrorHttp429)
        {
            return CleanIpBadgeTestMode.ErrorTimeout;
        }

        if (mode == CleanIpBadgeTestMode.ErrorTimeout)
        {
            return CleanIpBadgeTestMode.ErrorDns;
        }

        if (mode == CleanIpBadgeTestMode.ErrorDns)
        {
            return CleanIpBadgeTestMode.ErrorConnect;
        }

        return CleanIpBadgeTestMode.Off;
    }

    private static string GetCleanIpBadgeTestButtonText(CleanIpBadgeTestMode mode)
    {
        if (mode == CleanIpBadgeTestMode.NativeResidential)
        {
            return "94 A 原生 住宅";
        }

        if (mode == CleanIpBadgeTestMode.BroadcastBusiness)
        {
            return "74 B 广播 商业";
        }

        if (mode == CleanIpBadgeTestMode.UnannouncedIdc)
        {
            return "52 C 未通告 IDC";
        }

        if (mode == CleanIpBadgeTestMode.ProxyRisk)
        {
            return "28 D 代理";
        }

        if (mode == CleanIpBadgeTestMode.ErrorHttp403)
        {
            return "错误 HTTP 403";
        }

        if (mode == CleanIpBadgeTestMode.ErrorHttp429)
        {
            return "错误 HTTP 429";
        }

        if (mode == CleanIpBadgeTestMode.ErrorTimeout)
        {
            return "错误 超时";
        }

        if (mode == CleanIpBadgeTestMode.ErrorDns)
        {
            return "错误 DNS";
        }

        if (mode == CleanIpBadgeTestMode.ErrorConnect)
        {
            return "错误 连接失败";
        }

        return "实时";
    }

    private static void SelectComboValue(ComboBox combo, object value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            ComboOption option = combo.Items[i] as ComboOption;
            if (option != null && object.Equals(option.Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static object GetComboValue(ComboBox combo, object defaultValue)
    {
        ComboOption option = combo.SelectedItem as ComboOption;
        if (option == null)
        {
            return defaultValue;
        }

        return option.Value;
    }

    private sealed class ComboOption
    {
        public ComboOption(string text, object value)
        {
            this.Text = text;
            this.Value = value;
        }

        public string Text { get; private set; }
        public object Value { get; private set; }

        public override string ToString()
        {
            return this.Text;
        }
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? DesignTokens.Colors.Accent : DesignTokens.Colors.Border;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = primary ? DesignTokens.Colors.Accent : DesignTokens.Colors.Control;
        button.ForeColor = primary ? DesignTokens.Colors.TextOnAccent : DesignTokens.Colors.Text;
        button.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        button.UseCompatibleTextRendering = true;
        button.Margin = new Padding(DesignTokens.Spacing.SettingsButtonGap, 0, 0, 0);
    }
}
