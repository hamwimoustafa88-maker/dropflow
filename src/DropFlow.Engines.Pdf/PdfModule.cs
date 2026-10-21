using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;
using DropFlow.Engines.Pdf.Tools;

namespace DropFlow.Engines.Pdf;

/// <summary>
/// Entry point the app calls: every PDF, image-to-PDF, PDF-export and Office-document converter in one list.
/// The first converter is everything done in-process (no engine); the others need qpdf or LibreOffice and declare it through
/// <see cref="IConverter.EngineFor"/>. Their (operation, extension) pairs never overlap, so the registry order does not matter.
/// </summary>
public static class PdfModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) => Create(engines, new ProcessEngineRunner());

    /// <summary>Test seam: lets tests replace the external-process layer without installing qpdf or LibreOffice.</summary>
    internal static IReadOnlyList<IConverter> Create(IEngineProvider engines, IEngineRunner runner) =>
    [
        new SpecConverter(EngineId.None,
        [
            .. PdfPageTools.Specs(),
            .. PdfStampTools.Specs(),
            .. PdfImageTools.Specs(),
            .. ImageToPdfTools.Specs(),
            .. PdfTextTools.Specs(),
            .. PdfExportTools.Specs(),
            .. TabularTools.Specs(),
            .. DocumentTools.Specs(),
            .. EpubTools.Specs(),
        ]),
        new SpecConverter(EngineId.Qpdf, new QpdfTools(engines, runner).Specs()),
        new SpecConverter(EngineId.LibreOffice, new LibreOfficeTools(engines, runner).Specs()),
    ];
}
