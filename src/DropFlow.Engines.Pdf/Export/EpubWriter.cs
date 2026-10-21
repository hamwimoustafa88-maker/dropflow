using System.IO.Compression;
using System.Net;
using System.Text;
using DropFlow.Engines.Pdf.Extraction;

namespace DropFlow.Engines.Pdf.Export;

/// <summary>
/// Writes an EPUB 3 container by hand: the stored "mimetype" entry first, META-INF/container.xml, the package document, a navigation
/// document and XHTML chapters. Chapters follow the top-level headings; a PDF without headings is cut every ten pages.
/// </summary>
internal static class EpubWriter
{
    private const int PagesPerChapter = 10;
    private const string XhtmlNs = "http://www.w3.org/1999/xhtml";
    private const string EpubNs = "http://www.idpf.org/2007/ops";

    internal sealed record Chapter(string Title, IReadOnlyList<Block> Blocks);

    public static void Write(PdfTextDocument source, string path, string title, string language, DateTime? modifiedUtc = null)
    {
        var chapters = SplitIntoChapters(source, title);
        var rtl = language is "ar" or "he";
        var modified = (modifiedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        // The EPUB container rules: "mimetype" is the first entry, stored without compression and without an extra field.
        AddText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        AddText(zip, "META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
            "  <rootfiles>\n    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n  </rootfiles>\n</container>\n");

        for (var i = 0; i < chapters.Count; i++)
            AddText(zip, $"OEBPS/chapter{i + 1}.xhtml", ChapterXhtml(chapters[i], language, rtl));

        AddText(zip, "OEBPS/nav.xhtml", NavXhtml(chapters, title, language, rtl));
        AddText(zip, "OEBPS/style.css", Css);
        AddText(zip, "OEBPS/content.opf", PackageXml(source, chapters, title, language, rtl, modified));
    }

    // ---- chapters -------------------------------------------------------------------------------------------------------

    internal static List<Chapter> SplitIntoChapters(PdfTextDocument source, string documentTitle)
    {
        var blocks = new List<(int Page, Block Block)>();
        foreach (var page in source.Pages)
            foreach (var block in page.Blocks)
                blocks.Add((page.Number, block));

        var headings = blocks.Where(b => b.Block is HeadingBlock).Select(b => ((HeadingBlock)b.Block).Level).ToList();
        var chapters = new List<Chapter>();

        if (headings.Count >= 2)
        {
            var topLevel = headings.Min();
            var current = new List<Block>();
            var currentTitle = documentTitle;
            foreach (var (_, block) in blocks)
            {
                if (block is HeadingBlock h && h.Level == topLevel)
                {
                    if (current.Count > 0) chapters.Add(new Chapter(currentTitle, current));
                    current = [block];
                    currentTitle = h.Text;
                }
                else
                {
                    current.Add(block);
                }
            }
            if (current.Count > 0) chapters.Add(new Chapter(currentTitle, current));
        }

        if (chapters.Count == 0)
        {
            foreach (var group in blocks.GroupBy(b => (b.Page - 1) / PagesPerChapter))
            {
                var first = group.First().Page;
                var last = group.Last().Page;
                var name = first == last ? $"Page {first}" : $"Pages {first}-{last}";
                chapters.Add(new Chapter(blocks.Count > 0 && source.Pages.Count <= PagesPerChapter ? documentTitle : name, group.Select(g => g.Block).ToList()));
            }
        }

        if (chapters.Count == 0) chapters.Add(new Chapter(documentTitle, [new ParagraphBlock("", false)]));
        return chapters;
    }

    // ---- documents ------------------------------------------------------------------------------------------------------

    private static string ChapterXhtml(Chapter chapter, string language, bool rtl)
    {
        var body = new StringBuilder();
        HtmlWriter.AppendBlocks(body, chapter.Blocks);
        return XhtmlHead(chapter.Title, language, rtl) + body + "</body>\n</html>\n";
    }

    private static string NavXhtml(IReadOnlyList<Chapter> chapters, string title, string language, bool rtl)
    {
        var sb = new StringBuilder(XhtmlHead(title, language, rtl));
        sb.Append("<nav epub:type=\"toc\" id=\"toc\">\n<h1>").Append(Escape(title)).Append("</h1>\n<ol>\n");
        for (var i = 0; i < chapters.Count; i++)
            sb.Append("<li><a href=\"chapter").Append(i + 1).Append(".xhtml\">").Append(Escape(chapters[i].Title)).Append("</a></li>\n");
        sb.Append("</ol>\n</nav>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string XhtmlHead(string title, string language, bool rtl) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n" +
        $"<html xmlns=\"{XhtmlNs}\" xmlns:epub=\"{EpubNs}\" lang=\"{language}\" xml:lang=\"{language}\" dir=\"{(rtl ? "rtl" : "ltr")}\">\n" +
        $"<head>\n<meta charset=\"utf-8\"/>\n<title>{Escape(title)}</title>\n<link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/>\n</head>\n<body>\n";

    private static string PackageXml(PdfTextDocument source, IReadOnlyList<Chapter> chapters, string title, string language, bool rtl, string modified)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append($"<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"{language}\">\n");
        sb.Append("  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append($"    <dc:identifier id=\"bookid\">urn:uuid:{Guid.NewGuid()}</dc:identifier>\n");
        sb.Append($"    <dc:title>{Escape(title)}</dc:title>\n");
        sb.Append($"    <dc:language>{language}</dc:language>\n");
        sb.Append($"    <dc:creator>{Escape(source.Author ?? "Unknown")}</dc:creator>\n");
        sb.Append($"    <meta property=\"dcterms:modified\">{modified}</meta>\n");
        sb.Append("  </metadata>\n  <manifest>\n");
        sb.Append("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
        sb.Append("    <item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
        for (var i = 0; i < chapters.Count; i++)
            sb.Append($"    <item id=\"chapter{i + 1}\" href=\"chapter{i + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
        sb.Append("  </manifest>\n");
        sb.Append($"  <spine page-progression-direction=\"{(rtl ? "rtl" : "ltr")}\">\n");
        for (var i = 0; i < chapters.Count; i++) sb.Append($"    <itemref idref=\"chapter{i + 1}\"/>\n");
        sb.Append("    <itemref idref=\"nav\" linear=\"no\"/>\n  </spine>\n</package>\n");
        return sb.ToString();
    }

    private const string Css =
        "body { font-family: serif; line-height: 1.5; margin: 5%; }\n" +
        "h1, h2, h3, h4, h5, h6 { font-family: sans-serif; line-height: 1.2; }\n" +
        "table { border-collapse: collapse; margin: 1em 0; }\n" +
        "td, th { border: 1px solid #888; padding: 0.2em 0.5em; }\n";

    private static void AddText(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
