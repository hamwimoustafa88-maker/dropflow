using System.Diagnostics;
using DropFlow.AI.Models;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.AI.Speech;

/// <summary>
/// <c>aud.transcribe</c> and <c>vid.transcribe</c>: audio or video in, subtitles/text out, entirely on this PC.
/// <para>
/// Options (keys): <c>model</c> ("auto" or a Whisper model id), <c>language</c> ("auto" or ISO code), <c>translateToEnglish</c> (bool),
/// <c>outputs</c> (srt | vtt | txt | json | all, or a comma list such as "srt,txt"), <c>maxCharsPerLine</c> (20..80),
/// <c>rtlMarks</c> (rlm | embedding | none).
/// </para>
/// Results are written next to the input as "name (transcript).srt" etc. (never overwriting).
/// </summary>
public sealed class TranscribeConverter : IConverter, IDisposable
{
    public const string ModelKey = "model";
    public const string LanguageKey = "language";
    public const string TranslateKey = "translateToEnglish";
    public const string OutputsKey = "outputs";
    public const string MaxCharsKey = "maxCharsPerLine";
    public const string RtlMarksKey = "rtlMarks";

    /// <summary>Languages offered in the UI (Whisper knows ~99; any ISO code still works when passed programmatically).</summary>
    public static readonly IReadOnlyList<string> LanguageChoices =
        ["auto", "ar", "en", "fr", "es", "de", "it", "pt", "tr", "fa", "ur", "he", "hi", "id", "ru", "uk", "pl", "nl", "zh", "ja", "ko"];

    private readonly IEngineProvider _engines;
    private readonly ModelManager _models;
    private readonly ISpeechTranscriber _transcriber;
    private readonly Func<long> _availableRam;
    private readonly IDisposable? _ownedTranscriber;
    private readonly Lazy<IReadOnlySet<string>> _audioInputs = new(() => AiCatalog.Inputs("audio"));
    private readonly Lazy<IReadOnlySet<string>> _videoInputs = new(() => AiCatalog.Inputs("video"));

    public TranscribeConverter(IEngineProvider engines, ModelManager models, ISpeechTranscriber? transcriber = null, Func<long>? availableRam = null)
    {
        _engines = engines;
        _models = models;
        _transcriber = transcriber ?? new WhisperNetTranscriber();
        _ownedTranscriber = transcriber is null ? _transcriber as IDisposable : null;   // only dispose what we created
        _availableRam = availableRam ?? WhisperModelChoice.GetAvailableRamBytes;
    }

    /// <summary>Releases the loaded Whisper model (when this converter created the transcriber).</summary>
    public void Dispose() => _ownedTranscriber?.Dispose();

    public IReadOnlyCollection<string> Operations => [AiOperations.AudioTranscribe, AiOperations.VideoTranscribe];

    public bool Supports(string operation, string inputExtension)
    {
        var ext = AiCatalog.Normalize(inputExtension);
        return operation switch
        {
            AiOperations.AudioTranscribe => _audioInputs.Value.Contains(ext),
            // An animated GIF is listed as a video input but has no sound to transcribe.
            AiOperations.VideoTranscribe => ext != "gif" && _videoInputs.Value.Contains(ext),
            _ => false,
        };
    }

    // The audio track is always decoded by ffmpeg (a 16 kHz mono WAV is the one exception and is read directly).
    public EngineId EngineFor(string operation) => EngineId.Ffmpeg;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new(ModelKey, ToolOptionKind.Choice, "auto", ["auto", .. ModelCatalog.Default.Where(m => m.Purpose == ModelPurpose.SpeechToText).Select(m => m.Id)]),
        new(LanguageKey, ToolOptionKind.Choice, "auto", LanguageChoices),
        new(TranslateKey, ToolOptionKind.Bool, false),
        new(OutputsKey, ToolOptionKind.Choice, "srt", ["srt", "vtt", "txt", "json", "all"]),
        new(MaxCharsKey, ToolOptionKind.Number, 42, Min: 20, Max: 80),
        new(RtlMarksKey, ToolOptionKind.Choice, "rlm", ["rlm", "embedding", "none"]),
    ];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        if (!Supports(job.Operation, Path.GetExtension(input).TrimStart('.')))
            return ConversionResult.Fail($"'{Path.GetExtension(input)}' files are not supported by {job.Operation}.");

        var language = WhisperModelChoice.NormalizeLanguage(job.GetString(LanguageKey, "auto"));
        var translate = job.GetBool(TranslateKey);
        var formats = TranscriptFormats.Parse(job.GetString(OutputsKey, "srt"));
        var subtitleOptions = new SubtitleOptions(
            job.GetInt(MaxCharsKey, 42),
            RtlMarks: (job.GetString(RtlMarksKey, "rlm") ?? "rlm").ToLowerInvariant() switch
            {
                "none" => RtlMarkStyle.None,
                "embedding" => RtlMarkStyle.Embedding,
                _ => RtlMarkStyle.Rlm,
            });

        // 1) Model. Resolved before any heavy work so a missing download is reported immediately.
        var modelId = ResolveModel(job.GetString(ModelKey, "auto") ?? "auto", language, translate, out var modelError);
        if (modelId is null) return ConversionResult.Fail(modelError!, sw.Elapsed);
        var modelPath = _models.InstalledPath(modelId)!;

        using var work = WorkDirectory.Create();
        var outputs = new List<string>();
        try
        {
            // 2) Audio: 16 kHz mono WAV.
            progress.Report(new JobProgress(0.01, "Preparing audio"));
            var wav = await PrepareAudioAsync(input, work, cancellationToken).ConfigureAwait(false);
            if (wav.Error is not null) return ConversionResult.Fail(wav.Error, sw.Elapsed);
            progress.Report(new JobProgress(0.1, "Reading audio"));
            float[] samples;
            try
            {
                samples = WavFile.ReadWhisperSamples(wav.Path!, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                return ConversionResult.Fail("The audio could not be read: " + ex.Message, sw.Elapsed);
            }
            if (samples.Length < 1600) return ConversionResult.Fail("The audio is shorter than 0.1 seconds; nothing to transcribe.", sw.Elapsed);

            // 3) Recognition.
            progress.Report(new JobProgress(0.12, "Transcribing"));
            TranscriptionResult result;
            try
            {
                var inner = new InlineProgress<double>(p => progress.Report(new JobProgress(0.12 + p * 0.8, "Transcribing")));
                result = await _transcriber.TranscribeAsync(modelPath, samples, new TranscribeSettings(language, translate && language != "en"), inner, cancellationToken).ConfigureAwait(false);
            }
            catch (SpeechEngineException ex)
            {
                return ConversionResult.Fail(ex.Message, sw.Elapsed);
            }
            samples = [];

            var segments = TextNormalizer.Clean(result.Segments);
            if (segments.Count == 0) return ConversionResult.Fail("No speech was detected in the file.", sw.Elapsed);
            var transcript = new Transcript(
                segments,
                translate ? "en" : (language != "auto" ? language : result.DetectedLanguage),
                modelId,
                translate && language != "en");

            // 4) Output files, all or nothing.
            progress.Report(new JobProgress(0.95, "Writing files"));
            var paths = OutputNaming.ForSet(input, formats.Select(TranscriptFormats.Extension).ToList(), "transcript", job.OutputDirectory);
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OutputNaming.WriteNew(paths[i], TranscriptWriters.Render(formats[i], transcript, subtitleOptions));
                outputs.Add(paths[i]);
            }
            progress.Report(new JobProgress(1, "Done"));
            return ConversionResult.Ok(outputs, sw.Elapsed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            foreach (var path in outputs) TryDelete(path);
            if (ex is OperationCanceledException) throw;
            return ConversionResult.Fail("Could not write the transcript: " + ex.Message, sw.Elapsed);
        }
    }

    /// <summary>Chooses the model to use; null with an explanatory error when none can be used.</summary>
    internal string? ResolveModel(string requested, string language, bool translate, out string? error)
    {
        error = null;
        if (string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase) || requested.Length == 0)
        {
            var installed = _models.Installed(ModelPurpose.SpeechToText).Select(m => m.Id).ToList();
            var pick = WhisperModelChoice.PickInstalled(installed, language, _availableRam(), translate);
            if (pick is not null) return pick;

            var recommended = WhisperModelChoice.Recommend(language, _availableRam(), translate).ModelId;
            error = installed.Count == 0
                ? AiFailures.MissingModel(_models, recommended) + " (no speech model is installed yet)"
                : $"None of the installed speech models can do this ({(translate ? "translation to English" : $"language '{language}'")}). " + AiFailures.MissingModel(_models, recommended);
            return null;
        }

        var info = _models.Find(requested);
        if (info is null || info.Purpose != ModelPurpose.SpeechToText)
        {
            error = $"'{requested}' is not a speech-to-text model.";
            return null;
        }
        if (!WhisperModelChoice.IsCompatible(requested, language, translate))
        {
            var profile = WhisperModelChoice.Profile(requested);
            error = profile is { EnglishOnly: true }
                ? $"Model '{requested}' only understands English; choose a multilingual model for language '{language}'."
                : $"Model '{requested}' cannot translate to English; use ggml-medium-q5_0 or ggml-small for translation.";
            return null;
        }
        if (!_models.IsInstalled(requested))
        {
            error = AiFailures.MissingModel(_models, requested);
            return null;
        }
        return requested;
    }

    private sealed record PreparedAudio(string? Path, string? Error);

    private async Task<PreparedAudio> PrepareAudioAsync(string input, WorkDirectory work, CancellationToken ct)
    {
        // A WAV that is already 16 kHz mono PCM needs no conversion (and therefore no ffmpeg).
        if (string.Equals(Path.GetExtension(input), ".wav", StringComparison.OrdinalIgnoreCase) && WavFile.TryReadInfo(input) is { IsWhisperReady: true })
            return new PreparedAudio(input, null);

        var ffmpeg = _engines.Locate(EngineId.Ffmpeg);
        if (ffmpeg is null)
            return new PreparedAudio(null, AiFailures.MissingEngine("ffmpeg", "It is needed to read the audio track; install it from the engine installer."));

        var wav = work.File("audio.wav");
        var run = await ProcessRunner.RunAsync(
            ffmpeg, FfmpegAudio.BuildExtractArguments(input, wav),
            new ProcessOptions { Timeout = TimeSpan.FromHours(2), WorkingDirectory = work.Path }, ct).ConfigureAwait(false);
        if (run.Cancelled) throw new OperationCanceledException(ct);
        if (run.TimedOut) return new PreparedAudio(null, "ffmpeg took too long to extract the audio.");
        if (!run.Success || !File.Exists(wav)) return new PreparedAudio(null, FfmpegAudio.ExplainFailure(run.StdErr, run.LastMessage));
        return new PreparedAudio(wav, null);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
