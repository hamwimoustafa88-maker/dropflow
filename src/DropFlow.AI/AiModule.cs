using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.AI.Speech;
using DropFlow.AI.Translation;
using DropFlow.Core.Operations;

namespace DropFlow.AI;

/// <summary>
/// Entry point of the local-AI engine. The app composes <see cref="Create(IEngineProvider, ModelManager)"/> with the other modules
/// into one tool registry.
/// <list type="bullet">
/// <item><c>aud.transcribe</c>, <c>vid.transcribe</c>: Whisper speech to text (needs ffmpeg and a downloaded Whisper model).</item>
/// <item><c>img.ocr</c>: Windows OCR with Tesseract as fallback (no download for English; Arabic needs the Windows pack or Tesseract); Tesseract only on macOS and Linux.</item>
/// <item><c>pdf.ocr</c>: searchable PDF through Tesseract; pages are rendered by an <see cref="IPageRasterizer"/>.</item>
/// <item><c>img.remove-bg</c>: ONNX segmentation model (u2netp or birefnet-lite from the model manager).</item>
/// <item><c>img.redact-auto</c>: faces (Windows face detection, else the UltraFace model) and text (OCR) covered with solid boxes or blur.</item>
/// <item><c>txt.translate</c>, <c>img.translate</c>, <c>pdf.translate</c>: English and Arabic translation with the Opus-MT models (ONNX Runtime, CPU), for text, Markdown, HTML, subtitles, pictures (OCR first) and PDFs with a text layer.</item>
/// </list>
/// Nothing here downloads anything. Models are fetched only by <see cref="ModelManager.DownloadAsync"/>, which the app calls after
/// the user agrees; converters that lack a model fail with "Model 'id' is not installed" (see <see cref="AiFailures.TryParseMissingModel"/>).
/// <para>
/// Loaded models are released after 2 idle minutes. Converters that own native resources implement <see cref="IDisposable"/>;
/// the app should dispose the returned converters on exit.
/// </para>
/// </summary>
public static class AiModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines, ModelManager models) =>
        Create(engines, models, rasterizer: null);

    /// <param name="rasterizer">
    /// Page renderer for <c>pdf.ocr</c>. Null uses the platform default: the renderer built into Windows (<c>WindowsPdfPageRasterizer</c>),
    /// or <see cref="PdfiumPageRasterizer"/> on macOS and Linux. It can also be replaced later through <see cref="PdfOcrConverter.Rasterizer"/>.
    /// </param>
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines, ModelManager models, IPageRasterizer? rasterizer)
    {
        var ocr = new OcrService(engines);
        var sessions = new OnnxSessionCache();
        var translator = new MarianTextTranslator(models);
        return
        [
            new TranscribeConverter(engines, models),
            new OcrConverter(ocr),
            new PdfOcrConverter(engines, rasterizer),
            new BackgroundRemovalConverter(models, sessions),
            new RedactAutoConverter(models, ocr, sessions),
            new TextTranslateConverter(translator, ownsTranslator: true),   // disposes the translator (and the loaded models) with the converters
            new ImageTranslateConverter(ocr, translator),
            new PdfTranslateConverter(translator),
        ];
    }
}
