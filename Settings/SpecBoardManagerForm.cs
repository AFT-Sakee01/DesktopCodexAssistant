using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Fully owner-drawn "workbench" manager window (approved proposal A). It deliberately does NOT
// inherit LayeredWidgetFormBase: the UpdateLayeredWindow pipeline cannot composite child windows,
// and the note/search/typed-confirm inputs must be real focusable TextBoxes. Everything else
// (title bar, chips, capsules, list, scrollbars, buttons) is painted in OnPaint against a hit
// target table, mirroring the SpecBoardForm pattern.
internal sealed class SpecBoardManagerForm : Form
{
    private const int TwoLineTitleLines = 2;

    private enum Hit
    {
        None,
        Close,
        ProjectFilter,
        StatusChip,
        BatchRegister,
        ListRow,
        ListThumb,
        RightThumb,
        Capsule,
        NoteSave,
        OpenFile,
        Reveal,
        DangerToggle,
        DangerDeleteRow,
        DangerDeleteFile,
        RegisterRow,
        BatchDelete
    }

    private sealed class HitTarget
    {
        public Rectangle Bounds;
        public Hit Kind;
        public string Status = string.Empty;
        public int Index = -1;
        public bool Enabled = true;
    }

    private readonly WidgetSettings settings;
    private readonly TextBox searchBox = new TextBox();
    private readonly TextBox noteBox = new TextBox();
    private readonly TextBox confirmBox = new TextBox();
    private readonly List<HitTarget> hitTargets = new List<HitTarget>();
    private readonly List<SpecBoardRow> viewRows = new List<SpecBoardRow>();
    private readonly List<string> selectedIds = new List<string>();
    private readonly object snapshotLoadSync = new object();
    private readonly Font titleBarFont;
    private readonly Font headingFont;
    private readonly Font bodyFont;
    private readonly Font smallFont;
    private SpecBoardSnapshot snapshot = new SpecBoardSnapshot();
    private float uiScale = 1f;
    private string projectFilter = string.Empty;
    private string statusFilter = string.Empty;
    private int listScroll;
    private int rightScroll;
    private int rightContentHeight;
    private int selectionAnchor = -1;
    private bool dangerExpanded;
    private bool noteDirty;
    private string noteRowId = string.Empty;
    private bool suppressNoteDirty;
    private Hit hoverKind = Hit.None;
    private string hoverStatus = string.Empty;
    private int hoverIndex = -1;
    private Hit dragThumb = Hit.None;
    private int dragThumbStartOffset;
    private int dragThumbStartY;
    private Rectangle listRect;
    private Rectangle rightRect;
    private Rectangle closeBounds;
    private Point hitOffset = Point.Empty;
    private Rectangle hitClip = Rectangle.Empty;
    private CancellationTokenSource snapshotLoadCancellation;
    private int snapshotLoadGeneration;
    private int snapshotAppliedGeneration;
    private int lastSnapshotReaderThreadId;
    private string snapshotLoadError = string.Empty;

    public SpecBoardManagerForm(WidgetSettings settings)
    {
        this.settings = settings.Clone();
        this.settings.Normalize();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "Spec Board 管理";
        this.ShowInTaskbar = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.ForeColor = DesignTokens.Colors.TextStrong;
        this.Font = new Font(DesignTokens.UiFontFamily, 9.0f, FontStyle.Regular, GraphicsUnit.Point);
        using (Graphics g = CreateGraphics())
        {
            this.uiScale = Math.Max(1f, g.DpiX / 96f);
        }

        this.titleBarFont = new Font(DesignTokens.UiFontFamily, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        this.headingFont = new Font(DesignTokens.UiFontFamily, 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        this.bodyFont = this.Font;
        this.smallFont = new Font(DesignTokens.UiFontFamily, 8.25f, FontStyle.Regular, GraphicsUnit.Point);

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        this.MinimumSize = new Size(S(WidgetSettings.MinSpecBoardManagerWidth), S(WidgetSettings.MinSpecBoardManagerHeight));
        this.Size = new Size(
            Math.Min(S(this.settings.SpecBoardManagerWidth), Math.Max(this.MinimumSize.Width, workArea.Width - S(48))),
            Math.Min(S(this.settings.SpecBoardManagerHeight), Math.Max(this.MinimumSize.Height, workArea.Height - S(48))));
        ApplicationIcon.ApplyTo(this);

        ConfigureInput(this.searchBox, false);
        this.searchBox.TextChanged += delegate { BuildView(null); Invalidate(); };
        ConfigureInput(this.noteBox, true);
        this.noteBox.TextChanged += delegate
        {
            if (!this.suppressNoteDirty)
            {
                this.noteDirty = true;
                Invalidate();
            }
        };
        ConfigureInput(this.confirmBox, false);
        this.confirmBox.TextChanged += delegate { Invalidate(); };
        Controls.Add(this.searchBox);
        Controls.Add(this.noteBox);
        Controls.Add(this.confirmBox);

        UpdateRegion();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Volatile.Read(ref this.snapshotLoadGeneration) == 0)
        {
            LoadSnapshot(null);
        }
    }

    public void ActivateOrShow()
    {
        if (!this.Visible)
        {
            Show();
        }

        if (this.WindowState == FormWindowState.Minimized)
        {
            this.WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    private void ConfigureInput(TextBox box, bool multiline)
    {
        box.BorderStyle = BorderStyle.None;
        box.BackColor = DesignTokens.Colors.Control;
        box.ForeColor = DesignTokens.Colors.TextStrong;
        box.Multiline = multiline;
        if (multiline)
        {
            box.ScrollBars = ScrollBars.Vertical;
        }

        box.Visible = false;
    }

    private int S(int value)
    {
        return (int)Math.Round(value * this.uiScale);
    }

    private int LineHeight(Font font)
    {
        return TextRenderer.MeasureText("宽Wg", font).Height;
    }

    // ── data ────────────────────────────────────────────────────────────────

    private void LoadSnapshot(string reselectId)
    {
        int generation = Interlocked.Increment(ref this.snapshotLoadGeneration);
        CancellationTokenSource cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        CancellationTokenSource previous;
        lock (this.snapshotLoadSync)
        {
            previous = this.snapshotLoadCancellation;
            this.snapshotLoadCancellation = cancellation;
            this.snapshotLoadError = string.Empty;
        }

        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }

        // A completed write immediately invalidates the selected rows' UpdatedUtc values. Clear the
        // interactive view until the background reload publishes a complete snapshot so the user
        // cannot submit a second mutation against stale conflict tokens.
        this.selectedIds.Clear();
        this.viewRows.Clear();
        Invalidate();

        string ledgerPath = this.settings.SpecBoardLedgerPath;
        Task.Run(delegate
        {
            Interlocked.Exchange(ref this.lastSnapshotReaderThreadId, Thread.CurrentThread.ManagedThreadId);
            return SpecBoardReader.Read(ledgerPath, true, token);
        }, token).ContinueWith(task =>
        {
            if (task.Status != TaskStatus.RanToCompletion ||
                !ShouldApplySnapshotLoad(generation, Volatile.Read(ref this.snapshotLoadGeneration), token.IsCancellationRequested))
            {
                if (task.IsFaulted && task.Exception != null)
                {
                    lock (this.snapshotLoadSync)
                    {
                        this.snapshotLoadError = task.Exception.GetBaseException().Message;
                    }

                    Program.LogInfo("SpecBoard manager reload failed: " + task.Exception.GetBaseException().Message);
                }

                CompleteSnapshotLoad(cancellation);
                return;
            }

            try
            {
                if (this.IsDisposed || !this.IsHandleCreated)
                {
                    CompleteSnapshotLoad(cancellation);
                    return;
                }

                this.BeginInvoke((Action)delegate
                {
                    if (!this.IsDisposed && ShouldApplySnapshotLoad(
                        generation,
                        Volatile.Read(ref this.snapshotLoadGeneration),
                        token.IsCancellationRequested))
                    {
                        this.snapshot = task.Result;
                        if (!string.IsNullOrEmpty(reselectId))
                        {
                            this.selectedIds.Clear();
                            this.selectedIds.Add(reselectId);
                        }

                        BuildView(reselectId);
                        Volatile.Write(ref this.snapshotAppliedGeneration, generation);
                        Invalidate();
                    }
                });
                CompleteSnapshotLoad(cancellation);
            }
            catch (ObjectDisposedException)
            {
                CompleteSnapshotLoad(cancellation);
            }
            catch (InvalidOperationException)
            {
                CompleteSnapshotLoad(cancellation);
            }
        }, TaskScheduler.Default);
    }

    private static bool ShouldApplySnapshotLoad(int resultGeneration, int currentGeneration, bool canceled)
    {
        return !canceled && resultGeneration == currentGeneration;
    }

    private void CompleteSnapshotLoad(CancellationTokenSource cancellation)
    {
        lock (this.snapshotLoadSync)
        {
            if (object.ReferenceEquals(this.snapshotLoadCancellation, cancellation))
            {
                this.snapshotLoadCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private void CancelSnapshotLoad()
    {
        Interlocked.Increment(ref this.snapshotLoadGeneration);
        CancellationTokenSource cancellation;
        lock (this.snapshotLoadSync)
        {
            cancellation = this.snapshotLoadCancellation;
            this.snapshotLoadCancellation = null;
        }

        if (cancellation != null)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void WaitForSnapshotLoadForTest(int timeoutMilliseconds)
    {
        Application.DoEvents();
        if (Volatile.Read(ref this.snapshotLoadGeneration) == 0 && !this.IsDisposed)
        {
            LoadSnapshot(null);
        }

        int expectedGeneration = Volatile.Read(ref this.snapshotLoadGeneration);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (Volatile.Read(ref this.snapshotAppliedGeneration) != expectedGeneration && elapsed.ElapsedMilliseconds < timeoutMilliseconds)
        {
            string error;
            lock (this.snapshotLoadSync)
            {
                error = this.snapshotLoadError;
            }

            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException("Spec Board manager background load failed: " + error);
            }

            Application.DoEvents();
            Thread.Sleep(5);
        }

        if (Volatile.Read(ref this.snapshotAppliedGeneration) != expectedGeneration)
        {
            throw new TimeoutException("Spec Board manager background load timed out.");
        }
    }

    private void BuildView(string reselectId)
    {
        string search = (this.searchBox.Text ?? string.Empty).Trim();
        this.viewRows.Clear();
        this.viewRows.AddRange(this.snapshot.Rows
            .Where(row =>
                (string.IsNullOrEmpty(this.projectFilter) || string.Equals(row.Project, this.projectFilter, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(this.statusFilter) || string.Equals(row.Status, this.statusFilter, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(search) ||
                 (row.Title ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (row.Project ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (row.SpecPath ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(row => StatusPriority(row.Status))
            .ThenByDescending(row => row.UpdatedUtc ?? row.EventTimeUtc ?? DateTime.MinValue));

        HashSet<string> visible = new HashSet<string>(this.viewRows.Select(row => row.Id ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        this.selectedIds.RemoveAll(id => !visible.Contains(id));
        OnSelectionChanged();
    }

    private List<SpecBoardRow> SelectedRows()
    {
        return this.viewRows.Where(row => this.selectedIds.Contains(row.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private SpecBoardRow SingleRow()
    {
        List<SpecBoardRow> rows = SelectedRows();
        return rows.Count == 1 ? rows[0] : null;
    }

    private static int StatusPriority(string status)
    {
        if (status == SpecBoardStatus.Unregistered) return 0;
        if (status == SpecBoardStatus.Pending) return 1;
        if (status == SpecBoardStatus.NeedsRevision) return 2;
        if (status == SpecBoardStatus.AwaitingVerify) return 3;
        if (status == SpecBoardStatus.Done) return 4;
        return 5;
    }

    private static Color StatusColor(string status)
    {
        if (status == SpecBoardStatus.Unregistered) return DesignTokens.Colors.WarningDeep;
        if (status == SpecBoardStatus.Pending) return DesignTokens.Colors.Danger;
        if (status == SpecBoardStatus.NeedsRevision) return DesignTokens.Colors.AccentAlt;
        if (status == SpecBoardStatus.AwaitingVerify) return DesignTokens.Colors.Warning;
        if (status == SpecBoardStatus.Done) return DesignTokens.Colors.SuccessSoft;
        return DesignTokens.Colors.GlyphMuted;
    }

    // ── window chrome ───────────────────────────────────────────────────────

    private void UpdateRegion()
    {
        using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, this.Width, this.Height), S(10)))
        {
            Region old = this.Region;
            this.Region = new Region(path);
            if (old != null)
            {
                old.Dispose();
            }
        }
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1)
            {
                Point screen = new Point(m.LParam.ToInt32());
                Point client = PointToClient(screen);
                int grip = S(6);
                bool left = client.X < grip;
                bool right = client.X >= this.Width - grip;
                bool top = client.Y < grip;
                bool bottom = client.Y >= this.Height - grip;
                if (top && left) { m.Result = (IntPtr)13; return; }
                if (top && right) { m.Result = (IntPtr)14; return; }
                if (bottom && left) { m.Result = (IntPtr)16; return; }
                if (bottom && right) { m.Result = (IntPtr)17; return; }
                if (left) { m.Result = (IntPtr)10; return; }
                if (right) { m.Result = (IntPtr)11; return; }
                if (top) { m.Result = (IntPtr)12; return; }
                if (bottom) { m.Result = (IntPtr)15; return; }
                if (client.Y < TitleBarHeight() && !this.closeBounds.Contains(client))
                {
                    m.Result = (IntPtr)2;
                    return;
                }
            }

            return;
        }

        base.WndProc(ref m);
    }

    private int TitleBarHeight()
    {
        return LineHeight(this.titleBarFont) + S(14);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        bool typing = this.ActiveControl is TextBox;
        if (!typing)
        {
            int digit = keyData >= Keys.D1 && keyData <= Keys.D5
                ? keyData - Keys.D1
                : keyData >= Keys.NumPad1 && keyData <= Keys.NumPad5 ? keyData - Keys.NumPad1 : -1;
            if (digit >= 0 && this.selectedIds.Count > 0)
            {
                SetStatusForSelection(SpecBoardStatus.LedgerValues[digit]);
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── painting ────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        this.hitTargets.Clear();

        using (SolidBrush back = new SolidBrush(DesignTokens.Colors.AppBackground))
        {
            g.FillRectangle(back, this.ClientRectangle);
        }

        using (Pen border = new Pen(DesignTokens.Colors.Border))
        {
            g.DrawRectangle(border, 0, 0, this.Width - 1, this.Height - 1);
        }

        int pad = S(12);
        int y = DrawTitleBar(g, pad);
        y = DrawToolbar(g, pad, y);

        int contentTop = y + S(6);
        int contentBottom = this.Height - pad;
        int listWidth = (int)Math.Round((this.Width - pad * 2) * 0.44);
        this.listRect = new Rectangle(pad, contentTop, listWidth, Math.Max(S(40), contentBottom - contentTop));
        this.rightRect = new Rectangle(pad + listWidth + S(10), contentTop, this.Width - pad * 2 - listWidth - S(10), Math.Max(S(40), contentBottom - contentTop));

        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 140)))
        {
            g.DrawLine(divider, this.listRect.Right + S(5), contentTop, this.listRect.Right + S(5), contentBottom);
        }

        DrawList(g);
        DrawDetail(g);
    }

    private int DrawTitleBar(Graphics g, int pad)
    {
        int height = TitleBarHeight();
        TextRenderer.DrawText(g, "SPEC 管理", this.titleBarFont, new Rectangle(pad, 0, this.Width - pad * 2, height), DesignTokens.Colors.TextStrong, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int closeSize = height - S(8);
        this.closeBounds = new Rectangle(this.Width - pad - closeSize, S(4), closeSize, closeSize);
        bool hover = this.hoverKind == Hit.Close;
        if (hover)
        {
            using (SolidBrush hoverBrush = new SolidBrush(DesignTokens.Colors.DangerClose))
            using (GraphicsPath path = RoundedPath(this.closeBounds, S(4)))
            {
                g.FillPath(hoverBrush, path);
            }
        }

        using (Pen glyph = new Pen(hover ? Color.White : DesignTokens.Colors.GlyphMuted, Math.Max(1f, this.uiScale)))
        {
            int inset = S(7);
            g.DrawLine(glyph, this.closeBounds.Left + inset, this.closeBounds.Top + inset, this.closeBounds.Right - inset, this.closeBounds.Bottom - inset);
            g.DrawLine(glyph, this.closeBounds.Right - inset, this.closeBounds.Top + inset, this.closeBounds.Left + inset, this.closeBounds.Bottom - inset);
        }

        AddHit(this.closeBounds, Hit.Close);
        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 140)))
        {
            g.DrawLine(divider, pad, height, this.Width - pad, height);
        }

        return height + S(6);
    }

    private int DrawToolbar(Graphics g, int pad, int top)
    {
        int chipHeight = LineHeight(this.smallFont) + S(8);
        int x = pad;
        int y = top;

        string projectLabel = (string.IsNullOrEmpty(this.projectFilter) ? "全部项目" : this.projectFilter) + " ▾";
        Rectangle projectChip = DrawChip(g, ref x, y, chipHeight, projectLabel, DesignTokens.Colors.TextMuted, false, this.hoverKind == Hit.ProjectFilter);
        AddHit(projectChip, Hit.ProjectFilter);
        x += S(8);

        string[] filterValues = { string.Empty, SpecBoardStatus.Unregistered, SpecBoardStatus.Pending, SpecBoardStatus.NeedsRevision, SpecBoardStatus.AwaitingVerify, SpecBoardStatus.Done, SpecBoardStatus.Abandoned };
        foreach (string value in filterValues)
        {
            string label = string.IsNullOrEmpty(value) ? "全部" : SpecBoardStatus.DisplayName(value);
            Color color = string.IsNullOrEmpty(value) ? DesignTokens.Colors.TextMuted : StatusColor(value);
            bool active = string.Equals(this.statusFilter, value, StringComparison.OrdinalIgnoreCase);
            if (x + TextRenderer.MeasureText(label, this.smallFont).Width + S(20) > this.Width - pad - S(150) - S(12))
            {
                x = pad;
                y += chipHeight + S(4);
            }

            bool hover = this.hoverKind == Hit.StatusChip && string.Equals(this.hoverStatus, value, StringComparison.OrdinalIgnoreCase);
            Rectangle chip = DrawChip(g, ref x, y, chipHeight, label, color, active, hover);
            AddHit(chip, Hit.StatusChip, value);
            x += S(4);
        }

        // Search input pinned to the right side of the first toolbar row.
        int searchWidth = S(150);
        Rectangle searchRect = new Rectangle(this.Width - pad - searchWidth - S(4), top, searchWidth, chipHeight);
        using (GraphicsPath path = RoundedPath(searchRect, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.Colors.Control))
        using (Pen edge = new Pen(DesignTokens.Colors.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        Rectangle searchInner = new Rectangle(searchRect.Left + S(8), searchRect.Top + Math.Max(1, (chipHeight - this.searchBox.PreferredHeight) / 2), searchRect.Width - S(14), this.searchBox.PreferredHeight);
        PlaceInput(this.searchBox, searchInner, true);
        if (this.searchBox.Text.Length == 0 && !this.searchBox.Focused)
        {
            TextRenderer.DrawText(g, "搜索…", this.smallFont, searchInner, DesignTokens.Colors.GlyphMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // Second toolbar row: batch register.
        y += chipHeight + S(4);
        x = pad;
        int unregistered = this.snapshot.Rows.Count(row => row.IsUnregistered);
        bool registerEnabled = unregistered > 0;
        Rectangle registerChip = DrawButton(g, ref x, y, chipHeight, "批量登记未登记项 (" + unregistered.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", registerEnabled, false, this.hoverKind == Hit.BatchRegister);
        AddHit(registerChip, Hit.BatchRegister, string.Empty, -1, registerEnabled);

        string counts = "共 " + this.snapshot.Rows.Count + " 项 · 坏行 " + this.snapshot.MalformedLines;
        TextRenderer.DrawText(g, counts, this.smallFont, new Rectangle(x + S(8), y, this.Width - pad - x - S(8), chipHeight), DesignTokens.Colors.GlyphMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return y + chipHeight + S(4);
    }

    private Rectangle DrawChip(Graphics g, ref int x, int y, int height, string text, Color color, bool active, bool hover)
    {
        int width = TextRenderer.MeasureText(text, this.smallFont).Width + S(16);
        Rectangle bounds = new Rectangle(x, y, width, height);
        using (GraphicsPath path = RoundedPath(bounds, height / 2))
        {
            if (active)
            {
                using (SolidBrush fill = new SolidBrush(color))
                {
                    g.FillPath(fill, path);
                }
            }
            else if (hover)
            {
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 40)))
                {
                    g.FillPath(fill, path);
                }
            }

            using (Pen edge = new Pen(active ? color : DesignTokens.WithAlpha(color, 150)))
            {
                g.DrawPath(edge, path);
            }
        }

        TextRenderer.DrawText(g, text, this.smallFont, bounds, active ? DesignTokens.Colors.AppBackground : color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        x += width;
        return bounds;
    }

    private Rectangle DrawButton(Graphics g, ref int x, int y, int height, string text, bool enabled, bool danger, bool hover)
    {
        int width = TextRenderer.MeasureText(text, this.smallFont).Width + S(18);
        Rectangle bounds = new Rectangle(x, y, width, height);
        Color edgeColor = danger ? DesignTokens.Colors.DangerBorder : DesignTokens.Colors.Border;
        Color textColor = !enabled ? DesignTokens.Colors.GlyphMuted : danger ? DesignTokens.Colors.DangerText : DesignTokens.Colors.TextStrong;
        using (GraphicsPath path = RoundedPath(bounds, S(5)))
        {
            using (SolidBrush fill = new SolidBrush(hover && enabled ? DesignTokens.Colors.ControlActive : DesignTokens.Colors.Control))
            {
                g.FillPath(fill, path);
            }

            using (Pen edge = new Pen(enabled ? edgeColor : DesignTokens.WithAlpha(edgeColor, 90)))
            {
                g.DrawPath(edge, path);
            }
        }

        TextRenderer.DrawText(g, text, this.smallFont, bounds, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        x += width;
        return bounds;
    }

    private void DrawList(Graphics g)
    {
        Rectangle bounds = this.listRect;
        int rowHeight = LineHeight(this.bodyFont) + S(8);
        int viewport = bounds.Height - S(8);
        int contentHeight = this.viewRows.Count * rowHeight;
        int maxScroll = Math.Max(0, contentHeight - viewport);
        this.listScroll = Math.Max(0, Math.Min(this.listScroll, maxScroll));

        // TextRenderer paints through Graphics.SetClip (GDI vs GDI+), so scrolled panes render
        // into an offscreen buffer whose bitmap edge is a hard clip.
        using (Bitmap buffer = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)))
        {
            using (Graphics bg = Graphics.FromImage(buffer))
            {
                bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                bg.Clear(DesignTokens.Colors.AppBackground);
                using (SolidBrush surface = new SolidBrush(DesignTokens.Colors.Surface))
                using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, bounds.Width, bounds.Height), S(7)))
                {
                    bg.FillPath(surface, path);
                }

                this.hitOffset = bounds.Location;
                this.hitClip = bounds;
                int y = S(4) - this.listScroll;
                for (int i = 0; i < this.viewRows.Count; i++)
                {
                    Rectangle rowRect = new Rectangle(S(4), y, bounds.Width - S(8) - (maxScroll > 0 ? S(10) : 0), rowHeight);
                    if (rowRect.Bottom >= 0 && rowRect.Top <= bounds.Height)
                    {
                        SpecBoardRow row = this.viewRows[i];
                        bool selected = this.selectedIds.Contains(row.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                        if (selected)
                        {
                            using (SolidBrush sel = new SolidBrush(DesignTokens.Colors.ControlActive))
                            using (GraphicsPath rowPath = RoundedPath(rowRect, S(5)))
                            {
                                bg.FillPath(sel, rowPath);
                            }
                        }
                        else if (this.hoverKind == Hit.ListRow && this.hoverIndex == i)
                        {
                            using (SolidBrush hov = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.ControlActive, 110)))
                            using (GraphicsPath rowPath = RoundedPath(rowRect, S(5)))
                            {
                                bg.FillPath(hov, rowPath);
                            }
                        }

                        int dot = S(8);
                        using (SolidBrush dotBrush = new SolidBrush(StatusColor(row.Status)))
                        {
                            SmoothingMode old = bg.SmoothingMode;
                            bg.SmoothingMode = SmoothingMode.AntiAlias;
                            bg.FillEllipse(dotBrush, rowRect.Left + S(6), rowRect.Top + (rowHeight - dot) / 2, dot, dot);
                            bg.SmoothingMode = old;
                        }

                        int projectWidth = Math.Min(S(120), rowRect.Width / 3);
                        Rectangle titleRect = new Rectangle(rowRect.Left + S(20), rowRect.Top, Math.Max(S(30), rowRect.Width - S(24) - projectWidth), rowHeight);
                        Rectangle projectRect = new Rectangle(titleRect.Right, rowRect.Top, projectWidth, rowHeight);
                        TextRenderer.DrawText(bg, row.Title, this.bodyFont, titleRect, row.FileMissing ? DesignTokens.Colors.GlyphMuted : DesignTokens.Colors.TextStrong, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(bg, row.Project, this.smallFont, projectRect, DesignTokens.Colors.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
                        AddHit(rowRect, Hit.ListRow, row.Status, i);
                    }

                    y += rowHeight;
                }

                if (this.viewRows.Count == 0)
                {
                    TextRenderer.DrawText(bg, "没有匹配的 spec", this.bodyFont, new Rectangle(0, 0, bounds.Width, bounds.Height), DesignTokens.Colors.GlyphMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }

                this.hitOffset = Point.Empty;
                this.hitClip = Rectangle.Empty;
            }

            g.DrawImage(buffer, bounds.Location);
        }

        if (maxScroll > 0)
        {
            DrawScrollbar(g, new Rectangle(bounds.Right - S(9), bounds.Top + S(4), S(6), viewport), contentHeight, viewport, this.listScroll, Hit.ListThumb);
        }
    }

    private void DrawScrollbar(Graphics g, Rectangle track, int contentHeight, int viewport, int offset, Hit kind)
    {
        int thumbHeight = Math.Max(S(24), (int)((float)viewport / contentHeight * track.Height));
        int maxScroll = Math.Max(1, contentHeight - viewport);
        int thumbTop = track.Top + (int)((float)offset / maxScroll * (track.Height - thumbHeight));
        Rectangle thumb = new Rectangle(track.Left, thumbTop, track.Width, thumbHeight);
        bool active = this.dragThumb == kind || (this.hoverKind == kind);
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, active ? 200 : 110)))
        using (GraphicsPath path = RoundedPath(thumb, track.Width / 2))
        {
            g.FillPath(brush, path);
        }

        AddHit(thumb, kind);
    }

    private void DrawDetail(Graphics g)
    {
        Rectangle bounds = this.rightRect;
        List<SpecBoardRow> selected = SelectedRows();
        this.noteBox.Visible = false;
        this.confirmBox.Visible = false;

        if (selected.Count == 0)
        {
            TextRenderer.DrawText(g, "在左侧选择一个 spec", this.bodyFont, bounds, DesignTokens.Colors.GlyphMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            this.rightContentHeight = 0;
            return;
        }

        int endY;
        using (Bitmap buffer = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)))
        {
            using (Graphics bg = Graphics.FromImage(buffer))
            {
                bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                bg.Clear(DesignTokens.Colors.AppBackground);
                this.hitOffset = bounds.Location;
                this.hitClip = bounds;
                int y = -this.rightScroll;
                int left = S(2);
                int width = bounds.Width - S(16);
                endY = selected.Count >= 2
                    ? DrawBatchDetail(bg, selected, left, y, width)
                    : DrawSingleDetail(bg, selected[0], left, y, width);
                this.hitOffset = Point.Empty;
                this.hitClip = Rectangle.Empty;
            }

            g.DrawImage(buffer, bounds.Location);
        }

        this.rightContentHeight = endY + this.rightScroll;
        int maxScroll = Math.Max(0, this.rightContentHeight - bounds.Height);
        if (this.rightScroll > maxScroll)
        {
            this.rightScroll = maxScroll;
            Invalidate();
        }

        if (maxScroll > 0)
        {
            DrawScrollbar(g, new Rectangle(bounds.Right - S(9), bounds.Top + S(2), S(6), bounds.Height - S(4)), this.rightContentHeight, bounds.Height, this.rightScroll, Hit.RightThumb);
        }
    }

    private int DrawSingleDetail(Graphics g, SpecBoardRow row, int left, int y, int width)
    {
        int headingLine = LineHeight(this.headingFont);
        Rectangle titleRect = new Rectangle(left, y, width, headingLine * TwoLineTitleLines);
        TextRenderer.DrawText(g, row.Title, this.headingFont, titleRect, DesignTokens.Colors.TextStrong, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int measured = Math.Min(titleRect.Height, TextRenderer.MeasureText(row.Title ?? string.Empty, this.headingFont, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
        y += Math.Max(headingLine, measured) + S(4);

        int smallLine = LineHeight(this.smallFont);
        string fileName = Path.GetFileName(string.IsNullOrEmpty(row.AbsolutePath) ? (row.SpecPath ?? string.Empty) : row.AbsolutePath);
        string meta = row.Project + " · " + fileName + (File.Exists(row.AbsolutePath) ? "  ✓" : "  文件不存在");
        TextRenderer.DrawText(g, meta, this.smallFont, new Rectangle(left, y, width, smallLine), DesignTokens.Colors.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        y += smallLine + S(2);
        TextRenderer.DrawText(g, row.AbsolutePath, this.smallFont, new Rectangle(left, y, width, smallLine), DesignTokens.Colors.GlyphMuted, TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
        y += smallLine + S(2);
        TextRenderer.DrawText(g, BuildTimeline(row), this.smallFont, new Rectangle(left, y, width, smallLine), DesignTokens.Colors.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        y += smallLine + S(8);

        if (row.IsUnregistered)
        {
            int x = left;
            int buttonHeight = LineHeight(this.smallFont) + S(8);
            Rectangle register = DrawButton(g, ref x, y, buttonHeight, "登记为未执行", true, false, this.hoverKind == Hit.RegisterRow);
            AddHit(register, Hit.RegisterRow);
            y += buttonHeight + S(8);
        }
        else
        {
            y = DrawCapsules(g, left, y, width, row.Status, true);
        }

        TextRenderer.DrawText(g, "备注", this.smallFont, new Rectangle(left, y, width, smallLine), DesignTokens.Colors.TextMuted, TextFormatFlags.NoPrefix);
        y += smallLine + S(2);
        Rectangle noteCard = new Rectangle(left, y, width, S(84));
        using (GraphicsPath path = RoundedPath(noteCard, S(6)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.Colors.Control))
        using (Pen edge = new Pen(this.noteDirty ? DesignTokens.Colors.AccentBorder : DesignTokens.Colors.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        if (!row.IsUnregistered)
        {
            SyncNoteBox(row);
            PlaceInput(this.noteBox, Rectangle.Inflate(noteCard, -S(7), -S(6)), true);
        }

        y += noteCard.Height + S(6);

        int actionsX = left;
        int actionHeight = LineHeight(this.smallFont) + S(8);
        if (!row.IsUnregistered)
        {
            Rectangle save = DrawButton(g, ref actionsX, y, actionHeight, "保存备注", this.noteDirty, false, this.hoverKind == Hit.NoteSave);
            AddHit(save, Hit.NoteSave, string.Empty, -1, this.noteDirty);
            actionsX += S(6);
        }

        Rectangle open = DrawButton(g, ref actionsX, y, actionHeight, "打开", File.Exists(row.AbsolutePath), false, this.hoverKind == Hit.OpenFile);
        AddHit(open, Hit.OpenFile, string.Empty, -1, File.Exists(row.AbsolutePath));
        actionsX += S(6);
        Rectangle reveal = DrawButton(g, ref actionsX, y, actionHeight, "定位", true, false, this.hoverKind == Hit.Reveal);
        AddHit(reveal, Hit.Reveal);
        actionsX += S(6);
        if (!row.IsUnregistered)
        {
            Rectangle danger = DrawButton(g, ref actionsX, y, actionHeight, this.dangerExpanded ? "危险 ▾" : "危险 ▸", true, true, this.hoverKind == Hit.DangerToggle);
            AddHit(danger, Hit.DangerToggle);
        }

        y += actionHeight + S(8);
        if (this.dangerExpanded && !row.IsUnregistered)
        {
            y = DrawDangerZone(g, row, left, y, width);
        }

        return y;
    }

    private int DrawCapsules(Graphics g, int left, int y, int width, string currentStatus, bool enabled)
    {
        int capsuleHeight = LineHeight(this.smallFont) + S(8);
        int x = left;
        foreach (string status in SpecBoardStatus.LedgerValues)
        {
            string label = SpecBoardStatus.DisplayName(status);
            int capsuleWidth = TextRenderer.MeasureText(label, this.smallFont).Width + S(16);
            if (x + capsuleWidth > left + width)
            {
                x = left;
                y += capsuleHeight + S(4);
            }

            bool active = string.Equals(status, currentStatus, StringComparison.OrdinalIgnoreCase);
            bool hover = enabled && this.hoverKind == Hit.Capsule && string.Equals(this.hoverStatus, status, StringComparison.OrdinalIgnoreCase);
            Rectangle capsule = DrawChip(g, ref x, y, capsuleHeight, label, enabled ? StatusColor(status) : DesignTokens.Colors.GlyphMuted, active, hover);
            AddHit(capsule, Hit.Capsule, status, -1, enabled);
            x += S(4);
        }

        return y + capsuleHeight + S(8);
    }

    private int DrawBatchDetail(Graphics g, List<SpecBoardRow> selected, int left, int y, int width)
    {
        int headingLine = LineHeight(this.headingFont);
        TextRenderer.DrawText(g, "已选 " + selected.Count + " 项", this.headingFont, new Rectangle(left, y, width, headingLine), DesignTokens.Colors.TextStrong, TextFormatFlags.NoPrefix);
        y += headingLine + S(6);
        int smallLine = LineHeight(this.smallFont);
        TextRenderer.DrawText(g, "点击胶囊对全部选中项应用状态（含未登记项则登记为该状态）", this.smallFont, new Rectangle(left, y, width, smallLine), DesignTokens.Colors.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        y += smallLine + S(6);
        y = DrawCapsules(g, left, y, width, string.Empty, true);

        int x = left;
        int actionHeight = LineHeight(this.smallFont) + S(8);
        int registered = selected.Count(row => !row.IsUnregistered);
        Rectangle batchDelete = DrawButton(g, ref x, y, actionHeight, "批量删除条目 (" + registered + ")", registered > 0, true, this.hoverKind == Hit.BatchDelete);
        AddHit(batchDelete, Hit.BatchDelete, string.Empty, -1, registered > 0);
        return y + actionHeight + S(8);
    }

    private int DrawDangerZone(Graphics g, SpecBoardRow row, int left, int y, int width)
    {
        int smallLine = LineHeight(this.smallFont);
        int actionHeight = smallLine + S(8);
        int inputHeight = this.confirmBox.PreferredHeight + S(8);
        int zoneHeight = S(10) * 2 + smallLine * 2 + actionHeight * 2 + inputHeight + S(16);
        Rectangle zone = new Rectangle(left, y, width, zoneHeight);
        using (GraphicsPath path = RoundedPath(zone, S(7)))
        using (SolidBrush fill = new SolidBrush(Color.FromArgb(52, 25, 29)))
        using (Pen edge = new Pen(DesignTokens.Colors.DangerBorder))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        int innerLeft = zone.Left + S(10);
        int innerWidth = zone.Width - S(20);
        int innerY = zone.Top + S(10);
        TextRenderer.DrawText(g, "危险区：删除源文件将送入 Windows 回收站", this.smallFont, new Rectangle(innerLeft, innerY, innerWidth, smallLine), DesignTokens.Colors.DangerText, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        innerY += smallLine + S(4);
        int x = innerLeft;
        Rectangle deleteRow = DrawButton(g, ref x, innerY, actionHeight, "删除账本条目", true, true, this.hoverKind == Hit.DangerDeleteRow);
        AddHit(deleteRow, Hit.DangerDeleteRow);
        innerY += actionHeight + S(4);
        TextRenderer.DrawText(g, "输入完整文件名后才可删除源文件", this.smallFont, new Rectangle(innerLeft, innerY, innerWidth, smallLine), DesignTokens.Colors.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        innerY += smallLine + S(3);
        Rectangle confirmRect = new Rectangle(innerLeft, innerY, innerWidth, inputHeight);
        using (GraphicsPath path = RoundedPath(confirmRect, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.Colors.Control))
        using (Pen edge = new Pen(DesignTokens.Colors.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        PlaceInput(this.confirmBox, new Rectangle(confirmRect.Left + S(7), confirmRect.Top + Math.Max(1, (inputHeight - this.confirmBox.PreferredHeight) / 2), confirmRect.Width - S(14), this.confirmBox.PreferredHeight), true);
        innerY += inputHeight + S(4);
        x = innerLeft;
        bool fileDeleteEnabled = IsDeleteFileEnabled(row);
        Rectangle deleteFile = DrawButton(g, ref x, innerY, actionHeight, "删除条目并删除源文件", fileDeleteEnabled, true, this.hoverKind == Hit.DangerDeleteFile);
        AddHit(deleteFile, Hit.DangerDeleteFile, string.Empty, -1, fileDeleteEnabled);
        return zone.Bottom + S(6);
    }

    internal bool IsDeleteFileEnabled(SpecBoardRow row)
    {
        if (row == null || row.IsUnregistered)
        {
            return false;
        }

        return !this.settings.SpecBoardManagerDangerZoneRequiresTypedConfirm ||
            string.Equals(this.confirmBox.Text, Path.GetFileName(row.AbsolutePath), StringComparison.Ordinal);
    }

    private void SyncNoteBox(SpecBoardRow row)
    {
        string id = row.Id ?? string.Empty;
        if (!string.Equals(this.noteRowId, id, StringComparison.OrdinalIgnoreCase))
        {
            this.suppressNoteDirty = true;
            this.noteBox.Text = row.Note ?? string.Empty;
            this.suppressNoteDirty = false;
            this.noteRowId = id;
            this.noteDirty = false;
        }
    }

    private void PlaceInput(TextBox box, Rectangle bounds, bool visible)
    {
        bounds.Offset(this.hitOffset);
        Rectangle visiblePart = this.hitClip == Rectangle.Empty ? bounds : Rectangle.Intersect(this.hitClip, bounds);
        if (visiblePart.Width <= 0 || visiblePart.Height <= 0)
        {
            visible = false;
        }

        if (box.Bounds != bounds)
        {
            box.Bounds = bounds;
        }

        // Native children ignore the offscreen-buffer clip, so a partially scrolled-out input
        // must be clipped via its own window region or it floats over the toolbar.
        bool partial = visible && visiblePart != bounds;
        if (partial)
        {
            Region old = box.Region;
            box.Region = new Region(new Rectangle(visiblePart.X - bounds.X, visiblePart.Y - bounds.Y, visiblePart.Width, visiblePart.Height));
            if (old != null)
            {
                old.Dispose();
            }
        }
        else if (box.Region != null)
        {
            Region old = box.Region;
            box.Region = null;
            old.Dispose();
        }

        if (box.Visible != visible)
        {
            box.Visible = visible;
        }
    }

    private void AddHit(Rectangle bounds, Hit kind, string status = "", int index = -1, bool enabled = true)
    {
        bounds.Offset(this.hitOffset);
        if (this.hitClip != Rectangle.Empty)
        {
            bounds = Rectangle.Intersect(bounds, this.hitClip);
        }

        if (bounds.Width > 0 && bounds.Height > 0)
        {
            this.hitTargets.Add(new HitTarget { Bounds = bounds, Kind = kind, Status = status, Index = index, Enabled = enabled });
        }
    }

    private HitTarget HitTest(Point location)
    {
        for (int i = this.hitTargets.Count - 1; i >= 0; i--)
        {
            if (this.hitTargets[i].Bounds.Contains(location))
            {
                return this.hitTargets[i];
            }
        }

        return null;
    }

    private static string BuildTimeline(SpecBoardRow row)
    {
        List<string> values = new List<string>();
        AddTime(values, "登记", row.RegisteredUtc);
        AddTime(values, "执行", row.ExecutedUtc);
        AddTime(values, "验证", row.VerifiedUtc);
        AddTime(values, "修改", row.RevisionRequestedUtc);
        AddTime(values, "中断", row.AbandonedUtc);
        return values.Count == 0 ? "—" : string.Join("  ·  ", values.ToArray());
    }

    private static void AddTime(List<string> values, string label, DateTime? utc)
    {
        if (utc.HasValue)
        {
            values.Add(label + " " + utc.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        }
    }

    // ── interaction ─────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (this.dragThumb != Hit.None)
        {
            int delta = e.Y - this.dragThumbStartY;
            if (this.dragThumb == Hit.ListThumb)
            {
                int rowHeight = LineHeight(this.bodyFont) + S(8);
                int contentHeight = this.viewRows.Count * rowHeight;
                int viewport = this.listRect.Height - S(8);
                float ratio = (float)(contentHeight - viewport) / Math.Max(1, viewport - S(24));
                this.listScroll = Math.Max(0, Math.Min(contentHeight - viewport, this.dragThumbStartOffset + (int)(delta * ratio)));
            }
            else
            {
                int viewport = this.rightRect.Height;
                float ratio = (float)(this.rightContentHeight - viewport) / Math.Max(1, viewport - S(24));
                this.rightScroll = Math.Max(0, Math.Min(Math.Max(0, this.rightContentHeight - viewport), this.dragThumbStartOffset + (int)(delta * ratio)));
            }

            Invalidate();
            return;
        }

        HitTarget target = HitTest(e.Location);
        Hit kind = target == null ? Hit.None : target.Kind;
        string status = target == null ? string.Empty : target.Status;
        int index = target == null ? -1 : target.Index;
        if (kind != this.hoverKind || !string.Equals(status, this.hoverStatus, StringComparison.OrdinalIgnoreCase) || index != this.hoverIndex)
        {
            this.hoverKind = kind;
            this.hoverStatus = status;
            this.hoverIndex = index;
            this.Cursor = kind == Hit.None ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (this.hoverKind != Hit.None)
        {
            this.hoverKind = Hit.None;
            this.hoverStatus = string.Empty;
            this.hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        HitTarget target = HitTest(e.Location);
        if (target == null)
        {
            this.ActiveControl = null;
            return;
        }

        if (target.Kind == Hit.ListThumb || target.Kind == Hit.RightThumb)
        {
            this.dragThumb = target.Kind;
            this.dragThumbStartY = e.Y;
            this.dragThumbStartOffset = target.Kind == Hit.ListThumb ? this.listScroll : this.rightScroll;
            return;
        }

        if (!target.Enabled)
        {
            return;
        }

        switch (target.Kind)
        {
            case Hit.Close:
                Close();
                break;
            case Hit.ProjectFilter:
                ShowProjectMenu(target.Bounds);
                break;
            case Hit.StatusChip:
                this.statusFilter = string.Equals(this.statusFilter, target.Status, StringComparison.OrdinalIgnoreCase) ? string.Empty : target.Status;
                BuildView(null);
                Invalidate();
                break;
            case Hit.BatchRegister:
                BatchRegisterUnregistered();
                break;
            case Hit.ListRow:
                HandleRowClick(target.Index, ModifierKeys);
                break;
            case Hit.Capsule:
                SetStatusForSelection(target.Status);
                break;
            case Hit.NoteSave:
                SaveNote();
                break;
            case Hit.OpenFile:
                OpenSelectedFile();
                break;
            case Hit.Reveal:
                RevealSelectedFile();
                break;
            case Hit.DangerToggle:
                ToggleDanger();
                break;
            case Hit.DangerDeleteRow:
                DeleteSelectedLedgerRow();
                break;
            case Hit.DangerDeleteFile:
                DeleteSelectedWithFile();
                break;
            case Hit.RegisterRow:
                RegisterSelected();
                break;
            case Hit.BatchDelete:
                DeleteBatchRows();
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        this.dragThumb = Hit.None;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        HitTarget target = HitTest(e.Location);
        if (target != null && target.Kind == Hit.ListRow)
        {
            OpenSelectedFile();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int step = S(48) * Math.Sign(e.Delta) * -1;
        if (this.listRect.Contains(e.Location))
        {
            this.listScroll = Math.Max(0, this.listScroll + step);
            Invalidate();
        }
        else if (this.rightRect.Contains(e.Location))
        {
            this.rightScroll = Math.Max(0, this.rightScroll + step);
            Invalidate();
        }
    }

    private void HandleRowClick(int index, Keys modifiers)
    {
        if (index < 0 || index >= this.viewRows.Count)
        {
            return;
        }

        if (!ConfirmDiscardDirtyNote())
        {
            return;
        }

        string id = this.viewRows[index].Id ?? string.Empty;
        if ((modifiers & Keys.Control) == Keys.Control)
        {
            if (!this.selectedIds.Remove(id))
            {
                this.selectedIds.Add(id);
            }

            this.selectionAnchor = index;
        }
        else if ((modifiers & Keys.Shift) == Keys.Shift && this.selectionAnchor >= 0 && this.selectionAnchor < this.viewRows.Count)
        {
            this.selectedIds.Clear();
            int from = Math.Min(this.selectionAnchor, index);
            int to = Math.Max(this.selectionAnchor, index);
            for (int i = from; i <= to; i++)
            {
                this.selectedIds.Add(this.viewRows[i].Id ?? string.Empty);
            }
        }
        else
        {
            this.selectedIds.Clear();
            this.selectedIds.Add(id);
            this.selectionAnchor = index;
        }

        OnSelectionChanged();
        Invalidate();
    }

    private void OnSelectionChanged()
    {
        // A fresh selection must not inherit the previous spec's scroll position or an armed danger zone.
        this.rightScroll = 0;
        this.dangerExpanded = false;
        this.confirmBox.Text = string.Empty;
        SpecBoardRow row = SingleRow();
        if (row == null || row.IsUnregistered)
        {
            this.noteRowId = string.Empty;
            this.noteDirty = false;
        }
    }

    private bool ConfirmDiscardDirtyNote()
    {
        if (!this.noteDirty)
        {
            return true;
        }

        DialogResult result = MessageBox.Show(this, "当前备注尚未保存，放弃修改？", "未保存的备注", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result == DialogResult.Yes)
        {
            this.noteDirty = false;
            this.noteRowId = string.Empty;
            return true;
        }

        return false;
    }

    private void ShowProjectMenu(Rectangle anchor)
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        menu.BackColor = DesignTokens.Colors.Surface;
        menu.ForeColor = DesignTokens.Colors.TextStrong;
        menu.ShowImageMargin = false;
        List<string> projects = new List<string> { string.Empty };
        projects.AddRange(this.snapshot.Rows.Select(row => row.Project ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        foreach (string project in projects)
        {
            string captured = project;
            ToolStripMenuItem item = new ToolStripMenuItem(project.Length == 0 ? "全部项目" : project)
            {
                Checked = string.Equals(this.projectFilter, project, StringComparison.OrdinalIgnoreCase),
                ForeColor = DesignTokens.Colors.TextStrong
            };
            item.Click += delegate
            {
                this.projectFilter = captured;
                BuildView(null);
                Invalidate();
            };
            menu.Items.Add(item);
        }

        menu.Closed += delegate { menu.Dispose(); };
        menu.Show(this, new Point(anchor.Left, anchor.Bottom + S(2)));
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return DesignTokens.Colors.ControlActive; } }
        public override Color MenuItemBorder { get { return DesignTokens.Colors.Border; } }
        public override Color ToolStripDropDownBackground { get { return DesignTokens.Colors.Surface; } }
        public override Color ImageMarginGradientBegin { get { return DesignTokens.Colors.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return DesignTokens.Colors.Surface; } }
        public override Color ImageMarginGradientEnd { get { return DesignTokens.Colors.Surface; } }
        public override Color MenuBorder { get { return DesignTokens.Colors.Border; } }
    }

    // ── ledger operations ───────────────────────────────────────────────────

    internal void SetStatusForSelection(string status)
    {
        List<SpecBoardRow> rows = SelectedRows();
        if (rows.Count == 0)
        {
            return;
        }

        string reselect = rows.Count == 1 ? rows[0].Id : null;
        string error;
        if (!SpecBoardLedgerStore.TrySetStatus(this.settings.SpecBoardLedgerPath, rows, status, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            this.noteRowId = string.Empty;
            LoadSnapshot(reselect);
        }
    }

    internal void SaveNote()
    {
        SpecBoardRow row = SingleRow();
        if (row == null || row.IsUnregistered || !this.noteDirty)
        {
            return;
        }

        string error;
        if (!SpecBoardLedgerStore.TrySetNote(this.settings.SpecBoardLedgerPath, row, this.noteBox.Text, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            this.noteDirty = false;
            this.noteRowId = string.Empty;
            LoadSnapshot(row.Id);
        }
    }

    internal void RegisterSelected()
    {
        SpecBoardRow row = SingleRow();
        if (row == null || !row.IsUnregistered)
        {
            return;
        }

        string error;
        if (!SpecBoardLedgerStore.TryRegister(this.settings.SpecBoardLedgerPath, row, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            LoadSnapshot(null);
        }
    }

    private void BatchRegisterUnregistered()
    {
        List<SpecBoardRow> rows = this.snapshot.Rows.Where(row => row.IsUnregistered).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(this, "将 " + rows.Count + " 个未登记 spec 全部登记为未执行？", "批量登记", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        string error;
        if (!SpecBoardLedgerStore.TrySetStatus(this.settings.SpecBoardLedgerPath, rows, SpecBoardStatus.Pending, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            LoadSnapshot(null);
        }
    }

    private void DeleteBatchRows()
    {
        List<SpecBoardRow> rows = SelectedRows().Where(row => !row.IsUnregistered).ToList();
        if (rows.Count < 1)
        {
            return;
        }

        if (MessageBox.Show(this, "只删除所选 " + rows.Count + " 条账本记录，源文件全部保留。", "批量删除账本条目", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        string error;
        if (!SpecBoardLedgerStore.TryRemoveRows(this.settings.SpecBoardLedgerPath, rows, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            LoadSnapshot(null);
        }
    }

    private void DeleteSelectedLedgerRow()
    {
        SpecBoardRow row = SingleRow();
        if (row == null || row.IsUnregistered)
        {
            return;
        }

        if (MessageBox.Show(this, "只删除账本条目，源文件保留：\r\n" + row.Title, "删除账本条目", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        string error;
        if (!SpecBoardLedgerStore.TryRemoveRows(this.settings.SpecBoardLedgerPath, new[] { row }, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            LoadSnapshot(null);
        }
    }

    private void DeleteSelectedWithFile()
    {
        SpecBoardRow row = SingleRow();
        if (row == null || row.IsUnregistered || !IsDeleteFileEnabled(row))
        {
            return;
        }

        string indexPath;
        if (SpecBoardLedgerStore.IsReferencedByTechnicalIndex(row, out indexPath) &&
            MessageBox.Show(this, "该文件仍被以下索引引用：\r\n" + indexPath + "\r\n\r\n请先清理索引；选择“是”表示我知道，仍要删除。", "索引引用阻断", MessageBoxButtons.YesNo, MessageBoxIcon.Stop) != DialogResult.Yes)
        {
            return;
        }

        if (MessageBox.Show(this, "源文件将移入 Windows 回收站，随后删除账本条目：\r\n" + row.AbsolutePath, "最终确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        string error;
        if (!SpecBoardLedgerStore.TryRemoveRowAndRecycleFile(this.settings.SpecBoardLedgerPath, row, out error))
        {
            ShowWriteError(error);
        }
        else
        {
            LoadSnapshot(null);
        }
    }

    internal void ToggleDanger()
    {
        this.dangerExpanded = !this.dangerExpanded;
        if (!this.dangerExpanded)
        {
            this.confirmBox.Text = string.Empty;
        }
        else
        {
            // Scroll the danger zone into view; DrawDetail clamps this to the real maximum.
            this.rightScroll = int.MaxValue / 2;
        }

        Invalidate();
    }

    private void OpenSelectedFile()
    {
        SpecBoardRow row = SingleRow();
        string target = row == null
            ? string.Empty
            : SpecBoardPathPolicy.ResolveOpenTarget(row.ProjectRoot, row.SpecPath);
        if (!string.IsNullOrEmpty(target))
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
    }

    private void RevealSelectedFile()
    {
        SpecBoardRow row = SingleRow();
        if (row == null)
        {
            return;
        }

        string path = SpecBoardPathPolicy.ResolveRevealTarget(row.ProjectRoot, row.SpecPath);
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
        }
        else if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
    }

    private void ShowWriteError(string error)
    {
        MessageBox.Show(this, error + "\r\n\r\n列表将重新读取磁盘最新状态。", "Spec Board 写入冲突或失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        LoadSnapshot(null);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CancelSnapshotLoad();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelSnapshotLoad();
            this.titleBarFont.Dispose();
            this.headingFont.Dispose();
            this.smallFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // ── selection helpers for tests and render harness ──────────────────────

    internal bool SelectById(string id)
    {
        if (!ConfirmDiscardDirtyNote())
        {
            return false;
        }

        for (int i = 0; i < this.viewRows.Count; i++)
        {
            if (string.Equals(this.viewRows[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                this.selectedIds.Clear();
                this.selectedIds.Add(this.viewRows[i].Id ?? string.Empty);
                this.selectionAnchor = i;
                OnSelectionChanged();
                Invalidate();
                return true;
            }
        }

        return false;
    }

    internal void SelectMany(IEnumerable<string> ids)
    {
        this.selectedIds.Clear();
        this.selectedIds.AddRange(ids);
        OnSelectionChanged();
        Invalidate();
    }

    // ── render harness ──────────────────────────────────────────────────────

    internal static void RenderSamples(string outputDir, bool sample, bool current)
    {
        Directory.CreateDirectory(outputDir);
        if (sample)
        {
            RenderFixtureSamples(outputDir);
        }

        if (current)
        {
            RenderCurrentSamples(outputDir);
        }
    }

    private static void RenderFixtureSamples(string outputDir)
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-manager-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ledger = CreateRenderFixture(root);
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.SpecBoardLedgerPath = ledger;
            using (SpecBoardManagerForm form = new SpecBoardManagerForm(settings))
            {
                PositionForCapture(form);
                form.Show();
                form.WaitForSnapshotLoadForTest(5000);
                form.SelectById("Fixture.quota_chain_hardening");
                form.ToggleDanger();
                Application.DoEvents();
                form.Refresh();
                SaveFormBitmap(form, Path.Combine(outputDir, "specboardmanager-detail.png"));

                form.SelectMany(form.viewRows.Where(row => !row.IsUnregistered).Take(3).Select(row => row.Id ?? string.Empty));
                Application.DoEvents();
                form.Refresh();
                SaveFormBitmap(form, Path.Combine(outputDir, "specboardmanager-batch.png"));

                form.Size = form.MinimumSize;
                PositionForCapture(form);
                form.SelectById("Fixture.quota_chain_hardening");
                form.ToggleDanger();
                Application.DoEvents();
                form.Refresh();
                SaveFormBitmap(form, Path.Combine(outputDir, "specboardmanager-min.png"));
                form.Close();
                Application.DoEvents();
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void RenderCurrentSamples(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (SpecBoardManagerForm form = new SpecBoardManagerForm(settings))
        {
            PositionForCapture(form);
            form.Show();
            form.WaitForSnapshotLoadForTest(5000);
            SelectLongest(form, false);
            Application.DoEvents();
            form.Refresh();
            SaveFormBitmap(form, Path.Combine(outputDir, "specboardmanager-current-detail.png"));

            SelectLongest(form, true);
            Application.DoEvents();
            form.Refresh();
            SaveFormBitmap(form, Path.Combine(outputDir, "specboardmanager-current-unregistered.png"));
            form.Close();
            Application.DoEvents();
        }
    }

    private static void SelectLongest(SpecBoardManagerForm form, bool unregistered)
    {
        SpecBoardRow best = null;
        foreach (SpecBoardRow row in form.viewRows)
        {
            if (row.IsUnregistered != unregistered)
            {
                continue;
            }

            if (best == null || (row.Title ?? string.Empty).Length > (best.Title ?? string.Empty).Length)
            {
                best = row;
            }
        }

        if (best != null)
        {
            form.SelectById(best.Id);
        }
    }

    private static void PositionForCapture(SpecBoardManagerForm form)
    {
        form.StartPosition = FormStartPosition.Manual;
        // Center of the work area: the user's layered widgets hug the screen edges and are
        // also topmost, so an edge-positioned capture would photograph them over the form.
        Rectangle captureArea = Screen.PrimaryScreen.WorkingArea;
        form.Location = new Point(
            captureArea.Left + Math.Max(0, (captureArea.Width - form.Width) / 2),
            captureArea.Top + Math.Max(0, (captureArea.Height - form.Height) / 2));
        form.TopMost = true;
    }

    private static string CreateRenderFixture(string root)
    {
        string projectRoot = Path.Combine(root, "DesktopCodexAssistant");
        string technical = Path.Combine(projectRoot, "Docs", "Technical");
        Directory.CreateDirectory(technical);
        string ledger = Path.Combine(root, "SPEC_BOARD.jsonl");
        string rootJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(projectRoot);
        File.WriteAllText(
            Path.Combine(root, "PROJECTS.json"),
            "{\"schema_version\":1,\"projects\":[{\"name\":\"DesktopCodexAssistant\",\"root\":" + rootJson + ",\"spec_glob\":\"Docs/Technical/*-SPEC-*.md\"}]}",
            SharedEncoding.Utf8NoBom);
        string[] fixtureFiles =
        {
            "Codex-ClaudeQuotaChainHardening-SPEC-v1.0.5.05-20260711-134303.md",
            "Fable5-SpecBoardManagerAndFreshness-SPEC-v1.0.5.11-20260712-013045.md",
            "Codex-RadarHardeningFollowUp-SPEC-v1.0.5.05-20260711-132944.md",
            "Codex-RadarBackendReuse-SPEC-v1.0.4.77-20260709-102446.md",
            "Fable5-NetworkMonitor-Layout-SPEC-v1.0.4.18-20260707-004500.md",
            "Codex-Unregistered-Fixture-SPEC-v1.0.5.15-20260712-090000.md"
        };
        foreach (string name in fixtureFiles)
        {
            File.WriteAllText(Path.Combine(technical, name), "fixture", SharedEncoding.Utf8NoBom);
        }

        string[] rows =
        {
            "{\"schema_version\":1,\"id\":\"Fixture.quota_chain_hardening\",\"project\":\"DesktopCodexAssistant\",\"spec_path\":\"Docs/Technical/" + fixtureFiles[0] + "\",\"title\":\"Claude 个人额度获取链路完整性加固（站点来源红色重置标记 + oauth 优先级 + 新鲜度门槛）\",\"status\":\"pending\",\"registered_utc\":\"2026-07-11T04:43:03Z\",\"updated_utc\":\"2026-07-11T04:43:03Z\",\"updated_by\":\"Fable5\",\"note\":\"等 1.0.5.09 稳定后排期；注意与 RadarHardeningFollowUp Phase 6 的衔接顺序，先做站点标记再动来源优先级。\"}",
            "{\"schema_version\":1,\"id\":\"Fixture.manager_freshness\",\"project\":\"DesktopCodexAssistant\",\"spec_path\":\"Docs/Technical/" + fixtureFiles[1] + "\",\"title\":\"Spec Board 管理窗口 + 项目栏新鲜度标记\",\"status\":\"needs_revision\",\"registered_utc\":\"2026-07-11T16:30:45Z\",\"revision_requested_utc\":\"2026-07-11T18:02:11Z\",\"updated_utc\":\"2026-07-11T18:02:11Z\",\"updated_by\":\"Codex\",\"note\":\"G6 并发用例描述与实现不符，需修订 spec。\"}",
            "{\"schema_version\":1,\"id\":\"Fixture.hardening_followup\",\"project\":\"DesktopCodexAssistant\",\"spec_path\":\"Docs/Technical/" + fixtureFiles[2] + "\",\"title\":\"Radar 加固跟进（错误码语义与日志降噪）\",\"status\":\"awaiting_verify\",\"registered_utc\":\"2026-07-11T04:29:44Z\",\"executed_utc\":\"2026-07-11T09:12:00Z\",\"updated_utc\":\"2026-07-11T09:12:00Z\",\"updated_by\":\"Codex\",\"note\":\"\"}",
            "{\"schema_version\":1,\"id\":\"Fixture.backend_reuse\",\"project\":\"DesktopCodexAssistant\",\"spec_path\":\"Docs/Technical/" + fixtureFiles[3] + "\",\"title\":\"Radar 后端复用边界统一\",\"status\":\"done\",\"registered_utc\":\"2026-07-09T01:24:46Z\",\"executed_utc\":\"2026-07-09T02:15:30Z\",\"verified_utc\":\"2026-07-09T03:00:00Z\",\"updated_utc\":\"2026-07-09T03:00:00Z\",\"updated_by\":\"Codex\",\"note\":\"\"}",
            "{\"schema_version\":1,\"id\":\"Fixture.netmon_rowplan\",\"project\":\"DesktopCodexAssistant\",\"spec_path\":\"Docs/Technical/" + fixtureFiles[4] + "\",\"title\":\"网络监控 RowPlan 自适应布局重构\",\"status\":\"abandoned\",\"registered_utc\":\"2026-07-06T15:45:00Z\",\"abandoned_utc\":\"2026-07-07T02:10:00Z\",\"abandoned_reason\":\"用户改选分组卡片方案\",\"updated_utc\":\"2026-07-07T02:10:00Z\",\"updated_by\":\"Fable5\",\"note\":\"由 1.0.4.31 分组卡片替代。\"}"
        };
        File.WriteAllText(ledger, string.Join("\n", rows) + "\n", SharedEncoding.Utf8NoBom);
        return ledger;
    }

    private static void SaveFormBitmap(Form form, string path)
    {
        // CopyFromScreen captures the real composited pixels; DrawToBitmap (WM_PRINT) hides
        // z-order overlap defects, which is exactly what this command exists to catch.
        Rectangle bounds = form.RectangleToScreen(form.ClientRectangle);
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        bool onScreen = screen.Contains(bounds.Location) && screen.Contains(new Point(bounds.Right - 1, bounds.Bottom - 1));
        using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
        {
            if (onScreen)
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }
            }
            else
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, bounds.Width, bounds.Height));
            }

            RenderSampleSupport.ApplyProofLuminance(bitmap);
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine(path + " (" + bounds.Width + "x" + bounds.Height + (onScreen ? ", screen" : ", wm_print") + ")");
        }
    }

    // ── self test ───────────────────────────────────────────────────────────

    internal static void RunSelfTest()
    {
        // --test-specboard-manager enters through this method, so keep the bounded reader and
        // cancellation fixtures on that acceptance path rather than relying on --test-layout.
        SpecBoardReader.RunBoundedReadSelfTest();
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-manager-form-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ledger = CreateRenderFixture(root);
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.SpecBoardLedgerPath = ledger;
            settings.SpecBoardManagerWidth = 811;
            settings.SpecBoardManagerHeight = 633;
            using (SpecBoardManagerForm form = new SpecBoardManagerForm(settings))
            {
                // The window must be on-screen: Windows never delivers WM_PAINT to a fully
                // off-screen window, and the hit target table only exists after a real paint.
                PositionForCapture(form);
                form.Show();
                int uiThreadId = Thread.CurrentThread.ManagedThreadId;
                form.WaitForSnapshotLoadForTest(5000);
                if (form.lastSnapshotReaderThreadId <= 0 || form.lastSnapshotReaderThreadId == uiThreadId ||
                    !ShouldApplySnapshotLoad(4, 4, false) ||
                    ShouldApplySnapshotLoad(3, 4, false) ||
                    ShouldApplySnapshotLoad(4, 4, true))
                {
                    throw new InvalidOperationException(
                        "Spec Board manager background-thread or stale-result guard self-test failed. UI=" +
                        uiThreadId + ", Reader=" + form.lastSnapshotReaderThreadId + ".");
                }
                Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                int expectedWidth = Math.Min(form.S(811), Math.Max(form.S(WidgetSettings.MinSpecBoardManagerWidth), workArea.Width - form.S(48)));
                int expectedHeight = Math.Min(form.S(633), Math.Max(form.S(WidgetSettings.MinSpecBoardManagerHeight), workArea.Height - form.S(48)));
                if (form.Width != expectedWidth || form.Height != expectedHeight || form.FormBorderStyle != FormBorderStyle.None || !form.ShowInTaskbar)
                {
                    throw new InvalidOperationException("Spec Board manager window chrome defaults failed.");
                }

                if (!form.SelectById("Fixture.quota_chain_hardening"))
                {
                    throw new InvalidOperationException("Spec Board manager fixture selection failed.");
                }

                form.Refresh();
                int capsuleCount = form.hitTargets.Count(target => target.Kind == Hit.Capsule);
                if (capsuleCount != SpecBoardStatus.LedgerValues.Length)
                {
                    throw new InvalidOperationException("Spec Board manager capsule hit targets missing: " + capsuleCount);
                }

                form.SetStatusForSelection(SpecBoardStatus.NeedsRevision);
                form.WaitForSnapshotLoadForTest(5000);
                SpecBoardRow revision = SpecBoardReader.Read(ledger, true).Rows.First(row => row.Id == "Fixture.quota_chain_hardening");
                if (revision.Status != SpecBoardStatus.NeedsRevision || revision.UpdatedBy != "User (SpecBoardManager)")
                {
                    throw new InvalidOperationException("Spec Board manager capsule status path failed.");
                }

                form.SelectById("Fixture.quota_chain_hardening");
                form.Refresh();
                form.noteBox.Text = "manager form note";
                form.SaveNote();
                form.WaitForSnapshotLoadForTest(5000);
                if (SpecBoardReader.Read(ledger, true).Rows.First(row => row.Id == "Fixture.quota_chain_hardening").Note != "manager form note")
                {
                    throw new InvalidOperationException("Spec Board manager note path failed.");
                }

                SpecBoardRow unregistered = form.snapshot.Rows.First(row => row.IsUnregistered);
                form.SelectById(unregistered.Id);
                form.RegisterSelected();
                form.WaitForSnapshotLoadForTest(5000);
                if (!SpecBoardReader.Read(ledger, true).Rows.Any(row => !row.IsUnregistered && row.SpecPath.EndsWith("Codex-Unregistered-Fixture-SPEC-v1.0.5.15-20260712-090000.md", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Spec Board manager registration path failed.");
                }

                form.SelectMany(form.viewRows.Where(row => !row.IsUnregistered).Select(row => row.Id ?? string.Empty));
                if (form.SelectedRows().Count < 3)
                {
                    throw new InvalidOperationException("Spec Board manager batch selection failed.");
                }

                form.SetStatusForSelection(SpecBoardStatus.Done);
                form.WaitForSnapshotLoadForTest(5000);
                if (SpecBoardReader.Read(ledger, true).Rows.Where(row => !row.IsUnregistered).Any(row => row.Status != SpecBoardStatus.Done))
                {
                    throw new InvalidOperationException("Spec Board manager batch status path failed.");
                }

                form.SelectById("Fixture.quota_chain_hardening");
                form.Refresh();
                SpecBoardRow target = form.SingleRow();
                form.ToggleDanger();
                form.confirmBox.Text = "wrong-name.md";
                if (form.IsDeleteFileEnabled(target))
                {
                    throw new InvalidOperationException("Spec Board manager typed confirmation should gate file deletion.");
                }

                form.confirmBox.Text = "Codex-ClaudeQuotaChainHardening-SPEC-v1.0.5.05-20260711-134303.md";
                if (!form.IsDeleteFileEnabled(target))
                {
                    throw new InvalidOperationException("Spec Board manager typed confirmation match failed.");
                }

                form.SelectById("Fixture.manager_freshness");
                if (form.dangerExpanded)
                {
                    throw new InvalidOperationException("Spec Board manager danger zone must collapse on selection change.");
                }

                form.Close();
                Application.DoEvents();
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
