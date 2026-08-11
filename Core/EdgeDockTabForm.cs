using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal enum EdgeDockTabRole
{
    Network,
    SpecBoard,
    CodexTask,
    Guard,
    CodexIq,
    ResetSpeed,
    SystemDay
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
    private const int NormalIdleArrowAlpha = 72;
    private const int NormalHoverArrowAlpha = 96;
    private const int BurnInIdleFillAlpha = 154;
    private const int BurnInIdleBorderAlpha = 214;
    private const int BurnInArrowAlpha = 245;
    // Keep the idle trapezoid visibly present without competing with the protected accent arrow.
    // These neutrals are deliberately darker than GlyphMuted; hover still restores the role colour.
    private static readonly Color BurnInIdleFillColor = Color.FromArgb(82, 88, 96);
    private static readonly Color BurnInIdleBorderColor = Color.FromArgb(104, 112, 122);

    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly int burnInSalt;
    private readonly string logName;
    private readonly EdgeDockTabRole settingsRole;
    private Func<Point> cursorPositionProvider;
    private Color accent;
    private bool hovered;
    private bool displaySuspended;
    private bool hiddenForFullscreen;
    private long burnInSlot = long.MinValue;
    private int anchorCenterY;
    private BurnInVisualLevel lastBurnInVisualLevel;

    public event EventHandler HoverEntered;
    public event EventHandler HoverExited;
    public event EventHandler PollTick;

    public EdgeDockTabForm(WidgetSettings settings, Color accent, int burnInSalt, string logName, EdgeDockTabRole settingsRole)
    {
        this.accent = accent;
        this.burnInSalt = burnInSalt;
        this.logName = string.IsNullOrEmpty(logName) ? "EdgeDockTab" : logName;
        this.settingsRole = settingsRole;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        this.lastBurnInVisualLevel = BurnInProtection.CurrentVisualLevel;
        ApplicationIcon.ApplyTo(this);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        // The title remains visually hidden on this borderless tool window, but gives accessibility
        // and test clients a stable way to distinguish the otherwise anonymous 5x30 tabs.
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
            return ResolveTransparencyOverride(this.CurrentSettings, this.settingsRole);
        }
    }

    protected override int WindowScaleOverridePercent
    {
        get
        {
            return ResolveScaleOverride(this.CurrentSettings, this.settingsRole);
        }
    }

    internal static int ResolveTransparencyOverride(WidgetSettings settings, EdgeDockTabRole role)
    {
        return LeftDockLayout.ResolveTransparencyOverride(settings, role);
    }

    internal static int ResolveScaleOverride(WidgetSettings settings, EdgeDockTabRole role)
    {
        return LeftDockLayout.ResolveScaleOverride(settings, role);
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen);
    }

    internal Func<Point> CursorPositionProviderForTest
    {
        set { this.cursorPositionProvider = value ?? (delegate { return Cursor.Position; }); }
    }

    private Size GetDesiredSize()
    {
        return LeftDockLayout.ResolveTabSize(this.CurrentSettings, this.settingsRole, this.LayerScale);
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
            case EdgeDockTabRole.CodexIq:
                return DesignTokens.Colors.Accent;
            case EdgeDockTabRole.ResetSpeed:
                return DesignTokens.Colors.Warning;
            case EdgeDockTabRole.SystemDay:
                return DesignTokens.Colors.WarningDeep;
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

        if (this.Visible && !LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            PositionAtLeftEdge(this.anchorCenterY);
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
        if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            this.hoverTimer.Stop();
            this.hovered = false;
            if (this.Visible)
            {
                Hide();
            }

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
            HideTab();
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
            HideTab();
        }
    }

    private void PositionAtLeftEdge(int centerY)
    {
        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
        // Automatic layout treats all enabled tabs as one column for burn-in movement, preserving
        // its custom order and exact gaps. Manual layout deliberately keeps the legacy per-tab salt.
        Point runtimeLocation = LeftDockLayout.ResolveTabRuntimeLocation(
            this.CurrentSettings,
            this.settingsRole,
            this.LayerScale,
            centerY,
            this.Size,
            this.burnInSalt);
        // Horizontal drift is always discarded: a positive runtime offset would move the 5px target
        // away from the physical edge, making a cursor at the leftmost pixel miss the dock tabs.
        this.Location = PinToLeftEdge(runtimeLocation, workArea);
    }

    internal static Point PinToLeftEdge(Point runtimeLocation, Rectangle workArea)
    {
        return new Point(workArea.Left, runtimeLocation.Y);
    }

    public void RefreshBurnInPosition()
    {
        if (this.Visible &&
            !LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen) &&
            BurnInProtection.ShouldRefreshPosition(ref this.burnInSlot))
        {
            PositionAtLeftEdge(this.anchorCenterY);
        }
    }

    private void OnHoverTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        if (!this.Visible || LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
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
        BurnInVisualLevel visualLevel = BurnInProtection.CurrentVisualLevel;
        bool visualLevelChanged = visualLevel != this.lastBurnInVisualLevel;
        this.lastBurnInVisualLevel = visualLevel;
        bool inside = this.Bounds.Contains(this.cursorPositionProvider());
        if (inside == this.hovered)
        {
            if (visualLevelChanged)
            {
                InvalidateLayeredRenderBuffer();
                RenderLayeredWindow();
            }

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
        // receive the exact same burn-in translation.
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
        DrawTab(
            g,
            new RectangleF(0.0f, 0.0f, this.Width, this.Height),
            this.LayerScale,
            ResolveVisualState(
                this.accent,
                this.hovered,
                BurnInProtection.CurrentVisualLevel));
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
        BurnInVisualLevel burnInVisualLevel)
    {
        BurnInVisualLevel level = BurnInProtection.NormalizeVisualLevel(burnInVisualLevel);
        if (level != BurnInVisualLevel.Normal)
        {
            Color arrowAccent = level == BurnInVisualLevel.LevelTwo
                ? BurnInProtection.InvertColor(accent)
                : accent;
            return new TabVisualState(
                isHovered
                    ? DesignTokens.WithAlpha(accent, NormalHoverFillAlpha)
                    : DesignTokens.WithAlpha(BurnInIdleFillColor, BurnInIdleFillAlpha),
                isHovered
                    ? DesignTokens.WithAlpha(accent, NormalHoverBorderAlpha)
                    : DesignTokens.WithAlpha(BurnInIdleBorderColor, BurnInIdleBorderAlpha),
                DesignTokens.WithAlpha(arrowAccent, BurnInArrowAlpha));
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
        LeftDockLayout.RunSelfTest();
        WidgetSettings roleSettings = WidgetSettings.CreateDefaults();
        roleSettings.NetworkMonitorTransparencyOverridePercent = 11;
        roleSettings.SpecBoardTransparencyOverridePercent = 22;
        roleSettings.CodexTaskBoardTransparencyOverridePercent = 33;
        roleSettings.GuardBoardTransparencyOverridePercent = 44;
        roleSettings.CodexIqBoardTransparencyOverridePercent = 55;
        roleSettings.ResetSpeedBoardTransparencyOverridePercent = 56;
        roleSettings.SystemDayBoardTransparencyOverridePercent = 57;
        roleSettings.NetworkMonitorScaleOverridePercent = 51;
        roleSettings.SpecBoardScaleOverridePercent = 62;
        roleSettings.CodexTaskBoardScaleOverridePercent = 73;
        roleSettings.GuardBoardScaleOverridePercent = 84;
        roleSettings.CodexIqBoardScaleOverridePercent = 95;
        roleSettings.ResetSpeedBoardScaleOverridePercent = 96;
        roleSettings.SystemDayBoardScaleOverridePercent = 97;
        if (ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.Network) != 11 ||
            ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.SpecBoard) != 22 ||
            ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.CodexTask) != 33 ||
            ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.Guard) != 44 ||
            ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.CodexIq) != 55 ||
            ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.ResetSpeed) != 56 ||
            ResolveTransparencyOverride(roleSettings, EdgeDockTabRole.SystemDay) != 57 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.Network) != 51 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.SpecBoard) != 62 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.CodexTask) != 73 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.Guard) != 84 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.CodexIq) != 95 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.ResetSpeed) != 96 ||
            ResolveScaleOverride(roleSettings, EdgeDockTabRole.SystemDay) != 97)
        {
            throw new InvalidOperationException("Edge dock tabs must use the visual override slots owned by their roles.");
        }

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

        // Mixed-scale auto queue geometry is covered by LeftDockLayout.RunSelfTest above.
        Rectangle workArea = new Rectangle(0, 0, 1440, 900);

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
            BurnInProtection.GuardBoardSalt,
            BurnInProtection.CodexIqBoardSalt,
            BurnInProtection.ResetSpeedBoardSalt,
            BurnInProtection.SystemDayBoardSalt
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
        TabVisualState normalIdle = ResolveVisualState(sampleAccent, false, BurnInVisualLevel.Normal);
        TabVisualState normalHover = ResolveVisualState(sampleAccent, true, BurnInVisualLevel.Normal);
        if (normalHover.Fill.A <= normalIdle.Fill.A ||
            normalHover.Border.A <= normalIdle.Border.A ||
            normalIdle.Arrow.A >= normalIdle.Fill.A)
        {
            throw new InvalidOperationException("Edge dock tab idle and hover states must have a strict visual hierarchy.");
        }

        TabVisualState levelOneIdle = ResolveVisualState(sampleAccent, false, BurnInVisualLevel.LevelOne);
        TabVisualState levelOneHover = ResolveVisualState(sampleAccent, true, BurnInVisualLevel.LevelOne);
        TabVisualState levelTwoIdle = ResolveVisualState(sampleAccent, false, BurnInVisualLevel.LevelTwo);
        if (levelOneIdle.Fill.R != BurnInIdleFillColor.R ||
            levelOneIdle.Fill.G != BurnInIdleFillColor.G ||
            levelOneIdle.Fill.B != BurnInIdleFillColor.B ||
            levelOneIdle.Border.R != BurnInIdleBorderColor.R ||
            levelOneIdle.Border.G != BurnInIdleBorderColor.G ||
            levelOneIdle.Border.B != BurnInIdleBorderColor.B ||
            levelOneIdle.Fill.R + levelOneIdle.Fill.G + levelOneIdle.Fill.B >=
                DesignTokens.Colors.GlyphMuted.R + DesignTokens.Colors.GlyphMuted.G + DesignTokens.Colors.GlyphMuted.B ||
            levelOneIdle.Arrow.R != sampleAccent.R ||
            levelOneIdle.Arrow.G != sampleAccent.G ||
            levelOneIdle.Arrow.B != sampleAccent.B ||
            levelOneHover.Fill.R != sampleAccent.R ||
            levelOneHover.Fill.G != sampleAccent.G ||
            levelOneHover.Fill.B != sampleAccent.B ||
            levelTwoIdle.Arrow.R != 255 - sampleAccent.R ||
            levelTwoIdle.Arrow.G != 255 - sampleAccent.G ||
            levelTwoIdle.Arrow.B != 255 - sampleAccent.B)
        {
            throw new InvalidOperationException("Edge dock burn-in levels must keep a dark-grey idle trapezoid, restore hover colour, and invert only the level-two arrow accent.");
        }

        Color[] queueAccents = new Color[]
        {
            ResolveQueueAccent(EdgeDockTabRole.Network),
            ResolveQueueAccent(EdgeDockTabRole.SpecBoard),
            ResolveQueueAccent(EdgeDockTabRole.CodexTask),
            ResolveQueueAccent(EdgeDockTabRole.Guard),
            ResolveQueueAccent(EdgeDockTabRole.CodexIq),
            ResolveQueueAccent(EdgeDockTabRole.ResetSpeed),
            ResolveQueueAccent(EdgeDockTabRole.SystemDay)
        };
        Color[] expectedAccents = new Color[]
        {
            DesignTokens.Colors.AccentAction,
            DesignTokens.Colors.WarningDeep,
            DesignTokens.Colors.Success,
            DesignTokens.Colors.AccentAlt,
            DesignTokens.Colors.Accent,
            DesignTokens.Colors.Warning,
            DesignTokens.Colors.WarningDeep
        };
        for (int i = 0; i < queueAccents.Length; i++)
        {
            if (queueAccents[i].ToArgb() != expectedAccents[i].ToArgb())
            {
                throw new InvalidOperationException("Edge dock queue colours must retain seven stable role accents.");
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

        Console.WriteLine("Edge dock tab: PASS 5x30 trapezoid arrow seven-role accents burn-in-gray-hover level2-inverted-arrow board-border-3px auto-slots-7 shared-pixel-shift");
    }

    internal static void RunDisplayLifecycleSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
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
        for (int i = 0; i < roles.Length; i++)
        {
            using (EdgeDockTabForm tab = new EdgeDockTabForm(
                settings,
                ResolveQueueAccent(roles[i]),
                BurnInProtection.NetworkMonitorDockTabSalt + i,
                "EdgeDockLifecycleTest" + i.ToString(),
                roles[i]))
            {
                int center = LeftDockLayout.ResolveTabCenterY(settings, roles[i], tab.LayerScale);
                tab.ShowTab(center);
                Application.DoEvents();
                if (!tab.Visible || !tab.hoverTimer.Enabled)
                {
                    throw new InvalidOperationException("Edge dock lifecycle initial show failed.");
                }

                tab.SetDisplaySuspended(true);
                tab.ShowTab(center);
                Application.DoEvents();
                if (tab.Visible || tab.hoverTimer.Enabled)
                {
                    throw new InvalidOperationException("Edge dock lifecycle suspend guard failed.");
                }

                tab.SetDisplaySuspended(false);
                if (tab.Visible || tab.hoverTimer.Enabled)
                {
                    throw new InvalidOperationException("Edge dock lifecycle resume must require an explicit show.");
                }

                tab.ShowTab(center);
                tab.SetHiddenForFullscreen(true);
                tab.ShowTab(center);
                Application.DoEvents();
                if (tab.Visible || tab.hoverTimer.Enabled)
                {
                    throw new InvalidOperationException("Edge dock lifecycle fullscreen guard failed.");
                }

                tab.SetHiddenForFullscreen(false);
                if (tab.Visible || tab.hoverTimer.Enabled)
                {
                    throw new InvalidOperationException("Edge dock fullscreen exit must require an explicit show.");
                }

                tab.ShowTab(center);
                if (!tab.Visible || !tab.hoverTimer.Enabled)
                {
                    throw new InvalidOperationException("Edge dock lifecycle explicit recovery show failed.");
                }

                tab.HideTab();
            }
        }
    }

    internal void SaveSample(string path, float scale)
    {
        SetLayerScale(scale);
        this.Size = GetDesiredSize();
        int gap = Math.Max(2, (int)Math.Round(2.0f * scale));
        TabVisualState[] states = new TabVisualState[]
        {
            ResolveVisualState(this.accent, false, BurnInVisualLevel.Normal),
            ResolveVisualState(this.accent, true, BurnInVisualLevel.Normal),
            ResolveVisualState(this.accent, false, BurnInVisualLevel.LevelOne),
            ResolveVisualState(this.accent, true, BurnInVisualLevel.LevelOne),
            ResolveVisualState(this.accent, false, BurnInVisualLevel.LevelTwo),
            ResolveVisualState(this.accent, true, BurnInVisualLevel.LevelTwo)
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
