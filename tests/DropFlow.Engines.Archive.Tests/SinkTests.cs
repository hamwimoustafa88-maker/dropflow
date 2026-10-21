using System.Formats.Tar;
using System.IO.Compression;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.Engines.Common;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

/// <summary>Direct tests of the pieces the archive pipeline is built from.</summary>
public class SinkTests
{
    [Fact]
    public void Tar_sink_refuses_an_entry_longer_than_its_header_says()
    {
        using var ms = new MemoryStream();
        var sink = new TarSink(ms);

        var ex = Assert.Throws<ArchiveException>(() => sink.AddFile("x.bin", new MemoryStream(Kit.Noise(1000)), declaredSize: 10, modified: null));
        Assert.Equal(ArchiveProblem.Corrupt, ex.Problem);
    }

    [Fact]
    public void Tar_sink_refuses_an_entry_shorter_than_its_header_says()
    {
        using var ms = new MemoryStream();
        var sink = new TarSink(ms);

        var ex = Assert.Throws<ArchiveException>(() => sink.AddFile("x.bin", new MemoryStream(Kit.Noise(5)), declaredSize: 50, modified: null));
        Assert.Equal(ArchiveProblem.Corrupt, ex.Problem);
    }

    [Fact]
    public void Tar_sink_spools_entries_of_unknown_size_and_writes_a_valid_tar()
    {
        var big = Kit.Noise(9 * 1024 * 1024); // larger than the in-memory spool limit, so it goes through a temp file
        using var ms = new MemoryStream();
        using (var sink = new TarSink(ms))
        {
            sink.AddFile($"{Kit.ArabicFolder}/\u0645\u0644\u0641.bin", new MemoryStream(big), declaredSize: -1, modified: DateTime.Now);
            sink.AddFile("small.txt", new MemoryStream(Kit.Bytes("hi")), declaredSize: -1, modified: null);
            sink.AddDirectory("emptydir", null);
            sink.Commit();
        }

        ms.Position = 0;
        var entries = Kit.ReadTarWithBcl(ms);
        Assert.Equal(Kit.Hash(big), entries[$"{Kit.ArabicFolder}/\u0645\u0644\u0641.bin"]);
        Assert.Equal(Kit.Hash(Kit.Bytes("hi")), entries["small.txt"]);
    }

    [Fact]
    public void Tar_sink_stores_very_long_unicode_names()
    {
        var name = Kit.ArabicFolder + "/" + new string('\u0639', 150) + ".txt"; // far beyond the 100-byte classic field
        using var ms = new MemoryStream();
        using (var sink = new TarSink(ms))
        {
            sink.AddFile(name, new MemoryStream(Kit.Bytes("long")), declaredSize: 4, modified: null);
            sink.Commit();
        }

        ms.Position = 0;
        Assert.Equal([name], Kit.ReadTarWithBcl(ms).Keys);
    }

    [Fact]
    public void Gzip_single_sink_takes_one_file_only()
    {
        using var ms = new MemoryStream();
        var sink = new GzipSingleSink(ms, 6);
        sink.AddFile("a.txt", new MemoryStream(Kit.Bytes("one")), 3, null);
        var ex = Assert.Throws<ArchiveException>(() => sink.AddFile("b.txt", new MemoryStream(Kit.Bytes("two")), 3, null));
        Assert.Equal(ArchiveProblem.Unsupported, ex.Problem);
    }

    [Fact]
    public void Zip_sink_clamps_impossible_timestamps()
    {
        using var ms = new MemoryStream();
        using (var sink = new ZipSink(ms, 6))
        {
            sink.AddFile("old.txt", new MemoryStream(Kit.Bytes("x")), 1, new DateTime(1601, 1, 1));
            sink.AddFile("future.txt", new MemoryStream(Kit.Bytes("x")), 1, new DateTime(9000, 1, 1));
            sink.Commit();
        }

        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        Assert.Equal(1980, zip.GetEntry("old.txt")!.LastWriteTime.Year);
        Assert.Equal(2107, zip.GetEntry("future.txt")!.LastWriteTime.Year);
    }

    [Theory]
    [InlineData(0, CompressionLevel.NoCompression)]
    [InlineData(1, CompressionLevel.Fastest)]
    [InlineData(3, CompressionLevel.Fastest)]
    [InlineData(6, CompressionLevel.Optimal)]
    [InlineData(9, CompressionLevel.SmallestSize)]
    public void Zip_levels_map_onto_the_framework_levels(int level, CompressionLevel expected) =>
        Assert.Equal(expected, ZipSink.MapLevel(level));

    [Fact]
    public void Folder_sink_never_overwrites_and_never_leaves_the_root()
    {
        using var temp = new TempDir();
        var root = Kit.NewDir(temp, "root");
        using var sink = new FolderSink(root);

        sink.AddFile("a.txt", new MemoryStream(Kit.Bytes("one")), 3, null);
        Assert.ThrowsAny<IOException>(() => sink.AddFile("a.txt", new MemoryStream(Kit.Bytes("two")), 3, null));
        Assert.Equal("one", File.ReadAllText(Path.Combine(root, "a.txt")));

        var ex = Assert.Throws<ArchiveException>(() => sink.AddFile("../escape.txt", new MemoryStream(Kit.Bytes("x")), 1, null));
        Assert.Equal(ArchiveProblem.UnsafePath, ex.Problem);
        Assert.False(File.Exists(temp.Combine("escape.txt")));
    }

    [Fact]
    public void Folder_sink_reports_a_file_in_the_way_of_a_folder()
    {
        using var temp = new TempDir();
        var root = Kit.NewDir(temp, "root");
        using var sink = new FolderSink(root);
        sink.AddFile("a", new MemoryStream(Kit.Bytes("file")), 4, null);

        var ex = Assert.Throws<ArchiveException>(() => sink.AddFile("a/b.txt", new MemoryStream(Kit.Bytes("x")), 1, null));
        Assert.Equal(ArchiveProblem.Conflict, ex.Problem);
        Assert.Equal("file", File.ReadAllText(Path.Combine(root, "a")));
    }

    [Fact]
    public async Task A_file_and_a_folder_with_the_same_name_in_one_archive_is_reported()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("clash.zip"), ("thing", Kit.Bytes("file")), ("thing/inner.txt", Kit.Bytes("x")));

        var result = await Kit.Run(new ArchiveConverter(Kit.RealEngines), "arc.extract", zip);

        Assert.False(result.Success);
        Assert.Contains("both a file and a folder", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Output_naming_drops_the_double_extension()
    {
        Assert.Equal("photos.tmp", Path.GetFileName(OutputNaming.BaseOf(@"C:\x\photos.tar.gz")));
        Assert.Equal("photos.tmp", Path.GetFileName(OutputNaming.BaseOf(@"C:\x\photos.tgz")));
        Assert.Equal("photos.tmp", Path.GetFileName(OutputNaming.BaseOf(@"C:\x\photos.zip")));
        Assert.Equal("my.data.tmp", Path.GetFileName(OutputNaming.BaseOf(@"C:\x\my.data.zip")));
        Assert.Equal("report.txt.tmp", Path.GetFileName(OutputNaming.BaseOf(@"C:\x\report.txt.gz")));
    }

    [Fact]
    public void Seven_zip_command_lines_are_built_as_argument_lists()
    {
        var create = SevenZipCommand.CreateArguments(@"C:\out dir\a b.7z", 7);
        Assert.Equal(["a", "-t7z", "-mx=7", "-y", "-bsp1", "--", @"C:\out dir\a b.7z", "*"], create);

        // the level is clamped and a hostile-looking archive name stays ONE argument after "--"
        Assert.Contains("-mx=9", SevenZipCommand.CreateArguments("x.7z", 99));
        Assert.Contains("-mx=0", SevenZipCommand.CreateArguments("x.7z", -5));
        var odd = SevenZipCommand.CreateArguments("-sdel.7z", 5);
        Assert.True(Array.IndexOf(odd.ToArray(), "--") < Array.IndexOf(odd.ToArray(), "-sdel.7z"));

        Assert.Equal(["l", "-slt", "-sccUTF-8", "--", "a.7z"], SevenZipCommand.ListArguments("a.7z"));
        Assert.Equal(["t", "-y", "--", "a.7z"], SevenZipCommand.TestArguments("a.7z"));
    }

    [Theory]
    [InlineData(" 42% 3 + file.txt", 42)]
    [InlineData("\r  7%\r 15%\r 99%", 99)]
    [InlineData("100%", 100)]
    [InlineData("Everything is Ok", null)]
    [InlineData("size 3.5% of", null)]
    [InlineData("250%", null)]
    public void Seven_zip_progress_is_parsed_from_its_output(string line, int? expected) =>
        Assert.Equal(expected, SevenZipCommand.ParsePercent(line));

    // ---------------------------------------------------------------- the engine, for real

    [Fact]
    public void The_engine_locator_finds_7z_from_PATH_when_it_is_installed()
    {
        var found = DropFlow.TestSupport.Engines.Locator.Locate(EngineId.SevenZip);
        if (found is null) return; // not installed here: the EngineFact tests report themselves as skipped

        Assert.True(File.Exists(found));
        Assert.Equal("7z.exe", Path.GetFileName(found), ignoreCase: true);
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task ProcessRunner_runs_7z_through_whatever_launcher_the_locator_found()
    {
        var exe = Kit.RealEngines.Locate(EngineId.SevenZip)!;

        var result = await ProcessRunner.RunAsync(exe, ["i"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) });

        Assert.True(result.Success, result.StdErr);
        Assert.Contains("7-Zip", result.StdOut);
        Assert.False(result.TimedOut);
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task ProcessRunner_cancellation_stops_a_running_7z()
    {
        using var temp = new TempDir();
        var big = Path.Combine(temp.Path, "big.bin");
        using (var fs = File.Create(big)) fs.Write(Kit.Noise(40 * 1024 * 1024));
        var exe = Kit.RealEngines.Locate(EngineId.SevenZip)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await ProcessRunner.RunAsync(exe, ["a", "-t7z", "-mx=9", "-y", "--", temp.Combine("out.7z"), big], new ProcessOptions { Timeout = TimeSpan.FromMinutes(2) }, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
    }
}
