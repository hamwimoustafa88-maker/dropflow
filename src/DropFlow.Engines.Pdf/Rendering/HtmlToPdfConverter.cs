using DropFlow.Core.Operations;
using DropFlow.Core.Output;

namespace DropFlow.Engines.Pdf.Rendering;

/// <summary>
/// "HTML or web page to PDF" (<c>pdf.html-to-pdf</c>) with options (paper, orientation, backgrounds, online resources), printed by any
/// <see cref="IHtmlPdfRenderer"/>: WebView2 on Windows (<c>DropFlow.Engines.Web</c>), <see cref="ChromiumHtmlPdfRenderer"/> on macOS and
/// Linux, or a host's own engine (for example WKWebView). Disposing the tool disposes the renderer when it is disposable.
/// </summary>
public class HtmlToPdfConverter(IHtmlPdfRenderer renderer) : IConverter, IDisposable
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

        var error = await renderer.PrintAsync(input, output, options, progress, cancellationToken).ConfigureAwait(false);
        return error is null ? ConversionResult.Ok([output], DateTime.UtcNow - started) : ConversionResult.Fail(error);
    }

    public void Dispose()
    {
        (renderer as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
