using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

// Presentation layer over the Codex task monitor backend (CodexTaskMonitorReader). Pure mapping from
// an immutable backend snapshot to colors, ring geometry, badge and row models: no file access, no
// timers, no UI types beyond Color. Drawing code owns pixels; this file owns the meaning.
//
// BACKEND SEAM: SnapshotProvider is the single point where the reader is attached. CodexRadarForm
// owns the reader instance and registers it; every consumer here reads through the provider, so the
// render harness and self-tests can substitute fixtures without constructing a reader.

internal sealed class CodexTaskRingSegment
{
    public int TaskNumber;
    public float StartAngle;
    public float SweepDegrees;
    public Color Color;
}

internal sealed class CodexTaskRingModel
{
    public IList<CodexTaskRingSegment> Segments = new List<CodexTaskRingSegment>();
    public bool AnyAttention;

    public bool HasSegments
    {
        get { return this.Segments != null && this.Segments.Count > 0; }
    }
}

internal sealed class CodexTaskBadgeModel
{
    public int TaskCount;
    public int AttentionCount;
    public CodexTaskStatus MostUrgentStatus;
    public Color StatusColor;

    public bool HasTasks
    {
        get { return this.TaskCount > 0; }
    }
}

internal sealed class CodexTaskRowModel
{
    public string FileKey;
    public int TaskNumber;
    public string WorkspaceLeaf;
    public string Model;
    public string StatusText;
    public string DetailText;
    public string SubText;
    public string Title;
    public string LastTurnTokensText;
    public string TotalTokensText;
    public string InputTokensText;
    public string CachedPercentText;
    public string OutputTokensText;
    public string ReasoningTokensText;
    public double ContextPercent;
    public Color ContextBarColor;
    public bool ContextCritical;
    public Color StatusColor;
    public bool NeedsAttention;
}

// One observed run of a single status for one task, used by the timeline view.
internal sealed class CodexTaskTimelineSegment
{
    public DateTime StartLocal;
    public DateTime EndLocal;
    public CodexTaskStatus Status;
}

internal sealed class CodexTaskTimelineLane
{
    public int TaskNumber;
    public string WorkspaceLeaf;
    public string StatusText;
    public Color StatusColor;
    public bool NeedsAttention;
    public IList<CodexTaskTimelineSegment> Segments = new List<CodexTaskTimelineSegment>();
}

internal sealed class CodexTaskTimelineModel
{
    public DateTime StartLocal;
    public DateTime EndLocal;
    public IList<CodexTaskTimelineLane> Lanes = new List<CodexTaskTimelineLane>();

    public bool HasLanes
    {
        get { return this.Lanes != null && this.Lanes.Count > 0; }
    }
}

internal static class CodexTaskPresentation
{
    internal const float RingStartAngle = -90.0f;
    private const float RingSegmentGapDegrees = 6.0f;
    private const int MaximumRingSegments = 12;
    // Water-level semantics folded into the table: past this share of the model context window the
    // session is close enough to full that the user should think about starting a new one.
    internal const double ContextCriticalPercent = 80.0;
    internal const double ContextWarningPercent = 60.0;
    private const int MaximumTimelineSegmentsPerTask = 96;

    private static Func<CodexTaskMonitorSnapshot> snapshotProvider;
    private static readonly object timelineLock = new object();
    private static readonly Dictionary<string, List<CodexTaskTimelineSegment>> timelineByFileKey =
        new Dictionary<string, List<CodexTaskTimelineSegment>>(StringComparer.Ordinal);

    // Registered by the window that owns the reader. Unset in the render harness and self-tests,
    // where an empty snapshot must degrade to "no ring, no badge" rather than throw.
    internal static Func<CodexTaskMonitorSnapshot> SnapshotProvider
    {
        get { return snapshotProvider; }
        set { snapshotProvider = value; }
    }

    internal static CodexTaskMonitorSnapshot GetSnapshot()
    {
        Func<CodexTaskMonitorSnapshot> provider = snapshotProvider;
        if (provider == null)
        {
            return CodexTaskMonitorSnapshot.Empty;
        }

        try
        {
            CodexTaskMonitorSnapshot snapshot = provider();
            return snapshot ?? CodexTaskMonitorSnapshot.Empty;
        }
        catch (Exception ex)
        {
            // A presentation read must never take down a paint pass.
            Logger.Error(ex);
            return CodexTaskMonitorSnapshot.Empty;
        }
    }

    // Higher wins when one color or one row has to represent the whole set.
    internal static int GetUrgencyRank(CodexTaskStatus status)
    {
        switch (status)
        {
            case CodexTaskStatus.Error:
                return 6;
            case CodexTaskStatus.Aborted:
                return 5;
            case CodexTaskStatus.Listening:
                return 4;
            case CodexTaskStatus.Completed:
                return 3;
            case CodexTaskStatus.Active:
                return 2;
            case CodexTaskStatus.Idle:
                return 1;
            case CodexTaskStatus.Paused:
            default:
                return 0;
        }
    }

    // Attention = the user plausibly has to do something. Active work does not qualify: the whole
    // point of the monitor is to stay quiet while Codex is producing.
    internal static bool NeedsAttention(CodexTaskStatus status)
    {
        return status == CodexTaskStatus.Error ||
            status == CodexTaskStatus.Aborted ||
            status == CodexTaskStatus.Listening ||
            status == CodexTaskStatus.Completed;
    }

    internal static Color GetStatusColor(CodexTaskStatus status)
    {
        switch (status)
        {
            case CodexTaskStatus.Active:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
            case CodexTaskStatus.Listening:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            case CodexTaskStatus.Completed:
                return DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 235);
            case CodexTaskStatus.Aborted:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 200);
            case CodexTaskStatus.Error:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            case CodexTaskStatus.Idle:
                return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 150);
            case CodexTaskStatus.Paused:
            default:
                return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 90);
        }
    }

    // Context bar color is a water level, not a status: a full context window is a problem even on a
    // healthy idle session, so this ramp is deliberately independent of the status palette.
    internal static Color GetContextBarColor(double contextPercent)
    {
        if (contextPercent >= ContextCriticalPercent)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 235);
        }

        if (contextPercent >= ContextWarningPercent)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 235);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 215);
    }

    internal static string GetStatusText(CodexTaskStatus status)
    {
        switch (status)
        {
            case CodexTaskStatus.Active:
                return "运行中";
            case CodexTaskStatus.Listening:
                return "等待输入";
            case CodexTaskStatus.Completed:
                return "已完成";
            case CodexTaskStatus.Aborted:
                return "已中止";
            case CodexTaskStatus.Error:
                return "读取出错";
            case CodexTaskStatus.Idle:
                return "闲置";
            case CodexTaskStatus.Paused:
            default:
                return "已暂停";
        }
    }

    internal static string FormatAge(DateTime eventLocal, DateTime nowLocal)
    {
        if (eventLocal == DateTime.MinValue)
        {
            return "--";
        }

        double seconds = (nowLocal - eventLocal).TotalSeconds;
        if (seconds < 0.0)
        {
            seconds = 0.0;
        }

        if (seconds < 60.0)
        {
            return "刚刚";
        }

        if (seconds < 3600.0)
        {
            return ((int)(seconds / 60.0)).ToString(CultureInfo.InvariantCulture) + " 分";
        }

        if (seconds < 86400.0)
        {
            return ((int)(seconds / 3600.0)).ToString(CultureInfo.InvariantCulture) + " 时";
        }

        return ((int)(seconds / 86400.0)).ToString(CultureInfo.InvariantCulture) + " 天";
    }

    internal static string FormatTokens(long tokens)
    {
        if (tokens <= 0L)
        {
            return "0";
        }

        if (tokens >= 1000000L)
        {
            return ((double)tokens / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (tokens >= 1000L)
        {
            return ((double)tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    // Cached share of this turn's input, as a compact "缓X%" chip. Two absolute counts would double
    // the width of the token line; the ratio is what tells the user how much of the prompt was reused.
    private static string FormatCachedPercent(CodexTaskTokenUsage usage)
    {
        CodexTaskTokenUsage last = usage ?? CodexTaskTokenUsage.Empty;
        if (last.InputTokens <= 0L)
        {
            return string.Empty;
        }

        long percent = (long)Math.Round(100.0 * last.CachedInputTokens / last.InputTokens);
        percent = Math.Max(0L, Math.Min(100L, percent));
        return "缓" + percent.ToString(CultureInfo.InvariantCulture) + "%";
    }

    // Second card line: model, this turn's tokens, and how full the context window is. Every part
    // is optional so a session observed before its first turn_context still renders cleanly.
    private static string BuildSubText(CodexTaskSnapshot task)
    {
        List<string> parts = new List<string>();
        if (!string.IsNullOrEmpty(task.Model))
        {
            parts.Add(task.Model);
        }

        if (task.LastTokenUsage != null && task.LastTokenUsage.TotalTokens > 0L)
        {
            parts.Add(FormatTokens(task.LastTokenUsage.TotalTokens) + " tok");
        }

        if (task.ContextPercent > 0.0)
        {
            parts.Add("上下文 " + task.ContextPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%");
        }

        return string.Join(" · ", parts.ToArray());
    }

    // Scheme F: one segment per task around the clock's outer edge, ordered by the backend's stable
    // task number so a segment never jumps position while the set is unchanged. No tasks -> no ring.
    internal static CodexTaskRingModel BuildRing(CodexTaskMonitorSnapshot snapshot)
    {
        CodexTaskRingModel model = new CodexTaskRingModel();
        List<CodexTaskSnapshot> tasks = GetOrderedTasks(snapshot);
        if (tasks.Count == 0)
        {
            return model;
        }

        if (tasks.Count > MaximumRingSegments)
        {
            // Beyond the cap the segments are too thin to read; keep the most urgent ones.
            tasks.Sort(delegate(CodexTaskSnapshot left, CodexTaskSnapshot right)
            {
                int compare = GetUrgencyRank(right.Status).CompareTo(GetUrgencyRank(left.Status));
                return compare != 0 ? compare : left.TaskNumber.CompareTo(right.TaskNumber);
            });
            tasks = tasks.GetRange(0, MaximumRingSegments);
            tasks.Sort(delegate(CodexTaskSnapshot left, CodexTaskSnapshot right)
            {
                return left.TaskNumber.CompareTo(right.TaskNumber);
            });
        }

        float perSegment = 360.0f / tasks.Count;
        float gap = tasks.Count > 1 ? Math.Min(RingSegmentGapDegrees, perSegment * 0.25f) : 0.0f;
        for (int i = 0; i < tasks.Count; i++)
        {
            CodexTaskSnapshot task = tasks[i];
            model.Segments.Add(new CodexTaskRingSegment
            {
                TaskNumber = task.TaskNumber,
                StartAngle = RingStartAngle + i * perSegment + gap / 2.0f,
                SweepDegrees = perSegment - gap,
                Color = GetStatusColor(task.Status)
            });

            if (NeedsAttention(task.Status))
            {
                model.AnyAttention = true;
            }
        }

        return model;
    }

    // Scheme 4: the launcher node shows the task count and takes the most urgent status color.
    internal static CodexTaskBadgeModel BuildBadge(CodexTaskMonitorSnapshot snapshot)
    {
        CodexTaskBadgeModel model = new CodexTaskBadgeModel
        {
            MostUrgentStatus = CodexTaskStatus.Idle,
            StatusColor = GetStatusColor(CodexTaskStatus.Idle)
        };

        List<CodexTaskSnapshot> tasks = GetOrderedTasks(snapshot);
        if (tasks.Count == 0)
        {
            model.TaskCount = 0;
            return model;
        }

        model.TaskCount = tasks.Count;
        int bestRank = int.MinValue;
        for (int i = 0; i < tasks.Count; i++)
        {
            CodexTaskStatus status = tasks[i].Status;
            if (NeedsAttention(status))
            {
                model.AttentionCount++;
            }

            int rank = GetUrgencyRank(status);
            if (rank > bestRank)
            {
                bestRank = rank;
                model.MostUrgentStatus = status;
            }
        }

        model.StatusColor = GetStatusColor(model.MostUrgentStatus);
        return model;
    }

    // Flyout rows: urgent first, then by task number so equal-status rows keep a stable order.
    internal static IList<CodexTaskRowModel> BuildRows(CodexTaskMonitorSnapshot snapshot, DateTime nowLocal, int maximumRows)
    {
        List<CodexTaskRowModel> rows = new List<CodexTaskRowModel>();
        List<CodexTaskSnapshot> tasks = GetOrderedTasks(snapshot);
        tasks.Sort(delegate(CodexTaskSnapshot left, CodexTaskSnapshot right)
        {
            int compare = GetUrgencyRank(right.Status).CompareTo(GetUrgencyRank(left.Status));
            return compare != 0 ? compare : left.TaskNumber.CompareTo(right.TaskNumber);
        });

        int limit = maximumRows > 0 ? Math.Min(maximumRows, tasks.Count) : tasks.Count;
        for (int i = 0; i < limit; i++)
        {
            CodexTaskSnapshot task = tasks[i];
            rows.Add(new CodexTaskRowModel
            {
                FileKey = task.FileKey,
                TaskNumber = task.TaskNumber,
                WorkspaceLeaf = string.IsNullOrEmpty(task.WorkspaceLeaf) ? "--" : task.WorkspaceLeaf,
                Model = task.Model ?? string.Empty,
                StatusText = GetStatusText(task.Status),
                DetailText = FormatAge(task.LastEventLocal, nowLocal),
                SubText = BuildSubText(task),
                Title = task.Title ?? string.Empty,
                LastTurnTokensText = FormatTokens(task.LastTokenUsage == null ? 0L : task.LastTokenUsage.TotalTokens),
                TotalTokensText = FormatTokens(task.TotalTokenUsage == null ? 0L : task.TotalTokenUsage.TotalTokens),
                InputTokensText = FormatTokens((task.LastTokenUsage ?? CodexTaskTokenUsage.Empty).InputTokens),
                CachedPercentText = FormatCachedPercent(task.LastTokenUsage),
                OutputTokensText = FormatTokens((task.LastTokenUsage ?? CodexTaskTokenUsage.Empty).OutputTokens),
                ReasoningTokensText = FormatTokens((task.LastTokenUsage ?? CodexTaskTokenUsage.Empty).ReasoningOutputTokens),
                ContextPercent = task.ContextPercent,
                ContextBarColor = GetContextBarColor(task.ContextPercent),
                ContextCritical = task.ContextPercent >= ContextCriticalPercent,
                StatusColor = GetStatusColor(task.Status),
                NeedsAttention = NeedsAttention(task.Status)
            });
        }

        return rows;
    }

    // Timeline sampling. The backend publishes only "what is true now", so the timeline view is built
    // from observations the frontend accumulates itself: every sample either extends the task's
    // current segment or opens a new one. Storing transitions rather than raw samples keeps this a
    // few dozen structs per task regardless of tick rate, and needs no backend change.
    //
    // Sampling is driven by the task board's own tick, which runs whenever the board is docked (even
    // collapsed) or open. With the dock off and the board closed, no history accrues — the timeline
    // then only shows what was observed while it was last running.
    internal static void SampleTimeline(CodexTaskMonitorSnapshot snapshot, DateTime nowLocal, int windowMinutes)
    {
        if (snapshot == null)
        {
            return;
        }

        DateTime cutoff = nowLocal.AddMinutes(-Math.Max(1, windowMinutes));
        lock (timelineLock)
        {
            HashSet<string> live = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Tasks.Count; i++)
            {
                CodexTaskSnapshot task = snapshot.Tasks[i];
                if (task == null || string.IsNullOrEmpty(task.FileKey))
                {
                    continue;
                }

                live.Add(task.FileKey);
                List<CodexTaskTimelineSegment> segments;
                if (!timelineByFileKey.TryGetValue(task.FileKey, out segments))
                {
                    segments = new List<CodexTaskTimelineSegment>();
                    timelineByFileKey[task.FileKey] = segments;
                }

                CodexTaskTimelineSegment last = segments.Count > 0 ? segments[segments.Count - 1] : null;
                if (last != null && last.Status == task.Status)
                {
                    last.EndLocal = nowLocal;
                }
                else
                {
                    segments.Add(new CodexTaskTimelineSegment
                    {
                        StartLocal = last != null ? last.EndLocal : nowLocal,
                        EndLocal = nowLocal,
                        Status = task.Status
                    });
                }

                TrimSegments(segments, cutoff);
            }

            // A task that left the active window stops accruing history and is dropped once its last
            // observation ages out, so the map cannot grow without bound across a long session.
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, List<CodexTaskTimelineSegment>> pair in timelineByFileKey)
            {
                if (live.Contains(pair.Key))
                {
                    continue;
                }

                TrimSegments(pair.Value, cutoff);
                if (pair.Value.Count == 0)
                {
                    stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                timelineByFileKey.Remove(stale[i]);
            }
        }
    }

    private static void TrimSegments(List<CodexTaskTimelineSegment> segments, DateTime cutoff)
    {
        while (segments.Count > 0 && segments[0].EndLocal < cutoff)
        {
            segments.RemoveAt(0);
        }

        if (segments.Count > 0 && segments[0].StartLocal < cutoff)
        {
            segments[0].StartLocal = cutoff;
        }

        while (segments.Count > MaximumTimelineSegmentsPerTask)
        {
            segments.RemoveAt(0);
        }
    }

    internal static void ResetTimelineForTest()
    {
        lock (timelineLock)
        {
            timelineByFileKey.Clear();
        }
    }

    internal static CodexTaskTimelineModel BuildTimeline(
        CodexTaskMonitorSnapshot snapshot,
        DateTime nowLocal,
        int windowMinutes,
        int maximumLanes)
    {
        int minutes = Math.Max(1, windowMinutes);
        CodexTaskTimelineModel model = new CodexTaskTimelineModel
        {
            StartLocal = nowLocal.AddMinutes(-minutes),
            EndLocal = nowLocal
        };

        List<CodexTaskSnapshot> tasks = GetOrderedTasks(snapshot);
        tasks.Sort(delegate(CodexTaskSnapshot left, CodexTaskSnapshot right)
        {
            int compare = GetUrgencyRank(right.Status).CompareTo(GetUrgencyRank(left.Status));
            return compare != 0 ? compare : left.TaskNumber.CompareTo(right.TaskNumber);
        });

        int limit = maximumLanes > 0 ? Math.Min(maximumLanes, tasks.Count) : tasks.Count;
        lock (timelineLock)
        {
            for (int i = 0; i < limit; i++)
            {
                CodexTaskSnapshot task = tasks[i];
                CodexTaskTimelineLane lane = new CodexTaskTimelineLane
                {
                    TaskNumber = task.TaskNumber,
                    WorkspaceLeaf = string.IsNullOrEmpty(task.WorkspaceLeaf) ? "--" : task.WorkspaceLeaf,
                    StatusText = GetStatusText(task.Status),
                    StatusColor = GetStatusColor(task.Status),
                    NeedsAttention = NeedsAttention(task.Status)
                };

                List<CodexTaskTimelineSegment> segments;
                if (timelineByFileKey.TryGetValue(task.FileKey ?? string.Empty, out segments))
                {
                    for (int s = 0; s < segments.Count; s++)
                    {
                        DateTime start = segments[s].StartLocal < model.StartLocal ? model.StartLocal : segments[s].StartLocal;
                        DateTime end = segments[s].EndLocal > model.EndLocal ? model.EndLocal : segments[s].EndLocal;
                        if (end <= start)
                        {
                            continue;
                        }

                        lane.Segments.Add(new CodexTaskTimelineSegment
                        {
                            StartLocal = start,
                            EndLocal = end,
                            Status = segments[s].Status
                        });
                    }
                }

                model.Lanes.Add(lane);
            }
        }

        return model;
    }

    private static List<CodexTaskSnapshot> GetOrderedTasks(CodexTaskMonitorSnapshot snapshot)
    {
        List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>();
        if (snapshot == null || snapshot.Tasks == null)
        {
            return tasks;
        }

        for (int i = 0; i < snapshot.Tasks.Count; i++)
        {
            if (snapshot.Tasks[i] != null)
            {
                tasks.Add(snapshot.Tasks[i]);
            }
        }

        tasks.Sort(delegate(CodexTaskSnapshot left, CodexTaskSnapshot right)
        {
            return left.TaskNumber.CompareTo(right.TaskNumber);
        });
        return tasks;
    }

    // Test-only. CodexRadarForm's constructor installs the reader-backed provider, so a sample that
    // builds a form must re-publish its fixture afterwards to stay independent of live sessions.
    internal static void UseFixtureSnapshotForSample(DateTime nowLocal)
    {
        CodexTaskMonitorSnapshot fixture = CreateFixtureSnapshot(nowLocal);
        snapshotProvider = delegate { return fixture; };
    }

    internal static CodexTaskMonitorSnapshot CreateFixtureSnapshot(DateTime nowLocal)
    {
        List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>();
        tasks.Add(CreateFixtureTask(1, "desktopdata", CodexTaskStatus.Active, nowLocal.AddSeconds(-4.0), 94035, 36.0));
        tasks.Add(CreateFixtureTask(2, "ni", CodexTaskStatus.Listening, nowLocal.AddMinutes(-9.0), 137520, 53.2));
        tasks.Add(CreateFixtureTask(3, "ni", CodexTaskStatus.Idle, nowLocal.AddMinutes(-18.0), 207283, 80.2));
        tasks.Add(CreateFixtureTask(4, "codex-monitor", CodexTaskStatus.Completed, nowLocal.AddMinutes(-2.0), 187236, 72.4));
        return new CodexTaskMonitorSnapshot(tasks, 2, nowLocal);
    }

    private static CodexTaskSnapshot CreateFixtureTask(
        int number,
        string workspace,
        CodexTaskStatus status,
        DateTime lastEventLocal,
        long lastTotalTokens,
        double contextPercent)
    {
        return CreateFixtureTask(number, workspace, status, lastEventLocal, lastTotalTokens, contextPercent,
            "官方任务 " + number.ToString(CultureInfo.InvariantCulture));
    }

    private static CodexTaskSnapshot CreateFixtureTask(
        int number,
        string workspace,
        CodexTaskStatus status,
        DateTime lastEventLocal,
        long lastTotalTokens,
        double contextPercent,
        string title)
    {
        CodexTaskTokenUsage last = new CodexTaskTokenUsage(lastTotalTokens - 200L, lastTotalTokens - 1200L, 200L, 40L, lastTotalTokens);
        return new CodexTaskSnapshot(
            "rollout:fixture" + number.ToString(CultureInfo.InvariantCulture),
            number,
            workspace,
            "gpt-5.6-sol",
            status,
            lastEventLocal.AddHours(-1.0),
            lastEventLocal,
            status == CodexTaskStatus.Completed ? (CodexTaskStatus?)CodexTaskStatus.Completed : null,
            status == CodexTaskStatus.Completed ? (DateTime?)lastEventLocal : null,
            false,
            last,
            last,
            contextPercent,
            title);
    }

    // Six-session fixture for the bubble-card board sample: enough to fill the default 2x3 grid, with
    // an attention (Error) card, a critical water level and one deliberately untitled session so the
    // "—" placeholder is exercised. Kept separate from CreateFixtureSnapshot so the ordering asserted
    // by the presentation self-test stays stable.
    internal static CodexTaskMonitorSnapshot CreateBoardSampleSnapshot(DateTime nowLocal)
    {
        List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>();
        tasks.Add(CreateFixtureTask(1, "desktopdata", CodexTaskStatus.Active, nowLocal.AddSeconds(-4.0), 94035, 36.0));
        tasks.Add(CreateFixtureTask(2, "ni", CodexTaskStatus.Listening, nowLocal.AddMinutes(-9.0), 137520, 53.2));
        tasks.Add(CreateFixtureTask(3, "ni", CodexTaskStatus.Idle, nowLocal.AddMinutes(-18.0), 5480, 9.0, string.Empty));
        tasks.Add(CreateFixtureTask(4, "codex-monitor", CodexTaskStatus.Completed, nowLocal.AddMinutes(-2.0), 96140, 41.0));
        tasks.Add(CreateFixtureTask(5, "BunkyoUNV", CodexTaskStatus.Active, nowLocal.AddSeconds(-30.0), 42310, 18.0));
        tasks.Add(CreateFixtureTask(6, "Codexproj", CodexTaskStatus.Error, nowLocal.AddMinutes(-1.0), 310800, 83.0));
        return new CodexTaskMonitorSnapshot(tasks, 2, nowLocal);
    }

    internal static void RunSelfTest()
    {
        DateTime now = new DateTime(2026, 7, 16, 20, 52, 0);

        Func<CodexTaskMonitorSnapshot> savedProvider = snapshotProvider;
        try
        {
            snapshotProvider = null;
            if (GetSnapshot().Tasks.Count != 0)
            {
                throw new InvalidOperationException("Codex task presentation must degrade to an empty snapshot without a provider.");
            }

            snapshotProvider = delegate { throw new InvalidOperationException("provider failure"); };
            if (GetSnapshot().Tasks.Count != 0)
            {
                throw new InvalidOperationException("Codex task presentation must swallow provider failures during a paint pass.");
            }
        }
        finally
        {
            snapshotProvider = savedProvider;
        }

        if (BuildRing(CodexTaskMonitorSnapshot.Empty).HasSegments ||
            BuildBadge(CodexTaskMonitorSnapshot.Empty).HasTasks ||
            BuildRows(CodexTaskMonitorSnapshot.Empty, now, 8).Count != 0)
        {
            throw new InvalidOperationException("Codex task presentation must render nothing when no task is tracked.");
        }

        CodexTaskMonitorSnapshot fixture = CreateFixtureSnapshot(now);
        CodexTaskRingModel ring = BuildRing(fixture);
        if (ring.Segments.Count != 4 || !ring.AnyAttention)
        {
            throw new InvalidOperationException("Codex task ring should carry one segment per task and flag attention.");
        }

        float totalSweep = 0.0f;
        for (int i = 0; i < ring.Segments.Count; i++)
        {
            totalSweep += ring.Segments[i].SweepDegrees;
            if (ring.Segments[i].TaskNumber != i + 1)
            {
                throw new InvalidOperationException("Codex task ring segments must stay ordered by stable task number.");
            }
        }

        // Four gaps of 6 degrees each are subtracted from the full lap.
        if (Math.Abs(totalSweep - (360.0f - 4.0f * RingSegmentGapDegrees)) > 0.01f ||
            Math.Abs(ring.Segments[0].StartAngle - (RingStartAngle + RingSegmentGapDegrees / 2.0f)) > 0.01f)
        {
            throw new InvalidOperationException("Codex task ring geometry changed.");
        }

        CodexTaskBadgeModel badge = BuildBadge(fixture);
        if (badge.TaskCount != 4 || badge.AttentionCount != 2 ||
            badge.MostUrgentStatus != CodexTaskStatus.Listening ||
            badge.StatusColor.ToArgb() != GetStatusColor(CodexTaskStatus.Listening).ToArgb())
        {
            throw new InvalidOperationException("Codex task badge should count attention tasks and take the most urgent color.");
        }

        IList<CodexTaskRowModel> rows = BuildRows(fixture, now, 8);
        if (rows.Count != 4 ||
            rows[0].TaskNumber != 2 || !rows[0].NeedsAttention ||
            rows[1].TaskNumber != 4 ||
            rows[2].TaskNumber != 1 ||
            rows[3].TaskNumber != 3)
        {
            throw new InvalidOperationException("Codex task rows should sort by urgency then task number.");
        }

        if (!string.Equals(rows[0].DetailText, "9 分", StringComparison.Ordinal) ||
            !string.Equals(rows[2].DetailText, "刚刚", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex task row age formatting changed.");
        }

        if (!string.Equals(rows[0].SubText, "gpt-5.6-sol · 137.5K tok · 上下文 53.2%", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex task row sub text formatting changed: " + rows[0].SubText);
        }

        if (!string.Equals(FormatTokens(0L), "0", StringComparison.Ordinal) ||
            !string.Equals(FormatTokens(953L), "953", StringComparison.Ordinal) ||
            !string.Equals(FormatTokens(137520L), "137.5K", StringComparison.Ordinal) ||
            !string.Equals(FormatTokens(895148308L), "895.1M", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex task token formatting changed.");
        }

        if (BuildRows(fixture, now, 2).Count != 2)
        {
            throw new InvalidOperationException("Codex task rows must honor the row cap.");
        }

        // An all-idle set still draws a dim ring; only an empty set removes it entirely.
        List<CodexTaskSnapshot> idleOnly = new List<CodexTaskSnapshot>();
        idleOnly.Add(CreateFixtureTask(7, "ni", CodexTaskStatus.Idle, now.AddMinutes(-40.0), 1000L, 10.0));
        CodexTaskRingModel idleRing = BuildRing(new CodexTaskMonitorSnapshot(idleOnly, 0, now));
        if (!idleRing.HasSegments || idleRing.AnyAttention ||
            Math.Abs(idleRing.Segments[0].SweepDegrees - 360.0f) > 0.01f)
        {
            throw new InvalidOperationException("A single idle task should draw one full dim ring without attention.");
        }

        List<CodexTaskSnapshot> overflow = new List<CodexTaskSnapshot>();
        for (int i = 1; i <= MaximumRingSegments + 4; i++)
        {
            CodexTaskStatus status = i == MaximumRingSegments + 3 ? CodexTaskStatus.Error : CodexTaskStatus.Idle;
            overflow.Add(CreateFixtureTask(i, "ni", status, now.AddMinutes(-i), 1000L, 10.0));
        }

        CodexTaskRingModel capped = BuildRing(new CodexTaskMonitorSnapshot(overflow, 0, now));
        bool errorKept = false;
        for (int i = 0; i < capped.Segments.Count; i++)
        {
            if (capped.Segments[i].TaskNumber == MaximumRingSegments + 3)
            {
                errorKept = true;
            }
        }

        if (capped.Segments.Count != MaximumRingSegments || !errorKept)
        {
            throw new InvalidOperationException("An over-cap task set must keep the most urgent segments.");
        }

        RunContextWaterLevelSelfTest(now, fixture);
        RunTimelineSelfTest(now);
        Console.WriteLine("Codex task presentation: PASS empty provider-failure ring badge rows cap idle-ring water-level timeline");
    }

    // Scheme E folded into the table: the context bar ramps green -> amber -> red by water level and
    // is independent of task status, because a nearly full window is a problem even when idle.
    private static void RunContextWaterLevelSelfTest(DateTime now, CodexTaskMonitorSnapshot fixture)
    {
        if (GetContextBarColor(0.0).ToArgb() != GetContextBarColor(ContextWarningPercent - 0.1).ToArgb() ||
            GetContextBarColor(ContextWarningPercent).ToArgb() == GetContextBarColor(0.0).ToArgb() ||
            GetContextBarColor(ContextCriticalPercent).ToArgb() != DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 235).ToArgb() ||
            GetContextBarColor(ContextCriticalPercent - 0.1).ToArgb() == GetContextBarColor(ContextCriticalPercent).ToArgb())
        {
            throw new InvalidOperationException("Codex task context water-level ramp changed.");
        }

        List<CodexTaskSnapshot> waterTasks = new List<CodexTaskSnapshot>();
        waterTasks.Add(CreateFixtureTask(1, "ni", CodexTaskStatus.Idle, now.AddMinutes(-30.0), 207283L, 80.2));
        IList<CodexTaskRowModel> waterRows = BuildRows(new CodexTaskMonitorSnapshot(waterTasks, 0, now), now, 8);
        if (!waterRows[0].ContextCritical ||
            waterRows[0].ContextBarColor.ToArgb() != DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 235).ToArgb())
        {
            throw new InvalidOperationException("An idle task above the context threshold must still flag critical.");
        }

        IList<CodexTaskRowModel> rows = BuildRows(fixture, now, 8);
        // Fixture #2 sits at 53.2% and must stay below the warning ramp.
        if (rows[0].ContextCritical ||
            !string.Equals(rows[0].LastTurnTokensText, "137.5K", StringComparison.Ordinal) ||
            !string.Equals(rows[0].TotalTokensText, "137.5K", StringComparison.Ordinal) ||
            !string.Equals(rows[0].Model, "gpt-5.6-sol", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(rows[0].FileKey))
        {
            throw new InvalidOperationException("Codex task table columns changed.");
        }
    }

    private static void RunTimelineSelfTest(DateTime now)
    {
        ResetTimelineForTest();
        try
        {
            if (BuildTimeline(CodexTaskMonitorSnapshot.Empty, now, 45, 8).HasLanes)
            {
                throw new InvalidOperationException("An empty snapshot must produce no timeline lanes.");
            }

            // Two samples of the same status coalesce into one segment; a status change opens a new
            // one that starts exactly where the previous ended (no gaps in the lane).
            List<CodexTaskSnapshot> active = new List<CodexTaskSnapshot>();
            active.Add(CreateFixtureTask(1, "ni", CodexTaskStatus.Active, now.AddMinutes(-10.0), 1000L, 10.0));
            SampleTimeline(new CodexTaskMonitorSnapshot(active, 1, now), now.AddMinutes(-10.0), 45);
            SampleTimeline(new CodexTaskMonitorSnapshot(active, 1, now), now.AddMinutes(-8.0), 45);

            List<CodexTaskSnapshot> listening = new List<CodexTaskSnapshot>();
            listening.Add(CreateFixtureTask(1, "ni", CodexTaskStatus.Listening, now.AddMinutes(-8.0), 1000L, 10.0));
            SampleTimeline(new CodexTaskMonitorSnapshot(listening, 0, now), now.AddMinutes(-6.0), 45);
            SampleTimeline(new CodexTaskMonitorSnapshot(listening, 0, now), now, 45);

            CodexTaskTimelineModel model = BuildTimeline(new CodexTaskMonitorSnapshot(listening, 0, now), now, 45, 8);
            if (model.Lanes.Count != 1 || model.Lanes[0].Segments.Count != 2)
            {
                throw new InvalidOperationException("Timeline should coalesce same-status samples into one segment per run.");
            }

            CodexTaskTimelineSegment first = model.Lanes[0].Segments[0];
            CodexTaskTimelineSegment second = model.Lanes[0].Segments[1];
            if (first.Status != CodexTaskStatus.Active || second.Status != CodexTaskStatus.Listening ||
                first.EndLocal != second.StartLocal || second.EndLocal != now)
            {
                throw new InvalidOperationException("Timeline segments must be contiguous and ordered.");
            }

            // Everything older than the window is trimmed, and lanes clip to the window bounds.
            CodexTaskTimelineModel narrow = BuildTimeline(new CodexTaskMonitorSnapshot(listening, 0, now), now, 5, 8);
            for (int i = 0; i < narrow.Lanes[0].Segments.Count; i++)
            {
                if (narrow.Lanes[0].Segments[i].StartLocal < narrow.StartLocal ||
                    narrow.Lanes[0].Segments[i].EndLocal > narrow.EndLocal)
                {
                    throw new InvalidOperationException("Timeline segments must clip to the visible window.");
                }
            }

            SampleTimeline(new CodexTaskMonitorSnapshot(new List<CodexTaskSnapshot>(), 0, now), now.AddMinutes(120.0), 45);
            if (BuildTimeline(new CodexTaskMonitorSnapshot(listening, 0, now), now.AddMinutes(120.0), 45, 8).Lanes[0].Segments.Count != 0)
            {
                throw new InvalidOperationException("Timeline history must age out once it leaves the window.");
            }
        }
        finally
        {
            ResetTimelineForTest();
        }
    }
}
