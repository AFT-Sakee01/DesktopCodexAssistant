using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// WarmCard render variant (WidgetSettings.NetworkMonitorRenderVariant == WarmCard): OLED-safe,
// no-blue restyle. Low-luminance warm-gray filled row cards, status dot carries severity.
// Background stays the existing semi-transparent AppBackground.
internal sealed partial class NetworkMonitorForm
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
        RectangleF content = new RectangleF(padding, S(6), this.Width - padding * 2.0f, this.Height - S(12));
        float headerHeight = Math.Max(S(16), content.Height * 0.16f);
        RectangleF header = new RectangleF(content.Left, content.Top, content.Width, headerHeight);
        DrawWarmCardHeader(g, header);

        float rowTop = header.Bottom + S(3);
        OledNetItem[] items = GetNetworkMonitorOledItems();
        float rowGap = Math.Max(1.0f, S(1));
        float rowHeight = Math.Max(S(12), (content.Bottom - rowTop - rowGap * (items.Length - 1)) / items.Length);
        float cornerRadius = Math.Max(1.0f, rowHeight * 0.22f);
        float dotDiameter = Math.Max(1.0f, rowHeight * 0.20f);
        float maxSize = Math.Max(7.5f, rowHeight * 0.48f);
        float minSize = Math.Max(6.0f, rowHeight * 0.24f);
        float textAvailableWidth = content.Width - dotDiameter - cornerRadius * 2.0f;
        for (int i = 0; i < items.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * (rowHeight + rowGap), content.Width, rowHeight);
            string text = items[i].Label + "  " + items[i].Value;
            float fittedSize = OledVariantPainting.FitFontSize(g, text, DesignTokens.UiFontFamily, FontStyle.Regular, maxSize, minSize, textAvailableWidth);
            Font font = this.GetCachedUiFont(fittedSize, FontStyle.Regular);
            Color dotColor = OledVariantPainting.PickSeverityColor(
                items[i].Severity,
                DesignTokens.OledCard.DotGood,
                DesignTokens.OledCard.DotWarn,
                DesignTokens.OledCard.DotDanger,
                DesignTokens.OledCard.Muted);
            OledVariantPainting.DrawDotCard(g, rowRect, text, dotColor, DesignTokens.OledCard.Text, DesignTokens.OledCard.CardFill, suppressFill, font, cornerRadius, dotDiameter);
        }
    }

    private void DrawWarmCardHeader(Graphics g, RectangleF rect)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        string statusText = GetHeaderStatusText(accessState);
        OledVariantPainting.Severity severity = (accessState == NetworkAccessState.Online && HasFailedGfwProbe())
            ? OledVariantPainting.Severity.Warn
            : GetAccessStateSeverity(accessState);
        Color statusColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledCard.DotGood,
            DesignTokens.OledCard.DotWarn,
            DesignTokens.OledCard.DotDanger,
            DesignTokens.OledCard.Muted);
        Font titleFont = this.GetCachedUiFont(Math.Max(9.0f, rect.Height * 0.56f), FontStyle.Bold);
        Font statusFont = this.GetCachedUiFont(Math.Max(8.0f, rect.Height * 0.48f), FontStyle.Regular);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.OledCard.Muted))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (SolidBrush publicBrush = new SolidBrush(DesignTokens.OledCard.Text))
        {
            RectangleF titleRect = new RectangleF(rect.Left, rect.Top, rect.Width * 0.22f, rect.Height);
            DrawFittedText(g, "NETWORK", titleFont, titleBrush, titleRect, StringAlignment.Near);

            string publicIp = BuildPublicAddressText("公网");
            RectangleF publicRect = new RectangleF(rect.Left + rect.Width * 0.60f, rect.Top, rect.Width * 0.40f, rect.Height);
            RectangleF statusRect = new RectangleF(titleRect.Right + S(4), rect.Top, publicRect.Left - titleRect.Right - S(4), rect.Height);
            DrawFittedText(g, statusText, statusFont, statusBrush, statusRect, StringAlignment.Near);
            DrawFittedText(g, publicIp, statusFont, publicBrush, publicRect, StringAlignment.Far);
        }
    }
}
