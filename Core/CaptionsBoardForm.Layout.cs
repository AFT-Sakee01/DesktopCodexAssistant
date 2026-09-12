using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

// Board content finalized across design-review rounds (see this board's originating brief): header
// (title + running dot + time-since-last-success), a divider, a single-row toolbar (context-aware
// toggle, rounds stepper, model chip, separator, start/stop, close, attribution), a failure alert
// strip that takes zero height when there is no recent failure, an article toolbar, and the article
// itself -- translation on top, source language below -- filling whatever height is left.
//
// The article replaced a list of recent rows from the translator's own history database. That list
// showed the same sentences this app already renders on its caption strip, in a worse form: the DB
// carries no reliable order for rewritten rows, so the list would visibly reshuffle. The article is
// built instead from the settled sentences this app latched itself (see CaptionTranscript), which
// is the only ordering that matches what the user actually read.
//
// No shared "OledVariantPainting" drawing-helper file exists in this codebase (checked before
// writing this): every board keeps its own small RoundedRectangle-based drawing helpers in its own
// .Layout.cs, matching ResetSpeedBoardForm.Layout.cs and GuardBoardForm.Layout.cs, which this
// file's DrawToggle/DrawStepper/toolbar-button shapes copy.
internal sealed partial class CaptionsBoardForm
{
    private const string AttributionText = "GenieX · NPU";

    // Width of the two square paging buttons. Glyph-only because the article toolbar has to carry
    // eight controls plus a stepper at the narrowest board width this layout supports.
    private const int PageButtonLogicalWidth = 22;

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

        int articleToolbarHeight = S(21);
        Rectangle articleToolbar = new Rectangle(content.Left, y, content.Width, articleToolbarHeight);
        y = articleToolbar.Bottom + S(6);

        Rectangle articleArea = new Rectangle(content.Left, y, content.Width, Math.Max(1, content.Bottom - y));

        DrawHeader(g, header, titleFont, bodyFont, monoFont);
        DrawServiceStatusRow(g, statusRow, labelFont, recordHitTargets);
        DrawToolbar(g, toolbar, labelFont, bodyFont, monoFont, glyphFont, recordHitTargets);
        DrawCaptionSourceRow(g, captionSourceRow, labelFont, recordHitTargets);
        if (hasFailure)
        {
            DrawFailureAlert(g, alert, smallFont, monoSmallFont);
        }

        // The article is drawn before the toolbar that sits above it: the page indicator and the
        // enabled state of the two paging buttons come from measurements only the article pass can
        // make, and drawing the toolbar first would leave both one frame behind.
        DrawArticle(g, articleArea, strongFont, smallFont, monoSmallFont);
        DrawArticleToolbar(g, articleToolbar, smallFont, smallFont, monoSmallFont, glyphFont, recordHitTargets);

        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.Captions, this.LayerScale);
    }

    private void DrawHeader(Graphics g, Rectangle bounds, Font titleFont, Font bodyFont, Font monoFont)
    {
        // 与本文件里芯片标签同一套理由：GenericTypographic 的量宽比 NoWrap/Ellipsis 的
        // DrawString 实际所需更紧，标题字号又大，差值随字号放大——留窄了会被截成 CAPTIO…。
        int titleWidth = MeasureTextWidth(g, BoardTitle, titleFont) + S(14);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            g.DrawString(BoardTitle, titleFont, titleBrush, new Rectangle(bounds.Left, bounds.Top, titleWidth, bounds.Height), near);
        }

        // 状态点跟着标题的实测宽度走：原来是写死的 S(46)，只对两个汉字的「字幕」成立，
        // 换成英文标题后必然叠压。
        int statusLeft = bounds.Left + titleWidth + S(6);
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
            Rectangle stepperBounds = DrawStepper(
                g,
                x,
                bounds.Top,
                bounds.Height,
                stepperValue,
                monoFont,
                glyphFont,
                busy,
                recordHitTargets,
                CaptionsHitAction.NumContextsMinus,
                CaptionsHitAction.NumContextsPlus);
            x = stepperBounds.Right + groupGap;

            bool modelPending = busy && this.pendingAction == CaptionsHitAction.ModelSet;
            string modelLabel = modelPending ? "切换中…" : "模型";
            int modelLabelWidth = MeasureTextWidth(g, modelLabel, labelFont) + S(8);
            g.DrawString(modelLabel, labelFont, labelBrush, new Rectangle(x, bounds.Top, modelLabelWidth, bounds.Height), near);
            x += modelLabelWidth + gap;

            x = DrawModelSegments(g, x, bounds, bodyFont, busy, recordHitTargets) + groupGap;
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

    private Rectangle DrawStepper(
        Graphics g,
        int left,
        int top,
        int height,
        string value,
        Font valueFont,
        Font glyphFont,
        bool busy,
        bool recordHitTargets,
        CaptionsHitAction minusAction,
        CaptionsHitAction plusAction)
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
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = minusBounds, Action = minusAction });
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = plusBounds, Action = plusAction });
        }

        return bounds;
    }

    // One button per installed model, side by side, each applying its own model directly. It used to
    // be a single chip that cycled: with three models installed, reaching the one you wanted took up
    // to two clicks, and every click that was not the last one restarted the translator and reloaded
    // a model you did not want (~10s each). Returns the right edge of the group.
    //
    // Labels come from TranslatorControlReader.BuildModelSegmentLabels (4B / 8B / VL here) because
    // three buttons have to fit where the chip was; the value written to setting.json is always the
    // full model id carried in the hit target payload.
    private int DrawModelSegments(Graphics g, int left, Rectangle row, Font font, bool busy, bool recordHitTargets)
    {
        IList<string> models = this.snapshot.AvailableModels;
        if (models == null || models.Count == 0)
        {
            using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            {
                int width = MeasureTextWidth(g, "无可用模型", font) + S(6);
                g.DrawString("无可用模型", font, mutedBrush, new Rectangle(left, row.Top, width, row.Height), near);
                return left + width;
            }
        }

        string[] labels = TranslatorControlReader.BuildModelSegmentLabels(models);
        int gap = S(3);
        int x = left;
        for (int i = 0; i < models.Count; i++)
        {
            string label = i < labels.Length ? labels[i] : FormatModelDisplayName(models[i]);
            // Measured per label, not an equal share of the row: these labels differ in length by a
            // factor of three ("8B" against "4B-Instruct-2507" on a machine whose names diverge
            // late), and equal shares would either clip the long one or pad the short one.
            int width = MeasureTextWidth(g, label, font) + S(14);
            Rectangle bounds = new Rectangle(x, row.Top, width, row.Height);
            bool active = string.Equals(models[i], this.snapshot.ModelName, StringComparison.Ordinal);
            DrawModelSegment(g, bounds, label, active, font, busy, recordHitTargets, models[i]);
            x = bounds.Right + gap;
        }

        return x - gap;
    }

    private void DrawModelSegment(
        Graphics g,
        Rectangle bounds,
        string label,
        bool active,
        Font font,
        bool busy,
        bool recordHitTargets,
        string modelName)
    {
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
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
            g.DrawString(label, font, textBrush, bounds, center);
        }

        // The model already in effect is inert, exactly like the active caption-source segment:
        // re-applying it costs a translator restart plus a model reload and changes nothing.
        if (recordHitTargets && !busy && !active)
        {
            this.hitTargets.Add(new CaptionsHitTarget
            {
                Bounds = bounds,
                Action = CaptionsHitAction.ModelSet,
                Payload = modelName,
            });
        }
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

    // One row, two groups, split by what they act on. Left of the separator everything acts on the
    // article; right of it everything acts on the caption strip, under a 字幕条 label that says so.
    //
    // The split exists because the first version did not have it: 编辑 and 重置 sat among 导出 and 清除
    // with nothing to say they were about the strip, and they read as "edit the text" and "reset the
    // text" -- the two most alarming things a button next to a transcript could mean. Renamed to
    // 调整/复位 for the same reason: 编辑 and 重置 are what you call operations on content.
    private void DrawArticleToolbar(Graphics g, Rectangle bounds, Font labelFont, Font bodyFont, Font monoFont, Font glyphFont, bool recordHitTargets)
    {
        int gap = S(4);
        int groupGap = S(9);

        // The strip group is measured and right-aligned first, so the article group on the left knows
        // where it has to stop.
        bool editing = IsCaptionOverlayEditing;
        bool shown = IsCaptionOverlayDisplayEnabled;
        bool hoverAutoHide = IsCaptionOverlayHoverAutoHideEnabled;
        // The button says what pressing it does, not what the current state is.
        string displayLabel = shown ? "隐藏" : "显示";
        string editLabel = editing ? "完成" : "调整";
        string stripLabel = "字幕条";
        string linesLabel = "句数";
        int settledLines = this.CurrentSettings == null
            ? WidgetSettings.DefaultCaptionOverlaySettledLines
            : this.CurrentSettings.CaptionOverlaySettledLines;
        string settledText = settledLines.ToString(CultureInfo.InvariantCulture);

        int stripLabelWidth = MeasureTextWidth(g, stripLabel, labelFont) + S(8);
        int displayWidth = MeasureTextWidth(g, displayLabel, bodyFont) + S(14);
        int hoverWidth = MeasureTextWidth(g, "避让", bodyFont) + S(14);
        int editWidth = MeasureTextWidth(g, editLabel, bodyFont) + S(14);
        int resetWidth = MeasureTextWidth(g, "复位", bodyFont) + S(14);
        int collapseWidth = MeasureTextWidth(g, "收起", bodyFont) + S(14);
        int linesLabelWidth = MeasureTextWidth(g, linesLabel, labelFont) + S(8);
        int stepperWidth = MeasureStepperWidth(g, settledText, monoFont, bounds.Height);

        // The article group, measured now so the row knows whether both groups fit before it commits
        // to drawing either. The buttons are the controls; the two section labels only name them.
        int articleLabelWidth = S(40) + gap;
        int pageWidth = S(PageButtonLogicalWidth);
        string pageText = (this.articleMaxPageBack - this.articlePageBack + 1).ToString(CultureInfo.InvariantCulture) +
            "/" + (this.articleMaxPageBack + 1).ToString(CultureInfo.InvariantCulture);
        int pageTextWidth = Math.Max(S(26), MeasureTextWidth(g, pageText, monoFont) + S(6));
        int exportWidth = MeasureTextWidth(g, "导出", bodyFont) + S(14);
        bool armed = IsClearArmed;
        string clearLabel = armed ? "确认" : "清除";
        int clearWidth = MeasureTextWidth(g, clearLabel, bodyFont) + S(14);

        // Labels are given up before anything the user can press, and the article label goes first: the
        // text it names is directly below it, while the strip group has no such context. A narrow board
        // therefore loses words, never buttons -- an unreachable control would be a worse trade than an
        // unlabelled group. Widths are measured rather than assumed because the labels are CJK and the
        // font follows LayerScale.
        bool showArticleLabel = true;
        bool showStripLabel = true;
        bool showLinesLabel = true;
        // 「收起」是这一行里唯一有替代路径的控件——收不到时用户仍可直接最小化那扇窗——
        // 所以它是标签全部让完之后第一个被放弃的按钮。其余按钮一个都不能丢：
        // 够不着的控件比没有标签的分组更糟。
        bool showCollapse = true;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int articleGroup = (showArticleLabel ? articleLabelWidth : 0) + pageWidth + gap + pageWidth + gap +
                pageTextWidth + groupGap + exportWidth + gap + clearWidth;
            int stripGroup = (showStripLabel ? stripLabelWidth + gap : 0) + displayWidth + gap + hoverWidth +
                gap + editWidth + gap + resetWidth + (showCollapse ? gap + collapseWidth : 0) + groupGap +
                (showLinesLabel ? linesLabelWidth + gap : 0) + stepperWidth;
            if (articleGroup + groupGap + stripGroup <= bounds.Width)
            {
                break;
            }

            if (showArticleLabel)
            {
                showArticleLabel = false;
            }
            else if (showLinesLabel)
            {
                showLinesLabel = false;
            }
            else if (showStripLabel)
            {
                showStripLabel = false;
            }
            else
            {
                showCollapse = false;
            }
        }

        int stripGroupWidth = (showStripLabel ? stripLabelWidth + gap : 0) + displayWidth + gap + hoverWidth +
            gap + editWidth + gap + resetWidth + (showCollapse ? gap + collapseWidth : 0) + groupGap +
            (showLinesLabel ? linesLabelWidth + gap : 0) + stepperWidth;
        int stripLeft = bounds.Right - stripGroupWidth;

        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateFormat(StringAlignment.Near))
        {
            if (showArticleLabel)
            {
                g.DrawString("文 章", labelFont, labelBrush, new Rectangle(bounds.Left, bounds.Top, S(40), bounds.Height), near);
            }

            if (showStripLabel)
            {
                g.DrawString(stripLabel, labelFont, labelBrush, new Rectangle(stripLeft, bounds.Top, stripLabelWidth, bounds.Height), near);
            }
        }

        int sx = stripLeft + (showStripLabel ? stripLabelWidth + gap : 0);
        // Hiding only stops the drawing: the reader keeps polling and the article keeps recording,
        // which is why this is a button here and not the master switch in the settings window. Muted
        // while hidden, so a glance at the row says which state the strip is in.
        Rectangle displayBounds = new Rectangle(sx, bounds.Top, displayWidth, bounds.Height);
        DrawToolbarButton(
            g,
            displayBounds,
            displayLabel,
            shown ? DesignTokens.Colors.Border : EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions),
            bodyFont,
            false);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = displayBounds, Action = CaptionsHitAction.OverlayDisplayToggle });
        }

        sx = displayBounds.Right + gap;
        // 避让 is a standing preference, not an action, so it is drawn filled while on rather than
        // changing its label -- the same shape the model and caption-source segments use for "this
        // one is in effect". Pressing it toggles; it is never inert, including while hidden, because
        // it is a setting for the next time the strip is on screen rather than a thing you do to it.
        Rectangle hoverBounds = new Rectangle(sx, bounds.Top, hoverWidth, bounds.Height);
        DrawStateButton(g, hoverBounds, "避让", hoverAutoHide, bodyFont);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = hoverBounds, Action = CaptionsHitAction.OverlayHoverAutoHideToggle });
        }

        sx = hoverBounds.Right + gap;
        // Editing is a mode, so the button says what pressing it will do: 调整 to enter, 完成 to leave
        // and save. Green while editing, because that press is the one that commits the new rectangle.
        // Both placement buttons go dead while the strip is hidden -- there is nothing on screen to
        // place, and a live button that silently does nothing is worse than one that looks disabled.
        Rectangle editBounds = new Rectangle(sx, bounds.Top, editWidth, bounds.Height);
        DrawToolbarButton(
            g,
            editBounds,
            editLabel,
            editing ? DesignTokens.Colors.Success : EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions),
            bodyFont,
            !shown);
        if (recordHitTargets && shown)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = editBounds, Action = CaptionsHitAction.OverlayEditToggle });
        }

        sx = editBounds.Right + gap;
        Rectangle resetBounds = new Rectangle(sx, bounds.Top, resetWidth, bounds.Height);
        DrawToolbarButton(g, resetBounds, "复位", DesignTokens.Colors.Border, bodyFont, !shown);
        if (recordHitTargets && shown)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = resetBounds, Action = CaptionsHitAction.OverlayReset });
        }

        // 收起 Windows 自己那扇实时辅助字幕窗。它不是覆盖条的控件，所以不随 shown 变灰：
        // 横幅藏起来的时候那扇窗一样碍事，甚至更需要收。
        if (showCollapse)
        {
            sx = resetBounds.Right + gap;
            Rectangle collapseBounds = new Rectangle(sx, bounds.Top, collapseWidth, bounds.Height);
            DrawToolbarButton(
                g,
                collapseBounds,
                "收起",
                EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions),
                bodyFont,
                false);
            if (recordHitTargets)
            {
                this.hitTargets.Add(new CaptionsHitTarget { Bounds = collapseBounds, Action = CaptionsHitAction.LiveCaptionsCollapse });
            }
        }

        if (showLinesLabel)
        {
            using (SolidBrush labelBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat near = CreateFormat(StringAlignment.Near))
            {
                g.DrawString(
                    linesLabel,
                    labelFont,
                    labelBrush,
                    new Rectangle(resetBounds.Right + groupGap, bounds.Top, linesLabelWidth, bounds.Height),
                    near);
            }
        }

        DrawStepper(
            g,
            bounds.Right - stepperWidth,
            bounds.Top,
            bounds.Height,
            settledText,
            monoFont,
            glyphFont,
            false,
            recordHitTargets,
            CaptionsHitAction.SettledLinesMinus,
            CaptionsHitAction.SettledLinesPlus);

        using (Pen separatorPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 140), Math.Max(1.0f, this.LayerScale)))
        {
            int separatorX = stripLeft - groupGap / 2;
            g.DrawLine(separatorPen, separatorX, bounds.Top + S(2), separatorX, bounds.Bottom - S(2));
        }

        int x = bounds.Left + (showArticleLabel ? articleLabelWidth : 0);

        // Up walks back through the article, down returns toward the live tail -- the same direction
        // the text itself scrolls, so the arrows mean what they look like.
        Rectangle pageUpBounds = new Rectangle(x, bounds.Top, pageWidth, bounds.Height);
        bool canPageUp = this.articlePageBack < this.articleMaxPageBack;
        DrawToolbarButton(g, pageUpBounds, "▲", DesignTokens.Colors.Border, bodyFont, !canPageUp);
        if (recordHitTargets && canPageUp)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = pageUpBounds, Action = CaptionsHitAction.ArticlePageUp });
        }

        x = pageUpBounds.Right + gap;
        Rectangle pageDownBounds = new Rectangle(x, bounds.Top, pageWidth, bounds.Height);
        bool canPageDown = this.articlePageBack > 0;
        DrawToolbarButton(g, pageDownBounds, "▼", DesignTokens.Colors.Border, bodyFont, !canPageDown);
        if (recordHitTargets && canPageDown)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = pageDownBounds, Action = CaptionsHitAction.ArticlePageDown });
        }

        x = pageDownBounds.Right + gap;
        using (SolidBrush pageBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.DrawString(pageText, monoFont, pageBrush, new Rectangle(x, bounds.Top, pageTextWidth, bounds.Height), centered);
        }

        x += pageTextWidth + groupGap;
        Rectangle exportBounds = new Rectangle(x, bounds.Top, exportWidth, bounds.Height);
        DrawToolbarButton(g, exportBounds, "导出", DesignTokens.Colors.Border, bodyFont, false);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = exportBounds, Action = CaptionsHitAction.ArticleExport });
        }

        x = exportBounds.Right + gap;
        // An armed clear says so on the button itself, not only in the status strip: the button is
        // where the next click is going to land.
        Rectangle clearBounds = new Rectangle(x, bounds.Top, clearWidth, bounds.Height);
        DrawToolbarButton(g, clearBounds, clearLabel, DesignTokens.Colors.Danger, bodyFont, false);
        if (recordHitTargets)
        {
            this.hitTargets.Add(new CaptionsHitTarget { Bounds = clearBounds, Action = CaptionsHitAction.ArticleClear });
        }
    }

    // A button whose label never changes because it shows a standing on/off preference: filled and
    // accented while on, plain while off. DrawToolbarButton cannot express this -- its only variable
    // is the border colour, which at a glance does not read as "switched on".
    private void DrawStateButton(Graphics g, Rectangle bounds, string label, bool active, Font font)
    {
        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
        using (GraphicsPath path = RoundedRectangle(RectangleF.Inflate(bounds, -1.0f, -1.0f), S(4)))
        using (SolidBrush fill = new SolidBrush(active
            ? DesignTokens.WithAlpha(accent, 46)
            : DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen border = new Pen(
            DesignTokens.WithAlpha(active ? accent : DesignTokens.Colors.Border, active ? 205 : 130),
            Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush textBrush = new SolidBrush(active ? accent : DesignTokens.Colors.TextMuted))
        using (StringFormat centered = CreateFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(label, font, textBrush, bounds, centered);
        }
    }

    // The stepper's own width, needed before it is drawn so the row can be right-aligned against it.
    private int MeasureStepperWidth(Graphics g, string value, Font valueFont, int height)
    {
        int stepWidth = Math.Max(S(14), height);
        int valueWidth = Math.Max(S(28), MeasureTextWidth(g, value, valueFont) + S(6));
        return stepWidth * 2 + valueWidth;
    }

    // The article: translation above, source language below, split down the middle. Both halves page
    // together on one page index -- they carry the same sentences, so paging them separately would
    // just be two ways to lose the correspondence between them.
    private void DrawArticle(Graphics g, Rectangle bounds, Font translatedFont, Font originalFont, Font monoFont)
    {
        this.lastDrawnArticleLineCount = 0;
        CaptionTranscript transcript = ResolveTranscript();
        if (transcript == null || transcript.Count == 0)
        {
            this.articleMaxPageBack = 0;
            this.articlePageBack = 0;
            using (SolidBrush mutedBrush = new SolidBrush(DesignTokens.Colors.GlyphMuted))
            using (StringFormat center = CreateFormat(StringAlignment.Center))
            {
                g.DrawString("暂无文章 · 定稿的字幕会累积到这里", originalFont, mutedBrush, bounds, center);
            }

            return;
        }

        int gap = S(8);
        int topHeight = Math.Max(1, (bounds.Height - gap) / 2);
        Rectangle topHalf = new Rectangle(bounds.Left, bounds.Top, bounds.Width, topHeight);
        Rectangle bottomHalf = new Rectangle(
            bounds.Left,
            topHalf.Bottom + gap,
            bounds.Width,
            Math.Max(1, bounds.Bottom - topHalf.Bottom - gap));
        DrawDivider(g, bounds.Left, topHalf.Bottom + gap / 2, bounds.Width);

        // Wrapped and drawn against the same width, with a small margin: GenericTypographic
        // measurement runs tighter than the draw path, so a line wrapped to the full box width
        // loses its last glyph to the right edge (see the FitFontSize width-must-match-draw-width
        // rule).
        int textWidth = Math.Max(S(60), bounds.Width - S(8));
        List<string> translatedLines = ResolveArticleLines(g, true, translatedFont, textWidth);
        List<string> originalLines = ResolveArticleLines(g, false, originalFont, textWidth);
        int translatedLineHeight = MeasureLineHeight(g, translatedFont, S(1));
        int originalLineHeight = MeasureLineHeight(g, originalFont, S(1));
        int translatedVisible = Math.Max(1, topHalf.Height / translatedLineHeight);
        int originalVisible = Math.Max(1, bottomHalf.Height / originalLineHeight);

        // The page count is whichever half needs more pages: the two wrap differently, and clamping
        // to the shorter one would make the tail of the longer half unreachable.
        int maxPageBack = Math.Max(
            ResolveMaxPageBack(translatedLines.Count, translatedVisible),
            ResolveMaxPageBack(originalLines.Count, originalVisible));
        this.articleMaxPageBack = maxPageBack;
        if (this.articlePageBack > maxPageBack)
        {
            this.articlePageBack = maxPageBack;
        }

        topHalf.Width = textWidth;
        bottomHalf.Width = textWidth;
        this.lastDrawnArticleLineCount = DrawArticleHalf(
            g, topHalf, translatedLines, translatedFont, translatedLineHeight, translatedVisible, DesignTokens.Colors.TextStrong, 255);
        DrawArticleHalf(
            g, bottomHalf, originalLines, originalFont, originalLineHeight, originalVisible, DesignTokens.Colors.TextMuted, 185);
    }

    private static int ResolveMaxPageBack(int lineCount, int visibleLines)
    {
        if (lineCount <= visibleLines || visibleLines <= 0)
        {
            return 0;
        }

        return (lineCount - 1) / visibleLines;
    }

    // Page 0 is the tail, so the window is measured back from the end of the text rather than forward
    // from its start: new sentences must not shift what page anything is on.
    private int DrawArticleHalf(
        Graphics g,
        Rectangle bounds,
        List<string> lines,
        Font font,
        int lineHeight,
        int visibleLines,
        Color color,
        int alpha)
    {
        int start = Math.Max(0, lines.Count - visibleLines * (this.articlePageBack + 1));
        int count = Math.Min(visibleLines, lines.Count - start);
        if (count <= 0)
        {
            return 0;
        }

        // Drawn through the same GenericTypographic format the lines were wrapped with. The board's
        // usual CreateFormat is GenericDefault, which pads roughly a sixth of an em onto each end of
        // every string -- enough to push the last glyph of an exactly-fitting line off the edge.
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, alpha)))
        using (StringFormat near = new StringFormat(StringFormat.GenericTypographic))
        {
            near.Alignment = StringAlignment.Near;
            near.LineAlignment = StringAlignment.Center;
            near.Trimming = StringTrimming.None;
            near.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            for (int i = 0; i < count; i++)
            {
                Rectangle line = new Rectangle(bounds.Left, bounds.Top + i * lineHeight, bounds.Width, lineHeight);
                g.DrawString(lines[start + i], font, brush, line, near);
            }
        }

        return count;
    }

    private static int ScaleAlpha(int baseAlpha, int scale)
    {
        return DesignTokens.ClampByte(baseAlpha * scale / 255);
    }

    // Delegates to the reader, which owns model-name semantics the same way it owns the caption
    // language table: what an org prefix or a quantisation suffix means is not a drawing concern.
    private static string FormatModelDisplayName(string fullModelName)
    {
        return TranslatorControlReader.FormatModelDisplayName(fullModelName);
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
        VerifyHiddenStripDisablesPlacement(648, 400);
        VerifyArticlePaging(648, 400);
        Console.WriteLine("Captions board layout: PASS hit targets, no overlap, zero-height alert strip when no failure, status-strip targets follow service state, hidden strip disables placement, article paging and empty state");
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
            // A seeded article is part of the densest frame: with nothing to page through, the two
            // paging buttons are inert and would not be checked for overlap at all.
            form.articleFixture = CreateArticleFixture(40);
            form.DrawBoard(g, true);
            form.articlePageBack = 1;
            form.DrawBoard(g, true);

            CaptionsHitAction[] required =
            {
                CaptionsHitAction.ContextAwareToggle,
                CaptionsHitAction.NumContextsMinus,
                CaptionsHitAction.NumContextsPlus,
                CaptionsHitAction.ModelSet,
                CaptionsHitAction.ToggleRunning,
                CaptionsHitAction.Close,
                CaptionsHitAction.GenieXStart,
                CaptionsHitAction.SanitizeProxyStart,
                CaptionsHitAction.LiveCaptionsStart,
                CaptionsHitAction.TranslatorStart,
                CaptionsHitAction.CaptionLanguageSet,
                CaptionsHitAction.ArticleExport,
                CaptionsHitAction.ArticleClear,
                CaptionsHitAction.SettledLinesMinus,
                CaptionsHitAction.SettledLinesPlus,
                CaptionsHitAction.OverlayEditToggle,
                CaptionsHitAction.OverlayReset,
                CaptionsHitAction.OverlayDisplayToggle,
                CaptionsHitAction.OverlayHoverAutoHideToggle,
                CaptionsHitAction.ArticlePageUp,
                CaptionsHitAction.ArticlePageDown,
                CaptionsHitAction.LiveCaptionsCollapse
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
            // Same rule for the model row, where the cost of a needless re-apply is higher still
            // (a translator restart plus a ~10s model reload).
            VerifyModelTargets(form, allUp.AvailableModels, allUp.ModelName);

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

    private static void VerifyModelTargets(CaptionsBoardForm form, IList<string> models, string activeModel)
    {
        for (int i = 0; i < models.Count; i++)
        {
            bool active = string.Equals(models[i], activeModel, StringComparison.Ordinal);
            Rectangle bounds = form.FindPayloadTarget(CaptionsHitAction.ModelSet, models[i]);
            if (active && bounds != Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the active model must not be clickable: " + models[i]);
            }

            if (!active && bounds == Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: model option is not clickable: " + models[i]);
            }
        }
    }

    private Rectangle FindCaptionLanguageTarget(string tag)
    {
        return FindPayloadTarget(CaptionsHitAction.CaptionLanguageSet, tag);
    }

    // Both "pick one of N" rows register one target per option and tell them apart by payload, so
    // finding one means matching the pair.
    private Rectangle FindPayloadTarget(CaptionsHitAction action, string payload)
    {
        for (int i = 0; i < this.hitTargets.Count; i++)
        {
            if (this.hitTargets[i].Action == action &&
                string.Equals(this.hitTargets[i].Payload, payload, StringComparison.OrdinalIgnoreCase))
            {
                return this.hitTargets[i].Bounds;
            }
        }

        return Rectangle.Empty;
    }

    // Hiding the strip must leave the two placement buttons inert: there is nothing on screen to move
    // or restore, and a live button that silently does nothing is worse than one that looks disabled.
    // The 隐藏/显示 button itself stays live, or the state would be a trap with no way out.
    private static void VerifyHiddenStripDisablesPlacement(int logicalWidth, int logicalHeight)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = logicalWidth;
        settings.SpecBoardHeight = logicalHeight;
        settings.CaptionOverlayDisplayEnabled = false;
        settings.Normalize();

        using (CaptionsBoardForm form = new CaptionsBoardForm(null, settings, delegate { return null; }))
        using (Bitmap bitmap = new Bitmap(logicalWidth, logicalHeight, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.Size = new Size(logicalWidth, logicalHeight);
            form.snapshot = CreateFixtureSnapshot();
            form.DrawBoard(g, true);

            if (form.FindHitTarget(CaptionsHitAction.OverlayEditToggle) != Rectangle.Empty ||
                form.FindHitTarget(CaptionsHitAction.OverlayReset) != Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: a hidden strip must not offer 调整/复位.");
            }

            if (form.FindHitTarget(CaptionsHitAction.OverlayDisplayToggle) == Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the 显示 button must stay clickable while hidden.");
            }

            // The stepper is about what the strip keeps, not about placing it, and the article keeps
            // recording either way -- so it stays live. 避让 likewise: it is a preference for the next
            // time the strip is on screen, not an action performed on it now.
            if (form.FindHitTarget(CaptionsHitAction.SettledLinesPlus) == Rectangle.Empty ||
                form.FindHitTarget(CaptionsHitAction.OverlayHoverAutoHideToggle) == Rectangle.Empty)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: 句数 and 避让 must stay clickable while hidden.");
            }
        }
    }

    // The article is the only scrollable surface in this app, so its paging maths has no precedent to
    // borrow from: this proves the page window is anchored at the tail (page 1 of N is the newest
    // text, not the oldest), that paging back moves it, and that a board with no article at all still
    // draws and reports a single page.
    private static void VerifyArticlePaging(int logicalWidth, int logicalHeight)
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
            form.snapshot = CreateFixtureSnapshot();

            form.DrawBoard(g, true);
            if (form.lastDrawnArticleLineCount != 0 || form.articleMaxPageBack != 0)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: an empty article must draw no lines and report one page, drew " +
                    form.lastDrawnArticleLineCount);
            }

            form.articleFixture = CreateArticleFixture(60);
            form.DrawBoard(g, true);
            int firstPageLines = form.lastDrawnArticleLineCount;
            if (firstPageLines < 2)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the article half must fill with lines, drew " + firstPageLines);
            }

            if (form.articleMaxPageBack < 1)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: sixty sentences must not fit on one page.");
            }

            // The newest sentence belongs on the first page shown, which is what makes the article
            // usable while someone is still speaking.
            string tail = form.articleCache.TranslatedLines[form.articleCache.TranslatedLines.Count - 1];
            if (!HalfContainsLine(form, true, tail))
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: page 1 must show the tail of the article.");
            }

            int maxPage = form.articleMaxPageBack;
            form.articlePageBack = maxPage;
            form.DrawBoard(g, true);
            if (HalfContainsLine(form, true, tail))
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: paging to the oldest page still showed the newest line.");
            }

            // Paging past the end must clamp rather than strand the view on a blank page.
            form.articlePageBack = maxPage + 5;
            form.DrawBoard(g, true);
            if (form.articlePageBack != maxPage || form.lastDrawnArticleLineCount <= 0)
            {
                throw new InvalidOperationException(
                    "Captions board layout self-test failed: the page index must clamp to the oldest page, got " +
                    form.articlePageBack);
            }
        }
    }

    // Recomputes the window the last draw painted and reports whether it covered a given line. Reading
    // the drawn pixels back would prove the same thing far less legibly.
    private static bool HalfContainsLine(CaptionsBoardForm form, bool translated, string line)
    {
        List<string> lines = translated ? form.articleCache.TranslatedLines : form.articleCache.OriginalLines;
        int visible = form.lastDrawnArticleLineCount;
        int start = Math.Max(0, lines.Count - visible * (form.articlePageBack + 1));
        for (int i = start; i < Math.Min(lines.Count, start + visible); i++)
        {
            if (string.Equals(lines[i], line, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static CaptionTranscript CreateArticleFixture(int sentences)
    {
        CaptionTranscript transcript = new CaptionTranscript();
        DateTime start = new DateTime(2026, 9, 12, 2, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < sentences; i++)
        {
            // Every eighth sentence lands past the paragraph gap, so the fixture exercises the indent
            // and the paragraph break as well as the wrapping.
            double offset = i * 3.0 + (i / 8) * CaptionTranscript.ParagraphGapSeconds;
            transcript.Append(
                "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 句译文，这一句写得足够长，可以把它折成不止一行。",
                "Sentence " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                    " in the source language, long enough to wrap across more than a single line.",
                start.AddSeconds(offset));
        }

        return transcript;
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
