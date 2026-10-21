using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Tests.Encoding;

public class EncoderPlannerTests
{
    private static EncoderPlan Plan(EncoderSettings s, MediaInfo? info = null, PlanContext? ctx = null) =>
        EncoderPlanner.Plan(s, info ?? TestMedia.Video(), ctx ?? TestMedia.Ctx());

    // ------------------------------------------------------------------ the default MP4 encode

    [Fact]
    public void Default_settings_make_a_crf_23_h264_aac_mp4_with_faststart()
    {
        var plan = Plan(TestMedia.Defaults);
        Assert.True(plan.Ok, plan.Error);
        Assert.Equal("mp4", plan.OutputExtension);
        var a = plan.Passes.Single();

        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.True(Args.HasPair(a, "-crf", "23"));
        Assert.True(Args.HasPair(a, "-preset", "medium"));
        Assert.True(Args.HasPair(a, "-pix_fmt", "yuv420p"));
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.True(Args.HasPair(a, "-b:a", "128k"));
        Assert.True(Args.HasPair(a, "-movflags", "+faststart"));
        Assert.True(Args.HasPair(a, "-map_metadata", "0"));
        Assert.Equal("libx264", plan.VideoEncoder);
        Assert.Equal("aac", plan.AudioEncoder);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Global_options_come_first_progress_is_on_and_overwriting_is_refused()
    {
        var a = Plan(TestMedia.Defaults).Passes.Single();
        Assert.Equal("-hide_banner", a[0]);
        Assert.Contains("-nostdin", a);
        Assert.Contains("-n", a);          // never overwrite
        Assert.DoesNotContain("-y", a);
        Assert.True(Args.HasPair(a, "-progress", "pipe:1"));
        Assert.True(Args.IndexOf(a, "-progress") < Args.IndexOf(a, "-i"));
    }

    [Fact]
    public void Input_and_output_are_single_arguments_even_with_spaces_and_arabic()
    {
        const string input = @"C:\Users\موسى\فيديوهات\عطلة الصيف (نهائي).mp4";
        const string output = @"D:\Out Folder\عطلة الصيف (compressed).mp4";
        var plan = Plan(TestMedia.Defaults, TestMedia.Video(path: input), TestMedia.Ctx(output));
        var a = plan.Passes.Single();

        Assert.Equal(input, Args.After(a, "-i"));
        Assert.Equal(output, a[^1]);
        Assert.Equal(1, a.Count(x => x == input));
    }

    [Fact]
    public void Placeholders_are_used_when_paths_are_unknown()
    {
        var plan = EncoderPlanner.Plan(TestMedia.Defaults, TestMedia.Video() with { Path = null });
        var a = plan.Passes.Single();
        Assert.Equal(EncoderPlan.InputPlaceholder, Args.After(a, "-i"));
        Assert.Equal(EncoderPlan.OutputPlaceholder, a[^1]);
    }

    [Fact]
    public void Only_the_first_video_and_first_audio_stream_are_mapped_and_cover_art_is_skipped()
    {
        var info = TestMedia.Video();
        var a = Plan(TestMedia.Defaults, info).Passes.Single();
        Assert.True(Args.HasPair(a, "-map", "0:0"));
        Assert.True(Args.HasPair(a, "-map", "0:a:0?"));
    }

    [Fact]
    public void Removing_audio_drops_the_map_and_adds_an()
    {
        var a = Plan(TestMedia.Defaults with { RemoveAudio = true }).Passes.Single();
        Assert.Contains("-an", a);
        Assert.False(Args.HasPair(a, "-map", "0:a:0?"));
        Assert.DoesNotContain("-c:a", a);
    }

    [Fact]
    public void A_source_without_audio_gets_no_audio_arguments()
    {
        var a = Plan(TestMedia.Defaults, TestMedia.Video(audioCodec: null)).Passes.Single();
        Assert.Contains("-an", a);
        Assert.DoesNotContain("-c:a", a);
    }

    [Fact]
    public void Metadata_can_be_stripped()
    {
        var a = Plan(TestMedia.Defaults with { KeepMetadata = false }).Passes.Single();
        Assert.True(Args.HasPair(a, "-map_metadata", "-1"));
        Assert.True(Args.HasPair(a, "-map_chapters", "-1"));
    }

    // ------------------------------------------------------------------ codecs and containers

    [Fact]
    public void H265_in_mp4_is_tagged_hvc1_for_apple_players()
    {
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.H265 });
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libx265"));
        Assert.True(Args.HasPair(a, "-crf", "28"));
        Assert.True(Args.HasPair(a, "-tag:v", "hvc1"));
        Assert.Contains("log-level=error", Args.After(a, "-x265-params"));
    }

    [Fact]
    public void Vp9_uses_constant_quality_with_zero_bitrate()
    {
        var plan = Plan(TestMedia.Defaults with { Container = ContainerFormat.Webm, VideoCodec = VideoCodec.Vp9, AudioCodec = AudioCodec.Opus });
        var a = plan.Passes.Single();
        Assert.Equal("webm", plan.OutputExtension);
        Assert.True(Args.HasPair(a, "-c:v", "libvpx-vp9"));
        Assert.True(Args.HasPair(a, "-crf", "32"));
        Assert.True(Args.HasPair(a, "-b:v", "0"));
        Assert.True(Args.HasPair(a, "-row-mt", "1"));
        Assert.True(Args.HasPair(a, "-c:a", "libopus"));
        Assert.DoesNotContain("-movflags", a);
    }

    [Fact]
    public void Av1_uses_svt_with_a_numeric_preset()
    {
        var a = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Av1, Container = ContainerFormat.Mkv }).Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libsvtav1"));
        Assert.True(Args.HasPair(a, "-preset", "7"));
        Assert.True(Args.HasPair(a, "-crf", "30"));
    }

    [Fact]
    public void Webm_forces_vp9_and_opus_with_warnings()
    {
        var plan = Plan(TestMedia.Defaults with { Container = ContainerFormat.Webm }); // H.264 + AAC requested
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libvpx-vp9"));
        Assert.True(Args.HasPair(a, "-c:a", "libopus"));
        Assert.Equal(2, plan.Warnings.Count);
    }

    [Fact]
    public void Mov_does_not_get_opus_or_vp9()
    {
        var plan = Plan(TestMedia.Defaults with { Container = ContainerFormat.Mov, VideoCodec = VideoCodec.Vp9, AudioCodec = AudioCodec.Opus });
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.Equal("mov", plan.OutputExtension);
        Assert.True(Args.HasPair(a, "-movflags", "+faststart"));
    }

    [Fact]
    public void Flac_in_mp4_is_replaced_by_aac()
    {
        var plan = Plan(TestMedia.Defaults with { AudioCodec = AudioCodec.Flac });
        Assert.True(Args.HasPair(plan.Passes.Single(), "-c:a", "aac"));
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Mkv_accepts_flac_and_has_no_faststart()
    {
        var a = Plan(TestMedia.Defaults with { Container = ContainerFormat.Mkv, AudioCodec = AudioCodec.Flac }).Passes.Single();
        Assert.True(Args.HasPair(a, "-c:a", "flac"));
        Assert.DoesNotContain("-movflags", a);
    }

    // ------------------------------------------------------------------ stream copy

    [Fact]
    public void Stream_copy_copies_both_streams_and_ignores_quality_settings()
    {
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Copy, AudioCodec = AudioCodec.Copy });
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "copy"));
        Assert.True(Args.HasPair(a, "-c:a", "copy"));
        Assert.DoesNotContain("-crf", a);
        Assert.DoesNotContain("-vf", a);
        Assert.Equal("copy", plan.VideoEncoder);
    }

    [Fact]
    public void Stream_copy_plus_a_filter_becomes_a_reencode_with_a_warning()
    {
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Copy, MaxHeight = 720 });
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libx264"));
        Assert.Contains(plan.Warnings, w => w.Contains("Stream copy", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_copy_into_a_container_that_cannot_hold_the_codec_reencodes()
    {
        // VP9 cannot go into MOV.
        var info = TestMedia.Video(videoCodec: "vp9", audioCodec: "opus");
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Copy, Container = ContainerFormat.Mov }, info);
        Assert.True(Args.HasPair(plan.Passes.Single(), "-c:v", "libx264"));
        Assert.Single(plan.Warnings, w => w.Contains("cannot be stored", StringComparison.Ordinal));
    }

    [Fact]
    public void Copied_hevc_in_mp4_gets_the_hvc1_tag()
    {
        var info = TestMedia.Video(videoCodec: "hevc");
        var a = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Copy, AudioCodec = AudioCodec.Copy }, info).Passes.Single();
        Assert.True(Args.HasPair(a, "-tag:v", "hvc1"));
    }

    [Fact]
    public void Copy_audio_that_the_container_rejects_is_reencoded()
    {
        var info = TestMedia.Video(audioCodec: "pcm_s16le");
        var plan = Plan(TestMedia.Defaults with { AudioCodec = AudioCodec.Copy }, info);
        Assert.True(Args.HasPair(plan.Passes.Single(), "-c:a", "aac"));
        Assert.Single(plan.Warnings);
    }

    // ------------------------------------------------------------------ trimming

    [Fact]
    public void Trim_seeks_on_the_input_and_limits_the_duration_on_the_output()
    {
        var plan = Plan(TestMedia.Defaults with { TrimStart = TimeSpan.FromSeconds(10), TrimEnd = TimeSpan.FromSeconds(25.5) });
        var a = plan.Passes.Single();
        Assert.True(Args.IndexOf(a, "-ss") < Args.IndexOf(a, "-i"));
        Assert.Equal("10", Args.After(a, "-ss"));
        Assert.True(Args.IndexOf(a, "-t") > Args.IndexOf(a, "-i"));
        Assert.Equal("15.5", Args.After(a, "-t"));
        Assert.Equal(15.5, plan.OutputDurationSeconds);
    }

    [Fact]
    public void Trim_end_before_start_is_rejected()
    {
        var plan = Plan(TestMedia.Defaults with { TrimStart = TimeSpan.FromSeconds(30), TrimEnd = TimeSpan.FromSeconds(20) });
        Assert.False(plan.Ok);
        Assert.Empty(plan.Passes);
    }

    [Fact]
    public void Trim_start_past_the_end_of_the_file_is_rejected()
    {
        var plan = Plan(TestMedia.Defaults with { TrimStart = TimeSpan.FromSeconds(61) });
        Assert.False(plan.Ok);
        Assert.Contains("past the end", plan.Error);
    }

    [Fact]
    public void Trim_end_beyond_the_file_is_clamped()
    {
        var plan = Plan(TestMedia.Defaults with { TrimStart = TimeSpan.FromSeconds(50), TrimEnd = TimeSpan.FromSeconds(500) });
        Assert.Equal(10, plan.OutputDurationSeconds);
        Assert.Equal("10", Args.After(plan.Passes.Single(), "-t"));
    }

    [Fact]
    public void Stream_copy_trim_normalises_negative_timestamps()
    {
        var a = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Copy, AudioCodec = AudioCodec.Copy, TrimStart = TimeSpan.FromSeconds(5) }).Passes.Single();
        Assert.True(Args.HasPair(a, "-avoid_negative_ts", "make_zero"));
    }

    // ------------------------------------------------------------------ size, fps, filters

    [Fact]
    public void Max_height_downscales_with_an_even_width()
    {
        var plan = Plan(TestMedia.Defaults with { MaxHeight = 720 });
        Assert.Equal("scale=-2:720:flags=lanczos", Args.After(plan.Passes.Single(), "-vf"));
        Assert.Equal(1280, plan.OutputWidth);
        Assert.Equal(720, plan.OutputHeight);
    }

    [Fact]
    public void A_source_already_below_the_limit_is_not_scaled_or_enlarged()
    {
        var plan = Plan(TestMedia.Defaults with { MaxHeight = 720 }, TestMedia.Video(1280, 540));
        Assert.DoesNotContain("-vf", plan.Passes.Single());
    }

    [Fact]
    public void Odd_dimensions_are_forced_even_for_yuv420p()
    {
        var plan = Plan(TestMedia.Defaults, TestMedia.Video(1919, 1079));
        Assert.Equal("scale=trunc(iw/2)*2:trunc(ih/2)*2", Args.After(plan.Passes.Single(), "-vf"));
        Assert.Equal(1918, plan.OutputWidth);
        Assert.Equal(1078, plan.OutputHeight);
    }

    [Fact]
    public void Rotation_metadata_is_honoured_when_limiting_the_size()
    {
        // 1080x1920 stored, rotated 90 => displays 1920x1080 landscape; limit 720 high must scale.
        var info = TestMedia.Video(1080, 1920, rotation: 90);
        var plan = Plan(TestMedia.Defaults with { MaxHeight = 720 }, info);
        Assert.Equal("scale=-2:720:flags=lanczos", Args.After(plan.Passes.Single(), "-vf"));
        Assert.Equal(1280, plan.OutputWidth);
    }

    [Fact]
    public void Max_width_binds_for_portrait_video()
    {
        var plan = Plan(TestMedia.Defaults with { MaxWidth = 720 }, TestMedia.Video(1080, 1920));
        Assert.Equal("scale=720:-2:flags=lanczos", Args.After(plan.Passes.Single(), "-vf"));
        Assert.Equal(1280, plan.OutputHeight);
    }

    [Fact]
    public void Fps_cap_only_applies_to_faster_sources()
    {
        var capped = Plan(TestMedia.Defaults with { MaxFps = 30 }, TestMedia.Video(fps: 60));
        Assert.Contains("fps=30", Args.After(capped.Passes.Single(), "-vf"));

        var untouched = Plan(TestMedia.Defaults with { MaxFps = 30 }, TestMedia.Video(fps: 29.97));
        Assert.DoesNotContain("-vf", untouched.Passes.Single());
    }

    [Fact]
    public void Deinterlace_and_custom_filters_come_before_scaling()
    {
        var plan = Plan(TestMedia.Defaults with
        {
            Deinterlace = true,
            VideoFilters = ["crop=1280:720:0:0"],
            MaxHeight = 540,
        });
        var vf = Args.After(plan.Passes.Single(), "-vf")!;
        Assert.StartsWith("yadif,crop=1280:720:0:0,scale=", vf);
        // With a custom filter the output size is unknown to the planner, so the limit is evaluated by FFmpeg per frame.
        Assert.Contains("min(540,ih)", vf);
    }

    [Fact]
    public void Letterbox_canvas_scales_down_then_pads_to_the_exact_size()
    {
        var plan = Plan(TestMedia.Defaults with { Canvas = new CanvasSize(1080, 1920, FitMode.Letterbox) });
        var vf = Args.After(plan.Passes.Single(), "-vf")!;
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=decrease", vf);
        Assert.Contains("pad=1080:1920:(ow-iw)/2:(oh-ih)/2", vf);
        Assert.EndsWith("setsar=1", vf);
        Assert.Equal(1080, plan.OutputWidth);
        Assert.Equal(1920, plan.OutputHeight);
    }

    [Fact]
    public void Crop_canvas_scales_up_then_crops()
    {
        var vf = Args.After(Plan(TestMedia.Defaults with { Canvas = new CanvasSize(1080, 1920, FitMode.Crop) }).Passes.Single(), "-vf")!;
        Assert.Contains("force_original_aspect_ratio=increase", vf);
        Assert.Contains("crop=1080:1920", vf);
    }

    [Fact]
    public void Odd_canvas_sizes_are_rejected()
    {
        var plan = Plan(TestMedia.Defaults with { Canvas = new CanvasSize(1081, 1920) });
        Assert.False(plan.Ok);
    }

    [Fact]
    public void Hdr_sources_produce_a_warning()
    {
        var plan = Plan(TestMedia.Defaults, TestMedia.Video(hdr: true));
        Assert.Contains(plan.Warnings, w => w.Contains("HDR", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ audio

    [Fact]
    public void Mp3_audio_in_a_video_caps_at_two_channels()
    {
        var plan = Plan(TestMedia.Defaults with { Container = ContainerFormat.Mkv, AudioCodec = AudioCodec.Mp3 }, TestMedia.Video(channels: 6));
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:a", "libmp3lame"));
        Assert.True(Args.HasPair(a, "-ac", "2"));
    }

    [Fact]
    public void Audio_channels_can_be_forced_to_mono()
    {
        var a = Plan(TestMedia.Defaults with { AudioChannels = 1 }).Passes.Single();
        Assert.True(Args.HasPair(a, "-ac", "1"));
    }

    [Fact]
    public void Audio_filters_force_a_reencode_when_copy_was_asked()
    {
        var plan = Plan(TestMedia.Defaults with { AudioCodec = AudioCodec.Copy, AudioFilters = ["atempo=2"] });
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.Equal("atempo=2", Args.After(a, "-af"));
    }

    // ------------------------------------------------------------------ bitrate mode

    [Fact]
    public void Bitrate_mode_sets_b_v_with_a_vbv_cap()
    {
        var a = Plan(TestMedia.Defaults with { RateControl = RateControl.Bitrate, VideoBitrateKbps = 3000 }).Passes.Single();
        Assert.True(Args.HasPair(a, "-b:v", "3000k"));
        Assert.DoesNotContain("-crf", a);
    }

    [Fact]
    public void Bitrate_mode_without_a_bitrate_is_an_error()
    {
        Assert.False(Plan(TestMedia.Defaults with { RateControl = RateControl.Bitrate }).Ok);
    }

    // ------------------------------------------------------------------ target size

    [Fact]
    public void Target_size_uses_two_passes_with_a_shared_log_and_a_null_first_pass()
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 40 });
        Assert.True(plan.Ok, plan.Error);
        Assert.True(plan.IsTwoPass);

        var p1 = plan.Passes[0];
        var p2 = plan.Passes[1];
        Assert.True(Args.HasPair(p1, "-pass", "1"));
        Assert.True(Args.HasPair(p2, "-pass", "2"));
        Assert.Equal("dropflow-pass", Args.After(p1, "-passlogfile"));
        Assert.Equal("dropflow-pass", Args.After(p2, "-passlogfile"));
        Assert.Contains("-an", p1);
        Assert.True(Args.HasPair(p1, "-f", "null"));
        Assert.Equal(FfmpegCommand.NullDevice, p1[^1]);
        Assert.Equal(@"C:\Out\out.mp4", p2[^1]);
        Assert.DoesNotContain("-n", p1); // writing to null must not trip the overwrite guard
        Assert.Contains("-n", p2);
        Assert.Equal(Args.After(p1, "-b:v"), Args.After(p2, "-b:v"));
        Assert.False(Args.HasPair(p2, "-crf", "23"));
    }

    [Theory]
    [InlineData(40, 60)]
    [InlineData(10, 120)]
    [InlineData(25, 600)]
    [InlineData(100, 3600)]
    public void Target_size_math_lands_within_five_percent_below_the_target(double mb, double seconds)
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = mb, AllowDownscaleForTargetSize = false },
            TestMedia.Video(seconds: seconds));
        Assert.True(plan.Ok, plan.Error);

        var expectedBytes = (plan.VideoBitrateKbps!.Value + plan.AudioBitrateKbps!.Value) * 1000.0 * seconds / 8;
        var target = mb * 1_000_000;
        Assert.InRange(expectedBytes / target, 0.95, 1.0);
    }

    [Fact]
    public void Target_size_reserves_the_requested_audio_bitrate()
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 40, AudioBitrateKbps = 160 });
        Assert.Equal(160, plan.AudioBitrateKbps);
        Assert.True(Args.HasPair(plan.Passes[1], "-b:a", "160k"));
    }

    [Fact]
    public void Target_size_without_audio_spends_everything_on_video()
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 20 }, TestMedia.Video(audioCodec: null));
        Assert.Null(plan.AudioBitrateKbps);
        Assert.DoesNotContain("-c:a", plan.Passes[1]);
    }

    [Fact]
    public void Target_size_too_small_for_the_duration_fails_cleanly()
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 0.2 }, TestMedia.Video(seconds: 600));
        Assert.False(plan.Ok);
        Assert.Contains("too small", plan.Error);
    }

    [Fact]
    public void Target_size_needs_a_known_duration()
    {
        var info = TestMedia.Video() with { DurationSeconds = null };
        Assert.False(Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 10 }, info).Ok);
    }

    [Fact]
    public void Target_size_steps_the_resolution_down_when_there_are_too_few_bits_per_pixel()
    {
        // 10 MB for 60 s of 1080p30 leaves ~1.2 Mbps: that is 0.019 bits/pixel, far below what H.264 needs.
        var plan = Plan(EncoderPresets.Discord10MB, TestMedia.Video(seconds: 60));
        Assert.True(plan.Ok, plan.Error);
        Assert.Equal(540, plan.OutputHeight);
        Assert.Equal("scale=-2:540:flags=lanczos", Args.After(plan.Passes[1], "-vf"));
        Assert.Contains(plan.Warnings, w => w.Contains("Scaled down", StringComparison.Ordinal));
    }

    [Fact]
    public void Target_size_keeps_the_resolution_when_the_budget_is_generous()
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 400 }, TestMedia.Video(seconds: 60));
        Assert.Equal(1080, plan.OutputHeight);
        Assert.DoesNotContain("-vf", plan.Passes[1]);
    }

    [Fact]
    public void Downscaling_for_target_size_can_be_switched_off()
    {
        var plan = Plan(EncoderPresets.Discord10MB with { AllowDownscaleForTargetSize = false }, TestMedia.Video(seconds: 60));
        Assert.True(plan.Ok);
        Assert.Equal(1080, plan.OutputHeight);
    }

    [Fact]
    public void Target_size_x265_passes_travel_in_x265_params_with_a_bare_stats_name()
    {
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.H265, RateControl = RateControl.TargetSize, TargetSizeMb = 100 });
        Assert.True(plan.IsTwoPass);
        Assert.Equal("log-level=error:pass=1:stats=dropflow-pass.log", Args.After(plan.Passes[0], "-x265-params"));
        Assert.Equal("log-level=error:pass=2:stats=dropflow-pass.log", Args.After(plan.Passes[1], "-x265-params"));
        Assert.DoesNotContain("-pass", plan.Passes[0]);
    }

    [Fact]
    public void Target_size_vp9_is_two_pass_too()
    {
        var plan = Plan(TestMedia.Defaults with { Container = ContainerFormat.Webm, VideoCodec = VideoCodec.Vp9, RateControl = RateControl.TargetSize, TargetSizeMb = 100 });
        Assert.True(plan.IsTwoPass);
        Assert.True(Args.HasPair(plan.Passes[0], "-pass", "1"));
        Assert.True(Args.HasPair(plan.Passes[1], "-pass", "2"));
        Assert.True(Args.HasPair(plan.Passes[1], "-c:a", "libopus"));
    }

    [Fact]
    public void Target_size_with_av1_is_single_pass_vbr()
    {
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Av1, Container = ContainerFormat.Mkv, RateControl = RateControl.TargetSize, TargetSizeMb = 100 });
        Assert.False(plan.IsTwoPass);
        Assert.NotNull(Args.After(plan.Passes.Single(), "-b:v"));
    }

    [Fact]
    public void Target_size_trims_before_computing_the_budget()
    {
        var plan = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 10, TrimEnd = TimeSpan.FromSeconds(20), AllowDownscaleForTargetSize = false });
        Assert.Equal(20, plan.OutputDurationSeconds);
        // 10 MB over 20 s is a much higher bitrate than over 60 s.
        var full = Plan(TestMedia.Defaults with { RateControl = RateControl.TargetSize, TargetSizeMb = 10, AllowDownscaleForTargetSize = false });
        Assert.True(plan.VideoBitrateKbps > full.VideoBitrateKbps * 2);
    }

    // ------------------------------------------------------------------ hardware

    [Fact]
    public void Auto_hardware_prefers_nvenc_and_uses_cq_mode()
    {
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Auto }, ctx: TestMedia.Ctx(allHardware: true));
        var a = plan.Passes.Single();
        Assert.Equal("h264_nvenc", plan.VideoEncoder);
        Assert.True(Args.HasPair(a, "-c:v", "h264_nvenc"));
        Assert.True(Args.HasPair(a, "-rc", "vbr"));
        Assert.True(Args.HasPair(a, "-cq", "23"));
        Assert.True(Args.HasPair(a, "-preset", "p5"));
        Assert.DoesNotContain("-crf", a);
    }

    [Fact]
    public void Hardware_none_stays_on_software_even_when_gpu_encoders_exist()
    {
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.None }, ctx: TestMedia.Ctx(allHardware: true));
        Assert.Equal("libx264", plan.VideoEncoder);
    }

    [Fact]
    public void A_forced_unavailable_vendor_falls_back_to_software_with_a_warning()
    {
        // The sample build has hevc_amf but no av1_amf; forcing AMF for AV1 must not silently pick NVENC.
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Amf, VideoCodec = VideoCodec.Av1, Container = ContainerFormat.Mkv }, ctx: TestMedia.Ctx(allHardware: true));
        Assert.Equal("libsvtav1", plan.VideoEncoder);
        Assert.Contains(plan.Warnings, w => w.Contains("Amf", StringComparison.Ordinal));
    }

    [Fact]
    public void Hardware_that_fails_the_trial_is_skipped_in_auto_order()
    {
        var ctx = TestMedia.Ctx(allHardware: true) with { HardwareWorks = name => name.EndsWith("_qsv", StringComparison.Ordinal) };
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Auto }, ctx: ctx);
        Assert.Equal("h264_qsv", plan.VideoEncoder);
        Assert.True(Args.HasPair(plan.Passes.Single(), "-global_quality", "23"));
    }

    [Fact]
    public void Amf_uses_cqp_quantisers()
    {
        var ctx = TestMedia.Ctx(allHardware: true) with { HardwareWorks = name => name.EndsWith("_amf", StringComparison.Ordinal) };
        var a = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Auto }, ctx: ctx).Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "h264_amf"));
        Assert.True(Args.HasPair(a, "-rc", "cqp"));
        Assert.True(Args.HasPair(a, "-qp_i", "23"));
    }

    [Fact]
    public void Hardware_target_size_is_single_pass_vbr()
    {
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Nvenc, RateControl = RateControl.TargetSize, TargetSizeMb = 60 }, ctx: TestMedia.Ctx(allHardware: true));
        Assert.False(plan.IsTwoPass);
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-rc", "vbr"));
        Assert.NotNull(Args.After(a, "-maxrate"));
    }

    [Fact]
    public void Vp9_never_uses_hardware()
    {
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Auto, VideoCodec = VideoCodec.Vp9, Container = ContainerFormat.Webm }, ctx: TestMedia.Ctx(allHardware: true));
        Assert.Equal("libvpx-vp9", plan.VideoEncoder);
    }

    // ------------------------------------------------------------------ missing encoders degrade clearly

    private static PlanContext Minimal => TestMedia.Ctx() with { Encoders = FfmpegEncoders.Parse(TestMedia.EncodersMinimal) };

    [Fact]
    public void Missing_h264_encoder_is_a_clear_error_not_a_runtime_failure()
    {
        var plan = Plan(TestMedia.Defaults, ctx: Minimal);
        Assert.False(plan.Ok);
        Assert.Contains("no H.264 encoder", plan.Error);
        Assert.Empty(plan.Passes);
    }

    [Fact]
    public void Missing_h265_falls_back_to_h264_when_available()
    {
        var encoders = FfmpegEncoders.Parse(TestMedia.EncodersGpl.Replace("libx265", "libx265_missing", StringComparison.Ordinal));
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.H265 }, ctx: TestMedia.Ctx() with { Encoders = encoders });
        Assert.Equal("libx264", plan.VideoEncoder);
        Assert.Contains(plan.Warnings, w => w.Contains("libx265", StringComparison.Ordinal));
    }

    [Fact]
    public void Openh264_is_used_with_an_estimated_bitrate_when_libx264_is_missing()
    {
        var encoders = FfmpegEncoders.Parse("Encoders:\n ------\n V....D libopenh264          OpenH264 H.264 / AVC (codec h264)\n A....D aac                  AAC\n");
        var plan = Plan(TestMedia.Defaults, ctx: TestMedia.Ctx() with { Encoders = encoders });
        var a = plan.Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libopenh264"));
        Assert.NotNull(Args.After(a, "-b:v"));
        Assert.DoesNotContain("-crf", a);
        Assert.True(plan.Warnings.Count >= 2);
    }

    [Fact]
    public void Missing_vp9_and_av1_are_errors()
    {
        Assert.False(Plan(TestMedia.Defaults with { Container = ContainerFormat.Webm, VideoCodec = VideoCodec.Vp9 }, ctx: Minimal).Ok);
        Assert.False(Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Av1 }, ctx: Minimal).Ok);
    }

    [Fact]
    public void Av1_falls_back_to_libaom_when_svt_is_missing_and_says_so()
    {
        var withoutSvt = TestMedia.EncodersGpl.Replace("libsvtav1", "libsvtav1_gone", StringComparison.Ordinal)
                         + "\n V....D libaom-av1           libaom AV1 (codec av1)\n";
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Av1, Container = ContainerFormat.Mkv },
            ctx: TestMedia.Ctx() with { Encoders = FfmpegEncoders.Parse(withoutSvt) });

        Assert.True(plan.Ok, plan.Error);
        var a = plan.Passes.Single();
        Assert.Equal("libaom-av1", plan.VideoEncoder);
        Assert.True(Args.HasPair(a, "-c:v", "libaom-av1"));
        Assert.True(Args.HasPair(a, "-crf", "30"));
        Assert.True(Args.HasPair(a, "-b:v", "0"));
        Assert.True(Args.HasPair(a, "-cpu-used", "4"));
        Assert.Contains(plan.Warnings, w => w.Contains("libaom", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_mp3_encoder_falls_back_to_aac_for_audio_extraction()
    {
        var plan = Plan(EncoderPresets.AudioMp3_192k, TestMedia.Audio(), Minimal);
        Assert.Equal("m4a", plan.OutputExtension);
        Assert.Contains(plan.Warnings, w => w.Contains("libmp3lame", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream_copy_works_even_when_every_software_encoder_is_missing()
    {
        var plan = Plan(TestMedia.Defaults with { VideoCodec = VideoCodec.Copy, AudioCodec = AudioCodec.Copy }, ctx: Minimal);
        Assert.True(plan.Ok);
    }

    // ------------------------------------------------------------------ gif

    [Fact]
    public void Gif_uses_a_two_stage_palette_and_loops_forever()
    {
        var plan = Plan(EncoderPresets.GifLoop);
        var a = plan.Passes.Single();
        var vf = Args.After(a, "-vf")!;
        Assert.Equal("gif", plan.OutputExtension);
        Assert.StartsWith("fps=15,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=", vf);
        Assert.Contains("-an", a);
        Assert.True(Args.HasPair(a, "-loop", "0"));
        Assert.Equal(@"C:\Out\out.mp4", a[^1]); // the caller supplies the .gif path; the plan only reports the extension
    }

    [Fact]
    public void Gif_never_runs_faster_than_the_source_nor_enlarges()
    {
        var plan = Plan(EncoderPresets.GifLoop, TestMedia.Video(320, 240, fps: 10));
        var vf = Args.After(plan.Passes.Single(), "-vf")!;
        Assert.StartsWith("fps=10,split", vf);
        Assert.DoesNotContain(",scale=", vf);
    }

    [Fact]
    public void Gif_ignores_rate_control_with_a_warning()
    {
        var plan = Plan(EncoderPresets.GifLoop with { RateControl = RateControl.TargetSize, TargetSizeMb = 5 });
        Assert.True(plan.Ok);
        Assert.Single(plan.Warnings);
    }

    // ------------------------------------------------------------------ audio only

    [Fact]
    public void Extracting_mp3_drops_video_and_writes_id3v2_3()
    {
        var plan = Plan(EncoderPresets.AudioMp3_192k, ctx: TestMedia.Ctx(@"C:\Out\song.mp3"));
        var a = plan.Passes.Single();
        Assert.Equal("mp3", plan.OutputExtension);
        Assert.Contains("-vn", a);
        Assert.True(Args.HasPair(a, "-map", "0:a:0"));
        Assert.True(Args.HasPair(a, "-c:a", "libmp3lame"));
        Assert.True(Args.HasPair(a, "-b:a", "192k"));
        Assert.True(Args.HasPair(a, "-id3v2_version", "3"));
        Assert.DoesNotContain("-c:v", a);
    }

    [Theory]
    [InlineData(AudioCodec.Aac, "m4a")]
    [InlineData(AudioCodec.Opus, "opus")]
    [InlineData(AudioCodec.Flac, "flac")]
    [InlineData(AudioCodec.Mp3, "mp3")]
    public void Audio_only_extension_follows_the_codec(AudioCodec codec, string extension)
    {
        Assert.Equal(extension, Plan(new EncoderSettings { ExtractAudioOnly = true, AudioCodec = codec }).OutputExtension);
    }

    [Theory]
    [InlineData("aac", "m4a")]
    [InlineData("mp3", "mp3")]
    [InlineData("vorbis", "ogg")]
    [InlineData("opus", "opus")]
    [InlineData("pcm_s16le", "wav")]
    [InlineData("wmapro", "mka")]
    public void Audio_copy_extension_follows_the_source_codec(string source, string extension)
    {
        var plan = Plan(new EncoderSettings { ExtractAudioOnly = true, AudioCodec = AudioCodec.Copy }, TestMedia.Video(audioCodec: source));
        Assert.Equal(extension, plan.OutputExtension);
        Assert.True(Args.HasPair(plan.Passes.Single(), "-c:a", "copy"));
    }

    [Fact]
    public void Flac_extraction_has_no_bitrate()
    {
        var a = Plan(EncoderPresets.AudioFlacLossless).Passes.Single();
        Assert.True(Args.HasPair(a, "-c:a", "flac"));
        Assert.DoesNotContain("-b:a", a);
    }

    [Fact]
    public void Audio_extraction_from_a_silent_video_is_an_error()
    {
        Assert.False(Plan(EncoderPresets.AudioMp3_192k, TestMedia.Video(audioCodec: null)).Ok);
    }

    [Fact]
    public void A_video_encode_of_an_audio_file_is_an_error()
    {
        var plan = Plan(TestMedia.Defaults, TestMedia.Audio());
        Assert.False(plan.Ok);
        Assert.Contains("no video", plan.Error);
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void Every_preset_plans_successfully_for_a_1080p_source()
    {
        foreach (var preset in EncoderPresets.All)
        {
            var plan = Plan(preset.Settings with { Hardware = HardwarePreference.None }, TestMedia.Video(seconds: 90));
            Assert.True(plan.Ok, $"{preset.Id}: {plan.Error}");
            Assert.False(string.IsNullOrEmpty(plan.OutputExtension), preset.Id);
            Assert.NotEmpty(plan.Passes);
        }
    }

    [Fact]
    public void Presets_have_unique_ids_and_the_documented_set_exists()
    {
        var ids = EncoderPresets.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var expected in new[] { "same-quality-smaller", "whatsapp", "discord-10mb", "email-25mb", "youtube-1080p", "youtube-4k", "reels-9x16", "gif-loop", "audio-mp3-192k", "audio-flac" })
            Assert.Contains(expected, ids);
        Assert.NotNull(EncoderPresets.ById("whatsapp"));
        Assert.Null(EncoderPresets.ById("nope"));
    }

    [Fact]
    public void Reels_preset_letterbox_and_crop_differ()
    {
        var crop = Plan(EncoderPresets.Reels9x16(FitMode.Crop));
        var box = Plan(EncoderPresets.Reels9x16(FitMode.Letterbox));
        Assert.Contains("crop=1080:1920", Args.After(crop.Passes.Single(), "-vf"));
        Assert.Contains("pad=1080:1920", Args.After(box.Passes.Single(), "-vf"));
    }

    [Fact]
    public void Size_limited_presets_aim_below_their_limit()
    {
        foreach (var (preset, limitMb) in new[] { (EncoderPresets.WhatsApp, 16.0), (EncoderPresets.Discord10MB, 10.0), (EncoderPresets.Email25MB, 25.0) })
        {
            var plan = Plan(preset, TestMedia.Video(seconds: 120));
            var bytes = (plan.VideoBitrateKbps!.Value + plan.AudioBitrateKbps!.Value) * 1000.0 * 120 / 8;
            Assert.True(bytes < limitMb * 1_000_000, $"{limitMb} MB preset would produce {bytes / 1e6:0.0} MB");
            Assert.True(bytes > limitMb * 1_000_000 * 0.9);
        }
    }

    [Fact]
    public void Youtube_1080p_downscales_4k_and_keeps_1080p()
    {
        var from4k = Plan(EncoderPresets.YouTube1080p, TestMedia.Video(3840, 2160));
        Assert.Equal(1080, from4k.OutputHeight);
        Assert.Contains("-crf", from4k.Passes.Single());
        Assert.DoesNotContain("-vf", Plan(EncoderPresets.YouTube1080p).Passes.Single());
    }

    [Fact]
    public void Same_quality_smaller_is_h265_crf26_slow()
    {
        var a = Plan(EncoderPresets.SameQualitySmaller).Passes.Single();
        Assert.True(Args.HasPair(a, "-c:v", "libx265"));
        Assert.True(Args.HasPair(a, "-crf", "26"));
        Assert.True(Args.HasPair(a, "-preset", "slow"));
    }

    [Theory]
    [InlineData("ultrafast", "p1")]
    [InlineData("fast", "p4")]
    [InlineData("medium", "p5")]
    [InlineData("veryslow", "p7")]
    [InlineData("bogus", "p5")]
    public void Named_presets_map_onto_nvenc_levels(string preset, string expected)
    {
        var plan = Plan(TestMedia.Defaults with { Hardware = HardwarePreference.Nvenc, Preset = preset }, ctx: TestMedia.Ctx(allHardware: true));
        Assert.True(Args.HasPair(plan.Passes.Single(), "-preset", expected));
    }

    [Fact]
    public void Crf_is_clamped_to_the_encoder_range()
    {
        Assert.True(Args.HasPair(Plan(TestMedia.Defaults with { Crf = 99 }).Passes.Single(), "-crf", "51"));
        Assert.True(Args.HasPair(Plan(TestMedia.Defaults with { Crf = -4 }).Passes.Single(), "-crf", "0"));
    }
}
