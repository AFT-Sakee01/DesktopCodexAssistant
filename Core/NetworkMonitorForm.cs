using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// UI-only projection of NetworkMonitorSnapshot. I/O stays in NetworkMonitorReader;
// this form is responsible for change detection and layered-window resource ownership.
internal sealed class NetworkMonitorForm : Form
{
    private const int RenderSecondBoundaryOffsetMs = 55;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly NetworkMonitorReader reader;
    private WidgetSettings currentSettings;
    private NetworkMonitorSnapshot snapshot;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private bool sharedInteractionPolling;
    // Buffers are reused until size changes. renderBufferValid distinguishes a content
    // redraw from an alpha-only UpdateLayeredWindow submission.
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private Bitmap contentBitmap;
    private Graphics contentGraphics;
    private bool renderBufferValid;
    private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>(StringComparer.Ordinal);
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();

    public NetworkMonitorForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.reader = new NetworkMonitorReader();
        this.snapshot = new NetworkMonitorSnapshot();

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.MinimumSize = new Size(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MinNetworkMonitorHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorHeight);
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextRenderTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetNetworkIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        this.snapshot = this.reader.GetSnapshot(this.currentSettings);
        PositionNetworkMonitorWindow();
        this.timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        this.reader.Dispose();
        DisposeRenderBuffers();
        DisposeFontCache();
        this.layeredSurface.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Font sizes and bitmap dimensions are layout-dependent; clearing both caches
        // prevents stale dimensions and unbounded font growth during repeated resizing.
        DisposeRenderBuffers();
        DisposeFontCache();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(12)))
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
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        ApplyPerformanceTimerIntervals();
        this.snapshot = this.reader.GetSnapshot(this.currentSettings);

        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();
        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
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
        if (this.hiddenForFullscreen == hidden &&
            ((hidden && !this.Visible) || (!hidden && this.Visible)))
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            if (this.Visible)
            {
                this.Hide();
            }

            UpdateHoverAnimationTimer();
            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionNetworkMonitorWindow();
        RenderLayeredWindow();
        UpdateHoverAnimationTimer();
    }

    public void ForceRefresh()
    {
        this.reader.RequestRefresh();
        OnTimerTick(this, EventArgs.Empty);
    }

    public void RecoverAfterDisplayResume()
    {
        ResetDisplayRenderResources();
        PositionNetworkMonitorWindow();
        RenderLayeredWindow();
        this.reader.RequestRefresh();
        ScheduleNextRenderTick();
    }

    public void PrepareForDisplaySuspend()
    {
        ResetDisplayRenderResources();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        try
        {
            // The reader owns I/O; this timer consumes snapshots and redraws only visible changes.
            NetworkMonitorSnapshot nextSnapshot = this.reader.GetSnapshot(this.currentSettings);
            bool displayChanged = !HasSameDisplayData(this.snapshot, nextSnapshot);
            this.snapshot = nextSnapshot;
            Size desiredSize = GetDesiredSize();
            bool sizeChanged = false;
            if (this.Size != desiredSize)
            {
                this.Size = desiredSize;
                PositionNetworkMonitorWindow();
                sizeChanged = true;
            }

            if (!this.hiddenForFullscreen && this.Visible && (displayChanged || sizeChanged))
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

        int hoverInterval = WidgetSettings.GetNetworkIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
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
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.currentSettings.PerformanceMode);
        int elapsedInInterval = (int)(now.TimeOfDay.TotalMilliseconds % targetInterval);
        int interval = targetInterval - elapsedInInterval + RenderSecondBoundaryOffsetMs;
        if (interval <= RenderSecondBoundaryOffsetMs)
        {
            interval += targetInterval;
        }

        return Math.Max(50, Math.Min(targetInterval + 100, interval));
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        bool animationActive = ProcessInteractionTick();
        int desiredInterval = animationActive
            ? WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode)
            : WidgetSettings.GetNetworkIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != desiredInterval)
        {
            this.hoverTimer.Interval = desiredInterval;
        }
    }

    private bool ProcessInteractionTick()
    {
        ApplyClickThroughStyle();
        bool opacityChanged = UpdateHoverOpacityAnimation();
        bool hoverTarget = IsHoverOpacityTargetActive();
        bool animationActive = Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
        if (opacityChanged && !this.hiddenForFullscreen && this.Visible)
        {
            RenderLayeredWindow(false);
        }

        return animationActive;
    }

    public void SetSharedInteractionPolling(bool shared)
    {
        this.sharedInteractionPolling = shared;
        this.hoverOpacityLastUtc = DateTime.UtcNow;
        UpdateHoverAnimationTimer();
    }

    public bool ProcessSharedInteractionTick()
    {
        if (!this.sharedInteractionPolling ||
            this.hiddenForFullscreen ||
            (!this.currentSettings.HoverOpacityEnabled && !NeedsClickThroughPolling()))
        {
            return false;
        }

        return ProcessInteractionTick();
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (this.currentSettings.HoverOpacityEnabled || NeedsClickThroughPolling()))
        {
            if (this.sharedInteractionPolling)
            {
                this.hoverTimer.Stop();
                return;
            }

            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow();
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered = IsHoverOpacityTargetActive();

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    private bool IsHoverOpacityTargetActive()
    {
        return this.currentSettings.HoverOpacityEnabled &&
            !this.hiddenForFullscreen &&
            this.Visible &&
            this.Bounds.Contains(Cursor.Position);
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = ShouldClickThroughNow();
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

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

    private bool ShouldClickThroughNow()
    {
        if (NativeMethods.IsClickThroughModifierDown())
        {
            return false;
        }

        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    private bool NeedsClickThroughPolling()
    {
        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    private void PositionNetworkMonitorWindow()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.NetworkMonitorLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinNetworkMonitorHeight, this.currentSettings.NetworkMonitorHeight);
        int top = this.currentSettings.NetworkMonitorBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredSize()
    {
        return new Size(this.currentSettings.NetworkMonitorWidth, this.currentSettings.NetworkMonitorHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawNetworkMonitorWindow(e.Graphics);
    }

    private void DrawNetworkMonitorWindow(Graphics g)
    {
        DrawBackground(g);
        DrawNetworkProblemMark(g);
        DrawContentLayer(g);
    }

    private void ConfigureGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawBackground(Graphics g)
    {
        ConfigureGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, alpha)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawNetworkProblemMark(Graphics g)
    {
        NetworkAccessState state = GetDisplayAccessState();
        if (state != NetworkAccessState.Offline && state != NetworkAccessState.AdapterMissing)
        {
            return;
        }

        // The mark belongs to the background layer, so its alpha follows background
        // transparency rather than the independently configurable content transparency.
        int alpha = GetBackgroundOpacityAlpha();
        if (alpha <= 0)
        {
            return;
        }

        ConfigureGraphics(g);
        float insetX = this.Width * 0.20f;
        float insetY = this.Height * 0.20f;
        RectangleF mark = new RectangleF(insetX, insetY, this.Width - insetX * 2.0f, this.Height - insetY * 2.0f);
        using (Pen cross = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.DangerGlyph, alpha), Math.Max(1.0f, S(20))))
        {
            cross.StartCap = LineCap.Round;
            cross.EndCap = LineCap.Round;
            g.DrawLine(cross, mark.Left, mark.Top, mark.Right, mark.Bottom);
            g.DrawLine(cross, mark.Right, mark.Top, mark.Left, mark.Bottom);
        }
    }

    private void DrawContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawContent(g);
            return;
        }

        // Partial content opacity needs an isolated premultiplied-alpha layer.
        EnsureContentBuffer();
        this.contentGraphics.Clear(Color.Transparent);
        DrawContent(this.contentGraphics);
        DrawingUtil.DrawImageWithAlpha(g, this.contentBitmap, contentAlpha);
    }

    private void DrawContent(Graphics g)
    {
        ConfigureGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = S(10);
        RectangleF content = new RectangleF(padding, S(6), this.Width - padding * 2.0f, this.Height - S(12));
        float headerHeight = Math.Max(S(18), content.Height * 0.18f);
        RectangleF header = new RectangleF(content.Left, content.Top, content.Width, headerHeight);
        DrawHeader(g, header);

        float rowTop = header.Bottom + S(1);
        float rowHeight = Math.Max(S(12), (content.Bottom - rowTop) / 7.0f);
        DrawInfoRow(g, 0, "IF", BuildInterfaceText(), rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawInfoRow(g, 1, "IP4", EmptyToDash(this.snapshot.IPv4), rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawInfoRow(g, 2, "IP6", EmptyToDash(this.snapshot.IPv6), rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawInfoRow(g, 3, "DNS", EmptyToDash(this.snapshot.DnsServers), rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawInfoRow(g, 4, "WIFI", BuildWifiText(), rowTop, rowHeight, this.snapshot.IsWifi ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.GlyphMuted);
        DrawInfoRow(g, 5, "PING", BuildConnectivityText(), rowTop, rowHeight, GetConnectivityColor());
        DrawInfoRow(g, 6, "GFW", BuildGfwProbeText(), rowTop, rowHeight, GetGfwProbeColor());
    }

    private void DrawHeader(Graphics g, RectangleF rect)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        Color statusColor = GetAccessStateColor(accessState);
        Font titleFont = GetCachedUiFont(Math.Max(10.0f, rect.Height * 0.56f), FontStyle.Bold);
        Font statusFont = GetCachedUiFont(Math.Max(8.0f, rect.Height * 0.42f), FontStyle.Bold);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (SolidBrush publicBrush = new SolidBrush(this.snapshot.PublicIpKnown ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.GlyphMuted))
        {
            RectangleF titleRect = new RectangleF(rect.Left, rect.Top, rect.Width * 0.30f, rect.Height);
            DrawFittedText(g, "NETWORK", titleFont, titleBrush, titleRect, StringAlignment.Near);

            RectangleF statusRect = new RectangleF(titleRect.Right + S(4), rect.Top, rect.Width * 0.24f, rect.Height);
            DrawFittedText(g, GetAccessStateText(accessState), statusFont, statusBrush, statusRect, StringAlignment.Near);

            string publicIp = "公网 " + (this.snapshot.PublicIpRefreshing && !this.snapshot.PublicIpKnown ? "..." : EmptyToDash(this.snapshot.PublicIp));
            RectangleF publicRect = new RectangleF(statusRect.Right, rect.Top, rect.Right - statusRect.Right, rect.Height);
            DrawFittedText(g, publicIp, statusFont, publicBrush, publicRect, StringAlignment.Far);
        }
    }

    private void DrawInfoRow(Graphics g, int row, string label, string value, float rowTop, float rowHeight, Color valueColor)
    {
        float y = rowTop + row * rowHeight;
        RectangleF labelRect = new RectangleF(S(10), y, S(42), rowHeight);
        RectangleF valueRect = new RectangleF(labelRect.Right + S(3), y, this.Width - labelRect.Right - S(13), rowHeight);
        Font labelFont = GetCachedUiFont(Math.Max(8.0f, rowHeight * 0.52f), FontStyle.Bold);
        Font valueFont = GetCachedUiFont(Math.Max(8.5f, rowHeight * 0.58f), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (SolidBrush valueBrush = new SolidBrush(valueColor))
        {
            DrawFittedText(g, label, labelFont, labelBrush, labelRect, StringAlignment.Near);
            DrawFittedText(g, value, valueFont, valueBrush, valueRect, StringAlignment.Near);
        }
    }

    private string BuildInterfaceText()
    {
        if (this.snapshot == null || !this.snapshot.InterfaceKnown)
        {
            return "--";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} | {1} | {2}",
            EmptyToDash(this.snapshot.InterfaceName),
            EmptyToDash(this.snapshot.InterfaceType),
            FormatLinkSpeed(this.snapshot.LinkSpeedBps));
    }

    private string BuildWifiText()
    {
        if (this.snapshot == null || !this.snapshot.IsWifi)
        {
            return "--";
        }

        WifiConnectionDetails wifi = this.snapshot.WifiDetails ?? new WifiConnectionDetails();
        string ssid = EmptyToDash(wifi.Ssid);
        string auth = EmptyToDash(wifi.AuthAlgorithm);
        string cipher = EmptyToDash(wifi.CipherAlgorithm);
        string phy = EmptyToDash(wifi.PhyType);
        string signal = wifi.SignalQuality > 0 ? wifi.SignalQuality.ToString(CultureInfo.InvariantCulture) + "%" : "--";
        string rate = FormatRateMbps(wifi.RxRateKbps) + "/" + FormatRateMbps(wifi.TxRateKbps);
        return ssid + " | " + auth + "/" + cipher + " | PHY " + phy + " | " + signal + " | " + rate;
    }

    private string BuildConnectivityText()
    {
        if (this.snapshot == null || !this.snapshot.ConnectivityKnown)
        {
            return "checking " + NetworkMonitorTarget();
        }

        NetworkAccessState accessState = GetDisplayAccessState();
        if (accessState == NetworkAccessState.NeedsValidation)
        {
            return "需要验证 | " + EmptyToDash(this.snapshot.AccessReason);
        }

        if (accessState == NetworkAccessState.Offline)
        {
            string reason = EmptyToDash(this.snapshot.AccessReason);
            if (reason == "--")
            {
                reason = "loss " + Math.Max(0, this.snapshot.PacketLossPercent).ToString(CultureInfo.InvariantCulture) + "%";
            }

            return "FAIL " + NetworkMonitorTarget() + " | " + reason;
        }

        if (accessState == NetworkAccessState.AdapterMissing)
        {
            return "网卡未识别 | " + EmptyToDash(this.snapshot.AccessReason);
        }

        string state = this.snapshot.ConnectivityOnline ? "OK" : "FAIL";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} | {2:0}ms | jitter {3:0}ms | loss {4}%",
            state,
            NetworkMonitorTarget(),
            Math.Max(0.0, this.snapshot.LatencyMs),
            Math.Max(0.0, this.snapshot.JitterMs),
            Math.Max(0, this.snapshot.PacketLossPercent));
    }

    private string NetworkMonitorTarget()
    {
        return this.snapshot == null ? "1.1.1.1" : EmptyToDash(this.snapshot.ConnectivityTarget);
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

    private NetworkAccessState GetDisplayAccessState()
    {
        if (this.snapshot == null)
        {
            return NetworkAccessState.Unknown;
        }

        if (!this.snapshot.Connected)
        {
            return NetworkAccessState.AdapterMissing;
        }

        if (this.snapshot.AccessState == NetworkAccessState.AdapterMissing)
        {
            return NetworkAccessState.Unknown;
        }

        if (!this.snapshot.ConnectivityKnown)
        {
            return NetworkAccessState.Unknown;
        }

        if (this.snapshot.AccessState != NetworkAccessState.Unknown)
        {
            return this.snapshot.AccessState;
        }

        return this.snapshot.ConnectivityOnline ? NetworkAccessState.Online : NetworkAccessState.Offline;
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

    private string BuildGfwProbeText()
    {
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw == null || !gfw.Enabled || gfw.Status == GfwProbeStatus.Disabled)
        {
            return "关闭";
        }

        string text;
        if (gfw.Running && !gfw.CheckedAtKnown)
        {
            text = "检测中";
        }
        else if (gfw.Status == GfwProbeStatus.Unknown || gfw.Status == GfwProbeStatus.Checking)
        {
            text = "等待检测";
        }
        else
        {
            text = EmptyToDash(gfw.Detail);
        }

        string reason = EmptyToDash(gfw.Reason);
        if (reason != "--")
        {
            text += " | " + reason;
        }

        if (gfw.CheckedAtKnown)
        {
            text += " | " + gfw.CheckedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (gfw.Running && gfw.CheckedAtKnown)
        {
            text += " | 更新中";
        }

        return text;
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
            while (size > 7.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                size -= 0.7f * this.scale;
                drawFont = GetCachedUiFont(size, baseFont.Style);
            }

            g.DrawString(text, drawFont, brush, rect, format);
        }
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
            string.Equals(left.PublicIp, right.PublicIp, StringComparison.Ordinal) &&
            string.Equals(left.AccessReason, right.AccessReason, StringComparison.Ordinal) &&
            string.Equals(left.ConnectivityTarget, right.ConnectivityTarget, StringComparison.Ordinal) &&
            HasSameWifiData(left.WifiDetails, right.WifiDetails) &&
            HasSameGfwData(left.GfwProbe, right.GfwProbe);
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
            string.Equals(left.Reason, right.Reason, StringComparison.Ordinal);
    }

    private void RenderLayeredWindow()
    {
        RenderLayeredWindow(true);
    }

    private void RenderLayeredWindow(bool redrawContent)
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            EnsureRenderBuffer();
            // Alpha-only hover updates can submit the cached bitmap without rebuilding the content layer.
            bool refreshNativeBitmap = redrawContent || !this.renderBufferValid;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawNetworkMonitorWindow(this.renderGraphics);
                this.renderBufferValid = true;
            }

            if (!this.layeredSurface.Update(
                this.Handle,
                this.Location,
                this.renderBitmap,
                GetApplicationOpacityAlpha(),
                refreshNativeBitmap))
            {
                if (!this.layeredUpdateFailureLogged)
                {
                    this.layeredUpdateFailureLogged = true;
                    Program.LogInfo("NetworkMonitor UpdateLayeredWindow failed; falling back to normal paint.");
                }

                this.Invalidate();
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private void EnsureRenderBuffer()
    {
        if (this.renderBitmap != null &&
            this.renderGraphics != null &&
            this.renderBitmap.Width == this.Width &&
            this.renderBitmap.Height == this.Height)
        {
            return;
        }

        if (this.renderGraphics != null)
        {
            this.renderGraphics.Dispose();
        }

        if (this.renderBitmap != null)
        {
            this.renderBitmap.Dispose();
        }

        // PArgb matches UpdateLayeredWindow and avoids per-frame format conversion.
        this.renderBitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        this.renderGraphics = Graphics.FromImage(this.renderBitmap);
        this.renderBufferValid = false;
    }

    private void EnsureContentBuffer()
    {
        if (this.contentBitmap != null &&
            this.contentGraphics != null &&
            this.contentBitmap.Width == this.Width &&
            this.contentBitmap.Height == this.Height)
        {
            return;
        }

        if (this.contentGraphics != null)
        {
            this.contentGraphics.Dispose();
        }

        if (this.contentBitmap != null)
        {
            this.contentBitmap.Dispose();
        }

        this.contentBitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        this.contentGraphics = Graphics.FromImage(this.contentBitmap);
    }

    private void DisposeRenderBuffers()
    {
        this.renderBufferValid = false;
        if (this.renderGraphics != null)
        {
            this.renderGraphics.Dispose();
            this.renderGraphics = null;
        }

        if (this.renderBitmap != null)
        {
            this.renderBitmap.Dispose();
            this.renderBitmap = null;
        }

        if (this.contentGraphics != null)
        {
            this.contentGraphics.Dispose();
            this.contentGraphics = null;
        }

        if (this.contentBitmap != null)
        {
            this.contentBitmap.Dispose();
            this.contentBitmap = null;
        }
    }

    private void ResetDisplayRenderResources()
    {
        DisposeRenderBuffers();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
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
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.NetworkMonitorTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!this.currentSettings.HoverOpacityEnabled || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    private int S(int value)
    {
        return (int)Math.Round(value * this.scale);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
