using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// Test-only render harness for --render-operation: paints one representative frame of each
// OperationRenderVariant (Classic plus the four OLED-safe schemes added in 1.0.3.44) to a PNG for
// visual review, mirroring the other windows' render harnesses.
internal sealed partial class OperationForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        OperationRenderVariant[] variants =
        {
            OperationRenderVariant.Classic,
            OperationRenderVariant.Typographic,
            OperationRenderVariant.AmberHud,
            OperationRenderVariant.WarmCard,
            OperationRenderVariant.Phosphor
        };

        foreach (OperationRenderVariant variant in variants)
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.OperationRenderVariant = variant;
            settings.Normalize();

            using (OperationForm form = new OperationForm(
                settings,
                () => { },
                () => { },
                () => { },
                (title, message, icon) => { },
                () => true,
                () => true,
                () => true))
            {
                form.SetLayerScale(2.0f);
                form.MaximumSize = new Size(4000, 4000);
                form.Size = form.GetDesiredSize();

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawOperationWindow(g);
                    string path = Path.Combine(outputDir, "operation-" + variant.ToString().ToLowerInvariant() + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(variant.ToString() + " -> " + path);
                }
            }
        }
    }

    // Current-mode sample: real settings.ini (button size/offsets/variant/transparency), drawn
    // through the real DrawOperationWindow pipeline and composited like the on-screen window.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (OperationForm form = new OperationForm(
            settings,
            () => { },
            () => { },
            () => { },
            (title, message, icon) => { },
            () => true,
            () => true,
            () => true))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = form.GetDesiredSize();
            RenderSampleSupport.SaveComposited(
                outputDir,
                "operation-current.png",
                form.Width,
                form.Height,
                255,
                form.DrawOperationWindow);
        }
    }
}
