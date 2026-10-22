using DropFlow.Engines.Media.Preview;
using DropFlow.TestSupport;
using static DropFlow.Engines.Media.Tests.Integration.FfmpegSamples;
using TestEngines = DropFlow.TestSupport.Engines;

namespace DropFlow.Engines.Media.Tests.Integration;

/// <summary>What the editor windows show and play, made by the real FFmpeg. Skipped when it is not installed.</summary>
public class PreviewIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_frame_comes_back_as_a_png_of_the_asked_height()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "فيديو قصير.mp4"), seconds: 3, width: 320, height: 240);

        var png = await MediaPreview.FrameAsync(TestEngines.Locator, input, 1.5, 90);

        Assert.NotNull(png);
        var file = Path.Combine(dir.Path, "f.png");
        await File.WriteAllBytesAsync(file, png!);
        Assert.Equal((120, 90), PngSize(file));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_frame_of_a_webm_and_of_a_file_that_is_not_video()
    {
        using var dir = new TempDir();
        var webm = Path.Combine(dir.Path, "a.webm");
        await RunFfmpeg("-f", "lavfi", "-i", "testsrc=duration=2:size=160x120:rate=10", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", webm);
        Assert.NotNull(await MediaPreview.FrameAsync(TestEngines.Locator, webm, 1, 60));

        var text = Path.Combine(dir.Path, "notes.mp4");
        await File.WriteAllTextAsync(text, "not a video");
        Assert.Null(await MediaPreview.FrameAsync(TestEngines.Locator, text, 0, 60));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_thumbnail_strip_delivers_every_frame_once()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 4);
        var got = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();

        await MediaPreview.ThumbnailsAsync(TestEngines.Locator, input, 4, 6, 48, (i, png) => got[i] = png);

        Assert.Equal([0, 1, 2, 3, 4, 5], got.Keys.Order());
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task The_waveform_follows_the_sound()
    {
        using var dir = new TempDir();
        var input = await SilenceToneSilence(Path.Combine(dir.Path, "sts.wav"));   // silent, tone, silent: one second each

        var bars = await MediaPreview.WaveformAsync(TestEngines.Locator, input, 30);

        Assert.Equal(30, bars.Length);
        Assert.All(bars[..9], b => Assert.True(b < 0.01, $"silence bar {b}"));
        Assert.All(bars[11..19], b => Assert.True(b > 0.1, $"tone bar {b}"));
        Assert.All(bars[21..], b => Assert.True(b < 0.01, $"silence bar {b}"));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Loudness_is_read_and_a_silent_file_reads_as_nothing()
    {
        using var dir = new TempDir();
        var tone = await Wav(Path.Combine(dir.Path, "t.wav"), seconds: 4);
        var progress = new List<double>();

        var reading = await MediaPreview.MeasureLoudnessAsync(TestEngines.Locator, tone, 4, new SyncProgress(progress.Add));

        Assert.NotNull(reading);
        Assert.InRange(reading!.IntegratedLufs, -30, -5);
        Assert.InRange(reading.TruePeakDb, -30, 1);   // lavfi sine is 1/8 of full scale
        Assert.Null(await MediaPreview.MeasureLoudnessAsync(TestEngines.Locator, await Silence(Path.Combine(dir.Path, "s.wav"), 2), 2));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Bleep_previews_are_short_aac_files_and_only_the_bleeped_one_beeps_in_silence()
    {
        using var dir = new TempDir();
        var input = await Silence(Path.Combine(dir.Path, "quiet.wav"), 6);
        var scratch = Path.Combine(dir.Path, "scratch");

        var bleeped = await MediaPreview.BleepPreviewAsync(TestEngines.Locator, input, [(3.0, 4.0)], 1000, 0.5, 1, 2, 3, bleeped: true, scratch);
        var original = await MediaPreview.BleepPreviewAsync(TestEngines.Locator, input, [(3.0, 4.0)], 1000, 0.5, 1, 2, 3, bleeped: false, scratch);

        Assert.StartsWith(scratch, bleeped);
        Assert.InRange((await Probe(bleeped)).DurationSeconds!.Value, 2.8, 3.3);
        Assert.Equal("aac", (await Probe(bleeped)).Audio!.CodecName);
        var loud = await MediaPreview.WaveformAsync(TestEngines.Locator, bleeped, 6);
        var quiet = await MediaPreview.WaveformAsync(TestEngines.Locator, original, 6);
        Assert.True(loud.Max() > 0.3, string.Join(",", loud));
        Assert.True(quiet.Max() < 0.01, string.Join(",", quiet));
        Assert.Single(Directory.GetFiles(dir.Path));   // nothing written beside the input
    }

    [FfmpegFact(FfmpegFeature.Encoder, "libvorbis", "libopus")]
    public async Task A_solo_preview_plays_one_side_and_a_playable_copy_is_made_for_ogg()
    {
        using var dir = new TempDir();
        var stereo = Path.Combine(dir.Path, "lr.wav");   // tone on the left, silence on the right
        await RunFfmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=2:sample_rate=44100", "-af", "pan=stereo|c0=c0|c1=0*c0", stereo);
        var scratch = Path.Combine(dir.Path, "scratch");

        var left = await MediaPreview.ChannelsPreviewAsync(TestEngines.Locator, stereo, "left", "stereo", 0, 0, 2, 0, 2, scratch);
        var right = await MediaPreview.ChannelsPreviewAsync(TestEngines.Locator, stereo, "right", "stereo", 0, 0, 2, 0, 2, scratch);
        Assert.True((await MediaPreview.WaveformAsync(TestEngines.Locator, left, 4)).Min() > 0.1);
        Assert.True((await MediaPreview.WaveformAsync(TestEngines.Locator, right, 4)).Max() < 0.01);

        var ogg = Path.Combine(dir.Path, "a.ogg");
        await RunFfmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", FfmpegCapabilities.HasEncoder("libvorbis") ? "libvorbis" : "libopus", ogg);
        var copy = await MediaPreview.PlayableCopyAsync(TestEngines.Locator, ogg, scratch);
        Assert.Equal("aac", (await Probe(copy)).Audio!.CodecName);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_cancelled_waveform_stops_and_throws()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "long.wav"), seconds: 60);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaPreview.WaveformAsync(TestEngines.Locator, input, 100, cts.Token));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) { lock (this) report(value); }
    }
}
