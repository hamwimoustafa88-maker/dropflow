using System.IO.Compression;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

public class ZipToolTests
{
    private static ArchiveConverter Converter => new(Kit.RealEngines);

    [Fact]
    public async Task Zips_several_files_and_a_folder_into_one_zip_beside_the_first_input()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "project");
        var files = Kit.BuildSampleTree(tree);
        var loose1 = temp.WriteText("a.txt", "alpha");
        var loose2 = temp.WriteText($"{Kit.ArabicFile}.txt", Kit.ArabicText);

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [loose1, loose2, tree]));

        Assert.True(result.Success, result.Error);
        var zip = Assert.Single(result.Outputs);
        Assert.Equal("a (zipped).zip", Path.GetFileName(zip));
        Assert.Equal(temp.Path, Path.GetDirectoryName(zip));

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.txt"] = Kit.Hash(Kit.Bytes("alpha")),
            [$"{Kit.ArabicFile}.txt"] = Kit.Hash(Kit.Bytes(Kit.ArabicText)),
        };
        foreach (var (rel, content) in files) expected[$"project/{rel}"] = Kit.Hash(content);

        Assert.Equal(expected, Kit.ReadZipWithBcl(zip));              // BCL reader
        Assert.Equal(expected, Kit.ReadWithSharpCompress(zip));       // and a second, different reader
        Assert.Contains("project/empty-dir", Kit.ZipFolderEntries(zip));
    }

    [Fact]
    public async Task Names_the_zip_after_a_dotted_folder_without_cutting_it()
    {
        using var temp = new TempDir();
        var folder = Kit.NewDir(temp, "My.Project v2");
        File.WriteAllText(Path.Combine(folder, "x.txt"), "x");

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [folder]));

        Assert.True(result.Success, result.Error);
        Assert.Equal("My.Project v2 (zipped).zip", Path.GetFileName(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Same_named_files_from_different_folders_get_numbered_entries()
    {
        using var temp = new TempDir();
        var one = Kit.NewDir(temp, "one");
        var two = Kit.NewDir(temp, "two");
        File.WriteAllText(Path.Combine(one, "notes.txt"), "from one");
        File.WriteAllText(Path.Combine(two, "notes.txt"), "from two");

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [Path.Combine(one, "notes.txt"), Path.Combine(two, "notes.txt")]));

        Assert.True(result.Success, result.Error);
        var entries = Kit.ReadZipWithBcl(Assert.Single(result.Outputs));
        Assert.Equal(["notes (2).txt", "notes.txt"], entries.Keys);
        Assert.Equal(Kit.Hash(Kit.Bytes("from one")), entries["notes.txt"]);
        Assert.Equal(Kit.Hash(Kit.Bytes("from two")), entries["notes (2).txt"]);
    }

    [Fact]
    public async Task The_level_option_changes_the_size_and_zero_stores()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("compressible.txt", string.Concat(Enumerable.Repeat("compress me please. ", 20_000)));
        var original = new FileInfo(file).Length;

        async Task<long> SizeAt(int level)
        {
            var result = await Kit.Run(Converter, Kit.Job("arc.zip", [file], new() { ["level"] = level }));
            Assert.True(result.Success, result.Error);
            var zip = Assert.Single(result.Outputs);
            // whatever the level, it must read back identically
            Assert.Equal(Kit.Hash(File.ReadAllBytes(file)), Kit.ReadZipWithBcl(zip)["compressible.txt"]);
            return new FileInfo(zip).Length;
        }

        var stored = await SizeAt(0);
        var fast = await SizeAt(1);
        var best = await SizeAt(9);

        Assert.True(stored > original, "level 0 stores the data uncompressed");
        Assert.True(fast < stored / 4, $"fast={fast} stored={stored}");
        Assert.True(best <= fast, $"best={best} fast={fast}");
    }

    [Fact]
    public async Task Bad_level_values_fall_back_to_the_default_instead_of_failing()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("a.txt", "abc");

        foreach (var level in new object?[] { "high", 99, -4, null })
        {
            var result = await Kit.Run(Converter, Kit.Job("arc.zip", [file], new() { ["level"] = level }));
            Assert.True(result.Success, $"{level}: {result.Error}");
        }
    }

    [Fact]
    public async Task Never_overwrites_an_existing_zip_and_never_touches_the_inputs()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("a.txt", "abc");
        File.WriteAllText(temp.Combine("a (zipped).zip"), "PRECIOUS");

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [file]));

        Assert.True(result.Success, result.Error);
        Assert.Equal("a (zipped 2).zip", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("a (zipped).zip")));
        Assert.Equal("abc", File.ReadAllText(file));
    }

    [Fact]
    public async Task Honours_the_output_directory()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("a.txt", "abc");
        var elsewhere = Path.Combine(temp.Path, "out");

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [file], outputDirectory: elsewhere));

        Assert.True(result.Success, result.Error);
        Assert.Equal(elsewhere, Path.GetDirectoryName(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task A_missing_input_fails_before_creating_anything()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("a.txt", "abc");

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [file, temp.Combine("ghost.txt")]));

        Assert.False(result.Success);
        Assert.Contains("ghost.txt", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Symlinks_inside_a_folder_are_skipped_not_followed()
    {
        using var temp = new TempDir();
        var secret = Kit.NewDir(temp, "secret");
        File.WriteAllText(Path.Combine(secret, "private.txt"), "do not include");
        var project = Kit.NewDir(temp, "project");
        File.WriteAllText(Path.Combine(project, "public.txt"), "public");

        // On Windows a junction needs no special privilege; fall back to a symlink if mklink is somehow unavailable.
        // macOS and Linux create symlinks freely.
        var link = Path.Combine(project, "link");
        var made = OperatingSystem.IsWindows() ? await Engines.Common.ProcessRunner.RunAsync("cmd.exe", ["/c", "mklink", "/J", link, secret]) : null;
        if (made is not { Success: true })
        {
            try { Directory.CreateSymbolicLink(link, secret); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Fail("Could not create a junction or a symlink to test with: " + made?.StdOut + made?.StdErr);
            }
        }
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);

        try
        {
            var result = await Kit.Run(Converter, Kit.Job("arc.zip", [project]));

            Assert.True(result.Success, result.Error);
            Assert.Equal(["project/public.txt"], Kit.ReadZipWithBcl(Assert.Single(result.Outputs)).Keys);
            Assert.Contains("link(s) skipped", result.Note);
            Assert.True(File.Exists(Path.Combine(secret, "private.txt"))); // and the link target was left alone
        }
        finally
        {
            Directory.Delete(link); // removes only the junction, so TempDir cleanup does not choke on it
        }
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_zip()
    {
        using var temp = new TempDir();
        var big = Path.Combine(temp.Path, "big.bin");
        using (var fs = File.Create(big)) fs.Write(new byte[1024 * 1024 * 120]);
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.05) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("arc.zip", [big]), cts.Token, recorder));

        Assert.Equal(["big.bin"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Progress_ends_at_one_and_never_goes_backwards()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var recorder = new Recorder();

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [tree]), recorder: recorder);

        Assert.True(result.Success, result.Error);
        var fractions = recorder.Items.Select(p => p.Fraction).ToList();
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1.0, fractions[^1]);
    }

    [Fact]
    public async Task A_zip_made_by_the_tool_extracts_back_to_the_same_tree()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);

        var zipped = await Kit.Run(Converter, Kit.Job("arc.zip", [tree]));
        var extracted = await Kit.Run(Converter, "arc.extract", Assert.Single(zipped.Outputs));

        Assert.True(extracted.Success, extracted.Error);
        var folder = Assert.Single(extracted.Outputs);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rel, content) in files) expected[$"src/{rel}"] = Kit.Hash(content);
        Assert.Equal(expected, Kit.TreeHashes(folder));
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task Seven_zip_itself_can_list_and_test_the_zip_with_arabic_names()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);

        var result = await Kit.Run(Converter, Kit.Job("arc.zip", [tree]));
        var zip = Assert.Single(result.Outputs);

        var names = await Kit.SevenZipFileNames(zip);
        Assert.Equal(files.Keys.Select(k => $"src/{k}").Order(StringComparer.Ordinal), names);
        var test = await Kit.SevenZip(SevenZipCommand.TestArguments(zip).ToArray());
        Assert.True(test.Success, test.StdOut);
    }
}
