using System;

internal static class TimeZoneUtilities
{
    public const string BeijingTimeZoneId = "China Standard Time";

    private static readonly TimeZoneInfo BeijingTimeZone = ResolveBeijingTimeZone();

    public static TimeZoneInfo GetBeijingTimeZone()
    {
        return BeijingTimeZone;
    }

    public static DateTime GetBeijingNow()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BeijingTimeZone);
    }

    public static DateTime GetBeijingDate(DateTime instant)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(ToUtc(instant), BeijingTimeZone).Date;
    }

    public static DateTime GetNextBeijingMidnightUtc(DateTime utcNow)
    {
        DateTime beijingNow = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(utcNow), BeijingTimeZone);
        DateTime nextMidnight = DateTime.SpecifyKind(beijingNow.Date.AddDays(1.0), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(nextMidnight, BeijingTimeZone);
    }

    public static DateTime GetNextBeijingHourUtc(DateTime utcNow)
    {
        DateTime beijingNow = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(utcNow), BeijingTimeZone);
        DateTime nextHour = new DateTime(
            beijingNow.Year,
            beijingNow.Month,
            beijingNow.Day,
            beijingNow.Hour,
            0,
            0).AddHours(1.0);
        nextHour = DateTime.SpecifyKind(nextHour, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(nextHour, BeijingTimeZone);
    }

    public static DateTime GetCurrentBeijingHalfDayStart(DateTime utcNow)
    {
        DateTime beijingNow = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(utcNow), BeijingTimeZone);
        int hour = beijingNow.Hour >= 12 ? 12 : 0;
        return beijingNow.Date.AddHours(hour);
    }

    public static TimeZoneInfo ResolveDisplayTimeZone(WidgetSettings settings)
    {
        if (settings == null || settings.DisplayTimeZoneMode == DisplayTimeZoneMode.Automatic)
        {
            return TimeZoneInfo.Local;
        }

        return ResolveTimeZone(settings.DisplayTimeZoneId, TimeZoneInfo.Local);
    }

    public static TimeZoneInfo ResolveTimeZone(string id, TimeZoneInfo fallback)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch
            {
            }
        }

        return fallback ?? TimeZoneInfo.Local;
    }

    public static DateTime ConvertToDisplayTime(DateTime instant, WidgetSettings settings)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(ToUtc(instant), ResolveDisplayTimeZone(settings));
    }

    public static DateTime ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        if (value.Kind == DateTimeKind.Local)
        {
            return value.ToUniversalTime();
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(BeijingTimeZoneId);
        }
        catch
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                BeijingTimeZoneId,
                TimeSpan.FromHours(8.0),
                "Beijing Time",
                "Beijing Time");
        }
    }
}
