using System.IO;
using DropFlow.Core.Operations;
using DropFlow.Overlay;

namespace DropFlow.Overlay.Tests;

public class OptionFormTests
{
    private static ToolOption Num(string key = "n", double? min = null, double? max = null, bool required = false, object? def = null) =>
        new(key, ToolOptionKind.Number, def, Min: min, Max: max, Required: required);

    [Theory]
    [InlineData("42", 42)]
    [InlineData(" 7.5 ", 7.5)]
    [InlineData("1,5", 1.5)]               // decimal comma
    [InlineData("1,000", 1000)]            // thousands comma
    [InlineData("1,000.5", 1000.5)]
    [InlineData("-3", -3)]
    [InlineData("٤٢", 42)]                 // Arabic-Indic digits
    [InlineData("٣٫٥", 3.5)]               // Arabic decimal separator
    [InlineData("۱۲۳", 123)]               // Persian digits
    public void Numbers_parse_across_locales(string raw, double expected)
    {
        Assert.True(OptionForm.TryParseNumber(raw, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Garbage_is_not_a_number(string raw) => Assert.False(OptionForm.TryParseNumber(raw, out _));

    [Fact]
    public void Number_ranges_are_enforced_with_specific_codes()
    {
        var o = Num(min: 1, max: 10);
        Assert.Equal((5d, null), OptionForm.Parse(o, "5"));
        Assert.Equal((null, "min"), OptionForm.Parse(o, "0"));
        Assert.Equal((null, "max"), OptionForm.Parse(o, "11"));
        Assert.Equal((null, "number"), OptionForm.Parse(o, "x"));
    }

    [Fact]
    public void Empty_input_uses_the_default_unless_the_field_is_required()
    {
        Assert.Equal((50d, null), OptionForm.Parse(Num(def: 50d), ""));
        Assert.Equal((null, "required"), OptionForm.Parse(Num(required: true), " "));
    }

    [Fact]
    public void Choices_must_be_one_of_the_offered_values()
    {
        var o = new ToolOption("level", ToolOptionKind.Choice, "less", ["less", "recommended", "extreme"]);
        Assert.Equal(("extreme", null), OptionForm.Parse(o, " extreme "));
        Assert.Equal((null, "choice"), OptionForm.Parse(o, "ultra"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("Yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("OFF", false)]
    public void Booleans_accept_the_usual_spellings(string raw, bool expected) =>
        Assert.Equal((expected, null), OptionForm.Parse(new ToolOption("b", ToolOptionKind.Bool, false), raw));

    [Fact]
    public void Passwords_are_never_trimmed_or_altered()
    {
        var o = new ToolOption("pw", ToolOptionKind.Password, null, Required: true);
        Assert.Equal((" s3 cret ", null), OptionForm.Parse(o, " s3 cret "));
        Assert.Equal((null, "required"), OptionForm.Parse(o, ""));
    }

    [Fact]
    public void Folders_must_exist()
    {
        var o = new ToolOption("dir", ToolOptionKind.Folder, null);
        Assert.Equal((Path.GetTempPath().TrimEnd('\\'), null), OptionForm.Parse(o, Path.GetTempPath().TrimEnd('\\')));
        Assert.Equal((null, "folder"), OptionForm.Parse(o, @"C:\definitely\not\here"));
    }

    [Fact]
    public void Validation_collects_every_error_and_the_good_values()
    {
        ToolOption[] options = [Num("a", min: 0, max: 5), Num("b", required: true), new("c", ToolOptionKind.Text, "x")];
        var (values, errors) = OptionForm.Validate(options, new Dictionary<string, string?> { ["a"] = "9", ["b"] = "", ["c"] = "hello" });

        Assert.Equal([new OptionError("a", "max"), new OptionError("b", "required")], errors);
        Assert.Equal("hello", values["c"]);
        Assert.DoesNotContain("a", values.Keys);
    }

    [Fact]
    public void Defaults_and_text_round_trip()
    {
        ToolOption[] options = [Num("q", def: 75d), new("flag", ToolOptionKind.Bool, true), new("mode", ToolOptionKind.Choice, "fast", ["fast", "slow"])];
        var defaults = OptionForm.Defaults(options);
        Assert.Equal(75d, defaults["q"]);

        var raw = options.ToDictionary(o => o.Key, o => (string?)OptionForm.ToText(defaults[o.Key]));
        var (values, errors) = OptionForm.Validate(options, raw);
        Assert.Empty(errors);
        Assert.Equal(75d, values["q"]);
        Assert.Equal(true, values["flag"]);
        Assert.Equal("fast", values["mode"]);
    }

    [Fact]
    public void Label_keys_fall_back_from_the_operation_to_the_generic_key() =>
        Assert.Equal(["opt.pdf.protect.password", "opt.password"], OptionForm.LabelKeys("pdf.protect", new ToolOption("password", ToolOptionKind.Password)));
}
