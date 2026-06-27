using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class Win11SettingsForm : Form, IMessageFilter, ISettingsWindow
{
    private const int PreviewDebounceMs = 75;
    private const int WmMouseWheel = 0x020A;
    private const int EmSetCueBanner = 0x1501;
    private const int NavWidth = 409;
    private const int NavItemHeight = 60;

    private static readonly Color MicaBase = DesignTokens.NeonGeekTheme.WindowBase;
    private static readonly Color MicaLayer = DesignTokens.NeonGeekTheme.WindowBase;
    private static readonly Color CardRest = DesignTokens.NeonGeekTheme.CardGlassRest;
    private static readonly Color CardHover = DesignTokens.NeonGeekTheme.CardGlassHover;
    private static readonly Color StrokeColor = DesignTokens.NeonGeekTheme.DividerLines;
    private static readonly Color DividerColor = DesignTokens.NeonGeekTheme.DividerLines;
    private static readonly Color ControlBg = DesignTokens.NeonGeekTheme.InputBackground;
    private static readonly Color ControlBorder = DesignTokens.NeonGeekTheme.DividerLines;
    private static readonly Color TextPrimary = DesignTokens.NeonGeekTheme.TextPrimary;
    private static readonly Color TextSecondary = DesignTokens.NeonGeekTheme.TextSecondary;
    private static readonly Color TextTertiary = DesignTokens.NeonGeekTheme.TextMuted;
    private static readonly Color AccentClr = DesignTokens.NeonGeekTheme.CyberCyan;
    private static readonly Color AccentHover = DesignTokens.NeonGeekTheme.ElectricPurple;
    private static readonly Color AccentPressed = DesignTokens.NeonGeekTheme.CyberCyan;
    private static readonly Color ErrorClr = DesignTokens.SettingsTheme.ErrorText;

    private readonly WidgetForm owner;
    private readonly Timer previewTimer;
    private readonly Timer statusTimer;
    private readonly Dictionary<string, SettingEditor> editors = new Dictionary<string, SettingEditor>(StringComparer.Ordinal);
    private readonly List<CategoryPage> pages = new List<CategoryPage>();
    private WidgetSettings baseline;
    private FlowLayoutPanel navigationPanel;
    private Panel contentHost;
    private TextBox searchBox;
    private Label statusLabel;
    private int selectedPageIndex;
    private bool initializing;
    private bool saved;
    private bool messageFilterRegistered;
    private static Font iconFontCache;

    public bool OwnerFormClosing { get; set; }

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
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = false;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.MinimumSize = GetMinimumWindowSizeForScreen();
        this.ClientSize = FitClientSizeToScreen(new Size(1888, 1312));
        this.Font = DesignTokens.CreateUIFont(10.0f);
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
        if (!this.messageFilterRegistered)
        {
            Application.AddMessageFilter(this);
            this.messageFilterRegistered = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        this.previewTimer.Stop();
        if (!this.saved && !this.OwnerFormClosing && this.owner != null)
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
        this.statusTimer.Tick -= OnStatusTimerTick;
        this.statusTimer.Dispose();
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

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;
        if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
        {
            Point screenPoint = new Point(m.LParam.ToInt32());
            Point clientPoint = this.PointToClient(screenPoint);
            if (clientPoint.Y <= 60 && clientPoint.X < this.Width - 60)
            {
                m.Result = (IntPtr)HTCAPTION;
            }
        }
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
        closeBtn.Font = DesignTokens.CreateUIFont(14.0f, FontStyle.Regular);
        closeBtn.ForeColor = TextSecondary;
        closeBtn.BackColor = MicaBase;
        closeBtn.Cursor = Cursors.Hand;
        closeBtn.AutoSize = false;
        closeBtn.Size = new Size(73, 51);
        closeBtn.TextAlign = ContentAlignment.MiddleCenter;
        closeBtn.Location = new Point(this.ClientSize.Width - 73, 0);
        closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeBtn.Click += delegate { this.Close(); };
        closeBtn.MouseEnter += delegate { closeBtn.BackColor = DesignTokens.NeonGeekTheme.ElectricPurple; closeBtn.ForeColor = Color.White; };
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
        title.Font = DesignTokens.CreateUIFont(22.0f, FontStyle.Bold);
        title.ForeColor = TextPrimary;
        title.BackColor = MicaBase;
        title.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom;
        title.TextAlign = ContentAlignment.MiddleLeft;

        Label subtitle = new Label();
        subtitle.Text = "按 Windows 11 设置结构重建，提供全局跨组件配置支持";
        subtitle.AutoSize = true;
        subtitle.Margin = new Padding(0, 10, 0, 50); // 10px below title, 50px below subtitle
        subtitle.Font = DesignTokens.CreateUIFont(9.5f);
        subtitle.ForeColor = TextTertiary;
        subtitle.BackColor = MicaBase;

        this.searchBox = new TextBox();
        this.searchBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        this.searchBox.Margin = new Padding(0, 10, 0, 0);
        this.searchBox.Height = 54;
        this.searchBox.BackColor = MicaLayer;
        this.searchBox.ForeColor = TextSecondary;
        this.searchBox.BorderStyle = BorderStyle.FixedSingle;
        this.searchBox.Font = DesignTokens.CreateUIFont(10.0f);
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
        body.Dock = DockStyle.Fill;
        body.BackColor = MicaBase;
        body.Padding = new Padding(38, 6, 38, 0);
        body.ColumnCount = 2;
        body.RowCount = 1;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavWidth));
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

        Button reset = BuildCommandButton("重置为默认", false);
        reset.Dock = DockStyle.Top;
        reset.Click += delegate
        {
            WidgetSettings defaults = WidgetSettings.CreateDefaults();
            LoadSettings(defaults);
            this.saved = false;
            if (this.owner != null)
            {
                this.owner.PreviewSettings(ReadSettings());
            }
        };

        this.statusLabel = new Label();
        this.statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        this.statusLabel.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
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
                ShowStatus("保存完成", false);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                ShowStatus("保存失败 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture), true);
            }
        };

        footer.Controls.Add(reset, 0, 0);
        footer.Controls.Add(this.statusLabel, 1, 0);
        footer.Controls.Add(cancel, 2, 0);
        footer.Controls.Add(save, 3, 0);
        return footer;
    }

    private static Button BuildCommandButton(string text, bool primary)
    {
        Button button = new Button();
        button.Text = text; // Keep text so AutoSize works
        button.AutoSize = true;
        button.Padding = new Padding(24, 0, 24, 0); // Adds horizontal padding to AutoSize
        button.Height = 54;
        button.Margin = new Padding(0, 0, 12, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = DesignTokens.CreateUIFont(9.5f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.BackColor = MicaBase;
        button.ForeColor = primary ? Color.Black : TextSecondary;
        Color backBase = primary ? AccentClr : DesignTokens.SettingsTheme.ButtonRest;
        Color backHover = primary ? AccentHover : DesignTokens.SettingsTheme.ButtonHover;
        Color backDown = primary ? AccentPressed : DesignTokens.SettingsTheme.ButtonPressed;
        Color borderColor = primary ? AccentClr : ControlBorder;

        bool hover = false;
        bool down = false;

        button.MouseEnter += (s, e) => { hover = true; button.Invalidate(); };
        button.MouseLeave += (s, e) => { hover = false; button.Invalidate(); };
        button.MouseDown += (s, e) => { down = true; button.Invalidate(); };
        button.MouseUp += (s, e) => { down = false; button.Invalidate(); };

        button.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(MicaBase); // Clear default drawn text/background
            Color currentBack = down ? backDown : (hover ? backHover : backBase);
            using (GraphicsPath path = CreateRoundRectangle(new Rectangle(0, 0, button.Width - 1, button.Height - 1), 6))
            {
                using (SolidBrush brush = new SolidBrush(currentBack))
                {
                    e.Graphics.FillPath(brush, path);
                }
                using (Pen pen = new Pen(borderColor, 1))
                {
                    e.Graphics.DrawPath(pen, path);
                }
            }
            TextRenderer.DrawText(e.Graphics, text, button.Font, button.ClientRectangle, button.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        return button;
    }

    // ── Pages Definition ─────────────────────────────────────────────────
    // Each page uses AddPageGrouped with string[][] where each inner array
    // is [groupTitle, property1, property2, ...].
    private void BuildPages()
    {
        AddPageGrouped("\uE115", "系统", "启动、性能、可见性和全局恢复。", new string[][]
        {
            new string[] { "启动与性能", "StartupEnabled", "PerformanceMode" },
            new string[] { "窗口行为", "VisibilityMode", "ClickThroughMode", "ForceShowForegroundFpsEnabled" },
            new string[] { "恢复与保护", "SeelenDockForegroundPulseEnabled", "WinDRecoveryPulseEnabled", "PowerResumeRestartEnabled" }
        });

        {
            CategoryPage sysPage = this.pages[this.pages.Count - 1];
            Button dumpBtn = BuildCommandButton("记录窗口排版日志", false);
            dumpBtn.Width = 200;
            dumpBtn.Margin = new Padding(0, 16, 0, 32);
            dumpBtn.Click += delegate { DumpLayout(); };
            sysPage.Stack.Controls.Add(dumpBtn);
        }
        AddPageGrouped("\uE7B3", "隐藏与鼠标", "悬停透明、判定范围、延迟显现和防烧屏。", new string[][]
        {
            new string[] { "悬停透明", "HoverOpacityEnabled", "SensitiveMouseModeEnabled", "SensitiveMouseRangePixels" },
            new string[] { "延迟显现", "HoverOpacityRevealDelayEnabled", "HoverOpacityRevealDelaySeconds", "HoverOpacityRevealResetSeconds" },
            new string[] { "覆盖与反向", "HoverOpacityCoverEnabled", "ReverseHoverOpacityRevealEnabled", "ReverseHoverOpacityRestoreDelaySeconds" },
            new string[] { "自动隐藏", "AutoHoverOpacityIdleEnabled", "AutoHoverOpacityIdleSeconds", "AutoHoverOpacityMaximizedEnabled", "BurnInHiddenModeColorProtectionEnabled" }
        });
        AddPageGrouped("\uE737", "主窗口", "主监控窗口尺寸、位置、透明度和显示项。", new string[][]
        {
            new string[] { "尺寸与位置", "Width", "Height", "LeftX", "BottomY" },
            new string[] { "透明度", "BackgroundTransparencyPercent", "ApplicationTransparencyPercent" },
            new string[] { "显示项", "ShowCpu", "ShowMemory", "ShowDisk", "ShowNetwork", "ShowGpu", "ShowNpu" }
        });
        AddPageGrouped("\uE71E", "Codex Radar", "模型、额度、效率和测试覆盖。", new string[][]
        {
            new string[] { "窗口", "CodexRadarWidth", "CodexRadarHeight", "CodexRadarLeftX", "CodexRadarBottomY", "CodexRadarTransparencyPercent" },
            new string[] { "模型与时区", "CodexRadarModelKey", "CodexRadarModelVersion", "DisplayTimeZoneMode", "DisplayTimeZoneId" },
            new string[] { "IQ 测试覆盖", "CodexModelIqTestEnabled", "CodexModelIqTestPassed", "CodexModelIqBaselineMode", "CodexModelIqBaselinePassed" },
            new string[] { "效率测试覆盖", "CodexModelEfficiencyTestEnabled", "CodexModelTokenEfficiencyTestPercent", "CodexModelTimeEfficiencyTestPercent",
                           "CodexModelTokenEfficiencyBaselineMode", "CodexModelTokenEfficiencyBaselinePassed", "CodexModelTokenEfficiencyBaselineTokens",
                           "CodexModelTimeEfficiencyBaselineMode", "CodexModelTimeEfficiencyBaselinePassed", "CodexModelTimeEfficiencyBaselineSeconds",
                           "CodexModelTokenEfficiencyLowThresholdPercent", "CodexModelTimeEfficiencyLowThresholdPercent" },
            new string[] { "随机测试", "CodexRadarRandomTestEnabled", "CodexRadarRandomTestAutoRefresh", "CodexRadarRandomTestRefreshToken" }
        });
        AddPageGrouped("\uEBB0", "功耗与温度", "UX3407N / UX3607O 专用功耗温度窗口。", new string[][]
        {
            new string[] { "窗口", "PowerThermalWidth", "PowerThermalHeight", "PowerThermalLeftX", "PowerThermalBottomY", "PowerThermalTransparencyPercent" },
            new string[] { "自动布局与告警", "PowerThermalAutoSizeEnabled", "PowerThermalAutoDirection", "PowerThermalVisibleAlertCount" },
            new string[] { "测试", "ThermalTestMode" }
        });
        AddPageGrouped("\uE774", "网络", "网络监控、GFW、云服务和出口身份检测。", new string[][]
        {
            new string[] { "网络监控窗口", "NetworkMonitorWidth", "NetworkMonitorHeight", "NetworkMonitorLeftX", "NetworkMonitorBottomY", "NetworkMonitorTransparencyPercent" },
            new string[] { "适配器", "NetworkMonitorAdapterId", "NetworkStatusTestMode" },
            new string[] { "GFW 检测", "GfwProbeEnabled", "GfwProbeIntervalMinutes", "GfwProbeManualRefreshToken" },
            new string[] { "云服务端点", "CloudEndpointTestSeed", "CloudStatusRegionMask" },
            new string[] { "连接检测", "ConnectionCheckWidth", "ConnectionCheckHeight", "ConnectionCheckLeftX", "ConnectionCheckBottomY",
                           "ConnectionCheckTransparencyPercent", "ConnectionCheckBorderTransparencyPercent", "ConnectionCheckIntervalSeconds" },
            new string[] { "出口身份", "ConnectionCheckManualRefreshToken", "CleanIpBadgeTestMode" }
        });
        AddPageGrouped("\uE700", "操作面板", "左下角操作面板尺寸、位置和告警测试。", new string[][]
        {
            new string[] { "尺寸与位置", "OperationButtonSize", "OperationLeftOffset", "OperationBottomOffset", "OperationBackgroundTransparencyPercent" },
            new string[] { "测试", "AlertTestEnabled" }
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
            SettingGroupData group = new SettingGroupData();
            group.Title = groupTitle;

            // Group title label (above the card, Win11 style)
            Label titleLabel = new Label();
            titleLabel.Text = groupTitle;
            titleLabel.Font = DesignTokens.CreateUIFont(10.0f, FontStyle.Bold);
            titleLabel.ForeColor = TextPrimary;
            titleLabel.BackColor = MicaBase;
            titleLabel.AutoSize = false;
            titleLabel.Width = 1152;
            titleLabel.Height = 44;
            titleLabel.Margin = new Padding(0, g == 0 ? 0 : 18, 0, 4);
            titleLabel.TextAlign = ContentAlignment.BottomLeft;
            group.TitleLabel = titleLabel;
            stack.Controls.Add(titleLabel);

            // Group card (rounded background, contains rows)
            SettingGroupCard card = new SettingGroupCard();
            card.Width = 1152;
            card.Margin = new Padding(0, 0, 0, 3);
            group.Card = card;

            for (int i = 1; i < groupDef.Length; i++)
            {
                SettingEditor editor = BuildEditor(groupDef[i]);
                if (editor != null)
                {
                    group.Editors.Add(editor);
                    page.Editors.Add(editor);
                    card.AddRow(editor.Card);
                    this.editors[editor.Property.Name] = editor;
                }
            }

            card.LayoutRows();
            stack.Controls.Add(card);
            page.Groups.Add(group);
        }

        page.ScrollPanel.Resize += delegate { LayoutPage(page); };
        NavigationItem nav = new NavigationItem(icon, title);
        nav.Width = NavWidth - 24;
        nav.Height = NavItemHeight;
        nav.Margin = new Padding(6, 3, 6, 3);
        nav.Font = DesignTokens.CreateUIFont(10.0f);
        nav.Cursor = Cursors.Hand;
        nav.Click += delegate { SelectPage(pageIndex); };
        page.NavItem = nav;
        this.navigationPanel.Controls.Add(nav);
        this.contentHost.Controls.Add(page.ScrollPanel);
        this.pages.Add(page);
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
        title.Font = DesignTokens.CreateUIFont(18.0f, FontStyle.Bold);
        title.ForeColor = TextPrimary;
        title.BackColor = MicaBase;
        title.Location = new Point(0, 0);
        title.Size = new Size(680, GetSingleLineHeight(title.Font, 14));
        title.TextAlign = ContentAlignment.MiddleLeft;

        Label subtitle = new Label();
        subtitle.Text = description;
        subtitle.Font = DesignTokens.CreateUIFont(9.5f);
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
        PropertyInfo property = typeof(WidgetSettings).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property == null || !property.CanRead || !property.CanWrite || property.PropertyType == typeof(string[]))
        {
            return null;
        }

        Control control = BuildValueControl(property);
        SettingRow card = new SettingRow(control);
        card.Width = 1152;
        card.Margin = new Padding(0);
        card.TitleLabel.Text = GetSettingTitle(propertyName);
        card.HintLabel.Text = GetSettingHint(propertyName);
        card.BackColor = Color.Transparent;
        return new SettingEditor(property, card, control);
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
            ComboBox combo = new ComboBox();
            combo.Width = 352;
            combo.Height = 54;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = ControlBg;
            combo.ForeColor = TextSecondary;
            combo.Font = DesignTokens.CreateUIFont(9.5f);
            Array values = Enum.GetValues(type);
            for (int i = 0; i < values.Length; i++)
            {
                combo.Items.Add(new EnumOption(values.GetValue(i)));
            }
            combo.SelectedIndexChanged += delegate { OnSettingChanged(); };
            return combo;
        }

        if (type == typeof(int) || type == typeof(double))
        {
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
            box.Font = DesignTokens.CreateUIFont(9.5f);
            box.ValueChanged += delegate { OnSettingChanged(); };
            return box;
        }

        TextBox text = new TextBox();
        text.Width = 400;
        text.Height = 54;
        text.BackColor = ControlBg;
        text.ForeColor = TextSecondary;
        text.BorderStyle = BorderStyle.FixedSingle;
        text.Font = DesignTokens.CreateUIFont(9.5f);
        text.TextChanged += delegate { OnSettingChanged(); };
        return text;
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
            }
        }

        this.selectedPageIndex = pageIndex;
        this.pages[pageIndex].ScrollPanel.BringToFront();
    }

    private void LayoutPage(CategoryPage page)
    {
        int width = Math.Max(360, page.ScrollPanel.ClientSize.Width - page.ScrollPanel.Padding.Left - page.ScrollPanel.Padding.Right - 22);
        page.Stack.Width = width;
        page.Heading.Width = width;
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
                object value = editor.Property.GetValue(settings, null);
                SetEditorValue(editor, value);
            }
        }
        finally
        {
            this.initializing = false;
        }
    }

    private WidgetSettings ReadSettings()
    {
        WidgetSettings settings = this.baseline.Clone();
        foreach (SettingEditor editor in this.editors.Values)
        {
            object value = GetEditorValue(editor);
            if (value != null)
            {
                editor.Property.SetValue(settings, value, null);
            }
        }

        settings.Normalize();
        return settings;
    }

    private void SetEditorValue(SettingEditor editor, object value)
    {
        ToggleSwitch toggle = editor.Control as ToggleSwitch;
        if (toggle != null)
        {
            toggle.SetCheckedSilent(value is bool && (bool)value);
            return;
        }

        ComboBox combo = editor.Control as ComboBox;
        if (combo != null)
        {
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
    }

    private object GetEditorValue(SettingEditor editor)
    {
        Type type = editor.Property.PropertyType;
        ToggleSwitch toggle = editor.Control as ToggleSwitch;
        if (toggle != null)
        {
            return toggle.Checked;
        }

        ComboBox combo = editor.Control as ComboBox;
        if (combo != null)
        {
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

    // ── Status Toast ─────────────────────────────────────────────────────
    private void ShowStatus(string text, bool error)
    {
        if (this.statusLabel == null)
        {
            return;
        }

        this.statusTimer.Stop();
        this.statusLabel.Text = text;
        this.statusLabel.ForeColor = error ? ErrorClr : AccentClr;
        this.statusLabel.Visible = true;
        this.statusTimer.Start();
    }

    private void OnStatusTimerTick(object sender, EventArgs e)
    {
        this.statusTimer.Stop();
        if (this.statusLabel != null)
        {
            this.statusLabel.Visible = false;
        }
    }

    // ── Search Filter ────────────────────────────────────────────────────
    private void ApplySearchFilter()
    {
        string query = (this.searchBox == null ? string.Empty : this.searchBox.Text ?? string.Empty).Trim();
        for (int i = 0; i < this.pages.Count; i++)
        {
            CategoryPage page = this.pages[i];
            bool pageMatch = query.Length == 0 ||
                page.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                page.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            bool anyVisible = false;

            for (int g = 0; g < page.Groups.Count; g++)
            {
                SettingGroupData group = page.Groups[g];
                bool anyRowVisible = false;
                bool groupTitleMatch = group.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                for (int j = 0; j < group.Editors.Count; j++)
                {
                    SettingEditor editor = group.Editors[j];
                    bool match = pageMatch || groupTitleMatch ||
                        editor.Property.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        GetSettingTitle(editor.Property.Name).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
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
        return SettingHints.TryGetValue(name, out value) ? value : name;
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

    private static Size FitClientSizeToScreen(Size desiredSize)
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int width = Math.Min(desiredSize.Width, Math.Max(760, workArea.Width - 80));
        int height = Math.Min(desiredSize.Height, Math.Max(560, workArea.Height - 80));
        return new Size(width, height);
    }

    private static Size GetMinimumWindowSizeForScreen()
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        return new Size(
            Math.Min(1440, Math.Max(1216, workArea.Width - 128)),
            Math.Min(992, Math.Max(896, workArea.Height - 128)));
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
    internal static void RunSettingsBindingSelfTest()
    {
        WidgetSettings baseline = WidgetSettings.CreateDefaults();
        using (Win11SettingsForm form = new Win11SettingsForm(null, baseline))
        {
            form.OwnerFormClosing = true;
            form.saved = true;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-51200, -51200);
            form.Show();
            Application.DoEvents();
            form.VerifySelfTest();
        }
    }


    private void DumpLayout()
    {
        try
        {
            using (System.IO.StreamWriter w = new System.IO.StreamWriter("settings_layout_dump.txt"))
            {
                DumpControl(this, w, 0);
            }
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("dump_error.txt", ex.ToString());
        }
    }

    private void DumpControl(Control c, System.IO.StreamWriter w, int indent)
    {
        string ind = new string(' ', indent * 2);
        w.WriteLine($"{ind}{c.GetType().Name} '{c.Text}' - Bounds: {c.Bounds}, PrefSize: {c.GetPreferredSize(new Size(c.Width, 0))}, ClientSize: {c.ClientSize}, Margin: {c.Margin}, Padding: {c.Padding}, AutoSize: {c.AutoSize}, Dock: {c.Dock}, Visible: {c.Visible}");
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

    private void VerifySelfTest()
    {
        string[] required = new string[]
        {
            "PerformanceMode",
            "HoverOpacityEnabled",
            "HoverOpacityRevealDelayEnabled",
            "Width",
            "CodexRadarModelKey",
            "PowerThermalAutoSizeEnabled",
            "GfwProbeIntervalMinutes",
            "OperationButtonSize"
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

    private void VerifyNoVisibleControlClipping()
    {
        Size originalSize = this.ClientSize;
        int originalPageIndex = this.selectedPageIndex;
        this.ClientSize = new Size(this.MinimumSize.Width, this.MinimumSize.Height);
        Application.DoEvents();
        try
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
                    int rightLimit = card.ClientSize.Width - card.Padding.Right + 1;
                    int bottomLimit = card.ClientSize.Height - card.Padding.Bottom + 1;
                    if (control.Left < card.Padding.Left ||
                        control.Right > rightLimit ||
                        control.Bottom > bottomLimit ||
                        card.TitleLabel.Right > rightLimit ||
                        card.HintLabel.Right > rightLimit)
                    {
                        throw new InvalidOperationException("WinUI settings layout clipped: " + editor.Property.Name);
                    }
                }
            }
        }
        finally
        {
            this.ClientSize = originalSize;
            SelectPage(originalPageIndex);
            Application.DoEvents();
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
        { "PowerThermalWidth", new NumericRange(WidgetSettings.MinPowerThermalWidth, WidgetSettings.MaxPowerThermalWidth) },
        { "PowerThermalHeight", new NumericRange(WidgetSettings.MinPowerThermalHeight, WidgetSettings.MaxPowerThermalHeight) },
        { "PowerThermalVisibleAlertCount", new NumericRange(WidgetSettings.MinPowerThermalVisibleAlerts, WidgetSettings.MaxPowerThermalVisibleAlerts) },
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
        { "SensitiveMouseRangePixels", new NumericRange(WidgetSettings.MinSensitiveMouseRangePixels, WidgetSettings.MaxSensitiveMouseRangePixels) },
        { "HoverOpacityRevealDelaySeconds", new NumericRange((decimal)WidgetSettings.MinHoverOpacityRevealDelaySeconds, (decimal)WidgetSettings.MaxHoverOpacityRevealDelaySeconds) },
        { "HoverOpacityRevealResetSeconds", new NumericRange((decimal)WidgetSettings.MinHoverOpacityRevealResetSeconds, (decimal)WidgetSettings.MaxHoverOpacityRevealResetSeconds) },
        { "ReverseHoverOpacityRestoreDelaySeconds", new NumericRange(WidgetSettings.MinReverseHoverOpacityRestoreDelaySeconds, WidgetSettings.MaxReverseHoverOpacityRestoreDelaySeconds) },
        { "AutoHoverOpacityIdleSeconds", new NumericRange(WidgetSettings.MinAutoHoverOpacityIdleSeconds, WidgetSettings.MaxAutoHoverOpacityIdleSeconds) },
        { "CodexModelIqTestPassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) },
        { "CodexModelIqBaselinePassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) },
        { "CodexModelTokenEfficiencyBaselinePassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) },
        { "CodexModelTimeEfficiencyBaselinePassed", new NumericRange(WidgetSettings.MinCodexModelIqPassed, WidgetSettings.MaxCodexModelIqPassed) }
    };

    private static readonly Dictionary<string, string> SettingTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "StartupEnabled", "开机启动" },
        { "PerformanceMode", "性能模式" },
        { "VisibilityMode", "可见性" },
        { "ClickThroughMode", "点击穿透" },
        { "ForceShowForegroundFpsEnabled", "强制显示 FPS" },
        { "SeelenDockForegroundPulseEnabled", "Seelen Dock 自动拉前" },
        { "WinDRecoveryPulseEnabled", "Win+D 后延迟拉前" },
        { "PowerResumeRestartEnabled", "休眠唤醒后重启" },
        { "HoverOpacityEnabled", "悬停透明 95%" },
        { "SensitiveMouseModeEnabled", "敏感鼠标模式" },
        { "SensitiveMouseRangePixels", "鼠标判定边长" },
        { "HoverOpacityRevealDelayEnabled", "延迟显现" },
        { "HoverOpacityRevealDelaySeconds", "显现延迟秒数" },
        { "HoverOpacityRevealResetSeconds", "重置秒数" },
        { "HoverOpacityCoverEnabled", "覆盖开启" },
        { "ReverseHoverOpacityRevealEnabled", "反向隐藏" },
        { "ReverseHoverOpacityRestoreDelaySeconds", "移开后恢复秒数" },
        { "AutoHoverOpacityIdleEnabled", "空闲自动隐藏" },
        { "AutoHoverOpacityIdleSeconds", "空闲隐藏秒数" },
        { "AutoHoverOpacityMaximizedEnabled", "最大化自动隐藏" },
        { "BurnInHiddenModeColorProtectionEnabled", "隐藏反色防烧屏" },
        { "CodexRadarModelKey", "Codex Radar 模型" },
        { "CodexRadarModelVersion", "模型版本" },
        { "CodexRadarRandomTestEnabled", "随机测试" },
        { "CodexRadarRandomTestAutoRefresh", "随机测试自动刷新" },
        { "CodexRadarRandomTestRefreshToken", "随机测试刷新令牌" },
        { "CodexModelIqTestEnabled", "覆盖实时 IQ 数据" },
        { "CodexModelIqTestPassed", "IQ 测试通过项" },
        { "CodexModelIqBaselineMode", "IQ 基准模式" },
        { "CodexModelIqBaselinePassed", "IQ 基准通过项" },
        { "CodexModelEfficiencyTestEnabled", "覆盖实时效率数据" },
        { "CodexModelTokenEfficiencyTestPercent", "Token 效率测试百分比" },
        { "CodexModelTimeEfficiencyTestPercent", "时间效率测试百分比" },
        { "CodexModelTokenEfficiencyBaselineMode", "Token 效率基准模式" },
        { "CodexModelTimeEfficiencyBaselineMode", "时间效率基准模式" },
        { "CodexModelTokenEfficiencyBaselineTokens", "Token 效率基准值" },
        { "CodexModelTimeEfficiencyBaselineSeconds", "时间效率基准秒数" },
        { "CodexModelTokenEfficiencyLowThresholdPercent", "Token 低效阈值" },
        { "CodexModelTimeEfficiencyLowThresholdPercent", "时间低效阈值" },
        { "DisplayTimeZoneId", "显示时区 ID" },
        { "DisplayTimeZoneMode", "显示时区模式" },
        { "PowerThermalAutoSizeEnabled", "功耗模块自动大小" },
        { "PowerThermalAutoDirection", "自动大小方向" },
        { "PowerThermalVisibleAlertCount", "可见告警数量" },
        { "NetworkMonitorAdapterId", "网络适配器 ID" },
        { "NetworkStatusTestMode", "网络状态测试" },
        { "GfwProbeIntervalMinutes", "GFW 检测间隔" },
        { "GfwProbeEnabled", "启用 GFW 检测" },
        { "GfwProbeManualRefreshToken", "GFW 手动刷新令牌" },
        { "CloudEndpointTestSeed", "云服务测试种子" },
        { "CloudStatusRegionMask", "云服务地区掩码" },
        { "CleanIpBadgeTestMode", "出口身份测试模式" },
        { "ThermalTestMode", "温控测试模式" },
        { "AlertTestEnabled", "告警测试" },
        { "CodexModelTokenEfficiencyBaselinePassed", "Token 效率基准通过项" },
        { "CodexModelTimeEfficiencyBaselinePassed", "时间效率基准通过项" }
    };

    private static readonly Dictionary<string, string> SettingHints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "StartupEnabled", "写入当前用户启动项。" },
        { "PerformanceMode", "控制采样、动画和后台刷新节奏。" },
        { "VisibilityMode", "决定窗口在桌面、前台或全屏时的行为。" },
        { "ClickThroughMode", "允许鼠标事件穿透主窗口。" },
        { "HoverOpacityEnabled", "鼠标接近窗口时进入隐藏透明度。" },
        { "SensitiveMouseModeEnabled", "使用鼠标周围方形区域判断命中。" },
        { "SensitiveMouseRangePixels", "范围 10-300，值越大越容易触发。" },
        { "HoverOpacityRevealDelayEnabled", "鼠标离开后延迟恢复显示。" },
        { "HoverOpacityCoverEnabled", "自动隐藏时只在移到窗口上方后恢复。" },
        { "ReverseHoverOpacityRevealEnabled", "手动隐藏下鼠标移入窗口临时恢复。" },
        { "BurnInHiddenModeColorProtectionEnabled", "隐藏时执行颜色反相和白灰透明化。" },
        { "CodexRadarModelKey", "动态模型目录中的当前模型键。" },
        { "NetworkMonitorAdapterId", "留空时自动选择网络适配器。" },
        { "GfwProbeEnabled", "与云服务检测独立调度。" },
        { "PowerResumeRestartEnabled", "系统唤醒后自动重启 SeelenUI 和本程序。" },
        { "WinDRecoveryPulseEnabled", "按 Win+D 后延迟拉前本程序和 SeelenUI。" }
    };

    // ═════════════════════════════════════════════════════════════════════
    // Nested Classes — Custom Controls
    // ═════════════════════════════════════════════════════════════════════

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
                    Color trackColor = this.hover ? DesignTokens.NeonGeekTheme.ToggleTrackHover : DesignTokens.NeonGeekTheme.ToggleTrackOff;
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

            using (SolidBrush knobBrush = new SolidBrush(DesignTokens.SettingsTheme.ToggleKnob))
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
            Color bg = this.selected ? DesignTokens.SettingsTheme.NavSelectedBg :
                       this.hover ? DesignTokens.SettingsTheme.NavHoverBg : Color.Transparent;

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
            int y = 0;
            bool first = true;
            for (int i = 0; i < this.rows.Count; i++)
            {
                SettingRow row = this.rows[i];
                row.ShowTopDivider = !first;
                int h = row.ComputeDesiredHeight(this.Width);
                row.SetBounds(0, y, this.Width, h);
                y += h;
                first = false;
            }
            this.Height = Math.Max(1, y);
            UpdateClipRegion();
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
            LayoutRows();
        }
    }

    // ── SettingRow ────────────────────────────────────────────────────────
    // A single setting item within a SettingGroupCard.
    // Left side: title + hint; right side: value control.
    // Draws hover highlight and optional top divider.
    private sealed class SettingRow : Panel
    {
        private readonly Control valueControl;
        private bool hover;
        public bool ShowTopDivider;

        public SettingRow(Control valueControl)
        {
            this.valueControl = valueControl;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer, true);
            this.BackColor = Color.Transparent;
            this.Padding = new Padding(
                DesignTokens.Spacing.SettingsCardPaddingX,
                DesignTokens.Spacing.SettingsCardPaddingY,
                DesignTokens.Spacing.SettingsCardPaddingX,
                DesignTokens.Spacing.SettingsCardPaddingY);

            this.TitleLabel = new Label();
            this.TitleLabel.Font = DesignTokens.CreateUIFont(10.0f);
            this.TitleLabel.ForeColor = TextPrimary;
            this.TitleLabel.BackColor = Color.Transparent;
            this.TitleLabel.TextAlign = ContentAlignment.MiddleLeft;
            this.TitleLabel.AutoSize = true;

            this.HintLabel = new Label();
            this.HintLabel.Font = DesignTokens.CreateUIFont(8.5f);
            this.HintLabel.ForeColor = TextTertiary;
            this.HintLabel.BackColor = Color.Transparent;
            this.HintLabel.TextAlign = ContentAlignment.TopLeft;
            this.HintLabel.AutoSize = true;

            this.Controls.Add(this.TitleLabel);
            this.Controls.Add(this.HintLabel);
            this.Controls.Add(valueControl);
        }

        public Label TitleLabel { get; private set; }
        public Label HintLabel { get; private set; }

        public int ComputeDesiredHeight(int width)
        {
            int pad = this.Padding.Top + this.Padding.Bottom;
            int controlWidth = Math.Min(this.valueControl.Width, Math.Max(44, width - this.Padding.Left - this.Padding.Right));
            bool compact = width < 928 || width - this.Padding.Left - this.Padding.Right - controlWidth < 320;

            if (compact)
            {
                int textWidth = Math.Max(120, width - this.Padding.Left - this.Padding.Right);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int controlTop = this.Padding.Top + titleHeight + hintHeight + 8;
                return Math.Max(80, controlTop + this.valueControl.Height + this.Padding.Bottom);
            }
            else
            {
                int controlLeft = width - this.Padding.Right - controlWidth;
                int textWidth = Math.Max(120, controlLeft - this.Padding.Left - 24);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int textHeight = titleHeight + hintHeight;
                int contentHeight = Math.Max(textHeight, this.valueControl.Height);
                return Math.Max(60, this.Padding.Top + contentHeight + this.Padding.Bottom);
            }
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
            LayoutChildren();
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
            int left = this.Padding.Left;
            int top = this.Padding.Top;
            int right = this.Width - this.Padding.Right;
            int controlWidth = Math.Min(this.valueControl.Width, Math.Max(44, this.Width - this.Padding.Left - this.Padding.Right));
            bool compact = this.Width < 580 || this.Width - this.Padding.Left - this.Padding.Right - controlWidth < 320;

            if (compact)
            {
                int textWidth = Math.Max(120, right - left);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int controlTop = top + titleHeight + hintHeight + 8;
                this.TitleLabel.SetBounds(left, top, textWidth, titleHeight);
                this.HintLabel.SetBounds(left, top + titleHeight, textWidth, hintHeight);
                this.valueControl.SetBounds(left, controlTop, controlWidth, this.valueControl.Height);
            }
            else
            {
                int controlLeft = right - controlWidth;
                int textWidth = Math.Max(120, controlLeft - left - 24);
                int titleHeight = GetWrappedTextHeight(this.TitleLabel.Text, this.TitleLabel.Font, textWidth, 6);
                int hintHeight = string.IsNullOrEmpty(this.HintLabel.Text) ? 0 : GetWrappedTextHeight(this.HintLabel.Text, this.HintLabel.Font, textWidth, 4);
                int textHeight = titleHeight + hintHeight;
                int contentHeight = Math.Max(textHeight, this.valueControl.Height);
                int controlTop = top + Math.Max(0, (contentHeight - this.valueControl.Height) / 2);
                this.valueControl.SetBounds(controlLeft, controlTop, controlWidth, this.valueControl.Height);
                this.TitleLabel.SetBounds(left, top, textWidth, titleHeight);
                this.HintLabel.SetBounds(left, top + titleHeight, textWidth, hintHeight);
            }
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
        public Label TitleLabel;
        public SettingGroupCard Card;
        public readonly List<SettingEditor> Editors = new List<SettingEditor>();
    }

    private sealed class SettingEditor
    {
        public SettingEditor(PropertyInfo property, SettingRow card, Control control)
        {
            this.Property = property;
            this.Card = card;
            this.Control = control;
        }

        public PropertyInfo Property { get; private set; }
        public SettingRow Card { get; private set; }
        public Control Control { get; private set; }
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
