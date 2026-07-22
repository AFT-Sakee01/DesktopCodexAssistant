using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
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
        CodexAppServerGoalController.RunExecutablePathPolicySelfTest();
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
            File.WriteAllText(tempPath, text, SharedEncoding.Utf8NoBom);
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

internal sealed class CodexExecutablePathPolicyContext
{
    public string CurrentDirectory { get; set; }
    public string[] TemporaryDirectories { get; set; }
    public string[] OfficialInstallRoots { get; set; }
}

internal sealed class CodexExecutablePathEvidence
{
    public string CandidatePath { get; set; }
    public bool FileExists { get; set; }
    public bool HasReparsePoint { get; set; }
    public bool AuthenticodeSignatureValid { get; set; }
    public string PublisherName { get; set; }
    public bool LocationWriteabilityKnown { get; set; }
    public bool LocationWritable { get; set; }
}

internal sealed class CodexExecutablePathDecision
{
    public bool Allowed { get; set; }
    public string CanonicalPath { get; set; }
    public string ErrorCode { get; set; }
    public bool IsOfficialInstall { get; set; }
}

// This policy is intentionally pure: callers must collect filesystem and signature evidence first.
// Keeping the decision free of I/O lets security failures be tested without starting or inspecting a real Codex process.
internal static class CodexExecutablePathPolicy
{
    private static readonly string[] TrustedPublisherNames = new string[]
    {
        "OpenAI OpCo, LLC",
        "OpenAI, L.L.C.",
        "OpenAI, LLC"
    };

    public static CodexExecutablePathDecision Evaluate(
        CodexExecutablePathEvidence evidence,
        CodexExecutablePathPolicyContext context)
    {
        if (evidence == null || context == null)
        {
            return Deny("INVALID_POLICY_INPUT", null);
        }

        string canonicalPath;
        string pathError;
        if (!TryCanonicalizeAbsoluteExecutablePath(evidence.CandidatePath, out canonicalPath, out pathError))
        {
            return Deny(pathError, null);
        }

        if (!evidence.FileExists)
        {
            return Deny("EXECUTABLE_NOT_FOUND", canonicalPath);
        }

        if (IsSameOrDescendant(canonicalPath, context.CurrentDirectory))
        {
            return Deny("CURRENT_DIRECTORY_FORBIDDEN", canonicalPath);
        }

        string[] temporaryDirectories = context.TemporaryDirectories ?? new string[0];
        for (int i = 0; i < temporaryDirectories.Length; i++)
        {
            if (IsSameOrDescendant(canonicalPath, temporaryDirectories[i]))
            {
                return Deny("TEMP_DIRECTORY_FORBIDDEN", canonicalPath);
            }
        }

        if (evidence.HasReparsePoint)
        {
            return Deny("REPARSE_POINT_FORBIDDEN", canonicalPath);
        }

        bool officialInstall = IsWithinAnyRoot(canonicalPath, context.OfficialInstallRoots);
        if (!officialInstall)
        {
            if (!evidence.LocationWriteabilityKnown)
            {
                return Deny("LOCATION_TRUST_UNVERIFIED", canonicalPath);
            }

            if (evidence.LocationWritable)
            {
                return Deny("UNTRUSTED_WRITABLE_LOCATION", canonicalPath);
            }
        }

        if (!evidence.AuthenticodeSignatureValid)
        {
            return Deny("AUTHENTICODE_UNVERIFIED", canonicalPath);
        }

        if (!IsTrustedPublisherName(evidence.PublisherName))
        {
            return Deny("PUBLISHER_UNTRUSTED", canonicalPath);
        }

        return new CodexExecutablePathDecision
        {
            Allowed = true,
            CanonicalPath = canonicalPath,
            ErrorCode = null,
            IsOfficialInstall = officialInstall
        };
    }

    public static bool TryCanonicalizeAbsoluteExecutablePath(string candidatePath, out string canonicalPath, out string errorCode)
    {
        canonicalPath = null;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            errorCode = "EMPTY_PATH";
            return false;
        }

        string value = candidatePath.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            value = value.Substring(1, value.Length - 2).Trim();
        }

        if (value.Length == 0 || value.IndexOf('"') >= 0)
        {
            errorCode = "INVALID_PATH_SYNTAX";
            return false;
        }

        bool isRooted;
        try
        {
            isRooted = Path.IsPathRooted(value);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
        {
            // Path APIs throw for embedded NUL and other malformed inputs. This method is a
            // security boundary, so malformed overrides are rejected instead of escaping policy.
            errorCode = "INVALID_PATH_SYNTAX";
            return false;
        }

        if (!isRooted)
        {
            errorCode = "RELATIVE_PATH_FORBIDDEN";
            return false;
        }

        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            errorCode = "NETWORK_PATH_FORBIDDEN";
            return false;
        }

        try
        {
            canonicalPath = Path.GetFullPath(value);
        }
        catch
        {
            errorCode = "INVALID_PATH_SYNTAX";
            return false;
        }

        if (!string.Equals(Path.GetFileName(canonicalPath), "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "EXECUTABLE_NAME_INVALID";
            canonicalPath = null;
            return false;
        }

        return true;
    }

    public static bool IsTrustedPublisherName(string publisherName)
    {
        if (string.IsNullOrWhiteSpace(publisherName))
        {
            return false;
        }

        string value = publisherName.Trim();
        for (int i = 0; i < TrustedPublisherNames.Length; i++)
        {
            if (string.Equals(value, TrustedPublisherNames[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSameOrDescendant(string candidatePath, string directoryPath)
    {
        string candidate = TryNormalizeForComparison(candidatePath);
        string directory = TryNormalizeForComparison(directoryPath);
        if (candidate == null || directory == null)
        {
            return false;
        }

        if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinAnyRoot(string candidatePath, string[] roots)
    {
        if (roots == null)
        {
            return false;
        }

        for (int i = 0; i < roots.Length; i++)
        {
            if (IsSameOrDescendant(candidatePath, roots[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string TryNormalizeForComparison(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static CodexExecutablePathDecision Deny(string errorCode, string canonicalPath)
    {
        return new CodexExecutablePathDecision
        {
            Allowed = false,
            CanonicalPath = canonicalPath,
            ErrorCode = errorCode ?? "POLICY_REJECTED",
            IsOfficialInstall = false
        };
    }
}

internal static class CodexAppServerGoalController
{
    private const int MaxThreadListCount = 100;

    internal static void RunExecutablePathPolicySelfTest()
    {
        string fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-CodexPathPolicy-" + Guid.NewGuid().ToString("N"));
        string currentDirectory = Path.Combine(fixtureRoot, "cwd");
        string temporaryDirectory = Path.Combine(fixtureRoot, "temp");
        string officialDirectory = Path.Combine(fixtureRoot, "official", "bin", "version-1");
        string protectedDirectory = Path.Combine(fixtureRoot, "protected");
        string writableDirectory = Path.Combine(fixtureRoot, "writable");
        string unknownDirectory = Path.Combine(fixtureRoot, "unknown");

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(temporaryDirectory);
            Directory.CreateDirectory(officialDirectory);
            Directory.CreateDirectory(protectedDirectory);
            Directory.CreateDirectory(writableDirectory);
            Directory.CreateDirectory(unknownDirectory);

            string currentExecutable = CreatePathPolicyFixtureExecutable(currentDirectory);
            string temporaryExecutable = CreatePathPolicyFixtureExecutable(temporaryDirectory);
            string officialExecutable = CreatePathPolicyFixtureExecutable(officialDirectory);
            string protectedExecutable = CreatePathPolicyFixtureExecutable(protectedDirectory);
            string writableExecutable = CreatePathPolicyFixtureExecutable(writableDirectory);
            string unknownExecutable = CreatePathPolicyFixtureExecutable(unknownDirectory);

            CodexExecutablePathPolicyContext context = new CodexExecutablePathPolicyContext
            {
                CurrentDirectory = currentDirectory,
                // The roots are injected so this fixture is independent of the machine running the test.
                TemporaryDirectories = new string[] { temporaryDirectory },
                OfficialInstallRoots = new string[] { Path.Combine(fixtureRoot, "official") }
            };

            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(officialExecutable, true, "OpenAI OpCo, LLC", true, true, true, context),
                true,
                null,
                "signed official install should be allowed even when its updater-owned location is writable");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(protectedExecutable, true, "OpenAI OpCo, LLC", true, true, false, context),
                true,
                null,
                "signed non-writable absolute install should be allowed");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture("codex.exe", true, "OpenAI OpCo, LLC", true, true, false, context),
                false,
                "RELATIVE_PATH_FORBIDDEN",
                "relative path should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(currentExecutable, true, "OpenAI OpCo, LLC", true, true, false, context),
                false,
                "CURRENT_DIRECTORY_FORBIDDEN",
                "current-directory executable should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(temporaryExecutable, true, "OpenAI OpCo, LLC", true, true, false, context),
                false,
                "TEMP_DIRECTORY_FORBIDDEN",
                "temporary-directory executable should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(writableExecutable, true, "OpenAI OpCo, LLC", true, true, true, context),
                false,
                "UNTRUSTED_WRITABLE_LOCATION",
                "untrusted writable executable should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(unknownExecutable, true, "OpenAI OpCo, LLC", false, true, false, context),
                false,
                "LOCATION_TRUST_UNVERIFIED",
                "unverifiable location should fail closed");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(officialExecutable, false, null, true, true, false, context),
                false,
                "AUTHENTICODE_UNVERIFIED",
                "unsigned official executable should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(officialExecutable, true, "Example Publisher", true, true, false, context),
                false,
                "PUBLISHER_UNTRUSTED",
                "unexpected Authenticode publisher should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(officialExecutable, true, "OpenAI OpCo, LLC", true, true, false, context, true),
                false,
                "REPARSE_POINT_FORBIDDEN",
                "reparse-point executable should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(Path.Combine(protectedDirectory, "missing", "codex.exe"), true, "OpenAI OpCo, LLC", true, true, false, context),
                false,
                "EXECUTABLE_NOT_FOUND",
                "missing executable should be rejected");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture("\"" + officialExecutable + "\" app-server", true, "OpenAI OpCo, LLC", true, true, false, context),
                false,
                "INVALID_PATH_SYNTAX",
                "path override must not contain arguments");
            AssertPathPolicyDecision(
                EvaluatePathPolicyFixture(officialExecutable + "\0suffix", true, "OpenAI OpCo, LLC", true, true, false, context),
                false,
                "INVALID_PATH_SYNTAX",
                "path override with an embedded NUL must fail closed");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, true);
            }
        }
    }

    private static string CreatePathPolicyFixtureExecutable(string directory)
    {
        string path = Path.Combine(directory, "codex.exe");
        File.WriteAllText(path, "SEC-08 path policy fixture; never execute.", SharedEncoding.Utf8NoBom);
        return path;
    }

    private static CodexExecutablePathDecision EvaluatePathPolicyFixture(
        string candidatePath,
        bool signatureValid,
        string publisherName,
        bool writeabilityKnown,
        bool fileExistsExpected,
        bool locationWritable,
        CodexExecutablePathPolicyContext context,
        bool hasReparsePoint = false)
    {
        return CodexExecutablePathPolicy.Evaluate(
            new CodexExecutablePathEvidence
            {
                CandidatePath = candidatePath,
                FileExists = fileExistsExpected && IsExistingRootedPathFixture(candidatePath),
                HasReparsePoint = hasReparsePoint,
                AuthenticodeSignatureValid = signatureValid,
                PublisherName = publisherName,
                LocationWriteabilityKnown = writeabilityKnown,
                LocationWritable = locationWritable
            },
            context);
    }

    private static bool IsExistingRootedPathFixture(string candidatePath)
    {
        try
        {
            return Path.IsPathRooted(candidatePath) && File.Exists(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
        {
            return false;
        }
    }

    private static void AssertPathPolicyDecision(
        CodexExecutablePathDecision decision,
        bool expectedAllowed,
        string expectedErrorCode,
        string message)
    {
        if (decision == null ||
            decision.Allowed != expectedAllowed ||
            !string.Equals(decision.ErrorCode, expectedErrorCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                message + "; actual=" +
                (decision == null ? "null" : (decision.Allowed ? "allowed" : decision.ErrorCode)));
        }
    }

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
                string resolutionError;
                using (CodexExecutableLease executable = ResolveCodexExecutable(out resolutionError))
                {
                    if (executable == null)
                    {
                        error = "Codex app-server executable failed security validation (" +
                            (resolutionError ?? "NO_APPROVED_EXECUTABLE") + ").";
                        Program.LogInfo(
                            "security_error module=CodexQuotaGoalPlanner event=codex_app_server_executable_rejected code=" +
                            (resolutionError ?? "NO_APPROVED_EXECUTABLE"));
                        return null;
                    }

                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = executable.CanonicalPath;
                    startInfo.Arguments = "app-server";
                    startInfo.WorkingDirectory = Path.GetDirectoryName(executable.CanonicalPath);
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
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Program.LogInfo("codex app-server start failed: " + error);
                return null;
            }
        }

        private static CodexExecutableLease ResolveCodexExecutable(out string errorCode)
        {
            errorCode = null;
            CodexExecutablePathPolicyContext context = BuildRuntimePathPolicyContext();
            List<string> candidates = DiscoverCodexExecutableCandidates(context.OfficialInstallRoots);
            if (candidates.Count == 0)
            {
                errorCode = "NO_EXECUTABLE_CANDIDATE";
                return null;
            }

            List<string> rejectionCodes = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidateError;
                CodexExecutableLease executable = TryAcquireApprovedExecutable(candidates[i], context, out candidateError);
                if (executable != null)
                {
                    return executable;
                }

                if (!string.IsNullOrWhiteSpace(candidateError) && !rejectionCodes.Contains(candidateError))
                {
                    rejectionCodes.Add(candidateError);
                }
            }

            errorCode = rejectionCodes.Count == 0
                ? "NO_APPROVED_EXECUTABLE"
                : string.Join(",", rejectionCodes.ToArray());
            return null;
        }

        private static CodexExecutablePathPolicyContext BuildRuntimePathPolicyContext()
        {
            List<string> temporaryDirectories = new List<string>();
            AddUniqueDirectory(temporaryDirectories, Path.GetTempPath());
            AddUniqueDirectory(temporaryDirectories, Environment.GetEnvironmentVariable("TEMP"));
            AddUniqueDirectory(temporaryDirectories, Environment.GetEnvironmentVariable("TMP"));
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                AddUniqueDirectory(temporaryDirectories, Path.Combine(windowsDirectory, "Temp"));
            }

            return new CodexExecutablePathPolicyContext
            {
                CurrentDirectory = Environment.CurrentDirectory,
                TemporaryDirectories = temporaryDirectories.ToArray(),
                OfficialInstallRoots = GetOfficialInstallRoots()
            };
        }

        private static string[] GetOfficialInstallRoots()
        {
            List<string> roots = new List<string>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                AddUniqueDirectory(roots, Path.Combine(localAppData, "OpenAI", "Codex", "bin"));
                AddUniqueDirectory(roots, Path.Combine(localAppData, "Programs", "OpenAI", "Codex"));
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                AddUniqueDirectory(roots, Path.Combine(programFiles, "OpenAI", "Codex"));
                AddUniqueDirectory(roots, Path.Combine(programFiles, "WindowsApps"));
            }

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                AddUniqueDirectory(roots, Path.Combine(programFilesX86, "OpenAI", "Codex"));
            }

            string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrWhiteSpace(programW6432))
            {
                AddUniqueDirectory(roots, Path.Combine(programW6432, "OpenAI", "Codex"));
            }

            return roots.ToArray();
        }

        private static List<string> DiscoverCodexExecutableCandidates(string[] officialInstallRoots)
        {
            List<string> officialCandidates = new List<string>();
            if (officialInstallRoots != null)
            {
                for (int i = 0; i < officialInstallRoots.Length; i++)
                {
                    string root = officialInstallRoots[i];
                    if (string.IsNullOrWhiteSpace(root) ||
                        root.EndsWith(Path.DirectorySeparatorChar + "WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    CollectOfficialCodexCandidates(root, 0, 3, officialCandidates);
                }
            }

            officialCandidates.Sort(delegate(string left, string right)
            {
                int newestFirst = GetLastWriteTimeUtcSafe(right).CompareTo(GetLastWriteTimeUtcSafe(left));
                return newestFirst != 0
                    ? newestFirst
                    : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });

            List<string> result = new List<string>();
            for (int i = 0; i < officialCandidates.Count; i++)
            {
                AddUniqueCandidate(result, officialCandidates[i]);
            }

            // Explicit overrides remain supported, but they are lower priority and pass the same fail-closed policy.
            AddUniqueCandidate(result, Environment.GetEnvironmentVariable("DESKTOP_CODEX_APP_SERVER_COMMAND"));
            AddUniqueCandidate(result, Environment.GetEnvironmentVariable("CODEX_CLI_PATH"));
            return result;
        }

        private static void CollectOfficialCodexCandidates(string directory, int depth, int maxDepth, List<string> result)
        {
            if (depth > maxDepth || string.IsNullOrWhiteSpace(directory) || result == null)
            {
                return;
            }

            try
            {
                if (!Directory.Exists(directory) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                string directCandidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(directCandidate))
                {
                    AddUniqueCandidate(result, directCandidate);
                }

                if (depth == maxDepth)
                {
                    return;
                }

                string[] children = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < children.Length; i++)
                {
                    CollectOfficialCodexCandidates(children[i], depth + 1, maxDepth, result);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static CodexExecutableLease TryAcquireApprovedExecutable(
            string candidatePath,
            CodexExecutablePathPolicyContext context,
            out string errorCode)
        {
            errorCode = null;
            string lexicalPath;
            if (!CodexExecutablePathPolicy.TryCanonicalizeAbsoluteExecutablePath(candidatePath, out lexicalPath, out errorCode))
            {
                return null;
            }

            FileStream leaseStream = null;
            try
            {
                // Excluding write/delete sharing prevents replacement between validation and Process.Start.
                leaseStream = new FileStream(
                    lexicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);

                string finalPath;
                if (!TryGetFinalCanonicalPath(leaseStream, out finalPath))
                {
                    errorCode = "CANONICAL_PATH_UNVERIFIED";
                    return null;
                }

                bool hasReparsePoint;
                if (!TryDetectReparsePoint(lexicalPath, out hasReparsePoint))
                {
                    errorCode = "REPARSE_CHECK_FAILED";
                    return null;
                }

                bool writeabilityKnown;
                bool locationWritable;
                TryDetermineLocationWriteability(finalPath, out writeabilityKnown, out locationWritable);

                bool signatureValid;
                string publisherName;
                ReadAuthenticodeEvidence(finalPath, out signatureValid, out publisherName);

                CodexExecutablePathDecision decision = CodexExecutablePathPolicy.Evaluate(
                    new CodexExecutablePathEvidence
                    {
                        CandidatePath = finalPath,
                        FileExists = true,
                        HasReparsePoint = hasReparsePoint,
                        AuthenticodeSignatureValid = signatureValid,
                        PublisherName = publisherName,
                        LocationWriteabilityKnown = writeabilityKnown,
                        LocationWritable = locationWritable
                    },
                    context);
                if (!decision.Allowed)
                {
                    errorCode = decision.ErrorCode;
                    return null;
                }

                CodexExecutableLease approved = new CodexExecutableLease(decision.CanonicalPath, leaseStream);
                leaseStream = null;
                return approved;
            }
            catch (FileNotFoundException)
            {
                errorCode = "EXECUTABLE_NOT_FOUND";
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                errorCode = "EXECUTABLE_NOT_FOUND";
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                errorCode = "EXECUTABLE_ACCESS_DENIED";
                return null;
            }
            catch (IOException)
            {
                errorCode = "EXECUTABLE_OPEN_FAILED";
                return null;
            }
            catch
            {
                errorCode = "EXECUTABLE_VALIDATION_FAILED";
                return null;
            }
            finally
            {
                if (leaseStream != null)
                {
                    leaseStream.Dispose();
                }
            }
        }

        private static bool TryGetFinalCanonicalPath(FileStream stream, out string finalPath)
        {
            finalPath = null;
            if (stream == null || stream.SafeFileHandle == null || stream.SafeFileHandle.IsInvalid)
            {
                return false;
            }

            StringBuilder buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                return false;
            }

            if (length >= (uint)buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(stream.SafeFileHandle.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= (uint)buffer.Capacity)
                {
                    return false;
                }
            }

            string value = buffer.ToString();
            if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                value = "\\\\" + value.Substring(8);
            }
            else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(4);
            }

            string ignored;
            return CodexExecutablePathPolicy.TryCanonicalizeAbsoluteExecutablePath(value, out finalPath, out ignored);
        }

        private static bool TryDetectReparsePoint(string path, out bool hasReparsePoint)
        {
            hasReparsePoint = false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                string current = root;
                string remainder = fullPath.Substring(root.Length);
                string[] segments = remainder.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segments.Length; i++)
                {
                    current = Path.Combine(current, segments[i]);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        hasReparsePoint = true;
                        return true;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDetermineLocationWriteability(
            string executablePath,
            out bool writeabilityKnown,
            out bool locationWritable)
        {
            writeabilityKnown = false;
            locationWritable = false;
            try
            {
                string directory = Path.GetDirectoryName(executablePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    HashSet<string> principalSids = GetEffectivePrincipalSids(identity);
                    DirectorySecurity directorySecurity = Directory.GetAccessControl(directory, AccessControlSections.Access | AccessControlSections.Owner);
                    FileSecurity fileSecurity = File.GetAccessControl(executablePath, AccessControlSections.Access | AccessControlSections.Owner);
                    locationWritable = HasMutationAccess(directorySecurity, principalSids) ||
                        HasMutationAccess(fileSecurity, principalSids);
                    writeabilityKnown = true;
                }
            }
            catch
            {
                writeabilityKnown = false;
                locationWritable = false;
            }
        }

        private static HashSet<string> GetEffectivePrincipalSids(WindowsIdentity identity)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (identity == null)
            {
                return result;
            }

            if (identity.User != null)
            {
                result.Add(identity.User.Value);
            }

            bool administratorEnabled = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            SecurityIdentifier administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            IdentityReferenceCollection groups = identity.Groups;
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    SecurityIdentifier sid = groups[i] as SecurityIdentifier;
                    if (sid == null || (!administratorEnabled && sid.Equals(administratorsSid)))
                    {
                        continue;
                    }

                    result.Add(sid.Value);
                }
            }

            return result;
        }

        private static bool HasMutationAccess(FileSystemSecurity security, HashSet<string> principalSids)
        {
            if (security == null || principalSids == null || principalSids.Count == 0)
            {
                return false;
            }

            SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner != null && principalSids.Contains(owner.Value))
            {
                // Owners can rewrite the DACL even if the current rules appear read-only.
                return true;
            }

            const FileSystemRights mutationRights =
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.WriteAttributes |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;
            FileSystemRights allowed = 0;
            FileSystemRights denied = 0;
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            for (int i = 0; i < rules.Count; i++)
            {
                FileSystemAccessRule rule = rules[i] as FileSystemAccessRule;
                SecurityIdentifier sid = rule == null ? null : rule.IdentityReference as SecurityIdentifier;
                if (rule == null || sid == null || !principalSids.Contains(sid.Value))
                {
                    continue;
                }

                if (rule.AccessControlType == AccessControlType.Deny)
                {
                    denied |= rule.FileSystemRights;
                }
                else
                {
                    allowed |= rule.FileSystemRights;
                }
            }

            FileSystemRights effective = allowed & ~denied;
            return (effective & mutationRights) != 0;
        }

        private static void ReadAuthenticodeEvidence(string executablePath, out bool signatureValid, out string publisherName)
        {
            signatureValid = false;
            publisherName = null;
            try
            {
                if (VerifyEmbeddedAuthenticodeSignature(executablePath) != 0)
                {
                    return;
                }

                using (X509Certificate2 certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executablePath)))
                {
                    publisherName = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    signatureValid = true;
                }
            }
            catch
            {
                signatureValid = false;
                publisherName = null;
            }
        }

        private static int VerifyEmbeddedAuthenticodeSignature(string executablePath)
        {
            Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            IntPtr pathPointer = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            IntPtr trustDataPointer = IntPtr.Zero;
            IntPtr actionPointer = IntPtr.Zero;
            WinTrustData trustData = new WinTrustData();
            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(executablePath);
                WinTrustFileInfo fileInfo = new WinTrustFileInfo
                {
                    StructureSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                    FilePath = pathPointer,
                    FileHandle = IntPtr.Zero,
                    KnownSubject = IntPtr.Zero
                };
                fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                trustData = new WinTrustData
                {
                    StructureSize = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    PolicyCallbackData = IntPtr.Zero,
                    SipClientData = IntPtr.Zero,
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 1,
                    StateData = IntPtr.Zero,
                    UrlReference = IntPtr.Zero,
                    ProviderFlags = 0x00000010 | 0x00001000,
                    UiContext = 0,
                    SignatureSettings = IntPtr.Zero
                };
                trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(trustData, trustDataPointer, false);

                actionPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(Guid)));
                Marshal.StructureToPtr(action, actionPointer, false);
                int status = WinVerifyTrust(IntPtr.Zero, actionPointer, trustDataPointer);

                trustData = (WinTrustData)Marshal.PtrToStructure(trustDataPointer, typeof(WinTrustData));
                trustData.StateAction = 2;
                Marshal.StructureToPtr(trustData, trustDataPointer, false);
                WinVerifyTrust(IntPtr.Zero, actionPointer, trustDataPointer);
                return status;
            }
            finally
            {
                if (actionPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(actionPointer);
                }

                if (trustDataPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(trustDataPointer);
                }

                if (fileInfoPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(fileInfoPointer);
                }

                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
        }

        private static void AddUniqueDirectory(List<string> directories, string path)
        {
            if (directories == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string canonical = Path.GetFullPath(path.Trim());
                for (int i = 0; i < directories.Count; i++)
                {
                    if (string.Equals(directories[i], canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                directories.Add(canonical);
            }
            catch
            {
            }
        }

        private static void AddUniqueCandidate(List<string> candidates, string path)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string value = path.Trim();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            candidates.Add(value);
        }

        private static DateTime GetLastWriteTimeUtcSafe(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructureSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructureSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        private sealed class CodexExecutableLease : IDisposable
        {
            private FileStream stream;

            public CodexExecutableLease(string canonicalPath, FileStream stream)
            {
                this.CanonicalPath = canonicalPath;
                this.stream = stream;
            }

            public string CanonicalPath { get; private set; }

            public void Dispose()
            {
                if (this.stream != null)
                {
                    this.stream.Dispose();
                    this.stream = null;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            IntPtr fileHandle,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr windowHandle, IntPtr actionId, IntPtr trustData);

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
