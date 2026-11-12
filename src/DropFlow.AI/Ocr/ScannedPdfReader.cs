// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Extraction;

namespace DropFlow.AI.Ocr;

/// <summary>
/// Lets the PDF text and export tools read a scanned PDF: makes a searchable copy with <see cref="PdfOcrConverter"/> (shipped
/// Tesseract, Arabic + English) in a folder the caller deletes, so converting a scan to Word or text no longer means "run OCR first".
/// </summary>
public sealed class ScannedPdfReader(PdfOcrConverter ocr) : IScannedPdfReader
{
    public async Task<string> MakeSearchableAsync(string pdf, string folder, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var job = new ConversionJob([pdf], AiOperations.PdfOcr, new Dictionary<string, object?> { [PdfOcrConverter.LanguageKey] = "auto" }, folder);
        var result = await ocr.RunAsync(job, new InlineProgress<JobProgress>(p => progress.Report(p.Fraction)), cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Outputs.Count == 0) throw new InvalidOperationException(result.Error ?? "OCR produced no file.");
        return result.Outputs[0];
    }
}
