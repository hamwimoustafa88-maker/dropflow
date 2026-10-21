using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Rendering;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DropFlow.Engines.Web;

/// <summary>
/// Prints local HTML files to PDF with the Chromium (WebView2) runtime that ships with Windows 10/11, so documents become PDF and
/// pictures with nothing to download. Pages are rendered in one hidden off-screen window that is reused between jobs (creating a
/// browser right after the previous one closed can stall for 20–30 s) and released after <see cref="IdleTimeout"/>. By default every
/// http(s) request is blocked, so a page cannot reach the network. All browser objects live on the UI thread of the dispatcher.
/// </summary>
public sealed class WebPageRenderer(Dispatcher dispatcher, string userDataFolder) : IHtmlPdfRenderer, IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // UI-thread state
    private Window? _window;
    private WebView2? _view;
    private DispatcherTimer? _idle;
    private bool _allowOnline;
    private bool _broken;

    /// <summary>How long the hidden browser stays alive after the last job.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<string?> PrintAsync(string htmlPath, string outputPath, HtmlPdfOptions options, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(htmlPath)) return $"File not found: {Path.GetFileName(htmlPath)}";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            progress.Report(new JobProgress(0.1, "Starting the web renderer"));
            string? error;
            try
            {
                error = await await dispatcher.InvokeAsync(() => PrintOnUiThreadAsync(htmlPath, outputPath, options, progress, cancellationToken));
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return "The Microsoft Edge WebView2 runtime is not installed.";
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                DeletePartial(outputPath);
                return $"Could not render the page: {ex.Message}";
            }
            catch (OperationCanceledException)
            {
                DeletePartial(outputPath);
                throw;
            }

            if (error is not null)
            {
                DeletePartial(outputPath);
                return error;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                DeletePartial(outputPath);
                return "The renderer produced no PDF.";
            }

            progress.Report(new JobProgress(1));
            return null;
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

    /// <summary>Navigates (through about:blank first when a page is showing) and waits for the completion event. Runs on the UI thread.</summary>
    private async Task<(CoreWebView2NavigationCompletedEventArgs? Result, bool TimedOut)> NavigateAsync(CoreWebView2 core, string uri, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e);
        core.NavigationCompleted += OnCompleted;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoadTimeout);
            try
            {
                if (!string.Equals(core.Source, "about:blank", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(core.Source))
                {
                    core.Navigate("about:blank");
                    await loaded.Task.WaitAsync(timeout.Token);
                    loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                core.Navigate(uri);
                return (await loaded.Task.WaitAsync(timeout.Token), false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, true);
            }
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>Runs on the UI thread. Returns an error message, or null on success.</summary>
    private async Task<string?> PrintOnUiThreadAsync(string htmlPath, string outputPath, HtmlPdfOptions options, IProgress<JobProgress> progress, CancellationToken ct)
    {
        _idle?.Stop();
        try
        {
            var core = await AcquireAsync();
            _allowOnline = options.AllowOnline;

            // A navigation that an earlier one overlaps is aborted by WebView2 (seen on CI: "ConnectionAborted" on the second page in a
            // row). Start from a blank page and retry once; any other failure is reported as is.
            CoreWebView2NavigationCompletedEventArgs navigation;
            var uri = new Uri(htmlPath).AbsoluteUri;
            var attempt = 0;
            while (true)
            {
                var (result, timedOut) = await NavigateAsync(core, uri, ct);
                if (timedOut)
                {
                    _broken = true; // a stuck page: do not reuse this browser
                    return "The page took too long to load.";
                }
                navigation = result!;
                if (navigation.IsSuccess || ++attempt >= 2
                    || navigation.WebErrorStatus is not (CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.OperationCanceled))
                    break;
                await Task.Delay(200, ct);
            }

            if (!navigation.IsSuccess) return $"The page could not be loaded ({navigation.WebErrorStatus}).";
            progress.Report(new JobProgress(0.6, "Printing"));

            // let web fonts and late layout settle before printing
            await Task.Delay(300, ct);

            var print = core.Environment.CreatePrintSettings();
            print.ShouldPrintBackgrounds = options.Backgrounds;
            print.ShouldPrintHeaderAndFooter = false;
            print.Orientation = options.Landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            (print.PageWidth, print.PageHeight) = options.Paper == "letter" ? (8.5, 11.0) : (8.27, 11.69);

            ct.ThrowIfCancellationRequested();
            var printed = await core.PrintToPdfAsync(outputPath, print);
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
}
