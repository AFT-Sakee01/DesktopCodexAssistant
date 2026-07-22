using System;

// Ownership and cross-window routing for the fifth left-dock member. CodexRadarForm remains the
// sole data owner; this partial only exposes a cache-only provider to the presentation surface and
// participates in the same mutual-exclusion/lifecycle contract as the other dock boards.
internal sealed partial class OperationForm
{
    private CodexIqBoardForm codexIqBoardForm;

    internal Func<CodexIqBoardSnapshot> CodexIqSnapshotProvider;

    internal CodexIqBoardForm EnsureCodexIqBoardForm()
    {
        if (this.codexIqBoardForm == null || this.codexIqBoardForm.IsDisposed)
        {
            this.codexIqBoardForm = new CodexIqBoardForm(
                this,
                this.CurrentSettings,
                delegate { return ResolveCodexIqSnapshot(); });
            this.codexIqBoardForm.CollapseOtherLeftDockOverlays = delegate
            {
                HideNetworkDockedPanelIfVisible();
            };
        }

        this.codexIqBoardForm.PreparePresentationState(this.displaySuspended, this.hiddenForFullscreen);
        this.codexIqBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.codexIqBoardForm;
    }

    private CodexIqBoardSnapshot ResolveCodexIqSnapshot()
    {
        Func<CodexIqBoardSnapshot> provider = this.CodexIqSnapshotProvider;
        if (provider == null)
        {
            return CodexIqBoardSnapshot.CreateEmpty();
        }

        try
        {
            return provider() ?? CodexIqBoardSnapshot.CreateEmpty();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return CodexIqBoardSnapshot.CreateEmpty();
        }
    }

    internal void HideCodexIqBoardIfVisible()
    {
        if (this.codexIqBoardForm != null && !this.codexIqBoardForm.IsDisposed)
        {
            this.codexIqBoardForm.HideBoardIfVisible();
        }
    }

    internal void SetCodexIqBoardHiddenForFullscreen(bool hidden)
    {
        if (this.codexIqBoardForm != null && !this.codexIqBoardForm.IsDisposed)
        {
            this.codexIqBoardForm.SetHiddenForFullscreen(hidden);
        }
    }

    internal void PrepareCodexIqBoardForDisplaySuspend()
    {
        if (this.codexIqBoardForm != null && !this.codexIqBoardForm.IsDisposed)
        {
            this.codexIqBoardForm.PrepareForDisplaySuspend();
        }
    }

    internal void RecoverCodexIqBoardAfterDisplayResume()
    {
        if (this.codexIqBoardForm != null && !this.codexIqBoardForm.IsDisposed)
        {
            this.codexIqBoardForm.RecoverAfterDisplayResume();
        }
    }

    internal void PrepareForCodexIqBoardOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        HideLauncherTrioIfVisible();
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.CodexIq);
    }

    private void DisposeCodexIqBoardForm()
    {
        if (this.codexIqBoardForm == null)
        {
            return;
        }

        try
        {
            this.codexIqBoardForm.Close();
            this.codexIqBoardForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.codexIqBoardForm = null;
        }
    }
}
