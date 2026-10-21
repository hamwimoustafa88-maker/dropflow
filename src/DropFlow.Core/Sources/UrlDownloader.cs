using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DropFlow.Core.Sources;

public enum UrlDownloadError
{
    /// <summary>Not a web address at all.</summary>
    Invalid,
    /// <summary>A scheme other than http or https (file:, ftp:, data:, javascript: ...).</summary>
    UnsupportedScheme,
    /// <summary>The address carries a user name or password.</summary>
    HasCredentials,
    /// <summary>The server answered something other than 200 OK.</summary>
    HttpStatus,
    /// <summary>More than <see cref="UrlDownloader.MaxRedirects"/> redirects in a row.</summary>
    TooManyRedirects,
    /// <summary>The file is bigger than the limit.</summary>
    TooLarge,
    /// <summary>The server could not be reached, or the connection broke.</summary>
    Network,
    /// <summary>The file could not be written to disk.</summary>
    Storage,
}

/// <summary>A download that was refused or failed. <see cref="Exception.Message"/> is English; the UI shows a translated text from <see cref="Kind"/>.</summary>
public sealed class UrlDownloadException(
    UrlDownloadError kind, string message, int statusCode = 0, string? reason = null, long limit = 0, Exception? inner = null)
    : Exception(message, inner)
{
    public UrlDownloadError Kind { get; } = kind;

    /// <summary>The HTTP status for <see cref="UrlDownloadError.HttpStatus"/>.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The HTTP reason phrase (may be empty) or the system's description of a network/disk error.</summary>
    public string? Reason { get; } = reason;

    /// <summary>The size limit in bytes for <see cref="UrlDownloadError.TooLarge"/>.</summary>
    public long Limit { get; } = limit;
}

/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Size announced by the server, or null when it did not say.</param>
/// <param name="FileName">The name the file will have (without a folder).</param>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes, string FileName)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// Downloads one file from a web address the user typed or copied. A plain GET: no cookies, no credentials, no accounts, redirects
/// followed (at most 10). The only network traffic is that request; nothing from the user's computer is ever sent.
/// The bytes go to a <c>.part</c> file that is renamed when complete and deleted on failure or cancel; an existing file is never replaced.
/// </summary>
public sealed class UrlDownloader : IDisposable
{
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxRedirects = 10;
    public const int MaxUrlLength = 8192;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly string _folder;
    private readonly long _maxBytes;

    /// <param name="folder">Where files are saved; created on demand. Defaults to <c>%USERPROFILE%\Downloads\DropFlow</c>.</param>
    /// <param name="maxBytes">Files bigger than this are refused (before downloading when the server announces the size, else mid-stream).</param>
    public UrlDownloader(string? folder = null, long maxBytes = DefaultMaxBytes)
    {
        _folder = folder ?? DefaultFolder;
        _maxBytes = maxBytes;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.None,
            ResponseHeaderEncodingSelector = (_, _) => Encoding.UTF8, // servers do send raw UTF-8 file names
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DropFlow", "1.0"));
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DropFlow");

    public string Folder => _folder;

    /// <summary>Checks that <paramref name="text"/> is exactly one http(s) address without credentials.</summary>
    public static Uri Validate(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxUrlLength || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new UrlDownloadException(UrlDownloadError.Invalid, "That is not a web address.");
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new UrlDownloadException(UrlDownloadError.Invalid, "That is not a web address.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new UrlDownloadException(UrlDownloadError.UnsupportedScheme, $"Only http and https addresses are supported, not {uri.Scheme}:.");
        if (string.IsNullOrEmpty(uri.Host))
            throw new UrlDownloadException(UrlDownloadError.Invalid, "That is not a web address.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new UrlDownloadException(UrlDownloadError.HasCredentials, "Addresses with a user name or password are not supported.");
        return uri;
    }

    public static bool TryValidate(string? text, out Uri? uri, out UrlDownloadException? error)
    {
        try { uri = Validate(text); error = null; return true; }
        catch (UrlDownloadException ex) { uri = null; error = ex; return false; }
    }

    /// <summary>The address without user info, query string or fragment — safe to write to a log or show in a message.</summary>
    public static string ForLog(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}";

    /// <summary>Downloads <paramref name="url"/> and returns the full path of the saved file.</summary>
    /// <exception cref="UrlDownloadException">The address is not allowed, the server refused, the file is too big, or the network/disk failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the partial file is already removed).</exception>
    public async Task<string> DownloadAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var requested = Validate(url);

        try { Directory.CreateDirectory(_folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Storage(ex);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, requested), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Network(ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UrlDownloadException(UrlDownloadError.Network, "The server did not answer in time.", reason: "timeout", inner: ex);
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                throw new UrlDownloadException(UrlDownloadError.TooManyRedirects, $"The address redirects more than {MaxRedirects} times.");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UrlDownloadException(
                    UrlDownloadError.HttpStatus, $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd(),
                    (int)response.StatusCode, response.ReasonPhrase);
            }

            var announced = response.Content.Headers.ContentLength;
            if (announced > _maxBytes) throw TooLarge();

            var finalUri = response.RequestMessage?.RequestUri ?? requested;
            response.Content.Headers.TryGetValues("Content-Disposition", out var dispositions);
            var wanted = DownloadNaming.Choose(
                dispositions?.FirstOrDefault(), finalUri, requested, response.Content.Headers.ContentType?.MediaType);

            Claim claim;
            try { claim = ClaimFile(_folder, wanted); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Storage(ex); }

            var completed = false;
            try
            {
                Stream source;
                try { source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
                catch (HttpRequestException ex) { throw Network(ex); }

                await using (source.ConfigureAwait(false))
                {
                    await CopyAsync(source, claim.Stream, announced, Path.GetFileName(claim.FinalPath), progress, cancellationToken).ConfigureAwait(false);
                }

                await claim.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                completed = true;
            }
            finally
            {
                await claim.Stream.DisposeAsync().ConfigureAwait(false);
                if (!completed) TryDelete(claim.PartPath);
            }

            try { return Publish(claim); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(claim.PartPath);
                throw Storage(ex);
            }
        }
    }

    private async Task CopyAsync(
        Stream source, Stream target, long? announced, string fileName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        progress?.Report(new DownloadProgress(0, announced, fileName));

        while (true)
        {
            int read;
            try { read = await source.ReadAsync(buffer, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or HttpRequestException) { throw Network(ex); }
            if (read == 0) break;

            total += read;
            if (total > _maxBytes) throw TooLarge();

            try { await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Storage(ex); }

            if (progress is not null && clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100))
            {
                lastReport = clock.Elapsed;
                progress.Report(new DownloadProgress(total, announced, fileName));
            }
        }
        progress?.Report(new DownloadProgress(total, announced ?? total, fileName));
    }

    // ------------------------------------------------------------------------------------------------------------------ files

    private sealed record Claim(string FinalPath, string PartPath, FileStream Stream);

    /// <summary>Reserves a name: exclusively creates <c>name.part</c> for a name whose final file does not exist yet.</summary>
    private static Claim ClaimFile(string folder, string fileName)
    {
        for (var n = 1; n < 10_000; n++)
        {
            var final = Path.Combine(folder, DownloadNaming.Numbered(fileName, n));
            var part = final + ".part";
            if (File.Exists(final)) continue;
            try
            {
                var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                return new Claim(final, part, stream);
            }
            catch (IOException) when (File.Exists(part) || File.Exists(final))
            {
                // another download owns this name right now: try the next number
            }
        }
        throw new IOException("Too many files with the same name.");
    }

    /// <summary>Renames the finished <c>.part</c> to its final name without ever replacing an existing file.</summary>
    private static string Publish(Claim claim)
    {
        var final = claim.FinalPath;
        var folder = Path.GetDirectoryName(final)!;
        var name = Path.GetFileName(final);
        for (var n = 1; n < 10_000; n++)
        {
            var target = n == 1 ? final : Path.Combine(folder, DownloadNaming.Numbered(name, n));
            if (File.Exists(target)) continue;
            try
            {
                File.Move(claim.PartPath, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // somebody created the file between the check and the move: take the next free name
            }
        }
        throw new IOException("Too many files with the same name.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover .part is harmless */ }
    }

    private UrlDownloadException TooLarge() =>
        new(UrlDownloadError.TooLarge, $"The file is larger than the limit of {ByteSize.Format(_maxBytes)}.", limit: _maxBytes);

    private static UrlDownloadException Network(Exception ex) =>
        new(UrlDownloadError.Network, "Could not download: " + Detail(ex), reason: Detail(ex), inner: ex);

    private static UrlDownloadException Storage(Exception ex) =>
        new(UrlDownloadError.Storage, "Could not save the file: " + ex.Message, reason: ex.Message, inner: ex);

    private static string Detail(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return inner.Message;
    }

    public void Dispose() => _client.Dispose();
}
