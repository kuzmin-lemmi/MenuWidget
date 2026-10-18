using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MenuWidget.Core;

namespace MenuWidget.Services;

public record WorkbookInfo(IReadOnlyList<string> Sheets, DateCell DefaultCell);

public sealed class XlsxDateEditor : IXlsxDateEditor
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const long FileLimit = 64 * 1024 * 1024;
    private const long XmlLimit = 8 * 1024 * 1024;

    public WorkbookInfo Inspect(string path)
    {
        using var zip = OpenSnapshot(path);
        var book = ReadXml(zip, "xl/workbook.xml");
        var sheets = book.Descendants(Main + "sheet").Select(s => (string?)s.Attribute("name") ?? "").ToArray();
        if (sheets.Length == 0) throw new InvalidDataException("В книге нет листов.");
        return new(sheets, new(sheets[0], "J1"));
    }

    public DateOnly ReadDate(string path, DateCell target)
    {
        using var zip = OpenSnapshot(path);
        return ReadDate(zip, target);
    }

    public Task WriteCopyAsync(string source, string destination, DateCell cell, DateOnly date, CancellationToken cancellationToken)
        => Task.Run(() => WriteCopy(source, destination, cell, date, cancellationToken), cancellationToken);

    public void WriteCopy(string source, string destination, DateCell target, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Исходный файл нельзя перезаписывать.");
        using var zip = OpenSnapshot(source);
        cancellationToken.ThrowIfCancellationRequested();
        _ = ReadDate(zip, target);
        var book = ReadXml(zip, "xl/workbook.xml");
        var relationships = ReadXml(zip, "xl/_rels/workbook.xml.rels");
        var sheetPath = SheetPath(book, relationships, target.SheetName);
        var sheet = ReadXml(zip, sheetPath);
        if (sheet.Descendants(Main + "sheetProtection").Any()) throw new InvalidDataException("Лист защищён. Выберите шаблон без защиты.");
        var cell = Cell(sheet, target.Address);
        var serial = ToSerial(date, Is1904(book));
        var stylesPath = PartPath(relationships, "styles");
        var styles = ReadXml(zip, stylesPath);
        var xfs = styles.Root!.Element(Main + "cellXfs") ?? throw new InvalidDataException("В книге отсутствуют стили ячеек.");
        var oldStyles = xfs.Elements(Main + "xf").ToArray();
        var styleIndex = (int?)cell.Attribute("s") ?? 0;
        if (styleIndex < 0 || styleIndex >= oldStyles.Length) throw new InvalidDataException("Повреждён стиль ячейки даты.");
        var formats = styles.Root.Element(Main + "numFmts");
        if (formats is null) { formats = new(Main + "numFmts"); styles.Root.AddFirst(formats); }
        var formatId = Math.Max(164, formats.Elements(Main + "numFmt").Select(f => (int)f.Attribute("numFmtId")! + 1).DefaultIfEmpty(164).Max());
        formats.Add(new XElement(Main + "numFmt", new XAttribute("numFmtId", formatId), new XAttribute("formatCode", "dd.mm.yyyy")));
        formats.SetAttributeValue("count", formats.Elements().Count());
        var clone = new XElement(oldStyles[styleIndex]);
        clone.SetAttributeValue("numFmtId", formatId); clone.SetAttributeValue("applyNumberFormat", 1);
        xfs.Add(clone); xfs.SetAttributeValue("count", oldStyles.Length + 1);
        cell.SetAttributeValue("s", oldStyles.Length); cell.SetAttributeValue("t", "n");
        cell.Element(Main + "is")?.Remove();
        cell.SetElementValue(Main + "v", serial.ToString(CultureInfo.InvariantCulture));
        var created = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew))
            {
                created = true;
                using var result = new ZipArchive(output, ZipArchiveMode.Create);
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var part = result.CreateEntry(entry.FullName); part.LastWriteTime = entry.LastWriteTime;
                    using var stream = part.Open();
                    if (entry.FullName == sheetPath) sheet.Save(stream, SaveOptions.DisableFormatting);
                    else if (entry.FullName == stylesPath) styles.Save(stream, SaveOptions.DisableFormatting);
                    else { using var input = entry.Open(); input.CopyTo(stream); }
                }
            }
            if (ReadDate(destination, target) != date) throw new InvalidDataException("Дата не сохранилась. Комплект не создан.");
        }
        catch { if (created) File.Delete(destination); throw; }
    }

    private static DateOnly ReadDate(ZipArchive zip, DateCell target)
    {
        var book = ReadXml(zip, "xl/workbook.xml");
        var sheet = ReadXml(zip, SheetPath(book, ReadXml(zip, "xl/_rels/workbook.xml.rels"), target.SheetName));
        var cell = Cell(sheet, target.Address);
        if (cell.Element(Main + "f") is not null) throw new InvalidDataException("В выбранной ячейке формула. Выберите ячейку с датой, введённой вручную.");
        var type = (string?)cell.Attribute("t");
        var value = cell.Element(Main + "v")?.Value ?? "";
        if (type is null or "n" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            if (!double.IsFinite(serial) || serial < (Is1904(book) ? 0 : 61) || serial > 2957003)
                throw new InvalidDataException("В выбранной ячейке нет поддерживаемой даты.");
            try { return DateOnly.FromDateTime(Is1904(book) ? new DateTime(1904, 1, 1).AddDays(serial) : DateTime.FromOADate(serial)); }
            catch (ArgumentException) { throw new InvalidDataException("Дата в ячейке выходит за допустимый диапазон."); }
        }
        if (type == "s")
        {
            var shared = ReadXml(zip, PartPath(ReadXml(zip, "xl/_rels/workbook.xml.rels"), "sharedStrings"));
            if (!int.TryParse(value, out var index) || index < 0) throw new InvalidDataException("Повреждён текст даты.");
            value = string.Concat(shared.Root!.Elements(Main + "si").ElementAt(index).Descendants(Main + "t").Select(t => t.Value));
        }
        if (type == "inlineStr") value = string.Concat(cell.Descendants(Main + "t").Select(t => t.Value));
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fffZ" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return DateOnly.FromDateTime(date);
        throw new InvalidDataException("В выбранной ячейке нет даты. Проверьте лист и адрес ячейки.");
    }

    private static XElement Cell(XDocument sheet, string address)
    {
        var normalized = address.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z]{1,3}[1-9][0-9]{0,6}$")) throw new InvalidDataException("Укажите адрес ячейки, например J1.");
        return sheet.Descendants(Main + "c").SingleOrDefault(c => (string?)c.Attribute("r") == normalized)
            ?? throw new InvalidDataException($"Ячейка {normalized} не найдена на выбранном листе.");
    }
    private static bool Is1904(XDocument book) => (string?)book.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is "1" or "true";
    private static double ToSerial(DateOnly date, bool date1904)
    {
        if (date < new DateOnly(date1904 ? 1904 : 1900, date1904 ? 1 : 3, 1)) throw new InvalidDataException("Дата слишком ранняя для этой книги.");
        return date1904 ? date.DayNumber - new DateOnly(1904, 1, 1).DayNumber : date.ToDateTime(TimeOnly.MinValue).ToOADate();
    }
    private static string SheetPath(XDocument book, XDocument relationships, string sheetName)
    {
        var id = (string?)book.Descendants(Main + "sheet").SingleOrDefault(s => (string?)s.Attribute("name") == sheetName)
            ?.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")) ?? throw new InvalidDataException("Выбранный лист отсутствует в книге.");
        return Resolve(relationships.Root!.Elements(Rel + "Relationship").Single(r => (string?)r.Attribute("Id") == id));
    }
    private static string PartPath(XDocument relationships, string type) => Resolve(relationships.Root!.Elements(Rel + "Relationship").SingleOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/" + type, StringComparison.Ordinal) == true) ?? throw new InvalidDataException("Отсутствует часть книги: " + type));
    private static string Resolve(XElement relation)
    {
        if ((string?)relation.Attribute("TargetMode") == "External") throw new InvalidDataException("Внешние ссылки вместо листа или стилей не поддерживаются.");
        var uri = new Uri(new Uri("https://local/xl/workbook.xml"), (string?)relation.Attribute("Target") ?? "");
        if (uri.Host != "local" || !uri.AbsolutePath.StartsWith("/xl/", StringComparison.Ordinal)) throw new InvalidDataException("Некорректный путь внутри книги.");
        return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    }
    private static ZipArchive OpenSnapshot(string path)
    {
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Поддерживаются только файлы .xlsx.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length > FileLimit) throw new InvalidDataException("Файл меню слишком большой: допустимо до 64 МБ.");
        var snapshot = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                if (snapshot.Length + count > FileLimit) throw new InvalidDataException("Файл меню превышает 64 МБ.");
                snapshot.Write(buffer, 0, count);
            }
            snapshot.Position = 0;
            var zip = new ZipArchive(snapshot, ZipArchiveMode.Read);
            if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 256 * 1024 * 1024L || zip.Entries.GroupBy(e => e.FullName).Any(g => g.Count() != 1))
            { zip.Dispose(); throw new InvalidDataException("Некорректный или слишком большой архив Excel."); }
            return zip;
        }
        catch { snapshot.Dispose(); throw; }
    }
    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("Не найдена часть книги: " + path);
        if (entry.Length > XmlLimit) throw new InvalidDataException("Слишком большой лист Excel.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = XmlLimit });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}
