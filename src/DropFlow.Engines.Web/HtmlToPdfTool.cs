using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DropFlow.Engines.Web;

/// <summary>
/// Prints local HTML files to PDF with the Chromium (WebView2) runtime that ships with Windows 10/11.
/// Pages are rendered in one hidden off-screen window that is reused between jobs (creating a browser right after the previous one
/// closed can stall for 20–30 s) and released after <see cref="IdleTimeout"/>. By default every http(s) request is blocked, so a page
/// cannot reach the network. All browser objects live on the UI thread of <paramref name="dispatcher"/>.
/// </summary>
public sealed class HtmlToPdfTool(Dispatcher dispatcher, string userDataFolder) : IConverter, IDisposable
{
    public const string ToolId = "pdf.html-to-pdf";
    public const string ConvertPdf = "convert:pdf";

    private static readonly string[] HtmlExtensions = ["html", "htm", "xhtml"];
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OutputPathResolver _paths = new();

    // UI-thread state
    private Window? _window;
    private WebView2? _view;
    private DispatcherTimer? _idle;
    private bool _allowOnline;
    private bool _broken;

    /// <summary>How long the hidden browser stays alive after the last job.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyCollection<string> Operations => [ToolId, ConvertPdf];

    public bool Supports(string operation, string inputExtension) =>
        operation is ToolId or ConvertPdf && HtmlExtensions.Contains(inputExtension);

    public IReadOnlyList<ToolOption> OptionsFor(string operation) => operation == ToolId
        ?
        [
            new("paper", ToolOptionKind.Choice, "a4", ["a4", "letter"]),
            new("landscape", ToolOptionKind.Bool, false),
            new("background", ToolOptionKind.Bool, true),
            new("online", ToolOptionKind.Bool, false),
        ]
        : [];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var input = job.Inputs[0];
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        var choices = new PrintChoices(
            Paper: job.GetString("paper", "a4")!,
            Landscape: job.GetBool("landscape"),
            Backgrounds: job.GetBool("background", true),
            Online: job.GetBool("online"));
        var output = _paths.ForFile(input, "pdf", "converted", job.OutputDirectory);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            progress.Report(new JobProgress(0.1, "Starting the web renderer"));
            string? error;
            try
            {
                error = await await dispatcher.InvokeAsync(() => PrintAsync(input, output, choices, progress, cancellationToken));
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return ConversionResult.Fail("The Microsoft Edge WebView2 runtime is not installed.");
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                DeletePartial(output);
                return ConversionResult.Fail($"Could not render the page: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                DeletePartial(output);
                throw;
            }

            if (error is not null)
            {
                DeletePartial(output);
                return ConversionResult.Fail(error);
            }

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                DeletePartial(output);
                return ConversionResult.Fail("The renderer produced no PDF.");
            }

            progress.Report(new JobProgress(1));
            return ConversionResult.Ok([output], DateTime.UtcNow - started);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the hidden browser now (the next job starts a new one).</summary>
    public void Dispose()
    {
        if (dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(Release);
    }

    /// <summary>Runs on the UI thread. Returns an error message, or null on success.</summary>
    private async Task<string?> PrintAsync(string input, string output, PrintChoices choices, IProgress<JobProgress> progress, CancellationToken ct)
    {
        _idle?.Stop();
        try
        {
            var core = await AcquireAsync();
            _allowOnline = choices.Online;

            var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e);
            core.NavigationCompleted += OnCompleted;
            CoreWebView2NavigationCompletedEventArgs navigation;
            try
            {
                core.Navigate(new Uri(input).AbsoluteUri);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(LoadTimeout);
                try
                {
                    navigation = await loaded.Task.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _broken = true; // a stuck page: do not reuse this browser
                    return "The page took too long to load.";
                }
            }
            finally
            {
                core.NavigationCompleted -= OnCompleted;
            }

            if (!navigation.IsSuccess) return $"The page could not be loaded ({navigation.WebErrorStatus}).";
            progress.Report(new JobProgress(0.6, "Printing"));

            // let web fonts and late layout settle before printing
            await Task.Delay(300, ct);

            var print = core.Environment.CreatePrintSettings();
            print.ShouldPrintBackgrounds = choices.Backgrounds;
            print.ShouldPrintHeaderAndFooter = false;
            print.Orientation = choices.Landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            (print.PageWidth, print.PageHeight) = choices.Paper == "letter" ? (8.5, 11.0) : (8.27, 11.69);

            ct.ThrowIfCancellationRequested();
            var printed = await core.PrintToPdfAsync(output, print);
            return printed ? null : "The renderer could not write the PDF.";
        }
        catch
        {
            _broken = true; // unknown state after a failure: start fresh next time
            throw;
        }
        finally
        {
            if (_broken) Release();
            else
            {
                // stop the previous page's scripts and free its memory while the browser idles
                _view?.CoreWebView2?.Navigate("about:blank");
                ScheduleRelease();
            }
        }
    }

    private async Task<CoreWebView2> AcquireAsync()
    {
        if (_broken) Release();
        if (_view?.CoreWebView2 is { } existing) return existing;

        var view = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder } };
        var window = new Window
        {
            Width = 1024,
            Height = 768,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = view,
        };
        _view = view;
        _window = window;
        try
        {
            window.Show();
            await view.EnsureCoreWebView2Async();
        }
        catch
        {
            Release();
            throw;
        }

        var core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.ProcessFailed += (_, _) => _broken = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        return core;
    }

    /// <summary>Allows file:, data:, blob: and about: only; everything else (http, https, ws…) gets a 403 unless the job allows online.</summary>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_allowOnline || sender is not CoreWebView2 core) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "file" or "data" or "blob" or "about") return;
        e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by DropFlow", "");
    }

    private void ScheduleRelease()
    {
        _idle ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = IdleTimeout };
        _idle.Tick -= OnIdle;
        _idle.Tick += OnIdle;
        _idle.Interval = IdleTimeout;
        _idle.Start();
    }

    private void OnIdle(object? sender, EventArgs e) => Release();

    private void Release()
    {
        _idle?.Stop();
        var view = _view;
        var window = _window;
        _view = null;
        _window = null;
        _broken = false;
        view?.Dispose();
        window?.Close();
    }

    private static void DeletePartial(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leave it; the user can delete it */ }
    }

    private sealed record PrintChoices(string Paper, bool Landscape, bool Backgrounds, bool Online);
}
