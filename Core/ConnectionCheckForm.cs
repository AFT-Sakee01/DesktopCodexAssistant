using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

internal sealed class ConnectionCheckForm : Form
{
    private const int RenderSecondBoundaryOffsetMs = 75;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly CleanIpConnectionReader reader;
    private WidgetSettings currentSettings;
    private CleanIpConnectionSnapshot snapshot;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private bool sharedInteractionPolling;
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private bool renderBufferValid;
    private bool lastRenderedBurnInColorProtectionActive;
    private long burnInShiftSlot = long.MinValue;
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private readonly UiFontCache fontCache = new UiFontCache();

    public ConnectionCheckForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.reader = new CleanIpConnectionReader();
        this.snapshot = new CleanIpConnectionSnapshot();

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
        this.MinimumSize = new Size(WidgetSettings.MinConnectionCheckWidth, WidgetSettings.MinConnectionCheckHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxConnectionCheckWidth, WidgetSettings.MaxConnectionCheckHeight);
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextRenderTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
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
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        this.layeredSurface.Dispose();
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
        return IsHoverOpacityRuntimeEnabled() &&
            !this.hiddenForFullscreen &&
            this.Visible &&
            (this.currentSettings.ForceHoverOpacityActive || this.Bounds.Contains(Cursor.Position));
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

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.ConnectionCheckLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinConnectionCheckHeight, this.currentSettings.ConnectionCheckHeight);
        int top = this.currentSettings.ConnectionCheckBottomY - baseHeight + 1;
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
        return new Size(this.currentSettings.ConnectionCheckWidth, this.currentSettings.ConnectionCheckHeight);
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

    private void DrawContent(Graphics g)
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
        using (GraphicsPath path = RoundedRectangle(rect, S(6)))
        using (SolidBrush fill = new SolidBrush(fillColor))
        using (Pen border = new Pen(borderColor, Math.Max(1.4f, S(2))))
        {
            g.FillPath(fill, path);
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
            DrawBadgeIcon(g, iconClass, iconRect, textColor);

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
        DrawBadgeIcon(g, iconClass, horizontalIconRect, textColor);

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

    private void DrawBadgeIcon(Graphics g, string iconClass, RectangleF rect, Color color)
    {
        string icon = (iconClass ?? string.Empty).ToLowerInvariant();
        if (icon.IndexOf("location-check", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawLocationCheckIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("router", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawRouterIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("circle-minus", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawCircleMinusIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("house", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawHouseIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("mobile", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawMobileIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("briefcase", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawBriefcaseIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("graduation", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawGraduationIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("landmark", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawLandmarkIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("network-wired", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawNetworkIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawServerIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("user-secret", StringComparison.OrdinalIgnoreCase) >= 0 ||
            icon.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawMaskIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("filter", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawFilterIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("link", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawLinkIcon(g, rect, color);
            return;
        }

        if (icon.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            DrawShieldIcon(g, rect, color);
            return;
        }

        DrawQuestionIcon(g, rect, color);
    }

    private void DrawShieldIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 54)))
        using (GraphicsPath outer = new GraphicsPath())
        {
            outer.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.08f),
                new PointF(rect.Right - rect.Width * 0.16f, rect.Top + rect.Height * 0.24f),
                new PointF(rect.Right - rect.Width * 0.26f, rect.Bottom - rect.Height * 0.14f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Bottom - rect.Height * 0.02f),
                new PointF(rect.Left + rect.Width * 0.26f, rect.Bottom - rect.Height * 0.14f),
                new PointF(rect.Left + rect.Width * 0.16f, rect.Top + rect.Height * 0.24f)
            });
            g.FillPath(brush, outer);
            g.DrawPath(pen, outer);
            g.DrawLine(pen, rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.16f, rect.Left + rect.Width * 0.50f, rect.Bottom - rect.Height * 0.18f);
            g.DrawLine(pen, rect.Left + rect.Width * 0.33f, rect.Top + rect.Height * 0.48f, rect.Right - rect.Width * 0.33f, rect.Top + rect.Height * 0.48f);
        }
    }

    private void DrawLocationCheckIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(color))
        {
            PointF center = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.50f);
            float node = Math.Max(S(3), rect.Width * 0.13f);
            PointF left = new PointF(rect.Left + rect.Width * 0.22f, rect.Top + rect.Height * 0.68f);
            PointF right = new PointF(rect.Right - rect.Width * 0.20f, rect.Top + rect.Height * 0.26f);
            PointF top = new PointF(rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.16f);
            g.DrawLine(pen, left, center);
            g.DrawLine(pen, center, right);
            g.DrawLine(pen, top, center);
            g.FillEllipse(brush, center.X - node, center.Y - node, node * 2.0f, node * 2.0f);
            g.FillEllipse(brush, left.X - node * 0.75f, left.Y - node * 0.75f, node * 1.5f, node * 1.5f);
            g.FillEllipse(brush, right.X - node * 0.75f, right.Y - node * 0.75f, node * 1.5f, node * 1.5f);
            g.FillEllipse(brush, top.X - node * 0.65f, top.Y - node * 0.65f, node * 1.3f, node * 1.3f);
        }
    }

    private void DrawRouterIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 72)))
        using (SolidBrush nodeBrush = new SolidBrush(color))
        {
            RectangleF core = new RectangleF(rect.Left + rect.Width * 0.32f, rect.Top + rect.Height * 0.34f, rect.Width * 0.36f, rect.Height * 0.32f);
            using (GraphicsPath corePath = RoundedRectangle(core, core.Height * 0.22f))
            {
                g.FillPath(brush, corePath);
                g.DrawPath(pen, corePath);
            }

            PointF origin = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.50f);
            PointF[] ends = new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.22f),
                new PointF(rect.Right - rect.Width * 0.16f, rect.Top + rect.Height * 0.22f),
                new PointF(rect.Left + rect.Width * 0.18f, rect.Bottom - rect.Height * 0.18f),
                new PointF(rect.Right - rect.Width * 0.16f, rect.Bottom - rect.Height * 0.18f)
            };
            for (int i = 0; i < ends.Length; i++)
            {
                g.DrawLine(pen, origin, ends[i]);
                g.FillEllipse(nodeBrush, ends[i].X - rect.Width * 0.045f, ends[i].Y - rect.Width * 0.045f, rect.Width * 0.09f, rect.Width * 0.09f);
            }
        }
    }

    private void DrawCircleMinusIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.4f, S(2))))
        using (Pen gap = new Pen(DesignTokens.Colors.AppBackground, Math.Max(1.5f, S(3))))
        {
            RectangleF ring = new RectangleF(rect.Left + rect.Width * 0.14f, rect.Top + rect.Height * 0.14f, rect.Width * 0.72f, rect.Height * 0.72f);
            g.DrawArc(pen, ring, 25, 275);
            g.DrawArc(gap, ring, 306, 34);
            g.DrawLine(pen, rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.62f, rect.Right - rect.Width * 0.28f, rect.Top + rect.Height * 0.38f);
        }
    }

    private void DrawHouseIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 62)))
        using (SolidBrush nodeBrush = new SolidBrush(color))
        {
            RectangleF arch = new RectangleF(rect.Left + rect.Width * 0.26f, rect.Top + rect.Height * 0.20f, rect.Width * 0.48f, rect.Height * 0.64f);
            using (GraphicsPath path = RoundedRectangle(arch, arch.Width * 0.22f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            g.DrawLine(pen, rect.Left + rect.Width * 0.18f, rect.Bottom - rect.Height * 0.16f, rect.Right - rect.Width * 0.18f, rect.Bottom - rect.Height * 0.16f);
            g.FillEllipse(nodeBrush, rect.Left + rect.Width * 0.44f, rect.Top + rect.Height * 0.54f, rect.Width * 0.06f, rect.Width * 0.06f);
        }
    }

    private void DrawMobileIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 66)))
        using (SolidBrush nodeBrush = new SolidBrush(color))
        {
            RectangleF body = new RectangleF(rect.Left + rect.Width * 0.32f, rect.Top + rect.Height * 0.10f, rect.Width * 0.36f, rect.Height * 0.76f);
            using (GraphicsPath phone = RoundedRectangle(body, body.Width * 0.22f))
            {
                g.FillPath(brush, phone);
                g.DrawPath(pen, phone);
            }

            g.DrawLine(pen, body.Left + body.Width * 0.26f, body.Top + body.Height * 0.16f, body.Right - body.Width * 0.26f, body.Top + body.Height * 0.16f);
            g.FillEllipse(nodeBrush, body.Left + body.Width * 0.43f, body.Bottom - body.Height * 0.14f, body.Width * 0.14f, body.Width * 0.14f);
        }
    }

    private void DrawBriefcaseIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 62)))
        {
            RectangleF tower = new RectangleF(rect.Left + rect.Width * 0.24f, rect.Top + rect.Height * 0.16f, rect.Width * 0.52f, rect.Height * 0.68f);
            g.FillRectangle(brush, tower);
            g.DrawRectangle(pen, Rectangle.Round(tower));
            for (int row = 0; row < 3; row++)
            {
                float y = tower.Top + tower.Height * (0.20f + row * 0.23f);
                g.DrawLine(pen, tower.Left + tower.Width * 0.20f, y, tower.Right - tower.Width * 0.20f, y);
            }

            g.DrawLine(pen, tower.Left + tower.Width * 0.50f, tower.Top, tower.Left + tower.Width * 0.50f, tower.Bottom);
        }
    }

    private void DrawGraduationIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(1))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 62)))
        {
            RectangleF book = new RectangleF(rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.24f, rect.Width * 0.64f, rect.Height * 0.52f);
            using (GraphicsPath leftPage = RoundedRectangle(new RectangleF(book.Left, book.Top, book.Width * 0.48f, book.Height), book.Height * 0.12f))
            using (GraphicsPath rightPage = RoundedRectangle(new RectangleF(book.Left + book.Width * 0.52f, book.Top, book.Width * 0.48f, book.Height), book.Height * 0.12f))
            {
                g.FillPath(brush, leftPage);
                g.FillPath(brush, rightPage);
                g.DrawPath(pen, leftPage);
                g.DrawPath(pen, rightPage);
            }

            g.DrawLine(pen, rect.Left + rect.Width * 0.50f, book.Top + book.Height * 0.08f, rect.Left + rect.Width * 0.50f, book.Bottom - book.Height * 0.06f);
            g.DrawLine(pen, book.Left + book.Width * 0.18f, book.Top + book.Height * 0.30f, book.Left + book.Width * 0.40f, book.Top + book.Height * 0.22f);
            g.DrawLine(pen, book.Right - book.Width * 0.18f, book.Top + book.Height * 0.30f, book.Right - book.Width * 0.40f, book.Top + book.Height * 0.22f);
        }
    }

    private void DrawLandmarkIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 64)))
        using (SolidBrush nodeBrush = new SolidBrush(color))
        {
            RectangleF seal = new RectangleF(rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.14f, rect.Width * 0.64f, rect.Height * 0.64f);
            g.FillEllipse(brush, seal);
            g.DrawEllipse(pen, seal);
            for (int i = 0; i < 3; i++)
            {
                double angle = (-90 + i * 120) * Math.PI / 180.0;
                PointF p = new PointF(
                    seal.Left + seal.Width * 0.50f + (float)Math.Cos(angle) * seal.Width * 0.25f,
                    seal.Top + seal.Height * 0.50f + (float)Math.Sin(angle) * seal.Height * 0.25f);
                g.FillEllipse(nodeBrush, p.X - seal.Width * 0.055f, p.Y - seal.Width * 0.055f, seal.Width * 0.11f, seal.Width * 0.11f);
            }
        }
    }

    private void DrawNetworkIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        {
            RectangleF globe = new RectangleF(rect.Left + rect.Width * 0.16f, rect.Top + rect.Height * 0.16f, rect.Width * 0.68f, rect.Height * 0.68f);
            g.DrawEllipse(pen, globe);
            g.DrawArc(pen, globe.Left + globe.Width * 0.22f, globe.Top, globe.Width * 0.56f, globe.Height, 90, 180);
            g.DrawArc(pen, globe.Left + globe.Width * 0.22f, globe.Top, globe.Width * 0.56f, globe.Height, 270, 180);
            g.DrawLine(pen, globe.Left + globe.Width * 0.12f, globe.Top + globe.Height * 0.50f, globe.Right - globe.Width * 0.12f, globe.Top + globe.Height * 0.50f);
        }
    }

    private void DrawServerIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 58)))
        using (SolidBrush dot = new SolidBrush(color))
        {
            for (int i = 0; i < 3; i++)
            {
                RectangleF rack = new RectangleF(rect.Left + rect.Width * 0.20f, rect.Top + rect.Height * (0.14f + i * 0.24f), rect.Width * 0.60f, rect.Height * 0.17f);
                using (GraphicsPath path = RoundedRectangle(rack, rack.Height * 0.22f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }

                g.FillEllipse(dot, rack.Right - rack.Height * 0.42f, rack.Top + rack.Height * 0.34f, rack.Height * 0.28f, rack.Height * 0.28f);
            }
        }
    }

    private void DrawMaskIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        {
            RectangleF outer = new RectangleF(rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.14f, rect.Width * 0.64f, rect.Height * 0.72f);
            g.DrawEllipse(pen, outer);
            g.DrawArc(pen, outer.Left + outer.Width * 0.18f, outer.Top + outer.Height * 0.08f, outer.Width * 0.64f, outer.Height * 0.84f, 90, 280);
            g.DrawArc(pen, outer.Left + outer.Width * 0.34f, outer.Top + outer.Height * 0.18f, outer.Width * 0.34f, outer.Height * 0.64f, 90, 280);
        }
    }

    private void DrawFilterIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        {
            PointF left = new PointF(rect.Left + rect.Width * 0.20f, rect.Top + rect.Height * 0.30f);
            PointF center = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.50f);
            PointF right = new PointF(rect.Right - rect.Width * 0.20f, rect.Bottom - rect.Height * 0.30f);
            g.DrawLine(pen, left, center);
            g.DrawLine(pen, center, right);
            g.DrawLine(pen, rect.Left + rect.Width * 0.20f, rect.Bottom - rect.Height * 0.30f, rect.Right - rect.Width * 0.20f, rect.Top + rect.Height * 0.30f);
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, left.X - rect.Width * 0.045f, left.Y - rect.Width * 0.045f, rect.Width * 0.09f, rect.Width * 0.09f);
                g.FillEllipse(brush, center.X - rect.Width * 0.045f, center.Y - rect.Width * 0.045f, rect.Width * 0.09f, rect.Width * 0.09f);
                g.FillEllipse(brush, right.X - rect.Width * 0.045f, right.Y - rect.Width * 0.045f, rect.Width * 0.09f, rect.Width * 0.09f);
            }
        }
    }

    private void DrawLinkIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        using (SolidBrush brush = new SolidBrush(color))
        {
            PointF a = new PointF(rect.Left + rect.Width * 0.24f, rect.Top + rect.Height * 0.30f);
            PointF b = new PointF(rect.Left + rect.Width * 0.52f, rect.Top + rect.Height * 0.50f);
            PointF c = new PointF(rect.Right - rect.Width * 0.20f, rect.Bottom - rect.Height * 0.24f);
            g.DrawLine(pen, a, b);
            g.DrawLine(pen, b, c);
            g.FillEllipse(brush, a.X - rect.Width * 0.075f, a.Y - rect.Width * 0.075f, rect.Width * 0.15f, rect.Width * 0.15f);
            g.FillEllipse(brush, b.X - rect.Width * 0.06f, b.Y - rect.Width * 0.06f, rect.Width * 0.12f, rect.Width * 0.12f);
            g.FillEllipse(brush, c.X - rect.Width * 0.075f, c.Y - rect.Width * 0.075f, rect.Width * 0.15f, rect.Width * 0.15f);
        }
    }

    private void DrawQuestionIcon(Graphics g, RectangleF rect, Color color)
    {
        Font font = this.fontCache.GetUi(Math.Max(10.0f, rect.Height * 0.62f), FontStyle.Bold);
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 58)))
        using (SolidBrush text = new SolidBrush(color))
        using (StringFormat format = new StringFormat())
        {
            using (GraphicsPath diamond = new GraphicsPath())
            {
                diamond.AddPolygon(new PointF[]
                {
                    new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.08f),
                    new PointF(rect.Right - rect.Width * 0.08f, rect.Top + rect.Height * 0.50f),
                    new PointF(rect.Left + rect.Width * 0.50f, rect.Bottom - rect.Height * 0.08f),
                    new PointF(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.50f)
                });
                diamond.CloseFigure();
                g.FillPath(brush, diamond);
                g.DrawPath(pen, diamond);
            }

            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("?", font, text, rect, format);
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]) && values[i].Trim() != "--")
            {
                return values[i].Trim();
            }
        }

        return string.Empty;
    }

    private static string EmptyToDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
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

        return left.CheckedAtLocal == right.CheckedAtLocal &&
            left.CheckedAtKnown == right.CheckedAtKnown &&
            left.Success == right.Success &&
            left.Running == right.Running &&
            left.TestMode == right.TestMode &&
            left.ScoreKnown == right.ScoreKnown &&
            left.Score == right.Score &&
            left.LatencyMs == right.LatencyMs &&
            string.Equals(left.Ip, right.Ip, StringComparison.Ordinal) &&
            string.Equals(left.Asn, right.Asn, StringComparison.Ordinal) &&
            string.Equals(left.Organization, right.Organization, StringComparison.Ordinal) &&
            string.Equals(left.Grade, right.Grade, StringComparison.Ordinal) &&
            string.Equals(left.NativeKey, right.NativeKey, StringComparison.Ordinal) &&
            string.Equals(left.NativeLabel, right.NativeLabel, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeKey, right.IpTypeKey, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeLabel, right.IpTypeLabel, StringComparison.Ordinal) &&
            string.Equals(left.Error, right.Error, StringComparison.Ordinal) &&
            string.Equals(left.RefreshTrigger, right.RefreshTrigger, StringComparison.Ordinal);
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
            while (size > 7.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.7f * this.scale;
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
            // Opacity-only changes reuse the cached content bitmap.
            bool burnInColorProtectionActive = IsBurnInColorProtectionActive();
            bool refreshNativeBitmap =
                redrawContent ||
                !this.renderBufferValid ||
                burnInColorProtectionActive != this.lastRenderedBurnInColorProtectionActive;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawBackground(this.renderGraphics);
                DrawContentLayer(this.renderGraphics);
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
                    Program.LogInfo("ConnectionCheck UpdateLayeredWindow failed; falling back to normal paint.");
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

        DisposeRenderBuffer();
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

    private void DisposeRenderBuffer()
    {
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

        this.renderBufferValid = false;
    }

    private void ResetDisplayRenderResources()
    {
        DisposeRenderBuffer();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
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
