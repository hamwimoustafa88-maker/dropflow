using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;
using DropFlow.Engines.Pdf.Rendering;
using DropFlow.Engines.Pdf.Tools;

namespace DropFlow.Engines.Pdf;

/// <summary>
/// Entry point the app calls: every PDF, image-to-PDF, PDF-export and Office-document converter in one list.
/// The first converter is everything done in-process (no engine); the others need qpdf or LibreOffice and declare it through
/// <see cref="IConverter.EngineFor"/>. Their (operation, extension) pairs never overlap, so the registry order does not matter.
/// </summary>
public static class PdfModule
{
    /// <param name="engines">Locates qpdf and LibreOffice.</param>
    /// <param name="htmlRenderer">
    /// A browser engine that prints HTML to PDF. When given, HTML, text, Markdown, Word, spreadsheet, CSV/JSON and EPUB files become PDF
    /// and pictures in-process (no LibreOffice, nothing to download); LibreOffice is then used only for the formats a browser cannot read.
    /// </param>
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines, IHtmlPdfRenderer? htmlRenderer = null) =>
        Create(engines, new ProcessEngineRunner(), htmlRenderer);

    /// <summary>Test seam: lets tests replace the external-process layer without installing qpdf or LibreOffice.</summary>
    internal static IReadOnlyList<IConverter> Create(IEngineProvider engines, IEngineRunner runner, IHtmlPdfRenderer? htmlRenderer = null) =>
    [
        new SpecConverter(EngineId.None,
        [
            .. PdfPageTools.Specs(),
            .. PdfStampTools.Specs(),
            .. PdfCropTools.Specs(),
            .. PdfSignTools.Specs(),
            .. PdfRedactTools.Specs(),
            .. PdfCompareTools.Specs(),
            .. PdfImageTools.Specs(),
            .. PdfATools.Specs(),
            .. ImageToPdfTools.Specs(),
            .. PdfTextTools.Specs(),
            .. PdfExportTools.Specs(),
            .. TabularTools.Specs(),
            .. DocumentTools.Specs(),
            .. EpubTools.Specs(),
        ]),
        new SpecConverter(EngineId.Qpdf, new QpdfTools(engines, runner).Specs()),
        .. htmlRenderer is null ? [] : new IConverter[] { new SpecConverter(EngineId.None, new BrowserDocumentTools(htmlRenderer).Specs()) },
        new SpecConverter(EngineId.LibreOffice, new LibreOfficeTools(engines, runner, browserRenders: htmlRenderer is not null).Specs()),
    ];
}
