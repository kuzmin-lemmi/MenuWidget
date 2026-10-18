using System.Security.Cryptography;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Probes;

internal static class ServicesChecks
{
    public static void Run(string temp, Action<string, Action> check, Action<string> fixture)
    {
        var data = new LocalData(Path.Combine(temp, "profile"));
        var editor = new XlsxDateEditor(); var store = new TemplateStore(data, editor);
        var sources = Enumerable.Range(1, 5).Select(i => Path.Combine(temp, $"menu-{i}.xlsx")).ToArray();
        foreach (var file in sources) fixture(file);
        var set = new MenuSet(MenuSetId.First, sources.Select((p, i) => new MenuTemplate((DayOfWeek)(i + 1), p, Path.GetFileName(p), new("Меню", "J1"))).ToArray());
        check("settings/persist-after-new-instance", () =>
        {
            data.SaveSettings(new() { FirstWeekMonday = new(2026, 10, 12), ReminderTime = new(19, 45), Topmost = true });
            var read = new LocalData(data.Root).ReadSettings();
            Require(read.FirstWeekMonday == new DateOnly(2026, 10, 12) && read.ReminderTime == new TimeOnly(19, 45) && read.Topmost);
        });
        check("settings/corrupt-file-recovers", () =>
        {
            var root = Path.Combine(temp, "corrupt-profile"); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"SchemaVersion\": 1, \"FirstWeek");
            File.WriteAllText(Path.Combine(root, "window.json"), "not json");
            var local = new LocalData(root); var read = local.ReadSettings();
            Require(read.FirstWeekMonday is null && local.RecoveredSettingsBackup is { } backup && File.Exists(backup) && !File.Exists(Path.Combine(root, "settings.json")));
            Require(local.ReadPlacement() is null && new LocalData(root).ReadSettings() == new UserSettings());
        });
        check("templates/import-independent-copies", () =>
        {
            store.ReplaceAsync(set, default).GetAwaiter().GetResult();
            var read = new TemplateStore(data, editor).ReadAsync(MenuSetId.First, default).GetAwaiter().GetResult()!;
            Require(read.Templates.Count == 5 && read.Templates.All(t => t.LocalPath.StartsWith(data.Root, StringComparison.Ordinal)));
            Require(editor.ReadDate(read.Templates[0].LocalPath, read.Templates[0].DateCell) == new DateOnly(2026, 10, 5));
        });
        check("templates/invalid-replacement-keeps-old", () =>
        {
            var pointer = Path.Combine(data.Root, "templates", "1-5", "active.json"); var before = File.ReadAllText(pointer);
            var broken = Path.Combine(temp, "broken.xlsx"); File.WriteAllText(broken, "broken zip");
            var changed = set.Templates.ToArray(); changed[4] = changed[4] with { LocalPath = broken };
            Reject(() => store.ReplaceAsync(new(MenuSetId.First, changed), default).GetAwaiter().GetResult());
            Require(before == File.ReadAllText(pointer) && Directory.GetDirectories(Path.GetDirectoryName(pointer)!).Length == 1);
        });
        check("templates/sets-independent", () =>
        {
            store.ReplaceAsync(set with { Id = MenuSetId.Second }, default).GetAwaiter().GetResult();
            Require(store.ReadAsync(MenuSetId.First, default).Result!.Templates[0].LocalPath != store.ReadAsync(MenuSetId.Second, default).Result!.Templates[0].LocalPath);
        });
        var export = new ExportService(store, editor, new CycleService());
        check("export/five-correct-dates-and-names", () =>
        {
            var result = export.ExportAsync(new(new(2026, 10, 19), MenuSetId.First, Path.Combine(temp, "exports")), default).GetAwaiter().GetResult();
            Require(result.Files.Count == 5);
            for (var i = 0; i < 5; i++) Require(Path.GetFileName(result.Files[i]) == $"2026-10-{19 + i}-sm.xlsx" && editor.ReadDate(result.Files[i], new("Меню", "J1")) == new DateOnly(2026, 10, 19 + i));
            Require(editor.ReadDate(sources[0], new("Меню", "J1")) == new DateOnly(2026, 10, 5));
        });
        check("export/repeat-preserves-old-result", () =>
        {
            var old = Path.Combine(temp, "exports", "2026-10-19", "2026-10-19-sm.xlsx"); var hash = SHA256.HashData(File.ReadAllBytes(old));
            var result = export.ExportAsync(new(new(2026, 10, 19), MenuSetId.First, Path.Combine(temp, "exports")), default).GetAwaiter().GetResult();
            Require(result.Directory != Path.GetDirectoryName(old) && hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(old))));
        });
        check("widget/finds-newest-complete-week", () =>
        {
            var output = Path.Combine(temp, "exports"); var rows = new CycleService().Preview(new(2026, 10, 19), MenuSetId.First);
            var newest = Directory.GetDirectories(output, "2026-10-19*").OrderByDescending(Directory.GetCreationTimeUtc).First();
            Require(ExportService.FindLatest(output, new(2026, 10, 19), rows)?.Directory == newest);
            var incomplete = Path.Combine(output, "2026-10-19_235959_ffffff"); Directory.CreateDirectory(incomplete);
            Require(ExportService.FindLatest(output, new(2026, 10, 19), rows)?.Directory == newest);
            Require(ExportService.FindLatest(output, new(2026, 10, 26), rows) is null && ExportService.FindLatest(Path.Combine(temp, "missing"), new(2026, 10, 19), rows) is null);
        });
        check("export/fifth-file-failure-leaves-no-partial-set", () =>
        {
            var stored = store.ReadAsync(MenuSetId.Second, default).Result!; File.WriteAllText(stored.Templates[4].LocalPath, "corrupt fixture");
            var output = Path.Combine(temp, "failed-exports");
            Reject(() => export.ExportAsync(new(new(2026, 10, 19), MenuSetId.Second, output), default).GetAwaiter().GetResult());
            Require(Directory.GetFileSystemEntries(output).Length == 0);
        });
    }
    private static void Require(bool condition) { if (!condition) throw new Exception("Expected user data/result preservation."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid workbook was accepted."); }
}
