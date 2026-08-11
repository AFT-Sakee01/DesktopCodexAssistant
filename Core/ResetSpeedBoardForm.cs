using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

// Sixth left-dock board. It follows the established dock lifecycle while presenting a cache-only
// seven-day quota/reset projection from CodexRadarForm.
internal sealed partial class ResetSpeedBoardForm : LayeredWidgetFormBase
{
    private const int MaintenanceIntervalMs = 500;
    private const int SnapshotRefreshIntervalMs = 5000;

    private readonly OperationForm owner;
    private readonly Func<ResetSpeedBoardSnapshot> snapshotProvider;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly System.Windows.Forms.Timer maintenanceTimer;
    private Func<Point> cursorPositionProvider;
    private ResetSpeedBoardSnapshot snapshot = ResetSpeedBoardSnapshot.CreateEmpty();
    private EdgeDockTabForm dockTab;
    private DateTime nextSnapshotRefreshUtc = DateTime.MinValue;
    private DateTime dockPointerLeftUtc = DateTime.MinValue;
    private DateTime lastInteractionUtc = DateTime.UtcNow;
    private DateTime outsideClickCollapseUtc = DateTime.MinValue;
    private long outsideClickSequence;
    private bool mouseWasInside;
    private bool displaySuspended;
    private bool hiddenForFullscreen;
    private bool restoreAfterFullscreen;
    private string visibleSignature = string.Empty;
    private Rectangle refreshHitBounds = Rectangle.Empty;
    private Rectangle closeHitBounds = Rectangle.Empty;

    internal Action CollapseOtherLeftDockOverlays;

    internal ResetSpeedBoardForm(
        OperationForm owner,
        WidgetSettings settings,
        Func<ResetSpeedBoardSnapshot> snapshotProvider)
    {
        this.owner = owner;
        this.snapshotProvider = snapshotProvider;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        this.SetStyle(ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "重置与速蹬";
        this.AccessibleName = "重置与速蹬看板";
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.Cursor = Cursors.Hand;
        this.Size = GetDesiredSize();
        this.maintenanceTimer = new System.Windows.Forms.Timer();
        this.maintenanceTimer.Interval = MaintenanceIntervalMs;
        this.maintenanceTimer.Tick += OnMaintenanceTick;
        RefreshSnapshot(true);
    }

    protected override string LayeredWindowLogName
    {
        get { return "ResetSpeedBoard"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.ResetSpeedBoardTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.ResetSpeedBoardScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    private bool IsLeftDocked
    {
        get { return this.owner != null; }
    }

    internal void PreparePresentationState(bool suspended, bool fullscreenHidden)
    {
        this.displaySuspended = suspended;
        this.hiddenForFullscreen = fullscreenHidden;
    }

    private Size GetDesiredSize()
    {
        return new Size(
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardWidth * this.LayerScale)),
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardHeight * this.LayerScale)));
    }

    internal void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        Size desired = GetDesiredSize();
        if (this.Size != desired) this.Size = desired;
        if (this.Visible)
        {
            PositionForDisplay();
            ResetAutoHideClock();
            RenderLayeredWindow();
        }
        else
        {
            InvalidateLayeredRenderBuffer();
        }
        SyncLeftDockTab();
    }

    internal void SyncLeftDockTab()
    {
        if (this.IsDisposed) return;
        if (!this.IsLeftDocked)
        {
            DisposeDockTab();
            return;
        }

        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.ResetSpeed);
        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                accent,
                BurnInProtection.ResetSpeedBoardDockTabSalt,
                "ResetSpeedBoardDockTab",
                EdgeDockTabRole.ResetSpeed);
            this.dockTab.HoverEntered += OnDockTabHoverEntered;
            this.dockTab.HoverExited += OnDockTabHoverExited;
            this.dockTab.PollTick += OnDockTabPollTick;
        }
        else
        {
            this.dockTab.ApplyRuntimeSettings(this.CurrentSettings, accent);
        }

        this.dockTab.SetDisplaySuspended(this.displaySuspended);
        this.dockTab.SetHiddenForFullscreen(this.hiddenForFullscreen);
        this.dockTab.ShowTab(ResolveDockTabCenterY());
    }

    private int ResolveDockTabCenterY()
    {
        return LeftDockLayout.ResolveTabCenterY(this.CurrentSettings, EdgeDockTabRole.ResetSpeed, this.LayerScale);
    }

    private void OnDockTabHoverEntered(object sender, EventArgs e)
    {
        if (this.IsDisposed || !this.IsLeftDocked || this.Visible ||
            LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen) ||
            OutsideClickDismissalMonitor.ShouldSuppressTabReopen(this.outsideClickCollapseUtc, DateTime.UtcNow))
        {
            return;
        }
        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.dockPointerLeftUtc = DateTime.MinValue;
        ShowBoard();
    }

    private void OnDockTabHoverExited(object sender, EventArgs e)
    {
        this.outsideClickCollapseUtc = DateTime.MinValue;
    }

    private void OnDockTabPollTick(object sender, EventArgs e)
    {
        UpdateOutsideClickDismissal(DateTime.UtcNow);
    }

    private void DisposeDockTab()
    {
        if (this.dockTab == null) return;
        try
        {
            this.dockTab.HoverEntered -= OnDockTabHoverEntered;
            this.dockTab.HoverExited -= OnDockTabHoverExited;
            this.dockTab.PollTick -= OnDockTabPollTick;
            this.dockTab.Close();
            this.dockTab.Dispose();
        }
        catch (ObjectDisposedException) { }
        finally { this.dockTab = null; }
    }

    internal void ShowBoard()
    {
        if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen)) return;
        if (this.owner != null) this.owner.PrepareForResetSpeedBoardOverlayShow();
        Action collapse = this.CollapseOtherLeftDockOverlays;
        if (collapse != null) collapse();
        RefreshSnapshot(true);
        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.outsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
        PositionForDisplay();
        if (!this.Visible)
        {
            if (this.owner == null) Show();
            else Show(this.owner);
        }
        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(true, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        this.maintenanceTimer.Start();
        ResetAutoHideClock();
        RenderLayeredWindow();
        Program.LogInfo("Reset / Speed board expanded from left dock.");
    }

    internal void HideBoard()
    {
        this.maintenanceTimer.Stop();
        if (this.Visible) Hide();
    }

    internal void HideBoardIfVisible()
    {
        if (this.Visible) HideBoard();
    }

    internal void SetHiddenForFullscreen(bool hidden)
    {
        if (this.hiddenForFullscreen == hidden) return;
        this.hiddenForFullscreen = hidden;
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetHiddenForFullscreen(hidden);
            if (!hidden && !this.displaySuspended && this.IsLeftDocked) this.dockTab.ShowTab(ResolveDockTabCenterY());
        }
        if (hidden)
        {
            this.restoreAfterFullscreen = this.Visible;
            HideBoard();
        }
        else if (this.restoreAfterFullscreen && !this.displaySuspended)
        {
            this.restoreAfterFullscreen = false;
            ShowBoard();
        }
    }

    internal void PrepareForDisplaySuspend()
    {
        this.displaySuspended = true;
        this.maintenanceTimer.Stop();
        if (this.dockTab != null && !this.dockTab.IsDisposed) this.dockTab.SetDisplaySuspended(true);
        ResetDisplayRenderResources();
    }

    internal void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        ResetDisplayRenderResources();
        if (this.dockTab != null && !this.dockTab.IsDisposed) this.dockTab.SetDisplaySuspended(false);
        SyncLeftDockTab();
        if (this.Visible)
        {
            this.maintenanceTimer.Start();
            PositionForDisplay();
            RenderLayeredWindow();
        }
    }

    private void PositionForDisplay()
    {
        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
        Point baseLocation = LeftDockLayout.ResolveBoardBaseLocation(
            this.CurrentSettings,
            EdgeDockTabRole.ResetSpeed,
            this.LayerScale,
            this.Size);
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
            this.Size,
            workArea,
            BurnInProtection.ResetSpeedBoardSalt);
    }

    private bool RefreshSnapshot(bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now < this.nextSnapshotRefreshUtc) return false;
        this.nextSnapshotRefreshUtc = now.AddMilliseconds(SnapshotRefreshIntervalMs);
        ResetSpeedBoardSnapshot next = null;
        try
        {
            if (this.snapshotProvider != null)
            {
                long start = TimingStats.StartTimestamp();
                try { next = this.snapshotProvider(); }
                finally { TimingStats.RecordElapsed("codex.reset_speed_snapshot_projection", start); }
            }
        }
        catch (Exception ex) { Program.LogException(ex); }
        if (next == null) next = ResetSpeedBoardSnapshot.CreateEmpty();
        string signature = BuildSnapshotSignature(next);
        bool changed = !string.Equals(signature, this.visibleSignature, StringComparison.Ordinal);
        this.snapshot = next.Clone();
        this.visibleSignature = signature;
        return changed;
    }

    private static string BuildSnapshotSignature(ResetSpeedBoardSnapshot value)
    {
        if (value == null) return string.Empty;
        StringBuilder key = new StringBuilder(512);
        key.Append(value.QuotaKnown ? '1' : '0').Append(':')
            .Append(value.FiveHourRemainingPercent).Append(':')
            .Append(value.WeeklyRemainingPercent).Append(':')
            .Append(value.WeeklyResetKnown ? value.WeeklyResetLocal.Ticks : 0L).Append('|')
            .Append(value.SpeedWindowKnown ? '1' : '0').Append(value.SpeedWindowOpen ? '1' : '0').Append(':')
            .Append(value.SpeedWindowRemainingMinutes).Append('|')
            .Append(value.ResetCreditsKnown ? '1' : '0').Append(':')
            .Append(value.ResetCreditCount).Append(':')
            .Append(value.ResetCreditExpirationKnown ? value.ResetCreditExpirationLocal.Ticks : 0L).Append('|')
            .Append(value.ResetRadarKnown ? '1' : '0').Append(':')
            .Append(value.ResetRadarUpdatedAtKnown ? value.ResetRadarUpdatedAtLocal.Ticks : 0L).Append(':')
            .Append(value.ResetCardStatus).Append('\u001f')
            .Append(value.ResetCardDescription).Append('\u001f')
            .Append(value.HardResetStatus).Append('\u001f')
            .Append(value.HardResetDescription).Append('|');
        for (int i = 0; i < value.QuotaHistory.Count; i++)
        {
            ResetSpeedQuotaPoint point = value.QuotaHistory[i];
            if (point != null) key.Append(point.DateLocal.Ticks).Append(':').Append(point.Known ? point.WeeklyRemainingPercent : -1).Append(';');
        }
        for (int i = 0; i < value.ResetEvents.Count; i++)
        {
            ResetSpeedResetEvent resetEvent = value.ResetEvents[i];
            if (resetEvent != null) key.Append(resetEvent.TimestampLocal.Ticks).Append(':').Append((int)resetEvent.Kind).Append(';');
        }
        return key.ToString();
    }

    private bool UpdateOutsideClickDismissal(DateTime nowUtc)
    {
        if (!this.Visible || !this.IsLeftDocked || this.CurrentSettings == null ||
            !this.CurrentSettings.LeftDockOutsideClickCollapseEnabled) return false;
        Point clickPosition;
        DateTime clickUtc;
        if (!OutsideClickDismissalMonitor.TryGetClickAfter(ref this.outsideClickSequence, out clickPosition, out clickUtc)) return false;
        Rectangle tabBounds = this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible
            ? this.dockTab.Bounds : Rectangle.Empty;
        if (!OutsideClickDismissalMonitor.ShouldDismissOutsideClick(true, clickPosition, this.Bounds, tabBounds, Rectangle.Empty)) return false;
        this.outsideClickCollapseUtc = clickUtc == DateTime.MinValue ? nowUtc : clickUtc;
        HideBoard();
        return true;
    }

    private bool UpdateDockCollapse(DateTime nowUtc)
    {
        if (!this.IsLeftDocked || !this.Visible)
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            return false;
        }
        Point cursor = this.cursorPositionProvider();
        bool overBoard = this.Bounds.Contains(cursor);
        bool overTab = this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible && this.dockTab.Bounds.Contains(cursor);
        if (overBoard || overTab)
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            return false;
        }
        if (this.dockPointerLeftUtc == DateTime.MinValue)
        {
            this.dockPointerLeftUtc = nowUtc;
            return false;
        }
        if (nowUtc < this.dockPointerLeftUtc.AddSeconds(this.CurrentSettings.LeftDockCollapseSeconds)) return false;
        this.dockPointerLeftUtc = DateTime.MinValue;
        HideBoard();
        return true;
    }

    private void OnMaintenanceTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        DateTime now = DateTime.UtcNow;
        if (this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible) this.dockTab.RefreshBurnInPosition();
        if (UpdateOutsideClickDismissal(now) || UpdateDockCollapse(now) || !this.Visible) return;
        if (RefreshSnapshot(false)) RenderLayeredWindow();
        bool inside = this.Bounds.Contains(this.cursorPositionProvider());
        if (inside) this.mouseWasInside = true;
        else if (this.mouseWasInside)
        {
            this.mouseWasInside = false;
            ResetAutoHideClock();
        }
        int autoHideSeconds = this.CurrentSettings.ResetSpeedBoardAutoHideSeconds;
        if (autoHideSeconds > 0 && !inside && now >= this.lastInteractionUtc.AddSeconds(autoHideSeconds))
        {
            HideBoard();
            return;
        }
        if (ShouldRefreshBurnInPosition()) PositionForDisplay();
    }

    private void ResetAutoHideClock()
    {
        this.lastInteractionUtc = DateTime.UtcNow;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        this.fontCache.Dispose();
        ResetDisplayRenderResources();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), Math.Max(3, S(10))))
        {
            Region old = this.Region;
            this.Region = new Region(path);
            if (old != null) old.Dispose();
        }
        RenderLayeredWindow();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        this.mouseWasInside = true;
        ResetAutoHideClock();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.mouseWasInside = false;
        ResetAutoHideClock();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ResetAutoHideClock();
        if (e.Button != MouseButtons.Left) return;
        if (!this.closeHitBounds.IsEmpty && this.closeHitBounds.Contains(e.Location))
        {
            HideBoard();
            return;
        }
        if (!this.refreshHitBounds.IsEmpty && this.refreshHitBounds.Contains(e.Location))
        {
            // Reset/Speed consumes a cache-only Radar projection. This button clones the current
            // published state and intentionally does not bypass the owner's network schedule.
            RefreshSnapshot(true);
            RenderLayeredWindow();
            return;
        }
        HideBoard();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeDockTab();
            this.maintenanceTimer.Stop();
            this.maintenanceTimer.Tick -= OnMaintenanceTick;
            this.maintenanceTimer.Dispose();
            this.fontCache.Dispose();
        }
        base.Dispose(disposing);
    }
}
