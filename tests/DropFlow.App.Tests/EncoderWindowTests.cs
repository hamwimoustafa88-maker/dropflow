using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Views;
using DropFlow.Core.Localization;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Overlay;

namespace DropFlow.App.Tests;

internal static class Sta
{
    /// <summary>Runs <paramref name="work"/> on a fresh STA thread (WPF controls need one) and rethrows what it threw.</summary>
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}

public class EncoderWindowTests
{
    private static EncoderWindow Create(string language) =>
        new(new UiKit(WheelTheme.FromSystem(), JsonLocalizer.Load(language)), TestSupport.Engines.Locator, [@"C:\videos\clip.mp4"], (_, _) => { });

    [Fact]
    public void Every_preset_survives_a_round_trip_through_the_controls()
    {
        var problems = Sta.Run(() =>
        {
            var found = new List<string>();
            var window = Create("en");
            foreach (var preset in EncoderPresets.All)
            {
                window.ChoosePreset(preset.Id);
                var got = window.CurrentSettings;
                if (got is null) { found.Add($"{preset.Id}: no settings ({window.ErrorText})"); continue; }
                var want = preset.Settings;

                void Check(string field, object? expected, object? actual)
                {
                    if (!Equals(expected, actual)) found.Add($"{preset.Id}: {field} expected {expected ?? "null"} but was {actual ?? "null"}");
                }

                Check("Container", want.Container, got.Container);
                Check("VideoCodec", want.VideoCodec, got.VideoCodec);
                Check("Hardware", want.Hardware, got.Hardware);
                Check("RateControl", want.RateControl, got.RateControl);
                if (want.RateControl == RateControl.TargetSize) Check("TargetSizeMb", want.TargetSizeMb, got.TargetSizeMb);
                if (want.RateControl == RateControl.Crf && want.Crf is not null) Check("Crf", want.Crf, got.Crf);
                Check("MaxHeight", want.MaxHeight, got.MaxHeight);
                Check("Canvas", want.Canvas, got.Canvas);
                Check("MaxFps", want.MaxFps, got.MaxFps);
                Check("Preset", want.Preset, got.Preset);
                Check("AudioCodec", want.AudioCodec, got.AudioCodec);
                Check("AudioBitrateKbps", want.AudioBitrateKbps, got.AudioBitrateKbps);
                Check("ExtractAudioOnly", want.ExtractAudioOnly, got.ExtractAudioOnly);
            }
            return found;
        });

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_bad_trim_time_is_reported_instead_of_encoding_with_it()
    {
        var (settings, error) = Sta.Run(() =>
        {
            var window = Create("en");
            window.ChoosePreset("whatsapp");
            window.TypeTrimStart("abc");
            return (window.CurrentSettings, window.ErrorText);
        });

        Assert.Null(settings);
        Assert.Equal(JsonLocalizer.Load("en")["encoder.invalidTime"], error);
    }

    [Fact]
    public void A_valid_trim_time_reaches_the_settings()
    {
        var settings = Sta.Run(() =>
        {
            var window = Create("en");
            window.ChoosePreset("whatsapp");
            window.TypeTrimStart("1:30");
            return window.CurrentSettings;
        });

        Assert.Equal(TimeSpan.FromSeconds(90), settings!.TrimStart);
    }

    [Fact]
    public void Digits_typed_on_an_arabic_keyboard_are_understood()
    {
        var settings = Sta.Run(() =>
        {
            var window = Create("ar");
            window.ChoosePreset("whatsapp");
            window.TypeTrimStart("١:٣٠");
            return window.CurrentSettings;
        });

        Assert.Equal(TimeSpan.FromSeconds(90), settings!.TrimStart);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void The_window_shows_and_renders_in_both_directions(string language)
    {
        var path = Sta.Run(() =>
        {
            var window = Create(language);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var file = Path.Combine(Path.GetTempPath(), $"dropflow-encoder-{language}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, window.FlowDirection);
            window.Close();
            return file;
        });

        Assert.True(new FileInfo(path).Length > 5_000);
    }
}
