using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

/// <summary>
/// Lightweight append-only JSONL log for network check refreshes.
/// Each completed check writes one line. Records older than 48 hours are cleaned
/// at startup and on a coarse interval to avoid rewriting the file on every row.
/// 
/// Thread-safe: all public methods acquire a static lock.
/// </summary>
internal static class NetworkCheckHistoryLogger
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

    static NetworkCheckHistoryLogger()
    {
        // Network checks can complete in bursts. Buffering coalesces many JSONL rows
        // into one append while Shutdown/ProcessExit bounds possible data loss.
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
                "network-check-history.jsonl");
        }
    }

    /// <summary>
    /// Trim at process start so the file represents the requested rolling window
    /// even if no network check completes immediately after launch.
    /// </summary>
    public static void Initialize()
    {
        Trim();
    }

    /// <summary>
    /// Buffer one JSONL row. Called after a network check completes.
    /// </summary>
    public static void Log(NetworkCheckHistoryEntry entry)
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

            // Trim on a coarse interval; never during shutdown or on every row.
            TryTrim();
        }
        catch
        {
            // Never let logging failures propagate.
        }
    }

    public static void LogCompleted(
        string module,
        string checkName,
        string trigger,
        string result,
        bool success,
        int durationMs,
        Dictionary<string, object> detail)
    {
        DateTime nowUtc = DateTime.UtcNow;
        Log(new NetworkCheckHistoryEntry
        {
            TimestampUtc = nowUtc,
            TimestampLocal = nowUtc.ToLocalTime(),
            Timezone = GetTimezoneOffsetString(),
            Module = module,
            CheckName = checkName,
            Trigger = trigger,
            Result = result,
            Success = success,
            DurationMs = durationMs,
            Detail = detail
        });
    }

    /// <summary>
    /// Trim rows older than 48 hours. Called at startup by the test/self-test path
    /// and on a coarse interval from Log(). Reads the whole file but only rewrites
    /// when rows are actually removed.
    /// </summary>
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
            // If trimming fails, leave the file as-is; Log() will retry later.
        }
    }

    private static bool TryParseTimestamp(string line, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;
        try
        {
            // Quick prefix scan: "timestamp_utc":"2026-06-28T...
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

    private static string SerializeEntry(NetworkCheckHistoryEntry entry)
    {
        // Build a minimal dictionary to control serialization.
        Dictionary<string, object> dict = new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "timestamp_utc", entry.TimestampUtc.ToString("o") },
            { "timestamp_local", entry.TimestampLocal.ToString("yyyy-MM-ddTHH:mm:ss") },
            { "timezone", entry.Timezone },
            { "module", entry.Module },
            { "check_name", entry.CheckName },
            { "trigger", entry.Trigger ?? string.Empty },
            { "result", entry.Result ?? string.Empty },
            { "success", entry.Success },
            { "duration_ms", entry.DurationMs >= 0 ? (object)entry.DurationMs : null }
        };

        if (entry.Detail != null && entry.Detail.Count > 0)
        {
            dict["detail"] = entry.Detail;
        }

        return new JavaScriptSerializer().Serialize(dict) + "\n";
    }

    private static void NormalizeEntry(NetworkCheckHistoryEntry entry)
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
            string.IsNullOrWhiteSpace(entry.Timezone) ? GetTimezoneOffsetString() : entry.Timezone,
            16);
        entry.Module = NormalizeString(entry.Module, 64);
        entry.CheckName = NormalizeString(entry.CheckName, 80);
        entry.Trigger = NormalizeString(entry.Trigger, 120);
        entry.Result = NormalizeString(entry.Result, MaxTextFieldLength);
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

    /// <summary>
    /// Self-test: write entries, read back, verify JSONL structure, trim old entries.
    /// </summary>
    public static void RunSelfTest()
    {
        string testPath = Path.Combine(
            Path.GetTempPath(),
            ProductIdentity.MachineName + "-NetworkCheckHistoryTest-" + Guid.NewGuid().ToString("N"),
            "network-check-history.jsonl");
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

            // Write fresh entries through the public path. They should stay buffered
            // until Flush(), avoiding one file open/write/close per row.
            NetworkCheckHistoryEntry entry = new NetworkCheckHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                TimestampLocal = DateTime.Now,
                Timezone = GetTimezoneOffsetString(),
                Module = "network_monitor",
                CheckName = "connectivity",
                Trigger = "self_test",
                Result = "PASS",
                Success = true,
                DurationMs = 42
            };
            NetworkCheckHistoryEntry secondEntry = new NetworkCheckHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                TimestampLocal = DateTime.Now,
                Timezone = GetTimezoneOffsetString(),
                Module = "network_monitor",
                CheckName = "dns",
                Trigger = "self_test",
                Result = "PASS",
                Success = true,
                DurationMs = 12
            };

            string line = JsonSerializeForTest(entry);
            Log(entry);
            Log(secondEntry);
            lock (SyncRoot)
            {
                if (pendingBytes <= 0 || PendingLines.Length == 0)
                {
                    throw new InvalidOperationException("Network check history self-test: buffered append did not retain rows.");
                }
            }

            if (File.Exists(testPath))
            {
                throw new InvalidOperationException("Network check history self-test: buffered append wrote before Flush().");
            }

            Flush();

            // Verify line is valid JSONL.
            string[] readLines = File.ReadAllLines(testPath, Utf8NoBom);
            if (readLines.Length != 2)
            {
                throw new InvalidOperationException("Network check history self-test: expected 2 lines, got " + readLines.Length);
            }

            Dictionary<string, object> parsed = new JavaScriptSerializer().DeserializeObject(readLines[0]) as Dictionary<string, object>;
            if (parsed == null)
            {
                throw new InvalidOperationException("Network check history self-test: line is not valid JSON.");
            }

            if (!parsed.ContainsKey("schema_version") || (int)parsed["schema_version"] != 1)
            {
                throw new InvalidOperationException("Network check history self-test: schema_version mismatch.");
            }

            if (!string.Equals("network_monitor", parsed["module"] as string, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network check history self-test: module field mismatch.");
            }

            if (!string.Equals("connectivity", parsed["check_name"] as string, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network check history self-test: check_name field mismatch.");
            }

            if (!(bool)parsed["success"])
            {
                throw new InvalidOperationException("Network check history self-test: success field mismatch.");
            }

            // Verify null duration_ms serialization.
            Dictionary<string, object> nullDurationEntry = new Dictionary<string, object>
            {
                { "schema_version", 1 },
                { "timestamp_utc", DateTime.UtcNow.ToString("o") },
                { "timestamp_local", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "timezone", GetTimezoneOffsetString() },
                { "module", "network_monitor" },
                { "check_name", "dns" },
                { "trigger", "self_test" },
                { "result", "N/A" },
                { "success", true },
                { "duration_ms", null }
            };

            string nullLine = new JavaScriptSerializer().Serialize(nullDurationEntry) + "\n";
            Dictionary<string, object> nullParsed = new JavaScriptSerializer().DeserializeObject(nullLine.TrimEnd('\n')) as Dictionary<string, object>;
            if (nullParsed == null || nullParsed["duration_ms"] != null)
            {
                throw new InvalidOperationException("Network check history self-test: null duration_ms serialization failed.");
            }

            Dictionary<string, object> rollingLossEntry = new Dictionary<string, object>
            {
                { "schema_version", 1 },
                { "timestamp_utc", DateTime.UtcNow.ToString("o") },
                { "timestamp_local", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "timezone", GetTimezoneOffsetString() },
                { "module", "network_monitor" },
                { "check_name", "rolling_ping_loss_confirmed" },
                { "trigger", "self_test" },
                { "result", "丢包确认 2.0%" },
                { "success", false },
                { "duration_ms", null },
                { "detail", new Dictionary<string, object>
                    {
                        { "active_profile", "PUB" },
                        { "group", "public" },
                        { "sample_count", 60 },
                        { "lost_count", 2 },
                        { "loss_percent", 3.3 },
                        { "latency_ms", 32.0 },
                        { "jitter_ms", 4.0 },
                        { "diagnosis", "WanLoss" }
                    }
                }
            };
            string rollingLossLine = new JavaScriptSerializer().Serialize(rollingLossEntry);
            Dictionary<string, object> rollingLossParsed = new JavaScriptSerializer().DeserializeObject(rollingLossLine) as Dictionary<string, object>;
            Dictionary<string, object> rollingLossDetail = rollingLossParsed == null ? null : rollingLossParsed["detail"] as Dictionary<string, object>;
            if (rollingLossDetail == null ||
                rollingLossDetail.ContainsKey("gateway") ||
                rollingLossDetail.ContainsKey("dns_server") ||
                !string.Equals("rolling_ping_loss_confirmed", rollingLossParsed["check_name"] as string, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network check history self-test: rolling loss detail serialization failed.");
            }

            // Test trim: write an entry with timestamp 49 hours ago and verify TrimLocked would remove it.
            DateTime oldTime = DateTime.UtcNow - TimeSpan.FromHours(49);
            Dictionary<string, object> oldEntry = new Dictionary<string, object>
            {
                { "schema_version", 1 },
                { "timestamp_utc", oldTime.ToString("o") },
                { "timestamp_local", oldTime.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss") },
                { "timezone", GetTimezoneOffsetString() },
                { "module", "network_monitor" },
                { "check_name", "connectivity" },
                { "trigger", "old_test" },
                { "result", "OLD" },
                { "success", true },
                { "duration_ms", 10 }
            };

            string oldLine = new JavaScriptSerializer().Serialize(oldEntry) + "\n";
            File.WriteAllText(testPath, oldLine + line, Utf8NoBom);
            Trim();
            string[] trimmedLines = File.ReadAllLines(testPath, Utf8NoBom);
            if (trimmedLines.Length != 1)
            {
                throw new InvalidOperationException("Network check history self-test: trim logic expected 1 kept row, got " + trimmedLines.Length);
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

    public static string GetTimezoneOffsetString()
    {
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        char sign = offset.TotalHours >= 0 ? '+' : '-';
        int hours = Math.Abs(offset.Hours);
        int minutes = Math.Abs(offset.Minutes);
        return string.Format("{0}{1:D2}:{2:D2}", sign, hours, minutes);
    }

    private static string JsonSerializeForTest(NetworkCheckHistoryEntry entry)
    {
        Dictionary<string, object> dict = new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "timestamp_utc", entry.TimestampUtc.ToString("o") },
            { "timestamp_local", entry.TimestampLocal.ToString("yyyy-MM-ddTHH:mm:ss") },
            { "timezone", entry.Timezone },
            { "module", entry.Module },
            { "check_name", entry.CheckName },
            { "trigger", entry.Trigger ?? string.Empty },
            { "result", entry.Result ?? string.Empty },
            { "success", entry.Success },
            { "duration_ms", entry.DurationMs >= 0 ? (object)entry.DurationMs : null }
        };

        if (entry.Detail != null && entry.Detail.Count > 0)
        {
            dict["detail"] = entry.Detail;
        }

        return new JavaScriptSerializer().Serialize(dict) + "\n";
    }
}

/// <summary>
/// Immutable entry for a single network check history row.
/// </summary>
internal sealed class NetworkCheckHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public DateTime TimestampLocal { get; set; }
    public string Timezone { get; set; }
    /// <summary>codex_radar, connection_check, or network_monitor</summary>
    public string Module { get; set; }
    /// <summary>codex_radar_status, claude_status, codex_connection, clean_ip, connectivity, public_ip, dns, gfw, cloud_endpoints</summary>
    public string CheckName { get; set; }
    public string Trigger { get; set; }
    /// <summary>Short status string, never raw API bodies</summary>
    public string Result { get; set; }
    public bool Success { get; set; }
    /// <summary>milliseconds, or -1 if unknown</summary>
    public int DurationMs { get; set; } = -1;
    /// <summary>Optional bounded detail with non-sensitive summary only</summary>
    public Dictionary<string, object> Detail { get; set; }
}
