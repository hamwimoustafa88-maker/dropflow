using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Tests.Ffmpeg;

public class TimeSpecTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("90", 90)]
    [InlineData("90.5", 90.5)]
    [InlineData("90,5", 90.5)]
    [InlineData("12s", 12)]
    [InlineData("1:30", 90)]
    [InlineData("01:30.5", 90.5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("01:02:03.250", 3723.25)]
    [InlineData("00:00:05,500", 5.5)]
    [InlineData("0:75", 75)]
    [InlineData("  7  ", 7)]
    public void Valid_times_parse(string text, double expected)
    {
        Assert.True(TimeSpec.TryParseSeconds(text, out var seconds));
        Assert.Equal(expected, seconds, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("1::2")]
    [InlineData(":30")]
    [InlineData("1.2.3")]
    [InlineData("NaN")]
    public void Invalid_times_are_rejected(string? text)
    {
        Assert.False(TimeSpec.TryParseSeconds(text, out _));
    }

    [Theory]
    [InlineData("٩٠", 90)]               // Arabic-Indic digits
    [InlineData("۱:۳۰", 90)]             // Persian digits with a colon
    [InlineData("١٢٫٥", 12.5)]           // Arabic decimal separator
    public void Arabic_digits_and_separators_are_understood(string text, double expected)
    {
        Assert.True(TimeSpec.TryParseSeconds(text, out var seconds));
        Assert.Equal(expected, seconds, 6);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(90, "90")]
    [InlineData(90.5, "90.5")]
    [InlineData(1.23456, "1.235")]
    [InlineData(0.0004, "0")]
    [InlineData(-0.0001, "0")]
    public void Format_is_ffmpeg_friendly_and_culture_neutral(double seconds, string expected)
    {
        Assert.Equal(expected, TimeSpec.Format(seconds));
    }

    [Fact]
    public void Format_ignores_the_current_culture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-EG");
            Assert.Equal("12.5", TimeSpec.Format(12.5));
            Assert.Equal("0.35", TimeSpec.Num(0.35));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }

    [Theory]
    [InlineData(3723.25, false, "01:02:03.250")]
    [InlineData(3723.25, true, "01-02-03.250")]
    [InlineData(0, true, "00-00-00.000")]
    [InlineData(59.9996, false, "00:01:00.000")]
    public void Clock_format_pads_and_rounds(double seconds, bool forFileName, string expected)
    {
        Assert.Equal(expected, TimeSpec.FormatClock(seconds, forFileName));
    }

    [Fact]
    public void Time_lists_accept_commas_semicolons_spaces_and_clock_times()
    {
        Assert.Equal([5.0, 70.0, 90.5], TimeSpec.ParseList("5, 00:01:10; 90.5"));
        Assert.Equal([1.0, 2.0, 3.0], TimeSpec.ParseList("1 2\n3"));
        Assert.Equal([1.0, 2.0], TimeSpec.ParseList("١، ٢")); // Arabic comma
        Assert.Empty(TimeSpec.ParseList(""));
        Assert.Empty(TimeSpec.ParseList(null));
    }

    [Fact]
    public void A_bad_entry_in_a_list_throws_with_the_offending_text()
    {
        var ex = Assert.Throws<FormatException>(() => TimeSpec.ParseList("5, oops, 7"));
        Assert.Contains("oops", ex.Message);
    }

    [Fact]
    public void Ranges_parse_in_several_notations()
    {
        Assert.Equal([new TimeRange(1.5, 2.3), new TimeRange(10, 11)], TimeSpec.ParseRanges("1.5-2.3; 10-11"));
        Assert.Equal([new TimeRange(10, 12), new TimeRange(60, 65)], TimeSpec.ParseRanges("0:10-0:12, 1:00 to 1:05"));
        Assert.Equal([new TimeRange(1, 2), new TimeRange(3.5, 4)], TimeSpec.ParseRanges("[[1,2],[3.5,4]]"));
        Assert.Equal([new TimeRange(1, 2)], TimeSpec.ParseRanges("[[\"0:01\", \"00:00:02\"]]"));
        Assert.Equal([new TimeRange(5, 6)], TimeSpec.ParseRanges("٥-٦"));
        Assert.Equal([new TimeRange(1, 2)], TimeSpec.ParseRanges("1..2"));
        Assert.Equal([new TimeRange(1, 2)], TimeSpec.ParseRanges("1–2")); // en dash
    }

    [Fact]
    public void Ranges_are_sorted_and_overlaps_merged()
    {
        Assert.Equal([new TimeRange(1, 6), new TimeRange(10, 12)], TimeSpec.ParseRanges("10-12; 3-6; 1-4"));
        Assert.Equal([new TimeRange(1, 4)], TimeSpec.ParseRanges("1-2; 2-4")); // touching ranges join
    }

    [Theory]
    [InlineData("5-3")]
    [InlineData("3-3")]
    [InlineData("a-b")]
    [InlineData("12")]
    [InlineData("[[1,2,3]]")]
    [InlineData("[[1]]")]
    [InlineData("[1,2]")]
    [InlineData("[[1,2]")]
    public void Bad_ranges_throw_a_readable_format_exception(string text)
    {
        Assert.Throws<FormatException>(() => TimeSpec.ParseRanges(text));
    }

    [Fact]
    public void Empty_ranges_are_empty_not_an_error()
    {
        Assert.Empty(TimeSpec.ParseRanges(""));
        Assert.Empty(TimeSpec.ParseRanges("  "));
        Assert.Empty(TimeSpec.ParseRanges(null));
    }
}
