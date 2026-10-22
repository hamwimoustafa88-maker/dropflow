using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DropFlow.AI.Models;
using DropFlow.TestSupport;

namespace DropFlow.Cli.Tests;

public class ModelsCommandTests : IDisposable
{
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))];

    private readonly TempDir _dir = new();
    private int _requests;

    public void Dispose() => _dir.Dispose();

    private sealed class Handler(byte[] body, Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private ModelManager Manager(string? sha = null)
    {
        var info = new ModelInfo
        {
            Id = "tiny-test",
            DisplayName = "Tiny test model",
            Purpose = ModelPurpose.FaceDetection,
            FileName = "tiny-test.onnx",
            Url = new Uri("https://example.test/models/tiny-test.onnx"),
            SizeBytes = Payload.Length,
            Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(Payload)),
            License = "MIT",
            SourcePage = "https://example.test/models",
        };
        return new ModelManager(_dir.Combine("models"), [info], new HttpClient(new Handler(Payload, () => Interlocked.Increment(ref _requests))));
    }

    private static async Task<(int Code, string Out, string Err)> Run(ModelManager models, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await ModelsCommand.RunAsync(args, models, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task List_shows_ids_sizes_state_and_licence()
    {
        using var models = Manager();
        var (code, stdout, _) = await Run(models, "list");

        Assert.Equal(0, code);
        Assert.Contains("tiny-test", stdout);
        Assert.Contains("not downloaded", stdout);
        Assert.Contains("MIT", stdout);
    }

    [Fact]
    public async Task Download_without_yes_only_describes_and_fetches_nothing()
    {
        using var models = Manager();
        var (code, stdout, _) = await Run(models, "download", "tiny-test");

        Assert.Equal(2, code);
        Assert.Contains("example.test", stdout);
        Assert.Contains("Nothing was downloaded", stdout);
        Assert.Equal(0, _requests);
        Assert.False(models.IsInstalled("tiny-test"));
    }

    [Fact]
    public async Task Download_with_yes_installs_a_verified_file_and_list_then_says_installed()
    {
        using var models = Manager();
        var (code, _, _) = await Run(models, "download", "tiny-test", "--yes");

        Assert.Equal(0, code);
        Assert.True(models.IsInstalled("tiny-test"));
        Assert.Equal(1, _requests);
        Assert.Contains("installed", (await Run(models, "list")).Out);
        Assert.Contains("already installed", (await Run(models, "download", "tiny-test", "--yes")).Out);
        Assert.Equal(1, _requests);   // not fetched again
    }

    [Fact]
    public async Task A_file_that_does_not_match_the_pinned_hash_is_refused_and_not_installed()
    {
        using var models = Manager(sha: new string('0', 64));
        var (code, _, stderr) = await Run(models, "download", "tiny-test", "--yes");

        Assert.Equal(1, code);
        Assert.Contains("failed", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(models.IsInstalled("tiny-test"));
    }

    [Fact]
    public async Task Unknown_ids_and_verbs_are_usage_errors()
    {
        using var models = Manager();
        Assert.Equal(2, (await Run(models, "download", "nope", "--yes")).Code);
        Assert.Equal(2, (await Run(models, "frobnicate")).Code);
        Assert.Equal(2, (await Run(models, "delete")).Code);
    }

    [Fact]
    public async Task Delete_removes_an_installed_model()
    {
        using var models = Manager();
        await Run(models, "download", "tiny-test", "--yes");

        var (code, stdout, _) = await Run(models, "delete", "tiny-test");

        Assert.Equal(0, code);
        Assert.Contains("Deleted", stdout);
        Assert.False(models.IsInstalled("tiny-test"));
    }

    // ---- the translation models are multi-file packs in the real catalogue ----------------------------------------

    [Fact]
    public async Task The_translation_models_are_listed_with_their_whole_size_and_nothing_is_downloaded_without_yes()
    {
        var requests = 0;
        using var models = new ModelManager(_dir.Combine("real-models"), httpClient: new HttpClient(new Handler([], () => Interlocked.Increment(ref requests))));

        var list = (await Run(models, "list")).Out;
        Assert.Contains("opus-mt-en-ar", list);
        Assert.Contains("opus-mt-ar-en", list);
        Assert.Contains("Translation", list);
        Assert.Contains("108 MB", list);                                   // the total of the four files, not of one of them
        Assert.Contains("Apache-2.0", list);

        var (code, stdout, _) = await Run(models, "download", "opus-mt-ar-en");
        Assert.Equal(2, code);
        Assert.Contains("108.4 MB from huggingface.co", stdout);
        Assert.Contains("Nothing was downloaded", stdout);
        Assert.Equal(0, requests);
        Assert.False(models.IsInstalled("opus-mt-ar-en"));
        Assert.False(Directory.Exists(models.PathOf("opus-mt-ar-en")));
    }

    [Fact]
    public async Task Deleting_a_translation_model_that_is_not_installed_says_so()
    {
        using var models = new ModelManager(_dir.Combine("real-models"));
        var (code, stdout, _) = await Run(models, "delete", "opus-mt-en-ar");
        Assert.Equal(0, code);
        Assert.Contains("was not installed", stdout);
    }
}
