using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Engines.Fonts.Sfnt;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Fonts.Tests;

public sealed class FontConverterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FontConverter _converter = new();
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    public void Dispose() => _dir.Dispose();

    private Task<ConversionResult> Run(string input, string target, CancellationToken ct = default) =>
        _converter.RunAsync(new ConversionJob([input], Operation.Convert(target)), NoProgress, ct);

    [Theory]
    [InlineData("ttf", "woff", true)]
    [InlineData("ttf", "woff2", true)]
    [InlineData("ttf", "eot", true)]
    [InlineData("ttf", "otf", true)]
    [InlineData("otf", "ttf", true)]
    [InlineData("otf", "woff2", true)]
    [InlineData("woff", "woff2", true)]
    [InlineData("woff2", "woff", true)]
    [InlineData("woff2", "ttf", true)]
    [InlineData("woff", "otf", true)]
    [InlineData("TTF", "woff", true)]
    [InlineData("ttf", "ttf", false)]
    [InlineData("woff", "woff", false)]
    [InlineData("eot", "ttf", false)]
    [InlineData("ttc", "ttf", false)]
    [InlineData("ttf", "pdf", false)]
    [InlineData("pdf", "woff", false)]
    public void Supports_exactly_the_font_pairs(string input, string target, bool expected) =>
        Assert.Equal(expected, _converter.Supports(Operation.Convert(target), input));

    [Fact]
    public void Tools_and_engines()
    {
        Assert.False(_converter.Supports("img.compress", "ttf"));
        Assert.Equal(EngineId.None, _converter.EngineFor("convert:woff2"));
        Assert.Empty(_converter.OptionsFor("convert:woff2"));
        var registry = new ToolRegistry().Register(FontsModule.Create(new NoEngines()));
        Assert.NotNull(registry.Find("convert:woff2", "ttf"));
    }

    [Fact]
    public async Task Ttf_to_woff2_and_back_writes_beside_the_source_with_an_arabic_name_and_never_overwrites()
    {
        var input = Path.Combine(_dir.Path, "خط تجريبي.ttf");
        var original = TestFonts.Tiny();
        File.WriteAllBytes(input, original);

        var first = await Run(input, "woff2");
        Assert.True(first.Success, first.Error);
        Assert.Equal(Path.Combine(_dir.Path, "خط تجريبي (converted).woff2"), Assert.Single(first.Outputs));
        var second = await Run(input, "woff2");
        Assert.Equal(Path.Combine(_dir.Path, "خط تجريبي (converted 2).woff2"), Assert.Single(second.Outputs));
        Assert.Equal(original, File.ReadAllBytes(input));

        var back = await Run(first.Outputs[0], "ttf");
        Assert.True(back.Success, back.Error);
        var restored = File.ReadAllBytes(back.Outputs[0]);
        TestFonts.AssertChecksums(restored);
        TestFonts.AssertEquivalent(SfntFont.Read(original), SfntFont.Read(restored), "DSIG");
    }

    [Theory]
    [InlineData("woff")]
    [InlineData("woff2")]
    [InlineData("eot")]
    public async Task Every_web_format_is_written(string target)
    {
        var input = Path.Combine(_dir.Path, "tiny.ttf");
        File.WriteAllBytes(input, TestFonts.Tiny());
        var result = await Run(input, target);
        Assert.True(result.Success, result.Error);
        Assert.EndsWith($"tiny (converted).{target}", result.Outputs[0]);
        Assert.True(new FileInfo(result.Outputs[0]).Length > 0);
    }

    [Fact]
    public async Task Outline_conversions_tell_the_user_that_hinting_was_dropped_and_unwrapping_does_not()
    {
        var input = Path.Combine(_dir.Path, "tiny.ttf");
        File.WriteAllBytes(input, TestFonts.Tiny());
        var otf = await Run(input, "otf");
        Assert.True(otf.Success, otf.Error);
        Assert.Equal("note.font.outlinesConverted", otf.Note);
        Assert.Equal(SfntFont.CffFlavor, SfntFont.Read(File.ReadAllBytes(otf.Outputs[0])).Flavor);

        var woff = await Run(otf.Outputs[0], "woff");
        var back = await Run(woff.Outputs[0], "otf"); // CFF inside: unwrapped as it is, no note
        Assert.True(back.Success, back.Error);
        Assert.Null(back.Note);

        var ttf = await Run(back.Outputs[0], "ttf");
        Assert.Equal("note.font.outlinesConverted", ttf.Note);
        Assert.True(SfntFont.Read(File.ReadAllBytes(ttf.Outputs[0])).HasTrueTypeOutlines);
    }

    [Fact]
    public async Task A_ttf_file_that_is_really_a_woff_is_read_by_its_content()
    {
        var input = Path.Combine(_dir.Path, "renamed.ttf");
        File.WriteAllBytes(input, Web.Woff.Encode(SfntFont.Read(TestFonts.Tiny())));
        var result = await Run(input, "woff2");
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task A_desktop_font_unwrapped_from_woff_keeps_its_original_bytes_when_it_was_given_as_ttf()
    {
        var input = Path.Combine(_dir.Path, "tiny.woff");
        var sfnt = SfntFont.Read(TestFonts.Tiny());
        File.WriteAllBytes(input, Web.Woff.Encode(sfnt));
        var result = await Run(input, "ttf");
        Assert.True(result.Success, result.Error);
        var restored = SfntFont.Read(File.ReadAllBytes(result.Outputs[0]));
        foreach (var (tag, data) in sfnt.Tables) if (tag != "head") Assert.Equal(data, restored.Tables[tag]);
    }

    [Fact]
    public async Task A_damaged_font_fails_with_a_message_and_leaves_no_output()
    {
        var input = Path.Combine(_dir.Path, "broken.woff2");
        var bytes = Web.Woff2.Encode(SfntFont.Read(TestFonts.Tiny()));
        File.WriteAllBytes(input, bytes.AsSpan(0, 60).ToArray());
        var result = await Run(input, "ttf");
        Assert.False(result.Success);
        Assert.Contains("broken.woff2", result.Error);
        Assert.Single(Directory.GetFiles(_dir.Path));

        var text = Path.Combine(_dir.Path, "notes.ttf");
        File.WriteAllText(text, "this is not a font at all, just text");
        Assert.False((await Run(text, "woff")).Success);
        Assert.Equal(2, Directory.GetFiles(_dir.Path).Length);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var input = Path.Combine(_dir.Path, "tiny.ttf");
        File.WriteAllBytes(input, TestFonts.Tiny());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(input, "woff2", cts.Token));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task A_missing_file_fails()
    {
        var result = await Run(Path.Combine(_dir.Path, "missing.ttf"), "woff");
        Assert.False(result.Success);
    }

    private sealed class NoEngines : IEngineProvider
    {
        public string? Locate(EngineId engine) => null;
    }
}
