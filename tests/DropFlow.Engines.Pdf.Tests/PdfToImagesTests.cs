using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

public class PdfToImagesTests
{
    [Fact]
    public async Task A_single_page_becomes_one_file_at_300_dpi_by_default()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1);

        var result = await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf]));

        var file = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("doc (image).jpg"), file);
        Assert.Equal("jpg", PixelKit.Signature(file));
        Assert.Equal((2550, 3300), PixelKit.HeaderSize(file)); // Letter page: 8.5 x 11 in at 300 DPI
    }

    [Fact]
    public async Task Several_pages_go_into_a_folder_named_after_the_file()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 3);

        var result = await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("format", "png"), ("dpi", 72))));

        var folder = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("doc (images)"), folder);
        Assert.Equal(["doc - 1.png", "doc - 2.png", "doc - 3.png"], Kit.FilesIn(folder));
        foreach (var file in Directory.GetFiles(folder))
        {
            Assert.Equal("png", PixelKit.Signature(file));
            Assert.Equal((612, 792), PixelKit.HeaderSize(file));
        }
    }

    [Fact]
    public async Task The_rendered_picture_contains_the_page_text()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => "HELLO");

        var result = await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("format", "png"), ("dpi", 72))));

        var picture = PixelKit.Decode(result.Outputs[0]);
        Assert.Equal((612, 792), (picture.Width, picture.Height));
        var bounds = picture.InkBounds()!.Value;
        // Text is drawn at x = 72 pt, 92 pt below the top edge (baseline y = 700 of 792) in a 24 pt font.
        Assert.InRange(bounds.MinX, 70, 80);
        Assert.InRange(bounds.MinY, 70, 90);
        Assert.InRange(bounds.MaxX - bounds.MinX, 50, 120);
        Assert.InRange(picture.At(5, 5), 250, 255); // white paper
    }

    [Fact]
    public async Task The_pages_option_selects_which_pages_are_drawn()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 3, n => new string('W', n * 4));

        var result = await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("format", "png"), ("dpi", 72), ("pages", "3"))));

        var file = Assert.Single(result.Outputs); // one selected page -> one file, not a folder
        var bounds = PixelKit.Decode(file).InkBounds()!.Value;
        Assert.True(bounds.MaxX - bounds.MinX > 150, "page 3 has twelve wide letters");
    }

    [Fact]
    public async Task Convert_png_and_jpg_use_the_converted_suffix_and_default_resolution()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1);

        var png = await Kit.Ok(Kit.RunAsync("convert:png", [pdf]));
        var jpg = await Kit.Ok(Kit.RunAsync("convert:jpg", [pdf]));

        Assert.Equal(dir.Combine("doc (converted).png"), png.Outputs[0]);
        Assert.Equal(dir.Combine("doc (converted).jpg"), jpg.Outputs[0]);
        Assert.Equal((2550, 3300), PixelKit.HeaderSize(png.Outputs[0]));
    }

    [Fact]
    public async Task Higher_jpeg_quality_makes_a_bigger_file()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => "Quality matters");

        var low = (await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("dpi", 150), ("quality", 20))))).Outputs[0];
        var high = (await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("dpi", 150), ("quality", 95))))).Outputs[0];

        Assert.True(new FileInfo(high).Length > new FileInfo(low).Length);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2000)]
    public async Task Out_of_range_dpi_is_rejected(int dpi)
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1);

        var result = await Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("dpi", dpi)));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Unicode_paths_work_and_the_output_directory_is_honoured()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("مجلد")).FullName;
        var pdf = Samples.Pdf(Path.Combine(folder, "ملف.pdf"), 1);

        var result = await Kit.Ok(Kit.RunAsync("convert:png", [pdf], outputDirectory: outDir.Path));

        Assert.Equal(outDir.Combine("ملف (converted).png"), result.Outputs[0]);
        Assert.Equal("png", PixelKit.Signature(result.Outputs[0]));
    }

    [Fact]
    public async Task Corrupt_pdf_fails_without_leaving_files()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("bad.pdf", "nope");

        var result = await Kit.RunAsync("convert:png", [bad]);

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["bad.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_folder()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 5);
        using var cts = new CancellationTokenSource();
        var cancelling = new CancelOnSecondReport(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Kit.Converter("pdf.to-images", "pdf").RunAsync(
                new DropFlow.Core.Operations.ConversionJob([pdf], "pdf.to-images", Kit.Opts(("dpi", 72))), cancelling, cts.Token));

        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
        Assert.Empty(Directory.GetDirectories(dir.Path)); // the half-written folder was removed
    }

    [Fact]
    public async Task Progress_is_reported_for_each_page_and_ends_at_one()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 4);
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("pdf.to-images", [pdf], Kit.Opts(("dpi", 36)), progress: progress));

        Assert.True(progress.Reports.Count >= 4);
        Assert.Equal(1, progress.Reports[^1].Fraction);
        Assert.Equal(progress.Reports.Select(r => r.Fraction).OrderBy(f => f), progress.Reports.Select(r => r.Fraction));
    }

    [Theory]
    [InlineData(612, 792, 300, 2550, 3300, false)]
    [InlineData(612, 792, 72, 612, 792, false)]
    public void FitBitmap_keeps_normal_pages_at_the_requested_dpi(double w, double h, double dpi, int ew, int eh, bool reduced)
    {
        var fit = PdfiumDocument.FitBitmap(w, h, dpi);
        Assert.Equal((ew, eh, reduced), (fit.Width, fit.Height, fit.Reduced));
    }

    [Fact]
    public void FitBitmap_lowers_the_dpi_for_poster_sized_pages()
    {
        // A0 poster (2384 x 3370 pt) at 600 dpi would be 19867 x 28083 px = 558 MP.
        var fit = PdfiumDocument.FitBitmap(2384, 3370, 600);
        Assert.True(fit.Reduced);
        Assert.True((long)fit.Width * fit.Height <= PdfiumDocument.MaxPixels);
        Assert.True(fit.Dpi < 600);
    }

    private sealed class CancelOnSecondReport(CancellationTokenSource cts) : IProgress<DropFlow.Core.Operations.JobProgress>
    {
        private int _count;
        public void Report(DropFlow.Core.Operations.JobProgress value)
        {
            if (++_count == 2) cts.Cancel();
        }
    }
}
