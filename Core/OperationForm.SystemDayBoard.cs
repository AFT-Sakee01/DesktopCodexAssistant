using System;

// Ownership and routing for the seventh left-dock member. WidgetForm owns sampling/history; this
// partial exposes only a cache-only range provider and the standard board lifecycle.
internal sealed partial class OperationForm
{
    private SystemDayBoardForm systemDayBoardForm;

    internal Func<SystemDayRange, SystemDayBoardSnapshot> SystemDaySnapshotProvider;

    internal SystemDayBoardForm EnsureSystemDayBoardForm()
    {
        if (this.systemDayBoardForm == null || this.systemDayBoardForm.IsDisposed)
        {
            this.systemDayBoardForm = new SystemDayBoardForm(
                this,
                this.CurrentSettings,
                delegate(SystemDayRange range) { return ResolveSystemDaySnapshot(range); });
            this.systemDayBoardForm.CollapseOtherLeftDockOverlays = delegate
            {
                HideNetworkDockedPanelIfVisible();
            };
        }
        this.systemDayBoardForm.PreparePresentationState(this.displaySuspended, this.hiddenForFullscreen);
        this.systemDayBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.systemDayBoardForm;
    }

    private SystemDayBoardSnapshot ResolveSystemDaySnapshot(SystemDayRange range)
    {
        Func<SystemDayRange, SystemDayBoardSnapshot> provider = this.SystemDaySnapshotProvider;
        if (provider == null) return SystemDayBoardSnapshot.CreateEmpty(range, DateTime.Now);
        try { return provider(range) ?? SystemDayBoardSnapshot.CreateEmpty(range, DateTime.Now); }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return SystemDayBoardSnapshot.CreateEmpty(range, DateTime.Now);
        }
    }

    internal void HideSystemDayBoardIfVisible()
    {
        if (this.systemDayBoardForm != null && !this.systemDayBoardForm.IsDisposed)
            this.systemDayBoardForm.HideBoardIfVisible();
    }

    internal void SetSystemDayBoardHiddenForFullscreen(bool hidden)
    {
        if (this.systemDayBoardForm != null && !this.systemDayBoardForm.IsDisposed)
            this.systemDayBoardForm.SetHiddenForFullscreen(hidden);
    }

    internal void PrepareSystemDayBoardForDisplaySuspend()
    {
        if (this.systemDayBoardForm != null && !this.systemDayBoardForm.IsDisposed)
            this.systemDayBoardForm.PrepareForDisplaySuspend();
    }

    internal void RecoverSystemDayBoardAfterDisplayResume()
    {
        if (this.systemDayBoardForm != null && !this.systemDayBoardForm.IsDisposed)
            this.systemDayBoardForm.RecoverAfterDisplayResume();
    }

    internal void PrepareForSystemDayBoardOverlayShow()
    {
        if (this.radialMenuOpen) CloseRadialMenu();
        HideLauncherTrioIfVisible();
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.SystemDay);
    }

    private void DisposeSystemDayBoardForm()
    {
        if (this.systemDayBoardForm == null) return;
        try
        {
            this.systemDayBoardForm.Close();
            this.systemDayBoardForm.Dispose();
        }
        catch (ObjectDisposedException) { }
        finally { this.systemDayBoardForm = null; }
    }
}
