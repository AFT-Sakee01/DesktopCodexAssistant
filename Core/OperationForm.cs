using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class OperationForm : Form
{
    private const int ButtonCount = 13;
    private const int StartButtonIndex = 0;
    private const int WindowsSettingsButtonIndex = 1;
    private const int WindowsPowerMenuButtonIndex = 2;
    private const int RefreshButtonIndex = 3;
    private const int RestartButtonIndex = 4;
    private const int BatteryCarePauseButtonIndex = 5;
    private const int BatteryLimitRestoreButtonIndex = 6;
    private const int AppSettingsButtonIndex = 7;
    private const int TaskManagerButtonIndex = 8;
    private const int WindowsAiStudioButtonIndex = 9;
    private const int WindowsQuickSettingsButtonIndex = 10;
    private const int LiveCaptionsButtonIndex = 11;
    private const int HoverOpacityToggleButtonIndex = 12;
    private const int SmallColumnCount = 6;
    private const int ForegroundFpsRefreshIntervalMs = 1000;
    private const int ForcedOperationOpacityAlpha = 48;
    private const string AsusAssistantPackagePrefix = "B9ECED6F.ASUSPCAssistant_";
    private const string AsusAssistantPackageSuffix = "_qmba6cd70vzyy";
    private const string AsusKeyboardHostRelativePath = @"HwAdjustPage\ATK Package\AsusKeyboardHost.exe";
    private const string AsusKeyboardHostAlias = "B9ECED6F.ASUSPCAssistant.AsusKeyboardHost.exe";
    private const string AsusBatteryCarePauseArguments = "-HWSettingsToast acin_set";
    private const string AsusBatteryLimitRestoreArguments = "-HWSettingsToast acin80";
    private const string SeelenPowerMenuWidgetId = "@seelen/power-menu";
    private const string SeelenCliFileName = "slu.exe";
    private const string SeelenUiProcessName = "seelen-ui";
    private const string SeelenUiExecutableName = "seelen-ui.exe";
    private const string SeelenInstallRelativePath = @"Seelen\Seelen UI";
    private const int SeelenStatusRefreshIntervalMs = 2500;
    private const double HoverStepPerSecond = 7.5;
    private const double PressAnimationMs = 150.0;
    private readonly Action openSettingsAction;
    private readonly Action forceRefreshAction;
    private readonly Action restartAction;
    private readonly Action<string, string, ToolTipIcon> notificationAction;
    private readonly Func<bool> toggleHoverOpacityAction;
    private readonly Func<bool> pulseSeelenDockAction;
    private readonly System.Windows.Forms.Timer animationTimer;
    private readonly System.Windows.Forms.Timer seelenStatusTimer;
    private readonly System.Windows.Forms.Timer foregroundFpsTimer;
    private readonly System.Windows.Forms.Timer restartSingleClickTimer;
    private readonly ForegroundFpsReader foregroundFpsReader;
    private readonly ToolTip hoverToolTip;
    private readonly bool isAsusZenbookDevice;
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private bool seelenUiRunning;
    private bool myAsusInstalled;
    private bool windowsAiStudioAvailable;
    private bool liveCaptionsAvailable;
    private int hoveredButton = -1;
    private int toolTipButton = -1;
    private int pressedButton = -1;
    private int pressAnimationButton = -1;
    private bool batteryCarePauseRunning;
    private bool batteryLimitRestoreRunning;
    private DateTime animationLastUtc;
    private DateTime pressAnimationStartUtc;
    private int? foregroundFrameRate;
    private long burnInShiftSlot = long.MinValue;
    private readonly double[] hoverProgress = new double[ButtonCount];
    private Bitmap renderBitmap;
    private Graphics renderGraphics;

    public OperationForm(WidgetSettings settings, Action openSettingsAction, Action forceRefreshAction, Action restartAction, Action<string, string, ToolTipIcon> notificationAction, Func<bool> toggleHoverOpacityAction, Func<bool> pulseSeelenDockAction)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.openSettingsAction = openSettingsAction;
        this.forceRefreshAction = forceRefreshAction;
        this.restartAction = restartAction;
        this.notificationAction = notificationAction;
        this.toggleHoverOpacityAction = toggleHoverOpacityAction;
        this.pulseSeelenDockAction = pulseSeelenDockAction;
        this.isAsusZenbookDevice = DetectAsusZenbookDevice();
        this.myAsusInstalled = DetectMyAsusInstalled();
        this.seelenUiRunning = IsSeelenUiRunning();
        this.windowsAiStudioAvailable = NativeMethods.IsWindowsAiStudioAvailable();
        this.liveCaptionsAvailable = NativeMethods.IsLiveCaptionsAvailable();
        Program.LogInfo(
            "Operation panel device capabilities. AsusZenbook=" +
            this.isAsusZenbookDevice.ToString() +
            ", MyAsusInstalled=" +
            this.myAsusInstalled.ToString() +
            ", SeelenUiRunning=" +
            this.seelenUiRunning.ToString() +
            ", AiStudioAvailable=" +
            this.windowsAiStudioAvailable.ToString() +
            ", LiveCaptionsAvailable=" +
            this.liveCaptionsAvailable.ToString());

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.Size = GetDesiredSize();
        this.Cursor = Cursors.Hand;

        this.animationTimer = new System.Windows.Forms.Timer();
        this.animationTimer.Interval = WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode);
        this.animationTimer.Tick += OnAnimationTimerTick;

        this.seelenStatusTimer = new System.Windows.Forms.Timer();
        this.seelenStatusTimer.Interval = SeelenStatusRefreshIntervalMs;
        this.seelenStatusTimer.Tick += OnSeelenStatusTimerTick;

        this.foregroundFpsReader = new ForegroundFpsReader();
        this.foregroundFpsTimer = new System.Windows.Forms.Timer();
        this.foregroundFpsTimer.Interval = ForegroundFpsRefreshIntervalMs;
        this.foregroundFpsTimer.Tick += OnForegroundFpsTimerTick;
        this.restartSingleClickTimer = new System.Windows.Forms.Timer();
        this.restartSingleClickTimer.Interval = Math.Max(1, SystemInformation.DoubleClickTime);
        this.restartSingleClickTimer.Tick += OnRestartSingleClickTimerTick;

        this.hoverToolTip = new ToolTip();
        this.hoverToolTip.ShowAlways = true;
        this.hoverToolTip.InitialDelay = 450;
        this.hoverToolTip.ReshowDelay = 100;
        this.hoverToolTip.AutoPopDelay = 5000;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshSeelenUiStatus();
        this.seelenStatusTimer.Start();
        ApplyRuntimeSettings(this.currentSettings);
        UpdateForegroundFpsTimer();
        PositionOperationWindow();
        RenderLayeredWindow();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.animationTimer.Stop();
        this.animationTimer.Tick -= OnAnimationTimerTick;
        this.animationTimer.Dispose();
        this.seelenStatusTimer.Stop();
        this.seelenStatusTimer.Tick -= OnSeelenStatusTimerTick;
        this.seelenStatusTimer.Dispose();
        this.foregroundFpsTimer.Stop();
        this.foregroundFpsTimer.Tick -= OnForegroundFpsTimerTick;
        this.foregroundFpsTimer.Dispose();
        this.restartSingleClickTimer.Stop();
        this.restartSingleClickTimer.Tick -= OnRestartSingleClickTimerTick;
        this.restartSingleClickTimer.Dispose();
        this.foregroundFpsReader.Dispose();
        this.hoverToolTip.Hide(this);
        this.hoverToolTip.Dispose();
        DisposeRenderBuffer();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(9)))
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

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionOperationWindow();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        int animationInterval = WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode);
        if (this.animationTimer.Interval != animationInterval)
        {
            this.animationTimer.Interval = animationInterval;
        }

        if (!IsButtonInteractive(this.hoveredButton))
        {
            this.hoveredButton = -1;
            HideHoverToolTip();
        }

        if (!IsButtonInteractive(this.pressedButton))
        {
            this.pressedButton = -1;
        }

        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionOperationWindow();
        UpdateForegroundFpsTimer();
        RenderLayeredWindow();
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
            this.animationTimer.Stop();
            if (this.Visible)
            {
                this.Hide();
            }

            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionOperationWindow();
        RenderLayeredWindow();
    }

    public void RecoverAfterDisplayResume()
    {
        ResetDisplayRenderResources();
        PositionOperationWindow();
        RenderLayeredWindow();
    }

    public void PrepareForDisplaySuspend()
    {
        ResetDisplayRenderResources();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int button = HitTest(e.Location);
        if (button != this.hoveredButton)
        {
            this.hoveredButton = button;
            UpdateHoverToolTip(button, e.Location);
            EnsureAnimationTimer();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.hoveredButton = -1;
        HideHoverToolTip();
        EnsureAnimationTimer();
    }

    private void UpdateHoverToolTip(int button, Point location)
    {
        string text = GetButtonToolTipText(button);
        if (string.IsNullOrEmpty(text))
        {
            HideHoverToolTip();
            return;
        }

        this.toolTipButton = button;
        this.hoverToolTip.Hide(this);
        this.hoverToolTip.Show(text, this, new Point(location.X + S(12), location.Y + S(18)), 5000);
    }

    private void HideHoverToolTip()
    {
        if (this.toolTipButton < 0)
        {
            return;
        }

        this.toolTipButton = -1;
        this.hoverToolTip.Hide(this);
    }

    private string GetButtonToolTipText(int button)
    {
        if (!IsButtonVisible(button))
        {
            return string.Empty;
        }

        if (IsButtonUnavailable(button))
        {
            return GetUnavailableButtonToolTipText(button);
        }

        if (!IsButtonEnabled(button))
        {
            return string.Empty;
        }

        if (button == StartButtonIndex)
        {
            return "左键：Windows 开始菜单\r\n右键：Windows 开始右键菜单\r\n通过任务栏系统入口打开，无快捷键回退";
        }

        if (button == WindowsSettingsButtonIndex)
        {
            return "Windows 设置";
        }

        if (button == WindowsPowerMenuButtonIndex)
        {
            return "打开 SeelenUI 电源界面\r\n不可用时尝试 Windows 安全菜单，无快捷键回退";
        }

        if (button == RefreshButtonIndex)
        {
            return "刷新所有模块";
        }

        if (button == RestartButtonIndex)
        {
            return "单击：拉到前 Seelen Dock\r\n双击：重启 SeelenUI 和本程序";
        }

        if (button == BatteryCarePauseButtonIndex)
        {
            return "关闭电池保护 24 小时";
        }

        if (button == BatteryLimitRestoreButtonIndex)
        {
            return "开启电池保护";
        }

        if (button == AppSettingsButtonIndex)
        {
            return "程序设置";
        }

        if (button == TaskManagerButtonIndex)
        {
            return "打开任务管理器";
        }

        if (button == WindowsAiStudioButtonIndex)
        {
            return "打开 AI Studio";
        }

        if (button == WindowsQuickSettingsButtonIndex)
        {
            return "打开快速设置\r\n使用快捷键 Win+A";
        }

        if (button == LiveCaptionsButtonIndex)
        {
            return "打开实时字幕";
        }

        if (button == HoverOpacityToggleButtonIndex)
        {
            return this.currentSettings.ForceHoverOpacityActive
                ? "恢复模块透明度"
                : "切换到悬停透明度";
        }

        return string.Empty;
    }

    private string GetUnavailableButtonToolTipText(int button)
    {
        if (button == WindowsAiStudioButtonIndex)
        {
            return "AI Studio 当前不可用\r\n未检测到 ms-clicktodo 协议或 CoreAI 包";
        }

        if (button == LiveCaptionsButtonIndex)
        {
            return "实时字幕当前不可用\r\n未检测到系统实时字幕入口";
        }

        return "当前系统入口不可用";
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        HideHoverToolTip();
        int button = HitTest(e.Location);
        if (!AcceptsMouseButton(button, e.Button))
        {
            return;
        }

        this.pressedButton = button;
        this.pressAnimationButton = button;
        this.pressAnimationStartUtc = DateTime.UtcNow;
        EnsureAnimationTimer();
        RenderLayeredWindow();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int button = HitTest(e.Location);
        int pressed = this.pressedButton;
        this.pressedButton = -1;
        this.pressAnimationButton = pressed;
        this.pressAnimationStartUtc = DateTime.UtcNow;
        EnsureAnimationTimer();
        RenderLayeredWindow();

        if (button != pressed || !AcceptsMouseButton(button, e.Button))
        {
            return;
        }

        if (button == RestartButtonIndex)
        {
            HandleRestartButtonClick();
            return;
        }

        ExecuteButton(button, e.Button);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawOperationWindow(e.Graphics);
    }

    private void OnAnimationTimerTick(object sender, EventArgs e)
    {
        bool changed = UpdateAnimationState();
        if (changed)
        {
            RenderLayeredWindow();
        }

        // The operation panel has no continuous workload once the interaction animation settles.
        if (!NeedsAnimationTimer())
        {
            this.animationTimer.Stop();
        }
    }

    private void OnSeelenStatusTimerTick(object sender, EventArgs e)
    {
        RefreshSeelenUiStatus();
        if (!this.hiddenForFullscreen &&
            this.Visible &&
            BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot))
        {
            PositionOperationWindow();
        }
    }

    private void OnForegroundFpsTimerTick(object sender, EventArgs e)
    {
        if (!ShouldDrawFpsPanel())
        {
            UpdateForegroundFpsTimer();
            RenderLayeredWindow();
            return;
        }

        UpdateForegroundFrameRate();
        RenderLayeredWindow();
    }

    private void RefreshSeelenUiStatus()
    {
        SetSeelenUiRunning(IsSeelenUiRunning());
    }

    private void SetSeelenUiRunning(bool running)
    {
        if (this.seelenUiRunning == running)
        {
            return;
        }

        this.seelenUiRunning = running;
        if (!IsButtonInteractive(this.hoveredButton))
        {
            this.hoveredButton = -1;
            HideHoverToolTip();
        }

        if (!IsButtonInteractive(this.pressedButton))
        {
            this.pressedButton = -1;
        }

        RenderLayeredWindow();
    }

    private void ExecuteButton(int button, MouseButtons mouseButton)
    {
        if (button == StartButtonIndex)
        {
            if (mouseButton == MouseButtons.Right)
            {
                if (!NativeMethods.OpenWindowsStartContextMenu())
                {
                    ShowOperationNotification(
                        "开始右键菜单",
                        "未能通过任务栏系统入口打开 Windows 开始右键菜单。",
                        ToolTipIcon.Warning);
                }
            }
            else
            {
                if (!NativeMethods.OpenWindowsStartMenu())
                {
                    ShowOperationNotification(
                        "开始菜单",
                        "未能通过任务栏系统入口打开 Windows 开始菜单。",
                        ToolTipIcon.Warning);
                }
            }

            return;
        }

        if (button == WindowsSettingsButtonIndex)
        {
            NativeMethods.OpenWindowsSettings();
            return;
        }

        if (button == WindowsPowerMenuButtonIndex)
        {
            if (!TryOpenSeelenPowerMenu())
            {
                if (!NativeMethods.OpenWindowsSecurityMenu())
                {
                    ShowOperationNotification(
                        "电源菜单",
                        "SeelenUI 电源界面不可用，且未能通过系统接口打开 Windows 安全菜单。",
                        ToolTipIcon.Warning);
                }
            }

            return;
        }

        if (button == AppSettingsButtonIndex)
        {
            if (this.openSettingsAction != null)
            {
                this.openSettingsAction();
            }

            return;
        }

        if (button == RefreshButtonIndex)
        {
            RefreshSeelenUiStatus();
            RefreshMyAsusInstallStatus();
            RefreshSystemButtonAvailability();
            if (this.forceRefreshAction != null)
            {
                this.forceRefreshAction();
            }

            return;
        }

        if (button == RestartButtonIndex)
        {
            HandleRestartButtonClick();
            return;
        }

        if (button == BatteryCarePauseButtonIndex)
        {
            BeginInvokeBatteryCarePause();
            return;
        }

        if (button == BatteryLimitRestoreButtonIndex)
        {
            BeginInvokeBatteryLimitRestore();
            return;
        }

        if (button == TaskManagerButtonIndex)
        {
            NativeMethods.OpenTaskManager();
            return;
        }

        if (button == WindowsAiStudioButtonIndex)
        {
            if (!NativeMethods.OpenWindowsAiStudio())
            {
                ShowOperationNotification(
                    "AI Studio",
                    "未能通过系统入口启动 AI Studio。",
                    ToolTipIcon.Warning);
            }

            return;
        }

        if (button == WindowsQuickSettingsButtonIndex)
        {
            NativeMethods.OpenQuickSettings();
            return;
        }

        if (button == LiveCaptionsButtonIndex)
        {
            if (!NativeMethods.OpenLiveCaptions())
            {
                ShowOperationNotification(
                    "实时字幕",
                    "未能通过系统入口启动实时字幕。",
                    ToolTipIcon.Warning);
            }

            return;
        }

        if (button == HoverOpacityToggleButtonIndex && this.toggleHoverOpacityAction != null)
        {
            this.toggleHoverOpacityAction();
        }
    }

    private void HandleRestartButtonClick()
    {
        if (this.restartSingleClickTimer.Enabled)
        {
            this.restartSingleClickTimer.Stop();
            ExecuteRestartButtonDoubleClick();
            return;
        }

        this.restartSingleClickTimer.Interval = Math.Max(1, SystemInformation.DoubleClickTime);
        this.restartSingleClickTimer.Start();
    }

    private void OnRestartSingleClickTimerTick(object sender, EventArgs e)
    {
        this.restartSingleClickTimer.Stop();
        PulseSeelenDockFromOperationPanel();
    }

    private void PulseSeelenDockFromOperationPanel()
    {
        bool success = false;
        if (this.pulseSeelenDockAction != null)
        {
            try
            {
                success = this.pulseSeelenDockAction();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        if (!success)
        {
            ShowOperationNotification(
                "Seelen Dock",
                "未能找到或拉前 Seelen Dock。",
                ToolTipIcon.Warning);
        }
    }

    private void ExecuteRestartButtonDoubleClick()
    {
        if (this.restartAction == null)
        {
            return;
        }

        RestartSeelenUiWithApplicationIfRunning();
        this.restartAction();
    }

    private bool TryOpenSeelenPowerMenu()
    {
        bool running = IsSeelenUiRunning();
        SetSeelenUiRunning(running);
        if (!running)
        {
            Program.LogInfo("SeelenUI power menu fallback: seelen-ui is not running.");
            return false;
        }

        string cliPath = FindSeelenCliPath();
        if (string.IsNullOrEmpty(cliPath))
        {
            Program.LogInfo("SeelenUI power menu fallback: slu.exe was not found.");
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = cliPath;
            startInfo.Arguments = "widget trigger " + SeelenPowerMenuWidgetId;
            startInfo.WorkingDirectory = Path.GetDirectoryName(cliPath);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            Process process = Process.Start(startInfo);
            if (process == null)
            {
                Program.LogInfo("SeelenUI power menu fallback: Process.Start returned null for " + cliPath);
                return false;
            }

            using (process)
            {
                if (!process.WaitForExit(1500))
                {
                    Program.LogInfo("SeelenUI power menu trigger is still running; assuming it was accepted.");
                    return true;
                }

                if (process.ExitCode == 0)
                {
                    Program.LogInfo("SeelenUI power menu triggered through " + cliPath);
                    return true;
                }

                Program.LogInfo(
                    "SeelenUI power menu fallback: slu.exe exited with code " +
                    process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    private void RestartSeelenUiWithApplicationIfRunning()
    {
        bool wasRunning = IsSeelenUiRunning();
        SetSeelenUiRunning(wasRunning);
        if (!wasRunning)
        {
            Program.LogInfo("SeelenUI restart skipped during application restart because seelen-ui is not running.");
            return;
        }

        string exePath = FindRunningSeelenUiExecutablePath();
        if (string.IsNullOrEmpty(exePath))
        {
            exePath = FindInstalledSeelenUiExecutablePath();
        }

        if (string.IsNullOrEmpty(exePath))
        {
            Program.LogInfo("SeelenUI restart skipped during application restart because seelen-ui.exe path was not found.");
            return;
        }

        Program.LogInfo("SeelenUI restart requested during application restart. Path=" + exePath);
        int killed = KillSeelenUiProcesses();
        Thread.Sleep(350);
        bool started = TryStartSeelenUi(exePath);
        Program.LogInfo(
            "SeelenUI restart finished during application restart. KilledProcesses=" +
            killed.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ", Started=" +
            started.ToString());
        SetSeelenUiRunning(started || IsSeelenUiRunning());
    }

    private static bool TryStartSeelenUi(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = exePath;
            startInfo.WorkingDirectory = Path.GetDirectoryName(exePath);
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(startInfo);
            if (process != null)
            {
                process.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static string FindSeelenCliPath()
    {
        string runningDirectory = FindRunningSeelenUiDirectory();
        string candidate = CombineExistingFile(runningDirectory, SeelenCliFileName);
        if (!string.IsNullOrEmpty(candidate))
        {
            return candidate;
        }

        candidate = CombineExistingFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SeelenInstallRelativePath),
            SeelenCliFileName);
        if (!string.IsNullOrEmpty(candidate))
        {
            return candidate;
        }

        candidate = CombineExistingFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), SeelenInstallRelativePath),
            SeelenCliFileName);
        if (!string.IsNullOrEmpty(candidate))
        {
            return candidate;
        }

        candidate = CombineExistingFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), SeelenInstallRelativePath),
            SeelenCliFileName);
        return candidate;
    }

    private static string FindRunningSeelenUiExecutablePath()
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(SeelenUiProcessName);
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                if (process == null)
                {
                    continue;
                }

                string path = NativeMethods.TryGetProcessImagePath(process.Id);
                if (string.IsNullOrEmpty(path) ||
                    !string.Equals(Path.GetFileName(path), SeelenUiExecutableName, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path))
                {
                    continue;
                }

                return path;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            DisposeProcesses(processes);
        }

        return null;
    }

    private static string FindInstalledSeelenUiExecutablePath()
    {
        string candidate = CombineExistingFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SeelenInstallRelativePath),
            SeelenUiExecutableName);
        if (!string.IsNullOrEmpty(candidate))
        {
            return candidate;
        }

        candidate = CombineExistingFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), SeelenInstallRelativePath),
            SeelenUiExecutableName);
        if (!string.IsNullOrEmpty(candidate))
        {
            return candidate;
        }

        return CombineExistingFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), SeelenInstallRelativePath),
            SeelenUiExecutableName);
    }

    private static string FindRunningSeelenUiDirectory()
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(SeelenUiProcessName);
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                if (process == null)
                {
                    continue;
                }

                string path = NativeMethods.TryGetProcessImagePath(process.Id);
                if (string.IsNullOrEmpty(path) ||
                    !string.Equals(Path.GetFileName(path), SeelenUiExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Path.GetDirectoryName(path);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            DisposeProcesses(processes);
        }

        return null;
    }

    private static string CombineExistingFile(string directory, string fileName)
    {
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, fileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private void BeginInvokeBatteryCarePause()
    {
        if (this.batteryCarePauseRunning)
        {
            return;
        }

        this.batteryCarePauseRunning = true;
        Program.LogInfo("ASUS battery care pause requested from operation panel.");
        RenderLayeredWindow();
        Task.Run((Action)delegate
        {
            string detail;
            bool success = TryInvokeAsusBatteryCarePause(out detail);
            Program.LogInfo("ASUS battery care pause invocation finished. Success=" + success + ", Detail=" + detail);
            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            this.batteryCarePauseRunning = false;
                            ShowOperationNotification(
                                "电池保养",
                                success ? "已发送解除 80% 充电限制指令。" : "解除 80% 限制失败：" + detail,
                                success ? ToolTipIcon.Info : ToolTipIcon.Warning);
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

    private void BeginInvokeBatteryLimitRestore()
    {
        if (this.batteryLimitRestoreRunning)
        {
            return;
        }

        this.batteryLimitRestoreRunning = true;
        Program.LogInfo("ASUS battery 80 percent limit restore requested from operation panel.");
        RenderLayeredWindow();
        Task.Run((Action)delegate
        {
            string detail;
            bool success = TryInvokeAsusBatteryLimitRestore(out detail);
            Program.LogInfo("ASUS battery 80 percent limit restore invocation finished. Success=" + success + ", Detail=" + detail);
            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            this.batteryLimitRestoreRunning = false;
                            ShowOperationNotification(
                                "电池保养",
                                success ? "已发送恢复 80% 充电限制指令。" : "恢复 80% 限制失败：" + detail,
                                success ? ToolTipIcon.Info : ToolTipIcon.Warning);
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

    private void ShowOperationNotification(string title, string message, ToolTipIcon icon)
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

    private void RefreshMyAsusInstallStatus()
    {
        bool installed = DetectMyAsusInstalled();
        if (this.myAsusInstalled == installed)
        {
            UpdateForegroundFpsTimer();
            RenderLayeredWindow();
            return;
        }

        this.myAsusInstalled = installed;
        Program.LogInfo("MyASUS install status refreshed from operation panel. Installed=" + installed.ToString());
        if (!ShouldShowBatteryCareButtons() &&
            (this.hoveredButton == BatteryCarePauseButtonIndex || this.hoveredButton == BatteryLimitRestoreButtonIndex))
        {
            this.hoveredButton = -1;
            HideHoverToolTip();
        }

        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        PositionOperationWindow();
        UpdateForegroundFpsTimer();
        RenderLayeredWindow();
    }

    private void RefreshSystemButtonAvailability()
    {
        bool aiStudioAvailable = NativeMethods.IsWindowsAiStudioAvailable();
        bool liveCaptionsAvailable = NativeMethods.IsLiveCaptionsAvailable();
        if (this.windowsAiStudioAvailable == aiStudioAvailable &&
            this.liveCaptionsAvailable == liveCaptionsAvailable)
        {
            return;
        }

        this.windowsAiStudioAvailable = aiStudioAvailable;
        this.liveCaptionsAvailable = liveCaptionsAvailable;
        Program.LogInfo(
            "Operation panel system entry availability refreshed. AiStudioAvailable=" +
            aiStudioAvailable.ToString() +
            ", LiveCaptionsAvailable=" +
            liveCaptionsAvailable.ToString());
        if (!IsButtonInteractive(this.pressedButton))
        {
            this.pressedButton = -1;
        }

        RenderLayeredWindow();
    }

    private static bool TryInvokeAsusBatteryCarePause(out string detail)
    {
        if (TryStartAsusKeyboardHostDirect(AsusBatteryCarePauseArguments, out detail))
        {
            return true;
        }

        string directFailure = detail;
        if (TryStartAsusKeyboardHostAlias(AsusBatteryCarePauseArguments, out detail))
        {
            detail = "Alias fallback succeeded after direct failure: " + directFailure;
            return true;
        }

        detail = "Direct failure: " + directFailure + "; Alias failure: " + detail;
        return false;
    }

    private static bool TryInvokeAsusBatteryLimitRestore(out string detail)
    {
        if (TryStartAsusKeyboardHostDirect(AsusBatteryLimitRestoreArguments, out detail))
        {
            return true;
        }

        string directFailure = detail;
        if (TryStartAsusKeyboardHostAlias(AsusBatteryLimitRestoreArguments, out detail))
        {
            detail = "Alias fallback succeeded after direct failure: " + directFailure;
            return true;
        }

        detail = "Direct failure: " + directFailure + "; Alias failure: " + detail;
        return false;
    }

    private static bool TryStartAsusKeyboardHostDirect(string arguments, out string detail)
    {
        string installLocation = FindAsusAssistantInstallLocation();
        if (string.IsNullOrEmpty(installLocation))
        {
            detail = "MyASUS package was not found.";
            return false;
        }

        string hostPath = Path.Combine(installLocation, AsusKeyboardHostRelativePath);
        if (!File.Exists(hostPath))
        {
            detail = "AsusKeyboardHost.exe was not found: " + hostPath;
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = hostPath;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = Path.GetDirectoryName(hostPath);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(startInfo);
            if (process != null)
            {
                detail = "Started " + hostPath + " " + arguments + ", Pid=" + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                process.Dispose();
                return true;
            }

            detail = "Process.Start returned null for " + hostPath;
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool TryStartAsusKeyboardHostAlias(string arguments, out string detail)
    {
        string aliasPath = GetAsusKeyboardHostAliasPath();
        string fileName = File.Exists(aliasPath) ? aliasPath : AsusKeyboardHostAlias;
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(startInfo);
            if (process != null)
            {
                detail = "Started alias " + fileName + " " + arguments + ", Pid=" + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                process.Dispose();
                return true;
            }

            detail = "Process.Start returned null for alias " + fileName;
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool DetectMyAsusInstalled()
    {
        if (!string.IsNullOrEmpty(FindAsusAssistantInstallLocation()))
        {
            return true;
        }

        string aliasPath = GetAsusKeyboardHostAliasPath();
        if (!string.IsNullOrEmpty(aliasPath) && File.Exists(aliasPath))
        {
            return true;
        }

        return IsAsusAssistantPackageRegistered();
    }

    private static string GetAsusKeyboardHostAliasPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps",
            AsusKeyboardHostAlias);
    }

    private static string FindAsusAssistantInstallLocation()
    {
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (!Directory.Exists(windowsApps))
        {
            return null;
        }

        try
        {
            DirectoryInfo best = null;
            DirectoryInfo root = new DirectoryInfo(windowsApps);
            DirectoryInfo[] candidates = root.GetDirectories(AsusAssistantPackagePrefix + "*" + AsusAssistantPackageSuffix);
            for (int i = 0; i < candidates.Length; i++)
            {
                DirectoryInfo candidate = candidates[i];
                string hostPath = Path.Combine(candidate.FullName, AsusKeyboardHostRelativePath);
                if (!File.Exists(hostPath))
                {
                    continue;
                }

                if (best == null ||
                    string.Compare(candidate.Name, best.Name, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    best = candidate;
                }
            }

            return best == null ? null : best.FullName;
        }
        catch (UnauthorizedAccessException)
        {
            // WindowsApps is normally ACL-protected for non-elevated processes.
            // Treat that expected condition as "direct path unavailable" and use the alias fallback.
            return null;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    private static bool IsAsusAssistantPackageRegistered()
    {
        return RegistryContainsAsusAssistantPackage(
            Microsoft.Win32.Registry.CurrentUser,
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages") ||
            RegistryContainsAsusAssistantPackage(
                Microsoft.Win32.Registry.CurrentUser,
                @"Software\Classes\ActivatableClasses\Package") ||
            RegistryContainsAsusAssistantPackage(
                Microsoft.Win32.Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications") ||
            RegistryContainsAsusAssistantPackage(
                Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");
    }

    private static bool RegistryContainsAsusAssistantPackage(Microsoft.Win32.RegistryKey root, string path)
    {
        if (root == null || string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            using (Microsoft.Win32.RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null)
                {
                    return false;
                }

                string[] names = key.GetSubKeyNames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (IsAsusAssistantPackageName(names[i]))
                    {
                        return true;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool IsAsusAssistantPackageName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
            name.StartsWith(AsusAssistantPackagePrefix, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(AsusAssistantPackageSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectAsusZenbookDevice()
    {
        List<string> values = new List<string>();
        AddWmiValues(
            values,
            "SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem",
            new string[] { "Manufacturer", "Model", "SystemFamily" });
        AddWmiValues(
            values,
            "SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct",
            new string[] { "Vendor", "Name", "Version" });

        bool asus = false;
        bool zenbook = false;
        for (int i = 0; i < values.Count; i++)
        {
            string value = values[i];
            asus |= IsAsusHardwareName(value);
            zenbook |= IsZenbookHardwareName(value);
        }

        return asus && zenbook;
    }

    private static void AddWmiValues(List<string> values, string query, string[] propertyNames)
    {
        if (values == null || string.IsNullOrEmpty(query) || propertyNames == null)
        {
            return;
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        for (int i = 0; i < propertyNames.Length; i++)
                        {
                            object rawValue = item[propertyNames[i]];
                            if (rawValue == null)
                            {
                                continue;
                            }

                            string value = rawValue.ToString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                values.Add(value.Trim());
                            }
                        }
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            Program.LogException(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Program.LogException(ex);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static bool IsAsusHardwareName(string value)
    {
        return !string.IsNullOrEmpty(value) &&
            (value.IndexOf("ASUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
             value.IndexOf("ASUSTeK", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsZenbookHardwareName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.IndexOf("Zenbook", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("UX3407", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("UX3607", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int KillSeelenUiProcesses()
    {
        int killed = 0;
        int currentId = Process.GetCurrentProcess().Id;
        DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(4.0);
        do
        {
            bool killedThisPass = false;
            Process[] processes = null;
            try
            {
                processes = Process.GetProcesses();
                for (int i = 0; i < processes.Length; i++)
                {
                    Process process = processes[i];
                    if (process == null || process.Id == currentId)
                    {
                        continue;
                    }

                    if (!IsSeelenUiProcess(process))
                    {
                        continue;
                    }

                    try
                    {
                        if (TryKillProcess(process))
                        {
                            killed++;
                            killedThisPass = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogException(ex);
                    }
                }
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

            if (DateTime.UtcNow >= deadlineUtc)
            {
                break;
            }

            Thread.Sleep(killedThisPass ? 120 : 200);
        }
        while (DateTime.UtcNow < deadlineUtc);

        RunTaskkill(false);
        if (IsSluServiceStillRunning())
        {
            RunTaskkill(true);
        }

        return killed;
    }

    private static bool TryKillProcess(Process process)
    {
        try
        {
            process.Kill();
            try
            {
                process.WaitForExit(250);
            }
            catch
            {
            }

            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    private static void RunTaskkill(bool elevated)
    {
        string arguments = "/c taskkill /F /T /IM seelen-ui.exe & taskkill /F /T /IM slu-service.exe";
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            if (elevated)
            {
                startInfo.Verb = "runas";
                startInfo.WindowStyle = ProcessWindowStyle.Normal;
            }

            Process process = Process.Start(startInfo);
            if (process != null && !elevated)
            {
                process.WaitForExit(2000);
                process.Dispose();
            }

            Program.LogInfo(elevated ? "Started elevated taskkill for SeelenUI." : "Ran taskkill for SeelenUI.");
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static bool IsSluServiceStillRunning()
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName("slu-service");
            return processes != null && processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
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
    }

    private static bool IsSeelenUiRunning()
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(SeelenUiProcessName);
            return processes != null && processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static void DisposeProcesses(Process[] processes)
    {
        if (processes == null)
        {
            return;
        }

        for (int i = 0; i < processes.Length; i++)
        {
            if (processes[i] != null)
            {
                processes[i].Dispose();
            }
        }
    }

    private static bool IsSeelenUiProcess(Process process)
    {
        string name = string.Empty;
        try
        {
            name = process.ProcessName;
        }
        catch
        {
        }

        if (IsSeelenProcessName(name))
        {
            return true;
        }

        string path = NativeMethods.TryGetProcessImagePath(process.Id);
        return ContainsSeelen(path);
    }

    private static bool IsSeelenProcessName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return ContainsSeelen(value) ||
            string.Equals(value, "slu-service", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "slu-service.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSeelen(string value)
    {
        return !string.IsNullOrEmpty(value) &&
            value.IndexOf("seelen", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsButtonVisible(int button)
    {
        if (button < 0 || button >= ButtonCount)
        {
            return false;
        }

        if ((button == BatteryCarePauseButtonIndex || button == BatteryLimitRestoreButtonIndex) &&
            !ShouldShowBatteryCareButtons())
        {
            return false;
        }

        return true;
    }

    private bool IsButtonEnabled(int button)
    {
        if (!IsButtonVisible(button))
        {
            return false;
        }

        if (IsButtonUnavailable(button))
        {
            return false;
        }

        if (button == BatteryCarePauseButtonIndex)
        {
            return !this.batteryCarePauseRunning;
        }

        if (button == BatteryLimitRestoreButtonIndex)
        {
            return !this.batteryLimitRestoreRunning;
        }

        return true;
    }

    private bool IsButtonInteractive(int button)
    {
        return IsButtonEnabled(button);
    }

    private bool IsButtonUnavailable(int button)
    {
        if (button == WindowsAiStudioButtonIndex)
        {
            return !this.windowsAiStudioAvailable;
        }

        if (button == LiveCaptionsButtonIndex)
        {
            return !this.liveCaptionsAvailable;
        }

        return false;
    }

    private bool IsStateButtonActive(int button)
    {
        if (button == HoverOpacityToggleButtonIndex)
        {
            return this.currentSettings.ForceHoverOpacityActive;
        }

        return false;
    }

    private bool ShouldShowBatteryCareButtons()
    {
        return this.isAsusZenbookDevice &&
            this.myAsusInstalled &&
            !this.currentSettings.ForceShowForegroundFpsEnabled;
    }

    private bool ShouldDrawFpsPanel()
    {
        return !ShouldShowBatteryCareButtons();
    }

    private bool AcceptsMouseButton(int button, MouseButtons mouseButton)
    {
        if (!IsButtonEnabled(button))
        {
            return false;
        }

        if (button == StartButtonIndex)
        {
            return mouseButton == MouseButtons.Left || mouseButton == MouseButtons.Right;
        }

        return mouseButton == MouseButtons.Left;
    }

    private int HitTest(Point point)
    {
        RectangleF[] rects = GetButtonRects();
        for (int i = 0; i < rects.Length; i++)
        {
            if (!IsButtonVisible(i))
            {
                continue;
            }

            if (rects[i].Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    private RectangleF[] GetButtonRects()
    {
        int margin = S(3);
        int startSize = GetStartButtonSize();
        int smallSize = GetSmallButtonSize();
        RectangleF[] rects = new RectangleF[ButtonCount];
        rects[StartButtonIndex] = new RectangleF(margin, margin, startSize, startSize);

        int columnLeft = margin + startSize;
        rects[WindowsSettingsButtonIndex] = new RectangleF(columnLeft, margin, smallSize, smallSize);
        rects[WindowsPowerMenuButtonIndex] = new RectangleF(columnLeft, margin + smallSize, smallSize, smallSize);

        columnLeft += smallSize;
        rects[HoverOpacityToggleButtonIndex] = new RectangleF(columnLeft, margin, smallSize, smallSize);
        rects[RefreshButtonIndex] = new RectangleF(columnLeft, margin + smallSize, smallSize, smallSize);

        columnLeft += smallSize;
        rects[AppSettingsButtonIndex] = new RectangleF(columnLeft, margin, smallSize, smallSize);
        rects[TaskManagerButtonIndex] = new RectangleF(columnLeft, margin + smallSize, smallSize, smallSize);

        columnLeft += smallSize;
        rects[RestartButtonIndex] = new RectangleF(columnLeft, margin, smallSize, smallSize);
        rects[WindowsQuickSettingsButtonIndex] = new RectangleF(columnLeft, margin + smallSize, smallSize, smallSize);

        columnLeft += smallSize;
        rects[BatteryCarePauseButtonIndex] = new RectangleF(columnLeft, margin, smallSize, smallSize);
        rects[LiveCaptionsButtonIndex] = new RectangleF(columnLeft, margin + smallSize, smallSize, smallSize);

        columnLeft += smallSize;
        rects[BatteryLimitRestoreButtonIndex] = new RectangleF(columnLeft, margin, smallSize, smallSize);
        rects[WindowsAiStudioButtonIndex] = new RectangleF(columnLeft, margin + smallSize, smallSize, smallSize);
        return rects;
    }

    private Size GetDesiredSize()
    {
        int margin = S(3);
        int startSize = GetStartButtonSize();
        int smallSize = GetSmallButtonSize();
        return new Size(margin * 2 + startSize + smallSize * SmallColumnCount, margin * 2 + startSize);
    }

    private int GetStartButtonSize()
    {
        return Math.Max(WidgetSettings.MinOperationButtonSize, Math.Min(WidgetSettings.MaxOperationButtonSize, this.currentSettings.OperationButtonSize));
    }

    private int GetSmallButtonSize()
    {
        return Math.Max(S(18), (int)Math.Round(GetStartButtonSize() / 2.0f));
    }

    private void PositionOperationWindow()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int left = workArea.Left + Math.Max(0, this.currentSettings.OperationLeftOffset);
        int top = workArea.Bottom - this.Height - Math.Max(0, this.currentSettings.OperationBottomOffset);
        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - this.Width));
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        Point shiftedLocation = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.OperationPanelSalt);
        left = shiftedLocation.X;
        top = shiftedLocation.Y;
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private bool UpdateAnimationState()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.animationLastUtc == DateTime.MinValue ? 0.016 : (now - this.animationLastUtc).TotalSeconds;
        this.animationLastUtc = now;
        double step = Math.Max(0.02, elapsed * HoverStepPerSecond);
        bool changed = false;
        for (int i = 0; i < this.hoverProgress.Length; i++)
        {
            double target = i == this.hoveredButton && IsButtonEnabled(i) ? 1.0 : 0.0;
            double old = this.hoverProgress[i];
            if (this.hoverProgress[i] < target)
            {
                this.hoverProgress[i] = Math.Min(target, this.hoverProgress[i] + step);
            }
            else if (this.hoverProgress[i] > target)
            {
                this.hoverProgress[i] = Math.Max(target, this.hoverProgress[i] - step);
            }

            changed |= Math.Abs(old - this.hoverProgress[i]) > 0.001;
        }

        return changed || IsPressAnimationActive(now);
    }

    private bool NeedsAnimationTimer()
    {
        if (IsButtonEnabled(this.hoveredButton) || IsButtonEnabled(this.pressedButton))
        {
            return true;
        }

        if (IsPressAnimationActive(DateTime.UtcNow))
        {
            return true;
        }

        for (int i = 0; i < this.hoverProgress.Length; i++)
        {
            if (this.hoverProgress[i] > 0.001)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPressAnimationActive(DateTime now)
    {
        return this.pressAnimationButton >= 0 &&
            this.pressAnimationStartUtc != DateTime.MinValue &&
            (now - this.pressAnimationStartUtc).TotalMilliseconds < PressAnimationMs;
    }

    private double GetPressProgress(int button)
    {
        if (button != this.pressAnimationButton || this.pressAnimationStartUtc == DateTime.MinValue)
        {
            return 0.0;
        }

        double elapsed = (DateTime.UtcNow - this.pressAnimationStartUtc).TotalMilliseconds;
        double progress = Math.Max(0.0, Math.Min(1.0, elapsed / PressAnimationMs));
        if (progress >= 1.0)
        {
            return 0.0;
        }

        return 1.0 - progress;
    }

    private void EnsureAnimationTimer()
    {
        if (!this.animationTimer.Enabled)
        {
            this.animationLastUtc = DateTime.UtcNow;
            this.animationTimer.Start();
        }
    }

    private void DrawOperationWindow(Graphics g)
    {
        ConfigureGraphics(g);
        RectangleF[] rects = GetButtonRects();
        DrawButton(g, rects[StartButtonIndex], StartButtonIndex, true, false, false, true);
        DrawButton(g, rects[WindowsSettingsButtonIndex], WindowsSettingsButtonIndex, false, false, false, false);
        DrawButton(g, rects[HoverOpacityToggleButtonIndex], HoverOpacityToggleButtonIndex, false, false, false, false);
        DrawButton(g, rects[AppSettingsButtonIndex], AppSettingsButtonIndex, false, false, false, false);
        DrawButton(g, rects[RestartButtonIndex], RestartButtonIndex, false, false, false, false);
        if (ShouldShowBatteryCareButtons())
        {
            DrawButton(g, rects[BatteryCarePauseButtonIndex], BatteryCarePauseButtonIndex, false, false, false, false);
            DrawButton(g, rects[BatteryLimitRestoreButtonIndex], BatteryLimitRestoreButtonIndex, false, true, false, false);
        }
        else if (ShouldDrawFpsPanel())
        {
            DrawFpsPanel(g, GetBatteryCareFallbackRect(rects));
        }

        DrawButton(g, rects[WindowsPowerMenuButtonIndex], WindowsPowerMenuButtonIndex, false, false, false, false);
        DrawButton(g, rects[RefreshButtonIndex], RefreshButtonIndex, false, false, false, false);
        DrawButton(g, rects[TaskManagerButtonIndex], TaskManagerButtonIndex, false, false, false, false);
        DrawButton(g, rects[WindowsQuickSettingsButtonIndex], WindowsQuickSettingsButtonIndex, false, false, false, false);
        DrawButton(g, rects[LiveCaptionsButtonIndex], LiveCaptionsButtonIndex, false, false, false, false);
        DrawButton(g, rects[WindowsAiStudioButtonIndex], WindowsAiStudioButtonIndex, false, false, true, false);
    }

    private static RectangleF GetBatteryCareFallbackRect(RectangleF[] rects)
    {
        RectangleF left = rects[BatteryCarePauseButtonIndex];
        RectangleF right = rects[BatteryLimitRestoreButtonIndex];
        return RectangleF.FromLTRB(left.Left, left.Top, right.Right, left.Bottom);
    }

    private void DrawFpsPanel(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        int backgroundAlpha = GetBackgroundOpacityAlpha();
        float radius = Math.Max(S(5), rect.Height * 0.24f);
        bool forcedFps = this.currentSettings.ForceShowForegroundFpsEnabled;
        using (GraphicsPath path = RoundedSegment(rect, radius, false, true, false))
        using (SolidBrush fillBrush = new SolidBrush(forcedFps
            ? DesignTokens.WithAlpha(DesignTokens.Colors.AccentSoft, ScaleAlpha(72, backgroundAlpha))
            : DesignTokens.White(ScaleAlpha(46, backgroundAlpha))))
        using (Pen borderPen = new Pen(forcedFps
            ? DesignTokens.WithAlpha(DesignTokens.Colors.AccentBorder, ScaleAlpha(132, backgroundAlpha))
            : DesignTokens.White(ScaleAlpha(52, backgroundAlpha)), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(fillBrush, path);
            g.DrawPath(borderPen, path);
        }

        if (forcedFps)
        {
            RectangleF ringRect = RectangleF.Inflate(rect, -Math.Max(1.0f, this.scale), -Math.Max(1.0f, this.scale));
            using (GraphicsPath ringPath = RoundedSegment(ringRect, Math.Max(S(4), ringRect.Height * 0.22f), false, true, false))
            using (Pen ringPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, ScaleAlpha(150, backgroundAlpha)), Math.Max(1.0f, this.scale)))
            {
                g.DrawPath(ringPen, ringPath);
            }
        }

        string text = this.foregroundFrameRate.HasValue
            ? "FPS=" + this.foregroundFrameRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "FPS=-";
        RectangleF textRect = RectangleF.Inflate(rect, -Math.Max(S(3), rect.Height * 0.12f), -Math.Max(1.0f, rect.Height * 0.10f));
        using (Font font = DesignTokens.CreateMonoFont(Math.Max(7.0f, Math.Min(rect.Height * 0.42f, rect.Width * 0.17f)), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush textBrush = new SolidBrush(forcedFps
            ? DesignTokens.TextStrong(238)
            : DesignTokens.White(226)))
        {
            DrawCenteredFittedText(g, text, font, textBrush, textRect);
        }
    }

    private void DrawCenteredFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 6.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.5f * this.scale;
                drawFont = DesignTokens.CreateMonoFont(size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);
            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private void DrawButton(Graphics g, RectangleF rect, int button, bool leftSegment, bool topRight, bool bottomRight, bool startButton)
    {
        if (!IsButtonVisible(button) || rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        double hover = this.hoverProgress[button];
        double press = button == this.pressedButton ? 1.0 : GetPressProgress(button);
        bool unavailable = IsButtonUnavailable(button);
        bool active = !unavailable && IsStateButtonActive(button);
        if (unavailable)
        {
            hover = 0.0;
            press = 0.0;
        }

        int backgroundAlpha = GetBackgroundOpacityAlpha();
        int fillAlpha = ScaleAlpha(ClampByte((int)Math.Round(58 + hover * 54 + press * 36)), backgroundAlpha);
        int outlineAlpha = ScaleAlpha(ClampByte((int)Math.Round(44 + hover * 70 + press * 40)), backgroundAlpha);
        Color fill;
        if (unavailable)
        {
            fill = DesignTokens.WithAlpha(
                DesignTokens.Colors.Control,
                ScaleAlpha(ClampByte((int)Math.Round(34 + press * 16)), backgroundAlpha));
            outlineAlpha = ScaleAlpha(44, backgroundAlpha);
        }
        else if (button == BatteryCarePauseButtonIndex)
        {
            fill = this.batteryCarePauseRunning
                ? DesignTokens.WithAlpha(DesignTokens.Colors.Warning, ClampByte(fillAlpha + ScaleAlpha(22, backgroundAlpha)))
                : DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, ScaleAlpha(ClampByte((int)Math.Round(42 + hover * 58 + press * 40)), backgroundAlpha));
        }
        else if (button == BatteryLimitRestoreButtonIndex)
        {
            fill = this.batteryLimitRestoreRunning
                ? DesignTokens.WithAlpha(DesignTokens.Colors.DangerDeep, ClampByte(fillAlpha + ScaleAlpha(28, backgroundAlpha)))
                : DesignTokens.WithAlpha(DesignTokens.Colors.Danger, ScaleAlpha(ClampByte((int)Math.Round(68 + hover * 64 + press * 44)), backgroundAlpha));
        }
        else if (button == StartButtonIndex)
        {
            fill = DesignTokens.Accent(ClampByte(fillAlpha + 6));
        }
        else if (active)
        {
            fill = DesignTokens.WithAlpha(
                Color.FromArgb(178, 225, 255),
                ScaleAlpha(ClampByte((int)Math.Round(92 + hover * 66 + press * 42)), backgroundAlpha));
        }
        else if (button == LiveCaptionsButtonIndex || button == WindowsAiStudioButtonIndex)
        {
            fill = DesignTokens.WithAlpha(
                Color.FromArgb(255, 236, 170),
                ScaleAlpha(ClampByte((int)Math.Round(74 + hover * 66 + press * 40)), backgroundAlpha));
        }
        else
        {
            fill = DesignTokens.White(fillAlpha);
        }

        Color border = active
            ? DesignTokens.WithAlpha(DesignTokens.Colors.AccentBorder, ClampByte(outlineAlpha + ScaleAlpha(72, backgroundAlpha)))
            : DesignTokens.White(outlineAlpha);
        float radius = Math.Max(S(5), rect.Height * 0.24f);
        using (GraphicsPath path = RoundedSegment(rect, radius, leftSegment, topRight, bottomRight))
        {
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            using (Pen pen = new Pen(border, Math.Max(1.0f, this.scale)))
            {
                g.DrawPath(pen, path);
            }

            if (active)
            {
                RectangleF ringRect = RectangleF.Inflate(rect, -Math.Max(1.0f, this.scale), -Math.Max(1.0f, this.scale));
                using (GraphicsPath ringPath = RoundedSegment(ringRect, Math.Max(S(4), ringRect.Height * 0.22f), leftSegment, topRight, bottomRight))
                using (Pen ringPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, ScaleAlpha(154, backgroundAlpha)), Math.Max(1.0f, this.scale)))
                {
                    g.DrawPath(ringPen, ringPath);
                }
            }
        }

        RectangleF iconRect = GetIconRect(rect);
        if (startButton)
        {
            DrawStartGlyph(g, iconRect);
        }
        else if (button == WindowsSettingsButtonIndex)
        {
            DrawSettingsGlyph(g, iconRect);
        }
        else if (button == WindowsPowerMenuButtonIndex)
        {
            DrawPowerGlyph(g, iconRect);
        }
        else if (button == AppSettingsButtonIndex)
        {
            DrawAppSettingsGlyph(g, iconRect);
        }
        else if (button == RefreshButtonIndex)
        {
            DrawRefreshGlyph(g, iconRect);
        }
        else if (button == RestartButtonIndex)
        {
            DrawRestartGlyph(g, iconRect);
        }
        else if (button == BatteryCarePauseButtonIndex)
        {
            DrawBatteryCareGlyph(g, iconRect);
        }
        else if (button == BatteryLimitRestoreButtonIndex)
        {
            DrawBatteryLimitRestoreGlyph(g, iconRect);
        }
        else if (button == TaskManagerButtonIndex)
        {
            DrawTaskManagerGlyph(g, iconRect);
        }
        else if (button == WindowsAiStudioButtonIndex)
        {
            DrawWindowsAiStudioGlyph(g, iconRect);
        }
        else if (button == WindowsQuickSettingsButtonIndex)
        {
            DrawQuickSettingsGlyph(g, iconRect);
        }
        else if (button == LiveCaptionsButtonIndex)
        {
            DrawLiveCaptionsGlyph(g, iconRect);
        }
        else if (button == HoverOpacityToggleButtonIndex)
        {
            DrawHoverOpacityGlyph(g, iconRect);
        }

        if (unavailable)
        {
            DrawUnavailableButtonOverlay(g, rect, leftSegment, topRight, bottomRight);
        }
    }

    private void DrawUnavailableButtonOverlay(Graphics g, RectangleF rect, bool leftSegment, bool topRight, bool bottomRight)
    {
        int backgroundAlpha = GetBackgroundOpacityAlpha();
        float radius = Math.Max(S(5), rect.Height * 0.24f);
        using (GraphicsPath path = RoundedSegment(rect, radius, leftSegment, topRight, bottomRight))
        using (SolidBrush veilBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(116, backgroundAlpha))))
        using (Pen mutedPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ScaleAlpha(118, backgroundAlpha)), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(veilBrush, path);
            g.DrawPath(mutedPen, path);
        }
    }

    private RectangleF GetIconRect(RectangleF tileRect)
    {
        float inset = Math.Max(S(4), tileRect.Height * 0.20f);
        return new RectangleF(
            tileRect.Left + inset,
            tileRect.Top + inset,
            Math.Max(1.0f, tileRect.Width - inset * 2.0f),
            Math.Max(1.0f, tileRect.Height - inset * 2.0f));
    }

    private void DrawStartGlyph(Graphics g, RectangleF icon)
    {
        float gap = Math.Max(2.0f, icon.Width * 0.07f);
        float paneWidth = (icon.Width - gap) / 2.0f;
        float paneHeight = (icon.Height - gap) / 2.0f;
        using (LinearGradientBrush brush = new LinearGradientBrush(icon, DesignTokens.White(248), DesignTokens.WithAlpha(DesignTokens.Colors.AccentGradientEnd, 248), LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(brush, icon.Left, icon.Top, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left + paneWidth + gap, icon.Top, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left, icon.Top + paneHeight + gap, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left + paneWidth + gap, icon.Top + paneHeight + gap, paneWidth, paneHeight);
        }
    }

    private void DrawSettingsGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float radius = Math.Min(rect.Width, rect.Height) * 0.33f;
        float innerRadius = radius * 0.36f;
        using (SolidBrush brush = new SolidBrush(DesignTokens.Glyph(240)))
        using (Pen pen = new Pen(DesignTokens.Glyph(240), Math.Max(1.1f, 1.55f * this.scale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            for (int i = 0; i < 8; i++)
            {
                double angle = Math.PI * 2.0 * i / 8.0;
                float x1 = cx + (float)Math.Cos(angle) * radius * 0.78f;
                float y1 = cy + (float)Math.Sin(angle) * radius * 0.78f;
                float x2 = cx + (float)Math.Cos(angle) * radius * 1.10f;
                float y2 = cy + (float)Math.Sin(angle) * radius * 1.10f;
                g.DrawLine(pen, x1, y1, x2, y2);
            }

            RectangleF outer = new RectangleF(cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
            RectangleF inner = new RectangleF(cx - innerRadius, cy - innerRadius, innerRadius * 2.0f, innerRadius * 2.0f);
            g.DrawEllipse(pen, outer);
            g.FillEllipse(brush, inner);
        }
    }

    private void DrawPowerGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float radius = Math.Min(rect.Width, rect.Height) * 0.38f;
        RectangleF arc = new RectangleF(cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
        using (Pen pen = new Pen(DesignTokens.White(246), Math.Max(1.1f, 1.65f * this.scale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, arc, 130.0f, 280.0f);
            g.DrawLine(pen, cx, rect.Top + rect.Height * 0.05f, cx, cy);
        }
    }

    private void DrawAppSettingsGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float inset = size * 0.08f;
        RectangleF panel = new RectangleF(
            rect.Left + inset,
            rect.Top + inset,
            rect.Width - inset * 2.0f,
            rect.Height - inset * 2.0f);
        float titleY = panel.Top + panel.Height * 0.27f;
        float stroke = Math.Max(1.0f, 1.20f * this.scale);
        using (GraphicsPath path = RoundedRectangle(panel, Math.Max(1.5f, size * 0.12f)))
        using (Pen panelPen = new Pen(DesignTokens.White(246), stroke))
        using (Pen sliderPen = new Pen(DesignTokens.White(222), Math.Max(1.0f, 1.05f * this.scale)))
        using (SolidBrush knobBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 255)))
        {
            panelPen.StartCap = LineCap.Round;
            panelPen.EndCap = LineCap.Round;
            sliderPen.StartCap = LineCap.Round;
            sliderPen.EndCap = LineCap.Round;
            g.DrawPath(panelPen, path);
            g.DrawLine(panelPen, panel.Left, titleY, panel.Right, titleY);
            g.DrawLine(panelPen, panel.Left + panel.Width * 0.15f, panel.Top + panel.Height * 0.13f, panel.Left + panel.Width * 0.25f, panel.Top + panel.Height * 0.13f);

            float left = panel.Left + panel.Width * 0.20f;
            float right = panel.Right - panel.Width * 0.18f;
            float firstY = titleY + panel.Height * 0.20f;
            float gapY = panel.Height * 0.18f;
            for (int i = 0; i < 3; i++)
            {
                float y = firstY + gapY * i;
                float knobX = left + (right - left) * (i == 0 ? 0.32f : (i == 1 ? 0.68f : 0.48f));
                g.DrawLine(sliderPen, left, y, right, y);
                g.FillEllipse(knobBrush, knobX - size * 0.055f, y - size * 0.055f, size * 0.11f, size * 0.11f);
            }
        }
    }

    private void DrawRefreshGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float size = Math.Min(rect.Width, rect.Height);
        float tileSize = size * 0.30f;
        float gap = size * 0.10f;
        float gridSize = tileSize * 2.0f + gap;
        float left = cx - gridSize / 2.0f;
        float top = cy - gridSize / 2.0f;
        using (SolidBrush tileBrush = new SolidBrush(DesignTokens.Glyph(210)))
        using (Pen tilePen = new Pen(DesignTokens.White(246), Math.Max(1.0f, 1.15f * this.scale)))
        using (SolidBrush boltBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 255)))
        {
            for (int row = 0; row < 2; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    RectangleF tile = new RectangleF(
                        left + column * (tileSize + gap),
                        top + row * (tileSize + gap),
                        tileSize,
                        tileSize);
                    g.FillRectangle(tileBrush, tile);
                    g.DrawRectangle(tilePen, tile.X, tile.Y, tile.Width, tile.Height);
                }
            }

            g.FillPolygon(
                boltBrush,
                new PointF[]
                {
                    new PointF(cx + size * 0.04f, cy - size * 0.46f),
                    new PointF(cx - size * 0.24f, cy + size * 0.03f),
                    new PointF(cx - size * 0.03f, cy + size * 0.03f),
                    new PointF(cx - size * 0.12f, cy + size * 0.46f),
                    new PointF(cx + size * 0.25f, cy - size * 0.10f),
                    new PointF(cx + size * 0.06f, cy - size * 0.10f)
                });
        }
    }

    private void DrawRestartGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float inset = size * 0.06f;
        RectangleF window = new RectangleF(
            rect.Left + inset,
            rect.Top + inset,
            rect.Width - inset * 2.0f,
            rect.Height - inset * 2.0f);
        float titleBarY = window.Top + window.Height * 0.25f;
        float cx = window.Left + window.Width / 2.0f;
        float cy = titleBarY + (window.Bottom - titleBarY) * 0.54f;
        float radius = Math.Min(window.Width, window.Bottom - titleBarY) * 0.27f;
        RectangleF arc = new RectangleF(cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
        using (GraphicsPath path = RoundedRectangle(window, Math.Max(1.5f, size * 0.12f)))
        using (Pen windowPen = new Pen(DesignTokens.White(246), Math.Max(1.0f, 1.20f * this.scale)))
        using (Pen restartPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 255), Math.Max(1.1f, 1.50f * this.scale)))
        using (SolidBrush restartBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 255)))
        {
            windowPen.StartCap = LineCap.Round;
            windowPen.EndCap = LineCap.Round;
            restartPen.StartCap = LineCap.Round;
            restartPen.EndCap = LineCap.Round;
            g.DrawPath(windowPen, path);
            g.DrawLine(windowPen, window.Left, titleBarY, window.Right, titleBarY);
            g.DrawLine(windowPen, window.Left + window.Width * 0.16f, window.Top + window.Height * 0.12f, window.Left + window.Width * 0.24f, window.Top + window.Height * 0.12f);
            g.DrawArc(restartPen, arc, 35.0f, 285.0f);
            DrawArrowHead(
                g,
                restartBrush,
                new PointF(arc.Right + radius * 0.05f, arc.Top + radius * 0.62f),
                new PointF(arc.Right - radius * 0.48f, arc.Top + radius * 0.42f),
                new PointF(arc.Right - radius * 0.18f, arc.Top + radius * 1.00f));
        }
    }

    private void DrawBatteryCareGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.25f * this.scale);
        RectangleF body = new RectangleF(
            rect.Left + size * 0.12f,
            rect.Top + size * 0.20f,
            size * 0.66f,
            size * 0.58f);
        RectangleF cap = new RectangleF(
            body.Right + size * 0.03f,
            body.Top + body.Height * 0.28f,
            size * 0.10f,
            body.Height * 0.44f);
        using (GraphicsPath bodyPath = RoundedRectangle(body, Math.Max(1.0f, size * 0.08f)))
        using (GraphicsPath capPath = RoundedRectangle(cap, Math.Max(1.0f, size * 0.04f)))
        using (Pen pen = new Pen(DesignTokens.White(246), stroke))
        using (SolidBrush accentBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 255)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, bodyPath);
            g.FillPath(accentBrush, capPath);
            g.DrawLine(
                pen,
                body.Left + body.Width * 0.20f,
                body.Top + body.Height * 0.58f,
                body.Left + body.Width * 0.39f,
                body.Top + body.Height * 0.38f);
            g.DrawLine(
                pen,
                body.Left + body.Width * 0.39f,
                body.Top + body.Height * 0.38f,
                body.Left + body.Width * 0.59f,
                body.Top + body.Height * 0.58f);
            g.DrawLine(
                pen,
                body.Left + body.Width * 0.39f,
                body.Top + body.Height * 0.38f,
                body.Left + body.Width * 0.39f,
                body.Bottom - body.Height * 0.18f);
        }
    }

    private void DrawBatteryLimitRestoreGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.15f * this.scale);
        RectangleF body = new RectangleF(
            rect.Left + size * 0.08f,
            rect.Top + size * 0.19f,
            size * 0.68f,
            size * 0.58f);
        RectangleF cap = new RectangleF(
            body.Right + size * 0.03f,
            body.Top + body.Height * 0.28f,
            size * 0.10f,
            body.Height * 0.44f);
        using (GraphicsPath bodyPath = RoundedRectangle(body, Math.Max(1.0f, size * 0.08f)))
        using (GraphicsPath capPath = RoundedRectangle(cap, Math.Max(1.0f, size * 0.04f)))
        using (Pen pen = new Pen(DesignTokens.White(246), stroke))
        using (SolidBrush capBrush = new SolidBrush(DesignTokens.White(232)))
        using (Font font = DesignTokens.CreateUIFont(Math.Max(7.0f, size * 0.36f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.White(252)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, bodyPath);
            g.FillPath(capBrush, capPath);
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString("80", font, textBrush, body, format);
            }

            float arrowX = rect.Left + size * 0.78f;
            float top = rect.Top + size * 0.20f;
            float bottom = rect.Top + size * 0.76f;
            g.DrawLine(pen, arrowX, top, arrowX, bottom);
            DrawArrowHead(
                g,
                textBrush,
                new PointF(arrowX, bottom),
                new PointF(arrowX - size * 0.16f, bottom - size * 0.16f),
                new PointF(arrowX + size * 0.16f, bottom - size * 0.16f));
        }
    }

    private void DrawTaskManagerGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.15f * this.scale);
        RectangleF panel = new RectangleF(
            rect.Left + size * 0.10f,
            rect.Top + size * 0.11f,
            size * 0.80f,
            size * 0.78f);
        float titleBarY = panel.Top + panel.Height * 0.25f;
        using (GraphicsPath panelPath = RoundedRectangle(panel, Math.Max(1.5f, size * 0.10f)))
        using (Pen panelPen = new Pen(DesignTokens.White(244), stroke))
        using (Pen graphPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 255), Math.Max(1.0f, 1.35f * this.scale)))
        using (SolidBrush barBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 238)))
        {
            panelPen.StartCap = LineCap.Round;
            panelPen.EndCap = LineCap.Round;
            graphPen.StartCap = LineCap.Round;
            graphPen.EndCap = LineCap.Round;
            graphPen.LineJoin = LineJoin.Round;
            g.DrawPath(panelPen, panelPath);
            g.DrawLine(panelPen, panel.Left, titleBarY, panel.Right, titleBarY);
            g.DrawLine(panelPen, panel.Left + panel.Width * 0.13f, panel.Top + panel.Height * 0.12f, panel.Left + panel.Width * 0.24f, panel.Top + panel.Height * 0.12f);

            float baseY = panel.Bottom - panel.Height * 0.14f;
            float barWidth = Math.Max(1.0f, size * 0.10f);
            float gap = size * 0.055f;
            float x = panel.Left + panel.Width * 0.18f;
            float[] heights = new float[] { size * 0.18f, size * 0.31f, size * 0.24f };
            for (int i = 0; i < heights.Length; i++)
            {
                RectangleF bar = new RectangleF(x + i * (barWidth + gap), baseY - heights[i], barWidth, heights[i]);
                g.FillRectangle(barBrush, bar);
            }

            PointF p1 = new PointF(panel.Left + panel.Width * 0.60f, baseY - size * 0.08f);
            PointF p2 = new PointF(panel.Left + panel.Width * 0.70f, baseY - size * 0.30f);
            PointF p3 = new PointF(panel.Left + panel.Width * 0.83f, baseY - size * 0.18f);
            g.DrawLines(graphPen, new PointF[] { p1, p2, p3 });
        }
    }

    private void DrawWindowsAiStudioGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.15f * this.scale);
        RectangleF chip = new RectangleF(
            rect.Left + size * 0.20f,
            rect.Top + size * 0.23f,
            size * 0.60f,
            size * 0.56f);
        using (GraphicsPath chipPath = RoundedRectangle(chip, Math.Max(1.5f, size * 0.11f)))
        using (Pen chipPen = new Pen(DesignTokens.White(236), stroke))
        using (Pen pinPen = new Pen(DesignTokens.White(214), Math.Max(1.0f, 0.95f * this.scale)))
        using (Pen sparklePen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.AccentAlt, 255), Math.Max(1.0f, 1.35f * this.scale)))
        using (SolidBrush sparkleBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 255)))
        {
            chipPen.StartCap = LineCap.Round;
            chipPen.EndCap = LineCap.Round;
            pinPen.StartCap = LineCap.Round;
            pinPen.EndCap = LineCap.Round;
            sparklePen.StartCap = LineCap.Round;
            sparklePen.EndCap = LineCap.Round;
            g.DrawPath(chipPen, chipPath);

            float pinTop = chip.Top + chip.Height * 0.20f;
            float pinBottom = chip.Bottom - chip.Height * 0.20f;
            for (int i = 0; i < 3; i++)
            {
                float y = pinTop + (pinBottom - pinTop) * i / 2.0f;
                g.DrawLine(pinPen, chip.Left - size * 0.10f, y, chip.Left, y);
                g.DrawLine(pinPen, chip.Right, y, chip.Right + size * 0.10f, y);
            }

            DrawSparkle(
                g,
                sparklePen,
                sparkleBrush,
                new PointF(chip.Left + chip.Width * 0.52f, chip.Top + chip.Height * 0.48f),
                size * 0.25f);
            DrawSparkle(
                g,
                sparklePen,
                sparkleBrush,
                new PointF(rect.Left + size * 0.76f, rect.Top + size * 0.22f),
                size * 0.12f);
        }
    }

    private void DrawQuickSettingsGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.1f * this.scale);
        RectangleF panel = new RectangleF(
            rect.Left + size * 0.13f,
            rect.Top + size * 0.17f,
            size * 0.74f,
            size * 0.66f);
        RectangleF firstTile = new RectangleF(
            panel.Left + panel.Width * 0.13f,
            panel.Top + panel.Height * 0.17f,
            panel.Width * 0.30f,
            panel.Height * 0.27f);
        RectangleF secondTile = new RectangleF(
            panel.Right - panel.Width * 0.43f,
            panel.Top + panel.Height * 0.17f,
            panel.Width * 0.30f,
            panel.Height * 0.27f);
        float sliderY = panel.Top + panel.Height * 0.68f;
        float sliderLeft = panel.Left + panel.Width * 0.18f;
        float sliderRight = panel.Right - panel.Width * 0.18f;
        float knobRadius = Math.Max(1.2f, size * 0.055f);

        using (GraphicsPath panelPath = RoundedRectangle(panel, Math.Max(1.5f, size * 0.09f)))
        using (GraphicsPath firstTilePath = RoundedRectangle(firstTile, Math.Max(1.0f, firstTile.Height * 0.45f)))
        using (GraphicsPath secondTilePath = RoundedRectangle(secondTile, Math.Max(1.0f, secondTile.Height * 0.45f)))
        using (Pen panelPen = new Pen(DesignTokens.White(232), stroke))
        using (Pen linePen = new Pen(DesignTokens.White(225), Math.Max(1.0f, 1.0f * this.scale)))
        using (Pen subtlePen = new Pen(DesignTokens.White(154), Math.Max(1.0f, 0.9f * this.scale)))
        using (SolidBrush activeBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 230)))
        using (SolidBrush inactiveBrush = new SolidBrush(DesignTokens.White(82)))
        using (SolidBrush knobBrush = new SolidBrush(DesignTokens.White(244)))
        {
            panelPen.StartCap = LineCap.Round;
            panelPen.EndCap = LineCap.Round;
            linePen.StartCap = LineCap.Round;
            linePen.EndCap = LineCap.Round;
            subtlePen.StartCap = LineCap.Round;
            subtlePen.EndCap = LineCap.Round;

            g.DrawPath(panelPen, panelPath);
            g.FillPath(activeBrush, firstTilePath);
            g.FillPath(inactiveBrush, secondTilePath);
            g.DrawPath(subtlePen, firstTilePath);
            g.DrawPath(subtlePen, secondTilePath);

            g.DrawLine(linePen, sliderLeft, sliderY, sliderRight, sliderY);
            float knobX = sliderLeft + (sliderRight - sliderLeft) * 0.62f;
            g.FillEllipse(knobBrush, knobX - knobRadius, sliderY - knobRadius, knobRadius * 2.0f, knobRadius * 2.0f);

            float dotRadius = Math.Max(1.0f, size * 0.036f);
            g.FillEllipse(knobBrush, firstTile.Left + firstTile.Width * 0.50f - dotRadius, firstTile.Top + firstTile.Height * 0.50f - dotRadius, dotRadius * 2.0f, dotRadius * 2.0f);
            g.DrawLine(linePen, secondTile.Left + secondTile.Width * 0.28f, secondTile.Top + secondTile.Height * 0.50f, secondTile.Right - secondTile.Width * 0.28f, secondTile.Top + secondTile.Height * 0.50f);
        }
    }

    private void DrawLiveCaptionsGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.15f * this.scale);
        RectangleF bubble = new RectangleF(
            rect.Left + size * 0.10f,
            rect.Top + size * 0.17f,
            size * 0.80f,
            size * 0.60f);
        PointF tailTip = new PointF(bubble.Left + bubble.Width * 0.34f, bubble.Bottom + size * 0.13f);
        PointF tailLeft = new PointF(bubble.Left + bubble.Width * 0.41f, bubble.Bottom - size * 0.01f);
        PointF tailRight = new PointF(bubble.Left + bubble.Width * 0.51f, bubble.Bottom - size * 0.01f);
        using (GraphicsPath bubblePath = RoundedRectangle(bubble, Math.Max(1.5f, size * 0.12f)))
        using (Pen bubblePen = new Pen(DesignTokens.White(240), stroke))
        using (Pen textPen = new Pen(DesignTokens.White(226), Math.Max(1.0f, 1.05f * this.scale)))
        using (SolidBrush tailBrush = new SolidBrush(DesignTokens.White(222)))
        {
            bubblePen.StartCap = LineCap.Round;
            bubblePen.EndCap = LineCap.Round;
            textPen.StartCap = LineCap.Round;
            textPen.EndCap = LineCap.Round;

            g.DrawPath(bubblePen, bubblePath);
            g.FillPolygon(tailBrush, new PointF[] { tailLeft, tailTip, tailRight });

            float lineLeft = bubble.Left + bubble.Width * 0.18f;
            float lineRight = bubble.Right - bubble.Width * 0.18f;
            float lineTop = bubble.Top + bubble.Height * 0.37f;
            float lineBottom = bubble.Top + bubble.Height * 0.61f;
            g.DrawLine(textPen, lineLeft, lineTop, lineRight, lineTop);
            g.DrawLine(textPen, lineLeft, lineBottom, bubble.Left + bubble.Width * 0.67f, lineBottom);
        }
    }

    private void DrawHoverOpacityGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.1f * this.scale);
        RectangleF backPanel = new RectangleF(
            rect.Left + size * 0.18f,
            rect.Top + size * 0.16f,
            size * 0.50f,
            size * 0.50f);
        RectangleF frontPanel = new RectangleF(
            rect.Left + size * 0.32f,
            rect.Top + size * 0.31f,
            size * 0.50f,
            size * 0.50f);
        using (GraphicsPath backPath = RoundedRectangle(backPanel, Math.Max(1.5f, size * 0.09f)))
        using (GraphicsPath frontPath = RoundedRectangle(frontPanel, Math.Max(1.5f, size * 0.09f)))
        using (Pen backPen = new Pen(DesignTokens.White(150), stroke))
        using (Pen frontPen = new Pen(DesignTokens.White(238), stroke))
        using (Pen slashPen = new Pen(
            this.currentSettings.ForceHoverOpacityActive
                ? DesignTokens.WithAlpha(DesignTokens.Colors.TextOnAccent, 232)
                : DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 248),
            Math.Max(1.0f, 1.35f * this.scale)))
        using (SolidBrush frontBrush = new SolidBrush(DesignTokens.White(this.currentSettings.ForceHoverOpacityActive ? 90 : 42)))
        {
            backPen.StartCap = LineCap.Round;
            backPen.EndCap = LineCap.Round;
            frontPen.StartCap = LineCap.Round;
            frontPen.EndCap = LineCap.Round;
            slashPen.StartCap = LineCap.Round;
            slashPen.EndCap = LineCap.Round;

            g.DrawPath(backPen, backPath);
            g.FillPath(frontBrush, frontPath);
            g.DrawPath(frontPen, frontPath);
            g.DrawLine(
                slashPen,
                rect.Left + size * 0.23f,
                rect.Bottom - size * 0.23f,
                rect.Right - size * 0.18f,
                rect.Top + size * 0.18f);
        }
    }

    private static void DrawSparkle(Graphics g, Pen pen, Brush brush, PointF center, float radius)
    {
        float shortRadius = radius * 0.45f;
        g.DrawLine(pen, center.X, center.Y - radius, center.X, center.Y + radius);
        g.DrawLine(pen, center.X - radius, center.Y, center.X + radius, center.Y);
        g.DrawLine(pen, center.X - shortRadius, center.Y - shortRadius, center.X + shortRadius, center.Y + shortRadius);
        g.DrawLine(pen, center.X - shortRadius, center.Y + shortRadius, center.X + shortRadius, center.Y - shortRadius);
        g.FillEllipse(brush, center.X - radius * 0.18f, center.Y - radius * 0.18f, radius * 0.36f, radius * 0.36f);
    }

    private static void DrawArrowHead(Graphics g, Brush brush, PointF tip, PointF left, PointF right)
    {
        g.FillPolygon(brush, new PointF[] { tip, left, right });
    }

    private void RenderLayeredWindow()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            EnsureRenderBuffer();
            this.renderGraphics.Clear(Color.Transparent);
            DrawOperationWindow(this.renderGraphics);
            if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, this.renderBitmap, GetLayeredWindowOpacityAlpha()))
            {
                if (!this.layeredUpdateFailureLogged)
                {
                    this.layeredUpdateFailureLogged = true;
                    Program.LogInfo("OperationForm UpdateLayeredWindow failed; falling back to normal paint.");
                }

                this.Invalidate();
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private void UpdateForegroundFrameRate()
    {
        this.foregroundFrameRate = this.foregroundFpsReader.ReadForegroundFps();
    }

    private void UpdateForegroundFpsTimer()
    {
        if (ShouldDrawFpsPanel())
        {
            if (!this.foregroundFpsTimer.Enabled)
            {
                this.foregroundFpsTimer.Start();
            }

            UpdateForegroundFrameRate();
            return;
        }

        if (this.foregroundFpsTimer.Enabled)
        {
            this.foregroundFpsTimer.Stop();
        }

        this.foregroundFrameRate = null;
    }

    private byte GetLayeredWindowOpacityAlpha()
    {
        return (byte)(this.currentSettings.ForceHoverOpacityActive ? ForcedOperationOpacityAlpha : 255);
    }

    private void EnsureRenderBuffer()
    {
        if (this.renderBitmap != null &&
            this.renderGraphics != null &&
            this.renderBitmap.Width == this.Width &&
            this.renderBitmap.Height == this.Height)
        {
            return;
        }

        DisposeRenderBuffer();
        this.renderBitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        this.renderGraphics = Graphics.FromImage(this.renderBitmap);
    }

    private void DisposeRenderBuffer()
    {
        if (this.renderGraphics != null)
        {
            this.renderGraphics.Dispose();
            this.renderGraphics = null;
        }

        if (this.renderBitmap != null)
        {
            this.renderBitmap.Dispose();
            this.renderBitmap = null;
        }
    }

    private void ResetDisplayRenderResources()
    {
        DisposeRenderBuffer();
        this.layeredUpdateFailureLogged = false;
    }

    private void ConfigureGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private int GetBackgroundOpacityAlpha()
    {
        int transparency = Math.Max(
            WidgetSettings.MinBackgroundTransparency,
            Math.Min(WidgetSettings.MaxBackgroundTransparency, this.currentSettings.OperationBackgroundTransparencyPercent));
        int alpha = (int)Math.Round(255.0 * (100 - transparency) / 100.0);
        return ClampByte(alpha);
    }

    private static int ScaleAlpha(int value, int alpha)
    {
        return ClampByte((int)Math.Round(value * Math.Max(0, Math.Min(255, alpha)) / 255.0));
    }

    private GraphicsPath RoundedSegment(RectangleF rect, float radius, bool leftSegment, bool topRight, bool bottomRight)
    {
        bool topLeft = leftSegment;
        bool bottomLeft = leftSegment;
        return RoundedRectangle(rect, radius, topLeft, topRight, bottomRight, bottomLeft);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        return RoundedRectangle(bounds, radius, true, true, true, true);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius, bool topLeft, bool topRight, bool bottomRight, bool bottomLeft)
    {
        GraphicsPath path = new GraphicsPath();
        if (bounds.Width <= 0.0f || bounds.Height <= 0.0f)
        {
            return path;
        }

        float maxRadius = Math.Min(bounds.Width, bounds.Height) / 2.0f;
        radius = Math.Max(0.0f, Math.Min(radius, maxRadius));
        if (radius <= 0.0f)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        float diameter = radius * 2.0f;
        path.StartFigure();
        path.AddLine(bounds.Left + (topLeft ? radius : 0.0f), bounds.Top, bounds.Right - (topRight ? radius : 0.0f), bounds.Top);
        if (topRight)
        {
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270.0f, 90.0f);
        }

        path.AddLine(bounds.Right, bounds.Top + (topRight ? radius : 0.0f), bounds.Right, bounds.Bottom - (bottomRight ? radius : 0.0f));
        if (bottomRight)
        {
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0.0f, 90.0f);
        }

        path.AddLine(bounds.Right - (bottomRight ? radius : 0.0f), bounds.Bottom, bounds.Left + (bottomLeft ? radius : 0.0f), bounds.Bottom);
        if (bottomLeft)
        {
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90.0f, 90.0f);
        }

        path.AddLine(bounds.Left, bounds.Bottom - (bottomLeft ? radius : 0.0f), bounds.Left, bounds.Top + (topLeft ? radius : 0.0f));
        if (topLeft)
        {
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180.0f, 90.0f);
        }

        path.CloseFigure();
        return path;
    }

    private int S(int value)
    {
        return Math.Max(1, (int)Math.Round(value * this.scale));
    }

    private static int ClampByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return value;
    }
}
