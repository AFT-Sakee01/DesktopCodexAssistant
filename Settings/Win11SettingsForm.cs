using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class Win11SettingsForm : Form, IMessageFilter, ISettingsWindow
{
    private const int PreviewDebounceMs = 75;
    private const int WmMouseWheel = 0x020A;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int EmSetCueBanner = 0x1501;
    private const int ReferenceWorkAreaWidth = 2880;
    private const int ReferenceWorkAreaHeight = 1740;
    private const int PreferredClientWidth = 1888;
    private const int PreferredClientHeight = 1312;
    private const int MinimumClientWidth = 760;
    private const int MinimumClientHeight = 560;
    private const int ScreenMargin = 80;
    private const int DefaultNavWidth = 409;
    private const int MediumNavWidth = 300;
    private const int CompactNavWidth = 220;
    private const int NavItemHeight = 60;
    private const int ClaudeRadarModelGridColumns = 5;
    private const int ClaudeRadarModelButtonWidth = 144;
    private const int ClaudeRadarModelButtonMinimumWidth = 88;
    private const int ClaudeRadarModelButtonHeight = 38;
    private const int ClaudeRadarModelButtonGap = 8;
    private const int ClaudeRadarModelButtonMaxTextChars = 14;
    private const string ClaudeRadarModelGridName = "ClaudeRadarModelGrid";
    private const string GlobalLayoutEditCommandName = "GlobalLayoutEditCommand";
    private const string ClaudeSetupTokenCommandName = "ClaudeSetupTokenCommand";

    private static readonly Color MicaBase = DesignTokens.SettingsWarmTheme.WindowBase;
    private static readonly Color MicaLayer = DesignTokens.SettingsWarmTheme.InputBackground;
    private static readonly Color CardRest = DesignTokens.SettingsWarmTheme.CardRest;
    private static readonly Color CardHover = DesignTokens.SettingsWarmTheme.CardHover;
    private static readonly Color StrokeColor = DesignTokens.SettingsWarmTheme.DividerLines;
    private static readonly Color DividerColor = DesignTokens.SettingsWarmTheme.DividerLines;
    private static readonly Color ControlBg = DesignTokens.SettingsWarmTheme.InputBackground;
    private static readonly Color ControlBorder = DesignTokens.SettingsWarmTheme.DividerLines;
    private static readonly Color TextPrimary = DesignTokens.SettingsWarmTheme.TextPrimary;
    private static readonly Color TextSecondary = DesignTokens.SettingsWarmTheme.TextSecondary;
    private static readonly Color TextTertiary = DesignTokens.SettingsWarmTheme.TextMuted;
    private static readonly Color AccentClr = DesignTokens.SettingsWarmTheme.Accent;
    private static readonly Color AccentHover = DesignTokens.SettingsWarmTheme.AccentHover;
    private static readonly Color AccentPressed = DesignTokens.SettingsWarmTheme.AccentPressed;
    private static readonly Color ErrorClr = DesignTokens.SettingsWarmTheme.ErrorText;

    private readonly WidgetForm owner;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly Timer previewTimer;
    private readonly Timer statusTimer;
    private readonly Dictionary<string, SettingEditor> editors = new Dictionary<string, SettingEditor>(StringComparer.Ordinal);
    private readonly List<CategoryPage> pages = new List<CategoryPage>();
    private WidgetSettings baseline;
    private TableLayoutPanel bodyLayout;
    private FlowLayoutPanel navigationPanel;
    private Panel contentHost;
    private TextBox searchBox;
    private Label statusLabel;
    private int selectedPageIndex;
    private bool initializing;
    private bool saved;
    private bool dirty;
    private bool messageFilterRegistered;
    private bool unsavedPreviewConsumed;
    private static Font iconFontCache;

    public bool OwnerFormClosing { get; set; }

    public bool TryConsumeUnsavedPreview(out WidgetSettings settings)
    {
        settings = null;
        if (this.saved || this.OwnerFormClosing || this.unsavedPreviewConsumed)
        {
            return false;
        }

        this.unsavedPreviewConsumed = true;
        settings = this.baseline.Clone();
        settings.Normalize();
        return true;
    }

    // ── Constructor ──────────────────────────────────────────────────────
    public Win11SettingsForm(WidgetForm owner, WidgetSettings baseline)
    {
        this.owner = owner;
        this.baseline = baseline.Clone();
        this.baseline.Normalize();

        this.previewTimer = new Timer();
        this.previewTimer.Interval = PreviewDebounceMs;
        this.previewTimer.Tick += OnPreviewTimerTick;
        this.statusTimer = new Timer();
        this.statusTimer.Interval = 5000;
        this.statusTimer.Tick += OnStatusTimerTick;

        this.Text = "Desktop Codex Assistant 设置";
        ApplicationIcon.ApplyTo(this);
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = true;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        ApplyDynamicResolutionSizing(true);
        this.Font = GetUiFont(10.0f);
        this.BackColor = MicaBase;
        this.ForeColor = TextSecondary;

        BuildShell();
        LoadSettings(this.baseline);
    }

    // ── Form Lifecycle ───────────────────────────────────────────────────
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryEnableDwmRoundCorners();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyDynamicResolutionSizing(false);
        if (!this.messageFilterRegistered)
        {
            Application.AddMessageFilter(this);
            this.messageFilterRegistered = true;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyResponsiveShellLayout();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmDisplayChange || m.Msg == WmSettingChange)
        {
            ScheduleDynamicResolutionSizing();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (this.dirty && !this.saved && this.owner != null && !this.OwnerFormClosing)
        {
            DialogResult result = MessageBox.Show(
                this,
                "设置还没有保存。要先保存再关闭吗？",
                "未保存的更改",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == DialogResult.Yes && !TrySaveSettings())
            {
                e.Cancel = true;
                return;
            }
        }

        this.previewTimer.Stop();
        WidgetSettings revertSettings;
        if (this.owner != null && TryConsumeUnsavedPreview(out revertSettings))
        {
            try
            {
                this.owner.RevertSettings(revertSettings);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
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
        this.statusTimer.Tick -= OnStatusTimerTick;
        this.statusTimer.Dispose();
        this.fontCache.Dispose();
        base.OnFormClosed(e);
    }

    // ── IMessageFilter ───────────────────────────────────────────────────
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel || !this.Visible || this.WindowState == FormWindowState.Minimized)
        {
            return false;
        }

        if (!this.Bounds.Contains(Cursor.Position))
        {
            return false;
        }

        int delta = unchecked((short)((long)m.WParam >> 16));
        FluentScrollPanel page = GetSelectedScrollPage();
        return page != null && page.ScrollByMouseWheelDelta(delta);
    }



    // ── DWM Round Corners (Win11) ────────────────────────────────────────
    private void TryEnableDwmRoundCorners()
    {
        try
        {
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(this.Handle, 33, ref preference, sizeof(int));
        }
        catch { }
    }

    // ── Shell Layout ─────────────────────────────────────────────────────
    private void BuildShell()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill;
        root.BackColor = MicaBase;
        root.Padding = new Padding(0);
        root.ColumnCount = 1;
        root.RowCount = 3;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        this.Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Label closeBtn = new Label();
        closeBtn.Text = "✕";
        closeBtn.Font = GetUiFont(14.0f, FontStyle.Regular);
        closeBtn.ForeColor = TextSecondary;
        closeBtn.BackColor = MicaBase;
        closeBtn.Cursor = Cursors.Hand;
        closeBtn.AutoSize = false;
        closeBtn.Size = new Size(73, 51);
        closeBtn.TextAlign = ContentAlignment.MiddleCenter;
        closeBtn.Location = new Point(this.ClientSize.Width - 73, 0);
        closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeBtn.Click += delegate { this.Close(); };
        closeBtn.MouseEnter += delegate { closeBtn.BackColor = DesignTokens.SettingsWarmTheme.ErrorText; closeBtn.ForeColor = Color.White; };
        closeBtn.MouseLeave += delegate { closeBtn.BackColor = MicaBase; closeBtn.ForeColor = TextSecondary; };
        this.Controls.Add(closeBtn);
        closeBtn.BringToFront();
    }

    // ── Header ───────────────────────────────────────────────────────────
    private Control BuildHeader()
    {
        TableLayoutPanel header = new TableLayoutPanel();
        header.Dock = DockStyle.Fill;
        header.Margin = new Padding(0);
        header.BackColor = MicaBase;
        header.Padding = new Padding(51, 28, 51, 28);
        header.AutoSize = true;
        header.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        header.ColumnCount = 3;
        header.RowCount = 2;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label title = new Label();
        title.Text = "设置";
        title.AutoSize = true;
        title.Margin = new Padding(0);
        title.Padding = new Padding(0, 0, 20, 0);
        title.Font = GetUiFont(22.0f, FontStyle.Bold);
        title.ForeColor = TextPrimary;
        title.BackColor = MicaBase;
        title.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom;
        title.TextAlign = ContentAlignment.MiddleLeft;

        Label subtitle = new Label();
        subtitle.Text = "按 Windows 11 设置结构重建，提供全局跨组件配置支持";
        subtitle.AutoSize = true;
        subtitle.Margin = new Padding(0, 10, 0, 50); // 10px below title, 50px below subtitle
        subtitle.Font = GetUiFont(9.5f);
        subtitle.ForeColor = TextTertiary;
        subtitle.BackColor = MicaBase;

        this.searchBox = new TextBox();
        this.searchBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        this.searchBox.Margin = new Padding(0, 10, 0, 0);
        this.searchBox.Height = 54;
        this.searchBox.BackColor = MicaLayer;
        this.searchBox.ForeColor = TextSecondary;
        this.searchBox.BorderStyle = BorderStyle.FixedSingle;
        this.searchBox.Font = GetUiFont(10.0f);
        this.searchBox.TextChanged += delegate { ApplySearchFilter(); };
        this.searchBox.HandleCreated += delegate
        {
            try { SendMessage(this.searchBox.Handle, EmSetCueBanner, IntPtr.Zero, "\uD83D\uDD0D  搜索配置..."); }
            catch { }
        };

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(this.searchBox, 1, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.SetColumnSpan(subtitle, 3);

        // Enable Dragging
        MouseEventHandler dragHandler = delegate(object sender, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                ReleaseCapture();
                SendMessage(this.Handle, 0xA1, (IntPtr)2, null);
            }
        };
        header.MouseDown += dragHandler;
        title.MouseDown += dragHandler;
        subtitle.MouseDown += dragHandler;

        return header;
    }

    // ── Body (Navigation + Content) ──────────────────────────────────────
    private Control BuildBody()
    {
        TableLayoutPanel body = new TableLayoutPanel();
        this.bodyLayout = body;
        body.Dock = DockStyle.Fill;
        body.BackColor = MicaBase;
        body.Padding = new Padding(38, 6, 38, 0);
        body.ColumnCount = 2;
        body.RowCount = 1;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, GetNavigationWidthForClientWidth(this.ClientSize.Width)));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        this.navigationPanel = new FlowLayoutPanel();
        this.navigationPanel.Dock = DockStyle.Fill;
        this.navigationPanel.FlowDirection = FlowDirection.TopDown;
        this.navigationPanel.WrapContents = false;
        this.navigationPanel.AutoScroll = true;
        this.navigationPanel.Padding = new Padding(0, 6, 19, 6);
        this.navigationPanel.BackColor = MicaBase;

        this.contentHost = new Panel();
        this.contentHost.Dock = DockStyle.Fill;
        this.contentHost.BackColor = MicaBase;
        this.contentHost.Margin = new Padding(0);

        body.Controls.Add(this.navigationPanel, 0, 0);
        body.Controls.Add(this.contentHost, 1, 0);

        BuildPages();
        SelectPage(0);
        return body;
    }

    // ── Footer ───────────────────────────────────────────────────────────
    private Control BuildFooter()
    {
        TableLayoutPanel footer = new TableLayoutPanel();
        footer.Dock = DockStyle.Fill;
        footer.BackColor = MicaBase;
        footer.Padding = new Padding(51, 19, 51, 12);
        footer.AutoSize = true;
        footer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        footer.ColumnCount = 4;
        footer.RowCount = 1;
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Button reset = BuildCommandButton("重置为默认", false, DesignTokens.Colors.Warning);
        reset.Dock = DockStyle.Top;
        reset.Click += delegate
        {
            WidgetSettings defaults = WidgetSettings.CreateDefaults();
            LoadSettings(defaults);
            this.saved = false;
            SetDirtyState(true);
            if (this.owner != null)
            {
                this.owner.PreviewSettings(ReadSettings());
            }
        };

        this.statusLabel = new Label();
        this.statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        this.statusLabel.Font = GetUiFont(9.5f, FontStyle.Bold);
        this.statusLabel.ForeColor = AccentClr;
        this.statusLabel.BackColor = MicaBase;
        this.statusLabel.Dock = DockStyle.Fill;
        this.statusLabel.Padding = new Padding(22, 0, 19, 0);
        this.statusLabel.Visible = false;

        Button cancel = BuildCommandButton("取消", false);
        cancel.Dock = DockStyle.Top;
        cancel.Click += delegate { this.Close(); };

        Button save = BuildCommandButton("保存", true);
        save.Dock = DockStyle.Top;
        save.Click += delegate
        {
            if (TrySaveSettings())
            {
                ShowStatus("保存完成", SettingsStatusSeverity.Success);
            }
        };

        footer.Controls.Add(reset, 0, 0);
        footer.Controls.Add(this.statusLabel, 1, 0);
        footer.Controls.Add(cancel, 2, 0);
        footer.Controls.Add(save, 3, 0);
        return footer;
    }

    private Button BuildCommandButton(string text, bool primary)
    {
        return SettingsFluentResources.CreateCommandButton(text, primary, GetUiFont(9.5f, FontStyle.Bold));
    }

    private Button BuildCommandButton(string text, bool primary, Color outlineAccent)
    {
        return SettingsFluentResources.CreateCommandButton(text, primary, GetUiFont(9.5f, FontStyle.Bold), outlineAccent);
    }

    // ── Pages Definition ─────────────────────────────────────────────────
    // Each page uses AddPageGrouped with string[][] where each inner array
    // is [groupTitle, property1, property2, ...].
    private void BuildPages()
    {
        // 分组名以 '!' 开头 = 收进该页底部的「复杂选项」折叠区。
        AddPageGrouped("\uE115", "系统", "开机启动、性能和窗口基础行为。", new string[][]
        {
            new string[] { "启动与性能", "StartupEnabled", "PerformanceMode" },
            new string[] { "窗口行为", "VisibilityMode", "VisibilityOverlapIgnoresOperationPanelEnabled", "ClickThroughMode" },
            new string[] { "AI 请求阻断", "AiRequestProtectionAutoEnabled", "AiRequestProtectionManualBlockEnabled" },
            new string[] { "!Codex 额度计划", "CodexQuotaPlanEnabled", "CodexQuotaPlanWeeklyComparison", "CodexQuotaPlanWeeklyThresholdPercent",
                           "CodexQuotaPlanFiveHourComparison", "CodexQuotaPlanFiveHourThresholdPercent", "CodexQuotaPlanResumeConditionMode",
                           "CodexQuotaPlanAutoResumePausedGoals", "CodexQuotaPlanPauseGoalIds", "CodexQuotaPlanResumeGoalIds" },
            new string[] { "!恢复与保护", "SeelenDockForegroundPulseEnabled", "WinDRecoveryPulseEnabled", "PowerResumeRestartEnabled" },
            new string[] { "!调试", "ForceShowForegroundFpsEnabled" }
        });

        AddPageGrouped("\uE7C2", "布局与位置", "所有浮窗的位置、大小和所在显示器；推荐用可视化编辑直接拖拽。", new string[][]
        {
            new string[] { "可视化编辑", GlobalLayoutEditCommandName },
            new string[] { "分辨率兼容", "ResolutionCompatibilityModeEnabled", "ResolutionCompatibilityScalePercent" },
            new string[] { "显示器分配", "FallbackDisconnectedDisplaysEnabled", "MainDisplayDeviceName", "CodexRadarDisplayDeviceName",
                           "ClaudeRadarDisplayDeviceName", "PowerThermalDisplayDeviceName", "NetworkMonitorDisplayDeviceName", "ConnectionCheckDisplayDeviceName", "OperationDisplayDeviceName" },
            new string[] { "!主窗口位置", "Width", "Height", "LeftX", "BottomY" },
            new string[] { "!Codex Radar 位置", "CodexRadarWidth", "CodexRadarHeight", "CodexRadarLeftX", "CodexRadarBottomY" },
            new string[] { "!Claude Radar 位置", "ClaudeRadarWidth", "ClaudeRadarHeight", "ClaudeRadarLeftX", "ClaudeRadarBottomY" },
            new string[] { "!功耗温度位置", "PowerThermalWidth", "PowerThermalHeight", "PowerThermalLeftX", "PowerThermalBottomY" },
            new string[] { "!网络监控位置", "NetworkMonitorWidth", "NetworkMonitorHeight", "NetworkMonitorLeftX", "NetworkMonitorBottomY" },
            new string[] { "!连接检测位置", "ConnectionCheckWidth", "ConnectionCheckHeight", "ConnectionCheckLeftX", "ConnectionCheckBottomY" },
            new string[] { "!操作面板位置", "OperationLeftOffset", "OperationBottomOffset" }
        });

        AddPageGrouped("\uE7B3", "隐藏与防烧屏", "鼠标靠近时隐藏、空闲自动隐藏和 OLED 防烧屏。", new string[][]
        {
            new string[] { "鼠标靠近时隐藏", "HoverOpacityEnabled", "SensitiveMouseModeEnabled", "SensitiveMouseRangePixels" },
            new string[] { "自动隐藏", "AutoHoverOpacityIdleEnabled", "AutoHoverOpacityIdleSeconds", "AutoHoverOpacityMaximizedEnabled", "OperationRadialCoreAutoHideKeepAliveEnabled", "OperationRadialIdleCollapseSeconds", "OperationRadialIdleResetOnInteractionEnabled", "OperationRadialKeepOpenAfterLeafClickEnabled", "BurnInHiddenModeColorProtectionEnabled" },
            new string[] { "!延迟显现", "HoverOpacityRevealDelayEnabled", "HoverOpacityRevealDelaySeconds", "HoverOpacityRevealResetSeconds" },
            new string[] { "!覆盖与反向", "HoverOpacityCoverEnabled", "ReverseHoverOpacityRevealEnabled", "ReverseHoverOpacityRestoreDelaySeconds" }
        });

        AddPageGrouped("\uE737", "主窗口", "主监控窗口显示哪些指标、透明度和外观。", new string[][]
        {
            new string[] { "显示哪些指标", "ShowCpu", "ShowMemory", "ShowDisk", "ShowNetwork", "ShowGpu", "ShowNpu" },
            new string[] { "透明度", "BackgroundTransparencyPercent", "ApplicationTransparencyPercent" }
        });

        AddPageGrouped("\uE121", "Radar 通用", "Codex / Claude Radar 共用的时钟显示和时区。", new string[][]
        {
            new string[] { "时钟显示", "RadarClockTimeDisplayMode", "RadarClockAutoSwitchModelEnabled" },
            new string[] { "显示时区", "DisplayTimeZoneMode", "DisplayTimeZoneId" }
        });

        AddPageGrouped("\uE71E", "Codex Radar", "共享 Radar 小窗：可按前台自动显示 Codex 或 Claude，也可固定检测对象。", new string[][]
        {
            // 手动布局与元素偏移分组已从设置界面隐藏（改用渲染变体切换布局）。
            // 对应 WidgetSettings 属性仍保留，Classic 布局若通过 settings.ini 设置仍生效；
            // 均布变体本就忽略这些设置。恢复入口时把下方分组加回，并同步 VerifySelfTest 必需绑定。
            new string[] { "共享小窗", "CodexRadarEnabled", "CodexRadarSoftwareMode", "CodexRadarTransparencyPercent" },
            new string[] { "CODEX 模式数据", "CodexRadarModelKey" },
            new string[] { "!CodexRadar.com 读取链路", "CodexRadarPublicJsonEnabled", "CodexRadarHtmlFallbackEnabled", "CodexRadarRssFallbackEnabled", "CodexRadarServiceProbeToken" },
            new string[] { "!额度保护", "CodexQuotaDueResetProtectionEnabled", "CodexQuotaRssResetProtectionEnabled",
                           "CodexQuotaProviderZeroDropProtectionEnabled", "CodexQuotaDuplicateSameBalanceRingProtectionEnabled",
                           "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled", "CodexQuotaProviderWeeklySpikeProtectionEnabled",
                           "CodexQuotaStrictFiveHourResetBoundaryEnabled", "CodexQuotaWeeklyBaselineAutoRepairEnabled" },
            new string[] { "!兼容模型设置", "CodexRadarModelVersion" },
            new string[] { "!IQ 测试覆盖", "CodexModelIqTestEnabled", "CodexModelIqTestPassed", "CodexModelIqBaselineAutoEnabled", "CodexModelIqBaselinePassed", "CodexModelIqBaselineValidTasks" },
            new string[] { "!效率测试覆盖", "CodexModelEfficiencyTestEnabled", "CodexModelTokenEfficiencyTestPercent", "CodexModelTimeEfficiencyTestPercent",
                           "CodexModelTokenEfficiencyBaselineMode", "CodexModelTokenEfficiencyBaselinePassed", "CodexModelTokenEfficiencyBaselineTokens",
                           "CodexModelTimeEfficiencyBaselineMode", "CodexModelTimeEfficiencyBaselinePassed", "CodexModelTimeEfficiencyBaselineSeconds",
                           "CodexModelTokenEfficiencyLowThresholdPercent", "CodexModelTimeEfficiencyLowThresholdPercent" },
            new string[] { "!随机测试", "CodexRadarRandomTestEnabled", "CodexRadarRandomTestAutoRefresh", "CodexRadarRandomTestRefreshToken" }
        });

        AddPageGrouped("\uE8D4", "Claude Radar", "Claude 相关数据：独立 Claude 小窗，以及共享 Radar 小窗的 CLAUDE 模式辅助状态。", new string[][]
        {
            new string[] { "独立小窗", "ClaudeRadarEnabled", "ClaudeRadarTransparencyPercent" },
            new string[] { "Claude 模型", "ClaudeRadarModelKey" },
            new string[] { "Claude 数据链路", "ClaudeRadarJsonEnabled", "ClaudeRadarCommunityRatingsEnabled", "ClaudeRadarLocalQuotaFallbackEnabled" },
            new string[] { "Claude Code 用量令牌", ClaudeSetupTokenCommandName },
            new string[] { "DeepSeek 余额", "DeepSeekApiKeyRevision" },
            new string[] { "!元数据与诊断", "ClaudeRadarHomepageFallbackEnabled", "ClaudeRadarServiceProbeToken" },
            new string[] { "!随机测试", "ClaudeRadarRandomTestEnabled", "ClaudeRadarRandomTestAutoRefresh", "ClaudeRadarRandomTestRefreshToken" }
        });

        AddPageGrouped("\uEBB0", "功耗与温度", "UX3407N / UX3607O 专用功耗温度窗口。", new string[][]
        {
            new string[] { "自动布局与告警", "PowerThermalAutoSizeEnabled", "PowerThermalAutoDirection", "PowerThermalVisibleAlertCount" },
            new string[] { "电池与节能", "PowerThermalManualEnergySaverThresholdPercent" },
            new string[] { "透明度", "PowerThermalTransparencyPercent" },
            new string[] { "!测试", "ThermalTestMode" }
        });

        AddPageGrouped("\uE774", "网络", "网络监控、GFW 检测、云服务和出口身份。", new string[][]
        {
            new string[] { "网络监控", "NetworkMonitorAdapterId", "NetworkMonitorTransparencyPercent" },
            new string[] { "GFW 检测", "GfwProbeEnabled", "GfwProbeIntervalMinutes" },
            new string[] { "连接检测", "ConnectionCheckIntervalSeconds", "ConnectionCheckTransparencyPercent", "ConnectionCheckBorderTransparencyPercent" },
            new string[] { "!手动刷新", "GfwProbeManualRefreshToken", "ConnectionCheckManualRefreshToken" },
            new string[] { "!云服务端点", "CloudEndpointTestSeed", "CloudStatusRegionMask" },
            new string[] { "!测试", "CleanIpBadgeTestMode", "NetworkStatusTestMode" }
        });

        AddPageGrouped("\uE700", "操作面板", "左下角操作面板的按钮、透明度和外观。", new string[][]
        {
            new string[] { "按钮与面板", "OperationButtonSize", "OperationPrimaryPanelMode", "OperationSettingsLogicExtensionEnabled", "OperationBackgroundTransparencyPercent" },
            new string[] { "外观风格", "OperationRenderVariant" },
            new string[] { "!测试", "AlertTestEnabled" }
        });
    }

    // ── AddPageGrouped ───────────────────────────────────────────────────
    private void AddPageGrouped(string icon, string title, string description, string[][] groups)
    {
        int pageIndex = this.pages.Count;
        CategoryPage page = new CategoryPage();
        page.Title = title;
        page.Description = description;

        page.ScrollPanel = new FluentScrollPanel();
        page.ScrollPanel.Dock = DockStyle.Fill;
        page.ScrollPanel.BackColor = MicaBase;
        page.ScrollPanel.AutoScroll = true;
        page.ScrollPanel.Padding = new Padding(0, 0, 16, 0);
        page.ScrollPanel.Visible = false;

        FlowLayoutPanel stack = new FlowLayoutPanel();
        stack.Dock = DockStyle.Top;
        stack.FlowDirection = FlowDirection.TopDown;
        stack.WrapContents = false;
        stack.AutoSize = true;
        stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        stack.BackColor = MicaBase;
        page.Stack = stack;
        page.ScrollPanel.Controls.Add(stack);

        Panel heading = BuildPageHeading(title, description);
        page.Heading = heading;
        stack.Controls.Add(heading);

        for (int g = 0; g < groups.Length; g++)
        {
            string[] groupDef = groups[g];
            string groupTitle = groupDef[0];
            // A leading '!' marks the group as advanced: it renders collapsed under the
            // per-page "复杂选项" header until the user expands it (or searches).
            bool advanced = groupTitle.Length > 0 && groupTitle[0] == '!';
            if (advanced)
            {
                groupTitle = groupTitle.Substring(1);
            }

            if (advanced && page.AdvancedHeader == null)
            {
                AdvancedSectionHeader advancedHeader = new AdvancedSectionHeader(GetUiFont(10.0f, FontStyle.Bold), GetUiFont(8.5f));
                advancedHeader.Width = 1152;
                advancedHeader.Height = 64;
                advancedHeader.Margin = new Padding(0, 32, 0, 6);
                advancedHeader.Click += delegate { ToggleAdvancedSection(page); };
                page.AdvancedHeader = advancedHeader;
                stack.Controls.Add(advancedHeader);
            }

            SettingGroupData group = new SettingGroupData();
            group.Title = groupTitle;
            group.Advanced = advanced;

            // Group title label (above the card, Win11 style)
            Label titleLabel = new Label();
            titleLabel.Text = groupTitle;
            titleLabel.Font = GetUiFont(10.0f, FontStyle.Bold);
            titleLabel.ForeColor = TextPrimary;
            titleLabel.BackColor = MicaBase;
            titleLabel.AutoSize = false;
            titleLabel.Width = 1152;
            titleLabel.Height = 44;
            titleLabel.Margin = new Padding(0, g == 0 ? 0 : 18, 0, 4);
            titleLabel.TextAlign = ContentAlignment.BottomLeft;
            titleLabel.Visible = !advanced;
            group.TitleLabel = titleLabel;
            stack.Controls.Add(titleLabel);

            // Group card (rounded background, contains rows)
            SettingGroupCard card = new SettingGroupCard();
            card.Width = 1152;
            card.Margin = new Padding(0, 0, 0, 3);
            card.Visible = !advanced;
            group.Card = card;

            for (int i = 1; i < groupDef.Length; i++)
            {
                SettingEditor editor = BuildEditor(groupDef[i]);
                if (editor != null)
                {
                    group.Editors.Add(editor);
                    page.Editors.Add(editor);
                    card.AddRow(editor.Card);
                    this.editors[editor.Name] = editor;
                }
            }

            card.LayoutRows();
            stack.Controls.Add(card);
            page.Groups.Add(group);
        }

        if (page.AdvancedHeader != null)
        {
            page.AdvancedHeader.AdvancedRowCount = CountAdvancedRows(page);
        }

        page.ScrollPanel.Resize += delegate { LayoutPage(page); };
        NavigationItem nav = new NavigationItem(icon, title);
        nav.Width = Math.Max(160, GetNavigationWidthForClientWidth(this.ClientSize.Width) - 24);
        nav.Height = NavItemHeight;
        nav.Margin = new Padding(6, 3, 6, 3);
        nav.Font = GetUiFont(10.0f);
        nav.Cursor = Cursors.Hand;
        nav.Click += delegate { SelectPage(pageIndex); };
        page.NavItem = nav;
        this.navigationPanel.Controls.Add(nav);
        this.contentHost.Controls.Add(page.ScrollPanel);
        this.pages.Add(page);
    }

    private static int CountAdvancedRows(CategoryPage page)
    {
        int count = 0;
        for (int g = 0; g < page.Groups.Count; g++)
        {
            if (page.Groups[g].Advanced)
            {
                count += page.Groups[g].Editors.Count;
            }
        }

        return count;
    }

    private void ToggleAdvancedSection(CategoryPage page)
    {
        page.AdvancedExpanded = !page.AdvancedExpanded;
        if (page.AdvancedHeader != null)
        {
            page.AdvancedHeader.Expanded = page.AdvancedExpanded;
        }

        ApplyAdvancedVisibility(page);
        LayoutPage(page);
    }

    private void ApplyAdvancedVisibility(CategoryPage page)
    {
        string query = (this.searchBox == null ? string.Empty : this.searchBox.Text ?? string.Empty).Trim();
        if (query.Length > 0)
        {
            // Search visibility is authoritative while a query is active.
            ApplySearchFilter();
            return;
        }

        for (int g = 0; g < page.Groups.Count; g++)
        {
            SettingGroupData group = page.Groups[g];
            if (!group.Advanced)
            {
                continue;
            }

            if (group.TitleLabel != null)
            {
                group.TitleLabel.Visible = page.AdvancedExpanded;
            }

            if (group.Card != null)
            {
                group.Card.Visible = page.AdvancedExpanded;
                if (page.AdvancedExpanded)
                {
                    group.Card.LayoutRows();
                }
            }
        }
    }

    // ── Page Heading ─────────────────────────────────────────────────────
    private Panel BuildPageHeading(string titleText, string description)
    {
        Panel panel = new Panel();
        panel.BackColor = MicaBase;
        panel.Width = 1152;
        panel.Height = 188;
        panel.Margin = new Padding(0, 0, 0, 32);

        Label title = new Label();
        title.Text = titleText;
        title.Font = GetUiFont(18.0f, FontStyle.Bold);
        title.ForeColor = TextPrimary;
        title.BackColor = MicaBase;
        title.Location = new Point(0, 0);
        title.Size = new Size(680, GetSingleLineHeight(title.Font, 14));
        title.TextAlign = ContentAlignment.MiddleLeft;

        Label subtitle = new Label();
        subtitle.Text = description;
        subtitle.Font = GetUiFont(9.5f);
        subtitle.ForeColor = TextTertiary;
        subtitle.BackColor = MicaBase;
        subtitle.Location = new Point(1, title.Bottom + 16);
        subtitle.Size = new Size(680, GetSingleLineHeight(subtitle.Font, 10));
        subtitle.TextAlign = ContentAlignment.MiddleLeft;

        panel.Resize += delegate
        {
            int width = Math.Max(416, panel.Width - 6);
            title.Width = width;
            subtitle.Width = width;
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    // ── Editor Building ──────────────────────────────────────────────────
    private SettingEditor BuildEditor(string propertyName)
    {
        if (string.Equals(propertyName, GlobalLayoutEditCommandName, StringComparison.Ordinal))
        {
            return BuildGlobalLayoutEditEditor();
        }

        if (string.Equals(propertyName, ClaudeSetupTokenCommandName, StringComparison.Ordinal))
        {
            return BuildClaudeSetupTokenEditor();
        }

        PropertyInfo property = typeof(WidgetSettings).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property == null || !property.CanRead || !property.CanWrite || property.PropertyType == typeof(string[]))
        {
            return null;
        }

        Control control = BuildValueControl(property);
        SettingRow card = new SettingRow(control, GetUiFont(10.0f), GetUiFont(8.5f));
        card.Width = 1152;
        card.Margin = new Padding(0);
        card.TitleLabel.Text = GetSettingTitle(propertyName);
        card.HintLabel.Text = GetSettingHint(propertyName);
        card.BackColor = Color.Transparent;
        // The model button grid wraps to multiple rows on its own and doesn't fit alongside the
        // title/hint text in the normal side-by-side column - stack it below instead of squeezing.
        if (string.Equals(propertyName, "ClaudeRadarModelKey", StringComparison.Ordinal))
        {
            card.ForceCompactLayout = true;
        }

        return new SettingEditor(property, card, control);
    }

    private SettingEditor BuildGlobalLayoutEditEditor()
    {
        Button button = BuildCommandButton("全局编辑", false);
        button.Width = 227;
        button.Height = 54;
        button.Click += delegate { OpenGlobalLayoutEditorFromSettings(); };

        SettingRow card = new SettingRow(button, GetUiFont(10.0f), GetUiFont(8.5f));
        card.Width = 1152;
        card.Margin = new Padding(0);
        card.TitleLabel.Text = GetSettingTitle(GlobalLayoutEditCommandName);
        card.HintLabel.Text = GetSettingHint(GlobalLayoutEditCommandName);
        card.BackColor = Color.Transparent;
        return new SettingEditor(GlobalLayoutEditCommandName, card, button);
    }

    private SettingEditor BuildClaudeSetupTokenEditor()
    {
        Button button = BuildCommandButton(GetClaudeSetupTokenButtonText(), false, GetClaudeSetupTokenAccentColor());
        button.Width = 227;
        button.Height = 54;
        button.Click += delegate { OpenClaudeSetupTokenDialog(button); };

        SettingRow card = new SettingRow(button, GetUiFont(10.0f), GetUiFont(8.5f));
        card.Width = 1152;
        card.Margin = new Padding(0);
        card.TitleLabel.Text = GetSettingTitle(ClaudeSetupTokenCommandName);
        card.HintLabel.Text = GetSettingHint(ClaudeSetupTokenCommandName);
        card.BackColor = Color.Transparent;
        return new SettingEditor(ClaudeSetupTokenCommandName, card, button);
    }

    private void OpenClaudeSetupTokenDialog(Button sourceButton)
    {
        Form dialog = new Form();
        try
        {
            dialog.Text = "Claude Code 用量令牌";
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.ShowInTaskbar = false;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.AutoScaleMode = AutoScaleMode.None;
            dialog.BackColor = MicaBase;
            dialog.ForeColor = TextSecondary;
            dialog.Font = GetUiFont(9.5f);

            // Every control's Y is accumulated from the previous control's *measured* height
            // (via GetSingleLineHeight/GetWrappedTextHeight) rather than guessed constants -
            // guessed row heights overlapped once the actual rendered font metrics differed
            // from what was assumed at a different DPI/text-scale setting.
            const int marginLeft = 24;
            const int contentWidth = 572;
            int y = 20;

            Font titleFont = GetUiFont(12.0f, FontStyle.Bold);
            Label title = new Label();
            title.Text = "生成并粘贴 setup-token";
            title.Font = titleFont;
            title.ForeColor = TextPrimary;
            title.BackColor = MicaBase;
            title.Location = new Point(marginLeft, y);
            title.Size = new Size(contentWidth, GetSingleLineHeight(titleFont, 8));
            title.TextAlign = ContentAlignment.MiddleLeft;
            y += title.Height + 8;

            Font hintFont = GetUiFont(8.8f);
            string hintText = "Claude 桌面版不会主动上报用量数据。先在下面复制命令，在任意 PowerShell 窗口运行并登录授权，" +
                "把打印出的一次性长效令牌粘贴到下方输入框保存。未配置时两个额度环会显示满环红色。";
            Label hint = new Label();
            hint.Text = hintText;
            hint.Font = hintFont;
            hint.ForeColor = TextTertiary;
            hint.BackColor = MicaBase;
            hint.Location = new Point(marginLeft, y);
            hint.Size = new Size(contentWidth, GetWrappedTextHeight(hintText, hintFont, contentWidth, 8));
            hint.TextAlign = ContentAlignment.TopLeft;
            y += hint.Height + 18;

            Font sectionFont = GetUiFont(9.0f, FontStyle.Bold);
            Label commandLabel = new Label();
            commandLabel.Text = "第 1 步：复制并在 PowerShell 中运行";
            commandLabel.Font = sectionFont;
            commandLabel.ForeColor = TextPrimary;
            commandLabel.BackColor = MicaBase;
            commandLabel.Location = new Point(marginLeft, y);
            commandLabel.Size = new Size(contentWidth, GetSingleLineHeight(sectionFont, 6));
            commandLabel.TextAlign = ContentAlignment.MiddleLeft;
            y += commandLabel.Height + 6;

            int commandRowHeight = 34;
            int copyButtonWidth = 112;
            int gapBeforeCopy = 10;
            int commandBoxWidth = contentWidth - copyButtonWidth - gapBeforeCopy;

            TextBox commandBox = new TextBox();
            commandBox.Location = new Point(marginLeft, y);
            commandBox.Size = new Size(commandBoxWidth, commandRowHeight);
            commandBox.BackColor = ControlBg;
            commandBox.ForeColor = TextSecondary;
            commandBox.BorderStyle = BorderStyle.FixedSingle;
            commandBox.Font = GetUiFont(8.8f);
            commandBox.ReadOnly = true;
            commandBox.Text = BuildClaudeSetupTokenCommandText();

            Button copyCommand = BuildCommandButton("复制命令", false);
            copyCommand.Location = new Point(marginLeft + commandBoxWidth + gapBeforeCopy, y);
            copyCommand.Width = copyButtonWidth;
            copyCommand.Height = commandRowHeight;
            copyCommand.Click += delegate
            {
                try
                {
                    Clipboard.SetText(commandBox.Text);
                    ShowStatus("命令已复制", SettingsStatusSeverity.Success);
                }
                catch (Exception ex)
                {
                    Program.LogException(ex);
                    ShowStatus("复制失败", SettingsStatusSeverity.Error);
                }
            };
            y += commandRowHeight + 18;

            Label tokenLabel = new Label();
            tokenLabel.Text = "第 2 步：粘贴命令打印出的令牌";
            tokenLabel.Font = sectionFont;
            tokenLabel.ForeColor = TextPrimary;
            tokenLabel.BackColor = MicaBase;
            tokenLabel.Location = new Point(marginLeft, y);
            tokenLabel.Size = new Size(contentWidth, GetSingleLineHeight(sectionFont, 6));
            tokenLabel.TextAlign = ContentAlignment.MiddleLeft;
            y += tokenLabel.Height + 6;

            TextBox tokenBox = new TextBox();
            tokenBox.Location = new Point(marginLeft, y);
            tokenBox.Size = new Size(contentWidth, commandRowHeight);
            tokenBox.BackColor = ControlBg;
            tokenBox.ForeColor = TextSecondary;
            tokenBox.BorderStyle = BorderStyle.FixedSingle;
            tokenBox.Font = GetUiFont(9.5f);
            tokenBox.UseSystemPasswordChar = true;
            y += commandRowHeight + 14;

            Font statusFont = GetUiFont(8.8f, FontStyle.Bold);
            Label statusHint = new Label();
            statusHint.AutoSize = false;
            statusHint.Font = statusFont;
            statusHint.Location = new Point(marginLeft, y);
            statusHint.Size = new Size(contentWidth, GetSingleLineHeight(statusFont, 4));
            statusHint.BackColor = MicaBase;
            statusHint.TextAlign = ContentAlignment.MiddleLeft;
            ApplyClaudeSetupTokenStatusHint(statusHint);
            y += statusHint.Height + 20;

            int buttonHeight = 44;
            int buttonWidth = 112;
            int buttonGap = 12;

            Button clear = BuildCommandButton("清除", false);
            clear.Location = new Point(marginLeft, y);
            clear.Width = buttonWidth;
            clear.Height = buttonHeight;

            Button save = BuildCommandButton("保存", true);
            save.Width = buttonWidth;
            save.Height = buttonHeight;
            save.Location = new Point(marginLeft + contentWidth - buttonWidth, y);

            Button cancel = BuildCommandButton("取消", false);
            cancel.Width = buttonWidth;
            cancel.Height = buttonHeight;
            cancel.Location = new Point(save.Left - buttonGap - buttonWidth, y);
            cancel.Click += delegate { dialog.Close(); };

            y += buttonHeight + 20;
            dialog.ClientSize = new Size(marginLeft * 2 + contentWidth, y);

            clear.Click += delegate
            {
                string errorCode;
                if (TrySaveClaudeSetupTokenFile(string.Empty, out errorCode))
                {
                    tokenBox.Text = string.Empty;
                    ApplyClaudeSetupTokenStatusHint(statusHint);
                    RefreshClaudeSetupTokenButton(sourceButton);
                    ShowStatus("Claude 用量令牌已清除", SettingsStatusSeverity.Warning);
                    return;
                }

                ShowStatus("Claude 用量令牌清除失败 " + errorCode, SettingsStatusSeverity.Error);
            };

            save.Click += delegate
            {
                string errorCode;
                if (TrySaveClaudeSetupTokenFile(tokenBox.Text, out errorCode))
                {
                    ApplyClaudeSetupTokenStatusHint(statusHint);
                    RefreshClaudeSetupTokenButton(sourceButton);
                    ShowStatus("Claude 用量令牌已保存", SettingsStatusSeverity.Success);
                    dialog.Close();
                    return;
                }

                ShowStatus("Claude 用量令牌保存失败 " + errorCode, SettingsStatusSeverity.Error);
            };

            dialog.Controls.Add(title);
            dialog.Controls.Add(hint);
            dialog.Controls.Add(commandLabel);
            dialog.Controls.Add(commandBox);
            dialog.Controls.Add(copyCommand);
            dialog.Controls.Add(tokenLabel);
            dialog.Controls.Add(tokenBox);
            dialog.Controls.Add(statusHint);
            dialog.Controls.Add(clear);
            dialog.Controls.Add(cancel);
            dialog.Controls.Add(save);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            dialog.ShowDialog(this);
        }
        finally
        {
            dialog.Dispose();
        }
    }

    private void RefreshClaudeSetupTokenButton(Button sourceButton)
    {
        if (sourceButton != null)
        {
            sourceButton.Text = GetClaudeSetupTokenButtonText();
        }

        OnSettingChanged();
    }

    private static void ApplyClaudeSetupTokenStatusHint(Label statusHint)
    {
        if (statusHint == null)
        {
            return;
        }

        bool configured = IsClaudeSetupTokenConfiguredForUi();
        statusHint.Text = configured ? "状态：已配置" : "状态：未配置（额度环将显示满环红色）";
        statusHint.ForeColor = configured ? DesignTokens.Colors.Success : DesignTokens.Colors.Danger;
    }

    private static string GetClaudeSetupTokenButtonText()
    {
        return IsClaudeSetupTokenConfiguredForUi() ? "已配置 · 修改" : "未配置 · 立即设置";
    }

    private static Color GetClaudeSetupTokenAccentColor()
    {
        return IsClaudeSetupTokenConfiguredForUi() ? DesignTokens.Colors.Success : DesignTokens.Colors.Danger;
    }

    private static bool IsClaudeSetupTokenConfiguredForUi()
    {
        try
        {
            return ClaudeCodeUsageReader.ReadConfiguredSetupToken().Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    // Dynamically resolves the installed claude-code CLI version directory instead of hardcoding
    // it - the Claude desktop app updates this bundled CLI independently of our own releases.
    private static string BuildClaudeSetupTokenCommandText()
    {
        return "$dir = Get-ChildItem \"$env:APPDATA\\Claude\\claude-code\" -Directory | " +
            "Sort-Object Name -Descending | Select-Object -First 1; " +
            "& \"$($dir.FullName)\\claude.exe\" setup-token";
    }

    private static bool TrySaveClaudeSetupTokenFile(string token, out string errorCode)
    {
        errorCode = string.Empty;
        try
        {
            string trimmed = (token ?? string.Empty).Trim().Trim('"', '\'');
            string path = ClaudeCodeUsageReader.SetupTokenFilePath;
            if (trimmed.Length == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, trimmed, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            errorCode = "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
            return false;
        }
    }

    private Control BuildValueControl(PropertyInfo property)
    {
        Type type = property.PropertyType;
        if (type == typeof(bool))
        {
            ToggleSwitch toggle = new ToggleSwitch();
            toggle.CheckedChanged += delegate { OnSettingChanged(); };
            return toggle;
        }

        if (type.IsEnum)
        {
            if (property.Name.EndsWith("RenderVariant", StringComparison.Ordinal) &&
                !property.Name.StartsWith("ClaudeRadar", StringComparison.Ordinal))
            {
                return BuildVariantPicker(property);
            }

            ComboBox combo = new ComboBox();
            combo.Width = 352;
            combo.Height = 54;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = ControlBg;
            combo.ForeColor = TextSecondary;
            combo.Font = GetUiFont(9.5f);
            Array values = Enum.GetValues(type);
            for (int i = 0; i < values.Length; i++)
            {
                combo.Items.Add(new EnumOption(values.GetValue(i)));
            }
            combo.SelectedIndexChanged += delegate { OnSettingChanged(); };
            return combo;
        }

        if (type == typeof(string) && string.Equals(property.Name, "CodexRadarModelKey", StringComparison.Ordinal))
        {
            return BuildCodexRadarModelCombo();
        }

        if (type == typeof(string) && string.Equals(property.Name, "ClaudeRadarModelKey", StringComparison.Ordinal))
        {
            return BuildClaudeRadarModelGridSelector();
        }

        if (type == typeof(string) && IsDisplayDeviceSetting(property.Name))
        {
            return BuildDisplayDeviceCombo();
        }

        if (type == typeof(int) || type == typeof(double))
        {
            if (string.Equals(property.Name, "ResolutionCompatibilityScalePercent", StringComparison.Ordinal))
            {
                PercentSliderControl slider = new PercentSliderControl(GetUiFont(9.0f, FontStyle.Bold));
                slider.Width = 400;
                slider.Height = 54;
                slider.Minimum = WidgetSettings.MinResolutionCompatibilityScalePercent;
                slider.Maximum = WidgetSettings.MaxResolutionCompatibilityScalePercent;
                slider.ValueChanged += delegate { OnSettingChanged(); };
                return slider;
            }

            if (string.Equals(property.Name, "DeepSeekApiKeyRevision", StringComparison.Ordinal))
            {
                Button button = BuildCommandButton(GetDeepSeekApiKeyButtonText(), false);
                button.Width = 227;
                button.Height = 54;
                button.Tag = 0;
                button.Click += delegate { OpenDeepSeekApiKeyDialog(button); };
                return button;
            }

            if (string.Equals(property.Name, "CodexRadarServiceProbeToken", StringComparison.Ordinal))
            {
                Button button = BuildCommandButton("立即检测", false);
                button.Width = 227;
                button.Height = 54;
                button.Tag = 0;
                button.Click += delegate
                {
                    int token = button.Tag is int ? (int)button.Tag : 0;
                    button.Tag = token == int.MaxValue ? 1 : token + 1;
                    ShowStatus("Codex Radar 服务检测已启动，结果写入本地诊断文件。", SettingsStatusSeverity.Warning);
                    OnSettingChanged();
                };
                return button;
            }

            if (string.Equals(property.Name, "ClaudeRadarServiceProbeToken", StringComparison.Ordinal))
            {
                Button button = BuildCommandButton("立即检测", false);
                button.Width = 227;
                button.Height = 54;
                button.Tag = 0;
                button.Click += delegate
                {
                    int token = button.Tag is int ? (int)button.Tag : 0;
                    button.Tag = token == int.MaxValue ? 1 : token + 1;
                    ShowStatus("Claude Radar 服务检测已启动，结果写入本地诊断文件。", SettingsStatusSeverity.Warning);
                    OnSettingChanged();
                };
                return button;
            }

            if (type == typeof(int) && property.Name.EndsWith("RefreshToken", StringComparison.Ordinal))
            {
                Button button = BuildCommandButton("立即刷新", false);
                button.Width = 227;
                button.Height = 54;
                button.Tag = 0;
                button.Click += delegate
                {
                    int token = button.Tag is int ? (int)button.Tag : 0;
                    button.Tag = token == int.MaxValue ? 1 : token + 1;
                    ShowStatus("刷新请求已发送。", SettingsStatusSeverity.Warning);
                    OnSettingChanged();
                };
                return button;
            }

            NumericUpDown box = new NumericUpDown();
            box.Width = 227;
            box.Height = 54;
            box.DecimalPlaces = type == typeof(double) ? 1 : 0;
            box.Increment = type == typeof(double) ? 0.1M : GetNumericIncrement(property.Name);
            NumericRange range = GetNumericRange(property.Name, type);
            box.Minimum = range.Minimum;
            box.Maximum = range.Maximum;
            box.BackColor = ControlBg;
            box.ForeColor = TextSecondary;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = GetUiFont(9.5f);
            box.ValueChanged += delegate { OnSettingChanged(); };
            return box;
        }

        TextBox text = new TextBox();
        text.Width = 400;
        text.Height = 54;
        text.BackColor = ControlBg;
        text.ForeColor = TextSecondary;
        text.BorderStyle = BorderStyle.FixedSingle;
        text.Font = GetUiFont(9.5f);
        text.TextChanged += delegate { OnSettingChanged(); };
        return text;
    }

    private Control BuildVariantPicker(PropertyInfo property)
    {
        VariantPicker picker = new VariantPicker(property.Name, property.PropertyType, GetUiFont(8.8f, FontStyle.Bold), GetUiFont(8.0f));
        picker.Width = 560;
        picker.ValueChanged += delegate { OnSettingChanged(); };
        picker.PreferredHeightChanged += delegate { RelayoutSettingGroup(picker); };
        return picker;
    }

    private Control BuildDisplayDeviceCombo()
    {
        ComboBox combo = new ComboBox();
        combo.Width = 400;
        combo.Height = 54;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = ControlBg;
        combo.ForeColor = TextSecondary;
        combo.Font = GetUiFont(9.5f);
        combo.Items.Add(new DisplayOption(string.Empty, "主显示器/自动"));
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            Screen screen = screens[i];
            Rectangle bounds = screen.Bounds;
            string label = string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1}x{2} @ {3},{4}{5}",
                screen.DeviceName,
                bounds.Width,
                bounds.Height,
                bounds.Left,
                bounds.Top,
                screen.Primary ? "  主屏" : string.Empty);
            combo.Items.Add(new DisplayOption(screen.DeviceName, label));
        }

        combo.SelectedIndexChanged += delegate { OnSettingChanged(); };
        return combo;
    }

    private Control BuildCodexRadarModelCombo()
    {
        ComboBox combo = new ComboBox();
        combo.Width = 400;
        combo.Height = 54;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = ControlBg;
        combo.ForeColor = TextSecondary;
        combo.Font = GetUiFont(9.5f);

        List<CodexRadarModelInfo> models = CodexRadarModelCatalog.LoadModels();
        HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < models.Count; i++)
        {
            CodexRadarModelInfo model = models[i];
            if (model == null)
            {
                continue;
            }

            string key = CodexRadarModelCatalog.NormalizeModelKey(model.Key);
            if (key.Length == 0 || keys.Contains(key))
            {
                continue;
            }

            combo.Items.Add(new ModelOption(
                key,
                CodexRadarModelCatalog.GetDisplayLabel(model.Label, key),
                model.Available,
                false));
            keys.Add(key);
        }

        string defaultKey = CodexRadarModelCatalog.DefaultModelKey;
        if (!keys.Contains(defaultKey))
        {
            combo.Items.Insert(0, new ModelOption(
                defaultKey,
                CodexRadarModelCatalog.GetDisplayLabel(string.Empty, defaultKey),
                true,
                false));
        }

        combo.SelectedIndexChanged += delegate { OnSettingChanged(); };
        return combo;
    }

    private Control BuildClaudeRadarModelGridSelector()
    {
        Panel panel = new Panel();
        panel.Width = GetClaudeRadarModelGridPreferredWidth();
        panel.BackColor = Color.Transparent;
        panel.Resize += delegate { LayoutClaudeRadarModelPanel(panel); };

        FlowLayoutPanel grid = new FlowLayoutPanel();
        grid.Name = ClaudeRadarModelGridName;
        grid.Width = panel.Width;
        grid.Location = new Point(0, 0);
        grid.Margin = new Padding(0);
        grid.Padding = new Padding(0);
        grid.FlowDirection = FlowDirection.LeftToRight;
        grid.WrapContents = true;
        grid.AutoSize = false;
        grid.BackColor = Color.Transparent;
        panel.Controls.Add(grid);

        Button edit = BuildCommandButton("编辑映射", false);
        edit.Width = panel.Width;
        edit.Height = 54;
        edit.Click += delegate
        {
            using (ClaudeRadarModelMapEditorForm dialog = new ClaudeRadarModelMapEditorForm())
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string selected = GetClaudeRadarModelGridValue(grid);
                    PopulateClaudeRadarModelGrid(grid, selected);
                    LayoutClaudeRadarModelPanel(panel);
                    RelayoutSettingGroup(panel);
                    ShowStatus("Claude Radar 模型映射已保存。", SettingsStatusSeverity.Success);
                    OnSettingChanged();
                }
            }
        };
        panel.Controls.Add(edit);
        PopulateClaudeRadarModelGrid(grid, string.Empty);
        LayoutClaudeRadarModelPanel(panel);
        return panel;
    }

    private void PopulateClaudeRadarModelGrid(FlowLayoutPanel grid, string selectedKey)
    {
        if (grid == null)
        {
            return;
        }

        string normalizedSelected = WidgetSettings.NormalizeClaudeRadarModelKey(selectedKey);
        List<ClaudeModelOption> options = BuildClaudeRadarModelOptions(normalizedSelected);
        bool selectedExists = false;
        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Key, normalizedSelected, StringComparison.OrdinalIgnoreCase))
            {
                selectedExists = true;
                break;
            }
        }

        if (!selectedExists)
        {
            normalizedSelected = string.Empty;
        }

        grid.Tag = normalizedSelected;
        grid.SuspendLayout();
        try
        {
            while (grid.Controls.Count > 0)
            {
                Control old = grid.Controls[0];
                grid.Controls.RemoveAt(0);
                old.Dispose();
            }

            int columns = GetClaudeRadarModelColumnCountForGrid(grid.Width);
            int slots = Math.Max(
                ClaudeRadarModelGridColumns,
                ((options.Count + ClaudeRadarModelGridColumns - 1) / ClaudeRadarModelGridColumns) * ClaudeRadarModelGridColumns);
            for (int i = 0; i < slots; i++)
            {
                ClaudeModelOption option = i < options.Count ? options[i] : null;
                Button button = BuildClaudeRadarModelButton(option);
                button.Margin = new Padding(0, 0, i % columns == columns - 1 ? 0 : ClaudeRadarModelButtonGap, ClaudeRadarModelButtonGap);
                grid.Controls.Add(button);
            }

            ApplyClaudeRadarModelGridSelection(grid);
            int rows = Math.Max(1, (slots + columns - 1) / columns);
            grid.Height = rows * ClaudeRadarModelButtonHeight + Math.Max(0, rows - 1) * ClaudeRadarModelButtonGap;
        }
        finally
        {
            grid.ResumeLayout();
        }
    }

    private List<ClaudeModelOption> BuildClaudeRadarModelOptions(string selectedKey)
    {
        List<ClaudeModelOption> options = new List<ClaudeModelOption>();
        options.Add(new ClaudeModelOption(string.Empty, "自动", true, false));
        HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        keys.Add(string.Empty);
        try
        {
            List<ClaudeRadarModelEntry> models = ClaudeRadarReader.LoadModelMap();
            for (int i = 0; i < models.Count; i++)
            {
                ClaudeRadarModelEntry model = models[i];
                if (model == null)
                {
                    continue;
                }

                string key = WidgetSettings.NormalizeClaudeRadarModelKey(model.SourceKey);
                if (key.Length == 0 || keys.Contains(key))
                {
                    continue;
                }

                string status = (model.Status ?? string.Empty).Trim();
                bool deleted = string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase);
                if (deleted && !string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool pending = string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);
                bool temporarilyMissing = string.Equals(status, "temporarily_missing", StringComparison.OrdinalIgnoreCase);
                bool available = model.Enabled &&
                    !deleted &&
                    !pending &&
                    !temporarilyMissing &&
                    !string.IsNullOrWhiteSpace(model.RatingKey);
                string label = string.IsNullOrWhiteSpace(model.DisplayName)
                    ? key
                    : model.DisplayName;
                options.Add(new ClaudeModelOption(
                    key,
                    label,
                    available,
                    pending || temporarilyMissing));
                keys.Add(key);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (!string.IsNullOrEmpty(selectedKey) && !keys.Contains(selectedKey))
        {
            options.Add(new ClaudeModelOption(selectedKey, selectedKey, false, true));
        }

        return options;
    }

    private Button BuildClaudeRadarModelButton(ClaudeModelOption option)
    {
        Button button = new Button();
        button.Width = ClaudeRadarModelButtonWidth;
        button.Height = ClaudeRadarModelButtonHeight;
        button.Tag = option;
        button.Text = option == null ? "--" : GetClaudeRadarModelButtonText(option);
        button.AutoEllipsis = true;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        button.Font = GetUiFont(8.8f, FontStyle.Bold);
        button.Cursor = option != null && option.Available ? Cursors.Hand : Cursors.Default;
        button.Enabled = option != null && option.Available;
        button.Click += delegate
        {
            ClaudeModelOption clicked = button.Tag as ClaudeModelOption;
            if (clicked == null)
            {
                return;
            }

            FlowLayoutPanel grid = FindClaudeRadarModelGrid(button.Parent);
            if (grid == null)
            {
                return;
            }

            string current = GetClaudeRadarModelGridValue(grid);
            if (string.Equals(current, clicked.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            grid.Tag = clicked.Key;
            ApplyClaudeRadarModelGridSelection(grid);
            OnSettingChanged();
        };
        return button;
    }

    private static string GetClaudeRadarModelButtonText(ClaudeModelOption option)
    {
        if (option == null)
        {
            return "--";
        }

        if (option.Key.Length == 0)
        {
            return "自动";
        }

        string label = option.Label ?? option.Key;
        if (label.Length <= ClaudeRadarModelButtonMaxTextChars)
        {
            return label;
        }

        return label.Substring(0, ClaudeRadarModelButtonMaxTextChars);
    }

    private void ApplyClaudeRadarModelGridSelection(FlowLayoutPanel grid)
    {
        if (grid == null)
        {
            return;
        }

        string selectedKey = WidgetSettings.NormalizeClaudeRadarModelKey(grid.Tag as string);
        for (int i = 0; i < grid.Controls.Count; i++)
        {
            Button button = grid.Controls[i] as Button;
            if (button == null)
            {
                continue;
            }

            ClaudeModelOption option = button.Tag as ClaudeModelOption;
            bool selected = option != null &&
                string.Equals(option.Key, selectedKey, StringComparison.OrdinalIgnoreCase);
            ApplyClaudeRadarModelButtonStyle(button, option, selected);
        }
    }

    private static void ApplyClaudeRadarModelButtonStyle(Button button, ClaudeModelOption option, bool selected)
    {
        if (button == null)
        {
            return;
        }

        if (option == null)
        {
            button.BackColor = ControlBg;
            button.ForeColor = TextTertiary;
            button.FlatAppearance.BorderColor = ControlBorder;
            return;
        }

        if (selected)
        {
            button.BackColor = DesignTokens.SettingsWarmTheme.Accent;
            button.ForeColor = Color.Black;
            button.FlatAppearance.BorderColor = DesignTokens.SettingsWarmTheme.AccentHover;
            return;
        }

        button.BackColor = option.Available ? DesignTokens.SettingsWarmTheme.ButtonRest : MicaLayer;
        button.ForeColor = option.Available ? TextSecondary : TextTertiary;
        button.FlatAppearance.BorderColor = option.Pending ? DesignTokens.Colors.Warning : ControlBorder;
    }

    private static string GetClaudeRadarModelGridValue(FlowLayoutPanel grid)
    {
        return grid == null ? string.Empty : WidgetSettings.NormalizeClaudeRadarModelKey(grid.Tag as string);
    }

    private void SetClaudeRadarModelGridValue(FlowLayoutPanel grid, string value)
    {
        PopulateClaudeRadarModelGrid(grid, value);
        if (grid != null && grid.Parent != null)
        {
            LayoutClaudeRadarModelPanel(grid.Parent);
        }
    }

    private static FlowLayoutPanel FindClaudeRadarModelGrid(Control control)
    {
        FlowLayoutPanel grid = control as FlowLayoutPanel;
        if (grid != null && string.Equals(grid.Name, ClaudeRadarModelGridName, StringComparison.Ordinal))
        {
            return grid;
        }

        if (control != null)
        {
            for (int i = 0; i < control.Controls.Count; i++)
            {
                grid = FindClaudeRadarModelGrid(control.Controls[i]);
                if (grid != null)
                {
                    return grid;
                }
            }
        }

        return null;
    }

    private static void LayoutClaudeRadarModelPanel(Control panel)
    {
        if (panel == null)
        {
            return;
        }

        FlowLayoutPanel grid = FindClaudeRadarModelGrid(panel);
        Button edit = null;
        for (int i = 0; i < panel.Controls.Count; i++)
        {
            Button candidate = panel.Controls[i] as Button;
            if (candidate != null && string.Equals(candidate.Text, "编辑映射", StringComparison.Ordinal))
            {
                edit = candidate;
                break;
            }
        }

        int y = 0;
        if (grid != null)
        {
            grid.Location = new Point(0, 0);
            grid.Width = panel.Width;
            int columns = GetClaudeRadarModelColumnCountForGrid(grid.Width);
            int buttonWidth = GetClaudeRadarModelButtonWidthForGrid(grid.Width);
            for (int i = 0; i < grid.Controls.Count; i++)
            {
                Button button = grid.Controls[i] as Button;
                if (button == null)
                {
                    continue;
                }

                button.Width = buttonWidth;
                button.Height = ClaudeRadarModelButtonHeight;
                button.Margin = new Padding(
                    0,
                    0,
                    i % columns == columns - 1 ? 0 : ClaudeRadarModelButtonGap,
                    ClaudeRadarModelButtonGap);
            }

            int rows = Math.Max(1, (grid.Controls.Count + columns - 1) / columns);
            grid.Height = rows * ClaudeRadarModelButtonHeight + Math.Max(0, rows - 1) * ClaudeRadarModelButtonGap;
            y = grid.Bottom + 10;
        }

        if (edit != null)
        {
            edit.Location = new Point(0, y);
            edit.Width = panel.Width;
            y = edit.Bottom;
        }

        panel.Height = Math.Max(54, y);
    }

    private static int GetClaudeRadarModelButtonWidthForGrid(int gridWidth)
    {
        int columns = GetClaudeRadarModelColumnCountForGrid(gridWidth);
        int gaps = Math.Max(0, columns - 1) * ClaudeRadarModelButtonGap;
        int usable = Math.Max(columns, gridWidth - gaps);
        return Math.Max(32, Math.Min(ClaudeRadarModelButtonWidth, usable / columns));
    }

    private static int GetClaudeRadarModelColumnCountForGrid(int gridWidth)
    {
        for (int columns = ClaudeRadarModelGridColumns; columns > 1; columns--)
        {
            int gaps = Math.Max(0, columns - 1) * ClaudeRadarModelButtonGap;
            if (gridWidth >= columns * ClaudeRadarModelButtonMinimumWidth + gaps)
            {
                return columns;
            }
        }

        return 1;
    }

    private static int GetClaudeRadarModelGridPreferredWidth()
    {
        return ClaudeRadarModelGridColumns * ClaudeRadarModelButtonWidth +
            Math.Max(0, ClaudeRadarModelGridColumns - 1) * ClaudeRadarModelButtonGap;
    }

    private static void RelayoutSettingGroup(Control control)
    {
        Control parent = control;
        while (parent != null)
        {
            SettingGroupCard card = parent as SettingGroupCard;
            if (card != null)
            {
                card.LayoutRows();
                return;
            }

            parent = parent.Parent;
        }
    }

    private static ComboBox FindComboBox(Control control)
    {
        ComboBox combo = control as ComboBox;
        if (combo != null)
        {
            return combo;
        }

        if (control != null)
        {
            for (int i = 0; i < control.Controls.Count; i++)
            {
                combo = FindComboBox(control.Controls[i]);
                if (combo != null)
                {
                    return combo;
                }
            }
        }

        return null;
    }

    private void PopulateClaudeRadarModelCombo(ComboBox combo)
    {
        if (combo == null)
        {
            return;
        }

        combo.Items.Clear();
        combo.Items.Add(new ClaudeModelOption(string.Empty, "自动选择", true, false));
        try
        {
            List<ClaudeRadarModelEntry> models = ClaudeRadarReader.LoadModelMap();
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < models.Count; i++)
            {
                ClaudeRadarModelEntry model = models[i];
                if (model == null)
                {
                    continue;
                }

                string key = WidgetSettings.NormalizeClaudeRadarModelKey(model.SourceKey);
                if (key.Length == 0 || keys.Contains(key))
                {
                    continue;
                }

                bool available = model.Enabled &&
                    !string.Equals(model.Status, "deleted", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(model.Status, "pending", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(model.Status, "temporarily_missing", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(model.RatingKey);
                string label = string.IsNullOrWhiteSpace(model.DisplayName)
                    ? key
                    : model.DisplayName;
                combo.Items.Add(new ClaudeModelOption(
                    key,
                    label,
                    available,
                    string.Equals(model.Status, "pending", StringComparison.OrdinalIgnoreCase)));
                keys.Add(key);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (combo.Items.Count > 0 && combo.SelectedIndex < 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static object GetClaudeRadarModelComboValue(ComboBox combo)
    {
        ClaudeModelOption option = combo == null ? null : combo.SelectedItem as ClaudeModelOption;
        return option == null ? string.Empty : option.Key;
    }

    private static void SetClaudeRadarModelComboValue(ComboBox combo, string value)
    {
        if (combo == null)
        {
            return;
        }

        string modelKey = WidgetSettings.NormalizeClaudeRadarModelKey(value);
        for (int i = 0; i < combo.Items.Count; i++)
        {
            ClaudeModelOption option = combo.Items[i] as ClaudeModelOption;
            if (option != null && string.Equals(option.Key, modelKey, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (!string.IsNullOrEmpty(modelKey))
        {
            combo.Items.Add(new ClaudeModelOption(modelKey, modelKey, false, true));
            combo.SelectedIndex = combo.Items.Count - 1;
            return;
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static bool IsDisplayDeviceSetting(string propertyName)
    {
        return propertyName != null &&
            propertyName.EndsWith("DisplayDeviceName", StringComparison.Ordinal);
    }

    // ── Page Selection & Layout ──────────────────────────────────────────
    private void SelectPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= this.pages.Count)
        {
            return;
        }

        for (int i = 0; i < this.pages.Count; i++)
        {
            CategoryPage page = this.pages[i];
            bool active = i == pageIndex;
            page.ScrollPanel.Visible = active;
            if (page.NavItem != null)
            {
                page.NavItem.Selected = active;
            }
            if (active)
            {
                LayoutPage(page);
                EnsureVariantSamplesForPage(page);
            }
        }

        this.selectedPageIndex = pageIndex;
        this.pages[pageIndex].ScrollPanel.BringToFront();
    }

    private void EnsureVariantSamplesForPage(CategoryPage page)
    {
        if (this.owner == null || page == null)
        {
            return;
        }

        string directory = GetVariantSampleDirectory();
        for (int i = 0; i < page.Editors.Count; i++)
        {
            SettingEditor editor = page.Editors[i];
            VariantPicker picker = editor.Control as VariantPicker;
            if (picker == null)
            {
                continue;
            }

            string prefix = GetVariantSamplePrefix(editor.Name);
            if (prefix.Length == 0)
            {
                continue;
            }

            try
            {
                if (picker.HasMissingSamples(directory, prefix))
                {
                    System.IO.Directory.CreateDirectory(directory);
                    RenderVariantSamplesForProperty(editor.Name, directory);
                }

                picker.LoadSamples(directory, prefix);
                RelayoutSettingGroup(picker);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                picker.MarkSamplesUnavailable();
            }
        }
    }

    private static string GetVariantSampleDirectory()
    {
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.MachineName,
            "variant-samples",
            "v" + ProductIdentity.Version);
    }

    private static string GetVariantSamplePrefix(string propertyName)
    {
        if (string.Equals(propertyName, "OperationRenderVariant", StringComparison.Ordinal)) return "operation";
        return string.Empty;
    }

    private static void RenderVariantSamplesForProperty(string propertyName, string directory)
    {
        if (string.Equals(propertyName, "OperationRenderVariant", StringComparison.Ordinal))
        {
            OperationForm.RenderVariantSamples(directory);
        }
    }

    private void LayoutPage(CategoryPage page)
    {
        int width = Math.Max(360, page.ScrollPanel.ClientSize.Width - page.ScrollPanel.Padding.Left - page.ScrollPanel.Padding.Right - 22);
        page.Stack.Width = width;
        page.Heading.Width = width;
        if (page.AdvancedHeader != null)
        {
            page.AdvancedHeader.Width = width;
        }

        for (int g = 0; g < page.Groups.Count; g++)
        {
            SettingGroupData group = page.Groups[g];
            if (group.TitleLabel != null)
            {
                group.TitleLabel.Width = width;
            }
            if (group.Card != null)
            {
                group.Card.Width = width;
                group.Card.LayoutRows();
            }
        }
    }

    // ── Settings I/O ─────────────────────────────────────────────────────
    private void LoadSettings(WidgetSettings settings)
    {
        this.initializing = true;
        try
        {
            foreach (SettingEditor editor in this.editors.Values)
            {
                if (editor.Property == null)
                {
                    continue;
                }

                object value = editor.Property.GetValue(settings, null);
                SetEditorValue(editor, value);
            }
        }
        finally
        {
            this.initializing = false;
        }

        SetDirtyState(false);
    }

    private WidgetSettings ReadSettings()
    {
        WidgetSettings settings = this.baseline.Clone();
        foreach (SettingEditor editor in this.editors.Values)
        {
            if (editor.Property == null)
            {
                continue;
            }

            object value = GetEditorValue(editor);
            if (value != null)
            {
                editor.Property.SetValue(settings, value, null);
            }
        }

        settings.Normalize();
        return settings;
    }

    private bool TrySaveSettings()
    {
        try
        {
            WidgetSettings settings = ReadSettings();
            if (this.owner != null)
            {
                this.owner.SaveSettings(settings);
            }

            this.baseline = settings.Clone();
            this.baseline.Normalize();
            this.saved = true;
            SetDirtyState(false);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowStatus("保存失败 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture), SettingsStatusSeverity.Error);
            return false;
        }
    }

    private void SetEditorValue(SettingEditor editor, object value)
    {
        ToggleSwitch toggle = editor.Control as ToggleSwitch;
        if (toggle != null)
        {
            toggle.SetCheckedSilent(value is bool && (bool)value);
            return;
        }

        if (string.Equals(editor.Property.Name, "ClaudeRadarModelKey", StringComparison.Ordinal))
        {
            FlowLayoutPanel grid = FindClaudeRadarModelGrid(editor.Control);
            if (grid != null)
            {
                SetClaudeRadarModelGridValue(grid, value as string);
                RelayoutSettingGroup(editor.Control);
                return;
            }
        }

        VariantPicker picker = editor.Control as VariantPicker;
        if (picker != null)
        {
            picker.SetSelectedValue(value);
            RelayoutSettingGroup(editor.Control);
            return;
        }

        PercentSliderControl slider = editor.Control as PercentSliderControl;
        if (slider != null)
        {
            slider.SetValueSilent(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return;
        }

        ComboBox combo = editor.Control as ComboBox;
        if (combo == null && string.Equals(editor.Property.Name, "ClaudeRadarModelKey", StringComparison.Ordinal))
        {
            combo = FindComboBox(editor.Control);
        }

        if (combo != null)
        {
            string displayDeviceName = value as string;
            if (string.Equals(editor.Property.Name, "CodexRadarModelKey", StringComparison.Ordinal))
            {
                string modelKey = CodexRadarModelCatalog.NormalizeModelKey(value as string);
                if (modelKey.Length == 0)
                {
                    modelKey = CodexRadarModelCatalog.DefaultModelKey;
                }

                for (int i = 0; i < combo.Items.Count; i++)
                {
                    ModelOption option = combo.Items[i] as ModelOption;
                    if (option != null && string.Equals(option.Key, modelKey, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedIndex = i;
                        return;
                    }
                }

                combo.Items.Add(new ModelOption(
                    modelKey,
                    CodexRadarModelCatalog.GetDisplayLabel(string.Empty, modelKey),
                    false,
                    true));
                combo.SelectedIndex = combo.Items.Count - 1;
                return;
            }

            if (string.Equals(editor.Property.Name, "ClaudeRadarModelKey", StringComparison.Ordinal))
            {
                SetClaudeRadarModelComboValue(combo, value as string);
                return;
            }

            if (IsDisplayDeviceSetting(editor.Property.Name))
            {
                displayDeviceName = WidgetSettings.NormalizeDisplayDeviceName(displayDeviceName);
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    DisplayOption option = combo.Items[i] as DisplayOption;
                    if (option != null && string.Equals(option.Value, displayDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedIndex = i;
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(displayDeviceName))
                {
                    combo.Items.Add(new DisplayOption(displayDeviceName, "未连接  " + displayDeviceName));
                    combo.SelectedIndex = combo.Items.Count - 1;
                    return;
                }

                if (combo.Items.Count > 0)
                {
                    combo.SelectedIndex = 0;
                }

                return;
            }

            for (int i = 0; i < combo.Items.Count; i++)
            {
                EnumOption option = combo.Items[i] as EnumOption;
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

            return;
        }

        NumericUpDown number = editor.Control as NumericUpDown;
        if (number != null)
        {
            decimal next = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (next < number.Minimum)
            {
                next = number.Minimum;
            }
            else if (next > number.Maximum)
            {
                next = number.Maximum;
            }

            number.Value = next;
            return;
        }

        TextBox text = editor.Control as TextBox;
        if (text != null)
        {
            text.Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        Button button = editor.Control as Button;
        if (button != null)
        {
            button.Tag = value is int ? (int)value : 0;
            if (string.Equals(editor.Name, "DeepSeekApiKeyRevision", StringComparison.Ordinal))
            {
                button.Text = GetDeepSeekApiKeyButtonText();
            }
        }
    }

    private void ApplyResponsiveShellLayout()
    {
        int navWidth = GetNavigationWidthForClientWidth(this.ClientSize.Width);
        if (this.bodyLayout != null && this.bodyLayout.ColumnStyles.Count > 0)
        {
            this.bodyLayout.ColumnStyles[0].Width = navWidth;
        }

        if (this.navigationPanel != null)
        {
            int itemWidth = Math.Max(160, navWidth - 24);
            for (int i = 0; i < this.navigationPanel.Controls.Count; i++)
            {
                this.navigationPanel.Controls[i].Width = itemWidth;
            }
        }

        for (int i = 0; i < this.pages.Count; i++)
        {
            LayoutPage(this.pages[i]);
        }
    }

    private object GetEditorValue(SettingEditor editor)
    {
        if (editor.Property == null)
        {
            return null;
        }

        Type type = editor.Property.PropertyType;
        ToggleSwitch toggle = editor.Control as ToggleSwitch;
        if (toggle != null)
        {
            return toggle.Checked;
        }

        if (string.Equals(editor.Property.Name, "ClaudeRadarModelKey", StringComparison.Ordinal))
        {
            FlowLayoutPanel grid = FindClaudeRadarModelGrid(editor.Control);
            if (grid != null)
            {
                return GetClaudeRadarModelGridValue(grid);
            }
        }

        VariantPicker picker = editor.Control as VariantPicker;
        if (picker != null)
        {
            return picker.GetSelectedValue();
        }

        PercentSliderControl slider = editor.Control as PercentSliderControl;
        if (slider != null)
        {
            return slider.Value;
        }

        ComboBox combo = editor.Control as ComboBox;
        if (combo == null && string.Equals(editor.Property.Name, "ClaudeRadarModelKey", StringComparison.Ordinal))
        {
            combo = FindComboBox(editor.Control);
        }

        if (combo != null)
        {
            ClaudeModelOption claudeModelOption = combo.SelectedItem as ClaudeModelOption;
            if (claudeModelOption != null)
            {
                return claudeModelOption.Key;
            }

            ModelOption modelOption = combo.SelectedItem as ModelOption;
            if (modelOption != null)
            {
                return modelOption.Key;
            }

            DisplayOption displayOption = combo.SelectedItem as DisplayOption;
            if (displayOption != null)
            {
                return displayOption.Value;
            }

            EnumOption option = combo.SelectedItem as EnumOption;
            return option == null ? null : option.Value;
        }

        NumericUpDown number = editor.Control as NumericUpDown;
        if (number != null)
        {
            return type == typeof(double) ? (object)Convert.ToDouble(number.Value, CultureInfo.InvariantCulture) : Convert.ToInt32(number.Value, CultureInfo.InvariantCulture);
        }

        TextBox text = editor.Control as TextBox;
        if (text != null)
        {
            return text.Text ?? string.Empty;
        }

        Button button = editor.Control as Button;
        if (button != null)
        {
            return button.Tag is int ? (int)button.Tag : 0;
        }

        return null;
    }

    // ── Setting Changed & Preview ────────────────────────────────────────
    private void OnSettingChanged()
    {
        if (this.initializing)
        {
            return;
        }

        this.saved = false;
        SetDirtyState(true);
        this.previewTimer.Stop();
        this.previewTimer.Start();
    }

    private void OnPreviewTimerTick(object sender, EventArgs e)
    {
        this.previewTimer.Stop();
        if (!this.IsDisposed && !this.OwnerFormClosing && this.owner != null)
        {
            this.owner.PreviewSettings(ReadSettings());
        }
    }

    private void OpenGlobalLayoutEditorFromSettings()
    {
        if (this.owner == null)
        {
            return;
        }

        this.previewTimer.Stop();
        WidgetSettings settings = ReadSettings();
        WidgetSettings editedSettings;
        if (this.owner.TryEditGlobalLayout(settings, out editedSettings))
        {
            this.baseline = editedSettings.Clone();
            this.baseline.Normalize();
            LoadSettings(this.baseline);
            this.saved = true;
            ShowStatus("全局布局已保存", SettingsStatusSeverity.Success);
            return;
        }

        this.owner.PreviewSettings(ReadSettings());
        ShowStatus("全局编辑已取消", SettingsStatusSeverity.Warning);
    }

    private void OpenDeepSeekApiKeyDialog(Button sourceButton)
    {
        Form dialog = new Form();
        try
        {
            dialog.Text = "DeepSeek 配置";
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.ShowInTaskbar = false;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.ClientSize = new Size(560, 238);
            dialog.BackColor = MicaBase;
            dialog.ForeColor = TextSecondary;
            dialog.Font = GetUiFont(9.5f);

            Label title = new Label();
            title.Text = "DeepSeek API Key";
            title.Font = GetUiFont(12.0f, FontStyle.Bold);
            title.ForeColor = TextPrimary;
            title.BackColor = MicaBase;
            title.Location = new Point(24, 20);
            title.Size = new Size(500, 30);
            title.TextAlign = ContentAlignment.MiddleLeft;

            Label hint = new Label();
            hint.Text = HasDeepSeekEnvironmentApiKeyForUi()
                ? "检测到 DEEPSEEK_API_KEY 环境变量；环境变量会优先于本地文件。"
                : "密钥保存到本地应用数据目录，不写入 settings.ini 或日志。";
            hint.Font = GetUiFont(8.8f);
            hint.ForeColor = TextTertiary;
            hint.BackColor = MicaBase;
            hint.Location = new Point(24, 56);
            hint.Size = new Size(500, 42);
            hint.TextAlign = ContentAlignment.MiddleLeft;

            TextBox keyBox = new TextBox();
            keyBox.Location = new Point(24, 104);
            keyBox.Size = new Size(512, 34);
            keyBox.BackColor = ControlBg;
            keyBox.ForeColor = TextSecondary;
            keyBox.BorderStyle = BorderStyle.FixedSingle;
            keyBox.Font = GetUiFont(9.5f);
            keyBox.UseSystemPasswordChar = true;
            keyBox.Text = ReadDeepSeekApiKeyFileForUi();

            Button clear = BuildCommandButton("清除", false);
            clear.Location = new Point(24, 166);
            clear.Width = 112;
            clear.Height = 44;

            Button cancel = BuildCommandButton("取消", false);
            cancel.Location = new Point(302, 166);
            cancel.Width = 112;
            cancel.Height = 44;
            cancel.Click += delegate { dialog.Close(); };

            Button save = BuildCommandButton("保存", true);
            save.Location = new Point(424, 166);
            save.Width = 112;
            save.Height = 44;

            clear.Click += delegate
            {
                string errorCode;
                if (TrySaveDeepSeekApiKeyFile(string.Empty, out errorCode))
                {
                    keyBox.Text = string.Empty;
                    IncrementDeepSeekApiKeyRevision(sourceButton);
                    ShowStatus("DeepSeek 配置已清除", SettingsStatusSeverity.Success);
                    dialog.Close();
                    return;
                }

                ShowStatus("DeepSeek 配置清除失败 " + errorCode, SettingsStatusSeverity.Error);
            };

            save.Click += delegate
            {
                string errorCode;
                if (TrySaveDeepSeekApiKeyFile(keyBox.Text, out errorCode))
                {
                    IncrementDeepSeekApiKeyRevision(sourceButton);
                    ShowStatus("DeepSeek 配置已保存", SettingsStatusSeverity.Success);
                    dialog.Close();
                    return;
                }

                ShowStatus("DeepSeek 配置保存失败 " + errorCode, SettingsStatusSeverity.Error);
            };

            dialog.Controls.Add(title);
            dialog.Controls.Add(hint);
            dialog.Controls.Add(keyBox);
            dialog.Controls.Add(clear);
            dialog.Controls.Add(cancel);
            dialog.Controls.Add(save);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            dialog.ShowDialog(this);
        }
        finally
        {
            dialog.Dispose();
        }
    }

    private void IncrementDeepSeekApiKeyRevision(Button sourceButton)
    {
        if (sourceButton != null)
        {
            int token = sourceButton.Tag is int ? (int)sourceButton.Tag : 0;
            sourceButton.Tag = token == int.MaxValue ? 1 : token + 1;
            sourceButton.Text = GetDeepSeekApiKeyButtonText();
        }

        OnSettingChanged();
    }

    private static string GetDeepSeekApiKeyButtonText()
    {
        return IsDeepSeekApiKeyConfiguredForUi() ? "修改配置" : "配置";
    }

    private static bool IsDeepSeekApiKeyConfiguredForUi()
    {
        if (HasDeepSeekEnvironmentApiKeyForUi())
        {
            return true;
        }

        return ReadDeepSeekApiKeyFileForUi().Length > 0;
    }

    private static bool HasDeepSeekEnvironmentApiKeyForUi()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeepSeekBalanceMonitor.ApiKeyEnvironmentVariable)))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeepSeekBalanceMonitor.ApiKeyEnvironmentVariable, EnvironmentVariableTarget.User)))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeepSeekBalanceMonitor.ApiKeyEnvironmentVariable, EnvironmentVariableTarget.Machine));
        }
        catch
        {
            return false;
        }
    }

    private static string ReadDeepSeekApiKeyFileForUi()
    {
        try
        {
            string secret;
            bool migrated;
            string errorCode;
            return SecretStore.TryReadOrMigrateSecret(
                DeepSeekBalanceMonitor.ApiKeyPath,
                DeepSeekBalanceMonitor.LegacyApiKeyPath,
                SecretStore.TrimSecret,
                out secret,
                out migrated,
                out errorCode)
                ? secret
                : string.Empty;
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool TrySaveDeepSeekApiKeyFile(string apiKey, out string errorCode)
    {
        errorCode = string.Empty;
        try
        {
            string trimmed = (apiKey ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                SecretStore.DeleteSecretFiles(DeepSeekBalanceMonitor.ApiKeyPath, DeepSeekBalanceMonitor.LegacyApiKeyPath);
                return true;
            }

            SecretStore.WriteSecret(DeepSeekBalanceMonitor.ApiKeyPath, trimmed);
            SecretStore.DeleteLegacySecretFiles(DeepSeekBalanceMonitor.LegacyApiKeyPath);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            errorCode = "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
            return false;
        }
    }

    // ── Status Toast ─────────────────────────────────────────────────────
    // Three-state severity instead of a plain success/error bool: Warning covers "action
    // fired but the outcome is not known yet" (detection/refresh requests, cancellations),
    // which used to be lumped in with Success and always rendered green.
    private enum SettingsStatusSeverity
    {
        Success,
        Warning,
        Error
    }

    private void ShowStatus(string text, SettingsStatusSeverity severity)
    {
        if (this.statusLabel == null)
        {
            return;
        }

        this.statusTimer.Stop();
        this.statusLabel.Text = text;
        this.statusLabel.ForeColor = GetStatusSeverityColor(severity);
        this.statusLabel.Visible = true;
        this.statusTimer.Start();
    }

    private static Color GetStatusSeverityColor(SettingsStatusSeverity severity)
    {
        if (severity == SettingsStatusSeverity.Error)
        {
            return ErrorClr;
        }

        if (severity == SettingsStatusSeverity.Warning)
        {
            return DesignTokens.Colors.Warning;
        }

        return AccentClr;
    }

    private void SetDirtyState(bool hasChanges)
    {
        this.dirty = hasChanges;
        if (this.statusLabel == null)
        {
            return;
        }

        if (hasChanges)
        {
            this.statusTimer.Stop();
            this.statusLabel.Text = "有未保存的更改";
            this.statusLabel.ForeColor = AccentClr;
            this.statusLabel.Visible = true;
        }
        else if (!this.statusTimer.Enabled)
        {
            this.statusLabel.Visible = false;
        }
    }

    private void OnStatusTimerTick(object sender, EventArgs e)
    {
        this.statusTimer.Stop();
        if (this.statusLabel != null)
        {
            if (this.dirty)
            {
                SetDirtyState(true);
            }
            else
            {
                this.statusLabel.Visible = false;
            }
        }
    }

    // ── Search Filter ────────────────────────────────────────────────────
    private void ApplySearchFilter()
    {
        string query = (this.searchBox == null ? string.Empty : this.searchBox.Text ?? string.Empty).Trim();
        bool searching = query.Length > 0;
        int firstMatchingPageIndex = -1;
        bool selectedPageHasVisibleRows = false;
        for (int i = 0; i < this.pages.Count; i++)
        {
            CategoryPage page = this.pages[i];
            bool pageMatch = query.Length == 0 ||
                page.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                page.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            bool anyVisible = false;

            if (page.AdvancedHeader != null)
            {
                // While searching, advanced rows surface directly, so the collapse header hides.
                page.AdvancedHeader.Visible = !searching;
            }

            for (int g = 0; g < page.Groups.Count; g++)
            {
                SettingGroupData group = page.Groups[g];
                bool anyRowVisible = false;
                bool groupTitleMatch = group.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                bool allowed = !group.Advanced || page.AdvancedExpanded || searching;

                for (int j = 0; j < group.Editors.Count; j++)
                {
                    SettingEditor editor = group.Editors[j];
                    bool match = allowed && (pageMatch || groupTitleMatch ||
                        editor.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        GetSettingTitle(editor.Name).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    editor.Card.Visible = match;
                    anyRowVisible |= match;
                }

                if (group.TitleLabel != null)
                {
                    group.TitleLabel.Visible = anyRowVisible;
                }
                if (group.Card != null)
                {
                    group.Card.Visible = anyRowVisible;
                    if (anyRowVisible) group.Card.LayoutRows();
                }
                anyVisible |= anyRowVisible;
            }

            if (page.NavItem != null)
            {
                page.NavItem.Visible = query.Length == 0 || pageMatch || anyVisible;
            }

            if (searching && anyVisible)
            {
                if (firstMatchingPageIndex < 0)
                {
                    firstMatchingPageIndex = i;
                }

                if (i == this.selectedPageIndex)
                {
                    selectedPageHasVisibleRows = true;
                }
            }
        }

        if (searching &&
            !selectedPageHasVisibleRows &&
            firstMatchingPageIndex >= 0 &&
            firstMatchingPageIndex != this.selectedPageIndex)
        {
            SelectPage(firstMatchingPageIndex);
        }
    }

    private FluentScrollPanel GetSelectedScrollPage()
    {
        if (this.selectedPageIndex < 0 || this.selectedPageIndex >= this.pages.Count)
        {
            return null;
        }

        return this.pages[this.selectedPageIndex].ScrollPanel;
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static string GetSettingTitle(string name)
    {
        string value;
        return SettingTitles.TryGetValue(name, out value) ? value : SplitPascalCase(name);
    }

    private static string GetSettingHint(string name)
    {
        string value;
        return SettingHints.TryGetValue(name, out value) ? value : string.Empty;
    }

    private static decimal GetNumericIncrement(string name)
    {
        if (IsCodexRadarManualElementOffsetSetting(name))
        {
            return 1;
        }

        if (name.EndsWith("Seconds", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.IndexOf("Transparency", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Percent", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 1;
        }

        return 10;
    }

    private static NumericRange GetNumericRange(string name, Type type)
    {
        NumericRange range;
        if (NumericRanges.TryGetValue(name, out range))
        {
            return range;
        }

        if (IsCodexRadarManualElementOffsetSetting(name))
        {
            return new NumericRange(
                WidgetSettings.MinCodexRadarManualElementOffsetPixels,
                WidgetSettings.MaxCodexRadarManualElementOffsetPixels);
        }

        if (name.IndexOf("Transparency", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.EndsWith("Percent", StringComparison.OrdinalIgnoreCase))
        {
            return new NumericRange(0, name.IndexOf("Efficiency", StringComparison.OrdinalIgnoreCase) >= 0 ? 200 : 100);
        }

        if (name.EndsWith("Seconds", StringComparison.OrdinalIgnoreCase))
        {
            return new NumericRange(0, 600);
        }

        if (name.EndsWith("Minutes", StringComparison.OrdinalIgnoreCase))
        {
            return new NumericRange(0, 240);
        }

        if (name.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new NumericRange(1, 4000);
        }

        if (name.IndexOf("LeftX", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("BottomY", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Offset", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new NumericRange(0, 5000);
        }

        if (type == typeof(double))
        {
            return new NumericRange(0, 100);
        }

        return new NumericRange(0, 100000000);
    }

    private static bool IsCodexRadarManualElementOffsetSetting(string name)
    {
        return name != null &&
            name.StartsWith("CodexRadar", StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith("OffsetX", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("OffsetY", StringComparison.OrdinalIgnoreCase));
    }

    private static string SplitPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        System.Text.StringBuilder builder = new System.Text.StringBuilder(name.Length + 8);
        builder.Append(name[0]);
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static int GetSingleLineHeight(Font font, int verticalPadding)
    {
        return Math.Max(24, TextRenderer.MeasureText("测量文字 Ag", font).Height + verticalPadding);
    }

    private static int GetWrappedTextHeight(string text, Font font, int width, int verticalPadding)
    {
        int safeWidth = Math.Max(80, width);
        Size measured = TextRenderer.MeasureText(
            string.IsNullOrEmpty(text) ? " " : text,
            font,
            new Size(safeWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return Math.Max(GetSingleLineHeight(font, verticalPadding), measured.Height + verticalPadding);
    }

    private void ScheduleDynamicResolutionSizing()
    {
        if (!this.IsHandleCreated)
        {
            ApplyDynamicResolutionSizing(false);
            return;
        }

        try
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (!this.IsDisposed)
                {
                    ApplyDynamicResolutionSizing(false);
                }
            });
        }
        catch
        {
            if (!this.IsDisposed)
            {
                ApplyDynamicResolutionSizing(false);
            }
        }
    }

    private void ApplyDynamicResolutionSizing(bool usePreferredSize)
    {
        Rectangle workArea = GetTargetWorkArea();
        Size minimum = GetMinimumWindowSizeForWorkArea(workArea);
        Size preferred = GetPreferredClientSizeForWorkArea(workArea);

        this.MinimumSize = minimum;
        this.MaximumSize = new Size(
            Math.Max(minimum.Width, Math.Max(1, workArea.Width)),
            Math.Max(minimum.Height, Math.Max(1, workArea.Height)));

        Size target = usePreferredSize || this.ClientSize.Width <= 0 || this.ClientSize.Height <= 0
            ? preferred
            : this.ClientSize;
        target = new Size(
            Clamp(target.Width, minimum.Width, preferred.Width),
            Clamp(target.Height, minimum.Height, preferred.Height));

        if (this.ClientSize != target)
        {
            this.ClientSize = target;
        }

        if (this.Visible || this.IsHandleCreated)
        {
            ClampWindowToWorkArea(workArea);
        }

        ApplyResponsiveShellLayout();
    }

    private Rectangle GetTargetWorkArea()
    {
        Rectangle workArea;
        if (this.owner != null && !this.owner.IsDisposed)
        {
            workArea = Screen.FromControl(this.owner).WorkingArea;
        }
        else if (this.IsHandleCreated)
        {
            workArea = Screen.FromHandle(this.Handle).WorkingArea;
        }
        else
        {
            workArea = Screen.PrimaryScreen.WorkingArea;
        }

        EnsureUsableWorkArea(ref workArea);
        return workArea;
    }

    private void ClampWindowToWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        int left = Clamp(this.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - this.Width));
        int top = Clamp(this.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - this.Height));
        if (left != this.Left || top != this.Top)
        {
            this.Location = new Point(left, top);
        }
    }

    private static Size GetPreferredClientSizeForWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        int width = Clamp(
            (int)Math.Round(workArea.Width * (PreferredClientWidth / (double)ReferenceWorkAreaWidth), MidpointRounding.AwayFromZero),
            Math.Min(MinimumClientWidth, Math.Max(1, workArea.Width)),
            PreferredClientWidth);
        int height = Clamp(
            (int)Math.Round(workArea.Height * (PreferredClientHeight / (double)ReferenceWorkAreaHeight), MidpointRounding.AwayFromZero),
            Math.Min(MinimumClientHeight, Math.Max(1, workArea.Height)),
            PreferredClientHeight);
        return FitClientSizeToWorkArea(new Size(width, height), workArea);
    }

    private static Size FitClientSizeToScreen(Size desiredSize)
    {
        return FitClientSizeToWorkArea(desiredSize, Screen.PrimaryScreen.WorkingArea);
    }

    private static Size FitClientSizeToWorkArea(Size desiredSize, Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        int width = Math.Min(desiredSize.Width, Math.Max(MinimumClientWidth, workArea.Width - ScreenMargin));
        int height = Math.Min(desiredSize.Height, Math.Max(MinimumClientHeight, workArea.Height - ScreenMargin));
        width = Math.Min(width, Math.Max(1, workArea.Width));
        height = Math.Min(height, Math.Max(1, workArea.Height));
        return new Size(width, height);
    }

    private static Size GetMinimumWindowSizeForScreen()
    {
        return GetMinimumWindowSizeForWorkArea(Screen.PrimaryScreen.WorkingArea);
    }

    private static Size GetMinimumWindowSizeForWorkArea(Rectangle workArea)
    {
        EnsureUsableWorkArea(ref workArea);
        Size preferred = GetPreferredClientSizeForWorkArea(workArea);
        return new Size(
            Math.Min(preferred.Width, Math.Max(Math.Min(MinimumClientWidth, workArea.Width), workArea.Width - 128)),
            Math.Min(preferred.Height, Math.Max(Math.Min(MinimumClientHeight, workArea.Height), workArea.Height - 128)));
    }

    private static int GetNavigationWidthForClientWidth(int clientWidth)
    {
        if (clientWidth <= 960)
        {
            return CompactNavWidth;
        }

        if (clientWidth <= 1280)
        {
            return MediumNavWidth;
        }

        return DefaultNavWidth;
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
            workArea = new Rectangle(0, 0, ReferenceWorkAreaWidth, ReferenceWorkAreaHeight);
        }
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (maximum < minimum)
        {
            maximum = minimum;
        }

        if (value < minimum)
        {
            return minimum;
        }

        if (value > maximum)
        {
            return maximum;
        }

        return value;
    }

    private Font GetUiFont(float size)
    {
        return GetUiFont(size, FontStyle.Regular);
    }

    private Font GetUiFont(float size, FontStyle style)
    {
        return this.fontCache.GetUiPoint(size, style);
    }

    private static Font GetIconFont()
    {
        if (iconFontCache != null) return iconFontCache;
        Font f = new Font("Segoe Fluent Icons", 12f);
        if (f.Name.IndexOf("Segoe Fluent", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            iconFontCache = f;
            return f;
        }
        f.Dispose();
        f = new Font("Segoe MDL2 Assets", 12f);
        iconFontCache = f;
        return f;
    }

    // ── Self-Test ────────────────────────────────────────────────────────
    internal static string RunOpenCloseStressSelfTest(int iterations)
    {
        int loopCount = Math.Max(1, Math.Min(500, iterations));
        WidgetSettings baseline = WidgetSettings.CreateDefaults();
        baseline.CodexRadarEnabled = true;
        baseline.ClaudeRadarEnabled = true;
        baseline.Normalize();

        using (Win11SettingsForm warmup = new Win11SettingsForm(null, baseline))
        {
            warmup.StartPosition = FormStartPosition.Manual;
            warmup.Location = new Point(-30000, -30000);
            warmup.Show();
            Application.DoEvents();
            warmup.Close();
            Application.DoEvents();
        }

        ForceResourceCleanup();
        RadarRuntimeDiagnostics.ResourceCounters before = RadarRuntimeDiagnostics.CaptureCurrentProcessResources();
        for (int i = 0; i < loopCount; i++)
        {
            using (Win11SettingsForm form = new Win11SettingsForm(null, baseline))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                Application.DoEvents();
                form.Close();
                Application.DoEvents();
            }

            if ((i + 1) % 25 == 0)
            {
                ForceResourceCleanup();
            }
        }

        ForceResourceCleanup();
        RadarRuntimeDiagnostics.ResourceCounters after = RadarRuntimeDiagnostics.CaptureCurrentProcessResources();
        int handleDelta = after.HandleCount - before.HandleCount;
        int gdiDelta = after.GdiObjects - before.GdiObjects;
        int userDelta = after.UserObjects - before.UserObjects;
        if (handleDelta > 80 || gdiDelta > 8 || userDelta > 16)
        {
            throw new InvalidOperationException(
                "WinUI settings open/close resource growth exceeded threshold. Iterations=" +
                loopCount.ToString(CultureInfo.InvariantCulture) +
                ", HandlesDelta=" +
                handleDelta.ToString(CultureInfo.InvariantCulture) +
                ", GdiDelta=" +
                gdiDelta.ToString(CultureInfo.InvariantCulture) +
                ", UserDelta=" +
                userDelta.ToString(CultureInfo.InvariantCulture));
        }

        return "Settings open/close policy: PASS iterations=" +
            loopCount.ToString(CultureInfo.InvariantCulture) +
            " handles_delta=" +
            handleDelta.ToString(CultureInfo.InvariantCulture) +
            " gdi_delta=" +
            gdiDelta.ToString(CultureInfo.InvariantCulture) +
            " user_delta=" +
            userDelta.ToString(CultureInfo.InvariantCulture);
    }

    internal static void RunSettingsBindingSelfTest()
    {
        VerifySettingsWindowActivationPolicy();
        VerifyUnsavedPreviewConsumePolicy();
        WidgetSettings baseline = WidgetSettings.CreateDefaults();
        using (Win11SettingsForm form = new Win11SettingsForm(null, baseline))
        {
            form.OwnerFormClosing = true;
            form.saved = true;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-51200, -51200);
            form.Show();
            Application.DoEvents();
            form.VerifySelfTest();
        }
    }

    private static void VerifySettingsWindowActivationPolicy()
    {
        WidgetSettings baseline = WidgetSettings.CreateDefaults();
        using (Win11SettingsForm form = new Win11SettingsForm(null, baseline))
        {
            if (!form.ShowInTaskbar)
            {
                throw new InvalidOperationException("WinUI settings window must be visible in taskbar and Alt+Tab.");
            }
        }
    }

    private static void VerifyUnsavedPreviewConsumePolicy()
    {
        WidgetSettings baseline = WidgetSettings.CreateDefaults();
        using (Win11SettingsForm form = new Win11SettingsForm(null, baseline))
        {
            WidgetSettings revertSettings;
            if (!form.TryConsumeUnsavedPreview(out revertSettings) ||
                revertSettings == null ||
                !object.Equals(revertSettings.OperationPrimaryPanelMode, baseline.OperationPrimaryPanelMode))
            {
                throw new InvalidOperationException("WinUI settings unsaved preview consume failed.");
            }

            if (form.TryConsumeUnsavedPreview(out revertSettings))
            {
                throw new InvalidOperationException("WinUI settings unsaved preview consumed twice.");
            }
        }

        using (Win11SettingsForm form = new Win11SettingsForm(null, baseline))
        {
            form.OwnerFormClosing = true;
            WidgetSettings revertSettings;
            if (form.TryConsumeUnsavedPreview(out revertSettings))
            {
                throw new InvalidOperationException("WinUI settings owner-closing preview should not be consumed.");
            }
        }
    }


    private void DumpLayout()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "settings_layout_dump.txt");
            using (System.IO.StreamWriter w = new System.IO.StreamWriter(path))
            {
                DumpControl(this, w, 0);
            }
        }
        catch (Exception ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductIdentity.MachineName);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dump_error.txt"), ex.ToString());
            }
            catch { }
        }
    }

    private void DumpControl(Control c, System.IO.StreamWriter w, int indent)
    {
        string ind = new string(' ', indent * 2);
        w.WriteLine($"{ind}{c.GetType().Name} '{c.Text}' - Bounds: {c.Bounds}, ClientSize: {c.ClientSize}, Margin: {c.Margin}, Padding: {c.Padding}, AutoSize: {c.AutoSize}, Dock: {c.Dock}, Visible: {c.Visible}");
        if (c is TableLayoutPanel tlp)
        {
            w.WriteLine($"{ind}  RowStyles: {tlp.RowStyles.Count}, ColStyles: {tlp.ColumnStyles.Count}, AutoSizeMode: {tlp.AutoSizeMode}");
        }
        if (c is FlowLayoutPanel flp)
        {
            w.WriteLine($"{ind}  FlowDirection: {flp.FlowDirection}, WrapContents: {flp.WrapContents}, AutoSizeMode: {flp.AutoSizeMode}");
        }
        foreach (Control child in c.Controls)
        {
            DumpControl(child, w, indent + 1);
        }
    }

    private static void ForceResourceCleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Application.DoEvents();
    }

    private void VerifySelfTest()
    {
        string[] required = new string[]
        {
            "PerformanceMode",
            "HoverOpacityEnabled",
            "HoverOpacityRevealDelayEnabled",
            "OperationRadialCoreAutoHideKeepAliveEnabled",
            "OperationRadialIdleCollapseSeconds",
            "OperationRadialIdleResetOnInteractionEnabled",
            "OperationRadialKeepOpenAfterLeafClickEnabled",
            "FallbackDisconnectedDisplaysEnabled",
            "ResolutionCompatibilityModeEnabled",
            "ResolutionCompatibilityScalePercent",
            "MainDisplayDeviceName",
            "CodexRadarDisplayDeviceName",
            "ClaudeRadarDisplayDeviceName",
            GlobalLayoutEditCommandName,
            "Width",
            "CodexRadarEnabled",
            "CodexRadarSoftwareMode",
            "CodexRadarModelKey",
            "RadarClockAutoSwitchModelEnabled",
            "RadarClockTimeDisplayMode",
            "DisplayTimeZoneMode",
            "DisplayTimeZoneId",
            "CodexRadarPublicJsonEnabled",
            "CodexRadarHtmlFallbackEnabled",
            "CodexRadarRssFallbackEnabled",
            "CodexRadarServiceProbeToken",
            "CodexQuotaDueResetProtectionEnabled",
            "CodexQuotaRssResetProtectionEnabled",
            "CodexQuotaProviderZeroDropProtectionEnabled",
            "CodexQuotaDuplicateSameBalanceRingProtectionEnabled",
            "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled",
            "CodexQuotaProviderWeeklySpikeProtectionEnabled",
            "CodexQuotaStrictFiveHourResetBoundaryEnabled",
            "CodexQuotaWeeklyBaselineAutoRepairEnabled",
            "ClaudeRadarEnabled",
            "ClaudeRadarModelKey",
            "ClaudeRadarJsonEnabled",
            "ClaudeRadarCommunityRatingsEnabled",
            "ClaudeRadarLocalQuotaFallbackEnabled",
            "ClaudeRadarServiceProbeToken",
            ClaudeSetupTokenCommandName,
            "DeepSeekApiKeyRevision",
            "AiRequestProtectionAutoEnabled",
            "AiRequestProtectionManualBlockEnabled",
            "CodexQuotaPlanEnabled",
            "CodexQuotaPlanWeeklyComparison",
            "CodexQuotaPlanWeeklyThresholdPercent",
            "CodexQuotaPlanFiveHourComparison",
            "CodexQuotaPlanFiveHourThresholdPercent",
            "CodexQuotaPlanResumeConditionMode",
            "CodexQuotaPlanAutoResumePausedGoals",
            "CodexQuotaPlanPauseGoalIds",
            "CodexQuotaPlanResumeGoalIds",
            "OperationRenderVariant",
            "PowerThermalAutoSizeEnabled",
            "PowerThermalManualEnergySaverThresholdPercent",
            "GfwProbeIntervalMinutes",
            "OperationButtonSize",
            "OperationPrimaryPanelMode",
            "OperationSettingsLogicExtensionEnabled"
        };

        for (int i = 0; i < required.Length; i++)
        {
            if (!this.editors.ContainsKey(required[i]))
            {
                throw new InvalidOperationException("WinUI settings binding missing: " + required[i]);
            }
        }

        for (int i = 0; i < this.pages.Count; i++)
        {
            LayoutPage(this.pages[i]);
        }

        VerifyClaudeRadarModelGridPolicy();
        VerifyVariantPickerPolicy();
        VerifyDynamicResolutionSizingPolicy();
        VerifyNoVisibleControlClipping();

        WidgetSettings settings = ReadSettings();
        if (settings == null)
        {
            throw new InvalidOperationException("WinUI settings read failed.");
        }

        FluentScrollPanel page = GetSelectedScrollPage();
        if (page == null || !page.ScrollByMouseWheelDelta(-120))
        {
            throw new InvalidOperationException("WinUI settings page wheel scroll failed.");
        }
    }

    private void VerifyClaudeRadarModelGridPolicy()
    {
        SettingEditor editor;
        if (!this.editors.TryGetValue("ClaudeRadarModelKey", out editor))
        {
            throw new InvalidOperationException("Claude Radar model grid editor missing.");
        }

        FlowLayoutPanel grid = FindClaudeRadarModelGrid(editor.Control);
        if (grid == null)
        {
            throw new InvalidOperationException("Claude Radar model selector must use the responsive button grid.");
        }

        if (grid.Controls.Count < ClaudeRadarModelGridColumns)
        {
            throw new InvalidOperationException("Claude Radar model grid slot count is below the maximum row size.");
        }

        int columns = GetClaudeRadarModelColumnCountForGrid(grid.Width);
        int buttonWidth = GetClaudeRadarModelButtonWidthForGrid(grid.Width);
        int rowWidth = columns * buttonWidth +
            Math.Max(0, columns - 1) * ClaudeRadarModelButtonGap;
        if (rowWidth > grid.Width)
        {
            throw new InvalidOperationException("Claude Radar model grid buttons exceed selector width.");
        }

        int preferredGridWidth = GetClaudeRadarModelGridPreferredWidth();
        if (grid.Width >= preferredGridWidth && buttonWidth < ClaudeRadarModelButtonWidth)
        {
            throw new InvalidOperationException("Claude Radar model grid preferred button width was reduced.");
        }

        int compactWidth = 280;
        int compactColumns = GetClaudeRadarModelColumnCountForGrid(compactWidth);
        int compactButtonWidth = GetClaudeRadarModelButtonWidthForGrid(compactWidth);
        if (compactColumns != 3 || compactButtonWidth < ClaudeRadarModelButtonMinimumWidth)
        {
            throw new InvalidOperationException("Claude Radar model grid does not expand cells in narrow settings rows.");
        }

        string commonLongLabel = GetClaudeRadarModelButtonText(
            new ClaudeModelOption("m1", "Opus 4.8 high", true, false));
        if (!string.Equals(commonLongLabel, "Opus 4.8 high", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar model grid truncates common model labels.");
        }

        bool sawAuto = false;
        for (int i = 0; i < grid.Controls.Count; i++)
        {
            Button button = grid.Controls[i] as Button;
            if (button == null)
            {
                throw new InvalidOperationException("Claude Radar model grid contains a non-button slot.");
            }

            if (button.Width != buttonWidth)
            {
                throw new InvalidOperationException("Claude Radar model grid button width is not responsive.");
            }

            ClaudeModelOption option = button.Tag as ClaudeModelOption;
            if (option == null)
            {
                if (button.Enabled || !string.Equals(button.Text, "--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Claude Radar empty model slot must be disabled --.");
                }
                continue;
            }

            if (option.Key.Length == 0)
            {
                sawAuto = true;
                if (!button.Enabled)
                {
                    throw new InvalidOperationException("Claude Radar automatic model slot must be selectable.");
                }
            }
            else if (!option.Available && button.Enabled)
            {
                throw new InvalidOperationException("Claude Radar unavailable model slot must be disabled.");
            }
        }

        if (!sawAuto)
        {
            throw new InvalidOperationException("Claude Radar model grid missing automatic slot.");
        }
    }

    private void VerifyVariantPickerPolicy()
    {
        string[] pickerNames = new string[]
        {
            "OperationRenderVariant"
        };

        for (int i = 0; i < pickerNames.Length; i++)
        {
            SettingEditor editor;
            if (!this.editors.TryGetValue(pickerNames[i], out editor))
            {
                throw new InvalidOperationException("Render variant picker binding missing: " + pickerNames[i]);
            }

            if (!(editor.Control is VariantPicker))
            {
                throw new InvalidOperationException("Render variant must use VariantPicker: " + pickerNames[i]);
            }
        }
    }

    private void VerifyNoVisibleControlClipping()
    {
        Size originalSize = this.ClientSize;
        Size originalMinimum = this.MinimumSize;
        int originalPageIndex = this.selectedPageIndex;
        try
        {
            Size[] testSizes = new Size[]
            {
                this.MinimumSize,
                GetMinimumWindowSizeForWorkArea(new Rectangle(0, 0, 1280, 680)),
                GetMinimumWindowSizeForWorkArea(new Rectangle(0, 0, 1024, 728))
            };

            for (int sizeIndex = 0; sizeIndex < testSizes.Length; sizeIndex++)
            {
                this.MinimumSize = testSizes[sizeIndex];
                this.ClientSize = testSizes[sizeIndex];
                Application.DoEvents();
                VerifyNoVisibleControlClippingAtCurrentSize();
            }
        }
        finally
        {
            this.MinimumSize = originalMinimum;
            this.ClientSize = originalSize;
            SelectPage(originalPageIndex);
            Application.DoEvents();
        }
    }

    private void VerifyNoVisibleControlClippingAtCurrentSize()
    {
        for (int i = 0; i < this.pages.Count; i++)
        {
            SelectPage(i);
            CategoryPage page = this.pages[i];
            LayoutPage(page);
            for (int j = 0; j < page.Editors.Count; j++)
            {
                SettingEditor editor = page.Editors[j];
                SettingRow card = editor.Card;
                Control control = editor.Control;
                card.RefreshLayoutForWidth(card.ClientSize.Width);
                int rightLimit = card.ClientSize.Width - card.Padding.Right + 1;
                int bottomLimit = card.ClientSize.Height - card.Padding.Bottom + 1;
                if (control.Left < card.Padding.Left ||
                    control.Right > rightLimit ||
                    control.Bottom > bottomLimit ||
                    card.TitleLabel.Right > rightLimit ||
                    card.HintLabel.Right > rightLimit)
                {
                    throw new InvalidOperationException(
                        "WinUI settings layout clipped: " + editor.Name +
                        " card=" + card.ClientSize.ToString() +
                        " padding=" + card.Padding.ToString() +
                        " control=" + control.Bounds.ToString() +
                        " title=" + card.TitleLabel.Bounds.ToString() +
                        " hint=" + card.HintLabel.Bounds.ToString() +
                        " rightLimit=" + rightLimit.ToString(CultureInfo.InvariantCulture) +
                        " bottomLimit=" + bottomLimit.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
    }

    private static void VerifyDynamicResolutionSizingPolicy()
    {
        Rectangle[] workAreas = new Rectangle[]
        {
            new Rectangle(0, 60, 2880, 1740),
            new Rectangle(0, 0, 1920, 1040),
            new Rectangle(0, 0, 1600, 860),
            new Rectangle(0, 0, 1366, 728),
            new Rectangle(0, 0, 1280, 680),
            new Rectangle(0, 0, 1024, 728)
        };

        for (int i = 0; i < workAreas.Length; i++)
        {
            Size preferred = GetPreferredClientSizeForWorkArea(workAreas[i]);
            Size minimum = GetMinimumWindowSizeForWorkArea(workAreas[i]);
            if (preferred.Width > workAreas[i].Width ||
                preferred.Height > workAreas[i].Height ||
                minimum.Width > workAreas[i].Width ||
                minimum.Height > workAreas[i].Height ||
                minimum.Width > preferred.Width ||
                minimum.Height > preferred.Height)
            {
                throw new InvalidOperationException(
                    "WinUI settings dynamic resolution sizing failed: " +
                    workAreas[i].Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    workAreas[i].Height.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Static Dictionaries
    // ═════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, NumericRange> NumericRanges = new Dictionary<string, NumericRange>(StringComparer.Ordinal)
    {
        { "Width", new NumericRange(WidgetSettings.MinWidth, WidgetSettings.MaxWidth) },
        { "Height", new NumericRange(WidgetSettings.MinHeight, WidgetSettings.MaxHeight) },
        { "CodexRadarWidth", new NumericRange(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MaxCodexRadarWidth) },
        { "CodexRadarHeight", new NumericRange(WidgetSettings.MinCodexRadarHeight, WidgetSettings.MaxCodexRadarHeight) },
        { "ClaudeRadarWidth", new NumericRange(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MaxCodexRadarWidth) },
        { "ClaudeRadarHeight", new NumericRange(WidgetSettings.MinCodexRadarHeight, WidgetSettings.MaxCodexRadarHeight) },
        { "CodexRadarManualLeftPercent", new NumericRange(WidgetSettings.MinCodexRadarManualLeftPercent, WidgetSettings.MaxCodexRadarManualLeftPercent) },
        { "CodexRadarManualGapPixels", new NumericRange(WidgetSettings.MinCodexRadarManualGapPixels, WidgetSettings.MaxCodexRadarManualGapPixels) },
        { "CodexRadarManualEfficiencyTextWidthPixels", new NumericRange(WidgetSettings.MinCodexRadarManualEfficiencyTextWidthPixels, WidgetSettings.MaxCodexRadarManualEfficiencyTextWidthPixels) },
        { "CodexRadarManualQuotaRowsWidthPixels", new NumericRange(WidgetSettings.MinCodexRadarManualQuotaRowsWidthPixels, WidgetSettings.MaxCodexRadarManualQuotaRowsWidthPixels) },
        { "CodexRadarManualIqStatusWidthPixels", new NumericRange(WidgetSettings.MinCodexRadarManualIqStatusWidthPixels, WidgetSettings.MaxCodexRadarManualIqStatusWidthPixels) },
        { "CodexRadarManualTextScalePercent", new NumericRange(WidgetSettings.MinCodexRadarManualTextScalePercent, WidgetSettings.MaxCodexRadarManualTextScalePercent) },
        { "CodexRadarManualRingScalePercent", new NumericRange(WidgetSettings.MinCodexRadarManualRingScalePercent, WidgetSettings.MaxCodexRadarManualRingScalePercent) },
        { "PowerThermalWidth", new NumericRange(WidgetSettings.MinPowerThermalWidth, WidgetSettings.MaxPowerThermalWidth) },
        { "PowerThermalHeight", new NumericRange(WidgetSettings.MinPowerThermalHeight, WidgetSettings.MaxPowerThermalHeight) },
        { "PowerThermalVisibleAlertCount", new NumericRange(WidgetSettings.MinPowerThermalVisibleAlerts, WidgetSettings.MaxPowerThermalVisibleAlerts) },
        { "PowerThermalManualEnergySaverThresholdPercent", new NumericRange(WidgetSettings.MinPowerThermalManualEnergySaverThresholdPercent, WidgetSettings.MaxPowerThermalManualEnergySaverThresholdPercent) },
        { "NetworkMonitorWidth", new NumericRange(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorWidth) },
        { "NetworkMonitorHeight", new NumericRange(WidgetSettings.MinNetworkMonitorHeight, WidgetSettings.MaxNetworkMonitorHeight) },
        { "GfwProbeIntervalMinutes", new NumericRange(WidgetSettings.MinGfwProbeIntervalMinutes, WidgetSettings.MaxGfwProbeIntervalMinutes) },
        { "ConnectionCheckWidth", new NumericRange(WidgetSettings.MinConnectionCheckWidth, WidgetSettings.MaxConnectionCheckWidth) },
        { "ConnectionCheckHeight", new NumericRange(WidgetSettings.MinConnectionCheckHeight, WidgetSettings.MaxConnectionCheckHeight) },
        { "ConnectionCheckIntervalSeconds", new NumericRange(WidgetSettings.MinConnectionCheckIntervalSeconds, WidgetSettings.MaxConnectionCheckIntervalSeconds) },
        { "ConnectionCheckBorderTransparencyPercent", new NumericRange(WidgetSettings.MinBorderTransparency, WidgetSettings.MaxBorderTransparency) },
        { "OperationButtonSize", new NumericRange(WidgetSettings.MinOperationButtonSize, WidgetSettings.MaxOperationButtonSize) },
        { "OperationLeftOffset", new NumericRange(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset) },
        { "OperationBottomOffset", new NumericRange(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset) },
        { "ResolutionCompatibilityScalePercent", new NumericRange(WidgetSettings.MinResolutionCompatibilityScalePercent, WidgetSettings.MaxResolutionCompatibilityScalePercent) },
        { "SensitiveMouseRangePixels", new NumericRange(WidgetSettings.MinSensitiveMouseRangePixels, WidgetSettings.MaxSensitiveMouseRangePixels) },
        { "HoverOpacityRevealDelaySeconds", new NumericRange((decimal)WidgetSettings.MinHoverOpacityRevealDelaySeconds, (decimal)WidgetSettings.MaxHoverOpacityRevealDelaySeconds) },
        { "HoverOpacityRevealResetSeconds", new NumericRange((decimal)WidgetSettings.MinHoverOpacityRevealResetSeconds, (decimal)WidgetSettings.MaxHoverOpacityRevealResetSeconds) },
        { "ReverseHoverOpacityRestoreDelaySeconds", new NumericRange(WidgetSettings.MinReverseHoverOpacityRestoreDelaySeconds, WidgetSettings.MaxReverseHoverOpacityRestoreDelaySeconds) },
        { "AutoHoverOpacityIdleSeconds", new NumericRange(WidgetSettings.MinAutoHoverOpacityIdleSeconds, WidgetSettings.MaxAutoHoverOpacityIdleSeconds) },
        { "OperationRadialIdleCollapseSeconds", new NumericRange(WidgetSettings.NeverOperationRadialIdleCollapseSeconds, WidgetSettings.MaxOperationRadialIdleCollapseSeconds) },
        { "CodexQuotaPlanWeeklyThresholdPercent", new NumericRange(WidgetSettings.MinCodexQuotaPlanThresholdPercent, WidgetSettings.MaxCodexQuotaPlanThresholdPercent) },
        { "CodexQuotaPlanFiveHourThresholdPercent", new NumericRange(WidgetSettings.MinCodexQuotaPlanThresholdPercent, WidgetSettings.MaxCodexQuotaPlanThresholdPercent) },
        { "CodexModelIqTestPassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) },
        { "CodexModelIqBaselinePassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) },
        { "CodexModelIqBaselineValidTasks", new NumericRange(WidgetSettings.MinCodexModelIqValidTasks, WidgetSettings.MaxCodexModelIqValidTasks) },
        { "CodexModelTokenEfficiencyBaselinePassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) },
        { "CodexModelTimeEfficiencyBaselinePassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) }
    };

    private static readonly Dictionary<string, string> SettingTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "StartupEnabled", "开机启动" },
        { "PerformanceMode", "性能模式" },
        { "VisibilityMode", "可见性" },
        { "VisibilityOverlapIgnoresOperationPanelEnabled", "遮挡忽略操作面板" },
        { "ClickThroughMode", "点击穿透" },
        { "ForceShowForegroundFpsEnabled", "强制显示 FPS" },
        { "OperationPrimaryPanelMode", "左侧区域模式" },
        { "OperationWindowsButtonEnabled", "显示 Windows 按钮" },
        { "OperationMemoryPieEnabled", "显示内存饼图" },
        { "SeelenDockForegroundPulseEnabled", "Seelen Dock 自动拉前" },
        { "WinDRecoveryPulseEnabled", "Win+D 后延迟拉前" },
        { "PowerResumeRestartEnabled", "休眠唤醒后重启" },
        { "AiRequestProtectionAutoEnabled", "AI 自动阻断" },
        { "AiRequestProtectionManualBlockEnabled", "AI 手动阻断" },
        { "CodexQuotaPlanEnabled", "启用额度计划" },
        { "CodexQuotaPlanWeeklyComparison", "周额度条件" },
        { "CodexQuotaPlanWeeklyThresholdPercent", "周额度阈值" },
        { "CodexQuotaPlanFiveHourComparison", "5 小时条件" },
        { "CodexQuotaPlanFiveHourThresholdPercent", "5 小时阈值" },
        { "CodexQuotaPlanResumeConditionMode", "恢复额度类型" },
        { "CodexQuotaPlanAutoResumePausedGoals", "恢复上次暂停" },
        { "CodexQuotaPlanPauseGoalIds", "暂停 goal 列表" },
        { "CodexQuotaPlanResumeGoalIds", "恢复 goal 列表" },
        { "FallbackDisconnectedDisplaysEnabled", "断开后回退显示器" },
        { "MainDisplayDeviceName", "主窗口显示器" },
        { "CodexRadarDisplayDeviceName", "Codex Radar 显示器" },
        { "ClaudeRadarDisplayDeviceName", "Claude Radar 显示器" },
        { "PowerThermalDisplayDeviceName", "功耗温度显示器" },
        { "NetworkMonitorDisplayDeviceName", "网络监控显示器" },
        { "ConnectionCheckDisplayDeviceName", "连接检测显示器" },
        { "OperationDisplayDeviceName", "操作面板显示器" },
        { "ResolutionCompatibilityModeEnabled", "分辨率兼容模式" },
        { "ResolutionCompatibilityScalePercent", "兼容缩放比例" },
        { GlobalLayoutEditCommandName, "全局编辑" },
        { "Width", "主窗口宽度" },
        { "Height", "主窗口高度" },
        { "LeftX", "主窗口距左边" },
        { "BottomY", "主窗口距下边" },
        { "CodexRadarWidth", "Codex Radar 宽度" },
        { "CodexRadarHeight", "Codex Radar 高度" },
        { "CodexRadarLeftX", "Codex Radar 距左边" },
        { "CodexRadarBottomY", "Codex Radar 距下边" },
        { "PowerThermalWidth", "功耗温度宽度" },
        { "PowerThermalHeight", "功耗温度高度" },
        { "PowerThermalLeftX", "功耗温度距左边" },
        { "PowerThermalBottomY", "功耗温度距下边" },
        { "NetworkMonitorWidth", "网络监控宽度" },
        { "NetworkMonitorHeight", "网络监控高度" },
        { "NetworkMonitorLeftX", "网络监控距左边" },
        { "NetworkMonitorBottomY", "网络监控距下边" },
        { "ConnectionCheckWidth", "连接检测宽度" },
        { "ConnectionCheckHeight", "连接检测高度" },
        { "ConnectionCheckLeftX", "连接检测距左边" },
        { "ConnectionCheckBottomY", "连接检测距下边" },
        { "OperationLeftOffset", "操作面板距左边" },
        { "OperationBottomOffset", "操作面板距下边" },
        { "BackgroundTransparencyPercent", "主窗口背景透明度" },
        { "ApplicationTransparencyPercent", "主窗口整体透明度" },
        { "ShowCpu", "显示 CPU" },
        { "ShowMemory", "显示内存" },
        { "ShowDisk", "显示磁盘" },
        { "ShowNetwork", "显示网络" },
        { "ShowGpu", "显示 GPU" },
        { "ShowNpu", "显示 NPU" },
        { "HoverOpacityEnabled", "鼠标靠近时隐藏" },
        { "SensitiveMouseModeEnabled", "敏感鼠标模式" },
        { "SensitiveMouseRangePixels", "触发距离（像素）" },
        { "HoverOpacityRevealDelayEnabled", "延迟显现" },
        { "HoverOpacityRevealDelaySeconds", "显现延迟秒数" },
        { "HoverOpacityRevealResetSeconds", "重置秒数" },
        { "HoverOpacityCoverEnabled", "覆盖开启" },
        { "ReverseHoverOpacityRevealEnabled", "反向隐藏" },
        { "ReverseHoverOpacityRestoreDelaySeconds", "移开后恢复秒数" },
        { "AutoHoverOpacityIdleEnabled", "空闲自动隐藏" },
        { "AutoHoverOpacityIdleSeconds", "空闲隐藏秒数" },
        { "AutoHoverOpacityMaximizedEnabled", "最大化自动隐藏" },
        { "OperationRadialCoreAutoHideKeepAliveEnabled", "圆圈悬停保持显示" },
        { "OperationRadialIdleCollapseSeconds", "扇形盘自动收回秒数" },
        { "OperationRadialIdleResetOnInteractionEnabled", "操作后重置收回计时" },
        { "OperationRadialKeepOpenAfterLeafClickEnabled", "末端按钮后保持展开" },
        { "BurnInHiddenModeColorProtectionEnabled", "隐藏反色防烧屏" },
        { "CodexRadarSoftwareMode", "共享窗检测对象" },
        { "CodexRadarModelKey", "CODEX 模型" },
        { "CodexRadarModelVersion", "旧模型版本" },
        { "RadarClockAutoSwitchModelEnabled", "过期自动切换模型" },
        { "RadarClockTimeDisplayMode", "时钟时间显示" },
        { "CodexRadarEnabled", "启用共享 Radar 小窗" },
        { "CodexRadarTransparencyPercent", "Codex Radar 透明度" },
        { "ClaudeRadarEnabled", "启用独立 Claude Radar" },
        { "ClaudeRadarWidth", "Claude Radar 宽度" },
        { "ClaudeRadarHeight", "Claude Radar 高度" },
        { "ClaudeRadarLeftX", "Claude Radar 左侧 X" },
        { "ClaudeRadarBottomY", "Claude Radar 底部 Y" },
        { "ClaudeRadarTransparencyPercent", "Claude Radar 背景透明" },
        { "ClaudeRadarModelKey", "Claude 模型映射" },
        { "ClaudeRadarJsonEnabled", "Claude 站点 JSON" },
        { "ClaudeRadarHomepageFallbackEnabled", "首页模型元数据回退" },
        { "ClaudeRadarCommunityRatingsEnabled", "Claude 社区体感分" },
        { "ClaudeRadarLocalQuotaFallbackEnabled", "本地 7 天额度线回退" },
        { "ClaudeRadarServiceProbeToken", "检查 Claude 数据链路" },
        { ClaudeSetupTokenCommandName, "Claude Code 用量令牌" },
        { "ClaudeRadarRandomTestEnabled", "Claude 随机测试" },
        { "ClaudeRadarRandomTestAutoRefresh", "Claude 随机测试自动刷新" },
        { "ClaudeRadarRandomTestRefreshToken", "立即刷新随机测试" },
        { "CodexRadarRandomTestEnabled", "随机测试" },
        { "CodexRadarRandomTestAutoRefresh", "随机测试自动刷新" },
        { "CodexRadarRandomTestRefreshToken", "立即刷新随机测试" },
        { "OperationRenderVariant", "外观风格" },
        { "OperationSettingsLogicExtensionEnabled", "设置扩展到操作逻辑" },
        { "CodexRadarManualLayoutEnabled", "启用手动布局" },
        { "CodexRadarManualLeftPercent", "左侧区域占比" },
        { "CodexRadarManualGapPixels", "模块间距" },
        { "CodexRadarManualEfficiencyTextWidthPixels", "效率文字列宽" },
        { "CodexRadarManualQuotaRowsWidthPixels", "余额列宽" },
        { "CodexRadarManualIqStatusWidthPixels", "IQ 状态列宽" },
        { "CodexRadarManualTextScalePercent", "文字比例" },
        { "CodexRadarManualRingScalePercent", "圆环比例" },
        { "CodexRadarTimeEfficiencyRingOffsetX", "时间环 X" },
        { "CodexRadarTimeEfficiencyRingOffsetY", "时间环 Y" },
        { "CodexRadarTimeEfficiencyTextOffsetX", "时间字 X" },
        { "CodexRadarTimeEfficiencyTextOffsetY", "时间字 Y" },
        { "CodexRadarTokenEfficiencyRingOffsetX", "Token 环 X" },
        { "CodexRadarTokenEfficiencyRingOffsetY", "Token 环 Y" },
        { "CodexRadarTokenEfficiencyTextOffsetX", "Token 字 X" },
        { "CodexRadarTokenEfficiencyTextOffsetY", "Token 字 Y" },
        { "CodexRadarConnectionTopTextOffsetX", "连接上字 X" },
        { "CodexRadarConnectionTopTextOffsetY", "连接上字 Y" },
        { "CodexRadarConnectionLineOffsetX", "连接线 X" },
        { "CodexRadarConnectionLineOffsetY", "连接线 Y" },
        { "CodexRadarConnectionBottomTextOffsetX", "连接下字 X" },
        { "CodexRadarConnectionBottomTextOffsetY", "连接下字 Y" },
        { "CodexRadarFiveHourQuotaRingOffsetX", "5h 环 X" },
        { "CodexRadarFiveHourQuotaRingOffsetY", "5h 环 Y" },
        { "CodexRadarFiveHourQuotaTextOffsetX", "5h 字 X" },
        { "CodexRadarFiveHourQuotaTextOffsetY", "5h 字 Y" },
        { "CodexRadarWeeklyQuotaRingOffsetX", "周环 X" },
        { "CodexRadarWeeklyQuotaRingOffsetY", "周环 Y" },
        { "CodexRadarWeeklyQuotaTextOffsetX", "周字 X" },
        { "CodexRadarWeeklyQuotaTextOffsetY", "周字 Y" },
        { "CodexRadarQuotaRadarLineOffsetX", "额度线 X" },
        { "CodexRadarQuotaRadarLineOffsetY", "额度线 Y" },
        { "CodexRadarIqRingOffsetX", "IQ 环 X" },
        { "CodexRadarIqRingOffsetY", "IQ 环 Y" },
        { "CodexRadarIqTextOffsetX", "IQ 字 X" },
        { "CodexRadarIqTextOffsetY", "IQ 字 Y" },
        { "CodexRadarPublicJsonEnabled", "Codex 公开 JSON" },
        { "CodexRadarHtmlFallbackEnabled", "Codex 首页 HTML 回退" },
        { "CodexRadarRssFallbackEnabled", "Codex RSS 重置提醒" },
        { "CodexRadarServiceProbeToken", "检查 Codex 数据链路" },
        { "CodexQuotaDueResetProtectionEnabled", "到期重置保护" },
        { "CodexQuotaRssResetProtectionEnabled", "RSS 重置保护" },
        { "CodexQuotaProviderZeroDropProtectionEnabled", "Provider 零值保护" },
        { "CodexQuotaDuplicateSameBalanceRingProtectionEnabled", "相同余额保留消耗环" },
        { "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled", "5h 提前满额保护" },
        { "CodexQuotaProviderWeeklySpikeProtectionEnabled", "周额度突增保护" },
        { "CodexQuotaStrictFiveHourResetBoundaryEnabled", "严格 5h 边界" },
        { "CodexQuotaWeeklyBaselineAutoRepairEnabled", "周基线自动修复" },
        { "DeepSeekApiKeyRevision", "DeepSeek 余额配置" },
        { "CodexModelIqTestEnabled", "用测试值代替实时 IQ（调试用）" },
        { "CodexModelIqTestPassed", "IQ 测试通过数" },
        { "CodexModelIqBaselineAutoEnabled", "IQ 基准自动跟随网站" },
        { "CodexModelIqBaselineMode", "IQ 基准模式" },
        { "CodexModelIqBaselinePassed", "IQ 基准通过数" },
        { "CodexModelIqBaselineValidTasks", "IQ 基准总题数" },
        { "CodexModelEfficiencyTestEnabled", "用测试值代替实时效率（调试用）" },
        { "CodexModelTokenEfficiencyTestPercent", "Token 效率测试百分比" },
        { "CodexModelTimeEfficiencyTestPercent", "时间效率测试百分比" },
        { "CodexModelTokenEfficiencyBaselineMode", "Token 效率基准模式" },
        { "CodexModelTimeEfficiencyBaselineMode", "时间效率基准模式" },
        { "CodexModelTokenEfficiencyBaselineTokens", "Token 效率基准值" },
        { "CodexModelTimeEfficiencyBaselineSeconds", "时间效率基准秒数" },
        { "CodexModelTokenEfficiencyLowThresholdPercent", "Token 低效阈值" },
        { "CodexModelTimeEfficiencyLowThresholdPercent", "时间低效阈值" },
        { "DisplayTimeZoneId", "通用显示时区 ID" },
        { "DisplayTimeZoneMode", "通用显示时区模式" },
        { "PowerThermalAutoSizeEnabled", "功耗模块自动大小" },
        { "PowerThermalAutoDirection", "自动大小方向" },
        { "PowerThermalVisibleAlertCount", "可见告警数量" },
        { "PowerThermalManualEnergySaverThresholdPercent", "手动节能阈值" },
        { "PowerThermalTransparencyPercent", "功耗温度透明度" },
        { "NetworkMonitorAdapterId", "网络适配器 ID" },
        { "NetworkMonitorTransparencyPercent", "网络监控透明度" },
        { "NetworkStatusTestMode", "网络状态测试" },
        { "GfwProbeIntervalMinutes", "GFW 检测间隔" },
        { "GfwProbeEnabled", "启用 GFW 检测" },
        { "GfwProbeManualRefreshToken", "立即刷新 GFW 检测" },
        { "CloudEndpointTestSeed", "云服务测试种子" },
        { "CloudStatusRegionMask", "云服务地区掩码" },
        { "ConnectionCheckIntervalSeconds", "连接检测间隔" },
        { "ConnectionCheckTransparencyPercent", "连接检测透明度" },
        { "ConnectionCheckBorderTransparencyPercent", "连接检测边框透明度" },
        { "ConnectionCheckManualRefreshToken", "立即刷新连接检测" },
        { "CleanIpBadgeTestMode", "出口身份测试模式" },
        { "ThermalTestMode", "温控测试模式" },
        { "AlertTestEnabled", "告警测试" },
        { "OperationButtonSize", "按钮大小" },
        { "OperationBackgroundTransparencyPercent", "操作面板透明度" },
        { "CodexModelTokenEfficiencyBaselinePassed", "Token 效率基准通过数" },
        { "CodexModelTimeEfficiencyBaselinePassed", "时间效率基准通过数" }
    };

    private static readonly Dictionary<string, string> SettingHints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "StartupEnabled", "写入当前用户启动项。" },
        { "PerformanceMode", "控制采样、动画和后台刷新节奏。" },
        { "VisibilityMode", "五档：总是可见、全屏时不可见、最大化时不可见、遮挡时不可见、仅桌面可见；默认全屏时不可见。最大化档也包含全屏。" },
        { "VisibilityOverlapIgnoresOperationPanelEnabled", "仅在“遮挡时不可见”生效；开启后左下角操作面板及其展开区域不会因为被其他应用窗口覆盖而隐藏。" },
        { "ClickThroughMode", "允许鼠标事件穿透主窗口。" },
        { "ForceShowForegroundFpsEnabled", "调试用，强制显示前台 FPS 信息。" },
        { "AiRequestProtectionAutoEnabled", "网络监控判定为 GFW 明确阻断时，阻断本程序发往 OpenAI、ChatGPT、Claude 和 Anthropic 的请求。" },
        { "AiRequestProtectionManualBlockEnabled", "手动启用后立即阻断本程序相关 AI 请求；也可在左下角程序设置按钮单击打开的特殊设置中切换。" },
        { "CodexQuotaPlanEnabled", "按本地 Codex 剩余额度缓存自动暂停或恢复选中的 Codex goal。" },
        { "CodexQuotaPlanWeeklyComparison", "周额度百分比的比较方向；默认小于 3%。" },
        { "CodexQuotaPlanWeeklyThresholdPercent", "周额度剩余百分比阈值，范围 0-100。" },
        { "CodexQuotaPlanFiveHourComparison", "5 小时额度百分比的比较方向；默认小于 90%。" },
        { "CodexQuotaPlanFiveHourThresholdPercent", "5 小时额度剩余百分比阈值，范围 0-100。" },
        { "CodexQuotaPlanResumeConditionMode", "额度计划触发后，选择额度恢复时看周额度、5 小时额度，还是两者都恢复。" },
        { "CodexQuotaPlanAutoResumePausedGoals", "额度恢复时优先恢复本程序上次因额度计划暂停的 goal；关闭后使用恢复列表。" },
        { "CodexQuotaPlanPauseGoalIds", "达到额度计划触发条件时暂停的 Codex goal ID，用 | 分隔；可从本地 goal 管理记录复制。" },
        { "CodexQuotaPlanResumeGoalIds", "关闭“恢复上次暂停”时使用，额度恢复后启用这些 Codex goal ID，用 | 分隔。" },
        { "HoverOpacityEnabled", "鼠标接近窗口时进入隐藏透明度。" },
        { "SensitiveMouseModeEnabled", "使用鼠标周围方形区域判断命中。" },
        { "SensitiveMouseRangePixels", "范围 10-300，值越大越容易触发。" },
        { "HoverOpacityRevealDelayEnabled", "鼠标离开后延迟恢复显示。" },
        { "HoverOpacityRevealDelaySeconds", "鼠标离开后继续隐藏的秒数。" },
        { "HoverOpacityRevealResetSeconds", "短时间重新靠近时，保持隐藏的重置秒数。" },
        { "HoverOpacityCoverEnabled", "自动隐藏时只在移到窗口上方后恢复。" },
        { "ReverseHoverOpacityRevealEnabled", "手动隐藏下鼠标移入窗口临时恢复。" },
        { "ReverseHoverOpacityRestoreDelaySeconds", "反向隐藏临时恢复后，鼠标移开多久回到隐藏。" },
        { "AutoHoverOpacityIdleEnabled", "鼠标一段时间不动时自动进入隐藏透明度。" },
        { "AutoHoverOpacityIdleSeconds", "范围 1-300 秒，达到后自动隐藏。" },
        { "AutoHoverOpacityMaximizedEnabled", "窗口状态缓存检测到非本程序、非 SeelenUI 应用最大化或全屏时自动进入隐藏透明度。" },
        { "OperationRadialCoreAutoHideKeepAliveEnabled", "鼠标停在左下角扇形速控盘核心圆圈上时，暂停并重置自动隐藏计时器，让所有隐藏透明度窗口保持显示。" },
        { "OperationRadialIdleCollapseSeconds", "范围 1-60 秒；设为 0 表示永不自动收回扇形速控盘。" },
        { "OperationRadialIdleResetOnInteractionEnabled", "开启后鼠标移动、按下或展开新分支会重新开始扇形速控盘自动收回计时。" },
        { "OperationRadialKeepOpenAfterLeafClickEnabled", "开启后点击扇形速控盘末端按钮不会自动收起菜单；关闭后恢复点击末端按钮即收起。" },
        { "OperationSettingsLogicExtensionEnabled", "开启后在扇形速控盘“设置”分支中增加常用逻辑和全部开关目录；关闭时保持原来的 3 项设置菜单。" },
        { "BurnInHiddenModeColorProtectionEnabled", "隐藏时执行颜色反相和白灰透明化。" },
        { "CodexRadarSoftwareMode", "只影响 Codex Radar 这个共享小窗：自动按前台和运行态选择，或固定显示 CODEX/CLAUDE 数据。独立 Claude Radar 不受这里影响。" },
        { "CodexRadarModelKey", "共享小窗处于 CODEX 模式时使用的 CodexRadar 模型；CLAUDE 模式使用 Claude 模型映射。" },
        { "CodexRadarModelVersion", "兼容旧配置的模型版本枚举；日常使用上方 CODEX 模型选择，不需要改这里。" },
        { "RadarClockAutoSwitchModelEnabled", "Codex/Claude Radar 时钟跨过完整周期仍没有当前模型 IQ 更新时，自动切到同站点当天最近刷新 IQ 的模型。" },
        { "RadarClockTimeDisplayMode", "控制两个 Radar 时钟中心下方时间：UTC、当前本机时间、上次尝试刷新，或上次实际 IQ 刷新。" },
        { "CodexRadarPublicJsonEnabled", "读取 codexradar.com/current.json 公开摘要层，包含窗口、预测和 API 可用性说明。" },
        { "CodexRadarHtmlFallbackEnabled", "公开 JSON 缺少展示字段时，从 codexradar.com 首页补齐 IQ、效率、额度线和模型目录展示数据。" },
        { "CodexRadarRssFallbackEnabled", "读取 CodexRadar feed.xml 的重置提醒；关闭后不会用 RSS 触发额度重置保护。" },
        { "CodexRadarServiceProbeToken", "点击后探测 Codex 公开摘要、授权 API、首页 HTML 和 RSS，并写入本地诊断结果。" },
        { "CodexQuotaDueResetProtectionEnabled", "本地 resets_at 到期后临时把对应余额显示为 100，直到新样本证明进入下一窗口；推荐保持开启。" },
        { "CodexQuotaRssResetProtectionEnabled", "CodexRadar RSS 出现新的重置记录时触发额外重置保护；推荐保持开启。" },
        { "CodexQuotaProviderZeroDropProtectionEnabled", "Provider 突然把高余额报成 0 且 reset 边界未推进时拒绝该样本；推荐保持开启。" },
        { "CodexQuotaDuplicateSameBalanceRingProtectionEnabled", "连续读取到相同余额时保留已有消耗环，不因重复日志清空尾段；推荐保持开启。" },
        { "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled", "Provider 在旧 5 小时窗口未到期时突然报接近满额并后移 reset 时拒绝；默认关闭，避免拦截手动重置卡。" },
        { "CodexQuotaProviderWeeklySpikeProtectionEnabled", "Provider 在周窗口未到期时把低周余额跳到近满时拒绝；默认关闭，避免拦截手动重置卡。" },
        { "CodexQuotaStrictFiveHourResetBoundaryEnabled", "只有旧 5 小时 reset 已到期且边界推进时才重建周消耗基线；默认关闭，允许手动重置卡被余额上涨识别。" },
        { "CodexQuotaWeeklyBaselineAutoRepairEnabled", "检测到疑似被 100 污染的周消耗基线时自动修回当前周余额；默认关闭，避免隐藏真实重置卡效果。" },
        { "CodexRadarRandomTestEnabled", "仅用于测试显示效果，日常保持关闭。" },
        { "CodexRadarRandomTestAutoRefresh", "仅用于测试显示效果，日常保持关闭。" },
        { "CodexRadarRandomTestRefreshToken", "点击后让随机测试数据立刻换一组。" },
        { "CodexModelIqTestEnabled", "仅用于测试显示效果，日常保持关闭。" },
        { "CodexModelIqTestPassed", "手动指定 IQ 测试通过数。" },
        { "CodexModelIqBaselineAutoEnabled", "开启时自动读取网站有效题数和常态区推导 n/N；关闭后使用下方手动 n/N。" },
        { "CodexModelIqBaselineMode", "选择 IQ 对比基准的来源。" },
        { "CodexModelIqBaselinePassed", "手动指定 IQ 基准通过数 n。" },
        { "CodexModelIqBaselineValidTasks", "手动指定 IQ 基准总题数 N；仅在关闭自动基准时用于 IQ 环。" },
        { "CodexModelEfficiencyTestEnabled", "仅用于测试显示效果，日常保持关闭。" },
        { "CodexModelTokenEfficiencyTestPercent", "手动指定 Token 效率测试百分比。" },
        { "CodexModelTimeEfficiencyTestPercent", "手动指定时间效率测试百分比。" },
        { "CodexModelTokenEfficiencyBaselineMode", "选择 Token 效率对比基准的来源。" },
        { "CodexModelTokenEfficiencyBaselinePassed", "手动指定 Token 效率基准通过数。" },
        { "CodexModelTokenEfficiencyBaselineTokens", "手动指定 Token 效率基准消耗。" },
        { "CodexModelTimeEfficiencyBaselineMode", "选择时间效率对比基准的来源。" },
        { "CodexModelTimeEfficiencyBaselinePassed", "手动指定时间效率基准通过数。" },
        { "CodexModelTimeEfficiencyBaselineSeconds", "手动指定时间效率基准秒数。" },
        { "CodexModelTokenEfficiencyLowThresholdPercent", "低于该百分比时标记为偏低。" },
        { "CodexModelTimeEfficiencyLowThresholdPercent", "低于该百分比时标记为偏低。" },
        { "CodexRadarEnabled", "关闭后会释放共享 Codex Radar 分层窗口；该窗口仍可在 CLAUDE 模式显示 Claude 数据。" },
        { "DisplayTimeZoneMode", "控制 Radar 和其他窗口里的时间文字使用本机、北京时间还是指定时区。" },
        { "DisplayTimeZoneId", "Windows 时区 ID；仅在通用显示时区模式选择“指定时区”时生效。" },
        { "ClaudeRadarEnabled", "开启独立 Claude Radar 分层窗口；它固定显示 Claude 数据，不读取 Codex Radar 缓存。" },
        { "ClaudeRadarModelKey", "五列按钮选择 Claude Radar 站点 m* 模型；留空为自动，编辑映射用于维护站点模型和社区评分 key 的对应关系。" },
        { "ClaudeRadarJsonEnabled", "读取 claudecoderadar.com/data/claude-code-radar.json 作为主数据源。" },
        { "ClaudeRadarHomepageFallbackEnabled", "主 JSON 缺少模型元数据时，读取首页 MODEL_NAMES 作为弱回退；不会伪造 IQ、效率或额度。" },
        { "ClaudeRadarCommunityRatingsEnabled", "读取 Claude Radar 社区体感分接口，并通过模型映射表关联当前模型。" },
        { "ClaudeRadarLocalQuotaFallbackEnabled", "站点额度趋势不完整时使用本地 7 天 JSONL 历史绘制额度线。" },
        { "ClaudeRadarServiceProbeToken", "点击后触发 Claude Radar 数据链路检查；服务状态显示在窗口右侧 R/C/U 和 API 摘要中。" },
        { ClaudeSetupTokenCommandName, "Claude 桌面版不会主动上报用量，需要生成一次性长效令牌并粘贴进来；未配置时两个额度环会显示满环红色。" },
        { "ClaudeRadarRandomTestEnabled", "仅用于测试显示效果，日常保持关闭。" },
        { "ClaudeRadarRandomTestAutoRefresh", "仅用于测试显示效果，日常保持关闭。" },
        { "ClaudeRadarRandomTestRefreshToken", "点击后让随机测试数据立刻换一组。" },
        { "OperationRenderVariant", "切换后立即预览，可随时切回。" },
        { "DeepSeekApiKeyRevision", "配置共享 Radar 小窗 CLAUDE 模式和独立 Claude Radar 底部 DS 余额使用的 DeepSeek API Key；密钥只写入本地 DPAPI 文件，修订号只用于触发即时刷新。" },
        { "CodexRadarManualLayoutEnabled", "开启后下方布局参数实时影响 Codex Radar 内部模块，不需要重启。" },
        { "CodexRadarManualLeftPercent", "调整效率/IQ/连接流程区与余额区的左右分配。" },
        { "CodexRadarManualGapPixels", "调整左侧区域和余额区之间的像素间距。" },
        { "CodexRadarManualEfficiencyTextWidthPixels", "调整左侧效率/IQ 圆环右侧文字列宽。" },
        { "CodexRadarManualQuotaRowsWidthPixels", "调整余额两行圆环和重置时间的总宽度。" },
        { "CodexRadarManualIqStatusWidthPixels", "调整余额区右侧 IQ 圆环与降智文字列宽。" },
        { "CodexRadarManualTextScalePercent", "只影响 Codex Radar 内部状态文字和余额时间字号。" },
        { "CodexRadarManualRingScalePercent", "只影响 Codex Radar 内部效率、余额和 IQ 圆环大小。" },
        { "PowerThermalAutoSizeEnabled", "根据告警数量自动调整功耗温度窗口高度。" },
        { "PowerThermalAutoDirection", "自动调整时从哪个方向展开。" },
        { "PowerThermalVisibleAlertCount", "功耗温度窗口中最多显示的告警数量。" },
        { "PowerThermalManualEnergySaverThresholdPercent", "电量低于该百分比时，在功耗电池模块显示节能叶子和“（节能）”；0 表示关闭。默认 30。" },
        { "ThermalTestMode", "仅用于测试显示效果，日常保持关闭。" },
        { "NetworkMonitorAdapterId", "留空时自动选择网络适配器。" },
        { "NetworkStatusTestMode", "仅用于测试显示效果，日常保持关闭。" },
        { "GfwProbeEnabled", "与云服务检测独立调度。" },
        { "GfwProbeIntervalMinutes", "GFW 检测的自动刷新间隔。" },
        { "GfwProbeManualRefreshToken", "点击后请求立刻刷新 GFW 检测。" },
        { "ConnectionCheckManualRefreshToken", "点击后请求立刻刷新连接检测。" },
        { "ConnectionCheckIntervalSeconds", "连接检测的自动刷新间隔。" },
        { "CloudEndpointTestSeed", "仅用于测试显示效果，日常保持默认。" },
        { "CloudStatusRegionMask", "云服务状态的地区过滤位掩码，日常保持默认。" },
        { "CleanIpBadgeTestMode", "仅用于测试显示效果，日常保持关闭。" },
        { "AlertTestEnabled", "仅用于测试显示效果，日常保持关闭。" },
        { "PowerResumeRestartEnabled", "系统唤醒后自动重启 SeelenUI 和本程序。" },
        { "SeelenDockForegroundPulseEnabled", "需要时短暂拉前 Seelen Dock，避免被系统窗口压住。" },
        { "FallbackDisconnectedDisplaysEnabled", "指定显示器未连接时，将对应模块回退到当前主显示器。" },
        { "MainDisplayDeviceName", "留空使用当前主显示器；选择 DISPLAY 后按该显示器工作区定位。" },
        { "CodexRadarDisplayDeviceName", "留空使用当前主显示器；未连接且不回退时保留原显示器工作区。" },
        { "PowerThermalDisplayDeviceName", "留空使用当前主显示器；适合把功耗模块固定到副屏。" },
        { "NetworkMonitorDisplayDeviceName", "留空使用当前主显示器；适合把网络监控固定到副屏。" },
        { "ConnectionCheckDisplayDeviceName", "留空使用当前主显示器；适合把连接检测固定到副屏。" },
        { "OperationDisplayDeviceName", "留空使用当前主显示器；操作面板偏移量按目标显示器左下角计算。" },
        { "ResolutionCompatibilityModeEnabled", "默认关闭。开启后按 2880x1800 参考布局在运行时投影所有浮窗，用于在当前设备预览其他分辨率占比。" },
        { "ResolutionCompatibilityScalePercent", "运行时输出比例，低于 100% 压缩，高于 100% 放大；不会改写保存的真实布局坐标。" },
        { GlobalLayoutEditCommandName, "打开全屏布局编辑遮罩，拖拽模块位置；Enter 保存，Esc 放弃。" },
        { "Width", "逻辑像素，主窗口宽度。" },
        { "Height", "逻辑像素，主窗口高度。" },
        { "LeftX", "逻辑像素，距目标显示器左边缘。" },
        { "BottomY", "逻辑像素，距目标显示器下边缘。" },
        { "CodexRadarWidth", "逻辑像素，Codex Radar 宽度。" },
        { "CodexRadarHeight", "逻辑像素，Codex Radar 高度。" },
        { "CodexRadarLeftX", "逻辑像素，距目标显示器左边缘。" },
        { "CodexRadarBottomY", "逻辑像素，距目标显示器下边缘。" },
        { "ClaudeRadarWidth", "逻辑像素，Claude Radar 宽度。" },
        { "ClaudeRadarHeight", "逻辑像素，Claude Radar 高度。" },
        { "ClaudeRadarLeftX", "逻辑像素，距目标显示器左边缘。" },
        { "ClaudeRadarBottomY", "逻辑像素，距目标显示器下边缘。" },
        { "PowerThermalWidth", "逻辑像素，功耗温度窗口宽度。" },
        { "PowerThermalHeight", "逻辑像素，功耗温度窗口高度。" },
        { "PowerThermalLeftX", "逻辑像素，距目标显示器左边缘。" },
        { "PowerThermalBottomY", "逻辑像素，距目标显示器下边缘。" },
        { "NetworkMonitorWidth", "逻辑像素，网络监控窗口宽度。" },
        { "NetworkMonitorHeight", "逻辑像素，网络监控窗口高度。" },
        { "NetworkMonitorLeftX", "逻辑像素，距目标显示器左边缘。" },
        { "NetworkMonitorBottomY", "逻辑像素，距目标显示器下边缘。" },
        { "ConnectionCheckWidth", "逻辑像素，连接检测窗口宽度。" },
        { "ConnectionCheckHeight", "逻辑像素，连接检测窗口高度。" },
        { "ConnectionCheckLeftX", "逻辑像素，距目标显示器左边缘。" },
        { "ConnectionCheckBottomY", "逻辑像素，距目标显示器下边缘。" },
        { "OperationLeftOffset", "逻辑像素，距目标显示器左边缘。" },
        { "OperationBottomOffset", "逻辑像素，距目标显示器下边缘。" },
        { "BackgroundTransparencyPercent", "只影响主窗口背景底色，数值越高越透明。" },
        { "ApplicationTransparencyPercent", "影响主窗口整体透明度，数值越高越透明。" },
        { "CodexRadarTransparencyPercent", "影响 Codex Radar 背景透明度。" },
        { "ClaudeRadarTransparencyPercent", "影响 Claude Radar 背景透明度。" },
        { "PowerThermalTransparencyPercent", "影响功耗温度窗口背景透明度。" },
        { "NetworkMonitorTransparencyPercent", "影响网络监控窗口背景透明度。" },
        { "ConnectionCheckTransparencyPercent", "影响连接检测窗口背景透明度。" },
        { "ConnectionCheckBorderTransparencyPercent", "影响连接检测三框边框透明度。" },
        { "OperationBackgroundTransparencyPercent", "影响左下角操作面板背景透明度。" },
        { "ShowCpu", "在主窗口显示 CPU 指标。" },
        { "ShowMemory", "在主窗口显示内存指标。" },
        { "ShowDisk", "在主窗口显示磁盘指标。" },
        { "ShowNetwork", "在主窗口显示网络指标。" },
        { "ShowGpu", "在主窗口显示 GPU 指标。" },
        { "ShowNpu", "在主窗口显示 NPU 指标。" },
        { "OperationPrimaryPanelMode", "自动模式会按 SeelenUI 运行态在 Windows 按钮和内存饼图之间切换；隐藏会让小按钮移到最左侧。" },
        { "OperationButtonSize", "左下角操作面板按钮的逻辑像素大小。" },
        { "OperationWindowsButtonEnabled", "SeelenUI 未运行时会自动隐藏；关闭后始终不显示左侧 Windows 按钮。" },
        { "OperationMemoryPieEnabled", "左侧 Windows 按钮隐藏时显示物理、虚拟和前台程序内存占用饼图。" },
        { "WinDRecoveryPulseEnabled", "按 Win+D 后延迟拉前本程序和 SeelenUI。" }
    };

    // ═════════════════════════════════════════════════════════════════════
    // Nested Classes — Custom Controls
    // ═════════════════════════════════════════════════════════════════════

    // ── PercentSliderControl ─────────────────────────────────────────────
    private sealed class PercentSliderControl : Panel
    {
        private readonly TrackBar trackBar;
        private readonly Label valueLabel;
        private bool suppressChanged;

        public event EventHandler ValueChanged;

        public PercentSliderControl(Font labelFont)
        {
            this.BackColor = Color.Transparent;
            this.trackBar = new TrackBar();
            this.trackBar.AutoSize = false;
            this.trackBar.TickStyle = TickStyle.None;
            this.trackBar.SmallChange = 1;
            this.trackBar.LargeChange = 5;
            this.trackBar.Height = 34;
            this.trackBar.Minimum = 0;
            this.trackBar.Maximum = 100;
            this.trackBar.ValueChanged += OnTrackBarValueChanged;

            this.valueLabel = new Label();
            this.valueLabel.AutoSize = false;
            this.valueLabel.TextAlign = ContentAlignment.MiddleRight;
            this.valueLabel.Font = labelFont;
            this.valueLabel.ForeColor = TextSecondary;
            this.valueLabel.BackColor = Color.Transparent;

            this.Controls.Add(this.trackBar);
            this.Controls.Add(this.valueLabel);
            this.Height = 54;
            UpdateValueLabel();
        }

        public int Minimum
        {
            get { return this.trackBar.Minimum; }
            set
            {
                this.trackBar.Minimum = value;
                if (this.trackBar.Value < value)
                {
                    this.trackBar.Value = value;
                }
            }
        }

        public int Maximum
        {
            get { return this.trackBar.Maximum; }
            set
            {
                this.trackBar.Maximum = value;
                if (this.trackBar.Value > value)
                {
                    this.trackBar.Value = value;
                }
            }
        }

        public int Value
        {
            get { return this.trackBar.Value; }
        }

        public void SetValueSilent(int value)
        {
            int next = Math.Max(this.trackBar.Minimum, Math.Min(this.trackBar.Maximum, value));
            this.suppressChanged = true;
            try
            {
                this.trackBar.Value = next;
                UpdateValueLabel();
            }
            finally
            {
                this.suppressChanged = false;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int labelWidth = 70;
            int gap = 8;
            int trackWidth = Math.Max(80, this.Width - labelWidth - gap);
            int top = Math.Max(0, (this.Height - this.trackBar.Height) / 2);
            this.trackBar.SetBounds(0, top, trackWidth, this.trackBar.Height);
            this.valueLabel.SetBounds(trackWidth + gap, 0, labelWidth, this.Height);
        }

        private void OnTrackBarValueChanged(object sender, EventArgs e)
        {
            UpdateValueLabel();
            if (!this.suppressChanged && this.ValueChanged != null)
            {
                this.ValueChanged(this, EventArgs.Empty);
            }
        }

        private void UpdateValueLabel()
        {
            this.valueLabel.Text = this.trackBar.Value.ToString(CultureInfo.InvariantCulture) + "%";
        }
    }

    // ── ToggleSwitch ─────────────────────────────────────────────────────
    // Authentic Win11-style toggle: pill track, animated sliding knob.
    private sealed class ToggleSwitch : Control
    {
        private bool isChecked;
        private float animProgress;
        private readonly Timer animTimer;
        private bool hover;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return this.isChecked; }
            set
            {
                if (this.isChecked == value) return;
                this.isChecked = value;
                StartAnimation();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void SetCheckedSilent(bool value)
        {
            this.isChecked = value;
            this.animProgress = value ? 1.0f : 0.0f;
            this.Invalidate();
        }

        public ToggleSwitch()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(70, 35);
            this.BackColor = Color.Transparent;
            this.Cursor = Cursors.Hand;
            this.animTimer = new Timer();
            this.animTimer.Interval = 16;
            this.animTimer.Tick += OnAnimTick;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int radius = this.Height / 2;
            Rectangle trackRect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (GraphicsPath path = CreateRoundRectangle(trackRect, radius))
            {
                if (this.isChecked)
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(trackRect, AccentClr, AccentHover, 0f))
                    {
                        g.FillPath(brush, path);
                    }
                }
                else
                {
                    Color trackColor = this.hover ? DesignTokens.SettingsWarmTheme.ToggleTrackHover : DesignTokens.SettingsWarmTheme.ToggleTrackOff;
                    using (SolidBrush brush = new SolidBrush(trackColor))
                    {
                        g.FillPath(brush, path);
                    }
                    using (Pen pen = new Pen(ControlBorder, 1.2f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            // Knob
            float knobDiameter = this.Height - 8;
            float knobMinX = 4;
            float knobMaxX = this.Width - knobDiameter - 4;
            float knobX = knobMinX + (knobMaxX - knobMinX) * this.animProgress;
            float knobY = 4;

            // Subtle knob shadow
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
            {
                g.FillEllipse(shadow, knobX + 0.5f, knobY + 0.8f, knobDiameter, knobDiameter);
            }

            using (SolidBrush knobBrush = new SolidBrush(DesignTokens.SettingsWarmTheme.ToggleKnob))
            {
                g.FillEllipse(knobBrush, knobX, knobY, knobDiameter, knobDiameter);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            this.Checked = !this.isChecked;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            this.hover = true;
            this.Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            this.hover = false;
            this.Invalidate();
            base.OnMouseLeave(e);
        }

        private void StartAnimation()
        {
            this.animTimer.Start();
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            float target = this.isChecked ? 1.0f : 0.0f;
            float step = 0.18f;
            if (Math.Abs(this.animProgress - target) < step)
            {
                this.animProgress = target;
                this.animTimer.Stop();
            }
            else
            {
                this.animProgress += this.isChecked ? step : -step;
            }
            this.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && this.animTimer != null)
            {
                this.animTimer.Stop();
                this.animTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ── NavigationItem ────────────────────────────────────────────────────
    // Win11-style nav item with icon glyph, text, indicator bar, and hover.
    private sealed class NavigationItem : Panel
    {
        private readonly string icon;
        private readonly string text;
        private bool selected;
        private bool hover;

        public NavigationItem(string icon, string text)
        {
            this.icon = icon;
            this.text = text;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Cursor = Cursors.Hand;
            this.BackColor = Color.Transparent;
        }

        public bool Selected
        {
            get { return this.selected; }
            set { this.selected = value; this.Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Background
            Color bg = this.selected ? DesignTokens.SettingsWarmTheme.NavSelectedBg :
                       this.hover ? DesignTokens.SettingsWarmTheme.NavHoverBg : Color.Transparent;

            if (bg.A > 0)
            {
                using (GraphicsPath path = CreateRoundRectangle(new Rectangle(2, 0, this.Width - 4, this.Height - 1), 5))
                using (SolidBrush brush = new SolidBrush(bg))
                {
                    g.FillPath(brush, path);
                }
            }

            // Indicator bar (accent-colored pill on left edge)
            if (this.selected)
            {
                int barH = 16;
                int barW = 3;
                int barY = (this.Height - barH) / 2;
                using (GraphicsPath bar = CreateRoundRectangle(new Rectangle(3, barY, barW, barH), 1))
                using (SolidBrush brush = new SolidBrush(AccentClr))
                {
                    g.FillPath(brush, bar);
                }
            }

            // Icon (Segoe Fluent Icons / MDL2 Assets)
            Color iconColor = this.selected ? TextPrimary : TextSecondary;
            Font icoFont = GetIconFont();
            using (SolidBrush brush = new SolidBrush(iconColor))
            {
                float iconY = (this.Height - icoFont.Height) / 2f + 1;
                g.DrawString(this.icon, icoFont, brush, 24, iconY);
            }

            // Text
            Color textColor = this.selected ? TextPrimary : TextSecondary;
            using (SolidBrush brush = new SolidBrush(textColor))
            {
                g.DrawString(this.text, this.Font, brush, 70, (this.Height - this.Font.Height) / 2f);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            this.hover = true;
            this.Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            this.hover = false;
            this.Invalidate();
            base.OnMouseLeave(e);
        }
    }

    // ── SettingGroupCard ──────────────────────────────────────────────────
    // Rounded-corner container holding multiple SettingRow children.
    // Draws a single card background; rows share the card surface.
    private sealed class SettingGroupCard : Panel
    {
        private readonly List<SettingRow> rows = new List<SettingRow>();
        private bool layoutInProgress;

        public SettingGroupCard()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer, true);
            this.BackColor = Color.Transparent;
        }

        public void AddRow(SettingRow row)
        {
            this.rows.Add(row);
            this.Controls.Add(row);
        }

        public void LayoutRows()
        {
            if (this.layoutInProgress) return;
            this.layoutInProgress = true;
            try
            {
                int layoutWidth = Math.Max(1, this.ClientSize.Width > 0 ? this.ClientSize.Width : this.Width);
                int y = 0;
                bool first = true;
                for (int i = 0; i < this.rows.Count; i++)
                {
                    SettingRow row = this.rows[i];
                    row.ShowTopDivider = !first;
                    int h = row.ComputeDesiredHeight(layoutWidth);
                    row.SetBounds(0, y, layoutWidth, h);
                    row.RefreshLayoutForWidth(layoutWidth);
                    y += h;
                    first = false;
                }
                this.Height = Math.Max(1, y);
                UpdateClipRegion();
            }
            finally
            {
                this.layoutInProgress = false;
            }
        }

        private void UpdateClipRegion()
        {
            if (this.Width > 1 && this.Height > 1)
            {
                using (GraphicsPath rr = CreateRoundRectangle(new Rectangle(0, 0, this.Width, this.Height), DesignTokens.Radius.SettingsCard))
                {
                    Region old = this.Region;
                    this.Region = new Region(rr);
                    if (old != null) old.Dispose();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
            using (SolidBrush bg = new SolidBrush(CardRest))
            {
                g.FillPath(bg, path);
            }

            // Left edge indicator
            using (GraphicsPath accent = CreateRoundRectangle(new Rectangle(0, 0, 3, this.Height), 1))
            using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 0, 3, this.Height), AccentClr, AccentHover, 90f))
            {
                g.FillPath(brush, accent);
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (!this.layoutInProgress) LayoutRows();
        }
    }

    // ── SettingRow ────────────────────────────────────────────────────────
    // A single setting item within a SettingGroupCard.
    // Left side: title + hint; right side: value control.
    // Draws hover highlight and optional top divider.
    private sealed class SettingRow : Panel
    {
        private const int CompactLayoutWidthThreshold = 928;
        private const int CompactLayoutRemainingTextThreshold = 320;

        private readonly Control valueControl;
        private readonly int preferredValueControlWidth;
        private bool hover;
        public bool ShowTopDivider;

        public SettingRow(Control valueControl, Font titleFont, Font hintFont)
        {
            this.valueControl = valueControl;
            this.preferredValueControlWidth = Math.Max(44, valueControl.Width);
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer, true);
            this.BackColor = Color.Transparent;
            this.Padding = new Padding(
                DesignTokens.Spacing.SettingsCardPaddingX,
                DesignTokens.Spacing.SettingsCardPaddingY,
                DesignTokens.Spacing.SettingsCardPaddingX,
                DesignTokens.Spacing.SettingsCardPaddingY);

            this.TitleLabel = new Label();
            this.TitleLabel.Font = titleFont;
            this.TitleLabel.ForeColor = TextPrimary;
            this.TitleLabel.BackColor = Color.Transparent;
            this.TitleLabel.TextAlign = ContentAlignment.MiddleLeft;
            this.TitleLabel.AutoSize = false;

            this.HintLabel = new Label();
            this.HintLabel.Font = hintFont;
            this.HintLabel.ForeColor = TextTertiary;
            this.HintLabel.BackColor = Color.Transparent;
            this.HintLabel.TextAlign = ContentAlignment.TopLeft;
            this.HintLabel.AutoSize = false;

            this.Controls.Add(this.TitleLabel);
            this.Controls.Add(this.HintLabel);
            this.Controls.Add(valueControl);
        }

        public Label TitleLabel { get; private set; }
        public Label HintLabel { get; private set; }

        // Some value controls (e.g. the Claude model button grid) are inherently wide and wrap
        // to multiple rows on their own; forcing them to share row width with the title/hint text
        // squeezes them into unreadable single-character buttons. This skips the width-threshold
        // heuristic and always stacks the control below the text for that row.
        public bool ForceCompactLayout;

        public int ComputeDesiredHeight(int width)
        {
            int controlWidth = Math.Min(this.preferredValueControlWidth, Math.Max(44, width - this.Padding.Left - this.Padding.Right));
            int controlHeight = GetValueControlHeight(controlWidth);
            bool compact = ShouldUseCompactLayout(width, controlWidth);

            if (compact)
            {
                int textWidth = Math.Max(1, width - this.Padding.Left - this.Padding.Right);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int controlTop = this.Padding.Top + titleHeight + hintHeight + 8;
                return Math.Max(80, controlTop + controlHeight + this.Padding.Bottom);
            }
            else
            {
                int controlLeft = width - this.Padding.Right - controlWidth;
                int textWidth = Math.Max(1, controlLeft - this.Padding.Left - 24);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int textHeight = titleHeight + hintHeight;
                int contentHeight = Math.Max(textHeight, controlHeight);
                return Math.Max(60, this.Padding.Top + contentHeight + this.Padding.Bottom);
            }
        }

        public void RefreshLayout()
        {
            RefreshLayoutForWidth(this.ClientSize.Width);
        }

        public void RefreshLayoutForWidth(int clientWidth)
        {
            LayoutChildren(Math.Max(1, clientWidth));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (this.hover)
            {
                using (SolidBrush hbg = new SolidBrush(CardHover))
                {
                    e.Graphics.FillRectangle(hbg, this.ClientRectangle);
                }
            }

            if (this.ShowTopDivider)
            {
                int x1 = this.Padding.Left;
                int x2 = this.Width - this.Padding.Right;
                using (Pen p = new Pen(DividerColor))
                {
                    e.Graphics.DrawLine(p, x1, 0, x2, 0);
                }
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutChildren(Math.Max(1, this.ClientSize.Width));
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.MouseEnter += delegate { OnChildMouseEnter(); };
            e.Control.MouseLeave += delegate { OnChildMouseLeave(); };
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            this.hover = true;
            this.Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!this.ClientRectangle.Contains(this.PointToClient(Cursor.Position)))
            {
                this.hover = false;
                this.Invalidate();
            }
            base.OnMouseLeave(e);
        }

        private void OnChildMouseEnter()
        {
            if (!this.hover) { this.hover = true; this.Invalidate(); }
        }

        private void OnChildMouseLeave()
        {
            if (!this.ClientRectangle.Contains(this.PointToClient(Cursor.Position)))
            {
                this.hover = false;
                this.Invalidate();
            }
        }

        private void LayoutChildren()
        {
            LayoutChildren(Math.Max(1, this.ClientSize.Width));
        }

        private void LayoutChildren(int clientWidth)
        {
            int left = this.Padding.Left;
            int top = this.Padding.Top;
            int right = clientWidth - this.Padding.Right;
            int controlWidth = Math.Min(this.preferredValueControlWidth, Math.Max(44, clientWidth - this.Padding.Left - this.Padding.Right));
            int controlHeight = GetValueControlHeight(controlWidth);
            bool compact = ShouldUseCompactLayout(clientWidth, controlWidth);

            if (compact)
            {
                int textWidth = Math.Max(1, right - left);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int controlTop = top + titleHeight + hintHeight + 8;
                this.TitleLabel.SetBounds(left, top, textWidth, titleHeight);
                this.HintLabel.SetBounds(left, top + titleHeight, textWidth, hintHeight);
                this.valueControl.SetBounds(left, controlTop, controlWidth, controlHeight);
            }
            else
            {
                int controlLeft = right - controlWidth;
                int textWidth = Math.Max(1, controlLeft - left - 24);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int textHeight = titleHeight + hintHeight;
                int contentHeight = Math.Max(textHeight, controlHeight);
                int controlTop = top + Math.Max(0, (contentHeight - controlHeight) / 2);
                this.valueControl.SetBounds(controlLeft, controlTop, controlWidth, controlHeight);
                this.TitleLabel.SetBounds(left, top, textWidth, titleHeight);
                this.HintLabel.SetBounds(left, top + titleHeight, textWidth, hintHeight);
            }
        }

        private int GetValueControlHeight(int controlWidth)
        {
            VariantPicker picker = this.valueControl as VariantPicker;
            if (picker != null)
            {
                return picker.GetPreferredHeightForWidth(controlWidth);
            }

            if (FindClaudeRadarModelGrid(this.valueControl) != null)
            {
                if (this.valueControl.Width != controlWidth)
                {
                    this.valueControl.Width = controlWidth;
                }

                LayoutClaudeRadarModelPanel(this.valueControl);
                return this.valueControl.Height;
            }

            return this.valueControl.Height;
        }

        private bool ShouldUseCompactLayout(int width, int controlWidth)
        {
            if (this.ForceCompactLayout)
            {
                return true;
            }

            int availableWidth = width - this.Padding.Left - this.Padding.Right;
            return width < CompactLayoutWidthThreshold || availableWidth - controlWidth < CompactLayoutRemainingTextThreshold;
        }
    }

    // ── AdvancedSectionHeader ────────────────────────────────────────────
    // Clickable "复杂选项" divider: everything below it stays collapsed until
    // the user expands it, keeping everyday pages short. Searching bypasses it.
    private sealed class AdvancedSectionHeader : Panel
    {
        private readonly Font titleFont;
        private readonly Font hintFont;
        private bool expanded;
        private bool hover;

        public AdvancedSectionHeader(Font titleFont, Font hintFont)
        {
            this.titleFont = titleFont;
            this.hintFont = hintFont;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.BackColor = MicaBase;
            this.Cursor = Cursors.Hand;
        }

        public int AdvancedRowCount { get; set; }

        public bool Expanded
        {
            get { return this.expanded; }
            set { this.expanded = value; this.Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
            using (SolidBrush bg = new SolidBrush(this.hover ? CardHover : CardRest))
            {
                g.FillPath(bg, path);
            }

            using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
            using (Pen pen = new Pen(StrokeColor))
            {
                g.DrawPath(pen, path);
            }

            // Chevron glyph: E70D = down, E76C = right (Segoe MDL2 / Fluent Icons). Tinted warning
            // yellow rather than the default green accent - this section holds advanced/power-user
            // settings, so it gets the "proceed with caution" color instead of "all good".
            string chevron = this.expanded ? "\uE70D" : "\uE76C";
            Font icoFont = GetIconFont();
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.Warning))
            {
                float iconY = (this.Height - icoFont.Height) / 2f + 1;
                g.DrawString(chevron, icoFont, brush, 20, iconY);
            }

            using (SolidBrush brush = new SolidBrush(TextPrimary))
            {
                g.DrawString("复杂选项", this.titleFont, brush, 58, (this.Height - this.titleFont.Height) / 2f - 8);
            }

            string hint = this.expanded
                ? "点击收起"
                : "不常用的精细调整与测试项" + (this.AdvancedRowCount > 0 ? "（" + this.AdvancedRowCount.ToString(CultureInfo.InvariantCulture) + " 项）" : string.Empty);
            using (SolidBrush brush = new SolidBrush(TextTertiary))
            {
                g.DrawString(hint, this.hintFont, brush, 58, this.Height / 2f + 2);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            this.hover = true;
            this.Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            this.hover = false;
            this.Invalidate();
            base.OnMouseLeave(e);
        }
    }

    // ── VariantPicker ───────────────────────────────────────────────────
    // Renders per-window visual variants as cards. Sample PNGs are loaded lazily by the outer form
    // only for real owner-backed settings windows, so binding self-tests never construct render forms.
    private sealed class VariantPicker : Panel
    {
        private const int CardHeight = 124;
        private const int CardGap = 10;
        private const int ThumbnailHeight = 78;

        private readonly Type enumType;
        private readonly Font labelFont;
        private readonly Font hintFont;
        private readonly List<VariantChoice> choices = new List<VariantChoice>();
        private object selectedValue;
        private int hoverIndex = -1;
        private bool samplesAttempted;

        public event EventHandler ValueChanged;
        public event EventHandler PreferredHeightChanged;

        public VariantPicker(string propertyName, Type enumType, Font labelFont, Font hintFont)
        {
            this.enumType = enumType;
            this.labelFont = labelFont;
            this.hintFont = hintFont;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.BackColor = Color.Transparent;
            this.Cursor = Cursors.Hand;

            Array values = Enum.GetValues(enumType);
            for (int i = 0; i < values.Length; i++)
            {
                object value = values.GetValue(i);
                this.choices.Add(new VariantChoice(value, new EnumOption(value).ToString()));
                if (i == 0)
                {
                    this.selectedValue = value;
                }
            }

            UpdatePreferredHeight();
        }

        public void SetSelectedValue(object value)
        {
            if (value == null || !this.enumType.IsInstanceOfType(value))
            {
                if (this.choices.Count > 0)
                {
                    this.selectedValue = this.choices[0].Value;
                }
            }
            else
            {
                this.selectedValue = value;
            }

            this.Invalidate();
        }

        public object GetSelectedValue()
        {
            return this.selectedValue;
        }

        public int GetPreferredHeightForWidth(int width)
        {
            int columns = GetColumnCount(width);
            int rows = Math.Max(1, (this.choices.Count + columns - 1) / columns);
            return rows * CardHeight + Math.Max(0, rows - 1) * CardGap;
        }

        public bool HasMissingSamples(string directory, string prefix)
        {
            if (this.samplesAttempted)
            {
                return false;
            }

            for (int i = 0; i < this.choices.Count; i++)
            {
                string path = GetSamplePath(directory, prefix, this.choices[i].Value);
                if (!System.IO.File.Exists(path))
                {
                    return true;
                }
            }

            return false;
        }

        public void LoadSamples(string directory, string prefix)
        {
            for (int i = 0; i < this.choices.Count; i++)
            {
                VariantChoice choice = this.choices[i];
                string path = GetSamplePath(directory, prefix, choice.Value);
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using (Image original = Image.FromFile(path))
                    {
                        Image old = choice.Image;
                        choice.Image = new Bitmap(original);
                        if (old != null)
                        {
                            old.Dispose();
                        }
                    }
                }
                catch
                {
                    Image old = choice.Image;
                    choice.Image = null;
                    if (old != null)
                    {
                        old.Dispose();
                    }
                }
            }

            this.samplesAttempted = true;
            this.Invalidate();
        }

        public void MarkSamplesUnavailable()
        {
            this.samplesAttempted = true;
            this.Invalidate();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdatePreferredHeight();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int nextHover = GetChoiceIndexAt(e.Location);
            if (nextHover != this.hoverIndex)
            {
                this.hoverIndex = nextHover;
                this.Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            this.hoverIndex = -1;
            this.Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int index = GetChoiceIndexAt(e.Location);
            if (index < 0 || index >= this.choices.Count)
            {
                return;
            }

            object nextValue = this.choices[index].Value;
            if (object.Equals(this.selectedValue, nextValue))
            {
                return;
            }

            this.selectedValue = nextValue;
            this.Invalidate();
            if (this.ValueChanged != null)
            {
                this.ValueChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            for (int i = 0; i < this.choices.Count; i++)
            {
                Rectangle rect = GetCardBounds(i);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                VariantChoice choice = this.choices[i];
                bool selected = object.Equals(choice.Value, this.selectedValue);
                bool hover = i == this.hoverIndex;

                using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
                using (SolidBrush bg = new SolidBrush(hover ? CardHover : ControlBg))
                {
                    g.FillPath(bg, path);
                }

                using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
                using (Pen pen = new Pen(selected ? AccentClr : ControlBorder, selected ? 2.0f : 1.0f))
                {
                    g.DrawPath(pen, path);
                }

                Rectangle thumb = new Rectangle(rect.Left + 8, rect.Top + 8, rect.Width - 16, ThumbnailHeight);
                DrawChoiceThumbnail(g, choice, thumb);

                Rectangle labelRect = new Rectangle(rect.Left + 10, thumb.Bottom + 7, rect.Width - 20, 24);
                using (StringFormat format = new StringFormat())
                using (SolidBrush textBrush = new SolidBrush(selected ? TextPrimary : TextSecondary))
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(choice.Label, this.labelFont, textBrush, labelRect, format);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (int i = 0; i < this.choices.Count; i++)
                {
                    if (this.choices[i].Image != null)
                    {
                        this.choices[i].Image.Dispose();
                        this.choices[i].Image = null;
                    }
                }
            }

            base.Dispose(disposing);
        }

        private void DrawChoiceThumbnail(Graphics g, VariantChoice choice, Rectangle thumb)
        {
            using (GraphicsPath thumbPath = CreateRoundRectangle(thumb, DesignTokens.Radius.SettingsCard))
            using (SolidBrush thumbBg = new SolidBrush(MicaBase))
            {
                g.FillPath(thumbBg, thumbPath);
            }

            if (choice.Image == null)
            {
                using (StringFormat format = new StringFormat())
                using (SolidBrush brush = new SolidBrush(TextTertiary))
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString("纯文字模式", this.hintFont, brush, thumb, format);
                }
                return;
            }

            Rectangle target = FitImage(choice.Image.Size, thumb);
            GraphicsState state = g.Save();
            using (GraphicsPath clipPath = CreateRoundRectangle(thumb, DesignTokens.Radius.SettingsCard))
            {
                g.SetClip(clipPath);
                g.DrawImage(choice.Image, target);
            }
            g.Restore(state);
        }

        private void UpdatePreferredHeight()
        {
            int oldHeight = this.Height;
            int nextHeight = GetPreferredHeightForWidth(this.Width);
            if (nextHeight != oldHeight)
            {
                this.Height = nextHeight;
                if (this.PreferredHeightChanged != null)
                {
                    this.PreferredHeightChanged(this, EventArgs.Empty);
                }
            }
        }

        private int GetColumnCount()
        {
            return GetColumnCount(this.Width);
        }

        private static int GetColumnCount(int width)
        {
            width = Math.Max(1, width);
            if (width >= 520)
            {
                return 3;
            }

            if (width >= 340)
            {
                return 2;
            }

            return 1;
        }

        private Rectangle GetCardBounds(int index)
        {
            int columns = GetColumnCount();
            int col = index % columns;
            int row = index / columns;
            int cardWidth = Math.Max(86, (Math.Max(1, this.Width) - (columns - 1) * CardGap) / columns);
            return new Rectangle(col * (cardWidth + CardGap), row * (CardHeight + CardGap), cardWidth, CardHeight);
        }

        private int GetChoiceIndexAt(Point point)
        {
            for (int i = 0; i < this.choices.Count; i++)
            {
                if (GetCardBounds(i).Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string GetSamplePath(string directory, string prefix, object value)
        {
            string variant = Convert.ToString(value, CultureInfo.InvariantCulture);
            return System.IO.Path.Combine(directory, prefix + "-" + variant.ToLowerInvariant() + ".png");
        }

        private static Rectangle FitImage(Size imageSize, Rectangle bounds)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return bounds;
            }

            float sx = bounds.Width / (float)imageSize.Width;
            float sy = bounds.Height / (float)imageSize.Height;
            float scale = Math.Min(sx, sy);
            int width = Math.Max(1, (int)Math.Round(imageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Round(imageSize.Height * scale));
            return new Rectangle(
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2,
                width,
                height);
        }

        private sealed class VariantChoice
        {
            public VariantChoice(object value, string label)
            {
                this.Value = value;
                this.Label = label ?? string.Empty;
            }

            public object Value;
            public string Label;
            public Image Image;
        }
    }

    // ── FluentScrollPanel ────────────────────────────────────────────────
    private sealed class FluentScrollPanel : Panel
    {
        public FluentScrollPanel()
        {
            this.AutoScroll = true;
        }

        public bool ScrollByMouseWheelDelta(int delta)
        {
            int old = -this.AutoScrollPosition.Y;
            int next = old - delta;
            if (next < 0)
            {
                next = 0;
            }

            this.AutoScrollPosition = new Point(0, next);
            return old != -this.AutoScrollPosition.Y;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Nested Data Classes
    // ═════════════════════════════════════════════════════════════════════

    private sealed class CategoryPage
    {
        public string Title;
        public string Description;
        public AdvancedSectionHeader AdvancedHeader;
        public bool AdvancedExpanded;
        public NavigationItem NavItem;
        public FluentScrollPanel ScrollPanel;
        public FlowLayoutPanel Stack;
        public Panel Heading;
        public readonly List<SettingEditor> Editors = new List<SettingEditor>();
        public readonly List<SettingGroupData> Groups = new List<SettingGroupData>();
    }

    private sealed class SettingGroupData
    {
        public string Title;
        public bool Advanced;
        public Label TitleLabel;
        public SettingGroupCard Card;
        public readonly List<SettingEditor> Editors = new List<SettingEditor>();
    }

    private sealed class SettingEditor
    {
        public SettingEditor(PropertyInfo property, SettingRow card, Control control)
        {
            this.Name = property == null ? string.Empty : property.Name;
            this.Property = property;
            this.Card = card;
            this.Control = control;
        }

        public SettingEditor(string name, SettingRow card, Control control)
        {
            this.Name = name ?? string.Empty;
            this.Property = null;
            this.Card = card;
            this.Control = control;
        }

        public string Name { get; private set; }
        public PropertyInfo Property { get; private set; }
        public SettingRow Card { get; private set; }
        public Control Control { get; private set; }
    }

    private sealed class ModelOption
    {
        public ModelOption(string key, string label, bool available, bool missingFromCatalog)
        {
            this.Key = CodexRadarModelCatalog.NormalizeModelKey(key);
            this.Label = label ?? string.Empty;
            this.Available = available;
            this.MissingFromCatalog = missingFromCatalog;
        }

        public string Key { get; private set; }
        private string Label { get; set; }
        private bool Available { get; set; }
        private bool MissingFromCatalog { get; set; }

        public override string ToString()
        {
            if (this.MissingFromCatalog)
            {
                return this.Label + "（未在目录）";
            }

            return this.Available ? this.Label : this.Label + "（暂不可用）";
        }
    }

    private sealed class ClaudeModelOption
    {
        public ClaudeModelOption(string key, string label, bool available, bool pending)
        {
            this.Key = WidgetSettings.NormalizeClaudeRadarModelKey(key);
            this.Label = label ?? string.Empty;
            this.Available = available;
            this.Pending = pending;
        }

        public string Key { get; private set; }
        public string Label { get; private set; }
        public bool Available { get; private set; }
        public bool Pending { get; private set; }

        public override string ToString()
        {
            if (this.Key.Length == 0)
            {
                return this.Label;
            }

            if (this.Pending)
            {
                return this.Label + "（待映射）";
            }

            return this.Available ? this.Label : this.Label + "（暂不可用）";
        }
    }

    private sealed class DisplayOption
    {
        public DisplayOption(string value, string label)
        {
            this.Value = WidgetSettings.NormalizeDisplayDeviceName(value);
            this.Label = label ?? string.Empty;
        }

        public string Value { get; private set; }
        private string Label { get; set; }

        public override string ToString()
        {
            return this.Label;
        }
    }

    private sealed class EnumOption
    {
        public EnumOption(object value)
        {
            this.Value = value;
        }

        public object Value { get; private set; }

        public override string ToString()
        {
            if (this.Value is OperationPrimaryPanelMode)
            {
                OperationPrimaryPanelMode mode = (OperationPrimaryPanelMode)this.Value;
                if (mode == OperationPrimaryPanelMode.Auto)
                {
                    return "自动";
                }

                if (mode == OperationPrimaryPanelMode.WindowsButton)
                {
                    return "Windows 按钮";
                }

                if (mode == OperationPrimaryPanelMode.MemoryPie)
                {
                    return "内存饼图";
                }

                if (mode == OperationPrimaryPanelMode.Hidden)
                {
                    return "隐藏";
                }
            }

            if (this.Value is CodexRadarSoftwareMode)
            {
                CodexRadarSoftwareMode mode = (CodexRadarSoftwareMode)this.Value;
                if (mode == CodexRadarSoftwareMode.Auto)
                {
                    return "自动（按前台）";
                }

                if (mode == CodexRadarSoftwareMode.Codex)
                {
                    return "固定 CODEX";
                }

                if (mode == CodexRadarSoftwareMode.Claude)
                {
                    return "固定 CLAUDE";
                }
            }

            if (this.Value is WidgetVisibilityMode)
            {
                WidgetVisibilityMode mode = (WidgetVisibilityMode)this.Value;
                if (mode == WidgetVisibilityMode.AlwaysVisible)
                {
                    return "总是可见";
                }

                if (mode == WidgetVisibilityMode.HideWhenFullscreen)
                {
                    return "全屏时不可见";
                }

                if (mode == WidgetVisibilityMode.HideWhenMaximized)
                {
                    return "最大化时不可见";
                }

                if (mode == WidgetVisibilityMode.HideWhenOverlapped)
                {
                    return "遮挡时不可见";
                }

                if (mode == WidgetVisibilityMode.DesktopOnly)
                {
                    return "仅桌面可见";
                }
            }

            if (this.Value is RadarClockTimeDisplayMode)
            {
                RadarClockTimeDisplayMode mode = (RadarClockTimeDisplayMode)this.Value;
                if (mode == RadarClockTimeDisplayMode.Utc)
                {
                    return "UTC";
                }

                if (mode == RadarClockTimeDisplayMode.CurrentLocal)
                {
                    return "当前时间";
                }

                if (mode == RadarClockTimeDisplayMode.LastAttemptRefresh)
                {
                    return "上次尝试刷新";
                }

                if (mode == RadarClockTimeDisplayMode.LastActualRefresh)
                {
                    return "上次实际刷新";
                }
            }

            if (this.Value is CodexQuotaPlanComparison)
            {
                CodexQuotaPlanComparison comparison = (CodexQuotaPlanComparison)this.Value;
                if (comparison == CodexQuotaPlanComparison.GreaterThan)
                {
                    return "大于";
                }

                return "小于";
            }

            if (this.Value is CodexQuotaPlanResumeConditionMode)
            {
                CodexQuotaPlanResumeConditionMode mode = (CodexQuotaPlanResumeConditionMode)this.Value;
                if (mode == CodexQuotaPlanResumeConditionMode.WeeklyOnly)
                {
                    return "仅周额度";
                }

                if (mode == CodexQuotaPlanResumeConditionMode.FiveHourOnly)
                {
                    return "仅 5 小时额度";
                }

                return "周额度与 5 小时额度";
            }

            // All six per-window render-variant enums share member names for the common variants
            // (Classic plus the four OLED-safe restyle schemes); CodexRadarRenderVariant additionally
            // carries EvenGrid/EvenRow. Label by member name once instead of duplicating per enum type.
            if (this.Value is NetworkMonitorRenderVariant)
            {
                string networkVariantName = Convert.ToString(this.Value, CultureInfo.InvariantCulture);
                if (networkVariantName == "Classic") return "扁平信息条";
                if (networkVariantName == "GroupedCards") return "分组卡片";
                if (networkVariantName == "Typographic") return "排版流（OLED 安全）";
                if (networkVariantName == "AmberHud") return "暗琥珀仪表（OLED 安全）";
                if (networkVariantName == "WarmCard") return "暖灰暗卡片（OLED 安全）";
                if (networkVariantName == "Phosphor") return "磷光绿终端（OLED 安全）";
            }

            if (this.Value is CodexRadarRenderVariant ||
                this.Value is MainWidgetRenderVariant ||
                this.Value is PowerThermalRenderVariant ||
                this.Value is ConnectionCheckRenderVariant ||
                this.Value is OperationRenderVariant)
            {
                string variantName = Convert.ToString(this.Value, CultureInfo.InvariantCulture);
                if (variantName == "Classic") return "经典布局";
                if (variantName == "EvenGrid") return "均布六格";
                if (variantName == "EvenRow") return "均布单行";
                if (variantName == "Typographic") return "排版流（OLED 安全）";
                if (variantName == "AmberHud") return "暗琥珀仪表（OLED 安全）";
                if (variantName == "WarmCard") return "暖灰暗卡片（OLED 安全）";
                if (variantName == "Phosphor") return "磷光绿终端（OLED 安全）";
                if (variantName == "RadialDial") return "扇形速控盘（新）";
            }

            return Convert.ToString(this.Value, CultureInfo.InvariantCulture);
        }
    }

    private struct NumericRange
    {
        public NumericRange(decimal minimum, decimal maximum)
        {
            this.Minimum = minimum;
            this.Maximum = maximum;
        }

        public decimal Minimum;
        public decimal Maximum;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Static Helpers & P/Invoke
    // ═════════════════════════════════════════════════════════════════════

    private static GraphicsPath CreateRoundRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
