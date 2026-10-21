using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Views;
using DropFlow.Core.Localization;
using DropFlow.Core.Sources;
using DropFlow.Overlay;

namespace DropFlow.App.Tests;

public class OpenLinkWindowTests
{
    private static readonly string[] LinkKeys =
    [
        "tray.openLink", "link.title", "link.hint", "link.download", "link.privacy", "link.downloading", "link.progress",
        "link.progressUnknown", "link.cancelled", "link.invalid", "link.scheme", "link.credentials", "link.error.status",
        "link.error.redirects", "link.error.tooLarge", "link.error.network", "link.error.storage",
    ];

    private delegate Task<string> Download(Uri url, IProgress<DownloadProgress> progress, CancellationToken ct);

    private static OpenLinkWindow Create(string language, Download download, Action<string> opened, string? initial = null)
    {
        // The real app awaits on the UI thread's dispatcher context; give the test thread the same, so continuations come back to it.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        return new(new UiKit(WheelTheme.FromSystem(), JsonLocalizer.Load(language)), (u, p, c) => download(u, p, c), opened, @"C:\Users\me\Downloads\DropFlow", initial);
    }

    /// <summary>Lets queued dispatcher work (continuations, Loaded) run until <paramref name="done"/> or a timeout.</summary>
    private static void Pump(Dispatcher dispatcher, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(5);
        }
        Assert.True(done(), "timed out waiting for the window");
    }

    private static OpenLinkWindow Show(OpenLinkWindow window)
    {
        window.Show();
        Pump(window.Dispatcher, () => window.IsLoaded);
        return window;
    }

    [Theory]
    [InlineData("en", FlowDirection.LeftToRight)]
    [InlineData("ar", FlowDirection.RightToLeft)]
    public void The_window_renders_and_follows_the_language_direction(string language, FlowDirection flow)
    {
        var (bytes, direction, urlFlow) = Sta.Run(() =>
        {
            var window = Show(Create(language, (_, _, _) => Task.FromResult("x"), _ => { }, "https://example.com/a.pdf"));
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var path = Path.Combine(Path.GetTempPath(), $"dropflow-link-{language}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var result = (new FileInfo(path).Length, window.FlowDirection, window.UrlText);
            window.Close();
            return result;
        });

        Assert.True(bytes > 5_000);
        Assert.Equal(flow, direction);
        Assert.Equal("https://example.com/a.pdf", urlFlow);
    }

    [Theory]
    [InlineData("", "link.invalid")]
    [InlineData("not a link", "link.invalid")]
    [InlineData("ftp://example.com/a.zip", "link.scheme")]
    [InlineData("file:///C:/Windows/win.ini", "link.scheme")]
    [InlineData("https://me:pw@example.com/a.pdf", "link.credentials")]
    public void A_bad_address_shows_a_message_and_starts_no_download(string text, string key)
    {
        foreach (var language in new[] { "en", "ar" })
        {
            var (message, calls) = Sta.Run(() =>
            {
                var calls = 0;
                var window = Show(Create(language, (_, _, _) => { calls++; return Task.FromResult("x"); }, _ => calls += 100));
                window.UrlText = text;
                Pump(window.Dispatcher, () => window.StartAsync().IsCompleted);
                var shown = window.MessageText;
                window.Close();
                return (shown, calls);
            });

            Assert.Equal(JsonLocalizer.Load(language)[key], message);
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public void A_good_address_downloads_closes_the_window_and_hands_over_the_file()
    {
        var (requested, opened, closed) = Sta.Run(() =>
        {
            Uri? requested = null;
            string? opened = null;
            var window = Show(Create("en", (url, progress, _) =>
            {
                requested = url;
                progress.Report(new DownloadProgress(5, 10, "a.pdf"));
                return Task.FromResult(@"C:\Users\me\Downloads\DropFlow\a.pdf");
            }, path => opened = path));
            window.UrlText = "  https://example.com/files/a.pdf?x=1  ";
            var start = window.StartAsync();
            Pump(window.Dispatcher, () => start.IsCompleted);
            return (requested, opened, !window.IsVisible);
        });

        Assert.Equal("https://example.com/files/a.pdf?x=1", requested!.AbsoluteUri);
        Assert.Equal(@"C:\Users\me\Downloads\DropFlow\a.pdf", opened);
        Assert.True(closed);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_refused_download_shows_the_translated_reason_and_keeps_the_window_open(string language)
    {
        var (message, opened, visible, enabled) = Sta.Run(() =>
        {
            string? opened = null;
            var window = Show(Create(language, (_, _, _) => throw new UrlDownloadException(UrlDownloadError.HttpStatus, "x", 404, "Not Found"), p => opened = p));
            window.UrlText = "https://example.com/missing.pdf";
            var start = window.StartAsync();
            Pump(window.Dispatcher, () => start.IsCompleted);
            var result = (window.MessageText, opened, window.IsVisible, !window.IsDownloading);
            window.Close();
            return result;
        });

        Assert.Equal(JsonLocalizer.Load(language).Format("link.error.status", "404", "Not Found"), message);
        Assert.Contains("404", message);
        Assert.Null(opened);
        Assert.True(visible);
        Assert.True(enabled);
    }

    [Fact]
    public void Cancelling_stops_the_running_download_keeps_the_window_and_hands_over_nothing()
    {
        var (token, message, opened, downloadingBefore, downloadingAfter, visible) = Sta.Run(() =>
        {
            CancellationToken seen = default;
            string? opened = null;
            var window = Show(Create("en", async (_, _, ct) =>
            {
                seen = ct;
                await Task.Delay(Timeout.Infinite, ct);
                return "never";
            }, p => opened = p));
            window.UrlText = "https://example.com/big.zip";
            var start = window.StartAsync();
            var before = window.IsDownloading;
            window.CancelOrClose();
            Pump(window.Dispatcher, () => start.IsCompleted);
            var result = (seen.IsCancellationRequested, window.MessageText, opened, before, window.IsDownloading, window.IsVisible);
            window.Close();
            return result;
        });

        Assert.True(token);
        Assert.Equal("Download cancelled.", message);
        Assert.Null(opened);
        Assert.True(downloadingBefore);
        Assert.False(downloadingAfter);
        Assert.True(visible);
    }

    [Fact]
    public void Cancel_with_nothing_running_closes_the_window_and_closing_mid_download_cancels_it()
    {
        var (closedByCancel, cancelledByClose) = Sta.Run(() =>
        {
            var idle = Show(Create("en", (_, _, _) => Task.FromResult("x"), _ => { }));
            idle.CancelOrClose();
            Pump(idle.Dispatcher, () => !idle.IsVisible);

            CancellationToken seen = default;
            var busy = Show(Create("en", async (_, _, ct) => { seen = ct; await Task.Delay(Timeout.Infinite, ct); return "never"; }, _ => { }));
            busy.UrlText = "https://example.com/x.bin";
            var start = busy.StartAsync();
            busy.Close();
            Pump(busy.Dispatcher, () => start.IsCompleted);
            return (!idle.IsVisible, seen.IsCancellationRequested);
        });

        Assert.True(closedByCancel);
        Assert.True(cancelledByClose);
    }

    [Fact]
    public void Every_link_string_exists_in_both_languages_and_every_failure_kind_has_a_message()
    {
        var en = JsonLocalizer.ReadEmbedded("en");
        var ar = JsonLocalizer.ReadEmbedded("ar");
        foreach (var key in LinkKeys)
        {
            Assert.True(en.ContainsKey(key), "missing in English: " + key);
            Assert.True(ar.ContainsKey(key), "missing in Arabic: " + key);
            Assert.Matches(@"[؀-ۿ]", ar[key]);
        }
        // no stray link.* key outside the list this window and the host use
        Assert.Empty(en.Keys.Where(k => k.StartsWith("link.", StringComparison.Ordinal)).Except(LinkKeys));

        foreach (var language in new[] { "en", "ar" })
        {
            var loc = JsonLocalizer.Load(language);
            foreach (var kind in Enum.GetValues<UrlDownloadError>())
            {
                var text = new UrlDownloadException(kind, "x", 500, "Server Error", 1024, null).Describe(loc);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}/{kind}");
                Assert.DoesNotContain("link.", text);
            }
        }
    }
}
