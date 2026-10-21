using System.Globalization;
using System.Text;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>An RFC 4180 reader and writer with the dialect handling real files need (semicolon-separated "CSV" from European and Arabic Excel).</summary>
internal static class Csv
{
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// Picks the delimiter that gives the most consistent column count over the first rows (outside quotes).
    /// Comma wins ties, which keeps ordinary files predictable.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        var best = ',';
        var bestScore = -1.0;
        foreach (var candidate in Candidates)
        {
            var rows = Parse(text, candidate, maxRows: 20);
            if (rows.Count == 0) continue;
            var widths = rows.Select(r => r.Count).ToList();
            var common = widths.GroupBy(w => w).OrderByDescending(g => g.Count()).First();
            if (common.Key < 2) continue; // this delimiter never splits anything
            var score = common.Count() * 1000.0 + common.Key;
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// Parses CSV text. Quoted fields may contain delimiters, quotes (doubled) and line breaks; CRLF, LF and CR all end a record;
    /// a final line break does not add an empty record, and trailing completely empty records are dropped.
    /// </summary>
    public static List<List<string>> Parse(string text, char delimiter = ',', int maxRows = int.MaxValue)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var atFieldStart = true; // a quote only opens a quoted field at the very start of a field
        var i = 0;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            atFieldStart = true;
        }

        bool EndRow()
        {
            EndField();
            rows.Add(row);
            row = [];
            return rows.Count >= maxRows;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && atFieldStart)
            {
                inQuotes = true;
                atFieldStart = false;
                i++;
            }
            else if (c == delimiter)
            {
                EndField();
                i++;
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                if (EndRow()) return rows;
            }
            else
            {
                field.Append(c);
                atFieldStart = false;
                i++;
            }
        }

        if (!atFieldStart || field.Length > 0 || row.Count > 0) EndRow();

        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    /// <summary>Writes rows as CSV. Fields are quoted only when they contain the delimiter, a quote or a line break.</summary>
    public static string Write(IEnumerable<IReadOnlyList<object?>> rows, char delimiter = ',', string newline = "\r\n")
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                sb.Append(Quote(Format(row[i]), delimiter));
            }
            sb.Append(newline);
        }
        return sb.ToString();
    }

    /// <summary>Tab-separated text has no quoting rule, so tabs and line breaks inside a value become spaces.</summary>
    public static string WriteTsv(IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join('\t', row.Select(v => Format(v).Replace('\t', ' ').Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '))));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Quote(string value, char delimiter)
    {
        if (value.Length == 0) return value;
        var needs = value.Contains(delimiter) || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
        return needs ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>The text a cell shows in a text file: invariant numbers, ISO dates, TRUE/FALSE.</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "TRUE" : "FALSE",
        double d => FormatNumber(d),
        float f => FormatNumber(f),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        int or long or short or byte => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
