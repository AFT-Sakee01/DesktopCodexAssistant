using System;

// Sampling bridge for the System Day history. It reuses WidgetForm's existing PDH snapshot and the
// PowerThermalForm cache; no new hardware sampler or UI-thread file I/O is introduced.
internal sealed partial class WidgetForm
{
    private const int SystemDayActiveIdleThresholdSeconds = 5 * 60;
    private readonly object systemDayHistoryLifecycleSync = new object();
    private SystemDayHistoryStore systemDayHistoryStore;

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
        int idleSeconds = ResolveSystemIdleSeconds();
        SystemDayWorkState state = idleSeconds >= SystemDayActiveIdleThresholdSeconds
            ? SystemDayWorkState.Idle
            : SystemDayWorkState.Active;
        store.RecordSample(this.snapshot, power, state, idleSeconds, DateTime.UtcNow);
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
