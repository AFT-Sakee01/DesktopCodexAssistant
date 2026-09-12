using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Drawing for the caption strip: a fixed stack of single-line slots.
//
//   three settled sentences   white, small, oldest at the top
//   the sentence in progress  tinted, one step larger
//   the original              faint, smallest
//
// Every slot is one line, never wrapped, and every slot is reserved whether or not it has text, so
// the strip's height and every line's y stay put while captions come and go. That is the whole point
// of the shape: an earlier version laid the settled and live text out as one wrapped paragraph, which
// re-flowed on every update and made the text visibly jump around.
//
// Heights come from measured font metrics, never from hand-typed pixel counts.
internal sealed partial class CaptionOverlayForm
{
    // Ratios of the configured font size. The in-progress line is the one being read, so it is the
    // largest; the settled lines are context and the original is a backstop for a word you missed.
    private const float SettledFontScale = 0.72f;
    private const float LiveFontScale = 0.88f;
    private const float OriginalFontScale = 0.58f;
    private const int ShadowOffsetLogical = 2;
    // Matches what the reader keeps. Slots are reserved for all of them from the first frame, so the
    // block does not grow as the first few sentences arrive.
    private const int SettledLineCount = TranslatorCaptionReader.SettledHistoryLimit;
    // Long enough for at least one sentence to finish while the current-state renderer is watching.
    private const int WatchSeconds = 20;

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

    // Constant for a given font size and scale: slot count times slot height plus the gaps. No caption
    // text enters this, which is why the strip never resizes mid-sentence. `width` is accepted only so
    // callers can stay uniform with the rest of the layout code.
    private int MeasureDesiredHeight(Graphics g, int width)
    {
        int padY = S(10);
        int settledLine = MeasureLineHeight(g, ResolveSettledFont());
        int liveLine = MeasureLineHeight(g, ResolveLiveFont());
        int height = padY * 2 + settledLine * SettledLineCount + S(3) + liveLine;
        if (this.CurrentSettings != null && this.CurrentSettings.CaptionOverlayShowOriginal)
        {
            height += S(2) + MeasureLineHeight(g, ResolveOriginalFont());
        }

        return height;
    }

    private static int MeasureLineHeight(Graphics g, Font font)
    {
        using (StringFormat format = CreateCaptionFormat())
        {
            return (int)Math.Ceiling(g.MeasureString("Ag国", font, int.MaxValue, format).Height);
        }
    }

    private Font ResolveSettledFont()
    {
        return this.fontCache.GetUi(ScaledFontSize(SettledFontScale), FontStyle.Regular);
    }

    private Font ResolveLiveFont()
    {
        return this.fontCache.GetUi(ScaledFontSize(LiveFontScale), FontStyle.Bold);
    }

    private Font ResolveOriginalFont()
    {
        return this.fontCache.GetUi(ScaledFontSize(OriginalFontScale), FontStyle.Regular);
    }

    private float ScaledFontSize(float scale)
    {
        int size = this.CurrentSettings == null
            ? WidgetSettings.DefaultCaptionOverlayFontSize
            : this.CurrentSettings.CaptionOverlayFontSize;
        return S(Math.Max(7.0f, size * scale));
    }

    // One line, centred, ellipsised. NoWrap is what keeps a slot a slot: a long sentence is trimmed
    // rather than allowed to push everything below it down the screen.
    private static StringFormat CreateCaptionFormat()
    {
        StringFormat format = new StringFormat(StringFormat.GenericTypographic);
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = StringTrimming.EllipsisCharacter;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        return format;
    }

    protected override void DrawWindowContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int padX = S(22);
        int padY = S(10);
        int width = ResolveRenderWidth();
        int textWidth = Math.Max(S(80), width - padX * 2);

        DrawScrim(g, width);

        Font settledFont = ResolveSettledFont();
        Font liveFont = ResolveLiveFont();
        int settledLine = MeasureLineHeight(g, settledFont);
        int liveLine = MeasureLineHeight(g, liveFont);

        // Oldest at the top, newest directly above the live line, so the eye travels down the same
        // path the speaker took.
        string[] settled = this.snapshot.SettledTranslations ?? new string[0];
        int y = padY;
        for (int slot = 0; slot < SettledLineCount; slot++)
        {
            // Bottom-aligned into the slots: with fewer than three settled sentences the empty slots
            // are the top ones, so the newest line keeps its place instead of climbing as history
            // fills in.
            int index = settled.Length - SettledLineCount + slot;
            string text = index >= 0 && index < settled.Length ? settled[index] : string.Empty;
            DrawCaptionLine(g, text, settledFont, new Rectangle(padX, y, textWidth, settledLine),
                DesignTokens.Colors.TextStrong, 215);
            y += settledLine;
        }

        y += S(3);
        DrawCaptionLine(g, this.snapshot.TranslatedCaption, liveFont, new Rectangle(padX, y, textWidth, liveLine),
            ResolveUnsettledColor(DesignTokens.Colors.TextStrong), 255);
        y += liveLine;

        if (this.CurrentSettings != null && this.CurrentSettings.CaptionOverlayShowOriginal)
        {
            Font originalFont = ResolveOriginalFont();
            int originalLine = MeasureLineHeight(g, originalFont);
            y += S(2);
            DrawCaptionLine(g, this.snapshot.OriginalCaption, originalFont,
                new Rectangle(padX, y, textWidth, originalLine), DesignTokens.Colors.TextMuted, 150);
        }
    }

    // A scrim, not a panel: the strip spans the whole screen width, so a solid bar would black out a
    // band of the video. The fill is heaviest behind the text and fades out at both edges.
    private void DrawScrim(Graphics g, int width)
    {
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
    }

    // The in-progress line is tinted the same way the translator tints it in its own overlay
    // (OverlayWindow.UpdateTranslationColor): push each channel toward the opposite luminance by
    // 30/40/30 percent. Weighting green hardest is what turns near-white into the mauve that reads as
    // "not settled yet" -- reproducing the formula rather than hard-coding that colour keeps it
    // correct if this app's text colour ever changes.
    private static Color ResolveUnsettledColor(Color settled)
    {
        double target = 0.299 * settled.R + 0.587 * settled.G + 0.114 * settled.B > 127 ? 0 : 255;
        int r = DesignTokens.ClampByte((int)Math.Round(settled.R + (target - settled.R) * 0.3));
        int g = DesignTokens.ClampByte((int)Math.Round(settled.G + (target - settled.G) * 0.4));
        int b = DesignTokens.ClampByte((int)Math.Round(settled.B + (target - settled.B) * 0.3));
        return Color.FromArgb(settled.A, r, g, b);
    }

    // Drawn twice: a dark copy offset by a couple of pixels, then the text itself. Captions sit on top
    // of arbitrary video, so light text on a light frame needs something behind every glyph, and a
    // shadow costs one extra DrawString where an opaque bar would cost a band of the picture.
    private void DrawCaptionLine(Graphics g, string text, Font font, Rectangle bounds, Color color, int alpha)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        int offset = Math.Max(1, S(ShadowOffsetLogical));
        using (StringFormat format = CreateCaptionFormat())
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

    // Layout self-test. The failure this guards against is the strip resizing or its lines moving as
    // captions change -- the thing the fixed slots exist to prevent, and the thing a reader notices
    // immediately when a line they were halfway through slides somewhere else.
    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        using (CaptionOverlayForm form = new CaptionOverlayForm(settings))
        using (Bitmap bitmap = new Bitmap(1440, 400, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            form.snapshot = CreateFixtureSnapshot("Short line.", "短句。", new string[0]);
            int emptyHistoryHeight = form.MeasureDesiredHeight(g, 1440);

            form.snapshot = CreateFixtureSnapshot(
                "These are both transport layer protocols, meaning they handle how data moves from one machine to another over the network.",
                "这些都是传输层协议，意味着它们负责数据如何在一台机器和另一台机器之间移动，这一句相当长。",
                new string[] { "第一句已经定稿。", "第二句也定稿了，而且长一些。", "第三句同样定稿，长度再长一点点。" });
            int fullHistoryHeight = form.MeasureDesiredHeight(g, 1440);
            AssertSelfTest(
                emptyHistoryHeight == fullHistoryHeight,
                "the strip height must not depend on the caption text: " + emptyHistoryHeight + " vs " + fullHistoryHeight);

            int narrowHeight = form.MeasureDesiredHeight(g, 600);
            AssertSelfTest(narrowHeight == fullHistoryHeight, "the strip height must not depend on its width either");

            WidgetSettings noOriginal = WidgetSettings.CreateDefaults();
            noOriginal.CaptionOverlayShowOriginal = false;
            noOriginal.Normalize();
            form.ApplySettings(noOriginal);
            AssertSelfTest(
                form.MeasureDesiredHeight(g, 1440) < fullHistoryHeight,
                "hiding the original line must reduce the strip height");

            form.ApplySettings(settings);
            form.snapshot = CreateFixtureSnapshot(string.Empty, string.Empty, new string[0]);
            AssertSelfTest(!form.ShouldBeVisible(), "an empty caption must not show the strip");
            TranslatorCaptionSnapshot stopped = CreateFixtureSnapshot("x", "y", new string[0]);
            stopped.TranslatorRunning = false;
            form.snapshot = stopped;
            AssertSelfTest(!form.ShouldBeVisible(), "no translator means no strip");
        }

        Console.WriteLine("Caption overlay layout: PASS fixed-height slots, optional original line, hidden when silent");
    }

    private static TranslatorCaptionSnapshot CreateFixtureSnapshot(string original, string translated, string[] settled)
    {
        return new TranslatorCaptionSnapshot
        {
            TranslatorRunning = true,
            CaptionElementsResolved = true,
            OriginalCaption = original,
            TranslatedCaption = translated,
            SettledTranslations = settled,
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

    // Renders whatever the strip would be showing, from the live reader. The fixtures prove the
    // layout; this proves the whole chain -- reader, settled history, colours -- against the
    // translator that is actually running, which is the only way to check it without photographing
    // the screen.
    //
    // Watches for a stretch rather than taking one sample: the settled lines are latched when the
    // reader sees one sentence give way to the next, so a single read would always report them empty.
    internal static void RenderCurrent(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        TranslatorCaptionReader reader = new TranslatorCaptionReader();
        TranslatorCaptionSnapshot live = TranslatorCaptionSnapshot.CreateEmpty();
        string lastPrinted = string.Empty;
        DateTime deadline = DateTime.UtcNow.AddSeconds(WatchSeconds);
        while (DateTime.UtcNow < deadline)
        {
            reader.RefreshIfDue();
            live = reader.GetSnapshot();
            string line = "settled=" + live.SettledTranslations.Length + " [" +
                string.Join(" / ", live.SettledTranslations) + "]  live=[" + live.TranslatedCaption + "]";
            if (!string.Equals(line, lastPrinted, StringComparison.Ordinal))
            {
                lastPrinted = line;
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            }

            System.Threading.Thread.Sleep(TranslatorCaptionReader.RefreshIntervalMs);
        }

        Console.WriteLine("translator running = " + live.TranslatorRunning.ToString());
        Console.WriteLine("original           = [" + live.OriginalCaption + "]");

        WidgetSettings settings = WidgetSettings.Load();
        settings.Normalize();
        RenderSnapshot(outputDir, "caption-overlay-current.png", settings, live);
    }

    internal static void RenderSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();

        RenderSnapshot(outputDir, "caption-overlay-full.png", settings, CreateFixtureSnapshot(
            "and that is why the handshake matters here.",
            "所以握手过程在这里才重要。",
            new string[]
            {
                "这些都是传输层协议，负责数据在机器之间的移动。",
                "你首先会收到一个未授权的响应，因为你没有提供凭证。",
                "于是我们提示用户或服务在访问前提供凭证。",
            }));

        // One settled sentence: the empty slots must be the top ones, so the live line does not move
        // as history fills in.
        RenderSnapshot(outputDir, "caption-overlay-warmup.png", settings, CreateFixtureSnapshot(
            "So the first thing you want to do is grab the health potion.",
            "所以你要做的第一件事是拿到恢复药水。",
            new string[] { "我们先把这段讲完。" }));

        WidgetSettings noOriginal = WidgetSettings.CreateDefaults();
        noOriginal.CaptionOverlayShowOriginal = false;
        noOriginal.Normalize();
        RenderSnapshot(outputDir, "caption-overlay-translation-only.png", noOriginal, CreateFixtureSnapshot(
            "So the first thing you want to do is grab the health potion.",
            "所以你要做的第一件事是拿到恢复药水。",
            new string[]
            {
                "这些都是传输层协议，负责数据在机器之间的移动。",
                "你首先会收到一个未授权的响应，因为你没有提供凭证。",
                "于是我们提示用户或服务在访问前提供凭证。",
            }));
    }

    private static void RenderSnapshot(string outputDir, string fileName, WidgetSettings settings, TranslatorCaptionSnapshot snapshot)
    {
        using (CaptionOverlayForm form = new CaptionOverlayForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.snapshot = snapshot;
            // Device pixels of a 1440-wide screen at LayerScale 2. Set on the layout, not through
            // Form.Size, which Windows would clamp to the real screen.
            int width = 1440 * 2;
            form.renderWidth = width;
            int height = form.MeasureDesiredHeight(width);
            using (Bitmap bitmap = new Bitmap(width, Math.Max(1, height), PixelFormat.Format32bppPArgb))
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
