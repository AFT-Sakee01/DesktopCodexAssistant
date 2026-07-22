using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// Test-only render harness for --render-operation: paints the single retained RadialDial frame
// plus the Operation companion windows.
internal sealed partial class OperationForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        OperationRenderVariant[] variants = { OperationRenderVariant.RadialDial };

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
                () => true,
                (enabled) => enabled,
                (enabled) => enabled,
                (propertyName, enabled) => enabled))
            {
                form.SetRadialDialExpandedForSample(true);

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

        // The launcher task node and the task flyout read live Codex sessions, which a sample run
        // must not depend on. Publish a fixture snapshot for both, then restore the real provider.
        Func<CodexTaskMonitorSnapshot> savedProvider = CodexTaskPresentation.SnapshotProvider;
        try
        {
            DateTime sampleNow = DateTime.Now;
            CodexTaskPresentation.SnapshotProvider = delegate
            {
                return CodexTaskPresentation.CreateFixtureSnapshot(sampleNow);
            };
            RenderLauncherTrioSample(outputDir);
            RenderCodexTaskBoardSample(outputDir);
            RenderEdgeDockTabSample(outputDir);
            CodexIqBoardForm.RenderSample(outputDir);
            ResetSpeedBoardForm.RenderSample(outputDir);
        }
        finally
        {
            CodexTaskPresentation.SnapshotProvider = savedProvider;
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
            () => true,
            (enabled) => enabled,
            (enabled) => enabled,
            (propertyName, enabled) => enabled))
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
