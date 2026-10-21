using System.Formats.Tar;
using System.IO.Compression;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

public class ExtractTests
{
    private static ArchiveConverter Converter => new(Kit.RealEngines);

    private static async Task<string> ExtractOk(string archive, Dictionary<string, object?>? options = null, string? outDir = null)
    {
        var result = await Kit.Run(Converter, "arc.extract", archive, options, outDir);
        Assert.True(result.Success, result.Error);
        return Assert.Single(result.Outputs);
    }

    [Fact]
    public async Task Extracts_a_zip_with_arabic_names_into_a_new_folder_beside_it()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var expected = Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("بيانات.zip"), tree);

        var folder = await ExtractOk(zip);

        Assert.Equal(temp.Path, Path.GetDirectoryName(folder));
        Assert.Equal("بيانات (extracted)", Path.GetFileName(folder));
        Assert.Equal(Kit.ExpectedHashes(expected), Kit.TreeHashes(folder));
        Assert.Contains("empty-dir", Kit.FolderNames(folder));
        Assert.Contains($"docs", Kit.FolderNames(folder));
        Assert.Contains(Kit.ArabicFolder, Kit.FolderNames(folder));
    }

    [Fact]
    public async Task Extracts_tar_and_tar_gz_and_gz_and_tgz()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var expected = Kit.ExpectedHashes(Kit.BuildSampleTree(tree));

        var tar = Kit.MakeTar(temp.Combine("a.tar"), tree);
        var targz = Kit.MakeTarGz(temp.Combine("b.tar.gz"), tree);
        var tgz = Kit.MakeTarGz(temp.Combine("c.tgz"), tree);
        var gz = Kit.MakeGzipOfFile(temp.Combine("readme.txt.gz"), Path.Combine(tree, "readme.txt"));

        Assert.Equal(expected, Kit.TreeHashes(await ExtractOk(tar)));
        Assert.Equal(expected, Kit.TreeHashes(await ExtractOk(targz)));
        Assert.Equal(expected, Kit.TreeHashes(await ExtractOk(tgz)));

        var single = Kit.TreeHashes(await ExtractOk(gz));
        Assert.Equal(["readme.txt"], single.Keys);
        Assert.Equal(Kit.Hash(File.ReadAllBytes(Path.Combine(tree, "readme.txt"))), single["readme.txt"]);
    }

    [Fact]
    public async Task Names_the_folder_after_the_archive_without_the_double_extension()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var archive = Kit.MakeTarGz(temp.Combine("photos.tar.gz"), tree);

        var folder = await ExtractOk(archive);
        Assert.Equal("photos (extracted)", Path.GetFileName(folder));
    }

    [Fact]
    public async Task Never_reuses_an_existing_folder_or_touches_its_files()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("a.zip"), tree);

        var existing = Kit.NewDir(temp, "a (extracted)");
        File.WriteAllText(Path.Combine(existing, "readme.txt"), "MINE");
        var inputBefore = Kit.Hash(File.ReadAllBytes(zip));

        var first = await ExtractOk(zip);
        var second = await ExtractOk(zip);

        Assert.Equal("a (extracted 2)", Path.GetFileName(first));
        Assert.Equal("a (extracted 3)", Path.GetFileName(second));
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(existing, "readme.txt")));
        Assert.Equal(inputBefore, Kit.Hash(File.ReadAllBytes(zip))); // the archive itself is never modified
    }

    [Fact]
    public async Task Honours_the_output_directory()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("a.zip"), tree);
        var elsewhere = Path.Combine(temp.Path, "out", "nested");

        var folder = await ExtractOk(zip, outDir: elsewhere);
        Assert.Equal(elsewhere, Path.GetDirectoryName(folder));
        Assert.True(File.Exists(Path.Combine(folder, "readme.txt")));
    }

    [Fact]
    public async Task Duplicate_entry_names_are_kept_with_numbered_names_instead_of_overwriting()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("dup.zip"),
            ("same.txt", Kit.Bytes("first")),
            ("same.txt", Kit.Bytes("second")),
            ("SAME.TXT", Kit.Bytes("third")));

        var folder = await ExtractOk(zip);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { "same (2).txt", "SAME (3).TXT", "same.txt" }.Order(), files);
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "same.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(folder, "same (2).txt")));
    }

    // -------------------------------------------------------------------------------------------- zip-slip

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("good/../../evil.txt")]
    public async Task Zip_slip_traversal_entry_fails_and_writes_nothing_anywhere(string evilName)
    {
        using var temp = new TempDir();
        var sandbox = Kit.NewDir(temp, "sandbox");
        var zip = Kit.CraftZip(Path.Combine(sandbox, "evil.zip"), ("ok.txt", Kit.Bytes("fine")), (evilName, Kit.Bytes("PWNED")));
        var before = Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).Order().ToList();

        var result = await Kit.Run(Converter, "arc.extract", zip);

        Assert.False(result.Success);
        Assert.Contains("Unsafe path", result.Error);
        Assert.Empty(result.Outputs);
        // nothing new anywhere under the temp root, including the parent of the sandbox, and no half-made output folder
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox, "evil.txt")));
    }

    [Theory]
    [InlineData("/abs.txt")]
    [InlineData("\\abs.txt")]
    [InlineData("C:\\Windows\\Temp\\dropflow-abs.txt")]
    [InlineData("C:dropflow-rel.txt")]
    [InlineData("//server/share/x.txt")]
    public async Task Absolute_paths_and_drive_letters_are_refused(string evilName)
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("evil.zip"), (evilName, Kit.Bytes("PWNED")));

        var result = await Kit.Run(Converter, "arc.extract", zip);

        Assert.False(result.Success);
        Assert.Contains("Unsafe path", result.Error);
        Assert.False(File.Exists(@"C:\Windows\Temp\dropflow-abs.txt"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "abs.txt")));
        Assert.Single(Directory.GetFileSystemEntries(temp.Path)); // only the archive
    }

    [Fact]
    public async Task Skip_policy_extracts_the_safe_entries_and_reports_the_rest()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("mixed.zip"),
            ("good.txt", Kit.Bytes("good")), ("../evil.txt", Kit.Bytes("PWNED")), ("/abs.txt", Kit.Bytes("PWNED")), ("sub/also-good.txt", Kit.Bytes("good2")));

        var result = await Kit.Run(Converter, "arc.extract", zip, new() { ["unsafePaths"] = "skip" });

        Assert.True(result.Success, result.Error);
        Assert.Contains("2 entries with unsafe paths skipped", result.Note);
        var folder = Assert.Single(result.Outputs);
        Assert.Equal(["good.txt", "sub/also-good.txt"], Kit.TreeHashes(folder).Keys);
        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.txt")));
    }

    [Fact]
    public async Task Reserved_device_names_become_ordinary_files()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("devices.zip"),
            ("CON", Kit.Bytes("console")), ("nul.txt", Kit.Bytes("null")), ("aux", Kit.Bytes("aux")), ("COM1.log", Kit.Bytes("com")), ("dir/PRN", Kit.Bytes("prn")));

        var result = await Kit.Run(Converter, "arc.extract", zip);

        Assert.True(result.Success, result.Error);
        var files = Kit.TreeHashes(Assert.Single(result.Outputs)).Keys.ToList();
        Assert.Equal(new[] { "_aux", "_COM1.log", "_CON", "_nul.txt", "dir/_PRN" }.Order(StringComparer.Ordinal), files);
        Assert.Contains("name(s) adjusted for Windows", result.Note);
    }

    [Fact]
    public async Task Alternate_data_stream_syntax_cannot_hide_data()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("ads.zip"), ("visible.txt:hidden.exe", Kit.Bytes("payload")));

        var result = await Kit.Run(Converter, "arc.extract", zip);

        Assert.True(result.Success, result.Error);
        var folder = Assert.Single(result.Outputs);
        Assert.Equal(["visible.txt_hidden.exe"], Kit.TreeHashes(folder).Keys);
    }

    // -------------------------------------------------------------------------------------------- links

    [Fact]
    public async Task Symlinks_in_a_tar_are_skipped_and_cannot_redirect_later_entries()
    {
        using var temp = new TempDir();
        var outside = Kit.NewDir(temp, "outside");
        var tarPath = temp.Combine("links.tar");
        using (var fs = File.Create(tarPath))
        using (var writer = new System.Formats.Tar.TarWriter(fs, TarEntryFormat.Pax))
        {
            // classic attack: a link to a folder outside, then a file "through" the link
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = outside });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "escape/pwned.txt") { DataStream = new MemoryStream(Kit.Bytes("PWNED")) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "escape/pwned.txt" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "normal.txt") { DataStream = new MemoryStream(Kit.Bytes("normal")) });
        }

        var result = await Kit.Run(Converter, "arc.extract", tarPath);

        Assert.True(result.Success, result.Error);
        Assert.Contains("link(s) skipped", result.Note);
        var folder = Assert.Single(result.Outputs);
        Assert.Empty(Directory.GetFiles(outside)); // nothing written through the link
        Assert.Equal(["escape/pwned.txt", "normal.txt"], Kit.TreeHashes(folder).Keys); // it is just an ordinary folder inside the output
        AssertNoReparsePoints(folder);
    }

    [Fact]
    public async Task Symlinks_in_a_zip_are_skipped()
    {
        using var temp = new TempDir();
        var outside = Kit.NewDir(temp, "outside");
        var zipPath = temp.Combine("links.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Kit.AddZipSymlink(zip, "escape", outside);
            var entry = zip.CreateEntry("escape/pwned.txt");
            using (var s = entry.Open()) s.Write(Kit.Bytes("PWNED"));
            var ok = zip.CreateEntry("ok.txt");
            using (var s = ok.Open()) s.Write(Kit.Bytes("ok"));
        }

        var result = await Kit.Run(Converter, "arc.extract", zipPath);

        Assert.True(result.Success, result.Error);
        Assert.Contains("1 link(s) skipped", result.Note);
        Assert.Empty(Directory.GetFiles(outside));
        var folder = Assert.Single(result.Outputs);
        Assert.Equal(["escape/pwned.txt", "ok.txt"], Kit.TreeHashes(folder).Keys);
        AssertNoReparsePoints(folder);
    }

    private static void AssertNoReparsePoints(string folder)
    {
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            Assert.Null(info.LinkTarget);
    }

    // -------------------------------------------------------------------------------------------- bombs

    [Fact]
    public async Task A_decompression_bomb_is_stopped_by_the_total_size_limit_and_leaves_nothing()
    {
        using var temp = new TempDir();
        var bomb = BuildZeroZip(temp.Combine("bomb.zip"), megabytes: 64);
        Assert.True(new FileInfo(bomb).Length < 200_000, "the archive itself is tiny");

        var result = await Kit.Run(Converter, "arc.extract", bomb, new() { ["maxTotalMB"] = 8 });

        Assert.False(result.Success);
        Assert.Contains("decompression bomb", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path)); // no leftover "(extracted)" folder
    }

    [Fact]
    public async Task The_default_limit_allows_ordinary_archives_but_is_finite()
    {
        Assert.Equal(10L * 1024 * 1024 * 1024, ArchiveLimits.Default.MaxTotalBytes);
        Assert.Equal(100_000, ArchiveLimits.Default.MaxEntries);

        using var temp = new TempDir();
        var zip = BuildZeroZip(temp.Combine("ok.zip"), megabytes: 16);
        var result = await Kit.Run(Converter, "arc.extract", zip);
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Too_many_entries_is_refused_before_anything_is_written()
    {
        using var temp = new TempDir();
        var entries = Enumerable.Range(0, 60).Select(i => ($"f{i}.txt", Kit.Bytes("x"))).ToArray();
        var zip = Kit.CraftZip(temp.Combine("many.zip"), entries);

        var result = await Kit.Run(Converter, "arc.extract", zip, new() { ["maxEntries"] = 50 });

        Assert.False(result.Success);
        Assert.Contains("60 entries", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Declared_sizes_are_not_trusted_the_bytes_that_come_out_are_counted()
    {
        using var temp = new TempDir();
        var bomb = BuildZeroZip(temp.Combine("liar.zip"), megabytes: 32);
        LieAboutSizes(bomb, claimed: 100);

        var result = await Kit.Run(Converter, "arc.extract", bomb, new() { ["maxTotalMB"] = 4 });

        // Whatever the zip reader does with a size it cannot trust, no more than the limit may ever reach the disk.
        if (result.Success)
        {
            var written = Directory.EnumerateFiles(Assert.Single(result.Outputs), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Assert.True(written <= 4 * 1024 * 1024, $"wrote {written} bytes");
        }
        else
        {
            Assert.Single(Directory.GetFileSystemEntries(temp.Path));
        }
    }

    [Fact]
    public void GuardedStream_counts_real_bytes_and_stops_at_the_limit()
    {
        var limits = new ArchiveLimits { MaxTotalBytes = 1_000_000 };
        var tracker = new ArchiveWalker.ProgressTracker(null, 0, new FakeSource());
        using var guarded = new GuardedStream(new ZeroStream(50_000_000), limits, tracker, CancellationToken.None);

        var buffer = new byte[64 * 1024];
        long read = 0;
        var ex = Assert.Throws<ArchiveException>(() =>
        {
            int n;
            while ((n = guarded.Read(buffer, 0, buffer.Length)) > 0) read += n;
        });

        Assert.Equal(ArchiveProblem.TooLarge, ex.Problem);
        Assert.InRange(read, 900_000, 1_000_000 + buffer.Length); // stopped within one buffer of the limit, long before 50 MB
    }

    // -------------------------------------------------------------------------------------------- failures

    [Fact]
    public async Task A_corrupt_archive_fails_with_a_message_and_leaves_nothing()
    {
        using var temp = new TempDir();
        var junk = temp.Combine("junk.zip");
        File.WriteAllBytes(junk, Kit.Noise(4000));

        var result = await Kit.Run(Converter, "arc.extract", junk);

        Assert.False(result.Success);
        Assert.Contains("junk.zip", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task A_truncated_zip_fails_cleanly()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("a.zip"), tree);
        var bytes = File.ReadAllBytes(zip);
        var cut = temp.Combine("cut.zip");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);

        var result = await Kit.Run(Converter, "arc.extract", cut);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.DoesNotContain(Directory.GetDirectories(temp.Path), d => d.EndsWith("(extracted)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_input_fails_with_a_clear_message()
    {
        using var temp = new TempDir();
        var result = await Kit.Run(Converter, "arc.extract", temp.Combine("nope.zip"));
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_extraction_and_removes_the_partial_folder()
    {
        using var temp = new TempDir();
        var zip = BuildZeroZip(temp.Combine("big.zip"), megabytes: 200);
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.05) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.Run(Converter, Kit.Job("arc.extract", [zip]), cts.Token, recorder));

        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Cancellation_before_start_does_nothing()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("a.zip"), tree);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("arc.extract", [zip]), cts.Token));
        Assert.Equal(2, Directory.GetFileSystemEntries(temp.Path).Length); // src + a.zip
    }

    [Fact]
    public async Task Progress_is_reported_and_ends_at_one()
    {
        using var temp = new TempDir();
        var zip = BuildZeroZip(temp.Combine("p.zip"), megabytes: 24);
        var recorder = new Recorder();

        var result = await Kit.Run(Converter, Kit.Job("arc.extract", [zip]), recorder: recorder);

        Assert.True(result.Success, result.Error);
        var fractions = recorder.Items.Select(p => p.Fraction).ToList();
        Assert.True(fractions.Count >= 2);
        Assert.Equal(fractions.Order(), fractions);          // never goes backwards
        Assert.Equal(1.0, fractions[^1]);
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
    }

    // -------------------------------------------------------------------------------------------- formats the library reads

    [Fact]
    public async Task Reads_a_hand_built_rar5_archive()
    {
        using var temp = new TempDir();
        var rar = temp.Combine("hand.rar");
        File.WriteAllBytes(rar, Rar5Builder.Build(("hello.txt", Kit.Bytes("hello rar")), ($"{Kit.ArabicFolder}/{Kit.ArabicFile}.txt", Kit.Bytes(Kit.ArabicText))));

        var folder = await ExtractOk(rar);

        Assert.Equal(Kit.Hash(Kit.Bytes("hello rar")), Kit.TreeHashes(folder)["hello.txt"]);
        Assert.Equal(Kit.Hash(Kit.Bytes(Kit.ArabicText)), Kit.TreeHashes(folder)[$"{Kit.ArabicFolder}/{Kit.ArabicFile}.txt"]);
    }

    [RarFact]
    public async Task Reads_a_real_rar_made_by_winrar()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var expected = Kit.BuildSampleTree(tree);
        var rar = temp.Combine("real.rar");

        var made = await Engines.Common.ProcessRunner.RunAsync(RarFactAttribute.RarExe!, ["a", "-r", "-ep1", "-ma5", "-m3", "-y", "-ed", rar, "*"],
            new Engines.Common.ProcessOptions { WorkingDirectory = tree, Timeout = TimeSpan.FromMinutes(1) });
        Assert.True(made.Success, made.StdOut + made.StdErr);

        var folder = await ExtractOk(rar);
        Assert.Equal(Kit.ExpectedHashes(expected), Kit.TreeHashes(folder));
    }

    // -------------------------------------------------------------------------------------------- helpers

    /// <summary>A zip holding one file of <paramref name="megabytes"/> MB of zeros: a few KB on disk, huge when expanded.</summary>
    internal static string BuildZeroZip(string path, int megabytes)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("zeros.bin", CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        var chunk = new byte[1024 * 1024];
        for (var i = 0; i < megabytes; i++) stream.Write(chunk);
        return path;
    }

    /// <summary>Rewrites the uncompressed size in the local and central headers so the archive claims to be small.</summary>
    private static void LieAboutSizes(string zipPath, uint claimed)
    {
        var bytes = File.ReadAllBytes(zipPath);
        for (var i = 0; i + 4 < bytes.Length - 30; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                BitConverter.GetBytes(claimed).CopyTo(bytes, i + 22);          // local header: uncompressed size
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                BitConverter.GetBytes(claimed).CopyTo(bytes, i + 24);          // central directory: uncompressed size
        }
        File.WriteAllBytes(zipPath, bytes);
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeSource : ArchiveSource
    {
        public override SourceKind Kind => SourceKind.Archive;
        public override IReadOnlyList<SourceEntry>? Listing => null;
        public override double PhysicalProgress => 0;
        public override IEnumerable<SourceItem> Items(CancellationToken cancellationToken) => [];
    }
}
