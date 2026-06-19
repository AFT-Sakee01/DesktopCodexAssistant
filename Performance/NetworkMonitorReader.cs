using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private const int PingCount = 4;
    private const int PingTimeoutMs = 1000;
    private const int HttpTimeoutMs = 4000;
    private const string DnsKnownDomain = "www.msftconnecttest.com";
    private const int DnsQueryTimeoutMs = 1000;
    private const int MaxDnsProbeConcurrency = 2;
    private const ushort DnsQueryTypeA = 1;
    private const ushort DnsQueryTypeAaaa = 28;
    private readonly object sync = new object();
    private readonly GfwProbeReader gfwProbeReader = new GfwProbeReader();
    private readonly CloudEndpointProbeReader cloudEndpointProbeReader = new CloudEndpointProbeReader();
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
    // Incremented whenever the selected adapter or its addresses may have changed.
    // Background results must match this generation and InterfaceId before commit.
    private long networkGeneration;
    private string selectedAdapterId = string.Empty;
    private string lastDnsProbeSignature = string.Empty;
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
            this.networkGeneration++;
            this.snapshot.PublicIpRefreshing = false;
            if (this.snapshot.Connected)
            {
                this.snapshot.ConnectivityKnown = false;
                this.snapshot.ConnectivityOnline = false;
                this.snapshot.AccessState = NetworkAccessState.Unknown;
                this.snapshot.AccessReason = "正在刷新";
            }
        }

        this.gfwProbeReader.RequestRefresh();
        this.cloudEndpointProbeReader.RequestRefresh();
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
            RefreshLocalSnapshot(now, requestedAdapterId);
        }

        bool connected;
        NetworkAccessState accessState;
        DateTime connectivityStartedUtc;
        DateTime publicIpStartedUtc;
        DateTime dnsStartedUtc;
        string dnsSignature;
        string lastDnsSignature;
        DnsServerStatus worstDnsStatus;
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
        }

        int connectivityIntervalMs = WidgetSettings.GetNetworkConnectivityIntervalMs(mode, accessState);
        if (connected &&
            connectivityIntervalMs != int.MaxValue &&
            (now - connectivityStartedUtc).TotalMilliseconds >= connectivityIntervalMs)
        {
            StartConnectivityRefresh(now);
        }

        if (connected &&
            accessState == NetworkAccessState.Online &&
            (now - publicIpStartedUtc).TotalMinutes >= WidgetSettings.GetNetworkPublicIpRefreshIntervalMinutes(mode))
        {
            StartPublicIpRefresh(now);
        }

        int dnsProbeIntervalMs = WidgetSettings.GetNetworkDnsProbeIntervalMs(mode, worstDnsStatus);
        if (connected &&
            dnsSignature.Length > 0 &&
            (!string.Equals(dnsSignature, lastDnsSignature, StringComparison.OrdinalIgnoreCase) ||
             (now - dnsStartedUtc).TotalMilliseconds >= dnsProbeIntervalMs))
        {
            StartDnsRefresh(now);
        }

        GfwProbeSnapshot gfwProbe = this.gfwProbeReader.GetSnapshot(settings, accessState);
        gfwProbe.CloudEndpoints = this.cloudEndpointProbeReader.GetSnapshot(settings, accessState);
        lock (this.sync)
        {
            this.snapshot.GfwProbe = gfwProbe;
        }

        lock (this.sync)
        {
            NetworkMonitorSnapshot clone = this.snapshot.Clone();
            ApplyNetworkStatusTestMode(clone, settings);
            ApplyCloudEndpointTestMode(clone, settings);
            return clone;
        }
    }

    private void RefreshLocalSnapshot(DateTime now, string requestedAdapterId)
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

        lock (this.sync)
        {
            // A network event during enumeration keeps the refresh pending for one more stable pass.
            bool eventDuringRefresh = generationAtStart != this.networkGeneration;
            bool identityChanged = HasNetworkIdentityChanged(this.snapshot, local);
            if (identityChanged)
            {
                this.networkGeneration++;
                this.lastPublicIpRefreshUtc = DateTime.MinValue;
                this.lastConnectivityRefreshUtc = DateTime.MinValue;
                this.lastDnsRefreshUtc = DateTime.MinValue;
                this.lastDnsProbeSignature = string.Empty;
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
                local.ConnectivityTarget = this.snapshot.ConnectivityTarget;
                local.DnsServerDetails = CloneDnsServerDetails(this.snapshot.DnsServerDetails);
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
            }

            this.snapshot = local;
            this.lastLocalRefreshUtc = now;
            this.localRefreshRequested = eventDuringRefresh;
            this.selectedAdapterId = requestedAdapterId;
        }
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
        bool requestGfwRefresh = false;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            // Incrementing the generation prevents old-network tasks from publishing stale results.
            this.localRefreshRequested = true;
            this.lastLocalRefreshUtc = DateTime.MinValue;
            this.lastPublicIpRefreshUtc = DateTime.MinValue;
            this.lastConnectivityRefreshUtc = DateTime.MinValue;
            this.lastDnsRefreshUtc = DateTime.MinValue;
            this.lastDnsProbeSignature = string.Empty;
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
            }

            requestGfwRefresh = true;
        }

        if (requestGfwRefresh)
        {
            this.gfwProbeReader.RequestRefresh();
            this.cloudEndpointProbeReader.RequestRefresh();
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
            !HasSameDnsServerAddresses(previous.DnsServerDetails, current.DnsServerDetails);
    }

    private static bool HasSameDnsServerAddresses(DnsServerSnapshot[] left, DnsServerSnapshot[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            string leftAddress = left[i] == null ? string.Empty : left[i].Address;
            string rightAddress = right[i] == null ? string.Empty : right[i].Address;
            if (!string.Equals(leftAddress, rightAddress, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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

    private void StartPublicIpRefresh(DateTime now)
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
            string ip = string.Empty;
            string error = string.Empty;
            bool success = false;
            try
            {
                ip = FetchText("https://api64.ipify.org");
                success = !string.IsNullOrWhiteSpace(ip);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
            }

            if (!success)
            {
                try
                {
                    ip = FetchText("https://api.ipify.org");
                    success = !string.IsNullOrWhiteSpace(ip);
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name;
                }
            }

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
                    this.snapshot.PublicIp = ip.Trim();
                    this.snapshot.PublicIpKnown = true;
                    this.snapshot.LastError = string.Empty;
                }
                else
                {
                    this.snapshot.PublicIpKnown = false;
                    this.snapshot.LastError = error;
                }
            }
        });
    }

    private void StartDnsRefresh(DateTime now)
    {
        long requestGeneration;
        string requestInterfaceId;
        string[] addresses;
        string signature;
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
        }

        Task.Run(delegate
        {
            DnsServerSnapshot[] result;
            try
            {
                result = ProbeDnsServers(addresses);
            }
            catch (Exception ex)
            {
                result = CreateDnsFailureSnapshots(addresses, ex);
            }

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
            }
        });
    }

    private static DnsServerSnapshot[] CreateDnsFailureSnapshots(string[] addresses, Exception ex)
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

    private static DnsServerSnapshot[] ProbeDnsServers(string[] addresses)
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
                        result[index] = ProbeDnsServer(addresses[index]);
                    }
                    catch (Exception ex)
                    {
                        result[index] = new DnsServerSnapshot
                        {
                            Address = addresses[index] ?? string.Empty,
                            Status = DnsServerStatus.Unavailable,
                            Reason = ex.GetType().Name,
                            CheckedAtLocal = DateTime.Now,
                            CheckedAtKnown = true
                        };
                    }
                }
            });
        }

        Task.WaitAll(workers);
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == null)
            {
                result[i] = new DnsServerSnapshot
                {
                    Address = addresses[i] ?? string.Empty,
                    Status = DnsServerStatus.Unavailable,
                    Reason = "检测任务失败",
                    CheckedAtLocal = DateTime.Now,
                    CheckedAtKnown = true
                };
            }
        }

        return result;
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
        using (TcpClient client = new TcpClient(server.AddressFamily))
        {
            IAsyncResult connect = client.BeginConnect(server, 53, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                throw new TimeoutException("DNS TCP connect timeout");
            }

            client.EndConnect(connect);
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;
                byte[] length = new byte[] { (byte)(query.Length >> 8), (byte)(query.Length & 0xFF) };
                stream.Write(length, 0, length.Length);
                stream.Write(query, 0, query.Length);
                byte[] header = ReadExact(stream, 2);
                int responseLength = (header[0] << 8) | header[1];
                if (responseLength <= 0 || responseLength > 4096)
                {
                    throw new InvalidOperationException("Invalid DNS TCP response length");
                }

                return ReadExact(stream, responseLength);
            }
        }
    }

    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new InvalidOperationException("Unexpected DNS TCP EOF");
            }

            offset += read;
        }

        return buffer;
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

    private void StartConnectivityRefresh(DateTime now)
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
            ConnectivityResult result = MeasureConnectivity(ConnectivityTarget);
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
                if (!result.Online)
                {
                    this.snapshot.LastError = string.IsNullOrEmpty(result.AccessReason) ? "Connectivity failed" : result.AccessReason;
                }
                else
                {
                    this.snapshot.LastError = string.Empty;
                }
            }
        });
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
        using (WebResponse response = request.GetResponse())
        using (System.IO.Stream stream = response.GetResponseStream())
        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
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

    private static CaptivePortalResult CheckCaptivePortal()
    {
        CaptivePortalResult result = new CaptivePortalResult();
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(CaptivePortalTestUrl);
            request.Method = "GET";
            request.Timeout = HttpTimeoutMs;
            request.ReadWriteTimeout = HttpTimeoutMs;
            request.AllowAutoRedirect = false;
            request.UserAgent = ProductIdentity.UserAgent;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                int status = (int)response.StatusCode;
                string location = response.Headers["Location"];
                if (status >= 300 && status < 400)
                {
                    result.NeedsValidation = true;
                    result.Reason = string.IsNullOrEmpty(location) ? "门户重定向" : "门户重定向";
                    return result;
                }

                if (status == 401 || status == 403 || status == 511)
                {
                    result.NeedsValidation = true;
                    result.Reason = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
                    return result;
                }

                string text = string.Empty;
                using (System.IO.Stream stream = response.GetResponseStream())
                using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
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

        // NetworkChange events are static and would otherwise keep the reader/window alive.
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
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
    }

    private struct CaptivePortalResult
    {
        public bool Online;
        public bool NeedsValidation;
        public string Reason;
    }
}
