using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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
    private readonly object sync = new object();
    private readonly GfwProbeReader gfwProbeReader = new GfwProbeReader();
    private readonly CloudEndpointProbeReader cloudEndpointProbeReader = new CloudEndpointProbeReader();
    private NetworkMonitorSnapshot snapshot = new NetworkMonitorSnapshot();
    private DateTime lastLocalRefreshUtc;
    private DateTime lastPublicIpRefreshUtc;
    private DateTime lastConnectivityRefreshUtc;
    private bool localRefreshRequested = true;
    // Public IP and connectivity requests are single-flight independently.
    private bool publicIpRequestRunning;
    private bool connectivityRequestRunning;
    // Incremented whenever the selected adapter or its addresses may have changed.
    // Background results must match this generation and InterfaceId before commit.
    private long networkGeneration;
    private string selectedAdapterId = string.Empty;
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
        lock (this.sync)
        {
            connected = this.snapshot.Connected;
            accessState = GetActualAccessState(this.snapshot);
            connectivityStartedUtc = this.lastConnectivityRefreshUtc;
            publicIpStartedUtc = this.lastPublicIpRefreshUtc;
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
            result.DnsServers = JoinDnsServers(properties);

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
            !string.Equals(previous.DnsServers, current.DnsServers, StringComparison.OrdinalIgnoreCase);
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

    private static string JoinDnsServers(IPInterfaceProperties properties)
    {
        if (properties == null || properties.DnsAddresses == null)
        {
            return "--";
        }

        List<string> values = new List<string>();
        foreach (IPAddress address in properties.DnsAddresses)
        {
            if (address == null || IsIgnorableAddress(address))
            {
                continue;
            }

            AddDistinct(values, address.ToString());
        }

        return values.Count == 0 ? "--" : JoinLimited(values, 3);
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
