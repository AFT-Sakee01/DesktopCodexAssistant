using System;
using System.Drawing;

// Render-harness output for the left-edge dock tabs. It survived the Codex Task board retirement
// because the tabs are a topology concern, not a task-board concern: the merged Work Board took the
// CodexTask role's place, so this file renders the seven roles that actually exist.
internal sealed partial class OperationForm
{
    // All seven dock tabs at 8x zoom so the 5x30 trapezoid and centre arrow are reviewable.
    private static void RenderEdgeDockTabSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        Color[] accents =
        {
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Network),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SpecBoard),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Guard),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.CodexIq),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.ResetSpeed),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.SystemDay),
            EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions)
        };
        int[] salts =
        {
            BurnInProtection.NetworkMonitorDockTabSalt,
            BurnInProtection.SpecBoardDockTabSalt,
            BurnInProtection.GuardBoardDockTabSalt,
            BurnInProtection.CodexIqBoardDockTabSalt,
            BurnInProtection.ResetSpeedBoardDockTabSalt,
            BurnInProtection.SystemDayBoardDockTabSalt,
            BurnInProtection.CaptionsBoardDockTabSalt
        };
        string[] names = { "network", "workbench", "guard", "codex-iq", "reset-speed", "system-day", "captions" };
        EdgeDockTabRole[] roles =
        {
            EdgeDockTabRole.Network,
            EdgeDockTabRole.SpecBoard,
            EdgeDockTabRole.Guard,
            EdgeDockTabRole.CodexIq,
            EdgeDockTabRole.ResetSpeed,
            EdgeDockTabRole.SystemDay,
            EdgeDockTabRole.Captions
        };
        for (int i = 0; i < accents.Length; i++)
        {
            using (EdgeDockTabForm tab = new EdgeDockTabForm(settings, accents[i], salts[i], "SampleDockTab", roles[i]))
            {
                string path = System.IO.Path.Combine(outputDir, "operation-dock-tab-" + names[i] + ".png");
                tab.SaveSample(path, 8.0f);
                Console.WriteLine("EdgeDockTab(" + names[i] + ") -> " + path);
            }
        }
    }
}
