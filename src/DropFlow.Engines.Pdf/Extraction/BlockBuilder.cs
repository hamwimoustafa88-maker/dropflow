using System.Text.RegularExpressions;

namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>Turns the lines of one page into tables, headings, list items and paragraphs using font size, spacing and alignment.</summary>
internal static partial class BlockBuilder
{
    // "1." "2)" "a)" "(3)" then text
    [GeneratedRegex(@"^(?<marker>(\d{1,3}|\p{Nd}{1,3}|[a-zA-Z](?=\)))[.)]|\((\d{1,3}|[a-zA-Z])\))\s+(?<text>\S.*)$")]
    private static partial Regex OrderedMarker();

    // bullets, including the private-use code points that Symbol/Wingdings bullets are mapped to
    [GeneratedRegex(@"^(?<marker>[•◦▪▫●○■□‣⁃∙·–—\-\*\+-])\s+(?<text>\S.*)$")]
    private static partial Regex BulletMarker();

    public static IReadOnlyList<Block> Build(IReadOnlyList<TextLine> lines, double bodySize, IReadOnlyList<double> headingSizes)
    {
        var blocks = new List<Block>();
        var items = TableDetector.Group(lines);

        TextLine? previousLine = null;
        var paragraph = new ParagraphBuilder();
        ListItemBuilder? listItem = null;
        HeadingBuilder? heading = null;

        void Flush()
        {
            if (heading is not null) { blocks.Add(heading.Build()); heading = null; }
            if (listItem is not null) { blocks.Add(listItem.Build()); listItem = null; }
            if (paragraph.HasText) blocks.Add(paragraph.Build());
            paragraph = new ParagraphBuilder();
            previousLine = null;
        }

        foreach (var item in items)
        {
            if (item.Table is not null)
            {
                Flush();
                blocks.Add(item.Table);
                continue;
            }

            var line = item.Line!;
            var gap = previousLine is null ? double.MaxValue : previousLine.BaseY - line.BaseY;
            var closeToPrevious = previousLine is not null && gap <= 1.75 * Math.Max(line.FontSize, previousLine.FontSize);

            var level = HeadingLevel(line, bodySize, headingSizes);
            if (level > 0)
            {
                if (heading is not null && heading.Level == level && closeToPrevious)
                {
                    heading.Append(line);
                }
                else
                {
                    Flush();
                    heading = new HeadingBuilder(level, line);
                }
                previousLine = line;
                continue;
            }

            if (heading is not null) Flush();

            var marker = MatchListMarker(line.Text);
            if (marker is not null)
            {
                Flush();
                listItem = new ListItemBuilder(marker.Value.Ordered, marker.Value.Marker, marker.Value.Text, line);
                previousLine = line;
                continue;
            }

            if (listItem is not null)
            {
                var indent = line.Rtl ? listItem.Line.Right - line.Right : line.Left - listItem.Line.Left;
                if (closeToPrevious && indent >= 0.5 * line.FontSize)
                {
                    listItem.Append(line.Text);
                    previousLine = line;
                    continue;
                }
                Flush();
            }

            var sameParagraph = paragraph.HasText && closeToPrevious && Math.Abs(previousLine!.FontSize - line.FontSize) <= 0.8;
            if (!sameParagraph)
            {
                if (paragraph.HasText) { blocks.Add(paragraph.Build()); paragraph = new ParagraphBuilder(); }
            }
            paragraph.Append(line);
            previousLine = line;
        }

        Flush();
        return blocks;
    }

    private static int HeadingLevel(TextLine line, double body, IReadOnlyList<double> headingSizes)
    {
        if (line.Text.Length > 160 || headingSizes.Count == 0) return 0;
        if (line.FontSize < body * 1.15 || line.FontSize - body < 1) return 0;
        var rounded = PdfTextExtractor.Round(line.FontSize);
        var index = headingSizes.ToList().IndexOf(rounded);
        return index < 0 ? 0 : Math.Min(index + 1, 6);
    }

    private static (bool Ordered, string Marker, string Text)? MatchListMarker(string text)
    {
        var bullet = BulletMarker().Match(text);
        if (bullet.Success) return (false, bullet.Groups["marker"].Value, bullet.Groups["text"].Value);
        var ordered = OrderedMarker().Match(text);
        if (ordered.Success) return (true, ordered.Groups["marker"].Value, ordered.Groups["text"].Value);
        return null;
    }

    // ---- small builders -------------------------------------------------------------------------------------------------

    private sealed class ParagraphBuilder
    {
        private readonly List<string> _lines = [];
        private bool _rtl;

        public bool HasText => _lines.Count > 0;

        public void Append(TextLine line)
        {
            _lines.Add(line.Text);
            _rtl = line.Rtl;
        }

        public ParagraphBlock Build() => new(Join(_lines), _rtl);
    }

    private sealed class ListItemBuilder(bool ordered, string marker, string text, TextLine line)
    {
        private readonly List<string> _lines = [text];

        public TextLine Line { get; } = line;

        public void Append(string more) => _lines.Add(more);

        public ListItemBlock Build() => new(ordered, marker, Join(_lines), Line.Rtl);
    }

    private sealed class HeadingBuilder(int level, TextLine first)
    {
        private readonly List<string> _lines = [first.Text];

        public int Level { get; } = level;

        public void Append(TextLine line) => _lines.Add(line.Text);

        public HeadingBlock Build() => new(Level, string.Join(" ", _lines), first.Rtl);
    }

    /// <summary>Joins wrapped lines: a trailing hyphen followed by a lower-case word is a split word, everything else gets a space.</summary>
    internal static string Join(IReadOnlyList<string> lines)
    {
        var result = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (result.Length == 0) { result.Append(line); continue; }
            var last = result[^1];
            var splitWord = last == '-' && result.Length > 1 && char.IsLetter(result[^2]) && line.Length > 0 && char.IsLower(line[0]);
            if (splitWord) result.Length--;
            else result.Append(' ');
            result.Append(line);
        }
        return result.ToString();
    }
}
