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
    private long burnInShiftSlot = long.MinValue;
    private int lastPresentationLuminancePercent = -1;

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
