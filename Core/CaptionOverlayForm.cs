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
internal enum CaptionOverlayDragKind
{
    None,
    Move,
    ResizeLeft,
    ResizeRight,
}

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
    // Edit mode: the strip stops being click-through so it can be dragged and resized, and draws
    // a frame with edge handles. Entered and left from the captions board, because the strip has
    // no chrome of its own and a mode you can only leave from the thing you are dragging is a trap.
    private bool editMode;
    private Point dragStartScreen;
    private Rectangle dragStartBounds;
    private CaptionOverlayDragKind dragKind = CaptionOverlayDragKind.None;

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

    internal bool IsEditing
    {
        get { return this.editMode; }
    }

    internal void SetEditMode(bool enabled)
    {
        if (this.editMode == enabled || this.IsDisposed)
        {
            return;
        }

        this.editMode = enabled;
        this.dragKind = CaptionOverlayDragKind.None;
        // Click-through is what makes the strip safe to leave over a video; it is also what makes
        // it impossible to grab, so it comes off for exactly as long as the user is placing it.
        ApplyMouseClickThroughStyle(!enabled);
        this.Cursor = enabled ? Cursors.SizeAll : Cursors.Default;
        this.lastRenderSignature = string.Empty;
        this.lastWorkArea = Rectangle.Empty;
        if (enabled)
        {
            // Something has to be on screen to drag. An empty strip in edit mode shows its frame
            // and nothing else, which is enough to place it before anyone has said a word.
            if (!this.Visible)
            {
                ApplyGeometry(ResolveWorkArea());
                ShowOverlay();
            }

            RenderLayeredWindow();
            return;
        }

        if (!ShouldBeVisible())
        {
            HideOverlay();
            return;
        }

        RenderLayeredWindow();
    }

    // Current bounds in logical pixels, for the board to persist when edit mode ends.
    internal Rectangle GetLogicalBounds()
    {
        float scale = this.LayerScale <= 0 ? 1.0f : this.LayerScale;
        return new Rectangle(
            (int)Math.Round(this.Left / scale),
            (int)Math.Round(this.Top / scale),
            (int)Math.Round(this.Width / scale),
            (int)Math.Round(this.Height / scale));
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

        // Hidden by the board's 隐藏 button: the banner goes away, the chain behind it does not.
        // The reader keeps polling and the article keeps recording -- this flag reaches no further
        // than whether anything is painted. Edit mode is checked after it on purpose: there is
        // nothing to place while the strip is hidden, and the board refuses to enter edit mode then.
        if (!this.CurrentSettings.CaptionOverlayDisplayEnabled)
        {
            return false;
        }

        if (this.editMode)
        {
            // Placing the strip is impossible if it vanishes whenever the speaker pauses.
            return true;
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
        // While the user is placing the strip their rectangle is the truth; recomputing it
        // underneath them would fight the drag.
        if (this.editMode)
        {
            this.renderWidth = this.Width;
            return;
        }

        Rectangle bounds;
        if (TryResolveSavedBounds(workArea, out bounds))
        {
            // A saved rectangle keeps the width and position the user chose but still follows the
            // measured text height, so changing the font size or the history count does not leave
            // the strip clipped or padded.
            bounds.Height = MeasureDesiredHeight(bounds.Width);
            bounds.Y = Math.Max(workArea.Top, Math.Min(bounds.Y, workArea.Bottom - bounds.Height));
        }
        else
        {
            int autoHeight = MeasureDesiredHeight(workArea.Width);
            int top = workArea.Top + (int)Math.Round(workArea.Height * (this.CurrentSettings.CaptionOverlayTopPercent / 100.0));
            // Never let the strip hang off the bottom of the work area, whatever the stored
            // percentage says about a screen that has since changed size.
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - autoHeight));
            bounds = new Rectangle(workArea.Left, top, workArea.Width, autoHeight);
        }

        this.renderWidth = bounds.Width;
        if (this.Bounds != bounds)
        {
            this.Bounds = bounds;
            InvalidateLayeredRenderBuffer();
        }
    }

    // Saved bounds are logical pixels and all-or-nothing (Normalize enforces that), scaled here to
    // the device pixels the window lives in. A rectangle that no longer touches the work area is
    // ignored rather than clamped: falling back to the default band is easier to recover from than
    // a strip squeezed against an edge of a screen that has since changed size.
    private bool TryResolveSavedBounds(Rectangle workArea, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (this.CurrentSettings == null ||
            this.CurrentSettings.CaptionOverlayWidth == WidgetSettings.AutoCaptionOverlayBounds)
        {
            return false;
        }

        float scale = this.LayerScale <= 0 ? 1.0f : this.LayerScale;
        Rectangle candidate = new Rectangle(
            (int)Math.Round(this.CurrentSettings.CaptionOverlayLeft * scale),
            (int)Math.Round(this.CurrentSettings.CaptionOverlayTop * scale),
            (int)Math.Round(this.CurrentSettings.CaptionOverlayWidth * scale),
            (int)Math.Round(this.CurrentSettings.CaptionOverlayHeight * scale));
        if (candidate.Width <= 0 || candidate.Height <= 0 || !workArea.IntersectsWith(candidate))
        {
            return false;
        }

        bounds = candidate;
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!this.editMode || e.Button != MouseButtons.Left)
        {
            return;
        }

        this.dragKind = ResolveDragKind(e.Location);
        this.dragStartScreen = Cursor.Position;
        this.dragStartBounds = this.Bounds;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!this.editMode)
        {
            return;
        }

        if (this.dragKind == CaptionOverlayDragKind.None)
        {
            this.Cursor = ResolveDragKind(e.Location) == CaptionOverlayDragKind.Move
                ? Cursors.SizeAll
                : Cursors.SizeWE;
            return;
        }

        // Deltas come from the screen cursor, not from e.Location: the window moves under the
        // pointer while the drag runs, so client coordinates would feed the move back into itself.
        Point now = Cursor.Position;
        int dx = now.X - this.dragStartScreen.X;
        int dy = now.Y - this.dragStartScreen.Y;
        Rectangle start = this.dragStartBounds;
        float scale = this.LayerScale <= 0 ? 1.0f : this.LayerScale;
        int minWidth = Math.Max(1, (int)Math.Round(WidgetSettings.MinCaptionOverlayWidth * scale));
        Rectangle next = start;
        switch (this.dragKind)
        {
            case CaptionOverlayDragKind.Move:
                next.X = start.X + dx;
                next.Y = start.Y + dy;
                break;

            case CaptionOverlayDragKind.ResizeLeft:
                // The right edge stays put while the left one moves, which is what grabbing a left
                // edge means; moving both would read as dragging the whole strip.
                next.X = Math.Min(start.X + dx, start.Right - minWidth);
                next.Width = start.Right - next.X;
                break;

            case CaptionOverlayDragKind.ResizeRight:
                next.Width = Math.Max(minWidth, start.Width + dx);
                break;
        }

        if (next != this.Bounds)
        {
            this.Bounds = next;
            this.renderWidth = next.Width;
            InvalidateLayeredRenderBuffer();
            RenderLayeredWindow();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        this.dragKind = CaptionOverlayDragKind.None;
    }

    // Height is not draggable: it follows the measured text, so a dragged height would be
    // overwritten by the next caption. Width and position are the parts the user owns.
    private CaptionOverlayDragKind ResolveDragKind(Point location)
    {
        int grip = S(EditGripLogical);
        if (location.X <= grip)
        {
            return CaptionOverlayDragKind.ResizeLeft;
        }

        if (location.X >= this.Width - grip)
        {
            return CaptionOverlayDragKind.ResizeRight;
        }

        return CaptionOverlayDragKind.Move;
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
