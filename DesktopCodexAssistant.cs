using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    private const string MutexName = @"Local\" + ProductIdentity.MachineName;
    private const string StopEventName = MutexName + "Stop";
    private const string CtfmonRestartHelperArgument = "--ctfmon-restart-helper";
    private const string CtfmonRestartCorrelationArgument = "--correlation-id";
    private const int CtfmonRestartHelperWaitMs = 30000;
    internal const string RunValueName = ProductIdentity.MachineName;
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool performanceModeKnown;
    private static WidgetPerformanceMode activePerformanceMode;

    [STAThread]
    private static int Main(string[] args)
    {
        MigrateLegacyStorage();
        NetworkCheckHistoryLogger.Initialize();
        QuotaDecisionHistoryLogger.Initialize();

        int restartAfterPid;
        if (TryGetIntArg(args, "--restart-after-pid", out restartAfterPid))
        {
            WaitForRestartTargetExit(restartAfterPid);
        }

        bool useDesktopParent = HasArg(args, "--desktop-parent") || HasArg(args, "--workerw");
        LogInfo("Starting. Args=[" + string.Join(" ", args) + "], " + NativeMethods.DescribeProcessMachine());

        if (HasArg(args, CtfmonRestartHelperArgument))
        {
            return RunCtfmonRestartHelperCommand(args);
        }

        if (HasArg(args, "--stop"))
        {
            LogInfo("Stop requested.");
            SignalStop();
            SignalLegacyStops();
            return 0;
        }

        if (HasArg(args, "--install"))
        {
            LogInfo("Install requested.");
            InstallStartup(useDesktopParent);
            SignalStop();
            if (!HasArg(args, "--no-start"))
            {
                StartWidget(useDesktopParent);
            }
            return 0;
        }

        if (HasArg(args, "--uninstall"))
        {
            LogInfo("Uninstall requested.");
            RemoveStartup();
            SignalStop();
            return 0;
        }

        if (HasArg(args, "--test"))
        {
            return TestProbe();
        }

        if (HasArg(args, "--test-logger"))
        {
            return TestLoggerStoragePolicy();
        }

        if (HasArg(args, "--test-layout"))
        {
            return TestLayoutScalingPolicy();
        }

        if (HasArg(args, "--test-settings-bindings"))
        {
            return TestSettingsBindingPolicy();
        }

        if (HasArg(args, "--test-settings-open-close"))
        {
            return TestSettingsOpenClosePolicy(args);
        }

        if (HasArg(args, "--test-display-recovery"))
        {
            return TestDisplayRecoveryPolicy();
        }

        if (HasArg(args, "--test-radar-display-lifecycle"))
        {
            return TestRadarDisplayLifecyclePolicy(args);
        }

        if (HasArg(args, "--test-operation-panel"))
        {
            return TestOperationPanelPolicy();
        }

        if (HasArg(args, "--render-codexradar"))
        {
            return RenderCodexRadarSamples(args);
        }

        if (HasArg(args, "--render-clauderadar"))
        {
            return RenderClaudeRadarSamples(args);
        }

        if (HasArg(args, "--render-connectioncheck"))
        {
            return RenderConnectionCheckSamples(args);
        }

        if (HasArg(args, "--render-networkmonitor"))
        {
            return RenderNetworkMonitorSamples(args);
        }

        if (HasArg(args, "--render-powerthermal"))
        {
            return RenderPowerThermalSamples(args);
        }

        if (HasArg(args, "--render-widget"))
        {
            return RenderWidgetSamples(args);
        }

        if (HasArg(args, "--render-operation"))
        {
            return RenderOperationSamples(args);
        }

        if (HasArg(args, "--diagnose-idle-cpu"))
        {
            return RunIdleCpuDiagnosisCommand(args);
        }

        if (HasArg(args, "--diagnose-radar-runtime"))
        {
            return RunRadarRuntimeDiagnosisCommand(args);
        }

        // Stop pre-rename processes before acquiring the new product mutex.
        SignalLegacyStops();

        // The named mutex prevents duplicate layered windows; the named event provides a clean cross-process stop path.
        bool createdNew;
        Mutex mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            LogInfo("Another instance is already running; exiting.");
            mutex.Dispose();
            return 0;
        }

        EventWaitHandle stopEvent = null;
        try
        {
            stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            WidgetSettings settings = WidgetSettings.Load();
            ApplyPerformanceMode(settings.PerformanceMode);
            UiHangWatchdog.Start();
            using (PdhSampler sampler = new PdhSampler())
            using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, useDesktopParent))
            {
                Application.Run(form);
            }

            LogInfo("Application loop exited.");
            return 0;
        }
        catch (Exception ex)
        {
            LogException(ex);
            try
            {
                MessageBox.Show(
                    ProductIdentity.DisplayName + " failed to start.\r\n\r\n" + ex.Message + "\r\n\r\nLog: " + Logger.LogPath,
                    ProductIdentity.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            return 1;
        }
        finally
        {
            if (stopEvent != null)
            {
                stopEvent.Dispose();
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            mutex.Dispose();
            UiHangWatchdog.Shutdown();
            NetworkCheckHistoryLogger.Shutdown();
            QuotaDecisionHistoryLogger.Shutdown();
            Logger.Shutdown();
        }
    }

    private static bool HasArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStringArg(string[] args, string name, out string value)
    {
        value = string.Empty;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1] ?? string.Empty;
                return value.Length > 0;
            }
        }

        return false;
    }

    private static bool TryGetIntArg(string[] args, string name, out int value)
    {
        value = 0;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value > 0;
            }
        }

        return false;
    }

    internal static bool RunElevatedCtfmonRestartHelper(string correlationId, out string detail)
    {
        string normalizedCorrelationId = NormalizeCorrelationIdForHelper(correlationId);
        Stopwatch stopwatch = Stopwatch.StartNew();
        Process helperProcess = null;
        try
        {
            string executablePath = Application.ExecutablePath;
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                detail = "helper_executable_missing path=" + (executablePath ?? string.Empty);
                LogInfo("ctfmon_restart_helper_launch_failed correlation_id=" + normalizedCorrelationId + ", detail=" + detail);
                return false;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executablePath;
            startInfo.Arguments = BuildCtfmonRestartHelperArguments(normalizedCorrelationId);
            startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath);
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            LogInfo("ctfmon_restart_helper_launch_requested correlation_id=" + normalizedCorrelationId + ", exe=" + executablePath);
            helperProcess = Process.Start(startInfo);
            if (helperProcess == null)
            {
                detail = "Process.Start returned null";
                LogInfo("ctfmon_restart_helper_launch_failed correlation_id=" + normalizedCorrelationId + ", detail=" + detail);
                return false;
            }

            int helperPid = helperProcess.Id;
            if (!helperProcess.WaitForExit(CtfmonRestartHelperWaitMs))
            {
                detail =
                    "helper_pid=" +
                    helperPid.ToString(CultureInfo.InvariantCulture) +
                    ", wait_timeout_ms=" +
                    CtfmonRestartHelperWaitMs.ToString(CultureInfo.InvariantCulture);
                LogInfo("ctfmon_restart_helper_launch_completed correlation_id=" + normalizedCorrelationId + ", success=False, detail=" + detail);
                return false;
            }

            int exitCode = helperProcess.ExitCode;
            stopwatch.Stop();
            detail =
                "helper_pid=" +
                helperPid.ToString(CultureInfo.InvariantCulture) +
                ", exit_code=" +
                exitCode.ToString(CultureInfo.InvariantCulture) +
                ", elapsed_ms=" +
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
            bool success = exitCode == 0;
            LogInfo("ctfmon_restart_helper_launch_completed correlation_id=" + normalizedCorrelationId + ", success=" + success.ToString() + ", detail=" + detail);
            return success;
        }
        catch (Win32Exception ex)
        {
            stopwatch.Stop();
            detail =
                "Win32Exception(" +
                ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) +
                "): " +
                ex.Message +
                ", elapsed_ms=" +
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
            LogInfo("ctfmon_restart_helper_launch_failed correlation_id=" + normalizedCorrelationId + ", detail=" + detail);
            return false;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogException(ex);
            detail =
                ex.GetType().Name +
                ": " +
                ex.Message +
                ", elapsed_ms=" +
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
            LogInfo("ctfmon_restart_helper_launch_failed correlation_id=" + normalizedCorrelationId + ", detail=" + detail);
            return false;
        }
        finally
        {
            if (helperProcess != null)
            {
                helperProcess.Dispose();
            }
        }
    }

    internal static string BuildCtfmonRestartHelperArguments(string correlationId)
    {
        string normalizedCorrelationId = NormalizeCorrelationIdForHelper(correlationId);
        return CtfmonRestartHelperArgument + " " + CtfmonRestartCorrelationArgument + " " + normalizedCorrelationId;
    }

    private static int RunCtfmonRestartHelperCommand(string[] args)
    {
        string rawCorrelationId;
        if (!TryGetStringArg(args, CtfmonRestartCorrelationArgument, out rawCorrelationId))
        {
            rawCorrelationId = Guid.NewGuid().ToString("N");
        }

        string correlationId = NormalizeCorrelationIdForHelper(rawCorrelationId);
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool success = false;
        string detail = string.Empty;
        try
        {
            LogInfo("ctfmon_restart_helper_started correlation_id=" + correlationId + ", elevated=" + IsCurrentProcessElevated().ToString());
            success = NativeMethods.RestartCtfmonTextServices(out detail);
            return success ? 0 : 2;
        }
        catch (Exception ex)
        {
            LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return 1;
        }
        finally
        {
            stopwatch.Stop();
            LogInfo(
                "ctfmon_restart_helper_completed correlation_id=" +
                correlationId +
                ", success=" +
                success.ToString() +
                ", elapsed_ms=" +
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ", detail=" +
                detail);
            NetworkCheckHistoryLogger.Shutdown();
            QuotaDecisionHistoryLogger.Shutdown();
            Logger.Shutdown();
        }
    }

    private static string NormalizeCorrelationIdForHelper(string correlationId)
    {
        // The elevated helper only needs a correlation token; constrain it so UI text
        // cannot append extra administrator-process arguments across the UAC boundary.
        if (string.IsNullOrEmpty(correlationId))
        {
            return Guid.NewGuid().ToString("N");
        }

        StringBuilder builder = new StringBuilder(correlationId.Length);
        for (int i = 0; i < correlationId.Length; i++)
        {
            char ch = correlationId[i];
            if ((ch >= '0' && ch <= '9') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= 'a' && ch <= 'z') ||
                ch == '-' ||
                ch == '_')
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? Guid.NewGuid().ToString("N") : builder.ToString();
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForRestartTargetExit(int processId)
    {
        if (processId <= 0 || processId == Process.GetCurrentProcess().Id)
        {
            return;
        }

        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            LogException(ex);
        }
    }

    internal static void InstallStartup(bool useDesktopParent)
    {
        string exePath = Application.ExecutablePath;
        string command = Quote(exePath);
        if (useDesktopParent)
        {
            command += " --desktop-parent";
        }

        using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (runKey == null)
            {
                throw new InvalidOperationException("Cannot open HKCU startup registry key.");
            }

            runKey.SetValue(RunValueName, command, RegistryValueKind.String);
            DeleteLegacyStartupValues(runKey);
            LogInfo("Startup registry value set: " + command);
        }
    }

    internal static void RemoveStartup()
    {
        using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
        {
            if (runKey != null)
            {
                runKey.DeleteValue(RunValueName, false);
                DeleteLegacyStartupValues(runKey);
                LogInfo("Startup registry value removed.");
            }
        }
    }

    private static void StartWidget(bool useDesktopParent)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = Application.ExecutablePath;
        startInfo.UseShellExecute = true;
        if (useDesktopParent)
        {
            startInfo.Arguments = "--desktop-parent";
        }

        Process.Start(startInfo);
        LogInfo("Started widget process.");
    }

    internal static void SetStartupEnabled(bool enabled, bool useDesktopParent)
    {
        if (enabled)
        {
            InstallStartup(useDesktopParent);
        }
        else
        {
            RemoveStartup();
        }
    }

    internal static void SetPowerSavingEnabled(bool enabled)
    {
        ApplyPerformanceMode(enabled ? WidgetPerformanceMode.BatterySaver : WidgetPerformanceMode.Balanced);
    }

    internal static void RestartApplication(bool useDesktopParent)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = Application.ExecutablePath;
        startInfo.UseShellExecute = true;
        startInfo.Arguments =
            "--restart-after-pid " +
            Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
            (useDesktopParent ? " --desktop-parent" : string.Empty);
        Process.Start(startInfo);
    }

    internal static void ApplyPerformanceMode(WidgetPerformanceMode mode)
    {
        WidgetPerformanceMode effectiveMode = WidgetSettings.GetEffectivePerformanceMode(mode);
        if (performanceModeKnown && activePerformanceMode == effectiveMode)
        {
            return;
        }

        bool powerSaving = WidgetSettings.ShouldEnableProcessPowerSaving(effectiveMode);
        // BelowNormal keeps the UI responsive while EcoQoS/Power Throttling reduces background
        // execution cost. Idle priority caused excessive delays in WMI and settings operations.
        bool throttlingSet = NativeMethods.TrySetProcessPowerThrottling(powerSaving);
        bool prioritySet = false;
        try
        {
            Process.GetCurrentProcess().PriorityClass = powerSaving ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            prioritySet = true;
        }
        catch
        {
        }

        performanceModeKnown = true;
        activePerformanceMode = effectiveMode;
        LogInfo(string.Format(
            "Performance mode {0}{1}. ProcessPowerSaving={2}, PowerThrottling={3}, Priority={4}",
            mode,
            effectiveMode == mode ? string.Empty : " -> " + effectiveMode,
            powerSaving,
            throttlingSet,
            prioritySet));
    }

    internal static bool IsStartupEnabled()
    {
        try
        {
            using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (runKey == null)
                {
                    return false;
                }

                object value = runKey.GetValue(RunValueName);
                return value != null && value.ToString().Length > 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static void SignalStop()
    {
        SignalStop(StopEventName);
    }

    private static void SignalLegacyStops()
    {
        for (int i = 0; i < ProductIdentity.LegacyStopEventNames.Length; i++)
        {
            SignalStop(ProductIdentity.LegacyStopEventNames[i]);
        }
    }

    private static void SignalStop(string eventName)
    {
        try
        {
            using (EventWaitHandle stop = EventWaitHandle.OpenExisting(eventName))
            {
                stop.Set();
                LogInfo("Stop signal sent.");
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int TestProbe()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            using (PdhSampler sampler = new PdhSampler())
            {
                Thread.Sleep(1100);
                PerfSnapshot snapshot = sampler.Sample();
                string rssi = snapshot.NetworkIsWifi
                    ? (snapshot.NetworkRssiKnown ? snapshot.NetworkRssiDbm.ToString(CultureInfo.InvariantCulture) + "dBm" : "--dBm")
                    : "n/a";
                string sampleText = string.Format(
                    "{0} {1:0}% {2} | Memory {3:0.0}/{4:0.0} GB ({5:0}%, HW {6:0.0} GB) | Disk {7} WT {8} RD {9} | GPU {10:0}% {11:0.0}/{12:0.#} GB | NPU {13:0}% {14:0.0}/{15:0.#} GB | Network {16} UP {17} DL {18} RSSI {19}",
                    snapshot.CpuName,
                    snapshot.CpuPercent,
                    FormatCpuFrequencyPair(snapshot.CpuFrequencyGhz, snapshot.CpuBaseFrequencyGhz),
                    snapshot.MemoryUsedGb,
                    snapshot.MemoryTotalGb,
                    snapshot.MemoryPercent,
                    snapshot.MemoryHardwareReservedGb,
                    string.IsNullOrWhiteSpace(snapshot.DiskVolumeLabel) ? "--" : snapshot.DiskVolumeLabel,
                    NetworkRateFormatter.Format(snapshot.DiskWriteBytesPerSecond),
                    NetworkRateFormatter.Format(snapshot.DiskReadBytesPerSecond),
                    snapshot.GpuPercent,
                    snapshot.GpuMemoryUsedGb,
                    snapshot.GpuMemoryTotalGb,
                    snapshot.NpuPercent,
                    snapshot.NpuMemoryUsedGb,
                    snapshot.NpuMemoryTotalGb,
                    snapshot.NetworkConnected ? "connected" : "disconnected",
                    NetworkRateFormatter.Format(snapshot.NetworkSentBytesPerSecond),
                    NetworkRateFormatter.Format(snapshot.NetworkReceivedBytesPerSecond),
                    rssi);
                Console.WriteLine(sampleText);
                LogInfo("Test sample: " + sampleText);
                Console.WriteLine("Process: {0}", NativeMethods.DescribeProcessMachine());
            }

            NetworkMonitorReader.RunRollingPingSelfTest();
            CloudEndpointProbe.RunSelfTest();
            ClaudeRadarReader.RunSelfTest();
            ClaudeRadarSnapshotScheduler.RunSelfTest();
            StatuspageMonitor.RunSelfTest();
            DeepSeekBalanceMonitor.RunSelfTest();
            ServiceAlertDebouncer.RunSelfTest();
            ClaudeCodeUsageReader.RunSelfTest();
            ClaudeCodeUsageScheduler.RunSelfTest();
            CodexRadarForm.RunSoftwareModeGateSelfTest();
            CodexQuotaGoalPlanner.RunSelfTest();
            RadarRuntimeDiagnostics.RunSelfTest();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int TestLoggerStoragePolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            Logger.RunStoragePolicySelfTest();
            UiHangWatchdog.RunSelfTest();
            NetworkCheckHistoryLogger.RunSelfTest();
            QuotaDecisionHistoryLogger.RunSelfTest();
            Console.WriteLine("Logger storage policy: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int TestLayoutScalingPolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            WidgetSettings.RunLayoutScalingSelfTest();
            ApplicationWindowStateTracker.RunSelfTest();
            CodexRadarForm.RunStatusAndQuotaSelfTest();
            ClaudeRadarForm.RunRenderResourceSelfTest();
            ConnectionCheckForm.RunHiddenModeBadgeRenderingSelfTest();
            NetworkMonitorForm.RunNetworkMonitorDisplaySelfTest();
            Console.WriteLine("Layout scaling policy: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int TestSettingsBindingPolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            WidgetSettings.RunCompatibilitySelfTest();
            WidgetSettings.RunFullRoundTripSelfTest();
            HoverInteractionPolicy.RunSelfTest();
            IdleCpuDiagnostics.RunSelfTest();
            Win11SettingsForm.RunSettingsBindingSelfTest();
            Console.WriteLine("Settings binding policy: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int TestSettingsOpenClosePolicy(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int iterations;
            if (!TryGetIntArg(args, "--iterations", out iterations))
            {
                iterations = 50;
            }

            string summary = Win11SettingsForm.RunOpenCloseStressSelfTest(iterations);
            Console.WriteLine(summary);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderCodexRadarSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            CodexRadarForm.RenderVariantSamples(outputDir);
            CodexRadarForm.RenderRadarSolo(outputDir);
            CodexRadarForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered CodexRadar variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderClaudeRadarSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            ClaudeRadarForm.RenderVariantSamples(outputDir);
            ClaudeRadarForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered ClaudeRadar variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderConnectionCheckSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            ConnectionCheckForm.RenderCleanIpIconSamples(outputDir);
            ConnectionCheckForm.RenderRestyleVariantSamples(outputDir);
            ConnectionCheckForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered CleanIP badge icon and restyle variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderNetworkMonitorSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            NetworkMonitorForm.RenderVariantSamples(outputDir);
            NetworkMonitorForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered NetworkMonitor variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderPowerThermalSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            PowerThermalForm.RenderVariantSamples(outputDir);
            PowerThermalForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered PowerThermal variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderWidgetSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            WidgetForm.RenderVariantSamples(outputDir);
            WidgetForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered main widget variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderOperationSamples(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = ".";
            }

            OperationForm.RenderVariantSamples(outputDir);
            OperationForm.RenderCurrentSample(outputDir);
            Console.WriteLine("Rendered operation panel variant samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static string GetStringArg(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int RunIdleCpuDiagnosisCommand(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            int minutes;
            if (!TryGetIntArg(args, "--diagnose-minutes", out minutes))
            {
                minutes = 30;
            }

            IdleCpuDiagnostics.Report report = IdleCpuDiagnostics.Run(minutes);
            Console.WriteLine(report.Summary);
            Console.WriteLine(report.ReportPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RunRadarRuntimeDiagnosisCommand(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            int seconds;
            if (!TryGetIntArg(args, "--diagnose-seconds", out seconds))
            {
                seconds = 10;
            }

            int targetPid;
            TryGetIntArg(args, "--diagnose-target-pid", out targetPid);
            string label = GetStringArg(args, "--diagnose-label");
            RadarRuntimeDiagnostics.Report report = RadarRuntimeDiagnostics.Run(seconds, targetPid, label);
            Console.WriteLine(report.Summary);
            Console.WriteLine(report.TextPath);
            Console.WriteLine(report.JsonPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int TestDisplayRecoveryPolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (DisplayRecoverySelfTestForm form = new DisplayRecoverySelfTestForm())
            using (NativeMethods.LayeredBitmapSurface surface = new NativeMethods.LayeredBitmapSurface())
            {
                Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                form.StartPosition = FormStartPosition.Manual;
                form.FormBorderStyle = FormBorderStyle.None;
                form.ShowInTaskbar = false;
                form.Size = new Size(32, 32);
                form.Location = new Point(workArea.Left, workArea.Top);
                form.BackColor = Color.Black;
                form.Show();
                Application.DoEvents();

                using (Bitmap first = CreateDisplayRecoveryTestBitmap(Color.FromArgb(220, 40, 140, 255)))
                {
                    if (!surface.Update(form.Handle, form.Location, first, 255, true))
                    {
                        throw new InvalidOperationException("Initial layered update failed.");
                    }
                }

                surface.Reset();
                using (Bitmap second = CreateDisplayRecoveryTestBitmap(Color.FromArgb(220, 70, 220, 120)))
                {
                    if (!surface.Update(form.Handle, form.Location, second, 255, true))
                    {
                        throw new InvalidOperationException("Layered update after reset failed.");
                    }
                }

                surface.Reset();
                surface.Reset();
                form.Close();
            }

            if (!WidgetForm.IsPowerResumeEventType(NativeMethods.PBT_APMRESUMEAUTOMATIC) ||
                !WidgetForm.IsPowerResumeEventType(NativeMethods.PBT_APMRESUMESUSPEND) ||
                !WidgetForm.IsPowerResumeEventType(NativeMethods.PBT_APMRESUMECRITICAL) ||
                WidgetForm.IsPowerResumeEventType(NativeMethods.PBT_APMSUSPEND) ||
                WidgetForm.IsPowerResumeEventType(NativeMethods.PBT_POWERSETTINGCHANGE))
            {
                throw new InvalidOperationException("Power resume event policy failed.");
            }

            GlobalWinDWatcher.RunGestureSelfTest();

            Console.WriteLine("Display recovery layered surface policy: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int TestRadarDisplayLifecyclePolicy(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int iterations;
            if (!TryGetIntArg(args, "--iterations", out iterations))
            {
                iterations = 50;
            }

            iterations = Math.Max(1, Math.Min(500, iterations));
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.CodexRadarEnabled = true;
            settings.ClaudeRadarEnabled = true;
            settings.CodexRadarPublicJsonEnabled = false;
            settings.CodexRadarHtmlFallbackEnabled = false;
            settings.CodexRadarRssFallbackEnabled = false;
            settings.ClaudeRadarJsonEnabled = false;
            settings.ClaudeRadarHomepageFallbackEnabled = false;
            settings.ClaudeRadarCommunityRatingsEnabled = false;
            settings.ClaudeRadarLocalQuotaFallbackEnabled = false;
            settings.CodexRadarRandomTestEnabled = true;
            settings.ClaudeRadarRandomTestEnabled = true;
            settings.Normalize();

            using (CodexRadarForm codex = new CodexRadarForm(settings, null))
            using (ClaudeRadarForm claude = new ClaudeRadarForm(settings, null))
            {
                codex.StartPosition = FormStartPosition.Manual;
                claude.StartPosition = FormStartPosition.Manual;
                codex.Location = new Point(-30000, -30000);
                claude.Location = new Point(-30000, -29800);
                codex.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
                claude.Size = new Size(settings.ClaudeRadarWidth, settings.ClaudeRadarHeight);

                IntPtr codexHandle = codex.Handle;
                IntPtr claudeHandle = claude.Handle;
                if (codexHandle == IntPtr.Zero || claudeHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Radar display lifecycle self-test failed to create window handles.");
                }

                Application.DoEvents();
                ForceResourceCleanup();
                RadarRuntimeDiagnostics.ResourceCounters before = RadarRuntimeDiagnostics.CaptureCurrentProcessResources();
                for (int i = 0; i < iterations; i++)
                {
                    codex.PrepareForDisplaySuspend();
                    claude.PrepareForDisplaySuspend();
                    Application.DoEvents();
                    codex.RecoverAfterDisplayResume();
                    claude.RecoverAfterDisplayResume();
                    Application.DoEvents();
                }

                ForceResourceCleanup();
                RadarRuntimeDiagnostics.ResourceCounters after = RadarRuntimeDiagnostics.CaptureCurrentProcessResources();
                int handleDelta = after.HandleCount - before.HandleCount;
                int gdiDelta = after.GdiObjects - before.GdiObjects;
                int userDelta = after.UserObjects - before.UserObjects;
                if (handleDelta > 100 || gdiDelta > 10 || userDelta > 20)
                {
                    throw new InvalidOperationException(
                        "Radar display lifecycle resource growth exceeded threshold. Iterations=" +
                        iterations.ToString(CultureInfo.InvariantCulture) +
                        ", HandlesDelta=" +
                        handleDelta.ToString(CultureInfo.InvariantCulture) +
                        ", GdiDelta=" +
                        gdiDelta.ToString(CultureInfo.InvariantCulture) +
                        ", UserDelta=" +
                        userDelta.ToString(CultureInfo.InvariantCulture));
                }

                Console.WriteLine(
                    "Radar display lifecycle policy: PASS iterations={0} handles_delta={1} gdi_delta={2} user_delta={3}",
                    iterations,
                    handleDelta,
                    gdiDelta,
                    userDelta);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static void ForceResourceCleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Application.DoEvents();
    }

    private static int TestOperationPanelPolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            OperationForm.RunSelfTest();
            RunCtfmonRestartHelperArgumentSelfTest();
            Console.WriteLine("Operation panel interaction and performance policy: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static void RunCtfmonRestartHelperArgumentSelfTest()
    {
        string arguments = BuildCtfmonRestartHelperArguments("abc DEF&bad");
        string[] argumentParts = arguments.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (arguments.IndexOf('&') >= 0 ||
            argumentParts.Length != 3 ||
            !string.Equals(argumentParts[2], "abcDEFbad", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CTF helper argument sanitizer failed.");
        }

        string[] parsed = new string[] { CtfmonRestartHelperArgument, CtfmonRestartCorrelationArgument, "abcDEFbad" };
        string value;
        if (!HasArg(parsed, CtfmonRestartHelperArgument) ||
            !TryGetStringArg(parsed, CtfmonRestartCorrelationArgument, out value) ||
            !string.Equals(value, "abcDEFbad", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CTF helper argument parser failed.");
        }
    }

    private static Bitmap CreateDisplayRecoveryTestBitmap(Color color)
    {
        Bitmap bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(brush, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        return bitmap;
    }

    private sealed class DisplayRecoverySelfTestForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }

    private static void MigrateLegacyStorage()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string currentDirectory = Path.Combine(localAppData, ProductIdentity.MachineName);
            if (Directory.Exists(currentDirectory))
            {
                return;
            }

            for (int legacyIndex = 0; legacyIndex < ProductIdentity.LegacyStorageDirectoryNames.Length; legacyIndex++)
            {
                string legacyDirectory = Path.Combine(localAppData, ProductIdentity.LegacyStorageDirectoryNames[legacyIndex]);
                if (!Directory.Exists(legacyDirectory))
                {
                    continue;
                }

                Directory.CreateDirectory(currentDirectory);
                string[] files = Directory.GetFiles(legacyDirectory);
                for (int i = 0; i < files.Length; i++)
                {
                    string destination = Path.Combine(currentDirectory, Path.GetFileName(files[i]));
                    if (!File.Exists(destination))
                    {
                        File.Copy(files[i], destination, false);
                    }
                }

                return;
            }
        }
        catch
        {
            // Migration is best-effort; startup must continue when legacy data is inaccessible.
        }
    }

    private static void DeleteLegacyStartupValues(RegistryKey runKey)
    {
        for (int i = 0; i < ProductIdentity.LegacyRunValueNames.Length; i++)
        {
            runKey.DeleteValue(ProductIdentity.LegacyRunValueNames[i], false);
        }
    }

    private static string FormatCpuFrequencyPair(double currentGhz, double baseGhz)
    {
        if (currentGhz <= 0.0 && baseGhz <= 0.0)
        {
            return string.Empty;
        }

        if (baseGhz <= 0.0)
        {
            return string.Format("{0:0.00}GHz/--GHz", currentGhz);
        }

        return string.Format("{0:0.00}GHz/{1:0.00}GHz", currentGhz, baseGhz);
    }

    internal static void LogInfo(string message)
    {
        Logger.Info(message);
    }

    internal static void LogException(Exception ex)
    {
        Logger.Error(ex);
    }
}
