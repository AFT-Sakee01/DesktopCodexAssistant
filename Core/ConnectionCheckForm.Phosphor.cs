using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Phosphor render variant (WidgetSettings.ConnectionCheckRenderVariant == Phosphor): OLED-safe,
// no-blue restyle. Single dim-green terminal text, zero shapes - the lowest lit-area-per-pixel of the
// four schemes. Background stays the existing semi-transparent AppBackground.
internal sealed partial class ConnectionCheckForm
{
    private void DrawContentPhosphor(Graphics g)
    {
        ConfigureGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Faint, GetBorderOpacityAlpha()), Math.Max(1, S(1))))
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

        Font font = this.fontCache.GetMono(Math.Max(10.0f, content.Height * 0.24f), FontStyle.Regular);
        float rowHeight = content.Height / 3.0f;
        RectangleF firstRow = new RectangleF(content.Left, content.Top, content.Width, rowHeight);
        RectangleF secondRow = new RectangleF(content.Left, firstRow.Bottom, content.Width, rowHeight);
        RectangleF thirdRow = new RectangleF(content.Left, secondRow.Bottom, content.Width, content.Bottom - secondRow.Bottom);

        DrawPhosphorRow(g, firstRow, TranslatePhosphorLabel(firstLabel), firstValue, firstSeverity, font);
        DrawPhosphorRow(g, secondRow, TranslatePhosphorLabel(secondLabel), secondValue, secondSeverity, font);
        DrawPhosphorRow(g, thirdRow, TranslatePhosphorLabel(thirdLabel), thirdValue, thirdSeverity, font);
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

    private static string TranslatePhosphorLabel(string label)
    {
        if (label == "评分") return "score";
        if (label == "归属") return "native";
        if (label == "类型") return "type";
        return label;
    }
}
