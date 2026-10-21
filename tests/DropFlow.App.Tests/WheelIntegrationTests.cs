using System.Windows.Threading;
using DropFlow.AI.Models;
using DropFlow.App.Services;
using DropFlow.Core.Catalog;
using DropFlow.Core.Registry;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;
using DropFlow.TestSupport;

namespace DropFlow.App.Tests;

/// <summary>What the wheels actually offer for common selections, built from the real converter list.</summary>
public sealed class WheelIntegrationTests : IDisposable
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();
    private readonly TempDir _dir = new();
    private readonly ModelManager _models;
    private readonly WheelBuilder _builder;

    public WheelIntegrationTests()
    {
        _models = new ModelManager(_dir.Combine("models"));
        var registry = new ToolRegistry().Register(
            Modules.Create(TestSupport.Engines.Locator, Dispatcher.CurrentDispatcher, _dir.Combine("web"), _ => Task.CompletedTask, _models));
        _builder = new WheelBuilder(Catalog, registry, TestSupport.Engines.Locator);
    }

    public void Dispose()
    {
        _models.Dispose();
        _dir.Dispose();
    }

    private WheelSet Build(params string[] files) => _builder.Build(files, "en", ["act.share", "act.float", "act.copy", "act.move", "act.copy-path", "act.terminal", "act.send-phone"]);

    private static void AssertHas(IEnumerable<string> actual, params string[] expected)
    {
        var missing = expected.Except(actual).ToList();
        Assert.True(missing.Count == 0, "missing: " + string.Join(", ", missing) + "\nhave: " + string.Join(", ", actual));
    }

    [Fact]
    public void A_photo_gets_conversions_image_tools_ai_tools_and_general_tools()
    {
        var set = Build(@"C:\p\photo.png");

        AssertHas(set.Convert.Select(w => w.Label), "JPG", "WEBP", "AVIF", "PDF", "ICO");
        AssertHas(set.Tools.Select(w => w.Id),
            "img.compress", "img.resize", "img.crop", "img.metadata", "img.strip-gps", "img.ocr", "img.remove-bg", "img.redact-auto",
            "img.qr", "img.icons", "img.edit", "img.annotate", "arc.zip", "gen.hash", "gen.base64");
        Assert.DoesNotContain(set.Convert, w => w.Label == "PNG"); // never its own format
    }

    [Fact]
    public void Several_photos_also_get_the_multi_file_tools()
    {
        var set = Build(@"C:\p\a.png", @"C:\p\b.jpg");
        AssertHas(set.Tools.Select(w => w.Id), "img.collage", "img.to-pdf", "gen.rename", "arc.zip");
    }

    [Fact]
    public void A_video_gets_the_encoder_conversions_and_speech_to_text()
    {
        var set = Build(@"C:\v\clip.mp4");

        AssertHas(set.Convert.Select(w => w.Label), "MKV", "WEBM", "MP3", "GIF");
        AssertHas(set.Tools.Select(w => w.Id), "vid.encoder", "vid.compress", "vid.trim", "vid.crop", "vid.speed", "vid.snapshot", "vid.split", "vid.transcribe", "vid.burn-subs", "arc.zip");
    }

    [Fact]
    public void Audio_gets_normalize_bleep_visualize_and_speech_to_text()
    {
        var set = Build(@"C:\a\song.mp3");
        AssertHas(set.Tools.Select(w => w.Id), "aud.normalize", "aud.trim", "aud.channels", "aud.visualize", "aud.bleep", "aud.transcribe");
    }

    [Fact]
    public void A_pdf_gets_the_pdf_suite()
    {
        var set = Build(@"C:\d\report.pdf");

        AssertHas(set.Convert.Select(w => w.Label), "DOCX", "PPTX", "XLSX", "JPG", "PNG", "EPUB", "TXT", "MD");
        AssertHas(set.Tools.Select(w => w.Id),
            "pdf.compress", "pdf.split", "pdf.rotate", "pdf.organize", "pdf.protect", "pdf.unlock", "pdf.watermark", "pdf.page-numbers", "pdf.metadata", "pdf.ocr");
        Assert.DoesNotContain(set.Tools, w => w.Id == "pdf.merge"); // needs two files
    }

    [Fact]
    public void Two_pdfs_can_be_merged()
    {
        var set = Build(@"C:\d\a.pdf", @"C:\d\b.pdf");
        AssertHas(set.Tools.Select(w => w.Id), "pdf.merge");
    }

    [Fact]
    public void A_word_document_converts_to_pdf_and_an_html_page_too()
    {
        AssertHas(Build(@"C:\d\letter.docx").Convert.Select(w => w.Label), "PDF", "TXT", "HTML");
        AssertHas(Build(@"C:\d\page.html").Convert.Select(w => w.Label), "PDF");
        AssertHas(Build(@"C:\d\page.html").Tools.Select(w => w.Id), "pdf.html-to-pdf");
    }

    [Fact]
    public void An_archive_can_be_extracted_and_a_folder_zipped()
    {
        AssertHas(Build(@"C:\a\pack.zip").Tools.Select(w => w.Id), "arc.extract");

        var folder = Build(_dir.Path); // a real directory: no extension, no category
        AssertHas(folder.Tools.Select(w => w.Id), "arc.zip");
        Assert.Empty(folder.Convert);
    }

    [Fact]
    public void Subtitles_convert_between_formats()
    {
        AssertHas(Build(@"C:\s\movie.srt").Convert.Select(w => w.Label), "VTT", "ASS", "TXT");
    }

    [Fact]
    public void Add_on_the_convert_wheel_of_a_photo_offers_ocr_and_pinning_it_puts_it_on_that_wheel()
    {
        string[] actions = ["act.share", "act.copy", "act.send-phone"];
        var set = _builder.Build([@"C:\p\photo.png"], "en", actions, WheelPins.None);

        Assert.True(set.Convert[^1].IsAdd);
        var addable = set.AddableFor(WheelKind.Convert);
        AssertHas(addable.Select(w => w.Id), "img.ocr", "img.remove-bg", "img.compress", "act.send-phone");
        Assert.DoesNotContain(addable, w => w.Id.StartsWith("convert:", StringComparison.Ordinal));   // all already on this wheel
        Assert.Equal("Extract text (OCR)", addable.Single(w => w.Id == "img.ocr").Label);

        var pinned = _builder.Build([@"C:\p\photo.png"], "en", actions, WheelPins.None.With(WheelKind.Convert, "image", "img.ocr"));
        Assert.Equal("img.ocr", pinned.Convert[^2].Id);
        Assert.True(pinned.Convert[^2].Pinned);
        Assert.DoesNotContain(_builder.Build([@"C:\d\report.pdf"], "en", actions, WheelPins.None.With(WheelKind.Convert, "image", "img.ocr")).Convert,
            w => w.Id == "img.ocr");
    }

    [Fact]
    public void Every_wheel_ends_with_add_for_real_selections()
    {
        foreach (var files in new[] { new[] { @"C:\p\photo.png" }, [@"C:\d\report.pdf"], [@"C:\v\clip.mp4"], [@"C:\p\a.png", @"C:\d\b.pdf"] })
        {
            var set = _builder.Build(files, "en", ["act.copy"], WheelPins.None);
            foreach (var kind in Enum.GetValues<WheelKind>())
                Assert.True(set.Of(kind) is [.., { IsAdd: true }], $"{kind} for {string.Join(",", files)}");
        }
    }

    [Fact]
    public void Every_selection_gets_the_phone_action_and_the_other_actions()
    {
        AssertHas(Build(@"C:\p\photo.png").Actions.Select(w => w.Id), "act.send-phone", "act.copy", "act.share", "act.float", "act.terminal");
    }
}
