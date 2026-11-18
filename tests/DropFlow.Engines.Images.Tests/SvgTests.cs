using System.IO.Compression;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Images.Tests;

/// <summary>SVG and SVGZ: gzip in both directions, SVGZ as a picture input, and Arabic text inside an SVG.</summary>
public class SvgTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    private const string Drawing = """<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect width="80" height="40" fill="#e11d48"/><circle cx="40" cy="20" r="12" fill="#ffffff"/></svg>""";

    private static IConverter For(string operation, string ext) =>
        ImagesModule.Create(new FormatConverterTests.NoEngines()).Single(c => c.Supports(operation, ext));

    private static Task<ConversionResult> Run(string operation, string input) =>
        For(operation, Path.GetExtension(input).TrimStart('.').ToLowerInvariant()).RunAsync(new ConversionJob([input], operation), NoProgress, CancellationToken.None);

    private static string WriteSvgz(TempDir dir, string name, string svg)
    {
        var path = dir.Combine(name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes(svg));
        return path;
    }

    [Fact]
    public async Task Svg_to_svgz_and_back_gives_the_same_bytes()
    {
        using var dir = new TempDir();
        var svg = dir.WriteText("logo.svg", Drawing);

        var zipped = await Run("convert:svgz", svg);
        Assert.True(zipped.Success, zipped.Error);
        Assert.Equal(dir.Combine("logo (converted).svgz"), zipped.Outputs[0]);
        Assert.Equal(new byte[] { 0x1F, 0x8B }, File.ReadAllBytes(zipped.Outputs[0]).Take(2).ToArray());

        var unzipped = await Run("convert:svg", zipped.Outputs[0]);
        Assert.True(unzipped.Success, unzipped.Error);
        Assert.Equal(dir.Combine("logo (converted) (converted).svg"), unzipped.Outputs[0]);
        Assert.Equal(File.ReadAllBytes(svg), File.ReadAllBytes(unzipped.Outputs[0]));
    }

    [Fact]
    public async Task An_svgz_converts_to_png_like_the_svg_it_holds()
    {
        using var dir = new TempDir();
        var svgz = WriteSvgz(dir, "شعار.svgz", Drawing);

        var result = await Run("convert:png", svgz);

        Assert.True(result.Success, result.Error);
        using var png = new MagickImage(result.Outputs[0]);
        Assert.True(png.Width >= 80);
        var corner = png.GetPixels().GetPixel(2, 2).ToColor()!;
        var centre = png.GetPixels().GetPixel((int)png.Width / 2, (int)png.Height / 2).ToColor()!;
        Assert.True(corner.R > 200 && corner.G < 60, $"corner should be the red background, got {corner}");
        Assert.True(centre.R > 240 && centre.G > 240 && centre.B > 240, $"centre should be the white circle, got {centre}");
    }

    [Fact]
    public async Task A_file_named_svgz_that_is_not_gzip_fails_cleanly()
    {
        using var dir = new TempDir();
        var fake = dir.WriteText("fake.svgz", Drawing);   // plain text, not compressed

        var result = await Run("convert:svg", fake);

        Assert.False(result.Success);
        Assert.Contains("fake.svgz", result.Error);
        Assert.Equal([fake], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Gzip_that_holds_something_other_than_svg_is_refused()
    {
        using var dir = new TempDir();
        var notSvg = WriteSvgz(dir, "notes.svgz", "just some text");

        var result = await Run("convert:svg", notSvg);

        Assert.False(result.Success);
        Assert.Contains("notes.svgz", result.Error);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task A_text_file_renamed_to_svg_is_not_zipped()
    {
        using var dir = new TempDir();
        var txt = dir.WriteText("readme.svg", "hello");

        var result = await Run("convert:svgz", txt);

        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Supports_is_truthful()
    {
        var c = For("convert:svgz", "svg");
        Assert.True(c.Supports("convert:svg", "svgz"));
        Assert.False(c.Supports("convert:svg", "svg"), "same format");
        Assert.False(c.Supports("convert:svgz", "svgz"), "same format");
        Assert.False(c.Supports("convert:svgz", "png"), "tracing is the tracer's job");
        Assert.False(c.Supports("convert:png", "svgz"));
    }

    /// <summary>
    /// SVG text goes through librsvg and Pango (HarfBuzz), so Arabic letters join. The same letters with a zero-width non-joiner between
    /// each pair cannot join: if shaping works the two drawings differ and the joined word is narrower; without shaping they would be the same.
    /// </summary>
    [Fact]
    public async Task Arabic_text_in_an_svg_is_drawn_with_joined_letters()
    {
        using var dir = new TempDir();
        const string word = "سلام عليكم";
        var isolated = string.Join("‌", word.ToCharArray());
        string Svg(string text) =>
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="400" height="90"><rect width="400" height="90" fill="#fff"/><text x="200" y="60" font-size="40" font-family="Arial, Tahoma, DejaVu Sans, sans-serif" text-anchor="middle" direction="rtl" fill="#000">{text}</text></svg>""";

        var joined = await Run("convert:png", dir.WriteText("joined.svg", Svg(word)));
        var apart = await Run("convert:png", dir.WriteText("apart.svg", Svg(isolated)));
        Assert.True(joined.Success, joined.Error);
        Assert.True(apart.Success, apart.Error);

        var (joinedWidth, joinedInk) = InkExtent(joined.Outputs[0]);
        var (apartWidth, apartInk) = InkExtent(apart.Outputs[0]);
        Assert.True(joinedInk > 200, "the Arabic text was not drawn at all (no font with Arabic letters?)");
        Assert.True(apartInk > 200);
        Assert.True(joinedWidth < apartWidth, $"joined text should be narrower than isolated letters ({joinedWidth} vs {apartWidth} px)");
    }

    private static (int Width, int Ink) InkExtent(string png)
    {
        using var image = new MagickImage(png);
        var px = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        int w = (int)image.Width, minX = int.MaxValue, maxX = -1, ink = 0;
        for (var i = 0; i < px.Length / 3; i++)
        {
            if (px[i * 3] > 128) continue;
            ink++;
            minX = Math.Min(minX, i % w);
            maxX = Math.Max(maxX, i % w);
        }
        return (maxX < 0 ? 0 : maxX - minX + 1, ink);
    }
}
