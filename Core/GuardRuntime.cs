using System;
using System.Globalization;
using System.Net.NetworkInformation;

// Power and program guard state machine behind the GUARD board (scheme D).
//
// This is a native C# reimplementation of the CodexSleepGuard PowerShell utility
// (E:\Codexproject\desktopdata\CodexSleepGuard). The board draws a live countdown ring, which an
// out-of-process script cannot feed without an IPC channel, so the three power guards moved
// in-process; the launcher entry in OperationForm.LauncherTrio still starts the standalone script
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
    // own record of when we sent the pause — the board labels it as such rather than claiming to
    // report MyASUS's true internal state.
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
    private DateTime offlineSinceUtc = DateTime.MinValue;
    private DateTime lastAutoSleepUtc = DateTime.MinValue;
    private int displayGuardMinutes = WidgetSettings.DefaultGuardDisplayMinutes;
    private int offlineThresholdMinutes = WidgetSettings.DefaultGuardOfflineThresholdMinutes;
    private string lastActionDetail = string.Empty;

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
        // A live display timer is a strict extension of the system guard. Repair a hand-edited or
        // partially written settings file here instead of restoring DISPLAY_REQUIRED by itself.
        this.sleepGuardEnabled = settings.GuardSleepEnabled || this.displayGuardUntilUtc != DateTime.MinValue;
        // Unlike the two deadlines this is a *start* stamp, so it is only valid in the past. A
        // future or absent value falls back to now, which reads as "just armed" rather than
        // inventing an elapsed time the guard never actually held.
        this.sleepGuardSinceUtc = this.sleepGuardEnabled
            ? NormalizeStartStamp(settings.GuardSleepSinceUtcTicks, nowUtc)
            : DateTime.MinValue;
        ApplyExecutionState();
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
        else
        {
            // Dropping the system guard must not silently keep the display awake: the display
            // guard is a strict extension of the sleep guard, never an independent flag.
            this.displayGuardUntilUtc = DateTime.MinValue;
            this.sleepGuardSinceUtc = DateTime.MinValue;
        }

        ApplyExecutionState();
        Program.LogInfo("Guard sleep protection set. Enabled=" + enabled.ToString());
        return true;
    }

    internal bool StartDisplayGuard(DateTime nowUtc)
    {
        if (this.displayGuardMinutes <= 0)
        {
            return false;
        }

        // Keeping the display on without keeping the system awake is incoherent, so starting the
        // display timer implies the sleep guard.
        if (!this.sleepGuardEnabled)
        {
            this.sleepGuardEnabled = true;
            this.sleepGuardSinceUtc = nowUtc;
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

    // Called by OperationForm right after a successful acin_set (pause) or acin80 (restore), so
    // this clock always reflects a command we actually managed to send.
    internal void NoteBatteryCarePaused(DateTime nowUtc)
    {
        this.batteryCarePauseUntilUtc = nowUtc.AddHours(BatteryCarePauseHours);
        Program.LogInfo(
            "Guard battery care pause recorded. UntilUtc=" +
            this.batteryCarePauseUntilUtc.ToString("o", CultureInfo.InvariantCulture));
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

        changed |= UpdateOfflineState(nowUtc);
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
        if (!this.sleepGuardEnabled && this.displayGuardUntilUtc == DateTime.MinValue)
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
        if (changed)
        {
            this.lastActionDetail = "系统已唤醒，断网倒计时重新开始。";
        }

        return changed;
    }

    // ES_CONTINUOUS alone clears any previously registered requirement, which is exactly what the
    // "everything off" state needs, so this one call covers both arming and releasing.
    private void ApplyExecutionState()
    {
        NativeMethods.ExecutionState state = NativeMethods.ExecutionState.Continuous;
        if (this.sleepGuardEnabled)
        {
            state |= NativeMethods.ExecutionState.SystemRequired;
        }

        if (this.displayGuardUntilUtc != DateTime.MinValue)
        {
            state |= NativeMethods.ExecutionState.DisplayRequired;
        }

        string detail;
        if (!NativeMethods.TrySetThreadExecutionState(state, out detail))
        {
            this.lastActionDetail = "设置电源守护状态失败：" + detail;
            Program.LogInfo("Guard SetThreadExecutionState failed. Detail=" + detail);
        }
    }

    // Releases the flags on shutdown. Without this the process can exit while Windows still holds
    // the requirement against a thread that no longer exists.
    internal void ReleaseAll()
    {
        this.sleepGuardEnabled = false;
        this.sleepGuardSinceUtc = DateTime.MinValue;
        this.displayGuardUntilUtc = DateTime.MinValue;
        string detail;
        NativeMethods.TrySetThreadExecutionState(NativeMethods.ExecutionState.Continuous, out detail);
    }

    internal static void RunSelfTest()
    {
        DateTime now = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

        GuardRuntime online = new GuardRuntime(delegate { return true; });
        AssertSelfTest(!online.SleepGuardEnabled, "sleep guard starts disabled");
        AssertSelfTest(online.SetSleepGuard(true), "enabling sleep guard reports a change");
        AssertSelfTest(!online.SetSleepGuard(true), "re-enabling sleep guard is a no-op");
        AssertSelfTest(online.StartDisplayGuard(now), "display guard starts");
        AssertSelfTest(online.DisplayGuardActive, "display guard reports active");
        AssertSelfTest(
            Math.Abs(online.GetDisplayGuardProgress(now) - 1.0f) < 0.001f,
            "display guard progress is full at start");
        AssertSelfTest(
            online.GetDisplayGuardProgress(now.AddMinutes(online.DisplayGuardMinutes)) == 0.0f,
            "display guard progress empties at the deadline");

        // Turning the system guard off must take the display guard with it.
        online.SetSleepGuard(false);
        AssertSelfTest(!online.DisplayGuardActive, "disabling sleep guard clears the display guard");

        // Expiry path.
        GuardRuntime expiry = new GuardRuntime(delegate { return true; });
        expiry.SetDisplayGuardMinutes(30);
        expiry.StartDisplayGuard(now);
        AssertSelfTest(expiry.Tick(now.AddMinutes(31)), "display guard expiry reports a repaint");
        AssertSelfTest(!expiry.DisplayGuardActive, "display guard clears itself at the deadline");

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

        AssertSelfTest(FormatCountdown(TimeSpan.FromSeconds(0)) == "0:00:00", "countdown zero format");
        AssertSelfTest(
            FormatCountdown(new TimeSpan(4, 32, 10)) == "4:32:10",
            "countdown h:mm:ss format");
        AssertSelfTest(
            FormatCountdown(new TimeSpan(1, 2, 3, 4)) == "26:03:04",
            "countdown rolls days into hours");

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
        AssertSelfTest(repaired.DisplayGuardActive && repaired.SleepGuardEnabled, "display deadline restores its required sleep guard");
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
