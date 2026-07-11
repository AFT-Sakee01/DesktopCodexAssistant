using System;
using System.Collections.Generic;
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
    private const int MaxProductIdentityCacheEntries = 64;
    private const double IdentityDiscoveryRefreshSeconds = 60.0;
    private const int MaxDiscoveredProcessNamesPerFamily = 16;
    private static readonly object SyncRoot = new object();
    private static readonly object ProductIdentitySyncRoot = new object();
    private static readonly string[] CodexProcessNames = new string[] { "codex", "codex-code-mode-host" };
    private static readonly string[] CodexAmbiguousProcessNames = new string[] { "chatgpt" };
    private static readonly string[] ClaudeProcessNames = new string[] { "claude", "claude-code", "anthropic" };
    private static readonly HashSet<string> DiscoveredCodexProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DiscoveredClaudeProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ProductIdentityCache =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static SoftwareRuntimePresenceSnapshot cachedSnapshot = SoftwareRuntimePresenceSnapshot.Empty();
    private static DateTime lastIdentityDiscoveryUtc = DateTime.MinValue;

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

            bool codexRunning = IsAnyProcessNameRunning(CodexProcessNames) ||
                IsAnyClassifiedProcessNameRunning(CodexAmbiguousProcessNames, CodexRadarSoftwareMode.Codex) ||
                IsAnyClassifiedProcessNameRunning(DiscoveredCodexProcessNames, CodexRadarSoftwareMode.Codex);
            bool claudeRunning = IsAnyProcessNameRunning(ClaudeProcessNames) ||
                IsAnyClassifiedProcessNameRunning(DiscoveredClaudeProcessNames, CodexRadarSoftwareMode.Claude);
            DiscoverMissingSoftwareProcessesIfDue(nowUtc, ref codexRunning, ref claudeRunning);
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

    public static bool TryClassifySoftwareProcess(
        string processName,
        string executablePath,
        string windowTitle,
        out CodexRadarSoftwareMode mode)
    {
        if (TryClassifySoftwareSignals(
            processName,
            executablePath,
            windowTitle,
            string.Empty,
            out mode))
        {
            return true;
        }

        string productIdentity = ReadExecutableProductIdentity(executablePath);
        return productIdentity.Length > 0 &&
            TryClassifySoftwareSignals(
                processName,
                executablePath,
                windowTitle,
                productIdentity,
                out mode);
    }

    internal static void RunSelfTest()
    {
        const string codexStorePath =
            @"C:\Program Files\WindowsApps\OpenAI.Codex_26.707.3748.0_arm64__2p2nqsd0c76g0\app\ChatGPT.exe";
        const string chatGptStorePath =
            @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2026.133.0_arm64__2p2nqsd0c76g0\app\ChatGPT.exe";

        AssertClassification(
            "ChatGPT",
            codexStorePath,
            "ChatGPT",
            string.Empty,
            CodexRadarSoftwareMode.Codex,
            "updated Codex Store package");
        AssertUnclassified(
            "ChatGPT",
            chatGptStorePath,
            "ChatGPT",
            "ChatGPT",
            "real ChatGPT desktop must not impersonate Codex");
        AssertClassification(
            "ChatGPT",
            @"C:\FutureVendor\desktop-shell.exe",
            "ChatGPT",
            "Codex",
            CodexRadarSoftwareMode.Codex,
            "Codex product metadata fallback");
        AssertClassification(
            "codex-code-mode-host",
            string.Empty,
            string.Empty,
            string.Empty,
            CodexRadarSoftwareMode.Codex,
            "Codex helper process");
        AssertClassification(
            "claude",
            @"C:\Program Files\WindowsApps\Claude_1.20186.0.0_arm64__pzs8sxrjxfjjc\app\claude.exe",
            "Claude",
            string.Empty,
            CodexRadarSoftwareMode.Claude,
            "Claude desktop process");
        AssertUnclassified(
            "chrome",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "Codex - Google Chrome",
            "Google Chrome",
            "browser title must not impersonate a local app");
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

    private static bool IsAnyClassifiedProcessNameRunning(
        IEnumerable<string> processNames,
        CodexRadarSoftwareMode expectedMode)
    {
        if (processNames == null)
        {
            return false;
        }

        foreach (string processName in processNames)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(processName);
                for (int j = 0; processes != null && j < processes.Length; j++)
                {
                    Process process = processes[j];
                    if (process == null)
                    {
                        continue;
                    }

                    CodexRadarSoftwareMode mode;
                    if (TryClassifySoftwareProcess(
                        process.ProcessName,
                        NativeMethods.TryGetProcessImagePath(process.Id),
                        SafeGetMainWindowTitle(process),
                        out mode) &&
                        mode == expectedMode)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogInfo(
                    "Software identity check failed for " +
                    processName +
                    ": " +
                    ex.GetType().Name +
                    " " +
                    ex.Message);
            }
            finally
            {
                DisposeProcesses(processes);
            }
        }

        return false;
    }

    private static void DiscoverMissingSoftwareProcessesIfDue(
        DateTime nowUtc,
        ref bool codexRunning,
        ref bool claudeRunning)
    {
        if (codexRunning && claudeRunning)
        {
            return;
        }

        if (lastIdentityDiscoveryUtc != DateTime.MinValue &&
            (nowUtc - lastIdentityDiscoveryUtc).TotalSeconds < IdentityDiscoveryRefreshSeconds)
        {
            return;
        }

        lastIdentityDiscoveryUtc = nowUtc;
        Process[] processes = null;
        try
        {
            // This miss-only discovery is deliberately low frequency. It lets Store updates
            // rename the desktop shell without turning every 3-10 second presence tick into a
            // full process scan; learned names return to the cheap explicit-name path above.
            processes = Process.GetProcesses();
            for (int i = 0; processes != null && i < processes.Length; i++)
            {
                Process process = processes[i];
                if (process == null || !HasMainWindow(process))
                {
                    continue;
                }

                string processName = SafeGetProcessName(process);
                CodexRadarSoftwareMode mode;
                if (!TryClassifySoftwareProcess(
                    processName,
                    NativeMethods.TryGetProcessImagePath(process.Id),
                    string.Empty,
                    out mode))
                {
                    continue;
                }

                if (mode == CodexRadarSoftwareMode.Claude)
                {
                    claudeRunning = true;
                    RememberDiscoveredProcessName(DiscoveredClaudeProcessNames, processName, "Claude");
                }
                else
                {
                    codexRunning = true;
                    RememberDiscoveredProcessName(DiscoveredCodexProcessNames, processName, "Codex");
                }

                if (codexRunning && claudeRunning)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogInfo(
                "Software identity fallback discovery failed: " +
                ex.GetType().Name +
                " " +
                ex.Message);
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static void RememberDiscoveredProcessName(
        HashSet<string> target,
        string processName,
        string family)
    {
        string normalized = (processName ?? string.Empty).Trim();
        if (normalized.Length == 0 || target.Contains(normalized))
        {
            return;
        }

        if (target.Count >= MaxDiscoveredProcessNamesPerFamily)
        {
            target.Clear();
        }

        target.Add(normalized);
        Program.LogInfo(
            "Software identity fallback learned process name. Family=" +
            family +
            " ProcessName=" +
            normalized);
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
            DisposeProcesses(processes);
        }
    }

    private static bool TryClassifySoftwareSignals(
        string processName,
        string executablePath,
        string windowTitle,
        string productIdentity,
        out CodexRadarSoftwareMode mode)
    {
        mode = CodexRadarSoftwareMode.Codex;
        string name = NormalizeIdentityText(processName);
        string path = NormalizeIdentityPath(executablePath);
        string title = NormalizeIdentityText(windowTitle);
        string product = NormalizeIdentityText(productIdentity);

        if (IsClaudePackagePath(path) || IsClaudeProcessName(name) || ContainsIdentity(product, "claude"))
        {
            mode = CodexRadarSoftwareMode.Claude;
            return true;
        }

        if (IsCodexPackagePath(path) || IsCodexProcessName(name) || ContainsIdentity(product, "codex"))
        {
            mode = CodexRadarSoftwareMode.Codex;
            return true;
        }

        if (!IsKnownBrowserOrShell(name))
        {
            if (ContainsIdentity(title, "claude"))
            {
                mode = CodexRadarSoftwareMode.Claude;
                return true;
            }

            if (ContainsIdentity(title, "codex"))
            {
                mode = CodexRadarSoftwareMode.Codex;
                return true;
            }
        }

        return false;
    }

    private static bool IsCodexPackagePath(string path)
    {
        return path.IndexOf(@"\windowsapps\openai.codex_", StringComparison.Ordinal) >= 0 ||
            path.IndexOf(@"\openai\codex\", StringComparison.Ordinal) >= 0 ||
            path.IndexOf(@"\openai.codex\", StringComparison.Ordinal) >= 0;
    }

    private static bool IsClaudePackagePath(string path)
    {
        return path.IndexOf(@"\windowsapps\claude_", StringComparison.Ordinal) >= 0 ||
            path.IndexOf(@"\appdata\roaming\claude\", StringComparison.Ordinal) >= 0 ||
            path.IndexOf(@"\anthropic\claude\", StringComparison.Ordinal) >= 0;
    }

    private static bool IsCodexProcessName(string name)
    {
        return string.Equals(name, "codex", StringComparison.Ordinal) ||
            name.StartsWith("codex-", StringComparison.Ordinal) ||
            name.StartsWith("codex_", StringComparison.Ordinal);
    }

    private static bool IsClaudeProcessName(string name)
    {
        return string.Equals(name, "claude", StringComparison.Ordinal) ||
            string.Equals(name, "anthropic", StringComparison.Ordinal) ||
            name.StartsWith("claude-", StringComparison.Ordinal) ||
            name.StartsWith("claude_", StringComparison.Ordinal);
    }

    private static bool IsKnownBrowserOrShell(string name)
    {
        return string.Equals(name, "chrome", StringComparison.Ordinal) ||
            string.Equals(name, "msedge", StringComparison.Ordinal) ||
            string.Equals(name, "firefox", StringComparison.Ordinal) ||
            string.Equals(name, "brave", StringComparison.Ordinal) ||
            string.Equals(name, "opera", StringComparison.Ordinal) ||
            string.Equals(name, "explorer", StringComparison.Ordinal) ||
            string.Equals(name, "applicationframehost", StringComparison.Ordinal);
    }

    private static bool ContainsIdentity(string text, string value)
    {
        return !string.IsNullOrEmpty(text) &&
            text.IndexOf(value, StringComparison.Ordinal) >= 0;
    }

    private static string NormalizeIdentityText(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeIdentityPath(string value)
    {
        return NormalizeIdentityText(value).Replace('/', '\\');
    }

    private static string ReadExecutableProductIdentity(string executablePath)
    {
        string path = (executablePath ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            return string.Empty;
        }

        lock (ProductIdentitySyncRoot)
        {
            string cached;
            if (ProductIdentityCache.TryGetValue(path, out cached))
            {
                return cached;
            }
        }

        string identity = string.Empty;
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            identity = string.Join(
                " ",
                info.ProductName ?? string.Empty,
                info.FileDescription ?? string.Empty).Trim();
        }
        catch
        {
            identity = string.Empty;
        }

        lock (ProductIdentitySyncRoot)
        {
            // Package updates create a new executable path. Bounding this cache keeps update
            // history from growing for the full process lifetime while avoiding repeated metadata IO.
            if (ProductIdentityCache.Count >= MaxProductIdentityCacheEntries)
            {
                ProductIdentityCache.Clear();
            }

            ProductIdentityCache[path] = identity;
        }

        return identity;
    }

    private static string SafeGetMainWindowTitle(Process process)
    {
        try
        {
            return process == null ? string.Empty : (process.MainWindowTitle ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool HasMainWindow(Process process)
    {
        try
        {
            return process != null && process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return process == null ? string.Empty : (process.ProcessName ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void DisposeProcesses(Process[] processes)
    {
        if (processes == null)
        {
            return;
        }

        for (int i = 0; i < processes.Length; i++)
        {
            if (processes[i] != null)
            {
                processes[i].Dispose();
            }
        }
    }

    private static void AssertClassification(
        string processName,
        string executablePath,
        string windowTitle,
        string productIdentity,
        CodexRadarSoftwareMode expected,
        string message)
    {
        CodexRadarSoftwareMode actual;
        if (!TryClassifySoftwareSignals(
            processName,
            executablePath,
            windowTitle,
            productIdentity,
            out actual) ||
            actual != expected)
        {
            throw new InvalidOperationException("Software identity self-test failed: " + message);
        }
    }

    private static void AssertUnclassified(
        string processName,
        string executablePath,
        string windowTitle,
        string productIdentity,
        string message)
    {
        CodexRadarSoftwareMode mode;
        if (TryClassifySoftwareSignals(
            processName,
            executablePath,
            windowTitle,
            productIdentity,
            out mode))
        {
            throw new InvalidOperationException("Software identity self-test failed: " + message);
        }
    }
}
