using System.Formats.Tar;
using System.IO.Compression;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

public class ConvertTests
{
    private static ArchiveConverter Converter => new(Kit.RealEngines);

    private static readonly string[] Inputs = ["zip", "tar", "gz", "tgz", "rar", "7z"];
    private static readonly string[] Targets = ["zip", "tar", "gz", "7z"];

    // ---------------------------------------------------------------- Supports / options contract

    public static TheoryData<string, string, bool> SupportMatrix()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var target in Targets)
            foreach (var input in Inputs)
                data.Add($"convert:{target}", input, input != target);
        return data;
    }

    [Theory]
    [MemberData(nameof(SupportMatrix))]
    public void Supports_every_pair_except_a_format_to_itself(string operation, string input, bool expected) =>
        Assert.Equal(expected, Converter.Supports(operation, input));

    [Theory]
    [InlineData("convert:rar", "zip")]       // rar is read-only
    [InlineData("convert:tgz", "zip")]       // not an output in the catalog
    [InlineData("convert:png", "zip")]
    [InlineData("convert:zip", "png")]
    [InlineData("convert:zip", "pdf")]
    [InlineData("convert:zip", "")]
    [InlineData("arc.extract", "png")]
    [InlineData("arc.extract", "docx")]
    [InlineData("img.compress", "zip")]
    public void Does_not_claim_what_it_cannot_do(string operation, string input) =>
        Assert.False(Converter.Supports(operation, input));

    [Theory]
    [InlineData("zip")]
    [InlineData(".ZIP")]
    [InlineData("tar.gz")]
    [InlineData("TGZ")]
    [InlineData("rar")]
    [InlineData("7z")]
    [InlineData("gz")]
    [InlineData("tar")]
    public void Extract_supports_every_archive_input_in_any_spelling(string input) =>
        Assert.True(Converter.Supports("arc.extract", input));

    [Theory]
    [InlineData("")]
    [InlineData("png")]
    [InlineData("zip")]
    [InlineData("docx")]
    public void Zip_tool_accepts_any_file_or_folder(string input) =>
        Assert.True(Converter.Supports("arc.zip", input));

    [Fact]
    public void Operations_match_the_catalog_and_only_7z_needs_an_engine()
    {
        Assert.Equal(
            new[] { "convert:7z", "convert:gz", "convert:tar", "convert:zip", "arc.extract", "arc.zip" }.Order(),
            Converter.Operations.Order());

        foreach (var op in Converter.Operations)
            Assert.Equal(op == "convert:7z" ? EngineId.SevenZip : EngineId.None, Converter.EngineFor(op));
    }

    [Fact]
    public void Only_the_zip_tool_asks_for_options_first()
    {
        var level = Assert.Single(Converter.OptionsFor("arc.zip"));
        Assert.Equal("level", level.Key);
        Assert.Equal(ToolOptionKind.Range, level.Kind);
        Assert.Equal(0, level.Min);
        Assert.Equal(9, level.Max);
        foreach (var op in Converter.Operations.Where(o => o != "arc.zip"))
            Assert.Empty(Converter.OptionsFor(op));
    }

    // ---------------------------------------------------------------- the matrix: every pair really works, checked with a different reader

    public static TheoryData<string, string> PureManagedPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var input in new[] { "zip", "tar", "tgz", "gz" })
            foreach (var target in new[] { "zip", "tar", "gz" })
                if (input != target) data.Add(input, target);
        return data;
    }

    [Theory]
    [MemberData(nameof(PureManagedPairs))]
    public async Task Converts_between_zip_tar_gz_and_tgz_losing_nothing(string input, string target)
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var (source, expected) = MakeSource(temp, tree, files, input);

        var result = await Kit.Run(Converter, $"convert:{target}", source);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output));
        Assert.Equal(temp.Path, Path.GetDirectoryName(output));
        AssertContent(output, target, expected, expectSingleFileGz: input == "gz" || IsSingleFile(expected, target));
        Assert.True(File.Exists(source)); // the input is never consumed
    }

    [Fact]
    public async Task Converts_from_rar_to_every_output()
    {
        using var temp = new TempDir();
        var rar = temp.Combine("hand.rar");
        var content = new (string, byte[])[] { ("a.txt", Kit.Bytes("alpha")), ($"{Kit.ArabicFolder}/{Kit.ArabicFile}.txt", Kit.Bytes(Kit.ArabicText)) };
        File.WriteAllBytes(rar, Rar5Builder.Build(content));
        var expected = new SortedDictionary<string, string>(content.ToDictionary(c => c.Item1, c => Kit.Hash(c.Item2)), StringComparer.Ordinal);

        foreach (var target in new[] { "zip", "tar", "gz" })
        {
            var result = await Kit.Run(Converter, $"convert:{target}", rar);
            Assert.True(result.Success, $"{target}: {result.Error}");
            AssertContent(Assert.Single(result.Outputs), target, expected, expectSingleFileGz: false);
        }
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task Converts_everything_to_7z_and_7zip_itself_agrees()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var expected = Kit.ExpectedHashes(files);

        foreach (var input in new[] { "zip", "tar", "tgz" })
        {
            var (source, _) = MakeSource(temp, tree, files, input);
            var result = await Kit.Run(Converter, "convert:7z", source);

            Assert.True(result.Success, $"{input}: {result.Error}");
            var archive = Assert.Single(result.Outputs);
            Assert.EndsWith(".7z", archive);

            // two independent readers: SharpCompress and the real 7z.exe
            Assert.Equal(expected, Kit.ReadWithSharpCompress(archive));
            var names = await Kit.SevenZipFileNames(archive);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), names);
            var test = await Kit.SevenZip(SevenZipCommand.TestArguments(archive).ToArray());
            Assert.True(test.Success, test.StdOut);
        }
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task Converts_gz_and_rar_to_7z()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var gz = Kit.MakeGzipOfFile(temp.Combine("readme.txt.gz"), Path.Combine(tree, "readme.txt"));
        var rar = temp.Combine("hand.rar");
        File.WriteAllBytes(rar, Rar5Builder.Build(("one.txt", Kit.Bytes("one"))));

        var fromGz = await Kit.Run(Converter, "convert:7z", gz);
        Assert.True(fromGz.Success, fromGz.Error);
        Assert.Equal(["readme.txt"], Kit.ReadWithSharpCompress(Assert.Single(fromGz.Outputs)).Keys);

        var fromRar = await Kit.Run(Converter, "convert:7z", rar);
        Assert.True(fromRar.Success, fromRar.Error);
        Assert.Equal(Kit.Hash(Kit.Bytes("one")), Kit.ReadWithSharpCompress(Assert.Single(fromRar.Outputs))["one.txt"]);
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task Reads_7z_archives_made_by_7zip_with_several_methods()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var expected = Kit.ExpectedHashes(files);

        foreach (var (label, switches) in new[] { ("default", Array.Empty<string>()), ("non-solid", ["-ms=off"]), ("ppmd", ["-m0=PPMd"]), ("copy", ["-mx=0"]), ("bzip2", ["-m0=BZip2"]), ("lzma", ["-m0=LZMA"]) })
        {
            var archive = temp.Combine($"{label}.7z");
            var args = new List<string> { "a", "-t7z" }; args.AddRange(switches); args.AddRange(["-y", "--", archive, "*"]);
            var exe = Kit.RealEngines.Locate(EngineId.SevenZip)!;
            var made = await Engines.Common.ProcessRunner.RunAsync(exe, args, new Engines.Common.ProcessOptions { WorkingDirectory = tree });
            Assert.True(made.Success, made.StdOut);

            var extracted = await Kit.Run(Converter, "arc.extract", archive);
            Assert.True(extracted.Success, $"{label}: {extracted.Error}");
            Assert.Equal(expected, Kit.TreeHashes(Assert.Single(extracted.Outputs)));

            var toZip = await Kit.Run(Converter, "convert:zip", archive);
            Assert.True(toZip.Success, $"{label}: {toZip.Error}");
            Assert.Equal(expected, Kit.ReadZipWithBcl(Assert.Single(toZip.Outputs)));
        }
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task Converts_7z_to_zip_tar_and_gz()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var expected = Kit.ExpectedHashes(files);
        var (archive, _) = MakeSource(temp, tree, files, "7z");

        foreach (var target in new[] { "zip", "tar", "gz" })
        {
            var result = await Kit.Run(Converter, $"convert:{target}", archive);
            Assert.True(result.Success, $"{target}: {result.Error}");
            AssertContent(Assert.Single(result.Outputs), target, expected, expectSingleFileGz: false);
        }
    }

    [Fact]
    public async Task Seven_zip_output_without_the_engine_fails_naming_it()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("a.zip"), tree);

        var result = await Kit.Run(new ArchiveConverter(new NoEngines()), "convert:7z", zip);

        Assert.False(result.Success);
        Assert.Contains("7-Zip", result.Error);
        Assert.Single(Directory.GetFiles(temp.Path)); // nothing created
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task Seven_zip_cancellation_leaves_no_partial_archive_or_staging_files()
    {
        using var temp = new TempDir();
        var zip = ExtractTests.BuildZeroZip(temp.Combine("big.zip"), megabytes: 150);
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.1) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("convert:7z", [zip]), cts.Token, recorder));

        Assert.Equal(["big.zip"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------- what convert:gz means

    [Fact]
    public async Task Gz_of_a_zip_with_one_top_level_file_is_a_plain_gzip_of_that_file()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("single.zip"), ("report.txt", Kit.Bytes("only file")));

        var result = await Kit.Run(Converter, "convert:gz", zip);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("single (converted).gz", Path.GetFileName(output));
        using var gz = new GZipStream(File.OpenRead(output), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        Assert.Equal("only file", reader.ReadToEnd());
    }

    [Fact]
    public async Task Gz_of_a_multi_file_archive_is_a_tar_gz_of_everything()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var zip = Kit.MakeZip(temp.Combine("many.zip"), tree);

        var result = await Kit.Run(Converter, "convert:gz", zip);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("many (converted).tar.gz", Path.GetFileName(output));
        Assert.Equal(Kit.ExpectedHashes(files), Kit.ReadTarGzWithBcl(output));
    }

    [Fact]
    public async Task Gz_of_a_single_file_inside_a_folder_keeps_the_folder_so_it_becomes_a_tar_gz()
    {
        using var temp = new TempDir();
        var zip = Kit.CraftZip(temp.Combine("nested.zip"), ("folder/file.txt", Kit.Bytes("x")));

        var result = await Kit.Run(Converter, "convert:gz", zip);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(".tar.gz", output);
        Assert.Equal(["folder/file.txt"], Kit.ReadTarGzWithBcl(output).Keys);
    }

    [Fact]
    public async Task Gz_of_a_tar_gzips_the_tar_itself_byte_for_byte()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var tar = Kit.MakeTar(temp.Combine("a.tar"), tree);

        var result = await Kit.Run(Converter, "convert:gz", tar);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("a (converted).tar.gz", Path.GetFileName(output));
        using var gz = new GZipStream(File.OpenRead(output), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        Assert.Equal(File.ReadAllBytes(tar), ms.ToArray());
    }

    [Fact]
    public async Task Tgz_to_tar_is_plain_decompression()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var tgz = Kit.MakeTarGz(temp.Combine("a.tgz"), tree);
        var originalTar = Kit.MakeTar(temp.Combine("orig.tar"), tree);

        var result = await Kit.Run(Converter, "convert:tar", tgz);

        Assert.True(result.Success, result.Error);
        Assert.Equal(File.ReadAllBytes(originalTar), File.ReadAllBytes(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Gz_of_a_gzipped_single_file_to_zip_names_the_entry_after_the_file()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("notes.txt", "gz content");
        var gz = Kit.MakeGzipOfFile(temp.Combine("notes.txt.gz"), file);

        var result = await Kit.Run(Converter, "convert:zip", gz);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["notes.txt"], Kit.ReadZipWithBcl(Assert.Single(result.Outputs)).Keys);
    }

    // ---------------------------------------------------------------- names, safety, unicode

    [Fact]
    public async Task Unicode_names_survive_a_full_round_trip_through_every_format()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var expected = Kit.ExpectedHashes(files);
        var zip = Kit.MakeZip(temp.Combine("دورة كاملة.zip"), tree);

        var tar = Assert.Single((await Kit.Run(Converter, "convert:tar", zip)).Outputs);
        var back = Assert.Single((await Kit.Run(Converter, "convert:zip", tar)).Outputs);
        var targz = Assert.Single((await Kit.Run(Converter, "convert:gz", back)).Outputs);
        var finalZip = Assert.Single((await Kit.Run(Converter, "convert:zip", targz)).Outputs);

        Assert.Equal(expected, Kit.ReadZipWithBcl(finalZip));
        Assert.Contains("دورة كاملة (converted)", Path.GetFileName(finalZip));
    }

    [Fact]
    public async Task Written_zips_flag_non_ascii_names_as_utf8_so_every_tool_reads_them()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var tar = Kit.MakeTar(temp.Combine("a.tar"), tree);

        var zip = Assert.Single((await Kit.Run(Converter, "convert:zip", tar)).Outputs);

        // Walk the central directory and check general-purpose bit 11 (0x0800) for every entry whose name is not ASCII.
        var bytes = File.ReadAllBytes(zip);
        var checkedEntries = 0;
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B || bytes[i + 2] != 0x01 || bytes[i + 3] != 0x02) continue;
            var flags = BitConverter.ToUInt16(bytes, i + 8);
            var nameLength = BitConverter.ToUInt16(bytes, i + 28);
            var name = bytes.AsSpan(i + 46, nameLength);
            if (name.ToArray().Any(b => b >= 0x80))
            {
                Assert.True((flags & 0x0800) != 0, "UTF-8 flag missing for a non-ASCII entry name");
                checkedEntries++;
            }
        }
        Assert.True(checkedEntries >= 2);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_converted_file()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var tar = Kit.MakeTar(temp.Combine("a.tar"), tree);
        File.WriteAllText(temp.Combine("a (converted).zip"), "PRECIOUS");

        var first = Assert.Single((await Kit.Run(Converter, "convert:zip", tar)).Outputs);
        var second = Assert.Single((await Kit.Run(Converter, "convert:zip", tar)).Outputs);

        Assert.Equal("a (converted 2).zip", Path.GetFileName(first));
        Assert.Equal("a (converted 3).zip", Path.GetFileName(second));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("a (converted).zip")));
    }

    [Fact]
    public async Task Honours_the_output_directory()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var tar = Kit.MakeTar(temp.Combine("a.tar"), tree);
        var elsewhere = Path.Combine(temp.Path, "elsewhere");

        var result = await Kit.Run(Converter, "convert:zip", tar, outputDirectory: elsewhere);

        Assert.True(result.Success, result.Error);
        Assert.Equal(elsewhere, Path.GetDirectoryName(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Conversion_refuses_hostile_entry_names_instead_of_laundering_them_into_a_new_archive()
    {
        using var temp = new TempDir();
        var evil = Kit.CraftZip(temp.Combine("evil.zip"), ("ok.txt", Kit.Bytes("ok")), ("../../escape.txt", Kit.Bytes("PWNED")));

        foreach (var target in new[] { "tar", "gz" })
        {
            var result = await Kit.Run(Converter, $"convert:{target}", evil);
            Assert.False(result.Success);
            Assert.Contains("Unsafe path", result.Error);
        }

        Assert.Equal(["evil.zip"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName)); // failed conversions leave no output
    }

    [Fact]
    public async Task Conversion_drops_symlinks_and_reports_them()
    {
        using var temp = new TempDir();
        var zipPath = temp.Combine("links.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Kit.AddZipSymlink(zip, "evil-link", "/etc/passwd");
            using var s = zip.CreateEntry("real.txt").Open();
            s.Write(Kit.Bytes("real"));
        }

        var result = await Kit.Run(Converter, "convert:tar", zipPath);

        Assert.True(result.Success, result.Error);
        Assert.Contains("1 link(s) skipped", result.Note);
        Assert.Equal(["real.txt"], Kit.ReadTarFileWithBcl(Assert.Single(result.Outputs)).Keys);
    }

    [Fact]
    public async Task A_booby_trapped_tar_is_not_gzipped_byte_for_byte()
    {
        using var temp = new TempDir();
        var tarPath = temp.Combine("trap.tar");
        using (var fs = File.Create(tarPath))
        using (var writer = new System.Formats.Tar.TarWriter(fs, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "ok.txt") { DataStream = new MemoryStream(Kit.Bytes("ok")) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "CON") { DataStream = new MemoryStream(Kit.Bytes("device")) });
        }

        var result = await Kit.Run(Converter, "convert:gz", tarPath);

        Assert.True(result.Success, result.Error);
        Assert.Contains("link(s) skipped", result.Note);
        var entries = Kit.ReadTarGzWithBcl(Assert.Single(result.Outputs));
        Assert.Equal(["_CON", "ok.txt"], entries.Keys);        // link dropped, device name neutralised

        var hostile = temp.Combine("hostile.tar");
        using (var fs = File.Create(hostile))
        using (var writer = new System.Formats.Tar.TarWriter(fs, TarEntryFormat.Pax))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../../escape.txt") { DataStream = new MemoryStream(Kit.Bytes("PWNED")) });
        var refused = await Kit.Run(Converter, "convert:gz", hostile);
        Assert.False(refused.Success);
        Assert.Contains("Unsafe path", refused.Error);
    }

    [Fact]
    public async Task A_bomb_is_stopped_during_conversion_too()
    {
        using var temp = new TempDir();
        var bomb = ExtractTests.BuildZeroZip(temp.Combine("bomb.zip"), megabytes: 64);

        var result = await Kit.Run(Converter, "convert:tar", bomb, new() { ["maxTotalMB"] = 8 });

        Assert.False(result.Success);
        Assert.Contains("decompression bomb", result.Error);
        Assert.Equal(["bomb.zip"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_corrupt_input_fails_without_creating_output()
    {
        using var temp = new TempDir();
        var junk = temp.Combine("junk.tar");
        File.WriteAllBytes(junk, Kit.Noise(3000));

        foreach (var target in new[] { "zip", "gz" })
        {
            var result = await Kit.Run(Converter, $"convert:{target}", junk);
            Assert.False(result.Success, target);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        Assert.Equal(["junk.tar"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_output()
    {
        using var temp = new TempDir();
        var zip = ExtractTests.BuildZeroZip(temp.Combine("big.zip"), megabytes: 200);
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.05) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("convert:tar", [zip]), cts.Token, recorder));

        Assert.Equal(["big.zip"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsSingleFile(SortedDictionary<string, string> expected, string target) =>
        target == "gz" && expected.Count == 1 && !expected.Keys.Single().Contains('/');

    /// <summary>Builds a source archive of the given kind (made by something other than the code under test) and its expected content.</summary>
    private static (string Path, SortedDictionary<string, string> Expected) MakeSource(TempDir temp, string tree, Dictionary<string, byte[]> files, string kind)
    {
        var all = Kit.ExpectedHashes(files);
        switch (kind)
        {
            case "zip": return (Kit.MakeZip(temp.Combine($"in-{kind}.zip"), tree), all);
            case "tar": return (Kit.MakeTar(temp.Combine($"in-{kind}.tar"), tree), all);
            case "tgz": return (Kit.MakeTarGz(temp.Combine($"in-{kind}.tgz"), tree), all);
            case "gz":
                var single = Path.Combine(tree, "readme.txt");
                return (Kit.MakeGzipOfFile(temp.Combine("readme.txt.gz"), single),
                    new SortedDictionary<string, string>(StringComparer.Ordinal) { ["readme.txt"] = Kit.Hash(File.ReadAllBytes(single)) });
            case "7z":
                var archive = temp.Combine("in-7z.7z");
                var exe = Kit.RealEngines.Locate(EngineId.SevenZip)!;
                var made = Engines.Common.ProcessRunner.RunAsync(exe, ["a", "-t7z", "-y", "--", archive, "*"], new Engines.Common.ProcessOptions { WorkingDirectory = tree }).GetAwaiter().GetResult();
                Assert.True(made.Success, made.StdOut);
                return (archive, all);
            default: throw new ArgumentException(kind);
        }
    }

    private static void AssertContent(string output, string target, SortedDictionary<string, string> expected, bool expectSingleFileGz)
    {
        switch (target)
        {
            case "zip":
                Assert.EndsWith(".zip", output);
                Assert.Equal(expected, Kit.ReadZipWithBcl(output));
                break;
            case "tar":
                Assert.EndsWith(".tar", output);
                Assert.Equal(expected, Kit.ReadTarFileWithBcl(output));
                break;
            case "gz" when expectSingleFileGz:
                Assert.EndsWith(".gz", output);
                Assert.False(output.EndsWith(".tar.gz", StringComparison.Ordinal));
                using (var gz = new GZipStream(File.OpenRead(output), CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    gz.CopyTo(ms);
                    Assert.Equal(expected.Values.Single(), Kit.Hash(ms.ToArray()));
                }
                break;
            case "gz":
                Assert.EndsWith(".tar.gz", output);
                Assert.Equal(expected, Kit.ReadTarGzWithBcl(output));
                break;
            default:
                throw new ArgumentException(target);
        }
    }
}
