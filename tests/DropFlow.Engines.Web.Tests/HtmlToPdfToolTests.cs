using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Microsoft.Web.WebView2.Core;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Web.Tests;

/// <summary>Skipped (visibly) when the Edge WebView2 runtime is missing.</summary>
public sealed class WebViewFactAttribute : FactAttribute
{
    public WebViewFactAttribute()
    {
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Skip = "The Microsoft Edge WebView2 runtime is not installed.";
        }
    }
}

/// <summary>A real WPF dispatcher on its own STA thread, like the app's UI thread.</summary>
public sealed class UiThread : IDisposable
{
    private readonly Thread _thread;

    public Dispatcher Dispatcher { get; }

    public UiThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        Dispatcher = dispatcher!;
    }

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>One UI thread and one profile for the whole class, as in the app (a fresh thread per test makes the browser process wait for the previous one to exit).</summary>
public sealed class WebHostFixture : IDisposable
{
    public UiThread Ui { get; } = new();

    public string Profile { get; } = Path.Combine(Path.GetTempPath(), "dropflow-webview-tests");

    public void Dispose() => Ui.Dispose();
}

public class HtmlToPdfToolTests(WebHostFixture host) : IClassFixture<WebHostFixture>, IDisposable
{
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><style>body{font-family:Arial;background:#eef}</style></head>
        <body><h1>Hello DropFlow</h1><p>مرحبا بالعالم</p></body></html>
        """;

    // The browser keeps its profile files open after a render, so the profile lives outside the per-test TempDir.
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private HtmlToPdfTool Tool() => new(host.Ui.Dispatcher, host.Profile);

    private static ConversionJob Job(string input, Dictionary<string, object?>? options = null) =>
        new([input], HtmlToPdfTool.ToolId, options);

    private static (double Width, double Height) FirstPageSize(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        return (page.Width, page.Height);
    }

    [Fact]
    public void Supports_only_html_inputs_for_its_own_operations()
    {
        var tool = Tool();
        Assert.True(tool.Supports(HtmlToPdfTool.ToolId, "html"));
        Assert.True(tool.Supports(HtmlToPdfTool.ToolId, "htm"));
        Assert.True(tool.Supports(HtmlToPdfTool.ConvertPdf, "html"));
        Assert.False(tool.Supports(HtmlToPdfTool.ToolId, "pdf"));
        Assert.False(tool.Supports(HtmlToPdfTool.ConvertPdf, "docx"));
        Assert.False(tool.Supports("convert:png", "html"));
    }

    [Fact]
    public void Only_the_tool_asks_for_options_not_the_plain_conversion()
    {
        var tool = Tool();
        Assert.NotEmpty(tool.OptionsFor(HtmlToPdfTool.ToolId));
        Assert.Empty(tool.OptionsFor(HtmlToPdfTool.ConvertPdf));
    }

    [WebViewFact]
    public async Task Renders_a_page_with_text_to_an_A4_pdf_next_to_the_input_without_touching_it()
    {
        var input = _dir.WriteText("page.html", Page);

        var result = await Tool().RunAsync(Job(input), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(_dir.Combine("page (converted).pdf"), output);
        Assert.Equal(Page, File.ReadAllText(input));

        using var doc = PdfDocument.Open(output);
        Assert.Contains("Hello DropFlow", doc.GetPage(1).Text);
        var (w, h) = FirstPageSize(output);
        Assert.InRange(w, 594, 597);
        Assert.InRange(h, 841, 843);
    }

    [WebViewFact]
    public async Task Handles_arabic_names_and_spaces_in_the_path()
    {
        var input = _dir.WriteText("تقرير اختبار.html", Page);

        var result = await Tool().RunAsync(Job(input), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Assert.Single(result.Outputs)));
    }

    [WebViewFact]
    public async Task Landscape_and_letter_change_the_page_size()
    {
        var input = _dir.WriteText("page.html", Page);

        var landscape = await Tool().RunAsync(Job(input, new() { ["landscape"] = true }), new Progress<JobProgress>(), CancellationToken.None);
        var letter = await Tool().RunAsync(Job(input, new() { ["paper"] = "letter" }), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(landscape.Success, landscape.Error);
        Assert.True(letter.Success, letter.Error);
        var (lw, lh) = FirstPageSize(landscape.Outputs[0]);
        Assert.True(lw > lh, $"landscape page was {lw}x{lh}");
        var (w, h) = FirstPageSize(letter.Outputs[0]);
        Assert.InRange(w, 611, 613);
        Assert.InRange(h, 791, 793);
    }

    [WebViewFact]
    public async Task Never_overwrites_an_earlier_output()
    {
        var input = _dir.WriteText("page.html", Page);
        var tool = Tool();

        var first = await tool.RunAsync(Job(input), new Progress<JobProgress>(), CancellationToken.None);
        var second = await tool.RunAsync(Job(input), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(first.Success && second.Success);
        Assert.NotEqual(first.Outputs[0], second.Outputs[0]);
        Assert.True(File.Exists(first.Outputs[0]));
    }

    [WebViewFact]
    public async Task Blocks_remote_resources_unless_online_is_allowed()
    {
        using var server = new TinyServer();
        var html = $"<!doctype html><html><body><p>x</p><img src=\"http://127.0.0.1:{server.Port}/pixel.png\"></body></html>";
        var input = _dir.WriteText("remote.html", html);

        var offline = await Tool().RunAsync(Job(input), new Progress<JobProgress>(), CancellationToken.None);
        Assert.True(offline.Success, offline.Error);
        Assert.Equal(0, server.Requests);

        var online = await Tool().RunAsync(Job(input, new() { ["online"] = true }), new Progress<JobProgress>(), CancellationToken.None);
        Assert.True(online.Success, online.Error);
        Assert.True(server.Requests > 0, "with online=true the page should be allowed to reach the network");
    }

    [Fact]
    public async Task A_missing_input_fails_with_a_message()
    {
        var result = await Tool().RunAsync(Job(_dir.Combine("nope.html")), new Progress<JobProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cancelled_token_cancels_before_any_work()
    {
        var input = _dir.WriteText("page.html", Page);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Tool().RunAsync(Job(input), new Progress<JobProgress>(), cts.Token));
        Assert.False(File.Exists(_dir.Combine("page (converted).pdf")));
    }

    /// <summary>Counts HTTP connections on a loopback port; answers 404 so the page finishes loading.</summary>
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _requests;

        public TinyServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoop()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref _requests);
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer);
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException or InvalidOperationException)
            {
                // listener stopped
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
