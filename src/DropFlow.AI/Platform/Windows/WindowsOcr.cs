using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace DropFlow.AI.Ocr;

/// <summary>An expected OCR failure (missing language pack, missing engine, unreadable image). The converters turn it into a failed result.</summary>
public sealed class OcrUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IWindowsOcr
{
    /// <param name="pngPath">An upright, opaque image (see <see cref="OcrImagePreparer"/>).</param>
    /// <param name="languageTag">BCP-47 tag of an installed pack, or null for the user's profile languages.</param>
    Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken cancellationToken);
}

/// <summary>Windows 10/11 built-in OCR (<c>Windows.Media.Ocr</c>): on-device, no download, languages come from installed OCR packs.</summary>
public sealed class WindowsOcr : IWindowsOcr
{
    public async Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken cancellationToken)
    {
        try
        {
            var engine = CreateEngine(languageTag)
                ?? throw new OcrUnavailableException(languageTag is null
                    ? "Windows OCR has no language pack for your Windows language and no English fallback."
                    : $"Windows OCR language '{languageTag}' is not installed.");

            var file = await StorageFile.GetFileFromPathAsync(pngPath).AsTask(cancellationToken).ConfigureAwait(false);
            using var stream = await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            if (decoder.PixelWidth > OcrEngine.MaxImageDimension || decoder.PixelHeight > OcrEngine.MaxImageDimension)
                throw new OcrUnavailableException($"The image is larger than Windows OCR allows ({OcrEngine.MaxImageDimension} px per side).");

            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask(cancellationToken).ConfigureAwait(false);
            var recognised = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

            var lines = new List<OcrLine>();
            foreach (var line in recognised.Lines)
            {
                // Rebuild each line from the word boxes so reading order never depends on how the engine listed them.
                var words = line.Words
                    .Select(w => new OcrWord(w.Text, new OcrRect(w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height)))
                    .ToList();
                var built = OcrOrdering.BuildLine(words);
                if (built.Words.Count > 0) lines.Add(built);
            }
            return new OcrResult("windows", engine.RecognizerLanguage.LanguageTag, (int)decoder.PixelWidth, (int)decoder.PixelHeight, lines);
        }
        catch (COMException ex)
        {
            throw new OcrUnavailableException("Windows OCR failed: " + ex.Message, ex);
        }
    }

    private static OcrEngine? CreateEngine(string? languageTag)
    {
        if (languageTag is not null) return OcrEngine.TryCreateFromLanguage(new Language(languageTag));

        // "Auto": the user's own Windows languages first, then English, then whatever pack happens to be installed.
        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e is not null);
    }
}
