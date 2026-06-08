using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class PowerThermalForm : Form
{
    private const int RenderSecondBoundaryOffsetMs = 30;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    // WMI access is single-flight. Forced events are coalesced into these pending flags.
    private readonly object samplingSync = new object();
    private readonly Dictionary<string, DateTime> thermalCriticalSinceUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> thermalAlertNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private bool samplingWorkerRunning;
    private bool pendingPowerSample;
    private bool pendingThermalSample;
    private bool formClosing;
    private bool sessionActive = true;
    private bool displayActive = true;
    private bool powerSuspended;
    private bool suppressSizeRender;
    // Native registrations wake the sampler immediately; timed sampling remains the fallback.
    private IntPtr displayPowerNotificationHandle;
    private IntPtr acDcPowerNotificationHandle;
    private IntPtr batteryPowerNotificationHandle;
    private IntPtr powerSchemeNotificationHandle;
    private IntPtr effectivePowerModeNotificationHandle;
    private NativeMethods.EffectivePowerModeCallback effectivePowerModeCallback;
    private PowerReading cachedPowerReading;
    private DateTime cachedPowerReadingUtc;
    private List<ThermalReading> cachedThermalReadings = new List<ThermalReading>();
    private DateTime cachedThermalReadingsUtc;
    private int renderTickCount;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private bool sharedInteractionPolling;
    // Layered-window content is reused until data or size changes. Alpha-only hover updates
    // can submit the existing bitmap without rebuilding paths, fonts, and brushes.
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private bool renderBufferValid;
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private readonly UiFontCache fontCache = new UiFontCache();

    private struct PowerReading
    {
        public bool StatusKnown;
        public bool IsCharging;
        public bool PluggedInKnown;
        public bool IsPluggedIn;
        public bool WattsKnown;
        public double Watts;
        public bool BatteryPercentKnown;
        public int BatteryPercent;
        public bool SystemPowerModeKnown;
        public string SystemPowerModeText;
        public bool BatteryCarePauseKnown;
        public bool BatteryCarePauseActive;
    }

    private sealed class ThermalReading
    {
        public string Name { get; set; }
        public double Celsius { get; set; }
        public bool CriticalActive { get; set; }
    }

    private sealed class SamplingResult
    {
        public bool PowerSampled { get; set; }
        public PowerReading Power { get; set; }
        public bool ThermalSampled { get; set; }
        public List<ThermalReading> ThermalReadings { get; set; }
        public DateTime SampledUtc { get; set; }
    }

    private struct SamplingPolicy
    {
        // Thermal sampling accelerates independently as the device approaches an alert.
        public int PowerIntervalMs;
        public int ThermalIntervalMs;
        public int WarmThermalIntervalMs;
        public int AlertThermalIntervalMs;
        public int CriticalThermalIntervalMs;
    }

    public PowerThermalForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();

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
        this.MinimumSize = new Size(WidgetSettings.MinPowerThermalWidth, WidgetSettings.MinPowerThermalHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxPowerThermalWidth, WidgetSettings.MaxPowerThermalAutoHeight + S(32));
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextRenderTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
        this.effectivePowerModeCallback = OnEffectivePowerModeChanged;
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
        ApplyRuntimeSettings(this.currentSettings);
        PositionPowerThermalWindow();
        this.timer.Start();
        RequestSampling(true, true, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Register both broad power broadcasts and specific setting GUIDs. Some devices
        // expose only a subset, so failure of one registration is intentionally non-fatal.
        this.displayPowerNotificationHandle = NativeMethods.RegisterConsoleDisplayStateNotification(this.Handle);
        this.acDcPowerNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
            this.Handle,
            NativeMethods.GUID_ACDC_POWER_SOURCE);
        this.batteryPowerNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
            this.Handle,
            NativeMethods.GUID_BATTERY_PERCENTAGE_REMAINING);
        this.powerSchemeNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
            this.Handle,
            NativeMethods.GUID_POWERSCHEME_PERSONALITY);
        NativeMethods.TryRegisterEffectivePowerModeNotification(
            this.effectivePowerModeCallback,
            out this.effectivePowerModeNotificationHandle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterPowerNotification(this.displayPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.acDcPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.batteryPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.powerSchemeNotificationHandle);
        NativeMethods.UnregisterEffectivePowerModeNotification(this.effectivePowerModeNotificationHandle);
        this.displayPowerNotificationHandle = IntPtr.Zero;
        this.acDcPowerNotificationHandle = IntPtr.Zero;
        this.batteryPowerNotificationHandle = IntPtr.Zero;
        this.powerSchemeNotificationHandle = IntPtr.Zero;
        this.effectivePowerModeNotificationHandle = IntPtr.Zero;
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.formClosing = true;
        lock (this.samplingSync)
        {
            this.pendingPowerSample = false;
            this.pendingThermalSample = false;
        }

        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
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

        if (!this.suppressSizeRender)
        {
            RenderLayeredWindow();
        }
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
            PositionPowerThermalWindow();
            if (m.Msg == WM_SETTINGCHANGE)
            {
                RequestSampling(true, false, true);
            }
        }
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (this.formClosing || this.IsDisposed)
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
            this.sessionActive = false;
            ScheduleNextRenderTick();
            return;
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            this.sessionActive = true;
            RequestSampling(true, true, true);
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.powerSuspended = true;
            ScheduleNextRenderTick();
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            this.powerSuspended = false;
            this.displayActive = true;
            RequestSampling(true, true, true);
            return;
        }

        if (eventType == NativeMethods.PBT_APMPOWERSTATUSCHANGE)
        {
            RequestSampling(true, false, true);
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
        if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
        {
            bool active = setting.Data != 0;
            if (this.displayActive != active)
            {
                this.displayActive = active;
                if (active)
                {
                    RequestSampling(true, true, true);
                }
                else
                {
                    ScheduleNextRenderTick();
                }
            }

            return;
        }

        if (setting.PowerSetting == NativeMethods.GUID_ACDC_POWER_SOURCE ||
            setting.PowerSetting == NativeMethods.GUID_BATTERY_PERCENTAGE_REMAINING ||
            setting.PowerSetting == NativeMethods.GUID_POWERSCHEME_PERSONALITY)
        {
            RequestSampling(true, false, true);
        }
    }

    private void OnEffectivePowerModeChanged(int mode, IntPtr context)
    {
        RequestSamplingFromAnyThread(true, false);
    }

    private void RequestSamplingFromAnyThread(bool readPower, bool readThermal)
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        try
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                RequestSampling(readPower, readThermal, true);
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool IsSamplingAllowed()
    {
        // Pausing here prevents WMI wakeups while the result cannot be observed.
        return !this.formClosing &&
            !this.hiddenForFullscreen &&
            this.sessionActive &&
            this.displayActive &&
            !this.powerSuspended &&
            this.Visible;
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        ThermalTestMode oldThermalTestMode = this.currentSettings.ThermalTestMode;
        WidgetPerformanceMode oldPerformanceMode = this.currentSettings.PerformanceMode;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        if (oldThermalTestMode != this.currentSettings.ThermalTestMode)
        {
            this.thermalCriticalSinceUtc.Clear();
            this.thermalAlertNames.Clear();
            this.cachedThermalReadingsUtc = DateTime.MinValue;
        }

        ApplyPerformanceTimerIntervals();
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            SetSizeWithoutImmediateRender(desiredSize);
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

        PositionPowerThermalWindow();
        RenderLayeredWindow();

        if (oldThermalTestMode != this.currentSettings.ThermalTestMode ||
            oldPerformanceMode != this.currentSettings.PerformanceMode)
        {
            RequestSampling(true, true, true);
        }
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

        PositionPowerThermalWindow();
        RenderLayeredWindow();
        UpdateHoverAnimationTimer();
        RequestSampling(true, true, true);
    }

    public void ForceRefresh()
    {
        this.cachedPowerReadingUtc = DateTime.MinValue;
        this.cachedThermalReadingsUtc = DateTime.MinValue;
        RequestSampling(true, true, true);
    }

    public void RecoverAfterDisplayResume()
    {
        this.powerSuspended = false;
        this.displayActive = true;
        this.sessionActive = true;
        this.cachedPowerReadingUtc = DateTime.MinValue;
        this.cachedThermalReadingsUtc = DateTime.MinValue;
        ResetDisplayRenderResources();
        PositionPowerThermalWindow();
        RenderLayeredWindow();
        RequestSampling(true, true, true);
        ScheduleNextRenderTick();
    }

    public void PrepareForDisplaySuspend()
    {
        ResetDisplayRenderResources();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        try
        {
            if (!IsSamplingAllowed())
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            SamplingPolicy policy = GetSamplingPolicy();
            // Power and thermal data have separate deadlines; one worker may satisfy both.
            bool powerDue =
                this.cachedPowerReadingUtc == DateTime.MinValue ||
                (now - this.cachedPowerReadingUtc).TotalMilliseconds >= policy.PowerIntervalMs;
            int thermalInterval = GetCurrentThermalIntervalMs(policy);
            bool thermalDue =
                this.cachedThermalReadingsUtc == DateTime.MinValue ||
                (now - this.cachedThermalReadingsUtc).TotalMilliseconds >= thermalInterval;
            if (powerDue || thermalDue)
            {
                RequestSampling(powerDue, thermalDue, false);
            }
        }
        finally
        {
            ScheduleNextRenderTick();
        }
    }

    private void ApplyPerformanceTimerIntervals()
    {
        ScheduleNextRenderTick();

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
    }

    private void ScheduleNextRenderTick()
    {
        int interval = GetNextRenderTickIntervalMs();
        if (this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private int GetNextRenderTickIntervalMs()
    {
        if (!IsSamplingAllowed())
        {
            return 5000;
        }

        DateTime now = DateTime.UtcNow;
        SamplingPolicy policy = GetSamplingPolicy();
        double powerRemaining = this.cachedPowerReadingUtc == DateTime.MinValue
            ? 50.0
            : policy.PowerIntervalMs - (now - this.cachedPowerReadingUtc).TotalMilliseconds;
        int thermalInterval = GetCurrentThermalIntervalMs(policy);
        double thermalRemaining = this.cachedThermalReadingsUtc == DateTime.MinValue
            ? 50.0
            : thermalInterval - (now - this.cachedThermalReadingsUtc).TotalMilliseconds;
        int interval = (int)Math.Ceiling(Math.Min(powerRemaining, thermalRemaining));
        return Math.Max(50, Math.Min(5000, interval + RenderSecondBoundaryOffsetMs));
    }

    private SamplingPolicy GetSamplingPolicy()
    {
        // "Smooth" is the legacy persisted enum name for the user-facing Performance mode.
        SamplingPolicy policy = new SamplingPolicy();
        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.Smooth)
        {
            policy.PowerIntervalMs = 1000;
            policy.ThermalIntervalMs = 2000;
            policy.WarmThermalIntervalMs = 1500;
            policy.AlertThermalIntervalMs = 1000;
            policy.CriticalThermalIntervalMs = 1000;
            return policy;
        }

        if (this.currentSettings.PerformanceMode == WidgetPerformanceMode.BatterySaver)
        {
            policy.PowerIntervalMs = 5000;
            policy.ThermalIntervalMs = 10000;
            policy.WarmThermalIntervalMs = 5000;
            policy.AlertThermalIntervalMs = 3000;
            policy.CriticalThermalIntervalMs = 1000;
            return policy;
        }

        policy.PowerIntervalMs = 2000;
        policy.ThermalIntervalMs = 5000;
        policy.WarmThermalIntervalMs = 3000;
        policy.AlertThermalIntervalMs = 2000;
        policy.CriticalThermalIntervalMs = 1000;
        return policy;
    }

    private int GetCurrentThermalIntervalMs(SamplingPolicy policy)
    {
        double maximumCelsius = 0.0;
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            maximumCelsius = Math.Max(maximumCelsius, this.cachedThermalReadings[i].Celsius);
        }

        if (maximumCelsius >= 90.0)
        {
            // Safety-related sampling always wins over the selected power-saving mode.
            return policy.CriticalThermalIntervalMs;
        }

        if (maximumCelsius >= 70.0)
        {
            return policy.AlertThermalIntervalMs;
        }

        if (maximumCelsius >= 65.0)
        {
            return policy.WarmThermalIntervalMs;
        }

        return policy.ThermalIntervalMs;
    }

    private void RequestSampling(bool readPower, bool readThermal, bool queueAfterCurrent)
    {
        if ((!readPower && !readThermal) || !IsSamplingAllowed())
        {
            ScheduleNextRenderTick();
            return;
        }

        lock (this.samplingSync)
        {
            if (this.samplingWorkerRunning)
            {
                // Timer requests may be dropped because the in-flight sample satisfies the
                // same deadline. Events/manual refreshes set queueAfterCurrent and are replayed.
                if (queueAfterCurrent)
                {
                    this.pendingPowerSample |= readPower;
                    this.pendingThermalSample |= readThermal;
                }

                return;
            }

            this.samplingWorkerRunning = true;
        }

        ThermalTestMode thermalTestMode = this.currentSettings.ThermalTestMode;
        List<string> simulatedNames = new List<string>();
        if (readThermal && thermalTestMode != ThermalTestMode.Off)
        {
            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < this.cachedThermalReadings.Count; i++)
            {
                string name = this.cachedThermalReadings[i].Name;
                if (!string.IsNullOrEmpty(name) && usedNames.Add(name))
                {
                    simulatedNames.Add(name);
                }
            }
        }

        Task.Run(delegate
        {
            // Never perform WMI or powercfg work on the WinForms UI thread.
            SamplingResult result = new SamplingResult();
            result.SampledUtc = DateTime.UtcNow;
            try
            {
                if (readPower)
                {
                    result.Power = ReadPowerReading();
                    result.PowerSampled = true;
                }

                if (readThermal)
                {
                    result.ThermalReadings = thermalTestMode == ThermalTestMode.Off
                        ? ReadThermalReadings()
                        : BuildSimulatedThermalReadings(thermalTestMode, simulatedNames);
                    result.ThermalSampled = true;
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }

            result.SampledUtc = DateTime.UtcNow;
            CompleteSamplingFromWorker(result);
        });
    }

    private void CompleteSamplingFromWorker(SamplingResult result)
    {
        if (this.formClosing || this.IsDisposed || !this.IsHandleCreated)
        {
            lock (this.samplingSync)
            {
                this.samplingWorkerRunning = false;
            }

            return;
        }

        try
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                ApplySamplingResult(result);
            });
        }
        catch (InvalidOperationException)
        {
            lock (this.samplingSync)
            {
                this.samplingWorkerRunning = false;
            }
        }
    }

    private void ApplySamplingResult(SamplingResult result)
    {
        // All cache, alert-state, layout, and rendering changes stay on the UI thread.
        PowerReading oldPower = this.cachedPowerReading;
        List<ThermalReading> oldAlerts = GetThermalAlerts();

        if (result.PowerSampled)
        {
            this.cachedPowerReading = result.Power;
            this.cachedPowerReadingUtc = result.SampledUtc;
        }

        if (result.ThermalSampled)
        {
            this.cachedThermalReadings = result.ThermalReadings ?? new List<ThermalReading>();
            this.cachedThermalReadingsUtc = result.SampledUtc;
            UpdateThermalAlertStates(this.cachedThermalReadings);
            UpdateThermalCriticalStates(
                this.cachedThermalReadings,
                result.SampledUtc,
                this.currentSettings.ThermalTestMode != ThermalTestMode.Off);
        }

        List<ThermalReading> newAlerts = GetThermalAlerts();
        bool contentChanged =
            (result.PowerSampled && !PowerDisplayEquals(oldPower, this.cachedPowerReading)) ||
            (result.ThermalSampled && !ThermalDisplayEquals(oldAlerts, newAlerts));
        // Critical temperature and very low battery colors alternate once per completed sample.
        bool animatedWarning =
            HasCriticalThermalAlert(newAlerts) ||
            (this.cachedPowerReading.BatteryPercentKnown && this.cachedPowerReading.BatteryPercent < 10);

        this.renderTickCount++;
        Size desiredSize = GetDesiredSize(newAlerts);
        bool sizeChanged = this.Size != desiredSize;
        if (sizeChanged)
        {
            SetSizeWithoutImmediateRender(desiredSize);
            PositionPowerThermalWindow();
        }

        lock (this.samplingSync)
        {
            this.samplingWorkerRunning = false;
        }

        if (contentChanged || animatedWarning || sizeChanged)
        {
            RenderLayeredWindow();
        }

        bool pendingPower;
        bool pendingThermal;
        lock (this.samplingSync)
        {
            pendingPower = this.pendingPowerSample;
            pendingThermal = this.pendingThermalSample;
            this.pendingPowerSample = false;
            this.pendingThermalSample = false;
        }

        if (pendingPower || pendingThermal)
        {
            RequestSampling(pendingPower, pendingThermal, false);
        }
        else
        {
            ScheduleNextRenderTick();
        }
    }

    private void SetSizeWithoutImmediateRender(Size size)
    {
        this.suppressSizeRender = true;
        try
        {
            this.Size = size;
        }
        finally
        {
            this.suppressSizeRender = false;
        }
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

    private void PositionPowerThermalWindow()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            SetSizeWithoutImmediateRender(desiredSize);
        }

        int left;
        if (IsPowerThermalAutoLeft())
        {
            int baseWidth = Math.Max(
                WidgetSettings.MinPowerThermalWidth,
                Math.Min(WidgetSettings.MaxPowerThermalWidth, this.currentSettings.PowerThermalWidth));
            int anchorRight = this.currentSettings.PowerThermalLeftX + baseWidth;
            anchorRight = Math.Max(workArea.Left + this.Width, Math.Min(anchorRight, workArea.Right));
            left = anchorRight - this.Width;
            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - this.Width));
        }
        else
        {
            left = this.currentSettings.PowerThermalLeftX;
            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - this.Width));
        }

        int baseHeight = Math.Max(WidgetSettings.MinPowerThermalHeight, this.currentSettings.PowerThermalHeight);
        int top = this.currentSettings.PowerThermalBottomY - baseHeight + 1;
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

    private Size GetDesiredSize()
    {
        return GetDesiredSize(GetThermalAlerts());
    }

    private Size GetDesiredSize(List<ThermalReading> alerts)
    {
        int width = this.currentSettings.PowerThermalWidth;
        int height = Math.Max(WidgetSettings.MinPowerThermalHeight, Math.Min(WidgetSettings.MaxPowerThermalHeight, this.currentSettings.PowerThermalHeight));
        if (this.currentSettings.PowerThermalAutoSizeEnabled)
        {
            if (IsPowerThermalAutoLeft())
            {
                width += GetThermalAutoExtensionWidth(alerts);
            }
            else if (IsPowerThermalAutoDown())
            {
                height += GetBatteryAutoExtensionHeight();
                height += GetThermalAutoExtensionHeight(alerts);
            }
        }

        width = Math.Max(WidgetSettings.MinPowerThermalWidth, Math.Min(WidgetSettings.MaxPowerThermalWidth, width));
        int maxHeight = IsPowerThermalAutoDown() ? WidgetSettings.MaxPowerThermalAutoHeight : WidgetSettings.MaxPowerThermalHeight;
        height = Math.Max(WidgetSettings.MinPowerThermalHeight, Math.Min(maxHeight, height));
        return new Size(width, height);
    }

    private bool IsPowerThermalAutoLeft()
    {
        return this.currentSettings.PowerThermalAutoSizeEnabled &&
            this.currentSettings.PowerThermalAutoDirection == PowerThermalAutoDirection.Left;
    }

    private bool IsPowerThermalAutoDown()
    {
        return this.currentSettings.PowerThermalAutoSizeEnabled &&
            this.currentSettings.PowerThermalAutoDirection == PowerThermalAutoDirection.Down;
    }

    private int GetThermalAutoExtensionWidth(List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0)
        {
            return 0;
        }

        int maxVisible = GetMaxVisibleThermalAlerts();
        int visibleSensors = Math.Min(maxVisible, alerts.Count);
        bool hasMore = alerts.Count > visibleSensors;
        float width = S(16);
        float gap = S(4);
        using (Font chipFont = CreateThermalChipFont())
        {
            if (hasMore)
            {
                string moreText = "+" + (alerts.Count - visibleSensors).ToString(CultureInfo.InvariantCulture);
                width += MeasureThermalChipWidth(moreText, false, chipFont) + gap;
            }

            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                width += MeasureThermalChipWidth(text, alerts[i].CriticalActive, chipFont);
                if (i < visibleSensors - 1)
                {
                    width += gap;
                }
            }
        }

        return Math.Max(0, (int)Math.Ceiling(width));
    }

    private int GetThermalAutoExtensionHeight(List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0)
        {
            return 0;
        }

        int maxVisible = GetMaxVisibleThermalAlerts();
        int visibleSensors = Math.Min(maxVisible, alerts.Count);
        int chipCount = visibleSensors + (alerts.Count > visibleSensors ? 1 : 0);
        if (chipCount <= 0)
        {
            return 0;
        }

        float chipHeight = GetThermalVerticalChipHeight();
        float gap = S(4);
        float bottomGap = S(7);
        return Math.Max(0, (int)Math.Ceiling(chipHeight * chipCount + gap * Math.Max(0, chipCount - 1) + bottomGap));
    }

    private Font CreateThermalChipFont()
    {
        return DesignTokens.CreateUIFont(Math.Max(9.0f, 10.0f * this.scale), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private int GetMaxVisibleThermalAlerts()
    {
        int count = this.currentSettings.PowerThermalAutoSizeEnabled
            ? this.currentSettings.PowerThermalVisibleAlertCount
            : WidgetSettings.DefaultPowerThermalVisibleAlerts;
        return Math.Max(
            WidgetSettings.MinPowerThermalVisibleAlerts,
            Math.Min(WidgetSettings.MaxPowerThermalVisibleAlerts, count));
    }

    private float GetThermalVerticalChipHeight()
    {
        return S(20);
    }

    private float GetBatteryModuleTopGap()
    {
        return S(5);
    }

    private float GetBatteryModuleHeight()
    {
        return S(36);
    }

    private float GetBatteryModuleBottomGap()
    {
        return S(6);
    }

    private int GetBatteryAutoExtensionHeight()
    {
        return (int)Math.Ceiling(GetBatteryModuleTopGap() + GetBatteryModuleHeight() + GetBatteryModuleBottomGap());
    }

    private float MeasureThermalChipWidth(string text, bool criticalActive, Font font)
    {
        Size proposed = new Size(int.MaxValue, int.MaxValue);
        Size measured = TextRenderer.MeasureText(
            string.IsNullOrEmpty(text) ? "TZ" : text,
            font,
            proposed,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        float sidePadding = S(8);
        return Math.Max(S(38), measured.Width + sidePadding * 2.0f);
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
        DrawPowerThermalWindow(e.Graphics);
    }

    private void DrawPowerThermalWindow(Graphics g)
    {
        DrawBackground(g);
        DrawContentLayer(g);
    }

    private void ConfigureGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawBackground(Graphics g)
    {
        ConfigureGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, alpha)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawContent(Graphics g)
    {
        ConfigureGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        List<ThermalReading> thermalAlerts = GetThermalAlerts();
        float contentTop = S(5);
        float contentHeight = Math.Max(10, this.Height - S(10));
        if (IsPowerThermalAutoDown())
        {
            DrawDownExtendedContent(g, thermalAlerts, contentTop);
            return;
        }

        float powerWidth;
        float powerRight;
        if (IsPowerThermalAutoLeft())
        {
            powerWidth = Math.Max(
                WidgetSettings.MinPowerThermalWidth,
                Math.Min(this.Width, this.currentSettings.PowerThermalWidth));
            powerRight = this.Width;
        }
        else
        {
            RectangleF contentRect = new RectangleF(
                S(10),
                contentTop,
                Math.Max(10, this.Width - S(30)),
                contentHeight);
            powerWidth = thermalAlerts.Count > 0
                ? Math.Max(S(54), Math.Min(S(82), contentRect.Width * 0.36f))
                : Math.Max(S(62), Math.Min(S(96), contentRect.Width * 0.42f));
            if (thermalAlerts.Count == 0)
            {
                powerWidth = Math.Min(contentRect.Width, Math.Max(powerWidth, contentRect.Width * 0.48f));
            }

            powerRight = contentRect.Right;
        }

        RectangleF powerRect = new RectangleF(
            Math.Max(0.0f, powerRight - powerWidth),
            contentTop,
            powerWidth,
            contentHeight);

        DrawPowerModule(g, powerRect);

        if (thermalAlerts.Count > 0)
        {
            float thermalLeft = S(10);
            float thermalRight = powerRect.Left - S(6);
            RectangleF thermalRect = new RectangleF(
                thermalLeft,
                contentTop,
                Math.Max(1.0f, thermalRight - thermalLeft),
                contentHeight);
            DrawThermalAlerts(g, thermalRect, thermalAlerts);
        }
    }

    private void DrawDownExtendedContent(Graphics g, List<ThermalReading> thermalAlerts, float contentTop)
    {
        int baseHeight = Math.Max(WidgetSettings.MinPowerThermalHeight, Math.Min(WidgetSettings.MaxPowerThermalHeight, this.currentSettings.PowerThermalHeight));
        float powerContentHeight = Math.Max(10, baseHeight - S(10));
        RectangleF powerRect = new RectangleF(0, contentTop, this.Width, powerContentHeight);
        DrawPowerModule(g, powerRect);

        float batteryTop = baseHeight + GetBatteryModuleTopGap();
        RectangleF batteryRect = new RectangleF(
            S(10),
            batteryTop,
            Math.Max(1.0f, this.Width - S(20)),
            GetBatteryModuleHeight());
        DrawBatteryModule(g, batteryRect);

        if (thermalAlerts.Count <= 0)
        {
            return;
        }

        float thermalTop = baseHeight + GetBatteryAutoExtensionHeight();
        RectangleF thermalRect = new RectangleF(
            S(10),
            thermalTop,
            Math.Max(1.0f, this.Width - S(20)),
            Math.Max(1.0f, this.Height - thermalTop - S(7)));
        DrawThermalAlertsVertical(g, thermalRect, thermalAlerts);
    }

    private void DrawPowerModule(Graphics g, RectangleF bounds)
    {
        PowerReading reading = GetPowerReading();
        bool charging = reading.StatusKnown && reading.IsCharging;
        string labelText = charging ? "Charging" : "Power";
        string valueText = reading.WattsKnown ? FormatWatts(reading.Watts) : "-- W";
        Color accent = charging ? DesignTokens.Colors.SuccessText : DesignTokens.Colors.DangerText;
        RectangleF textBounds = new RectangleF(bounds.Left + S(8), bounds.Top, Math.Max(4.0f, bounds.Width - S(16)), bounds.Height);
        RectangleF labelRect = new RectangleF(textBounds.Left, textBounds.Top, textBounds.Width, textBounds.Height * 0.48f);
        RectangleF valueRect = new RectangleF(textBounds.Left, textBounds.Top + textBounds.Height * 0.45f, textBounds.Width, textBounds.Height * 0.55f);
        float labelFontSize = Math.Max(8.5f, Math.Min(bounds.Height * 0.22f, bounds.Width * 0.18f));
        float valueFontSize = Math.Max(9.0f, Math.Min(bounds.Height * 0.28f, bounds.Width * 0.18f));

        Font labelFont = this.fontCache.GetUi(labelFontSize, FontStyle.Bold);
        Font valueFont = this.fontCache.GetUi(valueFontSize, FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(accent))
        {
            DrawFittedText(g, labelText, labelFont, brush, labelRect, StringAlignment.Center);
            DrawFittedText(g, valueText, valueFont, brush, valueRect, StringAlignment.Center);
        }
    }

    private void DrawBatteryModule(Graphics g, RectangleF bounds)
    {
        PowerReading reading = GetPowerReading();
        bool known = reading.BatteryPercentKnown;
        int percent = known ? Math.Max(0, Math.Min(100, reading.BatteryPercent)) : 0;
        bool charging = reading.StatusKnown && reading.IsCharging;
        bool pluggedIn = reading.PluggedInKnown && reading.IsPluggedIn;
        Color accent = GetBatteryPercentColor(known, percent);
        Color borderColor = pluggedIn
            ? Color.FromArgb(246, 248, 250)
            : Color.FromArgb(190, 195, 199);
        string powerModeText = reading.SystemPowerModeKnown ? reading.SystemPowerModeText : "--";

        float bodyWidth = Math.Max(S(46), Math.Min(bounds.Width - S(12), bounds.Width * 0.64f));
        float modeTextHeight = S(11);
        float bodyHeight = Math.Max(S(14), Math.Min(S(18), bounds.Height - modeTextHeight - S(5)));
        float bodyLeft = bounds.Left + (bounds.Width - bodyWidth) / 2.0f - S(2);
        float stackHeight = bodyHeight + S(2) + modeTextHeight;
        float bodyTop = bounds.Top + Math.Max(0.0f, (bounds.Height - stackHeight) / 2.0f);
        RectangleF bodyRect = new RectangleF(bodyLeft, bodyTop, bodyWidth, bodyHeight);
        float nubWidth = S(4);
        float nubHeight = Math.Max(S(6), bodyHeight * 0.46f);
        RectangleF nubRect = new RectangleF(bodyRect.Right + S(2), bodyRect.Top + (bodyHeight - nubHeight) / 2.0f, nubWidth, nubHeight);
        float radius = Math.Min(S(4), bodyHeight / 3.0f);

        using (GraphicsPath bodyPath = RoundedRectangle(bodyRect, radius))
        using (GraphicsPath nubPath = RoundedRectangle(nubRect, Math.Min(radius, nubRect.Height / 2.0f)))
        using (SolidBrush surfaceBrush = new SolidBrush(DesignTokens.White(28)))
        using (SolidBrush nubBrush = new SolidBrush(DesignTokens.WithAlpha(borderColor, pluggedIn ? 210 : 145)))
        using (Pen borderPen = new Pen(DesignTokens.WithAlpha(borderColor, pluggedIn ? 245 : 180), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(surfaceBrush, bodyPath);
            g.DrawPath(borderPen, bodyPath);
            g.FillPath(nubBrush, nubPath);
        }

        if (known && percent > 0)
        {
            RectangleF innerRect = RectangleF.Inflate(bodyRect, -S(3), -S(3));
            innerRect.Width = Math.Max(1.0f, innerRect.Width * percent / 100.0f);
            using (GraphicsPath fillPath = RoundedRectangle(innerRect, Math.Min(radius, innerRect.Height / 2.0f)))
            using (SolidBrush fillBrush = new SolidBrush(DesignTokens.WithAlpha(accent, charging ? 168 : 142)))
            {
                g.FillPath(fillBrush, fillPath);
            }
        }

        string percentText = known ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "--";
        bool batteryCarePauseActive = reading.BatteryCarePauseKnown && reading.BatteryCarePauseActive;
        Font percentFont = this.fontCache.GetUi(Math.Max(8.0f, Math.Min(bodyHeight * 0.58f, bodyWidth * 0.20f)), FontStyle.Bold);
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        {
            if (batteryCarePauseActive)
            {
                float badgeSize = Math.Max(S(9), bodyHeight * 0.72f);
                float gap = S(2);
                float maxTextWidth = Math.Max(S(10), bodyRect.Width - badgeSize - gap - S(4));
                float measuredTextWidth = Math.Min(maxTextWidth, g.MeasureString(percentText, percentFont).Width);
                float totalWidth = measuredTextWidth + gap + badgeSize;
                float left = bodyRect.Left + Math.Max(S(2), (bodyRect.Width - totalWidth) / 2.0f);
                RectangleF percentRect = new RectangleF(left, bodyRect.Top, measuredTextWidth, bodyRect.Height);
                RectangleF badgeRect = new RectangleF(
                    percentRect.Right + gap,
                    bodyRect.Top + (bodyRect.Height - badgeSize) / 2.0f,
                    badgeSize,
                    badgeSize);
                DrawFittedText(g, percentText, percentFont, textBrush, percentRect, StringAlignment.Center);
                DrawBatteryCarePauseBadge(g, badgeRect);
            }
            else
            {
                DrawFittedText(g, percentText, percentFont, textBrush, bodyRect, StringAlignment.Center);
            }
        }

        RectangleF modeRect = new RectangleF(bounds.Left, bodyRect.Bottom + S(1), bounds.Width, Math.Max(S(10), bounds.Bottom - bodyRect.Bottom - S(1)));
        Font modeFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.scale), FontStyle.Bold);
        using (SolidBrush modeBrush = new SolidBrush(GetSystemPowerModeColor(powerModeText)))
        {
            DrawFittedText(g, powerModeText, modeFont, modeBrush, modeRect, StringAlignment.Center);
        }
    }

    private Color GetSystemPowerModeColor(string powerModeText)
    {
        if (string.Equals(powerModeText, "性能", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(255, 166, 174);
        }

        if (string.Equals(powerModeText, "省电", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(powerModeText, "节能", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(134, 238, 150);
        }

        return DesignTokens.Colors.TextStrong;
    }

    private void DrawBatteryCarePauseBadge(Graphics g, RectangleF rect)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.0f, 1.05f * this.scale);
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

    private Color GetBatteryPercentColor(bool known, int percent)
    {
        if (!known)
        {
            return DesignTokens.Colors.SubtleText;
        }

        if (percent >= 97)
        {
            return Color.FromArgb(78, 177, 255);
        }

        if (percent >= 90)
        {
            return Color.FromArgb(75, 222, 108);
        }

        if (percent >= 75)
        {
            return Color.FromArgb(176, 246, 152);
        }

        if (percent >= 50)
        {
            return DesignTokens.Colors.Warning;
        }

        if (percent >= 30)
        {
            return DesignTokens.Colors.WarningDeep;
        }

        if (percent >= 10)
        {
            return DesignTokens.Colors.DangerStrong;
        }

        return (this.renderTickCount % 2 == 0) ? DesignTokens.Colors.DangerStrong : Color.FromArgb(105, 7, 18);
    }

    private void DrawThermalAlerts(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int maxVisible = GetMaxVisibleThermalAlerts();
        int visibleSensors = Math.Min(maxVisible, total);
        bool hasMore = total > visibleSensors;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(4);
        float chipHeight = Math.Max(S(13), Math.Min(S(20), (bounds.Height - S(2)) * 0.67f));
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height * 0.48f - chipHeight) / 2.0f);

        using (Font chipFont = CreateThermalChipFont())
        {
            float nextRight = bounds.Right;
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float width = MeasureThermalChipWidth(text, alerts[i].CriticalActive, chipFont);
                RectangleF chipRect = new RectangleF(nextRight - width, chipTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                nextRight = chipRect.Left - gap;
            }

            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString(CultureInfo.InvariantCulture);
                float moreWidth = MeasureThermalChipWidth(moreText, false, chipFont);
                RectangleF moreRect = new RectangleF(nextRight - moreWidth, chipTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }
        }
    }

    private void DrawThermalChip(Graphics g, RectangleF rect, string text, double celsius, bool criticalActive, Font font)
    {
        float radius = Math.Min(rect.Height / 2.0f, S(8));
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

        if (criticalActive)
        {
            float iconSize = Math.Max(S(12), Math.Min(rect.Height * 0.90f, S(18)));
            RectangleF iconRect = new RectangleF(
                rect.Left + (rect.Width - iconSize) / 2.0f,
                rect.Top + (rect.Height - iconSize) / 2.0f,
                iconSize,
                iconSize);
            DrawSmallWarningIcon(g, iconRect);
        }

        RectangleF textRect = new RectangleF(rect.Left + S(5), rect.Top, Math.Max(4, rect.Width - S(10)), rect.Height);

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        {
            DrawFittedText(g, text, font, textBrush, textRect, StringAlignment.Center);
        }
    }

    private float GetThermalWarningIconSize(float chipHeight)
    {
        return Math.Max(S(10), Math.Min(chipHeight * 0.66f, S(13)));
    }

    private void DrawThermalAlertsVertical(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int maxVisible = GetMaxVisibleThermalAlerts();
        int visibleSensors = Math.Min(maxVisible, total);
        bool hasMore = total > visibleSensors;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(4);
        float chipHeight = Math.Min(GetThermalVerticalChipHeight(), Math.Max(S(13), bounds.Height));
        float nextTop = bounds.Top;
        using (Font chipFont = CreateThermalChipFont())
        {
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float width = Math.Min(bounds.Width, MeasureThermalChipWidth(text, alerts[i].CriticalActive, chipFont));
                RectangleF chipRect = new RectangleF(bounds.Left + (bounds.Width - width) / 2.0f, nextTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                nextTop = chipRect.Bottom + gap;
            }

            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString(CultureInfo.InvariantCulture);
                float moreWidth = Math.Min(bounds.Width, MeasureThermalChipWidth(moreText, false, chipFont));
                RectangleF moreRect = new RectangleF(bounds.Left + (bounds.Width - moreWidth) / 2.0f, nextTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }
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

        float markCenterY = (triangle[0].Y + triangle[1].Y + triangle[2].Y) / 3.0f;
        RectangleF markRect = new RectangleF(rect.Left, markCenterY - rect.Height / 2.0f, rect.Width, rect.Height);
        Font markFont = this.fontCache.GetUi(Math.Max(7.0f, size * 0.62f), FontStyle.Bold);
        using (SolidBrush markBrush = new SolidBrush(DesignTokens.Warning(warningAlpha)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("!", markFont, markBrush, markRect, format);
        }
    }

    private void DrawFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
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

    private PowerReading GetPowerReading()
    {
        return this.cachedPowerReading;
    }

    private List<ThermalReading> GetThermalAlerts()
    {
        List<ThermalReading> alerts = new List<ThermalReading>();
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            ThermalReading reading = this.cachedThermalReadings[i];
            if (reading != null &&
                !string.IsNullOrEmpty(reading.Name) &&
                this.thermalAlertNames.Contains(reading.Name))
            {
                alerts.Add(reading);
            }
        }

        alerts.Sort(CompareThermalReading);
        return alerts;
    }

    private void UpdateThermalAlertStates(List<ThermalReading> readings)
    {
        // 70 C enters and below 67 C exits. The dead band prevents resize oscillation.
        HashSet<string> activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (readings != null)
        {
            for (int i = 0; i < readings.Count; i++)
            {
                ThermalReading reading = readings[i];
                if (reading == null || string.IsNullOrEmpty(reading.Name))
                {
                    continue;
                }

                activeNames.Add(reading.Name);
                if (reading.Celsius >= 70.0)
                {
                    this.thermalAlertNames.Add(reading.Name);
                }
                else if (reading.Celsius < 67.0)
                {
                    this.thermalAlertNames.Remove(reading.Name);
                }
            }
        }

        List<string> staleNames = new List<string>();
        foreach (string name in this.thermalAlertNames)
        {
            if (!activeNames.Contains(name))
            {
                staleNames.Add(name);
            }
        }

        for (int i = 0; i < staleNames.Count; i++)
        {
            this.thermalAlertNames.Remove(staleNames[i]);
        }
    }

    private static bool PowerDisplayEquals(PowerReading left, PowerReading right)
    {
        return left.StatusKnown == right.StatusKnown &&
            left.IsCharging == right.IsCharging &&
            left.PluggedInKnown == right.PluggedInKnown &&
            left.IsPluggedIn == right.IsPluggedIn &&
            left.WattsKnown == right.WattsKnown &&
            (!left.WattsKnown || string.Equals(FormatWatts(left.Watts), FormatWatts(right.Watts), StringComparison.Ordinal)) &&
            left.BatteryPercentKnown == right.BatteryPercentKnown &&
            left.BatteryPercent == right.BatteryPercent &&
            left.SystemPowerModeKnown == right.SystemPowerModeKnown &&
            string.Equals(left.SystemPowerModeText, right.SystemPowerModeText, StringComparison.Ordinal) &&
            left.BatteryCarePauseKnown == right.BatteryCarePauseKnown &&
            left.BatteryCarePauseActive == right.BatteryCarePauseActive;
    }

    private static bool ThermalDisplayEquals(List<ThermalReading> left, List<ThermalReading> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.OrdinalIgnoreCase) ||
                left[i].CriticalActive != right[i].CriticalActive ||
                GetThermalRedAlpha(left[i].Celsius) != GetThermalRedAlpha(right[i].Celsius))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCriticalThermalAlert(List<ThermalReading> alerts)
    {
        if (alerts == null)
        {
            return false;
        }

        for (int i = 0; i < alerts.Count; i++)
        {
            if (alerts[i].CriticalActive)
            {
                return true;
            }
        }

        return false;
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
            if (reading == null || string.IsNullOrEmpty(reading.Name))
            {
                continue;
            }

            activeNames.Add(reading.Name);
            DateTime since;
            bool criticalTracked = this.thermalCriticalSinceUtc.TryGetValue(reading.Name, out since);
            if (reading.Celsius >= 95.0)
            {
                // Real sensors must remain critical for three seconds; test mode is immediate.
                if (!criticalTracked)
                {
                    since = instantCritical ? now.AddSeconds(-3.0) : now;
                    this.thermalCriticalSinceUtc[reading.Name] = since;
                }

                reading.CriticalActive = (now - since).TotalSeconds >= 3.0;
            }
            else if (reading.Celsius < 92.0)
            {
                // A separate exit threshold avoids flicker around 95 C.
                this.thermalCriticalSinceUtc.Remove(reading.Name);
                reading.CriticalActive = false;
            }
            else
            {
                reading.CriticalActive = criticalTracked && (now - since).TotalSeconds >= 3.0;
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
                    string name = Convert.ToString(GetManagementValue(item, "Name"), CultureInfo.InvariantCulture);
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

    private static List<ThermalReading> BuildSimulatedThermalReadings(
        ThermalTestMode mode,
        List<string> sourceNames)
    {
        double celsius = mode == ThermalTestMode.Simulate100 ? 100.0 : 75.0;
        List<ThermalReading> readings = new List<ThermalReading>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceNames != null)
        {
            for (int i = 0; i < sourceNames.Count; i++)
            {
                string name = sourceNames[i];
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
        }

        if (readings.Count > 0)
        {
            return readings;
        }

        for (int i = 0; i < 6; i++)
        {
            readings.Add(new ThermalReading
            {
                Name = @"\_SB.TZ" + i.ToString(CultureInfo.InvariantCulture),
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
        bool noSystemBattery = false;
        try
        {
            PowerStatus powerStatus = SystemInformation.PowerStatus;
            noSystemBattery =
                (powerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0;
            PowerLineStatus lineStatus = powerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                reading.PluggedInKnown = true;
                reading.IsPluggedIn = lineStatus == PowerLineStatus.Online;
                if (lineStatus == PowerLineStatus.Offline)
                {
                    reading.StatusKnown = true;
                    reading.IsCharging = false;
                }
            }

            float batteryPercent = powerStatus.BatteryLifePercent;
            if (batteryPercent >= 0.0f && batteryPercent <= 1.0f)
            {
                reading.BatteryPercentKnown = true;
                reading.BatteryPercent = (int)Math.Round(batteryPercent * 100.0f);
            }

            string powerModeText = ReadSystemPowerModeText(reading.PluggedInKnown, reading.IsPluggedIn);
            if (!string.IsNullOrEmpty(powerModeText))
            {
                reading.SystemPowerModeKnown = true;
                reading.SystemPowerModeText = powerModeText;
            }
        }
        catch
        {
        }

        UpdateBatteryCarePauseState(ref reading);
        if (noSystemBattery)
        {
            return reading;
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
                    if (powerOnline != null)
                    {
                        reading.PluggedInKnown = true;
                        reading.IsPluggedIn = Convert.ToBoolean(powerOnline, CultureInfo.InvariantCulture);
                    }

                    if (chargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = true;
                        if (!reading.PluggedInKnown)
                        {
                            reading.PluggedInKnown = true;
                            reading.IsPluggedIn = true;
                        }

                        reading.WattsKnown = true;
                        reading.Watts = chargeMilliwatts / 1000.0;
                        UpdateSystemPowerModeText(ref reading);
                        UpdateBatteryCarePauseState(ref reading);
                        return reading;
                    }

                    if (dischargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        if (!reading.PluggedInKnown)
                        {
                            reading.PluggedInKnown = true;
                            reading.IsPluggedIn = false;
                        }

                        reading.WattsKnown = true;
                        reading.Watts = dischargeMilliwatts / 1000.0;
                        UpdateSystemPowerModeText(ref reading);
                        UpdateBatteryCarePauseState(ref reading);
                        return reading;
                    }

                    if (charging != null)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = Convert.ToBoolean(charging, CultureInfo.InvariantCulture);
                        if (reading.IsCharging && !reading.PluggedInKnown)
                        {
                            reading.PluggedInKnown = true;
                            reading.IsPluggedIn = true;
                        }
                    }

                    if (discharging != null && Convert.ToBoolean(discharging, CultureInfo.InvariantCulture))
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        if (!reading.PluggedInKnown)
                        {
                            reading.PluggedInKnown = true;
                            reading.IsPluggedIn = false;
                        }
                    }

                    UpdateSystemPowerModeText(ref reading);
                    UpdateBatteryCarePauseState(ref reading);
                    return reading;
                }
            }
        }
        catch
        {
        }

        return reading;
    }

    private static void UpdateBatteryCarePauseState(ref PowerReading reading)
    {
        // Windows does not expose a vendor-independent battery-care pause flag.
        reading.BatteryCarePauseKnown = false;
        reading.BatteryCarePauseActive = false;
    }

    private static void UpdateSystemPowerModeText(ref PowerReading reading)
    {
        if (reading.SystemPowerModeKnown)
        {
            return;
        }

        // A single power sample must not repeat the registry/WMI/powercfg fallback chain.
        string powerModeText = ReadSystemPowerModeText(reading.PluggedInKnown, reading.IsPluggedIn);
        if (!string.IsNullOrEmpty(powerModeText))
        {
            reading.SystemPowerModeKnown = true;
            reading.SystemPowerModeText = powerModeText;
        }
    }

    private static string ReadSystemPowerModeText(bool pluggedInKnown, bool pluggedIn)
    {
        string overlayMode = ReadPowerOverlayModeText(pluggedInKnown, pluggedIn);
        if (!string.IsNullOrEmpty(overlayMode))
        {
            return overlayMode;
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2\power", "SELECT ElementName FROM Win32_PowerPlan WHERE IsActive=True"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(GetManagementValue(item, "ElementName"), CultureInfo.InvariantCulture);
                    return NormalizeSystemPowerModeText(name);
                }
            }
        }
        catch
        {
        }

        return ReadPowerCfgPowerModeText();
    }

    private static string ReadPowerOverlayModeText(bool pluggedInKnown, bool pluggedIn)
    {
        if (!pluggedInKnown)
        {
            return string.Empty;
        }

        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
            {
                if (key == null)
                {
                    return string.Empty;
                }

                string valueName = pluggedIn ? "ActiveOverlayAcPowerScheme" : "ActiveOverlayDcPowerScheme";
                string overlayGuid = Convert.ToString(key.GetValue(valueName), CultureInfo.InvariantCulture);
                return NormalizeSystemPowerModeText(overlayGuid);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ReadPowerCfgPowerModeText()
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "powercfg.exe";
            startInfo.Arguments = "/getactivescheme";
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return string.Empty;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(1200))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }
                }

                return NormalizeSystemPowerModeText(ExtractPowerPlanName(output));
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractPowerPlanName(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return string.Empty;
        }

        int open = output.LastIndexOf('(');
        int close = output.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            return output.Substring(open + 1, close - open - 1).Trim();
        }

        return output.Trim();
    }

    private static string NormalizeSystemPowerModeText(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        string lower = name.ToLowerInvariant();
        if (lower.IndexOf("00000000-0000-0000-0000-000000000000", StringComparison.Ordinal) >= 0)
        {
            return "平衡";
        }

        if (lower.IndexOf("961cc777-2547-4f9d-8174-7d86181b8a7a", StringComparison.Ordinal) >= 0)
        {
            return "省电";
        }

        if (lower.IndexOf("ded574b5-45a0-4f42-8737-46345c09c238", StringComparison.Ordinal) >= 0)
        {
            return "性能";
        }

        if (lower.IndexOf("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.Ordinal) >= 0)
        {
            return "省电";
        }

        if (lower.IndexOf("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.Ordinal) >= 0)
        {
            return "性能";
        }

        if (lower.IndexOf("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.Ordinal) >= 0)
        {
            return "平衡";
        }

        if (lower.IndexOf("saver", StringComparison.Ordinal) >= 0 ||
            name.IndexOf("省电", StringComparison.Ordinal) >= 0 ||
            name.IndexOf("节能", StringComparison.Ordinal) >= 0)
        {
            return "省电";
        }

        if (lower.IndexOf("high", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("ultimate", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("performance", StringComparison.Ordinal) >= 0 ||
            name.IndexOf("性能", StringComparison.Ordinal) >= 0 ||
            name.IndexOf("卓越", StringComparison.Ordinal) >= 0)
        {
            return "性能";
        }

        if (lower.IndexOf("balanced", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("recommended", StringComparison.Ordinal) >= 0 ||
            name.IndexOf("平衡", StringComparison.Ordinal) >= 0)
        {
            return "平衡";
        }

        return "平衡";
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
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
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
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
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
            return watts.ToString("0", CultureInfo.InvariantCulture) + " W";
        }

        return watts.ToString("0.0", CultureInfo.InvariantCulture) + " W";
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

        string trimmed = name.Trim();
        int slash = trimmed.LastIndexOf('\\');
        int dot = trimmed.LastIndexOf('.');
        int start = Math.Max(slash, dot);
        if (start >= 0 && start < trimmed.Length - 1)
        {
            trimmed = trimmed.Substring(start + 1);
        }

        return trimmed.Length == 0 ? "TZ" : trimmed;
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
                DrawBackground(this.renderGraphics);
                DrawContentLayer(this.renderGraphics);
                this.renderBufferValid = true;
            }

            // Hover opacity changes only the global alpha, so redrawContent can be false.
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
                    Program.LogInfo("PowerThermal UpdateLayeredWindow failed; falling back to normal paint.");
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

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.PowerThermalTransparencyPercent) / 100.0);
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
