using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.Engines.Media.Tests.Preview;

public class PreviewArgsTests
{
    [Fact]
    public void A_frame_is_seeked_on_the_input_and_piped_out_as_one_png()
    {
        var a = PreviewArgs.Frame(@"C:\v\ملف.mp4", 12.5, 90);
        Assert.True(Args.HasPair(a, "-ss", "12.5"));
        Assert.True(Args.IndexOf(a, "-ss") < Args.IndexOf(a, "-i"));     // input seeking: fast on long files
        Assert.True(Args.HasPair(a, "-i", @"C:\v\ملف.mp4"));
        Assert.True(Args.HasPair(a, "-frames:v", "1"));
        Assert.True(Args.HasPair(a, "-f", "image2pipe"));
        Assert.True(Args.HasPair(a, "-vcodec", "png"));
        Assert.EndsWith("scale=-2:90", Args.After(a, "-vf"));
        Assert.StartsWith("scale=trunc(ih*dar/2)*2:ih,setsar=1", Args.After(a, "-vf"));   // square pixels first, so anamorphic video is not squeezed
        Assert.Equal("-", a[^1]);
        Assert.DoesNotContain("-y", a);
    }

    [Fact]
    public void Frame_heights_are_even_and_times_never_negative()
    {
        var a = PreviewArgs.Frame("in.mkv", -3, 91);
        Assert.True(Args.HasPair(a, "-ss", "0"));
        Assert.EndsWith("scale=-2:90", Args.After(a, "-vf"));
    }

    [Fact]
    public void The_waveform_is_decoded_to_mono_float_samples_on_stdout()
    {
        var a = PreviewArgs.Waveform("song.ogg");
        Assert.True(Args.HasPair(a, "-ac", "1"));
        Assert.True(Args.HasPair(a, "-ar", PreviewArgs.WaveformRate.ToString()));
        Assert.True(Args.HasPair(a, "-f", "f32le"));
        Assert.True(Args.HasPair(a, "-map", "0:a:0"));
        Assert.Equal("-", a[^1]);
    }

    [Fact]
    public void A_snippet_reads_only_its_window_and_writes_aac()
    {
        var a = PreviewArgs.AudioSnippet("song.flac", 10, 5, "volume=2", @"C:\t\p.m4a");
        Assert.True(Args.HasPair(a, "-ss", "10"));
        Assert.True(Args.HasPair(a, "-t", "5"));
        Assert.True(Args.IndexOf(a, "-t") < Args.IndexOf(a, "-i"));
        Assert.True(Args.HasPair(a, "-af", "volume=2"));
        Assert.True(Args.HasPair(a, "-c:a", "aac"));
        Assert.True(Args.HasPair(a, "-vn", "-sn") || a.Contains("-vn"));
        Assert.Contains("-n", a);   // never overwrite
        Assert.Equal(@"C:\t\p.m4a", a[^1]);
    }

    [Fact]
    public void A_whole_file_snippet_has_no_window_and_no_filter()
    {
        var a = PreviewArgs.AudioSnippet("song.opus", 0, null, null, "p.m4a");
        Assert.DoesNotContain("-ss", a);
        Assert.DoesNotContain("-t", a);
        Assert.DoesNotContain("-af", a);
    }

    [Fact]
    public void Thumbnail_times_sit_in_the_middle_of_equal_slots()
    {
        Assert.Equal([5.0, 15.0, 25.0, 35.0], PreviewArgs.ThumbnailTimes(40, 4));
        Assert.Empty(PreviewArgs.ThumbnailTimes(0, 4));
        Assert.Empty(PreviewArgs.ThumbnailTimes(10, 0));
    }

    [Fact]
    public void A_bleep_preview_shifts_the_ranges_into_its_window_and_clips_them()
    {
        // window 10..20; range 8-12 becomes 0-2, 15-16 becomes 5-6, 25-30 is outside
        var f = PreviewArgs.BleepWindowFilter([new(8, 12), new(15, 16), new(25, 30)], 2, 1000, 0.35, 10, 10);
        Assert.NotNull(f);
        Assert.Contains("between(t,0,2)", f);
        Assert.Contains("between(t,5,6)", f);
        Assert.DoesNotContain("25", f);
        Assert.StartsWith("aeval=", f);
    }

    [Fact]
    public void A_bleep_preview_with_no_range_in_its_window_plays_the_original()
    {
        Assert.Null(PreviewArgs.BleepWindowFilter([new(30, 31)], 2, 1000, 0.35, 0, 10));
    }

    [Fact]
    public void Surround_bleep_previews_are_brought_to_stereo_first()
    {
        Assert.StartsWith("aformat=channel_layouts=stereo,aeval=", PreviewArgs.BleepWindowFilter([new(1, 2)], 6, 1000, 0.35, 0, 10));
    }

    [Theory]
    [InlineData("left", 2, "pan=stereo|c0=c0|c1=c0")]
    [InlineData("right", 2, "pan=stereo|c0=c1|c1=c1")]
    [InlineData("left", 1, null)]      // mono has no sides
    [InlineData("both", 2, null)]
    public void Solo_previews_put_one_side_in_both_ears(string listen, int channels, string? expected)
    {
        Assert.Equal(expected, PreviewArgs.SoloFilter(listen, channels));
    }
}

public class WaveformPeaksTests
{
    private static byte[] Floats(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Peaks_are_the_loudest_absolute_sample_of_each_bar()
    {
        var sink = new WaveformSink(blockSamples: 2);
        sink.Write(Floats(0.1f, -0.5f, 0.2f, 0.3f, -0.9f, 0.0f, 0.05f, 0.05f));
        var bars = sink.Reduce(4);
        Assert.Equal([0.5f, 0.3f, 0.9f, 0.05f], bars);
    }

    [Fact]
    public void Samples_split_across_writes_are_reassembled()
    {
        var bytes = Floats(0.25f, -0.75f, 0.5f, 0.1f);
        var sink = new WaveformSink(blockSamples: 2);
        sink.Write(bytes, 0, 3);           // the first float is cut in the middle
        sink.Write(bytes, 3, 6);
        sink.Write(bytes, 9, bytes.Length - 9);
        Assert.Equal([0.75f, 0.5f], sink.Reduce(2));
    }

    [Fact]
    public void More_bars_than_blocks_repeat_the_nearest_block_and_values_stay_within_one()
    {
        var sink = new WaveformSink(blockSamples: 1);
        sink.Write(Floats(2.5f, 0.5f));
        Assert.Equal([1f, 1f, 0.5f, 0.5f], sink.Reduce(4));
    }

    [Fact]
    public void Nothing_decoded_gives_flat_bars_and_bad_samples_count_as_silence()
    {
        Assert.Equal([0f, 0f, 0f], new WaveformSink().Reduce(3));
        var sink = new WaveformSink(blockSamples: 1);
        sink.Write(Floats(float.NaN, 0.5f));
        Assert.Equal([0f, 0.5f], sink.Reduce(2));
    }
}
