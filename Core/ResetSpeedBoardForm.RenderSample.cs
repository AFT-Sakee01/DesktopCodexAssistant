using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

internal sealed partial class ResetSpeedBoardForm
{
    internal static void RenderSample(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        ResetSpeedBoardSnapshot fixture = CreateFixtureSnapshot();
        using (ResetSpeedBoardForm form = new ResetSpeedBoardForm(null, settings, delegate { return fixture; }))
        {
            form.Size = form.GetDesiredSize();
            string path = Path.Combine(outputDir, "reset-speed-board.png");
            RenderSampleSupport.SaveComposited(outputDir, Path.GetFileName(path), form.Width, form.Height, 255, form.DrawWindowContent);
            Console.WriteLine("Reset / Speed board -> " + path + " (" + form.Width + "x" + form.Height + ")");
        }
    }

    private static ResetSpeedBoardSnapshot CreateFixtureSnapshot()
    {
        ResetSpeedBoardSnapshot snapshot = ResetSpeedBoardSnapshot.CreateEmpty();
        DateTime now = new DateTime(2026, 7, 22, 23, 40, 0, DateTimeKind.Local);
        int[] remaining = { 100, 84, 72, 91, 68, 47, 33 };
        snapshot.QuotaKnown = true;
        snapshot.FiveHourRemainingPercent = 62;
        snapshot.FiveHourResetKnown = true;
        snapshot.FiveHourResetLocal = now.AddHours(3.0);
        snapshot.WeeklyRemainingPercent = 33;
        snapshot.WeeklyResetKnown = true;
        snapshot.WeeklyResetLocal = now.AddDays(2.0);
        snapshot.UpdatedKnown = true;
        snapshot.UpdatedLocal = now;
        for (int i = 0; i < remaining.Length; i++)
        {
            snapshot.QuotaHistory.Add(new ResetSpeedQuotaPoint
            {
                DateLocal = now.Date.AddDays(i - 6),
                Known = true,
                WeeklyRemainingPercent = remaining[i]
            });
        }
        snapshot.ResetEvents.Add(new ResetSpeedResetEvent { TimestampLocal = now.AddDays(-3.0).AddHours(-2), Kind = ResetSpeedResetKind.Credit, WeeklyRemainingPercent = 91 });
        snapshot.ResetEvents.Add(new ResetSpeedResetEvent { TimestampLocal = now.AddDays(-5.0), Kind = ResetSpeedResetKind.Natural, WeeklyRemainingPercent = 100 });
        snapshot.SpeedWindowKnown = true;
        snapshot.SpeedWindowOpen = true;
        snapshot.SpeedWindowOpenedAtKnown = true;
        snapshot.SpeedWindowOpenedAtLocal = now.AddHours(-4.0);
        snapshot.SpeedWindowClosedAtKnown = true;
        snapshot.SpeedWindowClosedAtLocal = now.AddHours(36.0);
        snapshot.SpeedWindowRemainingMinutes = 36 * 60;
        snapshot.SpeedWindowRemainingRatio = 0.36f;
        snapshot.ResetCreditsKnown = true;
        snapshot.ResetCreditCount = 3;
        snapshot.ResetCreditExpirationKnown = true;
        snapshot.ResetCreditExpirationLocal = now.AddHours(17.0);
        return snapshot;
    }

    internal static void RunSelfTest()
    {
        ResetSpeedBoardSnapshot fixture = CreateFixtureSnapshot();
        ResetSpeedBoardSnapshot clone = fixture.Clone();
        if (clone.QuotaHistory.Count != 7 || clone.ResetEvents.Count != 2 ||
            clone.ResetCreditCount != 3 || !clone.SpeedWindowOpen ||
            ComputeSevenDayUsedPercent(clone.QuotaHistory) != 86)
        {
            throw new InvalidOperationException("Reset / Speed board snapshot or seven-day usage self-test failed.");
        }
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        using (ResetSpeedBoardForm form = new ResetSpeedBoardForm(null, settings, delegate { return fixture; }))
        {
            form.Size = form.GetDesiredSize();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                form.DrawWindowContent(g);
                Color corner = bitmap.GetPixel(Math.Min(bitmap.Width - 1, form.S(3)), Math.Min(bitmap.Height - 1, form.S(3)));
                Color center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                if (corner.A == 0 || center.A == 0)
                {
                    throw new InvalidOperationException("Reset / Speed board renderer produced transparent output.");
                }
            }
        }
        Console.WriteLine("Reset / Speed board: PASS snapshot, seven-day trace, reset gates, speed dial and reset cards");
    }
}
