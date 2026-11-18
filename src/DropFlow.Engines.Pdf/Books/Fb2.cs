// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;
using ImageMagick;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>
/// FictionBook 2 (FB2): one XML file with the book's description, its text as nested &lt;section&gt;s of paragraphs, and the pictures as
/// base64 &lt;binary&gt; elements. Reading turns it into the shared book HTML; writing builds FB2 from it.
/// </summary>
internal static class Fb2
{
    public static readonly XNamespace Ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
    public static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    static Fb2() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ---- reading ------------------------------------------------------------------------------------------------------------

    public static EpubBook Read(string path)
    {
        var name = Path.GetFileName(path);
        XDocument document;
        try
        {
            document = Load(File.ReadAllBytes(path), name);
        }
        catch (XmlException ex)
        {
            throw new ConversionFailure($"{name} is not a valid FB2 book ({ex.Message}).");
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "FictionBook") throw new ConversionFailure($"{name} is not an FB2 book (no FictionBook element).");
        var ns = root.Name.Namespace;

        var info = root.Element(ns + "description")?.Element(ns + "title-info");
        var title = Clean(info?.Element(ns + "book-title")?.Value) ?? Path.GetFileNameWithoutExtension(path);
        var authors = info?.Elements(ns + "author").Select(AuthorName).Where(a => a.Length > 0).ToList() ?? [];
        var language = Clean(info?.Element(ns + "lang")?.Value);

        var binaries = root.Elements(ns + "binary")
            .Where(b => b.Attribute("id") is not null)
            .GroupBy(b => b.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary<IGrouping<string, XElement>, string, string?>(g => g.Key, g => Picture(g.First()), StringComparer.Ordinal);

        var reader = new Fb2HtmlReader(binaries);
        var body = new StringBuilder();
        var bodies = root.Elements(ns + "body").ToList();
        var chapter = 0;
        foreach (var (fb2Body, index) in bodies.Select((b, i) => (b, i)))
        {
            var isNotes = index > 0 && fb2Body.Attribute("name") is not null;
            var sections = fb2Body.Elements(ns + "section").ToList();
            var leading = fb2Body.Elements().Where(e => e.Name.LocalName is "title" or "epigraph" or "image").ToList();
            if (leading.Count > 0 && !isNotes)
            {
                var html = new StringBuilder();
                foreach (var element in leading) reader.Block(element, html, 1);
                body.Append("<section id=\"ch").Append(++chapter).Append("\">").Append(html).Append("</section>\n");
            }
            if (isNotes)
            {
                var html = new StringBuilder();
                foreach (var element in fb2Body.Elements()) reader.Block(element, html, 1);
                body.Append("<section id=\"ch").Append(++chapter).Append("\">").Append(html).Append("</section>\n");
                continue;
            }
            foreach (var section in sections)
            {
                var html = new StringBuilder();
                reader.Section(section, html, 1);
                body.Append("<section id=\"ch").Append(++chapter).Append("\">").Append(html).Append("</section>\n");
            }
        }
        if (chapter == 0) throw new ConversionFailure($"{name} has no text.");

        var cleaned = BookHtml.Clean(body.ToString(), _ => null);
        return new EpubBook(title, authors.Count > 0 ? string.Join(", ", authors) : null,
            string.IsNullOrEmpty(language) ? BookLanguage.Detect(cleaned) : language, cleaned)
        { Cover = Cover(root, info) };
    }

    /// <summary>The picture of &lt;title-info&gt;&lt;coverpage&gt;&lt;image l:href="#id"/&gt;, taken from its &lt;binary&gt;.</summary>
    private static byte[]? Cover(XElement root, XElement? info)
    {
        var ns = root.Name.Namespace;
        var href = info?.Element(ns + "coverpage")?.Elements(ns + "image").FirstOrDefault()?.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
        if (href is null || !href.StartsWith('#')) return null;
        var binary = root.Elements(ns + "binary").FirstOrDefault(b => b.Attribute("id")?.Value == href[1..]);
        if (binary is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(new string(binary.Value.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            return bytes.Length <= EpubReader.MaxImageBytes && BookHtml.SniffImage(bytes) is not null ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Parses the XML, honouring its declared encoding (often windows-1251). A zipped FB2 (".fb2.zip" renamed) is opened too.</summary>
    private static XDocument Load(byte[] bytes, string name)
    {
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                        ?? throw new ConversionFailure($"{name} is a zip without an .fb2 file inside.");
            using var inner = entry.Open();
            using var buffer = new MemoryStream();
            inner.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using var stream = new MemoryStream(bytes);
        using var xml = XmlReader.Create(stream, settings);
        return XDocument.Load(xml, LoadOptions.PreserveWhitespace);
    }

    private static string AuthorName(XElement author)
    {
        var ns = author.Name.Namespace;
        var parts = new[] { "first-name", "middle-name", "last-name" }.Select(p => Clean(author.Element(ns + p)?.Value)).Where(p => p is not null).ToList();
        if (parts.Count > 0) return string.Join(' ', parts);
        return Clean(author.Element(ns + "nickname")?.Value) ?? "";
    }

    private static string? Picture(XElement binary)
    {
        try
        {
            var bytes = Convert.FromBase64String(new string(binary.Value.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            return BookHtml.DataUri(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Clean(string? text)
    {
        var trimmed = text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>FB2 elements to HTML. Sections become headings of increasing level; poems, epigraphs and citations become quotes.</summary>
    private sealed class Fb2HtmlReader(IReadOnlyDictionary<string, string?> binaries)
    {
        public void Section(XElement section, StringBuilder html, int depth)
        {
            var id = section.Attribute("id")?.Value;
            if (id is not null) html.Append("<a id=\"").Append(Attr(id)).Append("\"></a>");
            foreach (var child in section.Elements())
            {
                if (child.Name.LocalName == "section") Section(child, html, Math.Min(depth + 1, 6));
                else Block(child, html, depth);
            }
        }

        public void Block(XElement element, StringBuilder html, int depth)
        {
            var id = element.Attribute("id")?.Value;
            var idAttr = id is null ? "" : $" id=\"{Attr(id)}\"";
            switch (element.Name.LocalName)
            {
                case "title":
                    html.Append("<h").Append(depth).Append(idAttr).Append('>');
                    var first = true;
                    foreach (var p in element.Elements())
                    {
                        if (p.Name.LocalName != "p") continue;
                        if (!first) html.Append("<br>");
                        Inline(p, html);
                        first = false;
                    }
                    html.Append("</h").Append(depth).Append('>');
                    break;
                case "subtitle":
                    var level = Math.Min(depth + 1, 6);
                    html.Append("<h").Append(level).Append(idAttr).Append('>');
                    Inline(element, html);
                    html.Append("</h").Append(level).Append('>');
                    break;
                case "p":
                    html.Append("<p").Append(idAttr).Append('>');
                    Inline(element, html);
                    html.Append("</p>");
                    break;
                case "empty-line":
                    html.Append("<p><br></p>");
                    break;
                case "image":
                    html.Append("<p>");
                    Image(element, html);
                    html.Append("</p>");
                    break;
                case "epigraph" or "cite" or "annotation":
                    html.Append("<blockquote").Append(idAttr).Append('>');
                    foreach (var child in element.Elements()) Block(child, html, depth);
                    html.Append("</blockquote>");
                    break;
                case "text-author":
                    html.Append("<p><em>");
                    Inline(element, html);
                    html.Append("</em></p>");
                    break;
                case "poem":
                    html.Append("<blockquote").Append(idAttr).Append('>');
                    foreach (var child in element.Elements())
                    {
                        if (child.Name.LocalName == "stanza")
                        {
                            html.Append("<p>");
                            var firstLine = true;
                            foreach (var v in child.Elements())
                            {
                                if (v.Name.LocalName is not ("v" or "subtitle" or "title")) continue;
                                if (!firstLine) html.Append("<br>");
                                Inline(v.Name.LocalName == "title" ? v.Elements().FirstOrDefault() ?? v : v, html);
                                firstLine = false;
                            }
                            html.Append("</p>");
                        }
                        else
                        {
                            Block(child, html, depth);
                        }
                    }
                    html.Append("</blockquote>");
                    break;
                case "table":
                    html.Append("<table>");
                    foreach (var row in element.Elements().Where(r => r.Name.LocalName == "tr"))
                    {
                        html.Append("<tr>");
                        foreach (var cell in row.Elements())
                        {
                            var tag = cell.Name.LocalName == "th" ? "th" : "td";
                            html.Append('<').Append(tag).Append('>');
                            Inline(cell, html);
                            html.Append("</").Append(tag).Append('>');
                        }
                        html.Append("</tr>");
                    }
                    html.Append("</table>");
                    break;
                case "section":
                    Section(element, html, Math.Min(depth + 1, 6));
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(element.Value))
                    {
                        html.Append("<p>");
                        Inline(element, html);
                        html.Append("</p>");
                    }
                    break;
            }
        }

        private void Inline(XElement element, StringBuilder html)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XText text)
                {
                    html.Append(WebUtility.HtmlEncode(text.Value));
                    continue;
                }
                if (node is not XElement child) continue;
                var tag = child.Name.LocalName switch
                {
                    "strong" => "strong",
                    "emphasis" => "em",
                    "strikethrough" => "s",
                    "sub" => "sub",
                    "sup" => "sup",
                    "code" => "code",
                    _ => null,
                };
                if (tag is not null)
                {
                    html.Append('<').Append(tag).Append('>');
                    Inline(child, html);
                    html.Append("</").Append(tag).Append('>');
                }
                else if (child.Name.LocalName == "a")
                {
                    var href = child.Attribute(XLink + "href")?.Value ?? child.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
                    var note = child.Attribute("type")?.Value == "note";
                    html.Append(href is null ? "<a>" : $"<a href=\"{Attr(href)}\">");
                    if (note) html.Append("<sup>");
                    Inline(child, html);
                    if (note) html.Append("</sup>");
                    html.Append("</a>");
                }
                else if (child.Name.LocalName == "image")
                {
                    Image(child, html);
                }
                else
                {
                    Inline(child, html); // style and unknown inline elements: keep the text
                }
            }
        }

        private void Image(XElement image, StringBuilder html)
        {
            var href = image.Attribute(XLink + "href")?.Value ?? image.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
            var alt = image.Attribute("alt")?.Value ?? image.Attribute("title")?.Value ?? "";
            if (href is not null && href.StartsWith('#') && binaries.TryGetValue(href[1..], out var data) && data is not null)
                html.Append("<img src=\"").Append(data).Append("\" alt=\"").Append(Attr(alt)).Append("\">");
            else if (alt.Length > 0)
                html.Append(WebUtility.HtmlEncode(alt));
        }

        private static string Attr(string value) => WebUtility.HtmlEncode(value);
    }

    // ---- writing ------------------------------------------------------------------------------------------------------------

    /// <summary>Writes <paramref name="book"/> as FB2 (UTF-8). Each chapter becomes a top-level section; pictures become binaries.</summary>
    public static void Write(EpubBook book, string path)
    {
        var language = BookLanguage.Primary(book.Language);
        var binaries = new List<XElement>();
        var body = BookHtml.ExtractImages(book.BodyHtml, (index, mime, bytes) =>
        {
            var (type, data) = Fb2Picture(mime, bytes);
            if (data is null) return null;
            var id = $"image{index}.{BookHtml.Extension(type)}";
            binaries.Add(new XElement(Ns + "binary", new XAttribute("id", id), new XAttribute("content-type", type), Convert.ToBase64String(data)));
            return "#" + id;
        });

        var writer = new Fb2Builder();
        var sections = new List<XElement>();
        foreach (var chapter in BookHtml.Chapters(body, book.Title))
            sections.Add(writer.Section(chapter.Html, chapter.Title));

        var fiction = new XElement(Ns + "FictionBook",
            new XAttribute(XNamespace.Xmlns + "l", XLink),
            new XElement(Ns + "description",
                new XElement(Ns + "title-info",
                    new XElement(Ns + "genre", "nonfiction"),
                    Author(book.Author),
                    new XElement(Ns + "book-title", book.Title),
                    new XElement(Ns + "lang", language)),
                new XElement(Ns + "document-info",
                    new XElement(Ns + "author", new XElement(Ns + "nickname", "DropFlow")),
                    new XElement(Ns + "program-used", "DropFlow"),
                    new XElement(Ns + "date", new XAttribute("value", DateTime.UtcNow.ToString("yyyy-MM-dd")), DateTime.UtcNow.ToString("yyyy-MM-dd")),
                    new XElement(Ns + "id", Guid.NewGuid().ToString()),
                    new XElement(Ns + "version", "1.0"))),
            new XElement(Ns + "body",
                new XElement(Ns + "title", new XElement(Ns + "p", book.Title)),
                sections),
            binaries);

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), fiction);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        document.Save(xml);
    }

    private static XElement Author(string? author)
    {
        var name = author?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(name)) return new XElement(Ns + "author", new XElement(Ns + "nickname", "Unknown"));
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1) return new XElement(Ns + "author", new XElement(Ns + "nickname", words[0]));
        return new XElement(Ns + "author",
            new XElement(Ns + "first-name", string.Join(' ', words[..^1])),
            new XElement(Ns + "last-name", words[^1]));
    }

    /// <summary>FB2 readers show JPEG and PNG; other pictures are converted to PNG (SVG is left out).</summary>
    private static (string Type, byte[]? Data) Fb2Picture(string mime, byte[] bytes)
    {
        if (mime is "image/jpeg" or "image/png") return (mime, bytes);
        if (mime == "image/svg+xml") return (mime, null);
        try
        {
            using var image = new MagickImage(bytes);
            image.Format = MagickFormat.Png;
            return ("image/png", image.ToByteArray());
        }
        catch (MagickException)
        {
            return (mime, null);
        }
    }

    /// <summary>Turns chapter HTML into FB2 section content: paragraphs with inline formatting, subtitles, citations, tables and images.</summary>
    private sealed class Fb2Builder
    {
        public XElement Section(string html, string title)
        {
            var document = new HtmlParser().ParseDocument("<body>" + html + "</body>");
            var section = new XElement(Ns + "section");
            var content = new List<XElement>();
            var titled = false;
            foreach (var node in document.Body!.ChildNodes) Blocks(node, content, ref titled, section);
            if (!titled) section.Add(new XElement(Ns + "title", new XElement(Ns + "p", title)));
            section.Add(content);
            if (content.Count == 0) section.Add(new XElement(Ns + "empty-line"));
            return section;
        }

        private void Blocks(INode node, List<XElement> output, ref bool titled, XElement section)
        {
            if (node is IText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Data)) output.Add(new XElement(Ns + "p", Normalize(text.Data)));
                return;
            }
            if (node is not IElement element) return;
            switch (element.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    var heading = Normalize(element.TextContent);
                    if (heading.Length == 0) return;
                    if (!titled && output.Count == 0)
                    {
                        section.Add(new XElement(Ns + "title", new XElement(Ns + "p", heading)));
                        titled = true;
                    }
                    else
                    {
                        output.Add(new XElement(Ns + "subtitle", heading));
                    }
                    return;
                case "p" or "figcaption" or "dt" or "dd" or "caption":
                    Paragraphs(element, output);
                    return;
                case "blockquote":
                    var cite = new XElement(Ns + "cite");
                    var inner = new List<XElement>();
                    var dummy = true;
                    foreach (var child in element.ChildNodes) Blocks(child, inner, ref dummy, section);
                    foreach (var item in inner) cite.Add(item.Name.LocalName is "p" or "subtitle" or "empty-line" or "table" or "poem" ? item : new XElement(Ns + "p", item.Value));
                    if (cite.HasElements) output.Add(cite);
                    return;
                case "ul" or "ol":
                    var number = 1;
                    foreach (var item in element.Children.Where(c => c.LocalName == "li"))
                    {
                        var p = new XElement(Ns + "p", element.LocalName == "ol" ? $"{number++}. " : "• ");
                        AppendInline(item, p);
                        output.Add(p);
                    }
                    return;
                case "table":
                    var table = new XElement(Ns + "table");
                    foreach (var row in element.QuerySelectorAll("tr"))
                    {
                        var tr = new XElement(Ns + "tr");
                        foreach (var cell in row.Children.Where(c => c.LocalName is "td" or "th"))
                        {
                            var cellElement = new XElement(Ns + (cell.LocalName == "th" ? "th" : "td"));
                            AppendInline(cell, cellElement);
                            tr.Add(cellElement);
                        }
                        if (tr.HasElements) table.Add(tr);
                    }
                    if (table.HasElements) output.Add(table);
                    return;
                case "pre":
                    foreach (var line in element.TextContent.Replace("\r\n", "\n").Split('\n'))
                        output.Add(line.Length == 0 ? new XElement(Ns + "empty-line") : new XElement(Ns + "p", new XElement(Ns + "code", line)));
                    return;
                case "img":
                    if (Image(element) is { } image) output.Add(image);
                    return;
                case "hr":
                    output.Add(new XElement(Ns + "empty-line"));
                    return;
                case "br":
                    output.Add(new XElement(Ns + "empty-line"));
                    return;
                default:
                    if (element.Children.Any(c => IsBlock(c.LocalName)))
                    {
                        foreach (var child in element.ChildNodes) Blocks(child, output, ref titled, section);
                    }
                    else
                    {
                        Paragraphs(element, output);
                    }
                    return;
            }
        }

        private static bool IsBlock(string name) => name is "p" or "div" or "section" or "article" or "blockquote" or "ul" or "ol" or "table" or "pre"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "figure" or "header" or "footer" or "aside" or "main" or "nav" or "hr" or "dl" or "img";

        /// <summary>One or more &lt;p&gt;: an FB2 paragraph cannot hold a line break, so each &lt;br&gt; starts a new one.</summary>
        private void Paragraphs(IElement element, List<XElement> output)
        {
            var paragraph = new XElement(Ns + "p");
            var produced = new List<XElement> { paragraph };
            AppendInline(element, paragraph, () =>
            {
                paragraph = new XElement(Ns + "p");
                produced.Add(paragraph);
                return paragraph;
            });
            foreach (var p in produced)
            {
                if (p.IsEmpty || (string.IsNullOrWhiteSpace(p.Value) && !p.Descendants(Ns + "image").Any())) continue;
                output.Add(p);
            }
            if (produced.All(p => string.IsNullOrWhiteSpace(p.Value) && !p.Descendants(Ns + "image").Any()) && element.QuerySelector("img") is null && output.Count > 0)
                output.Add(new XElement(Ns + "empty-line"));
        }

        private void AppendInline(INode node, XElement target, Func<XElement>? breakLine = null)
        {
            var current = target;
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text)
                {
                    current.Add(Normalize(text.Data, keepEdges: true));
                    continue;
                }
                if (child is not IElement element) continue;
                switch (element.LocalName)
                {
                    case "br":
                        if (breakLine is not null) current = breakLine();
                        else current.Add(" ");
                        break;
                    case "b" or "strong":
                        current.Add(Styled("strong", element));
                        break;
                    case "i" or "em" or "cite" or "dfn":
                        current.Add(Styled("emphasis", element));
                        break;
                    case "s" or "strike" or "del":
                        current.Add(Styled("strikethrough", element));
                        break;
                    case "sub" or "sup":
                        current.Add(Styled(element.LocalName, element));
                        break;
                    case "code" or "tt" or "kbd" or "samp":
                        current.Add(Styled("code", element));
                        break;
                    case "a":
                        var href = element.GetAttribute("href");
                        if (href is not null && (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                        {
                            var a = new XElement(Ns + "a", new XAttribute(XLink + "href", href));
                            AppendInline(element, a);
                            current.Add(a);
                        }
                        else
                        {
                            AppendInline(element, current, breakLine);
                        }
                        break;
                    case "img":
                        if (Image(element) is { } image) current.Add(image);
                        break;
                    default:
                        AppendInline(element, current, breakLine);
                        break;
                }
            }
        }

        private XElement Styled(string name, IElement element)
        {
            var styled = new XElement(Ns + name);
            AppendInline(element, styled);
            return styled;
        }

        private static XElement? Image(IElement img)
        {
            var src = img.GetAttribute("src");
            if (src is null || !src.StartsWith('#')) return null;
            var image = new XElement(Ns + "image", new XAttribute(XLink + "href", src));
            var alt = img.GetAttribute("alt");
            if (!string.IsNullOrWhiteSpace(alt)) image.Add(new XAttribute("alt", alt));
            return image;
        }

        private static string Normalize(string text, bool keepEdges = false)
        {
            var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            return keepEdges ? collapsed : collapsed.Trim();
        }
    }
}
