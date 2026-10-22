using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Engines.Images;
using DropFlow.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Images.Tests;

public class ImageToolsTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();
    private static readonly IReadOnlyList<IConverter> All = ImagesModule.Create(new FormatConverterTests.NoEngines());

    private static IConverter Tool(string id) => All.Single(c => c.Operations.Contains(id));

    private static Task<ConversionResult> Run(string tool, string[] inputs, Dictionary<string, object?>? options = null, CancellationToken ct = default) =>
        Tool(tool).RunAsync(new ConversionJob(inputs, tool, options), NoProgress, ct);

    /// <summary>A noisy JPEG: noise does not compress well, so the size is predictable and large.</summary>
    private static string NoisyJpeg(string path, uint w = 500, uint h = 400, uint quality = 98)
    {
        using var img = new MagickImage(MagickColors.Gray, w, h);
        img.AddNoise(NoiseType.Gaussian);
        img.Quality = quality;
        img.Write(path);
        return path;
    }

    private static double MeanAbsoluteDifference(string a, string b)
    {
        using var x = new MagickImage(a);
        using var y = new MagickImage(b);
        return x.Compare(y, ErrorMetric.MeanAbsolute);
    }

    // ---------- integrity ----------

    [Fact]
    public void Every_operation_the_module_claims_exists_in_the_catalog()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var imageOutputs = catalog.Category("image")!.Outputs;
        foreach (var op in All.SelectMany(c => c.Operations))
        {
            if (Operation.IsConvert(op)) Assert.Contains(Operation.TargetOf(op), imageOutputs);
            else Assert.NotNull(catalog.Tool(op));
        }
    }

    [Fact]
    public void Every_tool_that_needs_options_declares_them_with_defaults()
    {
        foreach (var op in All.SelectMany(c => c.Operations).Where(o => !Operation.IsConvert(o)))
        {
            var options = Tool(op).OptionsFor(op);
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Key)));
            Assert.Equal(options.Count, options.Select(o => o.Key).Distinct().Count());
        }
    }

    // ---------- compress ----------

    [Fact]
    public async Task Compress_preset_makes_a_jpeg_smaller_and_keeps_the_original()
    {
        using var dir = new TempDir();
        var jpg = NoisyJpeg(dir.Combine("photo.jpg"));
        var before = File.ReadAllBytes(jpg);

        var r = await Run("img.compress", [jpg], new() { ["mode"] = "preset", ["preset"] = "smallest" });

        Assert.True(r.Success, r.Error);
        Assert.Equal(Path.Combine(dir.Path, "photo (compressed).jpg"), r.Outputs.Single());
        Assert.True(new FileInfo(r.Outputs[0]).Length < before.Length / 2);
        Assert.Equal(before, File.ReadAllBytes(jpg));
    }

    [Fact]
    public async Task Compress_to_a_target_size_lands_at_or_under_the_target()
    {
        using var dir = new TempDir();
        var jpg = NoisyJpeg(dir.Combine("photo.jpg"));
        var original = new FileInfo(jpg).Length;
        var targetKb = original / 1024 / 4;

        var r = await Run("img.compress", [jpg], new() { ["mode"] = "targetKb", ["targetKb"] = targetKb });

        Assert.True(r.Success, r.Error);
        Assert.True(new FileInfo(r.Outputs[0]).Length <= targetKb * 1024, $"{new FileInfo(r.Outputs[0]).Length} > {targetKb * 1024}");
    }

    [Fact]
    public async Task Compress_by_percent_reduces_the_size_by_at_least_that_much()
    {
        using var dir = new TempDir();
        var jpg = NoisyJpeg(dir.Combine("photo.jpg"));
        var original = new FileInfo(jpg).Length;

        var r = await Run("img.compress", [jpg], new() { ["mode"] = "percent", ["percent"] = 60 });

        Assert.True(new FileInfo(r.Outputs[0]).Length <= original * 0.4);
    }

    [Fact]
    public async Task Compress_that_cannot_help_writes_nothing_and_says_so()
    {
        using var dir = new TempDir();
        var png = dir.Combine("flat.png");
        using (var img = new MagickImage(MagickColors.White, 16, 16)) img.Write(png);

        var r = await Run("img.compress", [png], new() { ["mode"] = "preset", ["preset"] = "best" });

        Assert.True(r.Success);
        Assert.Empty(r.Outputs);
        Assert.Equal("note.img.alreadySmall", r.Note);   // a resource key: the app shows it in the user's language
    }

    [Fact]
    public async Task Compress_can_cap_the_width()
    {
        using var dir = new TempDir();
        var jpg = NoisyJpeg(dir.Combine("photo.jpg"), 800, 600);

        var r = await Run("img.compress", [jpg], new() { ["maxWidth"] = 400 });

        using var result = new MagickImage(r.Outputs[0]);
        Assert.Equal(400u, result.Width);
    }

    [Fact]
    public async Task A_bmp_is_compressed_into_a_png()
    {
        using var dir = new TempDir();
        var bmp = dir.Combine("big.bmp");
        using (var img = new MagickImage(MagickColors.SteelBlue, 300, 300)) img.Write(bmp);

        var r = await Run("img.compress", [bmp]);

        Assert.EndsWith("big (compressed).png", r.Outputs.Single());
        Assert.True(new FileInfo(r.Outputs[0]).Length < new FileInfo(bmp).Length);
    }

    [Fact]
    public void Compress_only_supports_formats_it_can_really_shrink()
    {
        var c = Tool("img.compress");
        Assert.True(c.Supports("img.compress", "jpg"));
        Assert.True(c.Supports("img.compress", "png"));
        Assert.False(c.Supports("img.compress", "gif"));
        Assert.False(c.Supports("img.compress", "svg"));
    }

    // ---------- metadata ----------

    private static string JpegWithExif(string path, bool gps)
    {
        using var img = new MagickImage(MagickColors.Teal, 40, 30);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Artist, "Original Artist");
        exif.SetValue(ExifTag.Make, "TestCam");
        if (gps)
        {
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(40, 1), new Rational(26, 1), new Rational(46, 1)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(79, 1), new Rational(58, 1), new Rational(56, 1)]);
        }
        img.SetProfile(exif);
        img.Write(path);
        return path;
    }

    [Fact]
    public async Task Metadata_edit_writes_the_fields_and_keeps_the_others()
    {
        using var dir = new TempDir();
        var jpg = JpegWithExif(dir.Combine("p.jpg"), gps: false);

        var r = await Run("img.metadata", [jpg], new() { ["mode"] = "edit", ["artist"] = "Moustafa", ["copyright"] = "© 2026" });

        using var result = new MagickImage(r.Outputs.Single());
        var exif = result.GetExifProfile()!;
        Assert.Equal("Moustafa", exif.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal("© 2026", exif.GetValue(ExifTag.Copyright)!.Value);
        Assert.Equal("TestCam", exif.GetValue(ExifTag.Make)!.Value);
    }

    [Fact]
    public async Task Metadata_with_no_fields_changes_nothing()
    {
        using var dir = new TempDir();
        var r = await Run("img.metadata", [JpegWithExif(dir.Combine("p.jpg"), false)], new() { ["mode"] = "edit" });
        Assert.Empty(r.Outputs);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public async Task Metadata_strip_removes_all_exif_including_gps()
    {
        using var dir = new TempDir();
        var jpg = JpegWithExif(dir.Combine("p.jpg"), gps: true);

        var r = await Run("img.metadata", [jpg], new() { ["mode"] = "strip" });

        using var result = new MagickImage(r.Outputs.Single());
        Assert.Null(result.GetExifProfile());
        Assert.EndsWith("p (cleaned).jpg", r.Outputs[0]);
    }

    // ---------- strip gps ----------

    [Fact]
    public async Task Strip_gps_removes_the_location_but_keeps_other_exif()
    {
        using var dir = new TempDir();
        var jpg = JpegWithExif(dir.Combine("trip.jpg"), gps: true);
        using (var before = new MagickImage(jpg))
            Assert.Contains(before.GetExifProfile()!.Values, v => v.Tag.ToString().StartsWith("GPS"));

        var r = await Run("img.strip-gps", [jpg]);

        Assert.True(r.Success, r.Error);
        using var after = new MagickImage(r.Outputs.Single());
        var exif = after.GetExifProfile()!;
        Assert.DoesNotContain(exif.Values, v => v.Tag.ToString().StartsWith("GPS"));
        Assert.Equal("Original Artist", exif.GetValue(ExifTag.Artist)!.Value);
        Assert.EndsWith("trip (no location).jpg", r.Outputs[0]);
    }

    [Fact]
    public async Task Strip_gps_on_a_clean_photo_writes_nothing()
    {
        using var dir = new TempDir();
        var r = await Run("img.strip-gps", [JpegWithExif(dir.Combine("clean.jpg"), gps: false)]);
        Assert.True(r.Success);
        Assert.Empty(r.Outputs);
        Assert.Equal("No location data was found.", r.Note);
    }

    // ---------- crop ----------

    [Theory]
    [InlineData(400, 300, "1:1", 50, 0, 300, 300)]
    [InlineData(400, 300, "16:9", 0, 37, 400, 225)]
    [InlineData(300, 400, "16:9", 0, 115, 300, 169)]
    [InlineData(400, 300, "4:3", 0, 0, 400, 300)]
    public void Centered_ratio_crops_are_centred_and_exact(int w, int h, string ratio, int x, int y, int cw, int ch) =>
        Assert.Equal((x, y, cw, ch), CropTool.CenteredRatio(w, h, ratio));

    [Fact]
    public async Task Crop_to_an_aspect_ratio_gives_that_shape()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("wide.png"), 200, 100);

        var r = await Run("img.crop", [png], new() { ["aspect"] = "1:1" });

        using var result = new MagickImage(r.Outputs.Single());
        Assert.Equal((100u, 100u), (result.Width, result.Height));
        Assert.EndsWith("wide (cropped).png", r.Outputs[0]);
    }

    [Fact]
    public async Task Crop_to_an_exact_rectangle_is_clamped_to_the_picture()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"), 100, 80);

        var r = await Run("img.crop", [png], new() { ["x"] = 60, ["y"] = 50, ["width"] = 500, ["height"] = 500 });

        using var result = new MagickImage(r.Outputs.Single());
        Assert.Equal((40u, 30u), (result.Width, result.Height));
    }

    [Fact]
    public async Task Crop_with_nothing_chosen_explains_what_to_do()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"));
        var r = await Run("img.crop", [png], new() { ["aspect"] = "free" });
        Assert.False(r.Success);
        Assert.Contains("aspect ratio", r.Error);
    }

    // ---------- resize ----------

    [Theory]
    [InlineData(400u, 200u, 50.0, 0, 0, true, true, 200u, 100u)]      // percent
    [InlineData(400u, 200u, 50.0, 100, 0, true, true, 100u, 50u)]     // width only
    [InlineData(400u, 200u, 50.0, 0, 50, true, true, 100u, 50u)]      // height only
    [InlineData(400u, 200u, 50.0, 100, 100, true, true, 100u, 50u)]   // fit inside the box
    [InlineData(400u, 200u, 50.0, 100, 100, false, true, 100u, 100u)] // exact, distorted
    [InlineData(400u, 200u, 50.0, 800, 0, true, true, 400u, 200u)]    // never upscale by default
    [InlineData(400u, 200u, 50.0, 800, 0, true, false, 800u, 400u)]   // unless allowed
    public void Resize_target_sizes(uint w, uint h, double percent, int tw, int th, bool keep, bool noUp, uint ew, uint eh) =>
        Assert.Equal((ew, eh), ResizeTool.TargetSize(w, h, percent, tw, th, keep, noUp));

    [Fact]
    public async Task Resize_by_percent_produces_the_new_size()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"), 100, 60);

        var r = await Run("img.resize", [png], new() { ["percent"] = 50 });

        using var result = new MagickImage(r.Outputs.Single());
        Assert.Equal((50u, 30u), (result.Width, result.Height));
    }

    [Fact]
    public async Task Resize_to_the_same_size_is_a_no_op_with_a_note()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"), 100, 60);
        var r = await Run("img.resize", [png], new() { ["width"] = 100, ["height"] = 60 });
        Assert.Empty(r.Outputs);
        Assert.NotNull(r.Note);
    }

    // ---------- edit ----------

    [Fact]
    public async Task Edit_grayscale_makes_every_pixel_neutral()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("c.png"), 20, 20);

        var r = await Run("img.edit", [png], new() { ["filter"] = "grayscale" });

        using var result = new MagickImage(r.Outputs.Single());
        var p = result.GetPixels().GetPixel(7, 11).ToColor()!;
        Assert.True(Math.Abs(p.R - p.G) <= 2 && Math.Abs(p.G - p.B) <= 2, p.ToString());
    }

    [Fact]
    public async Task Edit_rotation_swaps_the_dimensions()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("c.png"), 40, 20);
        var r = await Run("img.edit", [png], new() { ["rotate"] = "90" });
        using var result = new MagickImage(r.Outputs.Single());
        Assert.Equal((20u, 40u), (result.Width, result.Height));
    }

    [Fact]
    public async Task Edit_brightness_makes_the_picture_lighter()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("c.png"), 30, 30);
        var r = await Run("img.edit", [png], new() { ["brightness"] = 40 });
        using var before = new MagickImage(png);
        using var after = new MagickImage(r.Outputs.Single());
        Assert.True(after.Statistics().Composite().Mean > before.Statistics().Composite().Mean);
    }

    [Fact]
    public async Task Edit_with_no_adjustments_does_nothing()
    {
        using var dir = new TempDir();
        var r = await Run("img.edit", [Samples.Png(dir.Combine("c.png"))]);
        Assert.Empty(r.Outputs);
        Assert.Equal("No adjustment chosen.", r.Note);
    }

    // ---------- annotate ----------

    [Fact]
    public async Task Annotate_draws_a_filled_rectangle_where_asked()
    {
        using var dir = new TempDir();
        var png = dir.Combine("blank.png");
        using (var img = new MagickImage(MagickColors.White, 100, 100)) img.Write(png);
        const string items = """[{"type":"rect","x":10,"y":10,"w":30,"h":30,"color":"#ff0000","fill":true}]""";

        var r = await Run("img.annotate", [png], new() { ["items"] = items });

        using var result = new MagickImage(r.Outputs.Single());
        var inside = result.GetPixels().GetPixel(25, 25).ToColor()!;
        var outside = result.GetPixels().GetPixel(80, 80).ToColor()!;
        Assert.True(inside.R > 200 && inside.G < 60, $"inside was {inside}");
        Assert.True(outside.G > 240, $"outside was {outside}");
    }

    [Fact]
    public async Task Annotate_supports_arrows_ellipses_numbers_and_text()
    {
        using var dir = new TempDir();
        var png = dir.Combine("blank.png");
        using (var img = new MagickImage(MagickColors.White, 200, 120)) img.Write(png);
        const string items = """
            [{"type":"arrow","x":10,"y":10,"x2":90,"y2":60,"color":"#0000ff"},
             {"type":"ellipse","x":100,"y":10,"w":60,"h":40,"color":"#00aa00"},
             {"type":"number","x":30,"y":90,"n":1,"color":"#ef4444"},
             {"type":"text","x":70,"y":80,"text":"Look","size":28,"color":"#000000"}]
            """;

        var r = await Run("img.annotate", [png], new() { ["items"] = items });

        Assert.True(r.Success, r.Error);
        Assert.True(MeanAbsoluteDifference(png, r.Outputs[0]) > 0.001, "annotations should change the picture");
    }

    [Fact]
    public async Task Annotate_rejects_bad_json_and_unknown_shapes_clearly()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"));
        var bad = await Run("img.annotate", [png], new() { ["items"] = "{ nope" });
        var unknown = await Run("img.annotate", [png], new() { ["items"] = """[{"type":"hexagon"}]""" });

        Assert.False(bad.Success);
        Assert.Contains("not valid JSON", bad.Error);
        Assert.False(unknown.Success);
        Assert.Contains("hexagon", unknown.Error);
    }

    [Fact]
    public async Task Annotate_overlay_images_are_composited_so_arabic_text_can_be_pre_rendered()
    {
        using var dir = new TempDir();
        var png = dir.Combine("base.png");
        using (var img = new MagickImage(MagickColors.White, 80, 80)) img.Write(png);
        var overlay = dir.Combine("label.png");
        using (var o = new MagickImage(MagickColors.Red, 20, 20)) o.Write(overlay);

        var r = await Run("img.annotate", [png], new() { ["items"] = $$"""[{"type":"image","x":10,"y":10,"path":{{System.Text.Json.JsonSerializer.Serialize(overlay)}}}]""" });

        Assert.True(r.Success, r.Error);
        using var result = new MagickImage(r.Outputs.Single());
        var px = result.GetPixels().GetPixel(15, 15).ToColor()!;
        Assert.True(px.R > 200 && px.G < 50, $"overlay pixel was {px}");
    }

    [Fact]
    public async Task Annotating_a_black_and_white_photo_keeps_the_annotation_colour()
    {
        using var dir = new TempDir();
        var gray = dir.Combine("bw.png");
        using (var g = new MagickImage(MagickColors.White, 60, 60)) { g.ColorType = ColorType.Grayscale; g.Write(gray); }
        using (var check = new MagickImage(gray)) Assert.Equal(ColorSpace.Gray, check.ColorSpace);   // the fixture really is grayscale
        var overlay = dir.Combine("red.png");
        using (var o = new MagickImage(MagickColors.Red, 10, 10)) o.Write(overlay);

        var r = await Run("img.annotate", [gray], new() { ["items"] = $$"""[{"type":"image","x":5,"y":5,"path":{{System.Text.Json.JsonSerializer.Serialize(overlay)}}}]""" });

        using var result = new MagickImage(r.Outputs.Single());
        var px = result.GetPixels().GetPixel(8, 8).ToColor()!;
        Assert.True(px.R > 200 && px.G < 60 && px.B < 60, $"expected red on a grayscale base, got {px}");
    }

    // ---------- background ----------

    [Theory]
    [InlineData(100u, 50u, 0.0, "original", 100u, 50u)]
    [InlineData(100u, 50u, 10.0, "original", 120u, 60u)]
    [InlineData(100u, 50u, 0.0, "1:1", 100u, 100u)]
    [InlineData(100u, 50u, 10.0, "16:9", 120u, 68u)]    // 120x60 padded picture, widened to 16:9
    public void Canvas_sizes(uint w, uint h, double pad, string aspect, uint ew, uint eh) =>
        Assert.Equal((ew, eh), BackgroundTool.CanvasSize(w, h, pad, aspect));

    [Fact]
    public async Task Background_puts_the_picture_on_a_coloured_canvas()
    {
        using var dir = new TempDir();
        var png = dir.Combine("logo.png");
        using (var img = new MagickImage(MagickColors.Red, 100, 100)) img.Write(png);

        var r = await Run("img.background", [png], new() { ["background"] = "#0000ff", ["padding"] = 20, ["aspect"] = "16:9" });

        using var result = new MagickImage(r.Outputs.Single());
        Assert.Equal((uint)Math.Round(result.Height * 16.0 / 9), result.Width);
        var corner = result.GetPixels().GetPixel(2, 2).ToColor()!;
        var center = result.GetPixels().GetPixel((int)result.Width / 2, (int)result.Height / 2).ToColor()!;
        Assert.True(corner.B > 200 && corner.R < 50, $"corner {corner}");
        Assert.True(center.R > 200 && center.B < 50, $"center {center}");
    }

    [Fact]
    public async Task Background_gradient_changes_across_the_canvas_and_radius_rounds_the_picture()
    {
        using var dir = new TempDir();
        var png = dir.Combine("sq.png");
        using (var img = new MagickImage(MagickColors.White, 100, 100)) img.Write(png);

        var r = await Run("img.background", [png], new() { ["background"] = "gradient:#000000:#ffffff", ["padding"] = 30, ["radius"] = 50 });

        using var result = new MagickImage(r.Outputs.Single());
        var top = result.GetPixels().GetPixel(1, 1).ToColor()!;
        var bottom = result.GetPixels().GetPixel(1, (int)result.Height - 2).ToColor()!;
        Assert.NotEqual(top.R, bottom.R);
        // radius 50% of the shorter side makes a circle: the picture's own corner must show the background, not white
        var pictureCorner = result.GetPixels().GetPixel((int)(result.Width - 100) / 2 + 1, (int)(result.Height - 100) / 2 + 1).ToColor()!;
        Assert.True(pictureCorner.R < 250, $"corner of the picture should be rounded away, got {pictureCorner}");
    }

    // ---------- redact ----------

    [Fact]
    public void Redact_regions_parse_and_reject_garbage()
    {
        Assert.Equal([(1, 2, 3, 4), (5, 6, 7, 8)], RedactTool.ParseRegions("1,2,3,4; 5,6,7,8"));
        Assert.Throws<ArgumentException>(() => RedactTool.ParseRegions("1,2,3"));
        Assert.Throws<ArgumentException>(() => RedactTool.ParseRegions("a,b,c,d"));
        Assert.Throws<ArgumentException>(() => RedactTool.ParseRegions("1,2,0,4"));
        Assert.Empty(RedactTool.ParseRegions(""));
    }

    [Fact]
    public async Task Redact_solid_covers_the_area_and_leaves_the_rest_alone()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"), 60, 60);

        var r = await Run("img.redact", [png], new() { ["regions"] = "10,10,20,20", ["style"] = "solid", ["color"] = "#000000" });

        using var result = new MagickImage(r.Outputs.Single());
        using var original = new MagickImage(png);
        Assert.Equal(0, result.GetPixels().GetPixel(15, 15).ToColor()!.R);
        Assert.Equal(original.GetPixels().GetPixel(50, 50).ToColor()!.R, result.GetPixels().GetPixel(50, 50).ToColor()!.R);
        Assert.EndsWith("p (redacted).png", r.Outputs[0]);
    }

    [Theory]
    [InlineData("blur")]
    [InlineData("pixelate")]
    public async Task Redact_blur_and_pixelate_destroy_the_detail_inside_the_area(string style)
    {
        using var dir = new TempDir();
        var jpg = NoisyJpeg(dir.Combine("n.jpg"), 120, 120);

        var r = await Run("img.redact", [jpg], new() { ["regions"] = "20,20,60,60", ["style"] = style });

        using var original = new MagickImage(jpg);
        using var result = new MagickImage(r.Outputs.Single());
        using var originalArea = (MagickImage)original.Clone();
        originalArea.Crop(new MagickGeometry(20, 20, 60, 60));
        using var resultArea = (MagickImage)result.Clone();
        resultArea.Crop(new MagickGeometry(20, 20, 60, 60));
        Assert.True(resultArea.Statistics().Composite().StandardDeviation < originalArea.Statistics().Composite().StandardDeviation / 2,
            "the covered area should be much smoother than the noisy original");
    }

    [Fact]
    public async Task Redact_removes_all_metadata_so_no_thumbnail_leaks_the_area()
    {
        using var dir = new TempDir();
        var jpg = JpegWithExif(dir.Combine("p.jpg"), gps: true);
        var r = await Run("img.redact", [jpg], new() { ["regions"] = "0,0,10,10" });
        using var result = new MagickImage(r.Outputs.Single());
        Assert.Null(result.GetExifProfile());
    }

    [Fact]
    public async Task Redact_with_no_region_or_an_invalid_one_is_handled()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"));
        var none = await Run("img.redact", [png], new() { ["regions"] = "" });
        var bad = await Run("img.redact", [png], new() { ["regions"] = "1,2" });
        Assert.True(none.Success);
        Assert.Empty(none.Outputs);
        Assert.False(bad.Success);
    }

    // ---------- collage ----------

    [Fact]
    public void Collage_layouts()
    {
        var (grid, gw, gh) = CollageTool.Layout("grid", 5, 0, 100, 10);
        Assert.Equal(5, grid.Count);
        Assert.Equal((10 + 3 * 110, 10 + 2 * 110), (gw, gh));          // 3 columns, 2 rows

        var (row, rw, rh) = CollageTool.Layout("row", 3, 0, 100, 10);
        Assert.Equal((10 + 3 * 110, 120), (rw, rh));
        Assert.All(row, c => Assert.Equal(10, c.Y));

        var (col, cw, ch) = CollageTool.Layout("column", 3, 0, 100, 10);
        Assert.Equal((120, 10 + 3 * 110), (cw, ch));

        var (feat, _, _) = CollageTool.Layout("featured", 4, 0, 100, 10);
        Assert.Equal(200, feat[0].W);
        Assert.All(feat.Skip(1), c => Assert.True(c.W < 100));

        var (twoCols, tw, _) = CollageTool.Layout("grid", 4, 2, 100, 0);
        Assert.Equal(2, twoCols.Select(c => c.X).Distinct().Count());
        Assert.Equal(200, tw);
    }

    [Fact]
    public void Collage_cells_never_overlap()
    {
        foreach (var layout in new[] { "grid", "row", "column", "featured" })
            foreach (var n in new[] { 2, 3, 4, 5, 7 })
            {
                var (cells, w, h) = CollageTool.Layout(layout, n, 0, 100, 8);
                for (var i = 0; i < cells.Count; i++)
                {
                    Assert.True(cells[i].X >= 0 && cells[i].Y >= 0 && cells[i].X + cells[i].W <= w && cells[i].Y + cells[i].H <= h, $"{layout}/{n} cell {i} outside canvas");
                    for (var j = i + 1; j < cells.Count; j++)
                    {
                        var a = cells[i]; var b = cells[j];
                        var overlap = a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
                        Assert.False(overlap, $"{layout}/{n}: cells {i} and {j} overlap");
                    }
                }
            }
    }

    [Fact]
    public async Task Collage_combines_pictures_into_one_named_after_the_first()
    {
        using var dir = new TempDir();
        string Solid(string name, MagickColor c) { var p = dir.Combine(name); using var i = new MagickImage(c, 80, 60); i.Write(p); return p; }
        var inputs = new[] { Solid("a.png", MagickColors.Red), Solid("b.png", MagickColors.Green), Solid("c.png", MagickColors.Blue), Solid("d.png", MagickColors.Yellow) };

        var r = await Run("img.collage", inputs, new() { ["layout"] = "grid", ["cell"] = 100, ["spacing"] = 10, ["format"] = "png", ["background"] = "#ffffff" });

        Assert.True(r.Success, r.Error);
        Assert.Equal(Path.Combine(dir.Path, "a (collage).png"), r.Outputs.Single());
        using var result = new MagickImage(r.Outputs[0]);
        Assert.Equal((230u, 230u), (result.Width, result.Height));
        Assert.True(result.GetPixels().GetPixel(60, 60).ToColor()!.R > 200);          // first cell is red
        Assert.True(result.GetPixels().GetPixel(170, 60).ToColor()!.G > 100);         // second is green
        Assert.True(result.GetPixels().GetPixel(5, 5).ToColor()!.R > 240);            // spacing is background
    }

    [Fact]
    public async Task Collage_needs_at_least_two_pictures()
    {
        using var dir = new TempDir();
        var r = await Run("img.collage", [Samples.Png(dir.Combine("a.png"))]);
        Assert.False(r.Success);
        Assert.Contains("at least two", r.Error);
    }
}
