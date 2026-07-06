using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class RadarRuntimeDiagnostics
{
    private const int MinSeconds = 2;
    private const int MaxSeconds = 600;
    private const int SampleIntervalMilliseconds = 1000;
    private const int GrGdiObjects = 0;
    private const int GrUserObjects = 1;

    internal sealed class Report
    {
        public string Summary { get; set; }
        public string TextPath { get; set; }
        public string JsonPath { get; set; }
    }

    internal sealed class ResourceCounters
    {
        public int HandleCount { get; set; }
        public int GdiObjects { get; set; }
        public int UserObjects { get; set; }
    }

    private sealed class Sample
    {
        public string TimestampUtc { get; set; }
        public double CpuPercent { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }
        public int HandleCount { get; set; }
        public int GdiObjects { get; set; }
        public int UserObjects { get; set; }
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr processHandle, int flags);

    public static Report Run(int seconds)
    {
        return Run(seconds, 0, string.Empty);
    }

    public static Report Run(int seconds, int targetPid, string label)
    {
        seconds = NormalizeSeconds(seconds);
        DateTime startedUtc = DateTime.UtcNow;
        Process process = targetPid > 0
            ? Process.GetProcessById(targetPid)
            : Process.GetCurrentProcess();
        string processName = process.ProcessName;
        int processId = process.Id;
        string safeLabel = NormalizeLabel(label);
        List<Sample> samples;
        try
        {
            samples = CollectSamples(process, seconds);
        }
        finally
        {
            if (targetPid > 0)
            {
                process.Dispose();
            }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Logger.DirectoryPath);
        string nameSuffix = string.IsNullOrWhiteSpace(safeLabel)
            ? stamp
            : safeLabel + "-" + stamp;
        string textPath = Path.Combine(Logger.DirectoryPath, "radar-runtime-diagnosis-" + nameSuffix + ".txt");
        string jsonPath = Path.Combine(Logger.DirectoryPath, "radar-runtime-diagnosis-" + nameSuffix + ".json");
        string latestPath = Path.Combine(Logger.DirectoryPath, "radar-runtime-diagnosis-latest.txt");
        string summary = BuildSummary(samples);
        string text = BuildTextReport(startedUtc, seconds, summary, samples, processName, processId, safeLabel);
        File.WriteAllText(textPath, text, Encoding.UTF8);
        File.WriteAllText(latestPath, text, Encoding.UTF8);
        File.WriteAllText(jsonPath, BuildJsonReport(startedUtc, seconds, summary, samples, processName, processId, safeLabel), Encoding.UTF8);
        Program.LogInfo("Radar runtime diagnosis completed. Seconds=" + seconds.ToString(CultureInfo.InvariantCulture) + ", TargetPid=" + processId.ToString(CultureInfo.InvariantCulture) + ", Report=" + textPath);
        return new Report
        {
            Summary = summary,
            TextPath = textPath,
            JsonPath = jsonPath
        };
    }

    internal static void RunSelfTest()
    {
        if (NormalizeSeconds(1) != MinSeconds || NormalizeSeconds(MaxSeconds + 1) != MaxSeconds)
        {
            throw new InvalidOperationException("Radar runtime diagnostics self-test failed: seconds clamp.");
        }

        if (!string.Equals(NormalizeLabel(" both windows / hot "), "both-windows-hot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Radar runtime diagnostics self-test failed: label normalization.");
        }

        List<Sample> samples = new List<Sample>();
        samples.Add(new Sample
        {
            CpuPercent = 1.0,
            WorkingSetBytes = 10,
            PrivateBytes = 20,
            HandleCount = 30,
            GdiObjects = 4,
            UserObjects = 5
        });
        samples.Add(new Sample
        {
            CpuPercent = 3.0,
            WorkingSetBytes = 14,
            PrivateBytes = 25,
            HandleCount = 33,
            GdiObjects = 6,
            UserObjects = 8
        });
        string summary = BuildSummary(samples);
        if (summary.IndexOf("cpu_avg=2.00", StringComparison.OrdinalIgnoreCase) < 0 ||
            summary.IndexOf("gdi_delta=2", StringComparison.OrdinalIgnoreCase) < 0 ||
            summary.IndexOf("user_delta=3", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Radar runtime diagnostics self-test failed: summary.");
        }
    }

    internal static ResourceCounters CaptureCurrentProcessResources()
    {
        using (Process process = Process.GetCurrentProcess())
        {
            return CaptureProcessResources(process);
        }
    }

    internal static ResourceCounters CaptureProcessResources(Process process)
    {
        if (process == null)
        {
            return new ResourceCounters();
        }

        process.Refresh();
        return new ResourceCounters
        {
            HandleCount = process.HandleCount,
            GdiObjects = SafeGetGuiResources(process.Handle, GrGdiObjects),
            UserObjects = SafeGetGuiResources(process.Handle, GrUserObjects)
        };
    }

    private static List<Sample> CollectSamples(Process process, int seconds)
    {
        List<Sample> samples = new List<Sample>();
        TimeSpan previousCpu = process.TotalProcessorTime;
        DateTime previousUtc = DateTime.UtcNow;
        int sampleCount = Math.Max(2, seconds + 1);
        for (int i = 0; i < sampleCount; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(SampleIntervalMilliseconds);
            }

            process.Refresh();
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan currentCpu = process.TotalProcessorTime;
            double elapsedSeconds = Math.Max(0.001, (nowUtc - previousUtc).TotalSeconds);
            double cpuPercent = i == 0
                ? 0.0
                : Math.Max(0.0, (currentCpu - previousCpu).TotalMilliseconds / (elapsedSeconds * 1000.0 * Math.Max(1, Environment.ProcessorCount)) * 100.0);
            samples.Add(new Sample
            {
                TimestampUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture),
                CpuPercent = cpuPercent,
                WorkingSetBytes = process.WorkingSet64,
                PrivateBytes = process.PrivateMemorySize64,
                HandleCount = process.HandleCount,
                GdiObjects = SafeGetGuiResources(process.Handle, GrGdiObjects),
                UserObjects = SafeGetGuiResources(process.Handle, GrUserObjects)
            });
            previousCpu = currentCpu;
            previousUtc = nowUtc;
        }

        return samples;
    }

    private static int SafeGetGuiResources(IntPtr processHandle, int flags)
    {
        try
        {
            return GetGuiResources(processHandle, flags);
        }
        catch
        {
            return -1;
        }
    }

    private static string BuildSummary(List<Sample> samples)
    {
        if (samples == null || samples.Count == 0)
        {
            return "samples=0";
        }

        double cpuSum = 0.0;
        double cpuMax = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            cpuSum += samples[i].CpuPercent;
            cpuMax = Math.Max(cpuMax, samples[i].CpuPercent);
        }

        Sample first = samples[0];
        Sample last = samples[samples.Count - 1];
        return string.Format(
            CultureInfo.InvariantCulture,
            "samples={0}; cpu_avg={1:0.00}; cpu_max={2:0.00}; working_set_mb={3:0.0}; private_mb={4:0.0}; handles_delta={5}; gdi_delta={6}; user_delta={7}",
            samples.Count,
            cpuSum / samples.Count,
            cpuMax,
            last.WorkingSetBytes / 1048576.0,
            last.PrivateBytes / 1048576.0,
            last.HandleCount - first.HandleCount,
            last.GdiObjects - first.GdiObjects,
            last.UserObjects - first.UserObjects);
    }

    private static string BuildTextReport(
        DateTime startedUtc,
        int seconds,
        string summary,
        List<Sample> samples,
        string processName,
        int processId,
        string label)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Radar runtime diagnosis");
        builder.AppendLine("started_utc=" + startedUtc.ToString("o", CultureInfo.InvariantCulture));
        builder.AppendLine("seconds=" + seconds.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("process=" + (processName ?? string.Empty));
        builder.AppendLine("pid=" + processId.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("label=" + (label ?? string.Empty));
        builder.AppendLine("summary=" + summary);
        builder.AppendLine();
        builder.AppendLine("timestamp_utc,cpu_percent,working_set_bytes,private_bytes,handle_count,gdi_objects,user_objects");
        for (int i = 0; i < samples.Count; i++)
        {
            Sample sample = samples[i];
            builder.Append(sample.TimestampUtc);
            builder.Append(',');
            builder.Append(sample.CpuPercent.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.WorkingSetBytes.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.PrivateBytes.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.HandleCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(sample.GdiObjects.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(sample.UserObjects.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildJsonReport(
        DateTime startedUtc,
        int seconds,
        string summary,
        List<Sample> samples,
        string processName,
        int processId,
        string label)
    {
        Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
        root["schema_version"] = 1;
        root["timestamp_utc"] = startedUtc.ToString("o", CultureInfo.InvariantCulture);
        root["seconds"] = seconds;
        root["process"] = processName ?? string.Empty;
        root["pid"] = processId;
        root["label"] = label ?? string.Empty;
        root["summary"] = summary;
        root["samples"] = samples;
        return new JavaScriptSerializer().Serialize(root);
    }

    private static int NormalizeSeconds(int seconds)
    {
        if (seconds <= 0)
        {
            seconds = 10;
        }

        return Math.Max(MinSeconds, Math.Min(MaxSeconds, seconds));
    }

    private static string NormalizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        string trimmed = label.Trim();
        for (int i = 0; i < trimmed.Length && builder.Length < 48; i++)
        {
            char ch = trimmed[i];
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9'))
            {
                builder.Append(ch);
                continue;
            }

            if ((ch == '-' || ch == '_' || char.IsWhiteSpace(ch) || ch == '/' || ch == '\\') &&
                builder.Length > 0 &&
                builder[builder.Length - 1] != '-')
            {
                builder.Append('-');
            }
        }

        while (builder.Length > 0 && builder[builder.Length - 1] == '-')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
