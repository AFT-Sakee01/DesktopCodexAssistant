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
    private bool lastRenderedBurnInColorProtectionActive;
    private bool layeredUpdateFailureLogged;
    private long burnInShiftSlot = long.MinValue;

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
        float compatibilityScale = settings == null ? 1.0f : settings.GetResolutionCompatibilityScaleFactor();
        SetLayerScale(GetCurrentDpiLayerScale() * compatibilityScale);
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
            bool burnInColorProtectionActive = IsLayeredBurnInColorProtectionActive();
            bool refreshNativeBitmap =
                redrawContent ||
                !this.renderBufferValid ||
                burnInColorProtectionActive != this.lastRenderedBurnInColorProtectionActive;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                bool contentReady = TryDrawCachedWindowContent(this.renderGraphics, burnInColorProtectionActive);
                if (!contentReady)
                {
                    DrawWindowContent(this.renderGraphics);
                    if (burnInColorProtectionActive)
                    {
                        BurnInProtection.ApplyHiddenModeColorProtection(this.renderBitmap);
                    }

                    OnLayeredBitmapPrepared(this.renderBitmap, burnInColorProtectionActive);
                }

                this.lastRenderedBurnInColorProtectionActive = burnInColorProtectionActive;
                OnLayeredNativeBitmapRefreshed(burnInColorProtectionActive);
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

    protected static IntPtr GetLayeredWidgetInsertAfter(bool shouldBeTopMost)
    {
        return shouldBeTopMost ? GetLayeredWidgetTopMostInsertAfter() : NativeMethods.HWND_NOTOPMOST;
    }

    protected static IntPtr GetLayeredWidgetInsertAfter(WidgetVisibilityMode visibilityMode)
    {
        return visibilityMode == WidgetVisibilityMode.DesktopOnly ?
            NativeMethods.HWND_TOP :
            GetLayeredWidgetTopMostInsertAfter();
    }

    private static IntPtr GetLayeredWidgetTopMostInsertAfter()
    {
        // SeelenUI owns the shell chrome; use its real topmost HWND as the insert-after target
        // so these widgets stay directly below it instead of racing above the dock/top bar.
        return NativeMethods.GetSeelenAwareTopMostInsertAfter();
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

    protected static int ComputeOpacityAlpha(int transparencyPercent)
    {
        return 255 - DesignTokens.ClampByte(transparencyPercent * 255 / 100);
    }

    protected virtual byte GetApplicationOpacityAlpha()
    {
        return 255;
    }

    protected virtual bool IsLayeredBurnInColorProtectionActive()
    {
        return false;
    }

    protected virtual bool CanRenderLayeredWindow()
    {
        return true;
    }

    protected virtual void DisposeAdditionalRenderBuffers()
    {
    }

    protected virtual void OnLayeredBitmapPrepared(Bitmap bitmap, bool burnInColorProtectionActive)
    {
    }

    protected virtual bool TryDrawCachedWindowContent(Graphics g, bool burnInColorProtectionActive)
    {
        return false;
    }

    protected virtual void OnLayeredNativeBitmapRefreshed(bool burnInColorProtectionActive)
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
}
