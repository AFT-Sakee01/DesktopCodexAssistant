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
        ExecuteSpecBoardEntrySingleClick(target);
    }

    private void HandleSpecBoardEntryDoubleClick(SpecBoardEntryClickTarget target)
    {
        this.suppressedSpecBoardEntryMouseUp = target;
        if (target == SpecBoardEntryClickTarget.RadialCore && this.radialMenuOpen)
        {
            // The first click is intentionally immediate. When a second click resolves the gesture
            // as a double-click, retract that first-click menu before running the double action.
            CloseRadialMenu();
        }

        if (ShouldOpenOperationDoubleClickSpecialMenu(this.CurrentSettings))
        {
            ToggleLauncherTrioWindow();
            return;
        }

        if (this.launcherTrioForm != null && !this.launcherTrioForm.IsDisposed && this.launcherTrioForm.Visible)
        {
            this.launcherTrioForm.HideTrio();
        }

        ToggleHiddenModeFromOperationDoubleClick();
    }

    private static bool ShouldOpenOperationDoubleClickSpecialMenu(WidgetSettings settings)
    {
        return settings != null && settings.OperationDoubleClickSpecialMenuEnabled;
    }

    private void ToggleHiddenModeFromOperationDoubleClick()
    {
        if (this.toggleHoverOpacityAction == null)
        {
            return;
        }

        try
        {
            bool active = this.toggleHoverOpacityAction();
            Program.LogInfo("Operation core double-click toggled hidden mode. Active=" + active.ToString());
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowOperationNotification("隐藏模式", "切换隐藏模式失败。", ToolTipIcon.Warning);
        }
    }

    private static void RunOperationDoubleClickRoutingSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        AssertSelfTest(
            !ShouldOpenOperationDoubleClickSpecialMenu(settings),
            "operation core double-click defaults to hidden-mode toggle");
        settings.OperationDoubleClickSpecialMenuEnabled = true;
        AssertSelfTest(
            ShouldOpenOperationDoubleClickSpecialMenu(settings),
            "operation core double-click special menu opt-in");

        using (OperationForm form = CreateRadialDialSelfTestForm())
        {
            AssertSelfTest(!form.radialMenuOpen, "radial menu starts closed for immediate-click test");
            form.HandleSpecBoardEntryMouseUp(SpecBoardEntryClickTarget.RadialCore);
            AssertSelfTest(form.radialMenuOpen, "radial core single click opens immediately");
            form.HandleSpecBoardEntryDoubleClick(SpecBoardEntryClickTarget.RadialCore);
            AssertSelfTest(!form.radialMenuOpen, "radial core double click retracts immediate single-click menu");
        }
    }

    private void ExecuteSpecBoardEntrySingleClick(SpecBoardEntryClickTarget target)
    {
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

        HideCodexTaskBoardIfVisible();
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

        HideCodexTaskBoardIfVisible();
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

        HideCodexTaskBoardIfVisible();
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
