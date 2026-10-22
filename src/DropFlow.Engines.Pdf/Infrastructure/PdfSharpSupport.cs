using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>Shared PDFsharp setup and the one place that turns "this PDF cannot be opened" into a clear user message.</summary>
internal static class PdfSharpSupport
{
    private static readonly object FontGate = new();
    private static bool _fontsConfigured;

    /// <summary>
    /// PDFsharp (core build) needs a font source before it can draw text. On Windows it reads the installed fonts, which is all
    /// DropFlow needs (watermarks and page numbers use Arial/Segoe UI that ship with Windows). On macOS and Linux the
    /// <see cref="SystemFontResolver"/> finds installed TrueType fonts and stands in for the Windows families. Safe to call repeatedly.
    /// </summary>
    public static void EnsureFonts()
    {
        lock (FontGate)
        {
            if (_fontsConfigured) return;
            if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            else if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SystemFontResolver();
            _fontsConfigured = true;
        }
    }

    /// <summary>Opens a PDF for copying pages out of it (merge, split, organize).</summary>
    public static PdfDocument OpenForImport(string path, string? password = null)
    {
        var document = Open(path, PdfDocumentOpenMode.Import, password);
        // A document that opens without a password but carries owner restrictions must not be quietly stripped of them
        // by copying its pages into a new, unprotected file.
        if (!AllowsAssembly(path))
        {
            document.Dispose();
            throw new ConversionFailure($"{Path.GetFileName(path)} has an owner password that does not allow its pages to be copied or rearranged. Unlock it first (Unlock PDF), then try again.");
        }
        return document;
    }

    /// <summary>Opens a PDF for editing in memory (rotate, metadata, watermark); the caller saves to a new file.</summary>
    public static PdfDocument OpenForModify(string path, string? password = null) => Open(path, PdfDocumentOpenMode.Modify, password);

    /// <summary>
    /// Reads the owner-set permission flags with PDFium (PDFsharp does not expose them for opened files). Unencrypted files allow everything;
    /// if PDFium cannot open the file the answer is "allowed" and PDFsharp reports any real problem itself.
    /// </summary>
    private static bool AllowsAssembly(string path)
    {
        try
        {
            using var pdfium = PdfiumDocument.Open(path);
            return pdfium.AllowsAssembly;
        }
        catch (ConversionFailure)
        {
            return true;
        }
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password)
    {
        var name = Path.GetFileName(path);
        // Read into memory first: no lock is held on the user's file, and Unicode paths never reach PDFsharp.
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        try
        {
            return password is null ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
        }
        catch (PdfReaderException ex) when (LooksEncrypted(ex.Message))
        {
            throw new ConversionFailure($"{name} is protected with a password. Unlock it first (Unlock PDF), then try again.");
        }
        catch (PdfReaderException ex)
        {
            throw new ConversionFailure($"{name} is not a valid PDF or is damaged ({ex.Message}). Try Repair PDF first.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotImplementedException or NotSupportedException or IndexOutOfRangeException or FormatException)
        {
            throw new ConversionFailure($"{name} is not a valid PDF or is damaged ({ex.Message}). Try Repair PDF first.");
        }
    }

    private static bool LooksEncrypted(string message) =>
        message.Contains("password", StringComparison.OrdinalIgnoreCase) || message.Contains("encrypt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rotates a page clockwise by a multiple of 90 degrees, keeping /Rotate within 0..270.</summary>
    public static void RotateBy(PdfPage page, int degrees) => page.Rotate = (((page.Rotate + degrees) % 360) + 360) % 360;

    public static void ApplyDefaultOptions(PdfDocument document)
    {
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;
        document.Options.NoCompression = false;
        document.Options.CompressContentStreams = true;
    }
}
