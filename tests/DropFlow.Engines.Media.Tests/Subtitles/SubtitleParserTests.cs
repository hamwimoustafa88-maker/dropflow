using System.Text;
using DropFlow.Engines.Media.Subtitles;
using TextEncoding = System.Text.Encoding;

namespace DropFlow.Engines.Media.Tests.Subtitles;

public class SrtParserTests
{
    private static SubtitleDocument Parse(string text) => SubtitleParser.Parse(text, SubtitleFormat.Srt);

    [Fact]
    public void Parses_a_plain_file_with_exact_times()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,500\nHello world\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond line\n");
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), doc.Cues[0].End);
        Assert.Equal("Hello world", doc.Cues[0].Text);
        Assert.Equal("1", doc.Cues[0].Identifier);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), doc.Cues[1].End);
        Assert.Empty(doc.Warnings);
        Assert.Equal(SubtitleFormat.Srt, doc.SourceFormat);
    }

    [Fact]
    public void Arabic_text_and_multiple_lines_are_preserved()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:03,000\nمرحبًا بالعالم\nهذا سطر ثانٍ\n");
        Assert.Equal("مرحبًا بالعالم\nهذا سطر ثانٍ", doc.Cues[0].Text);
    }

    [Fact]
    public void Crlf_and_lone_cr_line_endings_work_and_crlf_is_remembered()
    {
        var crlf = SubtitleParser.Parse("1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\nB\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nC\r\n", SubtitleFormat.Srt);
        Assert.Equal(2, crlf.Cues.Count);
        Assert.Equal("A\nB", crlf.Cues[0].Text);
        Assert.Equal("\r\n", crlf.NewLine);

        var cr = SubtitleParser.Parse("1\r00:00:01,000 --> 00:00:02,000\rA\r\r2\r00:00:03,000 --> 00:00:04,000\rB\r", SubtitleFormat.Srt);
        Assert.Equal(2, cr.Cues.Count);
        Assert.Equal("\n", SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000\nA\n", SubtitleFormat.Srt).NewLine);
    }

    [Fact]
    public void A_leading_bom_character_is_ignored()
    {
        Assert.Single(Parse("﻿1\n00:00:01,000 --> 00:00:02,000\nA\n").Cues);
    }

    [Fact]
    public void Indexes_are_optional()
    {
        var doc = Parse("00:00:01,000 --> 00:00:02,000\nNo index\n\n00:00:03,000 --> 00:00:04,000\nStill none\n");
        Assert.Equal(2, doc.Cues.Count);
        Assert.Null(doc.Cues[0].Identifier);
    }

    [Fact]
    public void Missing_blank_lines_between_cues_are_repaired_and_the_index_is_not_mistaken_for_text()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,000\nFirst\n2\n00:00:03,000 --> 00:00:04,000\nSecond\n3\n00:00:05,000 --> 00:00:06,000\nThird");
        Assert.Equal(["First", "Second", "Third"], doc.Cues.Select(c => c.Text));
        Assert.Equal(["1", "2", "3"], doc.Cues.Select(c => c.Identifier));
    }

    [Theory]
    [InlineData("00:00:01.500 --> 00:00:02.500", 1500, 2500)]     // dot decimal
    [InlineData("00:00:01,5 --> 00:00:02,25", 1500, 2250)]        // short fractions are fractions of a second
    [InlineData("00:00:01 --> 00:00:02", 1000, 2000)]            // no fraction at all
    [InlineData("00:01,000 --> 00:02,000", 1000, 2000)]           // no hours
    [InlineData("1:02:03,004 --> 1:02:04,004", 3723004, 3724004)] // one-digit hour
    [InlineData("100:00:00,000 --> 100:00:01,000", 360000000, 360001000)] // 100 hours
    [InlineData("00:00:01,5000 --> 00:00:02,0009", 1500, 2000)]   // more than 3 digits are truncated
    [InlineData("00:00:01,000 -> 00:00:02,000", 1000, 2000)]      // single dash arrow
    [InlineData("  00:00:01,000   -->   00:00:02,000  X1:10 X2:20", 1000, 2000)] // padding and coordinates
    public void Time_notations(string timing, int startMs, int endMs)
    {
        var doc = Parse($"1\n{timing}\nText\n");
        Assert.Equal(TimeSpan.FromMilliseconds(startMs), doc.Cues.Single().Start);
        Assert.Equal(TimeSpan.FromMilliseconds(endMs), doc.Cues.Single().End);
    }

    [Fact]
    public void Inline_markup_is_reduced_to_italic_bold_underline_strike()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,000\n<i>Italic</i> and <B>bold</B> <font color=\"#ff0000\">red</font> {\\an8}top\n");
        Assert.Equal("<i>Italic</i> and <b>bold</b> red top", doc.Cues[0].Text);
    }

    [Fact]
    public void Literal_less_than_signs_that_are_not_tags_survive()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,000\n5 < 10 and <3 you\n");
        Assert.Equal("5 < 10 and <3 you", doc.Cues[0].Text);
    }

    [Fact]
    public void Br_tags_become_line_breaks()
    {
        Assert.Equal("a\nb", Parse("1\n00:00:01,000 --> 00:00:02,000\na<br>b\n").Cues[0].Text);
    }

    [Fact]
    public void A_cue_that_ends_before_it_starts_is_clamped_with_a_warning()
    {
        var doc = Parse("1\n00:00:05,000 --> 00:00:02,000\nBackwards\n");
        Assert.Equal(doc.Cues[0].Start, doc.Cues[0].End);
        Assert.Single(doc.Warnings);
    }

    [Fact]
    public void Empty_placeholder_cues_are_skipped()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,000\n\n\n2\n00:00:03,000 --> 00:00:04,000\nReal\n");
        Assert.Equal("Real", doc.Cues.Single().Text);
    }

    [Fact]
    public void Numeric_dialogue_is_kept_when_the_cue_is_properly_separated()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,000\n42\n\n2\n00:00:03,000 --> 00:00:04,000\nNext\n");
        Assert.Equal("42", doc.Cues[0].Text);
    }

    [Fact]
    public void Stray_text_outside_cues_produces_a_warning_not_a_failure()
    {
        var doc = Parse("Subtitles by someone\n\n1\n00:00:01,000 --> 00:00:02,000\nA\n");
        Assert.Single(doc.Cues);
        Assert.Contains(doc.Warnings, w => w.Contains("outside any cue", StringComparison.Ordinal));
    }

    [Fact]
    public void Entities_in_srt_are_left_alone()
    {
        Assert.Equal("Tom &amp; Jerry", Parse("1\n00:00:01,000 --> 00:00:02,000\nTom &amp; Jerry\n").Cues[0].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("just some text\nwith no times at all")]
    [InlineData("1\n00:00:01,000 -> \nText")]
    [InlineData("1\n12345\nText")]
    public void Input_without_cues_throws_a_clear_exception(string text)
    {
        var ex = Assert.Throws<SubtitleFormatException>(() => Parse(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Content_of_the_wrong_format_is_detected_and_read_with_a_warning()
    {
        var vttText = "WEBVTT\n\n00:01.000 --> 00:02.000\nHello\n";
        var doc = SubtitleParser.Parse(vttText, SubtitleFormat.Ass);
        Assert.Equal(SubtitleFormat.Srt, doc.SourceFormat); // the SRT reader is tried first and accepts it
        Assert.Equal("Hello", doc.Cues.Single().Text);
        Assert.Contains(doc.Warnings, w => w.Contains("named like ASS", StringComparison.Ordinal));
    }
}

public class VttParserTests
{
    private static SubtitleDocument Parse(string text) => SubtitleParser.Parse(text, SubtitleFormat.Vtt);

    private const string Full = """
        WEBVTT - Example captions
        Kind: captions
        Language: ar

        STYLE
        ::cue { color: yellow }

        REGION
        id:fred width:40%

        NOTE This is a comment
        spanning two lines

        intro
        00:00:01.000 --> 00:00:04.000 align:start position:10%
        <v Roger Bingham>We are in New York City</v>
        and <c.yellow>this is yellow</c> &amp; &lt;done&gt;

        01:02.500 --> 01:04.000
        <i>Hours are optional</i> and <00:01:03.000>timestamps<00:01:03.500> go away
        """;

    [Fact]
    public void Full_file_with_header_style_region_note_identifier_and_settings()
    {
        var doc = Parse(Full);
        Assert.Equal(2, doc.Cues.Count);

        var a = doc.Cues[0];
        Assert.Equal("intro", a.Identifier);
        Assert.Equal(TimeSpan.FromSeconds(1), a.Start);
        Assert.Equal(TimeSpan.FromSeconds(4), a.End);
        Assert.Equal("align:start position:10%", a.Settings);
        Assert.Equal("We are in New York City\nand this is yellow & <done>", a.Text);

        var b = doc.Cues[1];
        Assert.Equal(TimeSpan.FromMilliseconds(62500), b.Start);
        Assert.Equal(TimeSpan.FromSeconds(64), b.End);
        Assert.Equal("<i>Hours are optional</i> and timestamps go away", b.Text);
        Assert.Null(b.Settings);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Note_blocks_never_become_cues_even_if_they_mention_arrows()
    {
        var doc = Parse("WEBVTT\n\nNOTE start --> end is just text here\n\n00:01.000 --> 00:02.000\nReal\n");
        Assert.Equal("Real", doc.Cues.Single().Text);
    }

    [Fact]
    public void A_header_that_runs_straight_into_the_first_cue_is_tolerated()
    {
        var doc = Parse("WEBVTT\n00:01.000 --> 00:02.000\nNo blank line\n");
        Assert.Equal("No blank line", doc.Cues.Single().Text);
    }

    [Fact]
    public void Comma_decimals_are_accepted_too()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), Parse("WEBVTT\n\n00:00:01,500 --> 00:00:02,000\nA\n").Cues.Single().Start);
    }

    [Fact]
    public void A_missing_header_is_a_warning_not_a_failure()
    {
        var doc = Parse("00:01.000 --> 00:02.000\nNo header\n");
        Assert.Single(doc.Cues);
        Assert.Contains(doc.Warnings, w => w.Contains("WEBVTT", StringComparison.Ordinal));
    }

    [Fact]
    public void Header_text_after_webvtt_needs_a_separator()
    {
        // "WEBVTTX" is not a header; the first block is then read as a (broken) cue block.
        var doc = Parse("WEBVTTX\n\n00:01.000 --> 00:02.000\nA\n");
        Assert.Single(doc.Cues);
        Assert.Contains(doc.Warnings, w => w.Contains("WEBVTT", StringComparison.Ordinal));
    }

    [Fact]
    public void Arabic_cues_with_rtl_marks_and_entities()
    {
        var doc = Parse("WEBVTT\n\n00:01.000 --> 00:02.000\n&rlm;مرحبا &amp; أهلا\n");
        Assert.Equal("‏مرحبا & أهلا", doc.Cues.Single().Text);
    }

    [Fact]
    public void Unreadable_timing_lines_are_skipped_with_a_warning()
    {
        var doc = Parse("WEBVTT\n\n00:01.000 --> nonsense\nBad\n\n00:03.000 --> 00:04.000\nGood\n");
        Assert.Equal("Good", doc.Cues.Single().Text);
        Assert.Single(doc.Warnings);
    }

    [Fact]
    public void Garbage_after_a_header_is_a_clear_failure()
    {
        Assert.Throws<SubtitleFormatException>(() => Parse("WEBVTT\n\nthis is not a cue\n"));
    }
}

public class AssParserTests
{
    private static SubtitleDocument Parse(string text) => SubtitleParser.Parse(text, SubtitleFormat.Ass);

    private const string Header = """
        [Script Info]
        Title: Demo
        ScriptType: v4.00+
        PlayResX: 1280

        [V4+ Styles]
        Format: Name, Fontname, Fontsize
        Style: Default,Arial,48

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

        """;

    [Fact]
    public void Dialogue_lines_with_commas_in_the_text()
    {
        var doc = Parse(Header + "Dialogue: 0,0:00:01.00,0:00:03.50,Default,Roger,0,0,0,,Hello, world, again\n");
        var cue = doc.Cues.Single();
        Assert.Equal("Hello, world, again", cue.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), cue.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), cue.End);
        Assert.Equal("Default", cue.Style);
        Assert.Equal("Roger", cue.Actor);
        Assert.Equal("Demo", doc.Title);
    }

    [Fact]
    public void Override_tags_are_stripped_and_basic_styles_become_inline_tags()
    {
        var doc = Parse(Header + @"Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\an8\pos(640,50)\fs40\c&HFF0000&}Top {\i1}italic{\i0} and {\b1\u1}bold underline{\r} end\NSecond line" + "\n");
        Assert.Equal("Top <i>italic</i> and <b><u>bold underline</u></b> end\nSecond line", doc.Cues.Single().Text);
    }

    [Fact]
    public void Unclosed_toggles_are_closed_at_the_end_of_the_cue()
    {
        var doc = Parse(Header + @"Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\i1}never closed" + "\n");
        Assert.Equal("<i>never closed</i>", doc.Cues.Single().Text);
    }

    [Fact]
    public void Overlapping_toggles_stay_properly_nested()
    {
        var doc = Parse(Header + @"Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\i1}a{\b1}b{\i0}c{\b0}d" + "\n");
        Assert.Equal("<i>a<b>b</b></i><b>c</b>d", doc.Cues.Single().Text);
    }

    [Fact]
    public void Soft_breaks_and_hard_spaces()
    {
        var doc = Parse(Header + @"Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,one\ntwo\hthree\Nfour" + "\n");
        Assert.Equal("one two three\nfour", doc.Cues.Single().Text);
    }

    [Fact]
    public void Vector_drawings_and_comments_are_not_cues()
    {
        var doc = Parse(Header
            + @"Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100 0 100{\p0}" + "\n"
            + "Comment: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,not shown\n"
            + "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Visible\n");
        Assert.Equal("Visible", doc.Cues.Single().Text);
    }

    [Fact]
    public void Events_are_sorted_by_start_time_stably()
    {
        var doc = Parse(Header
            + "Dialogue: 0,0:00:10.00,0:00:11.00,Default,,0,0,0,,late\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,early A\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,early B\n");
        Assert.Equal(["early A", "early B", "late"], doc.Cues.Select(c => c.Text));
    }

    [Fact]
    public void A_different_column_order_is_honoured_and_ssa_marked_works()
    {
        var doc = Parse("[Events]\nFormat: Marked, Text, End, Start\nDialogue: Marked=0,Reordered text,0:00:05.00,0:00:04.00\n");
        var cue = doc.Cues.Single();
        Assert.Equal("Reordered text", cue.Text);
        Assert.Equal(TimeSpan.FromSeconds(4), cue.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), cue.End);
    }

    [Fact]
    public void Centiseconds_and_hours()
    {
        var doc = Parse(Header + "Dialogue: 0,1:02:03.45,1:02:04.07,Default,,0,0,0,,t\n");
        Assert.Equal(TimeSpan.FromMilliseconds(3723450), doc.Cues.Single().Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3724070), doc.Cues.Single().End);
    }

    [Fact]
    public void Arabic_dialogue()
    {
        var doc = Parse(Header + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\an2}مرحبا، كيف حالك؟\n");
        Assert.Equal("مرحبا، كيف حالك؟", doc.Cues.Single().Text);
    }

    [Fact]
    public void Malformed_dialogue_lines_are_skipped_with_warnings_and_good_ones_survive()
    {
        var doc = Parse(Header
            + "Dialogue: 0,notatime,0:00:02.00,Default,,0,0,0,,bad time\n"
            + "Dialogue: 0,0:00:01.00\n"
            + "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,good\n");
        Assert.Equal("good", doc.Cues.Single().Text);
        Assert.Equal(2, doc.Warnings.Count);
    }

    [Fact]
    public void A_file_with_no_events_is_a_failure()
    {
        Assert.Throws<SubtitleFormatException>(() => Parse(Header));
    }
}

public class SubtitleDecoderTests
{
    private const string Sample = "1\n00:00:01,000 --> 00:00:02,000\nمرحبا بالعالم - Café\n";

    private static string RoundTrip(byte[] bytes, out string encoding) => SubtitleDecoder.Decode(bytes, out encoding);

    [Fact]
    public void Utf8_without_bom()
    {
        var text = RoundTrip(new UTF8Encoding(false).GetBytes(Sample), out var enc);
        Assert.Equal(Sample, text);
        Assert.Equal("utf-8", enc);
    }

    [Fact]
    public void Utf8_with_bom_drops_the_bom()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(Sample)).ToArray();
        var text = RoundTrip(bytes, out var enc);
        Assert.Equal(Sample, text);
        Assert.Equal("utf-8-bom", enc);
    }

    [Fact]
    public void Utf16_both_endiannesses_with_bom()
    {
        Assert.Equal(Sample, RoundTrip(TextEncoding.Unicode.GetPreamble().Concat(TextEncoding.Unicode.GetBytes(Sample)).ToArray(), out _));
        Assert.Equal(Sample, RoundTrip(TextEncoding.BigEndianUnicode.GetPreamble().Concat(TextEncoding.BigEndianUnicode.GetBytes(Sample)).ToArray(), out _));
    }

    [Fact]
    public void Utf16_without_bom_is_recognised_for_mostly_ascii_text()
    {
        var ascii = "1\n00:00:01,000 --> 00:00:02,000\nHello there\n\n2\n00:00:03,000 --> 00:00:04,000\nMore text\n";
        Assert.Equal(ascii, RoundTrip(TextEncoding.Unicode.GetBytes(ascii), out var le));
        Assert.Equal("utf-16le", le);
        Assert.Equal(ascii, RoundTrip(TextEncoding.BigEndianUnicode.GetBytes(ascii), out var be));
        Assert.Equal("utf-16be", be);
    }

    [Fact]
    public void Utf32_with_bom()
    {
        var bytes = new UTF32Encoding(false, true).GetPreamble().Concat(new UTF32Encoding(false, true).GetBytes(Sample)).ToArray();
        Assert.Equal(Sample, RoundTrip(bytes, out var enc));
        Assert.Equal("utf-32le", enc);
    }

    [Fact]
    public void Legacy_windows_1256_arabic_is_detected()
    {
        TextEncoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string arabic = "1\n00:00:01,000 --> 00:00:02,000\nمرحبا بالعالم كيف حالك\n";
        var bytes = TextEncoding.GetEncoding(1256).GetBytes(arabic);
        var text = RoundTrip(bytes, out var enc);
        Assert.Equal(arabic, text);
        Assert.Equal("windows-1256", enc);
    }

    [Fact]
    public void Legacy_windows_1252_western_text_is_not_mistaken_for_arabic()
    {
        TextEncoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string french = "1\n00:00:01,000 --> 00:00:02,000\nÇa va très bien, merci à tous\n";
        var text = RoundTrip(TextEncoding.GetEncoding(1252).GetBytes(french), out var enc);
        Assert.Equal(french, text);
        Assert.Equal("windows-1252", enc);
    }

    [Fact]
    public void Binary_data_is_recognised()
    {
        Assert.True(SubtitleDecoder.LooksBinary([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]));
        Assert.False(SubtitleDecoder.LooksBinary(new UTF8Encoding(false).GetBytes(Sample)));
        Assert.False(SubtitleDecoder.LooksBinary(TextEncoding.Unicode.GetPreamble().Concat(TextEncoding.Unicode.GetBytes(Sample)).ToArray()));
    }

    [Fact]
    public void Parsing_bytes_rejects_binary_and_empty_input_with_clear_messages()
    {
        var binary = Assert.Throws<SubtitleFormatException>(() => SubtitleParser.ParseBytes([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0], SubtitleFormat.Srt));
        Assert.Contains("binary", binary.Message, StringComparison.OrdinalIgnoreCase);
        var empty = Assert.Throws<SubtitleFormatException>(() => SubtitleParser.ParseBytes([], SubtitleFormat.Srt));
        Assert.Contains("empty", empty.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parsing_utf16_bytes_end_to_end()
    {
        var bytes = TextEncoding.Unicode.GetPreamble().Concat(TextEncoding.Unicode.GetBytes(Sample)).ToArray();
        var doc = SubtitleParser.ParseBytes(bytes, SubtitleFormat.Srt);
        Assert.Equal("مرحبا بالعالم - Café", doc.Cues.Single().Text);
    }
}
