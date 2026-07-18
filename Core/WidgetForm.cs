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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed partial class WidgetForm : LayeredWidgetFormBase
{
    private const int DisplayRecoveryDelayMs = 350;
    private const int DisplayRecoveryRetryDelayMs = 1500;
    private const int DisplayRecoveryMaxAttempts = 3;
    private const int SampleDiagnosticIntervalMinutes = 15;
    private const int SeelenDockPulseFallbackIntervalMs = 30 * 60 * 1000;
    private const int WinDRecoveryDelayMs = 2000;
    private const int PowerResumeRestartGuardSeconds = 30;
    private const int HotkeyToggleAllWindowsId = 0x51A1;
    private const int HotkeyToggleHoverOpacityId = 0x51A2;
    private const int HotkeyOpenSettingsId = 0x51A3;
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
    private readonly System.Windows.Forms.Timer seelenDockPulseTimer;
    private readonly System.Windows.Forms.Timer winDRecoveryTimer;
    private readonly GlobalWinDWatcher winDWatcher;
    private readonly List<double> cpuHistory;
    private readonly List<double> memoryHistory;
    private readonly List<double> memoryHardwareReservedHistory;
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
    private Form settingsForm;
    private Form aiQuickMenuForm;
    private WidgetSettings savedSettings;
    private PerfSnapshot snapshot;
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
    private bool globalLayoutEditActive;
    private bool manualAllWindowsHidden;
    private bool childWindowLifecycleStarted;
    private CodexRadarForm codexRadarForm;
    private ClaudeRadarForm claudeRadarForm;
    private PowerThermalForm powerThermalForm;
    private NetworkMonitorForm networkMonitorForm;
    private ConnectionCheckForm connectionCheckForm;
    private OperationForm operationForm;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime reverseHoverRevealUntilUtc;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool manualForceHoverOpacityActive;
    private bool autoIdleHoverOpacityActive;
    private bool autoMaximizedHoverOpacityActive;
    private bool operationRadialCoreAutoHideKeepAliveActive;
    private bool autoHideKeepAliveActive;
    private ApplicationWindowStateTracker applicationWindowStateTracker;
    private Point lastMouseActivityPosition;
    private bool lastMouseButtonDown;
    private DateTime lastMouseActivityUtc;
    private bool applyingAutomaticHoverOpacityState;
    private DateTime lastSettingsWriteUtc;
    private DateTime lastSampleDiagnosticUtc;
    private FileSystemWatcher settingsWatcher;
    private int settingsReloadRequested = 1;
    private Point lastLoggedPosition;
    private Size lastLoggedSize;
    private bool lastLoggedDesktopAttached;
    private bool positionLogInitialized;
    private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>(StringComparer.Ordinal);
    private readonly CodexQuotaGoalPlanner codexQuotaGoalPlanner;
    private bool formClosing;
    private IntPtr displayPowerNotificationHandle;
    private IntPtr acDcPowerNotificationHandle;
    private IntPtr batteryPowerNotificationHandle;
    private IntPtr powerSchemeNotificationHandle;
    private IntPtr energySaverNotificationHandle;
    private IntPtr effectivePowerModeNotificationHandle;
    private NativeMethods.EffectivePowerModeCallback effectivePowerModeCallback;
    private string pendingDisplayRecoveryReason = string.Empty;
    private int pendingDisplayRecoveryAttempt;
    private DateTime nextSeelenDockPulseLocal;
    private bool winDWatcherStartFailureLogged;
    private bool pendingPowerResumeRestart;
    private bool seelenUiWasRunningBeforePowerSuspend;
    private string seelenUiExecutablePathBeforePowerSuspend = string.Empty;
    private DateTime lastPowerResumeRestartUtc;
    private readonly Dictionary<int, string> registeredGlobalHotkeys = new Dictionary<int, string>();
    private readonly Dictionary<string, string> globalHotkeyRegistrationFailures =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private string globalHotkeyConfigurationSignature = string.Empty;
    private bool globalHotkeyConfigurationApplied;

    public WidgetForm(PdhSampler sampler, EventWaitHandle stopEvent, WidgetSettings settings, bool useDesktopParent)
    {
        this.sampler = sampler;
        this.stopEvent = stopEvent;
        this.useDesktopParent = useDesktopParent;
        this.savedSettings = settings.Clone();
        this.CurrentSettings = settings.Clone();
        this.codexQuotaGoalPlanner = new CodexQuotaGoalPlanner();
        this.manualForceHoverOpacityActive = this.CurrentSettings.ForceHoverOpacityActive;
        this.CurrentSettings.ManualHoverOpacityActive = this.manualForceHoverOpacityActive;
        this.lastMouseActivityPosition = Cursor.Position;
        this.lastMouseActivityUtc = DateTime.UtcNow;
        this.lastSettingsWriteUtc = GetSettingsWriteUtc();
        this.effectivePowerModeCallback = OnEffectivePowerModeChanged;
        InitializeSettingsWatcher();
        this.cpuHistory = new List<double>();
        this.memoryHistory = new List<double>();
        this.memoryHardwareReservedHistory = new List<double>();
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
        this.seelenDockPulseTimer = new System.Windows.Forms.Timer();
        this.seelenDockPulseTimer.Interval = SeelenDockPulseFallbackIntervalMs;
        this.seelenDockPulseTimer.Tick += OnSeelenDockPulseTimerTick;
        this.winDRecoveryTimer = new System.Windows.Forms.Timer();
        this.winDRecoveryTimer.Interval = WinDRecoveryDelayMs;
        this.winDRecoveryTimer.Tick += OnWinDRecoveryTimerTick;
        this.winDWatcher = new GlobalWinDWatcher();
        this.winDWatcher.WinDPressed += OnGlobalWinDPressed;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.Opacity = 1.0;
        this.MinimumSize = ScaleWindowSize(new Size(WidgetSettings.MinWidth, WidgetSettings.MinHeight));
        this.MaximumSize = ScaleWindowSize(new Size(WidgetSettings.MaxWidth, WidgetSettings.MaxHeight));
        this.Size = ScaleWindowSize(new Size(this.CurrentSettings.Width, this.CurrentSettings.Height));
        ApplicationIcon.ApplyTo(this);
        this.ContextMenuStrip = BuildContextMenu();
        BuildNotifyIcon();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = WidgetSettings.GetWidgetSampleIntervalMs(this.CurrentSettings.PerformanceMode);
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.CurrentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Program.LogInfo("Widget shown. Handle=0x" + this.Handle.ToInt64().ToString("X"));
        StartApplicationWindowStateTracking();
        ApplyRuntimeSettings(this.CurrentSettings);
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

        this.childWindowLifecycleStarted = true;
        EnsureRadarChildWindows();
        this.powerThermalForm = new PowerThermalForm(this.CurrentSettings);
        this.powerThermalForm.SetSharedInteractionPolling(true);
        this.powerThermalForm.Show(this);
        this.networkMonitorForm = new NetworkMonitorForm(this.CurrentSettings);
        this.networkMonitorForm.SetSharedInteractionPolling(true);
        this.networkMonitorForm.Show(this);
        this.connectionCheckForm = new ConnectionCheckForm(this.CurrentSettings);
        this.connectionCheckForm.SetSharedInteractionPolling(true);
        this.connectionCheckForm.Show(this);
        this.operationForm = new OperationForm(
            this.CurrentSettings,
            delegate { OpenSettings(); },
            delegate { ForceRefreshAllModules(); },
            delegate { RestartCurrentProcess(); },
            ShowWindowsNotification,
            delegate { return ToggleForcedHoverOpacity(); },
            delegate { return PulseSeelenDockToFront("operation panel", false, false); },
            delegate { return PromptToggleAiRequestBlockingFromOperationPanel(); },
            delegate(bool enabled) { return SetAiRequestBlockingFromOperationPanel(enabled); },
            delegate(bool enabled) { return SetCodexQuotaPlanFromOperationPanel(enabled); },
            delegate(string propertyName, bool enabled) { return SetBooleanSettingFromOperationPanel(propertyName, enabled); });
        this.operationForm.Show(this);
        this.timer.Start();
        UpdateSeelenDockPulseTimer();
        UpdateWinDRecoveryWatcher();
    }

    private void StartApplicationWindowStateTracking()
    {
        if (this.applicationWindowStateTracker != null || !this.IsHandleCreated)
        {
            return;
        }

        this.applicationWindowStateTracker =
            new ApplicationWindowStateTracker(this.Handle, OnApplicationWindowStateEvent);
    }

    private void StopApplicationWindowStateTracking()
    {
        ApplicationWindowStateTracker tracker = this.applicationWindowStateTracker;
        this.applicationWindowStateTracker = null;
        if (tracker != null)
        {
            tracker.Dispose();
        }
    }

    private void OnApplicationWindowStateEvent(uint eventId, IntPtr windowHandle)
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    ProcessApplicationWindowStateEvent(eventId, windowHandle);
                });
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        ProcessApplicationWindowStateEvent(eventId, windowHandle);
    }

    private void ProcessApplicationWindowStateEvent(uint eventId, IntPtr windowHandle)
    {
        if (this.applicationWindowStateTracker == null)
        {
            return;
        }

        this.applicationWindowStateTracker.ProcessWindowEvent(eventId, windowHandle);
        UpdateVisibilityForMode();
        if (eventId == NativeMethods.EVENT_SYSTEM_FOREGROUND &&
            !this.globalLayoutEditActive &&
            this.CurrentSettings != null &&
            this.CurrentSettings.VisibilityMode == WidgetVisibilityMode.AlwaysVisible)
        {
            RestoreApplicationTopMostPriority();
        }

        if (this.CurrentSettings != null && this.CurrentSettings.AutoHoverOpacityMaximizedEnabled)
        {
            UpdateAutomaticHoverOpacityTriggers();
        }
    }

    private void RefreshApplicationWindowState()
    {
        if (this.applicationWindowStateTracker != null)
        {
            this.applicationWindowStateTracker.RefreshAll();
        }
    }

    private void EnsureRadarChildWindows()
    {
        if (!this.childWindowLifecycleStarted || this.formClosing || this.CurrentSettings == null)
        {
            return;
        }

        EnsureCodexRadarWindow();
        EnsureClaudeRadarWindow();
    }

    private void EnsureCodexRadarWindow()
    {
        if (!this.CurrentSettings.CodexRadarEnabled)
        {
            CloseCodexRadarWindow();
            return;
        }

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            return;
        }

        this.codexRadarForm = new CodexRadarForm(this.CurrentSettings, ShowWindowsNotification);
        this.codexRadarForm.SetSharedInteractionPolling(true);
        this.codexRadarForm.Show(this);
        this.codexRadarForm.ApplyRuntimeSettings(this.CurrentSettings);
        if (ShouldHideFormForVisibilityMode(this.codexRadarForm))
        {
            this.codexRadarForm.SetHiddenForFullscreen(true);
        }

        Program.LogInfo("Codex Radar window created from enabled setting.");
    }

    private void EnsureClaudeRadarWindow()
    {
        if (!this.CurrentSettings.ClaudeRadarEnabled)
        {
            CloseClaudeRadarWindow();
            return;
        }

        if (this.claudeRadarForm != null && !this.claudeRadarForm.IsDisposed)
        {
            return;
        }

        this.claudeRadarForm = new ClaudeRadarForm(this.CurrentSettings, ShowWindowsNotification);
        this.claudeRadarForm.SetSharedInteractionPolling(true);
        this.claudeRadarForm.Show(this);
        this.claudeRadarForm.ApplyRuntimeSettings(this.CurrentSettings);
        if (ShouldHideFormForVisibilityMode(this.claudeRadarForm))
        {
            this.claudeRadarForm.SetHiddenForFullscreen(true);
        }

        Program.LogInfo("Claude Radar window created from enabled setting.");
    }

    private void CloseCodexRadarWindow()
    {
        if (this.codexRadarForm == null)
        {
            return;
        }

        CodexRadarForm form = this.codexRadarForm;
        this.codexRadarForm = null;
        if (!form.IsDisposed)
        {
            form.Close();
        }

        Program.LogInfo("Codex Radar window closed from disabled setting.");
    }

    private void CloseClaudeRadarWindow()
    {
        if (this.claudeRadarForm == null)
        {
            return;
        }

        ClaudeRadarForm form = this.claudeRadarForm;
        this.claudeRadarForm = null;
        if (!form.IsDisposed)
        {
            form.Close();
        }

        Program.LogInfo("Claude Radar window closed from disabled setting.");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (this.displayPowerNotificationHandle == IntPtr.Zero)
        {
            this.displayPowerNotificationHandle = NativeMethods.RegisterConsoleDisplayStateNotification(this.Handle);
        }

        if (this.acDcPowerNotificationHandle == IntPtr.Zero)
        {
            this.acDcPowerNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
                this.Handle,
                NativeMethods.GUID_ACDC_POWER_SOURCE);
        }

        if (this.batteryPowerNotificationHandle == IntPtr.Zero)
        {
            this.batteryPowerNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
                this.Handle,
                NativeMethods.GUID_BATTERY_PERCENTAGE_REMAINING);
        }

        if (this.powerSchemeNotificationHandle == IntPtr.Zero)
        {
            this.powerSchemeNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
                this.Handle,
                NativeMethods.GUID_POWERSCHEME_PERSONALITY);
        }

        if (this.energySaverNotificationHandle == IntPtr.Zero)
        {
            this.energySaverNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
                this.Handle,
                NativeMethods.GUID_POWER_SAVING_STATUS);
        }

        if (this.effectivePowerModeNotificationHandle == IntPtr.Zero)
        {
            NativeMethods.TryRegisterEffectivePowerModeNotification(
                this.effectivePowerModeCallback,
                out this.effectivePowerModeNotificationHandle);
        }

        ApplyGlobalHotkeyConfiguration();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterAllGlobalHotkeys();
        this.globalHotkeyConfigurationApplied = false;
        this.globalHotkeyConfigurationSignature = string.Empty;

        if (this.displayPowerNotificationHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerNotification(this.displayPowerNotificationHandle);
            this.displayPowerNotificationHandle = IntPtr.Zero;
        }

        NativeMethods.UnregisterPowerNotification(this.acDcPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.batteryPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.powerSchemeNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.energySaverNotificationHandle);
        NativeMethods.UnregisterEffectivePowerModeNotification(this.effectivePowerModeNotificationHandle);
        this.acDcPowerNotificationHandle = IntPtr.Zero;
        this.batteryPowerNotificationHandle = IntPtr.Zero;
        this.powerSchemeNotificationHandle = IntPtr.Zero;
        this.energySaverNotificationHandle = IntPtr.Zero;
        this.effectivePowerModeNotificationHandle = IntPtr.Zero;

        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.formClosing = true;
        this.childWindowLifecycleStarted = false;
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        this.displayRecoveryTimer.Stop();
        this.displayRecoveryTimer.Tick -= OnDisplayRecoveryTimerTick;
        this.displayRecoveryTimer.Dispose();
        this.seelenDockPulseTimer.Stop();
        this.seelenDockPulseTimer.Tick -= OnSeelenDockPulseTimerTick;
        this.seelenDockPulseTimer.Dispose();
        this.winDRecoveryTimer.Stop();
        this.winDRecoveryTimer.Tick -= OnWinDRecoveryTimerTick;
        this.winDRecoveryTimer.Dispose();
        this.winDWatcher.WinDPressed -= OnGlobalWinDPressed;
        this.winDWatcher.Dispose();
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        StopApplicationWindowStateTracking();
        DisposeSettingsWatcher();
        if (this.settingsForm != null)
        {
            ISettingsWindow settingsWindow = this.settingsForm as ISettingsWindow;
            if (settingsWindow != null)
            {
                settingsWindow.OwnerFormClosing = true;
            }

            this.settingsForm.Close();
            this.settingsForm = null;
        }

        if (this.aiQuickMenuForm != null)
        {
            this.aiQuickMenuForm.Close();
            this.aiQuickMenuForm = null;
        }

        if (this.codexRadarForm != null)
        {
            this.codexRadarForm.Close();
            this.codexRadarForm = null;
        }

        if (this.claudeRadarForm != null)
        {
            this.claudeRadarForm.Close();
            this.claudeRadarForm = null;
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

        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            HandleGlobalHotkey(m.WParam.ToInt32());
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_POWERBROADCAST)
        {
            HandlePowerBroadcast(m.WParam, m.LParam);
        }

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            ApplyDisplayLayoutForCurrentWorkArea();
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
            this.pendingDisplayRecoveryAttempt = 0;
            CapturePowerResumeRestartState();
            PrepareForDisplayInactive("power suspend");
            return;
        }

        if (IsPowerResumeEventType(eventType))
        {
            this.pendingPowerResumeRestart = this.CurrentSettings.PowerResumeRestartEnabled;
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
        if (setting.PowerSetting == NativeMethods.GUID_ACDC_POWER_SOURCE ||
            setting.PowerSetting == NativeMethods.GUID_BATTERY_PERCENTAGE_REMAINING ||
            setting.PowerSetting == NativeMethods.GUID_POWERSCHEME_PERSONALITY ||
            setting.PowerSetting == NativeMethods.GUID_POWER_SAVING_STATUS)
        {
            RefreshAutomaticPerformanceMode("power setting change");
            return;
        }

        if (setting.PowerSetting != NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
        {
            return;
        }

        if (setting.Data == 1)
        {
            ScheduleDisplayRecovery("display powered on");
            return;
        }

        PrepareForDisplayInactive("display powered off");
    }

    private void OnEffectivePowerModeChanged(int mode, IntPtr context)
    {
        WidgetSettings.InvalidateEffectivePerformanceModeCache();
        RefreshAutomaticPerformanceModeFromAnyThread("effective power mode");
    }

    private void RefreshAutomaticPerformanceModeFromAnyThread(string reason)
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    RefreshAutomaticPerformanceMode(reason);
                });
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        RefreshAutomaticPerformanceMode(reason);
    }

    private void RefreshAutomaticPerformanceMode(string reason)
    {
        WidgetSettings.InvalidateEffectivePerformanceModeCache();
        if (this.CurrentSettings.PerformanceMode != WidgetPerformanceMode.WindowsPowerMode)
        {
            return;
        }

        Program.LogInfo("Automatic performance mode refresh. Reason=" + reason);
        ApplyRuntimeSettings(this.CurrentSettings);
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
        this.pendingDisplayRecoveryAttempt = 0;
        this.displayRecoveryTimer.Interval = DisplayRecoveryDelayMs;
        this.displayRecoveryTimer.Stop();
        this.displayRecoveryTimer.Start();
    }

    private void OnDisplayRecoveryTimerTick(object sender, EventArgs e)
    {
        this.displayRecoveryTimer.Stop();
        this.pendingDisplayRecoveryAttempt++;
        int attempt = this.pendingDisplayRecoveryAttempt;
        RecoverAfterDisplayResume(attempt);

        if (!this.formClosing &&
            !this.IsDisposed &&
            this.IsHandleCreated &&
            attempt < DisplayRecoveryMaxAttempts)
        {
            this.displayRecoveryTimer.Interval = DisplayRecoveryRetryDelayMs;
            this.displayRecoveryTimer.Start();
            return;
        }

        Program.LogInfo(
            "Display recovery completed. Reason=" +
            this.pendingDisplayRecoveryReason +
            ", Attempts=" +
            attempt.ToString(CultureInfo.InvariantCulture));
        if (!this.pendingPowerResumeRestart &&
            ShouldPulseSeelenDockAfterDisplayRecovery(this.pendingDisplayRecoveryReason))
        {
            PulseSeelenDockToFront("display recovery: " + this.pendingDisplayRecoveryReason, false, true);
        }

        RestartApplicationAfterPowerResumeIfNeeded(this.pendingDisplayRecoveryReason);

        this.pendingDisplayRecoveryReason = string.Empty;
        this.pendingDisplayRecoveryAttempt = 0;
    }

    internal static bool IsPowerResumeEventType(int eventType)
    {
        return eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL;
    }

    private void RecoverAfterDisplayResume(int attempt)
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        if (this.useDesktopParent)
        {
            DetachFromDesktopLayer("display recovery");
            AttachToDesktopLayer();
        }

        UpdateVisibilityForMode();
        ApplyClickThroughStyle();
        ApplyDisplayLayoutForCurrentWorkArea();
        PositionWidget();
        ResetDisplayRenderResources();
        RenderLayeredWindow();

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.RecoverAfterDisplayResume();
        }

        if (this.claudeRadarForm != null && !this.claudeRadarForm.IsDisposed)
        {
            this.claudeRadarForm.RecoverAfterDisplayResume();
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

        Program.LogInfo(
            "Display recovery pass completed. Reason=" +
            this.pendingDisplayRecoveryReason +
            ", Attempt=" +
            attempt.ToString(CultureInfo.InvariantCulture) +
            "/" +
            DisplayRecoveryMaxAttempts.ToString(CultureInfo.InvariantCulture));
    }

    private void PrepareForDisplayInactive(string reason)
    {
        ResetDisplayRenderResources();

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.PrepareForDisplaySuspend();
        }

        if (this.claudeRadarForm != null && !this.claudeRadarForm.IsDisposed)
        {
            this.claudeRadarForm.PrepareForDisplaySuspend();
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.PrepareForDisplaySuspend();
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.PrepareForDisplaySuspend();
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.PrepareForDisplaySuspend();
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.PrepareForDisplaySuspend();
        }

        Program.LogInfo("Display resources released. Reason=" + reason);
    }

    private void CapturePowerResumeRestartState()
    {
        this.seelenUiWasRunningBeforePowerSuspend = false;
        this.seelenUiExecutablePathBeforePowerSuspend = string.Empty;
        this.pendingPowerResumeRestart = false;
        if (this.CurrentSettings == null || !this.CurrentSettings.PowerResumeRestartEnabled)
        {
            return;
        }

        string exePath;
        this.seelenUiWasRunningBeforePowerSuspend =
            OperationForm.TryCaptureRunningSeelenUiExecutablePath(out exePath);
        this.seelenUiExecutablePathBeforePowerSuspend = exePath ?? string.Empty;
        Program.LogInfo(
            "Power resume restart state captured. SeelenWasRunning=" +
            this.seelenUiWasRunningBeforePowerSuspend.ToString() +
            ", SeelenPathKnown=" +
            (!string.IsNullOrEmpty(this.seelenUiExecutablePathBeforePowerSuspend)).ToString());
    }

    private void RestartApplicationAfterPowerResumeIfNeeded(string reason)
    {
        if (!this.pendingPowerResumeRestart)
        {
            return;
        }

        this.pendingPowerResumeRestart = false;
        if (this.formClosing || this.IsDisposed || !this.CurrentSettings.PowerResumeRestartEnabled)
        {
            Program.LogInfo("Power resume application restart skipped because setting is disabled or form is closing.");
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (this.lastPowerResumeRestartUtc != DateTime.MinValue &&
            (nowUtc - this.lastPowerResumeRestartUtc).TotalSeconds < PowerResumeRestartGuardSeconds)
        {
            Program.LogInfo("Power resume application restart skipped by duplicate guard. Reason=" + reason);
            return;
        }

        this.lastPowerResumeRestartUtc = nowUtc;
        OperationForm.RestartSeelenUiForApplicationRestart(
            "power resume: " + reason,
            this.seelenUiExecutablePathBeforePowerSuspend,
            this.seelenUiWasRunningBeforePowerSuspend);
        Program.LogInfo("Application restart requested after power resume. Reason=" + reason);
        Program.RestartApplication(this.useDesktopParent);
        this.Close();
    }

    private static bool ShouldPulseSeelenDockAfterDisplayRecovery(string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return false;
        }

        return reason.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("power resume", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("session unlock", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void UpdateSeelenDockPulseTimer()
    {
        if (this.formClosing || this.IsDisposed)
        {
            return;
        }

        if (!this.CurrentSettings.SeelenDockForegroundPulseEnabled)
        {
            this.seelenDockPulseTimer.Stop();
            this.nextSeelenDockPulseLocal = DateTime.MinValue;
            return;
        }

        ScheduleNextSeelenDockPulse(DateTime.Now);
    }

    private void ScheduleNextSeelenDockPulse(DateTime nowLocal)
    {
        DateTime nextLocal = GetNextSeelenDockPulseLocalTime(nowLocal);
        this.nextSeelenDockPulseLocal = nextLocal;
        int interval = (int)Math.Max(
            1000.0,
            Math.Min(
                int.MaxValue,
                Math.Ceiling((nextLocal - nowLocal).TotalMilliseconds)));
        this.seelenDockPulseTimer.Interval = interval;
        this.seelenDockPulseTimer.Stop();
        this.seelenDockPulseTimer.Start();
    }

    private static DateTime GetNextSeelenDockPulseLocalTime(DateTime nowLocal)
    {
        DateTime hour = new DateTime(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            nowLocal.Hour,
            0,
            0,
            nowLocal.Kind);
        DateTime nextLocal = nowLocal.Minute < 30
            ? hour.AddMinutes(30)
            : hour.AddHours(1);
        if (nextLocal <= nowLocal.AddSeconds(1))
        {
            nextLocal = nextLocal.Minute == 0
                ? nextLocal.AddMinutes(30)
                : nextLocal.AddMinutes(30);
        }

        return nextLocal;
    }

    private void OnSeelenDockPulseTimerTick(object sender, EventArgs e)
    {
        this.seelenDockPulseTimer.Stop();
        if (this.formClosing || this.IsDisposed)
        {
            return;
        }

        if (!this.CurrentSettings.SeelenDockForegroundPulseEnabled)
        {
            this.nextSeelenDockPulseLocal = DateTime.MinValue;
            return;
        }

        DateTime nowLocal = DateTime.Now;
        if (this.nextSeelenDockPulseLocal == DateTime.MinValue ||
            nowLocal < this.nextSeelenDockPulseLocal.AddSeconds(-1))
        {
            ScheduleNextSeelenDockPulse(nowLocal);
            return;
        }

        PulseSeelenDockToFront(
            "scheduled " + this.nextSeelenDockPulseLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            true,
            true);
        ScheduleNextSeelenDockPulse(nowLocal.AddSeconds(1));
    }

    private void UpdateWinDRecoveryWatcher()
    {
        if (this.formClosing || this.IsDisposed)
        {
            return;
        }

        this.winDRecoveryTimer.Stop();
        if (!this.CurrentSettings.WinDRecoveryPulseEnabled)
        {
            this.winDWatcher.Stop();
            return;
        }

        int errorCode;
        if (!this.winDWatcher.Start(out errorCode) && !this.winDWatcherStartFailureLogged)
        {
            this.winDWatcherStartFailureLogged = true;
            Program.LogInfo("Win+D recovery watcher failed to start. Win32Error=" + errorCode.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void OnGlobalWinDPressed(object sender, EventArgs e)
    {
        if (this.formClosing || this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate { OnGlobalWinDPressed(sender, e); });
            }
            catch
            {
            }

            return;
        }

        if (!this.CurrentSettings.WinDRecoveryPulseEnabled)
        {
            return;
        }

        this.winDRecoveryTimer.Stop();
        this.winDRecoveryTimer.Interval = WinDRecoveryDelayMs;
        this.winDRecoveryTimer.Start();
        Program.LogInfo("Win+D recovery pulse scheduled after " + WinDRecoveryDelayMs.ToString(CultureInfo.InvariantCulture) + " ms.");
    }

    private void OnWinDRecoveryTimerTick(object sender, EventArgs e)
    {
        this.winDRecoveryTimer.Stop();
        if (this.formClosing || this.IsDisposed || !this.CurrentSettings.WinDRecoveryPulseEnabled)
        {
            return;
        }

        bool seelenSuccess = PulseSeelenDockToFront("Win+D delayed recovery", false, false);
        RestoreApplicationTopMostPriority();
        Program.LogInfo("Win+D recovery pulse completed. SeelenSuccess=" + seelenSuccess.ToString());
    }

    private bool PulseSeelenDockToFront(string reason, bool skipWhenForegroundMaximizedOrFullscreen, bool respectSetting)
    {
        if (respectSetting && !this.CurrentSettings.SeelenDockForegroundPulseEnabled)
        {
            return false;
        }

        if (skipWhenForegroundMaximizedOrFullscreen &&
            IsAnyApplicationWindowMaximizedOrFullscreen())
        {
            Program.LogInfo("Seelen dock foreground pulse skipped because foreground window is maximized or fullscreen. Reason=" + reason);
            return false;
        }

        string detail;
        bool success = NativeMethods.TryPulseSeelenDockWindowToFront(out detail);
        Program.LogInfo(
            "Seelen dock foreground pulse. Reason=" +
            reason +
            ", Success=" +
            success.ToString() +
            ", Detail=" +
            detail);
        if (success)
        {
            RestoreApplicationTopMostPriority();
        }

        return success;
    }

    private void RestoreApplicationTopMostPriority()
    {
        if (this.CurrentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly)
        {
            return;
        }

        // Enumerating the process covers independently owned overlays such as dock tabs and task
        // boards, which a fixed Form field list cannot keep complete as new surfaces are added.
        NativeMethods.RestoreCurrentProcessTopMostWindows(
            this.CurrentSettings.CodexPetZOrderProtectionEnabled);
    }

    private bool ApplyDisplayLayoutForCurrentWorkArea()
    {
        WidgetSettings adjustedSettings = this.CurrentSettings.Clone();
        if (!adjustedSettings.AdaptToCurrentWorkArea())
        {
            return false;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Program.LogInfo(string.Format(
            "Display layout adapted to work area {0},{1},{2},{3}.",
            workArea.Left,
            workArea.Top,
            workArea.Width,
            workArea.Height));
        ApplyRuntimeSettings(adjustedSettings);
        return true;
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
        this.notifyIconImage = ApplicationIcon.CreateIcon();
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
        RefreshNightScheduleAtExistingTick();
        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.RefreshNightScheduleFromOwnerTick();
        }

        long tickStart = TimingStats.StartTimestamp();
        UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:start");
        if (this.stopEvent.WaitOne(0))
        {
            TimingStats.RecordElapsed("widget.main_tick", tickStart);
            UiHangWatchdog.MarkUiHeartbeat("widget.main_tick:stop_requested");
            this.Close();
            return;
        }

        try
        {
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:reload_settings");
            ReloadSettingsIfChanged();
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:refresh_window_state");
            RefreshApplicationWindowState();
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:update_visibility");
            UpdateVisibilityForMode();
            if (!this.hiddenForFullscreen &&
                ShouldRefreshBurnInPosition())
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:position_burn_in_shift");
                PositionWidget();
            }

            if (this.operationForm != null && !this.operationForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:operation_maintenance");
                this.operationForm.ProcessSharedMaintenanceTick();
            }

            if (this.codexQuotaGoalPlanner != null)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:codex_quota_goal_plan");
                Action<string, string, ToolTipIcon> quotaNotification =
                    AlertPresentationPolicy.ShouldPresent(this.CurrentSettings, AlertPresentationCategory.Quota)
                        ? (Action<string, string, ToolTipIcon>)ShowWindowsNotification
                        : null;
                this.codexQuotaGoalPlanner.ProcessMaintenanceTick(this.CurrentSettings, quotaNotification);
            }

            if (this.hiddenForFullscreen &&
                WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode) == WidgetPerformanceMode.BatterySaver)
            {
                // Keep the control tick alive for settings, stop, and visibility checks, but skip PDH sampling.
                return;
            }

            long sampleStart = TimingStats.StartTimestamp();
            try
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:pdh_sample");
                this.snapshot = this.sampler.Sample(
                    WidgetSettings.GetExpensiveHardwareSampleIntervalMs(this.CurrentSettings.PerformanceMode));
            }
            finally
            {
                TimingStats.RecordElapsed("widget.pdh_sample", sampleStart);
            }

            AddHistory(this.cpuHistory, this.snapshot.CpuPercent);
            AddHistory(this.memoryHistory, this.snapshot.MemoryPercent);
            AddHistory(this.memoryHardwareReservedHistory, this.snapshot.MemoryHardwareReservedPercent);
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
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:update_alerts");
            UpdateAlertIconStates();
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:render");
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
        finally
        {
            TimingStats.RecordElapsed("widget.main_tick", tickStart);
            UiHangWatchdog.MarkUiHeartbeat("widget.main_tick:complete");
        }

        try
        {
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:timing_summary");
            TimingStats.TryLogSummary(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            UiHangWatchdog.MarkUiHeartbeat("widget.main_tick:post_summary_complete");
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
        NativeMethods.SetWindowPos(
            this.Handle,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
        this.desktopAttached = true;
        Program.LogInfo("Attached to desktop host. Host=0x" + desktopHost.ToInt64().ToString("X"));
    }

    private void DetachFromDesktopLayer(string reason)
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        NativeMethods.SetParent(this.Handle, IntPtr.Zero);
        int style = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_STYLE);
        style = (style | NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE) & ~NativeMethods.WS_CHILD;
        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_STYLE, style);
        NativeMethods.SetWindowPos(
            this.Handle,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);

        if (this.desktopAttached)
        {
            Program.LogInfo("Detached from desktop host. Reason=" + reason);
        }

        this.desktopAttached = false;
    }

    private void PositionWidget()
    {
        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
        Point location = CalculateLocation(workArea);
        int left = location.X;
        int top = location.Y;
        Point shiftedLocation = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.MainWidgetSalt);
        left = shiftedLocation.X;
        top = shiftedLocation.Y;
        this.Location = new Point(left, top);
        uint flags =
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED;

        if (!this.hiddenForFullscreen)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }

        if (this.CurrentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly && !this.useDesktopParent)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(this.CurrentSettings.VisibilityMode, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
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
        int left = this.CurrentSettings.MapResolutionCompatibilityLeft(WidgetSettings.ModuleMain, workArea, this.CurrentSettings.LeftX);
        int bottom = this.CurrentSettings.MapResolutionCompatibilityBottom(WidgetSettings.ModuleMain, workArea, this.CurrentSettings.BottomY);
        int top = bottom - this.Height + 1;
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
        nextSettings.ForceHoverOpacityActive = false;
        nextSettings.ManualHoverOpacityActive = false;
        this.manualForceHoverOpacityActive = false;
        nextSettings.Save();
        Program.SetStartupEnabled(nextSettings.StartupEnabled, false);
        this.savedSettings = nextSettings.Clone();
        ApplyRuntimeSettings(this.savedSettings);
        this.lastSettingsWriteUtc = GetSettingsWriteUtc();
        Program.LogInfo("Settings saved.");
    }

    internal bool TryEditGlobalLayout(WidgetSettings settings, out WidgetSettings editedSettings)
    {
        editedSettings = null;
        WidgetSettings editingBaseline = settings.Clone();
        editingBaseline.Normalize();
        bool previousGlobalLayoutEditActive = this.globalLayoutEditActive;
        Program.LogInfo("Global layout edit requested from settings.");

        this.globalLayoutEditActive = true;
        try
        {
            ApplyRuntimeSettings(CreateGlobalLayoutEditRuntimeSettings(editingBaseline));
            RestoreApplicationTopMostPriority();

            using (GlobalLayoutEditorForm editor = new GlobalLayoutEditorForm(
                editingBaseline,
                delegate(WidgetSettings previewSettings)
                {
                    PreviewSettings(CreateGlobalLayoutEditRuntimeSettings(previewSettings));
                    RestoreApplicationTopMostPriority();
                },
                delegate { RestoreApplicationTopMostPriority(); }))
            {
                DialogResult result = editor.ShowDialog();
                if (result == DialogResult.OK && editor.EditedSettings != null)
                {
                    this.globalLayoutEditActive = previousGlobalLayoutEditActive;
                    SaveSettings(editor.EditedSettings);
                    editedSettings = this.savedSettings.Clone();
                    Program.LogInfo("Global layout edit saved.");
                    return true;
                }
            }
        }
        finally
        {
            this.globalLayoutEditActive = previousGlobalLayoutEditActive;
        }

        ApplyRuntimeSettings(editingBaseline);
        Program.LogInfo("Global layout edit canceled.");
        return false;
    }

    private WidgetSettings CreateGlobalLayoutEditRuntimeSettings(WidgetSettings settings)
    {
        WidgetSettings runtimeSettings = settings.Clone();
        runtimeSettings.Normalize();
        runtimeSettings.VisibilityMode = WidgetVisibilityMode.AlwaysVisible;
        runtimeSettings.HoverOpacityEnabled = false;
        runtimeSettings.ForceHoverOpacityActive = false;
        runtimeSettings.ManualHoverOpacityActive = false;
        runtimeSettings.AutoHoverOpacityIdleEnabled = false;
        runtimeSettings.AutoHoverOpacityMaximizedEnabled = false;
        runtimeSettings.ResolutionCompatibilityModeEnabled = false;
        return runtimeSettings;
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
        this.sampler.RequestDiskUsageRefresh();
        OnTimerTick(this, EventArgs.Empty);

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.ForceRefresh();
        }

        if (this.claudeRadarForm != null && !this.claudeRadarForm.IsDisposed)
        {
            this.claudeRadarForm.ForceRefresh("操作面板刷新");
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

    internal bool ToggleForcedHoverOpacity()
    {
        this.manualForceHoverOpacityActive = !this.manualForceHoverOpacityActive;
        Program.LogInfo(
            "Forced hover opacity toggled from shared action. ManualActive=" +
            this.manualForceHoverOpacityActive.ToString());
        ApplyCombinedHoverOpacityState("operation panel toggle");
        return this.CurrentSettings.ForceHoverOpacityActive;
    }

    internal bool TryGetGlobalHotkeyRegistrationFailure(string settingName, out string failure)
    {
        return this.globalHotkeyRegistrationFailures.TryGetValue(settingName ?? string.Empty, out failure);
    }

    private void ApplyGlobalHotkeyConfiguration()
    {
        if (!this.IsHandleCreated || this.CurrentSettings == null)
        {
            return;
        }

        string signature = string.Join(
            "\n",
            this.CurrentSettings.HotkeyToggleAllWindows ?? string.Empty,
            this.CurrentSettings.HotkeyToggleHoverOpacity ?? string.Empty,
            this.CurrentSettings.HotkeyOpenSettings ?? string.Empty);
        if (this.globalHotkeyConfigurationApplied &&
            string.Equals(signature, this.globalHotkeyConfigurationSignature, StringComparison.Ordinal))
        {
            return;
        }

        // Registration is replaced atomically per normalized settings snapshot. Remembering the
        // signature prevents preview ticks and settings-window reopen cycles from retrying a
        // conflicting binding without an actual setting change.
        UnregisterAllGlobalHotkeys();
        this.globalHotkeyRegistrationFailures.Clear();
        RegisterGlobalHotkey(
            HotkeyToggleAllWindowsId,
            "HotkeyToggleAllWindows",
            this.CurrentSettings.HotkeyToggleAllWindows);
        RegisterGlobalHotkey(
            HotkeyToggleHoverOpacityId,
            "HotkeyToggleHoverOpacity",
            this.CurrentSettings.HotkeyToggleHoverOpacity);
        RegisterGlobalHotkey(
            HotkeyOpenSettingsId,
            "HotkeyOpenSettings",
            this.CurrentSettings.HotkeyOpenSettings);
        this.globalHotkeyConfigurationSignature = signature;
        this.globalHotkeyConfigurationApplied = true;
    }

    private void RegisterGlobalHotkey(int id, string settingName, string text)
    {
        GlobalHotkeyBinding binding;
        if (!GlobalHotkeyParser.TryParse(text, out binding))
        {
            return;
        }

        int win32Error;
        if (NativeMethods.TryRegisterGlobalHotkey(
            this.Handle,
            id,
            binding.Modifiers,
            binding.VirtualKey,
            out win32Error))
        {
            this.registeredGlobalHotkeys[id] = settingName;
            Program.LogInfo(
                "Global hotkey registered. Setting=" + settingName +
                " Binding=" + binding.NormalizedText);
            return;
        }

        string failure = "注册失败（Win32 " + win32Error.ToString(CultureInfo.InvariantCulture) + "）";
        this.globalHotkeyRegistrationFailures[settingName] = failure;
        Program.LogInfo(
            "Global hotkey registration failed. Setting=" + settingName +
            " Binding=" + binding.NormalizedText +
            " Win32Error=" + win32Error.ToString(CultureInfo.InvariantCulture));
    }

    private void UnregisterAllGlobalHotkeys()
    {
        if (!this.IsHandleCreated)
        {
            this.registeredGlobalHotkeys.Clear();
            return;
        }

        foreach (int id in this.registeredGlobalHotkeys.Keys)
        {
            NativeMethods.UnregisterGlobalHotkey(this.Handle, id);
        }

        this.registeredGlobalHotkeys.Clear();
    }

    private void HandleGlobalHotkey(int id)
    {
        if (!this.registeredGlobalHotkeys.ContainsKey(id))
        {
            return;
        }

        if (id == HotkeyToggleAllWindowsId)
        {
            this.manualAllWindowsHidden = !this.manualAllWindowsHidden;
            Program.LogInfo(
                "Global hotkey action. Action=toggle_all_windows Hidden=" +
                this.manualAllWindowsHidden.ToString());
            UpdateVisibilityForMode();
            return;
        }

        if (id == HotkeyToggleHoverOpacityId)
        {
            Program.LogInfo("Global hotkey action. Action=toggle_hover_opacity");
            ToggleForcedHoverOpacity();
            return;
        }

        if (id == HotkeyOpenSettingsId)
        {
            Program.LogInfo("Global hotkey action. Action=open_settings");
            OpenSettings();
        }
    }

    internal bool PromptToggleAiRequestBlockingFromOperationPanel()
    {
        ShowAiQuickMenuFromOperationPanel();
        return true;
    }

    internal bool SetAiRequestBlockingFromOperationPanel(bool enabled)
    {
        bool currentlyBlocked = this.CurrentSettings != null &&
            this.CurrentSettings.AiRequestProtectionManualBlockEnabled;
        if (currentlyBlocked == enabled)
        {
            return true;
        }

        WidgetSettings nextSettings = this.savedSettings == null
            ? (this.CurrentSettings == null ? WidgetSettings.CreateDefaults() : this.CurrentSettings.Clone())
            : this.savedSettings.Clone();
        nextSettings.AiRequestProtectionManualBlockEnabled = enabled;
        SaveSettings(nextSettings);

        if (enabled)
        {
            AiExternalToolBlockResult blockResult = AiExternalToolBlocker.TryStopKnownTools();
            ShowWindowsNotification(
                "AI 阻断已启用",
                blockResult == null ? "手动阻断已开启。" : blockResult.Summary,
                blockResult != null && blockResult.FailedCount > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        else
        {
            ShowWindowsNotification(
                "AI 阻断已关闭",
                "手动阻断已关闭，自动模式仍按设置工作。",
                ToolTipIcon.Info);
        }

        return true;
    }

    internal bool SetCodexQuotaPlanFromOperationPanel(bool enabled)
    {
        bool currentlyEnabled = this.CurrentSettings != null &&
            this.CurrentSettings.CodexQuotaPlanEnabled;
        if (currentlyEnabled == enabled)
        {
            return true;
        }

        WidgetSettings nextSettings = this.savedSettings == null
            ? (this.CurrentSettings == null ? WidgetSettings.CreateDefaults() : this.CurrentSettings.Clone())
            : this.savedSettings.Clone();
        nextSettings.CodexQuotaPlanEnabled = enabled;
        SaveSettings(nextSettings);

        ShowWindowsNotification(
            enabled ? "Codex 额度计划已启用" : "Codex 额度计划已关闭",
            enabled
                ? "具体阈值和 goal 列表在普通设置中调整。"
                : "额度计划不会再自动暂停或恢复 goal。",
            ToolTipIcon.Info);
        return true;
    }

    internal bool SetBooleanSettingFromOperationPanel(string propertyName, bool enabled)
    {
        if (string.Equals(propertyName, "AiRequestProtectionManualBlockEnabled", StringComparison.Ordinal))
        {
            return SetAiRequestBlockingFromOperationPanel(enabled);
        }

        if (string.Equals(propertyName, "CodexQuotaPlanEnabled", StringComparison.Ordinal))
        {
            return SetCodexQuotaPlanFromOperationPanel(enabled);
        }

        try
        {
            PropertyInfo property = typeof(WidgetSettings).GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanRead || !property.CanWrite)
            {
                ShowWindowsNotification(
                    "设置切换失败",
                    "未找到可切换的布尔设置：" + propertyName,
                    ToolTipIcon.Warning);
                return false;
            }

            WidgetSettings nextSettings = this.savedSettings == null
                ? (this.CurrentSettings == null ? WidgetSettings.CreateDefaults() : this.CurrentSettings.Clone())
                : this.savedSettings.Clone();
            bool currentValue = (bool)property.GetValue(nextSettings, null);
            if (currentValue == enabled)
            {
                return true;
            }

            property.SetValue(nextSettings, enabled, null);
            SaveSettings(nextSettings);
            ShowWindowsNotification(
                enabled ? "设置已开启" : "设置已关闭",
                propertyName,
                ToolTipIcon.Info);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowWindowsNotification(
                "设置切换失败",
                propertyName + " 保存失败。",
                ToolTipIcon.Warning);
            return false;
        }
    }

    private void ShowAiQuickMenuFromOperationPanel()
    {
        if (IsReusableSettingsWindow(this.aiQuickMenuForm))
        {
            ShowSettingsWindow(this.aiQuickMenuForm, "existing special function menu");
            return;
        }

        CleanupAiQuickMenuReference(this.aiQuickMenuForm, "stale special function menu before open");
        WidgetSettings baseline = this.savedSettings == null
            ? (this.CurrentSettings == null ? WidgetSettings.CreateDefaults() : this.CurrentSettings.Clone())
            : this.savedSettings.Clone();
        baseline.Normalize();
        Form quickMenu = new AiQuickMenuForm(this, baseline);
        this.aiQuickMenuForm = quickMenu;
        quickMenu.FormClosed += OnAiQuickMenuFormClosed;
        quickMenu.Disposed += OnAiQuickMenuFormDisposed;
        PositionAiQuickMenu(quickMenu);
        try
        {
            quickMenu.Show(this);
            ShowSettingsWindow(quickMenu, "new special function menu");
        }
        catch
        {
            CleanupAiQuickMenuReference(quickMenu, "special function menu open failed");
            throw;
        }
    }

    private void PositionAiQuickMenu(Form quickMenu)
    {
        if (quickMenu == null)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            workArea = Screen.FromControl(this.operationForm).WorkingArea;
            Point operationTopLeft = this.operationForm.PointToScreen(Point.Empty);
            int left = operationTopLeft.X;
            int top = operationTopLeft.Y - quickMenu.Height - 10;
            if (top < workArea.Top)
            {
                top = operationTopLeft.Y + this.operationForm.Height + 10;
            }

            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - quickMenu.Width));
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - quickMenu.Height));
            quickMenu.Location = new Point(left, top);
            return;
        }

        quickMenu.Location = new Point(
            Math.Max(workArea.Left, Math.Min(this.Left, workArea.Right - quickMenu.Width)),
            Math.Max(workArea.Top, Math.Min(this.Top, workArea.Bottom - quickMenu.Height)));
    }

    private void OnAiQuickMenuFormClosed(object sender, FormClosedEventArgs e)
    {
        CleanupAiQuickMenuReference(sender as Form, "special function menu closed");
    }

    private void OnAiQuickMenuFormDisposed(object sender, EventArgs e)
    {
        CleanupAiQuickMenuReference(sender as Form, "special function menu disposed");
    }

    private void CleanupAiQuickMenuReference(Form form, string reason)
    {
        if (form == null || !object.ReferenceEquals(form, this.aiQuickMenuForm))
        {
            return;
        }

        form.FormClosed -= OnAiQuickMenuFormClosed;
        form.Disposed -= OnAiQuickMenuFormDisposed;
        this.aiQuickMenuForm = null;
        Program.LogInfo("Special function menu reference cleared. Reason=" + reason + ".");
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
        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:start");
        WidgetSettings nextSettings = settings.Clone();
        nextSettings.Normalize();
        if (!this.applyingAutomaticHoverOpacityState)
        {
            this.manualForceHoverOpacityActive = nextSettings.ForceHoverOpacityActive;
        }

        if (!nextSettings.AutoHoverOpacityIdleEnabled)
        {
            this.autoIdleHoverOpacityActive = false;
        }

        if (!nextSettings.AutoHoverOpacityMaximizedEnabled)
        {
            this.autoMaximizedHoverOpacityActive = false;
        }

        if (this.manualForceHoverOpacityActive || !nextSettings.OperationRadialCoreAutoHideKeepAliveEnabled)
        {
            SetOperationRadialCoreAutoHideKeepAliveActive(false);
        }

        nextSettings.ForceHoverOpacityActive = IsCombinedHoverOpacityActive();
        nextSettings.ManualHoverOpacityActive = this.manualForceHoverOpacityActive;
        this.CurrentSettings = nextSettings;
        ApplyGlobalHotkeyConfiguration();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.MinimumSize = ScaleWindowSize(new Size(WidgetSettings.MinWidth, WidgetSettings.MinHeight));
        this.MaximumSize = ScaleWindowSize(new Size(WidgetSettings.MaxWidth, WidgetSettings.MaxHeight));
        Program.ApplyPerformanceMode(this.CurrentSettings.PerformanceMode);
        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:timers");
        ApplyPerformanceTimerIntervals();
        UpdateSeelenDockPulseTimer();
        UpdateWinDRecoveryWatcher();

        Size desiredSize = ScaleWindowSize(new Size(this.CurrentSettings.Width, this.CurrentSettings.Height));
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.CurrentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:click_through");
        ApplyClickThroughStyle();
        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:hover_timer");
        UpdateHoverAnimationTimer();

        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:set_window_pos");
        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(shouldBeTopMost, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:position_widget");
        PositionWidget();
        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:update_visibility");
        UpdateVisibilityForMode();
        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:radar_lifecycle");
        EnsureRadarChildWindows();
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_codex_radar");
            this.codexRadarForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        if (this.claudeRadarForm != null && !this.claudeRadarForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_claude_radar");
            this.claudeRadarForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_power_thermal");
            this.powerThermalForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_network");
            this.networkMonitorForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_connection");
            this.connectionCheckForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_operation");
            this.operationForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:render");
        RenderLayeredWindow();
        UiHangWatchdog.MarkUiHeartbeat("apply_runtime_settings:complete");
    }

    private void ApplyPerformanceTimerIntervals()
    {
        int sampleInterval = GetCurrentWidgetTimerIntervalMs();
        if (this.timer.Interval != sampleInterval)
        {
            this.timer.Interval = sampleInterval;
        }

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.CurrentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
    }

    private int GetCurrentWidgetTimerIntervalMs()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode);
        if (!this.hiddenForFullscreen)
        {
            return WidgetSettings.GetWidgetSampleIntervalMs(mode);
        }

        // Hidden windows still need to notice when the foreground app leaves fullscreen.
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return WidgetSettings.GetWidgetSampleIntervalMs(mode);
        }

        return mode == WidgetPerformanceMode.BatterySaver ? 5000 : 2500;
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
        UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:start");
        try
        {
            UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:update_automatic_triggers");
            bool automaticStateChanged = UpdateAutomaticHoverOpacityTriggers();
            UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:apply_click_through");
            ApplyClickThroughStyle();
            UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:update_animation");
            bool opacityChanged = UpdateHoverOpacityAnimation();
            bool hoverTarget = IsHoverOpacityTargetActive();
            bool animationActive =
                automaticStateChanged ||
                Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
            // All passive panels share this UI-thread timer so hover support costs one
            // message-pump wakeup instead of one wakeup per window.
            if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:codex_radar_shared_tick");
                animationActive |= this.codexRadarForm.ProcessSharedInteractionTick();
            }

            if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:power_shared_tick");
                animationActive |= this.powerThermalForm.ProcessSharedInteractionTick();
            }

            if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:network_shared_tick");
                animationActive |= this.networkMonitorForm.ProcessSharedInteractionTick();
            }

            if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:connection_shared_tick");
                animationActive |= this.connectionCheckForm.ProcessSharedInteractionTick();
            }

            if (this.operationForm != null && !this.operationForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:operation_shared_tick");
                animationActive |= this.operationForm.ProcessSharedInteractionTick();
            }

            int desiredInterval = animationActive
                ? WidgetSettings.GetHoverAnimationIntervalMs(this.CurrentSettings.PerformanceMode)
                : WidgetSettings.GetInteractionIdlePollingIntervalMs(this.CurrentSettings.PerformanceMode);
            if (this.hoverTimer.Interval != desiredInterval)
            {
                this.hoverTimer.Interval = desiredInterval;
            }

            if (opacityChanged)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:render_opacity");
                RenderLayeredWindow(false);
            }
        }
        finally
        {
            UiHangWatchdog.MarkUiHeartbeat("widget.hover_tick:complete");
        }
    }

    private bool UpdateAutomaticHoverOpacityTriggers()
    {
        if (this.CurrentSettings == null)
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        UpdateMouseActivityState(nowUtc);
        bool keepAliveStateChanged = UpdateOperationRadialCoreAutoHideKeepAlive(nowUtc);

        bool idleActive = false;
        if (this.CurrentSettings.AutoHoverOpacityIdleEnabled)
        {
            int idleSeconds = Math.Max(
                WidgetSettings.MinAutoHoverOpacityIdleSeconds,
                Math.Min(
                    WidgetSettings.MaxAutoHoverOpacityIdleSeconds,
                    this.CurrentSettings.AutoHoverOpacityIdleSeconds));
            idleActive = (nowUtc - this.lastMouseActivityUtc).TotalSeconds >= idleSeconds;
        }

        bool maximizedActive =
            this.CurrentSettings.AutoHoverOpacityMaximizedEnabled &&
            IsAnyApplicationWindowMaximizedOrFullscreen();

        if (this.operationRadialCoreAutoHideKeepAliveActive)
        {
            idleActive = false;
            maximizedActive = false;
        }

        if (idleActive == this.autoIdleHoverOpacityActive &&
            maximizedActive == this.autoMaximizedHoverOpacityActive)
        {
            return keepAliveStateChanged;
        }

        this.autoIdleHoverOpacityActive = idleActive;
        this.autoMaximizedHoverOpacityActive = maximizedActive;
        Program.LogInfo(
            "Automatic hover opacity state changed. IdleActive=" +
            idleActive.ToString() +
            ", MaximizedActive=" +
            maximizedActive.ToString() +
            ", ManualActive=" +
            this.manualForceHoverOpacityActive.ToString());
        UiHangWatchdog.MarkUiCheckpoint("hover.apply_combined:automatic trigger");
        ApplyCombinedHoverOpacityState("automatic trigger");
        return true;
    }

    private bool UpdateOperationRadialCoreAutoHideKeepAlive(DateTime nowUtc)
    {
        bool active = false;
        if (this.CurrentSettings != null &&
            this.CurrentSettings.OperationRadialCoreAutoHideKeepAliveEnabled &&
            !this.manualForceHoverOpacityActive &&
            this.operationForm != null &&
            !this.operationForm.IsDisposed)
        {
            active = this.operationForm.IsRadialCoreAutoHideKeepAliveActive();
        }

        if (active)
        {
            this.lastMouseActivityPosition = Cursor.Position;
            this.lastMouseButtonDown = NativeMethods.IsAnyMouseButtonDown();
            this.lastMouseActivityUtc = nowUtc;
        }

        return SetOperationRadialCoreAutoHideKeepAliveActive(active);
    }

    private bool SetOperationRadialCoreAutoHideKeepAliveActive(bool active)
    {
        if (this.operationRadialCoreAutoHideKeepAliveActive == active)
        {
            return false;
        }

        this.operationRadialCoreAutoHideKeepAliveActive = active;
        SetAutoHideKeepAliveActive(active);

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.SetAutoHideKeepAliveActive(active);
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.SetAutoHideKeepAliveActive(active);
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.SetAutoHideKeepAliveActive(active);
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.SetAutoHideKeepAliveActive(active);
        }

        return true;
    }

    private void SetAutoHideKeepAliveActive(bool active)
    {
        if (this.autoHideKeepAliveActive == active)
        {
            return;
        }

        this.autoHideKeepAliveActive = active;
        if (active)
        {
            this.hoverOpacityDelayState.Reset();
            this.reverseHoverRevealUntilUtc = DateTime.MinValue;
        }
    }

    private bool IsAnyApplicationWindowMaximizedOrFullscreen()
    {
        if (this.applicationWindowStateTracker == null)
        {
            return false;
        }

        return this.applicationWindowStateTracker.HasMaximizedOrFullscreenWindow();
    }

    private void UpdateMouseActivityState(DateTime nowUtc)
    {
        Point cursor = Cursor.Position;
        bool mouseButtonDown = NativeMethods.IsAnyMouseButtonDown();
        if (cursor != this.lastMouseActivityPosition || mouseButtonDown != this.lastMouseButtonDown || mouseButtonDown)
        {
            this.lastMouseActivityPosition = cursor;
            this.lastMouseButtonDown = mouseButtonDown;
            if (ShouldSuppressAutomaticHoverOpacityRelease(cursor, mouseButtonDown))
            {
                return;
            }

            this.lastMouseActivityUtc = nowUtc;
        }
    }

    private bool ShouldSuppressAutomaticHoverOpacityRelease(Point cursor, bool mouseButtonDown)
    {
        if (this.CurrentSettings == null ||
            !this.CurrentSettings.HoverOpacityCoverEnabled ||
            (!this.autoIdleHoverOpacityActive && !this.autoMaximizedHoverOpacityActive))
        {
            return false;
        }

        // Cover mode suppresses stray mouse movement while automatic hiding is active.
        // Actual interaction still exits hidden state once the cursor reaches any app window.
        if (mouseButtonDown)
        {
            return false;
        }

        return !IsPointInAnyManagedWindowActivationRange(cursor);
    }

    private bool IsPointInAnyManagedWindowActivationRange(Point cursor)
    {
        return IsPointInFormActivationRange(this.CurrentSettings, this, cursor) ||
            IsPointInFormActivationRange(this.CurrentSettings, this.codexRadarForm, cursor) ||
            IsPointInFormActivationRange(this.CurrentSettings, this.claudeRadarForm, cursor) ||
            IsPointInFormActivationRange(this.CurrentSettings, this.powerThermalForm, cursor) ||
            IsPointInFormActivationRange(this.CurrentSettings, this.networkMonitorForm, cursor) ||
            IsPointInFormActivationRange(this.CurrentSettings, this.connectionCheckForm, cursor) ||
            IsPointInFormActivationRange(this.CurrentSettings, this.operationForm, cursor);
    }

    private static bool IsPointInFormActivationRange(WidgetSettings settings, Form form, Point cursor)
    {
        return form != null &&
            !form.IsDisposed &&
            form.Visible &&
            HoverInteractionPolicy.IsPointInActivationRange(settings, cursor, form.Bounds);
    }

    private void ApplyCombinedHoverOpacityState(string reason)
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        bool combined = IsCombinedHoverOpacityActive();
        bool manualStateChanged = this.CurrentSettings.ManualHoverOpacityActive != this.manualForceHoverOpacityActive;
        if (this.CurrentSettings.ForceHoverOpacityActive == combined && !manualStateChanged)
        {
            return;
        }

        WidgetSettings nextSettings = this.CurrentSettings.Clone();
        nextSettings.ForceHoverOpacityActive = combined;
        UiHangWatchdog.MarkUiCheckpoint("hover.apply_combined:" + reason);
        this.applyingAutomaticHoverOpacityState = true;
        try
        {
            ApplyRuntimeSettings(nextSettings);
        }
        finally
        {
            this.applyingAutomaticHoverOpacityState = false;
        }

        Program.LogInfo("Hover opacity runtime state applied. Active=" + combined.ToString() + ", Reason=" + reason);
    }

    private bool IsCombinedHoverOpacityActive()
    {
        if (this.globalLayoutEditActive)
        {
            return false;
        }

        return this.manualForceHoverOpacityActive ||
            this.autoIdleHoverOpacityActive ||
            this.autoMaximizedHoverOpacityActive;
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (IsHoverOpacityRuntimeEnabled() || NeedsClickThroughPolling()))
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
        return HoverInteractionPolicy.IsHoverOpacityTargetActive(
            this.CurrentSettings,
            this.Bounds,
            this.hiddenForFullscreen,
            this.Visible,
            ref this.reverseHoverRevealUntilUtc,
            this.hoverOpacityDelayState,
            this.autoHideKeepAliveActive);
    }

    private bool IsHoverOpacityRuntimeEnabled()
    {
        return this.CurrentSettings.HoverOpacityEnabled ||
            this.CurrentSettings.ForceHoverOpacityActive ||
            this.CurrentSettings.AutoHoverOpacityIdleEnabled ||
            this.CurrentSettings.AutoHoverOpacityMaximizedEnabled;
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
            this.CurrentSettings.ClickThroughMode,
            this.CurrentSettings.VisibilityMode);
    }

    private bool NeedsClickThroughPolling()
    {
        return WidgetSettings.ShouldEnableClickThrough(
            this.CurrentSettings.ClickThroughMode,
            this.CurrentSettings.VisibilityMode);
    }

    private void UpdateVisibilityForMode()
    {
        if (this.globalLayoutEditActive)
        {
            bool hiddenChanged = this.hiddenForFullscreen;
            this.hiddenForFullscreen = false;
            if (!this.Visible)
            {
                this.Show();
            }

            if (!this.TopMost)
            {
                this.TopMost = true;
            }

            if (hiddenChanged)
            {
                ApplyChildWindowsVisibilityMode();
                ApplyPerformanceTimerIntervals();
                UpdateHoverAnimationTimer();
            }

            RestoreApplicationTopMostPriority();
            return;
        }

        bool hideForVisibilityMode = ShouldHideFormForVisibilityMode(this);

        if (hideForVisibilityMode)
        {
            bool hiddenChanged = !this.hiddenForFullscreen;
            this.hiddenForFullscreen = true;
            if (this.Visible)
            {
                this.Hide();
            }

            if (hiddenChanged)
            {
                ApplyChildWindowsVisibilityMode();
                ApplyPerformanceTimerIntervals();
                UpdateHoverAnimationTimer();
            }
            else
            {
                ApplyChildWindowsVisibilityMode();
            }

            return;
        }

        bool shownChanged = this.hiddenForFullscreen;
        this.hiddenForFullscreen = false;
        if (!this.Visible)
        {
            this.Show();
        }

        bool shouldBeTopMost = this.CurrentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        if (shownChanged)
        {
            ApplyPerformanceTimerIntervals();
            UpdateHoverAnimationTimer();
        }

        ApplyChildWindowsVisibilityMode();
    }

    private bool ShouldHideFormForVisibilityMode(Form form)
    {
        if (this.CurrentSettings == null || form == null ||
            form.IsDisposed)
        {
            return false;
        }

        // Manual hide is an independent visibility source. Keeping it in the shared visibility
        // decision preserves fullscreen/overlap state so un-hiding restores the correct policy.
        if (this.manualAllWindowsHidden && !this.globalLayoutEditActive)
        {
            return true;
        }

        if (this.globalLayoutEditActive || this.applicationWindowStateTracker == null)
        {
            return false;
        }

        Rectangle screenBounds = form.IsHandleCreated
            ? Screen.FromHandle(form.Handle).Bounds
            : Screen.FromControl(form).Bounds;
        Rectangle formBounds = form.IsHandleCreated
            ? new Rectangle(form.Left, form.Top, form.Width, form.Height)
            : form.Bounds;
        bool ignoreOverlapForTarget =
            this.CurrentSettings.VisibilityOverlapIgnoresOperationPanelEnabled &&
            this.CurrentSettings.VisibilityMode == WidgetVisibilityMode.HideWhenOverlapped &&
            object.ReferenceEquals(form, this.operationForm);
        return this.applicationWindowStateTracker.ShouldHideForVisibilityMode(
            this.CurrentSettings.VisibilityMode,
            formBounds,
            screenBounds,
            ignoreOverlapForTarget);
    }

    private void ApplyChildWindowsVisibilityMode()
    {
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.codexRadarForm));
        }

        if (this.claudeRadarForm != null && !this.claudeRadarForm.IsDisposed)
        {
            this.claudeRadarForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.claudeRadarForm));
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.powerThermalForm));
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.networkMonitorForm));
        }

        if (this.connectionCheckForm != null && !this.connectionCheckForm.IsDisposed)
        {
            this.connectionCheckForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.connectionCheckForm));
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.operationForm));
        }
    }

    private void OpenSettings()
    {
        if (IsReusableSettingsWindow(this.settingsForm))
        {
            ShowSettingsWindow(this.settingsForm, "existing settings window");
            return;
        }

        CleanupSettingsWindowReference(this.settingsForm, "stale settings window before open");
        WidgetSettings baseline = this.savedSettings.Clone();
        baseline.Normalize();
        Form nextSettingsForm = CreateSettingsWindow(baseline);
        this.settingsForm = nextSettingsForm;
        nextSettingsForm.FormClosed += OnSettingsFormClosed;
        nextSettingsForm.Disposed += OnSettingsFormDisposed;
        try
        {
            nextSettingsForm.Show();
            ShowSettingsWindow(nextSettingsForm, "new settings window");
        }
        catch
        {
            CleanupSettingsWindowReference(nextSettingsForm, "settings open failed");
            throw;
        }
    }

    private Form CreateSettingsWindow(WidgetSettings baseline)
    {
        return new Win11SettingsForm(this, baseline);
    }

    private void ShowSettingsWindow(Form form, string reason)
    {
        if (form == null || form.IsDisposed)
        {
            return;
        }

        ClearOperationPanelTransientInteractionState();
        if (!form.Visible)
        {
            form.Show();
        }

        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.BringToFront();
        bool activated = false;
        if (form.IsHandleCreated)
        {
            activated = NativeMethods.ActivateWindow(form.Handle);
        }

        if (!activated)
        {
            form.Activate();
        }

        Program.LogInfo("Settings foreground requested. Reason=" + reason + ", Activated=" + activated.ToString() + ".");
    }

    private void OnSettingsFormClosed(object sender, FormClosedEventArgs e)
    {
        CleanupSettingsWindowReference(sender as Form, "settings form closed");
    }

    private void OnSettingsFormDisposed(object sender, EventArgs e)
    {
        CleanupSettingsWindowReference(sender as Form, "settings form disposed");
    }

    private static bool IsReusableSettingsWindow(Form form)
    {
        return form != null &&
            !form.IsDisposed &&
            form.IsHandleCreated &&
            form.Visible;
    }

    private void CleanupSettingsWindowReference(Form form, string reason)
    {
        if (form == null)
        {
            return;
        }

        form.FormClosed -= OnSettingsFormClosed;
        form.Disposed -= OnSettingsFormDisposed;

        ISettingsWindow settingsWindow = form as ISettingsWindow;
        WidgetSettings revertSettings;
        if (settingsWindow != null && settingsWindow.TryConsumeUnsavedPreview(out revertSettings))
        {
            Program.LogInfo("Unsaved settings preview reverted during " + reason + ".");
            try
            {
                RevertSettings(revertSettings);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        if (object.ReferenceEquals(this.settingsForm, form))
        {
            this.settingsForm = null;
        }

        try
        {
            RecoverOperationPanelAfterSettingsWindowClosed();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void RecoverOperationPanelAfterSettingsWindowClosed()
    {
        ClearOperationPanelTransientInteractionState();
    }

    private void ClearOperationPanelTransientInteractionState()
    {
        if (this.operationForm == null || this.operationForm.IsDisposed)
        {
            return;
        }

        this.operationForm.ClearTransientInteractionState();
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

    protected override string LayeredRenderTimingName
    {
        get { return "widget.render"; }
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawWidget(g);
    }

    protected override bool IsLayeredBurnInColorProtectionActive()
    {
        return IsBurnInColorProtectionActive();
    }

    private void ConfigureWidgetGraphics(Graphics g)
    {
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
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

    // Render-variant dispatch (mirrors CodexRadarForm). Only Classic exists today; add a case and a
    // sibling partial file (WidgetForm.<Name>.cs) to introduce an alternate main-window layout.
    private void DrawWidgetContent(Graphics g)
    {
        DrawWidgetContentClassic(g);
    }

    private void DrawWidgetContentClassic(Graphics g)
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
            Font font = GetCachedFont(13.0f * this.LayerScale, FontStyle.Bold);
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

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(
            this.CurrentSettings,
            IsHoverOpacityTargetActive());
    }

    private List<MetricPanel> BuildMetricPanels()
    {
        List<MetricPanel> panels = new List<MetricPanel>();
        string[] order = this.CurrentSettings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        for (int i = 0; i < order.Length; i++)
        {
            AddMetricPanel(panels, order[i]);
        }

        return panels;
    }

    private void AddMetricPanel(List<MetricPanel> panels, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowCpu)
        {
            MetricPanel cpuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.CpuName), string.Format("CPU {0:0}%", this.snapshot.CpuPercent), FormatCpuFrequencyPair(this.snapshot.CpuFrequencyGhz, this.snapshot.CpuBaseFrequencyGhz) },
                new Color[] { DesignTokens.Colors.Accent },
                new List<double>[] { this.cpuHistory },
                100.0,
                false);
            cpuPanel.CoreValues = this.snapshot.CpuCorePercents;
            cpuPanel.UseHardwareStackText = true;
            if (this.CurrentSettings.AlertTestEnabled)
            {
                cpuPanel.AlertPercent = 100.0;
                cpuPanel.AlertIconVisible = true;
            }

            panels.Add(cpuPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowMemory)
        {
            MetricPanel memoryPanel = new MetricPanel(
                new string[]
                {
                    FormatMemoryTitleForPanel(this.snapshot.MemoryManufacturer, this.snapshot.MemorySpeedMtps),
                    string.Format("MEM {0:0}%", this.snapshot.MemoryPercent),
                    FormatGbPair(this.snapshot.MemoryUsedGb, this.snapshot.MemoryTotalGb)
                },
                new Color[] { DesignTokens.Colors.AccentAlt, DesignTokens.Colors.Warning },
                new List<double>[] { this.memoryHistory, this.memoryHardwareReservedHistory },
                100.0,
                false);
            memoryPanel.AlertPercent = this.snapshot.MemoryPercent;
            memoryPanel.UseHardwareStackText = true;
            if (this.CurrentSettings.AlertTestEnabled)
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

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowDisk)
        {
            MetricPanel diskPanel = new MetricPanel(
                new string[]
                {
                    FormatDiskTitleForPanel(this.snapshot.DiskVolumeLabel),
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
            if (this.CurrentSettings.AlertTestEnabled)
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

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowNetwork)
        {
            MetricPanel networkPanel = new MetricPanel(
                GetNetworkPanelTextLines(),
                new Color[] { DesignTokens.Colors.Accent, DesignTokens.Colors.Danger },
                new List<double>[] { this.networkSentHistory, this.networkReceivedHistory },
                1.0,
                true);
            networkPanel.UseCompactValueFont = true;
            networkPanel.IsNetworkDisconnected = !this.snapshot.NetworkConnected;
            if (this.CurrentSettings.AlertTestEnabled)
            {
                networkPanel.AlertPercent = 100.0;
                networkPanel.AlertIconVisible = true;
            }

            panels.Add(networkPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowGpu)
        {
            MetricPanel gpuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.GpuName), string.Format("GPU {0:0}%", this.snapshot.GpuPercent), FormatGbPair(this.snapshot.GpuMemoryUsedGb, this.snapshot.GpuMemoryTotalGb) },
                new Color[] { DesignTokens.Colors.Accent, DesignTokens.Colors.AccentAlt },
                new List<double>[] { this.gpuHistory, this.gpuMemoryHistory },
                100.0,
                false);
            gpuPanel.AlertPercent = Math.Max(this.snapshot.GpuPercent, this.snapshot.GpuMemoryPercent);
            gpuPanel.UseHardwareStackText = true;
            if (this.CurrentSettings.AlertTestEnabled)
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

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowNpu)
        {
            MetricPanel npuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.NpuName), string.Format("NPU {0:0}%", this.snapshot.NpuPercent), FormatGbPair(this.snapshot.NpuMemoryUsedGb, this.snapshot.NpuMemoryTotalGb) },
                new Color[] { DesignTokens.Colors.Warning, DesignTokens.Colors.AccentAlt },
                new List<double>[] { this.npuHistory, this.npuMemoryHistory },
                100.0,
                false);
            npuPanel.AlertPercent = Math.Max(this.snapshot.NpuPercent, this.snapshot.NpuMemoryPercent);
            npuPanel.UseHardwareStackText = true;
            if (this.CurrentSettings.AlertTestEnabled)
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
        bool quotaAlertsVisible = AlertPresentationPolicy.ShouldPresent(
            this.CurrentSettings,
            AlertPresentationCategory.Quota);
        DrawGraph(
            g,
            graphRect,
            panel.Colors,
            panel.Histories,
            panel.GraphMax,
            panel.AutoScale,
            panel.IsNetworkDisconnected,
            panel.CoreValues,
            quotaAlertsVisible ? panel.AlertPercent : 0.0,
            quotaAlertsVisible && panel.AlertIconVisible);

        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(9);
        float textWidth = Math.Max(20, area.Right - textX);
        // Disk capacity and Wi-Fi RSSI opt into a fourth line; other metrics keep three-line spacing.
        int textLineCount = panel.TextLines != null && panel.TextLines.Length >= 4 ? 4 : 3;
        float lineH = Math.Max(1.0f, area.Height / textLineCount);

        Font smallFont = GetCachedFont(10.5f * this.LayerScale, FontStyle.Bold);
        Font valueFont = GetCachedFont(11.5f * this.LayerScale, FontStyle.Bold);
        Font compactFont = GetCachedFont(10.0f * this.LayerScale, FontStyle.Bold);
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
        using (Pen border = new Pen(DesignTokens.WithAlpha(borderColor, borderAlpha), Math.Max(1.0f, 1.5f * this.LayerScale)))
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

            using (Pen line = new Pen(accent, Math.Max(1.0f, 2.0f * this.LayerScale)))
            {
                line.LineJoin = LineJoin.Round;
                g.DrawLines(line, points);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        return ComputeOpacityAlpha(this.CurrentSettings.BackgroundTransparencyPercent);
    }

    private int GetContentOpacityAlpha()
    {
        return ComputeOpacityAlpha(this.CurrentSettings.ApplicationTransparencyPercent);
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.MainWidgetTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.MainWidgetScaleOverridePercent; }
    }

    protected override int ApplyHoverAlpha(int alpha)
    {
        return ApplyHoverTransparencyTarget(alpha);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!IsHoverOpacityRuntimeEnabled() || this.hoverOpacityProgress <= 0.0)
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
        size = Math.Max(14.0f * this.LayerScale, Math.Min(size, 28.0f * this.LayerScale));
        float centerX = rect.Left + rect.Width * 0.5f;
        float centerY = rect.Top + rect.Height * 0.52f;
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.58f),
            new PointF(centerX - size * 0.58f, centerY + size * 0.48f),
            new PointF(centerX + size * 0.58f, centerY + size * 0.48f)
        };

        int warningAlpha = (this.tickCount % 2 == 0) ? 77 : 179;
        using (Pen triangleBorder = new Pen(DesignTokens.Warning(warningAlpha), Math.Max(1.0f, 3.0f * this.LayerScale)))
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

        float left = rect.Left + Math.Max(2.0f, 2.0f * this.LayerScale);
        float bottom = rect.Bottom - Math.Max(2.0f, 2.0f * this.LayerScale);
        float width = Math.Max(1.0f, rect.Width - Math.Max(4.0f, 4.0f * this.LayerScale));
        float height = Math.Max(1.0f, rect.Height - Math.Max(4.0f, 4.0f * this.LayerScale));
        float slot = width / values.Length;
        float gap = slot >= 4.0f ? Math.Min(2.0f * this.LayerScale, slot * 0.28f) : 0.0f;
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
        float padding = Math.Max(3.0f, 4.0f * this.LayerScale);
        using (Pen cross = new Pen(DesignTokens.Colors.DangerGlyph, Math.Max(2.0f, 3.2f * this.LayerScale)))
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
            while (size > 7.0f * this.LayerScale && !TitleTextFits(g, lines, drawFont, rect))
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.6f * this.LayerScale;
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

            while (size > 8.0f * this.LayerScale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.7f * this.LayerScale;
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

    private static string FormatDiskTitleForPanel(string volumeLabel)
    {
        return string.IsNullOrWhiteSpace(volumeLabel) ? "DISK" : "DISK " + volumeLabel.Trim();
    }

    private string[] GetNetworkPanelTextLines()
    {
        if (!this.snapshot.NetworkConnected)
        {
            return new string[] { "Network", "网络已断开", "" };
        }

        string up = "UP " + FormatRate(this.snapshot.NetworkSentBytesPerSecond);
        string down = "DL " + FormatRate(this.snapshot.NetworkReceivedBytesPerSecond);
        if (this.snapshot.NetworkIsWifi)
        {
            return new string[]
            {
                this.snapshot.NetworkName,
                up,
                down,
                FormatWifiRssi(this.snapshot.NetworkRssiKnown, this.snapshot.NetworkRssiDbm)
            };
        }

        return new string[] { this.snapshot.NetworkName, up, down };
    }

    private static string FormatWifiRssi(bool known, int rssiDbm)
    {
        return known
            ? string.Format("RSSI {0}dBm", rssiDbm)
            : "RSSI --dBm";
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

}
