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
// One window is reused for all eleven tiles: the metric only changes which content renderer runs, so
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
    // Hover reveals the right group's original colours and luminance without clearing the real
    // level-two state; white/neutral text therefore remains hidden until burn-in protection exits.
    private bool burnInPresentationRestored;
    private bool quotaRevivalVisible;

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyMouseClickThroughStyle(this.CurrentSettings.RightTileMouseClickThroughEnabled);
    }

    protected override int PresentationLuminancePercent
    {
        get
        {
            return this.burnInVisualLevel != BurnInVisualLevel.Normal && !this.burnInPresentationRestored
                ? BurnInProtection.LevelOneLuminancePercent
                : 100;
        }
    }

    public bool SetBurnInVisualState(BurnInVisualLevel level, bool restoreRightGroupPresentation)
    {
        BurnInVisualLevel normalized = BurnInProtection.NormalizeVisualLevel(level);
        bool restored = normalized != BurnInVisualLevel.Normal && restoreRightGroupPresentation;
        if (this.burnInVisualLevel == normalized && this.burnInPresentationRestored == restored)
        {
            return false;
        }

        this.burnInVisualLevel = normalized;
        this.burnInPresentationRestored = restored;
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
        ApplyMouseClickThroughStyle(this.CurrentSettings.RightTileMouseClickThroughEnabled);
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
        ShowForTile(id, anchorTile, next, false);
    }

    public void ShowForTile(MetricTileId id, Rectangle anchorTile, MetricTileFeed next, bool showRevival)
    {
        this.metricId = id;
        this.feed = next ?? this.feed ?? new MetricTileFeed();
        this.quotaRevivalVisible = showRevival &&
            (id == MetricTileId.CodexQuota || id == MetricTileId.ClaudeQuota);
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
        this.quotaRevivalVisible = false;
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
            case MetricTileId.DeepSeekQuota: DrawDeepSeekBalance(g, content, accent); break;
            default: DrawGuard(g, content, accent); break;
        }

        if (this.metricId == MetricTileId.CodexQuota || this.metricId == MetricTileId.ClaudeQuota)
        {
            string secondLine = string.Empty;
            QuotaEasterEggVisual visual = this.quotaRevivalVisible
                ? QuotaEasterEggVisual.Revived
                : MetricTileModel.ResolveQuotaEasterEggVisual(
                    this.metricId,
                    this.feed.QuotaEasterEgg,
                    out secondLine);
            if (this.quotaRevivalVisible)
            {
                secondLine = "成功复活！！！";
            }

            if (visual != QuotaEasterEggVisual.None)
            {
                DrawQuotaEasterEggOverlay(g, bounds, visual, secondLine);
            }
        }
    }

    private void DrawQuotaEasterEggOverlay(
        Graphics g,
        RectangleF bounds,
        QuotaEasterEggVisual visual,
        string secondLine)
    {
        Color color = visual == QuotaEasterEggVisual.FallenTogether
            ? DesignTokens.Colors.DangerStrong
            : (visual == QuotaEasterEggVisual.Revived
                ? DesignTokens.Colors.Accent
                : DesignTokens.Colors.Warning);
        color = MetricTileForm.ResolveBurnInRingColor(
            color,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        RectangleF veilBounds = RectangleF.Inflate(bounds, -S(2), -S(2));
        using (GraphicsPath veil = RoundedRectangle(veilBounds, Math.Max(2.0f, S(DesignTokens.Radius.Panel - 1))))
        using (SolidBrush dim = new SolidBrush(Color.FromArgb(205, 4, 6, 9)))
        {
            g.FillPath(dim, veil);
        }

        FontStyle titleStyle = visual == QuotaEasterEggVisual.Revived
            ? FontStyle.Bold | FontStyle.Italic
            : FontStyle.Bold;
        FontStyle detailStyle = visual == QuotaEasterEggVisual.Revived
            ? FontStyle.Italic
            : FontStyle.Bold;
        float horizontalPad = Math.Max(S(12), bounds.Width * 0.028f);
        float verticalPad = Math.Max(S(4), bounds.Height * 0.055f);
        float lineGap = Math.Max(S(1), bounds.Height * 0.025f);
        float availableHeight = Math.Max(S(12), bounds.Height - verticalPad * 2.0f - lineGap);
        float titleHeight = availableHeight * 0.54f;
        float detailHeight = availableHeight - titleHeight;
        RectangleF titleRect = new RectangleF(
            bounds.X + horizontalPad,
            bounds.Y + verticalPad,
            Math.Max(1.0f, bounds.Width - horizontalPad * 2.0f),
            titleHeight);
        RectangleF detailRect = new RectangleF(
            titleRect.X,
            titleRect.Bottom + lineGap,
            titleRect.Width,
            detailHeight);
        float titleSize = FitQuotaMessageFontSize(
            g,
            "传奇程序员",
            titleStyle,
            Math.Min(S(38), titleRect.Height * 0.72f),
            titleRect.Width);
        float detailSize = FitQuotaMessageFontSize(
            g,
            secondLine ?? string.Empty,
            detailStyle,
            Math.Min(S(30), detailRect.Height * 0.72f),
            detailRect.Width);
        using (Font titleFont = new Font(DesignTokens.UiFontFamily, titleSize, titleStyle, GraphicsUnit.Pixel))
        using (Font detailFont = new Font(DesignTokens.UiFontFamily, detailSize, detailStyle, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 255)))
        using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            g.DrawString("传奇程序员", titleFont, brush, titleRect, format);
            g.DrawString(secondLine ?? string.Empty, detailFont, brush, detailRect, format);
        }
    }

    private static float FitQuotaMessageFontSize(
        Graphics g,
        string text,
        FontStyle style,
        float preferredSize,
        float availableWidth)
    {
        float safePreferred = Math.Max(6.0f, preferredSize);
        if (g == null || string.IsNullOrEmpty(text) || availableWidth <= 1.0f)
        {
            return safePreferred;
        }

        using (Font measureFont = new Font(
            DesignTokens.UiFontFamily,
            safePreferred,
            style,
            GraphicsUnit.Pixel))
        using (StringFormat measureFormat = new StringFormat(StringFormatFlags.NoWrap))
        {
            SizeF measured = g.MeasureString(text, measureFont, PointF.Empty, measureFormat);
            if (measured.Width <= availableWidth || measured.Width <= 0.0f)
            {
                return safePreferred;
            }

            return Math.Max(6.0f, safePreferred * availableWidth / measured.Width);
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
    // GUARD has no time series to spread across the ground: it is four independent states, so its
    // ground is four tinted bands rather than an invented curve. PWR now receives a cache-only
    // System Day history projection and can draw real battery/watts series.
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
                NetworkRateFormatter.FormatStorage(s.DiskWriteBytesPerSecond),
                NetworkRateFormatter.FormatStorage(s.DiskReadBytesPerSecond)));
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
    // PWR reuses the cache-only last-24-hour projection already owned by System Day. The hover
    // panel must never start sampling or file I/O: its job is to translate battery, watts and the
    // existing ETA into the same identity/chart/forecast hierarchy as the quota panels.
    private void DrawPower(Graphics g, RectangleF content, Color accent)
    {
        PowerStripSnapshot p = this.feed.Power;
        SystemDayBoardSnapshot day = this.feed.PowerDay;
        DrawPowerHistory(g, GroundRect(content, true), p, day);
        DrawRadarQuotaScrims(g, content);
        DrawPowerIdentity(g, content, accent, p);
        DrawPowerModeRail(g, content, p);
        DrawPowerForecast(g, content, accent, p, day);
        DrawPowerPeakBadge(g, content, day);
        DrawPowerBatteryStrip(g, content, accent, p, day);
    }

    private void DrawPowerHistory(
        Graphics g,
        RectangleF ground,
        PowerStripSnapshot power,
        SystemDayBoardSnapshot day)
    {
        List<double> watts = new List<double>();
        List<double> battery = new List<double>();
        double wattsPeak = 0.0;
        if (day != null && day.Points != null)
        {
            for (int i = 0; i < day.Points.Count; i++)
            {
                SystemDayBoardPoint point = day.Points[i];
                if (point == null)
                {
                    continue;
                }

                if (point.WattsKnown)
                {
                    double value = Math.Max(0.0, point.Watts);
                    watts.Add(value);
                    wattsPeak = Math.Max(wattsPeak, value);
                }

                if (point.BatteryKnown)
                {
                    battery.Add(MetricTileModel.Clamp(point.BatteryPercent, 0.0, 100.0));
                }
            }
        }

        Color wattsColor = MetricTileForm.ResolveBurnInRingColor(
            DesignTokens.Colors.Warning,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        bool charging = power != null ? power.Charging : day != null && day.CurrentCharging;
        Color batteryColor = MetricTileForm.ResolveBurnInRingColor(
            charging ? DesignTokens.Colors.DangerStrong : DesignTokens.Colors.Accent,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        DrawSpark(g, ground, watts, wattsColor, Math.Max(1.0, wattsPeak), true, false);
        DrawSparkLineOnly(g, ground, battery, batteryColor, 100.0, 205, true);
    }

    private void DrawPowerIdentity(
        Graphics g,
        RectangleF content,
        Color accent,
        PowerStripSnapshot power)
    {
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        Color dataAccent = MetricTileForm.ResolveBurnInRingColor(
            accent,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        int battery = ResolvePowerBatteryPercent(power);
        using (Font labelFont = new Font(DesignTokens.UiFontFamily, S(15), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(dataAccent, 235)))
        {
            g.DrawString("PWR", labelFont, labelBrush, content.X, content.Y);
        }

        RectangleF batteryRect = new RectangleF(
            content.X,
            content.Y + S(23),
            S(69),
            S(29));
        Color batteryColor = power != null && power.Charging
            ? DesignTokens.Colors.DangerStrong
            : battery >= 0 && battery <= 20
                ? DesignTokens.Colors.DangerStrong
                : dataAccent;
        DrawPowerBatteryGlyph(g, batteryRect, battery, batteryColor, drawNeutral);

        double watts;
        bool wattsKnown = TryResolveLivePowerWatts(power, out watts);
        string wattsText = wattsKnown
            ? watts.ToString("0.0", CultureInfo.InvariantCulture) + " W"
            : "-- W";
        DrawText(g, wattsText, content.X + S(3), content.Y + S(57), S(13.5f),
            DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, 230), FontStyle.Bold);
    }

    private void DrawPowerBatteryGlyph(
        Graphics g,
        RectangleF rect,
        int battery,
        Color color,
        bool drawValue)
    {
        RectangleF body = new RectangleF(rect.X, rect.Y, rect.Width - S(6), rect.Height);
        RectangleF nub = new RectangleF(body.Right + S(1), body.Y + body.Height * 0.32f, S(5), body.Height * 0.36f);
        using (GraphicsPath bodyPath = RoundedRectangle(body, Math.Max(2.0f, S(5))))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(22)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(color, 230), Math.Max(1.0f, S(1.2f))))
        using (SolidBrush nubBrush = new SolidBrush(DesignTokens.WithAlpha(color, 215)))
        {
            g.FillPath(trackBrush, bodyPath);
            if (battery >= 0)
            {
                RectangleF inner = RectangleF.Inflate(body, -S(3), -S(3));
                float fillWidth = inner.Width * (float)(battery / 100.0);
                GraphicsState clipState = g.Save();
                g.SetClip(bodyPath, CombineMode.Intersect);
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 88)))
                {
                    g.FillRectangle(fill, inner.X, inner.Y, Math.Max(0.0f, fillWidth), inner.Height);
                }

                g.Restore(clipState);
            }

            g.DrawPath(outline, bodyPath);
            g.FillRectangle(nubBrush, nub);
        }

        if (!drawValue)
        {
            return;
        }

        string value = battery >= 0
            ? battery.ToString(CultureInfo.InvariantCulture) + "%"
            : "--";
        using (Font font = new Font(DesignTokens.MonoFontFamily, S(14), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, 238)))
        using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString(value, font, brush, body, format);
        }
    }

    private void DrawPowerModeRail(Graphics g, RectangleF content, PowerStripSnapshot power)
    {
        PowerModeVisual active = ResolvePowerModeVisual(power);
        RectangleF rail = new RectangleF(content.X + S(96), content.Y + S(10), S(228), S(34));
        float segmentWidth = rail.Width / 3.0f;
        using (GraphicsPath track = RoundedRectangle(rail, Math.Max(2.0f, S(8))))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(18)))
        using (Pen trackPen = new Pen(DesignTokens.White(42), Math.Max(1.0f, S(1))))
        {
            g.FillPath(trackBrush, track);
            g.DrawPath(trackPen, track);
        }

        for (int i = 0; i < 3; i++)
        {
            PowerModeVisual mode = i == 0
                ? PowerModeVisual.Saver
                : i == 1
                    ? PowerModeVisual.Balanced
                    : PowerModeVisual.Performance;
            RectangleF segment = new RectangleF(
                rail.X + i * segmentWidth,
                rail.Y,
                segmentWidth,
                rail.Height);
            Color modeColor = ResolvePowerModeColor(mode);
            bool selected = mode == active;
            if (selected)
            {
                RectangleF selectedRect = RectangleF.Inflate(segment, -S(2), -S(2));
                using (GraphicsPath selectedPath = RoundedRectangle(selectedRect, Math.Max(2.0f, S(6))))
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(modeColor, 42)))
                using (Pen outline = new Pen(DesignTokens.WithAlpha(modeColor, 180), Math.Max(1.0f, S(1))))
                {
                    g.FillPath(fill, selectedPath);
                    g.DrawPath(outline, selectedPath);
                }
            }

            if (i > 0)
            {
                using (Pen separator = new Pen(DesignTokens.White(28), Math.Max(1.0f, S(1))))
                {
                    g.DrawLine(separator, segment.Left, segment.Top + S(7), segment.Left, segment.Bottom - S(7));
                }
            }

            Color glyphColor = selected
                ? modeColor
                : DesignTokens.Colors.GlyphMuted;
            RectangleF icon = new RectangleF(segment.X + S(14), segment.Y + S(9), S(16), S(16));
            DrawPowerModeGlyph(g, icon, mode, glyphColor);
            if (ShouldDrawNeutralText(this.burnInVisualLevel))
            {
                string label = mode == PowerModeVisual.Saver
                    ? "省"
                    : mode == PowerModeVisual.Balanced
                        ? "衡"
                        : "性";
                using (Font font = new Font(DesignTokens.UiFontFamily, S(12), FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(glyphColor, selected ? 245 : 190)))
                using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
                {
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(label, font, brush,
                        new RectangleF(icon.Right + S(6), segment.Y, S(26), segment.Height), format);
                }
            }
        }

        bool saverActive = power != null && power.EnergySaverActive;
        Color saverColor = saverActive
            ? MetricTileForm.ResolveBurnInRingColor(
                DesignTokens.Colors.Success,
                this.burnInVisualLevel,
                this.burnInPresentationRestored)
            : DesignTokens.Colors.GlyphMuted;
        RectangleF leaf = new RectangleF(rail.X + S(12), rail.Bottom + S(8), S(15), S(15));
        DrawPowerLeafGlyph(g, leaf, saverColor);
        DrawPowerSaverToggle(
            g,
            new RectangleF(leaf.Right + S(8), leaf.Y + S(1), S(31), S(13)),
            saverActive,
            saverColor);
    }

    private void DrawPowerModeGlyph(Graphics g, RectangleF rect, PowerModeVisual mode, Color color)
    {
        if (mode == PowerModeVisual.Saver)
        {
            DrawPowerLeafGlyph(g, rect, color);
            return;
        }

        using (Pen pen = new Pen(DesignTokens.WithAlpha(color, 235), Math.Max(1.0f, S(1.4f))))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            if (mode == PowerModeVisual.Balanced)
            {
                RectangleF gauge = RectangleF.Inflate(rect, -S(1), -S(1));
                g.DrawArc(pen, gauge, 205.0f, 130.0f);
                PointF center = new PointF(gauge.X + gauge.Width / 2.0f, gauge.Y + gauge.Height * 0.72f);
                g.DrawLine(pen, center, new PointF(gauge.Right - S(3), gauge.Y + S(4)));
                return;
            }
        }

        PointF[] bolt = new PointF[]
        {
            new PointF(rect.X + rect.Width * 0.58f, rect.Y),
            new PointF(rect.X + rect.Width * 0.20f, rect.Y + rect.Height * 0.56f),
            new PointF(rect.X + rect.Width * 0.48f, rect.Y + rect.Height * 0.56f),
            new PointF(rect.X + rect.Width * 0.34f, rect.Bottom),
            new PointF(rect.X + rect.Width * 0.82f, rect.Y + rect.Height * 0.42f),
            new PointF(rect.X + rect.Width * 0.55f, rect.Y + rect.Height * 0.42f)
        };
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 235)))
        {
            g.FillPolygon(brush, bolt);
        }
    }

    private void DrawPowerLeafGlyph(Graphics g, RectangleF rect, Color color)
    {
        using (GraphicsPath leaf = new GraphicsPath())
        {
            leaf.StartFigure();
            leaf.AddBezier(
                rect.X + rect.Width * 0.12f, rect.Bottom - rect.Height * 0.10f,
                rect.X + rect.Width * 0.03f, rect.Y + rect.Height * 0.18f,
                rect.X + rect.Width * 0.62f, rect.Y,
                rect.Right - rect.Width * 0.05f, rect.Y + rect.Height * 0.12f);
            leaf.AddBezier(
                rect.Right - rect.Width * 0.05f, rect.Y + rect.Height * 0.12f,
                rect.Right, rect.Y + rect.Height * 0.62f,
                rect.X + rect.Width * 0.55f, rect.Bottom,
                rect.X + rect.Width * 0.12f, rect.Bottom - rect.Height * 0.10f);
            leaf.CloseFigure();
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 72)))
            using (Pen outline = new Pen(DesignTokens.WithAlpha(color, 225), Math.Max(1.0f, S(1.1f))))
            {
                g.FillPath(fill, leaf);
                g.DrawPath(outline, leaf);
                g.DrawLine(
                    outline,
                    rect.X + rect.Width * 0.16f,
                    rect.Bottom - rect.Height * 0.12f,
                    rect.Right - rect.Width * 0.18f,
                    rect.Y + rect.Height * 0.25f);
            }
        }
    }

    private void DrawPowerSaverToggle(Graphics g, RectangleF rect, bool active, Color color)
    {
        float radius = rect.Height / 2.0f;
        using (GraphicsPath track = RoundedRectangle(rect, Math.Max(2.0f, radius)))
        using (SolidBrush trackBrush = new SolidBrush(active
            ? DesignTokens.WithAlpha(color, 118)
            : DesignTokens.White(26)))
        {
            g.FillPath(trackBrush, track);
        }

        float knobSize = Math.Max(S(7), rect.Height - S(4));
        float knobX = active
            ? rect.Right - knobSize - S(2)
            : rect.X + S(2);
        using (SolidBrush knob = new SolidBrush(active
            ? DesignTokens.WithAlpha(color, 245)
            : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 210)))
        {
            g.FillEllipse(knob, knobX, rect.Y + (rect.Height - knobSize) / 2.0f, knobSize, knobSize);
        }
    }

    private void DrawPowerForecast(
        Graphics g,
        RectangleF content,
        Color accent,
        PowerStripSnapshot power,
        SystemDayBoardSnapshot day)
    {
        PowerForecastPresentation forecast = ResolvePowerForecast(power, day);
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        float width = Math.Min(content.Width * 0.30f, S(138));
        RectangleF rect = new RectangleF(content.Right - width, content.Y + S(8), width, S(62));
        Color stateColor = ResolvePowerForecastColor(forecast.Tone, accent);
        stateColor = MetricTileForm.ResolveBurnInRingColor(
            stateColor,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);

        DrawPowerForecastGlyph(
            g,
            new RectangleF(rect.X + S(2), rect.Y + S(10), S(30), S(30)),
            power,
            forecast,
            stateColor);

        if (forecast.Known || drawNeutral)
        {
            DrawRightAlignedText(g, forecast.Main, rect, rect.Y + S(5), S(24),
                DesignTokens.WithAlpha(stateColor, forecast.Known ? 255 : 220), FontStyle.Bold);
            DrawRightAlignedText(g, forecast.Status, rect, rect.Y + S(35), S(12),
                DesignTokens.WithAlpha(stateColor, forecast.Known ? 245 : 210), FontStyle.Bold);
        }
    }

    private void DrawPowerForecastGlyph(
        Graphics g,
        RectangleF rect,
        PowerStripSnapshot power,
        PowerForecastPresentation forecast,
        Color color)
    {
        using (SolidBrush halo = new SolidBrush(DesignTokens.WithAlpha(color, 24)))
        using (Pen pen = new Pen(DesignTokens.WithAlpha(color, 225), Math.Max(1.0f, S(1.4f))))
        {
            g.FillEllipse(halo, rect);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            bool charging = power != null && power.Charging;
            bool pluggedIn = power != null && power.PluggedIn;
            if (!charging && !pluggedIn && forecast.Known)
            {
                RectangleF clock = RectangleF.Inflate(rect, -S(4), -S(4));
                g.DrawEllipse(pen, clock);
                PointF center = new PointF(clock.X + clock.Width / 2.0f, clock.Y + clock.Height / 2.0f);
                g.DrawLine(pen, center, new PointF(center.X, clock.Y + S(5)));
                g.DrawLine(pen, center, new PointF(clock.Right - S(5), center.Y + S(3)));
                return;
            }

            RectangleF body = new RectangleF(
                rect.X + S(4),
                rect.Y + S(8),
                rect.Width - S(11),
                rect.Height - S(16));
            g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
            g.DrawLine(pen, body.Right + S(2), body.Y + body.Height * 0.35f,
                body.Right + S(2), body.Bottom - body.Height * 0.35f);
            if (charging)
            {
                DrawPowerModeGlyph(
                    g,
                    RectangleF.Inflate(body, -S(4), -S(2)),
                    PowerModeVisual.Performance,
                    color);
            }
            else if (pluggedIn)
            {
                g.DrawLine(pen, body.X + S(4), body.Y + body.Height * 0.55f,
                    body.X + body.Width * 0.44f, body.Bottom - S(3));
                g.DrawLine(pen, body.X + body.Width * 0.44f, body.Bottom - S(3),
                    body.Right - S(3), body.Y + S(3));
            }
            else
            {
                g.DrawLine(pen, body.X + S(5), body.Y + body.Height / 2.0f,
                    body.Right - S(5), body.Y + body.Height / 2.0f);
            }
        }
    }

    private void DrawPowerPeakBadge(Graphics g, RectangleF content, SystemDayBoardSnapshot day)
    {
        double peak = ResolvePowerWattsPeak(day);
        if (peak <= 0.0)
        {
            return;
        }

        Color color = MetricTileForm.ResolveBurnInRingColor(
            DesignTokens.Colors.Warning,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        float x = content.X + S(188);
        float y = content.Bottom - S(30);
        PointF[] marker = new PointF[]
        {
            new PointF(x, y + S(7)),
            new PointF(x + S(4), y),
            new PointF(x + S(8), y + S(7))
        };
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 240)))
        {
            g.FillPolygon(brush, marker);
        }

        DrawText(
            g,
            peak.ToString("0.0", CultureInfo.InvariantCulture) + " W",
            x + S(12),
            y - S(3),
            S(10.5f),
            DesignTokens.WithAlpha(color, 230),
            FontStyle.Bold);
    }

    private void DrawPowerBatteryStrip(
        Graphics g,
        RectangleF content,
        Color accent,
        PowerStripSnapshot power,
        SystemDayBoardSnapshot day)
    {
        int battery = ResolvePowerBatteryPercent(power);
        bool charging = power != null && power.Charging;
        Color fillColor = charging
            ? DesignTokens.Colors.DangerStrong
            : battery >= 0 && battery <= 20
                ? DesignTokens.Colors.DangerStrong
                : accent;
        fillColor = MetricTileForm.ResolveBurnInRingColor(
            fillColor,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        DrawSegmentBar(g, StripRect(content),
            new double[] { battery >= 0 ? battery : 0 },
            new Color[] { fillColor },
            new int[] { 225 });
        RectangleF strip = StripRect(content);
        using (Pen tick = new Pen(DesignTokens.Black(105), Math.Max(1.0f, S(1))))
        {
            for (int i = 1; i < 10; i++)
            {
                float x = strip.X + strip.Width * i / 10.0f;
                g.DrawLine(tick, x, strip.Y + S(1), x, strip.Bottom - S(1));
            }
        }
    }

    private static string ResolvePowerModeForDisplay(PowerStripSnapshot power)
    {
        if (power == null || string.IsNullOrWhiteSpace(power.PowerModeText))
        {
            return "--";
        }

        string text = power.PowerModeText.Trim();
        if (power.EnergySaverActive)
        {
            const string energySaverSuffix = "（节能）";
            if (text.EndsWith(energySaverSuffix, StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - energySaverSuffix.Length).Trim();
            }

            // The snapshot uses "节能" when the base Windows power mode is unknown.
            // Keep the two independent states honest instead of presenting that fallback as the base mode.
            if (string.Equals(text, "节能", StringComparison.Ordinal))
            {
                return "--";
            }
        }

        return string.IsNullOrEmpty(text) ? "--" : text;
    }

    private static PowerModeVisual ResolvePowerModeVisual(PowerStripSnapshot power)
    {
        string text = ResolvePowerModeForDisplay(power);
        if (string.IsNullOrEmpty(text) || string.Equals(text, "--", StringComparison.Ordinal))
        {
            return PowerModeVisual.Unknown;
        }

        if (text.IndexOf("性能", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("performance", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return PowerModeVisual.Performance;
        }

        if (text.IndexOf("省电", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("节能", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("efficiency", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return PowerModeVisual.Saver;
        }

        if (text.IndexOf("平衡", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("推荐", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("balanced", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return PowerModeVisual.Balanced;
        }

        return PowerModeVisual.Unknown;
    }

    private static Color ResolvePowerModeColor(PowerModeVisual mode)
    {
        switch (mode)
        {
            case PowerModeVisual.Saver:
                return DesignTokens.Colors.Success;
            case PowerModeVisual.Performance:
                return DesignTokens.Colors.DangerStrong;
            default:
                return DesignTokens.Colors.Warning;
        }
    }

    private static int ResolvePowerBatteryPercent(PowerStripSnapshot power)
    {
        if (power != null && power.BatteryPercentKnown)
        {
            return Math.Max(0, Math.Min(100, power.BatteryPercent));
        }

        return -1;
    }

    private static bool TryResolveLivePowerWatts(PowerStripSnapshot power, out double watts)
    {
        watts = 0.0;
        if (power == null ||
            !power.WattsKnown ||
            double.IsNaN(power.Watts) ||
            double.IsInfinity(power.Watts) ||
            power.Watts < 0.0)
        {
            return false;
        }

        watts = power.Watts;
        return true;
    }

    private static double ResolvePowerWattsPeak(SystemDayBoardSnapshot day)
    {
        double peak = 0.0;
        if (day == null || day.Points == null)
        {
            return peak;
        }

        for (int i = 0; i < day.Points.Count; i++)
        {
            SystemDayBoardPoint point = day.Points[i];
            if (point != null && point.WattsKnown)
            {
                peak = Math.Max(peak, point.Watts);
            }
        }

        return peak;
    }

    private static PowerForecastPresentation ResolvePowerForecast(
        PowerStripSnapshot power,
        SystemDayBoardSnapshot day)
    {
        int battery = ResolvePowerBatteryPercent(power);
        if (battery < 0)
        {
            return new PowerForecastPresentation(
                "--",
                "电量",
                "续航预测尚不可用",
                PowerForecastTone.Muted,
                false);
        }

        bool charging = power != null && power.Charging;
        bool pluggedIn = power != null && power.PluggedIn;
        if (charging)
        {
            int target = day != null && day.BatteryEtaTargetPercent > battery
                ? day.BatteryEtaTargetPercent
                : battery < 80 ? 80 : 100;
            if (day != null && day.BatteryEtaKnown && day.BatteryEtaMinutes > 0)
            {
                return new PowerForecastPresentation(
                    FormatPowerDuration(day.BatteryEtaMinutes),
                    "到" + target.ToString(CultureInfo.InvariantCulture) + "%",
                    "按近 3h 电量趋势",
                    PowerForecastTone.Charge,
                    true);
            }

            return new PowerForecastPresentation(
                "充电",
                "到" + target.ToString(CultureInfo.InvariantCulture) + "%",
                "等待充电趋势",
                PowerForecastTone.Charge,
                true);
        }

        if (pluggedIn)
        {
            return new PowerForecastPresentation(
                "AC",
                power != null && power.BatteryCarePauseActive ? "保养" : "供电",
                "当前无需续航估算",
                PowerForecastTone.Accent,
                true);
        }

        if (power != null && power.RuntimeSecondsKnown && power.RuntimeSeconds > 0)
        {
            int minutes = Math.Max(1, (int)Math.Round(power.RuntimeSeconds / 60.0));
            return new PowerForecastPresentation(
                FormatPowerDuration(minutes),
                "耗尽",
                "Windows 当前状态",
                battery <= 20 ? PowerForecastTone.Danger : PowerForecastTone.Accent,
                true);
        }

        if (day != null &&
            day.BatteryEtaKnown &&
            day.BatteryEtaMinutes > 0 &&
            day.BatteryEtaTargetPercent <= 0)
        {
            return new PowerForecastPresentation(
                FormatPowerDuration(day.BatteryEtaMinutes),
                "耗尽",
                "按近 3h 电量趋势",
                battery <= 20 ? PowerForecastTone.Danger : PowerForecastTone.Accent,
                true);
        }

        return new PowerForecastPresentation(
            "--",
            "估算",
            "保持使用即可建立趋势",
            PowerForecastTone.Muted,
            false);
    }

    private static string FormatPowerDuration(int totalMinutes)
    {
        int minutes = Math.Max(1, totalMinutes);
        int hours = minutes / 60;
        if (hours >= 24)
        {
            int days = hours / 24;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}d{1}h",
                days,
                hours % 24);
        }

        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}h{1:00}", hours, minutes % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0}m", minutes);
    }

    private static Color ResolvePowerForecastColor(PowerForecastTone tone, Color accent)
    {
        switch (tone)
        {
            case PowerForecastTone.Charge:
                return DesignTokens.Colors.DangerStrong;
            case PowerForecastTone.Danger:
                return DesignTokens.Colors.Danger;
            case PowerForecastTone.Muted:
                return DesignTokens.Colors.GlyphMuted;
            default:
                return accent;
        }
    }

    private enum PowerForecastTone
    {
        Accent,
        Charge,
        Danger,
        Muted
    }

    private enum PowerModeVisual
    {
        Unknown,
        Saver,
        Balanced,
        Performance
    }

    private sealed class PowerForecastPresentation
    {
        public PowerForecastPresentation(
            string main,
            string status,
            string source,
            PowerForecastTone tone,
            bool known)
        {
            this.Main = main;
            this.Status = status;
            this.Source = source;
            this.Tone = tone;
            this.Known = known;
        }

        public string Main { get; private set; }
        public string Status { get; private set; }
        public string Source { get; private set; }
        public PowerForecastTone Tone { get; private set; }
        public bool Known { get; private set; }
    }

    // ── Radar: quota ─────────────────────────────────────────────────────
    // The full-bleed chart is the instrument: accepted weekly readings occupy its first 68%, leaving
    // a real future lane between "now" and the reset marker. The forecast headline translates that
    // geometry into the answer people need: when it runs out, or whether it survives the reset.
    private void DrawDeepSeekBalance(Graphics g, RectangleF content, Color accent)
    {
        DeepSeekBalanceSnapshot d = this.feed.GetDeepSeekBalance();
        DeepSeekServiceSnapshot service = this.feed.GetDeepSeekService();
        Color chartColor = MetricTileForm.ResolveBurnInRingColor(
            accent,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        Color usageColor = MetricTileForm.ResolveBurnInRingColor(
            DesignTokens.Colors.Warning,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        List<double> balances = new List<double>();
        if (d.History != null)
        {
            for (int i = 0; i < d.History.Count; i++)
            {
                DeepSeekBalancePoint point = d.History[i];
                if (point != null && string.Equals(point.Currency, d.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    balances.Add(Math.Max(0.0, point.Balance));
                }
            }
        }

        RectangleF ground = GroundRect(content, true);
        double max = Math.Max(d.ReferenceBalance, d.Balance);
        DrawSpark(g, ground, balances, chartColor, max <= 0.0 ? 1.0 : max, true, false);
        DrawCaption(g, content, "DEEPSEEK · 48h 余额");

        string balance = d.Known ? d.Balance.ToString("0.##", CultureInfo.InvariantCulture) : "--";
        string currency = string.IsNullOrWhiteSpace(d.Currency) ? "CNY" : d.Currency;
        using (Font labelFont = new Font(DesignTokens.UiFontFamily, S(LabelSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font valueFont = new Font(DesignTokens.UiFontFamily, S(ValueSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font suffixFont = new Font(DesignTokens.UiFontFamily, S(SuffixSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(chartColor, 230)))
        using (SolidBrush valueBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, 225)))
        using (SolidBrush suffixBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 200)))
        {
            g.DrawString("DS", labelFont, labelBrush, content.X, content.Y + S(9));
            float labelWidth = g.MeasureString("DS", labelFont).Width;
            if (ShouldDrawNeutralText(this.burnInVisualLevel))
            {
                float valueX = content.X + labelWidth + S(8);
                g.DrawString(balance, valueFont, valueBrush, valueX, content.Y);
                float valueWidth = g.MeasureString(balance, valueFont).Width;
                g.DrawString(currency, suffixFont, suffixBrush, valueX + valueWidth + S(2), content.Y + S(14));
            }
        }

        string usage = d.Last24HourUsageKnown
            ? "24h 消耗 " + d.Last24HourUsage.ToString("0.##", CultureInfo.InvariantCulture) + " " + currency
            : "24h 消耗采样中";
        using (Font usageFont = new Font(DesignTokens.UiFontFamily, S(14), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush usageBrush = new SolidBrush(DesignTokens.WithAlpha(usageColor, 245)))
        {
            g.DrawString(usage, usageFont, usageBrush, content.X, content.Y + S(45));
        }

        if (ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            string runway = d.RunwayKnown
                ? "按近 24h 趋势约 " + FormatForecastHours(d.RunwayHours) + " 可用"
                : (d.ApiKeyConfigured ? "等待余额下降后估算可用时长" : "未配置 DeepSeek API Key");
            string status = d.RequestRunning
                ? "正在刷新"
                : (!string.IsNullOrEmpty(d.ErrorCode)
                    ? (d.ErrorMessage ?? "刷新失败，保留上次余额")
                    : (d.Known ? (d.IsAvailable ? "账户可用" : "账户暂不可用") : "等待数据"));
            if (service.Known)
            {
                status += service.IsAvailable ? " · API 正常" : " · API 服务异常";
            }
            DrawRightAlignedText(g, runway, content, content.Y + S(26), S(13),
                DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, 225), FontStyle.Bold);
            DrawRightAlignedText(g, status, content, content.Y + S(48), S(11),
                DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 215), FontStyle.Regular);
            if (d.CheckedAtLocal != DateTime.MinValue)
            {
                DrawRightAlignedText(g, "更新 " + d.CheckedAtLocal.ToString("HH:mm", CultureInfo.CurrentCulture),
                    content, content.Y + S(65), S(10),
                    DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 205), FontStyle.Regular);
            }
        }

        RectangleF strip = StripRect(content);
        using (GraphicsPath track = RoundedRectangle(strip, Math.Max(1.0f, strip.Height / 2.0f)))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(28)))
        {
            g.FillPath(trackBrush, track);
            double denominator = d.Balance + d.Last24HourUsage;
            float width = d.Last24HourUsageKnown && denominator > 0.0001
                ? (float)(strip.Width * MetricTileModel.Clamp(d.Last24HourUsage / denominator, 0.0, 1.0))
                : 0.0f;
            if (width > 0.0f)
            {
                Region previous = g.Clip;
                g.SetClip(track, CombineMode.Intersect);
                using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(usageColor, 225)))
                {
                    g.FillRectangle(fill, strip.X, strip.Y, width, strip.Height);
                }

                g.Clip = previous;
            }
        }
    }

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

    internal void PrepareForRenderSample(MetricTileId id, MetricTileFeed sampleFeed, bool showRevival = false)
    {
        this.metricId = id;
        this.feed = sampleFeed ?? new MetricTileFeed();
        this.quotaRevivalVisible = showRevival;
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

        PowerStripSnapshot discharging = new PowerStripSnapshot
        {
            BatteryPercentKnown = true,
            BatteryPercent = 79,
            RuntimeSecondsKnown = true,
            RuntimeSeconds = 3 * 3600 + 42 * 60
        };
        PowerForecastPresentation dischargeForecast = ResolvePowerForecast(discharging, null);
        if (!dischargeForecast.Known || dischargeForecast.Main.IndexOf("3h42", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("PWR must prefer the current Windows battery runtime.");
        }

        PowerStripSnapshot charging = new PowerStripSnapshot
        {
            BatteryPercentKnown = true,
            BatteryPercent = 62,
            Charging = true,
            PluggedIn = true
        };
        SystemDayBoardSnapshot chargingDay = SystemDayBoardSnapshot.CreateEmpty(SystemDayRange.Last24Hours, DateTime.Now);
        chargingDay.BatteryEtaKnown = true;
        chargingDay.BatteryEtaMinutes = 55;
        chargingDay.BatteryEtaTargetPercent = 80;
        PowerForecastPresentation chargeForecast = ResolvePowerForecast(charging, chargingDay);
        if (!chargeForecast.Known ||
            chargeForecast.Main.IndexOf("55m", StringComparison.Ordinal) < 0 ||
            chargeForecast.Status.IndexOf("80%", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("PWR charging forecast must show time and target.");
        }

        PowerStripSnapshot plugged = new PowerStripSnapshot
        {
            BatteryPercentKnown = true,
            BatteryPercent = 80,
            PluggedIn = true,
            WattsKnown = true,
            Watts = 0.0
        };
        if (ResolvePowerForecast(plugged, null).Main != "AC")
        {
            throw new InvalidOperationException("PWR must distinguish external power from battery runway.");
        }

        double idleWatts;
        if (!TryResolveLivePowerWatts(plugged, out idleWatts) || Math.Abs(idleWatts) > 0.0001)
        {
            throw new InvalidOperationException("PWR must preserve a live BatteryStatus idle value as known 0 W.");
        }

        PowerStripSnapshot unknownWatts = new PowerStripSnapshot
        {
            WattsKnown = false,
            Watts = 17.5
        };
        double ignoredWatts;
        if (TryResolveLivePowerWatts(unknownWatts, out ignoredWatts))
        {
            throw new InvalidOperationException("PWR must not present a non-live or unknown watt value as current.");
        }

        PowerStripSnapshot energySaver = new PowerStripSnapshot
        {
            EnergySaverActive = true,
            PowerModeText = "性能（节能）"
        };
        if (!string.Equals(ResolvePowerModeForDisplay(energySaver), "性能", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PWR must present the base power mode separately from energy saver.");
        }

        energySaver.PowerModeText = "节能";
        if (!string.Equals(ResolvePowerModeForDisplay(energySaver), "--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PWR must not present the energy-saver fallback as a known base mode.");
        }

        energySaver.PowerModeText = "平衡（节能）";
        if (ResolvePowerModeVisual(energySaver) != PowerModeVisual.Balanced)
        {
            throw new InvalidOperationException("PWR graphical rail must highlight the base mode independently from energy saver.");
        }

        energySaver.PowerModeText = "最佳性能";
        energySaver.EnergySaverActive = false;
        if (ResolvePowerModeVisual(energySaver) != PowerModeVisual.Performance)
        {
            throw new InvalidOperationException("PWR graphical rail must recognize the Windows performance mode.");
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

        Console.WriteLine("Metric tile expand: PASS Radar-module size, placement, large mode, level-two neutral-text suppression, PWR graphical runway states");
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
