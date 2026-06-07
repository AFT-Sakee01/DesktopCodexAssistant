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
        this.renderBufferValid = false;
        PositionConnectionCheckWindow();
        RenderLayeredWindow();
        this.reader.RequestRefresh();
        ScheduleNextRenderTick();
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
        bool firstRun = this.snapshot == null || (!this.snapshot.CheckedAtKnown && this.snapshot.Running);
        bool failed = this.snapshot != null && !this.snapshot.Success && !this.snapshot.Running && this.snapshot.CheckedAtKnown;

        if (firstRun)
        {
            DrawStateMessage(g, content, "CleanIP", "检测中", DesignTokens.Colors.Warning);
            return;
        }

        if (failed)
        {
            string errorText = this.snapshot == null ? "不可用" : EmptyToDash(this.snapshot.Error);
            string title = "CleanIP";
            if (this.snapshot != null && this.snapshot.CheckedAtKnown)
            {
                title += " · " + this.snapshot.CheckedAtLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            DrawStateMessage(g, content, title, errorText, DesignTokens.Colors.Danger);
            return;
        }

        float footerHeight = content.Height < S(78) ? 0.0f : Math.Min(S(18), Math.Max(S(12), content.Height * 0.18f));
        RectangleF badgeArea = new RectangleF(content.Left, content.Top, content.Width, content.Height - footerHeight - (footerHeight > 0 ? S(3) : 0));
        if (badgeArea.Height < S(38))
        {
            badgeArea = new RectangleF(content.Left, content.Top, content.Width, content.Height);
            footerHeight = 0;
        }

        float gap = Math.Max(S(2), content.Width * 0.014f);
        float badgeWidth = (badgeArea.Width - gap * 2.0f) / 3.0f;
        RectangleF scoreRect = new RectangleF(badgeArea.Left, badgeArea.Top, badgeWidth, badgeArea.Height);
        RectangleF nativeRect = new RectangleF(scoreRect.Right + gap, badgeArea.Top, badgeWidth, badgeArea.Height);
        RectangleF typeRect = new RectangleF(nativeRect.Right + gap, badgeArea.Top, badgeWidth, badgeArea.Height);

        Color scoreText;
        Color scoreBorder;
        Color scoreFill;
        GetScorePalette(out scoreText, out scoreBorder, out scoreFill);
        DrawCleanIpBadge(g, scoreRect, "fa-solid fa-shield-halved", this.snapshot.ScoreLabel, scoreText, scoreBorder, scoreFill);

        Color nativeText;
        Color nativeBorder;
        Color nativeFill;
        GetNativePalette(this.snapshot.NativeKey, out nativeText, out nativeBorder, out nativeFill);
        DrawCleanIpBadge(g, nativeRect, this.snapshot.NativeIconClass, this.snapshot.NativeLabel, nativeText, nativeBorder, nativeFill);

        Color typeText;
        Color typeBorder;
        Color typeFill;
        GetIpTypePalette(this.snapshot.IpTypeKey, out typeText, out typeBorder, out typeFill);
        DrawCleanIpBadge(g, typeRect, this.snapshot.IpTypeIconClass, this.snapshot.IpTypeLabel, typeText, typeBorder, typeFill);

        if (footerHeight > 0)
        {
            RectangleF footer = new RectangleF(content.Left + S(1), badgeArea.Bottom + S(2), content.Width - S(2), footerHeight);
            DrawMetaLine(g, footer);
        }
    }

    private void DrawStateMessage(Graphics g, RectangleF rect, string title, string detail, Color color)
    {
        Color fill = DesignTokens.WithAlpha(color, 34);
        Color border = DesignTokens.WithAlpha(color, 172);
        using (GraphicsPath path = RoundedRectangle(rect, S(6)))
        using (SolidBrush brush = new SolidBrush(fill))
        using (Pen pen = new Pen(border, Math.Max(1, S(1))))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        Font titleFont = this.fontCache.GetUi(Math.Max(10.0f, rect.Height * 0.26f), FontStyle.Bold);
        Font valueFont = this.fontCache.GetUi(Math.Max(13.0f, rect.Height * 0.34f), FontStyle.Bold);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush valueBrush = new SolidBrush(color))
        {
            RectangleF titleRect = new RectangleF(rect.Left + S(10), rect.Top + S(8), rect.Width - S(20), rect.Height * 0.34f);
            RectangleF valueRect = new RectangleF(rect.Left + S(10), rect.Top + rect.Height * 0.38f, rect.Width - S(20), rect.Height * 0.44f);
            DrawFittedText(g, title, titleFont, titleBrush, titleRect, StringAlignment.Center);
            DrawFittedText(g, detail, valueFont, valueBrush, valueRect, StringAlignment.Center);
        }
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

    private void DrawMetaLine(Graphics g, RectangleF rect)
    {
        string meta = BuildMetaText();
        Color color = this.snapshot != null && this.snapshot.Running ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted;
        Font font = this.fontCache.GetUi(Math.Max(8.0f, rect.Height * 0.52f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(color))
        {
            DrawFittedText(g, meta, font, brush, rect, StringAlignment.Center);
        }
    }

    private string BuildMetaText()
    {
        if (this.snapshot == null || !this.snapshot.CheckedAtKnown)
        {
            return "cleanip.io · 等待检测";
        }

        string time = this.snapshot.CheckedAtLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string prefix = this.snapshot.TestMode ? "CleanIP测试" : "cleanip.io";
        string status = this.snapshot.Running ? "更新中 · " : string.Empty;
        string trigger = string.IsNullOrWhiteSpace(this.snapshot.RefreshTrigger) ? string.Empty : this.snapshot.RefreshTrigger.Trim() + " · ";
        string left = FirstNonEmpty(this.snapshot.Ip, "--");
        string middle = FirstNonEmpty(this.snapshot.Asn, this.snapshot.Organization, "--");
        return status + prefix + " · " + trigger + left + " · " + middle + " · " + time;
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
        using (GraphicsPath path = new GraphicsPath())
        using (SolidBrush brush = new SolidBrush(color))
        {
            path.AddLines(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.04f),
                new PointF(rect.Right - rect.Width * 0.12f, rect.Top + rect.Height * 0.18f),
                new PointF(rect.Right - rect.Width * 0.18f, rect.Top + rect.Height * 0.62f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Bottom - rect.Height * 0.05f),
                new PointF(rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.62f),
                new PointF(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * 0.18f)
            });
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }

    private void DrawLocationCheckIcon(Graphics g, RectangleF rect, Color color)
    {
        using (GraphicsPath pin = new GraphicsPath())
        using (SolidBrush brush = new SolidBrush(color))
        using (Pen check = new Pen(DesignTokens.Colors.AppBackground, Math.Max(1.2f, S(2))))
        {
            pin.AddEllipse(rect.Left + rect.Width * 0.20f, rect.Top + rect.Height * 0.05f, rect.Width * 0.60f, rect.Height * 0.58f);
            pin.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.50f, rect.Bottom - rect.Height * 0.02f),
                new PointF(rect.Left + rect.Width * 0.30f, rect.Top + rect.Height * 0.50f),
                new PointF(rect.Left + rect.Width * 0.70f, rect.Top + rect.Height * 0.50f)
            });
            g.FillPath(brush, pin);
            g.DrawLines(check, new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.35f, rect.Top + rect.Height * 0.34f),
                new PointF(rect.Left + rect.Width * 0.47f, rect.Top + rect.Height * 0.46f),
                new PointF(rect.Left + rect.Width * 0.67f, rect.Top + rect.Height * 0.26f)
            });
        }
    }

    private void DrawRouterIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        using (SolidBrush brush = new SolidBrush(color))
        {
            RectangleF body = new RectangleF(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * 0.42f, rect.Width * 0.76f, rect.Height * 0.36f);
            g.DrawRectangle(pen, Rectangle.Round(body));
            g.FillEllipse(brush, rect.Left + rect.Width * 0.24f, rect.Top + rect.Height * 0.56f, rect.Width * 0.08f, rect.Height * 0.08f);
            g.FillEllipse(brush, rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.56f, rect.Width * 0.08f, rect.Height * 0.08f);
            g.DrawLine(pen, rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.42f, rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.14f);
            g.DrawArc(pen, rect.Left + rect.Width * 0.32f, rect.Top + rect.Height * 0.06f, rect.Width * 0.36f, rect.Height * 0.28f, 210, 120);
        }
    }

    private void DrawCircleMinusIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.4f, S(2))))
        {
            g.DrawEllipse(pen, rect);
            g.DrawLine(pen, rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.50f, rect.Right - rect.Width * 0.28f, rect.Top + rect.Height * 0.50f);
        }
    }

    private void DrawHouseIcon(Graphics g, RectangleF rect, Color color)
    {
        using (GraphicsPath path = new GraphicsPath())
        using (SolidBrush brush = new SolidBrush(color))
        {
            path.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.48f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.12f),
                new PointF(rect.Right - rect.Width * 0.10f, rect.Top + rect.Height * 0.48f),
                new PointF(rect.Right - rect.Width * 0.18f, rect.Top + rect.Height * 0.48f),
                new PointF(rect.Right - rect.Width * 0.18f, rect.Bottom - rect.Height * 0.12f),
                new PointF(rect.Left + rect.Width * 0.18f, rect.Bottom - rect.Height * 0.12f),
                new PointF(rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.48f)
            });
            g.FillPath(brush, path);
        }
    }

    private void DrawMobileIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(color))
        using (GraphicsPath phone = RoundedRectangle(new RectangleF(rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.06f, rect.Width * 0.44f, rect.Height * 0.88f), rect.Width * 0.10f))
        {
            g.DrawPath(pen, phone);
            g.FillEllipse(brush, rect.Left + rect.Width * 0.46f, rect.Bottom - rect.Height * 0.18f, rect.Width * 0.08f, rect.Width * 0.08f);
        }
    }

    private void DrawBriefcaseIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.3f, S(2))))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 64)))
        {
            RectangleF body = new RectangleF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.34f, rect.Width * 0.80f, rect.Height * 0.52f);
            g.FillRectangle(brush, body);
            g.DrawRectangle(pen, Rectangle.Round(body));
            g.DrawArc(pen, rect.Left + rect.Width * 0.34f, rect.Top + rect.Height * 0.16f, rect.Width * 0.32f, rect.Height * 0.30f, 180, 180);
        }
    }

    private void DrawGraduationIcon(Graphics g, RectangleF rect, Color color)
    {
        using (SolidBrush brush = new SolidBrush(color))
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(1))))
        {
            g.FillPolygon(brush, new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.42f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.18f),
                new PointF(rect.Right - rect.Width * 0.08f, rect.Top + rect.Height * 0.42f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.64f)
            });
            g.DrawLine(pen, rect.Left + rect.Width * 0.72f, rect.Top + rect.Height * 0.48f, rect.Left + rect.Width * 0.72f, rect.Bottom - rect.Height * 0.12f);
            g.FillRectangle(brush, rect.Left + rect.Width * 0.30f, rect.Top + rect.Height * 0.60f, rect.Width * 0.40f, rect.Height * 0.16f);
        }
    }

    private void DrawLandmarkIcon(Graphics g, RectangleF rect, Color color)
    {
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.FillPolygon(brush, new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.34f),
                new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.10f),
                new PointF(rect.Right - rect.Width * 0.10f, rect.Top + rect.Height * 0.34f)
            });
            for (int i = 0; i < 3; i++)
            {
                float x = rect.Left + rect.Width * (0.22f + i * 0.22f);
                g.FillRectangle(brush, x, rect.Top + rect.Height * 0.40f, rect.Width * 0.08f, rect.Height * 0.38f);
            }

            g.FillRectangle(brush, rect.Left + rect.Width * 0.12f, rect.Bottom - rect.Height * 0.16f, rect.Width * 0.76f, rect.Height * 0.10f);
        }
    }

    private void DrawNetworkIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(color))
        {
            RectangleF top = new RectangleF(rect.Left + rect.Width * 0.40f, rect.Top + rect.Height * 0.08f, rect.Width * 0.20f, rect.Height * 0.20f);
            RectangleF left = new RectangleF(rect.Left + rect.Width * 0.12f, rect.Bottom - rect.Height * 0.28f, rect.Width * 0.20f, rect.Height * 0.20f);
            RectangleF right = new RectangleF(rect.Right - rect.Width * 0.32f, rect.Bottom - rect.Height * 0.28f, rect.Width * 0.20f, rect.Height * 0.20f);
            g.DrawLine(pen, top.Left + top.Width / 2, top.Bottom, left.Left + left.Width / 2, left.Top);
            g.DrawLine(pen, top.Left + top.Width / 2, top.Bottom, right.Left + right.Width / 2, right.Top);
            g.FillRectangle(brush, top);
            g.FillRectangle(brush, left);
            g.FillRectangle(brush, right);
        }
    }

    private void DrawServerIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush dot = new SolidBrush(color))
        {
            RectangleF top = new RectangleF(rect.Left + rect.Width * 0.14f, rect.Top + rect.Height * 0.18f, rect.Width * 0.72f, rect.Height * 0.26f);
            RectangleF bottom = new RectangleF(rect.Left + rect.Width * 0.14f, rect.Top + rect.Height * 0.56f, rect.Width * 0.72f, rect.Height * 0.26f);
            g.DrawRectangle(pen, Rectangle.Round(top));
            g.DrawRectangle(pen, Rectangle.Round(bottom));
            g.FillEllipse(dot, top.Left + top.Width * 0.12f, top.Top + top.Height * 0.38f, top.Height * 0.22f, top.Height * 0.22f);
            g.FillEllipse(dot, bottom.Left + bottom.Width * 0.12f, bottom.Top + bottom.Height * 0.38f, bottom.Height * 0.22f, bottom.Height * 0.22f);
        }
    }

    private void DrawMaskIcon(Graphics g, RectangleF rect, Color color)
    {
        using (GraphicsPath path = new GraphicsPath())
        using (SolidBrush brush = new SolidBrush(color))
        using (SolidBrush cutout = new SolidBrush(DesignTokens.Colors.AppBackground))
        {
            path.AddEllipse(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.22f, rect.Width * 0.84f, rect.Height * 0.50f);
            g.FillPath(brush, path);
            g.FillEllipse(cutout, rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.40f, rect.Width * 0.14f, rect.Height * 0.10f);
            g.FillEllipse(cutout, rect.Right - rect.Width * 0.42f, rect.Top + rect.Height * 0.40f, rect.Width * 0.14f, rect.Height * 0.10f);
        }
    }

    private void DrawFilterIcon(Graphics g, RectangleF rect, Color color)
    {
        using (GraphicsPath path = new GraphicsPath())
        using (SolidBrush brush = new SolidBrush(color))
        {
            path.AddPolygon(new PointF[]
            {
                new PointF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.14f),
                new PointF(rect.Right - rect.Width * 0.10f, rect.Top + rect.Height * 0.14f),
                new PointF(rect.Left + rect.Width * 0.58f, rect.Top + rect.Height * 0.56f),
                new PointF(rect.Left + rect.Width * 0.58f, rect.Bottom - rect.Height * 0.12f),
                new PointF(rect.Left + rect.Width * 0.42f, rect.Bottom - rect.Height * 0.22f),
                new PointF(rect.Left + rect.Width * 0.42f, rect.Top + rect.Height * 0.56f)
            });
            g.FillPath(brush, path);
        }
    }

    private void DrawLinkIcon(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.4f, S(2))))
        {
            g.DrawArc(pen, rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.28f, rect.Width * 0.46f, rect.Height * 0.34f, 70, 250);
            g.DrawArc(pen, rect.Right - rect.Width * 0.54f, rect.Top + rect.Height * 0.38f, rect.Width * 0.46f, rect.Height * 0.34f, 250, 250);
            g.DrawLine(pen, rect.Left + rect.Width * 0.38f, rect.Top + rect.Height * 0.56f, rect.Left + rect.Width * 0.62f, rect.Top + rect.Height * 0.44f);
        }
    }

    private void DrawQuestionIcon(Graphics g, RectangleF rect, Color color)
    {
        Font font = this.fontCache.GetUi(Math.Max(10.0f, rect.Height * 0.62f), FontStyle.Bold);
        using (Pen pen = new Pen(color, Math.Max(1.2f, S(2))))
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat format = new StringFormat())
        {
            g.DrawEllipse(pen, rect);
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("?", font, brush, rect, format);
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
            bool refreshNativeBitmap = redrawContent || !this.renderBufferValid;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawBackground(this.renderGraphics);
                DrawContentLayer(this.renderGraphics);
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
