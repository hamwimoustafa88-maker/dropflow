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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow", "models");

    public string Directory { get; }

    public IReadOnlyList<ModelInfo> Catalog => _catalog;

    public ModelInfo? Find(string id) => _catalog.FirstOrDefault(m => m.Id == id);

    public ModelInfo Get(string id) =>
        Find(id) ?? throw new ModelDownloadException(ModelDownloadFailure.UnknownModel, $"Unknown model '{id}'.");

    public IEnumerable<ModelInfo> ForPurpose(ModelPurpose purpose) => _catalog.Where(m => m.Purpose == purpose);

    /// <summary>Where the finished model lives (whether or not it is installed).</summary>
    public string PathOf(string id) => System.IO.Path.Combine(Directory, Get(id).FileName);

    private string PartPathOf(ModelInfo model) => System.IO.Path.Combine(Directory, model.FileName + PartExtension);

    /// <summary>True when the file exists with exactly the catalog size. (Hash is checked at install time; see <see cref="VerifyInstalledAsync"/>.)</summary>
    public bool IsInstalled(string id)
    {
        var model = Find(id);
        if (model is null) return false;
        var info = new FileInfo(System.IO.Path.Combine(Directory, model.FileName));
        return info.Exists && info.Length == model.SizeBytes;
    }

    /// <summary>Full path of the model when installed; otherwise null.</summary>
    public string? InstalledPath(string id) => IsInstalled(id) ? PathOf(id) : null;

    public IEnumerable<ModelInfo> Installed(ModelPurpose? purpose = null) =>
        _catalog.Where(m => (purpose is null || m.Purpose == purpose) && IsInstalled(m.Id));

    public ModelDownloadPlan GetDownloadPlan(string id)
    {
        var model = Get(id);
        var part = new FileInfo(PartPathOf(model));
        return new ModelDownloadPlan(
            model,
            IsInstalled(id),
            model.SizeBytes,
            part.Exists ? Math.Min(part.Length, model.SizeBytes) : 0,
            RequiresUnverifiedConsent: !model.IsVerified);
    }

    /// <summary>Recomputes the SHA-256 of an installed model. Returns false if it is missing or does not match.</summary>
    public async Task<bool> VerifyInstalledAsync(string id, CancellationToken cancellationToken = default)
    {
        var model = Get(id);
        if (!IsInstalled(id)) return false;
        if (model.Sha256 is null) return true; // nothing to compare against
        var actual = await HashFileAsync(PathOf(id), null, model, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes an installed model and any partial download. Returns true if anything was deleted.</summary>
    public bool Delete(string id)
    {
        var model = Get(id);
        var deleted = false;
        foreach (var path in new[] { System.IO.Path.Combine(Directory, model.FileName), PartPathOf(model) })
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            deleted = true;
        }
        return deleted;
    }

    /// <summary>
    /// Downloads and installs a model, returning its final path. Cancellation and dropped connections keep the partial file so the
    /// next call resumes. Throws <see cref="ModelDownloadException"/> for expected failures and <see cref="OperationCanceledException"/>
    /// when cancelled. Only call this after the user has agreed to the download.
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
            var partPath = PartPathOf(model);

            if (IsInstalled(id))
            {
                progress?.Report(new(id, ModelDownloadPhase.Done, model.SizeBytes, model.SizeBytes));
                return finalPath;
            }

            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(finalPath)) File.Delete(finalPath); // wrong size = corrupt or truncated: start clean
            EnsureFreeSpace(model, partPath);

            for (var attempt = 0; ; attempt++)
            {
                var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (resumeFrom > model.SizeBytes) { File.Delete(partPath); resumeFrom = 0; }

                if (resumeFrom < model.SizeBytes)
                {
                    progress?.Report(new(id, ModelDownloadPhase.Connecting, resumeFrom, model.SizeBytes));
                    await FetchIntoPartAsync(model, partPath, resumeFrom, progress, cancellationToken).ConfigureAwait(false);
                }
                // else: a previous run finished downloading but died before verifying; no need to ask the server for anything

                var length = new FileInfo(partPath).Length;
                if (length > model.SizeBytes)
                {
                    File.Delete(partPath);
                    throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                        $"Downloaded {length} bytes for '{id}' but the catalog says {model.SizeBytes}; the partial file was deleted.");
                }
                if (length < model.SizeBytes)
                    throw new ModelDownloadException(ModelDownloadFailure.Network,
                        $"Download of '{id}' ended early at {length} of {model.SizeBytes} bytes. Run it again to resume.");

                if (model.Sha256 is not null)
                {
                    var actual = await HashFileAsync(partPath, progress, model, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(partPath);
                        // A resumed prefix may have been damaged on disk long ago; give a clean download one chance before giving up.
                        if (resumeFrom > 0 && attempt == 0) continue;
                        throw new ModelDownloadException(ModelDownloadFailure.HashMismatch,
                            $"SHA-256 of '{id}' does not match the expected value; the file was discarded (expected {model.Sha256}, got {actual}).");
                    }
                }

                File.Move(partPath, finalPath, overwrite: true);
                progress?.Report(new(id, ModelDownloadPhase.Done, model.SizeBytes, model.SizeBytes));
                return finalPath;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FetchIntoPartAsync(ModelInfo model, string partPath, long resumeFrom, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DropFlow", "1.0"));
        if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Network, $"Could not reach {model.Url.Host}: {ex.Message}", ex);
        }

        using (response)
        {
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Our partial file is at or past the end of the remote file. Complete? Otherwise it is junk: start again.
                var total = response.Content.Headers.ContentRange?.Length;
                if (total == resumeFrom) return;
                File.Delete(partPath);
                await FetchIntoPartAsync(model, partPath, 0, progress, ct).ConfigureAwait(false);
                return;
            }

            if (!response.IsSuccessStatusCode)
                throw new ModelDownloadException(ModelDownloadFailure.Http,
                    $"Server answered {(int)response.StatusCode} {response.ReasonPhrase} for '{model.Id}'.");

            var start = 0L;
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != resumeFrom)
                {
                    // The server resumed somewhere else; do not trust it.
                    File.Delete(partPath);
                    await FetchIntoPartAsync(model, partPath, 0, progress, ct).ConfigureAwait(false);
                    return;
                }
                start = resumeFrom;
            }
            // A plain 200 to a ranged request means the server ignores Range: take the whole body and overwrite the partial file.

            var remaining = response.Content.Headers.ContentLength;
            if (remaining is { } r && start + r != model.SizeBytes)
                throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                    $"Server reports {start + r} bytes for '{model.Id}' but the catalog says {model.SizeBytes}.");

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
                        progress?.Report(new(model.Id, ModelDownloadPhase.Downloading, received, model.SizeBytes));
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Network,
                    $"No data from {model.Url.Host} for {_stallTimeout.TotalSeconds:0} s while downloading '{model.Id}' ({received} bytes kept; run it again to resume).");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Network,
                    $"Connection lost while downloading '{model.Id}' at {received} bytes ({ex.Message}). Run it again to resume.", ex);
            }
            finally
            {
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            progress?.Report(new(model.Id, ModelDownloadPhase.Downloading, received, model.SizeBytes));
        }
    }

    /// <summary>Refuses to start a download that cannot fit: 64 MB of headroom is kept for the system.</summary>
    private static void EnsureFreeSpace(ModelInfo model, string partPath)
    {
        long needed;
        try
        {
            var have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            needed = Math.Max(0, model.SizeBytes - have);
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(partPath));
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

    private static async Task<string> HashFileAsync(string path, IProgress<ModelDownloadProgress>? progress, ModelInfo model, CancellationToken ct)
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
            progress?.Report(new(model.Id, ModelDownloadPhase.Verifying, done, stream.Length));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        foreach (var gate in _locks.Values) gate.Dispose();
    }
}
