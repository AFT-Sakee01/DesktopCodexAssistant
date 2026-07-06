using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// WarmCard render variant (WidgetSettings.ConnectionCheckRenderVariant == WarmCard): OLED-safe,
// no-blue restyle. Low-luminance warm-gray filled cards, no border, colored status dot carries all
// semantic meaning. Background stays the existing semi-transparent AppBackground.
internal sealed partial class ConnectionCheckForm
{
    private void DrawContentWarmCard(Graphics g)
    {
        ConfigureGraphics(g);
        bool suppressFill = IsBurnInColorProtectionActive();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledCard.Muted, GetBorderOpacityAlpha()), Math.Max(1, S(1))))
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

        float gap = Math.Max(S(3), content.Width * 0.018f);
        float cardWidth = (content.Width - gap * 2.0f) / 3.0f;
        RectangleF firstRect = new RectangleF(content.Left, content.Top, cardWidth, content.Height);
        RectangleF secondRect = new RectangleF(firstRect.Right + gap, content.Top, cardWidth, content.Height);
        RectangleF thirdRect = new RectangleF(secondRect.Right + gap, content.Top, cardWidth, content.Height);

        float cornerRadius = Math.Max(S(4), content.Height * 0.18f);
        float dotDiameter = Math.Max(S(3), content.Height * 0.11f);
        // Must mirror OledVariantPainting.DrawDotCard's own text-rect math exactly (textLeft =
        // dotX + dotDiameter + cornerRadius*0.6, dotX = rect.Left + cornerRadius*0.9, textRect.Width =
        // rect.Right - textLeft - cornerRadius*0.5) or the fitted size can still overflow the real rect.
        float textAvailableWidth = cardWidth - dotDiameter - cornerRadius * 2.0f;

        string firstText = firstValue + " " + firstLabel;
        float maxSize = Math.Max(10.0f, content.Height * 0.24f);
        float minSize = Math.Max(7.5f, content.Height * 0.09f);
        float fittedSize = Math.Min(
            OledVariantPainting.FitFontSize(g, firstText, DesignTokens.UiFontFamily, FontStyle.Regular, maxSize, minSize, textAvailableWidth),
            Math.Min(
                OledVariantPainting.FitFontSize(g, secondValue, DesignTokens.UiFontFamily, FontStyle.Regular, maxSize, minSize, textAvailableWidth),
                OledVariantPainting.FitFontSize(g, thirdValue, DesignTokens.UiFontFamily, FontStyle.Regular, maxSize, minSize, textAvailableWidth)));
        Font font = this.fontCache.GetUi(fittedSize, FontStyle.Regular);

        DrawWarmCard(g, firstRect, firstText, firstSeverity, font, cornerRadius, dotDiameter, suppressFill);
        DrawWarmCard(g, secondRect, secondValue, secondSeverity, font, cornerRadius, dotDiameter, suppressFill);
        DrawWarmCard(g, thirdRect, thirdValue, thirdSeverity, font, cornerRadius, dotDiameter, suppressFill);
    }

    private void DrawWarmCard(Graphics g, RectangleF rect, string text, OledVariantPainting.Severity severity, Font font, float cornerRadius, float dotDiameter, bool suppressFill)
    {
        Color dotColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledCard.DotGood,
            DesignTokens.OledCard.DotWarn,
            DesignTokens.OledCard.DotDanger,
            DesignTokens.OledCard.Muted);
        OledVariantPainting.DrawDotCard(g, rect, text, dotColor, DesignTokens.OledCard.Text, DesignTokens.OledCard.CardFill, suppressFill, font, cornerRadius, dotDiameter);
    }
}
