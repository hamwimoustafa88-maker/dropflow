using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Services;
using DropFlow.App.Views;
using DropFlow.Core.Catalog;
using DropFlow.Core.Localization;
using DropFlow.Engines.Archive.Sharing;
using DropFlow.Overlay;
using DropFlow.TestSupport;

namespace DropFlow.App.Tests;

public class PhoneShareWindowTests
{
    [Fact]
    public void Every_windows_action_in_the_catalog_is_handled_by_the_action_service()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var missing = catalog.Actions.Where(a => a.Platforms.Contains("windows")).Select(a => a.Id).Except(ActionService.SupportedIds).ToList();
        Assert.True(missing.Count == 0, "not handled: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void The_window_shows_the_qr_code_and_closing_it_stops_the_share(string language)
    {
        using var dir = new TempDir();
        var file = dir.WriteText("تقرير.txt", "hello");

        var (png, running, runningAfterClose) = Sta.Run(() =>
        {
            var server = new PhoneShareServer([file], new PhoneShareOptions { LoopbackOnly = true, Lifetime = TimeSpan.FromMinutes(5) });
            server.Start();
            var window = new PhoneShareWindow(new UiKit(WheelTheme.FromSystem(), JsonLocalizer.Load(language)), server);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var path = Path.Combine(Path.GetTempPath(), $"dropflow-phone-{language}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);

            var wasRunning = server.IsRunning;
            window.Close();
            return (path, wasRunning, server.IsRunning);
        });

        Assert.True(new FileInfo(png).Length > 5_000);
        Assert.True(running);
        Assert.False(runningAfterClose);
    }
}
