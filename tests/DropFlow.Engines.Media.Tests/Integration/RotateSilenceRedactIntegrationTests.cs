using DropFlow.TestSupport;
using static DropFlow.Engines.Media.Tests.Integration.FfmpegSamples;

namespace DropFlow.Engines.Media.Tests.Integration;

/// <summary>Real-FFmpeg checks for lossless rotation, trimming silence and multi-box redaction (skipped, visibly, without FFmpeg).</summary>
public class RotateSilenceRedactIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task Rotate_changes_only_the_display_rotation_and_copies_the_streams()
    {
        using var dir = new TempDir();
        foreach (var ext in new[] { "mp4", "mkv", "mov" })
        {
            var input = await Video(Path.Combine(dir.Path, $"تدوير.{ext}"), seconds: 2);
            var before = await Probe(input);

            var once = await Run("vid.rotate", input, Opts(("rotate", "90")));
            Assert.True(once.Success, $"{ext}: {once.Error}");
            var rotated = await Probe(once.Outputs.Single());
            Assert.Equal(270, rotated.Video!.Rotation);                                        // ffprobe -90: shown turned right
            Assert.Equal((before.Video!.Width, before.Video.Height), (rotated.Video.Width, rotated.Video.Height));   // pixels untouched
            Assert.Equal((240, 320), (rotated.Video.DisplayWidth, rotated.Video.DisplayHeight));
            Assert.True(rotated.HasAudio);
            Assert.Equal(before.Video.CodecName, rotated.Video.CodecName);

            // turning a further 270 degrees brings it back upright, which clears the rotation
            var back = await Run("vid.rotate", once.Outputs.Single(), Opts(("rotate", "270")));
            Assert.True(back.Success, back.Error);
            Assert.Equal(0, (await Probe(back.Outputs.Single())).Video!.Rotation);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Rotate_copies_the_video_bit_for_bit()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);
        var output = (await Run("vid.rotate", input, Opts(("rotate", "180")))).Outputs.Single();

        // the compressed video packets are the same: hash them with FFmpeg's own muxer
        async Task<string> PacketHash(string path) => (await RunFfmpeg("-i", path, "-map", "0:v", "-c", "copy", "-f", "hash", "-hash", "md5", "-")).StdOut.Trim();
        Assert.Equal(await PacketHash(input), await PacketHash(output));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Trim_silence_cuts_both_ends_of_audio_and_keeps_a_little_padding()
    {
        using var dir = new TempDir();
        var input = await Compose(Path.Combine(dir.Path, "صمت.wav"), "silence", "tone", "silence", "tone", "silence");

        var both = await Run("aud.trim-silence", input);
        Assert.True(both.Success, both.Error);
        Assert.InRange((await Probe(both.Outputs.Single())).DurationSeconds!.Value, 3.0, 3.3);   // tone, the 1 s gap (kept), tone, ~0.1 s each side

        var start = await Run("aud.trim-silence", input, Opts(("ends", "start")));
        Assert.InRange((await Probe(start.Outputs.Single())).DurationSeconds!.Value, 4.0, 4.2);
        var end = await Run("aud.trim-silence", input, Opts(("ends", "end")));
        Assert.InRange((await Probe(end.Outputs.Single())).DurationSeconds!.Value, 4.0, 4.2);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Trim_silence_of_a_video_cuts_picture_and_sound_together()
    {
        using var dir = new TempDir();
        var sound = await Compose(Path.Combine(dir.Path, "sound.wav"), "silence", "silence", "tone", "tone", "silence", "silence");
        var picture = await Video(Path.Combine(dir.Path, "picture.mp4"), seconds: 6, audio: false);
        var input = Path.Combine(dir.Path, "talk.mp4");
        await RunFfmpeg("-i", picture, "-i", sound, "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest", input);

        var result = await Run("aud.trim-silence", input);

        Assert.True(result.Success, result.Error);
        var info = await Probe(result.Outputs.Single());
        Assert.True(info.HasVideo && info.HasAudio);
        Assert.InRange(info.DurationSeconds!.Value, 1.9, 3.3);                // the 2 s of tone, cut on keyframes (1 per second)
        Assert.InRange(info.Video!.DurationSeconds ?? info.DurationSeconds.Value, 1.9, 3.3);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Redact_many_boxes_pixelates_each_box_and_strips_the_metadata()
    {
        using var dir = new TempDir();
        // A detailed picture everywhere (test pattern plus strong grain), the kind of content a redaction must flatten, with tags to strip.
        var input = Path.Combine(dir.Path, "tagged.mp4");
        await RunFfmpeg("-f", "lavfi", "-i", "testsrc=size=640x360:rate=25:duration=3,noise=alls=90:allf=t+u", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "aac", "-shortest",
            "-metadata", "title=Secret meeting", "-metadata", "location=+33.8938+035.5018/", input);
        Assert.True((await Probe(input)).Tags.ContainsKey("title"));

        var result = await Run("vid.redact", input, Opts(("boxes", """[{"x":0,"y":0,"w":320,"h":180},{"x":320,"y":180,"w":320,"h":180,"start":2}]""")));

        Assert.True(result.Success, result.Error);
        var output = result.Outputs.Single();
        var info = await Probe(output);
        Assert.Equal((640, 360), (info.Video!.Width, info.Video.Height));
        Assert.False(info.Tags.ContainsKey("title"));
        Assert.False(info.Tags.ContainsKey("location"));

        // The first box (always on) has lost its detail: almost no pixel-to-pixel change left compared with the original.
        var original = await Detail(input, 1, "320:180:0:0");
        var hidden = await Detail(output, 1, "320:180:0:0");
        Assert.True(hidden < original * 0.25, $"detail {hidden} vs original {original}");

        // The second box only starts at 2 s: at 1 s that corner is still sharp, at 2.5 s it is hidden.
        var cornerBefore = await Detail(output, 1, "320:180:320:180");
        var cornerAfter = await Detail(output, 2.5, "320:180:320:180");
        Assert.True(cornerAfter < cornerBefore * 0.25, $"corner {cornerAfter} vs {cornerBefore}");
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Target_size_that_is_reachable_never_ends_above_the_target()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "clip.mp4"), seconds: 20);
        var result = await Run("vid.target-size", input, Opts(("sizeMb", 1), ("audioBitrate", 48)));   // 1 MB is the smallest target the tool accepts
        Assert.True(result.Success, result.Error);
        var size = new FileInfo(result.Outputs.Single()).Length;
        Assert.True(size <= 1_000_000, $"{size} bytes");
    }

    /// <summary>Mean absolute difference between horizontally neighbouring grey pixels of a crop at <paramref name="seconds"/>: high for text and edges.</summary>
    private static async Task<double> Detail(string video, double seconds, string crop)
    {
        using var dir = new TempDir();
        var raw = Path.Combine(dir.Path, "frame.gray");
        await RunFfmpeg("-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-vf", $"crop={crop}", "-f", "rawvideo", "-pix_fmt", "gray", raw);
        var parts = crop.Split(':');
        int w = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), h = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var bytes = await File.ReadAllBytesAsync(raw);
        Assert.Equal(w * h, bytes.Length);
        long sum = 0;
        for (var y = 0; y < h; y++)
            for (var x = 1; x < w; x++)
                sum += Math.Abs(bytes[y * w + x] - bytes[y * w + x - 1]);
        return sum / (double)(h * (w - 1));
    }
}
