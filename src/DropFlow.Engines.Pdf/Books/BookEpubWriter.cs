// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using AngleSharp;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>Reads any supported e-book into the shared model (title, author, language and one HTML body of &lt;section&gt; chapters).</summary>
internal static class BookReader
{
    public static EpubBook Read(string path, EpubImages images, string? imageFolder = null)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension == "epub") return EpubReader.Read(path, images, imageFolder);
        var book = extension switch
        {
            "fb2" => Fb2.Read(path),
            "mobi" or "azw3" => Mobi.Read(path),
            "htmlz" => ZippedBooks.ReadHtmlz(path),
            "txtz" => ZippedBooks.ReadTxtz(path),
            _ => throw new ConversionFailure($"{Path.GetFileName(path)}: .{extension} is not an e-book format DropFlow reads."),
        };
        return book with { BodyHtml = BookHtml.ApplyImageMode(book.BodyHtml, images, imageFolder) };
    }
}

/// <summary>
/// Writes the shared book model as an EPUB 3: one XHTML file per chapter, pictures in an images folder, a navigation document, and the
/// reading direction of the language (dir="rtl" and page-progression-direction="rtl" for Arabic, Hebrew, Persian, Urdu...).
/// Also writes fixed-layout picture books (comics): one page per picture.
/// </summary>
internal static class BookEpubWriter
{
    private const string XhtmlNs = "http://www.w3.org/1999/xhtml";
    private const string EpubNs = "http://www.idpf.org/2007/ops";

    public static void Write(EpubBook book, string path)
    {
        var language = BookLanguage.Primary(book.Language);
        var rtl = BookLanguage.IsRtl(language);
        var pictures = new List<(string Name, string Mime, byte[] Bytes)>();
        var body = BookHtml.ExtractImages(book.BodyHtml, (index, mime, bytes) =>
        {
            var name = $"images/image{index}.{BookHtml.Extension(mime)}";
            pictures.Add((name, mime, bytes));
            return name;
        });
        var chapters = BookHtml.Chapters(body, book.Title);

        // Anchors move into separate files: "#id" must become "chapterN.xhtml#id".
        var parser = new HtmlParser();
        var documents = chapters.Select(c => parser.ParseDocument("<body>" + c.Html + "</body>")).ToList();
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < documents.Count; i++)
        {
            owner.TryAdd(chapters[i].Id, i);
            foreach (var element in documents[i].QuerySelectorAll("[id]")) owner.TryAdd(element.Id!, i);
        }
        for (var i = 0; i < documents.Count; i++)
        {
            foreach (var link in documents[i].QuerySelectorAll("a[href^='#']"))
            {
                var id = link.GetAttribute("href")![1..];
                if (!owner.TryGetValue(id, out var target)) link.RemoveAttribute("href");
                else if (target != i) link.SetAttribute("href", $"chapter{target + 1}.xhtml#{id}");
            }
        }

        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        AddText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        AddText(zip, "META-INF/container.xml", Container);
        for (var i = 0; i < documents.Count; i++)
        {
            var xhtml = new StringBuilder(Head(chapters[i].Title, language, rtl, fixedSize: null));
            foreach (var node in documents[i].Body!.ChildNodes) xhtml.Append(Xhtml(node));
            xhtml.Append("\n</body>\n</html>\n");
            AddText(zip, $"OEBPS/chapter{i + 1}.xhtml", xhtml.ToString());
        }
        foreach (var (name, _, bytes) in pictures)
        {
            using var entry = zip.CreateEntry("OEBPS/" + name, CompressionLevel.NoCompression).Open();
            entry.Write(bytes);
        }
        AddText(zip, "OEBPS/nav.xhtml", Nav(chapters.Select(c => c.Title).ToList(), book.Title, language, rtl));
        AddText(zip, "OEBPS/style.css", Css);

        var manifest = new StringBuilder();
        manifest.Append("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
        manifest.Append("    <item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
        for (var i = 0; i < documents.Count; i++)
            manifest.Append($"    <item id=\"chapter{i + 1}\" href=\"chapter{i + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
        for (var i = 0; i < pictures.Count; i++)
            manifest.Append($"    <item id=\"image{i + 1}\" href=\"{pictures[i].Name}\" media-type=\"{pictures[i].Mime}\"/>\n");
        var spine = string.Concat(Enumerable.Range(1, documents.Count).Select(i => $"    <itemref idref=\"chapter{i}\"/>\n"));
        AddText(zip, "OEBPS/content.opf", Package(book.Title, book.Author, language, rtl, modified, manifest.ToString(), spine, fixedLayout: false));
    }

    /// <summary>A fixed-layout EPUB with one page per picture (comics). <paramref name="rtl"/> turns pages right to left (manga).</summary>
    public static void WritePictureBook(string path, string title, string? author, string language, bool rtl,
        IReadOnlyList<(byte[] Bytes, string Mime, int Width, int Height)> pages, CancellationToken ct)
    {
        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        AddText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        AddText(zip, "META-INF/container.xml", Container);
        var digits = Math.Max(3, pages.Count.ToString().Length);
        var manifest = new StringBuilder();
        manifest.Append("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
        manifest.Append("    <item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
        var spine = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var number = (i + 1).ToString().PadLeft(digits, '0');
            var image = $"images/page{number}.{BookHtml.Extension(pages[i].Mime)}";
            using (var entry = zip.CreateEntry("OEBPS/" + image, CompressionLevel.NoCompression).Open()) entry.Write(pages[i].Bytes);
            var page = Head($"{title} {i + 1}", BookLanguage.Primary(language), rtl, (pages[i].Width, pages[i].Height)) +
                       $"<div class=\"page\"><img src=\"{image}\" alt=\"{i + 1}\" width=\"{pages[i].Width}\" height=\"{pages[i].Height}\"/></div>\n</body>\n</html>\n";
            AddText(zip, $"OEBPS/page{number}.xhtml", page);
            manifest.Append($"    <item id=\"p{number}\" href=\"page{number}.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            manifest.Append($"    <item id=\"i{number}\" href=\"{image}\" media-type=\"{pages[i].Mime}\"/>\n");
            spine.Append($"    <itemref idref=\"p{number}\"/>\n");
        }
        AddText(zip, "OEBPS/nav.xhtml", Nav([title], title, BookLanguage.Primary(language), rtl, firstFile: $"page{1.ToString().PadLeft(digits, '0')}.xhtml"));
        AddText(zip, "OEBPS/style.css", "html,body{margin:0;padding:0}.page{margin:0;padding:0}.page img{display:block;width:100%;height:100%}\n");
        AddText(zip, "OEBPS/content.opf", Package(title, author, BookLanguage.Primary(language), rtl, modified, manifest.ToString(), spine.ToString(), fixedLayout: true));
    }

    /// <summary>XHTML (well-formed XML) for one node; HTML-only named entities become numeric ones.</summary>
    private static string Xhtml(AngleSharp.Dom.INode node)
    {
        var text = node.ToHtml(XhtmlMarkupFormatter.Instance);
        return text.Replace("&nbsp;", "&#160;");
    }

    private const string Container =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
        "  <rootfiles>\n    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n  </rootfiles>\n</container>\n";

    private const string Css =
        "body { font-family: serif; line-height: 1.5; margin: 5%; }\n" +
        "h1, h2, h3, h4, h5, h6 { line-height: 1.2; }\n" +
        "p, li, h1, h2, h3, h4, h5, h6, blockquote { unicode-bidi: plaintext; text-align: start; }\n" +
        "img { max-width: 100%; height: auto; }\n" +
        "table { border-collapse: collapse; margin: 1em 0; }\n" +
        "td, th { border: 1px solid #888; padding: 0.2em 0.5em; }\n";

    private static string Head(string title, string language, bool rtl, (int Width, int Height)? fixedSize) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n" +
        $"<html xmlns=\"{XhtmlNs}\" xmlns:epub=\"{EpubNs}\" lang=\"{language}\" xml:lang=\"{language}\" dir=\"{(rtl ? "rtl" : "ltr")}\">\n" +
        $"<head>\n<meta charset=\"utf-8\"/>\n<title>{Escape(title)}</title>\n" +
        (fixedSize is { } size ? $"<meta name=\"viewport\" content=\"width={size.Width}, height={size.Height}\"/>\n" : "") +
        "<link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/>\n</head>\n<body>\n";

    private static string Nav(IReadOnlyList<string> titles, string bookTitle, string language, bool rtl, string? firstFile = null)
    {
        var sb = new StringBuilder(Head(bookTitle, language, rtl, null));
        sb.Append("<nav epub:type=\"toc\" id=\"toc\">\n<h1>").Append(Escape(bookTitle)).Append("</h1>\n<ol>\n");
        for (var i = 0; i < titles.Count; i++)
            sb.Append("<li><a href=\"").Append(firstFile ?? $"chapter{i + 1}.xhtml").Append("\">").Append(Escape(titles[i])).Append("</a></li>\n");
        sb.Append("</ol>\n</nav>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Package(string title, string? author, string language, bool rtl, string modified, string manifest, string spine, bool fixedLayout)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append($"<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"{language}\"");
        if (fixedLayout) sb.Append(" prefix=\"rendition: http://www.idpf.org/vocab/rendition/#\"");
        sb.Append(">\n  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append($"    <dc:identifier id=\"bookid\">urn:uuid:{Guid.NewGuid()}</dc:identifier>\n");
        sb.Append($"    <dc:title>{Escape(title)}</dc:title>\n");
        sb.Append($"    <dc:language>{language}</dc:language>\n");
        if (author is not null) sb.Append($"    <dc:creator>{Escape(author)}</dc:creator>\n");
        sb.Append($"    <meta property=\"dcterms:modified\">{modified}</meta>\n");
        if (fixedLayout)
        {
            sb.Append("    <meta property=\"rendition:layout\">pre-paginated</meta>\n");
            sb.Append("    <meta property=\"rendition:spread\">auto</meta>\n");
        }
        sb.Append("  </metadata>\n  <manifest>\n").Append(manifest).Append("  </manifest>\n");
        sb.Append($"  <spine page-progression-direction=\"{(rtl ? "rtl" : "ltr")}\">\n").Append(spine).Append("  </spine>\n</package>\n");
        return sb.ToString();
    }

    private static void AddText(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
