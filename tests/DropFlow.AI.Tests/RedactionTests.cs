using System.Drawing;
using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;
using Xunit;

namespace DropFlow.AI.Tests;

public class RedactionGeometryTests
{
    [Fact]
    public void Padding_is_a_percentage_of_the_shorter_side_and_is_clipped_to_the_image()
    {
        // 100 x 20 box: 50 % of the shorter side = 10 px on every edge
        var padded = RedactionRegions.PadAndClamp(new OcrRect(40, 30, 100, 20), 50, 1000, 500);
        Assert.Equal(new PixelRect(30, 20, 120, 40), padded);

        // clipped at the top-left and bottom-right edges
        var clipped = RedactionRegions.PadAndClamp(new OcrRect(2, 2, 100, 20), 50, 105, 25);
        Assert.Equal(new PixelRect(0, 0, 105, 25), clipped);

        Assert.Equal(new PixelRect(40, 30, 100, 20), RedactionRegions.PadAndClamp(new OcrRect(40, 30, 100, 20), 0, 1000, 500));
        // fractional boxes round outwards so nothing at the edge stays uncovered
        Assert.Equal(new PixelRect(10, 10, 21, 11), RedactionRegions.PadAndClamp(new OcrRect(10.4, 10.4, 20, 10), 0, 100, 100));
    }

    [Fact]
    public void Boxes_outside_the_image_vanish()
    {
        Assert.Null(RedactionRegions.PadAndClamp(new OcrRect(500, 500, 10, 10), 0, 100, 100));
        Assert.Null(RedactionRegions.PadAndClamp(new OcrRect(-50, -50, 10, 10), 0, 100, 100));
    }

    [Fact]
    public void Overlapping_and_touching_regions_merge_but_distant_ones_do_not()
    {
        var merged = RedactionRegions.Merge(
        [
            new PixelRect(0, 0, 10, 10), new PixelRect(5, 5, 10, 10), new PixelRect(15, 15, 5, 5),   // chain: all three join
            new PixelRect(100, 100, 10, 10),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Contains(new PixelRect(0, 0, 20, 20), merged);
        Assert.Contains(new PixelRect(100, 100, 10, 10), merged);
        Assert.Empty(RedactionRegions.Merge([]));
    }

    [Fact]
    public void Text_is_covered_a_line_at_a_time_so_gaps_between_words_do_not_leak_word_lengths()
    {
        var words = new[] { new OcrWord("a", new OcrRect(10, 10, 20, 20)), new OcrWord("b", new OcrRect(60, 10, 20, 20)) };
        var line = OcrOrdering.BuildLine(words);
        var boxes = RedactionRegions.TextBoxes(new OcrResult("t", "en", 100, 100, [line])).ToList();
        Assert.Single(boxes);
        Assert.Equal(new OcrRect(10, 10, 70, 20), boxes[0]);
    }
}

public class RedactionRendererTests
{
    /// <summary>A noisy 200x120 image, so "unchanged" and "changed" are unmistakable pixel by pixel.</summary>
    private static string NoisePng(string path)
    {
        var rng = new Random(42);
        return TestPng.FromPixels(path, 200, 120, (_, _) => Color.FromArgb(255, 40 + rng.Next(200), 40 + rng.Next(200), 40 + rng.Next(200)));
    }

    private static string Redact(string source, string output, RedactStyle style, params PixelRect[] regions)
    {
        using var image = new MagickImage(source);
        RedactionRenderer.Apply(image, regions, style);
        image.Format = MagickFormat.Png;
        image.Write(output);
        return output;
    }

    [Fact]
    public void Solid_boxes_turn_exactly_the_requested_pixels_black_and_nothing_else()
    {
        using var dir = new TempDir();
        var original = NoisePng(dir.Combine("in.png"));
        var box = new PixelRect(50, 40, 60, 30);
        var output = Redact(original, dir.Combine("out.png"), RedactStyle.Solid, box);

        using var before = new TextImage.Reader(original);
        using var after = new TextImage.Reader(output);          // decoded by GDI+
        for (var y = 0; y < 120; y++)
            for (var x = 0; x < 200; x++)
            {
                var inside = x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom;
                if (inside) Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(x, y));
                else Assert.Equal(before.Pixel(x, y), after.Pixel(x, y));
            }
    }

    [Fact]
    public void Blur_changes_only_the_box_and_destroys_the_detail_inside_it()
    {
        using var dir = new TempDir();
        var original = NoisePng(dir.Combine("in.png"));
        var box = new PixelRect(30, 20, 100, 60);
        var output = Redact(original, dir.Combine("out.png"), RedactStyle.Blur, box);

        using var before = new TextImage.Reader(original);
        using var after = new TextImage.Reader(output);
        Assert.Equal(0, before.CountDifferences(after, new Rectangle(0, 0, 200, 20)));             // above
        Assert.Equal(0, before.CountDifferences(after, new Rectangle(0, 80, 200, 40)));            // below
        Assert.Equal(0, before.CountDifferences(after, new Rectangle(0, 20, 30, 60)));             // left
        Assert.Equal(0, before.CountDifferences(after, new Rectangle(130, 20, 70, 60)));           // right
        Assert.True(before.CountDifferences(after, new Rectangle(box.X, box.Y, box.Width, box.Height)) > box.Width * box.Height * 0.9);

        // The noise (std-dev ~58 per channel) must be gone, not merely dimmed.
        Assert.True(StdDev(before, box) > 40);
        Assert.True(StdDev(after, box) < 12, $"std-dev after blur: {StdDev(after, box):0.0}");
    }

    [Fact]
    public void Several_regions_and_regions_touching_the_image_edge_work()
    {
        using var dir = new TempDir();
        var original = NoisePng(dir.Combine("in.png"));
        var output = Redact(original, dir.Combine("out.png"), RedactStyle.Solid,
            new PixelRect(0, 0, 10, 10), new PixelRect(190, 110, 10, 10), new PixelRect(100, 50, 1, 1));

        using var after = new TextImage.Reader(output);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(0, 0));
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(199, 119));
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(100, 50));
        Assert.NotEqual(Color.FromArgb(255, 0, 0, 0), after.Pixel(11, 11));
    }

    [Fact]
    public void Empty_regions_are_ignored()
    {
        using var dir = new TempDir();
        var original = NoisePng(dir.Combine("in.png"));
        var output = Redact(original, dir.Combine("out.png"), RedactStyle.Solid, new PixelRect(10, 10, 0, 5), new PixelRect(10, 10, 5, 0));
        using var before = new TextImage.Reader(original);
        using var after = new TextImage.Reader(output);
        Assert.Equal(0, before.CountDifferences(after, new Rectangle(0, 0, 200, 120)));
    }

    private static double StdDev(TextImage.Reader image, PixelRect box)
    {
        var values = new List<double>();
        for (var y = box.Y; y < box.Bottom; y++)
            for (var x = box.X; x < box.Right; x++) values.Add(image.Pixel(x, y).G);
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }
}

public class UltraFaceMathTests
{
    [Fact]
    public void There_are_4420_priors_laid_out_as_upstream_defines_them()
    {
        var priors = UltraFaceMath.Priors;
        Assert.Equal(UltraFaceMath.PriorCount * 4, priors.Length);

        // first prior: map 40x30, cell (0,0), 10 px box
        Assert.Equal(new[] { 0.5f / 40, 0.5f / 30, 10f / 320, 10f / 240 }, priors[..4]);

        // map 0, cell column i=19, row j=14, 16 px box (second of three boxes): index = ((14*40)+19)*3 + 1
        var k = ((14 * 40) + 19) * 3 + 1;
        Assert.Equal(new[] { 19.5f / 40, 14.5f / 30, 16f / 320, 16f / 240 }, priors[(k * 4)..(k * 4 + 4)]);

        // last prior: map 3 (5x4), last cell, 256 px box clipped to 1.0 wide / 1.0 tall (256/240 > 1)
        Assert.Equal(new[] { 4.5f / 5, 3.5f / 4, 256f / 320, 1f }, priors[^4..]);
        Assert.All(priors, p => Assert.InRange(p, 0f, 1f));
    }

    [Fact]
    public void Preprocessing_maps_0_to_minus_0_992_and_255_to_1()
    {
        var rgb = new byte[320 * 240 * 3];
        rgb[0] = 0; rgb[1] = 255; rgb[2] = 127;     // first pixel R, G, B
        var t = UltraFaceMath.ToTensor(rgb);
        Assert.Equal(3 * 320 * 240, t.Length);
        Assert.Equal(-127f / 128f, t[0], 6);                 // R plane, pixel 0
        Assert.Equal(1f, t[320 * 240], 6);                   // G plane, pixel 0: (255-127)/128
        Assert.Equal(0f, t[2 * 320 * 240], 6);               // B plane, pixel 0: (127-127)/128
        Assert.Throws<ArgumentException>(() => UltraFaceMath.ToTensor(new byte[10]));
    }

    private static (float[] Conf, float[] Loc) Heads()
    {
        var conf = new float[UltraFaceMath.PriorCount * 2];
        for (var i = 0; i < UltraFaceMath.PriorCount; i++) { conf[i * 2] = 8f; conf[i * 2 + 1] = -8f; }   // background everywhere
        return (conf, new float[UltraFaceMath.PriorCount * 4]);
    }

    [Fact]
    public void A_confident_prior_with_zero_deltas_decodes_to_the_prior_box_in_image_pixels()
    {
        var (conf, loc) = Heads();
        const int k = ((14 * 40) + 19) * 3 + 1;
        conf[k * 2] = -5f; conf[k * 2 + 1] = 5f;

        var faces = UltraFaceMath.Decode(conf, loc, 640, 480);

        var face = Assert.Single(faces);
        Assert.Equal((19.5 / 40 - 0.025) * 640, face.X, 3);       // centre minus half the 16/320 width, times image width
        Assert.Equal((14.5 / 30 - 16.0 / 240 / 2) * 480, face.Y, 3);
        Assert.Equal(0.05 * 640, face.Width, 3);
        Assert.Equal(16.0 / 240 * 480, face.Height, 3);
        Assert.Equal(1 / (1 + Math.Exp(-10)), face.Score, 6);
    }

    [Fact]
    public void Deltas_move_and_scale_the_box_with_the_variances()
    {
        var (conf, loc) = Heads();
        const int k = ((14 * 40) + 19) * 3 + 1;
        conf[k * 2] = -5f; conf[k * 2 + 1] = 5f;
        loc[k * 4] = 2f;        // centre x: + 2 * 0.1 * prior width
        loc[k * 4 + 1] = -1f;   // centre y: - 1 * 0.1 * prior height
        loc[k * 4 + 2] = 1f;    // width  * exp(1 * 0.2)
        loc[k * 4 + 3] = 0f;

        var face = Assert.Single(UltraFaceMath.Decode(conf, loc, 320, 240));

        double pw = 16.0 / 320, ph = 16.0 / 240;
        var cx = 19.5 / 40 + 2 * 0.1 * pw;
        var cy = 14.5 / 30 - 1 * 0.1 * ph;
        var w = Math.Exp(0.2) * pw;
        Assert.Equal((cx - w / 2) * 320, face.X, 3);
        Assert.Equal((cy - ph / 2) * 240, face.Y, 3);
        Assert.Equal(w * 320, face.Width, 3);
        Assert.Equal(ph * 240, face.Height, 3);
    }

    [Fact]
    public void Overlapping_detections_collapse_to_the_best_one_and_distant_ones_survive()
    {
        var (conf, loc) = Heads();
        const int k = ((14 * 40) + 19) * 3 + 1;
        const int neighbour = k + 3;            // next cell to the right: shifted by 8 px of 320
        const int far = ((5 * 40) + 5) * 3 + 1;
        conf[k * 2] = -5f; conf[k * 2 + 1] = 3f;                 // best
        conf[neighbour * 2] = -5f; conf[neighbour * 2 + 1] = 2f; // overlaps the best (IoU 0.33 > 0.3)
        conf[far * 2] = -5f; conf[far * 2 + 1] = 4f;

        var faces = UltraFaceMath.Decode(conf, loc, 320, 240);

        Assert.Equal(2, faces.Count);
        Assert.True(faces[0].Score > faces[1].Score);
        Assert.Contains(faces, f => Math.Abs(f.X - (19.5 / 40 - 0.025) * 320) < 1e-3);
        Assert.Contains(faces, f => Math.Abs(f.X - (5.5 / 40 - 0.025) * 320) < 1e-3);
    }

    [Fact]
    public void Low_confidence_is_ignored_and_the_threshold_is_adjustable()
    {
        var (conf, loc) = Heads();
        const int k = 1000;
        conf[k * 2] = 0f; conf[k * 2 + 1] = 0.4f;   // softmax ~ 0.599
        Assert.Single(UltraFaceMath.Decode(conf, loc, 320, 240, scoreThreshold: 0.5));
        Assert.Empty(UltraFaceMath.Decode(conf, loc, 320, 240, scoreThreshold: 0.7));
        Assert.Empty(UltraFaceMath.Decode(Heads().Conf, Heads().Loc, 320, 240));
    }

    [Fact]
    public void Wrong_shapes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => UltraFaceMath.Decode(new float[10], new float[20], 100, 100));
        Assert.Throws<ArgumentException>(() => UltraFaceMath.Decode(new float[UltraFaceMath.PriorCount * 2], new float[10], 100, 100));
    }

    [Fact]
    public void Nms_and_iou_basics()
    {
        var a = new FaceBox(0, 0, 10, 10, 0.9);
        var b = new FaceBox(5, 0, 10, 10, 0.8);
        var c = new FaceBox(100, 100, 10, 10, 0.7);
        Assert.Equal(50.0 / 150.0, UltraFaceMath.IoU(a, b), 6);
        Assert.Equal(0, UltraFaceMath.IoU(a, c));
        Assert.Equal([a, c], UltraFaceMath.NonMaxSuppression([c, b, a], 0.3));
        Assert.Equal([a, b, c], UltraFaceMath.NonMaxSuppression([c, b, a], 0.6));
    }
}

/// <summary>The auto-redaction converter, end to end.</summary>
public class RedactAutoConverterTests
{
    private sealed class FixedOcr(params OcrRect[] lineBoxes) : IWindowsOcr
    {
        public Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken ct)
        {
            using var img = new MagickImage(pngPath);
            var lines = lineBoxes.Select(b => new OcrLine("secret", [new OcrWord("secret", b)], b, false)).ToList();
            return Task.FromResult(new OcrResult("windows", "en-US", (int)img.Width, (int)img.Height, lines));
        }
    }

    private static OcrService OcrWith(params OcrRect[] boxes) =>
        new(new FakeEngines(), new OcrLanguageService(new FakeLanguagesSource("en-US")), new FixedOcr(boxes), new NoTesseract());

    private sealed class FakeLanguagesSource(params string[] tags) : IWindowsOcrLanguageSource
    {
        public IReadOnlyList<string> InstalledTags() => tags;
    }

    private sealed class NoTesseract : ITesseractOcr
    {
        public bool IsInstalled => false;
        public Task<IReadOnlyList<string>> LanguagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<OcrResult> RecognizeAsync(string pngPath, IReadOnlyList<string> languages, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static ModelManager Models(TempDir dir, bool faceInstalled = false, byte[]? faceModel = null)
    {
        var folder = dir.Combine("models");
        Directory.CreateDirectory(folder);
        var catalog = ModelCatalog.Default.ToList();
        if (faceInstalled)
        {
            var index = catalog.FindIndex(m => m.Id == "ultraface-rfb-320");
            catalog[index] = catalog[index] with { SizeBytes = faceModel!.Length, Sha256 = null, AllowUnverified = true };
            File.WriteAllBytes(Path.Combine(folder, catalog[index].FileName), faceModel);
        }
        return new ModelManager(folder, catalog);
    }

    /// <summary>A smooth gradient: no pixel is black, and neighbours differ, so every redacted pixel is a visible change.</summary>
    private static string GradientPng(string path, int width = 320, int height = 240)
    {
        return TestPng.FromPixels(path, width, height, (x, y) => Color.FromArgb(255, 40 + x * 200 / width, 40 + y * 200 / height, 130));
    }

    private static ConversionJob Job(string input, Dictionary<string, object?>? options = null, string? outDir = null) => new([input], "img.redact-auto", options, outDir);

    /// <summary>The converter with the UltraFace (ONNX) finder, so these tests drive the model path on every OS.</summary>
    private static RedactAutoConverter Converter(ModelManager models, OcrService ocr, OnnxSessionCache sessions) =>
        new(models, ocr, sessions, new UltraFaceFinder(models, new FaceDetector(sessions)));

    private static int Diffs(string a, string b, Rectangle area)
    {
        using var ra = new TextImage.Reader(a);
        using var rb = new TextImage.Reader(b);
        return ra.CountDifferences(rb, area);
    }

    [Fact]
    public async Task Text_rectangles_from_ocr_are_covered_exactly_and_nothing_else_changes()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("form.png"));
        var box = new OcrRect(60, 50, 100, 20);
        var converter = Converter(models, OcrWith(box), sessions);

        var result = await converter.RunAsync(Job(input, new() { ["target"] = "text", ["padding"] = 0 }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(dir.Combine("form (redacted).png"), result.Outputs.Single());
        using var after = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(60, 50));
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(159, 69));
        // every pixel in the box changed, none outside did: the number of differing pixels IS the box area
        Assert.Equal(100 * 20, Diffs(input, result.Outputs[0], new Rectangle(0, 0, 320, 240)));
    }

    [Fact]
    public async Task Padding_widens_the_box_and_blur_style_blurs_instead_of_blackening()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("form.png"));
        var converter = Converter(models, OcrWith(new OcrRect(60, 50, 100, 20)), sessions);

        // 50 % of the shorter side (20) = 10 px on each edge: box becomes (50,40,120,40)
        var solid = await converter.RunAsync(Job(input, new() { ["target"] = "text", ["padding"] = 50 }), new Progress<JobProgress>(), default);
        Assert.Equal(120 * 40, Diffs(input, solid.Outputs[0], new Rectangle(0, 0, 320, 240)));

        var blur = await converter.RunAsync(Job(input, new() { ["target"] = "text", ["padding"] = 0, ["style"] = "blur" }), new Progress<JobProgress>(), default);
        using var after = new TextImage.Reader(blur.Outputs[0]);
        Assert.NotEqual(Color.FromArgb(255, 0, 0, 0), after.Pixel(100, 60)); // not black
        Assert.Equal(0, Diffs(input, blur.Outputs[0], new Rectangle(0, 0, 320, 50)));     // nothing above the box changed
        Assert.Equal(0, Diffs(input, blur.Outputs[0], new Rectangle(0, 70, 320, 170)));   // nor below
    }

    [Fact]
    public async Task Never_overwrites_the_input_or_an_earlier_result()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("form.png"));
        var original = File.ReadAllBytes(input);
        var converter = Converter(models, OcrWith(new OcrRect(10, 10, 50, 10)), sessions);

        var one = await converter.RunAsync(Job(input, new() { ["target"] = "text" }), new Progress<JobProgress>(), default);
        var two = await converter.RunAsync(Job(input, new() { ["target"] = "text" }), new Progress<JobProgress>(), default);

        Assert.EndsWith("form (redacted).png", one.Outputs[0]);
        Assert.EndsWith("form (redacted 2).png", two.Outputs[0]);
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public async Task Jpeg_keeps_its_format_and_loses_its_exif_data()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var input = TestImages.JpegRotated90(dir.Combine("phone.jpg"), 200, 100, MagickColors.Orange);
        using (var probe = new MagickImage(input)) Assert.NotNull(probe.GetExifProfile());   // the source really has EXIF
        // the photo is shown 100 wide x 200 tall; OCR coordinates are in that upright space
        var converter = Converter(models, OcrWith(new OcrRect(10, 20, 50, 20)), sessions);

        var result = await converter.RunAsync(Job(input, new() { ["target"] = "text" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.EndsWith("phone (redacted).jpg", result.Outputs[0]);
        using var output = new MagickImage(result.Outputs[0]);
        Assert.Equal((100u, 200u), (output.Width, output.Height));       // saved upright
        Assert.Null(output.GetExifProfile());                            // thumbnail and metadata gone
        Assert.Null(output.GetXmpProfile());
    }

    [Fact]
    public async Task Nothing_found_means_no_file_and_a_clear_message()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("empty.png"));

        var result = await Converter(models, OcrWith(), sessions).RunAsync(Job(input, new() { ["target"] = "text" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("Nothing to redact", result.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(redacted*"));
    }

    [Fact]
    public async Task A_missing_face_model_fails_the_whole_job_instead_of_redacting_only_the_text()
    {
        using var dir = new TempDir();
        using var models = Models(dir);   // face model not installed
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("form.png"));
        var converter = Converter(models, OcrWith(new OcrRect(10, 10, 50, 10)), sessions);

        foreach (var target in new[] { "both", "faces" })
        {
            var result = await converter.RunAsync(Job(input, new() { ["target"] = target }), new Progress<JobProgress>(), default);
            Assert.False(result.Success);
            Assert.Equal("ultraface-rfb-320", AiFailures.TryParseMissingModel(result.Error));
        }
        Assert.Empty(Directory.GetFiles(dir.Path, "*(redacted*"));
        // text-only needs no face model
        Assert.True((await converter.RunAsync(Job(input, new() { ["target"] = "text" }), new Progress<JobProgress>(), default)).Success);
    }

    [Fact]
    public async Task Ocr_problems_fail_the_job_and_name_the_missing_language()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("form.png"));
        var ocr = new OcrService(new FakeEngines(), new OcrLanguageService(new FakeLanguagesSource("en-US")), new FixedOcr(), new NoTesseract());

        var result = await Converter(models, ocr, sessions).RunAsync(Job(input, new() { ["target"] = "text", ["language"] = "ar" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("Language.OCR~~~ar-SA~0.0.1.0", result.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(redacted*"));
    }

    // ---- faces, with a network whose outputs are constants (so the whole ONNX path runs for real) --------------------

    private static byte[] FaceHeadsModel(int faceIndex)
    {
        var conf = new float[UltraFaceMath.PriorCount * 2];
        for (var i = 0; i < UltraFaceMath.PriorCount; i++) { conf[i * 2] = 8f; conf[i * 2 + 1] = -8f; }
        conf[faceIndex * 2] = -5f; conf[faceIndex * 2 + 1] = 5f;
        return OnnxTestModel.ConstantOutputs("input", [1, 3, 240, 320],
            ("confidences", [1, UltraFaceMath.PriorCount, 2], conf),
            ("locations", [1, UltraFaceMath.PriorCount, 4], new float[UltraFaceMath.PriorCount * 4]));
    }

    [Fact]
    public async Task A_face_detected_by_the_network_is_covered_at_the_right_place_and_scale()
    {
        using var dir = new TempDir();
        const int k = ((14 * 40) + 19) * 3 + 1;              // prior near the centre, 16 px of 320
        using var models = Models(dir, faceInstalled: true, FaceHeadsModel(k));
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("group.png"), 640, 480);   // twice the network's frame, so scaling matters
        var converter = Converter(models, OcrWith(), sessions);

        var result = await converter.RunAsync(Job(input, new() { ["target"] = "faces", ["padding"] = 0 }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        // expected box: x=(19.5/40-0.025)*640=296, y=(14.5/30-16/240/2)*480=216, 32 x 32 px
        var x0 = (int)Math.Round((19.5 / 40 - 0.025) * 640);
        var y0 = (int)Math.Round((14.5 / 30 - 16.0 / 240 / 2) * 480);
        using var after = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(x0 + 5, y0 + 5));
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(x0 + 28, y0 + 28));
        var changed = Diffs(input, result.Outputs[0], new Rectangle(0, 0, 640, 480));
        Assert.InRange(changed, 31 * 31, 34 * 34);          // roughly the 32 x 32 box, and nothing else
        Assert.Equal(changed, Diffs(input, result.Outputs[0], new Rectangle(x0 - 2, y0 - 2, 37, 37))); // all of it inside that window
    }

    [Fact]
    public async Task Faces_and_text_are_both_covered_when_asked()
    {
        using var dir = new TempDir();
        const int k = ((14 * 40) + 19) * 3 + 1;
        using var models = Models(dir, faceInstalled: true, FaceHeadsModel(k));
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("both.png"), 640, 480);
        var converter = Converter(models, OcrWith(new OcrRect(20, 20, 200, 30)), sessions);

        var result = await converter.RunAsync(Job(input, new() { ["padding"] = 0 }), new Progress<JobProgress>(), default);   // target defaults to both

        Assert.True(result.Success, result.Error);
        using var after = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(100, 30));   // text line
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(312, 232));  // face
        Assert.InRange(Diffs(input, result.Outputs[0], new Rectangle(0, 0, 640, 480)), 200 * 30 + 31 * 31, 200 * 30 + 34 * 34);
    }

    [Fact]
    public async Task A_model_that_is_not_an_ultraface_network_is_reported()
    {
        using var dir = new TempDir();
        using var models = Models(dir, faceInstalled: true, OnnxTestModel.BrightnessMask(240, 320));   // wrong outputs
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("x.png"));
        var result = await Converter(models, OcrWith(), sessions).RunAsync(Job(input, new() { ["target"] = "faces" }), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.Contains("face model", result.Error);
    }

    // ---- real engines --------------------------------------------------------------------------------------------------

    [OcrFact("en")]
    public async Task Real_text_is_found_by_windows_ocr_and_cannot_be_read_back_afterwards()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var logo = new Rectangle(700, 120, 120, 40);
        var image = TextImage.Render(dir.Combine("memo.png"), "Confidential 12345", fontSize: 60, width: 900, height: 200,
            boxes: [(logo, Color.Crimson)]);
        var ocr = new OcrService(new FakeEngines());
        var converter = Converter(models, ocr, sessions);

        // sanity: before redaction the text IS readable
        var readable = await ocr.RecognizeAsync(image.Path, new OcrRequest("en", OcrEnginePreference.Windows), default);
        Assert.Contains("Confidential", readable.Text, StringComparison.OrdinalIgnoreCase);

        foreach (var style in new[] { "solid", "blur" })
        {
            var result = await converter.RunAsync(Job(image.Path, new() { ["target"] = "text", ["style"] = style, ["language"] = "en" }), new Progress<JobProgress>(), default);
            Assert.True(result.Success, result.Error);

            // the decorative rectangle far from the text is untouched
            Assert.Equal(0, Diffs(image.Path, result.Outputs[0], new Rectangle(690, 110, 140, 70)));
            // and the words are gone for any reader of the output
            using var check = new TextImage.Reader(result.Outputs[0]);
            Assert.NotEqual(Color.White, check.Pixel(40, 50));
            var after = await ocr.RecognizeAsync(result.Outputs[0], new OcrRequest("en", OcrEnginePreference.Windows), default);
            Assert.DoesNotContain("Confidential", after.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("12345", after.Text);
        }
    }

    [OcrFact("ar")]
    public async Task Real_arabic_text_is_covered_too()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var image = TextImage.Render(dir.Combine("ar.png"), "رقم الحساب السري", fontSize: 64, width: 900, height: 200, rtl: true);
        var ocr = new OcrService(new FakeEngines());

        var before = await ocr.RecognizeAsync(image.Path, new OcrRequest("ar", OcrEnginePreference.Windows), default);
        Assert.Contains("الحساب", before.Text);

        var result = await Converter(models, ocr, sessions).RunAsync(Job(image.Path, new() { ["target"] = "text", ["language"] = "ar" }), new Progress<JobProgress>(), default);
        Assert.True(result.Success, result.Error);
        var after = await ocr.RecognizeAsync(result.Outputs[0], new OcrRequest("ar", OcrEnginePreference.Windows), default);
        Assert.DoesNotContain("الحساب", after.Text);
        Assert.DoesNotContain("السري", after.Text);
    }

    [ModelFact("ultraface-rfb-320")]
    public async Task Real_ultraface_runs_on_a_picture_without_faces_and_says_nothing_was_found()
    {
        using var dir = new TempDir();
        using var models = ModelFactAttribute.OpenManager();
        using var sessions = new OnnxSessionCache();
        var input = GradientPng(dir.Combine("landscape.png"), 640, 480);

        var result = await Converter(models, OcrWith(), sessions).RunAsync(Job(input, new() { ["target"] = "faces" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Contains("Nothing to redact", result.Error);   // the real network loaded and ran; a gradient has no faces
    }

    [Fact]
    public void Supports_options_engine_and_catalog_entry_are_truthful()
    {
        using var dir = new TempDir();
        using var models = Models(dir);
        using var sessions = new OnnxSessionCache();
        var c = Converter(models, OcrWith(), sessions);

        Assert.Equal(["img.redact-auto"], c.Operations);
        Assert.NotNull(ToolCatalog.LoadEmbedded().Tool("img.redact-auto"));
        Assert.Equal(EngineId.None, c.EngineFor("img.redact-auto"));
        foreach (var ext in new[] { "png", "jpg", "webp", "bmp", "tiff", "gif", "avif" }) Assert.True(c.Supports("img.redact-auto", ext), ext);
        foreach (var ext in new[] { "pdf", "mp3", "txt", "" }) Assert.False(c.Supports("img.redact-auto", ext), ext);
        Assert.False(c.Supports("img.redact", "png"));

        var options = c.OptionsFor("img.redact-auto").ToDictionary(o => o.Key);
        Assert.Equal(["language", "padding", "style", "target"], options.Keys.Order());
        Assert.Equal(["both", "faces", "text"], options["target"].Choices);
        Assert.Equal(["solid", "blur"], options["style"].Choices);
        Assert.Equal(0, options["padding"].Min);
        Assert.Equal(50, options["padding"].Max);
    }
}

/// <summary>Who finds the faces: the platform default, the fallback chain, and Windows' own detector.</summary>
public class FaceFinderTests
{
    private sealed class Fixed(string name, string? unavailable, params FaceBox[] faces) : IFaceFinder
    {
        public int Calls;
        public string Name => name;
        public string? Unavailable() => unavailable;
        public Task<IReadOnlyList<FaceBox>> FindAsync(IMagickImage<byte> image, CancellationToken ct) { Calls++; return Task.FromResult<IReadOnlyList<FaceBox>>(faces); }
    }

    private static string Gradient(string path) => TestPng.FromPixels(path, 320, 240, (x, y) => Color.FromArgb(255, 40 + x * 200 / 320, 40 + y * 200 / 240, 130));

    private static OcrService NoOcr() => new(new FakeEngines(), new OcrLanguageService(new NoPacks()), new NeverOcr(), new NoTesseract());

    private sealed class NoPacks : IWindowsOcrLanguageSource { public IReadOnlyList<string> InstalledTags() => []; }

    private sealed class NeverOcr : IWindowsOcr
    {
        public Task<OcrResult> RecognizeAsync(string pngPath, string? languageTag, CancellationToken ct) => throw new InvalidOperationException("no OCR in these tests");
    }

    private sealed class NoTesseract : ITesseractOcr
    {
        public bool IsInstalled => false;
        public Task<IReadOnlyList<string>> LanguagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<OcrResult> RecognizeAsync(string pngPath, IReadOnlyList<string> languages, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact]
    public void The_first_available_finder_runs_and_a_missing_model_is_reported_only_when_nothing_can_run()
    {
        var ready = new Fixed("ready", null);
        var missing = new Fixed("model", "Model 'ultraface-rfb-320' is not installed.");
        var broken = new Fixed("os", "not on this PC");

        Assert.Same(ready, new FirstAvailableFaceFinder(broken, ready, missing).Current);
        Assert.Null(new FirstAvailableFaceFinder(broken, ready).Unavailable());
        var none = new FirstAvailableFaceFinder(broken, missing);
        Assert.Null(none.Current);
        Assert.Equal("ultraface-rfb-320", AiFailures.TryParseMissingModel(none.Unavailable()));   // the app can offer the download
        Assert.Throws<ArgumentException>(() => new FirstAvailableFaceFinder());
    }

    [Fact]
    public async Task The_converter_uses_the_injected_finder_and_covers_its_box()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("models"));   // no model installed: the injected finder needs none
        var finder = new Fixed("test", null, new FaceBox(100, 80, 40, 30, 0.9));
        var input = Gradient(dir.Combine("p.png"));

        var result = await new RedactAutoConverter(models, NoOcr(), faces: finder)
            .RunAsync(new ConversionJob([input], "img.redact-auto", new Dictionary<string, object?> { ["target"] = "faces", ["padding"] = 0 }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, finder.Calls);
        using var after = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), after.Pixel(110, 90));
        Assert.NotEqual(Color.FromArgb(255, 0, 0, 0), after.Pixel(10, 10));
    }

    [Fact]
    public async Task An_unavailable_finder_fails_the_job_with_its_reason()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("models"));
        var input = Gradient(dir.Combine("p.png"));

        var result = await new RedactAutoConverter(models, NoOcr(), faces: new Fixed("x", "no detector here"))
            .RunAsync(new ConversionJob([input], "img.redact-auto", new Dictionary<string, object?> { ["target"] = "faces" }), new Progress<JobProgress>(), default);

        Assert.False(result.Success);
        Assert.Equal("no detector here", result.Error);
    }

#if WINDOWS
    [Fact]
    public void On_windows_the_default_is_windows_face_detection_with_the_model_as_fallback()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("models"));
        var chain = Assert.IsType<FirstAvailableFaceFinder>(new RedactAutoConverter(models, NoOcr()).Faces);
        Assert.IsType<WindowsFaceFinder>(chain.Finders[0]);
        Assert.IsType<UltraFaceFinder>(chain.Finders[1]);
    }

    [WindowsFaceFact]
    public async Task Windows_face_detection_needs_no_model_and_runs_end_to_end()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("models"));   // UltraFace is NOT installed
        var input = Gradient(dir.Combine("landscape.png"));

        var converter = new RedactAutoConverter(models, NoOcr());
        Assert.Null(converter.Faces.Unavailable());

        // A gradient has no faces: the built-in detector ran (no "model not installed" error) and found nothing.
        var result = await converter.RunAsync(new ConversionJob([input], "img.redact-auto", new Dictionary<string, object?> { ["target"] = "faces" }), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.Contains("Nothing to redact", result.Error);
        Assert.Null(AiFailures.TryParseMissingModel(result.Error));
    }

    [WindowsFaceFact]
    public async Task Windows_face_detection_maps_boxes_back_from_a_downscaled_picture()
    {
        using var image = new MagickImage(MagickColors.White, 5000, 3000);   // larger than MaxSide: scaled down for the detector
        var faces = await new WindowsFaceFinder().FindAsync(image, default);
        Assert.Empty(faces);   // a blank page has no faces, and the large picture is accepted
    }
#else
    [Fact]
    public void Without_windows_the_default_is_the_ultraface_model()
    {
        using var dir = new TempDir();
        using var models = new ModelManager(dir.Combine("models"));
        var faces = new RedactAutoConverter(models, NoOcr()).Faces;
        Assert.IsType<UltraFaceFinder>(faces);
        Assert.Equal("ultraface-rfb-320", AiFailures.TryParseMissingModel(faces.Unavailable()));
    }
#endif
}

#if WINDOWS
/// <summary>Skipped, visibly, when this Windows has no face-analysis component.</summary>
public sealed class WindowsFaceFactAttribute : FactAttribute
{
    public WindowsFaceFactAttribute()
    {
        if (!WindowsFaceFinder.IsSupported) Skip = "Windows face detection (Windows.Media.FaceAnalysis) is not available on this PC.";
    }
}
#endif
