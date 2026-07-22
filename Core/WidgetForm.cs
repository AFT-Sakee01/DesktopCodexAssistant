using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    private const int ChinaEgressWarningCooldownSeconds = 60;
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
    private ChinaEgressWarningForm chinaEgressWarningForm;
    private DateTime chinaEgressWarningSuppressedUntilUtc;
    private bool chinaEgressOutsideConfirmed;
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
    private bool hiddenForFullscreen;
    private bool globalLayoutEditActive;
    private bool manualAllWindowsHidden;
    private bool childWindowLifecycleStarted;
    private CodexRadarForm codexRadarForm;
    private PowerThermalForm powerThermalForm;
    private NetworkMonitorForm networkMonitorForm;
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
        this.MinimumSize = new Size(1, 1);
        this.MaximumSize = new Size(1, 1);
        this.Size = new Size(1, 1);
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
        StartChildWindowLifecycle();
    }

    private void StartChildWindowLifecycle()
    {
        // Form.Shown can be queued behind the first message-pump turn. Keep startup idempotent so
        // bounded cold-start tests can invoke the exact production path without pumping timers.
        if (this.childWindowLifecycleStarted || this.formClosing)
        {
            return;
        }

        Program.LogInfo("Widget shown. Handle=0x" + this.Handle.ToInt64().ToString("X"));
        StartApplicationWindowStateTracking();
        ApplyRuntimeSettings(this.CurrentSettings);
        PositionWidget();

        // The form is now a hidden message-loop, sampling and child-lifecycle host. Attaching its
        // HWND to the desktop would add WS_VISIBLE/SWP_SHOWWINDOW and resurrect the retired panel.
        Program.LogInfo("Main widget host remains hidden; desktop-parent presentation is retired.");

        this.childWindowLifecycleStarted = true;
        EnsureRadarChildWindows();
        this.powerThermalForm = new PowerThermalForm(this.CurrentSettings);
        this.powerThermalForm.StartHeadlessDataOwner();
        this.networkMonitorForm = new NetworkMonitorForm(this.CurrentSettings);
        this.networkMonitorForm.SetSharedInteractionPolling(true);
        this.networkMonitorForm.StartDockedOwner(this);
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
            delegate(string propertyName, bool enabled) { return SetBooleanSettingFromOperationPanel(propertyName, enabled); },
            PersistGuardStateFromOperationPanel);
        this.operationForm.Show(this);
        // Left-dock mutual exclusion: the network panel and the two operation-owned boards live in
        // different forms, so WidgetForm (the coordination owner) ties the two directions together.
        this.operationForm.HideNetworkDockedPanelForOverlay = delegate
        {
            if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
            {
                this.networkMonitorForm.HideDockedPanelIfVisible();
            }
        };
        this.networkMonitorForm.CollapseOtherLeftDockOverlays = delegate
        {
            if (this.operationForm != null && !this.operationForm.IsDisposed)
            {
                this.operationForm.HideLeftDockBoardsForPeerOverlay();
            }
        };
        // The guard board's offline auto-sleep reads connectivity from the network window rather
        // than probing on its own, so the two never disagree about whether the link is down.
        this.operationForm.GuardNetworkOnlineProvider = delegate
        {
            if (this.networkMonitorForm == null || this.networkMonitorForm.IsDisposed)
            {
                return null;
            }

            return this.networkMonitorForm.GetGuardOnlineState();
        };
        // The IQ board consumes the existing Codex Radar cache. Keeping this provider read-only
        // prevents a fifth dock surface from starting a second refresh cadence or bypassing Radar's
        // validation/fallback chain.
        this.operationForm.CodexIqSnapshotProvider = delegate
        {
            if (this.codexRadarForm == null || this.codexRadarForm.IsDisposed)
            {
                return CodexIqBoardSnapshot.CreateEmpty();
            }

            return this.codexRadarForm.BuildCodexIqBoardSnapshot();
        };
        // ApplyRuntimeSettings runs before childWindowLifecycleStarted so the hidden host can
        // establish its own HWND safely. Build the canonical tile set only after every data owner
        // and board provider is connected; otherwise a cold start has no later creation path.
        ApplyMetricTilePresentation();
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
    }

    private void EnsureCodexRadarWindow()
    {
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            return;
        }

        this.codexRadarForm = new CodexRadarForm(this.CurrentSettings, ShowWindowsNotification);
        this.codexRadarForm.StartHeadlessDataOwner();

        Program.LogInfo("Codex/Claude Radar headless data owner started.");
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
            form.StopHeadlessDataOwner();
            form.Dispose();
        }

        Program.LogInfo("Codex/Claude Radar headless data owner stopped.");
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

        CloseChinaEgressWarningForm();

        if (this.codexRadarForm != null)
        {
            this.codexRadarForm.StopHeadlessDataOwner();
            this.codexRadarForm.Dispose();
            this.codexRadarForm = null;
        }

        if (this.powerThermalForm != null)
        {
            this.powerThermalForm.StopHeadlessDataOwner();
            this.powerThermalForm.Dispose();
            this.powerThermalForm = null;
        }

        if (this.networkMonitorForm != null)
        {
            this.networkMonitorForm.Close();
            this.networkMonitorForm = null;
        }

        if (this.operationForm != null)
        {
            this.operationForm.Close();
            this.operationForm = null;
        }

        CloseMetricTileWindows();

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
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // The root form is a hidden lifetime/data host. Retained surfaces own their own geometry
        // and rendering, so a host resize must not allocate a layered buffer or window region.
        DisposeRenderBuffer();
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
            if (this.operationForm != null && !this.operationForm.IsDisposed)
            {
                this.operationForm.NotifyGuardBoardSystemResume();
            }

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

        UpdateVisibilityForMode();
        ApplyClickThroughStyle();
        ApplyDisplayLayoutForCurrentWorkArea();
        PositionWidget();
        ResetDisplayRenderResources();

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

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.RecoverAfterDisplayResume();
        }

        RecoverMetricTilesAfterDisplayResume();

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

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.PrepareForDisplaySuspend();
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.PrepareForDisplaySuspend();
        }

        SetMetricTileDisplaySuspended(true);

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
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:china_egress_guard");
            UpdateChinaEgressProtection();
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:refresh_window_state");
            RefreshApplicationWindowState();
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:update_visibility");
            UpdateVisibilityForMode();
            if (!this.hiddenForFullscreen &&
                ShouldRefreshBurnInPosition())
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:position_burn_in_shift");
                PositionWidget();
                RefreshMetricTileBurnInPosition();
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
            UiHangWatchdog.MarkUiCheckpoint("widget.main_tick:metric_tiles");
            PushMetricTileFeed();

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

    private void PositionWidget()
    {
        // The root form is a one-pixel hidden HWND used only for message-loop coordination.
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

    private void PersistGuardStateFromOperationPanel(WidgetSettings guardState)
    {
        WidgetSettings baseline = this.savedSettings == null
            ? (this.CurrentSettings == null ? WidgetSettings.CreateDefaults() : this.CurrentSettings.Clone())
            : this.savedSettings.Clone();
        SaveSettings(MergeGuardRuntimeFields(baseline, guardState));
    }

    internal static WidgetSettings MergeGuardRuntimeFields(WidgetSettings committed, WidgetSettings guardState)
    {
        WidgetSettings merged = committed == null ? WidgetSettings.CreateDefaults() : committed.Clone();
        if (guardState == null)
        {
            return merged;
        }

        merged.GuardSleepEnabled = guardState.GuardSleepEnabled;
        merged.GuardSleepSinceUtcTicks = guardState.GuardSleepSinceUtcTicks;
        merged.GuardDisplayMinutes = guardState.GuardDisplayMinutes;
        merged.GuardOfflineThresholdMinutes = guardState.GuardOfflineThresholdMinutes;
        merged.GuardDisplayUntilUtcTicks = guardState.GuardDisplayUntilUtcTicks;
        merged.GuardBatteryCarePauseUntilUtcTicks = guardState.GuardBatteryCarePauseUntilUtcTicks;
        merged.Normalize();
        return merged;
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

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.ForceRefresh();
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.ForceRefresh();
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
        bool chinaGuardWasEnabled = this.CurrentSettings != null &&
            this.CurrentSettings.AiChinaEgressGuardEnabled;
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
        if (chinaGuardWasEnabled && !this.CurrentSettings.AiChinaEgressGuardEnabled)
        {
            this.chinaEgressOutsideConfirmed = false;
            RequestSensitiveAiRefreshAfterEgressAuthorization("大陆出口保护已关闭");
        }
        ApplyGlobalHotkeyConfiguration();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.MinimumSize = new Size(1, 1);
        this.MaximumSize = new Size(1, 1);
        Program.ApplyPerformanceMode(this.CurrentSettings.PerformanceMode);
        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:timers");
        ApplyPerformanceTimerIntervals();
        UpdateSeelenDockPulseTimer();
        UpdateWinDRecoveryWatcher();

        Size desiredSize = new Size(1, 1);
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

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:child_operation");
            this.operationForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        UiHangWatchdog.MarkUiCheckpoint("apply_runtime_settings:metric_tiles");
        ApplyMetricTilePresentation();
        if (this.globalLayoutEditActive)
        {
            // Child ApplyRuntimeSettings calls occur after the first visibility pass and some child
            // forms intentionally know nothing about their replacement presentation. Reapply the
            // structural plan here so a just-switched classic Radar/network board cannot flash back
            // above the editor mask.
            ApplyGlobalLayoutEditStructuralVisibility();
        }
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

    private void UpdateChinaEgressProtection()
    {
        if (this.CurrentSettings == null || !this.CurrentSettings.AiChinaEgressGuardEnabled)
        {
            this.chinaEgressOutsideConfirmed = false;
            ClearChinaEgressWarning("guard disabled");
            return;
        }

        CleanIpConnectionSnapshot snapshot = CleanIpConnectionReader.Shared.GetSnapshot(this.CurrentSettings);
        string country = snapshot == null ? string.Empty : (snapshot.CountryRaw ?? string.Empty).Trim();
        bool egressKnown = snapshot != null &&
            snapshot.CheckedAtKnown &&
            snapshot.Success &&
            snapshot.EgressIdentityCurrent &&
            !snapshot.TestMode &&
            country.Length != 0;
        bool mainlandChina = egressKnown && AiRequestProtection.IsMainlandChinaEgress(country);
        DateTime observedUtc = snapshot == null || !snapshot.CheckedAtKnown
            ? DateTime.MinValue
            : snapshot.CheckedAtLocal;
        AiRequestProtection.UpdateEgressSignal(
            egressKnown,
            mainlandChina,
            country,
            observedUtc);

        bool outsideConfirmed = AiRequestProtection.HasConfirmedOutsideChinaEgress();
        if (outsideConfirmed && !this.chinaEgressOutsideConfirmed)
        {
            RequestSensitiveAiRefreshAfterEgressAuthorization("出口确认境外");
        }

        this.chinaEgressOutsideConfirmed = outsideConfirmed;

        string reason;
        if (!AiRequestProtection.ShouldWarnChinaEgress(this.CurrentSettings, out reason))
        {
            ClearChinaEgressWarning("mainland signal cleared");
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc < this.chinaEgressWarningSuppressedUntilUtc)
        {
            if (this.chinaEgressWarningForm != null &&
                !this.chinaEgressWarningForm.IsDisposed &&
                this.chinaEgressWarningForm.Visible)
            {
                this.chinaEgressWarningForm.Hide();
            }

            return;
        }

        EnsureChinaEgressWarningForm();
        bool wasVisible = this.chinaEgressWarningForm.Visible;
        this.chinaEgressWarningForm.ShowReason(reason);
        if (!wasVisible)
        {
            Program.LogInfo("China egress warning shown. Reason=" + reason);
        }
    }

    private void EnsureChinaEgressWarningForm()
    {
        if (this.chinaEgressWarningForm != null && !this.chinaEgressWarningForm.IsDisposed)
        {
            return;
        }

        ChinaEgressWarningForm warning = new ChinaEgressWarningForm();
        warning.HideForCooldownRequested += OnChinaEgressWarningHideRequested;
        warning.FormClosed += OnChinaEgressWarningFormClosed;
        this.chinaEgressWarningForm = warning;
    }

    private void RequestSensitiveAiRefreshAfterEgressAuthorization(string trigger)
    {
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.RequestSensitiveAiRefreshAfterEgressAuthorization(trigger);
        }
    }

    private void OnChinaEgressWarningHideRequested(object sender, EventArgs e)
    {
        this.chinaEgressWarningSuppressedUntilUtc =
            DateTime.UtcNow.AddSeconds(ChinaEgressWarningCooldownSeconds);
        if (this.chinaEgressWarningForm != null &&
            !this.chinaEgressWarningForm.IsDisposed)
        {
            this.chinaEgressWarningForm.Hide();
        }

        Program.LogInfo("China egress warning temporarily hidden for 60 seconds.");
    }

    private void OnChinaEgressWarningFormClosed(object sender, FormClosedEventArgs e)
    {
        ChinaEgressWarningForm warning = sender as ChinaEgressWarningForm;
        if (warning != null)
        {
            warning.HideForCooldownRequested -= OnChinaEgressWarningHideRequested;
            warning.FormClosed -= OnChinaEgressWarningFormClosed;
        }

        if (object.ReferenceEquals(this.chinaEgressWarningForm, warning))
        {
            this.chinaEgressWarningForm = null;
        }
    }

    private void ClearChinaEgressWarning(string reason)
    {
        this.chinaEgressWarningSuppressedUntilUtc = DateTime.MinValue;
        if (this.chinaEgressWarningForm == null ||
            this.chinaEgressWarningForm.IsDisposed ||
            !this.chinaEgressWarningForm.Visible)
        {
            return;
        }

        this.chinaEgressWarningForm.Hide();
        Program.LogInfo("China egress warning hidden. Reason=" + reason);
    }

    private void CloseChinaEgressWarningForm()
    {
        ChinaEgressWarningForm warning = this.chinaEgressWarningForm;
        this.chinaEgressWarningForm = null;
        this.chinaEgressWarningSuppressedUntilUtc = DateTime.MinValue;
        if (warning == null)
        {
            return;
        }

        warning.HideForCooldownRequested -= OnChinaEgressWarningHideRequested;
        warning.FormClosed -= OnChinaEgressWarningFormClosed;
        if (!warning.IsDisposed)
        {
            warning.CloseFromOwner();
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
            UpdateHoverOpacityAnimation();
            bool hoverTarget = IsHoverOpacityTargetActive();
            bool animationActive =
                automaticStateChanged ||
                Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
            // Visible passive panels share this UI-thread timer. Headless Radar/Power owners are
            // deliberately excluded from interaction polling.
            if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
            {
                UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:network_shared_tick");
                animationActive |= this.networkMonitorForm.ProcessSharedInteractionTick();
            }

            UiHangWatchdog.MarkUiCheckpoint("widget.hover_tick:metric_tile_shared_tick");
            animationActive |= ProcessMetricTileInteractionTick();

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

        SetMetricTileAutoHideKeepAlive(active);

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.SetAutoHideKeepAliveActive(active);
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
        return IsPointInFormActivationRange(this.CurrentSettings, this.networkMonitorForm, cursor) ||
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
            ApplyGlobalLayoutEditStructuralVisibility();

            if (!this.TopMost)
            {
                this.TopMost = true;
            }

            if (hiddenChanged)
            {
                ApplyPerformanceTimerIntervals();
                UpdateHoverAnimationTimer();
            }

            RestoreApplicationTopMostPriority();
            return;
        }

        // hiddenForFullscreen means exactly one thing: the visibility policy says the retained tile
        // surfaces are not allowed on screen. It still gates the control-tick rate, PDH sampling and
        // shared interaction timer; the permanently hidden host itself must never set it merely to
        // remain off screen.
        // The retired host rectangle must not decide whether ten independently positioned tiles
        // may sample. Only the explicit global hide pauses the shared runtime; fullscreen/overlap
        // policy is evaluated per retained tile below.
        bool hideForVisibilityMode = this.manualAllWindowsHidden && !this.globalLayoutEditActive;

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
        if (this.Visible)
        {
            this.Hide();
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

    private void ApplyGlobalLayoutEditStructuralVisibility()
    {
        // Layout editing may reveal retained visible surfaces, never the retired host panel or the
        // two headless data owners. Their headless lifecycle keeps sampling independent of this
        // visibility flag, while dock tabs remain live with their board bodies collapsed.
        if (this.Visible)
        {
            this.Hide();
        }

        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.SetHiddenForFullscreen(true);
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.SetHiddenForFullscreen(true);
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.SetHiddenForFullscreen(false);
            this.networkMonitorForm.HideDockedPanelIfVisible();
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.SetHiddenForFullscreen(false);
            this.operationForm.HideLeftDockBoardsForPeerOverlay();
        }

        ApplyMetricTilesVisibilityMode();
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
        // The retained Radar data owner remains alive and hidden; closing it would starve the tiles.
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            this.codexRadarForm.SetHiddenForFullscreen(true);
        }

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            this.powerThermalForm.SetHiddenForFullscreen(true);
        }

        if (this.networkMonitorForm != null && !this.networkMonitorForm.IsDisposed)
        {
            this.networkMonitorForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.networkMonitorForm));
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.SetHiddenForFullscreen(ShouldHideFormForVisibilityMode(this.operationForm));
        }

        ApplyMetricTilesVisibilityMode();
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

    protected override string LayeredRenderTimingName
    {
        get { return "widget.render"; }
    }

    protected override void DrawWindowContent(Graphics g)
    {
        // Intentionally empty: the root form owns runtime orchestration only. The visible main
        // presentation is the fixed MetricTileForm column.
    }

    protected override bool CanRenderLayeredWindow()
    {
        // Runtime presentation belongs exclusively to MetricTileForm.
        return false;
    }

    protected override bool IsLayeredBurnInColorProtectionActive()
    {
        return false;
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

}
