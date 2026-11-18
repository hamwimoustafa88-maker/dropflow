// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>Tables written as an Excel workbook (ClosedXML) or an HTML page.</summary>
internal static class SheetFormats
{
    private const int MaxCellText = 32_767; // Excel's limit for one cell

    public static void WriteXlsx(Table table, Stream output, string sheetName)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SafeSheetName(sheetName));
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = Fit(table.Columns[c]);
            cell.Style.Font.Bold = true;
        }

        var rtl = table.Columns.Any(TextDecoding.HasArabic);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                var value = row[c];
                var cell = sheet.Cell(r + 2, c + 1);
                switch (value.Kind)
                {
                    case ValueKind.Null:
                        break;
                    case ValueKind.Bool:
                        cell.Value = value.Text == "true";
                        break;
                    case ValueKind.Number when double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                                               && double.IsFinite(d) && value.Text!.Count(char.IsAsciiDigit) <= 15:
                        cell.Value = d;
                        break;
                    default: // strings, and numbers a spreadsheet would round (long ids): kept as text
                        var text = value.Text ?? "";
                        if (text.Length > 0) cell.Value = Fit(text);
                        rtl |= r < 200 && TextDecoding.HasArabic(text);
                        break;
                }
            }
        }

        sheet.SheetView.FreezeRows(1);
        if (table.Columns.Count > 0) sheet.Columns(1, table.Columns.Count).AdjustToContents(1, Math.Min(table.Rows.Count + 1, 200), 6, 60);
        if (rtl) sheet.RightToLeft = true;
        workbook.SaveAs(output);
    }

    private static string Fit(string text) => text.Length > MaxCellText ? text[..MaxCellText] : text;

    // Excel sheet names: at most 31 characters, none of : \ / ? * [ ].
    private static string SafeSheetName(string name)
    {
        var clean = new string(name.Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' || char.IsControl(c) ? '_' : c).ToArray()).Trim('\'', ' ');
        if (clean.Length > 31) clean = clean[..31];
        return clean.Length == 0 ? "Data" : clean;
    }

    public static string ToHtml(Table table, string title)
    {
        var sb = new StringBuilder();
        var rtl = TextDecoding.HasArabic(title) || table.Columns.Any(TextDecoding.HasArabic);
        sb.Append("<!doctype html>\n<html dir=\"").Append(rtl ? "rtl" : "ltr").Append("\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
          .Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n")
          .Append("body{font-family:system-ui,\"Segoe UI\",Tahoma,sans-serif;margin:24px;color:#1b1b1f}\n")
          .Append("table{border-collapse:collapse;font-size:14px}\nth,td{border:1px solid #c9c9d1;padding:4px 8px;text-align:start;vertical-align:top}\n")
          .Append("th{background:#f1f1f5;position:sticky;top:0}\n</style>\n</head>\n<body>\n<table>\n<thead><tr>");
        foreach (var column in table.Columns) sb.Append("<th dir=\"auto\">").Append(WebUtility.HtmlEncode(column)).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in table.Rows)
        {
            sb.Append("<tr>");
            foreach (var value in row) sb.Append("<td dir=\"auto\">").Append(WebUtility.HtmlEncode(value.Text ?? "")).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n</body>\n</html>\n");
        return sb.ToString();
    }
}
