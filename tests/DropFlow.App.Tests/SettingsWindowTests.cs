using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Views;
using DropFlow.Core.Automation;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Settings;
using DropFlow.Overlay;

namespace DropFlow.App.Tests;

public class SettingsWindowTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump(Dispatcher dispatcher)
    {
        for (var i = 0; i < 5; i++) dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    private static SettingsWindow Create(string language, List<ModelItem> models) => new(
        new UiKit(WheelTheme.FromSystem(), JsonLocalizer.Load(language)),
        new SettingsContext(
            new AppSettings(), StartupOn: false, MenuOn: true, Apply: (_, _, _) => [],
            Engines: [EngineId.Ffmpeg, EngineId.Qpdf], IsInstalled: e => e == EngineId.Ffmpeg, Install: _ => Task.FromResult(true),
            Version: "0.1.0", Rules: [], RecipeNames: ["web-photo"], SaveRules: _ => { }, Models: models));

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void The_window_renders_with_the_model_list_in_both_directions(string language)
    {
        var png = Sta.Run(() =>
        {
            var models = new List<ModelItem>
            {
                new("ggml-small", "Whisper small", 487_601_967, () => true, () => Task.FromResult(true), () => true),
                new("u2netp", "U2-Net (small)", 4_574_861, () => false, () => Task.FromResult(true), () => true),
            };
            var window = Create(language, models);
            window.Show();
            Pump(window.Dispatcher);
            window.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var path = Path.Combine(Path.GetTempPath(), $"dropflow-settings-{language}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            window.Close();
            return path;
        });

        Assert.True(new FileInfo(png).Length > 5_000);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Items_added_to_the_wheels_are_listed_and_remove_takes_them_off(string language)
    {
        var (removed, before, after, empty) = Sta.Run(() =>
        {
            var loc = JsonLocalizer.Load(language);
            var gone = new List<string>();
            var window = new SettingsWindow(
                new UiKit(WheelTheme.FromSystem(), loc),
                new SettingsContext(
                    new AppSettings(), false, false, (_, _, _) => [], [], _ => false, _ => Task.FromResult(true), "0.1.0", [], [], _ => { }, [],
                    [new PinnedRow("Extract text (OCR)", "Convert · Images", () => gone.Add("img.ocr")), new PinnedRow("PDF", "Tools · Images", () => gone.Add("convert:pdf"))]));
            window.Show();
            Pump(window.Dispatcher);

            int Removes() => Descendants<Button>(window).Count(b => Equals(b.Content, loc["settings.pins.remove"]));
            var count = Removes();
            foreach (var b in Descendants<Button>(window).Where(b => Equals(b.Content, loc["settings.pins.remove"])).ToList())
                b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window.Dispatcher);
            var left = Removes();
            var emptyShown = Descendants<TextBlock>(window).Any(t => t.Text == loc["settings.pins.empty"]);
            window.Close();
            return (gone, count, left, emptyShown);
        });

        Assert.Equal(["img.ocr", "convert:pdf"], removed);
        Assert.Equal(2, before);
        Assert.Equal(0, after);
        Assert.True(empty);
    }

    [Fact]
    public void Pressing_download_runs_the_download_and_the_row_then_offers_delete()
    {
        var (installedAfter, label) = Sta.Run(() =>
        {
            var installed = false;
            var loc = JsonLocalizer.Load("en");
            var models = new List<ModelItem> { new("m", "Model M", 10_000_000, () => installed, () => { installed = true; return Task.FromResult(true); }, () => true) };
            var window = Create("en", models);
            window.Show();
            Pump(window.Dispatcher);

            var button = Descendants<Button>(window).Single(b => Equals(b.Content, loc["settings.model.download"]));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window.Dispatcher);

            var after = Descendants<Button>(window).Any(b => Equals(b.Content, loc["settings.model.delete"]));
            window.Close();
            return (installed, after ? "delete" : "download");
        });

        Assert.True(installedAfter);
        Assert.Equal("delete", label);
    }
}
