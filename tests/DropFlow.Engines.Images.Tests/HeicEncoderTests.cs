using DropFlow.Core.Operations;
using DropFlow.Engines.Images;
using DropFlow.Engines.Images.Heic;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Images.Tests;

/// <summary>
/// HEIC output: Magick.NET cannot write HEIC ("no encode delegate"), which the conversion matrix found on every wheel that offered it.
/// DropFlow now encodes with FFmpeg's libx265 and writes the HEIF container itself; libheif (inside Magick.NET) reads the result back.
/// </summary>
public class HeicEncoderTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    private static IReadOnlyList<IConverter> Module() => ImagesModule.Create(TestSupport.Engines.Locator);

    private static async Task<ConversionResult> Run(string operation, string input, Dictionary<string, object?>? options = null)
    {
        var converter = Module().First(c => c.Supports(operation, DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input)));
        return await converter.RunAsync(new ConversionJob([input], operation, options), NoProgress, CancellationToken.None);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Png_becomes_a_heic_file_that_a_heif_reader_opens_with_the_same_picture()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"), 64, 48);

        var result = await Run("convert:heic", png, new() { ["quality"] = 95 });

        Assert.True(result.Success, result.Error);
        var heic = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("pic (converted).heic"), heic);
        var bytes = File.ReadAllBytes(heic);
        Assert.Equal("ftypheic", System.Text.Encoding.ASCII.GetString(bytes, 4, 8));
        using var back = new MagickImage(heic);
        Assert.Equal(64u, back.Width);
        Assert.Equal(48u, back.Height);
        using var original = new MagickImage(png);
        Assert.True(original.Compare(back, ErrorMetric.MeanAbsolute) < 0.05, "HEIC at quality 95 should stay visually close to the source");
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task An_odd_sized_picture_keeps_its_exact_size()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("odd.png"), 65, 47);

        var result = await Run("convert:heic", png);

        Assert.True(result.Success, result.Error);
        using var back = new MagickImage(result.Outputs[0]);
        Assert.Equal(65u, back.Width);
        Assert.Equal(47u, back.Height);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Exif_fields_survive_and_the_orientation_tag_is_reset_because_the_pixels_are_saved_upright()
    {
        using var dir = new TempDir();
        var jpg = dir.Combine("rotated.jpg");
        using (var image = new MagickImage(MagickColors.SkyBlue, 80, 40))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Matrix Tester");
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            image.SetProfile(exif);
            image.Write(jpg, MagickFormat.Jpeg);
        }

        var result = await Run("convert:heic", jpg);

        Assert.True(result.Success, result.Error);
        using var back = new MagickImage(result.Outputs[0]);
        var exifBack = back.GetExifProfile();
        Assert.NotNull(exifBack);
        Assert.Equal("Matrix Tester", exifBack.GetValue(ExifTag.Artist)?.Value);
        Assert.NotEqual((ushort)6, exifBack.GetValue(ExifTag.Orientation)?.Value ?? 1);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Tools_that_keep_the_format_can_now_save_a_heic_photo()
    {
        var fixture = FindRepoFile(Path.Combine("tests", "samples", "heic", "RGB_8__128x128.heic"));
        using var dir = new TempDir();
        var heic = dir.Combine("photo.heic");
        File.Copy(fixture, heic);

        foreach (var (op, options) in new (string, Dictionary<string, object?>)[]
        {
            ("img.resize", new() { ["mode"] = "percent", ["percent"] = 50d }),
            ("img.rotate", new() { ["rotate"] = "90" }),
            ("img.edit", new() { ["filter"] = "grayscale" }),
            ("img.compress", new() { ["preset"] = "smallest" }),
        })
        {
            var result = await Run(op, heic, options);
            Assert.True(result.Success, $"{op}: {result.Error}");
            var output = Assert.Single(result.Outputs);
            Assert.EndsWith(".heic", output);
            using var back = new MagickImage(output);
            Assert.True(back.Width > 0 && back.Height > 0, op);
        }
    }

    [Fact]
    public void The_wheel_knows_heic_output_needs_ffmpeg()
    {
        var converter = Module().First(c => c.Supports("convert:heic", "png"));
        Assert.Equal(EngineId.Ffmpeg, converter.EngineFor("convert:heic"));
        Assert.Equal(EngineId.None, converter.EngineFor("convert:png"));
    }

    [Theory]
    [InlineData(92u, 14)]
    [InlineData(85u, 18)]
    [InlineData(50u, 35)]
    [InlineData(1u, 51)]
    public void Quality_maps_to_an_x265_crf(uint quality, int crf) => Assert.Equal(crf, HeicEncoder.CrfFor(quality));

    [Fact]
    public void Annex_b_streams_split_into_nal_units_and_emulation_prevention_is_undone()
    {
        byte[] stream = [0, 0, 0, 1, 0x40, 0x01, 0xAA, 0, 0, 1, 0x42, 0x01, 0x00, 0x00, 0x03, 0x01, 0x00];
        var nals = HeifContainer.SplitNals(stream);
        Assert.Equal(2, nals.Count);
        Assert.Equal(32, HeifContainer.NalType(nals[0])); // VPS
        Assert.Equal(33, HeifContainer.NalType(nals[1])); // SPS
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, HeifContainer.Rbsp(nals[1]));
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
