using TextEncoding = System.Text.Encoding;
using ConcatPlanner = DropFlow.Engines.Media.Ffmpeg.ConcatPlanner;
using ToolFilters = DropFlow.Engines.Media.Ffmpeg.ToolFilters;
using VideoBurnSubsOperation = DropFlow.Engines.Media.Operations.VideoBurnSubsOperation;

namespace DropFlow.Engines.Media.Tests.Operations;

public class VideoCompressTests
{
    [Fact]
    public async Task Defaults_are_crf26_medium_h264_aac128_beside_the_input()
    {
        using var h = new MediaHarness();
        var input = h.Video("holiday.mp4");

        var result = await h.Run("vid.compress", input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "holiday (compressed).mp4"), result.Outputs.Single());
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.True(Args.HasPair(a, "-crf", "26"));
        Assert.True(Args.HasPair(a, "-preset", "medium"));
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.True(Args.HasPair(a, "-b:a", "128k"));
        Assert.Equal(input, Args.After(a, "-i"));
    }

    [Fact]
    public async Task Options_change_codec_quality_height_and_audio()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.compress", h.Video("v.mp4"),
            MediaHarness.Opts(("crf", 30), ("preset", "slow"), ("codec", "h265"), ("maxHeight", "720"), ("audioBitrate", 96)));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-c:v", "libx265"));
        Assert.True(Args.HasPair(a, "-crf", "35"));   // x265 needs +5 for the same look as x264
        Assert.True(Args.HasPair(a, "-preset", "slow"));
        Assert.True(Args.HasPair(a, "-b:a", "96k"));
        Assert.True(Args.HasPair(a, "-tag:v", "hvc1"));
        Assert.Equal("scale=-2:720:flags=lanczos", Args.After(a, "-vf"));
    }

    [Theory]
    [InlineData("clip.avi")]
    [InlineData("clip.wmv")]
    [InlineData("clip.flv")]
    [InlineData("clip.m4v")]
    public async Task Legacy_containers_are_written_as_mp4(string name)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.compress", h.Video(name));
        Assert.True(result.Success, result.Error);
        Assert.EndsWith(" (compressed).mp4", result.Outputs.Single());
    }

    [Fact]
    public async Task Mkv_stays_mkv_and_webm_gets_vp9_with_a_matching_crf()
    {
        using var h = new MediaHarness();
        var mkv = await h.Run("vid.compress", h.Video("a.mkv"));
        Assert.EndsWith("(compressed).mkv", mkv.Outputs.Single());

        var webm = await h.Run("vid.compress", h.Video("b.webm"));
        Assert.EndsWith("(compressed).webm", webm.Outputs.Single());
        var a = h.Runner.Calls.Last().Args;
        Assert.True(Args.HasPair(a, "-c:v", "libvpx-vp9"));
        Assert.True(Args.HasPair(a, "-crf", "34")); // 26 + 8
        Assert.True(Args.HasPair(a, "-c:a", "libopus"));
    }

    [Fact]
    public async Task Without_ffprobe_the_tool_says_so_instead_of_guessing()
    {
        using var h = new MediaHarness();
        h.Runner.FfprobePath = null;
        var result = await h.Run("vid.compress", h.Video("v.mp4"));
        Assert.False(result.Success);
        Assert.Contains("ffprobe", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task A_result_that_is_not_smaller_adds_a_hint_but_still_succeeds()
    {
        using var h = new MediaHarness();
        var log = new ProgressLog();
        var result = await h.Run("vid.compress", h.Video("v.mp4"), progress: log); // input is 11 bytes, fake output 4 bytes: smaller
        Assert.DoesNotContain(log.Reports, r => r.Message?.Contains("not smaller", StringComparison.Ordinal) == true);

        var tiny = h.Input("tiny.mp4", TestMedia.Video(path: Path.Combine(h.Dir, "tiny.mp4")), content: "1");
        var log2 = new ProgressLog();
        var result2 = await h.Run("vid.compress", tiny, progress: log2);
        Assert.True(result2.Success);
        Assert.Contains(log2.Reports, r => r.Message?.Contains("not smaller", StringComparison.Ordinal) == true);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Invalid_choices_are_refused_with_the_allowed_values()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.compress", h.Video("v.mp4"), MediaHarness.Opts(("codec", "divx")));
        Assert.False(result.Success);
        Assert.Contains("h264", result.Error);
    }

    [Fact]
    public async Task An_audio_only_mp4_has_nothing_to_compress()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "podcast.mp4");
        h.Input("podcast.mp4", TestMedia.Audio("aac", path: path));
        var result = await h.Run("vid.compress", path);
        Assert.False(result.Success);
        Assert.Contains("no video", result.Error);
    }

    [Fact]
    public async Task Out_of_range_numbers_are_clamped_to_the_schema()
    {
        using var h = new MediaHarness();
        await h.Run("vid.compress", h.Video("v.mp4"), MediaHarness.Opts(("crf", 99)));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-crf", "38"));
    }
}

public class VideoTargetSizeTests
{
    [Fact]
    public async Task Runs_two_passes_in_a_scratch_folder_that_is_removed_afterwards()
    {
        using var h = new MediaHarness();
        var input = h.Video("big.mp4", seconds: 60);
        h.Runner.WriteOutputSize(19_500_000);   // a result inside the 95-100% band, so no correction pass follows

        var result = await h.Run("vid.target-size", input, MediaHarness.Opts(("sizeMb", 20)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        var p1 = h.Runner.Calls[0];
        var p2 = h.Runner.Calls[1];
        Assert.True(Args.HasPair(p1.Args, "-pass", "1"));
        Assert.True(Args.HasPair(p2.Args, "-pass", "2"));
        Assert.Equal(p1.After("-b:v"), p2.After("-b:v"));
        Assert.NotNull(p1.WorkingDirectory);
        Assert.Equal(p1.WorkingDirectory, p2.WorkingDirectory);
        Assert.NotEqual(h.Dir, p1.WorkingDirectory);
        Assert.True(h.Runner.WorkingDirectories.All(w => w.Existed));
        Assert.False(Directory.Exists(p1.WorkingDirectory));          // scratch folder cleaned up
        Assert.Equal(Path.Combine(h.Dir, "big (compressed).mp4"), result.Outputs.Single());
        Assert.False(File.Exists(Path.Combine(h.Dir, "NUL")));
    }

    [Fact]
    public async Task Steps_the_resolution_down_when_the_size_is_tight()
    {
        using var h = new MediaHarness();
        await h.Run("vid.target-size", h.Video("big.mp4", seconds: 60), MediaHarness.Opts(("sizeMb", 20)));
        Assert.Equal("scale=-2:720:flags=lanczos", Args.After(h.Runner.Calls[1].Args, "-vf"));
    }

    [Fact]
    public async Task An_impossible_target_fails_before_any_encoding()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.target-size", h.Video("long.mp4", seconds: 3600), MediaHarness.Opts(("sizeMb", 1)));
        Assert.False(result.Success);
        Assert.Contains("too small", result.Error);
        Assert.Empty(h.Runner.Calls);
        Assert.Single(h.Tree()); // only the input
    }

    [Fact]
    public async Task H265_option_uses_x265_params_for_the_passes()
    {
        using var h = new MediaHarness();
        await h.Run("vid.target-size", h.Video("v.mp4", seconds: 60), MediaHarness.Opts(("sizeMb", 100), ("codec", "h265")));
        Assert.Contains("pass=1", Args.After(h.Runner.Calls[0].Args, "-x265-params"));
        Assert.Contains("pass=2", Args.After(h.Runner.Calls[1].Args, "-x265-params"));
    }

    [Fact]
    public async Task A_failing_second_pass_removes_the_output_and_the_scratch_folder()
    {
        using var h = new MediaHarness();
        var n = 0;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (++n == 1) return Task.FromResult<ProcessResult?>(null);
            FakeToolRunner.WriteOutput(call.Output);
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Error while encoding\nConversion failed!"));
        };

        var result = await h.Run("vid.target-size", h.Video("v.mp4", seconds: 60), MediaHarness.Opts(("sizeMb", 100)));

        Assert.False(result.Success);
        Assert.False(Directory.Exists(h.Runner.Calls[0].WorkingDirectory));
        Assert.DoesNotContain(h.Tree(), p => p.Contains("compressed", StringComparison.Ordinal));
    }
}

public class VideoMuteMetadataTests
{
    [Fact]
    public async Task Mute_copies_video_and_leaves_the_audio_out()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.mute", h.Video("talk.mkv"));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-map", "0:v"));
        Assert.DoesNotContain("0:a", a);
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.EndsWith("talk (muted).mkv", result.Outputs.Single());
    }

    [Fact]
    public async Task Mute_on_m4v_forces_the_mp4_muxer()
    {
        using var h = new MediaHarness();
        await h.Run("vid.mute", h.Video("itunes.m4v"));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-f", "mp4"));
    }

    [Fact]
    public async Task Metadata_sets_unicode_tags_without_reencoding()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.metadata", h.Video("v.mp4"), MediaHarness.Opts(("title", "عطلة الصيف"), ("artist", "موسى"), ("comment", "نسخة 2")));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-metadata", "title=عطلة الصيف"));
        Assert.True(Args.HasPair(a, "-metadata", "artist=موسى"));
        Assert.True(Args.HasPair(a, "-metadata", "comment=نسخة 2"));
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.True(Args.HasPair(a, "-map_metadata", "0"));
        Assert.EndsWith("v (edited).mp4", result.Outputs.Single());
    }

    [Fact]
    public async Task Empty_values_leave_the_other_tags_alone()
    {
        using var h = new MediaHarness();
        await h.Run("vid.metadata", h.Video("v.mp4"), MediaHarness.Opts(("title", "Only title")));
        var a = h.Runner.Calls.Single().Args;
        Assert.Single(a, x => x == "-metadata");
    }

    [Fact]
    public async Task Strip_removes_everything_and_ignores_the_text_fields()
    {
        using var h = new MediaHarness();
        await h.Run("vid.metadata", h.Video("v.mp4"), MediaHarness.Opts(("strip", true), ("title", "ignored")));
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-map_metadata", "-1"));
        Assert.True(Args.HasPair(a, "-map_chapters", "-1"));
        Assert.DoesNotContain("-metadata", a);
        Assert.Contains("+bitexact", a);
    }

    [Fact]
    public async Task Metadata_with_nothing_to_do_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.metadata", h.Video("v.mp4"));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }
}

public class VideoTrimTests
{
    [Fact]
    public async Task Copy_trim_seeks_before_the_input_and_limits_the_length()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.trim", h.Video("v.mp4", seconds: 60), MediaHarness.Opts(("start", "10"), ("end", "25.5")));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.IndexOf(a, "-ss") < Args.IndexOf(a, "-i"));
        Assert.Equal("10", Args.After(a, "-ss"));
        Assert.Equal("15.5", Args.After(a, "-t"));
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.True(Args.HasPair(a, "-avoid_negative_ts", "make_zero"));
        Assert.EndsWith("v (trimmed).mp4", result.Outputs.Single());
    }

    [Fact]
    public async Task Clock_times_and_arabic_digits_are_accepted()
    {
        using var h = new MediaHarness();
        await h.Run("vid.trim", h.Video("v.mp4", seconds: 600), MediaHarness.Opts(("start", "1:30"), ("end", "٢:٠٠")));
        var a = h.Runner.Calls.Single().Args;
        Assert.Equal("90", Args.After(a, "-ss"));
        Assert.Equal("30", Args.After(a, "-t"));
    }

    [Fact]
    public async Task Open_ended_trim_has_no_t_option()
    {
        using var h = new MediaHarness();
        await h.Run("vid.trim", h.Video("v.mp4"), MediaHarness.Opts(("start", "5")));
        Assert.DoesNotContain("-t", h.Runner.Calls.Single().Args);
    }

    [Theory]
    [InlineData("61", "")]       // start beyond the 60 s file
    [InlineData("20", "10")]     // end before start
    [InlineData("abc", "")]      // not a time
    [InlineData("0", "xyz")]
    public async Task Bad_ranges_fail_without_running_ffmpeg(string start, string end)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.trim", h.Video("v.mp4"), MediaHarness.Opts(("start", start), ("end", end)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
        Assert.Single(h.Tree());
    }

    [Fact]
    public async Task An_end_beyond_the_file_is_clamped_to_the_end()
    {
        using var h = new MediaHarness();
        await h.Run("vid.trim", h.Video("v.mp4", seconds: 60), MediaHarness.Opts(("start", "50"), ("end", "500")));
        Assert.Equal("10", Args.After(h.Runner.Calls.Single().Args, "-t"));
    }

    [Fact]
    public async Task Reencode_gives_an_exact_cut_with_the_h264_encoder()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.trim", h.Video("v.mkv", seconds: 60), MediaHarness.Opts(("start", "10"), ("end", "20"), ("reencode", true)));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.True(Args.HasPair(a, "-crf", "18"));
        Assert.Equal("10", Args.After(a, "-ss"));
        Assert.Equal("10", Args.After(a, "-t"));
        Assert.EndsWith("v (trimmed).mkv", result.Outputs.Single());
    }

    [Fact]
    public async Task Copy_trim_works_without_ffprobe_but_reencode_needs_it()
    {
        using var h = new MediaHarness();
        h.Runner.FfprobePath = null;
        var copy = await h.Run("vid.trim", h.Video("v.mp4"), MediaHarness.Opts(("start", "5")));
        Assert.True(copy.Success, copy.Error);

        var re = await h.Run("vid.trim", h.Video("v2.mp4"), MediaHarness.Opts(("start", "5"), ("reencode", true)));
        Assert.False(re.Success);
        Assert.Contains("ffprobe", re.Error);
    }

    [Fact]
    public async Task M4v_copy_trim_keeps_the_extension_and_uses_the_mp4_muxer()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.trim", h.Video("v.m4v"), MediaHarness.Opts(("start", "5")));
        Assert.EndsWith("v (trimmed).m4v", result.Outputs.Single());
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-f", "mp4"));
    }
}

public class VideoCropTests
{
    [Fact]
    public async Task Square_preset_crops_the_centre_of_a_landscape_frame()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.crop", h.Video("v.mp4"), MediaHarness.Opts(("aspect", "1:1")));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.StartsWith("crop=1080:1080:420:0", Args.After(a, "-vf"));
        Assert.True(Args.HasPair(a, "-c:a", "copy"));
        Assert.EndsWith("v (cropped).mp4", result.Outputs.Single());
    }

    [Fact]
    public async Task Vertical_preset_on_a_landscape_frame()
    {
        using var h = new MediaHarness();
        await h.Run("vid.crop", h.Video("v.mp4"), MediaHarness.Opts(("aspect", "9:16")));
        Assert.StartsWith("crop=608:1080:656:0", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task Rotation_metadata_is_respected_when_choosing_the_area()
    {
        // Stored 1080x1920 rotated by 90 degrees displays as 1920x1080 landscape.
        using var h = new MediaHarness();
        await h.Run("vid.crop", h.Video("v.mp4", width: 1080, height: 1920, rotation: 90), MediaHarness.Opts(("aspect", "1:1")));
        Assert.StartsWith("crop=1080:1080:420:0", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task Custom_area_is_rounded_down_to_even_numbers()
    {
        using var h = new MediaHarness();
        await h.Run("vid.crop", h.Video("v.mp4"), MediaHarness.Opts(("aspect", "custom"), ("x", 101), ("y", 51), ("w", 801), ("h", 601)));
        Assert.StartsWith("crop=800:600:100:50", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "width and height")]
    [InlineData(0, 0, 2000, 500, "does not fit")]
    [InlineData(1000, 0, 1000, 500, "does not fit")]
    public async Task Custom_area_must_fit_inside_the_picture(int x, int y, int w, int hh, string message)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.crop", h.Video("v.mp4"), MediaHarness.Opts(("aspect", "custom"), ("x", x), ("y", y), ("w", w), ("h", hh)));
        Assert.False(result.Success);
        Assert.Contains(message, result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public void Aspect_math_never_exceeds_the_frame_and_is_always_even()
    {
        foreach (var (fw, fh) in new[] { (1920, 1080), (1080, 1920), (1280, 720), (1000, 1000), (3840, 2160), (1919, 1079), (640, 480) })
            foreach (var (aw, ah) in new[] { (1, 1), (4, 5), (3, 4), (4, 3), (16, 9), (9, 16), (21, 9) })
            {
                var r = ToolFilters.CenteredAspectCrop(fw, fh, aw, ah);
                Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= fw && r.Y + r.Height <= fh, $"{fw}x{fh} -> {aw}:{ah} = {r}");
                Assert.True(r.Width % 2 == 0 && r.Height % 2 == 0 && r.X % 2 == 0 && r.Y % 2 == 0, $"{r}");
                Assert.True(Math.Abs(r.Width / (double)r.Height - aw / (double)ah) < 0.02 * aw / ah + 2.0 / Math.Min(r.Width, r.Height), $"{fw}x{fh} -> {aw}:{ah} = {r}");
            }
    }
}

public class VideoSpeedTests
{
    [Fact]
    public async Task Double_speed_rescales_timestamps_and_stretches_audio_without_changing_pitch()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.speed", h.Video("v.mp4"), MediaHarness.Opts(("speed", 2)));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.StartsWith("setpts=PTS/2", Args.After(a, "-vf"));
        Assert.Equal("atempo=2", Args.After(a, "-af"));
        Assert.EndsWith("v (speed 2x).mp4", result.Outputs.Single());
    }

    [Theory]
    [InlineData(4, "atempo=2,atempo=2")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(0.5, "atempo=0.5")]
    [InlineData(3, "atempo=2,atempo=1.5")]
    [InlineData(1.5, "atempo=1.5")]
    [InlineData(0.75, "atempo=0.75")]
    public async Task Audio_tempo_is_chained_within_the_range_each_stage_accepts(double speed, string expected)
    {
        using var h = new MediaHarness();
        await h.Run("vid.speed", h.Video("v.mp4"), MediaHarness.Opts(("speed", speed)));
        Assert.Equal(expected, Args.After(h.Runner.Calls.Single().Args, "-af"));
    }

    [Fact]
    public void Tempo_chain_math_multiplies_back_to_the_requested_speed()
    {
        foreach (var speed in new[] { 0.25, 0.3, 0.5, 0.6, 0.99, 1.01, 1.5, 2, 2.5, 3, 3.9, 4 })
        {
            var chain = ToolFilters.AtempoChain(speed)!;
            var factors = chain.Split(',').Select(s => double.Parse(s["atempo=".Length..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.All(factors, f => Assert.InRange(f, 0.5, 2.0));
            Assert.Equal(speed, factors.Aggregate(1.0, (x, y) => x * y), 4);
        }
        Assert.Null(ToolFilters.AtempoChain(1));
    }

    [Fact]
    public async Task Half_speed_names_the_file_and_uses_setpts_with_a_decimal()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.speed", h.Video("v.mp4"), MediaHarness.Opts(("speed", 0.5)));
        Assert.EndsWith("v (speed 0.5x).mp4", result.Outputs.Single());
        Assert.StartsWith("setpts=PTS/0.5", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task One_x_is_refused_and_out_of_range_values_are_clamped()
    {
        using var h = new MediaHarness();
        var same = await h.Run("vid.speed", h.Video("v.mp4"), MediaHarness.Opts(("speed", 1)));
        Assert.False(same.Success);
        Assert.Empty(h.Runner.Calls);

        await h.Run("vid.speed", h.Video("w.mp4"), MediaHarness.Opts(("speed", 10)));
        Assert.StartsWith("setpts=PTS/4", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task Speed_of_a_silent_video_has_no_audio_filter()
    {
        using var h = new MediaHarness();
        await h.Run("vid.speed", h.Video("v.mp4", audioCodec: null), MediaHarness.Opts(("speed", 2)));
        Assert.DoesNotContain("-af", h.Runner.Calls.Single().Args);
    }

    [Fact]
    public async Task Progress_denominator_is_the_shortened_output_length()
    {
        using var h = new MediaHarness();
        var log = new ProgressLog();
        // The fake's first block reports 0.5 s of output time; a 60 s source at 2x produces 30 s, so progress reads 0.5/30.
        await h.Run("vid.speed", h.Video("v.mp4", seconds: 60), MediaHarness.Opts(("speed", 2)), log);
        Assert.Contains(log.Reports, r => Math.Abs(r.Fraction - 0.5 / 30) < 1e-6);
    }
}

public class VideoSnapshotTests
{
    [Fact]
    public async Task One_png_per_requested_time_into_a_new_folder()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.snapshot", h.Video("trip.mp4", seconds: 60), MediaHarness.Opts(("times", "5, 0:30, 999")));
        Assert.True(result.Success, result.Error);

        var folder = Path.Combine(h.Dir, "trip (frames)");
        Assert.Equal([folder], result.Outputs);
        Assert.Equal(2, h.Runner.Calls.Count); // 999 s is past the end and is skipped
        Assert.Equal("5", h.Runner.Calls[0].After("-ss"));
        Assert.Equal("30", h.Runner.Calls[1].After("-ss"));
        Assert.Equal(Path.Combine(folder, "trip 00-00-05.000.png"), h.Runner.Calls[0].Output);
        Assert.Equal(Path.Combine(folder, "trip 00-00-30.000.png"), h.Runner.Calls[1].Output);
        Assert.True(Args.HasPair(h.Runner.Calls[0].Args, "-frames:v", "1"));
        Assert.True(File.Exists(h.Runner.Calls[0].Output));
    }

    [Fact]
    public async Task Default_is_the_first_frame()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.snapshot", h.Video("v.mp4"));
        Assert.True(result.Success, result.Error);
        Assert.Equal("0", h.Runner.Calls.Single().After("-ss"));
    }

    [Fact]
    public async Task Every_n_seconds_uses_one_fps_filter_run_and_escapes_percent_signs_in_the_name()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.snapshot", h.Video("100% real.mp4", seconds: 60), MediaHarness.Opts(("times", ""), ("every", 10)));
        Assert.True(result.Success, result.Error);
        var call = h.Runner.Calls.Single();
        Assert.Equal("fps=1/10", call.After("-vf"));
        Assert.EndsWith("100%% real frame %05d.png", call.Output);
        Assert.Single(Directory.GetFiles(Path.Combine(h.Dir, "100% real (frames)")));
    }

    [Fact]
    public async Task Fractional_intervals_become_decimal_rates()
    {
        using var h = new MediaHarness();
        await h.Run("vid.snapshot", h.Video("v.mp4"), MediaHarness.Opts(("times", ""), ("every", 2.5)));
        Assert.Equal("fps=0.4", h.Runner.Calls.Single().After("-vf"));
    }

    [Fact]
    public async Task Times_past_the_end_fail_and_leave_no_folder()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.snapshot", h.Video("v.mp4", seconds: 10), MediaHarness.Opts(("times", "50, 60")));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
        Assert.Single(h.Tree());
    }

    [Fact]
    public async Task A_bad_time_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.snapshot", h.Video("v.mp4"), MediaHarness.Opts(("times", "5, soon")));
        Assert.False(result.Success);
        Assert.Contains("soon", result.Error);
    }

    [Fact]
    public async Task A_failure_part_way_removes_the_whole_folder()
    {
        using var h = new MediaHarness();
        var n = 0;
        h.Runner.OnFfmpeg = (_, _, _) => Task.FromResult<ProcessResult?>(++n == 2 ? FakeToolRunner.Fail("Invalid data found when processing input") : null);
        var result = await h.Run("vid.snapshot", h.Video("v.mp4", seconds: 60), MediaHarness.Opts(("times", "1,2,3")));
        Assert.False(result.Success);
        Assert.Single(h.Tree());
    }
}

public class VideoSplitTests
{
    [Fact]
    public async Task Copy_split_uses_the_segment_muxer_with_the_given_points()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "2:00, 60")));
        Assert.True(result.Success, result.Error);

        var folder = Path.Combine(h.Dir, "film (clips)");
        Assert.Equal([folder], result.Outputs);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-f", "segment"));
        Assert.True(Args.HasPair(a, "-segment_times", "60,120"));  // sorted
        Assert.True(Args.HasPair(a, "-reset_timestamps", "1"));
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.Equal(Path.Combine(folder, "film part %03d.mp4"), a[^1]);
        Assert.True(Args.HasPair(a, "-segment_format_options", "movflags=+faststart"));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Fixed_length_clips()
    {
        using var h = new MediaHarness();
        await h.Run("vid.split", h.Video("film.mkv", seconds: 300), MediaHarness.Opts(("segment", 100)));
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-segment_time", "100"));
        Assert.DoesNotContain("-segment_times", a);
        Assert.DoesNotContain("-segment_format_options", a);
    }

    [Fact]
    public async Task Reencode_forces_keyframes_at_the_split_points_and_uses_the_planner_codec_args()
    {
        using var h = new MediaHarness();
        await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "60,120"), ("reencode", true)));
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-force_key_frames", "60,120"));
        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.DoesNotContain("-movflags", a); // the segment muxer cannot take it directly
        Assert.True(Args.IndexOf(a, "-c:v") < Args.IndexOf(a, "-f"));
        Assert.EndsWith("film part %03d.mp4", a[^1]);
        Assert.DoesNotContain(EncoderPlan.OutputPlaceholder, a);
    }

    [Fact]
    public async Task Percent_signs_in_the_name_and_folder_are_escaped_in_the_pattern()
    {
        using var h = new MediaHarness();
        await h.Run("vid.split", h.Video("50% off.mp4", seconds: 300), MediaHarness.Opts(("points", "60")));
        var pattern = h.Runner.Calls.Single().Output;
        Assert.EndsWith("50%% off part %03d.mp4", pattern);
        Assert.Equal(5, pattern.Count(c => c == '%')); // doubled in the folder name, doubled in the file name, plus the single %03d
    }

    [Theory]
    [InlineData("", 0)]          // nothing given
    [InlineData("400", 0)]       // past the end of a 300 s file
    [InlineData("0", 0)]         // zero is not a split point
    [InlineData("10, nope", 0)]  // unparsable
    [InlineData("", 500)]        // clip longer than the video
    public async Task Bad_input_fails_without_running_or_leaving_a_folder(string points, int segment)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", points), ("segment", segment)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
        Assert.Single(h.Tree());
    }

    [Fact]
    public async Task A_failed_split_removes_the_clips_folder()
    {
        using var h = new MediaHarness();
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            FakeToolRunner.WriteOutput(call.Output);
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("No space left on device"));
        };
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "60")));
        Assert.False(result.Success);
        Assert.Contains("disk is full", result.Error);
        Assert.Single(h.Tree());
    }

    [Fact]
    public async Task Existing_clip_folders_are_never_reused()
    {
        using var h = new MediaHarness();
        Directory.CreateDirectory(Path.Combine(h.Dir, "film (clips)"));
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "60")));
        Assert.Equal(Path.Combine(h.Dir, "film (clips 2)"), result.Outputs.Single());
    }

    /// <summary>Makes the fake segment muxer write one file per given start time, plus the CSV list FFmpeg writes with <c>-segment_list</c>.</summary>
    private static void FakeSegments(MediaHarness h, params double[] starts)
    {
        h.Runner.OnFfmpeg = (call, options, _) =>
        {
            if (call.After("-f") != "segment") return Task.FromResult<ProcessResult?>(null);
            var pattern = call.Output;
            var csv = new System.Text.StringBuilder();
            for (var i = 0; i < starts.Length; i++)
            {
                var file = pattern.Replace("%03d", (i + 1).ToString("000"), StringComparison.Ordinal);
                File.WriteAllText(file, $"clip {i + 1}");
                var end = i + 1 < starts.Length ? starts[i + 1] : 300;
                csv.Append(Path.GetFileName(file)).Append(',').Append(starts[i].ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture))
                   .Append(',').Append(end.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(call.After("-segment_list")!, csv.ToString());
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", "", TimeSpan.Zero, false, false));
        };
    }

    [Fact]
    public async Task Removed_clips_are_left_out_and_the_others_keep_their_planned_numbers()
    {
        using var h = new MediaHarness();
        FakeSegments(h, 0, 61.2, 120.4);   // stream copy starts each clip on the next keyframe
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "60,120"), ("remove", "2")));

        Assert.True(result.Success, result.Error);
        var folder = Path.Combine(h.Dir, "film (clips)");
        Assert.Equal([folder], result.Outputs);
        Assert.Equal(["film part 001.mp4", "film part 003.mp4"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal("clip 3", File.ReadAllText(Path.Combine(folder, "film part 003.mp4")));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-segment_list_type", "csv"));
        Assert.Equal(["film (clips)", Path.Combine("film (clips)", "film part 001.mp4"), Path.Combine("film (clips)", "film part 003.mp4"), "film.mp4"], h.Tree());
    }

    [Fact]
    public async Task Join_the_rest_concatenates_the_kept_clips_by_stream_copy_into_one_file()
    {
        using var h = new MediaHarness();
        FakeSegments(h, 0, 60, 120);
        string? list = null;
        var segmenter = h.Runner.OnFfmpeg!;
        h.Runner.OnFfmpeg = async (call, options, ct) =>
        {
            if (call.After("-f") == "concat") list = File.ReadAllText(call.After("-i")!);
            return await segmenter(call, options, ct);
        };

        var result = await h.Run("vid.split", h.Video("film.mkv", seconds: 300), MediaHarness.Opts(("points", "60,120"), ("remove", "2"), ("output", "join")));

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "film (edited).mkv"), result.Outputs.Single());
        Assert.Equal(2, h.Runner.Calls.Count);
        var join = h.Runner.Calls[1].Args;
        Assert.True(Args.HasPair(join, "-c", "copy"));
        Assert.DoesNotContain("-movflags", join); // mkv
        Assert.Contains("clip001.mkv", list);
        Assert.DoesNotContain("clip002.mkv", list);
        Assert.Contains("clip003.mkv", list);
        Assert.True(list!.IndexOf("clip001", StringComparison.Ordinal) < list.IndexOf("clip003", StringComparison.Ordinal));
        Assert.Equal(["film (edited).mkv", "film.mkv"], h.Tree());   // the scratch clips are gone
    }

    [Fact]
    public async Task Equal_length_clips_can_be_removed_too()
    {
        using var h = new MediaHarness();
        FakeSegments(h, 0, 100, 200);
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("segment", 100), ("remove", "1, 3")));
        Assert.True(result.Success, result.Error);
        Assert.Equal(["film part 002.mp4"], Directory.GetFiles(result.Outputs.Single()).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("", "join")]       // nothing to leave out, so "join the rest" would rebuild the original
    [InlineData("1,2,3", "clips")] // every clip removed
    [InlineData("4", "clips")]     // there are only three clips
    [InlineData("0", "clips")]     // clips are numbered from 1
    [InlineData("two", "clips")]   // not a number
    public async Task Impossible_removals_fail_before_anything_runs(string remove, string output)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "60,120"), ("remove", remove), ("output", output)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
        Assert.Single(h.Tree());
    }

    [Fact]
    public async Task Clip_numbers_typed_with_arabic_digits_are_understood()
    {
        using var h = new MediaHarness();
        FakeSegments(h, 0, 60, 120);
        var result = await h.Run("vid.split", h.Video("film.mp4", seconds: 300), MediaHarness.Opts(("points", "60,120"), ("remove", "٢")));
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, Directory.GetFiles(result.Outputs.Single()).Length);
    }
}

public class VideoJoinTests
{
    [Fact]
    public async Task Identical_clips_are_joined_by_copying_streams_with_a_concat_list()
    {
        using var h = new MediaHarness();
        var a = h.Video("part1.mp4", seconds: 20);
        var b = h.Video("part2.mp4", seconds: 30);
        string? listText = null;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            listText = File.ReadAllText(call.After("-i")!);
            return Task.FromResult<ProcessResult?>(null);
        };

        var result = await h.RunMulti("vid.join", [a, b]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "part1 (joined).mp4"), result.Outputs.Single());
        var call = h.Runner.Calls.Single();
        Assert.True(Args.HasPair(call.Args, "-f", "concat"));
        Assert.True(Args.HasPair(call.Args, "-safe", "0"));
        Assert.True(Args.HasPair(call.Args, "-c", "copy"));
        Assert.Equal($"ffconcat version 1.0\nfile '{a.Replace('\\', '/')}'\nfile '{b.Replace('\\', '/')}'\n", listText);
        Assert.False(File.Exists(call.After("-i")!)); // list file is scratch
    }

    [Fact]
    public async Task Different_sizes_are_scaled_and_padded_then_concatenated_with_the_filter()
    {
        using var h = new MediaHarness();
        var a = h.Video("a.mp4", seconds: 10, width: 1920, height: 1080);
        var b = h.Video("b.mp4", seconds: 10, width: 1280, height: 720);

        var result = await h.RunMulti("vid.join", [a, b]);

        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        var graph = Args.After(args, "-filter_complex")!;
        Assert.Contains("[0:v:0]scale=1920:1080:force_original_aspect_ratio=decrease", graph);
        Assert.Contains("[1:v:0]scale=1920:1080", graph);
        Assert.Contains("pad=1920:1080:(ow-iw)/2:(oh-ih)/2", graph);
        Assert.Contains("fps=30", graph);
        Assert.Contains("[1:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a1]", graph);
        Assert.EndsWith("concat=n=2:v=1:a=1[v][a]", graph);
        Assert.True(Args.HasPair(args, "-map", "[v]"));
        Assert.True(Args.HasPair(args, "-map", "[a]"));
        Assert.True(Args.HasPair(args, "-c:v", "libx264"));
        Assert.Equal(2, args.Count(x => x == "-i"));
    }

    [Fact]
    public async Task A_clip_without_sound_gets_silence_of_the_same_length()
    {
        using var h = new MediaHarness();
        var a = h.Video("a.mp4", seconds: 10);
        var b = h.Video("b.mp4", seconds: 7.5, audioCodec: null);
        await h.RunMulti("vid.join", [a, b]);
        var graph = Args.After(h.Runner.Calls.Single().Args, "-filter_complex")!;
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=7.5,asetpts=PTS-STARTPTS[a1]", graph);
    }

    [Fact]
    public async Task All_silent_clips_produce_a_video_only_result()
    {
        using var h = new MediaHarness();
        var a = h.Video("a.mp4", audioCodec: null, width: 1280, height: 720);
        var b = h.Video("b.mp4", audioCodec: null, width: 1920, height: 1080);
        await h.RunMulti("vid.join", [a, b]);
        var args = h.Runner.Calls.Single().Args;
        Assert.EndsWith("concat=n=2:v=1:a=0[v]", Args.After(args, "-filter_complex")!);
        Assert.Contains("-an", args);
        Assert.DoesNotContain("[a]", args);
    }

    [Fact]
    public async Task If_the_copy_join_fails_in_auto_mode_it_re_encodes_after_removing_the_partial_file()
    {
        using var h = new MediaHarness();
        var a = h.Video("a.mp4");
        var b = h.Video("b.mp4");
        var n = 0;
        bool partialGoneBeforeRetry = false;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (++n == 1)
            {
                FakeToolRunner.WriteOutput(call.Output);
                return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Non-monotonous DTS in output stream\nInvalid argument"));
            }
            partialGoneBeforeRetry = !File.Exists(call.Output);
            return Task.FromResult<ProcessResult?>(null);
        };

        var result = await h.RunMulti("vid.join", [a, b]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.True(partialGoneBeforeRetry);
        Assert.True(h.Runner.Calls[1].Has("-filter_complex"));
    }

    [Fact]
    public async Task Forcing_copy_mode_reports_the_failure_instead_of_re_encoding()
    {
        using var h = new MediaHarness();
        h.Runner.OnFfmpeg = (_, _, _) => Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Invalid argument"));
        var result = await h.RunMulti("vid.join", [h.Video("a.mp4"), h.Video("b.mp4")], MediaHarness.Opts(("mode", "copy")));
        Assert.False(result.Success);
        Assert.Single(h.Runner.Calls);
    }

    [Fact]
    public async Task Reencode_mode_skips_the_copy_attempt()
    {
        using var h = new MediaHarness();
        await h.RunMulti("vid.join", [h.Video("a.mp4"), h.Video("b.mp4")], MediaHarness.Opts(("mode", "reencode")));
        Assert.True(h.Runner.Calls.Single().Has("-filter_complex"));
    }

    [Fact]
    public async Task Paths_with_apostrophes_are_escaped_in_the_concat_list()
    {
        using var h = new MediaHarness();
        var a = h.Video("it's a clip.mp4");
        var b = h.Video("part 2.mp4");
        string? listText = null;
        h.Runner.OnFfmpeg = (call, _, _) => { listText = File.ReadAllText(call.After("-i")!); return Task.FromResult<ProcessResult?>(null); };
        await h.RunMulti("vid.join", [a, b]);
        Assert.Contains("it'\\''s a clip.mp4'", listText);
    }

    [Fact]
    public async Task One_file_is_not_a_join()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.join", h.Video("a.mp4"));
        Assert.False(result.Success);
        Assert.Contains("two", result.Error);
    }

    [Fact]
    public async Task A_clip_without_video_is_named_in_the_error()
    {
        using var h = new MediaHarness();
        var a = h.Video("a.mp4");
        var path = Path.Combine(h.Dir, "voice.mp4");
        h.Input("voice.mp4", TestMedia.Audio("aac", path: path));
        var result = await h.RunMulti("vid.join", [a, path]);
        Assert.False(result.Success);
        Assert.Contains("voice.mp4", result.Error);
    }

    [Fact]
    public void Stream_copy_planning_requires_matching_streams()
    {
        MediaInfo V(int w = 1920, int h = 1080, double fps = 30, string codec = "h264", string? audio = "aac", int rate = 48000) =>
            TestMedia.Video(w, h, fps, 10, codec, audio, sampleRate: rate);

        Assert.True(ConcatPlanner.CanStreamCopyIntoMp4([V(), V()]));
        Assert.True(ConcatPlanner.CanStreamCopyIntoMp4([V(fps: 30), V(fps: 30.1)]));  // within 1%
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(), V(w: 1280, h: 720)]));
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(), V(fps: 25)]));
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(), V(codec: "hevc")]));
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(), V(audio: null)]));
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(), V(rate: 44100)]));
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(codec: "vp9"), V(codec: "vp9")]));          // not in the MP4 list
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V(audio: "pcm_s16le"), V(audio: "pcm_s16le")])); // PCM does not fit MP4
        Assert.False(ConcatPlanner.CanStreamCopyIntoMp4([V()]));
    }
}

public class VideoRedactTests
{
    [Fact]
    public async Task Blur_box_for_the_whole_video()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("x", 100), ("y", 50), ("w", 300), ("h", 200)));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.StartsWith("split[orig][part];[part]crop=300:200:100:50,boxblur=20:2[blurred];[orig][blurred]overlay=100:50,", Args.After(a, "-vf"));
        Assert.True(Args.HasPair(a, "-c:a", "copy"));
        Assert.EndsWith("v (redacted).mp4", result.Outputs.Single());
    }

    [Fact]
    public async Task Blur_for_a_time_range_adds_an_enable_expression()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("x", 0), ("y", 0), ("w", 100), ("h", 100), ("start", "1"), ("end", "2.5")));
        Assert.Contains("overlay=0:0:enable='between(t,1,2.5)',", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task Open_ended_range_uses_gte()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("w", 100), ("h", 100), ("start", "10")));
        Assert.Contains("enable='gte(t,10)'", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task Solid_box_with_a_hex_colour()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("x", 10), ("y", 20), ("w", 30), ("h", 40), ("mode", "solid"), ("color", "#FF0000")));
        Assert.StartsWith("drawbox=x=10:y=20:w=30:h=40:color='0xFF0000@1':t=fill,", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task The_blur_radius_is_limited_by_the_box_size()
    {
        using var h = new MediaHarness();
        await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("w", 16), ("h", 16), ("strength", 60)));
        Assert.Contains("boxblur=4:2", Args.After(h.Runner.Calls.Single().Args, "-vf")); // 16 / 4
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "", "", "width and height")]
    [InlineData(1900, 0, 100, 100, "", "", "does not fit")]
    [InlineData(0, 0, 100, 100, "5", "2", "end time")]
    public async Task Invalid_boxes_and_ranges_are_refused(int x, int y, int w, int hh, string start, string end, string message)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("x", x), ("y", y), ("w", w), ("h", hh), ("start", start), ("end", end)));
        Assert.False(result.Success);
        Assert.Contains(message, result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Theory]
    [InlineData("not a colour!")]
    [InlineData("red;drawbox")]
    [InlineData("#12345")]
    public async Task Hostile_colour_text_cannot_reach_the_filter_graph(string color)
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.redact", h.Video("v.mp4"), MediaHarness.Opts(("w", 100), ("h", 100), ("mode", "solid"), ("color", color)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }
}

public class VideoBurnSubsTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:03,000\nمرحبا بالعالم\n\n2\n00:00:04,000 --> 00:00:05,000\nHello\n";

    [Fact]
    public async Task A_sidecar_file_is_found_converted_to_a_clean_scratch_copy_and_referenced_by_a_relative_name()
    {
        using var h = new MediaHarness();
        var input = h.Video("episode.mp4");
        await File.WriteAllTextAsync(Path.Combine(h.Dir, "episode.srt"), Srt, new System.Text.UTF8Encoding(true));

        string? scratchCopy = null;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            scratchCopy = File.ReadAllText(Path.Combine(call.WorkingDirectory!, "subs.srt"));
            return Task.FromResult<ProcessResult?>(null);
        };

        var result = await h.Run("vid.burn-subs", input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "episode (subtitled).mp4"), result.Outputs.Single());
        var call = h.Runner.Calls.Single();
        var vf = Args.After(call.Args, "-vf")!;
        Assert.StartsWith("subtitles=filename='subs.srt'", vf);
        Assert.Contains("force_style='FontName\\=Arial,FontSize\\=18,", vf);
        Assert.Contains("مرحبا بالعالم", scratchCopy);
        Assert.StartsWith("1\n00:00:01,000 --> 00:00:03,000", scratchCopy);   // normalised: no BOM, LF
        Assert.True(Args.HasPair(call.Args, "-c:a", "copy"));
        Assert.Equal(input, Args.After(call.Args, "-i"));
        Assert.False(Directory.Exists(call.WorkingDirectory));               // scratch folder removed
    }

    [Fact]
    public async Task The_original_subtitle_path_never_reaches_the_filter_graph()
    {
        using var h = new MediaHarness();
        var input = h.Video("episode.mp4");
        var nasty = Path.Combine(h.Dir, "it's [1], v=2; مترجم.srt");
        await File.WriteAllBytesAsync(nasty, [.. TextEncoding.Unicode.GetPreamble(), .. TextEncoding.Unicode.GetBytes(Srt)]); // UTF-16 with BOM

        string? scratchCopy = null;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            scratchCopy = File.ReadAllText(Path.Combine(call.WorkingDirectory!, "subs.srt"));
            return Task.FromResult<ProcessResult?>(null);
        };
        var result = await h.Run("vid.burn-subs", input, MediaHarness.Opts(("subtitle", nasty)));

        Assert.True(result.Success, result.Error);
        var vf = Args.After(h.Runner.Calls.Single().Args, "-vf")!;
        Assert.DoesNotContain("مترجم", vf);
        Assert.DoesNotContain("it's", vf);
        Assert.Contains("مرحبا بالعالم", scratchCopy);
    }

    [Fact]
    public async Task The_fonts_folder_is_passed_with_a_properly_escaped_drive_colon()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!Directory.Exists(fonts)) return; // nothing to assert on a machine without a fonts folder
        using var h = new MediaHarness();
        var input = h.Video("episode.mp4");
        await File.WriteAllTextAsync(Path.Combine(h.Dir, "episode.srt"), Srt);
        await h.Run("vid.burn-subs", input);
        var vf = Args.After(h.Runner.Calls.Single().Args, "-vf")!;
        Assert.Contains("fontsdir='" + fonts.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal) + "'", vf);
    }

    [Fact]
    public async Task Ass_files_keep_their_own_styling_and_get_no_style_override()
    {
        using var h = new MediaHarness();
        var input = h.Video("episode.mp4");
        var ass = Path.Combine(h.Dir, "styled.ass");
        await File.WriteAllTextAsync(ass, "[Script Info]\nTitle: x\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hi\n");

        string? file = null;
        h.Runner.OnFfmpeg = (call, _, _) => { file = Directory.GetFiles(call.WorkingDirectory!).Single(); return Task.FromResult<ProcessResult?>(null); };
        var result = await h.Run("vid.burn-subs", input, MediaHarness.Opts(("subtitle", ass)));

        Assert.True(result.Success, result.Error);
        Assert.EndsWith("subs.ass", file);
        var vf = Args.After(h.Runner.Calls.Single().Args, "-vf")!;
        Assert.StartsWith("subtitles=filename='subs.ass'", vf);
        Assert.DoesNotContain("force_style", vf);
    }

    [Fact]
    public async Task Font_size_and_position_options_reach_the_style()
    {
        using var h = new MediaHarness();
        var input = h.Video("episode.mp4");
        await File.WriteAllTextAsync(Path.Combine(h.Dir, "episode.srt"), Srt);
        await h.Run("vid.burn-subs", input, MediaHarness.Opts(("fontSize", 30), ("position", "top")));
        var vf = Args.After(h.Runner.Calls.Single().Args, "-vf")!;
        Assert.Contains("FontSize\\=30", vf);
        Assert.Contains("Alignment\\=8", vf);
    }

    [Fact]
    public void Sidecar_discovery_prefers_the_exact_name_and_srt_then_language_variants()
    {
        using var h = new MediaHarness();
        var video = h.Video("show.mp4");
        File.WriteAllText(Path.Combine(h.Dir, "show.ar.srt"), Srt);
        File.WriteAllText(Path.Combine(h.Dir, "show.vtt"), "WEBVTT");
        File.WriteAllText(Path.Combine(h.Dir, "show.txt"), "nope");
        File.WriteAllText(Path.Combine(h.Dir, "other.srt"), Srt);

        Assert.Equal(Path.Combine(h.Dir, "show.vtt"), VideoBurnSubsOperation.FindSidecar(video));
        File.WriteAllText(Path.Combine(h.Dir, "show.srt"), Srt);
        Assert.Equal(Path.Combine(h.Dir, "show.srt"), VideoBurnSubsOperation.FindSidecar(video));
        File.Delete(Path.Combine(h.Dir, "show.srt"));
        File.Delete(Path.Combine(h.Dir, "show.vtt"));
        Assert.Equal(Path.Combine(h.Dir, "show.ar.srt"), VideoBurnSubsOperation.FindSidecar(video));
    }

    [Fact]
    public async Task No_subtitle_given_and_none_beside_the_video_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.burn-subs", h.Video("episode.mp4"));
        Assert.False(result.Success);
        Assert.Contains("subtitle", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Runner.Calls);
    }

    [Theory]
    [InlineData("notes.txt", "hello")]
    [InlineData("empty.srt", "")]
    [InlineData("garbage.srt", "this is not a subtitle file")]
    public async Task Unusable_subtitle_files_fail_before_ffmpeg_and_leave_nothing_behind(string name, string content)
    {
        using var h = new MediaHarness();
        var input = h.Video("episode.mp4");
        var sub = Path.Combine(h.Dir, name);
        await File.WriteAllTextAsync(sub, content);
        var result = await h.Run("vid.burn-subs", input, MediaHarness.Opts(("subtitle", sub)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
        Assert.Equal(2, h.Tree().Count); // video + subtitle only
    }

    [Fact]
    public async Task A_missing_subtitle_file_is_reported()
    {
        using var h = new MediaHarness();
        var result = await h.Run("vid.burn-subs", h.Video("episode.mp4"), MediaHarness.Opts(("subtitle", Path.Combine(h.Dir, "gone.srt"))));
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }
}
