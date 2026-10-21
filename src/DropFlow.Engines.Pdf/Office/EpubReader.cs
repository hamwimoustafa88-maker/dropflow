using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

internal enum EpubImages
{
    /// <summary>Pictures become data: URIs, so the HTML is one self-contained file.</summary>
    Inline,
    /// <summary>Pictures are written to a folder next to the HTML (what a word processor import needs).</summary>
    ToFolder,
    /// <summary>Pictures are left out; their alt text stays.</summary>
    Drop,
}

internal sealed record EpubBook(string Title, string? Author, string Language, string BodyHtml);

/// <summary>
/// Reads an EPUB (a zip of XHTML chapters) without any engine: container.xml -> package document -> spine order -> chapters.
/// The chapters are cleaned (no scripts, styles, forms or event handlers), links between chapters become in-page anchors, and the
/// result is one HTML body that the HTML, Markdown and plain-text writers - or LibreOffice - can take from there.
/// </summary>
internal static class EpubReader
{
    private const long MaxImageBytes = 8 * 1024 * 1024;

    private static readonly string[] RemovedElements =
        ["script", "style", "link", "meta", "iframe", "object", "embed", "form", "input", "button", "select", "textarea", "base", "template", "noscript", "audio", "video", "canvas"];

    private static readonly HashSet<string> KeptAttributes = new(StringComparer.OrdinalIgnoreCase)
        { "id", "dir", "lang", "href", "src", "alt", "title", "colspan", "rowspan", "start", "type" };

    public static EpubBook Read(string path, EpubImages images, string? imageFolder = null)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            RejectDrm(zip, name);

            var containerEntry = zip.GetEntry("META-INF/container.xml") ?? throw new ConversionFailure($"{name} is not a valid EPUB (container.xml is missing).");
            var container = Load(containerEntry);
            var rootfile = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
                           ?? throw new ConversionFailure($"{name} is not a valid EPUB (no package document is declared).");
            rootfile = Uri.UnescapeDataString(rootfile);
            var opfEntry = zip.GetEntry(rootfile) ?? throw new ConversionFailure($"{name} is not a valid EPUB ({rootfile} is missing).");
            var opf = Load(opfEntry);
            var baseDir = rootfile.Contains('/') ? rootfile[..(rootfile.LastIndexOf('/') + 1)] : "";

            var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
                .Where(e => e.Attribute("id") is not null && e.Attribute("href") is not null)
                .ToDictionary(e => e.Attribute("id")!.Value, e => (Href: Resolve(baseDir, e.Attribute("href")!.Value), Type: e.Attribute("media-type")?.Value ?? "", Props: e.Attribute("properties")?.Value ?? ""), StringComparer.Ordinal);

            var chapters = opf.Descendants().Where(e => e.Name.LocalName == "itemref")
                .Where(e => !string.Equals(e.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("idref")?.Value)
                .Where(id => id is not null && manifest.ContainsKey(id))
                .Select(id => manifest[id!])
                .Where(m => m.Type is "application/xhtml+xml" or "text/html" && !m.Props.Split(' ').Contains("nav"))
                .ToList();
            if (chapters.Count == 0) throw new ConversionFailure($"{name} has no readable chapters.");

            var chapterIds = chapters.Select((c, i) => (c.Href, Id: $"ch{i + 1}")).ToDictionary(c => c.Href, c => c.Id, StringComparer.Ordinal);
            if (images == EpubImages.ToFolder && imageFolder is not null) Directory.CreateDirectory(imageFolder);

            var parser = new HtmlParser();
            var written = new Dictionary<string, string>(StringComparer.Ordinal);
            var body = new StringBuilder();
            for (var i = 0; i < chapters.Count; i++)
            {
                var entry = zip.GetEntry(chapters[i].Href);
                if (entry is null) continue;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var document = parser.ParseDocument(reader.ReadToEnd());
                var chapterDir = chapters[i].Href.Contains('/') ? chapters[i].Href[..(chapters[i].Href.LastIndexOf('/') + 1)] : "";

                Clean(document, zip, chapterDir, chapterIds, images, imageFolder, written);
                body.Append("<section id=\"").Append(chapterIds[chapters[i].Href]).Append("\">\n").Append(document.Body?.InnerHtml ?? "").Append("\n</section>\n");
            }

            var title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
            var author = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "creator")?.Value.Trim();
            var language = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "language")?.Value.Trim();
            return new EpubBook(
                string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(path) : title,
                string.IsNullOrEmpty(author) ? null : author,
                string.IsNullOrEmpty(language) ? "en" : language,
                body.ToString());
        }
        catch (InvalidDataException ex)
        {
            throw new ConversionFailure($"{name} is not a valid EPUB ({ex.Message}).");
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ConversionFailure($"{name} is not a valid EPUB ({ex.Message}).");
        }
    }

    /// <summary>A complete HTML page around the book body.</summary>
    public static string ToHtmlDocument(EpubBook book)
    {
        var language = book.Language.Split('-')[0].ToLowerInvariant();
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\" dir=\"auto\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        if (book.Author is not null) sb.Append("<meta name=\"author\" content=\"").Append(WebUtility.HtmlEncode(book.Author)).Append("\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(book.Title)).Append("</title>\n<style>\n");
        sb.Append("body{font-family:Georgia,'Times New Roman',serif;line-height:1.6;max-width:46rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a}\n");
        sb.Append("p,li,h1,h2,h3,h4,h5,h6,blockquote,td,th{unicode-bidi:plaintext;text-align:start}img{max-width:100%;height:auto}\n");
        sb.Append("table{border-collapse:collapse}td,th{border:1px solid #999;padding:.2rem .5rem}section{margin-bottom:2rem}\n");
        sb.Append("</style>\n</head>\n<body>\n").Append(book.BodyHtml).Append("</body>\n</html>\n");
        return sb.ToString();
    }

    // ---- cleaning -------------------------------------------------------------------------------------------------------

    private static void Clean(
        IDocument document, ZipArchive zip, string chapterDir, Dictionary<string, string> chapterIds,
        EpubImages images, string? imageFolder, Dictionary<string, string> written)
    {
        // Inline SVG is only kept as a picture wrapper: <svg><image href="cover.jpg"/></svg> becomes <img>.
        foreach (var svg in document.QuerySelectorAll("svg").ToList())
        {
            var image = svg.QuerySelectorAll("image").FirstOrDefault();
            var href = image?.Attributes.FirstOrDefault(a => a.LocalName == "href")?.Value;
            if (href is not null)
            {
                var img = document.CreateElement("img");
                img.SetAttribute("src", href);
                svg.Replace(img);
            }
            else
            {
                svg.Remove();
            }
        }

        foreach (var tag in RemovedElements)
            foreach (var element in document.QuerySelectorAll(tag).ToList()) element.Remove();

        foreach (var element in document.QuerySelectorAll("*").ToList())
        {
            foreach (var attribute in element.Attributes.ToList())
                if (!KeptAttributes.Contains(attribute.Name)) element.RemoveAttribute(attribute.Name);
        }

        foreach (var link in document.QuerySelectorAll("a[href]").ToList())
        {
            var href = link.GetAttribute("href") ?? "";
            var rewritten = RewriteLink(href, chapterDir, chapterIds);
            if (rewritten is null) link.RemoveAttribute("href");
            else link.SetAttribute("href", rewritten);
        }

        foreach (var img in document.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src") ?? "";
            var replacement = images == EpubImages.Drop ? null : ImageSource(src, chapterDir, zip, images, imageFolder, written);
            if (replacement is null)
            {
                var alt = img.GetAttribute("alt");
                if (!string.IsNullOrWhiteSpace(alt)) img.Replace(document.CreateTextNode(alt));
                else img.Remove();
            }
            else
            {
                img.SetAttribute("src", replacement);
            }
        }
    }

    private static string? RewriteLink(string href, string chapterDir, Dictionary<string, string> chapterIds)
    {
        href = href.Trim();
        if (href.StartsWith('#')) return href;
        var colon = href.IndexOf(':');
        if (colon > 0 && !href[..colon].Contains('/'))
            return href[..colon].ToLowerInvariant() is "http" or "https" or "mailto" ? href : null;

        var hash = href.IndexOf('#');
        var path = hash >= 0 ? href[..hash] : href;
        var fragment = hash >= 0 ? href[(hash + 1)..] : "";
        var target = Resolve(chapterDir, path);
        if (!chapterIds.TryGetValue(target, out var id)) return null;
        return fragment.Length > 0 ? "#" + fragment : "#" + id;
    }

    private static string? ImageSource(string src, string chapterDir, ZipArchive zip, EpubImages mode, string? imageFolder, Dictionary<string, string> written)
    {
        if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return mode == EpubImages.Inline ? src : null;
        var entryName = Resolve(chapterDir, src.Split('#')[0]);
        if (written.TryGetValue(entryName, out var done)) return done;
        var entry = zip.GetEntry(entryName);
        if (entry is null || entry.Length > MaxImageBytes) return null;

        var ext = Path.GetExtension(entryName).TrimStart('.').ToLowerInvariant();
        var mime = ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            _ => null,
        };
        if (mime is null) return null;

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        string result;
        if (mode == EpubImages.Inline)
        {
            result = $"data:{mime};base64,{Convert.ToBase64String(buffer.ToArray())}";
        }
        else
        {
            var fileName = $"image{written.Count + 1}.{ext}";
            File.WriteAllBytes(Path.Combine(imageFolder!, fileName), buffer.ToArray());
            result = Path.GetFileName(imageFolder!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/" + fileName;
        }
        written[entryName] = result;
        return result;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static void RejectDrm(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry("META-INF/encryption.xml");
        if (entry is null) return;
        var algorithms = Load(entry).Descendants().Where(e => e.Name.LocalName == "EncryptionMethod").Select(e => e.Attribute("Algorithm")?.Value ?? "");
        // Font obfuscation (IDPF and Adobe) is not DRM; anything else means the chapters themselves are encrypted.
        if (algorithms.Any(a => a != "http://www.idpf.org/2008/embedding" && a != "http://ns.adobe.com/pdf/enc#RC"))
            throw new ConversionFailure($"{name} is protected by DRM and cannot be converted.");
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>Joins a relative reference to a base folder inside the zip, resolving ".." and percent-encoding.</summary>
    internal static string Resolve(string baseDir, string href)
    {
        var parts = new List<string>();
        var combined = href.StartsWith('/') ? href.TrimStart('/') : baseDir + href;
        foreach (var part in Uri.UnescapeDataString(combined).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
