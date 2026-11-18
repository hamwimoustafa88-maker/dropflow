using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Subtitles;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Media.Tests.Subtitles;

/// <summary>SAMI, LRC, TTML/DFXP, MicroDVD and YouTube SBV: reading (from the sample files in tests/samples/subtitles) and writing.</summary>
public class SubtitleFormatsTests
{
    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "subtitles", name);

    private static SubtitleDocument Sample(string name) =>
        SubtitleParser.ParseBytes(File.ReadAllBytes(SamplePath(name)), SubtitleParser.FormatFromExtension(Path.GetExtension(name).TrimStart('.'))!.Value);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static void AssertCue(SubtitleCue cue, int startMs, int endMs, string text)
    {
        Assert.Equal(Ms(startMs), cue.Start);
        Assert.Equal(Ms(endMs), cue.End);
        Assert.Equal(text, cue.Text);
    }

    [Theory]
    [InlineData("smi", SubtitleFormat.Smi)]
    [InlineData("sami", SubtitleFormat.Smi)]
    [InlineData("lrc", SubtitleFormat.Lrc)]
    [InlineData("ttml", SubtitleFormat.Ttml)]
    [InlineData("dfxp", SubtitleFormat.Ttml)]
    [InlineData("sub", SubtitleFormat.MicroDvd)]
    [InlineData("sbv", SubtitleFormat.Sbv)]
    public void New_extensions_map_to_their_formats(string ext, SubtitleFormat format) =>
        Assert.Equal(format, SubtitleParser.FormatFromExtension(ext));

    // ------------------------------------------------------------------ SAMI

    [Fact]
    public void Sami_sample_reads_the_first_language_with_breaks_entities_and_clears()
    {
        var doc = Sample("sample.smi");
        Assert.Equal(SubtitleFormat.Smi, doc.SourceFormat);
        Assert.Equal(2, doc.Cues.Count);
        AssertCue(doc.Cues[0], 1000, 3500, "Hello <i>world</i>\nsecond line");   // source line breaks are spaces, <br> is a break
        AssertCue(doc.Cues[1], 4000, 6000, "Fish & chips");                       // <font> dropped, &amp; decoded
        Assert.Contains(doc.Warnings, w => w.Contains("ARSACC", StringComparison.Ordinal)); // the other language is named
    }

    [Fact]
    public void Sami_without_classes_or_a_final_clear_still_gets_an_end_time()
    {
        var doc = SubtitleParser.Parse("<SAMI><BODY><SYNC Start=500><P>One<SYNC Start=2000><P>مرحبا</BODY></SAMI>", SubtitleFormat.Smi);
        Assert.Equal(2, doc.Cues.Count);
        AssertCue(doc.Cues[0], 500, 2000, "One");
        Assert.Equal(Ms(2000), doc.Cues[1].Start);
        Assert.True(doc.Cues[1].End > doc.Cues[1].Start);
        Assert.Equal("مرحبا", doc.Cues[1].Text);
    }

    // ------------------------------------------------------------------ LRC

    [Fact]
    public void Lrc_sample_ends_lines_at_the_next_time_tag_and_repeats_multi_tag_lines()
    {
        var doc = Sample("sample.lrc");
        Assert.Equal(SubtitleFormat.Lrc, doc.SourceFormat);
        Assert.Equal("DropFlow sample", doc.Title);
        Assert.Equal(4, doc.Cues.Count);
        AssertCue(doc.Cues[0], 1000, 3500, "Hello world");
        AssertCue(doc.Cues[1], 4000, 6000, "Chorus مرحبا");
        AssertCue(doc.Cues[2], 10000, 12000, "Chorus مرحبا");     // [00:04.00][00:10.00]: the same line twice
        Assert.Equal(Ms(12000), doc.Cues[3].Start);
        Assert.Equal("Last line", doc.Cues[3].Text);              // enhanced word times removed
        Assert.True(doc.Cues[3].End > doc.Cues[3].Start);
    }

    [Fact]
    public void Lrc_offset_moves_every_line_earlier_or_later()
    {
        var earlier = SubtitleParser.Parse("[offset:+500]\n[00:02.00]A\n[00:04.00]B\n", SubtitleFormat.Lrc);
        AssertCue(earlier.Cues[0], 1500, 3500, "A");
        var later = SubtitleParser.Parse("[offset:-250]\n[00:02.00]A\n[00:04.00]B\n", SubtitleFormat.Lrc);
        AssertCue(later.Cues[0], 2250, 4250, "A");
        var clamped = SubtitleParser.Parse("[offset:5000]\n[00:01.00]A\n[00:03.00]B\n", SubtitleFormat.Lrc);
        Assert.Equal(TimeSpan.Zero, clamped.Cues[0].Start);
    }

    [Fact]
    public void Lrc_accepts_minutes_past_99_and_colon_or_no_fractions()
    {
        var doc = SubtitleParser.Parse("[123:04.5]X\n[123:06]Y\n[123:08:25]Z\n", SubtitleFormat.Lrc);
        Assert.Equal(TimeSpan.FromMinutes(123) + TimeSpan.FromSeconds(4.5), doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(123) + TimeSpan.FromSeconds(6), doc.Cues[1].Start);
        Assert.Equal(TimeSpan.FromMinutes(123) + TimeSpan.FromSeconds(8.25), doc.Cues[2].Start);
    }

    // ------------------------------------------------------------------ TTML / DFXP

    [Fact]
    public void Ttml_sample_reads_clock_tick_and_frame_times_and_styles()
    {
        var doc = Sample("sample.ttml");
        Assert.Equal(SubtitleFormat.Ttml, doc.SourceFormat);
        Assert.Equal(3, doc.Cues.Count);
        AssertCue(doc.Cues[0], 1000, 3500, "Hello <b>world</b>\nsecond line");
        AssertCue(doc.Cues[1], 4000, 6000, "<i>مرحبا بالعالم</i>");     // 40000000 ticks at 10 MHz, dur 2s, style from <head>
        AssertCue(doc.Cues[2], 7480, 9000, "Frames & ticks");           // 00:00:07:12 at 25 fps
    }

    [Fact]
    public void Dfxp_times_are_relative_to_the_enclosing_div()
    {
        var doc = Sample("sample.dfxp");
        Assert.Equal(2, doc.Cues.Count);
        AssertCue(doc.Cues[0], 10000, 11500, "First, inside a div that starts at ten seconds");
        AssertCue(doc.Cues[1], 12000, 14000, "<i>مرحبا</i> again");
    }

    [Fact]
    public void Ttml_with_a_doctype_is_read_without_resolving_anything_and_broken_xml_fails_clearly()
    {
        const string withDoctype = """
            <?xml version="1.0"?>
            <!DOCTYPE tt [ <!ENTITY e SYSTEM "file:///c:/windows/win.ini"> ]>
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="1s" end="2s">ok &amp; safe</p></div></body></tt>
            """;
        var doc = SubtitleParser.Parse(withDoctype, SubtitleFormat.Ttml);
        AssertCue(Assert.Single(doc.Cues), 1000, 2000, "ok & safe");

        var broken = Assert.Throws<SubtitleFormatException>(() => SubtitleParser.Parse("<tt><body><p begin=\"1s\">x</body>", SubtitleFormat.Ttml));
        Assert.Contains("TTML", broken.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01:02:03.5", 3723500)]
    [InlineData("00:00:01,250", 1250)]
    [InlineData("1.5s", 1500)]
    [InlineData("2m", 120000)]
    [InlineData("1h", 3600000)]
    [InlineData("750ms", 750)]
    [InlineData("50f", 2000)]          // 25 fps
    [InlineData("00:00:02:05", 2200)]  // 2 s + 5 frames at 25 fps
    [InlineData("5000000t", 500)]      // tick rate 10 MHz
    public void Ttml_time_expressions(string expression, int expectedMs)
    {
        var timing = new TtmlTiming(FrameRate: 25, TickRate: 10_000_000);
        Assert.Equal(Ms(expectedMs), timing.Parse(expression));
    }

    // ------------------------------------------------------------------ MicroDVD

    [Fact]
    public void MicroDvd_sample_uses_the_declared_frame_rate_and_its_style_codes()
    {
        var doc = Sample("sample.sub");
        Assert.Equal(SubtitleFormat.MicroDvd, doc.SourceFormat);
        Assert.Equal(4, doc.Cues.Count);                         // the {1}{1}25 line declares the rate, it is not a cue
        AssertCue(doc.Cues[0], 1000, 3000, "Hello world\nsecond line");
        AssertCue(doc.Cues[1], 4000, 6000, "<i>مرحبا بالعالم</i>");
        AssertCue(doc.Cues[2], 7000, 9000, "<b>Bold</b>\n<b>both lines</b>");
        AssertCue(doc.Cues[3], 10000, 12000, "<i>Slash italic</i>\nplain");
    }

    [Fact]
    public void MicroDvd_without_a_rate_line_assumes_23_976_fps()
    {
        var doc = SubtitleParser.Parse("{24}{48}One\n{1000}{}Two\n", SubtitleFormat.MicroDvd);
        AssertCue(doc.Cues[0], 1001, 2002, "One");               // 24 / 23.976 = 1.001 s
        Assert.Equal(Ms(41708), doc.Cues[1].Start);              // 1000 / 23.976 = 41.708 s
        Assert.True(doc.Cues[1].End > doc.Cues[1].Start);        // an empty end frame still gets a duration
        Assert.Contains(doc.Warnings, w => w.Contains("23.976", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sub_file_in_subviewer_form_is_read_too()
    {
        var doc = SubtitleParser.Parse("[INFORMATION]\n[TITLE]x\n[END INFORMATION]\n00:00:01.00,00:00:02.50\nHello[br]there\n\n", SubtitleFormat.MicroDvd);
        AssertCue(Assert.Single(doc.Cues), 1000, 2500, "Hello\nthere");
    }

    [Fact]
    public void A_vobsub_picture_file_is_refused_with_a_message_that_says_why()
    {
        byte[] mpegPs = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
        var ex = Assert.Throws<SubtitleFormatException>(() => SubtitleParser.ParseBytes(mpegPs, SubtitleFormat.MicroDvd));
        Assert.Contains("VobSub", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ SBV

    [Fact]
    public void Sbv_sample_reads_youtube_times()
    {
        var doc = Sample("sample.sbv");
        Assert.Equal(SubtitleFormat.Sbv, doc.SourceFormat);
        Assert.Equal(3, doc.Cues.Count);
        AssertCue(doc.Cues[0], 1000, 3500, "Hello world\nsecond line");
        AssertCue(doc.Cues[1], 4000, 6250, "مرحبا بالعالم");
        AssertCue(doc.Cues[2], 62500, 64000, "Fish & chips");
    }

    // ------------------------------------------------------------------ writers

    private static SubtitleDocument Doc(params SubtitleCue[] cues) => new(cues, SubtitleFormat.Srt, "\n", []);

    [Fact]
    public void Lrc_output_writes_one_line_per_cue_and_a_clear_tag_where_a_gap_follows()
    {
        var doc = Doc(
            new SubtitleCue(Ms(1000), Ms(3000), "Hello <i>world</i>\nagain"),
            new SubtitleCue(Ms(3000), Ms(4000), "مرحبا"),
            new SubtitleCue(Ms(125_500), Ms(127_000), "late"));
        var text = SubtitleWriter.Write(doc, SubtitleFormat.Lrc);
        Assert.Equal("[00:01.00]Hello world again\n[00:03.00]مرحبا\n[00:04.00]\n[02:05.50]late\n[02:07.00]\n", text);

        // and it reads back to the same cues
        var back = SubtitleParser.Parse(text, SubtitleFormat.Lrc);
        Assert.Equal(3, back.Cues.Count);
        AssertCue(back.Cues[1], 3000, 4000, "مرحبا");
        AssertCue(back.Cues[2], 125_500, 127_000, "late");
    }

    [Fact]
    public void Sbv_output_round_trips_through_the_reader()
    {
        var doc = Doc(new SubtitleCue(Ms(1000), Ms(2500), "<b>Two</b>\nlines"), new SubtitleCue(Ms(3_723_004), Ms(3_725_000), "مرحبا"));
        var text = SubtitleWriter.Write(doc, SubtitleFormat.Sbv);
        Assert.Equal("0:00:01.000,0:00:02.500\nTwo\nlines\n\n1:02:03.004,1:02:05.000\nمرحبا\n\n", text);
        var back = SubtitleParser.Parse(text, SubtitleFormat.Sbv);
        AssertCue(back.Cues[0], 1000, 2500, "Two\nlines");
        AssertCue(back.Cues[1], 3_723_004, 3_725_000, "مرحبا");
    }

    [Theory]
    [InlineData("sample.smi")]
    [InlineData("sample.lrc")]
    [InlineData("sample.ttml")]
    [InlineData("sample.dfxp")]
    [InlineData("sample.sub")]
    [InlineData("sample.sbv")]
    [InlineData("arabic-cp1256.srt")]
    public async Task Every_sample_converts_to_every_output_as_utf8(string name)
    {
        using var dir = new TempDir();
        var input = Path.Combine(dir.Path, "ملف ترجمة " + name);
        File.Copy(SamplePath(name), input);
        var converter = new SubtitleConverter();
        var ext = Path.GetExtension(name).TrimStart('.');

        foreach (var target in new[] { "srt", "vtt", "ass", "txt", "lrc", "sbv" }.Where(t => t != ext))
        {
            var result = await converter.RunAsync(new ConversionJob([input], "convert:" + target), new Progress<JobProgress>(), default);
            Assert.True(result.Success, $"{name} -> {target}: {result.Error}");
            var bytes = File.ReadAllBytes(result.Outputs.Single());
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); // throws if not UTF-8
            if (target != "txt") Assert.NotEmpty(SubtitleParser.Parse(text, SubtitleParser.FormatFromExtension(target)!.Value).Cues);
            if (name.Contains("cp1256", StringComparison.Ordinal)) Assert.Contains("شكراً لكم", text, StringComparison.Ordinal);
        }
        Assert.Equal(File.ReadAllBytes(SamplePath(name)), File.ReadAllBytes(input)); // the original is untouched
    }

    [Fact]
    public void Supports_every_new_input_for_every_output_but_never_itself()
    {
        var converter = new SubtitleConverter();
        string[] inputs = ["srt", "vtt", "ass", "smi", "lrc", "ttml", "dfxp", "sub", "sbv"];
        string[] outputs = ["srt", "vtt", "ass", "txt", "lrc", "sbv"];
        foreach (var i in inputs)
            foreach (var o in outputs)
                Assert.Equal(i != o, converter.Supports("convert:" + o, i));
        Assert.False(converter.Supports("convert:smi", "srt"));     // the new reading formats are input only
        Assert.False(converter.Supports("convert:ttml", "srt"));
        Assert.False(converter.Supports("convert:sub", "srt"));
    }
}
