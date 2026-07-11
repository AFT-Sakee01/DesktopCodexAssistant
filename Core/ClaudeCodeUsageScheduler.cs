using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

internal sealed class ClaudeCodeUsageSchedulerOutcome
{
    public ClaudeCodeUsageReadResult Result { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Trigger { get; set; }
}

internal static class ClaudeCodeUsageScheduler
{
    private static readonly object SyncRoot = new object();
    private static readonly HashSet<string> JoinedConsumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static Task<ClaudeCodeUsageSchedulerOutcome> runningTask;
    private static DateTime nextRefreshUtc = DateTime.MinValue;
    private static string pendingTrigger = "首次刷新";
    private static bool expectedCredentialMissCompletionLogged;
    private static string lastErrorCode = string.Empty;

    public static bool IsRequestRunning
    {
        get
        {
            lock (SyncRoot)
            {
                return runningTask != null && !runningTask.IsCompleted;
            }
        }
    }

    public static string LastErrorCode
    {
        get
        {
            lock (SyncRoot)
            {
                return lastErrorCode;
            }
        }
    }

    public static void ClearLastError()
    {
        lock (SyncRoot)
        {
            lastErrorCode = string.Empty;
        }
    }

    public static void RequestRefresh(string trigger)
    {
        lock (SyncRoot)
        {
            nextRefreshUtc = DateTime.MinValue;
            pendingTrigger = NormalizeTrigger(trigger, "强制刷新");
        }
    }

    public static bool TryStartOrJoin(
        string consumerId,
        WidgetSettings settings,
        string trigger,
        out Task<ClaudeCodeUsageSchedulerOutcome> task)
    {
        task = null;
        if (settings == null)
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            if (runningTask != null && !runningTask.IsCompleted)
            {
                string consumerKey = NormalizeConsumerId(consumerId);
                if (JoinedConsumers.Contains(consumerKey))
                {
                    return false;
                }

                JoinedConsumers.Add(consumerKey);
                task = runningTask;
                return true;
            }

            if (nextRefreshUtc != DateTime.MinValue && nowUtc < nextRefreshUtc)
            {
                return false;
            }

            WidgetSettings requestSettings = settings.Clone();
            string effectiveTrigger = NormalizeTrigger(
                string.IsNullOrWhiteSpace(pendingTrigger) || string.Equals(pendingTrigger, "定时间隔", StringComparison.Ordinal)
                    ? trigger
                    : pendingTrigger,
                "定时间隔");
            pendingTrigger = "定时间隔";
            JoinedConsumers.Clear();
            JoinedConsumers.Add(NormalizeConsumerId(consumerId));
            runningTask = Task.Run(delegate
            {
                return ExecuteRequest(requestSettings, effectiveTrigger);
            });
            task = runningTask;
            return true;
        }
    }

    private static ClaudeCodeUsageSchedulerOutcome ExecuteRequest(WidgetSettings settings, string trigger)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ClaudeCodeUsageReadResult result;
        bool setupTokenConfigured = !string.IsNullOrWhiteSpace(ClaudeCodeUsageReader.ReadConfiguredSetupToken());
        Program.LogInfo(
            "Claude Code usage scheduler started. Trigger=" + NormalizeTrigger(trigger, "定时间隔") +
            ", Source=" + (setupTokenConfigured ? "api.anthropic.com" : "statusline-cache"));

        try
        {
            result = ClaudeCodeUsageReader.Read(settings);
            if (result != null && result.Success && result.Snapshot != null)
            {
                ClaudeCodeUsageReader.TryWriteQuotaCache(result.Snapshot);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            result = BuildSchedulerError();
        }

        stopwatch.Stop();
        if (result == null)
        {
            result = BuildSchedulerError();
        }

        DateTime nextUtc = DateTime.UtcNow.AddSeconds(
            result.Success
                ? ClaudeCodeUsageReader.NormalRefreshSeconds
                : (result.RateLimited ? ClaudeCodeUsageReader.RateLimitRefreshSeconds : ClaudeCodeUsageReader.ErrorRefreshSeconds));
        lock (SyncRoot)
        {
            nextRefreshUtc = nextUtc;
            runningTask = null;
            JoinedConsumers.Clear();
            lastErrorCode = result.Success ? string.Empty : (result.ErrorCode ?? string.Empty);
        }

        if (ShouldLogCompletion(result))
        {
            Program.LogInfo(
                "Claude Code usage scheduler completed. Success=" + result.Success.ToString() +
                ", State=" + result.State.ToString() +
                ", ErrorCode=" + (result.ErrorCode ?? string.Empty) +
                ", ElapsedMs=" + stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new ClaudeCodeUsageSchedulerOutcome
        {
            Result = result,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Trigger = NormalizeTrigger(trigger, "定时间隔")
        };
    }

    private static ClaudeCodeUsageReadResult BuildSchedulerError()
    {
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = false,
            Success = false,
            RateLimited = false,
            Snapshot = null,
            State = ClaudeRadarServiceState.Unreachable,
            ErrorCode = "ERROR",
            ErrorMessage = "请求失败"
        };
    }

    private static bool ShouldLogCompletion(ClaudeCodeUsageReadResult result)
    {
        lock (SyncRoot)
        {
            if (result != null && result.Success)
            {
                expectedCredentialMissCompletionLogged = false;
                return true;
            }

            if (result != null &&
                (string.Equals(result.ErrorCode, "NO_SETUP_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(result.ErrorCode, "NO_STATUSLINE_CACHE", StringComparison.OrdinalIgnoreCase)))
            {
                // An unbound desktop session is stable until the user adds a token or a statusline
                // snapshot appears. Log the first miss, then stay quiet until success resets it.
                if (expectedCredentialMissCompletionLogged)
                {
                    return false;
                }

                expectedCredentialMissCompletionLogged = true;
            }

            return true;
        }
    }

    internal static void RunSelfTest()
    {
        bool previousState;
        lock (SyncRoot)
        {
            previousState = expectedCredentialMissCompletionLogged;
            expectedCredentialMissCompletionLogged = false;
        }

        try
        {
            ClaudeCodeUsageReadResult missingCache = new ClaudeCodeUsageReadResult
            {
                Success = false,
                ErrorCode = "NO_SETUP_TOKEN"
            };
            ClaudeCodeUsageReadResult success = new ClaudeCodeUsageReadResult
            {
                Success = true,
                ErrorCode = string.Empty
            };

            if (!ShouldLogCompletion(missingCache) ||
                ShouldLogCompletion(missingCache) ||
                !ShouldLogCompletion(success) ||
                !ShouldLogCompletion(missingCache))
            {
                throw new InvalidOperationException("Claude Code usage scheduler completion-log suppression self-test failed.");
            }
        }
        finally
        {
            lock (SyncRoot)
            {
                expectedCredentialMissCompletionLogged = previousState;
            }
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
}
