using System;

// Ownership and cross-window routing for the sixth left-dock member. CodexRadarForm remains the
// data owner; this partial only exposes a cache-only provider and the standard board lifecycle.
internal sealed partial class OperationForm
{
    private ResetSpeedBoardForm resetSpeedBoardForm;

    internal Func<ResetSpeedBoardSnapshot> ResetSpeedSnapshotProvider;

    // Codex account switch requested from the board. Routed through the same hidden-host ownership
    // as the snapshot provider so the board never reaches into CodexRadarForm or auth.json itself.
    internal Func<string, CodexAccountSwitchResult> CodexAccountSwitchHandler;

    internal ResetSpeedBoardForm EnsureResetSpeedBoardForm()
    {
        if (this.resetSpeedBoardForm == null || this.resetSpeedBoardForm.IsDisposed)
        {
            this.resetSpeedBoardForm = new ResetSpeedBoardForm(
                this,
                this.CurrentSettings,
                delegate { return ResolveResetSpeedSnapshot(); },
                delegate(string accountKey) { return RequestCodexAccountSwitch(accountKey); });
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

    internal CodexAccountSwitchResult RequestCodexAccountSwitch(string accountKey)
    {
        Func<string, CodexAccountSwitchResult> handler = this.CodexAccountSwitchHandler;
        if (handler == null)
        {
            return CodexAccountSwitchResult.CreateFailure("账户切换不可用。");
        }

        try
        {
            return handler(accountKey) ?? CodexAccountSwitchResult.CreateFailure("账户切换没有返回结果。");
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return CodexAccountSwitchResult.CreateFailure("账户切换失败：" + ex.GetType().Name);
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
