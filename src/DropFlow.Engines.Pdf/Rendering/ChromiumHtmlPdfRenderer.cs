using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.Engines.Pdf.Rendering;

/// <summary>
/// Prints local HTML files to PDF with an installed Chromium-family browser running headless (<c>--headless --print-to-pdf</c>): the
/// HTML-to-PDF engine on macOS and Linux (on Windows the app uses WebView2; a macOS host may plug in WKWebView instead through
/// <see cref="IHtmlPdfRenderer"/>). Each job gets a throw-away browser profile. Unless the job allows online resources, every network
/// request is sent to an unreachable proxy and host names do not resolve, so a page cannot phone home; local files still load.
/// Paper size, orientation and backgrounds are applied with an injected print style sheet on a temporary copy of the page, whose
/// <c>&lt;base&gt;</c> points at the original folder so relative images and styles still load. When no browser is installed the
/// result says which one to install; nothing is downloaded.
/// </summary>
public sealed class ChromiumHtmlPdfRenderer : IHtmlPdfRenderer
{
    public const string MissingBrowserMessage =
        "HTML to PDF needs Google Chrome, Chromium or Microsoft Edge, and none was found. Install one (macOS: brew install --cask google-chrome; " +
        "Debian/Ubuntu: sudo apt install chromium), then try again.";

    public delegate Task<ProcessResult> Runner(string executable, IReadOnlyList<string> arguments, ProcessOptions options, CancellationToken cancellationToken);

    private readonly Func<string?> _browser;
    private readonly Runner _run;

    /// <param name="browserPath">A browser executable; null finds one with <see cref="ChromiumLocator"/> at each job.</param>
    /// <param name="runner">Test seam: replaces starting the browser process.</param>
    public ChromiumHtmlPdfRenderer(string? browserPath = null, Runner? runner = null)
        : this(browserPath is not null ? () => browserPath : new ChromiumLocator().Locate, runner)
    {
    }

    /// <summary>Test seam: decides which browser (or none) is found.</summary>
    internal ChromiumHtmlPdfRenderer(Func<string?> browser, Runner? runner)
    {
        _browser = browser;
        _run = runner ?? ((exe, args, options, ct) => ProcessRunner.RunAsync(exe, args, options, ct));
    }

    /// <summary>How long one page may take, including the browser start.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>True when a browser was found (the app may hide or explain the tool otherwise).</summary>
    public bool IsAvailable => _browser() is not null;

    public async Task<string?> PrintAsync(string htmlPath, string outputPath, HtmlPdfOptions options, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(htmlPath)) return $"File not found: {Path.GetFileName(htmlPath)}";
        if (_browser() is not { } browser) return MissingBrowserMessage;

        var work = Path.Combine(Path.GetTempPath(), "dropflow-chromium-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress.Report(new JobProgress(0.1, "Starting the browser"));
            var page = Path.Combine(work, "page.html");
            await File.WriteAllTextAsync(page, PreparePage(await ReadHtmlAsync(htmlPath, cancellationToken).ConfigureAwait(false), htmlPath, options),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var pdf = Path.Combine(work, "out.pdf");
            var args = BuildArguments(new Uri(page).AbsoluteUri, pdf, Path.Combine(work, "profile"), options.AllowOnline, RunningAsRoot());
            var result = await _run(browser, args, new ProcessOptions { Timeout = Timeout, WorkingDirectory = work }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.TimedOut) return $"The browser took longer than {Timeout.TotalSeconds:0} s to print the page.";
            if (!IsPdf(pdf)) return "The browser produced no PDF: " + result.LastMessage;

            progress.Report(new JobProgress(0.9, "Saving"));
            // the caller reserved outputPath (never an original); an empty placeholder there may be replaced
            File.Move(pdf, outputPath, overwrite: File.Exists(outputPath) && new FileInfo(outputPath).Length == 0);
            progress.Report(new JobProgress(1));
            return null;
        }
        catch (IOException ex)
        {
            return "Could not print the page: " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The browser's command line. Pure, so it is tested without a browser.</summary>
    public static IReadOnlyList<string> BuildArguments(string pageUrl, string pdfPath, string profileDirectory, bool allowOnline, bool noSandbox)
    {
        var args = new List<string>
        {
            "--headless",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-sync",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-default-apps",
            "--mute-audio",
            "--hide-scrollbars",
            "--run-all-compositor-stages-before-draw",
            "--allow-file-access-from-files",
            "--user-data-dir=" + profileDirectory,
            "--no-pdf-header-footer",
            "--print-to-pdf-no-header",
            "--print-to-pdf=" + pdfPath,
        };
        if (!allowOnline)
        {
            // Nothing leaves the machine: names do not resolve and every connection goes to a closed local port.
            args.Insert(0, "--host-resolver-rules=MAP * ~NOTFOUND");
            args.Insert(1, "--proxy-server=http://127.0.0.1:9");
            args.Insert(2, "--proxy-bypass-list=<-loopback>");
        }
        if (noSandbox) args.Insert(0, "--no-sandbox");   // Chromium refuses to start as root (containers) with its sandbox on
        args.Add(pageUrl);
        return args;
    }

    /// <summary>
    /// The page as printed: a print style sheet for paper, orientation and backgrounds, and a <c>&lt;base&gt;</c> at the original folder
    /// (unless the page has its own) so relative links keep working from the temporary copy.
    /// </summary>
    public static string PreparePage(string html, string originalPath, HtmlPdfOptions options)
    {
        var paper = options.Paper.Equals("letter", StringComparison.OrdinalIgnoreCase) ? "Letter" : "A4";
        var orientation = options.Landscape ? " landscape" : "";
        var adjust = options.Backgrounds ? "exact" : "economy";
        var folder = Path.GetDirectoryName(Path.GetFullPath(originalPath)) ?? Path.GetTempPath();
        var baseHref = new Uri(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar).AbsoluteUri;

        var head = new StringBuilder();
        if (!Regex.IsMatch(html, @"<base\s", RegexOptions.IgnoreCase))
            head.Append(CultureInfo.InvariantCulture, $"<base href=\"{baseHref}\">");
        head.Append(CultureInfo.InvariantCulture,
            $"<style>@page {{ size: {paper}{orientation}; }} html, body {{ -webkit-print-color-adjust: {adjust} !important; print-color-adjust: {adjust} !important; }}</style>");

        var match = Regex.Match(html, @"<head(\s[^>]*)?>", RegexOptions.IgnoreCase);
        if (match.Success) return html.Insert(match.Index + match.Length, head.ToString());
        var htmlTag = Regex.Match(html, @"<html(\s[^>]*)?>", RegexOptions.IgnoreCase);
        return htmlTag.Success
            ? html.Insert(htmlTag.Index + htmlTag.Length, "<head>" + head + "</head>")
            : "<!doctype html><html><head><meta charset=\"utf-8\">" + head + "</head><body>" + html + "</body></html>";
    }

    private static async Task<string> ReadHtmlAsync(string path, CancellationToken cancellationToken)
    {
        // Browsers sniff the encoding; DropFlow writes UTF-8 (with or without BOM), which is also what most saved pages are.
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var text = new UTF8Encoding(false).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (!Regex.IsMatch(text, @"<meta[^>]+charset", RegexOptions.IgnoreCase))
        {
            var head = Regex.Match(text, @"<head(\s[^>]*)?>", RegexOptions.IgnoreCase);
            text = head.Success ? text.Insert(head.Index + head.Length, "<meta charset=\"utf-8\">") : "<meta charset=\"utf-8\">" + text;
        }
        return text;
    }

    private static bool IsPdf(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 5) return false;
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[5];
        return stream.Read(head) == 5 && head.SequenceEqual("%PDF-"u8);
    }

    private static bool RunningAsRoot() => !OperatingSystem.IsWindows() && Environment.UserName == "root";
}
