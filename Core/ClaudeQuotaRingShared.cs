using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

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
        bool forceDanger = spec.ForceDangerFullRing;
        Color ringColor = forceDanger ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245) : GetRingColor(percent);
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
            if (visibleConsumptionRingPercent > arcPercent)
            {
                g.DrawArc(consumptionRingPen, arcRect, -90.0f, 360.0f * visibleConsumptionRingPercent / 100.0f);
            }

            if (arcPercent > 0)
            {
                g.DrawArc(valuePen, arcRect, -90.0f, 360.0f * arcPercent / 100.0f);
            }
        }

        Color displayColor = spec.ResetDisplayColor;
        if (!spec.Running)
        {
            displayColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        string numberText = forceDanger
            ? "!"
            : (spec.QuotaValueKnown ? percent.ToString(CultureInfo.InvariantCulture) : "-");
        Color numberColor = forceDanger
            ? DesignTokens.White(246)
            : GetRingNumberColor(displayColor, spec.AnySupportedAppRunning, spec.QuotaValueKnown);

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

    internal static void RunSelfTest()
    {
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
// reset-label logic - e.g. Codex's speed-window flash text vs Claude's plain "已重置") so this
// type stays free of any per-family knowledge.
internal sealed class QuotaRingDrawSpec
{
    public int Percent;
    public int ConsumptionRingPercent;
    public string ResetDisplayText;
    public Color ResetDisplayColor;
    public bool Running;
    public bool AnySupportedAppRunning;
    public bool QuotaValueKnown;
    public bool ForceDangerFullRing;
    public Font NumberFont;
    public Font LabelFont;
    public Action<Graphics, string, Font, Brush, RectangleF> DrawFittedLabel;
}
