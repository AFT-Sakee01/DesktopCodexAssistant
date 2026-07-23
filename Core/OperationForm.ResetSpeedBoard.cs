using System;

// Ownership and cross-window routing for the sixth left-dock member. CodexRadarForm remains the
// data owner; this partial only exposes a cache-only provider and the standard board lifecycle.
internal sealed partial class OperationForm
{
    private ResetSpeedBoardForm resetSpeedBoardForm;

    internal Func<ResetSpeedBoardSnapshot> ResetSpeedSnapshotProvider;

    internal ResetSpeedBoardForm EnsureResetSpeedBoardForm()
    {
        if (this.resetSpeedBoardForm == null || this.resetSpeedBoardForm.IsDisposed)
        {
            this.resetSpeedBoardForm = new ResetSpeedBoardForm(
                this,
                this.CurrentSettings,
                delegate { return ResolveResetSpeedSnapshot(); });
            this.resetSpeedBoardForm.CollapseOtherLeftDockOverlays = delegate
            {
                HideNetworkDockedPanelIfVisible();
            };
        }
        this.resetSpeedBoardForm.PreparePresentationState(this.displaySuspended, AreLeftDockSurfacesHidden());
        this.resetSpeedBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.resetSpeedBoardForm;
    }

    private ResetSpeedBoardSnapshot ResolveResetSpeedSnapshot()
    {
        Func<ResetSpeedBoardSnapshot> provider = this.ResetSpeedSnapshotProvider;
        if (provider == null) return ResetSpeedBoardSnapshot.CreateEmpty();
        try { return provider() ?? ResetSpeedBoardSnapshot.CreateEmpty(); }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return ResetSpeedBoardSnapshot.CreateEmpty();
        }
    }

    internal void HideResetSpeedBoardIfVisible()
    {
        if (this.resetSpeedBoardForm != null && !this.resetSpeedBoardForm.IsDisposed)
            this.resetSpeedBoardForm.HideBoardIfVisible();
    }

    internal void SetResetSpeedBoardHiddenForFullscreen(bool hidden)
    {
        if (this.resetSpeedBoardForm != null && !this.resetSpeedBoardForm.IsDisposed)
            this.resetSpeedBoardForm.SetHiddenForFullscreen(hidden);
    }

    internal void PrepareResetSpeedBoardForDisplaySuspend()
    {
        if (this.resetSpeedBoardForm != null && !this.resetSpeedBoardForm.IsDisposed)
            this.resetSpeedBoardForm.PrepareForDisplaySuspend();
    }

    internal void RecoverResetSpeedBoardAfterDisplayResume()
    {
        if (this.resetSpeedBoardForm != null && !this.resetSpeedBoardForm.IsDisposed)
            this.resetSpeedBoardForm.RecoverAfterDisplayResume();
    }

    internal void PrepareForResetSpeedBoardOverlayShow()
    {
        if (this.radialMenuOpen) CloseRadialMenu();
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.ResetSpeed);
    }

    private void DisposeResetSpeedBoardForm()
    {
        if (this.resetSpeedBoardForm == null) return;
        try
        {
            this.resetSpeedBoardForm.Close();
            this.resetSpeedBoardForm.Dispose();
        }
        catch (ObjectDisposedException) { }
        finally { this.resetSpeedBoardForm = null; }
    }
}
