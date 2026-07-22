using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

// Fifth left-dock board: cache-only Codex model intelligence comparison. It intentionally mirrors
// the established board lifecycle so outside-click dismissal, fullscreen suppression, no-activate
// z-order, tab reopening suppression and burn-in movement behave identically across the queue.
internal sealed partial class CodexIqBoardForm : LayeredWidgetFormBase
{
    private const int MaintenanceIntervalMs = 500;
    private const int SnapshotRefreshIntervalMs = 5000;

    private readonly OperationForm owner;
    private readonly Func<CodexIqBoardSnapshot> snapshotProvider;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly System.Windows.Forms.Timer maintenanceTimer;
    private Func<Point> cursorPositionProvider;
    private CodexIqBoardSnapshot snapshot = CodexIqBoardSnapshot.CreateEmpty();
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
    private Rectangle closeHitBounds = Rectangle.Empty;

    internal Action CollapseOtherLeftDockOverlays;

    internal CodexIqBoardForm(
        OperationForm owner,
        WidgetSettings settings,
        Func<CodexIqBoardSnapshot> snapshotProvider)
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
        this.Text = "Codex IQ Board";
        this.AccessibleName = "Codex IQ Board";
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
        get { return "CodexIqBoard"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.CodexIqBoardTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.CodexIqBoardScaleOverridePercent; }
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
        if (this.Size != desired)
        {
            this.Size = desired;
        }

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
        if (this.IsDisposed)
        {
            return;
        }

        if (!this.IsLeftDocked)
        {
            DisposeDockTab();
            return;
        }

        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexIq);
        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                accent,
                BurnInProtection.CodexIqBoardDockTabSalt,
                "CodexIqBoardDockTab",
                EdgeDockTabRole.CodexIq);
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
        return LeftDockLayout.ResolveTabCenterY(
            this.CurrentSettings,
            EdgeDockTabRole.CodexIq,
            this.LayerScale);
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

    internal void ShowBoard()
    {
        if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            return;
        }

        if (this.owner != null)
        {
            this.owner.PrepareForCodexIqBoardOverlayShow();
        }

        Action collapse = this.CollapseOtherLeftDockOverlays;
        if (collapse != null)
        {
            collapse();
        }

        RefreshSnapshot(true);
        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.outsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
        PositionForDisplay();
        if (!this.Visible)
        {
            if (this.owner == null)
            {
                Show();
            }
            else
            {
                Show(this.owner);
            }
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
        Program.LogInfo("Codex IQ board expanded from left dock.");
    }

    internal void HideBoard()
    {
        this.maintenanceTimer.Stop();
        if (this.Visible)
        {
            Hide();
        }
    }

    internal void HideBoardIfVisible()
    {
        if (this.Visible)
        {
            HideBoard();
        }
    }

    internal void SetHiddenForFullscreen(bool hidden)
    {
        if (this.hiddenForFullscreen == hidden)
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetHiddenForFullscreen(hidden);
            if (!hidden && !this.displaySuspended && this.IsLeftDocked)
            {
                this.dockTab.ShowTab(ResolveDockTabCenterY());
            }
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
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(true);
        }

        ResetDisplayRenderResources();
    }

    internal void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        ResetDisplayRenderResources();
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(false);
        }

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
            EdgeDockTabRole.CodexIq,
            this.LayerScale,
            this.Size);
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
            this.Size,
            workArea,
            BurnInProtection.CodexIqBoardSalt);
    }

    private bool RefreshSnapshot(bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now < this.nextSnapshotRefreshUtc)
        {
            return false;
        }

        this.nextSnapshotRefreshUtc = now.AddMilliseconds(SnapshotRefreshIntervalMs);
        CodexIqBoardSnapshot next = null;
        try
        {
            if (this.snapshotProvider != null)
            {
                long projectionStart = TimingStats.StartTimestamp();
                try
                {
                    next = this.snapshotProvider();
                }
                finally
                {
                    TimingStats.RecordElapsed("codex.iq_snapshot_projection", projectionStart);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (next == null)
        {
            next = CodexIqBoardSnapshot.CreateEmpty();
        }

        string nextSignature = BuildSnapshotSignature(next);
        bool changed = !string.Equals(nextSignature, this.visibleSignature, StringComparison.Ordinal);
        this.snapshot = next.Clone();
        this.visibleSignature = nextSignature;
        return changed;
    }

    private static string BuildSnapshotSignature(CodexIqBoardSnapshot value)
    {
        StringBuilder key = new StringBuilder(256);
        if (value == null)
        {
            return string.Empty;
        }

        key.Append(value.UpdatedKnown ? value.UpdatedLocal.Ticks : 0L).Append('|');
        key.Append(value.SelectedModelKey ?? string.Empty).Append('|')
            .Append(value.SelectedModelLabel ?? string.Empty).Append('|')
            .Append(value.SourceStale ? '1' : '0').Append(':')
            .Append(value.SourceStatus ?? string.Empty).Append('|');
        for (int i = 0; i < value.Models.Count; i++)
        {
            CodexIqBoardModelPoint point = value.Models[i];
            if (point != null)
            {
                key.Append(point.Key).Append(':')
                    .Append(point.Label).Append(':')
                    .Append(point.Family).Append(':')
                    .Append(point.Effort).Append(':')
                    .Append(point.Status).Append(':')
                    .Append(point.DataKnown ? point.DataLocal.Ticks : 0L).Append(':')
                    .Append(point.Iq.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                    .Append(point.AverageCostUsd.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                    .Append(point.AverageTaskSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                    .Append(point.TotalTokens.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                    .Append(point.Passed.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                    .Append(point.ValidTasks.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                    .Append(point.Current ? '1' : '0').Append(';');
            }
            else
            {
                key.Append("null;");
            }
        }

        key.Append("|T");
        for (int i = 0; i < value.Trends.Count; i++)
        {
            CodexIqBoardTrendPoint point = value.Trends[i];
            if (point == null)
            {
                key.Append("null;");
                continue;
            }

            key.Append(point.DateLocal.Ticks).Append(':')
                .Append(point.AverageTaskSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(point.TokenEfficiencyPercent.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(point.TotalTokens.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(point.EfficiencyKnown ? '1' : '0').Append(';');
        }

        key.Append("|Q");
        for (int i = 0; i < value.WeeklyQuotaRemaining.Count; i++)
        {
            key.Append(value.WeeklyQuotaRemaining[i].ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        for (int i = 0; i < value.Roster.Count; i++)
        {
            CodexIqBoardRosterEntry entry = value.Roster[i];
            if (entry != null)
            {
                key.Append('|').Append(entry.Key).Append(':')
                    .Append(entry.Label).Append(':').Append((int)entry.State);
            }
        }

        for (int i = 0; i < value.Services.Count; i++)
        {
            RadarServiceHealthEntry service = value.Services[i];
            if (service != null)
            {
                key.Append('|').Append(service.Label).Append(':')
                    .Append(service.Color.ToArgb()).Append(service.Checking ? "?" : string.Empty);
            }
        }

        CodexIqBoardRefreshStatus refresh = value.Refresh;
        if (refresh != null)
        {
            // Round the moving fractions so the band repaints as the marker advances, without a
            // repaint on every sub-pixel tick.
            key.Append("|R").Append(refresh.Known ? '1' : '0')
                .Append(refresh.StatusColor.ToArgb())
                .Append(refresh.Warning ? 'w' : '-')
                .Append(refresh.RequestRunning ? 'r' : '-')
                .Append(refresh.CurrentFraction.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(refresh.MarkerVisible ? refresh.MarkerFraction.ToString("0.00", CultureInfo.InvariantCulture) : "-")
                .Append(refresh.ArcStartFraction.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(refresh.ArcSweepFraction.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(refresh.PhaseText ?? string.Empty)
                .Append(':').Append(refresh.DetailText ?? string.Empty);
        }

        return key.ToString();
    }

    private bool UpdateOutsideClickDismissal(DateTime nowUtc)
    {
        if (!this.Visible || !this.IsLeftDocked || this.CurrentSettings == null ||
            !this.CurrentSettings.LeftDockOutsideClickCollapseEnabled)
        {
            return false;
        }

        Point clickPosition;
        DateTime clickUtc;
        if (!OutsideClickDismissalMonitor.TryGetClickAfter(ref this.outsideClickSequence, out clickPosition, out clickUtc))
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

        this.outsideClickCollapseUtc = clickUtc == DateTime.MinValue ? nowUtc : clickUtc;
        Program.LogInfo("Codex IQ board outside click collapsed transient board.");
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

        if (nowUtc < this.dockPointerLeftUtc.AddSeconds(this.CurrentSettings.LeftDockCollapseSeconds))
        {
            return false;
        }

        this.dockPointerLeftUtc = DateTime.MinValue;
        HideBoard();
        return true;
    }

    private void OnMaintenanceTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        DateTime now = DateTime.UtcNow;
        if (this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible)
        {
            this.dockTab.RefreshBurnInPosition();
        }

        if (UpdateOutsideClickDismissal(now) || UpdateDockCollapse(now) || !this.Visible)
        {
            return;
        }

        if (RefreshSnapshot(false))
        {
            RenderLayeredWindow();
        }

        bool inside = this.Bounds.Contains(this.cursorPositionProvider());
        if (inside)
        {
            this.mouseWasInside = true;
        }
        else if (this.mouseWasInside)
        {
            this.mouseWasInside = false;
            ResetAutoHideClock();
        }

        int autoHideSeconds = this.CurrentSettings.CodexIqBoardAutoHideSeconds;
        if (autoHideSeconds > 0 && !inside && now >= this.lastInteractionUtc.AddSeconds(autoHideSeconds))
        {
            HideBoard();
            return;
        }

        if (ShouldRefreshBurnInPosition())
        {
            PositionForDisplay();
        }
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
            if (old != null)
            {
                old.Dispose();
            }
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
        if (e.Button == MouseButtons.Left)
        {
            // This board is read-only. A click either hits the explicit close control or blank data
            // space, so both intentionally collapse it like the Spec and task boards.
            HideBoard();
        }
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
