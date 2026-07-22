using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

internal sealed partial class CodexIqBoardForm
{
    internal static void RenderSample(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        settings.Normalize();
        CodexIqBoardSnapshot fixture = CreateFixtureSnapshot();
        using (CodexIqBoardForm form = new CodexIqBoardForm(null, settings, delegate { return fixture; }))
        {
            form.SetLayerScale(2.0f);
            form.Size = new Size(settings.SpecBoardWidth * 2, settings.SpecBoardHeight * 2);
            form.snapshot = fixture.Clone();
            form.visibleSignature = BuildSnapshotSignature(form.snapshot);
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                form.DrawBoard(g);
                string path = Path.Combine(outputDir, "operation-codex-iq-board.png");
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine("Codex IQ board -> " + path + " (" + form.Width + "x" + form.Height + ")");
            }
        }
    }

    private static CodexIqBoardSnapshot CreateFixtureSnapshot()
    {
        CodexIqBoardSnapshot snapshot = CodexIqBoardSnapshot.CreateEmpty();
        snapshot.UpdatedKnown = true;
        snapshot.UpdatedLocal = new DateTime(2026, 7, 21, 12, 49, 0);
        snapshot.SelectedModelKey = "gpt_56_sol_medium";
        snapshot.SelectedModelLabel = "GPT-5.6 Sol medium";
        AddFixtureModel(snapshot, "gpt_56_sol_max", "Sol", "max", 107.6, 9.47, 2138, 112, 1454.6, true);
        AddFixtureModel(snapshot, "gpt_56_sol_xhigh", "Sol", "xhigh", 96.4, 7.08, 1786, 111, 1058.6, false);
        AddFixtureModel(snapshot, "gpt_56_sol_high", "Sol", "high", 95.5, 5.01, 1355, 112, 709.5, false);
        AddFixtureModel(snapshot, "gpt_56_sol_medium", "Sol", "medium", 94.2, 3.47, 1101, 112, 492.8, false);
        AddFixtureModel(snapshot, "gpt_56_sol_low", "Sol", "low", 80.1, 2.07, 752, 111, 259.7, false);
        AddFixtureModel(snapshot, "gpt_56_terra_max", "Terra", "max", 99.5, 4.61, 1944, 112, 1340.7, false);
        AddFixtureModel(snapshot, "gpt_56_terra_high", "Terra", "high", 71.3, 1.34, 765, 112, 339.2, false);
        AddFixtureModel(snapshot, "gpt_56_luna_max", "Luna", "max", 87.4, 2.54, 2316, 112, 1953.1, false);
        AddFixtureModel(snapshot, "gpt_56_luna_high", "Luna", "high", 72.6, 1.09, 1149, 112, 797.7, false);
        AddFixtureModel(snapshot, "gpt_55_high", "Legacy", "high", 84.7, 3.57, 956, 112, 501.3, false);

        double[] quota = { 100, 96, 91, 84, 77, 69 };
        snapshot.WeeklyQuotaRemaining.AddRange(quota);
        for (int i = 0; i < 7; i++)
        {
            snapshot.Trends.Add(new CodexIqBoardTrendPoint
            {
                DateLocal = snapshot.UpdatedLocal.Date.AddDays(i - 6),
                AverageTaskSeconds = 1320 - i * 36,
                TokenEfficiencyPercent = 84 + i * 3,
                TotalTokens = (410 + i * 14) * 1000000.0,
                EfficiencyKnown = true
            });
        }

        for (int i = 0; i < snapshot.Models.Count; i++)
        {
            snapshot.Roster.Add(new CodexIqBoardRosterEntry
            {
                Key = snapshot.Models[i].Key,
                Label = snapshot.Models[i].Label,
                State = CodexIqBoardRosterState.Active
            });
        }

        snapshot.Roster.Add(new CodexIqBoardRosterEntry { Key = "gpt_55_max", Label = "GPT-5.5 max", State = CodexIqBoardRosterState.Intermittent });
        snapshot.Roster.Add(new CodexIqBoardRosterEntry { Key = "gpt_54_high", Label = "GPT-5.4 high", State = CodexIqBoardRosterState.Retired });

        snapshot.Services.Add(new RadarServiceHealthEntry { Label = "Radar", Color = DesignTokens.Colors.Success });
        snapshot.Services.Add(new RadarServiceHealthEntry { Label = "OpenAI", Color = DesignTokens.Colors.Success });
        snapshot.Services.Add(new RadarServiceHealthEntry { Label = "Claude", Color = DesignTokens.Colors.Warning });
        snapshot.Services.Add(new RadarServiceHealthEntry { Label = "DeepSeek", Color = DesignTokens.Colors.Success, Checking = true });

        snapshot.Refresh = new CodexIqBoardRefreshStatus
        {
            Known = true,
            StatusColor = DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 255),
            Warning = false,
            RequestRunning = false,
            MarkerVisible = true,
            MarkerFraction = 0.28f,
            CurrentFraction = 0.62f,
            ArcStartFraction = 0.28f,
            ArcSweepFraction = 0.34f,
            PhaseText = "本轮已刷新",
            DetailText = "7月21日"
        };
        return snapshot;
    }

    private static void AddFixtureModel(
        CodexIqBoardSnapshot snapshot,
        string key,
        string family,
        string effort,
        double iq,
        double cost,
        double seconds,
        double tasks,
        double tokensMillions,
        bool current)
    {
        snapshot.Models.Add(new CodexIqBoardModelPoint
        {
            Key = key,
            Label = "GPT-5.6 " + family + " " + effort,
            Family = family,
            Effort = effort,
            Status = "normal",
            DataKnown = true,
            DataLocal = snapshot.UpdatedLocal,
            Iq = iq,
            AverageCostUsd = cost,
            AverageTaskSeconds = seconds,
            TotalTokens = tokensMillions * 1000000.0,
            Passed = Math.Round(tasks * Math.Min(1.0, iq / 140.0)),
            ValidTasks = tasks,
            Current = current
        });
    }
}
