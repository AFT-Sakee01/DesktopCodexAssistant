using System;
using System.Windows.Forms;

internal sealed partial class OperationForm
{
    private enum OperationEntryClickTarget
    {
        None,
        StartButton,
        RadialCore
    }

    private SpecBoardForm specBoardForm;
    private OperationEntryClickTarget suppressedOperationEntryMouseUp;

    private void HandleOperationEntryMouseUp(OperationEntryClickTarget target)
    {
        if (this.suppressedOperationEntryMouseUp == target)
        {
            // WinForms reports the second MouseUp after MouseDoubleClick. Consume that release so
            // the resolved double-click cannot enqueue a new single-click action.
            this.suppressedOperationEntryMouseUp = OperationEntryClickTarget.None;
            return;
        }

        this.suppressedOperationEntryMouseUp = OperationEntryClickTarget.None;
        ExecuteOperationEntrySingleClick(target);
    }

    private void HandleOperationEntryDoubleClick(OperationEntryClickTarget target)
    {
        this.suppressedOperationEntryMouseUp = target;
        if (target == OperationEntryClickTarget.RadialCore && this.radialMenuOpen)
        {
            // The first click is intentionally immediate. When a second click resolves the gesture
            // as a double-click, retract that first-click menu before running the double action.
            CloseRadialMenu();
        }

        if (this.toggleSideSurfacesAction == null)
        {
            return;
        }

        try
        {
            bool hidden = this.toggleSideSurfacesAction();
            Program.LogInfo("Operation core double-click toggled physical side-surface visibility. Hidden=" + hidden.ToString());
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowOperationNotification("两侧窗格", "切换两侧窗格显示状态失败。", ToolTipIcon.Warning);
        }
    }

    private static void RunOperationDoubleClickRoutingSelfTest()
    {
        bool sideSurfacesHidden = false;
        using (OperationForm form = CreateRadialDialSelfTestForm(delegate
        {
            sideSurfacesHidden = !sideSurfacesHidden;
            return sideSurfacesHidden;
        }))
        {
            AssertSelfTest(!form.radialMenuOpen, "radial menu starts closed for immediate-click test");
            form.HandleOperationEntryMouseUp(OperationEntryClickTarget.RadialCore);
            AssertSelfTest(form.radialMenuOpen, "radial core single click opens immediately");
            form.HandleOperationEntryDoubleClick(OperationEntryClickTarget.RadialCore);
            AssertSelfTest(!form.radialMenuOpen, "radial core double click retracts immediate single-click menu");
            AssertSelfTest(sideSurfacesHidden, "radial core double click physically hides side surfaces through the host");
            form.HandleOperationEntryDoubleClick(OperationEntryClickTarget.RadialCore);
            AssertSelfTest(!sideSurfacesHidden, "a second radial core double click restores side surfaces through the host");
        }
    }

    private void ExecuteOperationEntrySingleClick(OperationEntryClickTarget target)
    {
        if (target == OperationEntryClickTarget.StartButton && !IsRadialDialActive())
        {
            ExecuteButton(StartButtonIndex, MouseButtons.Left);
        }
        else if (target == OperationEntryClickTarget.RadialCore && IsRadialDialActive())
        {
            ExecuteRadialCoreSingleClick();
        }
    }

    private void CancelOperationEntryClick()
    {
        this.suppressedOperationEntryMouseUp = OperationEntryClickTarget.None;
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

    // Set by WidgetForm: collapses the network window's docked panel, the third member of the
    // left-dock queue, which lives outside this form's ownership.
    internal Action HideNetworkDockedPanelForOverlay;

    private void HideNetworkDockedPanelIfVisible()
    {
        Action handler = this.HideNetworkDockedPanelForOverlay;
        if (handler != null)
        {
            handler();
        }
    }

    // The seven left-dock boards. Expanded, they overlap heavily — their tabs are close together
    // pixels apart while the boards themselves are 400 tall — so two of them visible at once is not
    // a cosmetic glitch: the top one covers the other, and the covered board's own collapse timer
    // then reads the cursor as still inside its bounds and never fires. Mutual exclusion at show
    // time is the only thing keeping that from happening.
    internal enum LeftDockBoardKind
    {
        None,
        Spec,
        CodexTask,
        Network,
        Guard,
        CodexIq,
        ResetSpeed,
        SystemDay
    }

    // Single place that knows the full membership of the queue. Every expand path routes through
    // here instead of listing its peers by hand: the guard board once stayed open underneath the
    // Codex task board for exactly one reason — PrepareForCodexTaskOverlayShow was the one call
    // site of four that had not been updated when the fourth board was added.
    private static LeftDockBoardKind[] GetLeftDockBoardMembership()
    {
        return new LeftDockBoardKind[]
        {
            LeftDockBoardKind.Spec,
            LeftDockBoardKind.CodexTask,
            LeftDockBoardKind.Network,
            LeftDockBoardKind.Guard,
            LeftDockBoardKind.CodexIq,
            LeftDockBoardKind.ResetSpeed,
            LeftDockBoardKind.SystemDay
        };
    }

    private void CollapseLeftDockBoardsExcept(LeftDockBoardKind keep)
    {
        LeftDockBoardKind[] members = GetLeftDockBoardMembership();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i] != keep)
            {
                HideLeftDockBoard(members[i]);
            }
        }
    }

    private void HideLeftDockBoard(LeftDockBoardKind kind)
    {
        switch (kind)
        {
            case LeftDockBoardKind.Spec:
                if (this.specBoardForm != null && !this.specBoardForm.IsDisposed && this.specBoardForm.Visible)
                {
                    this.specBoardForm.HideBoard();
                }

                break;

            case LeftDockBoardKind.CodexTask:
                HideCodexTaskBoardIfVisible();
                break;

            case LeftDockBoardKind.Network:
                HideNetworkDockedPanelIfVisible();
                break;

            case LeftDockBoardKind.Guard:
                HideGuardBoardIfVisible();
                break;

            case LeftDockBoardKind.CodexIq:
                HideCodexIqBoardIfVisible();
                break;

            case LeftDockBoardKind.ResetSpeed:
                HideResetSpeedBoardIfVisible();
                break;

            case LeftDockBoardKind.SystemDay:
                HideSystemDayBoardIfVisible();
                break;
        }
    }

    // Proves the collapse membership still covers every declared board. A new board added to the
    // enum without being added to the membership list fails here instead of silently sitting open
    // underneath whichever peer the user expands next.
    internal static void RunLeftDockMutualExclusionSelfTest()
    {
        LeftDockBoardKind[] members = GetLeftDockBoardMembership();
        Array declared = Enum.GetValues(typeof(LeftDockBoardKind));
        int expected = 0;
        foreach (LeftDockBoardKind kind in declared)
        {
            if (kind == LeftDockBoardKind.None)
            {
                continue;
            }

            expected++;
            if (Array.IndexOf(members, kind) < 0)
            {
                throw new InvalidOperationException(
                    "Left-dock collapse membership is missing " + kind.ToString() +
                    "; expanding a peer would leave it open underneath.");
            }
        }

        if (members.Length != expected)
        {
            throw new InvalidOperationException("Left-dock collapse membership must list each board exactly once.");
        }

        Console.WriteLine(
            "Left-dock mutual exclusion: PASS " +
            expected.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " boards share one collapse membership");
    }

    // Expanding the network docked panel collapses the other five boards; the panel's own
    // collapse is handled by the HideNetworkDockedPanelForOverlay callback in the inverse direction.
    internal void HideLeftDockBoardsForPeerOverlay()
    {
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.Network);
    }

    internal void PrepareForSpecBoardOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        CollapseLeftDockBoardsExcept(LeftDockBoardKind.Spec);
    }

    private void PrepareForRadialOverlayShow()
    {
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.None);
    }

    private SpecBoardForm EnsureSpecBoardForm()
    {
        if (this.specBoardForm == null || this.specBoardForm.IsDisposed)
        {
            this.specBoardForm = new SpecBoardForm(this, this.CurrentSettings);
        }

        this.specBoardForm.PreparePresentationState(this.displaySuspended, AreLeftDockSurfacesHidden());
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
