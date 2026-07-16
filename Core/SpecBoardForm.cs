using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    private Func<Point> cursorPositionProvider;
    private FileSystemWatcher watcher;
    private SpecBoardSnapshot snapshot = new SpecBoardSnapshot();
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
        this.Text = "Spec Board";
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

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
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
            DisposeWatcher();
            DisposeProjectWatchers();
            this.autoPopupKnownRows.Clear();
            this.autoPopupHighlightedRows.Clear();
            this.autoPopupBaselineInitialized = false;
            this.autoPopupActive = false;
        }

        if (this.Visible)
        {
            PositionNearOperationPanel();
            ResetAutoHideClock();
            RenderLayeredWindow();
        }
        else
        {
            InvalidateLayeredRenderBuffer();
        }

        UpdateMonitoringState();
        if (ShouldMonitorWork() && (ledgerPathChanged || !oldAutoPopupEnabled && this.CurrentSettings.SpecBoardAutoPopupEnabled))
        {
            RequestRefresh(true);
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
        if (this.owner != null)
        {
            this.owner.PrepareForSpecBoardOverlayShow();
        }

        this.autoPopupActive = automaticPopup;
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
        PositionNearOperationPanel();
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
        if (hidden)
        {
            this.restoreAfterFullscreen = this.Visible;
            this.restoreAutoPopupAfterFullscreen = this.autoPopupActive;
            HideBoard();
        }
        else if (this.restoreAfterFullscreen)
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
        SuspendVisibleWork();
        ResetDisplayRenderResources();
    }

    public void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        ResetDisplayRenderResources();
        if (ShouldMonitorWork())
        {
            ResumeVisibleWork();
            if (this.Visible)
            {
                PositionNearOperationPanel();
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
        BurnInProtection.ConfigureGraphics(g, false);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        this.projectHitTargets.Clear();
        this.cardHitTargets.Clear();
        this.managerButtonBounds = Rectangle.Empty;
        this.closeButtonBounds = Rectangle.Empty;

        OperationRenderVariant variant = this.CurrentSettings == null ? OperationRenderVariant.Classic : this.CurrentSettings.OperationRenderVariant;
        if (IsOledVariant(variant))
        {
            // ClearType encodes blue/red subpixel fringes into layered bitmaps. OLED variants
            // use grayscale antialiasing so their no-blue palette remains true pixel-for-pixel.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        SpecBoardPalette palette = GetPalette(variant);
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 238)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }

        DrawBoard(g, palette, true);
        DrawCopySuccessNotice(g, palette);
    }

    private void DrawCopySuccessNotice(Graphics g, SpecBoardPalette palette)
    {
        if (string.IsNullOrEmpty(this.copySuccessNotice) || DateTime.UtcNow >= this.copySuccessNoticeUntilUtc)
        {
            return;
        }

        Font font = this.fontCache.GetUi(S(8.4f), FontStyle.Bold);
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
        Font headerFont = this.fontCache.GetUi(S(12.0f), FontStyle.Bold);
        Font countFont = this.fontCache.GetMono(S(9.0f), FontStyle.Bold);
        Font bodyFont = this.fontCache.GetUi(S(9.0f), FontStyle.Regular);
        Font bodyBold = this.fontCache.GetUi(S(9.2f), FontStyle.Bold);
        Font smallFont = this.fontCache.GetUi(S(7.8f), FontStyle.Regular);
        Font smallBold = this.fontCache.GetUi(S(7.8f), FontStyle.Bold);
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
        int leftWidth = Math.Max(S(112), (int)Math.Round(content.Width * 0.37));
        Rectangle left = new Rectangle(content.Left, columnsTop, leftWidth, columnsHeight);
        Rectangle right = new Rectangle(left.Right + S(7), columnsTop, Math.Max(1, content.Right - left.Right - S(7)), columnsHeight);

        DrawHeader(g, header, headerFont, countFont, palette);
        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 112), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(divider, left.Right + S(3), left.Top, left.Right + S(3), left.Bottom);
        }

        DrawProjectRail(g, left, footerHeight, projectRowHeight, bodyFont, smallFont, palette, recordHitTargets);
        DrawActionFlow(g, right, segmentHeight, cardHeight, cardGap, bodyBold, smallFont, smallBold, palette, recordHitTargets);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font headerFont, Font countFont, SpecBoardPalette palette)
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
            g.DrawString("SPEC BOARD", headerFont, text, bounds, left);
            string time = this.snapshot.LedgerLastWriteLocal.HasValue ? this.snapshot.LedgerLastWriteLocal.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "--:--";
            float timeWidth = g.MeasureString(time, countFont).Width;
            RectangleF timeRect = new RectangleF(bounds.Right - timeWidth, bounds.Top, timeWidth, bounds.Height);
            g.DrawString(time, countFont, text, timeRect, right);
            float x = timeRect.Left - S(8);
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

    private void DrawProjectRail(Graphics g, Rectangle bounds, int footerHeight, int rowHeight, Font bodyFont, Font smallFont, SpecBoardPalette palette, bool recordHitTargets)
    {
        Rectangle footer = new Rectangle(bounds.Left, Math.Max(bounds.Top, bounds.Bottom - footerHeight), bounds.Width, footerHeight);
        int availableRowsHeight = Math.Max(0, footer.Top - bounds.Top - S(3));
        List<SpecBoardProject> projects = this.snapshot.Projects;
        int totalRows = projects.Count + 1;
        int maxRows = rowHeight <= 0 ? 0 : availableRowsHeight / rowHeight;
        bool needsMore = totalRows > maxRows;
        int rowsToDraw = Math.Min(totalRows, needsMore ? Math.Max(0, maxRows - 1) : maxRows);
        int y = bounds.Top;
        for (int i = 0; i < rowsToDraw; i++)
        {
            string project = i == 0 ? string.Empty : projects[i - 1].Name;
            string display = i == 0 ? "全部" : projects[i - 1].Display;
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

            int redCount = this.snapshot.Count(project, SpecBoardStatus.Pending) + this.snapshot.Count(project, SpecBoardStatus.Unregistered);
            int revisionCount = this.snapshot.Count(project, SpecBoardStatus.NeedsRevision);
            int yellowCount = this.snapshot.Count(project, SpecBoardStatus.AwaitingVerify);
            string countText = redCount == 0 && revisionCount == 0 && yellowCount == 0
                ? "✓"
                : redCount.ToString(CultureInfo.InvariantCulture) + "/" + revisionCount.ToString(CultureInfo.InvariantCulture) + "/" + yellowCount.ToString(CultureInfo.InvariantCulture);
            bool fresh = !string.IsNullOrEmpty(project) && this.seenStateStore != null && this.seenStateStore.IsFresh(project, this.snapshot);
            using (SolidBrush labelBrush = new SolidBrush(selected ? palette.Text : palette.Muted))
            using (StringFormat labelFormat = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
            {
                float countWidth = g.MeasureString(countText, bodyFont).Width + S(6);
                int freshWidth = fresh ? S(10) : 0;
                RectangleF labelRect = new RectangleF(row.Left + S(4) + freshWidth, row.Top, Math.Max(1, row.Width - countWidth - S(8) - freshWidth), row.Height);
                g.DrawString(display, bodyFont, labelBrush, labelRect, labelFormat);
                if (fresh)
                {
                    using (SolidBrush freshBrush = new SolidBrush(DesignTokens.Colors.Accent))
                    {
                        float diameter = S(6);
                        g.FillEllipse(freshBrush, row.Left + S(3), row.Top + (row.Height - diameter) / 2.0f, diameter, diameter);
                    }
                }

                DrawProjectCounts(g, row, bodyFont, redCount, revisionCount, yellowCount, palette);
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

        string footerText = "✓" + this.snapshot.Count(string.Empty, SpecBoardStatus.Done).ToString(CultureInfo.InvariantCulture) + " · ×" + this.snapshot.Count(string.Empty, SpecBoardStatus.Abandoned).ToString(CultureInfo.InvariantCulture);
        int warnings = this.snapshot.MalformedLines + (this.snapshot.ProjectRegistryAvailable ? 0 : 1) + (this.snapshot.ReconciliationTimedOut ? 1 : 0);
        if (warnings > 0)
        {
            footerText += "  ⚠" + warnings.ToString(CultureInfo.InvariantCulture);
        }
        int managerWidth = Math.Min(footer.Width, Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("管理", smallFont).Width) + S(14)));
        int closeWidth = Math.Min(footer.Width, Math.Max(S(42), (int)Math.Ceiling(g.MeasureString("关闭", smallFont).Width) + S(14)));
        Rectangle managerBounds = new Rectangle(footer.Left, footer.Top, managerWidth, footer.Height);
        Rectangle closeBounds = new Rectangle(managerBounds.Right + S(4), footer.Top, closeWidth, footer.Height);
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

    private void DrawActionFlow(Graphics g, Rectangle bounds, int segmentHeight, int cardHeight, int cardGap, Font titleFont, Font smallFont, Font segmentFont, SpecBoardPalette palette, bool recordHitTargets)
    {
        if (this.snapshot.LedgerMissing)
        {
            DrawCenteredEmptyState(g, bounds, "账本未找到", this.snapshot.LedgerPath, palette.Danger, palette.Muted, titleFont, smallFont);
            return;
        }

        List<SpecBoardRow> actionable = this.snapshot.Rows
            .Where(row => (string.IsNullOrEmpty(this.selectedProject) || string.Equals(row.Project, this.selectedProject, StringComparison.OrdinalIgnoreCase)) &&
                (row.Status == SpecBoardStatus.Unregistered || row.Status == SpecBoardStatus.Pending ||
                 row.Status == SpecBoardStatus.NeedsRevision || row.Status == SpecBoardStatus.AwaitingVerify))
            .ToList();
        if (actionable.Count == 0)
        {
            DrawCenteredEmptyState(g, bounds, "没有待办 spec ✓", string.Empty, palette.Success, palette.Muted, titleFont, smallFont);
            return;
        }

        int nonEmptySections = new[] { SpecBoardStatus.Unregistered, SpecBoardStatus.Pending, SpecBoardStatus.NeedsRevision, SpecBoardStatus.AwaitingVerify }
            .Count(status => actionable.Any(row => string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase)));
        if (nonEmptySections >= 4)
        {
            // Four actionable states must each retain one complete card in the fixed-height board.
            // Derive compact dimensions from the measured right-column height instead of dropping
            // an earlier section or relying on guessed absolute Y coordinates.
            int slotHeight = Math.Max(1, bounds.Height / nonEmptySections);
            cardGap = Math.Min(cardGap, Math.Max(1, S(2)));
            segmentHeight = Math.Min(segmentHeight, Math.Max(S(9), slotHeight / 3));
            cardHeight = Math.Min(cardHeight, Math.Max(S(20), slotHeight - segmentHeight - cardGap));
        }

        int y = bounds.Top;
        int sectionMinimum = segmentHeight + cardHeight + cardGap;
        int pendingReserve = actionable.Any(row => row.Status == SpecBoardStatus.Pending) ? sectionMinimum : 0;
        int revisionReserve = actionable.Any(row => row.Status == SpecBoardStatus.NeedsRevision) ? sectionMinimum : 0;
        int awaitingReserve = actionable.Any(row => row.Status == SpecBoardStatus.AwaitingVerify) ? sectionMinimum : 0;
        DrawSection(g, bounds, ref y, actionable, SpecBoardStatus.Unregistered, "◆ 未登记", palette.Unregistered, segmentHeight, cardHeight, cardGap, pendingReserve + revisionReserve + awaitingReserve, titleFont, smallFont, segmentFont, palette, recordHitTargets);
        DrawSection(g, bounds, ref y, actionable, SpecBoardStatus.Pending, "◆ 需要执行", palette.Danger, segmentHeight, cardHeight, cardGap, revisionReserve + awaitingReserve, titleFont, smallFont, segmentFont, palette, recordHitTargets);
        DrawSection(g, bounds, ref y, actionable, SpecBoardStatus.NeedsRevision, "◆ 需要修改", palette.Revision, segmentHeight, cardHeight, cardGap, awaitingReserve, titleFont, smallFont, segmentFont, palette, recordHitTargets);
        DrawSection(g, bounds, ref y, actionable, SpecBoardStatus.AwaitingVerify, "◆ 等待验证", palette.Warning, segmentHeight, cardHeight, cardGap, 0, titleFont, smallFont, segmentFont, palette, recordHitTargets);
    }

    private void DrawSection(Graphics g, Rectangle bounds, ref int y, List<SpecBoardRow> allRows, string status, string label, Color statusColor, int segmentHeight, int cardHeight, int cardGap, int reservedBottomHeight, Font titleFont, Font smallFont, Font segmentFont, SpecBoardPalette palette, bool recordHitTargets)
    {
        List<SpecBoardRow> rows = allRows.Where(row => string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.EventTimeUtc ?? DateTime.MaxValue)
            .ToList();
        int localBottom = Math.Max(y, bounds.Bottom - Math.Max(0, reservedBottomHeight));
        if (rows.Count == 0 || y + segmentHeight > localBottom)
        {
            return;
        }

        int headerTop = y;
        using (SolidBrush brush = new SolidBrush(statusColor))
        using (StringFormat format = CreateStringFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(label + " · " + rows.Count.ToString(CultureInfo.InvariantCulture), segmentFont, brush, new Rectangle(bounds.Left, y, bounds.Width, segmentHeight), format);
        }

        y += segmentHeight;
        int remainingHeight = localBottom - y;
        int capacity = Math.Max(0, (remainingHeight + cardGap) / (cardHeight + cardGap));
        int drawCount = Math.Min(rows.Count, capacity);
        bool hasMore = drawCount < rows.Count;
        int moreHeight = MeasureLineHeight(g, smallFont, S(2));
        if (hasMore && drawCount > 1 && y + drawCount * (cardHeight + cardGap) + moreHeight > localBottom)
        {
            drawCount--;
        }

        for (int i = 0; i < drawCount; i++)
        {
            Rectangle card = new Rectangle(bounds.Left, y, bounds.Width, cardHeight);
            DrawCard(g, card, rows[i], statusColor, titleFont, smallFont, palette);
            if (recordHitTargets)
            {
                this.cardHitTargets.Add(new CardHitTarget { Bounds = card, Row = rows[i] });
            }

            y += cardHeight + cardGap;
        }

        if (drawCount < rows.Count && y + moreHeight <= localBottom)
        {
            using (SolidBrush muted = new SolidBrush(palette.Muted))
            using (StringFormat format = CreateStringFormat(StringAlignment.Near, StringTrimming.None))
            {
                g.DrawString("+" + (rows.Count - drawCount).ToString(CultureInfo.InvariantCulture) + " 更多", smallFont, muted, new Rectangle(bounds.Left + S(5), y, bounds.Width - S(5), moreHeight), format);
            }

            y += moreHeight + cardGap;
        }
        else if (drawCount < rows.Count)
        {
            using (SolidBrush muted = new SolidBrush(palette.Muted))
            using (StringFormat right = CreateStringFormat(StringAlignment.Far, StringTrimming.None))
            {
                g.DrawString("+" + (rows.Count - drawCount).ToString(CultureInfo.InvariantCulture), smallFont, muted, new Rectangle(bounds.Left, headerTop, bounds.Width, segmentHeight), right);
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
        if (!ShouldMonitorWork())
        {
            SuspendVisibleWork();
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool renderNeeded = ExpireCopySuccessNotice(now);
        if (this.autoPopupHighlightUntilUtc != DateTime.MinValue && now >= this.autoPopupHighlightUntilUtc)
        {
            this.autoPopupHighlightUntilUtc = DateTime.MinValue;
            this.autoPopupHighlightedRows.Clear();
            renderNeeded = true;
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
            PositionNearOperationPanel();
        }
    }

    private void RequestRefresh(bool reconcile)
    {
        if (Interlocked.CompareExchange(ref this.refreshRunning, 1, 0) != 0)
        {
            Interlocked.Exchange(ref this.refreshQueued, reconcile ? 2 : 1);
            return;
        }

        string ledgerPath = this.CurrentSettings.SpecBoardLedgerPath;
        Task.Run(delegate
        {
            SpecBoardSnapshot basic = SpecBoardReader.Read(ledgerPath, false);
            if (!reconcile || basic.LedgerMissing || !basic.ProjectRegistryAvailable)
            {
                return basic;
            }

            Task<SpecBoardSnapshot> full = Task.Run(() => SpecBoardReader.Read(ledgerPath, true));
            if (full.Wait(ReconcileTimeoutMs))
            {
                return full.Result;
            }

            basic.ReconciliationTimedOut = true;
            return basic;
        }).ContinueWith(task =>
        {
            SpecBoardSnapshot result = task.Status == TaskStatus.RanToCompletion ? task.Result : SpecBoardReader.Read(ledgerPath, false);
            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((Action)delegate
                    {
                        ApplyRefreshResult(result);
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref this.refreshRunning, 0);
                int queued = Interlocked.Exchange(ref this.refreshQueued, 0);
                if (queued != 0 && !this.IsDisposed)
                {
                    try
                    {
                        if (this.IsHandleCreated)
                        {
                            this.BeginInvoke((Action)(() => RequestRefresh(queued == 2)));
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        });
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
        return !this.displaySuspended && !this.hiddenForFullscreen &&
            (this.Visible || this.CurrentSettings != null && this.CurrentSettings.SpecBoardAutoPopupEnabled);
    }

    private void SuspendVisibleWork()
    {
        this.maintenanceTimer.Stop();
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
        this.Location = BurnInProtection.ApplyRuntimeOffset(baseLocation, this.Size, workArea, 73);
    }

    private void OpenRow(SpecBoardRow row)
    {
        string path = row == null ? string.Empty : row.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string target = File.Exists(path) && !row.FileMissing ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target) && !File.Exists(target))
        {
            target = row.ProjectRoot;
        }

        if (string.IsNullOrEmpty(target) || !Directory.Exists(target) && !File.Exists(target))
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
            SuspendVisibleWork();
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

    private static SpecBoardPalette GetPalette(OperationRenderVariant variant)
    {
        if (variant == OperationRenderVariant.Typographic)
        {
            return new SpecBoardPalette(DesignTokens.OledTypographic.Primary, DesignTokens.OledTypographic.Muted, DesignTokens.OledTypographic.AccentDanger, DesignTokens.OledTypographic.AccentWarn, DesignTokens.OledTypographic.AccentGood, DesignTokens.OledTypographic.AccentWarn, Color.FromArgb(232, 126, 220));
        }

        if (variant == OperationRenderVariant.AmberHud)
        {
            return new SpecBoardPalette(DesignTokens.OledAmber.Bright, DesignTokens.OledAmber.Dim, DesignTokens.OledAmber.Danger, DesignTokens.OledAmber.Base, DesignTokens.OledAmber.Bright, DesignTokens.OledAmber.Base, Color.FromArgb(255, 160, 96));
        }

        if (variant == OperationRenderVariant.WarmCard)
        {
            return new SpecBoardPalette(DesignTokens.OledCard.Text, DesignTokens.OledCard.Muted, DesignTokens.OledCard.DotDanger, DesignTokens.OledCard.DotWarn, DesignTokens.OledCard.DotGood, DesignTokens.OledCard.DotWarn, Color.FromArgb(230, 140, 210));
        }

        if (variant == OperationRenderVariant.Phosphor)
        {
            return new SpecBoardPalette(DesignTokens.OledPhosphor.Bright, DesignTokens.OledPhosphor.Dim, DesignTokens.OledPhosphor.Danger, DesignTokens.OledPhosphor.Warn, DesignTokens.OledPhosphor.Base, DesignTokens.OledPhosphor.Warn, Color.FromArgb(210, 150, 200));
        }

        return new SpecBoardPalette(DesignTokens.Colors.TextStrong, DesignTokens.Colors.GlyphMuted, DesignTokens.Colors.Danger, DesignTokens.Colors.Warning, DesignTokens.Colors.Success, DesignTokens.Colors.WarningDeep, DesignTokens.Colors.AccentAlt);
    }

    private static bool IsOledVariant(OperationRenderVariant variant)
    {
        return variant == OperationRenderVariant.Typographic ||
            variant == OperationRenderVariant.AmberHud ||
            variant == OperationRenderVariant.WarmCard ||
            variant == OperationRenderVariant.Phosphor;
    }

    internal static void RenderSamples(string outputDir, bool sample, bool current)
    {
        Directory.CreateDirectory(outputDir);
        if (sample)
        {
            OperationRenderVariant[] variants = { OperationRenderVariant.Classic, OperationRenderVariant.Typographic, OperationRenderVariant.AmberHud, OperationRenderVariant.WarmCard, OperationRenderVariant.Phosphor };
            for (int i = 0; i < variants.Length; i++)
            {
                WidgetSettings settings = WidgetSettings.CreateDefaults();
                settings.OperationRenderVariant = variants[i];
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
                    // Mirror GetDesiredSize: the physical canvas grows by the same factor as the content.
                    form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(DesignTokens.Colors.AppBackground);
                        form.DrawWindowContent(g);
                        string path = Path.Combine(outputDir, "specboard-" + variants[i].ToString().ToLowerInvariant() + ".png");
                        bitmap.Save(path, ImageFormat.Png);
                        Console.WriteLine(variants[i] + " -> " + path + " (" + form.Width + "x" + form.Height + ")");
                    }
                }
            }
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

        RunAutoHideSelfTest();
        RunAutoPopupSelfTest();
        RunManagerLifecycleSelfTest();
        RunManagerWatcherWriteSelfTest();
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
