using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class CodexGoalInfo
{
    public string ThreadId { get; set; }
    public string Objective { get; set; }
    public string Status { get; set; }
    public string ThreadName { get; set; }
    public string Preview { get; set; }

    public string DisplayText
    {
        get
        {
            string title = FirstNonEmpty(this.Objective, this.ThreadName, this.Preview, this.ThreadId);
            string status = string.IsNullOrWhiteSpace(this.Status) ? "unknown" : this.Status.Trim();
            return title + "  [" + status + "]  " + this.ThreadId;
        }
    }

    public static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

internal sealed class CodexQuotaPlanSnapshot
{
    public int FiveHourPercent { get; set; }
    public int WeeklyPercent { get; set; }
    public DateTime SourceUpdatedUtc { get; set; }
    public bool SourceUpdatedKnown { get; set; }
}

internal sealed class CodexQuotaGoalPlanner
{
    private const int MinEvaluationIntervalSeconds = 60;
    private const int MaxQuotaSnapshotAgeMinutes = 45;
    private const string GoalPauseStatus = "usageLimited";
    private const string GoalResumeStatus = "active";
    private static readonly object StateFileLock = new object();
    private readonly object syncRoot = new object();
    private DateTime lastEvaluationUtc = DateTime.MinValue;
    private bool evaluationRunning;
    private bool triggerActive;
    private HashSet<string> pausedGoalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public CodexQuotaGoalPlanner()
    {
        LoadActionState();
    }

    public static string StatePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "codex-quota-plan-state.json"); }
    }

    public void ProcessMaintenanceTick(WidgetSettings settings, Action<string, string, ToolTipIcon> notificationAction)
    {
        if (settings == null || !settings.CodexQuotaPlanEnabled)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        lock (this.syncRoot)
        {
            if (this.evaluationRunning ||
                (this.lastEvaluationUtc != DateTime.MinValue &&
                 (nowUtc - this.lastEvaluationUtc).TotalSeconds < MinEvaluationIntervalSeconds))
            {
                return;
            }

            this.evaluationRunning = true;
            this.lastEvaluationUtc = nowUtc;
        }

        WidgetSettings captured = settings.Clone();
        Task.Run(delegate
        {
            try
            {
                EvaluateAndApply(captured, notificationAction);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
            finally
            {
                lock (this.syncRoot)
                {
                    this.evaluationRunning = false;
                }
            }
        });
    }

    public static bool ShouldTrigger(WidgetSettings settings, int weeklyPercent, int fiveHourPercent)
    {
        if (settings == null || !settings.CodexQuotaPlanEnabled)
        {
            return false;
        }

        return Matches(settings.CodexQuotaPlanWeeklyComparison, weeklyPercent, settings.CodexQuotaPlanWeeklyThresholdPercent) &&
            Matches(settings.CodexQuotaPlanFiveHourComparison, fiveHourPercent, settings.CodexQuotaPlanFiveHourThresholdPercent);
    }

    public static bool ShouldResume(WidgetSettings settings, int weeklyPercent, int fiveHourPercent)
    {
        if (settings == null || !settings.CodexQuotaPlanEnabled)
        {
            return false;
        }

        bool weeklyRecovered = IsRecovered(
            settings.CodexQuotaPlanWeeklyComparison,
            weeklyPercent,
            settings.CodexQuotaPlanWeeklyThresholdPercent);
        bool fiveHourRecovered = IsRecovered(
            settings.CodexQuotaPlanFiveHourComparison,
            fiveHourPercent,
            settings.CodexQuotaPlanFiveHourThresholdPercent);

        if (settings.CodexQuotaPlanResumeConditionMode == CodexQuotaPlanResumeConditionMode.WeeklyOnly)
        {
            return weeklyRecovered;
        }

        if (settings.CodexQuotaPlanResumeConditionMode == CodexQuotaPlanResumeConditionMode.FiveHourOnly)
        {
            return fiveHourRecovered;
        }

        return weeklyRecovered && fiveHourRecovered;
    }

    public static bool TryReadQuotaSnapshot(out CodexQuotaPlanSnapshot snapshot)
    {
        snapshot = new CodexQuotaPlanSnapshot
        {
            FiveHourPercent = 100,
            WeeklyPercent = 100,
            SourceUpdatedUtc = DateTime.MinValue,
            SourceUpdatedKnown = false
        };

        string path = Path.Combine(Logger.DirectoryPath, "quota.ini");
        if (!File.Exists(path))
        {
            return false;
        }

        bool found = false;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = (lines[i] ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.FiveHourPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "SourceUpdatedUtc", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out dateTime))
                {
                    snapshot.SourceUpdatedUtc = dateTime.ToUniversalTime();
                    snapshot.SourceUpdatedKnown = true;
                    found = true;
                }
            }

            if (found && !snapshot.SourceUpdatedKnown)
            {
                snapshot.SourceUpdatedUtc = File.GetLastWriteTimeUtc(path);
                snapshot.SourceUpdatedKnown = snapshot.SourceUpdatedUtc != DateTime.MinValue;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        return found;
    }

    public static bool IsQuotaSnapshotFresh(CodexQuotaPlanSnapshot snapshot, DateTime nowUtc)
    {
        return snapshot != null &&
            snapshot.SourceUpdatedKnown &&
            snapshot.SourceUpdatedUtc != DateTime.MinValue &&
            snapshot.SourceUpdatedUtc <= nowUtc.AddMinutes(5) &&
            (nowUtc - snapshot.SourceUpdatedUtc).TotalMinutes <= MaxQuotaSnapshotAgeMinutes;
    }

    public static List<CodexGoalInfo> LoadKnownGoals()
    {
        lock (StateFileLock)
        {
            Dictionary<string, object> root = ReadStateNoLock();
            object raw;
            if (root == null || !root.TryGetValue("known_goals", out raw))
            {
                return new List<CodexGoalInfo>();
            }

            object[] array = raw as object[];
            List<CodexGoalInfo> goals = new List<CodexGoalInfo>();
            if (array == null)
            {
                return goals;
            }

            for (int i = 0; i < array.Length; i++)
            {
                Dictionary<string, object> item = array[i] as Dictionary<string, object>;
                if (item == null)
                {
                    continue;
                }

                string threadId = GetString(item, "thread_id");
                if (WidgetSettings.NormalizeGoalIdList(threadId).Length == 0)
                {
                    continue;
                }

                goals.Add(new CodexGoalInfo
                {
                    ThreadId = threadId,
                    Objective = GetString(item, "objective"),
                    Status = GetString(item, "status"),
                    ThreadName = GetString(item, "thread_name"),
                    Preview = GetString(item, "preview")
                });
            }

            return goals;
        }
    }

    public static void SaveKnownGoals(IEnumerable<CodexGoalInfo> goals)
    {
        lock (StateFileLock)
        {
            Dictionary<string, object> root = ReadStateNoLock() ?? new Dictionary<string, object>(StringComparer.Ordinal);
            List<object> serialized = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (goals != null)
            {
                foreach (CodexGoalInfo goal in goals)
                {
                    if (goal == null)
                    {
                        continue;
                    }

                    string id = WidgetSettings.NormalizeGoalIdList(goal.ThreadId);
                    if (id.Length == 0 || seen.Contains(id))
                    {
                        continue;
                    }

                    seen.Add(id);
                    serialized.Add(new Dictionary<string, object>
                    {
                        { "thread_id", id },
                        { "objective", TrimForState(goal.Objective, 240) },
                        { "status", TrimForState(goal.Status, 40) },
                        { "thread_name", TrimForState(goal.ThreadName, 160) },
                        { "preview", TrimForState(goal.Preview, 200) }
                    });
                }
            }

            root["schema_version"] = 1;
            root["known_goals"] = serialized.ToArray();
            WriteStateNoLock(root);
        }
    }

    public static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CodexQuotaPlanEnabled = true;
        settings.CodexQuotaPlanWeeklyComparison = CodexQuotaPlanComparison.LessThan;
        settings.CodexQuotaPlanWeeklyThresholdPercent = 3;
        settings.CodexQuotaPlanFiveHourComparison = CodexQuotaPlanComparison.LessThan;
        settings.CodexQuotaPlanFiveHourThresholdPercent = 90;
        settings.Normalize();
        AssertSelfTest(ShouldTrigger(settings, 2, 89), "default condition should trigger below both thresholds");
        AssertSelfTest(!ShouldTrigger(settings, 3, 89), "less-than condition should be strict for weekly quota");
        AssertSelfTest(!ShouldTrigger(settings, 2, 90), "less-than condition should be strict for five-hour quota");
        settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.Both;
        AssertSelfTest(!ShouldResume(settings, 3, 89), "both resume condition should wait for five-hour quota recovery");
        AssertSelfTest(ShouldResume(settings, 3, 90), "both resume condition should pass when both quotas are recovered");
        settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.FiveHourOnly;
        AssertSelfTest(ShouldResume(settings, 2, 90), "five-hour resume condition should ignore weekly quota");
        settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.WeeklyOnly;
        AssertSelfTest(ShouldResume(settings, 3, 89), "weekly resume condition should ignore five-hour quota");
        settings.CodexQuotaPlanResumeConditionMode = CodexQuotaPlanResumeConditionMode.Both;
        settings.CodexQuotaPlanWeeklyComparison = CodexQuotaPlanComparison.GreaterThan;
        settings.CodexQuotaPlanWeeklyThresholdPercent = 80;
        settings.CodexQuotaPlanFiveHourComparison = CodexQuotaPlanComparison.GreaterThan;
        settings.CodexQuotaPlanFiveHourThresholdPercent = 50;
        AssertSelfTest(ShouldTrigger(settings, 81, 51), "greater-than condition should trigger above both thresholds");
        AssertSelfTest(ShouldResume(settings, 80, 50), "greater-than resume condition should recover at or below thresholds");
        settings.CodexQuotaPlanEnabled = false;
        AssertSelfTest(!ShouldTrigger(settings, 1, 1), "disabled quota plan should not trigger");
        AssertSelfTest(!ShouldResume(settings, 100, 100), "disabled quota plan should not resume");
    }

    private void EvaluateAndApply(WidgetSettings settings, Action<string, string, ToolTipIcon> notificationAction)
    {
        settings.Normalize();
        CodexQuotaPlanSnapshot snapshot;
        if (!TryReadQuotaSnapshot(out snapshot))
        {
            Program.LogInfo("Codex quota plan skipped: quota.ini is unavailable.");
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (!IsQuotaSnapshotFresh(snapshot, nowUtc))
        {
            Program.LogInfo("Codex quota plan skipped: quota snapshot is stale.");
            return;
        }

        bool shouldTrigger = ShouldTrigger(settings, snapshot.WeeklyPercent, snapshot.FiveHourPercent);
        bool wasTriggerActive;
        HashSet<string> pausedBefore;
        lock (this.syncRoot)
        {
            wasTriggerActive = this.triggerActive;
            pausedBefore = new HashSet<string>(this.pausedGoalIds, StringComparer.OrdinalIgnoreCase);
        }

        if (shouldTrigger && !wasTriggerActive)
        {
            List<string> pauseIds = SplitGoalIds(settings.CodexQuotaPlanPauseGoalIds);
            if (pauseIds.Count == 0)
            {
                Program.LogInfo("Codex quota plan triggered but no pause goals are selected.");
                return;
            }

            string error;
            List<string> changed = CodexAppServerGoalController.SetGoalStatuses(pauseIds, GoalPauseStatus, out error);
            if (changed.Count == 0)
            {
                Program.LogInfo("Codex quota plan pause failed. Error=" + (error ?? string.Empty));
                return;
            }

            lock (this.syncRoot)
            {
                this.triggerActive = true;
                for (int i = 0; i < changed.Count; i++)
                {
                    this.pausedGoalIds.Add(changed[i]);
                }
            }

            SaveActionState();
            Notify(notificationAction, "Codex 额度计划", "已按额度条件暂停 " + changed.Count.ToString(CultureInfo.InvariantCulture) + " 个 goal。", ToolTipIcon.Warning);
            Program.LogInfo("Codex quota plan paused goals. Count=" + changed.Count.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (wasTriggerActive && ShouldResume(settings, snapshot.WeeklyPercent, snapshot.FiveHourPercent))
        {
            List<string> resumeIds = settings.CodexQuotaPlanAutoResumePausedGoals
                ? new List<string>(pausedBefore)
                : SplitGoalIds(settings.CodexQuotaPlanResumeGoalIds);
            if (resumeIds.Count == 0)
            {
                lock (this.syncRoot)
                {
                    this.triggerActive = false;
                    this.pausedGoalIds.Clear();
                }

                SaveActionState();
                return;
            }

            string error;
            List<string> changed = CodexAppServerGoalController.SetGoalStatuses(resumeIds, GoalResumeStatus, out error);
            if (changed.Count == 0)
            {
                Program.LogInfo("Codex quota plan resume failed. Error=" + (error ?? string.Empty));
                return;
            }

            lock (this.syncRoot)
            {
                for (int i = 0; i < changed.Count; i++)
                {
                    this.pausedGoalIds.Remove(changed[i]);
                }

                if (this.pausedGoalIds.Count == 0 || !settings.CodexQuotaPlanAutoResumePausedGoals)
                {
                    this.triggerActive = false;
                }
            }

            SaveActionState();
            Notify(notificationAction, "Codex 额度计划", "额度条件已恢复，已启用 " + changed.Count.ToString(CultureInfo.InvariantCulture) + " 个 goal。", ToolTipIcon.Info);
            Program.LogInfo("Codex quota plan resumed goals. Count=" + changed.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void LoadActionState()
    {
        lock (StateFileLock)
        {
            Dictionary<string, object> root = ReadStateNoLock();
            if (root == null)
            {
                return;
            }

            this.triggerActive = GetBool(root, "trigger_active");
            this.pausedGoalIds = new HashSet<string>(GetStringList(root, "paused_goal_ids"), StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveActionState()
    {
        lock (StateFileLock)
        {
            Dictionary<string, object> root = ReadStateNoLock() ?? new Dictionary<string, object>(StringComparer.Ordinal);
            bool active;
            string[] paused;
            lock (this.syncRoot)
            {
                active = this.triggerActive;
                paused = new List<string>(this.pausedGoalIds).ToArray();
            }

            root["schema_version"] = 1;
            root["trigger_active"] = active;
            root["paused_goal_ids"] = paused;
            root["last_action_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            WriteStateNoLock(root);
        }
    }

    private static bool Matches(CodexQuotaPlanComparison comparison, int actual, int threshold)
    {
        if (comparison == CodexQuotaPlanComparison.GreaterThan)
        {
            return actual > threshold;
        }

        return actual < threshold;
    }

    private static bool IsRecovered(CodexQuotaPlanComparison comparison, int actual, int threshold)
    {
        return !Matches(comparison, actual, threshold);
    }

    private static List<string> SplitGoalIds(string raw)
    {
        string normalized = WidgetSettings.NormalizeGoalIdList(raw);
        List<string> values = new List<string>();
        if (normalized.Length == 0)
        {
            return values;
        }

        string[] parts = normalized.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            values.Add(parts[i]);
        }

        return values;
    }

    private static Dictionary<string, object> ReadStateNoLock()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            string text = File.ReadAllText(StatePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    private static void WriteStateNoLock(Dictionary<string, object> root)
    {
        try
        {
            Directory.CreateDirectory(Logger.DirectoryPath);
            string text = new JavaScriptSerializer().Serialize(root ?? new Dictionary<string, object>()) + Environment.NewLine;
            string tempPath = StatePath + ".tmp";
            File.WriteAllText(tempPath, text, new UTF8Encoding(false));
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }

            File.Move(tempPath, StatePath);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static string GetString(Dictionary<string, object> values, string key)
    {
        object raw;
        return values != null && values.TryGetValue(key, out raw) && raw != null ? Convert.ToString(raw, CultureInfo.InvariantCulture) : string.Empty;
    }

    private static bool GetBool(Dictionary<string, object> values, string key)
    {
        object raw;
        if (values == null || !values.TryGetValue(key, out raw) || raw == null)
        {
            return false;
        }

        if (raw is bool)
        {
            return (bool)raw;
        }

        bool parsed;
        return bool.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out parsed) && parsed;
    }

    private static List<string> GetStringList(Dictionary<string, object> values, string key)
    {
        List<string> result = new List<string>();
        object raw;
        if (values == null || !values.TryGetValue(key, out raw) || raw == null)
        {
            return result;
        }

        object[] array = raw as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                string value = WidgetSettings.NormalizeGoalIdList(Convert.ToString(array[i], CultureInfo.InvariantCulture));
                if (value.Length > 0)
                {
                    result.Add(value);
                }
            }

            return result;
        }

        return SplitGoalIds(Convert.ToString(raw, CultureInfo.InvariantCulture));
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static string TrimForState(string value, int maxLength)
    {
        value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength);
    }

    private static void Notify(Action<string, string, ToolTipIcon> notificationAction, string title, string message, ToolTipIcon icon)
    {
        if (notificationAction == null)
        {
            return;
        }

        try
        {
            notificationAction(title, message, icon);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal static class CodexAppServerGoalController
{
    private const int MaxThreadListCount = 100;

    public static List<CodexGoalInfo> ListGoals(out string error)
    {
        error = null;
        List<CodexGoalInfo> goals = new List<CodexGoalInfo>();
        using (CodexAppServerClient client = CodexAppServerClient.Start(out error))
        {
            if (client == null)
            {
                return goals;
            }

            if (!client.Initialize(out error))
            {
                return goals;
            }

            Dictionary<string, object> listParams = new Dictionary<string, object>
            {
                { "limit", MaxThreadListCount },
                { "sortKey", "recency_at" },
                { "sortDirection", "desc" },
                { "sourceKinds", new object[] { "cli", "vscode", "exec", "appServer", "subAgent", "subAgentReview", "subAgentCompact", "subAgentThreadSpawn", "subAgentOther", "unknown" } }
            };
            Dictionary<string, object> result = client.SendRequest("thread/list", listParams, out error);
            if (result == null)
            {
                listParams.Remove("sourceKinds");
                result = client.SendRequest("thread/list", listParams, out error);
            }

            if (result == null)
            {
                return goals;
            }

            object rawData;
            object[] threads = result.TryGetValue("data", out rawData) ? rawData as object[] : null;
            if (threads == null)
            {
                error = "thread/list did not return a data array.";
                return goals;
            }

            for (int i = 0; i < threads.Length; i++)
            {
                Dictionary<string, object> thread = threads[i] as Dictionary<string, object>;
                if (thread == null)
                {
                    continue;
                }

                string threadId = GetString(thread, "id");
                if (WidgetSettings.NormalizeGoalIdList(threadId).Length == 0)
                {
                    continue;
                }

                string getError;
                Dictionary<string, object> goalResult = client.SendRequest(
                    "thread/goal/get",
                    new Dictionary<string, object> { { "threadId", threadId } },
                    out getError);
                Dictionary<string, object> goal = GetObject(goalResult, "goal");
                if (goal == null)
                {
                    continue;
                }

                string objective = GetString(goal, "objective");
                if (string.IsNullOrWhiteSpace(objective))
                {
                    continue;
                }

                goals.Add(new CodexGoalInfo
                {
                    ThreadId = threadId,
                    Objective = objective,
                    Status = GetString(goal, "status"),
                    ThreadName = GetString(thread, "name"),
                    Preview = GetString(thread, "preview")
                });
            }
        }

        return goals;
    }

    public static List<string> SetGoalStatuses(IEnumerable<string> threadIds, string status, out string error)
    {
        error = null;
        List<string> changed = new List<string>();
        List<string> ids = NormalizeIds(threadIds);
        if (ids.Count == 0)
        {
            return changed;
        }

        using (CodexAppServerClient client = CodexAppServerClient.Start(out error))
        {
            if (client == null)
            {
                return changed;
            }

            if (!client.Initialize(out error))
            {
                return changed;
            }

            StringBuilder errors = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                string setError;
                if (TrySetGoalStatus(client, ids[i], status, out setError))
                {
                    changed.Add(ids[i]);
                }
                else
                {
                    if (errors.Length > 0)
                    {
                        errors.Append("; ");
                    }

                    errors.Append(ids[i]).Append(": ").Append(setError ?? "unknown");
                }
            }

            if (errors.Length > 0)
            {
                error = errors.ToString();
            }
        }

        return changed;
    }

    private static bool TrySetGoalStatus(CodexAppServerClient client, string threadId, string status, out string error)
    {
        Dictionary<string, object> setParams = new Dictionary<string, object>
        {
            { "threadId", threadId },
            { "status", status }
        };

        Dictionary<string, object> result = client.SendRequest("thread/goal/set", setParams, out error);
        if (result != null)
        {
            return true;
        }

        string resumeError;
        client.SendRequest("thread/resume", new Dictionary<string, object> { { "threadId", threadId } }, out resumeError);
        result = client.SendRequest("thread/goal/set", setParams, out error);
        return result != null;
    }

    private static List<string> NormalizeIds(IEnumerable<string> threadIds)
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (threadIds == null)
        {
            return result;
        }

        foreach (string raw in threadIds)
        {
            string id = WidgetSettings.NormalizeGoalIdList(raw);
            if (id.Length == 0 || seen.Contains(id))
            {
                continue;
            }

            seen.Add(id);
            result.Add(id);
        }

        return result;
    }

    private static string GetString(Dictionary<string, object> values, string key)
    {
        object raw;
        return values != null && values.TryGetValue(key, out raw) && raw != null ? Convert.ToString(raw, CultureInfo.InvariantCulture) : string.Empty;
    }

    private static Dictionary<string, object> GetObject(Dictionary<string, object> values, string key)
    {
        object raw;
        return values != null && values.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null;
    }

    private sealed class CodexAppServerClient : IDisposable
    {
        private const int RequestTimeoutMs = 12000;
        private readonly Process process;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private int nextId;

        private CodexAppServerClient(Process process)
        {
            this.process = process;
        }

        public static CodexAppServerClient Start(out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = ResolveCodexExecutable();
                startInfo.Arguments = "app-server";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                Process process = Process.Start(startInfo);
                if (process == null)
                {
                    error = "codex app-server failed to start.";
                    return null;
                }

                return new CodexAppServerClient(process);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Program.LogInfo("codex app-server start failed: " + error);
                return null;
            }
        }

        private static string ResolveCodexExecutable()
        {
            string overridePath = Environment.GetEnvironmentVariable("DESKTOP_CODEX_APP_SERVER_COMMAND");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath.Trim().Trim('"');
            }

            string cliPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!string.IsNullOrWhiteSpace(cliPath))
            {
                return cliPath.Trim().Trim('"');
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string binRoot = Path.Combine(Path.Combine(Path.Combine(localAppData, "OpenAI"), "Codex"), "bin");
                if (Directory.Exists(binRoot))
                {
                    string[] candidates = Directory.GetFiles(binRoot, "codex.exe", SearchOption.AllDirectories);
                    string newestPath = null;
                    DateTime newestWriteUtc = DateTime.MinValue;
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        DateTime writeUtc = File.GetLastWriteTimeUtc(candidates[i]);
                        if (newestPath == null || writeUtc > newestWriteUtc)
                        {
                            newestPath = candidates[i];
                            newestWriteUtc = writeUtc;
                        }
                    }

                    if (!string.IsNullOrEmpty(newestPath))
                    {
                        return newestPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }

            return "codex";
        }

        public bool Initialize(out string error)
        {
            Dictionary<string, object> initParams = new Dictionary<string, object>
            {
                {
                    "clientInfo",
                    new Dictionary<string, object>
                    {
                        { "name", "desktop_codex_assistant" },
                        { "title", ProductIdentity.DisplayName },
                        { "version", ProductIdentity.Version }
                    }
                },
                {
                    "capabilities",
                    new Dictionary<string, object>
                    {
                        { "experimentalApi", true }
                    }
                }
            };

            if (SendRequest("initialize", initParams, out error) == null)
            {
                return false;
            }

            SendNotification("initialized", new Dictionary<string, object>());
            return true;
        }

        public Dictionary<string, object> SendRequest(string method, Dictionary<string, object> parameters, out string error)
        {
            error = null;
            int id = ++this.nextId;
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                { "method", method },
                { "id", id },
                { "params", parameters ?? new Dictionary<string, object>() }
            };
            if (!WriteMessage(request, out error))
            {
                return null;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                string line = ReadLine(remaining);
                if (line == null)
                {
                    break;
                }

                Dictionary<string, object> message;
                try
                {
                    message = this.serializer.DeserializeObject(line) as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }

                if (message == null || !IsResponseId(message, id))
                {
                    continue;
                }

                Dictionary<string, object> errorObject = GetObject(message, "error");
                if (errorObject != null)
                {
                    error = GetString(errorObject, "message");
                    return null;
                }

                return GetObject(message, "result") ?? new Dictionary<string, object>();
            }

            error = method + " timed out.";
            return null;
        }

        private void SendNotification(string method, Dictionary<string, object> parameters)
        {
            string error;
            WriteMessage(new Dictionary<string, object>
            {
                { "method", method },
                { "params", parameters ?? new Dictionary<string, object>() }
            }, out error);
        }

        private bool WriteMessage(Dictionary<string, object> message, out string error)
        {
            error = null;
            try
            {
                this.process.StandardInput.WriteLine(this.serializer.Serialize(message));
                this.process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                error = ex.Message;
                return false;
            }
        }

        private string ReadLine(int timeoutMs)
        {
            try
            {
                Task<string> task = Task<string>.Factory.StartNew(delegate { return this.process.StandardOutput.ReadLine(); });
                if (!task.Wait(timeoutMs))
                {
                    return null;
                }

                return task.Result;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsResponseId(Dictionary<string, object> message, int expectedId)
        {
            object raw;
            if (message == null || !message.TryGetValue("id", out raw) || raw == null)
            {
                return false;
            }

            try
            {
                return Convert.ToInt32(raw, CultureInfo.InvariantCulture) == expectedId;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (this.process == null)
                {
                    return;
                }

                try
                {
                    this.process.StandardInput.Close();
                }
                catch
                {
                }

                if (!this.process.WaitForExit(1000))
                {
                    this.process.Kill();
                }
            }
            catch
            {
            }
            finally
            {
                if (this.process != null)
                {
                    this.process.Dispose();
                }
            }
        }
    }
}
