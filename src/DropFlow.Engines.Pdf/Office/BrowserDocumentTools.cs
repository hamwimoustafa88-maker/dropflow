using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Rendering;
using DropFlow.Engines.Pdf.Tools;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// PDF and pictures from HTML, text, Markdown, Word, spreadsheets, CSV/JSON and EPUB, printed by the app's browser engine
/// (<see cref="IHtmlPdfRenderer"/>). No LibreOffice: the page is built in-process by <see cref="DocumentHtml"/>, printed to a PDF, and
/// for PNG/JPG the PDF's pages are drawn with PDFium like any other PDF. Layout fidelity is that of a web page, not of Word.
/// </summary>
internal sealed class BrowserDocumentTools(IHtmlPdfRenderer renderer)
{
    /// <summary>What the browser engine prints: an HTML file as it is, or any document <see cref="DocumentHtml"/> can build.</summary>
    public static readonly IReadOnlyList<string> Inputs = ["html", .. DocumentHtml.Inputs];

    public static readonly string[] Targets = ["pdf", "png", "jpg"];

    /// <summary>True for pairs the browser engine handles (so LibreOffice is not asked for them when an engine is available).</summary>
    public static bool Handles(string input, string target) => Inputs.Contains(input) && Targets.Contains(target) && input != target;

    public IEnumerable<OperationSpec> Specs()
    {
        foreach (var target in Targets)
            yield return new OperationSpec($"convert:{target}", [.. Inputs], ctx => RunAsync(ctx, target));
    }

    private async Task<ConversionResult> RunAsync(JobContext ctx, string target)
    {
        var extension = Path.GetExtension(ctx.Input).TrimStart('.').ToLowerInvariant();
        var html = ctx.Input;
        if (extension != "html" && !(extension is "htm" or "xhtml"))
        {
            ctx.Report(0.05, "Reading the document");
            html = ctx.TempFile("page.html");
            File.WriteAllText(html, DocumentHtml.Build(ctx.Input), TextFile.Utf8);
        }
        ctx.Ct.ThrowIfCancellationRequested();

        var pdf = ctx.TempFile("render.pdf");
        var error = await renderer.PrintAsync(html, pdf, new HtmlPdfOptions(), new Progress<JobProgress>(p => ctx.Report(0.1 + p.Fraction * 0.6, p.Message)), ctx.Ct)
            .ConfigureAwait(false);
        if (error is not null) return ctx.Fail($"{Path.GetFileName(ctx.Input)}: {error}");
        ctx.Ct.ThrowIfCancellationRequested();

        if (target == "pdf")
        {
            var path = ctx.ReserveFile(ctx.Input, "pdf", "converted");
            File.Move(pdf, path, overwrite: true);
            ctx.Report(1);
            return ctx.Ok(path);
        }

        return PdfImageTools.RenderToImages(ctx, pdf, ctx.Input, target, PdfImageTools.DefaultDpi, PdfImageTools.DefaultQuality, null, "converted", "converted");
    }
}
