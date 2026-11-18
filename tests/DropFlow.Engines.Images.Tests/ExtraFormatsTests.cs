using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;
using Xunit.Abstractions;

namespace DropFlow.Engines.Images.Tests;

/// <summary>
/// The picture formats beyond the everyday ones: camera RAW, Photoshop, JPEG XL / 2000, Targa, QOI, DirectDraw, PCX, OpenEXR,
/// Radiance HDR, DICOM and FITS. Every format is decoded by the Magick.NET build DropFlow ships; samples are made with Magick itself
/// where it can write the format, and byte by byte (<see cref="PictureSamples"/>) for DNG and DICOM, which it only reads.
/// Canon/Nikon/Sony/Olympus/Panasonic/Fuji RAW files are decoded by the same LibRaw code path as DNG, but no sample of them can be made here.
/// </summary>
public class ExtraFormatsTests(ITestOutputHelper output)
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    private static IConverter Converter() => ImagesModule.Create(new FormatConverterTests.NoEngines()).First(c => c.Supports("convert:jpg", "png"));

    private static async Task<ConversionResult> Run(string input, string target) =>
        await Converter().RunAsync(new ConversionJob([input], Operation.Convert(target)), NoProgress, CancellationToken.None);

    private static readonly string[] RawInputs = ["dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf"];
    private static readonly string[] OtherNewInputs = ["psd", "jxl", "jp2", "tga", "qoi", "dds", "pcx", "exr", "hdr", "dcm", "fits"];
    private static readonly string[] NewOutputs = ["jxl", "jp2", "psd", "tga", "qoi"];

    [Fact]
    public void Probe_the_magick_build_reads_every_listed_input_and_writes_every_listed_output()
    {
        var formats = MagickNET.SupportedFormats.ToDictionary(f => f.Format.ToString().ToLowerInvariant());
        foreach (var f in MagickNET.SupportedFormats.Where(f => RawInputs.Concat(OtherNewInputs).Concat(NewOutputs).Contains(f.Format.ToString().ToLowerInvariant())))
            output.WriteLine($"{f.Format,-5} read={f.SupportsReading} write={f.SupportsWriting} {f.Description}");
        output.WriteLine("Delegates: " + MagickNET.Delegates);

        foreach (var ext in ImageIo.Inputs.Where(e => e != "svgz"))
            Assert.True(formats.TryGetValue(ext == "jpg" ? "jpeg" : ext, out var f) && f.SupportsReading, $"{ext} is listed as an input but this Magick build cannot read it");
        foreach (var ext in ImageIo.Writable.Where(e => e != "heic"))
            Assert.True(formats.TryGetValue(ext == "jpg" ? "jpeg" : ext, out var f) && f.SupportsWriting, $"{ext} is listed as an output but this Magick build cannot write it");
        Assert.True(formats["svgz"].SupportsReading);
    }

    [Fact]
    public void The_new_formats_are_inputs_and_outputs()
    {
        foreach (var ext in RawInputs.Concat(OtherNewInputs)) Assert.Contains(ext, ImageIo.Inputs);
        foreach (var ext in NewOutputs) Assert.Contains(ext, ImageIo.Writable);
        foreach (var ext in RawInputs.Concat(["dds", "exr", "hdr", "dcm", "fits", "pcx"])) Assert.DoesNotContain(ext, ImageIo.Writable);
    }

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
    public async Task A_picture_in_each_format_magick_writes_converts_to_png_with_its_colours(string ext)
    {
        using var dir = new TempDir();
        var input = dir.Combine($"sample.{ext}");
        using (var image = TwoColours(48, 32))
        {
            image.Format = ImageIo.FormatOf(ext);
            image.Write(input);
        }

        var result = await Run(input, "png");

        Assert.True(result.Success, result.Error);
        using var png = new MagickImage(result.Outputs[0]);
        Assert.Equal((48u, 32u), (png.Width, png.Height));
        AssertNear(png, 6, 16, (220, 40, 40), $"{ext} left half");
        AssertNear(png, 40, 16, (40, 90, 220), $"{ext} right half");
    }

    [Fact]
    public async Task A_fits_file_converts_to_png()
    {
        // FITS is a single-channel science format (astronomy): Magick writes and reads the grey level only.
        using var dir = new TempDir();
        var input = dir.Combine("nebula.fits");
        using (var image = new MagickImage(new MagickColor("#808080"), 40, 30)) { image.Format = MagickFormat.Fits; image.Write(input); }

        var result = await Run(input, "jpg");

        Assert.True(result.Success, result.Error);
        using var jpg = new MagickImage(result.Outputs[0]);
        Assert.Equal((40u, 30u), (jpg.Width, jpg.Height));
        AssertNear(jpg, 10, 10, (128, 128, 128), "fits grey");
    }

    [Fact]
    public async Task A_dng_camera_raw_file_is_developed_into_a_png()
    {
        using var dir = new TempDir();
        var dng = PictureSamples.Dng(dir.Combine("IMG_0001.dng"), 48, 32, left: (200, 30, 30), right: (30, 30, 200));

        var result = await Run(dng, "png");

        Assert.True(result.Success, result.Error);
        using var png = new MagickImage(result.Outputs[0]);
        Assert.Equal((48u, 32u), (png.Width, png.Height));
        var left = png.GetPixels().GetPixel(6, 16).ToColor()!;
        var right = png.GetPixels().GetPixel(40, 16).ToColor()!;
        Assert.True(left.R > left.B + 100, $"left half should be red after developing, got {left}");
        Assert.True(right.B > right.R + 100, $"right half should be blue after developing, got {right}");
    }

    [Fact]
    public async Task A_dicom_file_converts_to_jpg()
    {
        using var dir = new TempDir();
        var dcm = PictureSamples.Dicom(dir.Combine("scan 1.dcm"), 48, 32, leftGray: 40, rightGray: 220);

        var result = await Run(dcm, "jpg");

        Assert.True(result.Success, result.Error);
        using var jpg = new MagickImage(result.Outputs[0]);
        Assert.Equal((48u, 32u), (jpg.Width, jpg.Height));
        AssertNear(jpg, 6, 16, (40, 40, 40), "dicom left");
        AssertNear(jpg, 40, 16, (220, 220, 220), "dicom right");
    }

    [Theory]
    [InlineData("jxl", "FF0A")]                 // JPEG XL codestream
    [InlineData("jp2", "0000000C6A502020")]     // JPEG 2000 signature box
    [InlineData("psd", "38425053")]             // 8BPS
    [InlineData("qoi", "716F6966")]             // qoif
    [InlineData("tga", "")]                     // Targa has no signature at the start
    public async Task Png_converts_to_each_new_output_and_reads_back_with_the_same_pixels(string target, string magic)
    {
        using var dir = new TempDir();
        var png = dir.Combine("pic.png");
        using (var image = TwoColours(48, 32)) image.Write(png);

        var result = await Run(png, target);

        Assert.True(result.Success, result.Error);
        var produced = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine($"pic (converted).{target}"), produced);
        var head = Convert.ToHexString(File.ReadAllBytes(produced).Take(Math.Max(1, magic.Length / 2)).ToArray());
        Assert.StartsWith(magic, head, StringComparison.OrdinalIgnoreCase);

        using var back = new MagickImage(produced, new MagickReadSettings { Format = ImageIo.FormatOf(target) });
        Assert.Equal((48u, 32u), (back.Width, back.Height));
        AssertNear(back, 6, 16, (220, 40, 40), $"{target} left");
        AssertNear(back, 40, 16, (40, 90, 220), $"{target} right");
    }

    [Fact]
    public async Task Transparency_survives_in_formats_that_have_it()
    {
        using var dir = new TempDir();
        var png = dir.Combine("clear.png");
        using (var transparent = new MagickImage(MagickColors.Transparent, 8, 8)) transparent.Write(png);

        foreach (var target in new[] { "qoi", "tga", "psd" })
        {
            var result = await Run(png, target);
            Assert.True(result.Success, result.Error);
            using var back = new MagickImage(result.Outputs[0], new MagickReadSettings { Format = ImageIo.FormatOf(target) });
            Assert.True(back.HasAlpha, $"{target} lost its alpha channel");
            Assert.Equal(0, back.GetPixels().GetPixel(4, 4).ToColor()!.A);
        }
    }

    [Fact]
    public async Task A_new_format_works_with_an_arabic_file_name()
    {
        using var dir = new TempDir();
        var input = dir.Combine("لوحة الغلاف.psd");
        using (var image = TwoColours(20, 10)) { image.Format = MagickFormat.Psd; image.Write(input); }

        var result = await Run(input, "jxl");

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(dir.Combine("لوحة الغلاف (converted).jxl")));
    }

    [Fact]
    public async Task A_file_with_a_raw_extension_that_is_not_raw_fails_cleanly()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("broken.cr2", "not a camera file");

        var result = await Run(bad, "jpg");

        Assert.False(result.Success);
        Assert.Contains("broken.cr2", result.Error);
        Assert.Equal([bad], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Supports_is_truthful_for_the_new_formats()
    {
        var c = Converter();
        foreach (var input in RawInputs.Concat(OtherNewInputs))
        {
            Assert.True(c.Supports("convert:png", input), $"{input} -> png");
            Assert.Equal(input != "jxl", c.Supports("convert:jxl", input));
        }
        Assert.True(c.Supports("convert:psd", "png"));
        Assert.False(c.Supports("convert:psd", "psd"), "same format");
        Assert.False(c.Supports("convert:dng", "png"), "camera RAW is input-only");
        Assert.False(c.Supports("convert:exr", "png"), "EXR is input-only");
    }

    /// <summary>Red on the left, blue on the right (distinct colours so a channel mix-up or a wrong colour space shows).</summary>
    internal static MagickImage TwoColours(uint width, uint height)
    {
        var image = new MagickImage(new MagickColor(40, 90, 220), width, height);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(new MagickColor(220, 40, 40)).Rectangle(0, 0, width / 2.0 - 1, height));
        return image;
    }

    private static void AssertNear(MagickImage image, int x, int y, (int R, int G, int B) expected, string what, int tolerance = 12)
    {
        var c = image.GetPixels().GetPixel(x, y).ToColor()!;
        var gray = image.ChannelCount < 3;
        int r = c.R, g = gray ? c.R : c.G, b = gray ? c.R : c.B;
        Assert.True(Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance && Math.Abs(b - expected.B) <= tolerance,
            $"{what}: expected about {expected}, got ({r},{g},{b})");
    }
}
