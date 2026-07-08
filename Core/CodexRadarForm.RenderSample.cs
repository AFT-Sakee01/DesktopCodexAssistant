using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

// Headless render harness: the Codex monitor is a layered tool window that screenshot tools cannot
// capture (it is not a Start-menu app), so `--render-codexradar --out <dir>` paints one representative
// frame of each render variant to a PNG for visual review during layout work. Test-only; not part of
// the normal runtime path.
internal sealed partial class CodexRadarForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        CodexRadarRenderVariant[] variants =
        {
            CodexRadarRenderVariant.EvenRow
        };

        foreach (CodexRadarRenderVariant variant in variants)
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.CodexRadarRenderVariant = variant;
            settings.Normalize();

            using (CodexRadarForm form = new CodexRadarForm(settings, null))
            {
                // Replicate a 2880x1800 @ 200% display: scale=2 matches real DPI, and CodexRadarWidth/
                // Height are already the real physical pixel size (see CodexRadarForm.cs's
                // `this.Size = new Size(this.currentSettings.CodexRadarWidth, ...)` - there is no
                // separate DPI multiplication at runtime). An earlier `* 2` here rendered a canvas
                // twice as wide as any real window ever is, which hid genuine overflow/truncation
                // (e.g. "13:00" clipping to "13...") that only shows up at the true width.
                form.SetLayerScale(2.0f);
                form.MaximumSize = new Size(4000, 4000);
                form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
                // Normalize() force-disables CodexRadarTestMode, so inject a populated snapshot
                // directly instead - this is what actually exercises the quota radar trend content.
                form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
                // Realistic long DeepSeek balance ("DS:¥473.36 ...") to exercise the EvenRow bottom
                // info panel's fit/centering with real-length text instead of the short "未配置".
                form.deepSeekBalanceSnapshot = new DeepSeekBalanceSnapshot
                {
                    ApiKeyConfigured = true,
                    Known = true,
                    IsAvailable = true,
                    Currency = "CNY",
                    BalanceCny = 473.36,
                    CheckedAtUtc = DateTime.UtcNow,
                    CheckedAtLocal = DateTime.Now
                };

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawCodexRadarContent(g);
                    string path = Path.Combine(
                        outputDir,
                        "codexradar-" + variant.ToString().ToLowerInvariant() + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(
                        variant.ToString() + " -> " + path +
                        " (" + form.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                        form.Height.ToString(CultureInfo.InvariantCulture) + ")");
                }

                // Also render EvenRow at a deliberately narrow width to reproduce the user's
                // truncation-prone case (short window, long DeepSeek balance).
                if (variant == CodexRadarRenderVariant.EvenRow)
                {
                    int narrowW = (int)(settings.CodexRadarWidth * 0.72f);
                    form.Size = new Size(narrowW, form.Height);
                    using (Bitmap nb = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics ng = Graphics.FromImage(nb))
                    {
                        ng.Clear(DesignTokens.Colors.AppBackground);
                        form.DrawCodexRadarContent(ng);
                        nb.Save(Path.Combine(outputDir, "codexradar-evenrow-narrow.png"), ImageFormat.Png);
                        Console.WriteLine("EvenRow(narrow) -> " + form.Width.ToString(CultureInfo.InvariantCulture) + "x" + form.Height.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }
    }

    // Current-mode sample: real settings.ini (size/variant/transparency/manual offsets) plus
    // whatever disk caches the constructor loaded (codex-radar-cache.ini, quota snapshots), drawn
    // through the real full-frame pipeline and composited like the on-screen layered window.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            if (form.codexRadarSnapshot == null)
            {
                // Cold start with no disk cache yet: fall back to the synthetic snapshot so the
                // frame is reviewable instead of blank. The PNG name still says "current" because
                // geometry, variant and transparency remain the user's real configuration.
                form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
            }

            RenderSampleSupport.SaveComposited(
                outputDir,
                "codexradar-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawCodexRadar);
        }
    }

    internal static void RenderRadarSolo(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(4.0f);
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
            CodexRadarSnapshot snap = form.GetCodexRadarDisplaySnapshot();
            using (Bitmap bmp = new Bitmap(160, 460, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                BurnInProtection.ConfigureGraphics(g, false);
                RectangleF radarRect = new RectangleF(66, 20, 28, 420);
                form.DrawCodexQuotaRadarVerticalLine(g, radarRect, snap == null ? null : snap.QuotaRadar, 1.5f);
                bmp.Save(Path.Combine(outputDir, "radar-solo.png"), ImageFormat.Png);
                Console.WriteLine("radar-solo QuotaRadar.Known=" + (snap != null && snap.QuotaRadar != null ? snap.QuotaRadar.Known.ToString() : "null"));
            }
        }
    }
}
