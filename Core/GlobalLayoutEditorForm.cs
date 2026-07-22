using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

internal sealed class GlobalLayoutEditorForm : Form
{
    private const int WindowConnectDistanceLimit = 500;
    private const string LeftDockTabModuleIdPrefix = "LeftDockTab.";
    // Each metric tile is its own draggable item. The suffix after the prefix is the tile id from
    // WidgetSettings.MetricTileIds, so the editor can map an item back to its tile index.
    private const string MetricTileModuleIdPrefix = "MetricTile.";
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
        ApplyDragDelta();

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
        List<string> surfaceIds = BuildEditableSurfaceIds(this.workingSettings);
        for (int i = 0; i < surfaceIds.Count; i++)
        {
            AddLayoutItem(surfaceIds[i]);
        }
    }

    // The converged editor exposes only real on-screen surfaces: Operation, five left-dock tabs,
    // and ten right-side tiles. Headless owners and retired classic windows must never reappear
    // merely because layout editing bypasses environmental hiding.
    private static List<string> BuildEditableSurfaceIds(WidgetSettings settings)
    {
        List<string> result = new List<string>();
        if (settings == null)
        {
            return result;
        }

        result.Add(WidgetSettings.ModuleOperation);

        string[] leftOrder = WidgetSettings.NormalizeLeftDockButtonOrder(settings.LeftDockButtonOrder);
        for (int i = 0; i < leftOrder.Length; i++)
        {
            EdgeDockTabRole role;
            if (TryGetLeftDockRole(LeftDockTabModuleIdPrefix + leftOrder[i], out role))
            {
                result.Add(LeftDockTabModuleIdPrefix + leftOrder[i]);
            }
        }

        string[] rightOrder = WidgetSettings.NormalizeRightTileButtonOrder(settings.RightTileButtonOrder);
        for (int i = 0; i < rightOrder.Length; i++)
        {
            int tileIndex = WidgetSettings.IndexOfMetricTile(rightOrder[i]);
            if (tileIndex >= 0 && MetricTileForm.IsTileEnabled(settings, tileIndex))
            {
                result.Add(MetricTileModuleIdPrefix + WidgetSettings.MetricTileIds[tileIndex]);
            }
        }

        return result;
    }

    private void AddLayoutItem(string moduleId)
    {
        if (string.Equals(moduleId, WidgetSettings.ModuleOperation, StringComparison.Ordinal))
        {
            this.items.Add(new LayoutItem(moduleId, "操作面板", GetOperationBounds()));
            return;
        }

        EdgeDockTabRole role;
        if (TryGetLeftDockRole(moduleId, out role))
        {
            this.items.Add(new LayoutItem(moduleId, GetLeftDockRoleLabel(role), GetLeftDockTabBounds(role)));
            return;
        }

        int tileIndex = MetricTileIndexOf(moduleId);
        if (tileIndex >= 0)
        {
            this.items.Add(new LayoutItem(
                moduleId,
                "方块 " + MetricTileModel.GetLabel(MetricTileModel.AllOrder[tileIndex]),
                MetricTileForm.GetTileBounds(this.workingSettings, tileIndex)));
        }
    }

    // -1 when the item is not a metric tile.
    private static int MetricTileIndexOf(string moduleId)
    {
        if (moduleId == null || !moduleId.StartsWith(MetricTileModuleIdPrefix, StringComparison.Ordinal))
        {
            return -1;
        }

        return WidgetSettings.IndexOfMetricTile(moduleId.Substring(MetricTileModuleIdPrefix.Length));
    }

    private static bool TryGetLeftDockRole(string moduleId, out EdgeDockTabRole role)
    {
        role = EdgeDockTabRole.Network;
        if (moduleId == null || !moduleId.StartsWith(LeftDockTabModuleIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string id = moduleId.Substring(LeftDockTabModuleIdPrefix.Length);
        if (string.Equals(id, "Network", StringComparison.OrdinalIgnoreCase))
        {
            role = EdgeDockTabRole.Network;
            return true;
        }

        if (string.Equals(id, "SpecBoard", StringComparison.OrdinalIgnoreCase))
        {
            role = EdgeDockTabRole.SpecBoard;
            return true;
        }

        if (string.Equals(id, "CodexTask", StringComparison.OrdinalIgnoreCase))
        {
            role = EdgeDockTabRole.CodexTask;
            return true;
        }

        if (string.Equals(id, "Guard", StringComparison.OrdinalIgnoreCase))
        {
            role = EdgeDockTabRole.Guard;
            return true;
        }

        if (string.Equals(id, "CodexIq", StringComparison.OrdinalIgnoreCase))
        {
            role = EdgeDockTabRole.CodexIq;
            return true;
        }

        return false;
    }

    private static string GetLeftDockRoleLabel(EdgeDockTabRole role)
    {
        switch (role)
        {
            case EdgeDockTabRole.Network:
                return "左侧 网络";
            case EdgeDockTabRole.SpecBoard:
                return "左侧 Spec";
            case EdgeDockTabRole.CodexTask:
                return "左侧 Task";
            case EdgeDockTabRole.Guard:
                return "左侧 Guard";
            case EdgeDockTabRole.CodexIq:
                return "左侧 IQ";
            default:
                return "左侧按钮";
        }
    }

    private Rectangle GetLeftDockTabBounds(EdgeDockTabRole role)
    {
        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.workingSettings);
        float layerScale = GetLeftDockRoleLayerScale(role);
        Size size = LeftDockLayout.ResolveTabSize(this.workingSettings, role, layerScale);
        int centerY = LeftDockLayout.ResolveTabCenterY(this.workingSettings, role, layerScale);
        int top = centerY - size.Height / 2;
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
        return new Rectangle(workArea.Left, top, size.Width, size.Height);
    }

    private float GetLeftDockRoleLayerScale(EdgeDockTabRole role)
    {
        int scaleOverride = LeftDockLayout.ResolveScaleOverride(this.workingSettings, role);
        float windowScale = scaleOverride >= WidgetSettings.MinResolutionCompatibilityScalePercent
            ? Math.Min(WidgetSettings.MaxWindowScaleOverridePercent, scaleOverride) / 100.0f
            // Global layout edit deliberately disables resolution-compatibility projection before
            // showing the real tabs, so an inherited role scale is 100% for the edit preview too.
            : 1.0f;
        return Math.Max(0.25f, GetDesktopScale() * windowScale);
    }

    private void ApplyDragDelta()
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

    private void ApplyItemBoundsToSettings(LayoutItem item)
    {
        EdgeDockTabRole leftDockRole;
        if (TryGetLeftDockRole(item.ModuleId, out leftDockRole))
        {
            ApplyLeftDockTabBoundsToSettings(leftDockRole, item.Bounds);
            this.workingSettings.Normalize();
            return;
        }

        int tileIndex = MetricTileIndexOf(item.ModuleId);
        if (tileIndex >= 0 && this.workingSettings.RightTileAutoArrangeEnabled)
        {
            Rectangle currentBounds = MetricTileForm.GetTileBounds(this.workingSettings, tileIndex);
            ShiftColumnGroupOffset(this.workingSettings, false, item.Bounds.Top - currentBounds.Top);
            this.workingSettings.Normalize();
            return;
        }

        Screen screen = FindBestScreen(item.Bounds);
        if (screen == null)
        {
            screen = Screen.PrimaryScreen;
        }

        SetModuleDisplayDeviceName(item.ModuleId, screen == null || screen.Primary ? string.Empty : screen.DeviceName);
        // Tiles share the main widget work-area baseline; Operation has its own visible-module
        // baseline. No retired/headless module is allowed through this editor path.
        string workAreaModule = tileIndex >= 0
            ? WidgetSettings.ModuleMain
            : WidgetSettings.ModuleOperation;

        Rectangle workArea = screen == null ? this.workingSettings.GetWorkAreaForModule(workAreaModule) : screen.WorkingArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            workArea = this.workingSettings.GetWorkAreaForModule(workAreaModule);
        }

        int bottomY = item.Bounds.Bottom - 1;
        if (tileIndex >= 0)
        {
            this.workingSettings.SetMetricTilePosition(tileIndex, item.Bounds.Left, bottomY);
        }
        else if (string.Equals(item.ModuleId, WidgetSettings.ModuleOperation, StringComparison.Ordinal))
        {
            this.workingSettings.OperationLeftOffset = item.Bounds.Left - workArea.Left;
            this.workingSettings.OperationBottomOffset = workArea.Bottom - item.Bounds.Bottom;
        }

        this.workingSettings.Normalize();
    }

    private void ApplyLeftDockTabBoundsToSettings(EdgeDockTabRole role, Rectangle draggedBounds)
    {
        int draggedCenterY = draggedBounds.Top + draggedBounds.Height / 2;
        if (this.workingSettings.LeftDockAutoArrangeEnabled)
        {
            Rectangle currentBounds = GetLeftDockTabBounds(role);
            int currentCenterY = currentBounds.Top + currentBounds.Height / 2;
            ShiftColumnGroupOffset(this.workingSettings, true, draggedCenterY - currentCenterY);
            return;
        }

        SetLeftDockTabCenterY(this.workingSettings, role, draggedCenterY);
    }

    private static void ShiftColumnGroupOffset(WidgetSettings settings, bool leftDock, int deltaY)
    {
        long current = leftDock ? settings.LeftDockGroupOffsetY : settings.RightTileGroupOffsetY;
        long shifted = current + deltaY;
        int normalized = (int)Math.Max(
            WidgetSettings.MinColumnGroupOffsetY,
            Math.Min(WidgetSettings.MaxColumnGroupOffsetY, shifted));
        if (leftDock)
        {
            settings.LeftDockGroupOffsetY = normalized;
        }
        else
        {
            settings.RightTileGroupOffsetY = normalized;
        }
    }

    private static void SetLeftDockTabCenterY(WidgetSettings settings, EdgeDockTabRole role, int centerY)
    {
        switch (role)
        {
            case EdgeDockTabRole.Network:
                settings.NetworkMonitorLeftDockTabCenterY = centerY;
                break;
            case EdgeDockTabRole.SpecBoard:
                settings.SpecBoardLeftDockTabCenterY = centerY;
                break;
            case EdgeDockTabRole.CodexTask:
                settings.CodexTaskBoardLeftDockTabCenterY = centerY;
                break;
            case EdgeDockTabRole.Guard:
                settings.GuardBoardLeftDockTabCenterY = centerY;
                break;
            case EdgeDockTabRole.CodexIq:
                settings.CodexIqBoardLeftDockTabCenterY = centerY;
                break;
        }
    }

    private void SetModuleDisplayDeviceName(string moduleId, string displayDeviceName)
    {
        // Metric tiles have no per-tile display setting: they follow the main widget's target
        // display, so dragging one to another monitor stores absolute coordinates without
        // repointing a module baseline.
        EdgeDockTabRole ignoredRole;
        if (MetricTileIndexOf(moduleId) >= 0 || TryGetLeftDockRole(moduleId, out ignoredRole))
        {
            return;
        }

        displayDeviceName = WidgetSettings.NormalizeDisplayDeviceName(displayDeviceName);
        if (string.Equals(moduleId, WidgetSettings.ModuleOperation, StringComparison.Ordinal))
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

    internal static void RunSelfTest()
    {
        WidgetSettings edge = WidgetSettings.CreateDefaults();
        edge.SpecBoardLeftDockEnabled = true;
        edge.CodexTaskBoardLeftDockEnabled = true;
        edge.GuardBoardLeftDockEnabled = true;
        edge.CodexIqBoardLeftDockEnabled = true;
        edge.LeftDockAutoArrangeEnabled = true;
        edge.RightTileAutoArrangeEnabled = true;
        edge.Normalize();

        List<string> edgeIds = BuildEditableSurfaceIds(edge);
        List<string> expectedIds = new List<string>
        {
            WidgetSettings.ModuleOperation,
            "LeftDockTab.Network",
            "LeftDockTab.SpecBoard",
            "LeftDockTab.CodexTask",
            "LeftDockTab.Guard",
            "LeftDockTab.CodexIq",
            "MetricTile.Cpu",
            "MetricTile.Memory",
            "MetricTile.Disk",
            "MetricTile.Network",
            "MetricTile.Gpu",
            "MetricTile.Npu",
            "MetricTile.Power",
            "MetricTile.Guard",
            "MetricTile.CodexQuota",
            "MetricTile.ClaudeQuota"
        };
        if (!HaveSameSurfaceIds(edgeIds, expectedIds))
        {
            throw new InvalidOperationException(
                "Global layout editor must expose the exact canonical 16-surface plan: Operation, five dock tabs, and ten tiles.");
        }

        edge.VisibilityMode = WidgetVisibilityMode.HideWhenOverlapped;
        if (!HaveSameSurfaceIds(edgeIds, BuildEditableSurfaceIds(edge)))
        {
            throw new InvalidOperationException(
                "Environmental visibility modes must not change the structural edit-surface plan.");
        }

        edge.LeftDockGroupOffsetY = WidgetSettings.MaxColumnGroupOffsetY - 2;
        edge.RightTileGroupOffsetY = WidgetSettings.MinColumnGroupOffsetY + 2;
        ShiftColumnGroupOffset(edge, true, 50);
        ShiftColumnGroupOffset(edge, false, -50);
        if (edge.LeftDockGroupOffsetY != WidgetSettings.MaxColumnGroupOffsetY ||
            edge.RightTileGroupOffsetY != WidgetSettings.MinColumnGroupOffsetY)
        {
            throw new InvalidOperationException("Global layout editor group-drag offset clamp self-test failed.");
        }

        SetLeftDockTabCenterY(edge, EdgeDockTabRole.Guard, 777);
        if (edge.GuardBoardLeftDockTabCenterY != 777)
        {
            throw new InvalidOperationException("Global layout editor manual left-tab coordinate self-test failed.");
        }

        Console.WriteLine("Global layout editor surface policy: PASS structural filtering, 10 tiles, five tabs, and group drag");
    }

    private static bool HaveSameSurfaceIds(List<string> a, List<string> b)
    {
        if (a == null || b == null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
