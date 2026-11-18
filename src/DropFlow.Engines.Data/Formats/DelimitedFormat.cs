// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>
/// CSV and TSV (RFC 4180 quoting: a field with the separator, a quote or a line break is quoted, quotes are doubled). The first row is
/// the header. A .csv file may use ',', ';' (Excel in Arabic and European languages) or a tab; the separator is detected from the
/// header row. Written files are UTF-8 with a byte order mark and CRLF line ends, which is what Excel needs to show Arabic.
/// </summary>
internal static class DelimitedFormat
{
    /// <summary>Reads the text as a table; cells are typed only when plainly numbers (see <see cref="DataValue.Infer"/>).</summary>
    public static Table Read(string text, char? separator)
    {
        var rows = Parse(text, separator ?? DetectSeparator(text));
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) return new Table([], []);

        var columns = Tables.UniqueNames(rows[0]);
        var body = new List<DataValue[]>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 1 && row[0].Length == 0) continue; // a blank line
            var cells = new DataValue[Math.Max(columns.Count, row.Count)];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = i < row.Count ? (row[i].Length == 0 ? DataValue.Null : DataValue.Infer(row[i])) : DataValue.Null;
            body.Add(cells);
        }

        // A row longer than the header gets extra columns rather than losing its cells.
        var width = body.Count == 0 ? columns.Count : Math.Max(columns.Count, body.Max(r => r.Length));
        var names = columns.ToList();
        for (var i = names.Count; i < width; i++) names.Add($"column{i + 1}");
        var fixedRows = body.Select(r => r.Length == width ? r : [.. r, .. Enumerable.Repeat(DataValue.Null, width - r.Length)]).ToList();
        return new Table(Tables.UniqueNames(names), fixedRows);
    }

    public static char DetectSeparator(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && (c == '\n' || c == '\r')) break;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value == 0 ? ',' : best.Key;
    }

    public static List<List<string>> Parse(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && field.Length == 0) { quoted = true; i++; continue; }
            if (c == separator) { row.Add(field.ToString()); field.Clear(); i++; continue; }
            if (c is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }
            field.Append(c);
            i++;
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public static string Write(Table table, char separator)
    {
        var sb = new StringBuilder();
        AppendRow(sb, table.Columns, separator);
        foreach (var row in table.Rows) AppendRow(sb, row.Select(v => v.Text ?? ""), separator);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells, char separator)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) sb.Append(separator);
            first = false;
            var needsQuotes = cell.IndexOfAny([separator, '"', '\r', '\n']) >= 0 || (cell.Length > 0 && (char.IsWhiteSpace(cell[0]) || char.IsWhiteSpace(cell[^1])));
            if (needsQuotes) sb.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
            else sb.Append(cell);
        }
        sb.Append("\r\n");
    }
}
