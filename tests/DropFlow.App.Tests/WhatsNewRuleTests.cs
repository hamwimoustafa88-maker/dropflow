using DropFlow.App.Views;

namespace DropFlow.App.Tests;

public class WhatsNewRuleTests
{
    [Theory]
    [InlineData("0.3.0", "0.2.0", false, true)]   // a feature update shows the slides
    [InlineData("0.3.0", null, false, true)]      // versions before the setting recorded nothing
    [InlineData("0.3.1", "0.3.0", false, false)]  // a fix-only release does not show the same slides again
    [InlineData("0.3.1+abc", "0.3.0", false, false)]
    [InlineData("0.4.0", "0.3.1", false, true)]
    [InlineData("0.3.0", "0.3.0", false, false)]
    [InlineData("0.3.0", null, true, false)]      // first install: the welcome guide instead
    public void What_is_new_shows_only_for_a_new_feature_version(string current, string? seen, bool firstRun, bool shows) =>
        Assert.Equal(shows, WhatsNew.ShouldShow(current, seen, firstRun));
}
