using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class ClaudeRadarSnapshotSchedulerOutcome
{
    public ClaudeRadarSnapshot Snapshot { get; set; }
    public bool Success { get; set; }
    public ClaudeRadarServiceState Health { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Trigger { get; set; }
    public string RequestKey { get; set; }
    public List<string> ConsumerIds { get; set; }
}

internal static class ClaudeRadarSnapshotScheduler
{
    private static readonly object SyncRoot = new object();
    // Public Claude Radar data can be consumed by the shared Codex Radar Claude
    // mode and by the standalone Claude window at the same time. The request key
    // includes all data-source switches so matching consumers join one request,
    // but different selected models or fallback policies cannot overwrite each
    // other. Stored snapshots are cloned before leaving the scheduler.
    private static readonly Dictionary<string, RequestRuntime> Requests =
        new Dictionary<string, RequestRuntime>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ClaudeRadarSnapshot> LastGoodSnapshots =
        new Dictionary<string, ClaudeRadarSnapshot>(StringComparer.OrdinalIgnoreCase);
    private static Func<ClaudeRadarRequest, ClaudeRadarSnapshot> readerOverride;

    public static bool TryStartOrJoin(
        string consumerId,
        WidgetSettings settings,
        string trigger,
        out Task<ClaudeRadarSnapshotSchedulerOutcome> task)
    {
        task = null;
        if (settings == null)
        {
            return false;
        }

        ClaudeRadarRequest request = ClaudeRadarRequest.FromSettings(settings);
        string key = request.RequestKey;
        lock (SyncRoot)
        {
            RequestRuntime runtime;
            if (!Requests.TryGetValue(key, out runtime) || runtime == null)
            {
                runtime = new RequestRuntime(key);
                Requests[key] = runtime;
            }

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

            runtime.JoinedConsumers.Clear();
            runtime.JoinedConsumers.Add(NormalizeConsumerId(consumerId));
            string effectiveTrigger = NormalizeTrigger(trigger, "定时间隔");
            runtime.RunningTask = Task.Run(delegate
            {
                return ExecuteRequest(request, effectiveTrigger);
            });
            task = runtime.RunningTask;
            return true;
        }
    }

    public static ClaudeRadarSnapshot GetLastGoodSnapshot(WidgetSettings settings)
    {
        if (settings == null)
        {
            return null;
        }

        string key = ClaudeRadarRequest.FromSettings(settings).RequestKey;
        lock (SyncRoot)
        {
            ClaudeRadarSnapshot snapshot;
            return LastGoodSnapshots.TryGetValue(key, out snapshot) && snapshot != null
                ? snapshot.Clone()
                : null;
        }
    }

    public static void RunSelfTest()
    {
        Func<ClaudeRadarRequest, ClaudeRadarSnapshot> previousOverride = readerOverride;
        try
        {
            int calls = 0;
            readerOverride = delegate(ClaudeRadarRequest request)
            {
                Interlocked.Increment(ref calls);
                Thread.Sleep(80);
                ClaudeRadarSnapshot snapshot = ClaudeRadarReader.BuildRandomTestSnapshot(42);
                snapshot.SelectedModelKey = request.SelectedModelKey;
                snapshot.Known = true;
                snapshot.DataState = ClaudeRadarServiceState.Normal;
                return snapshot;
            };

            ResetForTest();
            WidgetSettings settings = new WidgetSettings();
            settings.Normalize();
            settings.ClaudeRadarModelKey = "model-a";

            Task<ClaudeRadarSnapshotSchedulerOutcome> first;
            Task<ClaudeRadarSnapshotSchedulerOutcome> second;
            if (!TryStartOrJoin("codex_radar", settings, "self_test", out first) ||
                !TryStartOrJoin("claude_radar", settings, "self_test", out second) ||
                !object.ReferenceEquals(first, second))
            {
                throw new InvalidOperationException("Claude Radar scheduler self-test: same key did not join.");
            }

            first.Wait(5000);
            if (calls != 1 ||
                !first.Result.Success ||
                first.Result.ConsumerIds.Count != 2)
            {
                throw new InvalidOperationException("Claude Radar scheduler self-test: joined outcome mismatch.");
            }

            WidgetSettings other = settings.Clone();
            other.ClaudeRadarHomepageFallbackEnabled = !settings.ClaudeRadarHomepageFallbackEnabled;
            Task<ClaudeRadarSnapshotSchedulerOutcome> third;
            Task<ClaudeRadarSnapshotSchedulerOutcome> fourth;
            if (!TryStartOrJoin("a", settings, "self_test", out third) ||
                !TryStartOrJoin("b", other, "self_test", out fourth) ||
                object.ReferenceEquals(third, fourth))
            {
                throw new InvalidOperationException("Claude Radar scheduler self-test: different keys incorrectly joined.");
            }

            Task.WaitAll(new Task[] { third, fourth }, 5000);

            readerOverride = delegate(ClaudeRadarRequest request)
            {
                ClaudeRadarSnapshot failed = ClaudeRadarSnapshot.CreateDefault();
                failed.DataState = ClaudeRadarServiceState.Unreachable;
                failed.ErrorCode = "NET";
                failed.ErrorMessage = "无法连接";
                return failed;
            };
            Task<ClaudeRadarSnapshotSchedulerOutcome> failedTask;
            if (!TryStartOrJoin("codex_radar", settings, "failure", out failedTask))
            {
                throw new InvalidOperationException("Claude Radar scheduler self-test: failure request not started.");
            }

            failedTask.Wait(5000);
            if (failedTask.Result.Success ||
                failedTask.Result.Snapshot == null ||
                !failedTask.Result.Snapshot.Known)
            {
                throw new InvalidOperationException("Claude Radar scheduler self-test: last-good fallback missing.");
            }

            ClaudeRadarSnapshot clone = failedTask.Result.Snapshot;
            clone.SelectedModelKey = "mutated";
            ClaudeRadarSnapshot stored = GetLastGoodSnapshot(settings);
            if (stored == null || string.Equals(stored.SelectedModelKey, "mutated", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Claude Radar scheduler self-test: clone mutation reached stored snapshot.");
            }
        }
        finally
        {
            readerOverride = previousOverride;
            ResetForTest();
        }
    }

    private static ClaudeRadarSnapshotSchedulerOutcome ExecuteRequest(ClaudeRadarRequest request, string trigger)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ClaudeRadarSnapshot snapshot = null;
        try
        {
            snapshot = readerOverride == null
                ? ClaudeRadarReader.ReadSnapshot(
                    request.SelectedModelKey,
                    request.JsonEnabled,
                    request.HomepageFallbackEnabled,
                    request.RatingsEnabled,
                    request.LocalQuotaFallbackEnabled)
                : readerOverride(request);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        stopwatch.Stop();
        ClaudeRadarServiceState health = snapshot == null ? ClaudeRadarServiceState.Unreachable : snapshot.DataState;
        bool success = snapshot != null && snapshot.Known && snapshot.DataState == ClaudeRadarServiceState.Normal;
        List<string> consumers;
        ClaudeRadarSnapshot output = snapshot == null ? ClaudeRadarSnapshot.CreateDefault() : snapshot.Clone();
        lock (SyncRoot)
        {
            RequestRuntime runtime;
            if (!Requests.TryGetValue(request.RequestKey, out runtime) || runtime == null)
            {
                runtime = new RequestRuntime(request.RequestKey);
                Requests[request.RequestKey] = runtime;
            }

            consumers = new List<string>(runtime.JoinedConsumers);
            runtime.RunningTask = null;
            runtime.JoinedConsumers.Clear();
            if (success)
            {
                LastGoodSnapshots[request.RequestKey] = output.Clone();
            }
            else
            {
                ClaudeRadarSnapshot lastGood;
                if (LastGoodSnapshots.TryGetValue(request.RequestKey, out lastGood) && lastGood != null)
                {
                    output = lastGood.Clone();
                    output.RequestRunning = false;
                }
            }
        }

        NetworkCheckHistoryLogger.LogCompleted(
            "claude_radar_backend",
            "claude_radar_snapshot",
            trigger,
            success ? "Normal" : health.ToString(),
            success,
            (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
            new Dictionary<string, object>
            {
                { "health", health.ToString() },
                { "known", snapshot != null && snapshot.Known },
                { "error_code", snapshot == null ? "ERROR" : (snapshot.ErrorCode ?? string.Empty) },
                { "request_key_hash", HashRequestKey(request.RequestKey) },
                { "joined_consumers", string.Join(",", consumers.ToArray()) }
            });

        return new ClaudeRadarSnapshotSchedulerOutcome
        {
            Snapshot = output.Clone(),
            Success = success,
            Health = health,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Trigger = NormalizeTrigger(trigger, "定时间隔"),
            RequestKey = request.RequestKey,
            ConsumerIds = consumers
        };
    }

    private static string HashRequestKey(string key)
    {
        try
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < Math.Min(8, bytes.Length); i++)
                {
                    builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ResetForTest()
    {
        lock (SyncRoot)
        {
            Requests.Clear();
            LastGoodSnapshots.Clear();
        }
    }

    private static string NormalizeConsumerId(string consumerId)
    {
        return string.IsNullOrWhiteSpace(consumerId) ? "unknown" : consumerId.Trim();
    }

    private static string NormalizeTrigger(string trigger, string fallback)
    {
        return string.IsNullOrWhiteSpace(trigger) ? fallback : trigger.Trim();
    }

    internal sealed class ClaudeRadarRequest
    {
        public string SelectedModelKey { get; private set; }
        public bool JsonEnabled { get; private set; }
        public bool HomepageFallbackEnabled { get; private set; }
        public bool RatingsEnabled { get; private set; }
        public bool LocalQuotaFallbackEnabled { get; private set; }
        public string RequestKey { get; private set; }

        public static ClaudeRadarRequest FromSettings(WidgetSettings settings)
        {
            ClaudeRadarRequest request = new ClaudeRadarRequest
            {
                SelectedModelKey = settings == null ? string.Empty : WidgetSettings.NormalizeClaudeRadarModelKey(settings.ClaudeRadarModelKey),
                JsonEnabled = settings == null || settings.ClaudeRadarJsonEnabled,
                HomepageFallbackEnabled = settings != null && settings.ClaudeRadarHomepageFallbackEnabled,
                RatingsEnabled = settings == null || settings.ClaudeRadarCommunityRatingsEnabled,
                LocalQuotaFallbackEnabled = settings == null || settings.ClaudeRadarLocalQuotaFallbackEnabled
            };
            request.RequestKey = string.Join(
                "|",
                request.SelectedModelKey,
                request.JsonEnabled ? "json1" : "json0",
                request.HomepageFallbackEnabled ? "home1" : "home0",
                request.RatingsEnabled ? "rating1" : "rating0",
                request.LocalQuotaFallbackEnabled ? "quota1" : "quota0");
            return request;
        }
    }

    private sealed class RequestRuntime
    {
        public RequestRuntime(string key)
        {
            this.Key = key ?? string.Empty;
            this.JoinedConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Key { get; private set; }
        public Task<ClaudeRadarSnapshotSchedulerOutcome> RunningTask { get; set; }
        public HashSet<string> JoinedConsumers { get; private set; }
    }
}
