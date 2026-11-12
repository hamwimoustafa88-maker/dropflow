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

    /// <summary>The text layer; a scan is read with OCR when DropFlow.AI registered a reader (<paramref name="ocr"/> is then true).</summary>
    private static PdfTextDocument Read(JobContext ctx, out bool ocr, bool requireText = true)
    {
        if (requireText) return ScannedPdf.Read(ctx, 0.6, out ocr, NoTextMessage);
        try { return ScannedPdf.Read(ctx, 0.6, out ocr, NoTextMessage); }
        catch (ConversionFailure)
        {
            ocr = false;   // PowerPoint still makes picture slides from a scan that cannot be read
            return PdfTextExtractor.Read(ctx.Input, ctx.Ct, f => ctx.Report(f * 0.6));
        }
    }

    private static Core.Operations.ConversionResult Done(JobContext ctx, string path, bool ocr) =>
        ocr ? ctx.OkWithNote(ScannedPdf.ReadWithOcrNote, path) : ctx.Ok(path);

    private static string TitleOf(PdfTextDocument document, JobContext ctx) =>
        document.Title ?? Path.GetFileNameWithoutExtension(ctx.Input);

    private static Core.Operations.ConversionResult ToDocx(JobContext ctx)
    {
        var document = Read(ctx, out var ocr);
        var path = ctx.ReserveFile(ctx.Input, "docx", "converted");
        DocxWriter.Write(document, path, TitleOf(document, ctx), PdfTextTools.Language(document));
        ctx.Report(1, "Converted to Word. Layout, fonts and spacing may differ from the PDF.");
        return Done(ctx, path, ocr);
    }

    private static Core.Operations.ConversionResult ToXlsx(JobContext ctx)
    {
        var document = Read(ctx, out var ocr);
        var path = ctx.ReserveFile(ctx.Input, "xlsx", "converted");
        var sheets = XlsxWriter.Write(document, path);
        ctx.Report(1, sheets == 1 ? "1 sheet written." : $"{sheets} sheets written. Only text and tables are converted; check numbers and columns.");
        return Done(ctx, path, ocr);
    }

    private static Core.Operations.ConversionResult ToPptx(JobContext ctx)
    {
        var document = Read(ctx, out var ocr, requireText: false); // a scanned PDF still makes picture slides
        var path = ctx.ReserveFile(ctx.Input, "pptx", "converted");
        PptxWriter.Write(ctx.Input, document, path, TitleOf(document, ctx), ctx.Ct, f => ctx.Report(0.6 + 0.4 * f));
        ctx.Report(1, "Each slide shows one PDF page as a picture; the text is searchable but not editable.");
        return Done(ctx, path, ocr);
    }

    private static Core.Operations.ConversionResult ToEpub(JobContext ctx)
    {
        var document = Read(ctx, out var ocr);
        var path = ctx.ReserveFile(ctx.Input, "epub", "converted");
        EpubWriter.Write(document, path, TitleOf(document, ctx), PdfTextTools.Language(document));
        ctx.Report(1, "Converted to EPUB. Pictures and page layout are not carried over.");
        return Done(ctx, path, ocr);
    }
}
