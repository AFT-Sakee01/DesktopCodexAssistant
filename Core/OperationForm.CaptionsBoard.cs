using System;

// Ownership and cross-window routing for the eighth (and newest) left-dock member. TranslatorControlReader
// remains the data owner (constructed/started/stopped by WidgetForm, see WidgetForm.cs); this partial
// only exposes a reader-resolving provider and the standard board lifecycle, matching
// OperationForm.ResetSpeedBoard.cs's shape exactly.
internal sealed partial class OperationForm
{
    private CaptionsBoardForm captionsBoardForm;

    // Set by WidgetForm once it constructs and starts TranslatorControlReader, the same way
    // ResetSpeedSnapshotProvider/CodexIqSnapshotProvider are wired.
    internal Func<TranslatorControlReader> TranslatorControlReaderProvider;

    // The caption strip's two owners, wired the same way. The board needs the caption reader for the
    // article it draws (the reader is where a sentence is first known to be finished) and the overlay
    // itself for edit mode -- the strip is click-through, so its own move/resize controls have to live
    // on a surface that accepts clicks.
    internal Func<TranslatorCaptionReader> TranslatorCaptionReaderProvider;
    internal Func<CaptionOverlayForm> CaptionOverlayProvider;

    internal CaptionsBoardForm EnsureCaptionsBoardForm()
    {
        if (this.captionsBoardForm == null || this.captionsBoardForm.IsDisposed)
        {
            this.captionsBoardForm = new CaptionsBoardForm(this, this.CurrentSettings, ResolveTranslatorControlReader);
            this.captionsBoardForm.CaptionReaderProvider = ResolveTranslatorCaptionReader;
            this.captionsBoardForm.CaptionOverlayProvider = ResolveCaptionOverlay;
            this.captionsBoardForm.CollapseOtherLeftDockOverlays = delegate
            {
                HideNetworkDockedPanelIfVisible();
            };
        }

        this.captionsBoardForm.PreparePresentationState(this.displaySuspended, AreLeftDockSurfacesHidden());
        this.captionsBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.captionsBoardForm;
    }

    private TranslatorControlReader ResolveTranslatorControlReader()
    {
        Func<TranslatorControlReader> provider = this.TranslatorControlReaderProvider;
        return provider == null ? null : provider();
    }

    private TranslatorCaptionReader ResolveTranslatorCaptionReader()
    {
        Func<TranslatorCaptionReader> provider = this.TranslatorCaptionReaderProvider;
        return provider == null ? null : provider();
    }

    private CaptionOverlayForm ResolveCaptionOverlay()
    {
        Func<CaptionOverlayForm> provider = this.CaptionOverlayProvider;
        return provider == null ? null : provider();
    }

    internal void HideCaptionsBoardIfVisible()
    {
        if (this.captionsBoardForm != null && !this.captionsBoardForm.IsDisposed)
        {
            this.captionsBoardForm.HideBoardIfVisible();
        }
    }

    internal void SetCaptionsBoardHiddenForFullscreen(bool hidden)
    {
        if (this.captionsBoardForm != null && !this.captionsBoardForm.IsDisposed)
        {
            this.captionsBoardForm.SetHiddenForFullscreen(hidden);
        }
    }

    internal void PrepareCaptionsBoardForDisplaySuspend()
    {
        if (this.captionsBoardForm != null && !this.captionsBoardForm.IsDisposed)
        {
            this.captionsBoardForm.PrepareForDisplaySuspend();
        }
    }

    internal void RecoverCaptionsBoardAfterDisplayResume()
    {
        if (this.captionsBoardForm != null && !this.captionsBoardForm.IsDisposed)
        {
            this.captionsBoardForm.RecoverAfterDisplayResume();
        }
    }

    internal void PrepareForCaptionsBoardOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        CollapseLeftDockBoardsExcept(LeftDockBoardKind.Captions);
    }

    private void DisposeCaptionsBoardForm()
    {
        if (this.captionsBoardForm == null)
        {
            return;
        }

        try
        {
            this.captionsBoardForm.Close();
            this.captionsBoardForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.captionsBoardForm = null;
        }
    }
}
