using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MenuWidget.Core;
using MenuWidget.Probes;

var temp = Path.Combine(Path.GetTempPath(), "MenuWidget-Probes-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var passed = 0;
var failed = 0;
try
{
    var cycle = new CycleService();
    var settings = new CycleSettings(new DateOnly(2026, 10, 12));
    Check("calendar/first-week", () => Equal(MenuSetId.First, cycle.GetSet(new(2026, 10, 12), settings)));
    Check("calendar/second-week", () => Equal(MenuSetId.Second, cycle.GetSet(new(2026, 10, 19), settings)));
    Check("calendar/repeat", () => Equal(MenuSetId.First, cycle.GetSet(new(2026, 10, 26), settings)));
    Check("calendar/before-anchor", () => Equal(MenuSetId.Second, cycle.GetSet(new(2026, 10, 5), settings)));
    Check("calendar/weekend", () => Equal(new DateOnly(2026, 10, 19), cycle.SuggestedMonday(new(2026, 10, 18))));
    Check("calendar/weekday", () => Equal(new DateOnly(2026, 10, 19), cycle.SuggestedMonday(new(2026, 10, 21))));
    Check("calendar/year-boundary", () => Equal("2027-01-01-sm.xlsx", cycle.Preview(new(2026, 12, 28), MenuSetId.First)[4].FileName));
    Check("calendar/leap-day", () => Equal(new DateOnly(2028, 2, 29), cycle.Preview(new(2028, 2, 28), MenuSetId.Second)[1].Date));
    Check("calendar/reject-non-monday", () => Throws<ArgumentException>(() => cycle.Preview(new(2026, 10, 20), MenuSetId.First)));

    foreach (var date1904 in new[] { false, true })
    {
        Check($"xlsx/{(date1904 ? 1904 : 1900)}/preservation", () =>
        {
            var source = Path.Combine(temp, $"source-{date1904}.xlsx");
            var output = Path.Combine(temp, $"output-{date1904}.xlsx");
            Fixture(source, date1904);
            var originalHash = SHA256.HashData(File.ReadAllBytes(source));
            XlsxDatePatch.WriteCopy(source, output, new("Меню", "J1"), new(2026, 10, 19));
            Assert(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Source changed");
            using var a = ZipFile.OpenRead(source);
            using var b = ZipFile.OpenRead(output);
            Assert(a.Entries.Select(e => e.FullName).SequenceEqual(b.Entries.Select(e => e.FullName)), "Parts changed");
            foreach (var part in a.Entries)
            {
                if (part.FullName is not ("xl/worksheets/menu-seven.xml" or "xl/styles.xml"))
                    Assert(Bytes(a, part.FullName).SequenceEqual(Bytes(b, part.FullName)), $"Unrelated part changed: {part.FullName}");
            }
            XNamespace n = XlsxDatePatch.Main;
            var before = ParsePart(a, "xl/worksheets/menu-seven.xml");
            var after = ParsePart(b, "xl/worksheets/menu-seven.xml");
            var oldCell = before.Descendants(n + "c").Single(c => (string?)c.Attribute("r") == "J1");
            var newCell = after.Descendants(n + "c").Single(c => (string?)c.Attribute("r") == "J1");
            Equal(date1904 ? "44852" : "46314", newCell.Element(n + "v")!.Value);
            Equal("1", (string)newCell.Attribute("s")!);
            newCell.ReplaceWith(new XElement(oldCell));
            Assert(XNode.DeepEquals(before, after), "Non-date content changed in sheet");
            var oldStyle = ParsePart(a, "xl/styles.xml");
            var newStyle = ParsePart(b, "xl/styles.xml");
            Assert(XNode.DeepEquals(oldStyle.Root!.Element(n + "cellXfs")!.Elements().First(), newStyle.Root!.Element(n + "cellXfs")!.Elements().First()), "Shared style mutated");
            Equal("dd.mm.yyyy", (string)newStyle.Root.Element(n + "numFmts")!.Elements().Last().Attribute("formatCode")!);
            Equal(2, newStyle.Root.Element(n + "cellXfs")!.Elements().Count());
        });
    }
    var sourceForErrors = Path.Combine(temp, "guard-source.xlsx");
    Fixture(sourceForErrors, false);
    Check("xlsx/no-source-overwrite", () => Throws<InvalidOperationException>(() => XlsxDatePatch.WriteCopy(sourceForErrors, sourceForErrors, new("Меню", "J1"), new(2026, 10, 19))));
    Check("xlsx/no-existing-output-overwrite", () =>
    {
        var dest = Path.Combine(temp, "existing.xlsx"); File.WriteAllText(dest, "keep-me");
        Throws<IOException>(() => XlsxDatePatch.WriteCopy(sourceForErrors, dest, new("Меню", "J1"), new(2026, 10, 19)));
        Equal("keep-me", File.ReadAllText(dest));
    });
    Check("xlsx/reject-formula-date", () =>
    {
        var src = Path.Combine(temp, "formula.xlsx"); Fixture(src, false, formula: true);
        var dest = Path.Combine(temp, "no-formula-output.xlsx");
        Throws<InvalidDataException>(() => XlsxDatePatch.WriteCopy(src, dest, new("Меню", "J1"), new(2026, 10, 19)));
        Assert(!File.Exists(dest), "Failed export left a file");
    });
    Check("xlsx/reject-missing-cell", () => Throws<InvalidDataException>(() => XlsxDatePatch.WriteCopy(sourceForErrors, Path.Combine(temp, "missing.xlsx"), new("Меню", "Z99"), new(2026, 10, 19))));
    Console.WriteLine($"RESULT: {passed} passed, {failed} failed. All fixtures synthetic; no school files read.");
    return failed == 0 ? 0 : 1;
}
finally { Directory.Delete(temp, recursive: true); }

void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static XDocument ParsePart(ZipArchive archive, string part) => XDocument.Parse(Encoding.UTF8.GetString(Bytes(archive, part)).TrimStart('\uFEFF'));
static byte[] Bytes(ZipArchive archive, string part)
{
    using var stream = archive.GetEntry(part)!.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
}
static void Fixture(string path, bool date1904, bool formula = false)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
    Add("[Content_Types].xml", """
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/menu-seven.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/reference.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
        """);
    Add("_rels/.rels", """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="book" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
        """);
    Add("xl/workbook.xml", $$"""
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><workbookPr date1904="{{(date1904 ? 1 : 0)}}"/><sheets><sheet name="Справочник" sheetId="2" r:id="reference"/><sheet name="Меню" sheetId="7" r:id="menu"/></sheets></workbook>
        """);
    Add("xl/_rels/workbook.xml.rels", """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="reference" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/reference.xml"/><Relationship Id="menu" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/menu-seven.xml"/><Relationship Id="styles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
        """);
    Add("xl/styles.xml", """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy"/></numFmts><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>
        """);
    Add("xl/worksheets/menu-seven.xml", $$"""
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="3" topLeftCell="A4" state="frozen"/></sheetView></sheetViews><cols><col min="1" max="10" width="18" customWidth="1"/></cols><sheetData><row r="1" ht="28" customHeight="1"><c r="I1" t="inlineStr"><is><t>День</t></is></c><c r="J1" s="0" t="n">{{(formula ? "<f>TODAY()</f>" : "")}}<v>46300</v></c></row><row r="2" hidden="1"><c r="A2" s="0" t="n"><v>46300</v></c><c r="B2" t="n"><f>1+2</f><v>3</v></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>Искусственное меню для проверки</t></is></c></row></sheetData><mergeCells count="1"><mergeCell ref="A3:D3"/></mergeCells><printOptions horizontalCentered="1"/><pageMargins left="0.3" right="0.3" top="0.5" bottom="0.5" header="0.2" footer="0.2"/><pageSetup paperSize="9" orientation="landscape" fitToWidth="1" fitToHeight="1"/><headerFooter><oddFooter>Тест &amp;P</oddFooter></headerFooter></worksheet>
        """);
    Add("xl/worksheets/reference.xml", """
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Не изменять</t></is></c></row></sheetData></worksheet>
        """);
    Add("docProps/custom.xml", "<synthetic>Preserve exact bytes, including this whitespace. </synthetic>");
}
