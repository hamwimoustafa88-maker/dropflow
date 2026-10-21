using System.Diagnostics;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.AI.Ocr;

/// <summary>
/// <c>img.ocr</c>: image in, text file out ("name (ocr).txt"). Windows OCR first; Tesseract when the language has no Windows pack.
/// <para>
/// Options (keys): <c>language</c> ("auto", "en", "ar", "ar+en", ...), <c>engine</c> (auto | windows | tesseract),
/// <c>outputs</c> (txt | json | "txt,json"; json holds every word rectangle).
/// </para>
/// </summary>
public sealed class OcrConverter : IConverter
{
    public const string LanguageKey = "language";
    public const string EngineKey = "engine";
    public const string OutputsKey = "outputs";

    public static readonly IReadOnlyList<string> LanguageChoices = ["auto", "en", "ar", "ar+en"];

    private readonly OcrService _ocr;
    private readonly Lazy<IReadOnlySet<string>> _inputs = new(() => ImageInputs.Supported);

    public OcrConverter(OcrService ocr) => _ocr = ocr;

    public IReadOnlyCollection<string> Operations => [AiOperations.ImageOcr];

    public bool Supports(string operation, string inputExtension) =>
        operation == AiOperations.ImageOcr && _inputs.Value.Contains(AiCatalog.Normalize(inputExtension));

    // Windows OCR needs nothing installed; Tesseract is only a fallback, so the operation as a whole requires no engine.
    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new(LanguageKey, ToolOptionKind.Choice, "auto", LanguageChoices),
        new(EngineKey, ToolOptionKind.Choice, "auto", ["auto", "windows", "tesseract"]),
        new(OutputsKey, ToolOptionKind.Choice, "txt", ["txt", "json", "txt,json"]),
    ];

    public static OcrEnginePreference ParseEngine(string? value) => (value ?? "auto").Trim().ToLowerInvariant() switch
    {
        "windows" => OcrEnginePreference.Windows,
        "tesseract" => OcrEnginePreference.Tesseract,
        _ => OcrEnginePreference.Auto,
    };

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        if (!Supports(job.Operation, Path.GetExtension(input).TrimStart('.')))
            return ConversionResult.Fail($"'{Path.GetExtension(input)}' images are not supported by OCR.");

        var wantJson = false;
        var wantTxt = false;
        foreach (var part in (job.GetString(OutputsKey, "txt") ?? "txt").Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("json", StringComparison.OrdinalIgnoreCase)) wantJson = true;
            else wantTxt = true;
        }
        if (!wantJson) wantTxt = true;

        progress.Report(new JobProgress(0.05, "Reading text"));
        OcrResult result;
        try
        {
            result = await _ocr.RecognizeAsync(input, new OcrRequest(job.GetString(LanguageKey, "auto") ?? "auto", ParseEngine(job.GetString(EngineKey))), cancellationToken).ConfigureAwait(false);
        }
        catch (OcrUnavailableException ex)
        {
            return ConversionResult.Fail(ex.Message, sw.Elapsed);
        }
        if (result.IsEmpty) return ConversionResult.Fail("No text was found in the image.", sw.Elapsed);

        var extensions = new List<string>();
        if (wantTxt) extensions.Add("txt");
        if (wantJson) extensions.Add("json");
        var paths = OutputNaming.ForSet(input, extensions, "ocr", job.OutputDirectory);
        var written = new List<string>();
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OutputNaming.WriteNew(paths[i], extensions[i] == "json" ? result.ToJson() : result.ToPlainText());
                written.Add(paths[i]);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            foreach (var p in written) { try { File.Delete(p); } catch (IOException) { } }
            if (ex is OperationCanceledException) throw;
            return ConversionResult.Fail("Could not write the text file: " + ex.Message, sw.Elapsed);
        }
        progress.Report(new JobProgress(1, "Done"));
        return ConversionResult.Ok(written, sw.Elapsed);
    }
}
