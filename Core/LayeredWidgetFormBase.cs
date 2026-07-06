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

    protected float LayerScale { get; private set; } = 1.0f;

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

    protected void InitializeLayerScaleFromCurrentDpi()
    {
        using (Graphics g = this.CreateGraphics())
        {
            SetLayerScale(Math.Max(1.0f, g.DpiX / 96.0f));
        }
    }

    protected void SetLayerScale(float scale)
    {
        this.LayerScale = Math.Max(1.0f, scale);
    }

    protected void RenderLayeredWindow()
    {
        RenderLayeredWindow(true);
    }

    protected void RenderLayeredWindow(bool redrawContent)
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

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
                DrawWindowContent(this.renderGraphics);
                if (burnInColorProtectionActive)
                {
                    BurnInProtection.ApplyHiddenModeColorProtection(this.renderBitmap);
                }

                OnLayeredBitmapPrepared(this.renderBitmap, burnInColorProtectionActive);
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

    protected void ResetDisplayRenderResources()
    {
        DisposeRenderBuffer();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
    }

    protected int S(int value)
    {
        return (int)Math.Round(value * this.LayerScale);
    }

    protected static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
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

    protected virtual byte GetApplicationOpacityAlpha()
    {
        return 255;
    }

    protected virtual bool IsLayeredBurnInColorProtectionActive()
    {
        return false;
    }

    protected virtual void DisposeAdditionalRenderBuffers()
    {
    }

    protected virtual void OnLayeredBitmapPrepared(Bitmap bitmap, bool burnInColorProtectionActive)
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
}
