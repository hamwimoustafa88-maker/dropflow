using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Tests.Ffmpeg;

public class ProgressParserTests
{
    /// <summary>What <c>ffmpeg -progress pipe:1</c> really prints, one block per second plus a final one.</summary>
    private const string Stream = """
        frame=0
        fps=0.00
        stream_0_0_q=0.0
        bitrate=N/A
        total_size=48
        out_time_us=N/A
        out_time_ms=N/A
        out_time=N/A
        dup_frames=0
        drop_frames=0
        speed=N/A
        progress=continue
        frame=240
        fps=118.40
        stream_0_0_q=28.0
        stream_1_0_q=-1.0
        bitrate= 1837.2kbits/s
        total_size=2297234
        out_time_us=10003000
        out_time_ms=10003000
        out_time=00:00:10.003000
        dup_frames=0
        drop_frames=0
        speed=4.93x
        progress=continue
        frame=1800
        fps=121.00
        bitrate=1790.4kbits/s
        total_size=13447718
        out_time_us=60060000
        out_time_ms=60060000
        out_time=00:01:00.060000
        speed=5.1x
        progress=end
        """;

    private static List<FfmpegProgress> ParseAll(string text)
    {
        var parser = new FfmpegProgressParser();
        return text.Split('\n').Select(l => parser.Feed(l.TrimEnd('\r'))).OfType<FfmpegProgress>().ToList();
    }

    [Fact]
    public void Emits_one_snapshot_per_progress_line()
    {
        var all = ParseAll(Stream);
        Assert.Equal(3, all.Count);
        Assert.False(all[0].IsEnd);
        Assert.False(all[1].IsEnd);
        Assert.True(all[2].IsEnd);
    }

    [Fact]
    public void Early_blocks_with_na_values_do_not_crash_and_report_zero()
    {
        var first = ParseAll(Stream)[0];
        Assert.Equal(0, first.OutTimeUs);
        Assert.Null(first.Speed);
        Assert.Null(first.BitrateKbps);
        Assert.Equal(0, first.Frame);
        Assert.Equal(0, first.Fraction(60));
    }

    [Fact]
    public void Running_block_has_time_speed_bitrate_and_size()
    {
        var p = ParseAll(Stream)[1];
        Assert.Equal(10_003_000, p.OutTimeUs);
        Assert.Equal(10.003, p.OutSeconds, 6);
        Assert.Equal(4.93, p.Speed!.Value, 6);
        Assert.Equal(1837.2, p.BitrateKbps!.Value, 6);
        Assert.Equal(240, p.Frame);
        Assert.Equal(118.4, p.Fps!.Value, 6);
        Assert.Equal(2_297_234, p.TotalSizeBytes);
    }

    [Fact]
    public void Fraction_is_clamped_and_never_one_until_the_end()
    {
        var all = ParseAll(Stream);
        Assert.Equal(10.003 / 60.06, all[1].Fraction(60.06), 6);
        Assert.True(new FfmpegProgress(70_000_000, null, null, null, null, null, false).Fraction(60) <= 0.999);
        Assert.Equal(1, all[2].Fraction(60.06));
        Assert.Equal(0, all[1].Fraction(null));
        Assert.Equal(0, all[1].Fraction(0));
    }

    [Fact]
    public void Unknown_lines_and_negative_placeholders_are_ignored()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("garbage without equals"));
        Assert.Null(parser.Feed("=novalue"));
        Assert.Null(parser.Feed("out_time_us=-9223372036854775807"));
        var snap = parser.Feed("progress=continue");
        Assert.NotNull(snap);
        Assert.Equal(0, snap!.OutTimeUs);
    }

    [Fact]
    public void Only_the_legacy_out_time_ms_key_still_works_and_is_microseconds()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_ms=5000000");
        Assert.Equal(5_000_000, parser.Feed("progress=continue")!.OutTimeUs);
    }

    [Fact]
    public void A_block_without_a_time_keeps_the_last_known_time_so_progress_never_goes_backwards()
    {
        // FFmpeg 9 prints out_time=N/A in some blocks mid-run; reporting 0 there made the progress bar jump back.
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_us=5000000");
        parser.Feed("progress=continue");

        parser.Feed("out_time_us=N/A");
        Assert.Equal(5_000_000, parser.Feed("progress=continue")!.OutTimeUs);
        Assert.Equal(5_000_000, parser.Feed("progress=continue")!.OutTimeUs);

        parser.Feed("out_time_us=7000000");
        Assert.Equal(7_000_000, parser.Feed("progress=continue")!.OutTimeUs);
    }

    [Fact]
    public void A_smaller_time_never_replaces_a_larger_one()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_us=5000000");
        parser.Feed("progress=continue");
        parser.Feed("out_time_us=1000000");
        Assert.Equal(5_000_000, parser.Feed("progress=continue")!.OutTimeUs);
    }

    [Fact]
    public void Crlf_lines_work()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("out_time_us=1000000"));
        Assert.Equal(1_000_000, parser.Feed("progress=continue")!.OutTimeUs);
    }
}

public class SilenceDetectTests
{
    // Real silencedetect stderr (interleaved with the usual log noise).
    private const string Log = """
        Input #0, mp3, from 'talk.mp3':
          Duration: 00:01:00.00, start: 0.025057, bitrate: 128 kb/s
        [silencedetect @ 0000020a5b3f1a40] silence_start: -0.00133333
        [silencedetect @ 0000020a5b3f1a40] silence_end: 2.51188 | silence_duration: 2.51322
        [silencedetect @ 0000020a5b3f1a40] silence_start: 30.2
        [silencedetect @ 0000020a5b3f1a40] silence_end: 31.4 | silence_duration: 1.2
        [silencedetect @ 0000020a5b3f1a40] silence_start: 57.3
        size=N/A time=00:01:00.00 bitrate=N/A speed= 150x
        """;

    [Fact]
    public void Parses_intervals_and_clamps_the_tiny_negative_start()
    {
        var intervals = SilenceDetect.Parse(Log);
        Assert.Equal(3, intervals.Count);
        Assert.Equal(new SilenceInterval(0, 2.51188), intervals[0]);
        Assert.Equal(new SilenceInterval(30.2, 31.4), intervals[1]);
        Assert.Equal(new SilenceInterval(57.3, null), intervals[2]); // runs to the end of the file
    }

    [Fact]
    public void Newer_ffmpeg_closes_the_trailing_silence_at_the_duration()
    {
        var log = Log + "\n[silencedetect @ 0x1] silence_end: 60 | silence_duration: 2.7\n";
        var intervals = SilenceDetect.Parse(log.Replace("size=N/A", "", StringComparison.Ordinal));
        Assert.Equal(new SilenceInterval(57.3, 60), intervals[^1]);
    }

    [Fact]
    public void No_silence_gives_an_empty_list()
    {
        Assert.Empty(SilenceDetect.Parse("Duration: 00:00:10.00\nsize=N/A time=00:00:10.00"));
        Assert.Empty(SilenceDetect.Parse(""));
    }

    [Fact]
    public void Scientific_notation_is_read()
    {
        var intervals = SilenceDetect.Parse("[silencedetect @ 0x1] silence_start: 1.5e-05\n[silencedetect @ 0x1] silence_end: 2.5e0 | silence_duration: 2.5");
        Assert.Equal(new SilenceInterval(1.5e-05, 2.5), intervals.Single());
    }

    [Fact]
    public void Edge_trim_cuts_only_leading_and_trailing_silence_and_keeps_a_little_padding()
    {
        var trim = SilenceDetect.EdgeTrim(SilenceDetect.Parse(Log), 60.0);
        Assert.NotNull(trim);
        Assert.Equal(2.51188 - 0.1, trim!.Value.Start, 5);
        Assert.Equal(57.3 + 0.1, trim.Value.End, 5);
        // The silence at 30.2-31.4 is inside [start, end] and therefore untouched.
        Assert.InRange(30.2, trim.Value.Start, trim.Value.End);
    }

    [Fact]
    public void Edge_trim_with_only_a_leading_silence_keeps_the_end()
    {
        var trim = SilenceDetect.EdgeTrim([new SilenceInterval(0, 3)], 60);
        Assert.Equal((2.9, 60.0), (Math.Round(trim!.Value.Start, 6), trim.Value.End));
    }

    [Fact]
    public void Edge_trim_with_only_trailing_silence_keeps_the_start()
    {
        var trim = SilenceDetect.EdgeTrim([new SilenceInterval(50, null)], 60);
        Assert.Equal(0, trim!.Value.Start);
        Assert.Equal(50.1, trim.Value.End, 6);
    }

    [Fact]
    public void Edge_trim_with_middle_silence_only_changes_nothing()
    {
        var trim = SilenceDetect.EdgeTrim([new SilenceInterval(20, 25)], 60);
        Assert.Equal((0.0, 60.0), trim!.Value);
    }

    [Fact]
    public void A_file_that_is_all_silence_returns_null()
    {
        Assert.Null(SilenceDetect.EdgeTrim([new SilenceInterval(0, null)], 30));
        Assert.Null(SilenceDetect.EdgeTrim([new SilenceInterval(0.01, 29.99)], 30));
    }

    [Fact]
    public void Padding_never_extends_past_the_silence()
    {
        // 50 ms of leading silence with 100 ms of padding: start stays at 0.
        var trim = SilenceDetect.EdgeTrim([new SilenceInterval(0, 0.05)], 10);
        Assert.Equal(0, trim!.Value.Start);
    }

    [Fact]
    public void The_analysis_filter_text_is_stable()
    {
        Assert.Equal("silencedetect=noise=-50dB:d=0.5", SilenceDetect.Filter(-50, 0.5));
        Assert.Equal("silencedetect=noise=-42.5dB:d=1", SilenceDetect.Filter(-42.5, 1));
    }
}

public class LoudnormTests
{
    // Real loudnorm print_format=json output, preceded by log noise.
    private const string Log = """
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
        size=N/A time=00:03:10.00 bitrate=N/A speed= 210x
        """;

    [Fact]
    public void Parses_the_measurement_block_from_stderr()
    {
        var stats = Loudnorm.ParseStats("Input #0, mp3, from 'a.mp3':\n  Duration: 00:03:10.00\n" + Log)!;
        Assert.Equal("-23.54", stats.InputI);
        Assert.Equal("-6.18", stats.InputTp);
        Assert.Equal("5.80", stats.InputLra);
        Assert.Equal("-34.17", stats.InputThresh);
        Assert.Equal("0.66", stats.TargetOffset);
        Assert.Equal("dynamic", stats.NormalizationType);
        Assert.True(stats.IsUsable);
    }

    [Fact]
    public void Silent_input_measures_as_minus_infinity_and_is_not_usable()
    {
        var stats = Loudnorm.ParseStats(Log.Replace("\"-23.54\"", "\"-inf\"", StringComparison.Ordinal))!;
        Assert.Equal("-inf", stats.InputI);
        Assert.False(stats.IsUsable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{ \"input_i\" : \"-20\" }")]       // incomplete block
    [InlineData("\"input_i\" : \"-20\" }")]          // no opening brace
    public void Missing_or_incomplete_blocks_return_null(string log)
    {
        Assert.Null(Loudnorm.ParseStats(log));
    }

    [Fact]
    public void Pass_one_filter_asks_for_json()
    {
        Assert.Equal("loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", Loudnorm.Pass1Filter(-16, -1.5, 11));
    }

    [Fact]
    public void Pass_two_feeds_back_the_measured_values_in_linear_mode()
    {
        var stats = Loudnorm.ParseStats(Log)!;
        Assert.Equal(
            "loudnorm=I=-16:TP=-1.5:LRA=11:measured_I=-23.54:measured_TP=-6.18:measured_LRA=5.80:measured_thresh=-34.17:offset=0.66:linear=true:print_format=summary",
            Loudnorm.Pass2Filter(stats, -16, -1.5, 11));
    }

    [Fact]
    public void Targets_are_formatted_without_culture_surprises()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("TP=-1.5", Loudnorm.Pass1Filter(-14, -1.5, 9));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }
}

public class BitrateMathTests
{
    [Fact]
    public void A_ten_megabyte_budget_over_one_minute()
    {
        var plan = BitrateMath.PlanTargetSize(10, 60, hasAudio: true, preferredAudioKbps: 96);
        Assert.True(plan.Feasible);
        Assert.Equal(96, plan.AudioKbps);
        // 10 MB * 8 * 0.97 / 60 s = 1293 kbps total.
        Assert.Equal(1197, plan.VideoKbps);
    }

    [Theory]
    [InlineData(10, 60, true, 96)]
    [InlineData(25, 600, true, 128)]
    [InlineData(100, 3600, true, 192)]
    [InlineData(5, 30, false, 0)]
    [InlineData(16, 95.5, true, 96)]
    public void Expected_size_is_within_five_percent_under_the_target(double mb, double seconds, bool audio, int audioKbps)
    {
        var plan = BitrateMath.PlanTargetSize(mb, seconds, audio, audioKbps);
        Assert.True(plan.Feasible, plan.Problem);
        var ratio = plan.ExpectedBytes / (mb * BitrateMath.BytesPerMb);
        Assert.InRange(ratio, 0.95, 1.0);
    }

    [Fact]
    public void Audio_is_reduced_when_it_would_swallow_a_small_budget()
    {
        // 1 MB for 60 s = ~129 kbps total; 192 kbps audio is impossible.
        var plan = BitrateMath.PlanTargetSize(1, 60, true, 192);
        Assert.True(plan.AudioKbps <= 43);
        Assert.True(plan.AudioKbps >= BitrateMath.MinAudioKbps);
        Assert.True(plan.Feasible);
    }

    [Fact]
    public void Too_small_a_target_is_reported_with_the_numbers()
    {
        var plan = BitrateMath.PlanTargetSize(0.5, 3600, true, 96);
        Assert.False(plan.Feasible);
        Assert.Contains("too small", plan.Problem);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(10, 0)]
    [InlineData(-5, 60)]
    [InlineData(10, -1)]
    public void Non_positive_inputs_are_infeasible_not_a_crash(double mb, double seconds)
    {
        Assert.False(BitrateMath.PlanTargetSize(mb, seconds, true, 96).Feasible);
    }

    [Fact]
    public void Bits_per_pixel_and_bitrate_estimates()
    {
        Assert.Equal(1_200_000.0 / (1280 * 720 * 30), BitrateMath.BitsPerPixel(1200, 1280, 720, 30), 9);
        Assert.Equal(double.PositiveInfinity, BitrateMath.BitsPerPixel(1200, 0, 720, 30));
        Assert.Equal(6220, BitrateMath.EstimateVideoKbps(1920, 1080, 30)); // 1920*1080*30*0.1 / 1000
        Assert.Equal(4000, BitrateMath.EstimateVideoKbps(0, 0, 0));
    }
}

public class FfmpegErrorsTests
{
    [Theory]
    [InlineData("[libx264 @ 0x1] foo\nUnknown encoder 'libx265'\nError selecting an encoder\nConversion failed!", "no 'libx265' encoder")]
    [InlineData("[mov,mp4,m4a,3gp,3g2,mj2 @ 0000020c1] moov atom not found\nC:\\a.mp4: Invalid data found when processing input", "corrupt")]
    [InlineData("C:\\missing.mp4: No such file or directory", "could not open")]
    [InlineData("C:\\out\\x.mp4: Permission denied", "Permission denied")]
    [InlineData("av_interleaved_write_frame(): No space left on device", "disk is full")]
    [InlineData("Stream map '0:a:0' matches no streams.\nTo ignore this, add a trailing '?' to the map.", "does not contain")]
    [InlineData("Error initializing filter 'subtitles' with args 'x.srt'\nError opening filters!", "filter")]
    [InlineData("Unrecognized option 'fps_mode'.\nError splitting the argument list: Option not found", "Update FFmpeg")]
    [InlineData("Invalid argument\nCould not write header for output file #0", "rejected the output settings")]
    public void Known_failures_become_plain_sentences(string stderr, string expectedFragment)
    {
        Assert.Contains(expectedFragment, FfmpegErrors.Summarize(stderr), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_cause_printed_before_ffmpegs_generic_complaint_wins()
    {
        // Real shape of a rejected filter setting: the reason comes first, the generic "Invalid argument" last.
        const string stderr = "[Parsed_loudnorm_0 @ 0000023c3c2c1b40] Value -inf for parameter 'measured_thresh' out of range [-99 - 0]\n"
                              + "[Parsed_loudnorm_0 @ 0000023c3c2c1b40] Error setting option measured_thresh to value -inf.\n"
                              + "Error reinitializing filters!\n"
                              + "Failed to inject frame into filter network: Invalid argument\n"
                              + "Error while processing the decoded data for stream #0:0\n"
                              + "Conversion failed!";
        var message = FfmpegErrors.Summarize(stderr);
        Assert.StartsWith("FFmpeg could not build the filter", message);
        Assert.Contains("measured_thresh", message);
        Assert.DoesNotContain("rejected the output settings", message);
    }

    [Fact]
    public void A_bare_invalid_argument_still_reports_something_useful()
    {
        var message = FfmpegErrors.Summarize("Could not write header for output file #0 (incorrect codec parameters ?): Invalid argument\nConversion failed!");
        Assert.StartsWith("FFmpeg rejected the output settings", message);
        Assert.Contains("Could not write header", message);
    }

    [Fact]
    public void Out_of_range_settings_are_reported_with_the_offending_line()
    {
        var message = FfmpegErrors.Summarize("[aac @ 0x1] Value 900 for parameter 'b' out of range [0 - 600]");
        Assert.StartsWith("FFmpeg did not accept a setting", message);
    }

    [Fact]
    public void Unknown_failures_quote_the_last_meaningful_line_not_conversion_failed()
    {
        var message = FfmpegErrors.Summarize("something odd happened\nConversion failed!");
        Assert.Equal("FFmpeg failed: something odd happened", message);
    }

    [Fact]
    public void Empty_stderr_reports_the_exit_code()
    {
        Assert.Contains("exit code 7", FfmpegErrors.Summarize("", 7));
    }

    [Fact]
    public void Very_long_lines_are_truncated()
    {
        var message = FfmpegErrors.Summarize(new string('x', 2000));
        Assert.True(message.Length < 400);
    }
}

public class EncoderListTests
{
    [Fact]
    public void Parses_names_after_the_header_separator_only()
    {
        var encoders = FfmpegEncoders.Parse(TestMedia.EncodersGpl);
        Assert.True(encoders.Has("libx264"));
        Assert.True(encoders.Has("libvpx-vp9"));
        Assert.True(encoders.Has("aac"));
        Assert.True(encoders.Has("libmp3lame"));
        Assert.True(encoders.Has("h264_nvenc"));
        Assert.False(encoders.Has("libfdk_aac"));
        // The legend lines ("V..... = Video") sit above the separator and must not be mistaken for encoders.
        Assert.False(encoders.Has("="));
        Assert.False(encoders.Has("Video"));
    }

    [Fact]
    public void Handles_crlf_and_garbage()
    {
        var crlf = FfmpegEncoders.Parse(TestMedia.EncodersGpl.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.True(crlf.Has("libx265"));
        Assert.Empty(FfmpegEncoders.Parse("").Names);
        Assert.Empty(FfmpegEncoders.Parse("ffmpeg: command not found").Names);
        Assert.Empty(FfmpegEncoders.Parse("------\nnot an encoder line").Names);
    }

    [Fact]
    public void A_minimal_build_is_recognised_as_minimal()
    {
        var encoders = FfmpegEncoders.Parse(TestMedia.EncodersMinimal);
        Assert.True(encoders.Has("aac"));
        Assert.False(encoders.Has("libx264"));
    }
}

public class HardwareEncodersTests
{
    [Fact]
    public void Detects_vendor_encoders_per_codec()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        Assert.True(caps.Has(HardwareKind.Nvenc, VideoCodec.H264));
        Assert.True(caps.Has(HardwareKind.Nvenc, VideoCodec.H265));
        Assert.True(caps.Has(HardwareKind.Nvenc, VideoCodec.Av1));
        Assert.True(caps.Has(HardwareKind.Qsv, VideoCodec.H264));
        Assert.False(caps.Has(HardwareKind.Qsv, VideoCodec.Av1));
        Assert.True(caps.Has(HardwareKind.Amf, VideoCodec.H265));
        Assert.False(caps.Has(HardwareKind.Amf, VideoCodec.Av1));
        Assert.Equal("hevc_nvenc", caps.Find(HardwareKind.Nvenc, VideoCodec.H265)!.Name);
    }

    [Fact]
    public void A_software_only_build_has_no_hardware()
    {
        Assert.False(HardwareEncoders.Detect(TestMedia.EncodersMinimal).Any);
        Assert.False(HardwareEncoders.Detect("").Any);
    }

    [Fact]
    public void Auto_order_is_nvenc_then_qsv_then_amf_then_software()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        Assert.Equal(HardwareKind.Nvenc, HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Auto, caps)!.Kind);

        // Without NVENC the next is Quick Sync, then AMF.
        var noNvenc = new HardwareCapabilities(caps.Encoders.Where(e => e.Kind != HardwareKind.Nvenc).ToList());
        Assert.Equal(HardwareKind.Qsv, HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Auto, noNvenc)!.Kind);
        var amfOnly = new HardwareCapabilities(caps.Encoders.Where(e => e.Kind == HardwareKind.Amf).ToList());
        Assert.Equal(HardwareKind.Amf, HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Auto, amfOnly)!.Kind);
        Assert.Null(HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Auto, HardwareCapabilities.None));
    }

    [Fact]
    public void Av1_falls_through_to_the_vendor_that_has_it()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        var noNvenc = new HardwareCapabilities(caps.Encoders.Where(e => e.Kind != HardwareKind.Nvenc).ToList());
        Assert.Null(HardwareEncoders.Choose(VideoCodec.Av1, HardwarePreference.Auto, noNvenc)); // QSV/AMF lack AV1 in this build
    }

    [Fact]
    public void A_forced_vendor_never_switches_to_another()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        Assert.Null(HardwareEncoders.Choose(VideoCodec.Av1, HardwarePreference.Qsv, caps));
        Assert.Equal(HardwareKind.Amf, HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Amf, caps)!.Kind);
    }

    [Fact]
    public void None_copy_and_vp9_never_use_hardware()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        Assert.Null(HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.None, caps));
        Assert.Null(HardwareEncoders.Choose(VideoCodec.Copy, HardwarePreference.Auto, caps));
        Assert.Null(HardwareEncoders.Choose(VideoCodec.Vp9, HardwarePreference.Auto, caps));
    }

    [Fact]
    public void Detect_then_Best_picks_hardware_or_says_software()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        Assert.Equal("h264_nvenc", caps.Best(VideoCodec.H264)!.Name);
        Assert.Equal("hevc_amf", caps.Best(VideoCodec.H265, HardwarePreference.Amf)!.Name);
        Assert.Null(caps.Best(VideoCodec.H264, HardwarePreference.None));       // null = software
        Assert.Null(HardwareEncoders.Detect(TestMedia.EncodersMinimal).Best(VideoCodec.H264));
    }

    [Fact]
    public void A_trial_encode_result_can_veto_an_encoder()
    {
        var caps = HardwareEncoders.Detect(TestMedia.EncodersGpl);
        var chosen = HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Auto, caps, name => name.Contains("amf", StringComparison.Ordinal));
        Assert.Equal("h264_amf", chosen!.Name);
        Assert.Null(HardwareEncoders.Choose(VideoCodec.H264, HardwarePreference.Auto, caps, _ => false));
    }

    [Fact]
    public void Trial_arguments_encode_one_frame_to_the_null_device()
    {
        var args = HardwareEncoders.TrialArguments("h264_nvenc");
        Assert.Contains("h264_nvenc", args);
        Assert.True(Args.HasPair(args, "-frames:v", "1"));
        Assert.Equal(FfmpegCommand.NullDevice, args[^1]);
        Assert.Contains("color=c=black:s=256x256:d=0.2:r=10", args);
    }

    [Theory]
    [InlineData(HardwareKind.Nvenc, VideoCodec.H264, "h264_nvenc")]
    [InlineData(HardwareKind.Qsv, VideoCodec.H265, "hevc_qsv")]
    [InlineData(HardwareKind.Amf, VideoCodec.Av1, "av1_amf")]
    public void Encoder_names_follow_ffmpegs_convention(HardwareKind kind, VideoCodec codec, string expected)
    {
        Assert.Equal(expected, HardwareEncoders.EncoderName(kind, codec));
    }

    [Fact]
    public void Vp9_and_copy_have_no_hardware_name()
    {
        Assert.Null(HardwareEncoders.EncoderName(HardwareKind.Nvenc, VideoCodec.Vp9));
        Assert.Null(HardwareEncoders.EncoderName(HardwareKind.Nvenc, VideoCodec.Copy));
    }
}
