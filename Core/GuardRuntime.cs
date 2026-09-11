using System;
using System.Globalization;
using System.Net.NetworkInformation;

// The three-position Windows power-mode slider (registry ActiveOverlay*PowerScheme / powrprof
// overlay scheme). Unknown covers both "not yet read" and a custom vendor scheme this app does
// not recognize; GUARD never guesses a tier in that case.
internal enum GuardPowerModeTier
{
    Unknown = 0,
    Saver = 1,
    Balanced = 2,
    Performance = 3
}

// Power and program guard state machine behind the GUARD board (scheme D).
//
// This is a native C# reimplementation of the CodexSleepGuard PowerShell utility
// (E:\Codexproject\desktopdata\CodexSleepGuard). The board draws a live countdown ring, which an
// out-of-process script cannot feed without an IPC channel, so the three power guards moved
// in-process; no external SleepGuard launcher is retained in the Operation surface
// for users who want it outside this app.
//
// Thread affinity is the sharp edge here. SetThreadExecutionState registers against the *calling
// thread*, and Windows clears the registration when that thread exits. Every mutation therefore has
// to happen on the UI thread; GuardRuntime never touches the flags from a pool thread, and the
// owning form is responsible for calling Tick from its maintenance timer.
internal sealed class GuardRuntime
{
    // MyASUS pauses battery care for a fixed 24 hours per acin_set invocation, then restores the
    // 80% ceiling on its own. We cannot read that deadline back from MyASUS, so this clock is our
    // own record of the command request or an observed <=80 -> >80 edge, never vendor readback.
    internal const int BatteryCarePauseHours = 24;

    // The inner ring gauges how long the sleep guard has been held. There is no natural maximum for
    // that, so it sweeps across a 12 hour reference window and then stays full — long enough to
    // cover an overnight run without the arc looking pinned on a normal afternoon.
    internal const int SleepGuardGaugeHours = 12;

    private readonly Func<bool?> onlineProvider;
    private readonly Func<Tuple<bool, string>> sleepRequester;
    private bool sleepGuardEnabled;
    private DateTime sleepGuardSinceUtc = DateTime.MinValue;
    private DateTime displayGuardUntilUtc = DateTime.MinValue;
    private DateTime batteryCarePauseUntilUtc = DateTime.MinValue;
    private int? lastBatteryPercent;
    private DateTime offlineSinceUtc = DateTime.MinValue;
    private DateTime lastAutoSleepUtc = DateTime.MinValue;
    private DateTime nextExecutionRepairUtc = DateTime.MinValue;
    private int displayGuardMinutes = WidgetSettings.DefaultGuardDisplayMinutes;
    private int offlineThresholdMinutes = WidgetSettings.DefaultGuardOfflineThresholdMinutes;
    private string lastActionDetail = string.Empty;
    // Scheduled power-mode override: locks whichever tier is active when armed and always reverts
    // to Balanced at the deadline, so unlike displayGuardUntilUtc there is no separate "restore
    // tier" field to keep in sync - Balanced is a fixed, well-known, predictable revert target
    // even if the user manually changed modes again partway through a multi-hour window.
    private DateTime powerModeOverrideUntilUtc = DateTime.MinValue;
    private int powerModeOverrideHours = WidgetSettings.DefaultGuardPowerModeOverrideHours;
    // Energy Saver force is a plain on/off with no deadline (the user asked to switch it, not to
    // schedule it). RestoreThresholdPercent remembers the ESBATTTHRESHOLD value from just before
    // forcing it to 100 so toggling off does not overwrite the user's own auto-enable preference.
    private bool energySaverForcedOn;
    private int energySaverRestoreThresholdPercent = -1;

    // Modern Standby fix: the persistent power request is what actually holds an S0 machine in the
    // active phase. SetThreadExecutionState alone was suspended with other desktop apps when the
    // display powered off, which is why the old sleep prevention was ineffective on this hardware.
    private readonly NativeMethods.PowerRequestGuard powerRequests = new NativeMethods.PowerRequestGuard();

    internal GuardRuntime(Func<bool?> onlineProvider)
        : this(onlineProvider, null)
    {
    }

    // The requester seam is used by the self-test so an auto-sleep regression can be exercised
    // without ever sending a real suspend request to the developer's machine.
    internal GuardRuntime(Func<bool?> onlineProvider, Func<Tuple<bool, string>> sleepRequester)
    {
        this.onlineProvider = onlineProvider;
        this.sleepRequester = sleepRequester;
    }

    internal bool SleepGuardEnabled
    {
        get { return this.sleepGuardEnabled; }
    }

    internal bool DisplayGuardActive
    {
        get { return this.displayGuardUntilUtc != DateTime.MinValue; }
    }

    internal DateTime DisplayGuardUntilUtc
    {
        get { return this.displayGuardUntilUtc; }
    }

    internal DateTime BatteryCarePauseUntilUtc
    {
        get { return this.batteryCarePauseUntilUtc; }
    }

    internal bool BatteryCarePauseActive
    {
        get { return this.batteryCarePauseUntilUtc != DateTime.MinValue; }
    }

    internal DateTime OfflineSinceUtc
    {
        get { return this.offlineSinceUtc; }
    }

    internal DateTime SleepGuardSinceUtc
    {
        get { return this.sleepGuardSinceUtc; }
    }

    internal TimeSpan GetSleepGuardElapsed(DateTime nowUtc)
    {
        if (!this.sleepGuardEnabled || this.sleepGuardSinceUtc == DateTime.MinValue)
        {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed = nowUtc - this.sleepGuardSinceUtc;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    // Fraction of the 12h reference window the sleep guard has been held, 0..1. Drives the inner ring.
    internal float GetSleepGuardProgress(DateTime nowUtc)
    {
        if (!this.sleepGuardEnabled)
        {
            return 0.0f;
        }

        double elapsed = GetSleepGuardElapsed(nowUtc).TotalSeconds;
        return (float)Math.Max(0.0, Math.Min(1.0, elapsed / (SleepGuardGaugeHours * 3600.0)));
    }

    internal bool Online
    {
        get { return this.offlineSinceUtc == DateTime.MinValue; }
    }

    internal int DisplayGuardMinutes
    {
        get { return this.displayGuardMinutes; }
    }

    internal int OfflineThresholdMinutes
    {
        get { return this.offlineThresholdMinutes; }
    }

    internal string LastActionDetail
    {
        get { return this.lastActionDetail; }
    }

    internal bool PowerModeOverrideActive
    {
        get { return this.powerModeOverrideUntilUtc != DateTime.MinValue; }
    }

    internal DateTime PowerModeOverrideUntilUtc
    {
        get { return this.powerModeOverrideUntilUtc; }
    }

    internal int PowerModeOverrideHours
    {
        get { return this.powerModeOverrideHours; }
    }

    internal bool EnergySaverForcedOn
    {
        get { return this.energySaverForcedOn; }
    }

    internal TimeSpan GetPowerModeOverrideRemaining(DateTime nowUtc)
    {
        return Remaining(this.powerModeOverrideUntilUtc, nowUtc);
    }

    // The three Modern Standby power requests actually held right now. The board surfaces these so
    // the fix that keeps this machine awake is visible rather than implicit. They derive from the
    // armed guards, but reflect what the OS accepted rather than merely what was asked for: if the
    // power API failed the flag stays false even though the guard reads as enabled.
    internal bool SystemPowerRequestActive
    {
        get { return this.powerRequests.SystemActive; }
    }

    internal bool ExecutionPowerRequestActive
    {
        get { return this.powerRequests.ExecutionActive; }
    }

    internal bool DisplayPowerRequestActive
    {
        get { return this.powerRequests.DisplayActive; }
    }

    // Fraction of the configured display-guard window still remaining, 0..1. Drives the outer ring.
    internal float GetDisplayGuardProgress(DateTime nowUtc)
    {
        if (this.displayGuardUntilUtc == DateTime.MinValue || this.displayGuardMinutes <= 0)
        {
            return 0.0f;
        }

        double total = this.displayGuardMinutes * 60.0;
        double remaining = (this.displayGuardUntilUtc - nowUtc).TotalSeconds;
        if (remaining <= 0.0)
        {
            return 0.0f;
        }

        return (float)Math.Max(0.0, Math.Min(1.0, remaining / total));
    }

    // Fraction of the offline threshold already elapsed, 0..1. Drives the offline bar marker: it
    // sits at 0 while online and walks right as the outage approaches the auto-sleep deadline.
    internal float GetOfflineProgress(DateTime nowUtc)
    {
        if (this.offlineSinceUtc == DateTime.MinValue || this.offlineThresholdMinutes <= 0)
        {
            return 0.0f;
        }

        double total = this.offlineThresholdMinutes * 60.0;
        double elapsed = (nowUtc - this.offlineSinceUtc).TotalSeconds;
        return (float)Math.Max(0.0, Math.Min(1.0, elapsed / total));
    }

    // Fraction of the 24h battery-care pause already elapsed, 0..1. Drives the battery bar that
    // sits directly under the offline bar.
    internal float GetBatteryCarePauseProgress(DateTime nowUtc)
    {
        if (this.batteryCarePauseUntilUtc == DateTime.MinValue)
        {
            return 0.0f;
        }

        double total = BatteryCarePauseHours * 3600.0;
        double remaining = (this.batteryCarePauseUntilUtc - nowUtc).TotalSeconds;
        if (remaining <= 0.0)
        {
            return 1.0f;
        }

        return (float)Math.Max(0.0, Math.Min(1.0, 1.0 - remaining / total));
    }

    internal TimeSpan GetDisplayGuardRemaining(DateTime nowUtc)
    {
        return Remaining(this.displayGuardUntilUtc, nowUtc);
    }

    internal TimeSpan GetBatteryCarePauseRemaining(DateTime nowUtc)
    {
        return Remaining(this.batteryCarePauseUntilUtc, nowUtc);
    }

    internal TimeSpan GetOfflineElapsed(DateTime nowUtc)
    {
        if (this.offlineSinceUtc == DateTime.MinValue)
        {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed = nowUtc - this.offlineSinceUtc;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private static TimeSpan Remaining(DateTime untilUtc, DateTime nowUtc)
    {
        if (untilUtc == DateTime.MinValue)
        {
            return TimeSpan.Zero;
        }

        TimeSpan remaining = untilUtc - nowUtc;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    internal static string FormatCountdown(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0:00:00";
        }

        return ((int)value.TotalHours).ToString(CultureInfo.InvariantCulture) +
            value.ToString("\\:mm\\:ss", CultureInfo.InvariantCulture);
    }

    // Settings carry the durations plus the two absolute deadlines. The sleep guard preference is
    // restored too: this tool exists so a long Codex run survives the night, and silently dropping
    // the guard across an app restart is the failure this board is meant to make impossible. The
    // display deadline is only restored while it is still in the future.
    internal void LoadFromSettings(WidgetSettings settings, DateTime nowUtc)
    {
        if (settings == null)
        {
            return;
        }

        this.displayGuardMinutes = settings.GuardDisplayMinutes;
        this.offlineThresholdMinutes = settings.GuardOfflineThresholdMinutes;
        this.batteryCarePauseUntilUtc = NormalizeDeadline(settings.GuardBatteryCarePauseUntilUtcTicks, nowUtc);
        this.displayGuardUntilUtc = NormalizeDeadline(settings.GuardDisplayUntilUtcTicks, nowUtc);
        // Display and sleep are independent. A legacy file may contain both, but a future display
        // deadline must never silently arm sleep protection during load.
        this.sleepGuardEnabled = settings.GuardSleepEnabled;
        // Unlike the two deadlines this is a *start* stamp, so it is only valid in the past. A
        // future or absent value falls back to now, which reads as "just armed" rather than
        // inventing an elapsed time the guard never actually held.
        this.sleepGuardSinceUtc = this.sleepGuardEnabled
            ? NormalizeStartStamp(settings.GuardSleepSinceUtcTicks, nowUtc)
            : DateTime.MinValue;
        ApplyExecutionState();

        this.powerModeOverrideHours = WidgetSettings.NormalizeGuardPowerModeOverrideHours(settings.GuardPowerModeOverrideHours);
        DateTime overrideDeadline = NormalizeDeadline(settings.GuardPowerModeOverrideUntilUtcTicks, nowUtc);
        if (overrideDeadline != DateTime.MinValue)
        {
            // A future deadline just resumes counting down; Tick will fire the normal revert-to-
            // balanced expiry path later, exactly as if the app had stayed open the whole time.
            this.powerModeOverrideUntilUtc = overrideDeadline;
        }
        else if (settings.GuardPowerModeOverrideUntilUtcTicks > 0L)
        {
            // Unlike a power *request* handle, an overlay-scheme write has no OS-side owner that
            // quietly lets go when this process exits. A deadline discovered already in the past
            // means the window elapsed while the app was closed, so the revert-to-balanced action
            // that Tick would have fired must still run once here, or Windows is left in the
            // boosted tier indefinitely with no other code path left to notice.
            string staleRevertDetail;
            NativeMethods.TrySetActivePowerOverlayScheme(NativeMethods.PowerOverlaySchemeBalanced, out staleRevertDetail);
            Program.LogInfo("Guard power mode override had expired while closed; reverted to balanced on load.");
        }

        this.energySaverForcedOn = settings.GuardEnergySaverForcedOn;
        this.energySaverRestoreThresholdPercent = settings.GuardEnergySaverRestoreThresholdPercent;
        if (this.energySaverForcedOn)
        {
            // Best-effort repair, mirroring RefreshExecutionState's philosophy for the ES/PowerRequest
            // layer: re-assert in case Windows or another tool reset the threshold while this app was
            // closed. Never throws; a failure just leaves the board showing it as on so the user can
            // retry the toggle instead of the state silently drifting out of sync with reality.
            string forceDetail;
            NativeMethods.TryWriteEnergySaverBatteryThresholdPercent(100, out forceDetail);
        }
    }

    internal void SaveToSettings(WidgetSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        settings.GuardDisplayMinutes = this.displayGuardMinutes;
        settings.GuardOfflineThresholdMinutes = this.offlineThresholdMinutes;
        settings.GuardSleepEnabled = this.sleepGuardEnabled;
        settings.GuardSleepSinceUtcTicks = this.sleepGuardSinceUtc == DateTime.MinValue ? 0L : this.sleepGuardSinceUtc.Ticks;
        settings.GuardDisplayUntilUtcTicks = this.displayGuardUntilUtc == DateTime.MinValue ? 0L : this.displayGuardUntilUtc.Ticks;
        settings.GuardBatteryCarePauseUntilUtcTicks = this.batteryCarePauseUntilUtc == DateTime.MinValue ? 0L : this.batteryCarePauseUntilUtc.Ticks;
        settings.GuardPowerModeOverrideHours = this.powerModeOverrideHours;
        settings.GuardPowerModeOverrideUntilUtcTicks = this.powerModeOverrideUntilUtc == DateTime.MinValue ? 0L : this.powerModeOverrideUntilUtc.Ticks;
        settings.GuardEnergySaverForcedOn = this.energySaverForcedOn;
        settings.GuardEnergySaverRestoreThresholdPercent = this.energySaverRestoreThresholdPercent;
    }

    private static DateTime NormalizeDeadline(long ticks, DateTime nowUtc)
    {
        if (ticks <= 0L || ticks > DateTime.MaxValue.Ticks)
        {
            return DateTime.MinValue;
        }

        DateTime deadline = new DateTime(ticks, DateTimeKind.Utc);
        return deadline <= nowUtc ? DateTime.MinValue : deadline;
    }

    private static DateTime NormalizeStartStamp(long ticks, DateTime nowUtc)
    {
        if (ticks <= 0L || ticks > DateTime.MaxValue.Ticks)
        {
            return nowUtc;
        }

        DateTime stamp = new DateTime(ticks, DateTimeKind.Utc);
        return stamp > nowUtc ? nowUtc : stamp;
    }

    internal bool SetSleepGuard(bool enabled)
    {
        if (this.sleepGuardEnabled == enabled)
        {
            return false;
        }

        this.sleepGuardEnabled = enabled;
        if (enabled)
        {
            this.sleepGuardSinceUtc = DateTime.UtcNow;
        }
        else this.sleepGuardSinceUtc = DateTime.MinValue;

        ApplyExecutionState();
        Program.LogInfo("Guard sleep protection set. Enabled=" + enabled.ToString());
        return true;
    }

    // Reasserts the desired request set without changing user state. Display/session recovery and
    // an idempotent CLI "on" command use this to repair an OS-side request that disappeared while
    // the persisted toggle remained enabled.
    internal void RefreshExecutionState(bool recreatePowerRequestHandle)
    {
        if (recreatePowerRequestHandle)
        {
            this.powerRequests.Release();
        }

        ApplyExecutionState();
    }

    internal bool StartDisplayGuard(DateTime nowUtc)
    {
        if (this.displayGuardMinutes <= 0)
        {
            return false;
        }

        this.displayGuardUntilUtc = nowUtc.AddMinutes(this.displayGuardMinutes);
        ApplyExecutionState();
        Program.LogInfo(
            "Guard display timer started. Minutes=" +
            this.displayGuardMinutes.ToString(CultureInfo.InvariantCulture) +
            ", UntilUtc=" +
            this.displayGuardUntilUtc.ToString("o", CultureInfo.InvariantCulture));
        return true;
    }

    internal bool StopDisplayGuard()
    {
        if (this.displayGuardUntilUtc == DateTime.MinValue)
        {
            return false;
        }

        this.displayGuardUntilUtc = DateTime.MinValue;
        ApplyExecutionState();
        Program.LogInfo("Guard display timer stopped.");
        return true;
    }

    internal bool SetDisplayGuardMinutes(int minutes)
    {
        int normalized = WidgetSettings.NormalizeGuardDisplayMinutes(minutes);
        if (this.displayGuardMinutes == normalized)
        {
            return false;
        }

        this.displayGuardMinutes = normalized;
        // Re-arming a running timer against the new duration is what the user means by changing
        // the step while it counts down; leaving the old deadline would be silently ignoring them.
        if (this.displayGuardUntilUtc != DateTime.MinValue)
        {
            this.displayGuardUntilUtc = DateTime.UtcNow.AddMinutes(normalized);
        }

        return true;
    }

    internal bool SetOfflineThresholdMinutes(int minutes)
    {
        int normalized = WidgetSettings.NormalizeGuardOfflineThresholdMinutes(minutes);
        if (this.offlineThresholdMinutes == normalized)
        {
            return false;
        }

        this.offlineThresholdMinutes = normalized;
        return true;
    }

    internal static Guid ResolveOverlaySchemeGuid(GuardPowerModeTier tier)
    {
        switch (tier)
        {
            case GuardPowerModeTier.Saver:
                return NativeMethods.PowerOverlaySchemeSaver;
            case GuardPowerModeTier.Performance:
                return NativeMethods.PowerOverlaySchemePerformance;
            default:
                return NativeMethods.PowerOverlaySchemeBalanced;
        }
    }

    internal static GuardPowerModeTier ClassifyOverlaySchemeGuid(Guid overlaySchemeGuid)
    {
        if (overlaySchemeGuid == NativeMethods.PowerOverlaySchemeSaver)
        {
            return GuardPowerModeTier.Saver;
        }

        if (overlaySchemeGuid == NativeMethods.PowerOverlaySchemePerformance)
        {
            return GuardPowerModeTier.Performance;
        }

        if (overlaySchemeGuid == NativeMethods.PowerOverlaySchemeBalanced)
        {
            return GuardPowerModeTier.Balanced;
        }

        return GuardPowerModeTier.Unknown;
    }

    internal static string DescribeTier(GuardPowerModeTier tier)
    {
        switch (tier)
        {
            case GuardPowerModeTier.Saver:
                return "省电";
            case GuardPowerModeTier.Balanced:
                return "平衡";
            case GuardPowerModeTier.Performance:
                return "性能";
            default:
                return "未知";
        }
    }

    // English lowercase wire-format counterpart to DescribeTier, matching the request-side "mode"
    // token vocabulary in GuardControlProtocol so CLI callers never have to parse Chinese text.
    internal static string DescribeTierForWire(GuardPowerModeTier tier)
    {
        switch (tier)
        {
            case GuardPowerModeTier.Saver:
                return "saver";
            case GuardPowerModeTier.Balanced:
                return "balanced";
            case GuardPowerModeTier.Performance:
                return "performance";
            default:
                return "unknown";
        }
    }

    // Reads the live overlay scheme rather than trusting any cached field, since Windows (or the
    // user, via its own Settings UI) can change this at any time outside GUARD's control.
    internal static GuardPowerModeTier GetLivePowerModeTier()
    {
        Guid overlaySchemeGuid;
        return NativeMethods.TryGetActivePowerOverlayScheme(out overlaySchemeGuid)
            ? ClassifyOverlaySchemeGuid(overlaySchemeGuid)
            : GuardPowerModeTier.Unknown;
    }

    // Immediate, indefinite switch - the "quick switch" request. A manual switch always overrides
    // any pending scheduled reversion: leaving the old deadline armed would silently undo the
    // user's explicit choice a few hours later, which is a worse surprise than losing the timer.
    internal bool SetPowerMode(GuardPowerModeTier tier)
    {
        string detail;
        bool applied = NativeMethods.TrySetActivePowerOverlayScheme(ResolveOverlaySchemeGuid(tier), out detail);
        bool cancelledOverride = this.powerModeOverrideUntilUtc != DateTime.MinValue;
        this.powerModeOverrideUntilUtc = DateTime.MinValue;
        this.lastActionDetail = applied
            ? "电源模式已切换为" + DescribeTier(tier) + "。"
            : "切换电源模式失败：" + detail;
        Program.LogInfo("Guard power mode set. Tier=" + tier + ", Applied=" + applied.ToString());
        return applied || cancelledOverride;
    }

    // Arms the scheduled override: whichever tier is live right now stays in effect, and Tick
    // reverts to Balanced once the window elapses. Does not itself change the current tier - the
    // UI/CLI caller applies a tier first via SetPowerMode if a different one should be locked in.
    internal bool StartPowerModeOverride(int hours, DateTime nowUtc)
    {
        int normalized = WidgetSettings.NormalizeGuardPowerModeOverrideHours(hours);
        this.powerModeOverrideHours = normalized;
        this.powerModeOverrideUntilUtc = nowUtc.AddHours(normalized);
        this.lastActionDetail = "已锁定当前电源模式 " + normalized.ToString(CultureInfo.InvariantCulture) + " 小时，到点恢复至平衡。";
        Program.LogInfo(
            "Guard power mode override armed. Hours=" +
            normalized.ToString(CultureInfo.InvariantCulture) +
            ", UntilUtc=" +
            this.powerModeOverrideUntilUtc.ToString("o", CultureInfo.InvariantCulture));
        return true;
    }

    // Cancels the pending reversion without touching the current tier, mirroring StopDisplayGuard:
    // "stop babysitting this" is not the same request as "change it back right now".
    internal bool StopPowerModeOverride()
    {
        if (this.powerModeOverrideUntilUtc == DateTime.MinValue)
        {
            return false;
        }

        this.powerModeOverrideUntilUtc = DateTime.MinValue;
        this.lastActionDetail = "定时电源模式已取消，当前模式不受影响。";
        Program.LogInfo("Guard power mode override cancelled.");
        return true;
    }

    internal bool SetPowerModeOverrideHours(int hours)
    {
        int normalized = WidgetSettings.NormalizeGuardPowerModeOverrideHours(hours);
        if (this.powerModeOverrideHours == normalized)
        {
            return false;
        }

        this.powerModeOverrideHours = normalized;
        // Re-arming a running countdown against the new duration mirrors SetDisplayGuardMinutes:
        // leaving the old deadline in place would silently ignore the user's change while it counts.
        if (this.powerModeOverrideUntilUtc != DateTime.MinValue)
        {
            this.powerModeOverrideUntilUtc = DateTime.UtcNow.AddHours(normalized);
        }

        return true;
    }

    // Windows exposes no direct "turn Energy Saver on now" bit; ESBATTTHRESHOLD=100 is the same
    // mechanism community battery-saver toggle scripts use, since 100 is always >= the current
    // battery percentage. Because this overwrites the user's own auto-enable threshold, the
    // original value is captured once and restored exactly on toggle-off.
    internal bool SetEnergySaverForced(bool enabled)
    {
        if (this.energySaverForcedOn == enabled)
        {
            return false;
        }

        string detail;
        if (enabled)
        {
            if (this.energySaverRestoreThresholdPercent < 0)
            {
                // Only capture a restore point the first time. Re-reading "100" back after a prior
                // successful force-on (e.g. a second call after a crash restored the flag but not
                // this in-memory field) would clobber the real value with our own forced one.
                int currentThreshold;
                this.energySaverRestoreThresholdPercent = NativeMethods.TryReadEnergySaverBatteryThresholdPercent(out currentThreshold)
                    ? currentThreshold
                    : WidgetSettings.DefaultPowerThermalManualEnergySaverThresholdPercent;
            }

            if (!NativeMethods.TryWriteEnergySaverBatteryThresholdPercent(100, out detail))
            {
                this.lastActionDetail = "开启省电模式失败：" + detail;
                Program.LogInfo("Guard energy saver force-on failed. Detail=" + detail);
                return false;
            }

            this.energySaverForcedOn = true;
            this.lastActionDetail = "省电模式已开启。";
        }
        else
        {
            int restoreValue = this.energySaverRestoreThresholdPercent >= 0
                ? this.energySaverRestoreThresholdPercent
                : WidgetSettings.DefaultPowerThermalManualEnergySaverThresholdPercent;
            if (!NativeMethods.TryWriteEnergySaverBatteryThresholdPercent(restoreValue, out detail))
            {
                this.lastActionDetail = "关闭省电模式失败：" + detail;
                Program.LogInfo("Guard energy saver restore failed. Detail=" + detail);
                return false;
            }

            this.energySaverForcedOn = false;
            this.energySaverRestoreThresholdPercent = -1;
            this.lastActionDetail = "省电模式已关闭，恢复原自动阈值。";
        }

        Program.LogInfo("Guard energy saver forced state set. Enabled=" + enabled.ToString());
        return true;
    }

    // Command callers pass the click time only after a successful launch. Observations pass the
    // first crossing time. Neither path claims the vendor process actually applied the command.
    internal void NoteBatteryCarePaused(DateTime nowUtc)
    {
        this.batteryCarePauseUntilUtc = nowUtc.AddHours(BatteryCarePauseHours);
        Program.LogInfo(
            "Guard battery care pause recorded. UntilUtc=" +
            this.batteryCarePauseUntilUtc.ToString("o", CultureInfo.InvariantCulture));
    }

    internal bool ObserveBatteryPercent(bool known, int percent, DateTime nowUtc)
    {
        // An unknown/startup reading cannot establish an edge. Retaining only an in-memory
        // baseline prevents restart above 80% from inventing a fresh 24-hour pause window.
        int? previous = this.lastBatteryPercent;
        this.lastBatteryPercent = known && percent >= 0 && percent <= 100 ? (int?)percent : null;
        if (!previous.HasValue || !this.lastBatteryPercent.HasValue || previous.Value > 80 ||
            percent <= 80 || this.batteryCarePauseUntilUtc > nowUtc)
        {
            return false;
        }

        // Do not extend an active window for repeated samples or a later discharge/recharge.
        NoteBatteryCarePaused(nowUtc);
        Program.LogInfo("Battery care pause inferred from an observed upward 80 percent crossing.");
        return true;
    }

    // Render-harness only. The sample PNGs have to show a guard that has been held for hours, and
    // the only alternative would be waiting hours or letting the harness reach into private state.
    internal void BackdateSleepGuardForRenderSample(DateTime sinceUtc)
    {
        if (!this.sleepGuardEnabled)
        {
            SetSleepGuard(true);
        }

        this.sleepGuardSinceUtc = sinceUtc;
    }

    internal void NoteBatteryCareRestored()
    {
        this.batteryCarePauseUntilUtc = DateTime.MinValue;
        Program.LogInfo("Guard battery care restore recorded.");
    }

    // Returns true when visible state changed and the board needs a repaint. Countdown text
    // changes every second, so the owner ticks this at least that often while visible.
    internal bool Tick(DateTime nowUtc)
    {
        bool changed = false;

        if (this.displayGuardUntilUtc != DateTime.MinValue && nowUtc >= this.displayGuardUntilUtc)
        {
            this.displayGuardUntilUtc = DateTime.MinValue;
            this.lastActionDetail = "亮屏计时到点，已交还 Windows 超时。";
            ApplyExecutionState();
            Program.LogInfo("Guard display timer expired; display sleep returned to Windows.");
            changed = true;
        }

        if (this.batteryCarePauseUntilUtc != DateTime.MinValue && nowUtc >= this.batteryCarePauseUntilUtc)
        {
            this.batteryCarePauseUntilUtc = DateTime.MinValue;
            this.lastActionDetail = "电池保护 24 小时暂停已到期。";
            Program.LogInfo("Guard battery care pause window elapsed.");
            changed = true;
        }

        if (this.powerModeOverrideUntilUtc != DateTime.MinValue && nowUtc >= this.powerModeOverrideUntilUtc)
        {
            this.powerModeOverrideUntilUtc = DateTime.MinValue;
            string revertDetail;
            bool reverted = NativeMethods.TrySetActivePowerOverlayScheme(NativeMethods.PowerOverlaySchemeBalanced, out revertDetail);
            this.lastActionDetail = reverted
                ? "定时电源模式到点，已恢复至平衡。"
                : "定时电源模式到点，但恢复平衡失败：" + revertDetail;
            Program.LogInfo("Guard power mode override expired; reverted to balanced. Reverted=" + reverted.ToString());
            changed = true;
        }

        changed |= UpdateOfflineState(nowUtc);
        if ((this.sleepGuardEnabled || this.displayGuardUntilUtc != DateTime.MinValue) &&
            nowUtc >= this.nextExecutionRepairUtc)
        {
            // Retry transient PowerSetRequest failures and refresh the thread-affine ES flags.
            // Sync is idempotent for requests already held, so this does not increment refcounts.
            ApplyExecutionState();
        }
        return changed;
    }

    private bool UpdateOfflineState(DateTime nowUtc)
    {
        bool? online = ResolveOnline();
        if (!online.HasValue)
        {
            // Unknown connectivity must never trigger an auto sleep. Treat it as online and let a
            // confirmed offline reading start the clock.
            if (this.offlineSinceUtc == DateTime.MinValue)
            {
                return false;
            }

            this.offlineSinceUtc = DateTime.MinValue;
            return true;
        }

        if (online.Value)
        {
            if (this.offlineSinceUtc == DateTime.MinValue)
            {
                return false;
            }

            this.offlineSinceUtc = DateTime.MinValue;
            return true;
        }

        if (this.offlineSinceUtc == DateTime.MinValue)
        {
            this.offlineSinceUtc = nowUtc;
            Program.LogInfo("Guard offline clock started.");
            return true;
        }

        if (this.offlineThresholdMinutes <= 0)
        {
            return false;
        }

        if (nowUtc < this.offlineSinceUtc.AddMinutes(this.offlineThresholdMinutes))
        {
            return true;
        }

        // Match the original CodexSleepGuard contract: connectivity is observed all the time, but
        // an outage may request sleep only while the user has explicitly armed a power guard.
        // Without this gate, every default installation would sleep after ten offline minutes.
        if (!this.sleepGuardEnabled)
        {
            return true;
        }

        RequestAutoSleep(nowUtc);
        return true;
    }

    // Guard against a resume loop: waking from the requested sleep with the network still down
    // would immediately re-arm the same threshold and put the machine back to sleep, so the offline
    // clock restarts from the wake and a fresh full threshold must elapse first.
    private void RequestAutoSleep(DateTime nowUtc)
    {
        if (this.lastAutoSleepUtc != DateTime.MinValue &&
            nowUtc < this.lastAutoSleepUtc.AddMinutes(Math.Max(1, this.offlineThresholdMinutes)))
        {
            return;
        }

        this.lastAutoSleepUtc = nowUtc;
        this.offlineSinceUtc = nowUtc;

        // Release every guard first. Requesting sleep while ES_SYSTEM_REQUIRED is still held is a
        // contradiction Windows resolves by ignoring one of them, and which one is not documented.
        this.sleepGuardEnabled = false;
        this.sleepGuardSinceUtc = DateTime.MinValue;
        this.displayGuardUntilUtc = DateTime.MinValue;
        ApplyExecutionState();

        string detail;
        bool requested = TryRequestSystemSleep(out detail);
        this.lastActionDetail = requested
            ? "离线超过阈值，已解除守护并请求系统睡眠。"
            : "请求系统睡眠失败：" + detail;
        Program.LogInfo(
            "Guard offline auto sleep requested. ThresholdMinutes=" +
            this.offlineThresholdMinutes.ToString(CultureInfo.InvariantCulture) +
            ", Success=" +
            requested.ToString() +
            ", Detail=" +
            detail);
    }

    private bool? ResolveOnline()
    {
        if (this.onlineProvider != null)
        {
            try
            {
                // null is a meaningful "unknown" result. Do not turn it into a coarse adapter
                // verdict: a false offline result can put the machine to sleep under the user.
                return this.onlineProvider();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                return null;
            }
        }

        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    private bool TryRequestSystemSleep(out string detail)
    {
        if (this.sleepRequester == null)
        {
            return NativeMethods.TryRequestSystemSleep(out detail);
        }

        try
        {
            Tuple<bool, string> result = this.sleepRequester();
            if (result == null)
            {
                detail = "test sleep requester returned null";
                return false;
            }

            detail = result.Item2 ?? string.Empty;
            return result.Item1;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    // A suspend interval can be longer than the offline threshold. Resetting the outage clock on
    // resume prevents the just-woken machine from immediately satisfying the old deadline and
    // falling into a sleep/resume loop while the network is still unavailable.
    internal bool OnSystemResume(DateTime nowUtc)
    {
        bool changed = this.offlineSinceUtc != DateTime.MinValue;
        this.offlineSinceUtc = DateTime.MinValue;
        this.lastAutoSleepUtc = nowUtc;
        // Windows may invalidate request state across suspend/session transitions. Drop our cached
        // handle state and rebuild both layers immediately on the long-lived UI thread.
        RefreshExecutionState(true);
        if (changed)
        {
            this.lastActionDetail = "系统已唤醒，断网倒计时重新开始。";
        }

        return changed;
    }

    // Two layers, applied together. The persistent power request is the one that actually holds a
    // Modern Standby (S0) machine in the active phase on AC power; the ES_* flags are the
    // compatibility layer that S3 systems still honour, and ES_CONTINUOUS alone clears any previously
    // registered requirement, so that one call covers both arming and releasing. DisplayRequired
    // is intentionally independent: keeping the panel lit must not silently arm SystemRequired.
    private void ApplyExecutionState()
    {
        bool wantSystem = this.sleepGuardEnabled;
        bool wantExecution = this.sleepGuardEnabled;
        bool wantDisplay = this.displayGuardUntilUtc != DateTime.MinValue;

        string powerDetail;
        if (!this.powerRequests.Sync(wantSystem, wantExecution, wantDisplay, out powerDetail))
        {
            this.lastActionDetail = "设置电源请求失败：" + powerDetail;
            Program.LogInfo("Guard PowerRequest sync failed. Detail=" + powerDetail);
        }

        NativeMethods.ExecutionState state = NativeMethods.ExecutionState.Continuous;
        if (wantSystem)
        {
            state |= NativeMethods.ExecutionState.SystemRequired;
        }

        if (wantDisplay)
        {
            state |= NativeMethods.ExecutionState.DisplayRequired;
        }

        string detail;
        if (!NativeMethods.TrySetThreadExecutionState(state, out detail))
        {
            this.lastActionDetail = "设置电源守护状态失败：" + detail;
            Program.LogInfo("Guard SetThreadExecutionState failed. Detail=" + detail);
        }

        this.nextExecutionRepairUtc = DateTime.UtcNow.AddSeconds(30);
    }

    // Releases the flags on shutdown. Without this the process can exit while Windows still holds
    // the requirement against a thread that no longer exists.
    //
    // This deliberately does not touch batteryCarePauseUntilUtc, powerModeOverrideUntilUtc or
    // energySaverForcedOn. Sleep/display protection is released here because it is backed by a
    // process-scoped Win32 handle that becomes meaningless the moment this process exits; the power
    // mode, Energy Saver and battery-care states are durable writes to Windows' own settings store
    // with no such handle, and the entire point of scheduling them is that they survive this app
    // being closed and reopened. Reverting them on every shutdown would silently undo a boost the
    // user asked to keep for the next several hours.
    internal void ReleaseAll()
    {
        this.sleepGuardEnabled = false;
        this.sleepGuardSinceUtc = DateTime.MinValue;
        this.displayGuardUntilUtc = DateTime.MinValue;
        this.powerRequests.Release();
        string detail;
        NativeMethods.TrySetThreadExecutionState(NativeMethods.ExecutionState.Continuous, out detail);
    }

    internal static void RunSelfTest()
    {
        DateTime now = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

        GuardRuntime online = new GuardRuntime(delegate { return true; });
        AssertSelfTest(!online.SleepGuardEnabled, "sleep guard starts disabled");
        AssertSelfTest(!online.SystemPowerRequestActive, "system power request starts inactive");
        AssertSelfTest(online.SetSleepGuard(true), "enabling sleep guard reports a change");
        AssertSelfTest(!online.SetSleepGuard(true), "re-enabling sleep guard is a no-op");
        // The Modern Standby fix: arming the sleep guard must hold both the system and execution
        // power requests, not only the legacy execution-state flag.
        AssertSelfTest(online.SystemPowerRequestActive, "enabling sleep guard holds the system power request");
        AssertSelfTest(online.ExecutionPowerRequestActive, "enabling sleep guard holds the execution power request");
        AssertSelfTest(online.StartDisplayGuard(now), "display guard starts");
        AssertSelfTest(online.DisplayGuardActive, "display guard reports active");
        AssertSelfTest(online.DisplayPowerRequestActive, "starting the display guard holds the display power request");
        AssertSelfTest(
            Math.Abs(online.GetDisplayGuardProgress(now) - 1.0f) < 0.001f,
            "display guard progress is full at start");
        AssertSelfTest(
            online.GetDisplayGuardProgress(now.AddMinutes(online.DisplayGuardMinutes)) == 0.0f,
            "display guard progress empties at the deadline");

        // The two controls are independent: releasing system sleep protection must leave a live
        // display timer intact, while dropping only the corresponding system/execution requests.
        online.SetSleepGuard(false);
        AssertSelfTest(online.DisplayGuardActive, "disabling sleep guard preserves the display guard");
        AssertSelfTest(!online.SystemPowerRequestActive, "disabling sleep guard clears the system power request");
        AssertSelfTest(!online.ExecutionPowerRequestActive, "disabling sleep guard clears the execution power request");
        AssertSelfTest(online.DisplayPowerRequestActive, "disabling sleep guard preserves the display power request");
        online.StopDisplayGuard();
        AssertSelfTest(!online.DisplayPowerRequestActive, "stopping display guard clears its own power request");
        online.ReleaseAll();

        GuardRuntime displayOnly = new GuardRuntime(delegate { return true; });
        AssertSelfTest(displayOnly.StartDisplayGuard(now), "display-only guard starts");
        AssertSelfTest(!displayOnly.SleepGuardEnabled, "display-only guard does not arm sleep protection");
        AssertSelfTest(!displayOnly.SystemPowerRequestActive && !displayOnly.ExecutionPowerRequestActive,
            "display-only guard does not hold system or execution requests");
        AssertSelfTest(displayOnly.DisplayPowerRequestActive, "display-only guard holds the display request");
        displayOnly.ReleaseAll();

        // Expiry path.
        GuardRuntime expiry = new GuardRuntime(delegate { return true; });
        expiry.SetDisplayGuardMinutes(60);
        expiry.StartDisplayGuard(now);
        AssertSelfTest(expiry.Tick(now.AddMinutes(61)), "display guard expiry reports a repaint");
        AssertSelfTest(!expiry.DisplayGuardActive, "display guard clears itself at the deadline");
        AssertSelfTest(!expiry.SleepGuardEnabled, "display expiry does not alter independent sleep state");
        expiry.ReleaseAll();

        // Unknown connectivity must not start the offline clock.
        GuardRuntime unknown = new GuardRuntime(delegate { return (bool?)null; });
        unknown.Tick(now);
        AssertSelfTest(unknown.Online, "unknown connectivity is treated as online");

        // Offline clock accumulates but stays below the threshold.
        int sleepRequestCount = 0;
        GuardRuntime offline = new GuardRuntime(
            delegate { return false; },
            delegate
            {
                sleepRequestCount++;
                return Tuple.Create(true, "self-test");
            });
        offline.SetOfflineThresholdMinutes(10);
        offline.Tick(now);
        AssertSelfTest(!offline.Online, "confirmed offline starts the clock");
        AssertSelfTest(
            Math.Abs(offline.GetOfflineProgress(now.AddMinutes(5)) - 0.5f) < 0.01f,
            "offline progress is half way at half the threshold");
        offline.Tick(now.AddMinutes(11));
        AssertSelfTest(sleepRequestCount == 0, "unarmed offline state never requests sleep");

        GuardRuntime armedOffline = new GuardRuntime(
            delegate { return false; },
            delegate
            {
                sleepRequestCount++;
                return Tuple.Create(true, "self-test");
            });
        armedOffline.SetOfflineThresholdMinutes(10);
        armedOffline.SetSleepGuard(true);
        armedOffline.Tick(now);
        armedOffline.Tick(now.AddMinutes(11));
        AssertSelfTest(sleepRequestCount == 1, "armed offline threshold requests sleep once");
        AssertSelfTest(!armedOffline.SleepGuardEnabled, "auto sleep releases the sleep guard first");
        armedOffline.OnSystemResume(now.AddHours(1));
        armedOffline.Tick(now.AddHours(1).AddSeconds(1));
        AssertSelfTest(
            armedOffline.GetOfflineProgress(now.AddHours(1).AddSeconds(1)) < 0.01f,
            "resume restarts the offline threshold instead of immediately sleeping again");

        // Battery care pause window.
        GuardRuntime battery = new GuardRuntime(delegate { return true; });
        AssertSelfTest(!battery.BatteryCarePauseActive, "battery care pause starts inactive");
        battery.NoteBatteryCarePaused(now);
        AssertSelfTest(battery.BatteryCarePauseActive, "battery care pause records a deadline");
        AssertSelfTest(
            battery.GetBatteryCarePauseProgress(now) < 0.01f,
            "battery care pause progress starts empty");
        AssertSelfTest(
            battery.GetBatteryCarePauseRemaining(now).TotalHours > 23.9,
            "battery care pause leaves ~24h remaining at the start");
        AssertSelfTest(
            battery.Tick(now.AddHours(BatteryCarePauseHours + 1)),
            "battery care pause expiry reports a repaint");
        AssertSelfTest(!battery.BatteryCarePauseActive, "battery care pause clears itself after 24h");

        AssertSelfTest(!battery.ObserveBatteryPercent(true, 85, now), "startup above 80 does not invent a deadline");
        battery.ObserveBatteryPercent(true, 80, now);
        AssertSelfTest(battery.ObserveBatteryPercent(true, 81, now), "80 to 81 starts the inferred window");
        DateTime edgeDeadline = battery.BatteryCarePauseUntilUtc;
        battery.ObserveBatteryPercent(true, 90, now.AddHours(1));
        battery.ObserveBatteryPercent(true, 79, now.AddHours(2));
        battery.ObserveBatteryPercent(true, 82, now.AddHours(3));
        AssertSelfTest(battery.BatteryCarePauseUntilUtc == edgeDeadline, "active window never extends from observations");
        battery.NoteBatteryCareRestored();
        AssertSelfTest(!battery.ObserveBatteryPercent(true, 90, now.AddHours(4)), "restore above 80 stays restored");
        battery.ObserveBatteryPercent(true, 80, now.AddHours(4));
        battery.ObserveBatteryPercent(false, 0, now.AddHours(4));
        AssertSelfTest(!battery.ObserveBatteryPercent(true, 81, now.AddHours(4)), "unknown breaks the edge baseline");
        battery.NoteBatteryCarePaused(now);
        battery.Tick(now.AddHours(24));
        AssertSelfTest(!battery.BatteryCarePauseActive &&
            !battery.ObserveBatteryPercent(true, 82, now.AddHours(24)), "expiry at high charge does not restart the clock");
        battery.NoteBatteryCarePaused(now.AddHours(25));
        AssertSelfTest(battery.BatteryCarePauseUntilUtc == now.AddHours(49), "explicit command restarts 24 hours");

        AssertSelfTest(FormatCountdown(TimeSpan.FromSeconds(0)) == "0:00:00", "countdown zero format");
        AssertSelfTest(
            FormatCountdown(new TimeSpan(4, 32, 10)) == "4:32:10",
            "countdown h:mm:ss format");
        AssertSelfTest(
            FormatCountdown(new TimeSpan(1, 2, 3, 4)) == "26:03:04",
            "countdown rolls days into hours");

        // Power mode override and energy saver force. These exercise real Windows APIs (the power-
        // mode overlay scheme and ESBATTTHRESHOLD), so whatever the machine already has is captured
        // first and restored in a finally block - --test must never leave the developer's machine
        // in a different power mode or with a different Energy Saver auto-enable threshold.
        Guid originalOverlayGuid;
        bool hadOriginalOverlay = NativeMethods.TryGetActivePowerOverlayScheme(out originalOverlayGuid);
        int originalEnergySaverThreshold;
        bool hadOriginalEnergySaverThreshold = NativeMethods.TryReadEnergySaverBatteryThresholdPercent(out originalEnergySaverThreshold);
        try
        {
            AssertSelfTest(GuardRuntime.ClassifyOverlaySchemeGuid(GuardRuntime.ResolveOverlaySchemeGuid(GuardPowerModeTier.Saver)) == GuardPowerModeTier.Saver, "saver tier GUID round trip");
            AssertSelfTest(GuardRuntime.ClassifyOverlaySchemeGuid(GuardRuntime.ResolveOverlaySchemeGuid(GuardPowerModeTier.Balanced)) == GuardPowerModeTier.Balanced, "balanced tier GUID round trip");
            AssertSelfTest(GuardRuntime.ClassifyOverlaySchemeGuid(GuardRuntime.ResolveOverlaySchemeGuid(GuardPowerModeTier.Performance)) == GuardPowerModeTier.Performance, "performance tier GUID round trip");
            AssertSelfTest(GuardRuntime.ClassifyOverlaySchemeGuid(Guid.NewGuid()) == GuardPowerModeTier.Unknown, "unrecognized overlay GUID classifies as unknown");

            GuardRuntime powerMode = new GuardRuntime(delegate { return true; });
            AssertSelfTest(!powerMode.PowerModeOverrideActive, "power mode override starts disarmed");
            AssertSelfTest(powerMode.StartPowerModeOverride(3, now), "starting the override reports a change");
            AssertSelfTest(powerMode.PowerModeOverrideActive, "override reports armed after starting");
            AssertSelfTest(powerMode.PowerModeOverrideHours == 3, "override hours dial reflects the requested value");

            powerMode.SetPowerMode(GuardPowerModeTier.Balanced);
            AssertSelfTest(!powerMode.PowerModeOverrideActive, "manual power mode switch cancels a pending override");

            powerMode.StartPowerModeOverride(2, now);
            AssertSelfTest(powerMode.Tick(now.AddHours(3)), "power mode override expiry reports a repaint");
            AssertSelfTest(!powerMode.PowerModeOverrideActive, "power mode override clears itself at the deadline");

            AssertSelfTest(!powerMode.EnergySaverForcedOn, "energy saver force starts off");
            AssertSelfTest(powerMode.SetEnergySaverForced(true), "forcing energy saver on reports a change");
            AssertSelfTest(powerMode.EnergySaverForcedOn, "energy saver force reports on");
            AssertSelfTest(!powerMode.SetEnergySaverForced(true), "re-forcing energy saver on is a no-op");
            AssertSelfTest(powerMode.SetEnergySaverForced(false), "restoring energy saver reports a change");
            AssertSelfTest(!powerMode.EnergySaverForcedOn, "energy saver force reports off after restore");
            powerMode.ReleaseAll();

            WidgetSettings powerModeSettings = WidgetSettings.CreateDefaults();
            GuardRuntime savedPowerMode = new GuardRuntime(delegate { return true; });
            savedPowerMode.StartPowerModeOverride(5, DateTime.UtcNow);
            savedPowerMode.SaveToSettings(powerModeSettings);
            GuardRuntime loadedPowerMode = new GuardRuntime(delegate { return true; });
            loadedPowerMode.LoadFromSettings(powerModeSettings, DateTime.UtcNow);
            AssertSelfTest(loadedPowerMode.PowerModeOverrideActive, "power mode override deadline round trips");
            AssertSelfTest(loadedPowerMode.PowerModeOverrideHours == 5, "power mode override hours round trip");
            loadedPowerMode.ReleaseAll();
            savedPowerMode.ReleaseAll();

            // An override deadline already in the past at load time must fire its revert immediately
            // rather than only clearing the bookkeeping, since no Tick() will ever see it as "just
            // expired" once the in-memory field has already been reset to the unarmed sentinel.
            WidgetSettings stalePowerModeSettings = WidgetSettings.CreateDefaults();
            stalePowerModeSettings.GuardPowerModeOverrideUntilUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
            GuardRuntime staleOverride = new GuardRuntime(delegate { return true; });
            staleOverride.LoadFromSettings(stalePowerModeSettings, DateTime.UtcNow);
            AssertSelfTest(!staleOverride.PowerModeOverrideActive, "expired power mode override is not restored as active");
            staleOverride.ReleaseAll();
        }
        finally
        {
            if (hadOriginalOverlay)
            {
                string restoreOverlayDetail;
                NativeMethods.TrySetActivePowerOverlayScheme(originalOverlayGuid, out restoreOverlayDetail);
            }

            if (hadOriginalEnergySaverThreshold)
            {
                string restoreThresholdDetail;
                NativeMethods.TryWriteEnergySaverBatteryThresholdPercent(originalEnergySaverThreshold, out restoreThresholdDetail);
            }
        }

        // Settings round trip.
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        GuardRuntime saved = new GuardRuntime(delegate { return true; });
        saved.SetDisplayGuardMinutes(120);
        saved.SetOfflineThresholdMinutes(30);
        saved.SetSleepGuard(true);
        saved.NoteBatteryCarePaused(DateTime.UtcNow);
        saved.SaveToSettings(settings);
        GuardRuntime loaded = new GuardRuntime(delegate { return true; });
        loaded.LoadFromSettings(settings, DateTime.UtcNow);
        AssertSelfTest(loaded.DisplayGuardMinutes == 120, "display minutes round trip");
        AssertSelfTest(loaded.OfflineThresholdMinutes == 30, "offline threshold round trips");
        AssertSelfTest(loaded.SleepGuardEnabled, "sleep guard preference round trips");
        AssertSelfTest(loaded.BatteryCarePauseActive, "battery care deadline round trips");
        AssertSelfTest(loaded.BatteryCarePauseUntilUtc == saved.BatteryCarePauseUntilUtc,
            "restart preserves the exact deadline instead of extending it");
        loaded.ObserveBatteryPercent(true, 95, DateTime.UtcNow);
        AssertSelfTest(loaded.BatteryCarePauseUntilUtc == saved.BatteryCarePauseUntilUtc,
            "startup above 80 preserves the saved deadline");
        loaded.ReleaseAll();
        saved.ReleaseAll();

        // A deadline already in the past must not be restored.
        settings.GuardDisplayUntilUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks;
        GuardRuntime stale = new GuardRuntime(delegate { return true; });
        stale.LoadFromSettings(settings, DateTime.UtcNow);
        AssertSelfTest(!stale.DisplayGuardActive, "expired display deadline is not restored");
        stale.ReleaseAll();

        WidgetSettings inconsistent = WidgetSettings.CreateDefaults();
        inconsistent.GuardSleepEnabled = false;
        inconsistent.GuardDisplayUntilUtcTicks = DateTime.UtcNow.AddMinutes(10).Ticks;
        GuardRuntime repaired = new GuardRuntime(delegate { return true; });
        repaired.LoadFromSettings(inconsistent, DateTime.UtcNow);
        AssertSelfTest(repaired.DisplayGuardActive && !repaired.SleepGuardEnabled,
            "display deadline restores without silently enabling sleep protection");
        repaired.ReleaseAll();

        Console.WriteLine("Guard runtime: PASS sleep/display/offline/battery state machine");
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Guard runtime self-test failed: " + message);
        }
    }
}
