using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// One 60x60 metric tile — its own top-level layered window (1.0.6.13).
//
// The metric tiles used to be drawn into a single column window. That made them one hover target and
// one opacity surface: pointing at any tile faded the whole column, and none could be positioned
// independently. Each tile is now its own window, so hover and hidden-mode opacity remain per-tile.
// Manual placement is per-tile too; automatic placement deliberately shares one burn-in phase and
// one clamped group delta so a column cannot become ragged at a screen edge.
//
// Hover is polled against the cursor rather than taken from mouse messages, matching EdgeDockTabForm:
// layered WS_EX_NOACTIVATE tool windows do not receive a reliable enter/leave stream.
internal sealed partial class MetricTileForm : LayeredWidgetFormBase
{
    // Real screen pixels, not DPI-scaled logical units, matching every other user-facing size in
    // this app (the main panel's 522x448, the Radar module's 522x120). The drawing is authored
    // against a 60-unit tile and the content scale maps those units onto whichever pixel size is in
    // effect, so large mode is an exact 2x of the same picture.
    internal const int TileDesignUnits = 60;
    internal const int TileCompactPixels = 60;
    internal const int TileLargePixels = 120;
    internal const int TileGapDesignUnits = 8;
    internal const int TileLogicalRadius = 10;
    // A normal work area that can provide this many pixels per enabled tile never degrades below a
    // comfortably clickable target. Smaller values are only possible when fitting every enabled
    // tile is mathematically impossible at 24 px (for example eleven tiles in a 200 px work area).
    internal const int MinimumComfortableAutoTilePixels = 24;

    private const int HoverPollIntervalMs = 120;
    // Entering expands on the first poll; leaving waits three, so the pointer can cross the gap to
    // the expand panel without it collapsing underneath.
    private const int HoverEnterTicks = 1;
    private const int HoverExitTicks = 3;

    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly int tileIndex;
    private Func<Point> cursorPositionProvider;
    private MetricTileData tile;
    private bool pointerInside;
    private int pendingInside;
    private int pendingTicks;
    private bool displaySuspended;
    private bool hiddenForFullscreen;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc = DateTime.MinValue;
    private DateTime reverseHoverRevealUntilUtc = DateTime.MinValue;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool autoHideKeepAliveActive;
    private bool renderSampleHoverOverride;
    private BurnInVisualLevel burnInVisualLevel;
    private bool burnInBrightnessRestored;

    private struct AutoTileLayoutMetrics
    {
        public int TilePixels;
        public int DistributedWhitespacePixels;
    }

    // Raised when the pointer enters or leaves this tile. The host turns it into expand/collapse.
    public event EventHandler<MetricTileHoverEventArgs> TileHoverChanged;

    public MetricTileForm(WidgetSettings settings, int tileIndex)
    {
        this.tileIndex = tileIndex;
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        ApplicationIcon.ApplyTo(this);
        SetLayerScale(GetTileContentScale());
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        // Distinguishes the otherwise identical tool windows for accessibility and tests.
        this.Text = "MetricTile" + MetricTileModel.AllOrder[tileIndex];
        this.AccessibleName = this.Text;
        this.Size = GetDesiredSize();
        this.tile = MetricTileModel.BuildTile(MetricTileModel.AllOrder[tileIndex], new MetricTileFeed());
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = HoverPollIntervalMs;
        this.hoverTimer.Tick += OnHoverTick;
    }

    internal int TileIndex
    {
        get { return this.tileIndex; }
    }

    internal MetricTileId MetricId
    {
        get { return MetricTileModel.AllOrder[this.tileIndex]; }
    }

    protected override string LayeredWindowLogName
    {
        get { return this.Text; }
    }

    protected override string LayeredRenderTimingName
    {
        get { return "metrictile.render"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.MainWidgetTransparencyOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended && !this.hiddenForFullscreen;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyMouseClickThroughStyle(this.CurrentSettings.RightTileMouseClickThroughEnabled);
    }

    internal int GetTilePixels()
    {
        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
        return ResolveTilePixels(this.CurrentSettings, this.tileIndex, workArea);
    }

    private float GetTileContentScale()
    {
        return GetTilePixels() / (float)TileDesignUnits;
    }

    internal Size GetDesiredSize()
    {
        int px = GetTilePixels();
        return new Size(px, px);
    }

    internal static int GetTileGapPixels(WidgetSettings settings)
    {
        if (settings != null && settings.RightTileAutoArrangeEnabled)
        {
            Rectangle workArea = settings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
            int count = ResolveEnabledTileOrder(settings).Length;
            AutoTileLayoutMetrics metrics = ResolveAutoTileLayoutMetrics(settings, workArea, count);
            return EdgeColumnSpacing.ResolveGapAfterIndex(
                metrics.DistributedWhitespacePixels,
                0,
                Math.Max(0, count - 1));
        }

        int px = settings != null && settings.MainWidgetTileLargeModeEnabled ? TileLargePixels : TileCompactPixels;
        return (int)Math.Round(TileGapDesignUnits * (px / (float)TileDesignUnits));
    }

    private static int GetRequestedTilePixels(WidgetSettings settings)
    {
        return settings != null && settings.MainWidgetTileLargeModeEnabled
            ? TileLargePixels
            : TileCompactPixels;
    }

    private static int ResolveTilePixels(WidgetSettings settings, int index, Rectangle workArea)
    {
        if (settings != null && settings.RightTileAutoArrangeEnabled && IsTileEnabled(settings, index))
        {
            return ResolveAutoTileLayoutMetrics(
                settings,
                workArea,
                ResolveEnabledTileOrder(settings).Length).TilePixels;
        }

        return GetRequestedTilePixels(settings);
    }

    // Automatic mode retains the requested tile size whenever the bodies fit, then uses 0-100% of
    // the remaining work-area height as distributed whitespace. Only when the bodies themselves do
    // not fit do we shrink them. Manual layout never calls this resolver.
    private static AutoTileLayoutMetrics ResolveAutoTileLayoutMetrics(
        WidgetSettings settings,
        Rectangle workArea,
        int enabledCount)
    {
        AutoTileLayoutMetrics metrics = new AutoTileLayoutMetrics();
        int requestedPixels = GetRequestedTilePixels(settings);
        if (enabledCount <= 0)
        {
            metrics.TilePixels = requestedPixels;
            metrics.DistributedWhitespacePixels = 0;
            return metrics;
        }

        int availableWidth = Math.Max(1, workArea.Width);
        int availableHeight = Math.Max(enabledCount, workArea.Height);
        int tilePixels = Math.Min(requestedPixels, availableWidth);
        if ((long)tilePixels * enabledCount > availableHeight)
        {
            // Even touching tiles cannot retain the requested (possibly 120 px) mode. Use the largest
            // common square that fits every enabled tile. On any work area with N*24 px available,
            // this remains at least MinimumComfortableAutoTilePixels.
            tilePixels = Math.Max(1, Math.Min(tilePixels, availableHeight / enabledCount));
        }

        int distributedWhitespace = 0;
        if (enabledCount == 1)
        {
            tilePixels = Math.Max(1, Math.Min(tilePixels, availableHeight));
        }
        else
        {
            int spacingPercent = Math.Max(
                WidgetSettings.MinColumnButtonGapPixels,
                Math.Min(WidgetSettings.MaxColumnButtonGapPixels, settings.RightTileButtonGapPixels));
            distributedWhitespace = EdgeColumnSpacing.ResolveDistributedWhitespacePixels(
                spacingPercent,
                availableHeight - tilePixels * enabledCount);
        }

        metrics.TilePixels = tilePixels;
        metrics.DistributedWhitespacePixels = distributedWhitespace;
        return metrics;
    }

    // Default placement when a tile has no stored position: the right-edge column, derived from the
    // live work area so a fresh install is tidy at any resolution. All ten retained tiles are always
    // enabled; visibility policy may still temporarily hide an individual window.
    internal static bool IsTileEnabled(WidgetSettings settings, int index)
    {
        if (settings == null || index < 0 || index >= MetricTileModel.AllOrder.Length)
        {
            return false;
        }

        return true;
    }

    // The order helper remains shared by automatic placement and user ordering; convergence makes
    // all ten slots permanently active.
    internal static Point GetAutoTileLocation(WidgetSettings settings, int index)
    {
        if (settings != null && settings.RightTileAutoArrangeEnabled && IsTileEnabled(settings, index))
        {
            int[] order = ResolveEnabledTileOrder(settings);
            Rectangle[] bounds = ResolveAutoTileBounds(settings);
            for (int i = 0; i < order.Length && i < bounds.Length; i++)
            {
                if (order[i] == index)
                {
                    return bounds[i].Location;
                }
            }
        }

        return GetLegacyAutoTileLocation(settings, index);
    }

    // Automatic arrangement treats every enabled metric/Radar tile as one right-edge column. The
    // stable form index remains tied to MetricTileIds; only the visual slot follows the custom order,
    // so saved per-tile coordinates remain intact and become active again when auto arrange is off.
    internal static Rectangle[] ResolveAutoTileBounds(WidgetSettings settings)
    {
        return ResolveAutoTileBounds(settings, settings.GetWorkAreaForModule(WidgetSettings.ModuleMain));
    }

    internal static Rectangle[] ResolveAutoTileBounds(WidgetSettings settings, Rectangle workArea)
    {
        int[] order = ResolveEnabledTileOrder(settings);
        if (order.Length == 0)
        {
            return new Rectangle[0];
        }

        AutoTileLayoutMetrics metrics = ResolveAutoTileLayoutMetrics(settings, workArea, order.Length);
        int px = metrics.TilePixels;
        int distributedWhitespace = metrics.DistributedWhitespacePixels;
        int gapCount = Math.Max(0, order.Length - 1);
        int totalHeight = px * order.Length + distributedWhitespace;
        int offsetY = Math.Max(
            WidgetSettings.MinColumnGroupOffsetY,
            Math.Min(WidgetSettings.MaxColumnGroupOffsetY, settings.RightTileGroupOffsetY));
        int top = workArea.Top + (workArea.Height - totalHeight) / 2 + offsetY;
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - totalHeight)));
        int left = Math.Max(workArea.Left, workArea.Right - px);

        Rectangle[] bounds = new Rectangle[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            bounds[i] = new Rectangle(left, top, px, px);
            top += px;
            if (i < order.Length - 1)
            {
                top += EdgeColumnSpacing.ResolveGapAfterIndex(distributedWhitespace, i, gapCount);
            }
        }

        return bounds;
    }

    internal static Rectangle ResolveAutoTileGroupBounds(WidgetSettings settings)
    {
        return ResolveAutoTileGroupBounds(settings, settings.GetWorkAreaForModule(WidgetSettings.ModuleMain));
    }

    internal static Rectangle ResolveAutoTileGroupBounds(WidgetSettings settings, Rectangle workArea)
    {
        Rectangle[] bounds = ResolveAutoTileBounds(settings, workArea);
        if (bounds.Length == 0)
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(
            bounds[0].Left,
            bounds[0].Top,
            bounds[0].Right,
            bounds[bounds.Length - 1].Bottom);
    }

    internal static int[] ResolveEnabledTileOrder(WidgetSettings settings)
    {
        List<int> order = new List<int>(MetricTileModel.AllTileCount);
        HashSet<int> seen = new HashSet<int>();
        string[] configured = settings == null ? null : settings.RightTileButtonOrder;
        if (configured != null)
        {
            for (int i = 0; i < configured.Length; i++)
            {
                int index = WidgetSettings.IndexOfMetricTile(configured[i]);
                if (index >= 0 && seen.Add(index) && IsTileEnabled(settings, index))
                {
                    order.Add(index);
                }
            }
        }

        for (int i = 0; i < WidgetSettings.MetricTileIds.Length; i++)
        {
            if (seen.Add(i) && IsTileEnabled(settings, i))
            {
                order.Add(i);
            }
        }

        return order.ToArray();
    }

    private static Point GetLegacyAutoTileLocation(WidgetSettings settings, int index)
    {
        Rectangle workArea = settings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
        int px = settings != null && settings.MainWidgetTileLargeModeEnabled ? TileLargePixels : TileCompactPixels;
        int gap = (int)Math.Round(TileGapDesignUnits * (px / (float)TileDesignUnits));
        int stride = px + gap;

        int enabledCount = 0;
        int slot = 0;
        for (int i = 0; i < MetricTileModel.AllOrder.Length; i++)
        {
            if (!IsTileEnabled(settings, i))
            {
                continue;
            }

            if (i == index)
            {
                slot = enabledCount;
            }

            enabledCount++;
        }

        if (!IsTileEnabled(settings, index))
        {
            // Disabled tiles still need distinct fallback bounds for diagnostics and legacy manual
            // editing even though they do not participate in the visible automatic column.
            slot = index;
            enabledCount = MetricTileModel.AllOrder.Length;
        }

        if (enabledCount == 0)
        {
            enabledCount = 1;
        }

        int columnHeight = px * enabledCount + gap * (enabledCount - 1);
        int top = workArea.Top + Math.Max(0, (workArea.Height - columnHeight) / 2) + slot * stride;
        return new Point(Math.Max(workArea.Left, workArea.Right - px), top);
    }

    internal static Rectangle GetTileBounds(WidgetSettings settings, int index)
    {
        return GetTileBounds(
            settings,
            index,
            settings.GetWorkAreaForModule(WidgetSettings.ModuleMain));
    }

    private static Rectangle GetTileBounds(WidgetSettings settings, int index, Rectangle workArea)
    {
        int px = GetRequestedTilePixels(settings);
        if (settings != null && settings.RightTileAutoArrangeEnabled && IsTileEnabled(settings, index))
        {
            int[] order = ResolveEnabledTileOrder(settings);
            Rectangle[] bounds = ResolveAutoTileBounds(settings, workArea);
            for (int i = 0; i < order.Length && i < bounds.Length; i++)
            {
                if (order[i] == index)
                {
                    return bounds[i];
                }
            }
        }

        int left = settings.GetMetricTileLeftX(index);
        int bottom = settings.GetMetricTileBottomY(index);
        if (left == WidgetSettings.AutoTilePosition || bottom == WidgetSettings.AutoTilePosition)
        {
            Point auto = GetAutoTileLocation(settings, index);
            return new Rectangle(auto.X, auto.Y, px, px);
        }

        return new Rectangle(left, bottom - px + 1, px, px);
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyMouseClickThroughStyle(this.CurrentSettings.RightTileMouseClickThroughEnabled);
        SetLayerScale(GetTileContentScale());
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        if (this.Visible && CanRenderLayeredWindow())
        {
            PositionTile();
            RenderLayeredWindow();
        }
        else
        {
            InvalidateLayeredRenderBuffer();
        }
    }

    public void UpdateFeed(MetricTileFeed feed)
    {
        MetricTileData next = MetricTileModel.BuildTile(this.MetricId, feed);
        if (!HasVisibleChange(this.tile, next))
        {
            this.tile = next;
            return;
        }

        this.tile = next;
        InvalidateLayeredRenderBuffer();
        if (this.Visible && CanRenderLayeredWindow())
        {
            RenderLayeredWindow();
        }
    }

    private static bool HasVisibleChange(MetricTileData a, MetricTileData b)
    {
        if (a == null || b == null)
        {
            return true;
        }

        if (a.CenterValue != b.CenterValue ||
            a.CenterSuffix != b.CenterSuffix ||
            a.EasterEggVisual != b.EasterEggVisual ||
            a.EasterEggSecondLine != b.EasterEggSecondLine ||
            a.AlertIconVisible != b.AlertIconVisible ||
            a.Accent.ToArgb() != b.Accent.ToArgb() ||
            Math.Abs(a.OuterPercent - b.OuterPercent) >= 0.5 ||
            Math.Abs(a.InnerPercent - b.InnerPercent) >= 0.5 ||
            (a.AlertPercent >= 80.0) != (b.AlertPercent >= 80.0))
        {
            return true;
        }

        return !GuardsEqual(a.Guards, b.Guards);
    }

    private static bool GuardsEqual(List<MetricTileGuardEntry> a, List<MetricTileGuardEntry> b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Active != b[i].Active || a[i].Label != b[i].Label)
            {
                return false;
            }
        }

        return true;
    }

    public void ShowTile()
    {
        SetLayerScale(GetTileContentScale());
        this.Size = GetDesiredSize();
        PositionTile();
        if (!CanRenderLayeredWindow())
        {
            HideTile();
            return;
        }

        if (!this.Visible)
        {
            Show();
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(true, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
        this.hoverTimer.Start();
        RenderLayeredWindow();
    }

    public void HideTile()
    {
        this.hoverTimer.Stop();
        if (this.pointerInside)
        {
            this.pointerInside = false;
            RaiseHover(false);
        }

        this.pendingInside = -1;
        this.pendingTicks = 0;
        if (this.Visible)
        {
            Hide();
        }
    }

    public void SetDisplaySuspended(bool suspended)
    {
        if (this.displaySuspended == suspended)
        {
            return;
        }

        this.displaySuspended = suspended;
        if (suspended)
        {
            HideTile();
        }

        ResetDisplayRenderResources();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (this.hiddenForFullscreen == hidden)
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            HideTile();
        }
    }

    public void RefreshBurnInPosition()
    {
        if (this.Visible && CanRenderLayeredWindow() && ShouldRefreshBurnInPosition())
        {
            PositionTile();
            RenderLayeredWindow(false);
        }
    }

    public void RecoverAfterDisplayResume()
    {
        ResetDisplayRenderResources();
        if (this.Visible)
        {
            PositionTile();
            InvalidateLayeredRenderBuffer();
            RenderLayeredWindow();
        }
    }

    private void PositionTile()
    {
        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
        Rectangle bounds = GetTileBounds(this.CurrentSettings, this.tileIndex, workArea);
        if (this.Size != bounds.Size)
        {
            // Display changes can alter the auto-fit size without a settings mutation. Keep the HWND,
            // content scale and authored drawing canvas on the exact same resolved square.
            SetLayerScale(bounds.Width / (float)TileDesignUnits);
            this.Size = bounds.Size;
            InvalidateLayeredRenderBuffer();
        }

        this.Location = ResolveRuntimeTileLocation(this.CurrentSettings, this.tileIndex, workArea, bounds);
    }

    internal static Point ResolveRuntimeTileLocation(
        WidgetSettings settings,
        int index,
        Rectangle workArea,
        Rectangle tileBounds)
    {
        Point baseLocation = tileBounds.Location;
        if (settings != null && settings.RightTileAutoArrangeEnabled && IsTileEnabled(settings, index))
        {
            int[] order = ResolveEnabledTileOrder(settings);
            Rectangle[] runtimeBounds = ResolveAutoRuntimeTileBounds(settings, workArea);
            for (int i = 0; i < order.Length && i < runtimeBounds.Length; i++)
            {
                if (order[i] == index)
                {
                    return runtimeBounds[i].Location;
                }
            }
        }

        // Manual mode intentionally retains the historical per-window clamp semantics: a stored tile
        // is an independent surface and must not be moved merely because another tile reaches an edge.
        return BurnInProtection.ApplyRuntimeOffset(
            baseLocation,
            tileBounds.Size,
            workArea,
            BurnInProtection.MetricTileColumnSalt);
    }

    internal static Rectangle[] ResolveAutoRuntimeTileBounds(WidgetSettings settings, Rectangle workArea)
    {
        Rectangle[] baseBounds = ResolveAutoTileBounds(settings, workArea);
        if (baseBounds.Length == 0)
        {
            return baseBounds;
        }

        // Clamp the envelope exactly once, then distribute that one delta. Applying the same salt to
        // ten child rectangles is not sufficient: per-child edge clamps can turn the same raw offset
        // into different deltas and make the column overlap or go ragged at the top/bottom.
        Rectangle groupBounds = Rectangle.FromLTRB(
            baseBounds[0].Left,
            baseBounds[0].Top,
            baseBounds[0].Right,
            baseBounds[baseBounds.Length - 1].Bottom);
        Point runtimeGroupLocation = BurnInProtection.ApplyRuntimeOffset(
            groupBounds.Location,
            groupBounds.Size,
            workArea,
            BurnInProtection.MetricTileColumnSalt);
        Rectangle[] runtimeBounds = new Rectangle[baseBounds.Length];
        for (int i = 0; i < baseBounds.Length; i++)
        {
            runtimeBounds[i] = new Rectangle(
                ApplyRuntimeGroupDelta(baseBounds[i], groupBounds, runtimeGroupLocation),
                baseBounds[i].Size);
        }

        return runtimeBounds;
    }

    // Pure distribution seam used by the layout self-test. runtimeGroupLocation is already clamped
    // against the group envelope, so every tile receives the exact same X/Y delta.
    private static Point ApplyRuntimeGroupDelta(
        Rectangle tileBounds,
        Rectangle groupBounds,
        Point runtimeGroupLocation)
    {
        return new Point(
            tileBounds.Left + runtimeGroupLocation.X - groupBounds.Left,
            tileBounds.Top + runtimeGroupLocation.Y - groupBounds.Top);
    }

    // ── Hover / opacity ──────────────────────────────────────────────────
    // Hovering a tile must not hide it. The other layered windows fade to 5% because hidden mode
    // exists to clear a path to whatever sits underneath them, but a tile *is* the pointer target:
    // fading it out makes the button vanish under the cursor at the moment it is being aimed at.
    // Tiles dim to this floor instead — quieter, still legible, and paired with the accent border
    // DrawTile paints in the hovered state.
    private const double HoverDimAlphaScale = 0.6;

    protected override int ApplyHoverAlpha(int alpha)
    {
        if (!IsHoverOpacityRuntimeEnabled() || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(alpha * HoverDimAlphaScale);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    protected override int PresentationLuminancePercent
    {
        get
        {
            return this.burnInVisualLevel != BurnInVisualLevel.Normal && !this.burnInBrightnessRestored
                ? BurnInProtection.LevelOneLuminancePercent
                : 100;
        }
    }

    public bool SetBurnInVisualState(BurnInVisualLevel level, bool restoreRightGroupBrightness)
    {
        BurnInVisualLevel normalized = BurnInProtection.NormalizeVisualLevel(level);
        bool restored = normalized != BurnInVisualLevel.Normal && restoreRightGroupBrightness;
        if (this.burnInVisualLevel == normalized && this.burnInBrightnessRestored == restored)
        {
            return false;
        }

        this.burnInVisualLevel = normalized;
        this.burnInBrightnessRestored = restored;
        InvalidateLayeredRenderBuffer();
        if (this.Visible && CanRenderLayeredWindow())
        {
            RenderLayeredWindow();
        }

        return true;
    }

    private bool IsHoverOpacityRuntimeEnabled()
    {
        return this.CurrentSettings.HoverOpacityEnabled ||
            this.CurrentSettings.ForceHoverOpacityActive ||
            this.CurrentSettings.AutoHoverOpacityIdleEnabled ||
            this.CurrentSettings.AutoHoverOpacityMaximizedEnabled;
    }

    // Evaluated against this tile's own bounds, which is the whole point of splitting the column:
    // the pointer sitting on the CPU tile must not fade the other seven.
    private bool IsHoverOpacityTargetActive()
    {
        return HoverInteractionPolicy.IsHoverOpacityTargetActive(
            this.CurrentSettings,
            this.Bounds,
            this.hiddenForFullscreen,
            this.Visible,
            ref this.reverseHoverRevealUntilUtc,
            this.hoverOpacityDelayState,
            this.autoHideKeepAliveActive,
            // Exact bounds, not the sensitive-mouse square: tiles sit 68 px apart, so the ~100 px
            // activation square would put every neighbour into hidden mode along with the tile the
            // pointer is actually on.
            true);
    }

    public bool ProcessSharedInteractionTick()
    {
        if (this.hiddenForFullscreen || this.displaySuspended || !IsHoverOpacityRuntimeEnabled())
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue
            ? 0.03
            : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered = IsHoverOpacityTargetActive();
        double target = hovered ? 1.0 : 0.0;
        double previous = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        this.hoverOpacityProgress = this.hoverOpacityProgress < target
            ? Math.Min(target, this.hoverOpacityProgress + step)
            : Math.Max(target, this.hoverOpacityProgress - step);

        bool changed = Math.Abs(previous - this.hoverOpacityProgress) > 0.001;
        if (changed && this.Visible)
        {
            RenderLayeredWindow(false);
        }

        return Math.Abs(this.hoverOpacityProgress - target) > 0.001;
    }

    public void SetAutoHideKeepAliveActive(bool active)
    {
        if (this.autoHideKeepAliveActive == active)
        {
            return;
        }

        this.autoHideKeepAliveActive = active;
        if (active)
        {
            this.hoverOpacityDelayState.Reset();
            this.reverseHoverRevealUntilUtc = DateTime.MinValue;
        }
    }

    // The expand panel counts as part of this tile: moving the pointer onto it must not collapse it.
    internal Rectangle ExpandKeepAliveBounds { get; set; }

    private void OnHoverTick(object sender, EventArgs e)
    {
        if (!this.Visible || !CanRenderLayeredWindow())
        {
            return;
        }

        Point cursor = this.cursorPositionProvider();
        bool inside = new Rectangle(this.Location, this.Size).Contains(cursor);
        if (!inside && this.pointerInside)
        {
            Rectangle keepAlive = this.ExpandKeepAliveBounds;
            if (keepAlive.Width > 0 && keepAlive.Height > 0 && keepAlive.Contains(cursor))
            {
                inside = true;
            }
        }

        if (inside == this.pointerInside)
        {
            this.pendingInside = -1;
            this.pendingTicks = 0;
            return;
        }

        int desired = inside ? 1 : 0;
        if (desired != this.pendingInside)
        {
            this.pendingInside = desired;
            this.pendingTicks = 0;
        }

        this.pendingTicks++;
        if (this.pendingTicks < (inside ? HoverEnterTicks : HoverExitTicks))
        {
            return;
        }

        this.pendingInside = -1;
        this.pendingTicks = 0;
        this.pointerInside = inside;
        InvalidateLayeredRenderBuffer();
        RenderLayeredWindow();
        RaiseHover(inside);
    }

    private void RaiseHover(bool entered)
    {
        EventHandler<MetricTileHoverEventArgs> handler = this.TileHoverChanged;
        if (handler != null)
        {
            handler(this, new MetricTileHoverEventArgs(entered ? this.tileIndex : -1));
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────
    protected override void DrawWindowContent(Graphics g)
    {
        DrawTileContent(g);
    }

    internal void DrawTileContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int px = GetTilePixels();
        DrawTile(g, new RectangleF(0, 0, px, px), this.tile, this.pointerInside || this.renderSampleHoverOverride);
    }

    private void DrawTile(Graphics g, RectangleF bounds, MetricTileData data, bool hovered)
    {
        if (data == null)
        {
            return;
        }

        float radius = S(TileLogicalRadius);
        bool alert = data.AlertPercent >= 80.0;
        Color accent = alert ? DesignTokens.Colors.DangerStrong : data.Accent;

        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), radius))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 214)))
        {
            g.FillPath(fill, path);
            using (Pen border = new Pen(
                hovered ? DesignTokens.WithAlpha(accent, 240) : DesignTokens.White(38),
                hovered ? Math.Max(1.0f, S(1.5f)) : 1.0f))
            {
                g.DrawPath(border, path);
            }
        }

        float labelHeight = bounds.Height * 0.20f;
        RectangleF labelRect = new RectangleF(bounds.X, bounds.Y + bounds.Height * 0.055f, bounds.Width, labelHeight);
        using (Font labelFont = new Font("Segoe UI", Math.Max(6.0f, labelHeight * 0.74f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(accent, 255)))
        using (StringFormat format = CenterFormat())
        {
            g.DrawString(data.Label, labelFont, labelBrush, labelRect, format);
        }

        float ringTop = bounds.Y + bounds.Height * 0.235f;
        float ringSize = Math.Min(bounds.Width * 0.78f, bounds.Height - (ringTop - bounds.Y) - bounds.Height * 0.045f);
        RectangleF ringBox = new RectangleF(
            bounds.X + (bounds.Width - ringSize) / 2.0f,
            ringTop,
            ringSize,
            ringSize);

        if (data.Guards != null && data.OuterPercent < 0.0 && data.InnerPercent < 0.0)
        {
            DrawGuardDots(g, ringBox, data.Guards);
            return;
        }

        float outerStroke = Math.Max(1.5f, ringSize * 0.115f);
        float innerStroke = Math.Max(1.2f, ringSize * 0.090f);
        float innerInset = outerStroke + Math.Max(1.0f, ringSize * 0.045f);

        DrawRing(g, ringBox, outerStroke, data.OuterPercent, ResolveBurnInRingColor(accent, this.burnInVisualLevel), 255);
        if (data.InnerPercent >= 0.0)
        {
            RectangleF innerBox = RectangleF.Inflate(ringBox, -innerInset, -innerInset);
            // MEM's inner ring is itself the severity signal, so preserve its green/yellow/red
            // state even when the outer ring and centre enter the shared alert treatment.
            Color innerAccent = alert && data.Id != MetricTileId.Memory
                ? DesignTokens.Colors.Warning
                : data.InnerAccent;
            DrawRing(g, innerBox, innerStroke, data.InnerPercent, ResolveBurnInRingColor(innerAccent, this.burnInVisualLevel), 190);
        }

        if (ShouldDrawCenterText(this.burnInVisualLevel))
        {
            DrawCenterValue(g, ringBox, data, alert);
        }

        if (data.AlertIconVisible)
        {
            DrawAlertDot(g, bounds);
        }
    }

    private void DrawRing(Graphics g, RectangleF box, float stroke, double percent, Color color, int alpha)
    {
        if (box.Width <= stroke * 2.0f || box.Height <= stroke * 2.0f)
        {
            return;
        }

        RectangleF arcBox = new RectangleF(
            box.X + stroke / 2.0f,
            box.Y + stroke / 2.0f,
            box.Width - stroke,
            box.Height - stroke);

        using (Pen track = new Pen(DesignTokens.White(26), stroke))
        {
            g.DrawEllipse(track, arcBox);
        }

        if (percent <= 0.0)
        {
            return;
        }

        float sweep = (float)(MetricTileModel.Clamp(percent, 0.0, 100.0) / 100.0 * 360.0);
        sweep = Math.Max(1.5f, sweep);
        using (Pen arc = new Pen(DesignTokens.WithAlpha(color, alpha), stroke))
        {
            arc.StartCap = LineCap.Round;
            arc.EndCap = LineCap.Round;
            g.DrawArc(arc, arcBox, -90.0f, sweep);
        }
    }

    internal static Color ResolveBurnInRingColor(Color color, BurnInVisualLevel level)
    {
        return BurnInProtection.NormalizeVisualLevel(level) == BurnInVisualLevel.LevelTwo
            ? BurnInProtection.InvertColor(color)
            : color;
    }

    internal static bool ShouldDrawCenterText(BurnInVisualLevel level)
    {
        return BurnInProtection.NormalizeVisualLevel(level) != BurnInVisualLevel.LevelTwo;
    }

    private void DrawCenterValue(Graphics g, RectangleF ringBox, MetricTileData data, bool alert)
    {
        string text = string.IsNullOrEmpty(data.CenterValue) ? "--" : data.CenterValue;
        Color color = alert ? DesignTokens.Colors.DangerText : DesignTokens.Colors.TextStrong;
        float basis = ringBox.Height * (text.Length >= 3 ? 0.31f : 0.42f);
        using (Font font = new Font("Segoe UI", Math.Max(7.0f, basis), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat format = CenterFormat())
        {
            g.DrawString(text, font, brush, ringBox, format);
        }
    }

    private void DrawGuardDots(Graphics g, RectangleF box, List<MetricTileGuardEntry> guards)
    {
        float pad = box.Width * 0.16f;
        float cell = (box.Width - pad) / 2.0f;
        float dot = Math.Max(3.0f, cell * 0.56f);
        for (int i = 0; i < 4; i++)
        {
            int row = i / 2;
            int col = i % 2;
            float cx = box.X + col * (cell + pad) + cell / 2.0f;
            float cy = box.Y + row * (cell + pad) + cell / 2.0f;
            bool active = guards != null && i < guards.Count && guards[i].Active;
            Color accent = guards != null && i < guards.Count ? guards[i].Accent : DesignTokens.Colors.GlyphMuted;
            RectangleF dotBox = new RectangleF(cx - dot / 2.0f, cy - dot / 2.0f, dot, dot);
            if (active)
            {
                using (SolidBrush glow = new SolidBrush(DesignTokens.WithAlpha(accent, 70)))
                {
                    g.FillEllipse(glow, RectangleF.Inflate(dotBox, dot * 0.32f, dot * 0.32f));
                }
            }

            using (SolidBrush brush = new SolidBrush(active
                ? DesignTokens.WithAlpha(accent, 255)
                : DesignTokens.White(40)))
            {
                g.FillEllipse(brush, dotBox);
            }
        }
    }

    private void DrawAlertDot(Graphics g, RectangleF bounds)
    {
        float size = bounds.Width * 0.14f;
        RectangleF dot = new RectangleF(
            bounds.Right - size - bounds.Width * 0.10f,
            bounds.Y + bounds.Height * 0.075f,
            size,
            size);
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 235)))
        {
            g.FillEllipse(brush, dot);
        }
    }

    private static StringFormat CenterFormat()
    {
        StringFormat format = new StringFormat(StringFormatFlags.NoWrap);
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = StringTrimming.None;
        return format;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && this.hoverTimer != null)
        {
            this.hoverTimer.Stop();
            this.hoverTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    // ── Test / harness seams ─────────────────────────────────────────────
    internal Func<Point> CursorPositionProviderForTest
    {
        set { this.cursorPositionProvider = value ?? (delegate { return Cursor.Position; }); }
    }

    internal void UpdateFeedForRenderSample(MetricTileFeed feed)
    {
        this.tile = MetricTileModel.BuildTile(this.MetricId, feed);
    }

    internal void SetHoveredForRenderSample(bool hovered)
    {
        this.renderSampleHoverOverride = hovered;
    }

    internal void SetBurnInVisualStateForRenderSample(BurnInVisualLevel level, bool restoreRightGroupBrightness)
    {
        this.burnInVisualLevel = BurnInProtection.NormalizeVisualLevel(level);
        this.burnInBrightnessRestored = this.burnInVisualLevel != BurnInVisualLevel.Normal && restoreRightGroupBrightness;
    }

    private static void AssertAutoColumnReachable(
        WidgetSettings settings,
        Rectangle workArea,
        string scenario)
    {
        int[] order = ResolveEnabledTileOrder(settings);
        Rectangle[] bounds = ResolveAutoTileBounds(settings, workArea);
        if (bounds.Length != order.Length || bounds.Length == 0)
        {
            throw new InvalidOperationException(scenario + ": enabled order and auto bounds must match.");
        }

        int resolvedPixels = ResolveTilePixels(settings, order[0], workArea);
        for (int i = 0; i < bounds.Length; i++)
        {
            Rectangle current = bounds[i];
            if (current.Width != resolvedPixels || current.Height != resolvedPixels ||
                current.Width <= 0 || !workArea.Contains(current))
            {
                throw new InvalidOperationException(
                    scenario + ": every enabled tile must use the resolved square and remain reachable inside the work area.");
            }

            if (i > 0 && current.Top < bounds[i - 1].Bottom)
            {
                throw new InvalidOperationException(scenario + ": automatic tiles must never overlap.");
            }

            if (GetTileBounds(settings, order[i], workArea) != current)
            {
                throw new InvalidOperationException(
                    scenario + ": GetTileBounds must use the same size and slot as the automatic layout resolver.");
            }
        }
    }

    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();

        Color burnInSample = Color.FromArgb(255, 14, 62, 201);
        if (ResolveBurnInRingColor(burnInSample, BurnInVisualLevel.LevelOne).ToArgb() != burnInSample.ToArgb() ||
            ResolveBurnInRingColor(burnInSample, BurnInVisualLevel.LevelTwo).ToArgb() != BurnInProtection.InvertColor(burnInSample).ToArgb() ||
            !ShouldDrawCenterText(BurnInVisualLevel.LevelOne) ||
            ShouldDrawCenterText(BurnInVisualLevel.LevelTwo))
        {
            throw new InvalidOperationException("Metric tile level-two protection must invert rings and suppress center text only at level two.");
        }

        // Spec: 60x60 real screen pixels, independent of display DPI.
        using (MetricTileForm tile = new MetricTileForm(settings, 0))
        {
            Size desired = tile.GetDesiredSize();
            if (desired.Width != TileCompactPixels || desired.Height != TileCompactPixels)
            {
                throw new InvalidOperationException(
                    "Compact tile must be " + TileCompactPixels + "x" + TileCompactPixels +
                    " px regardless of display DPI; got " + desired.Width + "x" + desired.Height + ".");
            }
        }

        // Unplaced tiles fall back to the right-edge column, each in its own slot.
        int gap = GetTileGapPixels(settings);
        Rectangle first = GetTileBounds(settings, 0);
        Rectangle second = GetTileBounds(settings, 1);
        if (second.Top - first.Top != TileCompactPixels + gap)
        {
            throw new InvalidOperationException("Auto-placed tiles must stack one stride apart.");
        }

        Rectangle workArea = settings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
        if (first.Right != workArea.Right)
        {
            throw new InvalidOperationException("Auto-placed tiles must sit flush against the right work-area edge.");
        }

        // A stored position must win over the auto column, per tile, which is what makes each tile
        // independently placeable in the layout editor.
        WidgetSettings placed = WidgetSettings.CreateDefaults();
        placed.RightTileAutoArrangeEnabled = false;
        placed.SetMetricTilePosition(3, 100, 400);
        placed.Normalize();
        Rectangle moved = GetTileBounds(placed, 3);
        if (moved.Left != 100 || moved.Bottom - 1 != 400)
        {
            throw new InvalidOperationException("A stored tile position must override the auto column placement.");
        }

        if (GetTileBounds(placed, 4).Left != GetTileBounds(settings, 4).Left)
        {
            throw new InvalidOperationException("Moving one tile must not move its neighbours.");
        }

        WidgetSettings arranged = WidgetSettings.CreateDefaults();
        arranged.RightTileAutoArrangeEnabled = true;
        arranged.RightTileButtonGapPixels = 21;
        arranged.RightTileGroupOffsetY = 0;
        arranged.RightTileButtonOrder = new string[]
        {
            "ClaudeQuota", "Cpu", "CodexQuota", "Memory", "Disk",
            "Network", "Gpu", "Npu", "Power", "Guard", "DeepSeekQuota"
        };
        Rectangle testWorkArea = new Rectangle(-1920, 40, 1920, 1760);
        int[] arrangedOrder = ResolveEnabledTileOrder(arranged);
        Rectangle[] arrangedBounds = ResolveAutoTileBounds(arranged, testWorkArea);
        int arrangedWhitespace = EdgeColumnSpacing.ResolveDistributedWhitespacePixels(
            arranged.RightTileButtonGapPixels,
            testWorkArea.Height - arrangedBounds.Length * arrangedBounds[0].Height);
        if (arrangedOrder.Length != MetricTileModel.AllTileCount ||
            arrangedOrder[0] != WidgetSettings.IndexOfMetricTile("ClaudeQuota") ||
            arrangedOrder[1] != WidgetSettings.IndexOfMetricTile("Cpu") ||
            arrangedBounds[1].Top - arrangedBounds[0].Bottom !=
                EdgeColumnSpacing.ResolveGapAfterIndex(arrangedWhitespace, 0, arrangedBounds.Length - 1))
        {
            throw new InvalidOperationException("Right tile custom order, enabled filtering, or percentage spacing self-test failed.");
        }

        Rectangle centeredGroup = ResolveAutoTileGroupBounds(arranged, testWorkArea);
        arranged.RightTileGroupOffsetY = 100;
        Rectangle shiftedGroup = ResolveAutoTileGroupBounds(arranged, testWorkArea);
        if (shiftedGroup.Top - centeredGroup.Top != 100)
        {
            throw new InvalidOperationException("Right tile whole-group vertical offset self-test failed.");
        }

        Rectangle arrangedCpu = GetTileBounds(arranged, 0);
        arranged.SetMetricTilePosition(0, 100, 400);
        if (GetTileBounds(arranged, 0) != arrangedCpu)
        {
            throw new InvalidOperationException("Automatic right tile layout must not consume legacy per-tile coordinates.");
        }

        arranged.RightTileGroupOffsetY = WidgetSettings.MaxColumnGroupOffsetY;
        Rectangle bottomClamped = ResolveAutoTileGroupBounds(arranged, testWorkArea);
        arranged.RightTileGroupOffsetY = WidgetSettings.MinColumnGroupOffsetY;
        Rectangle topClamped = ResolveAutoTileGroupBounds(arranged, testWorkArea);
        if (bottomClamped.Bottom != testWorkArea.Bottom || topClamped.Top != testWorkArea.Top)
        {
            throw new InvalidOperationException("Right tile group offset must clamp the whole column inside the work area.");
        }

        // At 100, all remaining height becomes evenly distributed whitespace while the requested
        // 120 px tile bodies remain intact whenever those bodies fit.
        WidgetSettings extremeGap = arranged.Clone();
        extremeGap.MainWidgetTileLargeModeEnabled = true;
        extremeGap.RightTileButtonGapPixels = WidgetSettings.MaxColumnButtonGapPixels;
        extremeGap.RightTileGroupOffsetY = 0;
        Rectangle gapLimitedWorkArea = new Rectangle(100, 50, 320, 1370);
        Rectangle[] gapLimitedBounds = ResolveAutoTileBounds(extremeGap, gapLimitedWorkArea);
        AssertAutoColumnReachable(extremeGap, gapLimitedWorkArea, "large/extreme-gap auto column");
        if (gapLimitedBounds[0].Width != TileLargePixels ||
            gapLimitedBounds[0].Top != gapLimitedWorkArea.Top ||
            gapLimitedBounds[gapLimitedBounds.Length - 1].Bottom != gapLimitedWorkArea.Bottom ||
            gapLimitedBounds[1].Top - gapLimitedBounds[0].Bottom != 5)
        {
            throw new InvalidOperationException(
                "Right tile 100 spacing must cover the full work area without resizing fitting tile bodies.");
        }

        // On a genuinely short work area even zero gap cannot fit 120 px. The resolver therefore
        // picks the largest common square (26 px here), which is still a practical pointer target.
        Rectangle shortWorkArea = new Rectangle(-640, 30, 320, 286);
        Rectangle[] shortBounds = ResolveAutoTileBounds(extremeGap, shortWorkArea);
        AssertAutoColumnReachable(extremeGap, shortWorkArea, "short-screen large-mode auto column");
        if (shortBounds[0].Width != 26 ||
            shortBounds[0].Width < MinimumComfortableAutoTilePixels ||
            shortBounds[1].Top != shortBounds[0].Bottom)
        {
            throw new InvalidOperationException(
                "A short screen must deterministically shrink large tiles only after reducing the gap to zero.");
        }

        // GetDesiredSize and drawing both consume ResolveTilePixels; GetTileBounds consumes the same
        // metrics. Manual large mode intentionally remains exactly 120 px and retains stored geometry.
        WidgetSettings manualLarge = extremeGap.Clone();
        manualLarge.RightTileAutoArrangeEnabled = false;
        manualLarge.SetMetricTilePosition(0, 140, 500);
        Rectangle manualLargeBounds = GetTileBounds(manualLarge, 0);
        if (ResolveTilePixels(manualLarge, 0, shortWorkArea) != TileLargePixels ||
            manualLargeBounds.Size != new Size(TileLargePixels, TileLargePixels))
        {
            throw new InvalidOperationException("Automatic reachability fallback must not change manual large-mode sizing.");
        }

        // Pure group-delta test at the bottom/right edge. A simulated inward burn-in step is applied
        // to the envelope once; every child must receive the identical delta and remain disjoint.
        extremeGap.RightTileButtonGapPixels = 0;
        extremeGap.RightTileGroupOffsetY = WidgetSettings.MaxColumnGroupOffsetY;
        Rectangle burnInWorkArea = new Rectangle(20, 40, 500, 1400);
        Rectangle[] edgeBounds = ResolveAutoTileBounds(extremeGap, burnInWorkArea);
        Rectangle edgeGroup = ResolveAutoTileGroupBounds(extremeGap, burnInWorkArea);
        if (edgeGroup.Bottom != burnInWorkArea.Bottom || edgeGroup.Right != burnInWorkArea.Right)
        {
            throw new InvalidOperationException("Burn-in edge fixture must start flush with the bottom/right work-area edge.");
        }

        Point simulatedClampedGroup = new Point(edgeGroup.Left - 2, edgeGroup.Top - 3);
        int expectedDeltaX = simulatedClampedGroup.X - edgeGroup.Left;
        int expectedDeltaY = simulatedClampedGroup.Y - edgeGroup.Top;
        Rectangle previousRuntimeBounds = Rectangle.Empty;
        for (int i = 0; i < edgeBounds.Length; i++)
        {
            Point runtime = ApplyRuntimeGroupDelta(edgeBounds[i], edgeGroup, simulatedClampedGroup);
            Rectangle runtimeBounds = new Rectangle(runtime, edgeBounds[i].Size);
            if (runtime.X - edgeBounds[i].Left != expectedDeltaX ||
                runtime.Y - edgeBounds[i].Top != expectedDeltaY ||
                !burnInWorkArea.Contains(runtimeBounds) ||
                (i > 0 && runtimeBounds.Top < previousRuntimeBounds.Bottom))
            {
                throw new InvalidOperationException(
                    "Automatic burn-in must clamp the group envelope once and distribute one common delta.");
            }

            previousRuntimeBounds = runtimeBounds;
        }

        Rectangle[] productionRuntimeBounds = ResolveAutoRuntimeTileBounds(extremeGap, burnInWorkArea);
        int productionDeltaY = productionRuntimeBounds[0].Top - edgeBounds[0].Top;
        for (int i = 0; i < productionRuntimeBounds.Length; i++)
        {
            if (productionRuntimeBounds[i].Top - edgeBounds[i].Top != productionDeltaY ||
                !burnInWorkArea.Contains(productionRuntimeBounds[i]) ||
                (i > 0 && productionRuntimeBounds[i].Top < productionRuntimeBounds[i - 1].Bottom))
            {
                throw new InvalidOperationException(
                    "The production burn-in resolver must distribute one envelope-clamped Y delta to every tile.");
            }
        }

        // The reason the column was split into separate windows: the pointer resting on one tile
        // must not put its neighbours into hidden mode. Each tile evaluates the hover target against its
        // own bounds, so a cursor inside tile 0 must leave tile 1 unaffected.
        WidgetSettings hover = WidgetSettings.CreateDefaults();
        hover.HoverOpacityEnabled = true;
        hover.HoverOpacityRevealDelayEnabled = false;
        hover.Normalize();
        Rectangle firstBounds = GetTileBounds(hover, 0);
        Rectangle secondBounds = GetTileBounds(hover, 1);
        Point cursorOnFirst = new Point(firstBounds.Left + firstBounds.Width / 2, firstBounds.Top + firstBounds.Height / 2);
        DateTime nowUtc = DateTime.UtcNow;
        DateTime revealA = DateTime.MinValue;
        DateTime revealB = DateTime.MinValue;
        HoverInteractionPolicy.HoverOpacityDelayState stateA = new HoverInteractionPolicy.HoverOpacityDelayState();
        HoverInteractionPolicy.HoverOpacityDelayState stateB = new HoverInteractionPolicy.HoverOpacityDelayState();
        bool firstHidden = HoverInteractionPolicy.IsHoverOpacityTargetActiveAt(
            hover, cursorOnFirst, firstBounds, false, true, nowUtc, ref revealA, stateA, false, true);
        bool secondHidden = HoverInteractionPolicy.IsHoverOpacityTargetActiveAt(
            hover, cursorOnFirst, secondBounds, false, true, nowUtc, ref revealB, stateB, false, true);
        if (!firstHidden)
        {
            throw new InvalidOperationException("The hovered tile must enter hidden opacity.");
        }

        if (secondHidden)
        {
            throw new InvalidOperationException(
                "Hovering one tile must not put its neighbours into hidden mode; each tile evaluates hover against its own bounds.");
        }

        // Presentation modes were retired: every metric and Radar tile is now part of the single
        // canonical eleven-tile column.
        WidgetSettings canonical = WidgetSettings.CreateDefaults();
        canonical.Normalize();
        for (int i = 0; i < MetricTileModel.AllTileCount; i++)
        {
            if (!IsTileEnabled(canonical, i))
            {
                throw new InvalidOperationException("All eleven canonical metric tiles must remain enabled.");
            }
        }

        Console.WriteLine("Metric tile window: PASS consistent sizing, short-screen fallback, group burn-in, two-level ring/text protection, custom auto column, per-tile placement, hover isolation, fixed eleven-tile surface");
    }
}

internal sealed class MetricTileHoverEventArgs : EventArgs
{
    public MetricTileHoverEventArgs(int index)
    {
        this.Index = index;
    }

    public int Index { get; private set; }
}
