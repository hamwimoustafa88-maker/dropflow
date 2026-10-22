using System.IO;
using System.Windows;
using System.Windows.Threading;
using DropFlow.App.Services;
using DropFlow.App.Views;
using DropFlow.App.Views.Editors;
using DropFlow.Core.Localization;
using DropFlow.Core.Settings;
using DropFlow.Engines.Archive.Images;
using DropFlow.Engines.Images;
using DropFlow.Overlay;
using DropFlow.TestSupport;

namespace DropFlow.App.Tests;

/// <summary>Runs an editor window like the app does (UI context installed, window shown off-screen) and pumps until it is ready.</summary>
internal static class EditorHarness
{
    public static UiKit Kit(string language = "en") => new(WheelTheme.Light, JsonLocalizer.Load(language));

    public static T Run<T>(Func<T> work) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        return work();
    });

    public static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -32000;
        window.ShowActivated = false;
        window.Show();
    }

    public static void WaitReady(VisualEditorWindow window, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        Pump(TimeSpan.FromMilliseconds(50));
        while (!window.IsReady && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(30));
        Assert.True(window.IsReady, $"{window.GetType().Name} never became ready ({window.MessageText})");
    }

    public static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

public sealed class EditorWindowTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _photo;

    public EditorWindowTests() => _photo = PracticePhoto.Ensure(_dir.Path);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Every_image_tool_with_an_editor_opens_it_for_pictures_but_not_for_other_files()
    {
        var editors = new ImageEditors(EditorHarness.Kit());
        var text = _dir.Combine("note.txt");
        File.WriteAllText(text, "hello");
        foreach (var op in ImageEditors.Operations) Assert.True(editors.CanEdit(op, [_photo]), op);
        Assert.True(editors.CanEdit("img.qr", [text]));
        Assert.False(editors.CanEdit("img.edit", [text]));
        Assert.False(editors.CanEdit("img.compress", [_photo]));
        Assert.False(editors.CanEdit("img.edit", [_dir.Combine("missing.png")]));
    }

    [Fact]
    public void Edit_photo_turns_sliders_into_options_saturation_included()
    {
        var options = EditorHarness.Run(() =>
        {
            var w = new PhotoEditWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.SetSlider("exposure", 25);
            w.SetSlider("saturation", -40);
            w.SetSlider("vignette", 30);
            EditorHarness.WaitReady(w);
            var o = w.CurrentOptions;
            w.Close();
            return o;
        });
        Assert.NotNull(options);
        Assert.Equal(25d, options!["exposure"]);
        Assert.Equal(60d, options["saturation"]);
        Assert.Equal(30d, options["vignette"]);
        Assert.False(options.ContainsKey("contrast"));
    }

    [Fact]
    public void Edit_photo_with_nothing_changed_refuses_to_save()
    {
        var (options, message) = EditorHarness.Run(() =>
        {
            var w = new PhotoEditWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            var o = w.CurrentOptions;
            var m = w.MessageText;
            w.Close();
            return (o, m);
        });
        Assert.Null(options);
        Assert.Equal(JsonLocalizer.Load("en")["editor.nothingChanged"], message);
    }

    [Fact]
    public void Crop_with_a_ratio_gives_an_exact_box_in_original_pixels()
    {
        var options = EditorHarness.Run(() =>
        {
            var w = new CropWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.ChooseAspect("1:1");
            var o = w.CurrentOptions;
            w.Close();
            return o;
        });
        Assert.Equal(800d, options!["width"]);
        Assert.Equal(800d, options["height"]);
        Assert.Equal(200d, options["x"]);
        Assert.Equal("1:1", options["aspect"]);
    }

    [Fact]
    public void Redact_boxes_become_regions_and_missing_detectors_hide_their_buttons()
    {
        var (options, faces, text) = EditorHarness.Run(() =>
        {
            var w = new RedactWindow(EditorHarness.Kit(), [_photo], null);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.AddBox(new Box(10.4, 20, 100, 50));
            w.AddBox(new Box(1150, 700, 200, 200));     // runs off the picture: clamped
            EditorHarness.WaitReady(w);
            var o = w.CurrentOptions;
            var r = (o, w.FacesButtonVisible, w.TextButtonVisible);
            w.Close();
            return r;
        });
        Assert.Equal("10,20,100,50;1150,700,50,100", options!["regions"]);
        Assert.Equal("solid", options["style"]);
        Assert.False(faces);
        Assert.False(text);
    }

    [Fact]
    public void Redact_shows_detection_buttons_only_for_what_is_installed()
    {
        var (faces, text) = EditorHarness.Run(() =>
        {
            var w = new RedactWindow(EditorHarness.Kit(), [_photo], new FakeDetectors(faces: true, text: false));
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            EditorHarness.Pump(TimeSpan.FromMilliseconds(100));
            var r = (w.FacesButtonVisible, w.TextButtonVisible);
            w.Close();
            return r;
        });
        Assert.True(faces);
        Assert.False(text);
    }

    [Fact]
    public void Background_options_describe_what_the_preview_shows()
    {
        var options = EditorHarness.Run(() =>
        {
            var w = new BackgroundWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.ChooseKind("blur");
            EditorHarness.WaitReady(w);
            var o = w.CurrentOptions;
            w.Close();
            return o;
        });
        Assert.Equal("blur", options!["background"]);
        Assert.Equal(true, options["shadow"]);
    }

    [Theory]
    [InlineData(QrModuleShape.Fluid, QrEyeShape.Leaf)]
    [InlineData(QrModuleShape.Dots, QrEyeShape.Circle)]
    public void The_qr_preview_is_scanned_live(QrModuleShape modules, QrEyeShape eyes)
    {
        var (scans, options) = EditorHarness.Run(() =>
        {
            var w = new QrWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.SetText("https://example.org/مرحبا");
            w.SetModules(modules);
            w.SetEyes(eyes);
            w.SetGradient("#0e7490");
            EditorHarness.WaitReady(w);
            var r = (w.LastScans, w.CurrentOptions);
            w.Close();
            return r;
        });
        Assert.True(scans);
        Assert.Equal("create", options!["mode"]);
        Assert.Equal("https://example.org/مرحبا", options["text"]);
        Assert.Equal(modules.ToString().ToLowerInvariant(), options["modules"]);
        Assert.Equal("#0e7490", options["color2"]);
    }

    [Fact]
    public void Qr_wifi_content_is_encoded_for_phones()
    {
        var options = EditorHarness.Run(() =>
        {
            var w = new QrWindow(EditorHarness.Kit("ar"), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.SetWifi("Home;Net", "pa:ss");
            EditorHarness.WaitReady(w);
            var o = w.CurrentOptions;
            w.Close();
            return o;
        });
        Assert.Equal(@"WIFI:T:WPA;S:Home\;Net;P:pa\:ss;;", options!["text"]);
    }

    [Fact]
    public void Annotate_saves_relative_items_and_text_as_an_overlay_picture()
    {
        var (json, units) = EditorHarness.Run(() =>
        {
            var w = new AnnotateWindow(EditorHarness.Kit("ar"), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            var doc = w.Document!;
            w.AddShape(new AnnotationShape("arrow", 0, 0, doc.Width / 2, doc.Height / 2, "#ef4444", 6));
            w.AddShape(new AnnotationShape("text", 10, 10, 10, 10, "#000000", 2, Text: "مرحبا", Size: 40));
            var o = w.CurrentOptions!;
            w.Close();
            return ((string)o["items"]!, (string)o["units"]!);
        });
        Assert.Equal("relative", units);
        var items = AnnotationRenderer.Parse(json);
        Assert.Equal(0.5, items[0].X2, 3);
        Assert.Equal("image", items[1].Type);
        Assert.True(File.Exists(items[1].Path), "the text overlay is written");
        Assert.True(items[1].W > 0 && items[1].H > 0);
    }

    [Fact]
    public void Metadata_changes_reach_the_converter_option()
    {
        var options = EditorHarness.Run(() =>
        {
            var w = new MetadataWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.Model!.Add(MetadataGroup.Xmp, "dc:title", "غروب");
            var o = w.CurrentOptions;
            w.Close();
            return o;
        });
        var changes = ImageMetadata.ParseChanges((string)options!["changes"]!);
        Assert.Equal([new MetadataChange(MetadataGroup.Xmp, "dc:title", "غروب")], changes);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Every_editor_opens_and_lays_out_in_both_directions(string language)
    {
        var problems = EditorHarness.Run(() =>
        {
            var found = new List<string>();
            foreach (var op in ImageEditors.Operations)
            {
                var w = ImageEditors.Create(EditorHarness.Kit(language), op, [_photo]);
                EditorHarness.Show(w);
                EditorHarness.WaitReady(w);
                if (w.FlowDirection != (language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight)) found.Add(op + ": direction");
                if (w.MessageText.Length > 0) found.Add(op + ": " + w.MessageText);
                w.Close();
            }
            return found;
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Closing_an_editor_without_saving_starts_nothing()
    {
        var result = EditorHarness.Run(() =>
        {
            var w = new CropWindow(EditorHarness.Kit(), [_photo]);
            EditorHarness.Show(w);
            EditorHarness.WaitReady(w);
            w.Close();
            return w.Result.IsCompleted ? w.Result.Result : new Dictionary<string, object?> { ["not"] = "completed" };
        });
        Assert.Null(result);
    }

    private sealed class FakeDetectors(bool faces, bool text) : IRedactDetectors
    {
        public bool FacesAvailable => faces;
        public Task<bool> TextAvailableAsync() => Task.FromResult(text);
        public Task<IReadOnlyList<Box>> FacesAsync(string path, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Box>>([new Box(1, 2, 3, 4)]);
        public Task<IReadOnlyList<Box>> TextAsync(string path, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Box>>([]);
    }
}

public sealed class WelcomeWindowTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private WelcomeWindow Create(string language, Action<string, bool>? apply = null) =>
        new(EditorHarness.Kit(language), new WelcomeContext(PracticePhoto.Ensure(_dir.Path), new WheelKeys(), "system", false, "Ctrl+Alt+V", false, apply ?? ((_, _) => { })));

    [Fact]
    public void The_practice_photo_is_a_real_picture_in_the_data_folder()
    {
        var path = PracticePhoto.Ensure(_dir.Path);
        Assert.Equal(PracticePhoto.FileName, Path.GetFileName(path));
        using var image = new ImageMagick.MagickImage(path);
        Assert.Equal((1200u, 800u), (image.Width, image.Height));
        Assert.Equal(path, PracticePhoto.Ensure(_dir.Path));   // kept, not redrawn
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void All_four_pages_open_in_both_directions(string language)
    {
        var pages = EditorHarness.Run(() =>
        {
            var w = Create(language);
            EditorHarness.Show(w);
            var seen = new List<int>();
            for (var i = 0; i < WelcomeWindow.PageCount; i++) { w.GoTo(i); w.UpdateLayout(); seen.Add(w.Page); }
            Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, w.FlowDirection);
            w.Close();
            return seen;
        });
        Assert.Equal([0, 1, 2, 3], pages);
    }

    [Fact]
    public void Keycaps_light_up_while_held_and_the_wheel_tick_appears()
    {
        var (shift, ctrl, opened) = EditorHarness.Run(() =>
        {
            var w = Create("en");
            EditorHarness.Show(w);
            w.GoTo(1);
            w.ShowKeys(shift: true, ctrl: false, alt: false);
            w.MarkWheelOpened();
            var r = (w.IsLit(TriggerKey.Shift), w.IsLit(TriggerKey.Ctrl), w.WheelOpened);
            w.Close();
            return r;
        });
        Assert.True(shift);
        Assert.False(ctrl);
        Assert.True(opened);
    }
}
