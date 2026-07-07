using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed partial class CodexRadarForm
{
    private const string CodexProviderUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const int CodexProviderUsageTimeoutMs = 10000;
    private const int CodexProviderUsageNormalRefreshSeconds = 300;
    private const int CodexProviderUsageErrorRefreshSeconds = 600;
    private const int CodexProviderUsageRateLimitRefreshSeconds = 900;
    private const int CodexProviderUsageFreshSeconds = 900;

    private readonly object codexProviderUsageLock = new object();
    private DateTime nextCodexProviderUsageRefreshUtc;
    private bool codexProviderUsageRequestRunning;
    private string codexProviderUsageRefreshTrigger = "首次刷新";
    private CodexQuotaSnapshot codexProviderQuotaSnapshot = CodexQuotaSnapshot.CreateDefault();
    private bool codexProviderQuotaSourceKnown;
    private ServiceHealthState codexProviderUsageHealth = ServiceHealthState.Unknown;
    private string codexProviderUsageErrorCode = string.Empty;
    private string codexProviderUsageErrorMessage = string.Empty;

    private sealed class CodexProviderUsageResult
    {
        public bool TokenConfigured { get; set; }
        public bool Success { get; set; }
        public bool RateLimited { get; set; }
        public CodexQuotaSnapshot Snapshot { get; set; }
        public ServiceHealthState Health { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
    }

    private void RequestCodexProviderUsageRefresh(string trigger)
    {
        lock (this.codexProviderUsageLock)
        {
            this.nextCodexProviderUsageRefreshUtc = DateTime.UtcNow;
            this.codexProviderUsageRefreshTrigger = string.IsNullOrWhiteSpace(trigger) ? "强制刷新" : trigger.Trim();
        }
    }

    private void RefreshCodexProviderUsageIfNeeded()
    {
        if (GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Codex)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (!IsNetworkAvailable())
        {
            SetCodexProviderUsageHealth(ServiceHealthState.Offline, "OFFLINE", "无网络");
            lock (this.codexProviderUsageLock)
            {
                this.nextCodexProviderUsageRefreshUtc = nowUtc.AddSeconds(CodexProviderUsageErrorRefreshSeconds);
            }

            return;
        }

        string trigger = "定时间隔";
        lock (this.codexProviderUsageLock)
        {
            if (this.codexProviderUsageRequestRunning ||
                (this.nextCodexProviderUsageRefreshUtc != DateTime.MinValue &&
                 nowUtc < this.nextCodexProviderUsageRefreshUtc))
            {
                return;
            }

            this.codexProviderUsageRequestRunning = true;
            trigger = EmptyFallback(this.codexProviderUsageRefreshTrigger, "定时间隔");
            this.codexProviderUsageRefreshTrigger = "定时间隔";
        }

        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CodexProviderUsageResult result;
            try
            {
                result = ReadCodexProviderUsage(this.currentSettings);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                result = BuildCodexProviderUsageError(false, ServiceHealthState.Unreachable, "ERROR", "请求失败");
            }

            stopwatch.Stop();
            if (result == null)
            {
                result = BuildCodexProviderUsageError(false, ServiceHealthState.Unreachable, "ERROR", "请求失败");
            }

            DateTime nextRefreshUtc = DateTime.UtcNow.AddSeconds(
                result.Success
                    ? CodexProviderUsageNormalRefreshSeconds
                    : (result.RateLimited ? CodexProviderUsageRateLimitRefreshSeconds : CodexProviderUsageErrorRefreshSeconds));
            bool sourceKnownAfter;
            lock (this.codexProviderUsageLock)
            {
                this.codexProviderUsageHealth = result.Health;
                this.codexProviderUsageErrorCode = result.ErrorCode ?? string.Empty;
                this.codexProviderUsageErrorMessage = result.ErrorMessage ?? string.Empty;
                this.codexProviderUsageRequestRunning = false;
                this.nextCodexProviderUsageRefreshUtc = nextRefreshUtc;
                sourceKnownAfter = this.codexProviderQuotaSourceKnown || (result.Success && result.Snapshot != null);
            }

            if (result.Success && result.Snapshot != null)
            {
                ApplyCodexProviderUsageResultOnUiThread(result.Snapshot);
            }

            NetworkCheckHistoryLogger.LogCompleted(
                "codex_radar",
                "codex_provider_usage",
                trigger,
                result.Success ? "正常" : EmptyFallback(result.ErrorMessage, result.Health.ToString()),
                result.Success,
                (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                new Dictionary<string, object>
                {
                    { "health", result.Health.ToString() },
                    { "error_code", result.ErrorCode ?? string.Empty },
                    { "token_configured", result.TokenConfigured },
                    { "rate_limited", result.RateLimited },
                    { "source_known_after", sourceKnownAfter },
                    { "fallback_available", this.quotaSourceKnown }
                });
        });
    }

    private void ApplyCodexProviderUsageResultOnUiThread(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        try
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (this.IsDisposed ||
                    this.currentSettings == null ||
                    this.currentSettings.CodexRadarRandomTestEnabled ||
                    GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Codex)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                this.lastQuotaRefreshUtc = nowUtc;
                CodexQuotaSnapshot providerSnapshot = NormalizeQuotaSnapshot(snapshot.Clone());
                MarkQuotaSnapshotSource(providerSnapshot, "provider");
                bool codexRunning = GetLastSoftwareRuntimePresenceSnapshot().CodexRunning;
                string rejectReason;
                if (ShouldRejectSuspiciousProviderQuotaSnapshot(providerSnapshot, out rejectReason))
                {
                    LogRejectedProviderQuotaSnapshot(providerSnapshot, rejectReason, codexRunning);
                    RenderLayeredWindow();
                    return;
                }

                lock (this.codexProviderUsageLock)
                {
                    this.codexProviderQuotaSnapshot = providerSnapshot.Clone();
                    this.codexProviderQuotaSourceKnown = true;
                }

                TryWriteQuotaIniSnapshot(providerSnapshot);
                ApplyCodexQuotaSnapshot(providerSnapshot.Clone(), true, codexRunning, DateTime.Now, nowUtc, "provider");
                RenderLayeredWindow();
            });
        }
        catch
        {
        }
    }

    private bool TryGetCodexProviderQuotaSnapshot(out CodexQuotaSnapshot snapshot)
    {
        snapshot = null;
        DateTime nowUtc = DateTime.UtcNow;
        lock (this.codexProviderUsageLock)
        {
            if (!this.codexProviderQuotaSourceKnown || this.codexProviderQuotaSnapshot == null)
            {
                return false;
            }

            DateTime sourceUtc = this.codexProviderQuotaSnapshot.SourceUpdatedKnown
                ? this.codexProviderQuotaSnapshot.SourceUpdatedUtc
                : DateTime.MinValue;
            if (sourceUtc == DateTime.MinValue ||
                (nowUtc - sourceUtc).TotalSeconds > CodexProviderUsageFreshSeconds)
            {
                return false;
            }

            snapshot = this.codexProviderQuotaSnapshot.Clone();
            return true;
        }
    }

    private void SetCodexProviderUsageHealth(ServiceHealthState health, string errorCode, string errorMessage)
    {
        lock (this.codexProviderUsageLock)
        {
            this.codexProviderUsageHealth = health;
            this.codexProviderUsageErrorCode = errorCode ?? string.Empty;
            this.codexProviderUsageErrorMessage = errorMessage ?? string.Empty;
            if (health == ServiceHealthState.Offline)
            {
                this.codexProviderUsageRequestRunning = false;
            }
        }
    }

    private static CodexProviderUsageResult ReadCodexProviderUsage(WidgetSettings settings)
    {
        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, CodexProviderUsageUrl, out aiBlockReason))
        {
            return BuildCodexProviderUsageError(false, ServiceHealthState.Unavailable, "AI_BLOCK", "AI阻断");
        }

        string token = GetCodexAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildCodexProviderUsageError(false, ServiceHealthState.Unavailable, "NO_TOKEN", "未登录");
        }

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(CodexProviderUsageUrl);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent + "/" + ProductIdentity.Version;
        request.Timeout = CodexProviderUsageTimeoutMs;
        request.ReadWriteTimeout = CodexProviderUsageTimeoutMs;
        request.Headers["Authorization"] = "Bearer " + token;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                string content = ReadResponseText(response);
                return ParseCodexProviderUsageResponse(content, true, (int)response.StatusCode);
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    int statusCode = (int)response.StatusCode;
                    string content = ReadResponseText(response);
                    CodexProviderUsageResult parsed = ParseCodexProviderUsageResponse(content, true, statusCode);
                    if (parsed != null && parsed.RateLimited)
                    {
                        return parsed;
                    }

                    return BuildCodexProviderUsageError(
                        true,
                        statusCode == 429
                            ? ServiceHealthState.Degraded
                            : (statusCode == 401 || statusCode == 403
                                ? ServiceHealthState.Unavailable
                                : ServiceHealthState.Unreachable),
                        statusCode.ToString(CultureInfo.InvariantCulture),
                        GetCodexProviderUsageHttpErrorReason(statusCode));
                }
            }

            return BuildCodexProviderUsageError(true, ServiceHealthState.Unreachable, "NET", "无法连接");
        }
    }

    private static CodexProviderUsageResult ParseCodexProviderUsageResponse(
        string content,
        bool tokenConfigured,
        int statusCode)
    {
        if (statusCode == 429)
        {
            return BuildCodexProviderUsageError(tokenConfigured, ServiceHealthState.Degraded, "429", "限流");
        }

        if (statusCode < 200 || statusCode >= 300)
        {
            return BuildCodexProviderUsageError(
                tokenConfigured,
                statusCode == 401 || statusCode == 403 ? ServiceHealthState.Unavailable : ServiceHealthState.Unreachable,
                statusCode.ToString(CultureInfo.InvariantCulture),
                GetCodexProviderUsageHttpErrorReason(statusCode));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return BuildCodexProviderUsageError(tokenConfigured, ServiceHealthState.Incomplete, "EMPTY", "空响应");
        }

        Dictionary<string, object> root;
        try
        {
            root = new JavaScriptSerializer().DeserializeObject(content) as Dictionary<string, object>;
        }
        catch
        {
            return BuildCodexProviderUsageError(tokenConfigured, ServiceHealthState.Incomplete, "PARSE", "解析失败");
        }

        if (root == null)
        {
            return BuildCodexProviderUsageError(tokenConfigured, ServiceHealthState.Incomplete, "PARSE", "解析失败");
        }

        Dictionary<string, object> rateLimit = GetQuotaObject(root, "rate_limit");
        if (rateLimit == null)
        {
            rateLimit = GetQuotaObject(root, "rate_limits");
        }

        if (rateLimit == null)
        {
            rateLimit = root;
        }

        CodexQuotaSnapshot snapshot = CodexQuotaSnapshot.CreateDefault();
        bool found = ApplyCodexProviderUsageSlot(rateLimit, "primary_window", true, snapshot);
        found = ApplyCodexProviderUsageSlot(rateLimit, "secondary_window", false, snapshot) || found;
        found = ApplyCodexProviderUsageSlot(rateLimit, "primary", true, snapshot) || found;
        found = ApplyCodexProviderUsageSlot(rateLimit, "secondary", false, snapshot) || found;
        if (!found)
        {
            return BuildCodexProviderUsageError(tokenConfigured, ServiceHealthState.Incomplete, "NO_USAGE", "数据不完整");
        }

        snapshot.SourceUpdatedUtc = DateTime.UtcNow;
        snapshot.SourceUpdatedKnown = true;
        return new CodexProviderUsageResult
        {
            TokenConfigured = tokenConfigured,
            Success = true,
            RateLimited = false,
            Snapshot = snapshot,
            Health = ServiceHealthState.Normal,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty
        };
    }

    private static bool ApplyCodexProviderUsageSlot(
        Dictionary<string, object> rateLimit,
        string key,
        bool fiveHour,
        CodexQuotaSnapshot snapshot)
    {
        object slotObject;
        Dictionary<string, object> slot;
        if (rateLimit == null ||
            !rateLimit.TryGetValue(key, out slotObject) ||
            (slot = slotObject as Dictionary<string, object>) == null)
        {
            return false;
        }

        double rawUsedValue;
        double usedPercent;
        string usedFieldName;
        if (!TryGetProviderUsageUsedPercent(slot, out usedPercent, out usedFieldName, out rawUsedValue))
        {
            return false;
        }

        int remaining = ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool resetKnown = TryGetQuotaDate(slot, "reset_at", out resetLocal) ||
            TryGetQuotaDate(slot, "resets_at", out resetLocal);
        if (fiveHour)
        {
            snapshot.FiveHourPercent = remaining;
            snapshot.FiveHourResetLocal = resetLocal;
            snapshot.FiveHourResetKnown = resetKnown;
            SetQuotaUsageDiagnostics(snapshot, true, "provider", usedFieldName, rawUsedValue, usedPercent);
        }
        else
        {
            snapshot.WeeklyPercent = remaining;
            snapshot.WeeklyResetLocal = resetLocal;
            snapshot.WeeklyResetKnown = resetKnown;
            SetQuotaUsageDiagnostics(snapshot, false, "provider", usedFieldName, rawUsedValue, usedPercent);
        }

        return true;
    }

    private static bool TryGetProviderUsageUsedPercent(
        Dictionary<string, object> slot,
        out double usedPercent,
        out string fieldName,
        out double rawValue)
    {
        usedPercent = 0.0;
        fieldName = string.Empty;
        rawValue = 0.0;
        object value;
        if (slot != null && slot.TryGetValue("used_percent", out value) && TryReadQuotaNumber(value, out usedPercent))
        {
            fieldName = "used_percent";
            rawValue = usedPercent;
            usedPercent = NormalizeProviderUsedPercent(rawValue, false);
            return true;
        }

        if (slot != null && slot.TryGetValue("used_percentage", out value) && TryReadQuotaNumber(value, out usedPercent))
        {
            fieldName = "used_percentage";
            rawValue = usedPercent;
            usedPercent = NormalizeProviderUsedPercent(rawValue, false);
            return true;
        }

        if (slot != null && slot.TryGetValue("utilization", out value) && TryReadQuotaNumber(value, out usedPercent))
        {
            fieldName = "utilization";
            rawValue = usedPercent;
            usedPercent = NormalizeProviderUsedPercent(rawValue, true);
            return true;
        }

        return false;
    }

    private static double NormalizeProviderUsedPercent(double rawValue, bool fractionPreferred)
    {
        double percent = fractionPreferred && rawValue >= 0.0 && rawValue <= 1.0
            ? rawValue * 100.0
            : rawValue;
        if (percent < 0.0)
        {
            return 0.0;
        }

        if (percent > 100.0)
        {
            return 100.0;
        }

        return percent;
    }

    private static CodexProviderUsageResult BuildCodexProviderUsageError(
        bool tokenConfigured,
        ServiceHealthState health,
        string errorCode,
        string message)
    {
        return new CodexProviderUsageResult
        {
            TokenConfigured = tokenConfigured,
            Success = false,
            RateLimited = string.Equals(errorCode, "429", StringComparison.OrdinalIgnoreCase),
            Snapshot = null,
            Health = health,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = message ?? string.Empty
        };
    }

    private static string GetCodexProviderUsageHttpErrorReason(int statusCode)
    {
        if (statusCode == 401)
        {
            return "鉴权失败";
        }

        if (statusCode == 403)
        {
            return "权限不足";
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

    private static string GetCodexAccessToken()
    {
        string token = GetEnvironmentVariableAnyTarget("CODEX_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        string path = GetCodexAuthJsonPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            string content = File.ReadAllText(path, Encoding.UTF8);
            object root = new JavaScriptSerializer().DeserializeObject(content);
            return FindCodexAccessToken(root);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetCodexAuthJsonPath()
    {
        string codexHome = GetEnvironmentVariableAnyTarget("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Path.Combine(codexHome.Trim(), "auth.json");
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, ".codex", "auth.json");
    }

    private static string GetEnvironmentVariableAnyTarget(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static string FindCodexAccessToken(object node)
    {
        Dictionary<string, object> dictionary = node as Dictionary<string, object>;
        if (dictionary != null)
        {
            object token;
            if (dictionary.TryGetValue("access_token", out token) ||
                dictionary.TryGetValue("accessToken", out token))
            {
                string text = Convert.ToString(token, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                string nested = FindCodexAccessToken(pair.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        object[] array = node as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                string nested = FindCodexAccessToken(array[i]);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }
}
