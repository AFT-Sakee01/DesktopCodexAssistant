using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Typographic render variant (WidgetSettings.NetworkMonitorRenderVariant == Typographic): OLED-safe,
// no-blue restyle. No borders, no fills - a compact header line over seven label/value rows
// separated by hairlines. Background stays the existing semi-transparent AppBackground.
internal sealed partial class NetworkMonitorForm
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
        RectangleF content = new RectangleF(padding, S(6), this.Width - padding * 2.0f, this.Height - S(12));
        float headerHeight = Math.Max(S(16), content.Height * 0.16f);
        RectangleF header = new RectangleF(content.Left, content.Top, content.Width, headerHeight);
        DrawTypographicHeader(g, header);

        float rowTop = header.Bottom + S(2);
        OledNetItem[] items = GetNetworkMonitorOledItems();
        float rowHeight = Math.Max(S(12), (content.Bottom - rowTop) / items.Length);
        Font labelFont = this.GetCachedUiFont(Math.Max(7.0f, rowHeight * 0.40f), FontStyle.Regular);
        for (int i = 0; i < items.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * rowHeight, content.Width, rowHeight);
            DrawTypographicRow(g, rowRect, items[i], labelFont, suppressFill);
        }
    }

    private void DrawTypographicHeader(Graphics g, RectangleF rect)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        string statusText = GetHeaderStatusText(accessState);
        OledVariantPainting.Severity severity = (accessState == NetworkAccessState.Online && HasFailedGfwProbe())
            ? OledVariantPainting.Severity.Warn
            : GetAccessStateSeverity(accessState);
        Color statusColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledTypographic.AccentGood,
            DesignTokens.OledTypographic.AccentWarn,
            DesignTokens.OledTypographic.AccentDanger,
            DesignTokens.OledTypographic.Primary);
        Font titleFont = this.GetCachedUiFont(Math.Max(9.0f, rect.Height * 0.56f), FontStyle.Bold);
        Font statusFont = this.GetCachedUiFont(Math.Max(8.0f, rect.Height * 0.48f), FontStyle.Regular);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.OledTypographic.Muted))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (SolidBrush publicBrush = new SolidBrush(DesignTokens.OledTypographic.Secondary))
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

    private void DrawTypographicRow(Graphics g, RectangleF rect, OledNetItem item, Font labelFont, bool suppressFill)
    {
        Color valueColor = OledVariantPainting.PickSeverityColor(
            item.Severity,
            DesignTokens.OledTypographic.AccentGood,
            DesignTokens.OledTypographic.AccentWarn,
            DesignTokens.OledTypographic.AccentDanger,
            DesignTokens.OledTypographic.Primary);
        RectangleF labelRect = new RectangleF(rect.Left, rect.Top, S(34), rect.Height);
        RectangleF valueRect = new RectangleF(labelRect.Right + S(4), rect.Top, rect.Right - labelRect.Right - S(4), rect.Height);
        using (SolidBrush labelBrush = new SolidBrush(DesignTokens.OledTypographic.Muted))
        using (SolidBrush valueBrush = new SolidBrush(valueColor))
        {
            DrawFittedText(g, item.Label, labelFont, labelBrush, labelRect, StringAlignment.Near);
            DrawFittedText(g, item.Value, labelFont, valueBrush, valueRect, StringAlignment.Near);
        }

        if (!suppressFill)
        {
            using (Pen pen = new Pen(DesignTokens.OledTypographic.Hairline, Math.Max(1.0f, S(1))))
            {
                g.DrawLine(pen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
            }
        }
    }
}
