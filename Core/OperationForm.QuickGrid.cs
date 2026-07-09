using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed partial class OperationForm
{
    private const int QuickGridColumns = 4;
    private const int QuickGridCellPixels = 100;
    private const int QuickGridGapPixels = 10;
    private const int QuickGridPaddingPixels = 10;
    private const float QuickGridVisualScale = 1.0f / 3.0f;

    private void ToggleQuickGridWindow()
    {
        OperationQuickGridForm form = EnsureQuickGridForm();
        if (form.Visible)
        {
            form.Hide();
            return;
        }

        form.ShowQuickGrid();
    }

    private OperationQuickGridForm EnsureQuickGridForm()
    {
        if (this.quickGridForm == null || this.quickGridForm.IsDisposed)
        {
            this.quickGridForm = new OperationQuickGridForm(this);
        }

        this.quickGridForm.ApplyRuntimeSettings(this.currentSettings);
        return this.quickGridForm;
    }

    private void DisposeQuickGridForm()
    {
        if (this.quickGridForm == null)
        {
            return;
        }

        try
        {
            this.quickGridForm.Close();
            this.quickGridForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.quickGridForm = null;
        }
    }

    private void ExecuteQuickGridButton(int button)
    {
        if (!IsButtonEnabled(button))
        {
            return;
        }

        if (button == RestartButtonIndex)
        {
            HandleRestartButtonClick();
            return;
        }

        if (button == AppSettingsButtonIndex)
        {
            HandleAppSettingsButtonClick();
            return;
        }

        ExecuteButton(button, MouseButtons.Left);
    }

    private List<QuickGridItem> BuildQuickGridItems()
    {
        List<QuickGridItem> items = new List<QuickGridItem>();
        AddQuickGridButton(items, "windows_settings", WindowsSettingsButtonIndex, "Windows 设置");
        AddQuickGridButton(items, "windows_power", WindowsPowerMenuButtonIndex, "电源菜单");
        AddQuickGridButton(items, "hover_opacity", HoverOpacityToggleButtonIndex, "悬停透明度");
        AddQuickGridButton(items, "refresh", RefreshButtonIndex, "刷新");
        AddQuickGridButton(items, "app_settings", AppSettingsButtonIndex, "程序设置");
        AddQuickGridButton(items, "task_manager", TaskManagerButtonIndex, "任务管理器");
        AddQuickGridButton(items, "restart", RestartButtonIndex, "重启");
        AddQuickGridButton(items, "quick_settings", WindowsQuickSettingsButtonIndex, "快速设置");
        AddQuickGridButton(items, "battery_care_pause", BatteryCarePauseButtonIndex, "电池暂停");
        AddQuickGridButton(items, "live_captions", LiveCaptionsButtonIndex, "实时字幕");
        AddQuickGridButton(items, "battery_limit_restore", BatteryLimitRestoreButtonIndex, "电池恢复");
        AddQuickGridButton(items, "ai_studio", WindowsAiStudioButtonIndex, "AI Studio");
        return items;
    }

    private void AddQuickGridButton(List<QuickGridItem> items, string id, int buttonIndex, string label)
    {
        items.Add(new QuickGridItem
        {
            Id = id,
            ButtonIndex = buttonIndex,
            Label = label,
            Execute = delegate { ExecuteQuickGridButton(buttonIndex); },
            IsEnabled = delegate { return IsButtonEnabled(buttonIndex); },
            Tooltip = delegate
            {
                string text = GetButtonToolTipText(buttonIndex);
                return string.IsNullOrEmpty(text) ? label + "\r\n当前不可用" : text;
            }
        });
    }

    private void DrawQuickGridIcon(Graphics g, RectangleF rect, int button)
    {
        RectangleF icon = GetQuickGridIconRect(rect);
        if (button == WindowsSettingsButtonIndex)
        {
            DrawSettingsGlyph(g, icon);
        }
        else if (button == WindowsPowerMenuButtonIndex)
        {
            DrawPowerGlyph(g, icon);
        }
        else if (button == AppSettingsButtonIndex)
        {
            DrawAppSettingsGlyph(g, icon);
        }
        else if (button == RefreshButtonIndex)
        {
            DrawRefreshGlyph(g, icon);
        }
        else if (button == RestartButtonIndex)
        {
            DrawRestartGlyph(g, icon);
        }
        else if (button == BatteryCarePauseButtonIndex)
        {
            DrawBatteryCareGlyph(g, icon);
        }
        else if (button == BatteryLimitRestoreButtonIndex)
        {
            DrawBatteryLimitRestoreGlyph(g, icon);
        }
        else if (button == TaskManagerButtonIndex)
        {
            DrawTaskManagerGlyph(g, icon);
        }
        else if (button == WindowsAiStudioButtonIndex)
        {
            DrawWindowsAiStudioGlyph(g, icon);
        }
        else if (button == WindowsQuickSettingsButtonIndex)
        {
            DrawQuickSettingsGlyph(g, icon);
        }
        else if (button == LiveCaptionsButtonIndex)
        {
            DrawLiveCaptionsGlyph(g, icon);
        }
        else if (button == HoverOpacityToggleButtonIndex)
        {
            DrawHoverOpacityGlyph(g, icon);
        }
    }

    private RectangleF GetQuickGridIconRect(RectangleF tileRect)
    {
        float inset = Math.Max(2.0f, tileRect.Height * 0.22f);
        return new RectangleF(
            tileRect.Left + inset,
            tileRect.Top + inset,
            Math.Max(1.0f, tileRect.Width - inset * 2.0f),
            Math.Max(1.0f, tileRect.Height - inset * 2.0f));
    }

    private static Size ComputeQuickGridWindowSize(int itemCount, float scale)
    {
        int columns = QuickGridColumns;
        int rows = Math.Max(1, (int)Math.Ceiling(itemCount / (double)columns));
        float effectiveScale = GetQuickGridEffectiveScale(scale);
        int cell = ScaleQuickGridValue(QuickGridCellPixels, effectiveScale);
        int gap = ScaleQuickGridValue(QuickGridGapPixels, effectiveScale);
        int padding = ScaleQuickGridValue(QuickGridPaddingPixels, effectiveScale);
        return new Size(
            padding * 2 + columns * cell + Math.Max(0, columns - 1) * gap,
            padding * 2 + rows * cell + Math.Max(0, rows - 1) * gap);
    }

    private static int ScaleQuickGridValue(int value, float scale)
    {
        return Math.Max(1, (int)Math.Round(value * Math.Max(0.01f, scale)));
    }

    private static float GetQuickGridEffectiveScale(float scale)
    {
        return Math.Max(0.25f, scale) * QuickGridVisualScale;
    }

    private static void RunQuickGridSelfTest()
    {
        OperationForm form = CreateRadialDialSelfTestForm();
        try
        {
            List<QuickGridItem> items = form.BuildQuickGridItems();
            AssertSelfTest(items.Count == 12, "quick grid keeps the legacy 12 small operation buttons");
            AssertSelfTest(items[0].ButtonIndex == WindowsSettingsButtonIndex, "quick grid starts with Windows settings");
            AssertSelfTest(items[11].ButtonIndex == WindowsAiStudioButtonIndex, "quick grid preserves the AI Studio final slot");
            Size size = ComputeQuickGridWindowSize(items.Count, 1.0f);
            AssertSelfTest(size.Width == 147 && size.Height == 111, "quick grid 12-button layout is 147x111 at 1x scale");
            Point position = ComputeQuickGridPopupLocation(
                new Rectangle(0, 0, 500, 500),
                new RectangleF(10, 400, 90, 90),
                size,
                3);
            AssertSelfTest(position.X == 103 && position.Y == 286, "quick grid anchors above and to the right of the operation primary button");
            Point clamped = ComputeQuickGridPopupLocation(
                new Rectangle(0, 0, 500, 500),
                new RectangleF(470, 10, 40, 40),
                size,
                3);
            AssertSelfTest(clamped.X == 353 && clamped.Y == 0, "quick grid anchor position clamps inside the work area");
        }
        finally
        {
            form.Dispose();
        }
    }

    private RectangleF GetQuickGridAnchorScreenRect()
    {
        RectangleF anchor = RectangleF.Empty;
        if (IsRadialDialActive())
        {
            anchor = ComputeRadialLayout().Core;
        }
        else
        {
            RectangleF[] rects = GetButtonRects();
            if (StartButtonIndex >= 0 &&
                StartButtonIndex < rects.Length &&
                IsButtonVisible(StartButtonIndex) &&
                !rects[StartButtonIndex].IsEmpty)
            {
                anchor = rects[StartButtonIndex];
            }
            else
            {
                for (int i = 0; i < rects.Length; i++)
                {
                    if (IsButtonVisible(i) && !rects[i].IsEmpty)
                    {
                        anchor = rects[i];
                        break;
                    }
                }
            }
        }

        if (anchor.IsEmpty)
        {
            anchor = new RectangleF(0.0f, 0.0f, GetStartButtonSize(), GetStartButtonSize());
        }

        Point topLeft = PointToScreen(new Point(
            (int)Math.Round(anchor.Left),
            (int)Math.Round(anchor.Top)));
        return new RectangleF(topLeft.X, topLeft.Y, anchor.Width, anchor.Height);
    }

    private static Point ComputeQuickGridPopupLocation(Rectangle workArea, RectangleF anchorScreenRect, Size popupSize, int gap)
    {
        int left = (int)Math.Round(anchorScreenRect.Right + gap);
        int top = (int)Math.Round(anchorScreenRect.Top - popupSize.Height - gap);
        int maxLeft = Math.Max(workArea.Left, workArea.Right - popupSize.Width);
        int maxTop = Math.Max(workArea.Top, workArea.Bottom - popupSize.Height);
        left = Math.Max(workArea.Left, Math.Min(left, maxLeft));
        top = Math.Max(workArea.Top, Math.Min(top, maxTop));
        return new Point(left, top);
    }

    private static void RenderQuickGridSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.OperationRenderVariant = OperationRenderVariant.RadialDial;
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
        using (OperationQuickGridForm quickGrid = new OperationQuickGridForm(form))
        {
            string path = System.IO.Path.Combine(outputDir, "operation-quick-grid.png");
            quickGrid.SaveSample(path, 1.0f);
            Console.WriteLine("QuickGrid -> " + path);
        }
    }

    private sealed class QuickGridItem
    {
        public string Id;
        public int ButtonIndex;
        public string Label;
        public Action Execute;
        public Func<bool> IsEnabled;
        public Func<string> Tooltip;
    }

    private sealed class OperationQuickGridForm : LayeredWidgetFormBase
    {
        private readonly OperationForm owner;
        private readonly ToolTip toolTip;
        private List<QuickGridItem> items;
        private int hoveredIndex = -1;
        private int pressedIndex = -1;
        private int toolTipIndex = -1;

        public OperationQuickGridForm(OperationForm owner)
        {
            this.owner = owner;
            this.items = owner.BuildQuickGridItems();
            ApplicationIcon.ApplyTo(this);
            InitializeLayerScaleFromCurrentDpi();
            ApplyLayerScaleFromSettings(owner.currentSettings);
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.Black;
            this.Cursor = Cursors.Hand;
            this.Size = GetDesiredQuickGridSize();
            this.toolTip = new ToolTip
            {
                ShowAlways = true,
                InitialDelay = 350,
                ReshowDelay = 80,
                AutoPopDelay = 5000
            };
        }

        public void ApplyRuntimeSettings(WidgetSettings settings)
        {
            ApplyLayerScaleFromSettings(settings);
            Size desired = GetDesiredQuickGridSize();
            if (this.Size != desired)
            {
                this.Size = desired;
            }

            if (this.Visible)
            {
                PositionNearOperationMainButton();
                RenderLayeredWindow();
            }
            else
            {
                InvalidateLayeredRenderBuffer();
            }
        }

        public void ShowQuickGrid()
        {
            this.items = this.owner.BuildQuickGridItems();
            this.Size = GetDesiredQuickGridSize();
            PositionNearOperationMainButton();
            if (!this.Visible)
            {
                Show(this.owner);
            }

            NativeMethods.SetWindowPos(
                this.Handle,
                GetLayeredWidgetInsertAfter(true),
                this.Left,
                this.Top,
                this.Width,
                this.Height,
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_SHOWWINDOW);
            this.Capture = true;
            RenderLayeredWindow();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ResetDisplayRenderResources();
            using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), GetQuickGridCornerRadius()))
            {
                Region oldRegion = this.Region;
                this.Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }

            RenderLayeredWindow();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            if (index == this.hoveredIndex)
            {
                return;
            }

            this.hoveredIndex = index;
            UpdateToolTip(index, e.Location);
            RenderLayeredWindow();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            this.hoveredIndex = -1;
            this.pressedIndex = -1;
            HideToolTip();
            RenderLayeredWindow();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = HitTest(e.Location);
            if ((e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) && index < 0)
            {
                HideQuickGridFromInteraction();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            this.pressedIndex = IsItemEnabled(index) ? index : -1;
            RenderLayeredWindow();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int pressed = this.pressedIndex;
            this.pressedIndex = -1;
            int released = HitTest(e.Location);
            RenderLayeredWindow();
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (pressed < 0 || released < 0 || pressed != released || !IsItemEnabled(pressed))
            {
                if (released < 0)
                {
                    HideQuickGridFromInteraction();
                }

                return;
            }

            QuickGridItem item = this.items[pressed];
            HideQuickGridFromInteraction();
            if (item.Execute != null)
            {
                item.Execute();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!this.Visible)
            {
                this.Capture = false;
                this.hoveredIndex = -1;
                this.pressedIndex = -1;
                HideToolTip();
            }
        }

        protected override void DrawWindowContent(Graphics g)
        {
            this.owner.ConfigureGraphics(g);
            int backgroundAlpha = this.owner.GetBackgroundOpacityAlpha();
            RectangleF panel = new RectangleF(0, 0, this.Width, this.Height);
            using (GraphicsPath panelPath = RoundedRectangle(RectangleF.Inflate(panel, -0.5f, -0.5f), GetQuickGridCornerRadius()))
            using (SolidBrush panelBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(226, backgroundAlpha))))
            using (Pen panelPen = new Pen(DesignTokens.White(ScaleAlpha(70, backgroundAlpha)), Math.Max(1.0f, this.LayerScale)))
            {
                g.FillPath(panelBrush, panelPath);
                g.DrawPath(panelPen, panelPath);
            }

            RectangleF[] rects = GetItemRects();
            for (int i = 0; i < rects.Length && i < this.items.Count; i++)
            {
                DrawItem(g, rects[i], this.items[i], i);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.toolTip.Hide(this);
                this.toolTip.Dispose();
            }

            base.Dispose(disposing);
        }

        private Size GetDesiredQuickGridSize()
        {
            return ComputeQuickGridWindowSize(this.items == null ? 0 : this.items.Count, this.LayerScale);
        }

        private void PositionNearOperationMainButton()
        {
            Rectangle workArea = this.owner.currentSettings.GetWorkAreaForModule(WidgetSettings.ModuleOperation);
            RectangleF anchorScreenRect = this.owner.GetQuickGridAnchorScreenRect();
            this.Location = ComputeQuickGridPopupLocation(
                workArea,
                anchorScreenRect,
                this.Size,
                GetScaledQuickGridValue(QuickGridGapPixels));
        }

        private RectangleF[] GetItemRects()
        {
            int count = this.items == null ? 0 : this.items.Count;
            RectangleF[] rects = new RectangleF[count];
            int cell = GetScaledQuickGridValue(QuickGridCellPixels);
            int gap = GetScaledQuickGridValue(QuickGridGapPixels);
            int padding = GetScaledQuickGridValue(QuickGridPaddingPixels);
            for (int i = 0; i < count; i++)
            {
                int column = i % QuickGridColumns;
                int row = i / QuickGridColumns;
                rects[i] = new RectangleF(
                    padding + column * (cell + gap),
                    padding + row * (cell + gap),
                    cell,
                    cell);
            }

            return rects;
        }

        private int GetScaledQuickGridValue(int value)
        {
            return ScaleQuickGridValue(value, GetQuickGridEffectiveScale(this.LayerScale));
        }

        private float GetQuickGridCornerRadius()
        {
            return Math.Max(2.0f, GetScaledQuickGridValue(10));
        }

        private void DrawItem(Graphics g, RectangleF rect, QuickGridItem item, int index)
        {
            bool enabled = IsItemEnabled(index);
            bool hovered = index == this.hoveredIndex && enabled;
            bool pressed = index == this.pressedIndex && enabled;
            int fillAlpha = enabled
                ? ClampByte((int)Math.Round(54.0 + (hovered ? 46.0 : 0.0) + (pressed ? 40.0 : 0.0)))
                : 34;
            int outlineAlpha = enabled
                ? ClampByte((int)Math.Round(58.0 + (hovered ? 72.0 : 0.0) + (pressed ? 42.0 : 0.0)))
                : 50;
            Color fill = DesignTokens.White(fillAlpha);
            if (!enabled)
            {
                fill = DesignTokens.WithAlpha(DesignTokens.Colors.Control, fillAlpha);
            }
            else if (item.ButtonIndex == BatteryLimitRestoreButtonIndex)
            {
                fill = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, ClampByte(fillAlpha + 34));
            }
            else if (item.ButtonIndex == BatteryCarePauseButtonIndex)
            {
                fill = DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, ClampByte(fillAlpha + 18));
            }
            else if (item.ButtonIndex == WindowsAiStudioButtonIndex || item.ButtonIndex == LiveCaptionsButtonIndex)
            {
                fill = DesignTokens.WithAlpha(Color.FromArgb(255, 236, 170), ClampByte(fillAlpha + 20));
            }
            else if (item.ButtonIndex == HoverOpacityToggleButtonIndex && this.owner.currentSettings.ForceHoverOpacityActive)
            {
                fill = DesignTokens.WithAlpha(Color.FromArgb(178, 225, 255), ClampByte(fillAlpha + 42));
            }

            float radius = Math.Max(2.0f, rect.Height * 0.14f);
            using (GraphicsPath path = RoundedRectangle(rect, radius))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(enabled ? DesignTokens.White(outlineAlpha) : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, outlineAlpha), Math.Max(1.0f, this.LayerScale)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            this.owner.DrawQuickGridIcon(g, rect, item.ButtonIndex);
            if (!enabled)
            {
                using (GraphicsPath path = RoundedRectangle(rect, radius))
                using (SolidBrush veil = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 116)))
                using (Pen pen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 128), Math.Max(1.0f, this.LayerScale)))
                {
                    g.FillPath(veil, path);
                    g.DrawPath(pen, path);
                }
            }
        }

        private int HitTest(Point point)
        {
            RectangleF[] rects = GetItemRects();
            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i].Contains(point.X, point.Y))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool IsItemEnabled(int index)
        {
            if (index < 0 || this.items == null || index >= this.items.Count)
            {
                return false;
            }

            return this.items[index].IsEnabled == null || this.items[index].IsEnabled();
        }

        private void UpdateToolTip(int index, Point location)
        {
            if (index < 0 || this.items == null || index >= this.items.Count)
            {
                HideToolTip();
                return;
            }

            string text = this.items[index].Tooltip == null ? this.items[index].Label : this.items[index].Tooltip();
            if (string.IsNullOrEmpty(text))
            {
                HideToolTip();
                return;
            }

            this.toolTipIndex = index;
            this.toolTip.Hide(this);
            this.toolTip.Show(text, this, new Point(location.X + S(12), location.Y + S(18)), 5000);
        }

        private void HideToolTip()
        {
            if (this.toolTipIndex < 0)
            {
                return;
            }

            this.toolTipIndex = -1;
            this.toolTip.Hide(this);
        }

        private void HideQuickGridFromInteraction()
        {
            this.Capture = false;
            this.hoveredIndex = -1;
            this.pressedIndex = -1;
            HideToolTip();
            Hide();
        }

        public void SaveSample(string path, float scale)
        {
            SetLayerScale(scale);
            this.Size = GetDesiredQuickGridSize();
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawWindowContent(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
