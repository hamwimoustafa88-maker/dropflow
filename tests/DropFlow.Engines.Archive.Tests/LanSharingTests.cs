using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using DropFlow.Engines.Archive.Sharing;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

/// <summary>
/// Skipped (visibly) unless this machine has a LAN address and the test runs WITHOUT administrator rights: only then does the
/// result prove that sharing on the network works for an ordinary user.
/// </summary>
public sealed class LanFactAttribute : FactAttribute
{
    public static readonly IPAddress? Address = LanAddress.Find();

    public static bool IsElevated() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public LanFactAttribute()
    {
        if (Address is null) Skip = "This machine has no LAN address a phone could reach, so LAN mode cannot be demonstrated here.";
        else if (IsElevated()) Skip = "The tests are running elevated; run them as a normal user to prove that no administrator rights are needed.";
    }
}

/// <summary>The Kestrel-hosted share: it must work on the network for a standard user, and it must listen on exactly one port.</summary>
public class LanSharingTests : IDisposable
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

    private string File_(string name, byte[] content)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private PhoneShareServer Start(string[] files, PhoneShareOptions? options = null)
    {
        var server = new PhoneShareServer(files, options);
        _servers.Add(server);
        server.Start();
        return server;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    // ---------------------------------------------------------------- the claim: LAN works for a standard user

    [LanFact]
    public async Task By_default_the_share_is_in_lan_mode_and_a_file_downloads_through_the_lan_address_without_elevation()
    {
        Assert.False(LanFactAttribute.IsElevated());                     // belt and braces: the attribute already skips when elevated
        var data = Kit.Noise(1_500_000);
        var server = Start([File_($"{Kit.ArabicFile}.pdf", data)]);      // default options: nothing special asked for

        Assert.Equal(ShareBindMode.Lan, server.Mode);
        Assert.Null(server.LanUnavailableReason);
        Assert.Equal(LanFactAttribute.Address, server.LanIpAddress);
        Assert.Equal(LanFactAttribute.Address!.ToString(), server.ListingUrl.Host);
        Assert.NotEqual("127.0.0.1", server.ListingUrl.Host);

        // the very URL a phone would be given: LAN IP, not loopback
        var lanFile = server.FileUrl(0);
        Assert.Equal(LanFactAttribute.Address.ToString(), lanFile.Host);
        var response = await _http.GetAsync(lanFile);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"{Kit.ArabicFile}.pdf", response.Content.Headers.ContentDisposition!.FileNameStar);

        var listing = await _http.GetStringAsync(server.ListingUrl);
        Assert.Contains(Kit.ArabicFile, listing);

        // and the same share still answers on this computer's loopback address
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(server.FileUrl(0, loopback: true))).StatusCode);
    }

    [LanFact]
    public async Task A_wrong_token_on_the_lan_address_is_still_a_plain_404()
    {
        var server = Start([File_("secret.txt", Kit.Bytes("secret"))]);
        Assert.Equal(ShareBindMode.Lan, server.Mode);
        var host = $"http://{server.LanIpAddress}:{server.Port}";

        var attempts = new[] { "/", "/wrong-token/", $"/{server.Token}x/", $"/{server.Token[..^1]}/", "/wrong-token/f/0/secret.txt", "/f/0/secret.txt", "/secret.txt" };
        var bodies = new HashSet<string>();
        foreach (var path in attempts)
        {
            var response = await _http.GetAsync(host + path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies);                                          // indistinguishable: nothing to probe
        Assert.DoesNotContain("secret", bodies.Single());
        Assert.Equal("secret", await _http.GetStringAsync($"{host}/{server.Token}/f/0/secret.txt"));   // and the right token works on the same address
    }

    [LanFact]
    public async Task Path_traversal_attempts_fail_on_the_lan_address_too()
    {
        File_("secret.txt", Kit.Bytes("TOP SECRET"));
        var server = Start([File_("shared.txt", Kit.Bytes("shared"))]);

        foreach (var target in new[] { "/{t}/../secret.txt", "/{t}/f/0/../../secret.txt", "/{t}/f/..%2f..%2fsecret.txt", "/{t}/%2e%2e/secret.txt", "/{t}/f/0/..\\..\\secret.txt" })
        {
            var (status, body) = await RawGet(server.LanIpAddress!, server.Port, target.Replace("{t}", server.Token));
            Assert.DoesNotContain("TOP SECRET", body);
            Assert.True(status is 200 or 301 or 400 or 404, $"{target}: unexpected status {status}");
        }
    }

    // ---------------------------------------------------------------- exactly one endpoint

    [Fact]
    public void The_server_listens_on_one_endpoint_only_and_it_is_plain_http_on_ipv4()
    {
        foreach (var options in new[] { new PhoneShareOptions { LanAddress = IPAddress.Parse("192.0.2.77") }, new PhoneShareOptions { LoopbackOnly = true } })
        {
            var server = Start([File_("a.txt", Kit.Bytes("A"))], options);
            var expected = options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any;

            Assert.Equal([$"http://{expected}:{server.Port}"], server.BoundAddresses);     // one address, http, no https

            // what the operating system says: exactly one listening socket on this port, on the expected interface, none on IPv6
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(l => l.Port == server.Port).ToList();
            var only = Assert.Single(listeners);
            Assert.Equal(expected, only.Address);
            Assert.Equal(AddressFamily.InterNetwork, only.AddressFamily);
        }
    }

    [Fact]
    public async Task Environment_settings_cannot_add_endpoints_or_switch_on_other_protocols()
    {
        var extraA = FreePort();
        var extraB = FreePort();
        var saved = new[] { "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_URLS" }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://0.0.0.0:{extraA}");
        Environment.SetEnvironmentVariable("DOTNET_URLS", $"http://0.0.0.0:{extraA}");
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", extraB.ToString());
        try
        {
            var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true });

            Assert.Equal([$"http://127.0.0.1:{server.Port}"], server.BoundAddresses);
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            Assert.DoesNotContain(listeners, l => l.Port == extraA || l.Port == extraB);

            // cleartext HTTP/2 ("prior knowledge") is not offered: this server speaks HTTP/1.1 only
            var h2 = await RawRequest(IPAddress.Loopback, server.Port, "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
            Assert.NotEqual(200, h2.Status);
            Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task A_second_start_on_a_fixed_port_that_is_in_use_fails_cleanly_and_leaves_the_first_share_working()
    {
        var file = File_("a.txt", Kit.Bytes("A"));
        foreach (var loopbackOnly in new[] { false, true })
        {
            var port = FreePort();
            var options = new PhoneShareOptions { Port = port, LoopbackOnly = loopbackOnly, LanAddress = IPAddress.Parse("192.0.2.77") };
            var first = Start([file], options);
            Assert.Equal(port, first.Port);

            var second = new PhoneShareServer([file], options);
            _servers.Add(second);
            var failure = Assert.Throws<IOException>(() => second.Start());

            Assert.Contains(port.ToString(), failure.Message);
            Assert.False(second.IsRunning);
            Assert.Equal(ShareBindMode.NotStarted, second.Mode);
            Assert.Equal("A", await _http.GetStringAsync(first.FileUrl(0, loopback: true)));       // the first share is unharmed
            Assert.Single(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), l => l.Port == port);

            first.Stop();
            second.Dispose();
        }
    }

    [Fact]
    public async Task A_fixed_port_clash_does_not_quietly_fall_back_to_another_interface()
    {
        var file = File_("a.txt", Kit.Bytes("A"));
        var port = FreePort();
        using var occupier = new TcpListener(IPAddress.Any, port);
        occupier.Start();

        var server = new PhoneShareServer([file], new PhoneShareOptions { Port = port, LanAddress = IPAddress.Parse("192.0.2.77") });
        _servers.Add(server);

        Assert.Throws<IOException>(() => server.Start());
        Assert.False(server.IsRunning);
        Assert.Equal(ShareBindMode.NotStarted, server.Mode);
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- stopping frees the port

    [Fact]
    public async Task Stop_frees_the_port_at_once_so_another_share_can_take_it()
    {
        var file = File_("a.txt", Kit.Bytes("A"));
        foreach (var loopbackOnly in new[] { false, true })
        {
            var first = Start([file], new PhoneShareOptions { LoopbackOnly = loopbackOnly });
            var port = first.Port;
            Assert.Equal("A", await _http.GetStringAsync(first.FileUrl(0, loopback: true)));     // a real connection existed on the port

            first.Stop();

            Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), l => l.Port == port);
            var next = Start([File_("b.txt", Kit.Bytes("B"))], new PhoneShareOptions { Port = port, LoopbackOnly = loopbackOnly });
            Assert.Equal(port, next.Port);
            Assert.Equal("B", await _http.GetStringAsync(next.FileUrl(0, loopback: true)));
            next.Stop();
        }
    }

    [Fact]
    public async Task A_share_that_finished_by_itself_also_releases_its_port()
    {
        var port = FreePort();
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, Port = port, SingleDownload = true });
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));

        await Kit.Eventually(() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l => l.Port == port), still => !still, 8000);

        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), l => l.Port == port);
    }

    // ---------------------------------------------------------------- hardening: limits, timeouts, headers

    [Fact]
    public async Task Responses_do_not_announce_the_server_software()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true });

        foreach (var url in new[] { server.LoopbackListingUrl, server.FileUrl(0, loopback: true), new Uri($"http://127.0.0.1:{server.Port}/nope") })
        {
            var response = await _http.GetAsync(url);
            Assert.False(response.Headers.Contains("Server"), url.ToString());
            Assert.False(response.Headers.Contains("X-Powered-By"));
        }
    }

    [Fact]
    public async Task Oversized_request_lines_and_headers_are_refused_before_the_application_sees_them()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true });

        var longLine = await RawRequest(IPAddress.Loopback, server.Port, $"GET /{server.Token}/{new string('a', 10_000)} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        Assert.True(longLine.Status is 400 or 414, $"request line: {longLine.Status}");

        var bigHeader = await RawRequest(IPAddress.Loopback, server.Port, $"GET /{server.Token}/ HTTP/1.1\r\nHost: x\r\nX-Big: {new string('b', 20_000)}\r\nConnection: close\r\n\r\n");
        Assert.True(bigHeader.Status is 400 or 431, $"header: {bigHeader.Status}");

        var manyHeaders = new StringBuilder($"GET /{server.Token}/ HTTP/1.1\r\nHost: x\r\n");
        for (var i = 0; i < 100; i++) manyHeaders.Append($"X-{i}: v\r\n");
        manyHeaders.Append("Connection: close\r\n\r\n");
        var many = await RawRequest(IPAddress.Loopback, server.Port, manyHeaders.ToString());
        Assert.True(many.Status is 400 or 431, $"header count: {many.Status}");

        // and the server is fine afterwards
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));
    }

    [Fact]
    public async Task A_request_body_is_never_accepted()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true });

        var big = await RawRequest(IPAddress.Loopback, server.Port,
            $"POST /{server.Token}/ HTTP/1.1\r\nHost: x\r\nContent-Length: 50000000\r\nConnection: close\r\n\r\n");

        Assert.True(big.Status is 405 or 413, $"status {big.Status}");
    }

    [Fact]
    public async Task A_client_that_connects_and_sends_nothing_is_dropped_after_the_header_timeout()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, HeaderTimeout = TimeSpan.FromSeconds(1) });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync("GET /"u8.ToArray());                    // a request line that never ends

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var all = new MemoryStream();
        await stream.CopyToAsync(all, cts.Token);                         // returns when the server closes the connection

        var text = Encoding.ASCII.GetString(all.ToArray());
        Assert.True(text.Length == 0 || text.StartsWith("HTTP/1.1 408", StringComparison.Ordinal), text);
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));    // and the share still works
    }

    [Fact]
    public async Task The_number_of_simultaneous_connections_is_limited()
    {
        var server = Start([File_("a.txt", Kit.Bytes("A"))], new PhoneShareOptions { LoopbackOnly = true, MaxConnections = 2, HeaderTimeout = TimeSpan.FromSeconds(30) });

        using var one = new TcpClient();
        using var two = new TcpClient();
        await one.ConnectAsync(IPAddress.Loopback, server.Port);
        await two.ConnectAsync(IPAddress.Loopback, server.Port);
        await one.GetStream().WriteAsync("GET /x"u8.ToArray());          // both hold a slot with an unfinished request
        await two.GetStream().WriteAsync("GET /x"u8.ToArray());
        await Task.Delay(300);

        // a third connection is not served while the two slots are taken
        using var three = new TcpClient();
        await three.ConnectAsync(IPAddress.Loopback, server.Port);
        var request = Encoding.ASCII.GetBytes($"GET /{server.Token}/ HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await three.GetStream().WriteAsync(request);
        var buffer = new byte[256];
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
        {
            var served = false;
            try { served = await three.GetStream().ReadAsync(buffer, wait.Token) > 0 && Encoding.ASCII.GetString(buffer).StartsWith("HTTP/1.1 200", StringComparison.Ordinal); }
            catch (OperationCanceledException) { /* nothing arrived: exactly what the limit is for */ }
            catch (IOException) { /* refused or reset: also fine */ }
            Assert.False(served, "the third connection was served although two slots were taken");
        }

        // freeing a slot lets the next client in
        one.Dispose();
        await Task.Delay(300);
        using var fresh = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal("A", await fresh.GetStringAsync(server.FileUrl(0, loopback: true)));
    }

    [Fact]
    public async Task A_download_that_stalls_is_cut_off_and_its_single_download_claim_is_released()
    {
        var data = Kit.Noise(120_000_000, 5);
        var server = Start([File_("big.bin", data)], new PhoneShareOptions
        {
            LoopbackOnly = true,
            SingleDownload = true,
            StopWhenFinished = false,
            MinSendBytesPerSecond = 500_000,
            SlowClientGrace = TimeSpan.FromSeconds(2),      // Kestrel requires a grace period longer than one second
        });

        // a phone that starts the download and then freezes
        using var stalled = await _http.GetAsync(server.FileUrl(0, loopback: true), HttpCompletionOption.ResponseHeadersRead);
        await using var stalledStream = await stalled.Content.ReadAsStreamAsync();
        await stalledStream.ReadExactlyAsync(new byte[4096]);
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(server.FileUrl(0, loopback: true))).StatusCode);   // claimed while it is in progress

        // the server gives up on it...
        var status = HttpStatusCode.Gone;
        for (var attempt = 0; attempt < 200 && status != HttpStatusCode.OK; attempt++)
        {
            await Task.Delay(100);
            using var retry = await _http.GetAsync(server.FileUrl(0, loopback: true), HttpCompletionOption.ResponseHeadersRead);
            status = retry.StatusCode;
            if (status == HttpStatusCode.OK) await using (var stream = await retry.Content.ReadAsStreamAsync()) await stream.ReadExactlyAsync(new byte[4096]);
        }

        // ...and the claim came back, so another client could start the same file
        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---------------------------------------------------------------- path parsing and misc

    [Theory]
    [InlineData("/TOKEN/", true, new[] { "TOKEN" })]
    [InlineData("/TOKEN", false, new[] { "TOKEN" })]
    [InlineData("/TOKEN/f/0/name.txt", false, new[] { "TOKEN", "f", "0", "name.txt" })]
    [InlineData("/TOKEN/f/0/%D9%85%D9%84%D9%81.txt?x=1#y", false, new[] { "TOKEN", "f", "0", "ملف.txt" })]
    [InlineData("//TOKEN//f/0", false, new[] { "TOKEN", "f", "0" })]
    public void Request_targets_are_split_into_decoded_segments(string target, bool endsWithSlash, string[] expected)
    {
        var segments = PhoneShareServer.ParseSegments(target, out var slash);
        Assert.Equal(expected, segments);
        Assert.Equal(endsWithSlash, slash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("TOKEN/f/0")]
    [InlineData("http://example.com/TOKEN/")]
    [InlineData("/TOKEN/../x")]
    [InlineData("/TOKEN/./x")]
    [InlineData("/TOKEN/%2e%2e/x")]
    [InlineData("/TOKEN/%2E/x")]
    [InlineData("/TOKEN/a%2Fb")]
    [InlineData("/TOKEN/a%2fb")]
    [InlineData("/TOKEN/a%5Cb")]
    [InlineData("/TOKEN/a\\b")]
    [InlineData("/TOKEN/a%00b")]
    public void Anything_that_could_hide_a_traversal_is_rejected_outright(string target) =>
        Assert.Null(PhoneShareServer.ParseSegments(target, out _));

    [Fact]
    public async Task Start_can_be_called_again_and_the_async_start_works_too()
    {
        var file = File_("a.txt", Kit.Bytes("A"));
        var server = new PhoneShareServer([file], new PhoneShareOptions { LoopbackOnly = true });
        _servers.Add(server);

        await server.StartAsync();
        var port = server.Port;
        server.Start();                                  // already running: nothing changes

        Assert.Equal(port, server.Port);
        Assert.Single(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), l => l.Port == port);
        Assert.Equal("A", await _http.GetStringAsync(server.FileUrl(0, loopback: true)));
    }

    [Fact]
    public void Starting_from_a_thread_with_a_synchronisation_context_does_not_deadlock()
    {
        var file = File_("a.txt", Kit.Bytes("A"));
        var server = new PhoneShareServer([file], new PhoneShareOptions { LoopbackOnly = true });
        _servers.Add(server);

        // like a UI thread: a context that only runs work when its owner pumps it
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadContext());
            server.Start();
            server.Stop();
        });
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Start/Stop blocked a thread that has a synchronisation context");
        Assert.False(server.IsRunning);
    }

    private sealed class SingleThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* never pumped: anything that needs it would hang */ }
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Send is not supported");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(int Status, string Body)> RawGet(IPAddress address, int port, string target)
    {
        var (status, body) = await RawRequest(address, port, $"GET {target} HTTP/1.1\r\nHost: {address}:{port}\r\nConnection: close\r\n\r\n");
        return (status, body);
    }

    private static async Task<(int Status, string Body)> RawRequest(IPAddress address, int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var all = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await stream.CopyToAsync(all, cts.Token); }
        catch (IOException) { /* the server may reset the connection after refusing the request */ }
        var text = Encoding.UTF8.GetString(all.ToArray());
        var space = text.IndexOf(' ');
        var status = space > 0 && int.TryParse(text.AsSpan(space + 1, 3), out var s) ? s : 0;
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (status, split < 0 ? text : text[(split + 4)..]);
    }
}
