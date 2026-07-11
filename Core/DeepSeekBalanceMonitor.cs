using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class DeepSeekBalanceSnapshot
{
    public bool ApiKeyConfigured { get; set; }
    public bool Known { get; set; }
    public bool IsAvailable { get; set; }
    public string Currency { get; set; }
    public double BalanceCny { get; set; }
    public bool Last24HourUsageKnown { get; set; }
    public double Last24HourUsageCny { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public bool RequestRunning { get; set; }
    public bool ServiceKnown { get; set; }
    public bool ServiceIsAvailable { get; set; }
    public string ServiceErrorCode { get; set; }
    public string ServiceErrorMessage { get; set; }
    public bool ServiceRequestRunning { get; set; }
    public DateTime ServiceCheckedAtUtc { get; set; }
    public DateTime ServiceCheckedAtLocal { get; set; }

    public static DeepSeekBalanceSnapshot CreateDefault()
    {
        return new DeepSeekBalanceSnapshot
        {
            ApiKeyConfigured = false,
            Known = false,
            IsAvailable = false,
            Currency = "CNY",
            BalanceCny = 0.0,
            Last24HourUsageKnown = false,
            Last24HourUsageCny = 0.0,
            CheckedAtUtc = DateTime.MinValue,
            CheckedAtLocal = DateTime.MinValue,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            RequestRunning = false,
            ServiceKnown = false,
            ServiceIsAvailable = false,
            ServiceErrorCode = string.Empty,
            ServiceErrorMessage = string.Empty,
            ServiceRequestRunning = false,
            ServiceCheckedAtUtc = DateTime.MinValue,
            ServiceCheckedAtLocal = DateTime.MinValue
        };
    }

    public DeepSeekBalanceSnapshot Clone()
    {
        return new DeepSeekBalanceSnapshot
        {
            ApiKeyConfigured = this.ApiKeyConfigured,
            Known = this.Known,
            IsAvailable = this.IsAvailable,
            Currency = this.Currency,
            BalanceCny = this.BalanceCny,
            Last24HourUsageKnown = this.Last24HourUsageKnown,
            Last24HourUsageCny = this.Last24HourUsageCny,
            CheckedAtUtc = this.CheckedAtUtc,
            CheckedAtLocal = this.CheckedAtLocal,
            ErrorCode = this.ErrorCode,
            ErrorMessage = this.ErrorMessage,
            RequestRunning = this.RequestRunning,
            ServiceKnown = this.ServiceKnown,
            ServiceIsAvailable = this.ServiceIsAvailable,
            ServiceErrorCode = this.ServiceErrorCode,
            ServiceErrorMessage = this.ServiceErrorMessage,
            ServiceRequestRunning = this.ServiceRequestRunning,
            ServiceCheckedAtUtc = this.ServiceCheckedAtUtc,
            ServiceCheckedAtLocal = this.ServiceCheckedAtLocal
        };
    }
}

internal static class DeepSeekBalanceMonitor
{
    internal const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    private const string ApiKeyFileName = "deepseek-api-key.bin";
    private const string LegacyApiKeyFileName = "deepseek-api-key.txt";
    private const int TimeoutMs = 10000;
    private const int NormalRefreshSeconds = 60;
    private const int ErrorRefreshSeconds = 300;
    private const int HistoryRetentionHours = 48;

    private static readonly object syncRoot = new object();
    // DeepSeek balance is shared by every Claude view. The monitor owns the key
    // lookup, single-flight request, 48h history file and callbacks so two
    // windows cannot race the API or append duplicate history samples.
    private static readonly List<DeepSeekBalancePoint> history = new List<DeepSeekBalancePoint>();
    private static readonly Dictionary<string, Action> joinedCallbacks = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
    private static DeepSeekBalanceSnapshot snapshot = DeepSeekBalanceSnapshot.CreateDefault();
    private static bool requestRunning;
    private static bool historyLoaded;
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    private static string refreshTrigger = "首次刷新";
    private static Func<string> apiKeyProviderOverride;
    private static Func<string, DeepSeekBalanceSnapshot> balanceReaderOverride;
    private static Func<DeepSeekBalanceSnapshot> publicApiStatusReaderOverride;
    private static string historyPathOverride;

    internal static string ApiKeyPath
    {
        get { return Path.Combine(Logger.DirectoryPath, ApiKeyFileName); }
    }

    internal static string LegacyApiKeyPath
    {
        get { return Path.Combine(Logger.DirectoryPath, LegacyApiKeyFileName); }
    }

    internal static string HistoryPath
    {
        get
        {
            return string.IsNullOrWhiteSpace(historyPathOverride)
                ? Path.Combine(Logger.DirectoryPath, "deepseek-balance-history.jsonl")
                : historyPathOverride;
        }
    }

    internal static void RequestRefresh()
    {
        RequestRefresh("强制刷新");
    }

    internal static void RequestRefresh(string trigger)
    {
        lock (syncRoot)
        {
            nextRefreshUtc = DateTime.UtcNow;
            refreshTrigger = string.IsNullOrWhiteSpace(trigger) ? "强制刷新" : trigger.Trim();
        }
    }

    internal static DeepSeekBalanceSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return snapshot == null ? DeepSeekBalanceSnapshot.CreateDefault() : snapshot.Clone();
        }
    }

    internal static void SetSnapshotForTest(DeepSeekBalanceSnapshot testSnapshot)
    {
        lock (syncRoot)
        {
            snapshot = testSnapshot == null ? DeepSeekBalanceSnapshot.CreateDefault() : testSnapshot.Clone();
            snapshot.RequestRunning = false;
            snapshot.ServiceRequestRunning = false;
            requestRunning = false;
            nextRefreshUtc = DateTime.UtcNow.AddHours(1.0);
        }
    }

    internal static bool RefreshIfNeeded(Action onSnapshotChanged)
    {
        return RefreshIfNeeded("unknown", "定时间隔", onSnapshotChanged);
    }

    internal static bool RefreshIfNeeded(string consumerId, string trigger, Action onSnapshotChanged)
    {
        DateTime nowUtc = DateTime.UtcNow;
        string apiKey = GetApiKey();
        bool apiKeyConfigured = !string.IsNullOrWhiteSpace(apiKey);
        string effectiveTrigger = string.IsNullOrWhiteSpace(trigger) ? "定时间隔" : trigger.Trim();

        lock (syncRoot)
        {
            string consumer = NormalizeConsumerId(consumerId);
            if (requestRunning ||
                (nextRefreshUtc != DateTime.MinValue && nowUtc < nextRefreshUtc))
            {
                if (requestRunning && !joinedCallbacks.ContainsKey(consumer))
                {
                    joinedCallbacks[consumer] = onSnapshotChanged;
                    return true;
                }

                return false;
            }

            effectiveTrigger = !string.IsNullOrWhiteSpace(refreshTrigger) &&
                !string.Equals(refreshTrigger, "定时间隔", StringComparison.Ordinal)
                    ? refreshTrigger
                    : (string.IsNullOrWhiteSpace(trigger) ? "定时间隔" : trigger.Trim());
            refreshTrigger = "定时间隔";
            requestRunning = true;
            joinedCallbacks.Clear();
            joinedCallbacks[consumer] = onSnapshotChanged;
            DeepSeekBalanceSnapshot running = snapshot == null
                ? DeepSeekBalanceSnapshot.CreateDefault()
                : snapshot.Clone();
            running.ApiKeyConfigured = apiKeyConfigured;
            running.RequestRunning = apiKeyConfigured;
            running.ServiceRequestRunning = true;
            snapshot = running;
        }

        InvokeSnapshotChanged(onSnapshotChanged);
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            DeepSeekBalanceSnapshot next;
            try
            {
                if (apiKeyConfigured)
                {
                    next = balanceReaderOverride == null
                        ? ReadBalance(apiKey)
                        : balanceReaderOverride(apiKey);
                    EnsureServiceStatusInitialized(next, true);
                    ApplyHistory(next);
                }
                else
                {
                    next = publicApiStatusReaderOverride == null
                        ? ReadPublicApiStatus()
                        : publicApiStatusReaderOverride();
                    EnsureNoKeyBalanceState(next);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                next = BuildErrorSnapshot("ERROR", "请求失败");
                ApplyServiceError(next, "ERROR", "请求失败", false);
            }

            stopwatch.Stop();
            List<Action> callbacks;
            List<string> consumers;
            bool serviceHealthy = next.ServiceKnown && next.ServiceIsAvailable;
            bool balanceDoneOrNotNeeded = !next.ApiKeyConfigured || next.Known;
            DateTime nextDueUtc = DateTime.UtcNow.AddSeconds(serviceHealthy && balanceDoneOrNotNeeded ? NormalRefreshSeconds : ErrorRefreshSeconds);
            lock (syncRoot)
            {
                next.RequestRunning = false;
                next.ServiceRequestRunning = false;
                snapshot = next;
                requestRunning = false;
                nextRefreshUtc = nextDueUtc;
                consumers = new List<string>(joinedCallbacks.Keys);
                callbacks = new List<Action>(joinedCallbacks.Values);
                joinedCallbacks.Clear();
                refreshTrigger = serviceHealthy && balanceDoneOrNotNeeded ? "定时间隔" : "异常状态重试";
            }

            NetworkCheckHistoryLogger.LogCompleted(
                "deepseek_balance",
                "deepseek_balance",
                effectiveTrigger,
                serviceHealthy ? "Normal" : (next.ServiceErrorCode ?? string.Empty),
                serviceHealthy,
                (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                new Dictionary<string, object>
                {
                    { "health", serviceHealthy ? "Normal" : "Unknown" },
                    { "service_known", next.ServiceKnown },
                    { "service_error_code", next.ServiceErrorCode ?? string.Empty },
                    { "balance_known", next.Known },
                    { "balance_error_code", next.ErrorCode ?? string.Empty },
                    { "api_key_configured", next.ApiKeyConfigured },
                    { "joined_consumers", string.Join(",", consumers.ToArray()) }
                });

            InvokeSnapshotChanged(callbacks);
        });
        return true;
    }

    internal static void RunSelfTest()
    {
        Func<string> previousKeyProvider = apiKeyProviderOverride;
        Func<string, DeepSeekBalanceSnapshot> previousReader = balanceReaderOverride;
        Func<DeepSeekBalanceSnapshot> previousPublicApiReader = publicApiStatusReaderOverride;
        string previousHistoryPath = historyPathOverride;
        string testDir = Path.Combine(Path.GetTempPath(), ProductIdentity.MachineName + "-DeepSeekBalanceMonitor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testDir);
            historyPathOverride = Path.Combine(testDir, "deepseek-balance-history.jsonl");
            apiKeyProviderOverride = delegate { return string.Empty; };
            balanceReaderOverride = null;
            publicApiStatusReaderOverride = delegate
            {
                DeepSeekBalanceSnapshot result = DeepSeekBalanceSnapshot.CreateDefault();
                EnsureNoKeyBalanceState(result);
                ApplyServiceHttpStatus(result, 401);
                return result;
            };
            ResetForTest();
            if (!RefreshIfNeeded("test", "no_key", null))
            {
                throw new InvalidOperationException("DeepSeek balance self-test: no-key request did not start.");
            }

            SpinWait.SpinUntil(delegate { return !GetSnapshot().ServiceRequestRunning; }, 5000);
            DeepSeekBalanceSnapshot noKey = GetSnapshot();
            if (noKey.ApiKeyConfigured ||
                !string.Equals(noKey.ErrorCode, "NO_KEY", StringComparison.Ordinal) ||
                !noKey.ServiceKnown ||
                !noKey.ServiceIsAvailable)
            {
                throw new InvalidOperationException("DeepSeek balance self-test: no-key public API state failed.");
            }

            if (!string.Equals(GetHttpErrorReason(401), "鉴权失败", StringComparison.Ordinal) ||
                !string.Equals(GetHttpErrorReason(402), "余额不足", StringComparison.Ordinal) ||
                !string.Equals(GetHttpErrorReason(429), "限流", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DeepSeek balance self-test: HTTP error mapping failed.");
            }

            int calls = 0;
            int callbacks = 0;
            ManualResetEventSlim done = new ManualResetEventSlim(false);
            apiKeyProviderOverride = delegate { return "test-key"; };
            balanceReaderOverride = delegate(string key)
            {
                Interlocked.Increment(ref calls);
                Thread.Sleep(80);
                return new DeepSeekBalanceSnapshot
                {
                    ApiKeyConfigured = true,
                    Known = true,
                    IsAvailable = true,
                    Currency = "CNY",
                    BalanceCny = 88.5,
                    CheckedAtUtc = DateTime.UtcNow,
                    CheckedAtLocal = DateTime.Now
                };
            };
            ResetForTest();
            Action callback = delegate
            {
                if (Interlocked.Increment(ref callbacks) >= 2)
                {
                    done.Set();
                }
            };
            if (!RefreshIfNeeded("codex_radar", "self_test", callback) ||
                !RefreshIfNeeded("claude_radar", "self_test", callback))
            {
                throw new InvalidOperationException("DeepSeek balance self-test: consumers did not start/join.");
            }

            if (!done.Wait(5000) || calls != 1)
            {
                throw new InvalidOperationException("DeepSeek balance self-test: joined request did not fan out.");
            }

            DeepSeekBalanceSnapshot cloned = GetSnapshot();
            cloned.BalanceCny = 0.0;
            if (Math.Abs(GetSnapshot().BalanceCny - 88.5) > 0.01)
            {
                throw new InvalidOperationException("DeepSeek balance self-test: snapshot clone was mutable.");
            }

            DateTime now = DateTime.UtcNow;
            File.WriteAllText(
                historyPathOverride,
                "{\"schema_version\":1,\"timestamp_utc\":\"" + now.AddHours(-49).ToString("O", CultureInfo.InvariantCulture) + "\",\"balance_cny\":100}\n" +
                "{\"schema_version\":1,\"timestamp_utc\":\"" + now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture) + "\",\"balance_cny\":90}\n",
                SharedEncoding.Utf8NoBom);
            ResetForTest();
            balanceReaderOverride = delegate(string key)
            {
                return new DeepSeekBalanceSnapshot
                {
                    ApiKeyConfigured = true,
                    Known = true,
                    IsAvailable = true,
                    Currency = "CNY",
                    BalanceCny = 80,
                    CheckedAtUtc = now,
                    CheckedAtLocal = now.ToLocalTime()
                };
            };
            RequestRefresh("history");
            RefreshIfNeeded("history", "history", null);
            SpinWait.SpinUntil(delegate { return !GetSnapshot().RequestRunning; }, 5000);
            string[] lines = File.ReadAllLines(historyPathOverride, Encoding.UTF8);
            if (lines.Length != 2 || !GetSnapshot().Last24HourUsageKnown)
            {
                throw new InvalidOperationException("DeepSeek balance self-test: history retention/usage failed.");
            }
        }
        finally
        {
            apiKeyProviderOverride = previousKeyProvider;
            balanceReaderOverride = previousReader;
            publicApiStatusReaderOverride = previousPublicApiReader;
            historyPathOverride = previousHistoryPath;
            ResetForTest();
            try
            {
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

    internal static string FormatDisplayText(DeepSeekBalanceSnapshot displaySnapshot)
    {
        if (displaySnapshot == null || !displaySnapshot.ApiKeyConfigured)
        {
            return "DS:--";
        }

        if (!displaySnapshot.Known)
        {
            return displaySnapshot.RequestRunning ? "DS:..." : "DS:--";
        }

        double rounded = Math.Round(Math.Max(0.0, displaySnapshot.BalanceCny), 0, MidpointRounding.AwayFromZero);
        return "DS:" + rounded.ToString("0", CultureInfo.InvariantCulture);
    }

    internal static string BuildCacheSignature(DeepSeekBalanceSnapshot displaySnapshot)
    {
        if (displaySnapshot == null)
        {
            return "ds:null";
        }

        return string.Join(
            ",",
            new string[]
            {
                displaySnapshot.ApiKeyConfigured ? "1" : "0",
                displaySnapshot.Known ? "1" : "0",
                displaySnapshot.IsAvailable ? "1" : "0",
                displaySnapshot.RequestRunning ? "1" : "0",
                displaySnapshot.ServiceKnown ? "1" : "0",
                displaySnapshot.ServiceIsAvailable ? "1" : "0",
                displaySnapshot.ServiceRequestRunning ? "1" : "0",
                displaySnapshot.BalanceCny.ToString("0.###", CultureInfo.InvariantCulture),
                displaySnapshot.Last24HourUsageCny.ToString("0.###", CultureInfo.InvariantCulture),
                displaySnapshot.ErrorCode ?? string.Empty,
                displaySnapshot.ServiceErrorCode ?? string.Empty
            });
    }

    internal static ColorlessDeepSeekAlert BuildAlert()
    {
        DeepSeekBalanceSnapshot current = GetSnapshot();
        if (current == null)
        {
            return null;
        }

        if (current.ServiceRequestRunning && !current.ServiceKnown)
        {
            return new ColorlessDeepSeekAlert("deepseek:checking", "DeepSeek", "检测中", current);
        }

        if (current.ServiceKnown && current.ServiceIsAvailable)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(current.ServiceErrorCode))
        {
            string errorCode = current.ServiceErrorCode.Trim();
            return new ColorlessDeepSeekAlert(
                "deepseek:" + errorCode,
                "DeepSeek",
                string.IsNullOrWhiteSpace(current.ServiceErrorMessage) ? "请求失败" : current.ServiceErrorMessage,
                current);
        }

        return null;
    }

    internal static string GetApiKey()
    {
        if (apiKeyProviderOverride != null)
        {
            return apiKeyProviderOverride() ?? string.Empty;
        }

        string key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }

        key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }

        key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }

        try
        {
            string secret;
            bool migrated;
            string errorCode;
            return SecretStore.TryReadOrMigrateSecret(
                ApiKeyPath,
                LegacyApiKeyPath,
                SecretStore.TrimSecret,
                out secret,
                out migrated,
                out errorCode)
                ? secret
                : string.Empty;
        }
        catch
        {
        }

        return string.Empty;
    }

    private static void InvokeSnapshotChanged(Action onSnapshotChanged)
    {
        if (onSnapshotChanged == null)
        {
            return;
        }

        try
        {
            onSnapshotChanged();
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

    private static void ResetForTest()
    {
        ResetForTest(true);
    }

    private static void ResetForTest(bool clearHistoryFileState)
    {
        lock (syncRoot)
        {
            snapshot = DeepSeekBalanceSnapshot.CreateDefault();
            requestRunning = false;
            nextRefreshUtc = DateTime.MinValue;
            refreshTrigger = "首次刷新";
            joinedCallbacks.Clear();
            if (clearHistoryFileState)
            {
                historyLoaded = false;
                history.Clear();
            }
        }
    }

    private static DeepSeekBalanceSnapshot ReadPublicApiStatus()
    {
        HttpWebRequest request = CreateDeepSeekApiRequest(BalanceUrl);
        try
        {
            using ((HttpWebResponse)request.GetResponse())
            {
                DeepSeekBalanceSnapshot result = DeepSeekBalanceSnapshot.CreateDefault();
                EnsureNoKeyBalanceState(result);
                ApplyServiceHttpStatus(result, 200);
                return result;
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            DeepSeekBalanceSnapshot result = DeepSeekBalanceSnapshot.CreateDefault();
            EnsureNoKeyBalanceState(result);
            if (response != null)
            {
                ApplyServiceHttpStatus(result, (int)response.StatusCode);
                response.Close();
                return result;
            }

            ApplyServiceError(result, "NET", "无法连接", false);
            return result;
        }
    }

    private static DeepSeekBalanceSnapshot ReadBalance(string apiKey)
    {
        HttpWebRequest request = CreateDeepSeekApiRequest(BalanceUrl);
        request.Headers["Authorization"] = "Bearer " + apiKey;

        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string content = reader.ReadToEnd();
                DeepSeekBalanceSnapshot parsed;
                if (TryParseResponse(content, out parsed))
                {
                    ApplyServiceHttpStatus(parsed, (int)response.StatusCode);
                    return parsed;
                }

                DeepSeekBalanceSnapshot parseError = BuildErrorSnapshot("PARSE", "解析失败");
                ApplyServiceError(parseError, "PARSE", "元素缺失", true);
                return parseError;
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                int statusCode = (int)response.StatusCode;
                DeepSeekBalanceSnapshot error = BuildErrorSnapshot(
                    statusCode.ToString(CultureInfo.InvariantCulture),
                    GetHttpErrorReason(statusCode));
                ApplyServiceHttpStatus(error, statusCode);
                response.Close();
                return error;
            }

            DeepSeekBalanceSnapshot netError = BuildErrorSnapshot("NET", "无法连接");
            ApplyServiceError(netError, "NET", "无法连接", false);
            return netError;
        }
    }

    private static HttpWebRequest CreateDeepSeekApiRequest(string url)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = TimeoutMs;
        request.ReadWriteTimeout = TimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        return request;
    }

    private static void EnsureNoKeyBalanceState(DeepSeekBalanceSnapshot snapshotToUpdate)
    {
        if (snapshotToUpdate == null)
        {
            return;
        }

        snapshotToUpdate.ApiKeyConfigured = false;
        snapshotToUpdate.Known = false;
        snapshotToUpdate.IsAvailable = false;
        snapshotToUpdate.Currency = "CNY";
        snapshotToUpdate.BalanceCny = 0.0;
        snapshotToUpdate.ErrorCode = "NO_KEY";
        snapshotToUpdate.ErrorMessage = "未配置";
        snapshotToUpdate.RequestRunning = false;
    }

    private static void EnsureServiceStatusInitialized(DeepSeekBalanceSnapshot snapshotToUpdate, bool apiKeyConfigured)
    {
        if (snapshotToUpdate == null)
        {
            return;
        }

        snapshotToUpdate.ApiKeyConfigured = apiKeyConfigured;
        if (!snapshotToUpdate.ServiceKnown && string.IsNullOrWhiteSpace(snapshotToUpdate.ServiceErrorCode))
        {
            ApplyServiceHttpStatus(snapshotToUpdate, snapshotToUpdate.Known ? 200 : 500);
        }
    }

    private static void ApplyServiceHttpStatus(DeepSeekBalanceSnapshot snapshotToUpdate, int statusCode)
    {
        if (snapshotToUpdate == null)
        {
            return;
        }

        if (IsDeepSeekApiReachableStatus(statusCode))
        {
            ApplyServiceNormal(snapshotToUpdate);
            return;
        }

        string code = statusCode.ToString(CultureInfo.InvariantCulture);
        ApplyServiceError(snapshotToUpdate, code, GetHttpErrorReason(statusCode), true);
    }

    private static bool IsDeepSeekApiReachableStatus(int statusCode)
    {
        // 401/402/422 are account/request-level results from the API gateway. They prove the
        // public API is reachable, so they must not be shown as DeepSeek service failures.
        return statusCode == 200 ||
            statusCode == 400 ||
            statusCode == 401 ||
            statusCode == 402 ||
            statusCode == 422;
    }

    private static void ApplyServiceNormal(DeepSeekBalanceSnapshot snapshotToUpdate)
    {
        DateTime nowUtc = DateTime.UtcNow;
        snapshotToUpdate.ServiceKnown = true;
        snapshotToUpdate.ServiceIsAvailable = true;
        snapshotToUpdate.ServiceErrorCode = string.Empty;
        snapshotToUpdate.ServiceErrorMessage = string.Empty;
        snapshotToUpdate.ServiceRequestRunning = false;
        snapshotToUpdate.ServiceCheckedAtUtc = nowUtc;
        snapshotToUpdate.ServiceCheckedAtLocal = DateTime.Now;
    }

    private static void ApplyServiceError(
        DeepSeekBalanceSnapshot snapshotToUpdate,
        string errorCode,
        string message,
        bool responseReceived)
    {
        DateTime nowUtc = DateTime.UtcNow;
        snapshotToUpdate.ServiceKnown = responseReceived;
        snapshotToUpdate.ServiceIsAvailable = false;
        snapshotToUpdate.ServiceErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "ERROR" : errorCode.Trim();
        snapshotToUpdate.ServiceErrorMessage = string.IsNullOrWhiteSpace(message) ? "请求失败" : message.Trim();
        snapshotToUpdate.ServiceRequestRunning = false;
        snapshotToUpdate.ServiceCheckedAtUtc = nowUtc;
        snapshotToUpdate.ServiceCheckedAtLocal = DateTime.Now;
    }

    private static bool TryParseResponse(string content, out DeepSeekBalanceSnapshot parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        Dictionary<string, object> root;
        try
        {
            root = new JavaScriptSerializer().DeserializeObject(content) as Dictionary<string, object>;
        }
        catch
        {
            return false;
        }

        if (root == null)
        {
            return false;
        }

        object isAvailableObj;
        bool isAvailable = true;
        if (root.TryGetValue("is_available", out isAvailableObj) && isAvailableObj is bool)
        {
            isAvailable = (bool)isAvailableObj;
        }

        object balanceInfosObj;
        if (!root.TryGetValue("balance_infos", out balanceInfosObj))
        {
            return false;
        }

        object[] balanceInfos = balanceInfosObj as object[];
        if (balanceInfos == null)
        {
            return false;
        }

        double balanceCny = 0.0;
        string currency = "CNY";
        bool found = false;
        for (int i = 0; i < balanceInfos.Length; i++)
        {
            Dictionary<string, object> info = balanceInfos[i] as Dictionary<string, object>;
            if (info == null)
            {
                continue;
            }

            object currencyObj;
            string candidateCurrency = info.TryGetValue("currency", out currencyObj)
                ? Convert.ToString(currencyObj, CultureInfo.InvariantCulture)
                : string.Empty;
            if (!string.Equals(candidateCurrency, "CNY", StringComparison.OrdinalIgnoreCase) && found)
            {
                continue;
            }

            object balanceObj;
            double parsedBalance;
            if (info.TryGetValue("total_balance", out balanceObj) &&
                TryParseInvariantDouble(Convert.ToString(balanceObj, CultureInfo.InvariantCulture), out parsedBalance))
            {
                balanceCny = parsedBalance;
                currency = string.IsNullOrWhiteSpace(candidateCurrency) ? "CNY" : candidateCurrency.ToUpperInvariant();
                found = string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase);
                if (found)
                {
                    break;
                }
            }
        }

        if (!found && balanceCny <= 0.0)
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        parsed = new DeepSeekBalanceSnapshot
        {
            ApiKeyConfigured = true,
            Known = true,
            IsAvailable = isAvailable,
            Currency = currency,
            BalanceCny = Math.Max(0.0, balanceCny),
            Last24HourUsageKnown = false,
            Last24HourUsageCny = 0.0,
            CheckedAtUtc = nowUtc,
            CheckedAtLocal = DateTime.Now,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            RequestRunning = false
        };
        return true;
    }

    private static bool TryParseInvariantDouble(string value, out double number)
    {
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static DeepSeekBalanceSnapshot BuildErrorSnapshot(string errorCode, string message)
    {
        return new DeepSeekBalanceSnapshot
        {
            ApiKeyConfigured = true,
            Known = false,
            IsAvailable = false,
            Currency = "CNY",
            BalanceCny = 0.0,
            Last24HourUsageKnown = false,
            Last24HourUsageCny = 0.0,
            CheckedAtUtc = DateTime.UtcNow,
            CheckedAtLocal = DateTime.Now,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = string.IsNullOrWhiteSpace(message) ? "请求失败" : message,
            RequestRunning = false
        };
    }

    private static string GetHttpErrorReason(int statusCode)
    {
        if (statusCode == 401)
        {
            return "鉴权失败";
        }

        if (statusCode == 402)
        {
            return "余额不足";
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

    private static void ApplyHistory(DeepSeekBalanceSnapshot next)
    {
        if (next == null || !next.Known)
        {
            return;
        }

        lock (syncRoot)
        {
            EnsureHistoryLoadedLocked();
            DateTime nowUtc = next.CheckedAtUtc == DateTime.MinValue ? DateTime.UtcNow : next.CheckedAtUtc;
            history.Add(new DeepSeekBalancePoint
            {
                TimestampUtc = nowUtc,
                BalanceCny = next.BalanceCny
            });

            TrimHistoryLocked(nowUtc);
            next.Last24HourUsageCny = CalculateLast24HourUsageLocked(nowUtc);
            next.Last24HourUsageKnown = true;
            SaveHistoryLocked();
        }
    }

    private static void EnsureHistoryLoadedLocked()
    {
        if (historyLoaded)
        {
            return;
        }

        historyLoaded = true;
        history.Clear();
        string path = HistoryPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                Dictionary<string, object> row;
                try
                {
                    row = new JavaScriptSerializer().DeserializeObject(lines[i]) as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }

                if (row == null)
                {
                    continue;
                }

                object timestampObj;
                object balanceObj;
                DateTime timestampUtc;
                double balance;
                if (row.TryGetValue("timestamp_utc", out timestampObj) &&
                    row.TryGetValue("balance_cny", out balanceObj) &&
                    DateTime.TryParse(
                        Convert.ToString(timestampObj, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out timestampUtc) &&
                    TryParseInvariantDouble(Convert.ToString(balanceObj, CultureInfo.InvariantCulture), out balance))
                {
                    history.Add(new DeepSeekBalancePoint
                    {
                        TimestampUtc = timestampUtc,
                        BalanceCny = Math.Max(0.0, balance)
                    });
                }
            }
        }
        catch
        {
        }

        history.Sort(delegate(DeepSeekBalancePoint left, DeepSeekBalancePoint right)
        {
            return left.TimestampUtc.CompareTo(right.TimestampUtc);
        });
        TrimHistoryLocked(DateTime.UtcNow);
    }

    private static void TrimHistoryLocked(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-HistoryRetentionHours);
        history.RemoveAll(delegate(DeepSeekBalancePoint point)
        {
            return point == null || point.TimestampUtc < cutoffUtc;
        });
    }

    private static double CalculateLast24HourUsageLocked(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-24.0);
        double used = 0.0;
        DeepSeekBalancePoint previous = null;
        for (int i = 0; i < history.Count; i++)
        {
            DeepSeekBalancePoint point = history[i];
            if (point == null || point.TimestampUtc < cutoffUtc)
            {
                continue;
            }

            if (previous != null && point.BalanceCny < previous.BalanceCny)
            {
                used += previous.BalanceCny - point.BalanceCny;
            }

            previous = point;
        }

        return Math.Max(0.0, used);
    }

    private static void SaveHistoryLocked()
    {
        try
        {
            Directory.CreateDirectory(Logger.DirectoryPath);
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < history.Count; i++)
            {
                DeepSeekBalancePoint point = history[i];
                if (point == null)
                {
                    continue;
                }

                builder.Append("{\"schema_version\":1,\"timestamp_utc\":\"");
                builder.Append(point.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                builder.Append("\",\"balance_cny\":");
                builder.Append(point.BalanceCny.ToString("0.####", CultureInfo.InvariantCulture));
                builder.AppendLine("}");
            }

            File.WriteAllText(HistoryPath, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private sealed class DeepSeekBalancePoint
    {
        public DateTime TimestampUtc { get; set; }
        public double BalanceCny { get; set; }
    }
}

internal sealed class ColorlessDeepSeekAlert
{
    public ColorlessDeepSeekAlert(string key, string name, string reason, DeepSeekBalanceSnapshot snapshot)
    {
        this.Key = key ?? string.Empty;
        this.Name = name ?? string.Empty;
        this.Reason = reason ?? string.Empty;
        this.Snapshot = snapshot == null ? DeepSeekBalanceSnapshot.CreateDefault() : snapshot.Clone();
    }

    public string Key { get; private set; }
    public string Name { get; private set; }
    public string Reason { get; private set; }
    public DeepSeekBalanceSnapshot Snapshot { get; private set; }
}
