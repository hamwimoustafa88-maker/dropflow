using DocumentFormat.OpenXml.Packaging;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Reads a workbook with the Open XML SDK (the production writer is ClosedXML) into sheet name -> cell reference -> value.</summary>
internal static class XlsxProbe
{
    public sealed record Cell(string Text, bool IsNumber, bool IsBool = false);

    /// <summary>Reads the workbook with the Open XML SDK (the writer is ClosedXML).</summary>
    public static Dictionary<string, Dictionary<string, Cell>> Read(string path, out Dictionary<string, bool> rtlSheets)
    {
        var result = new Dictionary<string, Dictionary<string, Cell>>();
        rtlSheets = [];
        using var doc = SpreadsheetDocument.Open(path, false);
        var workbook = doc.WorkbookPart!;
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        foreach (var sheet in workbook.Workbook!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>())
        {
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var cells = new Dictionary<string, Cell>();
            foreach (var c in part.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>())
            {
                var raw = c.CellValue?.Text ?? c.InnerText;
                var type = c.DataType?.Value;
                var S = DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString;
                var I = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString;
                var B = DocumentFormat.OpenXml.Spreadsheet.CellValues.Boolean;
                var N = DocumentFormat.OpenXml.Spreadsheet.CellValues.Number;
                cells[c.CellReference!.Value!] =
                    type == S ? new Cell(shared[int.Parse(raw)], false)
                    : type == I ? new Cell(c.InnerText, false)
                    : type == B ? new Cell(raw == "1" ? "TRUE" : "FALSE", false, true)
                    : new Cell(raw, type is null || type == N);
            }
            result[sheet.Name!.Value!] = cells;
            rtlSheets[sheet.Name.Value!] = part.Worksheet!.SheetViews?.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetView>().Any(v => v.RightToLeft?.Value == true) == true;
        }
        return result;
    }
}
