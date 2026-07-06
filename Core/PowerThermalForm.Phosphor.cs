using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Phosphor render variant (WidgetSettings.PowerThermalRenderVariant == Phosphor): OLED-safe, no-blue
// restyle. Single dim-green terminal text, zero shapes. Background stays the existing semi-transparent
// AppBackground.
internal sealed partial class PowerThermalForm
{
    private void DrawContentPhosphor(Graphics g)
    {
        ConfigureGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Faint, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = S(10);
        RectangleF content = new RectangleF(padding, S(5), this.Width - padding * 2.0f, this.Height - S(10));
        OledPowerItem[] thermalItems = GetOledThermalItems();
        float headerHeight = thermalItems.Length > 0 ? content.Height * 0.5f : content.Height;
        RectangleF headerRect = new RectangleF(content.Left, content.Top, content.Width, headerHeight);

        float half = headerRect.Width / 2.0f;
        RectangleF powerRect = new RectangleF(headerRect.Left, headerRect.Top, half, headerRect.Height);
        RectangleF batteryRect = new RectangleF(headerRect.Left + half, headerRect.Top, headerRect.Width - half, headerRect.Height);
        OledPowerItem power = GetOledPowerItem();
        OledPowerItem battery = GetOledBatteryItem();
        // Short English prefixes ("pwr"/"bat") instead of the Chinese labels used elsewhere - this
        // window's default 120x110 size halves into ~55px-wide cells, too narrow for "充电中: 45W".
        const string powerPrefix = "pwr";
        const string batteryPrefix = "bat";
        float headerMax = Math.Max(7.5f, headerRect.Height * 0.30f);
        float headerMin = Math.Max(6.0f, headerRect.Height * 0.16f);
        float headerFitted = Math.Min(
            OledVariantPainting.FitFontSize(g, powerPrefix + ": " + power.Value, DesignTokens.MonoFontFamily, FontStyle.Regular, headerMax, headerMin, powerRect.Width * 0.92f),
            OledVariantPainting.FitFontSize(g, batteryPrefix + ": " + battery.Value, DesignTokens.MonoFontFamily, FontStyle.Regular, headerMax, headerMin, batteryRect.Width * 0.92f));
        Font headerFont = this.fontCache.GetMono(headerFitted, FontStyle.Regular);
        DrawPhosphorRow(g, powerRect, powerPrefix, power.Value, power.Severity, headerFont);
        DrawPhosphorRow(g, batteryRect, batteryPrefix, battery.Value, battery.Severity, headerFont);

        if (thermalItems.Length == 0)
        {
            return;
        }

        float rowTop = headerRect.Bottom + S(3);
        float rowHeight = Math.Max(S(10), (content.Bottom - rowTop) / thermalItems.Length);
        float rowMax = Math.Max(7.0f, rowHeight * 0.5f);
        float rowMin = Math.Max(6.0f, rowHeight * 0.24f);
        for (int i = 0; i < thermalItems.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * rowHeight, content.Width, rowHeight);
            string full = thermalItems[i].Label + ": " + thermalItems[i].Value;
            float fitted = OledVariantPainting.FitFontSize(g, full, DesignTokens.MonoFontFamily, FontStyle.Regular, rowMax, rowMin, rowRect.Width * 0.96f);
            Font font = this.fontCache.GetMono(fitted, FontStyle.Regular);
            DrawPhosphorRow(g, rowRect, thermalItems[i].Label, thermalItems[i].Value, thermalItems[i].Severity, font);
        }
    }

    private void DrawPhosphorRow(Graphics g, RectangleF rect, string prefix, string value, OledVariantPainting.Severity severity, Font font)
    {
        Color valueColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledPhosphor.Bright,
            DesignTokens.OledPhosphor.Warn,
            DesignTokens.OledPhosphor.Danger,
            DesignTokens.OledPhosphor.Base);
        OledVariantPainting.DrawTerminalRow(g, rect, prefix, value, DesignTokens.OledPhosphor.Dim, valueColor, font);
    }
}
