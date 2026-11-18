using System.IO.Compression;
using System.Text;
using DropFlow.Engines.Pdf.Tools;
using DropFlow.TestSupport;
using ImageMagick;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// The picture formats beyond the everyday ones become PDF pages too: camera RAW (a hand-made DNG), Photoshop, JPEG XL / 2000, Targa
/// (no signature: the extension tells Magick), QOI, DirectDraw, PCX, OpenEXR, Radiance HDR, DICOM, FITS and compressed SVG.
/// </summary>
public class MorePictureFormatsToPdfTests
{
    [Theory]
    [InlineData("psd")]
    [InlineData("jxl")]
    [InlineData("jp2")]
    [InlineData("tga")]
    [InlineData("qoi")]
    [InlineData("dds")]
    [InlineData("pcx")]
    [InlineData("exr")]
    [InlineData("hdr")]
    [InlineData("fits")]
    public async Task A_picture_magick_can_write_becomes_a_one_page_pdf(string ext)
    {
        using var dir = new TempDir();
        var input = dir.Combine($"picture.{ext}");
        using (var image = new MagickImage(new MagickColor(200, 60, 40), 64, 48))
        {
            image.Format = Enum.Parse<MagickFormat>(ext, ignoreCase: true);
            image.Write(input);
        }

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [input]));

        AssertOnePage(result.Outputs[0], 64, 48);
        if (ext is "fits") return;   // FITS keeps a single grey channel
        var rgb = PixelKit.RenderRgb(result.Outputs[0], 1, dpi: 72);
        var (r, g, b) = (rgb.Rgb[(24 * rgb.Width + 32) * 3], rgb.Rgb[(24 * rgb.Width + 32) * 3 + 1], rgb.Rgb[(24 * rgb.Width + 32) * 3 + 2]);
        Assert.True(Math.Abs(r - 200) < 16 && Math.Abs(g - 60) < 16 && Math.Abs(b - 40) < 16, $"{ext}: page colour ({r},{g},{b})");
    }

    [Fact]
    public async Task A_dng_camera_raw_file_becomes_a_pdf_page_of_the_developed_photo()
    {
        using var dir = new TempDir();
        var dng = PictureSamples.Dng(dir.Combine("DSC_0001.dng"), 48, 32);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [dng]));

        // Sniffing the bytes alone would read the DNG as a plain TIFF; the result must be the developed 48x32 photo.
        AssertOnePage(result.Outputs[0], 48, 32);
        var rgb = PixelKit.RenderRgb(result.Outputs[0], 1, dpi: 72);
        var left = (rgb.Rgb[(16 * rgb.Width + 6) * 3], rgb.Rgb[(16 * rgb.Width + 6) * 3 + 2]);
        Assert.True(left.Item1 > left.Item2 + 100, $"left half should be red, got R={left.Item1} B={left.Item2}");
    }

    [Fact]
    public async Task A_dicom_file_becomes_a_pdf_page()
    {
        using var dir = new TempDir();
        var dcm = PictureSamples.Dicom(dir.Combine("x-ray.dcm"), 48, 32);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [dcm]));

        AssertOnePage(result.Outputs[0], 48, 32);
    }

    [Fact]
    public async Task A_compressed_svg_becomes_a_pdf_page()
    {
        using var dir = new TempDir();
        var svgz = dir.Combine("logo.svgz");
        using (var file = File.Create(svgz))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            gzip.Write(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'><rect width='40' height='30' fill='black'/></svg>"));

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [svgz]));

        Assert.Equal(1, Kit.PageCount(result.Outputs[0]));
    }

    [Fact]
    public void Every_new_picture_format_is_an_input()
    {
        foreach (var ext in new[] { "svgz", "psd", "jxl", "jp2", "tga", "qoi", "dds", "pcx", "exr", "hdr", "dcm", "fits", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf" })
            Assert.Contains(ext, ImageToPdfTools.Inputs);
    }

    private static void AssertOnePage(string pdf, double width, double height)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = Assert.Single(doc.GetPages());
        Assert.Equal((width, height), (Math.Round(page.Width, 1), Math.Round(page.Height, 1)));
    }
}
