using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Typographic render variant (WidgetSettings.ConnectionCheckRenderVariant == Typographic): OLED-safe,
// no-blue restyle. No borders, no fills - three stacked value/label columns separated by hairlines.
// Background stays the existing semi-transparent AppBackground; only the content layer changes.
internal sealed partial class ConnectionCheckForm
{
    private void DrawContentTypographic(Graphics g)
    {
        ConfigureGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledTypographic.Muted, GetBorderOpacityAlpha()), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = Math.Max(S(4), this.Width * 0.024f);
        float verticalPadding = Math.Max(S(3), this.Height * 0.05f);
        RectangleF content = new RectangleF(padding, verticalPadding, this.Width - padding * 2.0f, this.Height - verticalPadding * 2.0f);

        string firstLabel;
        string firstValue;
        OledVariantPainting.Severity firstSeverity;
        string secondLabel;
        string secondValue;
        OledVariantPainting.Severity secondSeverity;
        string thirdLabel;
        string thirdValue;
        OledVariantPainting.Severity thirdSeverity;
        GetOledDisplayItems(out firstLabel, out firstValue, out firstSeverity, out secondLabel, out secondValue, out secondSeverity, out thirdLabel, out thirdValue, out thirdSeverity);

        float columnWidth = content.Width / 3.0f;
        RectangleF firstRect = new RectangleF(content.Left, content.Top, columnWidth, content.Height);
        RectangleF secondRect = new RectangleF(firstRect.Right, content.Top, columnWidth, content.Height);
        RectangleF thirdRect = new RectangleF(secondRect.Right, content.Top, content.Right - secondRect.Right, content.Height);

        float maxValueSize = Math.Max(14.0f, content.Height * 0.40f);
        float minValueSize = Math.Max(10.0f, content.Height * 0.16f);
        float columnInset = columnWidth * 0.90f;
        float fittedSize = Math.Min(
            OledVariantPainting.FitFontSize(g, firstValue, DesignTokens.UiFontFamily, FontStyle.Regular, maxValueSize, minValueSize, columnInset),
            Math.Min(
                OledVariantPainting.FitFontSize(g, secondValue, DesignTokens.UiFontFamily, FontStyle.Regular, maxValueSize, minValueSize, columnInset),
                OledVariantPainting.FitFontSize(g, thirdValue, DesignTokens.UiFontFamily, FontStyle.Regular, maxValueSize, minValueSize, columnInset)));
        Font valueFont = this.fontCache.GetUi(fittedSize, FontStyle.Regular);
        Font labelFont = this.fontCache.GetUi(Math.Max(8.5f, content.Height * 0.15f), FontStyle.Regular);

        DrawTypographicColumn(g, firstRect, firstLabel, firstValue, firstSeverity, valueFont, labelFont);
        DrawTypographicColumn(g, secondRect, secondLabel, secondValue, secondSeverity, valueFont, labelFont);
        DrawTypographicColumn(g, thirdRect, thirdLabel, thirdValue, thirdSeverity, valueFont, labelFont);

        if (!suppressFill)
        {
            Color hairline = DesignTokens.OledTypographic.Hairline;
            OledVariantPainting.DrawHairlineSeparatorVertical(g, firstRect.Right, content.Top + content.Height * 0.15f, content.Bottom - content.Height * 0.15f, hairline, Math.Max(1.0f, S(1)));
            OledVariantPainting.DrawHairlineSeparatorVertical(g, secondRect.Right, content.Top + content.Height * 0.15f, content.Bottom - content.Height * 0.15f, hairline, Math.Max(1.0f, S(1)));
        }
    }

    private void DrawTypographicColumn(Graphics g, RectangleF rect, string label, string value, OledVariantPainting.Severity severity, Font valueFont, Font labelFont)
    {
        Color valueColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledTypographic.AccentGood,
            DesignTokens.OledTypographic.AccentWarn,
            DesignTokens.OledTypographic.AccentDanger,
            DesignTokens.OledTypographic.Primary);
        OledVariantPainting.DrawStackedMetric(g, rect, value, label, valueColor, DesignTokens.OledTypographic.Muted, valueFont, labelFont);
    }
}
