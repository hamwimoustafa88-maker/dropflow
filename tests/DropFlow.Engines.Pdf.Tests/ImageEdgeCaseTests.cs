using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Pictures that PDF libraries commonly get wrong: gray, CMYK, 16-bit, interlaced, 1-bit, palette with transparency, progressive JPEG.</summary>
public class ImageEdgeCaseTests
{
    private static async Task<string> ToPdf(string picture)
    {
        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [picture]));
        return result.Outputs[0];
    }

    private static (int R, int G, int B) CenterPixel(string pdf)
    {
        var (w, h, rgb) = PixelKit.RenderRgb(pdf, 1, 72);
        var i = ((h / 2) * w + w / 2) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    private static void AssertClose((int R, int G, int B) actual, (int R, int G, int B) expected, int tolerance = 12)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
    }

    [Fact]
    public async Task A_gray_jpeg_keeps_its_gray_level()
    {
        using var dir = new TempDir();
        var path = dir.Combine("gray.jpg");
        using (var image = new MagickImage(new MagickColor("#808080"), 40, 40))
        {
            image.ColorSpace = ColorSpace.Gray;
            image.Write(path, MagickFormat.Jpeg);
        }

        AssertClose(CenterPixel(await ToPdf(path)), (128, 128, 128));
    }

    [Fact]
    public async Task A_cmyk_jpeg_is_converted_to_screen_colours()
    {
        using var dir = new TempDir();
        var path = dir.Combine("cmyk.jpg");
        using (var image = new MagickImage(new MagickColor("#FF0000"), 40, 40))
        {
            image.ColorSpace = ColorSpace.CMYK;
            image.Write(path, MagickFormat.Jpeg);
        }

        var (r, g, b) = CenterPixel(await ToPdf(path));
        Assert.True(r > 200 && g < 80 && b < 80, $"red expected, got {r},{g},{b}");
    }

    [Fact]
    public async Task A_sixteen_bit_png_is_embedded_as_eight_bit()
    {
        using var dir = new TempDir();
        var path = dir.Combine("deep.png");
        using (var image = new MagickImage(new MagickColor("#3366CC"), 40, 40))
        {
            image.Depth = 16;
            image.Write(path, MagickFormat.Png48);
        }

        AssertClose(CenterPixel(await ToPdf(path)), (0x33, 0x66, 0xCC));
    }

    [Fact]
    public async Task An_interlaced_png_works()
    {
        using var dir = new TempDir();
        var path = dir.Combine("interlaced.png");
        using (var image = new MagickImage(new MagickColor("#CC3333"), 40, 40))
        {
            image.Settings.Interlace = Interlace.Png;
            image.Write(path, MagickFormat.Png24);
        }

        AssertClose(CenterPixel(await ToPdf(path)), (0xCC, 0x33, 0x33));
    }

    [Fact]
    public async Task A_one_bit_png_works()
    {
        using var dir = new TempDir();
        var path = dir.Combine("bw.png");
        using (var image = new MagickImage(MagickColors.Black, 40, 40))
        {
            image.Settings.SetDefine("png:bit-depth", "1");
            image.Write(path, MagickFormat.Png8);
        }

        AssertClose(CenterPixel(await ToPdf(path)), (0, 0, 0));
    }

    [Fact]
    public async Task A_gif_with_a_transparent_palette_colour_shows_the_page_through()
    {
        using var dir = new TempDir();
        var path = dir.Combine("t.gif");
        using (var image = new MagickImage(MagickColors.Transparent, 40, 40)) image.Write(path, MagickFormat.Gif);

        AssertClose(CenterPixel(await ToPdf(path)), (255, 255, 255), tolerance: 4);
    }

    [Fact]
    public async Task A_progressive_jpeg_is_embedded()
    {
        using var dir = new TempDir();
        var path = dir.Combine("prog.jpg");
        using (var image = new MagickImage(new MagickColor("#33CC66"), 80, 60))
        {
            image.Settings.Interlace = Interlace.Plane;
            image.Write(path, MagickFormat.Jpeg);
        }

        AssertClose(CenterPixel(await ToPdf(path)), (0x33, 0xCC, 0x66));
    }

    [Fact]
    public async Task A_very_wide_picture_gets_a_page_inside_the_pdf_limits()
    {
        using var dir = new TempDir();
        var path = dir.Combine("wide.png");
        using (var image = new MagickImage(MagickColors.White, 20000, 100)) image.Write(path, MagickFormat.Png24);

        var pdf = await ToPdf(path);

        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        Assert.True(page.Width <= 14400.01);
        Assert.Equal(200.0, page.Width / page.Height, 1);
    }
}
