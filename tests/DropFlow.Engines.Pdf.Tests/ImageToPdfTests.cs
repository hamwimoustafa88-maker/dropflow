using DropFlow.Engines.Pdf.Tools;
using DropFlow.TestSupport;
using ImageMagick;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

public class ImageToPdfTests
{
    private static string Make(string path, int w, int h, MagickColor color, Action<MagickImage>? tweak = null)
    {
        using var image = new MagickImage(color, (uint)w, (uint)h);
        tweak?.Invoke(image);
        image.Write(path);
        return path;
    }

    private static List<(double W, double H)> PageSizes(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => (Math.Round(p.Width, 1), Math.Round(p.Height, 1))).ToList();
    }

    [Fact]
    public async Task A_png_becomes_a_one_page_pdf_the_size_of_the_picture()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"), 64, 48);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [png]));

        Assert.Equal(dir.Combine("pic (converted).pdf"), result.Outputs[0]);
        Assert.Equal("pdf", PixelKit.Signature(result.Outputs[0]));
        Assert.Equal([(64.0, 48.0)], PageSizes(result.Outputs[0]));
        using var doc = PdfDocument.Open(result.Outputs[0]);
        var image = Assert.Single(doc.GetPage(1).GetImages());
        Assert.Equal((64, 48), (image.WidthInSamples, image.HeightInSamples));
    }

    [Fact]
    public async Task The_pdf_page_shows_the_same_pixels_as_the_source_picture()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"), 64, 48);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [png]));

        var rendered = PixelKit.RenderRgb(result.Outputs[0], 1, dpi: 72);
        var source = PixelKit.DecodeRgb(png);
        Assert.Equal((source.Width, source.Height), (rendered.Width, rendered.Height));
        var diffs = source.Rgb.Select((p, i) => Math.Abs(p - rendered.Rgb[i])).ToList();
        Assert.True(diffs.Average() <= 2, $"mean channel difference was {diffs.Average():0.0}, worst {diffs.Max()}");
    }

    [Fact]
    public async Task Mixed_formats_become_one_page_each_in_the_given_order()
    {
        using var dir = new TempDir();
        var files = new List<string>
        {
            Samples.Png(dir.Combine("a.png"), 10, 20),
            Make(dir.Combine("b.jpg"), 30, 20, MagickColors.Red),
            Make(dir.Combine("c.bmp"), 40, 20, MagickColors.Green),
            Make(dir.Combine("d.gif"), 50, 20, MagickColors.Blue),
            Make(dir.Combine("e.tiff"), 60, 20, MagickColors.Yellow),
            Make(dir.Combine("f.webp"), 70, 20, MagickColors.Orange),
            Make(dir.Combine("g.ico"), 32, 32, MagickColors.Purple),
            Make(dir.Combine("h.avif"), 80, 20, MagickColors.Teal),
        };

        var result = await Kit.Ok(Kit.RunAsync("img.to-pdf", files));

        Assert.Equal(dir.Combine("a (converted).pdf"), result.Outputs[0]);
        Assert.Equal([(10.0, 20.0), (30, 20), (40, 20), (50, 20), (60, 20), (70, 20), (32, 32), (80, 20)], PageSizes(result.Outputs[0]));
        foreach (var input in files) Assert.True(File.Exists(input)); // inputs are left alone
    }

    [Fact]
    public async Task Each_page_shows_its_own_picture()
    {
        using var dir = new TempDir();
        var red = Make(dir.Combine("red.png"), 20, 20, MagickColors.Red);
        var blue = Make(dir.Combine("blue.png"), 20, 20, MagickColors.Blue);

        var result = await Kit.Ok(Kit.RunAsync("img.to-pdf", [red, blue]));

        // Convert to gray: red -> ~76, blue -> ~29. The pages must differ and keep the order.
        var first = PixelKit.RenderPage(result.Outputs[0], 1, 72).Pixels.Average(b => (double)b);
        var second = PixelKit.RenderPage(result.Outputs[0], 2, 72).Pixels.Average(b => (double)b);
        Assert.InRange(first, 70, 85);
        Assert.InRange(second, 24, 36);
    }

    [Fact]
    public async Task A_jpeg_is_embedded_without_re_encoding()
    {
        using var dir = new TempDir();
        var jpg = Make(dir.Combine("photo.jpg"), 64, 48, MagickColors.SteelBlue, img => img.Quality = 80);
        var original = File.ReadAllBytes(jpg);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [jpg]));

        using var doc = PdfDocument.Open(result.Outputs[0]);
        var image = Assert.Single(doc.GetPage(1).GetImages());
        Assert.True(image.TryGetPng(out _) || image.RawBytes.Length > 0);
        Assert.Equal(original, image.RawBytes.ToArray());
    }

    [Fact]
    public async Task Exif_orientation_is_applied_so_phone_photos_are_not_sideways()
    {
        using var dir = new TempDir();
        // 40 x 30 pixels stored landscape, tagged "rotate 90 clockwise to view": the reader sees 30 x 40.
        var jpg = Make(dir.Combine("phone.jpg"), 40, 30, MagickColors.Gray, img =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6); // 6 = rotate 90 degrees clockwise to view
            img.SetProfile(exif);
            img.Orientation = Orientation.RightTop; // Magick re-syncs the EXIF tag from this property when it writes
        });

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [jpg]));

        Assert.Equal([(30.0, 40.0)], PageSizes(result.Outputs[0]));
    }

    [Fact]
    public async Task Declared_resolution_sets_the_physical_page_size()
    {
        using var dir = new TempDir();
        var png = Make(dir.Combine("hi.png"), 144, 72, MagickColors.White, img => img.Density = new Density(144, DensityUnit.PixelsPerInch));

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [png]));

        Assert.Equal([(72.0, 36.0)], PageSizes(result.Outputs[0]));
    }

    [Fact]
    public async Task Transparent_pngs_keep_their_transparency()
    {
        using var dir = new TempDir();
        var png = Make(dir.Combine("t.png"), 20, 20, MagickColors.Transparent);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [png]));

        var page = PixelKit.RenderPage(result.Outputs[0], 1, 72);
        Assert.True(page.Pixels.All(p => p > 250), "a fully transparent picture shows the white page");
    }

    [Theory]
    [InlineData("a4", 595.3, 841.9)]
    public async Task Fixed_page_sizes_fit_and_center_the_picture(string size, double w, double h)
    {
        using var dir = new TempDir();
        var png = Make(dir.Combine("p.png"), 100, 100, MagickColors.Black);

        var result = await Kit.Ok(Kit.RunAsync("img.to-pdf", [png], Kit.Opts(("pageSize", size))));

        Assert.Equal([(w, h)], PageSizes(result.Outputs[0]));
        var page = PixelKit.RenderPage(result.Outputs[0], 1, 36);
        var bounds = page.InkBounds(128)!.Value;
        Assert.InRange((bounds.MinX + bounds.MaxX) / 2.0, page.Width * 0.48, page.Width * 0.52);
        Assert.InRange((bounds.MinY + bounds.MaxY) / 2.0, page.Height * 0.48, page.Height * 0.52);
    }

    [Fact]
    public async Task A_landscape_picture_gets_a_landscape_fixed_page()
    {
        using var dir = new TempDir();
        var png = Make(dir.Combine("wide.png"), 200, 100, MagickColors.Black);

        var result = await Kit.Ok(Kit.RunAsync("img.to-pdf", [png], Kit.Opts(("pageSize", "letter"))));

        Assert.Equal([(792.0, 612.0)], PageSizes(result.Outputs[0]));
    }

    [Fact]
    public async Task A_multi_page_tiff_becomes_several_pages()
    {
        using var dir = new TempDir();
        var path = dir.Combine("scan.tiff");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.White, 20, 30));
            frames.Add(new MagickImage(MagickColors.Black, 40, 50));
            frames.Write(path);
        }

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [path]));

        Assert.Equal([(20.0, 30.0), (40, 50)], PageSizes(result.Outputs[0]));
    }

    [Fact]
    public async Task An_svg_is_rasterised_into_the_pdf()
    {
        using var dir = new TempDir();
        var svg = dir.WriteText("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'><rect width='40' height='30' fill='black'/></svg>");

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [svg]));

        Assert.Single(PageSizes(result.Outputs[0]));
        Assert.True(PixelKit.RenderPage(result.Outputs[0], 1, 72).Darkest < 30);
    }

    [Fact]
    public async Task A_corrupt_picture_fails_cleanly()
    {
        using var dir = new TempDir();
        var good = Samples.Png(dir.Combine("good.png"));
        var bad = dir.WriteText("bad.png", "not a png");

        var result = await Kit.RunAsync("img.to-pdf", [good, bad]);

        Assert.False(result.Success);
        Assert.Contains("bad.png", result.Error);
        Assert.Equal(["bad.png", "good.png"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_named_in_the_error()
    {
        using var dir = new TempDir();
        var good = Samples.Png(dir.Combine("good.png"));
        var text = dir.WriteText("notes.txt", "hello");

        var result = await Kit.RunAsync("img.to-pdf", [good, text]);

        Assert.False(result.Success);
        Assert.Contains("notes.txt", result.Error);
    }

    [Fact]
    public async Task Unicode_names_and_existing_outputs_are_handled()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("صور")).FullName;
        var png = Samples.Png(Path.Combine(folder, "صورة.png"));

        var first = await Kit.Ok(Kit.RunAsync("convert:pdf", [png]));
        var second = await Kit.Ok(Kit.RunAsync("convert:pdf", [png]));

        Assert.Equal(Path.Combine(folder, "صورة (converted).pdf"), first.Outputs[0]);
        Assert.Equal(Path.Combine(folder, "صورة (converted 2).pdf"), second.Outputs[0]);
    }

    [Fact]
    public async Task Cancellation_leaves_no_pdf_behind()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:pdf", [png], ct: cts.Token));

        Assert.Equal(["p.png"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public void PointSize_defaults_to_72_dpi_so_one_pixel_is_one_point()
    {
        var (pw, ph) = ImageToPdfTools.PointSize(100, 50, new Density(0.0, 0.0));
        Assert.Equal((100.0, 50.0), (pw, ph));
    }

    [Fact]
    public void PointSize_never_exceeds_the_pdf_page_limit()
    {
        var (w, h) = ImageToPdfTools.PointSize(40000, 20000, new Density(0.0, 0.0));
        Assert.Equal(14400, Math.Max(w, h), 3);
        Assert.Equal(2.0, w / h, 3);
    }

    [Fact]
    public void PointSize_understands_dots_per_centimetre()
    {
        var (w, _) = ImageToPdfTools.PointSize(254, 100, new Density(100, 100, DensityUnit.PixelsPerCentimeter)); // 254 dpi
        Assert.Equal(72.0, w, 1);
    }
}
