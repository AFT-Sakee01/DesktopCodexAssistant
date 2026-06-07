using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class OperationForm : Form
{
    private const int ButtonCount = 9;
    private const int StartButtonIndex = 0;
    private const int WindowsSettingsButtonIndex = 1;
    private const int WindowsPowerMenuButtonIndex = 2;
    private const int RefreshButtonIndex = 3;
    private const int RestartButtonIndex = 4;
    private const int BatteryCarePauseButtonIndex = 5;
    private const int BatteryLimitRestoreButtonIndex = 6;
    private const int AppSettingsButtonIndex = 7;
    private const int SeelenExitButtonIndex = 8;
    private const string AsusAssistantPackagePrefix = "B9ECED6F.ASUSPCAssistant_";
    private const string AsusAssistantPackageSuffix = "_qmba6cd70vzyy";
    private const string AsusKeyboardHostRelativePath = @"HwAdjustPage\ATK Package\AsusKeyboardHost.exe";
    private const string AsusKeyboardHostAlias = "B9ECED6F.ASUSPCAssistant.AsusKeyboardHost.exe";
    private const string AsusBatteryCarePauseArguments = "-HWSettingsToast acin_set";
    private const string AsusBatteryLimitRestoreArguments = "-HWSettingsToast acin80";
    private const double HoverStepPerSecond = 7.5;
    private const double PressAnimationMs = 150.0;
    private readonly Action openSettingsAction;
    private readonly Action forceRefreshAction;
    private readonly Action restartAction;
    private readonly Action<string, string, ToolTipIcon> notificationAction;
    private readonly System.Windows.Forms.Timer animationTimer;
    private readonly ToolTip hoverToolTip;
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private int hoveredButton = -1;
    private int toolTipButton = -1;
    private int pressedButton = -1;
    private int pressAnimationButton = -1;
    private bool seelenExitRunning;
    private bool batteryCarePauseRunning;
    private bool batteryLimitRestoreRunning;
    private DateTime animationLastUtc;
    private DateTime pressAnimationStartUtc;
    private readonly double[] hoverProgress = new double[ButtonCount];
    private Bitmap renderBitmap;
    private Graphics renderGraphics;

    public OperationForm(WidgetSettings settings, Action openSettingsAction, Action forceRefreshAction, Action restartAction, Action<string, string, ToolTipIcon> notificationAction)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.openSettingsAction = openSettingsAction;
        this.forceRefreshAction = forceRefreshAction;
        this.restartAction = restartAction;
        this.notificationAction = notificationAction;

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
        ApplyRuntimeSettings(this.currentSettings);
        PositionOperationWindow();
        RenderLayeredWindow();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.animationTimer.Stop();
        this.animationTimer.Tick -= OnAnimationTimerTick;
        this.animationTimer.Dispose();
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
        PositionOperationWindow();
        RenderLayeredWindow();
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

    private static string GetButtonToolTipText(int button)
    {
        if (button == StartButtonIndex)
        {
            return "左键：Windows 开始菜单\r\n右键：Windows 开始右键菜单";
        }

        if (button == WindowsSettingsButtonIndex)
        {
            return "Windows 设置";
        }

        if (button == WindowsPowerMenuButtonIndex)
        {
            return "Windows 电源菜单";
        }

        if (button == RefreshButtonIndex)
        {
            return "刷新所有模块";
        }

        if (button == RestartButtonIndex)
        {
            return "重启本程序";
        }

        if (button == BatteryCarePauseButtonIndex)
        {
            return "解除 80% 充电限制 24 小时";
        }

        if (button == BatteryLimitRestoreButtonIndex)
        {
            return "恢复 80% 充电限制";
        }

        if (button == AppSettingsButtonIndex)
        {
            return "程序设置";
        }

        if (button == SeelenExitButtonIndex)
        {
            return "退出 SeelenUI";
        }

        return string.Empty;
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

    private void ExecuteButton(int button, MouseButtons mouseButton)
    {
        if (button == StartButtonIndex)
        {
            if (mouseButton == MouseButtons.Right)
            {
                NativeMethods.OpenWindowsStartContextMenu();
            }
            else
            {
                NativeMethods.OpenWindowsStartMenu();
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
            NativeMethods.OpenWindowsSystemPowerMenu();
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

        if (button == SeelenExitButtonIndex)
        {
            if (!ConfirmExitSeelenUi())
            {
                return;
            }

            BeginExitSeelenUi();
            return;
        }

        if (button == RefreshButtonIndex)
        {
            if (this.forceRefreshAction != null)
            {
                this.forceRefreshAction();
            }

            return;
        }

        if (button == RestartButtonIndex && this.restartAction != null)
        {
            this.restartAction();
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
        string aliasPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps",
            AsusKeyboardHostAlias);
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

    private bool ConfirmExitSeelenUi()
    {
        DialogResult result = MessageBox.Show(
            this,
            "确认退出 SeelenUI？\r\n这会结束 seelen-ui、slu-service 以及相关 Seelen 进程。\r\n如果 slu-service 权限较高，可能会弹出管理员确认。",
            "确认退出",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }

    private void BeginExitSeelenUi()
    {
        if (this.seelenExitRunning)
        {
            return;
        }

        this.seelenExitRunning = true;
        Program.LogInfo("SeelenUI exit requested.");
        RenderLayeredWindow();
        Task.Run((Action)delegate
        {
            int killed = KillSeelenUiProcesses();
            Program.LogInfo("SeelenUI exit sweep finished. KilledProcesses=" + killed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            this.seelenExitRunning = false;
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

    private static bool AcceptsMouseButton(int button, MouseButtons mouseButton)
    {
        if (button < 0)
        {
            return false;
        }

        if (IsEmptyButton(button))
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
            if (rects[i].Contains(point.X, point.Y))
            {
                if (IsEmptyButton(i))
                {
                    return -1;
                }

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
        return new RectangleF[]
        {
            new RectangleF(margin, margin, startSize, startSize),
            new RectangleF(margin + startSize, margin, smallSize, smallSize),
            new RectangleF(margin + startSize, margin + smallSize, smallSize, smallSize),
            new RectangleF(margin + startSize + smallSize, margin, smallSize, smallSize),
            new RectangleF(margin + startSize + smallSize, margin + smallSize, smallSize, smallSize),
            new RectangleF(margin + startSize + smallSize * 2, margin, smallSize, smallSize),
            new RectangleF(margin + startSize + smallSize * 2, margin + smallSize, smallSize, smallSize),
            new RectangleF(margin + startSize + smallSize * 3, margin, smallSize, smallSize),
            new RectangleF(margin + startSize + smallSize * 3, margin + smallSize, smallSize, smallSize)
        };
    }

    private static bool IsEmptyButton(int button)
    {
        return false;
    }

    private Size GetDesiredSize()
    {
        int margin = S(3);
        int startSize = GetStartButtonSize();
        int smallSize = GetSmallButtonSize();
        return new Size(margin * 2 + startSize + smallSize * 4, margin * 2 + startSize);
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
            double target = i == this.hoveredButton ? 1.0 : 0.0;
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
        if (this.hoveredButton >= 0 || this.pressedButton >= 0)
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
        DrawButton(g, rects[WindowsPowerMenuButtonIndex], WindowsPowerMenuButtonIndex, false, false, false, false);
        DrawButton(g, rects[RefreshButtonIndex], RefreshButtonIndex, false, false, false, false);
        DrawButton(g, rects[RestartButtonIndex], RestartButtonIndex, false, false, false, false);
        DrawButton(g, rects[BatteryCarePauseButtonIndex], BatteryCarePauseButtonIndex, false, false, false, false);
        DrawButton(g, rects[BatteryLimitRestoreButtonIndex], BatteryLimitRestoreButtonIndex, false, false, false, false);
        DrawButton(g, rects[AppSettingsButtonIndex], AppSettingsButtonIndex, false, true, false, false);
        DrawButton(g, rects[SeelenExitButtonIndex], SeelenExitButtonIndex, false, false, true, false);
    }

    private void DrawButton(Graphics g, RectangleF rect, int button, bool leftSegment, bool topRight, bool bottomRight, bool startButton)
    {
        double hover = this.hoverProgress[button];
        double press = button == this.pressedButton ? 1.0 : GetPressProgress(button);
        int backgroundAlpha = GetBackgroundOpacityAlpha();
        int fillAlpha = ScaleAlpha(ClampByte((int)Math.Round(58 + hover * 54 + press * 36)), backgroundAlpha);
        int outlineAlpha = ScaleAlpha(ClampByte((int)Math.Round(44 + hover * 70 + press * 40)), backgroundAlpha);
        Color fill;
        if (button == SeelenExitButtonIndex)
        {
            fill = this.seelenExitRunning
                ? DesignTokens.WithAlpha(DesignTokens.Colors.DangerDeep, ClampByte(fillAlpha + ScaleAlpha(24, backgroundAlpha)))
                : DesignTokens.WithAlpha(DesignTokens.Colors.Danger, ScaleAlpha(ClampByte((int)Math.Round(38 + hover * 52 + press * 45)), backgroundAlpha));
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
        else
        {
            fill = DesignTokens.White(fillAlpha);
        }

        Color border = DesignTokens.White(outlineAlpha);
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
        else if (button == SeelenExitButtonIndex)
        {
            DrawSeelenExitGlyph(g, iconRect);
        }
        else if (button == BatteryCarePauseButtonIndex)
        {
            DrawBatteryCareGlyph(g, iconRect);
        }
        else if (button == BatteryLimitRestoreButtonIndex)
        {
            DrawBatteryLimitRestoreGlyph(g, iconRect);
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

    private void DrawSeelenExitGlyph(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.25f * this.scale);
        RectangleF app = new RectangleF(
            rect.Left + size * 0.09f,
            rect.Top + size * 0.19f,
            size * 0.54f,
            size * 0.58f);
        using (GraphicsPath appPath = RoundedRectangle(app, Math.Max(1.5f, size * 0.10f)))
        using (Pen appPen = new Pen(DesignTokens.White(244), stroke))
        using (Pen exitPen = new Pen(DesignTokens.White(252), Math.Max(1.0f, 1.45f * this.scale)))
        using (SolidBrush exitBrush = new SolidBrush(DesignTokens.White(252)))
        using (SolidBrush markBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.DangerBorder, 255)))
        {
            appPen.StartCap = LineCap.Round;
            appPen.EndCap = LineCap.Round;
            exitPen.StartCap = LineCap.Round;
            exitPen.EndCap = LineCap.Round;
            g.DrawPath(appPen, appPath);
            g.FillEllipse(markBrush, app.Left + app.Width * 0.20f, app.Top + app.Height * 0.20f, size * 0.12f, size * 0.12f);
            g.DrawLine(appPen, app.Left + app.Width * 0.22f, app.Top + app.Height * 0.55f, app.Left + app.Width * 0.55f, app.Top + app.Height * 0.55f);
            g.DrawLine(appPen, app.Left + app.Width * 0.22f, app.Top + app.Height * 0.72f, app.Left + app.Width * 0.45f, app.Top + app.Height * 0.72f);

            PointF tail = new PointF(rect.Left + size * 0.43f, rect.Top + size * 0.50f);
            PointF tip = new PointF(rect.Left + size * 0.90f, rect.Top + size * 0.50f);
            g.DrawLine(exitPen, tail, tip);
            DrawArrowHead(
                g,
                exitBrush,
                tip,
                new PointF(tip.X - size * 0.18f, tip.Y - size * 0.15f),
                new PointF(tip.X - size * 0.18f, tip.Y + size * 0.15f));
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
            if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, this.renderBitmap, 255))
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
