// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>Turns a scanned PDF into a searchable one (OCR). Implemented by DropFlow.AI with the shipped Tesseract (Arabic + English).</summary>
public interface IScannedPdfReader
{
    /// <summary>Writes a searchable copy of <paramref name="pdf"/> into <paramref name="folder"/> and returns its path.</summary>
    /// <exception cref="InvalidOperationException">OCR is not possible here (no Tesseract, no language data); the message says why.</exception>
    Task<string> MakeSearchableAsync(string pdf, string folder, IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// A scanned PDF has no text layer. Instead of asking the user to run OCR first, the text and export tools (TXT, Markdown, HTML,
/// Word, Excel, PowerPoint, EPUB, extract text) read it through OCR themselves when a reader is registered, and say so in a note.
/// </summary>
public static class ScannedPdf
{
    /// <summary>Set once by the AI module (the app and the CLI load it). Null: scans are refused with a clear message, as before.</summary>
    public static IScannedPdfReader? Reader { get; set; }

    public const string ReadWithOcrNote = "This PDF is a scan: its text was read with OCR (Arabic and English). Check names and numbers.";

    /// <summary>
    /// The PDF's text; for a scan, the text of an OCR'd copy (made in the job's temp folder). <paramref name="ocr"/> tells the caller
    /// to add <see cref="ReadWithOcrNote"/>. <paramref name="share"/> is the part of the progress bar reading may use.
    /// </summary>
    internal static PdfTextDocument Read(JobContext ctx, double share, out bool ocr, string noTextMessage)
    {
        ocr = false;
        var document = PdfTextExtractor.Read(ctx.Input, ctx.Ct, f => ctx.Report(f * share * 0.2));
        if (document.HasText) return document;
        if (Reader is not { } reader) throw new ConversionFailure(noTextMessage);

        string searchable;
        try
        {
            ctx.Report(share * 0.2, "Reading the scan with OCR");
            var progress = new Core.Operations.InlineProgress<double>(p => ctx.Report(share * (0.2 + 0.7 * p), "Reading the scan with OCR"));
            searchable = reader.MakeSearchableAsync(ctx.Input, ctx.TempFolder(), progress, ctx.Ct).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            throw new ConversionFailure($"{noTextMessage} ({ex.Message})");
        }

        var read = PdfTextExtractor.Read(searchable, ctx.Ct);
        if (!read.HasText) throw new ConversionFailure("This PDF is a scan and OCR found no text on its pages.");
        ocr = true;
        return read;
    }
}
