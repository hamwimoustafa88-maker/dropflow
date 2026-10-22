using System.Net;
using System.Net.Sockets;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Rendering;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Skipped, visibly, when no Chromium-family browser (Chrome, Chromium, Edge, Brave) is installed.</summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
    public ChromiumFactAttribute()
    {
        if (new ChromiumLocator().Locate() is null) Skip = "No Chrome, Chromium or Edge is installed, so the headless HTML-to-PDF path cannot run here.";
    }
}

public class ChromiumLocatorTests
{
    [Fact]
    public void Finds_chrome_inside_the_macOS_applications_folder()
    {
        var chrome = Path.Combine("/Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome");
        var locator = new ChromiumLocator(p => p == chrome, pathDirs: [], windows: false, macOs: true);
        Assert.Equal(chrome, locator.Locate());
    }

    [Fact]
    public void Finds_chromium_or_edge_on_the_linux_PATH()
    {
        var chromium = Path.Combine("/usr/lib/bin-test", "chromium");
        var edge = Path.Combine("/usr/lib/bin-test", "microsoft-edge");
        Assert.Equal(chromium, new ChromiumLocator(p => p == chromium || p == edge, pathDirs: ["/usr/lib/bin-test"], windows: false, macOs: false).Locate());
        Assert.Equal(edge, new ChromiumLocator(p => p == edge, pathDirs: ["/usr/lib/bin-test"], windows: false, macOs: false).Locate());
    }

    [Fact]
    public void Windows_looks_for_chrome_and_edge_under_program_files()
    {
        var candidates = new ChromiumLocator(_ => false, pathDirs: [], windows: true, macOs: false).Candidates().ToList();
        Assert.Contains(candidates, c => c.EndsWith(Path.Combine("Google", "Chrome", "Application", "chrome.exe"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, c => c.EndsWith(Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reports_null_when_no_browser_is_installed() =>
        Assert.Null(new ChromiumLocator(_ => false, pathDirs: ["/nowhere"], windows: false, macOs: true).Locate());
}

public class ChromiumHtmlPdfRendererTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    [Fact]
    public void Offline_printing_blocks_name_lookups_and_every_connection()
    {
        var args = ChromiumHtmlPdfRenderer.BuildArguments("file:///tmp/page.html", "/tmp/out.pdf", "/tmp/profile", allowOnline: false, noSandbox: false);

        Assert.Contains("--headless", args);
        Assert.Contains("--host-resolver-rules=MAP * ~NOTFOUND", args);
        Assert.Contains("--proxy-server=http://127.0.0.1:9", args);
        Assert.Contains("--proxy-bypass-list=<-loopback>", args);   // loopback goes through the dead proxy too
        Assert.Contains("--print-to-pdf=/tmp/out.pdf", args);
        Assert.Contains("--user-data-dir=/tmp/profile", args);
        Assert.DoesNotContain("--no-sandbox", args);
        Assert.Equal("file:///tmp/page.html", args[^1]);
    }

    [Fact]
    public void Online_printing_and_running_as_root_change_only_their_flags()
    {
        var args = ChromiumHtmlPdfRenderer.BuildArguments("file:///p.html", "/o.pdf", "/prof", allowOnline: true, noSandbox: true);
        Assert.DoesNotContain(args, a => a.StartsWith("--proxy-server", StringComparison.Ordinal) || a.StartsWith("--host-resolver-rules", StringComparison.Ordinal));
        Assert.Equal("--no-sandbox", args[0]);
    }

    [Fact]
    public void The_print_style_sets_paper_orientation_backgrounds_and_a_base_at_the_original_folder()
    {
        var original = Path.Combine(Path.GetTempPath(), "site", "index.html");
        var page = ChromiumHtmlPdfRenderer.PreparePage("<html><head><title>t</title></head><body>x</body></html>", original,
            new HtmlPdfOptions(Paper: "letter", Landscape: true, Backgrounds: false));

        Assert.Contains("@page { size: Letter landscape; }", page);
        Assert.Contains("print-color-adjust: economy", page);
        Assert.Contains($"<base href=\"{new Uri(Path.GetDirectoryName(original) + Path.DirectorySeparatorChar).AbsoluteUri}\">", page);
        Assert.True(page.IndexOf("<base", StringComparison.Ordinal) < page.IndexOf("<title>", StringComparison.Ordinal));   // inside <head>, first
    }

    [Fact]
    public void A_page_with_its_own_base_or_without_a_head_is_handled()
    {
        var own = ChromiumHtmlPdfRenderer.PreparePage("<html><head><base href=\"https://x/\"></head><body/></html>", "/a/b.html", new HtmlPdfOptions());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(own, "<base "));
        Assert.Contains("size: A4;", own);

        var bare = ChromiumHtmlPdfRenderer.PreparePage("<p>hello</p>", "/a/b.html", new HtmlPdfOptions(Backgrounds: true));
        Assert.StartsWith("<!doctype html>", bare);
        Assert.Contains("print-color-adjust: exact", bare);
        Assert.Contains("<p>hello</p>", bare);
    }

    [Fact]
    public async Task Without_a_browser_the_result_says_what_to_install_and_nothing_runs()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("a.html", "<p>x</p>");
        var ran = false;
        var renderer = new ChromiumHtmlPdfRenderer(() => null, (_, _, _, _) => { ran = true; return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero, false, false)); });

        var error = await renderer.PrintAsync(html, dir.Combine("a.pdf"), new HtmlPdfOptions(), NoProgress, default);

        Assert.Equal(ChromiumHtmlPdfRenderer.MissingBrowserMessage, error);
        Assert.False(ran);
        Assert.False(File.Exists(dir.Combine("a.pdf")));
    }

    [Fact]
    public async Task The_pdf_the_browser_writes_becomes_the_output_and_the_work_folder_is_removed()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("a.html", "<p>x</p>");
        string? workFolder = null;
        var renderer = new ChromiumHtmlPdfRenderer(() => "/fake/chrome", (exe, args, options, _) =>
        {
            Assert.Equal("/fake/chrome", exe);
            workFolder = options.WorkingDirectory;
            File.WriteAllText(args.Single(a => a.StartsWith("--print-to-pdf=", StringComparison.Ordinal))["--print-to-pdf=".Length..], "%PDF-1.7 fake");
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero, false, false));
        });

        var output = dir.Combine("a.pdf");
        Assert.Null(await renderer.PrintAsync(html, output, new HtmlPdfOptions(), NoProgress, default));

        Assert.StartsWith("%PDF", File.ReadAllText(output));
        Assert.NotNull(workFolder);
        Assert.False(Directory.Exists(workFolder));
    }

    [Fact]
    public async Task A_browser_that_writes_no_pdf_is_reported()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("a.html", "<p>x</p>");
        var renderer = new ChromiumHtmlPdfRenderer(() => "/fake/chrome", (_, _, _, _) => Task.FromResult(new ProcessResult(1, "", "crashed badly", TimeSpan.Zero, false, false)));

        var error = await renderer.PrintAsync(html, dir.Combine("a.pdf"), new HtmlPdfOptions(), NoProgress, default);

        Assert.Contains("crashed badly", error);
        Assert.False(File.Exists(dir.Combine("a.pdf")));
    }

    [Fact]
    public async Task The_tool_names_its_output_beside_the_source_and_passes_the_options()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("page.html", "<p>x</p>");
        HtmlPdfOptions? seen = null;
        var renderer = new FakeRenderer(o => seen = o);

        var result = await new HtmlToPdfConverter(renderer).RunAsync(
            new ConversionJob([html], HtmlToPdfConverter.ToolId, new Dictionary<string, object?> { ["paper"] = "letter", ["landscape"] = true }), NoProgress, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(dir.Combine("page (converted).pdf"), result.Outputs.Single());
        Assert.Equal(new HtmlPdfOptions("letter", true, true, false), seen);
        Assert.Single(PdfModule.CreateHtmlTools(renderer));
    }

    private sealed class FakeRenderer(Action<HtmlPdfOptions> seen) : IHtmlPdfRenderer
    {
        public Task<string?> PrintAsync(string htmlPath, string outputPath, HtmlPdfOptions options, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            seen(options);
            File.WriteAllText(outputPath, "%PDF-1.7");
            return Task.FromResult<string?>(null);
        }
    }

    // ---- a real browser ----------------------------------------------------------------------------------------------

    [ChromiumFact]
    public async Task A_real_browser_prints_an_a4_page_with_the_text()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("نموذج.html", "<html><head><meta charset=\"utf-8\"></head><body><h1>Invoice 4711</h1><p>مرحبا</p></body></html>");
        var output = dir.Combine("out.pdf");

        Assert.Null(await new ChromiumHtmlPdfRenderer().PrintAsync(html, output, new HtmlPdfOptions(), NoProgress, default));

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        var page = pdf.GetPage(1);
        Assert.InRange(page.Width, 590, 600);    // A4 = 595 x 842 pt
        Assert.InRange(page.Height, 837, 847);
        Assert.Contains("Invoice 4711", page.Text);
    }

    [ChromiumFact]
    public async Task A_real_browser_offline_never_connects_and_online_does()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var dir = new TempDir();
        var html = dir.WriteText("ping.html", $"<html><body><img src=\"http://127.0.0.1:{port}/ping.png\"><p>ping</p></body></html>");

        var offline = new ChromiumHtmlPdfRenderer();
        Assert.Null(await offline.PrintAsync(html, dir.Combine("offline.pdf"), new HtmlPdfOptions(AllowOnline: false), NoProgress, default));
        Assert.False(listener.Pending(), "the page reached the network although online resources were not allowed");

        var accept = listener.AcceptTcpClientAsync();
        var print = new ChromiumHtmlPdfRenderer().PrintAsync(html, dir.Combine("online.pdf"), new HtmlPdfOptions(AllowOnline: true), NoProgress, default);
        var first = await Task.WhenAny(accept, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(accept, first);            // allowed: the image request arrives
        (await accept).Dispose();                        // hang up so the page finishes
        await print;
    }
}
