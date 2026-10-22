using System.Text.RegularExpressions;

namespace DropFlow.AI.Translation.Formats;

/// <summary>
/// Markdown: only the words are translated. Code (fenced, indented and inline), front matter, link and image targets, reference
/// definitions, HTML tags, emphasis markers, heading and list markers, quote marks and table structure are copied unchanged.
/// A paragraph that was soft-wrapped over several lines is translated as one unit and written back as one line (rendering is the same).
/// </summary>
internal static partial class MarkdownParser
{
    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"^\s*\|?\s*:?-{1,}:?\s*(?:\|\s*:?-{1,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s{0,3}=+\s*$")]
    private static partial Regex SetextEquals();

    [GeneratedRegex(@"^\s{0,3}\[[^\]]+\]:\s*\S+")]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"^(\s{0,3}#{1,6}[ \t]+)(.*?)([ \t]+#+[ \t]*)?$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*(?:>[ \t]?)+)(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*(?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellBar();

    private sealed class Paragraph
    {
        public string FirstPrefix = "";
        public string Trailing = "";
        public readonly List<string> Lines = [];
        public bool Open => Lines.Count > 0;
    }

    public static TranslationTemplate Parse(string text)
    {
        var lines = text.Split('\n');
        var parts = new List<Part>();
        var paragraph = new Paragraph();

        void Newline(int index)
        {
            if (index < lines.Length - 1) parts.Add(Part.Keep("\n"));
        }

        void Flush(int index)
        {
            if (!paragraph.Open) return;
            parts.Add(Part.Keep(paragraph.FirstPrefix));
            var joined = string.Join(' ', paragraph.Lines.Select(l => l.Trim()).Where(l => l.Length > 0));
            AddInline(joined, parts);
            if (paragraph.Trailing.Length > 0) parts.Add(Part.Keep(paragraph.Trailing));
            paragraph.Lines.Clear();
            paragraph.FirstPrefix = "";
            paragraph.Trailing = "";
            Newline(index - 1);       // the line break that ended the paragraph
        }

        var i = 0;
        // Front matter: a "---" first line up to the closing "---" or "...".
        if (lines.Length > 0 && lines[0].TrimEnd() == "---")
        {
            var close = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
            if (close > 0)
            {
                for (; i <= close; i++) { parts.Add(Part.Keep(lines[i])); Newline(i); }
            }
        }

        var previousBlank = true;
        var lastWasList = false;
        string? fence = null;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (fence is not null)
            {
                parts.Add(Part.Keep(lines[i]));
                Newline(i);
                if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal) && line.Trim().Trim(fence[0]).Length == 0) fence = null;
                continue;
            }

            if (FenceStart().Match(line) is { Success: true } f)
            {
                Flush(i);
                fence = f.Groups[1].Value;
                parts.Add(Part.Keep(lines[i]));
                Newline(i);
                previousBlank = false;
                continue;
            }

            if (line.Trim().Length == 0)
            {
                Flush(i);
                parts.Add(Part.Keep(lines[i]));
                Newline(i);
                previousBlank = true;
                lastWasList = false;
                continue;
            }

            var indentedCode = (line.StartsWith('\t') || line.StartsWith("    ", StringComparison.Ordinal)) && previousBlank && !lastWasList && !paragraph.Open;
            if (indentedCode || ThematicBreak().IsMatch(line) || ReferenceDefinition().IsMatch(line) || SetextEquals().IsMatch(line)
                || (TableSeparator().IsMatch(line) && line.Contains('-') && (line.Contains('|') || paragraph.Open || previousBlank)))
            {
                // A "---" or "===" line right under text is a heading underline; a table separator row is structure.
                Flush(i);
                parts.Add(Part.Keep(lines[i]));
                Newline(i);
                previousBlank = false;
                continue;
            }

            if (Heading().Match(line) is { Success: true } h)
            {
                Flush(i);
                parts.Add(Part.Keep(h.Groups[1].Value));
                AddInline(h.Groups[2].Value, parts);
                if (h.Groups[3].Success) parts.Add(Part.Keep(h.Groups[3].Value));
                Newline(i);
                previousBlank = false;
                lastWasList = false;
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                Flush(i);
                AddTableRow(line, parts);
                Newline(i);
                previousBlank = false;
                continue;
            }

            // Block prefixes: quote marks and list markers start a new paragraph; a plain line continues the open one.
            var rest = line;
            var prefix = "";
            var startsBlock = false;
            if (Quote().Match(rest) is { Success: true } q) { prefix = q.Groups[1].Value; rest = q.Groups[2].Value; startsBlock = true; }
            if (ListItem().Match(rest) is { Success: true } li) { prefix += li.Groups[1].Value; rest = li.Groups[2].Value; startsBlock = true; lastWasList = true; }

            if (!startsBlock && !char.IsWhiteSpace(line[0])) lastWasList = false;

            // A hard line break (two trailing spaces or a backslash) ends the paragraph and must stay in the output.
            var core = rest.TrimEnd();
            var hardBreak = rest.Length - core.Length >= 2 || core.EndsWith('\\');
            var trailing = "";
            if (hardBreak)
            {
                if (core.EndsWith('\\')) core = core[..^1];
                trailing = rest[core.Length..];
                rest = core;
            }

            if (startsBlock || !paragraph.Open)
            {
                Flush(i);
                paragraph.FirstPrefix = prefix.Length > 0 ? prefix : line[..(line.Length - line.TrimStart().Length)];
                paragraph.Lines.Add(prefix.Length > 0 ? rest : rest.TrimStart());
            }
            else
            {
                paragraph.Lines.Add(rest);
            }
            previousBlank = false;

            if (hardBreak)
            {
                paragraph.Trailing = trailing;
                Flush(i + 1);
            }
        }
        Flush(lines.Length);
        return new TranslationTemplate(parts);
    }

    private static void AddTableRow(string line, List<Part> parts)
    {
        var pieces = CellBar().Split(line);
        for (var c = 0; c < pieces.Length; c++)
        {
            if (c > 0) parts.Add(Part.Keep("|"));
            AddInline(pieces[c], parts);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ inline

    // Everything that must survive untouched, as alternatives. Links and images are handled with groups so their text is translated.
    [GeneratedRegex(
        @"(?<image>!\[)(?<alt>[^\]\n]*)(?<imageTarget>\]\([^)\n]*\))"
        + @"|(?<link>\[)(?<label>[^\]\n]+)(?<linkTarget>\](?:\([^)\n]*\)|\[[^\]\n]*\]))"
        + @"|(?<code>(?<bt>`+)[^`\n]+?\k<bt>|`+)"
        + @"|(?<tag><!--.*?-->|</?[A-Za-z][^>\n]*>|<(?:https?://|mailto:)[^>\n]+>)"
        + @"|(?<footnote>\[\^[^\]\n]+\])"
        + @"|(?<entity>&(?:#\d+|#x[0-9A-Fa-f]+|\w+);)"
        + @"|(?<escape>\\[\\`*_{}\[\]()#+\-.!|>~])"
        + @"|(?<url>https?://[^\s<>]*[^\s<>.,;:!?)\]])"
        + @"|(?<mark>\*+|~~|(?<![\w])_{1,3}(?=\S)|(?<=\S)_{1,3}(?![\w]))",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex InlineToken();

    /// <summary>Splits one line (or joined paragraph) into kept syntax and translatable words.</summary>
    internal static void AddInline(string text, List<Part> parts)
    {
        var position = 0;
        void Words(string s)
        {
            if (s.Length == 0) return;
            if (s.Trim().Length == 0) parts.Add(Part.Keep(s)); else parts.Add(Part.Tr(s));
        }

        foreach (Match m in InlineToken().Matches(text))
        {
            Words(text[position..m.Index]);
            if (m.Groups["image"].Success)
            {
                parts.Add(Part.Keep(m.Groups["image"].Value));
                Words(m.Groups["alt"].Value);
                parts.Add(Part.Keep(m.Groups["imageTarget"].Value));
            }
            else if (m.Groups["link"].Success)
            {
                parts.Add(Part.Keep(m.Groups["link"].Value));
                // The label can hold emphasis or code: split it again.
                AddInline(m.Groups["label"].Value, parts);
                parts.Add(Part.Keep(m.Groups["linkTarget"].Value));
            }
            else
            {
                parts.Add(Part.Keep(m.Value));
            }
            position = m.Index + m.Length;
        }
        Words(text[position..]);
    }
}
