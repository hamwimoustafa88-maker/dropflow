using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;

namespace DropFlow.AI;

/// <summary>
/// The operating-system pieces of the AI engine, Windows build (net10.0-windows): Windows OCR, the Windows PDF renderer and Windows
/// face detection, none of which needs a download. The macOS/Linux build of this class lives in <c>Platform/Unix</c>.
/// </summary>
internal static class AiPlatform
{
    /// <summary>True when the operating system has its own OCR engine (Windows.Media.Ocr).</summary>
    public const bool HasSystemOcr = true;

    public static int SystemOcrMaxImageDimension => (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;

    public static IWindowsOcr CreateSystemOcr() => new WindowsOcr();

    public static IWindowsOcrLanguageSource CreateOcrLanguageSource() => new WindowsOcrLanguageSource();

    public static IPageRasterizer CreatePageRasterizer() => new WindowsPdfPageRasterizer();

    /// <summary>Windows face detection first (built in, nothing to download); the UltraFace model when Windows cannot detect faces.</summary>
    public static IFaceFinder CreateFaceFinder(ModelManager models, FaceDetector onnx) =>
        new FirstAvailableFaceFinder(new WindowsFaceFinder(), new UltraFaceFinder(models, onnx));
}
