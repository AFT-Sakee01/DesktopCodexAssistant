using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal enum StatuspageHealthState
{
    Unknown,
    Normal,
    Degraded,
    Incomplete,
    Offline,
    Unavailable,
    Unreachable
}

internal sealed class StatuspageSnapshot
{
    public string ServiceKey { get; set; }
    public bool Known { get; set; }
    public StatuspageHealthState State { get; set; }
    public string Indicator { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public bool RequestRunning { get; set; }

    public static StatuspageSnapshot CreateDefault(string serviceKey)
    {
        return new StatuspageSnapshot
        {
            ServiceKey = NormalizeServiceKey(serviceKey),
            Known = false,
            State = StatuspageHealthState.Unknown,
            Indicator = string.Empty,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            CheckedAtUtc = DateTime.MinValue,
            RequestRunning = false
        };
    }

    public StatuspageSnapshot Clone()
    {
        return new StatuspageSnapshot
        {
            ServiceKey = this.ServiceKey ?? string.Empty,
            Known = this.Known,
            State = this.State,
            Indicator = this.Indicator ?? string.Empty,
            ErrorCode = this.ErrorCode ?? string.Empty,
            ErrorMessage = this.ErrorMessage ?? string.Empty,
            CheckedAtUtc = this.CheckedAtUtc,
            RequestRunning = this.RequestRunning
        };
    }

    internal static string NormalizeServiceKey(string serviceKey)
    {
        return string.IsNullOrWhiteSpace(serviceKey) ? string.Empty : serviceKey.Trim().ToLowerInvariant();
    }
}

internal sealed class StatuspageRefreshOutcome
{
    public StatuspageSnapshot Snapshot { get; set; }
    public string Trigger { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public List<string> ConsumerIds { get; set; }
}

internal static class StatuspageMonitor
{
    public const string OpenAiServiceKey = "openai";
    public const string ClaudeServiceKey = "claude";
    private const int TimeoutMs = 10000;
    private const int NormalRefreshMinutes = 15;
    private const int FailureRefreshMinutes = 2;
    private static readonly object SyncRoot = new object();
    // The status endpoints are small but shared by two radar windows. Keeping one
    // runtime per service key prevents duplicate HTTP requests and duplicate
    // network-history rows, while still allowing OpenAI and Claude checks to run
    // independently. UI color mapping stays outside this monitor.
    private static readonly Dictionary<string, ServiceRuntime> Services =
        new Dictionary<string, ServiceRuntime>(StringComparer.OrdinalIgnoreCase);
    private static Func<string, WidgetSettings, StatuspageSnapshot> readerOverride;

    static StatuspageMonitor()
    {
        Services[OpenAiServiceKey] = new ServiceRuntime(OpenAiServiceKey);
        Services[ClaudeServiceKey] = new ServiceRuntime(ClaudeServiceKey);
    }

    public static void RequestRefresh(string serviceKey, string trigger)
    {
        ServiceRuntime runtime = GetRuntime(serviceKey);
        lock (SyncRoot)
        {
            runtime.NextRefreshUtc = DateTime.MinValue;
            runtime.PendingTrigger = NormalizeTrigger(trigger, "强制刷新");
        }
    }

    public static StatuspageSnapshot GetSnapshot(string serviceKey)
    {
        ServiceRuntime runtime = GetRuntime(serviceKey);
        lock (SyncRoot)
        {
            StatuspageSnapshot snapshot = runtime.Snapshot ?? StatuspageSnapshot.CreateDefault(runtime.ServiceKey);
            if (runtime.RunningTask != null && !runtime.RunningTask.IsCompleted)
            {
                StatuspageSnapshot running = snapshot.Clone();
                running.RequestRunning = true;
                return running;
            }

            return snapshot.Clone();
        }
    }

    public static bool TryStartOrJoin(
        string serviceKey,
        string consumerId,
        WidgetSettings settings,
        string trigger,
        out Task<StatuspageRefreshOutcome> task)
    {
        task = null;
        if (settings == null)
        {
            return false;
        }

        ServiceRuntime runtime = GetRuntime(serviceKey);
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            if (runtime.RunningTask != null && !runtime.RunningTask.IsCompleted)
            {
                string consumer = NormalizeConsumerId(consumerId);
                if (runtime.JoinedConsumers.Contains(consumer))
                {
                    return false;
                }

                runtime.JoinedConsumers.Add(consumer);
                task = runtime.RunningTask;
                return true;
            }

            bool scheduledDue = runtime.NextRefreshUtc == DateTime.MinValue || nowUtc >= runtime.NextRefreshUtc;
            bool force = runtime.Snapshot == null ||
                runtime.Snapshot.State == StatuspageHealthState.Unknown ||
                runtime.Snapshot.State == StatuspageHealthState.Offline;
            if (!scheduledDue && !force)
            {
                return false;
            }

            WidgetSettings requestSettings = settings.Clone();
            string effectiveTrigger = NormalizeTrigger(
                string.IsNullOrWhiteSpace(runtime.PendingTrigger) || string.Equals(runtime.PendingTrigger, "定时间隔", StringComparison.Ordinal)
                    ? trigger
                    : runtime.PendingTrigger,
                "定时间隔");
            runtime.PendingTrigger = "定时间隔";
            runtime.JoinedConsumers.Clear();
            runtime.JoinedConsumers.Add(NormalizeConsumerId(consumerId));

            StatuspageSnapshot running = runtime.Snapshot == null
                ? StatuspageSnapshot.CreateDefault(runtime.ServiceKey)
                : runtime.Snapshot.Clone();
            running.RequestRunning = true;
            runtime.Snapshot = running;

            runtime.RunningTask = Task.Run(delegate
            {
                return ExecuteRequest(runtime.ServiceKey, requestSettings, effectiveTrigger);
            });
            task = runtime.RunningTask;
            return true;
        }
    }

    public static void RunSelfTest()
    {
        if (ParseIndicator("none") != StatuspageHealthState.Normal ||
            ParseIndicator("minor") != StatuspageHealthState.Degraded ||
            ParseIndicator("major") != StatuspageHealthState.Unavailable ||
            ParseIndicator("critical") != StatuspageHealthState.Unavailable)
        {
            throw new InvalidOperationException("Statuspage monitor self-test: indicator mapping failed.");
        }

        Func<string, WidgetSettings, StatuspageSnapshot> previousOverride = readerOverride;
        try
        {
            int openAiCalls = 0;
            int claudeCalls = 0;
            readerOverride = delegate(string key, WidgetSettings settings)
            {
                if (string.Equals(key, OpenAiServiceKey, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref openAiCalls);
                    Thread.Sleep(80);
                    return BuildSnapshotForTest(key, StatuspageHealthState.Normal, "none", string.Empty);
                }

                Interlocked.Increment(ref claudeCalls);
                return BuildSnapshotForTest(key, StatuspageHealthState.Degraded, "minor", string.Empty);
            };

            ResetForTest(OpenAiServiceKey);
            ResetForTest(ClaudeServiceKey);
            WidgetSettings settings = new WidgetSettings();
            settings.Normalize();
            WidgetSettings blocked = settings.Clone();
            blocked.AiRequestProtectionManualBlockEnabled = true;
            StatuspageSnapshot blockedSnapshot = ReadStatuspage(OpenAiServiceKey, blocked);
            if (blockedSnapshot.State != StatuspageHealthState.Unavailable ||
                !string.Equals(blockedSnapshot.ErrorCode, "AI_BLOCK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Statuspage monitor self-test: AI request block was not respected.");
            }

            Task<StatuspageRefreshOutcome> first;
            Task<StatuspageRefreshOutcome> second;
            if (!TryStartOrJoin(OpenAiServiceKey, "codex_radar", settings, "self_test", out first) ||
                !TryStartOrJoin(OpenAiServiceKey, "claude_radar", settings, "self_test", out second) ||
                !object.ReferenceEquals(first, second))
            {
                throw new InvalidOperationException("Statuspage monitor self-test: same service did not join running request.");
            }

            first.Wait(5000);
            if (openAiCalls != 1 ||
                first.Result.ConsumerIds.Count != 2 ||
                first.Result.Snapshot.State != StatuspageHealthState.Normal)
            {
                throw new InvalidOperationException("Statuspage monitor self-test: joined request result mismatch.");
            }

            Task<StatuspageRefreshOutcome> openAiTask;
            Task<StatuspageRefreshOutcome> claudeTask;
            RequestRefresh(OpenAiServiceKey, "parallel");
            RequestRefresh(ClaudeServiceKey, "parallel");
            if (!TryStartOrJoin(OpenAiServiceKey, "a", settings, "parallel", out openAiTask) ||
                !TryStartOrJoin(ClaudeServiceKey, "b", settings, "parallel", out claudeTask) ||
                object.ReferenceEquals(openAiTask, claudeTask))
            {
                throw new InvalidOperationException("Statuspage monitor self-test: different services incorrectly joined.");
            }

            Task.WaitAll(new Task[] { openAiTask, claudeTask }, 5000);
            if (claudeCalls != 1)
            {
                throw new InvalidOperationException("Statuspage monitor self-test: Claude service request did not run.");
            }
        }
        finally
        {
            readerOverride = previousOverride;
            ResetForTest(OpenAiServiceKey);
            ResetForTest(ClaudeServiceKey);
        }
    }

    private static StatuspageRefreshOutcome ExecuteRequest(string serviceKey, WidgetSettings settings, string trigger)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        StatuspageSnapshot snapshot;
        try
        {
            snapshot = readerOverride == null
                ? ReadStatuspage(serviceKey, settings)
                : readerOverride(serviceKey, settings);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            snapshot = BuildErrorSnapshot(serviceKey, StatuspageHealthState.Unreachable, "ERROR", "请求失败");
        }

        stopwatch.Stop();
        if (snapshot == null)
        {
            snapshot = BuildErrorSnapshot(serviceKey, StatuspageHealthState.Unreachable, "ERROR", "请求失败");
        }

        snapshot.ServiceKey = StatuspageSnapshot.NormalizeServiceKey(serviceKey);
        snapshot.RequestRunning = false;
        List<string> consumers;
        lock (SyncRoot)
        {
            ServiceRuntime runtime = GetRuntime(serviceKey);
            consumers = new List<string>(runtime.JoinedConsumers);
            runtime.Snapshot = snapshot.Clone();
            runtime.NextRefreshUtc = DateTime.UtcNow.AddMinutes(snapshot.State == StatuspageHealthState.Normal
                ? NormalRefreshMinutes
                : FailureRefreshMinutes);
            runtime.RunningTask = null;
            runtime.JoinedConsumers.Clear();
        }

        NetworkCheckHistoryLogger.LogCompleted(
            "statuspage_monitor",
            serviceKey + "_status",
            trigger,
            snapshot.State.ToString(),
            snapshot.State == StatuspageHealthState.Normal,
            (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
            new Dictionary<string, object>
            {
                { "health", snapshot.State.ToString() },
                { "indicator", snapshot.Indicator ?? string.Empty },
                { "error_code", snapshot.ErrorCode ?? string.Empty },
                { "joined_consumers", string.Join(",", consumers.ToArray()) }
            });

        return new StatuspageRefreshOutcome
        {
            Snapshot = snapshot.Clone(),
            Trigger = NormalizeTrigger(trigger, "定时间隔"),
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            ConsumerIds = consumers
        };
    }

    private static StatuspageSnapshot ReadStatuspage(string serviceKey, WidgetSettings settings)
    {
        serviceKey = StatuspageSnapshot.NormalizeServiceKey(serviceKey);
        string url = GetServiceUrl(serviceKey);
        if (string.IsNullOrEmpty(url))
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Unavailable, "SERVICE", "未知服务");
        }

        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, url, out aiBlockReason))
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Unavailable, "AI_BLOCK", "请求保护");
        }

        if (!IsNetworkAvailable())
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Offline, "OFFLINE", "无网络");
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = TimeoutMs;
        request.ReadWriteTimeout = TimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
            request,
            BoundedHttpTextReader.PublicJsonMaxBytes,
            TimeoutMs,
            CancellationToken.None);
        if (response.StatusCode <= 0)
        {
            return BuildErrorSnapshot(
                serviceKey,
                IsNetworkAvailable() ? StatuspageHealthState.Unreachable : StatuspageHealthState.Offline,
                response.ErrorCode,
                "无法连接");
        }

        if (!response.Success)
        {
            string code = response.StatusCode >= 400
                ? response.StatusCode.ToString(CultureInfo.InvariantCulture)
                : response.ErrorCode;
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Unavailable, code, "响应不可用");
        }

        if (string.IsNullOrEmpty(response.Content))
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Incomplete, "EMPTY", "空响应");
        }

        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = BoundedHttpTextReader.PublicJsonMaxBytes;
            Dictionary<string, object> root = serializer.DeserializeObject(response.Content) as Dictionary<string, object>;
            return ParseStatuspageSummary(serviceKey, root);
        }
        catch
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Incomplete, "PARSE", "响应格式无效");
        }
    }

    private static StatuspageSnapshot ParseStatuspageSummary(string serviceKey, Dictionary<string, object> root)
    {
        if (root == null)
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Incomplete, "PARSE", "解析失败");
        }

        Dictionary<string, object> status = ReadObject(root, "status");
        string indicator = ReadString(status, "indicator").Trim().ToLowerInvariant();
        StatuspageHealthState state = ParseIndicator(indicator);
        if (state == StatuspageHealthState.Unknown)
        {
            return BuildErrorSnapshot(serviceKey, StatuspageHealthState.Incomplete, "INDICATOR", "状态缺失");
        }

        return new StatuspageSnapshot
        {
            ServiceKey = StatuspageSnapshot.NormalizeServiceKey(serviceKey),
            Known = true,
            State = state,
            Indicator = indicator,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            CheckedAtUtc = DateTime.UtcNow,
            RequestRunning = false
        };
    }

    private static StatuspageHealthState ParseIndicator(string indicator)
    {
        if (string.Equals(indicator, "none", StringComparison.OrdinalIgnoreCase))
        {
            return StatuspageHealthState.Normal;
        }

        if (string.Equals(indicator, "minor", StringComparison.OrdinalIgnoreCase))
        {
            return StatuspageHealthState.Degraded;
        }

        if (string.Equals(indicator, "major", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(indicator, "critical", StringComparison.OrdinalIgnoreCase))
        {
            return StatuspageHealthState.Unavailable;
        }

        return StatuspageHealthState.Unknown;
    }

    private static StatuspageSnapshot BuildErrorSnapshot(
        string serviceKey,
        StatuspageHealthState state,
        string errorCode,
        string errorMessage)
    {
        return new StatuspageSnapshot
        {
            ServiceKey = StatuspageSnapshot.NormalizeServiceKey(serviceKey),
            Known = false,
            State = state,
            Indicator = string.Empty,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            CheckedAtUtc = DateTime.UtcNow,
            RequestRunning = false
        };
    }

    private static StatuspageSnapshot BuildSnapshotForTest(
        string serviceKey,
        StatuspageHealthState state,
        string indicator,
        string errorCode)
    {
        return new StatuspageSnapshot
        {
            ServiceKey = StatuspageSnapshot.NormalizeServiceKey(serviceKey),
            Known = true,
            State = state,
            Indicator = indicator ?? string.Empty,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = string.Empty,
            CheckedAtUtc = DateTime.UtcNow,
            RequestRunning = false
        };
    }

    private static void ResetForTest(string serviceKey)
    {
        lock (SyncRoot)
        {
            ServiceRuntime runtime = GetRuntime(serviceKey);
            runtime.Snapshot = StatuspageSnapshot.CreateDefault(runtime.ServiceKey);
            runtime.NextRefreshUtc = DateTime.MinValue;
            runtime.PendingTrigger = "首次刷新";
            runtime.RunningTask = null;
            runtime.JoinedConsumers.Clear();
        }
    }

    private static ServiceRuntime GetRuntime(string serviceKey)
    {
        string normalized = StatuspageSnapshot.NormalizeServiceKey(serviceKey);
        lock (SyncRoot)
        {
            ServiceRuntime runtime;
            if (!Services.TryGetValue(normalized, out runtime))
            {
                runtime = new ServiceRuntime(normalized);
                Services[normalized] = runtime;
            }

            return runtime;
        }
    }

    private static string GetServiceUrl(string serviceKey)
    {
        if (string.Equals(serviceKey, OpenAiServiceKey, StringComparison.OrdinalIgnoreCase))
        {
            return "https://status.openai.com/api/v2/summary.json";
        }

        if (string.Equals(serviceKey, ClaudeServiceKey, StringComparison.OrdinalIgnoreCase))
        {
            return "https://status.claude.com/api/v2/summary.json";
        }

        return string.Empty;
    }

    private static bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    private static Dictionary<string, object> ReadObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values != null && values.TryGetValue(key, out value))
        {
            return value as Dictionary<string, object>;
        }

        return null;
    }

    private static string ReadString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values != null && values.TryGetValue(key, out value) && value != null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeConsumerId(string consumerId)
    {
        return string.IsNullOrWhiteSpace(consumerId) ? "unknown" : consumerId.Trim();
    }

    private static string NormalizeTrigger(string trigger, string fallback)
    {
        return string.IsNullOrWhiteSpace(trigger) ? fallback : trigger.Trim();
    }

    private sealed class ServiceRuntime
    {
        public ServiceRuntime(string serviceKey)
        {
            this.ServiceKey = StatuspageSnapshot.NormalizeServiceKey(serviceKey);
            this.Snapshot = StatuspageSnapshot.CreateDefault(this.ServiceKey);
            this.NextRefreshUtc = DateTime.MinValue;
            this.PendingTrigger = "首次刷新";
            this.JoinedConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public string ServiceKey { get; private set; }
        public StatuspageSnapshot Snapshot { get; set; }
        public DateTime NextRefreshUtc { get; set; }
        public string PendingTrigger { get; set; }
        public Task<StatuspageRefreshOutcome> RunningTask { get; set; }
        public HashSet<string> JoinedConsumers { get; private set; }
    }
}
