using System.Text.Json;
using MenuWidget.Core;

namespace MenuWidget.Services;

public record UserSettings
{
    public int SchemaVersion { get; init; } = 1;
    public DateOnly? FirstWeekMonday { get; init; }
    public bool ReminderEnabled { get; init; }
    public TimeOnly ReminderTime { get; init; } = new(18, 0);
    public bool Topmost { get; init; }
    public bool AutoStart { get; init; }
    // Widget look: "system" / "light" / "dark"; corner "top-right" … or "custom" for a dragged position.
    public string WidgetTheme { get; init; } = "system";
    public int WidgetOpacity { get; init; } = 100;
    public string WidgetCorner { get; init; } = "top-right";
    public bool WidgetLocked { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Меню для сайта");
}

public record WindowPlacement(double Left, double Top, double Width, double Height);
public record WidgetState(double Left, double Top, bool Hidden);

public sealed class LocalData
{
    public string Root { get; }
    public LocalData(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MenuWidget"));
    // Path of the damaged settings file set aside by the last ReadSettings, so the UI can tell the user.
    public string? RecoveredSettingsBackup { get; private set; }
    public UserSettings ReadSettings()
    {
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new();
        UserSettings? result;
        try { result = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)); }
        catch (JsonException) { result = null; }
        if (result is null)
        {
            // A truncated file (power loss, disk error) must not lock the user out: keep a copy and start with defaults.
            var backup = Path.Combine(Root, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
            File.Move(path, backup);
            RecoveredSettingsBackup = backup;
            return new();
        }
        if (result.SchemaVersion != 1) throw new InvalidDataException("Эта версия программы не поддерживает сохранённые настройки.");
        return result;
    }
    public void SaveSettings(UserSettings settings) => AtomicJson(Path.Combine(Root, "settings.json"), settings);
    public ReminderState ReadReminderState() => ReadOptional<ReminderState>("reminder-state.json") ?? new(null, null);
    public void SaveReminderState(ReminderState state) => AtomicJson(Path.Combine(Root, "reminder-state.json"), state);
    public WindowPlacement? ReadPlacement() => ReadOptional<WindowPlacement>("window.json");
    public void SavePlacement(WindowPlacement placement) => AtomicJson(Path.Combine(Root, "window.json"), placement);
    public WidgetState? ReadWidget() => ReadOptional<WidgetState>("widget.json");
    public void SaveWidget(WidgetState state) => AtomicJson(Path.Combine(Root, "widget.json"), state);
    private T? ReadOptional<T>(string file) where T : class
    {
        var path = Path.Combine(Root, file);
        if (!File.Exists(path)) return null;
        // Optional state (window position, reminder marks) is safe to forget when damaged.
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    internal static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class TemplateStore(LocalData data, XlsxDateEditor editor) : ITemplateStore
{
    private record Manifest(string Generation, string[] OriginalNames, DateCell[] DateCells);
    public const int KeptGenerations = 2;

    public Task<MenuSet?> ReadAsync(MenuSetId id, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = SetRoot(id);
        var pointer = Path.Combine(root, "active.json");
        if (!File.Exists(pointer)) return null;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(pointer)) ?? throw new InvalidDataException("Не удалось прочитать сохранённый комплект.");
        if (!Guid.TryParseExact(manifest.Generation, "N", out _) || manifest.OriginalNames.Length != 5 || manifest.DateCells.Length != 5)
            throw new InvalidDataException("Сохранённый комплект повреждён. Добавьте пять файлов заново.");
        return new MenuSet(id, Enumerable.Range(0, 5).Select(i => new MenuTemplate((DayOfWeek)(i + 1), Path.Combine(root, manifest.Generation, $"{i + 1}.xlsx"), manifest.OriginalNames[i], manifest.DateCells[i])).ToArray());
    }, cancellationToken);

    public Task ReplaceAsync(MenuSet set, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (set.Templates.Count != 5 || set.Templates.Select(t => t.Day).Distinct().Count() != 5 || set.Templates.Any(t => t.Day < DayOfWeek.Monday || t.Day > DayOfWeek.Friday)
            || set.Templates.Select(t => Path.GetFullPath(t.LocalPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
            throw new InvalidDataException("Комплект должен содержать пять меню: с понедельника по пятницу.");
        var ordered = set.Templates.OrderBy(t => t.Day).ToArray();
        var root = SetRoot(set.Id);
        var generation = Guid.NewGuid().ToString("N");
        var newRoot = Path.Combine(root, generation);
        Directory.CreateDirectory(newRoot);
        var committed = false;
        try
        {
            foreach (var (template, index) in ordered.Select((t, i) => (t, i)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var copy = Path.Combine(newRoot, $"{index + 1}.xlsx");
                using (var source = new FileStream(template.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destination = new FileStream(copy, FileMode.CreateNew))
                {
                    if (source.Length > 64 * 1024 * 1024) throw new InvalidDataException("Размер файла превышает 64 МБ.");
                    source.CopyTo(destination);
                }
                _ = editor.ReadDate(copy, template.DateCell);
            }
            cancellationToken.ThrowIfCancellationRequested();
            LocalData.AtomicJson(Path.Combine(root, "active.json"), new Manifest(generation, ordered.Select(t => Path.GetFileName(t.OriginalName)).ToArray(), ordered.Select(t => t.DateCell).ToArray()));
            committed = true;
        }
        finally { if (!committed && Directory.Exists(newRoot)) Directory.Delete(newRoot, recursive: true); }
        // Keep the two previous generations for recovery; they are never exported over or edited. Older ones only take disk space.
        foreach (var old in new DirectoryInfo(root).EnumerateDirectories().Where(d => d.Name != generation && Guid.TryParseExact(d.Name, "N", out _))
            .OrderByDescending(d => d.CreationTimeUtc).Skip(KeptGenerations))
        {
            try { old.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // A file may be open in Excel; retry next time.
        }
    }, cancellationToken);

    private string SetRoot(MenuSetId id)
    {
        if (!Enum.IsDefined(id)) throw new ArgumentOutOfRangeException(nameof(id));
        return Path.Combine(data.Root, "templates", id == MenuSetId.First ? "1-5" : "6-10");
    }
}

public sealed class ExportService(ITemplateStore templates, XlsxDateEditor editor, ICycleService cycle) : IExportService
{
    // Newest complete export for the week: "yyyy-MM-dd" or a repeat "yyyy-MM-dd_HHmmss_xxxxxx" with all five files.
    public static ExportResult? FindLatest(string outputDirectory, DateOnly monday, IReadOnlyList<ExportRow> rows)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) return null;
        var output = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(output)) return null;
        var name = monday.ToString("yyyy-MM-dd");
        return new DirectoryInfo(output).EnumerateDirectories(name + "*")
            .Where(d => d.Name == name || d.Name.StartsWith(name + "_", StringComparison.Ordinal))
            .OrderByDescending(d => d.CreationTimeUtc).ThenByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => new ExportResult(d.FullName, rows.Select(r => Path.Combine(d.FullName, r.FileName)).ToArray()))
            .FirstOrDefault(r => r.Files.All(File.Exists));
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        var rows = cycle.Preview(request.Monday, request.SetId);
        var set = await templates.ReadAsync(request.SetId, cancellationToken) ?? throw new InvalidOperationException("Сначала сохраните пять шаблонов выбранного комплекта.");
        var output = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(output);
        var staging = Path.Combine(output, ".подготовка-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var row in rows)
            {
                var template = set.Templates.Single(t => t.Day == row.Day);
                await editor.WriteCopyAsync(template.LocalPath, Path.Combine(staging, row.FileName), template.DateCell, row.Date, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(output, request.Monday.ToString("yyyy-MM-dd"));
            if (Directory.Exists(destination) || File.Exists(destination)) destination += "_" + DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
            Directory.Move(staging, destination);
            return new(destination, rows.Select(r => Path.Combine(destination, r.FileName)).ToArray());
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
