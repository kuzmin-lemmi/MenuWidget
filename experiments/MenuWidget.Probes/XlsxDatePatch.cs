using System.Xml.Linq;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Probes;

// Existing preservation checks now exercise the application's real editor.
internal static class XlsxDatePatch
{
    internal static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void WriteCopy(string source, string destination, DateCell target, DateOnly date)
        => new XlsxDateEditor().WriteCopy(source, destination, target, date);
}
