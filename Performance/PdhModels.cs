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

internal enum CloudEndpointStatus
{
    Unknown,
    Checking,
    Normal,
    Slow,
    Down,
    Abnormal
}

internal enum DnsServerStatus
{
    Unknown,
    Normal,
    Problem,
    Hijacked,
    Unavailable
}

internal enum PingPathDiagnosis
{
    None,
    AdapterMissing,
    CaptivePortal,
    Offline,
    LocalLoss,
    LocalLatency,
    GlobalBlock,
    WanLoss,
    WanLatency,
    BaiduLoss,
    BaiduLatency,
    IcmpBlocked
}

internal enum PingDiagnosisSeverity
{
    None,
    Info,
    Warning,
    Error
}

internal sealed class DnsServerSnapshot
{
    public string Address { get; set; }
    public DnsServerStatus Status { get; set; }
    public int FailureCount { get; set; }
    public int LatencyMs { get; set; }
    public string Reason { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public bool CheckedAtKnown { get; set; }

    public DnsServerSnapshot()
    {
        this.Address = string.Empty;
        this.Status = DnsServerStatus.Unknown;
        this.Reason = string.Empty;
        this.CheckedAtLocal = DateTime.MinValue;
    }

    public DnsServerSnapshot Clone()
    {
        return new DnsServerSnapshot
        {
            Address = this.Address,
            Status = this.Status,
            FailureCount = this.FailureCount,
            LatencyMs = this.LatencyMs,
            Reason = this.Reason,
            CheckedAtLocal = this.CheckedAtLocal,
            CheckedAtKnown = this.CheckedAtKnown
        };
    }

    public static DnsServerSnapshot[] CreateUnknown(IEnumerable<string> addresses)
    {
        if (addresses == null)
        {
            return new DnsServerSnapshot[0];
        }

        List<DnsServerSnapshot> snapshots = new List<DnsServerSnapshot>();
        foreach (string address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            snapshots.Add(new DnsServerSnapshot
            {
                Address = address.Trim(),
                Status = DnsServerStatus.Unknown,
                Reason = string.Empty
            });
        }

        return snapshots.ToArray();
    }
}

internal sealed class CloudEndpointSnapshot
{
    public string Key { get; set; }
    public string ShortLabel { get; set; }
    public string DisplayName { get; set; }
    public bool Domestic { get; set; }
    public CloudEndpointStatus Status { get; set; }
    public int LatencyMs { get; set; }
    public string Reason { get; set; }
    public string AlertReason { get; set; }
    public string AlertName { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public bool CheckedAtKnown { get; set; }

    public CloudEndpointSnapshot()
    {
        this.Key = string.Empty;
        this.ShortLabel = string.Empty;
        this.DisplayName = string.Empty;
        this.Reason = string.Empty;
        this.AlertReason = string.Empty;
        this.AlertName = string.Empty;
        this.CheckedAtLocal = DateTime.MinValue;
    }

    public CloudEndpointSnapshot Clone()
    {
        return new CloudEndpointSnapshot
        {
            Key = this.Key,
            ShortLabel = this.ShortLabel,
            DisplayName = this.DisplayName,
            Domestic = this.Domestic,
            Status = this.Status,
            LatencyMs = this.LatencyMs,
            Reason = this.Reason,
            AlertReason = this.AlertReason,
            AlertName = this.AlertName,
            CheckedAtLocal = this.CheckedAtLocal,
            CheckedAtKnown = this.CheckedAtKnown
        };
    }

    public static CloudEndpointSnapshot[] CreateDefaults(CloudEndpointStatus status)
    {
        return new CloudEndpointSnapshot[]
        {
            Create("cloudflare", "Cf", "Cloudflare", false, status),
            Create("akamai", "Ak", "Akamai", false, status),
            Create("github", "Gi", "GitHub", false, status),
            Create("aws", "Aw", "AWS", false, status),
            Create("azure", "Az", "Azure", false, status),
            Create("google", "Go", "Google", false, status)
        };
    }

    private static CloudEndpointSnapshot Create(
        string key,
        string shortLabel,
        string displayName,
        bool domestic,
        CloudEndpointStatus status)
    {
        return new CloudEndpointSnapshot
        {
            Key = key,
            ShortLabel = shortLabel,
            DisplayName = displayName,
            Domestic = domestic,
            Status = status,
            Reason = string.Empty,
            AlertReason = string.Empty,
            AlertName = string.Empty
        };
    }
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
    public CloudEndpointSnapshot[] CloudEndpoints { get; set; }

    public GfwProbeSnapshot()
    {
        this.Status = GfwProbeStatus.Disabled;
        this.Detail = "关闭";
        this.Reason = string.Empty;
        this.CheckedAtLocal = DateTime.MinValue;
        this.CloudEndpoints = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
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
            AnomalyCount = this.AnomalyCount,
            CloudEndpoints = CloneCloudEndpoints(this.CloudEndpoints)
        };
    }

    private static CloudEndpointSnapshot[] CloneCloudEndpoints(CloudEndpointSnapshot[] endpoints)
    {
        if (endpoints == null)
        {
            return CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
        }

        if (endpoints.Length == 0)
        {
            return new CloudEndpointSnapshot[0];
        }

        CloudEndpointSnapshot[] clone = new CloudEndpointSnapshot[endpoints.Length];
        for (int i = 0; i < endpoints.Length; i++)
        {
            clone[i] = endpoints[i] == null ? new CloudEndpointSnapshot() : endpoints[i].Clone();
        }

        return clone;
    }
}

internal sealed class PingRollingSnapshot
{
    public string ActiveProfile { get; set; }
    public string ActiveTargetLabel { get; set; }
    public string Group { get; set; }
    public int SampleCount { get; set; }
    public int LostCount { get; set; }
    public double LossPercent { get; set; }
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public bool JitterKnown { get; set; }
    public bool StatsReady { get; set; }
    public bool IcmpBlocked { get; set; }
    public string DiagnosisText { get; set; }
    public PingPathDiagnosis Diagnosis { get; set; }
    public PingDiagnosisSeverity Severity { get; set; }

    public PingRollingSnapshot()
    {
        this.ActiveProfile = "PUB";
        this.ActiveTargetLabel = "PUB";
        this.Group = "public";
        this.DiagnosisText = string.Empty;
    }

    public PingRollingSnapshot Clone()
    {
        return new PingRollingSnapshot
        {
            ActiveProfile = this.ActiveProfile,
            ActiveTargetLabel = this.ActiveTargetLabel,
            Group = this.Group,
            SampleCount = this.SampleCount,
            LostCount = this.LostCount,
            LossPercent = this.LossPercent,
            LatencyMs = this.LatencyMs,
            JitterMs = this.JitterMs,
            JitterKnown = this.JitterKnown,
            StatsReady = this.StatsReady,
            IcmpBlocked = this.IcmpBlocked,
            DiagnosisText = this.DiagnosisText,
            Diagnosis = this.Diagnosis,
            Severity = this.Severity
        };
    }
}

internal enum PathPingHopSeverity
{
    None,
    Normal,
    RateLimited,
    Loss,
    Unresponsive
}

// Why the path is unhealthy, not merely which hop reported loss. A router that rate-limits
// direct ICMP to itself is the single most common false positive in traceroute-style tools,
// so it gets its own verdict distinct from real forwarding loss.
internal enum PathPingBlame
{
    None,
    NodeRateLimit,
    LinkLoss,
    Unreachable,
    IcmpUnavailable
}

internal sealed class PathPingHopSnapshot
{
    public int HopNumber { get; set; }
    public string Address { get; set; }
    public bool Responding { get; set; }
    public bool IsGateway { get; set; }
    public bool IsTarget { get; set; }
    public double AvgLatencyMs { get; set; }
    public double LossPercent { get; set; }
    public int SampleCount { get; set; }
    // Consecutive silent hops collapse into one row; 1 means the row stands for a single hop.
    public int MergedHopCount { get; set; }
    public PathPingHopSeverity Severity { get; set; }

    public PathPingHopSnapshot()
    {
        this.Address = string.Empty;
        this.MergedHopCount = 1;
        this.Severity = PathPingHopSeverity.None;
    }

    public PathPingHopSnapshot Clone()
    {
        return new PathPingHopSnapshot
        {
            HopNumber = this.HopNumber,
            Address = this.Address,
            Responding = this.Responding,
            IsGateway = this.IsGateway,
            IsTarget = this.IsTarget,
            AvgLatencyMs = this.AvgLatencyMs,
            LossPercent = this.LossPercent,
            SampleCount = this.SampleCount,
            MergedHopCount = this.MergedHopCount,
            Severity = this.Severity
        };
    }
}

internal sealed class PathPingSnapshot
{
    public string TargetLabel { get; set; }
    public bool PathKnown { get; set; }
    public bool DiscoveryInProgress { get; set; }
    public int DiscoveryCurrentHop { get; set; }
    public int DiscoveryMaxHops { get; set; }
    // True while a rediscovery is in flight: hops still hold the previous path so the UI
    // keeps showing data instead of blanking out for the duration of the trace.
    public bool Stale { get; set; }
    public DateTime LastTraceLocal { get; set; }
    public bool LastTraceKnown { get; set; }
    public int RoundCount { get; set; }
    public PathPingHopSnapshot[] Hops { get; set; }
    public double EndToEndLatencyMs { get; set; }
    public double EndToEndLossPercent { get; set; }
    public bool EndToEndKnown { get; set; }
    public PathPingBlame Blame { get; set; }
    public int BlameHopNumber { get; set; }
    public string BlameText { get; set; }
    public bool IcmpUnavailable { get; set; }

    public PathPingSnapshot()
    {
        this.TargetLabel = string.Empty;
        this.LastTraceLocal = DateTime.MinValue;
        this.Hops = new PathPingHopSnapshot[0];
        this.Blame = PathPingBlame.None;
        this.BlameText = string.Empty;
    }

    public PathPingSnapshot Clone()
    {
        return new PathPingSnapshot
        {
            TargetLabel = this.TargetLabel,
            PathKnown = this.PathKnown,
            DiscoveryInProgress = this.DiscoveryInProgress,
            DiscoveryCurrentHop = this.DiscoveryCurrentHop,
            DiscoveryMaxHops = this.DiscoveryMaxHops,
            Stale = this.Stale,
            LastTraceLocal = this.LastTraceLocal,
            LastTraceKnown = this.LastTraceKnown,
            RoundCount = this.RoundCount,
            Hops = CloneHops(this.Hops),
            EndToEndLatencyMs = this.EndToEndLatencyMs,
            EndToEndLossPercent = this.EndToEndLossPercent,
            EndToEndKnown = this.EndToEndKnown,
            Blame = this.Blame,
            BlameHopNumber = this.BlameHopNumber,
            BlameText = this.BlameText,
            IcmpUnavailable = this.IcmpUnavailable
        };
    }

    private static PathPingHopSnapshot[] CloneHops(PathPingHopSnapshot[] hops)
    {
        if (hops == null || hops.Length == 0)
        {
            return new PathPingHopSnapshot[0];
        }

        PathPingHopSnapshot[] clone = new PathPingHopSnapshot[hops.Length];
        for (int i = 0; i < hops.Length; i++)
        {
            clone[i] = hops[i] == null ? new PathPingHopSnapshot() : hops[i].Clone();
        }

        return clone;
    }
}

internal enum FixedPingStatus
{
    Unknown,
    Checking,
    Normal,
    Slow,
    Down
}

internal sealed class FixedPingTargetSnapshot
{
    public string Key { get; set; }
    public string DisplayName { get; set; }
    public string Target { get; set; }
    public FixedPingStatus Status { get; set; }
    public int LatencyMs { get; set; }
    public string Reason { get; set; }

    public FixedPingTargetSnapshot()
    {
        this.Key = string.Empty;
        this.DisplayName = string.Empty;
        this.Target = string.Empty;
        this.Reason = string.Empty;
    }

    public FixedPingTargetSnapshot Clone()
    {
        return new FixedPingTargetSnapshot
        {
            Key = this.Key,
            DisplayName = this.DisplayName,
            Target = this.Target,
            Status = this.Status,
            LatencyMs = this.LatencyMs,
            Reason = this.Reason
        };
    }
}

internal sealed class FixedPingSnapshot
{
    public bool Running { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public bool CheckedAtKnown { get; set; }
    public long NetworkGeneration { get; set; }
    public string InterfaceId { get; set; }
    public string TargetSignature { get; set; }
    public FixedPingTargetSnapshot[] Targets { get; set; }

    public FixedPingSnapshot()
    {
        this.CheckedAtLocal = DateTime.MinValue;
        this.InterfaceId = string.Empty;
        this.TargetSignature = string.Empty;
        this.Targets = new FixedPingTargetSnapshot[0];
    }

    public FixedPingSnapshot Clone()
    {
        FixedPingTargetSnapshot[] source = this.Targets ?? new FixedPingTargetSnapshot[0];
        FixedPingTargetSnapshot[] targets = new FixedPingTargetSnapshot[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            targets[i] = source[i] == null ? new FixedPingTargetSnapshot() : source[i].Clone();
        }

        return new FixedPingSnapshot
        {
            Running = this.Running,
            CheckedAtLocal = this.CheckedAtLocal,
            CheckedAtKnown = this.CheckedAtKnown,
            NetworkGeneration = this.NetworkGeneration,
            InterfaceId = this.InterfaceId,
            TargetSignature = this.TargetSignature,
            Targets = targets
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
    public string DefaultGatewayAddress { get; set; }
    public string DnsServers { get; set; }
    public DnsServerSnapshot[] DnsServerDetails { get; set; }
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
    public bool LocalNetworkDegraded { get; set; }
    public string LocalNetworkDegradedReason { get; set; }
    public GfwProbeSnapshot GfwProbe { get; set; }
    public PingRollingSnapshot PingRolling { get; set; }
    public PathPingSnapshot PathPing { get; set; }
    public FixedPingSnapshot FixedPing { get; set; }
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
        this.DefaultGatewayAddress = string.Empty;
        this.DnsServers = "--";
        this.DnsServerDetails = new DnsServerSnapshot[0];
        this.WifiDetails = new WifiConnectionDetails();
        this.PublicIp = "--";
        this.AccessState = NetworkAccessState.Unknown;
        this.AccessReason = string.Empty;
        this.ConnectivityTarget = "1.1.1.1";
        this.LocalNetworkDegradedReason = string.Empty;
        this.GfwProbe = new GfwProbeSnapshot();
        this.PingRolling = new PingRollingSnapshot();
        this.PathPing = new PathPingSnapshot();
        this.FixedPing = new FixedPingSnapshot();
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
            DefaultGatewayAddress = this.DefaultGatewayAddress,
            DnsServers = this.DnsServers,
            DnsServerDetails = CloneDnsServerDetails(this.DnsServerDetails),
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
            LocalNetworkDegraded = this.LocalNetworkDegraded,
            LocalNetworkDegradedReason = this.LocalNetworkDegradedReason,
            GfwProbe = this.GfwProbe == null ? new GfwProbeSnapshot() : this.GfwProbe.Clone(),
            PingRolling = this.PingRolling == null ? new PingRollingSnapshot() : this.PingRolling.Clone(),
            PathPing = this.PathPing == null ? new PathPingSnapshot() : this.PathPing.Clone(),
            FixedPing = this.FixedPing == null ? new FixedPingSnapshot() : this.FixedPing.Clone(),
            LastError = this.LastError
        };
    }

    private static DnsServerSnapshot[] CloneDnsServerDetails(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return new DnsServerSnapshot[0];
        }

        DnsServerSnapshot[] clone = new DnsServerSnapshot[details.Length];
        for (int i = 0; i < details.Length; i++)
        {
            clone[i] = details[i] == null ? new DnsServerSnapshot() : details[i].Clone();
        }

        return clone;
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
    // Raw egress country as reported by the geo lookup (before it is joined into Location).
    // The China-egress AI guard reads this to decide whether the outbound IP is mainland China.
    public string CountryRaw { get; set; }
    // False after a Windows network-identity event until a lookup on the replacement network
    // succeeds. This prevents an otherwise-fresh country result from authorizing the new route.
    public bool EgressIdentityCurrent { get; set; }
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
        this.CountryRaw = string.Empty;
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
            CountryRaw = this.CountryRaw,
            EgressIdentityCurrent = this.EgressIdentityCurrent,
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

internal enum MemoryPressureLevel
{
    Normal,
    Warning,
    Critical
}

internal struct MemoryPressureHistoryPoint
{
    public DateTime TimestampUtc;
    public double Percent;
    public MemoryPressureLevel Level;
}

internal sealed class DiskInfo
{
    public string Name { get; set; }
    public string CounterPath { get; set; }
    public List<string> VolumeRoots { get; set; }
    public string DisplayVolumes { get; set; }
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
    public double MemoryAvailableGb { get; set; }
    public double MemoryCommittedGb { get; set; }
    public double MemoryCommitLimitGb { get; set; }
    public double MemoryCommitPercent { get; set; }
    public double MemoryPagesOutputPerSecond { get; set; }
    public double MemoryPageOutMegabytesPerSecond { get; set; }
    public double MemoryPressurePercent { get; set; }
    public MemoryPressureLevel MemoryPressureLevel { get; set; }
    public double MemoryHardwareReservedGb { get; set; }
    public double MemoryHardwareReservedPercent { get; set; }
    // Bytes actually written to pagefile.sys, not commit charge. Commit charge counts memory the
    // system has promised (including pages never touched), so it reads far above physical usage and
    // was routinely misread as "virtual memory is eating 35 GB". This is the figure that says how
    // much really spilled to disk.
    public double PageFileUsedGb { get; set; }
    public double PageFileTotalGb { get; set; }
    public string MemoryManufacturer { get; set; }
    public int MemorySpeedMtps { get; set; }
    public string DiskName { get; set; }
    public string DiskVolumeLabel { get; set; }
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
        this.DiskVolumeLabel = string.Empty;
        this.NetworkName = "Network";
        this.NetworkConnected = true;
        this.GpuName = "GPU";
        this.NpuName = "NPU";
    }
}

// Apple describes memory pressure as how efficiently memory is serving current processing needs,
// so this Windows projection is deliberately driven by reclaimable physical headroom and sustained
// page-outs. Commit is only a late safety floor: promised-but-untouched private allocations must not
// turn a machine with abundant available RAM and no swapping yellow or red.
internal sealed class MemoryPressureTracker
{
    internal const double WarningPromotionDelaySeconds = 10.0;
    internal const double CriticalPromotionDelaySeconds = 5.0;
    internal const double CriticalRecoveryDelaySeconds = 30.0;
    internal const double NormalRecoveryDelaySeconds = 60.0;
    internal const double PageOutSmoothingSeconds = 10.0;

    private MemoryPressureLevel currentLevel = MemoryPressureLevel.Normal;
    private MemoryPressureLevel pendingLevel = MemoryPressureLevel.Normal;
    private DateTime pendingSinceUtc = DateTime.MinValue;
    private DateTime lastSampleUtc = DateTime.MinValue;
    private double smoothedPageOutMegabytesPerSecond;

    internal void Update(PerfSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot == null)
        {
            return;
        }

        double rawPageOutMegabytesPerSecond = Math.Max(0.0, snapshot.MemoryPagesOutputPerSecond) *
            Math.Max(1, Environment.SystemPageSize) /
            1048576.0;

        if (this.lastSampleUtc == DateTime.MinValue || nowUtc <= this.lastSampleUtc)
        {
            this.smoothedPageOutMegabytesPerSecond = rawPageOutMegabytesPerSecond;
        }
        else
        {
            // Time-based smoothing preserves a ten-second page-out meaning across performance
            // modes even though their main sampling intervals differ.
            double elapsedSeconds = Math.Min(30.0, (nowUtc - this.lastSampleUtc).TotalSeconds);
            double alpha = 1.0 - Math.Exp(-elapsedSeconds / PageOutSmoothingSeconds);
            this.smoothedPageOutMegabytesPerSecond +=
                (rawPageOutMegabytesPerSecond - this.smoothedPageOutMegabytesPerSecond) * alpha;
        }

        this.lastSampleUtc = nowUtc;
        double rawScore = CalculateScore(
            snapshot.MemoryTotalGb,
            snapshot.MemoryAvailableGb,
            snapshot.MemoryCommitPercent,
            this.smoothedPageOutMegabytesPerSecond);
        MemoryPressureLevel targetLevel = ResolveTargetLevel(rawScore, this.currentLevel);
        bool immediateCritical = targetLevel == MemoryPressureLevel.Critical &&
            (snapshot.MemoryCommitPercent >= 98.0 ||
             IsCriticallyLowAvailable(snapshot.MemoryTotalGb, snapshot.MemoryAvailableGb));

        UpdateStableLevel(targetLevel, immediateCritical, nowUtc);
        snapshot.MemoryPageOutMegabytesPerSecond = this.smoothedPageOutMegabytesPerSecond;
        snapshot.MemoryPressurePercent = ApplyStableLevelFloor(rawScore, this.currentLevel);
        snapshot.MemoryPressureLevel = this.currentLevel;
    }

    private void UpdateStableLevel(MemoryPressureLevel targetLevel, bool immediateCritical, DateTime nowUtc)
    {
        if (targetLevel == this.currentLevel)
        {
            this.pendingLevel = this.currentLevel;
            this.pendingSinceUtc = DateTime.MinValue;
            return;
        }

        if (immediateCritical)
        {
            this.currentLevel = MemoryPressureLevel.Critical;
            this.pendingLevel = this.currentLevel;
            this.pendingSinceUtc = DateTime.MinValue;
            return;
        }

        if (this.pendingSinceUtc == DateTime.MinValue || this.pendingLevel != targetLevel)
        {
            this.pendingLevel = targetLevel;
            this.pendingSinceUtc = nowUtc;
            return;
        }

        double requiredSeconds;
        if (targetLevel > this.currentLevel)
        {
            requiredSeconds = targetLevel == MemoryPressureLevel.Critical
                ? CriticalPromotionDelaySeconds
                : WarningPromotionDelaySeconds;
        }
        else
        {
            requiredSeconds = this.currentLevel == MemoryPressureLevel.Critical
                ? CriticalRecoveryDelaySeconds
                : NormalRecoveryDelaySeconds;
        }

        if ((nowUtc - this.pendingSinceUtc).TotalSeconds >= requiredSeconds)
        {
            this.currentLevel = targetLevel;
            this.pendingLevel = this.currentLevel;
            this.pendingSinceUtc = DateTime.MinValue;
        }
    }

    internal static double CalculateScore(
        double totalGb,
        double availableGb,
        double commitPercent,
        double pageOutMegabytesPerSecond)
    {
        double availableScore = MapAvailableMemory(totalGb, availableGb);
        double pageOutScore = MapPageOutRate(pageOutMegabytesPerSecond);
        double activePressure = Math.Max(availableScore, pageOutScore) +
            0.25 * Math.Min(availableScore, pageOutScore);
        double commitSafetyFloor = MapCommitSafetyFloor(commitPercent);
        return Math.Max(Clamp(activePressure, 0.0, 100.0), commitSafetyFloor);
    }

    internal static MemoryPressureLevel Classify(double score)
    {
        if (score >= 80.0)
        {
            return MemoryPressureLevel.Critical;
        }

        if (score >= 50.0)
        {
            return MemoryPressureLevel.Warning;
        }

        return MemoryPressureLevel.Normal;
    }

    private static MemoryPressureLevel ResolveTargetLevel(double score, MemoryPressureLevel currentLevel)
    {
        if (currentLevel == MemoryPressureLevel.Critical)
        {
            return score < 70.0 ? MemoryPressureLevel.Warning : MemoryPressureLevel.Critical;
        }

        if (currentLevel == MemoryPressureLevel.Warning)
        {
            if (score >= 80.0)
            {
                return MemoryPressureLevel.Critical;
            }

            return score < 40.0 ? MemoryPressureLevel.Normal : MemoryPressureLevel.Warning;
        }

        return Classify(score);
    }

    private static double ApplyStableLevelFloor(double score, MemoryPressureLevel level)
    {
        if (level == MemoryPressureLevel.Critical)
        {
            return Math.Max(80.0, score);
        }

        if (level == MemoryPressureLevel.Warning)
        {
            return Math.Max(50.0, score);
        }

        return score;
    }

    private static double MapCommitSafetyFloor(double commitPercent)
    {
        double value = Clamp(commitPercent, 0.0, 100.0);
        if (value <= 90.0)
        {
            return 0.0;
        }

        if (value <= 95.0)
        {
            return MapRange(value, 90.0, 95.0, 0.0, 50.0);
        }

        if (value <= 98.0)
        {
            return MapRange(value, 95.0, 98.0, 50.0, 85.0);
        }

        return MapRange(value, 98.0, 100.0, 85.0, 100.0);
    }

    private static double MapPageOutRate(double pageOutMegabytesPerSecond)
    {
        double value = Math.Max(0.0, pageOutMegabytesPerSecond);
        if (value <= 0.25)
        {
            return 0.0;
        }

        if (value <= 1.0)
        {
            return MapRange(value, 0.25, 1.0, 0.0, 25.0);
        }

        if (value <= 4.0)
        {
            return MapRange(value, 1.0, 4.0, 25.0, 60.0);
        }

        return MapRange(value, 4.0, 16.0, 60.0, 100.0);
    }

    private static double MapAvailableMemory(double totalGb, double availableGb)
    {
        if (totalGb <= 0.0)
        {
            return 0.0;
        }

        double total = Math.Max(0.01, totalGb);
        double available = Clamp(availableGb, 0.0, total);
        double abundantThreshold = total * 0.20;
        double healthyThreshold = total * 0.10;
        double severeThreshold = total * 0.05;
        double criticalThreshold = Math.Max(total * 0.01, 0.5);
        if (severeThreshold <= criticalThreshold)
        {
            severeThreshold = Math.Min(total, criticalThreshold + Math.Max(0.1, total * 0.02));
        }

        healthyThreshold = Math.Max(severeThreshold, healthyThreshold);
        abundantThreshold = Math.Max(healthyThreshold, abundantThreshold);
        if (available >= abundantThreshold)
        {
            return 0.0;
        }

        if (available >= healthyThreshold)
        {
            return MapRange(available, abundantThreshold, healthyThreshold, 0.0, 50.0);
        }

        if (available >= severeThreshold)
        {
            return MapRange(available, healthyThreshold, severeThreshold, 50.0, 80.0);
        }

        if (available >= criticalThreshold)
        {
            return MapRange(available, severeThreshold, criticalThreshold, 80.0, 100.0);
        }

        return 100.0;
    }

    private static bool IsCriticallyLowAvailable(double totalGb, double availableGb)
    {
        if (totalGb <= 0.0)
        {
            return false;
        }

        return availableGb <= Math.Max(totalGb * 0.01, 0.5);
    }

    private static double MapRange(double value, double fromStart, double fromEnd, double toStart, double toEnd)
    {
        if (Math.Abs(fromEnd - fromStart) < 0.000001)
        {
            return toEnd;
        }

        double ratio = Clamp((value - fromStart) / (fromEnd - fromStart), 0.0, 1.0);
        return toStart + (toEnd - toStart) * ratio;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return value < min ? min : (value > max ? max : value);
    }

    internal static void RunSelfTest()
    {
        double commitHeavyButIdle = CalculateScore(47.6, 20.6, 83.8, 0.0);
        if (commitHeavyButIdle >= 10.0 || Classify(commitHeavyButIdle) != MemoryPressureLevel.Normal)
        {
            throw new InvalidOperationException("Abundant available RAM and no page-outs must stay normal below the commit guard.");
        }

        if (Classify(CalculateScore(48.0, 4.8, 43.0, 0.0)) != MemoryPressureLevel.Warning ||
            Classify(CalculateScore(48.0, 2.4, 43.0, 0.0)) != MemoryPressureLevel.Critical)
        {
            throw new InvalidOperationException("Available-memory headroom must drive the macOS-style warning and critical bands.");
        }

        if (Classify(CalculateScore(48.0, 20.0, 43.0, 4.0)) != MemoryPressureLevel.Warning ||
            Classify(CalculateScore(48.0, 20.0, 43.0, 16.0)) != MemoryPressureLevel.Critical)
        {
            throw new InvalidOperationException("Sustained page-outs must independently express active pressure.");
        }

        if (Classify(CalculateScore(48.0, 20.0, 95.0, 0.0)) != MemoryPressureLevel.Warning ||
            Classify(CalculateScore(48.0, 20.0, 98.0, 0.0)) != MemoryPressureLevel.Critical)
        {
            throw new InvalidOperationException("Commit must remain a late warning/critical safety floor.");
        }

        MemoryPressureTracker tracker = new MemoryPressureTracker();
        DateTime start = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        PerfSnapshot sample = new PerfSnapshot
        {
            MemoryTotalGb = 48.0,
            MemoryAvailableGb = 20.0,
            MemoryCommitPercent = 43.0
        };
        tracker.Update(sample, start);
        sample.MemoryAvailableGb = 4.8;
        tracker.Update(sample, start.AddSeconds(1.0));
        if (sample.MemoryPressureLevel != MemoryPressureLevel.Normal)
        {
            throw new InvalidOperationException("Warning promotion must wait for ten seconds.");
        }

        tracker.Update(sample, start.AddSeconds(11.1));
        if (sample.MemoryPressureLevel != MemoryPressureLevel.Warning)
        {
            throw new InvalidOperationException("Sustained pressure must promote to warning after ten seconds.");
        }

        sample.MemoryAvailableGb = 2.4;
        tracker.Update(sample, start.AddSeconds(12.0));
        tracker.Update(sample, start.AddSeconds(17.1));
        if (sample.MemoryPressureLevel != MemoryPressureLevel.Critical)
        {
            throw new InvalidOperationException("Critical promotion must settle after five seconds.");
        }

        sample.MemoryAvailableGb = 20.0;
        tracker.Update(sample, start.AddSeconds(18.0));
        tracker.Update(sample, start.AddSeconds(48.1));
        if (sample.MemoryPressureLevel != MemoryPressureLevel.Warning)
        {
            throw new InvalidOperationException("Critical recovery must step down to warning after thirty seconds.");
        }

        tracker.Update(sample, start.AddSeconds(49.0));
        tracker.Update(sample, start.AddSeconds(109.1));
        if (sample.MemoryPressureLevel != MemoryPressureLevel.Normal)
        {
            throw new InvalidOperationException("Warning recovery must return to normal after sixty seconds.");
        }

        sample.MemoryCommitPercent = 98.0;
        tracker.Update(sample, start.AddSeconds(110.0));
        if (sample.MemoryPressureLevel != MemoryPressureLevel.Critical)
        {
            throw new InvalidOperationException("Commit exhaustion must enter critical pressure immediately.");
        }

        Console.WriteLine("Memory pressure: PASS macOS-style headroom/page-out score, commit guard, three states, hysteresis");
    }
}
