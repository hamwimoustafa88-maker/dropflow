using System.Text.Json;
using DropFlow.AI.Speech;
using Xunit;

namespace DropFlow.AI.Tests;

public class TimestampTests
{
    [Theory]
    [InlineData(0.0, "00:00:00,000")]
    [InlineData(1.5, "00:00:01,500")]
    [InlineData(61.001, "00:01:01,001")]
    [InlineData(3661.789, "01:01:01,789")]
    [InlineData(59.9996, "00:01:00,000")]   // rounds up and carries, never "00:00:59,1000"
    [InlineData(359999.0, "99:59:59,000")]
    [InlineData(360000.0, "100:00:00,000")]  // more than two hour digits is legal
    [InlineData(-5.0, "00:00:00,000")]
    public void Srt_uses_a_comma_and_rounds_to_milliseconds(double seconds, string expected) =>
        Assert.Equal(expected, TranscriptWriters.SrtTimestamp(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Vtt_uses_a_dot()
    {
        Assert.Equal("00:00:01.500", TranscriptWriters.VttTimestamp(TimeSpan.FromSeconds(1.5)));
        Assert.Equal("01:00:00.000", TranscriptWriters.VttTimestamp(TimeSpan.FromHours(1)));
    }
}

public class TranscriptWriterTests
{
    private static TranscriptSegment Seg(double start, double end, string text) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    private const string ArabicHello = "مرحبا بالعالم";
    private const string ArabicSentence = "هذا اختبار لتحويل الكلام إلى نص باللغة العربية";

    [Fact]
    public void Srt_has_numbered_cues_crlf_and_a_blank_line_after_each()
    {
        var t = new Transcript([Seg(0, 2.5, "Hello world"), Seg(3, 4, "Second line")]);
        var srt = TranscriptWriters.ToSrt(t);
        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:02,500\r\nHello world\r\n\r\n" +
            "2\r\n00:00:03,000 --> 00:00:04,000\r\nSecond line\r\n\r\n", srt);
    }

    [Fact]
    public void Srt_output_is_read_back_correctly_by_a_second_parser()
    {
        var t = new Transcript([Seg(1.25, 3.5, "One"), Seg(65.5, 70, "Two"), Seg(3600, 3601, ArabicHello)]);
        var cues = ParseSrt(TranscriptWriters.ToSrt(t, new SubtitleOptions(RtlMarks: RtlMarkStyle.None)));
        Assert.Equal(3, cues.Count);
        Assert.Equal((1.25, 3.5, "One"), cues[0]);
        Assert.Equal((65.5, 70.0, "Two"), cues[1]);
        Assert.Equal((3600.0, 3601.0, ArabicHello), cues[2]);
    }

    [Fact]
    public void Vtt_has_header_dots_and_escaped_text()
    {
        var t = new Transcript([Seg(0, 1, "Tom & Jerry <3 a > b")]);
        var vtt = TranscriptWriters.ToVtt(t);
        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:00.000 --> 00:00:01.000\n", vtt);
        Assert.Contains("Tom &amp; Jerry &lt;3 a &gt; b", vtt);
        Assert.DoesNotContain("\r", vtt);
    }

    [Fact]
    public void Txt_is_one_segment_per_line_without_timestamps_or_direction_marks()
    {
        var t = new Transcript([Seg(0, 1, "  First   line "), Seg(1, 2, ArabicHello), Seg(2, 3, "   ")]);
        var txt = TranscriptWriters.ToTxt(t);
        Assert.Equal("First line\n" + ArabicHello + "\n", txt);
        Assert.DoesNotContain(RtlText.RightToLeftMark, txt);
    }

    [Fact]
    public void Json_keeps_arabic_readable_and_carries_times_in_seconds()
    {
        var t = new Transcript([Seg(0.5, 2.25, ArabicHello), Seg(3, 4, "Hello")], Language: "ar", Model: "ggml-small");
        var json = TranscriptWriters.ToJson(t);
        Assert.Contains(ArabicHello, json); // not \uXXXX escaped

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ar", root.GetProperty("language").GetString());
        Assert.Equal("ggml-small", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("translatedToEnglish").GetBoolean());
        Assert.Equal(4.0, root.GetProperty("duration").GetDouble());
        Assert.Equal(ArabicHello + " Hello", root.GetProperty("text").GetString());
        var segs = root.GetProperty("segments");
        Assert.Equal(2, segs.GetArrayLength());
        Assert.Equal(1, segs[0].GetProperty("index").GetInt32());
        Assert.Equal(0.5, segs[0].GetProperty("start").GetDouble());
        Assert.Equal(2.25, segs[0].GetProperty("end").GetDouble());
        Assert.Equal(ArabicHello, segs[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Render_dispatches_on_format()
    {
        var t = new Transcript([Seg(0, 1, "x")]);
        foreach (var f in Enum.GetValues<TranscriptFormat>())
            Assert.False(string.IsNullOrWhiteSpace(TranscriptWriters.Render(f, t)));
        Assert.Equal(new[] { "srt", "vtt", "txt", "json" }, Enum.GetValues<TranscriptFormat>().Select(TranscriptFormats.Extension));
    }

    // ---- RTL marks ----------------------------------------------------------------------------------------------

    [Fact]
    public void Arabic_lines_get_rlm_marks_and_english_lines_do_not()
    {
        var t = new Transcript([Seg(0, 1, ArabicHello), Seg(1, 2, "Hello world")]);
        var srt = TranscriptWriters.ToSrt(t);
        // Ordinal on purpose: culture-aware comparison ignores invisible characters, which would make these checks meaningless.
        Assert.Contains($"{RtlText.RightToLeftMark}{ArabicHello}{RtlText.RightToLeftMark}", srt, StringComparison.Ordinal);
        Assert.Contains("\r\nHello world\r\n", srt, StringComparison.Ordinal);
        Assert.DoesNotContain($"{RtlText.RightToLeftMark}Hello", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedding_style_uses_rle_and_pdf()
    {
        var t = new Transcript([Seg(0, 1, ArabicHello)]);
        var vtt = TranscriptWriters.ToVtt(t, new SubtitleOptions(RtlMarks: RtlMarkStyle.Embedding));
        Assert.Contains($"{RtlText.RightToLeftEmbedding}{ArabicHello}{RtlText.PopDirectionalFormatting}", vtt, StringComparison.Ordinal);
        Assert.Equal((char)0x202B, RtlText.RightToLeftEmbedding);
        Assert.Equal((char)0x202C, RtlText.PopDirectionalFormatting);
        Assert.Equal((char)0x200F, RtlText.RightToLeftMark);
    }

    [Fact]
    public void No_marks_when_disabled_and_marks_strip_back_to_the_original()
    {
        var t = new Transcript([Seg(0, 1, ArabicHello)]);
        Assert.DoesNotContain(RtlText.RightToLeftMark, TranscriptWriters.ToSrt(t, new SubtitleOptions(RtlMarks: RtlMarkStyle.None)));
        var marked = RtlText.MarkLine(ArabicHello, RtlMarkStyle.Embedding);
        Assert.NotEqual(ArabicHello, marked);
        Assert.Equal(ArabicHello, RtlText.StripMarks(marked));
    }

    [Fact]
    public void Each_line_of_a_wrapped_arabic_cue_is_marked_separately()
    {
        var t = new Transcript([Seg(0, 4, ArabicSentence)]);
        var cue = TranscriptWriters.ToSrt(t, new SubtitleOptions(MaxCharsPerLine: 20));
        var textLines = cue.Split("\r\n").Skip(2).TakeWhile(l => l.Length > 0).ToList();
        Assert.True(textLines.Count >= 2);
        Assert.All(textLines, l =>
        {
            Assert.StartsWith(RtlText.RightToLeftMark.ToString(), l, StringComparison.Ordinal);
            Assert.EndsWith(RtlText.RightToLeftMark.ToString(), l, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("مرحبا", true)]
    [InlineData("Hello", false)]
    [InlineData("مرحبا بالعالم Hello", true)]    // two Arabic words, one Latin
    [InlineData("Hello my friend مرحبا", false)]  // mostly Latin
    [InlineData("12345 !?", false)]               // no letters at all
    [InlineData("שלום עולם", true)]
    [InlineData("", false)]
    public void Direction_is_decided_by_the_majority_of_strong_letters(string text, bool rtl) =>
        Assert.Equal(rtl, RtlText.IsRightToLeft(text));

    // ---- Splitting ---------------------------------------------------------------------------------------------

    [Fact]
    public void Long_segments_are_wrapped_and_split_with_proportional_contiguous_times()
    {
        var words = Enumerable.Range(1, 40).Select(i => $"word{i:00}").ToList();
        var seg = Seg(10, 30, string.Join(' ', words));
        var cues = SegmentSplitter.Split([seg], new SubtitleOptions(MaxCharsPerLine: 20, MaxLines: 2));

        Assert.True(cues.Count > 1);
        foreach (var cue in cues)
        {
            var lines = cue.Text.Split('\n');
            Assert.InRange(lines.Length, 1, 2);
            Assert.All(lines, l => Assert.InRange(l.Length, 1, 20));
        }
        // nothing lost, nothing reordered
        Assert.Equal(words, cues.SelectMany(c => c.Text.Split(['\n', ' '])).ToList());
        // times: first start, last end, contiguous
        Assert.Equal(seg.Start, cues[0].Start);
        Assert.Equal(seg.End, cues[^1].End);
        for (var i = 1; i < cues.Count; i++) Assert.Equal(cues[i - 1].End, cues[i].Start);
        // proportional to the text each cue carries
        var total = cues.Sum(c => c.Text.Length - c.Text.Count(ch => ch == '\n'));
        foreach (var cue in cues)
        {
            var weight = (cue.Text.Length - cue.Text.Count(ch => ch == '\n')) / (double)total;
            Assert.InRange(cue.Duration.TotalSeconds / 20.0, weight - 0.02, weight + 0.02);
        }
    }

    [Fact]
    public void A_segment_that_fits_stays_one_cue_with_its_original_times()
    {
        var cues = SegmentSplitter.Split([Seg(1, 3, "Short text")], new SubtitleOptions(MaxCharsPerLine: 42));
        var cue = Assert.Single(cues);
        Assert.Equal("Short text", cue.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cue.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), cue.End);
    }

    [Fact]
    public void Two_lines_fit_in_one_cue_and_lines_respect_the_limit()
    {
        var cues = SegmentSplitter.Split([Seg(0, 5, "The quick brown fox jumps over the lazy dog")], new SubtitleOptions(MaxCharsPerLine: 25));
        var cue = Assert.Single(cues);
        Assert.Equal("The quick brown fox jumps\nover the lazy dog", cue.Text);
    }

    [Fact]
    public void Wrap_keeps_an_overlong_word_whole_and_breaks_unspaced_scripts_anywhere()
    {
        Assert.Equal(["tiny", "extraordinarily", "end"], LineWrapper.Wrap("tiny extraordinarily end", 10));
        Assert.Equal(["今天天气很", "好我们去公", "园散步吧"], LineWrapper.Wrap("今天天气很好我们去公园散步吧", 5));
        Assert.Empty(LineWrapper.Wrap("   ", 10));
        Assert.Equal(["a b c"], LineWrapper.Wrap("a b c", 5));
    }

    [Fact]
    public void Wrap_counts_user_perceived_characters_not_code_units()
    {
        // Arabic letter + 2 combining marks is one character for the reader.
        var word = "بِسْمِ";
        Assert.Equal(6, word.Length);
        Assert.Equal(3, LineWrapper.Length(word));
    }

    [Fact]
    public void Arabic_text_wraps_at_spaces()
    {
        var lines = LineWrapper.Wrap(ArabicSentence, 20);
        Assert.True(lines.Count >= 2);
        Assert.All(lines, l => Assert.True(LineWrapper.Length(l) <= 20));
        Assert.Equal(ArabicSentence, string.Join(' ', lines));
    }

    // ---- Normalisation -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("  hello   world \n again  ", "hello world again")]
    [InlineData("hello , world !", "hello, world!")]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("hi [BLANK_AUDIO] there", "hi there")]
    [InlineData("[ Silence ]", "")]
    [InlineData("(silence) ok", "ok")]
    [InlineData("tab\tseparated", "tab separated")]
    public void Normalize_cleans_whitespace_punctuation_and_silence_tags(string raw, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(raw));

    [Fact]
    public void Normalize_handles_arabic_punctuation_and_invisible_characters()
    {
        Assert.Equal("ماذا؟", TextNormalizer.Normalize("ماذا ؟"));
        Assert.Equal("نعم، لا", TextNormalizer.Normalize("نعم ، لا"));
        // zero-width space, BOM, replacement char and bidi controls disappear
        var dirty = "a" + (char)0x200B + "b" + (char)0xFEFF + "c" + (char)0xFFFD + "d" + (char)0x200F + "e" + (char)0x202B + "f";
        Assert.Equal("abcdef", TextNormalizer.Normalize(dirty));
        // joiners that Persian needs are kept
        var persian = "می" + (char)0x200C + "خواهم";
        Assert.Equal(persian, TextNormalizer.Normalize(persian));
        Assert.Equal("ok", TextNormalizer.Normalize("ok" + (char)7));
    }

    [Fact]
    public void Silence_tags_can_be_kept()
    {
        Assert.Equal("[BLANK_AUDIO]", TextNormalizer.Normalize("[BLANK_AUDIO]", stripNonSpeechTags: false));
    }

    [Fact]
    public void Clean_drops_empty_orders_by_start_and_repairs_timing()
    {
        var cleaned = TextNormalizer.Clean(
        [
            Seg(5, 6, "second"),
            Seg(1, 2, "[BLANK_AUDIO]"),
            Seg(3, 3, "zero length"),
            Seg(-2, 1, "negative start"),
            Seg(4, 3, "end before start"),
        ]);
        Assert.Equal(["negative start", "zero length", "end before start", "second"], cleaned.Select(c => c.Text));
        Assert.All(cleaned, c => Assert.True(c.End > c.Start));
        Assert.Equal(TimeSpan.Zero, cleaned[0].Start);
    }

    [Fact]
    public void Output_formats_parse_leniently_and_never_come_back_empty()
    {
        Assert.Equal([TranscriptFormat.Srt], TranscriptFormats.Parse(null));
        Assert.Equal([TranscriptFormat.Srt], TranscriptFormats.Parse("nonsense"));
        Assert.Equal([TranscriptFormat.Vtt, TranscriptFormat.Txt], TranscriptFormats.Parse("VTT, txt"));
        Assert.Equal([TranscriptFormat.Srt, TranscriptFormat.Vtt], TranscriptFormats.Parse("srt+vtt+srt"));
        Assert.Equal(4, TranscriptFormats.Parse("all").Count);
    }

    // ---- helper: independent SRT reader ------------------------------------------------------------------------

    private static List<(double Start, double End, string Text)> ParseSrt(string srt)
    {
        var result = new List<(double, double, string)>();
        foreach (var block in srt.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var times = lines[1].Split(" --> ");
            result.Add((Seconds(times[0]), Seconds(times[1]), string.Join('\n', lines.Skip(2))));
        }
        return result;

        static double Seconds(string stamp)
        {
            var parts = stamp.Split(':', ',');
            return int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]) + int.Parse(parts[3]) / 1000.0;
        }
    }
}
