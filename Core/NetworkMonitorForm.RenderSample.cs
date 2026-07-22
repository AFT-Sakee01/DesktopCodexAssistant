using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Test-only render harness for --render-networkmonitor. It exercises only the retained left-dock
// panel and uses deterministic data for visual review.
internal sealed partial class NetworkMonitorForm
{
    // Current sample keeps real user geometry/scale and the retained docked renderer while using a
    // deterministic snapshot; readers have no durable display cache.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
        {
            form.MaximumSize = Size.Empty;
            form.Size = form.GetDockedSize();
            form.snapshot = BuildSampleSnapshot();
            form.cleanIpSnapshot = BuildSampleCleanIpSnapshot();
            RenderSampleSupport.SaveComposited(
                outputDir,
                "networkmonitor-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawNetworkMonitorWindow);
        }
    }

    internal static string RenderScaleOverrideProof(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        Size baseline = RenderScaleOverrideSample(outputDir, -1, "networkmonitor-scale-default.png");
        Size scaled = RenderScaleOverrideSample(outputDir, 150, "networkmonitor-scale-150.png");
        double widthRatio = baseline.Width <= 0 ? 0.0 : (double)scaled.Width / baseline.Width;
        double heightRatio = baseline.Height <= 0 ? 0.0 : (double)scaled.Height / baseline.Height;
        if (Math.Abs(widthRatio - 1.5) > 0.01 || Math.Abs(heightRatio - 1.5) > 0.01)
        {
            throw new InvalidOperationException(
                "NetworkMonitor 150% scale proof did not enlarge both window dimensions proportionally.");
        }

        return "NetworkMonitor scale override: PASS default=" + baseline.Width + "x" + baseline.Height +
            " scaled_150=" + scaled.Width + "x" + scaled.Height +
            " width_ratio=" + widthRatio.ToString("0.000") +
            " height_ratio=" + heightRatio.ToString("0.000");
    }

    private static Size RenderScaleOverrideSample(string outputDir, int overridePercent, string fileName)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.NetworkMonitorScaleOverridePercent = overridePercent;
        settings.Normalize();
        using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
        {
            form.MaximumSize = new Size(4000, 4000);
            form.snapshot = BuildSampleSnapshot();
            RenderSampleSupport.SaveComposited(
                outputDir,
                fileName,
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawNetworkMonitorWindow);
            return form.Size;
        }
    }

    // Docked samples: the wide left-dock layout at its default (two-column) size and at the
    // minimum Spec board width, which is where it degrades to a single column. Rendering both is
    // the only way to see whether the hop table and the DNS/egress rows still fit when compressed.
    internal static void RenderDockedSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        RenderDockedSample(outputDir, "networkmonitor-docked.png", 648, 400, false);
        RenderDockedSample(outputDir, "networkmonitor-docked-narrow.png", WidgetSettings.MinSpecBoardWidth, 400, false);
        RenderDockedSample(outputDir, "networkmonitor-docked-discovery.png", 648, 400, true);
    }

    private static void RenderDockedSample(string outputDir, string fileName, int width, int height, bool discovery)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = width;
        settings.SpecBoardHeight = height;
        settings.Normalize();

        using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
        {
            // Same sampling recipe as SpecBoardForm.RenderSamples: scale 2 with a doubled canvas,
            // which is exactly what the runtime window does at 200% layer scale. Any truncation
            // visible here is truncation the user would see.
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
            form.snapshot = BuildSampleSnapshot();
            if (discovery)
            {
                form.snapshot.PathPing = new PathPingSnapshot
                {
                    TargetLabel = "1.1.1.1",
                    PathKnown = false,
                    DiscoveryInProgress = true,
                    DiscoveryCurrentHop = 11,
                    DiscoveryMaxHops = 30
                };
            }
            form.cleanIpSnapshot = BuildSampleCleanIpSnapshot();

            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                form.DrawContentDocked(g);
                string path = Path.Combine(outputDir, fileName);
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine("Docked " + width + "x" + height + " -> " + path);
            }
        }
    }

    private static CleanIpConnectionSnapshot BuildSampleCleanIpSnapshot()
    {
        return new CleanIpConnectionSnapshot
        {
            CheckedAtLocal = DateTime.Now,
            CheckedAtKnown = true,
            Success = true,
            Ip = "203.0.113.10",
            Location = "江苏南京",
            Asn = "AS4837",
            Organization = "China Unicom",
            ScoreKnown = true,
            Score = 95,
            Grade = "A",
            NativeLabel = "原生IP",
            IpTypeLabel = "住宅IP",
            IpTypeReason = "原生住宅出口"
        };
    }

    // Synthetic hop table for the docked samples. It deliberately contains the two verdicts that
    // are easy to get wrong: a rate-limited middle hop (amber, target still clean) and a run of
    // silent hops that has to collapse into one row.
    private static PathPingSnapshot BuildSamplePathPing()
    {
        PathPingSnapshot pathPing = new PathPingSnapshot
        {
            TargetLabel = "1.1.1.1",
            PathKnown = true,
            LastTraceLocal = DateTime.Now.AddMinutes(-5),
            LastTraceKnown = true,
            RoundCount = 47,
            EndToEndLatencyMs = 12.0,
            EndToEndLossPercent = 0.0,
            EndToEndKnown = true,
            Blame = PathPingBlame.NodeRateLimit,
            BlameHopNumber = 5,
            BlameText = "第 5 跳节点限速，链路无实际丢包"
        };

        pathPing.Hops = new PathPingHopSnapshot[]
        {
            SampleHop(1, "192.168.1.1", 2.0, 0.0, PathPingHopSeverity.Normal, true, false),
            SampleHop(2, "100.64.0.1", 5.0, 0.0, PathPingHopSeverity.Normal, false, false),
            SampleHop(3, "58.53.192.33", 8.0, 0.0, PathPingHopSeverity.Normal, false, false),
            SilentHop(4, 1),
            SampleHop(5, "219.158.14.10", 24.0, 3.0, PathPingHopSeverity.RateLimited, false, false),
            SampleHop(6, "219.158.22.61", 28.0, 0.0, PathPingHopSeverity.Normal, false, false),
            SilentHop(7, 2),
            SampleHop(9, "1.1.1.1", 12.0, 0.0, PathPingHopSeverity.Normal, false, true)
        };

        return pathPing;
    }

    private static PathPingHopSnapshot SampleHop(
        int hopNumber,
        string address,
        double latencyMs,
        double lossPercent,
        PathPingHopSeverity severity,
        bool isGateway,
        bool isTarget)
    {
        return new PathPingHopSnapshot
        {
            HopNumber = hopNumber,
            Address = address,
            Responding = true,
            IsGateway = isGateway,
            IsTarget = isTarget,
            AvgLatencyMs = latencyMs,
            LossPercent = lossPercent,
            SampleCount = 20,
            MergedHopCount = 1,
            Severity = severity
        };
    }

    private static PathPingHopSnapshot SilentHop(int hopNumber, int mergedCount)
    {
        return new PathPingHopSnapshot
        {
            HopNumber = hopNumber,
            Address = string.Empty,
            Responding = false,
            MergedHopCount = mergedCount,
            Severity = PathPingHopSeverity.Unresponsive
        };
    }

    private static NetworkMonitorSnapshot BuildSampleSnapshot()
    {
        NetworkMonitorSnapshot snapshot = new NetworkMonitorSnapshot();
        snapshot.Connected = true;
        snapshot.InterfaceKnown = true;
        snapshot.InterfaceName = "Wi-Fi 6";
        snapshot.InterfaceType = "802.11ax";
        snapshot.LinkSpeedBps = 866000000;
        snapshot.IsWifi = true;
        snapshot.WifiDetails = new WifiConnectionDetails
        {
            Ssid = "HomeNet-5G",
            AuthAlgorithm = "WPA3",
            CipherAlgorithm = "AES",
            PhyType = "AX",
            SignalQuality = 88,
            RxRateKbps = 866000,
            TxRateKbps = 433000
        };
        snapshot.IPv4 = "192.168.1.42, 10.0.0.4";
        snapshot.IPv6 = "2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, fd00::1";
        snapshot.DnsServerDetails = new DnsServerSnapshot[]
        {
            new DnsServerSnapshot { Address = "1.1.1.1", Status = DnsServerStatus.Problem, Reason = "返回 SERVFAIL" },
            new DnsServerSnapshot { Address = "8.8.8.8", Status = DnsServerStatus.Normal, Reason = "正常" }
        };
        snapshot.ConnectivityKnown = true;
        snapshot.ConnectivityOnline = true;
        snapshot.AccessState = NetworkAccessState.Online;
        snapshot.ConnectivityTarget = "cloudflare.com";
        snapshot.LatencyMs = 18.0;
        snapshot.PacketLossPercent = 0;
        snapshot.PublicIp = "203.0.113.10";
        snapshot.PublicIpKnown = true;
        snapshot.GfwProbe = new GfwProbeSnapshot();
        snapshot.GfwProbe.Enabled = true;
        snapshot.GfwProbe.Status = GfwProbeStatus.Normal;
        snapshot.GfwProbe.CheckedAtKnown = true;
        snapshot.GfwProbe.Detail = "正常";
        snapshot.GfwProbe.CloudEndpoints = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Normal);
        snapshot.GfwProbe.CloudEndpoints[1].Status = CloudEndpointStatus.Slow;
        snapshot.GfwProbe.CloudEndpoints[1].LatencyMs = 420;
        snapshot.GfwProbe.CloudEndpoints[2].Status = CloudEndpointStatus.Down;
        snapshot.GfwProbe.CloudEndpoints[2].Reason = "请求超时";
        snapshot.GfwProbe.CloudEndpoints[3].Status = CloudEndpointStatus.Abnormal;
        snapshot.GfwProbe.CloudEndpoints[3].Reason = "状态公告";
        snapshot.DefaultGatewayAddress = "192.168.1.1";
        snapshot.MacAddress = "A1-B2-C3-D4-E5-F6";
        snapshot.UpdatedLocal = DateTime.Now;
        snapshot.PathPing = BuildSamplePathPing();
        snapshot.FixedPing = new FixedPingSnapshot
        {
            CheckedAtKnown = true,
            CheckedAtLocal = DateTime.Now,
            Targets = new FixedPingTargetSnapshot[]
            {
                new FixedPingTargetSnapshot { Key = "ping:8.8.8.8", DisplayName = "Google", Target = "8.8.8.8", Status = FixedPingStatus.Normal, LatencyMs = 38, Reason = "38ms" },
                new FixedPingTargetSnapshot { Key = "ping:180.101.50.188", DisplayName = "百度", Target = "180.101.50.188", Status = FixedPingStatus.Slow, LatencyMs = 382, Reason = "382ms" },
                new FixedPingTargetSnapshot { Key = "ping:98.137.11.163", DisplayName = "Yahoo", Target = "98.137.11.163", Status = FixedPingStatus.Down, Reason = "超时" }
            }
        };
        return snapshot;
    }
}
