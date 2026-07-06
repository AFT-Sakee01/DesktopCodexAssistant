using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Typographic render variant (WidgetSettings.PowerThermalRenderVariant == Typographic): OLED-safe,
// no-blue restyle. No borders, no fills - stacked value/label cells for power and battery, plus a
// thermal alert row per hot sensor. Background stays the existing semi-transparent AppBackground.
internal sealed partial class PowerThermalForm
{
    private void DrawContentTypographic(Graphics g)
    {
        ConfigureGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledTypographic.Muted, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = S(10);
        RectangleF content = new RectangleF(padding, S(5), this.Width - padding * 2.0f, this.Height - S(10));
        OledPowerItem[] thermalItems = GetOledThermalItems();
        float headerHeight = thermalItems.Length > 0 ? content.Height * 0.56f : content.Height;
        RectangleF headerRect = new RectangleF(content.Left, content.Top, content.Width, headerHeight);

        float half = headerRect.Width / 2.0f;
        RectangleF powerRect = new RectangleF(headerRect.Left, headerRect.Top, half, headerRect.Height);
        RectangleF batteryRect = new RectangleF(headerRect.Left + half, headerRect.Top, headerRect.Width - half, headerRect.Height);
        Font valueFont = this.fontCache.GetUi(Math.Max(11.0f, headerRect.Height * 0.36f), FontStyle.Regular);
        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, headerRect.Height * 0.15f), FontStyle.Regular);
        DrawTypographicItem(g, powerRect, GetOledPowerItem(), valueFont, labelFont);
        DrawTypographicItem(g, batteryRect, GetOledBatteryItem(), valueFont, labelFont);

        if (thermalItems.Length == 0)
        {
            return;
        }

        if (!suppressFill)
        {
            using (Pen pen = new Pen(DesignTokens.OledTypographic.Hairline, Math.Max(1.0f, S(1))))
            {
                g.DrawLine(pen, content.Left, headerRect.Bottom + S(2), content.Right, headerRect.Bottom + S(2));
            }
        }

        float rowTop = headerRect.Bottom + S(5);
        float rowHeight = Math.Max(S(10), (content.Bottom - rowTop) / thermalItems.Length);
        Font thermalFont = this.fontCache.GetUi(Math.Max(8.0f, rowHeight * 0.55f), FontStyle.Regular);
        for (int i = 0; i < thermalItems.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * rowHeight, content.Width, rowHeight);
            Color valueColor = OledVariantPainting.PickSeverityColor(
                thermalItems[i].Severity,
                DesignTokens.OledTypographic.AccentGood,
                DesignTokens.OledTypographic.AccentWarn,
                DesignTokens.OledTypographic.AccentDanger,
                DesignTokens.OledTypographic.Primary);
            RectangleF labelRect2 = new RectangleF(rowRect.Left, rowRect.Top, rowRect.Width * 0.6f, rowRect.Height);
            RectangleF valueRect2 = new RectangleF(labelRect2.Right, rowRect.Top, rowRect.Width - labelRect2.Width, rowRect.Height);
            using (SolidBrush labelBrush = new SolidBrush(DesignTokens.OledTypographic.Muted))
            using (SolidBrush valueBrush = new SolidBrush(valueColor))
            {
                DrawFittedText(g, thermalItems[i].Label, thermalFont, labelBrush, labelRect2, StringAlignment.Near);
                DrawFittedText(g, thermalItems[i].Value, thermalFont, valueBrush, valueRect2, StringAlignment.Far);
            }
        }
    }

    private void DrawTypographicItem(Graphics g, RectangleF rect, OledPowerItem item, Font valueFont, Font labelFont)
    {
        Color valueColor = OledVariantPainting.PickSeverityColor(
            item.Severity,
            DesignTokens.OledTypographic.AccentGood,
            DesignTokens.OledTypographic.AccentWarn,
            DesignTokens.OledTypographic.AccentDanger,
            DesignTokens.OledTypographic.Primary);
        float fittedSize = OledVariantPainting.FitFontSize(g, item.Value, DesignTokens.UiFontFamily, FontStyle.Regular, valueFont.Size, Math.Max(7.0f, valueFont.Size * 0.5f), rect.Width * 0.92f);
        Font fittedFont = Math.Abs(fittedSize - valueFont.Size) < 0.5f ? valueFont : this.fontCache.GetUi(fittedSize, FontStyle.Regular);
        OledVariantPainting.DrawStackedMetric(g, rect, item.Value, item.Label, valueColor, DesignTokens.OledTypographic.Muted, fittedFont, labelFont);
    }
}
