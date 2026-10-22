using DropFlow.Engines.Pdf.Rendering;

namespace DropFlow.Engines.Web;

/// <summary>
/// "HTML or web page to PDF" printed by the Windows WebView2 renderer. The tool itself (options, output naming) is the portable
/// <see cref="HtmlToPdfConverter"/>; macOS and Linux use the same tool with <see cref="ChromiumHtmlPdfRenderer"/>. The plain conversion
/// of HTML (and of text, Markdown, Word, spreadsheets, EPUB) to PDF or pictures is <c>convert:*</c> in the PDF engine, which prints through
/// the same <see cref="WebPageRenderer"/>. Disposing the tool closes the hidden browser.
/// </summary>
public sealed class HtmlToPdfTool(WebPageRenderer renderer) : HtmlToPdfConverter(renderer);
