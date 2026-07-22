using System;
using System.Drawing;
using System.Windows.Forms;

// Left-edge dock behaviour for the network window: the blue member of the five-tab queue. The
// window body is hidden until its tab is hovered and always renders DrawContentDocked.
internal sealed partial class NetworkMonitorForm
{
    private EdgeDockTabForm dockTab;
    private DateTime dockPointerLeftUtc = DateTime.MinValue;
    private long dockOutsideClickSequence;
    private DateTime dockOutsideClickCollapseUtc = DateTime.MinValue;
    private CleanIpConnectionSnapshot cleanIpSnapshot = new CleanIpConnectionSnapshot();

    // Mutual exclusion with the other left-dock boards. WidgetForm wires this to
    // OperationForm.HideLeftDockBoardsForPeerOverlay after both forms exist; expanding this panel
    // collapses its peers, and their PrepareFor*OverlayShow calls collapse this panel in return.
    internal Action CollapseOtherLeftDockOverlays;

    private int ResolveDockTabCenterY()
    {
        return LeftDockLayout.ResolveTabCenterY(
            this.CurrentSettings,
            EdgeDockTabRole.Network,
            this.LayerScale);
    }

    // Docked size follows the Spec board so the boards in the queue expand to the same
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
        // No bounds, exactly like SpecBoardForm. Size is computed as
        // SpecBoardWidth/Height * LayerScale, and any Min/MaximumSize derived from
        // ScaleWindowSize would fight that formula: ScaleWindowSize folds in the global window
        // scale factor but not the DPI component of LayerScale, so on a scaled desktop the clamp
        // lands below the intended size and silently shrinks the panel (observed as a 648x400
        // request clamped to 350x400 at 200% DPI with 50% window scale).
        this.MinimumSize = Size.Empty;
        this.MaximumSize = Size.Empty;
    }

    internal void SyncLeftDockTab()
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                GetDockTabAccent(),
                BurnInProtection.NetworkMonitorDockTabSalt,
                "NetworkMonitorDockTab",
                EdgeDockTabRole.Network);
            this.dockTab.HoverEntered += OnDockTabHoverEntered;
            this.dockTab.HoverExited += OnDockTabHoverExited;
            this.dockTab.PollTick += OnDockTabPollTick;
        }
        else
        {
            this.dockTab.ApplyRuntimeSettings(this.CurrentSettings, GetDockTabAccent());
        }

        this.dockTab.SetDisplaySuspended(this.displaySuspended);
        this.dockTab.SetHiddenForFullscreen(this.hiddenForFullscreen);
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
        if (this.IsDisposed || this.Visible ||
            LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
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
        if (!this.Visible)
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
        if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            return;
        }

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
            true,
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
            true,
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
        if (!this.IsDisposed && this.Visible)
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

        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
        Point baseLocation = LeftDockLayout.ResolveBoardBaseLocation(
            this.CurrentSettings,
            EdgeDockTabRole.Network,
            this.LayerScale,
            this.Size);
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
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

        this.dockTab.SetHiddenForFullscreen(hidden);
        if (hidden)
        {
            HideDockedPanel();
        }
        else
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
    private bool RefreshCleanIpSnapshot()
    {
        CleanIpConnectionSnapshot next = CleanIpConnectionReader.Shared.GetSnapshot(this.CurrentSettings);
        bool changed = !HasSameCleanIpDisplayData(this.cleanIpSnapshot, next);
        this.cleanIpSnapshot = next;
        return changed;
    }

    private static bool HasSameCleanIpDisplayData(
        CleanIpConnectionSnapshot left,
        CleanIpConnectionSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        // CheckedAt is included because the dock visibly paints HH:mm. Internal trigger and
        // latency fields are excluded because they do not affect this board's pixels.
        return left.CheckedAtKnown == right.CheckedAtKnown &&
            (!left.CheckedAtKnown || left.CheckedAtLocal == right.CheckedAtLocal) &&
            left.Success == right.Success &&
            left.Running == right.Running &&
            left.ScoreKnown == right.ScoreKnown &&
            left.Score == right.Score &&
            string.Equals(left.Grade, right.Grade, StringComparison.Ordinal) &&
            string.Equals(left.NativeLabel, right.NativeLabel, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeLabel, right.IpTypeLabel, StringComparison.Ordinal) &&
            string.Equals(left.Ip, right.Ip, StringComparison.Ordinal) &&
            string.Equals(left.Location, right.Location, StringComparison.Ordinal) &&
            string.Equals(left.Asn, right.Asn, StringComparison.Ordinal) &&
            string.Equals(left.Organization, right.Organization, StringComparison.Ordinal) &&
            string.Equals(left.IpTypeReason, right.IpTypeReason, StringComparison.Ordinal) &&
            string.Equals(left.Error, right.Error, StringComparison.Ordinal);
    }
}
