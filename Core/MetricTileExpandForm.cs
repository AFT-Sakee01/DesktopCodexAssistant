using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// Hover-expanded detail window for the right-edge tile column (1.0.6.09). Its own retained size
// settings are migrated from the retired Codex Radar geometry and it pops out to the LEFT of
// the column so it never covers the tiles the cursor is tracking.
//
// One window is reused for all ten tiles: the metric only changes which content renderer runs, so
// there is a single layered surface, a single burn-in slot and no per-tile window lifecycle. It
// samples nothing — WidgetForm pushes the same MetricTileFeed the column renders from.
internal sealed partial class MetricTileExpandForm : LayeredWidgetFormBase
{
    // The panel size is expressed in real screen pixels (MetricTileExpandWidth/Height), matching
    // every other user-facing size
    // in this app. Content is authored against a 522x120 design box and scaled to whatever pixel
    // size is in effect, so large mode is an exact 2x of the same drawing.
    internal const int DesignHeightUnits = 120;
    internal const int FallbackRadarWidth = 522;
    internal const int FallbackRadarHeight = 120;
    // Horizontal gap between the expand window and the tile column, so the accent outline of a
    // hovered tile stays visible next to the panel it opened.
    internal const int GapToColumnDesignUnits = 6;

    private MetricTileId metricId = MetricTileId.Cpu;
    private MetricTileFeed feed = new MetricTileFeed();
    private bool displaySuspended;
    private BurnInVisualLevel burnInVisualLevel;
    private bool burnInBrightnessRestored;

    public MetricTileExpandForm(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.Text = "MetricTileExpand";
        this.AccessibleName = "MetricTileExpand";
        ApplyPanelSizeAndScale();
    }

    protected override string LayeredWindowLogName
    {
        get { return "MetricTileExpand"; }
    }

    protected override string LayeredRenderTimingName
    {
        get { return "tileexpand.render"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.MainWidgetTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.MainWidgetScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    protected override int ApplyHoverAlpha(int alpha)
    {
        return alpha;
    }

    protected override int PresentationLuminancePercent
    {
        get
        {
            return this.burnInVisualLevel != BurnInVisualLevel.Normal && !this.burnInBrightnessRestored
                ? BurnInProtection.LevelOneLuminancePercent
                : 100;
        }
    }

    public bool SetBurnInVisualState(BurnInVisualLevel level, bool restoreRightGroupBrightness)
    {
        BurnInVisualLevel normalized = BurnInProtection.NormalizeVisualLevel(level);
        bool restored = normalized != BurnInVisualLevel.Normal && restoreRightGroupBrightness;
        if (this.burnInVisualLevel == normalized && this.burnInBrightnessRestored == restored)
        {
            return false;
        }

        this.burnInVisualLevel = normalized;
        this.burnInBrightnessRestored = restored;
        InvalidateLayeredRenderBuffer();
        if (this.Visible && CanRenderLayeredWindow())
        {
            RenderLayeredWindow();
        }

        return true;
    }

    // Dedicated expanded-panel size, doubled in large mode.
    internal Size GetDesiredSize()
    {
        int width = FallbackRadarWidth;
        int height = FallbackRadarHeight;
        if (this.CurrentSettings != null)
        {
            width = Math.Max(1, this.CurrentSettings.MetricTileExpandWidth);
            height = Math.Max(1, this.CurrentSettings.MetricTileExpandHeight);
        }

        int multiplier = this.CurrentSettings != null && this.CurrentSettings.MainWidgetTileLargeModeEnabled ? 2 : 1;
        return new Size(width * multiplier, height * multiplier);
    }

    private float GetPanelContentScale()
    {
        return Math.Max(0.25f, GetDesiredSize().Height / (float)DesignHeightUnits);
    }

    private void ApplyPanelSizeAndScale()
    {
        SetLayerScale(GetPanelContentScale());
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }
    }

    internal MetricTileId MetricIdForTest
    {
        get { return this.metricId; }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyPanelSizeAndScale();
        InvalidateLayeredRenderBuffer();
        if (this.Visible && CanRenderLayeredWindow())
        {
            RenderLayeredWindow();
        }
    }

    public void UpdateFeed(MetricTileFeed next)
    {
        this.feed = next ?? new MetricTileFeed();
        if (this.Visible && CanRenderLayeredWindow())
        {
            InvalidateLayeredRenderBuffer();
            RenderLayeredWindow();
        }
    }

    // anchorTile is the screen rect of the hovered tile. The panel's top edge lines up with the
    // tile's top edge, then slides up if that would push it past the bottom of the work area, so a
    // tile near the taskbar still opens a fully visible panel.
    public void ShowForTile(MetricTileId id, Rectangle anchorTile, MetricTileFeed next)
    {
        this.metricId = id;
        this.feed = next ?? this.feed ?? new MetricTileFeed();
        ApplyPanelSizeAndScale();

        Rectangle workArea = this.CurrentSettings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
        int left = anchorTile.Left - this.Width - S(GapToColumnDesignUnits);
        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - this.Width)));
        int top = anchorTile.Top;
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Height)));
        this.Location = new Point(left, top);

        if (!CanRenderLayeredWindow())
        {
            HidePanel();
            return;
        }

        InvalidateLayeredRenderBuffer();
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
        RenderLayeredWindow();
    }

    public void HidePanel()
    {
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
            HidePanel();
        }

        ResetDisplayRenderResources();
    }

    public void RecoverAfterDisplayResume()
    {
        ResetDisplayRenderResources();
        HidePanel();
    }

    protected override void DrawWindowContent(Graphics g)
    {
        DrawPanel(g);
    }

    internal void DrawPanel(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF bounds = new RectangleF(0, 0, this.Width - 1, this.Height - 1);
        Color accent = MetricTileModel.GetAccent(this.metricId);
        using (GraphicsPath shell = RoundedRectangle(bounds, S(DesignTokens.Radius.Panel)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 245)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 150), Math.Max(1.0f, S(1))))
        {
            g.FillPath(fill, shell);
            g.DrawPath(border, shell);
        }

        float pad = S(12);
        RectangleF content = new RectangleF(pad, S(7), this.Width - pad * 2.0f, this.Height - S(14));
        PerfSnapshot s = this.feed.Snapshot ?? new PerfSnapshot();

        switch (this.metricId)
        {
            case MetricTileId.Cpu: DrawCpu(g, content, accent, s); break;
            case MetricTileId.Memory: DrawMemory(g, content, accent, s); break;
            case MetricTileId.Disk: DrawDisk(g, content, accent, s); break;
            case MetricTileId.Network: DrawNetwork(g, content, accent, s); break;
            case MetricTileId.Gpu: DrawGpu(g, content, accent, s); break;
            case MetricTileId.Npu: DrawNpu(g, content, accent, s); break;
            case MetricTileId.Power: DrawPower(g, content, accent); break;
            case MetricTileId.CodexQuota: DrawRadarQuota(g, content, accent, false); break;
            case MetricTileId.ClaudeQuota: DrawRadarQuota(g, content, accent, true); break;
            default: DrawGuard(g, content, accent); break;
        }
    }

    // ── Layout ───────────────────────────────────────────────────────────
    //
    // Full-bleed: one large graphic fills the panel and acts as the ground, the label, headline
    // value and a single sub line float over its top-left on a soft scrim, a thin strip along the
    // bottom edge carries the granular secondary data, and a caption sits top-right.
    //
    // 522x120 is a 4.35:1 box. Stacking header / sub line / bar / legend / chart vertically starved
    // all five and forced 8-10 px type; giving the chart the whole panel and floating the text over
    // it buys both a readable type size and a chart wide enough to read a shape off.
    //
    // Two panels have no time series to spread across the ground and are adapted rather than faked:
    // PWR has no watts history buffer, so its ground is the thermal zone wall; GUARD is four states,
    // so its ground is four tinted bands. Drawing a curve through either would mean inventing data.
    //
    // Type scale, in design units (= physical px at compact scale):
    private const int LabelSize = 18;      // metric name
    private const int ValueSize = 32;      // headline number
    private const int SuffixSize = 16;     // unit after the headline
    private const int SubSize = 17;        // sub line
    private const int CaptionSize = 13;    // caption

    // The floating text block. The scrim is measured from the text it protects — including the sub
    // line, because on a busy panel (MEM at 63%, the NET mirror) the curve runs straight through
    // where that line sits — so it never covers more of the ground than it has to.
    private void DrawFloatingHeader(Graphics g, RectangleF content, Color accent, string label, string value, string suffix, string subLine)
    {
        bool drawNeutralText = ShouldDrawNeutralText(this.burnInVisualLevel);
        float scrimW = content.Width * 0.42f;
        float scrimH = S(ValueSize) + S(8);
        if (!string.IsNullOrEmpty(subLine))
        {
            using (Font subFont = new Font("Segoe UI", S(SubSize) * 0.92f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                scrimW = Math.Max(scrimW, g.MeasureString(subLine, subFont).Width + S(10));
            }

            scrimH = S(ValueSize) + S(SubSize) + S(8);
        }

        scrimW = Math.Min(scrimW, content.Width + S(8));
        // A light veil rather than an opaque plate: just enough to seat the glyphs, so the chart
        // behind stays visible and reads as the panel's background.
        using (SolidBrush scrim = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 60)))
        {
            g.FillRectangle(scrim, content.X - S(4), content.Y - S(3), scrimW, scrimH);
        }

        using (Font labelFont = new Font("Segoe UI", S(LabelSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font valueFont = new Font("Segoe UI", S(ValueSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font suffixFont = new Font("Segoe UI", S(SuffixSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(accent, 205)))
        using (SolidBrush valueBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, 205)))
        using (SolidBrush suffixBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 188)))
        {
            g.DrawString(label, labelFont, labelBrush, content.X, content.Y + S(9));
            float labelW = g.MeasureString(label, labelFont).Width;
            float vx = content.X + labelW + S(8);
            if (drawNeutralText && !string.IsNullOrEmpty(value))
            {
                g.DrawString(value, valueFont, valueBrush, vx, content.Y);
                if (!string.IsNullOrEmpty(suffix))
                {
                    float valueW = g.MeasureString(value, valueFont).Width;
                    g.DrawString(suffix, suffixFont, suffixBrush, vx + valueW - S(6), content.Y + S(14));
                }
            }
        }

        if (drawNeutralText && !string.IsNullOrEmpty(subLine))
        {
            DrawText(g, subLine, content.X, content.Y + S(ValueSize) + S(2), S(SubSize) * 0.92f,
                DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 180), FontStyle.Regular);
        }
    }

    // The chart is the panel's background: it runs from just under the top caption row down to the
    // bottom strip, filling the whole panel, and the header text floats over it on a light veil with
    // semi-transparent glyphs (see DrawFloatingHeader) so peaks read through the text instead of being
    // clipped by an opaque scrim.
    private RectangleF GroundRect(RectangleF content, bool hasStrip)
    {
        float bottom = hasStrip ? content.Bottom - S(13) : content.Bottom;
        float top = content.Y + content.Height * 0.16f;
        return new RectangleF(content.X, top, content.Width, Math.Max(1.0f, bottom - top));
    }

    private RectangleF StripRect(RectangleF content)
    {
        return new RectangleF(content.X, content.Bottom - S(9), content.Width, S(9));
    }

    private void DrawCaption(Graphics g, RectangleF rect, string text)
    {
        if (!ShouldDrawNeutralText(this.burnInVisualLevel) || string.IsNullOrEmpty(text))
        {
            return;
        }

        using (Font font = new Font("Segoe UI", S(CaptionSize), FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 220)))
        using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
        {
            format.Alignment = StringAlignment.Far;
            g.DrawString(text, font, brush, new RectangleF(rect.X, rect.Y, rect.Width, S(CaptionSize) + S(3)), format);
        }
    }

    private void DrawText(Graphics g, string text, float x, float y, float pixelSize, Color color, FontStyle style)
    {
        if (!ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            return;
        }

        using (Font font = new Font("Segoe UI", Math.Max(6.0f, pixelSize), style, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.DrawString(text, font, brush, x, y);
        }
    }

    // Filled area chart with an emphasised endpoint.
    private void DrawSpark(Graphics g, RectangleF rect, List<double> values, Color color, double explicitMax, bool drawEndpoint, bool percentGuides)
    {
        if (rect.Width <= 2.0f || rect.Height <= 2.0f)
        {
            return;
        }

        if (percentGuides && explicitMax > 0.0)
        {
            DrawScaleGuide(g, rect, 50.0, explicitMax);
            DrawScaleGuide(g, rect, 100.0, explicitMax);
        }

        if (values == null || values.Count < 2)
        {
            using (Pen flat = new Pen(DesignTokens.WithAlpha(color, 120), Math.Max(1.0f, S(1))))
            {
                g.DrawLine(flat, rect.Left, rect.Bottom - 1.0f, rect.Right, rect.Bottom - 1.0f);
            }

            return;
        }

        double max = explicitMax;
        if (max <= 0.0)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] > max)
                {
                    max = values[i];
                }
            }

            max *= 1.15;
        }

        if (max <= 0.0)
        {
            max = 1.0;
        }

        PointF[] points = new PointF[values.Count];
        float step = rect.Width / (values.Count - 1);
        for (int i = 0; i < values.Count; i++)
        {
            points[i] = new PointF(rect.X + i * step, ResolvePlotY(rect, values[i], max));
        }

        using (GraphicsPath area = new GraphicsPath())
        {
            area.AddLines(points);
            area.AddLine(points[points.Length - 1].X, rect.Bottom, rect.X, rect.Bottom);
            area.CloseFigure();
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 40)))
            {
                g.FillPath(fill, area);
            }
        }

        using (Pen line = new Pen(DesignTokens.WithAlpha(color, 235), Math.Max(1.0f, S(1.6f))))
        {
            line.LineJoin = LineJoin.Round;
            g.DrawLines(line, points);
        }

        if (drawEndpoint)
        {
            PointF last = points[points.Length - 1];
            float dot = Math.Max(2.0f, S(2.6f));
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 255)))
            {
                g.FillEllipse(brush, last.X - dot, last.Y - dot, dot * 2.0f, dot * 2.0f);
            }
        }
    }

    // A dotted horizontal reference at a value on the chart's own scale, with a small left-edge
    // label. Only 50 and 100 are drawn, so a percentage curve makes half and full readable at a
    // glance (100 lands just under the header now that the ground clears the text band).
    private void DrawScaleGuide(Graphics g, RectangleF rect, double value, double max)
    {
        if (max <= 0.0)
        {
            return;
        }

        float y = ResolvePlotY(rect, value, max);
        using (Pen guide = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 96), 1.0f))
        {
            guide.DashStyle = DashStyle.Dot;
            g.DrawLine(guide, rect.Left, y, rect.Right, y);
        }

        // Label right-aligned at the edge: the top-left is where the header value lives, so a
        // left-edge "100" would hide behind it once the chart runs full-height behind the text.
        if (ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            float labelSize = Math.Max(6.0f, S(SubSize) * 0.66f);
            using (Font font = new Font("Segoe UI", labelSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 220)))
            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.Alignment = StringAlignment.Far;
                g.DrawString(((int)value).ToString(CultureInfo.InvariantCulture), font, brush,
                    new RectangleF(rect.Right - S(34), y + S(1), S(32), labelSize + S(2)), format);
            }
        }
    }

    // Curves, guides and per-core bars must share this exact projection. In particular, a core at
    // 100% resolves to rect.Top + 1, the same pixel row as the 100 guide, instead of appearing one
    // pixel short because two renderers rounded different height formulas.
    private static float ResolvePlotY(RectangleF rect, double value, double max)
    {
        if (max <= 0.0)
        {
            return rect.Bottom - 1.0f;
        }

        double ratio = MetricTileModel.Clamp(value / max, 0.0, 1.0);
        return rect.Bottom - (float)(ratio * (rect.Height - 2.0f)) - 1.0f;
    }

    // Secondary series: thin translucent line, no fill, so two series share one ground without the
    // second reading as another area chart.
    private void DrawSparkLineOnly(Graphics g, RectangleF rect, List<double> values, Color color, double max, int alpha, bool drawEndpoint)
    {
        if (values == null || values.Count < 2 || rect.Width <= 2.0f)
        {
            return;
        }

        if (max <= 0.0)
        {
            max = 1.0;
        }

        PointF[] points = new PointF[values.Count];
        float step = rect.Width / (values.Count - 1);
        for (int i = 0; i < values.Count; i++)
        {
            points[i] = new PointF(rect.X + i * step, ResolvePlotY(rect, values[i], max));
        }

        using (Pen line = new Pen(DesignTokens.WithAlpha(color, alpha), Math.Max(1.0f, S(1.1f))))
        {
            line.LineJoin = LineJoin.Round;
            g.DrawLines(line, points);
        }

        if (drawEndpoint)
        {
            PointF last = points[points.Length - 1];
            float dot = Math.Max(1.5f, S(2.0f));
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, Math.Max(alpha, 220))))
            {
                g.FillEllipse(brush, last.X - dot, last.Y - dot, dot * 2.0f, dot * 2.0f);
            }
        }
    }

    // Thin proportion strip. Deliberately slim: a fill ratio reads at a few pixels, and the height
    // it does not take is what lets the ground graphic run the full panel.
    private void DrawSegmentBar(Graphics g, RectangleF rect, double[] percents, Color[] colors, int[] alphas)
    {
        float radius = Math.Max(1.5f, rect.Height / 2.0f);
        using (GraphicsPath track = RoundedRectangle(rect, radius))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(28)))
        {
            g.FillPath(trackBrush, track);
            Region previous = g.Clip;
            g.SetClip(track, CombineMode.Intersect);
            float x = rect.X;
            for (int i = 0; i < percents.Length; i++)
            {
                float w = (float)(MetricTileModel.Clamp(percents[i], 0.0, 100.0) / 100.0 * rect.Width);
                if (w <= 0.0f)
                {
                    continue;
                }

                using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(colors[i], alphas[i])))
                {
                    g.FillRectangle(brush, x, rect.Y, w, rect.Height);
                }

                x += w;
            }

            g.Clip = previous;
        }
    }

    // ── CPU ──────────────────────────────────────────────────────────────
    // Core loads at or above this warn (yellow); a maxed core (at/near 100) is danger (red).
    private const double CoreLoadWarningPercent = 75.0;
    private const double CoreLoadDangerPercent = 95.0;

    private void DrawCpu(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        double[] cores = s.CpuCorePercents ?? new double[0];
        // Per-core bars and the load curve share one 0-100 ground and the same bottom baseline: the
        // bars are drawn first as a background histogram of the current spread, then the filled
        // 60 s curve rides over them so both are read against one scale.
        RectangleF ground = GroundRect(content, false);
        DrawCoreBars(g, ground, cores, accent);
        DrawSpark(g, ground, this.feed.CpuHistory, accent, 100.0, true, true);

        DrawFloatingHeader(g, content, accent, "CPU",
            Math.Round(s.CpuPercent).ToString("0", CultureInfo.InvariantCulture), "%",
            string.Format(CultureInfo.InvariantCulture, "{0:0.00} / {1:0.00} GHz · {2} 核 · 峰值核心 {3:0}%",
                s.CpuFrequencyGhz, s.CpuBaseFrequencyGhz, cores.Length, PeakOf(cores)));
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");
    }

    // Per-core bars, one per core across the shared ground, rising from the baseline. Each bar warns
    // yellow at 75 %+ and turns red when the core is maxed, so a single hot core is visible even
    // while the average load curve looks calm.
    private void DrawCoreBars(Graphics g, RectangleF rect, double[] cores, Color accent)
    {
        if (cores == null || cores.Length == 0 || rect.Height <= 1.0f)
        {
            return;
        }

        float gap = Math.Max(1.0f, S(1));
        float barW = (rect.Width - gap * (cores.Length - 1)) / cores.Length;
        if (barW <= 0.3f)
        {
            return;
        }

        for (int i = 0; i < cores.Length; i++)
        {
            double value = MetricTileModel.Clamp(cores[i], 0.0, 100.0);
            float top = ResolvePlotY(rect, value, 100.0);
            float h = Math.Max(1.5f, rect.Bottom - top);
            // accent is already energy-adjusted by the caller; the raw warn/danger tokens are not.
            Color barColor = value >= CoreLoadDangerPercent
                ? DesignTokens.Colors.DangerStrong
                : (value >= CoreLoadWarningPercent ? DesignTokens.Colors.Warning : accent);
            int alpha = 96 + (int)(value / 100.0 * 96.0);
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(barColor, alpha)))
            {
                g.FillRectangle(brush, rect.X + i * (barW + gap), rect.Bottom - h, barW, h);
            }
        }
    }

    // ── Memory ───────────────────────────────────────────────────────────
    private void DrawMemory(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        double reservedGb = s.MemoryHardwareReservedGb > 0.0
            ? s.MemoryHardwareReservedGb
            : s.MemoryTotalGb * s.MemoryHardwareReservedPercent / 100.0;
        double availableGb = s.MemoryAvailableGb > 0.0
            ? s.MemoryAvailableGb
            : Math.Max(0.0, s.MemoryTotalGb - s.MemoryUsedGb);
        RectangleF ground = GroundRect(content, true);
        DrawSpark(g, ground, this.feed.MemoryHistory, accent, 100.0, true, true);
        DrawSparkLineOnly(g, ground, this.feed.MemoryHardwareReservedHistory,
            DesignTokens.Colors.Warning, 100.0, 225, true);
        DrawMemoryPressureSummary(g, content, s);
        DrawMemoryPressureHistory(g, StripRect(content), this.feed.MemoryPressureHistory, s);

        DrawFloatingHeader(g, content, accent, "MEM",
            Math.Round(s.MemoryPercent).ToString("0", CultureInfo.InvariantCulture), "%",
            string.Format(CultureInfo.InvariantCulture,
                "{0:0.0}/{1:0.0} GB · 可用 {2:0.0} · GPU/NPU {3:0.0}",
                s.MemoryUsedGb,
                s.MemoryTotalGb,
                availableGb,
                reservedGb));
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");
    }

    private void DrawMemoryPressureSummary(Graphics g, RectangleF content, PerfSnapshot s)
    {
        if (!ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            return;
        }

        Color pressureColor = MetricTileModel.GetMemoryPressureColor(s.MemoryPressureLevel);
        string pressureText = "压力 " + MetricTileModel.GetMemoryPressureLabel(s.MemoryPressureLevel);
        string commitText = s.MemoryCommitLimitGb > 0.0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "提交 {0:0}%{1}",
                s.MemoryCommitPercent,
                s.MemoryCommitPercent >= 80.0 ? " 偏高" : string.Empty)
            : "提交 --";
        string pageOutText = string.Format(
            CultureInfo.InvariantCulture,
            "换出 {0}/s",
            FormatPageOutRate(s.MemoryPageOutMegabytesPerSecond));

        float y = content.Bottom - S(25);
        float fontSize = Math.Max(7.0f, S(11.5f));
        using (SolidBrush scrim = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 118)))
        {
            g.FillRectangle(
                scrim,
                content.X,
                y - S(1),
                content.Width,
                fontSize + S(5));
        }

        using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font detailFont = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush pressureBrush = new SolidBrush(DesignTokens.WithAlpha(pressureColor, 235)))
        using (SolidBrush detailBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 190)))
        using (SolidBrush commitBrush = new SolidBrush(DesignTokens.WithAlpha(
            s.MemoryCommitPercent >= 98.0
                ? DesignTokens.Colors.DangerStrong
                : (s.MemoryCommitPercent >= 80.0 ? DesignTokens.Colors.Warning : DesignTokens.Colors.TextMuted),
            220)))
        {
            g.DrawString(pressureText, font, pressureBrush, content.X, y);
            SizeF pageOutSize = g.MeasureString(pageOutText, detailFont);
            SizeF separatorSize = g.MeasureString(" · ", detailFont);
            SizeF commitSize = g.MeasureString(commitText, detailFont);
            float pageOutX = content.Right - pageOutSize.Width;
            float separatorX = pageOutX - separatorSize.Width;
            float commitX = separatorX - commitSize.Width;
            g.DrawString(commitText, detailFont, commitBrush, commitX, y);
            g.DrawString(" · ", detailFont, detailBrush, separatorX, y);
            g.DrawString(pageOutText, detailFont, detailBrush, pageOutX, y);
        }
    }

    private void DrawMemoryPressureHistory(
        Graphics g,
        RectangleF rect,
        List<MemoryPressureHistoryPoint> history,
        PerfSnapshot snapshot)
    {
        float radius = Math.Max(1.5f, rect.Height / 2.0f);
        using (GraphicsPath track = RoundedRectangle(rect, radius))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(28)))
        {
            g.FillPath(trackBrush, track);
            Region previous = g.Clip;
            g.SetClip(track, CombineMode.Intersect);

            if (history != null && history.Count > 0)
            {
                DateTime endUtc = history[history.Count - 1].TimestampUtc;
                DateTime startUtc = endUtc.AddSeconds(-60.0);
                for (int i = 0; i < history.Count; i++)
                {
                    MemoryPressureHistoryPoint point = history[i];
                    DateTime nextUtc = i + 1 < history.Count ? history[i + 1].TimestampUtc : endUtc;
                    double startSeconds = (point.TimestampUtc - startUtc).TotalSeconds;
                    double endSeconds = (nextUtc - startUtc).TotalSeconds;
                    float x1 = rect.X + (float)(MetricTileModel.Clamp(startSeconds, 0.0, 60.0) / 60.0 * rect.Width);
                    float x2 = rect.X + (float)(MetricTileModel.Clamp(endSeconds, 0.0, 60.0) / 60.0 * rect.Width);
                    float width = Math.Max(S(1.4f), x2 - x1);
                    Color color = MetricTileModel.GetMemoryPressureColor(point.Level);
                    int alpha = (int)Math.Round(155.0 + MetricTileModel.Clamp(point.Percent, 0.0, 100.0) * 0.85);
                    using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, alpha)))
                    {
                        g.FillRectangle(fill, x1, rect.Y, width, rect.Height);
                    }
                }
            }
            else
            {
                Color color = MetricTileModel.GetMemoryPressureColor(snapshot.MemoryPressureLevel);
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 225)))
                {
                    g.FillRectangle(fill, rect);
                }
            }

            g.Clip = previous;
        }

        // The bright right edge is "now". Unlike a capacity fill, the strip reads left-to-right as
        // time and preserves the green/yellow/red state changes that made the current state useful.
        Color currentColor = MetricTileModel.GetMemoryPressureColor(snapshot.MemoryPressureLevel);
        using (Pen marker = new Pen(DesignTokens.WithAlpha(currentColor, 255), Math.Max(1.5f, S(1.8f))))
        {
            g.DrawLine(marker, rect.Right - S(1), rect.Y, rect.Right - S(1), rect.Bottom);
        }
    }

    private static string FormatPageOutRate(double megabytesPerSecond)
    {
        double value = Math.Max(0.0, megabytesPerSecond);
        if (value < 0.05)
        {
            return "0 MB";
        }

        return value < 10.0
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : value.ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    // ── Disk ─────────────────────────────────────────────────────────────
    private void DrawDisk(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        RectangleF ground = GroundRect(content, true);
        // Write and read share one auto-scaled axis so their relative magnitude stays honest; the
        // read line usually hugs the floor because Windows serves most reads from cache.
        double max = Math.Max(PeakOf(this.feed.DiskWriteHistory), PeakOf(this.feed.DiskReadHistory)) * 1.15;
        DrawSpark(g, ground, this.feed.DiskWriteHistory, DesignTokens.Colors.Warning, max, true, false);
        DrawSparkLineOnly(g, ground, this.feed.DiskReadHistory,
            DesignTokens.Colors.Success, max, 225, true);

        double capacityPercent = s.DiskTotalGb > 0.0 ? s.DiskUsedGb / s.DiskTotalGb * 100.0 : 0.0;
        DrawSegmentBar(g, StripRect(content), new double[] { capacityPercent }, new Color[] { accent }, new int[] { 215 });

        DrawFloatingHeader(g, content, accent, "DISK",
            Math.Round(s.DiskPercent).ToString("0", CultureInfo.InvariantCulture), "%",
            string.Format(CultureInfo.InvariantCulture, "W 写入 {0}    R 读取 {1}",
                FormatRate(s.DiskWriteBytesPerSecond), FormatRate(s.DiskReadBytesPerSecond)));
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)),
            string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:0} GB", s.DiskUsedGb, s.DiskTotalGb));
    }

    // ── Network ──────────────────────────────────────────────────────────
    // The mirror chart needs both halves and already spans the full height, so this is the one
    // panel with no bottom strip: the ground runs edge to edge.
    private void DrawNetwork(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        // The shell and NET label retain the existing green role accent. Only the two data series
        // use their stable transfer-direction colors, so the window continues to look like the
        // current product while down/up remain identifiable without a legend.
        DrawMirrorChart(
            g,
            GroundRect(content, false),
            this.feed.NetworkReceivedHistory,
            this.feed.NetworkSentHistory,
            DesignTokens.Colors.AccentSoft,
            DesignTokens.Colors.Danger);

        // Prefix dropped: the SSID already identifies a wireless link, and the sub line has to fit.
        string link = s.NetworkConnected
            ? (s.NetworkIsWifi ? (string.IsNullOrEmpty(s.NetworkName) ? "Wi-Fi" : s.NetworkName) : "以太网")
            : "未连接";
        string signal = s.NetworkRssiKnown
            ? s.NetworkRssiDbm.ToString(CultureInfo.InvariantCulture) + " dBm"
            : (s.NetworkConnected ? "正常" : "断开");

        DrawFloatingHeader(g, content, accent, "NET", string.Empty, null,
            "↓ 下行 " + FormatRate(s.NetworkReceivedBytesPerSecond) +
            "    ↑ 上行 " + FormatRate(s.NetworkSentBytesPerSecond));
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), link + " · " + signal);
    }

    private void DrawMirrorChart(Graphics g, RectangleF rect, List<double> down, List<double> up, Color downColor, Color upColor)
    {
        float baseline = rect.Y + rect.Height * 0.62f;
        double max = Math.Max(PeakOf(down), PeakOf(up)) * 1.1;
        if (max <= 0.0)
        {
            max = 1.0;
        }

        // Both halves are built explicitly rather than by reusing DrawSpark under a flip transform:
        // the flipped version mapped the upstream box back above the baseline, drawing the two
        // series on top of each other instead of mirroring them.
        DrawMirrorHalf(g, rect, down, downColor, max, baseline, baseline - rect.Y, true, 48, 235, true);
        DrawMirrorHalf(g, rect, up, upColor, max, baseline, rect.Bottom - baseline, false, 26, 220, true);

        using (Pen basePen = new Pen(DesignTokens.White(54), 1.0f))
        {
            g.DrawLine(basePen, rect.Left, baseline, rect.Right, baseline);
        }
    }

    private void DrawMirrorHalf(Graphics g, RectangleF rect, List<double> values, Color color, double max, float baseline, float amplitude, bool growUp, int fillAlpha, int lineAlpha, bool drawEndpoint)
    {
        if (values == null || values.Count < 2 || amplitude <= 1.0f)
        {
            return;
        }

        PointF[] points = new PointF[values.Count];
        float step = rect.Width / (values.Count - 1);
        for (int i = 0; i < values.Count; i++)
        {
            double ratio = MetricTileModel.Clamp(values[i] / max, 0.0, 1.0);
            float offset = (float)(ratio * (amplitude - 1.0f));
            points[i] = new PointF(rect.X + i * step, growUp ? baseline - offset : baseline + offset);
        }

        using (GraphicsPath area = new GraphicsPath())
        {
            area.AddLines(points);
            area.AddLine(points[points.Length - 1].X, baseline, rect.X, baseline);
            area.CloseFigure();
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, fillAlpha)))
            {
                g.FillPath(fill, area);
            }
        }

        using (Pen line = new Pen(
            DesignTokens.WithAlpha(color, lineAlpha),
            Math.Max(1.0f, S(growUp ? 1.6f : 1.1f))))
        {
            line.LineJoin = LineJoin.Round;
            g.DrawLines(line, points);
        }

        if (drawEndpoint)
        {
            PointF last = points[points.Length - 1];
            float dot = Math.Max(2.0f, S(2.6f));
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 255)))
            {
                g.FillEllipse(brush, last.X - dot, last.Y - dot, dot * 2.0f, dot * 2.0f);
            }
        }
    }

    // ── GPU / NPU ────────────────────────────────────────────────────────
    private void DrawGpu(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        DrawAcceleratorPanel(g, content, accent, "GPU", s.GpuPercent, s.GpuMemoryPercent,
            this.feed.GpuHistory, this.feed.GpuMemoryHistory,
            string.Format(CultureInfo.InvariantCulture, "VRAM {0:0.0} / {1:0.0} GB · 占用 {2:0.0}%",
                s.GpuMemoryUsedGb, s.GpuMemoryTotalGb, s.GpuMemoryPercent),
            "60 秒 亮=占用 淡=显存 · 底条=显存");
    }

    private void DrawNpu(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        DrawAcceleratorPanel(g, content, accent, "NPU", s.NpuPercent, s.NpuMemoryPercent,
            this.feed.NpuHistory, this.feed.NpuMemoryHistory,
            string.Format(CultureInfo.InvariantCulture, "内存 {0:0.0} / {1:0.0} GB · {2}",
                s.NpuMemoryUsedGb, s.NpuMemoryTotalGb, s.NpuPercent <= 0.5 ? "当前空闲" : "推理中"),
            "60 秒 亮=占用 淡=内存 · 底条=内存");
    }

    private void DrawAcceleratorPanel(Graphics g, RectangleF content, Color accent, string label, double percent, double memoryPercent, List<double> load, List<double> memory, string subLine, string caption)
    {
        RectangleF ground = GroundRect(content, true);
        DrawSpark(g, ground, load, accent, 100.0, true, true);
        DrawSparkLineOnly(g, ground, memory, accent, 100.0, 115, false);
        DrawSegmentBar(g, StripRect(content), new double[] { memoryPercent }, new Color[] { accent }, new int[] { 200 });

        DrawFloatingHeader(g, content, accent, label,
            Math.Round(MetricTileModel.Clamp(percent, 0.0, 100.0)).ToString("0", CultureInfo.InvariantCulture), "%",
            subLine);
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), caption);
    }

    // ── Power ────────────────────────────────────────────────────────────
    // No watts history buffer exists, so there is no curve to spread across the ground. Rather than
    // invent one, the ground is the thermal zone wall: every reporting sensor as a column, height
    // and brightness both tracking temperature. Battery level takes the bottom strip.
    private void DrawPower(Graphics g, RectangleF content, Color accent)
    {
        PowerStripSnapshot p = this.feed.Power;
        DrawThermalWall(g, GroundRect(content, true), p);

        int battery = p != null && p.BatteryPercentKnown ? p.BatteryPercent : -1;
        DrawSegmentBar(g, StripRect(content),
            new double[] { battery >= 0 ? battery : 0 },
            new Color[] { battery >= 0 && battery <= 20 ? DesignTokens.Colors.DangerStrong : DesignTokens.Colors.Success },
            new int[] { 225 });

        string watts = p != null && p.WattsKnown ? p.Watts.ToString("0.0", CultureInfo.InvariantCulture) + " W" : "-- W";
        string mode = p != null && !string.IsNullOrEmpty(p.PowerModeText) ? p.PowerModeText : "--";
        DrawFloatingHeader(g, content, accent, "PWR",
            battery >= 0 ? battery.ToString(CultureInfo.InvariantCulture) : "--", "%",
            watts + " · " + mode + " · " + FormatRuntime(p));

        string maxText = p != null && p.MaxCelsius > 0.0
            ? Math.Round(p.MaxCelsius).ToString("0", CultureInfo.InvariantCulture) + "°C"
            : "--";
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)),
            p != null && p.ZoneCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} 传感区 峰值 {1} · 底条=电池", p.ZoneCount, maxText)
                : "无温度传感数据");
    }

    private void DrawThermalWall(Graphics g, RectangleF rect, PowerStripSnapshot p)
    {
        int count = p != null && p.ZoneCount > 0 ? p.ZoneCount : 0;
        if (count <= 0 || rect.Height <= 2.0f)
        {
            return;
        }

        // Hot zones carry real readings; the remaining sensors are known to be below the alert
        // threshold, so they are drawn at the average rather than invented per-zone values.
        List<double> temps = new List<double>();
        if (p.HotZones != null)
        {
            for (int i = 0; i < p.HotZones.Count && temps.Count < count; i++)
            {
                temps.Add(p.HotZones[i].Celsius);
            }
        }

        while (temps.Count < count)
        {
            temps.Add(p.AvgCelsius);
        }

        float gap = Math.Max(1.0f, S(2));
        float cellW = (rect.Width - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            // Absolute 30-85 degree mapping, and the column height tracks temperature too, so the
            // wall has a silhouette instead of being a flat block of equal bars. A cool machine
            // honestly shows a low, dim, even row; a thermal event raises and lights its columns.
            double ratio = MetricTileModel.Clamp((temps[i] - 30.0) / 55.0, 0.0, 1.0);
            float h = Math.Max(rect.Height * 0.22f, (float)(0.35 + ratio * 0.65) * rect.Height);
            int alpha = 55 + (int)(ratio * 185.0);
            RectangleF cell = new RectangleF(rect.X + i * (cellW + gap), rect.Bottom - h, cellW, h);
            using (GraphicsPath path = RoundedRectangle(cell, Math.Max(1.0f, S(2))))
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, alpha)))
            {
                g.FillPath(brush, path);
            }
        }
    }

    private static string FormatRuntime(PowerStripSnapshot p)
    {
        if (p == null || !p.RuntimeSecondsKnown || p.RuntimeSeconds <= 0)
        {
            return p != null && p.PluggedIn ? "外接电源" : "剩余未知";
        }

        int minutes = p.RuntimeSeconds / 60;
        int hours = minutes / 60;
        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}h{1:00} 剩余", hours, minutes % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0}m 剩余", minutes);
    }

    // ── Radar: quota ─────────────────────────────────────────────────────
    // The full-bleed chart is the instrument: accepted weekly readings occupy its first 68%, leaving
    // a real future lane between "now" and the reset marker. The forecast headline translates that
    // geometry into the answer people need: when it runs out, or whether it survives the reset.
    private void DrawRadarQuota(Graphics g, RectangleF content, Color accent, bool claude)
    {
        RadarTileSnapshot r = this.feed.GetRadar(claude);
        RectangleF ground = GroundRect(content, true);
        Color dataAccent = BurnInProtection.NormalizeVisualLevel(this.burnInVisualLevel) == BurnInVisualLevel.LevelTwo
            ? BurnInProtection.InvertColor(accent)
            : accent;

        if (r.HasBurnCurve)
        {
            DrawBurnDown(g, ground, r, dataAccent);
        }

        DrawRadarQuotaScrims(g, content);
        DrawRadarQuotaIdentity(g, content, dataAccent, r);
        DrawRadarQuotaForecast(g, content, dataAccent, r);
        DrawFiveHourForecastStrip(g, content, dataAccent, r);
    }

    private void DrawRadarQuotaScrims(Graphics g, RectangleF content)
    {
        float leftWidth = Math.Min(content.Width * 0.60f, S(302));
        RectangleF left = new RectangleF(content.X - S(4), content.Y - S(3), leftWidth, S(78));
        using (LinearGradientBrush brush = new LinearGradientBrush(
            left,
            DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 245),
            DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 0),
            LinearGradientMode.Horizontal))
        {
            g.FillRectangle(brush, left);
        }

        RectangleF right = new RectangleF(content.Right - S(218), content.Y - S(3), S(222), S(76));
        using (LinearGradientBrush brush = new LinearGradientBrush(
            right,
            DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 0),
            DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 235),
            LinearGradientMode.Horizontal))
        {
            g.FillRectangle(brush, right);
        }
    }

    private void DrawRadarQuotaIdentity(Graphics g, RectangleF content, Color accent, RadarTileSnapshot r)
    {
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        using (Font labelFont = new Font(DesignTokens.UiFontFamily, S(17), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font valueFont = new Font(DesignTokens.MonoFontFamily, S(30), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font suffixFont = new Font(DesignTokens.UiFontFamily, S(14), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(accent, 235)))
        using (SolidBrush valueBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, 235)))
        using (SolidBrush suffixBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 210)))
        {
            float labelY = content.Y + S(6);
            g.DrawString(r.FamilyLabel, labelFont, labelBrush, content.X, labelY);
            if (drawNeutral)
            {
                float labelWidth = g.MeasureString(r.FamilyLabel, labelFont).Width;
                string balance = r.QuotaKnown
                    ? Math.Round(MetricTileModel.Clamp(r.WeeklyPercent, 0.0, 100.0)).ToString("0", CultureInfo.InvariantCulture)
                    : "--";
                float valueX = content.X + labelWidth + S(8);
                g.DrawString(balance, valueFont, valueBrush, valueX, content.Y);
                float valueWidth = g.MeasureString(balance, valueFont).Width;
                if (r.QuotaKnown)
                {
                    g.DrawString("%", suffixFont, suffixBrush, valueX + valueWidth - S(4), content.Y + S(13));
                }
            }
        }

        if (!drawNeutral)
        {
            return;
        }

        string fiveHour = r.FiveHourLimitAbsent
            ? "∞"
            : r.FiveHourPercent.ToString(CultureInfo.InvariantCulture) + "%";
        string quotaLine = r.QuotaKnown
            ? string.Format(
                CultureInfo.InvariantCulture,
                "5h {0}@{1} · 周 {2}%@{3}",
                fiveHour,
                r.FiveHourResetKnown ? r.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "未知",
                r.WeeklyPercent,
                r.WeeklyResetKnown ? r.WeeklyResetLocal.ToString("MM/dd HH:mm", CultureInfo.CurrentCulture) : "未知")
            : "额度未知";
        DrawText(g, quotaLine, content.X, content.Y + S(36), S(12.5f),
            DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 210), FontStyle.Regular);

        string model = string.IsNullOrEmpty(r.ModelName) ? r.FamilyLabel : r.ModelName;
        string sampleText = r.BurnRateKnown
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} · 最近 {1} 活跃时",
                model,
                FormatForecastHours(r.BurnObservedHours))
            : (r.CalendarRunwayKnown ? model + " · 近 24h 节奏已建立" : model + " · 趋势采样中");
        DrawText(g, sampleText, content.X, content.Y + S(57), S(11),
            DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 225), FontStyle.Regular);
    }

    private void DrawRadarQuotaForecast(Graphics g, RectangleF content, Color accent, RadarTileSnapshot r)
    {
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        float width = Math.Min(content.Width * 0.45f, S(205));
        RectangleF rect = new RectangleF(content.Right - width, content.Y, width, S(74));
        bool activeForecast = r.BurnRateKnown;
        bool forecastKnown = activeForecast || r.CalendarRunwayKnown;
        double forecastRunway = activeForecast ? r.RunwayHours : r.CalendarRunwayHours;
        bool comparisonKnown = forecastKnown && r.HoursToReset > 0.0;
        bool exhaustsBeforeReset = comparisonKnown && forecastRunway < r.HoursToReset;
        Color dangerColor = BurnInProtection.NormalizeVisualLevel(this.burnInVisualLevel) == BurnInVisualLevel.LevelTwo
            ? BurnInProtection.InvertColor(DesignTokens.Colors.Danger)
            : DesignTokens.Colors.Danger;
        Color stateColor = exhaustsBeforeReset ? dangerColor : accent;
        string main;
        string status;
        if (!r.QuotaKnown)
        {
            main = "额度未知";
            status = "等待额度来源";
            stateColor = DesignTokens.Colors.GlyphMuted;
        }
        else if (!forecastKnown)
        {
            main = "趋势采样中";
            status = r.HoursToReset > 0.0
                ? "距周重置 " + FormatForecastHours(r.HoursToReset)
                : "等待至少 1% 变化";
            stateColor = DesignTokens.Colors.GlyphMuted;
        }
        else if (exhaustsBeforeReset)
        {
            main = FormatForecastHours(forecastRunway) + " 后用完";
            status = "比周重置早 " + FormatForecastHours(r.HoursToReset - forecastRunway);
        }
        else if (comparisonKnown)
        {
            main = "可撑到重置";
            status = "按趋势多余 " + FormatForecastHours(forecastRunway - r.HoursToReset);
        }
        else
        {
            main = "约 " + FormatForecastHours(forecastRunway) + " 可用";
            status = "周重置时间未知";
        }

        if (drawNeutral)
        {
            DrawRightAlignedText(g, activeForecast ? "按当前活跃趋势" : "按近 24h 节奏", rect, rect.Y, S(10),
                DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 225), FontStyle.Regular);
        }

        string confidence = forecastKnown
            ? FormatQuotaForecastConfidence(activeForecast ? r.BurnConfidence : r.CalendarConfidence)
            : string.Empty;
        float statusRightInset = drawNeutral && !string.IsNullOrEmpty(confidence) ? S(48) : 0.0f;
        if (forecastKnown || drawNeutral)
        {
            DrawRightAlignedText(g, main, rect, rect.Y + S(14), S(21),
                DesignTokens.WithAlpha(stateColor, forecastKnown ? 255 : 220), FontStyle.Bold);
            DrawRightAlignedText(g, status, rect, rect.Y + S(40), S(12),
                DesignTokens.WithAlpha(stateColor, forecastKnown ? 245 : 210), FontStyle.Bold,
                statusRightInset);
        }

        if (drawNeutral)
        {
            if (!string.IsNullOrEmpty(confidence))
            {
                DrawRightAlignedText(g, confidence, rect, rect.Y + S(40), S(10),
                    DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 220), FontStyle.Regular);
            }

            string rhythm = activeForecast
                ? (r.CalendarRunwayKnown
                    ? "按近 24h 节奏：约 " + FormatForecastHours(r.CalendarRunwayHours)
                    : "近 24h 节奏：采样中")
                : "当前活跃趋势：采样中";
            DrawRightAlignedText(g, rhythm, rect, rect.Y + S(58), S(10),
                DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 205), FontStyle.Regular);
        }
    }

    private void DrawFiveHourForecastStrip(Graphics g, RectangleF content, Color accent, RadarTileSnapshot r)
    {
        RectangleF bar = new RectangleF(content.X, content.Bottom - S(4), content.Width, S(4));
        using (GraphicsPath track = RoundedRectangle(bar, Math.Max(1.0f, bar.Height / 2.0f)))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(28)))
        {
            g.FillPath(trackBrush, track);
            if (r.QuotaKnown && !r.FiveHourLimitAbsent)
            {
                float fillWidth = (float)(bar.Width * MetricTileModel.Clamp(r.FiveHourPercent, 0.0, 100.0) / 100.0);
                if (fillWidth > 0.0f)
                {
                    Region previous = g.Clip;
                    g.SetClip(track, CombineMode.Intersect);
                    using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(accent, 225)))
                    {
                        g.FillRectangle(fill, bar.X, bar.Y, fillWidth, bar.Height);
                    }
                    g.Clip = previous;
                }
            }
        }

        if (!ShouldDrawNeutralText(this.burnInVisualLevel) || !r.QuotaKnown)
        {
            return;
        }

        string prefix = r.FiveHourLimitAbsent
            ? "5h ∞"
            : "5h " + r.FiveHourPercent.ToString(CultureInfo.InvariantCulture) + "%";
        string detail;
        Color detailColor = DesignTokens.Colors.TextMuted;
        if (r.FiveHourLimitAbsent)
        {
            detail = " · 当前计划无短窗";
        }
        else if (!r.FiveHourBurnRateKnown)
        {
            detail = r.FiveHourHoursToReset > 0.0
                ? " · 趋势采样中，" + FormatForecastHours(r.FiveHourHoursToReset) + " 后重置"
                : " · 趋势采样中";
        }
        else if (r.FiveHourHoursToReset > 0.0 && r.FiveHourRunwayHours < r.FiveHourHoursToReset)
        {
            detail = " · 预计 " + FormatForecastHours(r.FiveHourRunwayHours) + " 用完，早 " +
                FormatForecastHours(r.FiveHourHoursToReset - r.FiveHourRunwayHours);
            detailColor = BurnInProtection.NormalizeVisualLevel(this.burnInVisualLevel) == BurnInVisualLevel.LevelTwo
                ? BurnInProtection.InvertColor(DesignTokens.Colors.Danger)
                : DesignTokens.Colors.Danger;
        }
        else if (r.FiveHourHoursToReset > 0.0)
        {
            detail = " · 可撑到本轮重置";
        }
        else
        {
            detail = " · 约 " + FormatForecastHours(r.FiveHourRunwayHours) + " 可用";
        }

        float y = content.Bottom - S(20);
        using (Font font = new Font(DesignTokens.UiFontFamily, S(10.5f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font detailFont = new Font(DesignTokens.UiFontFamily, S(10.5f), FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush prefixBrush = new SolidBrush(DesignTokens.WithAlpha(accent, 245)))
        using (SolidBrush detailBrush = new SolidBrush(DesignTokens.WithAlpha(detailColor, 225)))
        {
            g.DrawString(prefix, font, prefixBrush, content.X, y);
            float prefixWidth = g.MeasureString(prefix, font).Width - S(2);
            g.DrawString(detail, detailFont, detailBrush, content.X + prefixWidth, y);
        }

        if (r.WeeklyResetKnown)
        {
            DrawRightAlignedText(g, "周重置", content, y, S(9),
                DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 210), FontStyle.Regular);
        }
    }

    private void DrawRightAlignedText(
        Graphics g,
        string text,
        RectangleF rect,
        float y,
        float pixelSize,
        Color color,
        FontStyle style,
        float rightInset = 0.0f)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using (Font font = new Font(DesignTokens.UiFontFamily, Math.Max(6.0f, pixelSize), style, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
        {
            format.Alignment = StringAlignment.Far;
            g.DrawString(text, font, brush,
                new RectangleF(rect.X, y, Math.Max(1.0f, rect.Width - rightInset), pixelSize + S(5)), format);
        }
    }

    private static string FormatQuotaForecastConfidence(QuotaForecastConfidence confidence)
    {
        switch (confidence)
        {
            case QuotaForecastConfidence.High: return "高置信";
            case QuotaForecastConfidence.Medium: return "中置信";
            case QuotaForecastConfidence.Low: return "低置信";
            default: return string.Empty;
        }
    }

    private static string FormatForecastHours(double hours)
    {
        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0.0)
        {
            return "--";
        }

        if (hours < 1.0)
        {
            return Math.Max(1, (int)Math.Round(hours * 60.0)).ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (hours < 36.0)
        {
            return Math.Max(1, (int)Math.Round(hours)).ToString(CultureInfo.InvariantCulture) + "h";
        }

        int totalHours = Math.Max(1, (int)Math.Round(hours));
        int days = totalHours / 24;
        int remainder = totalHours % 24;
        return remainder == 0
            ? days.ToString(CultureInfo.InvariantCulture) + "天"
            : string.Format(CultureInfo.InvariantCulture, "{0}天{1}h", days, remainder);
    }

    private void DrawBurnDown(Graphics g, RectangleF rect, RadarTileSnapshot r, Color accent)
    {
        List<double> v = r.WeeklyBurnRemaining;
        PointF[] pts = new PointF[v.Count];
        float historyRight = rect.X + rect.Width * 0.68f;
        float step = (historyRight - rect.X) / (v.Count - 1);
        for (int i = 0; i < v.Count; i++)
        {
            double ratio = MetricTileModel.Clamp(v[i] / 100.0, 0.0, 1.0);
            pts[i] = new PointF(rect.X + i * step, rect.Bottom - (float)(ratio * (rect.Height - 2.0f)) - 1.0f);
        }

        using (GraphicsPath area = new GraphicsPath())
        {
            area.AddLines(pts);
            area.AddLine(pts[pts.Length - 1].X, rect.Bottom, rect.X, rect.Bottom);
            area.CloseFigure();
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(accent, 40)))
            {
                g.FillPath(fill, area);
            }
        }

        using (Pen line = new Pen(DesignTokens.WithAlpha(accent, 235), Math.Max(1.0f, S(1.6f))))
        {
            line.LineJoin = LineJoin.Round;
            g.DrawLines(line, pts);
        }

        // Projection uses the active rate when available and otherwise the recent wall-clock rate.
        // The plotted source follows the same preference, so the line and headline cannot disagree.
        bool forecastKnown = r.BurnRateKnown || r.CalendarRunwayKnown;
        double forecastRunway = r.BurnRateKnown ? r.RunwayHours : r.CalendarRunwayHours;
        double forecastRate = r.BurnRateKnown ? r.BurnPercentPerHour : r.CalendarBurnPercentPerHour;
        if (forecastKnown && r.HoursToReset > 0.0)
        {
            PointF last = pts[pts.Length - 1];
            float resetX = rect.Right - S(2);
            bool exhaustsBeforeReset = forecastRunway < r.HoursToReset;
            Color projColor = exhaustsBeforeReset
                ? (BurnInProtection.NormalizeVisualLevel(this.burnInVisualLevel) == BurnInVisualLevel.LevelTwo
                    ? BurnInProtection.InvertColor(DesignTokens.Colors.DangerStrong)
                    : DesignTokens.Colors.DangerStrong)
                : accent;
            using (Pen dash = new Pen(DesignTokens.WithAlpha(projColor, 170), Math.Max(1.0f, S(1.2f))))
            {
                dash.DashStyle = DashStyle.Dash;
                if (exhaustsBeforeReset)
                {
                    double ratio = MetricTileModel.Clamp(forecastRunway / r.HoursToReset, 0.0, 1.0);
                    float exhaustX = last.X + (resetX - last.X) * (float)ratio;
                    float zeroY = rect.Bottom - S(1);
                    g.DrawLine(dash, last.X, last.Y, exhaustX, zeroY);
                    using (Pen bracket = new Pen(DesignTokens.WithAlpha(projColor, 120), Math.Max(1.0f, S(0.8f))))
                    using (SolidBrush dotBrush = new SolidBrush(DesignTokens.WithAlpha(projColor, 245)))
                    {
                        g.DrawLine(bracket, exhaustX, zeroY, resetX, zeroY);
                        g.DrawLine(bracket, exhaustX, zeroY - S(3), exhaustX, zeroY + S(1));
                        float riskDot = Math.Max(1.5f, S(2.2f));
                        g.FillEllipse(dotBrush, exhaustX - riskDot, zeroY - riskDot, riskDot * 2.0f, riskDot * 2.0f);
                    }
                }
                else
                {
                    double projected = Math.Max(0.0, r.WeeklyPercent - forecastRate * r.HoursToReset);
                    float projY = rect.Bottom - (float)(projected / 100.0 * (rect.Height - 2.0f)) - 1.0f;
                    g.DrawLine(dash, last.X, last.Y, resetX, projY);
                }
            }
        }

        if (r.WeeklyResetKnown && r.HoursToReset > 0.0)
        {
            using (Pen resetLine = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 120), 1.0f))
            {
                resetLine.DashStyle = DashStyle.Dot;
                g.DrawLine(resetLine, rect.Right - S(2), rect.Y, rect.Right - S(2), rect.Bottom);
            }
        }

        PointF endPoint = pts[pts.Length - 1];
        float dot = Math.Max(2.0f, S(2.6f));
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(accent, 255)))
        {
            g.FillEllipse(brush, endPoint.X - dot, endPoint.Y - dot, dot * 2.0f, dot * 2.0f);
        }
    }

    // ── Guard ────────────────────────────────────────────────────────────
    // Four independent states, no series. The ground is four full-width bands — armed ones tinted
    // with their own accent and given a bright leading edge, idle ones nearly empty — so "what is
    // being held open" is scannable down the left side without reading any text.
    private void DrawGuard(Graphics g, RectangleF content, Color accent)
    {
        List<MetricTileGuardEntry> guards = this.feed.Guards ?? new List<MetricTileGuardEntry>();
        if (guards.Count == 0)
        {
            DrawFloatingHeader(g, content, accent, "守护", "--", null, "守护状态不可用");
            return;
        }

        float rowH = content.Height / guards.Count;
        for (int i = 0; i < guards.Count; i++)
        {
            MetricTileGuardEntry entry = guards[i];
            RectangleF band = new RectangleF(content.X, content.Y + i * rowH, content.Width, rowH - S(2));
            Color tint = entry.Accent;
            using (GraphicsPath path = RoundedRectangle(band, Math.Max(1.0f, S(3))))
            using (SolidBrush brush = new SolidBrush(entry.Active
                ? DesignTokens.WithAlpha(tint, 46)
                : DesignTokens.White(12)))
            {
                g.FillPath(brush, path);
            }

            if (entry.Active)
            {
                using (SolidBrush edge = new SolidBrush(DesignTokens.WithAlpha(tint, 235)))
                {
                    g.FillRectangle(edge, band.X, band.Y, S(3), band.Height);
                }
            }

            float textY = band.Y + (band.Height - S(SubSize)) / 2.0f;
            DrawText(g, entry.Label, band.X + S(10), textY, S(SubSize),
                entry.Active ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.TextMuted, FontStyle.Bold);
            DrawText(g, entry.Description, band.X + S(92), textY + S(1), S(SubSize) * 0.86f,
                DesignTokens.Colors.GlyphMuted, FontStyle.Regular);

            if (ShouldDrawNeutralText(this.burnInVisualLevel))
            {
                using (Font font = new Font("Segoe UI", S(SubSize) * 0.94f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(entry.Active ? DesignTokens.Colors.TextStrong : DesignTokens.Colors.GlyphMuted))
                using (StringFormat fmt = new StringFormat(StringFormatFlags.NoWrap))
                {
                    fmt.Alignment = StringAlignment.Far;
                    g.DrawString(entry.Detail, font, brush, new RectangleF(band.X, textY, band.Width - S(10), band.Height), fmt);
                }
            }
        }
    }

    internal static bool ShouldDrawNeutralText(BurnInVisualLevel level)
    {
        return BurnInProtection.NormalizeVisualLevel(level) != BurnInVisualLevel.LevelTwo;
    }

    private static double PeakOf(List<double> history)
    {
        if (history == null || history.Count == 0)
        {
            return 0.0;
        }

        double peak = 0.0;
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] > peak)
            {
                peak = history[i];
            }
        }

        return peak;
    }

    private static double PeakOf(double[] values)
    {
        if (values == null || values.Length == 0)
        {
            return 0.0;
        }

        double peak = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > peak)
            {
                peak = values[i];
            }
        }

        return peak;
    }

    private static string FormatRate(double bytesPerSecond)
    {
        return NetworkRateFormatter.Format(bytesPerSecond);
    }

    internal void PrepareForRenderSample(MetricTileId id, MetricTileFeed sampleFeed)
    {
        this.metricId = id;
        this.feed = sampleFeed ?? new MetricTileFeed();
    }

    internal static void RunSelfTest()
    {
        if (!ShouldDrawNeutralText(BurnInVisualLevel.Normal) ||
            !ShouldDrawNeutralText(BurnInVisualLevel.LevelOne) ||
            ShouldDrawNeutralText(BurnInVisualLevel.LevelTwo))
        {
            throw new InvalidOperationException("Level-two burn-in must suppress expanded-panel neutral text.");
        }

        RectangleF testPlot = new RectangleF(3.0f, 5.0f, 200.0f, 80.0f);
        float fullScaleY = ResolvePlotY(testPlot, 100.0, 100.0);
        if (Math.Abs(fullScaleY - (testPlot.Top + 1.0f)) > 0.001f)
        {
            throw new InvalidOperationException("A 100% CPU core must align with the 100% guide row.");
        }

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        using (MetricTileExpandForm panel = new MetricTileExpandForm(settings))
        {
            // The migrated compact panel preserves the user's prior expanded-window size.
            Size desired = panel.GetDesiredSize();
            if (desired.Width != settings.MetricTileExpandWidth || desired.Height != settings.MetricTileExpandHeight)
            {
                throw new InvalidOperationException(
                    "Compact expand panel must match its retained size (" +
                    settings.MetricTileExpandWidth + "x" + settings.MetricTileExpandHeight + "); got " +
                    desired.Width + "x" + desired.Height + ".");
            }

            // The panel opens to the LEFT of the column and must stay inside the work area even when
            // the hovered tile sits at the very bottom of the screen.
            Rectangle workArea = settings.GetWorkAreaForModule(WidgetSettings.ModuleMain);
            Rectangle lowTile = new Rectangle(workArea.Right - 60, workArea.Bottom - 40, 60, 60);
            panel.ShowForTileGeometryForTest(lowTile, workArea);
            if (panel.Bottom > workArea.Bottom || panel.Top < workArea.Top)
            {
                throw new InvalidOperationException("Expand panel must slide up to stay inside the work area.");
            }

            if (panel.Right > lowTile.Left)
            {
                throw new InvalidOperationException("Expand panel must open to the left of the hovered tile.");
            }
        }

        WidgetSettings large = WidgetSettings.CreateDefaults();
        large.MainWidgetTileLargeModeEnabled = true;
        large.Normalize();
        using (MetricTileExpandForm panel = new MetricTileExpandForm(large))
        {
            Size desired = panel.GetDesiredSize();
            if (desired.Width != large.MetricTileExpandWidth * 2 || desired.Height != large.MetricTileExpandHeight * 2)
            {
                throw new InvalidOperationException("Large mode must double the expanded-panel size.");
            }
        }

        Console.WriteLine("Metric tile expand: PASS Radar-module size, placement, large mode, level-two neutral-text suppression");
    }

    // Geometry-only half of ShowForTile, so the self test can assert placement without creating a
    // window handle or touching the layered surface.
    internal void ShowForTileGeometryForTest(Rectangle anchorTile, Rectangle workArea)
    {
        ApplyPanelSizeAndScale();
        int left = anchorTile.Left - this.Width - S(GapToColumnDesignUnits);
        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - this.Width)));
        int top = anchorTile.Top;
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Height)));
        this.Location = new Point(left, top);
    }
}
