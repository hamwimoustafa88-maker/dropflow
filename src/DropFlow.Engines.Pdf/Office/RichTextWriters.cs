using System.Net;
using System.Text;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>Plain text from the rich model: structure is kept as simple text (list markers, tab-separated tables).</summary>
internal static class RichTextWriter
{
    public static string Write(RichDocument document)
    {
        var sb = new StringBuilder();
        var blocks = document.Blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0) sb.Append(RichText.ContinuesList(blocks, i) ? "\n" : "\n\n");
            switch (blocks[i])
            {
                case RHeading h: sb.Append(RichText.Plain(h.Runs).Trim()); break;
                case RParagraph p: sb.Append(RichText.Plain(p.Runs)); break;
                case RListItem item:
                    sb.Append(new string(' ', item.Depth * 4));
                    sb.Append(item.Ordered ? $"{item.Number}. " : "- ");
                    sb.Append(RichText.Plain(item.Runs).Replace("\n", "\n" + new string(' ', item.Depth * 4 + 3)));
                    break;
                case RTable t:
                    sb.Append(string.Join("\n", t.Rows.Select(r => string.Join("\t", r.Select(c => RichText.Plain(c).Replace("\n", " "))))));
                    break;
                case RCode c: sb.Append(c.Text); break;
                case RRule: sb.Append("----------"); break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}

internal static class RichMarkdownWriter
{
    public static string Write(RichDocument document)
    {
        var sb = new StringBuilder();
        var blocks = document.Blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0) sb.Append(RichText.ContinuesList(blocks, i) ? "\n" : "\n\n");
            switch (blocks[i])
            {
                case RHeading h:
                    sb.Append(new string('#', Math.Clamp(h.Level, 1, 6))).Append(' ').Append(Inline(h.Runs, "\n", " "));
                    break;
                case RParagraph p:
                    var text = Inline(p.Runs, "  \n");
                    sb.Append(p.Quote ? string.Join("\n", text.Split('\n').Select(l => "> " + l)) : text);
                    break;
                case RListItem item:
                    var indent = new string(' ', item.Depth * 4);
                    sb.Append(indent).Append(item.Ordered ? $"{item.Number}. " : "- ");
                    sb.Append(Inline(item.Runs, "  \n").Replace("\n", "\n" + indent + "   "));
                    break;
                case RTable t:
                    sb.Append(Table(t));
                    break;
                case RCode c:
                    var fence = c.Text.Contains("```") ? "~~~~" : "```";
                    sb.Append(fence).Append(c.Language ?? "").Append('\n').Append(c.Text).Append('\n').Append(fence);
                    break;
                case RRule:
                    sb.Append("---");
                    break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Table(RTable table)
    {
        var rows = table.Rows;
        if (rows.Count == 0) return "";
        var columns = rows.Max(r => r.Count);
        // The header row is already bold in a Markdown table, so bold inside it is not repeated.
        string Row(IReadOnlyList<IReadOnlyList<Run>> cells, bool header = false) =>
            "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(c =>
                c < cells.Count ? Inline(header ? cells[c].Select(x => x with { Bold = false }) : cells[c], "<br>").Replace("|", "\\|") : "")) + " |";

        var sb = new StringBuilder(Row(rows[0], header: true)).Append('\n');
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columns))).Append(" |");
        foreach (var row in rows.Skip(1)) sb.Append('\n').Append(Row(row));
        return sb.ToString();
    }

    /// <summary>Runs as Markdown inline syntax. Spaces at the edges of a formatted run are moved outside the markers (CommonMark requires it).</summary>
    internal static string Inline(IEnumerable<Run> runs, string lineBreak, string? headingBreak = null)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.Break)
            {
                sb.Append(headingBreak ?? lineBreak);
                continue;
            }
            if (run.Text.Length == 0) continue;

            var body = run.Code ? run.Text : Escape(run.Text);
            var leading = body.Length - body.TrimStart().Length;
            var trailing = body.Length - body.TrimEnd().Length;
            if (leading == body.Length) { sb.Append(body); continue; } // only whitespace
            var core = body.Trim();
            sb.Append(body, 0, leading);

            if (run.Code)
            {
                var fence = core.Contains('`') ? "``" : "`";
                core = fence + (core.StartsWith('`') || core.EndsWith('`') ? " " + core + " " : core) + fence;
            }
            if (run.Bold && run.Italic) core = "***" + core + "***";
            else if (run.Bold) core = "**" + core + "**";
            else if (run.Italic) core = "*" + core + "*";
            if (run.Href is { Length: > 0 } href) core = $"[{core}]({href.Replace(" ", "%20").Replace(")", "%29")})";

            sb.Append(core);
            sb.Append(body, body.Length - trailing, trailing);
        }
        return sb.ToString();
    }

    /// <summary>Backslash-escapes characters that would be read as Markdown syntax inside running text.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\\' or '*' or '`' or '[' or ']' or '<' or '>' or '_' or '|') sb.Append('\\');
            else if (i == 0 && c is '#' or '+' or '-') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

internal static class RichHtmlWriter
{
    private static readonly string[] SafeSchemes = ["http", "https", "mailto", "tel", "ftp"];

    public static string Write(RichDocument document, string title, string language)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\" dir=\"auto\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n");
        sb.Append("body{font-family:system-ui,'Segoe UI',Arial,sans-serif;line-height:1.6;max-width:46rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a}\n");
        sb.Append("table{border-collapse:collapse;margin:1rem 0}th,td{border:1px solid #999;padding:.3rem .6rem}th{background:#f0f0f0}\n");
        sb.Append("pre{background:#f4f4f4;padding:.8rem;overflow:auto}code{font-family:Consolas,'Courier New',monospace}blockquote{border-inline-start:3px solid #bbb;margin-inline:0;padding-inline-start:1rem;color:#444}\n");
        sb.Append("</style>\n</head>\n<body>\n");
        AppendBlocks(sb, document.Blocks);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    internal static void AppendBlocks(StringBuilder sb, IReadOnlyList<RichBlock> blocks)
    {
        var open = new Stack<string>(); // currently open list tags, innermost last
        void CloseLists(int keep)
        {
            while (open.Count > keep) sb.Append("</li></").Append(open.Pop()).Append(">\n");
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not RListItem) CloseLists(0);

            switch (blocks[i])
            {
                case RHeading h:
                    var level = Math.Clamp(h.Level, 1, 6);
                    sb.Append($"<h{level} dir=\"auto\">").Append(Inline(h.Runs)).Append($"</h{level}>\n");
                    break;
                case RParagraph p:
                    sb.Append(p.Quote ? "<blockquote><p dir=\"auto\">" : "<p dir=\"auto\">").Append(Inline(p.Runs)).Append(p.Quote ? "</p></blockquote>\n" : "</p>\n");
                    break;
                case RListItem item:
                    var tag = item.Ordered ? "ol" : "ul";
                    var wantDepth = item.Depth + 1;
                    while (open.Count > wantDepth) sb.Append("</li></").Append(open.Pop()).Append(">\n");
                    if (open.Count == wantDepth)
                    {
                        if (open.Peek() == tag)
                        {
                            sb.Append("</li>\n"); // next sibling in the same list
                        }
                        else
                        {
                            sb.Append("</li></").Append(open.Pop()).Append(">\n"); // the kind of list changed: close it and open the other kind
                        }
                    }
                    while (open.Count < wantDepth)
                    {
                        var last = open.Count + 1 == wantDepth;
                        sb.Append('<').Append(tag).Append(" dir=\"auto\"");
                        if (last && item.Ordered && item.Number != 1) sb.Append($" start=\"{item.Number}\"");
                        sb.Append(">\n");
                        open.Push(tag);
                        if (!last) sb.Append("<li>"); // a nested list whose parent items are missing gets an empty parent
                    }
                    sb.Append("<li>").Append(Inline(item.Runs));
                    break;
                case RTable t:
                    AppendTable(sb, t);
                    break;
                case RCode c:
                    sb.Append("<pre><code");
                    if (c.Language is { Length: > 0 } lang) sb.Append(" class=\"language-").Append(WebUtility.HtmlEncode(lang.Split(' ')[0])).Append('"');
                    sb.Append('>').Append(WebUtility.HtmlEncode(c.Text)).Append("</code></pre>\n");
                    break;
                case RRule:
                    sb.Append("<hr>\n");
                    break;
            }
        }
        CloseLists(0);
    }

    private static void AppendTable(StringBuilder sb, RTable table)
    {
        sb.Append("<table dir=\"auto\">\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var tag = r == 0 ? "th" : "td";
            if (r == 0) sb.Append("<thead>\n");
            else if (r == 1) sb.Append("<tbody>\n");
            sb.Append("<tr>");
            foreach (var cell in table.Rows[r]) sb.Append('<').Append(tag).Append('>').Append(Inline(r == 0 ? cell.Select(x => x with { Bold = false }) : cell)).Append("</").Append(tag).Append('>');
            sb.Append("</tr>\n");
            if (r == 0) sb.Append("</thead>\n");
        }
        if (table.Rows.Count > 1) sb.Append("</tbody>\n");
        sb.Append("</table>\n");
    }

    internal static string Inline(IEnumerable<Run> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.Break) { sb.Append("<br>"); continue; }
            var text = WebUtility.HtmlEncode(run.Text).Replace("\t", "&emsp;");
            if (run.Code) text = $"<code>{text}</code>";
            if (run.Bold) text = $"<strong>{text}</strong>";
            if (run.Italic) text = $"<em>{text}</em>";
            if (SafeHref(run.Href) is { } href) text = $"<a href=\"{WebUtility.HtmlEncode(href)}\">{text}</a>";
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>Only ordinary link schemes are written, so a document cannot smuggle a javascript: link into the HTML.</summary>
    private static string? SafeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var trimmed = href.Trim();
        if (trimmed.StartsWith('#') || trimmed.StartsWith('/') || trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal)) return trimmed;
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return trimmed; // relative path
        return SafeSchemes.Contains(trimmed[..colon], StringComparer.OrdinalIgnoreCase) ? trimmed : null;
    }
}
