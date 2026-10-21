using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>EPUB to HTML, Markdown and plain text, in-process. (EPUB to PDF/DOCX/ODT/RTF goes through LibreOffice after <see cref="EpubReader"/> prepares HTML.)</summary>
internal static class EpubTools
{
    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("convert:html", "epub", ctx => Task.FromResult(ToHtml(ctx)));
        yield return OperationSpec.For("convert:md", "epub", ctx => Task.FromResult(ToMarkdown(ctx)));
        yield return OperationSpec.For("convert:txt", "epub", ctx => Task.FromResult(ToText(ctx)));
    }

    private static Core.Operations.ConversionResult ToHtml(JobContext ctx)
    {
        var book = EpubReader.Read(ctx.Input, EpubImages.Inline);
        ctx.Ct.ThrowIfCancellationRequested();
        var path = ctx.ReserveFile(ctx.Input, "html", "converted");
        File.WriteAllText(path, EpubReader.ToHtmlDocument(book), TextFile.Utf8);
        return ctx.Ok(path);
    }

    private static Core.Operations.ConversionResult ToMarkdown(JobContext ctx)
    {
        var markdown = ChaptersAsMarkdown(ctx);
        var path = ctx.ReserveFile(ctx.Input, "md", "converted");
        File.WriteAllText(path, markdown, TextFile.Utf8);
        return ctx.Ok(path);
    }

    private static Core.Operations.ConversionResult ToText(JobContext ctx)
    {
        var document = MarkdownReader.Parse(ChaptersAsMarkdown(ctx));
        var path = ctx.ReserveFile(ctx.Input, "txt", "converted");
        File.WriteAllText(path, RichTextWriter.Write(document), TextFile.Utf8WithBom);
        return ctx.Ok(path);
    }

    private static string ChaptersAsMarkdown(JobContext ctx)
    {
        var book = EpubReader.Read(ctx.Input, EpubImages.Drop);
        ctx.Ct.ThrowIfCancellationRequested();
        return DocumentTools.HtmlToMarkdown(book.BodyHtml);
    }
}
