using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.TestSupport;
using TestEngines = DropFlow.TestSupport.Engines;
using static DropFlow.Engines.Media.Tests.Integration.FfmpegSamples;

namespace DropFlow.Engines.Media.Tests.Integration;

/// <summary>
/// The pure parsers and the planner checked against the real programs: what they print must be what the parsers expect, and the
/// plans must be accepted by FFmpeg. Skipped when FFmpeg is not installed.
/// </summary>
public class ParsersAgainstRealOutputTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_encoder_list_parser_reads_the_real_encoders_output()
    {
        var result = await ProcessRunner.RunAsync(FfmpegExe, FfmpegCommand.ListEncoders());
        Assert.True(result.Success, result.LastMessage);
        var encoders = FfmpegEncoders.Parse(result.StdOut);

        Assert.True(encoders.Names.Count > 50, $"only {encoders.Names.Count} encoders parsed");
        Assert.True(encoders.Has("aac"));
        Assert.True(encoders.Has("flac"));
        Assert.True(encoders.Has("mpeg4"));
        Assert.True(encoders.Has("gif"));
        Assert.False(encoders.Has("Video"));
        Assert.False(encoders.Has("="));

        var hardware = HardwareEncoders.Detect(encoders);     // must not throw whatever the build contains
        Assert.All(hardware.Encoders, e => Assert.True(encoders.Has(e.Name)));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_probe_parser_reads_real_ffprobe_json()
    {
        using var dir = new TempDir();
        var path = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 6);
        var info = await Probe(path);

        Assert.Equal("h264", info.Video!.CodecName);
        Assert.Equal(320, info.Video.Width);
        Assert.Equal(240, info.Video.Height);
        Assert.InRange(info.Video.FrameRate!.Value, 24.9, 25.1);
        Assert.Equal("yuv420p", info.Video.PixelFormat);
        Assert.Equal("aac", info.Audio!.CodecName);
        Assert.Equal(44100, info.Audio.SampleRate);
        Assert.Equal(1, info.Audio.Channels);
        Assert.InRange(info.DurationSeconds!.Value, 5.9, 6.2);
        Assert.False(info.Video.IsInterlaced);
        Assert.False(info.Video.IsHdr);
        Assert.Equal(path, info.Path);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_progress_parser_reads_a_real_progress_stream()
    {
        using var dir = new TempDir();
        var parser = new FfmpegProgressParser();
        var snapshots = new List<FfmpegProgress>();
        var result = await ProcessRunner.RunAsync(FfmpegExe,
            ["-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=25", "-c:v", "libx264", "-preset", "ultrafast", Path.Combine(dir.Path, "p.mp4")],
            new ProcessOptions { OnOutputLine = line => { if (parser.Feed(line) is { } p) lock (snapshots) snapshots.Add(p); } });

        Assert.True(result.Success, result.LastMessage);
        lock (snapshots)
        {
            Assert.NotEmpty(snapshots);
            Assert.True(snapshots[^1].IsEnd);
            Assert.InRange(snapshots[^1].OutSeconds, 2.9, 3.1);
            Assert.Equal(1, snapshots[^1].Fraction(3));
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_silencedetect_parser_reads_real_output()
    {
        using var dir = new TempDir();
        var input = await SilenceToneSilence(Path.Combine(dir.Path, "sts.wav"));
        var result = await ProcessRunner.RunAsync(FfmpegExe, AudioToolArgs.Analyze(input, SilenceDetect.Filter(-50, 0.5)));
        Assert.True(result.Success, result.LastMessage);

        var silences = SilenceDetect.Parse(result.StdErr);
        Assert.Equal(2, silences.Count);
        Assert.InRange(silences[0].Start, 0, 0.05);
        Assert.InRange(silences[0].End ?? 99, 0.95, 1.1);
        Assert.InRange(silences[1].Start, 1.95, 2.1);

        var range = SilenceDetect.EdgeTrim(silences, 3.0)!.Value;
        Assert.InRange(range.Start, 0.85, 1.0);
        Assert.InRange(range.End, 2.0, 2.2);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_loudnorm_parser_reads_real_output()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 5);
        var result = await ProcessRunner.RunAsync(FfmpegExe, AudioToolArgs.Analyze(input, Loudnorm.Pass1Filter(-16, -1.5, 11)));
        Assert.True(result.Success, result.LastMessage);

        var stats = Loudnorm.ParseStats(result.StdErr);
        Assert.NotNull(stats);
        Assert.True(stats!.IsUsable);
        Assert.InRange(double.Parse(stats.InputI, System.Globalization.CultureInfo.InvariantCulture), -40, -10);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_filter_escaper_produces_graphs_that_the_real_ffmpeg_accepts_for_hostile_paths()
    {
        using var dir = new TempDir();
        var video = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);
        var hostile = Path.Combine(dir.Path, "it's [1], v=2; ترجمة.srt");
        await File.WriteAllTextAsync(hostile, "1\n00:00:00,500 --> 00:00:01,500\nHello\n");

        // Direct use of the absolute, hostile path (no scratch-folder trick): the escaping alone must carry it.
        var output = Path.Combine(dir.Path, "out.mp4");
        var result = await ProcessRunner.RunAsync(FfmpegExe,
            ["-hide_banner", "-nostdin", "-y", "-i", video, "-vf", ToolFilters.Subtitles(hostile, Environment.GetFolderPath(Environment.SpecialFolder.Fonts)), "-an", output]);

        Assert.True(result.Success, result.LastMessage);
        Assert.True(new FileInfo(output).Length > 0);
    }
}

public class EncoderIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task Probe_plan_and_run_through_the_public_encoder_api()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 4);
        var info = await MediaEncoder.ProbeAsync(TestEngines.Locator, input);

        var plan = await MediaEncoder.PlanAsync(TestEngines.Locator, info, EncoderPresets.YouTube1080p with { Hardware = HardwarePreference.None });
        Assert.True(plan.Ok, plan.Error);
        Assert.Equal("mp4", plan.OutputExtension);

        var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, EncoderPresets.YouTube1080p with { Hardware = HardwarePreference.None }, new Progress<JobProgress>());
        Assert.True(result.Success, result.Error);
        Assert.EndsWith("a (encoded).mp4", result.Outputs.Single());
        Assert.Equal("h264", (await Probe(result.Outputs.Single())).Video!.CodecName);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Audio_only_presets_extract_the_sound()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 3);

        foreach (var (preset, ext, codec) in new[]
                 {
                     (EncoderPresets.AudioMp3_192k, "mp3", "mp3"),
                     (EncoderPresets.AudioAacM4a_192k, "m4a", "aac"),
                     (EncoderPresets.AudioFlacLossless, "flac", "flac"),
                     (EncoderPresets.AudioOpus_96k, "opus", "opus"),
                 })
        {
            var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, preset, new Progress<JobProgress>(), suffix: "audio " + ext);
            Assert.True(result.Success, $"{ext}: {result.Error}");
            Assert.EndsWith("." + ext, result.Outputs.Single());
            var info = await Probe(result.Outputs.Single());
            Assert.False(info.HasVideo);
            Assert.Equal(codec, info.Audio!.CodecName);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Reels_preset_fills_or_letterboxes_a_1080x1920_canvas()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);

        foreach (var mode in new[] { FitMode.Crop, FitMode.Letterbox })
        {
            var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, EncoderPresets.Reels9x16(mode), new Progress<JobProgress>(), suffix: "reels " + mode);
            Assert.True(result.Success, $"{mode}: {result.Error}");
            var video = (await Probe(result.Outputs.Single())).Video!;
            Assert.Equal((1080, 1920), (video.Width, video.Height));
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Gif_preset_makes_a_looping_gif()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);
        var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, EncoderPresets.GifLoop, new Progress<JobProgress>());
        Assert.True(result.Success, result.Error);
        Assert.EndsWith(".gif", result.Outputs.Single());
        Assert.Equal("gif", (await Probe(result.Outputs.Single())).Video!.CodecName);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Size_limited_presets_stay_under_their_limit()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 40, width: 640, height: 360);
        var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, EncoderPresets.Discord10MB, new Progress<JobProgress>());
        Assert.True(result.Success, result.Error);
        Assert.True(new FileInfo(result.Outputs.Single()).Length <= 10_000_000);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task H265_av1_and_vp9_encode_when_the_build_has_them()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);
        var encoders = FfmpegEncoders.Parse((await ProcessRunner.RunAsync(FfmpegExe, FfmpegCommand.ListEncoders())).StdOut);

        foreach (var (settings, encoder, codec) in new[]
                 {
                     (new EncoderSettings { VideoCodec = VideoCodec.H265, Hardware = HardwarePreference.None }, "libx265", "hevc"),
                     (new EncoderSettings { VideoCodec = VideoCodec.Av1, Container = ContainerFormat.Mkv, Hardware = HardwarePreference.None }, "libsvtav1", "av1"),
                     (new EncoderSettings { VideoCodec = VideoCodec.Vp9, Container = ContainerFormat.Webm, Hardware = HardwarePreference.None }, "libvpx-vp9", "vp9"),
                 })
        {
            if (!encoders.Has(encoder)) continue;   // a lean build: the planner would warn/fall back, which the unit tests cover
            var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, settings, new Progress<JobProgress>(), suffix: codec);
            Assert.True(result.Success, $"{codec}: {result.Error}");
            Assert.Equal(codec, (await Probe(result.Outputs.Single())).Video!.CodecName);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Hardware_auto_never_fails_a_job_even_without_a_gpu()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);
        var result = await MediaEncoder.RunAsync(TestEngines.Locator, input, new EncoderSettings { Hardware = HardwarePreference.Auto }, new Progress<JobProgress>());
        Assert.True(result.Success, result.Error);   // trial encode vets the GPU, otherwise software is used
    }
}
