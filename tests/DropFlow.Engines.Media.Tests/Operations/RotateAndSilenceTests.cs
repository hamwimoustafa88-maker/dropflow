using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Tests.Operations;

public class VideoRotateTests
{
    [Fact]
    public async Task Rotates_by_changing_only_the_display_rotation_with_stream_copy()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.rotate", h.Video("v.mp4"), MediaHarness.Opts(("rotate", "90")));

        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        // 90 degrees clockwise is -90 in FFmpeg's counter-clockwise convention, written as 270; it is an INPUT option.
        Assert.True(Args.HasPair(a, "-display_rotation:v:0", "270"));
        Assert.True(Args.IndexOf(a, "-display_rotation:v:0") < Args.IndexOf(a, "-i"));
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.DoesNotContain("libx264", a);
        Assert.DoesNotContain("-vf", a);
        Assert.Equal(Path.Combine(h.Dir, "v (rotated).mp4"), result.Outputs.Single());
    }

    [Theory]
    [InlineData(0, "90", 270)]
    [InlineData(0, "180", 180)]
    [InlineData(0, "270", 90)]
    [InlineData(90, "90", 180)]   // TestMedia rotation 90 = ffprobe -90 (a phone portrait clip): turning it once more makes it upside down
    [InlineData(90, "270", 0)]    // ... and turning it back clears the rotation
    [InlineData(180, "180", 0)]
    public async Task The_new_angle_is_added_to_the_rotation_the_file_already_has(int existing, string clockwise, int expectedCcw)
    {
        using var h = new MediaHarness();
        await h.Run("vid.rotate", h.Video("v.mp4", rotation: existing), MediaHarness.Opts(("rotate", clockwise)));
        Assert.Equal(expectedCcw.ToString(System.Globalization.CultureInfo.InvariantCulture), h.Runner.Calls.Single().After("-display_rotation:v:0"));
    }

    [Theory]
    [InlineData("mp4", true)]
    [InlineData("mov", true)]
    [InlineData("mkv", true)]
    [InlineData("webm", true)]
    [InlineData("m4v", true)]
    [InlineData("avi", false)]   // AVI, WMV and FLV cannot store a rotation, so the wheel never offers it there
    [InlineData("wmv", false)]
    [InlineData("flv", false)]
    [InlineData("mp3", false)]
    public void Only_containers_that_store_a_rotation_are_supported(string ext, bool supported)
    {
        using var h = new MediaHarness();
        Assert.Equal(supported, h.Converters.Any(c => c.Supports("vid.rotate", ext)));
    }

    [Fact]
    public async Task M4v_keeps_its_extension_and_uses_the_mp4_muxer()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.rotate", h.Video("v.m4v"), MediaHarness.Opts(("rotate", "180")));
        Assert.True(result.Success, result.Error);
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-f", "mp4"));
        Assert.EndsWith("v (rotated).m4v", result.Outputs.Single());
    }

    [Fact]
    public async Task Unicode_names_and_collisions_never_overwrite()
    {
        using var h = new MediaHarness();
        var input = h.Video("فيديو العائلة.mp4");
        File.WriteAllText(Path.Combine(h.Dir, "فيديو العائلة (rotated).mp4"), "earlier");

        var result = await h.Run("vid.rotate", input, MediaHarness.Opts(("rotate", "90")));

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "فيديو العائلة (rotated 2).mp4"), result.Outputs.Single());
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(h.Dir, "فيديو العائلة (rotated).mp4")));
    }

    [Fact]
    public async Task A_bad_angle_is_refused_without_running_ffmpeg()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.rotate", h.Video("v.mp4"), MediaHarness.Opts(("rotate", "45")));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task A_file_without_video_is_refused()
    {
        using var h = new MediaHarness();
        var input = h.Input("a.mp4", TestMedia.Audio("aac", path: Path.Combine(h.Dir, "a.mp4")));
        var result = await h.Run("vid.rotate", input, MediaHarness.Opts(("rotate", "90")));
        Assert.False(result.Success);
        Assert.Contains("no video", result.Error);
    }
}

public class TrimSilenceTests
{
    [Fact]
    public async Task Audio_both_ends_use_silenceremove_twice_around_a_reversal()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.trim-silence", h.Audio("song.mp3"));

        Assert.True(result.Success, result.Error);
        var filter = h.Runner.Calls.Single().After("-af");
        Assert.Equal(
            "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.1,areverse," +
            "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.1,areverse", filter);
        Assert.Equal(Path.Combine(h.Dir, "song (trimmed).mp3"), result.Outputs.Single());
    }

    [Theory]
    [InlineData("start", "silenceremove=start_periods=1:start_threshold=-30dB:start_silence=0.1")]
    [InlineData("end", "areverse,silenceremove=start_periods=1:start_threshold=-30dB:start_silence=0.1,areverse")]
    public async Task One_end_only(string ends, string expected)
    {
        using var h = new MediaHarness();
        await h.Run("aud.trim-silence", h.Audio("a.wav", codec: "pcm_s16le"), MediaHarness.Opts(("ends", ends), ("silenceDb", -30)));
        Assert.Equal(expected, h.Runner.Calls.Single().After("-af"));
    }

    [Fact]
    public void Long_audio_is_not_reversed_in_memory()
    {
        // areverse holds the whole decoded stream in memory, so past the limit the silence is measured first and cut with atrim.
        Assert.True(SilenceTrim.UsesReversal(SilenceTrim.MaxReverseSeconds));
        Assert.False(SilenceTrim.UsesReversal(SilenceTrim.MaxReverseSeconds + 1));
        Assert.True(SilenceTrim.UsesReversal(null));
    }

    [Fact]
    public async Task Long_audio_is_measured_then_cut_with_atrim()
    {
        using var h = new MediaHarness();
        var input = h.Audio("talk.mp3", seconds: 3600);
        h.Runner.OnFfmpeg = (call, _, _) => Task.FromResult<ProcessResult?>(call.Args.Contains("-f") && call.After("-f") == "null"
            ? new ProcessResult(0, "", "[silencedetect @ 0x1] silence_start: 0\n[silencedetect @ 0x1] silence_end: 4.5 | silence_duration: 4.5\n[silencedetect @ 0x1] silence_start: 3590\n[silencedetect @ 0x1] silence_end: 3600 | silence_duration: 10\n", TimeSpan.Zero, false, false)
            : null);

        var result = await h.Run("aud.trim-silence", input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.Contains("silencedetect=noise=-45dB", h.Runner.Calls[0].After("-af"));
        Assert.Equal("atrim=start=4.4:end=3590.1,asetpts=PTS-STARTPTS", h.Runner.Calls[1].After("-af"));
    }

    [Fact]
    public async Task Video_is_cut_by_stream_copy_to_the_sound_so_picture_and_sound_stay_in_sync()
    {
        using var h = new MediaHarness();
        var input = h.Video("clip.mp4", seconds: 20);
        h.Runner.OnFfmpeg = (call, _, _) => Task.FromResult<ProcessResult?>(call.After("-f") == "null"
            ? new ProcessResult(0, "", "[silencedetect @ 0x1] silence_start: 0\n[silencedetect @ 0x1] silence_end: 2 | silence_duration: 2\n[silencedetect @ 0x1] silence_start: 17\n", TimeSpan.Zero, false, false)
            : null);

        var result = await h.Run("aud.trim-silence", input);

        Assert.True(result.Success, result.Error);
        var cut = h.Runner.Calls[1].Args;
        Assert.True(Args.HasPair(cut, "-ss", "1.9"));
        Assert.True(Args.HasPair(cut, "-t", "15.2"));
        Assert.True(Args.HasPair(cut, "-c", "copy"));          // video and audio copied, nothing re-encoded
        Assert.DoesNotContain("-af", cut);
        Assert.Equal(Path.Combine(h.Dir, "clip (trimmed).mp4"), result.Outputs.Single());
    }

    [Fact]
    public async Task A_video_with_no_silence_at_the_ends_writes_nothing_and_says_so()
    {
        using var h = new MediaHarness();
        var input = h.Video("clip.mp4", seconds: 20);
        h.Runner.OnFfmpeg = (call, _, _) => Task.FromResult<ProcessResult?>(call.After("-f") == "null"
            ? new ProcessResult(0, "", "[silencedetect @ 0x1] silence_start: 8\n[silencedetect @ 0x1] silence_end: 9 | silence_duration: 1\n", TimeSpan.Zero, false, false)
            : null);

        var result = await h.Run("aud.trim-silence", input);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Outputs);
        Assert.Equal("note.media.noEdgeSilence", result.Note);
        Assert.Single(h.Runner.Calls);
        Assert.Single(h.Tree());
    }

    [Fact]
    public async Task A_completely_silent_file_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var input = h.Video("clip.mp4", seconds: 20);
        h.Runner.OnFfmpeg = (call, _, _) => Task.FromResult<ProcessResult?>(call.After("-f") == "null"
            ? new ProcessResult(0, "", "[silencedetect @ 0x1] silence_start: 0\n[silencedetect @ 0x1] silence_end: 20 | silence_duration: 20\n", TimeSpan.Zero, false, false)
            : null);
        var result = await h.Run("aud.trim-silence", input);
        Assert.False(result.Success);
        Assert.Contains("silent", result.Error);
    }

    [Fact]
    public async Task A_video_without_sound_is_refused()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.trim-silence", h.Video("mute.mp4", audioCodec: null));
        Assert.False(result.Success);
        Assert.Contains("no audio", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Theory]
    [InlineData("mp3", true)]
    [InlineData("wav", true)]
    [InlineData("flac", true)]
    [InlineData("mp4", true)]
    [InlineData("mkv", true)]
    [InlineData("webm", true)]
    [InlineData("png", false)]
    [InlineData("gif", false)]
    public void Supports_audio_files_and_the_sound_of_videos(string ext, bool supported)
    {
        using var h = new MediaHarness();
        Assert.Equal(supported, h.Converters.Any(c => c.Supports("aud.trim-silence", ext)));
    }
}

public class RedactBoxesTests
{
    [Fact]
    public void Compact_text_lists_boxes_with_optional_time_ranges()
    {
        var boxes = RedactBoxes.Parse("100,50,300,200@0:05-0:12; 0,0,160,90\n10,10,20,20@30-; 1,2,3,4@-1:00");
        Assert.Equal(4, boxes.Count);
        Assert.Equal(new RedactBox(new PixelRect(100, 50, 300, 200), 5, 12), boxes[0]);
        Assert.Equal(new RedactBox(new PixelRect(0, 0, 160, 90), null, null), boxes[1]);
        Assert.Equal(new RedactBox(new PixelRect(10, 10, 20, 20), 30, null), boxes[2]);
        Assert.Equal(new RedactBox(new PixelRect(1, 2, 3, 4), null, 60), boxes[3]);
    }

    [Fact]
    public void Json_from_the_visual_editor_is_accepted()
    {
        var boxes = RedactBoxes.Parse("""[{"x":100,"y":50,"w":300,"h":200,"start":5,"end":"0:12.5"},{"x":0,"y":0,"w":160,"h":90}]""");
        Assert.Equal(new RedactBox(new PixelRect(100, 50, 300, 200), 5, 12.5), boxes[0]);
        Assert.Equal(new RedactBox(new PixelRect(0, 0, 160, 90), null, null), boxes[1]);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,0,10")]
    [InlineData("-5,0,10,10")]
    [InlineData("0,0,10,10@5-2")]
    [InlineData("0,0,10,10@x-y")]
    [InlineData("[{\"x\":1}]")]
    [InlineData("[{\"x\":0,\"y\":0,\"w\":10,\"h\":10,\"start\":\"later\"}]")]
    [InlineData("{not json")]
    public void Bad_boxes_are_refused_with_a_reason(string text)
    {
        var ex = Assert.Throws<FormatException>(() => RedactBoxes.Parse(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Too_many_boxes_are_refused()
    {
        var text = string.Join(";", Enumerable.Range(0, RedactBoxes.MaxBoxes + 1).Select(i => $"{i},0,10,10"));
        Assert.Throws<FormatException>(() => RedactBoxes.Parse(text));
    }

    [Fact]
    public async Task Many_boxes_each_get_their_own_time_window()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("boxes", "100,50,320,160@1-2; 0,0,64,64")));

        Assert.True(result.Success, result.Error);
        var vf = h.Runner.Calls.Single().After("-vf")!;
        Assert.StartsWith("split=3[base][c0][c1];", vf);
        Assert.Contains("[c0]crop=320:160:100:50,scale=20:10:flags=area,scale=320:160:flags=neighbor,boxblur=20:2[p0]", vf);
        Assert.Contains("[c1]crop=64:64:0:0,scale=4:4:flags=area,scale=64:64:flags=neighbor,boxblur=16:2[p1]", vf);
        Assert.Contains("[base][p0]overlay=100:50:enable='between(t,1,2)'[o0]", vf);
        Assert.Contains("[o0][p1]overlay=0:0", vf);
        Assert.DoesNotContain("[o1]", vf);                                          // the last overlay is the graph's output
    }

    [Fact]
    public async Task Blur_pixelates_before_blurring_so_text_cannot_be_read_back()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("x", 100), ("y", 50), ("w", 300), ("h", 200)));
        var vf = h.Runner.Calls.Single().After("-vf")!;
        var pixelate = vf.IndexOf("flags=neighbor", StringComparison.Ordinal);
        var blur = vf.IndexOf("boxblur", StringComparison.Ordinal);
        Assert.True(pixelate > 0 && blur > pixelate, vf);
    }

    [Fact]
    public async Task The_single_box_options_still_work_and_add_to_the_list()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("x", 10), ("y", 20), ("w", 30), ("h", 40), ("mode", "solid"), ("boxes", "0,0,100,100@3-4")));
        var vf = h.Runner.Calls.Single().After("-vf")!;
        Assert.Contains("drawbox=x=10:y=20:w=30:h=40:color='black@1':t=fill", vf);
        Assert.Contains("drawbox=x=0:y=0:w=100:h=100:color='black@1':t=fill:enable='between(t,3,4)'", vf);
    }

    [Fact]
    public async Task Output_carries_no_metadata_from_the_original()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("w", 100), ("h", 100)));
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-map_metadata", "-1"));
        Assert.True(Args.HasPair(a, "-map_chapters", "-1"));
    }

    [Fact]
    public async Task A_box_outside_the_picture_is_named_in_the_error()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("boxes", "0,0,10,10; 1900,0,100,100")));
        Assert.False(result.Success);
        Assert.Contains("Box 2", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task A_bad_box_list_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("boxes", "10,10,abc,20")));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }
}
