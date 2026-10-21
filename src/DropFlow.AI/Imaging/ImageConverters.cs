using System.Diagnostics;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.AI.Imaging;

/// <summary>
/// <c>img.remove-bg</c>: photo in, PNG cut-out out ("name (no background).png"). Runs a segmentation network with ONNX Runtime.
/// <para>
/// Options (keys): <c>model</c> ("auto" = best installed of birefnet-lite, u2netp), <c>edge</c> (soft | hard),
/// <c>background</c> (transparent | white | black).
/// </para>
/// </summary>
public sealed class BackgroundRemovalConverter : IConverter, IDisposable
{
    public const string ModelKey = "model";
    public const string EdgeKey = "edge";
    public const string BackgroundKey = "background";

    private readonly ModelManager _models;
    private readonly BackgroundRemover _remover;
    private readonly OnnxSessionCache? _ownedSessions;

    public BackgroundRemovalConverter(ModelManager models, OnnxSessionCache? sessions = null)
    {
        _models = models;
        _ownedSessions = sessions is null ? new OnnxSessionCache() : null;
        _remover = new BackgroundRemover(sessions ?? _ownedSessions!);
    }

    /// <summary>Releases the loaded network (when this converter created its own session cache).</summary>
    public void Dispose() => _ownedSessions?.Dispose();

    public IReadOnlyCollection<string> Operations => [AiOperations.RemoveBackground];

    public bool Supports(string operation, string inputExtension) =>
        operation == AiOperations.RemoveBackground && ImageInputs.Supported.Contains(AiCatalog.Normalize(inputExtension));

    // ONNX Runtime ships inside the app and the model is managed by ModelManager, so no external program is required.
    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new(ModelKey, ToolOptionKind.Choice, "auto", ["auto", .. BackgroundModelSpec.AutoPreference]),
        new(EdgeKey, ToolOptionKind.Choice, "soft", ["soft", "hard"]),
        new(BackgroundKey, ToolOptionKind.Choice, "transparent", ["transparent", "white", "black"]),
    ];

    /// <summary>Chooses the model id to run; null with an error when none can be used.</summary>
    internal string? ResolveModel(string requested, out string? error)
    {
        error = null;
        if (requested.Equals("auto", StringComparison.OrdinalIgnoreCase) || requested.Length == 0)
        {
            var pick = BackgroundModelSpec.AutoPreference.FirstOrDefault(_models.IsInstalled);
            if (pick is not null) return pick;
            error = AiFailures.MissingModel(_models, BackgroundModelSpec.AutoPreference[^1]);
            return null;
        }
        if (BackgroundModelSpec.For(requested) is null || _models.Find(requested) is not { Purpose: ModelPurpose.BackgroundRemoval })
        {
            error = $"'{requested}' is not a supported background-removal model.";
            return null;
        }
        if (!_models.IsInstalled(requested))
        {
            error = AiFailures.MissingModel(_models, requested);
            return null;
        }
        return requested;
    }

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(job, progress, cancellationToken), cancellationToken);

    private ConversionResult Run(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        if (!Supports(job.Operation, Path.GetExtension(input).TrimStart('.')))
            return ConversionResult.Fail($"'{Path.GetExtension(input)}' images are not supported.");

        var modelId = ResolveModel(job.GetString(ModelKey, "auto") ?? "auto", out var error);
        if (modelId is null) return ConversionResult.Fail(error!, sw.Elapsed);

        var fill = (job.GetString(BackgroundKey, "transparent") ?? "").ToLowerInvariant() switch
        {
            "white" => BackgroundFill.White,
            "black" => BackgroundFill.Black,
            _ => BackgroundFill.Transparent,
        };
        var hard = string.Equals(job.GetString(EdgeKey, "soft"), "hard", StringComparison.OrdinalIgnoreCase) ? 0.5 : 0;

        string? output = null;
        try
        {
            progress.Report(new JobProgress(0.05, "Reading image"));
            using var image = ImageIo.LoadUpright(input);

            progress.Report(new JobProgress(0.15, "Removing background"));
            using var cutout = _remover.Remove(image, _models.PathOf(modelId), BackgroundModelSpec.For(modelId)!, hard, fill, ct);

            progress.Report(new JobProgress(0.9, "Saving"));
            output = OutputNaming.ForOne(input, "png", "no background", job.OutputDirectory);
            cutout.Format = MagickFormat.Png;
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                cutout.Write(stream);
            progress.Report(new JobProgress(1, "Done"));
            return ConversionResult.Ok([output], sw.Elapsed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ImageToolException or IOException or MagickException)
        {
            if (output is not null) { try { File.Delete(output); } catch (IOException) { } }
            if (ex is OperationCanceledException) throw;
            return ConversionResult.Fail(ex.Message, sw.Elapsed);
        }
    }
}

/// <summary>
/// <c>img.redact-auto</c>: finds faces (UltraFace, ONNX) and/or printed text (OCR) and covers them. Writes "name (redacted).ext"
/// and strips EXIF/XMP/IPTC (an embedded thumbnail would otherwise still show the original).
/// <para>
/// Options (keys): <c>target</c> (faces | text | both), <c>style</c> (solid | blur), <c>padding</c> (0..50, percent of the box's shorter
/// side added on every edge), <c>language</c> (OCR language for text, as in img.ocr).
/// </para>
/// Detection is automatic and can miss things (small or turned faces, handwriting, stylised text): review the result before sharing.
/// If a requested detector is unavailable the job fails instead of silently redacting less.
/// </summary>
public sealed class RedactAutoConverter : IConverter, IDisposable
{
    public const string TargetKey = "target";
    public const string StyleKey = "style";
    public const string PaddingKey = "padding";
    public const string LanguageKey = "language";

    public const string FaceModelId = "ultraface-rfb-320";

    private readonly ModelManager _models;
    private readonly OcrService _ocr;
    private readonly FaceDetector _faces;
    private readonly OnnxSessionCache? _ownedSessions;

    public RedactAutoConverter(ModelManager models, OcrService ocr, OnnxSessionCache? sessions = null)
    {
        _models = models;
        _ocr = ocr;
        _ownedSessions = sessions is null ? new OnnxSessionCache() : null;
        _faces = new FaceDetector(sessions ?? _ownedSessions!);
    }

    /// <summary>Releases the loaded network (when this converter created its own session cache).</summary>
    public void Dispose() => _ownedSessions?.Dispose();

    public IReadOnlyCollection<string> Operations => [AiOperations.RedactAuto];

    public bool Supports(string operation, string inputExtension) =>
        operation == AiOperations.RedactAuto && ImageInputs.Supported.Contains(AiCatalog.Normalize(inputExtension));

    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new(TargetKey, ToolOptionKind.Choice, "both", ["both", "faces", "text"]),
        new(StyleKey, ToolOptionKind.Choice, "solid", ["solid", "blur"]),
        new(PaddingKey, ToolOptionKind.Number, 10, Min: 0, Max: 50),
        new(LanguageKey, ToolOptionKind.Choice, "auto", OcrConverter.LanguageChoices),
    ];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        if (!Supports(job.Operation, Path.GetExtension(input).TrimStart('.')))
            return ConversionResult.Fail($"'{Path.GetExtension(input)}' images are not supported.");

        var target = (job.GetString(TargetKey, "both") ?? "both").ToLowerInvariant();
        var wantFaces = target is "both" or "faces";
        var wantText = target is "both" or "text";
        var style = string.Equals(job.GetString(StyleKey, "solid"), "blur", StringComparison.OrdinalIgnoreCase) ? RedactStyle.Blur : RedactStyle.Solid;
        var padding = Math.Clamp(job.GetDouble(PaddingKey, 10), 0, 50);

        // Fail early and loudly: a half-redacted image is worse than none.
        if (wantFaces && !_models.IsInstalled(FaceModelId))
            return ConversionResult.Fail(AiFailures.MissingModel(_models, FaceModelId), sw.Elapsed);

        string? output = null;
        try
        {
            progress.Report(new JobProgress(0.05, "Reading image"));
            using var image = await Task.Run(() => ImageIo.LoadUpright(input), cancellationToken).ConfigureAwait(false);
            var width = (int)image.Width;
            var height = (int)image.Height;
            var rects = new List<PixelRect>();
            var found = new List<string>();

            if (wantFaces)
            {
                progress.Report(new JobProgress(0.15, "Finding faces"));
                var faces = await Task.Run(() => _faces.Detect(image, _models.PathOf(FaceModelId), cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
                foreach (var f in faces)
                    if (RedactionRegions.PadAndClamp(new OcrRect(f.X, f.Y, f.Width, f.Height), padding, width, height) is { } r) rects.Add(r);
                if (faces.Count > 0) found.Add($"{faces.Count} face(s)");
            }

            if (wantText)
            {
                progress.Report(new JobProgress(0.4, "Finding text"));
                var ocr = await _ocr.RecognizeAsync(input, new OcrRequest(job.GetString(LanguageKey, "auto") ?? "auto"), cancellationToken).ConfigureAwait(false);
                var before = rects.Count;
                foreach (var box in RedactionRegions.TextBoxes(ocr))
                    if (RedactionRegions.PadAndClamp(box, padding, width, height) is { } r) rects.Add(r);
                if (rects.Count > before) found.Add($"{rects.Count - before} text line(s)");
            }

            if (rects.Count == 0)
                return ConversionResult.Fail($"Nothing to redact was detected ({target}). No file was written; check the image or redact it by hand.", sw.Elapsed);

            progress.Report(new JobProgress(0.8, "Redacting"));
            RedactionRenderer.Apply(image, RedactionRegions.Merge(rects), style);
            foreach (var profile in new[] { "exif", "xmp", "iptc", "8bim" }) image.RemoveProfile(profile);

            var (extension, format) = ImageIo.OutputFor(Path.GetExtension(input));
            output = OutputNaming.ForOne(input, extension, "redacted", job.OutputDirectory);
            image.Format = format;
            if (format == MagickFormat.Jpeg) image.Quality = 92;
            progress.Report(new JobProgress(0.9, "Saving"));
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                image.Write(stream);
            progress.Report(new JobProgress(1, "Done"));
            return ConversionResult.Ok([output], sw.Elapsed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ImageToolException or OcrUnavailableException or IOException or MagickException)
        {
            if (output is not null) { try { File.Delete(output); } catch (IOException) { } }
            if (ex is OperationCanceledException) throw;
            return ConversionResult.Fail(ex.Message, sw.Elapsed);
        }
    }
}
