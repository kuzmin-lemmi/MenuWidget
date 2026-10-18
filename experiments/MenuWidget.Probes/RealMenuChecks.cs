using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Probes;

internal static class RealMenuChecks
{
    // Opt-in local acceptance only: never read private workbooks in CI.
    public static async Task RunAsync(string sourceDirectory, string outputDirectory, DateOnly monday)
    {
        var files = Directory.GetFiles(sourceDirectory, "*.xlsx").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != 5) throw new ArgumentException("Select a folder containing exactly five XLSX files.");
        if (Directory.Exists(outputDirectory)) throw new IOException("Use a new acceptance output folder.");
        Directory.CreateDirectory(outputDirectory);
        var hashes = files.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
        var data = new LocalData(Path.Combine(outputDirectory, "profile")); var editor = new XlsxDateEditor();
        var store = new TemplateStore(data, editor);
        var cells = files.Select(p => editor.Inspect(p).DefaultCell).ToArray();
        var oldDates = files.Select((p, i) => editor.ReadDate(p, cells[i])).ToArray();
        await store.ReplaceAsync(new(MenuSetId.Second, files.Select((p, i) => new MenuTemplate((DayOfWeek)(i + 1), p, Path.GetFileName(p), cells[i])).ToArray()), default);
        var result = await new ExportService(store, editor, new CycleService()).ExportAsync(new(monday, MenuSetId.Second, Path.Combine(outputDirectory, "exports")), default);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        for (var i = 0; i < files.Length; i++)
        {
            if (!hashes[i].SequenceEqual(SHA256.HashData(File.ReadAllBytes(files[i])))) throw new Exception("Source changed.");
            if (editor.ReadDate(result.Files[i], cells[i]) != monday.AddDays(i) || Path.GetFileName(result.Files[i]) != $"{monday.AddDays(i):yyyy-MM-dd}-sm.xlsx") throw new Exception("Wrong result date/name.");
            using var source = ZipFile.OpenRead(files[i]); using var output = ZipFile.OpenRead(result.Files[i]);
            if (!source.Entries.Select(e => e.FullName).SequenceEqual(output.Entries.Select(e => e.FullName))) throw new Exception("Workbook parts changed.");
            foreach (var entry in source.Entries)
            {
                byte[] Read(ZipArchiveEntry e) { using var stream = e.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray(); }
                var before = Read(entry); var after = Read(output.GetEntry(entry.FullName)!);
                if (before.SequenceEqual(after)) continue;
                if (entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
                {
                    var a = XDocument.Load(new MemoryStream(before)); var b = XDocument.Load(new MemoryStream(after));
                    a.Descendants(main + "c").Single(c => (string?)c.Attribute("r") == cells[i].Address).Remove();
                    b.Descendants(main + "c").Single(c => (string?)c.Attribute("r") == cells[i].Address).Remove();
                    if (!XNode.DeepEquals(a, b)) throw new Exception("Content outside date cell changed.");
                }
                else if (entry.FullName == "xl/styles.xml")
                {
                    var a = XDocument.Load(new MemoryStream(before)); var b = XDocument.Load(new MemoryStream(after));
                    foreach (var section in new[] { "numFmts", "cellXfs" })
                    {
                        var oldSection = a.Root!.Element(main + section); var newSection = b.Root!.Element(main + section);
                        if (oldSection is null) { newSection?.Remove(); continue; }
                        if (newSection is null) throw new Exception("Style section lost.");
                        newSection.Elements().Skip(oldSection.Elements().Count()).Remove(); newSection.SetAttributeValue("count", oldSection.Attribute("count")?.Value);
                    }
                    if (!XNode.DeepEquals(a, b)) throw new Exception("Existing styles changed.");
                }
                else throw new Exception("Unrelated workbook part changed: " + entry.FullName);
            }
            Console.WriteLine($"PASS real-menu/{i + 1}: date, name, source hash, content, formulas, existing styles and print settings");
        }
        File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new { passed = 5, sourceDates = oldDates, dates = Enumerable.Range(0, 5).Select(monday.AddDays), result.Directory }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("RESULT: all five real menus passed; no originals changed.");
    }
}
