using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Drawing for the caption strip. Two blocks: the translation, which is what the user is actually
// reading, and the original underneath it at a fraction of the size and opacity.
//
// Every height here is measured from the real font at the real scale (MeasureString with the same
// wrapping format used to draw), never a hand-typed pixel count -- the strip is full screen width and
// its height has to follow however many lines the current sentence wraps to.
internal sealed partial class CaptionOverlayForm
{
    // Ratio, not a second setting: the original line exists to be glanced at, and letting the user
    // set both sizes independently mostly produces combinations where it competes with the
    // translation.
    private const float OriginalFontScale = 0.58f;
    private const int MaxTranslationLines = 3;
    private const int ShadowOffsetLogical = 2;

    private int ResolveRenderWidth()
    {
        return this.renderWidth > 0 ? this.renderWidth : this.Width;
    }

    private int MeasureDesiredHeight(int width)
    {
        using (Bitmap probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(probe))
        {
            return MeasureDesiredHeight(g, width);
        }
    }

    private int MeasureDesiredHeight(Graphics g, int width)
    {
        int padX = S(22);
        int padY = S(12);
        int textWidth = Math.Max(S(80), width - padX * 2);

        Font translationFont = ResolveTranslationFont();
        Font originalFont = ResolveOriginalFont();
        int height = padY * 2 + MeasureBlockHeight(g, BuildTranslationText(), translationFont, textWidth, MaxTranslationLines);
        if (ShouldDrawOriginal())
        {
            height += S(4) + MeasureBlockHeight(g, this.snapshot.OriginalCaption, originalFont, textWidth, 1);
        }

        return Math.Max(S(36), height);
    }

    // Settled sentence and in-progress sentence, in that order, as one string. Measuring and
    // drawing both from the same string is what keeps the two colours on one wrapped paragraph
    // instead of two separately-centred blocks.
    private string BuildTranslationText()
    {
        string previous = (this.snapshot.PreviousTranslation ?? string.Empty).Trim();
        string current = (this.snapshot.TranslatedCaption ?? string.Empty).Trim();
        if (previous.Length == 0)
        {
            return current;
        }

        if (current.Length == 0)
        {
            return previous;
        }

        return previous + " " + current;
    }

    // The in-progress sentence is tinted the same way the translator tints it in its own overlay
    // (OverlayWindow.UpdateTranslationColor): push each channel toward the opposite luminance by
    // 30/40/30 percent. Weighting green hardest is what turns near-white into the mauve the user
    // reads as "not settled yet" -- reproducing the formula rather than hard-coding that colour
    // keeps it correct if this app's text colour ever changes.
    private static Color ResolveUnsettledColor(Color settled)
    {
        double target = 0.299 * settled.R + 0.587 * settled.G + 0.114 * settled.B > 127 ? 0 : 255;
        int r = DesignTokens.ClampByte((int)Math.Round(settled.R + (target - settled.R) * 0.3));
        int g = DesignTokens.ClampByte((int)Math.Round(settled.G + (target - settled.G) * 0.4));
        int b = DesignTokens.ClampByte((int)Math.Round(settled.B + (target - settled.B) * 0.3));
        return Color.FromArgb(settled.A, r, g, b);
    }

    // Draws the paragraph twice with opposite clips: once with the settled run visible and once
    // with the in-progress run visible. GDI+ has no rich-text run model, and measuring each run
    // separately would break the wrap -- a sentence that flows across the boundary has to be laid
    // out as one string or the two halves stop lining up.
    private void DrawTranslationRuns(Graphics g, Font font, Rectangle bounds)
    {
        string previous = (this.snapshot.PreviousTranslation ?? string.Empty).Trim();
        string current = (this.snapshot.TranslatedCaption ?? string.Empty).Trim();
        string full = BuildTranslationText();
        if (full.Length == 0)
        {
            return;
        }

        Color settled = DesignTokens.Colors.TextStrong;
        if (previous.Length == 0 || current.Length == 0)
        {
            // Only one run exists: the settled colour for a settled-only line, the tinted colour
            // when everything on screen is still moving.
            Color single = previous.Length == 0 ? ResolveUnsettledColor(settled) : settled;
            DrawCaptionText(g, full, font, bounds, single, 255, MaxTranslationLines);
            return;
        }

        using (StringFormat format = CreateCaptionFormat(MaxTranslationLines))
        {
            RegionAndRanges(g, font, bounds, full, previous.Length, format);
        }
    }

    private void RegionAndRanges(Graphics g, Font font, Rectangle bounds, string full, int splitIndex, StringFormat format)
    {
        // CharacterRange -> Region is the only way GDI+ exposes "where did this substring land
        // after wrapping". Clipping to it lets one measured layout carry two colours.
        format.SetMeasurableCharacterRanges(new CharacterRange[]
        {
            new CharacterRange(0, splitIndex),
            new CharacterRange(splitIndex, full.Length - splitIndex),
        });

        Region[] regions = g.MeasureCharacterRanges(full, font, bounds, format);
        try
        {
            Region previousClip = g.Clip;
            try
            {
                g.Clip = regions[0];
                DrawCaptionText(g, full, font, bounds, DesignTokens.Colors.TextStrong, 255, MaxTranslationLines);
                g.Clip = regions[1];
                DrawCaptionText(g, full, font, bounds, ResolveUnsettledColor(DesignTokens.Colors.TextStrong), 255, MaxTranslationLines);
            }
            finally
            {
                g.Clip = previousClip;
            }
        }
        finally
        {
            for (int i = 0; i < regions.Length; i++)
            {
                regions[i].Dispose();
            }
        }
    }

    private bool ShouldDrawOriginal()
    {
        return this.CurrentSettings != null &&
            this.CurrentSettings.CaptionOverlayShowOriginal &&
            !string.IsNullOrWhiteSpace(this.snapshot.OriginalCaption);
    }

    private Font ResolveTranslationFont()
    {
        int size = this.CurrentSettings == null ? 22 : this.CurrentSettings.CaptionOverlayFontSize;
        return this.fontCache.GetUi(S((float)size), FontStyle.Bold);
    }

    private Font ResolveOriginalFont()
    {
        int size = this.CurrentSettings == null ? 22 : this.CurrentSettings.CaptionOverlayFontSize;
        return this.fontCache.GetUi(S(Math.Max(8.0f, size * OriginalFontScale)), FontStyle.Regular);
    }

    private static StringFormat CreateCaptionFormat(int maxLines)
    {
        StringFormat format = new StringFormat(StringFormat.GenericTypographic);
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Near;
        format.Trimming = StringTrimming.EllipsisCharacter;
        if (maxLines <= 1)
        {
            format.FormatFlags |= StringFormatFlags.NoWrap;
        }

        return format;
    }

    private static int MeasureBlockHeight(Graphics g, string text, Font font, int width, int maxLines)
    {
        string value = string.IsNullOrWhiteSpace(text) ? "Ag国" : text;
        using (StringFormat format = CreateCaptionFormat(maxLines))
        {
            SizeF size = g.MeasureString(value, font, width, format);
            int lineHeight = (int)Math.Ceiling(g.MeasureString("Ag国", font, int.MaxValue, format).Height);
            int measured = (int)Math.Ceiling(size.Height);
            // Cap by whole lines rather than by pixels: clipping mid-line leaves half a row of
            // Chinese glyphs on screen, which reads as a rendering fault rather than as truncation.
            return Math.Max(lineHeight, Math.Min(measured, lineHeight * Math.Max(1, maxLines)));
        }
    }

    protected override void DrawWindowContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int padX = S(22);
        int padY = S(12);
        int width = ResolveRenderWidth();
        int textWidth = Math.Max(S(80), width - padX * 2);

        // A scrim, not a panel: the strip spans the whole screen width, so a solid bar would black
        // out a band of the video. The fill is heaviest behind the text and fades out at the edges.
        using (LinearGradientBrush scrim = new LinearGradientBrush(
            new Rectangle(0, 0, Math.Max(1, width), Math.Max(1, this.Height)),
            DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 0),
            DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 0),
            LinearGradientMode.Horizontal))
        {
            ColorBlend blend = new ColorBlend(4);
            blend.Colors = new Color[]
            {
                DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 0),
                DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 170),
                DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 170),
                DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 0),
            };
            blend.Positions = new float[] { 0.0f, 0.12f, 0.88f, 1.0f };
            scrim.InterpolationColors = blend;
            g.FillRectangle(scrim, 0, 0, width, this.Height);
        }

        Font translationFont = ResolveTranslationFont();
        int y = padY;
        int translationHeight = MeasureBlockHeight(g, BuildTranslationText(), translationFont, textWidth, MaxTranslationLines);
        DrawTranslationRuns(g, translationFont, new Rectangle(padX, y, textWidth, translationHeight));
        y += translationHeight;

        if (ShouldDrawOriginal())
        {
            Font originalFont = ResolveOriginalFont();
            int originalHeight = MeasureBlockHeight(g, this.snapshot.OriginalCaption, originalFont, textWidth, 1);
            y += S(4);
            // Faint on purpose: the original is there for the word you did not catch, not to be read
            // in parallel with the translation.
            DrawCaptionText(
                g,
                this.snapshot.OriginalCaption,
                originalFont,
                new Rectangle(padX, y, textWidth, originalHeight),
                DesignTokens.Colors.TextMuted,
                150,
                1);
        }
    }

    // Drawn twice: a dark copy offset by a couple of pixels, then the text itself. Captions sit on
    // top of arbitrary video, so light text on a light frame needs something behind every glyph, and
    // a shadow costs one extra DrawString where a full opaque bar would cost a band of the picture.
    private void DrawCaptionText(Graphics g, string text, Font font, Rectangle bounds, Color color, int alpha, int maxLines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int offset = Math.Max(1, S(ShadowOffsetLogical));
        using (StringFormat format = CreateCaptionFormat(maxLines))
        using (SolidBrush shadow = new SolidBrush(Color.FromArgb(ScaleAlpha(alpha, 190), 0, 0, 0)))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(color, alpha)))
        {
            g.DrawString(text, font, shadow, new Rectangle(bounds.Left + offset, bounds.Top + offset, bounds.Width, bounds.Height), format);
            g.DrawString(text, font, brush, bounds, format);
        }
    }

    private static int ScaleAlpha(int alpha, int scale)
    {
        return DesignTokens.ClampByte(alpha * scale / 255);
    }

    // Layout self-test. The failure it guards against is a strip whose measured height does not
    // match what it draws: too short clips the last line, too tall parks an empty band over the
    // video. Both are invisible in code review and obvious only on screen.
    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        using (CaptionOverlayForm form = new CaptionOverlayForm(settings))
        using (Bitmap bitmap = new Bitmap(1440, 300, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.snapshot = CreateFixtureSnapshot(
                "These are both transport layer protocols, meaning they handle how data moves from one machine to another over the network.",
                "这些都是传输层协议，意味着它们负责数据如何在一台机器和另一台机器之间移动。");
            int twoLineHeight = form.MeasureDesiredHeight(g, 1440);
            int oneLineHeight;
            form.snapshot = CreateFixtureSnapshot("Short line.", "短句。");
            oneLineHeight = form.MeasureDesiredHeight(g, 1440);
            AssertSelfTest(
                oneLineHeight > 0 && twoLineHeight >= oneLineHeight,
                "caption strip height must grow with the caption, not shrink");

            // A width narrow enough to force wrapping must add height, never clip silently.
            form.snapshot = CreateFixtureSnapshot(
                "These are both transport layer protocols, meaning they handle how data moves from one machine to another over the network.",
                "这些都是传输层协议，意味着它们负责数据如何在一台机器和另一台机器之间移动。");
            int narrowHeight = form.MeasureDesiredHeight(g, 600);
            AssertSelfTest(narrowHeight >= twoLineHeight, "a narrower strip must be at least as tall as a wide one");

            // The original line is the only optional block; turning it off must actually save height.
            WidgetSettings noOriginal = WidgetSettings.CreateDefaults();
            noOriginal.CaptionOverlayShowOriginal = false;
            noOriginal.Normalize();
            form.ApplySettings(noOriginal);
            int withoutOriginal = form.MeasureDesiredHeight(g, 1440);
            AssertSelfTest(withoutOriginal < twoLineHeight, "hiding the original line must reduce the strip height");

            // Empty captions keep the window hidden rather than parking an empty band on screen.
            // A settled sentence plus an in-progress one is one paragraph, so it must measure at
            // least as tall as the in-progress sentence alone -- never shorter, which would clip.
            form.ApplySettings(settings);
            TranslatorCaptionSnapshot split = CreateFixtureSnapshot("Short line.", "短句。");
            split.PreviousTranslation = "这是上一句已经确定下来的译文。";
            form.snapshot = split;
            int splitHeight = form.MeasureDesiredHeight(g, 1440);
            form.snapshot = CreateFixtureSnapshot("Short line.", "短句。");
            AssertSelfTest(
                splitHeight >= form.MeasureDesiredHeight(g, 1440),
                "a settled sentence plus an in-progress one must not measure shorter than the in-progress one alone");

            form.snapshot = CreateFixtureSnapshot(string.Empty, string.Empty);
            AssertSelfTest(!form.ShouldBeVisible(), "an empty caption must not show the strip");
            form.snapshot = CreateFixtureSnapshot("x", "y");
            form.snapshot.TranslatorRunning = false;
            AssertSelfTest(!form.ShouldBeVisible(), "no translator means no strip");
        }

        Console.WriteLine("Caption overlay layout: PASS measured height tracks caption length, optional original line, hidden when silent");
    }

    private static TranslatorCaptionSnapshot CreateFixtureSnapshot(string original, string translated)
    {
        return new TranslatorCaptionSnapshot
        {
            TranslatorRunning = true,
            CaptionElementsResolved = true,
            OriginalCaption = original,
            TranslatedCaption = translated,
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Caption overlay layout self-test failed: " + message);
        }
    }

    // Render harness for --render-captionoverlay: the strip is drawn over a mid-grey backdrop so the
    // scrim and the shadow are both visible in the sample, which is the point of looking at it.
    // Renders whatever the strip would be showing this second, from the live reader. The fixtures
    // above prove the layout; this proves the whole chain -- reader, settled/live split, colours --
    // against the translator that is actually running, which is the only way to check the split
    // without photographing the screen.
    internal static void RenderCurrent(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        TranslatorCaptionReader reader = new TranslatorCaptionReader();
        reader.RefreshIfDue();
        System.Threading.Thread.Sleep(TranslatorCaptionReader.RefreshIntervalMs + 120);
        reader.RefreshIfDue();
        TranslatorCaptionSnapshot live = reader.GetSnapshot();
        Console.WriteLine("translator running = " + live.TranslatorRunning.ToString());
        Console.WriteLine("settled            = [" + live.PreviousTranslation + "]");
        Console.WriteLine("in progress        = [" + live.TranslatedCaption + "]");
        Console.WriteLine("original           = [" + live.OriginalCaption + "]");

        WidgetSettings settings = WidgetSettings.Load();
        settings.Normalize();
        using (CaptionOverlayForm form = new CaptionOverlayForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.snapshot = live;
            int width = 1440 * 2;
            form.renderWidth = width;
            int height = form.MeasureDesiredHeight(width);
            using (Bitmap bitmap = new Bitmap(width, Math.Max(1, height), PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(255, 96, 104, 112));
                form.DrawWindowContent(g);
                string path = Path.Combine(outputDir, "caption-overlay-current.png");
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine("caption-overlay-current.png -> " + path + " (" + width + "x" + height + ")");
            }
        }
    }

    internal static void RenderSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        RenderSample(
            outputDir,
            "caption-overlay-long.png",
            "These are both transport layer protocols, meaning they handle how data moves from one machine to another over the network.",
            "这些都是传输层协议，意味着它们负责数据如何在一台机器和另一台机器之间移动。",
            true);
        RenderSample(
            outputDir,
            "caption-overlay-short.png",
            "So the first thing you want to do is grab the health potion.",
            "所以你要做的第一件事是拿到恢复药水。",
            true);
        RenderSample(
            outputDir,
            "caption-overlay-settled-and-live.png",
            "and that is why the handshake matters here.",
            "所以握手过程在这里才重要。",
            true,
            "这些都是传输层协议，负责数据在机器之间的移动。");
        RenderSample(
            outputDir,
            "caption-overlay-translation-only.png",
            "So the first thing you want to do is grab the health potion.",
            "所以你要做的第一件事是拿到恢复药水。",
            false);
    }

    private static void RenderSample(string outputDir, string fileName, string original, string translated, bool showOriginal)
    {
        RenderSample(outputDir, fileName, original, translated, showOriginal, string.Empty);
    }

    private static void RenderSample(string outputDir, string fileName, string original, string translated, bool showOriginal, string previousTranslation)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CaptionOverlayShowOriginal = showOriginal;
        settings.Normalize();
        using (CaptionOverlayForm form = new CaptionOverlayForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.snapshot = CreateFixtureSnapshot(original, translated);
            form.snapshot.PreviousTranslation = previousTranslation;
            // Device pixels of a 1440-wide screen at LayerScale 2. Set on the layout, not through
            // Form.Size, which Windows would clamp to the real screen.
            int width = 1440 * 2;
            form.renderWidth = width;
            int height = form.MeasureDesiredHeight(width);
            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(255, 96, 104, 112));
                form.DrawWindowContent(g);
                string path = Path.Combine(outputDir, fileName);
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine(fileName + " -> " + path + " (" + width + "x" + height + ")");
            }
        }
    }
}
