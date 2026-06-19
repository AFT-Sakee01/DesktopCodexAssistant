using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class PdhSampler : IDisposable
{
    private readonly IntPtr query;
    private readonly IntPtr expensiveQuery;
    private readonly PdhCounter cpuCounter;
    private readonly PdhCounter cpuFrequencyCounter;
    private readonly List<PdhCounter> cpuCoreCounters;
    private readonly PdhCounter diskCounter;
    private readonly PdhCounter diskWriteCounter;
    private readonly PdhCounter diskReadCounter;
    private readonly PdhCounter diskWritePercentCounter;
    private readonly PdhCounter diskReadPercentCounter;
    private readonly List<PdhCounter> networkSentCounters;
    private readonly List<PdhCounter> networkReceivedCounters;
    private readonly List<PdhCounter> gpuEngineCounters;
    private readonly List<PdhCounter> gpuDedicatedMemoryCounters;
    private readonly List<PdhCounter> gpuSharedMemoryCounters;
    private readonly List<PdhCounter> npuEngineCounters;
    private readonly List<PdhCounter> npuDedicatedMemoryCounters;
    private readonly List<PdhCounter> npuSharedMemoryCounters;
    private readonly DiskInfo diskInfo;
    private readonly string cpuName;
    private readonly int cpuCoreCount;
    private readonly double cpuBaseFrequencyGhz;
    private readonly double cpuCurrentFrequencyFallbackGhz;
    private readonly MemoryInfo memoryInfo;
    private readonly string gpuName;
    private readonly double gpuMemoryTotalGb;
    private readonly string npuName;
    private readonly double npuMemoryTotalGb;
    private readonly HashSet<string> npuLuidTokens;
    private NetworkState cachedNetworkState;
    private int networkStateRefreshRequested;
    private DateTime lastNetworkStateRefreshUtc;
    private DateTime lastDiskUsageRefreshUtc;
    private double cachedDiskCapacityPercent;
    private double cachedDiskUsedGb;
    private double cachedDiskTotalGb;
    private DateTime lastExpensiveCounterRefreshUtc;
    private double cachedGpuPercent;
    private double cachedGpuMemoryUsedGb;
    private double cachedGpuMemoryPercent;
    private double cachedNpuPercent;
    private double cachedNpuMemoryUsedGb;
    private double cachedNpuMemoryPercent;
    private bool disposed;
    private const int WifiRssiRefreshIntervalMs = 5000;

    public PdhSampler()
    {
        uint status = PdhNative.PdhOpenQuery(null, IntPtr.Zero, out this.query);
        if (status != PdhNative.ERROR_SUCCESS)
        {
            throw new InvalidOperationException("PdhOpenQuery failed: 0x" + status.ToString("X8"));
        }

        status = PdhNative.PdhOpenQuery(null, IntPtr.Zero, out this.expensiveQuery);
        if (status != PdhNative.ERROR_SUCCESS)
        {
            PdhNative.PdhCloseQuery(this.query);
            throw new InvalidOperationException("PdhOpenQuery for GPU/NPU failed: 0x" + status.ToString("X8"));
        }

        this.cpuCounter = AddFirstAvailable(new string[]
        {
            @"\Processor Information(_Total)\% Processor Utility",
            @"\Processor(_Total)\% Processor Time"
        });
        CpuInfo cpuInfo = DetectCpuInfo();
        this.cpuName = cpuInfo.Name;
        this.cpuCoreCount = cpuInfo.CoreCount;
        this.cpuBaseFrequencyGhz = cpuInfo.BaseFrequencyGhz;
        this.cpuCurrentFrequencyFallbackGhz = cpuInfo.CurrentFrequencyGhz;
        this.cpuFrequencyCounter = AddFirstAvailable(new string[]
        {
            @"\Processor Information(_Total)\Actual Frequency",
            @"\Processor Information(0,_Total)\Actual Frequency"
        });
        this.cpuCoreCounters = AddCpuCoreCounters();
        if (this.cpuCoreCounters.Count > 0)
        {
            this.cpuCoreCount = this.cpuCoreCounters.Count;
        }

        this.memoryInfo = DetectMemoryInfo();

        this.diskInfo = DetectDiskInfo();
        this.diskCounter = AddFirstAvailable(new string[]
        {
            this.diskInfo.CounterPath,
            @"\PhysicalDisk(_Total)\% Disk Time",
            @"\LogicalDisk(C:)\% Disk Time"
        });
        // Throughput and busy-time counters are separate: rates drive the graph, busy time drives alerts.
        this.diskWriteCounter = AddFirstAvailable(new string[]
        {
            ReplaceCounterName(this.diskInfo.CounterPath, "Disk Write Bytes/sec"),
            @"\PhysicalDisk(_Total)\Disk Write Bytes/sec"
        });
        this.diskReadCounter = AddFirstAvailable(new string[]
        {
            ReplaceCounterName(this.diskInfo.CounterPath, "Disk Read Bytes/sec"),
            @"\PhysicalDisk(_Total)\Disk Read Bytes/sec"
        });
        this.diskWritePercentCounter = AddFirstAvailable(new string[]
        {
            ReplaceCounterName(this.diskInfo.CounterPath, "% Disk Write Time"),
            @"\PhysicalDisk(_Total)\% Disk Write Time"
        });
        this.diskReadPercentCounter = AddFirstAvailable(new string[]
        {
            ReplaceCounterName(this.diskInfo.CounterPath, "% Disk Read Time"),
            @"\PhysicalDisk(_Total)\% Disk Read Time"
        });

        this.networkSentCounters = AddCountersFromWildcard(@"\Network Interface(*)\Bytes Sent/sec", ShouldUseNetworkPath);
        this.networkReceivedCounters = AddCountersFromWildcard(@"\Network Interface(*)\Bytes Received/sec", ShouldUseNetworkPath);
        string[] gpuEnginePaths = ExpandWildcard(@"\GPU Engine(*)\Utilization Percentage");
        string[] gpuDedicatedMemoryPaths = ExpandWildcard(@"\GPU Adapter Memory(*)\Dedicated Usage");
        string[] gpuSharedMemoryPaths = ExpandWildcard(@"\GPU Adapter Memory(*)\Shared Usage");
        GpuInfo npuInfo = DetectNpuInfo();
        this.npuLuidTokens = DetectNpuLuidTokens(gpuEnginePaths, npuInfo.IsDetected);
        this.gpuEngineCounters = AddCountersFromPaths(this.expensiveQuery, gpuEnginePaths, delegate(string path) { return !IsNpuPath(path, this.npuLuidTokens); });
        this.gpuDedicatedMemoryCounters = AddCountersFromPaths(this.expensiveQuery, gpuDedicatedMemoryPaths, delegate(string path) { return !IsNpuPath(path, this.npuLuidTokens); });
        this.gpuSharedMemoryCounters = AddCountersFromPaths(this.expensiveQuery, gpuSharedMemoryPaths, delegate(string path) { return !IsNpuPath(path, this.npuLuidTokens); });
        this.npuEngineCounters = AddCountersFromWildcard(this.expensiveQuery, @"\NPU Engine(*)\Utilization Percentage", null);
        if (this.npuEngineCounters.Count == 0)
        {
            this.npuEngineCounters = AddCountersFromPaths(this.expensiveQuery, gpuEnginePaths, delegate(string path) { return IsNpuPath(path, this.npuLuidTokens); });
        }

        this.npuDedicatedMemoryCounters = AddCountersFromWildcard(this.expensiveQuery, @"\NPU Adapter Memory(*)\Dedicated Usage", null);
        if (this.npuDedicatedMemoryCounters.Count == 0)
        {
            this.npuDedicatedMemoryCounters = AddCountersFromPaths(this.expensiveQuery, gpuDedicatedMemoryPaths, delegate(string path) { return IsNpuPath(path, this.npuLuidTokens); });
        }

        this.npuSharedMemoryCounters = AddCountersFromWildcard(this.expensiveQuery, @"\NPU Adapter Memory(*)\Shared Usage", null);
        if (this.npuSharedMemoryCounters.Count == 0)
        {
            this.npuSharedMemoryCounters = AddCountersFromPaths(this.expensiveQuery, gpuSharedMemoryPaths, delegate(string path) { return IsNpuPath(path, this.npuLuidTokens); });
        }

        this.cachedNetworkState = DetectNetworkState();
        this.lastNetworkStateRefreshUtc = DateTime.UtcNow;
        GpuInfo gpuInfo = DetectGpuInfo();
        this.gpuName = gpuInfo.Name;
        this.gpuMemoryTotalGb = gpuInfo.MemoryTotalGb;
        this.npuName = npuInfo.Name;
        this.npuMemoryTotalGb = npuInfo.MemoryTotalGb;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        PdhNative.PdhCollectQueryData(this.query);
        PdhNative.PdhCollectQueryData(this.expensiveQuery);
        Program.LogInfo(string.Format(
            "PDH counters initialized. CPU={0}, CPUName={1}, CPUCores={2}, CPUFreq={3}, CPUBaseGHz={4:0.00}, Disk={5}, NetSent={6}, NetRecv={7}, GPU={8}, GPUMem={9}/{10}, NPU={11}, NPUMem={12}/{13}, NPULuids={14}",
            this.cpuCounter == null ? "none" : this.cpuCounter.Path,
            this.cpuName,
            this.cpuCoreCounters.Count,
            this.cpuFrequencyCounter == null ? "none" : this.cpuFrequencyCounter.Path,
            this.cpuBaseFrequencyGhz,
            this.diskCounter == null ? "none" : this.diskCounter.Path,
            this.networkSentCounters.Count,
            this.networkReceivedCounters.Count,
            this.gpuEngineCounters.Count,
            this.gpuDedicatedMemoryCounters.Count,
            this.gpuSharedMemoryCounters.Count,
            this.npuEngineCounters.Count,
            this.npuDedicatedMemoryCounters.Count,
            this.npuSharedMemoryCounters.Count,
            JoinSet(this.npuLuidTokens)));
        Program.LogInfo(string.Format(
            "Memory hardware initialized. Manufacturer={0}, Speed={1}MT/s",
            this.memoryInfo.Manufacturer,
            this.memoryInfo.SpeedMtps));
    }

    public PerfSnapshot Sample()
    {
        return Sample(0);
    }

    public PerfSnapshot Sample(int expensiveCounterIntervalMs)
    {
        EnsureNotDisposed();
        PdhNative.PdhCollectQueryData(this.query);
        DateTime nowUtc = DateTime.UtcNow;
        if (this.lastExpensiveCounterRefreshUtc == DateTime.MinValue ||
            expensiveCounterIntervalMs <= 0 ||
            (nowUtc - this.lastExpensiveCounterRefreshUtc).TotalMilliseconds >= expensiveCounterIntervalMs)
        {
            // GPU Engine expands to hundreds of per-process counters on modern Windows.
            // Collect and format that query less often than the lightweight CPU/disk query.
            PdhNative.PdhCollectQueryData(this.expensiveQuery);
            RefreshExpensiveCounterCache();
            this.lastExpensiveCounterRefreshUtc = nowUtc;
        }

        PerfSnapshot snapshot = new PerfSnapshot();
        snapshot.CpuName = this.cpuName;
        snapshot.CpuPercent = Clamp(ReadCounter(this.cpuCounter), 0.0, 100.0);
        snapshot.CpuCoreCount = this.cpuCoreCount;
        snapshot.CpuCorePercents = ReadCpuCorePercents();
        snapshot.CpuFrequencyGhz = ReadCpuFrequencyGhz();
        snapshot.CpuBaseFrequencyGhz = this.cpuBaseFrequencyGhz;
        snapshot.MemoryManufacturer = this.memoryInfo.Manufacturer;
        snapshot.MemorySpeedMtps = this.memoryInfo.SpeedMtps;
        snapshot.DiskPercent = Clamp(ReadCounter(this.diskCounter), 0.0, 100.0);
        snapshot.DiskWriteBytesPerSecond = Math.Max(0.0, ReadCounter(this.diskWriteCounter));
        snapshot.DiskReadBytesPerSecond = Math.Max(0.0, ReadCounter(this.diskReadCounter));
        snapshot.DiskWritePercent = Clamp(ReadCounter(this.diskWritePercentCounter), 0.0, 100.0);
        snapshot.DiskReadPercent = Clamp(ReadCounter(this.diskReadPercentCounter), 0.0, 100.0);
        NetworkState networkState = GetCachedNetworkState(nowUtc);
        snapshot.NetworkName = networkState.Name;
        snapshot.NetworkConnected = networkState.Connected;
        snapshot.NetworkIsWifi = networkState.IsWifi;
        snapshot.NetworkRssiKnown = networkState.RssiKnown;
        snapshot.NetworkRssiDbm = networkState.RssiDbm;
        snapshot.DiskName = this.diskInfo.Name;
        snapshot.DiskVolumeLabel = this.diskInfo.DisplayVolumes;
        snapshot.GpuName = this.gpuName;
        snapshot.NpuName = this.npuName;

        double sent = 0.0;
        for (int i = 0; i < this.networkSentCounters.Count; i++)
        {
            sent += Math.Max(0.0, ReadCounter(this.networkSentCounters[i]));
        }

        double received = 0.0;
        for (int i = 0; i < this.networkReceivedCounters.Count; i++)
        {
            received += Math.Max(0.0, ReadCounter(this.networkReceivedCounters[i]));
        }

        if (!snapshot.NetworkConnected)
        {
            sent = 0.0;
            received = 0.0;
        }

        snapshot.NetworkSentBytesPerSecond = sent;
        snapshot.NetworkReceivedBytesPerSecond = received;

        ApplyCachedDiskUsage(snapshot);

        snapshot.GpuPercent = this.cachedGpuPercent;
        snapshot.GpuMemoryUsedGb = this.cachedGpuMemoryUsedGb;
        snapshot.GpuMemoryTotalGb = this.gpuMemoryTotalGb;
        snapshot.GpuMemoryPercent = this.cachedGpuMemoryPercent;
        snapshot.NpuPercent = this.cachedNpuPercent;
        snapshot.NpuMemoryUsedGb = this.cachedNpuMemoryUsedGb;
        snapshot.NpuMemoryTotalGb = this.npuMemoryTotalGb;
        snapshot.NpuMemoryPercent = this.cachedNpuMemoryPercent;

        NativeMethods.MEMORYSTATUSEX memory = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(memory))
        {
            double totalBytes = memory.ullTotalPhys;
            double availableBytes = memory.ullAvailPhys;
            double usedBytes = Math.Max(0.0, totalBytes - availableBytes);
            snapshot.MemoryTotalGb = totalBytes / 1073741824.0;
            snapshot.MemoryUsedGb = usedBytes / 1073741824.0;
            snapshot.MemoryPercent = Clamp(memory.dwMemoryLoad, 0.0, 100.0);
        }

        snapshot.MemoryHardwareReservedGb =
            Math.Max(0.0, snapshot.GpuMemoryUsedGb) +
            Math.Max(0.0, snapshot.NpuMemoryUsedGb);
        if (snapshot.MemoryTotalGb > 0.0)
        {
            snapshot.MemoryHardwareReservedPercent = Clamp(
                snapshot.MemoryHardwareReservedGb * 100.0 / snapshot.MemoryTotalGb,
                0.0,
                100.0);
        }

        return snapshot;
    }

    public void RequestDiskUsageRefresh()
    {
        EnsureNotDisposed();
        this.lastDiskUsageRefreshUtc = DateTime.MinValue;
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            PdhNative.PdhCloseQuery(this.query);
            PdhNative.PdhCloseQuery(this.expensiveQuery);
            this.disposed = true;
        }
    }

    private void RefreshExpensiveCounterCache()
    {
        double gpuPercent = SumCounters(this.gpuEngineCounters);
        double gpuMemoryBytes =
            SumCounters(this.gpuDedicatedMemoryCounters) +
            SumCounters(this.gpuSharedMemoryCounters);
        double npuPercent = SumCounters(this.npuEngineCounters);
        double npuMemoryBytes =
            SumCounters(this.npuDedicatedMemoryCounters) +
            SumCounters(this.npuSharedMemoryCounters);

        this.cachedGpuPercent = Clamp(gpuPercent, 0.0, 100.0);
        this.cachedGpuMemoryUsedGb = gpuMemoryBytes / 1073741824.0;
        this.cachedGpuMemoryPercent = this.gpuMemoryTotalGb > 0.0
            ? Clamp(this.cachedGpuMemoryUsedGb * 100.0 / this.gpuMemoryTotalGb, 0.0, 100.0)
            : 0.0;
        this.cachedNpuPercent = Clamp(npuPercent, 0.0, 100.0);
        this.cachedNpuMemoryUsedGb = npuMemoryBytes / 1073741824.0;
        this.cachedNpuMemoryPercent = this.npuMemoryTotalGb > 0.0
            ? Clamp(this.cachedNpuMemoryUsedGb * 100.0 / this.npuMemoryTotalGb, 0.0, 100.0)
            : 0.0;
    }

    private static double SumCounters(List<PdhCounter> counters)
    {
        double total = 0.0;
        for (int i = 0; i < counters.Count; i++)
        {
            total += Math.Max(0.0, ReadCounter(counters[i]));
        }

        return total;
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        // Network callbacks only invalidate the cache; enumeration stays on the sampler thread.
        Interlocked.Exchange(ref this.networkStateRefreshRequested, 1);
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        Interlocked.Exchange(ref this.networkStateRefreshRequested, 1);
    }

    private NetworkState GetCachedNetworkState(DateTime nowUtc)
    {
        bool refreshRequested = Interlocked.Exchange(ref this.networkStateRefreshRequested, 0) != 0;
        bool rssiRefreshDue =
            this.cachedNetworkState != null &&
            this.cachedNetworkState.Connected &&
            this.cachedNetworkState.IsWifi &&
            (this.lastNetworkStateRefreshUtc == DateTime.MinValue ||
             (nowUtc - this.lastNetworkStateRefreshUtc).TotalMilliseconds >= WifiRssiRefreshIntervalMs);

        if (refreshRequested || rssiRefreshDue || this.cachedNetworkState == null)
        {
            this.cachedNetworkState = DetectNetworkState();
            this.lastNetworkStateRefreshUtc = nowUtc;
        }

        return this.cachedNetworkState;
    }

    private void ApplyCachedDiskUsage(PerfSnapshot snapshot)
    {
        DateTime now = DateTime.UtcNow;
        if (this.lastDiskUsageRefreshUtc == DateTime.MinValue ||
            (now - this.lastDiskUsageRefreshUtc).TotalSeconds >= 60.0)
        {
            // Capacity changes slowly, unlike throughput and busy-time PDH counters.
            PerfSnapshot capacity = new PerfSnapshot();
            ApplyDiskUsage(capacity, this.diskInfo);
            this.cachedDiskCapacityPercent = capacity.DiskCapacityPercent;
            this.cachedDiskUsedGb = capacity.DiskUsedGb;
            this.cachedDiskTotalGb = capacity.DiskTotalGb;
            this.lastDiskUsageRefreshUtc = now;
        }

        snapshot.DiskCapacityPercent = this.cachedDiskCapacityPercent;
        snapshot.DiskUsedGb = this.cachedDiskUsedGb;
        snapshot.DiskTotalGb = this.cachedDiskTotalGb;
    }

    private PdhCounter AddFirstAvailable(string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (string.IsNullOrEmpty(paths[i]))
            {
                continue;
            }

            PdhCounter counter = AddCounter(paths[i]);
            if (counter != null)
            {
                return counter;
            }
        }

        return null;
    }

    private static string ReplaceCounterName(string counterPath, string counterName)
    {
        if (string.IsNullOrEmpty(counterPath) || string.IsNullOrEmpty(counterName))
        {
            return string.Empty;
        }

        int separator = counterPath.LastIndexOf('\\');
        if (separator <= 0)
        {
            return string.Empty;
        }

        return counterPath.Substring(0, separator + 1) + counterName;
    }

    private List<PdhCounter> AddCpuCoreCounters()
    {
        string[] paths = ExpandWildcard(@"\Processor Information(*)\% Processor Utility");
        Array.Sort(paths, CompareCpuCounterPaths);
        List<PdhCounter> counters = AddCountersFromPaths(paths, ShouldUseCpuCorePath);
        if (counters.Count > 0)
        {
            return counters;
        }

        paths = ExpandWildcard(@"\Processor(*)\% Processor Time");
        Array.Sort(paths, CompareCpuCounterPaths);
        return AddCountersFromPaths(paths, ShouldUseCpuCorePath);
    }

    private double[] ReadCpuCorePercents()
    {
        if (this.cpuCoreCounters == null || this.cpuCoreCounters.Count == 0)
        {
            return new double[0];
        }

        double[] values = new double[this.cpuCoreCounters.Count];
        for (int i = 0; i < this.cpuCoreCounters.Count; i++)
        {
            values[i] = Clamp(ReadCounter(this.cpuCoreCounters[i]), 0.0, 100.0);
        }

        return values;
    }

    private double ReadCpuFrequencyGhz()
    {
        double mhz = ReadCounter(this.cpuFrequencyCounter);
        if (mhz > 0.0)
        {
            return mhz / 1000.0;
        }

        return this.cpuCurrentFrequencyFallbackGhz;
    }

    private List<PdhCounter> AddCountersFromWildcard(string wildcardPath)
    {
        return AddCountersFromWildcard(wildcardPath, null);
    }

    private List<PdhCounter> AddCountersFromWildcard(string wildcardPath, Predicate<string> shouldUsePath)
    {
        return AddCountersFromWildcard(this.query, wildcardPath, shouldUsePath);
    }

    private List<PdhCounter> AddCountersFromWildcard(IntPtr targetQuery, string wildcardPath, Predicate<string> shouldUsePath)
    {
        string[] paths = ExpandWildcard(wildcardPath);
        return AddCountersFromPaths(targetQuery, paths, shouldUsePath);
    }

    private List<PdhCounter> AddCountersFromPaths(string[] paths, Predicate<string> shouldUsePath)
    {
        return AddCountersFromPaths(this.query, paths, shouldUsePath);
    }

    private List<PdhCounter> AddCountersFromPaths(IntPtr targetQuery, string[] paths, Predicate<string> shouldUsePath)
    {
        List<PdhCounter> counters = new List<PdhCounter>();
        for (int i = 0; i < paths.Length; i++)
        {
            if (shouldUsePath != null && !shouldUsePath(paths[i]))
            {
                continue;
            }

            PdhCounter counter = AddCounter(targetQuery, paths[i]);
            if (counter != null)
            {
                counters.Add(counter);
            }
        }

        return counters;
    }

    private PdhCounter AddCounter(string path)
    {
        return AddCounter(this.query, path);
    }

    private static PdhCounter AddCounter(IntPtr targetQuery, string path)
    {
        IntPtr counterHandle;
        uint status = PdhNative.PdhAddEnglishCounter(targetQuery, path, IntPtr.Zero, out counterHandle);
        if (status == PdhNative.ERROR_SUCCESS)
        {
            return new PdhCounter(counterHandle, path);
        }

        return null;
    }

    private static string[] ExpandWildcard(string wildcardPath)
    {
        uint size = 0;
        uint status = PdhNative.PdhExpandWildCardPath(null, wildcardPath, null, ref size, 0);
        if (status != PdhNative.PDH_MORE_DATA || size == 0)
        {
            return ExpandWildcardWithCategory(wildcardPath);
        }

        StringBuilder buffer = new StringBuilder((int)size);
        status = PdhNative.PdhExpandWildCardPath(null, wildcardPath, buffer, ref size, 0);
        if (status != PdhNative.ERROR_SUCCESS)
        {
            return ExpandWildcardWithCategory(wildcardPath);
        }

        string[] paths = buffer.ToString().Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length <= 1 && wildcardPath.IndexOf("(*)", StringComparison.Ordinal) >= 0)
        {
            string[] categoryPaths = ExpandWildcardWithCategory(wildcardPath);
            if (categoryPaths.Length > paths.Length)
            {
                return categoryPaths;
            }
        }

        return paths;
    }

    private static string[] ExpandWildcardWithCategory(string wildcardPath)
    {
        try
        {
            int open = wildcardPath.IndexOf("(*)", StringComparison.Ordinal);
            if (open <= 1 || !wildcardPath.StartsWith("\\", StringComparison.Ordinal))
            {
                return new string[0];
            }

            int counterStart = open + 3;
            if (counterStart >= wildcardPath.Length || wildcardPath[counterStart] != '\\')
            {
                return new string[0];
            }

            string categoryName = wildcardPath.Substring(1, open - 1);
            string counterName = wildcardPath.Substring(counterStart + 1);
            PerformanceCounterCategory category = new PerformanceCounterCategory(categoryName);
            string[] instances = category.GetInstanceNames();
            List<string> paths = new List<string>();
            for (int i = 0; i < instances.Length; i++)
            {
                if (string.IsNullOrEmpty(instances[i]))
                {
                    continue;
                }

                paths.Add("\\" + categoryName + "(" + instances[i] + ")\\" + counterName);
            }

            return paths.ToArray();
        }
        catch
        {
            return new string[0];
        }
    }

    private static bool ShouldUseNetworkPath(string path)
    {
        string lower = path.ToLowerInvariant();
        if (lower.IndexOf("loopback", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        if (lower.IndexOf("isatap", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        if (lower.IndexOf("teredo", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        // Keep virtual-switch traffic out of the primary network graph.
        if (lower.IndexOf("vethernet", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("hyper-v", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("hyper_v", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("vswitch", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("wsl", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("virtual ethernet", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("wan miniport", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldUseCpuCorePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static int CompareCpuCounterPaths(string left, string right)
    {
        int leftKey = ExtractCpuCounterSortKey(left);
        int rightKey = ExtractCpuCounterSortKey(right);
        if (leftKey != rightKey)
        {
            return leftKey.CompareTo(rightKey);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractCpuCounterSortKey(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return int.MaxValue;
        }

        int open = path.IndexOf('(');
        int close = path.IndexOf(')', open + 1);
        if (open < 0 || close <= open)
        {
            return int.MaxValue - 1;
        }

        string instance = path.Substring(open + 1, close - open - 1);
        if (instance.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return int.MaxValue;
        }

        string[] parts = instance.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        int group = 0;
        int core = 0;
        if (parts.Length == 1)
        {
            int.TryParse(parts[0], out core);
            return core;
        }

        int.TryParse(parts[0], out group);
        int.TryParse(parts[1], out core);
        return group * 10000 + core;
    }

    private static NetworkState DetectNetworkState()
    {
        NetworkState state = new NetworkState();
        state.Name = "Network";
        state.Connected = false;

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            NetworkInterface best = null;
            long bestScore = long.MinValue;
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface item = interfaces[i];
                if (item.OperationalStatus != OperationalStatus.Up ||
                    item.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    item.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    IsVirtualNetworkInterface(item))
                {
                    continue;
                }

                // Prefer the interface that actually owns the default route, then physical link quality.
                long score = ScoreNetworkInterface(item);
                if (best == null || score > bestScore)
                {
                    best = item;
                    bestScore = score;
                }
            }

            if (best != null)
            {
                state.Connected = true;
                state.IsWifi = best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                WifiConnectionDetails wifi = GetWifiDetails(best);
                if (wifi != null)
                {
                    state.RssiKnown = wifi.RssiKnown;
                    state.RssiDbm = wifi.RssiDbm;
                    if (!string.IsNullOrEmpty(wifi.Ssid))
                    {
                        state.Name = wifi.Ssid;
                        return state;
                    }
                }

                if (!string.IsNullOrEmpty(best.Name))
                {
                    state.Name = best.Name;
                    return state;
                }

                if (!string.IsNullOrEmpty(best.Description))
                {
                    state.Name = best.Description;
                }

                return state;
            }
        }
        catch
        {
        }

        return state;
    }

    private static long ScoreNetworkInterface(NetworkInterface item)
    {
        long score = 0;
        try
        {
            IPInterfaceProperties properties = item.GetIPProperties();
            if (HasDefaultGateway(properties))
            {
                score += 1000000000000L;
            }

            if (HasIpv4Address(properties))
            {
                score += 1000000000L;
            }
        }
        catch
        {
        }

        if (item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        {
            score += 10000000L;
        }
        else if (item.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                 item.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
        {
            score += 5000000L;
        }

        if (item.Speed > 0)
        {
            score += Math.Min(item.Speed / 1000000L, 1000000L);
        }

        return score;
    }

    private static bool HasDefaultGateway(IPInterfaceProperties properties)
    {
        if (properties == null || properties.GatewayAddresses == null)
        {
            return false;
        }

        foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
        {
            if (gateway != null &&
                gateway.Address != null &&
                !IPAddress.Any.Equals(gateway.Address) &&
                !IPAddress.IPv6Any.Equals(gateway.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasIpv4Address(IPInterfaceProperties properties)
    {
        if (properties == null || properties.UnicastAddresses == null)
        {
            return false;
        }

        foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
        {
            if (address != null &&
                address.Address != null &&
                address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.Any.Equals(address.Address) &&
                !IPAddress.Loopback.Equals(address.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVirtualNetworkInterface(NetworkInterface item)
    {
        if (item == null)
        {
            return true;
        }

        // Name and description are both required because Windows vendors label virtual adapters differently.
        string identity = ((item.Name ?? string.Empty) + " " + (item.Description ?? string.Empty)).ToLowerInvariant();
        return
            identity.IndexOf("vethernet", StringComparison.Ordinal) >= 0 ||
            identity.IndexOf("hyper-v", StringComparison.Ordinal) >= 0 ||
            identity.IndexOf("hyper v", StringComparison.Ordinal) >= 0 ||
            identity.IndexOf("vswitch", StringComparison.Ordinal) >= 0 ||
            identity.IndexOf("wsl", StringComparison.Ordinal) >= 0 ||
            identity.IndexOf("virtual ethernet", StringComparison.Ordinal) >= 0 ||
            identity.IndexOf("wan miniport", StringComparison.Ordinal) >= 0;
    }

    private static WifiConnectionDetails GetWifiDetails(NetworkInterface networkInterface)
    {
        if (networkInterface == null || networkInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
        {
            return null;
        }

        Guid interfaceGuid;
        try
        {
            interfaceGuid = new Guid(networkInterface.Id);
        }
        catch
        {
            return null;
        }

        WifiConnectionDetails details;
        return NativeMethods.TryGetConnectedWifiDetails(interfaceGuid, out details) ? details : null;
    }

    private static CpuInfo DetectCpuInfo()
    {
        CpuInfo info = new CpuInfo();
        info.Name = "CPU";
        info.CoreCount = Math.Max(1, Environment.ProcessorCount);

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(item["Name"]);
                    if (!string.IsNullOrEmpty(name))
                    {
                        info.Name = name.Trim();
                    }

                    object logical = item["NumberOfLogicalProcessors"];
                    object cores = item["NumberOfCores"];
                    if (logical != null && Convert.ToInt32(logical) > 0)
                    {
                        info.CoreCount = Convert.ToInt32(logical);
                    }
                    else if (cores != null && Convert.ToInt32(cores) > 0)
                    {
                        info.CoreCount = Convert.ToInt32(cores);
                    }

                    object currentClock = item["CurrentClockSpeed"];
                    if (currentClock != null && Convert.ToDouble(currentClock) > 0.0)
                    {
                        info.CurrentFrequencyGhz = Convert.ToDouble(currentClock) / 1000.0;
                    }

                    object maxClock = item["MaxClockSpeed"];
                    if (maxClock != null && Convert.ToDouble(maxClock) > 0.0)
                    {
                        info.BaseFrequencyGhz = Convert.ToDouble(maxClock) / 1000.0;
                    }

                    if (info.BaseFrequencyGhz <= 0.0)
                    {
                        info.BaseFrequencyGhz = info.CurrentFrequencyGhz;
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return info;
    }

    private static MemoryInfo DetectMemoryInfo()
    {
        MemoryInfo info = new MemoryInfo();
        info.Manufacturer = "Memory";
        info.SpeedMtps = 0;

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Manufacturer, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                List<string> manufacturers = new List<string>();
                foreach (ManagementObject item in collection)
                {
                    string manufacturer = NormalizeMemoryManufacturer(Convert.ToString(item["Manufacturer"]));
                    if (manufacturer.Length > 0 && !ContainsText(manufacturers, manufacturer))
                    {
                        manufacturers.Add(manufacturer);
                    }

                    int configuredSpeed = ToPositiveInt(item["ConfiguredClockSpeed"]);
                    int speed = configuredSpeed > 0 ? configuredSpeed : ToPositiveInt(item["Speed"]);
                    if (speed > info.SpeedMtps)
                    {
                        info.SpeedMtps = speed;
                    }
                }

                if (manufacturers.Count == 1)
                {
                    info.Manufacturer = manufacturers[0];
                }
                else if (manufacturers.Count > 1)
                {
                    info.Manufacturer = string.Join("/", manufacturers.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return info;
    }

    private static string NormalizeMemoryManufacturer(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = CollapseWhitespace(value.Trim());
        if (text.Length == 0 ||
            string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Undefined", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Not Specified", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return text;
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        bool previousWhitespace = false;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                    previousWhitespace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        return builder.ToString();
    }

    private static bool ContainsText(List<string> values, string text)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ToPositiveInt(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            int number = Convert.ToInt32(value);
            return number > 0 ? number : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static DiskInfo DetectDiskInfo()
    {
        DiskInfo info = new DiskInfo();
        info.Name = "Physical Disk";
        info.CounterPath = string.Empty;
        info.VolumeRoots = new List<string>();
        info.DisplayVolumes = string.Empty;

        string systemDrive = GetSystemDriveName();
        string[] physicalDiskPaths = ExpandWildcard(@"\PhysicalDisk(*)\% Disk Time");
        info.CounterPath = SelectPhysicalDiskCounterPath(physicalDiskPaths, systemDrive);
        info.VolumeRoots = ExtractVolumeRootsFromPhysicalDiskPath(info.CounterPath);
        int diskIndex = ExtractPhysicalDiskIndex(info.CounterPath);
        List<string> associatedRoots = DetectUsableVolumeRootsForPhysicalDisk(diskIndex);
        if (associatedRoots.Count > 0)
        {
            info.VolumeRoots = associatedRoots;
        }
        else
        {
            info.VolumeRoots = FilterUsableDriveRoots(info.VolumeRoots);
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Index, Model, Size FROM Win32_DiskDrive"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                ManagementObject fallback = null;
                foreach (ManagementObject item in collection)
                {
                    if (fallback == null)
                    {
                        fallback = item;
                    }

                    object indexValue = item["Index"];
                    int index = -1;
                    if (indexValue != null)
                    {
                        index = Convert.ToInt32(indexValue);
                    }

                    if (diskIndex >= 0 && index != diskIndex)
                    {
                        continue;
                    }

                    ApplyDiskDriveObject(info, item, diskIndex);
                    break;
                }

                if (info.TotalBytes <= 0.0 && fallback != null)
                {
                    ApplyDiskDriveObject(info, fallback, diskIndex);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (info.VolumeRoots.Count == 0)
        {
            info.VolumeRoots = DetectFixedDriveRoots();
        }

        info.DisplayVolumes = BuildDiskVolumeLabel(info.VolumeRoots);

        if (info.TotalBytes <= 0.0)
        {
            info.TotalBytes = SumDriveTotalBytes(info.VolumeRoots);
        }

        if (string.IsNullOrEmpty(info.Name))
        {
            info.Name = diskIndex >= 0 ? "Disk " + diskIndex : "Physical Disk";
        }

        return info;
    }

    private sealed class DiskVolumeDisplayInfo
    {
        public string Root { get; set; }
        public string Letter { get; set; }
        public long TotalSize { get; set; }
    }

    private static void ApplyDiskDriveObject(DiskInfo info, ManagementObject item, int diskIndex)
    {
        string model = Convert.ToString(item["Model"]);
        if (!string.IsNullOrEmpty(model))
        {
            info.Name = model.Trim();
        }
        else if (diskIndex >= 0)
        {
            info.Name = "Disk " + diskIndex;
        }

        object size = item["Size"];
        if (size != null)
        {
            double bytes = Convert.ToDouble(size);
            if (bytes > 0.0)
            {
                info.TotalBytes = bytes;
            }
        }
    }

    private static void ApplyDiskUsage(PerfSnapshot snapshot, DiskInfo info)
    {
        double totalBytes = info.TotalBytes;
        double freeBytes = 0.0;
        double logicalTotalBytes = 0.0;
        List<string> roots = info.VolumeRoots;
        if (roots == null || roots.Count == 0)
        {
            roots = DetectFixedDriveRoots();
        }

        for (int i = 0; i < roots.Count; i++)
        {
            try
            {
                DriveInfo drive = new DriveInfo(roots[i]);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                logicalTotalBytes += drive.TotalSize;
                freeBytes += Math.Max(0.0, drive.AvailableFreeSpace);
            }
            catch
            {
            }
        }

        if (totalBytes <= 0.0)
        {
            totalBytes = logicalTotalBytes;
        }

        if (totalBytes <= 0.0)
        {
            return;
        }

        double usedBytes = Math.Max(0.0, totalBytes - freeBytes);
        usedBytes = Math.Min(usedBytes, totalBytes);
        snapshot.DiskTotalGb = totalBytes / 1073741824.0;
        snapshot.DiskUsedGb = usedBytes / 1073741824.0;
        snapshot.DiskCapacityPercent = Clamp(usedBytes * 100.0 / totalBytes, 0.0, 100.0);
    }

    private static string SelectPhysicalDiskCounterPath(string[] paths, string systemDrive)
    {
        string firstPhysicalDisk = string.Empty;
        string totalPath = string.Empty;
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (path.IndexOf("(_Total)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                totalPath = path;
                continue;
            }

            if (firstPhysicalDisk.Length == 0)
            {
                firstPhysicalDisk = path;
            }

            if (systemDrive.Length > 0 && path.IndexOf(systemDrive, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return path;
            }
        }

        if (firstPhysicalDisk.Length > 0)
        {
            return firstPhysicalDisk;
        }

        return totalPath;
    }

    private static int ExtractPhysicalDiskIndex(string counterPath)
    {
        if (string.IsNullOrEmpty(counterPath))
        {
            return -1;
        }

        int start = counterPath.IndexOf(@"\PhysicalDisk(", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return -1;
        }

        start += @"\PhysicalDisk(".Length;
        int end = start;
        while (end < counterPath.Length && char.IsDigit(counterPath[end]))
        {
            end++;
        }

        if (end <= start)
        {
            return -1;
        }

        int index;
        if (int.TryParse(counterPath.Substring(start, end - start), out index))
        {
            return index;
        }

        return -1;
    }

    private static List<string> ExtractVolumeRootsFromPhysicalDiskPath(string counterPath)
    {
        List<string> roots = new List<string>();
        if (string.IsNullOrEmpty(counterPath))
        {
            return roots;
        }

        int open = counterPath.IndexOf('(');
        int close = counterPath.IndexOf(')', open + 1);
        if (open < 0 || close <= open)
        {
            return roots;
        }

        string instance = counterPath.Substring(open + 1, close - open - 1);
        string[] parts = instance.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 2 && part[1] == ':')
            {
                roots.Add(part.ToUpperInvariant() + "\\");
            }
        }

        return roots;
    }

    private static List<string> DetectFixedDriveRoots()
    {
        List<string> roots = new List<string>();
        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            for (int i = 0; i < drives.Length; i++)
            {
                if (IsUsableFixedDrive(drives[i]))
                {
                    roots.Add(drives[i].Name);
                }
            }
        }
        catch
        {
        }

        return roots;
    }

    private static List<string> DetectUsableVolumeRootsForPhysicalDisk(int diskIndex)
    {
        List<string> roots = new List<string>();
        if (diskIndex < 0)
        {
            return roots;
        }

        try
        {
            string deviceId = null;
            using (ManagementObjectSearcher diskSearcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_DiskDrive WHERE Index=" + diskIndex.ToString(CultureInfo.InvariantCulture)))
            using (ManagementObjectCollection disks = diskSearcher.Get())
            {
                foreach (ManagementObject disk in disks)
                {
                    deviceId = Convert.ToString(disk["DeviceID"]);
                    break;
                }
            }

            if (string.IsNullOrEmpty(deviceId))
            {
                return roots;
            }

            string partitionQuery =
                "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" +
                EscapeWmiObjectString(deviceId) +
                "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
            using (ManagementObjectSearcher partitionSearcher = new ManagementObjectSearcher(partitionQuery))
            using (ManagementObjectCollection partitions = partitionSearcher.Get())
            {
                foreach (ManagementObject partition in partitions)
                {
                    string logicalQuery =
                        "ASSOCIATORS OF {" +
                        partition.Path.RelativePath +
                        "} WHERE AssocClass=Win32_LogicalDiskToPartition";
                    using (ManagementObjectSearcher logicalSearcher = new ManagementObjectSearcher(logicalQuery))
                    using (ManagementObjectCollection logicalDisks = logicalSearcher.Get())
                    {
                        foreach (ManagementObject logicalDisk in logicalDisks)
                        {
                            if (!IsUsableLogicalDisk(logicalDisk))
                            {
                                continue;
                            }

                            AddUniqueDriveRoot(roots, Convert.ToString(logicalDisk["DeviceID"]));
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return roots;
    }

    private static bool IsUsableLogicalDisk(ManagementObject logicalDisk)
    {
        if (logicalDisk == null)
        {
            return false;
        }

        object driveType = logicalDisk["DriveType"];
        if (driveType == null || Convert.ToInt32(driveType) != 3)
        {
            return false;
        }

        string fileSystem = Convert.ToString(logicalDisk["FileSystem"]);
        string deviceId = Convert.ToString(logicalDisk["DeviceID"]);
        return !string.IsNullOrWhiteSpace(fileSystem) &&
            !string.IsNullOrWhiteSpace(deviceId) &&
            deviceId.Length >= 2 &&
            deviceId[1] == ':';
    }

    private static string EscapeWmiObjectString(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static List<string> FilterUsableDriveRoots(List<string> roots)
    {
        List<string> usable = new List<string>();
        if (roots == null)
        {
            return usable;
        }

        for (int i = 0; i < roots.Count; i++)
        {
            try
            {
                DriveInfo drive = new DriveInfo(roots[i]);
                if (IsUsableFixedDrive(drive))
                {
                    AddUniqueDriveRoot(usable, drive.Name);
                }
            }
            catch
            {
            }
        }

        return usable;
    }

    private static bool IsUsableFixedDrive(DriveInfo drive)
    {
        return drive != null &&
            drive.DriveType == DriveType.Fixed &&
            drive.IsReady &&
            drive.TotalSize > 0 &&
            !string.IsNullOrWhiteSpace(drive.Name) &&
            drive.Name.Length >= 2 &&
            drive.Name[1] == ':';
    }

    private static void AddUniqueDriveRoot(List<string> roots, string value)
    {
        string root = NormalizeDriveRoot(value);
        if (root.Length == 0)
        {
            return;
        }

        for (int i = 0; i < roots.Count; i++)
        {
            if (string.Equals(roots[i], root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        roots.Add(root);
    }

    private static string NormalizeDriveRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Trim();
        if (text.Length < 2 || text[1] != ':' || !char.IsLetter(text[0]))
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(text[0]).ToString() + @":\";
    }

    private static string BuildDiskVolumeLabel(List<string> roots)
    {
        List<DiskVolumeDisplayInfo> volumes = new List<DiskVolumeDisplayInfo>();
        if (roots == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < roots.Count; i++)
        {
            try
            {
                DriveInfo drive = new DriveInfo(roots[i]);
                if (!IsUsableFixedDrive(drive))
                {
                    continue;
                }

                volumes.Add(new DiskVolumeDisplayInfo
                {
                    Root = drive.Name,
                    Letter = char.ToUpperInvariant(drive.Name[0]).ToString(),
                    TotalSize = drive.TotalSize
                });
            }
            catch
            {
            }
        }

        if (volumes.Count == 0)
        {
            return string.Empty;
        }

        if (volumes.Count > 3)
        {
            volumes.Sort(CompareDiskVolumesBySizeDescending);
            while (volumes.Count > 3)
            {
                volumes.RemoveAt(volumes.Count - 1);
            }
        }

        volumes.Sort(CompareDiskVolumesByLetter);
        string[] letters = new string[volumes.Count];
        for (int i = 0; i < volumes.Count; i++)
        {
            letters[i] = volumes[i].Letter;
        }

        return string.Join("/", letters);
    }

    private static int CompareDiskVolumesBySizeDescending(DiskVolumeDisplayInfo left, DiskVolumeDisplayInfo right)
    {
        int result = right.TotalSize.CompareTo(left.TotalSize);
        return result != 0 ? result : CompareDiskVolumesByLetter(left, right);
    }

    private static int CompareDiskVolumesByLetter(DiskVolumeDisplayInfo left, DiskVolumeDisplayInfo right)
    {
        return string.Compare(left.Letter, right.Letter, StringComparison.OrdinalIgnoreCase);
    }

    private static double SumDriveTotalBytes(List<string> roots)
    {
        double total = 0.0;
        for (int i = 0; i < roots.Count; i++)
        {
            try
            {
                DriveInfo drive = new DriveInfo(roots[i]);
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    total += drive.TotalSize;
                }
            }
            catch
            {
            }
        }

        return total;
    }

    private static string GetSystemDriveName()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.IsNullOrEmpty(root))
            {
                return root.TrimEnd('\\');
            }
        }
        catch
        {
        }

        return "C:";
    }

    private static GpuInfo DetectGpuInfo()
    {
        GpuInfo info = new GpuInfo();
        info.Name = "GPU";
        info.MemoryTotalGb = 0.0;

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(item["Name"]);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (info.Name == "GPU" || name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        info.Name = name;
                        info.IsDetected = true;
                    }

                    object adapterRam = item["AdapterRAM"];
                    if (adapterRam != null)
                    {
                        double bytes = Convert.ToDouble(adapterRam);
                        if (bytes > 0.0)
                        {
                            info.MemoryTotalGb = Math.Max(info.MemoryTotalGb, bytes / 1073741824.0);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (info.MemoryTotalGb < 0.25)
        {
            info.MemoryTotalGb = GetPhysicalMemoryTotalGb();
        }

        return info;
    }

    private static GpuInfo DetectNpuInfo()
    {
        GpuInfo info = new GpuInfo();
        info.Name = "NPU";
        info.MemoryTotalGb = 0.0;

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPClass, Manufacturer FROM Win32_PnPEntity"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(item["Name"]);
                    string pnpClass = Convert.ToString(item["PNPClass"]);
                    string manufacturer = Convert.ToString(item["Manufacturer"]);
                    if (!LooksLikeNpuDevice(name, pnpClass, manufacturer))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        info.Name = name;
                        info.IsDetected = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (info.IsDetected)
        {
            info.MemoryTotalGb = GetPhysicalMemoryTotalGb();
        }

        return info;
    }

    private static bool LooksLikeNpuDevice(string name, string pnpClass, string manufacturer)
    {
        if (string.Equals(pnpClass, "ComputeAccelerator", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string combined = ((name ?? string.Empty) + " " + (manufacturer ?? string.Empty)).ToLowerInvariant();
        return combined.IndexOf(" npu", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("(npu", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("neural", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("hexagon", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("ai boost", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("xdna", StringComparison.Ordinal) >= 0;
    }

    private static HashSet<string> DetectNpuLuidTokens(string[] gpuEnginePaths, bool hasNpuDevice)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < gpuEnginePaths.Length; i++)
        {
            if (ContainsNpuKeyword(gpuEnginePaths[i]))
            {
                string luid = ExtractLuidToken(gpuEnginePaths[i]);
                if (luid.Length > 0)
                {
                    result.Add(luid);
                }
            }
        }

        if (result.Count > 0 || !hasNpuDevice)
        {
            return result;
        }

        Dictionary<string, HashSet<string>> engineTypesByLuid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < gpuEnginePaths.Length; i++)
        {
            string luid = ExtractLuidToken(gpuEnginePaths[i]);
            string engineType = ExtractEngineType(gpuEnginePaths[i]);
            if (luid.Length == 0 || engineType.Length == 0)
            {
                continue;
            }

            HashSet<string> engineTypes;
            if (!engineTypesByLuid.TryGetValue(luid, out engineTypes))
            {
                engineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                engineTypesByLuid.Add(luid, engineTypes);
            }

            engineTypes.Add(engineType);
        }

        foreach (KeyValuePair<string, HashSet<string>> item in engineTypesByLuid)
        {
            if (item.Value.Count == 1 && item.Value.Contains("Compute"))
            {
                result.Add(item.Key);
            }
        }

        return result;
    }

    private static bool IsNpuPath(string path, HashSet<string> npuLuidTokens)
    {
        if (ContainsNpuKeyword(path))
        {
            return true;
        }

        if (npuLuidTokens == null || npuLuidTokens.Count == 0)
        {
            return false;
        }

        string luid = ExtractLuidToken(path);
        return luid.Length > 0 && npuLuidTokens.Contains(luid);
    }

    private static bool ContainsNpuKeyword(string path)
    {
        string lower = (path ?? string.Empty).ToLowerInvariant();
        return lower.IndexOf("npu", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("neural", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("hexagon", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("ai boost", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("xdna", StringComparison.Ordinal) >= 0;
    }

    private static string ExtractLuidToken(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        int start = path.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        int end = path.IndexOf("_phys_", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = path.IndexOf(")", start, StringComparison.Ordinal);
        }

        if (end < 0 || end <= start)
        {
            return string.Empty;
        }

        return path.Substring(start, end - start).ToLowerInvariant();
    }

    private static string ExtractEngineType(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        int start = path.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += "engtype_".Length;
        int end = path.IndexOf(")", start, StringComparison.Ordinal);
        if (end < 0 || end <= start)
        {
            return string.Empty;
        }

        return path.Substring(start, end - start);
    }

    private static double GetPhysicalMemoryTotalGb()
    {
        NativeMethods.MEMORYSTATUSEX memory = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(memory))
        {
            return memory.ullTotalPhys / 1073741824.0;
        }

        return 0.0;
    }

    private static string JoinSet(HashSet<string> values)
    {
        if (values == null || values.Count == 0)
        {
            return "none";
        }

        StringBuilder builder = new StringBuilder();
        foreach (string value in values)
        {
            if (builder.Length > 0)
            {
                builder.Append(",");
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static double ReadCounter(PdhCounter counter)
    {
        if (counter == null || counter.Handle == IntPtr.Zero)
        {
            return 0.0;
        }

        uint counterType;
        PdhNative.PDH_FMT_COUNTERVALUE_DOUBLE value;
        uint status = PdhNative.PdhGetFormattedCounterValue(
            counter.Handle,
            PdhNative.PDH_FMT_DOUBLE,
            out counterType,
            out value);

        if (status == PdhNative.ERROR_SUCCESS &&
            (value.CStatus == PdhNative.PDH_CSTATUS_VALID_DATA ||
             value.CStatus == PdhNative.PDH_CSTATUS_NEW_DATA))
        {
            return value.DoubleValue;
        }

        return 0.0;
    }

    private void EnsureNotDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException("PdhSampler");
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
