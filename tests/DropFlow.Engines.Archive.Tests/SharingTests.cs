using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Engines.Archive.Images;
using DropFlow.Engines.Archive.Sharing;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

/// <summary>A clock the test moves by hand; its timers fire when the time is advanced past them.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>When false, timers never fire: simulates a timer that is late so only the per-request clock check protects the link.</summary>
    public bool FireTimers { get; set; } = true;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, _now + dueTime);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        List<ManualTimer> due;
        lock (_timers) due = _timers.Where(t => !t.Disposed && !t.Fired && t.Due <= _now).ToList();
        if (FireTimers)
            foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;
        public bool Disposed { get; private set; }
        public bool Fired { get; private set; }

        public void Fire()
        {
            Fired = true;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

public class SharingTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly List<PhoneShareServer> _servers = [];
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        foreach (var server in _servers) server.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    private PhoneShareServer Start(string[] files, PhoneShareOptions? options = null)
    {
        var server = new PhoneShareServer(files, options ?? new PhoneShareOptions { LoopbackOnly = true });
        _servers.Add(server);
        server.Start();
        return server;
    }

    private string File_(string name, byte[] content)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static Uri Loopback(PhoneShareServer server, string path = "") => new(server.LoopbackListingUrl, path);

    // ---------------------------------------------------------------- the basics

    [Fact]
    public async Task Serves_a_listing_and_the_file_bytes_under_the_token()
    {
        var data = Kit.Noise(100_000);
        var server = Start([File_("photo.bin", data)]);

        var listing = await _http.GetAsync(server.LoopbackListingUrl);
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.Equal("text/html", listing.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", listing.Content.Headers.ContentType.CharSet);
        var html = await listing.Content.ReadAsStringAsync();
        Assert.Contains("photo.bin", html);
        Assert.Contains("97.7 KB", html);
        Assert.DoesNotContain(_temp.Path, html);                                         // never leaks local paths

        var download = await _http.GetAsync(server.FileUrl(0, loopback: true));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(data, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("bytes", download.Headers.AcceptRanges.Single());
        Assert.Equal(data.Length, download.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Listing_headers_forbid_sniffing_caching_and_scripts()
    {
        var server = Start([File_("a.txt", Kit.Bytes("x"))]);

        var response = await _http.GetAsync(server.LoopbackListingUrl);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_listing_is_right_to_left_friendly_for_arabic_and_escapes_names()
    {
        var arabic = File_($"{Kit.ArabicFile}.txt", Kit.Bytes("1"));
        var tricky = File_("a&b 'q' #1.txt", Kit.Bytes("2"));
        var server = Start([arabic, tricky], new PhoneShareOptions { LoopbackOnly = true, Strings = PhoneShareStrings.Arabic });

        var html = await _http.GetStringAsync(server.LoopbackListingUrl);

        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", html);
        Assert.Contains("dir=\"auto\"", html);                                            // each file name picks its own direction
        Assert.Contains(Kit.ArabicFile + ".txt", html);
        Assert.Contains("a&amp;b &#39;q&#39; #1.txt", html);                              // HTML-escaped, never raw
        Assert.DoesNotContain("a&b", html);
        Assert.Contains(PhoneShareStrings.Arabic.Title, html);

        var english = await new HttpClient().GetStringAsync(Start([arabic]).LoopbackListingUrl);
        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", english);
    }

    [Fact]
    public async Task Every_link_on_the_listing_page_downloads_the_right_file()
    {
        var files = new Dictionary<string, byte[]>
        {
            [$"{Kit.ArabicFile}.pdf"] = Kit.Noise(2000, 1),
            ["plain name.txt"] = Kit.Bytes("plain"),
            ["50% off & more #1 (final).txt"] = Kit.Bytes("percent"),
            ["emoji \U0001F600.txt"] = Kit.Bytes("grin"),
        };
        var paths = files.Select(kv => File_(kv.Key, kv.Value)).ToArray();
        var server = Start(paths);

        var html = await _http.GetStringAsync(server.LoopbackListingUrl);
        var hrefs = Regex.Matches(html, "href=\"([^\"]+)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Equal(files.Count, hrefs.Count);

        foreach (var href in hrefs)
        {
            var response = await _http.GetAsync(new Uri(server.LoopbackListingUrl, href));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var name = response.Content.Headers.ContentDisposition!.FileNameStar!;
            Assert.Equal(files[name], await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task Unicode_file_names_use_rfc5987_content_disposition_with_an_ascii_fallback()
    {
        var name = $"{Kit.ArabicFile}.pdf";
        var server = Start([File_(name, Kit.Bytes("pdf"))]);

        var response = await _http.GetAsync(server.FileUrl(0, loopback: true));
        var raw = response.Headers.TryGetValues("Content-Disposition", out var values) ? values.Single() : response.Content.Headers.GetValues("Content-Disposition").Single();

        Assert.StartsWith("attachment; filename=\"", raw);
        Assert.Contains("filename*=UTF-8''", raw);
        Assert.All(raw, c => Assert.True(c < 128, "the header itself must be pure ASCII"));
        Assert.Equal(name, response.Content.Headers.ContentDisposition!.FileNameStar);     // the framework's own RFC 5987 decoder agrees
        Assert.Equal("download.pdf", response.Content.Headers.ContentDisposition.FileName);  // fallback has no letters left, so it is generic
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf", "report.pdf")]
    [InlineData("caf\u00E9.txt", "caf_.txt", "caf%C3%A9.txt")]
    [InlineData("a b.txt", "a b.txt", "a%20b.txt")]
    [InlineData("\u20AC rates.csv", "_ rates.csv", "%E2%82%AC%20rates.csv")]
    [InlineData("50%.txt", "50_.txt", "50%25.txt")]
    [InlineData("it's.txt", "it's.txt", "it%27s.txt")]
    [InlineData("a;b=c.txt", "a_b=c.txt", "a%3Bb%3Dc.txt")]
    public void Content_disposition_encoding_vectors(string name, string fallback, string encoded)
    {
        Assert.Equal(fallback, ContentDispositionHeader.AsciiFallback(name));
        Assert.Equal(encoded, ContentDispositionHeader.Rfc5987Encode(name));
        Assert.Equal($"attachment; filename=\"{fallback}\"; filename*=UTF-8''{encoded}", ContentDispositionHeader.Attachment(name));
    }

    [Fact]
    public void Content_disposition_cannot_be_used_for_header_injection()
    {
        var header = ContentDispositionHeader.Attachment("evil\"\r\nSet-Cookie: x=1\r\n.txt");

        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);
        Assert.Matches("^attachment; filename=\"[^\"\\\\]*\"; filename\\*=UTF-8''[A-Za-z0-9!#$&+\\-.^_`|~%]*$", header);
    }

    // ---------------------------------------------------------------- the token

    [Fact]
    public async Task Without_the_exact_token_everything_is_404_with_the_same_answer()
    {
        var server = Start([File_("secret.txt", Kit.Bytes("secret"))]);
        var port = server.Port;
        var token = server.Token;
        var wrong = new[]
        {
            $"http://127.0.0.1:{port}/",
            $"http://127.0.0.1:{port}/index.html",
            $"http://127.0.0.1:{port}/wrong-token/",
            $"http://127.0.0.1:{port}/{token}x/",
            $"http://127.0.0.1:{port}/{token[..^1]}/",
            $"http://127.0.0.1:{port}/{token.ToUpperInvariant()}/",
            $"http://127.0.0.1:{port}/{token.ToLowerInvariant()}/",
            $"http://127.0.0.1:{port}/wrong-token/f/0/secret.txt",
            $"http://127.0.0.1:{port}/f/0/secret.txt",
            $"http://127.0.0.1:{port}/secret.txt",
        };

        var bodies = new HashSet<string>();
        foreach (var url in wrong)
        {
            if (new Uri(url).AbsolutePath == $"/{token}/") continue;   // a case-folded copy can equal the real token only by freak chance
            var response = await _http.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies);          // wrong token, wrong path, no path: indistinguishable, so nothing can be probed
        Assert.DoesNotContain("secret", bodies.Single());
    }

    [Fact]
    public void The_token_is_128_random_bits_in_url_safe_text_and_differs_per_share()
    {
        var file = File_("a.txt", Kit.Bytes("x"));
        var tokens = Enumerable.Range(0, 50).Select(_ => new PhoneShareServer([file]).Token).ToList();

        Assert.All(tokens, t =>
        {
            Assert.Equal(22, t.Length);
            Assert.Matches("^[A-Za-z0-9_-]{22}$", t);
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public async Task The_token_without_a_trailing_slash_redirects_to_the_listing()
    {
        var server = Start([File_("a.txt", Kit.Bytes("x"))]);

        var response = await _http.GetAsync($"http://127.0.0.1:{server.Port}/{server.Token}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/{server.Token}/", response.Headers.Location!.OriginalString);
    }

    // ---------------------------------------------------------------- only the listed files, no traversal

    [Fact]
    public async Task Only_the_listed_files_can_be_fetched_and_never_by_path()
    {
        var shared = File_("shared.txt", Kit.Bytes("shared"));
        File_("secret.txt", Kit.Bytes("TOP SECRET"));
        Directory.CreateDirectory(_temp.Combine("sub"));
        File.WriteAllText(_temp.Combine("sub/inner.txt"), "INNER SECRET");
        var server = Start([shared]);

        var attempts = new[]
        {
            $"/{server.Token}/secret.txt",
            $"/{server.Token}/f/0/secret.txt",           // the name segment is cosmetic: it must not select a file
            $"/{server.Token}/f/1",
            $"/{server.Token}/f/-1",
            $"/{server.Token}/f/00",
            $"/{server.Token}/f/0x",
            $"/{server.Token}/f/+0",
            $"/{server.Token}/f/99999999999999999999",
            $"/{server.Token}/f/",
            $"/{server.Token}/f",
            $"/{server.Token}/sub/inner.txt",
        };

        foreach (var path in attempts)
        {
            var response = await _http.GetAsync($"http://127.0.0.1:{server.Port}{path}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET", body);
            if (path.EndsWith("/f/0/secret.txt", StringComparison.Ordinal)) Assert.Equal("shared", body);   // served by index, not by that name
            else Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/{t}/../secret.txt")]
    [InlineData("/{t}/f/0/../../secret.txt")]
    [InlineData("/{t}/f/..%2f..%2fsecret.txt")]
    [InlineData("/{t}/f/0%2f..%2f..%2fsecret.txt")]
    [InlineData("/{t}/%2e%2e/secret.txt")]
    [InlineData("/{t}/%2E%2E%2Fsecret.txt")]
    [InlineData("/{t}/f/0/..\\..\\secret.txt")]
    [InlineData("/{t}/f/0/%5c..%5csecret.txt")]
    [InlineData("/{t}/./f/./0/./")]
    [InlineData("//{t}/../secret.txt")]
    [InlineData("/{t}/f/0/..%00/secret.txt")]
    [InlineData("/{t}/f/0/%2e%2e/%2e%2e/secret.txt")]
    [InlineData("/%2e%2e/%2e%2e/windows/win.ini")]
    [InlineData("/{t}/f/0/C:/Windows/win.ini")]
    [InlineData("/{t}/f/0/..%c0%af..%c0%afsecret.txt")]
    public async Task Path_traversal_attempts_never_reach_other_files(string template)
    {
        var shared = File_("shared.txt", Kit.Bytes("shared"));
        File_("secret.txt", Kit.Bytes("TOP SECRET"));
        var server = Start([shared]);

        var (status, _, body) = await RawGet(server.Port, template.Replace("{t}", server.Token));

        Assert.DoesNotContain("TOP SECRET", Encoding.UTF8.GetString(body));
        Assert.DoesNotContain("[fonts]", Encoding.UTF8.GetString(body));      // win.ini
        Assert.True(status is 200 or 301 or 400 or 403 or 404, $"unexpected status {status}");
        if (status == 200) Assert.Contains("shared", Encoding.UTF8.GetString(body));   // only the shared file or the listing may ever answer
    }

    [Fact]
    public async Task Other_methods_are_refused()
    {
        var server = Start([File_("a.txt", Kit.Bytes("x"))]);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Options })
        {
            var response = await _http.SendAsync(new HttpRequestMessage(method, server.FileUrl(0, loopback: true)) { Content = new StringContent("x") });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    // ---------------------------------------------------------------- range requests

    [Fact]
    public async Task Range_requests_return_206_with_exactly_the_requested_bytes()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        var server = Start([File_("r.bin", data)]);
        var url = server.FileUrl(0, loopback: true);

        async Task<HttpResponseMessage> Get(string range)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Range", range);
            return await _http.SendAsync(request);
        }

        var first = await Get("bytes=0-9");
        Assert.Equal(HttpStatusCode.PartialContent, first.StatusCode);
        Assert.Equal("bytes 0-9/1000", first.Content.Headers.ContentRange!.ToString());
        Assert.Equal(data[..10], await first.Content.ReadAsByteArrayAsync());

        var middle = await Get("bytes=100-199");
        Assert.Equal(data[100..200], await middle.Content.ReadAsByteArrayAsync());
        Assert.Equal(100, middle.Content.Headers.ContentLength);

        var open = await Get("bytes=990-");
        Assert.Equal("bytes 990-999/1000", open.Content.Headers.ContentRange!.ToString());
        Assert.Equal(data[990..], await open.Content.ReadAsByteArrayAsync());

        var suffix = await Get("bytes=-5");
        Assert.Equal("bytes 995-999/1000", suffix.Content.Headers.ContentRange!.ToString());
        Assert.Equal(data[995..], await suffix.Content.ReadAsByteArrayAsync());

        var clamped = await Get("bytes=900-5000");
        Assert.Equal("bytes 900-999/1000", clamped.Content.Headers.ContentRange!.ToString());

        var whole = await Get("bytes=0-");
        Assert.Equal(HttpStatusCode.PartialContent, whole.StatusCode);
        Assert.Equal(data, await whole.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Unsatisfiable_ranges_get_416_and_unusable_ones_get_the_whole_file()
    {
        var data = Kit.Noise(100);
        var server = Start([File_("r.bin", data)]);
        var url = server.FileUrl(0, loopback: true);

        async Task<HttpResponseMessage> Get(string range)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Range", range);
            return await _http.SendAsync(request);
        }

        var beyond = await Get("bytes=100-200");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
        Assert.Equal("bytes */100", beyond.Content.Headers.ContentRange?.ToString() ?? string.Join("", beyond.Headers.GetValues("Content-Range")));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await Get("bytes=-0")).StatusCode);

        // Well-formed but unusable ranges are ignored (RFC 9110): the whole file comes back.
        foreach (var ignored in new[] { "bytes=5-2", "items=0-5", "bytes=0-1,5-9", "garbage" })
        {
            var response = await Get(ignored);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Range: {ignored} gave {(int)response.StatusCode}");
            Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());
        }

        // Syntactically broken ones never reach this code: Windows' HTTP.sys answers 400 itself (verified with a bare HttpListener).
        foreach (var broken in new[] { "bytes=abc-def", "bytes=", "bytes=1-x" })
        {
            var response = await Get(broken);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"Range: {broken} gave {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Head_returns_the_headers_without_a_body_and_does_not_use_up_a_single_download()
    {
        var data = Kit.Noise(5000);
        var server = Start([File_("h.bin", data)], new PhoneShareOptions { LoopbackOnly = true, SingleDownload = true, StopWhenFinished = false });

        var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.FileUrl(0, loopback: true)));

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(5000, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", head.Content.Headers.ContentDisposition!.DispositionType);

        var get = await _http.GetAsync(server.FileUrl(0, loopback: true));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);                           // the HEAD did not consume it
    }

    [Theory]
    [InlineData(null, 1000, true, 0L, 999L, false)]
    [InlineData("bytes=0-9", 1000, true, 0L, 9L, true)]
    [InlineData("bytes=10-", 1000, true, 10L, 999L, true)]
    [InlineData("bytes=-10", 1000, true, 990L, 999L, true)]
    [InlineData("bytes=-5000", 1000, true, 0L, 999L, true)]
    [InlineData("bytes=0-0", 1000, true, 0L, 0L, true)]
    [InlineData("bytes=999-999", 1000, true, 999L, 999L, true)]
    [InlineData("bytes=1000-", 1000, false, 0L, 0L, false)]
    [InlineData("bytes=-0", 1000, false, 0L, 0L, false)]
    [InlineData("bytes=1-2", 0, true, 0L, -1L, false)]
    [InlineData("bytes=0-1,4-5", 1000, true, 0L, 999L, false)]
    [InlineData("pages=1-2", 1000, true, 0L, 999L, false)]
    public void Range_header_parsing(string? header, long length, bool satisfiable, long start, long end, bool partial)
    {
        var ok = PhoneShareServer.TryParseRange(header, length, out var s, out var e, out var p);

        Assert.Equal(satisfiable, ok);
        if (!satisfiable) return;
        Assert.Equal(start, s);
        Assert.Equal(end, e);
        Assert.Equal(partial, p);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task Many_downloads_at_once_all_arrive_intact()
    {
        var big = Kit.Noise(3_000_000, 7);
        var small = Kit.Noise(20_000, 8);
        var server = Start([File_("big.bin", big), File_("small.bin", small)]);
        var events = 0;
        server.DownloadCompleted += (_, _) => Interlocked.Increment(ref events);

        var tasks = Enumerable.Range(0, 24).Select(async i =>
        {
            var index = i % 2;
            var bytes = await _http.GetByteArrayAsync(server.FileUrl(index, loopback: true));
            Assert.Equal(Kit.Hash(index == 0 ? big : small), Kit.Hash(bytes));
        }).Append(Task.Run(async () => Assert.Contains("big.bin", await _http.GetStringAsync(server.LoopbackListingUrl)))).ToArray();

        await Task.WhenAll(tasks);

        await Kit.Eventually(() => events, n => n == 24);
        Assert.Equal(24, events);
        Assert.Equal(24, server.CompletedDownloads);
    }

    [Fact]
    public async Task A_slow_download_does_not_block_other_requests()
    {
        var big = Kit.Noise(30_000_000, 9);
        var server = Start([File_("big.bin", big), File_("tiny.txt", Kit.Bytes("tiny"))]);

        using var slow = await _http.GetAsync(server.FileUrl(0, loopback: true), HttpCompletionOption.ResponseHeadersRead);
        await using var slowStream = await slow.Content.ReadAsStreamAsync();
        var one = new byte[1024];
        await slowStream.ReadExactlyAsync(one);                 // started, deliberately not finished

        var tiny = await _http.GetStringAsync(server.FileUrl(1, loopback: true));
        Assert.Equal("tiny", tiny);
        Assert.Contains("tiny.txt", await _http.GetStringAsync(server.LoopbackListingUrl));
    }

    // ---------------------------------------------------------------- single download and expiry

    [Fact]
    public async Task Single_download_mode_serves_each_file_once_then_says_gone()
    {
        var a = File_("a.txt", Kit.Bytes("A"));
        var b = File_("b.txt", Kit.Bytes("B"));
        var server = Start([a, b], new PhoneShareOptions { LoopbackOnly = true, SingleDownload = true, StopWhenFinished = false });
        var finished = 0;
        server.Finished += (_, _) => Interlocked.Increment(ref finished);

        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));
        var again = await _http.GetAsync(server.FileUrl(0, loopback: true));
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.DoesNotContain("A", (await again.Content.ReadAsStringAsync()).Replace("already downloaded", ""));   // no content in the error

        var listing = await _http.GetStringAsync(server.LoopbackListingUrl);
        Assert.Contains("class=\"gone\"", listing);                                       // the consumed file is greyed out and unlinked
        Assert.DoesNotContain("href=\"f/0/", listing);
        Assert.Contains("href=\"f/1/", listing);
        Assert.False(server.IsFinished);

        Assert.Equal("B", await _http.GetStringAsync(server.FileUrl(1, loopback: true)));
        await Kit.Eventually(() => finished, n => n == 1);

        Assert.Equal(1, finished);
        Assert.True(server.IsFinished);
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.LoopbackListingUrl)).StatusCode);   // the whole share is over
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.FileUrl(1, loopback: true))).StatusCode);
    }

    [Fact]
    public async Task Single_download_stops_listening_when_everything_was_fetched()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, SingleDownload = true });
        var finished = new TaskCompletionSource();
        server.Finished += (_, _) => finished.TrySetResult();

        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Kit.Eventually(() => server.IsRunning, running => !running);

        Assert.False(server.IsRunning);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync(server.LoopbackListingUrl));
    }

    [Fact]
    public async Task Concurrent_requests_for_a_single_download_file_get_it_exactly_once()
    {
        var server = Start([File_("once.bin", Kit.Noise(8_000_000))], new PhoneShareOptions { LoopbackOnly = true, SingleDownload = true, StopWhenFinished = false });

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await _http.GetAsync(server.FileUrl(0, loopback: true));
            if (response.StatusCode == HttpStatusCode.OK) await response.Content.ReadAsByteArrayAsync();
            return response.StatusCode;
        }));

        Assert.Equal(1, responses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(s => s == HttpStatusCode.Gone));
        Assert.Equal(1, server.CompletedDownloads);
    }

    [Fact]
    public async Task A_cut_off_single_download_can_be_retried_but_a_finished_one_cannot()
    {
        var data = Kit.Noise(40_000_000, 3);
        var server = Start([File_("big.bin", data)], new PhoneShareOptions { LoopbackOnly = true, SingleDownload = true, StopWhenFinished = false });

        // Start the download and abandon it after a few bytes. This client has a tiny receive buffer: on a fast machine the kernel's loopback
        // buffers can swallow all 40 MB, and the server (which counts a file as delivered when its last byte went out) would then rightly
        // consider it delivered. With a 4 KB window only a few MB can be in flight, so the transfer is still running when the client leaves.
        using var narrow = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveBufferSize = 4096 };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
        using (var abandoned = await narrow.GetAsync(server.FileUrl(0, loopback: true), HttpCompletionOption.ResponseHeadersRead))
        await using (var stream = await abandoned.Content.ReadAsStreamAsync())
        {
            await stream.ReadExactlyAsync(new byte[4096]);
        }

        // the server notices the broken connection and gives the file back
        HttpStatusCode status = HttpStatusCode.Gone;
        byte[] body = [];
        for (var attempt = 0; attempt < 100 && status != HttpStatusCode.OK; attempt++)
        {
            await Task.Delay(50);
            using var retry = await _http.GetAsync(server.FileUrl(0, loopback: true));
            status = retry.StatusCode;
            if (status == HttpStatusCode.OK) body = await retry.Content.ReadAsByteArrayAsync();
        }

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Kit.Hash(data), Kit.Hash(body));
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.FileUrl(0, loopback: true))).StatusCode);
    }

    [Fact]
    public async Task A_partial_range_does_not_use_up_a_single_download_but_the_final_part_does()
    {
        var data = Kit.Noise(10_000);
        var server = Start([File_("p.bin", data), File_("q.bin", Kit.Bytes("q"))], new PhoneShareOptions { LoopbackOnly = true, SingleDownload = true, StopWhenFinished = false });
        var url = server.FileUrl(0, loopback: true);

        async Task<HttpResponseMessage> Get(string range)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Range", range);
            var response = await _http.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            return response;
        }

        Assert.Equal(HttpStatusCode.PartialContent, (await Get("bytes=0-4999")).StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, (await Get("bytes=0-4999")).StatusCode);   // still available: the end was not reached
        Assert.Equal(HttpStatusCode.PartialContent, (await Get("bytes=5000-")).StatusCode);    // delivers the last byte: consumed now
        Assert.Equal(HttpStatusCode.Gone, (await Get("bytes=0-10")).StatusCode);
    }

    [Fact]
    public async Task The_link_expires_after_its_lifetime_even_for_the_right_token()
    {
        var clock = new ManualTimeProvider();
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, Lifetime = TimeSpan.FromMinutes(10), StopWhenFinished = false, Time = clock });
        var finished = 0;
        server.Finished += (_, _) => Interlocked.Increment(ref finished);

        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), server.ExpiresAt);
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));       // still valid
        Assert.False(server.IsFinished);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, finished);
        Assert.True(server.IsFinished);
        var listing = await _http.GetAsync(server.LoopbackListingUrl);
        Assert.Equal(HttpStatusCode.Gone, listing.StatusCode);
        Assert.Contains(PhoneShareStrings.English.Expired, await listing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.FileUrl(0, loopback: true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"http://127.0.0.1:{server.Port}/wrong/")).StatusCode);   // wrong tokens still learn nothing
    }

    [Fact]
    public async Task Expiry_checks_the_clock_on_each_request_so_it_holds_even_if_the_timer_is_late()
    {
        var clock = new ManualTimeProvider { FireTimers = false };
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, Lifetime = TimeSpan.FromSeconds(30), StopWhenFinished = false, Time = clock });

        clock.Advance(TimeSpan.FromSeconds(31));      // time passes, the timer callback never runs

        Assert.False(server.IsFinished);
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.FileUrl(0, loopback: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.LoopbackListingUrl)).StatusCode);
    }

    [Fact]
    public async Task Expiry_stops_the_server_by_default()
    {
        var clock = new ManualTimeProvider();
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, Lifetime = TimeSpan.FromMinutes(1), Time = clock });

        clock.Advance(TimeSpan.FromMinutes(2));
        await Kit.Eventually(() => server.IsRunning, running => !running);

        Assert.False(server.IsRunning);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync(server.LoopbackListingUrl));
    }

    // ---------------------------------------------------------------- lifecycle, binding

    [Fact]
    public async Task Stop_closes_the_port_and_can_be_called_twice()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))]);
        var url = server.LoopbackListingUrl;
        Assert.True(server.IsRunning);
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));

        server.Stop();
        server.Stop();
        server.Dispose();

        Assert.False(server.IsRunning);
        Assert.Equal(ShareBindMode.NotStarted, server.Mode);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync(url));
        Assert.Throws<ObjectDisposedException>(() => server.Start());                      // a stopped share is finished: make a new one
    }

    [MacOsUnverifiedFact("on the CI Mac the client of a download in progress saw neither an end nor an error within 60 s of Stop; Linux and Windows do")]
    public async Task Stop_cuts_off_a_download_in_progress()
    {
        var server = Start([File_("big.bin", Kit.Noise(60_000_000))]);
        using var response = await _http.GetAsync(server.FileUrl(0, loopback: true), HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(new byte[4096]);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        server.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Stop took {watch.Elapsed}");

        // A bounded wait: a socket left half-open by the server must fail this test, not hang the whole run.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var error = await Record.ExceptionAsync(async () =>
        {
            var buffer = new byte[1 << 20];
            while (await stream.ReadAsync(buffer, limit.Token) > 0) { }
            throw new InvalidOperationException("the stream ended normally although the share was stopped");
        });
        Assert.NotNull(error);
        Assert.False(limit.IsCancellationRequested, "the download neither ended nor failed within 60 s of Stop");
    }

    [Fact]
    public void Listening_on_the_network_needs_no_administrator_rights_so_the_bind_mode_is_lan()
    {
        // A documentation address (TEST-NET-1) stands in for "the address the phone would use"; the socket itself is bound to every interface.
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LanAddress = IPAddress.Parse("192.0.2.77") });

        Assert.True(server.IsRunning);
        Assert.Equal(ShareBindMode.Lan, server.Mode);
        Assert.Null(server.LanUnavailableReason);
        Assert.InRange(server.Port, 1024, 65535);
        Assert.Equal("192.0.2.77", server.ListingUrl.Host);                 // the phone-facing link uses the LAN address
        Assert.Equal("127.0.0.1", server.LoopbackListingUrl.Host);          // and this PC can still use loopback
        Assert.Equal(IPAddress.Parse("192.0.2.77"), server.LanIpAddress);
    }

    [Fact]
    public async Task Without_a_usable_network_address_the_share_falls_back_to_this_computer_and_says_why()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LanAddressFinder = () => null });

        Assert.Equal(ShareBindMode.LoopbackOnly, server.Mode);
        Assert.Equal("127.0.0.1", server.ListingUrl.Host);
        Assert.Null(server.LanIpAddress);
        Assert.Contains("no network address", server.LanUnavailableReason);
        Assert.Equal(["http://127.0.0.1:" + server.Port], server.BoundAddresses);
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0)));
    }
    [Fact]
    public void Loopback_only_option_never_tries_the_network()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true });

        Assert.Equal(ShareBindMode.LoopbackOnly, server.Mode);
        Assert.Equal("127.0.0.1", server.ListingUrl.Host);
        Assert.Contains("turned off", server.LanUnavailableReason);
    }

    [Fact]
    public void Separate_shares_get_separate_ports_and_a_fixed_port_cannot_be_shared_twice()
    {
        var file = File_("a.txt", Kit.Bytes("A"));
        var one = Start([file]);
        var two = Start([file]);
        Assert.NotEqual(one.Port, two.Port);
        Assert.NotEqual(one.Token, two.Token);

        var taken = one.Port;
        var clash = new PhoneShareServer([file], new PhoneShareOptions { LoopbackOnly = true, Port = taken });
        Assert.Throws<IOException>(() => clash.Start());
    }

    [Fact]
    public void Constructor_validates_what_it_is_given()
    {
        var file = File_("a.txt", Kit.Bytes("A"));

        Assert.Throws<ArgumentException>(() => new PhoneShareServer([]));
        Assert.Throws<ArgumentException>(() => new PhoneShareServer([_temp.Path]));                 // a folder
        Assert.Throws<FileNotFoundException>(() => new PhoneShareServer([_temp.Combine("ghost.txt")]));

        // the same file listed twice is shared once (and, where names ignore case, also under another spelling)
        var deduplicated = new PhoneShareServer(OperatingSystem.IsWindows() ? [file, file, file.ToUpperInvariant()] : [file, file]);
        Assert.Single(deduplicated.Files);
        Assert.Equal(0, deduplicated.Files[0].Index);
        Assert.Equal("a.txt", deduplicated.Files[0].Name);
        Assert.Equal(1, deduplicated.Files[0].Length);
    }

    [Fact]
    public async Task A_file_that_vanishes_after_sharing_answers_404_not_a_crash()
    {
        var path = File_("gone.txt", Kit.Bytes("soon gone"));
        var server = Start([path, File_("stays.txt", Kit.Bytes("stays"))]);
        File.Delete(path);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(server.FileUrl(0, loopback: true))).StatusCode);
        Assert.Equal("stays", await _http.GetStringAsync(server.FileUrl(1, loopback: true)));          // the server is still fine
    }

    [Fact]
    public async Task Sharing_never_modifies_or_locks_the_original()
    {
        var path = File_("keep.txt", Kit.Bytes("original"));
        var server = Start([path]);

        await _http.GetByteArrayAsync(server.FileUrl(0, loopback: true));

        File.AppendAllText(path, " + edit");                       // not locked afterwards
        Assert.Equal("original + edit", File.ReadAllText(path));
    }

    [Fact]
    public async Task Download_events_carry_the_file()
    {
        var server = Start([File_("evt.txt", Kit.Bytes("e"))]);
        var started = new List<string>();
        var completed = new List<string>();
        server.DownloadStarted += (_, e) => started.Add(e.File.Name);
        server.DownloadCompleted += (_, e) => { completed.Add(e.File.Name); Assert.True(e.Completed); };

        await _http.GetByteArrayAsync(server.FileUrl(0, loopback: true));
        await Kit.Eventually(() => completed.Count, n => n == 1);

        Assert.Equal(["evt.txt"], started);
        Assert.Equal(["evt.txt"], completed);
    }

    // ---------------------------------------------------------------- the QR code for the link

    [Fact]
    public async Task The_qr_code_decodes_to_the_exact_link()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))]);

        var png = PhoneShareLink.CreateQrPng(server);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        var found = QrCodeService.Read(png);
        Assert.Equal(server.ListingUrl.AbsoluteUri, Assert.Single(found).Text);
        Assert.Equal("QR_CODE", found[0].Format);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("http://192.168.0.10:49321/AbCdEfGhIjKlMnOpQrStUv/")]
    [InlineData("https://example.com/path?x=1&y=2#frag")]
    public void Any_http_link_becomes_a_scannable_qr(string url)
    {
        var png = PhoneShareLink.CreateQrPng(url, size: 400);
        Assert.Equal(url, Assert.Single(QrCodeService.Read(png)).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://host/file")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    public void Only_http_links_can_be_turned_into_a_qr(string url) =>
        Assert.Throws<ArgumentException>(() => PhoneShareLink.CreateQrPng(url));

    // ---------------------------------------------------------------- choosing the LAN address

    private static NetworkAdapter Adapter(string name, NetworkInterfaceType type, string ip, bool gateway, bool up = true, string? description = null) =>
        new(name, description ?? name, type, up, [IPAddress.Parse(ip)], gateway);

    [Fact]
    public void The_lan_address_is_the_real_adapter_with_a_gateway_not_a_virtual_switch()
    {
        var adapters = new[]
        {
            Adapter("vEthernet (Default Switch)", NetworkInterfaceType.Ethernet, "172.18.16.1", gateway: false, description: "Hyper-V Virtual Ethernet Adapter"),
            Adapter("vEthernet (WSL)", NetworkInterfaceType.Ethernet, "172.26.64.1", gateway: false, description: "Hyper-V Virtual Ethernet Adapter #2"),
            Adapter("Ethernet", NetworkInterfaceType.Ethernet, "192.168.0.253", gateway: true, description: "Intel(R) Ethernet Connection (7) I219-V"),
            Adapter("Loopback Pseudo-Interface 1", NetworkInterfaceType.Loopback, "127.0.0.1", gateway: false),
        };

        Assert.Equal(IPAddress.Parse("192.168.0.253"), LanAddress.Pick(adapters));
    }

    [Fact]
    public void The_lan_address_skips_adapters_that_are_down_have_no_dhcp_or_are_tunnels()
    {
        var adapters = new[]
        {
            Adapter("Wi-Fi", NetworkInterfaceType.Wireless80211, "10.0.0.5", gateway: true, up: false),
            Adapter("Ethernet 2", NetworkInterfaceType.Ethernet, "169.254.12.7", gateway: false),
            Adapter("VPN", NetworkInterfaceType.Tunnel, "10.8.0.2", gateway: true),
            Adapter("VirtualBox Host-Only", NetworkInterfaceType.Ethernet, "192.168.56.1", gateway: true, description: "VirtualBox Host-Only Ethernet Adapter"),
        };

        Assert.Null(LanAddress.Pick(adapters));
    }

    [Fact]
    public void The_lan_address_prefers_a_gateway_but_falls_back_to_a_private_address()
    {
        var wifi = Adapter("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.20", gateway: false);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), LanAddress.Pick([wifi]));

        var pub = Adapter("Ethernet", NetworkInterfaceType.Ethernet, "93.184.216.34", gateway: false);   // public address without a gateway: not a LAN
        Assert.Null(LanAddress.Pick([pub]));

        var routed = Adapter("Ethernet", NetworkInterfaceType.Ethernet, "10.1.1.9", gateway: true);
        Assert.Equal(IPAddress.Parse("10.1.1.9"), LanAddress.Pick([wifi, routed]));                       // the one with a gateway wins
    }

    [Fact]
    public void Finding_the_lan_address_on_this_machine_never_throws()
    {
        var found = LanAddress.Find();
        if (found is not null)
        {
            Assert.Equal(AddressFamily.InterNetwork, found.AddressFamily);
            Assert.False(IPAddress.IsLoopback(found));
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Sends a request line exactly as given (HttpClient would normalise ".." away) and returns status, headers and body.</summary>
    private static async Task<(int Status, string Headers, byte[] Body)> RawGet(int port, string target)
    {
        // Bounded: a server that never closes the connection fails the test instead of hanging the run.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"), limit.Token);

        using var all = new MemoryStream();
        await stream.CopyToAsync(all, limit.Token);
        var bytes = all.ToArray();
        var split = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        var head = split < 0 ? Encoding.ASCII.GetString(bytes) : Encoding.ASCII.GetString(bytes, 0, split);
        var body = split < 0 ? [] : bytes[(split + 4)..];
        var status = int.Parse(head.Split(' ')[1]);
        return (status, head, body);
    }
}
