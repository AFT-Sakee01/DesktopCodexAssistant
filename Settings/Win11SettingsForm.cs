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
    private const string GlobalLayoutEditCommandName = "GlobalLayoutEditCommand";
    private const string ClaudeSetupTokenCommandName = "ClaudeSetupTokenCommand";
    private const string DeepSeekApiKeyCommandName = "DeepSeekApiKeyCommand";

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
            new string[] { "窗口行为", "VisibilityMode", "CodexPetZOrderProtectionEnabled", "VisibilityOverlapIgnoresOperationPanelEnabled" },
            new string[] { "全局快捷键", "HotkeyToggleAllWindows", "HotkeyToggleHoverOpacity", "HotkeyOpenSettings" },
            new string[] { "AI 请求阻断", "AiRequestProtectionAutoEnabled", "AiRequestProtectionManualBlockEnabled", "AiChinaEgressGuardEnabled" },
            new string[] { "!Codex 额度计划", "CodexQuotaPlanEnabled", "CodexQuotaPlanWeeklyComparison", "CodexQuotaPlanWeeklyThresholdPercent",
                           "CodexQuotaPlanFiveHourComparison", "CodexQuotaPlanFiveHourThresholdPercent", "CodexQuotaPlanResumeConditionMode",
                           "CodexQuotaPlanAutoResumePausedGoals", "CodexQuotaPlanPauseGoalIds", "CodexQuotaPlanResumeGoalIds" },
            new string[] { "!恢复与保护", "SeelenDockForegroundPulseEnabled", "WinDRecoveryPulseEnabled", "PowerResumeRestartEnabled" },
            new string[] { "!调试", "ForceShowForegroundFpsEnabled" }
        });

        AddPageGrouped("\uE7C2", "布局与位置", "可见停靠列、看板与操作面板的位置、缩放和显示器。", new string[][]
        {
            new string[] { "可视化编辑", GlobalLayoutEditCommandName },
            new string[] { "左侧面板列", "LeftDockAutoArrangeEnabled", "LeftDockButtonOrder", "LeftDockButtonGapPixels", "LeftDockGroupOffsetY" },
            new string[] { "右侧窗口列", "RightTileAutoArrangeEnabled", "RightTileButtonOrder", "RightTileButtonGapPixels", "RightTileGroupOffsetY" },
            new string[] { "分辨率兼容", "ResolutionCompatibilityModeEnabled", "ResolutionCompatibilityScalePercent" },
            new string[] { "可见面缩放", "NetworkMonitorScaleOverridePercent", "OperationScaleOverridePercent", "SpecBoardScaleOverridePercent", "CodexTaskBoardScaleOverridePercent" },
            new string[] { "显示器分配", "FallbackDisconnectedDisplaysEnabled", "MainDisplayDeviceName", "OperationDisplayDeviceName" },
            new string[] { "!Spec Board 尺寸", "SpecBoardWidth", "SpecBoardHeight" },
            new string[] { "!操作面板位置", "OperationLeftOffset", "OperationBottomOffset" }
        });

        AddPageGrouped("\uE7B3", "显示与隐藏", "配置鼠标靠近、空闲和窗口状态触发的隐藏行为。", new string[][]
        {
            new string[] { "夜间时段", "NightScheduleEnabled", "NightScheduleStartMinutes", "NightScheduleEndMinutes", "NightDimLuminancePercent", "NightQuietHoursEnabled" },
            new string[] { "提醒分类", "AlertQuotaEnabled", "AlertResetProtectionEnabled", "AlertServiceHealthEnabled", "AlertCodexTaskEnabled" },
            new string[] { "防烧屏", "BurnInProtectionEnabled", "BurnInLevelOneIdleSeconds", "BurnInLevelTwoDelaySeconds" },
            new string[] { "鼠标靠近时隐藏", "HoverOpacityEnabled", "SensitiveMouseModeEnabled", "SensitiveMouseRangePixels" },
            new string[] { "自动隐藏", "AutoHoverOpacityIdleEnabled", "AutoHoverOpacityIdleSeconds", "AutoHoverOpacityMaximizedEnabled", "OperationRadialCoreAutoHideKeepAliveEnabled", "OperationRadialIdleCollapseSeconds", "OperationRadialIdleResetOnInteractionEnabled", "OperationRadialKeepOpenAfterLeafClickEnabled" },
            new string[] { "!延迟显现", "HoverOpacityRevealDelayEnabled", "HoverOpacityRevealDelaySeconds", "HoverOpacityRevealResetSeconds" },
            new string[] { "!覆盖与反向", "HoverOpacityCoverEnabled", "ReverseHoverOpacityRevealEnabled", "ReverseHoverOpacityRestoreDelaySeconds" }
        });

        AddPageGrouped("\uE737", "右侧磁贴", "性能与 Radar 磁贴列的尺寸和透明度。", new string[][]
        {
            new string[] { "磁贴与展开面板", "MainWidgetTileLargeModeEnabled", "MetricTileExpandWidth", "MetricTileExpandHeight" },
            new string[] { "交互与彩蛋", "RightTileMouseClickThroughEnabled", "GeniusProgrammerEasterEggEnabled" },
            new string[] { "透明度", "ApplicationTransparencyPercent", "MainWidgetTransparencyOverridePercent" }
        });

        AddPageGrouped("\uE71E", "Codex Radar", "Codex Radar 数据、额度保护与右侧磁贴内容。", new string[][]
        {
            new string[] { "数据模式", "CodexRadarSoftwareMode", "CodexRadarModelKey", "RadarClockAutoSwitchModelEnabled" },
            new string[] { "!CodexRadar.com 读取链路", "CodexRadarPublicJsonEnabled", "CodexRadarHtmlFallbackEnabled", "CodexRadarRssFallbackEnabled", "CodexRadarServiceProbeToken" },
            new string[] { "!额度保护", "CodexQuotaDueResetProtectionEnabled", "CodexQuotaRssResetProtectionEnabled",
                           "CodexQuotaProviderZeroDropProtectionEnabled", "CodexQuotaDuplicateSameBalanceRingProtectionEnabled",
                           "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled", "CodexQuotaProviderWeeklySpikeProtectionEnabled",
                           "CodexQuotaStrictFiveHourResetBoundaryEnabled", "CodexQuotaWeeklyBaselineAutoRepairEnabled" },
            new string[] { "!服务健康测试", "CodexRadarRandomTestEnabled", "CodexRadarRandomTestAutoRefresh", "CodexRadarRandomTestRefreshToken" }
        });

        AddPageGrouped("\uE8D4", "Claude 用量", "Claude 官方额度凭据与右侧 CLD 磁贴数据。", new string[][]
        {
            new string[] { "Claude Code 用量令牌", ClaudeSetupTokenCommandName }
        });

        AddPageGrouped("\uE950", "DeepSeek 用量", "DeepSeek 官方余额与本地消费趋势。", new string[][]
        {
            new string[] { "DeepSeek API Key", DeepSeekApiKeyCommandName }
        });

        AddPageGrouped("\uEBB0", "功耗与温度", "UX3407N / UX3607O 专用功耗温度数据。", new string[][]
        {
            new string[] { "电池与节能", "PowerThermalManualEnergySaverThresholdPercent" },
            new string[] { "!测试", "ThermalTestMode" }
        });

        AddPageGrouped("\uE774", "网络", "网络监控、GFW 检测、云服务和出口身份。", new string[][]
        {
            new string[] { "网络停靠板", "NetworkMonitorAdapterId", "NetworkMonitorTransparencyOverridePercent", "NetworkMonitorLeftDockTabCenterY" },
            new string[] { "GFW 检测", "GfwProbeEnabled", "GfwProbeIntervalMinutes" },
            new string[] { "云服务检测", "CloudEndpointTargets", "CloudStatusRegionMask" },
            new string[] { "固定站点 Ping", "FixedPingTargets" },
            new string[] { "连接检测", "ConnectionCheckIntervalSeconds" },
            new string[] { "!手动刷新", "GfwProbeManualRefreshToken", "ConnectionCheckManualRefreshToken" },
            new string[] { "!云服务测试", "CloudEndpointTestSeed" },
            new string[] { "!测试", "CleanIpBadgeTestMode", "NetworkStatusTestMode" }
        });

        AddPageGrouped("\uE700", "操作面板", "左下角操作面板的按钮、透明度和外观。", new string[][]
        {
            new string[] { "按钮与面板", "OperationButtonSize", "OperationPrimaryPanelMode", "OperationDoubleClickSpecialMenuEnabled", "OperationSettingsLogicExtensionEnabled", "OperationBackgroundTransparencyPercent", "OperationTransparencyOverridePercent" },
            new string[] { "Spec Board", "SpecBoardTransparencyOverridePercent", "SpecBoardLeftDockTabCenterY", "LeftDockOutsideClickCollapseEnabled", "SpecBoardAutoPopupEnabled", "SpecBoardAutoPopupSeconds", "SpecBoardAutoHideSeconds", "SpecBoardLedgerPath", "SpecBoardManagerWidth", "SpecBoardManagerHeight", "SpecBoardManagerDangerZoneRequiresTypedConfirm" },
            new string[] { "Codex 任务看板", "CodexTaskBoardTransparencyOverridePercent", "CodexTaskBoardLeftDockTabCenterY" },
            new string[] { "GUARD 看板", "GuardBoardTransparencyOverridePercent", "GuardBoardScaleOverridePercent", "GuardBoardLeftDockTabCenterY", "GuardBoardAutoHideSeconds" },
            new string[] { "Codex IQ 看板", "CodexIqBoardTransparencyOverridePercent", "CodexIqBoardScaleOverridePercent", "CodexIqBoardLeftDockTabCenterY", "CodexIqBoardAutoHideSeconds" },
            new string[] { "重置与速蹬看板", "ResetSpeedBoardTransparencyOverridePercent", "ResetSpeedBoardScaleOverridePercent", "ResetSpeedBoardLeftDockTabCenterY", "ResetSpeedBoardAutoHideSeconds" },
            new string[] { "系统日记看板", "SystemDayBoardTransparencyOverridePercent", "SystemDayBoardScaleOverridePercent", "SystemDayBoardLeftDockTabCenterY", "SystemDayBoardAutoHideSeconds" },
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

        if (string.Equals(propertyName, DeepSeekApiKeyCommandName, StringComparison.Ordinal))
        {
            return BuildDeepSeekApiKeyEditor();
        }

        PropertyInfo property = typeof(WidgetSettings).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property == null || !property.CanRead || !property.CanWrite ||
            (property.PropertyType == typeof(string[]) &&
                !IsNetworkProbeTargetSetting(property.Name) &&
                !IsColumnOrderSetting(property.Name)))
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
        // Column-order controls wrap on their own and need the full row width.
        if (IsColumnOrderSetting(propertyName))
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

    private SettingEditor BuildDeepSeekApiKeyEditor()
    {
        Button button = BuildCommandButton(GetDeepSeekApiKeyButtonText(), false, GetDeepSeekApiKeyAccentColor());
        button.Width = 227;
        button.Height = 54;
        button.Click += delegate { OpenDeepSeekApiKeyDialog(button); };

        SettingRow card = new SettingRow(button, GetUiFont(10.0f), GetUiFont(8.5f));
        card.Width = 1152;
        card.Margin = new Padding(0);
        card.TitleLabel.Text = GetSettingTitle(DeepSeekApiKeyCommandName);
        card.HintLabel.Text = GetSettingHint(DeepSeekApiKeyCommandName);
        card.BackColor = Color.Transparent;
        return new SettingEditor(DeepSeekApiKeyCommandName, card, button);
    }

    private void OpenDeepSeekApiKeyDialog(Button sourceButton)
    {
        Form dialog = new Form();
        try
        {
            dialog.Text = "DeepSeek API Key";
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.ShowInTaskbar = false;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.AutoScaleMode = AutoScaleMode.None;
            dialog.BackColor = MicaBase;
            dialog.ForeColor = TextSecondary;
            dialog.Font = GetUiFont(9.5f);

            const int marginLeft = 24;
            const int contentWidth = 572;
            int y = 20;

            Font titleFont = GetUiFont(12.0f, FontStyle.Bold);
            Label title = new Label();
            title.Text = "连接 DeepSeek 官方余额 API";
            title.Font = titleFont;
            title.ForeColor = TextPrimary;
            title.BackColor = MicaBase;
            title.Location = new Point(marginLeft, y);
            title.Size = new Size(contentWidth, GetSingleLineHeight(titleFont, 8));
            title.TextAlign = ContentAlignment.MiddleLeft;
            y += title.Height + 8;

            Font hintFont = GetUiFont(8.8f);
            string hintText = "Key 仅以当前 Windows 用户的 DPAPI 加密保存在本机，用于读取 /user/balance；" +
                "消费量由本地 48 小时余额变化估算，不会上传历史。环境变量 DEEPSEEK_API_KEY 的优先级更高。";
            Label hint = new Label();
            hint.Text = hintText;
            hint.Font = hintFont;
            hint.ForeColor = TextTertiary;
            hint.BackColor = MicaBase;
            hint.Location = new Point(marginLeft, y);
            hint.Size = new Size(contentWidth, GetWrappedTextHeight(hintText, hintFont, contentWidth, 8));
            hint.TextAlign = ContentAlignment.TopLeft;
            y += hint.Height + 16;

            Font sectionFont = GetUiFont(9.0f, FontStyle.Bold);
            Label keyLabel = new Label();
            keyLabel.Text = "API Key";
            keyLabel.Font = sectionFont;
            keyLabel.ForeColor = TextPrimary;
            keyLabel.BackColor = MicaBase;
            keyLabel.Location = new Point(marginLeft, y);
            keyLabel.Size = new Size(contentWidth, GetSingleLineHeight(sectionFont, 6));
            keyLabel.TextAlign = ContentAlignment.MiddleLeft;
            y += keyLabel.Height + 6;

            TextBox keyBox = new TextBox();
            keyBox.Location = new Point(marginLeft, y);
            keyBox.Size = new Size(contentWidth, 34);
            keyBox.BackColor = ControlBg;
            keyBox.ForeColor = TextSecondary;
            keyBox.BorderStyle = BorderStyle.FixedSingle;
            keyBox.Font = GetUiFont(9.2f);
            keyBox.UseSystemPasswordChar = true;
            SendMessage(keyBox.Handle, EmSetCueBanner, IntPtr.Zero, "sk-...（留空不会覆盖现有 Key）");
            y += keyBox.Height + 10;

            Font statusFont = GetUiFont(8.5f);
            Label status = new Label();
            status.Text = IsDeepSeekApiKeyConfiguredForUi() ? "当前状态：已配置" : "当前状态：未配置";
            status.Font = statusFont;
            status.ForeColor = IsDeepSeekApiKeyConfiguredForUi() ? AccentClr : TextTertiary;
            status.BackColor = MicaBase;
            status.Location = new Point(marginLeft, y);
            status.Size = new Size(contentWidth, GetSingleLineHeight(statusFont, 6));
            status.TextAlign = ContentAlignment.MiddleLeft;
            y += status.Height + 18;

            Button clearButton = BuildCommandButton("清除", false, ErrorClr);
            clearButton.Location = new Point(marginLeft, y);
            clearButton.Width = 112;
            clearButton.Height = 38;
            Button cancelButton = BuildCommandButton("取消", false);
            cancelButton.Location = new Point(marginLeft + contentWidth - 238, y);
            cancelButton.Width = 112;
            cancelButton.Height = 38;
            cancelButton.DialogResult = DialogResult.Cancel;
            Button saveButton = BuildCommandButton("保存并刷新", true);
            saveButton.Location = new Point(marginLeft + contentWidth - 118, y);
            saveButton.Width = 118;
            saveButton.Height = 38;

            clearButton.Click += delegate
            {
                string errorCode;
                if (TrySaveDeepSeekApiKeyFile(string.Empty, out errorCode))
                {
                    DeepSeekBalanceMonitor.RequestRefresh("设置清除");
                    RefreshDeepSeekApiKeyButton(sourceButton);
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                    ShowStatus("DeepSeek API Key 已清除", SettingsStatusSeverity.Warning);
                    return;
                }

                ShowStatus("DeepSeek API Key 清除失败 " + errorCode, SettingsStatusSeverity.Error);
            };
            saveButton.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(keyBox.Text))
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                    return;
                }

                string errorCode;
                if (TrySaveDeepSeekApiKeyFile(keyBox.Text, out errorCode))
                {
                    DeepSeekBalanceMonitor.RequestRefresh("设置更新");
                    RefreshDeepSeekApiKeyButton(sourceButton);
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                    ShowStatus("DeepSeek API Key 已保存", SettingsStatusSeverity.Success);
                    return;
                }

                ShowStatus("DeepSeek API Key 保存失败 " + errorCode, SettingsStatusSeverity.Error);
            };

            dialog.Controls.Add(title);
            dialog.Controls.Add(hint);
            dialog.Controls.Add(keyLabel);
            dialog.Controls.Add(keyBox);
            dialog.Controls.Add(status);
            dialog.Controls.Add(clearButton);
            dialog.Controls.Add(cancelButton);
            dialog.Controls.Add(saveButton);
            dialog.AcceptButton = saveButton;
            dialog.CancelButton = cancelButton;
            dialog.ClientSize = new Size(contentWidth + marginLeft * 2, y + saveButton.Height + 22);
            dialog.ShowDialog(this);
        }
        finally
        {
            dialog.Dispose();
        }
    }

    private static bool IsDeepSeekApiKeyConfiguredForUi()
    {
        try
        {
            return DeepSeekBalanceMonitor.ReadConfiguredApiKey().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDeepSeekApiKeyButtonText()
    {
        return IsDeepSeekApiKeyConfiguredForUi() ? "已配置 · 修改" : "未配置 · 立即设置";
    }

    private static Color GetDeepSeekApiKeyAccentColor()
    {
        return IsDeepSeekApiKeyConfiguredForUi() ? AccentClr : ErrorClr;
    }

    private static void RefreshDeepSeekApiKeyButton(Button sourceButton)
    {
        if (sourceButton != null && !sourceButton.IsDisposed)
        {
            sourceButton.Text = GetDeepSeekApiKeyButtonText();
        }
    }

    private static bool TrySaveDeepSeekApiKeyFile(string apiKey, out string errorCode)
    {
        return TrySaveDeepSeekApiKeyFile(
            apiKey,
            DeepSeekBalanceMonitor.ApiKeyPath,
            DeepSeekBalanceMonitor.LegacyApiKeyPath,
            out errorCode);
    }

    private static bool TrySaveDeepSeekApiKeyFile(
        string apiKey,
        string encryptedPath,
        string legacyTextPath,
        out string errorCode)
    {
        errorCode = string.Empty;
        try
        {
            string trimmed = SecretStore.TrimSecret(apiKey);
            if (trimmed.Length == 0)
            {
                SecretStore.DeleteSecretFiles(encryptedPath, legacyTextPath);
                return true;
            }

            SecretStore.WriteSecret(encryptedPath, trimmed);
            SecretStore.DeleteLegacySecretFiles(legacyTextPath);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            errorCode = "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
            return false;
        }
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
        bool invalid = configured && IsClaudeSetupTokenInvalidForUi();
        statusHint.Text = invalid
            ? "状态：令牌已失效，请重新绑定"
            : (configured ? "状态：已配置" : "状态：未配置（额度环将显示满环红色）");
        statusHint.ForeColor = configured && !invalid ? DesignTokens.Colors.Success : DesignTokens.Colors.Danger;
    }

    private static string GetClaudeSetupTokenButtonText()
    {
        if (IsClaudeSetupTokenConfiguredForUi() && IsClaudeSetupTokenInvalidForUi())
        {
            return "令牌已失效，请重新绑定";
        }

        return IsClaudeSetupTokenConfiguredForUi() ? "已配置 · 修改" : "未配置 · 立即设置";
    }

    private static Color GetClaudeSetupTokenAccentColor()
    {
        return IsClaudeSetupTokenConfiguredForUi() && !IsClaudeSetupTokenInvalidForUi()
            ? DesignTokens.Colors.Success
            : DesignTokens.Colors.Danger;
    }

    private static bool IsClaudeSetupTokenInvalidForUi()
    {
        return string.Equals(
            ClaudeCodeUsageScheduler.LastErrorCode,
            "TOKEN_INVALID",
            StringComparison.OrdinalIgnoreCase);
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
        return TrySaveClaudeSetupTokenFile(
            token,
            ClaudeCodeUsageReader.SetupTokenFilePath,
            ClaudeCodeUsageReader.LegacySetupTokenFilePath,
            out errorCode);
    }

    private static bool TrySaveClaudeSetupTokenFile(
        string token,
        string encryptedPath,
        string legacyTextPath,
        out string errorCode)
    {
        errorCode = string.Empty;
        try
        {
            string trimmed = ClaudeCodeUsageReader.NormalizeSetupToken(token);
            if (trimmed.Length == 0)
            {
                SecretStore.DeleteSecretFiles(encryptedPath, legacyTextPath);
                ClaudeCodeUsageScheduler.ClearLastError();
                return true;
            }

            SecretStore.WriteSecret(encryptedPath, trimmed);
            SecretStore.DeleteLegacySecretFiles(legacyTextPath);
            ClaudeCodeUsageScheduler.ClearLastError();
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
        if (type == typeof(string[]) && IsColumnOrderSetting(property.Name))
        {
            ColumnOrderEditorControl editor = new ColumnOrderEditorControl(
                string.Equals(property.Name, "LeftDockButtonOrder", StringComparison.Ordinal),
                GetUiFont(9.0f, FontStyle.Bold));
            editor.Width = 560;
            editor.ValueChanged += delegate { OnSettingChanged(); };
            return editor;
        }

        if (type == typeof(string[]) && IsNetworkProbeTargetSetting(property.Name))
        {
            bool cloud = string.Equals(property.Name, "CloudEndpointTargets", StringComparison.Ordinal);
            NetworkProbeTargetEditorState state = new NetworkProbeTargetEditorState(cloud, null);
            Button button = BuildCommandButton(state.GetButtonText(), false, AccentClr);
            button.Width = 300;
            button.Height = 54;
            button.Tag = state;
            button.Click += delegate
            {
                using (NetworkProbeTargetEditorForm dialog = new NetworkProbeTargetEditorForm(state.Cloud, state.Values))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        state.SetValues(dialog.GetValues());
                        button.Text = state.GetButtonText();
                        OnSettingChanged();
                    }
                }
            };
            return button;
        }

        if (type == typeof(bool))
        {
            ToggleSwitch toggle = new ToggleSwitch();
            toggle.CheckedChanged += delegate
            {
                if (IsColumnAutoArrangeSetting(property.Name))
                {
                    RefreshColumnArrangementEditorEnabledStates();
                }

                OnSettingChanged();
            };
            return toggle;
        }

        if (type.IsEnum)
        {
            if (property.Name.EndsWith("RenderVariant", StringComparison.Ordinal))
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

        if (type == typeof(string) && IsDisplayDeviceSetting(property.Name))
        {
            return BuildDisplayDeviceCombo();
        }

        if (type == typeof(string) && string.Equals(property.Name, "SpecBoardLedgerPath", StringComparison.Ordinal))
        {
            return BuildSpecBoardLedgerPathPicker();
        }

        if (type == typeof(int) || type == typeof(double))
        {
            if (type == typeof(int) && IsLeftDockTabCenterSetting(property.Name))
            {
                return BuildLeftDockTabCenterEditor();
            }

            if (string.Equals(property.Name, "ResolutionCompatibilityScalePercent", StringComparison.Ordinal))
            {
                PercentSliderControl slider = new PercentSliderControl(GetUiFont(9.0f, FontStyle.Bold));
                slider.Width = 400;
                slider.Height = 54;
                slider.AccessibleLabel = GetSettingTitle(property.Name);
                slider.Minimum = WidgetSettings.MinResolutionCompatibilityScalePercent;
                slider.Maximum = WidgetSettings.MaxResolutionCompatibilityScalePercent;
                slider.ValueChanged += delegate { OnSettingChanged(); };
                return slider;
            }

            if (type == typeof(int) &&
                (IsColumnButtonGapSetting(property.Name) || IsColumnGroupOffsetSetting(property.Name)))
            {
                PercentSliderControl slider = new PercentSliderControl(GetUiFont(9.0f, FontStyle.Bold));
                slider.Width = 480;
                slider.Height = 54;
                slider.AccessibleLabel = GetSettingTitle(property.Name);
                slider.Minimum = IsColumnButtonGapSetting(property.Name)
                    ? WidgetSettings.MinColumnButtonGapPixels
                    : WidgetSettings.MinColumnGroupOffsetY;
                slider.Maximum = IsColumnButtonGapSetting(property.Name)
                    ? WidgetSettings.MaxColumnButtonGapPixels
                    : WidgetSettings.MaxColumnGroupOffsetY;
                slider.Suffix = IsColumnButtonGapSetting(property.Name) ? "%" : " px";
                slider.UseNumericInput = IsColumnButtonGapSetting(property.Name);
                slider.ShowPositiveSign = IsColumnGroupOffsetSetting(property.Name);
                slider.ValueChanged += delegate { OnSettingChanged(); };
                return slider;
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
            box.ValueChanged += delegate
            {
                if (!this.initializing &&
                    IsWindowScaleOverrideSetting(property.Name) &&
                    box.Value >= 0 &&
                    box.Value < WidgetSettings.MinResolutionCompatibilityScalePercent)
                {
                    // NumericUpDown cannot express a discontinuous domain. Invalid custom values
                    // snap immediately to the supported floor so the editor and preview agree.
                    box.Value = WidgetSettings.MinResolutionCompatibilityScalePercent;
                    return;
                }

                OnSettingChanged();
            };
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

    private static bool IsNetworkProbeTargetSetting(string propertyName)
    {
        return string.Equals(propertyName, "CloudEndpointTargets", StringComparison.Ordinal) ||
            string.Equals(propertyName, "FixedPingTargets", StringComparison.Ordinal);
    }

    private static bool IsColumnOrderSetting(string propertyName)
    {
        return string.Equals(propertyName, "LeftDockButtonOrder", StringComparison.Ordinal) ||
            string.Equals(propertyName, "RightTileButtonOrder", StringComparison.Ordinal);
    }

    private static bool IsColumnAutoArrangeSetting(string propertyName)
    {
        return string.Equals(propertyName, "LeftDockAutoArrangeEnabled", StringComparison.Ordinal) ||
            string.Equals(propertyName, "RightTileAutoArrangeEnabled", StringComparison.Ordinal);
    }

    private static bool IsColumnButtonGapSetting(string propertyName)
    {
        return string.Equals(propertyName, "LeftDockButtonGapPixels", StringComparison.Ordinal) ||
            string.Equals(propertyName, "RightTileButtonGapPixels", StringComparison.Ordinal);
    }

    private static bool IsColumnGroupOffsetSetting(string propertyName)
    {
        return string.Equals(propertyName, "LeftDockGroupOffsetY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "RightTileGroupOffsetY", StringComparison.Ordinal);
    }

    private static bool IsWindowScaleOverrideSetting(string propertyName)
    {
        return !string.IsNullOrEmpty(propertyName) &&
            propertyName.EndsWith("ScaleOverridePercent", StringComparison.Ordinal);
    }

    private static bool IsLeftDockTabCenterSetting(string propertyName)
    {
        return string.Equals(propertyName, "SpecBoardLeftDockTabCenterY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "CodexTaskBoardLeftDockTabCenterY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "NetworkMonitorLeftDockTabCenterY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "GuardBoardLeftDockTabCenterY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "CodexIqBoardLeftDockTabCenterY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "ResetSpeedBoardLeftDockTabCenterY", StringComparison.Ordinal) ||
            string.Equals(propertyName, "SystemDayBoardLeftDockTabCenterY", StringComparison.Ordinal);
    }

    private Control BuildLeftDockTabCenterEditor()
    {
        Panel panel = new Panel();
        panel.Width = 400;
        panel.Height = 54;
        panel.BackColor = Color.Transparent;

        ComboBox mode = new ComboBox();
        mode.Width = 124;
        mode.Height = 54;
        mode.DropDownStyle = ComboBoxStyle.DropDownList;
        mode.FlatStyle = FlatStyle.Flat;
        mode.BackColor = ControlBg;
        mode.ForeColor = TextSecondary;
        mode.Font = GetUiFont(9.5f);
        mode.Items.Add("自动");
        mode.Items.Add("手动");

        NumericUpDown position = new NumericUpDown();
        position.Location = new Point(136, 0);
        position.Width = 264;
        position.Height = 54;
        position.Minimum = 0;
        position.Maximum = 1000000;
        position.Increment = 10;
        position.BackColor = ControlBg;
        position.ForeColor = TextSecondary;
        position.BorderStyle = BorderStyle.FixedSingle;
        position.Font = GetUiFont(9.5f);

        LeftDockTabCenterEditorState state = new LeftDockTabCenterEditorState(mode, position);
        panel.Tag = state;
        mode.SelectedIndexChanged += delegate
        {
            state.SyncEnabledState();
            OnSettingChanged();
        };
        position.ValueChanged += delegate { OnSettingChanged(); };
        panel.Controls.Add(mode);
        panel.Controls.Add(position);
        return panel;
    }

    private Control BuildSpecBoardLedgerPathPicker()
    {
        Panel panel = new Panel();
        panel.Width = 560;
        panel.Height = 54;
        panel.BackColor = Color.Transparent;
        TextBox text = new TextBox();
        text.Width = 430;
        text.Height = 54;
        text.BackColor = ControlBg;
        text.ForeColor = TextSecondary;
        text.BorderStyle = BorderStyle.FixedSingle;
        text.Font = GetUiFont(9.5f);
        text.TextChanged += delegate { OnSettingChanged(); };
        Button browse = BuildCommandButton("浏览…", false);
        browse.Width = 116;
        browse.Height = 54;
        browse.Location = new Point(442, 0);
        browse.Click += delegate
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 Spec Board 账本";
                dialog.Filter = "JSON Lines (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = false;
                dialog.FileName = string.IsNullOrWhiteSpace(text.Text) ? WidgetSettings.DefaultSpecBoardLedgerPath : text.Text.Trim().Trim('"');
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    text.Text = dialog.FileName;
                }
            }
        };
        panel.Tag = text;
        panel.Controls.Add(text);
        panel.Controls.Add(browse);
        return panel;
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
        return string.Empty;
    }

    private static void RenderVariantSamplesForProperty(string propertyName, string directory)
    {
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

        RefreshColumnArrangementEditorEnabledStates();
        RefreshGlobalHotkeyRegistrationState();
        SetDirtyState(false);
    }

    private void RefreshColumnArrangementEditorEnabledStates()
    {
        RefreshColumnArrangementEditorEnabledState(
            "LeftDockAutoArrangeEnabled",
            new string[] { "LeftDockButtonOrder", "LeftDockButtonGapPixels", "LeftDockGroupOffsetY" });
        RefreshColumnArrangementEditorEnabledState(
            "RightTileAutoArrangeEnabled",
            new string[] { "RightTileButtonOrder", "RightTileButtonGapPixels", "RightTileGroupOffsetY" });
    }

    private void RefreshColumnArrangementEditorEnabledState(string toggleName, string[] dependentNames)
    {
        SettingEditor toggleEditor;
        if (!this.editors.TryGetValue(toggleName, out toggleEditor))
        {
            return;
        }

        ToggleSwitch toggle = toggleEditor.Control as ToggleSwitch;
        bool enabled = toggle != null && toggle.Checked;
        for (int i = 0; i < dependentNames.Length; i++)
        {
            SettingEditor dependent;
            if (this.editors.TryGetValue(dependentNames[i], out dependent))
            {
                // A disabled editor remains visible so users can understand the master/subordinate
                // relationship, but it cannot accept changes that have no runtime effect.
                dependent.Control.Enabled = enabled;
            }
        }
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
            RefreshNormalizedGlobalHotkeyEditors();
            RefreshGlobalHotkeyRegistrationState();
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
        ColumnOrderEditorControl orderEditor = editor.Control as ColumnOrderEditorControl;
        if (orderEditor != null)
        {
            orderEditor.SetOrderSilent(value as string[]);
            RelayoutSettingGroup(editor.Control);
            return;
        }

        NetworkProbeTargetEditorState targetState = editor.Control == null
            ? null
            : editor.Control.Tag as NetworkProbeTargetEditorState;
        if (targetState != null)
        {
            targetState.SetValues(value as string[]);
            editor.Control.Text = targetState.GetButtonText();
            return;
        }

        LeftDockTabCenterEditorState dockCenterState = editor.Control == null
            ? null
            : editor.Control.Tag as LeftDockTabCenterEditorState;
        if (dockCenterState != null)
        {
            dockCenterState.SetValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return;
        }

        if (editor.Property != null && string.Equals(editor.Property.Name, "SpecBoardLedgerPath", StringComparison.Ordinal))
        {
            TextBox pathText = editor.Control.Tag as TextBox;
            if (pathText != null)
            {
                pathText.Text = value as string ?? string.Empty;
                return;
            }
        }

        ToggleSwitch toggle = editor.Control as ToggleSwitch;
        if (toggle != null)
        {
            toggle.SetCheckedSilent(value is bool && (bool)value);
            return;
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
        ColumnOrderEditorControl orderEditor = editor.Control as ColumnOrderEditorControl;
        if (orderEditor != null)
        {
            return orderEditor.GetOrder();
        }

        NetworkProbeTargetEditorState targetState = editor.Control == null
            ? null
            : editor.Control.Tag as NetworkProbeTargetEditorState;
        if (targetState != null)
        {
            return NetworkProbeTargetSettings.CloneArray(targetState.Values);
        }

        LeftDockTabCenterEditorState dockCenterState = editor.Control == null
            ? null
            : editor.Control.Tag as LeftDockTabCenterEditorState;
        if (dockCenterState != null)
        {
            return dockCenterState.Value;
        }

        if (string.Equals(editor.Property.Name, "SpecBoardLedgerPath", StringComparison.Ordinal))
        {
            TextBox pathText = editor.Control.Tag as TextBox;
            return pathText == null ? string.Empty : pathText.Text ?? string.Empty;
        }

        ToggleSwitch toggle = editor.Control as ToggleSwitch;
        if (toggle != null)
        {
            return toggle.Checked;
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
        if (combo != null)
        {
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
            RefreshGlobalHotkeyRegistrationState();
        }
    }

    private void RefreshNormalizedGlobalHotkeyEditors()
    {
        string[] names =
        {
            "HotkeyToggleAllWindows",
            "HotkeyToggleHoverOpacity",
            "HotkeyOpenSettings"
        };
        this.initializing = true;
        try
        {
            for (int i = 0; i < names.Length; i++)
            {
                SettingEditor editor;
                if (this.editors.TryGetValue(names[i], out editor))
                {
                    SetEditorValue(editor, editor.Property.GetValue(this.baseline, null));
                }
            }
        }
        finally
        {
            this.initializing = false;
        }
    }

    private void RefreshGlobalHotkeyRegistrationState()
    {
        string[] names =
        {
            "HotkeyToggleAllWindows",
            "HotkeyToggleHoverOpacity",
            "HotkeyOpenSettings"
        };
        for (int i = 0; i < names.Length; i++)
        {
            SettingEditor editor;
            if (!this.editors.TryGetValue(names[i], out editor))
            {
                continue;
            }

            string failure = string.Empty;
            bool failed = this.owner != null &&
                this.owner.TryGetGlobalHotkeyRegistrationFailure(names[i], out failure);
            editor.Card.HintLabel.Text = GetSettingHint(names[i]) +
                (failed ? Environment.NewLine + failure : string.Empty);
            editor.Card.HintLabel.ForeColor = failed ? ErrorClr : TextTertiary;
            SettingGroupCard group = editor.Card.Parent as SettingGroupCard;
            if (group != null)
            {
                group.LayoutRows();
            }
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
        long hotkeyRegisterFirst = NativeMethods.GlobalHotkeyRegistrationAttemptCount;
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
        long hotkeyRegisterFinal = NativeMethods.GlobalHotkeyRegistrationAttemptCount;
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

        if (hotkeyRegisterFinal != hotkeyRegisterFirst)
        {
            throw new InvalidOperationException(
                "Settings open/close unexpectedly registered global hotkeys. First=" +
                hotkeyRegisterFirst.ToString(CultureInfo.InvariantCulture) +
                " Final=" + hotkeyRegisterFinal.ToString(CultureInfo.InvariantCulture));
        }

        return "Settings open/close policy: PASS iterations=" +
            loopCount.ToString(CultureInfo.InvariantCulture) +
            " handles_delta=" +
            handleDelta.ToString(CultureInfo.InvariantCulture) +
            " gdi_delta=" +
            gdiDelta.ToString(CultureInfo.InvariantCulture) +
            " user_delta=" +
            userDelta.ToString(CultureInfo.InvariantCulture) +
            " hotkey_register_first=" +
            hotkeyRegisterFirst.ToString(CultureInfo.InvariantCulture) +
            " hotkey_register_final=" +
            hotkeyRegisterFinal.ToString(CultureInfo.InvariantCulture);
    }

    internal static void RunSettingsBindingSelfTest()
    {
        VerifySettingsWindowActivationPolicy();
        VerifyUnsavedPreviewConsumePolicy();
        VerifyClaudeSetupTokenStoragePolicy();
        VerifyDeepSeekApiKeyStoragePolicy();
        VerifyGuardRuntimePersistencePolicy();
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
            "CodexPetZOrderProtectionEnabled",
            "HotkeyToggleAllWindows",
            "HotkeyToggleHoverOpacity",
            "HotkeyOpenSettings",
            "HoverOpacityEnabled",
            "HoverOpacityRevealDelayEnabled",
            "BurnInProtectionEnabled",
            "BurnInLevelOneIdleSeconds",
            "BurnInLevelTwoDelaySeconds",
            "OperationRadialCoreAutoHideKeepAliveEnabled",
            "OperationRadialIdleCollapseSeconds",
            "OperationRadialIdleResetOnInteractionEnabled",
            "OperationRadialKeepOpenAfterLeafClickEnabled",
            "SpecBoardWidth",
            "SpecBoardHeight",
            "SpecBoardAutoHideSeconds",
            "LeftDockOutsideClickCollapseEnabled",
            "SpecBoardAutoPopupEnabled",
            "SpecBoardAutoPopupSeconds",
            "SpecBoardLedgerPath",
            "SpecBoardManagerWidth",
            "SpecBoardManagerHeight",
            "SpecBoardManagerDangerZoneRequiresTypedConfirm",
            "SpecBoardLeftDockTabCenterY",
            "LeftDockAutoArrangeEnabled",
            "LeftDockButtonOrder",
            "LeftDockButtonGapPixels",
            "LeftDockGroupOffsetY",
            "CodexTaskBoardLeftDockTabCenterY",
            "NetworkMonitorLeftDockTabCenterY",
            "GuardBoardLeftDockTabCenterY",
            "GuardBoardAutoHideSeconds",
            "GuardBoardTransparencyOverridePercent",
            "GuardBoardScaleOverridePercent",
            "CodexIqBoardLeftDockTabCenterY",
            "CodexIqBoardAutoHideSeconds",
            "CodexIqBoardTransparencyOverridePercent",
            "CodexIqBoardScaleOverridePercent",
            "ResetSpeedBoardLeftDockTabCenterY",
            "ResetSpeedBoardAutoHideSeconds",
            "ResetSpeedBoardTransparencyOverridePercent",
            "ResetSpeedBoardScaleOverridePercent",
            "SystemDayBoardLeftDockTabCenterY",
            "SystemDayBoardAutoHideSeconds",
            "SystemDayBoardTransparencyOverridePercent",
            "SystemDayBoardScaleOverridePercent",
            "RightTileAutoArrangeEnabled",
            "RightTileButtonOrder",
            "RightTileButtonGapPixels",
            "RightTileGroupOffsetY",
            "RightTileMouseClickThroughEnabled",
            "GeniusProgrammerEasterEggEnabled",
            "FallbackDisconnectedDisplaysEnabled",
            "ResolutionCompatibilityModeEnabled",
            "ResolutionCompatibilityScalePercent",
            "MainDisplayDeviceName",
            GlobalLayoutEditCommandName,
            "MainWidgetTileLargeModeEnabled",
            "MetricTileExpandWidth",
            "MetricTileExpandHeight",
            "CodexRadarSoftwareMode",
            "CodexRadarModelKey",
            "RadarClockAutoSwitchModelEnabled",
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
            ClaudeSetupTokenCommandName,
            DeepSeekApiKeyCommandName,
            "AiRequestProtectionAutoEnabled",
            "AiRequestProtectionManualBlockEnabled",
            "AiChinaEgressGuardEnabled",
            "CodexQuotaPlanEnabled",
            "CodexQuotaPlanWeeklyComparison",
            "CodexQuotaPlanWeeklyThresholdPercent",
            "CodexQuotaPlanFiveHourComparison",
            "CodexQuotaPlanFiveHourThresholdPercent",
            "CodexQuotaPlanResumeConditionMode",
            "CodexQuotaPlanAutoResumePausedGoals",
            "CodexQuotaPlanPauseGoalIds",
            "CodexQuotaPlanResumeGoalIds",
            "PowerThermalManualEnergySaverThresholdPercent",
            "GfwProbeIntervalMinutes",
            "CloudEndpointTargets",
            "FixedPingTargets",
            "OperationButtonSize",
            "OperationPrimaryPanelMode",
            "OperationDoubleClickSpecialMenuEnabled",
            "OperationSettingsLogicExtensionEnabled"
        };

        for (int i = 0; i < required.Length; i++)
        {
            if (!this.editors.ContainsKey(required[i]))
            {
                throw new InvalidOperationException("WinUI settings binding missing: " + required[i]);
            }
        }

        VerifySettingsUiBindingCoverage();
        VerifyLeftDockTabCenterEditorPolicy();
        VerifyColumnArrangementEditorPolicy();

        for (int i = 0; i < this.pages.Count; i++)
        {
            LayoutPage(this.pages[i]);
        }

        VerifySearchFilteredGroupLayoutPolicy();
        VerifyVisibleSurfaceScaleEditorNormalization();
        VerifyDynamicResolutionSizingPolicy();
        VerifyNoVisibleControlClipping();

        WidgetSettings settings = ReadSettings();
        if (settings == null)
        {
            throw new InvalidOperationException("WinUI settings read failed.");
        }

        VerifyNetworkProbeTargetEditors(settings);

        FluentScrollPanel page = GetSelectedScrollPage();
        if (page == null || !page.ScrollByMouseWheelDelta(-120))
        {
            throw new InvalidOperationException("WinUI settings page wheel scroll failed.");
        }
    }

    private void VerifyLeftDockTabCenterEditorPolicy()
    {
        string[] names = new string[]
        {
            "SpecBoardLeftDockTabCenterY",
            "CodexTaskBoardLeftDockTabCenterY",
            "NetworkMonitorLeftDockTabCenterY",
            "GuardBoardLeftDockTabCenterY",
            "CodexIqBoardLeftDockTabCenterY",
            "ResetSpeedBoardLeftDockTabCenterY",
            "SystemDayBoardLeftDockTabCenterY"
        };
        for (int i = 0; i < names.Length; i++)
        {
            SettingEditor editor = this.editors[names[i]];
            object original = editor.Property.GetValue(this.baseline, null);
            SetEditorValue(editor, WidgetSettings.AutoLeftDockTabCenterY);
            if (Convert.ToInt32(GetEditorValue(editor), CultureInfo.InvariantCulture) != WidgetSettings.AutoLeftDockTabCenterY)
            {
                throw new InvalidOperationException("WinUI left dock auto sentinel binding failed: " + names[i]);
            }

            SetEditorValue(editor, 731 + i);
            if (Convert.ToInt32(GetEditorValue(editor), CultureInfo.InvariantCulture) != 731 + i)
            {
                throw new InvalidOperationException("WinUI left dock manual center binding failed: " + names[i]);
            }

            SetEditorValue(editor, original);
        }

        WidgetSettings normalized = this.baseline.Clone();
        normalized.SpecBoardLeftDockTabCenterY = -20;
        normalized.CodexTaskBoardLeftDockTabCenterY = -30;
        normalized.NetworkMonitorLeftDockTabCenterY = -40;
        normalized.GuardBoardLeftDockTabCenterY = -50;
        normalized.CodexIqBoardLeftDockTabCenterY = -60;
        normalized.ResetSpeedBoardLeftDockTabCenterY = -70;
        normalized.SystemDayBoardLeftDockTabCenterY = -80;
        normalized.Normalize();
        if (normalized.SpecBoardLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY ||
            normalized.CodexTaskBoardLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY ||
            normalized.NetworkMonitorLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY ||
            normalized.GuardBoardLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY ||
            normalized.CodexIqBoardLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY ||
            normalized.ResetSpeedBoardLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY ||
            normalized.SystemDayBoardLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY)
        {
            throw new InvalidOperationException("WinUI left dock centers must normalize invalid negative values to the auto sentinel.");
        }
    }

    private void VerifyColumnArrangementEditorPolicy()
    {
        string[] orderNames = { "LeftDockButtonOrder", "RightTileButtonOrder" };
        for (int i = 0; i < orderNames.Length; i++)
        {
            SettingEditor editor = this.editors[orderNames[i]];
            string[] original = (string[])editor.Property.GetValue(this.baseline, null);
            string[] reversed = i == 0
                ? new string[] { "SystemDay", "ResetSpeed", "CodexIq", "Guard", "CodexTask", "SpecBoard", "Network" }
                : new string[] { "DeepSeekQuota", "ClaudeQuota", "CodexQuota", "Guard", "Power", "Npu", "Gpu", "Network", "Disk", "Memory", "Cpu" };
            SetEditorValue(editor, reversed);
            string[] actual = GetEditorValue(editor) as string[];
            if (actual == null || actual.Length != reversed.Length || object.ReferenceEquals(actual, reversed))
            {
                throw new InvalidOperationException("WinUI column order binding or clone isolation failed: " + orderNames[i]);
            }

            for (int j = 0; j < reversed.Length; j++)
            {
                if (!string.Equals(actual[j], reversed[j], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("WinUI column order binding failed: " + orderNames[i]);
                }
            }

            ColumnOrderEditorControl orderControl = editor.Control as ColumnOrderEditorControl;
            int changeCount = 0;
            orderControl.ValueChanged += delegate { changeCount++; };
            bool originalInitializing = this.initializing;
            this.initializing = true;
            try
            {
                orderControl.PerformMoveForSelfTest(reversed[0], 1);
            }
            finally
            {
                this.initializing = originalInitializing;
            }

            string[] moved = orderControl.GetOrder();
            if (changeCount != 1 ||
                !string.Equals(moved[0], reversed[1], StringComparison.Ordinal) ||
                !string.Equals(moved[1], reversed[0], StringComparison.Ordinal))
            {
                throw new InvalidOperationException("WinUI column order move action failed: " + orderNames[i]);
            }

            string duplicateId = i == 0 ? "guard" : "cpu";
            string[] malformed = { duplicateId, "unknown-surface", duplicateId.ToUpperInvariant() };
            SetEditorValue(editor, malformed);
            string[] normalized = orderControl.GetOrder();
            if (normalized.Length != reversed.Length ||
                !string.Equals(normalized[0], i == 0 ? "Guard" : "Cpu", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("WinUI column order normalization failed: " + orderNames[i]);
            }

            int originalWidth = orderControl.Width;
            orderControl.Width = 320;
            orderControl.VerifyRowsFitForSelfTest();
            orderControl.Width = originalWidth;

            SetEditorValue(editor, original);
        }

        string[] sliderNames =
        {
            "LeftDockButtonGapPixels", "LeftDockGroupOffsetY",
            "RightTileButtonGapPixels", "RightTileGroupOffsetY"
        };
        for (int i = 0; i < sliderNames.Length; i++)
        {
            SettingEditor editor = this.editors[sliderNames[i]];
            PercentSliderControl slider = editor.Control as PercentSliderControl;
            bool offset = IsColumnGroupOffsetSetting(sliderNames[i]);
            string expectedSuffix = offset ? " px" : "%";
            if (slider == null || slider.Suffix != expectedSuffix ||
                slider.UseNumericInput == offset || string.IsNullOrEmpty(slider.AccessibleLabel))
            {
                throw new InvalidOperationException("WinUI column range slider binding failed: " + sliderNames[i]);
            }

            int expectedMinimum = offset ? WidgetSettings.MinColumnGroupOffsetY : WidgetSettings.MinColumnButtonGapPixels;
            int expectedMaximum = offset ? WidgetSettings.MaxColumnGroupOffsetY : WidgetSettings.MaxColumnButtonGapPixels;
            object original = editor.Property.GetValue(this.baseline, null);
            SetEditorValue(editor, expectedMinimum);
            if (slider.Minimum != expectedMinimum || slider.Maximum != expectedMaximum ||
                Convert.ToInt32(GetEditorValue(editor), CultureInfo.InvariantCulture) != expectedMinimum ||
                slider.ShowPositiveSign != offset)
            {
                throw new InvalidOperationException("WinUI column range slider policy failed: " + sliderNames[i]);
            }

            if (!offset)
            {
                slider.SetNumericValueForSelfTest(expectedMaximum);
                if (slider.Value != expectedMaximum ||
                    Convert.ToInt32(GetEditorValue(editor), CultureInfo.InvariantCulture) != expectedMaximum)
                {
                    throw new InvalidOperationException("WinUI column spacing numeric input binding failed: " + sliderNames[i]);
                }
            }

            SetEditorValue(editor, original);
        }

        VerifyColumnArrangementDependentEnabledState("LeftDockAutoArrangeEnabled", "LeftDockButtonOrder");
        VerifyColumnArrangementDependentEnabledState("RightTileAutoArrangeEnabled", "RightTileButtonOrder");
    }

    private void VerifyColumnArrangementDependentEnabledState(string toggleName, string dependentName)
    {
        SettingEditor toggleEditor = this.editors[toggleName];
        SettingEditor dependentEditor = this.editors[dependentName];
        ToggleSwitch toggle = toggleEditor.Control as ToggleSwitch;
        bool original = toggle.Checked;
        toggle.SetCheckedSilent(false);
        RefreshColumnArrangementEditorEnabledStates();
        if (dependentEditor.Control.Enabled)
        {
            throw new InvalidOperationException("WinUI column subordinate editor must disable with auto arrange: " + dependentName);
        }

        toggle.SetCheckedSilent(true);
        RefreshColumnArrangementEditorEnabledStates();
        if (!dependentEditor.Control.Enabled)
        {
            throw new InvalidOperationException("WinUI column subordinate editor did not re-enable: " + dependentName);
        }

        toggle.SetCheckedSilent(original);
        RefreshColumnArrangementEditorEnabledStates();
    }

    private void VerifySearchFilteredGroupLayoutPolicy()
    {
        SettingEditor target = this.editors["LeftDockGroupOffsetY"];
        SettingGroupCard group = target.Card.Parent as SettingGroupCard;
        if (group == null)
        {
            throw new InvalidOperationException("WinUI layout search self-test could not locate target group.");
        }

        string originalQuery = this.searchBox.Text;
        try
        {
            this.searchBox.Text = GetSettingTitle(target.Name);
            ApplySearchFilter();
            if (!target.Card.Visible || target.Card.Top != 0 || target.Card.ShowTopDivider ||
                group.Height != target.Card.Height)
            {
                throw new InvalidOperationException("WinUI filtered group retained hidden-row geometry.");
            }

            for (int i = 0; i < group.Controls.Count; i++)
            {
                Control row = group.Controls[i];
                if (!row.Visible && (row.Width != 0 || row.Height != 0))
                {
                    throw new InvalidOperationException("WinUI filtered hidden row still occupies space.");
                }
            }
        }
        finally
        {
            this.searchBox.Text = originalQuery;
            ApplySearchFilter();
        }
    }

    private static void VerifyClaudeSetupTokenStoragePolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-settings-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encrypted = Path.Combine(root, "claude-setup-token.bin");
            string legacy = Path.Combine(root, "claude-setup-token.txt");
            string errorCode;
            const string token = "oauth-settings-entry";
            if (!TrySaveClaudeSetupTokenFile(token, encrypted, legacy, out errorCode) ||
                !File.Exists(encrypted) ||
                File.ReadAllText(encrypted, Encoding.UTF8).IndexOf(token, StringComparison.Ordinal) >= 0 ||
                !string.Equals(
                    ClaudeCodeUsageReader.ReadConfiguredSetupTokenFiles(encrypted, legacy),
                    token,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Claude setup-token encrypted save self-test failed: " + errorCode);
            }

            File.WriteAllText(legacy, "must-not-resurrect", SharedEncoding.Utf8NoBom);
            File.WriteAllText(legacy + ".migrated", "must-not-resurrect", SharedEncoding.Utf8NoBom);
            File.WriteAllText(legacy + ".migrated.20260720", "must-not-resurrect", SharedEncoding.Utf8NoBom);
            if (!TrySaveClaudeSetupTokenFile(string.Empty, encrypted, legacy, out errorCode) ||
                File.Exists(encrypted) ||
                File.Exists(legacy) ||
                File.Exists(legacy + ".migrated") ||
                File.Exists(legacy + ".migrated.20260720") ||
                ClaudeCodeUsageReader.ReadConfiguredSetupTokenFiles(encrypted, legacy).Length != 0)
            {
                throw new InvalidOperationException("Claude setup-token clear self-test failed: " + errorCode);
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

    private static void VerifyDeepSeekApiKeyStoragePolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-settings-deepseek-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encrypted = Path.Combine(root, "deepseek-api-key.bin");
            string legacy = Path.Combine(root, "deepseek-api-key.txt");
            string errorCode;
            const string apiKey = "sk-settings-entry";
            if (!TrySaveDeepSeekApiKeyFile(apiKey, encrypted, legacy, out errorCode) ||
                !File.Exists(encrypted) ||
                File.ReadAllText(encrypted, Encoding.UTF8).IndexOf(apiKey, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("DeepSeek API-key encrypted save self-test failed: " + errorCode);
            }

            string restored;
            bool migrated;
            if (!SecretStore.TryReadOrMigrateSecret(
                    encrypted,
                    legacy,
                    SecretStore.TrimSecret,
                    delegate(string value)
                    {
                        return !string.IsNullOrWhiteSpace(value) &&
                            value.Trim().StartsWith("sk-", StringComparison.Ordinal);
                    },
                    out restored,
                    out migrated,
                    out errorCode) ||
                !string.Equals(restored, apiKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DeepSeek API-key encrypted read self-test failed: " + errorCode);
            }

            if (!TrySaveDeepSeekApiKeyFile(string.Empty, encrypted, legacy, out errorCode) ||
                File.Exists(encrypted) || File.Exists(legacy))
            {
                throw new InvalidOperationException("DeepSeek API-key clear self-test failed: " + errorCode);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void VerifyGuardRuntimePersistencePolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-guard-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.ini");
            WidgetSettings committedA = WidgetSettings.CreateDefaults();
            committedA.MetricTileExpandWidth = 522;
            committedA.ApplicationTransparencyPercent = 71;
            committedA.GuardSleepEnabled = false;
            committedA.SaveToPathForSelfTest(path);

            WidgetSettings previewB = committedA.Clone();
            previewB.MetricTileExpandWidth = 777;
            previewB.ApplicationTransparencyPercent = 43;
            WidgetSettings guard = previewB.Clone();
            guard.GuardSleepEnabled = true;
            guard.GuardSleepSinceUtcTicks = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc).Ticks;
            guard.GuardDisplayMinutes = 120;
            guard.GuardOfflineThresholdMinutes = 5;
            guard.GuardDisplayUntilUtcTicks = new DateTime(2026, 7, 20, 2, 0, 0, DateTimeKind.Utc).Ticks;
            guard.GuardBatteryCarePauseUntilUtcTicks = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc).Ticks;

            WidgetSettings merged = WidgetForm.MergeGuardRuntimeFields(committedA, guard);
            merged.SaveToPathForSelfTest(path);
            WidgetSettings disk = WidgetSettings.LoadFromPathForSelfTest(path);
            if (disk.MetricTileExpandWidth != committedA.MetricTileExpandWidth ||
                disk.ApplicationTransparencyPercent != committedA.ApplicationTransparencyPercent ||
                disk.MetricTileExpandWidth == previewB.MetricTileExpandWidth ||
                disk.ApplicationTransparencyPercent == previewB.ApplicationTransparencyPercent ||
                !disk.GuardSleepEnabled ||
                disk.GuardSleepSinceUtcTicks != guard.GuardSleepSinceUtcTicks ||
                disk.GuardDisplayMinutes != 120 ||
                disk.GuardOfflineThresholdMinutes != 5 ||
                disk.GuardDisplayUntilUtcTicks != guard.GuardDisplayUntilUtcTicks ||
                disk.GuardBatteryCarePauseUntilUtcTicks != guard.GuardBatteryCarePauseUntilUtcTicks)
            {
                throw new InvalidOperationException("GUARD committed-snapshot persistence self-test failed.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private void VerifyVisibleSurfaceScaleEditorNormalization()
    {
        SettingEditor editor;
        if (!this.editors.TryGetValue("NetworkMonitorScaleOverridePercent", out editor))
        {
            throw new InvalidOperationException("Visible-surface scale override editor self-test fixture is missing.");
        }

        NumericUpDown number = editor.Control as NumericUpDown;
        if (number == null)
        {
            throw new InvalidOperationException("Visible-surface scale override editor is not numeric.");
        }

        decimal original = number.Value;
        try
        {
            number.Value = 20;
            WidgetSettings model = ReadSettings();
            if (number.Value != WidgetSettings.MinResolutionCompatibilityScalePercent ||
                model.NetworkMonitorScaleOverridePercent != (int)number.Value)
            {
                throw new InvalidOperationException("Visible-surface scale override editor/model normalization self-test failed.");
            }
        }
        finally
        {
            number.Value = original;
        }
    }

    private void VerifySettingsUiBindingCoverage()
    {
        Dictionary<string, string> exemptions = CreateSettingsUiBindingExemptions();
        PropertyInfo[] properties = typeof(WidgetSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Dictionary<string, PropertyInfo> writableProperties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        List<string> errors = new List<string>();
        int boundCount = 0;
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            writableProperties[property.Name] = property;
            bool bound = this.editors.ContainsKey(property.Name);
            bool exempt = exemptions.ContainsKey(property.Name);
            if (bound && exempt)
            {
                errors.Add(property.Name + " is both bound and exempt");
            }
            else if (!bound && !exempt)
            {
                errors.Add(property.Name + " has no settings UI binding or explicit exemption");
            }
            else if (bound)
            {
                boundCount++;
            }
        }

        foreach (KeyValuePair<string, string> exemption in exemptions)
        {
            if (!writableProperties.ContainsKey(exemption.Key))
            {
                errors.Add(exemption.Key + " exemption does not name a public writable setting");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("WinUI settings binding coverage failed: " + string.Join("; ", errors.ToArray()));
        }

        Console.WriteLine(
            "Settings UI binding coverage: PASS bound=" + boundCount.ToString(CultureInfo.InvariantCulture) +
            " exempt=" + exemptions.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static Dictionary<string, string> CreateSettingsUiBindingExemptions()
    {
        Dictionary<string, string> exemptions = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSettingsUiBindingExemptions(exemptions, "captured work-area cache; updated by display/layout adaptation, not edited directly", new string[]
        {
            "LayoutWorkAreaLeft", "LayoutWorkAreaTop", "LayoutWorkAreaWidth", "LayoutWorkAreaHeight",
            "OperationLayoutWorkAreaLeft", "OperationLayoutWorkAreaTop", "OperationLayoutWorkAreaWidth", "OperationLayoutWorkAreaHeight"
        });
        AddSettingsUiBindingExemptions(exemptions, "legacy render/test compatibility key; intentionally hidden from the supported settings surface", new string[]
        {
            "OperationRenderVariant", "CodexRadarTestMode", "ServiceHealthTestMode"
        });
        AddSettingsUiBindingExemptions(exemptions, "compatibility-only power integration flag; retained for existing data flow but hidden from the settings surface", new string[]
        {
            "PowerThermalIntegratedEnabled"
        });
        AddSettingsUiBindingExemptions(exemptions, "compatibility-only hidden-host policy; no visible surface consumes these values after interface convergence", new string[]
        {
            "ClickThroughMode", "MainWidgetScaleOverridePercent"
        });
        AddSettingsUiBindingExemptions(exemptions, "compatibility-only undocked Spec Board fallback coordinates; the production board is always owned by the fixed left dock", new string[]
        {
            "SpecBoardLeftX", "SpecBoardBottomY"
        });
        AddSettingsUiBindingExemptions(exemptions, "legacy model enum derived from CodexRadarModelKey; retained only for settings-file compatibility", new string[]
        {
            "CodexRadarModelVersion"
        });
        AddSettingsUiBindingExemptions(exemptions, "compatibility-only retired Radar presentation controls; current tiles, expansion panels and Codex IQ board consume family snapshots directly", new string[]
        {
            "RadarClockTimeDisplayMode", "CodexRadarSpeedWindowCountdownEnabled", "CodexRadarQuotaResetRainbowEnabled",
            "DisplayTimeZoneMode", "DisplayTimeZoneId",
            "CodexModelIqTestEnabled", "CodexModelIqTestPassed", "CodexModelIqBaselineAutoEnabled",
            "CodexModelIqBaselinePassed", "CodexModelIqBaselineValidTasks", "CodexModelEfficiencyTestEnabled",
            "CodexModelTokenEfficiencyTestPercent", "CodexModelTimeEfficiencyTestPercent",
            "CodexModelTokenEfficiencyBaselineMode", "CodexModelTokenEfficiencyBaselinePassed",
            "CodexModelTokenEfficiencyBaselineTokens", "CodexModelTimeEfficiencyBaselineMode",
            "CodexModelTimeEfficiencyBaselinePassed", "CodexModelTimeEfficiencyBaselineSeconds",
            "CodexModelTokenEfficiencyLowThresholdPercent", "CodexModelTimeEfficiencyLowThresholdPercent"
        });
        AddSettingsUiBindingExemptions(exemptions, "compatibility-only dock flags; the canonical visible topology always contains all seven left-edge tabs", new string[]
        {
            "SpecBoardLeftDockEnabled", "CodexTaskBoardLeftDockEnabled",
            "GuardBoardLeftDockEnabled", "CodexIqBoardLeftDockEnabled", "ResetSpeedBoardLeftDockEnabled",
            "SystemDayBoardLeftDockEnabled"
        });
        AddSettingsUiBindingExemptions(exemptions, "Codex task-board geometry/view is owned by the board surface; monitor thresholds are internal tuning", new string[]
        {
            "CodexTaskBoardWidth", "CodexTaskBoardHeight", "CodexTaskBoardView", "CodexTaskBoardTimelineMinutes",
            "CodexTaskMonitorEnabled", "CodexTaskMonitorActiveWindowMinutes", "CodexTaskMonitorActiveSeconds",
            "CodexTaskMonitorIdleSeconds", "CodexTaskMonitorTerminalHoldSeconds", "CodexTaskMonitorErrorHoldSeconds",
            "CodexTaskMonitorNumberCooldownSeconds"
        });
        AddSettingsUiBindingExemptions(exemptions, "GUARD runtime or board-owned value; persisted for restart recovery and edited on the GUARD board", new string[]
        {
            "GuardSleepEnabled", "GuardSleepSinceUtcTicks", "GuardDisplayMinutes", "GuardOfflineThresholdMinutes",
            "GuardDisplayUntilUtcTicks", "GuardBatteryCarePauseUntilUtcTicks"
        });
        AddSettingsUiBindingExemptions(exemptions, "transient action state controlled by the operation panel or hotkey, not a preference row", new string[]
        {
            "ForceHoverOpacityActive", "ManualHoverOpacityActive"
        });
        AddSettingsUiBindingExemptions(exemptions, "derived legacy compatibility value; OperationPrimaryPanelMode is the sole editor", new string[]
        {
            "OperationWindowsButtonEnabled", "OperationMemoryPieEnabled"
        });
        AddSettingsUiBindingExemptions(exemptions, "derived legacy compatibility value; PerformanceMode is the sole editor", new string[] { "PowerSavingEnabled" });
        AddSettingsUiBindingExemptions(exemptions, "per-tile manual coordinates are edited by the global layout editor", new string[]
        {
            "MetricTileLeftX", "MetricTileBottomY"
        });
        AddSettingsUiBindingExemptions(exemptions, "shared dock collapse timing is an internal interaction constant", new string[] { "LeftDockCollapseSeconds" });
        AddSettingsUiBindingExemptions(exemptions, "derived IQ baseline provenance retained for compatibility", new string[] { "CodexModelIqBaselineMode" });
        return exemptions;
    }

    private static void AddSettingsUiBindingExemptions(
        Dictionary<string, string> exemptions,
        string reason,
        string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            exemptions.Add(names[i], reason);
        }
    }

    private void VerifyNetworkProbeTargetEditors(WidgetSettings settings)
    {
        string[] names = { "CloudEndpointTargets", "FixedPingTargets" };
        for (int i = 0; i < names.Length; i++)
        {
            SettingEditor editor;
            if (!this.editors.TryGetValue(names[i], out editor))
            {
                throw new InvalidOperationException("Network probe target editor missing: " + names[i]);
            }

            NetworkProbeTargetEditorState state = editor.Control.Tag as NetworkProbeTargetEditorState;
            string[] value = editor.Property.GetValue(settings, null) as string[];
            if (state == null || value == null ||
                !string.Equals(
                    NetworkProbeTargetSettings.BuildSignature(state.Values),
                    NetworkProbeTargetSettings.BuildSignature(value),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network probe target editor round-trip failed: " + names[i]);
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
                if (!card.Visible)
                {
                    continue;
                }

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
        { "MetricTileExpandWidth", new NumericRange(WidgetSettings.MinMetricTileExpandWidth, WidgetSettings.MaxMetricTileExpandWidth) },
        { "MetricTileExpandHeight", new NumericRange(WidgetSettings.MinMetricTileExpandHeight, WidgetSettings.MaxMetricTileExpandHeight) },
        { "PowerThermalManualEnergySaverThresholdPercent", new NumericRange(WidgetSettings.MinPowerThermalManualEnergySaverThresholdPercent, WidgetSettings.MaxPowerThermalManualEnergySaverThresholdPercent) },
        { "GfwProbeIntervalMinutes", new NumericRange(WidgetSettings.MinGfwProbeIntervalMinutes, WidgetSettings.MaxGfwProbeIntervalMinutes) },
        { "SpecBoardWidth", new NumericRange(WidgetSettings.MinSpecBoardWidth, WidgetSettings.MaxSpecBoardWidth) },
        { "SpecBoardHeight", new NumericRange(WidgetSettings.MinSpecBoardHeight, WidgetSettings.MaxSpecBoardHeight) },
        { "SpecBoardAutoHideSeconds", new NumericRange(WidgetSettings.MinSpecBoardAutoHideSeconds, WidgetSettings.MaxSpecBoardAutoHideSeconds) },
        { "SpecBoardLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "CodexTaskBoardLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "NetworkMonitorLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "GuardBoardLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "GuardBoardAutoHideSeconds", new NumericRange(WidgetSettings.MinGuardBoardAutoHideSeconds, WidgetSettings.MaxGuardBoardAutoHideSeconds) },
        { "CodexIqBoardLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "CodexIqBoardAutoHideSeconds", new NumericRange(WidgetSettings.MinCodexIqBoardAutoHideSeconds, WidgetSettings.MaxCodexIqBoardAutoHideSeconds) },
        { "ResetSpeedBoardLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "ResetSpeedBoardAutoHideSeconds", new NumericRange(WidgetSettings.MinResetSpeedBoardAutoHideSeconds, WidgetSettings.MaxResetSpeedBoardAutoHideSeconds) },
        { "SystemDayBoardLeftDockTabCenterY", new NumericRange(WidgetSettings.AutoLeftDockTabCenterY, 1000000) },
        { "SystemDayBoardAutoHideSeconds", new NumericRange(WidgetSettings.MinSystemDayBoardAutoHideSeconds, WidgetSettings.MaxSystemDayBoardAutoHideSeconds) },
        { "SpecBoardAutoPopupSeconds", new NumericRange(WidgetSettings.MinSpecBoardAutoPopupSeconds, WidgetSettings.MaxSpecBoardAutoPopupSeconds) },
        { "SpecBoardManagerWidth", new NumericRange(WidgetSettings.MinSpecBoardManagerWidth, WidgetSettings.MaxSpecBoardManagerWidth) },
        { "SpecBoardManagerHeight", new NumericRange(WidgetSettings.MinSpecBoardManagerHeight, WidgetSettings.MaxSpecBoardManagerHeight) },
        { "ConnectionCheckIntervalSeconds", new NumericRange(WidgetSettings.MinConnectionCheckIntervalSeconds, WidgetSettings.MaxConnectionCheckIntervalSeconds) },
        { "OperationButtonSize", new NumericRange(WidgetSettings.MinOperationButtonSize, WidgetSettings.MaxOperationButtonSize) },
        { "OperationLeftOffset", new NumericRange(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset) },
        { "OperationBottomOffset", new NumericRange(WidgetSettings.MinOperationOffset, WidgetSettings.MaxOperationOffset) },
        { "ResolutionCompatibilityScalePercent", new NumericRange(WidgetSettings.MinResolutionCompatibilityScalePercent, WidgetSettings.MaxResolutionCompatibilityScalePercent) },
        { "NetworkMonitorScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "OperationScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "SpecBoardScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "CodexTaskBoardScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "GuardBoardScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "CodexIqBoardScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "ResetSpeedBoardScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "SystemDayBoardScaleOverridePercent", new NumericRange(WidgetSettings.MinWindowScaleOverridePercent, WidgetSettings.MaxWindowScaleOverridePercent) },
        { "MainWidgetTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "NetworkMonitorTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "OperationTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "SpecBoardTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "CodexTaskBoardTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "GuardBoardTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "CodexIqBoardTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "ResetSpeedBoardTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "SystemDayBoardTransparencyOverridePercent", new NumericRange(WidgetSettings.MinWindowTransparencyOverridePercent, WidgetSettings.MaxWindowTransparencyOverridePercent) },
        { "NightScheduleStartMinutes", new NumericRange(WidgetSettings.MinNightScheduleMinutes, WidgetSettings.MaxNightScheduleMinutes) },
        { "NightScheduleEndMinutes", new NumericRange(WidgetSettings.MinNightScheduleMinutes, WidgetSettings.MaxNightScheduleMinutes) },
        { "NightDimLuminancePercent", new NumericRange(WidgetSettings.MinNightDimLuminancePercent, WidgetSettings.MaxNightDimLuminancePercent) },
        { "SensitiveMouseRangePixels", new NumericRange(WidgetSettings.MinSensitiveMouseRangePixels, WidgetSettings.MaxSensitiveMouseRangePixels) },
        { "HoverOpacityRevealDelaySeconds", new NumericRange((decimal)WidgetSettings.MinHoverOpacityRevealDelaySeconds, (decimal)WidgetSettings.MaxHoverOpacityRevealDelaySeconds) },
        { "HoverOpacityRevealResetSeconds", new NumericRange((decimal)WidgetSettings.MinHoverOpacityRevealResetSeconds, (decimal)WidgetSettings.MaxHoverOpacityRevealResetSeconds) },
        { "ReverseHoverOpacityRestoreDelaySeconds", new NumericRange(WidgetSettings.MinReverseHoverOpacityRestoreDelaySeconds, WidgetSettings.MaxReverseHoverOpacityRestoreDelaySeconds) },
        { "AutoHoverOpacityIdleSeconds", new NumericRange(WidgetSettings.MinAutoHoverOpacityIdleSeconds, WidgetSettings.MaxAutoHoverOpacityIdleSeconds) },
        { "BurnInLevelOneIdleSeconds", new NumericRange(WidgetSettings.MinBurnInLevelOneIdleSeconds, WidgetSettings.MaxBurnInLevelOneIdleSeconds) },
        { "BurnInLevelTwoDelaySeconds", new NumericRange(WidgetSettings.MinBurnInLevelTwoDelaySeconds, WidgetSettings.MaxBurnInLevelTwoDelaySeconds) },
        { "OperationRadialIdleCollapseSeconds", new NumericRange(WidgetSettings.NeverOperationRadialIdleCollapseSeconds, WidgetSettings.MaxOperationRadialIdleCollapseSeconds) },
        { "CodexQuotaPlanWeeklyThresholdPercent", new NumericRange(WidgetSettings.MinCodexQuotaPlanThresholdPercent, WidgetSettings.MaxCodexQuotaPlanThresholdPercent) },
        { "CodexQuotaPlanFiveHourThresholdPercent", new NumericRange(WidgetSettings.MinCodexQuotaPlanThresholdPercent, WidgetSettings.MaxCodexQuotaPlanThresholdPercent) },
    };

    private static readonly Dictionary<string, string> SettingTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "StartupEnabled", "开机启动" },
        { "PerformanceMode", "性能模式" },
        { "VisibilityMode", "可见性" },
        { "CodexPetZOrderProtectionEnabled", "小窗保持在 Codex 宠物下层" },
        { "VisibilityOverlapIgnoresOperationPanelEnabled", "遮挡忽略操作面板" },
        { "ForceShowForegroundFpsEnabled", "强制显示 FPS" },
        { "OperationPrimaryPanelMode", "左侧区域模式" },
        { "OperationWindowsButtonEnabled", "显示 Windows 按钮" },
        { "OperationMemoryPieEnabled", "显示内存饼图" },
        { "SeelenDockForegroundPulseEnabled", "Seelen Dock 自动拉前" },
        { "WinDRecoveryPulseEnabled", "Win+D 后延迟拉前" },
        { "PowerResumeRestartEnabled", "休眠唤醒后重启" },
        { "AiRequestProtectionAutoEnabled", "AI 自动阻断" },
        { "AiRequestProtectionManualBlockEnabled", "AI 手动阻断" },
        { "AiChinaEgressGuardEnabled", "大陆出口保护" },
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
        { "MainDisplayDeviceName", "右侧磁贴列显示器" },
        { "OperationDisplayDeviceName", "操作面板显示器" },
        { "ResolutionCompatibilityModeEnabled", "分辨率兼容模式" },
        { "ResolutionCompatibilityScalePercent", "兼容缩放比例" },
        { GlobalLayoutEditCommandName, "全局编辑" },
        { "LeftDockAutoArrangeEnabled", "自动排列左侧面板列" },
        { "LeftDockButtonOrder", "左侧按钮顺序" },
        { "LeftDockButtonGapPixels", "左侧按钮分布间距（0–100）" },
        { "LeftDockGroupOffsetY", "左侧整列上下位置" },
        { "RightTileAutoArrangeEnabled", "自动排列右侧磁贴列" },
        { "RightTileButtonOrder", "右侧磁贴顺序" },
        { "RightTileButtonGapPixels", "右侧磁贴分布间距（0–100）" },
        { "RightTileGroupOffsetY", "右侧整列上下位置" },
        { "RightTileMouseClickThroughEnabled", "右侧窗口鼠标穿透" },
        { "GeniusProgrammerEasterEggEnabled", "你是天才程序员吗" },
        { "MetricTileExpandWidth", "展开面板宽度" },
        { "MetricTileExpandHeight", "展开面板高度" },
        { "SpecBoardWidth", "Spec Board 宽度" },
        { "SpecBoardHeight", "Spec Board 高度" },
        { "SpecBoardAutoHideSeconds", "Spec Board 自动收回秒数" },
        { "SpecBoardLeftDockTabCenterY", "Spec Board 标签中心 Y" },
        { "CodexTaskBoardLeftDockTabCenterY", "Codex 任务标签中心 Y" },
        { "NetworkMonitorLeftDockTabCenterY", "网络监控标签中心 Y" },
        { "GuardBoardLeftDockTabCenterY", "GUARD 标签中心 Y" },
        { "GuardBoardAutoHideSeconds", "GUARD 自动收回秒数" },
        { "CodexIqBoardLeftDockTabCenterY", "Codex IQ 标签中心 Y" },
        { "CodexIqBoardAutoHideSeconds", "Codex IQ 自动收回秒数" },
        { "ResetSpeedBoardLeftDockTabCenterY", "重置与速蹬标签中心 Y" },
        { "ResetSpeedBoardAutoHideSeconds", "重置与速蹬自动收回秒数" },
        { "SystemDayBoardLeftDockTabCenterY", "系统日记标签中心 Y" },
        { "SystemDayBoardAutoHideSeconds", "系统日记自动收回秒数" },
        { "LeftDockOutsideClickCollapseEnabled", "点击看板外部时收回" },
        { "SpecBoardAutoPopupEnabled", "发现新 Spec 时自动弹出" },
        { "SpecBoardAutoPopupSeconds", "新 Spec 弹窗停留秒数" },
        { "SpecBoardLedgerPath", "Spec Board 账本路径" },
        { "SpecBoardManagerWidth", "Spec 管理窗口宽度" },
        { "SpecBoardManagerHeight", "Spec 管理窗口高度" },
        { "SpecBoardManagerDangerZoneRequiresTypedConfirm", "删除源文件需要输入文件名确认" },
        { "OperationLeftOffset", "操作面板距左边" },
        { "OperationBottomOffset", "操作面板距下边" },
        { "ApplicationTransparencyPercent", "全局整体透明度" },
        { "MainWidgetTransparencyOverridePercent", "右侧磁贴透明度覆盖" },
        { "NetworkMonitorScaleOverridePercent", "网络监控缩放覆盖" },
        { "OperationScaleOverridePercent", "操作面板缩放覆盖" },
        { "SpecBoardScaleOverridePercent", "Spec Board 缩放覆盖" },
        { "CodexTaskBoardScaleOverridePercent", "Codex 任务看板缩放覆盖" },
        { "GuardBoardScaleOverridePercent", "GUARD 看板缩放覆盖" },
        { "CodexIqBoardScaleOverridePercent", "Codex IQ 看板缩放覆盖" },
        { "ResetSpeedBoardScaleOverridePercent", "重置与速蹬看板缩放覆盖" },
        { "SystemDayBoardScaleOverridePercent", "系统日记看板缩放覆盖" },
        { "NetworkMonitorTransparencyOverridePercent", "网络监控整体透明度覆盖" },
        { "OperationTransparencyOverridePercent", "操作面板整体透明度覆盖" },
        { "SpecBoardTransparencyOverridePercent", "Spec Board 整体透明度覆盖" },
        { "CodexTaskBoardTransparencyOverridePercent", "Codex 任务看板整体透明度覆盖" },
        { "GuardBoardTransparencyOverridePercent", "GUARD 看板整体透明度覆盖" },
        { "CodexIqBoardTransparencyOverridePercent", "Codex IQ 看板整体透明度覆盖" },
        { "ResetSpeedBoardTransparencyOverridePercent", "重置与速蹬看板整体透明度覆盖" },
        { "SystemDayBoardTransparencyOverridePercent", "系统日记看板整体透明度覆盖" },
        { "NightScheduleEnabled", "启用夜间时段" },
        { "NightScheduleStartMinutes", "夜间开始（自午夜分钟）" },
        { "NightScheduleEndMinutes", "夜间结束（自午夜分钟）" },
        { "NightDimLuminancePercent", "夜间亮度" },
        { "NightQuietHoursEnabled", "夜间勿扰" },
        { "AlertQuotaEnabled", "额度与阈值提醒" },
        { "AlertResetProtectionEnabled", "重置保护提醒" },
        { "AlertServiceHealthEnabled", "服务健康提醒" },
        { "AlertCodexTaskEnabled", "Codex 任务提醒" },
        { "HotkeyToggleAllWindows", "隐藏/显示全部挂件" },
        { "HotkeyToggleHoverOpacity", "切换悬停透明度" },
        { "HotkeyOpenSettings", "打开设置" },
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
        { "BurnInProtectionEnabled", "启用两级防烧屏" },
        { "BurnInLevelOneIdleSeconds", "一级进入秒数" },
        { "BurnInLevelTwoDelaySeconds", "二级追加秒数" },
        { "OperationRadialCoreAutoHideKeepAliveEnabled", "圆圈悬停保持显示" },
        { "OperationRadialIdleCollapseSeconds", "扇形盘自动收回秒数" },
        { "OperationRadialIdleResetOnInteractionEnabled", "操作后重置收回计时" },
        { "OperationRadialKeepOpenAfterLeafClickEnabled", "末端按钮后保持展开" },
        { "CodexRadarSoftwareMode", "Radar 数据族" },
        { "CodexRadarModelKey", "CODEX 模型" },
        { "RadarClockAutoSwitchModelEnabled", "过期自动切换模型" },
        { ClaudeSetupTokenCommandName, "Claude Code 用量令牌" },
        { DeepSeekApiKeyCommandName, "DeepSeek API Key" },
        { "CodexRadarRandomTestEnabled", "服务健康随机测试" },
        { "CodexRadarRandomTestAutoRefresh", "健康测试自动刷新" },
        { "CodexRadarRandomTestRefreshToken", "立即刷新健康测试" },
        { "OperationSettingsLogicExtensionEnabled", "设置扩展到操作逻辑" },
        { "OperationDoubleClickSpecialMenuEnabled", "双击打开特殊菜单" },
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
        { "MainWidgetTileLargeModeEnabled", "方块大窗口模式" },
        { "PowerThermalManualEnergySaverThresholdPercent", "手动节能阈值" },
        { "NetworkMonitorAdapterId", "网络适配器 ID" },
        { "NetworkStatusTestMode", "网络状态测试" },
        { "GfwProbeIntervalMinutes", "GFW 检测间隔" },
        { "GfwProbeEnabled", "启用 GFW 检测" },
        { "GfwProbeManualRefreshToken", "立即刷新 GFW 检测" },
        { "CloudEndpointTestSeed", "云服务测试种子" },
        { "CloudEndpointTargets", "检测目标" },
        { "FixedPingTargets", "Ping 目标" },
        { "CloudStatusRegionMask", "云服务地区掩码" },
        { "ConnectionCheckIntervalSeconds", "连接检测间隔" },
        { "ConnectionCheckManualRefreshToken", "立即刷新连接检测" },
        { "CleanIpBadgeTestMode", "出口身份测试模式" },
        { "ThermalTestMode", "温控测试模式" },
        { "AlertTestEnabled", "告警测试" },
        { "OperationButtonSize", "按钮大小" },
        { "OperationBackgroundTransparencyPercent", "操作面板透明度" }
    };

    private static readonly Dictionary<string, string> SettingHints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "StartupEnabled", "写入当前用户启动项。" },
        { "PerformanceMode", "控制采样、动画和后台刷新节奏。" },
        { "VisibilityMode", "五档：总是可见、全屏时不可见、最大化时不可见、遮挡时不可见、仅桌面可见；默认全屏时不可见。最大化档也包含全屏。" },
        { "CodexPetZOrderProtectionEnabled", "开启后，非桌面模式的小窗口始终排在 Codex 桌面宠物和 SeelenUI 浮层下方；默认开启。" },
        { "VisibilityOverlapIgnoresOperationPanelEnabled", "仅在“遮挡时不可见”生效；开启后左下角操作面板及其展开区域不会因为被其他应用窗口覆盖而隐藏。" },
        { "ForceShowForegroundFpsEnabled", "调试用，强制显示前台 FPS 信息。" },
        { "AiRequestProtectionAutoEnabled", "网络监控判定为 GFW 明确阻断时，阻断本程序发往 OpenAI、ChatGPT、Claude 和 Anthropic 的请求。" },
        { "AiRequestProtectionManualBlockEnabled", "手动启用后立即阻断本程序相关 AI 请求；也可在左下角程序设置按钮单击打开的特殊设置中切换。" },
        { "AiChinaEgressGuardEnabled", "出口 IP 明确位于中国大陆时，自动阻断本程序发往 Anthropic、OpenAI 的请求并弹出全屏警告；出口未知或结果过期时仅静默阻断，确认在日本等境外时不动作。判据为出口 IP 国别，采用 fail-closed。" },
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
        { "BurnInProtectionEnabled", "开启后，无操作先进入一级低亮保护，再进入二级反色/文字抑制保护；点击会退出，悬停只临时恢复对应区域。" },
        { "BurnInLevelOneIdleSeconds", "无操作达到该时长后进入一级；范围 1-300 秒，默认 10 秒。" },
        { "BurnInLevelTwoDelaySeconds", "进入一级后再经过该时长进入二级；范围 1-600 秒，默认 30 秒。" },
        { "OperationRadialCoreAutoHideKeepAliveEnabled", "鼠标停在左下角扇形速控盘核心圆圈上时，暂停并重置自动隐藏计时器，让所有隐藏透明度窗口保持显示。" },
        { "OperationRadialIdleCollapseSeconds", "范围 1-60 秒；设为 0 表示永不自动收回扇形速控盘。" },
        { "OperationRadialIdleResetOnInteractionEnabled", "开启后鼠标移动、按下或展开新分支会重新开始扇形速控盘自动收回计时。" },
        { "OperationRadialKeepOpenAfterLeafClickEnabled", "开启后点击扇形速控盘末端按钮不会自动收起菜单；关闭后恢复点击末端按钮即收起。" },
        { "OperationSettingsLogicExtensionEnabled", "开启后在扇形速控盘“设置”分支中增加常用逻辑和全部开关目录；关闭时保持原来的 3 项设置菜单。" },
        { "OperationDoubleClickSpecialMenuEnabled", "开启后双击左下角主操作按钮会打开 Spec 管理、Codex 任务和睡眠防护特殊菜单；关闭时双击直接开关隐藏模式。" },
        { "CodexRadarSoftwareMode", "控制后台额度数据族自动按运行态选择，或固定使用 CODEX/CLAUDE；CLAUDE 只读取官方 Claude Code 用量。" },
        { "CodexRadarModelKey", "仅用于 CODEX 数据族的 CodexRadar 模型选择；CLAUDE 额度不区分模型。" },
        { "RadarClockAutoSwitchModelEnabled", "Codex Radar 跨过完整周期仍没有当前模型 IQ 更新时，自动切到站点当天最近刷新 IQ 的模型。" },
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
        { "CodexRadarRandomTestEnabled", "仅用于联调 Network 面板中的 Radar 服务健康状态，日常保持关闭。" },
        { "CodexRadarRandomTestAutoRefresh", "开启后自动轮换服务健康测试值，日常保持关闭。" },
        { "CodexRadarRandomTestRefreshToken", "点击后立即换一组服务健康测试值。" },
        { ClaudeSetupTokenCommandName, "Claude 桌面版不会主动上报用量，需要生成一次性长效令牌并粘贴进来；未配置时两个额度环会显示满环红色。" },
        { DeepSeekApiKeyCommandName, "使用 DeepSeek 官方余额接口读取剩余额度；Key 以当前用户 DPAPI 加密保存，24 小时消耗和预计可用时长由本地余额历史计算。" },
        { "MainWidgetTileLargeModeEnabled", "只影响右缘方块列：关闭时磁贴为 60×60 像素；开启后磁贴与悬停展开面板各放大一倍，适合高分屏或视力需要。" },
        { "MetricTileExpandWidth", "右缘指标磁贴悬停展开后的逻辑像素宽度；大窗口模式会在运行时按比例放大。" },
        { "MetricTileExpandHeight", "右缘指标磁贴悬停展开后的逻辑像素高度；大窗口模式会在运行时按比例放大。" },
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
        { "CloudEndpointTargets", "勾选的内置服务会保留原官方状态检测；也可新增显示名称和 IP/主机。取消勾选后不发起该项检测。" },
        { "FixedPingTargets", "PathPing 下方固定显示的 Ping 站点；默认 Google、百度、Yahoo，可新增、删除或取消检测。" },
        { "CloudStatusRegionMask", "云服务状态的地区过滤位掩码，日常保持默认。" },
        { "CleanIpBadgeTestMode", "仅用于测试显示效果，日常保持关闭。" },
        { "AlertTestEnabled", "仅用于测试显示效果，日常保持关闭。" },
        { "PowerResumeRestartEnabled", "系统唤醒后自动重启 SeelenUI 和本程序。" },
        { "SeelenDockForegroundPulseEnabled", "需要时短暂拉前 Seelen Dock，避免被系统窗口压住。" },
        { "FallbackDisconnectedDisplaysEnabled", "指定显示器未连接时，将对应可见面回退到当前主显示器。" },
        { "MainDisplayDeviceName", "留空使用当前主显示器；右侧十一枚磁贴及展开面板按该显示器工作区定位。" },
        { "OperationDisplayDeviceName", "留空使用当前主显示器；操作面板偏移量按目标显示器左下角计算。" },
        { "ResolutionCompatibilityModeEnabled", "默认关闭。开启后按 2880x1800 参考布局投影 Operation、七个 Dock 与十一枚磁贴/展开面板。" },
        { "ResolutionCompatibilityScalePercent", "运行时输出比例，低于 100% 压缩，高于 100% 放大；不会改写保存的真实布局坐标。" },
        { GlobalLayoutEditCommandName, "打开全屏布局编辑遮罩，显示 Operation、固定七个左侧停靠按钮与固定十一枚右侧磁贴；Enter 保存，Esc 放弃。自动排列开启时拖动列成员会整体上下移动。" },
        { "LeftDockAutoArrangeEnabled", "开启后按下方顺序和分布间距自动排列固定七个左侧梯形按钮；关闭后保留全局编辑器写入的单项位置。" },
        { "LeftDockButtonOrder", "用上下箭头调整 Network、Spec、Codex Task、GUARD、Codex IQ、重置/速蹬与系统日记七个固定按钮的排列顺序。" },
        { "LeftDockButtonGapPixels", "可拖动滑块或直接输入 0–100；0 让按钮紧挨，100 让整列从工作区顶部到底部均匀分布，中间值按比例展开。" },
        { "LeftDockGroupOffsetY", "把全部左侧按钮视为一个整体，相对屏幕垂直居中位置上下移动；0 为居中，负值向上。" },
        { "RightTileAutoArrangeEnabled", "开启后按下方顺序和分布间距自动排列固定十一枚右缘磁贴；关闭后保留全局编辑器写入的单项位置。" },
        { "RightTileButtonOrder", "用上下箭头调整 CPU、内存、磁盘、网络、GPU、NPU、功耗、GUARD、Codex、Claude 与 DeepSeek 十一枚磁贴的顺序。" },
        { "RightTileButtonGapPixels", "可拖动滑块或直接输入 0–100；0 让磁贴紧挨，100 让整列从工作区顶部到底部均匀分布，中间值按比例展开。" },
        { "RightTileGroupOffsetY", "把全部右侧方块窗口视为一个整体，相对屏幕垂直居中位置上下移动；0 为居中，正值向下。" },
        { "RightTileMouseClickThroughEnabled", "开启后十一枚右侧小窗和悬停展开面板都不拦截鼠标点击；悬停展开仍由光标位置轮询工作。" },
        { "GeniusProgrammerEasterEggEnabled", "开启额度彩蛋：Codex 或 Claude 归零时显示陨落提示，额度恢复后第一次展开显示复活提示。" },
        { "SpecBoardWidth", "逻辑像素，范围 320-700。" },
        { "SpecBoardHeight", "逻辑像素，范围 240-800。" },
        { "SpecBoardAutoHideSeconds", "范围 0-600 秒；0 表示不自动收回。鼠标停在看板内时暂停，移出后重新计时。" },
        { "SpecBoardLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。无效负值保存时恢复自动。" },
        { "CodexTaskBoardLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。" },
        { "NetworkMonitorLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。" },
        { "GuardBoardLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。" },
        { "GuardBoardAutoHideSeconds", "范围 0-600 秒；0 表示展开后不自动收回。" },
        { "CodexIqBoardLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。" },
        { "CodexIqBoardAutoHideSeconds", "范围 0-600 秒；0 表示展开后不自动收回。" },
        { "ResetSpeedBoardLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。" },
        { "ResetSpeedBoardAutoHideSeconds", "范围 0-600 秒；0 表示展开后不自动收回。" },
        { "SystemDayBoardLeftDockTabCenterY", "自动模式按七看板队列计算位置；手动模式填写屏幕坐标 Y。" },
        { "SystemDayBoardAutoHideSeconds", "范围 0-600 秒；0 表示展开后不自动收回。" },
        { "LeftDockOutsideClickCollapseEnabled", "开启后，停靠展开的 Spec Board 或 Codex Task 在点击桌面、其他窗口或另一块看板时收回；点击自身、停靠梯形或 Spec 管理窗口不会误收回。" },
        { "SpecBoardAutoPopupEnabled", "开启后监测新建的 Spec；发现新项时自动弹出小看板并高亮。" },
        { "SpecBoardAutoPopupSeconds", "范围 1-120 秒；自动弹窗在鼠标未停留时的显示时长，鼠标移入会暂停并重置倒计时。" },
        { "SpecBoardLedgerPath", "跨项目 SPEC_BOARD.jsonl 的只读路径；PROJECTS.json 固定从同目录读取。" },
        { "SpecBoardManagerWidth", "管理窗口宽度，范围 560-1000。" },
        { "SpecBoardManagerHeight", "管理窗口高度，范围 400-900。" },
        { "SpecBoardManagerDangerZoneRequiresTypedConfirm", "推荐保持开启；删除账本条目并删除源文件前必须输入完全一致的文件名。" },
        { "OperationLeftOffset", "逻辑像素，距目标显示器左边缘。" },
        { "OperationBottomOffset", "逻辑像素，距目标显示器下边缘。" },
        { "ApplicationTransparencyPercent", "全部可见面的默认整体透明度；设置了每个可见面覆盖时以覆盖值为准。" },
        { "MainWidgetTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 覆盖右侧十一枚磁贴及展开面板。" },
        { "NetworkMonitorScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 覆盖 Network 停靠板及其标签。" },
        { "OperationScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 覆盖操作面板及其启动器子窗。" },
        { "SpecBoardScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 覆盖 Spec 看板及其停靠标签。" },
        { "CodexTaskBoardScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 覆盖 Codex 任务看板及其停靠标签。" },
        { "GuardBoardScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 只覆盖 GUARD 看板及其停靠标签。" },
        { "CodexIqBoardScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 只覆盖 Codex IQ 看板及其停靠标签。" },
        { "ResetSpeedBoardScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 只覆盖重置与速蹬看板及其停靠标签。" },
        { "SystemDayBoardScaleOverridePercent", "−1 = 跟随全局分辨率兼容缩放；40–200 只覆盖系统日记看板及其停靠标签。" },
        { "NetworkMonitorTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 覆盖 Network 停靠板及其标签。" },
        { "OperationTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 覆盖操作面板及其启动器子窗。" },
        { "SpecBoardTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 覆盖 Spec 看板及其停靠标签。" },
        { "CodexTaskBoardTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 覆盖 Codex 任务看板及其停靠标签。" },
        { "GuardBoardTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 只覆盖 GUARD 看板及其停靠标签。" },
        { "CodexIqBoardTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 只覆盖 Codex IQ 看板及其停靠标签。" },
        { "ResetSpeedBoardTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 只覆盖重置与速蹬看板及其停靠标签。" },
        { "SystemDayBoardTransparencyOverridePercent", "−1 = 跟随全局整体透明度；0–90 只覆盖系统日记看板及其停靠标签。" },
        { "NightScheduleEnabled", "按本地时间在固定时段降低全部挂件亮度。" },
        { "NightScheduleStartMinutes", "0–1439；例如 1380 = 23:00，可与结束时间组成跨午夜时段。" },
        { "NightScheduleEndMinutes", "0–1439；例如 420 = 07:00，结束分钟本身不属于夜间。" },
        { "NightDimLuminancePercent", "10–100；60 表示夜间保留 60% 亮度，100 不降亮。" },
        { "NightQuietHoursEnabled", "夜间只静默用户可见提醒，数据采集和状态机继续运行，退出后不补发。" },
        { "AlertQuotaEnabled", "控制额度、额度计划及 AlertPercent 阈值的颜色、图标、文本和系统通知；不停止采集。" },
        { "AlertResetProtectionEnabled", "控制 RSS/到期重置保护的提示和强调；保护状态机仍继续工作。" },
        { "AlertServiceHealthEnabled", "控制 Radar、OpenAI、Claude 与 DeepSeek 服务健康提示；探测仍继续。" },
        { "AlertCodexTaskEnabled", "控制 Codex 任务待处理数量、强调色和提醒文本；任务监控仍继续。" },
        { "HotkeyToggleAllWindows", "格式如 Ctrl+Alt+H；至少包含一个修饰键，留空表示不绑定。" },
        { "HotkeyToggleHoverOpacity", "切换悬停透明度动作；格式如 Ctrl+Shift+O。" },
        { "HotkeyOpenSettings", "从任意应用打开设置窗口；格式如 Ctrl+Alt+S。" },
        { "OperationBackgroundTransparencyPercent", "影响左下角操作面板背景透明度。" },
        { "OperationPrimaryPanelMode", "自动模式会按 SeelenUI 运行态在 Windows 按钮和内存饼图之间切换；隐藏会让小按钮移到最左侧。" },
        { "OperationButtonSize", "左下角操作面板按钮的逻辑像素大小。" },
        { "OperationWindowsButtonEnabled", "SeelenUI 未运行时会自动隐藏；关闭后始终不显示左侧 Windows 按钮。" },
        { "OperationMemoryPieEnabled", "左侧 Windows 按钮隐藏时显示物理、虚拟和前台程序内存占用饼图。" },
        { "WinDRecoveryPulseEnabled", "按 Win+D 后延迟拉前本程序和 SeelenUI。" }
    };

    // ═════════════════════════════════════════════════════════════════════
    // Nested Classes — Custom Controls
    // ═════════════════════════════════════════════════════════════════════

    // ── ColumnOrderEditorControl ─────────────────────────────────────────
    // A compact "rail composer" for the two edge columns. Stable ids are kept in settings while
    // the visible label and accent mirror the actual surface, so renaming UI copy cannot corrupt a
    // saved order. Up/down buttons are used instead of drag/drop because the settings page itself is
    // scrollable and pointer capture would otherwise fight vertical page scrolling.
    private sealed class ColumnOrderEditorControl : Panel
    {
        private const int RowGap = 6;

        private readonly bool leftColumn;
        private readonly Font rowFont;
        private readonly string[] allowedIds;
        private readonly int rowHeight;
        private readonly Dictionary<string, ColumnOrderRowState> rowStates;
        private string[] order;

        public event EventHandler ValueChanged;

        public ColumnOrderEditorControl(bool leftColumn, Font rowFont)
        {
            this.leftColumn = leftColumn;
            this.rowFont = rowFont;
            this.allowedIds = leftColumn
                ? new string[] { "Network", "SpecBoard", "CodexTask", "Guard", "CodexIq", "ResetSpeed", "SystemDay" }
                : (string[])WidgetSettings.MetricTileIds.Clone();
            this.rowStates = new Dictionary<string, ColumnOrderRowState>(StringComparer.OrdinalIgnoreCase);
            this.order = (string[])this.allowedIds.Clone();
            this.rowHeight = Math.Max(42, GetSingleLineHeight(rowFont, 4) + 12);
            this.BackColor = Color.Transparent;
            this.Width = 560;
            this.Height = this.allowedIds.Length * this.rowHeight + Math.Max(0, this.allowedIds.Length - 1) * RowGap;
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BuildRows();
        }

        public string[] GetOrder()
        {
            return (string[])this.order.Clone();
        }

        public void SetOrderSilent(string[] value)
        {
            this.order = NormalizeOrder(value);
            LayoutRows();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutRows();
        }

        private string[] NormalizeOrder(string[] value)
        {
            List<string> normalized = new List<string>(this.allowedIds.Length);
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    string canonical = FindCanonicalId(value[i]);
                    if (canonical.Length > 0 && !ContainsId(normalized, canonical))
                    {
                        normalized.Add(canonical);
                    }
                }
            }

            for (int i = 0; i < this.allowedIds.Length; i++)
            {
                if (!ContainsId(normalized, this.allowedIds[i]))
                {
                    normalized.Add(this.allowedIds[i]);
                }
            }

            return normalized.ToArray();
        }

        private string FindCanonicalId(string value)
        {
            for (int i = 0; i < this.allowedIds.Length; i++)
            {
                if (string.Equals(this.allowedIds[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return this.allowedIds[i];
                }
            }

            return string.Empty;
        }

        private static bool ContainsId(List<string> values, string id)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void BuildRows()
        {
            this.SuspendLayout();
            try
            {
                for (int i = 0; i < this.allowedIds.Length; i++)
                {
                    Panel row = BuildRow(this.allowedIds[i]);
                    this.Controls.Add(row);
                }
            }
            finally
            {
                this.ResumeLayout(false);
            }

            LayoutRows();
        }

        private Panel BuildRow(string id)
        {
            Panel row = new Panel();
            row.Height = this.rowHeight;
            row.BackColor = ControlBg;
            row.AccessibleName = GetItemLabel(id) + " 排序项";

            Label dot = new Label();
            dot.AutoSize = false;
            dot.Text = "●";
            dot.TextAlign = ContentAlignment.MiddleCenter;
            dot.Font = this.rowFont;
            dot.ForeColor = GetItemAccent(id);
            dot.BackColor = Color.Transparent;

            Label sequence = new Label();
            sequence.AutoSize = false;
            sequence.Text = string.Empty;
            sequence.TextAlign = ContentAlignment.MiddleCenter;
            sequence.Font = this.rowFont;
            sequence.ForeColor = TextTertiary;
            sequence.BackColor = Color.Transparent;

            Label name = new Label();
            name.AutoSize = false;
            name.Text = GetItemLabel(id);
            name.TextAlign = ContentAlignment.MiddleLeft;
            name.Font = this.rowFont;
            name.ForeColor = TextPrimary;
            name.BackColor = Color.Transparent;

            Button up = BuildMoveButton("↑", "上移 " + name.Text, id, -1);
            Button down = BuildMoveButton("↓", "下移 " + name.Text, id, 1);

            row.Controls.Add(dot);
            row.Controls.Add(sequence);
            row.Controls.Add(name);
            row.Controls.Add(up);
            row.Controls.Add(down);
            row.Resize += delegate { LayoutRow(row, dot, sequence, name, up, down); };
            ColumnOrderRowState state = new ColumnOrderRowState(row, sequence, up, down);
            row.Tag = state;
            this.rowStates.Add(id, state);
            LayoutRow(row, dot, sequence, name, up, down);
            return row;
        }

        private Button BuildMoveButton(string text, string accessibleName, string id, int delta)
        {
            Button button = new Button();
            button.Text = text;
            button.AccessibleName = accessibleName;
            button.Font = this.rowFont;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = DividerColor;
            button.BackColor = CardRest;
            button.ForeColor = TextSecondary;
            button.Cursor = Cursors.Hand;
            button.TabStop = true;
            button.Click += delegate { MoveItem(id, delta); };
            return button;
        }

        private void MoveItem(string id, int delta)
        {
            int index = Array.FindIndex(
                this.order,
                delegate(string candidate) { return string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase); });
            int next = index + delta;
            if (index < 0 || index >= this.order.Length || next < 0 || next >= this.order.Length)
            {
                return;
            }

            string value = this.order[index];
            this.order[index] = this.order[next];
            this.order[next] = value;
            // Keep row/button instances alive while changing their bounds. This preserves keyboard
            // focus for repeated Space/Enter moves and avoids flicker in the ten-item right rail.
            LayoutRows();
            if (this.ValueChanged != null)
            {
                this.ValueChanged(this, EventArgs.Empty);
            }
        }

        private void LayoutRows()
        {
            int width = Math.Max(160, this.ClientSize.Width);
            int top = 0;
            for (int i = 0; i < this.order.Length; i++)
            {
                ColumnOrderRowState state;
                if (!this.rowStates.TryGetValue(this.order[i], out state))
                {
                    continue;
                }

                state.Sequence.Text = (i + 1).ToString("00", CultureInfo.InvariantCulture);
                state.Up.Enabled = i > 0;
                state.Down.Enabled = i + 1 < this.order.Length;
                state.Row.SetBounds(0, top, width, this.rowHeight);
                top += this.rowHeight + RowGap;
            }
        }

        internal void PerformMoveForSelfTest(string id, int delta)
        {
            MoveItem(id, delta);
        }

        internal void VerifyRowsFitForSelfTest()
        {
            LayoutRows();
            for (int i = 0; i < this.order.Length; i++)
            {
                ColumnOrderRowState state;
                if (!this.rowStates.TryGetValue(this.order[i], out state) ||
                    state.Row.Left < 0 || state.Row.Top < 0 ||
                    state.Row.Right > this.ClientSize.Width || state.Row.Bottom > this.ClientSize.Height)
                {
                    throw new InvalidOperationException("WinUI column order row is clipped: " + this.order[i]);
                }

                for (int childIndex = 0; childIndex < state.Row.Controls.Count; childIndex++)
                {
                    Control child = state.Row.Controls[childIndex];
                    if (child.Left < 0 || child.Top < 0 ||
                        child.Right > state.Row.ClientSize.Width || child.Bottom > state.Row.ClientSize.Height)
                    {
                        throw new InvalidOperationException("WinUI column order child is clipped: " + this.order[i]);
                    }
                }
            }
        }

        private void LayoutRow(Panel row, Label dot, Label sequence, Label name, Button up, Button down)
        {
            int padding = 10;
            int buttonSize = Math.Max(30, row.Height - 10);
            int buttonGap = 6;
            int downLeft = Math.Max(padding, row.ClientSize.Width - padding - buttonSize);
            int upLeft = Math.Max(padding, downLeft - buttonGap - buttonSize);
            dot.SetBounds(padding, 0, 18, row.Height);
            sequence.SetBounds(dot.Right + 2, 0, 34, row.Height);
            name.SetBounds(sequence.Right + 8, 0, Math.Max(20, upLeft - sequence.Right - 16), row.Height);
            up.SetBounds(upLeft, (row.Height - buttonSize) / 2, buttonSize, buttonSize);
            down.SetBounds(downLeft, (row.Height - buttonSize) / 2, buttonSize, buttonSize);
        }

        private string GetItemLabel(string id)
        {
            if (this.leftColumn)
            {
                if (string.Equals(id, "Network", StringComparison.Ordinal)) return "Network 网络面板";
                if (string.Equals(id, "SpecBoard", StringComparison.Ordinal)) return "Spec Board";
                if (string.Equals(id, "CodexTask", StringComparison.Ordinal)) return "Codex Task";
                if (string.Equals(id, "Guard", StringComparison.Ordinal)) return "GUARD";
                if (string.Equals(id, "CodexIq", StringComparison.Ordinal)) return "Codex IQ";
                if (string.Equals(id, "ResetSpeed", StringComparison.Ordinal)) return "重置与速蹬";
                return "系统日记";
            }

            int index = WidgetSettings.IndexOfMetricTile(id);
            return index >= 0 && index < MetricTileModel.AllOrder.Length
                ? MetricTileModel.GetLabel(MetricTileModel.AllOrder[index])
                : id;
        }

        private Color GetItemAccent(string id)
        {
            if (this.leftColumn)
            {
                if (string.Equals(id, "Network", StringComparison.Ordinal)) return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Network);
                if (string.Equals(id, "SpecBoard", StringComparison.Ordinal)) return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SpecBoard);
                if (string.Equals(id, "CodexTask", StringComparison.Ordinal)) return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexTask);
                if (string.Equals(id, "Guard", StringComparison.Ordinal)) return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Guard);
                if (string.Equals(id, "CodexIq", StringComparison.Ordinal)) return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexIq);
                if (string.Equals(id, "ResetSpeed", StringComparison.Ordinal)) return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.ResetSpeed);
                return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay);
            }

            int index = WidgetSettings.IndexOfMetricTile(id);
            return index >= 0 && index < MetricTileModel.AllOrder.Length
                ? MetricTileModel.GetAccent(MetricTileModel.AllOrder[index])
                : AccentClr;
        }

        private sealed class ColumnOrderRowState
        {
            public ColumnOrderRowState(Panel row, Label sequence, Button up, Button down)
            {
                this.Row = row;
                this.Sequence = sequence;
                this.Up = up;
                this.Down = down;
            }

            public Panel Row { get; private set; }
            public Label Sequence { get; private set; }
            public Button Up { get; private set; }
            public Button Down { get; private set; }
        }
    }

    // ── PercentSliderControl ─────────────────────────────────────────────
    private sealed class PercentSliderControl : Panel
    {
        private readonly TrackBar trackBar;
        private readonly Label valueLabel;
        private readonly NumericUpDown numericInput;
        private bool suppressChanged;
        private string suffix = "%";
        private bool showPositiveSign;
        private bool useNumericInput;

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

            this.numericInput = new NumericUpDown();
            this.numericInput.AutoSize = false;
            this.numericInput.Width = 82;
            this.numericInput.Height = Math.Max(30, TextRenderer.MeasureText("100", labelFont).Height + 12);
            this.numericInput.DecimalPlaces = 0;
            this.numericInput.Minimum = 0;
            this.numericInput.Maximum = 100;
            this.numericInput.Increment = 1;
            this.numericInput.TextAlign = HorizontalAlignment.Right;
            this.numericInput.BorderStyle = BorderStyle.FixedSingle;
            this.numericInput.BackColor = ControlBg;
            this.numericInput.ForeColor = TextPrimary;
            this.numericInput.Font = labelFont;
            this.numericInput.Visible = false;
            this.numericInput.ValueChanged += OnNumericInputValueChanged;

            this.Controls.Add(this.trackBar);
            this.Controls.Add(this.numericInput);
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
                this.numericInput.Minimum = value;
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
                this.numericInput.Maximum = value;
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

        public string Suffix
        {
            get { return this.suffix; }
            set
            {
                this.suffix = value ?? string.Empty;
                UpdateValueLabel();
            }
        }

        public bool ShowPositiveSign
        {
            get { return this.showPositiveSign; }
            set
            {
                this.showPositiveSign = value;
                UpdateValueLabel();
            }
        }

        public bool UseNumericInput
        {
            get { return this.useNumericInput; }
            set
            {
                this.useNumericInput = value;
                this.numericInput.Visible = value;
                this.valueLabel.TextAlign = value
                    ? ContentAlignment.MiddleLeft
                    : ContentAlignment.MiddleRight;
                UpdateAccessibility();
                UpdateValueLabel();
                OnResize(EventArgs.Empty);
            }
        }

        public string AccessibleLabel
        {
            get { return this.trackBar.AccessibleName ?? string.Empty; }
            set
            {
                string label = value ?? string.Empty;
                this.trackBar.AccessibleName = label;
                UpdateAccessibility();
            }
        }

        public void SetValueSilent(int value)
        {
            int next = Math.Max(this.trackBar.Minimum, Math.Min(this.trackBar.Maximum, value));
            this.suppressChanged = true;
            try
            {
                this.trackBar.Value = next;
                this.numericInput.Value = next;
                UpdateValueLabel();
            }
            finally
            {
                this.suppressChanged = false;
            }
        }

        public void SetNumericValueForSelfTest(int value)
        {
            if (!this.useNumericInput)
            {
                throw new InvalidOperationException("Numeric input is not enabled for this slider.");
            }

            int next = Math.Max(this.trackBar.Minimum, Math.Min(this.trackBar.Maximum, value));
            this.numericInput.Value = next;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int gap = 8;
            int top = Math.Max(0, (this.Height - this.trackBar.Height) / 2);
            if (this.useNumericInput)
            {
                int inputWidth = 82;
                int suffixWidth = 28;
                int trackWidth = Math.Max(80, this.Width - inputWidth - suffixWidth - gap * 2);
                int inputTop = Math.Max(0, (this.Height - this.numericInput.Height) / 2);
                this.trackBar.SetBounds(0, top, trackWidth, this.trackBar.Height);
                this.numericInput.SetBounds(trackWidth + gap, inputTop, inputWidth, this.numericInput.Height);
                this.valueLabel.SetBounds(trackWidth + gap + inputWidth + gap, 0, suffixWidth, this.Height);
            }
            else
            {
                int labelWidth = 90;
                int trackWidth = Math.Max(80, this.Width - labelWidth - gap);
                this.trackBar.SetBounds(0, top, trackWidth, this.trackBar.Height);
                this.numericInput.SetBounds(trackWidth + gap, 0, 0, 0);
                this.valueLabel.SetBounds(trackWidth + gap, 0, labelWidth, this.Height);
            }
        }

        private void OnTrackBarValueChanged(object sender, EventArgs e)
        {
            if (this.numericInput.Value != this.trackBar.Value)
            {
                bool previousSuppression = this.suppressChanged;
                this.suppressChanged = true;
                try
                {
                    this.numericInput.Value = this.trackBar.Value;
                }
                finally
                {
                    this.suppressChanged = previousSuppression;
                }
            }

            UpdateValueLabel();
            if (!this.suppressChanged && this.ValueChanged != null)
            {
                this.ValueChanged(this, EventArgs.Empty);
            }
        }

        private void OnNumericInputValueChanged(object sender, EventArgs e)
        {
            int value = Decimal.ToInt32(this.numericInput.Value);
            if (this.trackBar.Value != value)
            {
                bool previousSuppression = this.suppressChanged;
                this.suppressChanged = true;
                try
                {
                    this.trackBar.Value = value;
                }
                finally
                {
                    this.suppressChanged = previousSuppression;
                }
            }

            UpdateValueLabel();
            if (!this.suppressChanged && this.ValueChanged != null)
            {
                this.ValueChanged(this, EventArgs.Empty);
            }
        }

        private void UpdateAccessibility()
        {
            string label = this.trackBar.AccessibleName ?? string.Empty;
            this.trackBar.AccessibleDescription = label.Length == 0
                ? string.Empty
                : label + (this.useNumericInput
                    ? "，可拖动滑块、使用方向键，或在数字框中直接输入。"
                    : "，使用方向键微调，Page Up 或 Page Down 大步调整。");
            this.numericInput.AccessibleName = label.Length == 0 ? string.Empty : label + " 数字输入";
            this.valueLabel.AccessibleName = label.Length == 0
                ? string.Empty
                : label + (this.useNumericInput ? " 单位" : " 当前值");
        }

        private void UpdateValueLabel()
        {
            int value = this.trackBar.Value;
            string prefix = this.showPositiveSign && value > 0 ? "+" : string.Empty;
            this.valueLabel.Text = this.useNumericInput
                ? this.suffix
                : prefix + value.ToString(CultureInfo.InvariantCulture) + this.suffix;
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
                    if (!row.Visible)
                    {
                        // Search filtering must remove hidden rows from geometry as well as paint;
                        // otherwise the tall order composers leave hundreds of blank pixels behind.
                        row.ShowTopDivider = false;
                        row.SetBounds(0, 0, 0, 0);
                        continue;
                    }

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

        // Column-order controls are inherently wide and wrap on their own. This skips the
        // width-threshold heuristic and always stacks the control below the text for that row.
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

    private sealed class LeftDockTabCenterEditorState
    {
        private readonly ComboBox mode;
        private readonly NumericUpDown position;

        public LeftDockTabCenterEditorState(ComboBox mode, NumericUpDown position)
        {
            this.mode = mode;
            this.position = position;
        }

        public int Value
        {
            get
            {
                return this.mode.SelectedIndex == 0
                    ? WidgetSettings.AutoLeftDockTabCenterY
                    : Convert.ToInt32(this.position.Value, CultureInfo.InvariantCulture);
            }
        }

        public void SetValue(int value)
        {
            bool automatic = value == WidgetSettings.AutoLeftDockTabCenterY || value < 0;
            decimal next = Math.Max(0, value);
            if (next > this.position.Maximum)
            {
                next = this.position.Maximum;
            }

            this.position.Value = next;
            this.mode.SelectedIndex = automatic ? 0 : 1;
            SyncEnabledState();
        }

        public void SyncEnabledState()
        {
            this.position.Enabled = this.mode.SelectedIndex == 1;
        }
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
