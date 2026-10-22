using System.Security.Cryptography;
using DropFlow.AI.Models;
using DropFlow.AI.Speech;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

public class FfmpegAudioTests
{
    [Fact]
    public void Extract_arguments_ask_for_16k_mono_pcm_wav_from_the_first_audio_stream()
    {
        var args = FfmpegAudio.BuildExtractArguments(@"C:\media\clip.mp4", @"C:\tmp\audio.wav");
        Assert.Equal(@"C:\media\clip.mp4", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal(@"C:\tmp\audio.wav", args[^1]);
        AssertPair(args, "-ar", "16000");
        AssertPair(args, "-ac", "1");
        AssertPair(args, "-c:a", "pcm_s16le");
        AssertPair(args, "-f", "wav");
        AssertPair(args, "-map", "0:a:0");
        Assert.Contains("-vn", args);
        Assert.Contains("-nostdin", args);
        Assert.Contains("-y", args);
    }

    [Fact]
    public void Unicode_and_spaced_paths_stay_single_arguments()
    {
        var input = @"C:\مقاطع\اجتماع اليوم (1).mp4";
        var args = FfmpegAudio.BuildExtractArguments(input, @"C:\tmp dir\a b.wav");
        Assert.Contains(input, args);
        Assert.Contains(@"C:\tmp dir\a b.wav", args);
        Assert.DoesNotContain(args, a => a.Contains('"'));
    }

    [Theory]
    [InlineData("Stream map '0:a:0' matches no streams.", "no audio track")]
    [InlineData("Output file #0 does not contain any stream", "no audio track")]
    [InlineData("Invalid data found when processing input", "damaged")]
    [InlineData("moov atom not found", "damaged")]
    [InlineData("C:\\x.mp4: No such file or directory", "could not open")]
    public void Failures_are_explained(string ffmpegSays, string expectedFragment) =>
        Assert.Contains(expectedFragment, FfmpegAudio.ExplainFailure(ffmpegSays), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Unknown_failures_pass_the_message_through()
    {
        Assert.Contains("boom", FfmpegAudio.ExplainFailure("boom"));
        Assert.False(string.IsNullOrWhiteSpace(FfmpegAudio.ExplainFailure(null)));
    }

    private static void AssertPair(IReadOnlyList<string> args, string flag, string value)
    {
        var i = args.ToList().IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"missing {flag}");
        Assert.Equal(value, args[i + 1]);
    }
}

public class WavFileTests
{
    [Fact]
    public void Reads_the_header_of_a_generated_wav()
    {
        using var dir = new TempDir();
        var path = Samples.Wav(dir.Combine("a.wav"), seconds: 2, sampleRate: 16000);
        var info = WavFile.TryReadInfo(path);
        Assert.NotNull(info);
        Assert.Equal(16000, info!.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.True(info.IsWhisperReady);
        Assert.Equal(64000, info.DataLength);
        Assert.Equal(2.0, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Other_sample_rates_are_not_whisper_ready()
    {
        using var dir = new TempDir();
        Assert.False(WavFile.TryReadInfo(Samples.Wav(dir.Combine("a.wav"), sampleRate: 8000))!.IsWhisperReady);
    }

    [Fact]
    public void Samples_are_scaled_to_minus_one_to_one()
    {
        using var dir = new TempDir();
        var path = Samples.Wav(dir.Combine("tone.wav"), seconds: 1, sampleRate: 16000, frequency: 440);
        var samples = WavFile.ReadWhisperSamples(path);
        Assert.Equal(16000, samples.Length);
        Assert.InRange(samples.Max(), 0.45f, 0.51f);   // Samples.Wav writes a tone at half scale
        Assert.InRange(samples.Min(), -0.51f, -0.45f);
    }

    [Fact]
    public void Non_wav_and_wrong_format_files_are_rejected_clearly()
    {
        using var dir = new TempDir();
        var junk = dir.WriteText("junk.wav", "this is not a wav file at all");
        Assert.Null(WavFile.TryReadInfo(junk));
        Assert.Throws<InvalidDataException>(() => WavFile.ReadWhisperSamples(junk));

        var wrongRate = Samples.Wav(dir.Combine("8k.wav"), sampleRate: 8000);
        var ex = Assert.Throws<InvalidDataException>(() => WavFile.ReadWhisperSamples(wrongRate));
        Assert.Contains("8000", ex.Message);
        Assert.Null(WavFile.TryReadInfo(dir.Combine("missing.wav")));
    }

    [Fact]
    public void A_wav_with_extra_chunks_before_the_data_is_still_read()
    {
        using var dir = new TempDir();
        var plain = File.ReadAllBytes(Samples.Wav(dir.Combine("p.wav"), seconds: 1, sampleRate: 16000));
        // insert a LIST chunk (odd size, so it needs a pad byte) between "fmt " and "data"
        var dataAt = FindAscii(plain, "data");
        using var ms = new MemoryStream();
        ms.Write(plain, 0, dataAt);
        ms.Write("LIST"u8); ms.Write(BitConverter.GetBytes(5)); ms.Write([1, 2, 3, 4, 5, 0]);
        ms.Write(plain, dataAt, plain.Length - dataAt);
        var withList = dir.Combine("list.wav");
        File.WriteAllBytes(withList, ms.ToArray());

        var info = WavFile.TryReadInfo(withList);
        Assert.NotNull(info);
        Assert.True(info!.IsWhisperReady);
        Assert.Equal(16000, WavFile.ReadWhisperSamples(withList).Length);
    }

    private static int FindAscii(byte[] data, string text)
    {
        var pattern = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i <= data.Length - pattern.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        throw new InvalidOperationException();
    }
}

public class TranscribeConverterTests
{
    private sealed class FakeTranscriber(Func<string, float[], TranscribeSettings, CancellationToken, Task<TranscriptionResult>> run) : ISpeechTranscriber
    {
        public int Calls { get; private set; }
        public string? ModelPath { get; private set; }
        public TranscribeSettings? Settings { get; private set; }
        public int SampleCount { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(string modelPath, float[] samples, TranscribeSettings settings, IProgress<double>? progress, CancellationToken ct)
        {
            Calls++;
            ModelPath = modelPath;
            Settings = settings;
            SampleCount = samples.Length;
            progress?.Report(0.5);
            return run(modelPath, samples, settings, ct);
        }
    }

    private static TranscriptionResult Speech() => new(
        [
            new TranscriptSegment(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1), "Hello world"),
            new TranscriptSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "مرحبا بالعالم"),
        ], "en");

    /// <summary>A manager whose catalog has one small "installed" model per requested id (the files are placeholders; the transcriber is fake).</summary>
    private static ModelManager ModelsWith(TempDir dir, params string[] installedIds)
    {
        var catalog = ModelCatalog.Default
            .Select(m => installedIds.Contains(m.Id) ? m with { SizeBytes = 64 } : m)
            .ToList();
        var folder = dir.Combine("models");
        Directory.CreateDirectory(folder);
        foreach (var id in installedIds)
            File.WriteAllBytes(Path.Combine(folder, catalog.First(m => m.Id == id).FileName), new byte[64]);
        return new ModelManager(folder, catalog);
    }

    private static TranscribeConverter Converter(ModelManager models, FakeTranscriber fake, IEngineProvider? engines = null, long ram = 16L << 30) =>
        new(engines ?? new FakeEngines(), models, fake, () => ram);

    private static ConversionJob Job(string input, string op = "aud.transcribe", Dictionary<string, object?>? options = null, string? outDir = null) =>
        new([input], op, options, outDir);

    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    [Fact]
    public async Task Transcribes_a_16k_wav_end_to_end_and_writes_srt_next_to_it()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var input = Samples.Wav(dir.Combine("talk.wav"), seconds: 2, sampleRate: 16000);
        var before = SHA256.HashData(File.ReadAllBytes(input));

        var result = await Converter(models, fake).RunAsync(Job(input), NoProgress, default);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("talk (transcript).srt"), output);
        var srt = File.ReadAllText(output);
        Assert.Contains("00:00:00,000 --> 00:00:01,000", srt);
        Assert.Contains("Hello world", srt);
        Assert.Contains(RtlText.RightToLeftMark + "مرحبا بالعالم" + RtlText.RightToLeftMark, srt, StringComparison.Ordinal);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(32000, fake.SampleCount);
        Assert.Equal(models.PathOf("ggml-small"), fake.ModelPath);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(input))); // input untouched
    }

    [Fact]
    public async Task Writes_every_requested_format_with_one_shared_name_and_never_overwrites()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var input = Samples.Wav(dir.Combine("talk.wav"), sampleRate: 16000);
        var options = new Dictionary<string, object?> { ["outputs"] = "srt,txt" };

        // an unrelated file already holds the .txt name: the whole set must move to "(transcript 2)" together
        File.WriteAllText(dir.Combine("talk (transcript).txt"), "mine");
        var first = await Converter(models, fake).RunAsync(Job(input, options: options), NoProgress, default);
        Assert.Equal([dir.Combine("talk (transcript 2).srt"), dir.Combine("talk (transcript 2).txt")], first.Outputs);
        Assert.Equal("mine", File.ReadAllText(dir.Combine("talk (transcript).txt")));

        var second = await Converter(models, fake).RunAsync(Job(input, options: options), NoProgress, default);
        Assert.Equal([dir.Combine("talk (transcript 3).srt"), dir.Combine("talk (transcript 3).txt")], second.Outputs);
        Assert.True(File.Exists(first.Outputs[0]) && File.Exists(first.Outputs[1]));

        var json = await Converter(models, fake).RunAsync(Job(input, options: new() { ["outputs"] = "json" }), NoProgress, default);
        Assert.Equal([dir.Combine("talk (transcript).json")], json.Outputs);
    }

    [Fact]
    public async Task Honours_the_output_directory_and_unicode_paths()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var arabicFolder = dir.Combine("تسجيلات اليوم");
        Directory.CreateDirectory(arabicFolder);
        var input = Samples.Wav(Path.Combine(arabicFolder, "اجتماع الفريق.wav"), sampleRate: 16000);

        var beside = await Converter(models, fake).RunAsync(Job(input), NoProgress, default);
        Assert.True(beside.Success, beside.Error);
        Assert.Equal(Path.Combine(arabicFolder, "اجتماع الفريق (transcript).srt"), beside.Outputs[0]);

        var elsewhere = dir.Combine("out");
        Directory.CreateDirectory(elsewhere);
        var moved = await Converter(models, fake).RunAsync(Job(input, outDir: elsewhere), NoProgress, default);
        Assert.Equal(Path.Combine(elsewhere, "اجتماع الفريق (transcript).srt"), moved.Outputs[0]);
    }

    [Fact]
    public async Task Passes_language_translation_and_line_settings_through()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small", "ggml-medium-q5_0");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(new TranscriptionResult(
            [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4), "The quick brown fox jumps over the lazy dog today")], "ar")));
        var input = Samples.Wav(dir.Combine("t.wav"), sampleRate: 16000);

        var result = await Converter(models, fake).RunAsync(Job(input, options: new()
        {
            ["language"] = "ar", ["translateToEnglish"] = true, ["maxCharsPerLine"] = 20, ["rtlMarks"] = "none", ["outputs"] = "json",
        }), NoProgress, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("ar", fake.Settings!.Language);
        Assert.True(fake.Settings.TranslateToEnglish);
        Assert.Equal(models.PathOf("ggml-medium-q5_0"), fake.ModelPath); // translation picks medium, not turbo/small
        var json = File.ReadAllText(result.Outputs[0]);
        Assert.Contains("\"translatedToEnglish\": true", json);
        Assert.Contains("\"language\": \"en\"", json);
        Assert.Contains("\"model\": \"ggml-medium-q5_0\"", json);
    }

    [Fact]
    public async Task Auto_model_picks_the_best_installed_one_for_free_memory()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-tiny", "ggml-small", "ggml-large-v3-turbo-q5_0");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var input = Samples.Wav(dir.Combine("t.wav"), sampleRate: 16000);

        await Converter(models, fake, ram: 16L << 30).RunAsync(Job(input, options: new() { ["language"] = "ar" }), NoProgress, default);
        Assert.Equal(models.PathOf("ggml-large-v3-turbo-q5_0"), fake.ModelPath);

        await Converter(models, fake, ram: 3L << 30).RunAsync(Job(input, options: new() { ["language"] = "ar" }), NoProgress, default);
        Assert.Equal(models.PathOf("ggml-small"), fake.ModelPath);
    }

    [Fact]
    public async Task Missing_model_fails_naming_the_model_so_the_app_can_offer_the_download()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir); // nothing installed
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var input = Samples.Wav(dir.Combine("t.wav"), sampleRate: 16000);

        var auto = await Converter(models, fake).RunAsync(Job(input, options: new() { ["language"] = "ar" }), NoProgress, default);
        Assert.False(auto.Success);
        Assert.Equal("ggml-large-v3-turbo-q5_0", AiFailures.TryParseMissingModel(auto.Error));

        var chosen = await Converter(models, fake).RunAsync(Job(input, options: new() { ["model"] = "ggml-base" }), NoProgress, default);
        Assert.False(chosen.Success);
        Assert.Equal("ggml-base", AiFailures.TryParseMissingModel(chosen.Error));
        Assert.Contains("MB", chosen.Error);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(transcript*"));
    }

    [Fact]
    public async Task Incompatible_model_choices_are_refused_with_an_explanation()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-base.en", "ggml-large-v3-turbo-q5_0");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var input = Samples.Wav(dir.Combine("t.wav"), sampleRate: 16000);

        var english = await Converter(models, fake).RunAsync(Job(input, options: new() { ["model"] = "ggml-base.en", ["language"] = "ar" }), NoProgress, default);
        Assert.False(english.Success);
        Assert.Contains("only understands English", english.Error);

        var translate = await Converter(models, fake).RunAsync(Job(input, options: new() { ["model"] = "ggml-large-v3-turbo-q5_0", ["language"] = "ar", ["translateToEnglish"] = true }), NoProgress, default);
        Assert.False(translate.Success);
        Assert.Contains("cannot translate", translate.Error);

        var notWhisper = await Converter(models, fake).RunAsync(Job(input, options: new() { ["model"] = "u2netp" }), NoProgress, default);
        Assert.False(notWhisper.Success);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Needs_ffmpeg_for_anything_that_is_not_a_16k_mono_wav_and_says_so()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var mp3 = dir.WriteText("song.mp3", "not really an mp3");
        var wav8k = Samples.Wav(dir.Combine("low.wav"), sampleRate: 8000);

        foreach (var input in new[] { mp3, wav8k })
        {
            var result = await Converter(models, fake, new FakeEngines()).RunAsync(Job(input), NoProgress, default);
            Assert.False(result.Success);
            Assert.Contains("ffmpeg is not installed", result.Error);
        }
        Assert.Equal(0, fake.Calls);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(transcript*"));
    }

    [Fact]
    public async Task A_failing_ffmpeg_is_reported_with_its_reason_and_leaves_no_output()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var mp3 = dir.WriteText("song.mp3", "not really an mp3");
        // whoami.exe (Windows) or false (macOS/Linux) stands in for ffmpeg: it exits non-zero and produces no wav, like a corrupt input would.
        var cmd = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "whoami.exe") : new[] { "/usr/bin/false", "/bin/false" }.First(File.Exists);
        var engines = new FakeEngines((EngineId.Ffmpeg, cmd));

        var result = await Converter(models, fake, engines).RunAsync(Job(mp3), NoProgress, default);

        Assert.False(result.Success);
        Assert.Contains("ffmpeg", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(transcript*"));
    }

    [Fact]
    public async Task Silence_is_a_clear_failure_not_an_empty_file()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(new TranscriptionResult(
            [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "[BLANK_AUDIO]")], null)));
        var input = Samples.Wav(dir.Combine("quiet.wav"), sampleRate: 16000);

        var result = await Converter(models, fake).RunAsync(Job(input), NoProgress, default);
        Assert.False(result.Success);
        Assert.Contains("No speech", result.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(transcript*"));
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_leaves_no_files()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var started = new TaskCompletionSource();
        var fake = new FakeTranscriber(async (_, _, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Speech();
        });
        var input = Samples.Wav(dir.Combine("long.wav"), sampleRate: 16000);

        using var cts = new CancellationTokenSource();
        var run = Converter(models, fake).RunAsync(Job(input), NoProgress, cts.Token);
        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(transcript*"));
    }

    [Fact]
    public async Task Engine_load_failures_become_failed_results()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => throw new SpeechEngineException("Whisper could not load the model 'ggml-small.bin': bad magic"));
        var input = Samples.Wav(dir.Combine("t.wav"), sampleRate: 16000);

        var result = await Converter(models, fake).RunAsync(Job(input), NoProgress, default);
        Assert.False(result.Success);
        Assert.Contains("could not load the model", result.Error);
    }

    [Fact]
    public async Task Missing_and_unsupported_inputs_fail_politely()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, "ggml-small");
        var fake = new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech()));
        var c = Converter(models, fake);

        Assert.False((await c.RunAsync(Job(dir.Combine("nope.wav")), NoProgress, default)).Success);
        var png = Samples.Png(dir.Combine("pic.png"));
        var r = await c.RunAsync(Job(png), NoProgress, default);
        Assert.False(r.Success);
        Assert.Contains("not supported", r.Error);
        Assert.False((await c.RunAsync(new ConversionJob([], "aud.transcribe"), NoProgress, default)).Success);
    }

    // ---- Supports / options --------------------------------------------------------------------------------------

    [Fact]
    public void Supports_is_exactly_the_catalogs_audio_and_video_inputs()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir);
        var c = Converter(models, new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech())));
        var catalog = ToolCatalog.LoadEmbedded();
        var audio = catalog.Category("audio")!.Inputs;
        var video = catalog.Category("video")!.Inputs;

        foreach (var ext in audio) Assert.True(c.Supports("aud.transcribe", ext), ext);
        foreach (var ext in video.Where(e => e != "gif")) Assert.True(c.Supports("vid.transcribe", ext), ext);
        Assert.False(c.Supports("vid.transcribe", "gif")); // an animated GIF has no sound
        Assert.False(c.Supports("vid.transcribe", "GIF"));

        // each operation accepts only its own category
        foreach (var ext in video.Except(audio)) Assert.False(c.Supports("aud.transcribe", ext), ext);
        foreach (var ext in audio.Except(video)) Assert.False(c.Supports("vid.transcribe", ext), ext);
        foreach (var other in new[] { "png", "jpg", "pdf", "docx", "zip", "srt", "txt", "" })
        {
            Assert.False(c.Supports("aud.transcribe", other), other);
            Assert.False(c.Supports("vid.transcribe", other), other);
        }
        Assert.False(c.Supports("convert:mp3", "wav"));
        Assert.False(c.Supports("img.ocr", "mp3"));
        Assert.True(c.Supports("aud.transcribe", ".MP3".TrimStart('.'))); // case-insensitive
    }

    [Fact]
    public void Operations_exist_in_the_catalog_and_need_ffmpeg()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir);
        var c = Converter(models, new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech())));
        var catalog = ToolCatalog.LoadEmbedded();
        foreach (var op in c.Operations)
        {
            Assert.NotNull(catalog.Tool(op));
            Assert.Equal(EngineId.Ffmpeg, c.EngineFor(op));
        }
        Assert.Equal(["aud.transcribe", "vid.transcribe"], c.Operations.Order());
    }

    [Fact]
    public void Options_are_a_complete_documented_schema()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir);
        var c = Converter(models, new FakeTranscriber((_, _, _, _) => Task.FromResult(Speech())));
        var options = c.OptionsFor("aud.transcribe").ToDictionary(o => o.Key);

        Assert.Equal(["language", "maxCharsPerLine", "model", "outputs", "rtlMarks", "translateToEnglish"], options.Keys.Order());
        Assert.Equal("auto", options["model"].Default);
        Assert.Contains("auto", options["model"].Choices!);
        Assert.Contains("ggml-small", options["model"].Choices!);
        Assert.Contains("ar", options["language"].Choices!);
        Assert.Contains("en", options["language"].Choices!);
        Assert.Equal(ToolOptionKind.Bool, options["translateToEnglish"].Kind);
        Assert.Equal(["srt", "vtt", "txt", "json", "all"], options["outputs"].Choices);
        Assert.Equal(20, options["maxCharsPerLine"].Min);
        Assert.Equal(80, options["maxCharsPerLine"].Max);
        // every default is one of its own choices
        foreach (var o in options.Values.Where(o => o.Choices is not null))
            Assert.Contains(o.Default!.ToString(), o.Choices!);
    }
}
