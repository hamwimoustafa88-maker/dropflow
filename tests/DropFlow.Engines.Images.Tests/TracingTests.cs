using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DropFlow.Core.Operations;
using DropFlow.Engines.Images.Vector.Tracing;
using DropFlow.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit.Abstractions;

namespace DropFlow.Engines.Images.Tests;

/// <summary>
/// Raster to SVG tracing (own code in Vector/Tracing). The end-to-end checks re-rasterise the SVG with a different renderer
/// (librsvg inside Magick.NET) and compare the shapes with the original pixels by intersection over union.
/// </summary>
public class TracingTests(ITestOutputHelper log)
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    private static IConverter Tool(string operation, string ext = "png") =>
        ImagesModule.Create(new FormatConverterTests.NoEngines()).Single(c => c.Supports(operation, ext));

    private static Task<ConversionResult> Run(string operation, string input, Dictionary<string, object?>? options = null, CancellationToken ct = default) =>
        Tool(operation, Path.GetExtension(input).TrimStart('.').ToLowerInvariant()).RunAsync(new ConversionJob([input], operation, options), NoProgress, ct);

    // ---------- pieces ----------

    [Fact]
    public void Otsu_puts_the_threshold_between_the_two_peaks()
    {
        var values = Enumerable.Repeat((byte)60, 500).Concat(Enumerable.Repeat((byte)190, 300)).ToArray();

        var t = Otsu.Threshold(values);

        Assert.InRange(t, 100, 150);   // any value in 60..189 separates them; the middle of that range is the robust choice
    }

    [Fact]
    public void Otsu_on_a_noisy_bimodal_picture_separates_the_peaks()
    {
        var random = new Random(3);
        var values = Enumerable.Range(0, 4000).Select(i => (byte)Math.Clamp((i % 2 == 0 ? 50 : 200) + random.Next(-25, 26), 0, 255)).ToArray();

        var t = Otsu.Threshold(values);

        Assert.InRange(t, 76, 174);
        Assert.Equal(2000, values.Count(v => v <= t));
    }

    [Fact]
    public void Kmeans_finds_the_distinct_colours_of_a_picture_and_is_repeatable()
    {
        var random = new Random(7);
        (byte, byte, byte)[] colours = [(230, 30, 40), (20, 120, 220), (250, 250, 250), (30, 160, 60)];
        var pixels = Enumerable.Range(0, 8000).Select(i =>
        {
            var (r, g, b) = colours[i % 4];
            byte N(byte v) => (byte)Math.Clamp(v + random.Next(-6, 7), 0, 255);
            return (N(r), N(g), N(b));
        }).ToArray();

        var palette = ColorQuantizer.Palette(pixels, 4);
        var again = ColorQuantizer.Palette(pixels, 4);

        Assert.Equal(4, palette.Length);
        Assert.Equal(palette, again);
        foreach (var (r, g, b) in colours)
            Assert.Contains(palette, p => Math.Abs(p.R - r) <= 8 && Math.Abs(p.G - g) <= 8 && Math.Abs(p.B - b) <= 8);
    }

    [Fact]
    public void Kmeans_never_invents_more_colours_than_the_picture_has()
    {
        var pixels = Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255)).ToArray();

        var palette = ColorQuantizer.Palette(pixels, 16);

        Assert.Equal(2, palette.Length);
    }

    [Fact]
    public void One_pixel_traces_to_one_clockwise_square_on_its_edges()
    {
        var mask = new bool[3 * 3];
        mask[1 * 3 + 1] = true;

        var loop = Assert.Single(BoundaryTracer.Trace(mask, 3, 3));

        Assert.Equal(4, loop.Count);
        Assert.Equal(new HashSet<(int, int)> { (1, 1), (2, 1), (2, 2), (1, 2) }, loop.ToHashSet());
        Assert.True(SignedArea(loop) > 0, "outer boundaries run clockwise on screen (positive area with y pointing down)");
    }

    [Fact]
    public void A_ring_has_an_outer_boundary_and_a_hole_that_run_opposite_ways()
    {
        const int w = 5, h = 5;
        var mask = new bool[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) mask[y * w + x] = !(x == 2 && y == 2);

        var loops = BoundaryTracer.Trace(mask, w, h);

        Assert.Equal(2, loops.Count);
        var areas = loops.Select(SignedArea).OrderBy(a => a).ToList();
        Assert.Equal(-1, areas[0]);    // the hole
        Assert.Equal(25, areas[1]);    // the outside
    }

    [Fact]
    public void Pixels_touching_only_at_a_corner_are_separate_shapes()
    {
        var mask = new[] { true, false, false, true };

        var loops = BoundaryTracer.Trace(mask, 2, 2);

        Assert.Equal(2, loops.Count);
        Assert.All(loops, l => Assert.Equal(1, SignedArea(l)));
    }

    [Fact]
    public void Schneider_fit_reproduces_a_cubic_from_its_samples_with_one_segment()
    {
        var truth = new CubicBezier(new Vec(0, 0), new Vec(30, 80), new Vec(90, -40), new Vec(120, 30));
        var points = Enumerable.Range(0, 60).Select(i => truth.At(i / 59.0)).ToList();

        var curves = BezierFitter.Fit(points, (truth.P1 - truth.P0).Normalized(), (truth.P2 - truth.P3).Normalized(), maxError: 0.25);

        var curve = Assert.Single(curves);
        foreach (var p in points) Assert.True(DistanceTo(curves, p) < 0.5, $"{p} is {DistanceTo(curves, p):0.00} px from the fit");
        Assert.Equal(truth.P0, curve.P0);
        Assert.Equal(truth.P3, curve.P3);
    }

    [Fact]
    public void Schneider_fit_splits_where_one_cubic_is_not_enough()
    {
        // Three quarters of a circle cannot be one cubic within 0.3 px.
        var points = Enumerable.Range(0, 200).Select(i => { var a = i / 199.0 * 1.5 * Math.PI; return new Vec(100 + 60 * Math.Cos(a), 100 + 60 * Math.Sin(a)); }).ToList();

        var curves = BezierFitter.Fit(points, new Vec(0, 1), new Vec(-1, 0), maxError: 0.3);

        Assert.InRange(curves.Count, 2, 8);
        foreach (var p in points) Assert.True(DistanceTo(curves, p) < 0.6, $"{p} is {DistanceTo(curves, p):0.00} px from the fit");
        for (var i = 1; i < curves.Count; i++) Assert.Equal(curves[i - 1].P3, curves[i].P0);
    }

    [Fact]
    public void Specks_smaller_than_the_minimum_join_the_region_around_them()
    {
        const int w = 6, h = 6;
        var labels = new int[w * h];
        labels[2 * w + 2] = 1;
        labels[2 * w + 3] = 1;          // a 2-pixel speck of colour 1 inside colour 0
        for (var x = 0; x < w; x++) labels[5 * w + x] = 2;   // a 6-pixel band of colour 2 stays

        Regions.RemoveSpecks(labels, w, h, minArea: 4);

        Assert.DoesNotContain(1, labels);
        Assert.Equal(6, labels.Count(l => l == 2));
    }

    // ---------- end to end ----------

    [Fact]
    public async Task Black_shapes_traced_in_bw_mode_come_back_with_an_iou_above_097()
    {
        using var dir = new TempDir();
        var png = dir.Combine("shapes.png");
        using (var image = new MagickImage(MagickColors.White, 240, 180))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Black).StrokeColor(MagickColors.Transparent)
                .Circle(60, 60, 60, 25)                                     // circle, radius 35
                .Rectangle(130, 30, 210, 80)
                .Polygon(new PointD(40, 160), new PointD(110, 160), new PointD(75, 105))
                .Ellipse(170, 135, 45, 22, 0, 360));
            image.Write(png);
        }

        var result = await Run("img.trace", png, new() { ["mode"] = "bw" });

        Assert.True(result.Success, result.Error);
        var svg = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("shapes (traced).svg"), svg);
        var original = DarkMask(png, out var w, out var h);
        var traced = DarkMask(Rasterise(svg), out var w2, out var h2);
        Assert.Equal((w, h), (w2, h2));
        var iou = Iou(original, traced);
        log.WriteLine($"bw shapes IoU {iou:0.0000}, {new FileInfo(svg).Length} bytes");
        Assert.True(iou >= 0.97, $"IoU {iou:0.0000}");
    }

    [Fact]
    public async Task A_sharp_rectangle_keeps_its_corners()
    {
        using var dir = new TempDir();
        var png = dir.Combine("box.png");
        using (var image = new MagickImage(MagickColors.White, 120, 90))
        {
            using var box = new MagickImage(MagickColors.Black, 80, 60);   // crisp edges, no anti-aliasing
            image.Composite(box, 20, 15);
            image.Write(png);
        }

        var result = await Run("img.trace", png, new() { ["mode"] = "bw" });

        var iou = Iou(DarkMask(png, out _, out _), DarkMask(Rasterise(result.Outputs[0]), out _, out _));
        log.WriteLine($"rectangle IoU {iou:0.0000}");
        Assert.True(iou >= 0.995, $"IoU {iou:0.0000}");
        var d = XDocument.Load(result.Outputs[0]).Descendants().Single(e => e.Name.LocalName == "path").Attribute("d")!.Value;
        Assert.DoesNotContain("C", d);   // four straight sides, no curves
    }

    [Fact]
    public async Task Colour_mode_keeps_each_colour_region_and_leaves_no_gaps_between_them()
    {
        using var dir = new TempDir();
        var png = dir.Combine("flag.png");
        using (var image = new MagickImage(MagickColors.White, 200, 150))
        {
            image.Draw(new Drawables().FillColor(new MagickColor(30, 80, 200)).Rectangle(20, 20, 179, 129)
                .FillColor(new MagickColor(220, 30, 40)).Circle(100, 75, 100, 30));
            image.Write(png);
        }

        var result = await Run("img.trace", png, new() { ["mode"] = "color", ["colors"] = 3 });

        Assert.True(result.Success, result.Error);
        var paths = XDocument.Load(result.Outputs[0]).Descendants().Where(e => e.Name.LocalName == "path").ToList();
        Assert.Equal(3, paths.Count);

        using var source = new MagickImage(png);
        using var rendered = new MagickImage(Rasterise(result.Outputs[0], transparent: true));
        var src = source.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        var dst = rendered.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        Assert.Equal(src.Length, dst.Length);

        var seams = Enumerable.Range(0, src.Length / 4).Count(i => dst[i * 4 + 3] < 250);
        Assert.Equal(0, seams);   // stacked layers: nothing shows through between two colours

        foreach (var (r, g, b) in new[] { (255, 255, 255), (30, 80, 200), (220, 30, 40) })
        {
            bool[] Of(byte[] px) => Enumerable.Range(0, px.Length / 4).Select(i => Near(px, i, r, g, b)).ToArray();
            var iou = Iou(Of(src), Of(dst));
            log.WriteLine($"colour ({r},{g},{b}) IoU {iou:0.0000}");
            Assert.True(iou >= 0.97, $"colour ({r},{g},{b}) IoU {iou:0.0000}");
        }
    }

    [Fact]
    public async Task Transparent_areas_are_left_unpainted()
    {
        using var dir = new TempDir();
        var png = dir.Combine("logo.png");
        using (var image = new MagickImage(MagickColors.Transparent, 100, 100))
        {
            image.Draw(new Drawables().FillColor(new MagickColor(0, 150, 70)).Rectangle(25, 25, 74, 74));
            image.Write(png);
        }

        var result = await Run("img.trace", png);

        Assert.True(result.Success, result.Error);
        using var rendered = new MagickImage(Rasterise(result.Outputs[0], transparent: true));
        var px = rendered.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        Assert.Equal(0, px[(5 * 100 + 5) * 4 + 3]);
        Assert.Equal(255, px[(50 * 100 + 50) * 4 + 3]);
        Assert.True(Near(px, 50 * 100 + 50, 0, 150, 70));
    }

    [Fact]
    public async Task Convert_to_svg_traces_with_defaults_and_keeps_the_picture_size()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("صورة الشعار.png"), 64, 48);

        var result = await Run("convert:svg", png);

        Assert.True(result.Success, result.Error);
        Assert.Equal(dir.Combine("صورة الشعار (converted).svg"), result.Outputs[0]);
        var root = XDocument.Load(result.Outputs[0]).Root!;
        Assert.Equal("svg", root.Name.LocalName);
        Assert.Equal("64", root.Attribute("width")!.Value);
        Assert.Equal("48", root.Attribute("height")!.Value);
        Assert.InRange(root.Elements().Count(e => e.Name.LocalName == "path"), 2, 8);   // the default palette has 8 colours
    }

    [Fact]
    public async Task Convert_to_svgz_writes_gzip_compressed_svg()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"), 40, 30);

        var result = await Run("convert:svgz", png);

        Assert.True(result.Success, result.Error);
        Assert.EndsWith("pic (converted).svgz", result.Outputs[0]);
        var bytes = File.ReadAllBytes(result.Outputs[0]);
        Assert.Equal(new byte[] { 0x1F, 0x8B }, bytes.Take(2).ToArray());
        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        Assert.Equal("svg", XDocument.Load(gz).Root!.Name.LocalName);
    }

    [Fact]
    public async Task Big_pictures_are_traced_at_a_working_size_but_keep_their_dimensions()
    {
        using var dir = new TempDir();
        var png = dir.Combine("big.png");
        using (var image = new MagickImage(MagickColors.White, 3000, 2000))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Black).Circle(1500, 1000, 1500, 300));
            image.Write(png);
        }

        var result = await Run("img.trace", png, new() { ["mode"] = "bw", ["detail"] = "low" });

        Assert.True(result.Success, result.Error);
        var root = XDocument.Load(result.Outputs[0]).Root!;
        Assert.Equal("3000", root.Attribute("width")!.Value);
        Assert.Equal("2000", root.Attribute("height")!.Value);
        var viewBox = root.Attribute("viewBox")!.Value.Split(' ').Select(double.Parse).ToArray();
        Assert.True(viewBox[2] < 3000 && Math.Abs(viewBox[2] / viewBox[3] - 1.5) < 0.01);
    }

    [Fact]
    public void Trace_options_have_sensible_defaults_and_limits()
    {
        var options = Tool("img.trace").OptionsFor("img.trace");
        Assert.Equal(["mode", "colors", "detail"], options.Select(o => o.Key));
        Assert.Equal("color", options[0].Default);
        Assert.Equal((2d, 16d), (options[1].Min!.Value, options[1].Max!.Value));
        Assert.Equal(["low", "medium", "high"], options[2].Choices!);
        Assert.Empty(Tool("convert:svg").OptionsFor("convert:svg"));
    }

    [Fact]
    public async Task The_colour_count_option_is_honoured_and_clamped()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("gradient.png"), 64, 48);

        var two = await Run("img.trace", png, new() { ["colors"] = 2 });
        var many = await Run("img.trace", png, new() { ["colors"] = 99 });

        Assert.Equal(2, XDocument.Load(two.Outputs[0]).Descendants().Count(e => e.Name.LocalName == "path"));
        Assert.InRange(XDocument.Load(many.Outputs[0]).Descendants().Count(e => e.Name.LocalName == "path"), 3, 16);
    }

    [Fact]
    public void Supports_is_truthful()
    {
        var tracer = Tool("img.trace");
        Assert.True(tracer.Supports("img.trace", "jpg"));
        Assert.True(tracer.Supports("convert:svg", "dng"));
        Assert.True(tracer.Supports("convert:svgz", "webp"));
        Assert.False(tracer.Supports("img.trace", "svg"), "already a vector");
        Assert.False(tracer.Supports("convert:svg", "svgz"), "svgz to svg is a decompression, not a trace");
        Assert.False(tracer.Supports("convert:svg", "pdf"));
        Assert.False(tracer.Supports("convert:png", "jpg"));
    }

    [Fact]
    public async Task A_corrupt_picture_fails_cleanly_and_cancellation_writes_nothing()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("broken.png", "nope");
        var bad2 = await Run("img.trace", bad);
        Assert.False(bad2.Success);
        Assert.Contains("broken.png", bad2.Error);

        var png = Samples.Png(dir.Combine("pic.png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("img.trace", png, ct: cts.Token));
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_result()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"));
        dir.WriteText("pic (traced).svg", "precious");

        var result = await Run("img.trace", png);

        Assert.Equal(dir.Combine("pic (traced 2).svg"), result.Outputs[0]);
        Assert.Equal("precious", File.ReadAllText(dir.Combine("pic (traced).svg")));
    }

    // ---------- helpers ----------

    private static int SignedArea(IReadOnlyList<(int X, int Y)> loop)
    {
        long sum = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var (x1, y1) = loop[i];
            var (x2, y2) = loop[(i + 1) % loop.Count];
            sum += (long)x1 * y2 - (long)x2 * y1;
        }
        return (int)(sum / 2);
    }

    private static double DistanceTo(IEnumerable<CubicBezier> curves, Vec p) =>
        curves.Min(c => Enumerable.Range(0, 401).Min(i => (c.At(i / 400.0) - p).Length));

    /// <summary>Renders the SVG with librsvg (Magick.NET) at its own size.</summary>
    internal static string Rasterise(string svg, bool transparent = false)
    {
        var settings = new MagickReadSettings { Format = svg.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase) ? MagickFormat.Svgz : MagickFormat.Svg };
        if (transparent) settings.BackgroundColor = MagickColors.Transparent;
        using var image = new MagickImage(svg, settings);
        var png = svg + ".render.png";
        image.Write(png, MagickFormat.Png32);
        return png;
    }

    private static bool[] DarkMask(string path, out int width, out int height)
    {
        using var image = new MagickImage(path);
        image.ColorSpace = ColorSpace.sRGB;
        width = (int)image.Width;
        height = (int)image.Height;
        var px = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        return Enumerable.Range(0, px.Length / 4).Select(i => px[i * 4 + 3] > 127 && (px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2]) / 3 < 128).ToArray();
    }

    private static bool Near(byte[] px, int i, int r, int g, int b, int tolerance = 40) =>
        Math.Abs(px[i * 4] - r) <= tolerance && Math.Abs(px[i * 4 + 1] - g) <= tolerance && Math.Abs(px[i * 4 + 2] - b) <= tolerance;

    private static double Iou(bool[] a, bool[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int inter = 0, union = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] && b[i]) inter++;
            if (a[i] || b[i]) union++;
        }
        return union == 0 ? 1 : (double)inter / union;
    }
}
