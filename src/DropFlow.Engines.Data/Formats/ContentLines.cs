// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace DropFlow.Engines.Data.Formats;

/// <summary>One property line of a vCard or iCalendar file: <c>[group.]NAME;PARAM=a,b;BARE:value</c>.</summary>
internal sealed record ContentLine(string Name, IReadOnlyList<KeyValuePair<string, string>> Parameters, string Value)
{
    /// <summary>All values of a parameter (case-insensitive name), split on commas. A vCard 2.1 bare parameter (TEL;HOME;CELL) is a TYPE.</summary>
    public IEnumerable<string> Param(string name) =>
        Parameters.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                  .SelectMany(p => p.Value.Replace("\"", "").Split(','))   // TYPE="work,voice" is a quoted list
                  .Select(v => v.Trim())
                  .Where(v => v.Length > 0);

    public string? First(string name) => Param(name).FirstOrDefault();

    /// <summary>A parameter's whole value without its quotes (CN="Bo, Jr." is one name, not two).</summary>
    public string? Whole(string name) =>
        Parameters.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value.Trim().Trim('"')).FirstOrDefault();

    public bool Has(string name, string value) => Param(name).Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The line syntax vCard (RFC 2425/2426/6350) and iCalendar (RFC 5545) share: unfolding, parameters, text escapes, and folding on write.
/// </summary>
internal static class ContentLines
{
    /// <summary>
    /// Physical lines to logical lines. A line starting with a space or tab continues the previous one; in a vCard 2.1 quoted-printable
    /// value a line ending in '=' continues on the next line (soft line break).
    /// </summary>
    public static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        StringBuilder? current = null;
        foreach (var line in lines)
        {
            if (current is not null && IsQuotedPrintable(current) && current.Length > 0 && current[^1] == '=')
            {
                current.Length--;
                current.Append(line.TrimStart(' ', '\t'));
                continue;
            }
            if (current is not null && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }
            if (current is not null) yield return current.ToString();
            current = new StringBuilder(line);
        }
        if (current is not null && current.Length > 0) yield return current.ToString();
    }

    private static bool IsQuotedPrintable(StringBuilder line)
    {
        var text = line.ToString();
        var colon = text.IndexOf(':');
        return colon > 0 && text[..colon].Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    public static ContentLine? Parse(string line)
    {
        var colon = -1;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;

        var head = SplitOutsideQuotes(line[..colon], ';');
        var name = head[0].Trim();
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..]; // "item1.TEL": the group is not needed
        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var part in head.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) parameters.Add(new(Bare(part.Trim()), part.Trim()));
            else parameters.Add(new(part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }
        return new ContentLine(name.ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    // vCard 2.1 writes some parameters without a name: QUOTED-PRINTABLE and BASE64 are encodings, CHARSET never is, the rest are types.
    private static string Bare(string value) => value.ToUpperInvariant() switch
    {
        "QUOTED-PRINTABLE" or "BASE64" or "8BIT" or "7BIT" or "B" => "ENCODING",
        _ => "TYPE",
    };

    public static List<string> SplitOutsideQuotes(string text, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (c == separator && !quoted) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>The value as text: quoted-printable decoded with the line's CHARSET (UTF-8 by default).</summary>
    public static string RawText(ContentLine line)
    {
        var encoding = line.First("ENCODING");
        if (encoding is not null && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
            return DecodeQuotedPrintable(line.Value, TextDecoding.ByName(line.First("CHARSET")));
        return line.Value;
    }

    public static string DecodeQuotedPrintable(string text, Encoding encoding)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                bytes.Add((byte)Convert.ToInt32(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '=' && i == text.Length - 1)
            {
                // a soft line break left at the very end
            }
            else
            {
                bytes.AddRange(TextDecoding.Utf8.GetBytes(c.ToString()));
            }
        }
        return encoding.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    /// <summary>Splits a structured value (N, ADR, ORG) on unescaped ';' and unescapes each part.</summary>
    public static List<string> Components(string value)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) { sb.Append(value[i]).Append(value[++i]); continue; }
            if (value[i] == ';') { parts.Add(Unescape(sb.ToString())); sb.Clear(); continue; }
            sb.Append(value[i]);
        }
        parts.Add(Unescape(sb.ToString()));
        return parts;
    }

    /// <summary>Text escapes: \n and \N are line breaks, \, \; \\ are the characters themselves.</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
                continue;
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");

    /// <summary>Folds one logical line at 75 octets (UTF-8), never inside a character, with CRLF and a space as the spec asks.</summary>
    public static void AppendFolded(StringBuilder sb, string line)
    {
        var octets = 0;
        var limit = 75;
        for (var i = 0; i < line.Length; i++)
        {
            var width = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 4 : TextDecoding.Utf8.GetByteCount(line[i].ToString());
            if (octets + width > limit)
            {
                sb.Append("\r\n ");
                octets = 1;
                limit = 75;
            }
            sb.Append(line[i]);
            if (width == 4) sb.Append(line[++i]);
            octets += width;
        }
        sb.Append("\r\n");
    }
}
