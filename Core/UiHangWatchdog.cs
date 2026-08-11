using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class UiHangWatchdog
{
    private const int CheckIntervalMs = 2000;
    private const int HangThresholdMs = 10000;
    private const int RepeatReportIntervalMs = 30000;
    private const int SuspendGapMs = 5 * 60 * 1000;
    private const long MaxActiveLogBytes = 1024L * 1024L;
    private const int MaxArchives = 3;
    private static readonly object SyncRoot = new object();
    private static Thread workerThread;
    private static bool running;
    private static bool shutdownRequested;
    private static DateTime lastHeartbeatUtc = DateTime.MinValue;
    private static DateTime currentOperationStartedUtc = DateTime.MinValue;
    private static DateTime lastCompletedOperationUtc = DateTime.MinValue;
    private static DateTime lastHangReportUtc = DateTime.MinValue;
    private static string currentOperation = string.Empty;
    private static string lastCompletedOperation = string.Empty;
    private static int reportedCurrentHang;
    private static int uiManagedThreadId;
    private static long windowEventsReceived;
    private static long windowEventsCoalesced;
    private static long windowEventsDropped;
    private static long windowEventQueueOverflows;
    private static long windowEventsProcessed;
    private static long windowEventBatchesProcessed;
    private static long windowEventFullRefreshes;
    private static int pendingWindowEvents;

    public static string LogPath
    {
        get { return Path.Combine(Logger.DirectoryPath, "ui-hang-watchdog.jsonl"); }
    }

    // This watchdog writes outside the normal Logger path because AppHang cases
    // can leave the UI thread stuck while the process is still alive. It records
    // operation breadcrumbs, not a full stack dump; if all threads are suspended
    // or Windows terminates the process immediately, no in-process logger can run.
    public static void Start()
    {
        lock (SyncRoot)
        {
            if (running)
            {
                return;
            }

            shutdownRequested = false;
            running = true;
            DateTime nowUtc = DateTime.UtcNow;
            lastHeartbeatUtc = nowUtc;
            currentOperationStartedUtc = DateTime.MinValue;
            lastCompletedOperationUtc = DateTime.MinValue;
            lastHangReportUtc = DateTime.MinValue;
            currentOperation = "startup";
            lastCompletedOperation = string.Empty;
            reportedCurrentHang = 0;
            uiManagedThreadId = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Exchange(ref windowEventsReceived, 0L);
            Interlocked.Exchange(ref windowEventsCoalesced, 0L);
            Interlocked.Exchange(ref windowEventsDropped, 0L);
            Interlocked.Exchange(ref windowEventQueueOverflows, 0L);
            Interlocked.Exchange(ref windowEventsProcessed, 0L);
            Interlocked.Exchange(ref windowEventBatchesProcessed, 0L);
            Interlocked.Exchange(ref windowEventFullRefreshes, 0L);
            Interlocked.Exchange(ref pendingWindowEvents, 0);
            workerThread = new Thread(WatchLoop);
            workerThread.Name = "DesktopCodexAssistant UI hang watchdog";
            workerThread.IsBackground = true;
            workerThread.Start();
        }
    }

    public static void Shutdown()
    {
        Thread threadToJoin = null;
        lock (SyncRoot)
        {
            if (!running)
            {
                return;
            }

            shutdownRequested = true;
            running = false;
            threadToJoin = workerThread;
            workerThread = null;
        }

        if (threadToJoin != null && threadToJoin.IsAlive)
        {
            try
            {
                threadToJoin.Join(500);
            }
            catch
            {
            }
        }
    }

    public static void MarkUiHeartbeat(string operation)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            lastHeartbeatUtc = nowUtc;
            lastCompletedOperation = string.IsNullOrEmpty(operation) ? currentOperation : operation;
            lastCompletedOperationUtc = nowUtc;
            currentOperation = string.Empty;
            currentOperationStartedUtc = DateTime.MinValue;
        }
    }

    public static OperationScope BeginUiOperation(string operation)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            lastHeartbeatUtc = nowUtc;
            currentOperation = string.IsNullOrEmpty(operation) ? "unknown" : operation;
            currentOperationStartedUtc = nowUtc;
        }

        return new OperationScope(operation);
    }

    public static void MarkUiCheckpoint(string operation)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            lastHeartbeatUtc = nowUtc;
            currentOperation = string.IsNullOrEmpty(operation) ? "unknown" : operation;
            currentOperationStartedUtc = nowUtc;
        }
    }

    private static void EndUiOperation(string operation)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            lastHeartbeatUtc = nowUtc;
            lastCompletedOperation = string.IsNullOrEmpty(operation) ? currentOperation : operation;
            lastCompletedOperationUtc = nowUtc;
            currentOperation = string.Empty;
            currentOperationStartedUtc = DateTime.MinValue;
        }
    }

    public static void RecordWindowEventQueued(
        int pendingCount,
        bool coalesced,
        bool dropped,
        bool overflowed)
    {
        Interlocked.Increment(ref windowEventsReceived);
        if (coalesced)
        {
            Interlocked.Increment(ref windowEventsCoalesced);
        }

        if (dropped)
        {
            Interlocked.Increment(ref windowEventsDropped);
        }

        if (overflowed)
        {
            Interlocked.Increment(ref windowEventQueueOverflows);
        }

        // Ignored events do not report the accumulator's current size. Preserve the last known
        // pending count instead of replacing it with the caller's sentinel zero.
        if (!dropped)
        {
            Interlocked.Exchange(ref pendingWindowEvents, Math.Max(0, pendingCount));
        }
    }

    public static void RecordWindowEventBatchProcessed(
        int pendingCount,
        int processedCount,
        bool fullRefresh)
    {
        Interlocked.Increment(ref windowEventBatchesProcessed);
        if (processedCount > 0)
        {
            Interlocked.Add(ref windowEventsProcessed, processedCount);
        }

        if (fullRefresh)
        {
            Interlocked.Increment(ref windowEventFullRefreshes);
        }

        Interlocked.Exchange(ref pendingWindowEvents, Math.Max(0, pendingCount));
    }

    private static void WatchLoop()
    {
        while (true)
        {
            Thread.Sleep(CheckIntervalMs);
            HangSnapshot snapshot;
            bool shouldReport;
            bool shouldWriteRecovered;
            lock (SyncRoot)
            {
                if (shutdownRequested)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                double delayMs = (nowUtc - lastHeartbeatUtc).TotalMilliseconds;
                if (delayMs > SuspendGapMs)
                {
                    lastHeartbeatUtc = nowUtc;
                    reportedCurrentHang = 0;
                    continue;
                }

                shouldWriteRecovered = reportedCurrentHang != 0 && delayMs < HangThresholdMs;
                shouldReport =
                    delayMs >= HangThresholdMs &&
                    (reportedCurrentHang == 0 ||
                    (nowUtc - lastHangReportUtc).TotalMilliseconds >= RepeatReportIntervalMs);
                if (!shouldReport && !shouldWriteRecovered)
                {
                    continue;
                }

                snapshot = new HangSnapshot(
                    shouldReport ? "ui_thread_unresponsive" : "ui_thread_responsive_again",
                    nowUtc,
                    lastHeartbeatUtc,
                    (long)Math.Round(delayMs),
                    currentOperation,
                    currentOperationStartedUtc,
                    lastCompletedOperation,
                    lastCompletedOperationUtc,
                    reportedCurrentHang != 0,
                    uiManagedThreadId,
                    Interlocked.CompareExchange(ref pendingWindowEvents, 0, 0),
                    Interlocked.Read(ref windowEventsReceived),
                    Interlocked.Read(ref windowEventsCoalesced),
                    Interlocked.Read(ref windowEventsDropped),
                    Interlocked.Read(ref windowEventQueueOverflows),
                    Interlocked.Read(ref windowEventsProcessed),
                    Interlocked.Read(ref windowEventBatchesProcessed),
                    Interlocked.Read(ref windowEventFullRefreshes));
            }

            bool reportWritten = WriteEmergencyHangReport(snapshot, Logger.DirectoryPath);
            if (!reportWritten)
            {
                // Keep the state unchanged so the next watchdog pass retries the record instead
                // of silently losing either the first hang or its recovery boundary.
                continue;
            }

            lock (SyncRoot)
            {
                if (shouldReport)
                {
                    lastHangReportUtc = snapshot.TimestampUtc;
                    reportedCurrentHang = 1;
                }
                else
                {
                    reportedCurrentHang = 0;
                }
            }
        }
    }

    private static bool WriteEmergencyHangReport(HangSnapshot snapshot, string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            string path = Path.Combine(directoryPath, "ui-hang-watchdog.jsonl");
            RotateIfNeeded(path, 4096);
            File.AppendAllText(path, BuildJsonLine(snapshot), SharedEncoding.Utf8NoBom);
            EnforceArchiveLimit(directoryPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildJsonLine(HangSnapshot snapshot)
    {
        StringBuilder builder = new StringBuilder(512);
        builder.Append('{');
        AppendJsonNumber(builder, "schema_version", 1, false);
        AppendJsonString(builder, "timestamp_utc", snapshot.TimestampUtc.ToString("o", CultureInfo.InvariantCulture), true);
        AppendJsonString(builder, "timestamp_local", snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), true);
        AppendJsonString(builder, "timezone", TimeZoneInfo.Local.StandardName, true);
        AppendJsonString(builder, "level", snapshot.EventName == "ui_thread_unresponsive" ? "warning" : "info", true);
        AppendJsonString(builder, "event", snapshot.EventName, true);
        AppendJsonString(builder, "process_name", Process.GetCurrentProcess().ProcessName, true);
        AppendJsonNumber(builder, "process_id", Process.GetCurrentProcess().Id, true);
        AppendJsonString(builder, "version", ProductIdentity.Version, true);
        AppendJsonNumber(builder, "delay_ms", snapshot.DelayMs, true);
        AppendJsonString(builder, "last_heartbeat_utc", snapshot.LastHeartbeatUtc.ToString("o", CultureInfo.InvariantCulture), true);
        AppendJsonString(builder, "current_operation", snapshot.CurrentOperation, true);
        AppendJsonNullableDate(builder, "current_operation_started_utc", snapshot.CurrentOperationStartedUtc, true);
        AppendJsonString(builder, "last_completed_operation", snapshot.LastCompletedOperation, true);
        AppendJsonNullableDate(builder, "last_completed_operation_utc", snapshot.LastCompletedOperationUtc, true);
        AppendJsonBool(builder, "repeated", snapshot.Repeated, true);
        AppendJsonNumber(builder, "ui_managed_thread_id", snapshot.UiManagedThreadId, true);
        AppendJsonNumber(builder, "pending_window_events", snapshot.PendingWindowEvents, true);
        AppendJsonNumber(builder, "window_events_received", snapshot.WindowEventsReceived, true);
        AppendJsonNumber(builder, "window_events_coalesced", snapshot.WindowEventsCoalesced, true);
        AppendJsonNumber(builder, "window_events_dropped", snapshot.WindowEventsDropped, true);
        AppendJsonNumber(builder, "window_event_queue_overflows", snapshot.WindowEventQueueOverflows, true);
        AppendJsonNumber(builder, "window_events_processed", snapshot.WindowEventsProcessed, true);
        AppendJsonNumber(builder, "window_event_batches_processed", snapshot.WindowEventBatchesProcessed, true);
        AppendJsonNumber(builder, "window_event_full_refreshes", snapshot.WindowEventFullRefreshes, true);
        builder.Append('}');
        builder.Append(Environment.NewLine);
        return builder.ToString();
    }

    private static void AppendJsonString(StringBuilder builder, string name, string value, bool comma)
    {
        if (comma)
        {
            builder.Append(',');
        }

        builder.Append('"');
        builder.Append(EscapeJson(name));
        builder.Append("\":\"");
        builder.Append(EscapeJson(value ?? string.Empty));
        builder.Append('"');
    }

    private static void AppendJsonNumber(StringBuilder builder, string name, long value, bool comma)
    {
        if (comma)
        {
            builder.Append(',');
        }

        builder.Append('"');
        builder.Append(EscapeJson(name));
        builder.Append("\":");
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendJsonBool(StringBuilder builder, string name, bool value, bool comma)
    {
        if (comma)
        {
            builder.Append(',');
        }

        builder.Append('"');
        builder.Append(EscapeJson(name));
        builder.Append("\":");
        builder.Append(value ? "true" : "false");
    }

    private static void AppendJsonNullableDate(StringBuilder builder, string name, DateTime value, bool comma)
    {
        if (comma)
        {
            builder.Append(',');
        }

        builder.Append('"');
        builder.Append(EscapeJson(name));
        builder.Append("\":");
        if (value == DateTime.MinValue)
        {
            builder.Append("null");
        }
        else
        {
            builder.Append('"');
            builder.Append(EscapeJson(value.ToString("o", CultureInfo.InvariantCulture)));
            builder.Append('"');
        }
    }

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static void RotateIfNeeded(string path, long incomingBytes)
    {
        FileInfo file = new FileInfo(path);
        if (!file.Exists || file.Length + incomingBytes <= MaxActiveLogBytes)
        {
            return;
        }

        string archivePath = Path.Combine(
            Path.GetDirectoryName(path),
            "ui-hang-watchdog-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
        File.Move(path, archivePath);
    }

    private static void EnforceArchiveLimit(string directoryPath)
    {
        FileInfo[] archives = new DirectoryInfo(directoryPath).GetFiles("ui-hang-watchdog-*.jsonl");
        Array.Sort(archives, delegate(FileInfo left, FileInfo right)
        {
            return left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
        });
        for (int i = 0; i < archives.Length - MaxArchives; i++)
        {
            try
            {
                archives[i].Delete();
            }
            catch
            {
            }
        }
    }

    internal static void RunSelfTest()
    {
        RunRecoveryStateSelfTest();
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            ProductIdentity.MachineName + "-UiHangWatchdogTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            HangSnapshot snapshot = new HangSnapshot(
                "ui_thread_unresponsive",
                DateTime.UtcNow,
                DateTime.UtcNow.AddSeconds(-12),
                12000,
                "widget.interaction_tick:update_burn_in",
                DateTime.UtcNow.AddSeconds(-12),
                "widget.interaction_tick",
                DateTime.UtcNow.AddSeconds(-13),
                false,
                1,
                2,
                30,
                20,
                4,
                1,
                10,
                3,
                1);
            if (!WriteEmergencyHangReport(snapshot, testDirectory))
            {
                throw new InvalidOperationException("UI hang watchdog self-test could not write its JSONL row.");
            }
            string path = Path.Combine(testDirectory, "ui-hang-watchdog.jsonl");
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length != 1)
            {
                throw new InvalidOperationException("UI hang watchdog self-test did not write one JSONL row.");
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object parsed = serializer.DeserializeObject(lines[0]);
            if (parsed == null ||
                !lines[0].Contains("\"event\":\"ui_thread_unresponsive\"") ||
                !lines[0].Contains("\"window_events_coalesced\":20"))
            {
                throw new InvalidOperationException("UI hang watchdog JSONL self-test failed.");
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, true);
            }
        }
    }

    private static void RunRecoveryStateSelfTest()
    {
        DateTime savedHeartbeat;
        DateTime savedCurrentStarted;
        DateTime savedCompletedUtc;
        string savedCurrent;
        string savedCompleted;
        int savedReported;
        lock (SyncRoot)
        {
            if (running)
            {
                return;
            }

            savedHeartbeat = lastHeartbeatUtc;
            savedCurrentStarted = currentOperationStartedUtc;
            savedCompletedUtc = lastCompletedOperationUtc;
            savedCurrent = currentOperation;
            savedCompleted = lastCompletedOperation;
            savedReported = reportedCurrentHang;
            reportedCurrentHang = 1;
            currentOperation = "test:active";
            currentOperationStartedUtc = DateTime.UtcNow.AddSeconds(-12);
        }

        try
        {
            MarkUiHeartbeat("test:complete");
            lock (SyncRoot)
            {
                if (reportedCurrentHang != 1 ||
                    currentOperation.Length != 0 ||
                    !string.Equals(lastCompletedOperation, "test:complete", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("UI hang watchdog heartbeat cleared recovery state or mislabeled completion.");
                }
            }

            MarkUiCheckpoint("test:next");
            lock (SyncRoot)
            {
                if (reportedCurrentHang != 1 ||
                    !string.Equals(currentOperation, "test:next", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("UI hang watchdog checkpoint cleared recovery state.");
                }
            }
        }
        finally
        {
            lock (SyncRoot)
            {
                lastHeartbeatUtc = savedHeartbeat;
                currentOperationStartedUtc = savedCurrentStarted;
                lastCompletedOperationUtc = savedCompletedUtc;
                currentOperation = savedCurrent;
                lastCompletedOperation = savedCompleted;
                reportedCurrentHang = savedReported;
            }
        }
    }

    internal struct OperationScope : IDisposable
    {
        private readonly string operation;
        private readonly bool active;

        internal OperationScope(string operation)
        {
            this.operation = operation;
            this.active = true;
        }

        public void Dispose()
        {
            if (this.active)
            {
                EndUiOperation(this.operation);
            }
        }
    }

    private struct HangSnapshot
    {
        public readonly string EventName;
        public readonly DateTime TimestampUtc;
        public readonly DateTime LastHeartbeatUtc;
        public readonly long DelayMs;
        public readonly string CurrentOperation;
        public readonly DateTime CurrentOperationStartedUtc;
        public readonly string LastCompletedOperation;
        public readonly DateTime LastCompletedOperationUtc;
        public readonly bool Repeated;
        public readonly int UiManagedThreadId;
        public readonly int PendingWindowEvents;
        public readonly long WindowEventsReceived;
        public readonly long WindowEventsCoalesced;
        public readonly long WindowEventsDropped;
        public readonly long WindowEventQueueOverflows;
        public readonly long WindowEventsProcessed;
        public readonly long WindowEventBatchesProcessed;
        public readonly long WindowEventFullRefreshes;

        public HangSnapshot(
            string eventName,
            DateTime timestampUtc,
            DateTime lastHeartbeatUtc,
            long delayMs,
            string currentOperation,
            DateTime currentOperationStartedUtc,
            string lastCompletedOperation,
            DateTime lastCompletedOperationUtc,
            bool repeated,
            int uiManagedThreadId,
            int pendingWindowEvents,
            long windowEventsReceived,
            long windowEventsCoalesced,
            long windowEventsDropped,
            long windowEventQueueOverflows,
            long windowEventsProcessed,
            long windowEventBatchesProcessed,
            long windowEventFullRefreshes)
        {
            this.EventName = eventName;
            this.TimestampUtc = timestampUtc;
            this.LastHeartbeatUtc = lastHeartbeatUtc;
            this.DelayMs = delayMs;
            this.CurrentOperation = currentOperation ?? string.Empty;
            this.CurrentOperationStartedUtc = currentOperationStartedUtc;
            this.LastCompletedOperation = lastCompletedOperation ?? string.Empty;
            this.LastCompletedOperationUtc = lastCompletedOperationUtc;
            this.Repeated = repeated;
            this.UiManagedThreadId = uiManagedThreadId;
            this.PendingWindowEvents = pendingWindowEvents;
            this.WindowEventsReceived = windowEventsReceived;
            this.WindowEventsCoalesced = windowEventsCoalesced;
            this.WindowEventsDropped = windowEventsDropped;
            this.WindowEventQueueOverflows = windowEventQueueOverflows;
            this.WindowEventsProcessed = windowEventsProcessed;
            this.WindowEventBatchesProcessed = windowEventBatchesProcessed;
            this.WindowEventFullRefreshes = windowEventFullRefreshes;
        }
    }
}
