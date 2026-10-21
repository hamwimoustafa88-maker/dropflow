using System.Globalization;
using ClosedXML.Excel;
using DropFlow.Engines.Pdf.Extraction;

namespace DropFlow.Engines.Pdf.Export;

/// <summary>
/// Writes a workbook from the PDF. When the PDF contains tables each table gets its own sheet (named after its page); otherwise each
/// page becomes a sheet with one row per text line and cells split at wide gaps. Numbers become real numeric cells.
/// </summary>
internal static partial class XlsxWriter
{
    public static int Write(PdfTextDocument source, string path)
    {
        using var workbook = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sheets = 0;

        var tables = source.Pages
            .SelectMany(page => page.Blocks.OfType<TableBlock>().Select((table, index) => (Page: page, Table: table, Index: index)))
            .ToList();

        if (tables.Count > 0)
        {
            foreach (var (page, table, index) in tables)
            {
                var perPage = page.Blocks.OfType<TableBlock>().Count();
                var name = perPage > 1 ? $"Page {page.Number} Table {index + 1}" : $"Page {page.Number}";
                var sheet = workbook.AddWorksheet(UniqueName(name, used));
                FillRows(sheet, table.Rows);
                if (table.Rtl) sheet.RightToLeft = true;
                sheets++;
            }
        }
        else
        {
            foreach (var page in source.Pages.Where(p => p.Lines.Count > 0))
            {
                var sheet = workbook.AddWorksheet(UniqueName($"Page {page.Number}", used));
                var rows = page.Lines.Select(line => (IReadOnlyList<string>)(line.Rtl
                    ? line.Segments.Select(s => s.Text).Reverse().ToList()
                    : line.Segments.Select(s => s.Text).ToList())).ToList();
                FillRows(sheet, rows);
                if (page.Lines.Count(l => l.Rtl) * 2 > page.Lines.Count) sheet.RightToLeft = true;
                sheets++;
            }
        }

        if (sheets == 0) workbook.AddWorksheet("Sheet1");
        workbook.Properties.Title = "Converted from PDF by DropFlow";
        workbook.Properties.Author = "DropFlow";
        workbook.SaveAs(path);
        return sheets;
    }

    private static void FillRows(IXLWorksheet sheet, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                var text = rows[r][c];
                if (text.Length == 0) continue;
                var cell = sheet.Cell(r + 1, c + 1);
                if (TryNumber(text, out var number)) cell.Value = number;
                else cell.Value = text;
            }
        }
        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count, 200), 6, 60);
    }

    // 12, -3.5, 1,234.50 (thousands groups must be exactly three digits, so "1,5" is not silently read as 15)
    [System.Text.RegularExpressions.GeneratedRegex(@"^-?([0-9]+|[0-9]{1,3}(,[0-9]{3})+)(\.[0-9]+)?$")]
    private static partial System.Text.RegularExpressions.Regex NumberPattern();

    /// <summary>"007", "+49 30 123", "1/2" and anything over 15 digits stay text so nothing is silently altered.</summary>
    internal static bool TryNumber(string text, out double number)
    {
        number = 0;
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 24 || !NumberPattern().IsMatch(t)) return false;
        var digits = t.TrimStart('-');
        if (digits.Length > 1 && digits[0] == '0' && char.IsAsciiDigit(digits[1])) return false; // leading zeros are an identifier, not a quantity
        if (digits.Count(char.IsAsciiDigit) > 15) return false;
        return double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out number);
    }

    private static string UniqueName(string wanted, HashSet<string> used)
    {
        var clean = new string(wanted.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];

        var candidate = clean;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $" {n}";
            candidate = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix;
        }
        return candidate;
    }
}
