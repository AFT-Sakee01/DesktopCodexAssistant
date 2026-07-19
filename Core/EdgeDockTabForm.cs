using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal enum EdgeDockTabRole
{
    Network,
    SpecBoard,
    CodexTask,
    Guard
}

// A 5x30 logical right-pointing trapezoid parked against the left screen edge. It is the only
// always-visible part of a left-docked board: hovering it raises HoverEntered and the owner board
// expands next to it. Hover is detected by polling the cursor against the tab bounds rather than by
// relying on layered-alpha hit-testing, matching how SpecBoardForm already tracks the pointer and
// keeping the whole 5x30 rect (not just the opaque trapezoid) as the target.
internal sealed class EdgeDockTabForm : LayeredWidgetFormBase
{
    internal const int LogicalWidth = 5;
    internal const int LogicalHeight = 30;
    internal const int BoardAccentBorderLogicalThickness = 3;
    private const int HoverPollIntervalMs = 120;
    private const int NormalIdleFillAlpha = 104;
    private const int NormalIdleBorderAlpha = 168;
    private const int NormalHoverFillAlpha = 200;
    private const int NormalHoverBorderAlpha = 245;
    private const int HiddenIdleFillAlpha = 36;
    private const int HiddenIdleBorderAlpha = 84;
    private const int HiddenHoverFillAlpha = 148;
    private const int HiddenHoverBorderAlpha = 224;
    private const int ProtectedIdleFillAlpha = 18;
    private const int ProtectedIdleBorderAlpha = 72;
    private const int ProtectedHoverBorderAlpha = 96;
    private const int NormalIdleArrowAlpha = 72;
    private const int NormalHoverArrowAlpha = 96;
    private const int HiddenIdleArrowAlpha = 28;
    private const int ProtectedIdleArrowAlpha = 68;
    private const int ProtectedHoverArrowAlpha = 84;

    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly int burnInSalt;
    private readonly string logName;
    private readonly bool followsCodexTaskBoardTransparency;
    private Func<Point> cursorPositionProvider;
    private Color accent;
    private bool hovered;
    private bool boardExpanded;
    private bool displaySuspended;
    private long burnInSlot = long.MinValue;
    private int anchorCenterY;

    public event EventHandler HoverEntered;
    public event EventHandler HoverExited;
    public event EventHandler PollTick;

    public EdgeDockTabForm(WidgetSettings settings, Color accent, int burnInSalt, string logName, bool followsCodexTaskBoardTransparency)
    {
        this.accent = accent;
        this.burnInSalt = burnInSalt;
        this.logName = string.IsNullOrEmpty(logName) ? "EdgeDockTab" : logName;
        this.followsCodexTaskBoardTransparency = followsCodexTaskBoardTransparency;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        // The title remains visually hidden on this borderless tool window, but gives accessibility
        // and test clients a stable way to distinguish the four otherwise anonymous 5x30 tabs.
        this.Text = this.logName;
        this.AccessibleName = this.logName;
        this.BackColor = Color.Black;
        this.Cursor = Cursors.Hand;
        this.Size = GetDesiredSize();
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = HoverPollIntervalMs;
        this.hoverTimer.Tick += OnHoverTick;
    }

    protected override string LayeredWindowLogName
    {
        get { return this.logName; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get
        {
            return this.followsCodexTaskBoardTransparency
                ? this.CurrentSettings.CodexTaskBoardTransparencyOverridePercent
                : this.CurrentSettings.SpecBoardTransparencyOverridePercent;
        }
    }

    protected override int WindowScaleOverridePercent
    {
        get
        {
            return this.followsCodexTaskBoardTransparency
                ? this.CurrentSettings.CodexTaskBoardScaleOverridePercent
                : this.CurrentSettings.SpecBoardScaleOverridePercent;
        }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    protected override bool IsLayeredBurnInColorProtectionActive()
    {
        // A dock tab is an active control surface made almost entirely from one coloured shape.
        // The generic bitmap pass would invert that shape and can erase its anti-aliased neutral
        // edge. DrawWindowContent selects a low-energy neutral state directly instead.
        return false;
    }

    internal Func<Point> CursorPositionProviderForTest
    {
        set { this.cursorPositionProvider = value ?? (delegate { return Cursor.Position; }); }
    }

    private Size GetDesiredSize()
    {
        return new Size(S(LogicalWidth), S(LogicalHeight));
    }

    internal static Color ResolveQueueAccent(EdgeDockTabRole role)
    {
        switch (role)
        {
            case EdgeDockTabRole.Network:
                return DesignTokens.Colors.AccentAction;
            case EdgeDockTabRole.SpecBoard:
                return DesignTokens.Colors.WarningDeep;
            case EdgeDockTabRole.CodexTask:
                return DesignTokens.Colors.Success;
            case EdgeDockTabRole.Guard:
                return DesignTokens.Colors.AccentAlt;
            default:
                return DesignTokens.Colors.GlyphMuted;
        }
    }

    internal static float GetBoardAccentBorderStroke(float layerScale)
    {
        return Math.Max(1.0f, BoardAccentBorderLogicalThickness * Math.Max(0.1f, layerScale));
    }

    internal static RectangleF GetBoardAccentBorderBounds(Size size, float layerScale)
    {
        float stroke = GetBoardAccentBorderStroke(layerScale);
        float inset = stroke / 2.0f;
        return new RectangleF(
            inset,
            inset,
            Math.Max(1.0f, size.Width - stroke - 1.0f),
            Math.Max(1.0f, size.Height - stroke - 1.0f));
    }

    internal static void DrawBoardAccentBorder(Graphics g, Size size, EdgeDockTabRole role, float layerScale)
    {
        // Match the Radar software-family chrome: a 3 logical-pixel rounded inner stroke. Keeping
        // the stroke inside the layered bitmap prevents clipping and avoids changing board bounds.
        float stroke = GetBoardAccentBorderStroke(layerScale);
        float inset = stroke / 2.0f;
        RectangleF bounds = GetBoardAccentBorderBounds(size, layerScale);
        float radius = Math.Max(1.0f, DesignTokens.Radius.Panel * Math.Max(0.1f, layerScale) - inset);
        using (GraphicsPath path = RoundedRectangle(bounds, radius))
        using (Pen pen = new Pen(DesignTokens.WithAlpha(ResolveQueueAccent(role), 238), stroke))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, path);
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings, Color tabAccent)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        this.accent = tabAccent;
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        if (this.Visible)
        {
            PositionAtLeftEdge(this.anchorCenterY);
            RenderLayeredWindow();
        }
        else
        {
            InvalidateLayeredRenderBuffer();
        }
    }

    public void SetBoardExpanded(bool expanded)
    {
        if (this.boardExpanded == expanded)
        {
            return;
        }

        this.boardExpanded = expanded;
        if (this.Visible && !this.displaySuspended)
        {
            RenderLayeredWindow();
        }
        else
        {
            InvalidateLayeredRenderBuffer();
        }
    }

    // centerY is a screen coordinate; the tab is clamped into the work area so a stale setting can
    // never park it off-screen where it would be unreachable.
    public void ShowTab(int centerY)
    {
        this.anchorCenterY = centerY;
        this.Size = GetDesiredSize();
        PositionAtLeftEdge(centerY);
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

    public void HideTab()
    {
        this.hoverTimer.Stop();
        this.hovered = false;
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
            this.hoverTimer.Stop();
        }
        else if (this.Visible)
        {
            this.hoverTimer.Start();
            RenderLayeredWindow();
        }
    }

    private void PositionAtLeftEdge(int centerY)
    {
        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleOperation);
        int top = centerY - this.Height / 2;
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Height)));
        Point baseLocation = new Point(workArea.Left, top);
        // The tab is on screen permanently, so it still needs burn-in movement. Horizontal drift is
        // intentionally discarded: a positive runtime offset would move the 5px target away from
        // the physical edge, making a cursor parked at the leftmost pixel miss both dock tabs.
        Point runtimeLocation = BurnInProtection.ApplyRuntimeOffset(baseLocation, this.Size, workArea, this.burnInSalt);
        this.Location = PinToLeftEdge(runtimeLocation, workArea);
    }

    internal static Point PinToLeftEdge(Point runtimeLocation, Rectangle workArea)
    {
        return new Point(workArea.Left, runtimeLocation.Y);
    }

    public void RefreshBurnInPosition()
    {
        if (this.Visible && BurnInProtection.ShouldRefreshPosition(ref this.burnInSlot))
        {
            PositionAtLeftEdge(this.anchorCenterY);
        }
    }

    private void OnHoverTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        if (!this.Visible || this.displaySuspended)
        {
            return;
        }

        OutsideClickDismissalMonitor.Poll();
        EventHandler pollHandler = this.PollTick;
        if (pollHandler != null)
        {
            pollHandler(this, EventArgs.Empty);
        }

        RefreshBurnInPosition();
        bool inside = this.Bounds.Contains(this.cursorPositionProvider());
        if (inside == this.hovered)
        {
            return;
        }

        this.hovered = inside;
        RenderLayeredWindow();
        if (!inside)
        {
            EventHandler exitHandler = this.HoverExited;
            if (exitHandler != null)
            {
                exitHandler(this, EventArgs.Empty);
            }

            return;
        }

        EventHandler handler = this.HoverEntered;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }

    internal static GraphicsPath CreateTrapezoidPath(RectangleF bounds)
    {
        // Right-pointing: the full-height edge sits on the screen border and the shape narrows
        // toward the desktop, so it reads as a handle pulling the board out from the left.
        float inset = bounds.Height * 0.22f;
        GraphicsPath path = new GraphicsPath();
        path.AddPolygon(new PointF[]
        {
            new PointF(bounds.Left, bounds.Top),
            new PointF(bounds.Right, bounds.Top + inset),
            new PointF(bounds.Right, bounds.Bottom - inset),
            new PointF(bounds.Left, bounds.Bottom)
        });
        path.CloseFigure();
        return path;
    }

    internal static GraphicsPath CreateArrowPath(RectangleF bounds)
    {
        // The arrow stays inside the same layered bitmap as the trapezoid, so both shapes always
        // receive the exact same burn-in translation. Its small colour cue remains visible while a
        // protected collapsed trapezoid is neutral gray.
        float horizontalInset = Math.Max(0.2f, bounds.Width * 0.18f);
        float halfHeight = Math.Min(bounds.Height * 0.12f, Math.Max(2.0f, bounds.Width * 0.65f));
        float centerY = bounds.Top + bounds.Height / 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddPolygon(new PointF[]
        {
            new PointF(bounds.Left + horizontalInset, centerY - halfHeight),
            new PointF(bounds.Right - horizontalInset, centerY),
            new PointF(bounds.Left + horizontalInset, centerY + halfHeight)
        });
        path.CloseFigure();
        return path;
    }

    protected override void DrawWindowContent(Graphics g)
    {
        bool hiddenModeActive = this.CurrentSettings != null && this.CurrentSettings.ForceHoverOpacityActive;
        // Dock tabs are permanent OLED pixels. For this surface the existing colour-protection
        // switch applies even when global hidden opacity is not active: a collapsed tab stays gray
        // and only the small arrow carries identity until its board is actually visible.
        bool colorProtectionEnabled = this.CurrentSettings != null &&
            this.CurrentSettings.BurnInHiddenModeColorProtectionEnabled;
        DrawTab(
            g,
            new RectangleF(0.0f, 0.0f, this.Width, this.Height),
            this.LayerScale,
            ResolveVisualState(
                this.accent,
                this.hovered,
                hiddenModeActive,
                colorProtectionEnabled,
                this.boardExpanded));
    }

    private static void DrawTab(Graphics g, RectangleF bounds, float scale, TabVisualState visual)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = CreateTrapezoidPath(RectangleF.Inflate(bounds, -0.5f, -0.5f)))
        using (GraphicsPath arrowPath = CreateArrowPath(RectangleF.Inflate(bounds, -0.25f, -0.25f)))
        using (SolidBrush fill = new SolidBrush(visual.Fill))
        using (SolidBrush arrow = new SolidBrush(visual.Arrow))
        using (Pen border = new Pen(visual.Border, Math.Max(1.0f, scale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.FillPath(arrow, arrowPath);
        }
    }

    private static TabVisualState ResolveVisualState(
        Color accent,
        bool isHovered,
        bool hiddenModeActive,
        bool colorProtectionEnabled,
        bool boardExpanded)
    {
        if (colorProtectionEnabled)
        {
            if (!boardExpanded)
            {
                return new TabVisualState(
                    DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, ProtectedIdleFillAlpha),
                    DesignTokens.WithAlpha(
                        DesignTokens.Colors.GlyphMuted,
                        isHovered ? ProtectedHoverBorderAlpha : ProtectedIdleBorderAlpha),
                    DesignTokens.WithAlpha(
                        accent,
                        isHovered ? ProtectedHoverArrowAlpha : ProtectedIdleArrowAlpha));
            }

            return new TabVisualState(
                DesignTokens.WithAlpha(accent, isHovered ? NormalHoverFillAlpha : NormalIdleFillAlpha),
                DesignTokens.WithAlpha(accent, isHovered ? NormalHoverBorderAlpha : NormalIdleBorderAlpha),
                DesignTokens.WithAlpha(accent, isHovered ? NormalHoverArrowAlpha : NormalIdleArrowAlpha));
        }

        if (hiddenModeActive)
        {
            return new TabVisualState(
                DesignTokens.WithAlpha(accent, isHovered ? HiddenHoverFillAlpha : HiddenIdleFillAlpha),
                DesignTokens.WithAlpha(accent, isHovered ? HiddenHoverBorderAlpha : HiddenIdleBorderAlpha),
                DesignTokens.WithAlpha(accent, isHovered ? NormalHoverArrowAlpha : HiddenIdleArrowAlpha));
        }

        return new TabVisualState(
            DesignTokens.WithAlpha(accent, isHovered ? NormalHoverFillAlpha : NormalIdleFillAlpha),
            DesignTokens.WithAlpha(accent, isHovered ? NormalHoverBorderAlpha : NormalIdleBorderAlpha),
            DesignTokens.WithAlpha(accent, isHovered ? NormalHoverArrowAlpha : NormalIdleArrowAlpha));
    }

    private struct TabVisualState
    {
        public TabVisualState(Color fill, Color border, Color arrow)
        {
            this.Fill = fill;
            this.Border = border;
            this.Arrow = arrow;
        }

        public Color Fill;
        public Color Border;
        public Color Arrow;
    }

    internal static void RunSelfTest()
    {
        // Shape contract: 5x30 logical, full-height edge on the screen border, narrowing to the
        // right. If this ever inverts, the tab stops reading as a pull-handle.
        using (GraphicsPath path = CreateTrapezoidPath(new RectangleF(0.0f, 0.0f, LogicalWidth, LogicalHeight)))
        {
            RectangleF bounds = path.GetBounds();
            if (Math.Abs(bounds.Width - LogicalWidth) > 0.01f || Math.Abs(bounds.Height - LogicalHeight) > 0.01f)
            {
                throw new InvalidOperationException("Edge dock tab must stay within its 5x30 logical box.");
            }

            PointF[] points = path.PathPoints;
            if (points.Length != 4)
            {
                throw new InvalidOperationException("Edge dock tab should be a four-point trapezoid.");
            }

            float leftEdgeHeight = Math.Abs(points[3].Y - points[0].Y);
            float rightEdgeHeight = Math.Abs(points[2].Y - points[1].Y);
            if (leftEdgeHeight <= rightEdgeHeight)
            {
                throw new InvalidOperationException("Edge dock tab must point right: its left edge has to be the tall one.");
            }

            if (Math.Abs(points[0].X - 0.0f) > 0.01f || Math.Abs(points[1].X - LogicalWidth) > 0.01f)
            {
                throw new InvalidOperationException("Edge dock tab geometry changed.");
            }
        }

        using (GraphicsPath arrow = CreateArrowPath(new RectangleF(0.0f, 0.0f, LogicalWidth, LogicalHeight)))
        {
            RectangleF arrowBounds = arrow.GetBounds();
            if (arrow.PathPoints.Length != 3 ||
                arrowBounds.Left < 0.0f ||
                arrowBounds.Right > LogicalWidth ||
                arrowBounds.Top < 0.0f ||
                arrowBounds.Bottom > LogicalHeight ||
                Math.Abs(arrowBounds.Top + arrowBounds.Height / 2.0f - LogicalHeight / 2.0f) > 0.01f)
            {
                throw new InvalidOperationException("Edge dock arrow must be a centered in-bounds right triangle.");
            }

            PointF[] arrowPoints = arrow.PathPoints;
            if (arrowPoints[1].X <= arrowPoints[0].X)
            {
                throw new InvalidOperationException("Edge dock arrow must point right.");
            }
        }

        // Auto tab slots must not overlap. Reading down the edge: Network, Spec, Codex, Guard,
        // each 30px tall. Adding a fifth member means extending this check, not eyeballing it.
        Rectangle workArea = new Rectangle(0, 0, 1440, 900);
        int middle = workArea.Top + workArea.Height / 2;
        int networkCenter = middle - WidgetSettings.LeftDockTabAutoOffsetY * 3;
        int specCenter = middle - WidgetSettings.LeftDockTabAutoOffsetY;
        int codexCenter = middle + WidgetSettings.LeftDockTabAutoOffsetY;
        int guardCenter = middle + WidgetSettings.LeftDockTabAutoOffsetY * 3;
        Rectangle[] tabs = new Rectangle[]
        {
            new Rectangle(workArea.Left, networkCenter - LogicalHeight / 2, LogicalWidth, LogicalHeight),
            new Rectangle(workArea.Left, specCenter - LogicalHeight / 2, LogicalWidth, LogicalHeight),
            new Rectangle(workArea.Left, codexCenter - LogicalHeight / 2, LogicalWidth, LogicalHeight),
            new Rectangle(workArea.Left, guardCenter - LogicalHeight / 2, LogicalWidth, LogicalHeight)
        };

        for (int i = 0; i < tabs.Length; i++)
        {
            if (!workArea.Contains(tabs[i]))
            {
                throw new InvalidOperationException("Auto-placed dock tabs must stay inside the work area.");
            }

            for (int j = i + 1; j < tabs.Length; j++)
            {
                if (tabs[i].IntersectsWith(tabs[j]))
                {
                    throw new InvalidOperationException("Auto-placed dock tabs must not overlap.");
                }
            }
        }

        // Burn-in offsets include positive X values. Both primary and negative-coordinate monitor
        // work areas must discard that horizontal drift so their absolute leftmost pixel remains a
        // valid hover target, while preserving the intended vertical movement.
        Point primaryPinned = PinToLeftEdge(new Point(3, 432), workArea);
        Rectangle leftmostTab = new Rectangle(primaryPinned, new Size(LogicalWidth, LogicalHeight));
        if (primaryPinned != new Point(0, 432) || !leftmostTab.Contains(new Point(0, 440)))
        {
            throw new InvalidOperationException("Edge dock tab must remain reachable from the primary screen's leftmost pixel.");
        }

        Rectangle negativeWorkArea = new Rectangle(-1920, 0, 1920, 1080);
        Point negativePinned = PinToLeftEdge(new Point(-1917, 500), negativeWorkArea);
        if (negativePinned != new Point(-1920, 500))
        {
            throw new InvalidOperationException("Edge dock tab must pin to a negative-coordinate monitor's left edge.");
        }

        // Expanded boards share the tab-width horizontal anchor. Their independent salts may retain
        // vertical movement, but every salt must resolve to exactly the same X in docked mode.
        Point boardBaseLocation = new Point(workArea.Left + LogicalWidth, 250);
        Size boardSize = new Size(648, 400);
        int[] boardSalts = new int[]
        {
            BurnInProtection.NetworkMonitorSalt,
            BurnInProtection.SpecBoardSalt,
            BurnInProtection.CodexTaskBoardSalt,
            BurnInProtection.GuardBoardSalt
        };
        for (int i = 0; i < boardSalts.Length; i++)
        {
            Point shiftedBoard = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
                boardBaseLocation,
                boardSize,
                workArea,
                boardSalts[i]);
            if (shiftedBoard.X != boardBaseLocation.X ||
                shiftedBoard.Y < workArea.Top ||
                shiftedBoard.Y + boardSize.Height > workArea.Bottom)
            {
                throw new InvalidOperationException("Left-docked boards must share one X anchor while retaining bounded Y movement.");
            }
        }

        Color sampleAccent = ResolveQueueAccent(EdgeDockTabRole.Network);
        TabVisualState normalIdle = ResolveVisualState(sampleAccent, false, false, false, false);
        TabVisualState normalHover = ResolveVisualState(sampleAccent, true, false, false, false);
        TabVisualState hiddenIdle = ResolveVisualState(sampleAccent, false, true, false, false);
        TabVisualState hiddenHover = ResolveVisualState(sampleAccent, true, true, false, false);
        TabVisualState protectedIdle = ResolveVisualState(sampleAccent, false, false, true, false);
        TabVisualState protectedHover = ResolveVisualState(sampleAccent, true, false, true, false);
        TabVisualState protectedExpanded = ResolveVisualState(sampleAccent, false, false, true, true);
        TabVisualState protectedExpandedHover = ResolveVisualState(sampleAccent, true, true, true, true);
        if (hiddenIdle.Fill.A >= normalIdle.Fill.A || hiddenIdle.Border.A >= normalIdle.Border.A ||
            hiddenHover.Fill.A <= hiddenIdle.Fill.A || hiddenHover.Border.A <= hiddenIdle.Border.A ||
            normalHover.Fill.A <= normalIdle.Fill.A || normalHover.Border.A <= normalIdle.Border.A ||
            normalIdle.Arrow.A >= normalIdle.Fill.A || hiddenIdle.Arrow.A >= hiddenIdle.Fill.A)
        {
            throw new InvalidOperationException("Edge dock tab hidden and hover states must have a strict visual hierarchy.");
        }

        if (protectedIdle.Fill.R != DesignTokens.Colors.GlyphMuted.R ||
            protectedIdle.Fill.G != DesignTokens.Colors.GlyphMuted.G ||
            protectedIdle.Fill.B != DesignTokens.Colors.GlyphMuted.B ||
            protectedHover.Fill.R != DesignTokens.Colors.GlyphMuted.R ||
            protectedHover.Fill.G != DesignTokens.Colors.GlyphMuted.G ||
            protectedHover.Fill.B != DesignTokens.Colors.GlyphMuted.B ||
            protectedIdle.Arrow.R != sampleAccent.R ||
            protectedIdle.Arrow.G != sampleAccent.G ||
            protectedIdle.Arrow.B != sampleAccent.B ||
            protectedIdle.Arrow.A <= 0 ||
            protectedIdle.Arrow.A >= protectedIdle.Border.A ||
            protectedExpanded.Fill.R != sampleAccent.R ||
            protectedExpanded.Fill.G != sampleAccent.G ||
            protectedExpanded.Fill.B != sampleAccent.B ||
            protectedExpanded.Arrow.A >= protectedExpanded.Fill.A ||
            protectedExpandedHover.Fill.A <= protectedExpanded.Fill.A)
        {
            throw new InvalidOperationException("Edge dock tab burn-in state must stay gray until the board expands while preserving its arrow cue.");
        }

        Color[] queueAccents = new Color[]
        {
            ResolveQueueAccent(EdgeDockTabRole.Network),
            ResolveQueueAccent(EdgeDockTabRole.SpecBoard),
            ResolveQueueAccent(EdgeDockTabRole.CodexTask),
            ResolveQueueAccent(EdgeDockTabRole.Guard)
        };
        Color[] expectedAccents = new Color[]
        {
            DesignTokens.Colors.AccentAction,
            DesignTokens.Colors.WarningDeep,
            DesignTokens.Colors.Success,
            DesignTokens.Colors.AccentAlt
        };
        for (int i = 0; i < queueAccents.Length; i++)
        {
            if (queueAccents[i].ToArgb() != expectedAccents[i].ToArgb())
            {
                throw new InvalidOperationException("Edge dock queue colours must remain blue, orange, green and purple from top to bottom.");
            }
        }

        float borderStroke1x = GetBoardAccentBorderStroke(1.0f);
        float borderStroke2x = GetBoardAccentBorderStroke(2.0f);
        RectangleF borderBounds = GetBoardAccentBorderBounds(new Size(648, 400), 1.0f);
        if (Math.Abs(borderStroke1x - 3.0f) > 0.01f ||
            Math.Abs(borderStroke2x - 6.0f) > 0.01f ||
            borderBounds.Left - borderStroke1x / 2.0f < -0.01f ||
            borderBounds.Top - borderStroke1x / 2.0f < -0.01f ||
            borderBounds.Right + borderStroke1x / 2.0f > 648.01f ||
            borderBounds.Bottom + borderStroke1x / 2.0f > 400.01f)
        {
            throw new InvalidOperationException("Left-dock board accent borders must stay as a clipped-safe 3px inner stroke.");
        }

        Console.WriteLine("Edge dock tab: PASS 5x30 trapezoid arrow blue-orange-green-purple board-border-3px auto-slots-4 shared-pixel-shift protected-gray-expanded-color");
    }

    internal void SaveSample(string path, float scale)
    {
        SetLayerScale(scale);
        this.Size = GetDesiredSize();
        int gap = Math.Max(2, (int)Math.Round(2.0f * scale));
        TabVisualState[] states = new TabVisualState[]
        {
            ResolveVisualState(this.accent, false, false, false, false),
            ResolveVisualState(this.accent, true, false, false, false),
            ResolveVisualState(this.accent, false, true, false, false),
            ResolveVisualState(this.accent, false, false, true, false),
            ResolveVisualState(this.accent, false, false, true, true),
            ResolveVisualState(this.accent, true, true, true, true)
        };
        int sampleWidth = this.Width * states.Length + gap * (states.Length - 1);
        using (Bitmap bitmap = new Bitmap(sampleWidth, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(255, 24, 24, 28));
            for (int i = 0; i < states.Length; i++)
            {
                float left = i * (this.Width + gap);
                DrawTab(g, new RectangleF(left, 0.0f, this.Width, this.Height), this.LayerScale, states[i]);
            }

            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.hoverTimer.Stop();
            this.hoverTimer.Tick -= OnHoverTick;
            this.hoverTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
