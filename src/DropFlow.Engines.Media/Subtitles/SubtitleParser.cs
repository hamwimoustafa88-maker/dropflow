using System.Globalization;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>
/// Reads SRT, WebVTT and ASS/SSA text. Lenient about the things real files get wrong (missing indexes, missing blank lines
/// between cues, <c>.</c> vs <c>,</c> decimals, one- to nine-digit fractions, optional hours, unordered ASS events, BOM characters)
/// and strict about the one thing it cannot work around: a file with no cues throws <see cref="SubtitleFormatException"/>.
/// </summary>
public static partial class SubtitleParser
{
    private const string Time1 = @"(?:(?<h1>\d+):)?(?<m1>\d{1,2}):(?<s1>\d{1,2})(?:[.,](?<f1>\d+))?";
    private const string Time2 = @"(?:(?<h2>\d+):)?(?<m2>\d{1,2}):(?<s2>\d{1,2})(?:[.,](?<f2>\d+))?";

    private static readonly Regex Timing = new(@"^\s*" + Time1 + @"\s*-+>\s*" + Time2 + @"(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ entry points

    /// <summary>Parses text whose format is known from the file extension. If it has no cues, the other formats are tried before giving up.</summary>
    public static SubtitleDocument Parse(string text, SubtitleFormat format)
    {
        text = Clean(text);
        if (string.IsNullOrWhiteSpace(text)) throw new SubtitleFormatException("The subtitle file is empty.");

        var newLine = SubtitleDecoder.DetectNewLine(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var order = new[] { format }.Concat(new[] { SubtitleFormat.Srt, SubtitleFormat.Vtt, SubtitleFormat.Ass }.Where(f => f != format && f != SubtitleFormat.Txt));
        foreach (var candidate in order)
        {
            var doc = candidate switch
            {
                SubtitleFormat.Vtt => ParseVtt(normalized, newLine),
                SubtitleFormat.Ass => ParseAss(normalized, newLine),
                _ => ParseSrt(normalized, newLine),
            };
            if (doc.Cues.Count == 0) continue;
            if (candidate == format) return doc;
            return doc with
            {
                SourceFormat = candidate,
                Warnings = [.. doc.Warnings, $"The file is named like {format.ToString().ToUpperInvariant()} but its content is {candidate.ToString().ToUpperInvariant()}; it was read as {candidate.ToString().ToUpperInvariant()}."],
            };
        }

        throw new SubtitleFormatException(
            $"No subtitles were found. The file does not look like a valid {format.ToString().ToUpperInvariant()} subtitle file (no time ranges such as 00:00:01,000 --> 00:00:02,500).");
    }

    public static SubtitleDocument ParseBytes(byte[] bytes, SubtitleFormat format)
    {
        if (bytes.Length == 0) throw new SubtitleFormatException("The subtitle file is empty.");
        if (SubtitleDecoder.LooksBinary(bytes)) throw new SubtitleFormatException("The file is binary data, not a text subtitle file.");
        var text = SubtitleDecoder.Decode(bytes, out _);
        return Parse(text, format);
    }

    public static SubtitleFormat? FormatFromExtension(string extensionWithoutDot) => extensionWithoutDot.ToLowerInvariant() switch
    {
        "srt" => SubtitleFormat.Srt,
        "vtt" => SubtitleFormat.Vtt,
        "ass" or "ssa" => SubtitleFormat.Ass,
        "txt" => SubtitleFormat.Txt,
        _ => null,
    };

    private static string Clean(string text) => text.TrimStart('﻿', '​');

    // ------------------------------------------------------------------ SRT

    public static SubtitleDocument ParseSrt(string normalizedText, string newLine)
    {
        var lines = normalizedText.Split('\n');
        var warnings = new List<string>();
        var timings = new List<(int Line, Match Match)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Timing.Match(lines[i]);
            if (m.Success) timings.Add((i, m));
        }

        var cues = new List<SubtitleCue>();
        var consumed = new bool[lines.Length];
        for (var t = 0; t < timings.Count; t++)
        {
            var (line, m) = timings[t];
            consumed[line] = true;
            var limit = t + 1 < timings.Count ? timings[t + 1].Line : lines.Length;

            var body = new List<string>();
            var stoppedAtBlank = false;
            for (var k = line + 1; k < limit; k++)
            {
                if (string.IsNullOrWhiteSpace(lines[k])) { stoppedAtBlank = true; break; }
                body.Add(lines[k]);
                consumed[k] = true;
            }

            // Missing blank line between two cues: the last line before the next time range is that cue's index number.
            if (!stoppedAtBlank && t + 1 < timings.Count && body.Count > 0 && IsIndexLine(body[^1]))
            {
                consumed[limit - 1] = false; // it belongs to the next cue (picked up below as that cue's identifier)
                body.RemoveAt(body.Count - 1);
            }
            var id = line > 0 && IsIndexLine(lines[line - 1]) && !consumed[line - 1] ? lines[line - 1].Trim() : null;

            AddCue(cues, warnings, m, string.Join("\n", body), decodeEntities: false, id, settings: null);
        }

        var stray = Enumerable.Range(0, lines.Length).Count(k => !consumed[k] && !string.IsNullOrWhiteSpace(lines[k]) && !IsIndexLine(lines[k]));
        if (stray > 0 && cues.Count > 0) warnings.Add($"{stray} line(s) outside any cue were ignored.");
        return new SubtitleDocument(cues, SubtitleFormat.Srt, newLine, warnings);
    }

    private static bool IsIndexLine(string line) => IndexLine().IsMatch(line);

    // ------------------------------------------------------------------ WebVTT

    public static SubtitleDocument ParseVtt(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        var cues = new List<SubtitleCue>();
        var blocks = SplitBlocks(normalizedText);
        var first = true;

        foreach (var block in blocks)
        {
            var lines = block;
            if (first)
            {
                first = false;
                if (lines[0].StartsWith("WEBVTT", StringComparison.Ordinal) && (lines[0].Length == 6 || lines[0][6] is ' ' or '\t'))
                {
                    // A header block may run straight into the first cue without a blank line: resume at the timing line.
                    var timingAt = lines.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
                    if (timingAt < 0) continue;
                    lines = lines.Skip(timingAt).ToList();
                }
                else
                {
                    warnings.Add("The file has no WEBVTT header.");
                }
            }

            var head = lines[0];
            if (IsBlockKeyword(head, "NOTE") || IsBlockKeyword(head, "STYLE") || IsBlockKeyword(head, "REGION")) continue;

            var timingIndex = lines.FindIndex(0, Math.Min(2, lines.Count), l => l.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                warnings.Add($"Skipped a block without a time range: \"{Shorten(head)}\".");
                continue;
            }

            var m = Timing.Match(lines[timingIndex]);
            if (!m.Success)
            {
                warnings.Add($"Skipped a cue with an unreadable time range: \"{Shorten(lines[timingIndex])}\".");
                continue;
            }

            var id = timingIndex > 0 ? lines[0].Trim() : null;
            var settings = m.Groups["rest"].Value.Trim();
            AddCue(cues, warnings, m, string.Join("\n", lines.Skip(timingIndex + 1)), decodeEntities: true, id, settings.Length == 0 ? null : settings);
        }

        return new SubtitleDocument(cues, SubtitleFormat.Vtt, newLine, warnings);
    }

    private static bool IsBlockKeyword(string line, string keyword) =>
        line.StartsWith(keyword, StringComparison.Ordinal) && (line.Length == keyword.Length || line[keyword.Length] is ' ' or '\t');

    private static List<List<string>> SplitBlocks(string normalizedText)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in normalizedText.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = [];
                blocks.Add(current);
            }
            current.Add(line);
        }
        return blocks;
    }

    // ------------------------------------------------------------------ shared SRT/VTT cue building

    private static void AddCue(List<SubtitleCue> cues, List<string> warnings, Match m, string rawText, bool decodeEntities, string? id, string? settings)
    {
        var start = ToTime(m, "1");
        var end = ToTime(m, "2");
        var text = SubtitleText.TidyLines(SubtitleText.FromHtmlish(rawText, decodeEntities));
        if (text.Length == 0) return; // placeholder cue with nothing to show

        if (end < start)
        {
            warnings.Add($"A cue at {FormatForLog(start)} ends before it starts; its end was set to its start.");
            end = start;
        }
        cues.Add(new SubtitleCue(start, end, text, id, settings));
    }

    private static TimeSpan ToTime(Match m, string suffix)
    {
        long hours = m.Groups["h" + suffix].Success ? long.Parse(m.Groups["h" + suffix].Value, CultureInfo.InvariantCulture) : 0;
        long minutes = long.Parse(m.Groups["m" + suffix].Value, CultureInfo.InvariantCulture);
        long seconds = long.Parse(m.Groups["s" + suffix].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups["f" + suffix].Success ? FractionToMs(m.Groups["f" + suffix].Value) : 0;
        return TimeSpan.FromMilliseconds(((hours * 60 + minutes) * 60 + seconds) * 1000 + fraction);
    }

    /// <summary>"5" is 500 ms, "50" is 500 ms, "500" is 500 ms, "5000" is 500 ms: the digits are a decimal fraction of a second.</summary>
    internal static int FractionToMs(string digits)
    {
        var three = digits.Length >= 3 ? digits[..3] : digits.PadRight(3, '0');
        return int.Parse(three, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ ASS / SSA

    private static readonly string[] DefaultAssEventFormat =
        ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    public static SubtitleDocument ParseAss(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        var cues = new List<SubtitleCue>();
        string? title = null;
        var section = "";
        string[] format = DefaultAssEventFormat;

        foreach (var raw in normalizedText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.ToLowerInvariant();
                continue;
            }

            if (section == "[script info]")
            {
                if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) title = line[6..].Trim();
                continue;
            }

            if (section != "[events]") continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line[7..].Split(',').Select(f => f.Trim()).ToArray();
                // SSA v4 uses "Marked" where ASS has "Layer"; the column position is all we need.
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue; // Comment:, Picture:, Sound: ...

            var textIndex = Array.FindIndex(format, f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
            var startIndex = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var endIndex = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
            if (textIndex < 0 || startIndex < 0 || endIndex < 0)
            {
                warnings.Add("The [Events] Format line has no Start, End and Text columns.");
                break;
            }

            // Text is the last column and may itself contain commas: split into at most format.Length fields.
            var fields = line[9..].TrimStart().Split(',', format.Length);
            if (fields.Length < format.Length)
            {
                warnings.Add($"Skipped a malformed Dialogue line: \"{Shorten(line)}\".");
                continue;
            }

            if (!TryAssTime(fields[startIndex], out var start) || !TryAssTime(fields[endIndex], out var end))
            {
                warnings.Add($"Skipped a Dialogue line with an unreadable time: \"{Shorten(line)}\".");
                continue;
            }

            var text = SubtitleText.TidyLines(SubtitleText.FromAss(fields[textIndex]));
            if (text.Length == 0) continue; // empty or a vector drawing

            string? Field(string name)
            {
                var idx = Array.FindIndex(format, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
                return idx >= 0 && idx < fields.Length && fields[idx].Trim().Length > 0 ? fields[idx].Trim() : null;
            }

            if (end < start)
            {
                warnings.Add($"A cue at {FormatForLog(start)} ends before it starts; its end was set to its start.");
                end = start;
            }
            cues.Add(new SubtitleCue(start, end, text, Style: Field("Style"), Actor: Field("Name")));
        }

        // ASS events are not required to be in time order; players sort them, so do we (stable).
        var ordered = cues.Select((c, i) => (c, i)).OrderBy(x => x.c.Start).ThenBy(x => x.i).Select(x => x.c).ToList();
        return new SubtitleDocument(ordered, SubtitleFormat.Ass, newLine, warnings, title);
    }

    private static bool TryAssTime(string text, out TimeSpan time)
    {
        time = default;
        var m = AssTime().Match(text.Trim());
        if (!m.Success) return false;
        var hours = long.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = long.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var frac = m.Groups["f"].Success ? FractionToMs(m.Groups["f"].Value) : 0;
        time = TimeSpan.FromMilliseconds(((hours * 60 + minutes) * 60 + seconds) * 1000 + frac);
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..60] + "...";

    internal static string FormatForLog(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    [GeneratedRegex(@"^\s*\d+\s*$")]
    private static partial Regex IndexLine();

    [GeneratedRegex(@"^(?<h>\d+):(?<m>\d{1,2}):(?<s>\d{1,2})(?:[.:,](?<f>\d+))?$")]
    private static partial Regex AssTime();
}
