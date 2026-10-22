using DropFlow.Core.Output;
using static DropFlow.Core.Tests.TestPaths;

namespace DropFlow.Core.Tests;

public class OutputPathResolverTests
{
    private static OutputPathResolver With(params string[] existing)
    {
        var set = existing.Select(e => OperatingSystem.IsWindows() ? e.Replace('/', '\\') : P(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new OutputPathResolver(set.Contains);
    }

    [Fact]
    public void Writes_beside_the_source_with_a_suffix_and_new_extension()
    {
        var r = With();
        Assert.Equal(P(@"C:\pics\photo (converted).png"), r.ForFile(P(@"C:\pics\photo.heic"), "png"));
    }

    [Fact]
    public void Keeps_the_source_extension_when_none_is_given()
    {
        Assert.Equal(P(@"C:\pics\photo (compressed).jpg"), With().ForFile(P(@"C:\pics\photo.jpg"), null, "compressed"));
    }

    [Fact]
    public void Never_overwrites_an_existing_file_and_numbers_the_result()
    {
        var r = With(P(@"C:\pics\photo (converted).png"), P(@"C:\pics\photo (converted 2).png"));
        Assert.Equal(P(@"C:\pics\photo (converted 3).png"), r.ForFile(P(@"C:\pics\photo.heic"), "png"));
    }

    [Fact]
    public void Output_directory_overrides_the_source_folder()
    {
        Assert.Equal(P(@"D:\out\photo (converted).png"), With().ForFile(P(@"C:\pics\photo.heic"), "png", outputDirectory: P(@"D:\out")));
    }

    [Fact]
    public void Group_results_are_named_after_the_first_input()
    {
        Assert.Equal(P(@"C:\docs\a (merged).pdf"), With().ForGroup([P(@"C:\docs\a.pdf"), P(@"C:\docs\b.pdf")], "pdf"));
    }

    [Fact]
    public void Folder_results_get_a_new_folder_and_never_reuse_one()
    {
        var r = With(P(@"C:\docs\report (pages)"));
        Assert.Equal(P(@"C:\docs\report (pages 2)"), r.ForFolder(P(@"C:\docs\report.pdf"), "pages"));
    }

    [Fact]
    public void Works_against_the_real_file_system()
    {
        using var dir = new TempDir();
        var input = dir.File("clip.mov");
        dir.File("clip (converted).mp4");
        var output = new OutputPathResolver().ForFile(input, "mp4");
        Assert.Equal(Path.Combine(dir.Path, "clip (converted 2).mp4"), output);
    }
}
