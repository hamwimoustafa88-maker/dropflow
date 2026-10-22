using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DropFlow.AI.Models;

public enum ModelDownloadPhase { Connecting, Downloading, Verifying, Done }

/// <param name="BytesReceived">Bytes of the file on disk so far (including a resumed prefix).</param>
/// <param name="TotalBytes">Expected size of the finished file.</param>
public sealed record ModelDownloadProgress(string ModelId, ModelDownloadPhase Phase, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

/// <summary>What a download would cost, computed without touching the network, so the UI can ask before starting.</summary>
public sealed record ModelDownloadPlan(
    ModelInfo Model,
    bool IsInstalled,
    long TotalBytes,
    long AlreadyDownloadedBytes,
    bool RequiresUnverifiedConsent)
{
    public long RemainingBytes => IsInstalled ? 0 : Math.Max(0, TotalBytes - AlreadyDownloadedBytes);
    public bool CanResume => !IsInstalled && AlreadyDownloadedBytes > 0 && AlreadyDownloadedBytes < TotalBytes;
}

public enum ModelDownloadFailure
{
    /// <summary>The catalog entry has no SHA-256 and does not allow unverified downloads. Nothing was requested.</summary>
    NotVerified,
    UnknownModel,
    Http,
    /// <summary>Connection dropped or stalled. The partial file is kept; calling again resumes.</summary>
    Network,
    /// <summary>The server's size does not match the catalog (wrong URL or changed file).</summary>
    SizeMismatch,
    /// <summary>The models folder is full or cannot be written. The partial file is kept.</summary>
    Disk,
    /// <summary>The finished file does not match the pinned SHA-256. The partial file was deleted.</summary>
    HashMismatch,
}

public sealed class ModelDownloadException(ModelDownloadFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public ModelDownloadFailure Kind { get; } = kind;
}

/// <summary>
/// Installs, verifies and removes AI models in one folder (default <c>%LOCALAPPDATA%\DropFlow\models</c>).
/// Nothing is downloaded unless <see cref="DownloadAsync"/> is called, which the app does only after the user agrees.
/// Downloads go to <c>name.part</c>, resume with HTTP Range, are SHA-256 checked, then moved into place atomically.
/// </summary>
public sealed class ModelManager : IDisposable
{
    public const string PartExtension = ".part";

    private readonly IReadOnlyList<ModelInfo> _catalog;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _stallTimeout;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ModelManager(string? directory = null, IEnumerable<ModelInfo>? catalog = null, HttpClient? httpClient = null, TimeSpan? stallTimeout = null)
    {
        Directory = directory ?? DefaultDirectory;
        _catalog = (catalog ?? ModelCatalog.Default).ToList();
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);
        if (httpClient is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 8 })
            {
                Timeout = Timeout.InfiniteTimeSpan, // a model download legitimately takes minutes; stalls are detected per read instead
            };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    public static string DefaultDirectory { get; } =
        Path.Combine(DropFlow.Core.App.DataFolder.Default, "models");

    public string Directory { get; }

    public IReadOnlyList<ModelInfo> Catalog => _catalog;

    public ModelInfo? Find(string id) => _catalog.FirstOrDefault(m => m.Id == id);

    public ModelInfo Get(string id) =>
        Find(id) ?? throw new ModelDownloadException(ModelDownloadFailure.UnknownModel, $"Unknown model '{id}'.");

    public IEnumerable<ModelInfo> ForPurpose(ModelPurpose purpose) => _catalog.Where(m => m.Purpose == purpose);

    /// <summary>Where the finished model lives (whether or not it is installed); for a pack, its folder.</summary>
    public string PathOf(string id) => System.IO.Path.Combine(Directory, Get(id).FileName);

    /// <summary>Full path of one file of a pack (whether or not it is installed).</summary>
    public string FilePathOf(string id, string fileName) => System.IO.Path.Combine(PathOf(id), fileName);

    private string PartPathOf(ModelInfo model) => System.IO.Path.Combine(Directory, model.FileName + PartExtension);

    /// <summary>One downloadable file: a plain model is a single one, a pack has one per <see cref="ModelInfo.Files"/> entry.</summary>
    private sealed record Artifact(string RelPath, Uri Url, long Size, string? Sha256);

    private static List<Artifact> ArtifactsOf(ModelInfo model) => model.IsPack
        ? [.. model.Files.Select(f => new Artifact(System.IO.Path.Combine(model.FileName, f.Name), f.Url, f.SizeBytes, f.Sha256))]
        : [new Artifact(model.FileName, model.Url, model.SizeBytes, model.Sha256)];

    private bool HasExactSize(Artifact a)
    {
        var info = new FileInfo(System.IO.Path.Combine(Directory, a.RelPath));
        return info.Exists && info.Length == a.Size;
    }

    /// <summary>True when every file exists with exactly the catalog size. (Hash is checked at install time; see <see cref="VerifyInstalledAsync"/>.)</summary>
    public bool IsInstalled(string id)
    {
        var model = Find(id);
        return model is not null && ArtifactsOf(model).All(HasExactSize);
    }

    /// <summary>Full path of the model when installed; otherwise null.</summary>
    public string? InstalledPath(string id) => IsInstalled(id) ? PathOf(id) : null;

    public IEnumerable<ModelInfo> Installed(ModelPurpose? purpose = null) =>
        _catalog.Where(m => (purpose is null || m.Purpose == purpose) && IsInstalled(m.Id));

    public ModelDownloadPlan GetDownloadPlan(string id)
    {
        var model = Get(id);
        long already = 0;
        foreach (var a in ArtifactsOf(model))
        {
            var final = new FileInfo(System.IO.Path.Combine(Directory, a.RelPath));
            if (final.Exists && final.Length == a.Size) { already += a.Size; continue; }
            var part = new FileInfo(System.IO.Path.Combine(Directory, a.RelPath + PartExtension));
            if (part.Exists) already += Math.Min(part.Length, a.Size);
        }
        return new ModelDownloadPlan(
            model,
            IsInstalled(id),
            model.SizeBytes,
            Math.Min(already, model.SizeBytes),
            RequiresUnverifiedConsent: !model.IsVerified);
    }

    /// <summary>Recomputes the SHA-256 of an installed model (every file of a pack). Returns false if it is missing or does not match.</summary>
    public async Task<bool> VerifyInstalledAsync(string id, CancellationToken cancellationToken = default)
    {
        var model = Get(id);
        if (!IsInstalled(id)) return false;
        foreach (var a in ArtifactsOf(model))
        {
            if (a.Sha256 is null) continue; // nothing to compare against
            var actual = await HashFileAsync(System.IO.Path.Combine(Directory, a.RelPath), null, id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, a.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Removes an installed model and any partial download. Returns true if anything was deleted.</summary>
    public bool Delete(string id)
    {
        var model = Get(id);
        var deleted = false;
        foreach (var a in ArtifactsOf(model))
        {
            foreach (var path in new[] { System.IO.Path.Combine(Directory, a.RelPath), System.IO.Path.Combine(Directory, a.RelPath + PartExtension) })
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deleted = true;
            }
        }
        if (model.IsPack)
        {
            var folder = PathOf(id);
            if (System.IO.Directory.Exists(folder) && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
                System.IO.Directory.Delete(folder);
        }
        return deleted;
    }

    /// <summary>
    /// Downloads and installs a model, returning its final path (for a pack, its folder). Cancellation and dropped connections keep
    /// the partial file so the next call resumes. Throws <see cref="ModelDownloadException"/> for expected failures and
    /// <see cref="OperationCanceledException"/> when cancelled. Only call this after the user has agreed to the download.
    /// </summary>
    public async Task<string> DownloadAsync(string id, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var model = Get(id);
        if (!model.IsVerified && !model.AllowUnverified)
            throw new ModelDownloadException(ModelDownloadFailure.NotVerified,
                $"Model '{id}' has no pinned SHA-256 and is not allowed to be installed unverified; nothing was downloaded.");

        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finalPath = System.IO.Path.Combine(Directory, model.FileName);

            if (IsInstalled(id))
            {
                progress?.Report(new(id, ModelDownloadPhase.Done, model.SizeBytes, model.SizeBytes));
                return finalPath;
            }

            System.IO.Directory.CreateDirectory(model.IsPack ? finalPath : Directory);
            var artifacts = ArtifactsOf(model);
            EnsureFreeSpace(model, artifacts);

            // Files of a pack are fetched one after the other; progress is reported against the total of the whole pack.
            long before = 0;
            foreach (var artifact in artifacts)
            {
                if (!HasExactSize(artifact))
                    await DownloadArtifactAsync(id, artifact, before, model.SizeBytes, progress, cancellationToken).ConfigureAwait(false);
                before += artifact.Size;
            }

            progress?.Report(new(id, ModelDownloadPhase.Done, model.SizeBytes, model.SizeBytes));
            return finalPath;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadArtifactAsync(string id, Artifact artifact, long before, long grandTotal, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var finalPath = System.IO.Path.Combine(Directory, artifact.RelPath);
        var partPath = finalPath + PartExtension;
        if (File.Exists(finalPath)) File.Delete(finalPath); // wrong size = corrupt or truncated: start clean

        for (var attempt = 0; ; attempt++)
        {
            var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (resumeFrom > artifact.Size) { File.Delete(partPath); resumeFrom = 0; }

            if (resumeFrom < artifact.Size)
            {
                progress?.Report(new(id, ModelDownloadPhase.Connecting, before + resumeFrom, grandTotal));
                await FetchIntoPartAsync(id, artifact, partPath, resumeFrom, before, grandTotal, progress, cancellationToken).ConfigureAwait(false);
            }
            // else: a previous run finished downloading but died before verifying; no need to ask the server for anything

            var length = new FileInfo(partPath).Length;
            if (length > artifact.Size)
            {
                File.Delete(partPath);
                throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                    $"Downloaded {length} bytes for '{id}' but the catalog says {artifact.Size}; the partial file was deleted.");
            }
            if (length < artifact.Size)
                throw new ModelDownloadException(ModelDownloadFailure.Network,
                    $"Download of '{id}' ended early at {length} of {artifact.Size} bytes. Run it again to resume.");

            if (artifact.Sha256 is not null)
            {
                var actual = await HashFileAsync(partPath, progress is null ? null : new InlineReport(progress, before, grandTotal), id, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partPath);
                    // A resumed prefix may have been damaged on disk long ago; give a clean download one chance before giving up.
                    if (resumeFrom > 0 && attempt == 0) continue;
                    throw new ModelDownloadException(ModelDownloadFailure.HashMismatch,
                        $"SHA-256 of '{id}' does not match the expected value; the file was discarded (expected {artifact.Sha256}, got {actual}).");
                }
            }

            File.Move(partPath, finalPath, overwrite: true);
            return;
        }
    }

    /// <summary>Maps the progress of one file of a pack onto the progress of the whole pack.</summary>
    private sealed class InlineReport(IProgress<ModelDownloadProgress> inner, long before, long total) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress p) =>
            inner.Report(p with { BytesReceived = p.Phase == ModelDownloadPhase.Verifying ? Math.Min(total, before + p.TotalBytes) : Math.Min(total, before + p.BytesReceived), TotalBytes = total });
    }

    private async Task FetchIntoPartAsync(string modelId, Artifact artifact, string partPath, long resumeFrom, long before, long grandTotal, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DropFlow", "1.0"));
        if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Network, $"Could not reach {artifact.Url.Host}: {ex.Message}", ex);
        }

        using (response)
        {
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Our partial file is at or past the end of the remote file. Complete? Otherwise it is junk: start again.
                var total = response.Content.Headers.ContentRange?.Length;
                if (total == resumeFrom) return;
                File.Delete(partPath);
                await FetchIntoPartAsync(modelId, artifact, partPath, 0, before, grandTotal, progress, ct).ConfigureAwait(false);
                return;
            }

            if (!response.IsSuccessStatusCode)
                throw new ModelDownloadException(ModelDownloadFailure.Http,
                    $"Server answered {(int)response.StatusCode} {response.ReasonPhrase} for '{modelId}'.");

            var start = 0L;
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != resumeFrom)
                {
                    // The server resumed somewhere else; do not trust it.
                    File.Delete(partPath);
                    await FetchIntoPartAsync(modelId, artifact, partPath, 0, before, grandTotal, progress, ct).ConfigureAwait(false);
                    return;
                }
                start = resumeFrom;
            }
            // A plain 200 to a ranged request means the server ignores Range: take the whole body and overwrite the partial file.

            var remaining = response.Content.Headers.ContentLength;
            if (remaining is { } r && start + r != artifact.Size)
                throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                    $"Server reports {start + r} bytes for '{modelId}' but the catalog says {artifact.Size}.");

            FileStream file;
            try
            {
                file = new FileStream(partPath, start == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
                if (start > 0) file.Seek(start, SeekOrigin.Begin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Disk, $"Could not create {partPath}: {ex.Message}", ex);
            }
            await using var fileScope = file;

            var received = start;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buffer = new byte[1 << 16];
            var lastReport = Environment.TickCount64;
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                while (true)
                {
                    stall.CancelAfter(_stallTimeout);
                    var read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        throw new ModelDownloadException(ModelDownloadFailure.Disk, $"Could not write to {Directory}: {ex.Message}", ex);
                    }
                    received += read;
                    var now = Environment.TickCount64;
                    if (now - lastReport >= 100)
                    {
                        lastReport = now;
                        progress?.Report(new(modelId, ModelDownloadPhase.Downloading, before + received, grandTotal));
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Network,
                    $"No data from {artifact.Url.Host} for {_stallTimeout.TotalSeconds:0} s while downloading '{modelId}' ({received} bytes kept; run it again to resume).");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Network,
                    $"Connection lost while downloading '{modelId}' at {received} bytes ({ex.Message}). Run it again to resume.", ex);
            }
            finally
            {
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            progress?.Report(new(modelId, ModelDownloadPhase.Downloading, before + received, grandTotal));
        }
    }

    /// <summary>Refuses to start a download that cannot fit: 64 MB of headroom is kept for the system.</summary>
    private void EnsureFreeSpace(ModelInfo model, List<Artifact> artifacts)
    {
        long needed;
        string partPath = "";
        try
        {
            needed = 0;
            foreach (var a in artifacts)
            {
                var final = new FileInfo(System.IO.Path.Combine(Directory, a.RelPath));
                if (final.Exists && final.Length == a.Size) continue;
                partPath = System.IO.Path.Combine(Directory, a.RelPath + PartExtension);
                var have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                needed += Math.Max(0, a.Size - have);
            }
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Directory));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= needed + 64L * 1024 * 1024) return;
            throw new ModelDownloadException(ModelDownloadFailure.Disk,
                $"Not enough free disk space for '{model.Id}': {needed / 1_048_576.0:0.#} MB needed, {free / 1_048_576.0:0.#} MB free on {root}.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            // Network shares and unusual volumes cannot report free space; let the download itself find out.
        }
    }

    private static async Task<string> HashFileAsync(string path, IProgress<ModelDownloadProgress>? progress, string modelId, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(new(modelId, ModelDownloadPhase.Verifying, done, stream.Length));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        foreach (var gate in _locks.Values) gate.Dispose();
    }
}
