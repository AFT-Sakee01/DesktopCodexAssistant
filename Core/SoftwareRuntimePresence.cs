using System;
using System.Diagnostics;

internal sealed class SoftwareRuntimePresenceSnapshot
{
    public SoftwareRuntimePresenceSnapshot(
        bool codexRunning,
        bool claudeRunning,
        DateTime checkedUtc,
        string changedReason)
    {
        this.CodexRunning = codexRunning;
        this.ClaudeRunning = claudeRunning;
        this.CheckedUtc = checkedUtc;
        this.ChangedReason = changedReason ?? string.Empty;
    }

    public bool CodexRunning { get; private set; }
    public bool ClaudeRunning { get; private set; }
    public DateTime CheckedUtc { get; private set; }
    public string ChangedReason { get; private set; }

    public bool AnySupportedAppRunning
    {
        get { return this.CodexRunning || this.ClaudeRunning; }
    }

    public bool BothSupportedAppsRunning
    {
        get { return this.CodexRunning && this.ClaudeRunning; }
    }

    public static SoftwareRuntimePresenceSnapshot Empty()
    {
        return new SoftwareRuntimePresenceSnapshot(false, false, DateTime.MinValue, "未检测");
    }
}

internal static class SoftwareRuntimePresence
{
    private static readonly object SyncRoot = new object();
    private static readonly string[] CodexProcessNames = new string[] { "codex" };
    private static readonly string[] ClaudeProcessNames = new string[] { "claude", "claude-code", "anthropic" };
    private static SoftwareRuntimePresenceSnapshot cachedSnapshot = SoftwareRuntimePresenceSnapshot.Empty();

    public static SoftwareRuntimePresenceSnapshot GetSnapshot(WidgetPerformanceMode performanceMode, bool force)
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (SyncRoot)
        {
            if (!force &&
                cachedSnapshot.CheckedUtc != DateTime.MinValue &&
                (nowUtc - cachedSnapshot.CheckedUtc).TotalSeconds < GetPresenceRefreshSeconds(performanceMode))
            {
                return cachedSnapshot;
            }

            bool codexRunning = IsAnyProcessNameRunning(CodexProcessNames);
            bool claudeRunning = IsAnyProcessNameRunning(ClaudeProcessNames);
            string reason = BuildChangedReason(cachedSnapshot, codexRunning, claudeRunning);
            cachedSnapshot = new SoftwareRuntimePresenceSnapshot(codexRunning, claudeRunning, nowUtc, reason);
            return cachedSnapshot;
        }
    }

    public static double GetPresenceRefreshSeconds(WidgetPerformanceMode performanceMode)
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(performanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 3.0;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 10.0;
        }

        return 5.0;
    }

    private static string BuildChangedReason(
        SoftwareRuntimePresenceSnapshot previous,
        bool codexRunning,
        bool claudeRunning)
    {
        if (previous == null || previous.CheckedUtc == DateTime.MinValue)
        {
            return "首次检测";
        }

        if (previous.CodexRunning == codexRunning && previous.ClaudeRunning == claudeRunning)
        {
            return "未变化";
        }

        return
            "Codex " +
            FormatBooleanTransition(previous.CodexRunning, codexRunning) +
            ", Claude " +
            FormatBooleanTransition(previous.ClaudeRunning, claudeRunning);
    }

    private static string FormatBooleanTransition(bool previous, bool current)
    {
        if (previous == current)
        {
            return current ? "运行中" : "未运行";
        }

        return previous
            ? "停止"
            : "启动";
    }

    private static bool IsAnyProcessNameRunning(string[] processNames)
    {
        if (processNames == null || processNames.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < processNames.Length; i++)
        {
            if (IsProcessNameRunning(processNames[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProcessNameRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        Process[] processes = null;
        try
        {
            // This intentionally queries explicit executable names only. The radar uses this
            // low-cost presence bit for gating; it must not scan every process or parse command
            // lines from paint or timer paths.
            processes = Process.GetProcessesByName(processName);
            return processes != null && processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogInfo(
                "Software presence check failed for " +
                processName +
                ": " +
                ex.GetType().Name +
                " " +
                ex.Message);
            return false;
        }
        finally
        {
            if (processes != null)
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    if (processes[i] != null)
                    {
                        processes[i].Dispose();
                    }
                }
            }
        }
    }
}
