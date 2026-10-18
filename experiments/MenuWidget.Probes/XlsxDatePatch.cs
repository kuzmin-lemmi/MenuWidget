using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using MenuWidget.Core;

namespace MenuWidget.Probes;

// Stage 1 feasibility probe, NOT wired to the prototype or production export.
internal static class XlsxDatePatch
{
    internal static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int Limit = 8 * 1024 * 1024;

    public static void WriteCopy(string source, string destination, DateCell target, DateOnly date)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Исходник нельзя перезаписывать.");
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 2000 || archive.Entries.GroupBy(e => e.FullName).Any(g => g.Count() != 1))
            throw new InvalidDataException("Некорректный архив.");
        var book = ReadXml(archive, "xl/workbook.xml");
        var relationId = (string?)book.Descendants(Main + "sheet").SingleOrDefault(s => (string?)s.Attribute("name") == target.SheetName)
            ?.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))
            ?? throw new InvalidDataException("Лист не найден.");
        var relationships = ReadXml(archive, "xl/_rels/workbook.xml.rels");
        var relationship = relationships.Root!.Elements(Rel + "Relationship").Single(e => (string?)e.Attribute("Id") == relationId);
        if ((string?)relationship.Attribute("TargetMode") == "External") throw new InvalidDataException("Внешний лист не поддерживается.");
        var uri = new Uri(new Uri("https://local/xl/workbook.xml"), (string)relationship.Attribute("Target")!);
        if (uri.Host != "local" || !uri.AbsolutePath.StartsWith("/xl/", StringComparison.Ordinal)) throw new InvalidDataException("Некорректный путь листа.");
        var sheetPath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var sheet = ReadXml(archive, sheetPath);
        if (sheet.Descendants(Main + "sheetProtection").Any()) throw new InvalidDataException("Лист защищён.");
        var cell = sheet.Descendants(Main + "c").SingleOrDefault(c => (string?)c.Attribute("r") == target.Address)
            ?? throw new InvalidDataException("Ячейка не найдена.");
        if (cell.Element(Main + "f") is not null) throw new InvalidDataException("В ячейке формула.");
        var date1904 = (string?)book.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var serial = date1904 ? date.DayNumber - new DateOnly(1904, 1, 1).DayNumber : date.ToDateTime(TimeOnly.MinValue).ToOADate();
        if (serial < 1 || (!date1904 && date < new DateOnly(1900, 3, 1))) throw new InvalidDataException("Дата вне поддержанного диапазона прототипа.");

        var styles = ReadXml(archive, "xl/styles.xml");
        var xfs = styles.Root!.Element(Main + "cellXfs") ?? throw new InvalidDataException("Нет стилей.");
        var oldIndex = (int?)cell.Attribute("s") ?? 0;
        var existingStyles = xfs.Elements(Main + "xf").ToArray();
        if (oldIndex < 0 || oldIndex >= existingStyles.Length) throw new InvalidDataException("Некорректный стиль.");
        var formats = styles.Root.Element(Main + "numFmts");
        if (formats is null) { formats = new XElement(Main + "numFmts"); styles.Root.AddFirst(formats); }
        var formatId = Math.Max(164, formats.Elements(Main + "numFmt").Select(f => (int)f.Attribute("numFmtId")! + 1).DefaultIfEmpty(164).Max());
        formats.Add(new XElement(Main + "numFmt", new XAttribute("numFmtId", formatId), new XAttribute("formatCode", "dd.mm.yyyy")));
        formats.SetAttributeValue("count", formats.Elements().Count());
        var clonedStyle = new XElement(existingStyles[oldIndex]);
        clonedStyle.SetAttributeValue("numFmtId", formatId); clonedStyle.SetAttributeValue("applyNumberFormat", 1);
        cell.SetAttributeValue("s", existingStyles.Length); cell.SetAttributeValue("t", "n");
        xfs.Add(clonedStyle); xfs.SetAttributeValue("count", existingStyles.Length + 1);
        cell.Element(Main + "is")?.Remove();
        cell.SetElementValue(Main + "v", serial.ToString(CultureInfo.InvariantCulture));

        // Validate everything before creating output. If output fails, remove only OUR new file.
        var created = false;
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew);
            created = true;
            using var result = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var entry in archive.Entries)
            {
                var copy = result.CreateEntry(entry.FullName); copy.LastWriteTime = entry.LastWriteTime;
                using var stream = copy.Open();
                if (entry.FullName == sheetPath) sheet.Save(stream, SaveOptions.DisableFormatting);
                else if (entry.FullName == "xl/styles.xml") styles.Save(stream, SaveOptions.DisableFormatting);
                else { using var input = entry.Open(); input.CopyTo(stream); }
            }
        }
        catch { if (created) File.Delete(destination); throw; }
    }

    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException($"Нет части {path}.");
        if (entry.Length > Limit) throw new InvalidDataException("Превышен лимит XML.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = Limit });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}
