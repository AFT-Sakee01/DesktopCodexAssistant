using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Owns the layered Codex monitor and schedules local quota reads plus Reset and
/// current.json snapshots without performing blocking work in paint code.
/// </summary>
internal sealed class CodexRadarForm : Form
{
    private const int CodexRadarSecondBoundaryOffsetMs = 30;
    private const int QuotaTailChunkBytes = 1024 * 1024;
    private const int MaxQuotaRolloutFilesToScan = 80;
    private const int CodexResetInferredAfterResetSeconds = 300;
    private const string CodexResetStatusUrl = "https://hascodexratelimitreset.today/api/status";
    private const int CodexResetStatusTimeoutMs = 10000;
    private const string CodexRadarStatusUrl = "https://codexradar.com/current.json";
    private const int CodexModelIqNominalTasks = WidgetSettings.MaxCodexModelIqPassed;
    private const int CodexRadarStatusTimeoutMs = 10000;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly Action<string, string, ToolTipIcon> notificationAction;
    // Cache the newest rollout result while its identity and append-sensitive metadata stay unchanged.
    private static readonly object codexQuotaSnapshotCacheLock = new object();
    private static string codexQuotaSnapshotCachePath = string.Empty;
    private static DateTime codexQuotaSnapshotCacheWriteUtc;
    private static long codexQuotaSnapshotCacheLength = -1;
    private static CodexQuotaSnapshot codexQuotaSnapshotCache;
    private readonly object codexResetStatusLock = new object();
    private readonly object codexRadarStatusLock = new object();
    private readonly object quotaResetStateLock = new object();
    private readonly object serviceHealthLock = new object();
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private int renderTickCount;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private bool sharedInteractionPolling;
    private DateTime lastQuotaRefreshUtc;
    private DateTime lastQuotaProcessCheckUtc;
    private CodexQuotaSnapshot quotaSnapshot;
    private bool quotaSourceKnown;
    private bool quotaCodexProcessRunning;
    private bool radarResetBaselineInitialized;
    private string lastRadarResetEventId = string.Empty;
    private DateTime lastRadarResetEventClosedUtc;
    private string lastRadarOpenEventId = string.Empty;
    private DateTime lastRadarOpenEventUtc;
    private DateTime fiveHourQuotaProtectionUtc;
    private DateTime weeklyQuotaProtectionUtc;
    private bool fiveHourQuotaProtectionGold;
    private bool weeklyQuotaProtectionGold;
    private DateTime extraResetGoldTestUntilUtc;
    private DateTime nextQuotaInactiveRefreshUtc;
    private DateTime nextCodexResetStatusRefreshUtc;
    private DateTime codexResetStatusInferredUntilUtc;
    private bool codexResetStatusRequestRunning;
    private bool codexResetStatusKnown;
    private bool codexResetStatusYes;
    private DateTime nextCodexRadarStatusRefreshUtc;
    private DateTime pendingCodexRadarOpenedRefreshLocal;
    private DateTime pendingCodexRadarOpenedEventLocal;
    private DateTime pendingCodexRadarClosedRefreshLocal;
    private DateTime pendingCodexRadarClosedEventLocal;
    private bool codexRadarStatusRequestRunning;
    private CodexRadarSnapshot codexRadarSnapshot;
    private IntPtr displayPowerNotificationHandle;
    private bool codexDisplayActive = true;
    private bool codexSessionActive = true;
    private bool codexPowerSuspended;
    private bool serviceNetworkAvailable = true;
    private ServiceHealthState radarServiceHealth = ServiceHealthState.Unknown;
    private ServiceHealthState codexServiceHealth = ServiceHealthState.Unknown;
    private ServiceHealthState resetServiceHealth = ServiceHealthState.Unknown;
    private bool serviceNetworkRefreshRequested = true;
    private FileSystemWatcher quotaSessionWatcher;
    private string quotaSessionsPath = string.Empty;
    private int quotaSessionFilesChanged = 1;
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private bool renderBufferValid;
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private readonly UiFontCache fontCache = new UiFontCache();
    private DateTime lastRenderedClockSecondLocal;

    private enum CodexRadarState
    {
        None,
        Open,
        Closed
    }

    private enum ServiceHealthState
    {
        Unknown,
        Normal,
        Offline,
        Unavailable,
        Unreachable
    }

    private sealed class CodexQuotaSnapshot
    {
        public int FiveHourPercent { get; set; }
        public int WeeklyPercent { get; set; }
        public DateTime FiveHourResetLocal { get; set; }
        public DateTime WeeklyResetLocal { get; set; }
        public bool FiveHourResetKnown { get; set; }
        public bool WeeklyResetKnown { get; set; }
        public DateTime SourceUpdatedUtc { get; set; }
        public bool SourceUpdatedKnown { get; set; }

        public static CodexQuotaSnapshot CreateDefault()
        {
            return new CodexQuotaSnapshot
            {
                FiveHourPercent = 100,
                WeeklyPercent = 100,
                FiveHourResetLocal = DateTime.MinValue,
                WeeklyResetLocal = DateTime.MinValue,
                FiveHourResetKnown = false,
                WeeklyResetKnown = false,
                SourceUpdatedUtc = DateTime.MinValue,
                SourceUpdatedKnown = false
            };
        }

        public CodexQuotaSnapshot Clone()
        {
            return new CodexQuotaSnapshot
            {
                FiveHourPercent = this.FiveHourPercent,
                WeeklyPercent = this.WeeklyPercent,
                FiveHourResetLocal = this.FiveHourResetLocal,
                WeeklyResetLocal = this.WeeklyResetLocal,
                FiveHourResetKnown = this.FiveHourResetKnown,
                WeeklyResetKnown = this.WeeklyResetKnown,
                SourceUpdatedUtc = this.SourceUpdatedUtc,
                SourceUpdatedKnown = this.SourceUpdatedKnown
            };
        }
    }

    private sealed class CodexQuotaEvent
    {
        public CodexQuotaSnapshot Snapshot { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class CodexRadarSnapshot
    {
        public CodexRadarState State { get; set; }
        public DateTime CheckedAtLocal { get; set; }
        public DateTime OpenedAtLocal { get; set; }
        public DateTime ClosedAtLocal { get; set; }
        public bool CheckedAtKnown { get; set; }
        public bool OpenedAtKnown { get; set; }
        public bool ClosedAtKnown { get; set; }
        public string CurrentWindowId { get; set; }
        public string LastWindowId { get; set; }
        public DateTime LastWindowClosedAtLocal { get; set; }
        public DateTime ModelIqRefreshedAtLocal { get; set; }
        public DateTime ModelIqDataDateLocal { get; set; }
        public bool LastWindowClosedAtKnown { get; set; }
        public bool ModelIqRefreshedAtKnown { get; set; }
        public bool ModelIqDataDateKnown { get; set; }
        public string PredictionLevel { get; set; }
        public int Probability24Percent { get; set; }
        public int Probability48Percent { get; set; }
        public bool PredictionKnown { get; set; }
        public string ModelIqStatus { get; set; }
        public int ModelIqPassRatePercent { get; set; }
        public int ModelIqPassed { get; set; }
        public int ModelIqValidTasks { get; set; }
        public int ModelIqTokenEfficiencyPercent { get; set; }
        public int ModelIqTimeEfficiencyPercent { get; set; }
        public double ModelIqEfficiencyPassed { get; set; }
        public double ModelIqEfficiencyTotalTokens { get; set; }
        public double ModelIqEfficiencySerialSeconds { get; set; }
        public bool ModelIqPassedKnown { get; set; }
        public bool ModelIqEfficiencyInputKnown { get; set; }
        public bool ModelIqEfficiencyKnown { get; set; }
        public bool ModelIqKnown { get; set; }

        public static CodexRadarSnapshot CreateDefault()
        {
            return new CodexRadarSnapshot
            {
                State = CodexRadarState.None,
                CheckedAtLocal = DateTime.MinValue,
                OpenedAtLocal = DateTime.MinValue,
                ClosedAtLocal = DateTime.MinValue,
                CheckedAtKnown = false,
                OpenedAtKnown = false,
                ClosedAtKnown = false,
                CurrentWindowId = string.Empty,
                LastWindowId = string.Empty,
                LastWindowClosedAtLocal = DateTime.MinValue,
                ModelIqRefreshedAtLocal = DateTime.MinValue,
                ModelIqDataDateLocal = DateTime.MinValue,
                LastWindowClosedAtKnown = false,
                ModelIqRefreshedAtKnown = false,
                ModelIqDataDateKnown = false,
                PredictionLevel = "low",
                Probability24Percent = 0,
                Probability48Percent = 0,
                PredictionKnown = false,
                ModelIqStatus = "invalid",
                ModelIqPassRatePercent = 0,
                ModelIqPassed = 0,
                ModelIqValidTasks = CodexModelIqNominalTasks,
                ModelIqTokenEfficiencyPercent = 100,
                ModelIqTimeEfficiencyPercent = 100,
                ModelIqEfficiencyPassed = 0.0,
                ModelIqEfficiencyTotalTokens = 0.0,
                ModelIqEfficiencySerialSeconds = 0.0,
                ModelIqPassedKnown = false,
                ModelIqEfficiencyInputKnown = false,
                ModelIqEfficiencyKnown = false,
                ModelIqKnown = false
            };
        }

        public CodexRadarSnapshot Clone()
        {
            return new CodexRadarSnapshot
            {
                State = this.State,
                CheckedAtLocal = this.CheckedAtLocal,
                OpenedAtLocal = this.OpenedAtLocal,
                ClosedAtLocal = this.ClosedAtLocal,
                CheckedAtKnown = this.CheckedAtKnown,
                OpenedAtKnown = this.OpenedAtKnown,
                ClosedAtKnown = this.ClosedAtKnown,
                CurrentWindowId = this.CurrentWindowId,
                LastWindowId = this.LastWindowId,
                LastWindowClosedAtLocal = this.LastWindowClosedAtLocal,
                ModelIqRefreshedAtLocal = this.ModelIqRefreshedAtLocal,
                ModelIqDataDateLocal = this.ModelIqDataDateLocal,
                LastWindowClosedAtKnown = this.LastWindowClosedAtKnown,
                ModelIqRefreshedAtKnown = this.ModelIqRefreshedAtKnown,
                ModelIqDataDateKnown = this.ModelIqDataDateKnown,
                PredictionLevel = this.PredictionLevel,
                Probability24Percent = this.Probability24Percent,
                Probability48Percent = this.Probability48Percent,
                PredictionKnown = this.PredictionKnown,
                ModelIqStatus = this.ModelIqStatus,
                ModelIqPassRatePercent = this.ModelIqPassRatePercent,
                ModelIqPassed = this.ModelIqPassed,
                ModelIqValidTasks = this.ModelIqValidTasks,
                ModelIqTokenEfficiencyPercent = this.ModelIqTokenEfficiencyPercent,
                ModelIqTimeEfficiencyPercent = this.ModelIqTimeEfficiencyPercent,
                ModelIqEfficiencyPassed = this.ModelIqEfficiencyPassed,
                ModelIqEfficiencyTotalTokens = this.ModelIqEfficiencyTotalTokens,
                ModelIqEfficiencySerialSeconds = this.ModelIqEfficiencySerialSeconds,
                ModelIqPassedKnown = this.ModelIqPassedKnown,
                ModelIqEfficiencyInputKnown = this.ModelIqEfficiencyInputKnown,
                ModelIqEfficiencyKnown = this.ModelIqEfficiencyKnown,
                ModelIqKnown = this.ModelIqKnown
            };
        }
    }

    public CodexRadarForm(WidgetSettings settings, Action<string, string, ToolTipIcon> notificationAction)
    {
        this.notificationAction = notificationAction;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.quotaSnapshot = CodexQuotaSnapshot.CreateDefault();
        this.codexRadarSnapshot = CodexRadarSnapshot.CreateDefault();
        LoadQuotaResetState();
        InitializeQuotaSessionWatcher();

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
        this.MinimumSize = new Size(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MinCodexRadarHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxCodexRadarWidth, WidgetSettings.MaxCodexRadarHeight + S(32));
        this.Size = new Size(this.currentSettings.CodexRadarWidth, this.currentSettings.CodexRadarHeight);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextCodexRadarTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
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
        ScheduleNextCodexRadarTick();
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
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        DisposeQuotaSessionWatcher();
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        this.layeredSurface.Dispose();
        base.OnFormClosed(e);
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        try
        {
            this.renderTickCount++;
            if (!IsCodexPollingAllowed())
            {
                return;
            }

            // This timer is only a lightweight scheduler. Each data source owns its business
            // interval and single-flight guard, so a faster UI mode does not multiply web traffic.
            UpdateServiceConnectivityHealth();
            RefreshQuotaInfoIfNeeded();
            RefreshCodexResetStatusIfNeeded();
            RefreshCodexRadarStatusIfNeeded();
            Size desiredSize = GetDesiredCodexRadarSize();
            bool sizeChanged = false;
            if (this.Size != desiredSize)
            {
                this.Size = desiredSize;
                PositionCodexRadar();
                sizeChanged = true;
            }

            DateTime renderSecond = TruncateToSecond(DateTime.Now);
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                (sizeChanged || this.lastRenderedClockSecondLocal != renderSecond))
            {
                RenderLayeredWindow();
            }
        }
        finally
        {
            ScheduleNextCodexRadarTick();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(12)))
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
            PositionCodexRadar();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        CodexRadarTestMode oldCodexRadarTestMode = this.currentSettings.CodexRadarTestMode;
        ServiceHealthTestMode oldServiceHealthTestMode = this.currentSettings.ServiceHealthTestMode;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        ApplyPerformanceTimerIntervals();

        if (oldCodexRadarTestMode != this.currentSettings.CodexRadarTestMode)
        {
            if (this.currentSettings.CodexRadarTestMode == CodexRadarTestMode.Off)
            {
                PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            }

            RenderLayeredWindow();
        }

        if (oldServiceHealthTestMode != this.currentSettings.ServiceHealthTestMode)
        {
            if (this.currentSettings.ServiceHealthTestMode == ServiceHealthTestMode.Off)
            {
                ResetServiceHealthAfterTestMode();
            }
            else
            {
                ApplyServiceHealthTestMode();
            }

            RenderLayeredWindow();
        }
        else if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
        }

        Size desiredSize = GetDesiredCodexRadarSize();
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

        PositionCodexRadar();
        RenderLayeredWindow();
    }

    public void ForceRefresh()
    {
        this.lastQuotaRefreshUtc = DateTime.MinValue;
        this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;

        lock (this.codexResetStatusLock)
        {
            this.nextCodexResetStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
            this.codexResetStatusInferredUntilUtc = DateTime.MinValue;
        }

        lock (this.codexRadarStatusLock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            this.nextCodexRadarStatusRefreshUtc = nowUtc.AddSeconds(4.0);
        }

        OnTimerTick(this, EventArgs.Empty);
    }

    public void RecoverAfterDisplayResume()
    {
        this.codexPowerSuspended = false;
        this.codexDisplayActive = true;
        this.codexSessionActive = true;
        ResetDisplayRenderResources();
        PositionCodexRadar();
        ResumeCodexPollingSoon();
        ScheduleNextCodexRadarTick();
    }

    public void PrepareForDisplaySuspend()
    {
        ResetDisplayRenderResources();
    }

    private void PrimeCodexWebRefreshSchedule(DateTime nowUtc)
    {
        lock (this.codexResetStatusLock)
        {
            this.nextCodexResetStatusRefreshUtc = nowUtc.AddSeconds(1.0);
        }

        lock (this.codexRadarStatusLock)
        {
            this.nextCodexRadarStatusRefreshUtc = nowUtc.AddSeconds(4.0);
        }
    }

    private bool IsCodexPollingAllowed()
    {
        return this.codexDisplayActive && this.codexSessionActive && !this.codexPowerSuspended;
    }

    private void ResumeCodexPollingSoon()
    {
        this.lastQuotaRefreshUtc = DateTime.MinValue;
        this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        RenderLayeredWindow();
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    OnSystemSessionSwitch(sender, e);
                });
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            this.codexSessionActive = false;
            return;
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            this.codexSessionActive = true;
            ResumeCodexPollingSoon();
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.codexPowerSuspended = true;
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            this.codexPowerSuspended = false;
            this.codexDisplayActive = true;
            ResumeCodexPollingSoon();
            return;
        }

        if (eventType == NativeMethods.PBT_POWERSETTINGCHANGE && dataPtr != IntPtr.Zero)
        {
            NativeMethods.POWERBROADCAST_SETTING setting =
                (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                    dataPtr,
                    typeof(NativeMethods.POWERBROADCAST_SETTING));
            if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
            {
                bool active = setting.Data != 0;
                if (this.codexDisplayActive != active)
                {
                    this.codexDisplayActive = active;
                    if (active)
                    {
                        ResumeCodexPollingSoon();
                    }
                }
            }
        }
    }

    public void TestExtraResetNotification()
    {
        this.extraResetGoldTestUntilUtc = DateTime.UtcNow.AddSeconds(15.0);
        ShowCodexNotification(
            "Codex 额外重置",
            "测试：检测到新的重置记录，余额已恢复至 100。",
            ToolTipIcon.Warning);
        RenderLayeredWindow();
    }

    public void TestRadarOpenNotification()
    {
        ShowCodexNotification(
            "Codex 速蹬窗口开启",
            "测试：检测到速蹬窗口已开启。",
            ToolTipIcon.Info);
    }

    private void ApplyPerformanceTimerIntervals()
    {
        ScheduleNextCodexRadarTick();

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
    }

    private void ScheduleNextCodexRadarTick()
    {
        int interval = GetNextCodexRadarTickIntervalMs();
        if (this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private int GetNextCodexRadarTickIntervalMs()
    {
        // Boundary alignment keeps the clock stable and groups wakeups with the other panels.
        DateTime now = DateTime.Now;
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.currentSettings.PerformanceMode);
        int elapsedInInterval = (int)(now.TimeOfDay.TotalMilliseconds % targetInterval);
        int interval = targetInterval - elapsedInInterval + CodexRadarSecondBoundaryOffsetMs;
        if (interval <= CodexRadarSecondBoundaryOffsetMs)
        {
            interval += targetInterval;
        }

        return Math.Max(50, Math.Min(targetInterval + 100, interval));
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        bool animationActive = ProcessInteractionTick();
        int desiredInterval = animationActive
            ? WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode)
            : WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != desiredInterval)
        {
            this.hoverTimer.Interval = desiredInterval;
        }
    }

    private bool ProcessInteractionTick()
    {
        ApplyClickThroughStyle();
        bool opacityChanged = UpdateHoverOpacityAnimation();
        bool hoverTarget = IsHoverOpacityTargetActive();
        bool animationActive = Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
        if (opacityChanged && !this.hiddenForFullscreen && this.Visible)
        {
            RenderLayeredWindow(false);
        }

        return animationActive;
    }

    public void SetSharedInteractionPolling(bool shared)
    {
        this.sharedInteractionPolling = shared;
        this.hoverOpacityLastUtc = DateTime.UtcNow;
        UpdateHoverAnimationTimer();
    }

    public bool ProcessSharedInteractionTick()
    {
        if (!this.sharedInteractionPolling ||
            this.hiddenForFullscreen ||
            (!this.currentSettings.HoverOpacityEnabled && !NeedsClickThroughPolling()))
        {
            return false;
        }

        return ProcessInteractionTick();
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (this.currentSettings.HoverOpacityEnabled || NeedsClickThroughPolling()))
        {
            if (this.sharedInteractionPolling)
            {
                this.hoverTimer.Stop();
                return;
            }

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
            if (this.Visible)
            {
                this.Hide();
            }

            UpdateHoverAnimationTimer();
            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionCodexRadar();
        RenderLayeredWindow();
        UpdateHoverAnimationTimer();
    }

    private void PositionCodexRadar()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredCodexRadarSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.CodexRadarLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinCodexRadarHeight, this.currentSettings.CodexRadarHeight);
        int top = this.currentSettings.CodexRadarBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - baseHeight));
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

    private Size GetDesiredCodexRadarSize()
    {
        return new Size(this.currentSettings.CodexRadarWidth, this.currentSettings.CodexRadarHeight);
    }

    private int GetThermalAlertExtraHeight()
    {
        return Math.Max(S(24), Math.Min(S(32), (int)Math.Round(this.currentSettings.CodexRadarHeight * 0.42f)));
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawCodexRadar(e.Graphics);
    }

    private void DrawCodexRadar(Graphics g)
    {
        DrawCodexRadarBackground(g);
        DrawCodexRadarContentLayer(g);
    }

    private void ConfigureCodexRadarGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawCodexRadarBackground(Graphics g)
    {
        ConfigureCodexRadarGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, alpha)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawCodexRadarContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawCodexRadarContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawCodexRadarContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawCodexRadarContent(Graphics g)
    {
        ConfigureCodexRadarGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        RectangleF textRect = new RectangleF(
            S(8),
            S(3),
            Math.Max(10, this.Width - S(16)),
            Math.Max(10, this.Height - S(6)));

        DrawCodexRadarModules(g, textRect);
    }

    private void DrawCodexRadarModules(Graphics g, RectangleF bounds)
    {
        float gap = S(4);
        float quotaWidth = Math.Max(S(230), Math.Min(S(390), bounds.Width * 0.64f));
        float minRadarWidth = Math.Max(S(116), Math.Min(S(176), bounds.Width * 0.30f));
        if (quotaWidth > bounds.Width - minRadarWidth - gap)
        {
            quotaWidth = Math.Max(S(150), bounds.Width - minRadarWidth - gap);
        }

        float radarWidth = Math.Max(0.0f, bounds.Width - quotaWidth - gap);
        if (radarWidth < S(96) && bounds.Width > S(180))
        {
            radarWidth = S(96);
            quotaWidth = Math.Max(S(44), bounds.Width - radarWidth - gap);
        }

        RectangleF radarRect = new RectangleF(bounds.Left, bounds.Top, Math.Max(0.0f, radarWidth), bounds.Height);
        RectangleF quotaRect = new RectangleF(radarRect.Right + gap, bounds.Top, Math.Max(0.0f, bounds.Right - radarRect.Right - gap), bounds.Height);
        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();

        DrawCodexRadarWidget(g, radarRect, radarSnapshot);
        DrawQuotaWidget(g, quotaRect, radarSnapshot);
    }

    private void DrawCodexRadarWidget(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= S(58) || rect.Height <= 0)
        {
            return;
        }

        float ringColumnWidth = Math.Max(S(36), Math.Min(S(50), rect.Width * 0.30f));
        float ringShiftLeft = S(5);
        float iqTextWidth = Math.Max(S(26), Math.Min(S(32), rect.Width * 0.18f));
        float iqTextShiftLeft = S(6);
        float statusGap = 0.0f;
        RectangleF ringsRect = new RectangleF(rect.Left - ringShiftLeft, rect.Top, ringColumnWidth, rect.Height);
        RectangleF iqTextRect = new RectangleF(
            rect.Left + ringColumnWidth - ringShiftLeft - iqTextShiftLeft,
            rect.Top,
            iqTextWidth,
            rect.Height);
        RectangleF fractionRect = new RectangleF(
            iqTextRect.Right + statusGap,
            rect.Top,
            Math.Max(1.0f, rect.Right - iqTextRect.Right - statusGap),
            rect.Height);
        string statusText = GetCodexRadarStateLabel(snapshot.State);
        string timeText = FormatCodexRadarTime(snapshot);
        Color accent = GetCodexRadarStateColor(snapshot.State);
        float middleY = fractionRect.Top + fractionRect.Height * 0.50f;
        float lineWidth = Math.Max(S(26), Math.Min(fractionRect.Width * 0.90f, S(76)));
        RectangleF statusRect = new RectangleF(fractionRect.Left - S(3), fractionRect.Top, fractionRect.Width + S(6), Math.Max(1.0f, middleY - fractionRect.Top - S(3)));
        RectangleF timeRect = new RectangleF(fractionRect.Left - S(3), middleY + S(2), fractionRect.Width + S(6), Math.Max(1.0f, fractionRect.Bottom - middleY - S(2)));

        Font statusFont = this.fontCache.GetUi(Math.Max(8.0f, Math.Min(fractionRect.Height * 0.22f, fractionRect.Width * 0.30f)), FontStyle.Bold);
        Font timeFont = this.fontCache.GetMono(Math.Max(8.0f, Math.Min(fractionRect.Height * 0.19f, fractionRect.Width * 0.22f)), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(accent))
        using (Pen linePen = new Pen(DesignTokens.WithAlpha(accent, 180), Math.Max(1.0f, S(1))))
        {
            DrawCodexRadarFittedText(g, statusText, statusFont, brush, statusRect, StringAlignment.Center);
            g.DrawLine(linePen, fractionRect.Left + (fractionRect.Width - lineWidth) / 2.0f, middleY, fractionRect.Left + (fractionRect.Width + lineWidth) / 2.0f, middleY);
            DrawCodexRadarFittedText(g, timeText, timeFont, brush, timeRect, StringAlignment.Center);
        }

        DrawCodexModelIqStack(g, ringsRect, iqTextRect, snapshot);
    }

    private void DrawCodexModelIqStack(Graphics g, RectangleF rect, RectangleF iqTextRect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        RectangleF efficiencyRect;
        RectangleF modelIqRect;
        GetStackRowRects(rect, out efficiencyRect, out modelIqRect);

        DrawCodexModelEfficiencyRing(g, efficiencyRect, snapshot);
        DrawCodexModelEfficiencyData(g, iqTextRect, efficiencyRect, snapshot);
        DrawCodexModelIqRing(g, modelIqRect, snapshot);
        DrawCodexModelIqDeltaData(g, iqTextRect, modelIqRect, snapshot);
        DrawCodexModelCombinedFreshnessStatus(g, iqTextRect, efficiencyRect, modelIqRect, snapshot);
    }

    private void DrawCodexModelEfficiencyRing(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool known = snapshot != null && snapshot.ModelIqEfficiencyKnown;
        int tokenEfficiency = known ? snapshot.ModelIqTokenEfficiencyPercent : 100;
        int timeEfficiency = known ? snapshot.ModelIqTimeEfficiencyPercent : 100;
        string centerText = known
            ? GetCodexModelCompositeEfficiency(tokenEfficiency, timeEfficiency).ToString(CultureInfo.InvariantCulture)
            : "-";
        float ringSize = Math.Max(S(22), Math.Min(Math.Min(rect.Height, rect.Width - S(2)), S(34)));
        RectangleF ringRect = new RectangleF(
            rect.Left + (rect.Width - ringSize) / 2.0f,
            rect.Top + (rect.Height - ringSize) / 2.0f,
            ringSize,
            ringSize);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen basePen = new Pen(DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 242), stroke))
        using (Pen tokenLowPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 244), stroke))
        using (Pen tokenHighPen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(255, 184, 54), 245), stroke))
        using (Pen timeLowPen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(176, 112, 255), 244), stroke))
        using (Pen timeHighPen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(255, 226, 92), 245), stroke))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            tokenLowPen.StartCap = LineCap.Round;
            tokenLowPen.EndCap = LineCap.Round;
            tokenHighPen.StartCap = LineCap.Round;
            tokenHighPen.EndCap = LineCap.Round;
            timeLowPen.StartCap = LineCap.Round;
            timeLowPen.EndCap = LineCap.Round;
            timeHighPen.StartCap = LineCap.Round;
            timeHighPen.EndCap = LineCap.Round;

            g.DrawArc(basePen, arcRect, -90.0f, 360.0f);
            if (known)
            {
                DrawEfficiencyHalfArc(g, arcRect, tokenEfficiency, true, tokenLowPen, tokenHighPen);
                DrawEfficiencyHalfArc(g, arcRect, timeEfficiency, false, timeLowPen, timeHighPen);
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(8.0f, ringSize * 0.38f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            center.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(centerText, font, brush, ringRect, center);
        }
    }

    private static void DrawEfficiencyHalfArc(Graphics g, RectangleF arcRect, int efficiency, bool leftHalf, Pen lowPen, Pen highPen)
    {
        int clamped = Math.Max(0, Math.Min(200, efficiency));
        if (clamped == 100)
        {
            return;
        }

        float progress = Math.Min(1.0f, Math.Abs(clamped - 100) / 100.0f);
        if (leftHalf)
        {
            if (clamped < 100)
            {
                g.DrawArc(lowPen, arcRect, 90.0f, 180.0f * progress);
            }
            else
            {
                g.DrawArc(highPen, arcRect, -90.0f, -180.0f * progress);
            }

            return;
        }

        if (clamped < 100)
        {
            g.DrawArc(lowPen, arcRect, 90.0f, -180.0f * progress);
        }
        else
        {
            g.DrawArc(highPen, arcRect, -90.0f, 180.0f * progress);
        }
    }

    private void DrawCodexModelEfficiencyData(Graphics g, RectangleF rect, RectangleF efficiencyRowRect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        string text = "-";
        Color color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (snapshot != null && snapshot.ModelIqEfficiencyKnown)
        {
            GetCodexModelEfficiencyLabelAndColor(
                snapshot.ModelIqTokenEfficiencyPercent,
                snapshot.ModelIqTimeEfficiencyPercent,
                out text,
                out color);
        }

        RectangleF textRect = GetCodexRadarSideTextRect(rect, efficiencyRowRect);

        Font font = this.fontCache.GetUi(GetCodexRadarModelStateTextFontSize(rect, efficiencyRowRect), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(color))
        {
            DrawCodexRadarFittedText(g, text, font, brush, textRect, StringAlignment.Center);
        }

    }

    private static int GetCodexModelCompositeEfficiency(int tokenEfficiency, int timeEfficiency)
    {
        int token = Math.Max(0, tokenEfficiency);
        int time = Math.Max(0, timeEfficiency);
        return ClampEfficiencyPercent((int)Math.Round(Math.Sqrt(token * (double)time), MidpointRounding.AwayFromZero));
    }

    private void GetCodexModelEfficiencyLabelAndColor(
        int tokenEfficiency,
        int timeEfficiency,
        out string text,
        out Color color)
    {
        int tokenLowThreshold = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent,
                this.currentSettings.CodexModelTokenEfficiencyLowThresholdPercent));
        int timeLowThreshold = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent,
                this.currentSettings.CodexModelTimeEfficiencyLowThresholdPercent));
        bool tokenLow = tokenEfficiency < tokenLowThreshold;
        bool timeLow = timeEfficiency < timeLowThreshold;
        bool tokenHigh = tokenEfficiency > 100;
        bool timeHigh = timeEfficiency > 100;
        bool tokenBelowBaseline = tokenEfficiency < 100;
        bool timeBelowBaseline = timeEfficiency < 100;
        if ((tokenBelowBaseline && timeHigh) || (timeBelowBaseline && tokenHigh))
        {
            int compositeEfficiency = GetCodexModelCompositeEfficiency(tokenEfficiency, timeEfficiency);
            if (compositeEfficiency > 100)
            {
                text = "较高";
                color = DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 245);
                return;
            }

            if (compositeEfficiency < 100)
            {
                text = "较低";
                color = DesignTokens.WithAlpha(GetCodexRadarLightRed(), 245);
                return;
            }

            text = "普通";
            color = DesignTokens.White(245);
            return;
        }

        if (tokenLow || timeLow)
        {
            text = "低效";
            if (tokenLow && timeLow)
            {
                color = tokenEfficiency <= timeEfficiency
                    ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245)
                    : DesignTokens.WithAlpha(Color.FromArgb(176, 112, 255), 245);
                return;
            }

            color = tokenLow
                ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245)
                : DesignTokens.WithAlpha(Color.FromArgb(176, 112, 255), 245);
            return;
        }

        if (tokenHigh || timeHigh)
        {
            text = "高效";
            if (tokenHigh && timeHigh)
            {
                color = tokenEfficiency >= timeEfficiency
                    ? DesignTokens.WithAlpha(Color.FromArgb(255, 184, 54), 245)
                    : DesignTokens.WithAlpha(Color.FromArgb(255, 226, 92), 245);
                return;
            }

            color = tokenHigh
                ? DesignTokens.WithAlpha(Color.FromArgb(255, 184, 54), 245)
                : DesignTokens.WithAlpha(Color.FromArgb(255, 226, 92), 245);
            return;
        }

        text = "普通";
        color = DesignTokens.White(245);
    }

    private void DrawCodexRadarPredictionRing(Graphics g, RectangleF rect, int probability24, int probability48, string centerText)
    {
        probability24 = ClampPercent(probability24);
        probability48 = ClampPercent(probability48);
        float ringSize = Math.Max(S(22), Math.Min(Math.Min(rect.Height, rect.Width - S(2)), S(34)));
        RectangleF ringRect = new RectangleF(
            rect.Left + (rect.Width - ringSize) / 2.0f,
            rect.Top + (rect.Height - ringSize) / 2.0f,
            ringSize,
            ringSize);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);
        Color probability24Color = probability24 >= 50
            ? DesignTokens.Colors.Warning
            : GetCodexRadarLightRed();
        Color probability48Color = Color.FromArgb(232, 86, 218);

        using (Pen backgroundPen = new Pen(DesignTokens.White(72), stroke))
        using (Pen probability48Pen = new Pen(DesignTokens.WithAlpha(probability48Color, 238), stroke))
        using (Pen probability24Pen = new Pen(DesignTokens.WithAlpha(probability24Color, 242), stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            probability48Pen.StartCap = LineCap.Round;
            probability48Pen.EndCap = LineCap.Round;
            probability24Pen.StartCap = LineCap.Round;
            probability24Pen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            float probability48Progress = GetCodexRadarPredictionRingProgress(probability48);
            float probability24Progress = GetCodexRadarPredictionRingProgress(probability24);
            if (probability48Progress > 0.0f)
            {
                g.DrawArc(probability48Pen, arcRect, -90.0f, 360.0f * probability48Progress);
            }

            if (probability24Progress > 0.0f)
            {
                g.DrawArc(probability24Pen, arcRect, -90.0f, 360.0f * probability24Progress);
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(8.0f, ringSize * 0.44f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            center.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(centerText, font, brush, ringRect, center);
        }
    }

    private static float GetCodexRadarPredictionRingProgress(int probability)
    {
        int clamped = Math.Max(0, Math.Min(50, probability));
        return clamped / 50.0f;
    }

    private static Color GetCodexRadarLightGreen()
    {
        return Color.FromArgb(142, 242, 185);
    }

    private static Color GetCodexRadarLightRed()
    {
        return Color.FromArgb(255, 151, 151);
    }

    private void DrawCodexModelIqRing(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool known = snapshot != null && snapshot.ModelIqKnown;
        int passRatePercent = known ? ClampPercent(snapshot.ModelIqPassRatePercent) : 0;
        string centerText = known ? passRatePercent.ToString(CultureInfo.InvariantCulture) : "-";
        int passed;
        int validTasks;
        bool scoreKnown = TryGetCodexModelIqPassed(snapshot, out passed, out validTasks);
        float ringSize = Math.Max(S(22), Math.Min(Math.Min(rect.Height, rect.Width - S(2)), S(34)));
        RectangleF ringRect = new RectangleF(
            rect.Left + (rect.Width - ringSize) / 2.0f,
            rect.Top + (rect.Height - ringSize) / 2.0f,
            ringSize,
            ringSize);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen backgroundPen = new Pen(DesignTokens.White(72), stroke))
        using (Pen baselinePen = new Pen(DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 242), stroke))
        using (Pen deficitPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 242), stroke))
        using (Pen surplusPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245), stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            baselinePen.StartCap = LineCap.Round;
            baselinePen.EndCap = LineCap.Round;
            deficitPen.StartCap = LineCap.Round;
            deficitPen.EndCap = LineCap.Round;
            surplusPen.StartCap = LineCap.Round;
            surplusPen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (scoreKnown)
            {
                g.DrawArc(baselinePen, arcRect, -90.0f, 360.0f);
                int baselinePassed = GetCodexModelIqBaselinePassed();
                int delta = passed - baselinePassed;
                if (delta < 0)
                {
                    float deficitProgress = Math.Min(1.0f, Math.Abs(delta) / (float)Math.Max(1, baselinePassed));
                    g.DrawArc(deficitPen, arcRect, -90.0f, -360.0f * deficitProgress);
                }
                else if (delta > 0)
                {
                    int surplusCapacity = Math.Max(1, validTasks - baselinePassed);
                    float surplusProgress = Math.Min(1.0f, delta / (float)surplusCapacity);
                    g.DrawArc(surplusPen, arcRect, -90.0f, 360.0f * surplusProgress);
                }
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(8.0f, ringSize * 0.40f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            center.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(centerText, font, brush, ringRect, center);
        }
    }

    private void DrawCodexModelIqDeltaData(Graphics g, RectangleF rect, RectangleF modelIqRowRect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int passed;
        int validTasks;
        bool known = TryGetCodexModelIqPassed(snapshot, out passed, out validTasks);
        string text = "-";
        Color color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (known)
        {
            int delta = passed - GetCodexModelIqBaselinePassed();
            if (delta < 0)
            {
                text = "降智";
                color = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            }
            else if (delta > 0)
            {
                text = "增智";
                color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            }
            else
            {
                text = "正常";
                color = DesignTokens.White(245);
            }
        }

        RectangleF textRect = GetCodexRadarSideTextRect(rect, modelIqRowRect);

        Font font = this.fontCache.GetUi(GetCodexRadarModelStateTextFontSize(rect, modelIqRowRect), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(color))
        {
            DrawCodexRadarFittedText(g, text, font, brush, textRect, StringAlignment.Center);
        }

    }

    private void DrawCodexModelCombinedFreshnessStatus(
        Graphics g,
        RectangleF textColumnRect,
        RectangleF efficiencyRowRect,
        RectangleF modelIqRowRect,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null ||
            !snapshot.ModelIqRefreshedAtKnown ||
            (!IsCodexModelEfficiencyLow(snapshot) && !IsCodexModelIqDown(snapshot)))
        {
            return;
        }

        RectangleF efficiencyTextRect = GetCodexRadarSideTextRect(textColumnRect, efficiencyRowRect);
        RectangleF modelIqTextRect = GetCodexRadarSideTextRect(textColumnRect, modelIqRowRect);
        float upperCenterY = efficiencyTextRect.Top + efficiencyTextRect.Height / 2.0f;
        float lowerCenterY = modelIqTextRect.Top + modelIqTextRect.Height / 2.0f;
        if (lowerCenterY <= upperCenterY)
        {
            return;
        }

        float upperTextBottom = upperCenterY + GetCodexRadarModelStateTextFontSize(textColumnRect, efficiencyRowRect) * 0.50f;
        float lowerTextTop = lowerCenterY - GetCodexRadarModelStateTextFontSize(textColumnRect, modelIqRowRect) * 0.50f;
        float spaceTop = upperTextBottom;
        float spaceBottom = lowerTextTop;
        if (spaceBottom <= spaceTop)
        {
            spaceTop = upperCenterY;
            spaceBottom = lowerCenterY;
        }

        float availableHeight = Math.Max(1.0f, spaceBottom - spaceTop);
        float timeHeight = Math.Max(S(7), Math.Min(S(10), availableHeight * 0.36f));
        string efficiencyText = "-";
        Color efficiencyColor;
        if (snapshot.ModelIqEfficiencyKnown)
        {
            GetCodexModelEfficiencyLabelAndColor(
                snapshot.ModelIqTokenEfficiencyPercent,
                snapshot.ModelIqTimeEfficiencyPercent,
                out efficiencyText,
                out efficiencyColor);
        }

        float efficiencyTextLeft = efficiencyTextRect.Left;
        Font efficiencyFont = this.fontCache.GetUi(
            GetCodexRadarModelStateTextFontSize(textColumnRect, efficiencyRowRect),
            FontStyle.Bold);
        {
            float efficiencyTextWidth = Math.Min(
                efficiencyTextRect.Width,
                g.MeasureString(efficiencyText, efficiencyFont).Width);
            efficiencyTextLeft += Math.Max(0.0f, (efficiencyTextRect.Width - efficiencyTextWidth) / 2.0f);
        }

        RectangleF timeRect = new RectangleF(
            efficiencyTextLeft,
            spaceTop + (availableHeight - timeHeight) / 2.0f,
            Math.Max(1.0f, textColumnRect.Right - efficiencyTextLeft),
            timeHeight);

        DrawCodexModelFreshnessStatus(g, timeRect, snapshot);
    }

    private bool IsCodexModelEfficiencyLow(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqEfficiencyKnown)
        {
            return false;
        }

        string text;
        Color color;
        GetCodexModelEfficiencyLabelAndColor(
            snapshot.ModelIqTokenEfficiencyPercent,
            snapshot.ModelIqTimeEfficiencyPercent,
            out text,
            out color);
        return string.Equals(text, "低效", StringComparison.Ordinal);
    }

    private bool IsCodexModelIqDown(CodexRadarSnapshot snapshot)
    {
        int passed;
        int validTasks;
        return TryGetCodexModelIqPassed(snapshot, out passed, out validTasks) &&
            passed < GetCodexModelIqBaselinePassed();
    }

    private void DrawCodexModelFreshnessStatus(Graphics g, RectangleF timeRect, CodexRadarSnapshot snapshot)
    {
        if (timeRect.Width <= 0 || timeRect.Height <= 0 || snapshot == null)
        {
            return;
        }

        string text = GetCodexModelFreshnessStatus(snapshot);
        if (text.Length == 0)
        {
            return;
        }

        float fontSize = Math.Max(6.0f * this.scale, Math.Min(timeRect.Height * 0.86f, timeRect.Width * 0.23f));

        Font font = this.fontCache.GetUi(fontSize, FontStyle.Regular);
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(Color.FromArgb(205, 205, 205), 230)))
        {
            RectangleF drawRect = GetCodexModelFreshnessDrawRect(g, text, font, timeRect);
            DrawCodexRadarFittedText(g, text, font, brush, drawRect, StringAlignment.Near);
        }
    }

    private RectangleF GetCodexModelFreshnessDrawRect(Graphics g, string text, Font font, RectangleF anchorRect)
    {
        if (string.IsNullOrEmpty(text) || anchorRect.Width <= 0)
        {
            return anchorRect;
        }

        float requiredWidth = g.MeasureString(text, font).Width + S(6);
        if (requiredWidth <= anchorRect.Width)
        {
            return anchorRect;
        }

        return new RectangleF(
            anchorRect.Left,
            anchorRect.Top,
            requiredWidth,
            anchorRect.Height);
    }

    private static string GetCodexModelFreshnessStatus(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqDataDateKnown)
        {
            return string.Empty;
        }

        // DataDate describes the website record; RefreshedAt describes our last successful fetch.
        DateTime today = DateTime.Now.Date;
        DateTime dataDate = snapshot.ModelIqDataDateLocal.Date;
        if (dataDate >= today)
        {
            return "Updated";
        }

        if (snapshot.ModelIqRefreshedAtKnown && snapshot.ModelIqRefreshedAtLocal.Date >= today)
        {
            return "Unupdated";
        }

        return "Outdated";
    }

    private RectangleF GetCodexRadarSideTextRect(RectangleF textColumnRect, RectangleF rowRect)
    {
        float rowHeight = rowRect.Height > 0 ? rowRect.Height : textColumnRect.Height;
        float verticalOffset = S(1);
        return new RectangleF(
            textColumnRect.Left,
            rowRect.Top + verticalOffset,
            Math.Max(1.0f, textColumnRect.Width),
            rowHeight);
    }

    private float GetCodexRadarSideTextFontSize(RectangleF textColumnRect, RectangleF rowRect)
    {
        float rowHeight = rowRect.Height > 0 ? rowRect.Height : textColumnRect.Height;
        return Math.Max(8.0f, Math.Min(rowHeight * 0.46f, textColumnRect.Width * 0.42f));
    }

    private float GetCodexRadarModelStateTextFontSize(RectangleF textColumnRect, RectangleF rowRect)
    {
        return GetCodexRadarSideTextFontSize(textColumnRect, rowRect) * 2.36f;
    }

    private void DrawQuotaWidget(Graphics g, RectangleF rect, CodexRadarSnapshot radarSnapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        CodexQuotaSnapshot snapshot = this.quotaSnapshot ?? CodexQuotaSnapshot.CreateDefault();
        bool goldTestActive = DateTime.UtcNow < this.extraResetGoldTestUntilUtc;
        bool fiveHourGold;
        bool weeklyGold;
        lock (this.quotaResetStateLock)
        {
            fiveHourGold = goldTestActive || this.fiveHourQuotaProtectionGold;
            weeklyGold = goldTestActive || this.weeklyQuotaProtectionGold;
        }

        float statusGap = S(4);
        float statusWidth = Math.Max(S(42), Math.Min(S(52), rect.Width * 0.16f));
        float healthWidth = Math.Max(S(58), Math.Min(S(72), rect.Width * 0.20f));
        RectangleF resetStatusRect = new RectangleF(rect.Right - statusWidth, rect.Top, statusWidth, rect.Height);
        RectangleF predictionStatusRect = GetCompactPredictionStatusRect(resetStatusRect);
        RectangleF healthRect = new RectangleF(resetStatusRect.Left - statusGap - healthWidth, rect.Top, healthWidth, rect.Height);
        RectangleF rowsBounds = new RectangleF(rect.Left, rect.Top, Math.Max(0.0f, healthRect.Left - rect.Left - statusGap), rect.Height);
        if (rowsBounds.Width >= S(54))
        {
            RectangleF firstRow;
            RectangleF secondRow;
            GetStackRowRects(rowsBounds, out firstRow, out secondRow);
            DrawQuotaRow(
                g,
                firstRow,
                snapshot.FiveHourPercent,
                snapshot.FiveHourResetKnown ? snapshot.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "N/A",
                this.quotaCodexProcessRunning,
                fiveHourGold);
            DrawQuotaRow(
                g,
                secondRow,
                snapshot.WeeklyPercent,
                snapshot.WeeklyResetKnown ? snapshot.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture) : "N/A",
                this.quotaCodexProcessRunning,
                weeklyGold);
        }

        DrawServiceHealthWidget(g, healthRect);
        DrawCodexResetStatus(g, resetStatusRect, predictionStatusRect.Bottom + S(2), radarSnapshot);
        DrawCodexRadarPredictionStatus(g, predictionStatusRect, radarSnapshot);
    }

    private RectangleF GetCompactPredictionStatusRect(RectangleF bounds)
    {
        RectangleF firstRow;
        RectangleF secondRow;
        GetStackRowRects(bounds, out firstRow, out secondRow);
        return firstRow;
    }

    private void DrawCodexRadarPredictionStatus(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int probability24 = snapshot != null && snapshot.PredictionKnown ? snapshot.Probability24Percent : 0;
        int probability48 = snapshot != null && snapshot.PredictionKnown ? snapshot.Probability48Percent : 0;
        string centerText = snapshot != null && snapshot.PredictionKnown
            ? GetCodexRadarPredictionLevelGlyph(snapshot.PredictionLevel)
            : "-";
        DrawCodexRadarPredictionRing(g, rect, probability24, probability48, centerText);
    }

    private static string GetCodexRadarPredictionLevelGlyph(string level)
    {
        if (string.Equals(level, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "高";
        }

        if (string.Equals(level, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "中";
        }

        if (string.Equals(level, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "低";
        }

        return "-";
    }

    private void DrawServiceHealthWidget(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool online;
        ServiceHealthState radarHealth;
        ServiceHealthState codexHealth;
        ServiceHealthState resetHealth;
        lock (this.serviceHealthLock)
        {
            online = this.serviceNetworkAvailable;
            radarHealth = online ? this.radarServiceHealth : ServiceHealthState.Offline;
            codexHealth = online ? this.codexServiceHealth : ServiceHealthState.Offline;
            resetHealth = online ? this.resetServiceHealth : ServiceHealthState.Offline;
        }

        float gap = S(1);
        float rowHeight = Math.Max(1.0f, (rect.Height - gap * 2.0f) / 3.0f);
        RectangleF radarRect = new RectangleF(rect.Left, rect.Top, rect.Width, rowHeight);
        RectangleF codexRect = new RectangleF(rect.Left, radarRect.Bottom + gap, rect.Width, rowHeight);
        RectangleF resetRect = new RectangleF(rect.Left, codexRect.Bottom + gap, rect.Width, rowHeight);

        DrawServiceHealthRow(g, radarRect, "Rader", radarHealth);
        DrawServiceHealthRow(g, codexRect, "Codex", codexHealth);
        DrawServiceHealthRow(g, resetRect, "Reseter", resetHealth);
    }

    private void DrawServiceHealthRow(Graphics g, RectangleF rect, string label, ServiceHealthState state)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Color textColor = state == ServiceHealthState.Offline
            ? DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)
            : DesignTokens.White(245);
        bool smallCross = state == ServiceHealthState.Unavailable || state == ServiceHealthState.Unreachable;
        float crossReserve = smallCross ? Math.Max(S(10), rect.Height * 0.52f) + S(2) : 0.0f;
        RectangleF textRect = new RectangleF(
            rect.Left + S(1),
            rect.Top,
            Math.Max(1.0f, rect.Width - S(2) - crossReserve),
            rect.Height);

        Font font = this.fontCache.GetUi(Math.Max(8.0f, Math.Min(rect.Height * 0.56f, rect.Width * 0.18f)), FontStyle.Bold);
        using (SolidBrush textBrush = new SolidBrush(textColor))
        {
            DrawCodexRadarFittedText(g, label, font, textBrush, textRect, StringAlignment.Near);
        }

        if (smallCross)
        {
            float crossSize = Math.Max(S(8), Math.Min(S(13), rect.Height * 0.56f));
            RectangleF crossRect = new RectangleF(
                rect.Right - crossSize - S(2),
                rect.Top + (rect.Height - crossSize) / 2.0f,
                crossSize,
                crossSize);
            Color crossColor = state == ServiceHealthState.Unreachable
                ? DesignTokens.Colors.DangerStrong
                : DesignTokens.Colors.Warning;
            DrawServiceHealthSmallCross(g, crossRect, crossColor);
        }
    }

    private void DrawServiceHealthSmallCross(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(DesignTokens.WithAlpha(color, 230), Math.Max(1.0f, S(2))))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom);
            g.DrawLine(pen, rect.Right, rect.Top, rect.Left, rect.Bottom);
        }
    }

    private void GetStackRowRects(RectangleF bounds, out RectangleF firstRow, out RectangleF secondRow)
    {
        float rowGap = S(8);
        float rowInsetX = S(2);
        float rowTopInset = S(1);
        float rowHeight = Math.Max(1.0f, (bounds.Height - rowGap) / 2.0f);
        float rowWidth = Math.Max(1.0f, bounds.Width - rowInsetX * 2.0f);
        float contentHeight = Math.Max(1.0f, rowHeight);
        float secondTop = bounds.Top + S(4) + rowHeight + S(3);
        firstRow = new RectangleF(bounds.Left + rowInsetX, bounds.Top + rowTopInset, rowWidth, contentHeight);
        secondRow = new RectangleF(bounds.Left + rowInsetX, secondTop, rowWidth, contentHeight);
    }

    private void DrawQuotaRow(Graphics g, RectangleF rect, int percent, string resetText, bool codexRunning, bool extraResetGold)
    {
        percent = ClampPercent(percent);
        float ringSize = Math.Max(S(22), Math.Min(rect.Height, S(34)));
        RectangleF ringRect = new RectangleF(rect.Left, rect.Top + (rect.Height - ringSize) / 2.0f, ringSize, ringSize);
        Color ringColor = extraResetGold
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245)
            : GetQuotaColor(percent);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen backgroundPen = new Pen(DesignTokens.White(78), stroke))
        using (Pen valuePen = new Pen(ringColor, stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (percent > 0)
            {
                g.DrawArc(valuePen, arcRect, -90.0f, 360.0f * percent / 100.0f);
            }
        }

        Font numberFont = this.fontCache.GetUi(Math.Max(8.5f, ringSize * 0.38f), FontStyle.Bold);
        using (SolidBrush numberBrush = new SolidBrush(codexRunning ? DesignTokens.White(248) : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            g.DrawString(percent.ToString(CultureInfo.InvariantCulture), numberFont, numberBrush, ringRect, center);
        }

        RectangleF resetRect = new RectangleF(
            ringRect.Right + S(2),
            rect.Top,
            Math.Max(1.0f, rect.Right - ringRect.Right - S(2)),
            rect.Height);

        Font resetFont = this.fontCache.GetUi(Math.Max(10.0f, ringSize * 0.66f), FontStyle.Bold);
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.TextStrong(226)))
        {
            DrawCodexRadarFittedText(g, resetText, resetFont, textBrush, resetRect, StringAlignment.Near);
        }
    }

    private void DrawCodexResetStatus(Graphics g, RectangleF rect, float reservedTop, CodexRadarSnapshot radarSnapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool isYes;
        DateTime nowUtc = DateTime.UtcNow;
        lock (this.codexResetStatusLock)
        {
            isYes = this.codexResetStatusKnown && this.codexResetStatusYes;
            if (this.codexResetStatusInferredUntilUtc != DateTime.MinValue &&
                nowUtc < this.codexResetStatusInferredUntilUtc)
            {
                isYes = true;
            }
        }

        if (radarSnapshot != null && radarSnapshot.State == CodexRadarState.Open)
        {
            isYes = false;
        }

        string text = isYes ? "Yes" : "No";
        Color color = isYes ? DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 245) : DesignTokens.White(245);
        float textOffset = Math.Max(0.0f, Math.Min(rect.Height * 0.34f, reservedTop - rect.Top));
        RectangleF textRect = new RectangleF(
            rect.Left + S(2),
            rect.Top + textOffset,
            Math.Max(1.0f, rect.Width - S(2)),
            Math.Max(1.0f, rect.Height - textOffset));

        Font font = this.fontCache.GetUi(Math.Max(11.0f, Math.Min(rect.Height * 0.48f, rect.Width * 0.48f)), FontStyle.Bold);
        using (Pen divider = new Pen(DesignTokens.White(46), Math.Max(1.0f, S(1))))
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.DrawLine(divider, rect.Left + S(1), rect.Top + S(8), rect.Left + S(1), rect.Bottom - S(8));
            DrawCodexRadarFittedText(g, text, font, brush, textRect, StringAlignment.Center);
        }
    }

    private CodexRadarSnapshot GetCodexRadarDisplaySnapshot()
    {
        CodexRadarSnapshot snapshot;
        if (this.currentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            snapshot = BuildTestCodexRadarSnapshot(this.currentSettings.CodexRadarTestMode);
            ApplyCodexModelEfficiencyBaselineOverride(snapshot);
            ApplyCodexModelIqTestOverride(snapshot);
            ApplyCodexModelEfficiencyTestOverride(snapshot);
            return snapshot;
        }

        lock (this.codexRadarStatusLock)
        {
            snapshot = this.codexRadarSnapshot != null
                ? this.codexRadarSnapshot.Clone()
                : CodexRadarSnapshot.CreateDefault();
        }

        ApplyCodexModelEfficiencyBaselineOverride(snapshot);
        ApplyCodexModelIqTestOverride(snapshot);
        ApplyCodexModelEfficiencyTestOverride(snapshot);
        return snapshot;
    }

    private CodexRadarSnapshot BuildTestCodexRadarSnapshot(CodexRadarTestMode mode)
    {
        DateTime now = DateTime.Now;
        CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
        snapshot.CheckedAtLocal = now;
        snapshot.CheckedAtKnown = true;
        snapshot.PredictionKnown = true;
        snapshot.ModelIqKnown = true;
        if (mode == CodexRadarTestMode.Open)
        {
            snapshot.State = CodexRadarState.Open;
            snapshot.OpenedAtLocal = now.AddMinutes(-83);
            snapshot.OpenedAtKnown = true;
            snapshot.ClosedAtLocal = now.AddDays(-2).AddHours(-4);
            snapshot.ClosedAtKnown = true;
            snapshot.PredictionLevel = "high";
            snapshot.Probability24Percent = 40;
            snapshot.Probability48Percent = 54;
            ApplyCodexModelIqScore(snapshot, 9);
            return snapshot;
        }

        if (mode == CodexRadarTestMode.Closed)
        {
            snapshot.State = CodexRadarState.Closed;
            snapshot.OpenedAtLocal = now.AddHours(-3).AddMinutes(-18);
            snapshot.ClosedAtLocal = now.AddMinutes(-24);
            snapshot.OpenedAtKnown = true;
            snapshot.ClosedAtKnown = true;
            snapshot.PredictionLevel = "medium";
            snapshot.Probability24Percent = 30;
            snapshot.Probability48Percent = 42;
            ApplyCodexModelIqScore(snapshot, 6);
            return snapshot;
        }

        snapshot.State = CodexRadarState.None;
        snapshot.ClosedAtLocal = now.AddDays(-3).AddHours(-2);
        snapshot.ClosedAtKnown = true;
        snapshot.PredictionLevel = "low";
        snapshot.Probability24Percent = 8;
        snapshot.Probability48Percent = 14;
        ApplyCodexModelIqScore(snapshot, GetCodexModelIqBaselinePassed());
        return snapshot;
    }

    private void ApplyCodexModelIqTestOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !this.currentSettings.CodexModelIqTestEnabled)
        {
            return;
        }

        ApplyCodexModelIqScore(snapshot, this.currentSettings.CodexModelIqTestPassed);
    }

    private void ApplyCodexModelEfficiencyBaselineOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqEfficiencyInputKnown)
        {
            return;
        }

        bool changed = false;
        int tokenEfficiency;
        if (TryCalculateCodexModelEfficiencyPercent(
            snapshot.ModelIqEfficiencyPassed,
            snapshot.ModelIqEfficiencyTotalTokens,
            this.currentSettings.CodexModelTokenEfficiencyBaselinePassed,
            this.currentSettings.CodexModelTokenEfficiencyBaselineTokens,
            out tokenEfficiency))
        {
            snapshot.ModelIqTokenEfficiencyPercent = tokenEfficiency;
            changed = true;
        }

        int timeEfficiency;
        if (TryCalculateCodexModelEfficiencyPercent(
            snapshot.ModelIqEfficiencyPassed,
            snapshot.ModelIqEfficiencySerialSeconds,
            this.currentSettings.CodexModelTimeEfficiencyBaselinePassed,
            this.currentSettings.CodexModelTimeEfficiencyBaselineSeconds,
            out timeEfficiency))
        {
            snapshot.ModelIqTimeEfficiencyPercent = timeEfficiency;
            changed = true;
        }

        if (changed)
        {
            snapshot.ModelIqEfficiencyKnown = true;
        }
    }

    private void ApplyCodexModelEfficiencyTestOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !this.currentSettings.CodexModelEfficiencyTestEnabled)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyPercent,
                this.currentSettings.CodexModelTokenEfficiencyTestPercent));
        snapshot.ModelIqTimeEfficiencyPercent = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyPercent,
                this.currentSettings.CodexModelTimeEfficiencyTestPercent));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private static bool TryCalculateCodexModelEfficiencyPercent(
        double currentPassed,
        double currentValue,
        int baselinePassed,
        int baselineValue,
        out int efficiencyPercent)
    {
        efficiencyPercent = 100;
        if (currentPassed <= 0.0 || currentValue <= 0.0 || baselinePassed <= 0 || baselineValue <= 0)
        {
            return false;
        }

        double baselineRate = baselinePassed / (double)baselineValue;
        if (baselineRate <= 0.0)
        {
            return false;
        }

        efficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentValue) / baselineRate * 100.0, MidpointRounding.AwayFromZero));
        return true;
    }

    private void ApplyCodexModelIqScore(CodexRadarSnapshot snapshot, int passed)
    {
        if (snapshot == null)
        {
            return;
        }

        int validTasks = CodexModelIqNominalTasks;
        passed = Math.Max(WidgetSettings.MinCodexModelIqPassed, Math.Min(validTasks, passed));
        snapshot.ModelIqKnown = true;
        snapshot.ModelIqPassedKnown = true;
        snapshot.ModelIqPassed = passed;
        snapshot.ModelIqValidTasks = validTasks;
        snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(passed / (double)validTasks);
        snapshot.ModelIqTokenEfficiencyPercent = 100;
        snapshot.ModelIqTimeEfficiencyPercent = 100;
        snapshot.ModelIqEfficiencyKnown = true;
        if (!snapshot.ModelIqDataDateKnown)
        {
            snapshot.ModelIqDataDateLocal = DateTime.Now.Date;
            snapshot.ModelIqDataDateKnown = true;
        }

        if (!snapshot.ModelIqRefreshedAtKnown)
        {
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
        }

        int delta = passed - GetCodexModelIqBaselinePassed();
        snapshot.ModelIqStatus = delta < 0 ? "red" : "green";
    }

    private Color GetCodexRadarStateColor(CodexRadarState state)
    {
        if (state == CodexRadarState.Open)
        {
            return this.renderTickCount % 2 == 0
                ? Color.FromArgb(245, 174, 43)
                : Color.FromArgb(255, 219, 82);
        }

        if (state == CodexRadarState.Closed)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 245);
    }

    private static string GetCodexRadarStateLabel(CodexRadarState state)
    {
        if (state == CodexRadarState.Open)
        {
            return "Open";
        }

        if (state == CodexRadarState.Closed)
        {
            return "Closed";
        }

        return "None";
    }

    private int GetCodexModelIqBaselinePassed()
    {
        return Math.Max(
            WidgetSettings.MinCodexModelIqPassed,
            Math.Min(CodexModelIqNominalTasks, this.currentSettings.CodexModelIqBaselinePassed));
    }

    private static bool TryGetCodexModelIqPassed(CodexRadarSnapshot snapshot, out int passed, out int validTasks)
    {
        passed = 0;
        validTasks = CodexModelIqNominalTasks;
        if (snapshot == null || !snapshot.ModelIqKnown)
        {
            return false;
        }

        validTasks = snapshot.ModelIqValidTasks > 0 ? snapshot.ModelIqValidTasks : CodexModelIqNominalTasks;
        validTasks = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTasks));
        if (snapshot.ModelIqPassedKnown)
        {
            passed = Math.Max(0, Math.Min(validTasks, snapshot.ModelIqPassed));
            return true;
        }

        passed = (int)Math.Round(ClampPercent(snapshot.ModelIqPassRatePercent) / 100.0 * validTasks, MidpointRounding.AwayFromZero);
        passed = Math.Max(0, Math.Min(validTasks, passed));
        return true;
    }

    private static Color GetCodexModelIqStatusColor(string status, bool known)
    {
        if (!known)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 235);
        }

        string normalized = NormalizeCodexModelIqStatus(status);
        if (normalized == "green")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235);
        }

        if (normalized == "yellow")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 238);
        }

        if (normalized == "orange")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 238);
    }

    private string FormatCodexRadarTime(CodexRadarSnapshot snapshot)
    {
        DateTime now = DateTime.Now;
        if (snapshot == null)
        {
            return "--:--";
        }

        if (snapshot.State == CodexRadarState.Open)
        {
            return snapshot.OpenedAtKnown ? FormatCompactDuration(now - snapshot.OpenedAtLocal) : "--:--:--";
        }

        if (snapshot.State == CodexRadarState.Closed)
        {
            if (snapshot.OpenedAtKnown && snapshot.ClosedAtKnown)
            {
                return FormatCompactDuration(snapshot.ClosedAtLocal - snapshot.OpenedAtLocal);
            }

            if (snapshot.ClosedAtKnown)
            {
                return FormatCompactDuration(now - snapshot.ClosedAtLocal);
            }

            return "--:--:--";
        }

        return snapshot.CheckedAtKnown ? snapshot.CheckedAtLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "--:--";
    }

    private static string FormatCompactDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 0.0)
        {
            duration = TimeSpan.Zero;
        }

        int totalHours = (int)Math.Floor(duration.TotalHours);
        return totalHours.ToString("00", CultureInfo.InvariantCulture) +
            ":" + duration.Minutes.ToString("00", CultureInfo.InvariantCulture) +
            ":" + duration.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    private bool RefreshQuotaInfoIfNeeded()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime nowLocal = DateTime.Now;
        bool codexProcessChanged;
        bool codexRunning = UpdateCodexProcessRunningStatus(nowUtc, out codexProcessChanged);
        bool resetDue = IsQuotaResetDue(this.quotaSnapshot, nowLocal);
        // Active Codex sessions need prompt quota updates; inactive sessions use a much slower
        // schedule unless a reset boundary or process transition requires an immediate read.
        bool refreshDue =
            resetDue ||
            this.lastQuotaRefreshUtc == DateTime.MinValue ||
            (codexProcessChanged && codexRunning);

        if (!refreshDue)
        {
            if (codexRunning)
            {
                refreshDue = (nowUtc - this.lastQuotaRefreshUtc).TotalSeconds >= GetQuotaActiveRefreshSeconds();
            }
            else
            {
                refreshDue = IsInactiveQuotaRefreshDue(nowUtc);
            }
        }

        if (!refreshDue)
        {
            return codexProcessChanged;
        }

        if (!codexRunning)
        {
            MarkInactiveQuotaRefresh(nowUtc);
        }

        if (resetDue)
        {
            ActivateDueQuotaResetProtections(this.quotaSnapshot, nowLocal, nowUtc);
        }

        this.lastQuotaRefreshUtc = nowUtc;
        bool quotaKnown;
        CodexQuotaSnapshot nextSnapshot = ReadQuotaSnapshot(out quotaKnown);
        if (IsQuotaResetDue(nextSnapshot, nowLocal))
        {
            ActivateDueQuotaResetProtections(nextSnapshot, nowLocal, nowUtc);
        }

        this.quotaSnapshot = ApplyQuotaResetProtections(nextSnapshot);
        this.quotaSourceKnown = quotaKnown;
        UpdateCodexServiceHealth(quotaKnown);
        return true;
    }

    private void UpdateServiceConnectivityHealth()
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            // NetworkChange callbacks only set this flag; the UI scheduler performs the actual query.
            if (!this.serviceNetworkRefreshRequested)
            {
                return;
            }

            this.serviceNetworkRefreshRequested = false;
        }

        bool networkAvailable = IsNetworkAvailable();
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = networkAvailable;
            if (!networkAvailable)
            {
                this.radarServiceHealth = ServiceHealthState.Offline;
                this.codexServiceHealth = ServiceHealthState.Offline;
                this.resetServiceHealth = ServiceHealthState.Offline;
                return;
            }

            if (this.radarServiceHealth == ServiceHealthState.Offline)
            {
                this.radarServiceHealth = ServiceHealthState.Unknown;
            }

            if (this.resetServiceHealth == ServiceHealthState.Offline)
            {
                this.resetServiceHealth = ServiceHealthState.Unknown;
            }

            this.codexServiceHealth = this.quotaSourceKnown ? ServiceHealthState.Normal : ServiceHealthState.Unavailable;
        }
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        RequestServiceNetworkRefresh();
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        RequestServiceNetworkRefresh();
    }

    private void RequestServiceNetworkRefresh()
    {
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkRefreshRequested = true;
        }
    }

    private void UpdateCodexServiceHealth(bool quotaKnown)
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.codexServiceHealth = this.serviceNetworkAvailable
                ? (quotaKnown ? ServiceHealthState.Normal : ServiceHealthState.Unavailable)
                : ServiceHealthState.Offline;
        }
    }

    private bool IsServiceNetworkAvailable()
    {
        lock (this.serviceHealthLock)
        {
            return this.serviceNetworkAvailable;
        }
    }

    private bool ShouldForceServiceHealthRefresh(ServiceHealthState state)
    {
        lock (this.serviceHealthLock)
        {
            return state == ServiceHealthState.Unknown || state == ServiceHealthState.Offline;
        }
    }

    private void SetRadarServiceHealth(ServiceHealthState health)
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.radarServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }
    }

    private void SetResetServiceHealth(ServiceHealthState health)
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.resetServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }
    }

    private void ApplyServiceHealthTestMode()
    {
        ServiceHealthTestMode mode = this.currentSettings.ServiceHealthTestMode;
        if (mode == ServiceHealthTestMode.Off)
        {
            return;
        }

        ServiceHealthState state = ConvertServiceHealthTestMode(mode);
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = mode != ServiceHealthTestMode.Offline;
            this.radarServiceHealth = state;
            this.codexServiceHealth = state;
            this.resetServiceHealth = state;
        }
    }

    private void ResetServiceHealthAfterTestMode()
    {
        bool networkAvailable = IsNetworkAvailable();
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkRefreshRequested = false;
            this.serviceNetworkAvailable = networkAvailable;
            this.radarServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
            this.codexServiceHealth = networkAvailable
                ? (this.quotaSourceKnown ? ServiceHealthState.Normal : ServiceHealthState.Unavailable)
                : ServiceHealthState.Offline;
            this.resetServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
        }

        lock (this.codexResetStatusLock)
        {
            this.nextCodexResetStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
            this.codexResetStatusInferredUntilUtc = DateTime.MinValue;
        }

        lock (this.codexRadarStatusLock)
        {
            this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(4.0);
            this.pendingCodexRadarOpenedRefreshLocal = DateTime.MinValue;
            this.pendingCodexRadarOpenedEventLocal = DateTime.MinValue;
            this.pendingCodexRadarClosedRefreshLocal = DateTime.MinValue;
            this.pendingCodexRadarClosedEventLocal = DateTime.MinValue;
        }
    }

    private static ServiceHealthState ConvertServiceHealthTestMode(ServiceHealthTestMode mode)
    {
        if (mode == ServiceHealthTestMode.Normal)
        {
            return ServiceHealthState.Normal;
        }

        if (mode == ServiceHealthTestMode.Offline)
        {
            return ServiceHealthState.Offline;
        }

        if (mode == ServiceHealthTestMode.Unavailable)
        {
            return ServiceHealthState.Unavailable;
        }

        if (mode == ServiceHealthTestMode.Unreachable)
        {
            return ServiceHealthState.Unreachable;
        }

        return ServiceHealthState.Unknown;
    }

    private static bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    private TimeSpan GetCodexResetRefreshInterval()
    {
        return TimeSpan.FromMinutes(15.0);
    }

    private TimeSpan GetCodexRadarRefreshInterval(bool radarOpen)
    {
        if (radarOpen)
        {
            return TimeSpan.FromMinutes(5.0);
        }

        return TimeSpan.FromMinutes(10.0);
    }

    private TimeSpan GetCodexWebRetryDelay()
    {
        return TimeSpan.FromMinutes(2.0);
    }

    private bool IsRadarOpenForRefresh()
    {
        lock (this.codexRadarStatusLock)
        {
            return this.codexRadarSnapshot != null && this.codexRadarSnapshot.State == CodexRadarState.Open;
        }
    }

    private bool IsCodexResetStatusInferredYes(DateTime nowUtc)
    {
        lock (this.codexResetStatusLock)
        {
            return this.codexResetStatusInferredUntilUtc != DateTime.MinValue &&
                nowUtc < this.codexResetStatusInferredUntilUtc;
        }
    }

    private void InferCodexResetStatusYes(DateTime detectedUtc)
    {
        lock (this.codexResetStatusLock)
        {
            this.codexResetStatusKnown = true;
            this.codexResetStatusYes = true;
            this.codexResetStatusInferredUntilUtc = detectedUtc.AddSeconds(CodexResetInferredAfterResetSeconds);
            this.nextCodexResetStatusRefreshUtc = this.codexResetStatusInferredUntilUtc;
        }

        SetResetServiceHealth(ServiceHealthState.Normal);
    }

    private void RefreshCodexResetStatusIfNeeded()
    {
        if (!IsServiceNetworkAvailable())
        {
            SetResetServiceHealth(ServiceHealthState.Offline);
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (IsRadarOpenForRefresh())
        {
            lock (this.codexResetStatusLock)
            {
                this.codexResetStatusKnown = true;
                this.codexResetStatusYes = false;
                this.nextCodexResetStatusRefreshUtc = nowUtc + GetCodexRadarRefreshInterval(true);
            }

            SetResetServiceHealth(ServiceHealthState.Normal);
            return;
        }

        if (IsCodexResetStatusInferredYes(nowUtc))
        {
            SetResetServiceHealth(ServiceHealthState.Normal);
            return;
        }

        bool shouldStart = false;
        bool forceRefresh = ShouldForceServiceHealthRefresh(this.resetServiceHealth);
        lock (this.codexResetStatusLock)
        {
            // The running flag is the single-flight boundary for this endpoint.
            bool scheduledRefreshDue = this.nextCodexResetStatusRefreshUtc == DateTime.MinValue ||
                nowUtc >= this.nextCodexResetStatusRefreshUtc;
            if (!this.codexResetStatusRequestRunning &&
                (scheduledRefreshDue || forceRefresh))
            {
                this.codexResetStatusRequestRunning = true;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        Task.Run((Action)delegate
        {
            bool yes;
            bool known = false;
            ServiceHealthState health = ServiceHealthState.Unknown;
            try
            {
                known = TryReadCodexResetStatus(out yes, out health);
            }
            catch (Exception ex)
            {
                yes = false;
                health = ServiceHealthState.Unreachable;
                Program.LogException(ex);
            }

            lock (this.codexResetStatusLock)
            {
                if (known)
                {
                    this.codexResetStatusKnown = true;
                    this.codexResetStatusYes = yes;
                }

                this.nextCodexResetStatusRefreshUtc = DateTime.UtcNow +
                    (health == ServiceHealthState.Normal ? GetCodexResetRefreshInterval() : GetCodexWebRetryDelay());
                this.codexResetStatusRequestRunning = false;
            }
            SetResetServiceHealth(health);

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
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

    private void RefreshCodexRadarStatusIfNeeded()
    {
        if (this.currentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            return;
        }

        if (!IsServiceNetworkAvailable())
        {
            SetRadarServiceHealth(ServiceHealthState.Offline);
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        DateTime nowLocal = DateTime.Now;
        bool shouldStart = false;
        bool forceRefresh = ShouldForceServiceHealthRefresh(this.radarServiceHealth);
        lock (this.codexRadarStatusLock)
        {
            // Scheduled and event-boundary refreshes share one request slot.
            bool scheduledRefreshDue = this.nextCodexRadarStatusRefreshUtc == DateTime.MinValue ||
                nowUtc >= this.nextCodexRadarStatusRefreshUtc;
            bool boundaryRefreshDue = IsCodexRadarBoundaryRefreshDue(nowLocal);
            if (!this.codexRadarStatusRequestRunning &&
                (scheduledRefreshDue ||
                    boundaryRefreshDue ||
                    forceRefresh))
            {
                this.codexRadarStatusRequestRunning = true;
                if (boundaryRefreshDue)
                {
                    ConsumeDueCodexRadarBoundaryRefresh(nowLocal);
                }

                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        Task.Run((Action)delegate
        {
            CodexRadarSnapshot snapshot;
            bool known = false;
            bool closedFromOpen = false;
            ServiceHealthState health = ServiceHealthState.Unknown;
            try
            {
                known = TryReadCodexRadarStatus(out snapshot, out health);
            }
            catch (Exception ex)
            {
                snapshot = null;
                health = ServiceHealthState.Unreachable;
                Program.LogException(ex);
            }

            lock (this.codexRadarStatusLock)
            {
                if (known && snapshot != null)
                {
                    closedFromOpen = this.codexRadarSnapshot != null &&
                        this.codexRadarSnapshot.State == CodexRadarState.Open &&
                        snapshot.State != CodexRadarState.Open;
                    PreserveCodexModelIqSnapshot(snapshot, this.codexRadarSnapshot);
                    this.codexRadarSnapshot = snapshot;
                    ScheduleCodexRadarBoundaryRefreshes(snapshot, DateTime.Now);
                }

                this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow +
                    (health == ServiceHealthState.Normal
                        ? GetCodexRadarRefreshInterval(snapshot != null && snapshot.State == CodexRadarState.Open)
                        : GetCodexWebRetryDelay());
                this.codexRadarStatusRequestRunning = false;
            }
            if (closedFromOpen)
            {
                InferCodexResetStatusYes(DateTime.UtcNow);
            }

            SetRadarServiceHealth(health);

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            if (known && snapshot != null)
                            {
                                HandleCodexRadarResetEvent(snapshot);
                                HandleCodexRadarOpenEvent(snapshot);
                            }

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

    private static void PreserveCodexModelIqSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || target.ModelIqKnown || !source.ModelIqKnown)
        {
            return;
        }

        // current.json may temporarily omit model_iq; preserve the last known IQ fields then.
        CopyCodexModelIqSnapshot(target, source);
    }

    private static void CopyCodexModelIqSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || !source.ModelIqKnown)
        {
            return;
        }

        target.ModelIqStatus = source.ModelIqStatus;
        target.ModelIqPassRatePercent = source.ModelIqPassRatePercent;
        target.ModelIqPassed = source.ModelIqPassed;
        target.ModelIqValidTasks = source.ModelIqValidTasks;
        target.ModelIqTokenEfficiencyPercent = source.ModelIqTokenEfficiencyPercent;
        target.ModelIqTimeEfficiencyPercent = source.ModelIqTimeEfficiencyPercent;
        target.ModelIqEfficiencyPassed = source.ModelIqEfficiencyPassed;
        target.ModelIqEfficiencyTotalTokens = source.ModelIqEfficiencyTotalTokens;
        target.ModelIqEfficiencySerialSeconds = source.ModelIqEfficiencySerialSeconds;
        target.ModelIqPassedKnown = source.ModelIqPassedKnown;
        target.ModelIqEfficiencyInputKnown = source.ModelIqEfficiencyInputKnown;
        target.ModelIqEfficiencyKnown = source.ModelIqEfficiencyKnown;
        target.ModelIqRefreshedAtLocal = source.ModelIqRefreshedAtLocal;
        target.ModelIqDataDateLocal = source.ModelIqDataDateLocal;
        target.ModelIqRefreshedAtKnown = source.ModelIqRefreshedAtKnown;
        target.ModelIqDataDateKnown = source.ModelIqDataDateKnown;
        target.ModelIqKnown = source.ModelIqKnown;
    }

    private bool IsCodexRadarBoundaryRefreshDue(DateTime nowLocal)
    {
        return (this.pendingCodexRadarOpenedRefreshLocal != DateTime.MinValue &&
                nowLocal >= this.pendingCodexRadarOpenedRefreshLocal) ||
            (this.pendingCodexRadarClosedRefreshLocal != DateTime.MinValue &&
                nowLocal >= this.pendingCodexRadarClosedRefreshLocal);
    }

    private void ConsumeDueCodexRadarBoundaryRefresh(DateTime nowLocal)
    {
        if (this.pendingCodexRadarOpenedRefreshLocal != DateTime.MinValue &&
            nowLocal >= this.pendingCodexRadarOpenedRefreshLocal)
        {
            this.pendingCodexRadarOpenedRefreshLocal = DateTime.MinValue;
        }

        if (this.pendingCodexRadarClosedRefreshLocal != DateTime.MinValue &&
            nowLocal >= this.pendingCodexRadarClosedRefreshLocal)
        {
            this.pendingCodexRadarClosedRefreshLocal = DateTime.MinValue;
        }
    }

    private void ScheduleCodexRadarBoundaryRefreshes(CodexRadarSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null)
        {
            return;
        }

        ScheduleCodexRadarBoundaryRefresh(
            snapshot.OpenedAtKnown,
            snapshot.OpenedAtLocal,
            nowLocal,
            ref this.pendingCodexRadarOpenedEventLocal,
            ref this.pendingCodexRadarOpenedRefreshLocal);
        ScheduleCodexRadarBoundaryRefresh(
            snapshot.ClosedAtKnown,
            snapshot.ClosedAtLocal,
            nowLocal,
            ref this.pendingCodexRadarClosedEventLocal,
            ref this.pendingCodexRadarClosedRefreshLocal);
    }

    private static void ScheduleCodexRadarBoundaryRefresh(
        bool eventKnown,
        DateTime eventLocal,
        DateTime nowLocal,
        ref DateTime pendingEventLocal,
        ref DateTime pendingRefreshLocal)
    {
        if (!eventKnown || eventLocal == DateTime.MinValue || pendingEventLocal == eventLocal)
        {
            return;
        }

        pendingEventLocal = eventLocal;
        // Give the remote JSON a short settling window, then refresh this boundary exactly once.
        DateTime refreshLocal = eventLocal.AddSeconds(10.0);
        pendingRefreshLocal = refreshLocal > nowLocal ? refreshLocal : DateTime.MinValue;
    }

    private static bool TryReadCodexResetStatus(out bool isYes, out ServiceHealthState health)
    {
        isYes = false;
        health = ServiceHealthState.Unreachable;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        string url = CodexResetStatusUrl + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = CodexResetStatusTimeoutMs;
        request.ReadWriteTimeout = CodexResetStatusTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    health = ServiceHealthState.Unavailable;
                    return false;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        health = ServiceHealthState.Unavailable;
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        bool parsed = TryParseCodexResetStatus(content, out isYes);
                        health = parsed ? ServiceHealthState.Normal : ServiceHealthState.Unavailable;
                        return parsed;
                    }
                }
            }
        }
        catch (WebException ex)
        {
            health = ClassifyWebException(ex);
            return false;
        }
        catch
        {
            health = ServiceHealthState.Unreachable;
            return false;
        }
    }

    private static bool TryReadCodexRadarStatus(out CodexRadarSnapshot snapshot, out ServiceHealthState health)
    {
        snapshot = null;
        health = ServiceHealthState.Unreachable;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        string url = CodexRadarStatusUrl + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = CodexRadarStatusTimeoutMs;
        request.ReadWriteTimeout = CodexRadarStatusTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    health = ServiceHealthState.Unavailable;
                    return false;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        health = ServiceHealthState.Unavailable;
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        bool parsed = TryParseCodexRadarStatus(content, out snapshot);
                        health = parsed ? ServiceHealthState.Normal : ServiceHealthState.Unavailable;
                        return parsed;
                    }
                }
            }
        }
        catch (WebException ex)
        {
            health = ClassifyWebException(ex);
            return false;
        }
        catch
        {
            health = ServiceHealthState.Unreachable;
            return false;
        }
    }

    private static ServiceHealthState ClassifyWebException(WebException ex)
    {
        if (ex != null &&
            ex.Status == WebExceptionStatus.ProtocolError &&
            ex.Response != null)
        {
            return ServiceHealthState.Unavailable;
        }

        return ServiceHealthState.Unreachable;
    }

    private static bool TryParseCodexRadarStatus(string content, out CodexRadarSnapshot snapshot)
    {
        snapshot = null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (root == null)
            {
                return false;
            }

            snapshot = CodexRadarSnapshot.CreateDefault();
            DateTime checkedAt;
            if (TryGetQuotaDate(root, "checked_at", out checkedAt) ||
                TryGetQuotaDate(root, "monitored_at", out checkedAt))
            {
                snapshot.CheckedAtLocal = checkedAt;
                snapshot.CheckedAtKnown = true;
            }

            Dictionary<string, object> currentWindow = GetQuotaObject(root, "current_window");
            Dictionary<string, object> lastWindow = GetQuotaObject(root, "last_window");
            snapshot.CurrentWindowId = GetQuotaString(currentWindow, "id");
            CodexRadarState state;
            bool windowOpen;
            if (TryGetJsonBool(root, "window_open", out windowOpen) && windowOpen)
            {
                state = CodexRadarState.Open;
            }
            else if (!TryParseCodexRadarState(GetQuotaString(currentWindow, "state"), out state) &&
                !TryParseCodexRadarState(GetQuotaString(root, "status"), out state))
            {
                state = CodexRadarState.None;
            }

            snapshot.State = state;
            DateTime openedAt;
            DateTime closedAt;
            if (currentWindow != null && TryGetQuotaDate(currentWindow, "opened_at", out openedAt))
            {
                snapshot.OpenedAtLocal = openedAt;
                snapshot.OpenedAtKnown = true;
            }

            if (currentWindow != null && TryGetQuotaDate(currentWindow, "closed_at", out closedAt))
            {
                snapshot.ClosedAtLocal = closedAt;
                snapshot.ClosedAtKnown = true;
            }

            if (lastWindow != null)
            {
                snapshot.LastWindowId = GetQuotaString(lastWindow, "id");
                if (!snapshot.OpenedAtKnown && TryGetQuotaDate(lastWindow, "opened_at", out openedAt))
                {
                    snapshot.OpenedAtLocal = openedAt;
                    snapshot.OpenedAtKnown = true;
                }

                DateTime lastWindowClosedAt;
                if (TryGetQuotaDate(lastWindow, "closed_at", out lastWindowClosedAt))
                {
                    snapshot.LastWindowClosedAtLocal = lastWindowClosedAt;
                    snapshot.LastWindowClosedAtKnown = true;
                    if (!snapshot.ClosedAtKnown)
                    {
                        snapshot.ClosedAtLocal = lastWindowClosedAt;
                        snapshot.ClosedAtKnown = true;
                    }
                }
            }

            ApplyCodexRadarPrediction(root, snapshot);
            Dictionary<string, object> modelIq = GetQuotaObject(root, "model_iq");
            if (TryApplyCodexModelIqStatus(modelIq, snapshot))
            {
                snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
                snapshot.ModelIqRefreshedAtKnown = true;
            }

            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static bool TryApplyCodexModelIqStatus(Dictionary<string, object> root, CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return false;
        }

        try
        {
            Dictionary<string, object> latest = GetQuotaObject(root, "latest") ?? root;
            DateTime dataDate;
            if (TryGetQuotaDate(latest, "date", out dataDate) ||
                TryGetQuotaDate(root, "date", out dataDate))
            {
                snapshot.ModelIqDataDateLocal = dataDate.Date;
                snapshot.ModelIqDataDateKnown = true;
            }

            string status = GetQuotaString(latest, "status");
            if (string.IsNullOrEmpty(status))
            {
                status = GetQuotaString(root, "status");
            }

            double passRate;
            bool hasPassRate =
                TryGetQuotaNumber(latest, "pass_rate", out passRate) ||
                TryGetQuotaNumber(latest, "passrate", out passRate) ||
                TryGetQuotaNumber(latest, "passRate", out passRate);
            double passed;
            double validTasks;
            bool hasPassed = TryGetQuotaNumber(latest, "passed", out passed);
            bool hasValidTasks =
                TryGetQuotaNumber(latest, "valid_tasks", out validTasks) ||
                TryGetQuotaNumber(latest, "validTasks", out validTasks) ||
                TryGetQuotaNumber(latest, "tasks", out validTasks);
            if (!hasPassRate)
            {
                if (hasPassed && hasValidTasks && validTasks > 0.0)
                {
                    passRate = passed / validTasks;
                    hasPassRate = true;
                }
            }

            if (string.IsNullOrEmpty(status) && !hasPassRate)
            {
                return false;
            }

            snapshot.ModelIqStatus = NormalizeCodexModelIqStatus(status);
            if (hasPassRate)
            {
                snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(passRate);
            }

            if (hasPassed)
            {
                int validTaskCount = hasValidTasks && validTasks > 0.0
                    ? (int)Math.Round(validTasks, MidpointRounding.AwayFromZero)
                    : CodexModelIqNominalTasks;
                validTaskCount = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTaskCount));
                snapshot.ModelIqValidTasks = validTaskCount;
                snapshot.ModelIqPassed = Math.Max(0, Math.Min(validTaskCount, (int)Math.Round(passed, MidpointRounding.AwayFromZero)));
                snapshot.ModelIqPassedKnown = true;
            }
            else if (hasPassRate)
            {
                int validTaskCount = hasValidTasks && validTasks > 0.0
                    ? (int)Math.Round(validTasks, MidpointRounding.AwayFromZero)
                    : CodexModelIqNominalTasks;
                validTaskCount = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTaskCount));
                snapshot.ModelIqValidTasks = validTaskCount;
                snapshot.ModelIqPassed = Math.Max(0, Math.Min(validTaskCount, (int)Math.Round(NormalizePassRatePercent(passRate) / 100.0 * validTaskCount, MidpointRounding.AwayFromZero)));
                snapshot.ModelIqPassedKnown = true;
            }

            ApplyCodexModelIqEfficiency(root, latest, snapshot);
            snapshot.ModelIqKnown = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyCodexModelIqEfficiency(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        CodexRadarSnapshot snapshot)
    {
        double currentPassed;
        double currentTotalTokens;
        double currentSerialSeconds;
        if (!TryReadModelIqEfficiencyInput(latest, out currentPassed, out currentTotalTokens, out currentSerialSeconds))
        {
            return;
        }

        snapshot.ModelIqEfficiencyPassed = currentPassed;
        snapshot.ModelIqEfficiencyTotalTokens = currentTotalTokens;
        snapshot.ModelIqEfficiencySerialSeconds = currentSerialSeconds;
        snapshot.ModelIqEfficiencyInputKnown = true;

        Dictionary<string, object> baseline =
            GetFirstQuotaObjectFromArray(root, "history") ??
            GetFirstQuotaObjectFromArray(root, "recent_days");
        if (baseline == null)
        {
            return;
        }

        double baselinePassed;
        double baselineTotalTokens;
        double baselineSerialSeconds;
        if (!TryReadModelIqEfficiencyInput(baseline, out baselinePassed, out baselineTotalTokens, out baselineSerialSeconds))
        {
            return;
        }

        double baselineTokenRate = baselinePassed / baselineTotalTokens;
        double baselineTimeRate = baselinePassed / baselineSerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentTotalTokens) / baselineTokenRate * 100.0, MidpointRounding.AwayFromZero));
        snapshot.ModelIqTimeEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentSerialSeconds) / baselineTimeRate * 100.0, MidpointRounding.AwayFromZero));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private static bool TryReadModelIqEfficiencyInput(
        Dictionary<string, object> values,
        out double passed,
        out double totalTokens,
        out double serialSeconds)
    {
        passed = 0.0;
        totalTokens = 0.0;
        serialSeconds = 0.0;
        if (values == null || !TryGetQuotaNumber(values, "passed", out passed) || passed <= 0.0)
        {
            return false;
        }

        if (!TryGetModelIqTotalTokens(values, out totalTokens) || totalTokens <= 0.0)
        {
            return false;
        }

        return (TryGetQuotaNumber(values, "serial_task_seconds", out serialSeconds) ||
                TryGetQuotaNumber(values, "serialTaskSeconds", out serialSeconds) ||
                TryGetQuotaNumber(values, "wall_seconds", out serialSeconds)) &&
            serialSeconds > 0.0;
    }

    private static bool TryGetModelIqTotalTokens(Dictionary<string, object> values, out double totalTokens)
    {
        totalTokens = 0.0;
        if (TryGetQuotaNumber(values, "total_tokens", out totalTokens) ||
            TryGetQuotaNumber(values, "totalTokens", out totalTokens) ||
            TryGetQuotaNumber(values, "n_total_tokens", out totalTokens))
        {
            return totalTokens > 0.0;
        }

        double inputTokens;
        double outputTokens;
        if (!TryGetQuotaNumber(values, "n_input_tokens", out inputTokens) &&
            !TryGetQuotaNumber(values, "input_tokens", out inputTokens))
        {
            return false;
        }

        if (!TryGetQuotaNumber(values, "n_output_tokens", out outputTokens) &&
            !TryGetQuotaNumber(values, "output_tokens", out outputTokens))
        {
            outputTokens = 0.0;
        }

        totalTokens = inputTokens + Math.Max(0.0, outputTokens);
        return totalTokens > 0.0;
    }

    private static bool TryParseCodexRadarState(string text, out CodexRadarState state)
    {
        state = CodexRadarState.None;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string normalized = text.Trim().ToLowerInvariant();
        if (normalized == "open" || normalized == "opened" || normalized == "active" || normalized == "running")
        {
            state = CodexRadarState.Open;
            return true;
        }

        if (normalized == "closed" || normalized == "close" || normalized == "completed")
        {
            state = CodexRadarState.Closed;
            return true;
        }

        if (normalized == "none" || normalized == "no" || normalized == "inactive" || normalized == "wait")
        {
            state = CodexRadarState.None;
            return true;
        }

        return false;
    }

    private static void ApplyCodexRadarPrediction(Dictionary<string, object> root, CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return;
        }

        Dictionary<string, object> prediction = GetQuotaObject(root, "prediction") ?? root;
        string level = GetQuotaString(prediction, "level");
        double probability24;
        double probability48;
        bool has24 = TryGetQuotaNumber(prediction, "probability_24h", out probability24);
        bool has48 = TryGetQuotaNumber(prediction, "probability_48h", out probability48);
        if (string.IsNullOrEmpty(level) && !has24 && !has48)
        {
            return;
        }

        snapshot.PredictionLevel = NormalizeCodexRadarPredictionLevel(level);
        if (has24)
        {
            snapshot.Probability24Percent = NormalizeProbabilityPercent(probability24);
        }

        if (has48)
        {
            snapshot.Probability48Percent = NormalizeProbabilityPercent(probability48);
        }

        snapshot.PredictionKnown = true;
    }

    private static string NormalizeCodexRadarPredictionLevel(string level)
    {
        if (string.Equals(level, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        if (string.Equals(level, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        if (string.Equals(level, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        return "invalid";
    }

    private static string NormalizeCodexModelIqStatus(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return "invalid";
        }

        string normalized = status.Trim().ToLowerInvariant();
        if (normalized == "green" ||
            normalized == "ok" ||
            normalized == "normal" ||
            normalized == "stable")
        {
            return "green";
        }

        if (normalized == "yellow" ||
            normalized == "amber" ||
            normalized == "warning")
        {
            return "yellow";
        }

        if (normalized == "orange")
        {
            return "orange";
        }

        if (normalized == "red" ||
            normalized == "danger" ||
            normalized == "critical")
        {
            return "red";
        }

        return "invalid";
    }

    private static int NormalizeProbabilityPercent(double value)
    {
        if (value <= 1.0)
        {
            value *= 100.0;
        }

        return ClampPercent((int)Math.Round(value));
    }

    private static int NormalizePassRatePercent(double value)
    {
        if (value <= 1.0)
        {
            value *= 100.0;
        }

        return ClampPercent((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static bool TryParseCodexResetStatus(string content, out bool isYes)
    {
        isYes = false;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(content) as Dictionary<string, object>;
            string state = GetQuotaString(root, "state");
            if (!string.IsNullOrEmpty(state))
            {
                isYes = !string.Equals(state, "no", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }
        catch
        {
        }

        if (content.IndexOf("\"state\":\"yes\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
            content.IndexOf("data-state=\"yes\"", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            isYes = true;
            return true;
        }

        if (content.IndexOf("\"state\":\"no\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
            content.IndexOf("data-state=\"no\"", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            isYes = false;
            return true;
        }

        return false;
    }

    private double GetQuotaActiveRefreshSeconds()
    {
        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.Smooth)
        {
            return 10.0;
        }

        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.BatterySaver)
        {
            return 30.0;
        }

        return 15.0;
    }

    private double GetQuotaProcessCheckSeconds()
    {
        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.Smooth)
        {
            return 3.0;
        }

        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.BatterySaver)
        {
            return 10.0;
        }

        return 5.0;
    }

    private TimeSpan GetQuotaInactiveRefreshInterval()
    {
        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.Smooth)
        {
            return TimeSpan.FromMinutes(10.0);
        }

        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.BatterySaver)
        {
            return TimeSpan.FromMinutes(60.0);
        }

        return TimeSpan.FromMinutes(20.0);
    }

    private bool UpdateCodexProcessRunningStatus(DateTime nowUtc, out bool changed)
    {
        if (this.lastQuotaProcessCheckUtc != DateTime.MinValue &&
            (nowUtc - this.lastQuotaProcessCheckUtc).TotalSeconds < GetQuotaProcessCheckSeconds())
        {
            changed = false;
            return this.quotaCodexProcessRunning;
        }

        bool running = IsCodexProcessRunning();
        changed = running != this.quotaCodexProcessRunning;
        this.quotaCodexProcessRunning = running;
        this.lastQuotaProcessCheckUtc = nowUtc;
        return running;
    }

    private static bool IsCodexProcessRunning()
    {
        Process[] processes = null;
        try
        {
            // Query only the required executable name instead of opening every process on the machine.
            processes = Process.GetProcessesByName("codex");
            return processes.Length > 0;
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

        return false;
    }

    private bool IsInactiveQuotaRefreshDue(DateTime nowUtc)
    {
        return this.nextQuotaInactiveRefreshUtc == DateTime.MinValue ||
            nowUtc >= this.nextQuotaInactiveRefreshUtc;
    }

    private void MarkInactiveQuotaRefresh(DateTime nowUtc)
    {
        this.nextQuotaInactiveRefreshUtc = nowUtc + GetQuotaInactiveRefreshInterval();
    }

    private static bool IsQuotaResetDue(CodexQuotaSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null)
        {
            return true;
        }

        return (snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal) ||
            (snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal);
    }

    private void HandleCodexRadarResetEvent(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null ||
            !snapshot.LastWindowClosedAtKnown ||
            snapshot.LastWindowClosedAtLocal == DateTime.MinValue)
        {
            return;
        }

        DateTime closedUtc = snapshot.LastWindowClosedAtLocal.ToUniversalTime();
        string eventId = (snapshot.LastWindowId ?? string.Empty).Trim();
        bool stateChanged = false;
        bool isNewReset = false;
        lock (this.quotaResetStateLock)
        {
            // The first historical item establishes a baseline. Only a strictly newer close time
            // is allowed to restore quota and notify, including after a process restart.
            if (!this.radarResetBaselineInitialized)
            {
                this.radarResetBaselineInitialized = true;
                this.lastRadarResetEventId = eventId;
                this.lastRadarResetEventClosedUtc = closedUtc;
                stateChanged = true;
            }
            else if (closedUtc > this.lastRadarResetEventClosedUtc)
            {
                this.lastRadarResetEventId = eventId;
                this.lastRadarResetEventClosedUtc = closedUtc;
                stateChanged = true;
                isNewReset = true;
            }
            else if (closedUtc == this.lastRadarResetEventClosedUtc &&
                !string.Equals(eventId, this.lastRadarResetEventId, StringComparison.Ordinal))
            {
                this.lastRadarResetEventId = eventId;
                stateChanged = true;
            }
        }

        if (isNewReset)
        {
            DateTime detectedUtc = DateTime.UtcNow;
            bool protectionSaved = ActivateQuotaResetProtections(
                true,
                detectedUtc,
                true,
                detectedUtc,
                "CodexRadar new reset event",
                true);
            if (!protectionSaved)
            {
                SaveQuotaResetState();
            }

            ShowCodexNotification(
                "Codex 额外重置",
                "检测到新的重置记录，余额已恢复至 100。",
                ToolTipIcon.Warning);
            InferCodexResetStatusYes(detectedUtc);
            this.lastQuotaRefreshUtc = DateTime.MinValue;
            return;
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
        }
    }

    private void HandleCodexRadarOpenEvent(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || snapshot.State != CodexRadarState.Open)
        {
            return;
        }

        string eventId = (snapshot.CurrentWindowId ?? string.Empty).Trim();
        DateTime openedUtc = snapshot.OpenedAtKnown
            ? snapshot.OpenedAtLocal.ToUniversalTime()
            : DateTime.MinValue;
        if (eventId.Length == 0 && openedUtc == DateTime.MinValue)
        {
            return;
        }

        bool stateChanged = false;
        bool isNewOpen = false;
        lock (this.quotaResetStateLock)
        {
            bool firstOpen = this.lastRadarOpenEventId.Length == 0 &&
                this.lastRadarOpenEventUtc == DateTime.MinValue;
            bool newerOpen = openedUtc != DateTime.MinValue && openedUtc > this.lastRadarOpenEventUtc;
            bool differentIdWithoutTime = openedUtc == DateTime.MinValue &&
                eventId.Length > 0 &&
                !string.Equals(eventId, this.lastRadarOpenEventId, StringComparison.Ordinal);
            if (firstOpen || newerOpen || differentIdWithoutTime)
            {
                this.lastRadarOpenEventId = eventId;
                this.lastRadarOpenEventUtc = openedUtc;
                stateChanged = true;
                isNewOpen = true;
            }
            else if (openedUtc == this.lastRadarOpenEventUtc &&
                eventId.Length > 0 &&
                !string.Equals(eventId, this.lastRadarOpenEventId, StringComparison.Ordinal))
            {
                this.lastRadarOpenEventId = eventId;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
        }

        if (isNewOpen)
        {
            ShowCodexNotification(
                "Codex 速蹬窗口开启",
                "检测到速蹬窗口已开启。",
                ToolTipIcon.Info);
        }
    }

    private void ShowCodexNotification(string title, string message, ToolTipIcon icon)
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

    private void ActivateDueQuotaResetProtections(CodexQuotaSnapshot snapshot, DateTime nowLocal, DateTime detectedUtc)
    {
        if (snapshot == null)
        {
            return;
        }

        bool fiveHourDue = snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal;
        bool weeklyDue = snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal;
        lock (this.quotaResetStateLock)
        {
            fiveHourDue = fiveHourDue && this.fiveHourQuotaProtectionUtc == DateTime.MinValue;
            weeklyDue = weeklyDue && this.weeklyQuotaProtectionUtc == DateTime.MinValue;
        }

        if (!fiveHourDue && !weeklyDue)
        {
            return;
        }

        ActivateQuotaResetProtections(
            fiveHourDue,
            detectedUtc,
            weeklyDue,
            detectedUtc,
            "local resets_at reached",
            false);
    }

    private bool ActivateQuotaResetProtections(
        bool protectFiveHour,
        DateTime fiveHourProtectionUtc,
        bool protectWeekly,
        DateTime weeklyProtectionUtc,
        string reason,
        bool gold)
    {
        bool stateChanged = false;
        lock (this.quotaResetStateLock)
        {
            if (protectFiveHour)
            {
                DateTime normalized = NormalizeStateUtc(fiveHourProtectionUtc);
                if (normalized == DateTime.MinValue)
                {
                    normalized = DateTime.UtcNow;
                }

                if (normalized > this.fiveHourQuotaProtectionUtc)
                {
                    this.fiveHourQuotaProtectionUtc = normalized;
                    this.fiveHourQuotaProtectionGold = gold;
                    stateChanged = true;
                }
                else if (gold && !this.fiveHourQuotaProtectionGold)
                {
                    this.fiveHourQuotaProtectionGold = true;
                    stateChanged = true;
                }
            }

            if (protectWeekly)
            {
                DateTime normalized = NormalizeStateUtc(weeklyProtectionUtc);
                if (normalized == DateTime.MinValue)
                {
                    normalized = DateTime.UtcNow;
                }

                if (normalized > this.weeklyQuotaProtectionUtc)
                {
                    this.weeklyQuotaProtectionUtc = normalized;
                    this.weeklyQuotaProtectionGold = gold;
                    stateChanged = true;
                }
                else if (gold && !this.weeklyQuotaProtectionGold)
                {
                    this.weeklyQuotaProtectionGold = true;
                    stateChanged = true;
                }
            }
        }

        if (this.quotaSnapshot == null)
        {
            this.quotaSnapshot = CodexQuotaSnapshot.CreateDefault();
        }

        if (protectFiveHour)
        {
            ForceFiveHourQuotaToFull(this.quotaSnapshot);
        }

        if (protectWeekly)
        {
            ForceWeeklyQuotaToFull(this.quotaSnapshot);
        }

        if (stateChanged)
        {
            Program.LogInfo("Quota reset protection activated. Reason=" + reason);
            SaveQuotaResetState();
        }

        return stateChanged;
    }

    private CodexQuotaSnapshot ApplyQuotaResetProtections(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            snapshot = CodexQuotaSnapshot.CreateDefault();
        }

        bool stateChanged = false;
        bool fiveHourReleased = false;
        bool weeklyReleased = false;
        lock (this.quotaResetStateLock)
        {
            if (this.fiveHourQuotaProtectionUtc != DateTime.MinValue)
            {
                if (IsQuotaProtectionReleaseSample(
                    snapshot,
                    this.fiveHourQuotaProtectionUtc,
                    snapshot.FiveHourResetKnown,
                    snapshot.FiveHourResetLocal))
                {
                    this.fiveHourQuotaProtectionUtc = DateTime.MinValue;
                    this.fiveHourQuotaProtectionGold = false;
                    stateChanged = true;
                    fiveHourReleased = true;
                }
                else
                {
                    ForceFiveHourQuotaToFull(snapshot);
                }
            }

            if (this.weeklyQuotaProtectionUtc != DateTime.MinValue)
            {
                if (IsQuotaProtectionReleaseSample(
                    snapshot,
                    this.weeklyQuotaProtectionUtc,
                    snapshot.WeeklyResetKnown,
                    snapshot.WeeklyResetLocal))
                {
                    this.weeklyQuotaProtectionUtc = DateTime.MinValue;
                    this.weeklyQuotaProtectionGold = false;
                    stateChanged = true;
                    weeklyReleased = true;
                }
                else
                {
                    ForceWeeklyQuotaToFull(snapshot);
                }
            }
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
            if (fiveHourReleased)
            {
                Program.LogInfo("Five-hour quota reset protection released by a newer quota sample.");
            }

            if (weeklyReleased)
            {
                Program.LogInfo("Weekly quota reset protection released by a newer quota sample.");
            }
        }

        return snapshot;
    }

    private static bool IsQuotaProtectionReleaseSample(
        CodexQuotaSnapshot snapshot,
        DateTime protectionUtc,
        bool resetKnown,
        DateTime resetLocal)
    {
        // Keep 100 visible until a post-protection sample proves the next quota window exists.
        return snapshot.SourceUpdatedKnown &&
            snapshot.SourceUpdatedUtc > protectionUtc &&
            (!resetKnown || resetLocal > DateTime.Now);
    }

    private static void ForceFiveHourQuotaToFull(CodexQuotaSnapshot snapshot)
    {
        snapshot.FiveHourPercent = 100;
        snapshot.FiveHourResetLocal = DateTime.MinValue;
        snapshot.FiveHourResetKnown = false;
    }

    private static void ForceWeeklyQuotaToFull(CodexQuotaSnapshot snapshot)
    {
        snapshot.WeeklyPercent = 100;
        snapshot.WeeklyResetLocal = DateTime.MinValue;
        snapshot.WeeklyResetKnown = false;
    }

    private static string QuotaResetStatePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "quota-reset-state.ini"); }
    }

    private void LoadQuotaResetState()
    {
        string path = QuotaResetStatePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            lock (this.quotaResetStateLock)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    bool boolValue;
                    DateTime utcValue;
                    if (string.Equals(key, "RadarBaselineInitialized", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.radarResetBaselineInitialized = boolValue;
                    }
                    else if (string.Equals(key, "LastRadarEventId", StringComparison.OrdinalIgnoreCase))
                    {
                        this.lastRadarResetEventId = value;
                    }
                    else if (string.Equals(key, "LastRadarEventClosedUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.lastRadarResetEventClosedUtc = utcValue;
                    }
                    else if (string.Equals(key, "LastRadarOpenEventId", StringComparison.OrdinalIgnoreCase))
                    {
                        this.lastRadarOpenEventId = value;
                    }
                    else if (string.Equals(key, "LastRadarOpenEventUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.lastRadarOpenEventUtc = utcValue;
                    }
                    else if (string.Equals(key, "FiveHourProtectionUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.fiveHourQuotaProtectionUtc = utcValue;
                    }
                    else if (string.Equals(key, "WeeklyProtectionUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.weeklyQuotaProtectionUtc = utcValue;
                    }
                    else if (string.Equals(key, "FiveHourProtectionGold", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.fiveHourQuotaProtectionGold = boolValue;
                    }
                    else if (string.Equals(key, "WeeklyProtectionGold", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.weeklyQuotaProtectionGold = boolValue;
                    }
                }

                if (this.lastRadarResetEventClosedUtc == DateTime.MinValue)
                {
                    this.radarResetBaselineInitialized = false;
                    this.lastRadarResetEventId = string.Empty;
                }

                if (this.fiveHourQuotaProtectionUtc == DateTime.MinValue)
                {
                    this.fiveHourQuotaProtectionGold = false;
                }

                if (this.weeklyQuotaProtectionUtc == DateTime.MinValue)
                {
                    this.weeklyQuotaProtectionGold = false;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void SaveQuotaResetState()
    {
        try
        {
            lock (this.quotaResetStateLock)
            {
                Directory.CreateDirectory(Logger.DirectoryPath);
                string eventId = (this.lastRadarResetEventId ?? string.Empty)
                    .Replace("\r", string.Empty)
                    .Replace("\n", string.Empty);
                string openEventId = (this.lastRadarOpenEventId ?? string.Empty)
                    .Replace("\r", string.Empty)
                    .Replace("\n", string.Empty);
                File.WriteAllLines(
                    QuotaResetStatePath,
                    new[]
                    {
                        "Version=2",
                        "RadarBaselineInitialized=" + this.radarResetBaselineInitialized.ToString(CultureInfo.InvariantCulture),
                        "LastRadarEventId=" + eventId,
                        "LastRadarEventClosedUtc=" + FormatStateUtc(this.lastRadarResetEventClosedUtc),
                        "LastRadarOpenEventId=" + openEventId,
                        "LastRadarOpenEventUtc=" + FormatStateUtc(this.lastRadarOpenEventUtc),
                        "FiveHourProtectionUtc=" + FormatStateUtc(this.fiveHourQuotaProtectionUtc),
                        "WeeklyProtectionUtc=" + FormatStateUtc(this.weeklyQuotaProtectionUtc),
                        "FiveHourProtectionGold=" + this.fiveHourQuotaProtectionGold.ToString(CultureInfo.InvariantCulture),
                        "WeeklyProtectionGold=" + this.weeklyQuotaProtectionGold.ToString(CultureInfo.InvariantCulture)
                    },
                    Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static string FormatStateUtc(DateTime value)
    {
        DateTime normalized = NormalizeStateUtc(value);
        return normalized == DateTime.MinValue
            ? string.Empty
            : normalized.ToString("o", CultureInfo.InvariantCulture);
    }

    private static bool TryParseStateUtc(string text, out DateTime value)
    {
        value = DateTime.MinValue;
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed))
        {
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }

    private static DateTime NormalizeStateUtc(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private CodexQuotaSnapshot ReadQuotaSnapshot(out bool sourceKnown)
    {
        CodexQuotaSnapshot snapshot;
        if (TryReadCodexSessionQuota(out snapshot))
        {
            sourceKnown = true;
            return NormalizeQuotaSnapshot(snapshot);
        }

        if (TryReadQuotaIniSnapshot(out snapshot))
        {
            sourceKnown = true;
            return NormalizeQuotaSnapshot(snapshot);
        }

        sourceKnown = false;
        return CodexQuotaSnapshot.CreateDefault();
    }

    private static CodexQuotaSnapshot NormalizeQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return CodexQuotaSnapshot.CreateDefault();
        }

        snapshot.FiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
        snapshot.WeeklyPercent = ClampPercent(snapshot.WeeklyPercent);
        return snapshot;
    }

    private bool TryReadCodexSessionQuota(out CodexQuotaSnapshot snapshot)
    {
        snapshot = null;
        string sessionsPath = this.quotaSessionsPath;
        if (string.IsNullOrEmpty(sessionsPath))
        {
            return false;
        }

        if (!Directory.Exists(sessionsPath))
        {
            return false;
        }

        // Clear the watcher hint before scanning so an append that happens during the scan
        // sets it again and is observed on the next quota refresh.
        bool filesChanged = this.quotaSessionWatcher == null ||
            Interlocked.Exchange(ref this.quotaSessionFilesChanged, 0) != 0;
        if (!filesChanged)
        {
            lock (codexQuotaSnapshotCacheLock)
            {
                // The watcher is only an invalidation hint; metadata still verifies append-only changes.
                if (codexQuotaSnapshotCache != null &&
                    File.Exists(codexQuotaSnapshotCachePath) &&
                    codexQuotaSnapshotCacheWriteUtc == SafeGetLastWriteTimeUtc(codexQuotaSnapshotCachePath) &&
                    codexQuotaSnapshotCacheLength == SafeGetFileLength(codexQuotaSnapshotCachePath))
                {
                    snapshot = codexQuotaSnapshotCache.Clone();
                    return true;
                }
            }
        }

        List<string> rolloutFiles = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name != null && name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                {
                    rolloutFiles.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
            Program.LogException(ex);
            return false;
        }

        if (rolloutFiles.Count == 0)
        {
            return false;
        }

        rolloutFiles.Sort(delegate(string left, string right)
        {
            return SafeGetLastWriteTimeUtc(right).CompareTo(SafeGetLastWriteTimeUtc(left));
        });

        string newestPath = rolloutFiles[0];
        DateTime newestWriteUtc = SafeGetLastWriteTimeUtc(newestPath);
        long newestLength = SafeGetFileLength(newestPath);
        lock (codexQuotaSnapshotCacheLock)
        {
            // Length participates in the key because the active JSONL file is append-only.
            if (codexQuotaSnapshotCache != null &&
                string.Equals(codexQuotaSnapshotCachePath, newestPath, StringComparison.OrdinalIgnoreCase) &&
                codexQuotaSnapshotCacheWriteUtc == newestWriteUtc &&
                codexQuotaSnapshotCacheLength == newestLength)
            {
                snapshot = codexQuotaSnapshotCache.Clone();
                return true;
            }
        }

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;

        CodexQuotaEvent latestEvent = null;
        int count = Math.Min(rolloutFiles.Count, MaxQuotaRolloutFilesToScan);
        for (int i = 0; i < count; i++)
        {
            string file = rolloutFiles[i];
            if (latestEvent != null && SafeGetLastWriteTimeUtc(file) < latestEvent.UpdatedUtc)
            {
                break;
            }

            CodexQuotaEvent quotaEvent;
            if (TryParseLatestQuotaEventFromFile(file, serializer, out quotaEvent) &&
                (latestEvent == null || quotaEvent.UpdatedUtc > latestEvent.UpdatedUtc))
            {
                latestEvent = quotaEvent;
            }
        }

        if (latestEvent == null)
        {
            return false;
        }

        snapshot = latestEvent.Snapshot;
        if (snapshot != null)
        {
            snapshot.SourceUpdatedUtc = latestEvent.UpdatedUtc;
            snapshot.SourceUpdatedKnown = latestEvent.UpdatedUtc != DateTime.MinValue;
        }

        if (snapshot != null)
        {
            lock (codexQuotaSnapshotCacheLock)
            {
                codexQuotaSnapshotCachePath = newestPath;
                codexQuotaSnapshotCacheWriteUtc = newestWriteUtc;
                codexQuotaSnapshotCacheLength = newestLength;
                codexQuotaSnapshotCache = snapshot.Clone();
            }

        }

        return snapshot != null;
    }

    private void InitializeQuotaSessionWatcher()
    {
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profilePath))
        {
            return;
        }

        this.quotaSessionsPath = Path.Combine(Path.Combine(profilePath, ".codex"), "sessions");
        if (!Directory.Exists(this.quotaSessionsPath))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new FileSystemWatcher(this.quotaSessionsPath, "rollout-*.jsonl");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size;
            watcher.Changed += OnQuotaSessionFileChanged;
            watcher.Created += OnQuotaSessionFileChanged;
            watcher.Deleted += OnQuotaSessionFileChanged;
            watcher.Renamed += OnQuotaSessionFileRenamed;
            watcher.Error += OnQuotaSessionWatcherError;
            watcher.EnableRaisingEvents = true;
            this.quotaSessionWatcher = watcher;
        }
        catch (Exception ex)
        {
            // Without a watcher the changed flag remains set, preserving the original polling behavior.
            Program.LogException(ex);
        }
    }

    private void OnQuotaSessionFileChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
    }

    private void OnQuotaSessionFileRenamed(object sender, RenamedEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
    }

    private void OnQuotaSessionWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
    }

    private void DisposeQuotaSessionWatcher()
    {
        FileSystemWatcher watcher = this.quotaSessionWatcher;
        this.quotaSessionWatcher = null;
        if (watcher == null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnQuotaSessionFileChanged;
        watcher.Created -= OnQuotaSessionFileChanged;
        watcher.Deleted -= OnQuotaSessionFileChanged;
        watcher.Renamed -= OnQuotaSessionFileRenamed;
        watcher.Error -= OnQuotaSessionWatcherError;
        watcher.Dispose();
    }

    private static bool TryParseLatestQuotaEventFromFile(string path, JavaScriptSerializer serializer, out CodexQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // Quota events are near the end of rollout files. Read backwards in bounded
                // chunks instead of deserializing the complete session history on every refresh.
                long offset = stream.Length;
                byte[] tail = new byte[0];
                while (offset > 0)
                {
                    int readSize = (int)Math.Min(QuotaTailChunkBytes, offset);
                    offset -= readSize;
                    stream.Seek(offset, SeekOrigin.Begin);

                    byte[] chunk = new byte[readSize];
                    int read = stream.Read(chunk, 0, readSize);
                    if (read <= 0)
                    {
                        continue;
                    }

                    byte[] expandedTail = new byte[read + tail.Length];
                    Buffer.BlockCopy(chunk, 0, expandedTail, 0, read);
                    if (tail.Length > 0)
                    {
                        Buffer.BlockCopy(tail, 0, expandedTail, read, tail.Length);
                    }

                    tail = expandedTail;
                    string text = Encoding.UTF8.GetString(tail, 0, tail.Length);
                    if (TryParseLatestQuotaEventFromText(text, path, serializer, out quotaEvent))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryParseLatestQuotaEventFromText(string text, string path, JavaScriptSerializer serializer, out CodexQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 ||
                line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                continue;
            }

            if (root == null ||
                !string.Equals(GetQuotaString(root, "type"), "event_msg", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> payload = GetQuotaObject(root, "payload");
            if (payload == null ||
                !string.Equals(GetQuotaString(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> rateLimits = GetQuotaObject(payload, "rate_limits");
            CodexQuotaSnapshot snapshot;
            if (rateLimits == null || !TryBuildQuotaSnapshot(rateLimits, out snapshot))
            {
                continue;
            }

            DateTime updatedLocal;
            DateTime updatedUtc = SafeGetLastWriteTimeUtc(path);
            if (TryGetQuotaDate(root, "timestamp", out updatedLocal))
            {
                updatedUtc = updatedLocal.ToUniversalTime();
            }
            else if (updatedUtc == DateTime.MinValue)
            {
                updatedUtc = DateTime.UtcNow;
            }

            quotaEvent = new CodexQuotaEvent();
            quotaEvent.Snapshot = snapshot;
            quotaEvent.UpdatedUtc = updatedUtc;
            return true;
        }

        return false;
    }

    private static bool TryBuildQuotaSnapshot(Dictionary<string, object> rateLimits, out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        bool found = false;
        found = ApplyQuotaSlot(rateLimits, "primary", snapshot) || found;
        found = ApplyQuotaSlot(rateLimits, "secondary", snapshot) || found;
        return found;
    }

    private static bool ApplyQuotaSlot(Dictionary<string, object> rateLimits, string key, CodexQuotaSnapshot snapshot)
    {
        Dictionary<string, object> slot = GetQuotaObject(rateLimits, key);
        if (slot == null)
        {
            return false;
        }

        double usedPercent;
        if (!TryGetQuotaNumber(slot, "used_percent", out usedPercent) &&
            !TryGetQuotaNumber(slot, "used_percentage", out usedPercent))
        {
            return false;
        }

        double windowMinutes;
        bool hasWindowMinutes = TryGetQuotaNumber(slot, "window_minutes", out windowMinutes);
        bool isFiveHour = string.Equals(key, "primary", StringComparison.OrdinalIgnoreCase);
        if (hasWindowMinutes)
        {
            isFiveHour = windowMinutes <= 300.0;
        }

        int remainingPercent = ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool hasReset = TryGetQuotaDate(slot, "resets_at", out resetLocal);
        if (isFiveHour)
        {
            snapshot.FiveHourPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.FiveHourResetLocal = resetLocal;
                snapshot.FiveHourResetKnown = true;
            }
        }
        else
        {
            snapshot.WeeklyPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.WeeklyResetLocal = resetLocal;
                snapshot.WeeklyResetKnown = true;
            }
        }

        return true;
    }

    private static bool TryReadQuotaIniSnapshot(out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        string path = Path.Combine(Logger.DirectoryPath, "quota.ini");
        if (!File.Exists(path))
        {
            return false;
        }

        bool found = false;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.FiveHourPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "FiveHourReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.FiveHourResetLocal = dateTime;
                    snapshot.FiveHourResetKnown = true;
                    found = true;
                }
                else if (string.Equals(key, "WeeklyReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.WeeklyResetLocal = dateTime;
                    snapshot.WeeklyResetKnown = true;
                    found = true;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        if (found)
        {
            snapshot.SourceUpdatedUtc = SafeGetLastWriteTimeUtc(path);
            snapshot.SourceUpdatedKnown = snapshot.SourceUpdatedUtc != DateTime.MinValue;
        }

        return found;
    }

    private static Dictionary<string, object> GetQuotaObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static Dictionary<string, object> GetFirstQuotaObjectFromArray(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return null;
        }

        object[] array = value as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                Dictionary<string, object> item = array[i] as Dictionary<string, object>;
                if (item != null)
                {
                    return item;
                }
            }
        }

        System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null || value is string)
        {
            return null;
        }

        foreach (object entry in enumerable)
        {
            Dictionary<string, object> item = entry as Dictionary<string, object>;
            if (item != null)
            {
                return item;
            }
        }

        return null;
    }

    private static string GetQuotaString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGetJsonBool(Dictionary<string, object> values, string key, out bool result)
    {
        result = false;
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return false;
        }

        if (value is bool)
        {
            result = (bool)value;
            return true;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return bool.TryParse(text, out result);
    }

    private static bool TryGetQuotaNumber(Dictionary<string, object> values, string key, out double number)
    {
        number = 0.0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaNumber(value, out number);
    }

    private static bool TryReadQuotaNumber(object value, out double number)
    {
        number = 0.0;
        if (value == null)
        {
            return false;
        }

        string text = value as string;
        if (text != null)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            number = 0.0;
            return false;
        }
    }

    private static bool TryGetQuotaDate(Dictionary<string, object> values, string key, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaDate(value, out localDate);
    }

    private static bool TryReadQuotaDate(object value, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        double seconds;
        if (TryReadQuotaNumber(value, out seconds))
        {
            if (seconds > 10000000000.0)
            {
                seconds /= 1000.0;
            }

            try
            {
                DateTimeOffset epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
                localDate = epoch.AddSeconds(seconds).LocalDateTime;
                return true;
            }
            catch
            {
                localDate = DateTime.MinValue;
                return false;
            }
        }

        string text = value as string;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        DateTimeOffset offsetDate;
        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out offsetDate))
        {
            localDate = offsetDate.LocalDateTime;
            return true;
        }

        DateTime dateTime;
        if (DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dateTime))
        {
            localDate = dateTime.ToLocalTime();
            return true;
        }

        return false;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long SafeGetFileLength(string path)
    {
        try
        {
            FileInfo info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static int ClampEfficiencyPercent(int value)
    {
        return Math.Max(0, Math.Min(999, value));
    }

    private static Color GetQuotaColor(int percent)
    {
        if (percent >= 80)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235);
        }

        if (percent >= 30)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningSoft, 238);
        }

        if (percent <= 5)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaDanger, 238);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
    }

    // Legacy power/thermal UI is retained only as reference; PowerThermalForm owns that workload.
#if false
    private void DrawThermalAlerts(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int visibleSensors = Math.Min(3, total);
        bool hasMore = total > 3;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(6);
        float chipHeight = Math.Max(S(16), bounds.Height - S(2));
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height - chipHeight) / 2.0f);

        using (Font chipFont = DesignTokens.CreateUIFont(Math.Max(8.0f, 9.5f * this.scale), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float moreWidth = 0.0f;
            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString();
                moreWidth = Math.Max(S(30), g.MeasureString(moreText, chipFont).Width + S(18));
                moreWidth = Math.Min(moreWidth, bounds.Width * 0.28f);
                RectangleF moreRect = new RectangleF(bounds.Right - moreWidth, chipTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }

            float sensorAreaRight = hasMore ? bounds.Right - moreWidth - gap : bounds.Right;
            float sensorAreaWidth = Math.Max(S(30), sensorAreaRight - bounds.Left);
            float slotWidth = Math.Max(S(30), (sensorAreaWidth - gap * 2.0f) / 3.0f);
            float x = bounds.Left;
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float desiredWidth = g.MeasureString(text, chipFont).Width + S(alerts[i].CriticalActive ? 32 : 20);
                float width = Math.Min(slotWidth, Math.Max(S(30), desiredWidth));
                RectangleF chipRect = new RectangleF(x, chipTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                x += slotWidth + gap;
            }
        }
    }

    private void DrawThermalChip(Graphics g, RectangleF rect, string text, double celsius, bool criticalActive, Font font)
    {
        float radius = Math.Min(rect.Height / 2.0f, S(11));
        int redAlpha = GetThermalRedAlpha(celsius);
        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush baseBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.ThermalChipSurface, 160)))
        using (SolidBrush redBrush = new SolidBrush(DesignTokens.DangerStrong(redAlpha)))
        using (Pen border = new Pen(DesignTokens.White(45), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(baseBrush, path);
            g.FillPath(redBrush, path);
            g.DrawPath(border, path);
        }

        RectangleF textRect = rect;
        if (criticalActive)
        {
            float iconSize = Math.Max(S(12), Math.Min(rect.Height * 0.70f, S(17)));
            RectangleF iconRect = new RectangleF(rect.Right - iconSize - S(7), rect.Top + (rect.Height - iconSize) / 2.0f, iconSize, iconSize);
            DrawSmallWarningIcon(g, iconRect);
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - iconSize - S(18)), rect.Height);
        }
        else
        {
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - S(16)), rect.Height);
        }

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        {
            DrawCodexRadarFittedText(g, text, font, textBrush, textRect, StringAlignment.Near);
        }
    }

    private void DrawSmallWarningIcon(Graphics g, RectangleF rect)
    {
        int warningAlpha = (this.renderTickCount % 2 == 0) ? 77 : 179;
        float centerX = rect.Left + rect.Width / 2.0f;
        float centerY = rect.Top + rect.Height / 2.0f;
        float size = Math.Min(rect.Width, rect.Height);
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.46f),
            new PointF(centerX - size * 0.48f, centerY + size * 0.42f),
            new PointF(centerX + size * 0.48f, centerY + size * 0.42f)
        };

        using (Pen pen = new Pen(DesignTokens.Warning(warningAlpha), Math.Max(1.0f, 2.0f * this.scale)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPolygon(pen, triangle);
        }

        using (Font markFont = DesignTokens.CreateUIFont(Math.Max(7.0f, size * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush markBrush = new SolidBrush(DesignTokens.Warning(warningAlpha)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("!", markFont, markBrush, rect, format);
        }
    }

    private static int GetThermalRedAlpha(double celsius)
    {
        double progress = (celsius - 70.0) / 30.0;
        if (progress < 0.0)
        {
            progress = 0.0;
        }
        else if (progress > 1.0)
        {
            progress = 1.0;
        }

        double alpha = 0.30 + progress * (0.85 - 0.30);
        return (int)Math.Round(alpha * 255.0);
    }

    private static string FormatThermalSensorName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "TZ";
        }

        return name.Trim();
    }

#endif

    private void DrawCodexRadarFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
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

                size -= 0.8f * this.scale;
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

#if false
    private PowerReading GetPowerReading()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedPowerReadingUtc).TotalSeconds < 2.0)
        {
            return this.cachedPowerReading;
        }

        this.cachedPowerReading = ReadPowerReading();
        this.cachedPowerReadingUtc = now;
        return this.cachedPowerReading;
    }

    private List<ThermalReading> GetThermalAlerts()
    {
        DateTime now = DateTime.UtcNow;
        if (this.currentSettings.ThermalTestMode != ThermalTestMode.Off)
        {
            List<ThermalReading> simulated = BuildSimulatedThermalReadings(this.currentSettings.ThermalTestMode);
            UpdateThermalCriticalStates(simulated, now, true);
            simulated.Sort(CompareThermalReading);
            return simulated;
        }

        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
            UpdateThermalCriticalStates(this.cachedThermalReadings, now, false);
        }

        List<ThermalReading> alerts = new List<ThermalReading>();
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            if (this.cachedThermalReadings[i].Celsius >= 70.0)
            {
                alerts.Add(this.cachedThermalReadings[i]);
            }
        }

        alerts.Sort(CompareThermalReading);
        return alerts;
    }

    private void UpdateThermalCriticalStates(List<ThermalReading> readings, DateTime now, bool instantCritical)
    {
        if (readings == null)
        {
            return;
        }

        HashSet<string> activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < readings.Count; i++)
        {
            ThermalReading reading = readings[i];
            if (reading == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(reading.Name))
            {
                continue;
            }

            activeNames.Add(reading.Name);
            if (reading.Celsius >= 95.0)
            {
                DateTime since;
                if (!this.thermalCriticalSinceUtc.TryGetValue(reading.Name, out since))
                {
                    since = instantCritical ? now.AddSeconds(-3.0) : now;
                    this.thermalCriticalSinceUtc[reading.Name] = since;
                }

                reading.CriticalActive = (now - since).TotalSeconds >= 3.0;
            }
            else
            {
                this.thermalCriticalSinceUtc.Remove(reading.Name);
                reading.CriticalActive = false;
            }
        }

        List<string> stale = new List<string>();
        foreach (string name in this.thermalCriticalSinceUtc.Keys)
        {
            if (!activeNames.Contains(name))
            {
                stale.Add(name);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            this.thermalCriticalSinceUtc.Remove(stale[i]);
        }
    }

    private static int CompareThermalReading(ThermalReading left, ThermalReading right)
    {
        int value = right.Celsius.CompareTo(left.Celsius);
        if (value != 0)
        {
            return value;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ThermalReading> ReadThermalReadings()
    {
        List<ThermalReading> readings = new List<ThermalReading>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(GetManagementValue(item, "Name"));
                    double celsius = ConvertThermalZoneCelsius(
                        GetManagementValue(item, "Temperature"),
                        GetManagementValue(item, "HighPrecisionTemperature"));
                    if (string.IsNullOrEmpty(name) || celsius <= 0.0)
                    {
                        continue;
                    }

                    readings.Add(new ThermalReading
                    {
                        Name = name.Trim(),
                        Celsius = celsius,
                        CriticalActive = false
                    });
                }
            }
        }
        catch
        {
        }

        return readings;
    }

    private List<ThermalReading> BuildSimulatedThermalReadings(ThermalTestMode mode)
    {
        double celsius = mode == ThermalTestMode.Simulate100 ? 100.0 : 75.0;
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0 || this.cachedThermalReadings.Count == 0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
        }

        List<ThermalReading> readings = new List<ThermalReading>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            string name = this.cachedThermalReadings[i].Name;
            if (string.IsNullOrEmpty(name) || !usedNames.Add(name))
            {
                continue;
            }

            readings.Add(new ThermalReading
            {
                Name = name,
                Celsius = celsius,
                CriticalActive = false
            });
        }

        if (readings.Count > 0)
        {
            return readings;
        }

        for (int i = 0; i < 6; i++)
        {
            readings.Add(new ThermalReading
            {
                Name = @"\_SB.TZ" + i.ToString(),
                Celsius = celsius,
                CriticalActive = false
            });
        }

        return readings;
    }

    private static double ConvertThermalZoneCelsius(object temperature, object highPrecisionTemperature)
    {
        double highPrecision = ToPositiveDouble(highPrecisionTemperature);
        if (highPrecision > 0.0)
        {
            return highPrecision / 10.0 - 273.15;
        }

        double standard = ToPositiveDouble(temperature);
        if (standard > 0.0)
        {
            return standard - 273.15;
        }

        return 0.0;
    }

    private static PowerReading ReadPowerReading()
    {
        PowerReading reading = new PowerReading();
        try
        {
            PowerLineStatus lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                reading.StatusKnown = true;
                reading.IsCharging = lineStatus == PowerLineStatus.Online;
            }
        }
        catch
        {
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    double chargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "ChargeRate"));
                    double dischargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "DischargeRate"));
                    object charging = GetManagementValue(item, "Charging");
                    object discharging = GetManagementValue(item, "Discharging");
                    object powerOnline = GetManagementValue(item, "PowerOnline");

                    if (chargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = true;
                        reading.WattsKnown = true;
                        reading.Watts = chargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (dischargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        reading.WattsKnown = true;
                        reading.Watts = dischargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (charging != null)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = Convert.ToBoolean(charging);
                    }

                    if (discharging != null && Convert.ToBoolean(discharging))
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                    }

                    if (powerOnline != null)
                    {
                        reading.StatusKnown = true;
                        if (!Convert.ToBoolean(powerOnline))
                        {
                            reading.IsCharging = false;
                        }
                    }

                    return reading;
                }
            }
        }
        catch
        {
        }

        return reading;
    }

    private static object GetManagementValue(ManagementBaseObject item, string name)
    {
        try
        {
            PropertyData property = item.Properties[name];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static double ToPositiveDouble(object value)
    {
        if (value == null)
        {
            return 0.0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            return number > 0.0 ? number : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static double ToPositiveMilliwatts(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            if (number <= 0 || number >= 4294967294.0)
            {
                return 0;
            }

            return number;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatWatts(double watts)
    {
        if (watts >= 100.0)
        {
            return watts.ToString("0") + " W";
        }

        return watts.ToString("0.0") + " W";
    }

#endif

    private static string GetOrdinalSuffix(int day)
    {
        int lastTwo = day % 100;
        if (lastTwo >= 11 && lastTwo <= 13)
        {
            return "th";
        }

        switch (day % 10)
        {
            case 1:
                return "st";
            case 2:
                return "nd";
            case 3:
                return "rd";
            default:
                return "th";
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
            // Hover opacity can reuse the previous content and update only the layered-window alpha.
            bool refreshNativeBitmap = redrawContent || !this.renderBufferValid;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawCodexRadarBackground(this.renderGraphics);
                DrawCodexRadarContentLayer(this.renderGraphics);
                this.renderBufferValid = true;
                this.lastRenderedClockSecondLocal = TruncateToSecond(DateTime.Now);
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
                    Program.LogInfo("CodexRadar UpdateLayeredWindow failed; falling back to normal paint.");
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

    private void ResetDisplayRenderResources()
    {
        DisposeRenderBuffer();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
    }

    private static DateTime TruncateToSecond(DateTime value)
    {
        return new DateTime(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Kind);
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.CodexRadarTransparencyPercent) / 100.0);
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
