using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.AI.Speech;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>The module as the app sees it: one list of converters, truthful about what it can and cannot do.</summary>
public class AiModuleTests
{
    private static readonly string[] AllOperations = ["aud.transcribe", "vid.transcribe", "img.ocr", "pdf.ocr", "img.remove-bg", "img.redact-auto"];

    private static IReadOnlyList<IConverter> Create(TempDir dir) => AiModule.Create(new FakeEngines(), new ModelManager(dir.Combine("models")));

    [Fact]
    public void Module_exposes_every_operation_exactly_once_and_all_of_them_exist_in_the_catalog()
    {
        using var dir = new TempDir();
        var converters = Create(dir);
        var operations = converters.SelectMany(c => c.Operations).ToList();

        Assert.Equal(AllOperations.Order(), operations.Order());
        Assert.Equal(operations.Count, operations.Distinct().Count());
        Assert.Equal(AllOperations.Order(), AiOperations.All.Order());

        var catalog = ToolCatalog.LoadEmbedded();
        foreach (var op in operations) Assert.NotNull(catalog.Tool(op));
        // no convert:<ext> operation sneaks in
        Assert.DoesNotContain(operations, Operation.IsConvert);
    }

    [Fact]
    public void The_registry_routes_each_operation_to_the_right_converter_by_extension()
    {
        using var dir = new TempDir();
        var registry = new ToolRegistry().Register(Create(dir));

        Assert.IsType<TranscribeConverter>(registry.Find("aud.transcribe", "mp3"));
        Assert.IsType<TranscribeConverter>(registry.Find("vid.transcribe", "mp4"));
        Assert.IsType<OcrConverter>(registry.Find("img.ocr", "png"));
        Assert.IsType<PdfOcrConverter>(registry.Find("pdf.ocr", "pdf"));
        Assert.IsType<DropFlow.AI.Imaging.BackgroundRemovalConverter>(registry.Find("img.remove-bg", "jpg"));
        Assert.IsType<DropFlow.AI.Imaging.RedactAutoConverter>(registry.Find("img.redact-auto", "webp"));

        Assert.Null(registry.Find("aud.transcribe", "mp4"));
        Assert.Null(registry.Find("vid.transcribe", "gif"));
        Assert.Null(registry.Find("img.ocr", "pdf"));
        Assert.Null(registry.Find("pdf.ocr", "png"));
        Assert.Null(registry.Find("img.remove-bg", "mp3"));
        Assert.Null(registry.Find("img.redact-auto", "docx"));
        Assert.Equal(AllOperations.Order(), registry.ImplementedOperations.Order());
    }

    [Fact]
    public void Supports_never_claims_an_extension_outside_the_tools_catalog_category()
    {
        using var dir = new TempDir();
        var catalog = ToolCatalog.LoadEmbedded();
        var registry = new ToolRegistry().Register(Create(dir));
        var everyExtension = catalog.Categories.SelectMany(c => c.Inputs).Distinct().ToList();

        foreach (var op in AllOperations)
        {
            var category = catalog.Tool(op)!.Cat!;
            var allowed = catalog.Category(category)!.Inputs;
            foreach (var ext in everyExtension)
                if (registry.Supports(op, ext))
                    Assert.True(allowed.Contains(ext), $"{op} claims .{ext} but the catalog category '{category}' does not list it");
        }
    }

    [Fact]
    public void Every_operation_declares_its_engine_and_options_consistently_with_the_catalog_ui_rules()
    {
        using var dir = new TempDir();
        var byOp = Create(dir).SelectMany(c => c.Operations.Select(op => (op, c))).ToDictionary(x => x.op, x => x.c);

        Assert.Equal(EngineId.Ffmpeg, byOp["aud.transcribe"].EngineFor("aud.transcribe"));
        Assert.Equal(EngineId.Ffmpeg, byOp["vid.transcribe"].EngineFor("vid.transcribe"));
        Assert.Equal(EngineId.Tesseract, byOp["pdf.ocr"].EngineFor("pdf.ocr"));
        Assert.Equal(EngineId.None, byOp["img.ocr"].EngineFor("img.ocr"));       // Windows OCR needs no install
        Assert.Equal(EngineId.None, byOp["img.remove-bg"].EngineFor("img.remove-bg"));
        Assert.Equal(EngineId.None, byOp["img.redact-auto"].EngineFor("img.redact-auto"));

        foreach (var (op, converter) in byOp)
        {
            var options = converter.OptionsFor(op);
            Assert.NotEmpty(options);                                              // each of these tools asks first
            Assert.Equal(options.Count, options.Select(o => o.Key).Distinct().Count());
            foreach (var o in options)
            {
                if (o.Kind == ToolOptionKind.Choice)
                {
                    Assert.NotEmpty(o.Choices!);
                    Assert.Contains(o.Default!.ToString(), o.Choices!);
                }
                if (o.Kind == ToolOptionKind.Number)
                {
                    Assert.InRange(Convert.ToDouble(o.Default), o.Min!.Value, o.Max!.Value);
                }
                Assert.False(o.Required, $"{op}.{o.Key}: every option must have a usable default");
            }
        }
    }

    [Fact]
    public void Every_option_default_passes_through_the_converters_option_readers()
    {
        // The UI submits the defaults as given; they must parse with GetString/GetInt/GetDouble/GetBool without throwing.
        using var dir = new TempDir();
        foreach (var c in Create(dir))
            foreach (var op in c.Operations)
            {
                var job = new ConversionJob(["x"], op, c.OptionsFor(op).ToDictionary(o => o.Key, o => o.Default));
                foreach (var o in c.OptionsFor(op))
                    switch (o.Kind)
                    {
                        case ToolOptionKind.Bool: _ = job.GetBool(o.Key); break;
                        case ToolOptionKind.Number: _ = job.GetDouble(o.Key); break;
                        default: Assert.False(string.IsNullOrEmpty(job.GetString(o.Key))); break;
                    }
            }
    }

    [Fact]
    public async Task Every_converter_fails_cleanly_instead_of_throwing_when_its_model_or_engine_is_missing()
    {
        using var dir = new TempDir();
        var converters = Create(dir);   // empty model folder, no engines
        var samples = new Dictionary<string, string>
        {
            ["aud.transcribe"] = Samples.Wav(dir.Combine("a.wav"), sampleRate: 16000),
            ["vid.transcribe"] = dir.WriteText("v.mp4", "not a video"),
            ["img.remove-bg"] = Samples.Png(dir.Combine("p.png")),
            ["img.redact-auto"] = Samples.Png(dir.Combine("q.png")),
            ["pdf.ocr"] = Samples.Pdf(dir.Combine("d.pdf")),
        };
        var expectedMissing = new Dictionary<string, string>
        {
            ["aud.transcribe"] = "Model '",
            ["vid.transcribe"] = "Model '",
            ["img.remove-bg"] = "Model 'u2netp'",
            ["img.redact-auto"] = "Model 'ultraface-rfb-320'",
            ["pdf.ocr"] = "Tesseract is not installed",
        };

        foreach (var c in converters)
            foreach (var op in c.Operations.Where(samples.ContainsKey))
            {
                var result = await c.RunAsync(new ConversionJob([samples[op]], op), new Progress<JobProgress>(), default);
                Assert.False(result.Success, op);
                Assert.Contains(expectedMissing[op], result.Error);
            }
    }

    [Fact]
    public void A_module_can_be_created_with_a_custom_page_renderer()
    {
        using var dir = new TempDir();
        var custom = new NullRasterizer();
        var converters = AiModule.Create(new FakeEngines(), new ModelManager(dir.Combine("models")), custom);
        Assert.Same(custom, converters.OfType<PdfOcrConverter>().Single().Rasterizer);
        Assert.IsType<WindowsPdfPageRasterizer>(Create(dir).OfType<PdfOcrConverter>().Single().Rasterizer);
    }

    private sealed class NullRasterizer : IPageRasterizer
    {
        public Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public void Missing_model_messages_round_trip_through_the_parser()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("m"));
        foreach (var m in ModelCatalog.Default)
        {
            var message = AiFailures.MissingModel(models, m.Id);
            Assert.Equal(m.Id, AiFailures.TryParseMissingModel(message));
            Assert.Contains(m.DisplayName, message);
        }
        Assert.Null(AiFailures.TryParseMissingModel("something else failed"));
        Assert.Null(AiFailures.TryParseMissingModel(null));
        Assert.Contains("Tesseract is not installed", AiFailures.MissingEngine("Tesseract"));
    }
}

/// <summary>Real Whisper.net native runtime, with no model: proves the library loads and fails correctly.</summary>
public class WhisperRuntimeTests
{
    [Fact]
    public async Task The_native_whisper_runtime_loads_and_a_bad_model_file_is_reported_as_a_speech_engine_error()
    {
        using var dir = new TempDir();
        var fakeModel = dir.Combine("ggml-tiny.bin");
        File.WriteAllBytes(fakeModel, Hashing.Bytes(4096));
        using var transcriber = new WhisperNetTranscriber();

        var ex = await Assert.ThrowsAsync<SpeechEngineException>(() =>
            transcriber.TranscribeAsync(fakeModel, new float[16000], new TranscribeSettings("en"), null, default));

        // If the native library had failed to load we would see DllNotFound/"runtime" wording; instead whisper.cpp itself answered.
        Assert.Contains("could not load the model", ex.Message);
        Assert.DoesNotContain("DllNotFound", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unable to load", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_model_in_a_folder_with_an_arabic_name_is_opened_the_same_way()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("نماذج");
        Directory.CreateDirectory(folder);
        var fakeModel = Path.Combine(folder, "ggml-tiny.bin");
        File.WriteAllBytes(fakeModel, Hashing.Bytes(4096));
        using var transcriber = new WhisperNetTranscriber();

        var ex = await Assert.ThrowsAsync<SpeechEngineException>(() =>
            transcriber.TranscribeAsync(fakeModel, new float[16000], new TranscribeSettings("ar"), null, default));
        Assert.Contains("could not load the model", ex.Message);
    }

    [Fact]
    public void Whisper_reports_the_runtime_it_loaded()
    {
        // Querying the runtime info forces the native library to load; a missing CPU runtime would throw here.
        var info = Whisper.net.WhisperFactory.GetRuntimeInfo();
        Assert.NotNull(info);
        Assert.NotNull(Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary);
    }
}

/// <summary>Real ffmpeg and real Whisper; each is skipped, visibly, when its dependency is missing.</summary>
public class SpeechIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task Real_ffmpeg_converts_any_wav_to_the_format_whisper_reads()
    {
        using var dir = new TempDir();
        var input = Samples.Wav(dir.Combine("tone.wav"), seconds: 2, sampleRate: 8000);
        var output = dir.Combine("out.wav");

        var run = await DropFlow.Engines.Common.ProcessRunner.RunAsync(
            DropFlow.TestSupport.Engines.Locator.Locate(EngineId.Ffmpeg)!, FfmpegAudio.BuildExtractArguments(input, output));

        Assert.True(run.Success, run.LastMessage);
        var info = WavFile.TryReadInfo(output);
        Assert.NotNull(info);
        Assert.True(info!.IsWhisperReady);
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 2.1);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Real_ffmpeg_reports_a_video_without_sound_as_having_no_audio_track()
    {
        using var dir = new TempDir();
        var ffmpeg = DropFlow.TestSupport.Engines.Locator.Locate(EngineId.Ffmpeg)!;
        var silent = dir.Combine("silent.mp4");
        var make = await DropFlow.Engines.Common.ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:d=1", "-pix_fmt", "yuv420p", silent]);
        Assert.True(make.Success, make.LastMessage);

        using var models = new ModelManager(dir.Combine("models"));
        var converter = new TranscribeConverter(DropFlow.TestSupport.Engines.Locator, models);
        // no model installed: the model check comes first, so install a placeholder under the right size to reach the ffmpeg step
        var catalog = ModelCatalog.Default.Select(m => m.Id == "ggml-tiny" ? m with { SizeBytes = 8 } : m).ToList();
        Directory.CreateDirectory(dir.Combine("models"));
        File.WriteAllBytes(Path.Combine(dir.Combine("models"), "ggml-tiny.bin"), new byte[8]);
        using var models2 = new ModelManager(dir.Combine("models"), catalog);
        var converter2 = new TranscribeConverter(DropFlow.TestSupport.Engines.Locator, models2, new NeverCalledTranscriber());

        var result = await converter2.RunAsync(new ConversionJob([silent], "vid.transcribe", new Dictionary<string, object?> { ["model"] = "ggml-tiny" }), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.Contains("no audio track", result.Error);
        _ = converter;
    }

    private sealed class NeverCalledTranscriber : ISpeechTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(string modelPath, float[] samples, TranscribeSettings settings, IProgress<double>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("must not get this far");
    }

    [ModelFact("ggml-tiny")]
    public async Task Real_whisper_tiny_loads_and_transcribes_silence_without_crashing()
    {
        using var models = ModelFactAttribute.OpenManager();
        using var transcriber = new WhisperNetTranscriber();

        // 3 seconds of silence: the model must load, run and return (typically no segments or a [BLANK_AUDIO] tag).
        var result = await transcriber.TranscribeAsync(models.PathOf("ggml-tiny"), new float[16000 * 3], new TranscribeSettings("en"), null, default);

        Assert.All(result.Segments, s => Assert.True(s.End >= s.Start));
        var cleaned = TextNormalizer.Clean(result.Segments);
        Assert.True(cleaned.Count <= 2, "silence should not produce a transcript: " + string.Join(" | ", cleaned.Select(c => c.Text)));
    }
}

/// <summary>Speech made by Windows' own voices, then pushed through the real pipeline.</summary>
public class SpokenAudioTests
{
    private const string Sentence = "Hello world. This is a test of speech recognition.";

    [Fact]
    public async Task The_speech_fixture_produces_audible_whisper_ready_audio()
    {
        using var dir = new TempDir();
        var wav = await SpokenAudio.GenerateAsync(dir.Combine("speech.wav"), Sentence, dir.Path);

        var info = WavFile.TryReadInfo(wav);
        Assert.NotNull(info);
        Assert.True(info!.IsWhisperReady);
        Assert.InRange(info.Duration.TotalSeconds, 2.0, 15.0);
        var samples = WavFile.ReadWhisperSamples(wav);
        var rms = Math.Sqrt(samples.Average(s => (double)s * s));
        Assert.True(rms > 0.01, $"the synthesised speech is silent (rms {rms:0.0000})");
    }

    [ModelFact("ggml-tiny")]
    public async Task Real_whisper_tiny_transcribes_spoken_english_into_an_srt_file()
    {
        using var dir = new TempDir();
        var wav = await SpokenAudio.GenerateAsync(dir.Combine("speech.wav"), Sentence, dir.Path);
        using var models = ModelFactAttribute.OpenManager();
        using var converter = new TranscribeConverter(new FakeEngines(), models);

        var result = await converter.RunAsync(
            new ConversionJob([wav], "aud.transcribe", new Dictionary<string, object?> { ["model"] = "ggml-tiny", ["language"] = "en", ["outputs"] = "srt,txt,json" }),
            new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Outputs.Count);
        var text = File.ReadAllText(result.Outputs[1]);
        Assert.Contains("hello", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("speech", text, StringComparison.OrdinalIgnoreCase);
        var srt = File.ReadAllText(result.Outputs[0]);
        Assert.Matches(@"1\r\n\d\d:\d\d:\d\d,\d\d\d --> \d\d:\d\d:\d\d,\d\d\d\r\n", srt);
    }
}

public class ResourceLifetimeTests
{
    [Fact]
    public async Task Idle_onnx_sessions_are_released_and_load_again_on_demand()
    {
        using var dir = new TempDir();
        var path = dir.Combine("m.onnx");
        File.WriteAllBytes(path, OnnxTestModel.BrightnessMask(8, 8));
        using var cache = new OnnxSessionCache(idleTimeout: TimeSpan.FromMilliseconds(150));

        cache.Run(path, s => 0);
        Assert.Equal(1, cache.LoadedCount);
        await WaitUntil(() => cache.LoadedCount == 0);
        Assert.Equal(0, cache.LoadedCount);

        Assert.Equal(8, cache.Run(path, s => s.InputMetadata.First().Value.Dimensions[2]));   // transparently reloaded
        Assert.Equal(1, cache.LoadedCount);
    }

    [Fact]
    public async Task A_session_in_use_is_never_released_underneath_the_caller()
    {
        using var dir = new TempDir();
        var path = dir.Combine("m.onnx");
        File.WriteAllBytes(path, OnnxTestModel.BrightnessMask(8, 8));
        using var cache = new OnnxSessionCache(idleTimeout: TimeSpan.FromMilliseconds(50));

        var height = cache.Run(path, s =>
        {
            Thread.Sleep(400);                                   // far longer than the idle timeout
            return s.InputMetadata.First().Value.Dimensions[2];  // would throw if the session had been disposed
        });
        Assert.Equal(8, height);
        await WaitUntil(() => cache.LoadedCount == 0);
    }

    [Fact]
    public void A_disposed_cache_refuses_work_and_converters_dispose_only_what_they_own()
    {
        var cache = new OnnxSessionCache();
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Run("x", s => 0));
        cache.Dispose();   // idempotent

        using var dir = new TempDir();
        using var shared = new OnnxSessionCache();
        var models = new ModelManager(dir.Combine("m"));
        new DropFlow.AI.Imaging.BackgroundRemovalConverter(models, shared).Dispose();      // does not own `shared`
        var model = dir.Combine("m.onnx");
        File.WriteAllBytes(model, OnnxTestModel.BrightnessMask(8, 8));
        shared.Run(model, s => 0);                                                          // still usable
        Assert.Equal(1, shared.LoadedCount);

        // a converter that created its own cache disposes it
        var own = new DropFlow.AI.Imaging.BackgroundRemovalConverter(models);
        own.Dispose();
        own.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
    }
}
