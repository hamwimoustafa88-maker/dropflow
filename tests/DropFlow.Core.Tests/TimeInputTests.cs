using DropFlow.Core.App;

namespace DropFlow.Core.Tests;

public class TimeInputTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("01:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:05", 5)]
    [InlineData("12.5", 12.5)]
    [InlineData("0:00:01.5", 1.5)]
    [InlineData("  2:00  ", 120)]
    public void Valid_times_parse_to_seconds(string text, double seconds)
    {
        Assert.True(TimeInput.TryParse(text, out var value));
        Assert.Equal(TimeSpan.FromSeconds(seconds), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_means_not_set(string text)
    {
        Assert.True(TimeInput.TryParse(text, out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("1:75")]
    [InlineData("-5")]
    [InlineData("1:-5")]
    [InlineData(":30")]
    [InlineData("1:")]
    [InlineData("1e3")]
    [InlineData("∞")]
    public void Invalid_times_are_rejected(string text) => Assert.False(TimeInput.TryParse(text, out _));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(90, "1:30")]
    [InlineData(3723, "1:02:03")]
    [InlineData(59.6, "1:00")]
    public void Durations_are_shown_as_clock_text(double seconds, string expected) =>
        Assert.Equal(expected, TimeInput.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_negative_duration_is_a_dash() =>
        Assert.Equal("—", TimeInput.Format(TimeSpan.FromSeconds(-1)));
}
