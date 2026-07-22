using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

// Scheme 1 frontend: the launcher's Codex task node opens this standalone task board window, a
// SpecBoard-style card list over CodexTaskPresentation. Read-only — no reader is constructed here
// and no task state is mutated. The board joins the existing overlay mutex, refreshes itself on a
// timer while visible, and positions itself so the operation core button and the launcher arc
// (which fans up-right from the core) are never covered and never cover it.
internal sealed partial class OperationForm
{
    private const int CodexTaskBoardMaximumRows = 10;

    private OperationCodexTaskBoardForm codexTaskBoardForm;

    private void ToggleCodexTaskBoard()
    {
        OperationCodexTaskBoardForm form = EnsureCodexTaskBoardForm();
        if (form.Visible)
        {
            form.HideBoard();
            return;
        }

        form.ShowBoard();
    }

    private OperationCodexTaskBoardForm EnsureCodexTaskBoardForm()
    {
        if (this.codexTaskBoardForm == null || this.codexTaskBoardForm.IsDisposed)
        {
            this.codexTaskBoardForm = new OperationCodexTaskBoardForm(this);
        }

        this.codexTaskBoardForm.SetDockTabDisplaySuspended(this.displaySuspended);
        this.codexTaskBoardForm.SetDockTabHiddenForFullscreen(this.hiddenForFullscreen);
        this.codexTaskBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.codexTaskBoardForm;
    }

    private void HideCodexTaskBoardIfVisible()
    {
        if (this.codexTaskBoardForm != null &&
            !this.codexTaskBoardForm.IsDisposed &&
            this.codexTaskBoardForm.Visible)
        {
            this.codexTaskBoardForm.HideBoard();
        }
    }

    private void PrepareForCodexTaskOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        HideLauncherTrioIfVisible();
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.CodexTask);
    }

    private void DisposeCodexTaskBoardForm()
    {
        if (this.codexTaskBoardForm == null)
        {
            return;
        }

        try
        {
            this.codexTaskBoardForm.Close();
            this.codexTaskBoardForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.codexTaskBoardForm = null;
        }
    }

    // Screen rect that the board must not overlap: the operation core button unioned with the
    // launcher arc's up-right quadrant (the trio buttons sit on the RadialDial second-level radius
    // between 8° and 82°). Geometry mirrors OperationLauncherTrioForm.ComputeArcCenterOffsets.
    internal Rectangle GetLauncherObstructionScreenRect()
    {
        RectangleF core = GetOperationAnchorScreenRect();
        float coreSize = GetStartButtonSize();
        float item = GetSmallButtonSize();
        float coreRadius = coreSize / 2.0f;
        float firstLevelRadius = coreRadius + coreSize * RadialGapScale + item / 2.0f;
        float secondLevelRadius = firstLevelRadius + item * (RadialGapScale + 1.0f) * RadialLevelSpacingMultiplier;
        float reach = secondLevelRadius + Math.Max(item, coreSize * 0.8f) / 2.0f;
        float centerX = core.Left + core.Width / 2.0f;
        float centerY = core.Top + core.Height / 2.0f;
        RectangleF arcQuadrant = new RectangleF(centerX, centerY - reach, reach, reach);
        return Rectangle.Round(RectangleF.Union(core, arcQuadrant));
    }

    // Pure placement rule shared by the live board and the self-test. Slots in preference order:
    // above the obstruction with the board's right edge at the core center (keeps the up-right
    // launcher arc clear), left of the obstruction centered on the core, below the obstruction.
    // Clamping into the work area runs last; if it reintroduced an overlap, shift away once more.
    internal static Point ComputeCodexTaskBoardPlacement(
        Rectangle workArea,
        RectangleF core,
        Rectangle obstruction,
        Size boardSize,
        int margin)
    {
        float coreCenterX = core.Left + core.Width / 2.0f;
        int left = (int)Math.Round(coreCenterX) - boardSize.Width;
        int top = obstruction.Top - margin - boardSize.Height;

        if (top < workArea.Top)
        {
            left = obstruction.Left - margin - boardSize.Width;
            top = (int)Math.Round(core.Top + core.Height / 2.0f) - boardSize.Height / 2;
        }

        if (left < workArea.Left)
        {
            left = (int)Math.Round(core.Left);
            top = obstruction.Bottom + margin;
        }

        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - boardSize.Width)));
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - boardSize.Height)));

        Rectangle candidate = new Rectangle(left, top, boardSize.Width, boardSize.Height);
        if (candidate.IntersectsWith(obstruction))
        {
            int shiftedLeft = obstruction.Left - margin - boardSize.Width;
            int shiftedTop = obstruction.Top - margin - boardSize.Height;
            if (shiftedLeft >= workArea.Left)
            {
                left = shiftedLeft;
            }
            else if (shiftedTop >= workArea.Top)
            {
                top = shiftedTop;
            }
        }

        return new Point(left, top);
    }

    internal static bool ShouldDismissCodexTaskBoardClick(
        RectangleF closeButton,
        RectangleF viewToggle,
        Point location)
    {
        return !closeButton.Contains(location) &&
            (viewToggle.IsEmpty || !viewToggle.Contains(location));
    }

    private struct CodexTaskFooterLayout
    {
        public RectangleF ViewAction;
        public RectangleF CloseAction;
        public RectangleF Summary;
    }

    private static CodexTaskFooterLayout ComputeCodexTaskFooterLayout(
        RectangleF footer,
        float viewTextWidth,
        float closeTextWidth,
        float minimumActionWidth,
        float actionTextPadding,
        float actionGap,
        float summaryGap)
    {
        // This deliberately mirrors SpecBoardForm.DrawBoardFooter: actions start at the left edge,
        // use content-measured widths with the same minimum, and the quiet summary follows them.
        // Keeping the geometry pure makes the wide and compact boards share one footer contract.
        float viewWidth = Math.Min(footer.Width, Math.Max(minimumActionWidth, viewTextWidth + actionTextPadding));
        RectangleF view = new RectangleF(footer.Left, footer.Top, viewWidth, footer.Height);
        float closeLeft = Math.Min(footer.Right, view.Right + actionGap);
        float closeWidth = Math.Min(Math.Max(0.0f, footer.Right - closeLeft), Math.Max(minimumActionWidth, closeTextWidth + actionTextPadding));
        RectangleF close = new RectangleF(closeLeft, footer.Top, closeWidth, footer.Height);
        float summaryLeft = Math.Min(footer.Right, close.Right + summaryGap);
        RectangleF summary = new RectangleF(summaryLeft, footer.Top, Math.Max(0.0f, footer.Right - summaryLeft), footer.Height);
        return new CodexTaskFooterLayout
        {
            ViewAction = view,
            CloseAction = close,
            Summary = summary
        };
    }

    private static void RunCodexTaskBoardPlacementSelfTest()
    {
        Rectangle workArea = new Rectangle(0, 0, 1440, 900);
        Size board = new Size(236, 210);
        int margin = 8;

        // Core parked mid-screen: the preferred slot must sit above the obstruction and fully clear it.
        RectangleF core = new RectangleF(700.0f, 600.0f, 48.0f, 48.0f);
        Rectangle obstruction = Rectangle.Round(RectangleF.Union(core, new RectangleF(724.0f, 464.0f, 160.0f, 160.0f)));
        Point placed = ComputeCodexTaskBoardPlacement(workArea, core, obstruction, board, margin);
        if (new Rectangle(placed, board).IntersectsWith(obstruction) || placed.Y + board.Height > obstruction.Top)
        {
            throw new InvalidOperationException("Codex task board should sit above the launcher obstruction when space allows.");
        }

        // Core near the top edge: no room above, the board must move to the left slot and stay clear.
        core = new RectangleF(700.0f, 30.0f, 48.0f, 48.0f);
        obstruction = Rectangle.Round(RectangleF.Union(core, new RectangleF(724.0f, -106.0f, 160.0f, 160.0f)));
        placed = ComputeCodexTaskBoardPlacement(workArea, core, obstruction, board, margin);
        if (new Rectangle(placed, board).IntersectsWith(obstruction) || placed.X + board.Width > obstruction.Left)
        {
            throw new InvalidOperationException("Codex task board should fall back to the left slot at the top edge.");
        }

        // Core in the top-left corner: neither above nor left fits, the board must drop below.
        core = new RectangleF(20.0f, 30.0f, 48.0f, 48.0f);
        obstruction = Rectangle.Round(RectangleF.Union(core, new RectangleF(44.0f, -106.0f, 160.0f, 160.0f)));
        placed = ComputeCodexTaskBoardPlacement(workArea, core, obstruction, board, margin);
        if (new Rectangle(placed, board).IntersectsWith(obstruction) || placed.Y < obstruction.Bottom)
        {
            throw new InvalidOperationException("Codex task board should drop below the launcher in the top-left corner.");
        }

        // Every placement must stay inside the work area.
        foreach (Point point in new Point[] { placed })
        {
            Rectangle rect = new Rectangle(point, board);
            if (rect.Left < workArea.Left || rect.Top < workArea.Top ||
                rect.Right > workArea.Right || rect.Bottom > workArea.Bottom)
            {
                throw new InvalidOperationException("Codex task board placement escaped the work area.");
            }
        }

        RectangleF close = new RectangleF(180.0f, 180.0f, 38.0f, 17.0f);
        RectangleF toggle = new RectangleF(122.0f, 180.0f, 52.0f, 17.0f);
        if (ShouldDismissCodexTaskBoardClick(close, toggle, new Point(190, 188)) ||
            ShouldDismissCodexTaskBoardClick(close, toggle, new Point(140, 188)) ||
            !ShouldDismissCodexTaskBoardClick(close, toggle, new Point(80, 100)))
        {
            throw new InvalidOperationException("Codex task board blank-area dismissal hit policy failed.");
        }

        RectangleF footer = new RectangleF(9.0f, 374.0f, 630.0f, 17.0f);
        CodexTaskFooterLayout footerLayout = ComputeCodexTaskFooterLayout(
            footer,
            24.0f,
            18.0f,
            42.0f,
            14.0f,
            4.0f,
            5.0f);
        if (footerLayout.ViewAction.Left != footer.Left ||
            footerLayout.ViewAction.Width != 42.0f ||
            footerLayout.CloseAction.Left != footerLayout.ViewAction.Right + 4.0f ||
            footerLayout.CloseAction.Width != 42.0f ||
            footerLayout.Summary.Left != footerLayout.CloseAction.Right + 5.0f ||
            footerLayout.Summary.Right != footer.Right ||
            ShouldDismissCodexTaskBoardClick(
                footerLayout.CloseAction,
                footerLayout.ViewAction,
                Point.Round(new PointF(footerLayout.ViewAction.Left + 2.0f, footerLayout.ViewAction.Top + 2.0f))) ||
            ShouldDismissCodexTaskBoardClick(
                footerLayout.CloseAction,
                footerLayout.ViewAction,
                Point.Round(new PointF(footerLayout.CloseAction.Left + 2.0f, footerLayout.CloseAction.Top + 2.0f))))
        {
            throw new InvalidOperationException("Codex task board footer must align with the Spec Board action rail.");
        }
    }

    // All seven dock tabs at 8x zoom so the 5x30 trapezoid and centre arrow are reviewable. Each
    // strip is normal idle/hover, hidden idle, protected collapsed, expanded and expanded-hover.
    private static void RenderEdgeDockTabSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        Color[] accents =
        {
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Network),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SpecBoard),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexTask),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Guard),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexIq),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.ResetSpeed),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay)
        };
        int[] salts =
        {
            BurnInProtection.NetworkMonitorDockTabSalt,
            BurnInProtection.SpecBoardDockTabSalt,
            BurnInProtection.CodexTaskBoardDockTabSalt,
            BurnInProtection.GuardBoardDockTabSalt,
            BurnInProtection.CodexIqBoardDockTabSalt,
            BurnInProtection.ResetSpeedBoardDockTabSalt,
            BurnInProtection.SystemDayBoardDockTabSalt
        };
        string[] names = { "network", "spec", "codex", "guard", "codex-iq", "reset-speed", "system-day" };
        EdgeDockTabRole[] roles =
        {
            EdgeDockTabRole.Network,
            EdgeDockTabRole.SpecBoard,
            EdgeDockTabRole.CodexTask,
            EdgeDockTabRole.Guard,
            EdgeDockTabRole.CodexIq,
            EdgeDockTabRole.ResetSpeed,
            EdgeDockTabRole.SystemDay
        };
        for (int i = 0; i < accents.Length; i++)
        {
            using (EdgeDockTabForm tab = new EdgeDockTabForm(settings, accents[i], salts[i], "SampleDockTab", roles[i]))
            {
                string path = System.IO.Path.Combine(outputDir, "operation-dock-tab-" + names[i] + ".png");
                tab.SaveSample(path, 8.0f);
                Console.WriteLine("EdgeDockTab(" + names[i] + ") -> " + path);
            }
        }
    }

    // Two-column bubble cards (default), the switchable timeline view, and the same cards falling
    // back to a single column on a narrow board.
    private static void RenderCodexTaskBoardSample(string outputDir)
    {
        RenderCodexTaskBoardSample(outputDir, "operation-codex-tasks.png", 648, 400, CodexTaskBoardView.Table, false);
        RenderCodexTaskBoardSample(outputDir, "operation-codex-tasks-timeline.png", 648, 400, CodexTaskBoardView.Timeline, true);
        RenderCodexTaskBoardSample(outputDir, "operation-codex-tasks-compact.png", 300, 400, CodexTaskBoardView.Table, false);
    }

    private static void RenderCodexTaskBoardSample(
        string outputDir,
        string fileName,
        int width,
        int height,
        CodexTaskBoardView view,
        bool seedTimeline)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.OperationRenderVariant = OperationRenderVariant.RadialDial;
        settings.CodexTaskBoardWidth = width;
        settings.CodexTaskBoardHeight = height;
        settings.CodexTaskBoardView = view;
        settings.Normalize();
        using (OperationForm form = new OperationForm(
            settings,
            delegate { },
            delegate { },
            delegate { },
            delegate(string title, string message, ToolTipIcon icon) { },
            delegate { return true; },
            delegate { return true; },
            delegate { return true; },
            delegate(bool enabled) { return enabled; },
            delegate(bool enabled) { return enabled; },
            delegate(string propertyName, bool enabled) { return enabled; }))
        using (OperationCodexTaskBoardForm board = new OperationCodexTaskBoardForm(form))
        {
            Func<CodexTaskMonitorSnapshot> savedProvider = CodexTaskPresentation.SnapshotProvider;
            if (seedTimeline)
            {
                SeedTimelineFixture(settings.CodexTaskBoardTimelineMinutes);
            }
            else
            {
                // The card grid sample publishes a six-session fixture so the default 2x3 layout,
                // the attention card and the untitled placeholder are all visible at once.
                DateTime sampleNow = DateTime.Now;
                CodexTaskPresentation.SnapshotProvider = delegate
                {
                    return CodexTaskPresentation.CreateBoardSampleSnapshot(sampleNow);
                };
            }

            try
            {
                string path = System.IO.Path.Combine(outputDir, fileName);
                board.SaveSample(path, 2.0f);
                Console.WriteLine("CodexTaskBoard(" + view + " " + width + "x" + height + ") -> " + path);
            }
            finally
            {
                CodexTaskPresentation.SnapshotProvider = savedProvider;
            }
        }
    }

    // The timeline is built from observations the frontend accumulates at runtime, so a sample run
    // has to replay a plausible history instead of rendering four empty lanes.
    private static void SeedTimelineFixture(int windowMinutes)
    {
        CodexTaskPresentation.ResetTimelineForTest();
        DateTime now = DateTime.Now;
        DateTime start = now.AddMinutes(-windowMinutes);
        CodexTaskStatus[][] script =
        {
            new[] { CodexTaskStatus.Idle, CodexTaskStatus.Active, CodexTaskStatus.Active, CodexTaskStatus.Listening },
            new[] { CodexTaskStatus.Active, CodexTaskStatus.Active, CodexTaskStatus.Active, CodexTaskStatus.Completed },
            new[] { CodexTaskStatus.Idle, CodexTaskStatus.Idle, CodexTaskStatus.Active, CodexTaskStatus.Active },
            new[] { CodexTaskStatus.Active, CodexTaskStatus.Idle, CodexTaskStatus.Idle, CodexTaskStatus.Idle }
        };
        CodexTaskMonitorSnapshot shape = CodexTaskPresentation.CreateFixtureSnapshot(now);
        for (int step = 0; step < 4; step++)
        {
            DateTime at = start.AddMinutes(windowMinutes * step / 4.0);
            List<CodexTaskSnapshot> tasks = new List<CodexTaskSnapshot>();
            for (int t = 0; t < shape.Tasks.Count; t++)
            {
                CodexTaskSnapshot source = shape.Tasks[t];
                tasks.Add(new CodexTaskSnapshot(
                    source.FileKey,
                    source.TaskNumber,
                    source.WorkspaceLeaf,
                    source.Model,
                    script[t % script.Length][step],
                    source.StartedAtLocal,
                    at,
                    null,
                    null,
                    false,
                    source.LastTokenUsage,
                    source.TotalTokenUsage,
                    source.ContextPercent,
                    source.Title));
            }

            CodexTaskPresentation.SampleTimeline(new CodexTaskMonitorSnapshot(tasks, 2, at), at, windowMinutes);
        }

        CodexTaskPresentation.SampleTimeline(shape, now, windowMinutes);
    }

    private sealed class OperationCodexTaskBoardForm : LayeredWidgetFormBase
    {
        private const int RefreshIntervalMs = 2000;

        private readonly OperationForm owner;
        private readonly UiFontCache fontCache = new UiFontCache();
        private readonly System.Windows.Forms.Timer refreshTimer;
        private Func<Point> cursorPositionProvider;
        private EdgeDockTabForm dockTab;
        private DateTime dockPointerLeftUtc = DateTime.MinValue;
        private long outsideClickSequence;
        private DateTime outsideClickCollapseUtc = DateTime.MinValue;
        private string lastContentSignature = string.Empty;
        private RectangleF closeButtonRect = RectangleF.Empty;
        private RectangleF viewToggleRect = RectangleF.Empty;
        private CodexTaskBoardView viewMode;
        private bool viewModeUserChosen;
        private bool displaySuspended;
        private bool hiddenForFullscreen;

        public OperationCodexTaskBoardForm(OperationForm owner)
        {
            this.owner = owner;
            this.cursorPositionProvider = delegate { return Cursor.Position; };
            this.CurrentSettings = owner.CurrentSettings;
            ApplicationIcon.ApplyTo(this);
            InitializeLayerScaleFromCurrentDpi();
            ApplyLayerScaleFromSettings(owner.CurrentSettings);
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.Black;
            this.Size = GetDesiredSize();
            this.refreshTimer = new System.Windows.Forms.Timer();
            this.refreshTimer.Interval = RefreshIntervalMs;
            this.refreshTimer.Tick += OnRefreshTick;
        }

        private bool IsLeftDocked
        {
            get { return true; }
        }

        protected override int WindowTransparencyOverridePercent
        {
            get { return this.CurrentSettings.CodexTaskBoardTransparencyOverridePercent; }
        }

        protected override int WindowScaleOverridePercent
        {
            get { return this.CurrentSettings.CodexTaskBoardScaleOverridePercent; }
        }

        protected override bool CanRenderLayeredWindow()
        {
            return !this.displaySuspended;
        }

        private int ResolveDockTabCenterY()
        {
            return LeftDockLayout.ResolveTabCenterY(
                this.CurrentSettings,
                EdgeDockTabRole.CodexTask,
                this.LayerScale);
        }

        public void ApplyRuntimeSettings(WidgetSettings settings)
        {
            this.CurrentSettings = settings;
            ApplyLayerScaleFromSettings(settings);
            Size desired = GetDesiredSize();
            if (this.Size != desired)
            {
                this.Size = desired;
            }

            if (this.Visible)
            {
                PositionForDisplay();
                RenderLayeredWindow();
            }
            else
            {
                InvalidateLayeredRenderBuffer();
            }

            SyncLeftDockTab();
        }

        // The dock tab is the board's only always-visible surface, so the owner constructs this
        // (hidden) board at startup purely to own it.
        internal void SyncLeftDockTab()
        {
            if (this.IsDisposed)
            {
                return;
            }

            if (!this.IsLeftDocked)
            {
                DisposeDockTab();
                if (!this.Visible)
                {
                    this.refreshTimer.Stop();
                }

                return;
            }

            if (this.dockTab == null || this.dockTab.IsDisposed)
            {
                this.dockTab = new EdgeDockTabForm(
                    this.CurrentSettings,
                    EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexTask),
                    BurnInProtection.CodexTaskBoardDockTabSalt,
                    "CodexTaskBoardDockTab",
                    EdgeDockTabRole.CodexTask);
                this.dockTab.HoverEntered += OnDockTabHoverEntered;
                this.dockTab.HoverExited += OnDockTabHoverExited;
                this.dockTab.PollTick += OnDockTabPollTick;
            }
            else
            {
                this.dockTab.ApplyRuntimeSettings(
                    this.CurrentSettings,
                    EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexTask));
            }

            this.dockTab.SetDisplaySuspended(this.displaySuspended);
            this.dockTab.SetHiddenForFullscreen(this.hiddenForFullscreen);
            this.dockTab.ShowTab(ResolveDockTabCenterY());
            // Docked and collapsed still needs the tick: it drives the tab's burn-in drift and the
            // collapse countdown once a hover expand happens.
            this.refreshTimer.Start();
        }

        internal void SetDockTabHiddenForFullscreen(bool hidden)
        {
            this.hiddenForFullscreen = hidden;
            if (this.dockTab != null && !this.dockTab.IsDisposed)
            {
                this.dockTab.SetHiddenForFullscreen(hidden);
            }

            if (hidden)
            {
                HideBoard();
            }
            else if (!this.displaySuspended && this.IsLeftDocked &&
                this.dockTab != null && !this.dockTab.IsDisposed)
            {
                this.dockTab.ShowTab(ResolveDockTabCenterY());
            }
        }

        internal void SetDockTabDisplaySuspended(bool suspended)
        {
            this.displaySuspended = suspended;
            if (this.dockTab != null && !this.dockTab.IsDisposed)
            {
                this.dockTab.SetDisplaySuspended(suspended);
            }

            if (suspended)
            {
                ResetDisplayRenderResources();
            }
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
                this.IsLeftDocked &&
                this.CurrentSettings != null &&
                this.CurrentSettings.LeftDockOutsideClickCollapseEnabled;
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
            Program.LogInfo("CodexTaskBoard outside click collapsed docked board.");
            HideBoard();
            return true;
        }

        // Docked boards collapse on their own once the pointer leaves both the board and its tab.
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

        private void PositionForDisplay()
        {
            if (this.IsLeftDocked)
            {
                PositionAtLeftDock();
                return;
            }

            PositionAvoidingLauncher();
        }

        // Docked: offset by the tab width so the tab stays visible and the pointer can travel
        // tab -> board without crossing a gap that would start the collapse countdown.
        private void PositionAtLeftDock()
        {
            Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
            Point baseLocation = LeftDockLayout.ResolveBoardBaseLocation(
                this.CurrentSettings,
                EdgeDockTabRole.CodexTask,
                this.LayerScale,
                this.Size);
            this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
                baseLocation,
                this.Size,
                workArea,
                BurnInProtection.CodexTaskBoardSalt);
        }

        public void ShowBoard()
        {
            if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
            {
                return;
            }

            this.owner.PrepareForCodexTaskOverlayShow();
            this.outsideClickCollapseUtc = DateTime.MinValue;
            this.outsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
            this.lastContentSignature = ComputeContentSignature();
            this.Size = GetDesiredSize();
            PositionForDisplay();
            if (!this.Visible)
            {
                Show(this.owner);
            }

            NativeMethods.SetWindowPos(
                this.Handle,
                GetLayeredWidgetInsertAfter(true, this.owner.CurrentSettings.CodexPetZOrderProtectionEnabled),
                this.Left,
                this.Top,
                this.Width,
                this.Height,
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_SHOWWINDOW);
            this.refreshTimer.Start();
            RenderLayeredWindow();
        }

        public void HideBoard()
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            if (this.Visible)
            {
                Hide();
            }

            // A docked board keeps ticking while collapsed so the tab can drift and re-expand; an
            // undocked one has nothing left to do until it is opened again.
            if (!this.IsLeftDocked)
            {
                this.refreshTimer.Stop();
            }
        }

        // While visible, poll the presentation snapshot and repaint only when the content actually
        // changed. The snapshot itself is refreshed by the radar's own reconcile cadence; this timer
        // just keeps the board from showing stale rows while it stays open. While docked-collapsed it
        // only services the tab and the collapse countdown.
        private void OnRefreshTick(object sender, EventArgs e)
        {
            RefreshNightScheduleAtExistingTick();
            if (this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible)
            {
                this.dockTab.RefreshBurnInPosition();
            }

            // Sample before the collapse/visibility bail-outs: a docked-collapsed board is exactly
            // when the timeline needs to keep accruing history, so that opening it later shows what
            // actually happened rather than starting from a blank lane.
            CodexTaskPresentation.SampleTimeline(
                CodexTaskPresentation.GetSnapshot(),
                DateTime.Now,
                this.CurrentSettings == null
                    ? WidgetSettings.DefaultCodexTaskBoardTimelineMinutes
                    : this.CurrentSettings.CodexTaskBoardTimelineMinutes);

            if (UpdateOutsideClickDismissal(DateTime.UtcNow) || UpdateDockCollapse(DateTime.UtcNow) || !this.Visible)
            {
                return;
            }

            string signature = ComputeContentSignature();
            if (string.Equals(signature, this.lastContentSignature, StringComparison.Ordinal))
            {
                return;
            }

            this.lastContentSignature = signature;
            Size desired = GetDesiredSize();
            if (this.Size != desired)
            {
                this.Size = desired;
                PositionForDisplay();
            }

            RenderLayeredWindow();
        }

        private string ComputeContentSignature()
        {
            // The timeline advances every tick even when no task changed, so it opts out of the
            // no-change short circuit and always repaints while visible.
            if (this.ActiveView == CodexTaskBoardView.Timeline)
            {
                return Guid.NewGuid().ToString("N");
            }

            IList<CodexTaskRowModel> rows = CodexTaskPresentation.BuildRows(
                CodexTaskPresentation.GetSnapshot(),
                DateTime.Now,
                MaximumVisibleRows());
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                builder.Append(rows[i].TaskNumber).Append('|')
                    .Append(rows[i].WorkspaceLeaf).Append('|')
                    .Append(rows[i].StatusText).Append('|')
                    .Append(rows[i].DetailText).Append('|')
                    .Append(rows[i].LastTurnTokensText).Append('|')
                    .Append(rows[i].TotalTokensText).Append('|')
                    .Append(rows[i].ContextPercent.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
            }

            return builder.ToString();
        }

        // A card needs at least this much logical width to stay readable; below two of them the grid
        // drops to a single column instead of squeezing.
        private const int BubbleCardMinimumLogicalWidth = 300;
        private const int BubbleCardLogicalHeight = 68;
        private const int BubbleCardLogicalGap = 6;

        // The card grid answers "table or compact" at layout time by column count: the same card
        // renders in a 1- or 2-column flow depending on how wide the board is.
        private int BubbleColumns()
        {
            int contentWidth = this.Width - BoardPadding() * 2;
            int perColumn = S(BubbleCardMinimumLogicalWidth);
            int gap = S(BubbleCardLogicalGap);
            if (perColumn <= 0 || contentWidth < perColumn * 2 + gap)
            {
                return 1;
            }

            return 2;
        }

        private CodexTaskBoardView ActiveView
        {
            get
            {
                if (this.CurrentSettings == null)
                {
                    return CodexTaskBoardView.Table;
                }

                return this.viewModeUserChosen ? this.viewMode : this.CurrentSettings.CodexTaskBoardView;
            }
        }

        private int BoardPadding()
        {
            return S(9);
        }

        private int HeaderHeight()
        {
            return S(16);
        }

        private int BubbleCardHeight()
        {
            return S(BubbleCardLogicalHeight);
        }

        private int BubbleCardGap()
        {
            return S(BubbleCardLogicalGap);
        }

        private int LaneHeight()
        {
            return S(20);
        }

        private int FooterHeight()
        {
            return S(17);
        }

        private int MaximumVisibleRows()
        {
            // The board is a fixed-height window: work out how many cards actually fit rather than
            // letting the grid grow past the frame. Timeline packs one lane per task; the card grid
            // packs columns × rows.
            int padding = BoardPadding();
            int chrome = padding * 2 + HeaderHeight() + S(4) + S(4) + FooterHeight();
            int rowSpace = Math.Max(0, S(this.CurrentSettings.CodexTaskBoardHeight) - chrome);
            bool timeline = this.ActiveView == CodexTaskBoardView.Timeline;
            if (timeline)
            {
                rowSpace -= S(11);
                int perLane = LaneHeight();
                return Math.Max(1, Math.Min(CodexTaskBoardMaximumRows, perLane <= 0 ? 1 : rowSpace / perLane));
            }

            int perCard = BubbleCardHeight() + BubbleCardGap();
            int gridRows = perCard <= 0 ? 1 : Math.Max(1, (rowSpace + BubbleCardGap()) / perCard);
            return Math.Max(1, Math.Min(CodexTaskBoardMaximumRows, gridRows * BubbleColumns()));
        }

        private Size GetDesiredSize()
        {
            return new Size(
                S(this.CurrentSettings.CodexTaskBoardWidth),
                S(this.CurrentSettings.CodexTaskBoardHeight));
        }

        private void PositionAvoidingLauncher()
        {
            this.Location = ComputeCodexTaskBoardPlacement(
                this.owner.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleOperation),
                this.owner.GetOperationAnchorScreenRect(),
                this.owner.GetLauncherObstructionScreenRect(),
                this.Size,
                S(8));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ResetDisplayRenderResources();
            RenderLayeredWindow();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (this.closeButtonRect.Contains(e.Location))
            {
                HideBoard();
                return;
            }

            if (!this.viewToggleRect.IsEmpty && this.viewToggleRect.Contains(e.Location))
            {
                ToggleView();
                return;
            }

            // The task rows are read-only; every surface outside the two footer controls is a
            // dismissal surface, matching the operation overlay's existing blank-click contract.
            if (ShouldDismissCodexTaskBoardClick(this.closeButtonRect, this.viewToggleRect, e.Location))
            {
                HideBoard();
            }
        }

        // The toggle is session-scoped: the board instance outlives every collapse/expand (it is
        // constructed at startup to own the dock tab), so the choice survives hovering away, while
        // the CodexTaskBoardView setting keeps deciding the startup default. Persisting the click
        // itself would need an enum writer through the owner's settings pipeline, which only carries
        // booleans today.
        private void ToggleView()
        {
            this.viewMode = this.ActiveView == CodexTaskBoardView.Timeline
                ? CodexTaskBoardView.Table
                : CodexTaskBoardView.Timeline;
            this.viewModeUserChosen = true;
            this.lastContentSignature = ComputeContentSignature();
            Size desired = GetDesiredSize();
            if (this.Size != desired)
            {
                this.Size = desired;
                PositionForDisplay();
            }

            RenderLayeredWindow();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (this.Visible)
            {
                return;
            }

            // Keep ticking while docked-collapsed: the tick still services the tab.
            if (!this.IsLeftDocked)
            {
                this.refreshTimer.Stop();
            }
        }

        protected override void DrawWindowContent(Graphics g)
        {
            this.owner.ConfigureGraphics(g);
            // A layered ARGB surface cannot safely use ClearType sub-pixels. Use monochrome grid
            // fitting plus physical-pixel font sizes/baselines so compact labels stay crisp instead
            // of acquiring translucent half-pixel edges at 200% DPI.
            g.TextRenderingHint = this.LayerScale >= 1.25f
                ? TextRenderingHint.SingleBitPerPixelGridFit
                : TextRenderingHint.AntiAliasGridFit;
            g.TextContrast = 0;
            int backgroundAlpha = this.owner.GetBackgroundOpacityAlpha();
            CodexTaskMonitorSnapshot snapshot = CodexTaskPresentation.GetSnapshot();
            DateTime nowLocal = DateTime.Now;
            int visibleRows = MaximumVisibleRows();
            IList<CodexTaskRowModel> rows = CodexTaskPresentation.BuildRows(snapshot, nowLocal, visibleRows);
            CodexTaskBadgeModel badge = CodexTaskPresentation.BuildBadge(snapshot);
            bool taskAlertsVisible = AlertPresentationPolicy.ShouldPresent(
                this.CurrentSettings,
                AlertPresentationCategory.CodexTask,
                nowLocal);
            RectangleF bounds = new RectangleF(0.0f, 0.0f, this.Width, this.Height);

            using (GraphicsPath shell = RoundedRectangle(
                RectangleF.Inflate(bounds, -S(1) / 2.0f, -S(1) / 2.0f),
                S(6)))
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(236, backgroundAlpha))))
            using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, ScaleAlpha(96, backgroundAlpha)), Math.Max(1.0f, this.LayerScale)))
            {
                g.FillPath(fill, shell);
                g.DrawPath(border, shell);
            }

            int padding = BoardPadding();
            Font titleFont = GetCrispUiFont(9.0f, 8.0f, FontStyle.Bold);
            Font rowFont = GetCrispUiFont(8.4f, 7.5f, FontStyle.Bold);
            Font subFont = GetCrispUiFont(7.0f, 6.5f, FontStyle.Regular);
            // Match the Spec Board footer's small-text role; these sibling boards should not use
            // different button typography for the same view/close actions.
            Font footFont = GetCrispUiFont(7.8f, 6.5f, FontStyle.Regular);

            bool timeline = this.ActiveView == CodexTaskBoardView.Timeline;
            using (SolidBrush titleBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Text, ScaleAlpha(240, backgroundAlpha))))
            using (StringFormat noWrap = new StringFormat { FormatFlags = StringFormatFlags.NoWrap })
            {
                string title = timeline
                    ? "CODEX TASKS · 时间线 " + this.CurrentSettings.CodexTaskBoardTimelineMinutes.ToString(CultureInfo.InvariantCulture) + " 分"
                    : "CODEX TASKS";
                g.DrawString(title, titleFont, titleBrush, new PointF(padding, SnapPixel(padding * 0.7f)), noWrap);
            }

            if (taskAlertsVisible && badge.AttentionCount > 0)
            {
                string attention = "待处理 " + badge.AttentionCount.ToString(CultureInfo.InvariantCulture);
                using (SolidBrush attentionBrush = new SolidBrush(DesignTokens.WithAlpha(badge.StatusColor, ScaleAlpha(235, backgroundAlpha))))
                using (StringFormat right = new StringFormat
                {
                    Alignment = StringAlignment.Far,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    g.DrawString(
                        attention,
                        footFont,
                        attentionBrush,
                        new RectangleF(this.Width - padding - S(70), SnapPixel(padding * 0.9f), S(70), HeaderHeight()),
                        right);
                }
            }

            float y = padding + HeaderHeight() + S(4);
            if (rows.Count == 0)
            {
                using (SolidBrush emptyBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ScaleAlpha(190, backgroundAlpha))))
                using (StringFormat noWrap = new StringFormat { FormatFlags = StringFormatFlags.NoWrap })
                {
                    g.DrawString("无活跃会话", subFont, emptyBrush, new PointF(padding, y + S(2)), noWrap);
                }
            }
            else if (timeline)
            {
                DrawTimeline(
                    g,
                    CodexTaskPresentation.BuildTimeline(snapshot, nowLocal, this.CurrentSettings.CodexTaskBoardTimelineMinutes, visibleRows),
                    padding,
                    y,
                    rowFont,
                    subFont,
                    backgroundAlpha,
                    taskAlertsVisible);
            }
            else
            {
                DrawBubbleCards(g, rows, padding, y, rowFont, subFont, backgroundAlpha, taskAlertsVisible);
            }

            DrawFooter(g, badge, rows.Count, padding, footFont, backgroundAlpha);
            EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.CodexTask, this.LayerScale);
        }

        private Font GetCrispUiFont(float logicalSize, float minimumPhysicalSize, FontStyle style)
        {
            float scaled = Math.Max(minimumPhysicalSize, logicalSize * this.LayerScale);
            float physicalPixelSize = (float)Math.Ceiling(scaled - 0.001f);
            return this.fontCache.GetUi(physicalPixelSize, style);
        }

        private static float SnapPixel(float value)
        {
            return (float)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static float SnapTextTop(Graphics g, Font font, float centerY)
        {
            return SnapPixel(centerY - font.GetHeight(g) / 2.0f);
        }

        // Bubble card grid: one rich card per session, packed into 1 or 2 columns by board width.
        // Replaces the eight-column table and the narrow compact list with a single adaptive layout.
        private void DrawBubbleCards(
            Graphics g,
            IList<CodexTaskRowModel> rows,
            int padding,
            float y,
            Font rowFont,
            Font subFont,
            int backgroundAlpha,
            bool taskAlertsVisible)
        {
            int columns = BubbleColumns();
            int gap = BubbleCardGap();
            int cardHeight = BubbleCardHeight();
            int contentWidth = this.Width - padding * 2;
            int cardWidth = columns <= 1 ? contentWidth : (contentWidth - gap * (columns - 1)) / columns;
            Font tokenFont = GetCrispUiFont(6.4f, 6.0f, FontStyle.Regular);
            for (int i = 0; i < rows.Count; i++)
            {
                int col = i % columns;
                int gridRow = i / columns;
                float cardX = padding + col * (cardWidth + gap);
                float cardY = y + gridRow * (cardHeight + gap);
                DrawBubbleCard(
                    g,
                    rows[i],
                    new RectangleF(cardX, cardY, cardWidth, cardHeight),
                    rowFont,
                    subFont,
                    tokenFont,
                    backgroundAlpha,
                    taskAlertsVisible);
            }
        }

        private void DrawBubbleCard(
            Graphics g,
            CodexTaskRowModel row,
            RectangleF card,
            Font rowFont,
            Font subFont,
            Font tokenFont,
            int backgroundAlpha,
            bool taskAlertsVisible)
        {
            bool attention = taskAlertsVisible && row.NeedsAttention;
            Color statusColor = row.NeedsAttention && !taskAlertsVisible
                ? DesignTokens.Colors.GlyphMuted
                : row.StatusColor;
            float pad = S(7);

            using (GraphicsPath shell = RoundedRectangle(RectangleF.Inflate(card, -S(1) / 2.0f, -S(1) / 2.0f), S(7)))
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(
                DesignTokens.Colors.Surface, ScaleAlpha(attention ? 205 : 150, backgroundAlpha))))
            using (Pen border = new Pen(
                DesignTokens.WithAlpha(attention ? statusColor : DesignTokens.Colors.Border,
                    ScaleAlpha(attention ? 150 : 90, backgroundAlpha)),
                Math.Max(1.0f, this.LayerScale)))
            {
                g.FillPath(fill, shell);
                g.DrawPath(border, shell);
            }

            float tokenLineHeight = tokenFont.GetHeight(g) + S(2);
            float textTop = card.Top + pad;
            float textBottom = card.Bottom - pad - tokenLineHeight - S(2);
            float ringRadius = Math.Max(S(11), Math.Min(S(15), (textBottom - textTop) / 2.0f));
            float ringCenterX = card.Right - pad - ringRadius;
            float ringCenterY = textTop + (textBottom - textTop) / 2.0f;
            DrawWaterRing(
                g,
                ringCenterX,
                ringCenterY,
                ringRadius,
                row.ContextPercent,
                taskAlertsVisible ? row.ContextBarColor : DesignTokens.Colors.SuccessSoft,
                taskAlertsVisible && row.ContextCritical,
                subFont,
                backgroundAlpha);

            float textLeft = card.Left + pad;
            float textRight = ringCenterX - ringRadius - S(6);
            float textWidth = Math.Max(S(20), textRight - textLeft);
            float lineGap = S(1);
            float rowH = rowFont.GetHeight(g);
            float subH = subFont.GetHeight(g);

            float dot = Math.Max(3.0f, S(5));
            float line1Y = textTop;
            using (SolidBrush dotBrush = new SolidBrush(DesignTokens.WithAlpha(statusColor, ScaleAlpha(245, backgroundAlpha))))
            {
                g.FillEllipse(dotBrush, textLeft, line1Y + rowH / 2.0f - dot / 2.0f, dot, dot);
            }

            float nameLeft = textLeft + dot + S(4);
            using (SolidBrush nameBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Text, ScaleAlpha(240, backgroundAlpha))))
            using (StringFormat trim = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                g.DrawString(
                    "#" + row.TaskNumber.ToString(CultureInfo.InvariantCulture) + " " + row.WorkspaceLeaf,
                    rowFont,
                    nameBrush,
                    new RectangleF(nameLeft, line1Y, textRight - nameLeft, rowH),
                    trim);
            }

            float line2Y = line1Y + rowH + lineGap;
            string title = string.IsNullOrEmpty(row.Title) ? "—" : row.Title;
            using (SolidBrush titleBrush = new SolidBrush(DesignTokens.WithAlpha(
                DesignTokens.Colors.GlyphMuted, ScaleAlpha(string.IsNullOrEmpty(row.Title) ? 130 : 205, backgroundAlpha))))
            using (StringFormat trim = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                g.DrawString(title, subFont, titleBrush, new RectangleF(textLeft, line2Y, textWidth, subH), trim);
            }

            float line3Y = line2Y + subH + lineGap;
            using (SolidBrush statusBrush = new SolidBrush(DesignTokens.WithAlpha(statusColor, ScaleAlpha(232, backgroundAlpha))))
            using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ScaleAlpha(196, backgroundAlpha))))
            using (StringFormat noWrap = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                g.DrawString(row.StatusText, subFont, statusBrush, new PointF(textLeft, line3Y), noWrap);
                float statusWidth = g.MeasureString(row.StatusText, subFont).Width;
                string tail = " · " + row.DetailText + " · " + ShortenModel(row.Model);
                float tailLeft = textLeft + statusWidth;
                g.DrawString(tail, subFont, mutedBrush, new RectangleF(tailLeft, line3Y, Math.Max(S(10), textRight - tailLeft), subH), noWrap);
            }

            float tokenY = card.Bottom - pad - tokenLineHeight;
            using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, ScaleAlpha(48, backgroundAlpha)), Math.Max(1.0f, this.LayerScale * 0.6f)))
            {
                g.DrawLine(divider, textLeft, tokenY - S(1), card.Right - pad, tokenY - S(1));
            }

            string leftCluster = "入" + row.InputTokensText +
                (string.IsNullOrEmpty(row.CachedPercentText) ? string.Empty : " " + row.CachedPercentText) +
                "  出" + row.OutputTokensText + "  思" + row.ReasoningTokensText;
            string rightCluster = "轮" + row.LastTurnTokensText + " · 计" + row.TotalTokensText;
            using (SolidBrush tokenBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ScaleAlpha(180, backgroundAlpha))))
            using (SolidBrush tokenStrong = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, ScaleAlpha(180, backgroundAlpha))))
            using (StringFormat near = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            using (StringFormat far = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
            {
                float rightWidth = g.MeasureString(rightCluster, tokenFont).Width;
                float leftWidth = Math.Max(S(10), (card.Right - pad) - textLeft - rightWidth - S(6));
                g.DrawString(leftCluster, tokenFont, tokenBrush, new RectangleF(textLeft, tokenY, leftWidth, tokenLineHeight), near);
                g.DrawString(rightCluster, tokenFont, tokenStrong, new RectangleF(card.Right - pad - rightWidth, tokenY, rightWidth, tokenLineHeight), far);
            }
        }

        // Context water level as a ring: a full context window is a problem even on an idle session,
        // so the ramp (green/amber/red) is independent of the status palette. Percentage rides in the
        // hub and turns red at the critical threshold.
        private void DrawWaterRing(
            Graphics g,
            float centerX,
            float centerY,
            float radius,
            double percent,
            Color ringColor,
            bool critical,
            Font labelFont,
            int backgroundAlpha)
        {
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float thickness = Math.Max(2.0f, S(3.0f));
            RectangleF rect = new RectangleF(centerX - radius, centerY - radius, radius * 2.0f, radius * 2.0f);
            using (Pen track = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, ScaleAlpha(70, backgroundAlpha)), thickness))
            {
                g.DrawEllipse(track, rect);
            }

            float sweep = (float)(Math.Max(0.0, Math.Min(100.0, percent)) / 100.0 * 360.0);
            if (sweep > 0.5f)
            {
                using (Pen arc = new Pen(DesignTokens.WithAlpha(ringColor, ScaleAlpha(235, backgroundAlpha)), thickness))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, rect, -90.0f, sweep);
                }
            }

            g.SmoothingMode = previous;
            using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(
                critical ? DesignTokens.Colors.Danger : DesignTokens.Colors.GlyphMuted,
                ScaleAlpha(critical ? 245 : 210, backgroundAlpha))))
            using (StringFormat center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                g.DrawString(percent.ToString("0", CultureInfo.InvariantCulture), labelFont, labelBrush, rect, center);
            }
        }

        // "gpt-5.6-sol" -> "5.6-sol": the vendor prefix is constant across every row and only steals
        // width from the workspace column.
        private static string ShortenModel(string model)
        {
            string value = model ?? string.Empty;
            if (value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
            {
                return value.Substring(4);
            }

            return value.Length == 0 ? "--" : value;
        }

        private void DrawTimeline(
            Graphics g,
            CodexTaskTimelineModel model,
            int padding,
            float y,
            Font rowFont,
            Font subFont,
            int backgroundAlpha,
            bool taskAlertsVisible)
        {
            float labelWidth = S(112);
            float statusWidth = S(58);
            float laneLeft = padding + labelWidth + S(8);
            float laneRight = this.Width - padding - statusWidth - S(8);
            float laneWidth = Math.Max(S(40), laneRight - laneLeft);
            double totalSeconds = Math.Max(1.0, (model.EndLocal - model.StartLocal).TotalSeconds);

            using (SolidBrush axisBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ScaleAlpha(140, backgroundAlpha))))
            using (StringFormat near = new StringFormat { FormatFlags = StringFormatFlags.NoWrap })
            using (StringFormat far = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
            {
                Font axisFont = GetCrispUiFont(6.6f, 6.0f, FontStyle.Regular);
                g.DrawString(model.StartLocal.ToString("HH:mm", CultureInfo.CurrentCulture), axisFont, axisBrush, new PointF(laneLeft, y), near);
                g.DrawString("NOW", axisFont, axisBrush, new RectangleF(laneRight - S(30), y, S(30), S(11)), far);
            }

            y += S(11);
            for (int i = 0; i < model.Lanes.Count; i++)
            {
                CodexTaskTimelineLane lane = model.Lanes[i];
                float laneCenter = y + LaneHeight() / 2.0f;
                bool attention = taskAlertsVisible && lane.NeedsAttention;
                Color laneStatusColor = lane.NeedsAttention && !taskAlertsVisible
                    ? DesignTokens.Colors.GlyphMuted
                    : lane.StatusColor;
                using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(
                    attention ? DesignTokens.Colors.Text : DesignTokens.Colors.GlyphMuted,
                    ScaleAlpha(attention ? 236 : 200, backgroundAlpha))))
                using (SolidBrush statusBrush = new SolidBrush(DesignTokens.WithAlpha(laneStatusColor, ScaleAlpha(230, backgroundAlpha))))
                using (StringFormat far = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                using (StringFormat near = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                {
                    float textY = SnapTextTop(g, rowFont, laneCenter);
                    g.DrawString(
                        "#" + lane.TaskNumber.ToString(CultureInfo.InvariantCulture) + " " + lane.WorkspaceLeaf,
                        rowFont,
                        labelBrush,
                        new RectangleF(padding, textY, labelWidth, LaneHeight()),
                        far);
                    g.DrawString(
                        lane.StatusText,
                        subFont,
                        statusBrush,
                        new RectangleF(laneRight + S(8), SnapTextTop(g, subFont, laneCenter), statusWidth, LaneHeight()),
                        near);
                }

                float barHeight = Math.Max(5.0f, S(10));
                RectangleF track = new RectangleF(laneLeft, laneCenter - barHeight / 2.0f, laneWidth, barHeight);
                using (SolidBrush trackBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Border, ScaleAlpha(34, backgroundAlpha))))
                {
                    g.FillRectangle(trackBrush, track);
                }

                for (int s = 0; s < lane.Segments.Count; s++)
                {
                    CodexTaskTimelineSegment segment = lane.Segments[s];
                    double startOffset = (segment.StartLocal - model.StartLocal).TotalSeconds / totalSeconds;
                    double endOffset = (segment.EndLocal - model.StartLocal).TotalSeconds / totalSeconds;
                    float sx = laneLeft + (float)(startOffset * laneWidth);
                    float sw = Math.Max(1.0f, (float)((endOffset - startOffset) * laneWidth));
                    Color segmentColor = !taskAlertsVisible && CodexTaskPresentation.NeedsAttention(segment.Status)
                        ? DesignTokens.Colors.GlyphMuted
                        : CodexTaskPresentation.GetStatusColor(segment.Status);
                    using (SolidBrush segmentBrush = new SolidBrush(DesignTokens.WithAlpha(
                        segmentColor,
                        ScaleAlpha(225, backgroundAlpha))))
                    {
                        g.FillRectangle(segmentBrush, sx, track.Top, sw, barHeight);
                    }
                }

                y += LaneHeight();
            }

            if (model.Lanes.Count > 0 && model.Lanes[0].Segments.Count == 0)
            {
                using (SolidBrush hintBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ScaleAlpha(150, backgroundAlpha))))
                using (StringFormat near = new StringFormat { FormatFlags = StringFormatFlags.NoWrap })
                {
                    g.DrawString("正在积累活动历史…", subFont, hintBrush, new PointF(laneLeft, y + S(2)), near);
                }
            }
        }

        private void DrawFooter(
            Graphics g,
            CodexTaskBadgeModel badge,
            int visibleRows,
            int padding,
            Font footFont,
            int backgroundAlpha)
        {
            float footerTop = this.Height - padding - FooterHeight();
            RectangleF footer = new RectangleF(
                padding,
                footerTop,
                Math.Max(1.0f, this.Width - padding * 2.0f),
                FooterHeight());
            string summary = badge.HasTasks
                ? "共 " + badge.TaskCount.ToString(CultureInfo.InvariantCulture) +
                    (badge.TaskCount > visibleRows
                        ? " · 显示前 " + visibleRows.ToString(CultureInfo.InvariantCulture)
                        : string.Empty)
                : string.Empty;
            bool timeline = this.ActiveView == CodexTaskBoardView.Timeline;
            string viewText = timeline ? "卡片" : "时间线";
            CodexTaskFooterLayout layout = ComputeCodexTaskFooterLayout(
                footer,
                g.MeasureString(viewText, footFont).Width,
                g.MeasureString("关闭", footFont).Width,
                S(42),
                S(14),
                S(4),
                S(5));
            this.viewToggleRect = layout.ViewAction;
            this.closeButtonRect = layout.CloseAction;

            DrawFooterAction(
                g,
                this.viewToggleRect,
                viewText,
                DesignTokens.Colors.Success,
                footFont,
                backgroundAlpha);
            DrawFooterAction(
                g,
                this.closeButtonRect,
                "关闭",
                DesignTokens.Colors.Danger,
                footFont,
                backgroundAlpha);

            using (SolidBrush summaryBrush = new SolidBrush(DesignTokens.WithAlpha(
                DesignTokens.Colors.GlyphMuted,
                ScaleAlpha(180, backgroundAlpha))))
            using (StringFormat summaryFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                g.DrawString(summary, footFont, summaryBrush, layout.Summary, summaryFormat);
            }
        }

        private void DrawFooterAction(
            Graphics g,
            RectangleF rect,
            string text,
            Color semanticColor,
            Font footFont,
            int backgroundAlpha)
        {
            // Exact SpecBoard action language: restrained 4px corners, opaque Control fill,
            // semantic outline, and neutral text. The label states the action; no persistent active
            // glow or blue capsule competes with task status colours.
            using (GraphicsPath action = RoundedRectangle(RectangleF.Inflate(rect, -1.0f, -1.0f), S(4)))
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(
                DesignTokens.Colors.Control,
                ScaleAlpha(220, backgroundAlpha))))
            using (Pen border = new Pen(
                DesignTokens.WithAlpha(semanticColor, ScaleAlpha(170, backgroundAlpha)),
                Math.Max(1.0f, this.LayerScale)))
            using (SolidBrush actionText = new SolidBrush(DesignTokens.WithAlpha(
                DesignTokens.Colors.Text,
                ScaleAlpha(240, backgroundAlpha))))
            using (StringFormat center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                g.FillPath(fill, action);
                g.DrawPath(border, action);
                g.DrawString(text, footFont, actionText, rect, center);
            }
        }

        internal void SaveSample(string path, float scale)
        {
            SetLayerScale(scale);
            this.Size = GetDesiredSize();
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(255, 24, 24, 28));
                DrawWindowContent(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeDockTab();
                this.refreshTimer.Stop();
                this.refreshTimer.Tick -= OnRefreshTick;
                this.refreshTimer.Dispose();
                this.fontCache.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
