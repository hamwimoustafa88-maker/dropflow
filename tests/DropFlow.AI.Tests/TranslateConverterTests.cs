using System.Text;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.AI.Translation;
using DropFlow.AI.Translation.Rendering;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>A translator that needs no model: it wraps every string that would be translated in a marker, and records what it was given.</summary>
internal sealed class FakeTranslator : ITextTranslator
{
    public HashSet<TranslationDirection> Ready { get; } = [TranslationDirection.EnglishToArabic, TranslationDirection.ArabicToEnglish];
    public List<(TranslationDirection Direction, IReadOnlyList<string> Texts)> Calls { get; } = [];
    public TaskCompletionSource? Gate { get; set; }
    public Func<TranslationDirection, string, string>? Translate { get; set; }

    public string? MissingModel(TranslationDirection direction) => Ready.Contains(direction) ? null : TranslationInfo.ModelId(direction);

    public string? NotReadyMessage(TranslationDirection direction) =>
        MissingModel(direction) is { } id ? $"Model '{id}' is not installed (Opus-MT, 108 MB). Download it from the model manager first." : null;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        TranslationDirection direction, IReadOnlyList<string> texts, TranslationQuality quality, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (NotReadyMessage(direction) is { } message) throw new TranslationUnavailableException(message);
        Calls.Add((direction, texts));
        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
        progress?.Report(0.5);
        return [.. texts.Select(t => ScriptDetector.NeedsTranslation(t, direction)
            ? (Translate?.Invoke(direction, t) ?? (direction == TranslationDirection.EnglishToArabic ? "ع<" + t + ">" : "en<" + t + ">"))
            : t)];
    }
}

public class TranslateConverterTests
{
    private static TextTranslateConverter Converter(FakeTranslator t, TimeSpan? timeout = null) => new(t, timeout);

    private static Task<ConversionResult> Run(IConverter c, string file, Dictionary<string, object?>? options = null, string? outDir = null, IProgress<JobProgress>? progress = null, CancellationToken ct = default) =>
        c.RunAsync(new ConversionJob([file], TextTranslateConverter.Operation, options, outDir), progress ?? new Progress<JobProgress>(), ct);

    private const string EnglishSrt = "1\n00:00:01,000 --> 00:00:03,000\nHello there\n\n2\n00:00:04,000 --> 00:00:06,000\nSee you soon\n";

    [Fact]
    public void Supports_only_text_markdown_html_and_subtitle_files()
    {
        var c = Converter(new FakeTranslator());
        foreach (var ext in new[] { "txt", "md", "srt", "vtt", "html", "SRT", ".txt" }) Assert.True(c.Supports("txt.translate", ext), ext);
        foreach (var ext in new[] { "docx", "pdf", "png", "ass", "mp4", "" }) Assert.False(c.Supports("txt.translate", ext), ext);
        Assert.False(c.Supports("img.translate", "txt"));
        Assert.Equal(EngineId.None, c.EngineFor("txt.translate"));
        Assert.Equal(["direction", "quality"], c.OptionsFor("txt.translate").Select(o => o.Key));
    }

    [Fact]
    public async Task A_subtitle_file_is_translated_beside_the_original_and_the_original_is_not_touched()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("movie.srt", EnglishSrt);
        var fake = new FakeTranslator();

        var result = await Run(Converter(fake), path);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("movie (translated).srt"), output);
        Assert.Equal(EnglishSrt, File.ReadAllText(path));                       // the original is untouched
        Assert.Equal("1\n00:00:01,000 --> 00:00:03,000\nع<Hello there>\n\n2\n00:00:04,000 --> 00:00:06,000\nع<See you soon>\n", File.ReadAllText(output));
        Assert.Equal(TranslationDirection.EnglishToArabic, fake.Calls.Single().Direction);
        Assert.Equal("note.translate.detectedEnAr", result.Note);
    }

    [Fact]
    public async Task Arabic_subtitles_go_to_english_when_the_direction_is_left_on_auto()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("film.srt", "1\n00:00:01,000 --> 00:00:03,000\nمرحباً بك\n");
        var fake = new FakeTranslator();
        var result = await Run(Converter(fake), path);
        Assert.True(result.Success, result.Error);
        Assert.Contains("en<مرحباً بك>", File.ReadAllText(result.Outputs[0]));
        Assert.Equal("note.translate.detectedArEn", result.Note);
    }

    [Fact]
    public async Task An_explicit_direction_is_used_and_needs_no_detection_note()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("a.txt", "Hello\n");
        var fake = new FakeTranslator();
        var result = await Run(Converter(fake), path, new() { ["direction"] = "en-ar" });
        Assert.True(result.Success, result.Error);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task Text_that_is_already_in_the_target_language_is_kept_and_counted()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("mixed.txt", "Hello there\nمرحباً بك\nGood bye\n");
        var result = await Run(Converter(new FakeTranslator()), path, new() { ["direction"] = "en-ar" });
        Assert.True(result.Success, result.Error);
        Assert.Equal("ع<Hello there>\nمرحباً بك\nع<Good bye>\n", File.ReadAllText(result.Outputs[0]));
        Assert.Equal("note.translate.skipped|1", result.Note);
    }

    [Fact]
    public async Task Nothing_is_written_when_the_file_has_no_text_in_the_source_language()
    {
        using var dir = new TempDir();
        var arabicOnly = dir.WriteText("ar.txt", "مرحباً بك\n");
        var r1 = await Run(Converter(new FakeTranslator()), arabicOnly, new() { ["direction"] = "en-ar" });
        Assert.False(r1.Success);
        Assert.Contains("No English text", r1.Error);

        var numbers = dir.WriteText("n.txt", "12345 ... 6789\n");
        var r2 = await Run(Converter(new FakeTranslator()), numbers);
        Assert.False(r2.Success);
        Assert.Contains("no English or Arabic text", r2.Error!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(["ar.txt", "n.txt"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_missing_model_fails_with_the_message_the_app_turns_into_a_download_offer()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("a.txt", "Hello there\n");
        var fake = new FakeTranslator();
        fake.Ready.Remove(TranslationDirection.EnglishToArabic);

        var result = await Run(Converter(fake), path);

        Assert.False(result.Success);
        Assert.Equal("opus-mt-en-ar", AiFailures.TryParseMissingModel(result.Error));
        Assert.Single(Directory.GetFiles(dir.Path));                            // no output was created
    }

    [Fact]
    public async Task Running_twice_never_overwrites_an_earlier_translation()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("a.txt", "Hello there\n");
        var first = await Run(Converter(new FakeTranslator()), path);
        var second = await Run(Converter(new FakeTranslator()), path);

        Assert.True(first.Success && second.Success);
        Assert.NotEqual(first.Outputs[0], second.Outputs[0]);
        Assert.True(File.Exists(first.Outputs[0]) && File.Exists(second.Outputs[0]));
        Assert.Equal(dir.Combine("a (translated).txt"), first.Outputs[0]);
    }

    [Fact]
    public async Task Arabic_file_names_folders_with_spaces_and_the_output_folder_option_work()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("مجلد الاختبار 1")).FullName;
        var path = Path.Combine(folder, "ترجمة الفيلم.srt");
        File.WriteAllText(path, EnglishSrt, new UTF8Encoding(false));

        var beside = await Run(Converter(new FakeTranslator()), path);
        Assert.Equal(Path.Combine(folder, "ترجمة الفيلم (translated).srt"), beside.Outputs[0]);

        var elsewhere = Directory.CreateDirectory(dir.Combine("out")).FullName;
        var moved = await Run(Converter(new FakeTranslator()), path, outDir: elsewhere);
        Assert.Equal(Path.Combine(elsewhere, "ترجمة الفيلم (translated).srt"), moved.Outputs[0]);
    }

    [Fact]
    public async Task A_utf8_bom_and_windows_line_breaks_of_the_source_are_kept_and_the_output_is_utf8()
    {
        using var dir = new TempDir();
        var path = dir.Combine("crlf.txt");
        File.WriteAllBytes(path, [.. new UTF8Encoding(true).GetPreamble(), .. new UTF8Encoding(false).GetBytes("Hello there\r\nSee you\r\n")]);

        var result = await Run(Converter(new FakeTranslator()), path);

        var bytes = File.ReadAllBytes(result.Outputs[0]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("ع<Hello there>\r\nع<See you>\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task A_file_in_the_windows_arabic_code_page_is_read_correctly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var dir = new TempDir();
        var path = dir.Combine("legacy.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1256).GetBytes("مرحباً بك في المتجر\n"));

        var result = await Run(Converter(new FakeTranslator()), path);

        Assert.True(result.Success, result.Error);
        Assert.Equal("en<مرحباً بك في المتجر>\n", File.ReadAllText(result.Outputs[0]));
    }

    [Fact]
    public async Task Cancelling_stops_the_job_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("a.txt", "Hello there\n");
        var fake = new FakeTranslator { Gate = new TaskCompletionSource() };
        using var cts = new CancellationTokenSource();

        var task = Run(Converter(fake), path, ct: cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task A_translation_that_takes_too_long_is_stopped_with_a_clear_message()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("a.txt", "Hello there\n");
        var fake = new FakeTranslator { Gate = new TaskCompletionSource() };      // never completes

        var result = await Run(Converter(fake, TimeSpan.FromMilliseconds(150)), path);

        Assert.False(result.Success);
        Assert.Contains("took longer", result.Error);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Progress_goes_up_to_one_and_never_backwards()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("a.srt", EnglishSrt);
        var seen = new List<double>();
        var progress = new InlineProgress<JobProgress>(p => seen.Add(p.Fraction));

        await Run(Converter(new FakeTranslator()), path, progress: progress);

        Assert.Equal(1.0, seen[^1]);
        Assert.Equal(seen.Order(), seen);
    }

    [Fact]
    public async Task A_missing_file_and_a_wrong_extension_fail_without_throwing()
    {
        using var dir = new TempDir();
        var c = Converter(new FakeTranslator());
        Assert.False((await Run(c, dir.Combine("nope.txt"))).Success);
        Assert.False((await Run(c, dir.WriteText("a.docx", "x"))).Success);
        Assert.False((await c.RunAsync(new ConversionJob([], "txt.translate"), new Progress<JobProgress>(), default)).Success);
        var bad = await Run(c, dir.WriteText("b.txt", "Hello"), new() { ["direction"] = "fr-en" });
        Assert.False(bad.Success);
        Assert.Contains("Unknown translation direction", bad.Error);
    }

    // ---- PDF -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pdf_with_text_is_translated_into_a_text_file_and_optionally_markdown()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), pages: 2, text: n => $"Hello page {n}");
        var c = new PdfTranslateConverter(new FakeTranslator());

        var result = await c.RunAsync(new ConversionJob([pdf], "pdf.translate", new Dictionary<string, object?> { ["outputs"] = "txt,md" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal([dir.Combine("doc (translated).txt"), dir.Combine("doc (translated).md")], result.Outputs);
        var text = File.ReadAllText(result.Outputs[0]);
        Assert.Contains("ع<Hello page 1>", text);
        Assert.Contains("ع<Hello page 2>", text);
        Assert.Contains("ع\\<Hello page 1\\>", File.ReadAllText(result.Outputs[1]));      // Markdown syntax characters are escaped
        Assert.Equal("note.translate.detectedEnAr", result.Note);
    }

    [Fact]
    public async Task A_pdf_without_a_text_layer_says_to_run_ocr_first_and_writes_nothing()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"), text: _ => "");
        var result = await new PdfTranslateConverter(new FakeTranslator()).RunAsync(new ConversionJob([pdf], "pdf.translate"), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("OCR", result.Error);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task A_pdf_that_is_not_a_pdf_fails_with_a_message()
    {
        using var dir = new TempDir();
        var fake = dir.WriteText("fake.pdf", "this is not a pdf");
        var result = await new PdfTranslateConverter(new FakeTranslator()).RunAsync(new ConversionJob([fake], "pdf.translate"), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ---- images ----------------------------------------------------------------------------------------------------------

    private sealed class FakeLanguages(params string[] tags) : IWindowsOcrLanguageSource
    {
        public IReadOnlyList<string> InstalledTags() => tags;
    }

    private sealed class FakeWindowsOcr(Func<string?, OcrResult> answer) : IWindowsOcr
    {
        public List<string?> Tags { get; } = [];

        public Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken cancellationToken)
        {
            Tags.Add(languageTag);
            return Task.FromResult(answer(languageTag));
        }
    }

    private sealed class NoTesseract : ITesseractOcr
    {
        public bool IsInstalled => false;
        public Task<IReadOnlyList<string>> LanguagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<OcrResult> RecognizeAsync(string pngPath, IReadOnlyList<string> languages, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static OcrResult Lines(string language, int width, int height, params (string Text, double X, double Y, double W, double H, bool Rtl)[] lines) =>
        new("windows", language, width, height, [.. lines.Select(l => new OcrLine(l.Text, [new OcrWord(l.Text, new OcrRect(l.X, l.Y, l.W, l.H))], new OcrRect(l.X, l.Y, l.W, l.H), l.Rtl))]);

    private static (ImageTranslateConverter Converter, FakeWindowsOcr Ocr) ImageConverter(FakeTranslator translator, Func<string?, OcrResult> ocrAnswer, string[]? tags = null, Func<string?>? font = null)
    {
        var windows = new FakeWindowsOcr(ocrAnswer);
        var service = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguages(tags ?? ["en-US", "ar-SA"])), windows, new NoTesseract());
        return (new ImageTranslateConverter(service, translator, fontFile: font ?? TextOverlayRenderer.FontFile), windows);
    }

    private static string WhitePicture(string path, int width = 600, int height = 300)
    {
        using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        image.Write(path);
        return path;
    }

    private static Task<ConversionResult> RunImage(ImageTranslateConverter c, string path, Dictionary<string, object?>? options = null) =>
        c.RunAsync(new ConversionJob([path], "img.translate", options), new Progress<JobProgress>(), default);

    [Fact]
    public async Task A_picture_gives_a_text_file_with_the_translated_paragraphs()
    {
        using var dir = new TempDir();
        var path = WhitePicture(dir.Combine("sign.png"));
        var (c, ocr) = ImageConverter(new FakeTranslator(), _ => Lines("en-US", 600, 300,
            ("Welcome to our store", 40, 30, 300, 40, false),
            ("We are open every day", 40, 120, 400, 30, false),
            ("from nine until eight", 40, 156, 380, 30, false)));

        var result = await RunImage(c, path, new() { ["direction"] = "en-ar" });

        Assert.True(result.Success, result.Error);
        Assert.Equal([dir.Combine("sign (translated).txt")], result.Outputs);       // overlay is off by default
        // the last two lines are one paragraph and are translated together
        var expected = "ع<Welcome to our store>" + Environment.NewLine + Environment.NewLine + "ع<We are open every day from nine until eight>" + Environment.NewLine;
        Assert.Equal(expected, File.ReadAllText(result.Outputs[0]));
        Assert.Equal(["en-US"], ocr.Tags);                                         // explicit en-ar reads the picture as English
    }

    [Fact]
    public async Task With_auto_the_picture_is_read_in_both_languages_and_the_better_reading_wins()
    {
        using var dir = new TempDir();
        var path = WhitePicture(dir.Combine("sign.png"));
        var (c, ocr) = ImageConverter(new FakeTranslator(), tag => tag == "ar-SA"
            ? Lines("ar-SA", 600, 300, ("مرحباً بكم في متجرنا", 300, 30, 250, 40, true))
            : Lines("en-US", 600, 300, ("zz", 10, 10, 20, 20, false)));

        var result = await RunImage(c, path);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, ocr.Tags.Count);
        Assert.Contains("en<مرحباً بكم في متجرنا>", File.ReadAllText(result.Outputs[0]));
        Assert.Equal("note.translate.detectedArEn", result.Note);
    }

    [Fact]
    public async Task A_picture_without_text_and_a_missing_model_are_reported_before_anything_is_written()
    {
        using var dir = new TempDir();
        var path = WhitePicture(dir.Combine("blank.png"));
        var (empty, _) = ImageConverter(new FakeTranslator(), _ => new OcrResult("windows", "en-US", 600, 300, []));
        var none = await RunImage(empty, path, new() { ["direction"] = "en-ar" });
        Assert.False(none.Success);
        Assert.Contains("No text was found", none.Error);

        var fake = new FakeTranslator();
        fake.Ready.Remove(TranslationDirection.ArabicToEnglish);
        var (c, ocr) = ImageConverter(fake, _ => throw new InvalidOperationException("OCR must not run without the model"));
        var missing = await RunImage(c, path, new() { ["direction"] = "ar-en" });
        Assert.False(missing.Success);
        Assert.Equal("opus-mt-ar-en", AiFailures.TryParseMissingModel(missing.Error));
        Assert.Empty(ocr.Tags);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task The_overlay_replaces_the_original_text_area_with_the_translation_and_changes_nothing_else()
    {
        if (TextOverlayRenderer.FontFile() is null) return;       // no font with Arabic glyphs on this machine: nothing to draw with
        using var dir = new TempDir();
        var path = dir.Combine("sign.png");
        using (var source = new MagickImage(new MagickColor(250, 240, 200), 600, 300))
        {
            // dark "original text" blobs where the OCR says text is
            new ImageMagick.Drawing.Drawables().FillColor(new MagickColor(30, 30, 90)).Rectangle(40, 40, 300, 60).Rectangle(40, 130, 440, 150).Draw(source);
            source.Write(path);
        }
        var before = File.ReadAllBytes(path);
        var (c, _) = ImageConverter(new FakeTranslator { Translate = (_, t) => "مرحباً بكم في متجرنا" },
            _ => Lines("en-US", 600, 300, ("Welcome to our store", 40, 36, 260, 30, false), ("We are open every day", 40, 126, 400, 30, false)));

        var result = await RunImage(c, path, new() { ["direction"] = "en-ar", ["overlay"] = true });

        Assert.True(result.Success, result.Error);
        Assert.Equal([dir.Combine("sign (translated).txt"), dir.Combine("sign (translated).png")], result.Outputs);
        Assert.Equal(before, File.ReadAllBytes(path));                           // the original picture is untouched

        using var original = new MagickImage(path);
        using var drawn = new MagickImage(result.Outputs[1]);
        Assert.Equal(original.Width, drawn.Width);
        // the dark blobs are gone and replaced by text ink: far fewer dark pixels than the 260x20 solid block had
        Assert.True(DarkPixels(drawn, 40, 36, 300, 70) < DarkPixels(original, 40, 36, 300, 70) / 2);
        Assert.True(DarkPixels(drawn, 40, 36, 300, 70) > 20, "the translation was drawn");
        // outside every text area the picture is identical
        Assert.Equal(DarkPixels(original, 0, 200, 600, 300), DarkPixels(drawn, 0, 200, 600, 300));
        Assert.Equal(new MagickColor(250, 240, 200), drawn.GetPixels().GetPixel(5, 5).ToColor());
    }

    [Fact]
    public async Task The_overlay_draws_arabic_with_either_layout_path_and_without_a_font_only_the_text_file_is_written()
    {
        if (TextOverlayRenderer.FontFile() is null) return;
        using var dir = new TempDir();
        var path = WhitePicture(dir.Combine("p.png"));
        foreach (var forceOwnShaping in new[] { false, true })
        {
            TextOverlayRenderer.ComplexLayoutOverride = !forceOwnShaping;
            try
            {
                var (c, _) = ImageConverter(new FakeTranslator { Translate = (_, _) => "شكراً لتسوقكم معنا" },
                    _ => Lines("en-US", 600, 300, ("Thanks for shopping with us", 40, 100, 400, 40, false)));
                var result = await RunImage(c, path, new() { ["direction"] = "en-ar", ["overlay"] = "true" });
                Assert.True(result.Success, result.Error);
                using var drawn = new MagickImage(result.Outputs[1]);
                Assert.True(DarkPixels(drawn, 40, 96, 440, 150) > 50, $"ink with ownShaping={forceOwnShaping}");
            }
            finally
            {
                TextOverlayRenderer.ComplexLayoutOverride = null;
            }
        }

        var (noFont, _) = ImageConverter(new FakeTranslator(), _ => Lines("en-US", 600, 300, ("Thanks", 40, 100, 100, 40, false)), font: () => null);
        var plain = await RunImage(noFont, path, new() { ["direction"] = "en-ar", ["overlay"] = true });
        Assert.True(plain.Success, plain.Error);
        Assert.Single(plain.Outputs);
        Assert.Contains("note.translate.noFont", plain.Note);
    }

    private static int DarkPixels(MagickImage image, int left, int top, int right, int bottom)
    {
        using var crop = (MagickImage)image.Clone();
        crop.Crop(new MagickGeometry(left, top, (uint)(right - left), (uint)(bottom - top)));
        crop.ResetPage();
        var count = 0;
        var bytes = crop.GetPixels().ToByteArray(PixelMapping.RGB)!;
        for (var i = 0; i < bytes.Length; i += 3)
            if (bytes[i] + bytes[i + 1] + bytes[i + 2] < 300) count++;
        return count;
    }
}
