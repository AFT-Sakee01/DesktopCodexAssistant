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

internal sealed class SettingsForm : Form, IMessageFilter
{
    private const int PreviewDebounceMs = 75;
    private const int WmMouseWheel = 0x020A;
    private const int EmSetCueBanner = 0x1501;
    private readonly WidgetForm owner;
    private readonly System.Windows.Forms.Timer previewTimer;
    private readonly System.Windows.Forms.Timer footerStatusTimer;
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
    private NumericUpDown autoHoverOpacityIdleSecondsBox;
    private CheckBox forceShowFpsCheck;
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
    private ComboBox networkAdapterCombo;
    private ComboBox codexModelIqBaselineModeCombo;
    private ComboBox codexModelTokenEfficiencyBaselineModeCombo;
    private ComboBox codexModelTimeEfficiencyBaselineModeCombo;
    private TableLayoutPanel codexRadarModelButtonGrid;
    private readonly List<Button> codexRadarModelButtons = new List<Button>();
    private string selectedCodexRadarModelKey;
    private ComboBox displayTimeZoneModeCombo;
    private ComboBox displayTimeZoneCombo;
    private Label displayTimeZoneOffsetLabel;
    private Label beijingMidnightLabel;
    private Button alertTestButton;
    private Button codexRadarRandomModeButton;
    private Button codexRadarRandomRefreshButton;
    private CheckBox codexRadarRandomAutoRefreshCheck;
    private Button cleanIpBadgeTestButton;
    private Button connectionCheckManualRefreshButton;
    private Button networkStatusTestButton;
    private Button gfwProbeTestButton;
    private Button cloudEndpointTestButton;
    private int codexRadarRandomTestRefreshToken;
    private CheckBox startupCheck;
    private CheckBox hoverOpacityCheck;
    private CheckBox autoHoverOpacityIdleCheck;
    private CheckBox autoHoverOpacityMaximizedCheck;
    private CheckBox burnInHiddenModeColorProtectionCheck;
    private CheckBox seelenDockForegroundPulseCheck;
    private CheckBox ctrlDRecoveryPulseCheck;
    private CheckBox powerResumeRestartCheck;
    private CheckBox codexModelIqTestCheck;
    private CheckBox codexModelEfficiencyTestCheck;
    private CheckBox gfwProbeCheck;
    private CheckBox cloudRegionJapanCheck;
    private CheckBox cloudRegionAsiaPacificCheck;
    private CheckBox cloudRegionNorthAmericaCheck;
    private CheckBox cloudRegionEuropeCheck;
    private FlowLayoutPanel availableMetricsPanel;
    private TableLayoutPanel metricSlotsPanel;
    private Panel[] metricSlotPanels;
    private Panel settingsContentHost;
    private Button[] settingsNavigationButtons;
    private Control[] settingsPages;
    private TextBox settingsSearchBox;
    private Label footerStatusLabel;
    private int selectedSettingsPageIndex;
    private bool messageFilterRegistered;
    private string draggedMetricId;
    private int draggedSourceSlotIndex;
    private int gfwProbeManualRefreshToken;
    private int connectionCheckManualRefreshToken;
    private int cloudEndpointTestSeed;
    private bool initializing;
    private bool saved;

    private static readonly string[] SettingsNavigationTitles = new string[]
    {
        "运行",
        "主窗口",
        "CodexRadar",
        "时区",
        "功耗模块",
        "网络监控",
        "连接检测",
        "操作模块"
    };

    public bool OwnerFormClosing { get; set; }

    public SettingsForm(WidgetForm owner, WidgetSettings baseline)
    {
        this.owner = owner;
        this.baseline = baseline.Clone();
        this.baseline.Normalize();
        this.previewTimer = new System.Windows.Forms.Timer();
        this.previewTimer.Interval = PreviewDebounceMs;
        this.previewTimer.Tick += OnPreviewTimerTick;
        this.footerStatusTimer = new System.Windows.Forms.Timer();
        this.footerStatusTimer.Interval = 5000;
        this.footerStatusTimer.Tick += OnFooterStatusTimerTick;

        this.Text = "性能小窗设置";
        this.FormBorderStyle = FormBorderStyle.Sizable;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = false;
        this.MaximizeBox = true;
        this.MinimizeBox = false;
        this.AutoScroll = false;
        Size desiredClientSize = GetDesiredClientSize();
        this.AutoScrollMinSize = desiredClientSize;
        this.ClientSize = FitClientSizeToScreen(desiredClientSize);
        this.MinimumSize = GetMinimumWindowSizeForScreen();
        this.Font = DesignTokens.CreateUIFont(10.0f);
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.ForeColor = DesignTokens.Colors.Text;

        BuildControls();
        LoadControls(this.baseline);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!this.messageFilterRegistered)
        {
            Application.AddMessageFilter(this);
            this.messageFilterRegistered = true;
        }
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
        if (this.messageFilterRegistered)
        {
            Application.RemoveMessageFilter(this);
            this.messageFilterRegistered = false;
        }

        this.previewTimer.Tick -= OnPreviewTimerTick;
        this.previewTimer.Dispose();
        this.footerStatusTimer.Stop();
        this.footerStatusTimer.Tick -= OnFooterStatusTimerTick;
        this.footerStatusTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (this.IsHandleCreated)
        {
            UpdateResponsiveLayout();
        }
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel || !this.Visible || this.WindowState == FormWindowState.Minimized)
        {
            return false;
        }

        Point cursor = Cursor.Position;
        if (!this.Bounds.Contains(cursor))
        {
            return false;
        }

        int delta = unchecked((short)((long)m.WParam >> 16));
        SettingsNavigationPanel navigation = GetNavigationPanelAt(cursor);
        if (navigation != null && navigation.ScrollByMouseWheelDelta(delta))
        {
            return true;
        }

        SettingsPagePanel page = GetSelectedSettingsPageAt(cursor);
        if (page != null && page.ScrollByMouseWheelDelta(delta))
        {
            return true;
        }

        return false;
    }

    private void BuildControls()
    {
        InitializeControls();
        PopulateComboOptions();
        WireControlPairs();

        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(18);
        root.BackColor = DesignTokens.Colors.AppBackground;
        root.ColumnCount = 2;
        root.RowCount = 3;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        this.Controls.Add(root);

        Control header = BuildSettingsHeader();
        root.SetColumnSpan(header, 2);
        root.Controls.Add(header, 0, 0);

        Control navigation = BuildSettingsNavigation();
        root.Controls.Add(navigation, 0, 1);

        this.settingsContentHost = new Panel();
        this.settingsContentHost.Dock = DockStyle.Fill;
        this.settingsContentHost.BackColor = DesignTokens.Colors.AppBackground;
        this.settingsContentHost.Margin = new Padding(16, 0, 0, 0);
        root.Controls.Add(this.settingsContentHost, 1, 1);

        this.settingsPages = new Control[]
        {
            BuildRuntimeTab(),
            BuildWidgetTab(),
            BuildCodexRadarTab(),
            BuildTimeZoneTab(),
            BuildPowerTab(),
            BuildNetworkMonitorTab(),
            BuildConnectionCheckTab(),
            BuildOperationTab()
        };

        for (int i = 0; i < this.settingsPages.Length; i++)
        {
            Control page = this.settingsPages[i];
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            this.settingsContentHost.Controls.Add(page);
        }

        SelectSettingsPage(0);

        Control footer = BuildFooterButtons();
        root.SetColumnSpan(footer, 2);
        root.Controls.Add(footer, 0, 2);
    }

    private Control BuildSettingsHeader()
    {
        TableLayoutPanel header = new TableLayoutPanel();
        header.Dock = DockStyle.Fill;
        header.BackColor = DesignTokens.Colors.AppBackground;
        header.ColumnCount = 2;
        header.RowCount = 2;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label title = new Label();
        title.Text = "性能小窗设置";
        title.Font = DesignTokens.CreateUIFont(16.0f, FontStyle.Bold);
        title.ForeColor = DesignTokens.Colors.TextStrong;
        title.BackColor = DesignTokens.Colors.AppBackground;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.UseCompatibleTextRendering = true;

        Label subtitle = new Label();
        subtitle.Text = "左侧选择模块，右侧调整参数，保存前会实时预览。";
        subtitle.Font = DesignTokens.CreateUIFont(9.5f);
        subtitle.ForeColor = DesignTokens.Colors.GlyphMuted;
        subtitle.BackColor = DesignTokens.Colors.AppBackground;
        subtitle.Dock = DockStyle.Fill;
        subtitle.TextAlign = ContentAlignment.TopLeft;
        subtitle.AutoEllipsis = true;
        subtitle.UseCompatibleTextRendering = true;

        this.settingsSearchBox = new TextBox();
        this.settingsSearchBox.Dock = DockStyle.Bottom;
        this.settingsSearchBox.Height = 32;
        this.settingsSearchBox.Margin = new Padding(12, 0, 0, 17);
        this.settingsSearchBox.Font = DesignTokens.CreateUIFont(10.0f);
        this.settingsSearchBox.BackColor = DesignTokens.Colors.Control;
        this.settingsSearchBox.ForeColor = DesignTokens.Colors.Text;
        this.settingsSearchBox.BorderStyle = BorderStyle.FixedSingle;
        this.settingsSearchBox.TextChanged += delegate { FilterSettingsNavigation(); };
        this.settingsSearchBox.HandleCreated += delegate
        {
            SendMessage(this.settingsSearchBox.Handle, EmSetCueBanner, IntPtr.Zero, "搜索设置");
        };

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.SetRowSpan(this.settingsSearchBox, 2);
        header.Controls.Add(this.settingsSearchBox, 1, 0);
        return header;
    }

    private Control BuildSettingsNavigation()
    {
        SettingsNavigationPanel navigation = new SettingsNavigationPanel();
        navigation.Dock = DockStyle.Fill;
        navigation.FlowDirection = FlowDirection.TopDown;
        navigation.WrapContents = false;
        navigation.AutoScroll = true;
        navigation.BackColor = DesignTokens.Colors.Surface;
        navigation.Padding = new Padding(12);
        navigation.Margin = new Padding(0);

        this.settingsNavigationButtons = new Button[SettingsNavigationTitles.Length];
        for (int i = 0; i < SettingsNavigationTitles.Length; i++)
        {
            int pageIndex = i;
            Button button = new Button();
            button.Text = SettingsNavigationTitles[i];
            button.Width = 184;
            button.Height = 42;
            button.Margin = new Padding(0, 0, 0, 8);
            button.Padding = new Padding(14, 0, 10, 1);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Font = DesignTokens.CreateUIFont(10.0f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.UseCompatibleTextRendering = true;
            button.Click += delegate { SelectSettingsPage(pageIndex); };
            this.settingsNavigationButtons[i] = button;
            navigation.Controls.Add(button);
        }

        return navigation;
    }

    private void SelectSettingsPage(int pageIndex)
    {
        if (this.settingsPages == null ||
            this.settingsNavigationButtons == null ||
            pageIndex < 0 ||
            pageIndex >= this.settingsPages.Length)
        {
            return;
        }

        for (int i = 0; i < this.settingsPages.Length; i++)
        {
            this.settingsPages[i].Visible = i == pageIndex;
            StyleNavigationButton(this.settingsNavigationButtons[i], i == pageIndex);
        }

        this.selectedSettingsPageIndex = pageIndex;
        this.settingsPages[pageIndex].BringToFront();
        UpdateResponsiveLayout();
    }

    private static void StyleNavigationButton(Button button, bool active)
    {
        button.BackColor = active ? DesignTokens.Colors.ControlActive : DesignTokens.Colors.Surface;
        button.ForeColor = active ? DesignTokens.Colors.Accent : DesignTokens.Colors.SubtleText;
        button.FlatAppearance.BorderColor = active ? DesignTokens.Colors.AccentDeep : DesignTokens.Colors.Surface;
        button.FlatAppearance.MouseOverBackColor = active ? DesignTokens.Colors.ControlActive : DesignTokens.Colors.Control;
        button.FlatAppearance.MouseDownBackColor = DesignTokens.Colors.ControlPressed;
    }

    private void FilterSettingsNavigation()
    {
        if (this.settingsNavigationButtons == null || this.settingsSearchBox == null)
        {
            return;
        }

        string query = (this.settingsSearchBox.Text ?? string.Empty).Trim();
        bool anyVisible = false;
        for (int i = 0; i < this.settingsNavigationButtons.Length; i++)
        {
            bool visible = query.Length == 0 ||
                SettingsNavigationTitles[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            this.settingsNavigationButtons[i].Visible = visible;
            anyVisible |= visible;
        }

        if (!anyVisible)
        {
            for (int i = 0; i < this.settingsNavigationButtons.Length; i++)
            {
                this.settingsNavigationButtons[i].Visible = true;
            }
        }
    }

    private SettingsPagePanel GetSelectedSettingsPage()
    {
        if (this.settingsPages == null ||
            this.selectedSettingsPageIndex < 0 ||
            this.selectedSettingsPageIndex >= this.settingsPages.Length)
        {
            return null;
        }

        return this.settingsPages[this.selectedSettingsPageIndex] as SettingsPagePanel;
    }

    private SettingsPagePanel GetSelectedSettingsPageAt(Point screenPoint)
    {
        SettingsPagePanel page = GetSelectedSettingsPage();
        if (page == null)
        {
            return null;
        }

        Point local = page.PointToClient(screenPoint);
        return page.ClientRectangle.Contains(local) ? page : null;
    }

    private SettingsNavigationPanel GetNavigationPanelAt(Point screenPoint)
    {
        if (this.settingsNavigationButtons == null || this.settingsNavigationButtons.Length == 0)
        {
            return null;
        }

        Control parent = this.settingsNavigationButtons[0].Parent;
        SettingsNavigationPanel navigation = parent as SettingsNavigationPanel;
        if (navigation == null)
        {
            return null;
        }

        Point local = navigation.PointToClient(screenPoint);
        return navigation.ClientRectangle.Contains(local) ? navigation : null;
    }

    private void UpdateResponsiveLayout()
    {
        if (this.settingsPages == null)
        {
            return;
        }

        for (int i = 0; i < this.settingsPages.Length; i++)
        {
            UpdateResponsiveLayout(this.settingsPages[i]);
        }
    }

    private void UpdateResponsiveLayout(Control root)
    {
        if (root == null)
        {
            return;
        }

        TableLayoutPanel table = root as TableLayoutPanel;
        if (table != null && table.Tag as string == "SettingsSection")
        {
            int available = Math.Max(0, table.ClientSize.Width - table.Padding.Left - table.Padding.Right);
            int labelWidth = available < 560 ? 148 : 214;
            int editorWidth = available < 560 ? 112 : 140;
            if (table.ColumnStyles.Count >= 3)
            {
                table.ColumnStyles[0].Width = labelWidth;
                table.ColumnStyles[1].Width = editorWidth;
            }
        }

        for (int i = 0; i < root.Controls.Count; i++)
        {
            UpdateResponsiveLayout(root.Controls[i]);
        }
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
        this.autoHoverOpacityIdleSecondsBox = BuildNumberBox(
            WidgetSettings.MinAutoHoverOpacityIdleSeconds,
            WidgetSettings.MaxAutoHoverOpacityIdleSeconds);
        this.autoHoverOpacityIdleSecondsBox.Increment = 1;
        this.autoHoverOpacityIdleSecondsBox.Width = 120;
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
        this.networkAdapterCombo = BuildCombo();
        this.codexModelIqBaselineModeCombo = BuildCombo();
        this.codexModelTokenEfficiencyBaselineModeCombo = BuildCombo();
        this.codexModelTimeEfficiencyBaselineModeCombo = BuildCombo();
        this.selectedCodexRadarModelKey = this.baseline.CodexRadarModelKey;
        this.displayTimeZoneModeCombo = BuildCombo();
        this.displayTimeZoneCombo = BuildCombo();
        this.displayTimeZoneOffsetLabel = BuildSettingsInfoLabel();
        this.beijingMidnightLabel = BuildSettingsInfoLabel();
        this.alertTestButton = BuildToggleButton();
        this.codexRadarRandomModeButton = BuildCodexRadarRandomModeButton();
        this.codexRadarRandomRefreshButton = BuildCodexRadarRandomRefreshButton();
        this.codexRadarRandomAutoRefreshCheck = BuildCheckBox("自动刷新");
        this.codexRadarRandomAutoRefreshCheck.CheckedChanged += delegate
        {
            UpdateCodexRadarRandomTestControls();
        };
        this.cleanIpBadgeTestButton = BuildCleanIpBadgeTestButton();
        this.connectionCheckManualRefreshButton = BuildConnectionCheckManualRefreshButton();
        this.networkStatusTestButton = BuildNetworkStatusTestButton();
        this.gfwProbeTestButton = BuildGfwProbeTestButton();
        this.cloudEndpointTestButton = BuildCloudEndpointTestButton();
        this.startupCheck = BuildCheckBox("开机自动启动");
        this.hoverOpacityCheck = BuildCheckBox("悬停透明 95%");
        this.autoHoverOpacityIdleCheck = BuildCheckBox("鼠标空闲后增高透明度");
        this.autoHoverOpacityMaximizedCheck = BuildCheckBox("前台最大化窗口时增高透明度");
        this.burnInHiddenModeColorProtectionCheck = BuildCheckBox("隐藏模式反色防烧屏");
        this.forceShowFpsCheck = BuildCheckBox("强制显示FPS模式");
        this.seelenDockForegroundPulseCheck = BuildCheckBox("Seelen Dock 自动拉前");
        this.ctrlDRecoveryPulseCheck = BuildCheckBox("Ctrl+D 后延迟拉前");
        this.powerResumeRestartCheck = BuildCheckBox("休眠唤醒后重启");
        this.powerThermalAutoSizeCheck = BuildCheckBox("启用自动大小");
        this.codexModelIqTestCheck = BuildCheckBox("覆盖实时 IQ 数据");
        this.codexModelEfficiencyTestCheck = BuildCheckBox("覆盖实时效率数据");
        this.gfwProbeCheck = BuildCheckBox("启用 GFW 检测");
        this.cloudRegionJapanCheck = BuildCheckBox("日本");
        this.cloudRegionAsiaPacificCheck = BuildCheckBox("亚太");
        this.cloudRegionNorthAmericaCheck = BuildCheckBox("北美");
        this.cloudRegionEuropeCheck = BuildCheckBox("欧洲");

        this.metricSlotPanels = new Panel[WidgetSettings.DefaultMetricOrder.Length];
    }

    private void PopulateComboOptions()
    {
        this.visibilityCombo.Items.Add(new ComboOption("仅桌面可见", WidgetVisibilityMode.DesktopOnly));
        this.visibilityCombo.Items.Add(new ComboOption("一直可见", WidgetVisibilityMode.AlwaysVisible));
        this.visibilityCombo.Items.Add(new ComboOption("仅全屏不可见", WidgetVisibilityMode.HideWhenFullscreen));
        this.performanceModeCombo.Items.Add(new ComboOption("根据 Windows 电源模式自动切换", WidgetPerformanceMode.WindowsPowerMode));
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
        PopulateCodexBaselineModeOptions(this.codexModelIqBaselineModeCombo);
        PopulateCodexBaselineModeOptions(this.codexModelTokenEfficiencyBaselineModeCombo);
        PopulateCodexBaselineModeOptions(this.codexModelTimeEfficiencyBaselineModeCombo);
        this.displayTimeZoneModeCombo.Items.Add(new ComboOption("自动使用系统时区", DisplayTimeZoneMode.Automatic));
        this.displayTimeZoneModeCombo.Items.Add(new ComboOption("手动选择时区", DisplayTimeZoneMode.Manual));
        PopulateTimeZoneOptions();
        PopulateNetworkAdapterOptions();
    }

    private static void PopulateCodexBaselineModeOptions(ComboBox combo)
    {
        combo.Items.Add(new ComboOption("绝对值", CodexModelBaselineMode.Absolute));
        combo.Items.Add(new ComboOption("近 7 日平均", CodexModelBaselineMode.Recent7Average));
        combo.Items.Add(new ComboOption("近 30 日平均", CodexModelBaselineMode.Recent30Average));
        combo.Items.Add(new ComboOption("全记录平均", CodexModelBaselineMode.AllRecordsAverage));
    }

    private void PopulateTimeZoneOptions()
    {
        this.displayTimeZoneCombo.Items.Clear();
        try
        {
            foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones())
            {
                string text = FormatUtcOffset(zone.BaseUtcOffset) + "  " + zone.DisplayName;
                this.displayTimeZoneCombo.Items.Add(new ComboOption(text, zone.Id));
            }
        }
        catch
        {
        }

        if (this.displayTimeZoneCombo.Items.Count == 0)
        {
            this.displayTimeZoneCombo.Items.Add(
                new ComboOption(TimeZoneInfo.Local.DisplayName, TimeZoneInfo.Local.Id));
        }
    }

    private void PopulateNetworkAdapterOptions()
    {
        if (this.networkAdapterCombo == null)
        {
            return;
        }

        this.networkAdapterCombo.Items.Clear();
        this.networkAdapterCombo.Items.Add(new ComboOption("自动选择", string.Empty));
        try
        {
            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
            Array.Sort(adapters, CompareNetworkInterfacesByName);
            for (int i = 0; i < adapters.Length; i++)
            {
                NetworkInterface adapter = adapters[i];
                if (adapter == null)
                {
                    continue;
                }

                string id = adapter.Id ?? string.Empty;
                if (id.Length == 0)
                {
                    continue;
                }

                string text = FallbackText(adapter.Name, "Network") + " | " +
                    adapter.OperationalStatus.ToString() + " | " +
                    FormatInterfaceTypeForSettings(adapter.NetworkInterfaceType);
                this.networkAdapterCombo.Items.Add(new ComboOption(text, id));
            }
        }
        catch
        {
        }
    }

    private static string FallbackText(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int CompareNetworkInterfacesByName(NetworkInterface left, NetworkInterface right)
    {
        return string.Compare(
            left == null ? string.Empty : left.Name,
            right == null ? string.Empty : right.Name,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatInterfaceTypeForSettings(NetworkInterfaceType type)
    {
        if (type == NetworkInterfaceType.Wireless80211)
        {
            return "Wi-Fi";
        }

        if (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet)
        {
            return "Ethernet";
        }

        return type.ToString();
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

    private SettingsPagePanel BuildRuntimeTab()
    {
        SettingsPagePanel page = BuildTabPage("运行");
        TableLayoutPanel section = BuildSettingsSection("性能和交互", 10);
        AddEditorRow(section, 1, "性能模式", this.performanceModeCombo);
        AddEditorRow(section, 2, "可见性", this.visibilityCombo);
        AddEditorRow(section, 3, "点击穿透", this.clickThroughCombo);
        AddCheckRow(section, 4, "启动", this.startupCheck);
        AddCheckRow(section, 5, "透明交互", this.hoverOpacityCheck);
        AddCheckRow(section, 6, "防烧屏空闲", this.autoHoverOpacityIdleCheck);
        AddEditorRow(section, 7, "空闲秒数", this.autoHoverOpacityIdleSecondsBox);
        AddCheckRow(section, 8, "最大化窗口", this.autoHoverOpacityMaximizedCheck);
        AddCheckRow(section, 9, "隐藏反色", this.burnInHiddenModeColorProtectionCheck);
        AddLabel(section, 10, "告警测试");
        Control alertEditor = BuildButtonEditor(this.alertTestButton);
        section.SetColumnSpan(alertEditor, 2);
        section.Controls.Add(alertEditor, 1, 10);
        page.Controls.Add(section);
        return page;
    }

    private SettingsPagePanel BuildWidgetTab()
    {
        SettingsPagePanel page = BuildTabPage("主窗口");
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

    private SettingsPagePanel BuildCodexRadarTab()
    {
        SettingsPagePanel page = BuildTabPage("CodexRadar");
        TableLayoutPanel section = BuildSettingsSection("CodexRadar 模块", 21);
        AddSliderRow(section, 1, "模块宽度", this.codexRadarWidthBox, this.codexRadarWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.codexRadarHeightBox, this.codexRadarHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.codexRadarLeftXBox, this.codexRadarLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.codexRadarBottomYBox, this.codexRadarBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.codexRadarTransparencyBox, this.codexRadarTransparencySlider);
        AddLabel(section, 6, "窗口测试");
        Control randomTestEditor = BuildCodexRadarRandomTestEditor();
        section.SetColumnSpan(randomTestEditor, 2);
        section.Controls.Add(randomTestEditor, 1, 6);
        AddCheckRow(section, 7, "IQ测试启用", this.codexModelIqTestCheck);
        AddSliderRow(section, 8, "IQ测试通过数", this.codexModelIqTestPassedBox, this.codexModelIqTestPassedSlider);
        AddEditorRow(section, 9, "IQ基准", this.codexModelIqBaselineModeCombo);
        AddSliderRow(section, 10, "IQ绝对基准", this.codexModelIqBaselineBox, this.codexModelIqBaselineSlider);
        AddEditorRow(section, 11, "Token基准", this.codexModelTokenEfficiencyBaselineModeCombo);
        AddEditorRow(section, 12, "Token基线通过", this.codexModelTokenEfficiencyBaselinePassedBox);
        AddEditorRow(section, 13, "Token基线Token", this.codexModelTokenEfficiencyBaselineTokensBox);
        AddEditorRow(section, 14, "时间基准", this.codexModelTimeEfficiencyBaselineModeCombo);
        AddEditorRow(section, 15, "时间基线通过", this.codexModelTimeEfficiencyBaselinePassedBox);
        AddEditorRow(section, 16, "时间基线秒", this.codexModelTimeEfficiencyBaselineSecondsBox);
        AddSliderRow(section, 17, "Token低效阈值", this.codexModelTokenEfficiencyLowThresholdBox, this.codexModelTokenEfficiencyLowThresholdSlider);
        AddSliderRow(section, 18, "时间低效阈值", this.codexModelTimeEfficiencyLowThresholdBox, this.codexModelTimeEfficiencyLowThresholdSlider);
        AddCheckRow(section, 19, "效率测试启用", this.codexModelEfficiencyTestCheck);
        AddSliderRow(section, 20, "Token效率测试", this.codexModelTokenEfficiencyTestBox, this.codexModelTokenEfficiencyTestSlider);
        AddSliderRow(section, 21, "时间效率测试", this.codexModelTimeEfficiencyTestBox, this.codexModelTimeEfficiencyTestSlider);
        page.Controls.Add(BuildCodexRadarModelSection());
        page.Controls.Add(section);
        return page;
    }

    private Control BuildCodexRadarRandomTestEditor()
    {
        FlowLayoutPanel panel = new FlowLayoutPanel();
        panel.Dock = DockStyle.Fill;
        panel.FlowDirection = FlowDirection.LeftToRight;
        panel.WrapContents = false;
        panel.BackColor = DesignTokens.Colors.Surface;
        panel.Padding = new Padding(0, 8, 0, 0);
        panel.Controls.Add(this.codexRadarRandomModeButton);
        panel.Controls.Add(this.codexRadarRandomRefreshButton);
        panel.Controls.Add(this.codexRadarRandomAutoRefreshCheck);
        return panel;
    }

    private Control BuildCodexRadarModelSection()
    {
        TableLayoutPanel section = BuildSettingsSection("检测模型", 1);
        section.RowStyles[1] = new RowStyle(SizeType.AutoSize);
        this.codexRadarModelButtonGrid = new TableLayoutPanel();
        this.codexRadarModelButtonGrid.Dock = DockStyle.Top;
        this.codexRadarModelButtonGrid.AutoSize = true;
        this.codexRadarModelButtonGrid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        this.codexRadarModelButtonGrid.BackColor = DesignTokens.Colors.Surface;
        this.codexRadarModelButtonGrid.Margin = new Padding(0, 4, 0, 0);
        this.codexRadarModelButtonGrid.ColumnCount = 5;
        for (int i = 0; i < 5; i++)
        {
            this.codexRadarModelButtonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0f));
        }

        RebuildCodexRadarModelButtons();
        section.SetColumnSpan(this.codexRadarModelButtonGrid, 2);
        section.Controls.Add(this.codexRadarModelButtonGrid, 1, 1);
        return section;
    }

    private SettingsPagePanel BuildTimeZoneTab()
    {
        SettingsPagePanel page = BuildTabPage("时区");
        TableLayoutPanel section = BuildSettingsSection("显示时区", 4);
        AddEditorRow(section, 1, "时区模式", this.displayTimeZoneModeCombo);
        AddEditorRow(section, 2, "手动时区", this.displayTimeZoneCombo);
        AddEditorRow(section, 3, "北京时间差", this.displayTimeZoneOffsetLabel);
        AddEditorRow(section, 4, "北京时间 0 点", this.beijingMidnightLabel);
        page.Controls.Add(section);
        return page;
    }

    private SettingsPagePanel BuildPowerTab()
    {
        SettingsPagePanel page = BuildTabPage("功耗模块");
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

    private SettingsPagePanel BuildNetworkMonitorTab()
    {
        SettingsPagePanel page = BuildTabPage("网络监控");
        TableLayoutPanel section = BuildSettingsSection("网络监控模块", 12);
        AddSliderRow(section, 1, "模块宽度", this.networkMonitorWidthBox, this.networkMonitorWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.networkMonitorHeightBox, this.networkMonitorHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.networkMonitorLeftXBox, this.networkMonitorLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.networkMonitorBottomYBox, this.networkMonitorBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.networkMonitorTransparencyBox, this.networkMonitorTransparencySlider);
        AddEditorRow(section, 6, "网卡选择", this.networkAdapterCombo);
        AddLabel(section, 7, "网络状态测试");
        Control statusEditor = BuildButtonEditor(this.networkStatusTestButton);
        section.SetColumnSpan(statusEditor, 2);
        section.Controls.Add(statusEditor, 1, 7);
        AddCheckRow(section, 8, "GFW检测", this.gfwProbeCheck);
        AddSliderRow(section, 9, "检测间隔分钟", this.gfwProbeIntervalBox, this.gfwProbeIntervalSlider);
        AddCheckRow(
            section,
            10,
            "官方地区",
            this.cloudRegionJapanCheck,
            this.cloudRegionAsiaPacificCheck,
            this.cloudRegionNorthAmericaCheck,
            this.cloudRegionEuropeCheck);
        AddLabel(section, 11, "立即测试");
        Control gfwEditor = BuildButtonEditor(this.gfwProbeTestButton);
        section.SetColumnSpan(gfwEditor, 2);
        section.Controls.Add(gfwEditor, 1, 11);
        AddLabel(section, 12, "云服务测试");
        Control cloudEditor = BuildButtonEditor(this.cloudEndpointTestButton);
        section.SetColumnSpan(cloudEditor, 2);
        section.Controls.Add(cloudEditor, 1, 12);
        page.Controls.Add(section);
        return page;
    }

    private SettingsPagePanel BuildConnectionCheckTab()
    {
        SettingsPagePanel page = BuildTabPage("连接检测");
        TableLayoutPanel section = BuildSettingsSection("CleanIP徽标模块", 9);
        AddSliderRow(section, 1, "模块宽度", this.connectionCheckWidthBox, this.connectionCheckWidthSlider);
        AddSliderRow(section, 2, "模块高度", this.connectionCheckHeightBox, this.connectionCheckHeightSlider);
        AddSliderRow(section, 3, "位置 X", this.connectionCheckLeftXBox, this.connectionCheckLeftXSlider);
        AddSliderRow(section, 4, "位置 Y", this.connectionCheckBottomYBox, this.connectionCheckBottomYSlider);
        AddSliderRow(section, 5, "背景透明度", this.connectionCheckTransparencyBox, this.connectionCheckTransparencySlider);
        AddSliderRow(section, 6, "白色边框透明度", this.connectionCheckBorderTransparencyBox, this.connectionCheckBorderTransparencySlider);
        AddSliderRow(section, 7, "自动刷新秒", this.connectionCheckIntervalBox, this.connectionCheckIntervalSlider);
        AddLabel(section, 8, "手动刷新");
        Control manualEditor = BuildButtonEditor(this.connectionCheckManualRefreshButton);
        section.SetColumnSpan(manualEditor, 2);
        section.Controls.Add(manualEditor, 1, 8);
        AddLabel(section, 9, "强制测试");
        Control cleanIpEditor = BuildButtonEditor(this.cleanIpBadgeTestButton);
        section.SetColumnSpan(cleanIpEditor, 2);
        section.Controls.Add(cleanIpEditor, 1, 9);
        page.Controls.Add(section);
        return page;
    }

    private SettingsPagePanel BuildOperationTab()
    {
        SettingsPagePanel page = BuildTabPage("操作模块");
        TableLayoutPanel section = BuildSettingsSection("操作模块", 8);
        AddSliderRow(section, 1, "按钮大小", this.operationButtonSizeBox, this.operationButtonSizeSlider);
        AddSliderRow(section, 2, "距左边缘", this.operationLeftOffsetBox, this.operationLeftOffsetSlider);
        AddSliderRow(section, 3, "距底边缘", this.operationBottomOffsetBox, this.operationBottomOffsetSlider);
        AddSliderRow(section, 4, "背景透明度", this.operationTransparencyBox, this.operationTransparencySlider);
        AddCheckRow(section, 5, "FPS显示", this.forceShowFpsCheck);
        AddCheckRow(section, 6, "Seelen修复", this.seelenDockForegroundPulseCheck);
        AddCheckRow(section, 7, "Ctrl+D拉前", this.ctrlDRecoveryPulseCheck);
        AddCheckRow(section, 8, "唤醒重启", this.powerResumeRestartCheck);
        page.Controls.Add(section);
        return page;
    }

    private SettingsPagePanel BuildMetricsTab()
    {
        SettingsPagePanel page = BuildTabPage("栏目");
        Control metrics = BuildMetricLayoutSidePanel();
        metrics.Dock = DockStyle.Fill;
        page.Controls.Add(metrics);
        return page;
    }

    private SettingsPagePanel BuildTabPage(string text)
    {
        SettingsPagePanel page = new SettingsPagePanel();
        page.Name = "SettingsPage" + text;
        page.BackColor = DesignTokens.Colors.AppBackground;
        page.ForeColor = DesignTokens.Colors.Text;
        page.Padding = new Padding(0, 0, 10, 0);
        page.AutoScroll = true;
        page.AutoScrollMargin = new Size(0, 18);
        page.HorizontalScroll.Enabled = false;
        page.HorizontalScroll.Visible = false;
        return page;
    }

    private TableLayoutPanel BuildSettingsSection(string title, int contentRows)
    {
        SettingsSectionPanel section = new SettingsSectionPanel();
        section.Tag = "SettingsSection";
        section.Dock = DockStyle.Top;
        section.AutoSize = true;
        section.BackColor = DesignTokens.Colors.Surface;
        section.Margin = new Padding(0, 0, 0, 14);
        section.Padding = new Padding(18, 8, 18, 18);
        section.ColumnCount = 3;
        section.RowCount = contentRows + 1;
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        for (int i = 0; i < contentRows; i++)
        {
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        }

        Label label = new Label();
        label.Text = title;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = DesignTokens.CreateUIFont(12.0f, FontStyle.Bold);
        label.ForeColor = DesignTokens.Colors.TextStrong;
        label.BackColor = DesignTokens.Colors.Surface;
        label.AutoEllipsis = true;
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
        checkBox.BackColor = DesignTokens.Colors.Surface;
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

    private Button BuildCodexRadarRandomModeButton()
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

            SetCodexRadarRandomTestEnabled(!GetCodexRadarRandomTestEnabled());
            if (GetCodexRadarRandomTestEnabled())
            {
                this.codexRadarRandomTestRefreshToken++;
            }

            UpdateCodexRadarRandomTestControls();
            this.saved = false;
            this.owner.PreviewSettings(ReadControls());
        };
        StyleButton(button, false);
        return button;
    }

    private Button BuildCodexRadarRandomRefreshButton()
    {
        Button button = new Button();
        button.Text = "刷新";
        button.Width = Math.Max(74, DesignTokens.Sizes.SettingsButtonWidth - 18);
        button.Height = DesignTokens.Sizes.SettingsToggleHeight;
        button.MinimumSize = new Size(button.Width, DesignTokens.Sizes.SettingsToggleHeight);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(12, 5, 12, 5);
        button.Click += delegate
        {
            if (this.initializing || !GetCodexRadarRandomTestEnabled())
            {
                return;
            }

            this.codexRadarRandomTestRefreshToken++;
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

    private Button BuildCloudEndpointTestButton()
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

            int current = GetCloudEndpointTestSeed();
            SetCloudEndpointTestSeed(current == 0 ? CreateCloudEndpointTestSeed() : 0);
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
                ShowFooterStatus("保存完成", false);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                ShowFooterStatus(
                    "保存失败 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message,
                    true);
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
        fullExitButton.Text = "退出";
        fullExitButton.Width = DesignTokens.Sizes.SettingsPrimaryButtonWidth;
        fullExitButton.Height = DesignTokens.Sizes.SettingsButtonHeight;
        fullExitButton.Click += delegate
        {
            this.OwnerFormClosing = true;
            this.saved = true;
            this.owner.FullyExitApplication();
        };

        Button exitButton = new Button();
        exitButton.Text = "强杀";
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
        footer.BackColor = DesignTokens.Colors.AppBackground;
        footer.ColumnCount = 3;
        footer.RowCount = 1;
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));

        FlowLayoutPanel leftButtons = new FlowLayoutPanel();
        leftButtons.FlowDirection = FlowDirection.LeftToRight;
        leftButtons.Dock = DockStyle.Fill;
        leftButtons.BackColor = DesignTokens.Colors.AppBackground;
        leftButtons.Padding = new Padding(0, 12, 0, 0);
        leftButtons.Controls.Add(fullExitButton);
        leftButtons.Controls.Add(exitButton);

        FlowLayoutPanel rightButtons = new FlowLayoutPanel();
        rightButtons.FlowDirection = FlowDirection.RightToLeft;
        rightButtons.Dock = DockStyle.Fill;
        rightButtons.BackColor = DesignTokens.Colors.AppBackground;
        rightButtons.Padding = new Padding(0, 12, 0, 0);
        rightButtons.Controls.Add(saveButton);
        rightButtons.Controls.Add(cancelButton);
        rightButtons.Controls.Add(resetButton);

        this.footerStatusLabel = new Label();
        this.footerStatusLabel.Dock = DockStyle.Fill;
        this.footerStatusLabel.Margin = new Padding(8, 12, 8, 0);
        this.footerStatusLabel.Padding = new Padding(10, 0, 10, 1);
        this.footerStatusLabel.TextAlign = ContentAlignment.MiddleCenter;
        this.footerStatusLabel.AutoEllipsis = true;
        this.footerStatusLabel.BackColor = DesignTokens.Colors.Control;
        this.footerStatusLabel.ForeColor = DesignTokens.Colors.SuccessText;
        this.footerStatusLabel.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        this.footerStatusLabel.UseCompatibleTextRendering = true;
        this.footerStatusLabel.Visible = false;

        footer.Controls.Add(leftButtons, 0, 0);
        footer.Controls.Add(this.footerStatusLabel, 1, 0);
        footer.Controls.Add(rightButtons, 2, 0);
        return footer;
    }

    private void ShowFooterStatus(string text, bool error)
    {
        if (this.footerStatusLabel == null)
        {
            return;
        }

        this.footerStatusTimer.Stop();
        this.footerStatusLabel.Text = text;
        this.footerStatusLabel.ForeColor = error ? DesignTokens.Colors.DangerText : DesignTokens.Colors.SuccessText;
        this.footerStatusLabel.FlatStyle = FlatStyle.Flat;
        this.footerStatusLabel.Visible = true;
        this.footerStatusLabel.BringToFront();
        this.footerStatusTimer.Start();
    }

    private void OnFooterStatusTimerTick(object sender, EventArgs e)
    {
        this.footerStatusTimer.Stop();
        if (this.footerStatusLabel != null)
        {
            this.footerStatusLabel.Visible = false;
            this.footerStatusLabel.Text = string.Empty;
        }
    }

    private static Size GetDesiredClientSize()
    {
        return new Size(1180, 820);
    }

    private static Size FitClientSizeToScreen(Size desiredSize)
    {
        Rectangle workArea = GetUsableWorkArea();
        int margin = GetAdaptiveScreenMargin(workArea);
        int maxWidth = Math.Max(320, workArea.Width - margin);
        int maxHeight = Math.Max(300, workArea.Height - margin);
        int width = Math.Min(desiredSize.Width + SystemInformation.VerticalScrollBarWidth, maxWidth);
        int height = Math.Min(desiredSize.Height, maxHeight);
        return new Size(width, height);
    }

    private static Size GetMinimumWindowSizeForScreen()
    {
        Rectangle workArea = GetUsableWorkArea();
        int margin = GetAdaptiveScreenMargin(workArea);
        int width = Math.Min(920, Math.Max(320, workArea.Width - margin));
        int height = Math.Min(620, Math.Max(300, workArea.Height - margin));
        return new Size(width, height);
    }

    private static Rectangle GetUsableWorkArea()
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            return workArea;
        }

        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            return bounds;
        }

        return new Rectangle(0, 0, 1280, 720);
    }

    private static int GetAdaptiveScreenMargin(Rectangle workArea)
    {
        int shortestSide = Math.Min(Math.Max(1, workArea.Width), Math.Max(1, workArea.Height));
        return Math.Max(24, Math.Min(64, shortestSide / 12));
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
        slider.BackColor = DesignTokens.Colors.Surface;
        slider.SmallChange = 1;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
        slider.ValueChanged += OnSettingChanged;
        return slider;
    }

    private ComboBox BuildCombo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 28;
        combo.Dock = DockStyle.Fill;
        combo.Font = DesignTokens.CreateUIFont(7.35f);
        combo.BackColor = DesignTokens.Colors.Control;
        combo.ForeColor = DesignTokens.Colors.Text;
        combo.DrawItem += DrawSettingsComboItem;
        combo.SelectedIndexChanged += OnSettingChanged;
        return combo;
    }

    private Label BuildSettingsInfoLabel()
    {
        Label label = new Label();
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        label.ForeColor = DesignTokens.Colors.Text;
        label.BackColor = DesignTokens.Colors.Surface;
        label.AutoEllipsis = true;
        label.UseCompatibleTextRendering = true;
        return label;
    }

    private static void DrawSettingsComboItem(object sender, DrawItemEventArgs e)
    {
        ComboBox combo = sender as ComboBox;
        if (combo == null)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        Color backColor = selected ? DesignTokens.Colors.ControlActive : DesignTokens.Colors.Control;
        Color textColor = selected ? DesignTokens.Colors.Accent : DesignTokens.Colors.Text;
        using (SolidBrush background = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        string text = string.Empty;
        if (e.Index >= 0 && e.Index < combo.Items.Count)
        {
            text = combo.Items[e.Index].ToString();
        }
        else if (combo.SelectedItem != null)
        {
            text = combo.SelectedItem.ToString();
        }

        Rectangle textBounds = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            combo.Font,
            textBounds,
            textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private Control BuildMetricLayoutSidePanel()
    {
        TableLayoutPanel panel = new TableLayoutPanel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DesignTokens.Colors.AppBackground;
        panel.ColumnCount = 1;
        panel.RowCount = 2;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Padding = new Padding(0);

        Label label = new Label();
        label.Text = "栏目排序";
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = DesignTokens.CreateUIFont(10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DesignTokens.Colors.TextStrong;
        label.BackColor = DesignTokens.Colors.AppBackground;

        Control editor = BuildMetricLayoutEditor();
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(editor, 0, 1);
        return panel;
    }

    private Control BuildMetricLayoutEditor()
    {
        TableLayoutPanel editor = new TableLayoutPanel();
        editor.Dock = DockStyle.Fill;
        editor.BackColor = DesignTokens.Colors.AppBackground;
        editor.ColumnCount = 1;
        editor.RowCount = 2;
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        this.availableMetricsPanel = new FlowLayoutPanel();
        this.availableMetricsPanel.Dock = DockStyle.Fill;
        this.availableMetricsPanel.BackColor = DesignTokens.Colors.Control;
        this.availableMetricsPanel.Padding = new Padding(10, 10, 10, 8);
        this.availableMetricsPanel.Margin = new Padding(0, 0, 0, 12);
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
        this.metricSlotsPanel.BackColor = DesignTokens.Colors.AppBackground;
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
        panel.BackColor = root.BackColor;
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
        panel.BackColor = DesignTokens.Colors.Surface;
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
        label.BackColor = root.BackColor;
        label.AutoEllipsis = true;
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
            this.autoHoverOpacityIdleSecondsBox.Value = settings.AutoHoverOpacityIdleSeconds;
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
            SetNetworkAdapterId(settings.NetworkMonitorAdapterId);
            this.connectionCheckTransparencyBox.Value = settings.ConnectionCheckTransparencyPercent;
            this.connectionCheckTransparencySlider.Value = settings.ConnectionCheckTransparencyPercent;
            this.operationTransparencyBox.Value = settings.OperationBackgroundTransparencyPercent;
            this.operationTransparencySlider.Value = settings.OperationBackgroundTransparencyPercent;
            SelectComboValue(this.visibilityCombo, settings.VisibilityMode);
            SelectComboValue(this.performanceModeCombo, settings.PerformanceMode);
            SelectComboValue(this.clickThroughCombo, settings.ClickThroughMode);
            SelectComboValue(this.codexModelIqBaselineModeCombo, settings.CodexModelIqBaselineMode);
            SelectComboValue(this.codexModelTokenEfficiencyBaselineModeCombo, settings.CodexModelTokenEfficiencyBaselineMode);
            SelectComboValue(this.codexModelTimeEfficiencyBaselineModeCombo, settings.CodexModelTimeEfficiencyBaselineMode);
            SetSelectedCodexRadarModelKey(settings.CodexRadarModelKey);
            RebuildCodexRadarModelButtons();
            SelectComboValue(this.displayTimeZoneModeCombo, settings.DisplayTimeZoneMode);
            SelectComboValue(this.displayTimeZoneCombo, settings.DisplayTimeZoneId);
            this.startupCheck.Checked = settings.StartupEnabled;
            this.hoverOpacityCheck.Checked = settings.HoverOpacityEnabled;
            this.autoHoverOpacityIdleCheck.Checked = settings.AutoHoverOpacityIdleEnabled;
            this.autoHoverOpacityMaximizedCheck.Checked = settings.AutoHoverOpacityMaximizedEnabled;
            this.burnInHiddenModeColorProtectionCheck.Checked = settings.BurnInHiddenModeColorProtectionEnabled;
            this.forceShowFpsCheck.Checked = settings.ForceShowForegroundFpsEnabled;
            this.seelenDockForegroundPulseCheck.Checked = settings.SeelenDockForegroundPulseEnabled;
            this.ctrlDRecoveryPulseCheck.Checked = settings.CtrlDRecoveryPulseEnabled;
            this.powerResumeRestartCheck.Checked = settings.PowerResumeRestartEnabled;
            this.powerThermalAutoSizeCheck.Checked = settings.PowerThermalAutoSizeEnabled;
            SelectComboValue(this.powerThermalAutoDirectionCombo, settings.PowerThermalAutoDirection);
            this.codexModelIqTestCheck.Checked = settings.CodexModelIqTestEnabled;
            this.codexModelEfficiencyTestCheck.Checked = settings.CodexModelEfficiencyTestEnabled;
            this.gfwProbeCheck.Checked = settings.GfwProbeEnabled;
            this.gfwProbeManualRefreshToken = settings.GfwProbeManualRefreshToken;
            SetCloudEndpointTestSeed(settings.CloudEndpointTestSeed);
            SetCloudStatusRegionMask(settings.CloudStatusRegionMask);
            this.connectionCheckManualRefreshToken = settings.ConnectionCheckManualRefreshToken;
            SelectComboValue(this.thermalTestCombo, settings.ThermalTestMode);
            SetAlertTestButtonState(settings.AlertTestEnabled);
            this.codexRadarRandomTestRefreshToken = settings.CodexRadarRandomTestRefreshToken;
            SetCodexRadarRandomTestEnabled(settings.CodexRadarRandomTestEnabled);
            this.codexRadarRandomAutoRefreshCheck.Checked = settings.CodexRadarRandomTestAutoRefresh;
            UpdateCodexRadarRandomTestControls();
            SetCleanIpBadgeTestButtonMode(settings.CleanIpBadgeTestMode);
            SetNetworkStatusTestButtonMode(settings.NetworkStatusTestMode);
            LoadMetricLayout(settings);
            UpdatePowerThermalAutoControls();
            UpdateGfwProbeControls();
            UpdateAutoHoverOpacityControls();
            UpdateTimeZoneControls();
            UpdateCodexBaselineControls();
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
        UpdateAutoHoverOpacityControls();
        UpdateTimeZoneControls();
        UpdateCodexBaselineControls();
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

    private void UpdateCodexBaselineControls()
    {
        bool iqAbsolute = GetComboBaselineMode(this.codexModelIqBaselineModeCombo) ==
            CodexModelBaselineMode.Absolute;
        bool tokenAbsolute = GetComboBaselineMode(this.codexModelTokenEfficiencyBaselineModeCombo) ==
            CodexModelBaselineMode.Absolute;
        bool timeAbsolute = GetComboBaselineMode(this.codexModelTimeEfficiencyBaselineModeCombo) ==
            CodexModelBaselineMode.Absolute;

        SetNumericAndSliderEnabled(this.codexModelIqBaselineBox, this.codexModelIqBaselineSlider, iqAbsolute);
        SetNumericAndSliderEnabled(this.codexModelTokenEfficiencyBaselinePassedBox, null, tokenAbsolute);
        SetNumericAndSliderEnabled(this.codexModelTokenEfficiencyBaselineTokensBox, null, tokenAbsolute);
        SetNumericAndSliderEnabled(this.codexModelTimeEfficiencyBaselinePassedBox, null, timeAbsolute);
        SetNumericAndSliderEnabled(this.codexModelTimeEfficiencyBaselineSecondsBox, null, timeAbsolute);
    }

    private static CodexModelBaselineMode GetComboBaselineMode(ComboBox combo)
    {
        return (CodexModelBaselineMode)GetComboValue(combo, CodexModelBaselineMode.AllRecordsAverage);
    }

    private static void SetNumericAndSliderEnabled(NumericUpDown number, TrackBar slider, bool enabled)
    {
        if (number != null)
        {
            number.Enabled = enabled;
        }

        if (slider != null)
        {
            slider.Enabled = enabled;
        }
    }

    private void UpdateGfwProbeControls()
    {
        if (this.gfwProbeIntervalBox != null)
        {
            this.gfwProbeIntervalBox.Enabled = true;
        }

        if (this.gfwProbeIntervalSlider != null)
        {
            this.gfwProbeIntervalSlider.Enabled = true;
        }

        if (this.gfwProbeTestButton != null)
        {
            this.gfwProbeTestButton.Enabled = true;
        }

        if (this.cloudRegionJapanCheck != null)
        {
            this.cloudRegionJapanCheck.Enabled = true;
        }

        if (this.cloudRegionAsiaPacificCheck != null)
        {
            this.cloudRegionAsiaPacificCheck.Enabled = true;
        }

        if (this.cloudRegionNorthAmericaCheck != null)
        {
            this.cloudRegionNorthAmericaCheck.Enabled = true;
        }

        if (this.cloudRegionEuropeCheck != null)
        {
            this.cloudRegionEuropeCheck.Enabled = true;
        }
    }

    private void UpdateAutoHoverOpacityControls()
    {
        bool enabled = this.autoHoverOpacityIdleCheck != null && this.autoHoverOpacityIdleCheck.Checked;
        if (this.autoHoverOpacityIdleSecondsBox != null)
        {
            this.autoHoverOpacityIdleSecondsBox.Enabled = enabled;
        }
    }

    private void UpdateTimeZoneControls()
    {
        if (this.displayTimeZoneModeCombo == null ||
            this.displayTimeZoneCombo == null ||
            this.displayTimeZoneOffsetLabel == null ||
            this.beijingMidnightLabel == null)
        {
            return;
        }

        DisplayTimeZoneMode mode = (DisplayTimeZoneMode)GetComboValue(
            this.displayTimeZoneModeCombo,
            DisplayTimeZoneMode.Automatic);
        this.displayTimeZoneCombo.Enabled = mode == DisplayTimeZoneMode.Manual;

        TimeZoneInfo selectedZone = mode == DisplayTimeZoneMode.Automatic
            ? TimeZoneInfo.Local
            : TimeZoneUtilities.ResolveTimeZone(
                Convert.ToString(GetComboValue(this.displayTimeZoneCombo, TimeZoneInfo.Local.Id), CultureInfo.InvariantCulture),
                TimeZoneInfo.Local);
        TimeZoneInfo beijingZone = TimeZoneUtilities.GetBeijingTimeZone();
        DateTime utcNow = DateTime.UtcNow;
        TimeSpan selectedOffset = selectedZone.GetUtcOffset(utcNow);
        TimeSpan beijingOffset = beijingZone.GetUtcOffset(utcNow);
        TimeSpan difference = selectedOffset - beijingOffset;
        string relation = difference == TimeSpan.Zero
            ? "与北京时间相同"
            : (difference > TimeSpan.Zero
                ? "比北京时间快 " + FormatDuration(difference)
                : "比北京时间慢 " + FormatDuration(difference.Negate()));
        this.displayTimeZoneOffsetLabel.Text =
            selectedZone.StandardName + "  " + FormatUtcOffset(selectedOffset) + "，" + relation;

        DateTime beijingDate = TimeZoneInfo.ConvertTimeFromUtc(utcNow, beijingZone).Date;
        DateTime beijingMidnight = DateTime.SpecifyKind(beijingDate, DateTimeKind.Unspecified);
        DateTime midnightUtc = TimeZoneInfo.ConvertTimeToUtc(beijingMidnight, beijingZone);
        DateTime selectedMidnight = TimeZoneInfo.ConvertTimeFromUtc(midnightUtc, selectedZone);
        int dayDelta = (selectedMidnight.Date - beijingDate).Days;
        string dayRelation = dayDelta < 0 ? "前一天" : (dayDelta > 0 ? "后一天" : "当天");
        this.beijingMidnightLabel.Text =
            dayRelation + " " + selectedMidnight.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatUtcOffset(TimeSpan offset)
    {
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        TimeSpan absolute = offset.Duration();
        return "UTC" + sign +
            ((int)absolute.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" +
            absolute.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        int totalMinutes = Math.Max(0, (int)Math.Round(duration.TotalMinutes));
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        if (minutes == 0)
        {
            return hours.ToString(CultureInfo.InvariantCulture) + " 小时";
        }

        return hours.ToString(CultureInfo.InvariantCulture) + " 小时 " +
            minutes.ToString(CultureInfo.InvariantCulture) + " 分";
    }

    private void UpdatePositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = GetUsableWorkArea();
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
            Rectangle bounds = GetUsableWorkArea();
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
            Rectangle bounds = GetUsableWorkArea();
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
            Rectangle bounds = GetUsableWorkArea();
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
            Rectangle bounds = GetUsableWorkArea();
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
                Rectangle workArea = GetUsableWorkArea();
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
        settings.NetworkMonitorAdapterId = GetNetworkAdapterId();
        settings.NetworkStatusTestMode = GetNetworkStatusTestButtonMode();
        settings.GfwProbeEnabled = this.gfwProbeCheck.Checked;
        settings.GfwProbeIntervalMinutes = (int)this.gfwProbeIntervalBox.Value;
        settings.GfwProbeManualRefreshToken = this.gfwProbeManualRefreshToken;
        settings.CloudEndpointTestSeed = GetCloudEndpointTestSeed();
        settings.CloudStatusRegionMask = GetCloudStatusRegionMask();
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
        settings.ForceShowForegroundFpsEnabled = this.forceShowFpsCheck.Checked;
        settings.SeelenDockForegroundPulseEnabled = this.seelenDockForegroundPulseCheck.Checked;
        settings.CtrlDRecoveryPulseEnabled = this.ctrlDRecoveryPulseCheck.Checked;
        settings.PowerResumeRestartEnabled = this.powerResumeRestartCheck.Checked;
        settings.ThermalTestMode = (ThermalTestMode)GetComboValue(this.thermalTestCombo, ThermalTestMode.Off);
        settings.CodexRadarTestMode = CodexRadarTestMode.Off;
        settings.ServiceHealthTestMode = ServiceHealthTestMode.Off;
        settings.CodexRadarRandomTestEnabled = GetCodexRadarRandomTestEnabled();
        settings.CodexRadarRandomTestAutoRefresh =
            this.codexRadarRandomAutoRefreshCheck != null &&
            this.codexRadarRandomAutoRefreshCheck.Checked;
        settings.CodexRadarRandomTestRefreshToken = this.codexRadarRandomTestRefreshToken;
        settings.CleanIpBadgeTestMode = GetCleanIpBadgeTestButtonMode();
        settings.CodexModelIqTestEnabled = this.codexModelIqTestCheck.Checked;
        settings.CodexModelIqTestPassed = (int)this.codexModelIqTestPassedBox.Value;
        settings.CodexModelIqBaselinePassed = (int)this.codexModelIqBaselineBox.Value;
        settings.CodexModelIqBaselineMode = GetComboBaselineMode(this.codexModelIqBaselineModeCombo);
        settings.CodexModelEfficiencyTestEnabled = this.codexModelEfficiencyTestCheck.Checked;
        settings.CodexModelTokenEfficiencyTestPercent = (int)this.codexModelTokenEfficiencyTestBox.Value;
        settings.CodexModelTimeEfficiencyTestPercent = (int)this.codexModelTimeEfficiencyTestBox.Value;
        settings.CodexModelTokenEfficiencyBaselinePassed = (int)this.codexModelTokenEfficiencyBaselinePassedBox.Value;
        settings.CodexModelTokenEfficiencyBaselineTokens = (int)this.codexModelTokenEfficiencyBaselineTokensBox.Value;
        settings.CodexModelTokenEfficiencyBaselineMode = GetComboBaselineMode(this.codexModelTokenEfficiencyBaselineModeCombo);
        settings.CodexModelTimeEfficiencyBaselinePassed = (int)this.codexModelTimeEfficiencyBaselinePassedBox.Value;
        settings.CodexModelTimeEfficiencyBaselineSeconds = (int)this.codexModelTimeEfficiencyBaselineSecondsBox.Value;
        settings.CodexModelTimeEfficiencyBaselineMode = GetComboBaselineMode(this.codexModelTimeEfficiencyBaselineModeCombo);
        settings.CodexModelTokenEfficiencyLowThresholdPercent = (int)this.codexModelTokenEfficiencyLowThresholdBox.Value;
        settings.CodexModelTimeEfficiencyLowThresholdPercent = (int)this.codexModelTimeEfficiencyLowThresholdBox.Value;
        settings.CodexRadarModelKey = CodexRadarModelCatalog.NormalizeModelKey(this.selectedCodexRadarModelKey);
        settings.CodexRadarModelVersion = CodexRadarModelCatalog.LegacyVersionFromKey(settings.CodexRadarModelKey);
        settings.DisplayTimeZoneMode = (DisplayTimeZoneMode)GetComboValue(
            this.displayTimeZoneModeCombo,
            DisplayTimeZoneMode.Automatic);
        settings.DisplayTimeZoneId = Convert.ToString(
            GetComboValue(this.displayTimeZoneCombo, TimeZoneInfo.Local.Id),
            CultureInfo.InvariantCulture);
        settings.VisibilityMode = (WidgetVisibilityMode)GetComboValue(this.visibilityCombo, WidgetVisibilityMode.DesktopOnly);
        settings.StartupEnabled = this.startupCheck.Checked;
        settings.PerformanceMode = (WidgetPerformanceMode)GetComboValue(this.performanceModeCombo, WidgetPerformanceMode.Balanced);
        settings.ClickThroughMode = (ClickThroughMode)GetComboValue(this.clickThroughCombo, ClickThroughMode.Auto);
        settings.HoverOpacityEnabled = this.hoverOpacityCheck.Checked;
        settings.AutoHoverOpacityIdleEnabled = this.autoHoverOpacityIdleCheck.Checked;
        settings.AutoHoverOpacityIdleSeconds = (int)this.autoHoverOpacityIdleSecondsBox.Value;
        settings.AutoHoverOpacityMaximizedEnabled = this.autoHoverOpacityMaximizedCheck.Checked;
        settings.BurnInHiddenModeColorProtectionEnabled = this.burnInHiddenModeColorProtectionCheck.Checked;
        string[] selectedMetrics = this.metricSlotsPanel == null
            ? CloneStringArray(this.baseline.MetricOrder)
            : ReadMetricSlots(false);
        if (this.metricSlotsPanel == null)
        {
            settings.ShowCpu = this.baseline.ShowCpu;
            settings.ShowMemory = this.baseline.ShowMemory;
            settings.ShowDisk = this.baseline.ShowDisk;
            settings.ShowNetwork = this.baseline.ShowNetwork;
            settings.ShowGpu = this.baseline.ShowGpu;
            settings.ShowNpu = this.baseline.ShowNpu;
        }
        else
        {
            settings.ShowCpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricCpu);
            settings.ShowMemory = ContainsMetricId(selectedMetrics, WidgetSettings.MetricMemory);
            settings.ShowDisk = ContainsMetricId(selectedMetrics, WidgetSettings.MetricDisk);
            settings.ShowNetwork = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNetwork);
            settings.ShowGpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricGpu);
            settings.ShowNpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNpu);
        }
        settings.AlertTestEnabled = GetAlertTestButtonState();
        settings.MetricOrder = selectedMetrics;
        settings.Normalize();
        return settings;
    }

    internal static void RunSettingsBindingSelfTest()
    {
        WidgetSettings baseline = WidgetSettings.CreateDefaults();
        baseline.Normalize();
        using (SettingsForm form = new SettingsForm(null, baseline))
        {
            form.OwnerFormClosing = true;
            form.saved = true;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();
            form.VerifySettingsBindingSelfTest();
        }
    }

    private void VerifySettingsBindingSelfTest()
    {
        AssertVisibleBinding(this.connectionCheckIntervalBox, "ConnectionCheckIntervalSeconds box");
        AssertVisibleBinding(this.connectionCheckIntervalSlider, "ConnectionCheckIntervalSeconds slider");
        AssertVisibleBinding(this.codexRadarModelButtonGrid, "CodexRadarModelKey buttons");
        AssertVisibleBinding(this.codexModelIqBaselineModeCombo, "CodexModelIqBaselineMode combo");
        AssertVisibleBinding(this.codexModelTokenEfficiencyBaselineModeCombo, "CodexModelTokenEfficiencyBaselineMode combo");
        AssertVisibleBinding(this.codexModelTimeEfficiencyBaselineModeCombo, "CodexModelTimeEfficiencyBaselineMode combo");
        AssertVisibleBinding(this.displayTimeZoneModeCombo, "DisplayTimeZoneMode combo");
        AssertVisibleBinding(this.displayTimeZoneCombo, "DisplayTimeZoneId combo");
        AssertVisibleBinding(this.autoHoverOpacityIdleSecondsBox, "AutoHoverOpacityIdleSeconds box");
        AssertVisibleBinding(this.seelenDockForegroundPulseCheck, "SeelenDockForegroundPulseEnabled check");
        AssertVisibleBinding(this.ctrlDRecoveryPulseCheck, "CtrlDRecoveryPulseEnabled check");
        AssertVisibleBinding(this.powerResumeRestartCheck, "PowerResumeRestartEnabled check");
        AssertVisibleBinding(this.burnInHiddenModeColorProtectionCheck, "BurnInHiddenModeColorProtectionEnabled check");
        AssertSettingsPagesWheelScrollable();
        AssertPositionRangeUsesWorkArea("Widget", this.widthBox, this.heightBox, this.leftXBox, this.bottomYBox);
        AssertPositionRangeUsesWorkArea("CodexRadar", this.codexRadarWidthBox, this.codexRadarHeightBox, this.codexRadarLeftXBox, this.codexRadarBottomYBox);
        AssertPositionRangeUsesWorkArea("PowerThermal", this.powerThermalWidthBox, this.powerThermalHeightBox, this.powerThermalLeftXBox, this.powerThermalBottomYBox);
        AssertPositionRangeUsesWorkArea("NetworkMonitor", this.networkMonitorWidthBox, this.networkMonitorHeightBox, this.networkMonitorLeftXBox, this.networkMonitorBottomYBox);
        AssertPositionRangeUsesWorkArea("ConnectionCheck", this.connectionCheckWidthBox, this.connectionCheckHeightBox, this.connectionCheckLeftXBox, this.connectionCheckBottomYBox);

        bool wasInitializing = this.initializing;
        this.initializing = true;
        int width = PickDifferentValue(this.widthBox);
        int height = PickDifferentValue(this.heightBox);
        int leftX = (int)this.leftXBox.Minimum;
        int bottomY = (int)this.bottomYBox.Maximum;
        int backgroundTransparency = PickDifferentValue(this.backgroundTransparencyBox);
        int applicationTransparency = PickDifferentValue(this.applicationTransparencyBox);
        int codexRadarWidth = PickDifferentValue(this.codexRadarWidthBox);
        int codexRadarHeight = PickDifferentValue(this.codexRadarHeightBox);
        int codexRadarLeftX = (int)this.codexRadarLeftXBox.Minimum;
        int codexRadarBottomY = (int)this.codexRadarBottomYBox.Maximum;
        int codexRadarTransparency = PickDifferentValue(this.codexRadarTransparencyBox);
        int powerThermalWidth = PickDifferentValue(this.powerThermalWidthBox);
        int powerThermalHeight = PickDifferentValue(this.powerThermalHeightBox);
        int powerThermalLeftX = (int)this.powerThermalLeftXBox.Minimum;
        int powerThermalBottomY = (int)this.powerThermalBottomYBox.Maximum;
        int powerThermalTransparency = PickDifferentValue(this.powerThermalTransparencyBox);
        int powerThermalVisibleAlerts = PickDifferentValue(this.powerThermalVisibleAlertCountBox);
        int networkMonitorWidth = PickDifferentValue(this.networkMonitorWidthBox);
        int networkMonitorHeight = PickDifferentValue(this.networkMonitorHeightBox);
        int networkMonitorLeftX = (int)this.networkMonitorLeftXBox.Minimum;
        int networkMonitorBottomY = (int)this.networkMonitorBottomYBox.Maximum;
        int networkMonitorTransparency = PickDifferentValue(this.networkMonitorTransparencyBox);
        int gfwProbeInterval = PickDifferentValue(this.gfwProbeIntervalBox);
        int connectionCheckWidth = PickDifferentValue(this.connectionCheckWidthBox);
        int connectionCheckHeight = PickDifferentValue(this.connectionCheckHeightBox);
        int connectionCheckLeftX = (int)this.connectionCheckLeftXBox.Minimum;
        int connectionCheckBottomY = (int)this.connectionCheckBottomYBox.Maximum;
        int connectionCheckTransparency = PickDifferentValue(this.connectionCheckTransparencyBox);
        int connectionCheckBorderTransparency = PickDifferentValue(this.connectionCheckBorderTransparencyBox);
        int connectionCheckInterval = PickDifferentValue(this.connectionCheckIntervalBox);
        int operationButtonSize = PickDifferentValue(this.operationButtonSizeBox);
        int operationLeftOffset = PickDifferentValue(this.operationLeftOffsetBox);
        int operationBottomOffset = PickDifferentValue(this.operationBottomOffsetBox);
        int operationTransparency = PickDifferentValue(this.operationTransparencyBox);
        int autoHoverOpacityIdleSeconds = PickDifferentValue(this.autoHoverOpacityIdleSecondsBox);
        int iqPassed = PickDifferentValue(this.codexModelIqTestPassedBox);
        int iqBaseline = PickDifferentValue(this.codexModelIqBaselineBox);
        int tokenEfficiency = PickDifferentValue(this.codexModelTokenEfficiencyTestBox);
        int timeEfficiency = PickDifferentValue(this.codexModelTimeEfficiencyTestBox);
        int tokenBaselinePassed = PickDifferentValue(this.codexModelTokenEfficiencyBaselinePassedBox);
        int tokenBaselineTokens = PickDifferentValue(this.codexModelTokenEfficiencyBaselineTokensBox);
        int timeBaselinePassed = PickDifferentValue(this.codexModelTimeEfficiencyBaselinePassedBox);
        int timeBaselineSeconds = PickDifferentValue(this.codexModelTimeEfficiencyBaselineSecondsBox);
        int tokenLowThreshold = PickDifferentValue(this.codexModelTokenEfficiencyLowThresholdBox);
        int timeLowThreshold = PickDifferentValue(this.codexModelTimeEfficiencyLowThresholdBox);
        try
        {
            SetNumber(this.widthBox, this.widthSlider, width);
            SetNumber(this.heightBox, this.heightSlider, height);
            SetNumber(this.leftXBox, this.leftXSlider, leftX);
            SetNumber(this.bottomYBox, this.bottomYSlider, bottomY);
            SetNumber(this.backgroundTransparencyBox, this.backgroundTransparencySlider, backgroundTransparency);
            SetNumber(this.applicationTransparencyBox, this.applicationTransparencySlider, applicationTransparency);
            SetNumber(this.codexRadarWidthBox, this.codexRadarWidthSlider, codexRadarWidth);
            SetNumber(this.codexRadarHeightBox, this.codexRadarHeightSlider, codexRadarHeight);
            SetNumber(this.codexRadarLeftXBox, this.codexRadarLeftXSlider, codexRadarLeftX);
            SetNumber(this.codexRadarBottomYBox, this.codexRadarBottomYSlider, codexRadarBottomY);
            SetNumber(this.codexRadarTransparencyBox, this.codexRadarTransparencySlider, codexRadarTransparency);
            SetNumber(this.powerThermalWidthBox, this.powerThermalWidthSlider, powerThermalWidth);
            SetNumber(this.powerThermalHeightBox, this.powerThermalHeightSlider, powerThermalHeight);
            SetNumber(this.powerThermalLeftXBox, this.powerThermalLeftXSlider, powerThermalLeftX);
            SetNumber(this.powerThermalBottomYBox, this.powerThermalBottomYSlider, powerThermalBottomY);
            SetNumber(this.powerThermalTransparencyBox, this.powerThermalTransparencySlider, powerThermalTransparency);
            SetNumber(this.powerThermalVisibleAlertCountBox, this.powerThermalVisibleAlertCountSlider, powerThermalVisibleAlerts);
            SetNumber(this.networkMonitorWidthBox, this.networkMonitorWidthSlider, networkMonitorWidth);
            SetNumber(this.networkMonitorHeightBox, this.networkMonitorHeightSlider, networkMonitorHeight);
            SetNumber(this.networkMonitorLeftXBox, this.networkMonitorLeftXSlider, networkMonitorLeftX);
            SetNumber(this.networkMonitorBottomYBox, this.networkMonitorBottomYSlider, networkMonitorBottomY);
            SetNumber(this.networkMonitorTransparencyBox, this.networkMonitorTransparencySlider, networkMonitorTransparency);
            SetNetworkAdapterId("settings-self-test-adapter");
            SetNumber(this.gfwProbeIntervalBox, this.gfwProbeIntervalSlider, gfwProbeInterval);
            SetNumber(this.connectionCheckWidthBox, this.connectionCheckWidthSlider, connectionCheckWidth);
            SetNumber(this.connectionCheckHeightBox, this.connectionCheckHeightSlider, connectionCheckHeight);
            SetNumber(this.connectionCheckLeftXBox, this.connectionCheckLeftXSlider, connectionCheckLeftX);
            SetNumber(this.connectionCheckBottomYBox, this.connectionCheckBottomYSlider, connectionCheckBottomY);
            SetNumber(this.connectionCheckTransparencyBox, this.connectionCheckTransparencySlider, connectionCheckTransparency);
            SetNumber(this.connectionCheckBorderTransparencyBox, this.connectionCheckBorderTransparencySlider, connectionCheckBorderTransparency);
            SetNumber(this.connectionCheckIntervalBox, this.connectionCheckIntervalSlider, connectionCheckInterval);
            SetNumber(this.operationButtonSizeBox, this.operationButtonSizeSlider, operationButtonSize);
            SetNumber(this.operationLeftOffsetBox, this.operationLeftOffsetSlider, operationLeftOffset);
            SetNumber(this.operationBottomOffsetBox, this.operationBottomOffsetSlider, operationBottomOffset);
            SetNumber(this.operationTransparencyBox, this.operationTransparencySlider, operationTransparency);
            SetNumber(this.autoHoverOpacityIdleSecondsBox, null, autoHoverOpacityIdleSeconds);
            SetNumber(this.codexModelIqTestPassedBox, this.codexModelIqTestPassedSlider, iqPassed);
            SetNumber(this.codexModelIqBaselineBox, this.codexModelIqBaselineSlider, iqBaseline);
            SetNumber(this.codexModelTokenEfficiencyTestBox, this.codexModelTokenEfficiencyTestSlider, tokenEfficiency);
            SetNumber(this.codexModelTimeEfficiencyTestBox, this.codexModelTimeEfficiencyTestSlider, timeEfficiency);
            SetNumber(this.codexModelTokenEfficiencyBaselinePassedBox, null, tokenBaselinePassed);
            SetNumber(this.codexModelTokenEfficiencyBaselineTokensBox, null, tokenBaselineTokens);
            SetNumber(this.codexModelTimeEfficiencyBaselinePassedBox, null, timeBaselinePassed);
            SetNumber(this.codexModelTimeEfficiencyBaselineSecondsBox, null, timeBaselineSeconds);
            SetNumber(this.codexModelTokenEfficiencyLowThresholdBox, this.codexModelTokenEfficiencyLowThresholdSlider, tokenLowThreshold);
            SetNumber(this.codexModelTimeEfficiencyLowThresholdBox, this.codexModelTimeEfficiencyLowThresholdSlider, timeLowThreshold);

            SelectComboValue(this.visibilityCombo, WidgetVisibilityMode.HideWhenFullscreen);
            SelectComboValue(this.performanceModeCombo, WidgetPerformanceMode.Smooth);
            SelectComboValue(this.clickThroughCombo, ClickThroughMode.Enabled);
            SelectComboValue(this.powerThermalAutoDirectionCombo, PowerThermalAutoDirection.Left);
            SelectComboValue(this.thermalTestCombo, ThermalTestMode.Simulate75);
            SelectComboValue(this.codexModelIqBaselineModeCombo, CodexModelBaselineMode.Recent7Average);
            SelectComboValue(this.codexModelTokenEfficiencyBaselineModeCombo, CodexModelBaselineMode.Recent30Average);
            SelectComboValue(this.codexModelTimeEfficiencyBaselineModeCombo, CodexModelBaselineMode.Absolute);
            SetSelectedCodexRadarModelKey("gpt_55_medium");
            SelectComboValue(this.displayTimeZoneModeCombo, DisplayTimeZoneMode.Manual);
            SelectComboValue(this.displayTimeZoneCombo, TimeZoneUtilities.BeijingTimeZoneId);
            this.startupCheck.Checked = true;
            this.hoverOpacityCheck.Checked = false;
            this.autoHoverOpacityIdleCheck.Checked = true;
            this.autoHoverOpacityMaximizedCheck.Checked = true;
            this.burnInHiddenModeColorProtectionCheck.Checked = true;
            this.forceShowFpsCheck.Checked = true;
            this.seelenDockForegroundPulseCheck.Checked = false;
            this.ctrlDRecoveryPulseCheck.Checked = false;
            this.powerResumeRestartCheck.Checked = false;
            this.powerThermalAutoSizeCheck.Checked = true;
            this.codexModelIqTestCheck.Checked = true;
            this.codexModelEfficiencyTestCheck.Checked = true;
            this.gfwProbeCheck.Checked = true;
            SetAlertTestButtonState(true);
            SetCodexRadarRandomTestEnabled(true);
            this.codexRadarRandomAutoRefreshCheck.Checked = true;
            this.codexRadarRandomTestRefreshToken = 7;
            UpdateCodexRadarRandomTestControls();
            SetCleanIpBadgeTestButtonMode(CleanIpBadgeTestMode.ProxyRisk);
            SetNetworkStatusTestButtonMode(NetworkStatusTestMode.NeedsValidation);
            this.gfwProbeManualRefreshToken = 3;
            SetCloudEndpointTestSeed(12345);
            SetCloudStatusRegionMask(WidgetSettings.CloudStatusRegionJapan | WidgetSettings.CloudStatusRegionEurope);
            this.connectionCheckManualRefreshToken = 5;
        }
        finally
        {
            this.initializing = wasInitializing;
        }

        WidgetSettings settings = ReadControls();
        AssertEqual(width, settings.Width, "Width");
        AssertEqual(height, settings.Height, "Height");
        AssertEqual(leftX, settings.LeftX, "LeftX");
        AssertEqual(bottomY, settings.BottomY, "BottomY");
        AssertEqual(backgroundTransparency, settings.BackgroundTransparencyPercent, "BackgroundTransparencyPercent");
        AssertEqual(applicationTransparency, settings.ApplicationTransparencyPercent, "ApplicationTransparencyPercent");
        AssertEqual(codexRadarWidth, settings.CodexRadarWidth, "CodexRadarWidth");
        AssertEqual(codexRadarHeight, settings.CodexRadarHeight, "CodexRadarHeight");
        AssertEqual(codexRadarLeftX, settings.CodexRadarLeftX, "CodexRadarLeftX");
        AssertEqual(codexRadarBottomY, settings.CodexRadarBottomY, "CodexRadarBottomY");
        AssertEqual(codexRadarTransparency, settings.CodexRadarTransparencyPercent, "CodexRadarTransparencyPercent");
        AssertEqual(powerThermalWidth, settings.PowerThermalWidth, "PowerThermalWidth");
        AssertEqual(powerThermalHeight, settings.PowerThermalHeight, "PowerThermalHeight");
        AssertEqual(powerThermalLeftX, settings.PowerThermalLeftX, "PowerThermalLeftX");
        AssertEqual(powerThermalBottomY, settings.PowerThermalBottomY, "PowerThermalBottomY");
        AssertEqual(powerThermalTransparency, settings.PowerThermalTransparencyPercent, "PowerThermalTransparencyPercent");
        AssertTrue(settings.PowerThermalAutoSizeEnabled, "PowerThermalAutoSizeEnabled");
        AssertEqual(PowerThermalAutoDirection.Left, settings.PowerThermalAutoDirection, "PowerThermalAutoDirection");
        AssertEqual(powerThermalVisibleAlerts, settings.PowerThermalVisibleAlertCount, "PowerThermalVisibleAlertCount");
        AssertEqual(networkMonitorWidth, settings.NetworkMonitorWidth, "NetworkMonitorWidth");
        AssertEqual(networkMonitorHeight, settings.NetworkMonitorHeight, "NetworkMonitorHeight");
        AssertEqual(networkMonitorLeftX, settings.NetworkMonitorLeftX, "NetworkMonitorLeftX");
        AssertEqual(networkMonitorBottomY, settings.NetworkMonitorBottomY, "NetworkMonitorBottomY");
        AssertEqual(networkMonitorTransparency, settings.NetworkMonitorTransparencyPercent, "NetworkMonitorTransparencyPercent");
        AssertEqual("settings-self-test-adapter", settings.NetworkMonitorAdapterId, "NetworkMonitorAdapterId");
        AssertEqual(NetworkStatusTestMode.NeedsValidation, settings.NetworkStatusTestMode, "NetworkStatusTestMode");
        AssertTrue(settings.GfwProbeEnabled, "GfwProbeEnabled");
        AssertEqual(gfwProbeInterval, settings.GfwProbeIntervalMinutes, "GfwProbeIntervalMinutes");
        AssertEqual(3, settings.GfwProbeManualRefreshToken, "GfwProbeManualRefreshToken");
        AssertEqual(12345, settings.CloudEndpointTestSeed, "CloudEndpointTestSeed");
        AssertEqual(
            WidgetSettings.CloudStatusRegionJapan | WidgetSettings.CloudStatusRegionEurope,
            settings.CloudStatusRegionMask,
            "CloudStatusRegionMask");
        AssertEqual(connectionCheckWidth, settings.ConnectionCheckWidth, "ConnectionCheckWidth");
        AssertEqual(connectionCheckHeight, settings.ConnectionCheckHeight, "ConnectionCheckHeight");
        AssertEqual(connectionCheckLeftX, settings.ConnectionCheckLeftX, "ConnectionCheckLeftX");
        AssertEqual(connectionCheckBottomY, settings.ConnectionCheckBottomY, "ConnectionCheckBottomY");
        AssertEqual(connectionCheckTransparency, settings.ConnectionCheckTransparencyPercent, "ConnectionCheckTransparencyPercent");
        AssertEqual(connectionCheckBorderTransparency, settings.ConnectionCheckBorderTransparencyPercent, "ConnectionCheckBorderTransparencyPercent");
        AssertEqual(connectionCheckInterval, settings.ConnectionCheckIntervalSeconds, "ConnectionCheckIntervalSeconds");
        AssertEqual(5, settings.ConnectionCheckManualRefreshToken, "ConnectionCheckManualRefreshToken");
        AssertEqual(operationButtonSize, settings.OperationButtonSize, "OperationButtonSize");
        AssertEqual(operationLeftOffset, settings.OperationLeftOffset, "OperationLeftOffset");
        AssertEqual(operationBottomOffset, settings.OperationBottomOffset, "OperationBottomOffset");
        AssertEqual(operationTransparency, settings.OperationBackgroundTransparencyPercent, "OperationBackgroundTransparencyPercent");
        AssertTrue(settings.ForceShowForegroundFpsEnabled, "ForceShowForegroundFpsEnabled");
        AssertTrue(!settings.SeelenDockForegroundPulseEnabled, "SeelenDockForegroundPulseEnabled");
        AssertTrue(!settings.CtrlDRecoveryPulseEnabled, "CtrlDRecoveryPulseEnabled");
        AssertTrue(!settings.PowerResumeRestartEnabled, "PowerResumeRestartEnabled");
        AssertEqual(WidgetVisibilityMode.HideWhenFullscreen, settings.VisibilityMode, "VisibilityMode");
        AssertEqual(ClickThroughMode.Enabled, settings.ClickThroughMode, "ClickThroughMode");
        AssertTrue(settings.StartupEnabled, "StartupEnabled");
        AssertTrue(!settings.HoverOpacityEnabled, "HoverOpacityEnabled");
        AssertTrue(settings.AutoHoverOpacityIdleEnabled, "AutoHoverOpacityIdleEnabled");
        AssertEqual(autoHoverOpacityIdleSeconds, settings.AutoHoverOpacityIdleSeconds, "AutoHoverOpacityIdleSeconds");
        AssertTrue(settings.AutoHoverOpacityMaximizedEnabled, "AutoHoverOpacityMaximizedEnabled");
        AssertTrue(settings.BurnInHiddenModeColorProtectionEnabled, "BurnInHiddenModeColorProtectionEnabled");
        AssertTrue(settings.AlertTestEnabled, "AlertTestEnabled");
        AssertEqual(ThermalTestMode.Simulate75, settings.ThermalTestMode, "ThermalTestMode");
        AssertEqual(CodexRadarTestMode.Off, settings.CodexRadarTestMode, "CodexRadarTestMode");
        AssertEqual(ServiceHealthTestMode.Off, settings.ServiceHealthTestMode, "ServiceHealthTestMode");
        AssertTrue(settings.CodexRadarRandomTestEnabled, "CodexRadarRandomTestEnabled");
        AssertTrue(settings.CodexRadarRandomTestAutoRefresh, "CodexRadarRandomTestAutoRefresh");
        AssertEqual(7, settings.CodexRadarRandomTestRefreshToken, "CodexRadarRandomTestRefreshToken");
        AssertEqual(CleanIpBadgeTestMode.ProxyRisk, settings.CleanIpBadgeTestMode, "CleanIpBadgeTestMode");
        AssertTrue(settings.CodexModelIqTestEnabled, "CodexModelIqTestEnabled");
        AssertEqual(iqPassed, settings.CodexModelIqTestPassed, "CodexModelIqTestPassed");
        AssertEqual(iqBaseline, settings.CodexModelIqBaselinePassed, "CodexModelIqBaselinePassed");
        AssertEqual(CodexModelBaselineMode.Recent7Average, settings.CodexModelIqBaselineMode, "CodexModelIqBaselineMode");
        AssertTrue(settings.CodexModelEfficiencyTestEnabled, "CodexModelEfficiencyTestEnabled");
        AssertEqual(tokenEfficiency, settings.CodexModelTokenEfficiencyTestPercent, "CodexModelTokenEfficiencyTestPercent");
        AssertEqual(timeEfficiency, settings.CodexModelTimeEfficiencyTestPercent, "CodexModelTimeEfficiencyTestPercent");
        AssertEqual(tokenBaselinePassed, settings.CodexModelTokenEfficiencyBaselinePassed, "CodexModelTokenEfficiencyBaselinePassed");
        AssertEqual(tokenBaselineTokens, settings.CodexModelTokenEfficiencyBaselineTokens, "CodexModelTokenEfficiencyBaselineTokens");
        AssertEqual(CodexModelBaselineMode.Recent30Average, settings.CodexModelTokenEfficiencyBaselineMode, "CodexModelTokenEfficiencyBaselineMode");
        AssertEqual(timeBaselinePassed, settings.CodexModelTimeEfficiencyBaselinePassed, "CodexModelTimeEfficiencyBaselinePassed");
        AssertEqual(timeBaselineSeconds, settings.CodexModelTimeEfficiencyBaselineSeconds, "CodexModelTimeEfficiencyBaselineSeconds");
        AssertEqual(CodexModelBaselineMode.Absolute, settings.CodexModelTimeEfficiencyBaselineMode, "CodexModelTimeEfficiencyBaselineMode");
        AssertEqual(tokenLowThreshold, settings.CodexModelTokenEfficiencyLowThresholdPercent, "CodexModelTokenEfficiencyLowThresholdPercent");
        AssertEqual(timeLowThreshold, settings.CodexModelTimeEfficiencyLowThresholdPercent, "CodexModelTimeEfficiencyLowThresholdPercent");
        AssertEqual("gpt_55_medium", settings.CodexRadarModelKey, "CodexRadarModelKey");
        AssertEqual(CodexRadarModelVersion.Gpt55Medium, settings.CodexRadarModelVersion, "CodexRadarModelVersion");
        AssertEqual(DisplayTimeZoneMode.Manual, settings.DisplayTimeZoneMode, "DisplayTimeZoneMode");
        AssertEqual(TimeZoneUtilities.BeijingTimeZoneId, settings.DisplayTimeZoneId, "DisplayTimeZoneId");
        AssertEqual(WidgetPerformanceMode.Smooth, settings.PerformanceMode, "PerformanceMode");
        AssertEqual(this.baseline.ShowCpu, settings.ShowCpu, "ShowCpu");
        AssertEqual(this.baseline.ShowMemory, settings.ShowMemory, "ShowMemory");
        AssertEqual(this.baseline.ShowDisk, settings.ShowDisk, "ShowDisk");
        AssertEqual(this.baseline.ShowNetwork, settings.ShowNetwork, "ShowNetwork");
        AssertEqual(this.baseline.ShowGpu, settings.ShowGpu, "ShowGpu");
        AssertEqual(this.baseline.ShowNpu, settings.ShowNpu, "ShowNpu");
    }

    private void AssertSettingsPagesWheelScrollable()
    {
        if (this.settingsPages == null)
        {
            throw new InvalidOperationException("Settings pages missing.");
        }

        int originalPage = this.selectedSettingsPageIndex;
        Size originalClientSize = this.ClientSize;
        int testedScrollablePages = 0;
        try
        {
            this.ClientSize = new Size(Math.Max(720, this.MinimumSize.Width), 360);
            UpdateResponsiveLayout();
            for (int i = 0; i < this.settingsPages.Length; i++)
            {
                SelectSettingsPage(i);
                SettingsPagePanel page = this.settingsPages[i] as SettingsPagePanel;
                if (page == null)
                {
                    continue;
                }

                page.PerformLayout();
                page.AutoScrollPosition = Point.Empty;
                page.PerformLayout();
                if (!page.HasVerticalOverflow)
                {
                    continue;
                }

                testedScrollablePages++;
                int before = page.ScrollTop;
                bool handled = page.ScrollByMouseWheelDelta(-120);
                int after = page.ScrollTop;
                string title = i >= 0 && i < SettingsNavigationTitles.Length ? SettingsNavigationTitles[i] : i.ToString(CultureInfo.InvariantCulture);
                AssertTrue(handled, "Settings page wheel handled: " + title);
                AssertTrue(after > before, "Settings page wheel moved: " + title);
                page.ScrollByMouseWheelDelta(120);
            }
        }
        finally
        {
            this.ClientSize = originalClientSize;
            SelectSettingsPage(originalPage);
        }

        AssertTrue(testedScrollablePages > 0, "Settings page wheel coverage");
    }

    private void AssertPositionRangeUsesWorkArea(string name, NumericUpDown widthBox, NumericUpDown heightBox, NumericUpDown leftBox, NumericUpDown bottomBox)
    {
        Rectangle workArea = GetUsableWorkArea();
        UpdatePositionRangeForSelfTest(name, (int)widthBox.Value, (int)heightBox.Value);
        AssertEqual(workArea.Left, (int)leftBox.Minimum, name + ".Left.Minimum");
        AssertEqual(Math.Max(workArea.Left, workArea.Right - (int)widthBox.Value), (int)leftBox.Maximum, name + ".Left.Maximum");
        AssertEqual(Math.Min(workArea.Bottom - 1, workArea.Top + (int)heightBox.Value - 1), (int)bottomBox.Minimum, name + ".Bottom.Minimum");
        AssertEqual(Math.Max(workArea.Top, workArea.Bottom - 1), (int)bottomBox.Maximum, name + ".Bottom.Maximum");
    }

    private void UpdatePositionRangeForSelfTest(string name, int width, int height)
    {
        if (string.Equals(name, "Widget", StringComparison.Ordinal))
        {
            UpdatePositionRanges(width, height);
            return;
        }

        if (string.Equals(name, "CodexRadar", StringComparison.Ordinal))
        {
            UpdateCodexRadarPositionRanges(width, height);
            return;
        }

        if (string.Equals(name, "PowerThermal", StringComparison.Ordinal))
        {
            UpdatePowerThermalPositionRanges(width, height);
            return;
        }

        if (string.Equals(name, "NetworkMonitor", StringComparison.Ordinal))
        {
            UpdateNetworkMonitorPositionRanges(width, height);
            return;
        }

        UpdateConnectionCheckPositionRanges(width, height);
    }

    private static void AssertVisibleBinding(Control control, string name)
    {
        if (control == null || control.Parent == null)
        {
            throw new InvalidOperationException(name + " is not attached to the settings panel.");
        }
    }

    private static int PickDifferentValue(NumericUpDown box)
    {
        int current = (int)box.Value;
        int min = (int)box.Minimum;
        int max = (int)box.Maximum;
        if (current < max)
        {
            return current + 1;
        }

        if (current > min)
        {
            return current - 1;
        }

        return current;
    }

    private static void SetNumber(NumericUpDown box, TrackBar slider, int value)
    {
        int clamped = Math.Max((int)box.Minimum, Math.Min((int)box.Maximum, value));
        box.Value = clamped;
        if (slider != null)
        {
            slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, clamped));
        }
    }

    private static void AssertEqual(int expected, int actual, string name)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(name + " expected " + expected.ToString(CultureInfo.InvariantCulture) + " but got " + actual.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            throw new InvalidOperationException(name + " expected " + expected + " but got " + actual + ".");
        }
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " did not satisfy the settings binding policy.");
        }
    }

    private void LoadMetricLayout(WidgetSettings settings)
    {
        if (this.metricSlotPanels == null || this.metricSlotsPanel == null)
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
        if (slot == null)
        {
            return;
        }

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
        if (this.metricSlotPanels == null ||
            index < 0 ||
            index >= this.metricSlotPanels.Length ||
            this.metricSlotPanels[index] == null)
        {
            return string.Empty;
        }

        return this.metricSlotPanels[index].Tag as string ?? string.Empty;
    }

    private void SetSlotMetric(int index, string metricId)
    {
        if (this.metricSlotPanels == null ||
            index < 0 ||
            index >= this.metricSlotPanels.Length ||
            this.metricSlotPanels[index] == null)
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

    private static string[] CloneStringArray(string[] values)
    {
        if (values == null || values.Length == 0)
        {
            return new string[0];
        }

        string[] clone = new string[values.Length];
        Array.Copy(values, clone, values.Length);
        return clone;
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

    private bool GetCodexRadarRandomTestEnabled()
    {
        return this.codexRadarRandomModeButton != null &&
            this.codexRadarRandomModeButton.Tag is bool &&
            (bool)this.codexRadarRandomModeButton.Tag;
    }

    private void SetCodexRadarRandomTestEnabled(bool enabled)
    {
        if (this.codexRadarRandomModeButton == null)
        {
            return;
        }

        this.codexRadarRandomModeButton.Tag = enabled;
        this.codexRadarRandomModeButton.Text = enabled ? "测试" : "实时";
        if (enabled)
        {
            this.codexRadarRandomModeButton.BackColor = DesignTokens.Colors.WarningSoft;
            this.codexRadarRandomModeButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.codexRadarRandomModeButton.FlatAppearance.BorderColor = DesignTokens.Colors.Warning;
            return;
        }

        this.codexRadarRandomModeButton.BackColor = DesignTokens.Colors.Control;
        this.codexRadarRandomModeButton.ForeColor = DesignTokens.Colors.Text;
        this.codexRadarRandomModeButton.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
    }

    private void UpdateCodexRadarRandomTestControls()
    {
        bool enabled = GetCodexRadarRandomTestEnabled();
        if (this.codexRadarRandomRefreshButton != null)
        {
            this.codexRadarRandomRefreshButton.Visible = enabled;
            this.codexRadarRandomRefreshButton.Enabled = enabled;
        }

        if (this.codexRadarRandomAutoRefreshCheck != null)
        {
            this.codexRadarRandomAutoRefreshCheck.Visible = enabled;
            this.codexRadarRandomAutoRefreshCheck.Enabled = enabled;
        }
    }

    private void RebuildCodexRadarModelButtons()
    {
        if (this.codexRadarModelButtonGrid == null)
        {
            return;
        }

        List<CodexRadarModelInfo> models = CodexRadarModelCatalog.LoadModels();
        string selectedKey = CodexRadarModelCatalog.NormalizeModelKey(this.selectedCodexRadarModelKey);
        if (selectedKey.Length == 0)
        {
            selectedKey = CodexRadarModelCatalog.DefaultModelKey;
        }

        bool selectedFound = false;
        for (int i = 0; i < models.Count; i++)
        {
            if (models[i] != null &&
                string.Equals(models[i].Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            {
                selectedFound = true;
                break;
            }
        }

        if (!selectedFound)
        {
            models.Add(new CodexRadarModelInfo
            {
                Key = selectedKey,
                Label = CodexRadarModelCatalog.GetDisplayLabel(string.Empty, selectedKey),
                Available = false,
                MissingCount = 1
            });
        }

        this.codexRadarModelButtonGrid.SuspendLayout();
        for (int i = this.codexRadarModelButtonGrid.Controls.Count - 1; i >= 0; i--)
        {
            Control control = this.codexRadarModelButtonGrid.Controls[i];
            this.codexRadarModelButtonGrid.Controls.RemoveAt(i);
            control.Dispose();
        }

        this.codexRadarModelButtons.Clear();
        this.codexRadarModelButtonGrid.RowStyles.Clear();
        int slotCount = Math.Max(5, ((models.Count + 4) / 5) * 5);
        this.codexRadarModelButtonGrid.RowCount = Math.Max(1, slotCount / 5);
        for (int row = 0; row < this.codexRadarModelButtonGrid.RowCount; row++)
        {
            this.codexRadarModelButtonGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        }

        for (int slot = 0; slot < slotCount; slot++)
        {
            Button button = BuildCodexRadarModelButton(slot < models.Count ? models[slot] : null);
            this.codexRadarModelButtonGrid.Controls.Add(button, slot % 5, slot / 5);
            this.codexRadarModelButtons.Add(button);
        }

        this.codexRadarModelButtonGrid.ResumeLayout(true);
        UpdateCodexRadarModelButtonStyles();
    }

    private Button BuildCodexRadarModelButton(CodexRadarModelInfo model)
    {
        Button button = new Button();
        button.Dock = DockStyle.Fill;
        button.Height = 36;
        button.Margin = new Padding(3);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Font = DesignTokens.CreateUIFont(9.0f, FontStyle.Bold);
        button.Text = "--";
        button.Tag = model;
        if (model == null)
        {
            button.Enabled = false;
            button.BackColor = DesignTokens.Colors.Control;
            button.ForeColor = DesignTokens.Colors.GlyphMuted;
            button.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
            return button;
        }

        button.Text = CodexRadarModelCatalog.GetDisplayLabel(model.Label, model.Key);
        button.Enabled = model.Available ||
            string.Equals(model.Key, this.selectedCodexRadarModelKey, StringComparison.OrdinalIgnoreCase);
        button.Click += delegate
        {
            CodexRadarModelInfo info = button.Tag as CodexRadarModelInfo;
            if (info == null || !info.Available)
            {
                return;
            }

            SetSelectedCodexRadarModelKey(info.Key);
            this.saved = false;
            QueuePreviewSettings();
        };
        return button;
    }

    private void SetSelectedCodexRadarModelKey(string key)
    {
        string normalized = CodexRadarModelCatalog.NormalizeModelKey(key);
        this.selectedCodexRadarModelKey = normalized.Length == 0
            ? CodexRadarModelCatalog.DefaultModelKey
            : normalized;
        UpdateCodexRadarModelButtonStyles();
    }

    private void UpdateCodexRadarModelButtonStyles()
    {
        string selectedKey = CodexRadarModelCatalog.NormalizeModelKey(this.selectedCodexRadarModelKey);
        for (int i = 0; i < this.codexRadarModelButtons.Count; i++)
        {
            Button button = this.codexRadarModelButtons[i];
            CodexRadarModelInfo model = button.Tag as CodexRadarModelInfo;
            if (model == null)
            {
                button.BackColor = DesignTokens.Colors.Control;
                button.ForeColor = DesignTokens.Colors.GlyphMuted;
                button.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
                continue;
            }

            bool selected = string.Equals(model.Key, selectedKey, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                button.BackColor = DesignTokens.Colors.AccentAction;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = DesignTokens.Colors.AccentBorder;
                continue;
            }

            if (!model.Available)
            {
                button.BackColor = DesignTokens.Colors.ControlPressed;
                button.ForeColor = DesignTokens.Colors.GlyphMuted;
                button.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
                continue;
            }

            button.BackColor = DesignTokens.Colors.Control;
            button.ForeColor = DesignTokens.Colors.Text;
            button.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
        }
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

    private int GetCloudEndpointTestSeed()
    {
        return this.cloudEndpointTestButton != null && this.cloudEndpointTestButton.Tag is int
            ? (int)this.cloudEndpointTestButton.Tag
            : 0;
    }

    private void SetCloudEndpointTestSeed(int seed)
    {
        this.cloudEndpointTestSeed = Math.Max(0, seed);
        if (this.cloudEndpointTestButton == null)
        {
            return;
        }

        this.cloudEndpointTestButton.Tag = this.cloudEndpointTestSeed;
        if (this.cloudEndpointTestSeed > 0)
        {
            this.cloudEndpointTestButton.Text = "恢复实时";
            this.cloudEndpointTestButton.BackColor = DesignTokens.Colors.WarningSoft;
            this.cloudEndpointTestButton.ForeColor = DesignTokens.Colors.TextOnAccent;
            this.cloudEndpointTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Warning;
            return;
        }

        this.cloudEndpointTestButton.Text = "随机状态";
        this.cloudEndpointTestButton.BackColor = DesignTokens.Colors.Control;
        this.cloudEndpointTestButton.ForeColor = DesignTokens.Colors.Text;
        this.cloudEndpointTestButton.FlatAppearance.BorderColor = DesignTokens.Colors.Border;
    }

    private string GetNetworkAdapterId()
    {
        object value = GetComboValue(this.networkAdapterCombo, string.Empty);
        return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
    }

    private void SetNetworkAdapterId(string adapterId)
    {
        adapterId = (adapterId ?? string.Empty).Trim();
        if (this.networkAdapterCombo == null)
        {
            return;
        }

        if (adapterId.Length > 0 && !ComboContainsValue(this.networkAdapterCombo, adapterId))
        {
            this.networkAdapterCombo.Items.Add(new ComboOption("已保存: " + ShortenAdapterId(adapterId), adapterId));
        }

        SelectComboValue(this.networkAdapterCombo, adapterId);
    }

    private static bool ComboContainsValue(ComboBox combo, object value)
    {
        if (combo == null)
        {
            return false;
        }

        for (int i = 0; i < combo.Items.Count; i++)
        {
            ComboOption option = combo.Items[i] as ComboOption;
            if (option != null && object.Equals(option.Value, value))
            {
                return true;
            }
        }

        return false;
    }

    private static string ShortenAdapterId(string adapterId)
    {
        if (string.IsNullOrEmpty(adapterId) || adapterId.Length <= 18)
        {
            return adapterId ?? string.Empty;
        }

        return adapterId.Substring(0, 8) + "..." + adapterId.Substring(adapterId.Length - 6);
    }

    private int GetCloudStatusRegionMask()
    {
        int mask = 0;
        if (this.cloudRegionJapanCheck != null && this.cloudRegionJapanCheck.Checked)
        {
            mask |= WidgetSettings.CloudStatusRegionJapan;
        }

        if (this.cloudRegionAsiaPacificCheck != null && this.cloudRegionAsiaPacificCheck.Checked)
        {
            mask |= WidgetSettings.CloudStatusRegionAsiaPacific;
        }

        if (this.cloudRegionNorthAmericaCheck != null && this.cloudRegionNorthAmericaCheck.Checked)
        {
            mask |= WidgetSettings.CloudStatusRegionNorthAmerica;
        }

        if (this.cloudRegionEuropeCheck != null && this.cloudRegionEuropeCheck.Checked)
        {
            mask |= WidgetSettings.CloudStatusRegionEurope;
        }

        mask &= WidgetSettings.CloudStatusRegionMaskAll;
        return mask == 0 ? WidgetSettings.DefaultCloudStatusRegionMask : mask;
    }

    private void SetCloudStatusRegionMask(int mask)
    {
        mask &= WidgetSettings.CloudStatusRegionMaskAll;
        if (mask == 0)
        {
            mask = WidgetSettings.DefaultCloudStatusRegionMask;
        }

        if (this.cloudRegionJapanCheck != null)
        {
            this.cloudRegionJapanCheck.Checked = (mask & WidgetSettings.CloudStatusRegionJapan) != 0;
        }

        if (this.cloudRegionAsiaPacificCheck != null)
        {
            this.cloudRegionAsiaPacificCheck.Checked = (mask & WidgetSettings.CloudStatusRegionAsiaPacific) != 0;
        }

        if (this.cloudRegionNorthAmericaCheck != null)
        {
            this.cloudRegionNorthAmericaCheck.Checked = (mask & WidgetSettings.CloudStatusRegionNorthAmerica) != 0;
        }

        if (this.cloudRegionEuropeCheck != null)
        {
            this.cloudRegionEuropeCheck.Checked = (mask & WidgetSettings.CloudStatusRegionEurope) != 0;
        }
    }

    private static int CreateCloudEndpointTestSeed()
    {
        return Environment.TickCount & int.MaxValue;
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
        button.FlatAppearance.MouseOverBackColor = primary ? DesignTokens.Colors.AccentBorder : DesignTokens.Colors.ControlActive;
        button.FlatAppearance.MouseDownBackColor = primary ? DesignTokens.Colors.AccentSoft : DesignTokens.Colors.ControlPressed;
        button.BackColor = primary ? DesignTokens.Colors.Accent : DesignTokens.Colors.Control;
        button.ForeColor = primary ? DesignTokens.Colors.TextOnAccent : DesignTokens.Colors.Text;
        button.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        button.UseCompatibleTextRendering = true;
        button.Margin = new Padding(DesignTokens.Spacing.SettingsButtonGap, 0, 0, 0);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        int diameter = Math.Max(1, radius * 2);
        Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private sealed class SettingsPagePanel : Panel
    {
        private readonly HashSet<Control> wheelTargets = new HashSet<Control>();

        public SettingsPagePanel()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            AttachWheelTarget(e.Control);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (ScrollByMouseWheelDelta(e.Delta))
            {
                if (e is HandledMouseEventArgs)
                {
                    ((HandledMouseEventArgs)e).Handled = true;
                }

                return;
            }

            base.OnMouseWheel(e);
        }

        public bool HasVerticalOverflow
        {
            get
            {
                return GetVerticalScrollMaximum() > 0;
            }
        }

        public int ScrollTop
        {
            get { return Math.Max(0, -this.AutoScrollPosition.Y); }
        }

        private void AttachWheelTarget(Control control)
        {
            if (control == null || control == this || !this.wheelTargets.Add(control))
            {
                return;
            }

            control.MouseWheel += OnDescendantMouseWheel;
            control.ControlAdded += OnDescendantControlAdded;
            control.Disposed += OnDescendantDisposed;
            for (int i = 0; i < control.Controls.Count; i++)
            {
                AttachWheelTarget(control.Controls[i]);
            }
        }

        private void OnDescendantControlAdded(object sender, ControlEventArgs e)
        {
            AttachWheelTarget(e.Control);
        }

        private void OnDescendantDisposed(object sender, EventArgs e)
        {
            Control control = sender as Control;
            if (control == null)
            {
                return;
            }

            control.MouseWheel -= OnDescendantMouseWheel;
            control.ControlAdded -= OnDescendantControlAdded;
            control.Disposed -= OnDescendantDisposed;
            this.wheelTargets.Remove(control);
        }

        private void OnDescendantMouseWheel(object sender, MouseEventArgs e)
        {
            if (ScrollByMouseWheelDelta(e.Delta) && e is HandledMouseEventArgs)
            {
                ((HandledMouseEventArgs)e).Handled = true;
            }
        }

        public bool ScrollByMouseWheelDelta(int delta)
        {
            if (delta == 0)
            {
                return false;
            }

            int max = GetVerticalScrollMaximum();
            if (max <= 0)
            {
                return false;
            }

            int notches = Math.Max(1, Math.Abs(delta) / 120);
            int lines = SystemInformation.MouseWheelScrollLines;
            int amount = lines <= 0
                ? Math.Max(1, this.ClientSize.Height)
                : Math.Max(24, lines * 28);
            int current = Math.Max(0, -this.AutoScrollPosition.Y);
            int next = current + (delta < 0 ? amount : -amount) * notches;
            next = Math.Max(0, Math.Min(max, next));
            if (next == current)
            {
                return true;
            }

            this.AutoScrollPosition = new Point(0, next);
            this.Invalidate();
            return true;
        }

        private int GetVerticalScrollMaximum()
        {
            int contentHeight = GetScrollableContentHeight();
            this.AutoScrollMinSize = new Size(0, contentHeight);
            return Math.Max(0, contentHeight - this.ClientSize.Height);
        }

        private int GetScrollableContentHeight()
        {
            int current = Math.Max(0, -this.AutoScrollPosition.Y);
            int bottom = this.Padding.Top + this.Padding.Bottom;
            for (int i = 0; i < this.Controls.Count; i++)
            {
                Control control = this.Controls[i];
                if (control.Visible)
                {
                    bottom = Math.Max(bottom, control.Bottom + current + control.Margin.Bottom + this.Padding.Bottom);
                }
            }

            return bottom;
        }
    }

    private sealed class SettingsNavigationPanel : FlowLayoutPanel
    {
        public SettingsNavigationPanel()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = CreateRoundedRectanglePath(bounds, DesignTokens.Radius.SettingsCard))
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.Surface))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = CreateRoundedRectanglePath(bounds, DesignTokens.Radius.SettingsCard))
            using (Pen pen = new Pen(DesignTokens.Colors.Border, 1))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        public bool ScrollByMouseWheelDelta(int delta)
        {
            if (delta == 0)
            {
                return false;
            }

            int max = GetVerticalScrollMaximum();
            if (max <= 0)
            {
                return false;
            }

            int current = Math.Max(0, -this.AutoScrollPosition.Y);
            int next = current + (delta < 0 ? 42 : -42);
            next = Math.Max(0, Math.Min(max, next));
            if (next == current)
            {
                return true;
            }

            this.AutoScrollPosition = new Point(0, next);
            this.Invalidate();
            return true;
        }

        private int GetVerticalScrollMaximum()
        {
            int contentHeight = GetScrollableContentHeight();
            this.AutoScrollMinSize = new Size(0, contentHeight);
            return Math.Max(0, contentHeight - this.ClientSize.Height);
        }

        private int GetScrollableContentHeight()
        {
            int current = Math.Max(0, -this.AutoScrollPosition.Y);
            int bottom = this.Padding.Top + this.Padding.Bottom;
            for (int i = 0; i < this.Controls.Count; i++)
            {
                Control control = this.Controls[i];
                if (control.Visible)
                {
                    bottom = Math.Max(bottom, control.Bottom + current + control.Margin.Bottom + this.Padding.Bottom);
                }
            }

            return bottom;
        }
    }

    private sealed class SettingsSectionPanel : TableLayoutPanel
    {
        public SettingsSectionPanel()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = CreateRoundedRectanglePath(bounds, DesignTokens.Radius.SettingsCard))
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.Surface))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = CreateRoundedRectanglePath(bounds, DesignTokens.Radius.SettingsCard))
            using (Pen pen = new Pen(DesignTokens.White(DesignTokens.Alpha.WeakOutline), 1))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }
    }
}
