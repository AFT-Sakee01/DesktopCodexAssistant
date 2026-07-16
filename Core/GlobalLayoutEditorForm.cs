using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

internal sealed class GlobalLayoutEditorForm : Form
{
    private const int WindowConnectDistanceLimit = 500;
    private const string SpecBoardModuleId = "SpecBoard";
    private static readonly Color TransparentKeyColor = Color.FromArgb(7, 3, 11);

    private readonly Action<WidgetSettings> previewAction;
    private readonly Action refreshWindowStackAction;
    private readonly Rectangle virtualBounds;
    private readonly List<LayoutItem> items = new List<LayoutItem>();
    private readonly Font labelFont;
    private readonly Font hintFont;
    private readonly Pen guidePen;
    private readonly Pen inactivePen;
    private readonly Pen activePen;
    private readonly Brush labelBackBrush;
    private readonly Brush labelTextBrush;
    private readonly Brush activeFillBrush;
    private WidgetSettings workingSettings;
    private MaskForm maskForm;
    private int activeIndex = -1;
    private bool dragging;
    private Point dragOffset;
    private bool processingPreview;

    public GlobalLayoutEditorForm(
        WidgetSettings settings,
        Action<WidgetSettings> previewAction,
        Action refreshWindowStackAction)
    {
        this.workingSettings = settings.Clone();
        this.workingSettings.Normalize();
        this.previewAction = previewAction;
        this.refreshWindowStackAction = refreshWindowStackAction;
        this.virtualBounds = GetVirtualScreenBounds();

        this.labelFont = new Font("Segoe UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point);
        this.hintFont = new Font("Segoe UI", 14.0f, FontStyle.Bold, GraphicsUnit.Point);
        this.guidePen = new Pen(Color.White, 2.0f);
        this.inactivePen = new Pen(Color.FromArgb(210, 255, 255, 255), 1.5f);
        this.activePen = new Pen(Color.White, 3.0f);
        this.labelBackBrush = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
        this.labelTextBrush = new SolidBrush(Color.White);
        this.activeFillBrush = new SolidBrush(Color.FromArgb(42, 255, 255, 255));

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.Bounds = this.virtualBounds;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.KeyPreview = true;
        this.DoubleBuffered = true;
        this.BackColor = TransparentKeyColor;
        this.TransparencyKey = TransparentKeyColor;
        this.Cursor = Cursors.SizeAll;
        this.Text = "全局布局编辑";

        RebuildItemsFromSettings();
    }

    public WidgetSettings EditedSettings { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        this.maskForm = new MaskForm(this.virtualBounds);
        this.maskForm.Show();
        RefreshWindowStack();
        this.BringToFront();
        this.Activate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (this.maskForm != null)
        {
            this.maskForm.Close();
            this.maskForm.Dispose();
            this.maskForm = null;
        }

        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.labelFont.Dispose();
            this.hintFont.Dispose();
            this.guidePen.Dispose();
            this.inactivePen.Dispose();
            this.activePen.Dispose();
            this.labelBackBrush.Dispose();
            this.labelTextBrush.Dispose();
            this.activeFillBrush.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter)
        {
            this.EditedSettings = this.workingSettings.Clone();
            this.EditedSettings.Normalize();
            this.DialogResult = DialogResult.OK;
            e.Handled = true;
            Close();
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            this.DialogResult = DialogResult.Cancel;
            e.Handled = true;
            Close();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Point cursor = ToVirtualPoint(e.Location);
        this.activeIndex = HitTest(cursor);
        if (this.activeIndex < 0)
        {
            Invalidate();
            return;
        }

        LayoutItem item = this.items[this.activeIndex];
        this.dragging = true;
        this.dragOffset = new Point(cursor.X - item.Bounds.Left, cursor.Y - item.Bounds.Top);
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point cursor = ToVirtualPoint(e.Location);
        if (!this.dragging || this.activeIndex < 0)
        {
            int hit = HitTest(cursor);
            this.Cursor = hit >= 0 ? Cursors.SizeAll : Cursors.Default;
            return;
        }

        LayoutItem item = this.items[this.activeIndex];
        Rectangle nextBounds = new Rectangle(
            cursor.X - this.dragOffset.X,
            cursor.Y - this.dragOffset.Y,
            item.Bounds.Width,
            item.Bounds.Height);
        item.Bounds = nextBounds;
        this.items[this.activeIndex] = item;
        ApplyItemBoundsToSettings(item);
        RebuildItemsFromSettings();
        this.activeIndex = IndexOfModule(item.ModuleId);
        PreviewDraggingSettings();

        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            this.dragging = false;
            Capture = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(TransparentKeyColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawTopHint(g);
        DrawItems(g);
        if (this.dragging && this.activeIndex >= 0 && this.activeIndex < this.items.Count)
        {
            DrawGuides(g, this.items[this.activeIndex]);
        }
    }

    private void RebuildItemsFromSettings()
    {
        this.items.Clear();
        this.items.Add(new LayoutItem(WidgetSettings.ModuleMain, "主窗口", GetPanelBounds(
            this.workingSettings.LeftX,
            this.workingSettings.BottomY,
            this.workingSettings.Width,
            this.workingSettings.Height)));
        this.items.Add(new LayoutItem(WidgetSettings.ModuleCodexRadar, "Codex Radar", GetPanelBounds(
            this.workingSettings.CodexRadarLeftX,
            this.workingSettings.CodexRadarBottomY,
            this.workingSettings.CodexRadarWidth,
            this.workingSettings.CodexRadarHeight)));
        this.items.Add(new LayoutItem(WidgetSettings.ModuleClaudeRadar, "Claude Radar", GetPanelBounds(
            this.workingSettings.ClaudeRadarLeftX,
            this.workingSettings.ClaudeRadarBottomY,
            this.workingSettings.ClaudeRadarWidth,
            this.workingSettings.ClaudeRadarHeight)));
        this.items.Add(new LayoutItem(WidgetSettings.ModulePowerThermal, "功耗温度", GetPanelBounds(
            this.workingSettings.PowerThermalLeftX,
            this.workingSettings.PowerThermalBottomY,
            this.workingSettings.PowerThermalWidth,
            this.workingSettings.PowerThermalHeight)));
        this.items.Add(new LayoutItem(WidgetSettings.ModuleNetworkMonitor, "网络监控", GetPanelBounds(
            this.workingSettings.NetworkMonitorLeftX,
            this.workingSettings.NetworkMonitorBottomY,
            this.workingSettings.NetworkMonitorWidth,
            this.workingSettings.NetworkMonitorHeight)));
        this.items.Add(new LayoutItem(WidgetSettings.ModuleConnectionCheck, "连接检测", GetPanelBounds(
            this.workingSettings.ConnectionCheckLeftX,
            this.workingSettings.ConnectionCheckBottomY,
            this.workingSettings.ConnectionCheckWidth,
            this.workingSettings.ConnectionCheckHeight)));
        this.items.Add(new LayoutItem(WidgetSettings.ModuleOperation, "操作面板", GetOperationBounds()));
        this.items.Add(new LayoutItem(SpecBoardModuleId, "Spec Board", GetSpecBoardBounds()));
    }

    private void PreviewDraggingSettings()
    {
        if (this.processingPreview)
        {
            return;
        }

        this.processingPreview = true;
        try
        {
            if (this.previewAction != null)
            {
                this.previewAction(this.workingSettings.Clone());
            }

            RefreshWindowStack();
            this.BringToFront();
            this.Update();
            Application.DoEvents();
        }
        finally
        {
            this.processingPreview = false;
        }
    }

    private void RefreshWindowStack()
    {
        if (this.maskForm != null)
        {
            this.maskForm.SendToBack();
        }

        if (this.refreshWindowStackAction != null)
        {
            this.refreshWindowStackAction();
        }

        if (this.maskForm != null)
        {
            this.maskForm.SendToBack();
        }
    }

    private Rectangle GetPanelBounds(int leftX, int bottomY, int width, int height)
    {
        return new Rectangle(leftX, bottomY - height + 1, Math.Max(1, width), Math.Max(1, height));
    }

    private Rectangle GetOperationBounds()
    {
        Rectangle workArea = this.workingSettings.GetWorkAreaForModule(WidgetSettings.ModuleOperation);
        float scale = GetDesktopScale();
        int width = WidgetSettings.GetOperationWindowWidth(this.workingSettings.OperationButtonSize, scale);
        int height = WidgetSettings.GetOperationWindowHeight(this.workingSettings.OperationButtonSize, scale);
        int left = workArea.Left + Math.Max(0, this.workingSettings.OperationLeftOffset);
        int top = workArea.Bottom - height - Math.Max(0, this.workingSettings.OperationBottomOffset);
        return new Rectangle(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    private Rectangle GetSpecBoardBounds()
    {
        Rectangle operation = GetOperationBounds();
        int left = this.workingSettings.SpecBoardLeftX >= 0 ? this.workingSettings.SpecBoardLeftX : operation.Left;
        int bottom = this.workingSettings.SpecBoardBottomY >= 0 ? this.workingSettings.SpecBoardBottomY : operation.Top - 10;
        return GetPanelBounds(left, bottom, this.workingSettings.SpecBoardWidth, this.workingSettings.SpecBoardHeight);
    }

    private void ApplyItemBoundsToSettings(LayoutItem item)
    {
        Screen screen = FindBestScreen(item.Bounds);
        if (screen == null)
        {
            screen = Screen.PrimaryScreen;
        }

        SetModuleDisplayDeviceName(item.ModuleId, screen == null || screen.Primary ? string.Empty : screen.DeviceName);
        string workAreaModule = string.Equals(item.ModuleId, SpecBoardModuleId, StringComparison.Ordinal) ? WidgetSettings.ModuleOperation : item.ModuleId;
        Rectangle workArea = screen == null ? this.workingSettings.GetWorkAreaForModule(workAreaModule) : screen.WorkingArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            workArea = this.workingSettings.GetWorkAreaForModule(item.ModuleId);
        }

        int bottomY = item.Bounds.Bottom - 1;
        if (string.Equals(item.ModuleId, WidgetSettings.ModuleMain, StringComparison.Ordinal))
        {
            this.workingSettings.LeftX = item.Bounds.Left;
            this.workingSettings.BottomY = bottomY;
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModuleCodexRadar, StringComparison.Ordinal))
        {
            this.workingSettings.CodexRadarLeftX = item.Bounds.Left;
            this.workingSettings.CodexRadarBottomY = bottomY;
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModuleClaudeRadar, StringComparison.Ordinal))
        {
            this.workingSettings.ClaudeRadarLeftX = item.Bounds.Left;
            this.workingSettings.ClaudeRadarBottomY = bottomY;
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModulePowerThermal, StringComparison.Ordinal))
        {
            this.workingSettings.PowerThermalLeftX = item.Bounds.Left;
            this.workingSettings.PowerThermalBottomY = bottomY;
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModuleNetworkMonitor, StringComparison.Ordinal))
        {
            this.workingSettings.NetworkMonitorLeftX = item.Bounds.Left;
            this.workingSettings.NetworkMonitorBottomY = bottomY;
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModuleConnectionCheck, StringComparison.Ordinal))
        {
            this.workingSettings.ConnectionCheckLeftX = item.Bounds.Left;
            this.workingSettings.ConnectionCheckBottomY = bottomY;
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModuleOperation, StringComparison.Ordinal))
        {
            this.workingSettings.OperationLeftOffset = item.Bounds.Left - workArea.Left;
            this.workingSettings.OperationBottomOffset = workArea.Bottom - item.Bounds.Bottom;
        }
        else if (string.Equals(item.ModuleId, SpecBoardModuleId, StringComparison.Ordinal))
        {
            this.workingSettings.SpecBoardLeftX = item.Bounds.Left;
            this.workingSettings.SpecBoardBottomY = bottomY;
        }

        this.workingSettings.Normalize();
    }

    private void SetModuleDisplayDeviceName(string moduleId, string displayDeviceName)
    {
        displayDeviceName = WidgetSettings.NormalizeDisplayDeviceName(displayDeviceName);
        if (string.Equals(moduleId, WidgetSettings.ModuleMain, StringComparison.Ordinal))
        {
            this.workingSettings.MainDisplayDeviceName = displayDeviceName;
        }
        else if (string.Equals(moduleId, WidgetSettings.ModuleCodexRadar, StringComparison.Ordinal))
        {
            this.workingSettings.CodexRadarDisplayDeviceName = displayDeviceName;
        }
        else if (string.Equals(moduleId, WidgetSettings.ModuleClaudeRadar, StringComparison.Ordinal))
        {
            this.workingSettings.ClaudeRadarDisplayDeviceName = displayDeviceName;
        }
        else if (string.Equals(moduleId, WidgetSettings.ModulePowerThermal, StringComparison.Ordinal))
        {
            this.workingSettings.PowerThermalDisplayDeviceName = displayDeviceName;
        }
        else if (string.Equals(moduleId, WidgetSettings.ModuleNetworkMonitor, StringComparison.Ordinal))
        {
            this.workingSettings.NetworkMonitorDisplayDeviceName = displayDeviceName;
        }
        else if (string.Equals(moduleId, WidgetSettings.ModuleConnectionCheck, StringComparison.Ordinal))
        {
            this.workingSettings.ConnectionCheckDisplayDeviceName = displayDeviceName;
        }
        else if (string.Equals(moduleId, WidgetSettings.ModuleOperation, StringComparison.Ordinal))
        {
            this.workingSettings.OperationDisplayDeviceName = displayDeviceName;
        }
    }

    private int HitTest(Point cursor)
    {
        for (int i = this.items.Count - 1; i >= 0; i--)
        {
            if (this.items[i].Bounds.Contains(cursor))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfModule(string moduleId)
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            if (string.Equals(this.items[i].ModuleId, moduleId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void DrawTopHint(Graphics g)
    {
        string text = "编辑模式    Enter 保存并退出    Esc 不保存并退出";
        SizeF size = g.MeasureString(text, this.hintFont);
        float x = (this.ClientSize.Width - size.Width) / 2.0f;
        float y = 20.0f;
        RectangleF background = new RectangleF(x - 18.0f, y - 8.0f, size.Width + 36.0f, size.Height + 16.0f);
        g.FillRectangle(this.labelBackBrush, background);
        g.DrawString(text, this.hintFont, this.labelTextBrush, x, y);
    }

    private void DrawItems(Graphics g)
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            LayoutItem item = this.items[i];
            Rectangle bounds = ToClient(item.Bounds);
            bool active = i == this.activeIndex;
            if (active)
            {
                g.FillRectangle(this.activeFillBrush, bounds);
            }

            g.DrawRectangle(active ? this.activePen : this.inactivePen, bounds);
            DrawLabel(g, item.Name, new Point(bounds.Left + 8, bounds.Top + 8));
        }
    }

    private void DrawGuides(Graphics g, LayoutItem activeItem)
    {
        Rectangle bounds = activeItem.Bounds;
        Screen screen = FindBestScreen(bounds);
        Rectangle screenBounds = screen == null ? this.virtualBounds : screen.Bounds;
        DrawScreenDistanceLine(g, new Point(bounds.Left, bounds.Top + bounds.Height / 2), new Point(screenBounds.Left, bounds.Top + bounds.Height / 2), Math.Max(0, bounds.Left - screenBounds.Left));
        DrawScreenDistanceLine(g, new Point(bounds.Right, bounds.Top + bounds.Height / 2), new Point(screenBounds.Right, bounds.Top + bounds.Height / 2), Math.Max(0, screenBounds.Right - bounds.Right));
        DrawScreenDistanceLine(g, new Point(bounds.Left + bounds.Width / 2, bounds.Top), new Point(bounds.Left + bounds.Width / 2, screenBounds.Top), Math.Max(0, bounds.Top - screenBounds.Top));
        DrawScreenDistanceLine(g, new Point(bounds.Left + bounds.Width / 2, bounds.Bottom), new Point(bounds.Left + bounds.Width / 2, screenBounds.Bottom), Math.Max(0, screenBounds.Bottom - bounds.Bottom));

        for (int i = 0; i < this.items.Count; i++)
        {
            LayoutItem other = this.items[i];
            if (string.Equals(other.ModuleId, activeItem.ModuleId, StringComparison.Ordinal))
            {
                continue;
            }

            ConnectionLine connection;
            if (TryGetConnectionLine(bounds, other.Bounds, out connection))
            {
                DrawScreenDistanceLine(g, connection.Start, connection.End, connection.Distance);
            }
        }
    }

    private void DrawScreenDistanceLine(Graphics g, Point start, Point end, int distance)
    {
        Point clientStart = ToClient(start);
        Point clientEnd = ToClient(end);
        g.DrawLine(this.guidePen, clientStart, clientEnd);
        Point labelPoint = new Point((clientStart.X + clientEnd.X) / 2, (clientStart.Y + clientEnd.Y) / 2);
        DrawLabel(g, distance.ToString(CultureInfo.InvariantCulture) + " px", labelPoint);
    }

    private void DrawLabel(Graphics g, string text, Point point)
    {
        SizeF size = g.MeasureString(text, this.labelFont);
        RectangleF background = new RectangleF(
            point.X - size.Width / 2.0f - 6.0f,
            point.Y - size.Height / 2.0f - 3.0f,
            size.Width + 12.0f,
            size.Height + 6.0f);
        g.FillRectangle(this.labelBackBrush, background);
        g.DrawString(text, this.labelFont, this.labelTextBrush, background.Left + 6.0f, background.Top + 3.0f);
    }

    private bool TryGetConnectionLine(Rectangle active, Rectangle other, out ConnectionLine line)
    {
        line = new ConnectionLine();
        bool found = false;
        int bestDistance = int.MaxValue;

        int overlapTop = Math.Max(active.Top, other.Top);
        int overlapBottom = Math.Min(active.Bottom, other.Bottom);
        if (overlapBottom > overlapTop)
        {
            int y = (overlapTop + overlapBottom) / 2;
            if (active.Right <= other.Left)
            {
                found |= TryUseConnection(new Point(active.Right, y), new Point(other.Left, y), other.Left - active.Right, ref bestDistance, ref line);
            }
            else if (other.Right <= active.Left)
            {
                found |= TryUseConnection(new Point(other.Right, y), new Point(active.Left, y), active.Left - other.Right, ref bestDistance, ref line);
            }
        }

        int overlapLeft = Math.Max(active.Left, other.Left);
        int overlapRight = Math.Min(active.Right, other.Right);
        if (overlapRight > overlapLeft)
        {
            int x = (overlapLeft + overlapRight) / 2;
            if (active.Bottom <= other.Top)
            {
                found |= TryUseConnection(new Point(x, active.Bottom), new Point(x, other.Top), other.Top - active.Bottom, ref bestDistance, ref line);
            }
            else if (other.Bottom <= active.Top)
            {
                found |= TryUseConnection(new Point(x, other.Bottom), new Point(x, active.Top), active.Top - other.Bottom, ref bestDistance, ref line);
            }
        }

        return found;
    }

    private static bool TryUseConnection(Point start, Point end, int distance, ref int bestDistance, ref ConnectionLine line)
    {
        if (distance < 0 || distance >= WindowConnectDistanceLimit || distance >= bestDistance)
        {
            return false;
        }

        bestDistance = distance;
        line = new ConnectionLine(start, end, distance);
        return true;
    }

    private Screen FindBestScreen(Rectangle bounds)
    {
        Screen[] screens = Screen.AllScreens;
        Screen bestScreen = null;
        int bestArea = -1;
        for (int i = 0; i < screens.Length; i++)
        {
            Rectangle intersection = Rectangle.Intersect(bounds, screens[i].Bounds);
            int area = Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
            if (area > bestArea)
            {
                bestArea = area;
                bestScreen = screens[i];
            }
        }

        if (bestScreen != null && bestArea > 0)
        {
            return bestScreen;
        }

        Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        for (int i = 0; i < screens.Length; i++)
        {
            if (screens[i].Bounds.Contains(center))
            {
                return screens[i];
            }
        }

        return Screen.PrimaryScreen;
    }

    private Point ToVirtualPoint(Point clientPoint)
    {
        return new Point(clientPoint.X + this.virtualBounds.Left, clientPoint.Y + this.virtualBounds.Top);
    }

    private Point ToClient(Point point)
    {
        return new Point(point.X - this.virtualBounds.Left, point.Y - this.virtualBounds.Top);
    }

    private Rectangle ToClient(Rectangle rectangle)
    {
        return new Rectangle(
            rectangle.Left - this.virtualBounds.Left,
            rectangle.Top - this.virtualBounds.Top,
            rectangle.Width,
            rectangle.Height);
    }

    private static Rectangle GetVirtualScreenBounds()
    {
        Screen[] screens = Screen.AllScreens;
        if (screens == null || screens.Length == 0)
        {
            return Screen.PrimaryScreen.Bounds;
        }

        Rectangle bounds = screens[0].Bounds;
        for (int i = 1; i < screens.Length; i++)
        {
            bounds = Rectangle.Union(bounds, screens[i].Bounds);
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new Rectangle(0, 0, 1920, 1080);
        }

        return bounds;
    }

    private static float GetDesktopScale()
    {
        try
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return Math.Max(1.0f, g.DpiX / 96.0f);
            }
        }
        catch
        {
            return 1.0f;
        }
    }

    private struct LayoutItem
    {
        public LayoutItem(string moduleId, string name, Rectangle bounds)
        {
            this.ModuleId = moduleId;
            this.Name = name;
            this.Bounds = bounds;
        }

        public string ModuleId;
        public string Name;
        public Rectangle Bounds;
    }

    private struct ConnectionLine
    {
        public ConnectionLine(Point start, Point end, int distance)
        {
            this.Start = start;
            this.End = end;
            this.Distance = distance;
        }

        public Point Start;
        public Point End;
        public int Distance;
    }

    private sealed class MaskForm : Form
    {
        public MaskForm(Rectangle bounds)
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = bounds;
            this.BackColor = Color.Black;
            this.Opacity = 0.5;
            this.ShowInTaskbar = false;
            this.TopMost = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }
}
