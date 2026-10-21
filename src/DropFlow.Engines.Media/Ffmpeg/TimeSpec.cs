using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>A time range in seconds (start inclusive, end exclusive).</summary>
internal readonly record struct TimeRange(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// Parsing and formatting of the time strings users type into tool options. Accepts plain seconds ("90", "90.5"),
/// clock times ("1:30", "01:02:03.250", "00:00:05,500") and Arabic-Indic digits and separators.
/// </summary>
internal static partial class TimeSpec
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Replaces Arabic-Indic / Persian digits and Arabic decimal/comma characters with their ASCII counterparts.</summary>
    public static string NormalizeDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                '٫' => '.',   // Arabic decimal separator
                '٬' => ',',   // Arabic thousands separator
                '،' => ',',   // Arabic comma
                '؛' => ';',   // Arabic semicolon
                '–' or '—' or '−' => '-', // en dash, em dash, minus sign
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>Parses a single time. Returns false for anything that is not a non-negative finite time.</summary>
    public static bool TryParseSeconds(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = NormalizeDigits(text.Trim());

        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            double total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                var last = i == parts.Length - 1;
                if (last) part = part.Replace(',', '.');
                if (part.Length == 0) return false;
                if (!last && !part.All(char.IsAsciiDigit)) return false;
                if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, Inv, out var v) || v < 0) return false;
                total = total * 60 + v;
            }
            // 1:75 is accepted as 135 s (mm:ss with seconds >= 60), matching how people type it.
            seconds = total;
            return true;
        }

        s = s.Replace(',', '.');
        if (s.EndsWith('s') || s.EndsWith('S')) s = s[..^1];
        if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, Inv, out var plain)) return false;
        if (plain < 0 || double.IsNaN(plain) || double.IsInfinity(plain)) return false;
        seconds = plain;
        return true;
    }

    /// <summary>Splits "5, 00:01:10; 90.5" into times. Throws <see cref="FormatException"/> on a bad entry.</summary>
    public static IReadOnlyList<double> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<double>();
        foreach (var token in ListSeparator().Split(NormalizeDigits(text)))
        {
            if (token.Length == 0) continue;
            if (!TryParseSeconds(token, out var v)) throw new FormatException($"'{token}' is not a valid time.");
            result.Add(v);
        }
        return result;
    }

    /// <summary>FFmpeg-friendly seconds: invariant culture, at most 3 decimals, no trailing zeros ("12.5", "3").</summary>
    public static string Format(double seconds)
    {
        var s = Math.Round(seconds, 3, MidpointRounding.AwayFromZero).ToString("0.###", Inv);
        return s == "-0" ? "0" : s;
    }

    /// <summary>"HH:MM:SS.mmm" for logs and file names (use <paramref name="forFileName"/> to avoid colons).</summary>
    public static string FormatClock(double seconds, bool forFileName = false)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
        var t = TimeSpan.FromMilliseconds(ms);
        return forFileName
            ? $"{(int)t.TotalHours:00}-{t.Minutes:00}-{t.Seconds:00}.{t.Milliseconds:000}"
            : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>Invariant decimal string with up to <paramref name="decimals"/> places and no trailing zeros.</summary>
    public static string Num(double value, int decimals = 6)
    {
        var s = Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("0." + new string('#', decimals), Inv);
        return s == "-0" ? "0" : s;
    }

    /// <summary>
    /// Parses time ranges: "1.5-2.3; 10-11", "0:10-0:12, 1:00 to 1:05", or JSON "[[1,2],[3.5,4]]". Overlapping ranges are
    /// merged and the result is sorted. Throws <see cref="FormatException"/> with a readable message on bad input.
    /// </summary>
    public static IReadOnlyList<TimeRange> ParseRanges(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var s = NormalizeDigits(text.Trim());
        var ranges = new List<TimeRange>();

        if (s.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2)
                        throw new FormatException("Each range must be [start, end].");
                    ranges.Add(MakeRange(ReadJsonTime(item[0]), ReadJsonTime(item[1]), item.GetRawText()));
                }
            }
            catch (JsonException)
            {
                throw new FormatException("Ranges must look like [[1.5, 2.3], [10, 11]] or 1.5-2.3; 10-11.");
            }
        }
        else
        {
            foreach (var chunk in RangeSeparator().Split(s))
            {
                var entry = chunk.Trim();
                if (entry.Length == 0) continue;
                var m = RangePair().Match(entry);
                if (!m.Success) throw new FormatException($"'{entry}' is not a range. Use start-end, for example 1.5-2.3.");
                if (!TryParseSeconds(m.Groups["a"].Value, out var a) || !TryParseSeconds(m.Groups["b"].Value, out var b))
                    throw new FormatException($"'{entry}' is not a valid range.");
                ranges.Add(MakeRange(a, b, entry));
            }
        }

        return Merge(ranges);
    }

    /// <summary>Sorts and merges overlapping or touching ranges.</summary>
    public static IReadOnlyList<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<TimeRange>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
                merged[^1] = new TimeRange(merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else
                merged.Add(r);
        }
        return merged;
    }

    private static TimeRange MakeRange(double a, double b, string original)
    {
        if (b <= a) throw new FormatException($"Range '{original}' must end after it starts.");
        return new TimeRange(a, b);
    }

    private static double ReadJsonTime(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String when TryParseSeconds(e.GetString(), out var v) => v,
        _ => throw new FormatException("A range value is not a time."),
    };

    // "a-b", "a..b", "a to b" where a/b are times (digits, colons, dot, comma-decimal).
    [GeneratedRegex(@"^(?<a>[\d:.]+)\s*(?:-|\.\.|to|until)\s*(?<b>[\d:.]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RangePair();

    // Ranges are separated by ';', ',' or a new line, so decimals inside a range must use '.'.
    [GeneratedRegex(@"[;,\n\r]+")]
    private static partial Regex RangeSeparator();

    [GeneratedRegex(@"[,;\s]+")]
    private static partial Regex ListSeparator();
}
