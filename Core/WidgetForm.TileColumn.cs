using System;
using System.Collections.Generic;
using System.Drawing;

// Host side of the only retained metric presentation. WidgetForm stays hidden as the lifecycle and
// sampler owner; the same per-tick snapshot is pushed into all eleven tiles and the active expand panel.
// Nothing here starts a data timer — each tile's own hover poll is the only extra clock.
internal sealed partial class WidgetForm
{
    private readonly List<MetricTileForm> metricTileForms = new List<MetricTileForm>();
    private readonly QuotaEasterEggTracker quotaEasterEggTracker = new QuotaEasterEggTracker();
    private MetricTileExpandForm metricTileExpandForm;
    private int hoveredMetricTileIndex = -1;

    private bool IsRadarTileMode
    {
        get { return true; }
    }

    // Created lazily and then kept alive so a settings reload never drops a tile's burn-in phase.
    private void EnsureMetricTileWindows()
    {
        if (!this.childWindowLifecycleStarted)
        {
            return;
        }

        if (this.metricTileForms.Count != MetricTileModel.AllTileCount)
        {
            CloseMetricTileWindows();
            for (int i = 0; i < MetricTileModel.AllTileCount; i++)
            {
                MetricTileForm tile = new MetricTileForm(this.CurrentSettings, i);
                tile.TileHoverChanged += OnMetricTileHoverChanged;
                tile.Show(this);
                tile.HideTile();
                this.metricTileForms.Add(tile);
            }
        }

        if (this.metricTileExpandForm == null || this.metricTileExpandForm.IsDisposed)
        {
            this.metricTileExpandForm = new MetricTileExpandForm(this.CurrentSettings);
            this.metricTileExpandForm.Show(this);
            this.metricTileExpandForm.HidePanel();
        }
    }

    private void ApplyMetricTilePresentation()
    {
        if (!this.childWindowLifecycleStarted)
        {
            return;
        }

        EnsureMetricTileWindows();
        if (this.metricTileExpandForm != null && !this.metricTileExpandForm.IsDisposed)
        {
            this.metricTileExpandForm.ApplyRuntimeSettings(this.CurrentSettings);
        }

        bool anyVisible = false;
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile == null || tile.IsDisposed)
            {
                continue;
            }

            tile.ApplyRuntimeSettings(this.CurrentSettings);
            // Each tile is evaluated against its own screen rect, so a fullscreen app on one monitor
            // only hides the tiles actually sitting on that monitor. A tile whose group is switched
            // off is hidden regardless.
            bool hide = !MetricTileForm.IsTileEnabled(this.CurrentSettings, i) || ShouldHideFormForVisibilityMode(tile);
            tile.SetHiddenForFullscreen(hide);
            if (hide)
            {
                continue;
            }

            tile.ShowTile();
            anyVisible = true;
        }

        if (!anyVisible)
        {
            HideMetricTileExpand();
            return;
        }

        PushMetricTileFeed();
    }

    private void HideMetricTileWindows()
    {
        HideMetricTileExpand();
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].HideTile();
            }
        }
    }

    private void HideMetricTileExpand()
    {
        this.hoveredMetricTileIndex = -1;
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].ExpandKeepAliveBounds = Rectangle.Empty;
            }
        }

        if (this.metricTileExpandForm != null && !this.metricTileExpandForm.IsDisposed)
        {
            this.metricTileExpandForm.HidePanel();
        }
    }

    // Snapshot handed to the tile windows. The history lists are passed by reference on purpose:
    // the windows only read them, on the same UI thread that appends to them.
    private MetricTileFeed BuildMetricTileFeed()
    {
        MetricTileFeed feed = new MetricTileFeed();
        feed.Snapshot = this.snapshot;
        feed.CpuHistory = this.cpuHistory;
        feed.MemoryHistory = this.memoryHistory;
        feed.MemoryHardwareReservedHistory = this.memoryHardwareReservedHistory;
        feed.MemoryPressureHistory = this.memoryPressureHistory;
        feed.DiskWriteHistory = this.diskWriteHistory;
        feed.DiskReadHistory = this.diskReadHistory;
        feed.NetworkSentHistory = this.networkSentHistory;
        feed.NetworkReceivedHistory = this.networkReceivedHistory;
        feed.GpuHistory = this.gpuHistory;
        feed.GpuMemoryHistory = this.gpuMemoryHistory;
        feed.NpuHistory = this.npuHistory;
        feed.NpuMemoryHistory = this.npuMemoryHistory;
        feed.AlertTestEnabled = this.CurrentSettings != null && this.CurrentSettings.AlertTestEnabled;
        feed.NpuAlertIconActive = this.npuAlertIconActive;

        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            try
            {
                feed.Power = this.powerThermalForm.BuildStripSnapshot();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        GuardRuntime runtime = null;
        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            try
            {
                runtime = this.operationForm.PeekGuardRuntime();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        feed.Guards = MetricTileModel.BuildGuardEntries(runtime, DateTime.UtcNow);

        // Both families come from the shared Radar form, which keeps a separate runtime state for
        // each — so the Claude tiles stay populated even while the shared window is showing Codex.
        if (this.codexRadarForm != null && !this.codexRadarForm.IsDisposed)
        {
            try
            {
                feed.CodexRadar = this.codexRadarForm.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Codex);
                feed.ClaudeRadar = this.codexRadarForm.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Claude);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        feed.DeepSeekBalance = DeepSeekBalanceMonitor.GetSnapshot();
        feed.DeepSeekService = DeepSeekServiceMonitor.GetSnapshot();
        feed.QuotaEasterEgg = this.quotaEasterEggTracker.Update(
            this.CurrentSettings == null || this.CurrentSettings.GeniusProgrammerEasterEggEnabled,
            feed.CodexRadar,
            feed.ClaudeRadar);

        return feed;
    }

    private void PushMetricTileFeed()
    {
        if (this.metricTileForms.Count == 0)
        {
            return;
        }

        MetricTileFeed feed = null;
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile == null || tile.IsDisposed || !tile.Visible)
            {
                continue;
            }

            if (feed == null)
            {
                feed = BuildMetricTileFeed();
            }

            tile.UpdateFeed(feed);
        }

        if (feed != null &&
            this.hoveredMetricTileIndex >= 0 &&
            this.metricTileExpandForm != null &&
            !this.metricTileExpandForm.IsDisposed &&
            this.metricTileExpandForm.Visible)
        {
            this.metricTileExpandForm.UpdateFeed(feed);
        }
    }

    private void OnMetricTileHoverChanged(object sender, MetricTileHoverEventArgs e)
    {
        if (this.metricTileExpandForm == null || this.metricTileExpandForm.IsDisposed)
        {
            return;
        }

        if (e == null || e.Index < 0 || e.Index >= MetricTileModel.AllOrder.Length)
        {
            // Only the tile that currently owns the panel may close it: with eight independent
            // pollers, a neighbour reporting "left" must not shut a panel someone else just opened.
            MetricTileForm source = sender as MetricTileForm;
            if (source == null || source.TileIndex == this.hoveredMetricTileIndex)
            {
                HideMetricTileExpand();
            }

            return;
        }

        this.hoveredMetricTileIndex = e.Index;
        MetricTileForm tile = this.metricTileForms[e.Index];
        Rectangle anchor = new Rectangle(tile.Location, tile.Size);
        MetricTileId id = MetricTileModel.AllOrder[e.Index];
        MetricTileFeed feed = BuildMetricTileFeed();
        bool showRevival = this.quotaEasterEggTracker.TryConsumeRevival(id);
        this.metricTileExpandForm.ShowForTile(id, anchor, feed, showRevival);

        // Feed the panel's rect back to the owning tile so the pointer moving onto the panel still
        // counts as hovering; clear it on the others so they do not hold it open.
        Rectangle panelBounds = new Rectangle(
            this.metricTileExpandForm.Left,
            this.metricTileExpandForm.Top,
            this.metricTileExpandForm.Width,
            this.metricTileExpandForm.Height);
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].ExpandKeepAliveBounds = i == e.Index ? panelBounds : Rectangle.Empty;
            }
        }
    }

    private bool ProcessMetricTileInteractionTick()
    {
        bool active = false;
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                active |= this.metricTileForms[i].ProcessSharedInteractionTick();
            }
        }

        return active;
    }

    private bool UpdateMetricTileBurnInPresentation(BurnInVisualLevel level)
    {
        Point cursor = System.Windows.Forms.Cursor.Position;
        bool restoreRightGroupBrightness = false;
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile != null && !tile.IsDisposed && tile.Visible && tile.Bounds.Contains(cursor))
            {
                restoreRightGroupBrightness = true;
                break;
            }
        }

        if (!restoreRightGroupBrightness &&
            this.metricTileExpandForm != null &&
            !this.metricTileExpandForm.IsDisposed &&
            this.metricTileExpandForm.Visible &&
            this.metricTileExpandForm.Bounds.Contains(cursor))
        {
            restoreRightGroupBrightness = true;
        }

        bool changed = false;
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile != null && !tile.IsDisposed)
            {
                changed |= tile.SetBurnInVisualState(level, restoreRightGroupBrightness);
            }
        }

        if (this.metricTileExpandForm != null && !this.metricTileExpandForm.IsDisposed)
        {
            changed |= this.metricTileExpandForm.SetBurnInVisualState(level, restoreRightGroupBrightness);
        }

        return changed;
    }

    private void RefreshMetricTileBurnInPosition()
    {
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].RefreshBurnInPosition();
            }
        }
    }

    private void SetMetricTileAutoHideKeepAlive(bool active)
    {
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].SetAutoHideKeepAliveActive(active);
            }
        }
    }

    private void ApplyMetricTilesVisibilityMode()
    {
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile == null || tile.IsDisposed)
            {
                continue;
            }

            bool hide = !MetricTileForm.IsTileEnabled(this.CurrentSettings, i) || ShouldHideFormForVisibilityMode(tile);
            tile.SetHiddenForFullscreen(hide);
            if (hide && this.hoveredMetricTileIndex == i)
            {
                HideMetricTileExpand();
            }
        }
    }

    private void SetMetricTileDisplaySuspended(bool suspended)
    {
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].SetDisplaySuspended(suspended);
            }
        }

        if (this.metricTileExpandForm != null && !this.metricTileExpandForm.IsDisposed)
        {
            this.metricTileExpandForm.SetDisplaySuspended(suspended);
        }

        if (suspended)
        {
            this.hoveredMetricTileIndex = -1;
        }
    }

    private void RecoverMetricTilesAfterDisplayResume()
    {
        // Clearing the suspend flag first is what re-arms CanRenderLayeredWindow; without it the
        // recovery pass below would rebuild the surfaces and then refuse to draw into them.
        SetMetricTileDisplaySuspended(false);

        if (this.metricTileExpandForm != null && !this.metricTileExpandForm.IsDisposed)
        {
            this.metricTileExpandForm.RecoverAfterDisplayResume();
        }

        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            if (this.metricTileForms[i] != null && !this.metricTileForms[i].IsDisposed)
            {
                this.metricTileForms[i].RecoverAfterDisplayResume();
            }
        }

        this.hoveredMetricTileIndex = -1;
        ApplyMetricTilePresentation();
    }

    private void CloseMetricTileWindows()
    {
        if (this.metricTileExpandForm != null)
        {
            if (!this.metricTileExpandForm.IsDisposed)
            {
                this.metricTileExpandForm.Close();
            }

            this.metricTileExpandForm = null;
        }

        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile != null && !tile.IsDisposed)
            {
                tile.TileHoverChanged -= OnMetricTileHoverChanged;
                tile.Close();
            }
        }

        this.metricTileForms.Clear();
        this.hoveredMetricTileIndex = -1;
    }
}
