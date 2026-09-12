using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

// Board content finalized across design-review rounds (see this board's originating brief): header
// (title + running dot + time-since-last-success), a divider, a single-row toolbar (context-aware
// toggle, rounds stepper, model chip, separator, start/stop, close, attribution), a failure alert
// strip that takes zero height when there is no recent failure, a section label, and a flex-filled
// history list capped by measured row height (the same MaximumVisibleRows-style budgeting
// CodexTaskBoard uses, not a hardcoded entry count). No shared "OledVariantPainting" drawing-helper
// file exists in this codebase (checked before writing this): every board keeps its own small
// RoundedRectangle-based drawing helpers in its own .Layout.cs, matching ResetSpeedBoardForm.Layout.cs
// and GuardBoardForm.Layout.cs, which this file's DrawToggle/DrawStepper/toolbar-button shapes copy.
internal sealed partial class CaptionsBoardForm
{
    private const string AttributionText = "GenieX · NPU";

    // The caption transcript is the lowest-value content on this board (the controls above it are
    // what the user actually comes here for), so it is capped well below what the canvas could fit
    // and the reclaimed height goes to the service status strip and to overall breathing room.
    private const int MaxHistoryEntries = 3;

    protected override void DrawWindowContent(Graphics g)
    {
        DrawBoard(g, true);
    }

    internal void DrawBoard(Graphics g, bool recordHitTargets)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (recordHitTargets)
        {
            this.hitTargets.Clear();
        }

        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 246)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0.5f, 0.5f, this.Width - 1, this.Height - 1), Math.Max(3, S(10))))
        using (Pen shellBorder = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 116), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawPath(shellBorder, shell);
        }

        Font titleFont = this.fontCache.GetUi(S(15.0f), FontStyle.Bold);
        Font labelFont = this.fontCache.GetUi(S(9.0f), FontStyle.Regular);
        Font bodyFont = this.fontCache.GetUi(S(8.5f), FontStyle.Regular);
        Font smallFont = this.fontCache.GetUi(S(7.8f), FontStyle.Regular);
        Font sectionFont = this.fontCache.GetUi(S(9.2f), FontStyle.Bold);
        Font strongFont = this.fontCache.GetUi(S(9.2f), FontStyle.Regular);
        Font monoFont = this.fontCache.GetMono(S(8.3f), FontStyle.Regular);
        Font monoSmallFont = this.fontCache.GetMono(S(7.5f), FontStyle.Regular);
        Font glyphFont = this.fontCache.GetUi(S(9.5f), FontStyle.Bold);

        int pad = S(14);
        Rectangle content = new Rectangle(pad, pad, Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - pad * 2));

        int headerHeight = S(22);
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, headerHeight);
        int y = header.Bottom + S(7);

        DrawDivider(g, content.Left, y, content.Width);
        y += S(10);

        // Service status strip: the four processes that all have to be up for a translation to
        // succeed. It sits above the toolbar because it answers "can this work at all right now",
        // which has to be readable before any of the settings controls below it mean anything.
        int statusRowHeight = S(24);
        Rectangle statusRow = new Rectangle(content.Left, y, content.Width, statusRowHeight);
        y = statusRow.Bottom + S(11);

        int toolbarHeight = S(25);
        Rectangle toolbar = new Rectangle(content.Left, y, content.Width, toolbarHeight);
        y = toolbar.Bottom;

        bool hasFailure = this.snapshot.RecentHistory.Count > 0 && this.snapshot.RecentHistory[0].IsError;
        Rectangle alert = Rectangle.Empty;
        if (hasFailure)
        {
            y += S(9);
            alert = new Rectangle(content.Left, y, content.Width, S(24));
            y = alert.Bottom;
        }

        y += S(10);
        DrawDivider(g, content.Left, y, content.Width);
        y += S(8);

        int sectionLabelHeight = S(13);
        Rectangle sectionLabel = new Rectangle(content.Left, y, content.Width, sectionLabelHeight);
        y = sectionLabel.Bottom + S(5);

        Rectangle historyArea = new Rectangle(content.Left, y, content.Width, Math.Max(1, content.Bottom - y));

        DrawHeader(g, header, titleFont, bodyFont, monoFont);
        DrawServiceStatusRow(g, statusRow, labelFont, recordHitTargets);
        DrawToolbar(g, toolbar, labelFont, bodyFont, monoFont, glyphFont, recordHitTargets);
        if (hasFailure)
        {
            DrawFailureAlert(g, alert, smallFont, monoSmallFont);
        }

        DrawSectionLabel(g, sectionLabel, smallFont);
        DrawHistoryList(g, historyArea, smallFont, strongFont, monoSmallFont);

        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.Captions, this.LayerScale);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font titleFont, Font bodyFont, Font monoFont)
    {
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString("字幕", titleFont, titleBrush, new Rectangle(bounds.Left, bounds.Top, S(50), bounds.Height), near);
        }

        int statusLeft = bounds.Left + S(46);
        Color statusColor = this.snapshot.IsRunning ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted;
        string statusText = this.snapshot.IsRunning ? "运行中" : "已停止";
        int dotSize = S(7);
        int dotTop = bounds.Top + (bounds.Height - dotSize) / 2;
        using (SolidBrush dotBrush = new SolidBrush(statusColor))
        {
            g.FillEllipse(dotBrush, statusLeft, dotTop, dotSize, dotSize);
        }

        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString(statusText, bodyFont, statusBrush, new Rectangle(statusLeft + dotSize + S(5), bounds.Top, S(70), bounds.Height), near);
        }

        if (this.snapshot.LastSuccessKnown)
        {
            string sinceText = "距上次成功 " + FormatElapsed(DateTime.Now - this.snapshot.LastSuccessLocal);
            using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat far = CreateFormat(StringAlignment.Far))
            {
                g.DrawString(sinceText, monoFont, mutedBrush, bounds, far);
            }
        }
    }

    // Four compact service chips left-aligned, plus the caption-source control right-aligned on the
    // same row. A chip that is UP registers no hit target at all -- clicking a healthy service must
    // never be able to restart it by accident (GenieX in particular costs ~10s of model reload).
    private void DrawServiceStatusRow(Graphics g, Rectangle bounds, Font labelFont, bool recordHitTargets)
    {
        bool busy = this.operationRunning;
        int gap = S(6);
        int x = bounds.Left;

        x = DrawServiceChip(g, x, bounds, "GenieX", this.snapshot.GenieXRunning, CaptionsHitAction.GenieXStart, labelFont, busy, recordHitTargets) + gap;
        x = DrawServiceChip(g, x, bounds, "代理", this.snapshot.SanitizeProxyRunning, CaptionsHitAction.SanitizeProxyStart, labelFont, busy, recordHitTargets) + gap;
        x = DrawServiceChip(g, x, bounds, "实时字幕", this.snapshot.LiveCaptionsRunning, CaptionsHitAction.LiveCaptionsStart, labelFont, busy, recordHitTargets) + gap;
        x = DrawServiceChip(g, x, bounds, "翻译器", this.snapshot.IsRunning, CaptionsHitAction.TranslatorStart, labelFont, busy, recordHitTargets) + gap;

        DrawCaptionSourceChip(g, bounds, labelFont, busy, recordHitTargets, x);
    }

    private int DrawServiceChip(
        Graphics g,
        int left,
        Rectangle row,
        string label,
        bool up,
        CaptionsHitAction startAction,
        Font font,
        bool busy,
        bool recordHitTargets)
    {
        bool pending = busy && this.pendingAction == startAction;
        int dotSize = S(7);
        int innerPad = S(8);
        // Same generous measurement pad the toolbar labels use: GenericTypographic measurement is
        // tighter than the NoWrap/Ellipsis DrawString below actually needs, and a snug box
        // ellipsis-trims even short CJK labels.
        int labelWidth = MeasureTextWidth(g, label, font) + S(8);
        int width = innerPad + dotSize + S(5) + labelWidth + innerPad;
        Rectangle bounds = new Rectangle(left, row.Top, width, row.Height);

        // A down chip is the click target that starts its service, so it carries the board's violet
        // accent border and brighter text to read as actionable; an up chip stays on the neutral
        // border and reads as a passive indicator.
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
        Color borderColor = up ? DesignTokens.Colors.Border : accent;
        int borderAlpha = up ? 110 : (busy ? 90 : 185);
        Color dotColor = pending
            ? DesignTokens.Colors.Warning
            : (up ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted);
        Color textColor = up ? DesignTokens.Colors.TextMuted : DesignTokens.Colors.Text;
        int textAlpha = busy && !pending ? 130 : 255;

        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, up ? 170 : 205)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(borderColor, borderAlpha), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        int dotTop = bounds.Top + (bounds.Height - dotSize) / 2;
        using (SolidBrush dotBrush = new SolidBrush(DesignTokens.WithAlpha(dotColor, textAlpha)))
        {
            g.FillEllipse(dotBrush, bounds.Left + innerPad, dotTop, dotSize, dotSize);
        }

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.WithAlpha(textColor, textAlpha)))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString(
                label,
                font,
                textBrush,
                new Rectangle(bounds.Left + innerPad + dotSize + S(5), bounds.Top, labelWidth, bounds.Height),
                near);
        }

        if (recordHitTargets && !up && !busy)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = bounds, Action = startAction });
        }

        return bounds.Right;
    }

    // Shows the consequence of the registry value, not the raw code: which text the local model
    // actually receives. "原文 EN" means Windows hands over untouched English and our model does the
    // EN->ZH work; "微软已翻 中" means Windows already translated it and the model only ever sees
    // Chinese. Clicking flips between exactly those two states (including out of 未知).
    private void DrawCaptionSourceChip(Graphics g, Rectangle row, Font font, bool busy, bool recordHitTargets, int minimumLeft)
    {
        bool pending = busy && this.pendingAction == CaptionsHitAction.CaptionLanguageToggle;
        bool original = this.snapshot.CaptionLanguageKnown && string.Equals(
            this.snapshot.CaptionLanguage,
            TranslatorControlReader.CaptionLanguageOriginalEnglish,
            StringComparison.OrdinalIgnoreCase);
        bool microsoftTranslated = this.snapshot.CaptionLanguageKnown && string.Equals(
            this.snapshot.CaptionLanguage,
            TranslatorControlReader.CaptionLanguageMicrosoftChinese,
            StringComparison.OrdinalIgnoreCase);

        const string prefix = "字幕源";
        string value = pending
            ? "切换中…"
            : (original ? "原文 EN" : (microsoftTranslated ? "微软已翻 中" : "未知"));
        Color valueColor = pending
            ? DesignTokens.Colors.TextMuted
            : (original
                ? DesignTokens.Colors.SuccessText
                : (microsoftTranslated ? DesignTokens.Colors.Warning : DesignTokens.Colors.GlyphMuted));

        int innerPad = S(8);
        int prefixWidth = MeasureTextWidth(g, prefix, font) + S(8);
        int valueWidth = MeasureTextWidth(g, value, font) + S(8);
        int width = innerPad + prefixWidth + S(5) + valueWidth + innerPad;
        // Never let this chip run back into the service chips: at an unusual LayerScale the measured
        // label widths could add up past the row, and an overlapping hit target would silently hand
        // the click to whichever target was registered first.
        width = Math.Max(S(24), Math.Min(width, Math.Max(S(24), row.Right - minimumLeft)));
        Rectangle bounds = new Rectangle(row.Right - width, row.Top, width, row.Height);

        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 205)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, busy ? 90 : 185), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        int textAlpha = busy && !pending ? 130 : 255;
        int availablePrefixWidth = Math.Max(1, Math.Min(prefixWidth, bounds.Width - innerPad * 2));
        int availableValueWidth = Math.Max(1, bounds.Width - innerPad * 2 - availablePrefixWidth - S(5));
        using (SolidBrush prefixBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, textAlpha)))
        using (SolidBrush valueBrush = new SolidBrush(DesignTokens.WithAlpha(valueColor, textAlpha)))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString(prefix, font, prefixBrush, new Rectangle(bounds.Left + innerPad, bounds.Top, availablePrefixWidth, bounds.Height), near);
            g.DrawString(
                value,
                font,
                valueBrush,
                new Rectangle(bounds.Left + innerPad + availablePrefixWidth + S(5), bounds.Top, availableValueWidth, bounds.Height),
                near);
        }

        if (recordHitTargets && !busy)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = bounds, Action = CaptionsHitAction.CaptionLanguageToggle });
        }
    }

    private void DrawToolbar(Graphics g, Rectangle bounds, Font labelFont, Font bodyFont, Font monoFont, Font glyphFont, bool recordHitTargets)
    {
        bool busy = this.operationRunning;
        int gap = S(5);
        int groupGap = S(10);
        int x = bounds.Left;

        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.TextMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            string contextAwareLabel = "上下文感知";
            // GenericTypographic measurement (MeasureTextWidth) is tighter than the NoWrap/Ellipsis
            // format DrawString below actually uses -- a small fixed pad silently ellipsis-trims even
            // very short CJK labels (see FitFontSize width-must-match-draw-width note). A generous
            // pad keeps the measured box wider than the real draw width.
            int contextAwareLabelWidth = MeasureTextWidth(g, contextAwareLabel, labelFont) + S(8);
            g.DrawString(contextAwareLabel, labelFont, labelBrush, new Rectangle(x, bounds.Top, contextAwareLabelWidth, bounds.Height), near);
            x += contextAwareLabelWidth + gap;

            int toggleWidth = S(28);
            int toggleHeight = S(15);
            Rectangle toggleBounds = new Rectangle(x, bounds.Top + (bounds.Height - toggleHeight) / 2, toggleWidth, toggleHeight);
            DrawToggle(g, toggleBounds, this.snapshot.ContextAware, busy);
            if (recordHitTargets && !busy)
            {
                this.hitTargets.Add(new CaptionsHitTarget { Bounds = toggleBounds, Action = CaptionsHitAction.ContextAwareToggle });
            }

            x = toggleBounds.Right + groupGap;

            string roundsLabel = "轮数";
            int roundsLabelWidth = MeasureTextWidth(g, roundsLabel, labelFont) + S(8);
            g.DrawString(roundsLabel, labelFont, labelBrush, new Rectangle(x, bounds.Top, roundsLabelWidth, bounds.Height), near);
            x += roundsLabelWidth + gap;

            bool numContextsPending = busy &&
                (this.pendingAction == CaptionsHitAction.NumContextsMinus || this.pendingAction == CaptionsHitAction.NumContextsPlus);
            string stepperValue = numContextsPending
                ? "…"
                : (this.snapshot.NumContextsKnown ? this.snapshot.NumContexts.ToString(CultureInfo.InvariantCulture) : "--");
            Rectangle stepperBounds = DrawStepper(g, x, bounds.Top, bounds.Height, stepperValue, monoFont, glyphFont, busy, recordHitTargets);
            x = stepperBounds.Right + groupGap;

            string modelLabel = "模型";
            int modelLabelWidth = MeasureTextWidth(g, modelLabel, labelFont) + S(8);
            g.DrawString(modelLabel, labelFont, labelBrush, new Rectangle(x, bounds.Top, modelLabelWidth, bounds.Height), near);
            x += modelLabelWidth + gap;

            bool modelPending = busy && this.pendingAction == CaptionsHitAction.ModelCycle;
            bool modelCyclable = this.snapshot.AvailableModels.Count > 1;
            string chipText = modelPending ? "切换中…" : FormatModelDisplayName(this.snapshot.ModelName);
            Rectangle chipBounds = DrawModelChip(g, x, bounds.Top, bounds.Height, chipText, bodyFont, glyphFont, busy || !modelCyclable);
            if (recordHitTargets && !busy && modelCyclable)
            {
                this.hitTargets.Add(new CaptionsHitTarget { Bounds = chipBounds, Action = CaptionsHitAction.ModelCycle });
            }

            x = chipBounds.Right + groupGap;
        }

        using (Pen separatorPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 140), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(separatorPen, x, bounds.Top + S(2), x, bounds.Bottom - S(2));
        }

        x += groupGap;

        bool runningPending = busy && this.pendingAction == CaptionsHitAction.ToggleRunning;
        string toggleLabel = runningPending ? "处理中…" : (this.snapshot.IsRunning ? "◼ 停止翻译" : "▶ 启动翻译");
        // Success green reads as "click to start"; the board's own violet accent (never Danger red,
        // which is reserved for Close) reads as "click to stop" -- stopping is not an error state.
        Color toggleColor = this.snapshot.IsRunning
            ? EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions)
            : DesignTokens.Colors.Success;
        int toggleButtonWidth = MeasureTextWidth(g, toggleLabel, bodyFont) + S(16);
        Rectangle toggleButtonBounds = new Rectangle(x, bounds.Top, toggleButtonWidth, bounds.Height);
        DrawToolbarButton(g, toggleButtonBounds, toggleLabel, toggleColor, bodyFont, busy);
        if (recordHitTargets && !busy)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = toggleButtonBounds, Action = CaptionsHitAction.ToggleRunning });
        }

        x = toggleButtonBounds.Right + gap;

        string closeLabel = "✕ 关闭";
        int closeWidth = MeasureTextWidth(g, closeLabel, bodyFont) + S(14);
        Rectangle closeBounds = new Rectangle(x, bounds.Top, closeWidth, bounds.Height);
        DrawToolbarButton(g, closeBounds, closeLabel, DesignTokens.Colors.Danger, bodyFont, false);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = closeBounds, Action = CaptionsHitAction.Close });
        }

        x = closeBounds.Right + gap;

        int attributionWidth = Math.Max(0, bounds.Right - x);
        if (attributionWidth > S(16))
        {
            using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat far = CreateFormat(StringAlignment.Far))
            {
                g.DrawString(AttributionText, bodyFont, mutedBrush, new Rectangle(x, bounds.Top, attributionWidth, bounds.Height), far);
            }
        }
    }

    private void DrawToggle(Graphics g, Rectangle bounds, bool on, bool busy)
    {
        float radius = bounds.Height / 2.0f;
        Color accent = on ? DesignTokens.Colors.Success : DesignTokens.Colors.Border;
        int alphaScale = busy ? 120 : 255;
        using (GraphicsPath track = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), radius))
        using (SolidBrush fill = new SolidBrush(on
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Success, ScaleAlpha(56, alphaScale))
            : DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(230, alphaScale))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, ScaleAlpha(on ? 205 : 150, alphaScale)), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, track);
            g.DrawPath(border, track);
        }

        float knob = Math.Max(3.0f, bounds.Height - S(5));
        float knobTop = bounds.Top + (bounds.Height - knob) / 2.0f;
        float knobLeft = on ? bounds.Right - knob - S(2) : bounds.Left + S(2);
        using (SolidBrush knobBrush = new SolidBrush(DesignTokens.WithAlpha(
            on ? DesignTokens.Colors.Success : DesignTokens.Colors.GlyphMuted,
            ScaleAlpha(255, alphaScale))))
        {
            g.FillEllipse(knobBrush, knobLeft, knobTop, knob, knob);
        }
    }

    private Rectangle DrawStepper(Graphics g, int left, int top, int height, string value, Font valueFont, Font glyphFont, bool busy, bool recordHitTargets)
    {
        int stepWidth = Math.Max(S(14), height);
        int valueWidth = Math.Max(S(28), MeasureTextWidth(g, value, valueFont) + S(6));
        int totalWidth = stepWidth * 2 + valueWidth;
        Rectangle bounds = new Rectangle(left, top, totalWidth, height);
        int alpha = busy ? 120 : 255;

        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(220, alpha))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, ScaleAlpha(170, alpha)), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        Rectangle minusBounds = new Rectangle(bounds.Left, bounds.Top, stepWidth, height);
        Rectangle valueBounds = new Rectangle(minusBounds.Right, bounds.Top, valueWidth, height);
        Rectangle plusBounds = new Rectangle(valueBounds.Right, bounds.Top, stepWidth, height);

        using (SolidBrush glyph = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, alpha)))
        using (SolidBrush text = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.TextStrong, alpha)))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.DrawString("−", glyphFont, glyph, minusBounds, centered);
            g.DrawString(value, valueFont, text, valueBounds, centered);
            g.DrawString("+", glyphFont, glyph, plusBounds, centered);
        }

        if (recordHitTargets && !busy)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = minusBounds, Action = CaptionsHitAction.NumContextsMinus });
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = plusBounds, Action = CaptionsHitAction.NumContextsPlus });
        }

        return bounds;
    }

    private Rectangle DrawModelChip(Graphics g, int left, int top, int height, string text, Font font, Font chevronFont, bool disabled)
    {
        int chevronWidth = S(12);
        int textWidth = Math.Min(S(150), MeasureTextWidth(g, text, font) + S(8));
        Rectangle bounds = new Rectangle(left, top, textWidth + chevronWidth + S(6), height);
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
        int alpha = disabled ? 110 : 255;

        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, ScaleAlpha(212, alpha))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, ScaleAlpha(90, alpha)), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Text, alpha)))
        using (SolidBrush chevronBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, alpha)))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.DrawString(text, font, textBrush, new Rectangle(bounds.Left + S(6), bounds.Top, textWidth, bounds.Height), near);
            g.DrawString("⌄", chevronFont, chevronBrush, new Rectangle(bounds.Right - chevronWidth - S(2), bounds.Top, chevronWidth, bounds.Height), centered);
        }

        return bounds;
    }

    private void DrawToolbarButton(Graphics g, Rectangle bounds, string text, Color semanticColor, Font font, bool disabled)
    {
        int alpha = disabled ? 110 : 255;
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -1.0f, -1.0f), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, ScaleAlpha(220, alpha))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(semanticColor, ScaleAlpha(190, alpha)), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Text, alpha)))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(text, font, textBrush, bounds, centered);
        }
    }

    private void DrawFailureAlert(Graphics g, Rectangle bounds, Font smallFont, Font monoFont)
    {
        if (this.snapshot.RecentHistory.Count == 0)
        {
            return;
        }

        TranslatorHistoryEntry newest = this.snapshot.RecentHistory[0];
        using (GraphicsPath path = RoundedRectangle(bounds, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 30)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 130), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        int dotSize = S(6);
        int dotTop = bounds.Top + (bounds.Height - dotSize) / 2;
        using (SolidBrush dotBrush = new SolidBrush(DesignTokens.Colors.Danger))
        {
            g.FillEllipse(dotBrush, bounds.Left + S(8), dotTop, dotSize, dotSize);
        }

        string message = "最近一次翻译失败：" + TranslatorControlReader.ExtractFailureReason(newest.TranslatedText);
        int timestampWidth = S(58);
        int textLeft = bounds.Left + S(8) + dotSize + S(6);
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.DangerText))
        using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        using (StringFormat far = CreateFormat(StringAlignment.Far))
        {
            int textWidth = Math.Max(1, bounds.Width - (textLeft - bounds.Left) - timestampWidth - S(8));
            g.DrawString(message, smallFont, textBrush, new Rectangle(textLeft, bounds.Top, textWidth, bounds.Height), near);
            if (newest.TimestampKnown)
            {
                g.DrawString(
                    newest.TimestampLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    monoFont,
                    mutedBrush,
                    new Rectangle(bounds.Right - timestampWidth - S(8), bounds.Top, timestampWidth, bounds.Height),
                    far);
            }
        }
    }

    private void DrawSectionLabel(Graphics g, Rectangle bounds, Font font)
    {
        using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString("最 近 字 幕", font, mutedBrush, bounds, near);
        }
    }

    private void DrawHistoryList(Graphics g, Rectangle bounds, Font smallFont, Font strongFont, Font monoSmallFont)
    {
        this.lastDrawnHistoryCount = 0;
        if (this.snapshot.RecentHistory.Count == 0)
        {
            using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat center = CreateFormat(StringAlignment.Center))
            {
                string message = this.snapshot.HistoryDatabaseFound ? "暂无字幕记录" : "等待翻译历史数据库";
                g.DrawString(message, smallFont, mutedBrush, bounds, center);
            }

            return;
        }

        int firstLineHeight = MeasureLineHeight(g, smallFont, S(2));
        int secondLineHeight = MeasureLineHeight(g, strongFont, S(2));
        int entryGap = S(6);
        int perEntry = firstLineHeight + secondLineHeight + entryGap;
        // Compute how many whole entries actually fit against the measured font metrics at this
        // canvas size, the same way CodexTaskBoard.MaximumVisibleRows() does, rather than assuming a
        // fixed count -- a different LayerScale/DPI changes the measured line heights. The measured
        // budget is then capped at MaxHistoryEntries: on this board the controls matter more than
        // the transcript, so the list deliberately shows less than it could fit.
        int maxEntries = perEntry > 0 ? Math.Max(1, (bounds.Height + entryGap) / perEntry) : 1;
        int count = Math.Min(Math.Min(MaxHistoryEntries, maxEntries), this.snapshot.RecentHistory.Count);

        // The cap frees far more height than the entries need, so instead of leaving a void under a
        // list crammed against the top, the area is divided into `count` equal rows that fill it and
        // each entry's measured two-line block is centred inside its own row. Row height therefore
        // comes from the area and the entry count, never from a hardcoded pixel rhythm, and the two
        // text lines inside a row are still positioned purely from their measured heights.
        int rowHeight = Math.Max(firstLineHeight + secondLineHeight + entryGap, bounds.Height / count);
        int blockHeight = firstLineHeight + secondLineHeight;

        this.lastDrawnHistoryCount = count;
        for (int i = 0; i < count; i++)
        {
            TranslatorHistoryEntry entry = this.snapshot.RecentHistory[i];
            int rowTop = bounds.Top + i * rowHeight;
            if (i > 0)
            {
                // Hairline between rows: with rows this tall, a separator is what makes the spacing
                // read as a deliberate list rather than as three stranded paragraphs.
                DrawDivider(g, bounds.Left, rowTop, bounds.Width);
            }

            int y = rowTop + Math.Max(0, (rowHeight - blockHeight) / 2);
            Rectangle firstLine = new Rectangle(bounds.Left, y, bounds.Width, firstLineHeight);
            Rectangle secondLine = new Rectangle(bounds.Left, firstLine.Bottom, bounds.Width, secondLineHeight);

            string badge = "→" + (string.IsNullOrWhiteSpace(entry.TargetLanguage) ? "--" : entry.TargetLanguage.Trim());
            int badgeWidth = MeasureTextWidth(g, badge, monoSmallFont) + S(6);
            int timestampWidth = S(50);

            using (SolidBrush badgeBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (SolidBrush sourceBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (SolidBrush timestampBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (SolidBrush translatedBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            using (StringFormat far = CreateFormat(StringAlignment.Far))
            {
                g.DrawString(badge, monoSmallFont, badgeBrush, new Rectangle(firstLine.Left, firstLine.Top, badgeWidth, firstLine.Height), near);
                int sourceLeft = firstLine.Left + badgeWidth + S(4);
                int sourceWidth = Math.Max(1, firstLine.Width - badgeWidth - S(4) - timestampWidth - S(4));
                g.DrawString(entry.SourceText, smallFont, sourceBrush, new Rectangle(sourceLeft, firstLine.Top, sourceWidth, firstLine.Height), near);
                if (entry.TimestampKnown)
                {
                    g.DrawString(
                        entry.TimestampLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        monoSmallFont,
                        timestampBrush,
                        new Rectangle(firstLine.Right - timestampWidth, firstLine.Top, timestampWidth, firstLine.Height),
                        far);
                }

                g.DrawString(entry.TranslatedText, strongFont, translatedBrush, secondLine, near);
            }
        }
    }

    private static int ScaleAlpha(int baseAlpha, int scale)
    {
        return DesignTokens.ClampByte(baseAlpha * scale / 255);
    }

    private static string FormatModelDisplayName(string fullModelName)
    {
        if (string.IsNullOrEmpty(fullModelName))
        {
            return "--";
        }

        string display = fullModelName;
        const string qualcommPrefix = "qualcomm/";
        if (display.StartsWith(qualcommPrefix, StringComparison.OrdinalIgnoreCase))
        {
            display = display.Substring(qualcommPrefix.Length);
        }

        int colonIndex = display.IndexOf(':');
        if (colonIndex >= 0)
        {
            display = display.Substring(0, colonIndex);
        }

        return display;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1.0)
        {
            return "刚刚";
        }

        if (elapsed.TotalHours < 1.0)
        {
            return ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
        }

        if (elapsed.TotalDays < 1.0)
        {
            return ((int)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时前";
        }

        return ((int)elapsed.TotalDays).ToString(CultureInfo.InvariantCulture) + " 天前";
    }

    private void DrawDivider(Graphics g, int left, int y, int width)
    {
        using (Pen pen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 90), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(pen, left, y, left + width, y);
        }
    }

    private static StringFormat CreateFormat(StringAlignment alignment)
    {
        return CreateFormat(alignment, StringTrimming.EllipsisCharacter);
    }

    private static StringFormat CreateFormat(StringAlignment alignment, StringTrimming trimming)
    {
        return new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = trimming,
            FormatFlags = StringFormatFlags.NoWrap
        };
    }

    private static int MeasureLineHeight(Graphics g, Font font, int padding)
    {
        return Math.Max(
            1,
            (int)Math.Ceiling(g.MeasureString("Ag国", font, int.MaxValue, StringFormat.GenericTypographic).Height) + padding);
    }

    private static int MeasureTextWidth(Graphics g, string text, Font font)
    {
        return (int)Math.Ceiling(g.MeasureString(text ?? string.Empty, font, int.MaxValue, StringFormat.GenericTypographic).Width);
    }

    // Layout self-test, mirroring GuardBoardForm.Layout.cs's VerifyHitTargets shape: proves every
    // interactive control the toolbar draws actually has a same-frame hit target (a control the user
    // can see but not click), and that no two hit targets overlap (the first one registered would
    // silently win the click) -- both invisible in a screenshot.
    internal static void RunSelfTest()
    {
        VerifyHitTargets(648, 400);
        VerifyServiceChipHitTargetsFollowServiceState(648, 400);
        VerifyHistoryEntryCap(648, 400);
        Console.WriteLine("Captions board layout: PASS hit targets, no overlap, zero-height alert strip when no failure, status-strip targets follow service state, history capped at " + MaxHistoryEntries);
    }

    private static void VerifyHitTargets(int logicalWidth, int logicalHeight)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = logicalWidth;
        settings.SpecBoardHeight = logicalHeight;
        settings.Normalize();

        using (CaptionsBoardForm form = new CaptionsBoardForm(null, settings, delegate { return null; }))
        using (Bitmap bitmap = new Bitmap(logicalWidth, logicalHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.Size = new Size(logicalWidth, logicalHeight);
            // Every service down, so all four status-strip start actions are registered in the same
            // frame: that is the only state in which the full interactive surface of this board is
            // drawn at once, and therefore the only state that can prove nothing overlaps.
            form.snapshot = CreateAllServicesDownFixtureSnapshot();
            form.DrawBoard(g, true);

            CaptionsHitAction[] required =
            {
                CaptionsHitAction.ContextAwareToggle,
                CaptionsHitAction.NumContextsMinus,
                CaptionsHitAction.NumContextsPlus,
                CaptionsHitAction.ModelCycle,
                CaptionsHitAction.ToggleRunning,
                CaptionsHitAction.Close,
                CaptionsHitAction.GenieXStart,
                CaptionsHitAction.SanitizeProxyStart,
                CaptionsHitAction.LiveCaptionsStart,
                CaptionsHitAction.TranslatorStart,
                CaptionsHitAction.CaptionLanguageToggle
            };

            for (int i = 0; i < required.Length; i++)
            {
                if (form.FindHitTarget(required[i]) == Rectangle.Empty)
                {
                    throw new InvalidOperationException("Captions board layout self-test failed: missing hit target " + required[i]);
                }
            }

            for (int i = 0; i < form.hitTargets.Count; i++)
            {
                Rectangle a = form.hitTargets[i].Bounds;
                if (a.Width <= 0 || a.Height <= 0)
                {
                    throw new InvalidOperationException("Captions board layout self-test failed: zero-area hit target " + form.hitTargets[i].Action);
                }

                for (int j = i + 1; j < form.hitTargets.Count; j++)
                {
                    if (a.IntersectsWith(form.hitTargets[j].Bounds))
                    {
                        throw new InvalidOperationException(
                            "Captions board layout self-test failed: overlapping hit targets " +
                            form.hitTargets[i].Action + " / " + form.hitTargets[j].Action);
                    }
                }
            }

            // The failure alert strip must take zero height (not merely be invisible) when there is
            // no recent failure, so the section label/history area below it are not pushed down for
            // nothing.
            TranslatorControlSnapshot noFailure = CreateAllServicesDownFixtureSnapshot();
            noFailure.RecentHistory[0].IsError = false;
            noFailure.RecentHistory[0].TranslatedText = "ok";
            form.snapshot = noFailure;
            int hitTargetCountWithoutAlert = form.hitTargets.Count;
            form.DrawBoard(g, true);
            if (form.hitTargets.Count != hitTargetCountWithoutAlert)
            {
                throw new InvalidOperationException("Captions board layout self-test failed: alert strip presence changed toolbar hit target count.");
            }
        }
    }

    // A healthy service must not be clickable: the status chip is an indicator, not a restart
    // button, and an accidental click on a green GenieX chip would cost ~10s of model reload.
    // The caption-source chip is the exception -- it is always actionable.
    private static void VerifyServiceChipHitTargetsFollowServiceState(int logicalWidth, int logicalHeight)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = logicalWidth;
        settings.SpecBoardHeight = logicalHeight;
        settings.Normalize();

        CaptionsHitAction[] startActions =
        {
            CaptionsHitAction.GenieXStart,
            CaptionsHitAction.SanitizeProxyStart,
            CaptionsHitAction.LiveCaptionsStart,
            CaptionsHitAction.TranslatorStart
        };

        using (CaptionsBoardForm form = new CaptionsBoardForm(null, settings, delegate { return null; }))
        using (Bitmap bitmap = new Bitmap(logicalWidth, logicalHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.Size = new Size(logicalWidth, logicalHeight);
            TranslatorControlSnapshot allUp = CreateFixtureSnapshot();
            allUp.LiveCaptionsRunning = true;
            form.snapshot = allUp;
            form.DrawBoard(g, true);

            for (int i = 0; i < startActions.Length; i++)
            {
                if (form.FindHitTarget(startActions[i]) != Rectangle.Empty)
                {
                    throw new InvalidOperationException(
                        "Captions board layout self-test failed: a running service still registered a start target " + startActions[i]);
                }
            }

            if (form.FindHitTarget(CaptionsHitAction.CaptionLanguageToggle) == Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the caption source chip must stay clickable while every service is up.");
            }

            // Unknown caption language must still be clickable -- that is the state a user most
            // needs to be able to correct.
            TranslatorControlSnapshot unknownLanguage = CreateFixtureSnapshot();
            unknownLanguage.LiveCaptionsRunning = true;
            unknownLanguage.CaptionLanguageKnown = false;
            unknownLanguage.CaptionLanguage = string.Empty;
            form.snapshot = unknownLanguage;
            form.DrawBoard(g, true);
            if (form.FindHitTarget(CaptionsHitAction.CaptionLanguageToggle) == Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the caption source chip must stay clickable when the registry value is unknown.");
            }
        }
    }

    private static void VerifyHistoryEntryCap(int logicalWidth, int logicalHeight)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = logicalWidth;
        settings.SpecBoardHeight = logicalHeight;
        settings.Normalize();

        using (CaptionsBoardForm form = new CaptionsBoardForm(null, settings, delegate { return null; }))
        using (Bitmap bitmap = new Bitmap(logicalWidth, logicalHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.Size = new Size(logicalWidth, logicalHeight);
            TranslatorControlSnapshot crowded = CreateFixtureSnapshot();
            while (crowded.RecentHistory.Count < 8)
            {
                crowded.RecentHistory.Add(crowded.RecentHistory[crowded.RecentHistory.Count - 1].Clone());
            }

            form.snapshot = crowded;
            form.DrawBoard(g, true);
            if (form.lastDrawnHistoryCount != MaxHistoryEntries)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: history must be capped at " + MaxHistoryEntries +
                    " entries, drew " + form.lastDrawnHistoryCount);
            }

            // Fewer available entries than the cap must not be padded out to the cap.
            TranslatorControlSnapshot sparse = CreateFixtureSnapshot();
            while (sparse.RecentHistory.Count > 2)
            {
                sparse.RecentHistory.RemoveAt(sparse.RecentHistory.Count - 1);
            }

            form.snapshot = sparse;
            form.DrawBoard(g, true);
            if (form.lastDrawnHistoryCount != 2)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: expected 2 history entries, drew " + form.lastDrawnHistoryCount);
            }
        }
    }

    private Rectangle FindHitTarget(CaptionsHitAction action)
    {
        for (int i = 0; i < this.hitTargets.Count; i++)
        {
            if (this.hitTargets[i].Action == action)
            {
                return this.hitTargets[i].Bounds;
            }
        }

        return Rectangle.Empty;
    }
}
