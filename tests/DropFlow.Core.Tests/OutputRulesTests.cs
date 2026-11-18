using DropFlow.Core.Output;

namespace DropFlow.Core.Tests;

public class OutputRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 5, 7);

    [Fact]
    public void No_rules_change_nothing()
    {
        using var dir = new TempDir();
        var output = dir.File("a (converted).jpg");
        Assert.Same(Array.Empty<string>(), OutputFinalizer.Apply(OutputRules.Default, [], [], Now));
        Assert.Equal([output], OutputFinalizer.Apply(OutputRules.Default, [dir.File("a.png")], [output], Now));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Every_token_is_filled_in()
    {
        var stem = OutputFinalizer.Expand("{name}-{tool}-{ext}-{date}-{time}-{index}", @"C:\p\رحلة.png", @"C:\p\رحلة (compressed 2).jpg", 2, 12, Now);
        Assert.Equal("رحلة-compressed-jpg-2026-11-18-090507-03", stem);
    }

    [Fact]
    public void A_pattern_renames_the_result_and_never_takes_the_name_of_the_source()
    {
        using var dir = new TempDir();
        var source = dir.File("photo.jpg", "source");
        var output = dir.File("photo (converted).jpg", "result");

        var result = OutputFinalizer.Apply(new OutputRules("{name}", NameConflict.Overwrite), [source], [output], Now);

        Assert.Equal(Path.Combine(dir.Path, "photo (2).jpg"), Assert.Single(result));   // "photo.jpg" is the source: overwrite falls back to renaming
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("result", File.ReadAllText(result[0]));
    }

    [Fact]
    public void Overwrite_replaces_an_unrelated_older_file_and_rename_keeps_it()
    {
        using var dir = new TempDir();
        var source = dir.File("a.png");
        dir.File("a-final.jpg", "old");

        var renamed = OutputFinalizer.Apply(new OutputRules("{name}-final"), [source], [dir.File("a (converted).jpg", "new1")], Now);
        Assert.Equal(Path.Combine(dir.Path, "a-final (2).jpg"), renamed[0]);

        var replaced = OutputFinalizer.Apply(new OutputRules("{name}-final", NameConflict.Overwrite), [source], [dir.File("a (converted).jpg", "new2")], Now);
        Assert.Equal(Path.Combine(dir.Path, "a-final.jpg"), replaced[0]);
        Assert.Equal("new2", File.ReadAllText(replaced[0]));
    }

    [Fact]
    public void Keep_dates_copies_the_sources_dates()
    {
        using var dir = new TempDir();
        var source = dir.File("old.png");
        File.SetLastWriteTimeUtc(source, new DateTime(2019, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        var output = dir.File("old (converted).jpg");

        OutputFinalizer.Apply(new OutputRules(KeepDates: true), [source], [output], Now);

        Assert.Equal(new DateTime(2019, 5, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(output));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("{name}-small", true)]
    [InlineData("{index}", true)]
    [InlineData("fixed", false)]          // a batch would collide
    [InlineData("{name}/x", false)]
    public void Patterns_are_checked(string? pattern, bool ok) => Assert.Equal(ok, OutputRules.IsValidPattern(pattern));
}
