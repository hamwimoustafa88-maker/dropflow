using System.Windows.Media;
using DropFlow.Overlay;

namespace DropFlow.Overlay.Tests;

public class WheelThemeTests
{
    private static Color C(Brush b) => ((SolidColorBrush)b).Color;

    [Fact]
    public void Every_accent_swatch_keeps_its_label_readable_in_light_and_dark()
    {
        foreach (var theme in new[] { WheelTheme.Light, WheelTheme.Dark })
            foreach (var hex in WheelTheme.AccentSwatches)
            {
                var t = theme.With(hex);
                var ratio = WheelTheme.ContrastRatio(C(t.Accent), C(t.TextOnAccent));
                Assert.True(ratio >= 4.5, $"{hex} {(theme.IsDark ? "dark" : "light")}: {ratio:0.00}");
            }
    }

    [Fact]
    public void The_built_in_themes_have_readable_text_on_wedges_and_accent()
    {
        foreach (var t in new[] { WheelTheme.Light, WheelTheme.Dark })
        {
            Assert.True(WheelTheme.ContrastRatio(C(t.Text), C(t.Wedge)) >= 4.5);
            Assert.True(WheelTheme.ContrastRatio(C(t.TextMuted), C(t.Hub)) >= 4.5);
            Assert.True(WheelTheme.ContrastRatio(C(t.TextOnAccent), C(t.Accent)) >= 4.5);
            Assert.True(WheelTheme.ContrastRatio(C(t.Highlight), C(t.Wedge)) >= 4.5, "the '+ Add' label uses Highlight on a wedge");
        }
    }

    [Fact]
    public void Opacity_fades_only_the_wheel_surfaces_and_is_kept_in_range()
    {
        var t = WheelTheme.Light.With(null, 0.7);
        Assert.Equal(WheelTheme.Light.Wedge.Opacity * 0.7, t.Wedge.Opacity, 3);
        Assert.Equal(1, t.Text.Opacity);
        Assert.Equal(WheelTheme.Light.Wedge.Opacity * 0.6, WheelTheme.Light.With(null, 0.1).Wedge.Opacity, 3);
        Assert.Same(WheelTheme.Light.Accent, WheelTheme.Light.With(null).Accent);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    public void Anything_that_is_not_a_six_digit_colour_keeps_dropflows_accent(string hex) =>
        Assert.Same(WheelTheme.Light.Accent, WheelTheme.Light.With(hex).Accent);
}
