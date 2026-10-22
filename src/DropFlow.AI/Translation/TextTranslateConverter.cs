using System.Diagnostics;
using DropFlow.AI.Translation.Formats;
using DropFlow.Core.Operations;

namespace DropFlow.AI.Translation;

/// <summary>
/// <c>txt.translate</c>: translates a text, Markdown, HTML or subtitle file between English and Arabic, entirely on this PC, and writes
/// "name (translated).ext" next to it with the same format. Subtitles keep their numbers, timings and line counts; Markdown keeps its syntax and code.
/// <para>Options (keys): <c>direction</c> (auto | en-ar | ar-en), <c>quality</c> (fast | balanced | best).</para>
/// </summary>
public sealed class TextTranslateConverter : IConverter, IDisposable
{
    public const string Operation = "txt.translate";

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "txt", "md", "srt", "vtt", "html" };

    private readonly ITextTranslator _translator;
    private readonly TimeSpan _timeout;

    private readonly bool _ownsTranslator;

    /// <param name="ownsTranslator">True when this converter should dispose <paramref name="translator"/> (one converter of the module owns the shared translator).</param>
    public TextTranslateConverter(ITextTranslator translator, TimeSpan? timeout = null, bool ownsTranslator = false)
    {
        _translator = translator;
        _timeout = timeout ?? TranslationJobSupport.DefaultTimeout;
        _ownsTranslator = ownsTranslator;
    }

    public void Dispose()
    {
        if (_ownsTranslator && _translator is IDisposable d) d.Dispose();
    }

    public IReadOnlyCollection<string> Operations => [Operation];

    public bool Supports(string operation, string inputExtension) =>
        operation == Operation && Extensions.Contains(AiCatalog.Normalize(inputExtension));

    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) => TranslationJobSupport.CommonOptions();

    internal static TranslationTemplate Parse(string extension, string text) => extension switch
    {
        "srt" or "vtt" => SubtitleParser.Parse(text),
        "md" => MarkdownParser.Parse(text),
        "html" => HtmlParser.Parse(text),
        _ => PlainTextParser.Parse(text),
    };

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        var extension = AiCatalog.Normalize(Path.GetExtension(input));
        if (!Supports(job.Operation, extension))
            return ConversionResult.Fail($"'.{extension}' files are not supported by {job.Operation}.");
        if (new FileInfo(input).Length > 50L * 1024 * 1024)
            return ConversionResult.Fail("The file is larger than 50 MB; split it first.");

        progress.Report(new JobProgress(0.02, "Reading the file"));
        TranslationJobSupport.SourceText source;
        TranslationTemplate template;
        try
        {
            source = TranslationJobSupport.ReadText(input);
            template = Parse(extension, source.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail("Could not read the file: " + ex.Message, sw.Elapsed);
        }

        var units = template.Units;
        var (chosen, error) = TranslationJobSupport.ChooseDirection(job.GetString(TranslationJobSupport.DirectionKey), units);
        if (chosen is null) return ConversionResult.Fail(error!, sw.Elapsed);
        var direction = chosen.Direction;
        if (!TranslationJobSupport.AnyToTranslate(units, direction))
            return ConversionResult.Fail(TranslationJobSupport.NothingToTranslate(direction), sw.Elapsed);

        var quality = TranslationInfo.ParseQuality(job.GetString(TranslationJobSupport.QualityKey));
        var (translations, failure) = await TranslationJobSupport.TranslateAsync(
            _translator, direction, units, quality, progress, 0.05, 0.92, "Translating", _timeout, cancellationToken).ConfigureAwait(false);
        if (translations is null) return ConversionResult.Fail(failure!, sw.Elapsed);

        progress.Report(new JobProgress(0.94, "Writing the file"));
        var output = template.Render(translations);
        if (extension == "html") output = HtmlParser.FixDocument(output, direction);
        var path = OutputNaming.ForOne(input, extension, "translated", job.OutputDirectory);
        try
        {
            TranslationJobSupport.WriteNew(path, output, source.NewLine, source.HadBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return ConversionResult.Fail("Could not write the translated file: " + ex.Message, sw.Elapsed);
        }
        progress.Report(new JobProgress(1, "Done"));
        var note = TranslationJobSupport.NoteFor(chosen, TranslationJobSupport.CountSkipped(units, direction));
        return ConversionResult.Ok([path], sw.Elapsed, note.Length == 0 ? null : note);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
