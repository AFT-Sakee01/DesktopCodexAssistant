using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// Baseline-aligned text for the Radar windows' bottom metadata row (LLM/RC/brand/auxiliary gray
// labels). Shared by CodexRadarForm's shared window (both Codex and Claude software modes) and the
// standalone ClaudeRadarForm so all three presentations use one identical algorithm.
//
// Why not per-string ink centering (the pre-1.0.5.11 approach, ported here in 1.0.5.11 then found
// wrong): centering each string's own rendered ink bounding box makes the INK CENTERS coincide, not
// the baselines. A string with a descender (e.g. "LLM:Op4.8H" - the 'p') has a taller ink box, so
// holding its center fixed shifts its whole glyph body - and its baseline - upward; a string with
// no descender (e.g. "DS:472") keeps its baseline lower. The result is the descender-less items
// visibly sink relative to the descender ones, even though every item shares one rect.
//
// Correct model: plain GDI+ StringFormat center already gives every string the SAME baseline,
// because the font line box (ascent+descent) is a font constant independent of the glyphs drawn -
// descenders simply extend below that shared baseline, which is exactly how aligned text reads. The
// only thing plain centering gets slightly wrong is that the visual cap mass can float high or low
// in the band. So we apply ONE row-uniform vertical correction (measured once from a fixed caps
// reference, cached, identical for every item because they share font size, style family and rect
// height) on top of plain centering. Uniform offset + string-independent baseline => every item's
// baseline stays identical while the cap band sits centered.
internal static class RadarBottomInfoTextRenderer
{
    private const int AlphaThreshold = 8;
    // Caps + digits + separators, no ascender-beyond-cap and no descender: represents the dominant
    // glyph mass of these labels so the cap band lands centered in the row.
    private const string CapBandReference = "RC08";

    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, float> CapBandOffsetCache =
        new Dictionary<string, float>(StringComparer.Ordinal);

    public static void DrawInkCenteredText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        string value = text ?? string.Empty;
        if (value.Length == 0 || g == null || font == null || brush == null ||
            rect.Width <= 1.0f || rect.Height <= 1.0f)
        {
            return;
        }

        float offsetY = GetCapBandOffsetY(g, font, rect.Height);
        if (Math.Abs(offsetY) < 0.2f)
        {
            DrawCenteredContent(g, value, font, brush, rect);
            return;
        }

        GraphicsState state = g.Save();
        try
        {
            g.TranslateTransform(0.0f, offsetY);
            DrawCenteredContent(g, value, font, brush, rect);
        }
        finally
        {
            g.Restore(state);
        }
    }

    // Routes the actual glyph painting. A pure single-script label (all the Latin RC/LLM/brand items,
    // plus a wholly CJK one) goes through one plain centered DrawString, unchanged. Only a string that
    // mixes CJK and non-CJK in one cell - the Codex-mode "RS:<n> <hours>小时/<days>天" auxiliary - is
    // painted run-by-run: GDI+ AND GDI both float the CJK run upward when it shares a single draw call
    // with Latin (a mixed-script line-layout effect of Microsoft YaHei UI, confirmed in both engines),
    // yet each script drawn on its own lands on the identical baseline. So we split at the script
    // boundary and paint each run with the same vertical centering, restoring one shared baseline.
    private static void DrawCenteredContent(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        if (!IsMixedScript(text))
        {
            DrawCentered(g, text, font, brush, rect);
            return;
        }

        DrawMixedScriptCentered(g, text, font, brush, rect);
    }

    // True only when the string contains at least one CJK ideograph AND at least one non-CJK visible
    // character, i.e. exactly the case that triggers the mixed-script baseline float.
    private static bool IsMixedScript(string text)
    {
        bool hasCjk = false;
        bool hasOther = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsCjk(c))
            {
                hasCjk = true;
            }
            else if (!char.IsWhiteSpace(c))
            {
                hasOther = true;
            }

            if (hasCjk && hasOther)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCjk(char c)
    {
        // CJK Unified Ideographs + Extension A + CJK symbols/punctuation, enough for the hour/day
        // units that appear in the bottom row. Kept deliberately narrow so ASCII/digits never match.
        int r = c;
        return (r >= 0x4E00 && r <= 0x9FFF) ||
            (r >= 0x3400 && r <= 0x4DBF) ||
            (r >= 0x3000 && r <= 0x303F) ||
            (r >= 0xF900 && r <= 0xFAFF);
    }

    // Lays the maximal same-script runs left-to-right, horizontally centered as a whole, each run
    // vertically centered in the full rect height so every run shares one baseline. The trailing run
    // absorbs any rounding so the group stays centered without accumulating a gap.
    private static void DrawMixedScriptCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        List<string> runs = SplitScriptRuns(text);
        float[] widths = new float[runs.Count];
        float total = 0.0f;
        for (int i = 0; i < runs.Count; i++)
        {
            widths[i] = MeasureRunWidth(g, runs[i], font);
            total += widths[i];
        }

        // If the runs cannot fit (should not happen given the row's fit-to-width sizing), fall back to
        // the plain path so trimming/ellipsis still applies instead of overflowing.
        if (total > rect.Width + 0.5f)
        {
            DrawCentered(g, text, font, brush, rect);
            return;
        }

        float x = rect.Left + (rect.Width - total) * 0.5f;
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            format.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            for (int i = 0; i < runs.Count; i++)
            {
                RectangleF runRect = new RectangleF(x, rect.Top, widths[i] + 1.0f, rect.Height);
                g.DrawString(runs[i], font, brush, runRect, format);
                x += widths[i];
            }
        }
    }

    private static float MeasureRunWidth(Graphics g, string run, Font font)
    {
        // GenericTypographic drops the extra padding DrawString/MeasureString add by default, so
        // adjacent runs abut at their true advance width instead of drifting apart.
        using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
        {
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return g.MeasureString(run, font, int.MaxValue, format).Width;
        }
    }

    private static List<string> SplitScriptRuns(string text)
    {
        List<string> runs = new List<string>();
        int start = 0;
        bool currentCjk = IsCjk(text[0]);
        for (int i = 1; i < text.Length; i++)
        {
            bool cjk = IsCjk(text[i]);
            // Whitespace joins whichever run precedes it so a space never becomes its own run.
            if (char.IsWhiteSpace(text[i]))
            {
                cjk = currentCjk;
            }

            if (cjk != currentCjk)
            {
                runs.Add(text.Substring(start, i - start));
                start = i;
                currentCjk = cjk;
            }
        }

        runs.Add(text.Substring(start));
        return runs;
    }

    // One row-uniform vertical correction that centers the cap band. Deliberately keyed by font
    // FAMILY + SIZE + rect height only - NOT style - and measured against a style-normalized (Bold)
    // reference. Cap height and baseline are style-independent design metrics, so bold, italic and
    // regular items of the same family must share one offset; keying by style (as the first 1.0.5.12
    // cut did) let an italic brand ("Codex") round to a different offset than its bold RS/RC/LLM
    // neighbors at some scales, sinking it by ~1px at the user's DPI even though it matched at the
    // 2.0 render scale. Family+size+height alone guarantees every item in a row - brand included -
    // receives the identical offset at every scale.
    private static float GetCapBandOffsetY(Graphics g, Font font, float rectHeight)
    {
        string family = font.FontFamily != null ? font.FontFamily.Name : string.Empty;
        string key = family + "|" +
            font.Size.ToString("0.0", CultureInfo.InvariantCulture) + "|" +
            ((int)font.Unit).ToString(CultureInfo.InvariantCulture) + "|" +
            ((int)Math.Round(rectHeight)).ToString(CultureInfo.InvariantCulture);
        lock (CacheLock)
        {
            float cached;
            if (CapBandOffsetCache.TryGetValue(key, out cached))
            {
                return cached;
            }
        }

        float offsetY = MeasureCapBandOffsetY(g, font, rectHeight);
        lock (CacheLock)
        {
            CapBandOffsetCache[key] = offsetY;
        }

        return offsetY;
    }

    private static float MeasureCapBandOffsetY(Graphics g, Font font, float rectHeight)
    {
        // Normalize to Bold so the offset is identical no matter which item (bold body vs italic
        // brand) first populates the cache for a given family/size/height.
        Font reference = font;
        bool ownsReference = false;
        try
        {
            if (font.FontFamily != null && font.Style != FontStyle.Bold &&
                font.FontFamily.IsStyleAvailable(FontStyle.Bold))
            {
                reference = new Font(font.FontFamily, font.Size, FontStyle.Bold, font.Unit);
                ownsReference = true;
            }

            float top;
            float bottom;
            // A generous reference width guarantees the caps reference is never horizontally clipped.
            SizeF referenceRect = new SizeF(Math.Max(64.0f, reference.Size * 8.0f), rectHeight);
            if (!TryMeasureInkVerticalExtent(g, CapBandReference, reference, referenceRect, out top, out bottom))
            {
                return 0.0f;
            }

            float desiredCenterY = rectHeight * 0.5f;
            float actualCenterY = (top + bottom + 1.0f) * 0.5f;
            float offsetY = desiredCenterY - actualCenterY;
            float bound = Math.Max(1.0f, rectHeight * 0.35f);
            if (Math.Abs(offsetY) > bound)
            {
                return 0.0f;
            }

            return Math.Abs(offsetY) < 0.2f ? 0.0f : offsetY;
        }
        finally
        {
            if (ownsReference)
            {
                reference.Dispose();
            }
        }
    }

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(text ?? string.Empty, font, brush, rect, format);
        }
    }

    // Rasterizes text centered in a rect the size of rectSize and returns the topmost/bottommost
    // rows that contain ink, in that rect's local coordinates (margin removed).
    private static bool TryMeasureInkVerticalExtent(
        Graphics sourceGraphics,
        string text,
        Font font,
        SizeF rectSize,
        out float top,
        out float bottom)
    {
        top = 0.0f;
        bottom = 0.0f;
        if (sourceGraphics == null || string.IsNullOrEmpty(text) || font == null ||
            rectSize.Width <= 1.0f || rectSize.Height <= 1.0f)
        {
            return false;
        }

        float margin = Math.Max(2.0f, font.Size * 0.5f);
        int width = Math.Max(4, (int)Math.Ceiling(rectSize.Width + margin * 2.0f));
        int height = Math.Max(4, (int)Math.Ceiling(rectSize.Height + margin * 2.0f));

        using (Bitmap bitmap = new Bitmap(width, height))
        using (Graphics measureGraphics = Graphics.FromImage(bitmap))
        using (SolidBrush measureBrush = new SolidBrush(Color.White))
        {
            measureGraphics.Clear(Color.Transparent);
            measureGraphics.SmoothingMode = sourceGraphics.SmoothingMode;
            measureGraphics.PixelOffsetMode = sourceGraphics.PixelOffsetMode;
            measureGraphics.TextRenderingHint = sourceGraphics.TextRenderingHint;
            measureGraphics.CompositingQuality = sourceGraphics.CompositingQuality;
            DrawCentered(
                measureGraphics,
                text,
                font,
                measureBrush,
                new RectangleF(margin, margin, rectSize.Width, rectSize.Height));

            int minY = height;
            int maxY = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= AlphaThreshold)
                    {
                        continue;
                    }

                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    break;
                }

                // Continue scanning: we only break the inner loop; still need full vertical range.
            }

            if (maxY < minY)
            {
                return false;
            }

            top = minY - margin;
            bottom = maxY - margin;
            return true;
        }
    }

    // Measures the topmost ink row of a string as actually drawn by DrawInkCenteredText into a rect,
    // used only by the self-test to prove baselines/cap-tops align across dissimilar strings.
    private static int MeasureDrawnInkTop(Font font, string text, SizeF rectSize)
    {
        int width = Math.Max(4, (int)Math.Ceiling(rectSize.Width + 8.0f));
        int height = Math.Max(4, (int)Math.Ceiling(rectSize.Height + 8.0f));
        using (Bitmap bitmap = new Bitmap(width, height))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            DrawInkCenteredText(g, text, font, brush, new RectangleF(4.0f, 4.0f, rectSize.Width, rectSize.Height));
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y).A > AlphaThreshold)
                    {
                        return y;
                    }
                }
            }

            return -1;
        }
    }

    // Reproduces the pre-1.0.5.11 per-string ink-center offset, so the self-test can demonstrate the
    // old approach diverged the baselines while the new one keeps them aligned.
    private static int MeasureLegacyInkCenteredTop(Font font, string text, SizeF rectSize)
    {
        float top;
        float bottom;
        int width = Math.Max(4, (int)Math.Ceiling(rectSize.Width + 8.0f));
        int height = Math.Max(4, (int)Math.Ceiling(rectSize.Height + 8.0f));
        using (Bitmap bitmap = new Bitmap(width, height))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (!TryMeasureInkVerticalExtent(g, text, font, rectSize, out top, out bottom))
            {
                return -1;
            }

            float legacyOffsetY = rectSize.Height * 0.5f - (top + bottom + 1.0f) * 0.5f;
            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(0.0f, legacyOffsetY);
                DrawCentered(g, text, font, brush, new RectangleF(4.0f, 4.0f, rectSize.Width, rectSize.Height));
            }
            finally
            {
                g.Restore(state);
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y).A > AlphaThreshold)
                    {
                        return y;
                    }
                }
            }

            return -1;
        }
    }

    // Draws a string via the real DrawInkCenteredText path into a fixed rect and returns the
    // bottom-most ink row within the x-column band [xFrom, xTo) of that rect (or -1 if no ink). The
    // self-test uses it to prove the CJK run and the Latin run of one mixed cell share a baseline.
    private static int MeasureRegionInkBottom(
        Font font, string text, SizeF rectSize, float xFromFrac, float xToFrac)
    {
        int width = Math.Max(4, (int)Math.Ceiling(rectSize.Width + 8.0f));
        int height = Math.Max(4, (int)Math.Ceiling(rectSize.Height + 8.0f));
        int xFrom = 4 + (int)(rectSize.Width * xFromFrac);
        int xTo = 4 + (int)(rectSize.Width * xToFrac);
        int bottom = -1;
        using (Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            // A fractional Top reproduces the real panel's non-integer baseline origin, under which
            // the single-DrawString path floats CJK; the fix must hold at this origin too.
            DrawInkCenteredText(g, text, font, brush, new RectangleF(4.0f, 4.2f, rectSize.Width, rectSize.Height));
            for (int y = 0; y < height; y++)
            {
                for (int x = Math.Max(0, xFrom); x < Math.Min(width, xTo); x++)
                {
                    if (bitmap.GetPixel(x, y).A > AlphaThreshold)
                    {
                        bottom = y;
                        break;
                    }
                }
            }
        }

        return bottom;
    }

    internal static void RunSelfTest()
    {
        lock (CacheLock)
        {
            CapBandOffsetCache.Clear();
        }

        // A larger font makes the descender depth (and thus the legacy method's divergence) clearly
        // exceed the 1px anti-aliasing tolerance, matching the high-DPI sizes where the user sees the
        // sink; baseline alignment must still hold cap tops within 1px at this size.
        using (Font font = new Font(FontFamily.GenericSansSerif, 30.0f, FontStyle.Bold))
        using (Bitmap surface = new Bitmap(240, 80))
        using (Graphics g = Graphics.FromImage(surface))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            SizeF rect = new SizeF(200.0f, 40.0f);

            // "DS:472" has no descender; "RC:Op4.8MAX" and "LLM:Op4.8H" contain a 'p' descender.
            // Under baseline alignment their cap tops must coincide (all start with capitals), so the
            // measured ink top should match within a 1px anti-aliasing tolerance.
            int dsTop = MeasureDrawnInkTop(font, "DS:472", rect);
            int rcTop = MeasureDrawnInkTop(font, "RC:Op4.8MAX", rect);
            int llmTop = MeasureDrawnInkTop(font, "LLM:Op4.8H", rect);
            int brandTop = MeasureDrawnInkTop(font, "Claude", rect);
            if (dsTop < 0 || rcTop < 0 || llmTop < 0 || brandTop < 0)
            {
                throw new InvalidOperationException("Radar bottom-info baseline self-test failed: could not measure drawn ink for a label.");
            }

            int maxTop = Math.Max(Math.Max(dsTop, rcTop), Math.Max(llmTop, brandTop));
            int minTop = Math.Min(Math.Min(dsTop, rcTop), Math.Min(llmTop, brandTop));
            if (maxTop - minTop > 1)
            {
                throw new InvalidOperationException(
                    "Radar bottom-info baseline self-test failed: cap tops diverge across labels (DS=" +
                    dsTop + ", RC=" + rcTop + ", LLM=" + llmTop + ", brand=" + brandTop + ").");
            }

            // Guard against regressing to the broken per-string ink-center behavior: under that
            // model the no-descender "DS:472" and the descender "LLM:Op4.8H" cap tops must differ.
            int legacyDsTop = MeasureLegacyInkCenteredTop(font, "DS:472", rect);
            int legacyLlmTop = MeasureLegacyInkCenteredTop(font, "LLM:Op4.8H", rect);
            if (legacyDsTop < 0 || legacyLlmTop < 0)
            {
                throw new InvalidOperationException("Radar bottom-info baseline self-test failed: legacy reference measurement did not resolve.");
            }

            if (Math.Abs(legacyDsTop - legacyLlmTop) <= 1)
            {
                throw new InvalidOperationException(
                    "Radar bottom-info baseline self-test failed: legacy ink-center reference did not diverge, so the alignment assertion would be meaningless (legacyDS=" +
                    legacyDsTop + ", legacyLLM=" + legacyLlmTop + ").");
            }

            // Drawing must never throw for empty/short/oversized/degenerate input.
            DrawInkCenteredText(g, string.Empty, font, brush, new RectangleF(0.0f, 0.0f, rect.Width, rect.Height));
            DrawInkCenteredText(g, "RC:--", font, brush, new RectangleF(0.0f, 0.0f, rect.Width, rect.Height));
            DrawInkCenteredText(g, "A label far wider than the item rectangle", font, brush, new RectangleF(0.0f, 0.0f, rect.Width, rect.Height));
            DrawInkCenteredText(g, "LLM:5.6SM", font, brush, new RectangleF(0.0f, 0.0f, 0.0f, 0.0f));
        }

        RunMixedStyleAlignmentSelfTest();
        RunMixedScriptBaselineSelfTest();

        lock (CacheLock)
        {
            CapBandOffsetCache.Clear();
        }
    }

    // The Codex-mode "RS:<n> <hours>小时/<days>天" auxiliary cell mixes Latin digits with CJK unit
    // characters in one label. A single GDI+/GDI draw call floats the CJK run upward off the Latin
    // baseline (worst at the small bottom-row sizes); DrawMixedScriptCentered splits the run and
    // restores one baseline. Assert the CJK run's ink bottom matches the leading Latin run's within a
    // small tolerance, and that IsMixedScript / SplitScriptRuns classify the label correctly.
    private static void RunMixedScriptBaselineSelfTest()
    {
        if (!IsMixedScript("RS:3 17小时") || IsMixedScript("RC:5.5M") ||
            IsMixedScript("小时") || IsMixedScript("LLM:5.6SM"))
        {
            throw new InvalidOperationException(
                "Radar bottom-info mixed-script self-test failed: IsMixedScript misclassified a label.");
        }

        // "17小时": the Latin "17" occupies the left ~45%, the CJK "小时" the right ~55%. Sizes span the
        // shrink range the bottom row actually uses; the smallest is where the float is most severe.
        float[] sizes = new float[] { 11.0f, 11.76f, 13.0f, 16.0f, 22.0f };
        foreach (float size in sizes)
        {
            lock (CacheLock)
            {
                CapBandOffsetCache.Clear();
            }

            using (Font font = new Font(DesignTokensUiFamily(), size, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF rect = new SizeF(Math.Max(80.0f, size * 8.0f), size * 2.2f);
                int latinBottom = MeasureRegionInkBottom(font, "17小时", rect, 0.0f, 0.42f);
                int cjkBottom = MeasureRegionInkBottom(font, "17小时", rect, 0.55f, 1.0f);
                if (latinBottom < 0 || cjkBottom < 0)
                {
                    // Tiny sizes can render a blank column band; skip only those.
                    continue;
                }

                if (Math.Abs(latinBottom - cjkBottom) > 2)
                {
                    throw new InvalidOperationException(
                        "Radar bottom-info mixed-script self-test failed: CJK run floats off the Latin baseline at size " +
                        size.ToString("0.0", CultureInfo.InvariantCulture) +
                        " (latinBottom=" + latinBottom + ", cjkBottom=" + cjkBottom + ").");
                }
            }
        }
    }

    private static string DesignTokensUiFamily()
    {
        // Mirror the runtime UI font family without a hard dependency; fall back to a generic family
        // if it is unavailable so the self-test still runs on machines lacking it.
        const string preferred = "Microsoft YaHei UI";
        try
        {
            using (FontFamily family = new FontFamily(preferred))
            {
                return family.Name;
            }
        }
        catch (ArgumentException)
        {
            return FontFamily.GenericSansSerif.Name;
        }
    }

    // The shared Codex window's Codex mode draws the brand ("Codex") in Italic while RS/RC/LLM stay
    // Bold. Their baselines must coincide at every scale, not just the 2.0 render scale - the earlier
    // style-keyed offset failed this at some sizes. Sweep representative sizes and assert the italic
    // brand and bold body cap tops align within 1px.
    private static void RunMixedStyleAlignmentSelfTest()
    {
        float[] sizes = new float[] { 8.5f, 11.0f, 13.0f, 16.0f, 21.0f, 26.0f, 32.0f };
        foreach (float size in sizes)
        {
            lock (CacheLock)
            {
                CapBandOffsetCache.Clear();
            }

            using (Font bold = new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold))
            using (Font italic = new Font(FontFamily.GenericSansSerif, size, FontStyle.Italic))
            {
                SizeF rect = new SizeF(Math.Max(80.0f, size * 8.0f), size * 1.4f);
                int brandTop = MeasureDrawnInkTop(italic, "Codex", rect);
                int rsTop = MeasureDrawnInkTop(bold, "RS:--", rect);
                int rcTop = MeasureDrawnInkTop(bold, "RC:5.5M", rect);
                int llmTop = MeasureDrawnInkTop(bold, "LLM:5.6SM", rect);
                if (brandTop < 0 || rsTop < 0 || rcTop < 0 || llmTop < 0)
                {
                    // Very small sizes can render blank caps for some glyphs; skip only those.
                    continue;
                }

                int hi = Math.Max(Math.Max(brandTop, rsTop), Math.Max(rcTop, llmTop));
                int lo = Math.Min(Math.Min(brandTop, rsTop), Math.Min(rcTop, llmTop));
                if (hi - lo > 1)
                {
                    throw new InvalidOperationException(
                        "Radar bottom-info baseline self-test failed: italic brand and bold body cap tops diverge at size " +
                        size.ToString("0.0", CultureInfo.InvariantCulture) +
                        " (Codex=" + brandTop + ", RS=" + rsTop + ", RC=" + rcTop + ", LLM=" + llmTop + ").");
                }
            }
        }
    }
}
