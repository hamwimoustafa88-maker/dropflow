using DropFlow.Core.Operations;

namespace DropFlow.Engines.Media.Tests.Operations;

/// <summary>
/// The formats added for read-only audio and video sources, the iPhone ringtone (.m4r), AC-3, ALAC in .m4a and the animated
/// WebP / APNG outputs: what is advertised and the FFmpeg commands that are built (fake FFmpeg; the real runs are in Integration).
/// </summary>
public class NewMediaFormatsTests
{
    private static readonly string[] ReadOnlyAudio = ["amr", "ape", "wv", "tta", "dsf", "dff", "ac3", "dts", "mka", "caf", "m4r"];
    private static readonly string[] AudioOut = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac", "m4r", "ac3"];
    private static readonly string[] NewVideoIn = ["ts", "m2ts", "mts", "3gp", "ogv", "vob", "mpg", "mpeg", "dat"];
    private static readonly string[] VideoOut = ["mp4", "mkv", "webm", "avi", "wmv", "gif", "mov", "webp", "apng"];

    private const string WebpEncoder = "\n V....D libwebp_anim         libwebp WebP image (codec webp)\n";

    [Fact]
    public void Every_audio_input_old_and_new_converts_to_every_audio_output_but_itself()
    {
        using var h = new MediaHarness();
        foreach (var from in ReadOnlyAudio.Concat(["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"]))
            foreach (var to in AudioOut)
                Assert.True(h.Converters.Any(c => c.Supports("convert:" + to, from)) == (from != to), $"{from}->{to}");
    }

    [Fact]
    public void New_video_inputs_convert_to_every_video_output_and_the_three_audio_ones()
    {
        using var h = new MediaHarness();
        foreach (var from in NewVideoIn.Concat(["mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v"]))
        {
            foreach (var to in VideoOut) Assert.True(h.Converters.Any(c => c.Supports("convert:" + to, from)) == (from != to), $"{from}->{to}");
            foreach (var to in new[] { "mp3", "m4a", "wav" }) Assert.True(h.Converters.Any(c => c.Supports("convert:" + to, from)), $"{from}->{to}");
            foreach (var to in new[] { "flac", "m4r", "ac3" }) Assert.False(h.Converters.Any(c => c.Supports("convert:" + to, from)), $"{from}->{to}");
        }
    }

    [Fact]
    public void Gif_stays_with_the_image_engine_for_webp_and_the_read_only_inputs_get_no_tools()
    {
        using var h = new MediaHarness();
        Assert.DoesNotContain(h.Converters, c => c.Supports("convert:webp", "gif"));
        Assert.DoesNotContain(h.Converters, c => c.Supports("convert:apng", "gif"));
        // The tools write the input's own format back, which FFmpeg cannot encode for these: conversions only.
        foreach (var ext in ReadOnlyAudio) Assert.False(h.Converters.Any(c => c.Supports("aud.compress", ext)), ext);
        foreach (var ext in NewVideoIn) Assert.False(h.Converters.Any(c => c.Supports("vid.compress", ext)), ext);
    }

    // ------------------------------------------------------------------ ringtone

    [Fact]
    public async Task A_long_song_becomes_a_40_second_iphone_ringtone_with_a_note()
    {
        using var h = new MediaHarness();
        var input = h.Audio("أغنية.mp3", seconds: 215);

        var result = await h.Run("convert:m4r", input);

        Assert.True(result.Success, result.Error);
        var call = h.Runner.Calls.Single();
        Assert.True(Args.HasPair(call.Args, "-c:a", "aac"));
        Assert.True(Args.HasPair(call.Args, "-f", "ipod"));
        Assert.True(Args.HasPair(call.Args, "-t", "40"));
        Assert.EndsWith("أغنية (converted).m4r", call.Output);
        Assert.True(Args.IndexOf(call.Args, "-t") > Args.IndexOf(call.Args, "-i"));   // an output option: the first 40 s are kept
        Assert.Equal("note.aud.ringtoneCut|40", result.Note);
    }

    [Fact]
    public async Task A_short_sound_becomes_a_ringtone_without_a_note()
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:m4r", h.Audio("ding.wav", "pcm_s16le", seconds: 12));
        Assert.True(result.Success, result.Error);
        Assert.Null(result.Note);
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-t", "40"));       // harmless, and it holds even if the length was wrong
    }

    // ------------------------------------------------------------------ AC-3 and ALAC

    [Theory]
    [InlineData(2, "192k", null)]
    [InlineData(6, "448k", null)]
    [InlineData(8, "448k", "6")]
    public async Task Ac3_output_uses_the_native_encoder_with_a_rate_for_its_channels(int channels, string bitrate, string? downmix)
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersMinimal;                      // ac3 is native to FFmpeg: no library needed
        var result = await h.Run("convert:ac3", h.Audio("in.flac", "flac", channels: channels));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:a", "ac3"));
        Assert.True(Args.HasPair(args, "-b:a", bitrate));
        Assert.Equal(downmix, Args.After(args, "-ac"));
    }

    [Fact]
    public async Task M4a_is_aac_by_default_and_lossless_alac_when_asked()
    {
        using var h = new MediaHarness();
        var input = h.Audio("in.flac", "flac");
        Assert.True((await h.Run("convert:m4a", input)).Success);
        Assert.True(Args.HasPair(h.Runner.Calls[^1].Args, "-c:a", "aac"));

        var alac = await h.Run("convert:m4a", input, MediaHarness.Opts(("codec", "alac")));
        Assert.True(alac.Success, alac.Error);
        var args = h.Runner.Calls[^1].Args;
        Assert.True(Args.HasPair(args, "-c:a", "alac"));
        Assert.DoesNotContain("-b:a", args);

        var bad = await h.Run("convert:m4a", input, MediaHarness.Opts(("codec", "mp3")));
        Assert.False(bad.Success);
        Assert.Contains("codec", bad.Error);
    }

    // ------------------------------------------------------------------ DSD and other read-only sources

    [Theory]
    [InlineData("flac", "88200")]
    [InlineData("wav", "88200")]
    [InlineData("mp3", "44100")]
    [InlineData("opus", "48000")]
    public async Task Dsd_sources_are_resampled_to_a_rate_the_target_can_hold(string target, string rate)
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:" + target, h.Audio("album.dsf", "dsd_lsbf_planar", sampleRate: 352800));
        Assert.True(result.Success, result.Error);
        Assert.True(Args.HasPair(h.Runner.Calls.Single().Args, "-ar", rate));
    }

    [Theory]
    [InlineData("call.amr", "amrnb")]
    [InlineData("album.ape", "ape")]
    [InlineData("track.wv", "wavpack")]
    [InlineData("movie.dts", "dts")]
    [InlineData("voice.caf", "pcm_s16le")]
    public async Task Read_only_audio_sources_convert_with_the_usual_command(string file, string codec)
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:mp3", h.Audio(file, codec));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:a", "libmp3lame"));
        Assert.True(Args.HasPair(args, "-map", "0:a:0"));
        Assert.Null(Args.After(args, "-ar"));                                      // only DSD needs resampling
    }

    [Theory]
    [InlineData("recording.ts")]
    [InlineData("camcorder.mts")]
    [InlineData("disc.vob")]
    [InlineData("phone.3gp")]
    [InlineData("vcd.dat")]
    public async Task New_video_sources_convert_to_mp4(string file)
    {
        using var h = new MediaHarness();
        var result = await h.Run("convert:mp4", h.Video(file, seconds: 10));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "libx264"));
        Assert.EndsWith(".mp4", args[^1]);
    }

    // ------------------------------------------------------------------ animated WebP and APNG

    [Fact]
    public async Task Video_to_animated_webp_loops_forever_without_sound_and_at_most_480_wide()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersGpl + WebpEncoder;
        var result = await h.Run("convert:webp", h.Video("clip.mp4", seconds: 5));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "libwebp_anim"));
        Assert.True(Args.HasPair(args, "-loop", "0"));
        Assert.Contains("-an", args);
        Assert.Contains("scale='min(480,iw)':-1:flags=lanczos", Args.After(args, "-vf"));
        Assert.Contains("fps=15", Args.After(args, "-vf"));
        Assert.EndsWith("clip (converted).webp", args[^1]);
    }

    [Fact]
    public async Task Without_a_webp_encoder_the_failure_names_it_and_nothing_runs()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersMinimal;
        var result = await h.Run("convert:webp", h.Video("clip.mp4", seconds: 5));
        Assert.False(result.Success);
        Assert.Contains("libwebp", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Video_to_apng_uses_the_native_encoder_and_plays_forever()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersMinimal;
        var result = await h.Run("convert:apng", h.Video("clip.mkv", seconds: 5, fps: 10), MediaHarness.Opts(("width", 320)));
        Assert.True(result.Success, result.Error);
        var args = h.Runner.Calls.Single().Args;
        Assert.True(Args.HasPair(args, "-c:v", "apng"));
        Assert.True(Args.HasPair(args, "-plays", "0"));
        Assert.True(Args.HasPair(args, "-f", "apng"));
        Assert.Contains("fps=10", Args.After(args, "-vf"));                       // a slower source is not padded up to 15 fps
        Assert.Contains("min(320,iw)", Args.After(args, "-vf"));
        Assert.EndsWith(".apng", args[^1]);
    }

    [Fact]
    public async Task A_file_without_a_picture_cannot_become_an_animation()
    {
        using var h = new MediaHarness();
        h.Runner.EncodersOutput = TestMedia.EncodersGpl + WebpEncoder;
        var input = h.Input("sound.mkv", TestMedia.Audio("aac", path: "x"));
        var result = await h.Run("convert:webp", input);
        Assert.False(result.Success);
        Assert.Contains("no video", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
