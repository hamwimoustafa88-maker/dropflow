using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;

namespace DropFlow.AI;

/// <summary>
/// The operating-system pieces of the AI engine, portable build (net10.0: macOS, Linux): no system OCR (Tesseract does the work),
/// PDFium page rendering, and the UltraFace ONNX model for faces. A host can inject platform engines instead (for example Apple
/// Vision through <see cref="OcrService"/>, or another <see cref="IFaceFinder"/> through <see cref="RedactAutoConverter"/>).
/// </summary>
internal static class AiPlatform
{
    /// <summary>False: this build has no operating-system OCR engine of its own.</summary>
    public const bool HasSystemOcr = false;

    public static int SystemOcrMaxImageDimension => 8000;

    public static IWindowsOcr CreateSystemOcr() => new NoSystemOcr();

    public static IWindowsOcrLanguageSource CreateOcrLanguageSource() => new NoSystemOcr();

    public static IPageRasterizer CreatePageRasterizer() => new PdfiumPageRasterizer();

    public static IFaceFinder CreateFaceFinder(ModelManager models, FaceDetector onnx) => new UltraFaceFinder(models, onnx);

    /// <summary>Reports no installed packs, so the planner always picks Tesseract; recognising directly is an error.</summary>
    private sealed class NoSystemOcr : IWindowsOcr, IWindowsOcrLanguageSource
    {
        public IReadOnlyList<string> InstalledTags() => [];

        public Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken cancellationToken) =>
            throw new OcrUnavailableException("This system has no built-in OCR engine. " + OcrPlanner.TesseractInstallHint);
    }
}
