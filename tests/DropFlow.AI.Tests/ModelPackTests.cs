using DropFlow.AI.Models;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>Models made of several files (the translation models): one id, one consent, one folder, every file checked.</summary>
public class ModelPackTests
{
    private static ModelInfo Pack(TestHttpServer server, string id, params (string Name, byte[] Content, string? Sha)[] files)
    {
        var list = files.Select(f => new ModelFile(f.Name, server.Add($"{id}/{f.Name}", f.Content), f.Content.Length, f.Sha ?? Hashing.Sha256Hex(f.Content))).ToList();
        return new ModelInfo
        {
            Id = id,
            DisplayName = "Test pack",
            Purpose = ModelPurpose.Translation,
            FileName = id,
            Url = list[0].Url,
            SizeBytes = list.Sum(f => f.SizeBytes),
            Files = list,
            License = "Apache-2.0",
            SourcePage = "http://127.0.0.1/",
        };
    }

    private static ModelManager Manager(TempDir dir, params ModelInfo[] catalog) =>
        new(dir.Combine("models"), catalog, stallTimeout: TimeSpan.FromSeconds(10));

    private static readonly (string, byte[], string?)[] ThreeFiles =
    [
        ("encoder.onnx", Hashing.Bytes(300_000, 1), null),
        ("decoder.onnx", Hashing.Bytes(200_000, 2), null),
        ("vocab.json", Hashing.Bytes(5_000, 3), null),
    ];

    [Fact]
    public async Task Every_file_lands_in_the_pack_folder_and_progress_covers_the_whole_pack()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var pack = Pack(server, "pack-a", ThreeFiles);
        using var models = Manager(dir, pack);
        var reports = new List<ModelDownloadProgress>();

        var path = await models.DownloadAsync("pack-a", new InlineProgress<ModelDownloadProgress>(reports.Add));

        Assert.Equal(models.PathOf("pack-a"), path);
        Assert.Equal(Path.Combine(dir.Combine("models"), "pack-a"), path);
        foreach (var (name, content, _) in ThreeFiles) Assert.Equal(content, await File.ReadAllBytesAsync(models.FilePathOf("pack-a", name)));
        Assert.True(models.IsInstalled("pack-a"));
        Assert.True(await models.VerifyInstalledAsync("pack-a"));
        Assert.Empty(Directory.GetFiles(path, "*" + ModelManager.PartExtension));

        Assert.All(reports, r => Assert.Equal(pack.SizeBytes, r.TotalBytes));
        var downloading = reports.Where(r => r.Phase == ModelDownloadPhase.Downloading).Select(r => r.BytesReceived).ToList();
        Assert.Equal(downloading.Order(), downloading);                               // never backwards across the files
        Assert.Equal(pack.SizeBytes, downloading.Max());
        Assert.Equal(ModelDownloadPhase.Done, reports[^1].Phase);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public void The_plan_shows_the_size_of_the_whole_pack_and_what_is_already_on_disk()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var pack = Pack(server, "pack-b", ThreeFiles);
        using var models = Manager(dir, pack);

        var plan = models.GetDownloadPlan("pack-b");
        Assert.Equal(505_000, plan.TotalBytes);
        Assert.Equal(505_000, plan.RemainingBytes);
        Assert.False(plan.IsInstalled);
        Assert.Empty(server.Requests);                                                // planning never touches the network

        // the first file is complete and half of the second is downloaded
        Directory.CreateDirectory(Path.Combine(dir.Combine("models"), "pack-b"));
        File.WriteAllBytes(Path.Combine(dir.Combine("models"), "pack-b", "encoder.onnx"), ThreeFiles[0].Item2);
        File.WriteAllBytes(Path.Combine(dir.Combine("models"), "pack-b", "decoder.onnx" + ModelManager.PartExtension), ThreeFiles[1].Item2[..100_000]);
        plan = models.GetDownloadPlan("pack-b");
        Assert.Equal(400_000, plan.AlreadyDownloadedBytes);
        Assert.Equal(105_000, plan.RemainingBytes);
        Assert.True(plan.CanResume);
    }

    [Fact]
    public async Task A_dropped_connection_keeps_the_partial_file_and_the_next_run_resumes_it()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Pack(server, "pack-c", ThreeFiles));
        server.CutNextResponses(1, 100_000);                                          // the first file's answer is cut after 100 kB

        var failure = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("pack-c"));

        Assert.Equal(ModelDownloadFailure.Network, failure.Kind);
        Assert.False(models.IsInstalled("pack-c"));
        Assert.Equal(100_000, new FileInfo(models.FilePathOf("pack-c", "encoder.onnx" + ModelManager.PartExtension)).Length);

        while (server.Requests.TryDequeue(out _)) { }
        await models.DownloadAsync("pack-c");

        Assert.Contains(server.Requests, r => r.Path.EndsWith("encoder.onnx", StringComparison.Ordinal) && r.Range == "bytes=100000-");
        Assert.True(models.IsInstalled("pack-c"));
        Assert.True(await models.VerifyInstalledAsync("pack-c"));
    }

    [Fact]
    public async Task A_second_run_does_not_ask_the_server_for_files_that_are_already_complete()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Pack(server, "pack-d", ThreeFiles));
        Directory.CreateDirectory(Path.Combine(dir.Combine("models"), "pack-d"));
        File.WriteAllBytes(Path.Combine(dir.Combine("models"), "pack-d", "encoder.onnx"), ThreeFiles[0].Item2);     // already installed
        File.WriteAllBytes(Path.Combine(dir.Combine("models"), "pack-d", "decoder.onnx" + ModelManager.PartExtension), ThreeFiles[1].Item2[..50_000]);

        await models.DownloadAsync("pack-d");

        var asked = server.Requests.ToList();
        Assert.DoesNotContain(asked, r => r.Path.EndsWith("encoder.onnx", StringComparison.Ordinal));
        Assert.Contains(asked, r => r.Path.EndsWith("decoder.onnx", StringComparison.Ordinal) && r.Range == "bytes=50000-");   // resumed
        Assert.Contains(asked, r => r.Path.EndsWith("vocab.json", StringComparison.Ordinal));
        Assert.True(await models.VerifyInstalledAsync("pack-d"));
    }

    [Fact]
    public async Task A_file_with_a_wrong_hash_is_discarded_alone_and_the_pack_is_not_installed()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var files = new (string, byte[], string?)[] { ThreeFiles[0], (ThreeFiles[1].Item1, ThreeFiles[1].Item2, new string('0', 64)), ThreeFiles[2] };
        using var models = Manager(dir, Pack(server, "pack-e", files));

        var failure = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("pack-e"));

        Assert.Equal(ModelDownloadFailure.HashMismatch, failure.Kind);
        Assert.False(models.IsInstalled("pack-e"));
        Assert.True(File.Exists(models.FilePathOf("pack-e", "encoder.onnx")));        // the good file stays, so a retry does not repeat it
        Assert.False(File.Exists(models.FilePathOf("pack-e", "decoder.onnx")));
        Assert.False(File.Exists(models.FilePathOf("pack-e", "decoder.onnx" + ModelManager.PartExtension)));
    }

    [Fact]
    public async Task A_pack_counts_as_installed_only_when_every_file_has_exactly_the_right_size()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Pack(server, "pack-f", ThreeFiles));
        await models.DownloadAsync("pack-f");
        Assert.True(models.IsInstalled("pack-f"));

        File.AppendAllText(models.FilePathOf("pack-f", "vocab.json"), "x");
        Assert.False(models.IsInstalled("pack-f"));
        File.WriteAllBytes(models.FilePathOf("pack-f", "vocab.json"), ThreeFiles[2].Item2);
        File.Delete(models.FilePathOf("pack-f", "decoder.onnx"));
        Assert.False(models.IsInstalled("pack-f"));
        Assert.Null(models.InstalledPath("pack-f"));
    }

    [Fact]
    public async Task Deleting_a_pack_removes_its_files_partial_downloads_and_folder()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Pack(server, "pack-g", ThreeFiles));
        await models.DownloadAsync("pack-g");
        File.WriteAllText(models.FilePathOf("pack-g", "extra.onnx" + ModelManager.PartExtension), "junk");   // not ours: must not break the delete

        Assert.True(models.Delete("pack-g"));

        Assert.False(models.IsInstalled("pack-g"));
        foreach (var (name, _, _) in ThreeFiles) Assert.False(File.Exists(models.FilePathOf("pack-g", name)));
        Assert.False(models.Delete("pack-g"));                                         // nothing left to delete
    }

    [Fact]
    public async Task A_pack_without_pinned_hashes_is_never_downloaded()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var pack = Pack(server, "pack-h", ThreeFiles) with
        {
            Files = [.. Pack(server, "pack-h", ThreeFiles).Files.Select(f => f with { Sha256 = "" })],
        };
        using var models = Manager(dir, pack);

        Assert.True(models.GetDownloadPlan("pack-h").RequiresUnverifiedConsent);
        var failure = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("pack-h"));
        Assert.Equal(ModelDownloadFailure.NotVerified, failure.Kind);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task An_installed_pack_is_not_downloaded_again()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Pack(server, "pack-i", ThreeFiles));
        await models.DownloadAsync("pack-i");
        while (server.Requests.TryDequeue(out _)) { }

        var reports = new List<ModelDownloadProgress>();
        await models.DownloadAsync("pack-i", new InlineProgress<ModelDownloadProgress>(reports.Add));

        Assert.Empty(server.Requests);
        Assert.Equal(ModelDownloadPhase.Done, Assert.Single(reports).Phase);
    }
}
