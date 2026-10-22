using System.Xml.Linq;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Images;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Archive.Tests;

/// <summary>The styled "create" options of <c>img.qr</c>: shapes, colours, logo and the svg/png formats.</summary>
public class QrStyleConverterTests
{
    private static QrConverter Converter => new();

    private static Dictionary<string, object?> Create(string text, params (string Key, object? Value)[] extra)
    {
        var options = new Dictionary<string, object?> { ["mode"] = "create", ["text"] = text };
        foreach (var (key, value) in extra) options[key] = value;
        return options;
    }

    [Fact]
    public void Offers_the_style_options_with_plain_defaults()
    {
        var options = Converter.OptionsFor("img.qr").ToDictionary(o => o.Key);

        Assert.Equal(["square", "rounded", "dots", "diamond", "fluid", "bars"], options["modules"].Choices);
        Assert.Equal("square", options["modules"].Default);
        Assert.Equal(["square", "rounded", "circle", "leaf"], options["eyes"].Choices);
        Assert.Equal("square", options["eyes"].Default);
        Assert.Equal(["png", "svg", "both"], options["format"].Choices);
        Assert.Equal("png", options["format"].Default);
        Assert.Equal((ToolOptionKind.Text, "#000000"), (options["color"].Kind, options["color"].Default));
        Assert.Equal((ToolOptionKind.Text, ""), (options["color2"].Kind, options["color2"].Default));
        Assert.Equal((ToolOptionKind.Text, ""), (options["eyeColor"].Kind, options["eyeColor"].Default));
        Assert.Equal((ToolOptionKind.Text, "#ffffff"), (options["background"].Kind, options["background"].Default));
        Assert.Equal((ToolOptionKind.Text, ""), (options["logo"].Kind, options["logo"].Default));
        Assert.Equal(["mode", "text", "errorCorrection", "size", "quietZone"], options.Keys.Take(5));   // the old options keep their order
    }

    [Fact]
    public async Task A_styled_png_is_written_and_scans()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create("https://example.com/styled",
            ("modules", "fluid"), ("eyes", "leaf"), ("color", "#1e3a8a"), ("color2", "#9333ea"), ("eyeColor", "#be123c"), ("background", "#fefce8"), ("size", 700)));

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("a (qr).png", Path.GetFileName(output));
        using var picture = new MagickImage(output);
        Assert.Equal(700u, picture.Width);
        Assert.Equal("https://example.com/styled", Assert.Single(QrCodeService.Read(output)).Text);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task Svg_format_writes_only_an_svg_that_another_renderer_can_scan()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create("svg out", ("format", "svg"), ("modules", "dots"), ("eyes", "circle"), ("size", 640)));

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("a (qr).svg", Path.GetFileName(output));
        var svg = File.ReadAllText(output);
        Assert.Equal("640", XDocument.Parse(svg).Root!.Attribute("width")!.Value);
        Assert.True(QrDesigner.Check(QrDesignerTests.Rasterise(svg, 640), "svg out").Scans);
    }

    [Fact]
    public async Task Plain_options_can_also_be_written_as_svg()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create("plain svg", ("format", "SVG")));

        Assert.True(result.Success, result.Error);
        Assert.Equal("a (qr).svg", Path.GetFileName(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Both_writes_a_png_and_an_svg_and_never_overwrites_either()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));
        File.WriteAllText(temp.Combine("a (qr).png"), "PRECIOUS PNG");
        File.WriteAllText(temp.Combine("a (qr).svg"), "PRECIOUS SVG");

        var result = await Kit.Run(Converter, "img.qr", image, Create("both", ("format", "both"), ("modules", "rounded")));

        Assert.True(result.Success, result.Error);
        Assert.Equal(["a (qr 2).png", "a (qr 2).svg"], result.Outputs.Select(Path.GetFileName));
        Assert.Equal("PRECIOUS PNG", File.ReadAllText(temp.Combine("a (qr).png")));
        Assert.Equal("PRECIOUS SVG", File.ReadAllText(temp.Combine("a (qr).svg")));
        Assert.Equal("both", Assert.Single(QrCodeService.Read(result.Outputs[0])).Text);
        XDocument.Parse(File.ReadAllText(result.Outputs[1]));
    }

    [Fact]
    public async Task A_logo_with_the_automatic_level_scans_and_auto_without_a_logo_is_M()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));
        var logo = QrDesignerTests.MakeLogo(temp.Combine($"{Kit.ArabicFile}.png"));
        const string text = "https://example.com/with-a-logo";

        var withLogo = await Kit.Run(Converter, "img.qr", image, Create(text, ("logo", logo), ("errorCorrection", "auto"), ("modules", "dots"), ("format", "both")));
        Assert.True(withLogo.Success, withLogo.Error);
        Assert.Equal(text, Assert.Single(QrCodeService.Read(withLogo.Outputs[0])).Text);
        Assert.Contains("data:image/png;base64,", File.ReadAllText(withLogo.Outputs[1]));

        // auto without a logo picks M: same module count as an explicit M
        var auto = await Kit.Run(Converter, "img.qr", image, Create(text, ("errorCorrection", "auto"), ("format", "svg")));
        var m = await Kit.Run(Converter, "img.qr", image, Create(text, ("errorCorrection", "M"), ("format", "svg")));
        Assert.Equal(ViewBox(auto.Outputs[0]), ViewBox(m.Outputs[0]));
        var h = await Kit.Run(Converter, "img.qr", image, Create(text, ("errorCorrection", "H"), ("format", "svg")));
        Assert.NotEqual(ViewBox(m.Outputs[0]), ViewBox(h.Outputs[0]));
    }

    [Fact]
    public async Task A_transparent_background_gives_a_see_through_png()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create("see through", ("background", "transparent")));

        Assert.True(result.Success, result.Error);
        using var picture = new MagickImage(result.Outputs[0]);
        Assert.True(picture.HasAlpha);
        Assert.Equal(0, picture.GetPixels().GetPixel(1, 1).ToColor()!.A);
        Assert.Equal("see through", Assert.Single(QrCodeService.Read(result.Outputs[0])).Text);
    }

    [Fact]
    public async Task Nothing_to_encode_fails_for_every_format_and_creates_nothing()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        foreach (var format in new[] { "png", "svg", "both" })
        {
            var result = await Kit.Run(Converter, "img.qr", image, Create("", ("format", format), ("modules", "dots")));
            Assert.False(result.Success);
            Assert.Contains("Enter the text", result.Error);
        }
        Assert.Equal(["a.png"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("modules", "hexagon")]
    [InlineData("eyes", "star")]
    [InlineData("format", "pdf")]
    [InlineData("color", "not a colour")]
    [InlineData("color2", "#12")]
    [InlineData("background", "nope")]
    [InlineData("errorCorrection", "Z")]
    public async Task Bad_style_values_fail_clearly_and_create_nothing(string key, string value)
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create("bad", ("format", "both"), (key, value)));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task A_missing_logo_file_fails_and_creates_nothing()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create("x", ("logo", temp.Combine("ghost.png")), ("format", "both")));

        Assert.False(result.Success);
        Assert.Contains("ghost.png", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Text_too_long_for_a_styled_code_fails_clearly()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, Create(new string('q', 5000), ("modules", "dots"), ("format", "both")));

        Assert.False(result.Success);
        Assert.Contains("too long", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    private static string ViewBox(string svgPath) => XDocument.Load(svgPath).Root!.Attribute("viewBox")!.Value;
}
