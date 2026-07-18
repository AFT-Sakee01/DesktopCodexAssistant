using System;
using System.Drawing;
using System.Drawing.Imaging;

internal static class NightScheduleController
{
    public static bool IsInNightWindow(WidgetSettings settings, DateTime localNow)
    {
        if (settings == null || !settings.NightScheduleEnabled)
        {
            return false;
        }

        int start = Math.Max(WidgetSettings.MinNightScheduleMinutes, Math.Min(WidgetSettings.MaxNightScheduleMinutes, settings.NightScheduleStartMinutes));
        int end = Math.Max(WidgetSettings.MinNightScheduleMinutes, Math.Min(WidgetSettings.MaxNightScheduleMinutes, settings.NightScheduleEndMinutes));
        int minute = localNow.Hour * 60 + localNow.Minute;

        // Equal boundaries intentionally mean a full-day schedule. This gives the user a stable
        // way to request permanent dimming without inventing another mode or special sentinel.
        if (start == end)
        {
            return true;
        }

        return start < end
            ? minute >= start && minute < end
            : minute >= start || minute < end;
    }

    public static int GetActiveLuminancePercent(WidgetSettings settings, DateTime localNow)
    {
        if (!IsInNightWindow(settings, localNow))
        {
            return 100;
        }

        return Math.Max(
            WidgetSettings.MinNightDimLuminancePercent,
            Math.Min(WidgetSettings.MaxNightDimLuminancePercent, settings.NightDimLuminancePercent));
    }

    public static bool IsQuietHoursActive(WidgetSettings settings, DateTime localNow)
    {
        return settings != null && settings.NightQuietHoursEnabled && IsInNightWindow(settings, localNow);
    }

    public static bool ShouldPresentAlerts(WidgetSettings settings, DateTime localNow)
    {
        return !IsQuietHoursActive(settings, localNow);
    }

    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.NightScheduleEnabled = true;
        settings.NightScheduleStartMinutes = 23 * 60;
        settings.NightScheduleEndMinutes = 7 * 60;
        settings.NightDimLuminancePercent = 60;
        settings.NightQuietHoursEnabled = true;

        Assert(IsInNightWindow(settings, new DateTime(2026, 7, 17, 6, 59, 0)), "06:59 should be inside the cross-midnight window");
        Assert(!IsInNightWindow(settings, new DateTime(2026, 7, 17, 7, 1, 0)), "07:01 should be outside the cross-midnight window");
        Assert(IsInNightWindow(settings, new DateTime(2026, 7, 17, 23, 0, 0)), "23:00 should include the start boundary");
        Assert(!IsInNightWindow(settings, new DateTime(2026, 7, 17, 22, 59, 0)), "22:59 should be outside the cross-midnight window");
        Assert(GetActiveLuminancePercent(settings, new DateTime(2026, 7, 17, 23, 30, 0)) == 60, "night luminance should use the configured percentage");
        Assert(IsQuietHoursActive(settings, new DateTime(2026, 7, 17, 23, 30, 0)), "quiet hours should follow the night window");

        settings.NightScheduleStartMinutes = 300;
        settings.NightScheduleEndMinutes = 300;
        Assert(IsInNightWindow(settings, new DateTime(2026, 7, 17, 12, 0, 0)), "equal schedule boundaries should mean full day");
        settings.NightScheduleEnabled = false;
        Assert(!IsInNightWindow(settings, new DateTime(2026, 7, 17, 12, 0, 0)), "disabled schedule should never activate");

        using (Bitmap bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        {
            bitmap.SetPixel(0, 0, Color.FromArgb(200, 100, 150, 200));
            BurnInProtection.ApplyLuminance(bitmap, 50);
            Color dimmed = bitmap.GetPixel(0, 0);
            Assert(dimmed.A == 200, "luminance transform must preserve alpha");
            Assert(dimmed.R >= 48 && dimmed.R <= 52 && dimmed.G >= 73 && dimmed.G <= 77 && dimmed.B >= 98 && dimmed.B <= 102, "luminance transform should scale RGB channels");
        }

        Console.WriteLine("Night schedule: PASS cross-midnight 06:59/07:01 quiet-hours luminance");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
