using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// Double-clicking the operation core opens this compact launcher. The legacy QuickGrid entry was
// intentionally removed; the remaining buttons open the Spec manager and CodexSleepGuard.
internal sealed partial class OperationForm
{
    // The user keeps the live tree on E:; D:\E_Drive_Files is a synced mirror. Try the requested
    // path first, then fall back to the in-repo sibling so the button works from either checkout.
    private static readonly string[] SleepGuardLauncherCandidates =
    {
        @"E:\Codexproject\desktopdata\CodexSleepGuard\Start-CodexSleepGuard.cmd",
        @"D:\E_Drive_Files\Codexproject\desktopdata\CodexSleepGuard\Start-CodexSleepGuard.cmd"
    };

    private OperationLauncherTrioForm launcherTrioForm;

    private void ToggleLauncherTrioWindow()
    {
        OperationLauncherTrioForm form = EnsureLauncherTrioForm();
        if (form.Visible)
        {
            form.HideTrio();
            return;
        }

        form.ShowTrio();
    }

    private OperationLauncherTrioForm EnsureLauncherTrioForm()
    {
        if (this.launcherTrioForm == null || this.launcherTrioForm.IsDisposed)
        {
            this.launcherTrioForm = new OperationLauncherTrioForm(this);
        }

        this.launcherTrioForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.launcherTrioForm;
    }

    private void DisposeLauncherTrioForm()
    {
        if (this.launcherTrioForm == null)
        {
            return;
        }

        try
        {
            this.launcherTrioForm.Close();
            this.launcherTrioForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.launcherTrioForm = null;
        }
    }

    private void LaunchSleepGuard()
    {
        for (int i = 0; i < SleepGuardLauncherCandidates.Length; i++)
        {
            string candidate = SleepGuardLauncherCandidates[i];
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    WorkingDirectory = Path.GetDirectoryName(candidate),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                ShowOperationNotification("睡眠防护", "启动 CodexSleepGuard 失败：" + ex.Message, ToolTipIcon.Warning);
                return;
            }
        }

        ShowOperationNotification("睡眠防护", "未找到 Start-CodexSleepGuard.cmd（已尝试 E: 与 D:\\E_Drive_Files）。", ToolTipIcon.Warning);
    }

    private enum LauncherTrioAction
    {
        SpecBoard,
        SleepGuard
    }

    private sealed class OperationLauncherTrioForm : LayeredWidgetFormBase
    {
        private const int TrioCount = 2;
        private static readonly string[] TrioLabels = { "Spec 管理", "睡眠防护" };

        private readonly OperationForm owner;
        private readonly ToolTip toolTip;
        private int hoveredIndex = -1;
        private int pressedIndex = -1;
        private int toolTipIndex = -1;

        public OperationLauncherTrioForm(OperationForm owner)
        {
            this.owner = owner;
            ApplicationIcon.ApplyTo(this);
            InitializeLayerScaleFromCurrentDpi();
            ApplyLayerScaleFromSettings(owner.CurrentSettings);
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.Black;
            this.Cursor = Cursors.Hand;
            this.Size = GetDesiredSize();
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
            Size desired = GetDesiredSize();
            if (this.Size != desired)
            {
                this.Size = desired;
            }

            if (this.Visible)
            {
                PositionAtSecondLevel();
                RenderLayeredWindow();
            }
            else
            {
                InvalidateLayeredRenderBuffer();
            }
        }

        public void ShowTrio()
        {
            this.owner.PrepareForLauncherOverlayShow();
            this.Size = GetDesiredSize();
            PositionAtSecondLevel();
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
            this.Capture = true;
            RenderLayeredWindow();
        }

        public void HideTrio()
        {
            HideToolTip();
            Hide();
        }

        // Buttons are slightly smaller than the main operation button per the user's request.
        private int TrioButtonSize()
        {
            return Math.Max(S(22), (int)Math.Round(this.owner.GetStartButtonSize() * 0.80f));
        }

        private int PopupMargin()
        {
            return Math.Max(S(6), (int)Math.Round(TrioButtonSize() * 0.22f));
        }

        // Screen-space offsets (X right, Y down) from the operation core center to each trio button
        // center, following the radial dial's own arc: sparse 8°–82° arc at the second-level radius.
        // The radius uses the real core/item sizing so the arc lands exactly where the constellation's
        // second level sits. Index 0 (Spec) is at the top of the arc, index 1 (SleepGuard) at the bottom.
        private PointF[] ComputeArcCenterOffsets()
        {
            float core = this.owner.GetStartButtonSize();
            float item = this.owner.GetSmallButtonSize();
            float coreRadius = core / 2.0f;
            float firstLevelRadius = coreRadius + core * RadialGapScale + item / 2.0f;
            float secondLevelRadius = firstLevelRadius + item * (RadialGapScale + 1.0f) * RadialLevelSpacingMultiplier;
            PointF[] offsets = new PointF[TrioCount];
            for (int i = 0; i < TrioCount; i++)
            {
                float t = TrioCount == 1 ? 0.5f : (float)i / (TrioCount - 1);
                // i=0 -> arc end (82°, top); i=last -> arc start (8°, bottom).
                float deg = RadialSparseArcEndDeg - t * (RadialSparseArcEndDeg - RadialSparseArcStartDeg);
                double rad = deg * Math.PI / 180.0;
                offsets[i] = new PointF(
                    (float)(Math.Cos(rad) * secondLevelRadius),
                    -(float)(Math.Sin(rad) * secondLevelRadius));
            }

            return offsets;
        }

        // Window-local button rects, sized to tightly bound the three arc buttons plus a margin.
        private RectangleF[] GetButtonRects()
        {
            int core = TrioButtonSize();
            int margin = PopupMargin();
            PointF[] offsets = ComputeArcCenterOffsets();
            float r = core / 2.0f;
            float minX = float.MaxValue, minY = float.MaxValue;
            for (int i = 0; i < offsets.Length; i++)
            {
                minX = Math.Min(minX, offsets[i].X - r);
                minY = Math.Min(minY, offsets[i].Y - r);
            }

            RectangleF[] rects = new RectangleF[TrioCount];
            for (int i = 0; i < offsets.Length; i++)
            {
                rects[i] = new RectangleF(margin + offsets[i].X - r - minX, margin + offsets[i].Y - r - minY, core, core);
            }

            return rects;
        }

        private Size GetDesiredSize()
        {
            int core = TrioButtonSize();
            int margin = PopupMargin();
            PointF[] offsets = ComputeArcCenterOffsets();
            float r = core / 2.0f;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < offsets.Length; i++)
            {
                minX = Math.Min(minX, offsets[i].X - r);
                minY = Math.Min(minY, offsets[i].Y - r);
                maxX = Math.Max(maxX, offsets[i].X + r);
                maxY = Math.Max(maxY, offsets[i].Y + r);
            }

            return new Size(
                (int)Math.Ceiling(maxX - minX) + margin * 2,
                (int)Math.Ceiling(maxY - minY) + margin * 2);
        }

        private void PositionAtSecondLevel()
        {
            Rectangle workArea = this.owner.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleOperation);
            RectangleF anchor = this.owner.GetQuickGridAnchorScreenRect();
            float coreCenterX = anchor.Left + anchor.Width / 2.0f;
            float coreCenterY = anchor.Top + anchor.Height / 2.0f;

            int core = TrioButtonSize();
            int margin = PopupMargin();
            PointF[] offsets = ComputeArcCenterOffsets();
            float r = core / 2.0f;
            float minX = float.MaxValue, minY = float.MaxValue;
            for (int i = 0; i < offsets.Length; i++)
            {
                minX = Math.Min(minX, offsets[i].X - r);
                minY = Math.Min(minY, offsets[i].Y - r);
            }

            // Anchor the virtual core center to the real operation core so button screen centers land
            // at coreCenter + arcOffset; then clamp the whole popup into the work area.
            int left = (int)Math.Round(coreCenterX + minX - margin);
            int top = (int)Math.Round(coreCenterY + minY - margin);
            left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - this.Size.Width)));
            top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Size.Height)));
            this.Location = new Point(left, top);
        }

        private int HitTest(Point location)
        {
            RectangleF[] rects = GetButtonRects();
            for (int i = 0; i < rects.Length; i++)
            {
                float cx = rects[i].Left + rects[i].Width / 2.0f;
                float cy = rects[i].Top + rects[i].Height / 2.0f;
                float r = rects[i].Width / 2.0f;
                float dx = location.X - cx;
                float dy = location.Y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    return i;
                }
            }

            return -1;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ResetDisplayRenderResources();
            // No window region: the buttons float on a transparent canvas like the constellation
            // panel. Mouse capture (set in ShowTrio) still routes outside clicks here for dismissal.
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
            if (index < 0)
            {
                // Click outside any button dismisses the launcher (mouse capture keeps us informed).
                HideTrio();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            this.pressedIndex = index;
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

            if (pressed < 0 || released != pressed)
            {
                if (released < 0)
                {
                    HideTrio();
                }

                return;
            }

            HideTrio();
            Execute((LauncherTrioAction)pressed);
        }

        private void Execute(LauncherTrioAction action)
        {
            switch (action)
            {
                case LauncherTrioAction.SpecBoard:
                    this.owner.OpenSpecBoardManagerWindow();
                    break;
                case LauncherTrioAction.SleepGuard:
                    this.owner.LaunchSleepGuard();
                    break;
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
            RectangleF[] rects = GetButtonRects();

            // Faint gray rail through the button centers, echoing the constellation's sibling rails.
            using (Pen rail = new Pen(DesignTokens.White(ScaleAlpha(48, backgroundAlpha)), Math.Max(1.0f, this.LayerScale * 1.4f)))
            {
                rail.StartCap = LineCap.Round;
                rail.EndCap = LineCap.Round;
                for (int i = 0; i + 1 < rects.Length; i++)
                {
                    g.DrawLine(
                        rail,
                        rects[i].Left + rects[i].Width / 2.0f,
                        rects[i].Top + rects[i].Height / 2.0f,
                        rects[i + 1].Left + rects[i + 1].Width / 2.0f,
                        rects[i + 1].Top + rects[i + 1].Height / 2.0f);
                }
            }

            for (int i = 0; i < rects.Length; i++)
            {
                DrawTrioButton(g, rects[i], i, backgroundAlpha);
            }
        }

        private static readonly Color[] TrioTints =
        {
            Color.FromArgb(120, 178, 255), // Spec 看板 — blue
            Color.FromArgb(150, 165, 225)  // 睡眠防护 — periwinkle indigo
        };

        // Constellation-node visual grammar (matches DrawRadialNode): a soft muted-tint disc at low
        // alpha, a faint white hairline ring, a category-colored outer ring like the dial's branch
        // nodes, and a soft near-white glyph. Hover/press lift alpha slightly.
        private void DrawTrioButton(Graphics g, RectangleF rect, int index, int backgroundAlpha)
        {
            bool hovered = index == this.hoveredIndex;
            bool pressed = index == this.pressedIndex;
            Color tint = TrioTints[index];
            int fillAlpha = ScaleAlpha(ClampByte(74 + (hovered ? 40 : 0) + (pressed ? 26 : 0)), backgroundAlpha);
            int ringAlpha = ScaleAlpha(ClampByte(58 + (hovered ? 60 : 0) + (pressed ? 34 : 0)), backgroundAlpha);
            int haloAlpha = ScaleAlpha(ClampByte(150 + (hovered ? 46 : 0)), backgroundAlpha);

            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(MutedCategoryTint(tint), fillAlpha)))
            {
                g.FillEllipse(brush, rect);
            }

            using (Pen halo = new Pen(DesignTokens.WithAlpha(tint, haloAlpha), Math.Max(0.9f, 1.1f * this.LayerScale)))
            {
                float inflate = Math.Max(1.5f, rect.Width * 0.085f);
                g.DrawEllipse(halo, RectangleF.Inflate(rect, inflate, inflate));
            }

            using (Pen pen = new Pen(DesignTokens.White(ringAlpha), Math.Max(1.0f, this.LayerScale)))
            {
                g.DrawEllipse(pen, rect);
            }

            Color ink = DesignTokens.Glyph(ScaleAlpha(236, backgroundAlpha));
            RectangleF iconRect = InsetSquare(rect, 0.34f);
            switch ((LauncherTrioAction)index)
            {
                case LauncherTrioAction.SpecBoard:
                    DrawSpecGlyph(g, iconRect, ink);
                    break;
                case LauncherTrioAction.SleepGuard:
                    DrawMoonGlyph(g, iconRect, ink);
                    break;
            }
        }

        private static RectangleF InsetSquare(RectangleF rect, float insetFraction)
        {
            float side = Math.Min(rect.Width, rect.Height) * (1.0f - insetFraction);
            float cx = rect.Left + rect.Width / 2.0f;
            float cy = rect.Top + rect.Height / 2.0f;
            return new RectangleF(cx - side / 2.0f, cy - side / 2.0f, side, side);
        }

        // A small "board" with a status dot and list lines.
        private void DrawSpecGlyph(Graphics g, RectangleF rect, Color inkColor)
        {
            using (SolidBrush ink = new SolidBrush(inkColor))
            using (Pen frame = new Pen(inkColor, Math.Max(1.0f, this.LayerScale)))
            {
                using (GraphicsPath board = RoundedRectangle(rect, Math.Max(1.5f, rect.Width * 0.12f)))
                {
                    g.DrawPath(frame, board);
                }

                float dotR = rect.Width * 0.10f;
                float lineX = rect.Left + rect.Width * 0.42f;
                float lineW = rect.Width * 0.42f;
                float lineH = Math.Max(1.0f, rect.Height * 0.08f);
                for (int i = 0; i < 3; i++)
                {
                    float cy = rect.Top + rect.Height * (0.30f + i * 0.22f);
                    g.FillEllipse(ink, rect.Left + rect.Width * 0.22f - dotR, cy - dotR, dotR * 2, dotR * 2);
                    g.FillRectangle(ink, lineX, cy - lineH / 2.0f, lineW, lineH);
                }
            }
        }

        // A crescent moon for the sleep guard.
        private void DrawMoonGlyph(Graphics g, RectangleF rect, Color inkColor)
        {
            using (SolidBrush ink = new SolidBrush(inkColor))
            {
                GraphicsPath outer = new GraphicsPath();
                outer.AddEllipse(rect);
                GraphicsPath inner = new GraphicsPath();
                RectangleF cut = new RectangleF(rect.Left + rect.Width * 0.28f, rect.Top - rect.Height * 0.04f, rect.Width * 0.92f, rect.Height * 1.02f);
                inner.AddEllipse(cut);
                using (Region moon = new Region(outer))
                {
                    moon.Exclude(inner);
                    g.FillRegion(ink, moon);
                }

                outer.Dispose();
                inner.Dispose();
            }
        }

        private void UpdateToolTip(int index, Point location)
        {
            if (index < 0 || index >= TrioLabels.Length)
            {
                HideToolTip();
                return;
            }

            if (index == this.toolTipIndex)
            {
                return;
            }

            this.toolTipIndex = index;
            this.toolTip.Show(TrioLabels[index], this, location.X + 12, location.Y + 18, 4000);
        }

        private void HideToolTip()
        {
            this.toolTipIndex = -1;
            this.toolTip.Hide(this);
        }

        public void SaveSample(string path, float scale)
        {
            SetLayerScale(scale);
            this.Size = GetDesiredSize();
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                // The buttons are translucent muted discs meant to sit on the dark desktop; clear to a
                // representative dark backdrop so the sample reads like the real on-screen appearance.
                g.Clear(Color.FromArgb(255, 24, 24, 28));
                DrawWindowContent(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
    }
}
