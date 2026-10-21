using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ClosedXML.Excel;
using DropFlow.Engines.Pdf.Export;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>One sheet of cells. A cell is null, string, bool, long, double or DateTime.</summary>
internal sealed record Sheet(string Name, List<List<object?>> Rows);

/// <summary>Reads and writes spreadsheet-like data: CSV, TSV, XLSX, JSON and HTML tables, all in-process.</summary>
internal static class Tabular
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    // ---- reading --------------------------------------------------------------------------------------------------------

    public static List<Sheet> Read(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var name = Path.GetFileName(path);
        try
        {
            return ext switch
            {
                "csv" => [FromText(path, null)],
                "tsv" => [FromText(path, '\t')],
                "xlsx" => ReadXlsx(path),
                "json" => ReadJson(path),
                _ => throw new ConversionFailure($"{name}: .{ext} files are not a table format DropFlow can read directly."),
            };
        }
        catch (ConversionFailure)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new ConversionFailure($"{name} could not be read ({ex.Message}).");
        }
    }

    private static Sheet FromText(string path, char? delimiter)
    {
        var text = TextFile.Read(path);
        var rows = Csv.Parse(text, delimiter ?? Csv.DetectDelimiter(text));
        return new Sheet(Path.GetFileNameWithoutExtension(path), rows.Select(r => r.Select(c => (object?)c).ToList()).ToList());
    }

    private static List<Sheet> ReadXlsx(string path)
    {
        using var workbook = new XLWorkbook(new MemoryStream(File.ReadAllBytes(path)));
        var sheets = new List<Sheet>();
        var visible = workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).ToList();
        foreach (var worksheet in visible.Count > 0 ? visible : workbook.Worksheets.ToList())
        {
            var rows = new List<List<object?>>();
            var used = worksheet.RangeUsed();
            if (used is not null)
            {
                foreach (var row in used.Rows())
                    rows.Add(row.Cells().Select(c => CellValue(c)).ToList());
            }
            sheets.Add(new Sheet(worksheet.Name, rows));
        }
        if (sheets.Count == 0) sheets.Add(new Sheet(Path.GetFileNameWithoutExtension(path), []));
        return sheets;
    }

    private static object? CellValue(IXLCell cell)
    {
        XLCellValue value;
        try
        {
            value = cell.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return cell.CachedValue.ToString(); // a formula ClosedXML cannot evaluate: use the last value Excel stored
        }

        if (value.IsBlank) return null;
        if (value.IsBoolean) return value.GetBoolean();
        if (value.IsNumber)
        {
            var d = value.GetNumber();
            return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? (object)(long)d : d;
        }
        if (value.IsDateTime) return value.GetDateTime();
        if (value.IsTimeSpan) return value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture);
        return value.ToString();
    }

    private static List<Sheet> ReadJson(string path)
    {
        var text = TextFile.Read(path);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        var name = Path.GetFileNameWithoutExtension(path);

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return [new Sheet(name, RowsFromArray(root))];

            case JsonValueKind.Object:
                var properties = root.EnumerateObject().ToList();
                if (properties.Count > 0 && properties.All(p => p.Value.ValueKind == JsonValueKind.Array))
                    return properties.Select(p => new Sheet(p.Name, RowsFromArray(p.Value))).ToList();
                return [new Sheet(name, RowsFromObjects([root]))];

            default:
                return [new Sheet(name, [["value"], [Scalar(root)]])];
        }
    }

    private static List<List<object?>> RowsFromArray(JsonElement array)
    {
        var items = array.EnumerateArray().ToList();
        if (items.Count == 0) return [];
        if (items.All(i => i.ValueKind == JsonValueKind.Object)) return RowsFromObjects(items);
        if (items.All(i => i.ValueKind == JsonValueKind.Array))
            return items.Select(i => i.EnumerateArray().Select(Scalar).ToList()).ToList();

        var rows = new List<List<object?>> { new() { "value" } };
        rows.AddRange(items.Select(i => new List<object?> { Scalar(i) }));
        return rows;
    }

    private static List<List<object?>> RowsFromObjects(IReadOnlyList<JsonElement> objects)
    {
        var keys = new List<string>();
        foreach (var o in objects)
            foreach (var p in o.EnumerateObject())
                if (!keys.Contains(p.Name)) keys.Add(p.Name);

        var rows = new List<List<object?>> { keys.Select(k => (object?)k).ToList() };
        foreach (var o in objects)
            rows.Add(keys.Select(k => o.TryGetProperty(k, out var v) ? Scalar(v) : null).ToList());
        return rows;
    }

    private static object? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        _ => e.GetRawText(), // nested object or array: keep it as JSON text rather than lose it
    };

    // ---- writing --------------------------------------------------------------------------------------------------------

    /// <summary>CSV and TSV text cells from numbers-as-text keep their type when written to XLSX or JSON: obvious numbers become numbers.</summary>
    public static object? Infer(object? value) =>
        value is string s && XlsxWriter.TryNumber(s, out var d) ? (d == Math.Floor(d) && Math.Abs(d) < 1e15 && !s.Contains('.') ? (object)(long)d : d) : value;

    public static void WriteXlsx(IReadOnlyList<Sheet> sheets, string path, bool inferTypes)
    {
        using var workbook = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            var worksheet = workbook.AddWorksheet(UniqueSheetName(sheet.Name, used));
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                for (var c = 0; c < sheet.Rows[r].Count; c++)
                {
                    var value = inferTypes ? Infer(sheet.Rows[r][c]) : sheet.Rows[r][c];
                    var cell = worksheet.Cell(r + 1, c + 1);
                    switch (value)
                    {
                        case null: break;
                        case string s when s.Length == 0: break;
                        case string s: cell.Value = s.Length > 32767 ? s[..32767] : s; break;
                        case bool b: cell.Value = b; break;
                        case long l: cell.Value = l; break;
                        case double d: cell.Value = d; break;
                        case DateTime dt: cell.Value = dt; break;
                        default: cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture); break;
                    }
                }
            }

            if (sheet.Rows.Count > 0) worksheet.Columns().AdjustToContents(1, Math.Min(sheet.Rows.Count, 200), 6, 60);
            if (sheet.Rows.SelectMany(r => r).OfType<string>().Any(ArabicShaper.ContainsRtl)) worksheet.RightToLeft = true;
        }
        if (workbook.Worksheets.Count == 0) workbook.AddWorksheet("Sheet1");
        workbook.SaveAs(path);
    }

    public static string ToJson(IReadOnlyList<Sheet> sheets, bool inferTypes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JsonOptions.Encoder }))
        {
            if (sheets.Count == 1)
            {
                WriteSheetArray(writer, sheets[0], inferTypes);
            }
            else
            {
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sheet in sheets)
                {
                    var key = sheet.Name;
                    for (var n = 2; !names.Add(key); n++) key = $"{sheet.Name} {n}";
                    writer.WritePropertyName(key);
                    WriteSheetArray(writer, sheet, inferTypes);
                }
                writer.WriteEndObject();
            }
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteSheetArray(Utf8JsonWriter writer, Sheet sheet, bool inferTypes)
    {
        writer.WriteStartArray();
        if (sheet.Rows.Count > 1)
        {
            var width = sheet.Rows.Max(r => r.Count);
            var keys = Headers(sheet.Rows[0], width);
            foreach (var row in sheet.Rows.Skip(1))
            {
                writer.WriteStartObject();
                for (var c = 0; c < width; c++)
                {
                    writer.WritePropertyName(keys[c]);
                    WriteValue(writer, c < row.Count ? (inferTypes ? Infer(row[c]) : row[c]) : null);
                }
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }

    /// <summary>Header cells become property names: blank ones are numbered, duplicates get a suffix, so no data is dropped.</summary>
    internal static List<string> Headers(IReadOnlyList<object?> header, int width)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < width; c++)
        {
            var key = c < header.Count ? Csv.Format(header[c]).Trim() : "";
            if (key.Length == 0) key = $"column{c + 1}";
            var unique = key;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{key}_{n}";
            keys.Add(unique);
        }
        return keys;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d when double.IsFinite(d): writer.WriteNumberValue(d); break;
            case DateTime dt: writer.WriteStringValue(Csv.Format(dt)); break;
            default: writer.WriteStringValue(Csv.Format(value)); break;
        }
    }

    public static string ToHtml(IReadOnlyList<Sheet> sheets, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\" dir=\"auto\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        sb.Append("body{font-family:system-ui,'Segoe UI',Arial,sans-serif;margin:2rem}\n");
        sb.Append("table{border-collapse:collapse;margin:1rem 0}th,td{border:1px solid #999;padding:.3rem .6rem;text-align:start}th{background:#f0f0f0}\n</style>\n</head>\n<body>\n");
        foreach (var sheet in sheets)
        {
            if (sheets.Count > 1) sb.Append("<h2>").Append(WebUtility.HtmlEncode(sheet.Name)).Append("</h2>\n");
            sb.Append("<table dir=\"auto\">\n");
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var tag = r == 0 ? "th" : "td";
                if (r == 0) sb.Append("<thead>\n");
                else if (r == 1) sb.Append("<tbody>\n");
                sb.Append("<tr>");
                foreach (var cell in sheet.Rows[r])
                    sb.Append('<').Append(tag).Append('>').Append(WebUtility.HtmlEncode(Csv.Format(cell))).Append("</").Append(tag).Append('>');
                sb.Append("</tr>\n");
                if (r == 0) sb.Append("</thead>\n");
            }
            if (sheet.Rows.Count > 1) sb.Append("</tbody>\n");
            sb.Append("</table>\n");
        }
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    public static string ToDelimited(Sheet sheet, string format) =>
        format == "tsv" ? Csv.WriteTsv(sheet.Rows) : Csv.Write(sheet.Rows, ',');

    internal static string UniqueSheetName(string wanted, HashSet<string> used)
    {
        var clean = new string(wanted.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim().Trim('\'');
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
