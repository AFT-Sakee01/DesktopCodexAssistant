using System;
using System.Drawing;
using System.Windows.Forms;

// This app's own caption panel: a click-through, full-width strip that renders the translator's live
// text so LiveCaptionsTranslator can sit fully in the background.
//
// It exists because the translator's own overlay cannot be made to behave: it is created only by a
// button click (no persisted setting), it is lost on every relaunch, and its styling is limited to
// the handful of fields in its setting.json. Rendering the same text here means the caption strip
// survives restarts, follows this app's typography and OLED rules, and leaves the translator with no
// visible window at all.
//
// Deliberately NOT one of the eleven tiles or seven dock boards: it is not part of the dock queue, it
// takes no layout-editor slot, it never accepts a click (WS_EX_TRANSPARENT throughout), and its
// position comes from two settings rather than from the layout editor. See AGENTS.md "Product Scope".
internal sealed partial class CaptionOverlayForm : LayeredWidgetFormBase
{
    private readonly UiFontCache fontCache = new UiFontCache();
    private TranslatorCaptionSnapshot snapshot = TranslatorCaptionSnapshot.CreateEmpty();
    private string lastRenderSignature = string.Empty;
    private Rectangle lastWorkArea = Rectangle.Empty;
    // Width the content is laid out against. Not simply this.Width: Windows clamps a Form to
    // SystemInformation.MaxWindowTrackSize, so on a 1440-wide screen a strip asked to be 2880 device
    // pixels wide (the render harness at LayerScale 2) silently becomes ~1456 and every centred line
    // lands in the left third. The layout therefore uses the width it was told to use.
    private int renderWidth;
    private bool displaySuspended;

    internal CaptionOverlayForm(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "字幕面板";
        this.AccessibleName = "字幕面板";
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        // Always topmost and always click-through. A caption strip that can take a click is a caption
        // strip that eats the play/pause you aimed at the video underneath it.
        this.TopMost = true;
        ApplyMouseClickThroughStyle(true);
    }

    protected override string LayeredWindowLogName
    {
        get { return "caption overlay"; }
    }

    protected override string LayeredRenderTimingName
    {
        get { return "caption_overlay.render"; }
    }

    // The caption strip is exempt from burn-in dimming on purpose: its whole job is to be readable
    // while the user sits still watching a video, which is exactly the state burn-in protection
    // treats as idle. Its content changes constantly anyway, so it is not a static-pixel risk.
    protected override int PresentationLuminancePercent
    {
        get { return 100; }
    }

    internal void ApplySettings(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        // Force the next update through: font size, position and the original-line toggle all change
        // geometry, and the caption text alone may not have changed.
        this.lastRenderSignature = string.Empty;
        this.lastWorkArea = Rectangle.Empty;
    }

    internal void SetDisplaySuspended(bool suspended)
    {
        if (this.displaySuspended == suspended)
        {
            return;
        }

        this.displaySuspended = suspended;
        if (suspended)
        {
            ResetDisplayRenderResources();
            HideOverlay();
        }
        else
        {
            this.lastRenderSignature = string.Empty;
            this.lastWorkArea = Rectangle.Empty;
        }
    }

    // Single entry point from the host. Decides visibility, geometry and whether a repaint is needed.
    internal void UpdateSnapshot(TranslatorCaptionSnapshot next)
    {
        if (this.IsDisposed || this.displaySuspended)
        {
            return;
        }

        this.snapshot = next ?? TranslatorCaptionSnapshot.CreateEmpty();
        if (!ShouldBeVisible())
        {
            HideOverlay();
            return;
        }

        Rectangle workArea = ResolveWorkArea();
        string signature = this.snapshot.BuildRenderSignature();
        bool geometryChanged = workArea != this.lastWorkArea;
        if (!geometryChanged && string.Equals(signature, this.lastRenderSignature, StringComparison.Ordinal) && this.Visible)
        {
            return;
        }

        this.lastRenderSignature = signature;
        this.lastWorkArea = workArea;
        ApplyGeometry(workArea);
        if (!this.Visible)
        {
            ShowOverlay();
        }

        RenderLayeredWindow();
    }

    private bool ShouldBeVisible()
    {
        if (this.CurrentSettings == null || !this.CurrentSettings.CaptionOverlayEnabled)
        {
            return false;
        }

        // Nothing to say, nothing on screen. An empty strip parked over the video is worse than no
        // strip at all, and the translator legitimately goes quiet between sentences.
        return this.snapshot.TranslatorRunning && this.snapshot.HasText();
    }

    private Rectangle ResolveWorkArea()
    {
        try
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen != null)
            {
                return screen.WorkingArea;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return new Rectangle(0, 0, 1280, 720);
    }

    private void ApplyGeometry(Rectangle workArea)
    {
        int height = MeasureDesiredHeight(workArea.Width);
        int top = workArea.Top + (int)Math.Round(workArea.Height * (this.CurrentSettings.CaptionOverlayTopPercent / 100.0));
        // Never let the strip hang off the bottom of the work area, whatever the stored percentage
        // says about a screen that has since changed size.
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - height));
        Rectangle bounds = new Rectangle(workArea.Left, top, workArea.Width, height);
        this.renderWidth = bounds.Width;
        if (this.Bounds != bounds)
        {
            this.Bounds = bounds;
            InvalidateLayeredRenderBuffer();
        }
    }

    private void ShowOverlay()
    {
        if (this.Visible)
        {
            return;
        }

        // ShowWithoutActivation keeps focus in the video player; the base class already returns true
        // for it, so Show() never steals the foreground. The insert-after handle is the same
        // Seelen/Codex-aware one every other top-most surface here uses, so the strip lands above the
        // video but below the protected pet/dock stack.
        Show();
        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(true, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);
    }

    private void HideOverlay()
    {
        this.lastRenderSignature = string.Empty;
        if (this.Visible)
        {
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.fontCache.Dispose();
        }

        base.Dispose(disposing);
    }
}
