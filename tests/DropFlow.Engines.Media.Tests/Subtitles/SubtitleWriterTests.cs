using System.Text;
using DropFlow.Core.Operations;
using TextEncoding = System.Text.Encoding;
using DropFlow.Engines.Media.Subtitles;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Media.Tests.Subtitles;

public class SubtitleWriterTests
{
    private static SubtitleDocument Doc(params (int startMs, int endMs, string text)[] cues) =>
        new(cues.Select(c => new SubtitleCue(TimeSpan.FromMilliseconds(c.startMs), TimeSpan.FromMilliseconds(c.endMs), c.text)).ToList(),
            SubtitleFormat.Srt, "\n", []);

    [Fact]
    public void Srt_output_is_numbered_with_comma_milliseconds_and_blank_separators()
    {
        var text = SubtitleWriter.Write(Doc((1000, 2500, "Hello world"), (3723004, 3724000, "Line A\nLine B")), SubtitleFormat.Srt);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,500\nHello world\n\n2\n01:02:03,004 --> 01:02:04,000\nLine A\nLine B\n\n", text);
    }

    [Fact]
    public void Vtt_output_has_a_header_dot_milliseconds_and_escapes_markup_characters()
    {
        var text = SubtitleWriter.Write(Doc((1000, 2500, "Tom & Jerry <i>5 < 6</i> --> end")), SubtitleFormat.Vtt);
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nTom &amp; Jerry <i>5 &lt; 6</i> --&gt; end\n\n", text);
    }

    [Fact]
    public void Txt_output_has_one_line_per_cue_without_tags()
    {
        var text = SubtitleWriter.Write(Doc((0, 1000, "<i>Hello</i>\nworld"), (1000, 2000, "<b>Bye</b>")), SubtitleFormat.Txt);
        Assert.Equal("Hello world\nBye\n", text);
    }

    [Fact]
    public void Txt_can_collapse_consecutive_repeats_but_only_when_asked()
    {
        var doc = Doc((0, 1000, "Same"), (1000, 2000, "Same"), (2000, 3000, "Different"), (3000, 4000, "Same"));
        Assert.Equal("Same\nSame\nDifferent\nSame\n", SubtitleWriter.Write(doc, SubtitleFormat.Txt));
        Assert.Equal("Same\nDifferent\nSame\n", SubtitleWriter.Write(doc, SubtitleFormat.Txt, new SubtitleWriteOptions { CollapseRepeats = true }));
    }

    [Fact]
    public void Ass_output_has_a_valid_skeleton_centisecond_times_and_inline_tags()
    {
        var text = SubtitleWriter.Write(Doc((1000, 2500, "<i>Hi</i> there\nSecond {x}")), SubtitleFormat.Ass);
        Assert.StartsWith("[Script Info]\n", text);
        Assert.Contains("\n[V4+ Styles]\n", text);
        Assert.Contains("\n[Events]\n", text);
        Assert.Contains("Style: Default,Arial,", text);
        Assert.Contains(@"Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\i1}Hi{\i0} there\NSecond (x)", text);
    }

    [Theory]
    [InlineData(0, "0:00:00.00")]
    [InlineData(1234, "0:00:01.23")]
    [InlineData(1235, "0:00:01.24")]   // half-centisecond rounds up
    [InlineData(1995, "0:00:02.00")]   // carries into the next second
    [InlineData(3723450, "1:02:03.45")]
    public void Ass_clock_rounds_to_centiseconds(int ms, string expected)
    {
        Assert.Equal(expected, SubtitleWriter.AssClock(TimeSpan.FromMilliseconds(ms)));
    }

    [Fact]
    public void Clock_never_goes_negative_and_grows_past_99_hours()
    {
        Assert.Equal("00:00:00,000", SubtitleWriter.Clock(TimeSpan.FromMilliseconds(-5), ','));
        Assert.Equal("100:00:01.500", SubtitleWriter.Clock(TimeSpan.FromMilliseconds(360001500), '.'));
    }

    [Fact]
    public void Newlines_follow_the_source_unless_overridden()
    {
        var crlf = Doc((0, 1000, "A\nB")) with { NewLine = "\r\n" };
        Assert.Equal("1\r\n00:00:00,000 --> 00:00:01,000\r\nA\r\nB\r\n\r\n", SubtitleWriter.Write(crlf, SubtitleFormat.Srt));
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\nA\nB\n\n", SubtitleWriter.Write(crlf, SubtitleFormat.Srt, new SubtitleWriteOptions { NewLine = "\n" }));
    }

    [Fact]
    public void Blank_lines_inside_a_cue_are_collapsed_so_they_cannot_end_the_cue_early()
    {
        var text = SubtitleWriter.Write(Doc((0, 1000, "A\n\n\nB")), SubtitleFormat.Srt);
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\nA\nB\n\n", text);
    }

    [Fact]
    public void Bom_defaults_are_yes_for_srt_ass_txt_and_no_for_vtt()
    {
        byte[] Bytes(SubtitleFormat f, SubtitleWriteOptions? o = null) => SubtitleWriter.ToBytes("x", f, o);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x'], Bytes(SubtitleFormat.Srt));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x'], Bytes(SubtitleFormat.Ass));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x'], Bytes(SubtitleFormat.Txt));
        Assert.Equal([(byte)'x'], Bytes(SubtitleFormat.Vtt));
        Assert.Equal([(byte)'x'], Bytes(SubtitleFormat.Srt, new SubtitleWriteOptions { Bom = false }));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x'], Bytes(SubtitleFormat.Vtt, new SubtitleWriteOptions { Bom = true }));
    }

    [Fact]
    public void Arabic_text_survives_every_target_format()
    {
        var doc = Doc((1000, 2000, "<i>مرحبًا</i> بالعالم، كيف حالك؟"));
        foreach (var target in new[] { SubtitleFormat.Srt, SubtitleFormat.Vtt, SubtitleFormat.Ass, SubtitleFormat.Txt })
        {
            var bytes = SubtitleWriter.ToBytes(SubtitleWriter.Write(doc, target), target);
            var text = SubtitleDecoder.Decode(bytes, out _);
            Assert.Contains("مرحبًا", text);
            Assert.Contains("بالعالم، كيف حالك؟", text);
        }
    }

    // ------------------------------------------------------------------ round trips: timing is preserved exactly

    private const string SrtSource =
        "1\n00:00:01,000 --> 00:00:02,500\nHello <i>world</i>\n\n2\n00:01:03,007 --> 00:01:05,999\nمرحبا\nثاني\n\n3\n01:00:00,001 --> 01:00:00,002\nEdge\n";

    [Fact]
    public void Srt_to_vtt_to_srt_keeps_every_millisecond_and_every_word()
    {
        var original = SubtitleParser.Parse(SrtSource, SubtitleFormat.Srt);
        var vtt = SubtitleWriter.Write(original, SubtitleFormat.Vtt);
        var back = SubtitleParser.Parse(vtt, SubtitleFormat.Vtt);

        Assert.Equal(original.Cues.Select(c => (c.Start, c.End, c.Text)), back.Cues.Select(c => (c.Start, c.End, c.Text)));
        var srt = SubtitleWriter.Write(back, SubtitleFormat.Srt);
        Assert.Equal(SubtitleWriter.Write(original, SubtitleFormat.Srt), srt);
    }

    [Fact]
    public void Srt_to_ass_to_srt_keeps_text_and_rounds_time_only_to_centiseconds()
    {
        var original = SubtitleParser.Parse(SrtSource, SubtitleFormat.Srt);
        var ass = SubtitleWriter.Write(original, SubtitleFormat.Ass);
        var back = SubtitleParser.Parse(ass, SubtitleFormat.Ass);

        Assert.Equal(original.Cues.Select(c => c.Text), back.Cues.Select(c => c.Text));
        for (var i = 0; i < original.Cues.Count; i++)
        {
            Assert.InRange(Math.Abs((back.Cues[i].Start - original.Cues[i].Start).TotalMilliseconds), 0, 5);
            Assert.InRange(Math.Abs((back.Cues[i].End - original.Cues[i].End).TotalMilliseconds), 0, 5);
        }
        // Times that are already whole centiseconds survive unchanged.
        Assert.Equal(TimeSpan.FromMilliseconds(1000), back.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), back.Cues[0].End);
    }

    [Fact]
    public void Ass_to_srt_text_and_times_match_the_source()
    {
        const string ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                           + "Dialogue: 0,0:00:01.50,0:00:03.25,Default,,0,0,0,,{\\an8}Line one\\NLine two\n";
        var srt = SubtitleWriter.Write(SubtitleParser.Parse(ass, SubtitleFormat.Ass), SubtitleFormat.Srt);
        Assert.Equal("1\n00:00:01,500 --> 00:00:03,250\nLine one\nLine two\n\n", srt);
    }

    [Fact]
    public void Vtt_with_settings_voices_and_notes_converts_to_clean_srt()
    {
        const string vtt = "WEBVTT\n\nNOTE hi\n\nid1\n00:00:01.000 --> 00:00:02.000 align:start line:0\n<v Roger>Hello &amp; welcome</v>\n";
        var srt = SubtitleWriter.Write(SubtitleParser.Parse(vtt, SubtitleFormat.Vtt), SubtitleFormat.Srt);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\nHello & welcome\n\n", srt);
    }

    [Fact]
    public void Text_with_a_cue_end_arrow_cannot_break_a_round_trip_through_vtt()
    {
        var original = Doc((0, 1000, "a --> b"));
        var back = SubtitleParser.Parse(SubtitleWriter.Write(original, SubtitleFormat.Vtt), SubtitleFormat.Vtt);
        Assert.Equal("a --> b", back.Cues.Single().Text);
    }
}

public class SubtitleConverterTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();
    private readonly SubtitleConverter _converter = new();

    [Theory]
    [InlineData("convert:vtt", "srt", true)]
    [InlineData("convert:ass", "srt", true)]
    [InlineData("convert:txt", "srt", true)]
    [InlineData("convert:srt", "vtt", true)]
    [InlineData("convert:ass", "vtt", true)]
    [InlineData("convert:txt", "vtt", true)]
    [InlineData("convert:srt", "ass", true)]
    [InlineData("convert:vtt", "ass", true)]
    [InlineData("convert:txt", "ass", true)]
    [InlineData("convert:srt", "srt", false)]   // never same to same
    [InlineData("convert:vtt", "vtt", false)]
    [InlineData("convert:ass", "ass", false)]
    [InlineData("convert:srt", "txt", false)]   // txt is output only
    [InlineData("convert:txt", "txt", false)]
    [InlineData("convert:png", "srt", false)]
    [InlineData("convert:srt", "mp4", false)]
    [InlineData("vid.compress", "srt", false)]
    public void Supports_is_truthful(string operation, string inputExtension, bool expected)
    {
        Assert.Equal(expected, _converter.Supports(operation, inputExtension));
    }

    [Fact]
    public void Supported_matrix_is_exactly_three_inputs_by_four_outputs_minus_identity()
    {
        var pairs = from op in _converter.Operations
                    from ext in new[] { "srt", "vtt", "ass", "txt", "mp3", "png" }
                    where _converter.Supports(op, ext)
                    select (op, ext);
        Assert.Equal(9, pairs.Count()); // srt->vtt,ass,txt  vtt->srt,ass,txt  ass->srt,vtt,txt
        Assert.Equal(4, _converter.Operations.Count);
        Assert.Equal(EngineId.None, _converter.EngineFor("convert:srt"));
        Assert.Empty(((IConverter)_converter).OptionsFor("convert:srt"));
    }

    [Fact]
    public async Task Converts_a_file_with_an_arabic_name_in_a_folder_with_spaces_and_leaves_the_original_alone()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "مجلد الترجمات 2026");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "فيلم عائلي.srt");
        const string source = "1\n00:00:01,000 --> 00:00:02,500\nمرحبا <i>بالعالم</i>\n";
        await File.WriteAllTextAsync(input, source, new UTF8Encoding(false));
        var before = await File.ReadAllBytesAsync(input);

        var result = await _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(folder, "فيلم عائلي (converted).vtt"), output);
        var written = await File.ReadAllBytesAsync(output);
        Assert.NotEqual(0xEF, written[0]); // VTT is written without a BOM
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nمرحبا <i>بالعالم</i>\n\n", TextEncoding.UTF8.GetString(written));
        Assert.Equal(before, await File.ReadAllBytesAsync(input)); // original untouched
    }

    [Fact]
    public async Task Srt_output_gets_a_utf8_bom_for_older_players()
    {
        using var dir = new TempDir();
        var input = Samples.Srt(Path.Combine(dir.Path, "a.srt"));
        var vtt = await _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, CancellationToken.None);
        var back = await _converter.RunAsync(new ConversionJob([vtt.Outputs[0]], "convert:srt"), NoProgress, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(back.Outputs[0]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_output()
    {
        using var dir = new TempDir();
        var input = Samples.Srt(Path.Combine(dir.Path, "talk.srt"));
        var existing = Path.Combine(dir.Path, "talk (converted).vtt");
        await File.WriteAllTextAsync(existing, "precious");

        var result = await _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(dir.Path, "talk (converted 2).vtt"), result.Outputs.Single());
        Assert.Equal("precious", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Honours_the_output_directory()
    {
        using var dir = new TempDir();
        var input = Samples.Srt(Path.Combine(dir.Path, "a.srt"));
        var outDir = Path.Combine(dir.Path, "out", "nested");

        var result = await _converter.RunAsync(new ConversionJob([input], "convert:txt", OutputDirectory: outDir), NoProgress, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(outDir, "a (converted).txt"), result.Outputs.Single());
        Assert.Equal("Hello world\nSecond line\n", (await File.ReadAllTextAsync(result.Outputs[0])).TrimStart('\uFEFF'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not a subtitle file\njust prose")]
    [InlineData("1\n00:00:01,000 -> \n")]
    public async Task Garbage_and_empty_files_fail_with_a_message_and_produce_nothing(string content)
    {
        using var dir = new TempDir();
        var input = dir.WriteText("bad.srt", content);

        var result = await _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Outputs);
        Assert.Single(Directory.GetFiles(dir.Path)); // only the input
    }

    [Fact]
    public async Task Binary_files_fail_cleanly()
    {
        using var dir = new TempDir();
        var input = Path.Combine(dir.Path, "fake.srt");
        await File.WriteAllBytesAsync(input, [0x50, 0x4B, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0]);

        var result = await _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("binary", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_input_is_a_failure_not_an_exception()
    {
        var result = await _converter.RunAsync(new ConversionJob([@"C:\definitely\missing\nope.srt"], "convert:vtt"), NoProgress, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task An_unsupported_pair_is_refused()
    {
        using var dir = new TempDir();
        var input = Samples.Srt(Path.Combine(dir.Path, "a.srt"));
        var result = await _converter.RunAsync(new ConversionJob([input], "convert:srt"), NoProgress, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Cancellation_throws_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var input = Samples.Srt(Path.Combine(dir.Path, "a.srt"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, cts.Token));

        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Content_that_does_not_match_the_extension_is_still_converted()
    {
        using var dir = new TempDir();
        var input = dir.WriteText("actually-srt.vtt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var result = await _converter.RunAsync(new ConversionJob([input], "convert:srt"), NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("Hi", await File.ReadAllTextAsync(result.Outputs[0]));
    }

    [Fact]
    public async Task Legacy_windows_1256_files_are_converted_to_utf8()
    {
        TextEncoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var dir = new TempDir();
        const string arabic = "1\n00:00:01,000 --> 00:00:02,000\nمرحبا بالعالم كيف حالك\n";
        var input = Path.Combine(dir.Path, "legacy.srt");
        await File.WriteAllBytesAsync(input, TextEncoding.GetEncoding(1256).GetBytes(arabic));

        var result = await _converter.RunAsync(new ConversionJob([input], "convert:vtt"), NoProgress, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("مرحبا بالعالم كيف حالك", await File.ReadAllTextAsync(result.Outputs[0], TextEncoding.UTF8));
    }
}
