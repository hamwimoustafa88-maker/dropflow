using DropFlow.Core.Operations;
using DropFlow.Desktop.App;

namespace DropFlow.Desktop.Tests;

public class LocalizerTests
{
    private static readonly IReadOnlyDictionary<string, string> En = DesktopLocalizer.ReadDesktop("en");
    private static readonly IReadOnlyDictionary<string, string> Ar = DesktopLocalizer.ReadDesktop("ar");

    [Fact]
    public void Arabic_and_english_have_the_same_keys()
    {
        Assert.Empty(En.Keys.Except(Ar.Keys));
        Assert.Empty(Ar.Keys.Except(En.Keys));
    }

    [Fact]
    public void Arabic_strings_are_written_in_arabic()
    {
        var notArabic = Ar.Where(kv => kv.Key != "history.when" && !kv.Value.Any(c => c is >= '؀' and <= 'ۿ')).Select(kv => kv.Key).ToList();
        Assert.Empty(notArabic);
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        foreach (var (key, en) in En)
        {
            var count = System.Text.RegularExpressions.Regex.Matches(en, @"\{\d").Count;
            Assert.True(count == System.Text.RegularExpressions.Regex.Matches(Ar[key], @"\{\d").Count, key);
        }
    }

    [Fact]
    public void Desktop_keys_win_and_core_keys_are_still_found()
    {
        var loc = DesktopLocalizer.Load("ar");
        Assert.Equal("أهلاً بك في DropFlow", loc["welcome.title"]);
        Assert.NotEqual("tray.quit", loc["tray.quit"]);            // from DropFlow.Core
        Assert.True(loc.IsRightToLeft);
        Assert.Equal("no.such.key", loc["no.such.key"]);
    }

    [Fact]
    public void English_has_no_right_to_left() => Assert.False(DesktopLocalizer.Load("en").IsRightToLeft);

    [Fact]
    public void Option_labels_fall_back_from_the_operation_to_the_generic_key_then_the_raw_key()
    {
        var loc = new DesktopLocalizer("en",
            new Dictionary<string, string> { ["opt.pdf.compress.level"] = "Compression", ["opt.quality"] = "Quality", ["opt.level.high"] = "High" });
        Assert.Equal("Compression", loc.OptionLabel("pdf.compress", new ToolOption("level", ToolOptionKind.Choice)));
        Assert.Equal("Quality", loc.OptionLabel("img.compress", new ToolOption("quality", ToolOptionKind.Number)));
        Assert.Equal("dpi", loc.OptionLabel("pdf.to-image", new ToolOption("dpi", ToolOptionKind.Number)));
        Assert.Equal("High", loc.ChoiceLabel("pdf.compress", new ToolOption("level", ToolOptionKind.Choice), "high"));
        Assert.Equal("low", loc.ChoiceLabel("pdf.compress", new ToolOption("level", ToolOptionKind.Choice), "low"));
    }
}
