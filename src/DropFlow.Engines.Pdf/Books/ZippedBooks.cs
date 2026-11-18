// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;
using Markdig;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>
/// HTMLZ and TXTZ, the zipped HTML and zipped text books e-book managers such as calibre exchange: one index.html (or index.txt, Markdown),
/// its pictures in an images folder, and the book's title, author and language in a metadata.opf file.
/// </summary>
internal static class ZippedBooks
{
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    // ---- reading ------------------------------------------------------------------------------------------------------------

    public static EpubBook ReadHtmlz(string path) => Read(path, text: false);

    public static EpubBook ReadTxtz(string path) => Read(path, text: true);

    private static EpubBook Read(string path, bool text)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            var main = text
                ? entries.FirstOrDefault(e => e.FullName.Equals("index.txt", StringComparison.OrdinalIgnoreCase))
                  ?? entries.Where(e => e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".text", StringComparison.OrdinalIgnoreCase))
                      .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
                : entries.FirstOrDefault(e => e.FullName.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                  ?? entries.Where(e => e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
                      .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault();
            if (main is null) throw new ConversionFailure($"{name} has no {(text ? "text" : "HTML")} file inside.");

            var bytes = ReadEntry(main);
            var baseDir = main.FullName.Contains('/') ? main.FullName[..(main.FullName.LastIndexOf('/') + 1)] : "";
            string bodyHtml;
            string? htmlTitle = null;
            if (text)
            {
                var markdown = TextFile.Decode(bytes);
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
                bodyHtml = Markdown.ToHtml(markdown, pipeline);
            }
            else
            {
                var document = new HtmlParser().ParseDocument(DecodeHtml(bytes));
                htmlTitle = document.Title;
                bodyHtml = document.Body?.InnerHtml ?? "";
            }

            var cleaned = BookHtml.Clean(bodyHtml, src => Picture(zip, baseDir, src));
            var (title, author, language) = Metadata(zip);
            title ??= string.IsNullOrWhiteSpace(htmlTitle) ? Path.GetFileNameWithoutExtension(path) : htmlTitle.Trim();
            return new EpubBook(title, author, string.IsNullOrEmpty(language) ? BookLanguage.Detect(cleaned) : language, cleaned);
        }
        catch (InvalidDataException ex)
        {
            throw new ConversionFailure($"{name} is not a valid {(text ? "TXTZ" : "HTMLZ")} book ({ex.Message}).");
        }
    }

    private static string? Picture(ZipArchive zip, string baseDir, string src)
    {
        var target = EpubReader.Resolve(baseDir, src.Split('#', '?')[0]);
        var entry = zip.GetEntry(target);
        if (entry is null || entry.Length > EpubReader.MaxImageBytes) return null;
        return BookHtml.DataUri(ReadEntry(entry));
    }

    private static (string? Title, string? Author, string? Language) Metadata(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return (null, null, null);
        try
        {
            using var stream = entry.Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var opf = XDocument.Load(xml);
            string? Value(string local) => opf.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim() is { Length: > 0 } v ? v : null;
            return (Value("title"), Value("creator"), Value("language"));
        }
        catch (XmlException)
        {
            return (null, null, null); // metadata is optional; the text is what matters
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) throw new ConversionFailure($"{entry.FullName} is too large.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string DecodeHtml(byte[] bytes)
    {
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        var match = System.Text.RegularExpressions.Regex.Match(head, @"charset\s*=\s*[""']?\s*(?<cs>[\w\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            try
            {
                var encoding = Encoding.GetEncoding(match.Groups["cs"].Value);
                if (encoding.CodePage != 65001) return encoding.GetString(bytes);
            }
            catch (ArgumentException)
            {
                // unknown name: detect below
            }
        }
        return TextFile.Decode(bytes);
    }

    // ---- writing ------------------------------------------------------------------------------------------------------------

    /// <summary>index.html (a complete page, pictures in images/), style.css and metadata.opf.</summary>
    public static void WriteHtmlz(EpubBook book, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var body = SaveImages(zip, book.BodyHtml);
        var language = BookLanguage.Primary(book.Language);
        var page = new StringBuilder();
        page.Append("<!DOCTYPE html>\n<html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\" dir=\"").Append(BookLanguage.IsRtl(language) ? "rtl" : "auto").Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        page.Append("<title>").Append(WebUtility.HtmlEncode(book.Title)).Append("</title>\n");
        if (book.Author is not null) page.Append("<meta name=\"author\" content=\"").Append(WebUtility.HtmlEncode(book.Author)).Append("\">\n");
        page.Append("<link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\">\n</head>\n<body>\n").Append(body).Append("\n</body>\n</html>\n");
        Add(zip, "index.html", page.ToString());
        Add(zip, "style.css", "body{font-family:serif;line-height:1.5;margin:0 5%}img{max-width:100%;height:auto}\np,li,h1,h2,h3,h4,h5,h6,blockquote{unicode-bidi:plaintext;text-align:start}\n");
        Add(zip, "metadata.opf", MetadataOpf(book, "index.html", "text/html"));
    }

    /// <summary>index.txt (Markdown, pictures referenced as images/...), the pictures, and metadata.opf.</summary>
    public static void WriteTxtz(EpubBook book, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var body = SaveImages(zip, book.BodyHtml);
        Add(zip, "index.txt", DocumentTools.HtmlToMarkdown(body));
        Add(zip, "metadata.opf", MetadataOpf(book, "index.txt", "text/plain"));
    }

    private static string SaveImages(ZipArchive zip, string bodyHtml) =>
        BookHtml.ExtractImages(bodyHtml, (index, mime, bytes) =>
        {
            var name = $"images/image{index}.{BookHtml.Extension(mime)}";
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(bytes);
            return name;
        });

    /// <summary>An OPF 2.0 package that carries the book's metadata (what e-book managers read from HTMLZ and TXTZ files).</summary>
    internal static string MetadataOpf(EpubBook book, string mainFile, string mediaType)
    {
        var package = new XElement(Opf + "package",
            new XAttribute("version", "2.0"),
            new XAttribute("unique-identifier", "id"),
            new XElement(Opf + "metadata",
                new XAttribute(XNamespace.Xmlns + "dc", Dc),
                new XElement(Dc + "title", book.Title),
                book.Author is null ? null : new XElement(Dc + "creator", book.Author),
                new XElement(Dc + "language", BookLanguage.Primary(book.Language)),
                new XElement(Dc + "identifier", new XAttribute("id", "id"), "urn:uuid:" + Guid.NewGuid())),
            new XElement(Opf + "manifest", new XElement(Opf + "item", new XAttribute("id", "main"), new XAttribute("href", mainFile), new XAttribute("media-type", mediaType))),
            new XElement(Opf + "spine", new XElement(Opf + "itemref", new XAttribute("idref", "main"))));
        return new XDeclaration("1.0", "utf-8", null) + "\n" + package;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
