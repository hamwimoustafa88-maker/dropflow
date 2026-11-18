// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>
/// E-books in-process: EPUB, FB2, MOBI/AZW3 (read only), HTMLZ and TXTZ to HTML, Markdown, plain text, and to each other (EPUB, FB2, HTMLZ,
/// TXTZ). PDF and pictures go through the browser engine (<see cref="BrowserDocumentTools"/>), Word/ODT/RTF through LibreOffice.
/// </summary>
internal static class BookTools
{
    /// <summary>Targets written here, for every e-book input except itself.</summary>
    public static readonly string[] Targets = ["html", "md", "txt", .. BookFormats.Outputs];

    public static IEnumerable<OperationSpec> Specs()
    {
        foreach (var target in Targets)
        {
            var inputs = BookFormats.Inputs.Where(i => i != target).ToArray();
            yield return new OperationSpec($"convert:{target}", inputs, ctx => Task.FromResult(Convert(ctx, target)));
        }
    }

    private static ConversionResult Convert(JobContext ctx, string target)
    {
        ctx.Report(0.05, "Reading the book");
        var images = target is "md" or "txt" ? EpubImages.Drop : EpubImages.Inline;
        var book = BookReader.Read(ctx.Input, images);
        ctx.Ct.ThrowIfCancellationRequested();
        ctx.Report(0.5, "Writing");

        var path = ctx.ReserveFile(ctx.Input, target, "converted");
        switch (target)
        {
            case "html":
                File.WriteAllText(path, EpubReader.ToHtmlDocument(book), TextFile.Utf8);
                break;
            case "md":
                File.WriteAllText(path, DocumentTools.HtmlToMarkdown(book.BodyHtml), TextFile.Utf8);
                break;
            case "txt":
                File.WriteAllText(path, RichTextWriter.Write(MarkdownReader.Parse(DocumentTools.HtmlToMarkdown(book.BodyHtml))), TextFile.Utf8WithBom);
                break;
            case "epub":
                BookEpubWriter.Write(book, path);
                break;
            case "fb2":
                Fb2.Write(book, path);
                break;
            case "htmlz":
                ZippedBooks.WriteHtmlz(book, path);
                break;
            case "txtz":
                ZippedBooks.WriteTxtz(book, path);
                break;
            default:
                throw new ConversionFailure($"Cannot write .{target} books.");
        }
        ctx.Report(1);
        return ctx.Ok(path);
    }
}
