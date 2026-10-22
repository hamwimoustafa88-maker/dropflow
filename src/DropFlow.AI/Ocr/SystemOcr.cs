namespace DropFlow.AI.Ocr;

/// <summary>An expected OCR failure (missing language pack, missing engine, unreadable image). The converters turn it into a failed result.</summary>
public sealed class OcrUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The operating system's own OCR engine. On Windows it is <c>WindowsOcr</c> (<c>Windows.Media.Ocr</c>); on macOS and Linux the
/// default reports that there is none, so <see cref="OcrPlanner"/> uses Tesseract. A host may pass another implementation (for example
/// Apple Vision on macOS) to <see cref="OcrService"/> together with a matching <see cref="IWindowsOcrLanguageSource"/>.
/// </summary>
public interface IWindowsOcr
{
    /// <param name="pngPath">An upright, opaque image (see <see cref="OcrImagePreparer"/>).</param>
    /// <param name="languageTag">BCP-47 tag of an installed pack, or null for the user's profile languages.</param>
    Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken cancellationToken);
}
