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
    private RectangleF batteryCareHitBounds;
    // Where the last-24-hour watts peak landed on the curve, so the badge annotates the data point
    // instead of floating at a hard-coded x in the middle of the panel.
    private PointF powerPeakAnchor;
    private bool powerPeakAnchorKnown;
    private bool batteryCareRequestPending;
    private string batteryCareNotice = string.Empty;
    internal Action<bool, Action<bool, string>> BatteryCareRequest;

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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || this.metricId != MetricTileId.Power ||
            this.displaySuspended || this.batteryCareRequestPending ||
            !this.batteryCareHitBounds.Contains(e.Location) || this.BatteryCareRequest == null)
        {
            return;
        }

        bool pause = this.feed.Power == null || !this.feed.Power.BatteryCarePauseActive;
        this.batteryCareRequestPending = true;
        this.batteryCareNotice = string.Empty;
        InvalidateLayeredRenderBuffer();
        RenderLayeredWindow();
        this.BatteryCareRequest(pause, delegate(bool success, string detail)
        {
            if (this.IsDisposed) return;
            this.batteryCareRequestPending = false;
            this.batteryCareNotice = success ? string.Empty : "指令失败 · 请重试";
            InvalidateLayeredRenderBuffer();
            if (this.Visible) RenderLayeredWindow();
        });
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
        return GroundRect(content, hasStrip, false);
    }

    // hasFooter pulls the chart's lower edge above the footer band so no series runs behind it.
    private RectangleF GroundRect(RectangleF content, bool hasStrip, bool hasFooter)
    {
        float bottom = hasStrip ? content.Bottom - S(13) : content.Bottom;
        if (hasFooter)
        {
            bottom = FooterRect(content, hasStrip).Y - S(3);
        }

        float top = content.Y + content.Height * 0.16f;
        return new RectangleF(content.X, top, content.Width, Math.Max(1.0f, bottom - top));
    }

    private RectangleF StripRect(RectangleF content)
    {
        return new RectangleF(content.X, content.Bottom - S(9), content.Width, S(9));
    }

    // -- Footer band ------------------------------------------------------
    // One secondary row shared by every chart panel (2.0.0.35). Legends, secondary numbers and the
    // single interactive control live here instead of floating over the curve, so the chart band
    // above stays clear of text and all the panels read as one family.
    //
    // Segment widths are measured from the actual fonts, never from guessed constants: rendered
    // metrics on the user's machine are routinely wider than assumed and segments would silently
    // collide. Segments flow left to right with a hairline divider between them, and a panel may
    // reserve the right end of the band for its own control via rightLimit.
    private const int FooterBandHeight = 24;
    private const int FooterValueSize = 12;
    private const int FooterKeySize = 11;
    private const int FooterSwatchSize = 8;
    private const int FooterSegmentPadding = 9;

    private sealed class FooterSegment
    {
        internal string Key;
        internal string Value;
        internal string Text;
        internal Color ValueColor;
        internal bool ValueColorSet;
        internal Color SwatchColor;
        internal bool HasSwatch;
        internal bool Tight;

        internal static FooterSegment Make(string key, string value, string text)
        {
            FooterSegment segment = new FooterSegment();
            segment.Key = key;
            segment.Value = value;
            segment.Text = text;
            return segment;
        }

        internal FooterSegment WithValueColor(Color color)
        {
            this.ValueColor = color;
            this.ValueColorSet = true;
            return this;
        }

        internal FooterSegment WithSwatch(Color color)
        {
            this.SwatchColor = color;
            this.HasSwatch = true;
            return this;
        }

        // Legend entries belong to one another; a divider between them would read as two topics.
        internal FooterSegment WithTight()
        {
            this.Tight = true;
            return this;
        }
    }

    private RectangleF FooterRect(RectangleF content, bool hasStrip)
    {
        float bottom = hasStrip ? content.Bottom - S(13) : content.Bottom - S(2);
        return new RectangleF(content.X, bottom - S(FooterBandHeight), content.Width, S(FooterBandHeight));
    }

    private void DrawFooterBand(Graphics g, RectangleF content, bool hasStrip, FooterSegment[] segments)
    {
        DrawFooterBand(g, content, hasStrip, segments, 0.0f);
    }

    private void DrawFooterBand(Graphics g, RectangleF content, bool hasStrip, FooterSegment[] segments, float rightLimit)
    {
        DrawFooterBand(g, content, hasStrip, segments, rightLimit, 0.0f);
    }

    private void DrawFooterBand(Graphics g, RectangleF content, bool hasStrip, FooterSegment[] segments,
        float rightLimit, float leftStart)
    {
        if (segments == null || segments.Length == 0)
        {
            return;
        }

        RectangleF band = FooterRect(content, hasStrip);
        float limit = rightLimit > 0.0f ? rightLimit : band.Right;
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        float x = leftStart > band.X ? leftStart : band.X;
        using (Font keyFont = new Font("Segoe UI", S(FooterKeySize), FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font valueFont = new Font("Segoe UI", S(FooterValueSize), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            for (int i = 0; i < segments.Length; i++)
            {
                FooterSegment segment = segments[i];
                if (segment == null)
                {
                    continue;
                }

                float width = LayoutFooterSegment(g, segment, band, 0.0f, keyFont, valueFont, drawNeutral, false);
                if (width <= 0.0f)
                {
                    continue;
                }

                bool tight = segment.Tight && x > band.X;
                float leading = x > band.X
                    ? (tight ? S(FooterSegmentPadding) : S(FooterSegmentPadding) * 2.0f + 1.0f)
                    : 0.0f;
                // Drop a whole segment rather than clipping it: a half-drawn number reads as a wrong
                // number, and the user can scale this panel down.
                if (x + leading + width > limit)
                {
                    break;
                }

                if (leading > 0.0f)
                {
                    if (!tight)
                    {
                        float dividerX = x + S(FooterSegmentPadding);
                        using (Pen divider = new Pen(DesignTokens.White(28), 1.0f))
                        {
                            g.DrawLine(divider, dividerX, band.Y + S(5), dividerX, band.Bottom - S(5));
                        }
                    }

                    x += leading;
                }

                x = LayoutFooterSegment(g, segment, band, x, keyFont, valueFont, drawNeutral, true);
            }
        }
    }

    // Measures when draw is false and paints when it is true, so the two can never disagree.
    private float LayoutFooterSegment(Graphics g, FooterSegment segment, RectangleF band, float x,
        Font keyFont, Font valueFont, bool drawNeutral, bool draw)
    {
        float cursor = x;
        float centerY = band.Y + band.Height / 2.0f;
        if (segment.HasSwatch)
        {
            float size = S(FooterSwatchSize);
            if (draw)
            {
                RectangleF chipRect = new RectangleF(cursor, centerY - size / 2.0f, size, size);
                using (GraphicsPath chip = RoundedRectangle(chipRect, Math.Max(1.0f, S(2))))
                using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(segment.SwatchColor, 235)))
                {
                    g.FillPath(brush, chip);
                }
            }

            cursor += size + S(5);
        }

        // Burn-in level two hides neutral text across the whole panel; the swatches still carry the
        // legend, so the band degrades to colour instead of vanishing.
        if (!drawNeutral)
        {
            return draw ? cursor : cursor - x;
        }

        Color keyColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 215);
        Color valueColor = segment.ValueColorSet
            ? segment.ValueColor
            : DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 240);
        cursor = DrawFooterText(g, segment.Key, keyFont, keyColor, band, cursor, draw, S(5));
        cursor = DrawFooterText(g, segment.Value, valueFont, valueColor, band, cursor, draw, S(4));
        cursor = DrawFooterText(g, segment.Text, keyFont, keyColor, band, cursor, draw, 0.0f);
        return draw ? cursor : cursor - x;
    }

    private static float DrawFooterText(Graphics g, string text, Font font, Color color, RectangleF band,
        float x, bool draw, float trailingGap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return x;
        }

        SizeF size = g.MeasureString(text, font);
        if (draw)
        {
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, new RectangleF(x, band.Y, size.Width + 2.0f, band.Height), format);
            }
        }

        return x + size.Width + trailingGap;
    }

    // -- Conclusion slot --------------------------------------------------
    // The right-hand answer, in the same place on every panel: exactly one conclusion, the big line
    // is the value and the small line names what it is. It starts below the caption row so the two
    // right-aligned blocks cannot collide.
    private const int ConclusionMainSize = 24;
    private const int ConclusionStatusSize = 12;

    private RectangleF ConclusionRect(RectangleF content)
    {
        float width = Math.Min(content.Width * 0.40f, S(196));
        return new RectangleF(content.Right - width, content.Y + S(19), width, S(46));
    }

    private void DrawConclusion(Graphics g, RectangleF content, Color color, string main, string status)
    {
        if (!ShouldDrawNeutralText(this.burnInVisualLevel) || string.IsNullOrEmpty(main))
        {
            return;
        }

        RectangleF rect = ConclusionRect(content);
        // The same light veil the header floats on. The conclusion sits over the chart, and its
        // small status line was losing against the curve running behind it.
        using (Font mainFont = new Font("Segoe UI", S(ConclusionMainSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font statusFont = new Font("Segoe UI", S(ConclusionStatusSize), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float veilWidth = Math.Max(
                g.MeasureString(main, mainFont).Width,
                string.IsNullOrEmpty(status) ? 0.0f : g.MeasureString(status, statusFont).Width);
            veilWidth = Math.Min(veilWidth + S(6), rect.Width + S(8));
            using (SolidBrush veil = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 60)))
            {
                g.FillRectangle(veil, rect.Right - veilWidth + S(4), rect.Y - S(3), veilWidth, S(48));
            }
        }

        DrawRightAlignedText(g, main, rect, rect.Y, S(ConclusionMainSize),
            DesignTokens.WithAlpha(color, 250), FontStyle.Bold);
        DrawRightAlignedText(g, status, rect, rect.Y + S(28), S(ConclusionStatusSize),
            DesignTokens.WithAlpha(color, 228), FontStyle.Bold);
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

        // No numeric label: the top-right caption and the conclusion slot both live at that edge,
        // and the headline value already states the reading the label duplicated.
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
        RectangleF ground = GroundRect(content, false, true);
        DrawCoreBars(g, ground, cores, accent);
        DrawSpark(g, ground, this.feed.CpuHistory, accent, 100.0, true, true);

        DrawFloatingHeader(g, content, accent, "CPU",
            Math.Round(s.CpuPercent).ToString("0", CultureInfo.InvariantCulture), "%", null);
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");

        // The hottest core is this panel's one conclusion: the average curve stays calm while a
        // single maxed core is what actually stalls a build.
        double peakCore = PeakOf(cores);
        Color peakColor = peakCore >= CoreLoadDangerPercent
            ? DesignTokens.Colors.DangerStrong
            : (peakCore >= CoreLoadWarningPercent ? DesignTokens.Colors.Warning : accent);
        DrawConclusion(g, content, peakColor,
            peakCore.ToString("0", CultureInfo.InvariantCulture) + "%", "峰值核心");
        DrawFooterBand(g, content, false, new FooterSegment[]
        {
            FooterSegment.Make("频率", s.CpuFrequencyGhz.ToString("0.00", CultureInfo.InvariantCulture),
                "/ " + s.CpuBaseFrequencyGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz"),
            FooterSegment.Make("核心", cores.Length.ToString(CultureInfo.InvariantCulture), null),
            FooterSegment.Make(null, null, "每核占用").WithSwatch(DesignTokens.WithAlpha(accent, 170)),
            FooterSegment.Make(null, null, "满载核").WithSwatch(DesignTokens.Colors.DangerStrong).WithTight()
        });
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
        RectangleF ground = GroundRect(content, true, true);
        DrawSpark(g, ground, this.feed.MemoryHistory, accent, 100.0, true, true);
        DrawSparkLineOnly(g, ground, this.feed.MemoryHardwareReservedHistory,
            DesignTokens.Colors.Warning, 100.0, 225, true);
        DrawMemoryPressureHistory(g, StripRect(content), this.feed.MemoryPressureHistory, s);

        DrawFloatingHeader(g, content, accent, "MEM",
            Math.Round(s.MemoryPercent).ToString("0", CultureInfo.InvariantCulture), "%",
            string.Format(CultureInfo.InvariantCulture,
                "{0:0.0}/{1:0.0} GB · 可用 {2:0.0}",
                s.MemoryUsedGb,
                s.MemoryTotalGb,
                availableGb));
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");

        // Pressure moves off the coloured history strip and into the conclusion slot: text over the
        // green/yellow/red band was unreadable at every level of that band.
        Color pressureColor = MetricTileModel.GetMemoryPressureColor(s.MemoryPressureLevel);
        DrawConclusion(g, content, pressureColor,
            MetricTileModel.GetMemoryPressureLabel(s.MemoryPressureLevel), "内存压力");

        Color commitColor = s.MemoryCommitPercent >= 98.0
            ? DesignTokens.Colors.DangerStrong
            : (s.MemoryCommitPercent >= 80.0 ? DesignTokens.Colors.Warning : DesignTokens.Colors.TextMuted);
        FooterSegment commit = s.MemoryCommitLimitGb > 0.0
            ? FooterSegment.Make("提交",
                s.MemoryCommitPercent.ToString("0", CultureInfo.InvariantCulture) + "%",
                s.MemoryCommitPercent >= 80.0 ? "偏高" : null).WithValueColor(
                    DesignTokens.WithAlpha(commitColor, 240))
            : FooterSegment.Make("提交", "--", null);
        DrawFooterBand(g, content, true, new FooterSegment[]
        {
            commit,
            FooterSegment.Make("换出", FormatPageOutRate(s.MemoryPageOutMegabytesPerSecond) + "/s", null),
            FooterSegment.Make("GPU/NPU", reservedGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB", null)
        });
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
        RectangleF ground = GroundRect(content, true, true);
        // Write and read share one auto-scaled axis so their relative magnitude stays honest; the
        // read line usually hugs the floor because Windows serves most reads from cache.
        double max = Math.Max(PeakOf(this.feed.DiskWriteHistory), PeakOf(this.feed.DiskReadHistory)) * 1.15;
        DrawSpark(g, ground, this.feed.DiskWriteHistory, DesignTokens.Colors.Warning, max, true, false);
        DrawSparkLineOnly(g, ground, this.feed.DiskReadHistory,
            DesignTokens.Colors.Success, max, 225, true);

        double capacityPercent = s.DiskTotalGb > 0.0 ? s.DiskUsedGb / s.DiskTotalGb * 100.0 : 0.0;
        DrawSegmentBar(g, StripRect(content), new double[] { capacityPercent }, new Color[] { accent }, new int[] { 215 });

        DrawFloatingHeader(g, content, accent, "DISK",
            Math.Round(s.DiskPercent).ToString("0", CultureInfo.InvariantCulture), "%", null);
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");

        // Free space is the question a disk panel actually answers; the right half used to be empty.
        double freeGb = Math.Max(0.0, s.DiskTotalGb - s.DiskUsedGb);
        DrawConclusion(g, content, accent, freeGb.ToString("0", CultureInfo.InvariantCulture), "GB 可用");
        DrawFooterBand(g, content, true, new FooterSegment[]
        {
            FooterSegment.Make("写", NetworkRateFormatter.FormatStorage(s.DiskWriteBytesPerSecond), null)
                .WithSwatch(DesignTokens.Colors.Warning),
            FooterSegment.Make("读", NetworkRateFormatter.FormatStorage(s.DiskReadBytesPerSecond), null)
                .WithSwatch(DesignTokens.Colors.Success),
            FooterSegment.Make("容量",
                string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:0} GB", s.DiskUsedGb, s.DiskTotalGb), null)
        });
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
            GroundRect(content, false, true),
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

        // NET was the only panel with no headline number, leaving its top-left blank. Downstream
        // throughput is what the tile is watched for, so it takes the headline; the footer keeps
        // both directions explicit with the same colours the mirror chart uses.
        string downValue;
        string downUnit;
        SplitRate(s.NetworkReceivedBytesPerSecond, out downValue, out downUnit);
        DrawFloatingHeader(g, content, accent, "NET", downValue, "↓ " + downUnit, null);
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");

        Color signalColor = s.NetworkConnected ? accent : DesignTokens.Colors.DangerStrong;
        DrawConclusion(g, content, signalColor,
            s.NetworkRssiKnown ? s.NetworkRssiDbm.ToString(CultureInfo.InvariantCulture) : signal,
            s.NetworkRssiKnown ? "dBm 信号" : "链路状态");
        DrawFooterBand(g, content, false, new FooterSegment[]
        {
            FooterSegment.Make("下行", FormatRate(s.NetworkReceivedBytesPerSecond), null)
                .WithSwatch(DesignTokens.Colors.AccentSoft),
            FooterSegment.Make("上行", FormatRate(s.NetworkSentBytesPerSecond), null)
                .WithSwatch(DesignTokens.Colors.Danger),
            FooterSegment.Make("链路", link, null)
        });
    }

    // The header needs the number and its unit separately; the shared rate formatter returns them
    // as one string, so split on the last space instead of duplicating the unit ladder here.
    private static void SplitRate(double bytesPerSecond, out string value, out string unit)
    {
        string text = FormatRate(bytesPerSecond);
        int space = text.LastIndexOf(' ');
        if (space <= 0)
        {
            value = text;
            unit = string.Empty;
            return;
        }

        value = text.Substring(0, space);
        unit = text.Substring(space + 1);
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
            s.GpuMemoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%", "显存占用",
            new FooterSegment[]
            {
                FooterSegment.Make(null, null, "占用").WithSwatch(accent),
                FooterSegment.Make(null, null, "显存")
                    .WithSwatch(DesignTokens.WithAlpha(accent, 115)).WithTight(),
                FooterSegment.Make("VRAM", string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0} / {1:0.0} GB", s.GpuMemoryUsedGb, s.GpuMemoryTotalGb), null),
                FooterSegment.Make("底条", null, "显存占比")
            });
    }

    private void DrawNpu(Graphics g, RectangleF content, Color accent, PerfSnapshot s)
    {
        // The NPU idles at 0% almost all the time, which left the panel visually empty; the idle
        // verdict fills the conclusion slot instead of hiding at the end of a sub line.
        bool npuIdle = s.NpuPercent <= 0.5;
        DrawAcceleratorPanel(g, content, accent, "NPU", s.NpuPercent, s.NpuMemoryPercent,
            this.feed.NpuHistory, this.feed.NpuMemoryHistory,
            npuIdle ? "空闲" : "推理中",
            npuIdle ? "近 60 秒无负载" : "当前推理",
            new FooterSegment[]
            {
                FooterSegment.Make(null, null, "占用").WithSwatch(accent),
                FooterSegment.Make(null, null, "内存")
                    .WithSwatch(DesignTokens.WithAlpha(accent, 115)).WithTight(),
                FooterSegment.Make("内存", string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0} / {1:0.0} GB", s.NpuMemoryUsedGb, s.NpuMemoryTotalGb), null)
            });
    }

    private void DrawAcceleratorPanel(Graphics g, RectangleF content, Color accent, string label,
        double percent, double memoryPercent, List<double> load, List<double> memory,
        string conclusionMain, string conclusionStatus, FooterSegment[] footer)
    {
        RectangleF ground = GroundRect(content, true, true);
        DrawSpark(g, ground, load, accent, 100.0, true, true);
        DrawSparkLineOnly(g, ground, memory, accent, 100.0, 115, false);
        DrawSegmentBar(g, StripRect(content), new double[] { memoryPercent }, new Color[] { accent }, new int[] { 200 });

        DrawFloatingHeader(g, content, accent, label,
            Math.Round(MetricTileModel.Clamp(percent, 0.0, 100.0)).ToString("0", CultureInfo.InvariantCulture), "%",
            null);
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)), "60 秒");
        DrawConclusion(g, content, accent, conclusionMain, conclusionStatus);
        DrawFooterBand(g, content, true, footer);
    }

    // ── Power ────────────────────────────────────────────────────────────
    // PWR reuses the cache-only last-24-hour projection already owned by System Day. The hover
    // panel must never start sampling or file I/O: its job is to translate battery, watts and the
    // existing ETA into the same identity/chart/forecast hierarchy as the quota panels.
    private void DrawPower(Graphics g, RectangleF content, Color accent)
    {
        PowerStripSnapshot p = this.feed.Power;
        SystemDayBoardSnapshot day = this.feed.PowerDay;
        DrawPowerHistory(g, GroundRect(content, true, true), p, day);
        DrawRadarQuotaScrims(g, content);
        DrawPowerIdentity(g, content, accent, p);
        DrawPowerForecast(g, content, accent, p, day);
        DrawPowerPeakBadge(g, content, day);
        // The care chip owns the right end of the footer band and is the only thing on this panel
        // that reacts to a click, so the mode and saver indicators next to it are drawn flat.
        float careLeft = DrawBatteryCareControl(g, content, p);
        DrawPowerFooter(g, content, p, careLeft);
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
        this.powerPeakAnchorKnown = false;
        if (watts.Count >= 2 && wattsPeak > 0.0)
        {
            int peakIndex = 0;
            for (int i = 1; i < watts.Count; i++)
            {
                if (watts[i] > watts[peakIndex])
                {
                    peakIndex = i;
                }
            }

            // Same projection DrawSpark uses, so the marker cannot drift off the point it labels.
            float step = ground.Width / (watts.Count - 1);
            this.powerPeakAnchor = new PointF(
                ground.X + peakIndex * step,
                ResolvePlotY(ground, wattsPeak, Math.Max(1.0, wattsPeak)));
            this.powerPeakAnchorKnown = true;
        }

        DrawSpark(g, ground, watts, wattsColor, Math.Max(1.0, wattsPeak), true, false);
        DrawSparkLineOnly(g, ground, battery, batteryColor, 100.0, 205, true);
    }

    // Same headline shape as every other panel. The battery outline this replaces carried only the
    // percentage, which the ten-tick strip along the bottom edge already shows full width.
    private void DrawPowerIdentity(
        Graphics g,
        RectangleF content,
        Color accent,
        PowerStripSnapshot power)
    {
        Color dataAccent = MetricTileForm.ResolveBurnInRingColor(
            accent,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        int battery = ResolvePowerBatteryPercent(power);
        double watts;
        bool wattsKnown = TryResolveLivePowerWatts(power, out watts);
        DrawFloatingHeader(g, content, dataAccent, "PWR",
            battery >= 0 ? battery.ToString(CultureInfo.InvariantCulture) : "--",
            battery >= 0 ? "%" : null,
            wattsKnown ? watts.ToString("0.0", CultureInfo.InvariantCulture) + " W" : "-- W");
    }

    private void DrawPowerForecast(
        Graphics g,
        RectangleF content,
        Color accent,
        PowerStripSnapshot power,
        SystemDayBoardSnapshot day)
    {
        PowerForecastPresentation forecast = ResolvePowerForecast(power, day);
        if (!forecast.Known && !ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            return;
        }

        Color stateColor = MetricTileForm.ResolveBurnInRingColor(
            ResolvePowerForecastColor(forecast.Tone, accent),
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        // The qualifier moves to the caption slot so the conclusion itself stays a single answer.
        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)),
            power != null && power.Charging ? "按当前充电功率" : "按近 24h 趋势");
        DrawConclusion(g, content, stateColor, forecast.Main, forecast.Status);
    }

    // Anchored to the peak sample and clamped inside the content box, so it reads as a chart
    // annotation rather than a fourth line of text in the middle of the panel.
    private void DrawPowerPeakBadge(Graphics g, RectangleF content, SystemDayBoardSnapshot day)
    {
        double peak = ResolvePowerWattsPeak(day);
        if (peak <= 0.0 || !this.powerPeakAnchorKnown || !ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            return;
        }

        Color color = MetricTileForm.ResolveBurnInRingColor(
            DesignTokens.Colors.Warning,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        string text = "▲ " + peak.ToString("0.0", CultureInfo.InvariantCulture) + " W";
        float size = S(10.5f);
        using (Font font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float width = g.MeasureString(text, font).Width;
            // The peak always resolves to the top of its own scale, so the label goes just below the
            // point: placing it above would drop it straight into the caption row.
            float y = Math.Max(this.powerPeakAnchor.Y + S(2), content.Y + S(CaptionSize) + S(3));
            RectangleF conclusion = ConclusionRect(content);
            float rightBound = y + size >= conclusion.Y && y <= conclusion.Bottom
                ? conclusion.X - S(6)
                : content.Right;
            float x = this.powerPeakAnchor.X - width / 2.0f;
            x = Math.Max(content.X, Math.Min(x, rightBound - width));
            using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, 240)))
            {
                g.DrawString(text, font, brush, x, y);
            }
        }
    }

    private void DrawPowerFooter(Graphics g, RectangleF content, PowerStripSnapshot power, float rightLimit)
    {
        RectangleF band = FooterRect(content, true);
        float x = DrawPowerModeIndicator(g, band, power);
        bool saverActive = power != null && power.EnergySaverActive;
        Color saverColor = saverActive
            ? MetricTileForm.ResolveBurnInRingColor(
                DesignTokens.Colors.Success, this.burnInVisualLevel, this.burnInPresentationRestored)
            : DesignTokens.Colors.GlyphMuted;
        DrawFooterBand(g, content, true, new FooterSegment[]
        {
            FooterSegment.Make("省电", saverActive ? "开" : "关", null)
                .WithValueColor(DesignTokens.WithAlpha(saverColor, 245))
                .WithSwatch(saverColor)
        }, rightLimit, x);
    }

    // Flat by design. The previous rail drew a rounded track, a filled selection block and a pill
    // switch, so two of the three things in this band looked pressable while only the battery-care
    // chip actually was; power mode and energy saver are read-only projections of Windows state.
    private float DrawPowerModeIndicator(Graphics g, RectangleF band, PowerStripSnapshot power)
    {
        PowerModeVisual active = ResolvePowerModeVisual(power);
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        float x = band.X;
        float centerY = band.Y + band.Height / 2.0f;
        using (Font keyFont = new Font("Segoe UI", S(FooterKeySize), FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font labelFont = new Font("Segoe UI", S(FooterValueSize), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            if (drawNeutral)
            {
                x = DrawFooterText(g, "档位", keyFont,
                    DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 215), band, x, true, S(6));
            }

            for (int i = 0; i < 3; i++)
            {
                PowerModeVisual mode = i == 0
                    ? PowerModeVisual.Saver
                    : (i == 1 ? PowerModeVisual.Balanced : PowerModeVisual.Performance);
                bool selected = mode == active;
                Color color = selected
                    ? MetricTileForm.ResolveBurnInRingColor(
                        ResolvePowerModeColor(mode), this.burnInVisualLevel, this.burnInPresentationRestored)
                    : DesignTokens.Colors.GlyphMuted;
                float glyph = S(12);
                DrawPowerModeGlyph(g, new RectangleF(x, centerY - glyph / 2.0f, glyph, glyph),
                    mode, DesignTokens.WithAlpha(color, selected ? 245 : 150));
                x += glyph + S(3);
                if (!drawNeutral)
                {
                    x += S(6);
                    continue;
                }

                string label = mode == PowerModeVisual.Saver
                    ? "省"
                    : (mode == PowerModeVisual.Balanced ? "衡" : "性");
                float labelStart = x;
                float trailing = i < 2 ? S(8) : S(2);
                x = DrawFooterText(g, label, labelFont,
                    DesignTokens.WithAlpha(color, selected ? 250 : 165), band, x, true, trailing);
                if (selected)
                {
                    using (Pen underline = new Pen(DesignTokens.WithAlpha(color, 175), Math.Max(1.0f, S(1.4f))))
                    {
                        g.DrawLine(underline, labelStart, band.Bottom - S(4), x - trailing, band.Bottom - S(4));
                    }
                }
            }
        }

        return x;
    }

    // The one interactive element on the panel. The whole chip is the hit target — roughly twice the
    // old two-line text block — and it is the only raised shape here, so "this one is pressable"
    // needs no explanation. Returns its left edge so the flat indicators stop before it.
    private float DrawBatteryCareControl(Graphics g, RectangleF content, PowerStripSnapshot power)
    {
        RectangleF band = FooterRect(content, true);
        bool paused = power != null && power.BatteryCarePauseActive;
        Color color = MetricTileForm.ResolveBurnInRingColor(
            paused ? DesignTokens.Colors.Warning : DesignTokens.Colors.Success,
            this.burnInVisualLevel,
            this.burnInPresentationRestored);
        bool drawNeutral = ShouldDrawNeutralText(this.burnInVisualLevel);
        string title = paused ? "已暂停" : "80%保护";
        string detail = this.batteryCareRequestPending
            ? "指令发送中…"
            : (!string.IsNullOrEmpty(this.batteryCareNotice)
                ? this.batteryCareNotice
                : (paused
                    ? FormatCompactCountdown(power.BatteryCarePauseUntilUtc - DateTime.UtcNow)
                    : "点击暂停 24h"));
        float pillWidth = S(26);
        float pillHeight = S(12);
        float padding = S(9);
        float width = padding * 2.0f + pillWidth;
        using (Font titleFont = new Font("Segoe UI", S(FooterValueSize), FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font detailFont = new Font("Segoe UI", S(FooterKeySize), FontStyle.Regular, GraphicsUnit.Pixel))
        {
            if (drawNeutral)
            {
                width += g.MeasureString(title, titleFont).Width + S(7)
                    + g.MeasureString(detail, detailFont).Width + S(7);
            }

            RectangleF chip = new RectangleF(band.Right - width, band.Y, width, band.Height);
            this.batteryCareHitBounds = chip;
            using (GraphicsPath path = RoundedRectangle(chip, Math.Max(2.0f, S(7))))
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, 26)))
            using (Pen border = new Pen(DesignTokens.WithAlpha(color, 120), Math.Max(1.0f, S(1))))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            float x = chip.X + padding;
            if (drawNeutral)
            {
                x = DrawFooterText(g, title, titleFont, DesignTokens.WithAlpha(color, 250), band, x, true, S(7));
            }

            DrawPowerSaverToggle(g,
                new RectangleF(x, band.Y + (band.Height - pillHeight) / 2.0f, pillWidth, pillHeight),
                !paused, color);
            x += pillWidth + S(7);
            if (drawNeutral)
            {
                DrawFooterText(g, detail, detailFont, DesignTokens.WithAlpha(color, 228), band, x, true, 0.0f);
            }

            return chip.X;
        }
    }

    // Minute resolution: this is a 24-hour window, and a per-second field was both visually noisy
    // and wide enough to force the old two-line block. GuardRuntime.FormatCountdown stays
    // second-level for the GUARD board, which is watched while it runs out.
    private static string FormatCompactCountdown(TimeSpan value)
    {
        int minutes = (int)Math.Round(Math.Max(0.0, value.TotalMinutes));
        int hours = minutes / 60;
        minutes = minutes % 60;
        return hours > 0
            ? hours.ToString(CultureInfo.InvariantCulture) + "h"
                + minutes.ToString("00", CultureInfo.InvariantCulture) + "m"
            : minutes.ToString(CultureInfo.InvariantCulture) + "m";
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
        int chargeTarget = power != null && power.BatteryCarePauseActive ? 100 : 80;
        if ((charging || pluggedIn) && battery >= chargeTarget)
        {
            return new PowerForecastPresentation("已到", chargeTarget + "%", "已达到当前充电上限",
                PowerForecastTone.Accent, true);
        }
        if (charging)
        {
            int target = chargeTarget;
            // A cached ETA may describe discharge or the previous protection limit. Never relabel
            // that duration as time to a different target; wait for the next history projection.
            if (day != null && day.BatteryEtaKnown && day.BatteryEtaMinutes > 0 &&
                day.BatteryEtaTargetPercent == target)
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
                "供电",
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

        RectangleF ground = GroundRect(content, true, true);
        double max = Math.Max(d.ReferenceBalance, d.Balance);
        DrawSpark(g, ground, balances, chartColor, max <= 0.0 ? 1.0 : max, true, false);
        DrawCaption(g, content, d.RunwayKnown ? "按近 24h 趋势" : "DEEPSEEK · 48h 余额");

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
            g.DrawString(usage, usageFont, usageBrush, content.X, content.Y + S(36));
        }

        // Runway becomes the single conclusion; account state, refresh time and the service verdict
        // drop into the footer band instead of stacking in the right-hand column.
        string status = d.RequestRunning
            ? "正在刷新"
            : (!string.IsNullOrEmpty(d.ErrorCode)
                ? (d.ErrorMessage ?? "刷新失败，保留上次余额")
                : (d.Known ? (d.IsAvailable ? "账户可用" : "账户暂不可用") : "等待数据"));
        Color statusColor = d.Known && d.IsAvailable && string.IsNullOrEmpty(d.ErrorCode)
            ? DesignTokens.Colors.Success
            : (string.IsNullOrEmpty(d.ErrorCode) ? DesignTokens.Colors.GlyphMuted : DesignTokens.Colors.Warning);
        if (d.RunwayKnown)
        {
            DrawConclusion(g, content, DesignTokens.WithAlpha(chartColor, 255),
                FormatForecastHours(d.RunwayHours), "余额可用");
        }
        else
        {
            DrawConclusion(g, content, DesignTokens.Colors.GlyphMuted,
                d.ApiKeyConfigured ? "估算中" : "未配置",
                d.ApiKeyConfigured ? "等余额下降" : "DeepSeek API Key");
        }

        FooterSegment[] footer = new FooterSegment[]
        {
            FooterSegment.Make(null, status, null)
                .WithValueColor(DesignTokens.WithAlpha(statusColor, 240))
                .WithSwatch(statusColor),
            d.CheckedAtLocal != DateTime.MinValue
                ? FooterSegment.Make("更新", d.CheckedAtLocal.ToString("HH:mm", CultureInfo.CurrentCulture), null)
                : null,
            service.Known
                ? FooterSegment.Make("API", service.IsAvailable ? "正常" : "服务异常", null)
                    .WithValueColor(DesignTokens.WithAlpha(
                        service.IsAvailable ? DesignTokens.Colors.TextMuted : DesignTokens.Colors.Warning, 240))
                : null,
            FooterSegment.Make("曲线", null, "近 48h 余额")
        };
        DrawFooterBand(g, content, true, footer);

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
        RectangleF ground = GroundRect(content, true, true);
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
        DrawRadarQuotaFooter(g, content, r);
        DrawQuotaWindowStrip(g, content, dataAccent, r);
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

        string model = string.IsNullOrEmpty(r.ModelName) ? r.FamilyLabel : r.ModelName;
        string sampleText = r.BurnRateKnown
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} · 最近 {1} 活跃时",
                model,
                FormatForecastHours(r.BurnObservedHours))
            : (r.CalendarRunwayKnown ? model + " · 近 24h 节奏已建立" : model + " · 趋势采样中");
        DrawText(g, sampleText, content.X, content.Y + S(36), S(12.5f),
            DesignTokens.WithAlpha(DesignTokens.Colors.TextMuted, 210), FontStyle.Regular);
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

    // One conclusion only. The qualifier moves to the caption slot and the two secondary forecasts
    // (five-hour window, recent-24h rhythm) move to the footer band, so the right-hand column stops
    // being a four-line paragraph competing with the curve.
    private void DrawRadarQuotaForecast(Graphics g, RectangleF content, Color accent, RadarTileSnapshot r)
    {
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

        DrawCaption(g, new RectangleF(content.X, content.Y, content.Width, S(CaptionSize)),
            activeForecast ? "按当前活跃趋势" : "按近 24h 节奏");
        DrawConclusion(g, content, stateColor, main, status);
    }

    private void DrawRadarQuotaFooter(Graphics g, RectangleF content, RadarTileSnapshot r)
    {
        string fiveHour = r.FiveHourLimitAbsent
            ? "∞"
            : r.FiveHourPercent.ToString(CultureInfo.InvariantCulture) + "%";
        string fiveHourReset = r.FiveHourResetKnown
            ? "@" + r.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture)
            : "@未知";
        string weeklyReset = r.WeeklyResetKnown
            ? "@" + r.WeeklyResetLocal.ToString("MM/dd HH:mm", CultureInfo.CurrentCulture)
            : "@未知";
        // The five-hour window turns red on the same condition the retired text row used, so the
        // colour still calls out "this window runs out before it resets".
        Color fiveHourColor = !r.FiveHourLimitAbsent && r.FiveHourBurnRateKnown
            && r.FiveHourHoursToReset > 0.0 && r.FiveHourRunwayHours < r.FiveHourHoursToReset
            ? MetricTileForm.ResolveBurnInRingColor(
                DesignTokens.Colors.Danger, this.burnInVisualLevel, this.burnInPresentationRestored)
            : DesignTokens.Colors.TextMuted;
        string rhythm = r.CalendarRunwayKnown
            ? FormatForecastHours(r.CalendarRunwayHours)
            : "采样中";
        string confidence = r.BurnRateKnown || r.CalendarRunwayKnown
            ? FormatQuotaForecastConfidence(r.BurnRateKnown ? r.BurnConfidence : r.CalendarConfidence)
            : null;
        DrawFooterBand(g, content, true, new FooterSegment[]
        {
            FooterSegment.Make("5h 窗口", r.QuotaKnown ? fiveHour : "--", r.QuotaKnown ? fiveHourReset : null)
                .WithValueColor(DesignTokens.WithAlpha(fiveHourColor, 240)),
            FooterSegment.Make("周窗口",
                r.QuotaKnown ? r.WeeklyPercent.ToString(CultureInfo.InvariantCulture) + "%" : "--",
                r.QuotaKnown ? weeklyReset : null),
            FooterSegment.Make("近 24h 节奏", rhythm, confidence)
        });
    }

    // Two plain windows, no text on top: the left half is the five-hour window and the right half
    // the weekly one, split by a hairline so they cannot be read as a single bar.
    private void DrawQuotaWindowStrip(Graphics g, RectangleF content, Color accent, RadarTileSnapshot r)
    {
        RectangleF strip = StripRect(content);
        float half = (strip.Width - S(2)) / 2.0f;
        RectangleF left = new RectangleF(strip.X, strip.Y, half, strip.Height);
        RectangleF right = new RectangleF(strip.Right - half, strip.Y, half, strip.Height);
        DrawQuotaWindowSegment(g, left, accent,
            r.QuotaKnown && !r.FiveHourLimitAbsent ? r.FiveHourPercent : (r.FiveHourLimitAbsent ? 100.0 : -1.0));
        DrawQuotaWindowSegment(g, right, accent, r.QuotaKnown ? r.WeeklyPercent : -1.0);
    }

    private void DrawQuotaWindowSegment(Graphics g, RectangleF rect, Color accent, double percent)
    {
        using (GraphicsPath track = RoundedRectangle(rect, Math.Max(1.0f, rect.Height / 2.0f)))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(28)))
        {
            g.FillPath(trackBrush, track);
            if (percent < 0.0)
            {
                return;
            }

            float width = (float)(rect.Width * MetricTileModel.Clamp(percent, 0.0, 100.0) / 100.0);
            if (width <= 0.0f)
            {
                return;
            }

            Region previous = g.Clip;
            g.SetClip(track, CombineMode.Intersect);
            using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(accent, 225)))
            {
                g.FillRectangle(fill, rect.X, rect.Y, width, rect.Height);
            }

            g.Clip = previous;
        }
    }

    // Labels the two vertical references the burn-down already draws, so the weekly reset stops
    // being an orphan word in the bottom-right corner of the panel.
    private void DrawBurnDownMarker(Graphics g, RectangleF rect, float x, string text)
    {
        float size = S(9);
        using (Font font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 210)))
        {
            float width = g.MeasureString(text, font).Width;
            float left = Math.Max(rect.X, Math.Min(x - width / 2.0f, rect.Right - width));
            g.DrawString(text, font, brush, left, rect.Bottom - size - S(2));
        }
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

        if (ShouldDrawNeutralText(this.burnInVisualLevel))
        {
            DrawBurnDownMarker(g, rect, historyRight, "现在");
            if (r.WeeklyResetKnown && r.HoursToReset > 0.0)
            {
                DrawBurnDownMarker(g, rect, rect.Right - S(2), "周重置");
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
        if (ResolvePowerForecast(plugged, null).Main != "已到" ||
            ResolvePowerForecast(plugged, null).Status != "80%")
        {
            throw new InvalidOperationException("PWR must show the 80% ceiling as reached, not time to 100%.");
        }

        charging.BatteryCarePauseActive = true;
        if (ResolvePowerForecast(charging, chargingDay).Status != "到100%" ||
            ResolvePowerForecast(charging, chargingDay).Main != "充电")
            throw new InvalidOperationException("A changed charge target must not reuse a stale 80% duration.");
        chargingDay.BatteryEtaTargetPercent = 100;
        if (ResolvePowerForecast(charging, chargingDay).Main != "55m")
            throw new InvalidOperationException("Paused care must use the 100% ETA.");
        chargingDay.BatteryEtaTargetPercent = 0;
        if (ResolvePowerForecast(charging, chargingDay).Main != "充电")
            throw new InvalidOperationException("Charging must never reuse a cached discharge ETA.");
        plugged.BatteryPercent = 75;
        if (ResolvePowerForecast(plugged, null).Main != "AC")
            throw new InvalidOperationException("Idle AC must not be described as discharging.");
        discharging.BatteryPercent = 85;
        if (ResolvePowerForecast(discharging, null).Status != "耗尽")
            throw new InvalidOperationException("Battery operation above 80% must still predict runtime.");

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

            panel.PrepareForRenderSample(MetricTileId.Power, new MetricTileFeed { Power = charging });
            using (Bitmap bitmap = new Bitmap(panel.Width, panel.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap)) panel.DrawPanel(graphics);
            RectangleF careBounds = panel.batteryCareHitBounds;
            if (careBounds.IsEmpty ||
                !new RectangleF(0, 0, panel.Width, panel.Height).Contains(careBounds))
                throw new InvalidOperationException("PWR battery protection must have a visible, in-bounds hit target.");
            int requests = 0;
            bool requestedPause = true;
            Action<bool, string> finish = null;
            panel.BatteryCareRequest = delegate(bool pause, Action<bool, string> done)
            {
                requests++;
                requestedPause = pause;
                finish = done;
            };
            int hitX = (int)careBounds.Left + 2;
            int hitY = (int)careBounds.Top + 2;
            MouseEventArgs click = new MouseEventArgs(MouseButtons.Left, 1, hitX, hitY, 0);
            panel.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            panel.OnMouseUp(click);
            panel.OnMouseUp(click);
            if (requests != 1 || requestedPause || finish == null)
                throw new InvalidOperationException("PWR paused protection must request restore once, ignoring outside/pending clicks.");
            finish(false, "fixture failure");
            charging.BatteryCarePauseActive = false;
            panel.OnMouseUp(click);
            if (requests != 2 || !requestedPause)
                throw new InvalidOperationException("PWR enabled protection must request pause and allow retry after failure.");
            finish(true, "fixture only; no hardware command");
            panel.PrepareForRenderSample(MetricTileId.Cpu, new MetricTileFeed());
            panel.OnMouseUp(click);
            if (requests != 2)
                throw new InvalidOperationException("Battery controls must not respond on another tile.");
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
