using System.Net;
using System.Net.Sockets;
using System.Text;
using DropFlow.Core.Localization;
using DropFlow.Core.Sources;

namespace DropFlow.Core.Tests;

/// <summary>A scripted HTTP/1.1 server on a loopback port: the handler writes raw bytes to the socket. Records every request.</summary>
internal sealed class ScriptedServer : IDisposable
{
    internal sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Request, Stream, Task> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Request> _requests = [];

    public ScriptedServer(Func<Request, Stream, Task> handler)
    {
        _handler = handler;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string target = "/") => $"http://127.0.0.1:{Port}{target}";

    public IReadOnlyList<Request> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    public static async Task Reply(Stream stream, int status, string reason, IEnumerable<string> headers, byte[]? body = null, bool withLength = true)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        foreach (var h in headers) head.Append(h).Append("\r\n");
        if (withLength) head.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()));
        if (body is { Length: > 0 }) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { /* stopped */ }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new List<byte>();
                var one = new byte[1];
                while (!EndsWithBlankLine(head))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                    head.Add(one[0]);
                }

                var lines = Encoding.UTF8.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var first = lines[0].Split(' ');
                var headers = lines.Skip(1).Select(l => l.Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var request = new Request(first[0], first[1], headers);
                lock (_requests) _requests.Add(request);
                await _handler(request, stream);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // the client went away (cancel, size limit): expected in these tests
            }
        }
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

public class UrlDownloaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string Folder => Path.Combine(_dir.Path, "Downloads", "DropFlow");

    public void Dispose() => _dir.Dispose();

    private UrlDownloader Downloader(long max = UrlDownloader.DefaultMaxBytes) => new(Folder, max);

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 pretend document");

    private static ScriptedServer Serve(string contentType, params string[] extraHeaders) =>
        new((_, s) => ScriptedServer.Reply(s, 200, "OK", [$"Content-Type: {contentType}", .. extraHeaders], Pdf));

    private string[] Files() => Directory.Exists(Folder) ? Directory.GetFiles(Folder).Select(Path.GetFileName).OfType<string>().Order().ToArray() : [];

    // ------------------------------------------------------------------------------------------------------------------ names

    [Fact]
    public async Task The_file_name_comes_from_an_Arabic_filename_star_header()
    {
        using var server = Serve("application/pdf", "Content-Disposition: attachment; filename*=UTF-8''%D8%AA%D9%82%D8%B1%D9%8A%D8%B1%20%D8%A7%D9%84%D9%85%D8%A8%D9%8A%D8%B9%D8%A7%D8%AA.pdf");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(server.Url("/dl?id=7"));

        Assert.Equal("تقرير المبيعات.pdf", Path.GetFileName(path));
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(path));
        Assert.Equal(Folder, Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task Filename_star_wins_over_plain_filename()
    {
        using var server = Serve("application/pdf", "Content-Disposition: attachment; filename=\"fallback.pdf\"; filename*=UTF-8''%D8%AA%D9%82%D8%B1%D9%8A%D8%B1.pdf");
        using var downloader = Downloader();

        Assert.Equal("تقرير.pdf", Path.GetFileName(await downloader.DownloadAsync(server.Url("/x"))));
    }

    [Fact]
    public async Task A_plain_quoted_filename_is_used_and_folders_in_it_are_dropped()
    {
        using var server = Serve("application/octet-stream", "Content-Disposition: attachment; filename=\"..\\..\\Windows\\evil.exe\"");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(server.Url("/x"));

        Assert.Equal("evil.exe", Path.GetFileName(path));
        Assert.Equal(Folder, Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task A_raw_UTF8_filename_in_the_header_is_read_correctly()
    {
        using var server = new ScriptedServer((_, s) => ScriptedServer.Reply(s, 200, "OK", ["Content-Disposition: attachment; filename=\"صورة.png\""], Pdf));
        using var downloader = Downloader();

        Assert.Equal("صورة.png", Path.GetFileName(await downloader.DownloadAsync(server.Url("/x"))));
    }

    [Fact]
    public async Task Without_a_header_the_last_path_segment_is_used_decoded_and_the_query_is_ignored()
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(server.Url("/files/My%20Report%20%D8%B9.pdf?token=secret&x=1#frag"));

        Assert.Equal("My Report ع.pdf", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("/", "image/png", "download.png")]
    [InlineData("/image/12345", "image/jpeg; charset=binary", "12345.jpg")]
    [InlineData("/doc", "application/pdf", "doc.pdf")]
    [InlineData("/", "application/x-unknown", "download")]
    public async Task The_extension_comes_from_the_content_type_when_the_address_has_none(string target, string contentType, string expected)
    {
        using var server = Serve(contentType);
        using var downloader = Downloader();

        Assert.Equal(expected, Path.GetFileName(await downloader.DownloadAsync(server.Url(target))));
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        Directory.CreateDirectory(Folder);
        var original = Path.Combine(Folder, "report.pdf");
        await File.WriteAllTextAsync(original, "precious");
        using var server = Serve("application/pdf");
        using var downloader = Downloader();

        var second = await downloader.DownloadAsync(server.Url("/report.pdf"));
        var third = await downloader.DownloadAsync(server.Url("/report.pdf"));

        Assert.Equal("report (2).pdf", Path.GetFileName(second));
        Assert.Equal("report (3).pdf", Path.GetFileName(third));
        Assert.Equal("precious", await File.ReadAllTextAsync(original));
        Assert.Equal(["report (2).pdf", "report (3).pdf", "report.pdf"], Files());
    }

    [Fact]
    public async Task Two_downloads_at_once_get_different_names()
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader();

        var paths = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => downloader.DownloadAsync(server.Url("/same.pdf"))));

        Assert.Equal(6, paths.Distinct().Count());
        Assert.Equal(6, Files().Length);
        Assert.All(paths, p => Assert.Equal(Pdf, File.ReadAllBytes(p)));
    }

    // ------------------------------------------------------------------------------------------------------------------ redirects and errors

    [Fact]
    public async Task Redirects_are_followed_the_final_address_names_the_file_and_no_cookie_is_sent()
    {
        using var server = new ScriptedServer((r, s) => r.Target.StartsWith("/start")
            ? ScriptedServer.Reply(s, 302, "Found", ["Location: /cdn/final-name.pdf", "Set-Cookie: session=abc"])
            : ScriptedServer.Reply(s, 200, "OK", ["Content-Type: application/pdf"], Pdf));
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(server.Url("/start?x=1"));

        Assert.Equal("final-name.pdf", Path.GetFileName(path));
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.False(r.Headers.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task An_endless_redirect_loop_is_stopped_with_a_clear_error()
    {
        using var server = new ScriptedServer((_, s) => ScriptedServer.Reply(s, 302, "Found", ["Location: /again"]));
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(server.Url("/again")));

        Assert.Equal(UrlDownloadError.TooManyRedirects, error.Kind);
        Assert.True(server.Requests.Count <= UrlDownloader.MaxRedirects + 1, $"{server.Requests.Count} requests");
        Assert.Empty(Files());
    }

    [Fact]
    public async Task The_request_is_a_plain_GET_with_the_DropFlow_user_agent_and_no_credentials()
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader();

        await downloader.DownloadAsync(server.Url("/a.pdf"));

        var request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.StartsWith("DropFlow", request.Headers["User-Agent"]);
        Assert.False(request.Headers.ContainsKey("Cookie"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.False(request.Headers.ContainsKey("Referer"));
    }

    [Theory]
    [InlineData(404, "Not Found")]
    [InlineData(403, "Forbidden")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(204, "No Content")]
    public async Task Anything_but_200_is_refused_with_the_status_and_leaves_nothing_behind(int status, string reason)
    {
        using var server = new ScriptedServer((_, s) => ScriptedServer.Reply(s, status, reason, [], status == 204 ? null : Encoding.ASCII.GetBytes("nope")));
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(server.Url("/missing.pdf")));

        Assert.Equal(UrlDownloadError.HttpStatus, error.Kind);
        Assert.Equal(status, error.StatusCode);
        Assert.Contains(status.ToString(), error.Message);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_connection_that_nobody_listens_on_is_a_network_error()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync($"http://127.0.0.1:{port}/x"));

        Assert.Equal(UrlDownloadError.Network, error.Kind);
    }

    [Fact]
    public async Task A_connection_that_breaks_half_way_removes_the_partial_file()
    {
        using var server = new ScriptedServer(async (_, s) =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n"));
            await s.WriteAsync(new byte[500]);
            await s.FlushAsync();
        });
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(server.Url("/big.bin")));

        Assert.Equal(UrlDownloadError.Network, error.Kind);
        Assert.Empty(Files());
    }

    // ------------------------------------------------------------------------------------------------------------------ size

    [Fact]
    public async Task A_announced_size_over_the_limit_is_refused_before_any_file_is_made()
    {
        using var server = new ScriptedServer(async (_, s) =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5000\r\nConnection: close\r\n\r\n"));
            await s.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(5)); // the body never comes: the refusal must not wait for it
        });
        using var downloader = Downloader(max: 1000);

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(server.Url("/big.bin")));

        Assert.Equal(UrlDownloadError.TooLarge, error.Kind);
        Assert.Equal(1000, error.Limit);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_streamed_body_that_grows_past_the_limit_is_aborted_and_removed()
    {
        using var server = new ScriptedServer(async (_, s) =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n")); // no Content-Length
            for (var i = 0; i < 100; i++) await s.WriteAsync(new byte[200]);
            await s.FlushAsync();
        });
        using var downloader = Downloader(max: 1000);

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(server.Url("/endless")));

        Assert.Equal(UrlDownloadError.TooLarge, error.Kind);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_file_exactly_at_the_limit_is_accepted()
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader(max: Pdf.Length);

        var path = await downloader.DownloadAsync(server.Url("/a.pdf"));

        Assert.Equal(Pdf.Length, new FileInfo(path).Length);
    }

    // ------------------------------------------------------------------------------------------------------------------ cancel and progress

    [Fact]
    public async Task Cancelling_mid_download_removes_the_part_file_and_leaves_no_file()
    {
        var firstChunkSent = new TaskCompletionSource();
        using var server = new ScriptedServer(async (_, s) =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\nContent-Type: application/zip\r\nConnection: close\r\n\r\n"));
            await s.WriteAsync(new byte[1000]);
            await s.FlushAsync();
            firstChunkSent.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30)); // stall until the client gives up
        });
        using var downloader = Downloader();
        using var cts = new CancellationTokenSource();

        var download = downloader.DownloadAsync(server.Url("/archive.zip"), null, cts.Token);
        await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.Equal(["archive.zip.part"], Files()); // the download is really under way
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_downloads_nothing()
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(server.Url("/a.pdf"), null, cts.Token));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task Progress_ends_with_everything_received_and_a_known_total()
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader();
        var reports = new List<DownloadProgress>();

        await downloader.DownloadAsync(server.Url("/a.pdf"), new SyncProgress(reports.Add));

        var last = reports[^1];
        Assert.Equal(Pdf.Length, last.BytesReceived);
        Assert.Equal(Pdf.Length, last.TotalBytes);
        Assert.Equal(1.0, last.Fraction);
        Assert.Equal("a.pdf", last.FileName);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    // ------------------------------------------------------------------------------------------------------------------ what is refused up front

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/a.zip")]
    [InlineData("data:text/plain;base64,SGk=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("\\\\server\\share\\a.pdf")]
    [InlineData("C:\\Users\\me\\a.pdf")]
    [InlineData("mailto:someone@example.com")]
    public async Task Other_schemes_are_refused_without_touching_the_disk(string address)
    {
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(address));

        Assert.Equal(UrlDownloadError.UnsupportedScheme, error.Kind);
        Assert.False(Directory.Exists(Folder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a link")]
    [InlineData("example.com/file.pdf")]
    [InlineData("http://")]
    [InlineData("https://exa mple.com/a")]
    public async Task Text_that_is_not_an_address_is_refused(string address)
    {
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(address));

        Assert.Equal(UrlDownloadError.Invalid, error.Kind);
    }

    [Theory]
    [InlineData("http://user:secret@example.com/a.pdf")]
    [InlineData("https://user@example.com/a.pdf")]
    public async Task An_address_with_credentials_is_refused_and_never_requested(string address)
    {
        using var server = Serve("application/pdf");
        using var downloader = Downloader();

        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync(address));
        var local = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync($"http://me:pw@127.0.0.1:{server.Port}/a.pdf"));

        Assert.Equal(UrlDownloadError.HasCredentials, error.Kind);
        Assert.Equal(UrlDownloadError.HasCredentials, local.Kind);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Surrounding_white_space_around_a_good_address_is_ignored()
    {
        Assert.Equal("https://example.com/a.pdf", UrlDownloader.Validate("  https://example.com/a.pdf\r\n").AbsoluteUri);
    }

    [Fact]
    public void The_log_form_of_an_address_has_no_query_fragment_or_credentials()
    {
        Assert.Equal("https://example.com/a/b.pdf", UrlDownloader.ForLog(new Uri("https://example.com/a/b.pdf?token=SECRET&x=1#frag")));
        Assert.Equal("http://example.com:8080/x", UrlDownloader.ForLog(new Uri("http://user:pw@example.com:8080/x?k=v")));
    }

    [Fact]
    public void The_default_folder_is_Downloads_DropFlow_in_the_user_profile()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DropFlow");
        Assert.Equal(expected, UrlDownloader.DefaultFolder);
    }

    // ------------------------------------------------------------------------------------------------------------------ names, unit level

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.pdf", "_COM1.pdf")]
    [InlineData("report.pdf.", "report.pdf")]
    [InlineData("a<b>c:d*e?f\"g|h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("file.txt:hidden-stream", "file.txt_hidden-stream")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\system32\\drivers\\etc\\hosts", "hosts")]
    [InlineData("invoice\u202Etxt.exe", "invoicetxt.exe")]
    [InlineData(".bashrc", ".bashrc")]
    [InlineData("تقرير.pdf", "تقرير.pdf")]
    public void Sanitize_makes_names_safe(string input, string expected) => Assert.Equal(expected, DownloadNaming.Sanitize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("???")]
    [InlineData("a/")]
    public void Sanitize_gives_null_when_nothing_usable_is_left(string? input) => Assert.Null(DownloadNaming.Sanitize(input));

    [Fact]
    public void Very_long_names_are_capped_and_keep_their_extension()
    {
        var name = DownloadNaming.Sanitize(new string('x', 500) + ".pdf")!;
        Assert.EndsWith(".pdf", name);
        Assert.True(name.Length <= 130, name.Length.ToString());

        var odd = DownloadNaming.Sanitize("a." + new string('e', 100))!;
        Assert.True(odd.Length <= 140);

        var emoji = DownloadNaming.Sanitize(string.Concat(Enumerable.Repeat("😀", 100)) + ".png")!;
        Assert.EndsWith(".png", emoji);
        Assert.DoesNotContain('\uFFFD', emoji);
    }

    [Fact]
    public void Numbered_inserts_the_counter_before_the_extension()
    {
        Assert.Equal("a.pdf", DownloadNaming.Numbered("a.pdf", 1));
        Assert.Equal("a (2).pdf", DownloadNaming.Numbered("a.pdf", 2));
        Assert.Equal("archive.tar (4).gz", DownloadNaming.Numbered("archive.tar.gz", 4));
        Assert.Equal("noext (3)", DownloadNaming.Numbered("noext", 3));
    }

    [Theory]
    [InlineData("attachment; filename=\"a b.pdf\"", "a b.pdf")]
    [InlineData("attachment; filename=a.pdf", "a.pdf")]
    [InlineData("inline; filename=\"semi;colon.pdf\"", "semi;colon.pdf")]
    [InlineData("attachment; filename=\"quo\\\"te.pdf\"", "quo\"te.pdf")]
    [InlineData("attachment; filename*=utf-8''%e2%82%ac%20rates.pdf", "€ rates.pdf")]
    [InlineData("attachment; filename*=ISO-8859-1''na%EFve.txt", "naïve.txt")]
    [InlineData("attachment; filename*=KOI8-R''x.txt; filename=\"plain.txt\"", "plain.txt")]
    [InlineData("attachment", null)]
    [InlineData("", null)]
    public void Content_disposition_parsing(string header, string? expected) =>
        Assert.Equal(expected, DownloadNaming.FromContentDisposition(header));

    // ------------------------------------------------------------------------------------------------------------------ clipboard text

    [Theory]
    [InlineData("https://example.com/file.pdf", ClipboardTextKind.SingleUrl)]
    [InlineData("  http://example.com/a?x=1&y=2  ", ClipboardTextKind.SingleUrl)]
    [InlineData("https://example.com/a.pdf\r\n", ClipboardTextKind.SingleUrl)]
    [InlineData("HTTPS://EXAMPLE.COM/A", ClipboardTextKind.SingleUrl)]
    [InlineData("https://example.com/a https://example.com/b", ClipboardTextKind.MultipleUrls)]
    [InlineData("https://example.com/a\nhttps://example.com/b\nhttp://x.org", ClipboardTextKind.MultipleUrls)]
    [InlineData("see https://example.com/a for details", ClipboardTextKind.Text)]
    [InlineData("https://example.com/a and some words", ClipboardTextKind.Text)]
    [InlineData("hello world", ClipboardTextKind.Text)]
    [InlineData("example.com/file.pdf", ClipboardTextKind.Text)]
    [InlineData("ftp://example.com/file.zip", ClipboardTextKind.Text)]
    [InlineData("file:///C:/a.pdf", ClipboardTextKind.Text)]
    [InlineData("javascript:alert(1)", ClipboardTextKind.Text)]
    [InlineData("https://example.com/a\nftp://example.com/b", ClipboardTextKind.Text)]
    [InlineData("", ClipboardTextKind.Empty)]
    [InlineData("  \r\n ", ClipboardTextKind.Empty)]
    [InlineData(null, ClipboardTextKind.Empty)]
    public void Clipboard_text_is_classified(string? text, ClipboardTextKind expected)
    {
        Assert.Equal(expected, LinkText.Classify(text, out var url));
        Assert.Equal(expected == ClipboardTextKind.SingleUrl, url is not null);
    }

    [Fact]
    public void A_single_url_is_returned_trimmed()
    {
        LinkText.Classify("  https://example.com/a.pdf?x=1 \n", out var url);
        Assert.Equal("https://example.com/a.pdf?x=1", url!.AbsoluteUri);
    }

    // ------------------------------------------------------------------------------------------------------------------ messages

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Every_failure_has_a_message_in_both_languages_without_leftover_placeholders(string language)
    {
        var loc = JsonLocalizer.Load(language);
        UrlDownloadException[] errors =
        [
            new(UrlDownloadError.Invalid, "x"),
            new(UrlDownloadError.UnsupportedScheme, "x"),
            new(UrlDownloadError.HasCredentials, "x"),
            new(UrlDownloadError.HttpStatus, "x", statusCode: 404, reason: "Not Found"),
            new(UrlDownloadError.TooManyRedirects, "x"),
            new(UrlDownloadError.TooLarge, "x", limit: 4L * 1024 * 1024 * 1024),
            new(UrlDownloadError.Network, "x", reason: "No such host is known."),
            new(UrlDownloadError.Storage, "x", reason: "Access denied"),
        ];

        foreach (var error in errors)
        {
            var text = error.Describe(loc);
            Assert.False(string.IsNullOrWhiteSpace(text), error.Kind.ToString());
            Assert.DoesNotMatch(@"\{\d+\}", text);
            Assert.NotEqual(error.Message, text);
            if (language == "ar") Assert.Matches(@"[؀-ۿ]", text);
        }
        Assert.Contains("404", errors[3].Describe(loc));
        Assert.Contains("4 GB", errors[5].Describe(loc));
    }

    [Fact]
    public void Progress_text_shows_sizes_and_copes_with_an_unknown_total()
    {
        var en = JsonLocalizer.Load("en");
        Assert.Equal("1.5 MB of 10 MB", new DownloadProgress(1_572_864, 10_485_760, "a").Describe(en));
        Assert.Equal("2 KB downloaded", new DownloadProgress(2048, null, "a").Describe(en));
        Assert.Null(new DownloadProgress(10, null, "a").Fraction);
        Assert.Equal(0.5, new DownloadProgress(5, 10, "a").Fraction);
    }
}
