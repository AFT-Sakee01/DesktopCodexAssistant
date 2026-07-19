using System;
using System.Drawing;
using System.Windows.Forms;

// Left-edge dock behaviour for the network window: the blue top member of the four-tab queue,
// alongside Spec, Codex task and GUARD. Docked, the window is hidden until its tab is
// hovered and it renders the wide DrawContentDocked layout instead of the classic strip; undocked,
// every code path here is inert and the window behaves exactly as before.
internal sealed partial class NetworkMonitorForm
{
    private EdgeDockTabForm dockTab;
    private DateTime dockPointerLeftUtc = DateTime.MinValue;
    private long dockOutsideClickSequence;
    private DateTime dockOutsideClickCollapseUtc = DateTime.MinValue;
    private CleanIpConnectionSnapshot cleanIpSnapshot = new CleanIpConnectionSnapshot();

    // Mutual exclusion with the other three left-dock boards. WidgetForm wires this to
    // OperationForm.HideLeftDockBoardsForPeerOverlay after both forms exist; expanding this panel
    // collapses its peers, and their PrepareFor*OverlayShow calls collapse this panel in return.
    internal Action CollapseOtherLeftDockOverlays;

    private bool IsLeftDocked
    {
        get { return this.CurrentSettings != null && this.CurrentSettings.NetworkMonitorLeftDockEnabled; }
    }

    // Auto sentinel puts this tab at the top of the queue: Spec sits one slot below the middle
    // marker and Codex one slot above it, so -3 offsets clear both without moving them.
    private int ResolveDockTabCenterY()
    {
        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleNetworkMonitor);
        if (this.CurrentSettings.NetworkMonitorLeftDockTabCenterY != WidgetSettings.AutoLeftDockTabCenterY)
        {
            return this.CurrentSettings.NetworkMonitorLeftDockTabCenterY;
        }

        return workArea.Top + workArea.Height / 2 - S(WidgetSettings.LeftDockTabAutoOffsetY * 3);
    }

    // Docked size follows the Spec board so the four boards in the queue expand to the same
    // footprint; the classic min/max clamp is far too short for the docked layout and has to be
    // relaxed here or Size assignment would silently truncate the panel.
    private Size GetDockedSize()
    {
        return new Size(
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardWidth * this.LayerScale)),
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardHeight * this.LayerScale)));
    }

    private void ApplyDockedSizeBounds()
    {
        // Docked: no bounds at all, exactly like SpecBoardForm. Size is computed as
        // SpecBoardWidth/Height * LayerScale, and any Min/MaximumSize derived from
        // ScaleWindowSize would fight that formula: ScaleWindowSize folds in the global window
        // scale factor but not the DPI component of LayerScale, so on a scaled desktop the clamp
        // lands below the intended size and silently shrinks the panel (observed as a 648x400
        // request clamped to 350x400 at 200% DPI with 50% window scale).
        if (this.IsLeftDocked)
        {
            this.MinimumSize = Size.Empty;
            this.MaximumSize = Size.Empty;
            return;
        }

        this.MinimumSize = ScaleWindowSize(new Size(WidgetSettings.MinNetworkMonitorWidth, WidgetSettings.MinNetworkMonitorHeight));
        this.MaximumSize = ScaleWindowSize(new Size(WidgetSettings.MaxNetworkMonitorWidth, WidgetSettings.MaxNetworkMonitorHeight));
    }

    internal void SyncLeftDockTab()
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (!this.IsLeftDocked)
        {
            DisposeDockTab();
            return;
        }

        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                GetDockTabAccent(),
                BurnInProtection.NetworkMonitorDockTabSalt,
                "NetworkMonitorDockTab",
                false);
            this.dockTab.HoverEntered += OnDockTabHoverEntered;
            this.dockTab.HoverExited += OnDockTabHoverExited;
            this.dockTab.PollTick += OnDockTabPollTick;
        }
        else
        {
            this.dockTab.ApplyRuntimeSettings(this.CurrentSettings, GetDockTabAccent());
        }

        this.dockTab.SetDisplaySuspended(this.hiddenForFullscreen);
        this.dockTab.SetBoardExpanded(this.Visible);
        this.dockTab.ShowTab(ResolveDockTabCenterY());
    }

    // Queue identity is positional and fixed: Network is always the blue first tab. Network health
    // remains on the expanded board; changing this colour would break the requested blue/orange/
    // green/purple scan order and the protected gray-rest state.
    private Color GetDockTabAccent()
    {
        return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Network);
    }

    private void OnDockTabHoverEntered(object sender, EventArgs e)
    {
        if (this.IsDisposed || !this.IsLeftDocked || this.Visible || this.hiddenForFullscreen)
        {
            return;
        }

        if (OutsideClickDismissalMonitor.ShouldSuppressTabReopen(this.dockOutsideClickCollapseUtc, DateTime.UtcNow))
        {
            return;
        }

        this.dockOutsideClickCollapseUtc = DateTime.MinValue;
        this.dockPointerLeftUtc = DateTime.MinValue;
        ShowDockedPanel();
    }

    private void OnDockTabHoverExited(object sender, EventArgs e)
    {
        this.dockOutsideClickCollapseUtc = DateTime.MinValue;
    }

    private void OnDockTabPollTick(object sender, EventArgs e)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (!UpdateDockOutsideClickDismissal(nowUtc))
        {
            UpdateDockCollapse(nowUtc);
        }
    }

    private bool UpdateDockOutsideClickDismissal(DateTime nowUtc)
    {
        bool enabled = this.Visible &&
            this.IsLeftDocked &&
            this.CurrentSettings != null &&
            this.CurrentSettings.LeftDockOutsideClickCollapseEnabled;
        if (!enabled)
        {
            return false;
        }

        Point clickPosition;
        DateTime clickUtc;
        if (!OutsideClickDismissalMonitor.TryGetClickAfter(ref this.dockOutsideClickSequence, out clickPosition, out clickUtc))
        {
            return false;
        }

        Rectangle tabBounds = this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible
            ? this.dockTab.Bounds
            : Rectangle.Empty;
        if (!OutsideClickDismissalMonitor.ShouldDismissOutsideClick(
            true,
            clickPosition,
            this.Bounds,
            tabBounds,
            Rectangle.Empty))
        {
            return false;
        }

        this.dockOutsideClickCollapseUtc = clickUtc == DateTime.MinValue ? nowUtc : clickUtc;
        Program.LogInfo("NetworkMonitor outside click collapsed docked panel.");
        HideDockedPanel();
        return true;
    }

    private bool UpdateDockCollapse(DateTime nowUtc)
    {
        if (!this.IsLeftDocked || !this.Visible)
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            return false;
        }

        Point cursor = Cursor.Position;
        bool overPanel = this.Bounds.Contains(cursor);
        bool overTab = this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible &&
            this.dockTab.Bounds.Contains(cursor);
        if (overPanel || overTab)
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            return false;
        }

        if (this.dockPointerLeftUtc == DateTime.MinValue)
        {
            this.dockPointerLeftUtc = nowUtc;
            return false;
        }

        if (nowUtc < this.dockPointerLeftUtc.AddSeconds(this.CurrentSettings.LeftDockCollapseSeconds))
        {
            return false;
        }

        this.dockPointerLeftUtc = DateTime.MinValue;
        HideDockedPanel();
        return true;
    }

    private void ShowDockedPanel()
    {
        Action collapseOthers = this.CollapseOtherLeftDockOverlays;
        if (collapseOthers != null)
        {
            collapseOthers();
        }

        this.dockOutsideClickCollapseUtc = DateTime.MinValue;
        this.dockOutsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
        ApplyDockedSizeBounds();
        this.Size = GetDockedSize();
        PositionAtLeftDock();
        // Per-hop probing is suspended while collapsed; expanding is what turns it back on.
        this.reader.SetPathPingSamplingActive(true);
        if (!this.Visible)
        {
            Show();
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(
                ShouldUseTopMostPlacement(),
                this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetBoardExpanded(true);
        }

        // Show() can recreate the native handle after a mode/display transition. Reassert the
        // board's input policy on that final handle before the first visible render.
        ApplyClickThroughStyle();
        RenderLayeredWindow();
    }

    private void HideDockedPanel()
    {
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetBoardExpanded(false);
        }

        this.dockPointerLeftUtc = DateTime.MinValue;
        this.dockedRefreshButtonBounds = Rectangle.Empty;
        this.dockedCloseButtonBounds = Rectangle.Empty;
        this.reader.SetPathPingSamplingActive(false);
        if (this.Visible)
        {
            Hide();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ShouldHandleDockedFooterActionClick(
            this.IsLeftDocked,
            this.Visible,
            e.Button,
            e.Location,
            this.dockedCloseButtonBounds))
        {
            Program.LogInfo("NetworkMonitor dock footer close requested.");
            HideDockedPanel();
            return;
        }

        if (!ShouldHandleDockedFooterActionClick(
            this.IsLeftDocked,
            this.Visible,
            e.Button,
            e.Location,
            this.dockedRefreshButtonBounds))
        {
            return;
        }

        Program.LogInfo("NetworkMonitor dock footer refresh requested.");
        ForceRefresh();
    }

    private static bool ShouldHandleDockedFooterActionClick(
        bool isLeftDocked,
        bool visible,
        MouseButtons button,
        Point location,
        Rectangle actionBounds)
    {
        return isLeftDocked &&
            visible &&
            button == MouseButtons.Left &&
            !actionBounds.IsEmpty &&
            actionBounds.Contains(location);
    }

    // Called by OperationForm's PrepareFor*OverlayShow chain when a sibling overlay expands.
    internal void HideDockedPanelIfVisible()
    {
        if (!this.IsDisposed && this.IsLeftDocked && this.Visible)
        {
            HideDockedPanel();
        }
    }

    // Docked: flush against the left edge but offset by the tab width so the tab stays visible and
    // the pointer can travel tab -> panel without crossing a gap that starts the collapse clock.
    private void PositionAtLeftDock()
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleNetworkMonitor);
        int left = workArea.Left + S(EdgeDockTabForm.LogicalWidth);
        int top = ResolveDockTabCenterY() - this.Height / 2;
        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - this.Width)));
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Height)));
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.NetworkMonitorSalt);
    }

    private void SetDockTabHiddenForFullscreen(bool hidden)
    {
        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            return;
        }

        if (hidden)
        {
            this.dockTab.HideTab();
            HideDockedPanel();
            return;
        }

        if (this.IsLeftDocked)
        {
            this.dockTab.ShowTab(ResolveDockTabCenterY());
        }
    }

    private void DisposeDockTab()
    {
        if (this.dockTab == null)
        {
            return;
        }

        try
        {
            this.dockTab.HoverEntered -= OnDockTabHoverEntered;
            this.dockTab.HoverExited -= OnDockTabHoverExited;
            this.dockTab.PollTick -= OnDockTabPollTick;
            this.dockTab.Close();
            this.dockTab.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.dockTab = null;
        }
    }

    // The docked network board is the runtime presentation owner for the outbound-IP profile.
    // It consumes the process-wide reader so hiding the retired standalone window cannot create
    // another request stream or change the cleanip.io quota boundary.
    private void RefreshCleanIpSnapshot()
    {
        if (!this.IsLeftDocked)
        {
            return;
        }

        this.cleanIpSnapshot = CleanIpConnectionReader.Shared.GetSnapshot(this.CurrentSettings);
    }
}
