using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

// Seven-day, credential-free quota history owned by CodexRadarForm. Record() updates the in-memory
// projection immediately and buffers disk work onto a ThreadPool timer, so owner/UI ticks never wait
// for an append. Snapshot construction only clones memory and therefore remains cache-only.
internal sealed class CodexQuotaHistoryStore : IDisposable
{
    private const int RetentionDays = 7;
    private const int MaximumEntries = 2048;
    private const int FlushIntervalMs = 15000;
    private const int MinimumSampleMinutes = 15;
    private const int ImmediateChangePercent = 3;
    private const int ResetJumpPercent = 5;
    private readonly object syncRoot = new object();
    private readonly object flushSyncRoot = new object();
    private readonly string path;
    private readonly Timer flushTimer;
    private readonly List<CodexQuotaHistoryEntry> entries = new List<CodexQuotaHistoryEntry>();
    private readonly List<string> pendingLines = new List<string>();
    private DateTime lastTrimUtc = DateTime.MinValue;
    private bool disposed;

    internal CodexQuotaHistoryStore(string pathOverride = null)
    {
        this.path = string.IsNullOrWhiteSpace(pathOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName,
                "codex-quota-seven-day-history.jsonl")
            : pathOverride;
        Load();
        this.flushTimer = new Timer(delegate { Flush(false); }, null, FlushIntervalMs, FlushIntervalMs);
    }

    internal string PathForDiagnostics
    {
        get { return this.path; }
    }

    internal void Record(
        int fiveHourRemainingPercent,
        int weeklyRemainingPercent,
        bool weeklyResetKnown,
        DateTime weeklyResetLocal,
        bool resetCreditsKnown,
        int resetCreditCount,
        DateTime nowUtc)
    {
        DateTime normalizedUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        CodexQuotaHistoryEntry next = new CodexQuotaHistoryEntry
        {
            TimestampUtc = normalizedUtc,
            FiveHourRemainingPercent = ClampPercent(fiveHourRemainingPercent),
            WeeklyRemainingPercent = ClampPercent(weeklyRemainingPercent),
            WeeklyResetKnown = weeklyResetKnown && weeklyResetLocal != DateTime.MinValue,
            WeeklyResetLocal = weeklyResetKnown ? weeklyResetLocal : DateTime.MinValue,
            ResetCreditsKnown = resetCreditsKnown,
            ResetCreditCount = Math.Max(0, resetCreditCount),
            ResetKind = string.Empty
        };

        lock (this.syncRoot)
        {
            if (this.disposed)
            {
                return;
            }

            CodexQuotaHistoryEntry previous = this.entries.Count > 0 ? this.entries[this.entries.Count - 1] : null;
            if (previous != null)
            {
                int weeklyIncrease = next.WeeklyRemainingPercent - previous.WeeklyRemainingPercent;
                if (weeklyIncrease >= ResetJumpPercent)
                {
                    DateTime observedLocal = normalizedUtc.ToLocalTime();
                    bool scheduled = previous.WeeklyResetKnown &&
                        previous.WeeklyResetLocal != DateTime.MinValue &&
                        observedLocal >= previous.WeeklyResetLocal.AddMinutes(-15.0) &&
                        observedLocal <= previous.WeeklyResetLocal.AddHours(6.0);
                    bool usedCredit = previous.ResetCreditsKnown && next.ResetCreditsKnown &&
                        next.ResetCreditCount < previous.ResetCreditCount;
                    next.ResetKind = scheduled ? "natural" : usedCredit ? "credit" : "hard";
                }

                double minutes = (normalizedUtc - previous.TimestampUtc).TotalMinutes;
                int materialChange = Math.Abs(next.WeeklyRemainingPercent - previous.WeeklyRemainingPercent);
                bool resetChanged = !string.Equals(next.ResetKind, string.Empty, StringComparison.Ordinal);
                if (!resetChanged && minutes < MinimumSampleMinutes && materialChange < ImmediateChangePercent)
                {
                    return;
                }
            }

            this.entries.Add(next);
            this.pendingLines.Add(Serialize(next));
            TrimMemoryLocked(normalizedUtc);
        }
    }

    internal CodexQuotaHistorySnapshot GetSnapshot(DateTime nowUtc)
    {
        DateTime cutoffUtc = (nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime()).AddDays(-RetentionDays);
        CodexQuotaHistorySnapshot snapshot = new CodexQuotaHistorySnapshot();
        lock (this.syncRoot)
        {
            for (int i = 0; i < this.entries.Count; i++)
            {
                CodexQuotaHistoryEntry entry = this.entries[i];
                if (entry != null && entry.TimestampUtc >= cutoffUtc)
                {
                    snapshot.Entries.Add(entry.Clone());
                }
            }
        }

        return snapshot;
    }

    public void Dispose()
    {
        lock (this.syncRoot)
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
        }
        this.flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Flush(true);
        this.flushTimer.Dispose();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this.path)) return;
            string[] lines = File.ReadAllLines(this.path, SharedEncoding.Utf8NoBom);
            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            for (int i = Math.Max(0, lines.Length - MaximumEntries); i < lines.Length; i++)
            {
                CodexQuotaHistoryEntry entry;
                if (TryDeserialize(serializer, lines[i], out entry) && entry.TimestampUtc >= cutoff)
                {
                    this.entries.Add(entry);
                }
            }
            this.lastTrimUtc = DateTime.UtcNow;
        }
        catch
        {
            // History is advisory. Corruption or an unavailable disk must not affect quota reads.
        }
    }

    private void Flush(bool forceTrim)
    {
        // The timer and Dispose can meet during shutdown. Serialize the complete append/rewrite
        // transaction so two flushes cannot race on the shared .tmp file or replace each other.
        lock (this.flushSyncRoot)
        {
            List<string> lines;
            bool trim;
            lock (this.syncRoot)
            {
                lines = new List<string>(this.pendingLines);
                this.pendingLines.Clear();
                DateTime nowUtc = DateTime.UtcNow;
                trim = forceTrim || this.lastTrimUtc == DateTime.MinValue ||
                    (nowUtc - this.lastTrimUtc).TotalHours >= 6.0;
                if (trim) this.lastTrimUtc = nowUtc;
            }

            try
            {
                string directory = Path.GetDirectoryName(this.path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (lines.Count > 0)
                {
                    File.AppendAllText(this.path, string.Concat(lines), SharedEncoding.Utf8NoBom);
                }
                if (trim) RewriteRetainedFile();
            }
            catch
            {
                // A missed history flush must never take down the owner or surface credentials.
            }
        }
    }

    private void RewriteRetainedFile()
    {
        CodexQuotaHistoryEntry[] retained;
        lock (this.syncRoot)
        {
            TrimMemoryLocked(DateTime.UtcNow);
            retained = this.entries.ToArray();
        }

        string directory = Path.GetDirectoryName(this.path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = this.path + ".tmp";
        using (StreamWriter writer = new StreamWriter(temporary, false, SharedEncoding.Utf8NoBom))
        {
            for (int i = 0; i < retained.Length; i++) writer.Write(Serialize(retained[i]));
        }
        if (File.Exists(this.path)) File.Replace(temporary, this.path, null);
        else File.Move(temporary, this.path);
    }

    private void TrimMemoryLocked(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddDays(-RetentionDays);
        while (this.entries.Count > 0 && this.entries[0].TimestampUtc < cutoff) this.entries.RemoveAt(0);
        while (this.entries.Count > MaximumEntries) this.entries.RemoveAt(0);
    }

    private static string Serialize(CodexQuotaHistoryEntry entry)
    {
        Dictionary<string, object> data = new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "timestamp_utc", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) },
            { "timestamp_local", entry.TimestampUtc.ToLocalTime().ToString("o", CultureInfo.InvariantCulture) },
            { "timezone", NetworkCheckHistoryLogger.GetTimezoneOffsetString() },
            { "five_hour_remaining_percent", entry.FiveHourRemainingPercent },
            { "weekly_remaining_percent", entry.WeeklyRemainingPercent },
            { "weekly_reset_known", entry.WeeklyResetKnown },
            { "weekly_reset_local", entry.WeeklyResetKnown ? entry.WeeklyResetLocal.ToString("o", CultureInfo.InvariantCulture) : null },
            { "reset_credits_known", entry.ResetCreditsKnown },
            { "reset_credit_count", entry.ResetCreditCount },
            { "reset_kind", entry.ResetKind ?? string.Empty }
        };
        return new JavaScriptSerializer().Serialize(data) + "\n";
    }

    private static bool TryDeserialize(JavaScriptSerializer serializer, string line, out CodexQuotaHistoryEntry entry)
    {
        entry = null;
        try
        {
            Dictionary<string, object> data = serializer.DeserializeObject(line) as Dictionary<string, object>;
            if (data == null || !data.ContainsKey("timestamp_utc") || !data.ContainsKey("weekly_remaining_percent")) return false;
            DateTime timestamp;
            if (!DateTime.TryParse(Convert.ToString(data["timestamp_utc"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp)) return false;
            DateTime resetLocal = DateTime.MinValue;
            bool resetKnown = ReadBool(data, "weekly_reset_known");
            if (resetKnown && data.ContainsKey("weekly_reset_local"))
            {
                DateTime.TryParse(Convert.ToString(data["weekly_reset_local"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out resetLocal);
            }
            entry = new CodexQuotaHistoryEntry
            {
                TimestampUtc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime(),
                FiveHourRemainingPercent = ClampPercent(ReadInt(data, "five_hour_remaining_percent", 100)),
                WeeklyRemainingPercent = ClampPercent(ReadInt(data, "weekly_remaining_percent", 100)),
                WeeklyResetKnown = resetKnown && resetLocal != DateTime.MinValue,
                WeeklyResetLocal = resetLocal,
                ResetCreditsKnown = ReadBool(data, "reset_credits_known"),
                ResetCreditCount = Math.Max(0, ReadInt(data, "reset_credit_count", 0)),
                ResetKind = data.ContainsKey("reset_kind") ? Convert.ToString(data["reset_kind"], CultureInfo.InvariantCulture) : string.Empty
            };
            return true;
        }
        catch { return false; }
    }

    private static int ReadInt(Dictionary<string, object> data, string key, int fallback)
    {
        try { return data.ContainsKey(key) ? Convert.ToInt32(data[key], CultureInfo.InvariantCulture) : fallback; }
        catch { return fallback; }
    }

    private static bool ReadBool(Dictionary<string, object> data, string key)
    {
        try { return data.ContainsKey(key) && Convert.ToBoolean(data[key], CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), ProductIdentity.MachineName + "-quota-seven-day-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "history.jsonl");
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
            using (CodexQuotaHistoryStore store = new CodexQuotaHistoryStore(path))
            {
                store.Record(80, 40, true, now.ToLocalTime().AddDays(1), true, 3, now.AddHours(-2));
                store.Record(99, 95, true, now.ToLocalTime().AddDays(7), true, 2, now);
                CodexQuotaHistorySnapshot snapshot = store.GetSnapshot(now);
                if (snapshot.Entries.Count != 2 || !string.Equals(snapshot.Entries[1].ResetKind, "credit", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Codex seven-day history did not classify a reset-credit jump.");
                }
            }
            string[] lines = File.ReadAllLines(path, SharedEncoding.Utf8NoBom);
            if (lines.Length != 2)
            {
                throw new InvalidOperationException("Codex seven-day history did not persist buffered rows.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

internal sealed class CodexQuotaHistorySnapshot
{
    public List<CodexQuotaHistoryEntry> Entries { get; private set; } = new List<CodexQuotaHistoryEntry>();
}

internal sealed class CodexQuotaHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public int FiveHourRemainingPercent { get; set; }
    public int WeeklyRemainingPercent { get; set; }
    public bool WeeklyResetKnown { get; set; }
    public DateTime WeeklyResetLocal { get; set; }
    public bool ResetCreditsKnown { get; set; }
    public int ResetCreditCount { get; set; }
    public string ResetKind { get; set; }

    public CodexQuotaHistoryEntry Clone()
    {
        return (CodexQuotaHistoryEntry)this.MemberwiseClone();
    }
}
