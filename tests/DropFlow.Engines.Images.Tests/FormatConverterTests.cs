using DropFlow.Core.Operations;
using DropFlow.Engines.Images;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Images.Tests;

public class FormatConverterTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    private static IConverter Converter() => ImagesModule.Create(new NoEngines()).First(c => c.Supports("convert:jpg", "png"));

    internal sealed class NoEngines : IEngineProvider
    {
        public string? Locate(EngineId engine) => null;
    }

    private static async Task<ConversionResult> Run(string input, string target, Dictionary<string, object?>? options = null, CancellationToken ct = default) =>
        await Converter().RunAsync(new ConversionJob([input], Operation.Convert(target), options), NoProgress, ct);

    [Theory]
    [InlineData("jpg", "FFD8FF")]
    [InlineData("webp", "52494646")]          // RIFF
    [InlineData("bmp", "424D")]               // BM
    [InlineData("tiff", "49492A00")]          // little-endian TIFF
    [InlineData("gif", "47494638")]           // GIF8
    [InlineData("ico", "00000100")]
    public async Task Png_converts_to_each_format_with_the_right_file_signature(string target, string magic)
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"), 40, 30);

        var result = await Run(png, target);

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(dir.Path, $"pic (converted).{target}"), output);
        var head = Convert.ToHexString(File.ReadAllBytes(output).Take(magic.Length / 2).ToArray());
        Assert.StartsWith(magic, head, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_converted_image_keeps_its_pixel_size_and_content()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"), 64, 48);

        var result = await Run(png, "jpg", new() { ["quality"] = 95 });

        using var jpg = new MagickImage(result.Outputs[0]);
        Assert.Equal(64u, jpg.Width);
        Assert.Equal(48u, jpg.Height);
        using var original = new MagickImage(png);
        Assert.True(original.Compare(jpg, ErrorMetric.MeanAbsolute) < 0.02, "JPEG at quality 95 should stay visually close to the source");
    }

    [Fact]
    public async Task Never_overwrites_an_existing_result_and_never_touches_the_input()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"));
        var before = File.ReadAllBytes(png);
        dir.WriteText("pic (converted).jpg", "precious");

        var result = await Run(png, "jpg");

        Assert.Equal(Path.Combine(dir.Path, "pic (converted 2).jpg"), result.Outputs[0]);
        Assert.Equal("precious", File.ReadAllText(dir.Combine("pic (converted).jpg")));
        Assert.Equal(before, File.ReadAllBytes(png));
    }

    [Fact]
    public async Task Works_with_arabic_and_spaced_file_names()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("صورة العائلة 2026.png"));

        var result = await Run(png, "webp");

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(dir.Path, "صورة العائلة 2026 (converted).webp")));
    }

    [Fact]
    public async Task Transparency_is_flattened_onto_white_when_the_target_has_no_alpha()
    {
        using var dir = new TempDir();
        var png = dir.Combine("clear.png");
        using (var transparent = new MagickImage(MagickColors.Transparent, 8, 8)) transparent.Write(png);

        var result = await Run(png, "jpg");

        using var jpg = new MagickImage(result.Outputs[0]);
        var pixel = jpg.GetPixels().GetPixel(4, 4).ToColor()!;
        Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240, $"expected white, got {pixel}");
    }

    [Fact]
    public async Task Exif_orientation_is_applied_so_the_result_is_upright()
    {
        using var dir = new TempDir();
        var jpg = dir.Combine("rotated.jpg");
        using (var img = new MagickImage(MagickColors.Red, 40, 20))
        {
            // Real cameras write the EXIF tag; ImageMagick only persists it when the profile exists, so set both.
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);   // "rotate 90° clockwise to display"
            img.SetProfile(exif);
            img.Orientation = Orientation.RightTop;
            img.Write(jpg);
        }

        var result = await Run(jpg, "png");

        using var png = new MagickImage(result.Outputs[0]);
        Assert.Equal(20u, png.Width);
        Assert.Equal(40u, png.Height);
    }

    [Fact]
    public async Task A_multi_frame_gif_converts_its_first_frame()
    {
        using var dir = new TempDir();
        var gif = dir.Combine("anim.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 10, 10));
            frames.Add(new MagickImage(MagickColors.Blue, 10, 10));
            frames.Write(gif);
        }

        var result = await Run(gif, "png");

        Assert.True(result.Success, result.Error);
        using var check = new MagickImageCollection(result.Outputs[0]);
        Assert.Single(check);
    }

    [Fact]
    public async Task Svg_is_rasterised()
    {
        using var dir = new TempDir();
        var svg = dir.WriteText("logo.svg", """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="32"><rect width="64" height="32" fill="#4338ca"/></svg>""");

        var result = await Run(svg, "png");

        Assert.True(result.Success, result.Error);
        using var png = new MagickImage(result.Outputs[0]);
        Assert.True(png.Width >= 64);
    }

    [Fact]
    public async Task Large_images_are_shrunk_to_fit_an_ico()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("big.png"), 600, 400);

        var result = await Run(png, "ico");

        using var ico = new MagickImage(result.Outputs[0]);
        Assert.True(ico.Width <= 256 && ico.Height <= 256);
    }

    [Fact]
    public async Task A_corrupt_file_fails_cleanly_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("broken.png", "this is not a png");

        var result = await Run(bad, "jpg");

        Assert.False(result.Success);
        Assert.Contains("broken.png", result.Error);
        Assert.Equal([bad], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Cancellation_stops_before_writing_anything()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(png, "jpg", ct: cts.Token));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Supports_is_truthful()
    {
        var c = Converter();
        Assert.True(c.Supports("convert:jpg", "png"));
        Assert.True(c.Supports("convert:png", "heic"));
        Assert.True(c.Supports("convert:png", "svg"));
        Assert.False(c.Supports("convert:png", "png"), "same format");
        Assert.False(c.Supports("convert:svg", "png"), "svg is input-only");
        Assert.False(c.Supports("convert:jpg", "pdf"));
        Assert.False(c.Supports("convert:pdf", "png"), "pdf output belongs to the PDF engine");
        Assert.False(c.Supports("img.compress", "png"));
    }

    [Fact]
    public async Task Several_inputs_are_each_converted()
    {
        using var dir = new TempDir();
        var a = Samples.Png(dir.Combine("a.png"));
        var b = Samples.Png(dir.Combine("b.png"));

        var result = await Converter().RunAsync(new ConversionJob([a, b], "convert:jpg"), NoProgress, CancellationToken.None);

        Assert.Equal(2, result.Outputs.Count);
    }

    [Fact]
    public async Task Strip_metadata_removes_exif()
    {
        using var dir = new TempDir();
        var jpg = dir.Combine("tagged.jpg");
        using (var img = new MagickImage(MagickColors.Green, 16, 16))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Someone");
            img.SetProfile(exif);
            img.Write(jpg);
        }

        var kept = await Run(jpg, "png");
        var stripped = await Run(jpg, "webp", new() { ["stripMetadata"] = true });

        using (var a = new MagickImage(kept.Outputs[0])) Assert.NotNull(a.GetExifProfile());
        using (var b = new MagickImage(stripped.Outputs[0])) Assert.Null(b.GetExifProfile());
    }
}
