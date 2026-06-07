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

internal sealed class WidgetForm : Form
{
    private const int DisplayRecoveryDelayMs = 350;
    private const int SampleDiagnosticIntervalMinutes = 15;
    private static readonly string[] HardwareVendorPrefixes = new string[]
    {
        "Western Digital",
        "Hewlett-Packard",
        "SK hynix",
        "Snapdragon(R)",
        "Snapdragon",
        "Qualcomm(R)",
        "Qualcomm",
        "Intel(R)",
        "Intel",
        "AMD",
        "NVIDIA",
        "Samsung",
        "SAMSUNG",
        "Micron",
        "KIOXIA",
        "Toshiba",
        "TOSHIBA",
        "Seagate",
        "Kingston",
        "SanDisk",
        "Realtek",
        "MediaTek",
        "Broadcom",
        "Marvell",
        "WDC",
        "WD",
        "Dell",
        "Lenovo",
        "ASUS",
        "HP"
    };

    private readonly PdhSampler sampler;
    private readonly EventWaitHandle stopEvent;
    private readonly bool useDesktopParent;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly System.Windows.Forms.Timer displayRecoveryTimer;
    private readonly List<double> cpuHistory;
    private readonly List<double> memoryHistory;
    private readonly List<double> diskWriteHistory;
    private readonly List<double> diskReadHistory;
    private readonly List<double> networkSentHistory;
    private readonly List<double> networkReceivedHistory;
    private readonly List<double> networkHistory;
    private readonly List<double> gpuHistory;
    private readonly List<double> gpuMemoryHistory;
    private readonly List<double> npuHistory;
    private readonly List<double> npuMemoryHistory;
    private NotifyIcon notifyIcon;
    private Icon notifyIconImage;
    private SettingsForm settingsForm;
    private WidgetSettings savedSettings;
    private WidgetSettings currentSettings;
    private PerfSnapshot snapshot;
    private float scale;
    private int tickCount;
    private DateTime memoryCriticalSinceUtc;
    private DateTime diskCriticalSinceUtc;
    private DateTime gpuCriticalSinceUtc;
    private DateTime npuCriticalSinceUtc;
    private bool memoryAlertIconActive;
    private bool diskAlertIconActive;
    private bool gpuAlertIconActive;
    private bool npuAlertIconActive;
    private bool desktopAttached;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private CodexRadarForm codexRadarForm;
    private PowerThermalForm powerThermalForm;
    private NetworkMonitorForm networkMonitorForm;
    private ConnectionCheckForm connectionCheckForm;
    private OperationForm operationForm;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime lastSettingsWriteUtc;
    private DateTime lastSampleDiagnosticUtc;
    private FileSystemWatcher settingsWatcher;
    private int settingsReloadRequested = 1;
    private Point lastLoggedPosition;
    private Size lastLoggedSize;
    private bool lastLoggedDesktopAttached;
    private bool positionLogInitialized;
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private bool renderBufferValid;
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>(StringComparer.Ordinal);
    private bool formClosing;
    private IntPtr displayPowerNotificationHandle;
    private string pendingDisplayRecoveryReason = string.Empty;

    public WidgetForm(PdhSampler sampler, EventWaitHandle stopEvent, WidgetSettings settings, bool useDesktopParent)
    {
        this.sampler = sampler;
        this.stopEvent = stopEvent;
        this.useDesktopParent = useDesktopParent;
        this.savedSettings = settings.Clone();
        this.currentSettings = settings.Clone();
        this.lastSettingsWriteUtc = GetSettingsWriteUtc();
        InitializeSettingsWatcher();
        this.cpuHistory = new List<double>();
        this.memoryHistory = new List<double>();
        this.diskWriteHistory = new List<double>();
        this.diskReadHistory = new List<double>();
        this.networkSentHistory = new List<double>();
        this.networkReceivedHistory = new List<double>();
        this.networkHistory = new List<double>();
        this.gpuHistory = new List<double>();
        this.gpuMemoryHistory = new List<double>();
        this.npuHistory = new List<double>();
        this.npuMemoryHistory = new List<double>();
        this.snapshot = new PerfSnapshot();
        this.displayRecoveryTimer = new System.Windows.Forms.Timer();
        this.displayRecoveryTimer.Interval = DisplayRecoveryDelayMs;
        this.displayRecoveryTimer.Tick += OnDisplayRecoveryTimerTick;

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
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.Opacity = 1.0;
        this.MinimumSize = new Size(WidgetSettings.MinWidth, WidgetSettings.MinHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxWidth, WidgetSettings.MaxHeight);
        this.Size = new Size(this.currentSettings.Width, this.currentSettings.Height);
        this.ContextMenuStrip = BuildContextMenu();
        BuildNotifyIcon();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = WidgetSettings.GetWidgetSampleIntervalMs(this.currentSettings.PerformanceMode);
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
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
        Program.LogInfo("Widget shown. Handle=0x" + this.Handle.ToInt64().ToString("X"));
        ApplyRuntimeSettings(this.currentSettings);
        PositionWidget();

        if (this.useDesktopParent)
        {
            AttachToDesktopLayer();
            PositionWidget();
        }
        else
        {
            Program.LogInfo("Desktop parent mode disabled; using stable visible desktop mode.");
        }

        this.codexRadarForm = new CodexRadarForm(this.currentSettings, ShowWindowsNotification);
        this.codexRadarForm.SetSharedInteractionPolling(true);
        this.codexRadarForm.Show(this);
        this.powerThermalForm = new PowerThermalForm(this.currentSettings);
        this.powerThermalForm.SetSharedInteractionPolling(true);
        this.powerThermalForm.Show(this);
        this.networkMonitorForm = new NetworkMonitorForm(this.currentSettings);
        this.networkMonitorForm.SetSharedInteractionPolling(true);
        this.networkMonitorForm.Show(this);
        this.connectionCheckForm = new ConnectionCheckForm(this.currentSettings);
        this.connectionCheckForm.SetSharedInteractionPolling(true);
        this.connectionCheckForm.Show(this);
        this.operationForm = new OperationForm(
            this.currentSettings,
            delegate { OpenSettings(); },
            delegate { ForceRefreshAllModules(); },
            delegate { RestartCurrentProcess(); },
            ShowWindowsNotification);
        this.operationForm.Show(this);
        this.timer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (this.displayPowerNotificationHandle == IntPtr.Zero)
        {
            this.displayPowerNotificationHandle = NativeMethods.RegisterConsoleDisplayStateNotification(this.Handle);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (this.displayPowerNotificationHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerNotification(this.displayPowerNotificationHandle);
            this.displayPowerNotificationHandle = IntPtr.Zero;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.formClosing = true;
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        this.displayRecoveryTimer.Stop();
        this.displayRecoveryTimer.Tick -= OnDisplayRecoveryTimerTick;
        this.displayRecoveryTimer.Dispose();
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        DisposeSettingsWatcher();
        if (this.settingsForm != null)
        {
            this.settingsForm.OwnerFormClosing = true;
            this.settingsForm.Close();
            this.settingsForm = null;
        }

        if (this.codexRadarForm != null)
        {
            this.codexRadarForm.Close();
            this.codexRadarForm = null;
        }

        if (this.powerThermalForm != null)
        {
            this.powerThermalForm.Close();
            this.powerThermalForm = null;
        }

        if (this.networkMonitorForm != null)
        {
            this.networkMonitorForm.Close();
            this.networkMonitorForm = null;
        }

        if (this.connectionCheckForm != null)
        {
            this.connectionCheckForm.Close();
            this.connectionCheckForm = null;
        }

        if (this.operationForm != null)
        {
            this.operationForm.Close();
            this.operationForm = null;
        }

        if (this.notifyIcon != null)
        {
            this.notifyIcon.Visible = false;
            this.notifyIcon.Dispose();
            this.notifyIcon = null;
        }

        if (this.notifyIconImage != null)
        {
            this.notifyIconImage.Dispose();
            this.notifyIconImage = null;
        }

        DisposeRenderBuffer();
        DisposeFontCache();
        this.layeredSurface.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        DisposeFontCache();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(13)))
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

        if (m.Msg == NativeMethods.WM_POWERBROADCAST)
        {
            HandlePowerBroadcast(m.WParam, m.LParam);
        }

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionWidget();
            ScheduleDisplayRecovery(m.Msg == WM_DISPLAYCHANGE ? "display change" : "settings change");
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.displayRecoveryTimer.Stop();
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            ScheduleDisplayRecovery("power resume 0x" + eventType.ToString("X"));
            return;
        }

        if (eventType != NativeMethods.PBT_POWERSETTINGCHANGE || dataPtr == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.POWERBROADCAST_SETTING setting =
            (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                dataPtr,
                typeof(NativeMethods.POWERBROADCAST_SETTING));
        if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE && setting.Data == 1)
        {
            ScheduleDisplayRecovery("display powered on");
        }
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock || this.formClosing || this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    ScheduleDisplayRecovery("session unlock");
                });
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        ScheduleDisplayRecovery("session unlock");
    }

    private void ScheduleDisplayRecovery(string reason)
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        this.pendingDisplayRecoveryReason = reason ?? string.Empty;
        this.displayRecoveryTimer.Stop();
        this.displayRecoveryTimer.Start();
    }

    private void OnDisplayRecoveryTimerTick(object sender, EventArgs e)
    {
        this.displayRecoveryTimer.Stop();
        RecoverAfterDisplayResume();
    }

    private void RecoverAfterDisplayResume()
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        if (this.useDesktopParent)
        {
            this.desktopAttached = false;
            AttachToDesktopLayer();
        }

        UpdateVisibilityForMode();
        ApplyClickThroughStyle();
        PositionWidget();
        this.renderBufferValid = false;
        RenderLayeredWindow();

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.RecoverAfterDisplayResume();
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.RecoverAfterDisplayResume();
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.RecoverAfterDisplayResume();
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.RecoverAfterDisplayResume();
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.RecoverAfterDisplayResume();
        }

        Program.LogInfo("Display recovery completed. Reason=" + this.pendingDisplayRecoveryReason);
        this.pendingDisplayRecoveryReason = string.Empty;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("设置...", null, delegate { OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { this.Close(); });
        return menu;
    }

    private void BuildNotifyIcon()
    {
        this.notifyIconImage = CreateNotifyIcon();
        this.notifyIcon = new NotifyIcon();
        this.notifyIcon.Icon = this.notifyIconImage;
        this.notifyIcon.Text = ProductIdentity.DisplayName;
        this.notifyIcon.ContextMenuStrip = BuildNotifyIconMenu();
        this.notifyIcon.Visible = true;
        this.notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenSettings();
            }
        };
    }

    private ContextMenuStrip BuildNotifyIconMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("设置...", null, delegate { OpenSettings(); });
        menu.Items.Add("退出", null, delegate { this.Close(); });
        return menu;
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        if (this.stopEvent.WaitOne(0))
        {
            this.Close();
            return;
        }

        try
        {
            ReloadSettingsIfChanged();
            UpdateVisibilityForMode();
            if (this.hiddenForFullscreen &&
                this.currentSettings.PerformanceMode == WidgetPerformanceMode.BatterySaver)
            {
                // Keep the control tick alive for settings, stop, and visibility checks, but skip PDH sampling.
                return;
            }

            this.snapshot = this.sampler.Sample(
                WidgetSettings.GetExpensiveHardwareSampleIntervalMs(this.currentSettings.PerformanceMode));
            AddHistory(this.cpuHistory, this.snapshot.CpuPercent);
            AddHistory(this.memoryHistory, this.snapshot.MemoryPercent);
            // Rate histories use Kbps so disk and network graphs share the same scaling convention.
            AddHistory(this.diskWriteHistory, this.snapshot.DiskWriteBytesPerSecond * 8.0 / 1000.0);
            AddHistory(this.diskReadHistory, this.snapshot.DiskReadBytesPerSecond * 8.0 / 1000.0);
            AddHistory(this.networkSentHistory, this.snapshot.NetworkSentBytesPerSecond * 8.0 / 1000.0);
            AddHistory(this.networkReceivedHistory, this.snapshot.NetworkReceivedBytesPerSecond * 8.0 / 1000.0);
            AddHistory(
                this.networkHistory,
                (this.snapshot.NetworkSentBytesPerSecond + this.snapshot.NetworkReceivedBytesPerSecond) * 8.0 / 1000.0);
            AddHistory(this.gpuHistory, this.snapshot.GpuPercent);
            AddHistory(this.gpuMemoryHistory, this.snapshot.GpuMemoryPercent);
            AddHistory(this.npuHistory, this.snapshot.NpuPercent);
            AddHistory(this.npuMemoryHistory, this.snapshot.NpuMemoryPercent);
            UpdateAlertIconStates();
            RenderLayeredWindow();

            DateTime nowUtc = DateTime.UtcNow;
            if (this.lastSampleDiagnosticUtc == DateTime.MinValue ||
                (nowUtc - this.lastSampleDiagnosticUtc).TotalMinutes >= SampleDiagnosticIntervalMinutes)
            {
                this.lastSampleDiagnosticUtc = nowUtc;
                Program.LogInfo(string.Format(
                    "Sample CPU={0:0}% Memory={1:0}% Disk={2:0}% GPU={3:0}% GPUMem={4:0}% NPU={5:0}% NPUMem={6:0}% NetConnected={7} NetSent={8:0.0}Bps NetRecv={9:0.0}Bps",
                    this.snapshot.CpuPercent,
                    this.snapshot.MemoryPercent,
                    this.snapshot.DiskPercent,
                    this.snapshot.GpuPercent,
                    this.snapshot.GpuMemoryPercent,
                    this.snapshot.NpuPercent,
                    this.snapshot.NpuMemoryPercent,
                    this.snapshot.NetworkConnected,
                    this.snapshot.NetworkSentBytesPerSecond,
                    this.snapshot.NetworkReceivedBytesPerSecond));
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        this.tickCount++;
        if (this.tickCount % 10 == 0)
        {
            PositionWidget();
            UpdateVisibilityForMode();
        }
    }

    private static void AddHistory(List<double> history, double value)
    {
        const int MaxPoints = 34;
        history.Add(value);
        while (history.Count > MaxPoints)
        {
            history.RemoveAt(0);
        }
    }

    private void UpdateAlertIconStates()
    {
        DateTime now = DateTime.UtcNow;
        UpdateAlertIconState(this.snapshot.MemoryPercent, now, ref this.memoryCriticalSinceUtc, ref this.memoryAlertIconActive);
        UpdateAlertIconState(GetDiskCombinedAlertPercent(), now, ref this.diskCriticalSinceUtc, ref this.diskAlertIconActive);
        UpdateAlertIconState(
            Math.Max(this.snapshot.GpuPercent, this.snapshot.GpuMemoryPercent),
            now,
            ref this.gpuCriticalSinceUtc,
            ref this.gpuAlertIconActive);
        UpdateAlertIconState(
            Math.Max(this.snapshot.NpuPercent, this.snapshot.NpuMemoryPercent),
            now,
            ref this.npuCriticalSinceUtc,
            ref this.npuAlertIconActive);
    }

    private static void UpdateAlertIconState(double value, DateTime now, ref DateTime criticalSinceUtc, ref bool active)
    {
        if (value >= 98.0)
        {
            if (criticalSinceUtc == DateTime.MinValue)
            {
                criticalSinceUtc = now;
            }

            active = (now - criticalSinceUtc).TotalSeconds >= 3.0;
            return;
        }

        criticalSinceUtc = DateTime.MinValue;
        active = false;
    }

    private double GetDiskCombinedAlertPercent()
    {
        // Using the lower busy-time value means read and write must both cross an alert threshold.
        return Math.Min(this.snapshot.DiskWritePercent, this.snapshot.DiskReadPercent);
    }

    private void AttachToDesktopLayer()
    {
        if (this.desktopAttached)
        {
            return;
        }

        IntPtr desktopHost = NativeMethods.FindDesktopHostWindow();
        if (desktopHost == IntPtr.Zero)
        {
            Program.LogInfo("Desktop host window was not found; using normal window parent.");
            return;
        }

        NativeMethods.SetParent(this.Handle, desktopHost);
        int style = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_STYLE);
        style = (style | NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE) & ~NativeMethods.WS_POPUP;
        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_STYLE, style);
        this.desktopAttached = true;
        Program.LogInfo("Attached to desktop host. Host=0x" + desktopHost.ToInt64().ToString("X"));
    }

    private void PositionWidget()
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Point location = CalculateLocation(workArea);
        int left = location.X;
        int top = location.Y;
        this.Location = new Point(left, top);
        uint flags =
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED;

        if (!this.hiddenForFullscreen)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }

        if (this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly && !this.useDesktopParent)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            flags);

        if (!this.positionLogInitialized ||
            this.lastLoggedPosition != this.Location ||
            this.lastLoggedSize != this.Size ||
            this.lastLoggedDesktopAttached != this.desktopAttached)
        {
            this.positionLogInitialized = true;
            this.lastLoggedPosition = this.Location;
            this.lastLoggedSize = this.Size;
            this.lastLoggedDesktopAttached = this.desktopAttached;
            Program.LogInfo(string.Format(
                "Positioned widget at {0},{1},{2},{3}. DesktopAttached={4}",
                left,
                top,
                this.Width,
                this.Height,
                this.desktopAttached));
        }
    }

    private Point CalculateLocation(Rectangle workArea)
    {
        int left = this.currentSettings.LeftX;
        int top = this.currentSettings.BottomY - this.Height + 1;
        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - this.Width));
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        return new Point(left, top);
    }

    internal void PreviewSettings(WidgetSettings settings)
    {
        ApplyRuntimeSettings(settings);
    }

    internal void SaveSettings(WidgetSettings settings)
    {
        WidgetSettings nextSettings = settings.Clone();
        nextSettings.Normalize();
        nextSettings.Save();
        Program.SetStartupEnabled(nextSettings.StartupEnabled, false);
        this.savedSettings = nextSettings.Clone();
        ApplyRuntimeSettings(this.savedSettings);
        this.lastSettingsWriteUtc = GetSettingsWriteUtc();
        Program.LogInfo("Settings saved.");
    }

    internal void ExitCurrentProcess()
    {
        Program.LogInfo("Exit requested from settings.");
        this.Close();
    }

    internal void FullyExitApplication()
    {
        Program.LogInfo("Full exit requested from settings.");
        KillOtherAssistantWindowProcesses();
        this.Close();
    }

    internal void ForceRefreshAllModules()
    {
        Program.LogInfo("Forced refresh requested from operation panel.");
        OnTimerTick(this, EventArgs.Empty);

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.ForceRefresh();
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.ForceRefresh();
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.ForceRefresh();
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.ForceRefresh();
        }
    }

    internal void TestCodexExtraResetNotification()
    {
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.TestExtraResetNotification();
        }
    }

    internal void TestCodexRadarOpenNotification()
    {
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.TestRadarOpenNotification();
        }
    }

    private void ShowWindowsNotification(string title, string message, ToolTipIcon icon)
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            this.BeginInvoke((MethodInvoker)delegate { ShowWindowsNotification(title, message, icon); });
            return;
        }

        if (this.notifyIcon == null)
        {
            return;
        }

        this.notifyIcon.ShowBalloonTip(10000, title, message, icon);
        Program.LogInfo("Windows notification shown. Title=" + title);
    }

    internal void RestartCurrentProcess()
    {
        Program.LogInfo("Restart requested from operation panel.");
        Program.RestartApplication(this.useDesktopParent);
        this.Close();
    }

    private static void KillOtherAssistantWindowProcesses()
    {
        int currentId = Process.GetCurrentProcess().Id;
        string currentPath = Application.ExecutablePath;
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                if (process == null || process.Id == currentId)
                {
                    continue;
                }

                string path = string.Empty;
                try
                {
                    path = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(path) &&
                    !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    process.Kill();
                    process.WaitForExit(1500);
                    Program.LogInfo("Killed duplicate process " + process.Id.ToString(CultureInfo.InvariantCulture));
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
    }

    internal void RevertSettings(WidgetSettings settings)
    {
        ApplyRuntimeSettings(settings);
        Program.LogInfo("Settings reverted.");
    }

    private void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        Program.ApplyPerformanceMode(this.currentSettings.PerformanceMode);
        ApplyPerformanceTimerIntervals();

        Size desiredSize = new Size(this.currentSettings.Width, this.currentSettings.Height);
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();

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

        PositionWidget();
        UpdateVisibilityForMode();
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.ApplyRuntimeSettings(this.currentSettings);
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.ApplyRuntimeSettings(this.currentSettings);
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.ApplyRuntimeSettings(this.currentSettings);
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.ApplyRuntimeSettings(this.currentSettings);
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.ApplyRuntimeSettings(this.currentSettings);
        }

        RenderLayeredWindow();
    }

    private void ApplyPerformanceTimerIntervals()
    {
        int sampleInterval = GetCurrentWidgetTimerIntervalMs();
        if (this.timer.Interval != sampleInterval)
        {
            this.timer.Interval = sampleInterval;
        }

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
    }

    private int GetCurrentWidgetTimerIntervalMs()
    {
        if (!this.hiddenForFullscreen)
        {
            return WidgetSettings.GetWidgetSampleIntervalMs(this.currentSettings.PerformanceMode);
        }

        // Hidden windows still need to notice when the foreground app leaves fullscreen.
        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.Smooth)
        {
            return WidgetSettings.GetWidgetSampleIntervalMs(this.currentSettings.PerformanceMode);
        }

        return this.currentSettings.PerformanceMode == WidgetPerformanceMode.BatterySaver ? 5000 : 2500;
    }

    private void ReloadSettingsIfChanged()
    {
        // A watcher converts the normal path to an in-memory flag check. If watcher
        // creation failed, retain timestamp polling as the compatibility fallback.
        if (this.settingsWatcher != null &&
            Interlocked.Exchange(ref this.settingsReloadRequested, 0) == 0)
        {
            return;
        }

        if (this.settingsForm != null && !this.settingsForm.IsDisposed)
        {
            // Keep the invalidation pending without touching the file while live preview owns settings.
            Interlocked.Exchange(ref this.settingsReloadRequested, 1);
            return;
        }

        DateTime settingsWriteUtc = GetSettingsWriteUtc();
        if (settingsWriteUtc == DateTime.MinValue || settingsWriteUtc == this.lastSettingsWriteUtc)
        {
            return;
        }

        WidgetSettings settings = WidgetSettings.Load();
        this.savedSettings = settings.Clone();
        ApplyRuntimeSettings(settings);
        this.lastSettingsWriteUtc = settingsWriteUtc;
        Program.LogInfo("Settings reloaded from disk.");
    }

    private void InitializeSettingsWatcher()
    {
        string settingsDirectory = Path.GetDirectoryName(WidgetSettings.SettingsPath);
        string settingsFileName = Path.GetFileName(WidgetSettings.SettingsPath);
        if (string.IsNullOrEmpty(settingsDirectory) ||
            string.IsNullOrEmpty(settingsFileName) ||
            !Directory.Exists(settingsDirectory))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new FileSystemWatcher(settingsDirectory, settingsFileName);
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Changed += OnSettingsFileChanged;
            watcher.Created += OnSettingsFileChanged;
            watcher.Deleted += OnSettingsFileChanged;
            watcher.Renamed += OnSettingsFileRenamed;
            watcher.Error += OnSettingsWatcherError;
            watcher.EnableRaisingEvents = true;
            this.settingsWatcher = watcher;
        }
        catch (Exception ex)
        {
            // Timestamp polling remains active if the watcher cannot be created.
            Program.LogException(ex);
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref this.settingsReloadRequested, 1);
    }

    private void OnSettingsFileRenamed(object sender, RenamedEventArgs e)
    {
        Interlocked.Exchange(ref this.settingsReloadRequested, 1);
    }

    private void OnSettingsWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref this.settingsReloadRequested, 1);
    }

    private void DisposeSettingsWatcher()
    {
        FileSystemWatcher watcher = this.settingsWatcher;
        this.settingsWatcher = null;
        if (watcher == null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnSettingsFileChanged;
        watcher.Created -= OnSettingsFileChanged;
        watcher.Deleted -= OnSettingsFileChanged;
        watcher.Renamed -= OnSettingsFileRenamed;
        watcher.Error -= OnSettingsWatcherError;
        watcher.Dispose();
    }

    private static DateTime GetSettingsWriteUtc()
    {
        try
        {
            if (File.Exists(WidgetSettings.SettingsPath))
            {
                return File.GetLastWriteTimeUtc(WidgetSettings.SettingsPath);
            }
        }
        catch
        {
        }

        return DateTime.MinValue;
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        ApplyClickThroughStyle();
        bool opacityChanged = UpdateHoverOpacityAnimation();
        bool hoverTarget = IsHoverOpacityTargetActive();
        bool animationActive = Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
        // All passive panels share this UI-thread timer so hover support costs one
        // message-pump wakeup instead of one wakeup per window.
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            animationActive |= this.codexRadarForm.ProcessSharedInteractionTick();
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            animationActive |= this.powerThermalForm.ProcessSharedInteractionTick();
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            animationActive |= this.networkMonitorForm.ProcessSharedInteractionTick();
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            animationActive |= this.connectionCheckForm.ProcessSharedInteractionTick();
        }

        int desiredInterval = animationActive
            ? WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode)
            : WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != desiredInterval)
        {
            this.hoverTimer.Interval = desiredInterval;
        }

        if (opacityChanged)
        {
            RenderLayeredWindow(false);
        }
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (this.currentSettings.HoverOpacityEnabled || NeedsClickThroughPolling()))
        {
            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow(false);
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered = IsHoverOpacityTargetActive();

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    private bool IsHoverOpacityTargetActive()
    {
        return this.currentSettings.HoverOpacityEnabled &&
            !this.hiddenForFullscreen &&
            this.Visible &&
            this.Bounds.Contains(Cursor.Position);
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = ShouldClickThroughNow();
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private bool ShouldClickThroughNow()
    {
        if (NativeMethods.IsClickThroughModifierDown())
        {
            return false;
        }

        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    private bool NeedsClickThroughPolling()
    {
        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    private void UpdateVisibilityForMode()
    {
        bool foregroundWindowFullscreen = NativeMethods.IsForegroundWindowFullscreen(this.Handle);
        bool hideForFullscreen =
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.HideWhenFullscreen &&
            foregroundWindowFullscreen;

        if (hideForFullscreen)
        {
            bool hiddenChanged = !this.hiddenForFullscreen;
            this.hiddenForFullscreen = true;
            if (this.Visible)
            {
                this.Hide();
            }

            if (hiddenChanged)
            {
                // Propagate only state transitions; repeated notifications force every child to redraw.
                SetChildWindowsHiddenForFullscreen(true);
                ApplyPerformanceTimerIntervals();
                UpdateHoverAnimationTimer();
            }

            return;
        }

        bool shownChanged = this.hiddenForFullscreen;
        this.hiddenForFullscreen = false;
        if (!this.Visible)
        {
            this.Show();
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        if (shownChanged)
        {
            SetChildWindowsHiddenForFullscreen(false);
            ApplyPerformanceTimerIntervals();
            UpdateHoverAnimationTimer();
        }
    }

    private void SetChildWindowsHiddenForFullscreen(bool hidden)
    {
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.SetHiddenForFullscreen(hidden);
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.SetHiddenForFullscreen(hidden);
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.SetHiddenForFullscreen(hidden);
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.SetHiddenForFullscreen(hidden);
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.SetHiddenForFullscreen(hidden);
        }
    }

    private void OpenSettings()
    {
        if (this.settingsForm != null && !this.settingsForm.IsDisposed)
        {
            this.settingsForm.Activate();
            return;
        }

        WidgetSettings baseline = this.savedSettings.Clone();
        baseline.Normalize();
        this.settingsForm = new SettingsForm(this, baseline);
        this.settingsForm.FormClosed += delegate { this.settingsForm = null; };
        this.settingsForm.Show();
        this.settingsForm.Activate();
    }

    private Icon CreateNotifyIcon()
    {
        using (Bitmap bitmap = new Bitmap(32, 32))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush background = new SolidBrush(DesignTokens.Colors.NotifyIconSurface))
            using (Pen border = new Pen(DesignTokens.White(120), 2.0f))
            {
                g.FillEllipse(background, 2, 2, 28, 28);
                g.DrawEllipse(border, 2, 2, 28, 28);
            }

            using (Pen cpu = new Pen(DesignTokens.Colors.Accent, 3.0f))
            using (Pen memory = new Pen(DesignTokens.Colors.AccentAlt, 3.0f))
            using (Pen disk = new Pen(DesignTokens.Colors.Success, 3.0f))
            {
                g.DrawLine(cpu, 9, 21, 9, 12);
                g.DrawLine(memory, 16, 21, 16, 8);
                g.DrawLine(disk, 23, 21, 23, 15);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                return icon;
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawWidget(e.Graphics);
    }

    private void DrawWidget(Graphics g)
    {
        DrawWidgetBackground(g);
        DrawWidgetContentLayer(g);
    }

    private void ConfigureWidgetGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawWidgetBackground(Graphics g)
    {
        ConfigureWidgetGraphics(g);
        int backgroundAlpha = GetBackgroundOpacityAlpha();

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Widget)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, backgroundAlpha)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawWidgetContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawWidgetContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawWidgetContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawWidgetContent(Graphics g)
    {
        ConfigureWidgetGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Widget)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        int margin = S(13);
        int gap = S(14);
        int rowGap = S(8);
        List<MetricPanel> panels = BuildMetricPanels();
        if (panels.Count == 0)
        {
            Font font = GetCachedFont(13.0f * this.scale, FontStyle.Bold);
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.TextMuted))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString("No metrics enabled", font, brush, this.ClientRectangle, format);
            }

            return;
        }

        int columns = panels.Count == 1 ? 1 : 2;
        int rows = (panels.Count + columns - 1) / columns;
        int colWidth = (this.ClientSize.Width - margin * 2 - gap * (columns - 1)) / columns;
        int rowHeight = (this.ClientSize.Height - margin * 2 - rowGap * (rows - 1)) / rows;

        for (int i = 0; i < panels.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            RectangleF area = new RectangleF(
                margin + column * (colWidth + gap),
                margin + row * (rowHeight + rowGap),
                colWidth,
                rowHeight);
            DrawMetric(g, area, panels[i]);
        }
    }

    private void RenderLayeredWindow()
    {
        RenderLayeredWindow(true);
    }

    private void RenderLayeredWindow(bool redrawContent)
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            EnsureRenderBuffer();
            bool refreshNativeBitmap = redrawContent || !this.renderBufferValid;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawWidgetBackground(this.renderGraphics);
                DrawWidgetContentLayer(this.renderGraphics);
                this.renderBufferValid = true;
            }

            if (!this.layeredSurface.Update(
                this.Handle,
                this.Location,
                this.renderBitmap,
                GetApplicationOpacityAlpha(),
                refreshNativeBitmap))
            {
                if (!this.layeredUpdateFailureLogged)
                {
                    this.layeredUpdateFailureLogged = true;
                    Program.LogInfo("UpdateLayeredWindow failed; falling back to normal paint.");
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
        this.renderBufferValid = false;
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

        this.renderBufferValid = false;
    }

    private List<MetricPanel> BuildMetricPanels()
    {
        List<MetricPanel> panels = new List<MetricPanel>();
        string[] order = this.currentSettings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        for (int i = 0; i < order.Length; i++)
        {
            AddMetricPanel(panels, order[i]);
        }

        return panels;
    }

    private void AddMetricPanel(List<MetricPanel> panels, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowCpu)
        {
            MetricPanel cpuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.CpuName), string.Format("CPU {0:0}%", this.snapshot.CpuPercent), FormatCpuFrequencyPair(this.snapshot.CpuFrequencyGhz, this.snapshot.CpuBaseFrequencyGhz) },
                new Color[] { DesignTokens.Colors.Accent },
                new List<double>[] { this.cpuHistory },
                100.0,
                false);
            cpuPanel.CoreValues = this.snapshot.CpuCorePercents;
            cpuPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                cpuPanel.AlertPercent = 100.0;
                cpuPanel.AlertIconVisible = true;
            }

            panels.Add(cpuPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowMemory)
        {
            MetricPanel memoryPanel = new MetricPanel(
                new string[] { "MEMORY", string.Format("MEM {0:0}%", this.snapshot.MemoryPercent), FormatGbPair(this.snapshot.MemoryUsedGb, this.snapshot.MemoryTotalGb) },
                new Color[] { DesignTokens.Colors.AccentAlt },
                new List<double>[] { this.memoryHistory },
                100.0,
                false);
            memoryPanel.AlertPercent = this.snapshot.MemoryPercent;
            memoryPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                memoryPanel.AlertPercent = 100.0;
                memoryPanel.AlertIconVisible = true;
            }
            else
            {
                memoryPanel.AlertIconVisible = this.memoryAlertIconActive;
            }

            panels.Add(memoryPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowDisk)
        {
            MetricPanel diskPanel = new MetricPanel(
                new string[]
                {
                    "DISK",
                    "WT " + FormatRate(this.snapshot.DiskWriteBytesPerSecond),
                    "RD " + FormatRate(this.snapshot.DiskReadBytesPerSecond),
                    FormatRoundedGbPair(this.snapshot.DiskUsedGb, this.snapshot.DiskTotalGb)
                },
                new Color[] { DesignTokens.Colors.Warning, DesignTokens.Colors.Success },
                new List<double>[] { this.diskWriteHistory, this.diskReadHistory },
                1.0,
                true);
            diskPanel.AlertPercent = GetDiskCombinedAlertPercent();
            diskPanel.UseHardwareStackText = true;
            diskPanel.UseCompactValueFont = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                diskPanel.AlertPercent = 100.0;
                diskPanel.AlertIconVisible = true;
            }
            else
            {
                diskPanel.AlertIconVisible = this.diskAlertIconActive;
            }

            panels.Add(diskPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowNetwork)
        {
            MetricPanel networkPanel = new MetricPanel(
                this.snapshot.NetworkConnected ?
                    new string[] { this.snapshot.NetworkName, "UP " + FormatRate(this.snapshot.NetworkSentBytesPerSecond), "DL " + FormatRate(this.snapshot.NetworkReceivedBytesPerSecond) } :
                    new string[] { "Network", "网络已断开", "" },
                new Color[] { DesignTokens.Colors.Accent, DesignTokens.Colors.Danger },
                new List<double>[] { this.networkSentHistory, this.networkReceivedHistory },
                1.0,
                true);
            networkPanel.UseCompactValueFont = true;
            networkPanel.IsNetworkDisconnected = !this.snapshot.NetworkConnected;
            if (this.currentSettings.AlertTestEnabled)
            {
                networkPanel.AlertPercent = 100.0;
                networkPanel.AlertIconVisible = true;
            }

            panels.Add(networkPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowGpu)
        {
            MetricPanel gpuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.GpuName), string.Format("GPU {0:0}%", this.snapshot.GpuPercent), FormatGbPair(this.snapshot.GpuMemoryUsedGb, this.snapshot.GpuMemoryTotalGb) },
                new Color[] { DesignTokens.Colors.Accent, DesignTokens.Colors.AccentAlt },
                new List<double>[] { this.gpuHistory, this.gpuMemoryHistory },
                100.0,
                false);
            gpuPanel.AlertPercent = Math.Max(this.snapshot.GpuPercent, this.snapshot.GpuMemoryPercent);
            gpuPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                gpuPanel.AlertPercent = 100.0;
                gpuPanel.AlertIconVisible = true;
            }
            else
            {
                gpuPanel.AlertIconVisible = this.gpuAlertIconActive;
            }

            panels.Add(gpuPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowNpu)
        {
            MetricPanel npuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.NpuName), string.Format("NPU {0:0}%", this.snapshot.NpuPercent), FormatGbPair(this.snapshot.NpuMemoryUsedGb, this.snapshot.NpuMemoryTotalGb) },
                new Color[] { DesignTokens.Colors.Warning, DesignTokens.Colors.AccentAlt },
                new List<double>[] { this.npuHistory, this.npuMemoryHistory },
                100.0,
                false);
            npuPanel.AlertPercent = Math.Max(this.snapshot.NpuPercent, this.snapshot.NpuMemoryPercent);
            npuPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                npuPanel.AlertPercent = 100.0;
                npuPanel.AlertIconVisible = true;
            }
            else
            {
                npuPanel.AlertIconVisible = this.npuAlertIconActive;
            }

            panels.Add(npuPanel);
        }
    }

    private void DrawMetric(Graphics g, RectangleF area, MetricPanel panel)
    {
        float graphW = Math.Min(S(86), Math.Max(S(58), area.Width * 0.34f));
        float graphH = Math.Max(S(32), area.Height - S(8));
        RectangleF graphRect = new RectangleF(area.X, area.Y + Math.Max(0, (area.Height - graphH) / 2), graphW, graphH);
        DrawGraph(g, graphRect, panel.Colors, panel.Histories, panel.GraphMax, panel.AutoScale, panel.IsNetworkDisconnected, panel.CoreValues, panel.AlertPercent, panel.AlertIconVisible);

        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(9);
        float textWidth = Math.Max(20, area.Right - textX);
        // Disk has a fourth capacity line; other metrics retain their established three-line spacing.
        int textLineCount = panel.TextLines != null && panel.TextLines.Length >= 4 ? 4 : 3;
        float lineH = Math.Max(1.0f, area.Height / textLineCount);

        Font smallFont = GetCachedFont(10.5f * this.scale, FontStyle.Bold);
        Font valueFont = GetCachedFont(11.5f * this.scale, FontStyle.Bold);
        Font compactFont = GetCachedFont(10.0f * this.scale, FontStyle.Bold);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (SolidBrush valueBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush alertBrush = new SolidBrush(DesignTokens.Colors.Danger))
        {
            RectangleF first = new RectangleF(textX, area.Y, textWidth, lineH);
            RectangleF second = new RectangleF(textX, area.Y + lineH, textWidth, lineH);
            RectangleF third = new RectangleF(textX, area.Y + lineH * 2, textWidth, lineH);
            RectangleF fourth = new RectangleF(textX, area.Y + lineH * 3, textWidth, lineH);
            if (panel.UseHardwareStackText && panel.TextLines[0].IndexOf('\n') >= 0)
            {
                DrawHardwareStackText(g, area, textX, textWidth, panel, smallFont, valueFont, titleBrush, valueBrush);
                return;
            }

            DrawTitleText(g, panel.TextLines[0], smallFont, titleBrush, first);
            if (panel.TextLines.Length >= 4)
            {
                DrawFixedText(g, panel.TextLines[1], compactFont, valueBrush, second);
                DrawFixedText(g, panel.TextLines[2], compactFont, valueBrush, third);
                DrawFixedText(g, panel.TextLines[3], compactFont, valueBrush, fourth);
                return;
            }

            if (panel.UseCompactValueFont)
            {
                DrawFixedText(g, panel.TextLines[1], compactFont, panel.IsNetworkDisconnected ? alertBrush : valueBrush, second);
                DrawFixedText(g, panel.TextLines[2], compactFont, valueBrush, third);
            }
            else
            {
                DrawFittedText(g, panel.TextLines[1], valueFont, valueBrush, second);
                DrawFittedText(g, panel.TextLines[2], valueFont, valueBrush, third);
            }
        }
    }

    private void DrawGraph(Graphics g, RectangleF rect, Color[] accents, List<double>[] histories, double graphMax, bool autoScale, bool dimmed, double[] coreValues, double alertPercent, bool alertIconVisible)
    {
        Color borderColor = accents.Length > 0 ? accents[0] : DesignTokens.Colors.TextMuted;
        int backgroundAlpha = GetBackgroundOpacityAlpha();
        int fillAlpha = dimmed ? Math.Min(backgroundAlpha, 128) : backgroundAlpha;
        int borderAlpha = dimmed ? 90 : 180;
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, fillAlpha)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(borderColor, borderAlpha), Math.Max(1.0f, 1.5f * this.scale)))
        {
            g.FillRectangle(fill, rect);
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
        }

        DrawUsageAlertLayer(g, rect, alertPercent, alertIconVisible);
        DrawCoreBars(g, rect, coreValues);

        double max = autoScale ? MaxValue(histories) : graphMax;
        if (max < 1.0)
        {
            max = 1.0;
        }

        for (int h = 0; h < histories.Length; h++)
        {
            List<double> history = histories[h];
            if (history == null || history.Count < 2)
            {
                continue;
            }

            PointF[] points = new PointF[history.Count];
            for (int i = 0; i < history.Count; i++)
            {
                double normalized = Clamp(history[i] / max, 0.0, 1.0);
                float x = rect.Left + (rect.Width - 2) * i / Math.Max(1, history.Count - 1) + 1;
                float y = rect.Bottom - 1 - (float)(normalized * (rect.Height - 2));
                points[i] = new PointF(x, y);
            }

            Color accent = accents[Math.Min(h, accents.Length - 1)];
            if (dimmed)
            {
                accent = DesignTokens.WithAlpha(accent, 110);
            }

            using (Pen line = new Pen(accent, Math.Max(1.0f, 2.0f * this.scale)))
            {
                line.LineJoin = LineJoin.Round;
                g.DrawLines(line, points);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.BackgroundTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!this.currentSettings.HoverOpacityEnabled || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    private void DrawUsageAlertLayer(Graphics g, RectangleF rect, double alertPercent, bool alertIconVisible)
    {
        if (alertPercent < 80.0)
        {
            return;
        }

        double progress = Clamp((alertPercent - 80.0) / 20.0, 0.0, 1.0);
        int redAlpha = (int)Math.Round(179.0 * progress);
        using (SolidBrush redOverlay = new SolidBrush(DesignTokens.DangerStrong(redAlpha)))
        {
            g.FillRectangle(redOverlay, rect);
        }

        if (!alertIconVisible)
        {
            return;
        }

        float size = Math.Min(rect.Width, rect.Height) * 0.48f;
        size = Math.Max(14.0f * this.scale, Math.Min(size, 28.0f * this.scale));
        float centerX = rect.Left + rect.Width * 0.5f;
        float centerY = rect.Top + rect.Height * 0.52f;
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.58f),
            new PointF(centerX - size * 0.58f, centerY + size * 0.48f),
            new PointF(centerX + size * 0.58f, centerY + size * 0.48f)
        };

        int warningAlpha = (this.tickCount % 2 == 0) ? 77 : 179;
        using (Pen triangleBorder = new Pen(DesignTokens.Warning(warningAlpha), Math.Max(1.0f, 3.0f * this.scale)))
        {
            triangleBorder.LineJoin = LineJoin.Round;
            g.DrawPolygon(triangleBorder, triangle);
        }

        Font markFont = GetCachedFont(Math.Max(9.0f, size * 0.7f), FontStyle.Bold);
        using (SolidBrush markBrush = new SolidBrush(DesignTokens.Warning(warningAlpha)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            RectangleF markRect = new RectangleF(centerX - size * 0.5f, centerY - size * 0.36f, size, size * 0.92f);
            g.DrawString("!", markFont, markBrush, markRect, format);
        }
    }

    private void DrawCoreBars(Graphics g, RectangleF rect, double[] values)
    {
        if (values == null || values.Length == 0)
        {
            return;
        }

        float left = rect.Left + Math.Max(2.0f, 2.0f * this.scale);
        float bottom = rect.Bottom - Math.Max(2.0f, 2.0f * this.scale);
        float width = Math.Max(1.0f, rect.Width - Math.Max(4.0f, 4.0f * this.scale));
        float height = Math.Max(1.0f, rect.Height - Math.Max(4.0f, 4.0f * this.scale));
        float slot = width / values.Length;
        float gap = slot >= 4.0f ? Math.Min(2.0f * this.scale, slot * 0.28f) : 0.0f;
        float barWidth = Math.Max(1.0f, slot - gap);

        using (SolidBrush normalBrush = new SolidBrush(DesignTokens.Accent(115)))
        using (SolidBrush warningBrush = new SolidBrush(DesignTokens.Warning(210)))
        using (SolidBrush criticalBrush = new SolidBrush(DesignTokens.Danger(225)))
        {
            for (int i = 0; i < values.Length; i++)
            {
                double value = Clamp(values[i], 0.0, 100.0);
                float x = left + slot * i + gap / 2.0f;
                float valueTop = bottom - (float)(height * value / 100.0);

                if (value > 95.0)
                {
                    g.FillRectangle(criticalBrush, x, valueTop, barWidth, bottom - valueTop);
                    continue;
                }

                float normalValue = (float)Math.Min(value, 80.0);
                if (normalValue > 0.0f)
                {
                    float normalTop = bottom - height * normalValue / 100.0f;
                    g.FillRectangle(normalBrush, x, normalTop, barWidth, bottom - normalTop);
                }

                if (value > 80.0)
                {
                    float warningTop = valueTop;
                    float warningBottom = bottom - height * 80.0f / 100.0f;
                    g.FillRectangle(warningBrush, x, warningTop, barWidth, warningBottom - warningTop);
                }
            }
        }
    }

    private void DrawDisconnectedCross(Graphics g, RectangleF rect)
    {
        float padding = Math.Max(3.0f, 4.0f * this.scale);
        using (Pen cross = new Pen(DesignTokens.Colors.DangerGlyph, Math.Max(2.0f, 3.2f * this.scale)))
        {
            cross.StartCap = LineCap.Round;
            cross.EndCap = LineCap.Round;
            g.DrawLine(cross, rect.Left + padding, rect.Top + padding, rect.Right - padding, rect.Bottom - padding);
            g.DrawLine(cross, rect.Right - padding, rect.Top + padding, rect.Left + padding, rect.Bottom - padding);
        }
    }

    private void DrawFixedText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(text, font, brush, rect, format);
        }
    }

    private Font GetCachedFont(float size, FontStyle style)
    {
        float normalizedSize = (float)Math.Round(Math.Max(1.0f, size), 2);
        string key = normalizedSize.ToString("0.00", CultureInfo.InvariantCulture) + "|" + ((int)style).ToString(CultureInfo.InvariantCulture);
        Font font;
        if (!this.fontCache.TryGetValue(key, out font))
        {
            font = DesignTokens.CreateUIFont(normalizedSize, style, GraphicsUnit.Pixel);
            this.fontCache[key] = font;
        }

        return font;
    }

    private void DisposeFontCache()
    {
        foreach (Font font in this.fontCache.Values)
        {
            font.Dispose();
        }

        this.fontCache.Clear();
    }

    private void DrawHardwareStackText(Graphics g, RectangleF area, float textX, float textWidth, MetricPanel panel, Font titleFont, Font valueFont, Brush titleBrush, Brush valueBrush)
    {
        string[] titleLines = panel.TextLines[0].Replace("\r", string.Empty).Split('\n');
        string titleFirst = titleLines.Length > 0 ? titleLines[0] : string.Empty;
        string titleSecond = titleLines.Length > 1 ? titleLines[1] : string.Empty;
        float stackLineH = Math.Max(S(10), area.Height / 4.0f);
        float stackTop = area.Y + Math.Max(0, (area.Height - stackLineH * 4.0f) / 2.0f);

        RectangleF titleFirstRect = new RectangleF(textX, stackTop, textWidth, stackLineH);
        RectangleF titleSecondRect = new RectangleF(textX, stackTop + stackLineH, textWidth, stackLineH);
        RectangleF valueRect = new RectangleF(textX, stackTop + stackLineH * 2.0f, textWidth, stackLineH);
        RectangleF detailRect = new RectangleF(textX, stackTop + stackLineH * 3.0f, textWidth, stackLineH);

        DrawFittedText(g, titleFirst, titleFont, titleBrush, titleFirstRect);
        DrawFittedText(g, titleSecond, titleFont, titleBrush, titleSecondRect);
        DrawFittedText(g, panel.TextLines[1], valueFont, valueBrush, valueRect);
        DrawFittedText(g, panel.TextLines[2], valueFont, valueBrush, detailRect);
    }

    private void DrawTitleText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\n') < 0)
        {
            DrawFittedText(g, text, baseFont, brush, rect);
            return;
        }

        string[] lines = text.Replace("\r", string.Empty).Split('\n');
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 7.0f * this.scale && !TitleTextFits(g, lines, drawFont, rect))
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.6f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private bool TitleTextFits(Graphics g, string[] lines, Font font, RectangleF rect)
    {
        float maxWidth = 0.0f;
        int visibleLines = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            visibleLines++;
            maxWidth = Math.Max(maxWidth, g.MeasureString(lines[i], font).Width);
        }

        visibleLines = Math.Max(1, visibleLines);
        float totalHeight = font.GetHeight(g) * visibleLines;
        return maxWidth <= rect.Width && totalHeight <= rect.Height * 1.02f;
    }

    private void DrawFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;

            while (size > 8.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.7f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        return NetworkRateFormatter.Format(bytesPerSecond);
    }

    private static string FormatGbPair(double usedGb, double totalGb)
    {
        if (totalGb <= 0.0)
        {
            return string.Format("{0:0.0}/-- GB", usedGb);
        }

        return string.Format("{0:0.0}/{1:0.#} GB", usedGb, totalGb);
    }

    private static string FormatRoundedGbPair(double usedGb, double totalGb)
    {
        double roundedUsed = Math.Round(Math.Max(0.0, usedGb), 0, MidpointRounding.AwayFromZero);
        if (totalGb <= 0.0)
        {
            return string.Format("{0:0}/-- GB", roundedUsed);
        }

        double roundedTotal = Math.Round(Math.Max(0.0, totalGb), 0, MidpointRounding.AwayFromZero);
        return string.Format("{0:0}/{1:0} GB", roundedUsed, roundedTotal);
    }

    private static string FormatCpuFrequencyPair(double currentGhz, double baseGhz)
    {
        if (currentGhz <= 0.0 && baseGhz <= 0.0)
        {
            return string.Empty;
        }

        if (baseGhz <= 0.0)
        {
            return string.Format("{0:0.00}GHz/--GHz", currentGhz);
        }

        return string.Format("{0:0.00}GHz/{1:0.00}GHz", currentGhz, baseGhz);
    }

    private static string FormatMemoryTitleForPanel(string manufacturer, int speedMtps)
    {
        string first = string.IsNullOrWhiteSpace(manufacturer) ? "Memory" : CollapseWhitespace(manufacturer.Trim());
        string second = speedMtps > 0 ? speedMtps.ToString() + " MT/s" : "-- MT/s";
        return first + "\n" + second;
    }

    private static string FormatHardwareNameForPanel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string text = CollapseWhitespace(name.Trim());
        if (text.IndexOf('\n') >= 0)
        {
            return text;
        }

        for (int i = 0; i < HardwareVendorPrefixes.Length; i++)
        {
            string vendor = HardwareVendorPrefixes[i];
            if (!StartsWithVendorPrefix(text, vendor))
            {
                continue;
            }

            string remainder = text.Substring(vendor.Length).TrimStart(' ', '\t', '-', '_');
            if (remainder.Length == 0)
            {
                return text;
            }

            return text.Substring(0, vendor.Length).Trim() + "\n" + remainder;
        }

        return text;
    }

    private static bool StartsWithVendorPrefix(string text, string vendor)
    {
        if (!text.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == vendor.Length)
        {
            return true;
        }

        char next = text[vendor.Length];
        return char.IsWhiteSpace(next) || next == '-' || next == '_' || next == '(';
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        bool previousWhitespace = false;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                    previousWhitespace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        return builder.ToString();
    }

    private static double MaxValue(List<double> values)
    {
        double max = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }

        return max;
    }

    private static double MaxValue(List<double>[] histories)
    {
        double max = 0.0;
        for (int i = 0; i < histories.Length; i++)
        {
            if (histories[i] == null)
            {
                continue;
            }

            double value = MaxValue(histories[i]);
            if (value > max)
            {
                max = value;
            }
        }

        return max;
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

    private int S(int value)
    {
        return (int)Math.Round(value * this.scale);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
