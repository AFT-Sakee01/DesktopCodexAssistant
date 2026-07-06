using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Test-only render harness for --render-connectioncheck: paints every CleanIP badge icon
// (normal + burn-in-suppressed) plus a sample badge triplet to PNGs for visual review.
internal sealed partial class ConnectionCheckForm
{
    internal static void RenderCleanIpIconSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string[] iconClasses =
        {
            "fa-solid fa-shield-halved",
            "fa-solid fa-location-check",
            "fa-solid fa-router",
            "fa-solid fa-circle-minus",
            "fa-solid fa-house",
            "fa-solid fa-mobile-screen",
            "fa-solid fa-briefcase",
            "fa-solid fa-graduation-cap",
            "fa-solid fa-landmark",
            "fa-solid fa-network-wired",
            "fa-sharp fa-solid fa-server",
            "fa-solid fa-user-secret",
            "fa-solid fa-filter",
            "fa-solid fa-link",
            "fa-solid fa-circle-question"
        };

        ConnectionCheckForm form = new ConnectionCheckForm(new WidgetSettings());
        try
        {
            form.SetLayerScale(2.0f);
            int columns = 5;
            int rows = (iconClasses.Length + columns - 1) / columns;
            int cell = 120;
            int labelHeight = 26;
            int sectionHeader = 30;
            int sectionHeight = sectionHeader + rows * (cell + labelHeight);
            using (Font labelFont = new Font("Segoe UI", 9.0f))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(200, 200, 210)))
            {
                using (Bitmap bitmap = new Bitmap(columns * cell, sectionHeight * 2, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    BurnInProtection.ConfigureGraphics(g, false);
                    for (int section = 0; section < 2; section++)
                    {
                        bool suppress = section == 1;
                        int baseY = section * sectionHeight;
                        g.DrawString(suppress ? "hidden (decorative fill suppressed)" : "normal", labelFont, textBrush, 8.0f, baseY + 6.0f);
                        for (int i = 0; i < iconClasses.Length; i++)
                        {
                            int col = i % columns;
                            int row = i / columns;
                            float x = col * cell;
                            float y = baseY + sectionHeader + row * (cell + labelHeight);
                            RectangleF iconRect = new RectangleF(x + (cell - 84) / 2.0f, y + 8.0f, 84.0f, 84.0f);
                            form.DrawBadgeIcon(g, iconClasses[i], iconRect, Color.FromArgb(126, 200, 255), suppress);
                            using (StringFormat format = new StringFormat())
                            {
                                format.Alignment = StringAlignment.Center;
                                string label = iconClasses[i].Replace("fa-sharp ", string.Empty).Replace("fa-solid fa-", string.Empty);
                                g.DrawString(label, labelFont, textBrush, new RectangleF(x, y + cell - 8.0f, cell, labelHeight), format);
                            }
                        }
                    }

                    bitmap.Save(Path.Combine(outputDir, "cleanip-icons.png"), ImageFormat.Png);
                }

                using (Bitmap bitmap = new Bitmap(720, 132, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    BurnInProtection.ConfigureGraphics(g, false);
                    Color scoreText;
                    Color scoreBorder;
                    Color scoreFill;
                    BuildPalette(Color.FromArgb(53, 169, 82), out scoreText, out scoreBorder, out scoreFill);
                    Color nativeText;
                    Color nativeBorder;
                    Color nativeFill;
                    BuildPalette(Color.FromArgb(59, 130, 246), out nativeText, out nativeBorder, out nativeFill);
                    Color typeText;
                    Color typeBorder;
                    Color typeFill;
                    BuildPalette(Color.FromArgb(168, 85, 247), out typeText, out typeBorder, out typeFill);
                    form.DrawBadgeTriplet(
                        g,
                        new RectangleF(8.0f, 8.0f, 704.0f, 116.0f),
                        "fa-solid fa-shield-halved",
                        "85",
                        "fa-solid fa-location-check",
                        "原生IP",
                        "fa-solid fa-house",
                        "住宅IP",
                        scoreText,
                        scoreBorder,
                        scoreFill,
                        nativeText,
                        nativeBorder,
                        nativeFill,
                        typeText,
                        typeBorder,
                        typeFill);
                    bitmap.Save(Path.Combine(outputDir, "cleanip-badges.png"), ImageFormat.Png);
                }
            }
        }
        finally
        {
            form.Close();
            form.Dispose();
        }
    }

    // Renders every ConnectionCheckRenderVariant (Classic plus the four OLED-safe schemes added in
    // 1.0.3.44) against a healthy and a risky test snapshot, so a reviewer can compare all five side
    // by side in one PNG instead of switching the dropdown and re-screenshotting five times.
    internal static void RenderVariantSamples(string outputDir)
    {
        RenderRestyleVariantSamples(outputDir);
    }

    internal static void RenderRestyleVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ConnectionCheckRenderVariant[] variants =
        {
            ConnectionCheckRenderVariant.Classic,
            ConnectionCheckRenderVariant.Typographic,
            ConnectionCheckRenderVariant.AmberHud,
            ConnectionCheckRenderVariant.WarmCard,
            ConnectionCheckRenderVariant.Phosphor
        };
        CleanIpBadgeTestMode[] modes =
        {
            CleanIpBadgeTestMode.NativeResidential,
            CleanIpBadgeTestMode.ProxyRisk
        };

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        // ConnectionCheckWidth/Height are already the real physical pixel size; an earlier *2 here
        // rendered double-size cells that hid true-width truncation. Same fix as
        // CodexRadarForm.RenderSample.cs.
        int cellWidth = settings.ConnectionCheckWidth;
        int cellHeight = settings.ConnectionCheckHeight;
        int labelHeight = 22;
        int rowHeight = cellHeight + labelHeight;
        int gap = 12;

        using (Bitmap bitmap = new Bitmap((cellWidth + gap) * modes.Length + gap, (rowHeight + gap) * variants.Length + gap, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        using (Font labelFont = new Font("Segoe UI", 9.0f))
        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(200, 200, 210)))
        using (StringFormat labelFormat = new StringFormat())
        {
            g.Clear(DesignTokens.Colors.AppBackground);
            BurnInProtection.ConfigureGraphics(g, false);
            labelFormat.Alignment = StringAlignment.Near;

            for (int row = 0; row < variants.Length; row++)
            {
                bool wroteSingleVariant = false;
                for (int col = 0; col < modes.Length; col++)
                {
                    settings.ConnectionCheckRenderVariant = variants[row];
                    settings.CleanIpBadgeTestMode = modes[col];
                    ConnectionCheckForm form = new ConnectionCheckForm(settings);
                    try
                    {
                        form.SetLayerScale(2.0f);
                        form.Width = cellWidth;
                        form.Height = cellHeight;
                        form.currentSettings.ConnectionCheckRenderVariant = variants[row];
                        form.currentSettings.CleanIpBadgeTestMode = modes[col];
                        form.snapshot = form.reader.GetSnapshot(form.currentSettings);

                        int x = gap + col * (cellWidth + gap);
                        int y = gap + row * (rowHeight + gap);
                        g.DrawString(variants[row].ToString() + " / " + modes[col].ToString(), labelFont, textBrush, new RectangleF(x, y, cellWidth, labelHeight), labelFormat);

                        using (Bitmap cellBitmap = new Bitmap(cellWidth, cellHeight, PixelFormat.Format32bppPArgb))
                        using (Graphics cellGraphics = Graphics.FromImage(cellBitmap))
                        {
                            cellGraphics.Clear(DesignTokens.Colors.AppBackground);
                            form.DrawContent(cellGraphics);
                            if (!wroteSingleVariant && modes[col] == CleanIpBadgeTestMode.NativeResidential)
                            {
                                cellBitmap.Save(
                                    Path.Combine(outputDir, "connectioncheck-" + variants[row].ToString().ToLowerInvariant() + ".png"),
                                    ImageFormat.Png);
                                wroteSingleVariant = true;
                            }

                            g.DrawImageUnscaled(cellBitmap, x, y + labelHeight);
                        }
                    }
                    finally
                    {
                        form.Close();
                        form.Dispose();
                    }
                }
            }

            bitmap.Save(Path.Combine(outputDir, "cleanip-restyle-variants.png"), ImageFormat.Png);
        }
    }

    // Current-mode sample: real settings.ini (size/variant/transparency/test mode). Without a
    // completed CleanIP request the reader snapshot shows the real cold-start placeholder state;
    // geometry and styling stay the user's real configuration.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (ConnectionCheckForm form = new ConnectionCheckForm(settings))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.ConnectionCheckWidth, settings.ConnectionCheckHeight);
            form.snapshot = form.reader.GetSnapshot(form.currentSettings);
            RenderSampleSupport.SaveComposited(
                outputDir,
                "connectioncheck-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawConnectionCheckWindow);
        }
    }
}
