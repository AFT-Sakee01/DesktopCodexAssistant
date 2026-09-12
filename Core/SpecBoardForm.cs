using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SpecBoardForm : LayeredWidgetFormBase
{
    private const int MaintenanceIntervalMs = 500;
    private const int PollFallbackSeconds = 60;
    private const int ReconcileIntervalMinutes = 5;
    private const int ReconcileTimeoutMs = 3000;
    private const int CopySuccessNoticeSeconds = 2;
    // Below this logical SpecBoardWidth the board drops the project rail and renders a single
    // full-width action column (compact mode). 360 keeps the wide layout's cards no narrower than
    // the compact layout would be.
    internal const int CompactRailMinimumLogicalWidth = 360;
    // Live sessions are cheap to fetch (an in-memory clone) but re-rendering on every 500 ms
    // maintenance tick would be wasteful, so the band is resampled on this throttle instead.
    private const int TaskSampleIntervalMs = 2000;
    // In-memory only rail selection key for the synthetic "unattributed" row. It is never persisted
    // and the leading control character cannot collide with a project name from PROJECTS.json.
    private const string UnattributedProjectKey = "unattributed";
    private readonly OperationForm owner;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly System.Windows.Forms.Timer maintenanceTimer;
    private readonly System.Windows.Forms.Timer cardSingleClickTimer;
    private readonly List<ProjectHitTarget> projectHitTargets = new List<ProjectHitTarget>();
    private readonly List<CardHitTarget> cardHitTargets = new List<CardHitTarget>();
    private readonly List<FileSystemWatcher> projectWatchers = new List<FileSystemWatcher>();
    private readonly HashSet<string> autoPopupKnownRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> autoPopupHighlightedRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly SpecBoardSeenStateStore seenStateStore;
    private readonly object refreshCancellationSync = new object();
    private Func<Point> cursorPositionProvider;
    private FileSystemWatcher watcher;
    private SpecBoardSnapshot snapshot = new SpecBoardSnapshot();
    // Live Codex sessions for the merged Work Board band. Resampled on a throttle from the shared
    // in-memory presentation snapshot (no IO), never sampled from inside a paint pass.
    private CodexTaskMonitorSnapshot taskSnapshot = CodexTaskMonitorSnapshot.Empty;
    private DateTime nextTaskSampleUtc = DateTime.MinValue;
    // Runtime view toggle. WorkBoardView keeps deciding the startup default; clicking the footer
    // only overrides it for this session, matching how the retired task board behaved.
    private bool timelineView;
    private bool timelineViewUserChosen;
    private Rectangle timelineButtonBounds = Rectangle.Empty;
    private string selectedProject = string.Empty;
    private DateTime lastInteractionUtc = DateTime.UtcNow;
    private DateTime nextPollUtc = DateTime.MinValue;
    private DateTime nextReconcileUtc = DateTime.MinValue;
    private DateTime watcherDebounceUntilUtc = DateTime.MinValue;
    private bool mouseWasInside;
    private bool displaySuspended;
    private bool hiddenForFullscreen;
    private bool restoreAfterFullscreen;
    private bool restoreAutoPopupAfterFullscreen;
    private int refreshRunning;
    private int refreshQueued;
    private long refreshGeneration;
    private CancellationTokenSource refreshCancellation;
    private int watcherSignal;
    private SpecBoardRow pendingCardSingleClick;
    private string suppressedCardMouseUpPath = string.Empty;
    private string copySuccessNotice = string.Empty;
    private DateTime copySuccessNoticeUntilUtc = DateTime.MinValue;
    private bool seenStateInitialized;
    private bool autoPopupBaselineInitialized;
    private bool autoPopupActive;
    private DateTime autoPopupHideUtc = DateTime.MinValue;
    private DateTime autoPopupHighlightUntilUtc = DateTime.MinValue;
    private string projectWatcherSignature = string.Empty;
    private Rectangle managerButtonBounds = Rectangle.Empty;
    private Rectangle closeButtonBounds = Rectangle.Empty;
    private SpecBoardManagerForm managerForm;
    private EdgeDockTabForm dockTab;
    private DateTime dockPointerLeftUtc = DateTime.MinValue;
    private long outsideClickSequence;
    private DateTime outsideClickCollapseUtc = DateTime.MinValue;

    public SpecBoardForm(OperationForm owner, WidgetSettings settings)
    {
        this.owner = owner;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.seenStateStore = owner == null ? null : new SpecBoardSeenStateStore(SpecBoardSeenStateStore.DefaultPath);
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        this.SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "Workbench";
        this.AccessibleName = "Workbench";
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.Cursor = Cursors.Hand;
        this.Size = GetDesiredSize();
        this.maintenanceTimer = new System.Windows.Forms.Timer();
        this.maintenanceTimer.Interval = MaintenanceIntervalMs;
        this.maintenanceTimer.Tick += OnMaintenanceTick;
        this.cardSingleClickTimer = new System.Windows.Forms.Timer();
        this.cardSingleClickTimer.Interval = Math.Max(1, SystemInformation.DoubleClickTime);
        this.cardSingleClickTimer.Tick += OnCardSingleClickTimerTick;
    }

    protected override string LayeredWindowLogName
    {
        get { return "SpecBoard"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.SpecBoardTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.SpecBoardScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    internal void PreparePresentationState(bool suspended, bool fullscreenHidden)
    {
        this.displaySuspended = suspended;
        this.hiddenForFullscreen = fullscreenHidden;
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        string oldLedgerPath = this.CurrentSettings == null ? string.Empty : this.CurrentSettings.SpecBoardLedgerPath;
        bool oldAutoPopupEnabled = this.CurrentSettings != null && this.CurrentSettings.SpecBoardAutoPopupEnabled;
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        bool ledgerPathChanged = !string.Equals(oldLedgerPath, this.CurrentSettings.SpecBoardLedgerPath, StringComparison.OrdinalIgnoreCase);
        if (ledgerPathChanged)
        {
            CancelRefresh();
            DisposeWatcher();
            DisposeProjectWatchers();
            this.autoPopupKnownRows.Clear();
            this.autoPopupHighlightedRows.Clear();
            this.autoPopupBaselineInitialized = false;
            this.autoPopupActive = false;
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

        UpdateMonitoringState();
        if (ShouldMonitorWork() && (ledgerPathChanged || !oldAutoPopupEnabled && this.CurrentSettings.SpecBoardAutoPopupEnabled))
        {
            RequestRefresh(true);
        }
    }

    // The dock tab is the board's only always-visible surface, so it is created as soon as the
    // owner builds the (hidden) board at startup and torn down when the setting is turned off.
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
                EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SpecBoard),
                BurnInProtection.SpecBoardDockTabSalt,
                "SpecBoardDockTab",
                EdgeDockTabRole.SpecBoard);
            this.dockTab.HoverEntered += OnDockTabHoverEntered;
            this.dockTab.HoverExited += OnDockTabHoverExited;
            this.dockTab.PollTick += OnDockTabPollTick;
        }
        else
        {
            this.dockTab.ApplyRuntimeSettings(
                this.CurrentSettings,
                EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SpecBoard));
        }

        this.dockTab.SetDisplaySuspended(this.displaySuspended);
        this.dockTab.SetHiddenForFullscreen(this.hiddenForFullscreen);
        this.dockTab.ShowTab(ResolveDockTabCenterY());
        this.maintenanceTimer.Start();
    }

    private void OnDockTabHoverEntered(object sender, EventArgs e)
    {
        if (this.IsDisposed || !this.IsLeftDocked || this.Visible ||
            LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            return;
        }

        if (OutsideClickDismissalMonitor.ShouldSuppressTabReopen(this.outsideClickCollapseUtc, DateTime.UtcNow))
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

    private bool UpdateOutsideClickDismissal(DateTime nowUtc)
    {
        bool enabled = this.Visible &&
            this.CurrentSettings != null &&
            this.CurrentSettings.LeftDockOutsideClickCollapseEnabled &&
            (this.IsLeftDocked || this.autoPopupActive);
        if (!enabled)
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
        Rectangle managerBounds = this.managerForm != null && !this.managerForm.IsDisposed && this.managerForm.Visible
            ? this.managerForm.Bounds
            : Rectangle.Empty;
        if (!OutsideClickDismissalMonitor.ShouldDismissOutsideClick(
            true,
            clickPosition,
            this.Bounds,
            tabBounds,
            managerBounds))
        {
            return false;
        }

        this.outsideClickCollapseUtc = clickUtc == DateTime.MinValue ? nowUtc : clickUtc;
        Program.LogInfo("SpecBoard outside click collapsed transient board.");
        HideBoard();
        return true;
    }

    // Docked boards collapse on their own once the pointer leaves both the board and its tab. This
    // is deliberately separate from SpecBoardAutoHideSeconds: a peek panel needs a ~1s exit, while
    // the manually opened board keeps its much longer idle timeout.
    private bool UpdateDockCollapse(DateTime nowUtc)
    {
        if (!this.IsLeftDocked || !this.Visible || this.autoPopupActive)
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

    public void StartAutoPopupMonitoring()
    {
        if (this.IsDisposed)
        {
            return;
        }

        // A hidden WinForms window needs a handle before an async refresh can marshal its result
        // back to the UI thread. Creating the handle does not show the board.
        IntPtr unused = this.Handle;
        UpdateMonitoringState();
        if (!this.autoPopupBaselineInitialized)
        {
            RequestRefresh(true);
        }
    }

    public void ShowBoard()
    {
        ShowBoardCore(false);
    }

    private void ShowBoardCore(bool automaticPopup)
    {
        if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            return;
        }

        if (this.owner != null)
        {
            this.owner.PrepareForSpecBoardOverlayShow();
        }

        // Expanding must show current sessions immediately rather than whatever the throttle last
        // captured, so force one resample before the first paint.
        RefreshTaskSampleIfDue(DateTime.UtcNow, true);

        this.autoPopupActive = automaticPopup;
        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.outsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
        if (automaticPopup)
        {
            this.autoPopupHideUtc = DateTime.UtcNow.AddSeconds(this.CurrentSettings.SpecBoardAutoPopupSeconds);
        }
        else
        {
            this.autoPopupHideUtc = DateTime.MinValue;
        }

        this.selectedProject = string.Empty;
        ApplyRuntimeSettings(this.CurrentSettings);
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
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        ResetAutoHideClock();
        RequestRefresh(true);
        RenderLayeredWindow();
    }

    public void HideBoard()
    {
        this.autoPopupActive = false;
        this.autoPopupHideUtc = DateTime.MinValue;
        this.autoPopupHighlightedRows.Clear();
        this.autoPopupHighlightUntilUtc = DateTime.MinValue;
        if (this.Visible)
        {
            Hide();
        }
    }

    public void SetHiddenForFullscreen(bool hidden)
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
            this.restoreAutoPopupAfterFullscreen = this.autoPopupActive;
            HideBoard();
        }
        else if (this.restoreAfterFullscreen && !this.displaySuspended)
        {
            this.restoreAfterFullscreen = false;
            bool automaticPopup = this.restoreAutoPopupAfterFullscreen;
            this.restoreAutoPopupAfterFullscreen = false;
            ShowBoardCore(automaticPopup);
        }
        else
        {
            UpdateMonitoringState();
        }
    }

    public void PrepareForDisplaySuspend()
    {
        this.displaySuspended = true;
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(true);
        }

        SuspendVisibleWork();
        ResetDisplayRenderResources();
    }

    public void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        ResetDisplayRenderResources();
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(false);
        }

        if (!this.hiddenForFullscreen && this.restoreAfterFullscreen)
        {
            this.restoreAfterFullscreen = false;
            bool automaticPopup = this.restoreAutoPopupAfterFullscreen;
            this.restoreAutoPopupAfterFullscreen = false;
            ShowBoardCore(automaticPopup);
        }

        if (ShouldMonitorWork())
        {
            ResumeVisibleWork();
            SyncLeftDockTab();
            if (this.Visible)
            {
                PositionForDisplay();
                RequestRefresh(true);
                RenderLayeredWindow();
            }
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (ShouldMonitorWork())
        {
            ResumeVisibleWork();
        }
        else
        {
            SuspendVisibleWork();
        }
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
        if (!this.mouseWasInside)
        {
            this.mouseWasInside = true;
            ResetAutoHideClock();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.mouseWasInside = false;
        ResetAutoHideClock();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ResetAutoHideClock();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ResetAutoHideClock();
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (!this.closeButtonBounds.IsEmpty && this.closeButtonBounds.Contains(e.Location))
        {
            HideBoard();
            return;
        }

        if (!this.managerButtonBounds.IsEmpty && this.managerButtonBounds.Contains(e.Location))
        {
            ShowManagerWindow();
            return;
        }

        if (!this.timelineButtonBounds.IsEmpty && this.timelineButtonBounds.Contains(e.Location))
        {
            // Runtime-only override; WorkBoardView still decides the next startup.
            this.timelineView = !IsTimelineView;
            this.timelineViewUserChosen = true;
            ResetAutoHideClock();
            RenderLayeredWindow();
            return;
        }

        for (int i = 0; i < this.projectHitTargets.Count; i++)
        {
            ProjectHitTarget target = this.projectHitTargets[i];
            if (!target.Bounds.Contains(e.Location))
            {
                continue;
            }

            this.selectedProject = string.Equals(this.selectedProject, target.Project, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : target.Project;
            if (!string.IsNullOrEmpty(target.Project) && this.seenStateStore != null)
            {
                EnsureSeenStateInitialized(this.snapshot);
                this.seenStateStore.MarkSeen(target.Project, this.snapshot.ScanTimeUtc);
            }
            RenderLayeredWindow();
            return;
        }

        for (int i = 0; i < this.cardHitTargets.Count; i++)
        {
            CardHitTarget target = this.cardHitTargets[i];
            if (target.Bounds.Contains(e.Location))
            {
                HandleCardMouseUp(target.Row);
                return;
            }
        }

        if (ShouldDismissForBlankClick(e.Location))
        {
            HideBoard();
        }
    }

    internal bool ShouldDismissForBlankClick(Point location)
    {
        if ((!this.closeButtonBounds.IsEmpty && this.closeButtonBounds.Contains(location)) ||
            (!this.timelineButtonBounds.IsEmpty && this.timelineButtonBounds.Contains(location)) ||
            (!this.managerButtonBounds.IsEmpty && this.managerButtonBounds.Contains(location)))
        {
            return false;
        }

        for (int i = 0; i < this.projectHitTargets.Count; i++)
        {
            if (this.projectHitTargets[i].Bounds.Contains(location))
            {
                return false;
            }
        }

        for (int i = 0; i < this.cardHitTargets.Count; i++)
        {
            if (this.cardHitTargets[i].Bounds.Contains(location))
            {
                return false;
            }
        }

        return true;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        ResetAutoHideClock();
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        for (int i = 0; i < this.cardHitTargets.Count; i++)
        {
            CardHitTarget target = this.cardHitTargets[i];
            if (target.Bounds.Contains(e.Location))
            {
                HandleCardDoubleClick(target.Row);
                return;
            }
        }
    }

    protected override void DrawWindowContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        this.projectHitTargets.Clear();
        this.cardHitTargets.Clear();
        this.managerButtonBounds = Rectangle.Empty;
        this.closeButtonBounds = Rectangle.Empty;

        SpecBoardPalette palette = GetPalette();
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 238)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }

        DrawBoard(g, palette, true);
        DrawCopySuccessNotice(g, palette);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.SpecBoard, this.LayerScale);
    }

    private void DrawCopySuccessNotice(Graphics g, SpecBoardPalette palette)
    {
        if (string.IsNullOrEmpty(this.copySuccessNotice) || DateTime.UtcNow >= this.copySuccessNoticeUntilUtc)
        {
            return;
        }

        Font font = this.fontCache.GetUi(S(9.0f), FontStyle.Bold);
        SizeF measured = g.MeasureString(this.copySuccessNotice, font, int.MaxValue, StringFormat.GenericTypographic);
        int horizontalPadding = S(10);
        int verticalPadding = S(5);
        int width = Math.Min(this.Width - S(20), Math.Max(S(90), (int)Math.Ceiling(measured.Width) + horizontalPadding * 2));
        int height = Math.Max(S(24), (int)Math.Ceiling(measured.Height) + verticalPadding * 2);
        Rectangle bounds = new Rectangle(Math.Max(S(10), this.Width - width - S(10)), Math.Max(S(10), this.Height - height - S(10)), width, height);
        using (GraphicsPath path = RoundedRectangle(bounds, S(7)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 246)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(palette.Success, 230), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush text = new SolidBrush(palette.Success))
        using (StringFormat centered = CreateStringFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            centered.LineAlignment = StringAlignment.Center;
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(this.copySuccessNotice, font, text, bounds, centered);
        }
    }

    private void DrawBoard(Graphics g, SpecBoardPalette palette, bool recordHitTargets)
    {
        int pad = S(10);
        Font headerFont = this.fontCache.GetUi(S(13.0f), FontStyle.Bold);
        Font countFont = this.fontCache.GetMono(S(9.5f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(9.5f), FontStyle.Regular);
        Font bodyBold = this.fontCache.GetUi(S(10.0f), FontStyle.Bold);
        Font smallFont = this.fontCache.GetUi(S(8.4f), FontStyle.Regular);
        Font smallBold = this.fontCache.GetUi(S(8.4f), FontStyle.Bold);
        int headerHeight = MeasureLineHeight(g, headerFont, S(6));
        int footerHeight = MeasureLineHeight(g, smallFont, S(5));
        int projectRowHeight = MeasureLineHeight(g, bodyFont, S(7));
        int segmentHeight = MeasureLineHeight(g, smallBold, S(6));
        int cardTitleHeight = MeasureLineHeight(g, bodyBold, S(2));
        int cardSubtitleHeight = MeasureLineHeight(g, smallFont, S(2));
        int cardHeight = cardTitleHeight + cardSubtitleHeight + S(8);
        int cardGap = S(4);

        Rectangle content = new Rectangle(pad, pad, Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - pad * 2));
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, headerHeight);
        int columnsTop = header.Bottom + S(5);
        int columnsHeight = Math.Max(1, content.Bottom - columnsTop);

        WorkBoardModel model = BuildWorkBoardModel();
        DrawHeader(g, header, headerFont, countFont, palette, model);

        // Compact single-column mode: below this logical width the 37% project rail would leave the
        // cards narrower than a readable title, so the rail is dropped and the action flow takes the
        // full width. The card subtitle already names each row's project, so no information is lost;
        // per-project filtering and freshness dots stay exclusive to the wide layout.
        if (this.CurrentSettings != null && this.CurrentSettings.SpecBoardWidth < CompactRailMinimumLogicalWidth)
        {
            this.selectedProject = string.Empty;
            model = BuildWorkBoardModel();
            Rectangle full = new Rectangle(content.Left, columnsTop, content.Width, columnsHeight);
            Rectangle compactFooter = new Rectangle(full.Left, Math.Max(full.Top, full.Bottom - footerHeight), full.Width, footerHeight);
            Rectangle flow = new Rectangle(full.Left, full.Top, full.Width, Math.Max(1, compactFooter.Top - S(3) - full.Top));
            DrawWorkFlow(g, flow, model, true, segmentHeight, cardHeight, cardGap, bodyBold, smallFont, smallBold, palette, recordHitTargets);
            DrawBoardFooter(g, compactFooter, smallFont, palette, recordHitTargets);
            return;
        }

        int leftWidth = Math.Max(S(112), (int)Math.Round(content.Width * 0.37));
        Rectangle left = new Rectangle(content.Left, columnsTop, leftWidth, columnsHeight);
        Rectangle right = new Rectangle(left.Right + S(7), columnsTop, Math.Max(1, content.Right - left.Right - S(7)), columnsHeight);

        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 112), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(divider, left.Right + S(3), left.Top, left.Right + S(3), left.Bottom);
        }

        DrawProjectRail(g, left, footerHeight, projectRowHeight, bodyFont, smallFont, palette, recordHitTargets, model);
        DrawWorkFlow(g, right, model, false, segmentHeight, cardHeight, cardGap, bodyBold, smallFont, smallBold, palette, recordHitTargets);
    }

    // The merged board reads both halves through one pure composer, so the rail filter applies to
    // live sessions and ledger rows at the same time. Compose does no IO and never mutates the
    // snapshots, which is what makes it safe to call from the paint path.
    private WorkBoardModel BuildWorkBoardModel()
    {
        WorkBoardFilter filter;
        if (string.Equals(this.selectedProject, UnattributedProjectKey, StringComparison.Ordinal))
        {
            filter = WorkBoardFilter.Unattributed;
        }
        else if (string.IsNullOrEmpty(this.selectedProject))
        {
            filter = WorkBoardFilter.All;
        }
        else
        {
            filter = WorkBoardFilter.ForProject(this.selectedProject);
        }

        WorkBoardLimits limits = WorkBoardLimits.Default;
        limits.SpecSessionHintEnabled = this.CurrentSettings == null || this.CurrentSettings.WorkBoardSpecSessionHintEnabled;
        return WorkBoardComposer.Compose(this.snapshot, this.taskSnapshot, filter, DateTime.Now, limits);
    }

    private bool IsTimelineView
    {
        get
        {
            return this.timelineViewUserChosen
                ? this.timelineView
                : (this.CurrentSettings != null && this.CurrentSettings.WorkBoardView == CodexTaskBoardView.Timeline);
        }
    }

    // Resampled off the existing maintenance tick, never from a paint pass. Returns true when the
    // visible content could have changed.
    private bool RefreshTaskSampleIfDue(DateTime nowUtc, bool force)
    {
        if (!force && nowUtc < this.nextTaskSampleUtc)
        {
            return false;
        }

        this.nextTaskSampleUtc = nowUtc.AddMilliseconds(TaskSampleIntervalMs);
        CodexTaskMonitorSnapshot next = CodexTaskPresentation.GetSnapshot();
        CodexTaskMonitorSnapshot previous = this.taskSnapshot;
        this.taskSnapshot = next ?? CodexTaskMonitorSnapshot.Empty;
        return !ReferenceEquals(previous, this.taskSnapshot);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font headerFont, Font countFont, SpecBoardPalette palette, WorkBoardModel model)
    {
        int unregistered = this.snapshot.Count(string.Empty, SpecBoardStatus.Unregistered);
        int pending = this.snapshot.Count(string.Empty, SpecBoardStatus.Pending);
        int awaiting = this.snapshot.Count(string.Empty, SpecBoardStatus.AwaitingVerify);
        int revision = this.snapshot.Count(string.Empty, SpecBoardStatus.NeedsRevision);
        int done = this.snapshot.Count(string.Empty, SpecBoardStatus.Done);
        using (SolidBrush text = new SolidBrush(palette.Text))
        using (SolidBrush red = new SolidBrush(palette.Danger))
        using (SolidBrush yellow = new SolidBrush(palette.Warning))
        using (SolidBrush purple = new SolidBrush(palette.Revision))
        using (SolidBrush green = new SolidBrush(palette.Success))
        using (StringFormat left = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat right = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
        {
            g.DrawString("WORKBENCH", headerFont, text, bounds, left);
            string time = this.snapshot.LedgerLastWriteLocal.HasValue ? this.snapshot.LedgerLastWriteLocal.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "--:--";
            float timeWidth = g.MeasureString(time, countFont).Width;
            RectangleF timeRect = new RectangleF(bounds.Right - timeWidth, bounds.Top, timeWidth, bounds.Height);
            g.DrawString(time, countFont, text, timeRect, right);
            float x = timeRect.Left - S(8);
            // Live session count leads the ledger dots: it is the most volatile number on the board.
            if (model != null && model.LiveCount > 0)
            {
                x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "▶" + model.LiveCount.ToString(CultureInfo.InvariantCulture), countFont, green);
            }

            x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + done.ToString(CultureInfo.InvariantCulture), countFont, green);
            x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + awaiting.ToString(CultureInfo.InvariantCulture), countFont, yellow);
            x = DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + revision.ToString(CultureInfo.InvariantCulture), countFont, purple);
            DrawHeaderCount(g, x, bounds.Top, bounds.Height, "●" + (pending + unregistered).ToString(CultureInfo.InvariantCulture), countFont, red);
        }
    }

    private static float DrawHeaderCount(Graphics g, float right, float top, float height, string text, Font font, Brush brush)
    {
        float width = g.MeasureString(text, font).Width + 4;
        RectangleF rect = new RectangleF(right - width, top, width, height);
        using (StringFormat format = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
        {
            g.DrawString(text, font, brush, rect, format);
        }

        return rect.Left - 4;
    }

    private void DrawProjectRail(Graphics g, Rectangle bounds, int footerHeight, int rowHeight, Font bodyFont, Font smallFont, SpecBoardPalette palette, bool recordHitTargets, WorkBoardModel model)
    {
        Rectangle footer = new Rectangle(bounds.Left, Math.Max(bounds.Top, bounds.Bottom - footerHeight), bounds.Width, footerHeight);
        int availableRowsHeight = Math.Max(0, footer.Top - bounds.Top - S(3));
        List<SpecBoardProject> projects = this.snapshot.Projects;
        // The synthetic "unattributed" row exists only while a session's cwd leaf matched no
        // registered project. It carries no ledger counts and never participates in freshness.
        WorkBoardProjectRow unattributed = null;
        if (model != null)
        {
            for (int i = 0; i < model.ProjectRows.Count; i++)
            {
                if (model.ProjectRows[i].IsUnattributed)
                {
                    unattributed = model.ProjectRows[i];
                    break;
                }
            }
        }

        int totalRows = projects.Count + 1 + (unattributed != null ? 1 : 0);
        int maxRows = rowHeight <= 0 ? 0 : availableRowsHeight / rowHeight;
        bool needsMore = totalRows > maxRows;
        int rowsToDraw = Math.Min(totalRows, needsMore ? Math.Max(0, maxRows - 1) : maxRows);
        int y = bounds.Top;
        for (int i = 0; i < rowsToDraw; i++)
        {
            bool isUnattributedRow = unattributed != null && i == projects.Count + 1;
            string project = i == 0 ? string.Empty : (isUnattributedRow ? UnattributedProjectKey : projects[i - 1].Name);
            string display = i == 0 ? "全部" : (isUnattributedRow ? unattributed.Display : projects[i - 1].Display);
            Rectangle row = new Rectangle(bounds.Left, y, bounds.Width, rowHeight);
            bool selected = string.Equals(this.selectedProject, project, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(row, -1, -1), S(5)))
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 220)))
                {
                    g.FillPath(fill, path);
                }
            }

            int redCount = isUnattributedRow ? 0 : this.snapshot.Count(project, SpecBoardStatus.Pending) + this.snapshot.Count(project, SpecBoardStatus.Unregistered);
            int revisionCount = isUnattributedRow ? 0 : this.snapshot.Count(project, SpecBoardStatus.NeedsRevision);
            int yellowCount = isUnattributedRow ? 0 : this.snapshot.Count(project, SpecBoardStatus.AwaitingVerify);
            int liveCount = ResolveRailLiveCount(model, i == 0, isUnattributedRow, project);
            string countText = redCount == 0 && revisionCount == 0 && yellowCount == 0
                ? "✓"
                : redCount.ToString(CultureInfo.InvariantCulture) + "/" + revisionCount.ToString(CultureInfo.InvariantCulture) + "/" + yellowCount.ToString(CultureInfo.InvariantCulture);
            string liveText = liveCount > 0 ? "▶" + liveCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
            bool fresh = !isUnattributedRow && !string.IsNullOrEmpty(project) && this.seenStateStore != null && this.seenStateStore.IsFresh(project, this.snapshot);
            using (SolidBrush labelBrush = new SolidBrush(selected ? palette.Text : palette.Muted))
            using (StringFormat labelFormat = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
            {
                float liveWidth = liveText.Length == 0 ? 0 : g.MeasureString(liveText, smallFont).Width + S(4);
                // The unattributed row has no ledger counts at all, so its "✓" would read as "this
                // project is clear" -- it must not be drawn.
                float countWidth = isUnattributedRow ? 0 : g.MeasureString(countText, bodyFont).Width + S(6);
                int freshWidth = fresh ? S(10) : 0;
                RectangleF labelRect = new RectangleF(row.Left + S(4) + freshWidth, row.Top, Math.Max(1, row.Width - countWidth - liveWidth - S(8) - freshWidth), row.Height);
                g.DrawString(display, bodyFont, labelBrush, labelRect, labelFormat);
                if (fresh)
                {
                    using (SolidBrush freshBrush = new SolidBrush(DesignTokens.Colors.Accent))
                    {
                        float diameter = S(6);
                        g.FillEllipse(freshBrush, row.Left + S(3), row.Top + (row.Height - diameter) / 2.0f, diameter, diameter);
                    }
                }

                if (!isUnattributedRow)
                {
                    DrawProjectCounts(g, new Rectangle(row.Left, row.Top, Math.Max(1, row.Width - (int)Math.Ceiling(liveWidth)), row.Height), bodyFont, redCount, revisionCount, yellowCount, palette);
                }

                if (liveText.Length > 0)
                {
                    using (SolidBrush liveBrush = new SolidBrush(palette.Success))
                    using (StringFormat far = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
                    {
                        g.DrawString(liveText, smallFont, liveBrush, new RectangleF(row.Left, row.Top, row.Width - 2, row.Height), far);
                    }
                }
            }

            if (recordHitTargets)
            {
                this.projectHitTargets.Add(new ProjectHitTarget { Bounds = row, Project = project });
            }

            y += rowHeight;
        }

        if (needsMore && y + rowHeight <= footer.Top)
        {
            int hidden = totalRows - rowsToDraw;
            using (SolidBrush muted = new SolidBrush(palette.Muted))
            using (StringFormat format = CreateStringFormat(StringAlignment.Near, StringTrimming.None))
            {
                g.DrawString("+" + hidden.ToString(CultureInfo.InvariantCulture), smallFont, muted, new Rectangle(bounds.Left + S(4), y, bounds.Width, rowHeight), format);
            }
        }

        DrawBoardFooter(g, footer, smallFont, palette, recordHitTargets);
    }

    // Rail live counts come from the composer's unfiltered project rows so the pills keep showing
    // every project's session count while one project is selected.
    private static int ResolveRailLiveCount(WorkBoardModel model, bool isAllRow, bool isUnattributedRow, string project)
    {
        if (model == null)
        {
            return 0;
        }

        int total = 0;
        for (int i = 0; i < model.ProjectRows.Count; i++)
        {
            WorkBoardProjectRow row = model.ProjectRows[i];
            if (isAllRow)
            {
                total += row.LiveCount;
                continue;
            }

            if (isUnattributedRow && row.IsUnattributed)
            {
                return row.LiveCount;
            }

            if (!isUnattributedRow && !row.IsUnattributed &&
                string.Equals(row.Name, project, StringComparison.OrdinalIgnoreCase))
            {
                return row.LiveCount;
            }
        }

        return isAllRow ? total : 0;
    }

    // Footer (管理/关闭 pills and the done/abandoned/warning stats) is shared by both layouts: the
    // wide board hosts it at the bottom of the project rail, the compact single-column board at the
    // bottom of the full-width action flow. It must stay reachable in every mode.
    private void DrawBoardFooter(Graphics g, Rectangle footer, Font smallFont, SpecBoardPalette palette, bool recordHitTargets)
    {
        string footerText = "✓" + this.snapshot.Count(string.Empty, SpecBoardStatus.Done).ToString(CultureInfo.InvariantCulture) + " · ×" + this.snapshot.Count(string.Empty, SpecBoardStatus.Abandoned).ToString(CultureInfo.InvariantCulture);
        int warnings = this.snapshot.MalformedLines + (this.snapshot.ProjectRegistryAvailable ? 0 : 1) + (this.snapshot.ReconciliationTimedOut ? 1 : 0);
        if (warnings > 0)
        {
            footerText += "  ⚠" + warnings.ToString(CultureInfo.InvariantCulture);
        }
        string viewLabel = IsTimelineView ? "卡片" : "时间线";
        int managerWidth = Math.Min(footer.Width, Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("管理", smallFont).Width) + S(14)));
        int viewWidth = Math.Min(footer.Width, Math.Max(S(46), (int)Math.Ceiling(g.MeasureString(viewLabel, smallFont).Width) + S(14)));
        int closeWidth = Math.Min(footer.Width, Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("关闭", smallFont).Width) + S(14)));
        Rectangle managerBounds = new Rectangle(footer.Left, footer.Top, managerWidth, footer.Height);
        Rectangle viewBounds = new Rectangle(managerBounds.Right + S(4), footer.Top, viewWidth, footer.Height);
        Rectangle closeBounds = new Rectangle(viewBounds.Right + S(4), footer.Top, closeWidth, footer.Height);
        Rectangle footerStats = new Rectangle(closeBounds.Right + S(5), footer.Top, Math.Max(1, footer.Right - closeBounds.Right - S(5)), footer.Height);
        using (GraphicsPath managerPath = RoundedRectangle(RectangleF.Inflate(managerBounds, -1, -1), S(4)))
        using (SolidBrush managerFill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen managerBorder = new Pen(DesignTokens.WithAlpha(palette.Success, 170), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush managerText = new SolidBrush(palette.Text))
        using (StringFormat centered = CreateStringFormat(StringAlignment.Center, StringTrimming.None))
        {
            centered.LineAlignment = StringAlignment.Center;
            g.FillPath(managerFill, managerPath);
            g.DrawPath(managerBorder, managerPath);
            g.DrawString("管理", smallFont, managerText, managerBounds, centered);
        }
        if (recordHitTargets)
        {
            this.managerButtonBounds = managerBounds;
        }

        using (GraphicsPath viewPath = RoundedRectangle(RectangleF.Inflate(viewBounds, -1, -1), S(4)))
        using (SolidBrush viewFill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen viewBorder = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 170), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush viewText = new SolidBrush(palette.Text))
        using (StringFormat centered = CreateStringFormat(StringAlignment.Center, StringTrimming.None))
        {
            centered.LineAlignment = StringAlignment.Center;
            g.FillPath(viewFill, viewPath);
            g.DrawPath(viewBorder, viewPath);
            g.DrawString(viewLabel, smallFont, viewText, viewBounds, centered);
        }

        if (recordHitTargets)
        {
            this.timelineButtonBounds = viewBounds;
        }

        using (GraphicsPath closePath = RoundedRectangle(RectangleF.Inflate(closeBounds, -1, -1), S(4)))
        using (SolidBrush closeFill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen closeBorder = new Pen(DesignTokens.WithAlpha(palette.Danger, 170), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush closeText = new SolidBrush(palette.Text))
        using (StringFormat centered = CreateStringFormat(StringAlignment.Center, StringTrimming.None))
        {
            centered.LineAlignment = StringAlignment.Center;
            g.FillPath(closeFill, closePath);
            g.DrawPath(closeBorder, closePath);
            g.DrawString("关闭", smallFont, closeText, closeBounds, centered);
        }
        if (recordHitTargets)
        {
            this.closeButtonBounds = closeBounds;
        }

        using (SolidBrush footerBrush = new SolidBrush(warnings > 0 ? palette.Unregistered : palette.Muted))
        using (StringFormat footerFormat = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(footerText, smallFont, footerBrush, footerStats, footerFormat);
        }
    }

    public void ShowManagerWindow()
    {
        ResetAutoHideClock();
        if (this.managerForm == null || this.managerForm.IsDisposed)
        {
            this.managerForm = new SpecBoardManagerForm(this.CurrentSettings);
            this.managerForm.FormClosed += delegate { this.managerForm = null; };
        }

        this.managerForm.ActivateOrShow();
    }

    private static void DrawProjectCounts(Graphics g, Rectangle row, Font font, int redCount, int revisionCount, int yellowCount, SpecBoardPalette palette)
    {
        if (redCount == 0 && revisionCount == 0 && yellowCount == 0)
        {
            using (SolidBrush success = new SolidBrush(palette.Success))
            using (StringFormat right = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
            {
                g.DrawString("✓", font, success, new RectangleF(row.Left, row.Top, row.Width - 2, row.Height), right);
            }

            return;
        }

        string[] parts =
        {
            redCount.ToString(CultureInfo.InvariantCulture),
            "/",
            revisionCount.ToString(CultureInfo.InvariantCulture),
            "/",
            yellowCount.ToString(CultureInfo.InvariantCulture)
        };
        Color[] colors = { palette.Danger, palette.Muted, palette.Revision, palette.Muted, palette.Warning };
        float x = row.Right - 2;
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            float width = g.MeasureString(parts[i], font, int.MaxValue, StringFormat.GenericTypographic).Width;
            x -= width;
            using (SolidBrush brush = new SolidBrush(colors[i]))
            using (StringFormat format = CreateStringFormat(StringAlignment.Near, StringTrimming.None))
            {
                g.DrawString(parts[i], font, brush, new RectangleF(x, row.Top, width + 1, row.Height), format);
            }
        }
    }

    // Merged five-section flow: the live Codex band sits on top of the four ledger sections.
    //
    // The default board is 648x400 logical. Title bar and footer take ~66, leaving ~334 for this
    // area, while five sections at "header + one full card" need ~340 -- so the pre-merge contract
    // "every section keeps one complete card" cannot hold universally any more. It is replaced by
    // the ladder in ComputeSectionPlan: empty sections collapse to a bare header, the remaining
    // budget is handed out in priority order, and sections that miss out degrade to a counted header
    // line. Section headers are never dropped, so the board's five-stage shape and every count stay
    // visible at any height.
    private void DrawWorkFlow(Graphics g, Rectangle bounds, WorkBoardModel model, bool compact, int segmentHeight, int cardHeight, int cardGap, Font titleFont, Font smallFont, Font segmentFont, SpecBoardPalette palette, bool recordHitTargets)
    {
        if (this.snapshot.LedgerMissing && model.LiveCount == 0)
        {
            DrawCenteredEmptyState(g, bounds, "账本未找到", this.snapshot.LedgerPath, palette.Danger, palette.Muted, titleFont, smallFont);
            return;
        }

        if (IsTimelineView)
        {
            // Timeline swaps only the flow column; the project rail keeps filtering both halves.
            DrawTimeline(g, bounds, model, titleFont, smallFont, segmentFont, palette);
            return;
        }

        int actionable = model.LiveCount;
        for (int i = 0; i < model.SpecSections.Count; i++)
        {
            actionable += model.SpecSections[i].Count;
        }

        if (actionable == 0)
        {
            DrawCenteredEmptyState(g, bounds, "没有待办 spec ✓", string.Empty, palette.Success, palette.Muted, titleFont, smallFont);
            return;
        }

        int liveCardHeight = cardHeight + MeasureLineHeight(g, smallFont, S(1));
        SectionPlan[] plan = ComputeSectionPlan(
            model, bounds.Height, segmentHeight, cardHeight, liveCardHeight, cardGap, compact);

        int y = bounds.Top;
        for (int i = 0; i < plan.Length; i++)
        {
            SectionPlan entry = plan[i];
            if (y + segmentHeight > bounds.Bottom)
            {
                break;
            }

            int hidden = entry.TotalCount - entry.CardCount;
            DrawSectionHeader(g, bounds, y, segmentHeight, entry.Label, entry.TotalCount, hidden, entry.Color, segmentFont, smallFont, palette);
            y += segmentHeight;

            for (int c = 0; c < entry.CardCount; c++)
            {
                int height = entry.IsLive ? liveCardHeight : cardHeight;
                if (y + height > bounds.Bottom)
                {
                    break;
                }

                Rectangle card = new Rectangle(bounds.Left, y, bounds.Width, height);
                if (entry.IsLive)
                {
                    DrawLiveCard(g, card, model.LiveRows[c], titleFont, smallFont, palette);
                }
                else
                {
                    DrawCard(g, card, entry.Section.Rows[c], entry.Color, titleFont, smallFont, palette);
                    if (recordHitTargets)
                    {
                        this.cardHitTargets.Add(new CardHitTarget { Bounds = card, Row = entry.Section.Rows[c] });
                    }
                }

                y += height + cardGap;
            }
        }
    }

    // Session activity lanes, carried over from the retired Codex task board. History itself is
    // accumulated by CodexRadarForm, so the lanes are populated even if this board was collapsed the
    // whole time.
    private void DrawTimeline(Graphics g, Rectangle bounds, WorkBoardModel model, Font titleFont, Font smallFont, Font segmentFont, SpecBoardPalette palette)
    {
        int windowMinutes = this.CurrentSettings == null
            ? WidgetSettings.DefaultWorkBoardTimelineMinutes
            : this.CurrentSettings.WorkBoardTimelineMinutes;
        CodexTaskTimelineModel timeline = CodexTaskPresentation.BuildTimeline(this.taskSnapshot, DateTime.Now, windowMinutes, WorkBoardLimits.Default.MaxLiveRows);
        int segmentHeight = MeasureLineHeight(g, segmentFont, S(6));
        int y = bounds.Top;
        using (SolidBrush header = new SolidBrush(palette.Success))
        using (StringFormat near = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(
                "▶ 时间线 · " + windowMinutes.ToString(CultureInfo.InvariantCulture) + " 分",
                segmentFont, header, new Rectangle(bounds.Left, y, bounds.Width, segmentHeight), near);
        }

        y += segmentHeight + S(4);
        if (timeline == null || !timeline.HasLanes)
        {
            DrawCenteredEmptyState(g, new Rectangle(bounds.Left, y, bounds.Width, Math.Max(1, bounds.Bottom - y)),
                "正在积累活动历史…", string.Empty, palette.Muted, palette.Muted, titleFont, smallFont);
            return;
        }

        int nameWidth = Math.Max(S(70), (int)Math.Round(bounds.Width * 0.26));
        int statusWidth = Math.Max(S(42), (int)Math.Round(bounds.Width * 0.16));
        int laneHeight = MeasureLineHeight(g, smallFont, S(7));
        int barHeight = Math.Max(2, laneHeight - S(5));
        double totalTicks = Math.Max(1.0, (timeline.EndLocal - timeline.StartLocal).TotalMilliseconds);
        using (SolidBrush nameBrush = new SolidBrush(palette.Text))
        using (SolidBrush track = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 150)))
        using (StringFormat far = CreateStringFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        using (StringFormat near = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            for (int i = 0; i < timeline.Lanes.Count; i++)
            {
                if (y + laneHeight > bounds.Bottom)
                {
                    break;
                }

                CodexTaskTimelineLane lane = timeline.Lanes[i];
                g.DrawString(
                    "#" + lane.TaskNumber.ToString(CultureInfo.InvariantCulture) + " " + lane.WorkspaceLeaf,
                    smallFont, nameBrush, new RectangleF(bounds.Left, y, nameWidth, laneHeight), far);

                int barLeft = bounds.Left + nameWidth + S(6);
                int barRight = bounds.Right - statusWidth - S(6);
                Rectangle bar = new Rectangle(barLeft, y + (laneHeight - barHeight) / 2, Math.Max(1, barRight - barLeft), barHeight);
                g.FillRectangle(track, bar);
                for (int sIndex = 0; sIndex < lane.Segments.Count; sIndex++)
                {
                    CodexTaskTimelineSegment segment = lane.Segments[sIndex];
                    double from = (segment.StartLocal - timeline.StartLocal).TotalMilliseconds / totalTicks;
                    double to = (segment.EndLocal - timeline.StartLocal).TotalMilliseconds / totalTicks;
                    int left = bar.Left + (int)Math.Round(Math.Max(0.0, Math.Min(1.0, from)) * bar.Width);
                    int right = bar.Left + (int)Math.Round(Math.Max(0.0, Math.Min(1.0, to)) * bar.Width);
                    if (right <= left)
                    {
                        continue;
                    }

                    using (SolidBrush fill = new SolidBrush(CodexTaskPresentation.GetStatusColor(segment.Status)))
                    {
                        g.FillRectangle(fill, new Rectangle(left, bar.Top, right - left, bar.Height));
                    }
                }

                using (SolidBrush statusBrush = new SolidBrush(lane.StatusColor))
                {
                    g.DrawString(lane.StatusText, smallFont, statusBrush,
                        new RectangleF(bounds.Right - statusWidth, y, statusWidth, laneHeight), near);
                }

                y += laneHeight;
            }
        }
    }

    internal struct SectionPlan
    {
        public string Label;
        public Color Color;
        public bool IsLive;
        public WorkBoardSection Section;
        public int TotalCount;
        public int CardCount;
    }

    // Pure height arithmetic so the ladder can be self-tested without a device context.
    internal static SectionPlan[] ComputeSectionPlan(
        WorkBoardModel model,
        int availableHeight,
        int segmentHeight,
        int cardHeight,
        int liveCardHeight,
        int cardGap,
        bool compact)
    {
        List<SectionPlan> plan = new List<SectionPlan>();
        plan.Add(new SectionPlan
        {
            Label = "▶ 进行中",
            Color = DesignTokens.Colors.Success,
            IsLive = true,
            Section = null,
            TotalCount = model.LiveCount,
            CardCount = 0
        });
        for (int i = 0; i < model.SpecSections.Count; i++)
        {
            WorkBoardSection section = model.SpecSections[i];
            plan.Add(new SectionPlan
            {
                Label = "◆ " + section.Label,
                Color = section.Color,
                IsLive = false,
                Section = section,
                TotalCount = section.Count,
                CardCount = 0
            });
        }

        // Step 1: every section always costs one header line; empty ones cost nothing more.
        int budget = availableHeight - plan.Count * segmentHeight;

        // Steps 2-3: hand out complete cards in fixed priority order (live band first), one per
        // section per pass, so no single busy section starves the ones below it.
        int perSectionCap = compact ? 1 : 3;
        bool progressed = true;
        while (budget > 0 && progressed)
        {
            progressed = false;
            for (int i = 0; i < plan.Count; i++)
            {
                SectionPlan entry = plan[i];
                if (entry.CardCount >= entry.TotalCount || entry.CardCount >= perSectionCap)
                {
                    continue;
                }

                int cost = (entry.IsLive ? liveCardHeight : cardHeight) + cardGap;
                if (cost > budget)
                {
                    continue;
                }

                budget -= cost;
                entry.CardCount++;
                plan[i] = entry;
                progressed = true;
            }
        }

        return plan.ToArray();
    }

    private void DrawSectionHeader(Graphics g, Rectangle bounds, int y, int segmentHeight, string label, int total, int hidden, Color statusColor, Font segmentFont, Font smallFont, SpecBoardPalette palette)
    {
        using (SolidBrush brush = new SolidBrush(total == 0 ? palette.Muted : statusColor))
        using (StringFormat format = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(label + " · " + total.ToString(CultureInfo.InvariantCulture), segmentFont, brush, new Rectangle(bounds.Left, y, bounds.Width, segmentHeight), format);
        }

        if (hidden > 0)
        {
            using (SolidBrush muted = new SolidBrush(palette.Muted))
            using (StringFormat right = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
            {
                g.DrawString("+" + hidden.ToString(CultureInfo.InvariantCulture), smallFont, muted, new Rectangle(bounds.Left, y, bounds.Width, segmentHeight), right);
            }
        }
    }

    // Slimmed live session card. The water ring and the four-part token line stay in the Codex tile
    // expand panel; here the context level is a thin bar so the card fits the shared shell.
    private void DrawLiveCard(Graphics g, Rectangle bounds, WorkBoardLiveRow live, Font titleFont, Font smallFont, SpecBoardPalette palette)
    {
        CodexTaskRowModel task = live.Task;
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -0.5f, -0.5f), S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 235)))
        {
            g.FillPath(fill, path);
        }

        using (SolidBrush stripe = new SolidBrush(task.StatusColor))
        {
            g.FillRectangle(stripe, bounds.Left, bounds.Top + S(2), Math.Max(1, S(2)), Math.Max(1, bounds.Height - S(4)));
        }

        int inset = S(7);
        int titleHeight = MeasureLineHeight(g, titleFont, S(1));
        int lineHeight = MeasureLineHeight(g, smallFont, S(1));
        string head = "#" + task.TaskNumber.ToString(CultureInfo.InvariantCulture) + " " + task.WorkspaceLeaf;
        string percent = Math.Round(task.ContextPercent).ToString("0", CultureInfo.InvariantCulture) + "%";
        float percentWidth = g.MeasureString(percent, smallFont).Width + S(4);
        string orphan = live.IsUnattributed ? "未归属" : string.Empty;
        float orphanWidth = orphan.Length == 0 ? 0 : g.MeasureString(orphan, smallFont).Width + S(6);

        using (SolidBrush textBrush = new SolidBrush(palette.Text))
        using (SolidBrush mutedBrush = new SolidBrush(palette.Muted))
        using (SolidBrush statusBrush = new SolidBrush(task.StatusColor))
        using (StringFormat left = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat right = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
        {
            RectangleF headRect = new RectangleF(bounds.Left + inset, bounds.Top + S(3), Math.Max(1, bounds.Width - inset * 2 - percentWidth - orphanWidth), titleHeight);
            g.DrawString(head, titleFont, textBrush, headRect, left);
            if (orphan.Length > 0)
            {
                g.DrawString(orphan, smallFont, mutedBrush, new RectangleF(bounds.Right - inset - percentWidth - orphanWidth, headRect.Top, orphanWidth, titleHeight), right);
            }

            using (SolidBrush percentBrush = new SolidBrush(task.ContextBarColor))
            {
                g.DrawString(percent, smallFont, percentBrush, new RectangleF(bounds.Right - inset - percentWidth, headRect.Top, percentWidth, titleHeight), right);
            }

            // The "≈" prefix is mandatory: the hint is a textual guess, never an assertion that this
            // session is executing that spec.
            string hint = live.HasSpecHint
                ? "≈ " + live.SpecHintTitle + (live.SpecHintOverflow > 0 ? " +" + live.SpecHintOverflow.ToString(CultureInfo.InvariantCulture) : string.Empty)
                : string.Empty;
            float hintWidth = hint.Length == 0 ? 0 : Math.Min(bounds.Width * 0.42f, g.MeasureString(hint, smallFont).Width + S(6));
            RectangleF titleRect = new RectangleF(bounds.Left + inset, headRect.Bottom, Math.Max(1, bounds.Width - inset * 2 - hintWidth), lineHeight);
            g.DrawString(task.Title, smallFont, mutedBrush, titleRect, left);
            if (hint.Length > 0)
            {
                using (SolidBrush hintBrush = new SolidBrush(DesignTokens.WithAlpha(palette.Success, 190)))
                {
                    g.DrawString(hint, smallFont, hintBrush, new RectangleF(bounds.Right - inset - hintWidth, titleRect.Top, hintWidth, lineHeight), right);
                }
            }

            // Status / age / model on the left, context bar pinned right.
            float barWidth = Math.Min(S(60), Math.Max(S(24), bounds.Width * 0.24f));
            RectangleF metaRect = new RectangleF(bounds.Left + inset, titleRect.Bottom, Math.Max(1, bounds.Width - inset * 2 - barWidth - S(6)), lineHeight);
            g.DrawString(task.StatusText, smallFont, statusBrush, metaRect, left);
            float statusWidth = g.MeasureString(task.StatusText, smallFont).Width + S(4);
            string tail = task.DetailText;
            if (!string.IsNullOrEmpty(task.Model))
            {
                tail += " · " + task.Model;
            }

            g.DrawString(tail, smallFont, mutedBrush, new RectangleF(metaRect.Left + statusWidth, metaRect.Top, Math.Max(1, metaRect.Width - statusWidth), lineHeight), left);

            float barHeight = Math.Max(1, S(4));
            float barTop = metaRect.Top + (lineHeight - barHeight) / 2.0f;
            RectangleF barRect = new RectangleF(bounds.Right - inset - barWidth, barTop, barWidth, barHeight);
            using (SolidBrush track = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 150)))
            using (SolidBrush level = new SolidBrush(task.ContextBarColor))
            {
                g.FillRectangle(track, barRect);
                float filled = (float)(Math.Max(0.0, Math.Min(100.0, task.ContextPercent)) / 100.0) * barRect.Width;
                if (filled > 0)
                {
                    g.FillRectangle(level, new RectangleF(barRect.Left, barRect.Top, filled, barRect.Height));
                }
            }
        }
    }

    private void DrawCard(Graphics g, Rectangle bounds, SpecBoardRow row, Color statusColor, Font titleFont, Font smallFont, SpecBoardPalette palette)
    {
        Color cardText = row.FileMissing ? palette.Muted : palette.Text;
        bool highlighted = IsAutoPopupHighlighted(row, DateTime.UtcNow);
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -0.5f, -0.5f), S(5)))
        using (SolidBrush fill = new SolidBrush(highlighted
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 82)
            : DesignTokens.WithAlpha(DesignTokens.Colors.Surface, row.FileMissing ? 120 : 220)))
        {
            g.FillPath(fill, path);
            if (highlighted)
            {
                using (Pen highlightBorder = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Accent, 238), Math.Max(1.0f, this.LayerScale * 1.5f)))
                {
                    g.DrawPath(highlightBorder, path);
                }
            }
        }

        using (SolidBrush stripe = new SolidBrush(row.FileMissing ? palette.Muted : statusColor))
        {
            g.FillRectangle(stripe, bounds.Left, bounds.Top + S(2), Math.Max(1, S(2)), Math.Max(1, bounds.Height - S(4)));
        }

        int inset = S(7);
        int titleHeight = MeasureLineHeight(g, titleFont, S(1));
        string age = row.FileMissing ? "文件丢失" : FormatRelativeAge(row.EventTimeUtc, DateTime.UtcNow);
        float ageWidth = g.MeasureString(age, smallFont).Width + S(4);
        RectangleF titleRect = new RectangleF(bounds.Left + inset, bounds.Top + S(3), Math.Max(1, bounds.Width - inset * 2 - ageWidth), titleHeight);
        RectangleF ageRect = new RectangleF(bounds.Right - inset - ageWidth, titleRect.Top, ageWidth, titleHeight);
        string eventLabel = GetEventLabel(row);
        if (row.FileMissing)
        {
            eventLabel += " · 文件丢失";
        }

        RectangleF subtitleRect = new RectangleF(bounds.Left + inset, titleRect.Bottom, Math.Max(1, bounds.Width - inset * 2), Math.Max(1, bounds.Bottom - titleRect.Bottom - S(2)));
        string projectLabel = FitProjectLabel(g, smallFont, row.Project, eventLabel, subtitleRect.Width);
        using (SolidBrush titleBrush = new SolidBrush(cardText))
        using (SolidBrush mutedBrush = new SolidBrush(palette.Muted))
        using (StringFormat left = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat right = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
        {
            g.DrawString(row.Title, titleFont, titleBrush, titleRect, left);
            g.DrawString(age, smallFont, mutedBrush, ageRect, right);
            g.DrawString(projectLabel + " · " + eventLabel, smallFont, mutedBrush, subtitleRect, left);
        }
    }

    private bool IsAutoPopupHighlighted(SpecBoardRow row, DateTime nowUtc)
    {
        return nowUtc < this.autoPopupHighlightUntilUtc &&
            this.autoPopupHighlightedRows.Contains(GetAutoPopupRowKey(row));
    }

    private static void DrawCenteredEmptyState(Graphics g, Rectangle bounds, string title, string subtitle, Color titleColor, Color subtitleColor, Font titleFont, Font smallFont)
    {
        int titleHeight = MeasureLineHeight(g, titleFont, 2);
        int subtitleHeight = string.IsNullOrEmpty(subtitle) ? 0 : MeasureLineHeight(g, smallFont, 2) * 2;
        int top = bounds.Top + Math.Max(0, (bounds.Height - titleHeight - subtitleHeight) / 2);
        using (SolidBrush titleBrush = new SolidBrush(titleColor))
        using (SolidBrush subtitleBrush = new SolidBrush(subtitleColor))
        using (StringFormat centered = CreateStringFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(title, titleFont, titleBrush, new Rectangle(bounds.Left, top, bounds.Width, titleHeight), centered);
            if (!string.IsNullOrEmpty(subtitle))
            {
                centered.FormatFlags &= ~StringFormatFlags.NoWrap;
                g.DrawString(subtitle, smallFont, subtitleBrush, new Rectangle(bounds.Left + 4, top + titleHeight, Math.Max(1, bounds.Width - 8), subtitleHeight), centered);
            }
        }
    }

    private void OnMaintenanceTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        if (!ShouldMonitorWork())
        {
            SuspendVisibleWork();
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool renderNeeded = ExpireCopySuccessNotice(now);
        // The live band folds the retired task board's 2 s cadence into this existing tick rather
        // than adding a second timer.
        if (this.Visible && RefreshTaskSampleIfDue(now, false))
        {
            renderNeeded = true;
        }
        if (this.autoPopupHighlightUntilUtc != DateTime.MinValue && now >= this.autoPopupHighlightUntilUtc)
        {
            this.autoPopupHighlightUntilUtc = DateTime.MinValue;
            this.autoPopupHighlightedRows.Clear();
            renderNeeded = true;
        }

        if (this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible)
        {
            this.dockTab.RefreshBurnInPosition();
        }

        if (UpdateOutsideClickDismissal(now) || UpdateDockCollapse(now))
        {
            return;
        }

        if (this.Visible)
        {
            if (renderNeeded)
            {
                RenderLayeredWindow();
            }

            bool inside = this.Bounds.Contains(this.cursorPositionProvider());
            if (inside)
            {
                this.mouseWasInside = true;
                if (this.autoPopupActive)
                {
                    // Hovering pauses auto-close and restarts the full dwell on every tick, so the
                    // countdown begins only after the pointer actually leaves the window.
                    this.autoPopupHideUtc = now.AddSeconds(this.CurrentSettings.SpecBoardAutoPopupSeconds);
                }
            }
            else if (this.mouseWasInside)
            {
                this.mouseWasInside = false;
                ResetAutoHideClock();
            }

            if (this.autoPopupActive)
            {
                if (!inside && this.autoPopupHideUtc != DateTime.MinValue && now >= this.autoPopupHideUtc)
                {
                    HideBoard();
                    return;
                }
            }
            else
            {
                int autoHideSeconds = this.CurrentSettings.SpecBoardAutoHideSeconds;
                if (autoHideSeconds > 0 && !inside && now >= this.lastInteractionUtc.AddSeconds(autoHideSeconds))
                {
                    HideBoard();
                    return;
                }
            }
        }

        if (Interlocked.Exchange(ref this.watcherSignal, 0) != 0)
        {
            this.watcherDebounceUntilUtc = now.AddMilliseconds(MaintenanceIntervalMs);
        }

        if (this.watcherDebounceUntilUtc != DateTime.MinValue && now >= this.watcherDebounceUntilUtc)
        {
            this.watcherDebounceUntilUtc = DateTime.MinValue;
            // A watcher signal can originate from the central ledger or a project Spec directory.
            // Reconcile both sources so an unregistered new file is visible immediately.
            RequestRefresh(true);
        }

        if (now >= this.nextPollUtc)
        {
            this.nextPollUtc = now.AddSeconds(PollFallbackSeconds);
            RequestRefresh(false);
        }

        if (now >= this.nextReconcileUtc)
        {
            this.nextReconcileUtc = now.AddMinutes(ReconcileIntervalMinutes);
            RequestRefresh(true);
        }

        if (this.Visible && ShouldRefreshBurnInPosition())
        {
            PositionForDisplay();
        }
    }

    private void RequestRefresh(bool reconcile)
    {
        if (Interlocked.CompareExchange(ref this.refreshRunning, 1, 0) != 0)
        {
            QueueRefresh(reconcile);
            return;
        }

        string ledgerPath = this.CurrentSettings.SpecBoardLedgerPath;
        long generation = Interlocked.Increment(ref this.refreshGeneration);
        CancellationTokenSource cancellation = new CancellationTokenSource();
        CancellationToken refreshToken = cancellation.Token;
        lock (this.refreshCancellationSync)
        {
            this.refreshCancellation = cancellation;
        }

        Task.Run(
            () => ReadRefreshSnapshot(ledgerPath, reconcile, refreshToken),
            refreshToken).ContinueWith(task =>
        {
            try
            {
                SpecBoardSnapshot result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (result != null && ShouldApplyRefreshResult(
                    generation,
                    Interlocked.Read(ref this.refreshGeneration),
                    refreshToken.IsCancellationRequested) &&
                    !this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((Action)delegate
                    {
                        if (ShouldApplyRefreshResult(
                            generation,
                            Interlocked.Read(ref this.refreshGeneration),
                            refreshToken.IsCancellationRequested) &&
                            !this.IsDisposed)
                        {
                            ApplyRefreshResult(result);
                        }
                    });
                }
                else if (task.IsFaulted && task.Exception != null)
                {
                    Program.LogInfo("SpecBoard refresh failed: " + task.Exception.GetBaseException().Message);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                lock (this.refreshCancellationSync)
                {
                    if (object.ReferenceEquals(this.refreshCancellation, cancellation))
                    {
                        this.refreshCancellation = null;
                    }
                }

                cancellation.Dispose();
                Interlocked.Exchange(ref this.refreshRunning, 0);
                int queued = Interlocked.Exchange(ref this.refreshQueued, 0);
                if (queued != 0 && !this.IsDisposed)
                {
                    try
                    {
                        if (this.IsHandleCreated)
                        {
                            this.BeginInvoke((Action)(() =>
                            {
                                if (!this.IsDisposed && ShouldMonitorWork())
                                {
                                    RequestRefresh(queued == 2);
                                }
                            }));
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        });
    }

    private static SpecBoardSnapshot ReadRefreshSnapshot(
        string ledgerPath,
        bool reconcile,
        CancellationToken cancellationToken)
    {
        SpecBoardSnapshot basic = SpecBoardReader.Read(ledgerPath, false, cancellationToken);
        if (!reconcile || basic.LedgerMissing || !basic.ProjectRegistryAvailable)
        {
            return basic;
        }

        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ReconcileTimeoutMs);
            try
            {
                return SpecBoardReader.Read(ledgerPath, true, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                basic.ReconciliationTimedOut = true;
                return basic;
            }
        }
    }

    private void QueueRefresh(bool reconcile)
    {
        int desired = reconcile ? 2 : 1;
        while (true)
        {
            int current = Volatile.Read(ref this.refreshQueued);
            if (current >= desired || Interlocked.CompareExchange(ref this.refreshQueued, desired, current) == current)
            {
                return;
            }
        }
    }

    private void CancelRefresh()
    {
        Interlocked.Increment(ref this.refreshGeneration);
        CancellationTokenSource cancellation;
        lock (this.refreshCancellationSync)
        {
            cancellation = this.refreshCancellation;
        }

        if (cancellation != null)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private static bool ShouldApplyRefreshResult(long resultGeneration, long currentGeneration, bool canceled)
    {
        return !canceled && resultGeneration == currentGeneration;
    }

    private void ApplyRefreshResult(SpecBoardSnapshot result)
    {
        this.snapshot = result ?? new SpecBoardSnapshot();
        EnsureSeenStateInitialized(this.snapshot);
        RefreshProjectWatchers(this.snapshot);

        List<string> newRows = UpdateAutoPopupBaseline(this.snapshot);
        if (newRows.Count > 0 && this.CurrentSettings.SpecBoardAutoPopupEnabled &&
            !this.displaySuspended && !this.hiddenForFullscreen)
        {
            this.autoPopupHighlightedRows.Clear();
            for (int i = 0; i < newRows.Count; i++)
            {
                this.autoPopupHighlightedRows.Add(newRows[i]);
            }

            this.autoPopupHighlightUntilUtc = DateTime.UtcNow.AddSeconds(this.CurrentSettings.SpecBoardAutoPopupSeconds);
            if (this.Visible)
            {
                ResetAutoHideClock();
                RenderLayeredWindow();
            }
            else
            {
                ShowBoardCore(true);
            }
        }
        else if (this.Visible)
        {
            RenderLayeredWindow();
        }
    }

    private List<string> UpdateAutoPopupBaseline(SpecBoardSnapshot currentSnapshot)
    {
        List<string> discovered = new List<string>();
        if (currentSnapshot == null)
        {
            return discovered;
        }

        List<SpecBoardRow> rows = currentSnapshot.Rows ?? new List<SpecBoardRow>();
        if (!this.autoPopupBaselineInitialized)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                this.autoPopupKnownRows.Add(GetAutoPopupRowKey(rows[i]));
            }

            this.autoPopupBaselineInitialized = true;
            return discovered;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            SpecBoardRow row = rows[i];
            string key = GetAutoPopupRowKey(row);
            bool firstSeen = this.autoPopupKnownRows.Add(key);
            if (firstSeen && IsAutoPopupActionable(row))
            {
                discovered.Add(key);
            }
        }

        return discovered;
    }

    private static bool IsAutoPopupActionable(SpecBoardRow row)
    {
        return row != null && !row.FileMissing &&
            (row.Status == SpecBoardStatus.Unregistered ||
             row.Status == SpecBoardStatus.Pending ||
             row.Status == SpecBoardStatus.NeedsRevision ||
             row.Status == SpecBoardStatus.AwaitingVerify);
    }

    private static string GetAutoPopupRowKey(SpecBoardRow row)
    {
        if (row == null)
        {
            return string.Empty;
        }

        string path = string.IsNullOrWhiteSpace(row.SpecPath) ? row.AbsolutePath : row.SpecPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = row.Id;
        }

        return ((row.Project ?? string.Empty).Trim() + "|" + (path ?? string.Empty).Trim().Replace('\\', '/')).ToLowerInvariant();
    }

    private void EnsureSeenStateInitialized(SpecBoardSnapshot currentSnapshot)
    {
        if (this.seenStateInitialized || this.seenStateStore == null || currentSnapshot == null)
        {
            return;
        }

        this.seenStateStore.LoadOrSeed(currentSnapshot);
        this.seenStateInitialized = true;
    }

    private void ResumeVisibleWork()
    {
        if (!ShouldMonitorWork())
        {
            SuspendVisibleWork();
            return;
        }

        EnsureWatcher();
        if (this.watcher != null)
        {
            this.watcher.EnableRaisingEvents = true;
        }

        RefreshProjectWatchers(this.snapshot);
        SetProjectWatchersEnabled(true);

        if (this.nextPollUtc == DateTime.MinValue)
        {
            this.nextPollUtc = DateTime.UtcNow.AddSeconds(PollFallbackSeconds);
        }

        if (this.nextReconcileUtc == DateTime.MinValue)
        {
            this.nextReconcileUtc = DateTime.UtcNow.AddMinutes(ReconcileIntervalMinutes);
        }

        this.maintenanceTimer.Start();
    }

    private void UpdateMonitoringState()
    {
        if (ShouldMonitorWork())
        {
            ResumeVisibleWork();
        }
        else
        {
            SuspendVisibleWork();
        }
    }

    private bool ShouldMonitorWork()
    {
        // A left-docked board must keep its maintenance tick even while collapsed: the tick is what
        // drives the tab's burn-in drift and the collapse countdown after a hover expand.
        return !this.displaySuspended && !this.hiddenForFullscreen &&
            (this.Visible || this.IsLeftDocked ||
                this.CurrentSettings != null && this.CurrentSettings.SpecBoardAutoPopupEnabled);
    }

    private void SuspendVisibleWork()
    {
        this.maintenanceTimer.Stop();
        CancelRefresh();
        CancelCardClick();
        if (this.watcher != null)
        {
            this.watcher.EnableRaisingEvents = false;
        }

        SetProjectWatchersEnabled(false);

        Interlocked.Exchange(ref this.watcherSignal, 0);
        this.watcherDebounceUntilUtc = DateTime.MinValue;
    }

    private void EnsureWatcher()
    {
        if (this.watcher != null)
        {
            return;
        }

        try
        {
            string directory = Path.GetDirectoryName(this.CurrentSettings.SpecBoardLedgerPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            this.watcher = new FileSystemWatcher(directory);
            this.watcher.Filter = "*.*";
            this.watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            this.watcher.Changed += OnWatchedFileChanged;
            this.watcher.Created += OnWatchedFileChanged;
            this.watcher.Deleted += OnWatchedFileChanged;
            this.watcher.Renamed += OnWatchedFileRenamed;
            this.watcher.Error += OnWatcherError;
        }
        catch
        {
            DisposeWatcher();
        }
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsWatchedFile(e == null ? string.Empty : e.Name) && ShouldMonitorWork())
        {
            Interlocked.Exchange(ref this.watcherSignal, 1);
        }
    }

    private void OnWatchedFileRenamed(object sender, RenamedEventArgs e)
    {
        if ((IsWatchedFile(e == null ? string.Empty : e.Name) || IsWatchedFile(e == null ? string.Empty : e.OldName)) && ShouldMonitorWork())
        {
            Interlocked.Exchange(ref this.watcherSignal, 1);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (this.watcher != null)
        {
            this.watcher.EnableRaisingEvents = false;
        }
    }

    private bool IsWatchedFile(string name)
    {
        return string.Equals(name, Path.GetFileName(this.CurrentSettings.SpecBoardLedgerPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "PROJECTS.json", StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeWatcher()
    {
        if (this.watcher == null)
        {
            return;
        }

        this.watcher.EnableRaisingEvents = false;
        this.watcher.Changed -= OnWatchedFileChanged;
        this.watcher.Created -= OnWatchedFileChanged;
        this.watcher.Deleted -= OnWatchedFileChanged;
        this.watcher.Renamed -= OnWatchedFileRenamed;
        this.watcher.Error -= OnWatcherError;
        this.watcher.Dispose();
        this.watcher = null;
    }

    private void RefreshProjectWatchers(SpecBoardSnapshot currentSnapshot)
    {
        List<ProjectWatcherSpec> specs = BuildProjectWatcherSpecs(currentSnapshot);
        string signature = string.Join("\n", specs.Select(spec => spec.Directory + "|" + spec.Filter + "|" + spec.IncludeSubdirectories).ToArray());
        if (string.Equals(signature, this.projectWatcherSignature, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeProjectWatchers();
        this.projectWatcherSignature = signature;
        for (int i = 0; i < specs.Count; i++)
        {
            ProjectWatcherSpec spec = specs[i];
            try
            {
                FileSystemWatcher projectWatcher = new FileSystemWatcher(spec.Directory);
                projectWatcher.Filter = spec.Filter;
                projectWatcher.IncludeSubdirectories = spec.IncludeSubdirectories;
                projectWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                projectWatcher.Created += OnProjectSpecChanged;
                projectWatcher.Changed += OnProjectSpecChanged;
                projectWatcher.Deleted += OnProjectSpecChanged;
                projectWatcher.Renamed += OnProjectSpecRenamed;
                projectWatcher.Error += OnProjectWatcherError;
                projectWatcher.EnableRaisingEvents = ShouldMonitorWork();
                this.projectWatchers.Add(projectWatcher);
            }
            catch
            {
                // The 60-second poll and five-minute reconciliation remain as fallbacks when a
                // project directory is unavailable or Windows cannot allocate a watcher buffer.
            }
        }
    }

    private static List<ProjectWatcherSpec> BuildProjectWatcherSpecs(SpecBoardSnapshot currentSnapshot)
    {
        List<ProjectWatcherSpec> specs = new List<ProjectWatcherSpec>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (currentSnapshot == null || currentSnapshot.Projects == null)
        {
            return specs;
        }

        for (int i = 0; i < currentSnapshot.Projects.Count; i++)
        {
            SpecBoardProject project = currentSnapshot.Projects[i];
            if (project == null || string.IsNullOrWhiteSpace(project.Root) || string.IsNullOrWhiteSpace(project.SpecGlob))
            {
                continue;
            }

            try
            {
                string relativeGlob = project.SpecGlob.Replace('/', Path.DirectorySeparatorChar);
                string relativeDirectory = Path.GetDirectoryName(relativeGlob) ?? string.Empty;
                bool wildcardDirectory = relativeDirectory.IndexOf('*') >= 0 || relativeDirectory.IndexOf('?') >= 0;
                string directory = wildcardDirectory
                    ? Path.GetFullPath(project.Root)
                    : Path.GetFullPath(Path.Combine(project.Root, relativeDirectory));
                string filter = wildcardDirectory ? "*.md" : Path.GetFileName(relativeGlob);
                if (string.IsNullOrWhiteSpace(filter) || filter.IndexOf("**", StringComparison.Ordinal) >= 0)
                {
                    filter = "*.md";
                }

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                bool includeSubdirectories = wildcardDirectory;
                string key = directory + "|" + filter + "|" + includeSubdirectories;
                if (seen.Add(key))
                {
                    specs.Add(new ProjectWatcherSpec
                    {
                        Directory = directory,
                        Filter = filter,
                        IncludeSubdirectories = includeSubdirectories
                    });
                }
            }
            catch
            {
            }
        }

        return specs.OrderBy(spec => spec.Directory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(spec => spec.Filter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnProjectSpecChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldMonitorWork())
        {
            Interlocked.Exchange(ref this.watcherSignal, 1);
        }
    }

    private void OnProjectSpecRenamed(object sender, RenamedEventArgs e)
    {
        OnProjectSpecChanged(sender, e);
    }

    private void OnProjectWatcherError(object sender, ErrorEventArgs e)
    {
        FileSystemWatcher projectWatcher = sender as FileSystemWatcher;
        if (projectWatcher != null)
        {
            projectWatcher.EnableRaisingEvents = false;
        }
    }

    private void SetProjectWatchersEnabled(bool enabled)
    {
        for (int i = 0; i < this.projectWatchers.Count; i++)
        {
            try
            {
                this.projectWatchers[i].EnableRaisingEvents = enabled;
            }
            catch
            {
            }
        }
    }

    private void DisposeProjectWatchers()
    {
        for (int i = 0; i < this.projectWatchers.Count; i++)
        {
            FileSystemWatcher projectWatcher = this.projectWatchers[i];
            projectWatcher.EnableRaisingEvents = false;
            projectWatcher.Created -= OnProjectSpecChanged;
            projectWatcher.Changed -= OnProjectSpecChanged;
            projectWatcher.Deleted -= OnProjectSpecChanged;
            projectWatcher.Renamed -= OnProjectSpecRenamed;
            projectWatcher.Error -= OnProjectWatcherError;
            projectWatcher.Dispose();
        }

        this.projectWatchers.Clear();
        this.projectWatcherSignature = string.Empty;
    }

    private bool IsLeftDocked
    {
        get { return this.owner != null; }
    }

    // Resolved screen Y of this board's dock tab center. The auto sentinel puts the Spec tab just
    // above the work area's vertical middle; the Codex task board takes the slot just below, so the
    // two 30px tabs sit adjacent without overlapping.
    private int ResolveDockTabCenterY()
    {
        return LeftDockLayout.ResolveTabCenterY(
            this.CurrentSettings,
            EdgeDockTabRole.SpecBoard,
            this.LayerScale);
    }

    private void PositionForDisplay()
    {
        if (this.IsLeftDocked)
        {
            PositionAtLeftDock();
            return;
        }

        PositionNearOperationPanel();
    }

    // Docked: flush against the left edge but offset by the tab width so the tab stays visible and
    // the pointer can travel tab -> board without crossing a gap (which would collapse the board).
    private void PositionAtLeftDock()
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
        Point baseLocation = LeftDockLayout.ResolveBoardBaseLocation(
            this.CurrentSettings,
            EdgeDockTabRole.SpecBoard,
            this.LayerScale,
            this.Size);
        this.Location = ApplySelfTestOffscreenOffset(BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
            this.Size,
            workArea,
            BurnInProtection.SpecBoardSalt));
    }

    private void PositionNearOperationPanel()
    {
        if (this.owner == null || this.CurrentSettings == null)
        {
            return;
        }

        Rectangle workArea = Screen.FromControl(this.owner).WorkingArea;
        int left = this.CurrentSettings.SpecBoardLeftX >= 0 ? this.CurrentSettings.SpecBoardLeftX : this.owner.Left;
        int bottom = this.CurrentSettings.SpecBoardBottomY >= 0 ? this.CurrentSettings.SpecBoardBottomY : this.owner.Top - S(10);
        int top = bottom - this.Height;
        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - this.Width)));
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Height)));
        Point baseLocation = new Point(left, top);
        this.Location = ApplySelfTestOffscreenOffset(BurnInProtection.ApplyRuntimeOffset(baseLocation, this.Size, workArea, BurnInProtection.SpecBoardSalt));
    }

    private void OpenRow(SpecBoardRow row)
    {
        if (row == null)
        {
            return;
        }

        string target = ResolveOpenTarget(row.SpecPath, row.ProjectRoot);
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    internal static string ResolveOpenTarget(string specPath, string projectRoot)
    {
        return SpecBoardPathPolicy.ResolveOpenTarget(projectRoot, specPath);
    }

    private void HandleCardMouseUp(SpecBoardRow row)
    {
        string path = row == null ? string.Empty : row.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (string.Equals(this.suppressedCardMouseUpPath, path, StringComparison.OrdinalIgnoreCase))
        {
            // MouseDoubleClick is followed by the second MouseUp. Consume it so the resolved
            // double-click cannot enqueue a delayed clipboard copy of the same card.
            this.suppressedCardMouseUpPath = string.Empty;
            return;
        }

        this.suppressedCardMouseUpPath = string.Empty;
        this.pendingCardSingleClick = row;
        this.cardSingleClickTimer.Stop();
        this.cardSingleClickTimer.Interval = Math.Max(1, SystemInformation.DoubleClickTime);
        this.cardSingleClickTimer.Start();
    }

    private void HandleCardDoubleClick(SpecBoardRow row)
    {
        string path = row == null ? string.Empty : row.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        this.cardSingleClickTimer.Stop();
        this.pendingCardSingleClick = null;
        this.suppressedCardMouseUpPath = path;
        OpenRow(row);
    }

    private void OnCardSingleClickTimerTick(object sender, EventArgs e)
    {
        this.cardSingleClickTimer.Stop();
        SpecBoardRow row = this.pendingCardSingleClick;
        this.pendingCardSingleClick = null;
        CopyRowAbsolutePath(row);
    }

    private void CopyRowAbsolutePath(SpecBoardRow row)
    {
        string path = row == null ? string.Empty : row.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Clipboard.SetText(path, TextDataFormat.UnicodeText);
            this.copySuccessNotice = "已复制 Spec 绝对路径";
            this.copySuccessNoticeUntilUtc = DateTime.UtcNow.AddSeconds(CopySuccessNoticeSeconds);
            ResetAutoHideClock();
            RenderLayeredWindow();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void CancelCardClick()
    {
        this.cardSingleClickTimer.Stop();
        this.pendingCardSingleClick = null;
        this.suppressedCardMouseUpPath = string.Empty;
        this.copySuccessNotice = string.Empty;
        this.copySuccessNoticeUntilUtc = DateTime.MinValue;
    }

    private bool ExpireCopySuccessNotice(DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(this.copySuccessNotice) || nowUtc < this.copySuccessNoticeUntilUtc)
        {
            return false;
        }

        this.copySuccessNotice = string.Empty;
        this.copySuccessNoticeUntilUtc = DateTime.MinValue;
        return true;
    }

    private Size GetDesiredSize()
    {
        // SpecBoardWidth/Height are 96-DPI logical sizes. Content is painted at LayerScale
        // (DPI x resolution-compatibility), so the physical window must grow by the same
        // factor or every element gets double-crowded on high-DPI displays.
        return new Size(
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardWidth * this.LayerScale)),
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardHeight * this.LayerScale)));
    }

    private void ResetAutoHideClock()
    {
        this.lastInteractionUtc = DateTime.UtcNow;
        if (this.autoPopupActive && this.CurrentSettings != null)
        {
            this.autoPopupHideUtc = this.lastInteractionUtc.AddSeconds(this.CurrentSettings.SpecBoardAutoPopupSeconds);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelRefresh();
            SuspendVisibleWork();
            DisposeDockTab();
            this.maintenanceTimer.Tick -= OnMaintenanceTick;
            this.maintenanceTimer.Dispose();
            this.cardSingleClickTimer.Stop();
            this.cardSingleClickTimer.Tick -= OnCardSingleClickTimerTick;
            this.cardSingleClickTimer.Dispose();
            if (this.managerForm != null && !this.managerForm.IsDisposed)
            {
                this.managerForm.Close();
                this.managerForm.Dispose();
                this.managerForm = null;
            }
            DisposeWatcher();
            DisposeProjectWatchers();
            this.fontCache.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int MeasureLineHeight(Graphics g, Font font, int padding)
    {
        return Math.Max(1, (int)Math.Ceiling(g.MeasureString("Ag国", font, int.MaxValue, StringFormat.GenericTypographic).Height) + padding);
    }

    private static StringFormat CreateStringFormat(StringAlignment alignment, StringTrimming trimming)
    {
        return new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = trimming,
            FormatFlags = StringFormatFlags.NoWrap
        };
    }

    private static string FormatRelativeAge(DateTime? eventUtc, DateTime nowUtc)
    {
        if (!eventUtc.HasValue)
        {
            return "--";
        }

        TimeSpan age = nowUtc - eventUtc.Value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 60)
        {
            return Math.Max(0, (int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (age.TotalHours < 48)
        {
            return Math.Max(0, (int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        }

        return Math.Max(0, (int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
    }

    private static string FitProjectLabel(Graphics g, Font smallFont, string project, string eventLabel, float availableWidth)
    {
        // Show the full project name whenever it fits; only trim (from the end, keeping the
        // event time intact) when the combined subtitle is genuinely wider than the card.
        project = project ?? string.Empty;
        string separatorAndEvent = " · " + eventLabel;
        if (g.MeasureString(project + separatorAndEvent, smallFont).Width <= availableWidth)
        {
            return project;
        }

        float reserved = g.MeasureString(separatorAndEvent, smallFont).Width;
        float projectWidth = Math.Max(1f, availableWidth - reserved);
        for (int length = project.Length - 1; length > 4; length--)
        {
            string candidate = project.Substring(0, length) + "…";
            if (g.MeasureString(candidate, smallFont).Width <= projectWidth)
            {
                return candidate;
            }
        }

        return project.Substring(0, Math.Min(4, project.Length)) + "…";
    }

    private static string GetEventLabel(SpecBoardRow row)
    {
        string label = row.Status == "unregistered" ? "发现于" : row.Status == "pending" ? "登记于" : row.Status == SpecBoardStatus.NeedsRevision ? "要求修改" : row.Status == "awaiting_verify" ? "执行完成" : "更新于";
        return label + " " + (row.EventTimeUtc.HasValue ? row.EventTimeUtc.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "--");
    }

    private static SpecBoardPalette GetPalette()
    {
        return new SpecBoardPalette(DesignTokens.Colors.TextStrong, DesignTokens.Colors.GlyphMuted, DesignTokens.Colors.Danger, DesignTokens.Colors.Warning, DesignTokens.Colors.Success, DesignTokens.Colors.WarningDeep, DesignTokens.Colors.AccentAlt);
    }

    internal static void RenderSamples(string outputDir, bool sample, bool current)
    {
        Directory.CreateDirectory(outputDir);
        if (sample)
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            using (SpecBoardForm form = new SpecBoardForm(null, settings))
            {
                form.snapshot = CreateSampleSnapshot();
                SpecBoardRow highlightedSample = form.snapshot.Rows.FirstOrDefault(row => row.Id == "u1");
                if (highlightedSample != null)
                {
                    form.autoPopupHighlightedRows.Add(GetAutoPopupRowKey(highlightedSample));
                    form.autoPopupHighlightUntilUtc = DateTime.UtcNow.AddMinutes(1);
                }
                form.SetLayerScale(2.0f);
                // Keep the stable baseline filename even though the board no longer has variants.
                form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawWindowContent(g);
                    string path = Path.Combine(outputDir, "specboard-classic.png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine("Default -> " + path + " (" + form.Width + "x" + form.Height + ")");
                }
            }

            // Compact single-column sample: rail hidden, cards full width, shared footer intact.
            WidgetSettings compactSettings = WidgetSettings.CreateDefaults();
            compactSettings.SpecBoardWidth = 240;
            compactSettings.SpecBoardHeight = 320;
            compactSettings.Normalize();
            using (SpecBoardForm form = new SpecBoardForm(null, compactSettings))
            {
                form.snapshot = CreateSampleSnapshot();
                form.SetLayerScale(2.0f);
                form.Size = new Size(compactSettings.SpecBoardWidth * 2, compactSettings.SpecBoardHeight * 2);
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawWindowContent(g);
                    string path = Path.Combine(outputDir, "specboard-compact.png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine("Compact -> " + path + " (" + form.Width + "x" + form.Height + ")");
                }
            }

            // Five-section proof shots. The merged board can no longer promise "one complete card per
            // section" at the default 400 logical height, so both the default and a roomier height
            // are rendered with a live band present, for the P1 acceptance eyeball.
            RenderFiveSectionSample(outputDir, 400, "specboard-fivesection-400.png");
            RenderFiveSectionSample(outputDir, 620, "specboard-fivesection-620.png");
        }

        if (current)
        {
            WidgetSettings settings = WidgetSettings.Load();
            using (SpecBoardForm form = new SpecBoardForm(null, settings))
            {
                form.snapshot = SpecBoardReader.Read(settings.SpecBoardLedgerPath, true);
                form.SetLayerScale(2.0f);
                form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
                RenderSampleSupport.SaveComposited(outputDir, "specboard-current.png", form.Width, form.Height, 255, form.DrawWindowContent);
            }
        }
    }

    internal static void RunSelfTest()
    {
        SpecBoardReader.RunSelfTest();
        SpecBoardSeenStateStore.RunSelfTest();
        SpecBoardLedgerStore.RunSelfTest();
        SpecBoardManagerForm.RunSelfTest();
        RunResolveOpenTargetSelfTest();
        if (ShouldApplyRefreshResult(10, 11, false) || ShouldApplyRefreshResult(10, 10, true) ||
            !ShouldApplyRefreshResult(10, 10, false))
        {
            throw new InvalidOperationException("Spec Board stale refresh result guard self-test failed.");
        }
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        using (SpecBoardForm form = new SpecBoardForm(null, settings))
        using (Bitmap bitmap = new Bitmap(settings.SpecBoardWidth, settings.SpecBoardHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.snapshot = CreateSampleSnapshot();
            form.SetLayerScale(1.0f);
            form.Size = bitmap.Size;
            form.DrawWindowContent(g);
            Rectangle managerButtonBounds = form.managerButtonBounds;
            Rectangle closeButtonBounds = form.closeButtonBounds;
            if (managerButtonBounds.IsEmpty || closeButtonBounds.IsEmpty ||
                form.snapshot.Count(string.Empty, SpecBoardStatus.NeedsRevision) == 0)
            {
                throw new InvalidOperationException("Spec Board manager/close entry or needs_revision fixture missing.");
            }
            Rectangle leftUnion = Rectangle.Empty;
            for (int i = 0; i < form.projectHitTargets.Count; i++)
            {
                leftUnion = Rectangle.Union(leftUnion, form.projectHitTargets[i].Bounds);
            }

            for (int i = 0; i < form.cardHitTargets.Count; i++)
            {
                Rectangle card = form.cardHitTargets[i].Bounds;
                if (card.Left < 0 || card.Top < 0 || card.Right > bitmap.Width || card.Bottom > bitmap.Height)
                {
                    throw new InvalidOperationException("Spec Board card escaped content bounds.");
                }

                if (!leftUnion.IsEmpty && leftUnion.IntersectsWith(card))
                {
                    throw new InvalidOperationException("Spec Board project rail overlaps action cards.");
                }
            }

            string[] requiredStatuses = { SpecBoardStatus.Unregistered, SpecBoardStatus.Pending, SpecBoardStatus.NeedsRevision, SpecBoardStatus.AwaitingVerify };
            for (int i = 0; i < requiredStatuses.Length; i++)
            {
                if (!form.cardHitTargets.Any(target => string.Equals(target.Row.Status, requiredStatuses[i], StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Spec Board did not retain a visible card for status " + requiredStatuses[i] + ".");
                }
            }

            Point firstCardPoint = new Point(
                form.cardHitTargets[0].Bounds.Left + form.cardHitTargets[0].Bounds.Width / 2,
                form.cardHitTargets[0].Bounds.Top + form.cardHitTargets[0].Bounds.Height / 2);
            Point managerPoint = new Point(
                managerButtonBounds.Left + managerButtonBounds.Width / 2,
                managerButtonBounds.Top + managerButtonBounds.Height / 2);
            if (form.ShouldDismissForBlankClick(firstCardPoint) ||
                form.ShouldDismissForBlankClick(managerPoint))
            {
                throw new InvalidOperationException("Spec Board interactive targets must not trigger blank-area dismissal.");
            }

            bool foundBlankDismissPoint = false;
            for (int y = 0; y < bitmap.Height && !foundBlankDismissPoint; y += 4)
            {
                for (int x = 0; x < bitmap.Width; x += 4)
                {
                    if (form.ShouldDismissForBlankClick(new Point(x, y)))
                    {
                        foundBlankDismissPoint = true;
                        break;
                    }
                }
            }

            if (!foundBlankDismissPoint)
            {
                throw new InvalidOperationException("Spec Board exposed no blank-area dismissal surface.");
            }

            if (form.cardHitTargets.Count >= form.snapshot.Rows.Count(row => row.Status == "unregistered" || row.Status == "pending" || row.Status == SpecBoardStatus.NeedsRevision || row.Status == "awaiting_verify"))
            {
                throw new InvalidOperationException("Spec Board overflow fixture did not hide any cards.");
            }

            using (Bitmap noticeShown = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppPArgb))
            using (Bitmap noticeGone = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppPArgb))
            using (Graphics shownGraphics = Graphics.FromImage(noticeShown))
            using (Graphics goneGraphics = Graphics.FromImage(noticeGone))
            {
                form.copySuccessNotice = "已复制 Spec 绝对路径";
                form.copySuccessNoticeUntilUtc = DateTime.UtcNow.AddSeconds(30);
                form.DrawWindowContent(shownGraphics);
                form.copySuccessNotice = string.Empty;
                form.copySuccessNoticeUntilUtc = DateTime.MinValue;
                form.DrawWindowContent(goneGraphics);
                int changedPixels = 0;
                for (int y = 0; y < noticeShown.Height; y++)
                {
                    for (int x = 0; x < noticeShown.Width; x++)
                    {
                        if (noticeShown.GetPixel(x, y).ToArgb() != noticeGone.GetPixel(x, y).ToArgb())
                        {
                            changedPixels++;
                        }
                    }
                }

                if (changedPixels < 100)
                {
                    throw new InvalidOperationException("Spec Board copy-success notice was not visibly rendered.");
                }
            }
        }

        if (FormatRelativeAge(DateTime.UtcNow.AddMinutes(-59), DateTime.UtcNow) != "59m" ||
            FormatRelativeAge(DateTime.UtcNow.AddHours(-47), DateTime.UtcNow) != "47h" ||
            FormatRelativeAge(DateTime.UtcNow.AddDays(-3), DateTime.UtcNow) != "3d")
        {
            throw new InvalidOperationException("Spec Board relative age boundaries failed.");
        }

        RunSectionLadderSelfTest();
        RunCompactLayoutSelfTest();
        RunAutoHideSelfTest();
        RunAutoPopupSelfTest();
        RunManagerLifecycleSelfTest();
        RunManagerWatcherWriteSelfTest();
    }

    // Compact single-column mode: below CompactRailMinimumLogicalWidth the project rail must
    // disappear, the cards must take (nearly) the full width, the shared footer must survive with
    // both pills reachable, and any sticky project filter must reset so no rows silently vanish.
    private static void RenderFiveSectionSample(string outputDir, int logicalHeight, string fileName)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardHeight = logicalHeight;
        settings.Normalize();
        using (SpecBoardForm form = new SpecBoardForm(null, settings))
        {
            form.snapshot = CreateSampleSnapshot();
            form.taskSnapshot = CreateSampleTaskSnapshot();
            form.SetLayerScale(2.0f);
            form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                form.DrawWindowContent(g);
                string path = Path.Combine(outputDir, fileName);
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine("Five-section -> " + path + " (" + form.Width + "x" + form.Height + ")");
            }
        }
    }

    // Fixture sessions for the render harness, where no reader is attached. Leaf names deliberately
    // mix a registered project with an unregistered one so the "未归属" rail row appears.
    private static CodexTaskMonitorSnapshot CreateSampleTaskSnapshot()
    {
        DateTime now = DateTime.Now;
        List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>
        {
            new CodexTaskSnapshot("rollout:a", 1, "DesktopCodexAssistant", "gpt-6-astra",
                CodexTaskStatus.Active, now.AddMinutes(-42), now.AddSeconds(-5), null, null, false,
                CodexTaskTokenUsage.Empty, CodexTaskTokenUsage.Empty, 36.0, "左侧七停靠板 macOS 移植 B 批"),
            new CodexTaskSnapshot("rollout:b", 2, "WSLmanager", "gpt-6-astra",
                CodexTaskStatus.Listening, now.AddMinutes(-18), now.AddMinutes(-9), null, null, false,
                CodexTaskTokenUsage.Empty, CodexTaskTokenUsage.Empty, 53.0, "GUI 三发行版 L1-L5 真实矩阵"),
            new CodexTaskSnapshot("rollout:c", 3, "qiyangtracker-x64", "gpt-6-astra",
                CodexTaskStatus.Idle, now.AddHours(-3), now.AddHours(-2), CodexTaskStatus.Completed,
                now.AddHours(-2), false, CodexTaskTokenUsage.Empty, CodexTaskTokenUsage.Empty, 22.7,
                "迁移 WinUI 3 并修复 x64 构建")
        };
        return new CodexTaskMonitorSnapshot(tasks, 2, now);
    }

    // The ladder is what replaces the pre-merge "every section keeps one complete card" contract, so
    // it is asserted on the real default geometry rather than on a convenient fixture size.
    private static void RunSectionLadderSelfTest()
    {
        int segment = 22;
        int card = 46;
        int liveCard = 58;
        int gap = 4;
        int defaultFlowHeight = 334;

        WorkBoardModel busy = BuildLadderFixture(3, 20, 4, 0, 10);
        SectionPlan[] plan = ComputeSectionPlan(busy, defaultFlowHeight, segment, card, liveCard, gap, false);
        if (plan.Length != 5)
        {
            throw new InvalidOperationException("Work Board ladder must always plan five sections.");
        }

        int used = plan.Length * segment;
        for (int i = 0; i < plan.Length; i++)
        {
            used += plan[i].CardCount * ((plan[i].IsLive ? liveCard : card) + gap);
            if (plan[i].CardCount > plan[i].TotalCount)
            {
                throw new InvalidOperationException("Work Board ladder planned more cards than rows exist.");
            }
        }

        if (used > defaultFlowHeight)
        {
            throw new InvalidOperationException("Work Board ladder overflowed the available flow height.");
        }

        if (plan[0].CardCount < 1)
        {
            throw new InvalidOperationException("Work Board ladder must serve the live band first.");
        }

        // 需要修改 is empty in the real ledger; an empty section costs a header and nothing else.
        if (plan[3].TotalCount != 0 || plan[3].CardCount != 0)
        {
            throw new InvalidOperationException("Work Board ladder must collapse empty sections to a bare header.");
        }

        // Only live sessions: every other section is a bare header, and the band must not take more
        // than its per-section cap even with the whole budget free.
        WorkBoardModel liveOnly = BuildLadderFixture(6, 0, 0, 0, 0);
        SectionPlan[] livePlan = ComputeSectionPlan(liveOnly, defaultFlowHeight, segment, card, liveCard, gap, false);
        if (livePlan[0].CardCount != 3)
        {
            throw new InvalidOperationException("Work Board live band must honour the wide-layout cap of three.");
        }

        for (int i = 1; i < livePlan.Length; i++)
        {
            if (livePlan[i].CardCount != 0)
            {
                throw new InvalidOperationException("Work Board ladder drew cards for an empty ledger section.");
            }
        }

        // Compact mode caps every section at one card.
        SectionPlan[] compactPlan = ComputeSectionPlan(busy, defaultFlowHeight, segment, card, liveCard, gap, true);
        for (int i = 0; i < compactPlan.Length; i++)
        {
            if (compactPlan[i].CardCount > 1)
            {
                throw new InvalidOperationException("Work Board compact ladder must cap each section at one card.");
            }
        }

        // Fully empty input still plans five headers and no cards.
        WorkBoardModel empty = BuildLadderFixture(0, 0, 0, 0, 0);
        SectionPlan[] emptyPlan = ComputeSectionPlan(empty, defaultFlowHeight, segment, card, liveCard, gap, false);
        for (int i = 0; i < emptyPlan.Length; i++)
        {
            if (emptyPlan[i].CardCount != 0 || emptyPlan[i].TotalCount != 0)
            {
                throw new InvalidOperationException("Work Board ladder must stay empty for empty input.");
            }
        }

        // A height that cannot even hold five headers must not produce negative or phantom cards.
        SectionPlan[] starved = ComputeSectionPlan(busy, segment * 2, segment, card, liveCard, gap, false);
        for (int i = 0; i < starved.Length; i++)
        {
            if (starved[i].CardCount != 0)
            {
                throw new InvalidOperationException("Work Board ladder must degrade to headers when starved.");
            }
        }

        // Raising the height restores a complete card for every non-empty section.
        SectionPlan[] roomy = ComputeSectionPlan(busy, 560, segment, card, liveCard, gap, false);
        for (int i = 0; i < roomy.Length; i++)
        {
            if (roomy[i].TotalCount > 0 && roomy[i].CardCount < 1)
            {
                throw new InvalidOperationException("Work Board ladder must serve every non-empty section at 560.");
            }
        }
    }

    private static WorkBoardModel BuildLadderFixture(int live, int unregistered, int pending, int revision, int awaiting)
    {
        SpecBoardSnapshot spec = new SpecBoardSnapshot();
        spec.ProjectRegistryAvailable = true;
        spec.Projects.Add(new SpecBoardProject { Name = "Demo", Display = "Demo", Root = @"D:\Demo", SpecGlob = "Docs/Technical/*-SPEC-*.md" });
        AppendLadderRows(spec, SpecBoardStatus.Unregistered, unregistered);
        AppendLadderRows(spec, SpecBoardStatus.Pending, pending);
        AppendLadderRows(spec, SpecBoardStatus.NeedsRevision, revision);
        AppendLadderRows(spec, SpecBoardStatus.AwaitingVerify, awaiting);

        List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>();
        for (int i = 0; i < live; i++)
        {
            tasks.Add(new CodexTaskSnapshot(
                "rollout:" + i.ToString(CultureInfo.InvariantCulture), i + 1, "Demo", "gpt-6-astra",
                CodexTaskStatus.Active, DateTime.Now.AddMinutes(-20), DateTime.Now.AddMinutes(-1),
                null, null, false, CodexTaskTokenUsage.Empty, CodexTaskTokenUsage.Empty, 30.0, "fixture"));
        }

        return WorkBoardComposer.Compose(
            spec,
            new CodexTaskMonitorSnapshot(tasks, tasks.Count, DateTime.Now),
            WorkBoardFilter.All,
            DateTime.Now,
            WorkBoardLimits.Default);
    }

    private static void AppendLadderRows(SpecBoardSnapshot spec, string status, int count)
    {
        for (int i = 0; i < count; i++)
        {
            spec.Rows.Add(new SpecBoardRow
            {
                Id = status + "." + i.ToString(CultureInfo.InvariantCulture),
                Project = "Demo",
                ProjectRoot = @"D:\Demo",
                SpecPath = "Docs/Technical/Demo-SPEC-v1.md",
                Title = status + " " + i.ToString(CultureInfo.InvariantCulture),
                Status = status,
                EventTimeUtc = DateTime.UtcNow.AddDays(-i - 1),
                IsUnregistered = string.Equals(status, SpecBoardStatus.Unregistered, StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    private static void RunCompactLayoutSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = 240;
        settings.SpecBoardHeight = 320;
        settings.Normalize();
        if (settings.SpecBoardWidth != 240)
        {
            throw new InvalidOperationException("Spec Board width floor should allow 240 after normalization.");
        }

        using (SpecBoardForm form = new SpecBoardForm(null, settings))
        using (Bitmap bitmap = new Bitmap(settings.SpecBoardWidth, settings.SpecBoardHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.snapshot = CreateSampleSnapshot();
            form.selectedProject = "DesktopCodexAssistant";
            form.SetLayerScale(1.0f);
            form.Size = bitmap.Size;
            form.DrawWindowContent(g);

            if (form.projectHitTargets.Count != 0)
            {
                throw new InvalidOperationException("Spec Board compact mode must not expose project rail hit targets.");
            }

            if (form.selectedProject.Length != 0)
            {
                throw new InvalidOperationException("Spec Board compact mode must reset the sticky project filter.");
            }

            Rectangle managerBounds = form.managerButtonBounds;
            Rectangle closeBounds = form.closeButtonBounds;
            if (managerBounds.IsEmpty || closeBounds.IsEmpty ||
                closeBounds.Right > bitmap.Width || managerBounds.Bottom > bitmap.Height)
            {
                throw new InvalidOperationException("Spec Board compact mode lost the manager/close footer pills.");
            }

            if (form.cardHitTargets.Count == 0)
            {
                throw new InvalidOperationException("Spec Board compact mode rendered no cards.");
            }

            for (int i = 0; i < form.cardHitTargets.Count; i++)
            {
                Rectangle card = form.cardHitTargets[i].Bounds;
                if (card.Left < 0 || card.Top < 0 || card.Right > bitmap.Width || card.Bottom > bitmap.Height)
                {
                    throw new InvalidOperationException("Spec Board compact card escaped content bounds.");
                }

                if (card.Width < (int)(bitmap.Width * 0.8))
                {
                    throw new InvalidOperationException("Spec Board compact card should span nearly the full width.");
                }

                if (card.Bottom > managerBounds.Top)
                {
                    throw new InvalidOperationException("Spec Board compact card overlaps the footer.");
                }
            }
        }
    }

    private static void RunResolveOpenTargetSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DesktopCodexAssistant-SpecBoardOpen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string markdownPath = Path.Combine(root, "safe.md");
            File.WriteAllText(markdownPath, "# safe", SharedEncoding.Utf8NoBom);
            if (!string.Equals(ResolveOpenTarget("safe.md", root), markdownPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spec Board open-target self-test failed for an allowed Markdown file.");
            }

            string executablePath = Path.Combine(root, "blocked.exe");
            File.WriteAllText(executablePath, "not executable", SharedEncoding.Utf8NoBom);
            if (!string.Equals(ResolveOpenTarget("blocked.exe", root), root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spec Board open-target self-test did not redirect an executable to its directory.");
            }

            if (!string.Equals(ResolveOpenTarget("missing.md", root), root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spec Board open-target self-test changed the existing missing-file directory fallback.");
            }

            if (!string.Equals(ResolveOpenTarget("missing/nested.md", root), root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spec Board open-target self-test did not fall back to the project root.");
            }

            if (!string.Equals(ResolveOpenTarget("../outside.md", root), root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spec Board traversal open-target self-test failed.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static void RunManagerWatcherWriteSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-watcher-write-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(root, "project");
        string technical = Path.Combine(projectRoot, "Docs", "Technical");
        Directory.CreateDirectory(technical);
        try
        {
            string ledger = Path.Combine(root, "SPEC_BOARD.jsonl");
            File.WriteAllText(Path.Combine(root, "PROJECTS.json"), "{\"schema_version\":1,\"projects\":[{\"name\":\"Test\",\"root\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(projectRoot) + ",\"spec_glob\":\"Docs/Technical/*-SPEC-*.md\"}]}", SharedEncoding.Utf8NoBom);
            File.WriteAllText(Path.Combine(technical, "Watcher-SPEC-v1.md"), "watcher", SharedEncoding.Utf8NoBom);
            File.WriteAllText(ledger, "{\"schema_version\":1,\"id\":\"Test.watcher\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/Watcher-SPEC-v1.md\",\"title\":\"Watcher\",\"status\":\"pending\",\"registered_utc\":\"2026-07-11T00:00:00Z\",\"updated_utc\":\"2026-07-11T00:00:00Z\"}\n", SharedEncoding.Utf8NoBom);
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.SpecBoardLedgerPath = ledger;
            settings.SpecBoardAutoHideSeconds = 0;
            using (SpecBoardForm form = new SpecBoardForm(null, settings))
            {
                form.ShowBoard();
                WaitForUiCondition(() => form.snapshot.Rows.Any(row => row.Id == "Test.watcher"), 3000);
                SpecBoardRow row = form.snapshot.Rows.First(value => value.Id == "Test.watcher");
                string error;
                if (!SpecBoardLedgerStore.TrySetStatus(ledger, new[] { row }, SpecBoardStatus.NeedsRevision, out error))
                {
                    throw new InvalidOperationException("Spec Board watcher write setup failed: " + error);
                }

                WaitForUiCondition(() => form.snapshot.Rows.Any(value => value.Id == "Test.watcher" && value.Status == SpecBoardStatus.NeedsRevision), 3000);
                if (form.snapshot.MalformedLines != 0 || !form.snapshot.Rows.Any(value => value.Id == "Test.watcher" && value.Status == SpecBoardStatus.NeedsRevision))
                {
                    throw new InvalidOperationException("Spec Board watcher observed partial JSON or missed atomic manager write.");
                }

                form.HideBoard();
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void WaitForUiCondition(Func<bool> condition, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
    }

    private static void RunManagerLifecycleSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardAutoHideSeconds = 0;
        using (SpecBoardForm form = new SpecBoardForm(null, settings))
        {
            form.ShowBoard();
            Application.DoEvents();
            form.ShowManagerWindow();
            Application.DoEvents();
            if (form.managerForm == null || !form.managerForm.Visible ||
                form.managerForm.Width != settings.SpecBoardManagerWidth || form.managerForm.Height != settings.SpecBoardManagerHeight)
            {
                throw new InvalidOperationException("Spec Board manager entry, visibility, or configured size failed.");
            }

            form.HideBoard();
            Application.DoEvents();
            if (!form.managerForm.Visible)
            {
                throw new InvalidOperationException("Spec Board manager incorrectly followed board hide.");
            }

            form.ShowBoard();
            Application.DoEvents();
            form.managerForm.Close();
            Application.DoEvents();
            if (!form.Visible)
            {
                throw new InvalidOperationException("Closing Spec Board manager incorrectly closed the board.");
            }

            form.HideBoard();
        }
    }

    private static void RunAutoHideSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardLedgerPath = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-missing-" + Guid.NewGuid().ToString("N") + ".jsonl");
        settings.SpecBoardAutoHideSeconds = 5;
        settings.SpecBoardAutoPopupEnabled = false;
        using (SpecBoardForm form = new SpecBoardForm(null, settings))
        {
            DateTime noticeNow = DateTime.UtcNow;
            form.copySuccessNotice = "已复制 Spec 绝对路径";
            form.copySuccessNoticeUntilUtc = noticeNow.AddSeconds(CopySuccessNoticeSeconds);
            if (form.ExpireCopySuccessNotice(noticeNow) ||
                !form.ExpireCopySuccessNotice(noticeNow.AddSeconds(CopySuccessNoticeSeconds + 1)) ||
                !string.IsNullOrEmpty(form.copySuccessNotice))
            {
                throw new InvalidOperationException("Spec Board copy-success notice lifetime failed.");
            }

            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            Point cursor = Cursor.Position;
            int awayX = cursor.X < workArea.Left + workArea.Width / 2 ? Math.Max(workArea.Left, workArea.Right - form.Width) : workArea.Left;
            int awayY = cursor.Y < workArea.Top + workArea.Height / 2 ? Math.Max(workArea.Top, workArea.Bottom - form.Height) : workArea.Top;
            form.Location = new Point(awayX, awayY);
            form.ShowBoard();
            Application.DoEvents();
            form.cursorPositionProvider = delegate { return new Point(form.Right + 10, form.Bottom + 10); };
            form.lastInteractionUtc = DateTime.UtcNow.AddSeconds(-6);
            form.OnMaintenanceTick(null, EventArgs.Empty);
            if (form.Visible || form.maintenanceTimer.Enabled)
            {
                throw new InvalidOperationException("Spec Board five-second auto-hide or hidden timer shutdown failed.");
            }

            form.CurrentSettings.SpecBoardAutoHideSeconds = 0;
            form.ShowBoard();
            Application.DoEvents();
            form.lastInteractionUtc = DateTime.UtcNow.AddMinutes(-2);
            form.OnMaintenanceTick(null, EventArgs.Empty);
            if (!form.Visible)
            {
                throw new InvalidOperationException("Spec Board zero auto-hide policy failed.");
            }

            form.Location = GetWindowLocationContainingPoint(form.Size, cursor);
            form.cursorPositionProvider = delegate { return new Point(form.Left + 5, form.Top + 5); };
            form.CurrentSettings.SpecBoardAutoHideSeconds = 5;
            form.lastInteractionUtc = DateTime.UtcNow.AddSeconds(-6);
            form.OnMaintenanceTick(null, EventArgs.Empty);
            if (!form.Visible)
            {
                throw new InvalidOperationException("Spec Board hover pause policy failed.");
            }

            SpecBoardRow clickRow = new SpecBoardRow
            {
                ProjectRoot = Path.GetTempPath(),
                SpecPath = "Docs/Technical/click-SPEC.md"
            };
            form.HandleCardMouseUp(clickRow);
            if (!form.cardSingleClickTimer.Enabled || form.pendingCardSingleClick != clickRow)
            {
                throw new InvalidOperationException("Spec Board card single-click deferral failed.");
            }

            form.HideBoard();
            Application.DoEvents();
            if (form.maintenanceTimer.Enabled || form.cardSingleClickTimer.Enabled || form.pendingCardSingleClick != null ||
                form.watcher != null && form.watcher.EnableRaisingEvents)
            {
                throw new InvalidOperationException("Spec Board hidden state retained active timer, pending click, or watcher.");
            }
        }
    }

    private static void RunAutoPopupSelfTest()
    {
        string watcherRoot = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-auto-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(watcherRoot, "Docs", "Technical"));
        try
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.SpecBoardLedgerPath = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-auto-popup-" + Guid.NewGuid().ToString("N") + ".jsonl");
            settings.SpecBoardAutoHideSeconds = 0;
            settings.SpecBoardAutoPopupSeconds = 5;
            using (SpecBoardForm form = new SpecBoardForm(null, settings))
            {
                SpecBoardSnapshot watcherSnapshot = new SpecBoardSnapshot();
            watcherSnapshot.Projects.Add(new SpecBoardProject
            {
                Name = "Watch",
                Root = watcherRoot,
                SpecGlob = "Docs/Technical/*-SPEC-*.md"
            });
            List<ProjectWatcherSpec> watcherSpecs = BuildProjectWatcherSpecs(watcherSnapshot);
            if (watcherSpecs.Count != 1 ||
                !string.Equals(watcherSpecs[0].Filter, "*-SPEC-*.md", StringComparison.OrdinalIgnoreCase) ||
                watcherSpecs[0].IncludeSubdirectories)
            {
                throw new InvalidOperationException("Spec Board did not derive the project Spec-directory watcher from PROJECTS.json metadata.");
            }

            SpecBoardSnapshot baseline = CreateSampleSnapshot();
            if (form.UpdateAutoPopupBaseline(baseline).Count != 0)
            {
                throw new InvalidOperationException("Spec Board initial auto-popup scan treated existing specs as new.");
            }

            SpecBoardSnapshot changed = CreateSampleSnapshot();
            SpecBoardRow newRow = CreateSampleRow("new-popup", "DesktopCodexAssistant", "新建 Spec 自动弹窗", SpecBoardStatus.Pending, DateTime.UtcNow, false);
            changed.Rows.Add(newRow);
            List<string> discovered = form.UpdateAutoPopupBaseline(changed);
            if (discovered.Count != 1 || form.UpdateAutoPopupBaseline(changed).Count != 0)
            {
                throw new InvalidOperationException("Spec Board auto-popup baseline missed or repeated a new spec.");
            }

            form.autoPopupHighlightedRows.Add(discovered[0]);
            form.autoPopupHighlightUntilUtc = DateTime.UtcNow.AddSeconds(5);
            if (!form.IsAutoPopupHighlighted(newRow, DateTime.UtcNow))
            {
                throw new InvalidOperationException("Spec Board new-spec highlight state failed.");
            }

            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            Point cursor = Cursor.Position;
            int awayX = cursor.X < workArea.Left + workArea.Width / 2 ? Math.Max(workArea.Left, workArea.Right - form.Width) : workArea.Left;
            int awayY = cursor.Y < workArea.Top + workArea.Height / 2 ? Math.Max(workArea.Top, workArea.Bottom - form.Height) : workArea.Top;
            form.Location = new Point(awayX, awayY);
            form.ShowBoard();
            Application.DoEvents();
            form.cursorPositionProvider = delegate { return new Point(form.Right + 10, form.Bottom + 10); };
            form.autoPopupActive = true;
            form.autoPopupHideUtc = DateTime.UtcNow.AddSeconds(-1);
            form.OnMaintenanceTick(null, EventArgs.Empty);
            if (form.Visible)
            {
                throw new InvalidOperationException("Spec Board automatic popup did not close at its configured deadline.");
            }

            form.ShowBoard();
            Application.DoEvents();
            form.Location = GetWindowLocationContainingPoint(form.Size, cursor);
            form.cursorPositionProvider = delegate { return new Point(form.Left + 5, form.Top + 5); };
            form.autoPopupActive = true;
            form.autoPopupHideUtc = DateTime.UtcNow.AddSeconds(-1);
            form.OnMaintenanceTick(null, EventArgs.Empty);
            if (!form.Visible || form.autoPopupHideUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("Spec Board automatic popup did not pause and reset while hovered.");
            }

                form.HideBoard();
            }
        }
        finally
        {
            try { Directory.Delete(watcherRoot, true); } catch { }
        }
    }

    private static SpecBoardSnapshot CreateSampleSnapshot()
    {
        SpecBoardSnapshot sample = new SpecBoardSnapshot
        {
            LedgerPath = @"D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl",
            LedgerLastWriteLocal = new DateTime(2026, 7, 11, 20, 27, 0),
            ProjectRegistryAvailable = true,
            MalformedLines = 1,
            ScanTimeUtc = new DateTime(2026, 7, 11, 11, 27, 0, DateTimeKind.Utc)
        };
        sample.Projects.Add(new SpecBoardProject { Name = "DesktopCodexAssistant", Display = "DesktopCodexAssistant", Root = @"D:\Demo", SpecGlob = "Docs/Technical/*-SPEC-*.md" });
        sample.Projects.Add(new SpecBoardProject { Name = "CodexSleepGuard", Display = "CodexSleepGuard", Root = @"D:\Demo2", SpecGlob = "Docs/Technical/*-SPEC-*.md" });
        sample.Projects.Add(new SpecBoardProject { Name = "SeelenNotificationGuard", Display = "SeelenNotificationGuard", Root = @"D:\Demo3", SpecGlob = "Docs/Technical/*-SPEC-*.md" });
        DateTime now = DateTime.UtcNow;
        sample.Rows.Add(CreateSampleRow("u1", "DesktopCodexAssistant", "未登记的超长规格名称用于验证标题省略号与卡片右侧相对时间不会相撞", "unregistered", now.AddDays(-3), false));
        sample.Rows.Add(CreateSampleRow("p1", "DesktopCodexAssistant", "Spec Board 看板窗口", "pending", now.AddHours(-6), false));
        sample.Rows.Add(CreateSampleRow("p2", "CodexSleepGuard", "睡眠保护恢复流程", "pending", now.AddHours(-47), true));
        sample.Rows.Add(CreateSampleRow("r1", "DesktopCodexAssistant", "规格描述需要修改后重新排队", SpecBoardStatus.NeedsRevision, now.AddHours(-2), false));
        sample.Rows.Add(CreateSampleRow("a1", "SeelenNotificationGuard", "通知恢复待验证并用于验证文件丢失灰显与超长标题省略号", "awaiting_verify", now.AddDays(-10), true));
        sample.Rows.Add(CreateSampleRow("d1", "DesktopCodexAssistant", "已完成规格", "done", now.AddDays(-5), false));
        for (int i = 0; i < 10; i++)
        {
            string status = i % 4 == 0 ? "unregistered" : i % 4 == 1 ? "pending" : i % 4 == 2 ? SpecBoardStatus.NeedsRevision : "awaiting_verify";
            sample.Rows.Add(CreateSampleRow("overflow" + i, i % 2 == 0 ? "DesktopCodexAssistant" : "CodexSleepGuard", "溢出卡片 " + (i + 1).ToString(CultureInfo.InvariantCulture), status, now.AddHours(-10 - i), false));
        }

        return sample;
    }

    private static Point GetWindowLocationContainingPoint(Size size, Point point)
    {
        Rectangle workArea = Screen.FromPoint(point).WorkingArea;
        int left = Math.Max(workArea.Left, Math.Min(point.X - Math.Min(10, Math.Max(1, size.Width - 1)), workArea.Right - size.Width));
        int top = Math.Max(workArea.Top, Math.Min(point.Y - Math.Min(10, Math.Max(1, size.Height - 1)), workArea.Bottom - size.Height));
        return new Point(left, top);
    }

    private static SpecBoardRow CreateSampleRow(string id, string project, string title, string status, DateTime eventUtc, bool missing)
    {
        return new SpecBoardRow { Id = id, Project = project, ProjectRoot = @"D:\Demo", SpecPath = "Docs/Technical/" + id + "-SPEC-demo.md", Title = title, Status = status, EventTimeUtc = eventUtc, UpdatedUtc = eventUtc, FileMissing = missing, IsUnregistered = status == "unregistered" };
    }

    private sealed class ProjectHitTarget
    {
        public Rectangle Bounds;
        public string Project;
    }

    private sealed class CardHitTarget
    {
        public Rectangle Bounds;
        public SpecBoardRow Row;
    }

    private sealed class ProjectWatcherSpec
    {
        public string Directory;
        public string Filter;
        public bool IncludeSubdirectories;
    }

    private struct SpecBoardPalette
    {
        public readonly Color Text;
        public readonly Color Muted;
        public readonly Color Danger;
        public readonly Color Warning;
        public readonly Color Success;
        public readonly Color Unregistered;
        public readonly Color Revision;

        public SpecBoardPalette(Color text, Color muted, Color danger, Color warning, Color success, Color unregistered, Color revision)
        {
            this.Text = text;
            this.Muted = muted;
            this.Danger = danger;
            this.Warning = warning;
            this.Success = success;
            this.Unregistered = unregistered;
            this.Revision = revision;
        }
    }
}
