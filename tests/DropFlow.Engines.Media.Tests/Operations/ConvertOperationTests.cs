using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Tests.Operations;

public class ConvertOperationTests
{
    // ------------------------------------------------------------------ Supports

    [Fact]
    public void Supports_covers_the_audio_matrix_without_same_to_same()
    {
        using var h = new MediaHarness();
        string[] audio = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"];
        foreach (var from in audio)
            foreach (var to in audio)
                Assert.Equal(from != to, h.Converters.Any(c => c.Supports("convert:" + to, from)));
    }

    [Fact]
    public void Supports_covers_the_video_matrix()
    {
        using var h = new MediaHarness();
        string[] videoIn = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v"];
        string[] videoOut = ["mp4", "mkv", "webm", "avi", "wmv", "gif", "mov"];
        string[] toAudio = ["mp3", "m4a", "wav"];
        foreach (var from in videoIn)
        {
            foreach (var to in videoOut) Assert.Equal(from != to, h.Converters.Any(c => c.Supports("convert:" + to, from)));
            foreach (var to in toAudio) Assert.True(h.Converters.Any(c => c.Supports("convert:" + to, from)), $"{from}->{to}");
            // Video can't become the audio-only formats that are not in the catalog's video outputs.
            foreach (var to in new[] { "flac", "ogg", "opus", "aiff", "wma", "aac" })
                Assert.False(h.Converters.Any(c => c.Supports("convert:" + to, from)), $"{from}->{to} must not be advertised");
        }
    }

    [Fact]
    public void Gif_is_a_video_source_only_for_video_targets()
    {
        using var h = new MediaHarness();
        foreach (var to in new[] { "mp4", "mkv", "webm", "avi", "wmv", "mov" })
            Assert.True(h.Converters.Any(c => c.Supports("convert:" + to, "gif")), to);
        foreach (var to in new[] { "gif", "mp3", "m4a", "wav", "png", "jpg" })
            Assert.False(h.Converters.Any(c => c.Supports("convert:" + to, "gif")), to);
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("png")]
    [InlineData("pdf")]
    [InlineData("srt")]
    public void Unrelated_inputs_are_not_supported(string input)
    {
        using var h = new MediaHarness();
        Assert.DoesNotContain(h.Converters, c => c.Supports("convert:mp3", input));
    }

    // ------------------------------------------------------------------ audio runs

    [Fact]
    public async Task Mp3_to_flac_builds_the_expected_command_and_writes_beside_the_input()
    {
        using var h = new MediaHarness();
        var input = h.Audio("song.mp3");

        var result = await h.Run("convert:flac", input);

        Assert.True(result.Success, result.Error);
        var expectedOutput = Path.Combine(h.Dir, "song (converted).flac");
        Assert.Equal([expectedOutput], result.Outputs);
        Assert.True(File.Exists(expectedOutput));

        var call = Assert.Single(h.Runner.Calls);
        Assert.Equal(input, call.After("-i"));
        Assert.Equal(expectedOutput, call.Output);
        Assert.True(Args.HasPair(call.Args, "-map", "0:a:0"));
        Assert.True(Args.HasPair(call.Args, "-c:a", "flac"));
        Assert.True(Args.HasPair(call.Args, "-map_metadata", "0"));
        Assert.Contains("-n", call.Args);          // FFmpeg itself refuses to overwrite
        Assert.DoesNotContain("-y", call.Args);
        Assert.True(File.Exists(input));          // original untouched
    }

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("ogg", "libvorbis")]
    [InlineData("opus", "libopus")]
    [InlineData("wma", "wmav2")]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("aiff", "pcm_s16be")]
    [InlineData("flac", "flac")]
    [InlineData("aac", "aac")]
    public async Task Each_audio_target_uses_its_documented_encoder(string target, string encoder)
    {
        using var h = new MediaHarness();
        var source = target == "wav" ? "flac" : "wav";
        var input = h.Audio("in." + source, codec: source == "wav" ? "pcm_s16le" : "flac");

        var result = await h.Run("convert:" + target, input);

        Assert.True(result.Success, result.Error);
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-c:a", encoder));
        Assert.EndsWith("." + target, result.Outputs.Single());
    }

    [Fact]
    public async Task Multichannel_sources_are_downmixed_for_mp3_and_wma()
    {
        using var h = new MediaHarness();
        var input = h.Audio("surround.flac", "flac", channels: 6);
        await h.Run("convert:mp3", input);
        Assert.True(Args.HasPair(h.Runner.Calls.Last().Args, "-ac", "2"));
    }

    [Fact]
    public async Task Twenty_four_bit_sources_stay_twenty_four_bit_in_wav()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "hires.flac");
        h.Input("hires.flac", new MediaInfo
        {
            Path = path,
            DurationSeconds = 10,
            Streams = [new StreamInfo { Index = 0, Kind = StreamKind.Audio, CodecName = "flac", Channels = 2, SampleRate = 96000, BitDepth = 24 }],
        });
        await h.Run("convert:wav", path);
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-c:a", "pcm_s24le"));
    }

    [Fact]
    public async Task Optional_bitrate_option_is_honoured()
    {
        using var h = new MediaHarness();
        var input = h.Audio("a.wav", "pcm_s16le");
        await h.Run("convert:mp3", input, MediaHarness.Opts(("bitrate", 256)));
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-b:a", "256k"));
        Assert.DoesNotContain("-q:a", args);
    }

    [Fact]
    public async Task Default_mp3_quality_is_vbr_v2()
    {
        using var h = new MediaHarness();
        await h.Run("convert:mp3", h.Audio("a.wav", "pcm_s16le"));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-q:a", "2"));
    }

    [Fact]
    public async Task Cover_art_is_carried_from_mp3_to_m4a()
    {
        using var h = new MediaHarness();
        var input = h.Audio("album.mp3", cover: true);
        await h.Run("convert:m4a", input);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-map", "0:1"));
        Assert.True(Args.HasPair(args, "-disposition:v:0", "attached_pic"));
    }

    [Fact]
    public async Task Cover_art_is_not_mapped_into_formats_that_cannot_hold_it()
    {
        using var h = new MediaHarness();
        var input = h.Audio("album.mp3", cover: true);
        await h.Run("convert:ogg", input);
        Assert.DoesNotContain("attached_pic", h.Runner.Calls.Single().Args);
    }

    [Fact]
    public async Task A_muxer_that_rejects_the_cover_picture_gets_one_retry_without_it()
    {
        using var h = new MediaHarness();
        var input = h.Audio("album.mp3", cover: true);
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (!call.Has("attached_pic")) return Task.FromResult<ProcessResult?>(null);
            FakeToolRunner.WriteOutput(call.Output); // the failed attempt left a partial file
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Could not write header for output file #0\nInvalid argument"));
        };

        var result = await h.Run("convert:m4a", input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);
        Assert.Contains("attached_pic", h.Runner.Calls[0].Args);
        Assert.DoesNotContain("attached_pic", h.Runner.Calls[1].Args);
        Assert.True(File.Exists(result.Outputs.Single()));
    }

    [Fact]
    public async Task A_video_with_no_audio_cannot_be_converted_to_mp3()
    {
        using var h = new MediaHarness();
        var input = h.Video("silent.mp4", audioCodec: null);
        var result = await h.Run("convert:mp3", input);
        Assert.False(result.Success);
        Assert.Contains("no audio", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Extracting_audio_from_video_maps_only_the_first_audio_stream()
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:mp3", h.Video("clip.mkv"));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-map", "0:a:0"));
        Assert.DoesNotContain("-c:v", args);
    }

    // ------------------------------------------------------------------ video runs

    [Fact]
    public async Task Mp4_to_mkv_copies_streams_the_container_accepts()
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:mkv", h.Video("clip.mp4"));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "copy"));
        Assert.True(Args.HasPair(args, "-c:a", "copy"));
        Assert.EndsWith("clip (converted).mkv", result.Outputs.Single());
    }

    [Fact]
    public async Task Mkv_reencodes_only_the_stream_it_cannot_copy()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "old.wmv");
        h.Input("old.wmv", TestMedia.Video(videoCodec: "wmv3", audioCodec: "wmav2", path: path));
        await h.Run("convert:mkv", path);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "libx264"));
        Assert.True(Args.HasPair(args, "-c:a", "aac"));
    }

    [Fact]
    public async Task Mkv_copies_video_but_converts_incompatible_audio()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "x.avi");
        h.Input("x.avi", TestMedia.Video(videoCodec: "mpeg4", audioCodec: "wmav2", path: path));
        await h.Run("convert:mkv", path);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "copy"));
        Assert.True(Args.HasPair(args, "-c:a", "aac"));
    }

    [Fact]
    public async Task Mov_to_mp4_is_h264_aac_with_faststart_and_even_dimensions()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "phone.mov");
        h.Input("phone.mov", TestMedia.Video(1919, 1079, path: path));
        var result = await h.Run("convert:mp4", path);
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "libx264"));
        Assert.True(Args.HasPair(args, "-pix_fmt", "yuv420p"));
        Assert.True(Args.HasPair(args, "-movflags", "+faststart"));
        Assert.Equal("scale=trunc(iw/2)*2:trunc(ih/2)*2", Args.After(args, "-vf"));
        Assert.True(Args.HasPair(args, "-preset", "fast"));
        Assert.True(Args.HasPair(args, "-crf", "21"));
    }

    [Fact]
    public async Task Webm_gets_vp9_and_opus()
    {
        using var h = new MediaHarness();
        await h.Run("convert:webm", h.Video("clip.mp4"));
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "libvpx-vp9"));
        Assert.True(Args.HasPair(args, "-c:a", "libopus"));
    }

    [Fact]
    public async Task Avi_uses_mpeg4_and_mp3_with_the_xvid_tag()
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:avi", h.Video("clip.mp4"));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "mpeg4"));
        Assert.True(Args.HasPair(args, "-vtag", "XVID"));
        Assert.True(Args.HasPair(args, "-c:a", "libmp3lame"));
        Assert.Equal("scale=trunc(iw/2)*2:trunc(ih/2)*2", Args.After(args, "-vf"));
    }

    [Fact]
    public async Task Avi_falls_back_to_ac3_when_lame_is_missing()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersGpl.Replace("libmp3lame", "libmp3lame_gone", StringComparison.Ordinal);
        await h.Run("convert:avi", h.Video("clip.mp4"));
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-c:a", "ac3"));
    }

    [Fact]
    public async Task Wmv_uses_wmv2_and_wmav2()
    {
        using var h = new MediaHarness();
        await h.Run("convert:wmv", h.Video("clip.mp4"));
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "wmv2"));
        Assert.True(Args.HasPair(args, "-c:a", "wmav2"));
    }

    [Fact]
    public async Task Gif_output_uses_a_palette_and_the_documented_defaults()
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:gif", h.Video("clip.mp4"));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        var vf = Args.After(args, "-vf")!;
        Assert.StartsWith("fps=15,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen", vf);
        Assert.Contains("paletteuse", vf);
        Assert.True(Args.HasPair(args, "-loop", "0"));
        Assert.Contains("-an", args);
    }

    [Fact]
    public async Task Gif_fps_and_width_options_are_applied()
    {
        using var h = new MediaHarness();
        await h.Run("convert:gif", h.Video("clip.mp4"), MediaHarness.Opts(("fps", 10), ("width", 320)));
        Assert.StartsWith("fps=10,scale=320:-1", Args.After(h.Runner.Calls.Single().Args, "-vf"));
    }

    [Fact]
    public async Task An_animated_gif_can_be_converted_to_mp4()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "loop.gif");
        h.Input("loop.gif", new MediaInfo
        {
            Path = path,
            DurationSeconds = 3,
            Streams = [new StreamInfo { Index = 0, Kind = StreamKind.Video, CodecName = "gif", Width = 481, Height = 271, FrameRate = 10 }],
        });
        var result = await h.Run("convert:mp4", path);
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.Equal("scale=trunc(iw/2)*2:trunc(ih/2)*2", Args.After(args, "-vf")); // 481x271 would fail yuv420p without this
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public async Task Crf_and_preset_options_apply()
    {
        using var h = new MediaHarness();
        await h.Run("convert:mp4", h.Video("clip.mkv"), MediaHarness.Opts(("crf", 18), ("preset", "slow")));
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-crf", "18"));
        Assert.True(Args.HasPair(args, "-preset", "slow"));
    }

    [Fact]
    public async Task An_audio_only_file_cannot_become_a_video()
    {
        using var h = new MediaHarness();
        var path = Path.Combine(h.Dir, "x.mp4");
        h.Input("x.mp4", TestMedia.Audio("aac", path: path));
        var result = await h.Run("convert:mkv", path);
        Assert.False(result.Success);
        Assert.Contains("no video", result.Error);
    }

    // ------------------------------------------------------------------ shared behaviour

    [Fact]
    public async Task Unicode_and_spaces_in_paths_reach_ffmpeg_as_single_intact_arguments()
    {
        using var h = new MediaHarness();
        var input = h.Audio(Path.Combine("مجلد الموسيقى 2026", "أغنية جميلة (نسخة 2).mp3"));

        var result = await h.Run("convert:wav", input);

        Assert.True(result.Success, result.Error);
        var call = h.Runner.Calls.Single();
        Assert.Equal(input, call.After("-i"));
        Assert.Equal(Path.Combine(h.Dir, "مجلد الموسيقى 2026", "أغنية جميلة (نسخة 2) (converted).wav"), call.Output);
        Assert.Equal(1, call.Args.Count(a => a.Contains("أغنية", StringComparison.Ordinal) && a.Contains("converted", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_result()
    {
        using var h = new MediaHarness();
        var input = h.Audio("song.mp3");
        var existing = Path.Combine(h.Dir, "song (converted).flac");
        await File.WriteAllTextAsync(existing, "precious");

        var result = await h.Run("convert:flac", input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(h.Dir, "song (converted 2).flac"), result.Outputs.Single());
        Assert.Equal("precious", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Output_directory_is_honoured_and_created()
    {
        using var h = new MediaHarness();
        var input = h.Audio("song.mp3");
        var outDir = Path.Combine(h.Dir, "out", "converted files");

        var result = await h.Run("convert:wav", input, outputDirectory: outDir);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(outDir, "song (converted).wav"), result.Outputs.Single());
        Assert.True(File.Exists(result.Outputs.Single()));
    }

    [Fact]
    public async Task Progress_goes_from_zero_to_one_and_never_backwards()
    {
        using var h = new MediaHarness();
        var log = new ProgressLog();
        await h.Run("convert:wav", h.Audio("song.mp3"), progress: log);

        var fractions = log.Reports.Select(r => r.Fraction).ToList();
        Assert.True(fractions.Count >= 3);
        Assert.Equal(0, fractions[0]);
        Assert.Equal(1, fractions[^1]);
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
        Assert.Equal(fractions.OrderBy(f => f), fractions);
    }

    [Fact]
    public async Task Progress_uses_the_probed_duration_as_the_denominator()
    {
        using var h = new MediaHarness();
        var log = new ProgressLog();
        // The fake reports out_time_us=500000 and 1000000 (0.5 s and 1 s) of a 30 s file.
        await h.Run("convert:wav", h.Audio("song.mp3", seconds: 30), progress: log);
        Assert.Contains(log.Reports, r => Math.Abs(r.Fraction - 0.5 / 30) < 1e-6);
    }

    [Fact]
    public async Task A_failing_ffmpeg_gives_a_readable_error_and_removes_the_partial_output()
    {
        using var h = new MediaHarness();
        var input = h.Audio("song.mp3");
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            FakeToolRunner.WriteOutput(call.Output);
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("[libmp3lame @ 0x1] Unknown encoder 'libmp3lame'\nConversion failed!"));
        };

        var result = await h.Run("convert:wav", input);

        Assert.False(result.Success);
        Assert.Contains("no 'libmp3lame' encoder", result.Error);
        Assert.Empty(result.Outputs);
        Assert.DoesNotContain(h.Tree(), p => p.Contains("converted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_corrupt_input_is_reported_before_ffmpeg_is_started()
    {
        using var h = new MediaHarness();
        var input = h.Audio("broken.mp3");
        h.Runner.ProbeFails(input);

        var result = await h.Run("convert:wav", input);

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.Error);
        Assert.Contains("corrupt", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Cancelling_kills_the_job_and_leaves_no_partial_file()
    {
        using var h = new MediaHarness();
        var input = h.Audio("song.mp3");
        using var cts = new CancellationTokenSource();
        h.Runner.OnFfmpeg = async (call, _, ct) =>
        {
            FakeToolRunner.WriteOutput(call.Output); // ffmpeg had started writing
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            return new ProcessResult(-1, "", "", TimeSpan.Zero, false, true);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run("convert:wav", input, ct: cts.Token));

        Assert.DoesNotContain(h.Tree(), p => p.Contains("converted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Already_cancelled_before_start_does_nothing()
    {
        using var h = new MediaHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run("convert:wav", h.Audio("song.mp3"), ct: cts.Token));
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Without_ffmpeg_the_job_fails_naming_it()
    {
        using var h = new MediaHarness();
        h.Runner.FfmpegPath = null;
        var result = await h.Run("convert:wav", h.Audio("song.mp3"));
        Assert.False(result.Success);
        Assert.Contains("FFmpeg", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Without_ffprobe_conversions_still_run_just_without_validation()
    {
        using var h = new MediaHarness();
        h.Runner.FfprobePath = null;
        var result = await h.Run("convert:mp4", h.Video("clip.mkv"));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-map", "0:v:0"));   // positional because the index is unknown
        Assert.True(Args.HasPair(args, "-c:v", "libx264"));
        Assert.Empty(h.Runner.ProbeCalls);
    }

    [Fact]
    public async Task A_missing_input_file_fails_cleanly()
    {
        using var h = new MediaHarness();
        var result = await h.For("convert:wav", "mp3").RunAsync(new ConversionJob([Path.Combine(h.Dir, "nope.mp3")], "convert:wav"), new ProgressLog(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task The_encoder_list_is_read_once_per_module()
    {
        using var h = new MediaHarness();
        await h.Run("convert:wav", h.Audio("a.mp3"));
        await h.Run("convert:flac", h.Audio("b.mp3"));
        await h.Run("convert:mp4", h.Video("c.mkv"));
        Assert.Equal(1, h.Runner.EncoderListCalls);
    }

    [Fact]
    public async Task If_the_encoder_list_cannot_be_read_a_standard_build_is_assumed()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersFail = true;
        var result = await h.Run("convert:mp3", h.Audio("a.wav", "pcm_s16le"));
        Assert.True(result.Success, result.Error);
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-c:a", "libmp3lame"));
    }

    [Fact]
    public async Task A_missing_mp3_encoder_is_reported_by_name_before_running()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersMinimal;
        var result = await h.Run("convert:mp3", h.Audio("a.wav", "pcm_s16le"));
        Assert.False(result.Success);
        Assert.Contains("libmp3lame", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task A_bad_option_value_is_a_clear_failure()
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:mp3", h.Audio("a.wav", "pcm_s16le"), MediaHarness.Opts(("bitrate", "lots")));
        Assert.False(result.Success);
        Assert.Contains("bitrate", result.Error);
    }
}
