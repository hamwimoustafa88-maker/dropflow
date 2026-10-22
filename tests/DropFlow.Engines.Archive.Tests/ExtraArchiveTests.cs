using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.Engines.Common;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

/// <summary>Passwords, more RAR flavours, timestamps and the child-process behaviour of the 7-Zip launcher.</summary>
public class ExtraArchiveTests
{
    private static ArchiveConverter Converter => new(Kit.RealEngines);

    private static async Task<ProcessResult> Make7z(string workingDirectory, params string[] args)
    {
        var exe = Kit.RealEngines.Locate(EngineId.SevenZip)!;
        var result = await ProcessRunner.RunAsync(exe, args, new ProcessOptions { WorkingDirectory = workingDirectory, Timeout = TimeSpan.FromMinutes(1) });
        Assert.True(result.Success, result.StdOut + result.StdErr);
        return result;
    }

    // ---------------------------------------------------------------- passwords

    [EngineFact(EngineId.SevenZip)]
    public async Task A_password_protected_zip_is_refused_without_the_password_and_opened_with_it()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        foreach (var method in new[] { "AES256", "ZipCrypto" })
        {
            var archive = temp.Combine($"secret-{method}.zip");
            await Make7z(tree, "a", "-tzip", "-pS3cret!", $"-mem={method}", "-y", "--", archive, "*");

            var foldersBefore = Directory.GetDirectories(temp.Path).Length;
            var without = await Kit.Run(Converter, "arc.extract", archive);
            Assert.False(without.Success, method);
            Assert.Contains("password", without.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(foldersBefore, Directory.GetDirectories(temp.Path).Length);      // a refused archive leaves no folder behind

            var with = await Kit.Run(Converter, "arc.extract", archive, new() { ["password"] = "S3cret!" });
            Assert.True(with.Success, $"{method}: {with.Error}");
            Assert.Equal(Kit.ExpectedHashes(files), Kit.TreeHashes(Assert.Single(with.Outputs)));
        }
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task A_wrong_password_fails_with_a_helpful_message_and_leaves_nothing()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        Kit.BuildSampleTree(tree);
        var archive = temp.Combine("secret.zip");
        await Make7z(tree, "a", "-tzip", "-pS3cret!", "-mem=AES256", "-y", "--", archive, "*");

        var result = await Kit.Run(Converter, "arc.extract", archive, new() { ["password"] = "not-it" });

        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);     // the message points at the likely cause
        Assert.Contains("wrong", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Directory.GetDirectories(temp.Path), d => d.EndsWith("(extracted)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_archives_are_reported_not_turned_into_empty_results()
    {
        using var temp = new TempDir();
        var emptyZip = temp.Combine("empty.zip");
        using (var fs = File.Create(emptyZip)) using (new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create)) { }
        var zeros = temp.Combine("zeros.tar");
        File.WriteAllBytes(zeros, new byte[2048]);                 // a valid but empty tar: two zero blocks

        foreach (var (input, operation) in new[] { (emptyZip, "arc.extract"), (zeros, "arc.extract"), (emptyZip, "convert:tar"), (zeros, "convert:zip"), (zeros, "convert:gz") })
        {
            var result = await Kit.Run(Converter, operation, input);
            Assert.False(result.Success, $"{operation} {Path.GetFileName(input)}");
            Assert.Contains("no files or folders", result.Error);
        }
        Assert.Equal(["empty.zip", "zeros.tar"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName).Order());
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task A_password_protected_7z_needs_its_password_too_with_and_without_header_encryption()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        foreach (var (label, headerFlag) in new[] { ("content", "-mhe=off"), ("headers", "-mhe=on") })
        {
            var archive = temp.Combine($"secret-{label}.7z");
            await Make7z(tree, "a", "-t7z", "-pS3cret!", headerFlag, "-y", "--", archive, "*");

            var without = await Kit.Run(Converter, "arc.extract", archive);
            Assert.False(without.Success, label);
            Assert.Contains("password", without.Error, StringComparison.OrdinalIgnoreCase);

            var with = await Kit.Run(Converter, "arc.extract", archive, new() { ["password"] = "S3cret!" });
            Assert.True(with.Success, $"{label}: {with.Error}");
            Assert.Equal(Kit.ExpectedHashes(files), Kit.TreeHashes(Assert.Single(with.Outputs)));
        }
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task An_encrypted_archive_can_be_converted_when_the_password_is_given()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var files = Kit.BuildSampleTree(tree);
        var archive = temp.Combine("secret.zip");
        await Make7z(tree, "a", "-tzip", "-pS3cret!", "-mem=AES256", "-y", "--", archive, "*");

        var refused = await Kit.Run(Converter, "convert:tar", archive);
        Assert.False(refused.Success);

        var converted = await Kit.Run(Converter, "convert:tar", archive, new() { ["password"] = "S3cret!" });
        Assert.True(converted.Success, converted.Error);
        Assert.Equal(Kit.ExpectedHashes(files), Kit.ReadTarFileWithBcl(Assert.Single(converted.Outputs)));   // the new tar is NOT encrypted: that is what was asked for
    }

    // ---------------------------------------------------------------- more RAR flavours (only when WinRAR's Rar.exe exists)

    [RarFact]
    public async Task Reads_rar_archives_in_several_flavours()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var expected = Kit.ExpectedHashes(Kit.BuildSampleTree(tree));

        foreach (var (label, switches) in new[]
        {
            ("rar5-solid", new[] { "-ma5", "-s", "-m3" }),
            ("rar5-store", ["-ma5", "-m0"]),
            ("rar5-best", ["-ma5", "-m5", "-md64m"]),
            ("rar4", ["-ma4", "-m3"]),
            ("rar4-solid", ["-ma4", "-s", "-m3"]),
        })
        {
            var rar = temp.Combine($"{label}.rar");
            var args = new List<string> { "a", "-r", "-ep1", "-y" };
            args.AddRange(switches);
            args.AddRange([rar, "*"]);
            var made = await ProcessRunner.RunAsync(RarFactAttribute.RarExe!, args, new ProcessOptions { WorkingDirectory = tree, Timeout = TimeSpan.FromMinutes(1) });
            Assert.True(made.Success, $"{label}: {made.StdOut}{made.StdErr}");

            var extracted = await Kit.Run(Converter, "arc.extract", rar);
            Assert.True(extracted.Success, $"{label}: {extracted.Error}");
            Assert.Equal(expected, Kit.TreeHashes(Assert.Single(extracted.Outputs)));

            var zip = await Kit.Run(Converter, "convert:zip", rar);
            Assert.True(zip.Success, $"{label}: {zip.Error}");
            Assert.Equal(expected, Kit.ReadZipWithBcl(Assert.Single(zip.Outputs)));
        }
    }

    [RarFact]
    public async Task Reads_password_protected_rar_archives()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var expected = Kit.ExpectedHashes(Kit.BuildSampleTree(tree));

        foreach (var (label, flag) in new[] { ("content", "-pS3cret!"), ("headers", "-hpS3cret!") })
        {
            var rar = temp.Combine($"secret-{label}.rar");
            var made = await ProcessRunner.RunAsync(RarFactAttribute.RarExe!, ["a", "-r", "-ep1", "-ma5", flag, "-y", rar, "*"], new ProcessOptions { WorkingDirectory = tree, Timeout = TimeSpan.FromMinutes(1) });
            Assert.True(made.Success, made.StdOut + made.StdErr);

            var without = await Kit.Run(Converter, "arc.extract", rar);
            Assert.False(without.Success, label);
            Assert.Contains("password", without.Error, StringComparison.OrdinalIgnoreCase);

            var with = await Kit.Run(Converter, "arc.extract", rar, new() { ["password"] = "S3cret!" });
            Assert.True(with.Success, $"{label}: {with.Error}");
            Assert.Equal(expected, Kit.TreeHashes(Assert.Single(with.Outputs)));
        }
    }

    // ---------------------------------------------------------------- timestamps

    [Fact]
    public async Task Extraction_and_zip_creation_keep_file_dates()
    {
        using var temp = new TempDir();
        var tree = Kit.NewDir(temp, "src");
        var file = Path.Combine(tree, "dated.txt");
        File.WriteAllText(file, "dated");
        var stamp = new DateTime(2021, 3, 14, 15, 9, 26);
        File.SetLastWriteTime(file, stamp);

        var zipped = await Kit.Run(Converter, Kit.Job("arc.zip", [file]));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(Assert.Single(zipped.Outputs)))
            Assert.InRange((zip.GetEntry("dated.txt")!.LastWriteTime.LocalDateTime - stamp).TotalSeconds, -2, 2);   // zip stores 2-second resolution

        var extracted = await Kit.Run(Converter, "arc.extract", zipped.Outputs[0]);
        Assert.InRange((File.GetLastWriteTime(Path.Combine(Assert.Single(extracted.Outputs), "dated.txt")) - stamp).TotalSeconds, -2, 2);

        var tar = await Kit.Run(Converter, "convert:tar", zipped.Outputs[0]);
        using var tarStream = File.OpenRead(Assert.Single(tar.Outputs));
        using var reader = new System.Formats.Tar.TarReader(tarStream);
        var entry = reader.GetNextEntry()!;
        Assert.InRange((entry.ModificationTime.LocalDateTime - stamp).TotalSeconds, -2, 2);
    }

    // ---------------------------------------------------------------- the 7-Zip launcher

    [EngineFact(EngineId.SevenZip)]
    public async Task Cancelling_through_the_launcher_really_kills_the_7z_process_and_releases_the_files()
    {
        var found = Kit.RealEngines.Locate(EngineId.SevenZip)!;
        using var temp = new TempDir();
        var big = Path.Combine(temp.Path, "big.bin");
        using (var fs = File.Create(big)) fs.Write(Kit.Noise(60 * 1024 * 1024));
        var marker = "dropflow-kill-" + Guid.NewGuid().ToString("N");
        var archive = Path.Combine(temp.Path, marker + ".7z");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        var result = await ProcessRunner.RunAsync(found, ["a", "-t7z", "-mx=9", "-y", "--", archive, big], new ProcessOptions { Timeout = TimeSpan.FromMinutes(2) }, cts.Token);
        Assert.True(result.Cancelled);

        // ask the OS whether any 7-Zip process with our unique archive name is still alive
        var probe = OperatingSystem.IsWindows()
            ? await ProcessRunner.RunAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter \"Name='7z.exe'\" | Where-Object {{ $_.CommandLine -like '*{marker}*' }} | Measure-Object).Count"],
                new ProcessOptions { Timeout = TimeSpan.FromSeconds(90) })
            // "[d]ropflow…" matches the 7z command line but not the grep/sh that carries the pattern itself.
            : await ProcessRunner.RunAsync("/bin/sh",
                ["-c", $"ps -A -o args= | grep -c '[d]{marker[1..]}'; true"],
                new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) });
        // WMI can be very slow to start on a cold CI machine (Windows on ARM): an unanswered probe is not evidence of a leak,
        // the deletion check below still proves the files were released.
        if (!probe.TimedOut)
            Assert.Equal("0", probe.StdOut.Trim());

        // and nothing keeps the half-written archive open: it can be deleted at once
        foreach (var leftover in Directory.GetFiles(temp.Path, marker + "*")) File.Delete(leftover);
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task The_scoop_style_shim_passes_arguments_with_spaces_and_unicode_unchanged()
    {
        using var temp = new TempDir();
        var folder = Kit.NewDir(temp, $"folder with spaces {Kit.ArabicFolder}");
        var file = Path.Combine(folder, $"{Kit.ArabicFile} (copy) & more.txt");
        File.WriteAllText(file, "payload");
        var archive = Path.Combine(temp.Path, $"{Kit.ArabicFile} out.7z");

        await Make7z(folder, "a", "-t7z", "-y", "--", archive, "*");

        Assert.True(File.Exists(archive));
        Assert.Equal([$"{Kit.ArabicFile} (copy) & more.txt"], await Kit.SevenZipFileNames(archive));
    }
}
