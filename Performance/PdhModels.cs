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

internal sealed class PdhCounter
{
    public PdhCounter(IntPtr handle, string path)
    {
        this.Handle = handle;
        this.Path = path;
    }

    public IntPtr Handle { get; private set; }
    public string Path { get; private set; }
}

internal sealed class GpuInfo
{
    public string Name { get; set; }
    public double MemoryTotalGb { get; set; }
    public bool IsDetected { get; set; }
}

internal sealed class NetworkState
{
    public string Name { get; set; }
    public bool Connected { get; set; }
    public bool IsWifi { get; set; }
    public bool RssiKnown { get; set; }
    public int RssiDbm { get; set; }
}

internal sealed class WifiConnectionDetails
{
    public string Ssid { get; set; }
    public string Bssid { get; set; }
    public string PhyType { get; set; }
    public string AuthAlgorithm { get; set; }
    public string CipherAlgorithm { get; set; }
    public bool SecurityEnabled { get; set; }
    public bool OneXEnabled { get; set; }
    public int SignalQuality { get; set; }
    public bool RssiKnown { get; set; }
    public int RssiDbm { get; set; }
    public uint TxRateKbps { get; set; }
    public uint RxRateKbps { get; set; }

    public WifiConnectionDetails()
    {
        this.Ssid = string.Empty;
        this.Bssid = string.Empty;
        this.PhyType = string.Empty;
        this.AuthAlgorithm = string.Empty;
        this.CipherAlgorithm = string.Empty;
    }
}

internal enum NetworkAccessState
{
    // Adapter exists but no connectivity result is currently authoritative.
    Unknown,
    // Internet reachability was proven by NCSI HTTP or at least one Ping.
    Online,
    // Adapter exists, but neither HTTP nor Ping proved Internet reachability.
    Offline,
    // No usable active adapter was selected.
    AdapterMissing,
    // A captive portal or HTTP authentication response requires user action.
    NeedsValidation
}

internal enum GfwProbeStatus
{
    Disabled,
    Unknown,
    Checking,
    Normal,
    SuspectedDns,
    SuspectedTcp,
    SuspectedTlsSni,
    SuspectedHttp,
    Inconclusive
}

internal sealed class GfwProbeSnapshot
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public GfwProbeStatus Status { get; set; }
    public string Detail { get; set; }
    public string Reason { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public bool CheckedAtKnown { get; set; }
    public int DomainsTested { get; set; }
    public int AnomalyCount { get; set; }

    public GfwProbeSnapshot()
    {
        this.Status = GfwProbeStatus.Disabled;
        this.Detail = "关闭";
        this.Reason = string.Empty;
        this.CheckedAtLocal = DateTime.MinValue;
    }

    public GfwProbeSnapshot Clone()
    {
        return new GfwProbeSnapshot
        {
            Enabled = this.Enabled,
            Running = this.Running,
            Status = this.Status,
            Detail = this.Detail,
            Reason = this.Reason,
            CheckedAtLocal = this.CheckedAtLocal,
            CheckedAtKnown = this.CheckedAtKnown,
            DomainsTested = this.DomainsTested,
            AnomalyCount = this.AnomalyCount
        };
    }
}

// Immutable-by-convention transfer object. NetworkMonitorReader returns a deep clone
// and NetworkMonitorForm treats the instance as read-only until the next timer tick.
internal sealed class NetworkMonitorSnapshot
{
    public DateTime UpdatedLocal { get; set; }
    public bool Connected { get; set; }
    public bool InterfaceKnown { get; set; }
    public string InterfaceId { get; set; }
    public string InterfaceName { get; set; }
    public string InterfaceDescription { get; set; }
    public string InterfaceType { get; set; }
    public string MacAddress { get; set; }
    public long LinkSpeedBps { get; set; }
    public bool IsWifi { get; set; }
    public string IPv4 { get; set; }
    public string IPv6 { get; set; }
    public string DnsServers { get; set; }
    public WifiConnectionDetails WifiDetails { get; set; }
    public string PublicIp { get; set; }
    public bool PublicIpKnown { get; set; }
    public bool PublicIpRefreshing { get; set; }
    public bool ConnectivityKnown { get; set; }
    public bool ConnectivityOnline { get; set; }
    public NetworkAccessState AccessState { get; set; }
    public string AccessReason { get; set; }
    public string ConnectivityTarget { get; set; }
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public int PacketLossPercent { get; set; }
    public GfwProbeSnapshot GfwProbe { get; set; }
    public string LastError { get; set; }

    public NetworkMonitorSnapshot()
    {
        this.UpdatedLocal = DateTime.MinValue;
        this.InterfaceId = string.Empty;
        this.InterfaceName = "Network";
        this.InterfaceDescription = string.Empty;
        this.InterfaceType = "--";
        this.MacAddress = "--";
        this.IPv4 = "--";
        this.IPv6 = "--";
        this.DnsServers = "--";
        this.WifiDetails = new WifiConnectionDetails();
        this.PublicIp = "--";
        this.AccessState = NetworkAccessState.Unknown;
        this.AccessReason = string.Empty;
        this.ConnectivityTarget = "1.1.1.1";
        this.GfwProbe = new GfwProbeSnapshot();
        this.LastError = string.Empty;
    }

    public NetworkMonitorSnapshot Clone()
    {
        return new NetworkMonitorSnapshot
        {
            UpdatedLocal = this.UpdatedLocal,
            Connected = this.Connected,
            InterfaceKnown = this.InterfaceKnown,
            InterfaceId = this.InterfaceId,
            InterfaceName = this.InterfaceName,
            InterfaceDescription = this.InterfaceDescription,
            InterfaceType = this.InterfaceType,
            MacAddress = this.MacAddress,
            LinkSpeedBps = this.LinkSpeedBps,
            IsWifi = this.IsWifi,
            IPv4 = this.IPv4,
            IPv6 = this.IPv6,
            DnsServers = this.DnsServers,
            WifiDetails = CloneWifiDetails(this.WifiDetails),
            PublicIp = this.PublicIp,
            PublicIpKnown = this.PublicIpKnown,
            PublicIpRefreshing = this.PublicIpRefreshing,
            ConnectivityKnown = this.ConnectivityKnown,
            ConnectivityOnline = this.ConnectivityOnline,
            AccessState = this.AccessState,
            AccessReason = this.AccessReason,
            ConnectivityTarget = this.ConnectivityTarget,
            LatencyMs = this.LatencyMs,
            JitterMs = this.JitterMs,
            PacketLossPercent = this.PacketLossPercent,
            GfwProbe = this.GfwProbe == null ? new GfwProbeSnapshot() : this.GfwProbe.Clone(),
            LastError = this.LastError
        };
    }

    private static WifiConnectionDetails CloneWifiDetails(WifiConnectionDetails details)
    {
        if (details == null)
        {
            return new WifiConnectionDetails();
        }

        return new WifiConnectionDetails
        {
            Ssid = details.Ssid,
            Bssid = details.Bssid,
            PhyType = details.PhyType,
            AuthAlgorithm = details.AuthAlgorithm,
            CipherAlgorithm = details.CipherAlgorithm,
            SecurityEnabled = details.SecurityEnabled,
            OneXEnabled = details.OneXEnabled,
            SignalQuality = details.SignalQuality,
            RssiKnown = details.RssiKnown,
            RssiDbm = details.RssiDbm,
            TxRateKbps = details.TxRateKbps,
            RxRateKbps = details.RxRateKbps
        };
    }
}

internal sealed class CleanIpConnectionSnapshot
{
    public DateTime CheckedAtLocal { get; set; }
    public bool CheckedAtKnown { get; set; }
    public bool Success { get; set; }
    public bool Running { get; set; }
    public bool TestMode { get; set; }
    public string Ip { get; set; }
    public string Location { get; set; }
    public string Asn { get; set; }
    public string Organization { get; set; }
    public bool ScoreKnown { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; }
    public string NativeKey { get; set; }
    public string NativeLabel { get; set; }
    public string NativeIconClass { get; set; }
    public string IpTypeKey { get; set; }
    public string IpTypeLabel { get; set; }
    public string IpTypeIconClass { get; set; }
    public string IpTypeReason { get; set; }
    public string Error { get; set; }
    public string RefreshTrigger { get; set; }
    public int LatencyMs { get; set; }

    public CleanIpConnectionSnapshot()
    {
        this.CheckedAtLocal = DateTime.MinValue;
        this.Ip = "--";
        this.Location = "--";
        this.Asn = "--";
        this.Organization = "--";
        this.Grade = "--";
        this.NativeKey = string.Empty;
        this.NativeLabel = "--";
        this.NativeIconClass = "fa-solid fa-circle-question";
        this.IpTypeKey = string.Empty;
        this.IpTypeLabel = "--";
        this.IpTypeIconClass = "fa-solid fa-circle-question";
        this.IpTypeReason = string.Empty;
        this.Error = string.Empty;
        this.RefreshTrigger = string.Empty;
    }

    public string ScoreLabel
    {
        get
        {
            if (!this.ScoreKnown)
            {
                return "--";
            }

            return string.IsNullOrWhiteSpace(this.Grade)
                ? this.Score.ToString(CultureInfo.InvariantCulture)
                : this.Score.ToString(CultureInfo.InvariantCulture) + this.Grade.Trim();
        }
    }

    public CleanIpConnectionSnapshot Clone()
    {
        return new CleanIpConnectionSnapshot
        {
            CheckedAtLocal = this.CheckedAtLocal,
            CheckedAtKnown = this.CheckedAtKnown,
            Success = this.Success,
            Running = this.Running,
            TestMode = this.TestMode,
            Ip = this.Ip,
            Location = this.Location,
            Asn = this.Asn,
            Organization = this.Organization,
            ScoreKnown = this.ScoreKnown,
            Score = this.Score,
            Grade = this.Grade,
            NativeKey = this.NativeKey,
            NativeLabel = this.NativeLabel,
            NativeIconClass = this.NativeIconClass,
            IpTypeKey = this.IpTypeKey,
            IpTypeLabel = this.IpTypeLabel,
            IpTypeIconClass = this.IpTypeIconClass,
            IpTypeReason = this.IpTypeReason,
            Error = this.Error,
            RefreshTrigger = this.RefreshTrigger,
            LatencyMs = this.LatencyMs
        };
    }
}

internal sealed class CpuInfo
{
    public string Name { get; set; }
    public int CoreCount { get; set; }
    public double CurrentFrequencyGhz { get; set; }
    public double BaseFrequencyGhz { get; set; }
}

internal sealed class MemoryInfo
{
    public string Manufacturer { get; set; }
    public int SpeedMtps { get; set; }
}

internal sealed class DiskInfo
{
    public string Name { get; set; }
    public string CounterPath { get; set; }
    public List<string> VolumeRoots { get; set; }
    public double TotalBytes { get; set; }
}

internal sealed class PerfSnapshot
{
    public string CpuName { get; set; }
    public double CpuPercent { get; set; }
    public int CpuCoreCount { get; set; }
    public double[] CpuCorePercents { get; set; }
    public double CpuFrequencyGhz { get; set; }
    public double CpuBaseFrequencyGhz { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }
    public double MemoryPercent { get; set; }
    public string MemoryManufacturer { get; set; }
    public int MemorySpeedMtps { get; set; }
    public string DiskName { get; set; }
    public double DiskPercent { get; set; }
    public double DiskWriteBytesPerSecond { get; set; }
    public double DiskReadBytesPerSecond { get; set; }
    public double DiskWritePercent { get; set; }
    public double DiskReadPercent { get; set; }
    public double DiskCapacityPercent { get; set; }
    public double DiskUsedGb { get; set; }
    public double DiskTotalGb { get; set; }
    public string NetworkName { get; set; }
    public bool NetworkConnected { get; set; }
    public bool NetworkIsWifi { get; set; }
    public bool NetworkRssiKnown { get; set; }
    public int NetworkRssiDbm { get; set; }
    public double NetworkSentBytesPerSecond { get; set; }
    public double NetworkReceivedBytesPerSecond { get; set; }
    public string GpuName { get; set; }
    public double GpuPercent { get; set; }
    public double GpuMemoryUsedGb { get; set; }
    public double GpuMemoryTotalGb { get; set; }
    public double GpuMemoryPercent { get; set; }
    public string NpuName { get; set; }
    public double NpuPercent { get; set; }
    public double NpuMemoryUsedGb { get; set; }
    public double NpuMemoryTotalGb { get; set; }
    public double NpuMemoryPercent { get; set; }

    public PerfSnapshot()
    {
        this.CpuName = "CPU";
        this.CpuCorePercents = new double[0];
        this.MemoryManufacturer = "Memory";
        this.DiskName = "Physical Disk";
        this.NetworkName = "Network";
        this.NetworkConnected = true;
        this.GpuName = "GPU";
        this.NpuName = "NPU";
    }
}
