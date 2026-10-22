using DropFlow.AI.Ocr;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>What every page renderer must do, run for real on generated PDFs (Windows' own renderer and PDFium).</summary>
public abstract class PageRasterizerContract
{
    protected abstract IPageRasterizer Create();

    protected abstract int MaxSide { get; }

    [Fact]
    public async Task Renders_every_page_to_a_png_at_the_requested_dpi()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("two.pdf"), pages: 2);   // US Letter: 612 x 792 pt
        var outDir = dir.Combine("pages");

        var progress = new List<double>();
        var files = await Create().RenderPagesAsync(pdf, outDir, 150, new SyncProgress(progress.Add), default);

        Assert.Equal([Path.Combine(outDir, "page-0001.png"), Path.Combine(outDir, "page-0002.png")], files);
        Assert.Equal([0.5, 1.0], progress);
        foreach (var file in files)
        {
            using var png = new TextImage.Reader(file);
            Assert.Equal(1275, png.Width);   // 612 pt / 72 * 150 dpi
            Assert.Equal(1650, png.Height);  // 792 pt / 72 * 150 dpi
            Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 255, 255), png.Pixel(5, 5)); // white page
        }
    }

    [Fact]
    public async Task The_rendered_page_actually_contains_the_text()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("one.pdf"), pages: 1);
        var files = await Create().RenderPagesAsync(pdf, dir.Combine("p"), 100, null, default);

        using var png = new TextImage.Reader(files[0]);
        var dark = 0;
        for (var y = 0; y < png.Height; y += 2)
            for (var x = 0; x < png.Width; x += 2)
                if (png.Pixel(x, y).GetBrightness() < 0.3f) dark++;
        Assert.InRange(dark, 100, 100_000);   // glyph pixels exist, but the page is mostly white
    }

    [Fact]
    public async Task Dpi_scales_the_output_and_huge_pages_are_capped()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("p.pdf"));
        var low = await Create().RenderPagesAsync(pdf, dir.Combine("low"), 72, null, default);
        var high = await Create().RenderPagesAsync(pdf, dir.Combine("high"), 600, null, default);
        using var a = new TextImage.Reader(low[0]);
        using var b = new TextImage.Reader(high[0]);
        Assert.Equal(612, a.Width);
        Assert.True(b.Height <= MaxSide);
        Assert.InRange(b.Height, 6000, MaxSide);   // 792/72*600 = 6600, under the cap
    }

    [Fact]
    public async Task Works_with_arabic_file_and_folder_names()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("مستندات");
        Directory.CreateDirectory(folder);
        var pdf = Samples.Pdf(Path.Combine(folder, "عقد الإيجار.pdf"));

        var files = await Create().RenderPagesAsync(pdf, dir.Combine("out"), 72, null, default);
        Assert.Single(files);
        Assert.True(new FileInfo(files[0]).Length > 1000);
    }

    [Fact]
    public async Task Damaged_and_empty_pdfs_raise_the_render_exception()
    {
        using var dir = new TempDir();
        var junk = dir.WriteText("junk.pdf", "%PDF-1.4\nthis is not really a pdf\n");
        var empty = dir.WriteText("empty.pdf", "");
        var r = Create();
        await Assert.ThrowsAsync<PdfRenderException>(() => r.RenderPagesAsync(junk, dir.Combine("a"), 72, null, default));
        await Assert.ThrowsAsync<PdfRenderException>(() => r.RenderPagesAsync(empty, dir.Combine("b"), 72, null, default));
        await Assert.ThrowsAsync<PdfRenderException>(() => r.RenderPagesAsync(dir.Combine("missing.pdf"), dir.Combine("c"), 72, null, default));
    }

    [Fact]
    public async Task Cancellation_is_honoured_between_pages()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("many.pdf"), pages: 6);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 0) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().RenderPagesAsync(pdf, dir.Combine("out"), 72, progress, cts.Token));
        Assert.True(Directory.GetFiles(dir.Combine("out")).Length < 6);
    }

    protected sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}

/// <summary>PDFium (PDFtoImage): the default on macOS and Linux.</summary>
public class PdfiumPageRasterizerTests : PageRasterizerContract
{
    protected override IPageRasterizer Create() => new PdfiumPageRasterizer();

    protected override int MaxSide => PdfiumPageRasterizer.MaxPixelsPerSide;

    [EngineFact(EngineId.Qpdf)]
    public async Task A_password_protected_pdf_raises_the_render_exception()
    {
        using var dir = new TempDir();
        var plain = Samples.Pdf(dir.Combine("plain.pdf"));
        var locked = dir.Combine("locked.pdf");
        var qpdf = DropFlow.TestSupport.Engines.Locator.Locate(EngineId.Qpdf)!;
        var run = await DropFlow.Engines.Common.ProcessRunner.RunAsync(qpdf, ["--encrypt", "user-pw", "owner-pw", "256", "--", plain, locked]);
        Assert.True(run.Success, run.LastMessage);

        var ex = await Assert.ThrowsAsync<PdfRenderException>(() => Create().RenderPagesAsync(locked, dir.Combine("out"), 72, null, default));
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pixel_size_follows_the_dpi()
    {
        Assert.Equal((1275, 1650), PdfiumPageRasterizer.PixelSize(612, 792, 150));
        Assert.Equal((5100, 6600), PdfiumPageRasterizer.PixelSize(612, 792, 600));   // under the cap
    }

    [Fact]
    public void A_huge_page_is_capped_on_its_longest_side()
    {
        // A0 (2384 x 3370 pt) at 300 dpi would be 9933 x 14042 px
        Assert.Equal((6367, PdfiumPageRasterizer.MaxPixelsPerSide), PdfiumPageRasterizer.PixelSize(2384, 3370, 300));
    }
}

#if WINDOWS
/// <summary>The PDF page renderer built into Windows (Windows.Data.Pdf): the default on Windows.</summary>
public class WindowsPdfPageRasterizerTests : PageRasterizerContract
{
    protected override IPageRasterizer Create() => new WindowsPdfPageRasterizer();

    protected override int MaxSide => WindowsPdfPageRasterizer.MaxPixelsPerSide;

    [OcrFact("en")]
    public async Task A_rendered_page_can_be_read_back_by_windows_ocr()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("inv.pdf"), pages: 2, text: n => $"Invoice number {4000 + n}");
        var pages = await new WindowsPdfPageRasterizer().RenderPagesAsync(pdf, dir.Combine("pages"), 200, null, default);

        var svc = new OcrService(new FakeEngines());
        for (var i = 0; i < pages.Count; i++)
        {
            var result = await svc.RecognizeAsync(pages[i], new OcrRequest("en", OcrEnginePreference.Windows), default);
            Assert.Contains($"{4001 + i}", result.Text);
            Assert.Contains("Invoice", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
#endif

public class PdfOcrConverterTests
{
    private static ConversionJob Job(string pdf, Dictionary<string, object?>? options = null, string? outDir = null) => new([pdf], "pdf.ocr", options, outDir);

    private sealed class RecordingRasterizer : IPageRasterizer
    {
        public int Dpi { get; private set; }
        public string? Pdf { get; private set; }
        public async Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken ct)
        {
            Dpi = dpi; Pdf = pdfPath;
            Directory.CreateDirectory(outputDirectory);
            var paths = new List<string>();
            for (var i = 1; i <= 3; i++)
            {
                var p = Path.Combine(outputDirectory, $"page-{i:0000}.png");
                Samples.Png(p);
                paths.Add(p);
            }
            await Task.CompletedTask;
            return paths;
        }
    }

    private sealed class FailingRasterizer(string message) : IPageRasterizer
    {
        public Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken ct) =>
            throw new PdfRenderException(message);
    }

    [Fact]
    public async Task Builds_a_searchable_pdf_through_the_documented_tesseract_command()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir);
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"), pages: 2);
        var before = File.ReadAllBytes(pdf);
        var reports = new List<JobProgress>();
        var converter = new PdfOcrConverter(fake.Engines, tesseract: new TesseractOcr(fake.Engines));

        var result = await converter.RunAsync(Job(pdf), new SyncProgress(reports.Add), default);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("scan (ocr).pdf"), output);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 5));
        Assert.Equal(before, File.ReadAllBytes(pdf));                         // original untouched

        var call = Assert.Single(fake.Calls);
        Assert.Contains("-l ara+eng --dpi 300 pdf", call);                    // auto language = Arabic + English, dpi passed explicitly
        Assert.EndsWith("pdf", call);
        var work = call.Split("CD=", 2)[1].Split(" ARGS=")[0];
        Assert.False(Directory.Exists(work), "the temporary work folder must be removed");

        Assert.Contains(reports, r => r.Message == "Rendering pages");
        Assert.Contains(reports, r => r.Message == "Recognising page 2 of 2");  // tesseract's "Page N" lines drive progress
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.Fraction >= p.First.Fraction - 1e-9), "progress must not go backwards");
    }

    [Fact]
    public async Task The_page_list_names_every_rendered_page_in_order()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir);
        // keep a copy of the page list the converter hands to tesseract (its first argument)
        File.WriteAllText(fake.ScriptPath, OperatingSystem.IsWindows()
            ? File.ReadAllText(fake.ScriptPath).Replace(">>\"%~dp0args.log\" echo", "type \"%~1\" > \"%~dp0pages-copy.txt\"\r\n>>\"%~dp0args.log\" echo")
            : File.ReadAllText(fake.ScriptPath).Replace("echo \"CD=", "cp \"$1\" \"$here/pages-copy.txt\"\necho \"CD="));
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));
        var rasterizer = new RecordingRasterizer();

        var result = await new PdfOcrConverter(fake.Engines, rasterizer, new TesseractOcr(fake.Engines)).RunAsync(Job(pdf, new() { ["dpi"] = 200, ["language"] = "en" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(200, rasterizer.Dpi);
        Assert.Equal(pdf, rasterizer.Pdf);
        var listed = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fake.ScriptPath)!, "pages-copy.txt"));
        Assert.Equal(3, listed.Length);
        Assert.Equal(["page-0001.png", "page-0002.png", "page-0003.png"], listed.Select(Path.GetFileName));
        Assert.Contains("-l eng --dpi 200 pdf", fake.Calls.Single());
    }

    [Fact]
    public async Task A_rasterizer_bound_later_replaces_the_default()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir);
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));
        var converter = new PdfOcrConverter(fake.Engines, tesseract: new TesseractOcr(fake.Engines));
#if WINDOWS
        Assert.IsType<WindowsPdfPageRasterizer>(converter.Rasterizer);
#else
        Assert.IsType<PdfiumPageRasterizer>(converter.Rasterizer);
#endif

        var custom = new RecordingRasterizer();
        converter.Rasterizer = custom;
        var result = await converter.RunAsync(Job(pdf), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(pdf, custom.Pdf);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_output()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir);
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));
        File.WriteAllText(dir.Combine("scan (ocr).pdf"), "precious");
        var converter = new PdfOcrConverter(fake.Engines, new RecordingRasterizer(), new TesseractOcr(fake.Engines));

        var result = await converter.RunAsync(Job(pdf), new Progress<JobProgress>(), default);
        Assert.Equal(dir.Combine("scan (ocr 2).pdf"), result.Outputs.Single());
        Assert.Equal("precious", File.ReadAllText(dir.Combine("scan (ocr).pdf")));
    }

    [Fact]
    public async Task Missing_tesseract_is_named_so_the_app_can_offer_to_install_it()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));
        var result = await new PdfOcrConverter(new FakeEngines(), new RecordingRasterizer()).RunAsync(Job(pdf), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.Contains("Tesseract is not installed", result.Error);
    }

    [Fact]
    public async Task Missing_language_data_is_named()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir, languages: ["eng", "osd"]);
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));
        var result = await new PdfOcrConverter(fake.Engines, new RecordingRasterizer(), new TesseractOcr(fake.Engines))
            .RunAsync(Job(pdf, new() { ["language"] = "ar" }), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.Contains("ara", result.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Render_errors_and_tesseract_errors_become_failures_without_leftovers()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));

        var locked = await new PdfOcrConverter(new FakeTesseract(dir).Engines, new FailingRasterizer("The PDF is password-protected; unlock it first."))
            .RunAsync(Job(pdf), new Progress<JobProgress>(), default);
        Assert.False(locked.Success);
        Assert.Contains("password-protected", locked.Error);

        using var dir2 = new TempDir();
        var broken = new FakeTesseract(dir2, failPdf: true);
        var pdf2 = Samples.Pdf(dir2.Combine("scan.pdf"));
        var failed = await new PdfOcrConverter(broken.Engines, new RecordingRasterizer(), new TesseractOcr(broken.Engines)).RunAsync(Job(pdf2), new Progress<JobProgress>(), default);
        Assert.False(failed.Success);
        Assert.Contains("cannot write pdf", failed.Error);

        using var dir3 = new TempDir();
        var garbage = new FakeTesseract(dir3, pdfIsGarbage: true);
        var pdf3 = Samples.Pdf(dir3.Combine("scan.pdf"));
        var bad = await new PdfOcrConverter(garbage.Engines, new RecordingRasterizer(), new TesseractOcr(garbage.Engines)).RunAsync(Job(pdf3), new Progress<JobProgress>(), default);
        Assert.False(bad.Success);
        Assert.Empty(Directory.GetFiles(dir3.Path, "*(ocr*"));
    }

    [Fact]
    public async Task Real_windows_rendering_feeds_the_tesseract_command_for_a_generated_pdf()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir);
        var pdf = Samples.Pdf(dir.Combine("عقد.pdf"), pages: 2);   // Arabic file name, rendered by the real Windows renderer

        var result = await new PdfOcrConverter(fake.Engines, tesseract: new TesseractOcr(fake.Engines)).RunAsync(Job(pdf), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(dir.Combine("عقد (ocr).pdf"), result.Outputs.Single());
    }

    [Fact]
    public async Task Cancellation_removes_the_work_folder_and_writes_nothing()
    {
        using var dir = new TempDir();
        var fake = new FakeTesseract(dir);
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"));
        using var cts = new CancellationTokenSource();
        var rasterizer = new CancelOnRender(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfOcrConverter(fake.Engines, rasterizer, new TesseractOcr(fake.Engines)).RunAsync(Job(pdf), new Progress<JobProgress>(), cts.Token));
        Assert.Empty(Directory.GetFiles(dir.Path, "*(ocr*"));
        Assert.Empty(fake.Calls);
    }

    private sealed class CancelOnRender(CancellationTokenSource cts) : IPageRasterizer
    {
        public Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    [EngineFact(EngineId.Tesseract)]
    public async Task Real_tesseract_builds_a_searchable_pdf_from_a_scanned_page()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("scan.pdf"), pages: 2, text: n => $"Invoice number {4000 + n}");
        var converter = new PdfOcrConverter(DropFlow.TestSupport.Engines.Locator);

        var result = await converter.RunAsync(Job(pdf, new() { ["language"] = "en" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        var bytes = File.ReadAllBytes(result.Outputs[0]);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.True(bytes.Length > new FileInfo(pdf).Length);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count);
    }

    [Fact]
    public void Language_resolution_prefers_arabic_and_english_when_auto_and_names_what_is_missing()
    {
        Assert.Equal(["ara", "eng"], PdfOcrConverter.ResolveLanguages("auto", ["eng", "ara", "osd"], out var e1));
        Assert.Null(e1);
        Assert.Equal(["eng"], PdfOcrConverter.ResolveLanguages("auto", ["eng", "osd"], out _));
        Assert.Equal(["deu"], PdfOcrConverter.ResolveLanguages("auto", ["osd", "deu"], out _));
        Assert.Equal(["ara", "eng"], PdfOcrConverter.ResolveLanguages("ar+en", ["eng", "ara"], out _));
        Assert.Null(PdfOcrConverter.ResolveLanguages("ar", ["eng"], out var e2));
        Assert.Contains("ara", e2);
        Assert.Null(PdfOcrConverter.ResolveLanguages("zz", ["eng"], out var e3));
        Assert.Contains("zz", e3);
        Assert.Null(PdfOcrConverter.ResolveLanguages("auto", ["osd"], out var e4));
        Assert.Contains("no language data", e4);
    }

    [Fact]
    public void Operations_options_engine_and_supports_are_truthful()
    {
        var c = new PdfOcrConverter(new FakeEngines());
        Assert.Equal(["pdf.ocr"], c.Operations);
        Assert.NotNull(ToolCatalog.LoadEmbedded().Tool("pdf.ocr"));
        Assert.Equal(EngineId.Tesseract, c.EngineFor("pdf.ocr"));
        Assert.True(c.Supports("pdf.ocr", "pdf"));
        Assert.True(c.Supports("pdf.ocr", "PDF"));
        foreach (var ext in new[] { "png", "docx", "mp3", "txt", "" }) Assert.False(c.Supports("pdf.ocr", ext), ext);
        Assert.False(c.Supports("img.ocr", "pdf"));
        var options = c.OptionsFor("pdf.ocr").ToDictionary(o => o.Key);
        Assert.Equal(["dpi", "language"], options.Keys.Order());
        Assert.Equal(150, options["dpi"].Min);
        Assert.Equal(400, options["dpi"].Max);
    }

    private sealed class SyncProgress(Action<JobProgress> onReport) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => onReport(value);
    }
}
