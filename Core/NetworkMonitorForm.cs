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
internal sealed partial class NetworkMonitorForm : LayeredWidgetFormBase
{
    private const int RenderSecondBoundaryOffsetMs = 55;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly NetworkMonitorReader reader;
    private NetworkMonitorSnapshot snapshot;
    private bool hiddenForFullscreen;
    private bool cloudEndpointCheckingBlink;
    private string cloudEndpointAlertSignature = string.Empty;
    private int cloudEndpointAlertIndex;
    private bool cloudEndpointAlertNamePhase = true;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime reverseHoverRevealUntilUtc;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool autoHideKeepAliveActive;
    private bool sharedInteractionPolling;
    private Bitmap contentBitmap;
    private Graphics contentGraphics;
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
        this.MinimumSize = ScaleWindowSize(new Size(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MinNetworkMonitorHeight));
        this.MaximumSize = ScaleWindowSize(new Size(WidgetSettings.MaxNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorHeight));
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextRenderTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetNetworkIdlePollingIntervalMs(this.CurrentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.CurrentSettings);
        this.snapshot = this.reader.GetSnapshot(this.CurrentSettings);
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
        DisposeRenderBuffer();
        DisposeFontCache();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Font sizes and bitmap dimensions are layout-dependent; clearing both caches
        // prevents stale dimensions and unbounded font growth during repeated resizing.
        DisposeRenderBuffer();
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
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.MinimumSize = ScaleWindowSize(new Size(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MinNetworkMonitorHeight));
        this.MaximumSize = ScaleWindowSize(new Size(WidgetSettings.MaxNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorHeight));
        ApplyPerformanceTimerIntervals();
        this.snapshot = this.reader.GetSnapshot(this.CurrentSettings);

        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.CurrentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();
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

            if (!this.hiddenForFullscreen && this.Visible && (displayChanged || sizeChanged || positionChanged || blinkChanged || alertChanged))
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

        int hoverInterval = WidgetSettings.GetNetworkIdlePollingIntervalMs(this.CurrentSettings.PerformanceMode);
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
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.CurrentSettings.PerformanceMode);
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
            ? WidgetSettings.GetHoverAnimationIntervalMs(this.CurrentSettings.PerformanceMode)
            : WidgetSettings.GetNetworkIdlePollingIntervalMs(this.CurrentSettings.PerformanceMode);
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

    public void SetAutoHideKeepAliveActive(bool active)
    {
        if (this.autoHideKeepAliveActive == active)
        {
            return;
        }

        this.autoHideKeepAliveActive = active;
        if (active)
        {
            this.hoverOpacityDelayState.Reset();
            this.reverseHoverRevealUntilUtc = DateTime.MinValue;
        }
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
            this.CurrentSettings,
            this.Bounds,
            this.hiddenForFullscreen,
            this.Visible,
            ref this.reverseHoverRevealUntilUtc,
            this.hoverOpacityDelayState,
            this.autoHideKeepAliveActive);
    }

    private bool IsHoverOpacityRuntimeEnabled()
    {
        return this.CurrentSettings.HoverOpacityEnabled || this.CurrentSettings.ForceHoverOpacityActive;
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
            this.CurrentSettings.ClickThroughMode,
            this.CurrentSettings.VisibilityMode);
    }

    private bool NeedsClickThroughPolling()
    {
        return WidgetSettings.ShouldEnableClickThrough(
            this.CurrentSettings.ClickThroughMode,
            this.CurrentSettings.VisibilityMode);
    }

    private void PositionNetworkMonitorWindow()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleNetworkMonitor);
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int baseWidth = GetNetworkMonitorAnchorWidth();
        int mappedLeft = this.CurrentSettings.MapResolutionCompatibilityLeft(WidgetSettings.ModuleNetworkMonitor, workArea, this.CurrentSettings.NetworkMonitorLeftX);
        int baseRight = mappedLeft + this.CurrentSettings.ScaleResolutionCompatibilityPixels(baseWidth);
        baseRight = Math.Max(workArea.Left + this.Width, Math.Min(baseRight, workArea.Right));
        int left = Math.Max(workArea.Left, Math.Min(baseRight - this.Width, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinNetworkMonitorHeight, this.CurrentSettings.NetworkMonitorHeight);
        int mappedBottom = this.CurrentSettings.MapResolutionCompatibilityBottom(WidgetSettings.ModuleNetworkMonitor, workArea, this.CurrentSettings.NetworkMonitorBottomY);
        int top = mappedBottom - this.CurrentSettings.ScaleResolutionCompatibilityPixels(baseHeight) + 1;
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
            GetLayeredWidgetInsertAfter(this.CurrentSettings.VisibilityMode, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
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
        int width = GetEffectiveNetworkMonitorWidth();
        return ScaleWindowSize(new Size(width, this.CurrentSettings.NetworkMonitorHeight));
    }

    private int GetNetworkMonitorAnchorWidth()
    {
        if (this.CurrentSettings.NetworkMonitorRenderVariant != NetworkMonitorRenderVariant.Classic)
        {
            return Math.Max(WidgetSettings.MinNetworkMonitorWidth, this.CurrentSettings.NetworkMonitorWidth);
        }

        return Math.Min(
            ClassicStripLayout.WidthMaximum,
            Math.Max(ClassicStripLayout.WidthCompact, this.CurrentSettings.NetworkMonitorWidth));
    }

    private int GetEffectiveNetworkMonitorWidth()
    {
        if (this.CurrentSettings.NetworkMonitorRenderVariant != NetworkMonitorRenderVariant.Classic)
        {
            return this.CurrentSettings.NetworkMonitorWidth;
        }

        int baseWidth = Math.Max(ClassicStripLayout.WidthCompact, this.CurrentSettings.NetworkMonitorWidth);
        int targetWidth = Math.Max(baseWidth, GetClassicStripContentWidthPreset());
        return Math.Min(ClassicStripLayout.WidthMaximum, targetWidth);
    }

    private int GetClassicStripContentWidthPreset()
    {
        // The strip can grow only through these preset levels. This keeps the hot UI path cheap
        // and deterministic: no network/disk work and no per-frame pixel probing, just snapshot
        // strings that are already needed for painting. The hard cap preserves the approved 628px
        // maximum while keeping the 520px compact shape for normal content.
        int targetWidth = ClassicStripLayout.WidthCompact;
        RaiseClassicStripAutoWidth(ref targetWidth, BuildClassicStripLinkSummary(), 42, 49, 56);
        RaiseClassicStripAutoWidth(ref targetWidth, GetHeaderStatusText(GetDisplayAccessState()), 12, 14, 16);
        RaiseClassicStripAutoWidth(ref targetWidth, BuildSingleAddressRowText(this.snapshot == null ? null : this.snapshot.IPv4, int.MaxValue), 30, 36, 42);
        RaiseClassicStripAutoWidth(ref targetWidth, BuildSingleAddressRowText(this.snapshot == null ? null : this.snapshot.IPv6, int.MaxValue), 50, 56, 62);
        RaiseClassicStripAutoWidth(ref targetWidth, "公网 " + BuildClassicStripPublicAddressValue(), 22, 25, 28);
        RaiseClassicStripAutoWidth(ref targetWidth, BuildClassicStripDnsSummaryForAutoWidth(), 32, 38, 44);

        DnsAlertCandidate dnsAlert = GetClassicStripDnsAlert();
        if (dnsAlert != null)
        {
            RaiseClassicStripAutoWidth(ref targetWidth, dnsAlert.Text, 15, 18, 21);
        }

        RaiseClassicStripAutoWidth(ref targetWidth, BuildClassicStripPingText(), 13, 16, 19);
        RaiseClassicStripAutoWidth(ref targetWidth, BuildClassicStripGfwText(), 16, 19, 22);
        return targetWidth;
    }

    private static void RaiseClassicStripAutoWidth(ref int targetWidth, string text, int width560Score, int width600Score, int width628Score)
    {
        int score = GetClassicStripAutoWidthScore(text);
        if (score > width628Score)
        {
            targetWidth = Math.Max(targetWidth, ClassicStripLayout.WidthMaximum);
            return;
        }

        if (score > width600Score)
        {
            targetWidth = Math.Max(targetWidth, ClassicStripLayout.WidthExpanded);
            return;
        }

        if (score > width560Score)
        {
            targetWidth = Math.Max(targetWidth, ClassicStripLayout.WidthMedium);
        }
    }

    private static int GetClassicStripAutoWidthScore(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int score = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            score += c > 0x7F ? 2 : 1;
        }

        return score;
    }

    private string BuildClassicStripDnsSummaryForAutoWidth()
    {
        DnsDisplayItem[] items = BuildDnsDisplayItems();
        if (items.Length == 0)
        {
            return "--";
        }

        int visibleCount = Math.Min(2, items.Length);
        List<string> parts = new List<string>();
        for (int i = 0; i < visibleCount; i++)
        {
            parts.Add(EmptyToDash(items[i].Address));
        }

        int hiddenCount = items.Length - visibleCount;
        if (hiddenCount > 0)
        {
            parts.Add("+" + hiddenCount.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" , ", parts.ToArray());
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

    protected override void DrawWindowContent(Graphics g)
    {
        DrawNetworkMonitorWindow(g);
    }

    protected override bool IsLayeredBurnInColorProtectionActive()
    {
        return IsBurnInColorProtectionActive();
    }

    private void ConfigureGraphics(Graphics g)
    {
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
    }

    private void DrawBackground(Graphics g)
    {
        ConfigureGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        Color backgroundColor = Color.FromArgb(15, 15, 19);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(backgroundColor, alpha)))
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

    // Render-variant dispatch (mirrors CodexRadarForm). Paint-only variants share the same snapshot
    // and reader; adding a layout must not add network or disk I/O to the draw path.
    private void DrawContent(Graphics g)
    {
        DrawContentClassic(g);
    }

    // Flat reference layout promoted to NetworkMonitorRenderVariant.Classic. Horizontal coordinates
    // are normalized from the compact 520px runtime width; vertical coordinates keep the original
    // 294px reference so width compression never changes row height, font size or vertical spacing.
    private static class ClassicStripLayout
    {
        public const int WidthCompact = 520;
        public const int WidthMedium = 560;
        public const int WidthExpanded = 600;
        public const int WidthMaximum = 628;
        public const float ReferenceWidth = WidthCompact;
        public const float Left = 12.0f / ReferenceWidth;
        public const float Right = 510.0f / ReferenceWidth;
        public const float TitleLeft = 12.0f / ReferenceWidth;
        public const float TitleRight = 88.0f / ReferenceWidth;
        public const float StatusLeft = 92.0f / ReferenceWidth;
        public const float StatusRight = 168.0f / ReferenceWidth;
        public const float LinkSummaryLeft = 172.0f / ReferenceWidth;
        public const float HeaderTop = 10.0f / 294.0f;
        public const float HeaderHeight = 42.0f / 294.0f;
        public const float DividerTop = 64.0f / 294.0f;
        public const float LabelLeft = 12.0f / ReferenceWidth;
        public const float ValueLeft = 52.0f / ReferenceWidth;
        public const float Ip4Top = 78.0f / 294.0f;
        public const float Ip6Top = 126.0f / 294.0f;
        public const float DnsTop = 174.0f / 294.0f;
        public const float InfoRowHeight = 36.0f / 294.0f;
        public const float RightModuleLeft = 334.0f / ReferenceWidth;
        public const float BottomDividerTop = 227.0f / 294.0f;
        public const float FooterTop = 239.0f / 294.0f;
        public const float FooterHeight = 40.0f / 294.0f;
        public const float PingLabelLeft = 10.0f / ReferenceWidth;
        public const float PingValueLeft = 54.0f / ReferenceWidth;
        public const float GfwLabelLeft = 160.0f / ReferenceWidth;
        public const float GfwValueLeft = 208.0f / ReferenceWidth;
        public const float CloudTilesLeft = 352.0f / ReferenceWidth;
        public const float CloudTilesRight = 510.0f / ReferenceWidth;
    }


    private void DrawContentClassic(Graphics g)
    {
        ConfigureGraphics(g);

        float w = this.Width;
        float h = this.Height;
        DrawClassicStripDividers(g, w, h);
        DrawClassicStripHeader(g, w, h);
        DrawClassicStripAddressRows(g, w, h);
        DrawClassicStripFooter(g, w, h);
        DrawClassicShellOutline(g);
    }

    private void DrawClassicStripDividers(Graphics g, float w, float h)
    {
        float left = ClassicStripLayout.Left * w;
        float right = ClassicStripLayout.Right * w;
        using (Pen divider = new Pen(Color.FromArgb(52, 52, 60), Math.Max(1.0f, S(1))))
        {
            g.DrawLine(divider, left, ClassicStripLayout.DividerTop * h, right, ClassicStripLayout.DividerTop * h);
            g.DrawLine(divider, left, ClassicStripLayout.BottomDividerTop * h, right, ClassicStripLayout.BottomDividerTop * h);
        }
    }

    private void DrawClassicStripHeader(Graphics g, float w, float h)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        string statusText = GetHeaderStatusText(accessState);
        Color statusColor = GetHeaderStatusColor(accessState);
        RectangleF titleRect = new RectangleF(
            ClassicStripLayout.TitleLeft * w,
            ClassicStripLayout.HeaderTop * h,
            (ClassicStripLayout.TitleRight - ClassicStripLayout.TitleLeft) * w,
            ClassicStripLayout.HeaderHeight * h);
        RectangleF statusRect = new RectangleF(
            ClassicStripLayout.StatusLeft * w,
            ClassicStripLayout.HeaderTop * h,
            (ClassicStripLayout.StatusRight - ClassicStripLayout.StatusLeft) * w,
            ClassicStripLayout.HeaderHeight * h);
        RectangleF summaryRect = new RectangleF(
            ClassicStripLayout.LinkSummaryLeft * w,
            ClassicStripLayout.HeaderTop * h,
            ClassicStripLayout.Right * w - ClassicStripLayout.LinkSummaryLeft * w,
            ClassicStripLayout.HeaderHeight * h);

        using (SolidBrush titleBrush = new SolidBrush(GetClassicStripNeutralTextColor()))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (SolidBrush summaryBrush = new SolidBrush(GetClassicStripNeutralTextColor()))
        {
            DrawFittedText(g, "NETWORK", GetClassicStripTitleFont(), titleBrush, titleRect, StringAlignment.Near);
            DrawFittedText(g, statusText, GetClassicStripHeaderStatusFont(), statusBrush, statusRect, StringAlignment.Near);
            DrawFittedText(g, BuildClassicStripLinkSummary(), GetClassicStripSubtleFont(), summaryBrush, summaryRect, StringAlignment.Far);
        }
    }

    private void DrawClassicStripAddressRows(Graphics g, float w, float h)
    {
        Font labelFont = GetClassicStripLabelFont();
        Font valueFont = GetClassicStripValueFont();
        float left = ClassicStripLayout.LabelLeft * w;
        float valueLeft = ClassicStripLayout.ValueLeft * w;
        float right = ClassicStripLayout.Right * w;
        float rightModuleLeft = ClassicStripLayout.RightModuleLeft * w;
        float rowHeight = ClassicStripLayout.InfoRowHeight * h;
        float labelWidth = Math.Max(28.0f, valueLeft - left - 4.0f);
        float rightGap = Math.Max(5.0f, w * (10.0f / ClassicStripLayout.ReferenceWidth));

        RectangleF publicRect = new RectangleF(
            rightModuleLeft,
            ClassicStripLayout.Ip4Top * h,
            Math.Max(50.0f, right - rightModuleLeft),
            rowHeight);
        RectangleF ip4ValueRect = new RectangleF(
            valueLeft,
            ClassicStripLayout.Ip4Top * h,
            Math.Max(40.0f, publicRect.Left - valueLeft - rightGap),
            rowHeight);
        RectangleF ip6ValueRect = new RectangleF(
            valueLeft,
            ClassicStripLayout.Ip6Top * h,
            Math.Max(40.0f, right - valueLeft),
            rowHeight);
        RectangleF dnsAlertRect = new RectangleF(
            rightModuleLeft,
            ClassicStripLayout.DnsTop * h,
            Math.Max(50.0f, right - rightModuleLeft),
            rowHeight);
        RectangleF dnsValueRect = new RectangleF(
            valueLeft,
            ClassicStripLayout.DnsTop * h,
            Math.Max(40.0f, dnsAlertRect.Left - valueLeft - rightGap),
            rowHeight);

        DrawClassicStripLabel(g, "IP4", labelFont, new RectangleF(left, ClassicStripLayout.Ip4Top * h, labelWidth, rowHeight));
        DrawClassicStripLabel(g, "IP6", labelFont, new RectangleF(left, ClassicStripLayout.Ip6Top * h, labelWidth, rowHeight));
        DrawClassicStripLabel(g, "DNS", labelFont, new RectangleF(left, ClassicStripLayout.DnsTop * h, labelWidth, rowHeight));

        string ip4 = BuildMeasuredAddressRowText(g, this.snapshot == null ? null : this.snapshot.IPv4, valueFont, ip4ValueRect.Width, 15);
        string ip6 = BuildMeasuredAddressRowText(g, this.snapshot == null ? null : this.snapshot.IPv6, valueFont, ip6ValueRect.Width, 24);
        using (SolidBrush valueBrush = new SolidBrush(GetClassicStripNeutralTextColor()))
        using (SolidBrush publicBrush = new SolidBrush(HasClassicStripPublicAddressValue() ? GetClassicStripNeutralTextColor() : DesignTokens.Colors.GlyphMuted))
        {
            DrawFittedText(g, ip4, valueFont, valueBrush, ip4ValueRect, StringAlignment.Near);
            DrawFittedText(g, ip6, valueFont, valueBrush, ip6ValueRect, StringAlignment.Near);
            DrawFittedText(g, "公网 " + BuildClassicStripPublicAddressValue(), valueFont, publicBrush, publicRect, StringAlignment.Far);
        }

        DrawClassicStripDnsSegments(g, valueFont, dnsValueRect);
        DnsAlertCandidate alert = GetClassicStripDnsAlert();
        if (alert != null)
        {
            using (SolidBrush alertBrush = new SolidBrush(GetClassicStripDnsStatusColor(alert.Status)))
            {
                DrawFittedText(g, alert.Text, valueFont, alertBrush, dnsAlertRect, StringAlignment.Far);
            }
        }
    }

    private void DrawClassicStripFooter(Graphics g, float w, float h)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        Font labelFont = GetClassicStripFooterFont();
        Font valueFont = GetClassicStripFooterFont();
        float y = ClassicStripLayout.FooterTop * h;
        float rowHeight = ClassicStripLayout.FooterHeight * h;
        float cloudLeft = ClassicStripLayout.CloudTilesLeft * w;
        float cloudRight = ClassicStripLayout.CloudTilesRight * w;
        RectangleF pingLabelRect = new RectangleF(
            ClassicStripLayout.PingLabelLeft * w,
            y,
            ClassicStripLayout.PingValueLeft * w - ClassicStripLayout.PingLabelLeft * w - 4.0f,
            rowHeight);
        RectangleF pingValueRect = new RectangleF(
            ClassicStripLayout.PingValueLeft * w,
            y,
            ClassicStripLayout.GfwLabelLeft * w - ClassicStripLayout.PingValueLeft * w - 4.0f,
            rowHeight);
        RectangleF gfwLabelRect = new RectangleF(
            ClassicStripLayout.GfwLabelLeft * w,
            y,
            ClassicStripLayout.GfwValueLeft * w - ClassicStripLayout.GfwLabelLeft * w - 4.0f,
            rowHeight);
        RectangleF gfwValueRect = new RectangleF(
            ClassicStripLayout.GfwValueLeft * w,
            y,
            cloudLeft - ClassicStripLayout.GfwValueLeft * w - 4.0f,
            rowHeight);
        RectangleF tilesRect = new RectangleF(cloudLeft, y + Math.Max(0.0f, rowHeight * 0.07f), Math.Max(0.0f, cloudRight - cloudLeft), Math.Max(10.0f, rowHeight * 0.86f));

        using (SolidBrush labelBrush = new SolidBrush(GetClassicStripNeutralTextColor()))
        using (SolidBrush pingBrush = new SolidBrush(GetConnectivityColor()))
        using (SolidBrush gfwBrush = new SolidBrush(GetGfwProbeColor()))
        {
            DrawFittedText(g, "PING", labelFont, labelBrush, pingLabelRect, StringAlignment.Near);
            DrawFittedText(g, BuildClassicStripPingText(), valueFont, pingBrush, pingValueRect, StringAlignment.Near);
            DrawFittedText(g, "GFW", labelFont, labelBrush, gfwLabelRect, StringAlignment.Near);
            DrawFittedText(g, BuildClassicStripGfwText(), valueFont, gfwBrush, gfwValueRect, StringAlignment.Near);
        }

        DrawCloudEndpointTiles(g, tilesRect, accessState);
    }

    private void DrawClassicStripLabel(Graphics g, string text, Font font, RectangleF rect)
    {
        using (SolidBrush brush = new SolidBrush(GetClassicStripNeutralTextColor()))
        {
            DrawFittedText(g, text, font, brush, rect, StringAlignment.Near);
        }
    }

    private static Color GetClassicStripNeutralTextColor()
    {
        return DesignTokens.White(206);
    }

    private Font GetClassicStripTitleFont()
    {
        return GetCachedUiFont(Math.Max(22.0f, S(12.0f)), FontStyle.Bold);
    }

    private Font GetClassicStripHeaderStatusFont()
    {
        return GetCachedUiFont(Math.Max(16.0f, S(8.8f)), FontStyle.Bold);
    }

    private Font GetClassicStripSubtleFont()
    {
        return GetCachedUiFont(Math.Max(13.0f, S(6.8f)), FontStyle.Bold);
    }

    private Font GetClassicStripLabelFont()
    {
        return GetCachedUiFont(Math.Max(15.0f, S(7.6f)), FontStyle.Bold);
    }

    private Font GetClassicStripValueFont()
    {
        return GetCachedUiFont(Math.Max(16.0f, S(8.2f)), FontStyle.Bold);
    }

    private Font GetClassicStripFooterFont()
    {
        return GetCachedUiFont(Math.Max(16.0f, S(8.4f)), FontStyle.Bold);
    }

    private string BuildClassicStripLinkSummary()
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

    private string BuildClassicStripPublicAddressValue()
    {
        if (this.snapshot != null && this.snapshot.PublicIpRefreshing && !this.snapshot.PublicIpKnown)
        {
            return "...";
        }

        if (this.snapshot != null && this.snapshot.PublicIpKnown)
        {
            return EmptyToDash(this.snapshot.PublicIp);
        }

        return BuildPublicAddressValue();
    }

    private bool HasClassicStripPublicAddressValue()
    {
        return (this.snapshot != null && this.snapshot.PublicIpKnown) || HasPublicAddressDisplayValue();
    }

    private void DrawClassicStripDnsSegments(Graphics g, Font baseFont, RectangleF rect)
    {
        DnsDisplayItem[] items = BuildDnsDisplayItems();
        if (items.Length == 0)
        {
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            {
                DrawFittedText(g, "--", baseFont, brush, rect, StringAlignment.Near);
            }

            return;
        }

        int visibleCount = Math.Min(2, items.Length);
        List<DnsDisplaySegment> segments = new List<DnsDisplaySegment>();
        for (int i = 0; i < visibleCount; i++)
        {
            if (i > 0)
            {
                segments.Add(new DnsDisplaySegment { Text = " , ", Color = GetClassicStripNeutralTextColor() });
            }

            segments.Add(new DnsDisplaySegment
            {
                Text = EmptyToDash(items[i].Address),
                Color = GetClassicStripDnsStatusColor(items[i].Status)
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
                Color = GetClassicStripDnsStatusColor(worstHidden)
            });
        }

        DrawDnsSegments(g, segments.ToArray(), baseFont, rect);
    }

    private DnsAlertCandidate GetClassicStripDnsAlert()
    {
        DnsDisplayItem[] items = BuildDnsDisplayItems();
        for (int i = 0; i < items.Length; i++)
        {
            DnsDisplayItem item = items[i];
            if (item == null || item.Detail == null || item.Status == DnsServerStatus.Normal)
            {
                continue;
            }

            return new DnsAlertCandidate
            {
                Key = GetDnsAlertCandidateKey(item.Detail, i),
                Address = item.Address,
                Text = "DNS" + GetDnsAlertReasonText(item.Detail),
                Status = item.Status,
                Color = GetClassicStripDnsStatusColor(item.Status)
            };
        }

        return null;
    }

    private static Color GetClassicStripDnsStatusColor(DnsServerStatus status)
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

    private string BuildClassicStripPingText()
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        if (accessState == NetworkAccessState.Online)
        {
            return this.snapshot != null && this.snapshot.LatencyMs > 0.0
                ? "OK PUB " + ((int)Math.Round(this.snapshot.LatencyMs)).ToString(CultureInfo.InvariantCulture) + "ms"
                : "OK PUB";
        }

        if (accessState == NetworkAccessState.NeedsValidation)
        {
            return "需要验证";
        }

        if (accessState == NetworkAccessState.Offline)
        {
            return "OFFLINE";
        }

        if (accessState == NetworkAccessState.AdapterMissing)
        {
            return "无网卡";
        }

        return "检测中";
    }

    private string BuildClassicStripGfwText()
    {
        string text = BuildCompactGfwText();
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw != null && gfw.CheckedAtKnown)
        {
            text += " " + gfw.CheckedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private void DrawClassicShellOutline(Graphics g)
    {
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }
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

    private string GetPingDiagnosisTextSuffix()
    {
        PingRollingSnapshot rolling = this.snapshot == null ? null : this.snapshot.PingRolling;
        if (rolling == null || rolling.Diagnosis == PingPathDiagnosis.None || string.IsNullOrWhiteSpace(rolling.DiagnosisText))
        {
            return string.Empty;
        }

        return " | " + rolling.DiagnosisText.Trim();
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

    private void DrawInfoRow(Graphics g, int row, string label, string value, float rowTop, float rowHeight, Color valueColor)
    {
        float y = rowTop + row * rowHeight;
        float labelLeft = S(10);
        Font labelFont = GetCachedUiFont(Math.Max(8.0f, rowHeight * 0.52f), FontStyle.Bold);
        Font valueFont = GetCachedUiFont(Math.Max(8.5f, rowHeight * 0.58f), FontStyle.Bold);
        float labelWidth = GetInfoRowLabelWidth(g, label, labelFont);
        float valueGap = GetInfoRowValueGap();
        RectangleF labelRect = new RectangleF(labelLeft, y, labelWidth, rowHeight);
        bool drawPublicAddress = string.Equals(label, "IP4", StringComparison.Ordinal);
        float rightWidth = GetInfoRowReservedRightWidth(g, label, valueFont, rowHeight);
        float rightGap = rightWidth > 0.0f ? S(4) : 0.0f;
        float valueWidth = Math.Max(S(28), this.Width - labelRect.Right - valueGap - S(10) - rightWidth - rightGap);
        RectangleF valueRect = new RectangleF(labelRect.Right + valueGap, y, valueWidth, rowHeight);
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

        if (drawPublicAddress && rightWidth > 0.0f)
        {
            RectangleF publicRect = new RectangleF(Math.Max(S(10), this.Width - S(10) - rightWidth), y, rightWidth, rowHeight);
            DrawPublicAddressModule(g, publicRect, valueFont);
        }
    }

    private float GetInfoRowLabelWidth(Graphics g, string label, Font labelFont)
    {
        float measured = g == null || labelFont == null
            ? S(18)
            : g.MeasureString(EmptyToDash(label), labelFont).Width + S(2);
        return Math.Min(S(34), Math.Max(S(18), measured));
    }

    private float GetInfoRowValueGap()
    {
        return S(2);
    }

    private float GetInfoRowReservedRightWidth(Graphics g, string label, Font valueFont, float rowHeight)
    {
        if (string.Equals(label, "IP4", StringComparison.Ordinal))
        {
            return GetPublicAddressStripWidth(g, valueFont, rowHeight);
        }

        return 0.0f;
    }

    private float GetPublicAddressStripWidth(Graphics g, Font font, float rowHeight)
    {
        Font drawFont = font ?? GetCachedUiFont(Math.Max(8.5f, rowHeight * 0.58f), FontStyle.Bold);
        float measured = g == null
            ? S(68)
            : g.MeasureString(BuildPublicAddressText("公网"), drawFont).Width + S(4);
        float minWidth = S(58);
        float maxWidth = Math.Max(minWidth, this.Width * 0.38f);
        return Math.Min(maxWidth, Math.Max(minWidth, measured));
    }

    private void DrawPublicAddressModule(Graphics g, RectangleF rect, Font font)
    {
        using (SolidBrush brush = new SolidBrush(HasPublicAddressDisplayValue() ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.GlyphMuted))
        {
            DrawFittedText(g, BuildPublicAddressText("公网"), font, brush, rect, StringAlignment.Far);
        }
    }

    private void DrawDnsRow(Graphics g, int row, float rowTop, float rowHeight)
    {
        float y = rowTop + row * rowHeight;
        float labelLeft = S(10);
        Font labelFont = GetCachedUiFont(Math.Max(8.0f, rowHeight * 0.52f), FontStyle.Bold);
        Font valueFont = GetCachedUiFont(Math.Max(8.5f, rowHeight * 0.58f), FontStyle.Bold);
        float labelWidth = GetInfoRowLabelWidth(g, "DNS", labelFont);
        float valueGap = GetInfoRowValueGap();
        RectangleF labelRect = new RectangleF(labelLeft, y, labelWidth, rowHeight);
        RectangleF valueRect = new RectangleF(labelRect.Right + valueGap, y, this.Width - labelRect.Right - valueGap - S(10), rowHeight);
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
        while (size > 7.0f * this.LayerScale && totalWidth > rect.Width)
        {
            size -= 0.7f * this.LayerScale;
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

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(
            this.CurrentSettings,
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

    protected override void DisposeAdditionalRenderBuffers()
    {
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
        return ComputeOpacityAlpha(this.CurrentSettings.NetworkMonitorTransparencyPercent);
    }

    private int GetContentOpacityAlpha()
    {
        return ComputeOpacityAlpha(this.CurrentSettings.ApplicationTransparencyPercent);
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.NetworkMonitorTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.NetworkMonitorScaleOverridePercent; }
    }

    protected override int ApplyHoverAlpha(int alpha)
    {
        return ApplyHoverTransparencyTarget(alpha);
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

        using (NetworkMonitorForm form = new NetworkMonitorForm(WidgetSettings.CreateDefaults()))
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
            float testRowHeight = 18.0f;
            using (Bitmap testBitmap = new Bitmap(form.Width, 120))
            using (Graphics testGraphics = Graphics.FromImage(testBitmap))
            {
                Font labelFont = form.GetCachedUiFont(Math.Max(8.0f, testRowHeight * 0.52f), FontStyle.Bold);
                float oldFixedLabelWidth = form.S(42);
                float ip4LabelWidth = form.GetInfoRowLabelWidth(testGraphics, "IP4", labelFont);
                if (ip4LabelWidth > oldFixedLabelWidth * 0.65f)
                {
                    throw new InvalidOperationException("Network monitor display self-test: IP row label gap must be tightened.");
                }

                Font addressFont = form.GetCachedUiFont(10.0f, FontStyle.Bold);
                if (form.GetInfoRowReservedRightWidth(testGraphics, "GFW", addressFont, testRowHeight) != 0.0f ||
                    form.GetInfoRowReservedRightWidth(testGraphics, "IP6", addressFont, testRowHeight) != 0.0f)
                {
                    throw new InvalidOperationException("Network monitor display self-test: only IP4 row may reserve a right-side module.");
                }

                float ip4ReservedWidth = form.GetInfoRowReservedRightWidth(testGraphics, "IP4", addressFont, testRowHeight);
                if (ip4ReservedWidth <= 0.0f)
                {
                    throw new InvalidOperationException("Network monitor display self-test: IP4 row must reserve public address module width.");
                }

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

        RunClassicStripLayoutSelfTest();
    }

    // The network layout is a fixed-canvas flat strip. These checks keep the reference
    // geometry and sample content from regressing.
    private static void RunClassicStripLayoutSelfTest()
    {

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.NetworkMonitorRenderVariant = NetworkMonitorRenderVariant.Classic;
        settings.NetworkMonitorWidth = 520;
        settings.NetworkMonitorHeight = 250;
        settings.NetworkMonitorTransparencyPercent = 0;
        settings.Normalize();
        using (NetworkMonitorForm form = new NetworkMonitorForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.Width = 520;
            form.Height = 250;
            form.snapshot = BuildSampleSnapshot();

            if (form.GetEffectiveNetworkMonitorWidth() != ClassicStripLayout.WidthCompact ||
                form.GetDesiredSize().Width != ClassicStripLayout.WidthCompact)
            {
                throw new InvalidOperationException("Network monitor display self-test: normal Classic strip content must stay at the compact width.");
            }

            NetworkMonitorSnapshot wideSnapshot = BuildSampleSnapshot();
            wideSnapshot.WifiDetails.Ssid = "VeryLongNetworkNameForClassicAutoWidthExpansion";
            wideSnapshot.IPv6 = "2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, 2606:4700:4700::1111";
            wideSnapshot.DnsServerDetails = new DnsServerSnapshot[]
            {
                new DnsServerSnapshot { Address = "2001:4860:4860::8888", Status = DnsServerStatus.Problem, Reason = "UDP失败/TCP可用" },
                new DnsServerSnapshot { Address = "2606:4700:4700::1111", Status = DnsServerStatus.Normal, Reason = "正常" },
                new DnsServerSnapshot { Address = "2620:fe::fe", Status = DnsServerStatus.Normal, Reason = "正常" }
            };
            wideSnapshot.GfwProbe = new GfwProbeSnapshot
            {
                Enabled = true,
                Status = GfwProbeStatus.SuspectedTlsSni,
                CheckedAtKnown = true,
                CheckedAtLocal = new DateTime(2026, 7, 8, 1, 23, 0),
                CloudEndpoints = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Normal)
            };
            form.snapshot = wideSnapshot;
            if (form.GetEffectiveNetworkMonitorWidth() != ClassicStripLayout.WidthMaximum ||
                form.GetDesiredSize().Width > ClassicStripLayout.WidthMaximum)
            {
                throw new InvalidOperationException("Network monitor display self-test: long Classic strip content must auto-expand to the 628px cap.");
            }

            form.snapshot = BuildSampleSnapshot();

            if (!string.Equals(form.BuildClassicStripLinkSummary(), "HomeNet-5G · WPA3 · 88% · 866M/433M", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network monitor display self-test: Classic strip link summary changed.");
            }

            if (!string.Equals(form.BuildClassicStripPublicAddressValue(), "203.0.113.10", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network monitor display self-test: Classic strip public module must use compact public IPv4 text.");
            }

            if (!string.Equals(form.BuildClassicStripPingText(), "OK PUB 18ms", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network monitor display self-test: Classic strip PING text changed.");
            }

            if (!string.Equals(form.BuildClassicStripGfwText(), "正常 00:00", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network monitor display self-test: Classic strip GFW text changed.");
            }

            DnsAlertCandidate dnsAlert = form.GetClassicStripDnsAlert();
            if (dnsAlert == null || !string.Equals(dnsAlert.Text, "DNS返回SERVFAIL", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Network monitor display self-test: Classic strip DNS alert changed.");
            }

            float w = form.Width;
            float h = form.Height;
            RectangleF ip4Value = new RectangleF(
                ClassicStripLayout.ValueLeft * w,
                ClassicStripLayout.Ip4Top * h,
                ClassicStripLayout.RightModuleLeft * w - ClassicStripLayout.ValueLeft * w - Math.Max(5.0f, w * (10.0f / ClassicStripLayout.ReferenceWidth)),
                ClassicStripLayout.InfoRowHeight * h);
            RectangleF ip6Value = new RectangleF(
                ClassicStripLayout.ValueLeft * w,
                ClassicStripLayout.Ip6Top * h,
                ClassicStripLayout.Right * w - ClassicStripLayout.ValueLeft * w,
                ClassicStripLayout.InfoRowHeight * h);
            RectangleF publicModule = new RectangleF(
                ClassicStripLayout.RightModuleLeft * w,
                ClassicStripLayout.Ip4Top * h,
                ClassicStripLayout.Right * w - ClassicStripLayout.RightModuleLeft * w,
                ClassicStripLayout.InfoRowHeight * h);
            RectangleF tiles = new RectangleF(
                ClassicStripLayout.CloudTilesLeft * w,
                ClassicStripLayout.FooterTop * h,
                (ClassicStripLayout.CloudTilesRight - ClassicStripLayout.CloudTilesLeft) * w,
                ClassicStripLayout.FooterHeight * h);
            if (ip4Value.IntersectsWith(publicModule) || tiles.Right > form.Width + 0.5f || tiles.Left <= 0.0f)
            {
                throw new InvalidOperationException("Network monitor display self-test: Classic strip modules must not collide.");
            }

            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                Font labelFont = form.GetClassicStripLabelFont();
                Font valueFont = form.GetClassicStripValueFont();
                if (labelFont.Size < 15.0f)
                {
                    throw new InvalidOperationException("Network monitor display self-test: Classic strip labels must stay readable.");
                }

                string fullIpv6 = form.BuildMeasuredAddressRowText(
                    g,
                    "2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890, fd00::1",
                    valueFont,
                    ip6Value.Width,
                    24);
                if (fullIpv6.IndexOf("2406:da18:7c3:8f00:1a2b:3c4d:5e6f:7890", StringComparison.Ordinal) < 0 ||
                    fullIpv6.IndexOf("+1", StringComparison.Ordinal) < 0 ||
                    fullIpv6.IndexOf("…", StringComparison.Ordinal) >= 0 ||
                    !DoesTextFit(g, fullIpv6, valueFont, ip6Value.Width))
                {
                    throw new InvalidOperationException("Network monitor display self-test: compact Classic strip must keep a full IPv6 address at 520px width.");
                }

                g.Clear(Color.FromArgb(15, 15, 19));
                form.DrawContent(g);
            }
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

}
