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

internal sealed class DeepSeekBalancePoint
{
    public DateTime TimestampUtc { get; set; }
    public string Currency { get; set; }
    public double Balance { get; set; }

    public DeepSeekBalancePoint Clone()
    {
        return new DeepSeekBalancePoint
        {
            TimestampUtc = this.TimestampUtc,
            Currency = this.Currency ?? "CNY",
            Balance = this.Balance
        };
    }
}

internal sealed class DeepSeekBalanceSnapshot
{
    public bool ApiKeyConfigured { get; set; }
    public bool Known { get; set; }
    public bool IsAvailable { get; set; }
    public string Currency { get; set; }
    public double Balance { get; set; }
    public bool Last24HourUsageKnown { get; set; }
    public double Last24HourUsage { get; set; }
    public double ReferenceBalance { get; set; }
    public bool RunwayKnown { get; set; }
    public double RunwayHours { get; set; }
    public bool RequestRunning { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public List<DeepSeekBalancePoint> History { get; set; }

    public static DeepSeekBalanceSnapshot CreateEmpty()
    {
        return new DeepSeekBalanceSnapshot
        {
            Currency = "CNY",
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            History = new List<DeepSeekBalancePoint>()
        };
    }

    public DeepSeekBalanceSnapshot Clone()
    {
        DeepSeekBalanceSnapshot copy = new DeepSeekBalanceSnapshot
        {
            ApiKeyConfigured = this.ApiKeyConfigured,
            Known = this.Known,
            IsAvailable = this.IsAvailable,
            Currency = this.Currency ?? "CNY",
            Balance = this.Balance,
            Last24HourUsageKnown = this.Last24HourUsageKnown,
            Last24HourUsage = this.Last24HourUsage,
            ReferenceBalance = this.ReferenceBalance,
            RunwayKnown = this.RunwayKnown,
            RunwayHours = this.RunwayHours,
            RequestRunning = this.RequestRunning,
            CheckedAtUtc = this.CheckedAtUtc,
            CheckedAtLocal = this.CheckedAtLocal,
            ErrorCode = this.ErrorCode ?? string.Empty,
            ErrorMessage = this.ErrorMessage ?? string.Empty,
            History = new List<DeepSeekBalancePoint>()
        };
        if (this.History != null)
        {
            for (int i = 0; i < this.History.Count; i++)
            {
                if (this.History[i] != null)
                {
                    copy.History.Add(this.History[i].Clone());
                }
            }
        }

        return copy;
    }
}

// Authenticated balance is deliberately separate from DeepSeekServiceMonitor. A bad or missing
// account key is an account state, not evidence that the public DeepSeek gateway is unavailable.
// This reader owns one process-wide request and a bounded local history; visible tiles only clone
// the published snapshot and never read credentials, disk, or the network.
internal static class DeepSeekBalanceMonitor
{
    internal const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
    internal const string BalanceUrl = "https://api.deepseek.com/user/balance";
    private const string ApiKeyFileName = "deepseek-api-key.bin";
    private const string LegacyApiKeyFileName = "deepseek-api-key.txt";
    private const string HistoryFileName = "deepseek-balance-history.jsonl";
    private const int RequestDeadlineMs = 10000;
    private const int NormalRefreshSeconds = 300;
    private const int ErrorRefreshSeconds = 600;
    private const int NoKeyRefreshSeconds = 900;
    private const int HistoryRetentionHours = 48;
    private const int MaximumHistoryPoints = 1024;
    private const int MaximumPublishedHistoryPoints = 96;

    private static readonly object SyncRoot = new object();
    private static readonly object HistorySyncRoot = new object();
    private static readonly Dictionary<string, Action> JoinedCallbacks =
        new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<DeepSeekBalancePoint> History = new List<DeepSeekBalancePoint>();
    private static DeepSeekBalanceSnapshot snapshot = DeepSeekBalanceSnapshot.CreateEmpty();
    private static bool requestRunning;
    private static bool historyLoaded;
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    private static string refreshTrigger = "首次刷新";

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
        get { return Path.Combine(Logger.DirectoryPath, HistoryFileName); }
    }

    internal static void RequestRefresh(string trigger)
    {
        lock (SyncRoot)
        {
            nextRefreshUtc = DateTime.UtcNow;
            refreshTrigger = string.IsNullOrWhiteSpace(trigger) ? "强制刷新" : trigger.Trim();
        }
    }

    internal static DeepSeekBalanceSnapshot GetSnapshot()
    {
        lock (SyncRoot)
        {
            return snapshot == null ? DeepSeekBalanceSnapshot.CreateEmpty() : snapshot.Clone();
        }
    }

    internal static bool RefreshIfNeeded(string consumerId, string trigger, Action onSnapshotChanged)
    {
        DateTime nowUtc = DateTime.UtcNow;
        DeepSeekBalanceSnapshot cachedAtStart;
        string consumer = string.IsNullOrWhiteSpace(consumerId) ? "unknown" : consumerId.Trim();
        string effectiveTrigger = string.IsNullOrWhiteSpace(trigger) ? "定时间隔" : trigger.Trim();
        lock (SyncRoot)
        {
            if (requestRunning || (nextRefreshUtc != DateTime.MinValue && nowUtc < nextRefreshUtc))
            {
                if (requestRunning && !JoinedCallbacks.ContainsKey(consumer))
                {
                    JoinedCallbacks[consumer] = onSnapshotChanged;
                    return true;
                }

                return false;
            }

            if (!string.IsNullOrWhiteSpace(refreshTrigger) &&
                !string.Equals(refreshTrigger, "定时间隔", StringComparison.Ordinal))
            {
                effectiveTrigger = refreshTrigger;
            }

            refreshTrigger = "定时间隔";
            requestRunning = true;
            JoinedCallbacks.Clear();
            JoinedCallbacks[consumer] = onSnapshotChanged;
            DeepSeekBalanceSnapshot running = snapshot == null
                ? DeepSeekBalanceSnapshot.CreateEmpty()
                : snapshot.Clone();
            running.RequestRunning = true;
            snapshot = running;
            cachedAtStart = running.Clone();
        }

        InvokeCallbacks(new List<Action> { onSnapshotChanged });
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            DeepSeekBalanceSnapshot next;
            string apiKey = ReadConfiguredApiKey();
            try
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    next = BuildNoKeySnapshot();
                }
                else
                {
                    next = ReadBalance(apiKey);
                    if (next.Known)
                    {
                        ApplyHistory(next);
                    }
                    else if (cachedAtStart.Known)
                    {
                        next = PreserveKnownBalanceOnRefreshError(cachedAtStart, next);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                next = BuildErrorSnapshot(true, "ERROR", "请求失败");
            }

            stopwatch.Stop();
            next.RequestRunning = false;
            bool refreshSucceeded = next.Known && string.IsNullOrEmpty(next.ErrorCode);
            int refreshSeconds = !next.ApiKeyConfigured
                ? NoKeyRefreshSeconds
                : (refreshSucceeded ? NormalRefreshSeconds : ErrorRefreshSeconds);
            List<Action> callbacks;
            List<string> consumers;
            lock (SyncRoot)
            {
                snapshot = next;
                requestRunning = false;
                nextRefreshUtc = DateTime.UtcNow.AddSeconds(refreshSeconds);
                callbacks = new List<Action>(JoinedCallbacks.Values);
                consumers = new List<string>(JoinedCallbacks.Keys);
                JoinedCallbacks.Clear();
                refreshTrigger = refreshSucceeded ? "定时间隔" : "异常状态重试";
            }

            NetworkCheckHistoryLogger.LogCompleted(
                "deepseek_balance",
                "deepseek_balance",
                effectiveTrigger,
                refreshSucceeded ? "Normal" : (next.ErrorCode ?? "Unknown"),
                refreshSucceeded,
                (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                new Dictionary<string, object>
                {
                    { "api_key_configured", next.ApiKeyConfigured },
                    { "balance_known", next.Known },
                    { "currency", next.Currency ?? string.Empty },
                    { "error_code", next.ErrorCode ?? string.Empty },
                    { "joined_consumers", string.Join(",", consumers.ToArray()) }
                });
            InvokeCallbacks(callbacks);
        });
        return true;
    }

    private static DeepSeekBalanceSnapshot PreserveKnownBalanceOnRefreshError(
        DeepSeekBalanceSnapshot cached,
        DeepSeekBalanceSnapshot error)
    {
        DeepSeekBalanceSnapshot result = cached.Clone();
        result.ApiKeyConfigured = true;
        result.RequestRunning = false;
        result.ErrorCode = error == null ? "ERROR" : (error.ErrorCode ?? "ERROR");
        result.ErrorMessage = error == null ? "请求失败" : (error.ErrorMessage ?? "请求失败");
        result.CheckedAtUtc = error == null ? DateTime.UtcNow : error.CheckedAtUtc;
        result.CheckedAtLocal = error == null ? DateTime.Now : error.CheckedAtLocal;
        return result;
    }

    internal static string ReadConfiguredApiKey()
    {
        string key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable, EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable, EnvironmentVariableTarget.Machine);
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }

        try
        {
            string secret;
            bool migrated;
            string errorCode;
            if (SecretStore.TryReadOrMigrateSecret(
                ApiKeyPath,
                LegacyApiKeyPath,
                SecretStore.TrimSecret,
                delegate(string value)
                {
                    return !string.IsNullOrWhiteSpace(value) &&
                        value.Trim().StartsWith("sk-", StringComparison.Ordinal);
                },
                out secret,
                out migrated,
                out errorCode))
            {
                return secret;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static DeepSeekBalanceSnapshot ReadBalance(string apiKey)
    {
        HttpWebRequest request = CreateBalanceRequest(apiKey);
        BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
            request,
            BoundedHttpTextReader.TinyProbeMaxBytes,
            RequestDeadlineMs,
            CancellationToken.None);
        if (!response.Success)
        {
            string code = response.StatusCode > 0
                ? response.StatusCode.ToString(CultureInfo.InvariantCulture)
                : (response.ErrorCode ?? "NETWORK");
            return BuildErrorSnapshot(true, code, GetErrorMessage(response.StatusCode, response.ErrorCode));
        }

        DeepSeekBalanceSnapshot parsed;
        return TryParseResponse(response.Content, out parsed)
            ? parsed
            : BuildErrorSnapshot(true, "PARSE", "解析失败");
    }

    private static HttpWebRequest CreateBalanceRequest(string apiKey)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BalanceUrl);
        request.Method = "GET";
        request.Accept = "application/json";
        request.UserAgent = ProductIdentity.UserAgent;
        request.AllowAutoRedirect = false;
        request.Timeout = RequestDeadlineMs;
        request.ReadWriteTimeout = RequestDeadlineMs;
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
        request.Headers[HttpRequestHeader.CacheControl] = "no-store, no-cache";
        return request;
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
            root = BoundedHttpTextReader.CreateJsonSerializer(
                BoundedHttpTextReader.TinyProbeMaxBytes).DeserializeObject(content) as Dictionary<string, object>;
        }
        catch
        {
            return false;
        }

        object infosObject;
        object[] infos = root != null && root.TryGetValue("balance_infos", out infosObject)
            ? infosObject as object[]
            : null;
        if (infos == null || infos.Length == 0)
        {
            return false;
        }

        string selectedCurrency = string.Empty;
        double selectedBalance = 0.0;
        bool found = false;
        for (int pass = 0; pass < 2 && !found; pass++)
        {
            for (int i = 0; i < infos.Length; i++)
            {
                Dictionary<string, object> info = infos[i] as Dictionary<string, object>;
                if (info == null)
                {
                    continue;
                }

                object currencyObject;
                object balanceObject;
                string currency = info.TryGetValue("currency", out currencyObject)
                    ? Convert.ToString(currencyObject, CultureInfo.InvariantCulture).ToUpperInvariant()
                    : string.Empty;
                if ((pass == 0 && !string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase)) ||
                    (pass == 1 && string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                double value;
                if (info.TryGetValue("total_balance", out balanceObject) &&
                    double.TryParse(
                        Convert.ToString(balanceObject, CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out value))
                {
                    selectedCurrency = string.IsNullOrWhiteSpace(currency) ? "CNY" : currency;
                    selectedBalance = Math.Max(0.0, value);
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            return false;
        }

        object availableObject;
        bool available = !root.TryGetValue("is_available", out availableObject) ||
            !(availableObject is bool) || (bool)availableObject;
        DateTime nowUtc = DateTime.UtcNow;
        parsed = new DeepSeekBalanceSnapshot
        {
            ApiKeyConfigured = true,
            Known = true,
            IsAvailable = available,
            Currency = selectedCurrency,
            Balance = selectedBalance,
            CheckedAtUtc = nowUtc,
            CheckedAtLocal = DateTime.Now,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            History = new List<DeepSeekBalancePoint>()
        };
        return true;
    }

    private static void ApplyHistory(DeepSeekBalanceSnapshot next)
    {
        lock (HistorySyncRoot)
        {
            EnsureHistoryLoadedLocked();
            DateTime nowUtc = next.CheckedAtUtc == DateTime.MinValue ? DateTime.UtcNow : next.CheckedAtUtc;
            History.Add(new DeepSeekBalancePoint
            {
                TimestampUtc = nowUtc,
                Currency = next.Currency ?? "CNY",
                Balance = next.Balance
            });
            TrimHistoryLocked(nowUtc);
            ApplyHistoryStats(next, History, nowUtc);
            SaveHistoryLocked();
        }
    }

    private static void ApplyHistoryStats(
        DeepSeekBalanceSnapshot target,
        List<DeepSeekBalancePoint> points,
        DateTime nowUtc)
    {
        string currency = target.Currency ?? "CNY";
        DateTime usageCutoff = nowUtc.AddHours(-24.0);
        DateTime historyCutoff = nowUtc.AddHours(-HistoryRetentionHours);
        DeepSeekBalancePoint previous = null;
        double used = 0.0;
        double reference = Math.Max(0.0, target.Balance);
        List<DeepSeekBalancePoint> visible = new List<DeepSeekBalancePoint>();
        for (int i = 0; i < points.Count; i++)
        {
            DeepSeekBalancePoint point = points[i];
            if (point == null || !string.Equals(point.Currency ?? "CNY", currency, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (point.TimestampUtc >= historyCutoff)
            {
                reference = Math.Max(reference, point.Balance);
                visible.Add(point.Clone());
            }

            if (point.TimestampUtc < usageCutoff)
            {
                previous = point;
                continue;
            }

            if (previous != null && point.Balance < previous.Balance)
            {
                used += previous.Balance - point.Balance;
            }

            previous = point;
        }

        target.Last24HourUsageKnown = visible.Count > 1;
        target.Last24HourUsage = Math.Max(0.0, used);
        target.ReferenceBalance = Math.Max(reference, target.Balance);
        target.RunwayKnown = used > 0.0001;
        target.RunwayHours = target.RunwayKnown ? Math.Max(0.0, target.Balance / (used / 24.0)) : 0.0;
        target.History = DownsampleForPublishedSnapshot(visible);
    }

    private static List<DeepSeekBalancePoint> DownsampleForPublishedSnapshot(List<DeepSeekBalancePoint> points)
    {
        if (points == null || points.Count <= MaximumPublishedHistoryPoints)
        {
            return points ?? new List<DeepSeekBalancePoint>();
        }

        List<DeepSeekBalancePoint> result = new List<DeepSeekBalancePoint>(MaximumPublishedHistoryPoints);
        int lastIndex = points.Count - 1;
        for (int i = 0; i < MaximumPublishedHistoryPoints; i++)
        {
            int sourceIndex = (int)Math.Round(
                i * lastIndex / (double)(MaximumPublishedHistoryPoints - 1),
                MidpointRounding.AwayFromZero);
            result.Add(points[sourceIndex].Clone());
        }

        return result;
    }

    private static void EnsureHistoryLoadedLocked()
    {
        if (historyLoaded)
        {
            return;
        }

        historyLoaded = true;
        History.Clear();
        if (!File.Exists(HistoryPath))
        {
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(HistoryPath, Encoding.UTF8);
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(
                BoundedHttpTextReader.TinyProbeMaxBytes);
            for (int i = 0; i < lines.Length; i++)
            {
                Dictionary<string, object> row;
                try
                {
                    row = serializer.DeserializeObject(lines[i]) as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }

                object timestampObject;
                object balanceObject;
                object currencyObject;
                DateTime timestampUtc;
                double balance;
                if (row == null || !row.TryGetValue("timestamp_utc", out timestampObject) ||
                    (!row.TryGetValue("balance", out balanceObject) && !row.TryGetValue("balance_cny", out balanceObject)) ||
                    !DateTime.TryParse(
                        Convert.ToString(timestampObject, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out timestampUtc) ||
                    !double.TryParse(
                        Convert.ToString(balanceObject, CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out balance))
                {
                    continue;
                }

                string currency = row.TryGetValue("currency", out currencyObject)
                    ? Convert.ToString(currencyObject, CultureInfo.InvariantCulture)
                    : "CNY";
                History.Add(new DeepSeekBalancePoint
                {
                    TimestampUtc = timestampUtc,
                    Currency = string.IsNullOrWhiteSpace(currency) ? "CNY" : currency.ToUpperInvariant(),
                    Balance = Math.Max(0.0, balance)
                });
            }
        }
        catch
        {
        }

        History.Sort(delegate(DeepSeekBalancePoint left, DeepSeekBalancePoint right)
        {
            return left.TimestampUtc.CompareTo(right.TimestampUtc);
        });
        TrimHistoryLocked(DateTime.UtcNow);
    }

    private static void TrimHistoryLocked(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddHours(-HistoryRetentionHours);
        History.RemoveAll(delegate(DeepSeekBalancePoint point)
        {
            return point == null || point.TimestampUtc < cutoff;
        });
        if (History.Count > MaximumHistoryPoints)
        {
            History.RemoveRange(0, History.Count - MaximumHistoryPoints);
        }
    }

    private static void SaveHistoryLocked()
    {
        try
        {
            Directory.CreateDirectory(Logger.DirectoryPath);
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < History.Count; i++)
            {
                DeepSeekBalancePoint point = History[i];
                builder.Append("{\"schema_version\":1,\"timestamp_utc\":\"");
                builder.Append(point.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                builder.Append("\",\"currency\":\"");
                builder.Append((point.Currency ?? "CNY").Replace("\"", string.Empty));
                builder.Append("\",\"balance\":");
                builder.Append(point.Balance.ToString("0.####", CultureInfo.InvariantCulture));
                builder.AppendLine("}");
            }

            string tempPath = HistoryPath + ".tmp";
            File.WriteAllText(tempPath, builder.ToString(), SharedEncoding.Utf8NoBom);
            if (File.Exists(HistoryPath))
            {
                File.Replace(tempPath, HistoryPath, null);
            }
            else
            {
                File.Move(tempPath, HistoryPath);
            }
        }
        catch
        {
        }
    }

    private static DeepSeekBalanceSnapshot BuildNoKeySnapshot()
    {
        DeepSeekBalanceSnapshot result = DeepSeekBalanceSnapshot.CreateEmpty();
        result.ErrorCode = "NO_KEY";
        result.ErrorMessage = "未配置 API Key";
        return result;
    }

    private static DeepSeekBalanceSnapshot BuildErrorSnapshot(bool configured, string code, string message)
    {
        DeepSeekBalanceSnapshot result = DeepSeekBalanceSnapshot.CreateEmpty();
        result.ApiKeyConfigured = configured;
        result.ErrorCode = string.IsNullOrWhiteSpace(code) ? "ERROR" : code;
        result.ErrorMessage = string.IsNullOrWhiteSpace(message) ? "请求失败" : message;
        result.CheckedAtUtc = DateTime.UtcNow;
        result.CheckedAtLocal = DateTime.Now;
        return result;
    }

    private static string GetErrorMessage(int statusCode, string fallback)
    {
        if (statusCode == 401) return "API Key 无效";
        if (statusCode == 402) return "余额不足";
        if (statusCode == 429) return "请求限流";
        if (statusCode >= 500) return "服务异常";
        if (string.Equals(fallback, "BODY_DEADLINE", StringComparison.Ordinal)) return "请求超时";
        return statusCode > 0 ? "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) : "无法连接";
    }

    private static void InvokeCallbacks(List<Action> callbacks)
    {
        if (callbacks == null)
        {
            return;
        }

        for (int i = 0; i < callbacks.Count; i++)
        {
            try
            {
                if (callbacks[i] != null)
                {
                    callbacks[i]();
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }
    }

    internal static void RunSelfTest()
    {
        DeepSeekBalanceSnapshot parsed;
        string fixture = "{\"is_available\":true,\"balance_infos\":[" +
            "{\"currency\":\"USD\",\"total_balance\":\"8.50\"}," +
            "{\"currency\":\"CNY\",\"total_balance\":\"88.50\"}]}";
        if (!TryParseResponse(fixture, out parsed) || !parsed.Known ||
            !string.Equals(parsed.Currency, "CNY", StringComparison.Ordinal) ||
            Math.Abs(parsed.Balance - 88.5) > 0.001)
        {
            throw new InvalidOperationException("DeepSeek balance parser self-test failed.");
        }

        DateTime nowUtc = DateTime.UtcNow;
        List<DeepSeekBalancePoint> points = new List<DeepSeekBalancePoint>
        {
            new DeepSeekBalancePoint { TimestampUtc = nowUtc.AddHours(-25), Currency = "CNY", Balance = 100 },
            new DeepSeekBalancePoint { TimestampUtc = nowUtc.AddHours(-20), Currency = "CNY", Balance = 95 },
            new DeepSeekBalancePoint { TimestampUtc = nowUtc.AddHours(-5), Currency = "CNY", Balance = 82 },
            new DeepSeekBalancePoint { TimestampUtc = nowUtc, Currency = "CNY", Balance = 88.5 }
        };
        ApplyHistoryStats(parsed, points, nowUtc);
        if (!parsed.Last24HourUsageKnown || Math.Abs(parsed.Last24HourUsage - 18.0) > 0.001 ||
            !parsed.RunwayKnown || parsed.History.Count != 4)
        {
            throw new InvalidOperationException("DeepSeek balance history self-test failed.");
        }

        DeepSeekBalanceSnapshot clone = parsed.Clone();
        clone.History[0].Balance = 0.0;
        if (Math.Abs(parsed.History[0].Balance - 100.0) > 0.001)
        {
            throw new InvalidOperationException("DeepSeek balance snapshot clone self-test failed.");
        }

        HttpWebRequest request = CreateBalanceRequest("self-test-key");
        try
        {
            if (!string.Equals(request.RequestUri.AbsoluteUri, BalanceUrl, StringComparison.Ordinal) ||
                !string.Equals(request.Headers[HttpRequestHeader.Authorization], "Bearer self-test-key", StringComparison.Ordinal) ||
                request.AllowAutoRedirect)
            {
                throw new InvalidOperationException("DeepSeek balance request policy self-test failed.");
            }
        }
        finally
        {
            request.Abort();
        }

        Console.WriteLine("DeepSeek balance monitor: PASS official response + local spend history + bounded request");
    }
}
