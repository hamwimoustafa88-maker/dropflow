using DropFlow.Core.Operations;

namespace DropFlow.Engines.Pdf.Rendering;

/// <param name="Paper">"a4" or "letter".</param>
/// <param name="AllowOnline">Let the page load remote resources (images, fonts, CSS). Off by default: a document must not phone home.</param>
/// <param name="PageWidth">A custom page width in inches (with <paramref name="PageHeight"/>); null = the paper size.</param>
/// <param name="NoMargins">Print edge to edge (an SVG page is exactly the drawing).</param>
public sealed record HtmlPdfOptions(string Paper = "a4", bool Landscape = false, bool Backgrounds = true, bool AllowOnline = false,
    double? PageWidth = null, double? PageHeight = null, bool NoMargins = false);

/// <summary>
/// Prints an HTML file to a PDF with a real browser engine. The PDF engine itself has none; the app supplies one (the WebView2-based
/// renderer in <c>DropFlow.Engines.Web</c>). It is what lets Word, Markdown, text, spreadsheet and EPUB files become PDF or pictures
/// with nothing to download.
/// </summary>
public interface IHtmlPdfRenderer
{
    /// <summary>Writes <paramref name="outputPath"/>. Returns null on success, or a short, user-facing reason on an expected failure.</summary>
    Task<string?> PrintAsync(string htmlPath, string outputPath, HtmlPdfOptions options, IProgress<JobProgress> progress, CancellationToken cancellationToken);
}
