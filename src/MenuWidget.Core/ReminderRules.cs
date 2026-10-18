namespace MenuWidget.Core;

public static class ReminderRules
{
    public static DateOnly LastDueSunday(DateTime localNow, TimeOnly time)
    {
        var sunday = DateOnly.FromDateTime(localNow).AddDays(-(int)localNow.DayOfWeek);
        return localNow < sunday.ToDateTime(time) ? sunday.AddDays(-7) : sunday;
    }

    public static bool IsDue(DateTimeOffset now, ReminderSettings settings, ReminderState state)
    {
        if (!settings.Enabled) return false;
        if (state.SnoozedUntil is { } snooze) return now >= snooze;
        var sunday = LastDueSunday(now.DateTime, settings.SundayTime);
        return state.LastSunday is null || state.LastSunday.Value < sunday;
    }

    public static ReminderState Acknowledge(DateTimeOffset now, ReminderSettings settings, ReminderState state)
    {
        var sunday = LastDueSunday(now.DateTime, settings.SundayTime);
        if (state.LastSunday is { } previous && previous > sunday) sunday = previous;
        return new(sunday, null);
    }

    public static ReminderState Start(DateTimeOffset now, ReminderSettings settings)
    {
        var sunday = LastDueSunday(now.DateTime, settings.SundayTime);
        // Enabling on weekdays does not replay the previous weekend. A Sunday already due may notify now.
        return new(now.DayOfWeek == DayOfWeek.Sunday && sunday == DateOnly.FromDateTime(now.DateTime) ? sunday.AddDays(-7) : sunday, null);
    }
}
