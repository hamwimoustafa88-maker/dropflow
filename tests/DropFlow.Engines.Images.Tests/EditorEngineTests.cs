using DropFlow.Core.Operations;
using DropFlow.Engines.Images;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Images.Tests;

/// <summary>Engine features the visual editors need: relative annotations, pen, highlight, magnifier, sized overlays, background styles.</summary>
public class EditorEngineTests
{
    private static readonly IReadOnlyList<IConverter> All = ImagesModule.Create(new FormatConverterTests.NoEngines());

    private static Task<ConversionResult> Run(string tool, string input, Dictionary<string, object?> options) =>
        All.Single(c => c.Operations.Contains(tool)).RunAsync(new ConversionJob([input], tool, options), new Progress<JobProgress>(), default);

    private static string White(TempDir dir, uint w, uint h, string name = "blank.png")
    {
        var path = dir.Combine(name);
        using var img = new MagickImage(MagickColors.White, w, h);
        img.Write(path);
        return path;
    }

    private static IMagickColor<byte> Pixel(string path, int x, int y)
    {
        using var img = new MagickImage(path);
        return img.GetPixels().GetPixel(x, y).ToColor()!;
    }

    [Fact]
    public async Task Relative_items_land_at_the_same_place_on_pictures_of_any_size()
    {
        using var dir = new TempDir();
        const string items = """[{"type":"rect","x":0.5,"y":0.5,"w":0.25,"h":0.25,"color":"#ff0000","fill":true,"stroke":0.002}]""";
        foreach (var (w, h) in new[] { (100u, 80u), (400u, 320u) })
        {
            var png = White(dir, w, h, $"p{w}.png");
            var r = await Run("img.annotate", png, new() { ["items"] = items, ["units"] = "relative" });
            Assert.True(r.Success, r.Error);
            var inside = Pixel(r.Outputs[0], (int)(w * 0.62), (int)(h * 0.62));
            var outside = Pixel(r.Outputs[0], (int)(w * 0.3), (int)(h * 0.3));
            Assert.True(inside.R > 200 && inside.G < 60, $"{w}: inside {inside}");
            Assert.True(outside.G > 240, $"{w}: outside {outside}");
        }
    }

    [Fact]
    public async Task Pen_highlight_and_magnifier_draw()
    {
        using var dir = new TempDir();
        var png = dir.Combine("p.png");
        using (var img = new MagickImage(MagickColors.White, 200, 200))
        {
            new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Blue).Rectangle(20, 20, 30, 30).Draw(img);
            img.Write(png);
        }
        const string items = """
            [{"type":"pen","points":[10,150,60,160,110,150],"color":"#00aa00","stroke":6},
             {"type":"highlight","x":120,"y":10,"w":60,"h":30,"color":"#ffeb3b"},
             {"type":"magnify","x":25,"y":25,"w":10,"x2":140,"y2":120,"zoom":3,"color":"#000000","stroke":2}]
            """;
        var r = await Run("img.annotate", png, new() { ["items"] = items });
        Assert.True(r.Success, r.Error);

        var pen = Pixel(r.Outputs[0], 60, 160);
        Assert.True(pen.G > 120 && pen.R < 80, $"pen {pen}");
        var marker = Pixel(r.Outputs[0], 150, 25);
        Assert.True(marker.B < 200 && marker.R > 200, $"highlight {marker}");     // yellow-ish over white, translucent
        var lens = Pixel(r.Outputs[0], 140, 120);                              // centre of the lens shows the blue square, enlarged
        Assert.True(lens.B > 200 && lens.R < 60, $"lens {lens}");
    }

    [Fact]
    public async Task An_overlay_is_stretched_to_the_size_given()
    {
        using var dir = new TempDir();
        var png = White(dir, 100, 100);
        var overlay = dir.Combine("o.png");
        using (var o = new MagickImage(MagickColors.Red, 10, 10)) o.Write(overlay);
        var items = AnnotationRenderer.ToJson([new AnnotationItem("image", X: 0.2, Y: 0.2, W: 0.5, H: 0.5, Path: overlay)]);
        var r = await Run("img.annotate", png, new() { ["items"] = items, ["units"] = "relative" });
        Assert.True(r.Success, r.Error);
        Assert.True(Pixel(r.Outputs[0], 65, 65).G < 40, "inside the stretched overlay");
        Assert.True(Pixel(r.Outputs[0], 75, 75).G > 240, "outside it");
    }

    [Fact]
    public void Items_survive_the_json_round_trip()
    {
        AnnotationItem[] items = [new("pen", Points: [0.1, 0.2, 0.3, 0.4], Color: "#123456", Stroke: 0.01), new("number", 0.5, 0.5, N: 3)];
        var back = AnnotationRenderer.Parse(AnnotationRenderer.ToJson(items));
        Assert.Equal(items[0].Points, back[0].Points);
        Assert.Equal(3, back[1].N);
        Assert.Equal("#123456", back[0].Color);
    }

    [Fact]
    public async Task Blurred_self_background_uses_the_picture_colours()
    {
        using var dir = new TempDir();
        var png = dir.Combine("red.png");
        using (var img = new MagickImage(MagickColors.Red, 100, 60)) img.Write(png);
        var r = await Run("img.background", png, new() { ["background"] = "blur", ["padding"] = 20d });
        Assert.True(r.Success, r.Error);
        var corner = Pixel(r.Outputs[0], 2, 2);
        Assert.True(corner.R > 150 && corner.G < 60, $"corner {corner}");
    }

    [Fact]
    public void A_gradient_angle_turns_the_gradient()
    {
        using var picture = new MagickImage(MagickColors.Gray, 50, 50);
        using var down = BackgroundComposer.Compose(picture, "gradient:#000000:#ffffff:180", "original", 30, 0, false);
        using var across = BackgroundComposer.Compose(picture, "gradient:#000000:#ffffff:90", "original", 30, 0, false);
        double G(MagickImage i, int x, int y) => i.GetPixels().GetPixel(x, y).ToColor()!.R;
        // two opposite corners differ in both, but along different axes
        var w = (int)down.Width - 1;
        var h = (int)down.Height - 1;
        Assert.True(Math.Abs(G(down, 1, 1) - G(down, 1, h)) > 100 || Math.Abs(G(down, 1, 1) - G(down, w, 1)) > 100);
        Assert.NotEqual(Math.Abs(G(down, 1, 1) - G(down, w, 1)) > 100, Math.Abs(G(across, 1, 1) - G(across, w, 1)) > 100);
    }

    [Fact]
    public void The_composer_preview_matches_the_full_result_scaled_down()
    {
        using var big = new MagickImage("gradient:#ff0000-#0000ff", new MagickReadSettings { Width = 800, Height = 600 });
        using var full = BackgroundComposer.Compose(big, "blur", "1:1", 12, 10, true);
        using var small = (MagickImage)big.Clone();
        small.Resize(200, 150);
        using var preview = BackgroundComposer.Compose(small, "blur", "1:1", 12, 10, true);
        full.Resize(preview.Width, preview.Height);
        Assert.True(full.Compare(preview, ErrorMetric.MeanAbsolute) < 0.03);
    }
}
