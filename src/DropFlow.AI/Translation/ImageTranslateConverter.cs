using System.Diagnostics;
using System.Text;
using DropFlow.AI.Imaging;
using DropFlow.AI.Ocr;
using DropFlow.AI.Translation.Rendering;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.AI.Translation;

/// <summary>
/// <c>img.translate</c>: reads the text in a picture with the OCR engine (Windows OCR, Tesseract as fallback), translates it between English and
/// Arabic on this PC and writes "name (translated).txt". With the <c>overlay</c> option it also writes a copy of the picture
/// ("name (translated).png" and so on) where each paragraph is replaced by its translation, drawn over a patch of the surrounding colour.
/// <para>Options (keys): <c>direction</c> (auto | en-ar | ar-en), <c>quality</c>, <c>language</c> (OCR language: auto | en | ar | ar+en), <c>overlay</c> (bool, default off).</para>
/// </summary>
public sealed class ImageTranslateConverter : IConverter
{
    public const string Operation = "img.translate";
    public const string LanguageKey = "language";
    public const string OverlayKey = "overlay";

    private readonly OcrService _ocr;
    private readonly ITextTranslator _translator;
    private readonly TimeSpan _timeout;
    private readonly Func<string?> _fontFile;
    private readonly Lazy<IReadOnlySet<string>> _inputs = new(() => ImageInputs.Supported);

    public ImageTranslateConverter(OcrService ocr, ITextTranslator translator, TimeSpan? timeout = null, Func<string?>? fontFile = null)
    {
        _ocr = ocr;
        _translator = translator;
        _timeout = timeout ?? TranslationJobSupport.DefaultTimeout;
        _fontFile = fontFile ?? TextOverlayRenderer.FontFile;
    }

    public IReadOnlyCollection<string> Operations => [Operation];

    public bool Supports(string operation, string inputExtension) =>
        operation == Operation && _inputs.Value.Contains(AiCatalog.Normalize(inputExtension));

    // Windows OCR needs nothing installed; Tesseract is only a fallback.
    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        .. TranslationJobSupport.CommonOptions(),
        new(LanguageKey, ToolOptionKind.Choice, "auto", OcrConverter.LanguageChoices),
        new(OverlayKey, ToolOptionKind.Bool, false),
    ];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        if (!Supports(job.Operation, Path.GetExtension(input).TrimStart('.')))
            return ConversionResult.Fail($"'{Path.GetExtension(input)}' images are not supported by {job.Operation}.");
        if (!TranslationInfo.TryParseDirection(job.GetString(TranslationJobSupport.DirectionKey), out var explicitDirection))
            return ConversionResult.Fail($"Unknown translation direction '{job.GetString(TranslationJobSupport.DirectionKey)}'. Use auto, en-ar or ar-en.");
        var overlay = job.GetBool(OverlayKey);
        var quality = TranslationInfo.ParseQuality(job.GetString(TranslationJobSupport.QualityKey));

        // 1) The model first: a missing download should be reported before any work is done.
        if (explicitDirection is { } wanted && _translator.NotReadyMessage(wanted) is { } notReady)
            return ConversionResult.Fail(notReady, sw.Elapsed);

        // 2) Read the text in the picture.
        progress.Report(new JobProgress(0.03, "Reading the text in the picture"));
        OcrResult ocr;
        try
        {
            ocr = await RecognizeAsync(input, explicitDirection, job.GetString(LanguageKey, "auto") ?? "auto", cancellationToken).ConfigureAwait(false);
        }
        catch (OcrUnavailableException ex)
        {
            return ConversionResult.Fail(ex.Message, sw.Elapsed);
        }
        catch (ImageToolException ex)
        {
            return ConversionResult.Fail(ex.Message, sw.Elapsed);
        }
        if (ocr.IsEmpty) return ConversionResult.Fail("No text was found in the picture.", sw.Elapsed);

        var blocks = OcrBlocks.Group(ocr.Lines);
        var units = blocks.Select(b => b.Text).ToList();
        var (chosen, error) = TranslationJobSupport.ChooseDirection(job.GetString(TranslationJobSupport.DirectionKey), units);
        if (chosen is null) return ConversionResult.Fail(error!, sw.Elapsed);
        var direction = chosen.Direction;
        if (!TranslationJobSupport.AnyToTranslate(units, direction))
            return ConversionResult.Fail(TranslationJobSupport.NothingToTranslate(direction), sw.Elapsed);

        // 3) Translate.
        var (translations, failure) = await TranslationJobSupport.TranslateAsync(
            _translator, direction, units, quality, progress, 0.35, 0.8, "Translating", _timeout, cancellationToken).ConfigureAwait(false);
        if (translations is null) return ConversionResult.Fail(failure!, sw.Elapsed);

        // 4) Write the text, and the picture when asked. Both or neither.
        var (imageExtension, imageFormat) = ImageIo.OutputFor(Path.GetExtension(input));
        var extensions = overlay ? new List<string> { "txt", imageExtension } : ["txt"];
        var paths = OutputNaming.ForSet(input, extensions, "translated", job.OutputDirectory);
        var written = new List<string>();
        var notes = new List<string>();
        var existingNote = TranslationJobSupport.NoteFor(chosen, TranslationJobSupport.CountSkipped(units, direction));
        if (existingNote.Length > 0) notes.Add(existingNote);
        try
        {
            progress.Report(new JobProgress(0.85, "Writing the text"));
            OutputNaming.WriteNew(paths[0], string.Join(Environment.NewLine + Environment.NewLine, translations) + Environment.NewLine);
            written.Add(paths[0]);

            if (overlay)
            {
                var font = _fontFile();
                if (font is null)
                {
                    notes.Add(NoteText.Key("note.translate.noFont"));
                }
                else
                {
                    progress.Report(new JobProgress(0.9, "Drawing the translation on the picture"));
                    using var image = await Task.Run(() => ImageIo.LoadUpright(input), cancellationToken).ConfigureAwait(false);
                    var items = blocks.Select((b, i) => new OverlayText(b.Bounds, b.LineHeight, translations[i], direction == TranslationDirection.EnglishToArabic || b.IsRightToLeft))
                        .Where((item, i) => ScriptDetector.NeedsTranslation(blocks[i].Text, direction))
                        .ToList();
                    await Task.Run(() => TextOverlayRenderer.Apply(image, items, font, cancellationToken), cancellationToken).ConfigureAwait(false);
                    foreach (var profile in new[] { "exif", "xmp", "iptc", "8bim" }) image.RemoveProfile(profile);
                    image.Format = imageFormat;
                    if (imageFormat == MagickFormat.Jpeg) image.Quality = 92;
                    await using var stream = new FileStream(paths[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    image.Write(stream);
                    written.Add(paths[1]);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ImageToolException or MagickException)
        {
            foreach (var p in paths) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (ex is OperationCanceledException) throw;
            return ConversionResult.Fail("Could not write the translation: " + ex.Message, sw.Elapsed);
        }

        progress.Report(new JobProgress(1, "Done"));
        return ConversionResult.Ok(written, sw.Elapsed, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>
    /// OCR with a language that fits the direction. With "auto" for both, Tesseract with Arabic and English is tried first (it reads a mix);
    /// without it the Windows languages of the user are used.
    /// </summary>
    private async Task<OcrResult> RecognizeAsync(string input, TranslationDirection? direction, string language, CancellationToken ct)
    {
        if (!language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return await _ocr.RecognizeAsync(input, new OcrRequest(language), ct).ConfigureAwait(false);

        switch (direction)
        {
            case TranslationDirection.EnglishToArabic:
                return await _ocr.RecognizeAsync(input, new OcrRequest("en"), ct).ConfigureAwait(false);
            case TranslationDirection.ArabicToEnglish:
                return await _ocr.RecognizeAsync(input, new OcrRequest("ar"), ct).ConfigureAwait(false);
        }

        var mixed = new OcrRequest("ar+en", OcrEnginePreference.Tesseract);
        if ((await _ocr.PlanAsync(mixed, ct).ConfigureAwait(false)).Plan is not null)
            return await _ocr.RecognizeAsync(input, mixed, ct).ConfigureAwait(false);

        // Windows OCR reads one language per run: read the picture as English and as Arabic and keep the reading that found more letters
        // of its own script (the wrong language finds little or nothing).
        OcrResult? best = null;
        var bestScore = -1;
        OcrUnavailableException? firstProblem = null;
        foreach (var candidate in new[] { "en", "ar" })
        {
            try
            {
                var result = await _ocr.RecognizeAsync(input, new OcrRequest(candidate), ct).ConfigureAwait(false);
                var counts = ScriptDetector.Count(result.Text);
                var score = candidate == "ar" ? counts.Arabic : counts.Latin;
                if (score > bestScore) { best = result; bestScore = score; }
            }
            catch (OcrUnavailableException ex)
            {
                firstProblem ??= ex;
            }
        }
        if (best is not null) return best;
        throw firstProblem ?? new OcrUnavailableException("No OCR language is available.");
    }
}
