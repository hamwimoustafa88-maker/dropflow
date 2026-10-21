using System.Net;
using DropFlow.AI.Models;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>The downloader against a real HTTP server on loopback: Range resume, hash checks, cancellation, recovery.</summary>
public class ModelManagerTests
{
    private static ModelInfo Entry(TestHttpServer server, byte[] content, string id = "test-model", string? sha = "auto", bool allowUnverified = false, long? size = null) => new()
    {
        Id = id,
        DisplayName = "Test model",
        Purpose = ModelPurpose.SpeechToText,
        FileName = id + ".bin",
        Url = server.Add(id + ".bin", content),
        SizeBytes = size ?? content.Length,
        Sha256 = sha == "auto" ? Hashing.Sha256Hex(content) : sha,
        AllowUnverified = allowUnverified,
        License = "MIT",
        SourcePage = "http://127.0.0.1/",
    };

    private static ModelManager Manager(TempDir dir, params ModelInfo[] catalog) =>
        new(dir.Combine("models"), catalog, stallTimeout: TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Downloads_verifies_and_installs_atomically()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(400_000);
        using var models = Manager(dir, Entry(server, content));

        Assert.False(models.IsInstalled("test-model"));
        var reports = new List<ModelDownloadProgress>();
        var path = await models.DownloadAsync("test-model", new Progress<ModelDownloadProgress>(reports.Add));
        await Task.Delay(50); // Progress<T> posts asynchronously

        Assert.Equal(models.PathOf("test-model"), path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.True(models.IsInstalled("test-model"));
        Assert.False(File.Exists(path + ModelManager.PartExtension));
        Assert.Contains(reports, r => r.Phase == ModelDownloadPhase.Verifying);
        Assert.Equal(ModelDownloadPhase.Done, reports[^1].Phase);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.True(await models.VerifyInstalledAsync("test-model"));
    }

    [Fact]
    public void Reports_size_before_any_network_call()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(1000)));

        var plan = models.GetDownloadPlan("test-model");
        Assert.Equal(1000, plan.TotalBytes);
        Assert.Equal(1000, plan.RemainingBytes);
        Assert.False(plan.IsInstalled);
        Assert.False(plan.CanResume);
        Assert.False(plan.RequiresUnverifiedConsent);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_a_range_request()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(500_000, seed: 7);
        using var models = Manager(dir, Entry(server, content));

        server.CutNextResponses(times: 1, afterBytes: 120_000);
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.Network, ex.Kind);
        Assert.False(models.IsInstalled("test-model"));

        var part = new FileInfo(models.PathOf("test-model") + ModelManager.PartExtension);
        Assert.True(part.Exists);
        Assert.InRange(part.Length, 1, 500_000 - 1);
        var plan = models.GetDownloadPlan("test-model");
        Assert.True(plan.CanResume);
        Assert.Equal(part.Length, plan.AlreadyDownloadedBytes);
        Assert.Equal(500_000 - part.Length, plan.RemainingBytes);

        var heldBack = part.Length;
        await models.DownloadAsync("test-model");

        var requests = server.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Null(requests[0].Range);
        Assert.Equal($"bytes={heldBack}-", requests[1].Range);
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
    }

    [Fact]
    public async Task Cancellation_keeps_the_partial_file_and_a_later_call_finishes_it()
    {
        using var server = new TestHttpServer { ChunkBytes = 20_000, ChunkDelayMs = 25 };
        using var dir = new TempDir();
        var content = Hashing.Bytes(400_000, seed: 3);
        using var models = Manager(dir, Entry(server, content));

        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(p =>
        {
            if (p.Phase == ModelDownloadPhase.Downloading && p.BytesReceived > 0) cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.DownloadAsync("test-model", progress, cts.Token));

        Assert.False(models.IsInstalled("test-model"));
        var partLength = new FileInfo(models.PathOf("test-model") + ModelManager.PartExtension).Length;
        Assert.InRange(partLength, 1, 399_999);

        server.ChunkBytes = 0;
        await models.DownloadAsync("test-model");
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
        Assert.Contains(server.Requests, r => r.Range == $"bytes={partLength}-");
    }

    [Fact]
    public async Task Already_cancelled_token_starts_nothing()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(1000)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.DownloadAsync("test-model", null, cts.Token));
        Assert.Empty(server.Requests);
        Assert.False(models.IsInstalled("test-model"));
    }

    [Fact]
    public async Task Hash_mismatch_discards_the_file_and_installs_nothing()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var served = Hashing.Bytes(50_000, seed: 1);
        var expected = Hashing.Sha256Hex(Hashing.Bytes(50_000, seed: 2)); // same size, different bytes
        using var models = Manager(dir, Entry(server, served, sha: expected));

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.HashMismatch, ex.Kind);
        Assert.False(File.Exists(models.PathOf("test-model")));
        Assert.False(File.Exists(models.PathOf("test-model") + ModelManager.PartExtension));
        Assert.Single(server.Requests); // a clean download that is wrong is not retried
    }

    [Fact]
    public async Task Corrupt_partial_prefix_is_discarded_and_downloaded_again_once()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(80_000, seed: 5);
        using var models = Manager(dir, Entry(server, content));

        // A .part left over from a damaged earlier attempt: right length so far, wrong bytes.
        Directory.CreateDirectory(Path.GetDirectoryName(models.PathOf("test-model"))!);
        await File.WriteAllBytesAsync(models.PathOf("test-model") + ModelManager.PartExtension, Hashing.Bytes(30_000, seed: 99));

        await models.DownloadAsync("test-model");

        var requests = server.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("bytes=30000-", requests[0].Range); // tried to resume
        Assert.Null(requests[1].Range);                  // then started over
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
    }

    [Fact]
    public async Task Server_that_ignores_range_still_produces_a_correct_file()
    {
        using var server = new TestHttpServer { SupportRange = false };
        using var dir = new TempDir();
        var content = Hashing.Bytes(60_000, seed: 11);
        using var models = Manager(dir, Entry(server, content));

        Directory.CreateDirectory(Path.GetDirectoryName(models.PathOf("test-model"))!);
        await File.WriteAllBytesAsync(models.PathOf("test-model") + ModelManager.PartExtension, content[..20_000]);

        await models.DownloadAsync("test-model");
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
    }

    [Fact]
    public async Task A_complete_partial_file_is_verified_and_moved_into_place()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(40_000, seed: 13);
        using var models = Manager(dir, Entry(server, content));

        // The previous run finished downloading but died before the final move.
        Directory.CreateDirectory(Path.GetDirectoryName(models.PathOf("test-model"))!);
        await File.WriteAllBytesAsync(models.PathOf("test-model") + ModelManager.PartExtension, content);

        await models.DownloadAsync("test-model");
        Assert.True(models.IsInstalled("test-model"));
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
        Assert.Empty(server.Requests);   // nothing left to fetch, so the server is not asked
    }

    [Fact]
    public async Task Truncated_installed_file_is_not_installed_and_is_replaced()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(30_000, seed: 17);
        using var models = Manager(dir, Entry(server, content));

        Directory.CreateDirectory(Path.GetDirectoryName(models.PathOf("test-model"))!);
        await File.WriteAllBytesAsync(models.PathOf("test-model"), content[..1234]);
        Assert.False(models.IsInstalled("test-model"));
        Assert.Null(models.InstalledPath("test-model"));

        await models.DownloadAsync("test-model");
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
    }

    [Fact]
    public async Task Entry_without_a_pinned_hash_is_refused_before_touching_the_network()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(1000), sha: null));

        Assert.True(models.GetDownloadPlan("test-model").RequiresUnverifiedConsent);
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.NotVerified, ex.Kind);
        Assert.Empty(server.Requests);
        Assert.False(models.IsInstalled("test-model"));
    }

    [Fact]
    public async Task Unverified_entry_installs_only_when_it_explicitly_allows_it()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(5000, seed: 21);
        using var models = Manager(dir, Entry(server, content, sha: null, allowUnverified: true));

        await models.DownloadAsync("test-model");
        Assert.Equal(content, await File.ReadAllBytesAsync(models.PathOf("test-model")));
    }

    [Fact]
    public async Task Size_that_disagrees_with_the_catalog_is_rejected()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(10_000);
        using var models = Manager(dir, Entry(server, content, size: 20_000));

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.SizeMismatch, ex.Kind);
        Assert.False(models.IsInstalled("test-model"));
    }

    [Fact]
    public async Task A_download_that_cannot_fit_on_the_disk_is_refused_before_it_starts()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var huge = Entry(server, Hashing.Bytes(1000), size: 200L * 1024 * 1024 * 1024 * 1024);   // 200 TB
        using var models = Manager(dir, huge);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.Disk, ex.Kind);
        Assert.Contains("free disk space", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_part_file_that_cannot_be_written_is_a_disk_failure_not_a_network_one()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(1000)));
        // a directory squats on the .part name, so the file cannot be created
        Directory.CreateDirectory(models.PathOf("test-model") + ModelManager.PartExtension);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.Disk, ex.Kind);
    }

    [Fact]
    public async Task Http_errors_are_reported_as_such()
    {
        using var server = new TestHttpServer { ForceStatus = HttpStatusCode.NotFound };
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(1000)));

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("test-model"));
        Assert.Equal(ModelDownloadFailure.Http, ex.Kind);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Unreachable_server_is_a_network_failure_not_a_crash()
    {
        using var dir = new TempDir();
        var entry = new ModelInfo
        {
            Id = "ghost", DisplayName = "Ghost", Purpose = ModelPurpose.SpeechToText, FileName = "ghost.bin",
            Url = new Uri("http://127.0.0.1:1/ghost.bin"), SizeBytes = 10, Sha256 = new string('a', 64), License = "MIT", SourcePage = "-",
        };
        using var models = new ModelManager(dir.Combine("models"), [entry]);
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("ghost"));
        Assert.Equal(ModelDownloadFailure.Network, ex.Kind);
    }

    [Fact]
    public async Task Unknown_model_id_is_reported()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("models"), []);
        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => models.DownloadAsync("nope"));
        Assert.Equal(ModelDownloadFailure.UnknownModel, ex.Kind);
        Assert.False(models.IsInstalled("nope"));
    }

    [Fact]
    public async Task Delete_removes_the_model_and_any_partial_file()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(2000)));
        await models.DownloadAsync("test-model");
        await File.WriteAllBytesAsync(models.PathOf("test-model") + ModelManager.PartExtension, [1, 2, 3]);

        Assert.True(models.Delete("test-model"));
        Assert.False(File.Exists(models.PathOf("test-model")));
        Assert.False(File.Exists(models.PathOf("test-model") + ModelManager.PartExtension));
        Assert.False(models.Delete("test-model"));
    }

    [Fact]
    public async Task Verify_detects_tampering_that_keeps_the_size()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(3000, seed: 4)));
        await models.DownloadAsync("test-model");
        Assert.True(await models.VerifyInstalledAsync("test-model"));

        var bytes = await File.ReadAllBytesAsync(models.PathOf("test-model"));
        bytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(models.PathOf("test-model"), bytes);
        Assert.True(models.IsInstalled("test-model")); // size alone cannot tell
        Assert.False(await models.VerifyInstalledAsync("test-model"));
    }

    [Fact]
    public async Task Two_simultaneous_requests_for_one_model_download_it_once()
    {
        using var server = new TestHttpServer { ChunkBytes = 50_000, ChunkDelayMs = 20 };
        using var dir = new TempDir();
        using var models = Manager(dir, Entry(server, Hashing.Bytes(300_000, seed: 8)));

        await Task.WhenAll(models.DownloadAsync("test-model"), models.DownloadAsync("test-model"));
        Assert.Single(server.Requests);
        Assert.True(models.IsInstalled("test-model"));
    }

    [Fact]
    public async Task Works_in_a_folder_with_an_arabic_name()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(9000, seed: 23);
        var arabicFolder = dir.Combine("نماذج الذكاء");
        using var models = new ModelManager(arabicFolder, [Entry(server, content)]);

        var path = await models.DownloadAsync("test-model");
        Assert.StartsWith(arabicFolder, path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void Lists_installed_models_by_purpose()
    {
        using var server = new TestHttpServer();
        using var dir = new TempDir();
        var content = Hashing.Bytes(100);
        using var models = Manager(dir, Entry(server, content));
        Assert.Empty(models.Installed());

        Directory.CreateDirectory(models.Directory);
        File.WriteAllBytes(models.PathOf("test-model"), content);
        Assert.Single(models.Installed());
        Assert.Single(models.Installed(ModelPurpose.SpeechToText));
        Assert.Empty(models.Installed(ModelPurpose.FaceDetection));
    }

    /// <summary>IProgress that runs inline (Progress&lt;T&gt; posts to the thread pool, which is too late for cancelling mid-transfer).</summary>
    private sealed class SynchronousProgress(Action<ModelDownloadProgress> onReport) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => onReport(value);
    }
}
