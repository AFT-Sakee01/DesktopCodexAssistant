using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

internal sealed class DeepSeekServiceSnapshot
{
    public bool Known { get; set; }
    public bool IsAvailable { get; set; }
    public bool RequestRunning { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public DateTime CheckedAtLocal { get; set; }

    public static DeepSeekServiceSnapshot CreateUnknown()
    {
        return new DeepSeekServiceSnapshot
        {
            Known = false,
            IsAvailable = false,
            RequestRunning = false,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            CheckedAtUtc = DateTime.MinValue,
            CheckedAtLocal = DateTime.MinValue
        };
    }

    public DeepSeekServiceSnapshot Clone()
    {
        return new DeepSeekServiceSnapshot
        {
            Known = this.Known,
            IsAvailable = this.IsAvailable,
            RequestRunning = this.RequestRunning,
            ErrorCode = this.ErrorCode,
            ErrorMessage = this.ErrorMessage,
            CheckedAtUtc = this.CheckedAtUtc,
            CheckedAtLocal = this.CheckedAtLocal
        };
    }
}

internal static class DeepSeekServiceMonitor
{
    internal const string ProbeUrl = "https://api.deepseek.com/models";
    private const int TimeoutMs = 10000;
    private const int NormalRefreshSeconds = 60;
    private const int ErrorRefreshSeconds = 300;

    private static readonly object SyncRoot = new object();
    // The monitor is process-wide: all consumers join one request so timer ticks, network events
    // and manual refreshes cannot multiply unauthenticated probes or duplicate completion logs.
    private static readonly Dictionary<string, Action> JoinedCallbacks =
        new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
    private static DeepSeekServiceSnapshot snapshot = DeepSeekServiceSnapshot.CreateUnknown();
    private static bool requestRunning;
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    private static string refreshTrigger = "首次刷新";
    private static Func<DeepSeekServiceSnapshot> serviceReaderOverride;

    internal static void RequestRefresh()
    {
        RequestRefresh("强制刷新");
    }

    internal static void RequestRefresh(string trigger)
    {
        lock (SyncRoot)
        {
            nextRefreshUtc = DateTime.UtcNow;
            refreshTrigger = NormalizeTrigger(trigger, "强制刷新");
        }
    }

    internal static DeepSeekServiceSnapshot GetSnapshot()
    {
        lock (SyncRoot)
        {
            return snapshot == null ? DeepSeekServiceSnapshot.CreateUnknown() : snapshot.Clone();
        }
    }

    internal static void SetSnapshotForTest(DeepSeekServiceSnapshot testSnapshot)
    {
        lock (SyncRoot)
        {
            snapshot = testSnapshot == null ? DeepSeekServiceSnapshot.CreateUnknown() : testSnapshot.Clone();
            snapshot.RequestRunning = false;
            requestRunning = false;
            nextRefreshUtc = DateTime.UtcNow.AddHours(1.0);
            JoinedCallbacks.Clear();
        }
    }

    internal static bool RefreshIfNeeded(Action onSnapshotChanged)
    {
        return RefreshIfNeeded("unknown", "定时间隔", onSnapshotChanged);
    }

    internal static bool RefreshIfNeeded(string consumerId, string trigger, Action onSnapshotChanged)
    {
        DateTime nowUtc = DateTime.UtcNow;
        string effectiveTrigger = NormalizeTrigger(trigger, "定时间隔");
        lock (SyncRoot)
        {
            string consumer = NormalizeConsumerId(consumerId);
            if (requestRunning ||
                (nextRefreshUtc != DateTime.MinValue && nowUtc < nextRefreshUtc))
            {
                if (requestRunning && !JoinedCallbacks.ContainsKey(consumer))
                {
                    JoinedCallbacks[consumer] = onSnapshotChanged;
                    return true;
                }

                return false;
            }

            effectiveTrigger = !string.IsNullOrWhiteSpace(refreshTrigger) &&
                !string.Equals(refreshTrigger, "定时间隔", StringComparison.Ordinal)
                    ? refreshTrigger
                    : effectiveTrigger;
            refreshTrigger = "定时间隔";
            requestRunning = true;
            JoinedCallbacks.Clear();
            JoinedCallbacks[consumer] = onSnapshotChanged;
            DeepSeekServiceSnapshot running = snapshot == null
                ? DeepSeekServiceSnapshot.CreateUnknown()
                : snapshot.Clone();
            running.RequestRunning = true;
            snapshot = running;
        }

        InvokeSnapshotChanged(onSnapshotChanged);
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            DeepSeekServiceSnapshot next;
            try
            {
                next = serviceReaderOverride == null
                    ? ReadServiceStatus()
                    : serviceReaderOverride();
                if (next == null)
                {
                    next = BuildErrorSnapshot("EMPTY", "未返回状态", false);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                next = BuildErrorSnapshot("ERROR", "请求失败", false);
            }

            stopwatch.Stop();
            next.RequestRunning = false;
            bool healthy = next.Known && next.IsAvailable;
            string healthLabel = GetHealthLabel(next);
            DateTime nextDueUtc = DateTime.UtcNow.AddSeconds(GetRefreshSeconds(healthy));
            List<Action> callbacks;
            List<string> consumers;
            lock (SyncRoot)
            {
                snapshot = next;
                requestRunning = false;
                nextRefreshUtc = nextDueUtc;
                consumers = new List<string>(JoinedCallbacks.Keys);
                callbacks = new List<Action>(JoinedCallbacks.Values);
                JoinedCallbacks.Clear();
                refreshTrigger = healthy ? "定时间隔" : "异常状态重试";
            }

            NetworkCheckHistoryLogger.LogCompleted(
                "deepseek_service",
                "deepseek_service",
                effectiveTrigger,
                healthLabel,
                healthy,
                (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                new Dictionary<string, object>
                {
                    { "health", healthLabel },
                    { "service_known", next.Known },
                    { "service_available", next.IsAvailable },
                    { "error_code", next.ErrorCode ?? string.Empty },
                    { "joined_consumers", string.Join(",", consumers.ToArray()) }
                });

            InvokeSnapshotChanged(callbacks);
        });
        return true;
    }

    internal static void RunSelfTest()
    {
        Func<DeepSeekServiceSnapshot> previousReader = serviceReaderOverride;
        try
        {
            AssertReachableStatus(200);
            AssertReachableStatus(204);
            AssertReachableStatus(400);
            AssertReachableStatus(401);
            AssertReachableStatus(402);
            AssertReachableStatus(422);
            AssertUnhealthyStatus(403);
            AssertUnhealthyStatus(429);
            AssertUnhealthyStatus(500);
            if (!string.Equals(GetHealthLabel(BuildSnapshotFromHttpStatus(401)), "Normal", StringComparison.Ordinal) ||
                !string.Equals(GetHealthLabel(BuildSnapshotFromHttpStatus(503)), "Unavailable", StringComparison.Ordinal) ||
                !string.Equals(GetHealthLabel(BuildErrorSnapshot("NET", "无法连接", false)), "Unreachable", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DeepSeek service self-test: health log classification failed.");
            }
            if (GetRefreshSeconds(true) != NormalRefreshSeconds ||
                GetRefreshSeconds(false) != ErrorRefreshSeconds)
            {
                throw new InvalidOperationException("DeepSeek service self-test: refresh cadence mapping failed.");
            }

            HttpWebRequest request = CreateServiceRequest();
            try
            {
                if (!string.Equals(request.RequestUri.AbsoluteUri, ProbeUrl, StringComparison.Ordinal) ||
                    !string.IsNullOrWhiteSpace(request.Headers[HttpRequestHeader.Authorization]))
                {
                    throw new InvalidOperationException("DeepSeek service self-test: probe URL or no-authorization contract failed.");
                }
            }
            finally
            {
                request.Abort();
            }

            int calls = 0;
            int completedCallbacks = 0;
            ManualResetEventSlim done = new ManualResetEventSlim(false);
            serviceReaderOverride = delegate
            {
                Interlocked.Increment(ref calls);
                Thread.Sleep(80);
                return BuildSnapshotFromHttpStatus(401);
            };
            ResetForTest();
            Action callback = delegate
            {
                if (!GetSnapshot().RequestRunning &&
                    Interlocked.Increment(ref completedCallbacks) >= 2)
                {
                    done.Set();
                }
            };
            if (!RefreshIfNeeded("codex_radar", "self_test", callback) ||
                !RefreshIfNeeded("codex_iq", "self_test", callback))
            {
                throw new InvalidOperationException("DeepSeek service self-test: consumers did not start/join.");
            }

            if (!done.Wait(5000) || calls != 1 || completedCallbacks != 2)
            {
                throw new InvalidOperationException("DeepSeek service self-test: single-flight callback fan-out failed.");
            }

            DeepSeekServiceSnapshot cloned = GetSnapshot();
            cloned.IsAvailable = false;
            if (!GetSnapshot().IsAvailable)
            {
                throw new InvalidOperationException("DeepSeek service self-test: snapshot clone was mutable.");
            }

            DeepSeekServiceSnapshot networkError = BuildErrorSnapshot("NET", "无法连接", false);
            if (networkError.Known || networkError.IsAvailable ||
                !string.Equals(networkError.ErrorCode, "NET", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DeepSeek service self-test: no-response network mapping failed.");
            }

            Console.WriteLine("DeepSeek service monitor: PASS no-auth + status mapping + single-flight");
        }
        finally
        {
            serviceReaderOverride = previousReader;
            ResetForTest();
        }
    }

    private static DeepSeekServiceSnapshot ReadServiceStatus()
    {
        HttpWebRequest request = CreateServiceRequest();
        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                return BuildSnapshotFromHttpStatus((int)response.StatusCode);
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                try
                {
                    return BuildSnapshotFromHttpStatus((int)response.StatusCode);
                }
                finally
                {
                    response.Close();
                }
            }

            return BuildErrorSnapshot("NET", "无法连接", false);
        }
    }

    private static HttpWebRequest CreateServiceRequest()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ProbeUrl);
        request.Method = "GET";
        request.Accept = "application/json";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = TimeoutMs;
        request.ReadWriteTimeout = TimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        // An expected 401 from this authenticated endpoint proves the public API gateway is
        // reachable. Never add an Authorization header or consult a local/environment secret.
        return request;
    }

    private static DeepSeekServiceSnapshot BuildSnapshotFromHttpStatus(int statusCode)
    {
        if (IsReachableNormalStatus(statusCode))
        {
            return BuildNormalSnapshot();
        }

        return BuildErrorSnapshot(
            statusCode.ToString(CultureInfo.InvariantCulture),
            GetHttpErrorReason(statusCode),
            true);
    }

    private static bool IsReachableNormalStatus(int statusCode)
    {
        return (statusCode >= 200 && statusCode <= 299) ||
            statusCode == 400 ||
            statusCode == 401 ||
            statusCode == 402 ||
            statusCode == 422;
    }

    private static DeepSeekServiceSnapshot BuildNormalSnapshot()
    {
        DateTime nowUtc = DateTime.UtcNow;
        return new DeepSeekServiceSnapshot
        {
            Known = true,
            IsAvailable = true,
            RequestRunning = false,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            CheckedAtUtc = nowUtc,
            CheckedAtLocal = DateTime.Now
        };
    }

    private static DeepSeekServiceSnapshot BuildErrorSnapshot(
        string errorCode,
        string errorMessage,
        bool responseReceived)
    {
        DateTime nowUtc = DateTime.UtcNow;
        return new DeepSeekServiceSnapshot
        {
            Known = responseReceived,
            IsAvailable = false,
            RequestRunning = false,
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "ERROR" : errorCode.Trim(),
            ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "请求失败" : errorMessage.Trim(),
            CheckedAtUtc = nowUtc,
            CheckedAtLocal = DateTime.Now
        };
    }

    private static string GetHttpErrorReason(int statusCode)
    {
        if (statusCode == 403)
        {
            return "访问受限";
        }

        if (statusCode == 429)
        {
            return "限流";
        }

        if (statusCode >= 500)
        {
            return "服务异常";
        }

        return "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetHealthLabel(DeepSeekServiceSnapshot value)
    {
        return value != null && value.Known && value.IsAvailable
            ? "Normal"
            : (value != null && value.Known ? "Unavailable" : "Unreachable");
    }

    private static int GetRefreshSeconds(bool healthy)
    {
        return healthy ? NormalRefreshSeconds : ErrorRefreshSeconds;
    }

    private static void InvokeSnapshotChanged(Action callback)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static void InvokeSnapshotChanged(List<Action> callbacks)
    {
        if (callbacks == null)
        {
            return;
        }

        for (int i = 0; i < callbacks.Count; i++)
        {
            InvokeSnapshotChanged(callbacks[i]);
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

    private static void ResetForTest()
    {
        lock (SyncRoot)
        {
            snapshot = DeepSeekServiceSnapshot.CreateUnknown();
            requestRunning = false;
            nextRefreshUtc = DateTime.MinValue;
            refreshTrigger = "首次刷新";
            JoinedCallbacks.Clear();
        }
    }

    private static void AssertReachableStatus(int statusCode)
    {
        DeepSeekServiceSnapshot result = BuildSnapshotFromHttpStatus(statusCode);
        if (!result.Known || !result.IsAvailable || !string.IsNullOrEmpty(result.ErrorCode))
        {
            throw new InvalidOperationException(
                "DeepSeek service self-test: reachable HTTP status mapping failed for " +
                statusCode.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    private static void AssertUnhealthyStatus(int statusCode)
    {
        DeepSeekServiceSnapshot result = BuildSnapshotFromHttpStatus(statusCode);
        if (!result.Known || result.IsAvailable ||
            !string.Equals(result.ErrorCode, statusCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DeepSeek service self-test: unhealthy HTTP status mapping failed for " +
                statusCode.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}
