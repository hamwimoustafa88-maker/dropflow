using System.Xml.Linq;
using DropFlow.Engines.Archive.Images;
using DropFlow.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Archive.Tests;

public class QrDesignerTests
{
    private const string Link = "https://dropflow.scouthub.dev/qr?x=1";

    public static TheoryData<QrModuleShape, QrEyeShape> EveryShape()
    {
        var data = new TheoryData<QrModuleShape, QrEyeShape>();
        foreach (var module in Enum.GetValues<QrModuleShape>())
            foreach (var eye in Enum.GetValues<QrEyeShape>())
                data.Add(module, eye);
        return data;
    }

    public static TheoryData<QrModuleShape> EveryModule()
    {
        var data = new TheoryData<QrModuleShape>();
        foreach (var module in Enum.GetValues<QrModuleShape>()) data.Add(module);
        return data;
    }

    // ---------------------------------------------------------------- matrix and levels

    [Fact]
    public void A_short_text_is_a_version_1_code_of_21_by_21_modules_with_finder_patterns_in_three_corners()
    {
        var m = QrDesigner.Matrix("hello", QrErrorCorrection.M);

        Assert.Equal(21, m.GetLength(0));
        Assert.Equal(21, m.GetLength(1));
        foreach (var (row, col) in new[] { (0, 0), (0, 14), (14, 0) })
        {
            Assert.True(m[row, col]);                 // frame corner
            Assert.True(m[row + 6, col + 6]);
            Assert.False(m[row + 1, col + 1]);        // light ring
            Assert.True(m[row + 3, col + 3]);         // ball centre
        }

        var bigger = QrDesigner.Matrix(new string('a', 200), QrErrorCorrection.M);
        Assert.True(bigger.GetLength(0) > 21);
        Assert.Equal(0, (bigger.GetLength(0) - 17) % 4);  // 17 + 4 * version
    }

    [Fact]
    public void Matrix_is_row_major_like_the_zxing_writer()
    {
        // The timing pattern runs along row 6 and column 6: dark, light, dark... between the finder patterns.
        var m = QrDesigner.Matrix(new string('z', 60), QrErrorCorrection.Q);
        var n = m.GetLength(0);
        for (var i = 8; i < n - 8; i++)
        {
            Assert.Equal(i % 2 == 0, m[6, i]);
            Assert.Equal(i % 2 == 0, m[i, 6]);
        }
        // the always-dark module sits beside the bottom-left finder pattern: row n-8, column 8
        Assert.True(m[n - 8, 8]);
    }

    [Fact]
    public void Text_that_does_not_fit_throws_the_too_large_exception()
    {
        var ex = Assert.Throws<QrTooLargeException>(() => QrDesigner.Matrix(new string('y', 5000), QrErrorCorrection.L));
        Assert.Contains("too long", ex.Message);
        Assert.Throws<ArgumentException>(() => QrDesigner.Matrix("", QrErrorCorrection.M));
    }

    [Fact]
    public void Effective_level_is_the_explicit_one_else_H_with_a_logo_else_M()
    {
        var plain = new QrStyle();
        var withLogo = new QrStyle(LogoPath: @"C:\logo.png");

        Assert.Equal(QrErrorCorrection.M, QrDesigner.EffectiveLevel(new QrDesign("x", plain)));
        Assert.Equal(QrErrorCorrection.H, QrDesigner.EffectiveLevel(new QrDesign("x", withLogo)));
        Assert.Equal(QrErrorCorrection.M, QrDesigner.EffectiveLevel(new QrDesign("x", new QrStyle(LogoPath: "  "))));
        Assert.Equal(QrErrorCorrection.L, QrDesigner.EffectiveLevel(new QrDesign("x", withLogo, QrErrorCorrection.L)));
        Assert.Equal(QrErrorCorrection.Q, QrDesigner.EffectiveLevel(new QrDesign("x", plain, QrErrorCorrection.Q)));
    }

    // ---------------------------------------------------------------- every style scans

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void Every_module_shape_with_every_eye_shape_scans(QrModuleShape module, QrEyeShape eye)
    {
        var design = new QrDesign(Link, new QrStyle(module, eye), Size: 480);

        var png = QrDesigner.RenderPng(design);

        using var image = new MagickImage(png);
        Assert.Equal(480u, image.Width);
        Assert.Equal(480u, image.Height);
        var check = QrDesigner.Check(png, Link);
        Assert.True(check.Scans, $"{module}/{eye} decoded as '{check.DecodedText}'");
        Assert.Equal(Link, check.DecodedText);
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void Every_shape_scans_with_a_gradient_a_separate_eye_colour_and_a_coloured_background(QrModuleShape module, QrEyeShape eye)
    {
        var style = new QrStyle(module, eye, Foreground: "#1d3fbf", Foreground2: "#7a0f6b", GradientAngle: 30, EyeColor: "#c2410c", Background: "#fff8e7");

        var check = QrDesigner.Check(QrDesigner.RenderPng(new QrDesign(Link, style, Size: 480)), Link);

        Assert.True(check.Scans, $"{module}/{eye}: '{check.DecodedText}'");
    }

    [Theory]
    [MemberData(nameof(EveryModule))]
    public void Every_module_shape_scans_on_a_transparent_background_and_the_png_keeps_its_alpha(QrModuleShape module)
    {
        var png = QrDesigner.RenderPng(new QrDesign(Link, new QrStyle(module, QrEyeShape.Rounded, Foreground: "#203040", Background: "transparent"), Size: 400));

        using var image = new MagickImage(png);
        Assert.True(image.HasAlpha);
        Assert.Equal(0, Alpha(image, 2, 2));                 // the quiet zone is see-through
        Assert.True(QrDesigner.Check(png, Link).Scans, module.ToString());
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void Every_shape_scans_with_a_logo_at_the_automatic_level(QrModuleShape module, QrEyeShape eye)
    {
        using var temp = new TempDir();
        var logo = MakeLogo(temp.Combine("logo.png"));
        var design = new QrDesign(Link, new QrStyle(module, eye, Foreground: "#0b3d2e", Foreground2: "#1f7a5a", LogoPath: logo, LogoScale: 0.3), Size: 560);

        Assert.Equal(QrErrorCorrection.H, QrDesigner.EffectiveLevel(design));
        var check = QrDesigner.Check(QrDesigner.RenderPng(design), Link);
        Assert.True(check.Scans, $"{module}/{eye}: '{check.DecodedText}'");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("hi")]
    [InlineData("WIFI:T:WPA;S:Cafe;P:12345678;;")]
    public void Even_the_shortest_texts_scan_with_the_biggest_logo(string text)
    {
        using var temp = new TempDir();
        var logo = MakeLogo(temp.Combine("logo.png"));

        foreach (var plate in new[] { true, false })
        {
            var design = new QrDesign(text, new QrStyle(QrModuleShape.Fluid, QrEyeShape.Leaf, LogoPath: logo, LogoScale: 0.3, LogoPlate: plate), Size: 512);
            var check = QrDesigner.Check(QrDesigner.RenderPng(design), text);
            Assert.True(check.Scans, $"'{text}' plate={plate}: '{check.DecodedText}'");
        }
    }

    [Fact]
    public void Arabic_text_round_trips_in_a_styled_code_and_in_the_svg()
    {
        var text = $"{Kit.ArabicText} - https://example.com/مرحبا";
        var design = new QrDesign(text, new QrStyle(QrModuleShape.Dots, QrEyeShape.Circle, "#0f172a", "#2563eb"), Size: 640);

        Assert.Equal(text, QrDesigner.Check(QrDesigner.RenderPng(design), text).DecodedText);
        Assert.True(QrDesigner.Check(Rasterise(QrDesigner.RenderSvg(design with { Style = design.Style with { Foreground2 = null } }), 640), text).Scans);
    }

    // ---------------------------------------------------------------- pixels

    [Theory]
    [InlineData(64)]
    [InlineData(333)]
    [InlineData(777)]
    [InlineData(1024)]
    public void The_png_is_exactly_the_requested_size(int size)
    {
        using var image = new MagickImage(QrDesigner.RenderPng(new QrDesign("size", new QrStyle(QrModuleShape.Rounded), Size: size)));
        Assert.Equal((uint)size, image.Width);
        Assert.Equal((uint)size, image.Height);
    }

    [Fact]
    public void Default_design_is_1024_pixels_and_opaque()
    {
        using var image = new MagickImage(QrDesigner.RenderPng(new QrDesign("default", new QrStyle())));
        Assert.Equal(1024u, image.Width);
        Assert.False(image.HasAlpha);
    }

    [Fact]
    public void Colours_land_where_they_belong()
    {
        // 21 modules + 2 * 4 quiet = 29 units; 29 * 20 px = 20 px per module
        var design = new QrDesign("colours", new QrStyle(Foreground: "#ff0000", EyeColor: "#0000ff", Background: "#00ff00"), Size: 580);
        using var image = new MagickImage(QrDesigner.RenderPng(design));

        Assert.Equal((0, 255, 0), Rgb(image, 10, 10));                 // quiet zone: background
        Assert.Equal((0, 0, 255), Rgb(image, Px(4 + 3.5), Px(4 + 3.5))); // eye ball: eye colour
        Assert.Equal((0, 0, 255), Rgb(image, Px(4 + 0.5), Px(4 + 3.5))); // eye frame
        Assert.Equal((0, 255, 0), Rgb(image, Px(4 + 1.5), Px(4 + 3.5))); // light ring inside the eye

        var m = QrDesigner.Matrix("colours", QrErrorCorrection.M);
        var (row, col) = FirstDataModule(m, (r, c) => m[r, c]);
        Assert.Equal((255, 0, 0), Rgb(image, Px(4 + col + 0.5), Px(4 + row + 0.5)));
    }

    [Fact]
    public void A_gradient_runs_along_its_angle_and_eyes_follow_it_when_they_have_no_colour_of_their_own()
    {
        var style = new QrStyle(Foreground: "#000000", Foreground2: "#ff0000", GradientAngle: 0);
        using var leftToRight = new MagickImage(QrDesigner.RenderPng(new QrDesign("gradient", style, Size: 580)));
        using var topToBottom = new MagickImage(QrDesigner.RenderPng(new QrDesign("gradient", style with { GradientAngle = 90 }, Size: 580)));

        // balls of the three eyes: top-left (col 3.5), top-right (col 17.5), bottom-left (row 17.5)
        int Red(MagickImage i, double col, double row) => Rgb(i, Px(4 + col), Px(4 + row)).R;

        Assert.True(Red(leftToRight, 3.5, 3.5) < 70);
        Assert.True(Red(leftToRight, 17.5, 3.5) > 180);
        Assert.InRange(Red(leftToRight, 3.5, 17.5) - Red(leftToRight, 3.5, 3.5), -8, 8);

        Assert.True(Red(topToBottom, 3.5, 3.5) < 70);
        Assert.True(Red(topToBottom, 3.5, 17.5) > 180);
        Assert.InRange(Red(topToBottom, 17.5, 3.5) - Red(topToBottom, 3.5, 3.5), -8, 8);
    }

    [Fact]
    public void Eyes_are_drawn_as_whole_shapes_not_as_module_dots()
    {
        // With dot modules a gap shows between two diagonal dots; the square eye frame has none.
        using var image = new MagickImage(QrDesigner.RenderPng(new QrDesign("eyes", new QrStyle(QrModuleShape.Dots, QrEyeShape.Square), Size: 580)));

        Assert.True(IsDark(image, Px(4 + 1.0), Px(4 + 0.02 + 0.45)));   // frame between modules (0,0) and (0,1)
        Assert.True(IsDark(image, Px(4 + 0.05), Px(4 + 0.05)));        // the frame's square corner
    }

    [Fact]
    public void Fluid_modules_merge_with_their_neighbours_and_round_only_free_corners()
    {
        const string text = "fluid corners 12345";
        var m = QrDesigner.Matrix(text, QrErrorCorrection.M);
        var units = m.GetLength(0) + 8;
        var size = units * 20;
        using var fluid = new MagickImage(QrDesigner.RenderPng(new QrDesign(text, new QrStyle(QrModuleShape.Fluid), Size: size)));
        using var square = new MagickImage(QrDesigner.RenderPng(new QrDesign(text, new QrStyle(QrModuleShape.Square), Size: size)));

        // a data module with nothing above and nothing to the left: its top-left corner is rounded off
        var (r1, c1) = FirstDataModule(m, (r, c) => m[r, c] && !Dark(m, r - 1, c) && !Dark(m, r, c - 1) && !Dark(m, r - 1, c - 1));
        Assert.False(IsDark(fluid, Px(4 + c1 + 0.06), Px(4 + r1 + 0.06)), $"free corner at ({r1},{c1}) should be round");
        Assert.True(IsDark(square, Px(4 + c1 + 0.06), Px(4 + r1 + 0.06)));

        // a data module with a dark right neighbour: the corner on that side stays square, so the two merge
        var (r2, c2) = FirstDataModule(m, (r, c) => m[r, c] && Dark(m, r, c + 1) && !IsFinder(m, r, c + 1) && !Dark(m, r - 1, c) && !Dark(m, r - 1, c + 1));
        Assert.True(IsDark(fluid, Px(4 + c2 + 1.0), Px(4 + r2 + 0.06)), $"joined edge at ({r2},{c2}) should stay square");
    }

    [Fact]
    public void Bars_join_vertical_runs_and_keep_columns_apart()
    {
        const string text = "bars bars bars";
        var m = QrDesigner.Matrix(text, QrErrorCorrection.M);
        var size = (m.GetLength(0) + 8) * 20;
        using var bars = new MagickImage(QrDesigner.RenderPng(new QrDesign(text, new QrStyle(QrModuleShape.Bars), Size: size)));

        var (r1, c1) = FirstDataModule(m, (r, c) => m[r, c] && Dark(m, r + 1, c) && !IsFinder(m, r + 1, c));
        Assert.True(IsDark(bars, Px(4 + c1 + 0.5), Px(4 + r1 + 1.0)), "two stacked modules form one bar");

        var (r2, c2) = FirstDataModule(m, (r, c) => m[r, c] && Dark(m, r, c + 1) && !IsFinder(m, r, c + 1));
        Assert.False(IsDark(bars, Px(4 + c2 + 1.0), Px(4 + r2 + 0.5)), "side-by-side modules stay separate bars");
    }

    [Fact]
    public void The_logo_sits_in_the_middle_on_a_plate_that_hides_the_modules_under_it()
    {
        using var temp = new TempDir();
        var logo = temp.Combine("red.png");
        using (var red = new MagickImage(MagickColors.Red, 64, 64)) red.Write(logo);
        var design = new QrDesign("logo in the middle", new QrStyle(Background: "#ffffff", LogoPath: logo, LogoScale: 0.2), Size: 800);

        using var image = new MagickImage(QrDesigner.RenderPng(design));

        Assert.Equal((255, 0, 0), Rgb(image, 400, 400));
        var m = QrDesigner.Matrix("logo in the middle", QrDesigner.EffectiveLevel(design));
        var n = m.GetLength(0);
        var scale = 800.0 / (n + 8);
        var halfLogo = n * 0.2 / 2 * scale;
        // just outside the logo but on the plate: background white, whatever the module there was
        for (var dx = -1; dx <= 1; dx += 2)
            Assert.Equal((255, 255, 255), Rgb(image, (int)(400 + dx * (halfLogo + scale * 0.3)), 400));

        // without the plate the same spots are module-coloured or background, but the logo is still on top
        using var noPlate = new MagickImage(QrDesigner.RenderPng(design with { Style = design.Style with { LogoPlate = false } }));
        Assert.Equal((255, 0, 0), Rgb(noPlate, 400, 400));
    }

    [Fact]
    public void A_missing_logo_or_a_bad_colour_is_an_argument_error()
    {
        Assert.Throws<FileNotFoundException>(() => QrDesigner.RenderPng(new QrDesign("x", new QrStyle(LogoPath: @"C:\no\such\logo.png"))));
        Assert.Throws<ArgumentException>(() => QrDesigner.RenderPng(new QrDesign("x", new QrStyle(Foreground: "blue-ish"))));
        Assert.Throws<ArgumentException>(() => QrDesigner.RenderSvg(new QrDesign("x", new QrStyle(Background: "#12345"))));
        Assert.Throws<ArgumentException>(() => QrDesigner.RenderPng(new QrDesign("", new QrStyle())));
    }

    [Fact]
    public void Short_and_alpha_colours_are_accepted()
    {
        var png = QrDesigner.RenderPng(new QrDesign("alpha", new QrStyle(Foreground: "#000", Background: "#ffffff80"), Size: 300));
        using var image = new MagickImage(png);
        Assert.True(image.HasAlpha);
        Assert.InRange(Alpha(image, 2, 2), 120, 136);
    }

    // ---------------------------------------------------------------- svg

    [Fact]
    public void The_svg_is_well_formed_with_a_module_viewbox_and_the_pixel_size()
    {
        var design = new QrDesign("svg check", new QrStyle(QrModuleShape.Fluid, QrEyeShape.Leaf, "#112233", "#445566", 60, "#aa0000"), Size: 900, QuietZone: 3);
        var n = QrDesigner.Matrix("svg check", QrErrorCorrection.M).GetLength(0);

        var doc = XDocument.Parse(QrDesigner.RenderSvg(design));

        XNamespace svg = "http://www.w3.org/2000/svg";
        Assert.Equal(svg + "svg", doc.Root!.Name);
        Assert.Equal($"0 0 {n + 6} {n + 6}", doc.Root.Attribute("viewBox")!.Value);
        Assert.Equal("900", doc.Root.Attribute("width")!.Value);
        Assert.Equal("900", doc.Root.Attribute("height")!.Value);
        var gradient = Assert.Single(doc.Descendants(svg + "linearGradient"));
        Assert.Equal(["#112233", "#445566"], gradient.Elements(svg + "stop").Select(s => s.Attribute("stop-color")!.Value));
        var paths = doc.Descendants(svg + "path").ToList();
        Assert.Contains(paths, p => p.Attribute("fill")!.Value == $"url(#{gradient.Attribute("id")!.Value})");
        Assert.Contains(paths, p => p.Attribute("fill")!.Value == "#aa0000" && p.Attribute("fill-rule")!.Value == "evenodd");
        Assert.Contains(doc.Descendants(svg + "rect"), r => r.Attribute("fill")?.Value == "#ffffff");
    }

    [Fact]
    public void A_transparent_svg_has_no_background_and_the_logo_is_embedded_as_a_data_uri()
    {
        using var temp = new TempDir();
        var logo = MakeLogo(temp.Combine($"{Kit.ArabicFile}.png"));

        var doc = XDocument.Parse(QrDesigner.RenderSvg(new QrDesign(Link, new QrStyle(Background: "transparent", LogoPath: logo))));

        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        Assert.DoesNotContain(doc.Descendants(svg + "rect"), r => r.Attribute("width")?.Value == doc.Root!.Attribute("viewBox")!.Value.Split(' ')[2]);
        var image = Assert.Single(doc.Descendants(svg + "image"));
        var href = image.Attribute(xlink + "href")!.Value;
        Assert.StartsWith("data:image/png;base64,", href);
        using var embedded = new MagickImage(Convert.FromBase64String(href["data:image/png;base64,".Length..]));
        Assert.True(embedded.Width > 0);
        // the plate is white because the background is transparent
        Assert.Contains(doc.Descendants(svg + "path"), p => p.Attribute("fill")!.Value == "#ffffff");
    }

    [Theory]
    [InlineData(QrModuleShape.Square, QrEyeShape.Square)]
    [InlineData(QrModuleShape.Rounded, QrEyeShape.Rounded)]
    [InlineData(QrModuleShape.Dots, QrEyeShape.Circle)]
    [InlineData(QrModuleShape.Fluid, QrEyeShape.Leaf)]
    [InlineData(QrModuleShape.Bars, QrEyeShape.Rounded)]
    [InlineData(QrModuleShape.Diamond, QrEyeShape.Square)]
    public void The_svg_rasterised_by_another_renderer_also_scans(QrModuleShape module, QrEyeShape eye)
    {
        var design = new QrDesign(Link, new QrStyle(module, eye, Foreground: "#102030", EyeColor: "#401010"), Size: 600);

        var check = QrDesigner.Check(Rasterise(QrDesigner.RenderSvg(design), 600), Link);

        Assert.True(check.Scans, $"{module}/{eye}: '{check.DecodedText}'");
    }

    [Fact]
    public void The_svg_and_the_png_draw_the_same_picture()
    {
        var design = new QrDesign("same picture", new QrStyle(QrModuleShape.Rounded, QrEyeShape.Circle, "#000000"), Size: 400);
        using var png = new MagickImage(QrDesigner.RenderPng(design));
        using var svg = new MagickImage(Rasterise(QrDesigner.RenderSvg(design), 400));
        png.ColorType = ColorType.TrueColor;
        svg.ColorType = ColorType.TrueColor;

        var difference = png.Compare(svg, ErrorMetric.RootMeanSquared);

        Assert.True(difference < 0.08, $"RMS difference {difference}");
    }

    // ---------------------------------------------------------------- check

    [Fact]
    public void Check_reports_a_picture_without_a_code_and_a_code_with_other_text()
    {
        using var blank = new MagickImage(MagickColors.White, 300, 300);
        blank.Format = MagickFormat.Png;

        Assert.Equal(new QrScanCheck(false, null), QrDesigner.Check(blank.ToByteArray(), "x"));
        var other = QrDesigner.Check(QrDesigner.RenderPng(new QrDesign("one", new QrStyle(), Size: 300)), "two");
        Assert.False(other.Scans);
        Assert.Equal("one", other.DecodedText);
        Assert.False(QrDesigner.Check(Kit.Noise(500), "x").Scans);
    }

    // ---------------------------------------------------------------- content

    [Fact]
    public void Text_content_is_kept_as_is()
    {
        Assert.Equal("  any text\n", QrContent.Text("  any text\n"));
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  example.com/path?q=1  ", "https://example.com/path?q=1")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("HTTPS://Example.com", "HTTPS://Example.com")]
    [InlineData("ftp://files.example.com", "ftp://files.example.com")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("tel:+9611234567", "tel:+9611234567")]
    [InlineData("tel:1234567", "tel:1234567")]
    [InlineData("localhost:8080/x", "https://localhost:8080/x")]
    [InlineData("example.com:8443", "https://example.com:8443")]
    [InlineData("", "")]
    public void Links_get_https_only_when_they_have_no_scheme(string input, string expected)
    {
        Assert.Equal(expected, QrContent.Link(input));
    }

    [Fact]
    public void Wifi_escapes_special_characters_and_follows_the_common_format()
    {
        Assert.Equal("WIFI:T:WPA;S:Home Net;P:pass word;;", QrContent.Wifi("Home Net", "pass word", "WPA", false));
        Assert.Equal(@"WIFI:T:WPA;S:a\;b\,c\:d\""e\\f;P:\;\,\:\""\\;H:true;;", QrContent.Wifi("a;b,c:d\"e\\f", ";,:\"\\", "wpa", true));
        Assert.Equal("WIFI:T:nopass;S:Open Cafe;;", QrContent.Wifi("Open Cafe", "ignored", "nopass", false));
        Assert.Equal("WIFI:T:nopass;S:Open;H:true;;", QrContent.Wifi("Open", null, "nopass", true));
        Assert.Equal("WIFI:T:WEP;S:Old;P:12345;;", QrContent.Wifi("Old", "12345", "WEP", false));
        Assert.Equal($"WIFI:T:WPA;S:{Kit.ArabicText};P:;;", QrContent.Wifi(Kit.ArabicText, null, "WPA", false));
    }

    [Fact]
    public void A_wifi_code_with_awkward_characters_scans_back_exactly()
    {
        var text = QrContent.Wifi("My;Net:\"5G\"", @"p@ss\word,1", "WPA", true);
        Assert.Equal(text, QrDesigner.Check(QrDesigner.RenderPng(new QrDesign(text, new QrStyle(QrModuleShape.Rounded), Size: 500)), text).DecodedText);
    }

    // ---------------------------------------------------------------- helpers

    internal static string MakeLogo(string path)
    {
        using var logo = new MagickImage(MagickColors.Transparent, 200, 200);
        new Drawables()
            .FillColor(new MagickColor("#e11d48")).Circle(100, 100, 100, 5)
            .FillColor(MagickColors.White).Rectangle(60, 85, 140, 115)
            .FillColor(new MagickColor("#facc15")).Polygon(new PointD(100, 20), new PointD(130, 70), new PointD(70, 70))
            .Draw(logo);
        logo.Write(path);
        return path;
    }

    internal static byte[] Rasterise(string svg, int size)
    {
        var settings = new MagickReadSettings { Format = MagickFormat.Svg, BackgroundColor = MagickColors.White, Width = (uint)size, Height = (uint)size };
        using var image = new MagickImage(System.Text.Encoding.UTF8.GetBytes(svg), settings);
        image.Alpha(AlphaAction.Remove);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static int Px(double units) => (int)Math.Floor(units * 20);

    private static bool Dark(bool[,] m, int r, int c) => r >= 0 && c >= 0 && r < m.GetLength(0) && c < m.GetLength(1) && m[r, c];

    private static bool IsFinder(bool[,] m, int r, int c)
    {
        var n = m.GetLength(0);
        return (r < 8 && c < 8) || (r < 8 && c >= n - 8) || (r >= n - 8 && c < 8);
    }

    private static (int Row, int Col) FirstDataModule(bool[,] m, Func<int, int, bool> match)
    {
        var n = m.GetLength(0);
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
                if (!IsFinder(m, r, c) && match(r, c)) return (r, c);
        throw new InvalidOperationException("no such module");
    }

    private static (int R, int G, int B) Rgb(MagickImage image, int x, int y)
    {
        var c = image.GetPixels().GetPixel(x, y).ToColor()!;
        return (c.R, c.G, c.B);
    }

    private static int Alpha(MagickImage image, int x, int y) => image.GetPixels().GetPixel(x, y).ToColor()!.A;

    private static bool IsDark(MagickImage image, int x, int y)
    {
        var (r, g, b) = Rgb(image, x, y);
        return (r + g + b) / 3.0 < 128;
    }
}
