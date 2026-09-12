using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal abstract class LayeredWidgetFormBase : Form
{
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private bool renderBufferValid;
    private bool layeredUpdateFailureLogged;
    private System.Windows.Forms.Timer hoverTimer;
    private Func<Point> cursorPositionProvider;
    private bool pointerInside;
    private int pendingInside = -1;
    private int pendingTicks;
    private long burnInShiftSlot = long.MinValue;
    private int lastPresentationLuminancePercent = -1;

    // Hover polling for layered surfaces, on the numbers in HoverPollPolicy -- the same clock and the
    // same debounce MetricTileForm and EdgeDockTabForm have always used, so a surface that opts in
    // here behaves exactly like the right tiles and the left dock tabs.

    protected float LayerScale { get; private set; } = 1.0f;

    protected WidgetSettings CurrentSettings { get; set; }

    protected Bitmap LayeredRenderBitmap
    {
        get { return this.renderBitmap; }
    }

    protected Graphics LayeredRenderGraphics
    {
        get { return this.renderGraphics; }
    }

    protected bool IsLayeredRenderBufferValid
    {
        get { return this.renderBufferValid; }
    }

    protected bool IsPointerInside
    {
        get { return this.pointerInside; }
    }

    protected bool IsHoverPollingActive
    {
        get { return this.hoverTimer != null && this.hoverTimer.Enabled; }
    }

    // Injectable so a self-test can put the pointer somewhere without moving the real one.
    internal Func<Point> CursorPositionProvider
    {
        get { return this.cursorPositionProvider; }
        set { this.cursorPositionProvider = value; }
    }

    // The rectangle the pointer is tested against. Override when a surface owns screen area beyond
    // its own window -- MetricTileForm's expand panel is the case that needs it.
    protected virtual Rectangle HoverPollBounds
    {
        get { return this.Bounds; }
    }

    // Opt-in: the timer does not exist until a surface asks for it, so the surfaces that do not
    // poll pay nothing.
    protected void StartHoverPolling()
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (this.hoverTimer == null)
        {
            this.hoverTimer = new System.Windows.Forms.Timer();
            this.hoverTimer.Interval = HoverPollPolicy.IntervalMs;
            this.hoverTimer.Tick += OnHoverPollTick;
        }

        this.hoverTimer.Start();
    }

    // Clears the hover state as well as stopping the clock: a surface that comes back still marked
    // "pointer inside" would keep whatever hover appearance it had until the pointer moved again.
    protected void StopHoverPolling()
    {
        if (this.hoverTimer != null)
        {
            this.hoverTimer.Stop();
        }

        this.pendingInside = -1;
        this.pendingTicks = 0;
        if (this.pointerInside)
        {
            this.pointerInside = false;
            OnPointerInsideChanged(false);
        }
    }

    // Called on the UI thread when the debounced hover state flips. The base does nothing: what a
    // hover means is the surface's business -- a tile redraws, the caption strip only re-blends.
    protected virtual void OnPointerInsideChanged(bool inside)
    {
    }

    // Whether this poll should be evaluated at all. A surface that is not on screen has no hover.
    protected virtual bool CanPollHover()
    {
        return this.Visible && CanRenderLayeredWindow();
    }

    // One poll, synchronously. The debounce is counted in ticks, so a test that wants to prove it
    // has to be able to produce ticks without waiting 120 ms for each one.
    internal void PollHoverForSelfTest()
    {
        OnHoverPollTick(null, EventArgs.Empty);
    }

    private void OnHoverPollTick(object sender, EventArgs e)
    {
        if (this.IsDisposed || !CanPollHover())
        {
            return;
        }

        bool inside;
        try
        {
            Func<Point> provider = this.cursorPositionProvider;
            Point cursor = provider == null ? Cursor.Position : provider();
            inside = HoverPollBounds.Contains(cursor);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return;
        }

        if (inside == this.pointerInside)
        {
            this.pendingInside = -1;
            this.pendingTicks = 0;
            return;
        }

        int desired = inside ? 1 : 0;
        if (desired != this.pendingInside)
        {
            this.pendingInside = desired;
            this.pendingTicks = 0;
        }

        this.pendingTicks++;
        if (this.pendingTicks < (inside ? HoverPollPolicy.EnterTicks : HoverPollPolicy.ExitTicks))
        {
            return;
        }

        this.pendingInside = -1;
        this.pendingTicks = 0;
        this.pointerInside = inside;
        OnPointerInsideChanged(inside);
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

    protected virtual string LayeredWindowLogName
    {
        get { return this.GetType().Name; }
    }

    protected virtual string LayeredRenderTimingName
    {
        get { return string.Empty; }
    }

    protected void InitializeLayerScaleFromCurrentDpi()
    {
        SetLayerScale(GetCurrentDpiLayerScale());
    }

    protected void ApplyLayerScaleFromSettings(WidgetSettings settings)
    {
        float compatibilityScale = ResolveWindowScaleFactor(settings, this.WindowScaleOverridePercent);
        SetLayerScale(GetCurrentDpiLayerScale() * compatibilityScale);
    }

    protected Size ScaleWindowSize(Size logicalSize)
    {
        return ScaleWindowSize(logicalSize, this.CurrentSettings, this.WindowScaleOverridePercent);
    }

    private static Size ScaleWindowSize(Size logicalSize, WidgetSettings settings, int windowOverridePercent)
    {
        float scale = ResolveWindowScaleFactor(settings, windowOverridePercent);
        return new Size(
            Math.Max(1, (int)Math.Round(logicalSize.Width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(logicalSize.Height * scale, MidpointRounding.AwayFromZero)));
    }

    protected virtual int WindowScaleOverridePercent
    {
        get { return WidgetSettings.MinWindowScaleOverridePercent; }
    }

    private static float ResolveWindowScaleFactor(WidgetSettings settings, int windowOverridePercent)
    {
        if (windowOverridePercent >= WidgetSettings.MinResolutionCompatibilityScalePercent)
        {
            int normalized = Math.Min(WidgetSettings.MaxWindowScaleOverridePercent, windowOverridePercent);
            return normalized / 100.0f;
        }

        return settings == null ? 1.0f : settings.GetResolutionCompatibilityScaleFactor();
    }

    protected void SetLayerScale(float scale)
    {
        // Resolution compatibility mode intentionally allows compression below the
        // physical DPI scale. Keep this below the 40% settings floor so low-DPI
        // preview displays do not render content larger than their scaled window.
        float next = Math.Max(0.25f, scale);
        if (Math.Abs(this.LayerScale - next) < 0.001f)
        {
            return;
        }

        this.LayerScale = next;
        InvalidateLayeredRenderBuffer();
    }

    protected void RenderLayeredWindow()
    {
        RenderLayeredWindow(true);
    }

    protected void RenderLayeredWindow(bool redrawContent)
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0 || !CanRenderLayeredWindow())
        {
            return;
        }

        string timingName = this.LayeredRenderTimingName;
        long renderStart = string.IsNullOrEmpty(timingName) ? 0L : TimingStats.StartTimestamp();
        try
        {
            EnsureRenderBuffer();
            int presentationLuminancePercent = GetActivePresentationLuminancePercent();
            bool refreshNativeBitmap =
                redrawContent ||
                !this.renderBufferValid ||
                presentationLuminancePercent != this.lastPresentationLuminancePercent;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawWindowContent(this.renderGraphics);

                if (presentationLuminancePercent < 100)
                {
                    BurnInProtection.ApplyLuminance(this.renderBitmap, presentationLuminancePercent);
                }

                this.lastPresentationLuminancePercent = presentationLuminancePercent;
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
                    Program.LogInfo(this.LayeredWindowLogName + " UpdateLayeredWindow failed; falling back to normal paint.");
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
        finally
        {
            if (!string.IsNullOrEmpty(timingName))
            {
                TimingStats.RecordElapsed(timingName, renderStart);
            }
        }
    }

    protected void DisposeRenderBuffer()
    {
        DisposeAdditionalRenderBuffers();

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

    protected void InvalidateLayeredRenderBuffer()
    {
        this.renderBufferValid = false;
    }

    protected void ResetDisplayRenderResources()
    {
        DisposeRenderBuffer();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
    }

    protected static IntPtr GetLayeredWidgetInsertAfter(bool shouldBeTopMost, bool keepBelowCodexPet)
    {
        return shouldBeTopMost ? GetLayeredWidgetTopMostInsertAfter(keepBelowCodexPet) : NativeMethods.HWND_NOTOPMOST;
    }

    protected static IntPtr GetLayeredWidgetInsertAfter(WidgetVisibilityMode visibilityMode, bool keepBelowCodexPet)
    {
        return visibilityMode == WidgetVisibilityMode.DesktopOnly ?
            NativeMethods.HWND_TOP :
            GetLayeredWidgetTopMostInsertAfter(keepBelowCodexPet);
    }

    // Right-edge tiles still discover hover by polling Cursor.Position, so making their HWNDs
    // transparent to hit-testing does not disable expansion. Keep the style mutation here so every
    // layered right-side surface preserves WS_EX_LAYERED and refreshes the non-client cache in the
    // same way when the setting changes at runtime.
    protected void ApplyMouseClickThroughStyle(bool enabled)
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = enabled
            ? (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED)
            : ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);
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

    private static IntPtr GetLayeredWidgetTopMostInsertAfter(bool keepBelowCodexPet)
    {
        // Normalize the protected stack before choosing its lowest HWND. Otherwise an unrelated
        // TopMost app created later can remain above both the protected surfaces and our widgets.
        return NativeMethods.PrepareSeelenAwareTopMostInsertAfter(keepBelowCodexPet);
    }

    // 自检要验证的正是真实的显示/隐藏策略，断言直接读 Visible，所以那些窗口不能改成
    // 隐藏创建。但它们是回归测试而不是给用户看的界面：--test 系列一跑，桌面上就会依次闪过
    // 巨大的右侧展开面板、左侧操作面板和设置窗口，看起来就像程序启动时的一串丑陋加载。
    // 折中做法是自检期间把可见层整体平移出屏幕：Visible 仍为 true，断言不受影响，
    // 用户什么也看不到。偏移量取 40000，远超任何多显示器桌面的坐标范围。
    private const int OffscreenSelfTestShift = 40000;

    internal static bool OffscreenPresentationForSelfTest { get; set; }

    // 兜底：自检里很多定位分支要求 owner 非空（左侧停靠路径尤其如此），owner 为 null 时
    // PositionForDisplay 会直接早返回、根本不设 Location，窗口于是停在默认位置露在桌面上。
    // 这里只在窗口真正转为可见的那一刻强制一次离屏坐标——不显示窗口的几何断言完全不受影响，
    // 各处 SetWindowPos 传的又是 this.Left / this.Top，所以跟着一起离屏。
    protected override void SetVisibleCore(bool value)
    {
        if (value && OffscreenPresentationForSelfTest)
        {
            this.Location = ApplySelfTestOffscreenOffset(Point.Empty);
        }

        base.SetVisibleCore(value);
    }

    internal static Point ApplySelfTestOffscreenOffset(Point location)
    {
        if (!OffscreenPresentationForSelfTest)
        {
            return location;
        }

        return new Point(location.X - OffscreenSelfTestShift, location.Y - OffscreenSelfTestShift);
    }


    protected int S(int value)
    {
        return (int)Math.Round(value * this.LayerScale);
    }

    protected int S(float value)
    {
        return Math.Max(1, (int)Math.Round(value * this.LayerScale));
    }

    protected static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Max(0.0f, radius * 2.0f);
        GraphicsPath path = new GraphicsPath();
        if (diameter <= 0.0f)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected bool ShouldRefreshBurnInPosition()
    {
        return BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot);
    }

    protected void RefreshNightScheduleAtExistingTick()
    {
        if (!this.Visible)
        {
            return;
        }

        int next = GetActivePresentationLuminancePercent();
        if (next == this.lastPresentationLuminancePercent)
        {
            return;
        }

        InvalidateLayeredRenderBuffer();
        RenderLayeredWindow();
    }

    // Derived visible surfaces can contribute a presentation-specific luminance without duplicating
    // bitmap walking. Multiplication composes burn-in dimming with the independent night schedule and
    // never lets either policy brighten pixels suppressed by the other.
    protected virtual int PresentationLuminancePercent
    {
        get { return 100; }
    }

    private int GetActivePresentationLuminancePercent()
    {
        int night = NightScheduleController.GetActiveLuminancePercent(this.CurrentSettings, DateTime.Now);
        int presentation = Math.Max(0, Math.Min(100, this.PresentationLuminancePercent));
        return Math.Max(0, Math.Min(100, (int)Math.Round(night * presentation / 100.0)));
    }

    protected static int ComputeOpacityAlpha(int transparencyPercent)
    {
        return 255 - DesignTokens.ClampByte(transparencyPercent * 255 / 100);
    }

    protected virtual int WindowTransparencyOverridePercent
    {
        get { return -1; }
    }

    protected byte GetApplicationOpacityAlpha()
    {
        int windowOverride = this.WindowTransparencyOverridePercent;
        int transparencyPercent = windowOverride >= 0
            ? windowOverride
            : (this.CurrentSettings == null ? 0 : this.CurrentSettings.ApplicationTransparencyPercent);
        transparencyPercent = Math.Max(0, Math.Min(WidgetSettings.MaxWindowTransparencyOverridePercent, transparencyPercent));
        return (byte)DesignTokens.ClampByte(ComputeOpacityAlpha(transparencyPercent));
    }

    internal static void RunOpacityPolicySelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.ApplicationTransparencyPercent = 50;
        using (OpacityPolicyProbeForm followGlobal = new OpacityPolicyProbeForm(settings, -1))
        using (OpacityPolicyProbeForm windowOverride = new OpacityPolicyProbeForm(settings, 60))
        {
            if (followGlobal.ReadApplicationAlpha() != ComputeOpacityAlpha(50) ||
                windowOverride.ReadApplicationAlpha() != ComputeOpacityAlpha(60))
            {
                throw new InvalidOperationException("Layered window opacity policy did not apply global and window override values in order.");
            }
        }

        Console.WriteLine("Layered window opacity policy: PASS global=50 override=60");
    }

    internal static void RunScalePolicySelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.ResolutionCompatibilityModeEnabled = true;
        settings.ResolutionCompatibilityScalePercent = 80;
        float followsGlobal = ResolveWindowScaleFactor(settings, WidgetSettings.MinWindowScaleOverridePercent);
        float windowOverride = ResolveWindowScaleFactor(settings, 125);
        float windowClamp = ResolveWindowScaleFactor(settings, int.MaxValue);
        Size followsGlobalSize = ScaleWindowSize(new Size(200, 100), settings, WidgetSettings.MinWindowScaleOverridePercent);
        Size overrideSize = ScaleWindowSize(new Size(200, 100), settings, 150);
        if (Math.Abs(followsGlobal - 0.80f) > 0.001f ||
            Math.Abs(windowOverride - 1.25f) > 0.001f ||
            Math.Abs(windowClamp - 2.0f) > 0.001f ||
            followsGlobalSize != new Size(160, 80) ||
            overrideSize != new Size(300, 150))
        {
            throw new InvalidOperationException("Layered window scale policy did not apply global compatibility and per-window override to content and bounds.");
        }

        Console.WriteLine("Layered window scale policy: PASS global=80 override=125 clamp=200 bounds=150%");
    }

    protected virtual bool CanRenderLayeredWindow()
    {
        return true;
    }

    protected virtual void DisposeAdditionalRenderBuffers()
    {
    }

    protected abstract void DrawWindowContent(Graphics g);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (this.hoverTimer != null)
            {
                this.hoverTimer.Stop();
                this.hoverTimer.Tick -= OnHoverPollTick;
                this.hoverTimer.Dispose();
                this.hoverTimer = null;
            }

            DisposeRenderBuffer();
            this.layeredSurface.Dispose();
        }

        base.Dispose(disposing);
    }

    protected void EnsureRenderBuffer()
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

    private float GetCurrentDpiLayerScale()
    {
        try
        {
            using (Graphics g = this.CreateGraphics())
            {
                return Math.Max(1.0f, g.DpiX / 96.0f);
            }
        }
        catch
        {
            return 1.0f;
        }
    }

    private sealed class OpacityPolicyProbeForm : LayeredWidgetFormBase
    {
        private readonly int transparencyOverride;

        public OpacityPolicyProbeForm(WidgetSettings settings, int transparencyOverride)
        {
            this.CurrentSettings = settings;
            this.transparencyOverride = transparencyOverride;
        }

        protected override int WindowTransparencyOverridePercent
        {
            get { return this.transparencyOverride; }
        }

        internal byte ReadApplicationAlpha()
        {
            return GetApplicationOpacityAlpha();
        }

        protected override void DrawWindowContent(Graphics g)
        {
        }
    }
}
