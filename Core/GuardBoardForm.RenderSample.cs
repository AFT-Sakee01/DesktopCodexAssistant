using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// Test-only render harness for --render-guard, mirroring the other windows' harnesses. The guard
// board's whole point is state, so the sample mode paints four different states rather than one
// pretty frame: everything idle, everything armed, an outage part way to the auto-sleep deadline,
// and the compact single-column fallback.
internal sealed partial class GuardBoardForm
{
    internal static void RenderSamples(string outputDir, bool sample, bool current)
    {
        Directory.CreateDirectory(outputDir);
        if (sample)
        {
            DateTime now = DateTime.UtcNow;

            RenderState(outputDir, "guard-idle.png", 648, 400, delegate(GuardBoardForm form)
            {
            });

            // Armed: sleep guard held 2h18m, a 5 hour display timer with 4:32:10 left, and the
            // battery-care pause 8 hours into its 24h window. StartDisplayGuard measures the
            // deadline from the instant it is handed, so it is backdated by the elapsed part.
            RenderState(outputDir, "guard-armed.png", 648, 400, delegate(GuardBoardForm form)
            {
                form.runtime.SetDisplayGuardMinutes(300);
                form.runtime.StartDisplayGuard(now.AddSeconds(-(27 * 60 + 50)));
                form.runtime.BackdateSleepGuardForRenderSample(now.AddHours(-2).AddMinutes(-18));
                form.runtime.NoteBatteryCarePaused(now.AddHours(-8));
                form.CurrentSettings.CodexQuotaPlanEnabled = true;
            });

            // Offline 6 of 10 minutes into the threshold: the bar marker has walked past the danger
            // point and the state text has flipped colour. Two ticks are needed — the first starts
            // the outage clock, the second advances it.
            RenderState(outputDir, "guard-offline.png", 648, 400, delegate(GuardBoardForm form)
            {
                form.runtime.SetSleepGuard(true);
                form.runtime.SetOfflineThresholdMinutes(10);
                form.runtime.Tick(now.AddMinutes(-6));
                form.runtime.BackdateSleepGuardForRenderSample(now.AddMinutes(-49));
                form.CurrentSettings.AiRequestProtectionManualBlockEnabled = true;
            }, delegate { return false; });

            RenderState(outputDir, "guard-compact.png", 320, 400, delegate(GuardBoardForm form)
            {
                form.runtime.SetSleepGuard(true);
            });
        }

        if (current)
        {
            WidgetSettings settings = WidgetSettings.Load();
            using (GuardBoardForm form = new GuardBoardForm(null, settings, delegate { return (bool?)true; }))
            {
                form.SetLayerScale(2.0f);
                form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
                RenderSampleSupport.SaveComposited(outputDir, "guard-current.png", form.Width, form.Height, 255, form.DrawWindowContent);
                Console.WriteLine("Current -> " + Path.Combine(outputDir, "guard-current.png") + " (" + form.Width + "x" + form.Height + ")");
                // The harness arms real guards on a throwaway form; releasing here keeps a render
                // run from leaving ES_SYSTEM_REQUIRED set on the process that invoked it.
                form.runtime.ReleaseAll();
            }
        }
    }

    private static void RenderState(string outputDir, string fileName, int logicalWidth, int logicalHeight, Action<GuardBoardForm> arrange)
    {
        RenderState(outputDir, fileName, logicalWidth, logicalHeight, arrange, delegate { return (bool?)true; });
    }

    private static void RenderState(
        string outputDir,
        string fileName,
        int logicalWidth,
        int logicalHeight,
        Action<GuardBoardForm> arrange,
        Func<bool?> onlineProvider)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = logicalWidth;
        settings.SpecBoardHeight = logicalHeight;
        settings.Normalize();
        using (GuardBoardForm form = new GuardBoardForm(null, settings, onlineProvider))
        {
            arrange(form);
            form.SetLayerScale(2.0f);
            form.Size = new Size(logicalWidth * 2, logicalHeight * 2);
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                form.DrawBoard(g, false);
                string path = Path.Combine(outputDir, fileName);
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine(fileName + " -> " + path + " (" + form.Width + "x" + form.Height + ")");
            }

            form.runtime.ReleaseAll();
        }
    }
}
