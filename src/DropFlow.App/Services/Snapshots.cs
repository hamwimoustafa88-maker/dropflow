// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.AI.Models;
using DropFlow.App.Views;
using DropFlow.App.Views.Editors;
using DropFlow.Core.Catalog;
using DropFlow.Core.Localization;
using DropFlow.Core.Registry;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;
using DropFlow.Engines.Common;
using DropFlow.Overlay;

namespace DropFlow.App.Services;

/// <summary>
/// Developer snapshot mode: renders one window (or a wheel) off-screen to a PNG at 2× and exits, so the layout can be checked in
/// Arabic and English without clicking through the app. Nothing is converted and no file is written except the PNG.
/// <code>
/// DropFlow.exe --snapshot-window &lt;name&gt; &lt;out.png&gt; [--lang ar|en] [--dark] [--page N] [files…]
/// DropFlow.exe --snapshot-wheel &lt;convert|tools|actions&gt; &lt;out.png&gt; [--lang ar|en] [--dark] &lt;files…&gt;
/// </code>
/// Window names: edit, annotate, crop, redact, background, qr, metadata, welcome, settings, encoder, history.
/// </summary>
internal static class Snapshots
{
    public static readonly string[] WindowNames = ["edit", "annotate", "crop", "redact", "background", "qr", "metadata", "welcome", "settings", "encoder", "history"];

    public static bool IsSnapshot(string[] args) =>
        args.Length > 0 && args[0] is "--snapshot-window" or "--snapshot-wheel";

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --snapshot-window <name> <out.png> [--lang ar|en] [--dark] [--page N] [files...] | --snapshot-wheel <kind> <out.png> <files...>");
            return 2;
        }
        var name = args[1].ToLowerInvariant();
        var output = Path.GetFullPath(args[2]);
        var lang = "en";
        var dark = false;
        var page = 0;
        var files = new List<string>();
        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lang" when i + 1 < args.Length: lang = args[++i] == "ar" ? "ar" : "en"; break;
                case "--dark": dark = true; break;
                case "--page" when i + 1 < args.Length: int.TryParse(args[++i], out page); break;
                default: files.Add(Path.GetFullPath(args[i])); break;
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Application.Run is never called here, so install the UI context ourselves: awaits in the windows must come back to this thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var loc = JsonLocalizer.Load(lang);
        var ui = new UiKit(dark ? WheelTheme.Dark : WheelTheme.Light, loc);
        var work = Path.Combine(Path.GetTempPath(), "DropFlow", "snapshots");
        Directory.CreateDirectory(work);
        FileLog.Init(work);
        try
        {
            if (args[0] == "--snapshot-wheel") return Wheel(ui, loc, name, output, files, work);
            if (files.Count == 0) files.Add(name == "metadata" ? SampleJpeg(work) : PracticePhoto.Ensure(work));
            var window = Create(ui, name, files, page, work);
            if (window is null)
            {
                Console.Error.WriteLine($"unknown window '{name}'. Known: {string.Join(", ", WindowNames)}");
                return 2;
            }
            RenderWindow(window, output);
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            app.Shutdown();
        }
    }

    /// <summary>A JPEG with camera, location and Arabic caption fields, so the metadata editor has something to show.</summary>
    private static string SampleJpeg(string work)
    {
        var plain = Path.Combine(work, "sample plain.jpg");
        var path = Path.Combine(work, "Sample photo.jpg");
        if (File.Exists(path)) File.Delete(path);
        using (var image = PracticePhoto.Draw(800, 534))
        {
            var exif = new ImageMagick.ExifProfile();
            exif.SetValue(ImageMagick.ExifTag.GPSLatitude, [new ImageMagick.Rational(33, 1), new ImageMagick.Rational(53, 1), new ImageMagick.Rational(3768, 100)]);
            exif.SetValue(ImageMagick.ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ImageMagick.ExifTag.GPSLongitude, [new ImageMagick.Rational(35, 1), new ImageMagick.Rational(30, 1), new ImageMagick.Rational(648, 100)]);
            exif.SetValue(ImageMagick.ExifTag.GPSLongitudeRef, "E");
            image.SetProfile(exif);
            image.Write(plain, ImageMagick.MagickFormat.Jpeg);
        }
        Engines.Images.ImageMetadata.Apply(plain,
        [
            new(Engines.Images.MetadataGroup.Exif, "Make", "DropFlow"),
            new(Engines.Images.MetadataGroup.Exif, "Model", "Practice camera"),
            new(Engines.Images.MetadataGroup.Exif, "Artist", "Moustafa"),
            new(Engines.Images.MetadataGroup.Exif, "DateTimeOriginal", "2026-10-22 09:30:00"),
            new(Engines.Images.MetadataGroup.Xmp, "dc:title", "غروب فوق التلال"),
            new(Engines.Images.MetadataGroup.Iptc, "City", "بيروت"),
        ], path);
        return path;
    }

    private static Window? Create(UiKit ui, string name, IReadOnlyList<string> files, int page, string work)
    {
        var op = name switch
        {
            "edit" => "img.edit", "annotate" => "img.annotate", "crop" => "img.crop", "redact" => "img.redact",
            "background" => "img.background", "qr" => "img.qr", "metadata" => "img.metadata", _ => null,
        };
        if (op is not null) return ImageEditors.Create(ui, op, files, new NoDetectors());

        switch (name)
        {
            case "welcome":
            {
                var w = new WelcomeWindow(ui, new WelcomeContext(PracticePhoto.Ensure(work), new WheelKeys(), "system", true, "Ctrl+Alt+V", false, (_, _) => { }));
                w.GoTo(page);
                if (page == 1) w.ShowKeys(shift: true, ctrl: false, alt: false);
                return w;
            }
            case "history":
                return new HistoryWindow(ui, new Core.History.HistoryStore(Path.Combine(work, "history.json")), new RecycleBinDisposer());
            case "encoder":
                return new EncoderWindow(ui, new EngineLocator(), [Path.Combine(work, "clip.mp4")], (_, _) => { });
            case "settings":
                return new SettingsWindow(ui, new SettingsContext(new AppSettings(), true, false, (_, _, _) => [], [], _ => false,
                    _ => Task.FromResult(false), "0.0.0", [], [], _ => { }, []));
            default:
                return null;
        }
    }

    /// <summary>Shows the window off-screen, waits until its preview is ready, sets a little demo state, and saves it at 2×.</summary>
    private static void RenderWindow(Window window, string output)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        var editor = window as VisualEditorWindow;
        WaitUntil(() => editor is null || editor.IsReady, TimeSpan.FromSeconds(15));
        if (editor is not null && Demo(editor)) WaitUntil(() => editor.IsReady, TimeSpan.FromSeconds(15));
        Pump(TimeSpan.FromMilliseconds(400));
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        FileLog.Info($"snapshot {window.GetType().Name}: ready={editor?.IsReady}");
        // render the window (it carries the right-to-left mirroring) cropped to its client area
        Save(window, (int)content.ActualWidth, (int)content.ActualHeight, output, window.Background);
        window.Close();
    }

    /// <summary>Puts each editor into a state that shows its controls at work. Returns true when something changed.</summary>
    private static bool Demo(VisualEditorWindow editor)
    {
        switch (editor)
        {
            case PhotoEditWindow edit:
                edit.SetSlider("exposure", 15);
                edit.SetSlider("temperature", 35);
                edit.SetSlider("vignette", 40);
                return true;
            case CropWindow crop:
                crop.ChooseAspect("16:9");
                return true;
            case RedactWindow redact:
                redact.AddBox(new Box(180, 420, 260, 140));
                redact.AddBox(new Box(760, 120, 220, 160));
                return true;
            case AnnotateWindow annotate when annotate.Document is { } doc:
            {
                var l = doc.LongSide;
                annotate.AddShape(new AnnotationShape("arrow", doc.Width * 0.15, doc.Height * 0.2, doc.Width * 0.45, doc.Height * 0.45, "#ef4444", l / 120));
                annotate.AddShape(new AnnotationShape("rect", doc.Width * 0.55, doc.Height * 0.15, doc.Width * 0.85, doc.Height * 0.45, "#facc15", l / 160));
                annotate.AddShape(new AnnotationShape("number", doc.Width * 0.12, doc.Height * 0.75, doc.Width * 0.12, doc.Height * 0.75, "#4338ca", 2, Size: l * 0.05, N: 1));
                annotate.AddShape(new AnnotationShape("text", doc.Width * 0.2, doc.Height * 0.66, doc.Width * 0.2, doc.Height * 0.66, "#ffffff", 2, Fill: true, Text: "مرحباً DropFlow", Size: l * 0.045));
                annotate.AddShape(new AnnotationShape("magnify", doc.Width * 0.74, doc.Height * 0.3, doc.Width * 0.85, doc.Height * 0.72, "#ffffff", l / 300, Size: l * 0.04, Zoom: 2.5));
                return true;
            }
            default:
                return false;
        }
    }

    private static int Wheel(UiKit ui, ILocalizer loc, string kindName, string output, IReadOnlyList<string> files, string work)
    {
        var kind = kindName switch { "tools" => WheelKind.Tools, "actions" => WheelKind.Actions, _ => WheelKind.Convert };
        if (files.Count == 0) files = [PracticePhoto.Ensure(work)];

        var catalog = ToolCatalog.LoadEmbedded();
        var engines = new EngineLocator(AppPaths.DefaultDir);
        using var models = new ModelManager(Path.Combine(work, "models"));
        var registry = new ToolRegistry().Register(Modules.Create(engines, Dispatcher.CurrentDispatcher, Path.Combine(work, "webview"), _ => Task.CompletedTask, models));
        var set = new WheelBuilder(catalog, registry, engines).Build(files, loc.Language, ActionService.SupportedIds);

        var window = new WheelWindow();
        window.Wheel.Theme = ui.Theme;
        var presenter = new WheelPresenter(window, loc);
        presenter.SetItems(kind, set.Of(kind), files.Count);
        var root = (FrameworkElement)window.Content;
        window.Content = null;

        var size = WheelWindow.SizeDips;
        root.Measure(new Size(size, size));
        root.Arrange(new Rect(0, 0, size, size));
        root.UpdateLayout();
        Save(root, (int)size, (int)size, output, ui.Theme.Hub);
        Console.WriteLine(output);
        return 0;
    }

    private static void Save(Visual visual, int width, int height, string output, Brush? background)
    {
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (background is not null)
        {
            var under = new DrawingVisual();
            using (var dc = under.RenderOpen()) dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            bitmap.Render(under);
        }
        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    private static void WaitUntil(Func<bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(50));
    }

    /// <summary>Lets the dispatcher run (async loads, renders, layout) for a while.</summary>
    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Snapshots never run detection; the buttons stay hidden.</summary>
    private sealed class NoDetectors : IRedactDetectors
    {
        public bool FacesAvailable => false;
        public Task<bool> TextAvailableAsync() => Task.FromResult(false);
        public Task<IReadOnlyList<Box>> FacesAsync(string path, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Box>>([]);
        public Task<IReadOnlyList<Box>> TextAsync(string path, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Box>>([]);
    }
}
