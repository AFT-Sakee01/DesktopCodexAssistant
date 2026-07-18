using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

// Source semantics stay caller-owned: this helper only resolves the reset-label treatment that
// both Claude views must apply before handing a QuotaRingDrawSpec to the shared renderer.
internal static class ClaudeQuotaSourcePresentation
{
    public static bool IsPublicSiteSource(string source)
    {
        return string.Equals(source, "site", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "claude_site_public", StringComparison.OrdinalIgnoreCase);
    }

    public static void ResolveResetDisplay(
        string source,
        Color defaultColor,
        out Color displayColor,
        out bool forceDisplayColor)
    {
        bool publicSite = IsPublicSiteSource(source);
        displayColor = publicSite
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245)
            : defaultColor;
        forceDisplayColor = publicSite;
    }

    internal static void RunSelfTest()
    {
        Color color;
        bool force;
        ResolveResetDisplay("site", Color.White, out color, out force);
        if (!force || color != DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245))
        {
            throw new InvalidOperationException("Claude public-site quota reset presentation self-test failed.");
        }

        ResolveResetDisplay("personal", Color.White, out color, out force);
        if (force || color != Color.White)
        {
            throw new InvalidOperationException("Claude personal quota reset presentation self-test failed.");
        }
    }
}

// Quota-ring painting shared by the CodexRadarForm shared window (Claude mode) and the
// standalone ClaudeRadarForm. Both windows used to keep an independent copy of this exact
// paint code (GetQuotaColor/GetClaudeQuotaColor, GetQuotaConsumptionRingColor/
// GetClaudeQuotaConsumptionRingColor, DrawEvenLayoutQuotaCell/DrawClaudeEvenLayoutQuotaCell)
// that had silently drifted apart - the standalone window's "quotaProtected" (gold ring)
// input was even hardcoded to false at its call site, so it never got the protection
// treatment the shared window has. This is now the single source of truth for both.
internal static class QuotaRingPresentation
{
    private static readonly Color ConsumptionRingBaseGreen = Color.FromArgb(142, 242, 185);

    public static Color GetRingColor(int percent)
    {
        if (percent >= 80)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235);
        }

        if (percent >= 30)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningSoft, 238);
        }

        if (percent <= 5)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaDanger, 238);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
    }

    public static Color GetConsumptionRingColor()
    {
        return DesignTokens.WithAlpha(ConsumptionRingBaseGreen, 242);
    }

    public static Color GetRingNumberColor(Color fallbackColor, bool anySupportedAppRunning, bool quotaValueKnown)
    {
        if (!quotaValueKnown)
        {
            return fallbackColor;
        }

        return anySupportedAppRunning
            ? DesignTokens.White(246)
            : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
    }

    // NO_SETUP_TOKEN / NO_TOKEN are the ClaudeCodeUsageReader error codes for "the user has
    // not pasted a setup-token yet" - the one case where the ring should scream instead of
    // quietly showing the CreateDefault() 100% snapshot as if quota were healthy.
    public static bool IsSetupTokenMissing(string errorCode)
    {
        return string.Equals(errorCode, "NO_SETUP_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "NO_TOKEN", StringComparison.OrdinalIgnoreCase);
    }

    public static void DrawQuotaRing(Graphics g, RectangleF ringRect, RectangleF textRect, QuotaRingDrawSpec spec)
    {
        if (ringRect.Width <= 0 || ringRect.Height <= 0 || spec == null)
        {
            return;
        }

        int percent = ClampPercent(spec.Percent);
        int consumptionRingPercent = ClampPercent(spec.ConsumptionRingPercent);
        bool forceDanger = spec.ForceDangerFullRing && !spec.SuppressQuotaAlerts;
        Color ringColor = forceDanger
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245)
            : (spec.SuppressQuotaAlerts
                ? DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235)
                : GetRingColor(percent));
        int arcPercent = forceDanger ? 100 : percent;
        int visibleConsumptionRingPercent = forceDanger ? 0 : Math.Max(percent, consumptionRingPercent);

        float stroke = Math.Max(2.0f, ringRect.Width * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);
        using (Pen backgroundPen = new Pen(DesignTokens.White(78), stroke))
        using (Pen valuePen = new Pen(ringColor, stroke))
        using (Pen consumptionRingPen = new Pen(GetConsumptionRingColor(), stroke))
        {
            backgroundPen.StartCap = LineCap.Flat;
            backgroundPen.EndCap = LineCap.Flat;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            consumptionRingPen.StartCap = LineCap.Round;
            consumptionRingPen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (spec.RainbowRing)
            {
                // Sub-day reset credit: the full ring becomes a STATIC rainbow (red at 12 o'clock,
                // hue advancing clockwise once around) regardless of percent, and the per-percent
                // consumption overlay is suppressed so the rainbow reads cleanly.
                DrawRainbowArc(g, arcRect, -90.0f, 360.0f, stroke);
            }
            else if (spec.ResetDetectedRing)
            {
                // Quota reset just detected: full ring solid sky blue (no consumption overlay).
                using (Pen skyBluePen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.QuotaResetSkyBlue, 245), stroke))
                {
                    skyBluePen.StartCap = LineCap.Round;
                    skyBluePen.EndCap = LineCap.Round;
                    g.DrawArc(skyBluePen, arcRect, -90.0f, 360.0f);
                }
            }
            else
            {
                if (visibleConsumptionRingPercent > arcPercent)
                {
                    g.DrawArc(consumptionRingPen, arcRect, -90.0f, 360.0f * visibleConsumptionRingPercent / 100.0f);
                }

                if (arcPercent > 0)
                {
                    g.DrawArc(valuePen, arcRect, -90.0f, 360.0f * arcPercent / 100.0f);
                }
            }
        }

        Color displayColor = spec.ResetDisplayColor;
        if (!spec.Running && !spec.ForceResetDisplayColor)
        {
            displayColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        string numberText = forceDanger
            ? "!"
            : (spec.QuotaValueKnown ? percent.ToString(CultureInfo.InvariantCulture) : "-");
        Color numberColor = forceDanger
            ? DesignTokens.White(246)
            : ((spec.RainbowRing || spec.ResetDetectedRing)
                ? DesignTokens.White(246)
                : GetRingNumberColor(displayColor, spec.AnySupportedAppRunning, spec.QuotaValueKnown));

        using (SolidBrush numberBrush = new SolidBrush(numberColor))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString(numberText, spec.NumberFont, numberBrush, ringRect, center);
        }

        if (spec.DrawFittedLabel != null)
        {
            string labelText = forceDanger ? "未配置" : (spec.ResetDisplayText ?? string.Empty);
            using (SolidBrush labelBrush = new SolidBrush(displayColor))
            {
                spec.DrawFittedLabel(g, labelText, spec.LabelFont, labelBrush, textRect);
            }
        }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    // Paints an arc as a STATIC rainbow by drawing many short segments, each a hue step further
    // around the color wheel: hue 0 (red) sits at the arc start (12 o'clock for a -90 start) and
    // advances clockwise once around the full sweep. No animation.
    private static void DrawRainbowArc(
        Graphics g,
        RectangleF arcRect,
        float startAngle,
        float sweepAngle,
        float stroke)
    {
        if (Math.Abs(sweepAngle) < 0.5f)
        {
            return;
        }

        const int Segments = 36;
        float segmentSweep = sweepAngle / Segments;
        // Slight overlap keeps the round-capped segments from leaving seams between hues.
        float overlap = Math.Abs(segmentSweep) * 0.35f;
        for (int i = 0; i < Segments; i++)
        {
            float hue = (i * (360.0f / Segments)) % 360.0f;
            using (Pen pen = new Pen(HueToColor(hue, 245), stroke))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arcRect, startAngle + i * segmentSweep, segmentSweep + overlap);
            }
        }
    }

    // Full-saturation, full-value HSV -> RGB for a given hue in [0,360).
    private static Color HueToColor(float hue, int alpha)
    {
        float h = ((hue % 360.0f) + 360.0f) % 360.0f / 60.0f;
        int sector = (int)Math.Floor(h);
        float f = h - sector;
        int v = 255;
        int p = 0;
        int q = (int)Math.Round(255.0f * (1.0f - f));
        int t = (int)Math.Round(255.0f * f);
        int r, gg, b;
        switch (sector)
        {
            case 0: r = v; gg = t; b = p; break;
            case 1: r = q; gg = v; b = p; break;
            case 2: r = p; gg = v; b = t; break;
            case 3: r = p; gg = q; b = v; break;
            case 4: r = t; gg = p; b = v; break;
            default: r = v; gg = p; b = q; break;
        }

        return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), r, gg, b);
    }

    internal static void RunSelfTest()
    {
        ClaudeQuotaSourcePresentation.RunSelfTest();
        ClaudeRadarResetTextFormatter.RunSelfTest();

        if (GetRingColor(85) != DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235) ||
            GetRingColor(50) != DesignTokens.WithAlpha(DesignTokens.Colors.WarningSoft, 238) ||
            GetRingColor(3) != DesignTokens.WithAlpha(DesignTokens.Colors.QuotaDanger, 238) ||
            GetRingColor(15) != DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238))
        {
            throw new InvalidOperationException("QuotaRingPresentation self-test failed: ring color thresholds.");
        }

        if (!IsSetupTokenMissing("NO_SETUP_TOKEN") ||
            !IsSetupTokenMissing("no_token") ||
            IsSetupTokenMissing("429") ||
            IsSetupTokenMissing(string.Empty))
        {
            throw new InvalidOperationException("QuotaRingPresentation self-test failed: setup-token detection.");
        }

        RunForceDangerFullRingRenderSelfTest();
        RunRainbowRingRenderSelfTest();
        RunResetDetectedRingRenderSelfTest();
    }

    // The ResetDetectedRing branch must paint a solid sky-blue full ring (not the percent-based
    // green/yellow and not a multi-hue rainbow): sample the arc top and require a blue-dominant,
    // low-hue-spread color.
    private static void RunResetDetectedRingRenderSelfTest()
    {
        using (Bitmap bitmap = new Bitmap(64, 64))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (Font font = new Font(FontFamily.GenericSansSerif, 8f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF ringRect = new RectangleF(2, 2, 60, 60);
            QuotaRingDrawSpec spec = new QuotaRingDrawSpec
            {
                Percent = 100,
                ConsumptionRingPercent = 0,
                ResetDisplayText = string.Empty,
                ResetDisplayColor = DesignTokens.White(220),
                Running = true,
                AnySupportedAppRunning = true,
                QuotaValueKnown = true,
                ResetDetectedRing = true,
                NumberFont = font,
                LabelFont = font
            };
            DrawQuotaRing(g, ringRect, new RectangleF(0, 0, 0, 0), spec);

            float cx = ringRect.Left + ringRect.Width / 2.0f;
            float cy = ringRect.Top + ringRect.Height / 2.0f;
            float radius = (ringRect.Width - Math.Max(2.0f, ringRect.Width * 0.14f)) / 2.0f;
            System.Collections.Generic.HashSet<int> hues = new System.Collections.Generic.HashSet<int>();
            bool sawBlue = false;
            for (int deg = 0; deg < 360; deg += 15)
            {
                double rad = (deg - 90) * Math.PI / 180.0;
                int px = (int)Math.Round(cx + Math.Cos(rad) * radius);
                int py = (int)Math.Round(cy + Math.Sin(rad) * radius);
                if (px < 0 || py < 0 || px >= bitmap.Width || py >= bitmap.Height)
                {
                    continue;
                }

                Color c = bitmap.GetPixel(px, py);
                if (c.A < 40)
                {
                    continue;
                }

                hues.Add(((int)Math.Round(c.GetHue() / 30.0f)) % 12);
                if (c.B > c.R && c.B > 120 && c.G > c.R)
                {
                    sawBlue = true;
                }
            }

            if (!sawBlue || hues.Count > 3)
            {
                throw new InvalidOperationException(
                    "QuotaRingPresentation self-test failed: reset-detected ring is not a solid sky-blue ring (sawBlue=" +
                    sawBlue + ", distinctHues=" + hues.Count + ").");
            }
        }
    }

    // The rainbow branch must paint several distinct hues around the ring (not a single solid
    // color), so sample the arc path at several angles and require multiple different colors.
    private static void RunRainbowRingRenderSelfTest()
    {
        using (Bitmap bitmap = new Bitmap(64, 64))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (Font font = new Font(FontFamily.GenericSansSerif, 8f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF ringRect = new RectangleF(2, 2, 60, 60);
            QuotaRingDrawSpec spec = new QuotaRingDrawSpec
            {
                Percent = 100,
                ConsumptionRingPercent = 0,
                ResetDisplayText = string.Empty,
                ResetDisplayColor = DesignTokens.White(220),
                Running = true,
                AnySupportedAppRunning = true,
                QuotaValueKnown = true,
                RainbowRing = true,
                NumberFont = font,
                LabelFont = font
            };
            DrawQuotaRing(g, ringRect, new RectangleF(0, 0, 0, 0), spec);

            float cx = ringRect.Left + ringRect.Width / 2.0f;
            float cy = ringRect.Top + ringRect.Height / 2.0f;
            float radius = (ringRect.Width - Math.Max(2.0f, ringRect.Width * 0.14f)) / 2.0f;
            System.Collections.Generic.HashSet<int> hues = new System.Collections.Generic.HashSet<int>();
            for (int deg = 0; deg < 360; deg += 15)
            {
                double rad = (deg - 90) * Math.PI / 180.0;
                int px = (int)Math.Round(cx + Math.Cos(rad) * radius);
                int py = (int)Math.Round(cy + Math.Sin(rad) * radius);
                if (px < 0 || py < 0 || px >= bitmap.Width || py >= bitmap.Height)
                {
                    continue;
                }

                Color c = bitmap.GetPixel(px, py);
                if (c.A < 40)
                {
                    continue;
                }

                hues.Add(((int)Math.Round(c.GetHue() / 30.0f)) % 12);
            }

            if (hues.Count < 4)
            {
                throw new InvalidOperationException(
                    "QuotaRingPresentation self-test failed: rainbow ring painted too few distinct hues (" + hues.Count + ").");
            }
        }
    }

    // Renders the ring to an in-memory bitmap and samples a pixel on the arc path to prove the
    // forced-red-full-ring branch actually paints red (not the percent-based green/yellow it
    // would otherwise show for a 100%-default snapshot) - a boolean-only test would miss a
    // regression in the drawing branch itself.
    private static void RunForceDangerFullRingRenderSelfTest()
    {
        using (Bitmap bitmap = new Bitmap(64, 64))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (Font font = new Font(FontFamily.GenericSansSerif, 8f))
        {
            RectangleF ringRect = new RectangleF(2, 2, 60, 60);
            RectangleF textRect = new RectangleF(0, 0, 0, 0);
            QuotaRingDrawSpec spec = new QuotaRingDrawSpec
            {
                Percent = 100,
                ConsumptionRingPercent = 0,
                ResetDisplayText = "N/A",
                ResetDisplayColor = Color.White,
                Running = true,
                AnySupportedAppRunning = true,
                QuotaValueKnown = false,
                ForceDangerFullRing = true,
                NumberFont = font,
                LabelFont = font,
                DrawFittedLabel = null
            };
            DrawQuotaRing(g, ringRect, textRect, spec);

            Color sample = bitmap.GetPixel(32, 3);
            if (sample.R < 180 || sample.G > 120 || sample.B > 120)
            {
                throw new InvalidOperationException(
                    "QuotaRingPresentation self-test failed: forced danger ring did not render red at the sampled pixel (R="
                    + sample.R + " G=" + sample.G + " B=" + sample.B + ").");
            }
        }
    }
}

internal static class ClaudeRadarResetTextFormatter
{
    public static string FormatCompact(string text, bool fiveHour)
    {
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "N/A";
        }

        DateTime resetLocal;
        if (TryParseResetText(value, fiveHour, out resetLocal))
        {
            return resetLocal.ToString(fiveHour ? "HH:mm" : "MM/dd", CultureInfo.CurrentCulture);
        }

        return value;
    }

    public static bool TryParseResetText(string text, bool fiveHour, out DateTime resetLocal)
    {
        resetLocal = DateTime.MinValue;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Match timeOnly = Regex.Match(value, @"(?<!\d)(\d{1,2})[:：](\d{2})(?!\d)");
        if (fiveHour && timeOnly.Success)
        {
            int hour;
            int minute;
            if (int.TryParse(timeOnly.Groups[1].Value, out hour) &&
                int.TryParse(timeOnly.Groups[2].Value, out minute) &&
                hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59)
            {
                DateTime now = DateTime.Now;
                resetLocal = now.Date.AddHours(hour).AddMinutes(minute);
                if (resetLocal < now.AddMinutes(-5.0))
                {
                    resetLocal = resetLocal.AddDays(1.0);
                }

                return true;
            }
        }

        Match monthDay = Regex.Match(value, @"(\d{1,2})\s*月\s*(\d{1,2})\s*日.*?(\d{1,2})[:：](\d{2})");
        if (monthDay.Success)
        {
            int month;
            int day;
            int hour;
            int minute;
            if (int.TryParse(monthDay.Groups[1].Value, out month) &&
                int.TryParse(monthDay.Groups[2].Value, out day) &&
                int.TryParse(monthDay.Groups[3].Value, out hour) &&
                int.TryParse(monthDay.Groups[4].Value, out minute))
            {
                int year = DateTime.Now.Year;
                try
                {
                    resetLocal = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
                    if (resetLocal < DateTime.Now.AddDays(-180.0))
                    {
                        resetLocal = resetLocal.AddYears(1);
                    }

                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    internal static void RunSelfTest()
    {
        if (!string.Equals(FormatCompact("13:00 重置", true), "13:00", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude reset text formatter self-test failed for five-hour reset text.");
        }

        if (!string.Equals(FormatCompact("7月4日 16:00 重置", false), "07/04", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude reset text formatter self-test failed for weekly reset text.");
        }

        if (!string.Equals(FormatCompact("N/A", true), "N/A", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude reset text formatter self-test failed for N/A text.");
        }
    }
}

// Plain data-holder passed to QuotaRingPresentation.DrawQuotaRing. ResetDisplayText/
// ResetDisplayColor are computed by the caller (each window has its own family-specific
// reset-label logic - e.g. Codex's speed-window forced-gold time vs Claude's plain "已重置") so this
// type stays free of any per-family knowledge.
internal sealed class QuotaRingDrawSpec
{
    public int Percent;
    public int ConsumptionRingPercent;
    public string ResetDisplayText;
    public Color ResetDisplayColor;
    // Codex speed-window time stays gold even when its local process is not running; quota
    // numbers still follow AnySupportedAppRunning and retain the existing gray inactive rule.
    public bool ForceResetDisplayColor;
    public bool Running;
    public bool AnySupportedAppRunning;
    public bool QuotaValueKnown;
    public bool ForceDangerFullRing;
    // Category/quiet-hours suppression neutralizes warning thresholds without hiding the quota
    // value itself. Collection and quota state remain untouched.
    public bool SuppressQuotaAlerts;
    // Sub-day reset-credit indicator: paint the full ring as a STATIC rainbow (red at 12 o'clock,
    // hue advancing clockwise once around). No animation. Takes priority over ResetDetectedRing.
    public bool RainbowRing;
    // Quota-reset detected: paint the full ring solid sky blue (this replaced the old celebratory
    // rainbow, which is now the sub-day RainbowRing above).
    public bool ResetDetectedRing;
    public Font NumberFont;
    public Font LabelFont;
    public Action<Graphics, string, Font, Brush, RectangleF> DrawFittedLabel;
}
