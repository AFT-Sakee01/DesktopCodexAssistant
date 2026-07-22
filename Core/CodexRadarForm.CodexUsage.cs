using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed partial class CodexRadarForm
{
    private const string CodexProviderUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string CodexResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private const int CodexProviderUsageTimeoutMs = 10000;
    private const int CodexResetCreditsTimeoutMs = 10000;
    private const int CodexProviderUsageNormalRefreshSeconds = 300;
    private const int CodexProviderUsageErrorRefreshSeconds = 600;
    private const int CodexProviderUsageRateLimitRefreshSeconds = 900;
    private const int CodexProviderUsageFreshSeconds = 900;
    private const int CodexAccountEndpointStaggerSeconds = 10;
    private const int CodexAuthJsonMaxBytes = 1024 * 1024;
    private const int CodexAccessTokenMaxChars = 16 * 1024;
    private const int CodexResetCreditsNormalRefreshSeconds = 3600;
    private const int CodexResetCreditsErrorRefreshSeconds = 900;

    private readonly object codexProviderUsageLock = new object();
    private readonly object codexAccountEndpointStaggerLock = new object();
    private readonly object codexResetCreditsLock = new object();
    private DateTime nextCodexProviderUsageRefreshUtc;
    private DateTime nextCodexResetCreditsRefreshUtc;
    private bool codexProviderUsageRequestRunning;
    private bool codexResetCreditsRequestRunning;
    private string codexProviderUsageRefreshTrigger = "首次刷新";
    private string codexResetCreditsRefreshTrigger = "首次刷新";
    private CodexQuotaSnapshot codexProviderQuotaSnapshot = CodexQuotaSnapshot.CreateDefault();
    private CodexResetCreditsSnapshot codexResetCreditsSnapshot = CodexResetCreditsSnapshot.CreateDefault();
    private readonly CodexQuotaHistoryStore codexQuotaHistoryStore;
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

    private sealed class CodexResetCreditsSnapshot
    {
        public bool Known { get; set; }
        public bool TokenConfigured { get; set; }
        public bool RequestRunning { get; set; }
        public int ReportedCount { get; set; }
        public bool AllExpirationTimesKnown { get; set; }
        public List<DateTime> ExpirationTimesUtc { get; set; }
        public DateTime SourceUpdatedUtc { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static CodexResetCreditsSnapshot CreateDefault()
        {
            return new CodexResetCreditsSnapshot
            {
                Known = false,
                TokenConfigured = false,
                RequestRunning = false,
                ReportedCount = 0,
                AllExpirationTimesKnown = false,
                ExpirationTimesUtc = new List<DateTime>(),
                SourceUpdatedUtc = DateTime.MinValue,
                ErrorCode = string.Empty,
                ErrorMessage = string.Empty
            };
        }

        public CodexResetCreditsSnapshot Clone()
        {
            return new CodexResetCreditsSnapshot
            {
                Known = this.Known,
                TokenConfigured = this.TokenConfigured,
                RequestRunning = this.RequestRunning,
                ReportedCount = this.ReportedCount,
                AllExpirationTimesKnown = this.AllExpirationTimesKnown,
                ExpirationTimesUtc = this.ExpirationTimesUtc == null
                    ? new List<DateTime>()
                    : new List<DateTime>(this.ExpirationTimesUtc),
                SourceUpdatedUtc = this.SourceUpdatedUtc,
                ErrorCode = this.ErrorCode ?? string.Empty,
                ErrorMessage = this.ErrorMessage ?? string.Empty
            };
        }

        public int GetActiveCount(DateTime nowUtc)
        {
            if (!this.Known)
            {
                return 0;
            }

            if (!this.AllExpirationTimesKnown)
            {
                return Math.Max(0, this.ReportedCount);
            }

            int count = 0;
            List<DateTime> expirations = this.ExpirationTimesUtc;
            for (int i = 0; expirations != null && i < expirations.Count; i++)
            {
                if (expirations[i] > nowUtc)
                {
                    count++;
                }
            }

            return count;
        }

        public bool TryGetEarliestActiveExpirationUtc(DateTime nowUtc, out DateTime expirationUtc)
        {
            expirationUtc = DateTime.MinValue;
            List<DateTime> expirations = this.ExpirationTimesUtc;
            for (int i = 0; expirations != null && i < expirations.Count; i++)
            {
                DateTime candidate = expirations[i];
                if (candidate <= nowUtc)
                {
                    continue;
                }

                if (expirationUtc == DateTime.MinValue || candidate < expirationUtc)
                {
                    expirationUtc = candidate;
                }
            }

            return expirationUtc != DateTime.MinValue;
        }
    }

    private sealed class CodexResetCreditsResult
    {
        public bool TokenConfigured { get; set; }
        public bool Success { get; set; }
        public bool RateLimited { get; set; }
        public CodexResetCreditsSnapshot Snapshot { get; set; }
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

    private void RequestCodexResetCreditsRefresh(string trigger)
    {
        lock (this.codexResetCreditsLock)
        {
            bool manual = !string.IsNullOrWhiteSpace(trigger) &&
                trigger.IndexOf("操作面板", StringComparison.OrdinalIgnoreCase) >= 0;
            DateTime nowUtc = DateTime.UtcNow;
            if (!manual &&
                this.codexResetCreditsSnapshot != null &&
                this.codexResetCreditsSnapshot.Known &&
                this.codexResetCreditsSnapshot.SourceUpdatedUtc != DateTime.MinValue &&
                (nowUtc - this.codexResetCreditsSnapshot.SourceUpdatedUtc).TotalSeconds < 60.0)
            {
                return;
            }

            this.nextCodexResetCreditsRefreshUtc = nowUtc;
            this.codexResetCreditsRefreshTrigger = string.IsNullOrWhiteSpace(trigger) ? "强制刷新" : trigger.Trim();
        }
    }

    private void RefreshCodexResetCreditsIfNeeded()
    {
        // Reset cards are account metadata, not quota percentages; keep them memory-only and out of
        // quota.ini/quota-decision-history so they cannot perturb 5h/weekly ring baselines.
        DateTime nowUtc = DateTime.UtcNow;
        if (IsCodexProviderUsageRequestRunning())
        {
            lock (this.codexResetCreditsLock)
            {
                this.nextCodexResetCreditsRefreshUtc = nowUtc.AddSeconds(CodexAccountEndpointStaggerSeconds);
            }

            return;
        }
        if (!IsNetworkAvailable())
        {
            bool changed = false;
            lock (this.codexResetCreditsLock)
            {
                CodexResetCreditsSnapshot snapshot = this.codexResetCreditsSnapshot.Clone();
                changed = snapshot.RequestRunning ||
                    !string.Equals(snapshot.ErrorCode, "OFFLINE", StringComparison.Ordinal);
                snapshot.RequestRunning = false;
                snapshot.ErrorCode = "OFFLINE";
                snapshot.ErrorMessage = "无网络";
                this.codexResetCreditsSnapshot = snapshot;
                this.codexResetCreditsRequestRunning = false;
                this.nextCodexResetCreditsRefreshUtc = nowUtc.AddSeconds(CodexResetCreditsErrorRefreshSeconds);
            }

            if (changed)
            {
                PublishProjectionStateFromOwner();
            }

            return;
        }

        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        string trigger = "定时间隔";
        lock (this.codexAccountEndpointStaggerLock)
        {
            if (IsCodexProviderUsageRequestRunning())
            {
                lock (this.codexResetCreditsLock)
                {
                    this.nextCodexResetCreditsRefreshUtc = nowUtc.AddSeconds(CodexAccountEndpointStaggerSeconds);
                }

                return;
            }

            lock (this.codexResetCreditsLock)
            {
                if (this.codexResetCreditsRequestRunning ||
                    (this.nextCodexResetCreditsRefreshUtc != DateTime.MinValue &&
                     nowUtc < this.nextCodexResetCreditsRefreshUtc))
                {
                    return;
                }

                this.codexResetCreditsRequestRunning = true;
                trigger = EmptyFallback(this.codexResetCreditsRefreshTrigger, "定时间隔");
                this.codexResetCreditsRefreshTrigger = "定时间隔";
                CodexResetCreditsSnapshot running = this.codexResetCreditsSnapshot.Clone();
                running.RequestRunning = true;
                this.codexResetCreditsSnapshot = running;
            }
        }

        WidgetSettings requestSettings = this.CurrentSettings.Clone();
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CodexResetCreditsResult result;
            try
            {
                result = ReadCodexResetCredits(requestSettings, lease.CancellationToken);
            }
            catch (Exception ex)
            {
                TryExecuteOwnerCurrent(lease, delegate { Program.LogException(ex); });
                result = BuildCodexResetCreditsError(false, ServiceHealthState.Unreachable, "ERROR", "请求失败");
            }

            stopwatch.Stop();
            if (result == null)
            {
                result = BuildCodexResetCreditsError(false, ServiceHealthState.Unreachable, "ERROR", "请求失败");
            }

            TryExecuteOwnerCurrent(lease, delegate
            {
                DateTime nextRefreshUtc = DateTime.UtcNow.AddSeconds(
                    result.Success
                        ? CodexResetCreditsNormalRefreshSeconds
                        : CodexResetCreditsErrorRefreshSeconds);
                CodexResetCreditsSnapshot displaySnapshot;
                lock (this.codexResetCreditsLock)
                {
                    if (result.Success && result.Snapshot != null)
                    {
                        displaySnapshot = result.Snapshot.Clone();
                    }
                    else
                    {
                        displaySnapshot = this.codexResetCreditsSnapshot.Clone();
                        displaySnapshot.TokenConfigured = result.TokenConfigured;
                        displaySnapshot.ErrorCode = result.ErrorCode ?? string.Empty;
                        displaySnapshot.ErrorMessage = result.ErrorMessage ?? string.Empty;
                    }

                    displaySnapshot.RequestRunning = false;
                    this.codexResetCreditsSnapshot = displaySnapshot;
                    this.codexResetCreditsRequestRunning = false;
                    this.nextCodexResetCreditsRefreshUtc = nextRefreshUtc;
                }

                TryBeginInvokeOwnerCurrent(lease, delegate { PublishProjectionStateFromOwner(); });

                DateTime logNowUtc = DateTime.UtcNow;
                DateTime earliestUtc;
                bool earliestKnown = displaySnapshot.TryGetEarliestActiveExpirationUtc(logNowUtc, out earliestUtc);
                NetworkCheckHistoryLogger.LogCompleted(
                    "codex_radar",
                    "codex_reset_credits",
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
                        { "count", displaySnapshot.GetActiveCount(logNowUtc) },
                        { "earliest_expiration_known", earliestKnown },
                        { "earliest_expiration_hours", earliestKnown ? (object)Math.Round((earliestUtc - logNowUtc).TotalHours, 2) : null }
                    });
            });
        });
    }

    private void RefreshCodexProviderUsageIfNeeded()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (IsCodexResetCreditsRequestRunning())
        {
            lock (this.codexProviderUsageLock)
            {
                this.nextCodexProviderUsageRefreshUtc = nowUtc.AddSeconds(CodexAccountEndpointStaggerSeconds);
            }

            return;
        }
        if (!IsNetworkAvailable())
        {
            SetCodexProviderUsageHealth(ServiceHealthState.Offline, "OFFLINE", "无网络");
            lock (this.codexProviderUsageLock)
            {
                this.nextCodexProviderUsageRefreshUtc = nowUtc.AddSeconds(CodexProviderUsageErrorRefreshSeconds);
            }

            return;
        }

        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
        {
            return;
        }

        string trigger = "定时间隔";
        lock (this.codexAccountEndpointStaggerLock)
        {
            if (IsCodexResetCreditsRequestRunning())
            {
                lock (this.codexProviderUsageLock)
                {
                    this.nextCodexProviderUsageRefreshUtc = nowUtc.AddSeconds(CodexAccountEndpointStaggerSeconds);
                }

                return;
            }

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
        }

        WidgetSettings requestSettings = this.CurrentSettings.Clone();
        Task.Run((Action)delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            CodexProviderUsageResult result;
            try
            {
                result = ReadCodexProviderUsage(requestSettings, lease.CancellationToken);
            }
            catch (Exception ex)
            {
                TryExecuteOwnerCurrent(lease, delegate { Program.LogException(ex); });
                result = BuildCodexProviderUsageError(false, ServiceHealthState.Unreachable, "ERROR", "请求失败");
            }

            stopwatch.Stop();
            if (result == null)
            {
                result = BuildCodexProviderUsageError(false, ServiceHealthState.Unreachable, "ERROR", "请求失败");
            }

            TryExecuteOwnerCurrent(lease, delegate
            {
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
                    ApplyCodexProviderUsageResultOnUiThread(result.Snapshot, lease);
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
        });
    }

    private void ApplyCodexProviderUsageResultOnUiThread(CodexQuotaSnapshot snapshot, OwnerOperationLease lease)
    {
        if (snapshot == null || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        try
        {
            TryBeginInvokeOwnerCurrent(lease, delegate
            {
                if (this.IsDisposed ||
                    this.CurrentSettings == null ||
                    this.CurrentSettings.CodexRadarRandomTestEnabled)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex).LastRefreshUtc = nowUtc;
                CodexQuotaSnapshot providerSnapshot = NormalizeQuotaSnapshot(snapshot.Clone());
                MarkQuotaSnapshotSource(providerSnapshot, "provider");
                bool identityChanged = HasCodexProviderQuotaIdentityChange(providerSnapshot);

                bool codexRunning = GetLastSoftwareRuntimePresenceSnapshot().CodexRunning;
                string rejectReason;
                if (ShouldRejectSuspiciousProviderQuotaSnapshot(providerSnapshot, out rejectReason))
                {
                    LogRejectedProviderQuotaSnapshot(providerSnapshot, rejectReason, codexRunning);
                    PublishProjectionStateFromOwner();
                    return;
                }

                if (identityChanged)
                {
                    LogCodexUsageIdentityChangeDiagnostic(providerSnapshot, codexRunning);
                }

                QuotaRingDecisionInfo decision = ApplyQuotaSnapshot(
                    CodexRadarSoftwareMode.Codex,
                    providerSnapshot,
                    true,
                    codexRunning,
                    DateTime.Now,
                    nowUtc,
                    "provider");
                if (decision == null || !decision.IdentitySampleRejected)
                {
                    lock (this.codexProviderUsageLock)
                    {
                        this.codexProviderQuotaSnapshot = providerSnapshot.Clone();
                        this.codexProviderQuotaSourceKnown = true;
                    }

                    TryWriteQuotaIniSnapshot(providerSnapshot);
                }

                PublishProjectionStateFromOwner();
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

    private bool IsCodexProviderUsageRequestRunning()
    {
        lock (this.codexProviderUsageLock)
        {
            return this.codexProviderUsageRequestRunning;
        }
    }

    private bool IsCodexResetCreditsRequestRunning()
    {
        lock (this.codexResetCreditsLock)
        {
            return this.codexResetCreditsRequestRunning;
        }
    }

    private bool HasCodexProviderQuotaIdentityChange(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        QuotaRuntimeState state = this.codexRuntimeState.Quota;
        return HasQuotaResetIdentityChanged(
                state.TrackedFiveHourResetLocal,
                snapshot.FiveHourResetKnown ? snapshot.FiveHourResetLocal : DateTime.MinValue) ||
            HasQuotaResetIdentityChanged(
                state.TrackedWeeklyResetLocal,
                snapshot.WeeklyResetKnown ? snapshot.WeeklyResetLocal : DateTime.MinValue);
    }

    private static bool HasQuotaResetIdentityChanged(DateTime trackedLocal, DateTime incomingLocal)
    {
        return trackedLocal != DateTime.MinValue &&
            incomingLocal != DateTime.MinValue &&
            Math.Abs((incomingLocal - trackedLocal).TotalMinutes) > QuotaIdentityToleranceMinutes;
    }

    private static void LogCodexUsageIdentityChangeDiagnostic(
        CodexQuotaSnapshot snapshot,
        bool codexRunning)
    {
        if (snapshot == null)
        {
            return;
        }

        QuotaDecisionHistoryLogger.LogDecision(
            "provider_identity_change",
            true,
            codexRunning,
            new Dictionary<string, object>
            {
                { "correlation_id", snapshot.ProviderCorrelationId ?? string.Empty },
                { "http_status", snapshot.ProviderHttpStatus },
                { "response_bytes", snapshot.ProviderResponseBytes },
                { "body_sha256", snapshot.ProviderResponseBodySha256 ?? string.Empty },
                { "provider_plan", NormalizeProviderDiagnosticEnum(snapshot.ProviderPlan) },
                { "provider_pool", NormalizeProviderDiagnosticEnum(snapshot.ProviderPool) },
                { "five_hour_used_percent", snapshot.FiveHourUsageDiagnosticKnown ? (object)snapshot.FiveHourNormalizedUsedPercent : null },
                { "weekly_used_percent", snapshot.WeeklyUsageDiagnosticKnown ? (object)snapshot.WeeklyNormalizedUsedPercent : null },
                { "five_hour_reset_local", snapshot.FiveHourResetKnown ? snapshot.FiveHourResetLocal.ToString("o", CultureInfo.InvariantCulture) : null },
                { "weekly_reset_local", snapshot.WeeklyResetKnown ? snapshot.WeeklyResetLocal.ToString("o", CultureInfo.InvariantCulture) : null }
            });
    }

    private static string NormalizeProviderDiagnosticEnum(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "free":
            case "plus":
            case "pro":
            case "team":
            case "business":
            case "enterprise":
            case "education":
            case "base":
            case "additional":
            case "default":
                return normalized;
            default:
                return "unknown";
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

    private CodexResetCreditsSnapshot GetCodexResetCreditsDisplaySnapshot()
    {
        lock (this.codexResetCreditsLock)
        {
            return this.codexResetCreditsSnapshot.Clone();
        }
    }

    private string GetCodexResetCreditsDisplayText()
    {
        if (GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Codex)
        {
            return "RS:--";
        }

        return BuildCodexResetCreditsDisplayText(GetCodexResetCreditsDisplaySnapshot(), DateTime.UtcNow);
    }

    // True when the earliest active reset credit expires within the next 24h - the same "<n>h"
    // sub-day condition shown in the RS label, used to turn the two quota rings rainbow. Cached
    // snapshot only; never triggers a token read or network request from the paint path.
    private bool HasSubDayActiveResetCredit(DateTime nowUtc)
    {
        CodexResetCreditsSnapshot snapshot = GetCodexResetCreditsDisplaySnapshot();
        DateTime expirationUtc;
        if (snapshot == null || !snapshot.Known ||
            !snapshot.TryGetEarliestActiveExpirationUtc(nowUtc, out expirationUtc))
        {
            return false;
        }

        double hours = (expirationUtc - nowUtc).TotalHours;
        return hours > 0.0 && hours <= 24.0;
    }

    private static string BuildCodexResetCreditsDisplayText(CodexResetCreditsSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot == null || !snapshot.Known)
        {
            return snapshot != null && snapshot.RequestRunning ? "RS:..." : "RS:--";
        }

        int count = snapshot.GetActiveCount(nowUtc);
        DateTime expirationUtc;
        if (count <= 0 || !snapshot.TryGetEarliestActiveExpirationUtc(nowUtc, out expirationUtc))
        {
            return "RS:" + Math.Max(0, count).ToString(CultureInfo.InvariantCulture);
        }

        return "RS:" + count.ToString(CultureInfo.InvariantCulture) + "-" +
            FormatCodexResetCreditRemaining(nowUtc, expirationUtc);
    }

    private static string FormatCodexResetCreditRemaining(DateTime nowUtc, DateTime expirationUtc)
    {
        double totalHours = Math.Max(0.0, (expirationUtc - nowUtc).TotalHours);
        if (totalHours <= 24.0)
        {
            // Under a day the earliest reset is imminent: show whole hours as "<n>h" (this is also
            // the sub-day condition that turns the two quota rings rainbow).
            int hours = Math.Max(0, (int)Math.Ceiling(totalHours));
            return hours.ToString(CultureInfo.InvariantCulture) + "h";
        }

        // A day or more away: whole days as "<n>d" (ASCII unit, no CJK).
        int days = Math.Max(1, (int)Math.Ceiling(totalHours / 24.0));
        return days.ToString(CultureInfo.InvariantCulture) + "d";
    }

    private static CodexResetCreditsResult ReadCodexResetCredits(
        WidgetSettings settings,
        CancellationToken cancellationToken)
    {
        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, CodexResetCreditsUrl, out aiBlockReason))
        {
            return BuildCodexResetCreditsError(false, ServiceHealthState.Unavailable, "AI_BLOCK", "AI阻断");
        }

        string token = GetCodexAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildCodexResetCreditsError(false, ServiceHealthState.Unavailable, "NO_TOKEN", "未登录");
        }

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(CodexResetCreditsUrl);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent + "/" + ProductIdentity.Version;
        request.Timeout = CodexResetCreditsTimeoutMs;
        request.ReadWriteTimeout = CodexResetCreditsTimeoutMs;
        request.Headers["Authorization"] = "Bearer " + token;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
            request,
            BoundedHttpTextReader.AuthenticatedJsonMaxBytes,
            CodexResetCreditsTimeoutMs,
            cancellationToken);
        if (response.StatusCode <= 0)
        {
            return BuildCodexResetCreditsError(true, ServiceHealthState.Unreachable, response.ErrorCode, "无法连接");
        }

        CodexResetCreditsResult parsed = ParseCodexResetCreditsResponse(
            response.Content,
            true,
            response.StatusCode);
        if (parsed != null && (parsed.Success || parsed.RateLimited))
        {
            return parsed;
        }

        if (!string.IsNullOrEmpty(response.ErrorCode) && response.StatusCode >= 200 && response.StatusCode < 300)
        {
            return BuildCodexResetCreditsError(true, ServiceHealthState.Unreachable, response.ErrorCode, "响应不可用");
        }

        return BuildCodexResetCreditsError(
            true,
            response.StatusCode == 429
                ? ServiceHealthState.Degraded
                : (response.StatusCode == 401 || response.StatusCode == 403
                    ? ServiceHealthState.Unavailable
                    : ServiceHealthState.Unreachable),
            response.StatusCode.ToString(CultureInfo.InvariantCulture),
            GetCodexProviderUsageHttpErrorReason(response.StatusCode));
    }

    private static CodexResetCreditsResult ParseCodexResetCreditsResponse(
        string content,
        bool tokenConfigured,
        int statusCode)
    {
        if (statusCode == 429)
        {
            return BuildCodexResetCreditsError(tokenConfigured, ServiceHealthState.Degraded, "429", "限流");
        }

        if (statusCode < 200 || statusCode >= 300)
        {
            return BuildCodexResetCreditsError(
                tokenConfigured,
                statusCode == 401 || statusCode == 403 ? ServiceHealthState.Unavailable : ServiceHealthState.Unreachable,
                statusCode.ToString(CultureInfo.InvariantCulture),
                GetCodexProviderUsageHttpErrorReason(statusCode));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return BuildCodexResetCreditsError(tokenConfigured, ServiceHealthState.Incomplete, "EMPTY", "空响应");
        }

        object root;
        try
        {
            root = BoundedHttpTextReader
                .CreateJsonSerializer(BoundedHttpTextReader.AuthenticatedJsonMaxBytes)
                .DeserializeObject(content);
        }
        catch
        {
            return BuildCodexResetCreditsError(tokenConfigured, ServiceHealthState.Incomplete, "PARSE", "解析失败");
        }

        object creditsObject = FindJsonMemberIgnoreCase(root, "credits", 0);
        object[] credits = creditsObject as object[];
        if (credits == null && creditsObject != null)
        {
            credits = FindJsonMemberIgnoreCase(creditsObject, "data", 0) as object[];
        }

        if (credits != null && credits.Length > 512)
        {
            return BuildCodexResetCreditsError(
                tokenConfigured,
                ServiceHealthState.Incomplete,
                "ARRAY_TOO_LARGE",
                "响应记录过多");
        }

        if (credits == null)
        {
            credits = root as object[];
        }

        if (credits == null)
        {
            return BuildCodexResetCreditsError(tokenConfigured, ServiceHealthState.Incomplete, "NO_CREDITS", "数据不完整");
        }

        DateTime nowUtc = DateTime.UtcNow;
        List<DateTime> expirations = new List<DateTime>();
        bool allExpirationTimesKnown = true;
        for (int i = 0; i < credits.Length; i++)
        {
            DateTime expirationUtc;
            if (TryGetCodexResetCreditExpirationUtc(credits[i], out expirationUtc))
            {
                expirations.Add(expirationUtc);
            }
            else
            {
                allExpirationTimesKnown = false;
            }
        }

        expirations.Sort();
        CodexResetCreditsSnapshot snapshot = CodexResetCreditsSnapshot.CreateDefault();
        snapshot.Known = true;
        snapshot.TokenConfigured = tokenConfigured;
        snapshot.ReportedCount = credits.Length;
        snapshot.AllExpirationTimesKnown = allExpirationTimesKnown;
        snapshot.ExpirationTimesUtc = expirations;
        snapshot.SourceUpdatedUtc = nowUtc;
        snapshot.ErrorCode = string.Empty;
        snapshot.ErrorMessage = string.Empty;
        return new CodexResetCreditsResult
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

    private static object FindJsonMemberIgnoreCase(object node, string key, int depth)
    {
        if (node == null || string.IsNullOrEmpty(key) || depth > 6)
        {
            return null;
        }

        Dictionary<string, object> dictionary = node as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                object nested = FindJsonMemberIgnoreCase(pair.Value, key, depth + 1);
                if (nested != null)
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
                object nested = FindJsonMemberIgnoreCase(array[i], key, depth + 1);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryGetCodexResetCreditExpirationUtc(object node, out DateTime expirationUtc)
    {
        expirationUtc = DateTime.MinValue;
        return TryGetCodexResetCreditExpirationUtc(node, 0, out expirationUtc);
    }

    private static bool TryGetCodexResetCreditExpirationUtc(object node, int depth, out DateTime expirationUtc)
    {
        expirationUtc = DateTime.MinValue;
        if (node == null || depth > 4)
        {
            return false;
        }

        Dictionary<string, object> dictionary = node as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (IsCodexResetCreditExpirationKey(pair.Key) &&
                    TryReadJsonDateUtc(pair.Value, out expirationUtc))
                {
                    return true;
                }
            }

            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (TryGetCodexResetCreditExpirationUtc(pair.Value, depth + 1, out expirationUtc))
                {
                    return true;
                }
            }
        }

        object[] array = node as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (TryGetCodexResetCreditExpirationUtc(array[i], depth + 1, out expirationUtc))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCodexResetCreditExpirationKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string normalized = Regex.Replace(key, "[^a-z0-9]+", string.Empty).ToLowerInvariant();
        return normalized.IndexOf("expir", StringComparison.Ordinal) >= 0 ||
            string.Equals(normalized, "validuntil", StringComparison.Ordinal);
    }

    private static bool TryReadJsonDateUtc(object value, out DateTime utc)
    {
        utc = DateTime.MinValue;
        DateTime local;
        if (TryReadQuotaDate(value, out local))
        {
            utc = local.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static CodexResetCreditsResult BuildCodexResetCreditsError(
        bool tokenConfigured,
        ServiceHealthState health,
        string errorCode,
        string message)
    {
        return new CodexResetCreditsResult
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

    private static CodexProviderUsageResult ReadCodexProviderUsage(
        WidgetSettings settings,
        CancellationToken cancellationToken)
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

        BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
            request,
            BoundedHttpTextReader.AuthenticatedJsonMaxBytes,
            CodexProviderUsageTimeoutMs,
            cancellationToken);
        if (response.StatusCode <= 0)
        {
            return BuildCodexProviderUsageError(true, ServiceHealthState.Unreachable, response.ErrorCode, "无法连接");
        }

        CodexProviderUsageResult parsed = ParseCodexProviderUsageResponse(
            response.Content,
            true,
            response.StatusCode,
            response.Bytes);
        if (parsed != null && (parsed.Success || parsed.RateLimited))
        {
            return parsed;
        }

        if (!string.IsNullOrEmpty(response.ErrorCode) && response.StatusCode >= 200 && response.StatusCode < 300)
        {
            return BuildCodexProviderUsageError(true, ServiceHealthState.Unreachable, response.ErrorCode, "响应不可用");
        }

        return BuildCodexProviderUsageError(
            true,
            response.StatusCode == 429
                ? ServiceHealthState.Degraded
                : (response.StatusCode == 401 || response.StatusCode == 403
                    ? ServiceHealthState.Unavailable
                    : ServiceHealthState.Unreachable),
            response.StatusCode.ToString(CultureInfo.InvariantCulture),
            GetCodexProviderUsageHttpErrorReason(response.StatusCode));
    }

    private static CodexProviderUsageResult ParseCodexProviderUsageResponse(
        string content,
        bool tokenConfigured,
        int statusCode)
    {
        return ParseCodexProviderUsageResponse(
            content,
            tokenConfigured,
            statusCode,
            string.IsNullOrEmpty(content) ? 0 : SharedEncoding.Utf8NoBom.GetByteCount(content));
    }

    private static CodexProviderUsageResult ParseCodexProviderUsageResponse(
        string content,
        bool tokenConfigured,
        int statusCode,
        int responseBytes)
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
            root = BoundedHttpTextReader
                .CreateJsonSerializer(BoundedHttpTextReader.AuthenticatedJsonMaxBytes)
                .DeserializeObject(content) as Dictionary<string, object>;
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

        ApplyFiveHourLimitAbsence(snapshot);

        snapshot.SourceUpdatedUtc = DateTime.UtcNow;
        snapshot.SourceUpdatedKnown = true;
        snapshot.ProviderHttpStatus = statusCode;
        snapshot.ProviderResponseBytes = Math.Max(0, responseBytes);
        snapshot.ProviderResponseBodySha256 = ComputeSha256Hex(content);
        snapshot.ProviderPlan = ResolveProviderDiagnosticValue(root, rateLimit, "plan_type", "account_plan");
        snapshot.ProviderPool = ResolveProviderDiagnosticValue(root, rateLimit, "pool_type", "pool");
        snapshot.ProviderCorrelationId = Guid.NewGuid().ToString("N");
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

    private static string ResolveProviderDiagnosticValue(
        Dictionary<string, object> root,
        Dictionary<string, object> rateLimit,
        string primaryKey,
        string alternateKey)
    {
        string value = GetQuotaString(rateLimit, primaryKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = GetQuotaString(rateLimit, alternateKey);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = GetQuotaString(root, primaryKey);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = GetQuotaString(root, alternateKey);
        }

        return NormalizeProviderDiagnosticEnum(value);
    }

    private static string ComputeSha256Hex(string content)
    {
        byte[] bytes = SharedEncoding.Utf8NoBom.GetBytes(content ?? string.Empty);
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(bytes);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
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

        // Route by the window's actual duration when the payload declares one. Since the provider
        // temporarily lifted the 5h limit, "primary_window" can BE the weekly window
        // (limit_window_seconds=604800) with secondary_window null - positional mapping then poured
        // the weekly quota into the five-hour ring. Slot position is only a fallback for payloads
        // that do not carry a duration.
        double windowSeconds;
        bool windowKnown = TryGetQuotaNumber(slot, "limit_window_seconds", out windowSeconds) ||
            TryGetQuotaNumber(slot, "window_seconds", out windowSeconds);
        if (!windowKnown)
        {
            double windowMinutes;
            if (TryGetQuotaNumber(slot, "window_minutes", out windowMinutes) ||
                TryGetQuotaNumber(slot, "limit_window_minutes", out windowMinutes))
            {
                windowSeconds = windowMinutes * 60.0;
                windowKnown = true;
            }
        }

        if (windowKnown && windowSeconds > 0.0)
        {
            fiveHour = windowSeconds <= FiveHourWindowRouteMaxSeconds;
        }

        // For duration-less positional guesses only: do not overwrite an already-routed ring while
        // the other ring is still empty. A slot whose declared duration routed it must stay on that
        // ring - a second weekly-length window must never spill into the five-hour ring.
        if (!windowKnown)
        {
            if (fiveHour && snapshot.FiveHourUsageDiagnosticKnown && !snapshot.WeeklyUsageDiagnosticKnown)
            {
                fiveHour = false;
            }
            else if (!fiveHour && snapshot.WeeklyUsageDiagnosticKnown && !snapshot.FiveHourUsageDiagnosticKnown)
            {
                fiveHour = true;
            }
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
            string parsedToken;
            return TryReadCodexAccessTokenFile(path, out parsedToken)
                ? parsedToken
                : string.Empty;
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

    private static bool TryReadCodexAccessTokenFile(string path, out string token)
    {
        token = string.Empty;
        string content;
        if (!TryReadBoundedUtf8File(path, CodexAuthJsonMaxBytes, out content))
        {
            return false;
        }

        return TryParseCodexAccessTokenJson(content, out token);
    }

    private static bool TryReadBoundedUtf8File(string path, int maxBytes, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0)
        {
            return false;
        }

        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes)
            {
                return false;
            }

            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream buffer = new MemoryStream((int)Math.Min(info.Length, maxBytes)))
            {
                byte[] chunk = new byte[8192];
                int total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return false;
                    }

                    buffer.Write(chunk, 0, read);
                }

                content = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                return true;
            }
        }
        catch
        {
            content = string.Empty;
            return false;
        }
    }

    private static bool TryParseCodexAccessTokenJson(string content, out string token)
    {
        token = string.Empty;
        Dictionary<string, object> root;
        try
        {
            root = BoundedHttpTextReader
                .CreateJsonSerializer(CodexAuthJsonMaxBytes)
                .DeserializeObject(content ?? string.Empty) as Dictionary<string, object>;
        }
        catch
        {
            return false;
        }

        if (root == null)
        {
            return false;
        }

        List<string> candidates = new List<string>();
        AddKnownCodexTokenCandidate(root, "access_token", candidates);
        Dictionary<string, object> tokens = GetQuotaObject(root, "tokens");
        AddKnownCodexTokenCandidate(tokens, "access_token", candidates);

        string selected = string.Empty;
        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = candidates[i];
            if (selected.Length == 0)
            {
                selected = candidate;
            }
            else if (!string.Equals(selected, candidate, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!IsPlausibleCodexAccessToken(selected) || !HasExpectedCodexJwtClaims(selected))
        {
            return false;
        }

        token = selected;
        return true;
    }

    private static void AddKnownCodexTokenCandidate(
        Dictionary<string, object> source,
        string key,
        List<string> candidates)
    {
        object value;
        if (source == null || candidates == null || !source.TryGetValue(key, out value))
        {
            return;
        }

        string candidate = value as string;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            candidates.Add(candidate.Trim());
        }
    }

    private static bool IsPlausibleCodexAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > CodexAccessTokenMaxChars)
        {
            return false;
        }

        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsWhiteSpace(token[i]) || char.IsControl(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExpectedCodexJwtClaims(string token)
    {
        string[] segments = (token ?? string.Empty).Split('.');
        if (segments.Length != 3)
        {
            // Older installations may persist an opaque access token. It is sent only to the
            // fixed ChatGPT origin and is never logged, so JWT claim checks do not apply.
            return true;
        }

        string payloadJson;
        if (!TryDecodeJwtSegment(segments[1], out payloadJson))
        {
            return false;
        }

        Dictionary<string, object> payload;
        try
        {
            payload = BoundedHttpTextReader
                .CreateJsonSerializer(BoundedHttpTextReader.TinyProbeMaxBytes)
                .DeserializeObject(payloadJson) as Dictionary<string, object>;
        }
        catch
        {
            return false;
        }

        string issuer = GetQuotaString(payload, "iss").TrimEnd('/');
        bool issuerExpected = string.Equals(issuer, "https://auth.openai.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(issuer, "https://auth0.openai.com", StringComparison.OrdinalIgnoreCase);
        object audience;
        bool audienceExpected = payload != null &&
            payload.TryGetValue("aud", out audience) &&
            IsExpectedCodexAudience(audience);
        return issuerExpected && audienceExpected;
    }

    private static bool IsExpectedCodexAudience(object value)
    {
        string text = value as string;
        if (text != null)
        {
            return string.Equals(text.TrimEnd('/'), "https://api.openai.com/v1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text.TrimEnd('/'), "https://api.openai.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "openai-api", StringComparison.OrdinalIgnoreCase);
        }

        object[] values = value as object[];
        for (int i = 0; values != null && i < Math.Min(values.Length, 32); i++)
        {
            if (IsExpectedCodexAudience(values[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeJwtSegment(string segment, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(segment) || segment.Length > BoundedHttpTextReader.TinyProbeMaxBytes)
        {
            return false;
        }

        try
        {
            string value = segment.Replace('-', '+').Replace('_', '/');
            int remainder = value.Length % 4;
            if (remainder == 1)
            {
                return false;
            }

            if (remainder > 0)
            {
                value = value.PadRight(value.Length + (4 - remainder), '=');
            }

            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length > BoundedHttpTextReader.TinyProbeMaxBytes)
            {
                return false;
            }

            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EncodeJwtFixtureSegment(string value)
    {
        return Convert.ToBase64String(SharedEncoding.Utf8NoBom.GetBytes(value ?? string.Empty))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void RunCodexAuthJsonSelfTest()
    {
        string validJwt = EncodeJwtFixtureSegment("{\"alg\":\"none\"}") + "." +
            EncodeJwtFixtureSegment("{\"iss\":\"https://auth.openai.com\",\"aud\":[\"https://api.openai.com/v1\"]}") +
            ".fixture";
        string token;
        if (!TryParseCodexAccessTokenJson(
                "{\"tokens\":{\"access_token\":\"" + validJwt + "\"}}",
                out token) ||
            !string.Equals(token, validJwt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex auth self-test: known token path failed.");
        }

        if (!TryParseCodexAccessTokenJson("{\"access_token\":\"opaque-fixture-token\"}", out token) ||
            !string.Equals(token, "opaque-fixture-token", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex auth self-test: legacy root token path failed.");
        }

        string[] rejected = new string[]
        {
            "{\"profile\":{\"access_token\":\"nested-decoy\"}}",
            "{\"accessToken\":\"unknown-casing\"}",
            "{\"access_token\":\"first\",\"tokens\":{\"access_token\":\"second\"}}",
            "{\"tokens\":{\"access_token\":\"" +
                EncodeJwtFixtureSegment("{\"alg\":\"none\"}") + "." +
                EncodeJwtFixtureSegment("{\"iss\":\"https://attacker.invalid\",\"aud\":\"https://api.openai.com/v1\"}") +
                ".fixture\"}}"
        };
        for (int i = 0; i < rejected.Length; i++)
        {
            if (TryParseCodexAccessTokenJson(rejected[i], out token))
            {
                throw new InvalidOperationException("Codex auth self-test: unsafe auth schema was accepted.");
            }
        }

        string directory = Path.Combine(Path.GetTempPath(), "CodexAuthSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string oversizedPath = Path.Combine(directory, "auth.json");
            File.WriteAllBytes(oversizedPath, new byte[CodexAuthJsonMaxBytes + 1]);
            DateTime writeUtc = File.GetLastWriteTimeUtc(oversizedPath);
            if (TryReadCodexAccessTokenFile(oversizedPath, out token) ||
                File.GetLastWriteTimeUtc(oversizedPath) != writeUtc)
            {
                throw new InvalidOperationException("Codex auth self-test: oversized file was read or modified.");
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }

    }

    private static void RunCodexResetCreditsSelfTest()
    {
        DateTime nowUtc = new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        string content = "{\"credits\":[" +
            "{\"issued_at\":\"2026-07-01T00:00:00Z\",\"expires_at\":\"2026-07-08T17:00:00Z\"}," +
            "{\"created_at\":\"2026-07-01T00:00:00Z\",\"expiresAt\":\"2026-07-10T01:00:00Z\"}," +
            "{\"grant\":{\"valid_until\":\"2026-07-09T00:30:00Z\"}}" +
            "]}";
        CodexResetCreditsResult result = ParseCodexResetCreditsResponse(content, true, 200);
        if (result == null ||
            !result.Success ||
            result.Snapshot == null ||
            result.Snapshot.GetActiveCount(nowUtc) != 3 ||
            !string.Equals(BuildCodexResetCreditsDisplayText(result.Snapshot, nowUtc), "RS:3-17h", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex reset credits self-test failed: hourly display.");
        }

        DateTime lateUtc = new DateTime(2026, 7, 8, 18, 0, 0, DateTimeKind.Utc);
        if (!string.Equals(BuildCodexResetCreditsDisplayText(result.Snapshot, lateUtc), "RS:2-7h", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex reset credits self-test failed: expired-card filtering.");
        }

        CodexResetCreditsSnapshot daySnapshot = CodexResetCreditsSnapshot.CreateDefault();
        daySnapshot.Known = true;
        daySnapshot.ReportedCount = 1;
        daySnapshot.AllExpirationTimesKnown = true;
        daySnapshot.ExpirationTimesUtc.Add(nowUtc.AddHours(25.0));
        if (!string.Equals(BuildCodexResetCreditsDisplayText(daySnapshot, nowUtc), "RS:1-2d", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex reset credits self-test failed: day display.");
        }
    }
}
