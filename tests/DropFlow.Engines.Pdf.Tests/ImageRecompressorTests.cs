using DropFlow.Engines.Pdf.Tools;
using DropFlow.TestSupport;
using ImageMagick;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

public class ImageRecompressorTests
{
    private static string NoisePicture(TempDir dir, string name, int w, int h, bool alpha = false)
    {
        var path = dir.Combine(name);
        using var image = new MagickImage(MagickColors.CornflowerBlue, (uint)w, (uint)h);
        image.AddNoise(NoiseType.Uniform);
        if (alpha)
        {
            image.HasAlpha = true;
            image.Evaluate(Channels.Alpha, EvaluateOperator.Set, Quantum.Max / 2);
        }
        // Force truecolor: Magick would otherwise save low-entropy noise as a palette PNG, which PDF viewers store as an Indexed image.
        image.Write(path, path.EndsWith(".png", StringComparison.Ordinal) ? MagickFormat.Png24 : image.Format);
        return path;
    }

    private static string PdfWithPicture(TempDir dir, string picture, string name)
    {
        var b = new PdfBuilder();
        b.AddPage().Text("caption", 72, 60).Image(picture, 36, 100, 540, 405);
        return b.Save(dir.Combine(name));
    }

    private static (int Width, int Height, bool IsJpeg, long Bytes) FirstImage(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var image = doc.GetPage(1).GetImages().First();
        var raw = image.RawBytes.ToArray();
        return (image.WidthInSamples, image.HeightInSamples, raw.Length > 2 && raw[0] == 0xFF && raw[1] == 0xD8, raw.Length);
    }

    private static double MeanLuma(string pdf)
    {
        var page = PixelKit.RenderPage(pdf, 1, 36);
        return page.Pixels.Average(p => (double)p);
    }

    [Fact]
    public void A_big_jpeg_is_re_encoded_smaller_and_scaled_to_what_the_page_can_show()
    {
        using var dir = new TempDir();
        var photo = NoisePicture(dir, "p.jpg", 2400, 1800);
        var pdf = PdfWithPicture(dir, photo, "in.pdf");
        var output = dir.Combine("out.pdf");

        var result = PdfImageRecompressor.Recompress(pdf, output, quality: 72, maxDpi: 150, CancellationToken.None);

        Assert.Equal((1, 1), (result.ImagesFound, result.ImagesRecompressed));
        Assert.True(result.BytesSaved > 0);
        var before = FirstImage(pdf);
        var after = FirstImage(output);
        Assert.True(after.IsJpeg);
        Assert.True(after.Bytes < before.Bytes / 2);
        Assert.Equal(2400, before.Width);
        Assert.Equal(1650, after.Width); // 11 in x 150 dpi: more detail than the page can show is dropped
        Assert.Equal(1238, after.Height);
        Assert.InRange(MeanLuma(output), MeanLuma(pdf) - 3, MeanLuma(pdf) + 3); // looks the same at page scale
        Assert.Equal(["caption"], Kit.PageTexts(output)); // text is untouched
    }

    [Fact]
    public void A_flate_rgb_picture_made_from_a_png_is_converted_to_jpeg()
    {
        using var dir = new TempDir();
        var png = NoisePicture(dir, "p.png", 1400, 1000);
        var pdf = PdfWithPicture(dir, png, "in.pdf");
        var output = dir.Combine("out.pdf");

        var result = PdfImageRecompressor.Recompress(pdf, output, quality: 70, maxDpi: 300, CancellationToken.None);

        Assert.Equal(1, result.ImagesRecompressed);
        var before = FirstImage(pdf);
        var after = FirstImage(output);
        Assert.False(before.IsJpeg);
        Assert.True(after.IsJpeg);
        Assert.True(after.Bytes < before.Bytes / 2);
        Assert.Equal(1400, after.Width);
        Assert.InRange(MeanLuma(output), MeanLuma(pdf) - 3, MeanLuma(pdf) + 3);
    }

    [Fact]
    public void A_picture_with_transparency_keeps_its_soft_mask_and_the_file_stays_valid()
    {
        using var dir = new TempDir();
        var png = NoisePicture(dir, "alpha.png", 1200, 900, alpha: true);
        var pdf = PdfWithPicture(dir, png, "in.pdf");
        var output = dir.Combine("out.pdf");

        PdfImageRecompressor.Recompress(pdf, output, quality: 70, maxDpi: 300, CancellationToken.None);

        Assert.Equal(1, Kit.PageCount(output));
        var page = PixelKit.RenderPage(output, 1, 36);
        Assert.True(page.Pixels.Any(p => p < 250), "the translucent picture is still drawn");
    }

    [Fact]
    public void Small_pictures_are_left_exactly_as_they_are()
    {
        using var dir = new TempDir();
        var small = dir.Combine("small.png");
        new MagickImage(MagickColors.Red, 40, 40).Write(small);
        var b = new PdfBuilder();
        b.AddPage().Image(small, 72, 72, 40, 40);
        var pdf = b.Save(dir.Combine("in.pdf"));

        var result = PdfImageRecompressor.Recompress(pdf, dir.Combine("out.pdf"), quality: 50, maxDpi: 72, CancellationToken.None);

        Assert.Equal((1, 0), (result.ImagesFound, result.ImagesRecompressed));
    }

    [Fact]
    public void A_pdf_without_pictures_is_passed_through()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("text.pdf"), 2);

        var result = PdfImageRecompressor.Recompress(pdf, dir.Combine("out.pdf"), 70, 150, CancellationToken.None);

        Assert.Equal((0, 0), (result.ImagesFound, result.ImagesRecompressed));
        Assert.Equal(["Page 1", "Page 2"], Kit.PageTexts(dir.Combine("out.pdf")));
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("text.pdf"), 3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => PdfImageRecompressor.Recompress(pdf, dir.Combine("out.pdf"), 70, 150, cts.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Png_row_predictors_are_reversed_exactly(int filter)
    {
        // 3 rows of 4 RGB pixels, encoded forward with the chosen PNG filter, then decoded by the production code.
        const int bpp = 3;
        const int rowBytes = 4 * bpp;
        var rng = new Random(7);
        var raw = new byte[3 * rowBytes];
        rng.NextBytes(raw);

        var encoded = new byte[3 * (rowBytes + 1)];
        for (var y = 0; y < 3; y++)
        {
            encoded[y * (rowBytes + 1)] = (byte)filter;
            for (var x = 0; x < rowBytes; x++)
            {
                int left = x >= bpp ? raw[y * rowBytes + x - bpp] : 0;
                int up = y > 0 ? raw[(y - 1) * rowBytes + x] : 0;
                int upLeft = y > 0 && x >= bpp ? raw[(y - 1) * rowBytes + x - bpp] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    _ => Paeth(left, up, upLeft),
                };
                encoded[y * (rowBytes + 1) + 1 + x] = (byte)(raw[y * rowBytes + x] - predicted);
            }
        }

        Assert.Equal(raw, PdfImageRecompressor.UndoPngPredictor(encoded, rowBytes, bpp));
    }

    [Fact]
    public void A_corrupt_predictor_stream_is_rejected_rather_than_guessed()
    {
        Assert.Null(PdfImageRecompressor.UndoPngPredictor(new byte[11], 4, 1)); // length is not a multiple of the row size
        Assert.Null(PdfImageRecompressor.UndoPngPredictor([9, 1, 2, 3, 4], 4, 1)); // unknown filter type
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
