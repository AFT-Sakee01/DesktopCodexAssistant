using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// WarmCard render variant (WidgetSettings.PowerThermalRenderVariant == WarmCard): OLED-safe, no-blue
// restyle. Low-luminance warm-gray filled cards for power/battery, status dot per thermal alert.
// Background stays the existing semi-transparent AppBackground.
internal sealed partial class PowerThermalForm
{
    private void DrawContentWarmCard(Graphics g)
    {
        ConfigureGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledCard.Muted, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = S(10);
        RectangleF content = new RectangleF(padding, S(5), this.Width - padding * 2.0f, this.Height - S(10));
        OledPowerItem[] thermalItems = GetOledThermalItems();
        float gap = Math.Max(1.0f, S(3));
        float headerHeight = thermalItems.Length > 0 ? content.Height * 0.52f : content.Height;
        RectangleF headerRect = new RectangleF(content.Left, content.Top, content.Width, headerHeight);

        float half = (headerRect.Width - gap) / 2.0f;
        RectangleF powerRect = new RectangleF(headerRect.Left, headerRect.Top, half, headerRect.Height);
        RectangleF batteryRect = new RectangleF(powerRect.Right + gap, headerRect.Top, headerRect.Width - half - gap, headerRect.Height);
        OledPowerItem power = GetOledPowerItem();
        OledPowerItem battery = GetOledBatteryItem();
        // Header cells stack value-over-label (DrawDotCardStacked) rather than sharing one line
        // (DrawDotCard): this window's default 120x110 size is the tightest of the six, and once
        // split into two side-by-side cells there is no readable single-line size that fits
        // "45W 充电中" past the dot and card padding - see the CodexRadar WarmCard top band for the
        // same fix applied to a wider window.
        float cornerRadius = Math.Max(2.0f, headerRect.Height * 0.16f);
        float dotDiameter = Math.Max(2.0f, headerRect.Height * 0.11f);
        float valueMax = Math.Max(9.0f, headerRect.Height * 0.30f);
        float valueMin = Math.Max(7.0f, headerRect.Height * 0.16f);
        float powerTextWidth = powerRect.Width - dotDiameter - cornerRadius * 2.0f;
        float batteryTextWidth = batteryRect.Width - dotDiameter - cornerRadius * 2.0f;
        float valueFitted = Math.Min(
            OledVariantPainting.FitFontSize(g, power.Value, DesignTokens.UiFontFamily, FontStyle.Regular, valueMax, valueMin, powerTextWidth),
            OledVariantPainting.FitFontSize(g, battery.Value, DesignTokens.UiFontFamily, FontStyle.Regular, valueMax, valueMin, batteryTextWidth));
        Font valueFont = this.fontCache.GetUi(valueFitted, FontStyle.Regular);
        Font labelFont = this.fontCache.GetUi(Math.Max(6.5f, headerRect.Height * 0.13f), FontStyle.Regular);
        DrawWarmStackedItem(g, powerRect, power, valueFont, labelFont, cornerRadius, dotDiameter, suppressFill);
        DrawWarmStackedItem(g, batteryRect, battery, valueFont, labelFont, cornerRadius, dotDiameter, suppressFill);

        if (thermalItems.Length == 0)
        {
            return;
        }

        float rowTop = headerRect.Bottom + gap;
        float rowGap = Math.Max(1.0f, S(2));
        float rowHeight = Math.Max(S(10), (content.Bottom - rowTop - rowGap * (thermalItems.Length - 1)) / thermalItems.Length);
        float rowCornerRadius = Math.Max(2.0f, rowHeight * 0.24f);
        float rowDotDiameter = Math.Max(2.0f, rowHeight * 0.20f);
        float rowMax = Math.Max(8.0f, rowHeight * 0.44f);
        float rowMin = Math.Max(6.5f, rowHeight * 0.22f);
        float rowTextWidth = content.Width - rowDotDiameter - rowCornerRadius * 2.0f;
        for (int i = 0; i < thermalItems.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * (rowHeight + rowGap), content.Width, rowHeight);
            string text = thermalItems[i].Label + "  " + thermalItems[i].Value;
            float fitted = OledVariantPainting.FitFontSize(g, text, DesignTokens.UiFontFamily, FontStyle.Regular, rowMax, rowMin, rowTextWidth);
            Font font = this.fontCache.GetUi(fitted, FontStyle.Regular);
            DrawWarmItem(g, rowRect, text, thermalItems[i].Severity, font, rowCornerRadius, rowDotDiameter, suppressFill);
        }
    }

    private void DrawWarmItem(Graphics g, RectangleF rect, string text, OledVariantPainting.Severity severity, Font font, float cornerRadius, float dotDiameter, bool suppressFill)
    {
        Color dotColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledCard.DotGood,
            DesignTokens.OledCard.DotWarn,
            DesignTokens.OledCard.DotDanger,
            DesignTokens.OledCard.Muted);
        OledVariantPainting.DrawDotCard(g, rect, text, dotColor, DesignTokens.OledCard.Text, DesignTokens.OledCard.CardFill, suppressFill, font, cornerRadius, dotDiameter);
    }

    private void DrawWarmStackedItem(Graphics g, RectangleF rect, OledPowerItem item, Font valueFont, Font labelFont, float cornerRadius, float dotDiameter, bool suppressFill)
    {
        Color dotColor = OledVariantPainting.PickSeverityColor(
            item.Severity,
            DesignTokens.OledCard.DotGood,
            DesignTokens.OledCard.DotWarn,
            DesignTokens.OledCard.DotDanger,
            DesignTokens.OledCard.Muted);
        OledVariantPainting.DrawDotCardStacked(g, rect, item.Value, item.Label, dotColor, DesignTokens.OledCard.Text, DesignTokens.OledCard.Muted, DesignTokens.OledCard.CardFill, suppressFill, valueFont, labelFont, cornerRadius, dotDiameter);
    }
}
