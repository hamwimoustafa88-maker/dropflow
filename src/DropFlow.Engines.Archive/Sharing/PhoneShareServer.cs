using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DropFlow.Engines.Archive.Sharing;

/// <summary>How the share is reachable.</summary>
public enum ShareBindMode
{
    NotStarted,

    /// <summary>Listening on every IPv4 network interface: a phone on the same Wi-Fi can open the link.</summary>
    Lan,

    /// <summary>
    /// Listening on this computer only (127.0.0.1). Used when the caller asked for it (<see cref="PhoneShareOptions.LoopbackOnly"/>),
    /// when this computer has no network address a phone could reach, or when binding to the network failed for another reason
    /// (see <see cref="PhoneShareServer.LanUnavailableReason"/>).
    /// </summary>
    LoopbackOnly,
}

/// <summary>Text shown on the page the phone opens. The page is served to another device, so it carries its own strings.</summary>
public sealed record PhoneShareStrings(string Title, string Intro, string Download, string Expired, string Gone, bool RightToLeft, string LanguageCode)
{
    public static PhoneShareStrings English { get; } = new(
        "Files from DropFlow", "Tap a file to download it to this device.", "Download", "This link has expired.", "This file was already downloaded.", false, "en");

    public static PhoneShareStrings Arabic { get; } = new(
        "ملفات من DropFlow", "اضغط على الملف لتنزيله على هذا الجهاز.", "تنزيل", "انتهت صلاحية هذا الرابط.", "تم تنزيل هذا الملف من قبل.", true, "ar");
}

public sealed record PhoneShareOptions
{
    /// <summary>Each file can be downloaded once; when every file has been, the share ends.</summary>
    public bool SingleDownload { get; init; }

    /// <summary>The link stops working this long after <see cref="PhoneShareServer.Start"/>. Null: until stopped.</summary>
    public TimeSpan? Lifetime { get; init; }

    /// <summary>Stop listening when the lifetime ends or every file was downloaded (single-download mode). Default true.</summary>
    public bool StopWhenFinished { get; init; } = true;

    /// <summary>0 picks a free port. A fixed port is only needed when the user has created a firewall rule for it.</summary>
    public int Port { get; init; }

    public PhoneShareStrings Strings { get; init; } = PhoneShareStrings.English;

    /// <summary>Do not listen on the network at all (127.0.0.1 only).</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>Overrides the address shown to the phone (normally detected).</summary>
    public IPAddress? LanAddress { get; init; }

    public TimeProvider? Time { get; init; }

    // Tuning knobs for tests only: the defaults below are what ships.
    internal Func<IPAddress?>? LanAddressFinder { get; init; }
    internal TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal int MaxConnections { get; init; } = 32;
    internal double MinSendBytesPerSecond { get; init; } = 2048;
    internal TimeSpan SlowClientGrace { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record SharedFile(int Index, string Name, string FullPath, long Length);

public sealed record ShareDownloadEventArgs(SharedFile File, string? RemoteAddress, bool Completed);

/// <summary>
/// Serves a few files to a phone over the local network, with no cloud and no account. Everything lives under an unguessable
/// token: <c>http://ip:port/TOKEN/</c> is the listing page, <c>/TOKEN/f/N/name</c> the Nth file. Anything else, including a wrong
/// token, answers 404. Only the files given to the constructor can ever be read; paths are never taken from the request.
/// Downloads support HTTP Range (resume, seeking) and use RFC 6266/5987 headers so Arabic and other Unicode names survive.
/// <para>
/// The server is Kestrel, which listens on a plain socket: no administrator rights and no URL reservation are needed, so the share
/// is reachable from a phone for an ordinary (non-elevated) user. Windows Defender Firewall may show its normal one-time prompt
/// the first time DropFlow listens on the network; the user has to allow it for the phone to connect. Exactly one endpoint is ever
/// opened: <c>0.0.0.0:port</c> in <see cref="ShareBindMode.Lan"/> mode, <c>127.0.0.1:port</c> otherwise. HTTPS is never used.
/// </para>
/// </summary>
public sealed class PhoneShareServer : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly SharedFile[] _files;
    private readonly FileState[] _states;
    private readonly PhoneShareOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();

    private IHost? _host;
    private ITimer? _expiryTimer;
    private DateTimeOffset? _expiresAt;
    private bool _finished;

    public PhoneShareServer(IEnumerable<string> files, PhoneShareOptions? options = null)
    {
        _options = options ?? new PhoneShareOptions();
        _time = _options.Time ?? TimeProvider.System;

        var list = new List<SharedFile>();
        foreach (var path in files)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full)) throw new ArgumentException($"'{path}' is a folder; share files (zip the folder first).", nameof(files));
            var info = new FileInfo(full);
            if (!info.Exists) throw new FileNotFoundException("File to share not found.", full);
            if (list.Any(f => string.Equals(f.FullPath, full, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new SharedFile(list.Count, info.Name, full, info.Length));
        }

        if (list.Count == 0) throw new ArgumentException("Nothing to share.", nameof(files));
        _files = [.. list];
        _states = _files.Select(_ => new FileState()).ToArray();

        Token = Base64Url(RandomNumberGenerator.GetBytes(16)); // 128 bits
    }

    // ------------------------------------------------------------------ public surface

    /// <summary>The unguessable path segment (22 URL-safe characters).</summary>
    public string Token { get; }

    public IReadOnlyList<SharedFile> Files => _files;

    public bool IsRunning { get; private set; }

    public ShareBindMode Mode { get; private set; } = ShareBindMode.NotStarted;

    /// <summary>Why the share is not reachable from the network (when <see cref="Mode"/> is <see cref="ShareBindMode.LoopbackOnly"/>), or null.</summary>
    public string? LanUnavailableReason { get; private set; }

    public int Port { get; private set; }

    /// <summary>The LAN address shown to the phone, or null when not on a network.</summary>
    public IPAddress? LanIpAddress { get; private set; }

    /// <summary>The address to open: the LAN URL in <see cref="ShareBindMode.Lan"/> mode, otherwise the loopback URL.</summary>
    public Uri ListingUrl => Mode == ShareBindMode.Lan && LanIpAddress is not null
        ? new Uri($"http://{LanIpAddress}:{Port}/{Token}/")
        : LoopbackListingUrl;

    /// <summary>Always works from this computer; what tests and a "preview on this PC" button use.</summary>
    public Uri LoopbackListingUrl => new($"http://127.0.0.1:{Port}/{Token}/");

    public Uri FileUrl(int index, bool loopback = false)
    {
        var file = _files[index];
        var baseUrl = loopback ? LoopbackListingUrl : ListingUrl;
        return new Uri(baseUrl, $"f/{file.Index}/{Uri.EscapeDataString(file.Name)}");
    }

    public DateTimeOffset? ExpiresAt => _expiresAt;

    /// <summary>True once the lifetime ran out or (single-download mode) everything was downloaded.</summary>
    public bool IsFinished => Volatile.Read(ref _finished);

    public int CompletedDownloads => _states.Sum(s => s.Completed);

    public event EventHandler<ShareDownloadEventArgs>? DownloadStarted;
    public event EventHandler<ShareDownloadEventArgs>? DownloadCompleted;

    /// <summary>Raised when the lifetime ends or every file of a single-download share was fetched.</summary>
    public event EventHandler? Finished;

    /// <summary>The addresses Kestrel reports it is listening on, e.g. <c>http://0.0.0.0:49321</c>. Always exactly one.</summary>
    internal IReadOnlyList<string> BoundAddresses { get; private set; } = [];

    /// <summary>
    /// Starts listening: on the whole network when this computer has a LAN address (and <see cref="PhoneShareOptions.LoopbackOnly"/>
    /// is not set), otherwise on this computer only (see <see cref="Mode"/> and <see cref="LanUnavailableReason"/>).
    /// A fixed <see cref="PhoneShareOptions.Port"/> that is already in use throws <see cref="IOException"/>.
    /// </summary>
    public void Start() => Task.Run(() => StartAsync()).GetAwaiter().GetResult();   // off the caller's context: safe on a UI thread

    /// <inheritdoc cref="Start"/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);

        LanIpAddress = null;
        LanUnavailableReason = null;
        IPAddress? lan = null;
        if (_options.LoopbackOnly)
            LanUnavailableReason = "Network sharing is turned off.";
        else if ((lan = _options.LanAddress ?? (_options.LanAddressFinder ?? LanAddress.Find)()) is null)
            LanUnavailableReason = "This computer has no network address that a phone could reach.";

        if (lan is not null)
        {
            try
            {
                await StartListeningAsync(IPAddress.Any, ShareBindMode.Lan, lan, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (_options.Port != 0)
            {
                throw; // a fixed port that is taken is the caller's problem, not a reason to quietly share on the wrong interface
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                LanUnavailableReason = $"Could not listen on the network: {ex.Message}";
            }
        }

        await StartListeningAsync(IPAddress.Loopback, ShareBindMode.LoopbackOnly, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops sharing immediately and frees the port. In-flight downloads are cut off. Safe to call more than once.</summary>
    public void Stop()
    {
        IHost? host;
        lock (_gate)
        {
            host = _host;
            _host = null;
            IsRunning = false;
            Mode = ShareBindMode.NotStarted;
        }

        _expiryTimer?.Dispose();
        if (!_stop.IsCancellationRequested) _stop.Cancel();   // running handlers see this and abort their connections

        if (host is not null)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Task.Run(() => host.StopAsync(deadline.Token)).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { /* connections that did not close in time are dropped with the host */ }
            finally
            {
                host.Dispose();
            }
        }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ startup helpers

    private async Task StartListeningAsync(IPAddress address, ShareBindMode mode, IPAddress? lan, CancellationToken cancellationToken)
    {
        var host = BuildHost(address, _options.Port);
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            host.Dispose();
            // "address already in use" is already an IOException (AddressInUseException); anything else is reported as one too.
            if (ex is IOException) throw;
            throw new IOException($"Could not start sharing on {address}: {ex.Message}", ex);
        }

        var bound = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        if (bound.Count != 1 || !Uri.TryCreate(bound[0], UriKind.Absolute, out var uri))
        {
            // Exactly one endpoint, or nothing: if configuration somehow added more, refuse to run.
            try { await host.StopAsync(CancellationToken.None).ConfigureAwait(false); } finally { host.Dispose(); }
            throw new IOException($"Unexpected listening addresses: {string.Join(", ", bound)}");
        }

        lock (_gate)
        {
            _host = host;
            BoundAddresses = bound;
            Port = uri.Port;
            Mode = mode;
            LanIpAddress = lan;
            IsRunning = true;
        }

        if (_options.Lifetime is { } lifetime)
        {
            _expiresAt = _time.GetUtcNow() + lifetime;
            _expiryTimer = _time.CreateTimer(_ => Finish(), null, lifetime, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A bare generic host with only Kestrel and one terminal request handler: no MVC, no routing, no config files, no logging.</summary>
    private IHost BuildHost(IPAddress address, int port) =>
        new HostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostLifetime, NoopHostLifetime>();   // no Ctrl+C / ProcessExit hooks: the app decides when to stop
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            })
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => ConfigureKestrel(kestrel, address, port))
                .Configure(app => app.Run(HandleSafelyAsync)))
            .Build();

    private void ConfigureKestrel(KestrelServerOptions kestrel, IPAddress address, int port)
    {
        kestrel.AddServerHeader = false;
        kestrel.AllowSynchronousIO = false;

        var limits = kestrel.Limits;
        limits.MaxRequestLineSize = 4 * 1024;                     // "GET /TOKEN/f/3/<escaped name> HTTP/1.1" is well under 2 KB
        limits.MaxRequestHeadersTotalSize = 8 * 1024;
        limits.MaxRequestHeaderCount = 40;
        limits.MaxRequestBodySize = 1024;                         // this server only ever answers GET and HEAD
        limits.MaxRequestBufferSize = 64 * 1024;
        limits.MaxConcurrentConnections = _options.MaxConnections;
        limits.MaxConcurrentUpgradedConnections = 0;              // no WebSockets
        limits.RequestHeadersTimeout = _options.HeaderTimeout;    // a phone that connects and says nothing is dropped
        limits.KeepAliveTimeout = _options.KeepAliveTimeout;
        limits.MinRequestBodyDataRate = new MinDataRate(240, TimeSpan.FromSeconds(10));
        limits.MinResponseDataRate = new MinDataRate(_options.MinSendBytesPerSecond, _options.SlowClientGrace);   // a stalled download is cut off

        // The one and only endpoint. Plain HTTP/1.1.
        kestrel.Listen(address, port, listen => listen.Protocols = HttpProtocols.Http1);
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, true)) return;
        Finished?.Invoke(this, EventArgs.Empty);
        if (_options.StopWhenFinished) _ = Task.Run(Stop);
    }

    private bool IsExpired => _expiresAt is { } at && _time.GetUtcNow() >= at;

    private sealed class NoopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ------------------------------------------------------------------ request handling

    private async Task HandleSafelyAsync(HttpContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled, client gone, or a bug: never let one request take the server down, and never leave a half-sent body looking complete.
            context.Abort();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";

        var head = HttpMethods.IsHead(request.Method);
        if (!head && !HttpMethods.IsGet(request.Method))
        {
            await ReplyAsync(response, 405, "Method Not Allowed", allow: "GET, HEAD").ConfigureAwait(false);
            return;
        }

        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
        var segments = ParseSegments(rawTarget, out var endsWithSlash);
        if (segments is null || segments.Count == 0 || !TokenMatches(segments[0]))
        {
            await ReplyAsync(response, 404, "Not Found").ConfigureAwait(false);
            return;
        }

        // From here on the caller proved they hold the link.
        if (IsExpired || IsFinished)
        {
            await ReplyAsync(response, 410, _options.Strings.Expired, html: true).ConfigureAwait(false);
            return;
        }

        if (segments.Count == 1)
        {
            // "/TOKEN" without the trailing slash: relative links need the slash
            if (!endsWithSlash)
            {
                response.StatusCode = 301;
                response.Headers.Location = $"/{Token}/";
                response.ContentLength = 0;
                return;
            }
            await ServeListingAsync(response, head).ConfigureAwait(false);
            return;
        }

        if (segments.Count is 3 or 4 && segments[1] == "f" && int.TryParse(segments[2], out var index) && index >= 0 && index < _files.Length && segments[2] == index.ToString())
        {
            await ServeFileAsync(context, _files[index], _states[index], head).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(response, 404, "Not Found").ConfigureAwait(false);
    }

    /// <summary>
    /// Splits the request path into decoded segments, working from the raw request target (before any server-side normalisation).
    /// Returns null for anything that could hide a traversal: ".." or "." segments, encoded or raw slashes and backslashes inside a
    /// segment, NUL characters, malformed escapes, or a target that is not a plain path.
    /// </summary>
    internal static List<string>? ParseSegments(string rawTarget, out bool endsWithSlash)
    {
        endsWithSlash = false;
        var path = rawTarget;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        if (path.Length == 0 || path[0] != '/') return null;
        endsWithSlash = path.EndsWith('/');

        var segments = new List<string>();
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(raw); }
            catch (UriFormatException) { return null; }

            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\') || decoded.Contains('\0')) return null;
            segments.Add(decoded);
        }
        return segments;
    }

    private bool TokenMatches(string candidate)
    {
        var bytes = Encoding.UTF8.GetBytes(candidate);
        var expected = Encoding.UTF8.GetBytes(Token);
        return bytes.Length == expected.Length && CryptographicOperations.FixedTimeEquals(bytes, expected);
    }

    // ------------------------------------------------------------------ listing

    private async Task ServeListingAsync(HttpResponse response, bool head)
    {
        var strings = _options.Strings;
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"").Append(WebUtility.HtmlEncode(strings.LanguageCode)).Append("\" dir=\"").Append(strings.RightToLeft ? "rtl" : "ltr").Append("\">\n");
        sb.Append("<head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(strings.Title)).Append("</title>");
        sb.Append("<style>body{font:16px/1.5 system-ui,'Segoe UI',Tahoma,sans-serif;margin:0;padding:16px;background:#f6f7f9;color:#1a1d23}");
        sb.Append("main{max-width:640px;margin:0 auto}h1{font-size:1.3rem}ul{list-style:none;padding:0}");
        sb.Append("li{display:flex;gap:12px;align-items:center;justify-content:space-between;background:#fff;border-radius:12px;padding:12px 14px;margin:8px 0;box-shadow:0 1px 2px rgba(0,0,0,.08)}");
        sb.Append("a{color:#3730a3;font-weight:600;word-break:break-all;unicode-bidi:plaintext}small{color:#5b6270;white-space:nowrap}");
        sb.Append(".gone{opacity:.55}</style></head>\n<body><main>\n");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(strings.Title)).Append("</h1>\n<p>").Append(WebUtility.HtmlEncode(strings.Intro)).Append("</p>\n<ul>\n");

        foreach (var file in _files)
        {
            var consumed = _options.SingleDownload && _states[file.Index].IsConsumed;
            var href = $"f/{file.Index}/{Uri.EscapeDataString(file.Name)}";
            sb.Append("<li").Append(consumed ? " class=\"gone\"" : "").Append("><span dir=\"auto\">");
            if (consumed) sb.Append(WebUtility.HtmlEncode(file.Name)).Append(" - ").Append(WebUtility.HtmlEncode(strings.Gone));
            else sb.Append("<a dir=\"auto\" href=\"").Append(WebUtility.HtmlEncode(href)).Append("\" download>").Append(WebUtility.HtmlEncode(file.Name)).Append("</a>");
            sb.Append("</span><small>").Append(FormatSize(file.Length)).Append("</small></li>\n");
        }

        sb.Append("</ul>\n</main></body></html>\n");

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
        response.ContentLength = body.Length;
        if (!head) await response.Body.WriteAsync(body, _stop.Token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ file download

    private async Task ServeFileAsync(HttpContext context, SharedFile file, FileState state, bool head)
    {
        var request = context.Request;
        var response = context.Response;

        FileStream stream;
        try
        {
            stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReplyAsync(response, 404, "Not Found").ConfigureAwait(false);
            return;
        }

        await using (stream.ConfigureAwait(false))
        {
            var length = stream.Length;
            if (!TryParseRange(request.Headers.Range.ToString(), length, out var start, out var end, out var partial))
            {
                response.Headers["Content-Range"] = $"bytes */{length}";
                await ReplyAsync(response, 416, "Range Not Satisfiable").ConfigureAwait(false);
                return;
            }

            if (head)
            {
                WriteFileHeaders(response, file, start, end, length, partial);
                return;
            }

            // A single-download share hands each file out once; the claim is released if the transfer does not finish.
            if (_options.SingleDownload && !state.TryClaim())
            {
                await ReplyAsync(response, 410, _options.Strings.Gone, html: true).ConfigureAwait(false);
                return;
            }

            var remote = context.Connection.RemoteIpAddress?.ToString();
            var delivered = false;
            try
            {
                DownloadStarted?.Invoke(this, new ShareDownloadEventArgs(file, remote, false));
                WriteFileHeaders(response, file, start, end, length, partial);

                // The transfer ends when the client goes away or the share is stopped, whichever comes first.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
                var token = linked.Token;

                stream.Seek(start, SeekOrigin.Begin);
                var remaining = end - start + 1;
                var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(1, remaining))];
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (read == 0) break; // the file shrank while sending: the client sees a short body and retries
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    remaining -= read;
                }

                if (remaining == 0)
                {
                    await response.CompleteAsync().ConfigureAwait(false);   // the last byte has been handed to the network
                    delivered = true;
                }
            }
            finally
            {
                // The file counts as downloaded when its last byte went out.
                var complete = delivered && end == length - 1;
                if (_options.SingleDownload)
                {
                    if (complete) state.MarkConsumed();
                    else state.Release();
                }
                if (complete)
                {
                    state.CountCompleted();
                    DownloadCompleted?.Invoke(this, new ShareDownloadEventArgs(file, remote, true));
                    if (_options.SingleDownload && _states.All(s => s.IsConsumed)) Finish();
                }
            }
        }
    }

    private static void WriteFileHeaders(HttpResponse response, SharedFile file, long start, long end, long length, bool partial)
    {
        response.StatusCode = partial ? 206 : 200;
        response.ContentType = MimeTypes.For(file.Name);
        response.ContentLength = end - start + 1;
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Content-Disposition"] = ContentDispositionHeader.Attachment(file.Name);
        if (partial) response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
    }

    /// <summary>
    /// Parses a single "bytes=a-b" range. No header, an unsupported unit or several ranges mean "send everything" (allowed by
    /// RFC 9110). Returns false when the range cannot be satisfied (416).
    /// </summary>
    internal static bool TryParseRange(string? header, long length, out long start, out long end, out bool partial)
    {
        start = 0;
        end = length - 1;
        partial = false;
        if (string.IsNullOrWhiteSpace(header) || length == 0) return true;

        const string unit = "bytes=";
        if (!header.StartsWith(unit, StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return true;

        var spec = header[unit.Length..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return true;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();

        if (first.Length == 0)
        {
            // suffix range "bytes=-500": the last 500 bytes
            if (!long.TryParse(last, out var suffix) || suffix < 0) return true;   // malformed: ignore the header
            if (suffix == 0) return false;                                         // asks for nothing: unsatisfiable
            start = Math.Max(0, length - suffix);
            partial = true;
            return true;
        }

        if (!long.TryParse(first, out var from) || from < 0) return true;         // malformed: ignore the header
        if (from >= length) return false;
        var to = length - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, out to) || to < from) return true;            // malformed or inverted: ignore the header
            to = Math.Min(to, length - 1);
        }

        start = from;
        end = to;
        partial = true;
        return true;
    }

    // ------------------------------------------------------------------ small helpers

    private async Task ReplyAsync(HttpResponse response, int status, string message, bool html = false, string? allow = null)
    {
        response.StatusCode = status;
        if (allow is not null) response.Headers["Allow"] = allow;
        var body = html
            ? Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{WebUtility.HtmlEncode(message)}</title><p dir=\"auto\">{WebUtility.HtmlEncode(message)}</p>")
            : Encoding.UTF8.GetBytes(message);
        response.ContentType = html ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, _stop.Token).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => $"{bytes} B",
    };

    /// <summary>Per-file bookkeeping for single-download mode.</summary>
    private sealed class FileState
    {
        private int _status;       // 0 available, 1 in progress, 2 consumed
        private int _completed;

        public bool IsConsumed => Volatile.Read(ref _status) == 2;
        public int Completed => Volatile.Read(ref _completed);
        public bool TryClaim() => Interlocked.CompareExchange(ref _status, 1, 0) == 0;
        public void Release() => Interlocked.CompareExchange(ref _status, 0, 1);
        public void MarkConsumed() => Interlocked.Exchange(ref _status, 2);
        public void CountCompleted() => Interlocked.Increment(ref _completed);
    }
}
