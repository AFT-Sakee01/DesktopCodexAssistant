using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;

// Keep-alive for the two packaged AI desktop apps behind GUARD's 程序守护 section. Detection and
// relaunch only; nothing here ever stops a process.
//
// Deliberately scoped to the *desktop apps*, not the CLIs. Relaunching a dead `codex`/`claude` CLI
// would produce an empty session in an arbitrary working directory -- a new window, not a repair.
// The packaged apps are tray-resident and lose nothing but their window when system sleep or a crash
// takes them down, so starting them again restores the state the user actually had.
//
// SoftwareRuntimePresence answers a different question ("is this family running at all", CLI
// included, for quota gating) and must keep doing so; this guard cannot reuse it because a running
// CLI would mask a closed app.
internal static class ProgramKeepAliveGuard
{
    internal enum KeepAliveTarget
    {
        CodexApp,
        ClaudeApp
    }

    // Package identity resolved from Get-StartApps on the target device. The ChatGPT desktop app
    // ships under OpenAI's "Codex" package name, which is why the process is ChatGPT.exe while the
    // package family reads OpenAI.Codex.
    private const string CodexAppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private const string ClaudeAppUserModelId = "Claude_pzs8sxrjxfjjc!Claude";

    private const string CodexAppProcessName = "ChatGPT";
    private const string ClaudeProcessName = "claude";

    // The Claude desktop app and the Claude Code CLI both run as claude.exe, so the process name
    // alone cannot tell them apart: with only the CLI running, a name-based check would report the
    // app as alive and the guard would never repair it. The packaged app always executes from the
    // MSIX install root, the CLI never does.
    private const string ClaudePackagedPathMarker = @"\windowsapps\claude_";

    internal static string DescribeTarget(KeepAliveTarget target)
    {
        return target == KeepAliveTarget.CodexApp ? "Codex 应用" : "Claude 应用";
    }

    // Returns false only when the app is known to be absent. Every failure to determine presence
    // reports true: a guard that guesses "gone" relaunches a healthy app every 30 seconds, which is
    // far worse than skipping one repair cycle.
    internal static bool IsRunning(KeepAliveTarget target)
    {
        if (target == KeepAliveTarget.CodexApp)
        {
            return IsAnyProcessRunning(CodexAppProcessName);
        }

        return IsPackagedClaudeAppRunning();
    }

    internal static bool TryEnsureRunning(KeepAliveTarget target, out string detail)
    {
        detail = string.Empty;
        if (IsRunning(target))
        {
            return false;
        }

        string appUserModelId = target == KeepAliveTarget.CodexApp
            ? CodexAppUserModelId
            : ClaudeAppUserModelId;
        if (NativeMethods.OpenAppsFolderApplication(appUserModelId))
        {
            return true;
        }

        detail = "shell:AppsFolder 启动失败";
        return false;
    }

    private static bool IsAnyProcessRunning(string processName)
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(processName);
            return processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return true; // unknown -> treat as alive, see IsRunning
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static bool IsPackagedClaudeAppRunning()
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ExecutablePath FROM Win32_Process WHERE Name = 'claude.exe'"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject row in results)
                {
                    using (row)
                    {
                        object path = row["ExecutablePath"];
                        if (path == null)
                        {
                            continue;
                        }

                        string text = path.ToString();
                        if (text.ToLower(CultureInfo.InvariantCulture).Contains(ClaudePackagedPathMarker))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return true; // WMI unavailable -> treat as alive rather than relaunch on a guess
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
            try
            {
                processes[i].Dispose();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }
    }

    // Pure-logic coverage for the one rule that is easy to regress: a CLI-only path must not count
    // as the packaged app. Kept here rather than in a process-level test because the real query
    // depends on whatever happens to be running on the machine.
    internal static bool PathLooksLikePackagedClaudeApp(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        return executablePath.ToLower(CultureInfo.InvariantCulture).Contains(ClaudePackagedPathMarker);
    }

    internal static void RunSelfTest()
    {
        if (PathLooksLikePackagedClaudeApp(@"C:\Users\X\AppData\Roaming\Claude\claude-code\2.1.266\claude.exe"))
        {
            throw new InvalidOperationException("Claude Code CLI path must not count as the packaged desktop app.");
        }

        if (!PathLooksLikePackagedClaudeApp(@"C:\Program Files\WindowsApps\Claude_1.52386.0.0_arm64__pzs8sxrjxfjjc\app\Claude.exe"))
        {
            throw new InvalidOperationException("Packaged Claude app path must be recognized.");
        }

        if (PathLooksLikePackagedClaudeApp(null) || PathLooksLikePackagedClaudeApp(string.Empty))
        {
            throw new InvalidOperationException("Empty executable path must not be recognized.");
        }

        Console.WriteLine("Program keep-alive guard: PASS packaged-vs-CLI Claude path discrimination");
    }
}
