// Lifecycle and incremental-tail behavior in this file is ported from the MIT-licensed
// LH-03/codex-monitor-hud v2.0.2-preview project. The port intentionally keeps only the
// privacy-safe backend data plane: no prompts, responses, UI, network, or rate-limit data.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

public enum CodexTaskStatus
{
    Active,
    Listening,
    Idle,
    Paused,
    Error,
    Completed,
    Aborted
}

public enum CodexTaskAttentionReason
{
    Completed,
    Aborted,
    Error
}

public sealed class CodexTaskTokenUsage
{
    public static readonly CodexTaskTokenUsage Empty = new CodexTaskTokenUsage(0, 0, 0, 0, 0);

    public CodexTaskTokenUsage(long inputTokens, long cachedInputTokens, long outputTokens, long reasoningOutputTokens, long totalTokens)
    {
        this.InputTokens = Math.Max(0L, inputTokens);
        this.CachedInputTokens = Math.Max(0L, cachedInputTokens);
        this.OutputTokens = Math.Max(0L, outputTokens);
        this.ReasoningOutputTokens = Math.Max(0L, reasoningOutputTokens);
        this.TotalTokens = Math.Max(0L, totalTokens);
    }

    public long InputTokens { get; private set; }
    public long CachedInputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long ReasoningOutputTokens { get; private set; }
    public long TotalTokens { get; private set; }

    internal CodexTaskTokenUsage Clone()
    {
        return new CodexTaskTokenUsage(
            this.InputTokens,
            this.CachedInputTokens,
            this.OutputTokens,
            this.ReasoningOutputTokens,
            this.TotalTokens);
    }

    internal string GetSignature()
    {
        return string.Join(",", new string[]
        {
            this.InputTokens.ToString(CultureInfo.InvariantCulture),
            this.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
            this.OutputTokens.ToString(CultureInfo.InvariantCulture),
            this.ReasoningOutputTokens.ToString(CultureInfo.InvariantCulture),
            this.TotalTokens.ToString(CultureInfo.InvariantCulture)
        });
    }
}

public sealed class CodexTaskSnapshot
{
    public CodexTaskSnapshot(
        string fileKey,
        int taskNumber,
        string workspaceLeaf,
        string model,
        CodexTaskStatus status,
        DateTime startedAtLocal,
        DateTime lastEventLocal,
        CodexTaskStatus? terminalStatus,
        DateTime? terminalAtLocal,
        bool terminalSilent,
        CodexTaskTokenUsage lastTokenUsage,
        CodexTaskTokenUsage totalTokenUsage,
        double contextPercent,
        string title)
    {
        this.FileKey = fileKey ?? string.Empty;
        this.TaskNumber = taskNumber;
        this.WorkspaceLeaf = workspaceLeaf ?? string.Empty;
        this.Model = model ?? string.Empty;
        this.Status = status;
        this.StartedAtLocal = startedAtLocal;
        this.LastEventLocal = lastEventLocal;
        this.TerminalStatus = terminalStatus;
        this.TerminalAtLocal = terminalAtLocal;
        this.TerminalSilent = terminalSilent;
        this.LastTokenUsage = (lastTokenUsage ?? CodexTaskTokenUsage.Empty).Clone();
        this.TotalTokenUsage = (totalTokenUsage ?? CodexTaskTokenUsage.Empty).Clone();
        this.ContextPercent = Math.Max(0.0, Math.Min(100.0, contextPercent));
        this.Title = title ?? string.Empty;
    }

    public string FileKey { get; private set; }
    public int TaskNumber { get; private set; }
    public string WorkspaceLeaf { get; private set; }
    public string Model { get; private set; }
    public CodexTaskStatus Status { get; private set; }
    public DateTime StartedAtLocal { get; private set; }
    public DateTime LastEventLocal { get; private set; }
    public CodexTaskStatus? TerminalStatus { get; private set; }
    public DateTime? TerminalAtLocal { get; private set; }
    public bool TerminalSilent { get; private set; }
    public CodexTaskTokenUsage LastTokenUsage { get; private set; }
    public CodexTaskTokenUsage TotalTokenUsage { get; private set; }
    public double ContextPercent { get; private set; }
    public string Title { get; private set; }

    internal CodexTaskSnapshot Clone()
    {
        return new CodexTaskSnapshot(
            this.FileKey,
            this.TaskNumber,
            this.WorkspaceLeaf,
            this.Model,
            this.Status,
            this.StartedAtLocal,
            this.LastEventLocal,
            this.TerminalStatus,
            this.TerminalAtLocal,
            this.TerminalSilent,
            this.LastTokenUsage,
            this.TotalTokenUsage,
            this.ContextPercent,
            this.Title);
    }

    internal string GetSignature()
    {
        return string.Join("|", new string[]
        {
            this.FileKey,
            this.TaskNumber.ToString(CultureInfo.InvariantCulture),
            this.WorkspaceLeaf,
            this.Model,
            this.Status.ToString(),
            this.StartedAtLocal.Ticks.ToString(CultureInfo.InvariantCulture),
            this.LastEventLocal.Ticks.ToString(CultureInfo.InvariantCulture),
            this.TerminalStatus.HasValue ? this.TerminalStatus.Value.ToString() : string.Empty,
            this.TerminalAtLocal.HasValue ? this.TerminalAtLocal.Value.Ticks.ToString(CultureInfo.InvariantCulture) : string.Empty,
            this.TerminalSilent ? "1" : "0",
            this.LastTokenUsage.GetSignature(),
            this.TotalTokenUsage.GetSignature(),
            this.ContextPercent.ToString("0.###", CultureInfo.InvariantCulture),
            this.Title
        });
    }
}

public sealed class CodexTaskMonitorSnapshot
{
    private static readonly ReadOnlyCollection<CodexTaskSnapshot> EmptyTasks =
        new ReadOnlyCollection<CodexTaskSnapshot>(new List<CodexTaskSnapshot>());

    public static readonly CodexTaskMonitorSnapshot Empty =
        new CodexTaskMonitorSnapshot(EmptyTasks, 0, DateTime.MinValue);

    public CodexTaskMonitorSnapshot(IList<CodexTaskSnapshot> tasks, int activeCount, DateTime generatedAtLocal)
    {
        List<CodexTaskSnapshot> copies = new List<CodexTaskSnapshot>();
        if (tasks != null)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                if (tasks[i] != null)
                {
                    copies.Add(tasks[i].Clone());
                }
            }
        }

        this.Tasks = new ReadOnlyCollection<CodexTaskSnapshot>(copies);
        this.ActiveCount = Math.Max(0, activeCount);
        this.GeneratedAtLocal = generatedAtLocal;
    }

    public IList<CodexTaskSnapshot> Tasks { get; private set; }
    public int ActiveCount { get; private set; }
    public DateTime GeneratedAtLocal { get; private set; }

    internal CodexTaskMonitorSnapshot Clone()
    {
        return new CodexTaskMonitorSnapshot(this.Tasks, this.ActiveCount, this.GeneratedAtLocal);
    }
}

public sealed class CodexTaskAttentionEventArgs : EventArgs
{
    public CodexTaskAttentionEventArgs(int taskNumber, string workspaceLeaf, CodexTaskAttentionReason reason, DateTime occurredAtLocal)
    {
        this.TaskNumber = taskNumber;
        this.WorkspaceLeaf = workspaceLeaf ?? string.Empty;
        this.Reason = reason;
        this.OccurredAtLocal = occurredAtLocal;
    }

    public int TaskNumber { get; private set; }
    public string WorkspaceLeaf { get; private set; }
    public CodexTaskAttentionReason Reason { get; private set; }
    public DateTime OccurredAtLocal { get; private set; }
}

public sealed class CodexTaskMonitorReader : IDisposable
{
    private const int MaximumTrackedFiles = 64;
    private const int MaximumReleasedNumbers = 512;
    private const int DefaultInitialReadLimitBytes = 8 * 1024 * 1024;
    private const int SessionIndexTailReadLimitBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly UTF8Encoding SessionIndexUtf8 = new UTF8Encoding(false, false);
    private static readonly Regex TurnContextGate = new Regex(
        "(?<!\\\\)\\\"type\\\"\\s*:\\s*(?<!\\\\)\\\"turn_context\\\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AnyTypeGate = new Regex(
        "(?<!\\\\)\\\"type\\\"\\s*:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EventMessageGate = new Regex(
        "(?<!\\\\)\\\"type\\\"\\s*:\\s*(?<!\\\\)\\\"event_msg\\\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AllowedEventGate = new Regex(
        "(?<!\\\\)\\\"type\\\"\\s*:\\s*(?<!\\\\)\\\"(?:task_started|task_complete|turn_aborted|token_count)\\\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object stateLock = new object();
    private readonly object workLock = new object();
    private readonly Dictionary<string, FileState> states =
        new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatcherChangeTypes> pendingChanges =
        new Dictionary<string, WatcherChangeTypes>(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEvent idleEvent = new ManualResetEvent(true);
    private readonly TaskNumberPool numberPool = new TaskNumberPool();
    private readonly int initialReadLimitBytes;
    private List<string> pendingReconcileFiles;
    private bool pendingStatusRefresh;
    private bool disposed;
    private int workerQueued;
    private bool paused;
    private ReaderSettings settings;
    private CodexTaskMonitorSnapshot snapshot = CodexTaskMonitorSnapshot.Empty;
    private string snapshotSignature = string.Empty;
    private Dictionary<string, string> sessionTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private DateTime sessionIndexLastWriteUtc = DateTime.MinValue;
    private string sessionIndexPath;
    private int sessionIndexReadCount;

    internal CodexTaskMonitorReader(WidgetSettings settings)
        : this(settings, DefaultInitialReadLimitBytes)
    {
    }

    private CodexTaskMonitorReader(WidgetSettings settings, int initialReadLimitBytes)
    {
        this.initialReadLimitBytes = Math.Max(128, Math.Min(DefaultInitialReadLimitBytes, initialReadLimitBytes));
        this.settings = ReaderSettings.From(settings);
        this.sessionIndexPath = GetDefaultSessionIndexPath();
    }

    internal string SessionIndexPathForTest
    {
        set
        {
            lock (this.stateLock)
            {
                this.sessionIndexPath = value ?? string.Empty;
                this.sessionIndexLastWriteUtc = DateTime.MinValue;
                this.sessionTitles.Clear();
                this.sessionIndexReadCount = 0;
            }
        }
    }

    internal int SessionIndexReadCountForTest
    {
        get
        {
            lock (this.stateLock)
            {
                return this.sessionIndexReadCount;
            }
        }
    }

    public event EventHandler SnapshotChanged;
    public event EventHandler<CodexTaskAttentionEventArgs> AttentionRaised;

    public bool IsPaused
    {
        get
        {
            lock (this.stateLock)
            {
                return this.paused;
            }
        }
    }

    public CodexTaskMonitorSnapshot GetSnapshot()
    {
        lock (this.stateLock)
        {
            return this.snapshot.Clone();
        }
    }

    public void SetPaused(bool value)
    {
        lock (this.stateLock)
        {
            if (this.paused == value || this.disposed)
            {
                return;
            }

            this.paused = value;
        }

        RequestStatusRefresh();
    }

    internal void UpdateSettings(WidgetSettings widgetSettings)
    {
        ReaderSettings next = ReaderSettings.From(widgetSettings);
        lock (this.stateLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.settings = next;
        }

        RequestStatusRefresh();
    }

    internal void RequestReconcile(IEnumerable<string> rolloutFiles)
    {
        List<string> copy = new List<string>();
        if (rolloutFiles != null)
        {
            foreach (string path in rolloutFiles)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    copy.Add(path);
                }
            }
        }

        lock (this.workLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.pendingReconcileFiles = copy;
            this.pendingStatusRefresh = true;
            QueueWorkerLocked();
        }
    }

    internal void NotifyFileChanged(string fullPath, WatcherChangeTypes changeType)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !IsRolloutFile(fullPath))
        {
            return;
        }

        lock (this.workLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.pendingChanges[fullPath] = changeType;
            this.pendingStatusRefresh = true;
            QueueWorkerLocked();
        }
    }

    internal void RequestStatusRefresh()
    {
        lock (this.workLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.pendingStatusRefresh = true;
            QueueWorkerLocked();
        }
    }

    internal bool WaitForIdle(int timeoutMilliseconds)
    {
        return this.idleEvent.WaitOne(Math.Max(0, timeoutMilliseconds));
    }

    public void Dispose()
    {
        lock (this.workLock)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.pendingChanges.Clear();
            this.pendingReconcileFiles = null;
            this.pendingStatusRefresh = false;
            this.idleEvent.Set();
        }

        lock (this.stateLock)
        {
            this.states.Clear();
            this.sessionTitles.Clear();
            this.snapshot = CodexTaskMonitorSnapshot.Empty;
            this.snapshotSignature = string.Empty;
        }

        // A queued worker may still perform its final Set after disposal. Keeping this one
        // process-lifetime wait handle avoids an ObjectDisposedException on the ThreadPool.
    }

    internal static string SerializeSnapshot(CodexTaskMonitorSnapshot value)
    {
        CodexTaskMonitorSnapshot safe = value ?? CodexTaskMonitorSnapshot.Empty;
        List<object> tasks = new List<object>();
        for (int i = 0; i < safe.Tasks.Count; i++)
        {
            CodexTaskSnapshot task = safe.Tasks[i];
            tasks.Add(new Dictionary<string, object>
            {
                { "file_key", task.FileKey },
                { "task_number", task.TaskNumber },
                { "workspace_leaf", task.WorkspaceLeaf },
                { "model", task.Model },
                { "status", task.Status.ToString() },
                { "started_at_local", FormatLocal(task.StartedAtLocal) },
                { "last_event_local", FormatLocal(task.LastEventLocal) },
                { "terminal_status", task.TerminalStatus.HasValue ? task.TerminalStatus.Value.ToString() : null },
                { "terminal_at_local", task.TerminalAtLocal.HasValue ? FormatLocal(task.TerminalAtLocal.Value) : null },
                { "terminal_silent", task.TerminalSilent },
                { "last_token_usage", ToDictionary(task.LastTokenUsage) },
                { "total_token_usage", ToDictionary(task.TotalTokenUsage) },
                { "context_percent", task.ContextPercent },
                { "title", task.Title }
            });
        }

        Dictionary<string, object> root = new Dictionary<string, object>
        {
            { "generated_at_local", FormatLocal(safe.GeneratedAtLocal) },
            { "active_count", safe.ActiveCount },
            { "tasks", tasks }
        };
        return new JavaScriptSerializer().Serialize(root);
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-codex-task-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            const string sessionUuid = "019f7074-805e-7f10-8db0-36087a3a6796";
            Assert(
                string.Equals(
                    TryExtractSessionUuid("rollout-2026-07-18T00-00-00-" + sessionUuid + ".jsonl"),
                    sessionUuid,
                    StringComparison.Ordinal),
                "rollout session uuid extraction failed");
            Assert(TryExtractSessionUuid("rollout-short.jsonl").Length == 0, "short rollout name produced a uuid");
            Assert(
                TryExtractSessionUuid("rollout-2026-07-18T00-00-00-019f7074-805e-7f10-8db0-36087a3a67zz.jsonl").Length == 0,
                "non-hex rollout name produced a uuid");
            Assert(TryExtractSessionUuid("rollout-without-session-id.jsonl").Length == 0, "uuid-free rollout name produced a uuid");
            string privateBodyFixture = "{\"type\":\"response_item\",\"payload\":{\"text\":\"{\\\"type\\\":\\\"event_msg\\\",\\\"payload\\\":{\\\"type\\\":\\\"task_complete\\\"}}\"}}";
            Assert(!LooksLikeAllowedRecord(privateBodyFixture), "prompt/response body escaped lifecycle gate");
            string visible = Path.Combine(root, "rollout-visible.jsonl");
            string silent = Path.Combine(root, "rollout-silent.jsonl");
            string aborted = Path.Combine(root, "rollout-aborted.jsonl");
            string degraded = Path.Combine(root, "rollout-degraded.jsonl");
            string incremental = Path.Combine(root, "rollout-incremental.jsonl");

            File.WriteAllText(
                visible,
                JsonContext(@"C:\\work\\中文项目", "gpt-test") + "\n" +
                JsonEvent(now.AddSeconds(-4), "task_started", null) + "\n" +
                JsonUsage(now.AddSeconds(-3), 120, 80, 20, 5, 140, 1200, 800, 200, 50, 1400, 1000) + "\n" +
                JsonEvent(now.AddSeconds(-1), "task_complete", "done"),
                StrictUtf8);
            File.WriteAllText(
                silent,
                JsonEvent(now.AddSeconds(-2), "task_started", null) + "\n" +
                JsonEvent(now.AddSeconds(-1), "task_complete", string.Empty),
                StrictUtf8);
            File.WriteAllText(
                aborted,
                JsonEvent(now.AddSeconds(-2), "task_started", null) + "\n" +
                JsonEvent(now.AddSeconds(-1), "turn_aborted", null),
                StrictUtf8);
            File.WriteAllText(
                degraded,
                "{bad json}\n" + JsonEvent(now.AddSeconds(-1), "task_started", null),
                StrictUtf8);
            File.WriteAllText(incremental, JsonEvent(now.AddSeconds(-1), "task_started", null), StrictUtf8);

            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.CodexTaskMonitorActiveWindowMinutes = 30;
            settings.CodexTaskMonitorActiveSeconds = 12;
            settings.CodexTaskMonitorIdleSeconds = 90;
            settings.CodexTaskMonitorTerminalHoldSeconds = 120;
            settings.CodexTaskMonitorErrorHoldSeconds = 30;
            settings.CodexTaskMonitorNumberCooldownSeconds = 120;

            using (CodexTaskMonitorReader reader = new CodexTaskMonitorReader(settings))
            {
                reader.SessionIndexPathForTest = Path.Combine(root, "missing-main-session-index.jsonl");
                int completedAttention = 0;
                int abortedAttention = 0;
                int errorAttention = 0;
                reader.AttentionRaised += delegate(object sender, CodexTaskAttentionEventArgs e)
                {
                    if (e.Reason == CodexTaskAttentionReason.Completed) Interlocked.Increment(ref completedAttention);
                    if (e.Reason == CodexTaskAttentionReason.Aborted) Interlocked.Increment(ref abortedAttention);
                    if (e.Reason == CodexTaskAttentionReason.Error) Interlocked.Increment(ref errorAttention);
                };

                reader.RequestReconcile(new string[] { visible, silent, aborted, degraded, incremental });
                Assert(reader.WaitForIdle(5000), "initial reconciliation timed out");
                CodexTaskMonitorSnapshot first = reader.GetSnapshot();
                Assert(first.Tasks.Count == 5 && first.ActiveCount == 5, "active-file aggregation or fallback failed");
                CodexTaskSnapshot visibleTask = FindByWorkspace(first, "中文项目");
                Assert(visibleTask != null, "UTF-8 workspace leaf was not retained");
                Assert(visibleTask.Status == CodexTaskStatus.Completed && !visibleTask.TerminalSilent, "final no-newline completion failed");
                Assert(visibleTask.LastTokenUsage.InputTokens == 120 && visibleTask.TotalTokenUsage.TotalTokens == 1400, "token usage parse failed");
                Assert(completedAttention == 1 && abortedAttention == 1 && errorAttention == 1, "attention events were not emitted exactly once");

                CodexTaskSnapshot silentTask = FindByPathKey(first, BuildFileKey(silent));
                Assert(silentTask != null && silentTask.Status == CodexTaskStatus.Completed && silentTask.TerminalSilent, "silent completion state failed");

                reader.RequestReconcile(new string[] { visible, silent, aborted, degraded, incremental });
                Assert(reader.WaitForIdle(5000), "unchanged reconciliation timed out");
                Assert(completedAttention == 1 && abortedAttention == 1 && errorAttention == 1, "unchanged data repeated attention");

                string usage = JsonUsage(now, 33, 11, 7, 2, 40, 330, 110, 70, 20, 400, 200);
                int split = usage.Length / 2;
                File.AppendAllText(incremental, "\n" + usage.Substring(0, split), StrictUtf8);
                reader.NotifyFileChanged(incremental, WatcherChangeTypes.Changed);
                Assert(reader.WaitForIdle(5000), "partial incremental read timed out");
                CodexTaskSnapshot beforeContinuation = FindByPathKey(reader.GetSnapshot(), BuildFileKey(incremental));
                Assert(beforeContinuation.LastTokenUsage.TotalTokens == 0, "invalid final prefix was consumed early");

                File.AppendAllText(incremental, usage.Substring(split), StrictUtf8);
                reader.NotifyFileChanged(incremental, WatcherChangeTypes.Changed);
                Assert(reader.WaitForIdle(5000), "incremental continuation timed out");
                CodexTaskSnapshot continued = FindByPathKey(reader.GetSnapshot(), BuildFileKey(incremental));
                Assert(continued.LastTokenUsage.InputTokens == 33 && continued.LastTokenUsage.OutputTokens == 7, "incremental offset continuation failed");

                string heartbeat = JsonUsage(now.AddSeconds(1), 0, 0, 0, 0, 0, 9999, 9999, 9999, 9999, 9999, 200);
                File.AppendAllText(incremental, "\n" + heartbeat, StrictUtf8);
                reader.NotifyFileChanged(incremental, WatcherChangeTypes.Changed);
                Assert(reader.WaitForIdle(5000), "heartbeat read timed out");
                CodexTaskSnapshot afterHeartbeat = FindByPathKey(reader.GetSnapshot(), BuildFileKey(incremental));
                Assert(afterHeartbeat.LastTokenUsage.InputTokens == 33 && afterHeartbeat.TotalTokenUsage.TotalTokens == 400, "maintenance heartbeat changed token usage");

                reader.SetPaused(true);
                Assert(reader.WaitForIdle(5000), "pause refresh timed out");
                Assert(AllStatuses(reader.GetSnapshot(), CodexTaskStatus.Paused), "paused status failed");
                reader.SetPaused(false);
                Assert(reader.WaitForIdle(5000), "resume refresh timed out");
            }

            RunTruncatedTailSelfTest(root, settings, now);
            RunSelectionPolicySelfTest(root, settings, now);
            RunStatusOrderSelfTest(settings, now);
            RunNumberPoolSelfTest(now.UtcDateTime);
            RunSessionTitleSelfTest(root, settings, now);
            Console.WriteLine("Codex task monitor: PASS no-newline silent aborted heartbeat utf8 degraded seven-status cooldown offset truncated-tail cap-fallback-terminal attention-once session-title-uuid-index-mtime-tail-event");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void RunSessionTitleSelfTest(string root, WidgetSettings settings, DateTimeOffset now)
    {
        const string sessionUuid = "019f7074-805e-7f10-8db0-36087a3a6796";
        string rollout = Path.Combine(
            root,
            "rollout-2026-07-18T00-00-00-" + sessionUuid + ".jsonl");
        string index = Path.Combine(root, "session_index.jsonl");
        File.WriteAllText(
            rollout,
            JsonContext(@"C:\work\title-fixture", "gpt-title") + "\n" +
            JsonEvent(now.AddSeconds(-1), "task_started", null),
            StrictUtf8);
        File.WriteAllText(
            index,
            "{bad json}\n" +
            JsonSessionTitle(sessionUuid, "旧标题") + "\n" +
            JsonSessionTitle(sessionUuid, "官方中文标题"),
            StrictUtf8);
        DateTime firstWriteUtc = DateTime.UtcNow.AddSeconds(-5);
        File.SetLastWriteTimeUtc(index, firstWriteUtc);

        using (CodexTaskMonitorReader reader = new CodexTaskMonitorReader(settings))
        {
            reader.SessionIndexPathForTest = index;
            int snapshotChanged = 0;
            reader.SnapshotChanged += delegate { Interlocked.Increment(ref snapshotChanged); };
            reader.RequestReconcile(new string[] { rollout });
            Assert(reader.WaitForIdle(5000), "session title reconciliation timed out");
            CodexTaskSnapshot first = FindByPathKey(reader.GetSnapshot(), BuildFileKey(rollout));
            Assert(first != null && first.Title == "官方中文标题", "session title duplicate-last or UTF-8 mapping failed");
            Assert(reader.SessionIndexReadCountForTest == 1, "session index initial read count failed");

            reader.RequestStatusRefresh();
            Assert(reader.WaitForIdle(5000), "unchanged session index refresh timed out");
            Assert(reader.SessionIndexReadCountForTest == 1, "unchanged session index was reread");
            Assert(snapshotChanged == 1, "unchanged session title repeated SnapshotChanged");

            File.WriteAllText(index, JsonSessionTitle(sessionUuid, "更新后的官方标题"), StrictUtf8);
            File.SetLastWriteTimeUtc(index, firstWriteUtc.AddSeconds(2));
            reader.RequestStatusRefresh();
            Assert(reader.WaitForIdle(5000), "changed session index refresh timed out");
            CodexTaskSnapshot updated = FindByPathKey(reader.GetSnapshot(), BuildFileKey(rollout));
            Assert(updated != null && updated.Title == "更新后的官方标题", "changed session title was not published");
            Assert(reader.SessionIndexReadCountForTest == 2, "changed session index read count failed");
            Assert(snapshotChanged == 2, "title change did not trigger exactly one SnapshotChanged");

            File.Delete(index);
            reader.RequestStatusRefresh();
            Assert(reader.WaitForIdle(5000), "missing session index refresh timed out");
            CodexTaskSnapshot missing = FindByPathKey(reader.GetSnapshot(), BuildFileKey(rollout));
            Assert(missing != null && missing.Title.Length == 0, "missing session index did not clear the title");
        }

        string largeIndex = Path.Combine(root, "session_index-large.jsonl");
        File.WriteAllText(
            largeIndex,
            new string('x', SessionIndexTailReadLimitBytes + 256) + "\n" +
            JsonSessionTitle(sessionUuid, "尾部标题"),
            StrictUtf8);
        using (CodexTaskMonitorReader reader = new CodexTaskMonitorReader(settings))
        {
            reader.SessionIndexPathForTest = largeIndex;
            reader.RequestReconcile(new string[] { rollout });
            Assert(reader.WaitForIdle(5000), "large session index reconciliation timed out");
            CodexTaskSnapshot tail = FindByPathKey(reader.GetSnapshot(), BuildFileKey(rollout));
            Assert(tail != null && tail.Title == "尾部标题", "bounded session index tail read failed");
        }
    }

    private void QueueWorkerLocked()
    {
        this.idleEvent.Reset();
        if (Interlocked.CompareExchange(ref this.workerQueued, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(ProcessPendingWork);
        }
    }

    private void ProcessPendingWork(object ignored)
    {
        while (true)
        {
            List<string> reconcile;
            Dictionary<string, WatcherChangeTypes> changes;
            bool refresh;
            lock (this.workLock)
            {
                if (this.disposed)
                {
                    Interlocked.Exchange(ref this.workerQueued, 0);
                    this.idleEvent.Set();
                    return;
                }

                reconcile = this.pendingReconcileFiles;
                this.pendingReconcileFiles = null;
                changes = new Dictionary<string, WatcherChangeTypes>(this.pendingChanges, StringComparer.OrdinalIgnoreCase);
                this.pendingChanges.Clear();
                refresh = this.pendingStatusRefresh;
                this.pendingStatusRefresh = false;
                if (reconcile == null && changes.Count == 0 && !refresh)
                {
                    Interlocked.Exchange(ref this.workerQueued, 0);
                    this.idleEvent.Set();
                    return;
                }
            }

            try
            {
                ProcessBatch(reconcile, changes, DateTime.UtcNow);
            }
            catch
            {
                // Reader failures are isolated from the host process. Per-file parsing and I/O
                // errors are represented in snapshots; this outer guard covers unexpected races.
            }
        }
    }

    private void ProcessBatch(List<string> reconcile, Dictionary<string, WatcherChangeTypes> changes, DateTime nowUtc)
    {
        List<CodexTaskAttentionEventArgs> attention = new List<CodexTaskAttentionEventArgs>();
        bool snapshotChanged;
        EventHandler snapshotHandler = null;
        EventHandler<CodexTaskAttentionEventArgs> attentionHandler = null;
        lock (this.stateLock)
        {
            if (this.disposed)
            {
                return;
            }

            if (!this.settings.Enabled)
            {
                ReleaseAllStatesLocked(nowUtc);
            }
            else
            {
                if (reconcile != null)
                {
                    ReconcileLocked(reconcile, nowUtc, attention);
                }

                foreach (KeyValuePair<string, WatcherChangeTypes> pair in changes)
                {
                    ProcessChangedFileLocked(pair.Key, pair.Value, nowUtc, attention);
                }
            }

            RefreshSessionTitlesLocked();
            snapshotChanged = PublishSnapshotLocked(nowUtc);
            if (snapshotChanged)
            {
                snapshotHandler = this.SnapshotChanged;
            }

            if (attention.Count > 0)
            {
                attentionHandler = this.AttentionRaised;
            }
        }

        if (snapshotChanged && snapshotHandler != null)
        {
            try { snapshotHandler(this, EventArgs.Empty); } catch { }
        }

        if (attentionHandler != null)
        {
            for (int i = 0; i < attention.Count; i++)
            {
                try { attentionHandler(this, attention[i]); } catch { }
            }
        }
    }

    private void ReconcileLocked(List<string> rolloutFiles, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        List<FileCandidate> candidates = new List<FileCandidate>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rolloutFiles.Count; i++)
        {
            string path = rolloutFiles[i];
            if (!IsRolloutFile(path) || !seen.Add(path))
            {
                continue;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                candidates.Add(new FileCandidate(path, info.LastWriteTimeUtc, info.CreationTimeUtc));
            }
            catch
            {
                FileState existing;
                if (this.states.TryGetValue(path, out existing))
                {
                    MarkReadErrorLocked(existing, nowUtc, attention);
                }
            }
        }

        candidates.Sort(delegate(FileCandidate left, FileCandidate right)
        {
            return right.LastWriteUtc.CompareTo(left.LastWriteUtc);
        });

        DateTime cutoffUtc = nowUtc.AddMinutes(-this.settings.ActiveWindowMinutes);
        List<FileCandidate> selected = new List<FileCandidate>();
        HashSet<string> selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Terminal retention is a stronger visibility guarantee than admitting a newly found
        // active file. Reserve those existing slots first, then fill the 64-file guardrail.
        foreach (FileState state in this.states.Values)
        {
            if (selected.Count >= MaximumTrackedFiles) break;
            if (IsTerminalHeld(state, nowUtc, this.settings.TerminalHoldSeconds) && File.Exists(state.Path))
            {
                selected.Add(new FileCandidate(state.Path, state.LastWriteUtc, state.CreationUtc));
                selectedPaths.Add(state.Path);
            }
        }

        bool foundActiveCandidate = false;
        for (int i = 0; i < candidates.Count && selected.Count < MaximumTrackedFiles; i++)
        {
            if (candidates[i].LastWriteUtc >= cutoffUtc)
            {
                foundActiveCandidate = true;
                if (selectedPaths.Add(candidates[i].Path)) selected.Add(candidates[i]);
            }
        }

        if (!foundActiveCandidate && candidates.Count > 0 && selected.Count < MaximumTrackedFiles && selectedPaths.Add(candidates[0].Path))
        {
            selected.Add(candidates[0]);
        }

        List<string> removed = new List<string>();
        foreach (KeyValuePair<string, FileState> pair in this.states)
        {
            if (!selectedPaths.Contains(pair.Key))
            {
                removed.Add(pair.Key);
            }
        }

        for (int i = 0; i < removed.Count; i++)
        {
            RemoveStateLocked(removed[i], nowUtc);
        }

        for (int i = 0; i < selected.Count; i++)
        {
            FileState state = GetOrCreateStateLocked(selected[i], nowUtc);
            ReadFileLocked(state, nowUtc, attention);
        }
    }

    private void ProcessChangedFileLocked(string path, WatcherChangeTypes changeType, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        if (changeType == WatcherChangeTypes.Deleted || !File.Exists(path))
        {
            RemoveStateLocked(path, nowUtc);
            return;
        }

        try
        {
            FileInfo info = new FileInfo(path);
            FileState state;
            if (!this.states.TryGetValue(path, out state))
            {
                DateTime cutoffUtc = nowUtc.AddMinutes(-this.settings.ActiveWindowMinutes);
                if (info.LastWriteTimeUtc < cutoffUtc && this.states.Count > 0)
                {
                    return;
                }

                if (this.states.Count >= MaximumTrackedFiles)
                {
                    return;
                }

                state = GetOrCreateStateLocked(new FileCandidate(path, info.LastWriteTimeUtc, info.CreationTimeUtc), nowUtc);
            }

            ReadFileLocked(state, nowUtc, attention);
        }
        catch
        {
            FileState state;
            if (this.states.TryGetValue(path, out state))
            {
                MarkReadErrorLocked(state, nowUtc, attention);
            }
        }
    }

    private FileState GetOrCreateStateLocked(FileCandidate candidate, DateTime nowUtc)
    {
        FileState state;
        if (this.states.TryGetValue(candidate.Path, out state))
        {
            state.LastWriteUtc = candidate.LastWriteUtc;
            return state;
        }

        state = new FileState
        {
            Path = candidate.Path,
            FileKey = BuildFileKey(candidate.Path),
            SessionUuid = TryExtractSessionUuid(candidate.Path),
            TaskNumber = this.numberPool.Acquire(nowUtc),
            CreationUtc = candidate.CreationUtc,
            LastWriteUtc = candidate.LastWriteUtc,
            StartedAtLocal = candidate.CreationUtc.ToLocalTime(),
            LastTokenUsage = CodexTaskTokenUsage.Empty,
            TotalTokenUsage = CodexTaskTokenUsage.Empty,
            PendingBytes = new byte[0],
            LastReadErrorUtc = DateTime.MinValue,
            TerminalAtUtc = DateTime.MinValue
        };
        this.states.Add(candidate.Path, state);
        return state;
    }

    private void ReadFileLocked(FileState state, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        try
        {
            using (FileStream stream = new FileStream(
                state.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                state.LastWriteUtc = File.GetLastWriteTimeUtc(state.Path);
                if (stream.Length < state.Offset)
                {
                    ResetStateDataLocked(state);
                }

                bool initial = !state.Initialized;
                long start = initial ? Math.Max(0L, stream.Length - this.initialReadLimitBytes) : state.Offset;
                bool startsMidFile = initial && start > 0L;
                stream.Seek(start, SeekOrigin.Begin);
                int count = checked((int)Math.Min(int.MaxValue, stream.Length - start));
                byte[] bytes = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int current = stream.Read(bytes, read, count - read);
                    if (current <= 0) break;
                    read += current;
                }

                state.Offset = start + read;
                state.Initialized = true;
                if (read == 0)
                {
                    return;
                }

                if (read != bytes.Length)
                {
                    Array.Resize(ref bytes, read);
                }

                if (startsMidFile)
                {
                    int newline = IndexOf(bytes, (byte)'\n', 0);
                    if (newline < 0)
                    {
                        state.PendingBytes = new byte[0];
                        return;
                    }

                    bytes = Slice(bytes, newline + 1, bytes.Length - newline - 1);
                }

                ProcessBytesLocked(state, bytes, nowUtc, attention);
            }
        }
        catch
        {
            MarkReadErrorLocked(state, nowUtc, attention);
        }
    }

    private void ProcessBytesLocked(FileState state, byte[] bytes, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        byte[] combined = Combine(state.PendingBytes, bytes);
        state.PendingBytes = new byte[0];
        int lineStart = 0;
        while (lineStart < combined.Length)
        {
            int newline = IndexOf(combined, (byte)'\n', lineStart);
            if (newline < 0)
            {
                break;
            }

            int length = newline - lineStart;
            if (length > 0 && combined[newline - 1] == (byte)'\r')
            {
                length--;
            }

            if (length > 0)
            {
                byte[] line = Slice(combined, lineStart, length);
                if (!TryProcessCompleteLineLocked(state, line, nowUtc, attention))
                {
                    MarkReadErrorLocked(state, nowUtc, attention);
                }
            }

            lineStart = newline + 1;
        }

        if (lineStart >= combined.Length)
        {
            return;
        }

        byte[] final = Slice(combined, lineStart, combined.Length - lineStart);
        string finalText;
        if (!TryDecode(final, out finalText) || !IsStructurallyCompleteJsonObject(finalText))
        {
            state.PendingBytes = final;
            return;
        }

        if (!LooksLikeAllowedRecord(finalText))
        {
            return;
        }

        if (!TryProcessAllowedRecordLocked(state, finalText, nowUtc, attention))
        {
            // A balanced but invalid final JSON record may still be in flight. Keep its bytes
            // until a later append or newline proves that the complete line is malformed.
            state.PendingBytes = final;
        }
    }

    private bool TryProcessCompleteLineLocked(FileState state, byte[] line, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        string text;
        if (!TryDecode(line, out text) || !IsStructurallyCompleteJsonObject(text))
        {
            return false;
        }

        if (!LooksLikeAllowedRecord(text))
        {
            // Every rollout record has a typed envelope. Rejecting an untyped complete line
            // catches corruption without deserializing unrelated prompt or response payloads.
            return AnyTypeGate.IsMatch(text);
        }

        return TryProcessAllowedRecordLocked(state, text, nowUtc, attention);
    }

    private bool TryProcessAllowedRecordLocked(FileState state, string line, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Math.Max(1024, line.Length + 32);
            Dictionary<string, object> record = serializer.DeserializeObject(line) as Dictionary<string, object>;
            if (record == null)
            {
                return false;
            }

            string recordType = GetString(record, "type");
            Dictionary<string, object> payload = GetDictionary(record, "payload");
            if (string.Equals(recordType, "turn_context", StringComparison.Ordinal))
            {
                if (payload == null) return false;
                state.Model = GetString(payload, "model");
                state.WorkspaceLeaf = GetWorkspaceLeaf(GetString(payload, "cwd"));
                state.HasSnapshot = true;
                return true;
            }

            if (!string.Equals(recordType, "event_msg", StringComparison.Ordinal) || payload == null)
            {
                return false;
            }

            string payloadType = GetString(payload, "type");
            DateTime eventUtc;
            DateTime eventLocal;
            if (!TryGetEventTime(record, out eventUtc, out eventLocal))
            {
                return false;
            }

            if (string.Equals(payloadType, "task_started", StringComparison.Ordinal))
            {
                if (!state.HasStartedEvent)
                {
                    state.StartedAtLocal = eventLocal;
                    state.HasStartedEvent = true;
                }

                state.TerminalStatus = null;
                state.TerminalAtUtc = DateTime.MinValue;
                state.TerminalAtLocal = null;
                state.TerminalSilent = false;
                state.LastEventLocal = eventLocal;
                state.HasSnapshot = true;
                return true;
            }

            if (string.Equals(payloadType, "task_complete", StringComparison.Ordinal))
            {
                bool silent = string.IsNullOrWhiteSpace(GetString(payload, "last_agent_message"));
                state.TerminalStatus = CodexTaskStatus.Completed;
                state.TerminalAtUtc = eventUtc;
                state.TerminalAtLocal = eventLocal;
                state.TerminalSilent = silent;
                state.LastEventLocal = eventLocal;
                state.HasSnapshot = true;
                if (!silent)
                {
                    AddTerminalAttentionLocked(state, CodexTaskAttentionReason.Completed, eventLocal, attention);
                }
                return true;
            }

            if (string.Equals(payloadType, "turn_aborted", StringComparison.Ordinal))
            {
                state.TerminalStatus = CodexTaskStatus.Aborted;
                state.TerminalAtUtc = eventUtc;
                state.TerminalAtLocal = eventLocal;
                state.TerminalSilent = false;
                state.LastEventLocal = eventLocal;
                state.HasSnapshot = true;
                AddTerminalAttentionLocked(state, CodexTaskAttentionReason.Aborted, eventLocal, attention);
                return true;
            }

            if (!string.Equals(payloadType, "token_count", StringComparison.Ordinal))
            {
                return false;
            }

            Dictionary<string, object> info = GetDictionary(payload, "info");
            Dictionary<string, object> last = GetDictionary(info, "last_token_usage");
            Dictionary<string, object> total = GetDictionary(info, "total_token_usage");
            if (last == null || total == null)
            {
                return false;
            }

            long input = GetInt64(last, "input_tokens");
            long output = GetInt64(last, "output_tokens");
            if (input == 0L && output == 0L)
            {
                return true;
            }

            state.LastTokenUsage = ReadTokenUsage(last);
            state.TotalTokenUsage = ReadTokenUsage(total);
            long contextWindow = GetInt64(info, "model_context_window");
            state.ContextPercent = contextWindow > 0L
                ? Math.Min(100.0, Math.Round((input * 100.0) / contextWindow, 1))
                : 0.0;
            state.LastEventLocal = eventLocal;
            state.HasSnapshot = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddTerminalAttentionLocked(FileState state, CodexTaskAttentionReason reason, DateTime eventLocal, List<CodexTaskAttentionEventArgs> attention)
    {
        string key = reason.ToString() + "|" + eventLocal.Ticks.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(state.LastTerminalAttentionKey, key, StringComparison.Ordinal))
        {
            return;
        }

        state.LastTerminalAttentionKey = key;
        attention.Add(new CodexTaskAttentionEventArgs(state.TaskNumber, state.WorkspaceLeaf, reason, eventLocal));
    }

    private void MarkReadErrorLocked(FileState state, DateTime nowUtc, List<CodexTaskAttentionEventArgs> attention)
    {
        bool wasRecent = state.LastReadErrorUtc != DateTime.MinValue &&
            (nowUtc - state.LastReadErrorUtc).TotalSeconds <= this.settings.ErrorHoldSeconds;
        state.LastReadErrorUtc = nowUtc;
        if (!wasRecent)
        {
            attention.Add(new CodexTaskAttentionEventArgs(
                state.TaskNumber,
                state.WorkspaceLeaf,
                CodexTaskAttentionReason.Error,
                nowUtc.ToLocalTime()));
        }
    }

    private bool PublishSnapshotLocked(DateTime nowUtc)
    {
        List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>();
        foreach (FileState state in this.states.Values)
        {
            CodexTaskStatus status = EvaluateStatus(state, this.settings, this.paused, nowUtc);
            string title;
            if (string.IsNullOrEmpty(state.SessionUuid) ||
                !this.sessionTitles.TryGetValue(state.SessionUuid, out title))
            {
                title = string.Empty;
            }

            tasks.Add(new CodexTaskSnapshot(
                state.FileKey,
                state.TaskNumber,
                state.WorkspaceLeaf,
                state.Model,
                status,
                state.StartedAtLocal,
                state.LastEventLocal,
                state.TerminalStatus,
                state.TerminalAtLocal,
                state.TerminalSilent,
                state.LastTokenUsage,
                state.TotalTokenUsage,
                state.ContextPercent,
                title));
        }

        tasks.Sort(delegate(CodexTaskSnapshot left, CodexTaskSnapshot right)
        {
            return left.TaskNumber.CompareTo(right.TaskNumber);
        });
        StringBuilder signature = new StringBuilder();
        for (int i = 0; i < tasks.Count; i++)
        {
            signature.Append(tasks[i].GetSignature()).Append('\n');
        }

        string nextSignature = signature.ToString();
        if (string.Equals(nextSignature, this.snapshotSignature, StringComparison.Ordinal))
        {
            return false;
        }

        this.snapshotSignature = nextSignature;
        this.snapshot = new CodexTaskMonitorSnapshot(tasks, tasks.Count, nowUtc.ToLocalTime());
        return true;
    }

    private static CodexTaskStatus EvaluateStatus(FileState state, ReaderSettings settings, bool paused, DateTime nowUtc)
    {
        if (paused)
        {
            return CodexTaskStatus.Paused;
        }

        if (state.TerminalStatus.HasValue && IsTerminalHeld(state, nowUtc, settings.TerminalHoldSeconds))
        {
            return state.TerminalStatus.Value;
        }

        if (state.LastReadErrorUtc != DateTime.MinValue &&
            (nowUtc - state.LastReadErrorUtc).TotalSeconds <= settings.ErrorHoldSeconds)
        {
            return CodexTaskStatus.Error;
        }

        if (!state.HasSnapshot)
        {
            return CodexTaskStatus.Idle;
        }

        double ageSeconds = Math.Max(0.0, (nowUtc - state.LastWriteUtc).TotalSeconds);
        if (ageSeconds <= settings.ActiveSeconds)
        {
            return CodexTaskStatus.Active;
        }

        if (ageSeconds <= settings.IdleSeconds)
        {
            return CodexTaskStatus.Listening;
        }

        return CodexTaskStatus.Idle;
    }

    private static bool IsTerminalHeld(FileState state, DateTime nowUtc, int holdSeconds)
    {
        return state.TerminalStatus.HasValue &&
            state.TerminalAtUtc != DateTime.MinValue &&
            (nowUtc - state.TerminalAtUtc).TotalSeconds <= holdSeconds;
    }

    private void RemoveStateLocked(string path, DateTime nowUtc)
    {
        FileState state;
        if (!this.states.TryGetValue(path, out state))
        {
            return;
        }

        this.states.Remove(path);
        this.numberPool.Release(state.TaskNumber, nowUtc, this.settings.NumberCooldownSeconds);
    }

    private void ReleaseAllStatesLocked(DateTime nowUtc)
    {
        foreach (FileState state in this.states.Values)
        {
            this.numberPool.Release(state.TaskNumber, nowUtc, this.settings.NumberCooldownSeconds);
        }

        this.states.Clear();
    }

    private static void ResetStateDataLocked(FileState state)
    {
        state.Offset = 0L;
        state.PendingBytes = new byte[0];
        state.Initialized = false;
        state.Model = string.Empty;
        state.WorkspaceLeaf = string.Empty;
        state.LastEventLocal = DateTime.MinValue;
        state.LastTokenUsage = CodexTaskTokenUsage.Empty;
        state.TotalTokenUsage = CodexTaskTokenUsage.Empty;
        state.ContextPercent = 0.0;
        state.TerminalStatus = null;
        state.TerminalAtUtc = DateTime.MinValue;
        state.TerminalAtLocal = null;
        state.TerminalSilent = false;
        state.HasSnapshot = false;
        state.HasStartedEvent = false;
        state.LastTerminalAttentionKey = string.Empty;
    }

    private static bool LooksLikeAllowedRecord(string text)
    {
        return TurnContextGate.IsMatch(text) || (EventMessageGate.IsMatch(text) && AllowedEventGate.IsMatch(text));
    }

    private static bool IsStructurallyCompleteJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool inString = false;
        bool escaped = false;
        Stack<char> containers = new Stack<char>();
        bool started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (!started)
            {
                if (char.IsWhiteSpace(value)) continue;
                if (value != '{') return false;
                started = true;
                containers.Push('{');
                continue;
            }

            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }

            if (value == '"') inString = true;
            else if (value == '{' || value == '[') containers.Push(value);
            else if (value == '}' || value == ']')
            {
                if (containers.Count == 0) return false;
                char expected = value == '}' ? '{' : '[';
                if (containers.Pop() != expected) return false;
                if (containers.Count == 0)
                {
                    for (int tail = i + 1; tail < text.Length; tail++)
                    {
                        if (!char.IsWhiteSpace(text[tail])) return false;
                    }
                    return !inString;
                }
            }
        }

        return false;
    }

    private static bool TryDecode(byte[] bytes, out string text)
    {
        text = string.Empty;
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetEventTime(Dictionary<string, object> record, out DateTime utc, out DateTime local)
    {
        utc = DateTime.MinValue;
        local = DateTime.MinValue;
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(
            GetString(record, "timestamp"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        local = parsed.LocalDateTime;
        return true;
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
    {
        if (source == null) return null;
        object value;
        return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
    }

    private static string GetString(Dictionary<string, object> source, string key)
    {
        if (source == null) return string.Empty;
        object value;
        return source.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
    }

    private static long GetInt64(Dictionary<string, object> source, string key)
    {
        if (source == null) return 0L;
        object value;
        if (!source.TryGetValue(key, out value) || value == null) return 0L;
        try { return Math.Max(0L, Convert.ToInt64(value, CultureInfo.InvariantCulture)); }
        catch { return 0L; }
    }

    private static CodexTaskTokenUsage ReadTokenUsage(Dictionary<string, object> source)
    {
        return new CodexTaskTokenUsage(
            GetInt64(source, "input_tokens"),
            GetInt64(source, "cached_input_tokens"),
            GetInt64(source, "output_tokens"),
            GetInt64(source, "reasoning_output_tokens"),
            GetInt64(source, "total_tokens"));
    }

    private static string GetWorkspaceLeaf(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return string.Empty;
        try
        {
            string trimmed = cwd.Trim().TrimEnd('\\', '/');
            if (trimmed.Length == 0) return string.Empty;
            string leaf = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? string.Empty : leaf;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string TryExtractSessionUuid(string path)
    {
        string name;
        try { name = Path.GetFileNameWithoutExtension(path); }
        catch { return string.Empty; }
        if (string.IsNullOrEmpty(name) || name.Length < 36)
        {
            return string.Empty;
        }

        string candidate = name.Substring(name.Length - 36, 36);
        Guid parsed;
        return Guid.TryParseExact(candidate, "D", out parsed)
            ? candidate
            : string.Empty;
    }

    private static string GetDefaultSessionIndexPath()
    {
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(profile)
                ? string.Empty
                : Path.Combine(Path.Combine(profile, ".codex"), "session_index.jsonl");
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RefreshSessionTitlesLocked()
    {
        string path = this.sessionIndexPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            this.sessionTitles.Clear();
            this.sessionIndexLastWriteUtc = DateTime.MinValue;
            return;
        }

        try
        {
            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(path);
            if (lastWriteUtc == this.sessionIndexLastWriteUtc)
            {
                return;
            }

            Dictionary<string, string> next = ReadSessionTitles(path);
            this.sessionTitles = next;
            this.sessionIndexLastWriteUtc = lastWriteUtc;
            this.sessionIndexReadCount++;
        }
        catch (FileNotFoundException)
        {
            this.sessionTitles.Clear();
            this.sessionIndexLastWriteUtc = DateTime.MinValue;
        }
        catch (DirectoryNotFoundException)
        {
            this.sessionTitles.Clear();
            this.sessionIndexLastWriteUtc = DateTime.MinValue;
        }
        catch
        {
            // The index is an unstable Codex-owned format. Transient sharing, decoding, or access
            // failures retain the last good title map so they cannot disturb the task state plane.
        }
    }

    private static Dictionary<string, string> ReadSessionTitles(string path)
    {
        Dictionary<string, string> titles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            bool startsMidFile = stream.Length > SessionIndexTailReadLimitBytes;
            if (startsMidFile)
            {
                stream.Seek(stream.Length - SessionIndexTailReadLimitBytes, SeekOrigin.Begin);
            }

            // A 1 MiB tail can begin in the middle of a UTF-8 code point. Lenient decoding is
            // limited to this unstable external index; the first partial line is discarded and
            // malformed complete lines are isolated by the JSON parser below.
            using (StreamReader reader = new StreamReader(stream, SessionIndexUtf8, true, 4096))
            {
                if (startsMidFile)
                {
                    // The bounded tail normally starts inside a JSONL record. Discard exactly one
                    // line so only complete records enter the map; later duplicate ids still win.
                    reader.ReadLine();
                }

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    TryAddSessionTitleLine(line, titles);
                }
            }
        }

        return titles;
    }

    private static void TryAddSessionTitleLine(string line, Dictionary<string, string> titles)
    {
        if (string.IsNullOrWhiteSpace(line) || titles == null)
        {
            return;
        }

        try
        {
            Dictionary<string, object> record =
                new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>;
            string id = GetString(record, "id").Trim();
            if (id.Length == 0)
            {
                return;
            }

            titles[id] = GetString(record, "thread_name");
        }
        catch
        {
            // A malformed line is isolated from all other mappings and from rollout parsing.
        }
    }

    private static bool IsRolloutFile(string path)
    {
        string name;
        try { name = Path.GetFileName(path); }
        catch { return false; }
        return !string.IsNullOrEmpty(name) &&
            name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFileKey(string path)
    {
        string normalized = (path ?? string.Empty).ToUpperInvariant();
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < normalized.Length; i++)
        {
            hash ^= normalized[i];
            hash *= 1099511628211UL;
        }

        return "rollout:" + hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static byte[] Combine(byte[] left, byte[] right)
    {
        int leftLength = left == null ? 0 : left.Length;
        int rightLength = right == null ? 0 : right.Length;
        byte[] result = new byte[leftLength + rightLength];
        if (leftLength > 0) Buffer.BlockCopy(left, 0, result, 0, leftLength);
        if (rightLength > 0) Buffer.BlockCopy(right, 0, result, leftLength, rightLength);
        return result;
    }

    private static byte[] Slice(byte[] source, int start, int count)
    {
        if (count <= 0) return new byte[0];
        byte[] result = new byte[count];
        Buffer.BlockCopy(source, start, result, 0, count);
        return result;
    }

    private static int IndexOf(byte[] source, byte value, int start)
    {
        for (int i = Math.Max(0, start); i < source.Length; i++)
        {
            if (source[i] == value) return i;
        }
        return -1;
    }

    private static Dictionary<string, object> ToDictionary(CodexTaskTokenUsage usage)
    {
        CodexTaskTokenUsage safe = usage ?? CodexTaskTokenUsage.Empty;
        return new Dictionary<string, object>
        {
            { "input_tokens", safe.InputTokens },
            { "cached_input_tokens", safe.CachedInputTokens },
            { "output_tokens", safe.OutputTokens },
            { "reasoning_output_tokens", safe.ReasoningOutputTokens },
            { "total_tokens", safe.TotalTokens }
        };
    }

    private static string FormatLocal(DateTime value)
    {
        return value == DateTime.MinValue ? null : value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);
    }

    private static CodexTaskSnapshot FindByWorkspace(CodexTaskMonitorSnapshot snapshot, string workspace)
    {
        for (int i = 0; i < snapshot.Tasks.Count; i++)
        {
            if (string.Equals(snapshot.Tasks[i].WorkspaceLeaf, workspace, StringComparison.Ordinal)) return snapshot.Tasks[i];
        }
        return null;
    }

    private static CodexTaskSnapshot FindByPathKey(CodexTaskMonitorSnapshot snapshot, string key)
    {
        for (int i = 0; i < snapshot.Tasks.Count; i++)
        {
            if (string.Equals(snapshot.Tasks[i].FileKey, key, StringComparison.Ordinal)) return snapshot.Tasks[i];
        }
        return null;
    }

    private static bool AllStatuses(CodexTaskMonitorSnapshot snapshot, CodexTaskStatus expected)
    {
        if (snapshot.Tasks.Count == 0) return false;
        for (int i = 0; i < snapshot.Tasks.Count; i++)
        {
            if (snapshot.Tasks[i].Status != expected) return false;
        }
        return true;
    }

    private static string JsonSessionTitle(string id, string title)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
        {
            { "id", id },
            { "thread_name", title },
            { "updated_at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
        });
    }

    private static string JsonContext(string cwd, string model)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
        {
            { "type", "turn_context" },
            { "payload", new Dictionary<string, object> { { "cwd", cwd }, { "model", model } } }
        });
    }

    private static string JsonEvent(DateTimeOffset timestamp, string type, string finalMessage)
    {
        Dictionary<string, object> payload = new Dictionary<string, object> { { "type", type } };
        if (string.Equals(type, "task_complete", StringComparison.Ordinal)) payload["last_agent_message"] = finalMessage;
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
        {
            { "timestamp", timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
            { "type", "event_msg" },
            { "payload", payload }
        });
    }

    private static string JsonUsage(
        DateTimeOffset timestamp,
        long input,
        long cached,
        long output,
        long reasoning,
        long total,
        long taskInput,
        long taskCached,
        long taskOutput,
        long taskReasoning,
        long taskTotal,
        long contextWindow)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
        {
            { "timestamp", timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
            { "type", "event_msg" },
            { "payload", new Dictionary<string, object>
                {
                    { "type", "token_count" },
                    { "info", new Dictionary<string, object>
                        {
                            { "last_token_usage", new Dictionary<string, object>
                                {
                                    { "input_tokens", input }, { "cached_input_tokens", cached },
                                    { "output_tokens", output }, { "reasoning_output_tokens", reasoning }, { "total_tokens", total }
                                }
                            },
                            { "total_token_usage", new Dictionary<string, object>
                                {
                                    { "input_tokens", taskInput }, { "cached_input_tokens", taskCached },
                                    { "output_tokens", taskOutput }, { "reasoning_output_tokens", taskReasoning }, { "total_tokens", taskTotal }
                                }
                            },
                            { "model_context_window", contextWindow }
                        }
                    }
                }
            }
        });
    }

    private static void RunTruncatedTailSelfTest(string root, WidgetSettings settings, DateTimeOffset now)
    {
        string path = Path.Combine(root, "rollout-truncated.jsonl");
        string context = JsonContext(@"C:\\tail\\尾部项目", "tail-model");
        File.WriteAllText(path, new string('x', 512) + "\n" + context, StrictUtf8);
        using (CodexTaskMonitorReader reader = new CodexTaskMonitorReader(settings, 256))
        {
            reader.RequestReconcile(new string[] { path });
            Assert(reader.WaitForIdle(5000), "truncated-tail reconciliation timed out");
            CodexTaskSnapshot task = FindByWorkspace(reader.GetSnapshot(), "尾部项目");
            Assert(task != null && string.Equals(task.Model, "tail-model", StringComparison.Ordinal), "truncated first line was not discarded");
        }
    }

    private static void RunStatusOrderSelfTest(WidgetSettings widgetSettings, DateTimeOffset now)
    {
        ReaderSettings settings = ReaderSettings.From(widgetSettings);
        DateTime nowUtc = now.UtcDateTime;
        FileState state = new FileState
        {
            HasSnapshot = true,
            LastWriteUtc = nowUtc,
            TerminalAtUtc = DateTime.MinValue,
            LastReadErrorUtc = DateTime.MinValue
        };
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Active, "active status failed");
        state.LastWriteUtc = nowUtc.AddSeconds(-(settings.ActiveSeconds + 1));
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Listening, "listening status failed");
        state.LastWriteUtc = nowUtc.AddSeconds(-(settings.IdleSeconds + 1));
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Idle, "idle status failed");
        Assert(EvaluateStatus(state, settings, true, nowUtc) == CodexTaskStatus.Paused, "paused precedence failed");
        state.LastReadErrorUtc = nowUtc;
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Error, "error status failed");
        state.TerminalStatus = CodexTaskStatus.Completed;
        state.TerminalAtUtc = nowUtc;
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Completed, "completed precedence failed");
        state.TerminalStatus = CodexTaskStatus.Aborted;
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Aborted, "aborted precedence failed");
        state.TerminalStatus = CodexTaskStatus.Completed;
        state.TerminalAtUtc = nowUtc.AddSeconds(-(settings.TerminalHoldSeconds + 1));
        state.LastReadErrorUtc = DateTime.MinValue;
        Assert(EvaluateStatus(state, settings, false, nowUtc) == CodexTaskStatus.Idle, "terminal hold fallback failed");
    }

    private static void RunSelectionPolicySelfTest(string root, WidgetSettings widgetSettings, DateTimeOffset now)
    {
        string selectionRoot = Path.Combine(root, "selection");
        Directory.CreateDirectory(selectionRoot);
        string terminal = Path.Combine(selectionRoot, "rollout-terminal.jsonl");
        File.WriteAllText(
            terminal,
            JsonEvent(now.AddSeconds(-2), "task_started", null) + "\n" +
            JsonEvent(now.AddSeconds(-1), "task_complete", "done"),
            StrictUtf8);

        using (CodexTaskMonitorReader reader = new CodexTaskMonitorReader(widgetSettings))
        {
            reader.RequestReconcile(new string[] { terminal });
            Assert(reader.WaitForIdle(5000), "terminal selection seed timed out");
            File.SetLastWriteTimeUtc(terminal, now.UtcDateTime.AddHours(-2));

            List<string> files = new List<string> { terminal };
            for (int i = 0; i < MaximumTrackedFiles; i++)
            {
                string path = Path.Combine(selectionRoot, "rollout-cap-" + i.ToString("00", CultureInfo.InvariantCulture) + ".jsonl");
                File.WriteAllText(path, JsonEvent(now, "task_started", null), StrictUtf8);
                files.Add(path);
            }

            reader.RequestReconcile(files);
            Assert(reader.WaitForIdle(10000), "64-file selection timed out");
            CodexTaskMonitorSnapshot capped = reader.GetSnapshot();
            Assert(capped.Tasks.Count == MaximumTrackedFiles, "64-file guardrail failed");
            Assert(FindByPathKey(capped, BuildFileKey(terminal)) != null, "terminal hold lost priority at 64-file cap");
        }

        string older = Path.Combine(selectionRoot, "rollout-fallback-older.jsonl");
        string newer = Path.Combine(selectionRoot, "rollout-fallback-newer.jsonl");
        File.WriteAllText(older, JsonContext(@"C:\\fallback\\older", "old"), StrictUtf8);
        File.WriteAllText(newer, JsonContext(@"C:\\fallback\\newer", "new"), StrictUtf8);
        File.SetLastWriteTimeUtc(older, now.UtcDateTime.AddHours(-3));
        File.SetLastWriteTimeUtc(newer, now.UtcDateTime.AddHours(-2));
        using (CodexTaskMonitorReader fallback = new CodexTaskMonitorReader(widgetSettings))
        {
            fallback.RequestReconcile(new string[] { older, newer });
            Assert(fallback.WaitForIdle(5000), "newest fallback timed out");
            CodexTaskMonitorSnapshot one = fallback.GetSnapshot();
            Assert(one.Tasks.Count == 1 && FindByPathKey(one, BuildFileKey(newer)) != null, "newest-file fallback failed");
        }
    }

    private static void RunNumberPoolSelfTest(DateTime nowUtc)
    {
        TaskNumberPool pool = new TaskNumberPool();
        int first = pool.Acquire(nowUtc);
        int second = pool.Acquire(nowUtc);
        pool.Release(first, nowUtc, 120);
        Assert(pool.Acquire(nowUtc.AddSeconds(119)) != first, "task number reused before cooldown");
        pool.Release(second, nowUtc, 0);
        Assert(pool.Acquire(nowUtc.AddSeconds(120)) == first, "FIFO task number cooldown reuse failed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Codex task monitor self-test failed: " + message);
    }

    private sealed class ReaderSettings
    {
        public bool Enabled;
        public int ActiveWindowMinutes;
        public int ActiveSeconds;
        public int IdleSeconds;
        public int TerminalHoldSeconds;
        public int ErrorHoldSeconds;
        public int NumberCooldownSeconds;

        public static ReaderSettings From(WidgetSettings source)
        {
            WidgetSettings settings = source == null ? WidgetSettings.CreateDefaults() : source.Clone();
            settings.Normalize();
            return new ReaderSettings
            {
                Enabled = settings.CodexTaskMonitorEnabled,
                ActiveWindowMinutes = settings.CodexTaskMonitorActiveWindowMinutes,
                ActiveSeconds = settings.CodexTaskMonitorActiveSeconds,
                IdleSeconds = settings.CodexTaskMonitorIdleSeconds,
                TerminalHoldSeconds = settings.CodexTaskMonitorTerminalHoldSeconds,
                ErrorHoldSeconds = settings.CodexTaskMonitorErrorHoldSeconds,
                NumberCooldownSeconds = settings.CodexTaskMonitorNumberCooldownSeconds
            };
        }
    }

    private sealed class FileState
    {
        public string Path;
        public string FileKey;
        public string SessionUuid;
        public int TaskNumber;
        public DateTime CreationUtc;
        public DateTime LastWriteUtc;
        public long Offset;
        public byte[] PendingBytes;
        public bool Initialized;
        public string WorkspaceLeaf = string.Empty;
        public string Model = string.Empty;
        public bool HasSnapshot;
        public bool HasStartedEvent;
        public DateTime StartedAtLocal;
        public DateTime LastEventLocal;
        public CodexTaskStatus? TerminalStatus;
        public DateTime TerminalAtUtc;
        public DateTime? TerminalAtLocal;
        public bool TerminalSilent;
        public DateTime LastReadErrorUtc;
        public CodexTaskTokenUsage LastTokenUsage;
        public CodexTaskTokenUsage TotalTokenUsage;
        public double ContextPercent;
        public string LastTerminalAttentionKey = string.Empty;
    }

    private sealed class FileCandidate
    {
        public FileCandidate(string path, DateTime lastWriteUtc, DateTime creationUtc)
        {
            this.Path = path;
            this.LastWriteUtc = lastWriteUtc;
            this.CreationUtc = creationUtc;
        }

        public string Path;
        public DateTime LastWriteUtc;
        public DateTime CreationUtc;
    }

    private sealed class TaskNumberPool
    {
        private readonly Queue<ReleasedNumber> released = new Queue<ReleasedNumber>();
        private readonly HashSet<int> releasedSet = new HashSet<int>();
        private readonly HashSet<int> inUse = new HashSet<int>();
        private int next = 1;

        public int Acquire(DateTime nowUtc)
        {
            while (this.released.Count > 0 && this.released.Peek().AvailableUtc <= nowUtc)
            {
                ReleasedNumber item = this.released.Dequeue();
                this.releasedSet.Remove(item.Number);
                if (this.inUse.Add(item.Number)) return item.Number;
            }

            while (this.inUse.Contains(this.next) || this.releasedSet.Contains(this.next)) this.next++;
            int value = this.next++;
            this.inUse.Add(value);
            return value;
        }

        public void Release(int number, DateTime nowUtc, int cooldownSeconds)
        {
            if (number <= 0 || !this.inUse.Remove(number) || this.releasedSet.Contains(number)) return;
            if (this.released.Count >= MaximumReleasedNumbers)
            {
                ReleasedNumber dropped = this.released.Dequeue();
                this.releasedSet.Remove(dropped.Number);
            }

            this.released.Enqueue(new ReleasedNumber(number, nowUtc.AddSeconds(Math.Max(0, cooldownSeconds))));
            this.releasedSet.Add(number);
        }
    }

    private sealed class ReleasedNumber
    {
        public ReleasedNumber(int number, DateTime availableUtc)
        {
            this.Number = number;
            this.AvailableUtc = availableUtc;
        }

        public int Number;
        public DateTime AvailableUtc;
    }
}
