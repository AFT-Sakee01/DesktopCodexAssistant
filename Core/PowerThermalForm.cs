using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.System.Power;

internal sealed partial class PowerThermalForm : LayeredWidgetFormBase
{
    private const int SamplingTimerBoundaryOffsetMs = 30;
    private readonly System.Windows.Forms.Timer timer;
    // WMI access is single-flight. Forced events are coalesced into these pending flags.
    private readonly object samplingSync = new object();
    private readonly Dictionary<string, DateTime> thermalCriticalSinceUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> thermalAlertNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Headless ownership is deliberately independent from fullscreen visibility. The power tile
    // still needs this form's sampler and power-notification HWND after the retired window stops
    // being shown, while fullscreen hiding remains a presentation-only state.
    // This type is permanently headless. The readonly invariant blocks any stale Show call while
    // preserving its sampler and notification HWND for the Power metric tile.
    private readonly bool headlessDataOwner = true;
    private bool dataOwnerRuntimeStarted;
    private bool ownedRuntimeResourcesDisposed;
    private bool samplingWorkerRunning;
    private bool pendingPowerSample;
    private bool pendingThermalSample;
    private bool formClosing;
    private bool sessionActive = true;
    private bool displayActive = true;
    private bool powerSuspended;
    private bool displayResumePrimePending;
    private int displayResumePrimeCountForSelfTest;
    // Native registrations wake the sampler immediately; timed sampling remains the fallback.
    private IntPtr displayPowerNotificationHandle;
    private IntPtr acDcPowerNotificationHandle;
    private IntPtr batteryPowerNotificationHandle;
    private IntPtr powerSchemeNotificationHandle;
    private IntPtr energySaverNotificationHandle;
    private IntPtr effectivePowerModeNotificationHandle;
    private NativeMethods.EffectivePowerModeCallback effectivePowerModeCallback;
    private PowerReading cachedPowerReading;
    private DateTime cachedPowerReadingUtc;
    private List<ThermalReading> cachedThermalReadings = new List<ThermalReading>();
    private DateTime cachedThermalReadingsUtc;

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
        public bool EnergySaverKnown;
        public bool EnergySaverEnabled;
        public bool BatteryCarePauseKnown;
        public bool BatteryCarePauseActive;
        // Windows only estimates remaining runtime on battery; on AC it reports -1.
        public bool RuntimeSecondsKnown;
        public int RuntimeSeconds;
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
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.MinimumSize = new Size(1, 1);
        this.MaximumSize = new Size(1, 1);
        this.Size = new Size(1, 1);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextSamplingTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.effectivePowerModeCallback = OnEffectivePowerModeChanged;
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // SetVisibleCore normally prevents this path. Keep the defensive hide so a future
        // WinForms lifecycle change cannot resurrect the retired window.
        if (this.Visible)
        {
            this.Hide();
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        // Headless data ownership must remain stronger than any legacy Show call.
        base.SetVisibleCore(false);
    }

    // Starts the sampler as a permanently hidden data owner. Call this on the WinForms UI thread:
    // creating the handle is required so display/power lifecycle notifications keep working even
    // though Show/OnShown is never used.
    public void StartHeadlessDataOwner()
    {
        if (this.IsDisposed || this.ownedRuntimeResourcesDisposed)
        {
            throw new ObjectDisposedException(nameof(PowerThermalForm));
        }

        if (this.IsHandleCreated && this.InvokeRequired)
        {
            this.Invoke((MethodInvoker)StartHeadlessDataOwner);
            return;
        }

        if (this.headlessDataOwner && this.dataOwnerRuntimeStarted)
        {
            return;
        }

        if (this.Visible)
        {
            this.Hide();
        }

        // Accessing Handle creates the hidden message target and runs OnHandleCreated, which owns
        // the existing console-display, battery, power-scheme, and effective-mode registrations.
        if (this.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Power data-owner notification handle was not created.");
        }

        ApplyRuntimeSettings(this.CurrentSettings);
        StartDataOwnerRuntime();
    }

    // Final shutdown for the headless owner. The application creates one owner for its lifetime,
    // so Stop intentionally tears down registrations/timers and is idempotent rather than being a
    // pause/resume switch. Dispose and OnFormClosed share this exact cleanup path.
    public void StopHeadlessDataOwner()
    {
        if (this.IsDisposed || this.ownedRuntimeResourcesDisposed)
        {
            return;
        }

        if (this.IsHandleCreated && this.InvokeRequired)
        {
            this.Invoke((MethodInvoker)StopHeadlessDataOwner);
            return;
        }

        if (this.Visible)
        {
            this.Hide();
        }

        DisposeOwnedRuntimeResources();
    }

    private void StartDataOwnerRuntime()
    {
        if (this.formClosing || this.ownedRuntimeResourcesDisposed)
        {
            return;
        }

        if (!this.dataOwnerRuntimeStarted)
        {
            this.dataOwnerRuntimeStarted = true;
            this.timer.Start();
        }

        RequestSampling(true, true, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (this.formClosing || this.ownedRuntimeResourcesDisposed)
        {
            return;
        }

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
        this.energySaverNotificationHandle = NativeMethods.RegisterPowerSettingNotificationForWindow(
            this.Handle,
            NativeMethods.GUID_POWER_SAVING_STATUS);
        NativeMethods.TryRegisterEffectivePowerModeNotification(
            this.effectivePowerModeCallback,
            out this.effectivePowerModeNotificationHandle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterPowerLifecycleNotifications();
        base.OnHandleDestroyed(e);
    }

    private void UnregisterPowerLifecycleNotifications()
    {
        NativeMethods.UnregisterPowerNotification(this.displayPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.acDcPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.batteryPowerNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.powerSchemeNotificationHandle);
        NativeMethods.UnregisterPowerNotification(this.energySaverNotificationHandle);
        NativeMethods.UnregisterEffectivePowerModeNotification(this.effectivePowerModeNotificationHandle);
        this.displayPowerNotificationHandle = IntPtr.Zero;
        this.acDcPowerNotificationHandle = IntPtr.Zero;
        this.batteryPowerNotificationHandle = IntPtr.Zero;
        this.powerSchemeNotificationHandle = IntPtr.Zero;
        this.energySaverNotificationHandle = IntPtr.Zero;
        this.effectivePowerModeNotificationHandle = IntPtr.Zero;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeOwnedRuntimeResources();
        base.OnFormClosed(e);
    }

    private void DisposeOwnedRuntimeResources()
    {
        if (this.ownedRuntimeResourcesDisposed)
        {
            return;
        }

        this.ownedRuntimeResourcesDisposed = true;
        this.formClosing = true;
        this.dataOwnerRuntimeStarted = false;
        lock (this.samplingSync)
        {
            this.pendingPowerSample = false;
            this.pendingThermalSample = false;
        }

        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        UnregisterPowerLifecycleNotifications();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Direct Dispose on a never-shown Form does not raise FormClosed. Sharing the cleanup
            // prevents SystemEvents, native callbacks, and timers from retaining the data owner.
            DisposeOwnedRuntimeResources();
        }

        base.Dispose(disposing);
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
            this.displayResumePrimePending = true;
            ScheduleNextSamplingTick();
            return;
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            this.sessionActive = true;
            TryPrimeDisplayResumeOnce();
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.powerSuspended = true;
            this.displayResumePrimePending = true;
            ScheduleNextSamplingTick();
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            this.powerSuspended = false;
            this.displayActive = true;
            TryPrimeDisplayResumeOnce();
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
                    TryPrimeDisplayResumeOnce();
                }
                else
                {
                    this.displayResumePrimePending = true;
                    ScheduleNextSamplingTick();
                }
            }

            return;
        }

        if (setting.PowerSetting == NativeMethods.GUID_ACDC_POWER_SOURCE ||
            setting.PowerSetting == NativeMethods.GUID_BATTERY_PERCENTAGE_REMAINING ||
            setting.PowerSetting == NativeMethods.GUID_POWERSCHEME_PERSONALITY ||
            setting.PowerSetting == NativeMethods.GUID_POWER_SAVING_STATUS)
        {
            RequestSampling(true, false, true);
        }
    }

    private void OnEffectivePowerModeChanged(int mode, IntPtr context)
    {
        WidgetSettings.InvalidateEffectivePerformanceModeCache();
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
        // Sampling belongs exclusively to the explicitly started headless owner. Visibility is
        // intentionally absent from this gate so fullscreen/layout policies cannot starve tiles.
        return !this.formClosing &&
            this.headlessDataOwner &&
            this.dataOwnerRuntimeStarted &&
            !this.ownedRuntimeResourcesDisposed &&
            this.sessionActive &&
            this.displayActive &&
            !this.powerSuspended;
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        ThermalTestMode oldThermalTestMode = this.CurrentSettings.ThermalTestMode;
        WidgetPerformanceMode oldPerformanceMode = this.CurrentSettings.PerformanceMode;
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        if (oldThermalTestMode != this.CurrentSettings.ThermalTestMode)
        {
            this.thermalCriticalSinceUtc.Clear();
            this.thermalAlertNames.Clear();
            this.cachedThermalReadingsUtc = DateTime.MinValue;
        }

        ScheduleNextSamplingTick();
        // Thermal-test mode and performance mode remain live data settings; geometry, z-order,
        // interaction, burn-in, and layered-render work are permanently retired.
        if (this.Visible)
        {
            this.Hide();
        }

        if (oldThermalTestMode != this.CurrentSettings.ThermalTestMode ||
            oldPerformanceMode != this.CurrentSettings.PerformanceMode)
        {
            RequestSampling(true, true, true);
        }
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        // Kept as a lifecycle seam for the shared visibility coordinator. The owner never becomes
        // visible and this flag never controls sampling.
        if (this.Visible)
        {
            this.Hide();
        }
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
        TryPrimeDisplayResumeOnce();
        ScheduleNextSamplingTick();
    }

    public void PrepareForDisplaySuspend()
    {
        this.powerSuspended = true;
        this.displayActive = false;
        this.displayResumePrimePending = true;
    }

    private void TryPrimeDisplayResumeOnce()
    {
        if (!this.displayResumePrimePending || !IsSamplingAllowed())
        {
            return;
        }

        this.displayResumePrimePending = false;
        unchecked
        {
            this.displayResumePrimeCountForSelfTest++;
        }

        this.cachedPowerReadingUtc = DateTime.MinValue;
        this.cachedThermalReadingsUtc = DateTime.MinValue;
        RequestSampling(true, true, true);
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
            ScheduleNextSamplingTick();
        }
    }

    private void ScheduleNextSamplingTick()
    {
        int interval = GetNextSamplingTickIntervalMs();
        if (this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private int GetNextSamplingTickIntervalMs()
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
        return Math.Max(50, Math.Min(5000, interval + SamplingTimerBoundaryOffsetMs));
    }

    private SamplingPolicy GetSamplingPolicy()
    {
        // "Smooth" is the legacy persisted enum name for the user-facing Performance mode.
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode);
        SamplingPolicy policy = new SamplingPolicy();
        if (mode == WidgetPerformanceMode.Smooth)
        {
            policy.PowerIntervalMs = 1000;
            policy.ThermalIntervalMs = 2000;
            policy.WarmThermalIntervalMs = 1500;
            policy.AlertThermalIntervalMs = 1000;
            policy.CriticalThermalIntervalMs = 1000;
            return policy;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
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
            ScheduleNextSamplingTick();
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

        ThermalTestMode thermalTestMode = this.CurrentSettings.ThermalTestMode;
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
        // Cache replacement and alert-state transitions stay on the UI thread. The owner has no
        // presentation state, so a completed sample never performs layout, paint, or hover work.
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
                this.CurrentSettings.ThermalTestMode != ThermalTestMode.Off);
        }

        lock (this.samplingSync)
        {
            this.samplingWorkerRunning = false;
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
            ScheduleNextSamplingTick();
        }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return false;
    }

    // LayeredWidgetFormBase requires a draw hook, but this owner never owns a visible surface.
    protected override void DrawWindowContent(Graphics g)
    {
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

            int runtimeSeconds = powerStatus.BatteryLifeRemaining;
            if (runtimeSeconds > 0)
            {
                reading.RuntimeSecondsKnown = true;
                reading.RuntimeSeconds = runtimeSeconds;
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

        UpdateEnergySaverState(ref reading);
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

    private static void UpdateEnergySaverState(ref PowerReading reading)
    {
        bool energySaverEnabled;
        if (TryReadEnergySaverEnabled(out energySaverEnabled))
        {
            reading.EnergySaverKnown = true;
            reading.EnergySaverEnabled = energySaverEnabled;
        }
    }

    private static bool TryReadEnergySaverEnabled(out bool enabled)
    {
        bool known = false;
        enabled = false;
        try
        {
            EnergySaverStatus status = PowerManager.EnergySaverStatus;
            known = true;
            enabled = status == EnergySaverStatus.On;
        }
        catch
        {
        }

        bool batterySaverEnabled;
        if (NativeMethods.TryGetBatterySaverStatus(out batterySaverEnabled))
        {
            known = true;
            enabled = enabled || batterySaverEnabled;
        }

        return known;
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

    internal static string ReadCurrentSystemPowerModeText()
    {
        bool pluggedInKnown = false;
        bool pluggedIn = false;
        try
        {
            PowerLineStatus lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                pluggedInKnown = true;
                pluggedIn = lineStatus == PowerLineStatus.Online;
            }
        }
        catch
        {
        }

        string powerModeText = ReadSystemPowerModeText(pluggedInKnown, pluggedIn);
        bool energySaverEnabled;
        if (TryReadEnergySaverEnabled(out energySaverEnabled) && energySaverEnabled)
        {
            return AppendEnergySaverSuffix(powerModeText);
        }

        return powerModeText;
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

}
