using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

/// <summary>
/// Buffered JSONL history for Codex quota ring decisions.
/// The quota reader can run every few seconds while Codex is active, so rows are
/// buffered and trimmed on a coarse cadence instead of rewriting on each decision.
/// </summary>
internal static class QuotaDecisionHistoryLogger
{
    private const int RetentionHours = 48;
    private const int TrimCheckIntervalMinutes = 6 * 60;
    private const int FlushIntervalMs = 15 * 1000;
    private const int MaxBufferedHistoryBytes = 32 * 1024;
    private const int MaxTextFieldLength = 240;
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(RetentionHours);
    private static readonly object SyncRoot = new object();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly StringBuilder PendingLines = new StringBuilder();
    private static readonly Timer FlushTimer;
    private static DateTime lastTrimUtc = DateTime.MinValue;
    private static int pendingBytes;
    private static bool shuttingDown;
    private static string testLogPathOverride;

    static QuotaDecisionHistoryLogger()
    {
        FlushTimer = new Timer(
            delegate { Flush(); },
            null,
            FlushIntervalMs,
            FlushIntervalMs);
        AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };
    }

    public static string LogPath
    {
        get
        {
            if (!string.IsNullOrEmpty(testLogPathOverride))
            {
                return testLogPathOverride;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName,
                "quota-decision-history.jsonl");
        }
    }

    public static void Initialize()
    {
        Trim();
    }

    public static void LogDecision(
        string reason,
        bool sourceKnown,
        bool codexRunning,
        Dictionary<string, object> detail)
    {
        DateTime nowUtc = DateTime.UtcNow;
        Log(new QuotaDecisionHistoryEntry
        {
            TimestampUtc = nowUtc,
            TimestampLocal = nowUtc.ToLocalTime(),
            Timezone = NetworkCheckHistoryLogger.GetTimezoneOffsetString(),
            Reason = reason,
            SourceKnown = sourceKnown,
            CodexRunning = codexRunning,
            Detail = detail
        });
    }

    public static void Log(QuotaDecisionHistoryEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        try
        {
            NormalizeEntry(entry);
            string line = SerializeEntry(entry);
            lock (SyncRoot)
            {
                if (shuttingDown)
                {
                    AppendTextLocked(line);
                    return;
                }

                PendingLines.Append(line);
                pendingBytes += Utf8NoBom.GetByteCount(line);
                if (pendingBytes >= MaxBufferedHistoryBytes)
                {
                    FlushBufferLocked();
                }
            }

            TryTrim();
        }
        catch
        {
            // Diagnostic logging must never affect quota rendering or reading.
        }
    }

    public static void Trim()
    {
        lock (SyncRoot)
        {
            FlushBufferLocked();
            lastTrimUtc = DateTime.UtcNow;
            TrimLocked();
        }
    }

    public static void Flush()
    {
        try
        {
            lock (SyncRoot)
            {
                FlushBufferLocked();
            }
        }
        catch
        {
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            FlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            FlushBufferLocked();
            FlushTimer.Dispose();
        }
    }

    private static void TryTrim()
    {
        DateTime now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            bool shouldTrim = lastTrimUtc == DateTime.MinValue ||
                (now - lastTrimUtc).TotalMinutes >= TrimCheckIntervalMinutes;
            if (shouldTrim)
            {
                lastTrimUtc = now;
                FlushBufferLocked();
                TrimLocked();
            }
        }
    }

    private static void FlushBufferLocked()
    {
        if (PendingLines.Length == 0)
        {
            return;
        }

        string text = PendingLines.ToString();
        PendingLines.Clear();
        pendingBytes = 0;
        AppendTextLocked(text);
    }

    private static void AppendTextLocked(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
        File.AppendAllText(LogPath, text, Utf8NoBom);
    }

    private static void TrimLocked()
    {
        string path = LogPath;
        if (!File.Exists(path))
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - RetentionWindow;
        List<string> kept = new List<string>();
        bool removed = false;

        try
        {
            string[] lines = File.ReadAllLines(path, Utf8NoBom);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    removed = true;
                    continue;
                }

                if (!TryParseTimestamp(line, out DateTime timestamp) || timestamp < cutoff)
                {
                    removed = true;
                    continue;
                }

                kept.Add(line);
            }

            if (removed)
            {
                if (kept.Count == 0)
                {
                    File.Delete(path);
                }
                else
                {
                    File.WriteAllLines(path, kept, Utf8NoBom);
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryParseTimestamp(string line, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;
        try
        {
            const string prefix = "\"timestamp_utc\":\"";
            int idx = line.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            int start = idx + prefix.Length;
            int end = line.IndexOf('"', start);
            if (end < 0)
            {
                return false;
            }

            string ts = line.Substring(start, end - start);
            return DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
        }
        catch
        {
            return false;
        }
    }

    private static string SerializeEntry(QuotaDecisionHistoryEntry entry)
    {
        Dictionary<string, object> dict = new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "timestamp_utc", entry.TimestampUtc.ToString("o") },
            { "timestamp_local", entry.TimestampLocal.ToString("yyyy-MM-ddTHH:mm:ss") },
            { "timezone", entry.Timezone },
            { "module", "codex_radar" },
            { "check_name", "quota_ring_decision" },
            { "reason", entry.Reason ?? string.Empty },
            { "source_known", entry.SourceKnown },
            { "codex_running", entry.CodexRunning }
        };

        if (entry.Detail != null && entry.Detail.Count > 0)
        {
            dict["detail"] = entry.Detail;
        }

        return new JavaScriptSerializer().Serialize(dict) + "\n";
    }

    private static void NormalizeEntry(QuotaDecisionHistoryEntry entry)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (entry.TimestampUtc == DateTime.MinValue)
        {
            entry.TimestampUtc = nowUtc;
        }

        if (entry.TimestampUtc.Kind == DateTimeKind.Local)
        {
            entry.TimestampUtc = entry.TimestampUtc.ToUniversalTime();
        }

        if (entry.TimestampLocal == DateTime.MinValue)
        {
            entry.TimestampLocal = entry.TimestampUtc.ToLocalTime();
        }

        entry.Timezone = NormalizeString(
            string.IsNullOrWhiteSpace(entry.Timezone)
                ? NetworkCheckHistoryLogger.GetTimezoneOffsetString()
                : entry.Timezone,
            16);
        entry.Reason = NormalizeString(entry.Reason, 160);
        if (entry.Detail != null)
        {
            Dictionary<string, object> normalized = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> item in entry.Detail)
            {
                string key = NormalizeString(item.Key, 80);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                normalized[key] = NormalizeDetailValue(item.Value);
            }

            entry.Detail = normalized;
        }
    }

    private static object NormalizeDetailValue(object value)
    {
        if (value == null ||
            value is bool ||
            value is int ||
            value is long ||
            value is double ||
            value is float ||
            value is decimal)
        {
            return value;
        }

        if (value is DateTime)
        {
            DateTime dateTime = (DateTime)value;
            return dateTime == DateTime.MinValue ? null : dateTime.ToString("o");
        }

        return NormalizeString(Convert.ToString(value, CultureInfo.InvariantCulture), MaxTextFieldLength);
    }

    private static string NormalizeString(string value, int maxLength)
    {
        string text = (value ?? string.Empty).Trim();
        if (maxLength > 0 && text.Length > maxLength)
        {
            return text.Substring(0, maxLength);
        }

        return text;
    }

    public static void RunSelfTest()
    {
        string testPath = Path.Combine(
            Path.GetTempPath(),
            ProductIdentity.MachineName + "-QuotaDecisionHistoryTest-" + Guid.NewGuid().ToString("N"),
            "quota-decision-history.jsonl");
        string testDir = Path.GetDirectoryName(testPath);
        string previousOverride = null;

        try
        {
            Directory.CreateDirectory(testDir);
            lock (SyncRoot)
            {
                FlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                FlushBufferLocked();
                previousOverride = testLogPathOverride;
                testLogPathOverride = testPath;
                PendingLines.Clear();
                pendingBytes = 0;
                lastTrimUtc = DateTime.UtcNow;
            }

            LogDecision(
                "self_test_decrease",
                true,
                true,
                new Dictionary<string, object>
                {
                    { "five_hour_balance_percent", 57 },
                    { "five_hour_consumption_ring_percent", 10 },
                    { "five_hour_consumption_baseline_percent", 67 },
                    { "weekly_balance_percent", 80 },
                    { "weekly_consumption_ring_percent", 5 },
                    { "weekly_consumption_baseline_percent", 85 }
                });
            LogDecision(
                "self_test_same_balance_keep_ring",
                true,
                true,
                new Dictionary<string, object>
                {
                    { "five_hour_balance_percent", 57 },
                    { "five_hour_consumption_ring_percent", 10 },
                    { "five_hour_consumption_baseline_percent", 67 }
                });

            lock (SyncRoot)
            {
                if (pendingBytes <= 0 || PendingLines.Length == 0)
                {
                    throw new InvalidOperationException("Quota decision history self-test: buffered append did not retain rows.");
                }
            }

            if (File.Exists(testPath))
            {
                throw new InvalidOperationException("Quota decision history self-test: buffered append wrote before Flush().");
            }

            Flush();
            string[] readLines = File.ReadAllLines(testPath, Utf8NoBom);
            if (readLines.Length != 2)
            {
                throw new InvalidOperationException("Quota decision history self-test: expected 2 lines, got " + readLines.Length);
            }

            Dictionary<string, object> parsed = new JavaScriptSerializer().DeserializeObject(readLines[0]) as Dictionary<string, object>;
            if (parsed == null ||
                !parsed.ContainsKey("schema_version") ||
                (int)parsed["schema_version"] != 1 ||
                !string.Equals("quota_ring_decision", parsed["check_name"] as string, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Quota decision history self-test: JSONL shape mismatch.");
            }

            Dictionary<string, object> detail = parsed["detail"] as Dictionary<string, object>;
            if (detail == null ||
                Convert.ToInt32(detail["five_hour_balance_percent"], CultureInfo.InvariantCulture) != 57 ||
                Convert.ToInt32(detail["five_hour_consumption_ring_percent"], CultureInfo.InvariantCulture) != 10)
            {
                throw new InvalidOperationException("Quota decision history self-test: detail fields mismatch.");
            }

            DateTime oldTime = DateTime.UtcNow - TimeSpan.FromHours(49);
            Dictionary<string, object> oldEntry = new Dictionary<string, object>
            {
                { "schema_version", 1 },
                { "timestamp_utc", oldTime.ToString("o") },
                { "timestamp_local", oldTime.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss") },
                { "timezone", NetworkCheckHistoryLogger.GetTimezoneOffsetString() },
                { "module", "codex_radar" },
                { "check_name", "quota_ring_decision" },
                { "reason", "old_test" },
                { "source_known", true },
                { "codex_running", true }
            };
            File.WriteAllText(testPath, new JavaScriptSerializer().Serialize(oldEntry) + "\n" + readLines[0] + "\n", Utf8NoBom);
            Trim();
            string[] trimmedLines = File.ReadAllLines(testPath, Utf8NoBom);
            if (trimmedLines.Length != 1)
            {
                throw new InvalidOperationException("Quota decision history self-test: trim logic expected 1 kept row, got " + trimmedLines.Length);
            }
        }
        finally
        {
            try
            {
                lock (SyncRoot)
                {
                    FlushBufferLocked();
                    testLogPathOverride = previousOverride;
                    PendingLines.Clear();
                    pendingBytes = 0;
                    lastTrimUtc = DateTime.UtcNow;
                    if (!shuttingDown)
                    {
                        FlushTimer.Change(FlushIntervalMs, FlushIntervalMs);
                    }
                }

                if (Directory.Exists(testDir))
                {
                    Directory.Delete(testDir, true);
                }
            }
            catch
            {
            }
        }
    }
}

internal sealed class QuotaDecisionHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public DateTime TimestampLocal { get; set; }
    public string Timezone { get; set; }
    public string Reason { get; set; }
    public bool SourceKnown { get; set; }
    public bool CodexRunning { get; set; }
    public Dictionary<string, object> Detail { get; set; }
}
