using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Threading;

internal static class AiRequestProtection
{
    private static readonly object SyncRoot = new object();
    private static readonly TimeSpan GfwSignalTtl = TimeSpan.FromHours(6.0);
    private static readonly TimeSpan BlockLogInterval = TimeSpan.FromMinutes(5.0);
    private static readonly string[] SensitiveHostSuffixes = new string[]
    {
        "openai.com",
        "chatgpt.com",
        "oaistatic.com",
        "oaiusercontent.com",
        "anthropic.com",
        "claude.ai",
        "claude.com"
    };

    private static readonly TimeSpan EgressSignalTtl = TimeSpan.FromMinutes(10.0);
    private static readonly string[] ChinaCountryTokens = new string[] { "cn", "china", "中国", "中华人民共和国" };
    // Regions that carry a China-adjacent name but are served normally; they must never be treated
    // as mainland China by the guard.
    private static readonly string[] NonMainlandTokens = new string[]
    {
        "hong kong", "hongkong", "hk", "macau", "macao", "mo", "taiwan", "tw",
        "香港", "澳门", "澳門", "台湾", "臺灣", "台灣"
    };

    private static bool lastInsideGfw;
    private static string lastGfwSignalReason = string.Empty;
    private static DateTime lastGfwSignalUtc = DateTime.MinValue;
    private static bool lastEgressKnown;
    private static bool lastEgressMainlandChina;
    private static string lastEgressCountry = string.Empty;
    private static DateTime lastEgressSignalUtc = DateTime.MinValue;
    private static bool egressSignalInitialized;
    private static string lastLoggedBlockKey = string.Empty;
    private static DateTime lastLoggedBlockUtc = DateTime.MinValue;

    public static void UpdateGfwSignal(bool insideGfw, string reason)
    {
        reason = NormalizeReason(reason);
        bool changed = false;
        lock (SyncRoot)
        {
            changed = lastGfwSignalUtc == DateTime.MinValue ||
                lastInsideGfw != insideGfw ||
                !string.Equals(lastGfwSignalReason, reason, StringComparison.Ordinal);
            lastInsideGfw = insideGfw;
            lastGfwSignalReason = reason;
            lastGfwSignalUtc = DateTime.UtcNow;
        }

        if (changed)
        {
            Program.LogInfo(
                "AI request protection GFW signal updated. InsideGfw=" +
                insideGfw.ToString() +
                ", Reason=" +
                reason);
        }
    }

    // Reports the egress IP country so the China guard can decide whether this app may talk to
    // Anthropic/OpenAI. Fed once per widget tick from the shared cleanip snapshot. egressKnown is
    // false when the lookup has not resolved yet or failed, which the guard treats as "not
    // confirmed outside China" (fail-closed).
    public static void UpdateEgressSignal(
        bool egressKnown,
        bool mainlandChina,
        string country,
        DateTime observedUtc)
    {
        observedUtc = NormalizeUtc(observedUtc);
        if (observedUtc == DateTime.MinValue)
        {
            egressKnown = false;
        }

        mainlandChina = egressKnown && mainlandChina;
        country = egressKnown ? (country ?? string.Empty).Trim() : string.Empty;
        bool changed;
        lock (SyncRoot)
        {
            changed = !egressSignalInitialized ||
                lastEgressKnown != egressKnown ||
                lastEgressMainlandChina != mainlandChina ||
                !string.Equals(lastEgressCountry, country, StringComparison.OrdinalIgnoreCase);
            egressSignalInitialized = true;
            lastEgressKnown = egressKnown;
            lastEgressMainlandChina = mainlandChina;
            lastEgressCountry = country;
            // Keep the provider observation time rather than the Widget tick time. Re-reporting
            // an old clone every tick must not keep a stale country result fresh indefinitely.
            lastEgressSignalUtc = observedUtc;
        }

        if (changed)
        {
            Program.LogInfo(
                "AI request protection egress signal updated. Known=" + egressKnown.ToString() +
                ", MainlandChina=" + mainlandChina.ToString() +
                ", Country=" + country);
        }
    }

    // Windows can announce an interface/address transition before the replacement Clean IP
    // lookup completes. Invalidate the old country immediately so a Japan result cannot briefly
    // authorize AI traffic after the machine has joined a different network.
    public static void InvalidateEgressSignal(string reason)
    {
        bool changed;
        lock (SyncRoot)
        {
            changed = egressSignalInitialized &&
                (lastEgressSignalUtc != DateTime.MinValue ||
                lastEgressKnown ||
                lastEgressMainlandChina ||
                lastEgressCountry.Length != 0);
            egressSignalInitialized = true;
            lastEgressKnown = false;
            lastEgressMainlandChina = false;
            lastEgressCountry = string.Empty;
            lastEgressSignalUtc = DateTime.MinValue;
            lastLoggedBlockKey = string.Empty;
            lastLoggedBlockUtc = DateTime.MinValue;
        }

        if (changed)
        {
            Program.LogInfo("AI request protection egress signal invalidated. Reason=" + NormalizeReason(reason));
        }
    }

    public static bool ShouldBlock(WidgetSettings settings, string url, out string reason)
    {
        reason = string.Empty;
        Uri uri;
        if (settings == null ||
            string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
            !IsSensitiveAiHost(uri.Host))
        {
            return false;
        }

        string trigger;
        if (settings.AiRequestProtectionManualBlockEnabled)
        {
            trigger = "manual";
            reason = "手动 AI 阻断";
            LogBlockedRequest(uri.Host, trigger, reason);
            return true;
        }

        // Fail-closed China-egress guard: this app must not reach out to Anthropic/OpenAI unless
        // the egress IP is positively confirmed to be outside mainland China. Unknown or stale
        // location counts as "not confirmed" and blocks, because being unsure while physically in
        // China is exactly the accident this guards against.
        if (settings.AiChinaEgressGuardEnabled && !IsEgressConfirmedOutsideChina(out trigger))
        {
            reason = "中国大陆出口保护: " + trigger;
            LogBlockedRequest(uri.Host, "china_egress", reason);
            return true;
        }

        if (settings.AiRequestProtectionAutoEnabled && HasActiveGfwSignal(out trigger))
        {
            reason = "GFW 自动 AI 阻断: " + trigger;
            LogBlockedRequest(uri.Host, "auto", reason);
            return true;
        }

        return false;
    }

    // The block direction is fail-closed; the warning direction is precise. A full-screen warning
    // fires only on a positive China indication (confirmed mainland egress, or a confirmed
    // inside-the-wall GFW signal), never on a merely-unknown egress, so travellers in Japan are
    // not alarmed during the brief window before the first geolocation resolves.
    public static bool ShouldWarnChinaEgress(WidgetSettings settings, out string reason)
    {
        reason = string.Empty;
        if (settings == null || !settings.AiChinaEgressGuardEnabled)
        {
            return false;
        }

        lock (SyncRoot)
        {
            bool egressFresh = lastEgressSignalUtc != DateTime.MinValue &&
                (DateTime.UtcNow - lastEgressSignalUtc) <= EgressSignalTtl;
            if (egressFresh && lastEgressKnown && lastEgressMainlandChina)
            {
                reason = "出口 IP 位于中国大陆" +
                    (lastEgressCountry.Length == 0 || string.Equals(lastEgressCountry, "未提供原因", StringComparison.Ordinal)
                        ? string.Empty
                        : "（" + lastEgressCountry + "）");
                return true;
            }

            bool gfwFresh = lastGfwSignalUtc != DateTime.MinValue &&
                (DateTime.UtcNow - lastGfwSignalUtc) <= GfwSignalTtl;
            if (gfwFresh && lastInsideGfw)
            {
                reason = "网络处于 GFW 墙内";
                return true;
            }
        }

        return false;
    }

    // True only when we positively know the egress is a non-China IP and no inside-the-wall signal
    // is active. Any other state (unknown, stale, mainland China, or behind the wall) returns false
    // so ShouldBlock stays fail-closed.
    private static bool IsEgressConfirmedOutsideChina(out string reason)
    {
        lock (SyncRoot)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (lastGfwSignalUtc != DateTime.MinValue &&
                (nowUtc - lastGfwSignalUtc) <= GfwSignalTtl &&
                lastInsideGfw)
            {
                reason = "GFW 墙内";
                return false;
            }

            if (lastEgressSignalUtc == DateTime.MinValue ||
                (nowUtc - lastEgressSignalUtc) > EgressSignalTtl)
            {
                reason = "出口位置未知";
                return false;
            }

            if (!lastEgressKnown)
            {
                reason = "出口位置未确认";
                return false;
            }

            if (lastEgressMainlandChina)
            {
                reason = "出口位于中国大陆";
                return false;
            }

            reason = "出口已确认在境外";
            return true;
        }
    }

    public static bool HasConfirmedOutsideChinaEgress()
    {
        string reason;
        return IsEgressConfirmedOutsideChina(out reason);
    }

    // Country string comes straight from the geo provider and may be a code ("CN") or a name
    // ("China" / "中国"). HK/MO/TW carry China-adjacent names but are served normally, so they are
    // excluded explicitly.
    public static bool IsMainlandChinaEgress(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return false;
        }

        string value = country.Trim().ToLowerInvariant();
        for (int i = 0; i < NonMainlandTokens.Length; i++)
        {
            if (MatchesCountryToken(value, NonMainlandTokens[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < ChinaCountryTokens.Length; i++)
        {
            string token = ChinaCountryTokens[i];
            if (MatchesCountryToken(value, token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCountryToken(string value, string token)
    {
        // Two-letter ISO codes must match the whole provider value. Treating "mo" or "cn" as a
        // substring would misclassify unrelated country names; longer localized names may appear
        // inside values such as "China (Mainland)" or "Hong Kong SAR, China".
        return token.Length <= 2
            ? string.Equals(value, token, StringComparison.Ordinal)
            : value.IndexOf(token, StringComparison.Ordinal) >= 0;
    }

    public static bool IsSensitiveAiHost(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < SensitiveHostSuffixes.Length; i++)
        {
            string suffix = SensitiveHostSuffixes[i];
            if (string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasActiveGfwSignal(out string reason)
    {
        lock (SyncRoot)
        {
            if (!lastInsideGfw || lastGfwSignalUtc == DateTime.MinValue)
            {
                reason = string.Empty;
                return false;
            }

            if ((DateTime.UtcNow - lastGfwSignalUtc) > GfwSignalTtl)
            {
                reason = string.Empty;
                return false;
            }

            reason = NormalizeReason(lastGfwSignalReason);
            return true;
        }
    }

    private static void LogBlockedRequest(string host, string trigger, string reason)
    {
        host = NormalizeHost(host);
        reason = NormalizeReason(reason);
        string key = host + "|" + trigger + "|" + reason;
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            if (string.Equals(lastLoggedBlockKey, key, StringComparison.Ordinal) &&
                (nowUtc - lastLoggedBlockUtc) < BlockLogInterval)
            {
                return;
            }

            lastLoggedBlockKey = key;
            lastLoggedBlockUtc = nowUtc;
        }

        Program.LogInfo(
            "AI request blocked. Host=" +
            host +
            ", Trigger=" +
            trigger +
            ", Reason=" +
            reason);
    }

    private static string NormalizeHost(string host)
    {
        return (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string NormalizeReason(string reason)
    {
        reason = reason == null ? string.Empty : reason.Trim();
        return reason.Length == 0 ? "未提供原因" : reason;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Local);
        }

        return value.ToUniversalTime();
    }

    // Clears the process-wide signal state. Test-only: the guard reads static state that would
    // otherwise leak between self-test cases (and from real runtime into a test run).
    internal static void ResetSignalsForTest()
    {
        lock (SyncRoot)
        {
            lastInsideGfw = false;
            lastGfwSignalReason = string.Empty;
            lastGfwSignalUtc = DateTime.MinValue;
            lastEgressKnown = false;
            lastEgressMainlandChina = false;
            lastEgressCountry = string.Empty;
            lastEgressSignalUtc = DateTime.MinValue;
            egressSignalInitialized = false;
            lastLoggedBlockKey = string.Empty;
            lastLoggedBlockUtc = DateTime.MinValue;
        }
    }

    internal static void RunSelfTest()
    {
        // Country classification: mainland CN vs the served China-adjacent regions.
        if (!IsMainlandChinaEgress("CN") || !IsMainlandChinaEgress("China") || !IsMainlandChinaEgress("中国") ||
            !IsMainlandChinaEgress("China (Mainland)"))
        {
            throw new InvalidOperationException("AI guard self-test: mainland China egress must be recognized.");
        }

        if (IsMainlandChinaEgress("Hong Kong") || IsMainlandChinaEgress("Taiwan") || IsMainlandChinaEgress("Macau") ||
            IsMainlandChinaEgress("香港") || IsMainlandChinaEgress("台湾") || IsMainlandChinaEgress("Japan") ||
            IsMainlandChinaEgress("United States") || IsMainlandChinaEgress(string.Empty))
        {
            throw new InvalidOperationException("AI guard self-test: HK/MO/TW and non-China egress must not be treated as mainland.");
        }

        WidgetSettings guardOn = WidgetSettings.CreateDefaults();
        guardOn.AiRequestProtectionManualBlockEnabled = false;
        guardOn.AiRequestProtectionAutoEnabled = false;
        guardOn.AiChinaEgressGuardEnabled = true;

        string[] sensitiveUrls = new string[]
        {
            "https://api.anthropic.com/api/oauth/usage",
            "https://api.anthropic.com/v1/messages",
            "https://chatgpt.com/backend-api/wham/usage",
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits",
            "https://status.claude.com/api/v2/summary.json",
            "https://status.openai.com/api/v2/summary.json"
        };
        string aiUrl = sensitiveUrls[0];
        const string neutralUrl = "https://example.com/health";
        string reason;

        try
        {
            // Confirmed Japan egress: no block, no warning.
            ResetSignalsForTest();
            UpdateEgressSignal(true, false, "Japan", DateTime.UtcNow);
            for (int i = 0; i < sensitiveUrls.Length; i++)
            {
                if (ShouldBlock(guardOn, sensitiveUrls[i], out reason))
                {
                    throw new InvalidOperationException("AI guard self-test: confirmed non-China egress must not block AI hosts.");
                }
            }

            if (ShouldWarnChinaEgress(guardOn, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: confirmed non-China egress must not warn.");
            }

            // A non-AI host is never touched by the guard.
            if (ShouldBlock(guardOn, neutralUrl, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: neutral hosts must never be blocked.");
            }

            // Confirmed mainland China egress: block and warn.
            ResetSignalsForTest();
            UpdateEgressSignal(true, true, "China", DateTime.UtcNow);
            for (int i = 0; i < sensitiveUrls.Length; i++)
            {
                if (!ShouldBlock(guardOn, sensitiveUrls[i], out reason))
                {
                    throw new InvalidOperationException("AI guard self-test: mainland China egress must block every sensitive AI URL.");
                }
            }

            if (!ShouldWarnChinaEgress(guardOn, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: mainland China egress must block and warn.");
            }

            // Unknown egress: fail-closed block, but no warning (avoids Japan startup false alarms).
            ResetSignalsForTest();
            if (!ShouldBlock(guardOn, aiUrl, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: unknown egress must fail closed and block.");
            }

            if (ShouldWarnChinaEgress(guardOn, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: unknown egress must not raise the warning.");
            }

            // A stale non-China observation must not be renewed by the caller's current tick.
            ResetSignalsForTest();
            UpdateEgressSignal(true, false, "Japan", DateTime.UtcNow - EgressSignalTtl - TimeSpan.FromSeconds(1.0));
            if (!ShouldBlock(guardOn, aiUrl, out reason) || ShouldWarnChinaEgress(guardOn, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: stale non-China egress must fail closed without warning.");
            }

            // Network identity changes invalidate an otherwise-fresh outside-China observation.
            ResetSignalsForTest();
            UpdateEgressSignal(true, false, "Japan", DateTime.UtcNow);
            InvalidateEgressSignal("self-test network change");
            if (!ShouldBlock(guardOn, aiUrl, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: network change must invalidate prior egress authorization.");
            }

            // Inside-the-wall GFW signal alone: block and warn even when egress country is unknown.
            ResetSignalsForTest();
            UpdateGfwSignal(true, "probe blocked");
            if (!ShouldBlock(guardOn, aiUrl, out reason) || !ShouldWarnChinaEgress(guardOn, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: inside-the-wall signal must block and warn.");
            }

            // Guard disabled: falls back to prior behavior (no block from this path even in China).
            WidgetSettings guardOff = WidgetSettings.CreateDefaults();
            guardOff.AiRequestProtectionManualBlockEnabled = false;
            guardOff.AiRequestProtectionAutoEnabled = false;
            guardOff.AiChinaEgressGuardEnabled = false;
            ResetSignalsForTest();
            UpdateEgressSignal(true, true, "China", DateTime.UtcNow);
            if (ShouldBlock(guardOff, aiUrl, out reason) || ShouldWarnChinaEgress(guardOff, out reason))
            {
                throw new InvalidOperationException("AI guard self-test: a disabled guard must not block or warn.");
            }
        }
        finally
        {
            ResetSignalsForTest();
        }

        Console.WriteLine("AI request protection: PASS china-egress fail-closed warn-precise hk-tw-excluded guard-toggle");
    }
}

internal sealed class AiExternalToolBlockResult
{
    public int MatchedCount { get; set; }
    public int StoppedCount { get; set; }
    public int FailedCount { get; set; }
    public string Summary { get; set; }
}

internal static class AiExternalToolBlocker
{
    private const int GracefulCloseTimeoutMs = 1500;

    public static AiExternalToolBlockResult TryStopKnownTools()
    {
        AiExternalToolBlockResult result = new AiExternalToolBlockResult();
        List<string> stoppedNames = new List<string>();
        Process current = Process.GetCurrentProcess();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            result.Summary = "无法枚举进程";
            return result;
        }

        try
        {
            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];
                if (process == null)
                {
                    continue;
                }

                try
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    string commandLine;
                    string executablePath;
                    TryReadProcessCommandLine(process.Id, out commandLine, out executablePath);
                    if (!IsKnownAiToolProcess(process, commandLine, executablePath))
                    {
                        continue;
                    }

                    result.MatchedCount++;
                    string displayName = GetDisplayName(process, executablePath);
                    if (TryStopProcess(process))
                    {
                        result.StoppedCount++;
                        stoppedNames.Add(displayName);
                    }
                    else
                    {
                        result.FailedCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    Program.LogException(ex);
                }
            }
        }
        finally
        {
            for (int i = 0; i < processes.Length; i++)
            {
                if (processes[i] != null)
                {
                    processes[i].Dispose();
                }
            }

            current.Dispose();
        }

        if (result.MatchedCount == 0)
        {
            result.Summary = "未发现正在运行的 Codex 或 Claude Code 进程";
        }
        else if (result.FailedCount == 0)
        {
            result.Summary = "已停止 " + result.StoppedCount.ToString(CultureInfo.InvariantCulture) + " 个 AI 工具进程";
        }
        else
        {
            result.Summary =
                "已停止 " +
                result.StoppedCount.ToString(CultureInfo.InvariantCulture) +
                " 个，失败 " +
                result.FailedCount.ToString(CultureInfo.InvariantCulture) +
                " 个";
        }

        Program.LogInfo(
            "Manual AI external tool block completed. Matched=" +
            result.MatchedCount.ToString(CultureInfo.InvariantCulture) +
            ", Stopped=" +
            result.StoppedCount.ToString(CultureInfo.InvariantCulture) +
            ", Failed=" +
            result.FailedCount.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    private static bool IsKnownAiToolProcess(Process process, string commandLine, string executablePath)
    {
        string processName = process == null ? string.Empty : (process.ProcessName ?? string.Empty);
        string fileName = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetFileNameWithoutExtension(executablePath);
        string combined = (processName + " " + fileName + " " + (commandLine ?? string.Empty)).ToLowerInvariant();
        if (combined.IndexOf("desktopcodexassistant", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        return ContainsAiToolName(combined);
    }

    private static bool ContainsAiToolName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.IndexOf("claude-code", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("claude code", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("@anthropic-ai/claude-code", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("anthropic", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("claude", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("openai/codex", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("codex", StringComparison.Ordinal) >= 0;
    }

    private static bool TryStopProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            bool closeSent = false;
            try
            {
                closeSent = process.CloseMainWindow();
            }
            catch
            {
                closeSent = false;
            }

            if (closeSent && process.WaitForExit(GracefulCloseTimeoutMs))
            {
                return true;
            }

            if (!process.HasExited)
            {
                process.Kill();
            }

            return process.WaitForExit(GracefulCloseTimeoutMs) || process.HasExited;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    private static string GetDisplayName(Process process, string executablePath)
    {
        string name = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(name) && process != null)
        {
            name = process.ProcessName;
        }

        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private static bool TryReadProcessCommandLine(int processId, out string commandLine, out string executablePath)
    {
        commandLine = string.Empty;
        executablePath = string.Empty;
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = " +
                processId.ToString(CultureInfo.InvariantCulture)))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    using (item)
                    {
                        commandLine = Convert.ToString(item["CommandLine"], CultureInfo.InvariantCulture) ?? string.Empty;
                        executablePath = Convert.ToString(item["ExecutablePath"], CultureInfo.InvariantCulture) ?? string.Empty;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
