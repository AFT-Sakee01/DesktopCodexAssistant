using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// AmberHud render variant (WidgetSettings.PowerThermalRenderVariant == AmberHud): OLED-safe, no-blue
// restyle. Single amber hue, thin hairline chips for power/battery, plus a chip per thermal alert.
// Background stays the existing semi-transparent AppBackground.
internal sealed partial class PowerThermalForm
{
    private void DrawContentAmberHud(Graphics g)
    {
        ConfigureGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledAmber.Dim, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = S(10);
        RectangleF content = new RectangleF(padding, S(5), this.Width - padding * 2.0f, this.Height - S(10));
        OledPowerItem[] thermalItems = GetOledThermalItems();
        float gap = Math.Max(1.0f, S(3));
        float headerHeight = thermalItems.Length > 0 ? content.Height * 0.50f : content.Height;
        RectangleF headerRect = new RectangleF(content.Left, content.Top, content.Width, headerHeight);

        float half = (headerRect.Width - gap) / 2.0f;
        RectangleF powerRect = new RectangleF(headerRect.Left, headerRect.Top, half, headerRect.Height);
        RectangleF batteryRect = new RectangleF(powerRect.Right + gap, headerRect.Top, headerRect.Width - half - gap, headerRect.Height);
        OledPowerItem power = GetOledPowerItem();
        OledPowerItem battery = GetOledBatteryItem();
        float cornerRadius = Math.Max(1.0f, headerRect.Height * 0.10f);
        float borderWidth = Math.Max(1.0f, S(1));
        float headerMax = Math.Max(9.0f, headerRect.Height * 0.24f);
        float headerMin = Math.Max(6.5f, headerRect.Height * 0.12f);
        float headerFitted = Math.Min(
            OledVariantPainting.FitFontSize(g, power.Label + " " + power.Value, DesignTokens.MonoFontFamily, FontStyle.Bold, headerMax, headerMin, powerRect.Width * 0.90f),
            OledVariantPainting.FitFontSize(g, battery.Label + " " + battery.Value, DesignTokens.MonoFontFamily, FontStyle.Bold, headerMax, headerMin, batteryRect.Width * 0.90f));
        Font headerFont = this.fontCache.GetMono(headerFitted, FontStyle.Bold);
        DrawAmberItem(g, powerRect, power.Label + " " + power.Value, power.Severity, headerFont, cornerRadius, borderWidth, suppressFill);
        DrawAmberItem(g, batteryRect, battery.Label + " " + battery.Value, battery.Severity, headerFont, cornerRadius, borderWidth, suppressFill);

        if (thermalItems.Length == 0)
        {
            return;
        }

        float rowTop = headerRect.Bottom + gap;
        float rowGap = Math.Max(1.0f, S(2));
        float rowHeight = Math.Max(S(10), (content.Bottom - rowTop - rowGap * (thermalItems.Length - 1)) / thermalItems.Length);
        float rowMax = Math.Max(8.0f, rowHeight * 0.5f);
        float rowMin = Math.Max(6.5f, rowHeight * 0.26f);
        for (int i = 0; i < thermalItems.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * (rowHeight + rowGap), content.Width, rowHeight);
            string text = thermalItems[i].Label + " " + thermalItems[i].Value;
            float fitted = OledVariantPainting.FitFontSize(g, text, DesignTokens.MonoFontFamily, FontStyle.Bold, rowMax, rowMin, rowRect.Width * 0.92f);
            Font font = this.fontCache.GetMono(fitted, FontStyle.Bold);
            DrawAmberItem(g, rowRect, text, thermalItems[i].Severity, font, cornerRadius, borderWidth, suppressFill);
        }
    }

    private void DrawAmberItem(Graphics g, RectangleF rect, string text, OledVariantPainting.Severity severity, Font font, float cornerRadius, float borderWidth, bool suppressFill)
    {
        Color lineColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledAmber.Bright,
            DesignTokens.OledAmber.Base,
            DesignTokens.OledAmber.Danger,
            DesignTokens.OledAmber.Dim);
        Color fillColor = DesignTokens.WithAlpha(lineColor, 20);
        OledVariantPainting.DrawHollowChip(g, rect, text, lineColor, lineColor, fillColor, suppressFill, font, cornerRadius, borderWidth);
    }
}
