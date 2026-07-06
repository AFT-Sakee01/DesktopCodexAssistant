using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// AmberHud render variant (WidgetSettings.ConnectionCheckRenderVariant == AmberHud): OLED-safe,
// no-blue restyle. Single amber hue, thin hairline chips, mono uppercase labels - a night-instrument
// look. Background stays the existing semi-transparent AppBackground.
internal sealed partial class ConnectionCheckForm
{
    private void DrawContentAmberHud(Graphics g)
    {
        ConfigureGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledAmber.Dim, GetBorderOpacityAlpha()), Math.Max(1, S(1))))
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

        float gap = Math.Max(S(2), content.Width * 0.014f);
        float chipWidth = (content.Width - gap * 2.0f) / 3.0f;
        RectangleF firstRect = new RectangleF(content.Left, content.Top, chipWidth, content.Height);
        RectangleF secondRect = new RectangleF(firstRect.Right + gap, content.Top, chipWidth, content.Height);
        RectangleF thirdRect = new RectangleF(secondRect.Right + gap, content.Top, chipWidth, content.Height);

        string firstText = firstLabel + " " + firstValue;
        float maxSize = Math.Max(10.0f, content.Height * 0.26f);
        float minSize = Math.Max(7.5f, content.Height * 0.14f);
        float fittedSize = Math.Min(
            OledVariantPainting.FitFontSize(g, firstText, DesignTokens.MonoFontFamily, FontStyle.Bold, maxSize, minSize, chipWidth * 0.88f),
            Math.Min(
                OledVariantPainting.FitFontSize(g, secondValue, DesignTokens.MonoFontFamily, FontStyle.Bold, maxSize, minSize, chipWidth * 0.88f),
                OledVariantPainting.FitFontSize(g, thirdValue, DesignTokens.MonoFontFamily, FontStyle.Bold, maxSize, minSize, chipWidth * 0.88f)));
        Font font = this.fontCache.GetMono(fittedSize, FontStyle.Bold);
        float cornerRadius = Math.Max(S(1), content.Height * 0.06f);
        float borderWidth = Math.Max(1.0f, S(1));

        DrawAmberChip(g, firstRect, firstText, firstSeverity, font, cornerRadius, borderWidth, suppressFill);
        DrawAmberChip(g, secondRect, secondValue, secondSeverity, font, cornerRadius, borderWidth, suppressFill);
        DrawAmberChip(g, thirdRect, thirdValue, thirdSeverity, font, cornerRadius, borderWidth, suppressFill);
    }

    private void DrawAmberChip(Graphics g, RectangleF rect, string text, OledVariantPainting.Severity severity, Font font, float cornerRadius, float borderWidth, bool suppressFill)
    {
        Color lineColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledAmber.Bright,
            DesignTokens.OledAmber.Base,
            DesignTokens.OledAmber.Danger,
            DesignTokens.OledAmber.Dim);
        Color fillColor = DesignTokens.WithAlpha(lineColor, 22);
        OledVariantPainting.DrawHollowChip(g, rect, text, lineColor, lineColor, fillColor, suppressFill, font, cornerRadius, borderWidth);
    }
}
