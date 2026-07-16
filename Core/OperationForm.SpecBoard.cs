using System;
using System.Windows.Forms;

internal sealed partial class OperationForm
{
    private enum SpecBoardEntryClickTarget
    {
        None,
        StartButton,
        RadialCore
    }

    private SpecBoardForm specBoardForm;
    private SpecBoardEntryClickTarget pendingSpecBoardEntryClick;
    private SpecBoardEntryClickTarget suppressedSpecBoardEntryMouseUp;

    private void HandleSpecBoardEntryMouseUp(SpecBoardEntryClickTarget target)
    {
        if (this.suppressedSpecBoardEntryMouseUp == target)
        {
            // WinForms reports the second MouseUp after MouseDoubleClick. Consume that release so
            // the resolved double-click cannot enqueue a new single-click action.
            this.suppressedSpecBoardEntryMouseUp = SpecBoardEntryClickTarget.None;
            return;
        }

        this.suppressedSpecBoardEntryMouseUp = SpecBoardEntryClickTarget.None;

        this.pendingSpecBoardEntryClick = target;
        this.specBoardEntrySingleClickTimer.Stop();
        this.specBoardEntrySingleClickTimer.Interval = Math.Max(1, SystemInformation.DoubleClickTime);
        this.specBoardEntrySingleClickTimer.Start();
    }

    private void HandleSpecBoardEntryDoubleClick(SpecBoardEntryClickTarget target)
    {
        this.specBoardEntrySingleClickTimer.Stop();
        this.pendingSpecBoardEntryClick = SpecBoardEntryClickTarget.None;
        this.suppressedSpecBoardEntryMouseUp = target;
        // Double-click opens the compact launcher (Spec manager / sleep guard).
        ToggleLauncherTrioWindow();
    }

    private void OnSpecBoardEntrySingleClickTimerTick(object sender, EventArgs e)
    {
        this.specBoardEntrySingleClickTimer.Stop();
        SpecBoardEntryClickTarget target = this.pendingSpecBoardEntryClick;
        this.pendingSpecBoardEntryClick = SpecBoardEntryClickTarget.None;

        if (target == SpecBoardEntryClickTarget.StartButton && !IsRadialDialActive())
        {
            ExecuteButton(StartButtonIndex, MouseButtons.Left);
        }
        else if (target == SpecBoardEntryClickTarget.RadialCore && IsRadialDialActive())
        {
            ExecuteRadialCoreSingleClick();
        }
    }

    private void CancelSpecBoardEntryClick()
    {
        this.specBoardEntrySingleClickTimer.Stop();
        this.pendingSpecBoardEntryClick = SpecBoardEntryClickTarget.None;
        this.suppressedSpecBoardEntryMouseUp = SpecBoardEntryClickTarget.None;
    }

    private void ToggleSpecBoardWindow()
    {
        SpecBoardForm form = EnsureSpecBoardForm();
        if (form.Visible)
        {
            form.HideBoard();
            return;
        }

        form.ShowBoard();
    }

    private void OpenSpecBoardManagerWindow()
    {
        EnsureSpecBoardForm().ShowManagerWindow();
    }

    internal void PrepareForSpecBoardOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        if (this.launcherTrioForm != null && !this.launcherTrioForm.IsDisposed && this.launcherTrioForm.Visible)
        {
            this.launcherTrioForm.HideTrio();
        }
    }

    private void PrepareForRadialOverlayShow()
    {
        if (this.launcherTrioForm != null && !this.launcherTrioForm.IsDisposed && this.launcherTrioForm.Visible)
        {
            this.launcherTrioForm.HideTrio();
        }

        if (this.specBoardForm != null && !this.specBoardForm.IsDisposed && this.specBoardForm.Visible)
        {
            this.specBoardForm.HideBoard();
        }
    }

    private void PrepareForLauncherOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        if (this.specBoardForm != null && !this.specBoardForm.IsDisposed && this.specBoardForm.Visible)
        {
            this.specBoardForm.HideBoard();
        }
    }

    private SpecBoardForm EnsureSpecBoardForm()
    {
        if (this.specBoardForm == null || this.specBoardForm.IsDisposed)
        {
            this.specBoardForm = new SpecBoardForm(this, this.CurrentSettings);
        }

        this.specBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.specBoardForm;
    }

    private void DisposeSpecBoardForm()
    {
        if (this.specBoardForm == null)
        {
            return;
        }

        try
        {
            this.specBoardForm.Close();
            this.specBoardForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.specBoardForm = null;
        }
    }
}
