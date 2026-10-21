using DropFlow.AI.Imaging;
using DropFlow.Core.Catalog;
using DropFlow.TestSupport;
using ImageMagick;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>Every image extension the AI tools claim to accept must really decode (written by Magick, read back).</summary>
public class ImageInputsTests
{
    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("avif")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("ico")]
    public void Claimed_formats_round_trip(string ext)
    {
        using var dir = new TempDir();
        var path = dir.Combine("probe." + ext);
        using (var src = new MagickImage(MagickColors.Red, 64, 48))
        {
            src.Format = ImageInputs.FormatOf(ext)!.Value;
            src.Write(path);
        }
        Assert.Contains(ext, ImageInputs.Supported);
        using var back = ImageIo.LoadUpright(path);
        Assert.Equal(64u, back.Width);
        Assert.Equal(48u, back.Height);
    }

    [Fact]
    public void Svg_is_rasterised()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("v.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"48\"><rect width=\"64\" height=\"48\" fill=\"red\"/></svg>");
        Assert.Contains("svg", ImageInputs.Supported);
        using var img = ImageIo.LoadUpright(path);
        Assert.True(img.Width >= 64 && img.Height >= 48);
    }

    [Fact]
    public void Supported_is_the_catalogs_image_inputs_that_the_library_can_read()
    {
        var catalogInputs = ToolCatalog.LoadEmbedded().Category("image")!.Inputs;
        Assert.All(ImageInputs.Supported, ext => Assert.Contains(ext, catalogInputs));
        var readable = MagickNET.SupportedFormats.Where(f => f.SupportsReading).Select(f => f.Format).ToHashSet();
        foreach (var ext in catalogInputs)
            Assert.Equal(readable.Contains(ImageInputs.FormatOf(ext)!.Value), ImageInputs.Supported.Contains(ext));
        // Everything the wheel offers for images except possibly HEIC (needs the optional codec in the Magick build) is covered.
        Assert.All(catalogInputs.Where(e => e != "heic"), ext => Assert.Contains(ext, ImageInputs.Supported));
    }

    [Fact]
    public void Exif_rotation_is_applied_so_coordinates_match_what_the_user_sees()
    {
        using var dir = new TempDir();
        var path = TestImages.JpegRotated90(dir.Combine("rotated.jpg"), 100, 40, MagickColors.Blue);
        using var upright = ImageIo.LoadUpright(path);
        Assert.Equal(40u, upright.Width);
        Assert.Equal(100u, upright.Height);
    }

    [Fact]
    public void Arabic_file_names_open()
    {
        using var dir = new TempDir();
        var path = dir.Combine("صورة العائلة.png");
        Samples.Png(path);
        using var img = ImageIo.LoadUpright(path);
        Assert.Equal(64u, img.Width);
    }

    [Fact]
    public void Unreadable_files_raise_the_tool_exception_not_a_crash()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("broken.png", "this is not a png");
        Assert.Throws<ImageToolException>(() => ImageIo.LoadUpright(path));
        Assert.Throws<ImageToolException>(() => ImageIo.LoadUpright(dir.Combine("missing.png")));
    }
}
