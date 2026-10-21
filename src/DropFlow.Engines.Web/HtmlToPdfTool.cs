using System.IO;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Pdf.Rendering;

namespace DropFlow.Engines.Web;

/// <summary>
/// "HTML or web page to PDF" with options (paper, orientation, backgrounds, online resources). The plain conversion of HTML (and of
/// text, Markdown, Word, spreadsheets, EPUB) to PDF or pictures is <c>convert:*</c> in the PDF engine, which prints through the same
/// <see cref="WebPageRenderer"/>. Disposing the tool closes the hidden browser.
/// </summary>
public sealed class HtmlToPdfTool(WebPageRenderer renderer) : IConverter, IDisposable
{
    public const string ToolId = "pdf.html-to-pdf";

    private static readonly string[] HtmlExtensions = ["html", "htm", "xhtml"];

    private readonly OutputPathResolver _paths = new();

    public IReadOnlyCollection<string> Operations => [ToolId];

    public bool Supports(string operation, string inputExtension) => operation == ToolId && HtmlExtensions.Contains(inputExtension);

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("paper", ToolOptionKind.Choice, "a4", ["a4", "letter"]),
        new("landscape", ToolOptionKind.Bool, false),
        new("background", ToolOptionKind.Bool, true),
        new("online", ToolOptionKind.Bool, false),
    ];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var input = job.Inputs[0];
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        var options = new HtmlPdfOptions(
            Paper: job.GetString("paper", "a4")!,
            Landscape: job.GetBool("landscape"),
            Backgrounds: job.GetBool("background", true),
            AllowOnline: job.GetBool("online"));
        var output = _paths.ForFile(input, "pdf", "converted", job.OutputDirectory);

        var error = await renderer.PrintAsync(input, output, options, progress, cancellationToken);
        return error is null ? ConversionResult.Ok([output], DateTime.UtcNow - started) : ConversionResult.Fail(error);
    }

    public void Dispose() => renderer.Dispose();
}
