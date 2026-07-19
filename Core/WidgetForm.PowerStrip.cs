using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// Power strip integrated into the bottom of the main widget (1.0.5.83). Carries the same three
// stat blocks the standalone power/thermal bar shows (draw / battery / thermal), reading a cloned
// snapshot from PowerThermalForm rather than sampling anything itself.
internal sealed partial class WidgetForm
{
    // Laid out on the metric grid's columns: draw and battery share the first row, thermal spans
    // the full width on the second. Cell width/height come from the caller so the dividers land on
    // exactly the same x positions as the metric cards above and the guard badges below.
    private void DrawPowerStrip(Graphics g, RectangleF strip, float cellW, float cellH, float gapX, float gapY)
    {
        PowerStripSnapshot snap = null;
        if (this.powerThermalForm != null && !this.powerThermalForm.IsDisposed)
        {
            try
            {
                snap = this.powerThermalForm.BuildStripSnapshot();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        if (snap == null)
        {
            return;
        }

        // One row of three panes (draw / battery / thermal). Their dividers do not line up with the
        // two-column grid above, which is accepted: only the outer edges have to match the cards and
        // the guard badges.
        float paneW = (strip.Width - gapX * 2.0f) / 3.0f;
        DrawStripPane(g, new RectangleF(strip.Left, strip.Top, paneW, strip.Height), snap, 0);
        DrawStripPane(g, new RectangleF(strip.Left + paneW + gapX, strip.Top, paneW, strip.Height), snap, 1);
        DrawStripPane(g, new RectangleF(strip.Left + (paneW + gapX) * 2.0f, strip.Top, paneW, strip.Height), snap, 2);
    }

    private void DrawStripPane(Graphics g, RectangleF pane, PowerStripSnapshot s, int kind)
    {
        using (GraphicsPath p = RoundedRectangle(pane, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 92)))
        using (Pen border = new Pen(DesignTokens.White(34), 1.0f))
        {
            g.FillPath(fill, p);
            g.DrawPath(border, p);
        }

        // Bands are sized from the pane so the content reaches the outline the same way the metric
        // cards do, instead of sitting in a fixed-height block inside a taller card.
        float x = pane.Left + S(3);
        float w = pane.Width - S(6);
        float unit = pane.Height / 47.0f;   // 47 = 2 + 8 + 1 + 22 + 1 + 4 + 1 + 8 band units
        float y = pane.Top + unit * 1.0f;
        float labelH = unit * 8.0f;
        float valueH = unit * 22.0f;
        float gaugeH = unit * 4.0f;
        float subH = unit * 9.0f;

        RectangleF labelRect = new RectangleF(x, y, w, labelH);
        y += labelH + unit;
        RectangleF valueRect = new RectangleF(x, y, w, valueH);
        y += valueH + unit;
        RectangleF gaugeRect = new RectangleF(x, y, w, gaugeH);
        y += gaugeH + unit;
        RectangleF subRect = new RectangleF(x, y, w, subH);

        Font labelFont = GetCachedFont(Math.Max(7.0f, labelH * 0.80f), FontStyle.Bold);
        Font valueFont = GetCachedFont(Math.Max(9.0f, valueH * 0.96f), FontStyle.Bold);
        Font subFont = GetCachedFont(Math.Max(7.0f, subH * 0.92f), FontStyle.Bold);

        if (kind == 0)
        {
            DrawStripText(g, s.Charging ? "CHARGING" : "POWER DRAW", labelFont, DesignTokens.Colors.TextMuted, labelRect);
            DrawStripText(g, s.WattsKnown ? s.Watts.ToString("0.0", CultureInfo.InvariantCulture) + " W" : "-- W",
                valueFont, s.Charging ? DesignTokens.Colors.SuccessText : DesignTokens.Colors.DangerText, valueRect);
            DrawStripGauge(g, gaugeRect, s.WattsKnown ? Math.Min(100.0, s.Watts / 60.0 * 100.0) : 0.0,
                s.Charging ? DesignTokens.Colors.Success : DesignTokens.Colors.DangerStrong);
            // Back to carrying the power mode: the three-pane row has no fourth slot for it.
            DrawStripText(g, s.PowerModeText, subFont, GetStripModeColor(s), subRect);
            return;
        }

        if (kind == 3)
        {
            DrawStripText(g, "POWER MODE", labelFont, DesignTokens.Colors.TextMuted, labelRect);
            // CJK glyphs are taller than the digits the other panes show, so at the shared size
            // they clip top and bottom inside the same band. DrawFittedText only shrinks on width.
            Font modeFont = GetCachedFont(Math.Max(9.0f, valueH * 0.74f), FontStyle.Bold);
            DrawStripText(g, string.IsNullOrEmpty(s.PowerModeText) ? "--" : s.PowerModeText,
                modeFont, GetStripModeColor(s), valueRect);
            // No meaningful 0-100 axis for a mode; the bar reads as a three-step position instead.
            DrawStripGauge(g, gaugeRect, GetPowerModePercent(s), GetStripModeColor(s));
            DrawStripText(g, FormatStripModeSub(s), subFont,
                s.EnergySaverActive ? GetStripModeColor(s) : DesignTokens.Colors.Text, subRect);
            return;
        }

        if (kind == 1)
        {
            int pct = s.BatteryPercentKnown ? Math.Max(0, Math.Min(100, s.BatteryPercent)) : 0;
            DrawStripText(g, s.PluggedIn ? "BATTERY · AC" : "BATTERY", labelFont, DesignTokens.Colors.TextMuted, labelRect);
            DrawStripText(g, s.BatteryPercentKnown ? pct.ToString(CultureInfo.InvariantCulture) + "%" : "--",
                valueFont, DesignTokens.Colors.TextStrong, valueRect);
            DrawStripGauge(g, gaugeRect, pct, GetStripBatteryColor(s.BatteryPercentKnown, pct));
            DrawStripText(g, FormatStripBatterySub(s), subFont, DesignTokens.Colors.Text, subRect);
            return;
        }

        if (s.AlertCount > 0)
        {
            DrawStripThermalRanking(g, pane, s, labelFont, subFont);
            return;
        }

        DrawStripText(g, "MAX TEMP", labelFont, DesignTokens.Colors.TextMuted, labelRect);
        DrawStripText(g, s.MaxCelsius > 0.0 ? s.MaxCelsius.ToString("0", CultureInfo.InvariantCulture) + "°C" : "--",
            valueFont, GetStripTempColor(s.MaxCelsius), valueRect);
        DrawStripGauge(g, gaugeRect, StripTempPercent(s.MaxCelsius), GetStripTempColor(s.MaxCelsius));
        DrawStripText(g, s.ZoneCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "均 {0:0}° · {1} 区", s.AvgCelsius, s.ZoneCount)
                : "无传感器",
            subFont, DesignTokens.Colors.Text, subRect);
    }

    // Balanced sits mid-bar, performance full, saver low — a position, not a measurement.
    private static double GetPowerModePercent(PowerStripSnapshot s)
    {
        if (s.EnergySaverActive)
        {
            return 20.0;
        }

        if (string.Equals(s.PowerModeText, "性能", StringComparison.OrdinalIgnoreCase))
        {
            return 100.0;
        }

        if (string.Equals(s.PowerModeText, "省电", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.PowerModeText, "节能", StringComparison.OrdinalIgnoreCase))
        {
            return 20.0;
        }

        return 60.0;
    }

    private static string FormatStripModeSub(PowerStripSnapshot s)
    {
        if (s.BatteryCarePauseActive)
        {
            return "养护暂停";
        }

        if (s.EnergySaverActive)
        {
            return "节能开启";
        }

        return s.Charging ? "充电中" : "常规";
    }

    private void DrawStripThermalRanking(Graphics g, RectangleF pane, PowerStripSnapshot s, Font labelFont, Font rowFont)
    {
        float x = pane.Left + S(9);
        float w = pane.Width - S(18);
        float y = pane.Top + S(3);

        DrawStripText(g, s.AlertCount.ToString(CultureInfo.InvariantCulture) + " 区超 70°C",
            labelFont, DesignTokens.Colors.Warning, new RectangleF(x, y, w, S(7)));
        y += S(9);

        float rowH = S(9);
        int capacity = Math.Max(1, (int)Math.Floor((pane.Bottom - S(3) - y) / rowH));
        int shown = Math.Min(s.HotZones.Count, capacity);
        bool overflow = s.HotZones.Count > shown;
        if (overflow && shown > 0)
        {
            shown--;
        }

        float nameW = S(26);
        float tempW = S(24);
        for (int i = 0; i < shown; i++)
        {
            PowerStripZone z = s.HotZones[i];
            Color c = GetStripTempColor(z.Celsius);
            RectangleF row = new RectangleF(x, y + rowH * i, w, rowH);
            DrawStripText(g, z.Name, rowFont, DesignTokens.Colors.Text, new RectangleF(row.Left, row.Top, nameW, row.Height));

            float barLeft = row.Left + nameW + S(2);
            float barRight = row.Right - tempW;
            if (barRight - barLeft > S(6))
            {
                DrawStripGauge(g, new RectangleF(barLeft, row.Top + row.Height * 0.38f, barRight - barLeft, S(2.5f)),
                    StripTempPercent(z.Celsius), c);
            }

            using (SolidBrush b = new SolidBrush(c))
            {
                DrawRightText(g, z.Celsius.ToString("0", CultureInfo.InvariantCulture) + "°", rowFont, b,
                    new RectangleF(row.Right - tempW, row.Top, tempW, row.Height));
            }
        }

        if (overflow)
        {
            int hidden = s.HotZones.Count - shown;
            double hiddenMax = s.HotZones[shown].Celsius;
            double hiddenMin = s.HotZones[s.HotZones.Count - 1].Celsius;
            string span = hiddenMax - hiddenMin >= 1.0
                ? hiddenMin.ToString("0", CultureInfo.InvariantCulture) + "-" + hiddenMax.ToString("0", CultureInfo.InvariantCulture) + "°"
                : hiddenMax.ToString("0", CultureInfo.InvariantCulture) + "°";
            DrawStripText(g, "+" + hidden.ToString(CultureInfo.InvariantCulture) + " 区 " + span,
                rowFont, GetStripTempColor(hiddenMax), new RectangleF(x, y + rowH * shown, w, rowH));
        }
    }

    private static double StripTempPercent(double celsius)
    {
        if (celsius <= 0.0)
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(100.0, (celsius - 20.0) / 70.0 * 100.0));
    }

    private Color GetStripTempColor(double celsius)
    {
        if (celsius >= 85.0) return DesignTokens.Colors.DangerStrong;
        if (celsius >= 70.0) return DesignTokens.Colors.Warning;
        if (celsius >= 55.0) return Color.FromArgb(176, 246, 152);
        if (celsius >= 40.0) return Color.FromArgb(120, 214, 168);
        return Color.FromArgb(96, 176, 222);
    }

    private static Color GetStripBatteryColor(bool known, int percent)
    {
        if (!known) return DesignTokens.Colors.SubtleText;
        if (percent >= 60) return Color.FromArgb(120, 222, 140);
        if (percent >= 30) return DesignTokens.Colors.Warning;
        return DesignTokens.Colors.DangerStrong;
    }

    private Color GetStripModeColor(PowerStripSnapshot s)
    {
        if (s.EnergySaverActive) return Color.FromArgb(134, 238, 150);
        if (string.Equals(s.PowerModeText, "性能", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(255, 166, 174);
        if (string.Equals(s.PowerModeText, "省电", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(134, 238, 150);
        return DesignTokens.Colors.TextStrong;
    }

    private static string FormatStripBatterySub(PowerStripSnapshot s)
    {
        if (s.BatteryCarePauseActive) return "养护暂停";
        if (s.RuntimeSecondsKnown)
        {
            int minutes = Math.Max(0, s.RuntimeSeconds / 60);
            int h = minutes / 60;
            int m = minutes % 60;
            return (h > 0
                ? h.ToString(CultureInfo.InvariantCulture) + "h" + m.ToString("00", CultureInfo.InvariantCulture)
                : m.ToString(CultureInfo.InvariantCulture) + "m") + " 剩余";
        }

        if (s.Charging) return "充电中";
        if (s.PluggedIn) return "外接供电";
        return s.EnergySaverActive ? "节能" : "--";
    }

    private void DrawStripText(Graphics g, string text, Font font, Color color, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text)) return;
        using (SolidBrush b = new SolidBrush(color))
        {
            DrawFittedText(g, text, font, b, rect);
        }
    }

    private void DrawStripGauge(Graphics g, RectangleF rect, double percent, Color fill)
    {
        if (rect.Width <= 1.0f) return;
        using (GraphicsPath track = RoundedRectangle(rect, rect.Height / 2.0f))
        using (SolidBrush tb = new SolidBrush(DesignTokens.White(30)))
        {
            g.FillPath(tb, track);
        }

        float w = (float)(rect.Width * Math.Max(0.0, Math.Min(100.0, percent)) / 100.0);
        if (w > 1.0f)
        {
            using (GraphicsPath p = RoundedRectangle(new RectangleF(rect.Left, rect.Top, w, rect.Height), rect.Height / 2.0f))
            using (SolidBrush b = new SolidBrush(DesignTokens.WithAlpha(fill, 235)))
            {
                g.FillPath(b, p);
            }
        }
    }

    private void DrawRightText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text)) return;
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Far;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool dispose = false;
            float size = baseFont.Size;
            while (size > 6.0f * this.LayerScale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (dispose) drawFont.Dispose();
                size -= 0.6f * this.LayerScale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                dispose = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);
            if (dispose) drawFont.Dispose();
        }
    }
}
