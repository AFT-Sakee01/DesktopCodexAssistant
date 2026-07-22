using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Owns all mutable network state. Callers only receive clones so background tasks
// can update the internal snapshot without exposing partially committed results.
internal sealed class NetworkMonitorReader : IDisposable
{
    private const string ConnectivityTarget = "1.1.1.1";
    private const string CaptivePortalTestUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string CaptivePortalExpectedText = "Microsoft Connect Test";
    private const string PublicIpv4Endpoint = "https://api.ipify.org";
    private const int PingCount = 4;
    private const int PingTimeoutMs = 1000;
    private const int HttpTimeoutMs = 4000;
    private const int CaptivePortalBodyLimitBytes = 4096;
    private const int DegradedPacketLossPercent = 15;
    private const double DegradedLatencyMs = 800.0;
    private const double DegradedJitterMs = 250.0;
    private const int RollingPingMinSamples = 10;
    private const int RollingPingMaxSamples = 60;
    private const int RollingPingPublicTimeoutMs = 1000;
    private const int RollingPingGatewayTimeoutMs = 500;
    private const double RollingPingLossWarningPercent = 2.0;
    private const double RollingPingLossErrorPercent = 10.0;
    private const double RollingPingGatewayLatencyWarningMs = 30.0;
    private const double RollingPingGatewayJitterWarningMs = 20.0;
    private const double RollingPingPublicLatencyWarningMs = 300.0;
    private const double RollingPingPublicJitterWarningMs = 120.0;
    private const double RollingPingBaiduLatencyWarningMs = 150.0;
    private const double RollingPingBaiduJitterWarningMs = 80.0;
    private const string DnsKnownDomain = "www.msftconnecttest.com";
    private const int DnsQueryTimeoutMs = 1000;
    private const int MaxDnsProbeConcurrency = 2;
    private const ushort DnsQueryTypeA = 1;
    private const ushort DnsQueryTypeAaaa = 28;
    private static readonly TimeSpan NetworkChangeDebounceInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RollingPingSampleTtl = TimeSpan.FromMinutes(15);
    private static readonly string[] RollingPublicTargets = new string[]
    {
        "1.1.1.1",
        "1.0.0.1",
        "8.8.8.8",
        "8.8.4.4",
        "9.9.9.9",
        "149.112.112.112"
    };
    private const string RollingBaiduTarget = "www.baidu.com";
    private readonly object sync = new object();
    private readonly GfwProbeReader gfwProbeReader = new GfwProbeReader();
    private readonly CloudEndpointProbeReader cloudEndpointProbeReader = new CloudEndpointProbeReader();
    private readonly PathPingProbeReader pathPingProbeReader = new PathPingProbeReader();
    private readonly FixedPingProbeReader fixedPingProbeReader = new FixedPingProbeReader();
    // Per-hop probing only runs while a surface that shows the hop table is actually visible.
    // A collapsed dock tab therefore costs nothing beyond the pre-existing end-to-end probes.
    private bool pathPingSamplingActive;
    private NetworkMonitorSnapshot snapshot = new NetworkMonitorSnapshot();
    private DateTime lastLocalRefreshUtc;
    private DateTime lastPublicIpRefreshUtc;
    private DateTime lastConnectivityRefreshUtc;
    private DateTime lastDnsRefreshUtc;
    private bool localRefreshRequested = true;
    // Public IP and connectivity requests are single-flight independently.
    private bool publicIpRequestRunning;
    private bool connectivityRequestRunning;
    private bool dnsProbeRunning;
    private bool rollingPingRequestRunning;
    // Incremented whenever the selected adapter or its addresses may have changed.
    // Background results must match this generation and InterfaceId before commit.
    private long networkGeneration;
    private string selectedAdapterId = string.Empty;
    private string lastDnsProbeSignature = string.Empty;
    private readonly PingSampleWindow rollingGatewaySamples = new PingSampleWindow();
    private readonly PingSampleWindow rollingPublicSamples = new PingSampleWindow();
    private readonly PingSampleWindow rollingBaiduSamples = new PingSampleWindow();
    private DateTime lastRollingPingRefreshUtc = DateTime.MinValue;
    private int nextPublicPingTargetIndex;
    private string rollingPingIdentitySignature = string.Empty;
    private string lastRollingPingDiagnosisSignature = string.Empty;
    private string rollingLossGroup = string.Empty;
    private int rollingLossAboveCount;
    private int rollingLossBelowCount;
    private bool rollingLossConfirmed;
    private DateTime lastNetworkChangeAcceptedUtc = DateTime.MinValue;
    private bool disposed;

    public NetworkMonitorReader()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public NetworkMonitorSnapshot GetSnapshot()
    {
        return GetSnapshot(null);
    }

    // The owning form reports whether its hop table is on screen; see pathPingSamplingActive.
    public void SetPathPingSamplingActive(bool active)
    {
        lock (this.sync)
        {
            this.pathPingSamplingActive = active;
        }
    }

    public void RequestRefresh()
    {
        lock (this.sync)
        {
            // Manual refresh invalidates in-flight work but preserves the last public IP
            // until a replacement succeeds, avoiding unnecessary header flicker.
            this.localRefreshRequested = true;
            this.lastLocalRefreshUtc = DateTime.MinValue;
            this.lastPublicIpRefreshUtc = DateTime.MinValue;
            this.lastConnectivityRefreshUtc = DateTime.MinValue;
            this.lastDnsRefreshUtc = DateTime.MinValue;
            this.lastDnsProbeSignature = string.Empty;
            ClearRollingPingStateLocked("手动刷新");
            this.networkGeneration++;
            this.snapshot.PublicIpRefreshing = false;
            if (this.snapshot.Connected)
            {
                this.snapshot.ConnectivityKnown = false;
                this.snapshot.ConnectivityOnline = false;
                this.snapshot.AccessState = NetworkAccessState.Unknown;
                this.snapshot.AccessReason = "正在刷新";
                this.snapshot.LocalNetworkDegraded = false;
                this.snapshot.LocalNetworkDegradedReason = string.Empty;
            }
        }

        this.gfwProbeReader.RequestRefresh("手动刷新");
        this.cloudEndpointProbeReader.RequestRefresh("云服务手动刷新");
        this.pathPingProbeReader.RequestRediscover();
        this.fixedPingProbeReader.RequestRefresh();
    }

    public NetworkMonitorSnapshot GetSnapshot(WidgetSettings settings)
    {
        // Local metadata is cheap and synchronous; public IP and connectivity are single-flight tasks.
        DateTime now = DateTime.UtcNow;
        WidgetPerformanceMode mode = settings == null ? WidgetPerformanceMode.Balanced : settings.PerformanceMode;
        string requestedAdapterId = settings == null ? string.Empty : NormalizeAdapterId(settings.NetworkMonitorAdapterId);
        bool refreshLocal;
        lock (this.sync)
        {
            refreshLocal = this.localRefreshRequested ||
                !string.Equals(requestedAdapterId, this.selectedAdapterId, StringComparison.OrdinalIgnoreCase) ||
                (now - this.lastLocalRefreshUtc).TotalMilliseconds >= WidgetSettings.GetNetworkLocalRefreshIntervalMs(mode);
        }

        if (refreshLocal)
        {
            bool refreshRemoteProbes = RefreshLocalSnapshot(now, requestedAdapterId);
            if (refreshRemoteProbes)
            {
                this.gfwProbeReader.RequestRefresh("网络身份变化");
                this.cloudEndpointProbeReader.RequestRefresh("云服务网络身份变化");
            }
        }

        bool connected;
        NetworkAccessState accessState;
        DateTime connectivityStartedUtc;
        DateTime publicIpStartedUtc;
        DateTime dnsStartedUtc;
        string dnsSignature;
        string lastDnsSignature;
        DnsServerStatus worstDnsStatus;
        bool localNetworkDegraded;
        string localNetworkDegradedReason;
        PingRollingSnapshot rollingForGfwGate;
        bool rollingLossConfirmedForGfwGate;
        long probeNetworkGeneration;
        string probeInterfaceId;
        lock (this.sync)
        {
            connected = this.snapshot.Connected;
            accessState = GetActualAccessState(this.snapshot);
            connectivityStartedUtc = this.lastConnectivityRefreshUtc;
            publicIpStartedUtc = this.lastPublicIpRefreshUtc;
            dnsStartedUtc = this.lastDnsRefreshUtc;
            dnsSignature = BuildDnsAddressSignature(this.snapshot.DnsServerDetails);
            lastDnsSignature = this.lastDnsProbeSignature;
            worstDnsStatus = GetWorstDnsStatus(this.snapshot.DnsServerDetails);
            localNetworkDegraded = this.snapshot.LocalNetworkDegraded;
            localNetworkDegradedReason = this.snapshot.LocalNetworkDegradedReason;
            rollingForGfwGate = this.snapshot.PingRolling == null ? null : this.snapshot.PingRolling.Clone();
            rollingLossConfirmedForGfwGate = this.rollingLossConfirmed;
            probeNetworkGeneration = this.networkGeneration;
            probeInterfaceId = this.snapshot.InterfaceId;
        }

        int connectivityIntervalMs = WidgetSettings.GetNetworkConnectivityIntervalMs(mode, accessState);
        if (connected &&
            connectivityIntervalMs != int.MaxValue &&
            (now - connectivityStartedUtc).TotalMilliseconds >= connectivityIntervalMs)
        {
            string trigger = connectivityStartedUtc == DateTime.MinValue ? "首次或强制刷新" : "定时间隔";
            StartConnectivityRefresh(now, trigger);
        }

        if (connected &&
            accessState == NetworkAccessState.Online &&
            (now - publicIpStartedUtc).TotalMinutes >= WidgetSettings.GetNetworkPublicIpRefreshIntervalMinutes(mode))
        {
            string trigger = publicIpStartedUtc == DateTime.MinValue ? "首次或强制刷新" : "定时间隔";
            StartPublicIpRefresh(now, trigger);
        }

        int dnsProbeIntervalMs = WidgetSettings.GetNetworkDnsProbeIntervalMs(mode, worstDnsStatus);
        bool dnsAddressChanged = !string.Equals(dnsSignature, lastDnsSignature, StringComparison.OrdinalIgnoreCase);
        if (connected &&
            dnsSignature.Length > 0 &&
            (dnsAddressChanged ||
             (now - dnsStartedUtc).TotalMilliseconds >= dnsProbeIntervalMs))
        {
            string trigger = dnsAddressChanged
                ? "DNS地址变化"
                : (dnsStartedUtc == DateTime.MinValue ? "首次或强制刷新" : "定时间隔");
            StartDnsRefresh(now, trigger);
        }

        string gfwLocalNetworkGateReason;
        bool gfwLocalNetworkGate = TryBuildGfwLocalNetworkGate(
            rollingForGfwGate,
            rollingLossConfirmedForGfwGate,
            out gfwLocalNetworkGateReason);
        GfwProbeSnapshot gfwProbe = this.gfwProbeReader.GetSnapshot(
            settings,
            accessState,
            gfwLocalNetworkGate,
            gfwLocalNetworkGateReason,
            probeNetworkGeneration,
            probeInterfaceId);
        gfwProbe.CloudEndpoints = this.cloudEndpointProbeReader.GetSnapshot(
            settings,
            accessState,
            localNetworkDegraded,
            localNetworkDegradedReason,
            probeNetworkGeneration,
            probeInterfaceId);
        bool insideWall = IsExplicitGfwBlock(gfwProbe, accessState);
        AiRequestProtection.UpdateGfwSignal(
            insideWall,
            gfwProbe == null
                ? "GFW 状态不可用"
                : (gfwProbe.Status.ToString() + ":" + (gfwProbe.Reason ?? string.Empty)));
        RollingPingHistoryEntry rollingHistory;
        lock (this.sync)
        {
            this.snapshot.GfwProbe = gfwProbe;
            rollingHistory = ApplyRollingPingSnapshotLocked(accessState, insideWall, insideWall ? "墙内回退" : "状态变化");
        }

        WriteRollingPingHistory(rollingHistory);
        StartRollingPingRefresh(now, mode, accessState, insideWall);
        RefreshPathPing(settings);
        RefreshFixedPing(settings);

        lock (this.sync)
        {
            NetworkMonitorSnapshot clone = this.snapshot.Clone();
            ApplyNetworkStatusTestMode(clone, settings);
            ApplyCloudEndpointTestMode(clone, settings);
            return clone;
        }
    }

    // Per-hop path quality. The probe self-throttles; this only forwards the identity it needs to
    // decide whether the cached route is still the route, plus the ICMP verdict the rolling ping
    // already computed (re-deriving it here would double the probe traffic for no new information).
    private void RefreshPathPing(WidgetSettings settings)
    {
        string target;
        string gateway;
        long generation;
        string interfaceId;
        bool icmpBlocked;
        bool connected;
        bool samplingActive;
        lock (this.sync)
        {
            target = ResolvePathPingTarget(this.snapshot);
            gateway = this.snapshot.DefaultGatewayAddress;
            generation = this.networkGeneration;
            interfaceId = this.snapshot.InterfaceId;
            icmpBlocked = this.snapshot.PingRolling != null && this.snapshot.PingRolling.IcmpBlocked;
            connected = this.snapshot.Connected;
            samplingActive = this.pathPingSamplingActive;
        }

        PathPingSnapshot pathPing = this.pathPingProbeReader.Poll(
            settings,
            target,
            gateway,
            generation,
            interfaceId,
            samplingActive,
            icmpBlocked,
            connected);

        lock (this.sync)
        {
            this.snapshot.PathPing = pathPing;
        }
    }

    private static string ResolvePathPingTarget(NetworkMonitorSnapshot source)
    {
        string candidate = source == null || string.IsNullOrWhiteSpace(source.ConnectivityTarget)
            ? string.Empty
            : source.ConnectivityTarget.Trim();
        string displayProfile = source == null || source.PingRolling == null
            ? string.Empty
            : (source.PingRolling.ActiveProfile ?? string.Empty).Trim();

        // ActiveProfile is a presentation label (PUB/BAIDU), not a resolvable probe endpoint. A
        // previous rolling-ping update accidentally copied that label into ConnectivityTarget,
        // leaving PathPing in an endless discovery loop. Keep this boundary defensive so an old
        // in-memory snapshot or future display refactor cannot feed the label back into traceroute.
        if (candidate.Length == 0 ||
            (displayProfile.Length > 0 && string.Equals(candidate, displayProfile, StringComparison.OrdinalIgnoreCase)))
        {
            return ConnectivityTarget;
        }

        return candidate;
    }

    private void RefreshFixedPing(WidgetSettings settings)
    {
        bool connected;
        bool samplingActive;
        long generation;
        string interfaceId;
        lock (this.sync)
        {
            connected = this.snapshot.Connected;
            samplingActive = this.pathPingSamplingActive;
            generation = this.networkGeneration;
            interfaceId = this.snapshot.InterfaceId;
        }

        FixedPingSnapshot fixedPing = this.fixedPingProbeReader.Poll(
            settings,
            generation,
            interfaceId,
            samplingActive,
            connected);
        lock (this.sync)
        {
            this.snapshot.FixedPing = fixedPing;
        }
    }

    private bool RefreshLocalSnapshot(DateTime now, string requestedAdapterId)
    {
        long generationAtStart;
        lock (this.sync)
        {
            generationAtStart = this.networkGeneration;
        }

        requestedAdapterId = NormalizeAdapterId(requestedAdapterId);
        NetworkMonitorSnapshot local = BuildLocalSnapshot(requestedAdapterId);
        local.PublicIp = "--";
        local.ConnectivityTarget = ConnectivityTarget;
        bool refreshRemoteProbes = false;

        lock (this.sync)
        {
            // A network event during enumeration keeps the refresh pending for one more stable pass.
            bool eventDuringRefresh = generationAtStart != this.networkGeneration;
            bool hadLocalSnapshot = this.lastLocalRefreshUtc != DateTime.MinValue;
            NetworkMonitorSnapshot previous = this.snapshot;
            bool identityChanged = HasNetworkIdentityChanged(previous, local);
            refreshRemoteProbes = hadLocalSnapshot && HasRemoteProbeIdentityChanged(previous, local);
            if (identityChanged)
            {
                this.networkGeneration++;
                this.lastPublicIpRefreshUtc = DateTime.MinValue;
                this.lastConnectivityRefreshUtc = DateTime.MinValue;
                this.lastDnsRefreshUtc = DateTime.MinValue;
                this.lastDnsProbeSignature = string.Empty;
                ClearRollingPingStateLocked("网络身份变化");
            }

            if (!identityChanged)
            {
                // Remote measurements remain valid only while the adapter identity is stable.
                local.PublicIp = this.snapshot.PublicIp;
                local.PublicIpKnown = this.snapshot.PublicIpKnown;
                local.PublicIpRefreshing = this.publicIpRequestRunning;
                local.ConnectivityKnown = this.snapshot.ConnectivityKnown;
                local.ConnectivityOnline = this.snapshot.ConnectivityOnline;
                local.AccessState = this.snapshot.AccessState;
                local.AccessReason = this.snapshot.AccessReason;
                local.LatencyMs = this.snapshot.LatencyMs;
                local.JitterMs = this.snapshot.JitterMs;
                local.PacketLossPercent = this.snapshot.PacketLossPercent;
                local.LocalNetworkDegraded = this.snapshot.LocalNetworkDegraded;
                local.LocalNetworkDegradedReason = this.snapshot.LocalNetworkDegradedReason;
                local.ConnectivityTarget = this.snapshot.ConnectivityTarget;
                local.DnsServerDetails = CloneDnsServerDetails(this.snapshot.DnsServerDetails);
                local.PingRolling = this.snapshot.PingRolling == null ? new PingRollingSnapshot() : this.snapshot.PingRolling.Clone();
            }

            local.GfwProbe = this.snapshot.GfwProbe == null ? new GfwProbeSnapshot() : this.snapshot.GfwProbe.Clone();
            if (!local.Connected)
            {
                local.PublicIp = "--";
                local.PublicIpKnown = false;
                local.PublicIpRefreshing = false;
                local.ConnectivityKnown = true;
                local.ConnectivityOnline = false;
                local.AccessState = NetworkAccessState.AdapterMissing;
                local.AccessReason = "网卡未识别";
                local.LatencyMs = 0.0;
                local.JitterMs = 0.0;
                local.PacketLossPercent = 100;
                local.LocalNetworkDegraded = false;
                local.LocalNetworkDegradedReason = string.Empty;
                local.AccessReason = local.InterfaceKnown ? "网卡未连接" : "网卡未识别";
                local.LastError = local.InterfaceKnown ? "Selected interface is not up" : "No active interface";
                local.DnsServerDetails = MarkDnsServers(local.DnsServerDetails, DnsServerStatus.Unavailable, "网卡未连接");
            }
            else if (identityChanged)
            {
                local.PublicIp = "--";
                local.PublicIpKnown = false;
                local.PublicIpRefreshing = false;
                local.ConnectivityKnown = false;
                local.ConnectivityOnline = false;
                local.AccessState = NetworkAccessState.Unknown;
                local.AccessReason = "网络已变化";
                local.LatencyMs = 0.0;
                local.JitterMs = 0.0;
                local.PacketLossPercent = 0;
                local.LocalNetworkDegraded = false;
                local.LocalNetworkDegradedReason = string.Empty;
            }

            this.snapshot = local;
            this.lastLocalRefreshUtc = now;
            this.localRefreshRequested = eventDuringRefresh;
            this.selectedAdapterId = requestedAdapterId;
        }

        return refreshRemoteProbes;
    }

    private static NetworkMonitorSnapshot BuildLocalSnapshot(string requestedAdapterId)
    {
        NetworkMonitorSnapshot result = new NetworkMonitorSnapshot();
        result.UpdatedLocal = DateTime.Now;
        result.ConnectivityTarget = ConnectivityTarget;

        try
        {
            NetworkInterface best = SelectPrimaryInterface(requestedAdapterId);
            if (best == null)
            {
                result.Connected = false;
                result.InterfaceKnown = false;
                result.LastError = "No active interface";
                return result;
            }

            result.Connected = best.OperationalStatus == OperationalStatus.Up;
            result.InterfaceKnown = true;
            result.InterfaceId = best.Id ?? string.Empty;
            result.InterfaceName = EmptyFallback(best.Name, "Network");
            result.InterfaceDescription = EmptyFallback(best.Description, result.InterfaceName);
            result.InterfaceType = FormatInterfaceType(best.NetworkInterfaceType);
            result.MacAddress = FormatMacAddress(best.GetPhysicalAddress());
            result.LinkSpeedBps = best.Speed;
            result.IsWifi = best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

            IPInterfaceProperties properties = best.GetIPProperties();
            result.IPv4 = JoinUnicastAddresses(properties, AddressFamily.InterNetwork);
            result.IPv6 = JoinUnicastAddresses(properties, AddressFamily.InterNetworkV6);
            result.DefaultGatewayAddress = SelectDefaultGatewayAddress(properties);
            List<string> dnsServers = CollectDnsServers(properties);
            result.DnsServers = dnsServers.Count == 0 ? "--" : JoinLimited(dnsServers, 3);
            result.DnsServerDetails = DnsServerSnapshot.CreateUnknown(dnsServers);

            if (result.IsWifi)
            {
                Guid interfaceGuid;
                if (Guid.TryParse(best.Id, out interfaceGuid))
                {
                    WifiConnectionDetails details;
                    if (NativeMethods.TryGetConnectedWifiDetails(interfaceGuid, out details) && details != null)
                    {
                        result.WifiDetails = details;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.LastError = ex.GetType().Name;
        }

        return result;
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        MarkNetworkChanged();
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        MarkNetworkChanged();
    }

    private void MarkNetworkChanged()
    {
        DateTime now = DateTime.UtcNow;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.localRefreshRequested = true;
            if (this.lastNetworkChangeAcceptedUtc != DateTime.MinValue &&
                now - this.lastNetworkChangeAcceptedUtc < NetworkChangeDebounceInterval)
            {
                return;
            }

            this.lastNetworkChangeAcceptedUtc = now;
            // Incrementing the generation prevents old-network tasks from publishing stale results.
            // GFW/cloud refresh is deferred until RefreshLocalSnapshot confirms a real network
            // identity change, so DNS churn or repeated Windows events cannot reset their cadence.
            this.lastLocalRefreshUtc = DateTime.MinValue;
            this.lastPublicIpRefreshUtc = DateTime.MinValue;
            this.lastConnectivityRefreshUtc = DateTime.MinValue;
            this.lastDnsRefreshUtc = DateTime.MinValue;
            this.lastDnsProbeSignature = string.Empty;
            ClearRollingPingStateLocked("网络身份变化");
            this.networkGeneration++;
            this.snapshot.PublicIp = "--";
            this.snapshot.PublicIpKnown = false;
            this.snapshot.PublicIpRefreshing = false;
            if (this.snapshot.Connected)
            {
                this.snapshot.ConnectivityKnown = false;
                this.snapshot.ConnectivityOnline = false;
                this.snapshot.AccessState = NetworkAccessState.Unknown;
                this.snapshot.AccessReason = "网络已变化";
                this.snapshot.LocalNetworkDegraded = false;
                this.snapshot.LocalNetworkDegradedReason = string.Empty;
            }
        }
    }

    private static NetworkAccessState GetActualAccessState(NetworkMonitorSnapshot value)
    {
        if (value == null || !value.Connected)
        {
            return NetworkAccessState.AdapterMissing;
        }

        if (!value.ConnectivityKnown || value.AccessState == NetworkAccessState.AdapterMissing)
        {
            return NetworkAccessState.Unknown;
        }

        if (value.AccessState != NetworkAccessState.Unknown)
        {
            return value.AccessState;
        }

        return value.ConnectivityOnline ? NetworkAccessState.Online : NetworkAccessState.Offline;
    }

    private static bool HasNetworkIdentityChanged(NetworkMonitorSnapshot previous, NetworkMonitorSnapshot current)
    {
        if (previous == null || current == null)
        {
            return true;
        }

        return previous.Connected != current.Connected ||
            previous.InterfaceKnown != current.InterfaceKnown ||
            !string.Equals(previous.InterfaceId, current.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.IPv4, current.IPv4, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.IPv6, current.IPv6, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.DefaultGatewayAddress, current.DefaultGatewayAddress, StringComparison.OrdinalIgnoreCase) ||
            !HasSameDnsServerAddresses(previous.DnsServerDetails, current.DnsServerDetails);
    }

    private static bool HasRemoteProbeIdentityChanged(NetworkMonitorSnapshot previous, NetworkMonitorSnapshot current)
    {
        if (current == null || !current.Connected)
        {
            return false;
        }

        if (previous == null || !previous.Connected)
        {
            return true;
        }

        return previous.InterfaceKnown != current.InterfaceKnown ||
            !string.Equals(previous.InterfaceId, current.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.DefaultGatewayAddress, current.DefaultGatewayAddress, StringComparison.OrdinalIgnoreCase) ||
            !HasSameAddressText(previous.IPv4, current.IPv4) ||
            !HasSameAddressText(previous.IPv6, current.IPv6);
    }

    private static bool HasSameDnsServerAddresses(DnsServerSnapshot[] left, DnsServerSnapshot[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        List<string> leftValues = ExtractDnsServerAddresses(left);
        List<string> rightValues = ExtractDnsServerAddresses(right);
        if (leftValues.Count != rightValues.Count)
        {
            return false;
        }

        leftValues.Sort(StringComparer.OrdinalIgnoreCase);
        rightValues.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < leftValues.Count; i++)
        {
            if (!string.Equals(leftValues[i], rightValues[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ExtractDnsServerAddresses(DnsServerSnapshot[] details)
    {
        List<string> values = new List<string>();
        if (details == null)
        {
            return values;
        }

        for (int i = 0; i < details.Length; i++)
        {
            string address = details[i] == null ? string.Empty : details[i].Address;
            if (!string.IsNullOrWhiteSpace(address))
            {
                AddDistinct(values, address.Trim());
            }
        }

        return values;
    }

    private static bool HasSameAddressText(string left, string right)
    {
        List<string> leftValues = SplitAddressText(left);
        List<string> rightValues = SplitAddressText(right);
        if (leftValues.Count != rightValues.Count)
        {
            return false;
        }

        leftValues.Sort(StringComparer.OrdinalIgnoreCase);
        rightValues.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < leftValues.Count; i++)
        {
            if (!string.Equals(leftValues[i], rightValues[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> SplitAddressText(string value)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "--", StringComparison.Ordinal))
        {
            return result;
        }

        string[] parts = value.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i] == null ? string.Empty : parts[i].Trim();
            if (part.Length == 0 || part[0] == '+')
            {
                continue;
            }

            AddDistinct(result, part);
        }

        return result;
    }

    private static NetworkInterface SelectPrimaryInterface(string requestedAdapterId)
    {
        // Prefer a usable default-route interface, then real address families and link type.
        // Link speed is only a final tie-breaker so a fast virtual interface does not win alone.
        NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
        requestedAdapterId = NormalizeAdapterId(requestedAdapterId);
        if (requestedAdapterId.Length > 0)
        {
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface item = interfaces[i];
                if (item != null &&
                    (string.Equals(item.Id, requestedAdapterId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Name, requestedAdapterId, StringComparison.OrdinalIgnoreCase)))
                {
                    return item;
                }
            }
        }

        NetworkInterface best = null;
        long bestScore = long.MinValue;
        for (int i = 0; i < interfaces.Length; i++)
        {
            NetworkInterface item = interfaces[i];
            if (item == null ||
                item.OperationalStatus != OperationalStatus.Up ||
                item.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                item.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            long score = ScoreInterface(item);
            if (best == null || score > bestScore)
            {
                best = item;
                bestScore = score;
            }
        }

        return best;
    }

    private static string NormalizeAdapterId(string adapterId)
    {
        return (adapterId ?? string.Empty).Trim();
    }

    private static long ScoreInterface(NetworkInterface item)
    {
        long score = 0;
        try
        {
            IPInterfaceProperties properties = item.GetIPProperties();
            if (HasGateway(properties))
            {
                score += 1000000000000L;
            }

            if (HasUnicastAddress(properties, AddressFamily.InterNetwork))
            {
                score += 1000000000L;
            }

            if (HasUnicastAddress(properties, AddressFamily.InterNetworkV6))
            {
                score += 1000000L;
            }
        }
        catch
        {
        }

        if (item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
            item.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
            item.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
        {
            score += 10000L;
        }

        if (item.Speed > 0)
        {
            score += Math.Min(item.Speed / 1000000L, 9000L);
        }

        return score;
    }

    private static bool HasGateway(IPInterfaceProperties properties)
    {
        if (properties == null || properties.GatewayAddresses == null)
        {
            return false;
        }

        foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
        {
            if (gateway != null && gateway.Address != null && !IPAddress.Any.Equals(gateway.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static string SelectDefaultGatewayAddress(IPInterfaceProperties properties)
    {
        if (properties == null || properties.GatewayAddresses == null)
        {
            return string.Empty;
        }

        string fallback = string.Empty;
        foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
        {
            if (gateway == null || gateway.Address == null || IPAddress.Any.Equals(gateway.Address) || IsIgnorableAddress(gateway.Address))
            {
                continue;
            }

            string address = gateway.Address.ToString();
            if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                return address;
            }

            if (fallback.Length == 0)
            {
                fallback = address;
            }
        }

        return fallback;
    }

    private static bool HasUnicastAddress(IPInterfaceProperties properties, AddressFamily family)
    {
        if (properties == null || properties.UnicastAddresses == null)
        {
            return false;
        }

        foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
        {
            if (address != null &&
                address.Address != null &&
                address.Address.AddressFamily == family &&
                !IsIgnorableAddress(address.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static string JoinUnicastAddresses(IPInterfaceProperties properties, AddressFamily family)
    {
        if (properties == null || properties.UnicastAddresses == null)
        {
            return "--";
        }

        List<string> values = new List<string>();
        foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
        {
            if (address == null ||
                address.Address == null ||
                address.Address.AddressFamily != family ||
                IsIgnorableAddress(address.Address))
            {
                continue;
            }

            AddDistinct(values, address.Address.ToString());
        }

        values.Sort(StringComparer.OrdinalIgnoreCase);
        return values.Count == 0 ? "--" : JoinLimited(values, 2);
    }

    private static List<string> CollectDnsServers(IPInterfaceProperties properties)
    {
        List<string> values = new List<string>();
        if (properties == null || properties.DnsAddresses == null)
        {
            return values;
        }

        foreach (IPAddress address in properties.DnsAddresses)
        {
            if (address == null || IsIgnorableAddress(address))
            {
                continue;
            }

            AddDistinct(values, address.ToString());
        }

        values.Sort(StringComparer.OrdinalIgnoreCase);
        return values;
    }

    private static bool IsIgnorableAddress(IPAddress address)
    {
        if (address == null)
        {
            return true;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                (bytes.Length > 0 && bytes[0] == 0);
        }

        return false;
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        values.Add(value);
    }

    private static string JoinLimited(List<string> values, int maxCount)
    {
        StringBuilder builder = new StringBuilder();
        int count = Math.Min(maxCount, values.Count);
        for (int i = 0; i < count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(values[i]);
        }

        if (values.Count > count)
        {
            builder.Append(" +");
            builder.Append((values.Count - count).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
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

    private static DnsServerSnapshot[] MarkDnsServers(DnsServerSnapshot[] details, DnsServerStatus status, string reason)
    {
        DnsServerSnapshot[] clone = CloneDnsServerDetails(details);
        DateTime now = DateTime.Now;
        for (int i = 0; i < clone.Length; i++)
        {
            clone[i].Status = status;
            clone[i].Reason = reason ?? string.Empty;
            clone[i].LatencyMs = 0;
            clone[i].CheckedAtLocal = now;
            clone[i].CheckedAtKnown = true;
        }

        return clone;
    }

    private static string BuildDnsAddressSignature(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < details.Length; i++)
        {
            string address = details[i] == null ? string.Empty : details[i].Address;
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("|");
            }

            builder.Append(address.Trim());
        }

        return builder.ToString();
    }

    private static DnsServerStatus GetWorstDnsStatus(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return DnsServerStatus.Unknown;
        }

        DnsServerStatus worst = DnsServerStatus.Normal;
        for (int i = 0; i < details.Length; i++)
        {
            DnsServerStatus status = details[i] == null ? DnsServerStatus.Unknown : details[i].Status;
            if (GetDnsStatusPriority(status) > GetDnsStatusPriority(worst))
            {
                worst = status;
            }
        }

        return worst;
    }

    private static int GetDnsStatusPriority(DnsServerStatus status)
    {
        if (status == DnsServerStatus.Hijacked)
        {
            return 400;
        }

        if (status == DnsServerStatus.Problem)
        {
            return 300;
        }

        if (status == DnsServerStatus.Unavailable)
        {
            return 200;
        }

        if (status == DnsServerStatus.Unknown)
        {
            return 100;
        }

        return 0;
    }

    private void StartRollingPingRefresh(DateTime now, WidgetPerformanceMode mode, NetworkAccessState accessState, bool insideWall)
    {
        RollingPingRequest request;
        lock (this.sync)
        {
            if (this.disposed || this.rollingPingRequestRunning)
            {
                return;
            }

            if (!this.snapshot.Connected || accessState != NetworkAccessState.Online)
            {
                this.lastRollingPingRefreshUtc = DateTime.MinValue;
                return;
            }

            string identity = BuildRollingPingIdentitySignatureLocked();
            if (!string.Equals(identity, this.rollingPingIdentitySignature, StringComparison.Ordinal))
            {
                ClearRollingPingStateLocked("网络身份变化");
                this.rollingPingIdentitySignature = identity;
            }

            bool firstRollingRefresh = this.lastRollingPingRefreshUtc == DateTime.MinValue;
            int intervalMs = GetRollingPingIntervalMs(mode);
            if (!firstRollingRefresh &&
                (now - this.lastRollingPingRefreshUtc).TotalMilliseconds < intervalMs)
            {
                return;
            }

            string target;
            string activeProfile;
            string activeGroup;
            if (insideWall)
            {
                target = RollingBaiduTarget;
                activeProfile = "BAIDU";
                activeGroup = "baidu";
            }
            else
            {
                int index = this.nextPublicPingTargetIndex % RollingPublicTargets.Length;
                if (index < 0)
                {
                    index = 0;
                }

                target = RollingPublicTargets[index];
                this.nextPublicPingTargetIndex = (index + 1) % RollingPublicTargets.Length;
                activeProfile = "PUB";
                activeGroup = "public";
            }

            this.rollingPingRequestRunning = true;
            this.lastRollingPingRefreshUtc = now;
            request = new RollingPingRequest
            {
                Generation = this.networkGeneration,
                InterfaceId = this.snapshot.InterfaceId,
                IdentitySignature = identity,
                Gateway = this.snapshot.DefaultGatewayAddress,
                Target = target,
                ActiveProfile = activeProfile,
                ActiveGroup = activeGroup,
                InsideWall = insideWall,
                Trigger = insideWall ? "墙内回退" : (firstRollingRefresh ? "首次或强制刷新" : "定时间隔")
            };
        }

        Task.Run(delegate
        {
            PingProbeResult gatewayResult = string.IsNullOrWhiteSpace(request.Gateway)
                ? PingProbeResult.CreateSkipped()
                : ProbePingOnce(request.Gateway, RollingPingGatewayTimeoutMs);
            PingProbeResult activeResult = ProbePingOnce(request.Target, RollingPingPublicTimeoutMs);
            DateTime completedUtc = DateTime.UtcNow;
            RollingPingHistoryEntry history = null;
            RollingPingHistoryEntry lossHistory = null;

            lock (this.sync)
            {
                this.rollingPingRequestRunning = false;
                if (this.disposed ||
                    request.Generation != this.networkGeneration ||
                    !string.Equals(request.InterfaceId, this.snapshot.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(request.IdentitySignature, BuildRollingPingIdentitySignatureLocked(), StringComparison.Ordinal))
                {
                    this.lastRollingPingRefreshUtc = DateTime.MinValue;
                    return;
                }

                if (!gatewayResult.Skipped)
                {
                    this.rollingGatewaySamples.Add(gatewayResult.Success, gatewayResult.LatencyMs, completedUtc);
                }

                if (string.Equals(request.ActiveGroup, "baidu", StringComparison.Ordinal))
                {
                    this.rollingBaiduSamples.Add(activeResult.Success, activeResult.LatencyMs, completedUtc);
                }
                else
                {
                    this.rollingPublicSamples.Add(activeResult.Success, activeResult.LatencyMs, completedUtc);
                }

                NetworkAccessState currentAccessState = GetActualAccessState(this.snapshot);
                bool currentInsideWall = IsExplicitGfwBlock(this.snapshot.GfwProbe, currentAccessState);
                history = ApplyRollingPingSnapshotLocked(currentAccessState, currentInsideWall, request.Trigger);
                lossHistory = ApplyRollingLossConfirmationLocked(this.snapshot.PingRolling, request.Trigger);
            }

            WriteRollingPingHistory(history);
            WriteRollingPingHistory(lossHistory);
        });
    }

    private RollingPingHistoryEntry ApplyRollingPingSnapshotLocked(NetworkAccessState accessState, bool insideWall, string trigger)
    {
        PingGroupStats gateway = this.rollingGatewaySamples.BuildStats("gateway", DateTime.UtcNow);
        PingGroupStats publicStats = this.rollingPublicSamples.BuildStats("public", DateTime.UtcNow);
        PingGroupStats baiduStats = this.rollingBaiduSamples.BuildStats("baidu", DateTime.UtcNow);
        PingGroupStats active = insideWall ? baiduStats : publicStats;

        PingRollingSnapshot rolling = BuildRollingPingSnapshot(accessState, insideWall, gateway, active);
        this.snapshot.PingRolling = rolling;
        if (rolling.StatsReady && !rolling.IcmpBlocked)
        {
            this.snapshot.LatencyMs = rolling.LatencyMs;
            if (rolling.JitterKnown)
            {
                this.snapshot.JitterMs = rolling.JitterMs;
            }

            this.snapshot.PacketLossPercent = Math.Max(0, Math.Min(100, (int)Math.Round(rolling.LossPercent)));
        }

        string signature = BuildRollingPingDiagnosisSignature(rolling);
        if (string.Equals(signature, this.lastRollingPingDiagnosisSignature, StringComparison.Ordinal))
        {
            return null;
        }

        this.lastRollingPingDiagnosisSignature = signature;
        if (rolling.Diagnosis == PingPathDiagnosis.None && rolling.SampleCount == 0)
        {
            return null;
        }

        return BuildRollingPingHistoryEntry("rolling_ping", trigger, rolling);
    }

    private RollingPingHistoryEntry ApplyRollingLossConfirmationLocked(PingRollingSnapshot rolling, string trigger)
    {
        if (rolling == null || !rolling.StatsReady || rolling.IcmpBlocked)
        {
            this.rollingLossAboveCount = 0;
            if (!this.rollingLossConfirmed)
            {
                this.rollingLossBelowCount = 0;
            }

            return null;
        }

        string group = rolling.Group ?? string.Empty;
        if (!string.Equals(group, this.rollingLossGroup, StringComparison.Ordinal))
        {
            this.rollingLossGroup = group;
            this.rollingLossAboveCount = 0;
            this.rollingLossBelowCount = 0;
            this.rollingLossConfirmed = false;
        }

        if (rolling.LossPercent >= RollingPingLossWarningPercent)
        {
            this.rollingLossAboveCount++;
            this.rollingLossBelowCount = 0;
            bool confirmed = this.rollingLossAboveCount >= 2 ||
                (rolling.LossPercent >= RollingPingLossErrorPercent && rolling.SampleCount >= 20);
            if (confirmed && !this.rollingLossConfirmed)
            {
                this.rollingLossConfirmed = true;
                RollingPingHistoryEntry entry = BuildRollingPingHistoryEntry("rolling_ping_loss_confirmed", trigger, rolling);
                entry.Result = "丢包确认 " + FormatLossPercent(rolling.LossPercent);
                entry.Success = false;
                return entry;
            }

            return null;
        }

        this.rollingLossAboveCount = 0;
        if (this.rollingLossConfirmed)
        {
            this.rollingLossBelowCount++;
            if (this.rollingLossBelowCount >= 2)
            {
                this.rollingLossConfirmed = false;
                this.rollingLossBelowCount = 0;
                RollingPingHistoryEntry entry = BuildRollingPingHistoryEntry("rolling_ping_loss_confirmed", "丢包恢复", rolling);
                entry.Result = "丢包恢复 " + FormatLossPercent(rolling.LossPercent);
                entry.Success = true;
                return entry;
            }
        }

        return null;
    }

    private static PingRollingSnapshot BuildRollingPingSnapshot(
        NetworkAccessState accessState,
        bool insideWall,
        PingGroupStats gateway,
        PingGroupStats active)
    {
        PingRollingSnapshot rolling = new PingRollingSnapshot
        {
            ActiveProfile = insideWall ? "BAIDU" : "PUB",
            ActiveTargetLabel = insideWall ? "BAIDU" : "PUB",
            Group = insideWall ? "baidu" : "public",
            SampleCount = active.TotalCount,
            LostCount = active.LostCount,
            LossPercent = active.LossPercent,
            LatencyMs = active.LatencyMs,
            JitterMs = active.JitterMs,
            JitterKnown = active.JitterKnown,
            StatsReady = active.StatsReady
        };

        bool gatewayHealthyEnough = gateway.SuccessCount > 0 &&
            (!gateway.StatsReady ||
             (gateway.LossPercent < RollingPingLossWarningPercent &&
              gateway.LatencyMs < RollingPingGatewayLatencyWarningMs &&
              (!gateway.JitterKnown || gateway.JitterMs < RollingPingGatewayJitterWarningMs)));
        rolling.IcmpBlocked = accessState == NetworkAccessState.Online &&
            gateway.SuccessCount > 0 &&
            active.StatsReady &&
            active.SuccessCount == 0;

        if (accessState == NetworkAccessState.AdapterMissing)
        {
            SetRollingDiagnosis(rolling, PingPathDiagnosis.AdapterMissing, PingDiagnosisSeverity.Error, "ADAPTER");
            return rolling;
        }

        if (accessState == NetworkAccessState.NeedsValidation)
        {
            SetRollingDiagnosis(rolling, PingPathDiagnosis.CaptivePortal, PingDiagnosisSeverity.Warning, "CAPTIVE");
            return rolling;
        }

        if (accessState == NetworkAccessState.Offline)
        {
            SetRollingDiagnosis(rolling, PingPathDiagnosis.Offline, PingDiagnosisSeverity.Error, "OFFLINE");
            return rolling;
        }

        if (gateway.StatsReady && gateway.LossPercent >= RollingPingLossWarningPercent)
        {
            SetRollingDiagnosis(
                rolling,
                PingPathDiagnosis.LocalLoss,
                gateway.LossPercent >= RollingPingLossErrorPercent ? PingDiagnosisSeverity.Error : PingDiagnosisSeverity.Warning,
                "LOCAL LOSS");
            return rolling;
        }

        if (gateway.SuccessCount > 0 &&
            (gateway.LatencyMs >= RollingPingGatewayLatencyWarningMs ||
             (gateway.JitterKnown && gateway.JitterMs >= RollingPingGatewayJitterWarningMs)))
        {
            SetRollingDiagnosis(rolling, PingPathDiagnosis.LocalLatency, PingDiagnosisSeverity.Warning, "LOCAL LAT");
            return rolling;
        }

        if (rolling.IcmpBlocked)
        {
            SetRollingDiagnosis(rolling, PingPathDiagnosis.IcmpBlocked, PingDiagnosisSeverity.Warning, "ICMP BLOCK");
            return rolling;
        }

        double latencyThreshold = insideWall ? RollingPingBaiduLatencyWarningMs : RollingPingPublicLatencyWarningMs;
        double jitterThreshold = insideWall ? RollingPingBaiduJitterWarningMs : RollingPingPublicJitterWarningMs;
        if (gatewayHealthyEnough && active.StatsReady && active.LossPercent >= RollingPingLossWarningPercent)
        {
            SetRollingDiagnosis(
                rolling,
                insideWall ? PingPathDiagnosis.BaiduLoss : PingPathDiagnosis.WanLoss,
                active.LossPercent >= RollingPingLossErrorPercent ? PingDiagnosisSeverity.Error : PingDiagnosisSeverity.Warning,
                insideWall ? "BAIDU LOSS" : "WAN LOSS");
            return rolling;
        }

        if (gatewayHealthyEnough &&
            active.SuccessCount > 0 &&
            (active.LatencyMs >= latencyThreshold || (active.JitterKnown && active.JitterMs >= jitterThreshold)))
        {
            SetRollingDiagnosis(
                rolling,
                insideWall ? PingPathDiagnosis.BaiduLatency : PingPathDiagnosis.WanLatency,
                PingDiagnosisSeverity.Warning,
                insideWall ? "BAIDU LAT" : "WAN LAT");
            return rolling;
        }

        if (insideWall)
        {
            SetRollingDiagnosis(rolling, PingPathDiagnosis.GlobalBlock, PingDiagnosisSeverity.Warning, "GLOBAL BLOCK");
        }

        return rolling;
    }

    private static void SetRollingDiagnosis(
        PingRollingSnapshot rolling,
        PingPathDiagnosis diagnosis,
        PingDiagnosisSeverity severity,
        string text)
    {
        if (rolling == null)
        {
            return;
        }

        rolling.Diagnosis = diagnosis;
        rolling.Severity = severity;
        rolling.DiagnosisText = text ?? string.Empty;
    }

    private static string BuildRollingPingDiagnosisSignature(PingRollingSnapshot rolling)
    {
        if (rolling == null)
        {
            return string.Empty;
        }

        string lossBand = "cold";
        if (rolling.StatsReady)
        {
            lossBand = rolling.LossPercent >= RollingPingLossErrorPercent
                ? "loss_error"
                : (rolling.LossPercent >= RollingPingLossWarningPercent ? "loss_warn" : "loss_ok");
        }

        return (rolling.ActiveProfile ?? string.Empty) + "|" +
            rolling.Diagnosis.ToString() + "|" +
            rolling.Severity.ToString() + "|" +
            lossBand + "|" +
            rolling.IcmpBlocked.ToString();
    }

    private RollingPingHistoryEntry BuildRollingPingHistoryEntry(string checkName, string trigger, PingRollingSnapshot rolling)
    {
        bool success = rolling != null &&
            rolling.Diagnosis != PingPathDiagnosis.AdapterMissing &&
            rolling.Diagnosis != PingPathDiagnosis.Offline &&
            rolling.Diagnosis != PingPathDiagnosis.IcmpBlocked;
        string result = rolling == null
            ? "无状态"
            : EmptyFallback(rolling.DiagnosisText, "OK") + " " + rolling.ActiveProfile + " loss " + FormatLossPercent(rolling.LossPercent);
        return new RollingPingHistoryEntry
        {
            CheckName = checkName,
            Trigger = trigger,
            Result = result,
            Success = success,
            Detail = new Dictionary<string, object>
            {
                { "active_profile", rolling == null ? string.Empty : rolling.ActiveProfile },
                { "group", rolling == null ? string.Empty : rolling.Group },
                { "sample_count", rolling == null ? 0 : rolling.SampleCount },
                { "lost_count", rolling == null ? 0 : rolling.LostCount },
                { "loss_percent", rolling == null ? 0.0 : Math.Round(rolling.LossPercent, 1) },
                { "latency_ms", rolling == null ? 0.0 : Math.Round(rolling.LatencyMs, 1) },
                { "jitter_ms", rolling == null ? 0.0 : Math.Round(rolling.JitterMs, 1) },
                { "diagnosis", rolling == null ? string.Empty : rolling.Diagnosis.ToString() }
            }
        };
    }

    private static void WriteRollingPingHistory(RollingPingHistoryEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        NetworkCheckHistoryLogger.LogCompleted(
            "network_monitor",
            entry.CheckName,
            entry.Trigger,
            entry.Result,
            entry.Success,
            -1,
            entry.Detail);
    }

    private void ClearRollingPingStateLocked(string trigger)
    {
        this.rollingGatewaySamples.Clear();
        this.rollingPublicSamples.Clear();
        this.rollingBaiduSamples.Clear();
        this.lastRollingPingRefreshUtc = DateTime.MinValue;
        this.rollingPingIdentitySignature = string.Empty;
        this.lastRollingPingDiagnosisSignature = string.Empty;
        this.rollingLossGroup = string.Empty;
        this.rollingLossAboveCount = 0;
        this.rollingLossBelowCount = 0;
        this.rollingLossConfirmed = false;
        if (this.snapshot != null)
        {
            this.snapshot.PingRolling = new PingRollingSnapshot
            {
                DiagnosisText = string.Equals(trigger, "网络身份变化", StringComparison.Ordinal) ? "RESET" : string.Empty
            };
        }
    }

    private string BuildRollingPingIdentitySignatureLocked()
    {
        return this.networkGeneration.ToString(CultureInfo.InvariantCulture) + "|" +
            (this.snapshot == null ? string.Empty : this.snapshot.InterfaceId ?? string.Empty) + "|" +
            (this.snapshot == null ? string.Empty : this.snapshot.IPv4 ?? string.Empty) + "|" +
            (this.snapshot == null ? string.Empty : this.snapshot.DefaultGatewayAddress ?? string.Empty);
    }

    private static int GetRollingPingIntervalMs(WidgetPerformanceMode mode)
    {
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 2000;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 10000;
        }

        return 5000;
    }

    private static bool IsExplicitGfwBlock(GfwProbeSnapshot gfw, NetworkAccessState accessState)
    {
        if (accessState != NetworkAccessState.Online || gfw == null || !gfw.Enabled || !gfw.CheckedAtKnown)
        {
            return false;
        }

        return gfw.Status == GfwProbeStatus.SuspectedDns ||
            gfw.Status == GfwProbeStatus.SuspectedTcp ||
            gfw.Status == GfwProbeStatus.SuspectedTlsSni ||
            gfw.Status == GfwProbeStatus.SuspectedHttp;
    }

    private static bool TryBuildGfwLocalNetworkGate(
        PingRollingSnapshot rolling,
        bool rollingLossConfirmed,
        out string reason)
    {
        reason = string.Empty;
        if (rolling == null || !rolling.StatsReady || rolling.IcmpBlocked)
        {
            return false;
        }

        // GFW gating must only consume confirmed packet loss on the active
        // public/Baidu rolling window. Gateway-only ICMP loss and latency/jitter
        // diagnostics are shown on the PING line, but they are too coarse to
        // suppress GFW probes.
        if (rolling.Diagnosis == PingPathDiagnosis.WanLoss ||
            rolling.Diagnosis == PingPathDiagnosis.BaiduLoss ||
            rolling.Diagnosis == PingPathDiagnosis.LocalLoss)
        {
            if (!rollingLossConfirmed || rolling.LossPercent < RollingPingLossWarningPercent)
            {
                return false;
            }

            reason = "滚动PING确认丢包 " + FormatLossPercent(rolling.LossPercent);
            return true;
        }

        return false;
    }

    private static PingProbeResult ProbePingOnce(string target, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return PingProbeResult.CreateSkipped();
        }

        try
        {
            using (Ping ping = new Ping())
            {
                PingReply reply = ping.Send(target, timeoutMs);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    return new PingProbeResult
                    {
                        Success = true,
                        LatencyMs = (int)Math.Min(int.MaxValue, Math.Max(0L, reply.RoundtripTime))
                    };
                }
            }
        }
        catch
        {
        }

        return new PingProbeResult();
    }

    private static string FormatLossPercent(double value)
    {
        return Math.Max(0.0, Math.Min(100.0, value)).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private void StartPublicIpRefresh(DateTime now, string trigger)
    {
        long requestGeneration;
        string requestInterfaceId;
        lock (this.sync)
        {
            if (this.disposed || this.publicIpRequestRunning)
            {
                return;
            }

            this.publicIpRequestRunning = true;
            this.lastPublicIpRefreshUtc = now;
            this.snapshot.PublicIpRefreshing = true;
            requestGeneration = this.networkGeneration;
            requestInterfaceId = this.snapshot.InterfaceId;
        }

        // A generation check alone is not sufficient if a caller manually invalidates and
        // rebuilds the same generation path; InterfaceId provides a second identity guard.
        Task.Run(delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string ip = string.Empty;
            string error = string.Empty;
            bool success = false;
            int endpointAttempts = 0;
            try
            {
                endpointAttempts++;
                string response = FetchText(PublicIpv4Endpoint);
                success = TryNormalizePublicIpv4Response(response, out ip);
                if (!success)
                {
                    error = "非IPv4公网响应";
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
            }

            stopwatch.Stop();
            bool committed = false;
            lock (this.sync)
            {
                this.publicIpRequestRunning = false;
                if (this.disposed ||
                    requestGeneration != this.networkGeneration ||
                    !string.Equals(requestInterfaceId, this.snapshot.InterfaceId, StringComparison.OrdinalIgnoreCase))
                {
                    this.snapshot.PublicIpRefreshing = false;
                    this.lastPublicIpRefreshUtc = DateTime.MinValue;
                    return;
                }

                this.snapshot.PublicIpRefreshing = false;
                if (success)
                {
                    this.snapshot.PublicIp = ip;
                    this.snapshot.PublicIpKnown = true;
                    this.snapshot.LastError = string.Empty;
                }
                else
                {
                    this.snapshot.PublicIpKnown = false;
                    this.snapshot.LastError = error;
                }
                committed = true;
            }

            if (committed)
            {
                NetworkCheckHistoryLogger.LogCompleted(
                    "network_monitor",
                    "public_ip",
                    trigger,
                    success ? "成功" : EmptyFallback(error, "失败"),
                    success,
                    (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                    new Dictionary<string, object>
                    {
                        { "endpoint_attempts", endpointAttempts }
                    });
            }
        });
    }

    private void StartDnsRefresh(DateTime now, string trigger)
    {
        long requestGeneration;
        string requestInterfaceId;
        string[] addresses;
        string signature;
        DnsServerSnapshot[] previousDetails;
        bool localNetworkDegraded;
        string localNetworkDegradedReason;
        lock (this.sync)
        {
            if (this.disposed || this.dnsProbeRunning)
            {
                return;
            }

            addresses = GetDnsAddresses(this.snapshot.DnsServerDetails);
            if (addresses.Length == 0)
            {
                return;
            }

            this.dnsProbeRunning = true;
            this.lastDnsRefreshUtc = now;
            requestGeneration = this.networkGeneration;
            requestInterfaceId = this.snapshot.InterfaceId;
            signature = BuildDnsAddressSignature(this.snapshot.DnsServerDetails);
            previousDetails = CloneDnsServerDetails(this.snapshot.DnsServerDetails);
            localNetworkDegraded = this.snapshot.LocalNetworkDegraded;
            localNetworkDegradedReason = this.snapshot.LocalNetworkDegradedReason;
        }

        Task.Run(delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            DnsServerSnapshot[] result;
            try
            {
                result = ProbeDnsServers(addresses, previousDetails, localNetworkDegraded, localNetworkDegradedReason);
            }
            catch (Exception ex)
            {
                result = CreateDnsFailureSnapshots(addresses, ex, previousDetails, localNetworkDegraded, localNetworkDegradedReason);
            }

            stopwatch.Stop();
            bool committed = false;
            lock (this.sync)
            {
                this.dnsProbeRunning = false;
                if (this.disposed ||
                    requestGeneration != this.networkGeneration ||
                    !string.Equals(requestInterfaceId, this.snapshot.InterfaceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(signature, BuildDnsAddressSignature(this.snapshot.DnsServerDetails), StringComparison.OrdinalIgnoreCase))
                {
                    this.lastDnsRefreshUtc = DateTime.MinValue;
                    return;
                }

                this.snapshot.DnsServerDetails = result;
                this.lastDnsProbeSignature = signature;
                committed = true;
            }

            if (committed)
            {
                DnsServerStatus worstStatus = GetWorstDnsStatus(result);
                NetworkCheckHistoryLogger.LogCompleted(
                    "network_monitor",
                    "dns",
                    trigger,
                    BuildDnsHistorySummary(result),
                    result != null && result.Length > 0 && worstStatus == DnsServerStatus.Normal,
                    (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                    new Dictionary<string, object>
                    {
                        { "dns_count", result == null ? 0 : result.Length },
                        { "worst_status", worstStatus.ToString() },
                        { "status_detail", BuildDnsHistoryStatusDetail(result) },
                        { "abnormal_detail", BuildDnsHistoryAbnormalDetail(result) }
                    });
            }
        });
    }

    private static DnsServerSnapshot[] CreateDnsFailureSnapshots(
        string[] addresses,
        Exception ex,
        DnsServerSnapshot[] previousDetails,
        bool localNetworkDegraded,
        string localNetworkDegradedReason)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return new DnsServerSnapshot[0];
        }

        DnsServerSnapshot[] snapshots = new DnsServerSnapshot[addresses.Length];
        DateTime now = DateTime.Now;
        string reason = ex == null ? "检测失败" : ex.GetType().Name;
        for (int i = 0; i < snapshots.Length; i++)
        {
            snapshots[i] = new DnsServerSnapshot
            {
                Address = addresses[i] ?? string.Empty,
                Status = DnsServerStatus.Unavailable,
                Reason = reason,
                CheckedAtLocal = now,
                CheckedAtKnown = true
            };
            snapshots[i] = ApplyDnsUnavailabilityGate(
                snapshots[i],
                FindDnsSnapshot(previousDetails, addresses[i]),
                localNetworkDegraded,
                localNetworkDegradedReason);
        }

        return snapshots;
    }

    private static string[] GetDnsAddresses(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return new string[0];
        }

        List<string> addresses = new List<string>();
        for (int i = 0; i < details.Length; i++)
        {
            string address = details[i] == null ? string.Empty : details[i].Address;
            if (!string.IsNullOrWhiteSpace(address))
            {
                AddDistinct(addresses, address.Trim());
            }
        }

        return addresses.ToArray();
    }

    private static DnsServerSnapshot[] ProbeDnsServers(
        string[] addresses,
        DnsServerSnapshot[] previousDetails,
        bool localNetworkDegraded,
        string localNetworkDegradedReason)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return new DnsServerSnapshot[0];
        }

        DnsServerSnapshot[] result = new DnsServerSnapshot[addresses.Length];
        int nextIndex = -1;
        int workerCount = Math.Min(MaxDnsProbeConcurrency, addresses.Length);
        Task[] workers = new Task[workerCount];
        for (int worker = 0; worker < workerCount; worker++)
        {
            workers[worker] = Task.Run(delegate
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= addresses.Length)
                    {
                        return;
                    }

                    try
                    {
                        result[index] = ApplyDnsUnavailabilityGate(
                            ProbeDnsServer(addresses[index]),
                            FindDnsSnapshot(previousDetails, addresses[index]),
                            localNetworkDegraded,
                            localNetworkDegradedReason);
                    }
                    catch (Exception ex)
                    {
                        DnsServerSnapshot failure = new DnsServerSnapshot
                        {
                            Address = addresses[index] ?? string.Empty,
                            Status = DnsServerStatus.Unavailable,
                            Reason = ex.GetType().Name,
                            CheckedAtLocal = DateTime.Now,
                            CheckedAtKnown = true
                        };
                        result[index] = ApplyDnsUnavailabilityGate(
                            failure,
                            FindDnsSnapshot(previousDetails, addresses[index]),
                            localNetworkDegraded,
                            localNetworkDegradedReason);
                    }
                }
            });
        }

        Task.WaitAll(workers);
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == null)
            {
                DnsServerSnapshot failure = new DnsServerSnapshot
                {
                    Address = addresses[i] ?? string.Empty,
                    Status = DnsServerStatus.Unavailable,
                    Reason = "检测任务失败",
                    CheckedAtLocal = DateTime.Now,
                    CheckedAtKnown = true
                };
                result[i] = ApplyDnsUnavailabilityGate(
                    failure,
                    FindDnsSnapshot(previousDetails, addresses[i]),
                    localNetworkDegraded,
                    localNetworkDegradedReason);
            }
        }

        return result;
    }

    private static DnsServerSnapshot FindDnsSnapshot(DnsServerSnapshot[] details, string address)
    {
        if (details == null || string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        string normalized = address.Trim();
        for (int i = 0; i < details.Length; i++)
        {
            DnsServerSnapshot item = details[i];
            if (item != null && string.Equals(item.Address, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static DnsServerSnapshot ApplyDnsUnavailabilityGate(
        DnsServerSnapshot current,
        DnsServerSnapshot previous,
        bool localNetworkDegraded,
        string localNetworkDegradedReason)
    {
        if (current == null)
        {
            return new DnsServerSnapshot();
        }

        if (current.Status != DnsServerStatus.Unavailable)
        {
            current.FailureCount = 0;
            return current;
        }

        if (IsPermanentDnsUnavailableReason(current.Reason))
        {
            current.FailureCount = 1;
            return current;
        }

        int previousFailureCount = previous == null ? 0 : previous.FailureCount;
        if (previous != null && previous.Status == DnsServerStatus.Unavailable && previousFailureCount <= 0)
        {
            previousFailureCount = 1;
        }

        int failureCount = Math.Min(1000, previousFailureCount + 1);
        current.FailureCount = failureCount;

        // DNS UDP/TCP timeouts are also affected by packet loss. During a degraded
        // local link window we keep the DNS row yellow until the link itself recovers;
        // outside that window, a second consecutive failed round confirms grey.
        if (localNetworkDegraded)
        {
            current.Status = DnsServerStatus.Problem;
            current.Reason = FormatLocalNetworkDegradedReason(localNetworkDegradedReason);
            return current;
        }

        if (failureCount < 2)
        {
            current.Status = DnsServerStatus.Problem;
            current.Reason = EmptyFallback(current.Reason, "无响应") + "待确认";
        }

        return current;
    }

    private static bool IsPermanentDnsUnavailableReason(string reason)
    {
        return string.Equals(reason, "地址无效", StringComparison.Ordinal);
    }

    private static DnsServerSnapshot ProbeDnsServer(string address)
    {
        DnsServerSnapshot snapshot = new DnsServerSnapshot();
        snapshot.Address = address ?? string.Empty;
        snapshot.CheckedAtLocal = DateTime.Now;
        snapshot.CheckedAtKnown = true;

        IPAddress server;
        if (!IPAddress.TryParse(snapshot.Address, out server))
        {
            snapshot.Status = DnsServerStatus.Unavailable;
            snapshot.Reason = "地址无效";
            return snapshot;
        }

        DnsQueryResult known = QueryDns(server, DnsKnownDomain, DnsQueryTypeA, false);
        if (!known.Success)
        {
            DnsQueryResult tcpKnown = QueryDns(server, DnsKnownDomain, DnsQueryTypeA, true);
            snapshot.LatencyMs = Math.Max(0, tcpKnown.ElapsedMs);
            if (tcpKnown.Success && tcpKnown.RCode == 0 && tcpKnown.AnswerAddressCount > 0)
            {
                snapshot.Status = DnsServerStatus.Problem;
                snapshot.Reason = "UDP失败/TCP可用";
                return snapshot;
            }

            snapshot.Status = DnsServerStatus.Unavailable;
            snapshot.Reason = "无响应";
            return snapshot;
        }

        snapshot.LatencyMs = Math.Max(0, known.ElapsedMs);
        if (known.RCode != 0)
        {
            snapshot.Status = DnsServerStatus.Problem;
            snapshot.Reason = "返回 " + FormatDnsRCode(known.RCode);
            return snapshot;
        }

        if (known.AnswerAddressCount <= 0)
        {
            snapshot.Status = DnsServerStatus.Problem;
            snapshot.Reason = "无地址答案";
            return snapshot;
        }

        string nonexistent = CreateDnsNegativeProbeName();
        DnsQueryResult negative = QueryDns(server, nonexistent, DnsQueryTypeA, false);
        if (!negative.Success)
        {
            snapshot.Status = DnsServerStatus.Problem;
            snapshot.Reason = "NXDOMAIN验证失败";
            return snapshot;
        }

        if (negative.RCode == 3)
        {
            snapshot.Status = DnsServerStatus.Normal;
            snapshot.Reason = "正常";
            return snapshot;
        }

        if (negative.RCode == 0 && negative.AnswerAddressCount > 0)
        {
            DnsQueryResult confirmation = QueryDns(server, CreateDnsNegativeProbeName(), DnsQueryTypeA, false);
            if (confirmation.Success && confirmation.RCode == 0 && confirmation.AnswerAddressCount > 0)
            {
                snapshot.Status = DnsServerStatus.Hijacked;
                snapshot.Reason = "不存在域名被解析";
                return snapshot;
            }

            snapshot.Status = DnsServerStatus.Problem;
            snapshot.Reason = "NXDOMAIN一次异常";
            return snapshot;
        }

        snapshot.Status = DnsServerStatus.Problem;
        snapshot.Reason = "NXDOMAIN异常 " + FormatDnsRCode(negative.RCode);
        return snapshot;
    }

    private static string CreateDnsNegativeProbeName()
    {
        return "dca-" +
            DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture) +
            "-" +
            Guid.NewGuid().ToString("N").Substring(0, 8) +
            ".invalid";
    }

    private static DnsQueryResult QueryDns(IPAddress server, string name, ushort queryType, bool tcp)
    {
        ushort id = (ushort)(Environment.TickCount & 0xFFFF);
        byte[] query = BuildDnsQuery(id, name, queryType);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            byte[] response = tcp
                ? SendDnsTcp(server, query, DnsQueryTimeoutMs)
                : SendDnsUdp(server, query, DnsQueryTimeoutMs);
            stopwatch.Stop();
            return ParseDnsResponse(response, id, (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DnsQueryResult
            {
                Success = false,
                Error = ex.GetType().Name,
                ElapsedMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)
            };
        }
    }

    private static byte[] SendDnsUdp(IPAddress server, byte[] query, int timeoutMs)
    {
        using (UdpClient client = new UdpClient(server.AddressFamily))
        {
            client.Client.ReceiveTimeout = timeoutMs;
            client.Client.SendTimeout = timeoutMs;
            client.Connect(new IPEndPoint(server, 53));
            client.Send(query, query.Length);
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            return client.Receive(ref remote);
        }
    }

    private static byte[] SendDnsTcp(IPAddress server, byte[] query, int timeoutMs)
    {
        DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs));
        using (TcpClient client = new TcpClient(server.AddressFamily))
        {
            IAsyncResult connect = client.BeginConnect(server, 53, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(GetRemainingTimeoutMs(deadlineUtc)))
            {
                throw new TimeoutException("DNS TCP connect timeout");
            }

            client.EndConnect(connect);
            using (NetworkStream stream = client.GetStream())
            {
                byte[] length = new byte[] { (byte)(query.Length >> 8), (byte)(query.Length & 0xFF) };
                WriteWithDeadline(stream, length, 0, length.Length, deadlineUtc);
                WriteWithDeadline(stream, query, 0, query.Length, deadlineUtc);
                byte[] header = ReadExactWithDeadline(stream, 2, deadlineUtc);
                int responseLength = (header[0] << 8) | header[1];
                if (responseLength <= 0 || responseLength > 4096)
                {
                    throw new InvalidOperationException("Invalid DNS TCP response length");
                }

                return ReadExactWithDeadline(stream, responseLength, deadlineUtc);
            }
        }
    }

    private static void WriteWithDeadline(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        DateTime deadlineUtc)
    {
        int remainingMs = GetRemainingTimeoutMs(deadlineUtc);
        if (stream.CanTimeout)
        {
            stream.WriteTimeout = remainingMs;
        }

        stream.Write(buffer, offset, count);
        GetRemainingTimeoutMs(deadlineUtc);
    }

    private static byte[] ReadExactWithDeadline(Stream stream, int count, DateTime deadlineUtc)
    {
        if (stream == null)
        {
            throw new ArgumentNullException("stream");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException("count");
        }

        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int remainingMs = GetRemainingTimeoutMs(deadlineUtc);
            if (stream.CanTimeout)
            {
                stream.ReadTimeout = remainingMs;
            }

            int read = stream.Read(buffer, offset, count - offset);
            GetRemainingTimeoutMs(deadlineUtc);
            if (read <= 0)
            {
                throw new InvalidOperationException("Unexpected DNS TCP EOF");
            }

            offset += read;
        }

        return buffer;
    }

    private static string ReadBoundedTextWithDeadline(
        Stream stream,
        Encoding encoding,
        int maxBytes,
        DateTime deadlineUtc,
        out bool exceeded)
    {
        if (stream == null)
        {
            throw new ArgumentNullException("stream");
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException("maxBytes");
        }

        encoding = encoding ?? Encoding.UTF8;
        exceeded = false;
        byte[] chunk = new byte[Math.Max(1, Math.Min(1024, maxBytes + 1))];
        using (MemoryStream output = new MemoryStream(Math.Max(0, maxBytes)))
        {
            while (true)
            {
                int remainingMs = GetRemainingTimeoutMs(deadlineUtc);
                if (stream.CanTimeout)
                {
                    stream.ReadTimeout = remainingMs;
                }

                int remainingCapacityWithSentinel = Math.Max(1, maxBytes + 1 - (int)output.Length);
                int requested = Math.Min(chunk.Length, remainingCapacityWithSentinel);
                int read = stream.Read(chunk, 0, requested);
                GetRemainingTimeoutMs(deadlineUtc);
                if (read <= 0)
                {
                    break;
                }

                int writable = Math.Min(read, Math.Max(0, maxBytes - (int)output.Length));
                if (writable > 0)
                {
                    output.Write(chunk, 0, writable);
                }

                if (read > writable || output.Length >= maxBytes)
                {
                    if (read > writable)
                    {
                        exceeded = true;
                        break;
                    }

                    // Read one sentinel byte so an exactly-at-limit response is not mistaken for
                    // an oversized one.
                    remainingMs = GetRemainingTimeoutMs(deadlineUtc);
                    if (stream.CanTimeout)
                    {
                        stream.ReadTimeout = remainingMs;
                    }

                    int sentinel = stream.ReadByte();
                    GetRemainingTimeoutMs(deadlineUtc);
                    exceeded = sentinel >= 0;
                    break;
                }
            }

            return encoding.GetString(output.ToArray());
        }
    }

    private static int GetRemainingTimeoutMs(DateTime deadlineUtc)
    {
        double remaining = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
        if (remaining <= 0.0)
        {
            throw new TimeoutException("Network read deadline exceeded");
        }

        return Math.Max(1, (int)Math.Min(int.MaxValue, Math.Ceiling(remaining)));
    }

    private static byte[] BuildDnsQuery(ushort id, string name, ushort queryType)
    {
        List<byte> bytes = new List<byte>();
        WriteUInt16(bytes, id);
        WriteUInt16(bytes, 0x0100);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);

        string[] labels = (name ?? string.Empty).Split('.');
        for (int i = 0; i < labels.Length; i++)
        {
            string label = labels[i];
            if (label.Length == 0)
            {
                continue;
            }

            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length > 63)
            {
                throw new ArgumentException("DNS label too long");
            }

            bytes.Add((byte)labelBytes.Length);
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0);
        WriteUInt16(bytes, queryType);
        WriteUInt16(bytes, 1);
        return bytes.ToArray();
    }

    private static DnsQueryResult ParseDnsResponse(byte[] response, ushort expectedId, int elapsedMs)
    {
        if (response == null || response.Length < 12)
        {
            return DnsQueryResult.CreateFailure("ShortResponse", elapsedMs);
        }

        ushort id = ReadUInt16(response, 0);
        if (id != expectedId)
        {
            return DnsQueryResult.CreateFailure("MismatchedId", elapsedMs);
        }

        ushort flags = ReadUInt16(response, 2);
        int qdCount = ReadUInt16(response, 4);
        int anCount = ReadUInt16(response, 6);
        int rcode = flags & 0x000F;
        int offset = 12;
        try
        {
            for (int i = 0; i < qdCount; i++)
            {
                offset = SkipDnsName(response, offset);
                offset += 4;
                if (offset > response.Length)
                {
                    return DnsQueryResult.CreateFailure("QuestionOverflow", elapsedMs);
                }
            }

            int addressAnswers = 0;
            for (int i = 0; i < anCount; i++)
            {
                offset = SkipDnsName(response, offset);
                if (offset + 10 > response.Length)
                {
                    return DnsQueryResult.CreateFailure("AnswerOverflow", elapsedMs);
                }

                ushort type = ReadUInt16(response, offset);
                offset += 2;
                offset += 2; // class
                offset += 4; // ttl
                int rdLength = ReadUInt16(response, offset);
                offset += 2;
                if (offset + rdLength > response.Length)
                {
                    return DnsQueryResult.CreateFailure("RDataOverflow", elapsedMs);
                }

                if ((type == DnsQueryTypeA && rdLength == 4) || (type == DnsQueryTypeAaaa && rdLength == 16))
                {
                    addressAnswers++;
                }

                offset += rdLength;
            }

            return new DnsQueryResult
            {
                Success = true,
                RCode = rcode,
                AnswerAddressCount = addressAnswers,
                ElapsedMs = elapsedMs
            };
        }
        catch (Exception ex)
        {
            return DnsQueryResult.CreateFailure(ex.GetType().Name, elapsedMs);
        }
    }

    private static int SkipDnsName(byte[] packet, int offset)
    {
        int jumps = 0;
        while (offset < packet.Length)
        {
            int length = packet[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length)
                {
                    throw new InvalidOperationException("Bad compression pointer");
                }

                jumps++;
                if (jumps > 8)
                {
                    throw new InvalidOperationException("Compression pointer loop");
                }

                return offset + 2;
            }

            if ((length & 0xC0) != 0)
            {
                throw new InvalidOperationException("Unsupported DNS label");
            }

            offset += length + 1;
        }

        throw new InvalidOperationException("DNS name overflow");
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static void WriteUInt16(List<byte> buffer, int value)
    {
        buffer.Add((byte)((value >> 8) & 0xFF));
        buffer.Add((byte)(value & 0xFF));
    }

    private static string FormatDnsRCode(int rcode)
    {
        switch (rcode)
        {
            case 0:
                return "NOERROR";
            case 1:
                return "FORMERR";
            case 2:
                return "SERVFAIL";
            case 3:
                return "NXDOMAIN";
            case 4:
                return "NOTIMP";
            case 5:
                return "REFUSED";
            default:
                return "RCODE " + rcode.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void StartConnectivityRefresh(DateTime now, string trigger)
    {
        long requestGeneration;
        string requestInterfaceId;
        lock (this.sync)
        {
            if (this.disposed || this.connectivityRequestRunning)
            {
                return;
            }

            this.connectivityRequestRunning = true;
            this.lastConnectivityRefreshUtc = now;
            requestGeneration = this.networkGeneration;
            requestInterfaceId = this.snapshot.InterfaceId;
        }

        // Ping and captive-portal checks must never block the WinForms UI thread.
        // The running flag also prevents slow offline probes from accumulating.
        Task.Run(delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            ConnectivityResult result = MeasureConnectivity(ConnectivityTarget);
            stopwatch.Stop();
            bool committed = false;
            lock (this.sync)
            {
                this.connectivityRequestRunning = false;
                if (this.disposed ||
                    requestGeneration != this.networkGeneration ||
                    !string.Equals(requestInterfaceId, this.snapshot.InterfaceId, StringComparison.OrdinalIgnoreCase))
                {
                    this.lastConnectivityRefreshUtc = DateTime.MinValue;
                    return;
                }

                this.snapshot.ConnectivityKnown = true;
                this.snapshot.ConnectivityOnline = result.Online;
                this.snapshot.AccessState = result.AccessState;
                this.snapshot.AccessReason = result.AccessReason;
                this.snapshot.ConnectivityTarget = ConnectivityTarget;
                this.snapshot.LatencyMs = result.LatencyMs;
                this.snapshot.JitterMs = result.JitterMs;
                this.snapshot.PacketLossPercent = result.PacketLossPercent;
                this.snapshot.LocalNetworkDegraded = result.LocalNetworkDegraded;
                this.snapshot.LocalNetworkDegradedReason = result.LocalNetworkDegradedReason;
                if (!result.Online)
                {
                    this.snapshot.LastError = string.IsNullOrEmpty(result.AccessReason) ? "Connectivity failed" : result.AccessReason;
                }
                else
                {
                    this.snapshot.LastError = string.Empty;
                }
                committed = true;
            }

            if (committed)
            {
                NetworkCheckHistoryLogger.LogCompleted(
                    "network_monitor",
                    "connectivity",
                    trigger,
                    FormatConnectivityHistoryResult(result),
                    result.Online,
                    (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds),
                    new Dictionary<string, object>
                    {
                        { "access_state", result.AccessState.ToString() },
                        { "latency_ms", Math.Round(result.LatencyMs, 1) },
                        { "jitter_ms", Math.Round(result.JitterMs, 1) },
                        { "packet_loss_percent", result.PacketLossPercent },
                        { "local_network_degraded", result.LocalNetworkDegraded }
                    });
            }
        });
    }

    private static string BuildDnsHistorySummary(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return "无DNS";
        }

        int normal = 0;
        int problem = 0;
        int hijacked = 0;
        int unavailable = 0;
        int unknown = 0;
        for (int i = 0; i < details.Length; i++)
        {
            DnsServerStatus status = details[i] == null ? DnsServerStatus.Unknown : details[i].Status;
            switch (status)
            {
                case DnsServerStatus.Normal:
                    normal++;
                    break;
                case DnsServerStatus.Problem:
                    problem++;
                    break;
                case DnsServerStatus.Hijacked:
                    hijacked++;
                    break;
                case DnsServerStatus.Unavailable:
                    unavailable++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "正常{0} 问题{1} 劫持{2} 不可用{3} 未知{4}",
            normal,
            problem,
            hijacked,
            unavailable,
            unknown) + BuildDnsHistoryAbnormalSummarySuffix(details);
    }

    private static string BuildDnsHistoryAbnormalSummarySuffix(DnsServerSnapshot[] details)
    {
        string abnormal = BuildDnsHistoryAbnormalDetail(details);
        return string.Equals(abnormal, "none", StringComparison.Ordinal)
            ? string.Empty
            : " 异常:" + abnormal;
    }

    private static string BuildDnsHistoryStatusDetail(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return "none";
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < details.Length; i++)
        {
            DnsServerSnapshot item = details[i];
            if (item == null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(item.Status.ToString());
            string reason = NormalizeDnsHistoryReason(item);
            if (reason.Length > 0)
            {
                builder.Append(":");
                builder.Append(reason);
            }

            if (item.LatencyMs > 0)
            {
                builder.Append("@");
                builder.Append(item.LatencyMs.ToString(CultureInfo.InvariantCulture));
                builder.Append("ms");
            }

            if (item.FailureCount > 0)
            {
                builder.Append("/fail");
                builder.Append(item.FailureCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        return builder.Length == 0 ? "none" : TrimHistoryText(builder.ToString(), 220);
    }

    private static string BuildDnsHistoryAbnormalDetail(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return "none";
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < details.Length; i++)
        {
            DnsServerSnapshot item = details[i];
            if (item == null ||
                (item.Status != DnsServerStatus.Problem &&
                 item.Status != DnsServerStatus.Hijacked &&
                 item.Status != DnsServerStatus.Unavailable))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(item.Status.ToString());
            string reason = NormalizeDnsHistoryReason(item);
            if (reason.Length > 0)
            {
                builder.Append(":");
                builder.Append(reason);
            }
        }

        return builder.Length == 0 ? "none" : TrimHistoryText(builder.ToString(), 160);
    }

    private static string NormalizeDnsHistoryReason(DnsServerSnapshot item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        string reason = string.IsNullOrWhiteSpace(item.Reason) ? item.Status.ToString() : item.Reason.Trim();
        StringBuilder builder = new StringBuilder(reason.Length);
        for (int i = 0; i < reason.Length; i++)
        {
            char ch = reason[i];
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return TrimHistoryText(builder.ToString(), 48);
    }

    private static string TrimHistoryText(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value.Substring(0, Math.Max(1, maxLength - 1)) + "…";
    }

    private static string FormatConnectivityHistoryResult(ConnectivityResult result)
    {
        string prefix = result.Online ? "在线" : "离线";
        string reason = EmptyFallback(result.AccessReason, result.AccessState.ToString());
        return prefix + " " + reason;
    }

    private static void ApplyNetworkStatusTestMode(NetworkMonitorSnapshot snapshot, WidgetSettings settings)
    {
        if (snapshot == null || settings == null || settings.NetworkStatusTestMode == NetworkStatusTestMode.Off)
        {
            return;
        }

        // Test modes are applied to the clone returned to UI. Never write them into the
        // reader snapshot or they would alter real scheduling and GFW/public-IP requests.
        snapshot.ConnectivityKnown = true;
        snapshot.AccessReason = "测试模式";
        snapshot.LocalNetworkDegraded = false;
        snapshot.LocalNetworkDegradedReason = string.Empty;
        switch (settings.NetworkStatusTestMode)
        {
            case NetworkStatusTestMode.Online:
                snapshot.Connected = true;
                snapshot.InterfaceKnown = true;
                snapshot.ConnectivityOnline = true;
                snapshot.AccessState = NetworkAccessState.Online;
                snapshot.PacketLossPercent = 0;
                if (snapshot.LatencyMs <= 0.0)
                {
                    snapshot.LatencyMs = 8.0;
                }

                return;

            case NetworkStatusTestMode.Offline:
                snapshot.Connected = true;
                snapshot.InterfaceKnown = true;
                snapshot.ConnectivityOnline = false;
                snapshot.AccessState = NetworkAccessState.Offline;
                snapshot.PacketLossPercent = 100;
                snapshot.LatencyMs = 0.0;
                snapshot.JitterMs = 0.0;
                return;

            case NetworkStatusTestMode.AdapterMissing:
                snapshot.Connected = false;
                snapshot.InterfaceKnown = false;
                snapshot.InterfaceName = "Network";
                snapshot.InterfaceDescription = string.Empty;
                snapshot.InterfaceType = "--";
                snapshot.IPv4 = "--";
                snapshot.IPv6 = "--";
                snapshot.DnsServers = "--";
                snapshot.IsWifi = false;
                snapshot.PublicIp = "--";
                snapshot.PublicIpKnown = false;
                snapshot.PublicIpRefreshing = false;
                snapshot.ConnectivityOnline = false;
                snapshot.AccessState = NetworkAccessState.AdapterMissing;
                snapshot.PacketLossPercent = 100;
                snapshot.LatencyMs = 0.0;
                snapshot.JitterMs = 0.0;
                return;

            case NetworkStatusTestMode.NeedsValidation:
                snapshot.Connected = true;
                snapshot.InterfaceKnown = true;
                snapshot.ConnectivityOnline = false;
                snapshot.AccessState = NetworkAccessState.NeedsValidation;
                snapshot.PacketLossPercent = Math.Max(snapshot.PacketLossPercent, 100);
                snapshot.LatencyMs = 0.0;
                snapshot.JitterMs = 0.0;
                return;
        }
    }

    private static void ApplyCloudEndpointTestMode(NetworkMonitorSnapshot snapshot, WidgetSettings settings)
    {
        if (snapshot == null || settings == null || settings.CloudEndpointTestSeed <= 0)
        {
            return;
        }

        if (settings.NetworkStatusTestMode == NetworkStatusTestMode.Off)
        {
            snapshot.Connected = true;
            snapshot.InterfaceKnown = true;
            snapshot.ConnectivityKnown = true;
            snapshot.ConnectivityOnline = true;
            snapshot.AccessState = NetworkAccessState.Online;
            snapshot.AccessReason = "云服务测试";
        }

        GfwProbeSnapshot gfw = snapshot.GfwProbe == null ? new GfwProbeSnapshot() : snapshot.GfwProbe.Clone();
        gfw.Enabled = true;
        gfw.Running = false;
        gfw.Status = GfwProbeStatus.Normal;
        gfw.Detail = "测试";
        gfw.Reason = "云服务随机状态测试";
        gfw.CheckedAtLocal = DateTime.Now;
        gfw.CheckedAtKnown = true;
        gfw.CloudEndpoints = BuildCloudEndpointTestSnapshots(settings.CloudEndpointTestSeed);
        snapshot.GfwProbe = gfw;
    }

    private static CloudEndpointSnapshot[] BuildCloudEndpointTestSnapshots(int seed)
    {
        CloudEndpointSnapshot[] snapshots = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Normal);
        CloudEndpointStatus[] statuses = new CloudEndpointStatus[]
        {
            CloudEndpointStatus.Normal,
            CloudEndpointStatus.Slow,
            CloudEndpointStatus.Down,
            CloudEndpointStatus.Abnormal
        };
        Random random = new Random(seed);
        DateTime checkedAt = DateTime.Now;
        for (int i = 0; i < snapshots.Length; i++)
        {
            CloudEndpointStatus status = statuses[random.Next(statuses.Length)];
            snapshots[i].Status = status;
            snapshots[i].CheckedAtLocal = checkedAt;
            snapshots[i].CheckedAtKnown = true;
            snapshots[i].LatencyMs = status == CloudEndpointStatus.Normal
                ? random.Next(30, 260)
                : (status == CloudEndpointStatus.Slow ? random.Next(1000, 2200) : 0);
            snapshots[i].Reason = "测试随机 " + status.ToString();
            snapshots[i].AlertReason = GetCloudEndpointTestAlertReason(status, random);
        }

        return snapshots;
    }

    private static string GetCloudEndpointTestAlertReason(CloudEndpointStatus status, Random random)
    {
        if (status == CloudEndpointStatus.Down)
        {
            string[] reasons = new string[] { "DNS失败", "TCP失败", "TLS失败", "请求超时" };
            return reasons[random.Next(reasons.Length)];
        }

        if (status == CloudEndpointStatus.Abnormal)
        {
            string[] reasons = new string[] { "拒绝访问", "访问限流", "服务异常", "官方降级" };
            return reasons[random.Next(reasons.Length)];
        }

        if (status == CloudEndpointStatus.Slow)
        {
            return "延迟过高";
        }

        return string.Empty;
    }

    private static string FetchText(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = HttpTimeoutMs;
        request.ReadWriteTimeout = HttpTimeoutMs;
        request.UserAgent = ProductIdentity.UserAgent;
        BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
            request,
            BoundedHttpTextReader.TinyProbeMaxBytes,
            HttpTimeoutMs,
            CancellationToken.None);
        if (!response.Success)
        {
            throw new InvalidOperationException("Public IP response failed: " + response.ErrorCode);
        }

        return response.Content;
    }

    private static bool TryNormalizePublicIpv4Response(string response, out string ipv4)
    {
        ipv4 = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        IPAddress address;
        string value = response.Trim();
        if (!IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        ipv4 = address.ToString();
        return true;
    }

    private static ConnectivityResult MeasureConnectivity(string target)
    {
        // Portal HTTP runs in parallel with sequential Ping. Sequential Ping preserves
        // meaningful adjacent samples for jitter while avoiding additive HTTP latency.
        Task<CaptivePortalResult> portalTask = Task.Run(delegate { return CheckCaptivePortal(); });
        PingMeasurement ping = MeasurePing(target);
        List<long> roundTrips = ping.RoundTrips ?? new List<long>();

        ConnectivityResult result = new ConnectivityResult();
        result.PacketLossPercent = Math.Max(0, Math.Min(100, (int)Math.Round(ping.Failures * 100.0 / PingCount)));
        CaptivePortalResult portal = portalTask.Result;
        if (portal.NeedsValidation)
        {
            result.Online = false;
            result.AccessState = NetworkAccessState.NeedsValidation;
            result.AccessReason = portal.Reason;
        }
        else if (portal.Online || roundTrips.Count > 0)
        {
            result.Online = true;
            result.AccessState = NetworkAccessState.Online;
            result.AccessReason = portal.Online ? "HTTP验证通过" : "Ping可达";
        }
        else
        {
            result.Online = false;
            result.AccessState = NetworkAccessState.Offline;
            result.AccessReason = string.IsNullOrEmpty(portal.Reason) ? "Ping和HTTP均失败" : portal.Reason;
        }

        ApplyPingStats(ref result, roundTrips);
        ApplyLocalNetworkQuality(ref result);
        return result;
    }

    private static PingMeasurement MeasurePing(string target)
    {
        PingMeasurement result = new PingMeasurement();
        result.RoundTrips = new List<long>();
        using (Ping ping = new Ping())
        {
            for (int i = 0; i < PingCount; i++)
            {
                try
                {
                    PingReply reply = ping.Send(target, PingTimeoutMs);
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        result.RoundTrips.Add(reply.RoundtripTime);
                    }
                    else
                    {
                        result.Failures++;
                    }
                }
                catch
                {
                    result.Failures++;
                }
            }
        }

        return result;
    }

    private static void ApplyPingStats(ref ConnectivityResult result, List<long> roundTrips)
    {
        // ConnectivityResult is a struct; ref is required or latency/jitter writes are lost.
        if (roundTrips == null || roundTrips.Count == 0)
        {
            return;
        }

        double sum = 0.0;
        for (int i = 0; i < roundTrips.Count; i++)
        {
            sum += roundTrips[i];
        }

        result.LatencyMs = sum / roundTrips.Count;
        if (roundTrips.Count <= 1)
        {
            return;
        }

        double jitterSum = 0.0;
        for (int i = 1; i < roundTrips.Count; i++)
        {
            jitterSum += Math.Abs(roundTrips[i] - roundTrips[i - 1]);
        }

        result.JitterMs = jitterSum / (roundTrips.Count - 1);
    }

    private static void ApplyLocalNetworkQuality(ref ConnectivityResult result)
    {
        result.LocalNetworkDegraded = false;
        result.LocalNetworkDegradedReason = string.Empty;
        if (result.AccessState != NetworkAccessState.Online)
        {
            return;
        }

        // This flag does not change Online/Offline. It only tells higher level probes
        // that a remote failure may be caused by local packet loss or extreme latency.
        if (result.PacketLossPercent >= DegradedPacketLossPercent)
        {
            result.LocalNetworkDegraded = true;
            result.LocalNetworkDegradedReason = "本地丢包高 " + result.PacketLossPercent.ToString(CultureInfo.InvariantCulture) + "%";
            return;
        }

        if (result.JitterMs >= DegradedJitterMs)
        {
            result.LocalNetworkDegraded = true;
            result.LocalNetworkDegradedReason = "本地抖动高 " + Math.Round(result.JitterMs).ToString(CultureInfo.InvariantCulture) + "ms";
            return;
        }

        if (result.LatencyMs >= DegradedLatencyMs)
        {
            result.LocalNetworkDegraded = true;
            result.LocalNetworkDegradedReason = "本地延迟高 " + Math.Round(result.LatencyMs).ToString(CultureInfo.InvariantCulture) + "ms";
        }
    }

    private static string FormatLocalNetworkDegradedReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "本地网络不稳定" : reason.Trim();
    }

    internal static string BuildCaptivePortalRedirectReason(string location)
    {
        const string shortReason = "门户重定向";
        Uri redirectUri;
        if (string.IsNullOrWhiteSpace(location) ||
            !Uri.TryCreate(location.Trim(), UriKind.Absolute, out redirectUri) ||
            string.IsNullOrWhiteSpace(redirectUri.Host))
        {
            return shortReason;
        }

        string host = redirectUri.Host.Trim();
        if (host.Length > 48)
        {
            host = host.Substring(0, 48);
        }

        return shortReason + " → " + host;
    }

    private static CaptivePortalResult CheckCaptivePortal()
    {
        CaptivePortalResult result = new CaptivePortalResult();
        DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(HttpTimeoutMs);
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(CaptivePortalTestUrl);
            request.Method = "GET";
            int remainingMs = GetRemainingTimeoutMs(deadlineUtc);
            request.Timeout = remainingMs;
            request.ReadWriteTimeout = remainingMs;
            request.AllowAutoRedirect = false;
            request.UserAgent = ProductIdentity.UserAgent;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                int status = (int)response.StatusCode;
                string location = response.Headers["Location"];
                if (status >= 300 && status < 400)
                {
                    result.NeedsValidation = true;
                    result.Reason = BuildCaptivePortalRedirectReason(location);
                    return result;
                }

                if (status == 401 || status == 403 || status == 511)
                {
                    result.NeedsValidation = true;
                    result.Reason = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
                    return result;
                }

                string text = string.Empty;
                bool bodyExceeded;
                using (Stream stream = response.GetResponseStream())
                {
                    text = ReadBoundedTextWithDeadline(
                        stream,
                        Encoding.UTF8,
                        CaptivePortalBodyLimitBytes,
                        deadlineUtc,
                        out bodyExceeded);
                }

                if (bodyExceeded)
                {
                    result.NeedsValidation = true;
                    result.Reason = "门户内容替换";
                    return result;
                }

                if (status == 200 && string.Equals((text ?? string.Empty).Trim(), CaptivePortalExpectedText, StringComparison.Ordinal))
                {
                    result.Online = true;
                    result.Reason = "NCSI通过";
                    return result;
                }

                if (status == 200)
                {
                    result.NeedsValidation = true;
                    result.Reason = "门户内容替换";
                    return result;
                }

                result.Reason = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
                return result;
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    int status = (int)response.StatusCode;
                    if ((status >= 300 && status < 400) || status == 401 || status == 403 || status == 511)
                    {
                        result.NeedsValidation = true;
                        result.Reason = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
                        return result;
                    }

                    result.Reason = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
                    return result;
                }
            }

            result.Reason = ex.Status.ToString();
            return result;
        }
        catch (Exception ex)
        {
            result.Reason = ex.GetType().Name;
            return result;
        }
    }

    private static string FormatInterfaceType(NetworkInterfaceType type)
    {
        if (type == NetworkInterfaceType.Wireless80211)
        {
            return "Wi-Fi";
        }

        if (type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet)
        {
            return "Ethernet";
        }

        if (type == NetworkInterfaceType.Ppp)
        {
            return "PPP";
        }

        return type.ToString();
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        if (address == null)
        {
            return "--";
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes == null || bytes.Length == 0)
        {
            return "--";
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(":");
            }

            builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.networkGeneration++;
        }

        this.pathPingProbeReader.Dispose();
        this.fixedPingProbeReader.Dispose();

        // NetworkChange events are static and would otherwise keep the reader/window alive.
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    internal static void RunRollingPingSelfTest()
    {
        byte[] oversizedBody = new byte[CaptivePortalBodyLimitBytes + 1];
        for (int i = 0; i < oversizedBody.Length; i++)
        {
            oversizedBody[i] = (byte)'A';
        }

        bool bodyExceeded;
        string boundedBody;
        using (MemoryStream oversizedStream = new MemoryStream(oversizedBody, false))
        {
            boundedBody = ReadBoundedTextWithDeadline(
                oversizedStream,
                Encoding.UTF8,
                CaptivePortalBodyLimitBytes,
                DateTime.UtcNow.AddSeconds(1),
                out bodyExceeded);
        }

        if (!bodyExceeded || Encoding.UTF8.GetByteCount(boundedBody) != CaptivePortalBodyLimitBytes)
        {
            throw new InvalidOperationException("Network read self-test: captive portal body limit failed.");
        }

        Stopwatch slowReadStopwatch = Stopwatch.StartNew();
        bool slowReadTimedOut = false;
        try
        {
            using (SlowChunkStream slowStream = new SlowChunkStream(new byte[] { 1, 2, 3, 4 }, 50))
            {
                ReadExactWithDeadline(slowStream, 4, DateTime.UtcNow.AddMilliseconds(25));
            }
        }
        catch (TimeoutException)
        {
            slowReadTimedOut = true;
        }
        slowReadStopwatch.Stop();
        if (!slowReadTimedOut || slowReadStopwatch.ElapsedMilliseconds >= 2000)
        {
            throw new InvalidOperationException("Network read self-test: slow chunk stream ignored the absolute deadline.");
        }

        string redirectReason = BuildCaptivePortalRedirectReason("https://login.example.com/connect");
        if (!string.Equals(redirectReason, "门户重定向 → login.example.com", StringComparison.Ordinal) ||
            !string.Equals(BuildCaptivePortalRedirectReason(string.Empty), "门户重定向", StringComparison.Ordinal) ||
            !string.Equals(BuildCaptivePortalRedirectReason("/relative/login"), "门户重定向", StringComparison.Ordinal) ||
            !string.Equals(BuildCaptivePortalRedirectReason("not a uri"), "门户重定向", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Captive portal self-test: redirect reason normalization failed.");
        }

        string longHostReason = BuildCaptivePortalRedirectReason(
            "https://" + new string('a', 60) + ".example.com/login");
        const string redirectPrefix = "门户重定向 → ";
        if (!longHostReason.StartsWith(redirectPrefix, StringComparison.Ordinal) ||
            longHostReason.Substring(redirectPrefix.Length).Length != 48)
        {
            throw new InvalidOperationException("Captive portal self-test: redirect host must be capped at 48 characters.");
        }

        string publicIpv4;
        if (!TryNormalizePublicIpv4Response("203.0.113.10", out publicIpv4) ||
            !string.Equals(publicIpv4, "203.0.113.10", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Public IP self-test: IPv4 normalization failed.");
        }

        if (TryNormalizePublicIpv4Response("2001:db8::1", out publicIpv4) ||
            TryNormalizePublicIpv4Response("not an ip", out publicIpv4))
        {
            throw new InvalidOperationException("Public IP self-test: non-IPv4 response must be rejected.");
        }

        DnsServerSnapshot[] dnsHistoryFixture = new DnsServerSnapshot[]
        {
            new DnsServerSnapshot { Address = "1.1.1.1", Status = DnsServerStatus.Normal, Reason = "正常", LatencyMs = 12 },
            new DnsServerSnapshot { Address = "8.8.8.8", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用", FailureCount = 1 },
            new DnsServerSnapshot { Address = "2001:4860:4860::8888", Status = DnsServerStatus.Unavailable, Reason = "无响应", FailureCount = 2 }
        };
        string dnsSummary = BuildDnsHistorySummary(dnsHistoryFixture);
        string dnsStatusDetail = BuildDnsHistoryStatusDetail(dnsHistoryFixture);
        string dnsAbnormalDetail = BuildDnsHistoryAbnormalDetail(dnsHistoryFixture);
        if (dnsSummary.IndexOf("异常:Problem:UDP失败/TCP可用", StringComparison.Ordinal) < 0 ||
            dnsStatusDetail.IndexOf("Problem:UDP失败/TCP可用", StringComparison.Ordinal) < 0 ||
            dnsAbnormalDetail.IndexOf("Unavailable:无响应", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("DNS history self-test: concrete DNS reasons must be logged.");
        }

        if (dnsSummary.IndexOf("1.1.1.1", StringComparison.Ordinal) >= 0 ||
            dnsStatusDetail.IndexOf("8.8.8.8", StringComparison.Ordinal) >= 0 ||
            dnsAbnormalDetail.IndexOf("2001:4860", StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("DNS history self-test: DNS server addresses must stay out of history detail.");
        }

        PingGroupStats gateway = PingSampleWindow.BuildStatsForTest("gateway", new bool[] { true, true, true, true, true, true, true, true, true, true }, new int[] { 2, 3, 2, 2, 3, 2, 2, 3, 2, 2 });
        PingGroupStats publicLoss = PingSampleWindow.BuildStatsForTest("public", new bool[] { true, true, true, true, true, true, true, true, true, false }, new int[] { 40, 41, 42, 39, 40, 41, 40, 42, 41, 0 });
        PingRollingSnapshot wanLoss = BuildRollingPingSnapshot(NetworkAccessState.Online, false, gateway, publicLoss);
        if (!wanLoss.StatsReady || Math.Abs(wanLoss.LossPercent - 10.0) > 0.01 || wanLoss.Diagnosis != PingPathDiagnosis.WanLoss)
        {
            throw new InvalidOperationException("Rolling ping self-test: WAN loss classification failed.");
        }

        PingGroupStats warming = PingSampleWindow.BuildStatsForTest("public", new bool[] { true, false, true }, new int[] { 20, 0, 21 });
        PingRollingSnapshot cold = BuildRollingPingSnapshot(NetworkAccessState.Online, false, gateway, warming);
        if (cold.StatsReady || cold.Diagnosis != PingPathDiagnosis.None)
        {
            throw new InvalidOperationException("Rolling ping self-test: warm-up state failed.");
        }

        string gfwGateReason;
        if (TryBuildGfwLocalNetworkGate(cold, false, out gfwGateReason))
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW gate must ignore warm-up samples.");
        }

        if (TryBuildGfwLocalNetworkGate(wanLoss, false, out gfwGateReason))
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW gate must wait for confirmed WAN loss.");
        }

        if (!TryBuildGfwLocalNetworkGate(wanLoss, true, out gfwGateReason) ||
            gfwGateReason.IndexOf("确认丢包", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW gate confirmed WAN loss failed.");
        }

        PingGroupStats publicClean = PingSampleWindow.BuildStatsForTest("public", new bool[] { true, true, true, true, true, true, true, true, true, true }, new int[] { 40, 41, 42, 39, 40, 41, 40, 42, 41, 40 });
        PingGroupStats unstableGateway = PingSampleWindow.BuildStatsForTest("gateway", new bool[] { true, true, true, true, true, true, true, true, true, false }, new int[] { 2, 2, 2, 3, 2, 2, 2, 2, 3, 0 });
        PingRollingSnapshot localOnlyLoss = BuildRollingPingSnapshot(NetworkAccessState.Online, false, unstableGateway, publicClean);
        if (localOnlyLoss.Diagnosis != PingPathDiagnosis.LocalLoss || Math.Abs(localOnlyLoss.LossPercent) > 0.01)
        {
            throw new InvalidOperationException("Rolling ping self-test: local-only loss fixture failed.");
        }

        if (TryBuildGfwLocalNetworkGate(localOnlyLoss, true, out gfwGateReason))
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW gate must ignore local-only loss when active loss is below threshold.");
        }

        PingGroupStats slowGateway = PingSampleWindow.BuildStatsForTest("gateway", new bool[] { true, true, true, true, true, true, true, true, true, true }, new int[] { 50, 55, 52, 54, 56, 53, 55, 52, 54, 55 });
        PingRollingSnapshot localLatency = BuildRollingPingSnapshot(NetworkAccessState.Online, false, slowGateway, publicClean);
        if (localLatency.Diagnosis != PingPathDiagnosis.LocalLatency)
        {
            throw new InvalidOperationException("Rolling ping self-test: local latency fixture failed.");
        }

        if (TryBuildGfwLocalNetworkGate(localLatency, true, out gfwGateReason))
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW gate must ignore latency without packet loss.");
        }

        PingGroupStats blockedPublic = PingSampleWindow.BuildStatsForTest("public", new bool[] { false, false, false, false, false, false, false, false, false, false }, new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        PingRollingSnapshot icmpBlocked = BuildRollingPingSnapshot(NetworkAccessState.Online, false, gateway, blockedPublic);
        if (!icmpBlocked.IcmpBlocked || icmpBlocked.Diagnosis != PingPathDiagnosis.IcmpBlocked)
        {
            throw new InvalidOperationException("Rolling ping self-test: ICMP blocked classification failed.");
        }

        PingRollingSnapshot localLoss = BuildRollingPingSnapshot(NetworkAccessState.Online, false, unstableGateway, publicLoss);
        if (localLoss.Diagnosis != PingPathDiagnosis.LocalLoss)
        {
            throw new InvalidOperationException("Rolling ping self-test: local priority failed.");
        }

        if (!TryBuildGfwLocalNetworkGate(localLoss, true, out gfwGateReason) ||
            gfwGateReason.IndexOf("确认丢包", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW gate local loss failed.");
        }

        PingRollingSnapshot baidu = BuildRollingPingSnapshot(NetworkAccessState.Online, true, gateway, warming);
        if (!string.Equals(baidu.ActiveProfile, "BAIDU", StringComparison.Ordinal) ||
            baidu.Diagnosis != PingPathDiagnosis.GlobalBlock)
        {
            throw new InvalidOperationException("Rolling ping self-test: GFW fallback failed.");
        }

        PingRollingSnapshot clone = publicLoss.ToSnapshot("PUB", PingPathDiagnosis.WanLoss, PingDiagnosisSeverity.Warning, "WAN LOSS");
        PingRollingSnapshot cloneCopy = clone.Clone();
        cloneCopy.ActiveProfile = "CHANGED";
        if (string.Equals(clone.ActiveProfile, cloneCopy.ActiveProfile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rolling ping self-test: clone isolation failed.");
        }

        NetworkMonitorSnapshot mislabeledTarget = new NetworkMonitorSnapshot
        {
            ConnectivityTarget = "PUB",
            PingRolling = new PingRollingSnapshot { ActiveProfile = "PUB" }
        };
        if (!string.Equals(ResolvePathPingTarget(mislabeledTarget), ConnectivityTarget, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PathPing target self-test: presentation labels must fall back to the concrete connectivity endpoint.");
        }

        mislabeledTarget.ConnectivityTarget = "9.9.9.9";
        if (!string.Equals(ResolvePathPingTarget(mislabeledTarget), "9.9.9.9", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PathPing target self-test: concrete endpoints must be preserved.");
        }

        NetworkMonitorReader targetOwner = new NetworkMonitorReader();
        try
        {
            lock (targetOwner.sync)
            {
                targetOwner.snapshot.ConnectivityTarget = ConnectivityTarget;
                targetOwner.ApplyRollingPingSnapshotLocked(NetworkAccessState.Online, false, "self-test");
                if (!string.Equals(targetOwner.snapshot.ConnectivityTarget, ConnectivityTarget, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Rolling ping self-test: applying a display profile must not overwrite the concrete connectivity target.");
                }
            }
        }
        finally
        {
            targetOwner.Dispose();
        }
    }

    private sealed class SlowChunkStream : Stream
    {
        private readonly byte[] data;
        private readonly int delayMs;
        private int offset;

        public SlowChunkStream(byte[] data, int delayMs)
        {
            this.data = data ?? new byte[0];
            this.delayMs = Math.Max(0, delayMs);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return this.data.Length; } }
        public override long Position
        {
            get { return this.offset; }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            if (this.offset >= this.data.Length || count <= 0)
            {
                return 0;
            }

            Thread.Sleep(this.delayMs);
            buffer[bufferOffset] = this.data[this.offset++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RollingPingRequest
    {
        public long Generation;
        public string InterfaceId;
        public string IdentitySignature;
        public string Gateway;
        public string Target;
        public string ActiveProfile;
        public string ActiveGroup;
        public bool InsideWall;
        public string Trigger;
    }

    private sealed class RollingPingHistoryEntry
    {
        public string CheckName;
        public string Trigger;
        public string Result;
        public bool Success;
        public Dictionary<string, object> Detail;
    }

    private struct PingProbeResult
    {
        public bool Success;
        public int LatencyMs;
        public bool Skipped;

        public static PingProbeResult CreateSkipped()
        {
            return new PingProbeResult { Skipped = true };
        }
    }

    private sealed class PingSampleWindow
    {
        private readonly List<RollingPingSample> samples = new List<RollingPingSample>();

        public void Add(bool success, int latencyMs, DateTime timestampUtc)
        {
            this.Prune(timestampUtc);
            this.samples.Add(new RollingPingSample
            {
                TimestampUtc = timestampUtc,
                Success = success,
                LatencyMs = success ? Math.Max(0, latencyMs) : 0
            });

            while (this.samples.Count > RollingPingMaxSamples)
            {
                this.samples.RemoveAt(0);
            }
        }

        public void Clear()
        {
            this.samples.Clear();
        }

        public PingGroupStats BuildStats(string group, DateTime nowUtc)
        {
            this.Prune(nowUtc);
            return BuildStats(group, this.samples);
        }

        private void Prune(DateTime nowUtc)
        {
            if (this.samples.Count == 0)
            {
                return;
            }

            DateTime cutoff = nowUtc - RollingPingSampleTtl;
            int removeCount = 0;
            for (int i = 0; i < this.samples.Count; i++)
            {
                if (this.samples[i].TimestampUtc >= cutoff)
                {
                    break;
                }

                removeCount++;
            }

            if (removeCount > 0)
            {
                this.samples.RemoveRange(0, removeCount);
            }
        }

        public static PingGroupStats BuildStatsForTest(string group, bool[] successes, int[] latencies)
        {
            List<RollingPingSample> values = new List<RollingPingSample>();
            DateTime now = DateTime.UtcNow;
            for (int i = 0; successes != null && i < successes.Length; i++)
            {
                values.Add(new RollingPingSample
                {
                    TimestampUtc = now.AddSeconds(i),
                    Success = successes[i],
                    LatencyMs = latencies != null && i < latencies.Length ? latencies[i] : 0
                });
            }

            return BuildStats(group, values);
        }

        private static PingGroupStats BuildStats(string group, List<RollingPingSample> values)
        {
            PingGroupStats stats = new PingGroupStats();
            stats.Group = group ?? string.Empty;
            if (values == null || values.Count == 0)
            {
                return stats;
            }

            List<int> successes = new List<int>();
            stats.TotalCount = values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                RollingPingSample sample = values[i];
                if (sample.Success)
                {
                    stats.SuccessCount++;
                    successes.Add(sample.LatencyMs);
                }
            }

            stats.LostCount = stats.TotalCount - stats.SuccessCount;
            stats.StatsReady = stats.TotalCount >= RollingPingMinSamples;
            stats.LossPercent = stats.TotalCount <= 0 ? 0.0 : stats.LostCount * 100.0 / stats.TotalCount;

            if (successes.Count > 0)
            {
                double sum = 0.0;
                for (int i = 0; i < successes.Count; i++)
                {
                    sum += successes[i];
                }

                stats.LatencyMs = sum / successes.Count;
            }

            if (successes.Count >= 3)
            {
                double jitterSum = 0.0;
                for (int i = 1; i < successes.Count; i++)
                {
                    jitterSum += Math.Abs(successes[i] - successes[i - 1]);
                }

                stats.JitterMs = jitterSum / (successes.Count - 1);
                stats.JitterKnown = true;
            }

            return stats;
        }
    }

    private struct RollingPingSample
    {
        public DateTime TimestampUtc;
        public bool Success;
        public int LatencyMs;
    }

    private struct PingGroupStats
    {
        public string Group;
        public int TotalCount;
        public int SuccessCount;
        public int LostCount;
        public double LossPercent;
        public double LatencyMs;
        public double JitterMs;
        public bool JitterKnown;
        public bool StatsReady;

        public PingRollingSnapshot ToSnapshot(
            string activeProfile,
            PingPathDiagnosis diagnosis,
            PingDiagnosisSeverity severity,
            string diagnosisText)
        {
            return new PingRollingSnapshot
            {
                ActiveProfile = activeProfile,
                ActiveTargetLabel = activeProfile,
                Group = this.Group,
                SampleCount = this.TotalCount,
                LostCount = this.LostCount,
                LossPercent = this.LossPercent,
                LatencyMs = this.LatencyMs,
                JitterMs = this.JitterMs,
                JitterKnown = this.JitterKnown,
                StatsReady = this.StatsReady,
                Diagnosis = diagnosis,
                Severity = severity,
                DiagnosisText = diagnosisText ?? string.Empty
            };
        }
    }

    private struct PingMeasurement
    {
        public List<long> RoundTrips;
        public int Failures;
    }

    private struct DnsQueryResult
    {
        public bool Success;
        public int RCode;
        public int AnswerAddressCount;
        public int ElapsedMs;
        public string Error;

        public static DnsQueryResult CreateFailure(string error, int elapsedMs)
        {
            return new DnsQueryResult
            {
                Success = false,
                Error = error ?? string.Empty,
                ElapsedMs = elapsedMs
            };
        }
    }

    private struct ConnectivityResult
    {
        public bool Online;
        public NetworkAccessState AccessState;
        public string AccessReason;
        public double LatencyMs;
        public double JitterMs;
        public int PacketLossPercent;
        public bool LocalNetworkDegraded;
        public string LocalNetworkDegradedReason;
    }

    private struct CaptivePortalResult
    {
        public bool Online;
        public bool NeedsValidation;
        public string Reason;
    }
}
