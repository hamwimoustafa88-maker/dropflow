using System.Security.Cryptography;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.General;
using DropFlow.Engines.Common;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

public class HashTests
{
    private static HashConverter Converter => new();

    // Known-answer vectors for the ASCII string "abc" (FIPS 180 / RFC 1321).
    private const string Md5Abc = "900150983cd24fb0d6963f7d28e17f72";
    private const string Sha1Abc = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string Sha256Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Sha512Abc = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";

    [Fact]
    public void Supports_every_file_and_has_the_algorithm_option()
    {
        Assert.True(Converter.Supports("gen.hash", "png"));
        Assert.True(Converter.Supports("gen.hash", ""));
        Assert.True(Converter.Supports("gen.hash", "zip"));
        Assert.False(Converter.Supports("gen.base64", "png"));
        Assert.Equal(EngineId.None, Converter.EngineFor("gen.hash"));

        var option = Assert.Single(Converter.OptionsFor("gen.hash"));
        Assert.Equal("algorithms", option.Key);
        Assert.Equal("sha256", option.Default);
        Assert.Contains("all", option.Choices!);
    }

    [Fact]
    public async Task Writes_a_sha256sum_compatible_file_named_after_the_input()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");

        var result = await Kit.Run(Converter, "gen.hash", file);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("abc (hash).txt", Path.GetFileName(output));
        Assert.Equal($"{Sha256Abc}  abc.txt\n", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("md5", Md5Abc)]
    [InlineData("sha1", Sha1Abc)]
    [InlineData("sha256", Sha256Abc)]
    [InlineData("sha512", Sha512Abc)]
    [InlineData("SHA-256", Sha256Abc)]
    public async Task Each_algorithm_matches_the_published_test_vector(string algorithm, string expected)
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");

        var result = await Kit.Run(Converter, "gen.hash", file, new() { ["algorithms"] = algorithm });

        Assert.True(result.Success, result.Error);
        Assert.Equal($"{expected}  abc.txt\n", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("md5,sha1,sha256,sha512")]
    [InlineData("sha512 sha256 sha1 md5")]
    public async Task Several_algorithms_are_listed_in_a_fixed_order_with_headers(string algorithms)
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");

        var result = await Kit.Run(Converter, "gen.hash", file, new() { ["algorithms"] = algorithms });

        Assert.True(result.Success, result.Error);
        var expected = $"# MD5\n{Md5Abc}  abc.txt\n# SHA-1\n{Sha1Abc}  abc.txt\n# SHA-256\n{Sha256Abc}  abc.txt\n# SHA-512\n{Sha512Abc}  abc.txt\n";
        Assert.Equal(expected, File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task A_subset_keeps_only_the_requested_blocks()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");

        var result = await Kit.Run(Converter, "gen.hash", file, new() { ["algorithms"] = "md5,sha256" });

        Assert.Equal($"# MD5\n{Md5Abc}  abc.txt\n# SHA-256\n{Sha256Abc}  abc.txt\n", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task An_empty_file_has_the_well_known_empty_hash()
    {
        using var temp = new TempDir();
        var file = temp.Combine("empty.bin");
        File.WriteAllBytes(file, []);

        var result = await Kit.Run(Converter, "gen.hash", file);

        Assert.StartsWith("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  empty.bin", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Arabic_file_names_work_in_the_input_the_output_and_the_content_line()
    {
        using var temp = new TempDir();
        var file = temp.WriteText($"{Kit.ArabicFile}.txt", "abc");

        var result = await Kit.Run(Converter, "gen.hash", file);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal($"{Kit.ArabicFile} (hash).txt", Path.GetFileName(output));
        Assert.Equal($"{Sha256Abc}  {Kit.ArabicFile}.txt\n", File.ReadAllText(output, new System.Text.UTF8Encoding(false, true)));
        Assert.False(File.ReadAllBytes(output).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM: sha256sum -c would choke on it");
    }

    [Fact]
    public async Task Matches_what_windows_certutil_computes()
    {
        var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
        if (!File.Exists(certutil)) return;

        using var temp = new TempDir();
        var file = temp.Combine("data.bin");
        File.WriteAllBytes(file, Kit.Noise(3_000_000));

        var result = await Kit.Run(Converter, "gen.hash", file, new() { ["algorithms"] = "sha256" });
        var ours = File.ReadAllText(Assert.Single(result.Outputs)).Split("  ")[0];

        var theirs = await ProcessRunner.RunAsync(certutil, ["-hashfile", file, "SHA256"]);
        Assert.True(theirs.Success, theirs.StdOut);
        Assert.Contains(ours, theirs.StdOut.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Big_files_hash_correctly_in_one_pass()
    {
        using var temp = new TempDir();
        var file = temp.Combine("big.bin");
        var data = Kit.Noise(5_500_000);       // not a multiple of the 1 MiB read buffer
        File.WriteAllBytes(file, data);

        var result = await Kit.Run(Converter, "gen.hash", file, new() { ["algorithms"] = "all" });

        var text = File.ReadAllText(Assert.Single(result.Outputs));
        Assert.Contains($"{Convert.ToHexStringLower(MD5.HashData(data))}  big.bin", text);
        Assert.Contains($"{Convert.ToHexStringLower(SHA1.HashData(data))}  big.bin", text);
        Assert.Contains($"{Convert.ToHexStringLower(SHA256.HashData(data))}  big.bin", text);
        Assert.Contains($"{Convert.ToHexStringLower(SHA512.HashData(data))}  big.bin", text);
    }

    [Fact]
    public async Task Never_overwrites_an_earlier_hash_file_or_the_input()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");
        File.WriteAllText(temp.Combine("abc (hash).txt"), "PRECIOUS");

        var result = await Kit.Run(Converter, "gen.hash", file);

        Assert.Equal("abc (hash 2).txt", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("abc (hash).txt")));
        Assert.Equal("abc", File.ReadAllText(file));
    }

    [Fact]
    public async Task Honours_the_output_directory()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");
        var elsewhere = Path.Combine(temp.Path, "hashes");

        var result = await Kit.Run(Converter, "gen.hash", file, outputDirectory: elsewhere);

        Assert.Equal(elsewhere, Path.GetDirectoryName(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Unknown_algorithms_missing_files_and_folders_fail_clearly()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("abc.txt", "abc");

        var unknown = await Kit.Run(Converter, "gen.hash", file, new() { ["algorithms"] = "crc32" });
        Assert.False(unknown.Success);
        Assert.Contains("crc32", unknown.Error);

        var missing = await Kit.Run(Converter, "gen.hash", temp.Combine("ghost.txt"));
        Assert.False(missing.Success);
        Assert.Contains("not found", missing.Error, StringComparison.OrdinalIgnoreCase);

        var folder = await Kit.Run(Converter, "gen.hash", Kit.NewDir(temp, "dir"));
        Assert.False(folder.Success);
        Assert.Contains("Folders", folder.Error);

        Assert.Equal(["abc.txt", "dir"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Cancellation_stops_hashing_and_leaves_no_output()
    {
        using var temp = new TempDir();
        var file = temp.Combine("huge.bin");
        using (var fs = File.Create(file)) fs.SetLength(600L * 1024 * 1024); // sparse zeros: slow enough to cancel, cheap to make
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.02) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("gen.hash", [file], new() { ["algorithms"] = "all" }), cts.Token, recorder));

        Assert.Equal(["huge.bin"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Algorithm_names_parse_forgivingly_and_in_canonical_order()
    {
        Assert.Equal(["sha256"], HashAlgorithms.Parse(null).Select(a => a.Key));
        Assert.Equal(["sha256"], HashAlgorithms.Parse("  ").Select(a => a.Key));
        Assert.Equal(["md5", "sha256"], HashAlgorithms.Parse("SHA-256; MD5").Select(a => a.Key));
        Assert.Equal(["md5", "sha1", "sha256", "sha512"], HashAlgorithms.Parse("all").Select(a => a.Key));
        Assert.Throws<ArgumentException>(() => HashAlgorithms.Parse("md5,whirlpool"));
    }
}
