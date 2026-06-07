using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
    internal const string RunValueName = ProductIdentity.MachineName;
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool performanceModeKnown;
    private static WidgetPerformanceMode activePerformanceMode;

    [STAThread]
    private static int Main(string[] args)
    {
        MigrateLegacyStorage();
        MigrateLegacyStartup();

        int restartAfterPid;
        if (TryGetIntArg(args, "--restart-after-pid", out restartAfterPid))
        {
            WaitForRestartTargetExit(restartAfterPid);
        }

        bool useDesktopParent = HasArg(args, "--desktop-parent") || HasArg(args, "--workerw");
        LogInfo("Starting. Args=[" + string.Join(" ", args) + "], " + NativeMethods.DescribeProcessMachine());

        if (HasArg(args, "--stop"))
        {
            LogInfo("Stop requested.");
            SignalStop();
            SignalStop(ProductIdentity.LegacyStopEventName);
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

        // Stop a pre-rename process before acquiring the new product mutex.
        SignalStop(ProductIdentity.LegacyStopEventName);

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
            runKey.DeleteValue(ProductIdentity.LegacyRunValueName, false);
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
                runKey.DeleteValue(ProductIdentity.LegacyRunValueName, false);
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
        if (performanceModeKnown && activePerformanceMode == mode)
        {
            return;
        }

        bool powerSaving = WidgetSettings.ShouldEnableProcessPowerSaving(mode);
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
        activePerformanceMode = mode;
        LogInfo(string.Format(
            "Performance mode {0}. ProcessPowerSaving={1}, PowerThrottling={2}, Priority={3}",
            mode,
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
                string sampleText = string.Format(
                    "{0} {1:0}% {2} | Memory {3:0.0}/{4:0.0} GB ({5:0}%) | Disk WT {6} RD {7} | GPU {8:0}% {9:0.0}/{10:0.#} GB | NPU {11:0}% {12:0.0}/{13:0.#} GB | Network {14} UP {15} DL {16}",
                    snapshot.CpuName,
                    snapshot.CpuPercent,
                    FormatCpuFrequencyPair(snapshot.CpuFrequencyGhz, snapshot.CpuBaseFrequencyGhz),
                    snapshot.MemoryUsedGb,
                    snapshot.MemoryTotalGb,
                    snapshot.MemoryPercent,
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
                    NetworkRateFormatter.Format(snapshot.NetworkReceivedBytesPerSecond));
                Console.WriteLine(sampleText);
                LogInfo("Test sample: " + sampleText);
                Console.WriteLine("Process: {0}", NativeMethods.DescribeProcessMachine());
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

    private static void MigrateLegacyStorage()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string legacyDirectory = Path.Combine(localAppData, ProductIdentity.LegacyStorageDirectoryName);
            string currentDirectory = Path.Combine(localAppData, ProductIdentity.MachineName);
            if (Directory.Exists(currentDirectory) || !Directory.Exists(legacyDirectory))
            {
                return;
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
        }
        catch
        {
            // Migration is best-effort; startup must continue when legacy data is inaccessible.
        }
    }

    private static void MigrateLegacyStartup()
    {
        try
        {
            using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (runKey == null || runKey.GetValue(RunValueName) != null)
                {
                    return;
                }

                object legacyValue = runKey.GetValue(ProductIdentity.LegacyRunValueName);
                if (legacyValue == null)
                {
                    return;
                }

                string legacyCommand = legacyValue.ToString();
                string command = Quote(Application.ExecutablePath);
                if (legacyCommand.IndexOf("--desktop-parent", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    command += " --desktop-parent";
                }

                runKey.SetValue(RunValueName, command, RegistryValueKind.String);
                runKey.DeleteValue(ProductIdentity.LegacyRunValueName, false);
            }
        }
        catch
        {
            // Startup migration is best-effort and can be retried by Install.ps1.
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
