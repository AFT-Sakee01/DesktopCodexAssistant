using System;

// Sampling bridge for the System Day history. It reuses WidgetForm's existing PDH snapshot and the
// PowerThermalForm cache; no new hardware sampler or UI-thread file I/O is introduced.
internal sealed partial class WidgetForm
{
    private const int SystemDayActiveIdleThresholdSeconds = 5 * 60;
    private const int MetricTilePowerProjectionRefreshIntervalMs = 5000;
    private readonly object systemDayHistoryLifecycleSync = new object();
    private SystemDayHistoryStore systemDayHistoryStore;
    private SystemDayBoardSnapshot metricTilePowerProjection;
    private DateTime nextMetricTilePowerProjectionUtc = DateTime.MinValue;

    private void InitializeSystemDayHistory()
    {
        lock (this.systemDayHistoryLifecycleSync)
        {
            if (this.systemDayHistoryStore != null) return;
            this.systemDayHistoryStore = new SystemDayHistoryStore();
            this.systemDayHistoryStore.RecordStartup(DateTime.UtcNow);
        }
    }

    private void DisposeSystemDayHistory()
    {
        SystemDayHistoryStore store;
        lock (this.systemDayHistoryLifecycleSync)
        {
            store = this.systemDayHistoryStore;
            this.systemDayHistoryStore = null;
            this.metricTilePowerProjection = null;
            this.nextMetricTilePowerProjectionUtc = DateTime.MinValue;
        }
        if (store != null) store.Dispose();
    }

    private SystemDayBoardSnapshot BuildSystemDayBoardSnapshot(SystemDayRange range)
    {
        SystemDayHistoryStore store = this.systemDayHistoryStore;
        return store == null
            ? SystemDayBoardSnapshot.CreateEmpty(range, DateTime.Now)
            : store.GetBoardSnapshot(range, DateTime.Now);
    }

    private SystemDayBoardSnapshot BuildMetricTilePowerProjection()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (this.metricTilePowerProjection == null || nowUtc >= this.nextMetricTilePowerProjectionUtc)
        {
            // The PWR panel reads the same owner-memory projection as System Day. Five-second
            // caching matches the board's existing refresh cadence and prevents every hover/feed
            // push from rebuilding a 24-hour plot; no timer, disk read or sampler is added here.
            this.metricTilePowerProjection = BuildSystemDayBoardSnapshot(SystemDayRange.Last24Hours);
            this.nextMetricTilePowerProjectionUtc =
                nowUtc.AddMilliseconds(MetricTilePowerProjectionRefreshIntervalMs);
        }

        return this.metricTilePowerProjection;
    }

    private void RecordSystemDaySample()
    {
        SystemDayHistoryStore store = this.systemDayHistoryStore;
        if (store == null) return;
        PowerStripSnapshot power = null;
        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            try { power = this.powerThermalForm.BuildStripSnapshot(); }
            catch (Exception ex) { Program.LogException(ex); }
        }
        // Observe from the existing sampling bridge even when every tile is hidden. Snapshot
        // builders and paint remain cache-only; only an actual edge causes settings persistence.
        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.ObserveBatteryPercent(
                power != null && power.BatteryPercentKnown,
                power == null ? 0 : power.BatteryPercent, DateTime.UtcNow);
        }
        ApplyBatteryCareRecord(power);
        int idleSeconds = ResolveSystemIdleSeconds();
        SystemDayWorkState state = idleSeconds >= SystemDayActiveIdleThresholdSeconds
            ? SystemDayWorkState.Idle
            : SystemDayWorkState.Active;
        store.RecordSample(this.snapshot, power, state, idleSeconds, DateTime.UtcNow);
    }

    private void ApplyBatteryCareRecord(PowerStripSnapshot power)
    {
        if (power == null) return;
        GuardRuntime runtime = this.operationForm == null || this.operationForm.IsDisposed
            ? null : this.operationForm.PeekGuardRuntime();
        DateTime deadline = runtime == null ? DateTime.MinValue : runtime.BatteryCarePauseUntilUtc;
        power.BatteryCarePauseActive = deadline > DateTime.UtcNow;
        power.BatteryCarePauseUntilUtc = power.BatteryCarePauseActive ? deadline : DateTime.MinValue;
    }

    private static int ResolveSystemIdleSeconds()
    {
        uint lastInputTick;
        if (!NativeMethods.TryGetLastInputTickCount(out lastInputTick)) return 0;
        uint currentTick = unchecked((uint)Environment.TickCount);
        uint elapsedMilliseconds = unchecked(currentTick - lastInputTick);
        return (int)Math.Min(int.MaxValue, elapsedMilliseconds / 1000U);
    }

    private void RecordSystemDaySuspend()
    {
        SystemDayHistoryStore store = this.systemDayHistoryStore;
        if (store != null) store.RecordSuspend(DateTime.UtcNow);
    }

    private void RecordSystemDayResume()
    {
        SystemDayHistoryStore store = this.systemDayHistoryStore;
        if (store != null) store.RecordResume(DateTime.UtcNow);
    }
}
