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
    private const int FatalRestartSuppressionMinutes = 15;
    internal const string RunValueName = ProductIdentity.MachineName;
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool performanceModeKnown;
    private static WidgetPerformanceMode activePerformanceMode;
    private static bool fatalRestartUseDesktopParent;
    private static int globalExceptionHandlersRegistered;

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

        if (HasArg(args, "--night-proof"))
        {
            RenderSampleSupport.ProofLuminancePercent = WidgetSettings.DefaultNightDimLuminancePercent;
        }

        if (HasArg(args, "--test"))
        {
            return TestProbe();
        }

        if (HasArg(args, "--test-codex-task-monitor"))
        {
            return TestCodexTaskMonitor();
        }

        if (HasArg(args, "--dump-codex-tasks"))
        {
            return DumpCodexTasks();
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

        if (HasArg(args, "--test-specboard-manager"))
        {
            return TestSpecBoardManagerPolicy();
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

        if (HasArg(args, "--render-networkmonitor"))
        {
            return RenderNetworkMonitorSamples(args);
        }

        if (HasArg(args, "--render-tilecolumn"))
        {
            return RenderTileColumnSamples(args);
        }

        if (HasArg(args, "--render-operation"))
        {
            return RenderOperationSamples(args);
        }

        if (HasArg(args, "--render-resetspeedboard"))
        {
            return RenderResetSpeedBoardSample(args);
        }

        if (HasArg(args, "--render-systemdayboard"))
        {
            return RenderSystemDayBoardSample(args);
        }

        if (HasArg(args, "--render-specboardmanager"))
        {
            return RenderSpecBoardManagerSamples(args);
        }

        if (HasArg(args, "--render-specboard"))
        {
            return RenderSpecBoardSamples(args);
        }

        if (HasArg(args, "--render-guard"))
        {
            return RenderGuardBoardSamples(args);
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
            RegisterGlobalExceptionHandlers(useDesktopParent);
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
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                IsArgumentValue(args[i + 1]))
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
                IsArgumentValue(args[i + 1]) &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value > 0;
            }
        }

        return false;
    }

    private static bool IsArgumentValue(string value)
    {
        return !string.IsNullOrEmpty(value) &&
            !value.StartsWith("--", StringComparison.Ordinal);
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
                string expectedPath = Application.ExecutablePath;
                if (!IsRestartTargetIdentityMatch(process, expectedPath))
                {
                    LogInfo("Restart target identity mismatch before wait. Pid=" + processId.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                if (!process.WaitForExit(10000))
                {
                    process.Refresh();
                    if (!IsRestartTargetIdentityMatch(process, expectedPath))
                    {
                        LogInfo("Restart target identity mismatch before termination. Pid=" + processId.ToString(CultureInfo.InvariantCulture));
                        return;
                    }

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
        startInfo.Arguments = BuildRestartArguments(Process.GetCurrentProcess().Id, useDesktopParent);
        Process.Start(startInfo);
    }

    internal static string BuildRestartArguments(int processId, bool useDesktopParent)
    {
        return "--restart-after-pid " +
            processId.ToString(CultureInfo.InvariantCulture) +
            (useDesktopParent ? " --desktop-parent" : string.Empty);
    }

    internal static bool ShouldRestartAfterFatalException(DateTime lastFatalUtc, DateTime nowUtc)
    {
        if (lastFatalUtc == DateTime.MinValue)
        {
            return true;
        }

        return nowUtc >= lastFatalUtc &&
            nowUtc - lastFatalUtc >= TimeSpan.FromMinutes(FatalRestartSuppressionMinutes);
    }

    private static void RegisterGlobalExceptionHandlers(bool useDesktopParent)
    {
        fatalRestartUseDesktopParent = useDesktopParent;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        if (Interlocked.Exchange(ref globalExceptionHandlersRegistered, 1) != 0)
        {
            return;
        }

        Application.ThreadException += OnApplicationThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
    }

    private static void OnApplicationThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Logger.Error(e == null || e.Exception == null
            ? new InvalidOperationException("UI thread raised an unhandled exception without an exception object.")
            : e.Exception);
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e == null ? null : e.ExceptionObject as Exception;
        if (exception == null)
        {
            exception = new InvalidOperationException("AppDomain raised an unhandled non-Exception object.");
        }

        Logger.Error(exception);
        DateTime nowUtc = DateTime.UtcNow;
        string restartDiagnostic;
        bool restart = FatalRestartBudget.TryAcquire(
            FatalRestartBudget.StatePath,
            nowUtc,
            TimeSpan.FromMinutes(FatalRestartSuppressionMinutes),
            out restartDiagnostic);
        if (!string.IsNullOrEmpty(restartDiagnostic))
        {
            LogInfo(restartDiagnostic);
        }

        if (restart)
        {
            try
            {
                RestartApplication(fatalRestartUseDesktopParent);
            }
            catch (Exception restartException)
            {
                Logger.Error(restartException);
            }
        }

        Environment.Exit(1);
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
                    NetworkRateFormatter.FormatStorage(snapshot.DiskWriteBytesPerSecond),
                    NetworkRateFormatter.FormatStorage(snapshot.DiskReadBytesPerSecond),
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

            RunNamedSelfTest("NetworkRateFormatter", NetworkRateFormatter.RunSelfTest);
            RunNamedSelfTest("NetworkMonitorReader.RollingPing", NetworkMonitorReader.RunRollingPingSelfTest);
            RunNamedSelfTest("PathPingProbeReader", PathPingProbeReader.RunSelfTest);
            RunNamedSelfTest("FixedPingProbeReader", FixedPingProbeReader.RunSelfTest);
            RunNamedSelfTest("CloudEndpointProbe", CloudEndpointProbe.RunSelfTest);
            RunNamedSelfTest("BoundedHttpTextReader", BoundedHttpTextReader.RunSelfTest);
            RunNamedSelfTest("OwnerOperationGeneration", OwnerOperationGeneration.RunSelfTest);
            RunNamedSelfTest("CodexRadarUrlPolicy", CodexRadarUrlPolicy.RunSelfTest);
            RunNamedSelfTest("CloudEndpointProbeReader", CloudEndpointProbeReader.RunSelfTest);
            RunNamedSelfTest("GfwProbeReader", GfwProbeReader.RunSelfTest);
            RunNamedSelfTest("StatuspageMonitor", StatuspageMonitor.RunSelfTest);
            RunNamedSelfTest("DeepSeekServiceMonitor", DeepSeekServiceMonitor.RunSelfTest);
            RunNamedSelfTest("DeepSeekBalanceMonitor", DeepSeekBalanceMonitor.RunSelfTest);
            RunNamedSelfTest("ServiceAlertDebouncer", ServiceAlertDebouncer.RunSelfTest);
            RunNamedSelfTest("ClaudeCodeUsageReader", ClaudeCodeUsageReader.RunSelfTest);
            RunNamedSelfTest("ClaudeCodeUsageScheduler", ClaudeCodeUsageScheduler.RunSelfTest);
            RunNamedSelfTest("CodexRadarForm.SoftwareModeGate", CodexRadarForm.RunSoftwareModeGateSelfTest);
            RunNamedSelfTest("CodexRadarForm.TileSnapshotFamily", CodexRadarForm.RunTileSnapshotFamilySelfTest);
            RunNamedSelfTest("CodexQuotaGoalPlanner", CodexQuotaGoalPlanner.RunSelfTest);
            RunNamedSelfTest("RadarRuntimeDiagnostics", RadarRuntimeDiagnostics.RunSelfTest);
            RunNamedSelfTest("CodexTaskMonitorReader", CodexTaskMonitorReader.RunSelfTest);
            RunNamedSelfTest("CodexTaskPresentation", CodexTaskPresentation.RunSelfTest);
            RunNamedSelfTest("GuardRuntime", GuardRuntime.RunSelfTest);
            RunNamedSelfTest("GuardBoardForm", GuardBoardForm.RunSelfTest);
            RunNamedSelfTest("OperationForm.LeftDockMutualExclusion", OperationForm.RunLeftDockMutualExclusionSelfTest);
            RunNamedSelfTest("BurnInProtection", BurnInProtection.RunSelfTest);
            RunNamedSelfTest("MemoryPressureTracker", MemoryPressureTracker.RunSelfTest);
            RunNamedSelfTest("MetricTileModel", MetricTileModel.RunSelfTest);
            RunNamedSelfTest("MetricTileForm", MetricTileForm.RunSelfTest);
            RunNamedSelfTest("MetricTileExpandForm", MetricTileExpandForm.RunSelfTest);
            RunNamedSelfTest("WidgetForm.TileColumnRuntime", WidgetForm.RunTileColumnRuntimeSelfTest);
            RunNamedSelfTest("TimingStats", TimingStats.RunSelfTest);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static void RunNamedSelfTest(string name, Action test)
    {
        if (test == null)
        {
            throw new ArgumentNullException("test");
        }

        long started = Stopwatch.GetTimestamp();
        LogInfo("Self-test begin. Name=" + name);
        test();
        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        LogInfo("Self-test passed. Name=" + name + ", ElapsedMs=" + elapsedMs.ToString("0.00", CultureInfo.InvariantCulture));
    }

    private static int TestCodexTaskMonitor()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            CodexTaskMonitorReader.RunSelfTest();
            CodexTaskPresentation.RunSelfTest();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int DumpCodexTasks()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            WidgetSettings settings = WidgetSettings.Load();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sessionsPath = string.IsNullOrWhiteSpace(profile)
                ? string.Empty
                : Path.Combine(Path.Combine(profile, ".codex"), "sessions");
            List<string> files = CodexRadarForm.EnumerateCodexRolloutFiles(sessionsPath);
            using (CodexTaskMonitorReader reader = new CodexTaskMonitorReader(settings))
            {
                reader.RequestReconcile(files);
                if (!reader.WaitForIdle(30000))
                {
                    throw new TimeoutException("Codex task snapshot did not finish within 30 seconds.");
                }

                Console.WriteLine(CodexTaskMonitorReader.SerializeSnapshot(reader.GetSnapshot()));
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

    private static int TestLoggerStoragePolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            Logger.RunStoragePolicySelfTest();
            BoundedHttpTextReader.RunSelfTest();
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
            LeftDockLayout.RunSelfTest();
            GlobalLayoutEditorForm.RunSelfTest();
            LayeredWidgetFormBase.RunOpacityPolicySelfTest();
            LayeredWidgetFormBase.RunScalePolicySelfTest();
            NightScheduleController.RunSelfTest();
            AlertPresentationPolicy.RunSelfTest();
            ApplicationWindowStateTracker.RunSelfTest();
            CodexRadarForm.RunStatusAndQuotaSelfTest();
            NetworkMonitorForm.RunNetworkMonitorDisplaySelfTest();
            SpecBoardForm.RunSelfTest();
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

    private static int TestSpecBoardManagerPolicy()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SpecBoardLedgerStore.RunSelfTest();
            SpecBoardLedgerStore.RunRecycleAcceptanceSelfTest();
            SpecBoardSeenStateStore.RunSelfTest();
            SpecBoardManagerForm.RunSelfTest();
            Console.WriteLine("Spec Board manager policy: PASS atomic backup conflict recycle rollback batch settings layout");
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
            string outputPath = GetStringArg(args, "--out");
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                string fullOutputPath = Path.GetFullPath(outputPath);
                string outputDirectory = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                File.WriteAllText(fullOutputPath, summary + Environment.NewLine, SharedEncoding.Utf8NoBom);
            }

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

            NetworkMonitorForm.RenderCurrentSample(outputDir);
            NetworkMonitorForm.RenderDockedSamples(outputDir);
            if (HasArg(args, "--scale-proof"))
            {
                string summary = NetworkMonitorForm.RenderScaleOverrideProof(outputDir);
                File.WriteAllText(
                    Path.Combine(outputDir, "scale-proof.txt"),
                    summary + Environment.NewLine,
                    SharedEncoding.Utf8NoBom);
                Console.WriteLine(summary);
            }
            Console.WriteLine("Rendered NetworkMonitor dock samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderTileColumnSamples(string[] args)
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

            MetricTileForm.RenderSamples(outputDir);
            Console.WriteLine("Rendered metric tile column samples to " + Path.GetFullPath(outputDir));
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

    private static int RenderResetSpeedBoardSample(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir)) outputDir = ".";
            ResetSpeedBoardForm.RenderSample(outputDir);
            Console.WriteLine("Rendered Reset / Speed board sample to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderSystemDayBoardSample(string[] args)
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDir = GetStringArg(args, "--out");
            if (string.IsNullOrEmpty(outputDir)) outputDir = ".";
            SystemDayBoardForm.RenderSample(outputDir);
            Console.WriteLine("Rendered System Day board sample to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderGuardBoardSamples(string[] args)
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

            string mode = GetStringArg(args, "--render-guard");
            bool sample = string.IsNullOrEmpty(mode) || string.Equals(mode, "sample", StringComparison.OrdinalIgnoreCase);
            bool current = string.IsNullOrEmpty(mode) || string.Equals(mode, "current", StringComparison.OrdinalIgnoreCase);
            if (!sample && !current)
            {
                throw new ArgumentException("--render-guard mode must be sample or current.");
            }

            GuardBoardForm.RenderSamples(outputDir, sample, current);
            Console.WriteLine("Rendered guard board samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderSpecBoardSamples(string[] args)
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

            string mode = GetStringArg(args, "--render-specboard");
            bool sample = string.IsNullOrEmpty(mode) || string.Equals(mode, "sample", StringComparison.OrdinalIgnoreCase);
            bool current = string.IsNullOrEmpty(mode) || string.Equals(mode, "current", StringComparison.OrdinalIgnoreCase);
            if (!sample && !current)
            {
                throw new ArgumentException("--render-specboard mode must be sample or current.");
            }

            SpecBoardForm.RenderSamples(outputDir, sample, current);
            Console.WriteLine("Rendered Spec Board samples to " + Path.GetFullPath(outputDir));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static int RenderSpecBoardManagerSamples(string[] args)
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

            string mode = GetStringArg(args, "--render-specboardmanager");
            bool sample = string.IsNullOrEmpty(mode) || string.Equals(mode, "sample", StringComparison.OrdinalIgnoreCase);
            bool current = string.IsNullOrEmpty(mode) || string.Equals(mode, "current", StringComparison.OrdinalIgnoreCase);
            if (!sample && !current)
            {
                throw new ArgumentException("--render-specboardmanager mode must be sample or current.");
            }

            SpecBoardManagerForm.RenderSamples(outputDir, sample, current);
            Console.WriteLine("Rendered Spec Board manager samples to " + Path.GetFullPath(outputDir));
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
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                IsArgumentValue(args[i + 1]))
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

            DateTime nowUtc = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
            if (!ShouldRestartAfterFatalException(DateTime.MinValue, nowUtc) ||
                ShouldRestartAfterFatalException(nowUtc.AddMinutes(-5), nowUtc) ||
                !ShouldRestartAfterFatalException(nowUtc.AddMinutes(-16), nowUtc))
            {
                throw new InvalidOperationException("Fatal exception restart suppression policy failed.");
            }

            FatalRestartBudget.RunSelfTest();
            EdgeDockTabForm.RunDisplayLifecycleSelfTest();
            using (Process currentProcess = Process.GetCurrentProcess())
            {
                if (!IsRestartTargetIdentityMatch(currentProcess, Application.ExecutablePath) ||
                    IsRestartTargetIdentityMatch(currentProcess, Path.Combine(Path.GetTempPath(), "fake-desktopcodex.exe")))
                {
                    throw new InvalidOperationException("Fatal restart process identity policy failed.");
                }
            }

            int currentPid = Process.GetCurrentProcess().Id;
            string restartArguments = BuildRestartArguments(currentPid, true);
            if (restartArguments.IndexOf(
                    "--restart-after-pid " + currentPid.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) < 0 ||
                restartArguments.IndexOf("--desktop-parent", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Fatal exception restart argument policy failed.");
            }

            Console.WriteLine("Display recovery layered surface and fatal restart policy: PASS");
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
            settings.CodexRadarPublicJsonEnabled = false;
            settings.CodexRadarHtmlFallbackEnabled = false;
            settings.CodexRadarRssFallbackEnabled = false;
            settings.CodexRadarRandomTestEnabled = true;
            settings.Normalize();

            string quotaHistoryTestPath = Path.Combine(
                Path.GetTempPath(),
                ProductIdentity.MachineName + "-radar-lifecycle-" + Guid.NewGuid().ToString("N") + ".jsonl");
            using (CodexRadarForm codex = new CodexRadarForm(settings, null, quotaHistoryTestPath))
            using (PowerThermalForm power = new PowerThermalForm(settings))
            {
                EdgeDockTabRole[] dockRoles =
                {
                    EdgeDockTabRole.Network,
                    EdgeDockTabRole.SpecBoard,
                    EdgeDockTabRole.CodexTask,
                    EdgeDockTabRole.Guard,
                    EdgeDockTabRole.CodexIq,
                    EdgeDockTabRole.ResetSpeed,
                    EdgeDockTabRole.SystemDay
                };
                EdgeDockTabForm[] dockTabs = new EdgeDockTabForm[dockRoles.Length];
                codex.StartHeadlessDataOwner();
                power.StartHeadlessDataOwner();
                IntPtr codexHandle = codex.Handle;
                if (codexHandle == IntPtr.Zero || codex.Visible || !codex.IsHeadlessDataOwner || !codex.IsBackendSchedulerRunning)
                {
                    throw new InvalidOperationException("Radar display lifecycle self-test failed to start the hidden data owner.");
                }
                if (!power.IsHeadlessDataOwnerRunningForSelfTest() || power.BuildStripSnapshot() == null)
                {
                    throw new InvalidOperationException("Power display lifecycle self-test failed to start the hidden data owner or publish a cache-only snapshot.");
                }

                try
                {
                    Rectangle workArea = LeftDockLayout.ResolveWorkArea(settings);
                    for (int roleIndex = 0; roleIndex < dockRoles.Length; roleIndex++)
                    {
                        dockTabs[roleIndex] = new EdgeDockTabForm(
                            settings,
                            EdgeDockTabForm.ResolveQueueAccent(dockRoles[roleIndex]),
                            BurnInProtection.NetworkMonitorDockTabSalt + roleIndex,
                            "EdgeDockResourceLifecycle" + roleIndex.ToString(CultureInfo.InvariantCulture),
                            dockRoles[roleIndex]);
                        dockTabs[roleIndex].ShowTab(workArea.Top + workArea.Height / 2);
                    }

                    Application.DoEvents();
                    ForceResourceCleanup();
                    RadarRuntimeDiagnostics.ResourceCounters before = RadarRuntimeDiagnostics.CaptureCurrentProcessResources();
                    for (int i = 0; i < iterations; i++)
                    {
                        int codexResumeCount = codex.ResumePrimeCountForSelfTest;
                        int powerResumeCount = power.ResumePrimeCountForSelfTest;
                        codex.PrepareForDisplaySuspend();
                        power.PrepareForDisplaySuspend();
                        codex.PrepareForDisplaySuspend();
                        power.PrepareForDisplaySuspend();
                        if (codex.IsPollingAllowedForSelfTest() || power.IsSamplingAllowedForSelfTest())
                        {
                            throw new InvalidOperationException("Headless owner suspend gate remained open.");
                        }
                        for (int roleIndex = 0; roleIndex < dockTabs.Length; roleIndex++)
                        {
                            dockTabs[roleIndex].SetDisplaySuspended(true);
                            dockTabs[roleIndex].ShowTab(workArea.Top + workArea.Height / 2);
                        }

                        Application.DoEvents();
                        codex.RecoverAfterDisplayResume();
                        power.RecoverAfterDisplayResume();
                        codex.RecoverAfterDisplayResume();
                        power.RecoverAfterDisplayResume();
                        if (!codex.IsPollingAllowedForSelfTest() || !power.IsSamplingAllowedForSelfTest() ||
                            codex.ResumePrimeCountForSelfTest != codexResumeCount + 1 ||
                            power.ResumePrimeCountForSelfTest != powerResumeCount + 1)
                        {
                            throw new InvalidOperationException("Headless owner resume was not idempotent.");
                        }
                        for (int roleIndex = 0; roleIndex < dockTabs.Length; roleIndex++)
                        {
                            dockTabs[roleIndex].SetDisplaySuspended(false);
                            dockTabs[roleIndex].ShowTab(workArea.Top + workArea.Height / 2);
                        }

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
                            "Radar/EdgeDock display lifecycle resource growth exceeded threshold. Iterations=" +
                            iterations.ToString(CultureInfo.InvariantCulture) +
                            ", HandlesDelta=" +
                            handleDelta.ToString(CultureInfo.InvariantCulture) +
                            ", GdiDelta=" +
                            gdiDelta.ToString(CultureInfo.InvariantCulture) +
                            ", UserDelta=" +
                            userDelta.ToString(CultureInfo.InvariantCulture));
                    }

                    Console.WriteLine(
                        "Radar/EdgeDock display lifecycle policy: PASS iterations={0} handles_delta={1} gdi_delta={2} user_delta={3}",
                        iterations,
                        handleDelta,
                        gdiDelta,
                        userDelta);
                }
                finally
                {
                    codex.StopHeadlessDataOwner();
                    power.StopHeadlessDataOwner();
                    if (!power.IsHeadlessDataOwnerStoppedForSelfTest())
                    {
                        throw new InvalidOperationException("Power display lifecycle self-test failed to stop the hidden data owner idempotently.");
                    }
                    for (int i = 0; i < dockTabs.Length; i++)
                    {
                        if (dockTabs[i] != null)
                        {
                            dockTabs[i].Dispose();
                        }
                    }
                }
            }
            try { File.Delete(quotaHistoryTestPath); } catch { }

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
            CodexIqBoardForm.RunSelfTest();
            ResetSpeedBoardForm.RunSelfTest();
            SystemDayHistoryStore.RunSelfTest();
            SystemDayBoardForm.RunSelfTest();
            RunCtfmonRestartHelperArgumentSelfTest();
            RunCommandLineArgumentParserSelfTest();
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

    internal static bool IsRestartTargetIdentityMatch(Process process, string expectedExecutablePath)
    {
        if (process == null || string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            return false;
        }

        try
        {
            string actual = process.MainModule == null ? string.Empty : process.MainModule.FileName;
            return string.Equals(
                Path.GetFullPath(actual),
                Path.GetFullPath(expectedExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void RunCommandLineArgumentParserSelfTest()
    {
        string[] guardWithoutMode = new string[] { "--render-guard", "--out", "X" };
        string[] guardWithMode = new string[] { "--render-guard", "sample", "--out", "X" };
        string[] specWithoutMode = new string[] { "--render-specboard", "--out", "X" };
        string[] managerWithoutMode = new string[] { "--render-specboardmanager", "--out", "X" };
        if (GetStringArg(guardWithoutMode, "--render-guard") != null ||
            !string.Equals(GetStringArg(guardWithMode, "--render-guard"), "sample", StringComparison.Ordinal) ||
            GetStringArg(specWithoutMode, "--render-specboard") != null ||
            GetStringArg(managerWithoutMode, "--render-specboardmanager") != null)
        {
            throw new InvalidOperationException("Render mode argument boundary self-test failed.");
        }

        string value;
        int integerValue;
        if (!TryGetStringArg(new string[] { "--correlation-id", "abc" }, "--correlation-id", out value) ||
            !string.Equals(value, "abc", StringComparison.Ordinal) ||
            TryGetStringArg(new string[] { "--correlation-id", "--out" }, "--correlation-id", out value) ||
            TryGetIntArg(new string[] { "--iterations", "--out" }, "--iterations", out integerValue))
        {
            throw new InvalidOperationException("Command-line value boundary self-test failed.");
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
