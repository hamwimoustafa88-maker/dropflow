using System.Text.RegularExpressions;

namespace DropFlow.AI.Translation.Formats;

/// <summary>
/// SubRip (.srt) and WebVTT (.vtt): only the words of each cue are translated. Cue numbers, identifiers, timestamps and settings, the WEBVTT header,
/// NOTE/STYLE/REGION blocks, tags such as &lt;i&gt; and {\an8}, and the line count of every cue are kept.
/// </summary>
internal static partial class SubtitleParser
{
    [GeneratedRegex(@"<[^>\n]*>|\{\\[^}\n]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    [GeneratedRegex(@"^\s*[-–—]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerDash();

    public static TranslationTemplate Parse(string text)
    {
        var parts = new List<Part>();
        // Blocks are separated by blank lines; keep the exact separators by walking the lines.
        var lines = text.Split('\n');
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
            var block = lines[start..i];
            var endsFile = i == lines.Length;                 // the block's last line is the last line of the file
            var timing = Array.FindIndex(block, l => l.Contains("-->", StringComparison.Ordinal));

            if (timing < 0)
            {
                // WEBVTT header, NOTE, STYLE, REGION or anything else that is not a cue.
                for (var j = 0; j < block.Length; j++)
                {
                    parts.Add(Part.Keep(block[j]));
                    if (j < block.Length - 1 || !endsFile) parts.Add(Part.Keep("\n"));
                }
                continue;
            }

            for (var j = 0; j <= timing; j++)
            {
                parts.Add(Part.Keep(block[j]));
                if (j < timing || block.Length > timing + 1 || !endsFile) parts.Add(Part.Keep("\n"));
            }
            if (block.Length > timing + 1)
            {
                AddCueText(block[(timing + 1)..], parts);
                if (!endsFile) parts.Add(Part.Keep("\n"));
            }
        }
        return new TranslationTemplate(parts);
    }

    /// <summary>Adds the parts of the text lines of a cue (the lines are separated by line breaks, which the parts keep).</summary>
    private static void AddCueText(string[] textLines, List<Part> parts)
    {
        if (textLines.Length == 0) return;

        // Two speakers: every line starts with a dash. Translate each line alone and keep the dashes.
        if (textLines.Length > 1 && textLines.All(l => SpeakerDash().IsMatch(l)))
        {
            for (var k = 0; k < textLines.Length; k++)
            {
                var m = SpeakerDash().Match(textLines[k]);
                parts.Add(Part.Keep(m.Value));
                AddLineParts(textLines[k][m.Length..], parts);
                if (k < textLines.Length - 1) parts.Add(Part.Keep("\n"));
            }
            return;
        }

        // Markup in the middle of the text (a word in italics) splits it into pieces: keep the original lines.
        var joined = string.Join("\n", textLines);
        var stripped = StripOuterMarkup(joined, out var head, out var tail);
        if (Markup().IsMatch(stripped))
        {
            for (var k = 0; k < textLines.Length; k++)
            {
                AddLineParts(textLines[k], parts);
                if (k < textLines.Length - 1) parts.Add(Part.Keep("\n"));
            }
            return;
        }

        // The common case: plain words, possibly wrapped in one tag pair. One unit for the cue; the line count is restored on output.
        if (head.Length > 0) parts.Add(Part.Keep(head));
        var oneLine = string.Join(' ', stripped.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (oneLine.Length == 0) parts.Add(Part.Keep(stripped)); else parts.Add(Part.Tr(oneLine, exactLines: textLines.Length));
        if (tail.Length > 0) parts.Add(Part.Keep(tail));
    }

    [GeneratedRegex(@"\A(?:<[^>\n]*>|\{\[^}\n]*\})+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMarkup();

    [GeneratedRegex(@"(?:<[^>\n]*>|\{\[^}\n]*\})+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingMarkup();

    /// <summary>Splits text into leading markup, the middle, and trailing markup (an override tag and &lt;i&gt; ... &lt;/i&gt;).</summary>
    private static string StripOuterMarkup(string text, out string head, out string tail)
    {
        head = LeadingMarkup().Match(text) is { Success: true } lead ? lead.Value : "";
        var rest = text[head.Length..];
        tail = TrailingMarkup().Match(rest) is { Success: true } trail ? trail.Value : "";
        return rest[..^tail.Length];
    }

    private static void AddLineParts(string line, List<Part> parts)
    {
        var position = 0;
        void Words(string s)
        {
            if (s.Length == 0) return;
            if (s.Trim().Length == 0) parts.Add(Part.Keep(s)); else parts.Add(Part.Tr(s));
        }
        foreach (Match m in Markup().Matches(line))
        {
            Words(line[position..m.Index]);
            parts.Add(Part.Keep(m.Value));
            position = m.Index + m.Length;
        }
        Words(line[position..]);
    }
}
