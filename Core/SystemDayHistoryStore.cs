using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

// Seven-day work/power/thermal history. Record methods update memory immediately and buffer JSONL
// appends for a ThreadPool timer; board projection never opens a file. Daily files keep future
// peak-to-zone analysis bounded and make retention independent of one continuously growing log.
internal sealed class SystemDayHistoryStore : IDisposable
{
    internal const int RetentionDays = 8;
    internal const int FlushIntervalMs = 15000;
    internal const int MinimumSampleSeconds = 55;
    internal const int MaximumEntries = 13000;
    internal const int MaximumPlotPoints = 180;
    private const string FilePrefix = "system-day-";
    private const string FileSuffix = ".jsonl";
    private readonly object syncRoot = new object();
    private readonly object flushSyncRoot = new object();
    private readonly string rootPath;
    private readonly Timer flushTimer;
    private readonly List<SystemDayHistoryEntry> entries = new List<SystemDayHistoryEntry>();
    private readonly Dictionary<string, List<string>> pendingLines =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private DateTime lastRetentionUtc = DateTime.MinValue;
    private bool disposed;

    internal SystemDayHistoryStore(string rootOverride = null)
    {
        this.rootPath = string.IsNullOrWhiteSpace(rootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName,
                "system-day")
            : rootOverride;
        Load();
        this.flushTimer = new Timer(delegate { Flush(false); }, null, FlushIntervalMs, FlushIntervalMs);
    }

    internal string RootPathForDiagnostics
    {
        get { return this.rootPath; }
    }

    internal void RecordSample(
        PerfSnapshot performance,
        PowerStripSnapshot power,
        SystemDayWorkState workState,
        int idleSeconds,
        DateTime nowUtc)
    {
        DateTime timestampUtc = NormalizeUtc(nowUtc);
        SystemDayHistoryEntry entry = BuildSampleEntry(performance, power, workState, idleSeconds, timestampUtc);
        lock (this.syncRoot)
        {
            if (this.disposed) return;
            SystemDayHistoryEntry previous = FindLastSampleLocked();
            if (previous != null && (timestampUtc - previous.TimestampUtc).TotalSeconds < MinimumSampleSeconds)
                return;
            entry.BatteryDirection = ResolveBatteryDirection(previous, entry);
            AddEntryLocked(entry);
        }
    }

    internal void RecordSuspend(DateTime nowUtc)
    {
        RecordEvent("suspend", SystemDayWorkState.Sleep, nowUtc, true);
    }

    internal void RecordResume(DateTime nowUtc)
    {
        RecordEvent("resume", SystemDayWorkState.Unknown, nowUtc, false);
    }

    internal void RecordStartup(DateTime nowUtc)
    {
        RecordEvent("startup", SystemDayWorkState.Unknown, nowUtc, false);
    }

    internal SystemDayBoardSnapshot GetBoardSnapshot(SystemDayRange range, DateTime nowLocal)
    {
        DateTime local = nowLocal.Kind == DateTimeKind.Utc ? nowLocal.ToLocalTime() : nowLocal;
        DateTime startLocal = SystemDayBoardSnapshot.ResolveStartLocal(range, local);
        DateTime startUtc = startLocal.ToUniversalTime();
        DateTime endUtc = local.ToUniversalTime();
        List<SystemDayHistoryEntry> selected = new List<SystemDayHistoryEntry>();
        SystemDayHistoryEntry boundarySuspend = null;
        lock (this.syncRoot)
        {
            for (int i = 0; i < this.entries.Count; i++)
            {
                SystemDayHistoryEntry item = this.entries[i];
                if (item == null) continue;
                if (item.TimestampUtc < startUtc)
                {
                    // Preserve the last sleep transition before the selected range so a
                    // suspend interval crossing the left boundary is clipped, not lost.
                    if (string.Equals(item.EventType, "suspend", StringComparison.Ordinal))
                        boundarySuspend = item.Clone();
                    else if (string.Equals(item.EventType, "resume", StringComparison.Ordinal) ||
                             string.Equals(item.EventType, "startup", StringComparison.Ordinal))
                        boundarySuspend = null;
                    continue;
                }
                if (item.TimestampUtc <= endUtc)
                    selected.Add(item.Clone());
            }
        }
        if (boundarySuspend != null) selected.Insert(0, boundarySuspend);
        return BuildBoardSnapshot(range, startLocal, local, selected);
    }

    public void Dispose()
    {
        lock (this.syncRoot)
        {
            if (this.disposed) return;
            this.disposed = true;
        }
        this.flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Flush(true);
        this.flushTimer.Dispose();
    }

    private void RecordEvent(string eventType, SystemDayWorkState state, DateTime nowUtc, bool flushBeforeSleep)
    {
        SystemDayHistoryEntry entry = new SystemDayHistoryEntry
        {
            TimestampUtc = NormalizeUtc(nowUtc),
            EventType = eventType,
            WorkState = state,
            ThermalZones = new List<SystemDayThermalZone>()
        };
        lock (this.syncRoot)
        {
            if (this.disposed) return;
            SystemDayHistoryEntry last = this.entries.Count == 0 ? null : this.entries[this.entries.Count - 1];
            if (last != null && string.Equals(last.EventType, eventType, StringComparison.Ordinal) &&
                Math.Abs((entry.TimestampUtc - last.TimestampUtc).TotalSeconds) < 5.0)
                return;
            AddEntryLocked(entry);
        }
        // Suspend is rare and the process may receive no more CPU time after this broadcast. A
        // synchronous small append is the only reliable boundary marker; normal samples stay async.
        if (flushBeforeSleep) Flush(false);
    }

    private void AddEntryLocked(SystemDayHistoryEntry entry)
    {
        this.entries.Add(entry);
        string path = ResolvePath(entry.TimestampUtc.ToLocalTime().Date);
        List<string> lines;
        if (!this.pendingLines.TryGetValue(path, out lines))
        {
            lines = new List<string>();
            this.pendingLines[path] = lines;
        }
        lines.Add(Serialize(entry));
        TrimMemoryLocked(entry.TimestampUtc);
    }

    private SystemDayHistoryEntry FindLastSampleLocked()
    {
        for (int i = this.entries.Count - 1; i >= 0; i--)
        {
            if (this.entries[i] != null && string.Equals(this.entries[i].EventType, "sample", StringComparison.Ordinal))
                return this.entries[i];
        }
        return null;
    }

    private static SystemDayBatteryDirection ResolveBatteryDirection(
        SystemDayHistoryEntry previous,
        SystemDayHistoryEntry current)
    {
        if (previous == null || current == null || !previous.BatteryKnown || !current.BatteryKnown)
            return SystemDayBatteryDirection.Unknown;
        if (current.BatteryPercent > previous.BatteryPercent) return SystemDayBatteryDirection.Rising;
        if (current.BatteryPercent < previous.BatteryPercent) return SystemDayBatteryDirection.Falling;
        return SystemDayBatteryDirection.Flat;
    }

    private static SystemDayHistoryEntry BuildSampleEntry(
        PerfSnapshot performance,
        PowerStripSnapshot power,
        SystemDayWorkState workState,
        int idleSeconds,
        DateTime timestampUtc)
    {
        PerfSnapshot perf = performance ?? new PerfSnapshot();
        PowerStripSnapshot p = power ?? new PowerStripSnapshot();
        SystemDayHistoryEntry entry = new SystemDayHistoryEntry
        {
            TimestampUtc = timestampUtc,
            EventType = "sample",
            WorkState = workState,
            IdleSeconds = Math.Max(0, idleSeconds),
            CpuPercent = SafeNonNegative(perf.CpuPercent),
            GpuPercent = SafeNonNegative(perf.GpuPercent),
            NpuPercent = SafeNonNegative(perf.NpuPercent),
            MemoryPercent = SafeNonNegative(perf.MemoryPercent),
            NetworkSentBytesPerSecond = SafeNonNegative(perf.NetworkSentBytesPerSecond),
            NetworkReceivedBytesPerSecond = SafeNonNegative(perf.NetworkReceivedBytesPerSecond),
            BatteryKnown = p.BatteryPercentKnown,
            BatteryPercent = Math.Max(0, Math.Min(100, p.BatteryPercent)),
            Charging = p.Charging,
            PluggedIn = p.PluggedIn,
            WattsKnown = p.WattsKnown,
            Watts = SafeNonNegative(p.Watts),
            RuntimeSecondsKnown = p.RuntimeSecondsKnown,
            RuntimeSeconds = Math.Max(0, p.RuntimeSeconds),
            BatteryCarePauseActive = p.BatteryCarePauseActive,
            PowerModeText = p.PowerModeText ?? string.Empty,
            MaxCelsius = SafeNonNegative(p.MaxCelsius),
            AvgCelsius = SafeNonNegative(p.AvgCelsius),
            ThermalZones = new List<SystemDayThermalZone>()
        };
        IList<PowerStripZone> zones = p.ThermalZones;
        for (int i = 0; zones != null && i < zones.Count; i++)
        {
            PowerStripZone zone = zones[i];
            if (zone == null || zone.Celsius <= 0.0) continue;
            entry.ThermalZones.Add(new SystemDayThermalZone
            {
                RawName = zone.RawName ?? string.Empty,
                DisplayName = string.IsNullOrEmpty(zone.Name) ? "TZ" : zone.Name,
                Celsius = zone.Celsius
            });
        }
        if (entry.ThermalZones.Count > 0)
        {
            entry.HotZoneRawName = entry.ThermalZones[0].RawName;
            entry.HotZoneName = entry.ThermalZones[0].DisplayName;
        }
        return entry;
    }

    private static SystemDayBoardSnapshot BuildBoardSnapshot(
        SystemDayRange range,
        DateTime startLocal,
        DateTime endLocal,
        List<SystemDayHistoryEntry> entries)
    {
        SystemDayBoardSnapshot snapshot = SystemDayBoardSnapshot.CreateEmpty(range, endLocal);
        snapshot.StartLocal = startLocal;
        snapshot.EndLocal = endLocal;
        List<SystemDayHistoryEntry> samples = new List<SystemDayHistoryEntry>();
        for (int i = 0; i < entries.Count; i++)
        {
            SystemDayHistoryEntry entry = entries[i];
            if (entry != null && string.Equals(entry.EventType, "sample", StringComparison.Ordinal)) samples.Add(entry);
        }
        snapshot.RawSampleCount = samples.Count;
        if (entries.Count > 0) snapshot.UpdatedLocal = entries[entries.Count - 1].TimestampUtc.ToLocalTime();
        BuildWorkSegments(snapshot, entries);
        BuildPeaks(snapshot, samples);
        List<SystemDayHistoryEntry> plotted = Downsample(samples, MaximumPlotPoints);
        for (int i = 0; i < plotted.Count; i++) snapshot.Points.Add(ToBoardPoint(plotted[i]));
        if (samples.Count > 0)
        {
            SystemDayHistoryEntry current = samples[samples.Count - 1];
            snapshot.CurrentBatteryKnown = current.BatteryKnown;
            snapshot.CurrentBatteryPercent = current.BatteryPercent;
            snapshot.CurrentCharging = current.Charging;
            snapshot.CurrentPluggedIn = current.PluggedIn;
            snapshot.CurrentWattsKnown = current.WattsKnown;
            snapshot.CurrentWatts = current.Watts;
            snapshot.CurrentPowerModeText = string.IsNullOrEmpty(current.PowerModeText) ? "--" : current.PowerModeText;
            snapshot.CurrentTemperatureKnown = current.MaxCelsius > 0.0;
            snapshot.CurrentMaxCelsius = current.MaxCelsius;
            snapshot.CurrentHotZoneName = string.IsNullOrEmpty(current.HotZoneName) ? "--" : current.HotZoneName;
            BuildBatteryEta(snapshot, samples, current);
        }
        return snapshot;
    }

    private static void BuildWorkSegments(SystemDayBoardSnapshot snapshot, List<SystemDayHistoryEntry> entries)
    {
        DateTime sleepStart = DateTime.MinValue;
        for (int i = 0; i < entries.Count; i++)
        {
            SystemDayHistoryEntry entry = entries[i];
            if (entry == null) continue;
            DateTime local = entry.TimestampUtc.ToLocalTime();
            if (string.Equals(entry.EventType, "suspend", StringComparison.Ordinal))
            {
                sleepStart = local < snapshot.StartLocal ? snapshot.StartLocal : local;
                continue;
            }
            if (string.Equals(entry.EventType, "resume", StringComparison.Ordinal) && sleepStart != DateTime.MinValue)
            {
                AddSegment(snapshot, sleepStart, local, SystemDayWorkState.Sleep);
                sleepStart = DateTime.MinValue;
                continue;
            }
            if (!string.Equals(entry.EventType, "sample", StringComparison.Ordinal)) continue;
            DateTime end = local.AddMinutes(1.0);
            if (i + 1 < entries.Count)
            {
                DateTime next = entries[i + 1].TimestampUtc.ToLocalTime();
                if (next > local && next < end) end = next;
            }
            AddSegment(snapshot, local, end, entry.WorkState);
        }
        if (sleepStart != DateTime.MinValue) AddSegment(snapshot, sleepStart, snapshot.EndLocal, SystemDayWorkState.Sleep);
        snapshot.RecordedMinutes = snapshot.ActiveMinutes + snapshot.IdleMinutes + snapshot.SleepMinutes;
    }

    private static void AddSegment(
        SystemDayBoardSnapshot snapshot,
        DateTime start,
        DateTime end,
        SystemDayWorkState state)
    {
        DateTime clippedStart = start < snapshot.StartLocal ? snapshot.StartLocal : start;
        DateTime clippedEnd = end > snapshot.EndLocal ? snapshot.EndLocal : end;
        if (clippedEnd <= clippedStart || state == SystemDayWorkState.Unknown) return;
        SystemDayWorkSegment last = snapshot.WorkSegments.Count == 0
            ? null
            : snapshot.WorkSegments[snapshot.WorkSegments.Count - 1];
        if (last != null && last.State == state && Math.Abs((clippedStart - last.EndLocal).TotalSeconds) <= 2.0)
            last.EndLocal = clippedEnd;
        else
            snapshot.WorkSegments.Add(new SystemDayWorkSegment { StartLocal = clippedStart, EndLocal = clippedEnd, State = state });
        double minutes = Math.Max(0.0, (clippedEnd - clippedStart).TotalMinutes);
        if (state == SystemDayWorkState.Active) snapshot.ActiveMinutes += minutes;
        else if (state == SystemDayWorkState.Idle) snapshot.IdleMinutes += minutes;
        else if (state == SystemDayWorkState.Sleep) snapshot.SleepMinutes += minutes;
    }

    private static void BuildPeaks(SystemDayBoardSnapshot snapshot, List<SystemDayHistoryEntry> samples)
    {
        AddPeak(snapshot, samples, "cpu", "%", delegate(SystemDayHistoryEntry e) { return e.CpuPercent; }, null);
        AddPeak(snapshot, samples, "gpu", "%", delegate(SystemDayHistoryEntry e) { return e.GpuPercent; }, null);
        AddPeak(snapshot, samples, "npu", "%", delegate(SystemDayHistoryEntry e) { return e.NpuPercent; }, null);
        AddPeak(snapshot, samples, "memory", "%", delegate(SystemDayHistoryEntry e) { return e.MemoryPercent; }, null);
        AddPeak(snapshot, samples, "network", "B/s", delegate(SystemDayHistoryEntry e)
        {
            return e.NetworkSentBytesPerSecond + e.NetworkReceivedBytesPerSecond;
        }, null);
        AddPeak(snapshot, samples, "power", "W", delegate(SystemDayHistoryEntry e) { return e.WattsKnown ? e.Watts : 0.0; }, null);
        AddPeak(snapshot, samples, "temperature", "°C", delegate(SystemDayHistoryEntry e) { return e.MaxCelsius; },
            delegate(SystemDayHistoryEntry e) { return e.HotZoneName; });
    }

    private static void AddPeak(
        SystemDayBoardSnapshot snapshot,
        List<SystemDayHistoryEntry> samples,
        string metricId,
        string unit,
        Func<SystemDayHistoryEntry, double> selector,
        Func<SystemDayHistoryEntry, string> zoneSelector)
    {
        SystemDayHistoryEntry best = null;
        double value = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            double candidate = SafeNonNegative(selector(samples[i]));
            if (best == null || candidate > value)
            {
                best = samples[i];
                value = candidate;
            }
        }
        snapshot.Peaks.Add(new SystemDayMetricPeak
        {
            MetricId = metricId,
            Value = value,
            TimestampLocal = best == null ? DateTime.MinValue : best.TimestampUtc.ToLocalTime(),
            Unit = unit,
            ZoneName = best == null || zoneSelector == null ? string.Empty : zoneSelector(best) ?? string.Empty
        });
    }

    private static List<SystemDayHistoryEntry> Downsample(List<SystemDayHistoryEntry> samples, int maximum)
    {
        if (samples.Count <= maximum) return samples;
        List<SystemDayHistoryEntry> result = new List<SystemDayHistoryEntry>(maximum);
        for (int bucket = 0; bucket < maximum; bucket++)
        {
            int start = (int)Math.Floor(bucket * samples.Count / (double)maximum);
            int end = (int)Math.Floor((bucket + 1) * samples.Count / (double)maximum);
            if (end <= start) end = start + 1;
            if (end > samples.Count) end = samples.Count;
            SystemDayHistoryEntry aggregate = samples[end - 1].Clone();
            double cpu = 0.0, gpu = 0.0, npu = 0.0, memory = 0.0, networkSent = 0.0, networkReceived = 0.0;
            double watts = 0.0, maxC = 0.0, avgC = 0.0;
            string hotZone = aggregate.HotZoneName;
            int count = end - start;
            for (int i = start; i < end; i++)
            {
                SystemDayHistoryEntry item = samples[i];
                cpu = Math.Max(cpu, item.CpuPercent);
                gpu = Math.Max(gpu, item.GpuPercent);
                npu = Math.Max(npu, item.NpuPercent);
                memory = Math.Max(memory, item.MemoryPercent);
                networkSent = Math.Max(networkSent, item.NetworkSentBytesPerSecond);
                networkReceived = Math.Max(networkReceived, item.NetworkReceivedBytesPerSecond);
                watts = Math.Max(watts, item.WattsKnown ? item.Watts : 0.0);
                avgC += item.AvgCelsius;
                if (item.MaxCelsius >= maxC) { maxC = item.MaxCelsius; hotZone = item.HotZoneName; }
            }
            aggregate.CpuPercent = cpu;
            aggregate.GpuPercent = gpu;
            aggregate.NpuPercent = npu;
            aggregate.MemoryPercent = memory;
            aggregate.NetworkSentBytesPerSecond = networkSent;
            aggregate.NetworkReceivedBytesPerSecond = networkReceived;
            aggregate.Watts = watts;
            aggregate.MaxCelsius = maxC;
            aggregate.AvgCelsius = count > 0 ? avgC / count : 0.0;
            aggregate.HotZoneName = hotZone;
            result.Add(aggregate);
        }
        return result;
    }

    private static SystemDayBoardPoint ToBoardPoint(SystemDayHistoryEntry entry)
    {
        return new SystemDayBoardPoint
        {
            TimestampLocal = entry.TimestampUtc.ToLocalTime(),
            WorkState = entry.WorkState,
            CpuPercent = entry.CpuPercent,
            GpuPercent = entry.GpuPercent,
            NpuPercent = entry.NpuPercent,
            MemoryPercent = entry.MemoryPercent,
            NetworkBytesPerSecond = entry.NetworkSentBytesPerSecond + entry.NetworkReceivedBytesPerSecond,
            BatteryKnown = entry.BatteryKnown,
            BatteryPercent = entry.BatteryPercent,
            BatteryDirection = entry.BatteryDirection,
            Charging = entry.Charging,
            PluggedIn = entry.PluggedIn,
            WattsKnown = entry.WattsKnown,
            Watts = entry.Watts,
            TemperatureKnown = entry.MaxCelsius > 0.0,
            MaxCelsius = entry.MaxCelsius,
            AvgCelsius = entry.AvgCelsius,
            HotZoneName = entry.HotZoneName ?? string.Empty
        };
    }

    private static void BuildBatteryEta(
        SystemDayBoardSnapshot snapshot,
        List<SystemDayHistoryEntry> samples,
        SystemDayHistoryEntry current)
    {
        if (!current.BatteryKnown)
        {
            snapshot.BatteryEtaText = "电量未知";
            return;
        }
        if (current.BatteryCarePauseActive && current.BatteryPercent >= 79)
        {
            snapshot.BatteryEtaTargetPercent = 80;
            snapshot.BatteryEtaText = "80% 保护已到";
            return;
        }
        if (!current.Charging && current.RuntimeSecondsKnown && current.RuntimeSeconds > 0)
        {
            snapshot.BatteryEtaKnown = true;
            snapshot.BatteryEtaMinutes = Math.Max(1, (int)Math.Round(current.RuntimeSeconds / 60.0));
            snapshot.BatteryEtaTargetPercent = 0;
            snapshot.BatteryEtaText = "约 " + FormatMinutes(snapshot.BatteryEtaMinutes) + " 后耗尽";
            return;
        }

        int target = current.Charging && current.BatteryCarePauseActive ? 80 : current.Charging ? 100 : 0;
        DateTime cutoff = current.TimestampUtc.AddHours(-3.0);
        SystemDayHistoryEntry baseline = null;
        for (int i = samples.Count - 2; i >= 0; i--)
        {
            SystemDayHistoryEntry candidate = samples[i];
            if (!candidate.BatteryKnown || candidate.TimestampUtc < cutoff) break;
            int delta = current.BatteryPercent - candidate.BatteryPercent;
            if ((current.Charging && delta >= 1) || (!current.Charging && delta <= -1)) baseline = candidate;
        }
        if (baseline == null)
        {
            snapshot.BatteryEtaTargetPercent = target;
            snapshot.BatteryEtaText = current.Charging ? "正在估算充电时间" : "正在估算续航";
            return;
        }
        double elapsedMinutes = (current.TimestampUtc - baseline.TimestampUtc).TotalMinutes;
        double percentPerMinute = (current.BatteryPercent - baseline.BatteryPercent) / Math.Max(1.0, elapsedMinutes);
        double remainingPercent = current.Charging ? target - current.BatteryPercent : current.BatteryPercent;
        if ((current.Charging && percentPerMinute <= 0.0) || (!current.Charging && percentPerMinute >= 0.0) || remainingPercent <= 0.0)
        {
            snapshot.BatteryEtaTargetPercent = target;
            snapshot.BatteryEtaText = current.Charging ? "目标电量已到" : "正在估算续航";
            return;
        }
        int minutes = (int)Math.Ceiling(Math.Abs(remainingPercent / percentPerMinute));
        if (minutes <= 0 || minutes > 7 * 24 * 60) return;
        snapshot.BatteryEtaKnown = true;
        snapshot.BatteryEtaMinutes = minutes;
        snapshot.BatteryEtaTargetPercent = target;
        snapshot.BatteryEtaText = current.Charging
            ? "约 " + FormatMinutes(minutes) + " 到 " + target.ToString(CultureInfo.InvariantCulture) + "%"
            : "约 " + FormatMinutes(minutes) + " 后耗尽";
    }

    private static string FormatMinutes(int totalMinutes)
    {
        int minutes = Math.Max(0, totalMinutes);
        int hours = minutes / 60;
        int remainder = minutes % 60;
        return hours > 0
            ? hours.ToString(CultureInfo.InvariantCulture) + "小时" + (remainder > 0 ? remainder.ToString(CultureInfo.InvariantCulture) + "分" : string.Empty)
            : remainder.ToString(CultureInfo.InvariantCulture) + "分";
    }

    private void Load()
    {
        try
        {
            if (!Directory.Exists(this.rootPath)) return;
            DateTime cutoffUtc = DateTime.UtcNow.AddDays(-RetentionDays);
            string[] files = Directory.GetFiles(this.rootPath, FilePrefix + "*" + FileSuffix, SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            for (int f = 0; f < files.Length; f++)
            {
                string[] lines = File.ReadAllLines(files[f], SharedEncoding.Utf8NoBom);
                for (int i = 0; i < lines.Length; i++)
                {
                    SystemDayHistoryEntry entry;
                    if (TryDeserialize(serializer, lines[i], out entry) && entry.TimestampUtc >= cutoffUtc)
                        this.entries.Add(entry);
                }
            }
            this.entries.Sort(delegate(SystemDayHistoryEntry left, SystemDayHistoryEntry right)
            {
                return left.TimestampUtc.CompareTo(right.TimestampUtc);
            });
            TrimMemoryLocked(DateTime.UtcNow);
            this.lastRetentionUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void Flush(bool forceRetention)
    {
        lock (this.flushSyncRoot)
        {
            Dictionary<string, List<string>> batches = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool runRetention;
            lock (this.syncRoot)
            {
                foreach (KeyValuePair<string, List<string>> pair in this.pendingLines)
                    batches[pair.Key] = new List<string>(pair.Value);
                this.pendingLines.Clear();
                DateTime nowUtc = DateTime.UtcNow;
                runRetention = forceRetention || this.lastRetentionUtc == DateTime.MinValue ||
                    (nowUtc - this.lastRetentionUtc).TotalHours >= 6.0;
                if (runRetention) this.lastRetentionUtc = nowUtc;
            }
            try
            {
                Directory.CreateDirectory(this.rootPath);
                foreach (KeyValuePair<string, List<string>> pair in batches)
                {
                    if (pair.Value.Count > 0)
                        File.AppendAllText(pair.Key, string.Concat(pair.Value), SharedEncoding.Utf8NoBom);
                }
                if (runRetention) DeleteExpiredFiles();
            }
            catch (Exception ex)
            {
                // Re-queue unwritten lines. History failure is advisory and must not break sampling.
                lock (this.syncRoot)
                {
                    foreach (KeyValuePair<string, List<string>> pair in batches)
                    {
                        List<string> pending;
                        if (!this.pendingLines.TryGetValue(pair.Key, out pending))
                        {
                            pending = new List<string>();
                            this.pendingLines[pair.Key] = pending;
                        }
                        pending.InsertRange(0, pair.Value);
                    }
                }
                Program.LogException(ex);
            }
        }
    }

    private void DeleteExpiredFiles()
    {
        if (!Directory.Exists(this.rootPath)) return;
        DateTime cutoffDate = DateTime.Now.Date.AddDays(-RetentionDays);
        string[] files = Directory.GetFiles(this.rootPath, FilePrefix + "*" + FileSuffix, SearchOption.TopDirectoryOnly);
        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(files[i]);
            string dateText = name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(FilePrefix.Length)
                : string.Empty;
            DateTime date;
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date < cutoffDate)
                File.Delete(files[i]);
        }
    }

    private void TrimMemoryLocked(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddDays(-RetentionDays);
        while (this.entries.Count > 0 && this.entries[0].TimestampUtc < cutoff) this.entries.RemoveAt(0);
        while (this.entries.Count > MaximumEntries) this.entries.RemoveAt(0);
    }

    private string ResolvePath(DateTime localDate)
    {
        return Path.Combine(this.rootPath, FilePrefix + localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileSuffix);
    }

    private static string Serialize(SystemDayHistoryEntry entry)
    {
        List<Dictionary<string, object>> zones = new List<Dictionary<string, object>>();
        for (int i = 0; entry.ThermalZones != null && i < entry.ThermalZones.Count; i++)
        {
            SystemDayThermalZone zone = entry.ThermalZones[i];
            if (zone == null) continue;
            zones.Add(new Dictionary<string, object>
            {
                { "raw_name", zone.RawName ?? string.Empty },
                { "display_name", zone.DisplayName ?? string.Empty },
                { "celsius", Math.Round(zone.Celsius, 2) }
            });
        }
        Dictionary<string, object> data = new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "timestamp_utc", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) },
            { "timestamp_local", entry.TimestampUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture) },
            { "timezone", NetworkCheckHistoryLogger.GetTimezoneOffsetString() },
            { "event_type", entry.EventType ?? string.Empty },
            { "work_state", entry.WorkState.ToString().ToLowerInvariant() },
            { "idle_seconds", entry.IdleSeconds },
            { "cpu_percent", Math.Round(entry.CpuPercent, 2) },
            { "gpu_percent", Math.Round(entry.GpuPercent, 2) },
            { "npu_percent", Math.Round(entry.NpuPercent, 2) },
            { "memory_percent", Math.Round(entry.MemoryPercent, 2) },
            { "network_sent_bytes_per_second", Math.Round(entry.NetworkSentBytesPerSecond, 2) },
            { "network_received_bytes_per_second", Math.Round(entry.NetworkReceivedBytesPerSecond, 2) },
            { "battery_known", entry.BatteryKnown },
            { "battery_percent", entry.BatteryKnown ? (object)entry.BatteryPercent : null },
            { "battery_direction", entry.BatteryDirection.ToString().ToLowerInvariant() },
            { "charging", entry.Charging },
            { "plugged_in", entry.PluggedIn },
            { "watts_known", entry.WattsKnown },
            { "watts", entry.WattsKnown ? (object)Math.Round(entry.Watts, 2) : null },
            { "runtime_seconds_known", entry.RuntimeSecondsKnown },
            { "runtime_seconds", entry.RuntimeSecondsKnown ? (object)entry.RuntimeSeconds : null },
            { "battery_care_pause_active", entry.BatteryCarePauseActive },
            { "power_mode_text", entry.PowerModeText ?? string.Empty },
            { "max_celsius", entry.MaxCelsius > 0.0 ? (object)Math.Round(entry.MaxCelsius, 2) : null },
            { "avg_celsius", entry.AvgCelsius > 0.0 ? (object)Math.Round(entry.AvgCelsius, 2) : null },
            { "hot_zone_raw_name", entry.HotZoneRawName ?? string.Empty },
            { "hot_zone_name", entry.HotZoneName ?? string.Empty },
            { "thermal_zones", zones }
        };
        return new JavaScriptSerializer().Serialize(data) + "\n";
    }

    private static bool TryDeserialize(JavaScriptSerializer serializer, string line, out SystemDayHistoryEntry entry)
    {
        entry = null;
        try
        {
            Dictionary<string, object> data = serializer.DeserializeObject(line) as Dictionary<string, object>;
            if (data == null) return false;
            DateTime timestamp;
            if (!DateTime.TryParse(ReadString(data, "timestamp_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                return false;
            entry = new SystemDayHistoryEntry
            {
                TimestampUtc = NormalizeUtc(timestamp),
                EventType = ReadString(data, "event_type"),
                WorkState = ParseWorkState(ReadString(data, "work_state")),
                IdleSeconds = ReadInt(data, "idle_seconds", 0),
                CpuPercent = ReadDouble(data, "cpu_percent", 0.0),
                GpuPercent = ReadDouble(data, "gpu_percent", 0.0),
                NpuPercent = ReadDouble(data, "npu_percent", 0.0),
                MemoryPercent = ReadDouble(data, "memory_percent", 0.0),
                NetworkSentBytesPerSecond = ReadDouble(data, "network_sent_bytes_per_second", 0.0),
                NetworkReceivedBytesPerSecond = ReadDouble(data, "network_received_bytes_per_second", 0.0),
                BatteryKnown = ReadBool(data, "battery_known"),
                BatteryPercent = ReadInt(data, "battery_percent", 0),
                BatteryDirection = ParseBatteryDirection(ReadString(data, "battery_direction")),
                Charging = ReadBool(data, "charging"),
                PluggedIn = ReadBool(data, "plugged_in"),
                WattsKnown = ReadBool(data, "watts_known"),
                Watts = ReadDouble(data, "watts", 0.0),
                RuntimeSecondsKnown = ReadBool(data, "runtime_seconds_known"),
                RuntimeSeconds = ReadInt(data, "runtime_seconds", 0),
                BatteryCarePauseActive = ReadBool(data, "battery_care_pause_active"),
                PowerModeText = ReadString(data, "power_mode_text"),
                MaxCelsius = ReadDouble(data, "max_celsius", 0.0),
                AvgCelsius = ReadDouble(data, "avg_celsius", 0.0),
                HotZoneRawName = ReadString(data, "hot_zone_raw_name"),
                HotZoneName = ReadString(data, "hot_zone_name"),
                ThermalZones = new List<SystemDayThermalZone>()
            };
            object rawZones;
            if (data.TryGetValue("thermal_zones", out rawZones))
            {
                object[] items = rawZones as object[];
                for (int i = 0; items != null && i < items.Length; i++)
                {
                    Dictionary<string, object> zone = items[i] as Dictionary<string, object>;
                    if (zone == null) continue;
                    entry.ThermalZones.Add(new SystemDayThermalZone
                    {
                        RawName = ReadString(zone, "raw_name"),
                        DisplayName = ReadString(zone, "display_name"),
                        Celsius = ReadDouble(zone, "celsius", 0.0)
                    });
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private static double SafeNonNegative(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 ? 0.0 : value;
    }

    private static string ReadString(Dictionary<string, object> data, string key)
    {
        object value;
        return data.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(Dictionary<string, object> data, string key, int fallback)
    {
        object value;
        try { return data.TryGetValue(key, out value) && value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : fallback; }
        catch { return fallback; }
    }

    private static double ReadDouble(Dictionary<string, object> data, string key, double fallback)
    {
        object value;
        try { return data.TryGetValue(key, out value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : fallback; }
        catch { return fallback; }
    }

    private static bool ReadBool(Dictionary<string, object> data, string key)
    {
        object value;
        try { return data.TryGetValue(key, out value) && value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static SystemDayWorkState ParseWorkState(string value)
    {
        if (string.Equals(value, "active", StringComparison.OrdinalIgnoreCase)) return SystemDayWorkState.Active;
        if (string.Equals(value, "idle", StringComparison.OrdinalIgnoreCase)) return SystemDayWorkState.Idle;
        if (string.Equals(value, "sleep", StringComparison.OrdinalIgnoreCase)) return SystemDayWorkState.Sleep;
        return SystemDayWorkState.Unknown;
    }

    private static SystemDayBatteryDirection ParseBatteryDirection(string value)
    {
        if (string.Equals(value, "rising", StringComparison.OrdinalIgnoreCase)) return SystemDayBatteryDirection.Rising;
        if (string.Equals(value, "falling", StringComparison.OrdinalIgnoreCase)) return SystemDayBatteryDirection.Falling;
        if (string.Equals(value, "flat", StringComparison.OrdinalIgnoreCase)) return SystemDayBatteryDirection.Flat;
        return SystemDayBatteryDirection.Unknown;
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), ProductIdentity.MachineName + "-system-day-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Keep the fixture within the active retention horizon so normal cleanup cannot
            // erase the rows before the persistence assertions run.
            DateTime now = DateTime.UtcNow;
            using (SystemDayHistoryStore store = new SystemDayHistoryStore(root))
            {
                store.RecordSuspend(now.AddHours(-25));
                store.RecordResume(now.AddHours(-23));
                PerfSnapshot perf = new PerfSnapshot { CpuPercent = 25, GpuPercent = 12, NpuPercent = 4, MemoryPercent = 48 };
                PowerStripSnapshot power = new PowerStripSnapshot
                {
                    BatteryPercentKnown = true,
                    BatteryPercent = 60,
                    WattsKnown = true,
                    Watts = 11.2,
                    MaxCelsius = 42.5,
                    AvgCelsius = 35.0
                };
                power.ThermalZones.Add(new PowerStripZone { RawName = "ACPI\\ThermalZone.TZ99", Name = "TZ99", Celsius = 42.5 });
                store.RecordSample(perf, power, SystemDayWorkState.Active, 2, now.AddMinutes(-3));
                power.BatteryPercent = 61;
                power.Charging = true;
                store.RecordSample(perf, power, SystemDayWorkState.Idle, 480, now.AddMinutes(-2));
                store.RecordSuspend(now.AddMinutes(-1));
                store.RecordResume(now);
                SystemDayBoardSnapshot snapshot = store.GetBoardSnapshot(SystemDayRange.Last24Hours, now.ToLocalTime());
                if (snapshot.RawSampleCount != 2 || snapshot.Points[1].BatteryDirection != SystemDayBatteryDirection.Rising ||
                    snapshot.SleepMinutes < 60.9 || snapshot.CurrentHotZoneName != "TZ99")
                    throw new InvalidOperationException("System Day history projection did not preserve direction, sleep or full thermal zones.");
            }
            string[] files = Directory.GetFiles(root, FilePrefix + "*" + FileSuffix);
            if (files.Length == 0) throw new InvalidOperationException("System Day history did not persist a daily JSONL file.");
            string[] lines = File.ReadAllLines(files[0], SharedEncoding.Utf8NoBom);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> row = serializer.DeserializeObject(lines[0]) as Dictionary<string, object>;
            if (row == null || !row.ContainsKey("thermal_zones") || !row.ContainsKey("battery_direction"))
                throw new InvalidOperationException("System Day JSONL is missing correlation fields.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine("System Day history: PASS daily JSONL, rise/fall direction, sleep intervals, full thermal zones, peaks and ETA");
    }
}

internal sealed class SystemDayHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; }
    public SystemDayWorkState WorkState { get; set; }
    public int IdleSeconds { get; set; }
    public double CpuPercent { get; set; }
    public double GpuPercent { get; set; }
    public double NpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public double NetworkSentBytesPerSecond { get; set; }
    public double NetworkReceivedBytesPerSecond { get; set; }
    public bool BatteryKnown { get; set; }
    public int BatteryPercent { get; set; }
    public SystemDayBatteryDirection BatteryDirection { get; set; }
    public bool Charging { get; set; }
    public bool PluggedIn { get; set; }
    public bool WattsKnown { get; set; }
    public double Watts { get; set; }
    public bool RuntimeSecondsKnown { get; set; }
    public int RuntimeSeconds { get; set; }
    public bool BatteryCarePauseActive { get; set; }
    public string PowerModeText { get; set; }
    public double MaxCelsius { get; set; }
    public double AvgCelsius { get; set; }
    public string HotZoneRawName { get; set; }
    public string HotZoneName { get; set; }
    public List<SystemDayThermalZone> ThermalZones { get; set; }

    public SystemDayHistoryEntry Clone()
    {
        SystemDayHistoryEntry clone = (SystemDayHistoryEntry)this.MemberwiseClone();
        clone.ThermalZones = new List<SystemDayThermalZone>();
        for (int i = 0; this.ThermalZones != null && i < this.ThermalZones.Count; i++)
            if (this.ThermalZones[i] != null) clone.ThermalZones.Add(this.ThermalZones[i].Clone());
        return clone;
    }
}

internal sealed class SystemDayThermalZone
{
    public string RawName { get; set; }
    public string DisplayName { get; set; }
    public double Celsius { get; set; }

    public SystemDayThermalZone Clone()
    {
        return (SystemDayThermalZone)this.MemberwiseClone();
    }
}
