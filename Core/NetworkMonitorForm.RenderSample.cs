using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Test-only render harness for --render-networkmonitor: paints one representative frame of the
// Classic reference strip to a PNG for visual review, mirroring the CodexRadar/ConnectionCheck
// render harnesses.
internal sealed partial class NetworkMonitorForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        NetworkMonitorRenderVariant[] variants =
        {
            NetworkMonitorRenderVariant.Classic
        };

        foreach (NetworkMonitorRenderVariant variant in variants)
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.NetworkMonitorRenderVariant = variant;
            settings.Normalize();

            using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
            {
                form.SetLayerScale(2.0f);
                form.MaximumSize = new Size(4000, 4000);
                // NetworkMonitorWidth/Height are already the real physical pixel size (runtime
                // GetDesiredSize() applies them 1:1); an earlier *2 here rendered a canvas twice as
                // large as any real window, hiding genuine overflow/truncation. Same fix as
                // CodexRadarForm.RenderSample.cs.
                form.Size = new Size(settings.NetworkMonitorWidth, settings.NetworkMonitorHeight);
                form.snapshot = BuildSampleSnapshot();

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.FromArgb(15, 15, 19));
                    form.DrawContent(g);
                    string path = Path.Combine(outputDir, "networkmonitor-" + variant.ToString().ToLowerInvariant() + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(variant.ToString() + " -> " + path);
                }
            }
        }
    }

    // Current-mode sample: real settings.ini (size/variant/transparency). Live network state is
    // owned by background readers with no disk cache, so the frame reuses the synthetic snapshot
    // for content while geometry and styling stay the user's real configuration.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.NetworkMonitorWidth, settings.NetworkMonitorHeight);
            form.snapshot = BuildSampleSnapshot();
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
        return snapshot;
    }
}
