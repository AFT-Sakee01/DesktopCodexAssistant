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

    private static bool lastInsideGfw;
    private static string lastGfwSignalReason = string.Empty;
    private static DateTime lastGfwSignalUtc = DateTime.MinValue;
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

        if (settings.AiRequestProtectionAutoEnabled && HasActiveGfwSignal(out trigger))
        {
            reason = "GFW 自动 AI 阻断: " + trigger;
            LogBlockedRequest(uri.Host, "auto", reason);
            return true;
        }

        return false;
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
