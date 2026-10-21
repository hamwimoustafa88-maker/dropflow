using AudioToolArgs = DropFlow.Engines.Media.Ffmpeg.AudioToolArgs;

namespace DropFlow.Engines.Media.Tests.Operations;

public class AudioCompressTests
{
    [Fact]
    public async Task Lossless_inputs_become_m4a_at_128k_by_default()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.compress", h.Audio("take.wav", "pcm_s16le"));
        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "take (compressed).m4a"), result.Outputs.Single());
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.True(Args.HasPair(a, "-b:a", "128k"));
    }

    [Fact]
    public async Task Lossy_inputs_stay_in_their_format()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.compress", h.Audio("song.mp3"));
        Assert.EndsWith("song (compressed).mp3", result.Outputs.Single());
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-c:a", "libmp3lame"));
        Assert.True(Args.HasPair(a, "-b:a", "128k"));
        Assert.DoesNotContain("-q:a", a);
    }

    [Fact]
    public async Task Bitrate_and_format_options()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.compress", h.Audio("song.flac", "flac"), MediaHarness.Opts(("bitrate", "64"), ("format", "opus")));
        Assert.EndsWith("song (compressed).opus", result.Outputs.Single());
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-c:a", "libopus"));
        Assert.True(Args.HasPair(a, "-b:a", "64k"));
    }

    [Fact]
    public async Task Integer_bitrates_from_a_ui_are_accepted_as_choices()
    {
        using var h = new MediaHarness();
        await h.Run("aud.compress", h.Audio("song.mp3"), MediaHarness.Opts(("bitrate", 96)));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-b:a", "96k"));
    }

    [Fact]
    public async Task A_bitrate_that_is_not_offered_is_refused()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.compress", h.Audio("song.mp3"), MediaHarness.Opts(("bitrate", "9999")));
        Assert.False(result.Success);
        Assert.Contains("bitrate", result.Error);
    }

    [Fact]
    public async Task Cover_art_survives_when_the_format_allows_it()
    {
        using var h = new MediaHarness();
        await h.Run("aud.compress", h.Audio("album.mp3", cover: true));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-disposition:v:0", "attached_pic"));
    }
}

public class AudioMetadataTests
{
    [Fact]
    public async Task Tags_are_set_by_stream_copy()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.metadata", h.Audio("song.mp3"),
            MediaHarness.Opts(("title", "أغنية"), ("artist", "فنان"), ("album", "ألبوم"), ("comment", "ملاحظة")));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-metadata", "title=أغنية"));
        Assert.True(Args.HasPair(a, "-metadata", "artist=فنان"));
        Assert.True(Args.HasPair(a, "-metadata", "album=ألبوم"));
        Assert.True(Args.HasPair(a, "-metadata", "comment=ملاحظة"));
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.True(Args.HasPair(a, "-id3v2_version", "3"));
        Assert.EndsWith("song (edited).mp3", result.Outputs.Single());
    }

    [Fact]
    public async Task Cover_art_is_kept_unless_stripping()
    {
        using var h = new MediaHarness();
        await h.Run("aud.metadata", h.Audio("a.mp3", cover: true), MediaHarness.Opts(("title", "t")));
        Assert.True(Args.HasPair(h.Runner.Calls.Last().Args, "-map", "0:1"));

        await h.Run("aud.metadata", h.Audio("b.mp3", cover: true), MediaHarness.Opts(("strip", true)));
        var strip = h.Runner.Calls.Last().Args;
        Assert.DoesNotContain("0:1", strip);
        Assert.True(Args.HasPair(strip, "-map_metadata", "-1"));
        Assert.DoesNotContain("-metadata", strip);
    }

    [Fact]
    public async Task Cover_art_that_cannot_be_copied_is_dropped_on_retry()
    {
        using var h = new MediaHarness();
        var n = 0;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (++n == 1)
            {
                FakeToolRunner.WriteOutput(call.Output);
                return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Could not write header for output file #0"));
            }
            return Task.FromResult<ProcessResult?>(null);
        };
        var result = await h.Run("aud.metadata", h.Audio("a.m4a", "aac", cover: true), MediaHarness.Opts(("title", "t")));
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.DoesNotContain("0:1", h.Runner.Calls[1].Args);
    }

    [Fact]
    public async Task Nothing_to_change_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.metadata", h.Audio("song.mp3"));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }
}

public class AudioNormalizeTests
{
    private const string Measured = """
        [Parsed_loudnorm_0 @ 0000023c3c2c1b40]
        {
        	"input_i" : "-23.54",
        	"input_tp" : "-6.18",
        	"input_lra" : "5.80",
        	"input_thresh" : "-34.17",
        	"output_i" : "-16.66",
        	"output_tp" : "-1.51",
        	"output_lra" : "4.40",
        	"output_thresh" : "-26.78",
        	"normalization_type" : "dynamic",
        	"target_offset" : "0.66"
        }
        """;

    private static void AnswerPassOne(MediaHarness h, string stderr)
    {
        h.Runner.OnFfmpeg = (call, options, _) =>
        {
            if (call.After("-f") != "null") return Task.FromResult<ProcessResult?>(null);
            options.OnOutputLine?.Invoke("out_time_us=1000000");
            options.OnOutputLine?.Invoke("progress=end");
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", stderr, TimeSpan.Zero, false, false));
        };
    }

    [Fact]
    public async Task Measures_first_then_applies_exactly_the_measured_values()
    {
        using var h = new MediaHarness();
        AnswerPassOne(h, Measured);

        var result = await h.Run("aud.normalize", h.Audio("talk.mp3"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);

        var p1 = h.Runner.Calls[0].Args;
        Assert.Equal("loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", Args.After(p1, "-af"));
        Assert.Equal(FfmpegNull, p1[^1]);
        Assert.DoesNotContain("-n", p1); // the null output must not trigger the overwrite guard

        var p2 = h.Runner.Calls[1].Args;
        Assert.Equal(
            "loudnorm=I=-16:TP=-1.5:LRA=11:measured_I=-23.54:measured_TP=-6.18:measured_LRA=5.80:measured_thresh=-34.17:offset=0.66:linear=true:print_format=summary",
            Args.After(p2, "-af"));
        // loudnorm outputs 192 kHz. The rate goes back to the source's with the -ar output option: an aresample filter after loudnorm
        // fails to negotiate a channel layout on files whose layout FFmpeg had to guess (seen with FFmpeg 4.0 and a plain WAV).
        Assert.Equal("44100", Args.After(p2, "-ar"));
        Assert.DoesNotContain("aresample", Args.After(p2, "-af"));
        Assert.True(Args.HasPair(p2, "-q:a", "0"));        // high-quality VBR to avoid generation loss
        Assert.EndsWith("talk (normalized).mp3", result.Outputs.Single());
    }

    private static string FfmpegNull => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    [Fact]
    public async Task Targets_come_from_the_options()
    {
        using var h = new MediaHarness();
        AnswerPassOne(h, Measured);
        await h.Run("aud.normalize", h.Audio("talk.flac", "flac", sampleRate: 48000), MediaHarness.Opts(("target", -23), ("truePeak", -2), ("lra", 7)));
        Assert.Equal("loudnorm=I=-23:TP=-2:LRA=7:print_format=json", Args.After(h.Runner.Calls[0].Args, "-af"));
        Assert.Equal("48000", Args.After(h.Runner.Calls[1].Args, "-ar"));
    }

    [Fact]
    public async Task Silent_audio_cannot_be_normalized()
    {
        using var h = new MediaHarness();
        AnswerPassOne(h, Measured.Replace("\"-23.54\"", "\"-inf\"", StringComparison.Ordinal));
        var result = await h.Run("aud.normalize", h.Audio("quiet.mp3"));
        Assert.False(result.Success);
        Assert.Contains("silent", result.Error);
        Assert.Single(h.Runner.Calls);                 // no second pass
        Assert.DoesNotContain(h.Tree(), p => p.Contains("normalized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_measurements_are_reported()
    {
        using var h = new MediaHarness();
        AnswerPassOne(h, "size=N/A time=00:00:30.00");
        var result = await h.Run("aud.normalize", h.Audio("a.mp3"));
        Assert.False(result.Success);
        Assert.Contains("could not be measured", result.Error);
    }

    [Fact]
    public async Task A_failing_measurement_pass_reports_ffmpegs_reason()
    {
        using var h = new MediaHarness();
        h.Runner.OnFfmpeg = (_, _, _) => Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Invalid data found when processing input"));
        var result = await h.Run("aud.normalize", h.Audio("a.mp3"));
        Assert.False(result.Success);
        Assert.Contains("corrupt", result.Error);
    }

    [Fact]
    public async Task Both_passes_report_progress_in_their_own_half()
    {
        using var h = new MediaHarness();
        AnswerPassOne(h, Measured);
        var log = new ProgressLog();
        await h.Run("aud.normalize", h.Audio("a.mp3"), progress: log);
        var fractions = log.Reports.Select(r => r.Fraction).ToList();
        Assert.Equal(fractions.OrderBy(f => f), fractions);
        Assert.Contains(fractions, f => f is > 0.44 and < 0.46);   // end of pass 1
        Assert.Equal(1, fractions[^1]);
    }
}

public class AudioTrimTests
{
    private const string SilenceLog = """
        [silencedetect @ 0000020a5b3f1a40] silence_start: -0.00133333
        [silencedetect @ 0000020a5b3f1a40] silence_end: 2.51188 | silence_duration: 2.51322
        [silencedetect @ 0000020a5b3f1a40] silence_start: 30.2
        [silencedetect @ 0000020a5b3f1a40] silence_end: 31.4 | silence_duration: 1.2
        [silencedetect @ 0000020a5b3f1a40] silence_start: 57.3
        """;

    private static void AnswerScan(MediaHarness h, string stderr)
    {
        h.Runner.OnFfmpeg = (call, _, _) =>
            Task.FromResult<ProcessResult?>(call.After("-f") == "null"
                ? new ProcessResult(0, "", stderr, TimeSpan.Zero, false, false)
                : null);
    }

    [Fact]
    public async Task Manual_range_is_a_stream_copy()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.trim", h.Audio("song.mp3", seconds: 60), MediaHarness.Opts(("start", "5"), ("end", "10")));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.Equal("5", Args.After(a, "-ss"));
        Assert.Equal("5", Args.After(a, "-t"));
        Assert.True(Args.HasPair(a, "-c", "copy"));
        Assert.EndsWith("song (trimmed).mp3", result.Outputs.Single());
    }

    [Fact]
    public async Task Auto_silence_cuts_only_the_two_ends()
    {
        using var h = new MediaHarness();
        AnswerScan(h, SilenceLog);

        var result = await h.Run("aud.trim", h.Audio("talk.mp3", seconds: 60), MediaHarness.Opts(("autoSilence", true)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.Equal("silencedetect=noise=-50dB:d=0.5", Args.After(h.Runner.Calls[0].Args, "-af"));
        var cut = h.Runner.Calls[1].Args;
        Assert.Equal("2.412", Args.After(cut, "-ss"));    // 2.51188 minus 0.1 s of kept silence
        Assert.Equal("54.988", Args.After(cut, "-t"));    // up to 57.3 + 0.1, the middle silence at 30.2 is untouched
        Assert.True(Args.HasPair(cut, "-c", "copy"));
    }

    [Fact]
    public async Task Auto_silence_uses_the_threshold_options()
    {
        using var h = new MediaHarness();
        AnswerScan(h, "");
        await h.Run("aud.trim", h.Audio("talk.mp3", seconds: 60), MediaHarness.Opts(("autoSilence", true), ("silenceDb", -40), ("minSilence", 1.5)));
        Assert.Equal("silencedetect=noise=-40dB:d=1.5", Args.After(h.Runner.Calls[0].Args, "-af"));
    }

    [Fact]
    public async Task Auto_silence_combines_with_a_manual_end()
    {
        using var h = new MediaHarness();
        AnswerScan(h, SilenceLog);
        await h.Run("aud.trim", h.Audio("talk.mp3", seconds: 60), MediaHarness.Opts(("autoSilence", true), ("end", "50")));
        var cut = h.Runner.Calls[1].Args;
        Assert.Equal("2.412", Args.After(cut, "-ss"));
        Assert.Equal("47.588", Args.After(cut, "-t")); // 50 - 2.41188
    }

    [Fact]
    public async Task A_file_with_no_edge_silence_is_copied_whole()
    {
        using var h = new MediaHarness();
        AnswerScan(h, "[silencedetect @ 0x1] silence_start: 20\n[silencedetect @ 0x1] silence_end: 21 | silence_duration: 1");
        var result = await h.Run("aud.trim", h.Audio("talk.mp3", seconds: 60), MediaHarness.Opts(("autoSilence", true)));
        Assert.True(result.Success, result.Error);
        var cut = h.Runner.Calls[1].Args;
        Assert.DoesNotContain("-ss", cut);
        Assert.DoesNotContain("-t", cut);
    }

    [Fact]
    public async Task A_completely_silent_file_has_nothing_to_keep()
    {
        using var h = new MediaHarness();
        AnswerScan(h, "[silencedetect @ 0x1] silence_start: 0");
        var result = await h.Run("aud.trim", h.Audio("silence.mp3", seconds: 30), MediaHarness.Opts(("autoSilence", true)));
        Assert.False(result.Success);
        Assert.Contains("silent", result.Error);
        Assert.Single(h.Runner.Calls);
    }

    [Theory]
    [InlineData("0", "", false)]    // nothing requested
    [InlineData("40", "", false)]   // start beyond a 30 s file
    [InlineData("10", "5", false)]  // end before start
    [InlineData("abc", "", false)]
    public async Task Bad_manual_ranges_fail_without_running_ffmpeg(string start, string end, bool _)
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.trim", h.Audio("song.mp3", seconds: 30), MediaHarness.Opts(("start", start), ("end", end)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Auto_silence_needs_ffprobe_for_the_length()
    {
        using var h = new MediaHarness();
        h.Runner.FfprobePath = null;
        var result = await h.Run("aud.trim", h.Audio("talk.mp3"), MediaHarness.Opts(("autoSilence", true)));
        Assert.False(result.Success);
        Assert.Contains("ffprobe", result.Error);
    }

    [Fact]
    public async Task Cover_art_is_kept_in_a_copy_trim_for_formats_that_hold_it()
    {
        using var h = new MediaHarness();
        await h.Run("aud.trim", h.Audio("album.mp3", cover: true), MediaHarness.Opts(("start", "2")));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-map", "0:1"));
    }
}

public class AudioChannelsTests
{
    [Fact]
    public async Task Stereo_to_mono_averages_the_channels()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.channels", h.Audio("song.mp3"), MediaHarness.Opts(("channels", "mono")));
        Assert.True(result.Success, result.Error);
        Assert.Equal("pan=mono|c0=0.5*c0+0.5*c1", Args.After(h.Runner.Calls.Single().Args, "-af"));
        Assert.EndsWith("song (mono).mp3", result.Outputs.Single());
    }

    [Fact]
    public async Task Mono_to_stereo_duplicates_at_full_level()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.channels", h.Audio("voice.wav", "pcm_s16le", channels: 1), MediaHarness.Opts(("channels", "stereo")));
        Assert.True(result.Success, result.Error);
        Assert.Equal("pan=stereo|c0=c0|c1=c0", Args.After(h.Runner.Calls.Single().Args, "-af"));
        Assert.EndsWith("voice (stereo).wav", result.Outputs.Single());
    }

    [Fact]
    public async Task Surround_is_downmixed_with_the_standard_matrix()
    {
        using var h = new MediaHarness();
        await h.Run("aud.channels", h.Audio("film.flac", "flac", channels: 6), MediaHarness.Opts(("channels", "stereo")));
        Assert.Equal("aformat=channel_layouts=stereo", Args.After(h.Runner.Calls.Single().Args, "-af"));
    }

    [Fact]
    public async Task A_file_that_already_has_the_layout_is_refused()
    {
        using var h = new MediaHarness();
        var mono = await h.Run("aud.channels", h.Audio("a.mp3", channels: 1), MediaHarness.Opts(("channels", "mono")));
        Assert.False(mono.Success);
        Assert.Contains("already mono", mono.Error);

        var stereo = await h.Run("aud.channels", h.Audio("b.mp3", channels: 2), MediaHarness.Opts(("channels", "stereo")));
        Assert.False(stereo.Success);
        Assert.Contains("already stereo", stereo.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Default_target_is_mono()
    {
        using var h = new MediaHarness();
        await h.Run("aud.channels", h.Audio("song.mp3"));
        Assert.Contains("pan=mono", Args.After(h.Runner.Calls.Single().Args, "-af"));
    }

    [Fact]
    public async Task Mp3_re_encode_uses_the_high_quality_vbr_setting()
    {
        using var h = new MediaHarness();
        await h.Run("aud.channels", h.Audio("song.mp3"));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-q:a", "0"));
    }
}

public class AudioVisualizeTests
{
    [Theory]
    [InlineData("landscape", "1920x1080")]
    [InlineData("portrait", "1080x1920")]
    [InlineData("square", "1080x1080")]
    public async Task Layouts_set_the_frame_size(string layout, string size)
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.visualize", h.Audio("song.mp3"), MediaHarness.Opts(("layout", layout)));
        Assert.True(result.Success, result.Error);
        var a = h.Runner.Calls.Single().Args;
        Assert.Equal($"[0:a:0]showwaves=s={size}:mode=cline:rate=25:colors='orange':scale=sqrt,format=yuv420p[v]", Args.After(a, "-filter_complex"));
        Assert.True(Args.HasPair(a, "-map", "[v]"));
        Assert.True(Args.HasPair(a, "-map", "0:a:0"));
        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.Contains("-shortest", a);
        Assert.EndsWith("song (visualized).mp4", result.Outputs.Single());
    }

    [Fact]
    public async Task A_picture_background_is_looped_scaled_to_fill_and_overlaid()
    {
        using var h = new MediaHarness();
        var bg = h.Input("cover art.png");
        await h.Run("aud.visualize", h.Audio("song.mp3"), MediaHarness.Opts(("layout", "portrait"), ("background", bg), ("color", "#FF6A00")));
        var a = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(a, "-loop", "1"));
        Assert.Equal(bg, a[Args.IndexOf(a, "-i") + 1]);
        Assert.Equal(2, a.Count(x => x == "-i"));
        var graph = Args.After(a, "-filter_complex")!;
        Assert.Contains("[0:v]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920", graph);
        Assert.Contains("[1:a:0]showwaves=s=1080x1920", graph);
        Assert.Contains("colors='0xFF6A00'", graph);
        Assert.Contains("overlay=format=auto", graph);
        Assert.True(Args.HasPair(a, "-map", "1:a:0"));
    }

    [Theory]
    [InlineData("not a colour")]
    [InlineData("red'];[x")]
    public async Task Hostile_colours_are_refused(string color)
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.visualize", h.Audio("song.mp3"), MediaHarness.Opts(("color", color)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Missing_or_non_picture_backgrounds_are_refused()
    {
        using var h = new MediaHarness();
        var missing = await h.Run("aud.visualize", h.Audio("a.mp3"), MediaHarness.Opts(("background", Path.Combine(h.Dir, "nope.png"))));
        Assert.False(missing.Success);
        Assert.Contains("not found", missing.Error);

        var notPicture = await h.Run("aud.visualize", h.Audio("b.mp3"), MediaHarness.Opts(("background", h.Input("notes.txt"))));
        Assert.False(notPicture.Success);
        Assert.Contains("picture", notPicture.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Missing_h264_encoder_is_named()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersMinimal;
        var result = await h.Run("aud.visualize", h.Audio("song.mp3"));
        Assert.False(result.Success);
        Assert.Contains("libx264", result.Error);
    }
}

public class AudioBleepTests
{
    [Fact]
    public async Task Ranges_are_replaced_by_a_sine_inside_one_sample_accurate_expression_per_channel()
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.bleep", h.Audio("interview.mp3"), MediaHarness.Opts(("ranges", "1.5-2.3; 10-11")));
        Assert.True(result.Success, result.Error);
        var af = Args.After(h.Runner.Calls.Single().Args, "-af")!;
        const string inRange = "gt(between(t,1.5,2.3)+between(t,10,11),0)";
        Assert.Equal(
            $"aeval=exprs='val(0)*(1-{inRange})+0.35*sin(2*PI*1000*t)*{inRange}|val(1)*(1-{inRange})+0.35*sin(2*PI*1000*t)*{inRange}':c=same",
            af);
        Assert.EndsWith("interview (bleeped).mp3", result.Outputs.Single());
    }

    [Fact]
    public async Task Mono_files_get_a_single_expression()
    {
        using var h = new MediaHarness();
        await h.Run("aud.bleep", h.Audio("voice.wav", "pcm_s16le", channels: 1), MediaHarness.Opts(("ranges", "1-2")));
        var af = Args.After(h.Runner.Calls.Single().Args, "-af")!;
        Assert.DoesNotContain("|", af);
        Assert.DoesNotContain("val(1)", af);
    }

    [Fact]
    public async Task Surround_files_are_folded_to_stereo_first()
    {
        using var h = new MediaHarness();
        await h.Run("aud.bleep", h.Audio("film.flac", "flac", channels: 6), MediaHarness.Opts(("ranges", "1-2")));
        Assert.StartsWith("aformat=channel_layouts=stereo,aeval=", Args.After(h.Runner.Calls.Single().Args, "-af"));
    }

    [Fact]
    public async Task Frequency_and_volume_options_and_json_ranges()
    {
        using var h = new MediaHarness();
        await h.Run("aud.bleep", h.Audio("a.mp3"), MediaHarness.Opts(("ranges", "[[0.5, 1], [0.9, 2]]"), ("frequency", 800), ("volume", 0.5)));
        var af = Args.After(h.Runner.Calls.Single().Args, "-af")!;
        Assert.Contains("0.5*sin(2*PI*800*t)", af);
        Assert.Contains("gt(between(t,0.5,2),0)", af);   // overlapping ranges merged
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("5-3")]
    [InlineData("100-200")]   // past the end of a 30 s file
    public async Task Bad_or_useless_ranges_fail_without_running(string ranges)
    {
        using var h = new MediaHarness();
        var result = await h.Run("aud.bleep", h.Audio("a.mp3", seconds: 30), MediaHarness.Opts(("ranges", ranges)));
        Assert.False(result.Success);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Bleeping_needs_ffprobe_for_the_channel_count()
    {
        using var h = new MediaHarness();
        h.Runner.FfprobePath = null;
        var result = await h.Run("aud.bleep", h.Audio("a.mp3"), MediaHarness.Opts(("ranges", "1-2")));
        Assert.False(result.Success);
        Assert.Contains("ffprobe", result.Error);
    }
}

public class AudioMergeTests
{
    [Fact]
    public async Task Identical_files_are_joined_by_copying()
    {
        using var h = new MediaHarness();
        var a = h.Audio("one.mp3");
        var b = h.Audio("two.mp3");
        string? list = null;
        h.Runner.OnFfmpeg = (call, _, _) => { list = File.ReadAllText(call.After("-i")!); return Task.FromResult<ProcessResult?>(null); };

        var result = await h.RunMulti("aud.merge", [a, b]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "one (merged).mp3"), result.Outputs.Single());
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-f", "concat"));
        Assert.True(Args.HasPair(args, "-c", "copy"));
        Assert.Equal($"ffconcat version 1.0\nfile '{a.Replace('\\', '/')}'\nfile '{b.Replace('\\', '/')}'\n", list);
    }

    [Fact]
    public async Task Different_formats_are_resampled_normalised_and_re_encoded_to_m4a()
    {
        using var h = new MediaHarness();
        var a = h.Audio("one.mp3", sampleRate: 44100);
        var b = h.Audio("two.wav", "pcm_s16le", sampleRate: 48000, channels: 1);

        var result = await h.RunMulti("aud.merge", [a, b]);

        Assert.True(result.Success, result.Error);
        Assert.EndsWith("one (merged).m4a", result.Outputs.Single());
        var args = h.Runner.Calls.Single().Args;
        var graph = Args.After(args, "-filter_complex")!;
        Assert.Contains("[0:a:0]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo[a0]", graph);
        Assert.Contains("[1:a:0]pan=stereo|c0=c0|c1=c0,aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo[a1]", graph); // mono is duplicated, not attenuated
        Assert.EndsWith("concat=n=2:v=0:a=1[out]", graph);
        Assert.True(Args.HasPair(args, "-map", "[out]"));
        Assert.True(Args.HasPair(args, "-c:a", "aac"));
        Assert.True(Args.HasPair(args, "-b:a", "256k"));
    }

    [Fact]
    public async Task An_explicit_format_always_re_encodes()
    {
        using var h = new MediaHarness();
        var result = await h.RunMulti("aud.merge", [h.Audio("a.mp3"), h.Audio("b.mp3")], MediaHarness.Opts(("format", "flac")));
        Assert.EndsWith("a (merged).flac", result.Outputs.Single());
        var args = h.Runner.Calls.Single().Args;
        Assert.Contains("-filter_complex", args);
        Assert.True(Args.HasPair(args, "-c:a", "flac"));
    }

    [Fact]
    public async Task A_failed_copy_falls_back_to_re_encoding()
    {
        using var h = new MediaHarness();
        var n = 0;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (++n > 1) return Task.FromResult<ProcessResult?>(null);
            FakeToolRunner.WriteOutput(call.Output);
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Invalid argument"));
        };
        var result = await h.RunMulti("aud.merge", [h.Audio("a.mp3"), h.Audio("b.mp3")]);
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.True(h.Runner.Calls[1].Has("-filter_complex"));
    }

    [Fact]
    public async Task Non_audio_inputs_and_single_files_are_refused()
    {
        using var h = new MediaHarness();
        var notAudio = await h.RunMulti("aud.merge", [h.Audio("a.mp3"), h.Input("b.png")]);
        Assert.False(notAudio.Success);
        Assert.Contains("b.png", notAudio.Error);

        var single = await h.Run("aud.merge", h.Audio("c.mp3"));
        Assert.False(single.Success);
        Assert.Contains("two", single.Error);
    }

    [Fact]
    public void Merge_extension_follows_the_inputs()
    {
        Assert.Equal("mp3", AudioToolArgs.MergeExtension(["mp3", "mp3"], "auto"));
        Assert.Equal("m4a", AudioToolArgs.MergeExtension(["mp3", "wav"], "auto"));
        Assert.Equal("flac", AudioToolArgs.MergeExtension(["mp3", "mp3"], "flac"));
    }
}
