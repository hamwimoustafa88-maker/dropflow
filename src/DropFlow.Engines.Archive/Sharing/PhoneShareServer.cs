using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DropFlow.Engines.Archive.Sharing;

/// <summary>How the share is reachable.</summary>
public enum ShareBindMode
{
    NotStarted,

    /// <summary>Listening on every network interface: a phone on the same Wi-Fi can open the link.</summary>
    Lan,

    /// <summary>
    /// Listening on this computer only (127.0.0.1). Windows refuses non-loopback listeners to non-administrators unless a URL
    /// reservation exists, so DropFlow falls back to this; see <see cref="PhoneShareServer.UrlAclCommand"/>.
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

    /// <summary>0 picks a free port. A fixed port is what a URL reservation (<see cref="PhoneShareServer.UrlAclCommand"/>) is made for.</summary>
    public int Port { get; init; }

    public PhoneShareStrings Strings { get; init; } = PhoneShareStrings.English;

    /// <summary>Do not even try to listen on the network (loopback only).</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>Overrides the address shown to the phone (normally detected).</summary>
    public IPAddress? LanAddress { get; init; }

    public TimeProvider? Time { get; init; }
}

public sealed record SharedFile(int Index, string Name, string FullPath, long Length);

public sealed record ShareDownloadEventArgs(SharedFile File, string? RemoteAddress, bool Completed);

/// <summary>
/// Serves a few files to a phone over the local network, with no cloud and no account. Everything lives under an unguessable
/// token: <c>http://ip:port/TOKEN/</c> is the listing page, <c>/TOKEN/f/N/name</c> the Nth file. Anything else, including a wrong
/// token, answers 404. Only the files given to the constructor can ever be read; paths are never taken from the request.
/// Downloads support HTTP Range (resume, seeking) and use RFC 6266/5987 headers so Arabic and other Unicode names survive.
/// </summary>
public sealed class PhoneShareServer : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly SharedFile[] _files;
    private readonly FileState[] _states;
    private readonly PhoneShareOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _tokenBytes;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _handlers = [];

    private HttpListener? _listener;
    private Task? _loop;
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

        _tokenBytes = RandomNumberGenerator.GetBytes(16); // 128 bits
        Token = Base64Url(_tokenBytes);
    }

    // ------------------------------------------------------------------ public surface

    /// <summary>The unguessable path segment (22 URL-safe characters).</summary>
    public string Token { get; }

    public IReadOnlyList<SharedFile> Files => _files;

    public bool IsRunning { get; private set; }

    public ShareBindMode Mode { get; private set; } = ShareBindMode.NotStarted;

    /// <summary>Why LAN listening was not possible (when <see cref="Mode"/> is <see cref="ShareBindMode.LoopbackOnly"/>), or null.</summary>
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

    /// <summary>
    /// The command that lets a non-administrator listen on the network for this port. Run it once, elevated; then start the share
    /// with <see cref="PhoneShareOptions.Port"/> set to the same port.
    /// </summary>
    public static string UrlAclCommand(int port) => $"netsh http add urlacl url=http://+:{port}/ user=Everyone";

    /// <summary>Starts listening. Tries the whole network first, then falls back to this computer only (see <see cref="Mode"/>).</summary>
    public void Start()
    {
        if (IsRunning) return;
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);

        LanIpAddress = _options.LoopbackOnly ? null : _options.LanAddress ?? LanAddress.Find();

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var port = _options.Port != 0 ? _options.Port : FreePort();

            if (LanIpAddress is not null && !_options.LoopbackOnly)
            {
                var lan = TryListen($"http://+:{port}/", out var lanError);
                if (lan is not null)
                {
                    Begin(lan, port, ShareBindMode.Lan);
                    return;
                }

                // Access denied is permanent (needs a URL reservation); anything else may be a port clash worth retrying.
                if (lanError?.ErrorCode == 5)
                {
                    LanUnavailableReason = $"Windows does not allow listening on the network without administrator rights. Run once as administrator: {UrlAclCommand(port)} (and share with Port = {port}).";
                    LanIpAddress = null;
                }
                else if (lanError is not null && _options.Port == 0)
                {
                    continue;
                }
                else if (lanError is not null)
                {
                    LanUnavailableReason = $"Could not listen on the network: {lanError.Message}";
                    LanIpAddress = null;
                }
            }
            else
            {
                LanUnavailableReason ??= _options.LoopbackOnly ? "Network sharing is turned off." : "This computer has no network address that a phone could reach.";
            }

            var loopback = TryListen($"http://127.0.0.1:{port}/", out var loopbackError);
            if (loopback is not null)
            {
                Begin(loopback, port, ShareBindMode.LoopbackOnly);
                return;
            }

            if (_options.Port != 0) throw new IOException($"Could not listen on port {port}: {loopbackError?.Message}", loopbackError);
        }

        throw new IOException("Could not find a free local port to share on.");
    }

    /// <summary>Stops sharing immediately. In-flight downloads are cut off. Safe to call more than once.</summary>
    public void Stop()
    {
        HttpListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
            IsRunning = false;
            Mode = ShareBindMode.NotStarted;
        }

        _expiryTimer?.Dispose();
        if (!_stop.IsCancellationRequested) _stop.Cancel();

        if (listener is not null)
        {
            try { listener.Abort(); }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { /* already closed */ }
        }

        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* the loop ends with an exception when the listener is aborted; that is the point */ }

        Task[] pending;
        lock (_gate) pending = [.. _handlers];
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* cut-off transfers fail; nobody is waiting for them */ }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ startup helpers

    private HttpListener? TryListen(string prefix, out HttpListenerException? error)
    {
        error = null;
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        // A phone that connects and then says nothing must not hold a connection forever.
        if (OperatingSystem.IsWindows())
        {
            listener.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(30);
            listener.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(2);
        }
        try
        {
            listener.Start();
            return listener;
        }
        catch (HttpListenerException ex)
        {
            error = ex;
            listener.Close();
            return null;
        }
    }

    private void Begin(HttpListener listener, int port, ShareBindMode mode)
    {
        lock (_gate)
        {
            _listener = listener;
            Port = port;
            Mode = mode;
            IsRunning = true;
        }

        if (_options.Lifetime is { } lifetime)
        {
            _expiresAt = _time.GetUtcNow() + lifetime;
            _expiryTimer = _time.CreateTimer(_ => Finish(), null, lifetime, Timeout.InfiniteTimeSpan);
        }

        _loop = Task.Run(() => AcceptLoop(listener));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, true)) return;
        Finished?.Invoke(this, EventArgs.Empty);
        if (_options.StopWhenFinished) _ = Task.Run(Stop);
    }

    private bool IsExpired => _expiresAt is { } at && _time.GetUtcNow() >= at;

    // ------------------------------------------------------------------ request loop

    private async Task AcceptLoop(HttpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // stopped
            }

            var task = Task.Run(() => HandleSafely(context));
            lock (_gate)
            {
                _handlers.RemoveAll(t => t.IsCompleted);
                _handlers.Add(task);
            }
        }
    }

    private async Task HandleSafely(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            TryAbort(context);
        }
        catch (Exception)
        {
            TryAbort(context); // never let one bad request take the server down
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";

        var isGet = request.HttpMethod == "GET";
        var isHead = request.HttpMethod == "HEAD";
        if (!isGet && !isHead)
        {
            Reply(response, 405, "Method Not Allowed", allow: "GET, HEAD");
            return;
        }

        var segments = ParseSegments(request.Url);
        if (segments is null || segments.Count == 0 || !TokenMatches(segments[0]))
        {
            Reply(response, 404, "Not Found");
            return;
        }

        // From here on the caller proved they hold the link.
        if (IsExpired || IsFinished)
        {
            Reply(response, 410, _options.Strings.Expired, html: true);
            return;
        }

        if (segments.Count == 1)
        {
            // "/TOKEN" without the trailing slash: relative links need the slash
            if (!request.Url!.AbsolutePath.EndsWith('/'))
            {
                response.StatusCode = 301;
                response.RedirectLocation = $"/{Token}/";
                response.Close();
                return;
            }
            await ServeListing(response, isHead).ConfigureAwait(false);
            return;
        }

        if (segments.Count is 3 or 4 && segments[1] == "f" && int.TryParse(segments[2], out var index) && index >= 0 && index < _files.Length && segments[2] == index.ToString())
        {
            await ServeFile(context, _files[index], _states[index], isHead).ConfigureAwait(false);
            return;
        }

        Reply(response, 404, "Not Found");
    }

    /// <summary>Splits the URL path into decoded segments. Returns null for anything that could hide a traversal.</summary>
    private static List<string>? ParseSegments(Uri? url)
    {
        if (url is null) return null;
        var segments = new List<string>();
        foreach (var raw in url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
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

    private async Task ServeListing(HttpListenerResponse response, bool head)
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
        response.ContentLength64 = body.Length;
        if (!head) await response.OutputStream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
        response.Close();
    }

    // ------------------------------------------------------------------ file download

    private async Task ServeFile(HttpListenerContext context, SharedFile file, FileState state, bool head)
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
            Reply(response, 404, "Not Found");
            return;
        }

        await using (stream.ConfigureAwait(false))
        {
            var length = stream.Length;
            if (!TryParseRange(request.Headers["Range"], length, out var start, out var end, out var partial))
            {
                response.Headers["Content-Range"] = $"bytes */{length}";
                Reply(response, 416, "Range Not Satisfiable");
                return;
            }

            if (head)
            {
                WriteFileHeaders(response, file, start, end, length, partial);
                response.Close();
                return;
            }

            // A single-download share hands each file out once; the claim is released if the transfer does not finish.
            if (_options.SingleDownload && !state.TryClaim())
            {
                Reply(response, 410, _options.Strings.Gone, html: true);
                return;
            }

            var remote = request.RemoteEndPoint?.Address.ToString();
            var delivered = false;
            try
            {
                DownloadStarted?.Invoke(this, new ShareDownloadEventArgs(file, remote, false));
                WriteFileHeaders(response, file, start, end, length, partial);

                stream.Seek(start, SeekOrigin.Begin);
                var remaining = end - start + 1;
                var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(1, remaining))];
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _stop.Token).ConfigureAwait(false);
                    if (read == 0) break; // the file shrank while sending: the client sees a short body and retries
                    await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
                    remaining -= read;
                }

                delivered = remaining == 0;
                response.Close();
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

    private static void WriteFileHeaders(HttpListenerResponse response, SharedFile file, long start, long end, long length, bool partial)
    {
        response.StatusCode = partial ? 206 : 200;
        response.ContentType = MimeTypes.For(file.Name);
        response.ContentLength64 = end - start + 1;
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

    private static void Reply(HttpListenerResponse response, int status, string message, bool html = false, string? allow = null)
    {
        try
        {
            response.StatusCode = status;
            if (allow is not null) response.Headers["Allow"] = allow;
            var body = html
                ? Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{WebUtility.HtmlEncode(message)}</title><p dir=\"auto\">{WebUtility.HtmlEncode(message)}</p>")
                : Encoding.UTF8.GetBytes(message);
            response.ContentType = html ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // client already gone
        }
    }

    private static void TryAbort(HttpListenerContext context)
    {
        try { context.Response.Abort(); }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { /* nothing left to abort */ }
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
