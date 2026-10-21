using System.Net;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// Turns a document into one self-contained HTML page (text, Markdown, Word, spreadsheet/CSV/JSON, EPUB) so a browser engine can print
/// it. Everything is read in-process; nothing here needs LibreOffice. Relative links (Markdown images) resolve against the source folder
/// through a <c>&lt;base&gt;</c> element.
/// </summary>
internal static class DocumentHtml
{
    /// <summary>Extensions this class can turn into HTML (HTML itself needs no preparation and is not listed).</summary>
    public static readonly IReadOnlyList<string> Inputs = ["txt", "md", "docx", "csv", "tsv", "xlsx", "json", "epub"];

    public static bool Supports(string extension) => Inputs.Contains(extension.TrimStart('.').ToLowerInvariant());

    /// <summary>Builds the page. Throws <see cref="ConversionFailure"/> for a file that cannot be read.</summary>
    public static string Build(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        var html = extension switch
        {
            "txt" => Rich(PlainTextReader.Read(path), title),
            "docx" => Rich(DocxReader.Read(path), title),
            "md" => DocumentTools.MarkdownToHtml(TextFile.Read(path), title),
            "csv" or "tsv" or "xlsx" or "json" => Tabular.ToHtml(Tabular.Read(path), title),
            "epub" => EpubReader.ToHtmlDocument(EpubReader.Read(path, EpubImages.Inline)),
            _ => throw new ConversionFailure($"{Path.GetFileName(path)}: .{extension} cannot be turned into a page."),
        };
        return WithBase(html, folder);
    }

    private static string Rich(RichDocument document, string fileTitle) =>
        RichHtmlWriter.Write(document, document.Title ?? fileTitle, DocumentTools.LanguageOf(document));

    private static string WithBase(string html, string folder)
    {
        if (folder.Length == 0) return html;
        var tag = $"<base href=\"{WebUtility.HtmlEncode(new Uri(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri)}\">";
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? tag + html : html.Insert(head + "<head>".Length, "\n" + tag);
    }
}
