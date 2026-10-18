namespace MenuWidget.Core;

public sealed class CycleService : ICycleService
{
    public DateOnly SuggestedMonday(DateOnly today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? monday.AddDays(7) : monday;
    }

    public MenuSetId GetSet(DateOnly date, CycleSettings settings)
    {
        RequireMonday(settings.FirstWeekMonday);
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var weeks = (monday.DayNumber - settings.FirstWeekMonday.DayNumber) / 7;
        return ((weeks % 2) + 2) % 2 == 0 ? MenuSetId.First : MenuSetId.Second;
    }

    public IReadOnlyList<ExportRow> Preview(DateOnly monday, MenuSetId set)
    {
        RequireMonday(monday);
        if (!Enum.IsDefined(set)) throw new ArgumentOutOfRangeException(nameof(set));
        return Enumerable.Range(0, 5).Select(i =>
        {
            var date = monday.AddDays(i);
            return new ExportRow(date.DayOfWeek, 1 + i + (set == MenuSetId.Second ? 5 : 0), date, $"{date:yyyy-MM-dd}-sm.xlsx");
        }).ToArray();
    }

    private static void RequireMonday(DateOnly date)
    {
        if (date.DayOfWeek != DayOfWeek.Monday) throw new ArgumentException("Выберите понедельник.");
    }
}
