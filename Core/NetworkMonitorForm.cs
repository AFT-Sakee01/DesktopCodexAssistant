using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

// UI-only projection of NetworkMonitorSnapshot. I/O stays in NetworkMonitorReader;
// this form is responsible for change detection and layered-window resource ownership.
internal sealed partial class NetworkMonitorForm : Form
{
    private const int RenderSecondBoundaryOffsetMs = 55;
    private const int DnsAlertOverlayRowIndex = 6;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly NetworkMonitorReader reader;
    private WidgetSettings currentSettings;
    private NetworkMonitorSnapshot snapshot;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private bool cloudEndpointCheckingBlink;
    private string cloudEndpointAlertSignature = string.Empty;
    private int cloudEndpointAlertIndex;
    private bool cloudEndpointAlertNamePhase = true;
    private string dnsAlertSignature = string.Empty;
    private int dnsAlertIndex;
    private string dnsAlertCandidateKey = string.Empty;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime reverseHoverRevealUntilUtc;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool sharedInteractionPolling;
    // Buffers are reused until size changes. renderBufferValid distinguishes a content
    // redraw from an alpha-only UpdateLayeredWindow submission.
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private Bitmap contentBitmap;
    private Graphics contentGraphics;
    private bool renderBufferValid;
    private bool lastRenderedBurnInColorProtectionActive;
    private long burnInShiftSlot = long.MinValue;
    private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>(StringComparer.Ordinal);
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();

    public NetworkMonitorForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.reader = new NetworkMonitorReader();
        this.snapshot = new NetworkMonitorSnapshot();
        ApplicationIcon.ApplyTo(this);

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
            bool blinkChanged = GetDisplayAccessState(nextSnapshot) == NetworkAccessState.Online &&
                HasCheckingCloudEndpoint(nextSnapshot);
            if (blinkChanged)
            {
                this.cloudEndpointCheckingBlink = !this.cloudEndpointCheckingBlink;
            }

            this.snapshot = nextSnapshot;
            bool alertChanged = AdvanceCloudEndpointAlertRotation();
            bool dnsAlertChanged = AdvanceDnsAlertRotation();
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
                BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot))
            {
                PositionNetworkMonitorWindow();
                positionChanged = true;
            }

            if (!this.hiddenForFullscreen && this.Visible && (displayChanged || sizeChanged || positionChanged || blinkChanged || alertChanged || dnsAlertChanged))
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
            (!IsHoverOpacityRuntimeEnabled() && !NeedsClickThroughPolling()))
        {
            return false;
        }

        return ProcessInteractionTick();
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (IsHoverOpacityRuntimeEnabled() || NeedsClickThroughPolling()))
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
        return HoverInteractionPolicy.IsHoverOpacityTargetActive(
            this.currentSettings,
            this.Bounds,
            this.hiddenForFullscreen,
            this.Visible,
            ref this.reverseHoverRevealUntilUtc,
            this.hoverOpacityDelayState);
    }

    private bool IsHoverOpacityRuntimeEnabled()
    {
        return this.currentSettings.HoverOpacityEnabled || this.currentSettings.ForceHoverOpacityActive;
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

        Rectangle workArea = this.currentSettings.GetWorkAreaForModule(WidgetSettings.ModuleNetworkMonitor);
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.NetworkMonitorLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinNetworkMonitorHeight, this.currentSettings.NetworkMonitorHeight);
        int top = this.currentSettings.NetworkMonitorBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        Point shiftedLocation = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.NetworkMonitorSalt);
        left = shiftedLocation.X;
        top = shiftedLocation.Y;
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
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
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

    // Render-variant dispatch (mirrors CodexRadarForm). Only Classic exists today; add a case and a
    // sibling partial file (NetworkMonitorForm.<Name>.cs) to introduce an alternate layout.
    private void DrawContent(Graphics g)
    {
        switch (this.currentSettings.NetworkMonitorRenderVariant)
        {
            case NetworkMonitorRenderVariant.Typographic:
                DrawContentTypographic(g);
                return;
            case NetworkMonitorRenderVariant.AmberHud:
                DrawContentAmberHud(g);
                return;
            case NetworkMonitorRenderVariant.WarmCard:
                DrawContentWarmCard(g);
                return;
            case NetworkMonitorRenderVariant.Phosphor:
                DrawContentPhosphor(g);
                return;
            default:
                DrawContentClassic(g);
                return;
        }
    }

    private void DrawContentClassic(Graphics g)
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
        DrawInfoRow(g, 0, "IP4", this.snapshot.IPv4, rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawInfoRow(g, 1, "IP6", this.snapshot.IPv6, rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawInfoRow(g, 2, "IF", BuildInterfaceText(), rowTop, rowHeight, DesignTokens.Colors.TextStrong);
        DrawDnsRow(g, 3, rowTop, rowHeight);
        DrawInfoRow(g, 4, "WIFI", BuildWifiText(), rowTop, rowHeight, this.snapshot.IsWifi ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.GlyphMuted);
        DrawInfoRow(g, 5, "PING", BuildConnectivityText(), rowTop, rowHeight, GetConnectivityColor());
        DrawInfoRow(g, 6, "GFW", BuildGfwProbeText(), rowTop, rowHeight, GetGfwProbeColor());
        DrawDnsAlertOverlay(g, rowTop, rowHeight);
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
        using (SolidBrush publicBrush = new SolidBrush(HasPublicAddressDisplayValue() ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.GlyphMuted))
        {
            RectangleF titleRect = new RectangleF(rect.Left, rect.Top, rect.Width * 0.26f, rect.Height);
            DrawFittedText(g, "NETWORK", titleFont, titleBrush, titleRect, StringAlignment.Near);

            RectangleF statusRect = new RectangleF(titleRect.Right + S(4), rect.Top, rect.Width * 0.36f, rect.Height);
            CloudEndpointAlert alert = GetCloudEndpointAlert(accessState);
            string publicIp = BuildPublicAddressText("公网");
            RectangleF rightTop = new RectangleF(statusRect.Right, rect.Top, rect.Right - statusRect.Right, rect.Height);
            RectangleF publicRect = rightTop;
            RectangleF statusTextRect = statusRect;
            if (alert.Active)
            {
                float gap = S(4);
                float statusWidth = Math.Min(statusRect.Width, Math.Max(S(38), g.MeasureString(statusText, statusFont).Width + S(2)));
                float publicWidth = Math.Min(rightTop.Width, Math.Max(S(34), g.MeasureString(publicIp, statusFont).Width + S(2)));
                float alertLeft = statusRect.Left + statusWidth + gap;
                float alertRight = Math.Max(alertLeft, rect.Right - publicWidth - gap);
                statusTextRect = new RectangleF(statusRect.Left, statusRect.Top, statusWidth, statusRect.Height);
                RectangleF alertRect = new RectangleF(alertLeft, statusRect.Top, Math.Max(0.0f, alertRight - alertLeft), statusRect.Height);
                using (SolidBrush alertBrush = new SolidBrush(alert.Color))
                {
                    DrawFixedText(g, alert.Text, statusFont, alertBrush, alertRect, StringAlignment.Near);
                }
            }

            DrawFittedText(g, statusText, statusFont, statusBrush, statusTextRect, StringAlignment.Near);
            DrawFittedText(g, publicIp, statusFont, publicBrush, publicRect, StringAlignment.Far);
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
        float tile = GetCloudEndpointTileSize(rect.Height);
        float total = tile * endpoints.Length + gap * (endpoints.Length - 1);
        if (total > rect.Width)
        {
            tile = Math.Max(4.0f, (rect.Width - gap * (endpoints.Length - 1)) / endpoints.Length);
            total = tile * endpoints.Length + gap * (endpoints.Length - 1);
        }

        float x = rect.Right - total;
        float y = rect.Top + Math.Max(0.0f, (rect.Height - tile) * 0.5f);
        Font tileFont = GetCachedUiFont(Math.Max(7.0f, tile * 0.62f), FontStyle.Bold);
        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(76, 82, 90)))
        {
            for (int i = 0; i < endpoints.Length; i++)
            {
                CloudEndpointSnapshot endpoint = endpoints[i] ?? new CloudEndpointSnapshot();
                RectangleF tileRect = new RectangleF(x + i * (tile + gap), y, tile, tile);
                using (GraphicsPath tilePath = RoundedRectangle(tileRect, Math.Max(1.0f, S(3))))
                using (SolidBrush tileBrush = new SolidBrush(GetCloudEndpointBackColor(endpoint, accessState)))
                {
                    g.FillPath(tileBrush, tilePath);
                }

                DrawCloudEndpointTileText(g, GetCloudEndpointTileLabel(endpoint), tileFont, textBrush, tileRect);
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
            while (size > 4.5f * this.scale && g.MeasureString(text, drawFont).Width > maxWidth)
            {
                size -= 0.5f * this.scale;
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
        if (gfw == null || gfw.CloudEndpoints == null || gfw.CloudEndpoints.Length == 0)
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
            return DesignTokens.Colors.Success;
        }

        if (status == CloudEndpointStatus.Slow || status == CloudEndpointStatus.Checking)
        {
            if (status == CloudEndpointStatus.Checking && this.cloudEndpointCheckingBlink)
            {
                return DesignTokens.Colors.Success;
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

        return candidates.ToArray();
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
        return Math.Max(1.0f, S(2));
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

    private string GetPingDiagnosisTextSuffix()
    {
        PingRollingSnapshot rolling = this.snapshot == null ? null : this.snapshot.PingRolling;
        if (rolling == null || rolling.Diagnosis == PingPathDiagnosis.None || string.IsNullOrWhiteSpace(rolling.DiagnosisText))
        {
            return string.Empty;
        }

        return " | " + rolling.DiagnosisText.Trim();
    }

    private float GetDnsAlertStripWidth(Graphics g, float rowHeight)
    {
        DnsAlertCandidate[] candidates = GetDnsAlertCandidates();
        if (candidates.Length == 0)
        {
            return 0.0f;
        }

        float cloudWidth = GetCloudEndpointTileStripWidth(rowHeight);
        float textWidth = GetMaxDnsAlertTextWidth(g, candidates, rowHeight);
        float desired = Math.Max(Math.Max(cloudWidth, textWidth + S(10)), S(76));
        float maxWidth = Math.Max(S(76), this.Width - S(92));
        return Math.Min(desired, maxWidth);
    }

    private float GetMaxDnsAlertTextWidth(Graphics g, DnsAlertCandidate[] candidates, float rowHeight)
    {
        if (g == null || candidates == null || candidates.Length == 0)
        {
            return 0.0f;
        }

        Font font = GetDnsAlertFont(rowHeight);
        float width = 0.0f;
        for (int i = 0; i < candidates.Length; i++)
        {
            string text = candidates[i] == null ? string.Empty : candidates[i].Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            width = Math.Max(width, g.MeasureString(text, font).Width);
        }

        return width;
    }

    private Font GetDnsAlertFont(float rowHeight)
    {
        return GetCachedUiFont(Math.Max(7.5f, rowHeight * 0.50f), FontStyle.Bold);
    }

    private void DrawDnsAlertStrip(Graphics g, RectangleF rect, float rowHeight)
    {
        DnsAlertCandidate[] candidates = GetDnsAlertCandidates();
        if (candidates.Length == 0 || rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        int index = Math.Max(0, Math.Min(this.dnsAlertIndex, candidates.Length - 1));
        DnsAlertCandidate candidate = candidates[index];
        Font font = GetDnsAlertFont(rowHeight);
        using (SolidBrush brush = new SolidBrush(candidate.Color))
        {
            DrawFittedText(g, candidate.Text, font, brush, rect, StringAlignment.Far);
        }
    }

    private void DrawDnsAlertOverlay(Graphics g, float rowTop, float rowHeight)
    {
        RectangleF rect = GetDnsAlertOverlayRect(g, rowTop, rowHeight);
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        DrawDnsAlertStrip(g, rect, rowHeight);
    }

    private RectangleF GetDnsAlertOverlayRect(Graphics g, float rowTop, float rowHeight)
    {
        float width = GetDnsAlertStripWidth(g, rowHeight);
        if (width <= 0.0f)
        {
            return RectangleF.Empty;
        }

        // DNS status is a top-layer badge aligned with the GFW row. It must not
        // change the GFW row's layout; only the IP4 cloud tiles reserve row width.
        return new RectangleF(
            Math.Max(S(10), this.Width - S(10) - width),
            rowTop + DnsAlertOverlayRowIndex * rowHeight,
            width,
            rowHeight);
    }

    private bool AdvanceDnsAlertRotation()
    {
        DnsAlertCandidate[] candidates = GetDnsAlertCandidates();
        if (candidates.Length == 0)
        {
            bool hadAlert = !string.IsNullOrEmpty(this.dnsAlertSignature);
            this.dnsAlertSignature = string.Empty;
            this.dnsAlertIndex = 0;
            this.dnsAlertCandidateKey = string.Empty;
            return hadAlert;
        }

        string signature = BuildDnsAlertSignature(candidates);
        bool signatureChanged = !string.Equals(signature, this.dnsAlertSignature, StringComparison.Ordinal);
        if (signatureChanged)
        {
            this.dnsAlertSignature = signature;
        }

        if (candidates.Length <= 1)
        {
            bool changed = signatureChanged || this.dnsAlertIndex != 0;
            this.dnsAlertIndex = 0;
            this.dnsAlertCandidateKey = candidates[0].Key;
            return changed;
        }

        if (string.IsNullOrEmpty(this.dnsAlertCandidateKey))
        {
            this.dnsAlertIndex = Math.Max(0, Math.Min(this.dnsAlertIndex, candidates.Length - 1));
            this.dnsAlertCandidateKey = candidates[this.dnsAlertIndex].Key;
            return true;
        }

        int previousIndex = FindDnsAlertCandidateIndex(candidates, this.dnsAlertCandidateKey);
        int baseIndex = previousIndex >= 0
            ? previousIndex
            : Math.Max(0, Math.Min(this.dnsAlertIndex, candidates.Length - 1));
        this.dnsAlertIndex = (baseIndex + 1) % candidates.Length;
        this.dnsAlertCandidateKey = candidates[this.dnsAlertIndex].Key;
        return true;
    }

    private static int FindDnsAlertCandidateIndex(DnsAlertCandidate[] candidates, string key)
    {
        if (candidates == null || candidates.Length == 0 || string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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

    private static string BuildDnsAlertSignature(DnsAlertCandidate[] candidates)
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
            builder.Append(candidates[i].Text);
            builder.Append(":");
            builder.Append(candidates[i].Status.ToString());
        }

        return builder.ToString();
    }

    private void DrawInfoRow(Graphics g, int row, string label, string value, float rowTop, float rowHeight, Color valueColor)
    {
        float y = rowTop + row * rowHeight;
        float labelLeft = S(10);
        RectangleF labelRect = new RectangleF(labelLeft, y, S(42), rowHeight);
        bool drawCloudTiles = string.Equals(label, "IP4", StringComparison.Ordinal);
        float rightWidth = GetInfoRowReservedRightWidth(label, rowHeight);
        float rightGap = rightWidth > 0.0f ? S(4) : 0.0f;
        float valueWidth = Math.Max(S(28), this.Width - labelRect.Right - S(13) - rightWidth - rightGap);
        RectangleF valueRect = new RectangleF(labelRect.Right + S(3), y, valueWidth, rowHeight);
        Font labelFont = GetCachedUiFont(Math.Max(8.0f, rowHeight * 0.52f), FontStyle.Bold);
        Font valueFont = GetCachedUiFont(Math.Max(8.5f, rowHeight * 0.58f), FontStyle.Bold);
        if (string.Equals(label, "IP4", StringComparison.Ordinal))
        {
            value = BuildMeasuredAddressRowText(g, value, valueFont, valueRect.Width, 15);
        }
        else if (string.Equals(label, "IP6", StringComparison.Ordinal))
        {
            value = BuildMeasuredAddressRowText(g, value, valueFont, valueRect.Width, 24);
        }

        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (SolidBrush valueBrush = new SolidBrush(valueColor))
        {
            DrawFittedText(g, label, labelFont, labelBrush, labelRect, StringAlignment.Near);
            DrawFittedText(g, value, valueFont, valueBrush, valueRect, StringAlignment.Near);
        }

        if (drawCloudTiles && rightWidth > 0.0f)
        {
            RectangleF cloudRect = new RectangleF(Math.Max(S(10), this.Width - S(10) - rightWidth), y, rightWidth, rowHeight);
            DrawCloudEndpointTiles(g, cloudRect, GetDisplayAccessState());
        }
    }

    private float GetInfoRowReservedRightWidth(string label, float rowHeight)
    {
        if (string.Equals(label, "IP4", StringComparison.Ordinal))
        {
            return GetCloudEndpointTileStripWidth(rowHeight);
        }

        return 0.0f;
    }

    private void DrawDnsRow(Graphics g, int row, float rowTop, float rowHeight)
    {
        float y = rowTop + row * rowHeight;
        float labelLeft = S(10);
        RectangleF labelRect = new RectangleF(labelLeft, y, S(42), rowHeight);
        RectangleF valueRect = new RectangleF(labelRect.Right + S(3), y, this.Width - labelRect.Right - S(13), rowHeight);
        Font labelFont = GetCachedUiFont(Math.Max(8.0f, rowHeight * 0.52f), FontStyle.Bold);
        Font valueFont = GetCachedUiFont(Math.Max(8.5f, rowHeight * 0.58f), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        {
            DrawFittedText(g, "DNS", labelFont, labelBrush, labelRect, StringAlignment.Near);
        }

        DnsDisplaySegment[] segments = BuildDnsDisplaySegments();
        DrawDnsSegments(g, segments, valueFont, valueRect);
    }

    private void DrawDnsSegments(Graphics g, DnsDisplaySegment[] segments, Font baseFont, RectangleF rect)
    {
        if (segments == null || segments.Length == 0)
        {
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            {
                DrawFittedText(g, "--", baseFont, brush, rect, StringAlignment.Near);
            }

            return;
        }

        Font drawFont = baseFont;
        float size = baseFont.Size;
        float totalWidth = MeasureDnsSegments(g, segments, drawFont);
        while (size > 7.0f * this.scale && totalWidth > rect.Width)
        {
            size -= 0.7f * this.scale;
            drawFont = GetCachedUiFont(size, baseFont.Style);
            totalWidth = MeasureDnsSegments(g, segments, drawFont);
        }

        RectangleF clip = rect;
        Region oldClip = g.Clip;
        try
        {
            g.SetClip(clip);
            float x = rect.Left;
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.None;
                format.FormatFlags = StringFormatFlags.NoWrap;
                for (int i = 0; i < segments.Length; i++)
                {
                    DnsDisplaySegment segment = segments[i];
                    if (segment == null || string.IsNullOrEmpty(segment.Text))
                    {
                        continue;
                    }

                    float width = g.MeasureString(segment.Text, drawFont).Width;
                    using (SolidBrush brush = new SolidBrush(segment.Color))
                    {
                        g.DrawString(segment.Text, drawFont, brush, new RectangleF(x, rect.Top, width + S(2), rect.Height), format);
                    }

                    x += width;
                    if (x >= rect.Right)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            g.Clip = oldClip;
            oldClip.Dispose();
        }
    }

    private float MeasureDnsSegments(Graphics g, DnsDisplaySegment[] segments, Font font)
    {
        float width = 0.0f;
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] != null && !string.IsNullOrEmpty(segments[i].Text))
            {
                width += g.MeasureString(segments[i].Text, font).Width;
            }
        }

        return width;
    }

    private DnsDisplaySegment[] BuildDnsDisplaySegments()
    {
        DnsDisplayItem[] items = BuildDnsDisplayItems();
        if (items.Length == 0)
        {
            return new DnsDisplaySegment[]
            {
                new DnsDisplaySegment { Text = "--", Color = DesignTokens.Colors.GlyphMuted }
            };
        }

        int visibleCount = Math.Min(3, items.Length);
        List<DnsDisplaySegment> segments = new List<DnsDisplaySegment>();
        for (int i = 0; i < visibleCount; i++)
        {
            segments.Add(new DnsDisplaySegment
            {
                Text = (i == 0 ? string.Empty : ", ") + EmptyToDash(items[i].Address),
                Color = GetDnsStatusColor(items[i].Status)
            });
        }

        int hiddenCount = items.Length - visibleCount;
        if (hiddenCount > 0)
        {
            DnsServerStatus worstHidden = DnsServerStatus.Normal;
            for (int i = visibleCount; i < items.Length; i++)
            {
                if (GetDnsStatusPriority(items[i].Status) > GetDnsStatusPriority(worstHidden))
                {
                    worstHidden = items[i].Status;
                }
            }

            segments.Add(new DnsDisplaySegment
            {
                Text = " +" + hiddenCount.ToString(CultureInfo.InvariantCulture),
                Color = GetDnsStatusColor(worstHidden)
            });
        }

        return segments.ToArray();
    }

    private DnsDisplayItem[] BuildDnsDisplayItems()
    {
        DnsServerSnapshot[] details = this.snapshot == null ? null : this.snapshot.DnsServerDetails;
        return BuildDnsDisplayItems(details);
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
            return "需要验证 | " + EmptyToDash(this.snapshot.AccessReason) + GetPingDiagnosisTextSuffix();
        }

        if (accessState == NetworkAccessState.Offline)
        {
            string reason = EmptyToDash(this.snapshot.AccessReason);
            if (reason == "--")
            {
                reason = "loss " + Math.Max(0, this.snapshot.PacketLossPercent).ToString(CultureInfo.InvariantCulture) + "%";
            }

            return "FAIL " + NetworkMonitorTarget() + " | " + reason + GetPingDiagnosisTextSuffix();
        }

        if (accessState == NetworkAccessState.AdapterMissing)
        {
            return "网卡未识别 | " + EmptyToDash(this.snapshot.AccessReason) + GetPingDiagnosisTextSuffix();
        }

        PingRollingSnapshot rolling = this.snapshot.PingRolling;
        if (rolling != null)
        {
            string profile = EmptyToDash(rolling.ActiveProfile);
            if (profile == "--")
            {
                profile = NetworkMonitorTarget();
            }

            if (rolling.IcmpBlocked)
            {
                return "FAIL " + profile + " | ICMP不可用" + GetPingDiagnosisTextSuffix();
            }

            if (!rolling.StatsReady)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} | 采样中 ({2}/{3})",
                    this.snapshot.ConnectivityOnline ? "OK" : "FAIL",
                    profile,
                    Math.Max(0, rolling.SampleCount),
                    RollingPingMinSamplesForDisplay()) + GetPingDiagnosisTextSuffix();
            }

            string jitter = rolling.JitterKnown
                ? Math.Max(0.0, rolling.JitterMs).ToString("0", CultureInfo.InvariantCulture) + "ms"
                : "--";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} | {2:0}ms | jitter {3} | loss {4:0.0}% ({5}/{6})",
                this.snapshot.ConnectivityOnline ? "OK" : "FAIL",
                profile,
                Math.Max(0.0, rolling.LatencyMs),
                jitter,
                Math.Max(0.0, Math.Min(100.0, rolling.LossPercent)),
                Math.Max(0, rolling.LostCount),
                Math.Max(0, rolling.SampleCount)) + GetPingDiagnosisTextSuffix();
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
        if (this.snapshot != null && this.snapshot.PingRolling != null && !string.IsNullOrWhiteSpace(this.snapshot.PingRolling.ActiveProfile))
        {
            return this.snapshot.PingRolling.ActiveProfile.Trim();
        }

        return this.snapshot == null ? "1.1.1.1" : EmptyToDash(this.snapshot.ConnectivityTarget);
    }

    private static int RollingPingMinSamplesForDisplay()
    {
        return 10;
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
        return GetDisplayAccessState(this.snapshot);
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

    private string BuildPublicAddressText(string prefix)
    {
        return EmptyToDash(prefix) + " " + BuildPublicAddressValue();
    }

    private string BuildPublicAddressValue()
    {
        string publicIpv6;
        if (TryGetPrimaryPublicIpv6(this.snapshot == null ? null : this.snapshot.IPv6, out publicIpv6))
        {
            return CompactIpAddressForDisplay(publicIpv6, 16);
        }

        if (this.snapshot != null && this.snapshot.PublicIpRefreshing && !this.snapshot.PublicIpKnown)
        {
            return "...";
        }

        return EmptyToDash(this.snapshot == null ? null : this.snapshot.PublicIp);
    }

    private bool HasPublicAddressDisplayValue()
    {
        string publicIpv6;
        if (TryGetPrimaryPublicIpv6(this.snapshot == null ? null : this.snapshot.IPv6, out publicIpv6))
        {
            return true;
        }

        return this.snapshot != null && this.snapshot.PublicIpKnown;
    }

    private string BuildMeasuredAddressRowText(Graphics g, string value, Font font, float maxWidth, int maxAddressLength)
    {
        string text = BuildSingleAddressRowText(value, int.MaxValue);
        if (DoesTextFit(g, text, font, maxWidth))
        {
            return text;
        }

        text = BuildSingleAddressRowText(value, maxAddressLength);
        if (DoesTextFit(g, text, font, maxWidth))
        {
            return text;
        }

        for (int length = Math.Max(8, maxAddressLength - 2); length >= 8; length -= 2)
        {
            text = BuildSingleAddressRowText(value, length);
            if (DoesTextFit(g, text, font, maxWidth))
            {
                return text;
            }
        }

        return text;
    }

    private static bool DoesTextFit(Graphics g, string text, Font font, float maxWidth)
    {
        if (g == null || font == null || maxWidth <= 0.0f)
        {
            return false;
        }

        return g.MeasureString(text ?? string.Empty, font).Width <= maxWidth;
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

    private static bool TryGetPrimaryPublicIpv6(string value, out string publicIpv6)
    {
        publicIpv6 = string.Empty;
        List<string> addresses = ExtractAddressList(value);
        for (int i = 0; i < addresses.Count; i++)
        {
            IPAddress address;
            if (IPAddress.TryParse(addresses[i], out address) && IsPublicRoutableIpv6(address))
            {
                publicIpv6 = address.ToString();
                return true;
            }
        }

        return false;
    }

    private static bool IsPublicRoutableIpv6(IPAddress address)
    {
        if (address == null || address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes == null || bytes.Length == 0 || bytes[0] == 0)
        {
            return false;
        }

        // fc00::/7 is ULA. It is valid on a local network but should not be
        // shown as the public address in the compact header.
        return (bytes[0] & 0xFE) != 0xFC;
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
            while (size > 7.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                size -= 0.7f * this.scale;
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
            HasSamePingRollingData(left.PingRolling, right.PingRolling);
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
                !string.Equals(leftEndpoint.Key, rightEndpoint.Key, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.ShortLabel, rightEndpoint.ShortLabel, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.DisplayName, rightEndpoint.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.AlertReason, rightEndpoint.AlertReason, StringComparison.Ordinal) ||
                !string.Equals(leftEndpoint.AlertName, rightEndpoint.AlertName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
            bool burnInColorProtectionActive = IsBurnInColorProtectionActive();
            bool refreshNativeBitmap =
                redrawContent ||
                !this.renderBufferValid ||
                burnInColorProtectionActive != this.lastRenderedBurnInColorProtectionActive;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawNetworkMonitorWindow(this.renderGraphics);
                if (burnInColorProtectionActive)
                {
                    BurnInProtection.ApplyHiddenModeColorProtection(this.renderBitmap);
                }

                this.lastRenderedBurnInColorProtectionActive = burnInColorProtectionActive;
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

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(
            this.currentSettings,
            IsHoverOpacityTargetActive());
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

    // Mono counterpart of GetCachedUiFont, needed by the AmberHud/Phosphor OLED-safe restyle
    // schemes (added in 1.0.3.44). Shares the same cache dictionary; the style+size key does not
    // collide with UI-font entries because callers never request the exact same normalized
    // size+style pair for both families in practice, and correctness does not depend on that anyway
    // since this uses its own "M:" prefix.
    private Font GetCachedMonoFont(float size, FontStyle style)
    {
        float normalizedSize = Math.Max(1.0f, (float)Math.Round(size, 2));
        string key = "M:" + ((int)style).ToString(CultureInfo.InvariantCulture) + ":" +
            normalizedSize.ToString("0.00", CultureInfo.InvariantCulture);
        Font font;
        if (!this.fontCache.TryGetValue(key, out font))
        {
            font = DesignTokens.CreateMonoFont(normalizedSize, style, GraphicsUnit.Pixel);
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
        if (!IsHoverOpacityRuntimeEnabled() || this.hoverOpacityProgress <= 0.0)
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

    internal static void RunNetworkMonitorDisplaySelfTest()
    {
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

        string publicIpv6;
        if (!TryGetPrimaryPublicIpv6("fd00::1234, 2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890", out publicIpv6) ||
            publicIpv6.IndexOf("2406:da18", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Network monitor display self-test: public IPv6 priority failed.");
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

        using (NetworkMonitorForm form = new NetworkMonitorForm(new WidgetSettings()))
        {
            form.snapshot = new NetworkMonitorSnapshot
            {
                IPv6 = "fd00::1234, 2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890",
                PublicIp = "203.0.113.10",
                PublicIpKnown = true
            };
            string publicText = form.BuildPublicAddressText("公网");
            if (publicText.IndexOf("203.0.113.10", StringComparison.Ordinal) >= 0 ||
                publicText.IndexOf("2406:", StringComparison.Ordinal) < 0 ||
                publicText.Length > 22)
            {
                throw new InvalidOperationException("Network monitor display self-test: compact public IPv6 must override IPv4.");
            }

            form.Width = 360;
            form.snapshot = new NetworkMonitorSnapshot { DnsServerDetails = duplicateDns };
            float testRowTop = 24.0f;
            float testRowHeight = 18.0f;
            using (Bitmap testBitmap = new Bitmap(form.Width, 120))
            using (Graphics testGraphics = Graphics.FromImage(testBitmap))
            {
                RectangleF dnsOverlay = form.GetDnsAlertOverlayRect(testGraphics, testRowTop, testRowHeight);
                if (dnsOverlay.IsEmpty ||
                    Math.Abs(dnsOverlay.Top - (testRowTop + DnsAlertOverlayRowIndex * testRowHeight)) > 0.01f)
                {
                    throw new InvalidOperationException("Network monitor display self-test: DNS alert overlay must align with the GFW row.");
                }

                if (form.GetInfoRowReservedRightWidth("GFW", testRowHeight) != 0.0f ||
                    form.GetInfoRowReservedRightWidth("IP6", testRowHeight) != 0.0f)
                {
                    throw new InvalidOperationException("Network monitor display self-test: DNS overlay must not change GFW or IP6 row layout width.");
                }

                form.snapshot = new NetworkMonitorSnapshot
                {
                    DnsServerDetails = new DnsServerSnapshot[]
                    {
                        new DnsServerSnapshot { Address = "1.1.1.1", Status = DnsServerStatus.Problem, Reason = "返回 SERVFAIL" }
                    }
                };
                RectangleF shortAlertOverlay = form.GetDnsAlertOverlayRect(testGraphics, testRowTop, testRowHeight);
                Font shortAlertFont = form.GetDnsAlertFont(testRowHeight);
                float requiredShortAlertWidth = testGraphics.MeasureString("DNS返回SERVFAIL", shortAlertFont).Width;
                if (shortAlertOverlay.Width < requiredShortAlertWidth)
                {
                    throw new InvalidOperationException("Network monitor display self-test: DNS short alert text must have enough overlay width.");
                }

                Font addressFont = form.GetCachedUiFont(10.0f, FontStyle.Bold);
                string measuredFullIpv6 = form.BuildMeasuredAddressRowText(
                    testGraphics,
                    "2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, fd00::1",
                    addressFont,
                    1000.0f,
                    24);
                if (measuredFullIpv6.IndexOf("2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890", StringComparison.Ordinal) < 0 ||
                    measuredFullIpv6.IndexOf("+1", StringComparison.Ordinal) < 0 ||
                    measuredFullIpv6.IndexOf(",", StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidOperationException("Network monitor display self-test: measured IPv6 row must keep one full address when it fits.");
                }

                string measuredCompactIpv6 = form.BuildMeasuredAddressRowText(
                    testGraphics,
                    "2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, fd00::1",
                    addressFont,
                    90.0f,
                    24);
                if (measuredCompactIpv6.IndexOf("2406:da18:7c3:8f00", StringComparison.Ordinal) >= 0 ||
                    measuredCompactIpv6.IndexOf("…", StringComparison.Ordinal) < 0 ||
                    measuredCompactIpv6.IndexOf("+1", StringComparison.Ordinal) < 0 ||
                    measuredCompactIpv6.IndexOf(",", StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidOperationException("Network monitor display self-test: measured IPv6 row must compact only the first address when width is tight.");
                }
            }

            form.snapshot = new NetworkMonitorSnapshot { DnsServerDetails = duplicateDns };
            if (!form.AdvanceDnsAlertRotation() || form.dnsAlertIndex != 0)
            {
                throw new InvalidOperationException("Network monitor display self-test: DNS alert rotation initial index failed.");
            }

            form.snapshot = new NetworkMonitorSnapshot
            {
                DnsServerDetails = new DnsServerSnapshot[]
                {
                    new DnsServerSnapshot { Address = "1.1.1.1", Status = DnsServerStatus.Problem, Reason = "返回 SERVFAIL" },
                    new DnsServerSnapshot { Address = "8.8.8.8", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用" }
                }
            };
            if (!form.AdvanceDnsAlertRotation() || form.dnsAlertIndex != 1)
            {
                throw new InvalidOperationException("Network monitor display self-test: DNS alert rotation must advance through signature changes.");
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

    private sealed class DnsDisplaySegment
    {
        public string Text;
        public Color Color;
    }

    private sealed class DnsDisplayItem
    {
        public string Address;
        public DnsServerStatus Status;
        public DnsServerSnapshot Detail;
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
