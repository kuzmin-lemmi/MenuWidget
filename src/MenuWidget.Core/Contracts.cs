namespace MenuWidget.Core;

public enum MenuSetId { First, Second }
public record CycleSettings(DateOnly FirstWeekMonday);
public record DateCell(string SheetName, string Address);
public record MenuTemplate(DayOfWeek Day, string LocalPath, string OriginalName, DateCell DateCell);
public record MenuSet(MenuSetId Id, IReadOnlyList<MenuTemplate> Templates);
public record ExportRow(DayOfWeek Day, int MenuNumber, DateOnly Date, string FileName);
public record ExportRequest(DateOnly Monday, MenuSetId SetId, string OutputDirectory);
public record WeekInfo(DateOnly Monday, MenuSetId Set, int WeekNumber, int? TodayMenuNumber);
public record ExportResult(string Directory, IReadOnlyList<string> Files);
public record ReminderSettings(bool Enabled, TimeOnly SundayTime, TimeSpan SnoozeDelay);
public record ReminderState(DateOnly? LastSunday, DateTimeOffset? SnoozedUntil);
public record UpdateCandidate(string Version, string Notes, Uri ManifestUri);
public record DownloadProgress(long ReceivedBytes, long? TotalBytes);
public enum UpdateCheckStatus { Available, UpToDate, NoReleases }
public record UpdateCheckResult(UpdateCheckStatus Status, UpdateCandidate? Candidate);

public interface IClock { DateTimeOffset Now { get; } }
public interface ICycleService
{
    DateOnly SuggestedMonday(DateOnly today);
    MenuSetId GetSet(DateOnly date, CycleSettings settings);
    IReadOnlyList<ExportRow> Preview(DateOnly monday, MenuSetId set);
}
public interface ITemplateStore
{
    Task<MenuSet?> ReadAsync(MenuSetId id, CancellationToken cancellationToken);
    Task ReplaceAsync(MenuSet set, CancellationToken cancellationToken);
}
public interface IXlsxDateEditor
{
    // Writes a NEW destination. Never modifies source, even on failure.
    Task WriteCopyAsync(string source, string destination, DateCell cell, DateOnly date, CancellationToken cancellationToken);
}
public interface IExportService
{
    // Returns only after all five files have been validated and committed.
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken);
}
public interface IReminderScheduler
{
    Task ApplyAsync(ReminderSettings settings, CancellationToken cancellationToken);
    Task SnoozeAsync(DateTimeOffset until, CancellationToken cancellationToken);
    Task RemoveAsync(CancellationToken cancellationToken);
}
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
    // Downloads and verifies only; must not start an installer implicitly.
    Task<string> DownloadVerifiedAsync(UpdateCandidate candidate, IProgress<DownloadProgress> progress, CancellationToken cancellationToken);
    // Caller must have obtained explicit user action and finished active work.
    Task LaunchInstallerAsync(string verifiedPath, CancellationToken cancellationToken);
}
