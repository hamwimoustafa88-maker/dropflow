using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Pdf.Extraction;

internal static class PlainTextWriter
{
    public static string BlockText(Block block) => block switch
    {
        HeadingBlock h => h.Text,
        ParagraphBlock p => p.Text,
        ListItemBlock l => $"{BulletOrNumber(l)} {l.Text}",
        TableBlock t => string.Join("\n", t.Rows.Select(r => string.Join("\t", r))),
        _ => "",
    };

    public static string Write(PdfTextDocument document)
    {
        var sb = new StringBuilder();
        foreach (var page in document.Pages)
        {
            var blocks = page.Blocks.ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (sb.Length > 0) sb.Append(SameList(blocks, i) ? "\n" : "\n\n");
                sb.Append(BlockText(blocks[i]));
            }
        }
        return sb.ToString().Trim() + "\n";
    }

    /// <summary>True when block <paramref name="i"/> continues the list that block i-1 belongs to (same kind of list).</summary>
    internal static bool SameList(IReadOnlyList<Block> blocks, int i) =>
        i > 0 && blocks[i] is ListItemBlock current && blocks[i - 1] is ListItemBlock previous && current.Ordered == previous.Ordered;

    private static string BulletOrNumber(ListItemBlock item) =>
        item.Ordered ? item.Marker : item.Marker.Length == 1 && item.Marker[0] is '-' or '*' or '+' or '\u2022' ? item.Marker : "\u2022";
}

internal static partial class MarkdownWriter
{
    [GeneratedRegex(@"^(\d{1,3})")]
    private static partial Regex LeadingNumber();

    public static string Write(PdfTextDocument document)
    {
        var sb = new StringBuilder();
        var firstPage = true;
        foreach (var page in document.Pages)
        {
            if (page.Blocks.Count == 0) continue;
            if (!firstPage) sb.Append("\n\n---\n\n");
            firstPage = false;

            var blocks = page.Blocks.ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (i > 0) sb.Append(PlainTextWriter.SameList(blocks, i) ? "\n" : "\n\n");
                sb.Append(BlockMarkdown(blocks[i]));
            }
        }
        return sb.ToString().Trim() + "\n";
    }

    private static string BlockMarkdown(Block block) => block switch
    {
        HeadingBlock h => new string('#', Math.Clamp(h.Level, 1, 6)) + " " + Escape(h.Text),
        ParagraphBlock p => Escape(p.Text),
        ListItemBlock l => (l.Ordered ? OrderedPrefix(l.Marker) : "-") + " " + Escape(l.Text),
        TableBlock t => Table(t),
        _ => "",
    };

    private static string OrderedPrefix(string marker)
    {
        var match = LeadingNumber().Match(marker.TrimStart('('));
        return (match.Success ? match.Groups[1].Value : "1") + ".";
    }

    private static string Table(TableBlock table)
    {
        var rows = table.Rows;
        var columns = rows.Max(r => r.Count);
        string Row(IReadOnlyList<string> r) =>
            "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => i < r.Count ? Cell(r[i]) : "")) + " |";

        var sb = new StringBuilder();
        sb.Append(Row(rows[0])).Append('\n');
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columns))).Append(" |");
        foreach (var row in rows.Skip(1)) sb.Append('\n').Append(Row(row));
        return sb.ToString();
    }

    private static string Cell(string text) => Escape(text).Replace("|", "\\|").Replace("\n", " ");

    /// <summary>Escapes the characters that would be read as Markdown syntax in running text.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\\' or '*' or '`' or '[' or ']' or '<' or '>' or '_') sb.Append('\\');
            else if (i == 0 && c is '#' or '+' or '-') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

internal static class HtmlWriter
{
    public static string Write(PdfTextDocument document, string title, string language)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\" dir=\"auto\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"generator\" content=\"DropFlow\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        sb.Append("body{font-family:system-ui,'Segoe UI',Arial,sans-serif;line-height:1.6;max-width:46rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a}\n");
        sb.Append("table{border-collapse:collapse;margin:1rem 0}td,th{border:1px solid #999;padding:.3rem .6rem;vertical-align:top}\n");
        sb.Append("section.page{margin-bottom:2rem;padding-bottom:1rem;border-bottom:1px solid #ddd}\n</style>\n</head>\n<body>\n");

        foreach (var page in document.Pages)
        {
            if (page.Blocks.Count == 0) continue;
            sb.Append("<section class=\"page\" id=\"page-").Append(page.Number).Append("\">\n");
            AppendBlocks(sb, page.Blocks);
            sb.Append("</section>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Block elements only (no document wrapper); shared with the EPUB chapters.</summary>
    internal static void AppendBlocks(StringBuilder sb, IEnumerable<Block> blocks)
    {
        var list = blocks.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case HeadingBlock h:
                    var level = Math.Clamp(h.Level, 1, 6);
                    sb.Append($"<h{level} dir=\"auto\">").Append(WebUtility.HtmlEncode(h.Text)).Append($"</h{level}>\n");
                    break;
                case ParagraphBlock p:
                    sb.Append("<p dir=\"auto\">").Append(WebUtility.HtmlEncode(p.Text)).Append("</p>\n");
                    break;
                case ListItemBlock first:
                    var tag = first.Ordered ? "ol" : "ul";
                    sb.Append('<').Append(tag).Append(" dir=\"auto\">\n");
                    while (i < list.Count && list[i] is ListItemBlock item && item.Ordered == first.Ordered)
                    {
                        sb.Append("<li>").Append(WebUtility.HtmlEncode(item.Text)).Append("</li>\n");
                        i++;
                    }
                    i--;
                    sb.Append("</").Append(tag).Append(">\n");
                    break;
                case TableBlock t:
                    sb.Append("<table dir=\"auto\">\n");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row) sb.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
                        sb.Append("</tr>\n");
                    }
                    sb.Append("</table>\n");
                    break;
            }
        }
    }
}
