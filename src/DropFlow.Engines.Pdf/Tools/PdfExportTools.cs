using DropFlow.Engines.Pdf.Export;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>PDF to Word, Excel, PowerPoint and EPUB, built in-process from the text layer (and page pictures for PowerPoint).</summary>
internal static class PdfExportTools
{
    private const string NoTextMessage =
        "This PDF has no text layer (it is probably a scan). Run OCR on it first, then convert it.";

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("convert:docx", "pdf", ctx => Task.FromResult(ToDocx(ctx)));
        yield return OperationSpec.For("convert:xlsx", "pdf", ctx => Task.FromResult(ToXlsx(ctx)));
        yield return OperationSpec.For("convert:pptx", "pdf", ctx => Task.FromResult(ToPptx(ctx)));
        yield return OperationSpec.For("convert:epub", "pdf", ctx => Task.FromResult(ToEpub(ctx)));
    }

    private static PdfTextDocument Read(JobContext ctx, bool requireText = true)
    {
        var document = PdfTextExtractor.Read(ctx.Input, ctx.Ct, f => ctx.Report(f * 0.6));
        if (requireText && !document.HasText) throw new ConversionFailure(NoTextMessage);
        return document;
    }

    private static string TitleOf(PdfTextDocument document, JobContext ctx) =>
        document.Title ?? Path.GetFileNameWithoutExtension(ctx.Input);

    private static Core.Operations.ConversionResult ToDocx(JobContext ctx)
    {
        var document = Read(ctx);
        var path = ctx.ReserveFile(ctx.Input, "docx", "converted");
        DocxWriter.Write(document, path, TitleOf(document, ctx), PdfTextTools.Language(document));
        ctx.Report(1, "Converted to Word. Layout, fonts and spacing may differ from the PDF.");
        return ctx.Ok(path);
    }

    private static Core.Operations.ConversionResult ToXlsx(JobContext ctx)
    {
        var document = Read(ctx);
        var path = ctx.ReserveFile(ctx.Input, "xlsx", "converted");
        var sheets = XlsxWriter.Write(document, path);
        ctx.Report(1, sheets == 1 ? "1 sheet written." : $"{sheets} sheets written. Only text and tables are converted; check numbers and columns.");
        return ctx.Ok(path);
    }

    private static Core.Operations.ConversionResult ToPptx(JobContext ctx)
    {
        var document = Read(ctx, requireText: false); // a scanned PDF still makes picture slides
        var path = ctx.ReserveFile(ctx.Input, "pptx", "converted");
        PptxWriter.Write(ctx.Input, document, path, TitleOf(document, ctx), ctx.Ct, f => ctx.Report(0.6 + 0.4 * f));
        ctx.Report(1, "Each slide shows one PDF page as a picture; the text is searchable but not editable.");
        return ctx.Ok(path);
    }

    private static Core.Operations.ConversionResult ToEpub(JobContext ctx)
    {
        var document = Read(ctx);
        var path = ctx.ReserveFile(ctx.Input, "epub", "converted");
        EpubWriter.Write(document, path, TitleOf(document, ctx), PdfTextTools.Language(document));
        ctx.Report(1, "Converted to EPUB. Pictures and page layout are not carried over.");
        return ctx.Ok(path);
    }
}
