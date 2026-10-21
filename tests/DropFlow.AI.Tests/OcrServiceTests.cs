using System.Text.Json;
using DropFlow.AI.Ocr;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;
using Xunit;

namespace DropFlow.AI.Tests;

public class OcrServiceTests
{
    private sealed class FakeLanguages(params string[] tags) : IWindowsOcrLanguageSource
    {
        public IReadOnlyList<string> InstalledTags() => tags;
    }

    private sealed class FakeWindowsOcr(Func<string, string?, OcrResult> answer) : IWindowsOcr
    {
        public List<(string Path, string? Tag)> Calls { get; } = [];

        public Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken cancellationToken)
        {
            Calls.Add((pngPath, languageTag));
            return Task.FromResult(answer(pngPath, languageTag));
        }
    }

    private sealed class FakeTesseractOcr(bool installed, string[] languages, OcrResult? result = null) : ITesseractOcr
    {
        public bool IsInstalled => installed;
        public List<string> RecognisedWith { get; } = [];
        public Task<IReadOnlyList<string>> LanguagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(languages);

        public Task<OcrResult> RecognizeAsync(string pngPath, IReadOnlyList<string> langs, CancellationToken ct)
        {
            RecognisedWith.Add(string.Join('+', langs));
            return Task.FromResult(result ?? Line("tesseract", "text", 0, 0, 10, 10, 8, 8));
        }
    }

    private static OcrResult Line(string engine, string text, double x, double y, double w, double h, int imageW, int imageH) =>
        new(engine, "en-US", imageW, imageH, [new OcrLine(text, [new OcrWord(text, new OcrRect(x, y, w, h))], new OcrRect(x, y, w, h), false)]);

    [Fact]
    public async Task Rectangles_from_a_downscaled_image_are_mapped_back_to_original_pixels()
    {
        using var dir = new TempDir();
        var wide = dir.Combine("wide.png");
        using (var img = new MagickImage(MagickColors.White, 9000, 100)) img.Write(wide);   // wider than the 8000 px limit
        var windows = new FakeWindowsOcr((path, _) =>
        {
            using var prepared = new MagickImage(path);
            Assert.Equal(8000u, prepared.Width);           // the engine got the shrunken copy
            return Line("windows", "x", 800, 10, 400, 40, (int)prepared.Width, (int)prepared.Height);
        });
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")), windows, new FakeTesseractOcr(false, []));

        var result = await svc.RecognizeAsync(wide, new OcrRequest("en"), default);

        Assert.Equal(9000, result.ImageWidth);
        Assert.Equal(100, result.ImageHeight);
        var box = result.Words.Single().Bounds;
        Assert.Equal(900, box.X, 3);                  // 800 * 9000/8000
        Assert.Equal(450, box.Width, 3);
        Assert.Equal(11.25, box.Y, 3);
    }

    [Fact]
    public async Task Rotated_phone_photos_report_rectangles_in_upright_coordinates()
    {
        using var dir = new TempDir();
        var path = TestImages.JpegRotated90(dir.Combine("rot.jpg"), 200, 100, MagickColors.White);
        var windows = new FakeWindowsOcr((p, _) => { using var i = new MagickImage(p); return Line("windows", "x", 1, 1, 5, 5, (int)i.Width, (int)i.Height); });
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")), windows, new FakeTesseractOcr(false, []));

        var result = await svc.RecognizeAsync(path, new OcrRequest("en"), default);
        Assert.Equal(100, result.ImageWidth);   // upright: the 200x100 sensor image is shown 100x200
        Assert.Equal(200, result.ImageHeight);
    }

    [Fact]
    public async Task Windows_is_used_first_and_the_chosen_pack_is_passed_to_it()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("a.png"));
        var windows = new FakeWindowsOcr((_, _) => Line("windows", "hello", 0, 0, 10, 10, 64, 48));
        var tess = new FakeTesseractOcr(true, ["eng", "ara"]);
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US", "ar-EG")), windows, tess);

        await svc.RecognizeAsync(png, new OcrRequest("ar"), default);
        Assert.Equal("ar-EG", windows.Calls.Single().Tag);
        Assert.Empty(tess.RecognisedWith);
        Assert.StartsWith(Path.GetTempPath(), windows.Calls[0].Path); // engines only ever see the prepared copy
    }

    [Fact]
    public async Task Missing_windows_language_falls_back_to_tesseract_with_both_languages()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("a.png"));
        var windows = new FakeWindowsOcr((_, _) => throw new InvalidOperationException("windows must not be called"));
        var tess = new FakeTesseractOcr(true, ["eng", "ara", "osd"]);
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")), windows, tess);

        var result = await svc.RecognizeAsync(png, new OcrRequest("ar+en"), default);
        Assert.Equal(["ara+eng"], tess.RecognisedWith);
        Assert.Equal("tesseract", result.Engine);
        Assert.Empty(windows.Calls);
    }

    [Fact]
    public async Task Unavailable_language_throws_with_actionable_text()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("a.png"));
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")),
            new FakeWindowsOcr((_, _) => throw new InvalidOperationException()), new FakeTesseractOcr(false, []));

        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(() => svc.RecognizeAsync(png, new OcrRequest("ar"), default));
        Assert.Contains("Language.OCR~~~ar-SA~0.0.1.0", ex.Message);
        var unknown = await Assert.ThrowsAsync<OcrUnavailableException>(() => svc.RecognizeAsync(png, new OcrRequest("klingon"), default));
        Assert.Contains("klingon", unknown.Message);
    }

    [Fact]
    public async Task Unreadable_images_are_reported_not_thrown_as_crashes()
    {
        using var dir = new TempDir();
        var junk = dir.WriteText("broken.png", "not an image");
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")), new FakeWindowsOcr((_, _) => throw new InvalidOperationException()), new FakeTesseractOcr(false, []));
        await Assert.ThrowsAsync<OcrUnavailableException>(() => svc.RecognizeAsync(junk, new OcrRequest("en"), default));
    }

    // ---- the real Windows OCR engine ------------------------------------------------------------------------------

    [OcrFact("en")]
    public async Task Windows_ocr_reads_text_rendered_with_gdi()
    {
        using var dir = new TempDir();
        var image = TextImage.Render(dir.Combine("hello.png"), "Hello World 2026", fontSize: 56, width: 900, height: 200);
        var svc = new OcrService(new FakeEngines());

        var result = await svc.RecognizeAsync(image.Path, new OcrRequest("en", OcrEnginePreference.Windows), default);

        Assert.Equal("windows", result.Engine);
        Assert.StartsWith("en", result.Language);
        Assert.Equal(900, result.ImageWidth);
        Assert.Equal(200, result.ImageHeight);
        var texts = result.Words.Select(w => w.Text.ToLowerInvariant()).ToList();
        Assert.Contains("hello", texts);
        Assert.Contains("world", texts);
        Assert.Contains("2026", texts);
        Assert.Equal("Hello World 2026", result.Text, ignoreCase: true);

        // each recognised word sits where GDI+ drew it
        foreach (var (word, drawn) in image.Words)
        {
            var found = result.Words.Single(w => w.Text.Equals(word, StringComparison.OrdinalIgnoreCase));
            Assert.InRange(Math.Abs(found.Bounds.CenterX - (drawn.X + drawn.Width / 2)), 0, 40);
            Assert.InRange(Math.Abs(found.Bounds.CenterY - (drawn.Y + drawn.Height / 2)), 0, 40);
            Assert.InRange(found.Bounds.Width, drawn.Width * 0.6, drawn.Width * 1.3);
        }
        Assert.All(result.Words, w =>
        {
            Assert.InRange(w.Bounds.X, 0, 900);
            Assert.InRange(w.Bounds.Right, 0, 900);
            Assert.InRange(w.Bounds.Bottom, 0, 200);
        });
    }

    [OcrFact("en")]
    public async Task Windows_ocr_keeps_lines_in_order()
    {
        using var dir = new TempDir();
        var image = TextImage.Render(dir.Combine("two.png"), "Invoice 1042\nTotal due 250 USD", fontSize: 52, width: 900, height: 260);
        var result = await new OcrService(new FakeEngines()).RecognizeAsync(image.Path, new OcrRequest("en"), default);

        Assert.Equal(2, result.Lines.Count);
        Assert.Contains("Invoice", result.Lines[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Total", result.Lines[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Lines[0].Bounds.Y < result.Lines[1].Bounds.Y);
    }

    [OcrFact("en")]
    public async Task The_converter_writes_a_text_file_beside_an_image_with_an_arabic_name_and_never_overwrites()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("صور ممسوحة");
        Directory.CreateDirectory(folder);
        var image = TextImage.Render(Path.Combine(folder, "فاتورة.png"), "Total 250 USD", fontSize: 56);
        var before = File.ReadAllBytes(image.Path);
        var converter = new OcrConverter(new OcrService(new FakeEngines()));
        var job = new ConversionJob([image.Path], "img.ocr", new Dictionary<string, object?> { ["language"] = "en" });

        var first = await converter.RunAsync(job, new Progress<JobProgress>(), default);
        Assert.True(first.Success, first.Error);
        Assert.Equal(Path.Combine(folder, "فاتورة (ocr).txt"), first.Outputs.Single());
        Assert.Contains("250", File.ReadAllText(first.Outputs[0]));

        var second = await converter.RunAsync(job, new Progress<JobProgress>(), default);
        Assert.Equal(Path.Combine(folder, "فاتورة (ocr 2).txt"), second.Outputs.Single());
        Assert.Equal(before, File.ReadAllBytes(image.Path));
    }

    [OcrFact("en")]
    public async Task The_converter_can_also_write_word_rectangles_as_json()
    {
        using var dir = new TempDir();
        var image = TextImage.Render(dir.Combine("pic.png"), "Hello World", fontSize: 56);
        var converter = new OcrConverter(new OcrService(new FakeEngines()));

        var result = await converter.RunAsync(new ConversionJob([image.Path], "img.ocr", new Dictionary<string, object?> { ["outputs"] = "txt,json" }), new Progress<JobProgress>(), default);

        Assert.Equal(2, result.Outputs.Count);
        Assert.EndsWith("(ocr).txt", result.Outputs[0]);
        using var doc = JsonDocument.Parse(File.ReadAllText(result.Outputs[1]));
        var words = doc.RootElement.GetProperty("lines").EnumerateArray().SelectMany(l => l.GetProperty("words").EnumerateArray()).ToList();
        Assert.Contains(words, w => w.GetProperty("text").GetString()!.Equals("Hello", StringComparison.OrdinalIgnoreCase));
        Assert.All(words, w => Assert.Equal(4, w.GetProperty("bounds").GetArrayLength()));
    }

    [OcrFact("en")]
    public async Task An_image_without_text_is_a_clear_failure_not_an_empty_file()
    {
        using var dir = new TempDir();
        var blank = dir.Combine("blank.png");
        using (var img = new MagickImage(MagickColors.White, 400, 200)) img.Write(blank);

        var result = await new OcrConverter(new OcrService(new FakeEngines())).RunAsync(
            new ConversionJob([blank], "img.ocr", new Dictionary<string, object?> { ["language"] = "en" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("No text", result.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(ocr*"));
    }

    [OcrFact("ar")]
    public async Task Windows_ocr_reads_arabic_and_returns_it_in_logical_order()
    {
        using var dir = new TempDir();
        const string sentence = "مرحبا بالعالم الجميل";
        var image = TextImage.Render(dir.Combine("ar.png"), sentence, fontSize: 64, rtl: true);

        var result = await new OcrService(new FakeEngines()).RecognizeAsync(image.Path, new OcrRequest("ar", OcrEnginePreference.Windows), default);

        Assert.True(result.Lines[0].IsRightToLeft);
        Assert.Equal(sentence, result.Lines[0].Text);
        Assert.True(result.Lines[0].Words[0].Bounds.X > result.Lines[0].Words[^1].Bounds.X); // first word is the rightmost
    }

    // ---- converter behaviour with fakes (always runs) ---------------------------------------------------------------

    [Fact]
    public async Task Arabic_without_a_pack_or_tesseract_fails_naming_the_exact_command()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("scan.png"));
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")),
            new FakeWindowsOcr((_, _) => throw new InvalidOperationException()), new FakeTesseractOcr(false, []));

        var result = await new OcrConverter(svc).RunAsync(
            new ConversionJob([png], "img.ocr", new Dictionary<string, object?> { ["language"] = "ar" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("Add-WindowsCapability -Online -Name Language.OCR~~~ar-SA~0.0.1.0", result.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(ocr*"));
    }

    [Fact]
    public async Task The_converter_writes_recognised_text_from_any_engine()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("scan.png"));
        var windows = new FakeWindowsOcr((_, _) => Line("windows", "Pay 100", 5, 5, 40, 10, 64, 48));
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")), windows, new FakeTesseractOcr(false, []));

        var result = await new OcrConverter(svc).RunAsync(new ConversionJob([png], "img.ocr"), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Pay 100" + Environment.NewLine, File.ReadAllText(result.Outputs[0]));
    }

    [Fact]
    public async Task Cancellation_is_honoured_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("scan.png"));
        using var cts = new CancellationTokenSource();
        // the fake engine cancels from inside its own call; nothing may be written afterwards
        var svc = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages("en-US")),
            new CancellingWindowsOcr(cts), new FakeTesseractOcr(false, []));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OcrConverter(svc).RunAsync(new ConversionJob([png], "img.ocr"), new Progress<JobProgress>(), cts.Token));
        Assert.Empty(Directory.GetFiles(dir.Path, "*(ocr*"));
    }

    private sealed class CancellingWindowsOcr(CancellationTokenSource cts) : IWindowsOcr
    {
        public Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<OcrResult>(null!);
        }
    }

    // ---- TesseractOcr + converters through a fake tesseract executable --------------------------------------------

    private const string ArabicTsv =
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        "1\t1\t0\t0\t0\t0\t0\t0\t64\t48\t-1\t\n" +
        "5\t1\t1\t1\t1\t1\t30\t5\t30\t12\t96.5\tمرحبا\n" +
        "5\t1\t1\t1\t1\t2\t2\t5\t25\t12\t91.2\tبالعالم\n";

    [Fact]
    public async Task Tesseract_process_plumbing_works_end_to_end_with_a_stand_in_executable()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir, ArabicTsv);
        var png = Samples.Png(dir.Combine("scan.png"));
        var svc = new OcrService(fake.Engines, new OcrLanguageService(new FakeLanguages("en-US")));   // real TesseractOcr, real ProcessRunner

        var caps = await svc.GetCapabilitiesAsync(default);
        Assert.True(caps.TesseractInstalled);
        Assert.Equal(["ara", "eng", "osd"], caps.TesseractLanguages);

        var result = await new OcrConverter(svc).RunAsync(
            new ConversionJob([png], "img.ocr", new Dictionary<string, object?> { ["language"] = "ar+en", ["outputs"] = "txt,json" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("مرحبا بالعالم" + Environment.NewLine, File.ReadAllText(result.Outputs[0]));   // logical order, rightmost first
        var call = Assert.Single(fake.Calls);
        Assert.Contains("-l ara+eng", call);
        Assert.EndsWith("tsv", call);
        Assert.Contains("input.png", call);
        using var doc = JsonDocument.Parse(File.ReadAllText(result.Outputs[1]));
        Assert.Equal("tesseract", doc.RootElement.GetProperty("engine").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("imageWidth").GetInt32());
    }

    [Fact]
    public async Task A_failing_tesseract_run_is_reported_with_its_message()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir, "");
        File.WriteAllText(fake.ScriptPath, "@echo off\r\nif \"%~1\"==\"--list-langs\" (echo eng& exit /b 0)\r\necho Error opening data file 1>&2\r\nexit /b 1\r\n");
        var png = Samples.Png(dir.Combine("scan.png"));
        var svc = new OcrService(fake.Engines, new OcrLanguageService(new FakeLanguages()));

        var result = await new OcrConverter(svc).RunAsync(new ConversionJob([png], "img.ocr", new Dictionary<string, object?> { ["language"] = "en" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("Error opening data file", result.Error);
    }

    // ---- real Tesseract (skipped unless installed) ---------------------------------------------------------------------

    [EngineFact(EngineId.Tesseract)]
    public async Task Real_tesseract_reads_rendered_english_text_with_rectangles()
    {
        using var dir = new TempDir();
        var image = TextImage.Render(dir.Combine("hello.png"), "Hello World 2026", fontSize: 56);
        var svc = new OcrService(DropFlow.TestSupport.Engines.Locator, new OcrLanguageService(new FakeLanguages()));

        var result = await svc.RecognizeAsync(image.Path, new OcrRequest("en", OcrEnginePreference.Tesseract), default);

        Assert.Equal("tesseract", result.Engine);
        var texts = result.Words.Select(w => w.Text.ToLowerInvariant()).ToList();
        Assert.Contains("hello", texts);
        Assert.Contains("world", texts);
        Assert.All(result.Words, w => Assert.InRange(w.Bounds.Right, 0, 900));
    }

    [EngineFact(EngineId.Tesseract)]
    public async Task Real_tesseract_works_when_the_image_path_has_arabic_characters()
    {
        using var dir = new TempDir();
        var image = TextImage.Render(dir.Combine("صورة.png"), "Total 250", fontSize: 56);
        var result = await new OcrConverter(new OcrService(DropFlow.TestSupport.Engines.Locator, new OcrLanguageService(new FakeLanguages()))).RunAsync(
            new ConversionJob([image.Path], "img.ocr", new Dictionary<string, object?> { ["language"] = "en", ["engine"] = "tesseract" }), new Progress<JobProgress>(), default);
        Assert.True(result.Success, result.Error);
        Assert.Contains("250", File.ReadAllText(result.Outputs[0]));
    }

    [Fact]
    public void Operations_options_engine_and_supports_are_truthful()
    {
        var c = new OcrConverter(new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages()), new FakeWindowsOcr((_, _) => null!), new FakeTesseractOcr(false, [])));
        var catalog = ToolCatalog.LoadEmbedded();

        Assert.Equal(["img.ocr"], c.Operations);
        Assert.NotNull(catalog.Tool("img.ocr"));
        Assert.Equal(EngineId.None, c.EngineFor("img.ocr"));
        foreach (var ext in new[] { "png", "jpg", "webp", "tiff", "bmp", "gif", "avif", "ico", "svg" })
            Assert.True(c.Supports("img.ocr", ext), ext);
        foreach (var ext in new[] { "mp3", "pdf", "docx", "txt", "mp4", "zip", "" })
            Assert.False(c.Supports("img.ocr", ext), ext);
        Assert.False(c.Supports("aud.transcribe", "png"));
        Assert.True(c.Supports("img.ocr", "JPG"));

        var options = c.OptionsFor("img.ocr").ToDictionary(o => o.Key);
        Assert.Equal(["engine", "language", "outputs"], options.Keys.Order());
        Assert.Equal(["auto", "en", "ar", "ar+en"], options["language"].Choices);
        Assert.Equal(["auto", "windows", "tesseract"], options["engine"].Choices);
        Assert.Equal(OcrEnginePreference.Tesseract, OcrConverter.ParseEngine("Tesseract"));
        Assert.Equal(OcrEnginePreference.Auto, OcrConverter.ParseEngine("???"));
    }
}
