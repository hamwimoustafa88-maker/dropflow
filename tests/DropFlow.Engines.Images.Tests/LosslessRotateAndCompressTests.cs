using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Images.Tests;

public class LosslessRotateTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();
    private static readonly IReadOnlyList<IConverter> All = ImagesModule.Create(new FormatConverterTests.NoEngines());

    private static Task<ConversionResult> Run(string tool, string input, Dictionary<string, object?> options) =>
        All.Single(c => c.Operations.Contains(tool)).RunAsync(new ConversionJob([input], tool, options), NoProgress, CancellationToken.None);

    /// <summary>A 60x40 JPEG made of six flat 20x20 blocks of different colours: any wrong turn or mirror moves a colour.</summary>
    private static string Blocks(string path, int exifOrientation = 0)
    {
        using var img = new MagickImage(MagickColors.Black, 60, 40);
        var colors = new[] { MagickColors.Red, MagickColors.Lime, MagickColors.Blue, MagickColors.Yellow, MagickColors.Magenta, MagickColors.Cyan };
        for (var i = 0; i < 6; i++)
        {
            using var block = new MagickImage(colors[i], 20, 20);
            img.Composite(block, i % 3 * 20, i / 3 * 20, CompositeOperator.Over);
        }
        img.Quality = 100;
        img.Format = MagickFormat.Jpeg;
        if (exifOrientation > 0)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)exifOrientation);
            img.SetProfile(exif);
        }
        img.Write(path);
        return path;
    }

    private static MagickImage Displayed(string path)
    {
        var img = new MagickImage(path);
        img.AutoOrient();
        return img;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Orientation_algebra_round_trips_every_exif_value(int exif)
    {
        Assert.Equal(exif, PhotoOrientation.FromExif(exif).ToExif());
        Assert.Equal(exif, PhotoOrientation.Identity.Then(PhotoOrientation.FromExif(exif)).ToExif());
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var start in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
            foreach (var (rotate, flipH, flipV) in new[] { ("90", false, false), ("180", false, false), ("270", false, false), ("0", true, false), ("0", false, true), ("90", true, false), ("270", false, true) })
                yield return [start, rotate, flipH, flipV];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Edit_rotation_of_a_jpeg_only_changes_the_orientation_tag_and_shows_the_same_picture_as_a_real_rotation(int startOrientation, string rotate, bool flipH, bool flipV)
    {
        using var dir = new TempDir();
        var input = Blocks(dir.Combine("photo.jpg"), startOrientation);

        var r = await Run("img.edit", input, new() { ["rotate"] = rotate, ["flipH"] = flipH, ["flipV"] = flipV });

        Assert.True(r.Success, r.Error);
        var output = r.Outputs.Single();
        Assert.Equal("note.img.rotatedLossless", r.Note);
        // lossless: the compressed picture data is byte-for-byte the original's
        Assert.Equal(JpegExif.ImageData(File.ReadAllBytes(input)).ToArray(), JpegExif.ImageData(File.ReadAllBytes(output)).ToArray());

        using var expected = Displayed(input);
        if (int.Parse(rotate) is > 0 and var degrees) expected.Rotate(degrees);
        if (flipH) expected.Flop();
        if (flipV) expected.Flip();
        using var actual = Displayed(output);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.True(expected.Compare(actual, ErrorMetric.MeanAbsolute) < 0.01, $"start {startOrientation}, rotate {rotate}, flipH {flipH}, flipV {flipV}");
    }

    [Fact]
    public async Task Edit_with_other_adjustments_still_re_encodes()
    {
        using var dir = new TempDir();
        var input = Blocks(dir.Combine("photo.jpg"));
        var r = await Run("img.edit", input, new() { ["rotate"] = "90", ["brightness"] = 20d });
        Assert.True(r.Success, r.Error);
        Assert.Null(r.Note);
        Assert.NotEqual(JpegExif.ImageData(File.ReadAllBytes(input)).ToArray(), JpegExif.ImageData(File.ReadAllBytes(r.Outputs.Single())).ToArray());
    }

    [Fact]
    public async Task Rotate_tool_turns_a_jpeg_losslessly_and_a_png_by_its_pixels()
    {
        using var dir = new TempDir();
        var jpg = Blocks(dir.Combine("صورة.jpg"));
        var r = await Run("img.rotate", jpg, new() { ["rotate"] = "90" });
        Assert.True(r.Success, r.Error);
        Assert.Equal(dir.Combine("صورة (rotated).jpg"), r.Outputs.Single());
        Assert.Equal(JpegExif.ImageData(File.ReadAllBytes(jpg)).ToArray(), JpegExif.ImageData(File.ReadAllBytes(r.Outputs[0])).ToArray());
        Assert.Equal(6, JpegExif.ReadOrientation(File.ReadAllBytes(r.Outputs[0])));
        using (var shown = Displayed(r.Outputs[0])) Assert.Equal((40u, 60u), (shown.Width, shown.Height));

        var png = Samples.Png(dir.Combine("p.png"), 64, 48);
        var p = await Run("img.rotate", png, new() { ["rotate"] = "270" });
        Assert.True(p.Success, p.Error);
        using var rotated = new MagickImage(p.Outputs.Single());
        Assert.Equal((48u, 64u), (rotated.Width, rotated.Height));
        using var expected = new MagickImage(png);
        expected.Rotate(270);
        Assert.Equal(0, expected.Compare(rotated, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Rotate_never_overwrites_an_earlier_result()
    {
        using var dir = new TempDir();
        var jpg = Blocks(dir.Combine("a.jpg"));
        File.WriteAllText(dir.Combine("a (rotated).jpg"), "earlier");
        var r = await Run("img.rotate", jpg, new() { ["rotate"] = "180" });
        Assert.Equal(dir.Combine("a (rotated 2).jpg"), r.Outputs.Single());
        Assert.Equal("earlier", File.ReadAllText(dir.Combine("a (rotated).jpg")));
    }

    [Fact]
    public void Exif_orientation_is_patched_in_place_or_a_minimal_exif_block_is_added()
    {
        using var dir = new TempDir();
        var plain = File.ReadAllBytes(Blocks(dir.Combine("plain.jpg")));
        Assert.Equal(1, JpegExif.ReadOrientation(plain));
        var added = JpegExif.WithOrientation(plain, 8)!;
        Assert.Equal(8, JpegExif.ReadOrientation(added));
        using (var m = new MagickImage(added)) Assert.Equal((ImageMagick.Orientation)8, m.Orientation);

        var tagged = File.ReadAllBytes(Blocks(dir.Combine("tagged.jpg"), 3));
        var patched = JpegExif.WithOrientation(tagged, 6)!;
        Assert.Equal(tagged.Length, patched.Length);           // same size: only two bytes changed
        Assert.Equal(6, JpegExif.ReadOrientation(patched));
    }

    [Fact]
    public void Not_a_jpeg_or_a_broken_one_is_rejected()
    {
        Assert.Null(JpegExif.WithOrientation([1, 2, 3], 6));
        Assert.Null(JpegExif.WithOrientation([0xFF, 0xD8, 0xFF, 0xE1, 0x00], 6));
        Assert.Equal(1, JpegExif.ReadOrientation([0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x45]));
    }
}

public class CompressSearchTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();
    private static readonly IReadOnlyList<IConverter> All = ImagesModule.Create(new FormatConverterTests.NoEngines());

    [Fact]
    public void Quality_is_binary_searched_in_seven_steps_then_the_picture_shrinks_by_a_fifth_up_to_eight_rounds()
    {
        var calls = new List<(double Scale, uint Quality)>();
        // An impossible target: every render is bigger.
        var result = QualitySearch.Lossy((scale, q) => { calls.Add((scale, q)); return new byte[1000 + q]; }, target: 10, CancellationToken.None);

        Assert.False(result.Fits);
        Assert.All(calls.GroupBy(c => c.Scale), g => Assert.InRange(g.Count(), 6, QualitySearch.QualitySteps));   // 86 qualities need at most 7 halvings
        Assert.Contains(calls, c => c.Quality == 10);                                                                 // the search reaches the floor
        var scales = calls.Select(c => Math.Round(c.Scale, 6)).Distinct().ToList();
        Assert.Equal(8, scales.Count);
        for (var i = 0; i < scales.Count; i++) Assert.Equal(Math.Round(Math.Pow(0.8, i), 6), scales[i]);
        Assert.Equal(1010, result.Data.Length);                  // the smallest render seen is returned
    }

    [Fact]
    public void The_highest_quality_that_fits_is_found_and_no_more_rounds_run()
    {
        var calls = 0;
        // size grows with quality; anything at quality <= 63 fits
        var result = QualitySearch.Lossy((_, q) => { calls++; return new byte[q <= 63 ? 100 : 200]; }, target: 150, CancellationToken.None);
        Assert.True(result.Fits);
        Assert.Equal(100, result.Data.Length);
        Assert.InRange(calls, 1, QualitySearch.QualitySteps);
    }

    [Fact]
    public void Lossless_formats_try_full_colour_then_256_colours_per_round()
    {
        var calls = new List<(double Scale, bool Reduced)>();
        var result = QualitySearch.Lossless((s, reduced) => { calls.Add((s, reduced)); return new byte[s < 0.5 ? 10 : 1000]; }, target: 50, CancellationToken.None);
        Assert.True(result.Fits);
        Assert.Equal(10, result.Data.Length);
        Assert.Equal((1.0, false), calls[0]);
        Assert.Equal((1.0, true), calls[1]);
    }

    [Fact]
    public void The_search_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => QualitySearch.Lossy((_, _) => new byte[10], 1, cts.Token));
    }

    [Fact]
    public async Task A_result_that_would_not_be_smaller_is_never_kept_and_the_note_is_a_translatable_key()
    {
        using var dir = new TempDir();
        var png = dir.Combine("flat.png");
        using (var img = new MagickImage(MagickColors.White, 16, 16)) img.Write(png);

        var r = await All.Single(c => c.Operations.Contains("img.compress")).RunAsync(new ConversionJob([png], "img.compress", new Dictionary<string, object?> { ["mode"] = "targetKb", ["targetKb"] = 1000d }), NoProgress, CancellationToken.None);

        Assert.True(r.Success, r.Error);
        Assert.Empty(r.Outputs);
        Assert.Equal("note.img.alreadySmall", r.Note);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task A_tiny_bmp_that_png_cannot_shrink_is_left_alone()
    {
        using var dir = new TempDir();
        var bmp = dir.Combine("dot.bmp");
        // A 1x1 BMP is ~60 bytes; any PNG of it is about as big or bigger, so nothing may be written.
        using (var img = new MagickImage(MagickColors.Red, 1, 1)) { img.Format = MagickFormat.Bmp3; img.Write(bmp); }
        var r = await All.Single(c => c.Operations.Contains("img.compress")).RunAsync(new ConversionJob([bmp], "img.compress"), NoProgress, CancellationToken.None);
        Assert.True(r.Success, r.Error);
        if (r.Outputs.Count > 0) Assert.True(new FileInfo(r.Outputs[0]).Length < new FileInfo(bmp).Length);
        else Assert.Equal("note.img.alreadySmall", r.Note);
    }

    [Fact]
    public async Task An_unreachable_target_keeps_the_smallest_smaller_result_and_says_how_small()
    {
        using var dir = new TempDir();
        var jpg = dir.Combine("noise.jpg");
        // Big and pure noise, so even 8 rounds of shrinking (to about a fifth) at the lowest quality stay above 1 KB.
        using (var img = new MagickImage(MagickColors.Gray, 1600, 1200)) { img.AddNoise(NoiseType.Gaussian); img.Quality = 98; img.Write(jpg); }

        var r = await All.Single(c => c.Operations.Contains("img.compress")).RunAsync(new ConversionJob([jpg], "img.compress", new Dictionary<string, object?> { ["mode"] = "targetKb", ["targetKb"] = 1d }), NoProgress, CancellationToken.None);

        Assert.True(r.Success, r.Error);
        var output = r.Outputs.Single();
        Assert.True(new FileInfo(output).Length < new FileInfo(jpg).Length);
        Assert.StartsWith("note.img.targetNotReached|", r.Note);
        Assert.Equal((new FileInfo(output).Length / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), r.Note!.Split('|')[1]);
    }
}
