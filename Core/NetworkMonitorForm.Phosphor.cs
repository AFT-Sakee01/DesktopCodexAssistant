using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Phosphor render variant (WidgetSettings.NetworkMonitorRenderVariant == Phosphor): OLED-safe,
// no-blue restyle. Single dim-green terminal text, zero shapes. Background stays the existing
// semi-transparent AppBackground.
internal sealed partial class NetworkMonitorForm
{
    private static readonly string[] PhosphorRowPrefixes = { "ip4", "ip6", "if", "dns", "wifi", "ping", "gfw" };

    private void DrawContentPhosphor(Graphics g)
    {
        ConfigureGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Faint, DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        float padding = S(10);
        RectangleF content = new RectangleF(padding, S(6), this.Width - padding * 2.0f, this.Height - S(12));
        float headerHeight = Math.Max(S(16), content.Height * 0.16f);
        RectangleF header = new RectangleF(content.Left, content.Top, content.Width, headerHeight);
        DrawPhosphorHeader(g, header);

        float rowTop = header.Bottom + S(2);
        OledNetItem[] items = GetNetworkMonitorOledItems();
        float rowHeight = Math.Max(S(12), (content.Bottom - rowTop) / items.Length);
        float maxSize = Math.Max(7.0f, rowHeight * 0.42f);
        float minSize = Math.Max(6.0f, rowHeight * 0.24f);
        for (int i = 0; i < items.Length; i++)
        {
            RectangleF rowRect = new RectangleF(content.Left, rowTop + i * rowHeight, content.Width, rowHeight);
            string full = PhosphorRowPrefixes[i] + ": " + items[i].Value;
            float fittedSize = OledVariantPainting.FitFontSize(g, full, DesignTokens.MonoFontFamily, FontStyle.Regular, maxSize, minSize, rowRect.Width * 0.96f);
            Font font = this.GetCachedMonoFont(fittedSize, FontStyle.Regular);
            Color valueColor = OledVariantPainting.PickSeverityColor(
                items[i].Severity,
                DesignTokens.OledPhosphor.Bright,
                DesignTokens.OledPhosphor.Warn,
                DesignTokens.OledPhosphor.Danger,
                DesignTokens.OledPhosphor.Base);
            OledVariantPainting.DrawTerminalRow(g, rowRect, PhosphorRowPrefixes[i], items[i].Value, DesignTokens.OledPhosphor.Dim, valueColor, font);
        }
    }

    private void DrawPhosphorHeader(Graphics g, RectangleF rect)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        string statusText = GetHeaderStatusText(accessState);
        OledVariantPainting.Severity severity = (accessState == NetworkAccessState.Online && HasFailedGfwProbe())
            ? OledVariantPainting.Severity.Warn
            : GetAccessStateSeverity(accessState);
        Color statusColor = OledVariantPainting.PickSeverityColor(
            severity,
            DesignTokens.OledPhosphor.Bright,
            DesignTokens.OledPhosphor.Warn,
            DesignTokens.OledPhosphor.Danger,
            DesignTokens.OledPhosphor.Base);
        Font titleFont = this.GetCachedMonoFont(Math.Max(9.0f, rect.Height * 0.56f), FontStyle.Regular);
        Font statusFont = this.GetCachedMonoFont(Math.Max(8.0f, rect.Height * 0.48f), FontStyle.Regular);
        using (SolidBrush titleBrush = new SolidBrush(DesignTokens.OledPhosphor.Dim))
        using (SolidBrush statusBrush = new SolidBrush(statusColor))
        using (SolidBrush publicBrush = new SolidBrush(DesignTokens.OledPhosphor.Base))
        {
            RectangleF titleRect = new RectangleF(rect.Left, rect.Top, rect.Width * 0.24f, rect.Height);
            DrawFittedText(g, "net>", titleFont, titleBrush, titleRect, StringAlignment.Near);

            string publicIp = BuildPublicAddressText("pub");
            RectangleF publicRect = new RectangleF(rect.Left + rect.Width * 0.60f, rect.Top, rect.Width * 0.40f, rect.Height);
            RectangleF statusRect = new RectangleF(titleRect.Right + S(4), rect.Top, publicRect.Left - titleRect.Right - S(4), rect.Height);
            DrawFittedText(g, statusText, statusFont, statusBrush, statusRect, StringAlignment.Near);
            DrawFittedText(g, publicIp, statusFont, publicBrush, publicRect, StringAlignment.Far);
        }
    }
}
