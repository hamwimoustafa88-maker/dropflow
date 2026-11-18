// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Office;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>The e-book formats this module reads and writes.</summary>
internal static class BookFormats
{
    /// <summary>Text e-books: read in-process into one HTML body (see <see cref="BookReader"/>).</summary>
    public static readonly string[] Inputs = ["epub", "fb2", "mobi", "azw3", "htmlz", "txtz"];

    /// <summary>Text e-book formats this module writes in-process.</summary>
    public static readonly string[] Outputs = ["epub", "fb2", "htmlz", "txtz"];

    /// <summary>Comic book archives: pictures in a zip (cbz), rar (cbr) or 7z (cb7).</summary>
    public static readonly string[] Comics = ["cbz", "cbr", "cb7"];

    public static bool IsBook(string extension) => Inputs.Contains(extension);
}

/// <summary>Language helpers: right-to-left scripts and a language guess for books that do not declare one.</summary>
internal static class BookLanguage
{
    private static readonly string[] RightToLeft = ["ar", "he", "iw", "fa", "ur", "ps", "yi", "ckb", "sd", "ug", "dv", "syr"];

    /// <summary>True for a language written right to left ("ar", "ar-SA", "fa-IR"...).</summary>
    public static bool IsRtl(string? language) =>
        !string.IsNullOrWhiteSpace(language) && RightToLeft.Contains(language.Split('-', '_')[0].Trim().ToLowerInvariant());

    /// <summary>The primary subtag in lower case ("ar-SA" -> "ar"), or "en" for nothing.</summary>
    public static string Primary(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "en" : language.Split('-', '_')[0].Trim().ToLowerInvariant();

    /// <summary>Guesses the language of HTML or text from its letters (Arabic, Hebrew, otherwise English).</summary>
    public static string Detect(string htmlOrText)
    {
        var text = Regex.Replace(htmlOrText.Length > 200_000 ? htmlOrText[..200_000] : htmlOrText, "<[^>]*>", " ");
        text = Regex.Replace(text, @"data:[^\s""')]+", " ");
        return DocumentTools.LanguageOf(text);
    }
}

/// <summary>
/// HTML shared by the e-book readers and writers. Readers hand their chapters to <see cref="Clean"/> (the same rules as the EPUB reader:
/// no scripts, styles, forms or event handlers; only safe links), with pictures already turned into data: URIs. Writers take pictures out
/// of the body again with <see cref="ExtractImages"/>.
/// </summary>
internal static partial class BookHtml
{
    /// <summary>
    /// Cleans one chapter's HTML. <paramref name="resolveImage"/> maps an img src to a data: URI (or null to drop the picture and keep its
    /// alt text); <paramref name="rewriteLink"/> maps an href (null drops the link and keeps its text).
    /// </summary>
    public static string Clean(string html, Func<string, string?> resolveImage, Func<string, string?>? rewriteLink = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        CleanDocument(document, resolveImage, rewriteLink ?? DefaultLink);
        return document.Body?.InnerHtml ?? "";
    }

    internal static void CleanDocument(IDocument document, Func<string, string?> resolveImage, Func<string, string?> rewriteLink)
    {
        foreach (var svg in document.QuerySelectorAll("svg").ToList())
        {
            var href = svg.QuerySelectorAll("image").FirstOrDefault()?.Attributes.FirstOrDefault(a => a.LocalName == "href")?.Value;
            if (href is null) { svg.Remove(); continue; }
            var img = document.CreateElement("img");
            img.SetAttribute("src", href);
            svg.Replace(img);
        }

        foreach (var tag in EpubReader.RemovedElements)
            foreach (var element in document.QuerySelectorAll(tag).ToList()) element.Remove();

        foreach (var element in document.QuerySelectorAll("*").ToList())
            foreach (var attribute in element.Attributes.ToList())
                if (!EpubReader.KeptAttributes.Contains(attribute.Name)) element.RemoveAttribute(attribute.Name);

        foreach (var link in document.QuerySelectorAll("a[href]").ToList())
        {
            var rewritten = rewriteLink(link.GetAttribute("href") ?? "");
            if (rewritten is null) link.RemoveAttribute("href");
            else link.SetAttribute("href", rewritten);
        }

        foreach (var img in document.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src") ?? "";
            var resolved = src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ? src : resolveImage(src);
            if (resolved is not null)
            {
                img.SetAttribute("src", resolved);
                continue;
            }
            var alt = img.GetAttribute("alt");
            if (!string.IsNullOrWhiteSpace(alt)) img.Replace(document.CreateTextNode(alt));
            else img.Remove();
        }
    }

    /// <summary>Keeps in-page anchors and ordinary web/mail links; anything else (javascript:, file:, other files) loses its link.</summary>
    public static string? DefaultLink(string href)
    {
        href = href.Trim();
        if (href.StartsWith('#')) return href.Length > 1 ? href : null;
        var colon = href.IndexOf(':');
        if (colon > 0 && !href[..colon].Contains('/'))
            return href[..colon].ToLowerInvariant() is "http" or "https" or "mailto" ? href : null;
        return null;
    }

    /// <summary>A data: URI for picture bytes, or null when they are not a picture a book can show.</summary>
    public static string? DataUri(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > EpubReader.MaxImageBytes) return null;
        var mime = SniffImage(bytes);
        return mime is null ? null : $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>The picture type from its first bytes: JPEG, PNG, GIF, BMP or WebP.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
        if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 5 && b[0] == '<' && (b[1] == 's' || b[1] == '?')) return null; // SVG text is not inlined from binary records
        return null;
    }

    public static string Extension(string mime) => mime switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        _ => "bin",
    };

    [GeneratedRegex(@"^data:(?<mime>image/[a-z0-9.+-]+);base64,(?<data>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DataUriPattern();

    /// <summary>Decodes a base64 data: URI picture; null for anything else.</summary>
    public static (string Mime, byte[] Bytes)? FromDataUri(string src)
    {
        var match = DataUriPattern().Match(src.Trim());
        if (!match.Success) return null;
        try
        {
            return (match.Groups["mime"].Value.ToLowerInvariant(), Convert.FromBase64String(match.Groups["data"].Value));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces every data: URI picture in <paramref name="bodyHtml"/> with a file name chosen by <paramref name="name"/> (called once per
    /// distinct picture with its index, type and bytes). Returns the new HTML.
    /// </summary>
    public static string ExtractImages(string bodyHtml, Func<int, string, byte[], string?> name)
    {
        var document = new HtmlParser().ParseDocument("<body>" + bodyHtml + "</body>");
        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var img in document.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src") ?? "";
            if (!seen.TryGetValue(src, out var target))
            {
                target = FromDataUri(src) is { } picture ? name(seen.Count + 1, picture.Mime, picture.Bytes) : null;
                seen[src] = target;
            }
            if (target is not null)
            {
                img.SetAttribute("src", target);
                continue;
            }
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || src.Length == 0)
            {
                var alt = img.GetAttribute("alt");
                if (!string.IsNullOrWhiteSpace(alt)) img.Replace(document.CreateTextNode(alt));
                else img.Remove();
            }
        }
        return document.Body!.InnerHtml;
    }

    /// <summary>Applies an image mode to a body whose pictures are data: URIs (what the non-EPUB readers produce).</summary>
    public static string ApplyImageMode(string bodyHtml, EpubImages mode, string? folder)
    {
        switch (mode)
        {
            case EpubImages.Inline:
                return bodyHtml;
            case EpubImages.Drop:
                return ExtractImages(bodyHtml, (_, _, _) => null);
            default:
                if (folder is null) return bodyHtml;
                Directory.CreateDirectory(folder);
                var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return ExtractImages(bodyHtml, (index, mime, bytes) =>
                {
                    var fileName = $"image{index}.{Extension(mime)}";
                    File.WriteAllBytes(Path.Combine(folder, fileName), bytes);
                    return folderName + "/" + fileName;
                });
        }
    }

    /// <summary>A chapter of a book body: its id (from the &lt;section&gt;), its title (first heading) and its inner HTML.</summary>
    internal sealed record Chapter(string Id, string Title, string Html);

    /// <summary>
    /// Splits a book body into chapters: one per top-level &lt;section&gt;. A body without sections (or with one section that holds several
    /// level-1 headings) is cut before each &lt;h1&gt;.
    /// </summary>
    public static List<Chapter> Chapters(string bodyHtml, string bookTitle)
    {
        var document = new HtmlParser().ParseDocument("<body>" + bodyHtml + "</body>");
        IElement root = document.Body!;
        // Look through wrappers: a body that is one <div> or <section> around everything.
        while (root.Children.Length == 1 && root.Children[0].LocalName is "section" or "div" or "article" or "main"
               && root.ChildNodes.All(n => n is IElement || string.IsNullOrWhiteSpace(n.TextContent)))
            root = root.Children[0];

        var sections = root.Children.Where(c => c.LocalName == "section").ToList();
        var hasOtherContent = root.ChildNodes.Any(n => n is IElement e ? e.LocalName != "section" : !string.IsNullOrWhiteSpace(n.TextContent));
        var parts = sections.Count > 1 && !hasOtherContent
            ? sections.Select(s => ((string?)s.Id, s.InnerHtml)).ToList()
            : SplitAtHeadings(root);

        var chapters = new List<Chapter>();
        foreach (var (id, html) in parts)
        {
            if (string.IsNullOrWhiteSpace(StripTags(html)) && !html.Contains("<img", StringComparison.OrdinalIgnoreCase)) continue;
            var fragment = new HtmlParser().ParseDocument("<body>" + html + "</body>");
            var heading = fragment.QuerySelector("h1, h2, h3")?.TextContent.Trim();
            var title = string.IsNullOrWhiteSpace(heading) ? (chapters.Count == 0 ? bookTitle : $"{chapters.Count + 1}") : Collapse(heading);
            chapters.Add(new Chapter(id ?? $"ch{chapters.Count + 1}", title, html));
        }
        if (chapters.Count == 0) chapters.Add(new Chapter("ch1", bookTitle, "<p></p>"));
        return chapters;
    }

    private static List<(string? Id, string Html)> SplitAtHeadings(IElement root)
    {
        var parts = new List<(string?, string)>();
        var current = new StringBuilder();
        // Cut at the top heading level used at least twice (h1, else h2).
        var level = root.Children.Count(c => c.LocalName == "h1") >= 2 ? "h1" : root.Children.Count(c => c.LocalName == "h2") >= 2 ? "h2" : null;
        foreach (var node in root.ChildNodes)
        {
            if (level is not null && node is IElement element0 && element0.LocalName == level && current.Length > 0 && !string.IsNullOrWhiteSpace(StripTags(current.ToString())))
            {
                parts.Add((null, current.ToString()));
                current.Clear();
            }
            current.Append(node is IElement element ? element.OuterHtml : WebUtility.HtmlEncode(node.TextContent));
        }
        if (current.Length > 0) parts.Add((null, current.ToString()));
        return parts;
    }

    private static string StripTags(string html) => Regex.Replace(html, "<[^>]*>", "");

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The plain text of an HTML fragment (tags removed, entities decoded, whitespace collapsed).</summary>
    public static string Text(string html) => Collapse(WebUtility.HtmlDecode(StripTags(html)));
}
