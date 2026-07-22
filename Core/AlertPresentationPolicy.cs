using System;

internal enum AlertPresentationCategory
{
    Quota,
    ResetProtection,
    ServiceHealth,
    CodexTask
}

internal static class AlertPresentationPolicy
{
    public static bool ShouldPresent(
        WidgetSettings settings,
        AlertPresentationCategory category,
        DateTime localNow)
    {
        if (settings == null || !NightScheduleController.ShouldPresentAlerts(settings, localNow))
        {
            return false;
        }

        switch (category)
        {
            case AlertPresentationCategory.Quota:
                return settings.AlertQuotaEnabled;
            case AlertPresentationCategory.ResetProtection:
                return settings.AlertResetProtectionEnabled;
            case AlertPresentationCategory.ServiceHealth:
                return settings.AlertServiceHealthEnabled;
            case AlertPresentationCategory.CodexTask:
                return settings.AlertCodexTaskEnabled;
            default:
                return false;
        }
    }

    public static bool ShouldPresent(WidgetSettings settings, AlertPresentationCategory category)
    {
        return ShouldPresent(settings, category, DateTime.Now);
    }

    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        DateTime daytime = new DateTime(2026, 7, 17, 12, 0, 0);
        foreach (AlertPresentationCategory category in Enum.GetValues(typeof(AlertPresentationCategory)))
        {
            Assert(ShouldPresent(settings, category, daytime), category + " should default on");
        }

        settings.AlertQuotaEnabled = false;
        Assert(!ShouldPresent(settings, AlertPresentationCategory.Quota, daytime), "quota category off");
        Assert(ShouldPresent(settings, AlertPresentationCategory.ServiceHealth, daytime), "category switches must stay independent");

        settings.AlertQuotaEnabled = true;
        settings.NightScheduleEnabled = true;
        settings.NightScheduleStartMinutes = 23 * 60;
        settings.NightScheduleEndMinutes = 7 * 60;
        settings.NightQuietHoursEnabled = true;
        Assert(!ShouldPresent(settings, AlertPresentationCategory.Quota, new DateTime(2026, 7, 17, 23, 30, 0)), "quiet hours should suppress enabled categories");
        Assert(ShouldPresent(settings, AlertPresentationCategory.Quota, daytime), "daytime should restore enabled categories without replay state");

        Console.WriteLine("Alert presentation policy: PASS four categories + quiet-hours AND");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
