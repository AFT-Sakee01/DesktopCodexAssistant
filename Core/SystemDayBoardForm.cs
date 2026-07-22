using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

// Seventh left-dock board. It projects the unified work/load/power/thermal history and never owns
// sampling or persistence; WidgetForm provides a cache-only range snapshot.
internal sealed partial class SystemDayBoardForm : LayeredWidgetFormBase
{
    private const int MaintenanceIntervalMs = 500;
    private const int SnapshotRefreshIntervalMs = 5000;
    private readonly OperationForm owner;
    private readonly Func<SystemDayRange, SystemDayBoardSnapshot> snapshotProvider;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly System.Windows.Forms.Timer maintenanceTimer;
    private Func<Point> cursorPositionProvider;
    private SystemDayBoardSnapshot snapshot;
    private SystemDayRange selectedRange = SystemDayRange.Today;
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

    internal Action CollapseOtherLeftDockOverlays;

    internal SystemDayBoardForm(
        OperationForm owner,
        WidgetSettings settings,
        Func<SystemDayRange, SystemDayBoardSnapshot> snapshotProvider)
    {
        this.owner = owner;
        this.snapshotProvider = snapshotProvider;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        this.snapshot = SystemDayBoardSnapshot.CreateEmpty(this.selectedRange, DateTime.Now);
        ApplicationIcon.ApplyTo(this);
        this.SetStyle(ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "系统日记";
        this.AccessibleName = "系统日记看板";
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
        get { return "SystemDayBoard"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.SystemDayBoardTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.SystemDayBoardScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    private bool IsLeftDocked
    {
        get { return this.owner != null; }
    }

    internal SystemDayRange SelectedRangeForSelfTest
    {
        get { return this.selectedRange; }
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
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay);
        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                accent,
                BurnInProtection.SystemDayBoardDockTabSalt,
                "SystemDayBoardDockTab",
                EdgeDockTabRole.SystemDay);
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
        return LeftDockLayout.ResolveTabCenterY(this.CurrentSettings, EdgeDockTabRole.SystemDay, this.LayerScale);
    }

    private void OnDockTabHoverEntered(object sender, EventArgs e)
    {
        if (this.IsDisposed || !this.IsLeftDocked || this.Visible ||
            LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen) ||
            OutsideClickDismissalMonitor.ShouldSuppressTabReopen(this.outsideClickCollapseUtc, DateTime.UtcNow)) return;
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
        if (this.owner != null) this.owner.PrepareForSystemDayBoardOverlayShow();
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
        Program.LogInfo("System Day board expanded from left dock. Range=" + this.selectedRange.ToString());
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
            EdgeDockTabRole.SystemDay,
            this.LayerScale,
            this.Size);
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
            this.Size,
            workArea,
            BurnInProtection.SystemDayBoardSalt);
    }

    private bool RefreshSnapshot(bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now < this.nextSnapshotRefreshUtc) return false;
        this.nextSnapshotRefreshUtc = now.AddMilliseconds(SnapshotRefreshIntervalMs);
        SystemDayBoardSnapshot next = null;
        try
        {
            if (this.snapshotProvider != null)
            {
                long start = TimingStats.StartTimestamp();
                try { next = this.snapshotProvider(this.selectedRange); }
                finally { TimingStats.RecordElapsed("system_day.board_snapshot_projection", start); }
            }
        }
        catch (Exception ex) { Program.LogException(ex); }
        if (next == null) next = SystemDayBoardSnapshot.CreateEmpty(this.selectedRange, DateTime.Now);
        string signature = BuildSnapshotSignature(next);
        bool changed = !string.Equals(signature, this.visibleSignature, StringComparison.Ordinal);
        this.snapshot = next.Clone();
        this.visibleSignature = signature;
        return changed;
    }

    private static string BuildSnapshotSignature(SystemDayBoardSnapshot value)
    {
        if (value == null) return string.Empty;
        StringBuilder key = new StringBuilder(256);
        key.Append((int)value.Range).Append(':')
            .Append(value.UpdatedLocal.Ticks).Append(':')
            .Append(value.RawSampleCount).Append(':')
            .Append(value.CurrentBatteryKnown ? value.CurrentBatteryPercent : -1).Append(':')
            .Append(Math.Round(value.CurrentWatts, 1)).Append(':')
            .Append(Math.Round(value.CurrentMaxCelsius, 1)).Append(':')
            .Append(value.Points.Count).Append(':')
            .Append(value.WorkSegments.Count);
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
        int autoHideSeconds = this.CurrentSettings.SystemDayBoardAutoHideSeconds;
        if (autoHideSeconds > 0 && !inside && now >= this.lastInteractionUtc.AddSeconds(autoHideSeconds))
        {
            HideBoard();
            return;
        }
        if (ShouldRefreshBurnInPosition()) PositionForDisplay();
    }

    private void SelectRange(SystemDayRange range)
    {
        if (this.selectedRange == range) return;
        this.selectedRange = range;
        this.nextSnapshotRefreshUtc = DateTime.MinValue;
        this.visibleSignature = string.Empty;
        RefreshSnapshot(true);
        ResetAutoHideClock();
        RenderLayeredWindow();
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
        if (GetCloseBounds().Contains(e.Location)) { HideBoard(); return; }
        if (GetRangeButtonBounds(SystemDayRange.Today).Contains(e.Location)) SelectRange(SystemDayRange.Today);
        else if (GetRangeButtonBounds(SystemDayRange.Last24Hours).Contains(e.Location)) SelectRange(SystemDayRange.Last24Hours);
        else if (GetRangeButtonBounds(SystemDayRange.LastWeek).Contains(e.Location)) SelectRange(SystemDayRange.LastWeek);
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
