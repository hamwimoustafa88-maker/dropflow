using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.TestSupport;
using static DropFlow.Engines.Media.Tests.Integration.FfmpegSamples;

namespace DropFlow.Engines.Media.Tests.Integration;

/// <summary>
/// End-to-end conversions with the real FFmpeg. Skipped when it is not installed. Every result is read back with ffprobe and checked
/// for the codec the conversion promises, the right streams and (roughly) the same duration.
/// </summary>
public class ConversionIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task Every_audio_format_converts_with_the_documented_codec_and_keeps_its_length()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "tone.wav"), seconds: 3);
        var expected = new Dictionary<string, string>
        {
            ["mp3"] = "mp3", ["m4a"] = "aac", ["flac"] = "flac", ["ogg"] = "vorbis", ["opus"] = "opus",
            ["aiff"] = "pcm_s16be", ["wma"] = "wmav2", ["aac"] = "aac",
        };

        foreach (var (target, codec) in expected)
        {
            var result = await Run("convert:" + target, input);
            Assert.True(result.Success, $"wav -> {target}: {result.Error}");
            var info = await Probe(result.Outputs.Single());
            Assert.True(info.HasAudio && !info.HasVideo, target);
            Assert.Equal(codec, info.Audio!.CodecName);
            Assert.InRange(info.DurationSeconds!.Value, 2.5, 3.5);
        }
        Assert.True(File.Exists(input));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Flac_to_wav_and_back_is_lossless_in_length_and_format()
    {
        using var dir = new TempDir();
        var flac = (await Run("convert:flac", await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 2))).Outputs.Single();
        var wav = await Run("convert:wav", flac);
        Assert.True(wav.Success, wav.Error);
        var info = await Probe(wav.Outputs.Single());
        Assert.Equal("pcm_s16le", info.Audio!.CodecName);
        Assert.Equal(44100, info.Audio.SampleRate);
        Assert.InRange(info.DurationSeconds!.Value, 1.9, 2.1);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Video_converts_to_every_target_with_the_documented_codecs()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "clip.mp4"), seconds: 6);

        (string Target, string Video, string Audio)[] cases =
        [
            ("mkv", "h264", "aac"),      // streams copied
            ("webm", "vp9", "opus"),
            ("avi", "mpeg4", "mp3"),
            ("wmv", "wmv2", "wmav2"),
            ("mov", "h264", "aac"),
        ];
        foreach (var (target, video, audio) in cases)
        {
            var result = await Run("convert:" + target, input);
            Assert.True(result.Success, $"mp4 -> {target}: {result.Error}");
            var info = await Probe(result.Outputs.Single());
            Assert.Equal(video, info.Video?.CodecName);
            Assert.Equal(audio, info.Audio?.CodecName);
            Assert.Equal(320, info.Video!.Width);
            Assert.Equal(240, info.Video.Height);
            Assert.InRange(info.DurationSeconds!.Value, 5.5, 6.5);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Video_to_gif_is_silent_looping_and_not_wider_than_480()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "wide.mp4"), seconds: 3, width: 1280, height: 720);
        var result = await Run("convert:gif", input);
        Assert.True(result.Success, result.Error);
        var info = await Probe(result.Outputs.Single());
        Assert.Equal("gif", info.Video!.CodecName);
        Assert.False(info.HasAudio);
        Assert.Equal(480, info.Video.Width);
        // GIF delays are whole hundredths of a second: 15 fps is stored as 6 cs = 16.67 fps
        Assert.InRange(info.Video.FrameRate!.Value, 10, 17);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task An_animated_gif_converts_to_mp4_even_with_odd_dimensions()
    {
        using var dir = new TempDir();
        var gif = Path.Combine(dir.Path, "loop.gif");
        await RunFfmpeg("-f", "lavfi", "-i", "testsrc=duration=2:size=321x241:rate=10", gif);   // odd size: x264 would refuse it without the even-size filter

        var result = await Run("convert:mp4", gif);

        Assert.True(result.Success, result.Error);
        var info = await Probe(result.Outputs.Single());
        Assert.Equal("h264", info.Video!.CodecName);
        Assert.True(info.Video.Width % 2 == 0 && info.Video.Height % 2 == 0);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Video_to_audio_extracts_the_sound()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "clip.mp4"), seconds: 4);
        foreach (var (target, codec) in new[] { ("mp3", "mp3"), ("m4a", "aac"), ("wav", "pcm_s16le") })
        {
            var result = await Run("convert:" + target, input);
            Assert.True(result.Success, $"mp4 -> {target}: {result.Error}");
            var info = await Probe(result.Outputs.Single());
            Assert.False(info.HasVideo, target);
            Assert.Equal(codec, info.Audio!.CodecName);
            Assert.InRange(info.DurationSeconds!.Value, 3.5, 4.5);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Arabic_and_spaced_paths_work_end_to_end()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "مجلد عربي مع مسافات");
        Directory.CreateDirectory(folder);
        var input = await Video(Path.Combine(folder, "فيديو عائلي (نسخة 2).mp4"), seconds: 2);

        var result = await Run("convert:mkv", input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(folder, "فيديو عائلي (نسخة 2) (converted).mkv"), result.Outputs.Single());
        Assert.NotNull((await Probe(result.Outputs.Single())).Video);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_second_conversion_never_overwrites_the_first()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 1);
        var first = await Run("convert:mp3", input);
        var firstBytes = await File.ReadAllBytesAsync(first.Outputs.Single());
        var second = await Run("convert:mp3", input);

        Assert.NotEqual(first.Outputs.Single(), second.Outputs.Single());
        Assert.EndsWith("a (converted 2).mp3", second.Outputs.Single());
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first.Outputs.Single()));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_corrupt_file_fails_with_a_message_and_leaves_nothing()
    {
        using var dir = new TempDir();
        var broken = Path.Combine(dir.Path, "broken.mp4");
        await File.WriteAllBytesAsync(broken, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251))]);

        var result = await Run("convert:mkv", broken);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_text_file_renamed_to_mp3_fails_cleanly()
    {
        using var dir = new TempDir();
        var fake = dir.WriteText("notes.mp3", "this is not audio at all");
        var result = await Run("convert:wav", fake);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Cancelling_a_slow_conversion_stops_ffmpeg_and_removes_the_partial_file()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "long.mp4"), seconds: 40, width: 1280, height: 720);
        using var cts = new CancellationTokenSource();

        var job = Run("convert:webm", input, ct: cts.Token); // VP9 at 720p takes far longer than the 400 ms we allow
        await Task.Delay(400);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(["long.mp4"], Directory.GetFiles(dir.Path).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Progress_advances_between_zero_and_one_during_a_real_encode()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "p.mp4"), seconds: 20, width: 640, height: 360);
        var reports = new List<double>();

        var result = await Run("convert:webm", input, progress: new InlineProgress(p => { lock (reports) reports.Add(p.Fraction); }));

        Assert.True(result.Success, result.Error);
        lock (reports)
        {
            Assert.Equal(reports.OrderBy(r => r), reports);
            Assert.Contains(reports, r => r is > 0.02 and < 0.98);
            Assert.Equal(1, reports[^1]);
        }
    }

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
