using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// A 10x30 logical right-pointing trapezoid parked against the left screen edge. It is the only
// always-visible part of a left-docked board: hovering it raises HoverEntered and the owner board
// expands next to it. Hover is detected by polling the cursor against the tab bounds rather than by
// relying on layered-alpha hit-testing, matching how SpecBoardForm already tracks the pointer and
// keeping the whole 10x30 rect (not just the opaque trapezoid) as an easy target.
internal sealed class EdgeDockTabForm : LayeredWidgetFormBase
{
    internal const int LogicalWidth = 10;
    internal const int LogicalHeight = 30;
    private const int HoverPollIntervalMs = 120;

    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly int burnInSalt;
    private readonly string logName;
    private readonly bool followsCodexTaskBoardTransparency;
    private Func<Point> cursorPositionProvider;
    private Color accent;
    private bool hovered;
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
        // and test clients a stable way to distinguish the two otherwise anonymous 10x30 tabs.
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

    internal Func<Point> CursorPositionProviderForTest
    {
        set { this.cursorPositionProvider = value ?? (delegate { return Cursor.Position; }); }
    }

    private Size GetDesiredSize()
    {
        return new Size(S(LogicalWidth), S(LogicalHeight));
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
        // intentionally discarded: a positive runtime offset would move the 10px target away from
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

    protected override void DrawWindowContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF bounds = new RectangleF(0.0f, 0.0f, this.Width, this.Height);
        using (GraphicsPath path = CreateTrapezoidPath(RectangleF.Inflate(bounds, -0.5f, -0.5f)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(this.accent, this.hovered ? 200 : 104)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(this.accent, this.hovered ? 245 : 168), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
    }

    internal static void RunSelfTest()
    {
        // Shape contract: 10x30 logical, full-height edge on the screen border, narrowing to the
        // right. If this ever inverts, the tab stops reading as a pull-handle.
        using (GraphicsPath path = CreateTrapezoidPath(new RectangleF(0.0f, 0.0f, LogicalWidth, LogicalHeight)))
        {
            RectangleF bounds = path.GetBounds();
            if (Math.Abs(bounds.Width - LogicalWidth) > 0.01f || Math.Abs(bounds.Height - LogicalHeight) > 0.01f)
            {
                throw new InvalidOperationException("Edge dock tab must stay within its 10x30 logical box.");
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

        // Auto tab slots must not overlap: Spec sits above the middle, Codex below, 30px tall each.
        Rectangle workArea = new Rectangle(0, 0, 1440, 900);
        int specCenter = workArea.Top + workArea.Height / 2 - WidgetSettings.LeftDockTabAutoOffsetY;
        int codexCenter = workArea.Top + workArea.Height / 2 + WidgetSettings.LeftDockTabAutoOffsetY;
        Rectangle specTab = new Rectangle(workArea.Left, specCenter - LogicalHeight / 2, LogicalWidth, LogicalHeight);
        Rectangle codexTab = new Rectangle(workArea.Left, codexCenter - LogicalHeight / 2, LogicalWidth, LogicalHeight);
        if (specTab.IntersectsWith(codexTab))
        {
            throw new InvalidOperationException("Auto-placed dock tabs must not overlap.");
        }

        if (!workArea.Contains(specTab) || !workArea.Contains(codexTab))
        {
            throw new InvalidOperationException("Auto-placed dock tabs must stay inside the work area.");
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

        Console.WriteLine("Edge dock tab: PASS trapezoid-shape right-pointing auto-slots left-edge-pinned");
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
            this.hoverTimer.Stop();
            this.hoverTimer.Tick -= OnHoverTick;
            this.hoverTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
