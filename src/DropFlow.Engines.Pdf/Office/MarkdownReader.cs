using System.Text.RegularExpressions;
using DropFlow.Engines.Pdf.Infrastructure;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>Markdown (CommonMark plus tables, task lists, autolinks) parsed with Markdig into the common rich model.</summary>
internal static partial class MarkdownReader
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    public static RichDocument Read(string path) => Parse(TextFile.Read(path));

    public static RichDocument Parse(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var blocks = new List<RichBlock>();
        foreach (var block in document) Convert(block, blocks, depth: 0, quote: false);
        var title = blocks.OfType<RHeading>().FirstOrDefault(h => h.Level == 1);
        return new RichDocument(title is null ? null : RichText.Plain(title.Runs).Trim(), blocks);
    }

    private static void Convert(Block block, List<RichBlock> output, int depth, bool quote)
    {
        switch (block)
        {
            case HeadingBlock heading:
                output.Add(new RHeading(Math.Clamp(heading.Level, 1, 6), InlineRuns(heading.Inline)));
                break;

            case ParagraphBlock paragraph:
                output.Add(new RParagraph(InlineRuns(paragraph.Inline), quote));
                break;

            case ListBlock list:
                ConvertList(list, output, depth);
                break;

            case FencedCodeBlock fenced:
                output.Add(new RCode(Lines(fenced), string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info));
                break;

            case CodeBlock code:
                output.Add(new RCode(Lines(code)));
                break;

            case QuoteBlock quoted:
                foreach (var child in quoted) Convert(child, output, depth, quote: true);
                break;

            case Table table:
                output.Add(ConvertTable(table));
                break;

            case ThematicBreakBlock:
                output.Add(new RRule());
                break;

            case HtmlBlock html:
                var text = Tag().Replace(Lines(html), "").Trim();
                if (text.Length > 0) output.Add(new RParagraph([new Run(text)], quote));
                break;

            case ContainerBlock container:
                foreach (var child in container) Convert(child, output, depth, quote);
                break;
        }
    }

    private static void ConvertList(ListBlock list, List<RichBlock> output, int depth)
    {
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            var first = true;
            foreach (var part in item)
            {
                switch (part)
                {
                    case ParagraphBlock paragraph when first:
                        output.Add(new RListItem(list.IsOrdered, depth, number, InlineRuns(paragraph.Inline)));
                        first = false;
                        break;
                    case ListBlock nested:
                        if (first) { output.Add(new RListItem(list.IsOrdered, depth, number, [])); first = false; }
                        ConvertList(nested, output, depth + 1);
                        break;
                    default:
                        if (first) { output.Add(new RListItem(list.IsOrdered, depth, number, [])); first = false; }
                        Convert(part, output, depth, quote: false);
                        break;
                }
            }
            if (first) output.Add(new RListItem(list.IsOrdered, depth, number, []));
            number++;
        }
    }

    private static RTable ConvertTable(Table table)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<Run>>>();
        foreach (var rowBlock in table)
        {
            if (rowBlock is not TableRow row) continue;
            var cells = new List<IReadOnlyList<Run>>();
            foreach (var cellBlock in row)
            {
                if (cellBlock is not TableCell cell) continue;
                var runs = new List<Run>();
                foreach (var part in cell)
                {
                    if (part is not ParagraphBlock p) continue;
                    if (runs.Count > 0) runs.Add(Run.LineBreak);
                    runs.AddRange(InlineRuns(p.Inline));
                }
                cells.Add(runs);
            }
            rows.Add(cells);
        }
        return new RTable(rows);
    }

    private static string Lines(LeafBlock block)
    {
        var lines = block.Lines;
        var text = new List<string>();
        for (var i = 0; i < lines.Count; i++) text.Add(lines.Lines[i].Slice.ToString());
        return string.Join("\n", text);
    }

    // ---- inlines --------------------------------------------------------------------------------------------------------

    private static List<Run> InlineRuns(ContainerInline? container)
    {
        var runs = new List<Run>();
        if (container is not null) Walk(container, bold: false, italic: false, href: null, runs);
        return Merge(runs);
    }

    private static void Walk(ContainerInline container, bool bold, bool italic, string? href, List<Run> runs)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    runs.Add(new Run(literal.Content.ToString(), bold, italic, Href: href));
                    break;
                case CodeInline code:
                    runs.Add(new Run(code.Content, bold, italic, Code: true, Href: href));
                    break;
                case LineBreakInline lineBreak:
                    runs.Add(lineBreak.IsHard ? Run.LineBreak : new Run(" ", bold, italic, Href: href));
                    break;
                case AutolinkInline auto:
                    runs.Add(new Run(auto.Url, bold, italic, Href: auto.Url));
                    break;
                case HtmlInline html:
                    if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) runs.Add(Run.LineBreak);
                    break;
                case TaskList task:
                    runs.Add(new Run(task.Checked ? "[x] " : "[ ] ", bold, italic));
                    break;
                case LinkInline link:
                    Walk(link, bold, italic, link.IsImage ? href : link.Url, runs);
                    break;
                case EmphasisInline emphasis:
                    var strong = emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2;
                    var em = emphasis.DelimiterChar is '*' or '_' && (emphasis.DelimiterCount == 1 || emphasis.DelimiterCount >= 3);
                    Walk(emphasis, bold || strong, italic || em, href, runs);
                    break;
                case ContainerInline other:
                    Walk(other, bold, italic, href, runs);
                    break;
            }
        }
    }

    /// <summary>Joins neighbouring runs that carry the same formatting and drops empty ones.</summary>
    internal static List<Run> Merge(IEnumerable<Run> runs)
    {
        var result = new List<Run>();
        foreach (var run in runs)
        {
            if (!run.Break && run.Text.Length == 0) continue;
            if (result.Count > 0 && !run.Break && !result[^1].Break
                && result[^1].Bold == run.Bold && result[^1].Italic == run.Italic && result[^1].Code == run.Code && result[^1].Href == run.Href)
            {
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            }
            else
            {
                result.Add(run);
            }
        }
        return result;
    }
}
