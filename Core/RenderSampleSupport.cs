using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

// Shared support for the "--render-* current" samples. Sample-mode PNGs are deterministic
// synthetic-data references painted on an opaque AppBackground; they intentionally ignore the
// user's settings.ini, so they never show what the configured window actually looks like.
// Current-mode fills that gap: the caller loads the real WidgetSettings, runs the window's real
// full-frame draw path (background + content at their real alphas) into a transparent ARGB layer,
// and this helper composites that layer over a neutral dark desktop stand-in so the translucent
// background reads the way it does on screen. Test-only; not part of the normal runtime path.
internal static class RenderSampleSupport
{
    // Solid dark stand-in for the wallpaper. Real wallpapers vary; a stable dark tone keeps
    // current-mode PNGs comparable across runs while still exercising the translucent background.
    public static readonly Color DesktopBackdrop = Color.FromArgb(255, 30, 30, 34);
    internal static int ProofLuminancePercent { get; set; } = 100;

    internal static void ApplyProofLuminance(Bitmap bitmap)
    {
        if (bitmap != null && ProofLuminancePercent < 100)
        {
            BurnInProtection.ApplyLuminance(bitmap, ProofLuminancePercent);
        }
    }

    public static void SaveComposited(
        string outputDir,
        string fileName,
        int width,
        int height,
        byte applicationAlpha,
        Action<Graphics> drawFullWindow)
    {
        Directory.CreateDirectory(outputDir);
        using (Bitmap layer = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        {
            using (Graphics g = Graphics.FromImage(layer))
            {
                g.Clear(Color.Transparent);
                drawFullWindow(g);
            }

            // Render-only acceptance hook: it uses the exact runtime bitmap transform while the
            // separate NightScheduleController self-test supplies simulated boundary times.
            ApplyProofLuminance(layer);

            using (Bitmap composed = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(composed))
                {
                    g.Clear(DesktopBackdrop);
                    if (applicationAlpha >= 255)
                    {
                        g.DrawImageUnscaled(layer, 0, 0);
                    }
                    else
                    {
                        DrawingUtil.DrawImageWithAlpha(g, layer, applicationAlpha);
                    }
                }

                string path = Path.Combine(outputDir, fileName);
                composed.Save(path, ImageFormat.Png);
                Console.WriteLine(
                    "current -> " + path +
                    " (" + width.ToString(CultureInfo.InvariantCulture) +
                    "x" + height.ToString(CultureInfo.InvariantCulture) +
                    ", appAlpha=" + applicationAlpha.ToString(CultureInfo.InvariantCulture) + ")");
            }
        }
    }
}
