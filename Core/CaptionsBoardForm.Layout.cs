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
    // Upper bound only. What actually shows is whatever fits the measured area, which is the point:
    // the reader hands over at most eight rows, so this cap exists to stop a future larger board
    // from turning the panel into a transcript window, not to keep the list short.
    private const int MaxHistoryEntries = 8;

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
        y = toolbar.Bottom + S(9);

        // Caption source gets a full row of its own rather than a chip in the status strip: it is
        // now ten mutually exclusive options, and ten hit targets cannot share a row with four
        // service chips at any width this board supports.
        int captionSourceHeight = S(24);
        Rectangle captionSourceRow = new Rectangle(content.Left, y, content.Width, captionSourceHeight);
        y = captionSourceRow.Bottom;

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
        DrawCaptionSourceRow(g, captionSourceRow, labelFont, recordHitTargets);
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

    // Four compact service chips, left-aligned. A chip that is UP registers no hit target at all --
    // clicking a healthy service must never be able to restart it by accident (GenieX in particular
    // costs ~10s of model reload).
    private void DrawServiceStatusRow(Graphics g, Rectangle bounds, Font labelFont, bool recordHitTargets)
    {
        bool busy = this.operationRunning;
        int gap = S(6);
        int x = bounds.Left;

        x = DrawServiceChip(g, x, bounds, "GenieX", this.snapshot.GenieXRunning, CaptionsHitAction.GenieXStart, labelFont, busy, recordHitTargets) + gap;
        x = DrawServiceChip(g, x, bounds, "代理", this.snapshot.SanitizeProxyRunning, CaptionsHitAction.SanitizeProxyStart, labelFont, busy, recordHitTargets) + gap;
        x = DrawServiceChip(g, x, bounds, "实时字幕", this.snapshot.LiveCaptionsRunning, CaptionsHitAction.LiveCaptionsStart, labelFont, busy, recordHitTargets) + gap;
        DrawServiceChip(g, x, bounds, "翻译器", this.snapshot.IsRunning, CaptionsHitAction.TranslatorStart, labelFont, busy, recordHitTargets);
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

    // Caption source: one segment per language Windows can hand over, plus a label that spells out
    // the consequence rather than the registry code.
    //
    // "原文" means Live Captions transcribes and passes the speech through untranslated, so the
    // local NPU model does the language work; "微软已翻" means Live Captions translated it first and
    // the model now only ever sees Chinese -- the same text Microsoft would have produced anyway,
    // which is the whole reason this stack exists. The ZH segment is therefore drawn in the warning
    // colour when it is active, not the accent colour every other segment uses.
    //
    // The segment for the language already in effect registers no hit target: re-applying it would
    // restart Live Captions and the translator for no change at all.
    private void DrawCaptionSourceRow(Graphics g, Rectangle row, Font font, bool recordHitTargets)
    {
        bool busy = this.operationRunning;
        bool pending = busy && this.pendingAction == CaptionsHitAction.CaptionLanguageSet;
        string current = this.snapshot.CaptionLanguageKnown ? this.snapshot.CaptionLanguage : null;
        bool known = !string.IsNullOrEmpty(current);
        bool original = known && TranslatorControlReader.IsOriginalCaptionLanguage(current);

        string state = pending
            ? "切换中…"
            : (!known
                ? "未知"
                : (original
                    ? "原文 " + TranslatorControlReader.DescribeCaptionLanguage(current)
                    : "微软已翻 中"));
        Color labelColor = pending || !known
            ? DesignTokens.Colors.GlyphMuted
            : (original ? DesignTokens.Colors.SuccessText : DesignTokens.Colors.Warning);

        TranslatorControlReader.CaptionLanguageOption[] options = TranslatorControlReader.CaptionLanguageOptions;
        int gap = S(4);
        // The segments are the control; the label only explains them. So the label gives up its
        // prefix, and then itself, before the segments are allowed to shrink below a readable width
        // -- ten unreadable boxes would be worse than an unlabelled row. All three widths are
        // measured, never assumed, because the labels are CJK and the font scales with LayerScale.
        int minimumSegment = MeasureTextWidth(g, "EN", font) + S(8);
        int segmentsNeeded = options.Length * minimumSegment + gap * (options.Length - 1);
        string label = "字幕源 · " + state;
        int labelWidth = MeasureTextWidth(g, label, font) + S(10);
        if (row.Width - labelWidth - S(8) < segmentsNeeded)
        {
            label = state;
            labelWidth = MeasureTextWidth(g, label, font) + S(10);
            if (row.Width - labelWidth - S(8) < segmentsNeeded)
            {
                label = string.Empty;
                labelWidth = 0;
            }
        }

        if (labelWidth > 0)
        {
            using (SolidBrush labelBrush = new SolidBrush(DesignTokens.WithAlpha(labelColor, busy && !pending ? 130 : 255)))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            {
                g.DrawString(label, font, labelBrush, new Rectangle(row.Left, row.Top, labelWidth, row.Height), near);
            }
        }

        int segmentsLeft = row.Left + labelWidth + (labelWidth > 0 ? S(8) : 0);
        int available = Math.Max(S(40), row.Right - segmentsLeft);
        // Width comes from the row, never from a per-segment constant: the set is fixed at ten, so a
        // hardcoded segment width would either overflow or leave a gap at some LayerScale.
        int segmentWidth = (available - gap * (options.Length - 1)) / options.Length;
        int x = segmentsLeft;
        for (int i = 0; i < options.Length; i++)
        {
            int width = i == options.Length - 1 ? Math.Max(1, row.Right - x) : segmentWidth;
            Rectangle bounds = new Rectangle(x, row.Top, width, row.Height);
            bool active = known && string.Equals(options[i].Tag, current, StringComparison.OrdinalIgnoreCase);
            bool microsoftTranslated = string.Equals(
                options[i].Tag,
                TranslatorControlReader.CaptionLanguageMicrosoftChinese,
                StringComparison.OrdinalIgnoreCase);
            DrawCaptionLanguageSegment(g, bounds, options[i], active, microsoftTranslated, font, busy, recordHitTargets);
            x += width + gap;
        }
    }

    private void DrawCaptionLanguageSegment(
        Graphics g,
        Rectangle bounds,
        TranslatorControlReader.CaptionLanguageOption option,
        bool active,
        bool microsoftTranslated,
        Font font,
        bool busy,
        bool recordHitTargets)
    {
        Color accent = microsoftTranslated
            ? DesignTokens.Colors.Warning
            : EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
        Color borderColor = active ? accent : DesignTokens.Colors.Border;
        int borderAlpha = active ? 205 : (busy ? 70 : 130);
        Color textColor = active ? accent : DesignTokens.Colors.TextMuted;
        int textAlpha = busy ? 130 : 255;

        using (GraphicsPath path = RoundedRectangle(new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), S(4)))
        using (SolidBrush fill = new SolidBrush(active
            ? DesignTokens.WithAlpha(accent, 46)
            : DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 170)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(borderColor, borderAlpha), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.WithAlpha(textColor, textAlpha)))
        using (StringFormat center = CreateFormat(StringAlignment.Center))
        {
            g.DrawString(option.Label, font, textBrush, bounds, center);
        }

        if (recordHitTargets && !busy && !active)
        {
            this.hitTargets.Add(new CaptionsHitTarget
            {
                Bounds = bounds,
                Action = CaptionsHitAction.CaptionLanguageSet,
                Payload = option.Tag
            });
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

        // Rows keep their measured height and the list stays top-aligned. An earlier version spread
        // a three-entry cap across the whole area instead, which made every row roughly twice its
        // own content and turned the transcript into three stranded paragraphs separated by voids.
        // Spare height is better spent on more entries -- which is what raising the cap does -- than
        // on padding three of them apart.
        int rowHeight = firstLineHeight + secondLineHeight + entryGap;
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

            int y = rowTop + Math.Max(0, (rowHeight - blockHeight));
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
        // Narrow board: the caption-source row carries ten targets, which is where a width-driven
        // overlap would appear first.
        VerifyHitTargets(460, 400);
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
                CaptionsHitAction.CaptionLanguageSet
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

            // Every language except the one already in effect must be reachable, and the active one
            // must not be: re-applying it restarts Live Captions and the translator for no change.
            VerifyCaptionLanguageTargets(form, allUp.CaptionLanguage);

            // An unknown registry value must leave every option clickable -- that is the state a
            // user most needs to be able to correct.
            TranslatorControlSnapshot unknownLanguage = CreateFixtureSnapshot();
            unknownLanguage.LiveCaptionsRunning = true;
            unknownLanguage.CaptionLanguageKnown = false;
            unknownLanguage.CaptionLanguage = string.Empty;
            form.snapshot = unknownLanguage;
            form.DrawBoard(g, true);
            VerifyCaptionLanguageTargets(form, null);
        }
    }

    private static void VerifyCaptionLanguageTargets(CaptionsBoardForm form, string activeLanguage)
    {
        TranslatorControlReader.CaptionLanguageOption[] options = TranslatorControlReader.CaptionLanguageOptions;
        for (int i = 0; i < options.Length; i++)
        {
            bool active = !string.IsNullOrEmpty(activeLanguage) &&
                string.Equals(options[i].Tag, activeLanguage, StringComparison.OrdinalIgnoreCase);
            Rectangle bounds = form.FindCaptionLanguageTarget(options[i].Tag);
            if (active && bounds != Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the active caption language must not be clickable: " + options[i].Tag);
            }

            if (!active && bounds == Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: caption language option is not clickable: " + options[i].Tag);
            }
        }
    }

    private Rectangle FindCaptionLanguageTarget(string tag)
    {
        for (int i = 0; i < this.hitTargets.Count; i++)
        {
            if (this.hitTargets[i].Action == CaptionsHitAction.CaptionLanguageSet &&
                string.Equals(this.hitTargets[i].Payload, tag, StringComparison.OrdinalIgnoreCase))
            {
                return this.hitTargets[i].Bounds;
            }
        }

        return Rectangle.Empty;
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
            if (form.lastDrawnHistoryCount > MaxHistoryEntries || form.lastDrawnHistoryCount < 1)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: history must stay within the " + MaxHistoryEntries +
                    " entry cap, drew " + form.lastDrawnHistoryCount);
            }

            // The point of the rework: with rows at their measured height the list fills the area
            // instead of spreading three entries across it. Four is a floor this board clears at its
            // default size with room to spare; asserting the exact count would just re-encode the
            // font metrics.
            if (form.lastDrawnHistoryCount < 4)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the history area must fill with entries, drew " +
                    form.lastDrawnHistoryCount);
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
