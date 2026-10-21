using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Tests.Encoding;

public class FfprobeParserTests
{
    [Fact]
    public void Parses_an_mp4_with_video_audio_and_subtitle_streams()
    {
        var info = FfprobeParser.Parse(TestMedia.FfprobeMp4);

        Assert.Equal(3, info.Streams.Count);
        Assert.Equal(60.06, info.DurationSeconds!.Value, 3);
        Assert.Equal(37_700_000, info.SizeBytes);
        Assert.Equal(5_021_000, info.BitRate);

        var v = info.Video!;
        Assert.Equal("h264", v.CodecName);
        Assert.Equal("High", v.Profile);
        Assert.Equal(1920, v.Width);
        Assert.Equal(1080, v.Height);
        Assert.Equal("yuv420p", v.PixelFormat);
        Assert.Equal(30000.0 / 1001, v.FrameRate!.Value, 6);
        Assert.False(v.IsVariableFrameRate);
        Assert.Equal(4_800_000, v.BitRate);
        Assert.False(v.IsInterlaced);
        Assert.False(v.IsHdr);

        var a = info.Audio!;
        Assert.Equal("aac", a.CodecName);
        Assert.Equal(48000, a.SampleRate);
        Assert.Equal(2, a.Channels);
        Assert.Equal("stereo", a.ChannelLayout);
        Assert.Equal(128_000, a.BitRate);
        Assert.Equal("ara", a.Language);

        Assert.Single(info.SubtitleStreams);
        Assert.Equal("mov_text", info.SubtitleStreams.Single().CodecName);
    }

    [Fact]
    public void Reads_rotation_from_the_display_matrix_and_swaps_the_display_size()
    {
        var v = FfprobeParser.Parse(TestMedia.FfprobeMp4).Video!;
        Assert.Equal(270, v.Rotation); // -90 normalised
        Assert.Equal(1920, v.Width);   // coded size is untouched
        Assert.Equal(1080, v.DisplayWidth);
        Assert.Equal(1920, v.DisplayHeight);
    }

    [Fact]
    public void Keeps_unicode_tags_and_matches_tag_names_case_insensitively()
    {
        var info = FfprobeParser.Parse(TestMedia.FfprobeMp4);
        Assert.Equal("عطلة الصيف", info.Tags["title"]);
        Assert.Equal("موسى", info.Tags["artist"]); // stored as "ARTIST" in the file
        Assert.Equal(@"C:\Users\Moustafa\Videos\فيديو عائلي.mp4", info.Path);
    }

    [Fact]
    public void Matroska_without_container_duration_uses_the_DURATION_tag_and_flags_vfr_hdr_and_interlace()
    {
        var info = FfprobeParser.Parse(TestMedia.FfprobeMkv, path: "movie.mkv");

        Assert.Equal(150.501, info.DurationSeconds!.Value, 3); // longest stream
        var v = info.Video!;
        Assert.True(v.IsVariableFrameRate);                 // 60 vs 29.97
        Assert.Equal(30000.0 / 1001, v.FrameRate!.Value, 6); // average rate wins
        Assert.True(v.IsHdr);
        Assert.True(v.IsInterlaced);
        Assert.Equal(25_000_000, v.BitRate);                // from the BPS tag
        Assert.Equal(6, info.Audio!.Channels);
        Assert.Equal("movie.mkv", info.Path);
    }

    [Fact]
    public void Cover_art_is_not_the_video_stream()
    {
        var info = FfprobeParser.Parse(TestMedia.FfprobeMp3WithCover);
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.NotNull(info.CoverArt);
        Assert.Equal(215.24898, info.DurationSeconds!.Value, 5);
    }

    [Theory]
    [InlineData("30000/1001", 29.97002997)]
    [InlineData("25/1", 25.0)]
    [InlineData("24", 24.0)]
    public void Frame_rates_are_parsed(string text, double expected) =>
        Assert.Equal(expected, FfprobeParser.Rate(text)!.Value, 6);

    [Theory]
    [InlineData("0/0")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("30/0")]
    public void Invalid_frame_rates_are_null(string text) => Assert.Null(FfprobeParser.Rate(text));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("{\"streams\": [], \"format\": null}")]
    public void Garbage_throws_a_probe_exception_not_a_crash(string json)
    {
        Assert.Throws<MediaProbeException>(() => FfprobeParser.Parse(json));
    }

    [Fact]
    public void Numbers_given_as_json_numbers_are_accepted()
    {
        var info = FfprobeParser.Parse("""{"streams":[{"index":0,"codec_type":"audio","codec_name":"flac","sample_rate":44100,"channels":2,"duration":12.5}],"format":{"format_name":"flac","duration":12.5}}""");
        Assert.Equal(44100, info.Audio!.SampleRate);
        Assert.Equal(12.5, info.DurationSeconds);
    }
}
