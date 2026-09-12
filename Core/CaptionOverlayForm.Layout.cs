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
    // Width of the draggable edge zones in edit mode, and of the frame drawn around the strip.
    internal const int EditGripLogical = 14;
    // Long enough for at least one sentence to finish while the current-state renderer is watching.
    private const int WatchSeconds = 20;

    private int ResolveRenderWidth()
    {
        return this.renderWidth > 0 ? this.renderWidth : this.Width;
    }

    // How many settled slots to reserve. The reader always keeps the maximum, so raising this from
    // the board shows real history immediately instead of starting from an empty block. Slots are
    // reserved whether or not they have text, which is what keeps the strip a fixed height.
    private int SettledLineCount
    {
        get
        {
            return this.CurrentSettings == null
                ? WidgetSettings.DefaultCaptionOverlaySettledLines
                : this.CurrentSettings.CaptionOverlaySettledLines;
        }
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
        int slots = SettledLineCount;
        int height = padY * 2 + settledLine * slots + (slots > 0 ? S(3) : 0) + liveLine;
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
        int slots = SettledLineCount;
        int y = padY;
        for (int slot = 0; slot < slots; slot++)
        {
            // Bottom-aligned into the slots: with fewer settled sentences than slots the empty ones
            // are at the top, so the newest line keeps its place instead of climbing as history
            // fills in.
            int index = settled.Length - slots + slot;
            string text = index >= 0 && index < settled.Length ? settled[index] : string.Empty;
            DrawCaptionLine(g, text, settledFont, new Rectangle(padX, y, textWidth, settledLine),
                DesignTokens.Colors.TextStrong, 215);
            y += settledLine;
        }

        if (slots > 0)
        {
            y += S(3);
        }
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

        if (this.editMode)
        {
            DrawEditChrome(g, width);
        }
    }

    // Edit mode has to look unmistakably different from the normal strip, because in this state the
    // strip swallows clicks meant for the video underneath. A full accent frame plus solid grab
    // bars on the two resizable edges says both "this is movable" and "this is not the usual mode".
    private void DrawEditChrome(Graphics g, int width)
    {
        int height = Math.Max(1, this.Height);
        int grip = S(EditGripLogical);
        using (SolidBrush wash = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.CaptionsAccent, 26)))
        {
            g.FillRectangle(wash, 0, 0, width, height);
        }

        using (Pen frame = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.CaptionsAccent, 230), Math.Max(1, S(2))))
        {
            int inset = (int)Math.Ceiling(frame.Width / 2.0);
            g.DrawRectangle(frame, inset, inset, Math.Max(1, width - inset * 2 - 1), Math.Max(1, height - inset * 2 - 1));
        }

        using (SolidBrush handle = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.CaptionsAccent, 150)))
        {
            g.FillRectangle(handle, 0, 0, grip, height);
            g.FillRectangle(handle, width - grip, 0, grip, height);
        }

        if (this.snapshot.HasText())
        {
            // Real captions are the better placement guide; the hint would only overprint them.
            return;
        }

        // Owned by the font cache, so it is not disposed here.
        Font hintFont = ResolveLiveFont();
        using (StringFormat format = CreateCaptionFormat())
        using (SolidBrush shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
        using (SolidBrush brush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.CaptionsAccent, 255)))
        {
            Rectangle bounds = new Rectangle(grip, 0, Math.Max(1, width - grip * 2), height);
            int offset = Math.Max(1, S(ShadowOffsetLogical));
            const string Hint = "编辑模式：拖动移动，拖两端改宽度，在字幕面板按「完成」保存";
            g.DrawString(Hint, hintFont, shadow, new Rectangle(bounds.Left + offset, bounds.Top + offset, bounds.Width, bounds.Height), format);
            g.DrawString(Hint, hintFont, brush, bounds, format);
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

            // The history count is a board control, so it has to move the strip height both ways --
            // including all the way to zero settled lines, which must still leave a usable strip.
            WidgetSettings threeLines = WidgetSettings.CreateDefaults();
            threeLines.CaptionOverlaySettledLines = 3;
            threeLines.Normalize();
            form.ApplySettings(threeLines);
            int threeLineHeight = form.MeasureDesiredHeight(g, 1440);
            AssertSelfTest(
                threeLineHeight > fullHistoryHeight,
                "more settled lines must make the strip taller: " + threeLineHeight + " vs " + fullHistoryHeight);

            WidgetSettings noHistory = WidgetSettings.CreateDefaults();
            noHistory.CaptionOverlaySettledLines = 0;
            noHistory.Normalize();
            form.ApplySettings(noHistory);
            int noHistoryHeight = form.MeasureDesiredHeight(g, 1440);
            AssertSelfTest(
                noHistoryHeight > 0 && noHistoryHeight < fullHistoryHeight,
                "zero settled lines must still leave the live and original lines: " + noHistoryHeight);

            form.ApplySettings(settings);
            form.snapshot = CreateFixtureSnapshot(string.Empty, string.Empty, new string[0]);
            AssertSelfTest(!form.ShouldBeVisible(), "an empty caption must not show the strip");
            TranslatorCaptionSnapshot stopped = CreateFixtureSnapshot("x", "y", new string[0]);
            stopped.TranslatorRunning = false;
            form.snapshot = stopped;
            AssertSelfTest(!form.ShouldBeVisible(), "no translator means no strip");
        }

        Console.WriteLine("Caption overlay layout: PASS fixed-height slots, settled-line count, optional original line, hidden when silent");
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
        // The samples exist to show the shape, so they ask for the three-line history rather than the
        // one-line default a first run gets.
        settings.CaptionOverlaySettledLines = 3;
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

        // The shipped default: one settled line above the sentence being spoken.
        WidgetSettings defaults = WidgetSettings.CreateDefaults();
        defaults.Normalize();
        RenderSnapshot(outputDir, "caption-overlay-default.png", defaults, CreateFixtureSnapshot(
            "So the first thing you want to do is grab the health potion.",
            "所以你要做的第一件事是拿到恢复药水。",
            new string[] { "我们先把这段讲完。" }));

        // Edit mode with nothing to say: the frame, the two grab bars and the hint, which is what
        // the user sees when they press 编辑 before the translator has produced a caption.
        RenderSnapshot(
            outputDir,
            "caption-overlay-edit.png",
            defaults,
            CreateFixtureSnapshot(string.Empty, string.Empty, new string[0]),
            true);

        WidgetSettings noOriginal = WidgetSettings.CreateDefaults();
        noOriginal.CaptionOverlaySettledLines = 3;
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
        RenderSnapshot(outputDir, fileName, settings, snapshot, false);
    }

    private static void RenderSnapshot(
        string outputDir,
        string fileName,
        WidgetSettings settings,
        TranslatorCaptionSnapshot snapshot,
        bool editing)
    {
        using (CaptionOverlayForm form = new CaptionOverlayForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.snapshot = snapshot;
            // Set directly rather than through SetEditMode: that call also changes window styles and
            // shows the window, neither of which a bitmap render has or wants.
            form.editMode = editing;
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
