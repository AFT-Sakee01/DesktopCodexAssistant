using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

internal sealed partial class ConnectionCheckForm : LayeredWidgetFormBase
{
    private const int RenderSecondBoundaryOffsetMs = 75;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly CleanIpConnectionReader reader;
    private WidgetSettings currentSettings;
    private CleanIpConnectionSnapshot snapshot;
    private bool hiddenForFullscreen;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime reverseHoverRevealUntilUtc;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool autoHideKeepAliveActive;
    private bool sharedInteractionPolling;
    private long burnInShiftSlot = long.MinValue;
    private readonly UiFontCache fontCache = new UiFontCache();

    public ConnectionCheckForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.reader = new CleanIpConnectionReader();
        this.snapshot = new CleanIpConnectionSnapshot();
        ApplicationIcon.ApplyTo(this);

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.currentSettings);

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.MinimumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MinConnectionCheckWidth, WidgetSettings.MinConnectionCheckHeight));
        this.MaximumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MaxConnectionCheckWidth, WidgetSettings.MaxConnectionCheckHeight));
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextRenderTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        this.snapshot = this.reader.GetSnapshot(this.currentSettings);
        PositionConnectionCheckWindow();
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
        this.fontCache.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        this.fontCache.Dispose();
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
            PositionConnectionCheckWindow();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.currentSettings);
        this.MinimumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MinConnectionCheckWidth, WidgetSettings.MinConnectionCheckHeight));
        this.MaximumSize = this.currentSettings.ScaleResolutionCompatibilitySize(new Size(WidgetSettings.MaxConnectionCheckWidth, WidgetSettings.MaxConnectionCheckHeight));
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
            GetLayeredWidgetInsertAfter(shouldBeTopMost),
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionConnectionCheckWindow();
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

        PositionConnectionCheckWindow();
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
        PositionConnectionCheckWindow();
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
            // The reader decides when to perform I/O; the form redraws only changed display fields.
            CleanIpConnectionSnapshot nextSnapshot = this.reader.GetSnapshot(this.currentSettings);
            bool displayChanged = !HasSameDisplayData(this.snapshot, nextSnapshot);
            this.snapshot = nextSnapshot;
            Size desiredSize = GetDesiredSize();
            bool sizeChanged = false;
            if (this.Size != desiredSize)
            {
                this.Size = desiredSize;
                PositionConnectionCheckWindow();
                sizeChanged = true;
            }

            bool positionChanged = false;
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot))
            {
                PositionConnectionCheckWindow();
                positionChanged = true;
            }

            if (!this.hiddenForFullscreen && this.Visible && (displayChanged || sizeChanged || positionChanged))
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

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
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
            : WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
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
            RenderLayeredWindow(false);
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
            this.hoverOpacityDelayState,
            this.autoHideKeepAliveActive);
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

    private void PositionConnectionCheckWindow()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = this.currentSettings.GetWorkAreaForModule(WidgetSettings.ModuleConnectionCheck);
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int mappedLeft = this.currentSettings.MapResolutionCompatibilityLeft(WidgetSettings.ModuleConnectionCheck, workArea, this.currentSettings.ConnectionCheckLeftX);
        int mappedBottom = this.currentSettings.MapResolutionCompatibilityBottom(WidgetSettings.ModuleConnectionCheck, workArea, this.currentSettings.ConnectionCheckBottomY);
        int left = Math.Max(workArea.Left, Math.Min(mappedLeft, workArea.Right - this.Width));
        int top = mappedBottom - this.Height + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        Point shiftedLocation = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.ConnectionCheckSalt);
        left = shiftedLocation.X;
        top = shiftedLocation.Y;
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(this.currentSettings.VisibilityMode),
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
        return this.currentSettings.ScaleResolutionCompatibilitySize(new Size(this.currentSettings.ConnectionCheckWidth, this.currentSettings.ConnectionCheckHeight));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawConnectionCheckWindow(e.Graphics);
    }

    private void DrawConnectionCheckWindow(Graphics g)
    {
        DrawBackground(g);
        DrawContentLayer(g);
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawConnectionCheckWindow(g);
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
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, alpha)))
        {
            g.FillPath(background, shell);
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

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    // Render-variant dispatch (mirrors CodexRadarForm). Only Classic exists today; add a case and a
    // sibling partial file (ConnectionCheckForm.<Name>.cs) to introduce an alternate layout.
    private void DrawContent(Graphics g)
    {
        DrawContentClassic(g);
    }

    private void DrawContentClassic(Graphics g)
    {
        ConfigureGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(GetBorderOpacityAlpha()), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = Math.Max(S(4), this.Width * 0.024f);
        float verticalPadding = Math.Max(S(3), this.Height * 0.05f);
        RectangleF content = new RectangleF(padding, verticalPadding, this.Width - padding * 2.0f, this.Height - verticalPadding * 2.0f);
        DrawInfoGrid(g, content);
    }

    private void DrawInfoGrid(Graphics g, RectangleF content)
    {
        bool waiting = this.snapshot == null || (!this.snapshot.CheckedAtKnown && !this.snapshot.Running);
        bool firstRun = this.snapshot == null || (!this.snapshot.CheckedAtKnown && this.snapshot.Running);
        bool failed = this.snapshot != null && !this.snapshot.Success && !this.snapshot.Running && this.snapshot.CheckedAtKnown;

        if (waiting || firstRun || failed)
        {
            Color stateText;
            Color stateBorder;
            Color stateFill;
            if (failed)
            {
                BuildPalette(DesignTokens.Colors.Danger, out stateText, out stateBorder, out stateFill);
            }
            else if (firstRun)
            {
                BuildPalette(DesignTokens.Colors.Warning, out stateText, out stateBorder, out stateFill);
            }
            else
            {
                BuildPalette(DesignTokens.Colors.GlyphMuted, out stateText, out stateBorder, out stateFill);
            }

            DrawBadgeTriplet(
                g,
                content,
                "fa-solid fa-shield-halved",
                failed ? "ERR" : "--",
                "fa-solid fa-circle-question",
                failed ? "失败" : (firstRun ? "检测中" : "等待"),
                "fa-solid fa-circle-question",
                failed ? GetCompactErrorLabel(this.snapshot.Error) : "--",
                stateText,
                stateBorder,
                stateFill,
                stateText,
                stateBorder,
                stateFill,
                stateText,
                stateBorder,
                stateFill);
            return;
        }

        Color scoreText;
        Color scoreBorder;
        Color scoreFill;
        GetScorePalette(out scoreText, out scoreBorder, out scoreFill);

        Color nativeText;
        Color nativeBorder;
        Color nativeFill;
        GetNativePalette(this.snapshot.NativeKey, out nativeText, out nativeBorder, out nativeFill);

        Color typeText;
        Color typeBorder;
        Color typeFill;
        GetIpTypePalette(this.snapshot.IpTypeKey, out typeText, out typeBorder, out typeFill);

        DrawBadgeTriplet(
            g,
            content,
            "fa-solid fa-shield-halved",
            this.snapshot.ScoreLabel,
            this.snapshot.NativeIconClass,
            this.snapshot.NativeLabel,
            this.snapshot.IpTypeIconClass,
            this.snapshot.IpTypeLabel,
            scoreText,
            scoreBorder,
            scoreFill,
            nativeText,
            nativeBorder,
            nativeFill,
            typeText,
            typeBorder,
            typeFill);
    }

    private void DrawBadgeTriplet(
        Graphics g,
        RectangleF content,
        string firstIcon,
        string firstText,
        string secondIcon,
        string secondText,
        string thirdIcon,
        string thirdText,
        Color firstTextColor,
        Color firstBorderColor,
        Color firstFillColor,
        Color secondTextColor,
        Color secondBorderColor,
        Color secondFillColor,
        Color thirdTextColor,
        Color thirdBorderColor,
        Color thirdFillColor)
    {
        float gap = Math.Max(S(2), content.Width * 0.014f);
        float badgeWidth = (content.Width - gap * 2.0f) / 3.0f;
        RectangleF first = new RectangleF(content.Left, content.Top, badgeWidth, content.Height);
        RectangleF second = new RectangleF(first.Right + gap, content.Top, badgeWidth, content.Height);
        RectangleF third = new RectangleF(second.Right + gap, content.Top, badgeWidth, content.Height);

        DrawCleanIpBadge(g, first, firstIcon, firstText, firstTextColor, firstBorderColor, firstFillColor);
        DrawCleanIpBadge(g, second, secondIcon, secondText, secondTextColor, secondBorderColor, secondFillColor);
        DrawCleanIpBadge(g, third, thirdIcon, thirdText, thirdTextColor, thirdBorderColor, thirdFillColor);
    }

    private static string GetCompactErrorLabel(string error)
    {
        string value = error ?? string.Empty;
        if (value.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "HTTP403";
        }

        if (value.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0) return "HTTP429";
        if (value.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0) return "TIMEOUT";
        if (value.IndexOf("DNS", StringComparison.OrdinalIgnoreCase) >= 0) return "DNS";
        if (value.IndexOf("连接", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0) return "CONNECT";
        return "ERROR";
    }

    private void DrawCleanIpBadge(Graphics g, RectangleF rect, string iconClass, string text, Color textColor, Color borderColor, Color fillColor)
    {
        DrawCleanIpBadge(g, rect, iconClass, text, textColor, borderColor, fillColor, IsBurnInColorProtectionActive());
    }

    private void DrawCleanIpBadge(Graphics g, RectangleF rect, string iconClass, string text, Color textColor, Color borderColor, Color fillColor, bool suppressDecorativeFill)
    {
        using (GraphicsPath path = RoundedRectangle(rect, S(6)))
        using (SolidBrush fill = new SolidBrush(fillColor))
        using (Pen border = new Pen(borderColor, Math.Max(1.4f, S(2))))
        {
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, path);
            }

            g.DrawPath(border, path);
        }

        bool compact = rect.Width < S(86);
        float padding = compact ? Math.Max(S(2), rect.Width * 0.035f) : Math.Max(S(4), rect.Width * 0.045f);
        if (compact)
        {
            float iconSize = Math.Min(rect.Height * 0.48f, rect.Width * 0.46f);
            iconSize = Math.Max(S(20), iconSize);
            bool scoreText = IsScoreBadgeText(text);
            float compactFontSize = GetCompactBadgeFontSize(rect);
            if (scoreText)
            {
                compactFontSize = Math.Min(rect.Height * 0.34f, compactFontSize * 1.22f);
            }

            float textHeight = Math.Max(S(15), compactFontSize * 1.35f);
            float totalHeight = iconSize + S(2) + textHeight;
            float top = rect.Top + Math.Max(S(2), (rect.Height - totalHeight) / 2.0f);
            RectangleF iconRect = new RectangleF(rect.Left + (rect.Width - iconSize) / 2.0f, top, iconSize, iconSize);
            DrawBadgeIcon(g, iconClass, iconRect, textColor, suppressDecorativeFill);

            float textTop = iconRect.Bottom + S(2);
            if (scoreText)
            {
                textTop -= S(3);
            }

            RectangleF textRect = new RectangleF(rect.Left + padding, textTop, rect.Width - padding * 2.0f, textHeight + (scoreText ? S(2) : 0));
            Font compactFont = this.fontCache.GetUi(compactFontSize, FontStyle.Bold);
            using (SolidBrush brush = new SolidBrush(textColor))
            {
                DrawFittedText(g, EmptyToDash(text), compactFont, brush, textRect, StringAlignment.Center);
            }

            return;
        }

        float horizontalIconSize = Math.Min(rect.Height * 0.64f, Math.Max(S(20), rect.Width * 0.24f));
        RectangleF horizontalIconRect = new RectangleF(rect.Left + padding, rect.Top + (rect.Height - horizontalIconSize) / 2.0f, horizontalIconSize, horizontalIconSize);
        DrawBadgeIcon(g, iconClass, horizontalIconRect, textColor, suppressDecorativeFill);

        RectangleF horizontalTextRect = new RectangleF(horizontalIconRect.Right + S(4), rect.Top + S(1), rect.Right - horizontalIconRect.Right - padding - S(4), rect.Height - S(2));
        Font horizontalFont = this.fontCache.GetUi(Math.Max(14.0f, rect.Height * 0.42f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(textColor))
        {
            DrawFittedText(g, EmptyToDash(text), horizontalFont, brush, horizontalTextRect, StringAlignment.Near);
        }
    }

    private float GetCompactBadgeFontSize(RectangleF rect)
    {
        float byHeight = rect.Height * 0.27f;
        float byWidth = rect.Width * 0.245f;
        return Math.Max(12.0f, Math.Min(byHeight, byWidth));
    }

    private static bool IsScoreBadgeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (value.Length < 2 || value.Length > 4)
        {
            return false;
        }

        bool hasDigit = false;
        bool hasLetter = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsDigit(c))
            {
                hasDigit = true;
                continue;
            }

            if (c >= 'A' && c <= 'Z')
            {
                hasLetter = true;
                continue;
            }

            return false;
        }

        return hasDigit && hasLetter;
    }

    private void GetScorePalette(out Color text, out Color border, out Color fill)
    {
        int score = this.snapshot != null && this.snapshot.ScoreKnown ? this.snapshot.Score : -1;
        if (score >= 85)
        {
            BuildPalette(Color.FromArgb(53, 169, 82), out text, out border, out fill);
            return;
        }

        if (score >= 70)
        {
            BuildPalette(Color.FromArgb(132, 204, 22), out text, out border, out fill);
            return;
        }

        if (score >= 50)
        {
            BuildPalette(Color.FromArgb(234, 179, 8), out text, out border, out fill);
            return;
        }

        if (score >= 25)
        {
            BuildPalette(Color.FromArgb(234, 88, 12), out text, out border, out fill);
            return;
        }

        if (score >= 0)
        {
            BuildPalette(Color.FromArgb(220, 38, 38), out text, out border, out fill);
            return;
        }

        BuildPalette(Color.FromArgb(107, 114, 128), out text, out border, out fill);
    }

    private static void GetNativePalette(string key, out Color text, out Color border, out Color fill)
    {
        if (string.Equals(key, "native", StringComparison.OrdinalIgnoreCase))
        {
            BuildPalette(Color.FromArgb(53, 169, 82), out text, out border, out fill);
            return;
        }

        if (string.Equals(key, "broadcast", StringComparison.OrdinalIgnoreCase))
        {
            BuildPalette(Color.FromArgb(217, 119, 6), out text, out border, out fill);
            return;
        }

        BuildPalette(Color.FromArgb(107, 114, 128), out text, out border, out fill);
    }

    private static void GetIpTypePalette(string key, out Color text, out Color border, out Color fill)
    {
        if (key == "Residential IP" || key == "Mobile IP" || key == "Business IP" ||
            key == "Education IP" || key == "Government IP")
        {
            BuildPalette(Color.FromArgb(53, 169, 82), out text, out border, out fill);
            return;
        }

        if (key == "Public DNS" || key == "Root DNS" || key == "Public CDN")
        {
            BuildPalette(Color.FromArgb(15, 118, 110), out text, out border, out fill);
            return;
        }

        if (key == "Tor IP" || key == "Tor Exit" || key == "VPN IP" || key == "Proxy IP")
        {
            BuildPalette(Color.FromArgb(220, 38, 38), out text, out border, out fill);
            return;
        }

        if (key == "Residential Proxy" || key == "IDC" || key == "Datacenter IP" || key == "Relay IP")
        {
            BuildPalette(Color.FromArgb(217, 119, 6), out text, out border, out fill);
            return;
        }

        BuildPalette(Color.FromArgb(107, 114, 128), out text, out border, out fill);
    }

    private static void BuildPalette(Color accent, out Color text, out Color border, out Color fill)
    {
        text = Lighten(accent, 0.28f);
        border = DesignTokens.WithAlpha(Lighten(accent, 0.52f), 210);
        fill = DesignTokens.WithAlpha(accent, 34);
    }

    private static Color Lighten(Color color, float amount)
    {
        amount = Math.Max(0.0f, Math.Min(1.0f, amount));
        int r = color.R + (int)Math.Round((255 - color.R) * amount);
        int g = color.G + (int)Math.Round((255 - color.G) * amount);
        int b = color.B + (int)Math.Round((255 - color.B) * amount);
        return Color.FromArgb(r, g, b);
    }

    private void DrawBadgeIcon(Graphics g, string iconClass, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        string icon = (iconClass ?? string.Empty).ToLowerInvariant();
        if (icon.IndexOf("location-check", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawLocationCheckIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("router", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawRouterIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("circle-minus", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawCircleMinusIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("house", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawHouseIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("mobile", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawMobileIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("briefcase", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawBriefcaseIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("graduation", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawGraduationIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("landmark", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawLandmarkIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("network-wired", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawNetworkIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawServerIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("user-secret", StringComparison.OrdinalIgnoreCase) >= 0 ||
            icon.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawMaskIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("filter", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawFilterIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        if (icon.IndexOf("link", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawLinkIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawShieldIcon(g, rect, color, suppressDecorativeFill);
            return;
        }

        DrawQuestionIcon(g, rect, color, suppressDecorativeFill);
    }

    private Pen CreateIconPen(Color color)
    {
        Pen pen = new Pen(color, Math.Max(1.2f, S(2)));
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    private void DrawShieldIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 46)))
        using (SolidBrush halfFill = new SolidBrush(DesignTokens.WithAlpha(color, 84)))
        using (GraphicsPath outline = new GraphicsPath())
        using (GraphicsPath leftHalf = new GraphicsPath())
        {
            PointF top = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.06f);
            PointF bottom = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.94f);
            outline.AddPolygon(new PointF[]
            {
                top,
                new PointF(rect.Left + rect.Width * 0.84f, rect.Top + rect.Height * 0.18f),
                new PointF(rect.Left + rect.Width * 0.84f, rect.Top + rect.Height * 0.44f),
                new PointF(rect.Left + rect.Width * 0.74f, rect.Top + rect.Height * 0.70f),
                bottom,
                new PointF(rect.Left + rect.Width * 0.26f, rect.Top + rect.Height * 0.70f),
                new PointF(rect.Left + rect.Width * 0.16f, rect.Top + rect.Height * 0.44f),
                new PointF(rect.Left + rect.Width * 0.16f, rect.Top + rect.Height * 0.18f)
            });
            leftHalf.AddPolygon(new PointF[]
            {
                top,
                bottom,
                new PointF(rect.Left + rect.Width * 0.26f, rect.Top + rect.Height * 0.70f),
                new PointF(rect.Left + rect.Width * 0.16f, rect.Top + rect.Height * 0.44f),
                new PointF(rect.Left + rect.Width * 0.16f, rect.Top + rect.Height * 0.18f)
            });
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, outline);
                g.FillPath(halfFill, leftHalf);
            }

            g.DrawPath(pen, outline);
            g.DrawLine(pen, top, bottom);
        }
    }

    private void DrawLocationCheckIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 58)))
        using (GraphicsPath pin = new GraphicsPath())
        {
            float head = rect.Width * 0.27f;
            PointF headCenter = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.38f);
            pin.AddArc(headCenter.X - head, headCenter.Y - head, head * 2.0f, head * 2.0f, 150f, 240f);
            pin.AddLine(
                new PointF(headCenter.X + head * 0.866f, headCenter.Y + head * 0.5f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.94f));
            pin.CloseFigure();
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, pin);
            }

            g.DrawPath(pen, pin);
            g.DrawLines(pen, new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.38f, rect.Top + rect.Height * 0.38f),
                new PointF(rect.Left + rect.Width * 0.47f, rect.Top + rect.Height * 0.47f),
                new PointF(rect.Left + rect.Width * 0.64f, rect.Top + rect.Height * 0.28f)
            });
        }
    }

    private void DrawRouterIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 62)))
        using (SolidBrush node = new SolidBrush(color))
        {
            RectangleF body = new RectangleF(rect.Left + rect.Width * 0.14f, rect.Top + rect.Height * 0.58f, rect.Width * 0.72f, rect.Height * 0.26f);
            using (GraphicsPath path = RoundedRectangle(body, body.Height * 0.35f))
            {
                if (!suppressDecorativeFill)
                {
                    g.FillPath(fill, path);
                }

                g.DrawPath(pen, path);
            }

            PointF leftTip = new PointF(rect.Left + rect.Width * 0.22f, rect.Top + rect.Height * 0.14f);
            PointF rightTip = new PointF(rect.Left + rect.Width * 0.78f, rect.Top + rect.Height * 0.14f);
            g.DrawLine(pen, rect.Left + rect.Width * 0.32f, body.Top, leftTip.X, leftTip.Y);
            g.DrawLine(pen, rect.Left + rect.Width * 0.68f, body.Top, rightTip.X, rightTip.Y);
            float tip = rect.Width * 0.05f;
            g.FillEllipse(node, leftTip.X - tip, leftTip.Y - tip, tip * 2.0f, tip * 2.0f);
            g.FillEllipse(node, rightTip.X - tip, rightTip.Y - tip, tip * 2.0f, tip * 2.0f);

            float led = rect.Width * 0.04f;
            float ledY = body.Top + body.Height * 0.50f;
            g.FillEllipse(node, rect.Left + rect.Width * 0.26f - led, ledY - led, led * 2.0f, led * 2.0f);
            g.FillEllipse(node, rect.Left + rect.Width * 0.40f - led, ledY - led, led * 2.0f, led * 2.0f);
            g.DrawLine(pen, rect.Left + rect.Width * 0.54f, ledY, rect.Left + rect.Width * 0.76f, ledY);
        }
    }

    private void DrawCircleMinusIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (Pen bar = new Pen(color, Math.Max(1.8f, S(3))))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 48)))
        {
            RectangleF ring = new RectangleF(rect.Left + rect.Width * 0.13f, rect.Top + rect.Height * 0.13f, rect.Width * 0.74f, rect.Height * 0.74f);
            if (!suppressDecorativeFill)
            {
                g.FillEllipse(fill, ring);
            }

            g.DrawEllipse(pen, ring);
            bar.StartCap = LineCap.Round;
            bar.EndCap = LineCap.Round;
            g.DrawLine(bar, rect.Left + rect.Width * 0.32f, rect.Top + rect.Height * 0.50f, rect.Left + rect.Width * 0.68f, rect.Top + rect.Height * 0.50f);
        }
    }

    private void DrawHouseIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 56)))
        using (GraphicsPath body = new GraphicsPath())
        {
            body.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.10f),
                new PointF(rect.Left + rect.Width * 0.92f, rect.Top + rect.Height * 0.48f),
                new PointF(rect.Left + rect.Width * 0.80f, rect.Top + rect.Height * 0.48f),
                new PointF(rect.Left + rect.Width * 0.80f, rect.Top + rect.Height * 0.88f),
                new PointF(rect.Left + rect.Width * 0.20f, rect.Top + rect.Height * 0.88f),
                new PointF(rect.Left + rect.Width * 0.20f, rect.Top + rect.Height * 0.48f),
                new PointF(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.48f)
            });
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, body);
            }

            g.DrawPath(pen, body);
            using (GraphicsPath door = RoundedRectangle(new RectangleF(rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.60f, rect.Width * 0.16f, rect.Height * 0.28f), rect.Width * 0.05f))
            {
                g.DrawPath(pen, door);
            }
        }
    }

    private void DrawMobileIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 56)))
        using (SolidBrush node = new SolidBrush(color))
        {
            RectangleF body = new RectangleF(rect.Left + rect.Width * 0.30f, rect.Top + rect.Height * 0.08f, rect.Width * 0.40f, rect.Height * 0.84f);
            using (GraphicsPath phone = RoundedRectangle(body, rect.Width * 0.09f))
            {
                if (!suppressDecorativeFill)
                {
                    g.FillPath(fill, phone);
                }

                g.DrawPath(pen, phone);
            }

            g.DrawLine(pen, body.Left + body.Width * 0.28f, body.Top + body.Height * 0.12f, body.Right - body.Width * 0.28f, body.Top + body.Height * 0.12f);
            float dot = rect.Width * 0.045f;
            g.FillEllipse(node, body.Left + body.Width * 0.50f - dot, body.Bottom - body.Height * 0.12f - dot, dot * 2.0f, dot * 2.0f);
        }
    }

    private void DrawBriefcaseIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 56)))
        using (SolidBrush node = new SolidBrush(color))
        {
            using (GraphicsPath handle = RoundedRectangle(new RectangleF(rect.Left + rect.Width * 0.36f, rect.Top + rect.Height * 0.14f, rect.Width * 0.28f, rect.Height * 0.20f), rect.Width * 0.06f))
            {
                g.DrawPath(pen, handle);
            }

            RectangleF body = new RectangleF(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * 0.30f, rect.Width * 0.76f, rect.Height * 0.56f);
            using (GraphicsPath box = RoundedRectangle(body, rect.Width * 0.08f))
            {
                if (!suppressDecorativeFill)
                {
                    g.FillPath(fill, box);
                }

                g.DrawPath(pen, box);
            }

            g.DrawLine(pen, body.Left, body.Top + body.Height * 0.42f, body.Right, body.Top + body.Height * 0.42f);
            float clasp = rect.Width * 0.055f;
            g.FillEllipse(node, rect.Left + rect.Width * 0.50f - clasp, body.Top + body.Height * 0.42f - clasp, clasp * 2.0f, clasp * 2.0f);
        }
    }

    private void DrawGraduationIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 60)))
        using (SolidBrush node = new SolidBrush(color))
        using (GraphicsPath board = new GraphicsPath())
        {
            board.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.10f),
                new PointF(rect.Left + rect.Width * 0.94f, rect.Top + rect.Height * 0.32f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.54f),
                new PointF(rect.Left + rect.Width * 0.06f, rect.Top + rect.Height * 0.32f)
            });
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, board);
            }

            g.DrawPath(pen, board);
            g.DrawLines(pen, new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.44f),
                new PointF(rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.66f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.78f),
                new PointF(rect.Left + rect.Width * 0.72f, rect.Top + rect.Height * 0.66f),
                new PointF(rect.Left + rect.Width * 0.72f, rect.Top + rect.Height * 0.44f)
            });

            g.DrawLine(pen, rect.Left + rect.Width * 0.94f, rect.Top + rect.Height * 0.32f, rect.Left + rect.Width * 0.94f, rect.Top + rect.Height * 0.56f);
            float tassel = rect.Width * 0.045f;
            g.FillEllipse(node, rect.Left + rect.Width * 0.94f - tassel, rect.Top + rect.Height * 0.60f - tassel, tassel * 2.0f, tassel * 2.0f);
        }
    }

    private void DrawLandmarkIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 60)))
        using (GraphicsPath pediment = new GraphicsPath())
        {
            pediment.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.08f),
                new PointF(rect.Left + rect.Width * 0.88f, rect.Top + rect.Height * 0.30f),
                new PointF(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * 0.30f)
            });
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, pediment);
            }

            g.DrawPath(pen, pediment);
            float columnTop = rect.Top + rect.Height * 0.38f;
            float columnBottom = rect.Top + rect.Height * 0.72f;
            float[] columns = { 0.25f, 0.50f, 0.75f };
            for (int i = 0; i < columns.Length; i++)
            {
                float x = rect.Left + rect.Width * columns[i];
                g.DrawLine(pen, x, columnTop, x, columnBottom);
            }

            g.DrawLine(pen, rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.80f, rect.Left + rect.Width * 0.82f, rect.Top + rect.Height * 0.80f);
            g.DrawLine(pen, rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.90f, rect.Left + rect.Width * 0.90f, rect.Top + rect.Height * 0.90f);
        }
    }

    private void DrawNetworkIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 58)))
        {
            RectangleF topBox = new RectangleF(rect.Left + rect.Width * 0.35f, rect.Top + rect.Height * 0.08f, rect.Width * 0.30f, rect.Height * 0.22f);
            RectangleF leftBox = new RectangleF(rect.Left + rect.Width * 0.06f, rect.Top + rect.Height * 0.62f, rect.Width * 0.30f, rect.Height * 0.22f);
            RectangleF rightBox = new RectangleF(rect.Left + rect.Width * 0.64f, rect.Top + rect.Height * 0.62f, rect.Width * 0.30f, rect.Height * 0.22f);

            float busY = rect.Top + rect.Height * 0.46f;
            g.DrawLine(pen, rect.Left + rect.Width * 0.50f, topBox.Bottom, rect.Left + rect.Width * 0.50f, busY);
            g.DrawLine(pen, rect.Left + rect.Width * 0.21f, busY, rect.Left + rect.Width * 0.79f, busY);
            g.DrawLine(pen, rect.Left + rect.Width * 0.21f, busY, rect.Left + rect.Width * 0.21f, leftBox.Top);
            g.DrawLine(pen, rect.Left + rect.Width * 0.79f, busY, rect.Left + rect.Width * 0.79f, rightBox.Top);

            RectangleF[] boxes = { topBox, leftBox, rightBox };
            for (int i = 0; i < boxes.Length; i++)
            {
                using (GraphicsPath path = RoundedRectangle(boxes[i], rect.Width * 0.04f))
                {
                    if (!suppressDecorativeFill)
                    {
                        g.FillPath(fill, path);
                    }

                    g.DrawPath(pen, path);
                }
            }
        }
    }

    private void DrawServerIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 56)))
        using (SolidBrush node = new SolidBrush(color))
        {
            for (int i = 0; i < 2; i++)
            {
                RectangleF rack = new RectangleF(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * (0.16f + i * 0.40f), rect.Width * 0.76f, rect.Height * 0.28f);
                using (GraphicsPath path = RoundedRectangle(rack, rack.Height * 0.28f))
                {
                    if (!suppressDecorativeFill)
                    {
                        g.FillPath(fill, path);
                    }

                    g.DrawPath(pen, path);
                }

                float led = rect.Width * 0.04f;
                float y = rack.Top + rack.Height * 0.50f;
                g.FillEllipse(node, rect.Left + rect.Width * 0.24f - led, y - led, led * 2.0f, led * 2.0f);
                g.DrawLine(pen, rect.Left + rect.Width * 0.44f, y, rect.Left + rect.Width * 0.76f, y);
            }
        }
    }

    private void DrawMaskIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 56)))
        using (GraphicsPath mask = new GraphicsPath())
        {
            mask.AddClosedCurve(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.42f),
                new PointF(rect.Left + rect.Width * 0.30f, rect.Top + rect.Height * 0.30f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.40f),
                new PointF(rect.Left + rect.Width * 0.70f, rect.Top + rect.Height * 0.30f),
                new PointF(rect.Left + rect.Width * 0.92f, rect.Top + rect.Height * 0.42f),
                new PointF(rect.Left + rect.Width * 0.74f, rect.Top + rect.Height * 0.68f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.60f),
                new PointF(rect.Left + rect.Width * 0.26f, rect.Top + rect.Height * 0.68f)
            }, 0.45f);
            mask.AddEllipse(rect.Left + rect.Width * 0.22f, rect.Top + rect.Height * 0.40f, rect.Width * 0.16f, rect.Height * 0.13f);
            mask.AddEllipse(rect.Left + rect.Width * 0.62f, rect.Top + rect.Height * 0.40f, rect.Width * 0.16f, rect.Height * 0.13f);
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, mask);
            }

            g.DrawPath(pen, mask);
        }
    }

    private void DrawFilterIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 56)))
        using (GraphicsPath funnel = new GraphicsPath())
        {
            funnel.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.12f),
                new PointF(rect.Left + rect.Width * 0.90f, rect.Top + rect.Height * 0.12f),
                new PointF(rect.Left + rect.Width * 0.58f, rect.Top + rect.Height * 0.52f),
                new PointF(rect.Left + rect.Width * 0.58f, rect.Top + rect.Height * 0.90f),
                new PointF(rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.78f),
                new PointF(rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.52f)
            });
            if (!suppressDecorativeFill)
            {
                g.FillPath(fill, funnel);
            }

            g.DrawPath(pen, funnel);
        }
    }

    private void DrawLinkIcon(Graphics g, RectangleF rect, Color color)
    {
        GraphicsState state = g.Save();
        try
        {
            g.TranslateTransform(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.50f);
            g.RotateTransform(-45f);
            using (Pen pen = CreateIconPen(color))
            using (GraphicsPath left = RoundedRectangle(new RectangleF(-rect.Width * 0.44f, -rect.Height * 0.13f, rect.Width * 0.48f, rect.Height * 0.26f), rect.Height * 0.12f))
            using (GraphicsPath right = RoundedRectangle(new RectangleF(-rect.Width * 0.04f, -rect.Height * 0.13f, rect.Width * 0.48f, rect.Height * 0.26f), rect.Height * 0.12f))
            {
                g.DrawPath(pen, left);
                g.DrawPath(pen, right);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void DrawQuestionIcon(Graphics g, RectangleF rect, Color color, bool suppressDecorativeFill)
    {
        Font font = this.fontCache.GetUi(Math.Max(10.0f, rect.Height * 0.56f), FontStyle.Bold);
        using (Pen pen = CreateIconPen(color))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 50)))
        using (SolidBrush text = new SolidBrush(color))
        using (StringFormat format = new StringFormat())
        {
            RectangleF ring = new RectangleF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.10f, rect.Width * 0.80f, rect.Height * 0.80f);
            if (!suppressDecorativeFill)
            {
                g.FillEllipse(fill, ring);
            }

            g.DrawEllipse(pen, ring);
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("?", font, text, rect, format);
        }
    }

    private static string EmptyToDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
    }

    internal static void RunHiddenModeBadgeRenderingSelfTest()
    {
        ConnectionCheckForm form = new ConnectionCheckForm(new WidgetSettings());
        try
        {
            using (Bitmap bitmap = new Bitmap(96, 48, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                BurnInProtection.ConfigureGraphics(graphics, true);
                Color text = Color.FromArgb(220, 53, 169, 82);
                Color border = Color.FromArgb(210, 150, 210, 160);
                Color fill = Color.FromArgb(34, 53, 169, 82);
                RectangleF badgeRect = new RectangleF(4.0f, 4.0f, 88.0f, 40.0f);

                graphics.Clear(Color.Transparent);
                form.DrawCleanIpBadge(
                    graphics,
                    badgeRect,
                    "fa-solid fa-shield-halved",
                    "OK",
                    text,
                    border,
                    fill,
                    false);
                AssertConnectionCheckSelfTest(bitmap.GetPixel(84, 24).A > 0, "normal badge fill sample");

                graphics.Clear(Color.Transparent);
                form.DrawCleanIpBadge(
                    graphics,
                    badgeRect,
                    "fa-solid fa-shield-halved",
                    "OK",
                    text,
                    border,
                    fill,
                    true);
                AssertConnectionCheckSelfTest(bitmap.GetPixel(84, 24).A == 0, "hidden badge fill suppression");

                RectangleF iconRect = new RectangleF(8.0f, 4.0f, 40.0f, 40.0f);
                graphics.Clear(Color.Transparent);
                form.DrawShieldIcon(graphics, iconRect, text, false);
                AssertConnectionCheckSelfTest(bitmap.GetPixel(23, 18).A > 0, "normal icon fill sample");

                graphics.Clear(Color.Transparent);
                form.DrawShieldIcon(graphics, iconRect, text, true);
                AssertConnectionCheckSelfTest(bitmap.GetPixel(23, 18).A == 0, "hidden icon fill suppression");
            }
        }
        finally
        {
            form.Close();
            form.Dispose();
        }
    }

    private static void AssertConnectionCheckSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Connection check self-test failed: " +
                message);
        }
    }

    private static bool HasSameDisplayData(CleanIpConnectionSnapshot left, CleanIpConnectionSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return left.CheckedAtKnown == right.CheckedAtKnown &&
            left.Success == right.Success &&
            left.Running == right.Running &&
            left.ScoreKnown == right.ScoreKnown &&
            left.Score == right.Score &&
            string.Equals(left.ScoreLabel, right.ScoreLabel, StringComparison.Ordinal) &&
            string.Equals(left.NativeKey, right.NativeKey, StringComparison.Ordinal) &&
            string.Equals(left.NativeIconClass, right.NativeIconClass, StringComparison.Ordinal) &&
            string.Equals(left.NativeLabel, right.NativeLabel, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeKey, right.IpTypeKey, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeIconClass, right.IpTypeIconClass, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeLabel, right.IpTypeLabel, StringComparison.Ordinal) &&
            string.Equals(GetCompactErrorLabel(left.Error), GetCompactErrorLabel(right.Error), StringComparison.Ordinal);
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
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 7.0f * this.LayerScale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.7f * this.LayerScale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(
            this.currentSettings,
            IsHoverOpacityTargetActive());
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ConnectionCheckTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetBorderOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ConnectionCheckBorderTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    protected override byte GetApplicationOpacityAlpha()
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

}
