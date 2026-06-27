using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class IdleCpuDiagnostics
{
    private const int DefaultLookbackMinutes = 30;
    private const int CpuSampleMilliseconds = 1500;
    private const double ProcessHighCpuThresholdPercent = 15.0;
    private const double KernelPrivilegedThresholdPercent = 15.0;
    private const double KernelInterruptDpcThresholdPercent = 5.0;
    private const double UnattributedCpuThresholdPercent = 20.0;

    internal sealed class Report
    {
        public string Summary { get; set; }
        public string ReportPath { get; set; }
        public string JsonPath { get; set; }
        public string PrimarySuspect { get; set; }
    }

    private sealed class CpuSnapshot
    {
        public double TotalPercent;
        public double UserPercent;
        public double PrivilegedPercent;
        public double InterruptPercent;
        public double DpcPercent;
        public double InterruptsPerSecond;
        public int ProcessorQueueLength;
        public string Source;
    }

    private sealed class ProcessSample
    {
        public string Name;
        public int Pid;
        public double CpuPercent;
        public double CpuSeconds;
        public double WorkingSetMb;
        public string Path;
        public string Services;
    }

    private sealed class EventStats
    {
        public int WindowsUpdateEvents;
        public int DefenderEvents;
        public int HyperVSwitchEvents;
        public int ThermalEvents;
        public readonly Dictionary<int, int> WmiClientCounts = new Dictionary<int, int>();
        public readonly Dictionary<string, int> LogReadFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    public static Report Run(int lookbackMinutes)
    {
        if (lookbackMinutes <= 0)
        {
            lookbackMinutes = DefaultLookbackMinutes;
        }

        lookbackMinutes = Math.Max(5, Math.Min(720, lookbackMinutes));
        DateTime startedUtc = DateTime.UtcNow;
        CpuSnapshot cpu = GetCpuDiagnostics();
        List<ProcessSample> topProcesses = SampleTopProcesses();
        EventStats events = ScanRecentEvents(lookbackMinutes);
        string primary = ChoosePrimarySuspect(cpu, topProcesses, events);
        string summary = BuildSummary(primary, cpu, topProcesses, events);

        Directory.CreateDirectory(Logger.DirectoryPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string reportPath = Path.Combine(Logger.DirectoryPath, "idle-cpu-diagnosis-" + stamp + ".txt");
        string jsonPath = Path.Combine(Logger.DirectoryPath, "idle-cpu-diagnosis-" + stamp + ".json");
        string latestPath = Path.Combine(Logger.DirectoryPath, "idle-cpu-diagnosis-latest.txt");

        string text = BuildTextReport(startedUtc, lookbackMinutes, primary, summary, cpu, topProcesses, events);
        File.WriteAllText(reportPath, text, Encoding.UTF8);
        File.WriteAllText(latestPath, text, Encoding.UTF8);
        File.WriteAllText(jsonPath, BuildJsonReport(startedUtc, lookbackMinutes, primary, summary, cpu, topProcesses, events), Encoding.UTF8);

        Program.LogInfo("Idle CPU diagnosis completed. Primary=" + primary + ", Report=" + reportPath);
        return new Report
        {
            Summary = summary,
            ReportPath = reportPath,
            JsonPath = jsonPath,
            PrimarySuspect = primary
        };
    }

    public static Report RunDefault()
    {
        return Run(DefaultLookbackMinutes);
    }

    internal static void RunSelfTest()
    {
        CpuSnapshot cpu = new CpuSnapshot
        {
            TotalPercent = 72.0,
            UserPercent = 10.0,
            PrivilegedPercent = 30.0,
            InterruptPercent = 3.0,
            DpcPercent = 3.0,
            Source = "self_test"
        };
        List<ProcessSample> samples = new List<ProcessSample>();
        EventStats events = new EventStats();
        string suspect = ChoosePrimarySuspect(cpu, samples, events);
        if (suspect.IndexOf("内核", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Idle CPU diagnosis self-test failed: kernel rule.");
        }

        samples.Add(new ProcessSample { Name = "example", Pid = 10, CpuPercent = 35.0 });
        suspect = ChoosePrimarySuspect(cpu, samples, events);
        if (suspect.IndexOf("example", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Idle CPU diagnosis self-test failed: process rule.");
        }
    }

    private static CpuSnapshot GetCpuDiagnostics()
    {
        try
        {
            using (ManagementObjectSearcher processorSearcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorTime,PercentUserTime,PercentPrivilegedTime,PercentInterruptTime,PercentDPCTime,InterruptsPersec FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
            using (ManagementObjectSearcher systemSearcher = new ManagementObjectSearcher(
                "SELECT ProcessorQueueLength FROM Win32_PerfFormattedData_PerfOS_System"))
            {
                ManagementObject processor = First(processorSearcher.Get());
                ManagementObject system = First(systemSearcher.Get());
                return new CpuSnapshot
                {
                    TotalPercent = ReadDouble(processor, "PercentProcessorTime"),
                    UserPercent = ReadDouble(processor, "PercentUserTime"),
                    PrivilegedPercent = ReadDouble(processor, "PercentPrivilegedTime"),
                    InterruptPercent = ReadDouble(processor, "PercentInterruptTime"),
                    DpcPercent = ReadDouble(processor, "PercentDPCTime"),
                    InterruptsPerSecond = ReadDouble(processor, "InterruptsPersec"),
                    ProcessorQueueLength = (int)ReadDouble(system, "ProcessorQueueLength"),
                    Source = "perf_formatted_data"
                };
            }
        }
        catch
        {
            return new CpuSnapshot
            {
                TotalPercent = GetProcessorLoadFallback(),
                Source = "win32_processor_fallback"
            };
        }
    }

    private static List<ProcessSample> SampleTopProcesses()
    {
        Dictionary<int, ProcessSample> before = SnapshotProcesses();
        Thread.Sleep(CpuSampleMilliseconds);
        Dictionary<int, ProcessSample> after = SnapshotProcesses();
        int logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        double elapsedSeconds = CpuSampleMilliseconds / 1000.0;
        List<ProcessSample> rows = new List<ProcessSample>();
        Dictionary<int, string> serviceMap = GetServiceMap();

        foreach (KeyValuePair<int, ProcessSample> pair in after)
        {
            ProcessSample previous;
            if (!before.TryGetValue(pair.Key, out previous))
            {
                continue;
            }

            double delta = pair.Value.CpuSeconds - previous.CpuSeconds;
            if (delta <= 0.0)
            {
                continue;
            }

            ProcessSample row = pair.Value;
            row.CpuPercent = Math.Round(delta / elapsedSeconds / logicalProcessors * 100.0, 2);
            row.CpuSeconds = Math.Round(delta, 3);
            string services;
            row.Services = serviceMap.TryGetValue(row.Pid, out services) ? services : string.Empty;
            rows.Add(row);
        }

        rows.Sort(delegate (ProcessSample left, ProcessSample right)
        {
            return right.CpuPercent.CompareTo(left.CpuPercent);
        });

        if (rows.Count > 15)
        {
            rows.RemoveRange(15, rows.Count - 15);
        }

        return rows;
    }

    private static Dictionary<int, ProcessSample> SnapshotProcesses()
    {
        Dictionary<int, ProcessSample> rows = new Dictionary<int, ProcessSample>();
        Process[] processes = Process.GetProcesses();
        for (int i = 0; i < processes.Length; i++)
        {
            using (Process process = processes[i])
            {
                try
                {
                    if (string.Equals(process.ProcessName, "Idle", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    rows[process.Id] = new ProcessSample
                    {
                        Name = process.ProcessName,
                        Pid = process.Id,
                        CpuSeconds = process.TotalProcessorTime.TotalSeconds,
                        WorkingSetMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
                        Path = SafeProcessPath(process)
                    };
                }
                catch
                {
                }
            }
        }

        return rows;
    }

    private static EventStats ScanRecentEvents(int lookbackMinutes)
    {
        EventStats stats = new EventStats();
        int milliseconds = checked(lookbackMinutes * 60 * 1000);
        ScanLog(stats, "Microsoft-Windows-WMI-Activity/Operational", milliseconds, "wmi");
        ScanLog(stats, "Microsoft-Windows-WindowsUpdateClient/Operational", milliseconds, "update");
        ScanLog(stats, "Microsoft-Windows-Windows Defender/Operational", milliseconds, "defender");
        ScanLog(stats, "System", milliseconds, "system");

        try
        {
            IEnumerable<string> logs = EventLogSession.GlobalSession.GetLogNames();
            foreach (string log in logs)
            {
                if (log != null && log.IndexOf("Hyper-V-VmSwitch", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ScanLog(stats, log, milliseconds, "hyperv");
                }
            }
        }
        catch
        {
        }

        return stats;
    }

    private static void ScanLog(EventStats stats, string logName, int milliseconds, string category)
    {
        try
        {
            string query = "*[System[TimeCreated[timediff(@SystemTime) <= " +
                milliseconds.ToString(CultureInfo.InvariantCulture) + "]]]";
            EventLogQuery eventQuery = new EventLogQuery(logName, PathType.LogName, query);
            using (EventLogReader reader = new EventLogReader(eventQuery))
            {
                for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        string xml = SafeEventXml(record);
                        string provider = SafeProviderName(record);
                        string lower = (provider + " " + xml).ToLowerInvariant();
                        if (category == "wmi")
                        {
                            int pid = ExtractInt(xml, "ClientProcessId");
                            if (pid > 0)
                            {
                                int count;
                                stats.WmiClientCounts.TryGetValue(pid, out count);
                                stats.WmiClientCounts[pid] = count + 1;
                            }
                        }
                        else if (category == "update" || lower.IndexOf("windowsupdate", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            stats.WindowsUpdateEvents++;
                        }
                        else if (category == "defender" || lower.IndexOf("defender", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            stats.DefenderEvents++;
                        }
                        else if (category == "hyperv" || lower.IndexOf("vmswitch", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            stats.HyperVSwitchEvents++;
                        }

                        if (lower.IndexOf("thermal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            lower.IndexOf("kernel-power", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            stats.ThermalEvents++;
                        }
                    }
                }
            }
        }
        catch
        {
            int count;
            stats.LogReadFailures.TryGetValue(logName, out count);
            stats.LogReadFailures[logName] = count + 1;
        }
    }

    private static string ChoosePrimarySuspect(CpuSnapshot cpu, List<ProcessSample> topProcesses, EventStats events)
    {
        ProcessSample top = topProcesses.Count == 0 ? null : topProcesses[0];
        if (top != null && top.CpuPercent >= ProcessHighCpuThresholdPercent)
        {
            return "普通进程: " + top.Name + " pid=" + top.Pid.ToString(CultureInfo.InvariantCulture) +
                " cpu=" + FormatPercent(top.CpuPercent) +
                (string.IsNullOrEmpty(top.Services) ? string.Empty : " services=" + top.Services);
        }

        if (events.WindowsUpdateEvents >= 3 || events.DefenderEvents >= 3)
        {
            return "Windows Update/Defender: update_events=" +
                events.WindowsUpdateEvents.ToString(CultureInfo.InvariantCulture) +
                ", defender_events=" + events.DefenderEvents.ToString(CultureInfo.InvariantCulture);
        }

        if (events.HyperVSwitchEvents >= 3)
        {
            return "Hyper-V vSwitch/虚拟网卡: events=" + events.HyperVSwitchEvents.ToString(CultureInfo.InvariantCulture);
        }

        int wmiPid;
        int wmiCount;
        GetTopWmiClient(events, out wmiPid, out wmiCount);
        if (wmiCount >= 5)
        {
            return "WMI 客户端: pid=" + wmiPid.ToString(CultureInfo.InvariantCulture) +
                " count=" + wmiCount.ToString(CultureInfo.InvariantCulture) +
                " name=" + ResolveProcessName(wmiPid);
        }

        double interruptDpc = cpu.InterruptPercent + cpu.DpcPercent;
        double processSum = SumProcessCpu(topProcesses);
        double unattributed = Math.Max(0.0, cpu.TotalPercent - processSum);
        if (cpu.PrivilegedPercent >= KernelPrivilegedThresholdPercent ||
            interruptDpc >= KernelInterruptDpcThresholdPercent ||
            unattributed >= UnattributedCpuThresholdPercent)
        {
            return "内核/驱动/中断: privileged=" + FormatPercent(cpu.PrivilegedPercent) +
                ", interrupt+dpc=" + FormatPercent(interruptDpc) +
                ", unattributed=" + FormatPercent(unattributed);
        }

        if (events.ThermalEvents > 0)
        {
            return "散热/电源事件: thermal_events=" + events.ThermalEvents.ToString(CultureInfo.InvariantCulture);
        }

        return "未发现明确单一元凶";
    }

    private static string BuildSummary(string primary, CpuSnapshot cpu, List<ProcessSample> topProcesses, EventStats events)
    {
        ProcessSample top = topProcesses.Count == 0 ? null : topProcesses[0];
        string topText = top == null ? "无进程样本" : top.Name + " " + FormatPercent(top.CpuPercent);
        return primary + "；当前 CPU " + FormatPercent(cpu.TotalPercent) +
            "，最高进程 " + topText +
            "，Update/Defender " + (events.WindowsUpdateEvents + events.DefenderEvents).ToString(CultureInfo.InvariantCulture) +
            " 条，vSwitch " + events.HyperVSwitchEvents.ToString(CultureInfo.InvariantCulture) + " 条。";
    }

    private static string BuildTextReport(DateTime startedUtc, int lookbackMinutes, string primary, string summary, CpuSnapshot cpu, List<ProcessSample> topProcesses, EventStats events)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Idle CPU Diagnosis");
        builder.AppendLine("StartedUtc: " + startedUtc.ToString("o", CultureInfo.InvariantCulture));
        builder.AppendLine("LookbackMinutes: " + lookbackMinutes.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("PrimarySuspect: " + primary);
        builder.AppendLine("Summary: " + summary);
        builder.AppendLine();
        builder.AppendLine("CPU:");
        builder.AppendLine("  Total=" + FormatPercent(cpu.TotalPercent) +
            " User=" + FormatPercent(cpu.UserPercent) +
            " Privileged=" + FormatPercent(cpu.PrivilegedPercent) +
            " Interrupt=" + FormatPercent(cpu.InterruptPercent) +
            " DPC=" + FormatPercent(cpu.DpcPercent) +
            " Queue=" + cpu.ProcessorQueueLength.ToString(CultureInfo.InvariantCulture) +
            " Source=" + cpu.Source);
        builder.AppendLine();
        builder.AppendLine("Top processes:");
        for (int i = 0; i < topProcesses.Count; i++)
        {
            ProcessSample row = topProcesses[i];
            builder.AppendLine("  " + row.Name +
                " pid=" + row.Pid.ToString(CultureInfo.InvariantCulture) +
                " cpu=" + FormatPercent(row.CpuPercent) +
                " ws=" + row.WorkingSetMb.ToString("0.0", CultureInfo.InvariantCulture) + "MB" +
                (string.IsNullOrEmpty(row.Services) ? string.Empty : " services=" + row.Services) +
                (string.IsNullOrEmpty(row.Path) ? string.Empty : " path=" + row.Path));
        }

        int wmiPid;
        int wmiCount;
        GetTopWmiClient(events, out wmiPid, out wmiCount);
        builder.AppendLine();
        builder.AppendLine("Recent events:");
        builder.AppendLine("  WindowsUpdate=" + events.WindowsUpdateEvents.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("  Defender=" + events.DefenderEvents.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("  HyperVVmSwitch=" + events.HyperVSwitchEvents.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("  ThermalOrKernelPower=" + events.ThermalEvents.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("  TopWmiClientPid=" + wmiPid.ToString(CultureInfo.InvariantCulture) +
            " Count=" + wmiCount.ToString(CultureInfo.InvariantCulture) +
            " Name=" + ResolveProcessName(wmiPid));
        return builder.ToString();
    }

    private static string BuildJsonReport(DateTime startedUtc, int lookbackMinutes, string primary, string summary, CpuSnapshot cpu, List<ProcessSample> topProcesses, EventStats events)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> root = new Dictionary<string, object>();
        root["schema_version"] = 1;
        root["timestamp_utc"] = startedUtc.ToString("o", CultureInfo.InvariantCulture);
        root["lookback_minutes"] = lookbackMinutes;
        root["primary_suspect"] = primary;
        root["summary"] = summary;
        root["cpu"] = cpu;
        root["top_processes"] = topProcesses;
        root["event_counts"] = new Dictionary<string, object>
        {
            { "windows_update", events.WindowsUpdateEvents },
            { "defender", events.DefenderEvents },
            { "hyper_v_vswitch", events.HyperVSwitchEvents },
            { "thermal", events.ThermalEvents },
            { "wmi_clients", events.WmiClientCounts }
        };
        return serializer.Serialize(root);
    }

    private static ManagementObject First(ManagementObjectCollection collection)
    {
        foreach (ManagementObject item in collection)
        {
            return item;
        }

        return null;
    }

    private static double ReadDouble(ManagementBaseObject obj, string property)
    {
        if (obj == null || obj[property] == null)
        {
            return 0.0;
        }

        return Convert.ToDouble(obj[property], CultureInfo.InvariantCulture);
    }

    private static double GetProcessorLoadFallback()
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
            {
                double total = 0.0;
                int count = 0;
                foreach (ManagementObject processor in searcher.Get())
                {
                    total += ReadDouble(processor, "LoadPercentage");
                    count++;
                }

                return count == 0 ? 0.0 : Math.Round(total / count, 2);
            }
        }
        catch
        {
            return 0.0;
        }
    }

    private static Dictionary<int, string> GetServiceMap()
    {
        Dictionary<int, List<string>> grouped = new Dictionary<int, List<string>>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId,Name FROM Win32_Service WHERE ProcessId > 0"))
            {
                foreach (ManagementObject service in searcher.Get())
                {
                    int pid = Convert.ToInt32(service["ProcessId"], CultureInfo.InvariantCulture);
                    List<string> names;
                    if (!grouped.TryGetValue(pid, out names))
                    {
                        names = new List<string>();
                        grouped[pid] = names;
                    }

                    names.Add(Convert.ToString(service["Name"], CultureInfo.InvariantCulture));
                }
            }
        }
        catch
        {
        }

        Dictionary<int, string> result = new Dictionary<int, string>();
        foreach (KeyValuePair<int, List<string>> pair in grouped)
        {
            result[pair.Key] = string.Join(";", pair.Value.ToArray());
        }

        return result;
    }

    private static string SafeProcessPath(Process process)
    {
        try
        {
            return process.MainModule == null ? string.Empty : process.MainModule.FileName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeEventXml(EventRecord record)
    {
        try
        {
            return record.ToXml() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeProviderName(EventRecord record)
    {
        try
        {
            return record.ProviderName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ExtractInt(string xml, string name)
    {
        Match match = Regex.Match(xml, "Name=['\\\"]" + Regex.Escape(name) + "['\\\"]>(?<value>\\d+)<");
        int value;
        return match.Success && int.TryParse(match.Groups["value"].Value, out value) ? value : 0;
    }

    private static double SumProcessCpu(List<ProcessSample> rows)
    {
        double total = 0.0;
        for (int i = 0; i < rows.Count; i++)
        {
            total += rows[i].CpuPercent;
        }

        return total;
    }

    private static void GetTopWmiClient(EventStats events, out int pid, out int count)
    {
        pid = 0;
        count = 0;
        foreach (KeyValuePair<int, int> pair in events.WmiClientCounts)
        {
            if (pair.Value > count)
            {
                pid = pair.Key;
                count = pair.Value;
            }
        }
    }

    private static string ResolveProcessName(int pid)
    {
        if (pid <= 0)
        {
            return string.Empty;
        }

        try
        {
            using (Process process = Process.GetProcessById(pid))
            {
                return process.ProcessName;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatPercent(double value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
