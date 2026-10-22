using DropFlow.Engines.Media.Preview;

namespace DropFlow.Engines.Media.Tests.Preview;

public class MediaTimeTextTests
{
    [Theory]
    [InlineData(0, "0:00.00")]
    [InlineData(5.25, "0:05.25")]
    [InlineData(62.499, "1:02.50")]
    [InlineData(720, "12:00.00")]
    [InlineData(3723.4, "1:02:03.40")]
    [InlineData(-3, "0:00.00")]
    public void Clock_shows_minutes_seconds_and_hundredths(double seconds, string expected) =>
        Assert.Equal(expected, MediaTimeText.Clock(seconds));

    [Theory]
    [InlineData(45.54, "45.5 s")]
    [InlineData(125, "2:05")]
    [InlineData(3723, "1:02:03")]
    public void Length_is_short(double seconds, string expected) => Assert.Equal(expected, MediaTimeText.Length(seconds));

    [Theory]
    [InlineData("1:30", 90)]
    [InlineData("0:05.25", 5.25)]
    [InlineData("١:٣٠", 90)]
    [InlineData("90.5", 90.5)]
    public void Typed_times_are_read(string text, double expected)
    {
        Assert.True(MediaTimeText.TryParse(text, out var s));
        Assert.Equal(expected, s, 3);
    }

    [Fact]
    public void A_clock_reads_back_to_the_same_time_and_options_are_invariant()
    {
        Assert.True(MediaTimeText.TryParse(MediaTimeText.Clock(3723.4), out var s));
        Assert.Equal(3723.4, s, 2);
        Assert.False(MediaTimeText.TryParse("soon", out _));
        Assert.Equal("12.5", MediaTimeText.ForOption(12.5));
        Assert.Equal("0", MediaTimeText.ForOption(-1));
    }
}
