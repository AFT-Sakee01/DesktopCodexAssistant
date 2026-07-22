using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Windows.Forms;

// UI-only projection of NetworkMonitorSnapshot. I/O stays in NetworkMonitorReader;
// this form is responsible for change detection and layered-window resource ownership.
internal sealed partial class NetworkMonitorForm : LayeredWidgetFormBase
{
    private const int RenderSecondBoundaryOffsetMs = 55;
    private readonly System.Windows.Forms.Timer timer;
    private readonly NetworkMonitorReader reader;
    private NetworkMonitorSnapshot snapshot;
    private bool dockedRuntimeStarted;
    private bool hiddenForFullscreen;
    private bool displaySuspended;
    private bool cloudEndpointCheckingBlink;
    private string cloudEndpointAlertSignature = string.Empty;
    private int cloudEndpointAlertIndex;
    private bool cloudEndpointAlertNamePhase = true;
    private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>(StringComparer.Ordinal);

    public NetworkMonitorForm(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        this.reader = new NetworkMonitorReader();
        this.snapshot = new NetworkMonitorSnapshot();
        ApplicationIcon.ApplyTo(this);

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.MinimumSize = Size.Empty;
        this.MaximumSize = Size.Empty;
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextRenderTickIntervalMs();
        this.timer.Tick += OnTimerTick;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartDockedOwner(this.Owner);
    }

    internal void StartDockedOwner(Form owner)
    {
        if (this.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(NetworkMonitorForm));
        }

        if (this.dockedRuntimeStarted)
        {
            return;
        }

        this.dockedRuntimeStarted = true;
        if (owner != null && this.Owner != owner)
        {
            this.Owner = owner;
        }

        // Create the hidden message target and the always-visible dock tab without showing the
        // 648x400 board first. The old Show-then-Hide startup could flash the full panel for a frame.
        IntPtr ignoredHandle = this.Handle;
        ApplyRuntimeSettings(this.CurrentSettings);
        this.snapshot = this.reader.GetSnapshot(this.CurrentSettings);
        this.reader.SetPathPingSamplingActive(false);
        this.timer.Start();

        // A stale direct Show call is treated as a request to initialize the canonical dock owner,
        // not as permission to restore the retired always-visible network window.
        if (this.Visible)
        {
            HideDockedPanel();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.dockedRuntimeStarted = false;
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        DisposeDockTab();
        this.reader.Dispose();
        DisposeFontCache();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Font sizes and bitmap dimensions are layout-dependent; clearing both caches
        // prevents stale dimensions and unbounded font growth during repeated resizing.
        DisposeFontCache();
        using (GraphicsPath path = RoundedRectangle(
            new RectangleF(0, 0, this.Width, this.Height),
            Math.Max(3, S(10))))
        {
            Region oldRegion = this.Region;
            this.Region = new Region(path);
            if (oldRegion != null)
            {
                oldRegion.Dispose();
            }
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionNetworkMonitorWindow();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        ApplyDockedSizeBounds();
        ApplyPerformanceTimerIntervals();
        this.snapshot = this.reader.GetSnapshot(this.CurrentSettings);
        SyncLeftDockTab();
        // The dock board must always receive pointer input.
        ApplyClickThroughStyle();
        if (!this.Visible)
        {
            // Collapsed docked panels have nothing to reposition or repaint; the tab owns display.
            this.reader.SetPathPingSamplingActive(false);
            InvalidateLayeredRenderBuffer();
            return;
        }

        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = ShouldUseTopMostPlacement();
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(shouldBeTopMost, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionNetworkMonitorWindow();
        RenderLayeredWindow();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        // The window body's visibility is driven by tab hover; environmental visibility hides the
        // tab and collapses any expanded board.
        if (this.hiddenForFullscreen == hidden)
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        SetDockTabHiddenForFullscreen(hidden);
    }

    public void ForceRefresh()
    {
        this.reader.RequestRefresh();
        // The docked network board owns the visible Clean IP profile now that the standalone
        // connection window is hidden. Keep both data sources behind this one user action while
        // preserving each reader's existing single-flight and cooldown rules.
        CleanIpConnectionReader.Shared.RequestRefresh();
        OnTimerTick(this, EventArgs.Empty);
    }

    public void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        ResetDisplayRenderResources();
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(false);
        }

        SyncLeftDockTab();
        if (!this.hiddenForFullscreen && this.Visible)
        {
            PositionNetworkMonitorWindow();
            RenderLayeredWindow();
        }

        this.reader.RequestRefresh();
        ScheduleNextRenderTick();
    }

    public void PrepareForDisplaySuspend()
    {
        this.displaySuspended = true;
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(true);
        }

        if (this.Visible)
        {
            HideDockedPanel();
        }

        ResetDisplayRenderResources();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        try
        {
            // The reader owns I/O; this timer consumes snapshots and redraws only visible changes.
            NetworkMonitorSnapshot nextSnapshot = this.reader.GetSnapshot(this.CurrentSettings);
            bool displayChanged = !HasSameDisplayData(this.snapshot, nextSnapshot);
            bool blinkChanged = GetDisplayAccessState(nextSnapshot) == NetworkAccessState.Online &&
                HasCheckingCloudEndpoint(nextSnapshot);
            if (blinkChanged)
            {
                this.cloudEndpointCheckingBlink = !this.cloudEndpointCheckingBlink;
            }

            this.snapshot = nextSnapshot;
            bool cleanIpChanged = RefreshCleanIpSnapshot();
            bool alertChanged = AdvanceCloudEndpointAlertRotation();
            Size desiredSize = GetDesiredSize();
            bool sizeChanged = false;
            if (this.Size != desiredSize)
            {
                this.Size = desiredSize;
                PositionNetworkMonitorWindow();
                sizeChanged = true;
            }

            bool positionChanged = false;
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                ShouldRefreshBurnInPosition())
            {
                PositionNetworkMonitorWindow();
                positionChanged = true;
            }

            if (!this.hiddenForFullscreen && this.Visible &&
                (displayChanged || cleanIpChanged || sizeChanged || positionChanged || blinkChanged || alertChanged))
            {
                RenderLayeredWindow();
            }
        }
        finally
        {
            ScheduleNextRenderTick();
        }
    }

    private void ApplyPerformanceTimerIntervals()
    {
        ScheduleNextRenderTick();

    }

    private void ScheduleNextRenderTick()
    {
        int interval = GetNextRenderTickIntervalMs();
        if (this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private int GetNextRenderTickIntervalMs()
    {
        // Align panel wakeups to wall-clock boundaries instead of preserving arbitrary startup offsets.
        DateTime now = DateTime.Now;
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.CurrentSettings.PerformanceMode);
        int elapsedInInterval = (int)(now.TimeOfDay.TotalMilliseconds % targetInterval);
        int interval = targetInterval - elapsedInInterval + RenderSecondBoundaryOffsetMs;
        if (interval <= RenderSecondBoundaryOffsetMs)
        {
            interval += targetInterval;
        }

        return Math.Max(50, Math.Min(targetInterval + 100, interval));
    }

    // Compatibility hooks retained until the host traversal is cleaned in phase 5. Dock boards do
    // not participate in floating-window hover opacity or shared click-through polling.
    public void SetSharedInteractionPolling(bool shared)
    {
    }

    public void SetAutoHideKeepAliveActive(bool active)
    {
    }

    public bool ProcessSharedInteractionTick()
    {
        return false;
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = (exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED;
        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private bool ShouldUseTopMostPlacement()
    {
        return true;
    }

    private void PositionNetworkMonitorWindow()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Size dockedSize = GetDockedSize();
        if (this.Size != dockedSize)
        {
            this.Size = dockedSize;
        }

        PositionAtLeftDock();
    }

    private Size GetDesiredSize()
    {
        return GetDockedSize();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawNetworkMonitorWindow(e.Graphics);
    }

    private void DrawNetworkMonitorWindow(Graphics g)
    {
        DrawContentDocked(g);
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawContentDocked(g);
    }

    private string BuildNetworkLinkSummary()
    {
        if (this.snapshot == null || !this.snapshot.InterfaceKnown)
        {
            return "--";
        }

        if (this.snapshot.IsWifi)
        {
            WifiConnectionDetails wifi = this.snapshot.WifiDetails ?? new WifiConnectionDetails();
            string signal = wifi.SignalQuality > 0 ? wifi.SignalQuality.ToString(CultureInfo.InvariantCulture) + "%" : "--";
            string rate = FormatRateMbps(wifi.RxRateKbps) + "/" + FormatRateMbps(wifi.TxRateKbps);
            return EmptyToDash(wifi.Ssid) + " · " + EmptyToDash(wifi.AuthAlgorithm) + " · " + signal + " · " + rate;
        }

        return EmptyToDash(this.snapshot.InterfaceName) + " · " + EmptyToDash(this.snapshot.InterfaceType) + " · " + FormatLinkSpeed(this.snapshot.LinkSpeedBps);
    }

    private static Color GetDockedDnsStatusColor(DnsServerStatus status)
    {
        if (status == DnsServerStatus.Normal)
        {
            return DesignTokens.Colors.Success;
        }

        if (status == DnsServerStatus.Unavailable || status == DnsServerStatus.Unknown)
        {
            return DesignTokens.Colors.GlyphMuted;
        }

        return DesignTokens.Colors.Danger;
    }

    private string BuildDockedGfwText()
    {
        string text = BuildCompactGfwText();
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw != null && gfw.CheckedAtKnown)
        {
            text += " " + gfw.CheckedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private string BuildCompactGfwText()
    {
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw == null || !gfw.Enabled || gfw.Status == GfwProbeStatus.Disabled)
        {
            return "关闭";
        }

        if (gfw.Running && !gfw.CheckedAtKnown)
        {
            return "检测中";
        }

        switch (gfw.Status)
        {
            case GfwProbeStatus.Normal:
                return "正常";
            case GfwProbeStatus.Checking:
                return "检测中";
            case GfwProbeStatus.Unknown:
                return "等待";
            case GfwProbeStatus.SuspectedDns:
                return "疑似DNS污染";
            case GfwProbeStatus.SuspectedTcp:
                return "疑似TCP阻断";
            case GfwProbeStatus.SuspectedTlsSni:
                return "疑似SNI阻断";
            case GfwProbeStatus.SuspectedHttp:
                return "疑似HTTP阻断";
            case GfwProbeStatus.Inconclusive:
                return "不确定";
            default:
                return EmptyToDash(gfw.Detail);
        }
    }

    private void DrawHeader(Graphics g, RectangleF rect)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        string statusText = GetHeaderStatusText(accessState);
        Color statusColor = GetHeaderStatusColor(accessState);
        Font titleFont = GetCachedUiFont(Math.Max(10.0f, rect.Height * 0.56f), FontStyle.Bold);
        Font statusFont = GetCachedUiFont(Math.Max(8.0f, rect.Height * 0.42f), FontStyle.Bold);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        {
            RectangleF titleRect = new RectangleF(rect.Left, rect.Top, rect.Width * 0.26f, rect.Height);
            DrawFittedText(g, "NETWORK", titleFont, titleBrush, titleRect, StringAlignment.Near);

            float cloudWidth = GetCloudEndpointTileStripWidth(rect.Height);
            float cloudLeft = cloudWidth > 0.0f ? Math.Max(titleRect.Right, rect.Right - cloudWidth) : rect.Right;
            RectangleF cloudRect = cloudWidth > 0.0f
                ? new RectangleF(cloudLeft, rect.Top, Math.Max(0.0f, rect.Right - cloudLeft), rect.Height)
                : RectangleF.Empty;
            float statusLeft = titleRect.Right + S(4);
            float statusRight = cloudRect.IsEmpty ? rect.Right : cloudRect.Left - S(4);
            RectangleF statusRect = new RectangleF(statusLeft, rect.Top, Math.Max(0.0f, statusRight - statusLeft), rect.Height);
            CloudEndpointAlert alert = GetCloudEndpointAlert(accessState);
            RectangleF statusTextRect = statusRect;
            if (alert.Active && statusRect.Width > 0.0f)
            {
                float gap = S(4);
                float statusWidth = Math.Min(statusRect.Width, Math.Max(S(38), g.MeasureString(statusText, statusFont).Width + S(2)));
                float alertLeft = statusRect.Left + statusWidth + gap;
                float alertRight = Math.Max(alertLeft, statusRect.Right);
                statusTextRect = new RectangleF(statusRect.Left, statusRect.Top, statusWidth, statusRect.Height);
                RectangleF alertRect = new RectangleF(alertLeft, statusRect.Top, Math.Max(0.0f, alertRight - alertLeft), statusRect.Height);
                using (SolidBrush alertBrush = new SolidBrush(alert.Color))
                {
                    DrawFixedText(g, alert.Text, statusFont, alertBrush, alertRect, StringAlignment.Near);
                }
            }

            if (statusTextRect.Width > 0.0f)
            {
                DrawFittedText(g, statusText, statusFont, statusBrush, statusTextRect, StringAlignment.Near);
            }

            DrawCloudEndpointTiles(g, cloudRect, accessState);
        }
    }

    private void DrawCloudEndpointTiles(Graphics g, RectangleF rect, NetworkAccessState accessState)
    {
        CloudEndpointSnapshot[] endpoints = GetDisplayCloudEndpoints();
        if (endpoints.Length == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        float gap = GetCloudEndpointTileGap();
        float tileHeight = Math.Max(4.0f, rect.Height * 0.98f);
        float tileWidth = Math.Max(tileHeight, (rect.Width - gap * (endpoints.Length - 1)) / endpoints.Length);
        float total = tileWidth * endpoints.Length + gap * (endpoints.Length - 1);
        if (total > rect.Width)
        {
            tileWidth = Math.Max(4.0f, (rect.Width - gap * (endpoints.Length - 1)) / endpoints.Length);
            total = tileWidth * endpoints.Length + gap * (endpoints.Length - 1);
        }

        float x = rect.Right - total;
        float y = rect.Top + Math.Max(0.0f, (rect.Height - tileHeight) * 0.5f);
        Font tileFont = GetCachedUiFont(Math.Max(7.0f, tileHeight * 0.76f), FontStyle.Bold);
        for (int i = 0; i < endpoints.Length; i++)
        {
            CloudEndpointSnapshot endpoint = endpoints[i] ?? new CloudEndpointSnapshot();
            RectangleF tileRect = new RectangleF(x + i * (tileWidth + gap), y, tileWidth, tileHeight);
            using (GraphicsPath tilePath = RoundedRectangle(tileRect, Math.Max(1.0f, tileHeight * 0.24f)))
            using (SolidBrush tileBrush = new SolidBrush(GetCloudEndpointBackColor(endpoint, accessState)))
            {
                g.FillPath(tileBrush, tilePath);
            }

            using (SolidBrush endpointTextBrush = new SolidBrush(GetCloudEndpointTextColor(endpoint, accessState)))
            {
                DrawCloudEndpointTileText(g, GetCloudEndpointTileLabel(endpoint), tileFont, endpointTextBrush, tileRect);
            }
        }
    }

    private void DrawCloudEndpointTileText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            float size = baseFont.Size;
            float maxWidth = Math.Max(1.0f, rect.Width * 0.94f);
            while (size > 4.5f * this.LayerScale && g.MeasureString(text, drawFont).Width > maxWidth)
            {
                size -= 0.5f * this.LayerScale;
                drawFont = GetCachedUiFont(size, baseFont.Style);
            }

            g.DrawString(text, drawFont, brush, rect, format);
        }
    }

    private static string GetCloudEndpointTileLabel(CloudEndpointSnapshot endpoint)
    {
        if (endpoint == null)
        {
            return "--";
        }

        if (string.Equals(endpoint.Key, "github", StringComparison.OrdinalIgnoreCase))
        {
            return "Gi";
        }

        if (string.Equals(endpoint.Key, "aws", StringComparison.OrdinalIgnoreCase))
        {
            return "Aw";
        }

        return EmptyToDash(endpoint.ShortLabel);
    }

    private CloudEndpointSnapshot[] GetDisplayCloudEndpoints()
    {
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw == null || gfw.CloudEndpoints == null)
        {
            return CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
        }

        return gfw.CloudEndpoints;
    }

    private Color GetCloudEndpointBackColor(CloudEndpointSnapshot endpoint, NetworkAccessState accessState)
    {
        CloudEndpointStatus status = GetEffectiveCloudEndpointStatus(endpoint, accessState);
        if (status == CloudEndpointStatus.Normal)
        {
            return Color.FromArgb(38, 72, 47);
        }

        if (status == CloudEndpointStatus.Slow || status == CloudEndpointStatus.Checking)
        {
            if (status == CloudEndpointStatus.Checking && this.cloudEndpointCheckingBlink)
            {
                return Color.FromArgb(38, 72, 47);
            }

            return DesignTokens.Colors.Warning;
        }

        if (status == CloudEndpointStatus.Down)
        {
            return DesignTokens.Colors.Danger;
        }

        if (status == CloudEndpointStatus.Abnormal)
        {
            return DesignTokens.Colors.WarningDeep;
        }

        return Color.FromArgb(78, 84, 92);
    }

    private Color GetCloudEndpointTextColor(CloudEndpointSnapshot endpoint, NetworkAccessState accessState)
    {
        CloudEndpointStatus status = GetEffectiveCloudEndpointStatus(endpoint, accessState);
        if (status == CloudEndpointStatus.Normal || (status == CloudEndpointStatus.Checking && this.cloudEndpointCheckingBlink))
        {
            return Color.FromArgb(143, 220, 168);
        }

        return Color.FromArgb(76, 82, 90);
    }

    private CloudEndpointStatus GetEffectiveCloudEndpointStatus(CloudEndpointSnapshot endpoint, NetworkAccessState accessState)
    {
        if (endpoint == null || accessState != NetworkAccessState.Online)
        {
            return CloudEndpointStatus.Unknown;
        }

        if (endpoint.Status == CloudEndpointStatus.Checking)
        {
            return CloudEndpointStatus.Checking;
        }

        return endpoint.Status;
    }

    private CloudEndpointAlert GetCloudEndpointAlert(NetworkAccessState accessState)
    {
        if (accessState != NetworkAccessState.Online)
        {
            return new CloudEndpointAlert();
        }

        if (HasCheckingCloudEndpoint(this.snapshot))
        {
            return new CloudEndpointAlert
            {
                Active = true,
                Text = "云服务测试中",
                Color = DesignTokens.Colors.Warning
            };
        }

        CloudEndpointAlertCandidate[] candidates = GetCloudEndpointAlertCandidates(accessState);
        if (candidates.Length == 0)
        {
            return new CloudEndpointAlert();
        }

        int index = Math.Max(0, Math.Min(this.cloudEndpointAlertIndex, candidates.Length - 1));
        CloudEndpointAlertCandidate candidate = candidates[index];
        return new CloudEndpointAlert
        {
            Active = true,
            Text = (this.cloudEndpointAlertNamePhase ? candidate.Name : candidate.Reason) + "!",
            Color = candidate.Color
        };
    }

    private bool AdvanceCloudEndpointAlertRotation()
    {
        CloudEndpointAlertCandidate[] candidates = GetCloudEndpointAlertCandidates(GetDisplayAccessState());
        if (candidates.Length == 0)
        {
            bool hadMultiAlert = !string.IsNullOrEmpty(this.cloudEndpointAlertSignature);
            this.cloudEndpointAlertSignature = string.Empty;
            this.cloudEndpointAlertIndex = 0;
            this.cloudEndpointAlertNamePhase = true;
            return hadMultiAlert;
        }

        string signature = BuildCloudEndpointAlertSignature(candidates);
        if (!string.Equals(signature, this.cloudEndpointAlertSignature, StringComparison.Ordinal))
        {
            this.cloudEndpointAlertSignature = signature;
            this.cloudEndpointAlertIndex = 0;
            this.cloudEndpointAlertNamePhase = true;
            return true;
        }

        if (this.cloudEndpointAlertNamePhase)
        {
            this.cloudEndpointAlertNamePhase = false;
        }
        else
        {
            this.cloudEndpointAlertNamePhase = true;
            this.cloudEndpointAlertIndex = (this.cloudEndpointAlertIndex + 1) % candidates.Length;
        }

        return true;
    }

    private CloudEndpointAlertCandidate[] GetCloudEndpointAlertCandidates(NetworkAccessState accessState)
    {
        if (accessState != NetworkAccessState.Online)
        {
            return new CloudEndpointAlertCandidate[0];
        }

        CloudEndpointSnapshot[] endpoints = GetDisplayCloudEndpoints();
        List<CloudEndpointAlertCandidate> candidates = new List<CloudEndpointAlertCandidate>();
        for (int i = 0; i < endpoints.Length; i++)
        {
            CloudEndpointSnapshot endpoint = endpoints[i];
            CloudEndpointStatus status = GetEffectiveCloudEndpointStatus(endpoint, accessState);
            if (status != CloudEndpointStatus.Down && status != CloudEndpointStatus.Abnormal)
            {
                continue;
            }

            candidates.Add(new CloudEndpointAlertCandidate
            {
                Key = endpoint == null ? string.Empty : endpoint.Key,
                Status = status,
                Name = GetCloudEndpointAlertName(endpoint),
                Reason = GetCloudEndpointAlertReason(endpoint, status),
                Color = status == CloudEndpointStatus.Down
                    ? DesignTokens.Colors.Danger
                    : DesignTokens.Colors.WarningDeep
            });
        }

        AddDnsHeaderAlertCandidates(candidates);
        return candidates.ToArray();
    }

    private void AddDnsHeaderAlertCandidates(List<CloudEndpointAlertCandidate> candidates)
    {
        DnsAlertCandidate[] dnsCandidates = GetDnsAlertCandidates();
        for (int i = 0; i < dnsCandidates.Length; i++)
        {
            DnsAlertCandidate dns = dnsCandidates[i];
            if (dns == null || dns.Status == DnsServerStatus.Normal)
            {
                continue;
            }

            candidates.Add(new CloudEndpointAlertCandidate
            {
                Key = "dns:" + dns.Key,
                Status = GetHeaderAlertStatusForDns(dns.Status),
                Name = "DNS",
                Reason = GetHeaderAlertReasonForDns(dns.Text),
                Color = dns.Color
            });
        }
    }

    private static CloudEndpointStatus GetHeaderAlertStatusForDns(DnsServerStatus status)
    {
        if (status == DnsServerStatus.Hijacked)
        {
            return CloudEndpointStatus.Down;
        }

        if (status == DnsServerStatus.Unavailable)
        {
            return CloudEndpointStatus.Unknown;
        }

        return CloudEndpointStatus.Abnormal;
    }

    private static string GetHeaderAlertReasonForDns(string text)
    {
        string value = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        if (value.StartsWith("DNS", StringComparison.Ordinal))
        {
            value = value.Substring(3);
        }

        return value.Length == 0 ? "异常" : value;
    }

    private static string BuildCloudEndpointAlertSignature(CloudEndpointAlertCandidate[] candidates)
    {
        if (candidates == null || candidates.Length == 0)
        {
            return string.Empty;
        }

        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("|");
            }

            builder.Append(candidates[i].Key);
            builder.Append(":");
            builder.Append(candidates[i].Status.ToString());
            builder.Append(":");
            builder.Append(candidates[i].Name);
            builder.Append(":");
            builder.Append(candidates[i].Reason);
        }

        return builder.ToString();
    }

    private static string GetCloudEndpointAlertName(CloudEndpointSnapshot endpoint)
    {
        if (endpoint == null)
        {
            return "Cloud";
        }

        if (!string.IsNullOrWhiteSpace(endpoint.AlertName))
        {
            return endpoint.AlertName.Trim();
        }

        if (string.Equals(endpoint.Key, "cloudflare", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloudflare";
        }

        if (string.Equals(endpoint.Key, "aws", StringComparison.OrdinalIgnoreCase))
        {
            return "AWS";
        }

        if (string.Equals(endpoint.Key, "google", StringComparison.OrdinalIgnoreCase))
        {
            return "Google";
        }

        if (string.Equals(endpoint.Key, "github", StringComparison.OrdinalIgnoreCase))
        {
            return "Github";
        }

        if (string.Equals(endpoint.Key, "akamai", StringComparison.OrdinalIgnoreCase))
        {
            return "Akamai";
        }

        if (string.Equals(endpoint.Key, "azure", StringComparison.OrdinalIgnoreCase))
        {
            return "Azure";
        }

        return EmptyToDash(endpoint.DisplayName);
    }

    private static string GetCloudEndpointAlertReason(CloudEndpointSnapshot endpoint, CloudEndpointStatus status)
    {
        if (endpoint != null && !string.IsNullOrWhiteSpace(endpoint.AlertReason))
        {
            return endpoint.AlertReason.Trim();
        }

        if (status == CloudEndpointStatus.Down)
        {
            return "无法连接";
        }

        if (status == CloudEndpointStatus.Abnormal)
        {
            return "状态异常";
        }

        if (status == CloudEndpointStatus.Slow)
        {
            return "延迟过高";
        }

        return "未知原因";
    }

    private float GetCloudEndpointTileGap()
    {
        return Math.Max(1.0f, this.Width * (8.0f / 1108.0f));
    }

    private float GetCloudEndpointTileSize(float rowHeight)
    {
        return Math.Min(Math.Max(7.0f, rowHeight * 0.98f), S(20));
    }

    private float GetCloudEndpointTileStripWidth(float rowHeight)
    {
        CloudEndpointSnapshot[] endpoints = GetDisplayCloudEndpoints();
        if (endpoints.Length == 0)
        {
            return 0.0f;
        }

        float gap = GetCloudEndpointTileGap();
        float tile = GetCloudEndpointTileSize(rowHeight);
        float desired = tile * endpoints.Length + gap * (endpoints.Length - 1);
        float maxWidth = Math.Max(S(72), this.Width * 0.42f);
        return Math.Min(desired, maxWidth);
    }

    private DnsAlertCandidate[] GetDnsAlertCandidates()
    {
        return BuildDnsAlertCandidates(this.snapshot == null ? null : this.snapshot.DnsServerDetails);
    }

    private static DnsAlertCandidate[] BuildDnsAlertCandidates(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return CreateDnsNormalAlertCandidates();
        }

        DnsDisplayItem[] items = BuildDnsDisplayItems(details);
        List<DnsAlertCandidate> candidates = new List<DnsAlertCandidate>();
        for (int i = 0; i < items.Length; i++)
        {
            DnsServerSnapshot detail = items[i].Detail;
            if (detail == null ||
                (detail.Status != DnsServerStatus.Problem &&
                 detail.Status != DnsServerStatus.Hijacked &&
                 detail.Status != DnsServerStatus.Unavailable))
            {
                continue;
            }

            candidates.Add(new DnsAlertCandidate
            {
                Key = GetDnsAlertCandidateKey(detail, i),
                Address = detail.Address == null ? string.Empty : detail.Address.Trim(),
                Text = "DNS" + GetDnsAlertReasonText(detail),
                Color = GetDnsStatusColor(detail.Status),
                Status = detail.Status
            });
        }

        DisambiguateDuplicateDnsAlertText(candidates);
        return candidates.Count == 0 ? CreateDnsNormalAlertCandidates() : candidates.ToArray();
    }

    private static string GetDnsAlertCandidateKey(DnsServerSnapshot detail, int index)
    {
        string address = detail == null || string.IsNullOrWhiteSpace(detail.Address)
            ? string.Empty
            : detail.Address.Trim();
        if (address.Length > 0)
        {
            return address;
        }

        return "dns-" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static void DisambiguateDuplicateDnsAlertText(List<DnsAlertCandidate> candidates)
    {
        if (candidates == null || candidates.Count <= 1)
        {
            return;
        }

        string[] originalTexts = new string[candidates.Count];
        DnsServerStatus[] originalStatuses = new DnsServerStatus[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            originalTexts[i] = candidates[i].Text;
            originalStatuses[i] = candidates[i].Status;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            DnsAlertCandidate current = candidates[i];
            bool duplicate = false;
            for (int j = 0; j < candidates.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                if (originalStatuses[i] == originalStatuses[j] &&
                    string.Equals(originalTexts[i], originalTexts[j], StringComparison.Ordinal))
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate && !string.IsNullOrWhiteSpace(current.Address))
            {
                current.Text = current.Text + "@" + GetCompactDnsAlertAddress(current.Address);
            }
        }
    }

    private static string GetCompactDnsAlertAddress(string address)
    {
        string value = string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim();
        if (value.Length <= 15)
        {
            return value;
        }

        return "…" + value.Substring(value.Length - 10);
    }

    private static DnsAlertCandidate[] CreateDnsNormalAlertCandidates()
    {
        return new DnsAlertCandidate[]
        {
            new DnsAlertCandidate
            {
                Key = "normal",
                Address = string.Empty,
                Text = "DNS正常",
                Color = GetDnsStatusColor(DnsServerStatus.Normal),
                Status = DnsServerStatus.Normal
            }
        };
    }

    private static string GetDnsAlertReasonText(DnsServerSnapshot detail)
    {
        if (detail == null)
        {
            return "异常";
        }

        if (detail.Status == DnsServerStatus.Hijacked)
        {
            return "污染";
        }

        string reason = GetDnsAlertCompactReason(detail.Reason);
        if (detail.Status == DnsServerStatus.Unavailable)
        {
            return string.IsNullOrEmpty(reason) ? "不可用" : reason;
        }

        return string.IsNullOrEmpty(reason) ? "异常" : reason;
    }

    private static string GetDnsAlertCompactReason(string reason)
    {
        string value = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.IndexOf("TCP", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("仅TCP", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "仅TCP";
        }

        if (string.Equals(value, "无响应", StringComparison.Ordinal))
        {
            return "无响应";
        }

        if (string.Equals(value, "地址无效", StringComparison.Ordinal))
        {
            return "地址无效";
        }

        if (string.Equals(value, "无地址答案", StringComparison.Ordinal))
        {
            return "无地址";
        }

        if (string.Equals(value, "NXDOMAIN验证失败", StringComparison.Ordinal))
        {
            return "NX验证失败";
        }

        if (string.Equals(value, "NXDOMAIN一次异常", StringComparison.Ordinal))
        {
            return "NX一次异常";
        }

        const string nxdomainPrefix = "NXDOMAIN异常 ";
        if (value.StartsWith(nxdomainPrefix, StringComparison.Ordinal))
        {
            return TrimDnsAlertReason("NX异常" + value.Substring(nxdomainPrefix.Length));
        }

        const string returnedPrefix = "返回 ";
        if (value.StartsWith(returnedPrefix, StringComparison.Ordinal))
        {
            return TrimDnsAlertReason("返回" + value.Substring(returnedPrefix.Length));
        }

        if (string.Equals(value, "TimeoutException", StringComparison.Ordinal) ||
            value.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "超时";
        }

        if (string.Equals(value, "SocketException", StringComparison.Ordinal))
        {
            return "Socket异常";
        }

        if (string.Equals(value, "InvalidOperationException", StringComparison.Ordinal))
        {
            return "响应异常";
        }

        return TrimDnsAlertReason(value);
    }

    private static string TrimDnsAlertReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        System.Text.StringBuilder compact = new System.Text.StringBuilder(reason.Length);
        for (int i = 0; i < reason.Length; i++)
        {
            char ch = reason[i];
            if (!char.IsWhiteSpace(ch))
            {
                compact.Append(ch);
            }
        }

        const int maxLength = 12;
        if (compact.Length <= maxLength)
        {
            return compact.ToString();
        }

        return compact.ToString(0, maxLength - 1) + "…";
    }

    private static DnsDisplayItem[] BuildDnsDisplayItems(DnsServerSnapshot[] details)
    {
        if (details == null || details.Length == 0)
        {
            return new DnsDisplayItem[0];
        }

        List<DnsDisplayItem> items = new List<DnsDisplayItem>();
        for (int i = 0; i < details.Length; i++)
        {
            DnsServerSnapshot detail = details[i];
            if (detail == null || string.IsNullOrWhiteSpace(detail.Address))
            {
                continue;
            }

            items.Add(new DnsDisplayItem
            {
                Address = detail.Address.Trim(),
                Status = detail.Status,
                Detail = detail
            });
        }

        // DNS servers are already ordered by adapter priority; status only colors the row.
        return items.ToArray();
    }

    private static Color GetDnsStatusColor(DnsServerStatus status)
    {
        if (status == DnsServerStatus.Normal)
        {
            return DesignTokens.Colors.Success;
        }

        if (status == DnsServerStatus.Problem)
        {
            return DesignTokens.Colors.Warning;
        }

        if (status == DnsServerStatus.Hijacked)
        {
            return DesignTokens.Colors.Danger;
        }

        return DesignTokens.Colors.GlyphMuted;
    }

    private NetworkAccessState GetDisplayAccessState()
    {
        return GetDisplayAccessState(this.snapshot);
    }

    private Color GetConnectivityColor()
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        if (accessState == NetworkAccessState.Unknown)
        {
            return DesignTokens.Colors.GlyphMuted;
        }

        return GetAccessStateColor(accessState);
    }

    // Connectivity as the guard board's offline auto-sleep needs it: a definite true/false only when
    // this window has actually proven reachability one way or the other. Unknown and AdapterMissing
    // both return null so the guard never starts its countdown on an inconclusive reading — the
    // consequence of a false "offline" here is putting the machine to sleep under the user.
    internal bool? GetGuardOnlineState()
    {
        switch (GetDisplayAccessState())
        {
            case NetworkAccessState.Online:
                return true;

            case NetworkAccessState.Offline:
                return false;

            default:
                return null;
        }
    }

    private static NetworkAccessState GetDisplayAccessState(NetworkMonitorSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return NetworkAccessState.Unknown;
        }

        if (!snapshot.Connected)
        {
            return NetworkAccessState.AdapterMissing;
        }

        if (snapshot.AccessState == NetworkAccessState.AdapterMissing)
        {
            return NetworkAccessState.Unknown;
        }

        if (!snapshot.ConnectivityKnown)
        {
            return NetworkAccessState.Unknown;
        }

        if (snapshot.AccessState != NetworkAccessState.Unknown)
        {
            return snapshot.AccessState;
        }

        return snapshot.ConnectivityOnline ? NetworkAccessState.Online : NetworkAccessState.Offline;
    }

    private static string GetAccessStateText(NetworkAccessState state)
    {
        if (state == NetworkAccessState.Online)
        {
            return "ONLINE";
        }

        if (state == NetworkAccessState.NeedsValidation)
        {
            return "需要验证";
        }

        if (state == NetworkAccessState.Offline)
        {
            return "OFFLINE";
        }

        if (state == NetworkAccessState.AdapterMissing)
        {
            return "网卡未识别";
        }

        return "CHECKING";
    }

    private string GetHeaderStatusText(NetworkAccessState accessState)
    {
        if (accessState == NetworkAccessState.Online && HasFailedGfwProbe())
        {
            return "全球互联网不可用";
        }

        return GetAccessStateText(accessState);
    }

    private Color GetHeaderStatusColor(NetworkAccessState accessState)
    {
        if (accessState == NetworkAccessState.Online && HasFailedGfwProbe())
        {
            return DesignTokens.Colors.Warning;
        }

        return GetAccessStateColor(accessState);
    }

    private bool HasFailedGfwProbe()
    {
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw == null || !gfw.Enabled || !gfw.CheckedAtKnown)
        {
            return false;
        }

        return gfw.Status == GfwProbeStatus.SuspectedDns ||
            gfw.Status == GfwProbeStatus.SuspectedTcp ||
            gfw.Status == GfwProbeStatus.SuspectedTlsSni ||
            gfw.Status == GfwProbeStatus.SuspectedHttp;
    }

    private static Color GetAccessStateColor(NetworkAccessState state)
    {
        if (state == NetworkAccessState.Online)
        {
            return DesignTokens.Colors.Success;
        }

        if (state == NetworkAccessState.NeedsValidation)
        {
            return DesignTokens.Colors.Warning;
        }

        if (state == NetworkAccessState.Offline)
        {
            return DesignTokens.Colors.Danger;
        }

        if (state == NetworkAccessState.AdapterMissing)
        {
            return DesignTokens.Colors.Danger;
        }

        return DesignTokens.Colors.GlyphMuted;
    }

    private Color GetGfwProbeColor()
    {
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw == null || !gfw.Enabled || gfw.Status == GfwProbeStatus.Disabled || gfw.Status == GfwProbeStatus.Unknown)
        {
            return DesignTokens.Colors.GlyphMuted;
        }

        if (gfw.Status == GfwProbeStatus.Normal)
        {
            return DesignTokens.Colors.Success;
        }

        if (gfw.Status == GfwProbeStatus.Inconclusive || gfw.Status == GfwProbeStatus.Checking)
        {
            return DesignTokens.Colors.Warning;
        }

        return DesignTokens.Colors.Danger;
    }

    private static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "--";
        }

        double mbps = bitsPerSecond / 1000000.0;
        if (mbps >= 1000.0)
        {
            return (mbps / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "Gbps";
        }

        return mbps.ToString("0", CultureInfo.InvariantCulture) + "Mbps";
    }

    private static string FormatRateMbps(uint kbps)
    {
        if (kbps == 0)
        {
            return "--";
        }

        double mbps = kbps / 1000.0;
        if (mbps >= 1000.0)
        {
            return (mbps / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "G";
        }

        return mbps.ToString("0", CultureInfo.InvariantCulture) + "M";
    }

    private static string EmptyToDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
    }

    private static string BuildSingleAddressRowText(string value, int maxAddressLength)
    {
        List<string> addresses = ExtractAddressList(value);
        if (addresses.Count == 0)
        {
            return "--";
        }

        string first = maxAddressLength == int.MaxValue
            ? NormalizeIpAddressForDisplay(addresses[0])
            : CompactIpAddressForDisplay(addresses[0], maxAddressLength);
        int hiddenCount = Math.Max(0, addresses.Count - 1) + ExtractHiddenAddressCount(value);
        if (hiddenCount <= 0)
        {
            return first;
        }

        return first + " +" + hiddenCount.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeIpAddressForDisplay(string value)
    {
        string text = EmptyToDash(value);
        if (text == "--")
        {
            return text;
        }

        IPAddress address;
        return IPAddress.TryParse(text, out address) ? address.ToString() : text;
    }

    private static List<string> ExtractAddressList(string value)
    {
        List<string> addresses = new List<string>();
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "--", StringComparison.Ordinal))
        {
            return addresses;
        }

        string[] parts = value.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = StripHiddenAddressSuffix(parts[i]);
            if (part.Length == 0 || part[0] == '+')
            {
                continue;
            }

            addresses.Add(part);
        }

        return addresses;
    }

    private static int ExtractHiddenAddressCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        int index = value.LastIndexOf(" +", StringComparison.Ordinal);
        if (index < 0 || index + 2 >= value.Length)
        {
            return 0;
        }

        int count;
        return int.TryParse(value.Substring(index + 2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
            ? Math.Max(0, count)
            : 0;
    }

    private static string StripHiddenAddressSuffix(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        int index = text.LastIndexOf(" +", StringComparison.Ordinal);
        if (index < 0 || index + 2 >= text.Length)
        {
            return text;
        }

        int count;
        return int.TryParse(text.Substring(index + 2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
            ? text.Substring(0, index).Trim()
            : text;
    }

    private static string CompactIpAddressForDisplay(string value, int maxLength)
    {
        string text = EmptyToDash(value);
        if (text == "--")
        {
            return text;
        }

        IPAddress address;
        if (IPAddress.TryParse(text, out address))
        {
            text = address.ToString();
        }

        maxLength = Math.Max(8, maxLength);
        if (text.Length <= maxLength)
        {
            return text;
        }

        int head = Math.Max(4, (maxLength - 1) / 2);
        int tail = Math.Max(4, maxLength - head - 1);
        if (head + tail + 1 >= text.Length)
        {
            return text;
        }

        return text.Substring(0, head) + "…" + text.Substring(text.Length - tail);
    }

    private void DrawFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            float size = baseFont.Size;
            while (size > 7.0f * this.LayerScale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                size -= 0.7f * this.LayerScale;
                drawFont = GetCachedUiFont(size, baseFont.Style);
            }

            g.DrawString(text, drawFont, brush, rect, format);
        }
    }

    private static void DrawFixedText(Graphics g, string text, Font font, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            g.DrawString(text ?? string.Empty, font, brush, rect, format);
        }
    }

    private static bool HasCheckingCloudEndpoint(NetworkMonitorSnapshot snapshot)
    {
        GfwProbeSnapshot gfw = snapshot == null ? null : snapshot.GfwProbe;
        CloudEndpointSnapshot[] endpoints = gfw == null ? null : gfw.CloudEndpoints;
        if (endpoints == null)
        {
            return false;
        }

        for (int i = 0; i < endpoints.Length; i++)
        {
            if (endpoints[i] != null && endpoints[i].Status == CloudEndpointStatus.Checking)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSameDisplayData(NetworkMonitorSnapshot left, NetworkMonitorSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        // Compare only fields that affect pixels. Internal refresh timestamps and errors
        // are intentionally excluded so background bookkeeping cannot force a redraw.
        return left.Connected == right.Connected &&
            left.InterfaceKnown == right.InterfaceKnown &&
            left.LinkSpeedBps == right.LinkSpeedBps &&
            left.IsWifi == right.IsWifi &&
            left.PublicIpKnown == right.PublicIpKnown &&
            left.PublicIpRefreshing == right.PublicIpRefreshing &&
            left.ConnectivityKnown == right.ConnectivityKnown &&
            left.ConnectivityOnline == right.ConnectivityOnline &&
            left.AccessState == right.AccessState &&
            left.PacketLossPercent == right.PacketLossPercent &&
            Math.Abs(left.LatencyMs - right.LatencyMs) < 0.5 &&
            Math.Abs(left.JitterMs - right.JitterMs) < 0.5 &&
            string.Equals(left.InterfaceName, right.InterfaceName, StringComparison.Ordinal) &&
            string.Equals(left.InterfaceType, right.InterfaceType, StringComparison.Ordinal) &&
            string.Equals(left.IPv4, right.IPv4, StringComparison.Ordinal) &&
            string.Equals(left.IPv6, right.IPv6, StringComparison.Ordinal) &&
            string.Equals(left.DnsServers, right.DnsServers, StringComparison.Ordinal) &&
            HasSameDnsServerData(left.DnsServerDetails, right.DnsServerDetails) &&
            string.Equals(left.PublicIp, right.PublicIp, StringComparison.Ordinal) &&
            string.Equals(left.AccessReason, right.AccessReason, StringComparison.Ordinal) &&
            string.Equals(left.ConnectivityTarget, right.ConnectivityTarget, StringComparison.Ordinal) &&
            HasSameWifiData(left.WifiDetails, right.WifiDetails) &&
            HasSameGfwData(left.GfwProbe, right.GfwProbe) &&
            HasSamePingRollingData(left.PingRolling, right.PingRolling) &&
            HasSamePathPingData(left.PathPing, right.PathPing) &&
            HasSameFixedPingData(left.FixedPing, right.FixedPing);
    }

    private static bool HasSamePingRollingData(PingRollingSnapshot left, PingRollingSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return left.SampleCount == right.SampleCount &&
            left.LostCount == right.LostCount &&
            left.StatsReady == right.StatsReady &&
            left.IcmpBlocked == right.IcmpBlocked &&
            left.JitterKnown == right.JitterKnown &&
            left.Diagnosis == right.Diagnosis &&
            left.Severity == right.Severity &&
            Math.Abs(left.LossPercent - right.LossPercent) < 0.05 &&
            Math.Abs(left.LatencyMs - right.LatencyMs) < 0.5 &&
            Math.Abs(left.JitterMs - right.JitterMs) < 0.5 &&
            string.Equals(left.ActiveProfile, right.ActiveProfile, StringComparison.Ordinal) &&
            string.Equals(left.DiagnosisText, right.DiagnosisText, StringComparison.Ordinal);
    }

    private static bool HasSamePathPingData(PathPingSnapshot left, PathPingSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        if (left.PathKnown != right.PathKnown ||
            left.DiscoveryInProgress != right.DiscoveryInProgress ||
            left.DiscoveryCurrentHop != right.DiscoveryCurrentHop ||
            left.DiscoveryMaxHops != right.DiscoveryMaxHops ||
            left.Stale != right.Stale ||
            left.LastTraceKnown != right.LastTraceKnown ||
            left.LastTraceLocal != right.LastTraceLocal ||
            left.RoundCount != right.RoundCount ||
            left.EndToEndKnown != right.EndToEndKnown ||
            left.Blame != right.Blame ||
            left.BlameHopNumber != right.BlameHopNumber ||
            left.IcmpUnavailable != right.IcmpUnavailable ||
            Math.Abs(left.EndToEndLatencyMs - right.EndToEndLatencyMs) >= 0.5 ||
            Math.Abs(left.EndToEndLossPercent - right.EndToEndLossPercent) >= 0.05 ||
            !string.Equals(left.TargetLabel, right.TargetLabel, StringComparison.Ordinal) ||
            !string.Equals(left.BlameText, right.BlameText, StringComparison.Ordinal))
        {
            return false;
        }

        PathPingHopSnapshot[] leftHops = left.Hops ?? new PathPingHopSnapshot[0];
        PathPingHopSnapshot[] rightHops = right.Hops ?? new PathPingHopSnapshot[0];
        if (leftHops.Length != rightHops.Length)
        {
            return false;
        }

        for (int i = 0; i < leftHops.Length; i++)
        {
            PathPingHopSnapshot leftHop = leftHops[i];
            PathPingHopSnapshot rightHop = rightHops[i];
            if (ReferenceEquals(leftHop, rightHop))
            {
                continue;
            }

            if (leftHop == null || rightHop == null ||
                leftHop.HopNumber != rightHop.HopNumber ||
                leftHop.Responding != rightHop.Responding ||
                leftHop.IsGateway != rightHop.IsGateway ||
                leftHop.IsTarget != rightHop.IsTarget ||
                leftHop.SampleCount != rightHop.SampleCount ||
                leftHop.MergedHopCount != rightHop.MergedHopCount ||
                leftHop.Severity != rightHop.Severity ||
                Math.Abs(leftHop.AvgLatencyMs - rightHop.AvgLatencyMs) >= 0.5 ||
                Math.Abs(leftHop.LossPercent - rightHop.LossPercent) >= 0.05 ||
                !string.Equals(leftHop.Address, rightHop.Address, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameFixedPingData(FixedPingSnapshot left, FixedPingSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        FixedPingTargetSnapshot[] leftTargets = left.Targets ?? new FixedPingTargetSnapshot[0];
        FixedPingTargetSnapshot[] rightTargets = right.Targets ?? new FixedPingTargetSnapshot[0];
        if (left.Running != right.Running || leftTargets.Length != rightTargets.Length)
        {
            return false;
        }

        for (int i = 0; i < leftTargets.Length; i++)
        {
            FixedPingTargetSnapshot leftTarget = leftTargets[i];
            FixedPingTargetSnapshot rightTarget = rightTargets[i];
            if (ReferenceEquals(leftTarget, rightTarget))
            {
                continue;
            }

            if (leftTarget == null || rightTarget == null ||
                leftTarget.Status != rightTarget.Status ||
                leftTarget.LatencyMs != rightTarget.LatencyMs ||
                !string.Equals(leftTarget.Key, rightTarget.Key, StringComparison.Ordinal) ||
                !string.Equals(leftTarget.DisplayName, rightTarget.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(leftTarget.Target, rightTarget.Target, StringComparison.Ordinal) ||
                !string.Equals(leftTarget.Reason, rightTarget.Reason, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameDnsServerData(DnsServerSnapshot[] left, DnsServerSnapshot[] right)
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
            DnsServerSnapshot leftDns = left[i];
            DnsServerSnapshot rightDns = right[i];
            if (ReferenceEquals(leftDns, rightDns))
            {
                continue;
            }

            if (leftDns == null || rightDns == null ||
                leftDns.Status != rightDns.Status ||
                !string.Equals(leftDns.Address, rightDns.Address, StringComparison.Ordinal) ||
                !string.Equals(leftDns.Reason, rightDns.Reason, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameWifiData(WifiConnectionDetails left, WifiConnectionDetails right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return left.SecurityEnabled == right.SecurityEnabled &&
            left.OneXEnabled == right.OneXEnabled &&
            left.SignalQuality == right.SignalQuality &&
            left.TxRateKbps == right.TxRateKbps &&
            left.RxRateKbps == right.RxRateKbps &&
            string.Equals(left.Ssid, right.Ssid, StringComparison.Ordinal) &&
            string.Equals(left.PhyType, right.PhyType, StringComparison.Ordinal) &&
            string.Equals(left.AuthAlgorithm, right.AuthAlgorithm, StringComparison.Ordinal) &&
            string.Equals(left.CipherAlgorithm, right.CipherAlgorithm, StringComparison.Ordinal);
    }

    private static bool HasSameGfwData(GfwProbeSnapshot left, GfwProbeSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return left.Enabled == right.Enabled &&
            left.Running == right.Running &&
            left.Status == right.Status &&
            left.CheckedAtKnown == right.CheckedAtKnown &&
            left.CheckedAtLocal == right.CheckedAtLocal &&
            string.Equals(left.Detail, right.Detail, StringComparison.Ordinal) &&
            string.Equals(left.Reason, right.Reason, StringComparison.Ordinal) &&
            HasSameCloudEndpointData(left.CloudEndpoints, right.CloudEndpoints);
    }

    private static bool HasSameCloudEndpointData(CloudEndpointSnapshot[] left, CloudEndpointSnapshot[] right)
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
            CloudEndpointSnapshot leftEndpoint = left[i];
            CloudEndpointSnapshot rightEndpoint = right[i];
            if (ReferenceEquals(leftEndpoint, rightEndpoint))
            {
                continue;
            }

            if (leftEndpoint == null || rightEndpoint == null ||
                leftEndpoint.Domestic != rightEndpoint.Domestic ||
                leftEndpoint.Status != rightEndpoint.Status ||
                leftEndpoint.LatencyMs != rightEndpoint.LatencyMs ||
                !string.Equals(leftEndpoint.Key, rightEndpoint.Key, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.ShortLabel, rightEndpoint.ShortLabel, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.DisplayName, rightEndpoint.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.Reason, rightEndpoint.Reason, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.AlertReason, rightEndpoint.AlertReason, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.AlertName, rightEndpoint.AlertName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private Font GetCachedUiFont(float size, FontStyle style)
    {
        // Fitted text requests a small bounded set of sizes; cache them until layout changes.
        float normalizedSize = Math.Max(1.0f, (float)Math.Round(size, 2));
        string key = ((int)style).ToString(CultureInfo.InvariantCulture) + ":" +
            normalizedSize.ToString("0.00", CultureInfo.InvariantCulture);
        Font font;
        if (!this.fontCache.TryGetValue(key, out font))
        {
            font = DesignTokens.CreateUIFont(normalizedSize, style, GraphicsUnit.Pixel);
            this.fontCache[key] = font;
        }

        return font;
    }

    private void DisposeFontCache()
    {
        foreach (Font font in this.fontCache.Values)
        {
            font.Dispose();
        }

        this.fontCache.Clear();
        // UiFontCache.Dispose clears and stays reusable, matching how SpecBoardForm recycles its
        // cache across size changes.
        this.dockFonts.Dispose();
    }

    // The docked network role keeps its own transparency/scale slots; sibling board style must not
    // leak into it.
    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.NetworkMonitorTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.NetworkMonitorScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    protected override int ApplyHoverAlpha(int alpha)
    {
        return alpha;
    }

    internal static void RunNetworkMonitorDisplaySelfTest()
    {
        CleanIpConnectionSnapshot cleanIp = new CleanIpConnectionSnapshot
        {
            CheckedAtKnown = true,
            CheckedAtLocal = new DateTime(2026, 7, 22, 12, 30, 0),
            Success = true,
            ScoreKnown = true,
            Score = 92,
            Grade = "A",
            NativeLabel = "原生",
            IpTypeLabel = "住宅",
            Ip = "203.0.113.10",
            Location = "Tokyo",
            Asn = "AS64500",
            Organization = "Example",
            IpTypeReason = "可信",
            Error = string.Empty
        };
        if (!HasSameCleanIpDisplayData(cleanIp, cleanIp.Clone()))
        {
            throw new InvalidOperationException("Network monitor display self-test: identical Clean IP snapshots must not redraw.");
        }

        CleanIpConnectionSnapshot changedCleanIp = cleanIp.Clone();
        changedCleanIp.Score = 91;
        if (HasSameCleanIpDisplayData(cleanIp, changedCleanIp))
        {
            throw new InvalidOperationException("Network monitor display self-test: Clean IP score changes must redraw.");
        }

        changedCleanIp = cleanIp.Clone();
        changedCleanIp.NativeLabel = "广播";
        if (HasSameCleanIpDisplayData(cleanIp, changedCleanIp))
        {
            throw new InvalidOperationException("Network monitor display self-test: Clean IP native-label changes must redraw.");
        }

        changedCleanIp = cleanIp.Clone();
        changedCleanIp.Error = "lookup failed";
        changedCleanIp.IpTypeReason = string.Empty;
        if (HasSameCleanIpDisplayData(cleanIp, changedCleanIp))
        {
            throw new InvalidOperationException("Network monitor display self-test: Clean IP error changes must redraw.");
        }

        CloudEndpointSnapshot[] endpoints = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Normal);
        string order = string.Empty;
        for (int i = 0; i < endpoints.Length; i++)
        {
            if (i > 0)
            {
                order += " ";
            }

            order += endpoints[i].ShortLabel;
        }

        if (!string.Equals(order, "Cf Ak Gi Aw Az Go", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Network monitor display self-test: cloud tile order mismatch: " + order);
        }

        if (GetCloudEndpointAlertName(endpoints[5]) != "Google")
        {
            throw new InvalidOperationException("Network monitor display self-test: Google alert name mismatch.");
        }

        string singleIpv4 = BuildSingleAddressRowText("192.168.1.42, 10.0.0.4", int.MaxValue);
        if (!string.Equals(singleIpv4, "192.168.1.42 +1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Network monitor display self-test: IPv4 row must show only the first full address plus hidden count.");
        }

        string singleIpv6 = BuildSingleAddressRowText("2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, fd00::1 +1", int.MaxValue);
        if (singleIpv6.IndexOf(",", StringComparison.Ordinal) >= 0 ||
            singleIpv6.IndexOf("2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890", StringComparison.Ordinal) < 0 ||
            singleIpv6.IndexOf("+2", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Network monitor display self-test: IPv6 row must prefer one full address plus hidden count.");
        }

        string compactIpv6 = BuildSingleAddressRowText("2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, fd00::1 +1", 16);
        if (compactIpv6.IndexOf(",", StringComparison.Ordinal) >= 0 ||
            compactIpv6.IndexOf("2406:da18:7c3:8f00", StringComparison.Ordinal) >= 0 ||
            compactIpv6.IndexOf("…", StringComparison.Ordinal) < 0 ||
            compactIpv6.IndexOf("+2", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Network monitor display self-test: IPv6 compact row must keep one address and hidden count.");
        }

        DnsServerSnapshot[] dns = new DnsServerSnapshot[]
        {
            new DnsServerSnapshot { Address = "dns-a", Status = DnsServerStatus.Hijacked, Reason = "不存在域名被解析" },
            new DnsServerSnapshot { Address = "dns-b", Status = DnsServerStatus.Unavailable, Reason = "无响应" },
            new DnsServerSnapshot { Address = "dns-c", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用" },
            new DnsServerSnapshot { Address = "dns-d", Status = DnsServerStatus.Problem, Reason = "返回 SERVFAIL" },
            new DnsServerSnapshot { Address = "dns-e", Status = DnsServerStatus.Problem, Reason = "无地址答案" },
            new DnsServerSnapshot { Address = "dns-f", Status = DnsServerStatus.Problem, Reason = "NXDOMAIN验证失败" },
            new DnsServerSnapshot { Address = "dns-g", Status = DnsServerStatus.Problem, Reason = "NXDOMAIN异常 FORMERR" },
            new DnsServerSnapshot { Address = "dns-h", Status = DnsServerStatus.Unavailable, Reason = "地址无效" },
            new DnsServerSnapshot { Address = "dns-i", Status = DnsServerStatus.Problem, Reason = "TimeoutException" }
        };
        DnsAlertCandidate[] candidates = BuildDnsAlertCandidates(dns);
        if (candidates.Length != 9 ||
            !string.Equals(candidates[0].Text, "DNS污染", StringComparison.Ordinal) ||
            !string.Equals(candidates[1].Text, "DNS无响应", StringComparison.Ordinal) ||
            !string.Equals(candidates[2].Text, "DNS仅TCP", StringComparison.Ordinal) ||
            !string.Equals(candidates[3].Text, "DNS返回SERVFAIL", StringComparison.Ordinal) ||
            !string.Equals(candidates[4].Text, "DNS无地址", StringComparison.Ordinal) ||
            !string.Equals(candidates[5].Text, "DNSNX验证失败", StringComparison.Ordinal) ||
            !string.Equals(candidates[6].Text, "DNSNX异常FORMERR", StringComparison.Ordinal) ||
            !string.Equals(candidates[7].Text, "DNS地址无效", StringComparison.Ordinal) ||
            !string.Equals(candidates[8].Text, "DNS超时", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Network monitor display self-test: DNS alert text mapping failed.");
        }

        DnsServerSnapshot[] orderedDns = new DnsServerSnapshot[]
        {
            new DnsServerSnapshot { Address = "dns-a", Status = DnsServerStatus.Normal, Reason = "正常" },
            new DnsServerSnapshot { Address = "dns-b", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用" }
        };
        DnsDisplayItem[] orderedItems = BuildDnsDisplayItems(orderedDns);
        if (orderedItems.Length != 2 ||
            !string.Equals(orderedItems[0].Address, "dns-a", StringComparison.Ordinal) ||
            !string.Equals(orderedItems[1].Address, "dns-b", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Network monitor display self-test: DNS row must keep adapter priority order.");
        }

        DnsAlertCandidate[] reorderedCandidates = BuildDnsAlertCandidates(orderedDns);
        if (reorderedCandidates.Length != 1 ||
            !string.Equals(reorderedCandidates[0].Text, "DNS仅TCP", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Network monitor display self-test: DNS alert text must not include positional numbering.");
        }

        DnsServerSnapshot[] duplicateDns = new DnsServerSnapshot[]
        {
            new DnsServerSnapshot { Address = "1.1.1.1", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用" },
            new DnsServerSnapshot { Address = "8.8.8.8", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用" }
        };
        DnsAlertCandidate[] duplicateCandidates = BuildDnsAlertCandidates(duplicateDns);
        if (duplicateCandidates.Length != 2 ||
            string.Equals(duplicateCandidates[0].Text, duplicateCandidates[1].Text, StringComparison.Ordinal) ||
            duplicateCandidates[0].Text.IndexOf("@1.1.1.1", StringComparison.Ordinal) < 0 ||
            duplicateCandidates[1].Text.IndexOf("@8.8.8.8", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Network monitor display self-test: duplicate DNS alert reasons must remain visually distinguishable.");
        }

        using (NetworkMonitorForm form = new NetworkMonitorForm(WidgetSettings.CreateDefaults()))
        {
            form.Width = 360;
            form.snapshot = new NetworkMonitorSnapshot
            {
                Connected = true,
                InterfaceKnown = true,
                AccessState = NetworkAccessState.Online,
                ConnectivityKnown = true,
                ConnectivityOnline = true,
                DnsServerDetails = duplicateDns,
                GfwProbe = new GfwProbeSnapshot
                {
                    CloudEndpoints = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Normal)
                }
            };

            CloudEndpointAlertCandidate[] headerCandidates = form.GetCloudEndpointAlertCandidates(NetworkAccessState.Online);
            if (headerCandidates.Length != 2 ||
                !string.Equals(headerCandidates[0].Key, "dns:1.1.1.1", StringComparison.Ordinal) ||
                !string.Equals(headerCandidates[0].Name, "DNS", StringComparison.Ordinal) ||
                headerCandidates[0].Reason.IndexOf("仅TCP@1.1.1.1", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Network monitor display self-test: DNS errors must join the cloud header alert candidates.");
            }

            if (!form.AdvanceCloudEndpointAlertRotation())
            {
                throw new InvalidOperationException("Network monitor display self-test: combined header alert rotation initial state failed.");
            }

            CloudEndpointAlert headerAlert = form.GetCloudEndpointAlert(NetworkAccessState.Online);
            if (!headerAlert.Active || !string.Equals(headerAlert.Text, "DNS!", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network monitor display self-test: DNS header alert must show source name first.");
            }

            if (!form.AdvanceCloudEndpointAlertRotation())
            {
                throw new InvalidOperationException("Network monitor display self-test: combined header alert rotation reason phase failed.");
            }

            headerAlert = form.GetCloudEndpointAlert(NetworkAccessState.Online);
            if (!headerAlert.Active || headerAlert.Text.IndexOf("仅TCP@1.1.1.1!", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Network monitor display self-test: DNS header alert must rotate to the concrete reason.");
            }
        }

        DnsAlertCandidate[] normalCandidates = BuildDnsAlertCandidates(new DnsServerSnapshot[]
        {
            new DnsServerSnapshot { Address = "dns-a", Status = DnsServerStatus.Normal, Reason = "正常" }
        });
        if (normalCandidates.Length != 1 ||
            !string.Equals(normalCandidates[0].Text, "DNS正常", StringComparison.Ordinal) ||
            normalCandidates[0].Color.ToArgb() != DesignTokens.Colors.Success.ToArgb() ||
            GetDnsStatusColor(DnsServerStatus.Normal).ToArgb() != DesignTokens.Colors.Success.ToArgb())
        {
            throw new InvalidOperationException("Network monitor display self-test: DNS normal alert mapping failed.");
        }

        RunDockedLayoutSelfTest();
    }

    // Docked mode has a different size contract from the floating strip: it follows the Spec board
    // so the three boards in the left-edge queue expand to the same footprint, and the classic
    // 300px height cap must not clamp it.
    private static void RunDockedLayoutSelfTest()
    {
        Rectangle footer = new Rectangle(10, 364, 628, 26);
        DockedFooterLayout footerLayout = ComputeDockedFooterLayout(footer, 24.0f, 24.0f, 42, 14, 4, 5);
        if (footerLayout.RefreshAction.Left != footer.Left ||
            footerLayout.RefreshAction.Width != 42 ||
            footerLayout.CloseAction.Left != footerLayout.RefreshAction.Right + 4 ||
            footerLayout.CloseAction.Width != 42 ||
            footerLayout.RecentError.Left != footerLayout.CloseAction.Right + 5 ||
            footerLayout.RecentError.Width <= footerLayout.Trace.Width ||
            footerLayout.RecentError.Right != footerLayout.Trace.Left ||
            footerLayout.Trace.Right != footer.Right)
        {
            throw new InvalidOperationException("Network monitor docked self-test: footer refresh/close/error/trace order is invalid.");
        }

        Point refreshCenter = new Point(
            footerLayout.RefreshAction.Left + footerLayout.RefreshAction.Width / 2,
            footerLayout.RefreshAction.Top + footerLayout.RefreshAction.Height / 2);
        Point closeCenter = new Point(
            footerLayout.CloseAction.Left + footerLayout.CloseAction.Width / 2,
            footerLayout.CloseAction.Top + footerLayout.CloseAction.Height / 2);
        if (!ShouldHandleDockedFooterActionClick(true, true, MouseButtons.Left, refreshCenter, footerLayout.RefreshAction) ||
            !ShouldHandleDockedFooterActionClick(true, true, MouseButtons.Left, closeCenter, footerLayout.CloseAction) ||
            ShouldHandleDockedFooterActionClick(true, true, MouseButtons.Right, refreshCenter, footerLayout.RefreshAction) ||
            ShouldHandleDockedFooterActionClick(false, true, MouseButtons.Left, refreshCenter, footerLayout.RefreshAction) ||
            ShouldHandleDockedFooterActionClick(true, true, MouseButtons.Left, footerLayout.RecentError.Location, footerLayout.RefreshAction) ||
            ShouldHandleDockedFooterActionClick(true, true, MouseButtons.Left, footerLayout.RecentError.Location, footerLayout.CloseAction))
        {
            throw new InvalidOperationException("Network monitor docked self-test: footer action hit policy is invalid.");
        }

        DockedFooterLayout narrowFooter = ComputeDockedFooterLayout(new Rectangle(10, 364, 220, 26), 24.0f, 24.0f, 42, 14, 4, 5);
        if (narrowFooter.RecentError.Width <= 0 ||
            narrowFooter.Trace.Width <= 0 ||
            narrowFooter.Trace.Right != 230)
        {
            throw new InvalidOperationException("Network monitor docked self-test: narrow footer must retain error and trace slots.");
        }

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        // Reproduce the user's 200%-DPI + 50%-compatibility combination: LayerScale is 1.0,
        // while ScaleWindowSize alone sees only the 0.5 compatibility factor. Reusing the
        // floating window's bounds in this state clamps a requested 648x400 panel to 350x400.
        settings.ResolutionCompatibilityModeEnabled = true;
        settings.ResolutionCompatibilityScalePercent = 50;
        settings.Normalize();
        using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
        {
            form.SetLayerScale(1.0f);
            form.ApplyDockedSizeBounds();
            Size docked = form.GetDesiredSize();
            if (docked.Width != 648 || docked.Height != 400)
            {
                throw new InvalidOperationException("Network monitor docked self-test: docked size must follow the Spec board size.");
            }

            if (form.MinimumSize != Size.Empty || form.MaximumSize != Size.Empty)
            {
                throw new InvalidOperationException("Network monitor docked self-test: docked mode must clear floating size bounds.");
            }

            form.Size = docked;
            if (form.Size != docked)
            {
                throw new InvalidOperationException("Network monitor docked self-test: DPI/compatibility scaling must not clamp the Spec board footprint.");
            }

            if (form.IsDockedSingleColumn)
            {
                throw new InvalidOperationException("Network monitor docked self-test: the default width must keep two columns.");
            }

            // Style contract: the board keeps the Network role's transparency/scale slots and has
            // no floating hover/click-through state. Settings are assigned directly so the test
            // never materialises a real dock tab window.
            WidgetSettings overrides = settings.Clone();
            overrides.SpecBoardTransparencyOverridePercent = 40;
            overrides.NetworkMonitorTransparencyOverridePercent = 90;
            overrides.SpecBoardScaleOverridePercent = 125;
            overrides.NetworkMonitorScaleOverridePercent = 80;
            overrides.VisibilityMode = WidgetVisibilityMode.AlwaysVisible;
            overrides.ClickThroughMode = ClickThroughMode.Auto;
            overrides.HoverOpacityEnabled = true;
            overrides.Normalize();
            form.CurrentSettings = overrides;
            if (form.WindowTransparencyOverridePercent != overrides.NetworkMonitorTransparencyOverridePercent ||
                form.WindowScaleOverridePercent != overrides.NetworkMonitorScaleOverridePercent)
            {
                throw new InvalidOperationException("Network monitor docked self-test: docked overrides must use the Network role slots.");
            }

            if (form.ApplyHoverAlpha(255) != 255)
            {
                throw new InvalidOperationException("Network monitor docked self-test: hover fade must not apply to the docked panel.");
            }

            if (form.ProcessSharedInteractionTick() ||
                !form.ShouldUseTopMostPlacement())
            {
                throw new InvalidOperationException("Network monitor docked self-test: floating hover and click-through policies must be isolated from the board.");
            }

            WidgetSettings narrow = settings.Clone();
            narrow.SpecBoardWidth = WidgetSettings.MinSpecBoardWidth;
            narrow.Normalize();
            form.CurrentSettings = narrow;
            if (!form.IsDockedSingleColumn)
            {
                throw new InvalidOperationException("Network monitor docked self-test: the minimum width must degrade to a single column.");
            }
        }

        Console.WriteLine("Network monitor docked layout: PASS spec-board-size dpi-scale-unclamped spec-overrides board-interaction-isolation footer-refresh-close-hit single-column-degrade");
    }

    private sealed class CloudEndpointAlert
    {
        public bool Active;
        public string Text;
        public Color Color;
    }

    private sealed class CloudEndpointAlertCandidate
    {
        public string Key;
        public CloudEndpointStatus Status;
        public string Name;
        public string Reason;
        public Color Color;
    }

    private sealed class DnsAlertCandidate
    {
        public string Key;
        public string Address;
        public string Text;
        public DnsServerStatus Status;
        public Color Color;
    }

    private sealed class DnsDisplayItem
    {
        public string Address;
        public DnsServerStatus Status;
        public DnsServerSnapshot Detail;
    }

}
