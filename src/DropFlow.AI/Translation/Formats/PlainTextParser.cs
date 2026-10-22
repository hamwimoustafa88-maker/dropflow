using System.Text.RegularExpressions;

namespace DropFlow.AI.Translation.Formats;

/// <summary>
/// Plain text: one unit per line (list markers and indentation are kept). A paragraph that was hard-wrapped (several lines of similar width that
/// stop in the middle of a sentence, as in e-mails and README files) is joined for translation and its translation is wrapped to the same width.
/// </summary>
internal static partial class PlainTextParser
{
    [GeneratedRegex(@"^(\s*(?:[-*+•–—▪●]|\d{1,3}[.)]|\(?[A-Za-z]\)|[A-Za-z][.)])\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"[.!?:;؟؛…""')\]]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();

    public static TranslationTemplate Parse(string text)
    {
        var lines = text.Split('\n');
        var parts = new List<Part>();
        var i = 0;
        while (i < lines.Length)
        {
            if (lines[i].Trim().Length == 0)
            {
                parts.Add(Part.Keep(lines[i]));
                if (i < lines.Length - 1) parts.Add(Part.Keep("\n"));
                i++;
                continue;
            }

            var start = i;
            while (i < lines.Length && lines[i].Trim().Length > 0) i++;
            var paragraph = lines[start..i];
            var endsFile = i == lines.Length;

            if (IsHardWrapped(paragraph)) AddWrapped(paragraph, parts);
            else
            {
                for (var j = 0; j < paragraph.Length; j++)
                {
                    AddLine(paragraph[j], parts);
                    if (j < paragraph.Length - 1) parts.Add(Part.Keep("\n"));
                }
            }
            if (!endsFile) parts.Add(Part.Keep("\n"));
        }
        return new TranslationTemplate(parts);
    }

    private static void AddLine(string line, List<Part> parts)
    {
        var (prefix, rest) = SplitPrefix(line);
        if (prefix.Length > 0) parts.Add(Part.Keep(prefix));
        if (rest.Trim().Length == 0) parts.Add(Part.Keep(rest)); else parts.Add(Part.Tr(rest));
    }

    private static (string Prefix, string Remainder) SplitPrefix(string line)
    {
        var m = ListPrefix().Match(line);
        if (m.Success) return (m.Value, line[m.Length..]);
        var indent = line.Length - line.TrimStart().Length;
        return (line[..indent], line[indent..]);
    }

    /// <summary>
    /// A paragraph is hard-wrapped when it has at least two lines, none of them a list item, and every line but the last is fairly long,
    /// of about the same width as the others, and ends without sentence punctuation (the text simply continues on the next line).
    /// </summary>
    internal static bool IsHardWrapped(string[] paragraph)
    {
        if (paragraph.Length < 2) return false;
        if (paragraph.Any(l => ListPrefix().IsMatch(l))) return false;
        var inner = paragraph.Take(paragraph.Length - 1).Select(l => l.TrimEnd()).ToList();
        var longest = inner.Max(l => l.Length);
        if (longest is < 35 or > 120) return false;
        if (inner.Any(l => l.Length < longest - 18 || SentenceEnd().IsMatch(l))) return false;
        return paragraph[^1].Length <= longest + 4;
    }

    private static void AddWrapped(string[] paragraph, List<Part> parts)
    {
        var (firstPrefix, firstRest) = SplitPrefix(paragraph[0]);
        var continuation = paragraph[1][..(paragraph[1].Length - paragraph[1].TrimStart().Length)];
        var joined = string.Join(' ', new[] { firstRest.Trim() }.Concat(paragraph.Skip(1).Select(l => l.Trim())).Where(s => s.Length > 0));
        var width = paragraph.Max(l => l.TrimEnd().Length);
        if (firstPrefix.Length > 0) parts.Add(Part.Keep(firstPrefix));
        parts.Add(Part.Tr(joined, wrapWidth: width, linePrefix: continuation, firstLineOffset: firstPrefix.Length));
    }
}
