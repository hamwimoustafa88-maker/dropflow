using System.Buffers.Binary;
using DropFlow.Engines.Fonts.Outlines;
using DropFlow.Engines.Fonts.Sfnt;
using DropFlow.Engines.Fonts.Web;
using ImageMagick;

namespace DropFlow.Engines.Fonts.Tests;

/// <summary>TTF to OTF (quadratic to cubic, exact) and OTF to TTF (cubic to quadratic, within a font unit).</summary>
public sealed class OutlineConversionTests
{
    [Fact]
    public void Ttf_to_otf_writes_cff_outlines_with_the_same_shapes_and_widths()
    {
        var ttf = SfntFont.Read(TestFonts.Tiny());
        var otf = OutlineConverter.TrueTypeToCff(ttf, default);
        Assert.Equal(SfntFont.CffFlavor, otf.Flavor);
        Assert.False(otf.Tables.ContainsKey("glyf"));
        Assert.False(otf.Tables.ContainsKey("loca"));
        Assert.Equal(6, otf.Tables["maxp"].Length);
        Assert.Equal(0x00030000u, BinaryPrimitives.ReadUInt32BigEndian(otf.Tables["post"]));
        TestFonts.AssertChecksums(otf.Clone().Write());

        var cff = CffFont.Parse(otf.Tables["CFF "]);
        Assert.Equal(5, cff.CharStrings.Length);
        var glyphs = GlyfTable.Read(ttf);
        int[] advances = [500, 600, 6100, 700, 250];
        for (var g = 0; g < glyphs.Count; g++)
        {
            var (contours, width) = cff.Draw(g);
            Assert.Equal(advances[g], width);
            var expected = ContourMath.FromTrueType(ContourMath.TrueTypeContours(glyphs, g));
            Assert.Equal(expected.Count, contours.Count);
            for (var c = 0; c < expected.Count; c++)
            {
                AssertClose(expected[c].Start, contours[c].Start);
                Assert.Equal(expected[c].Segments.Count, contours[c].Segments.Count);
                for (var s = 0; s < expected[c].Segments.Count; s++)
                {
                    Assert.Equal(expected[c].Segments[s].IsLine, contours[c].Segments[s].IsLine);
                    AssertClose(expected[c].Segments[s].End, contours[c].Segments[s].End);
                    AssertClose(expected[c].Segments[s].C1, contours[c].Segments[s].C1);
                    AssertClose(expected[c].Segments[s].C2, contours[c].Segments[s].C2);
                }
            }
        }
        // The composite "C" is flattened: the contours of A plus those of B.
        Assert.Equal(3, cff.Draw(3).Contours.Count);
    }

    [Fact]
    public void Ttf_to_otf_and_back_gives_the_original_points()
    {
        var ttf = SfntFont.Read(TestFonts.Tiny());
        var back = OutlineConverter.CffToTrueType(OutlineConverter.TrueTypeToCff(ttf, default), default);
        TestFonts.AssertChecksums(back.Clone().Write());
        var original = GlyfTable.Read(ttf);
        var converted = GlyfTable.Read(back);
        for (var g = 0; g < 3; g++) // simple glyphs: every original point comes back exactly
        {
            var before = Points(original[g]);
            var after = Points(converted[g]);
            Assert.Subset(after, before);
            Assert.Equal(original[g].EndPoints.Length, converted[g].EndPoints.Length);
            Assert.Equal((original[g].XMin, original[g].YMin, original[g].XMax, original[g].YMax), converted[g].PointBounds());
        }
        Assert.Equal(GlyphKind.Simple, converted[3].Kind); // the composite became a simple glyph
        Assert.Equal(3, converted[3].EndPoints.Length);
        Assert.Equal(GlyphKind.Empty, converted[4].Kind);
        Assert.Equal(ttf.Tables["cmap"], back.Tables["cmap"]);
        Assert.Equal(ttf.Tables["OS/2"], back.Tables["OS/2"]);
    }

    [Fact]
    public void Outer_contours_keep_the_direction_each_format_expects()
    {
        // TrueType: clockwise (negative signed area with y up). CFF: counter-clockwise.
        var ttf = SfntFont.Read(TestFonts.Tiny());
        var notdef = GlyfTable.Read(ttf)[0];

        var cff = CffFont.Parse(OutlineConverter.TrueTypeToCff(ttf, default).Tables["CFF "]);
        var contour = cff.Draw(0).Contours[0];
        var xs = new[] { contour.Start.X }.Concat(contour.Segments.Select(s => s.End.X)).ToArray();
        var ys = new[] { contour.Start.Y }.Concat(contour.Segments.Select(s => s.End.Y)).ToArray();
        Assert.NotEqual(IsCounterClockwise(notdef), Area(xs, ys) > 0);

        static bool IsCounterClockwise(Glyph g) => Area(g.X.Select(x => (double)x).ToArray(), g.Y.Select(y => (double)y).ToArray()) > 0;
    }

    [Fact]
    public void Cubic_curves_are_split_until_the_quadratic_pieces_are_within_tolerance()
    {
        var p0 = new Pt(0, 0); var p1 = new Pt(0, 1000); var p2 = new Pt(1000, 1000); var p3 = new Pt(1000, 0);
        var pieces = ContourMath.CubicToQuads(p0, p1, p2, p3, 1).ToList();
        Assert.True(pieces.Count is > 1 and < 20);
        // Sample the cubic and check the distance to the quadratic pieces.
        var start = p0;
        var worst = 0.0;
        for (var i = 0; i < pieces.Count; i++)
        {
            for (var k = 0; k <= 20; k++)
            {
                var t = k / 20.0;
                var u = 1 - t;
                var q = start * (u * u) + pieces[i].Control * (2 * u * t) + pieces[i].End * (t * t);
                worst = Math.Max(worst, Enumerable.Range(0, 2001).Min(j => (Cubic(p0, p1, p2, p3, j / 2000.0) - q).Length));
            }
            start = pieces[i].End;
        }
        Assert.True(worst <= 1.0, $"largest distance {worst}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(107.0)]
    [InlineData(-107.0)]
    [InlineData(108.0)]
    [InlineData(1131.0)]
    [InlineData(-1131.0)]
    [InlineData(1132.0)]
    [InlineData(-32000.0)]
    [InlineData(0.5)]
    [InlineData(-123.25)]
    [InlineData(2.0 / 3)]
    public void Charstring_numbers_round_trip(double value)
    {
        var w = new BigEndianWriter();
        CffWriter.Number(w, value);
        CffWriter.Number(w, 0);
        w.U8(21); // rmoveto
        w.U8(14);
        var interpreter = new Type2Charstring([], new CffPrivate([], 0, 0));
        interpreter.Run(w.ToArray());
        // An rmoveto alone draws nothing; check through a line instead.
        var line = new BigEndianWriter();
        CffWriter.Number(line, 0); CffWriter.Number(line, 0); line.U8(21);
        CffWriter.Number(line, value); CffWriter.Number(line, 1); line.U8(5);
        line.U8(14);
        var drawn = new Type2Charstring([], new CffPrivate([], 0, 0));
        drawn.Run(line.ToArray());
        Assert.Equal(Math.Round(value * 65536) / 65536, drawn.Contours[0].Segments[0].End.X, 6);
    }

    [Fact]
    public void Subroutines_flex_and_widths_are_interpreted()
    {
        // Local subr 0 (bias 107 -> index -107) draws a line; the charstring has a width, a hint, the call, a flex and endchar.
        var subr = new BigEndianWriter();
        CffWriter.Number(subr, 100); CffWriter.Number(subr, 0); subr.U8(5); subr.U8(11);
        var code = new BigEndianWriter();
        CffWriter.Number(code, 50);                   // width = nominal 400 + 50
        CffWriter.Number(code, 0); CffWriter.Number(code, 10); code.U8(1); // hstem
        CffWriter.Number(code, 10); CffWriter.Number(code, 20); code.U8(21); // rmoveto
        CffWriter.Number(code, -107); code.U8(10);    // callsubr 0
        foreach (var v in new[] { 10, 10, 10, 10, 10, 0, 10, 0, 10, -10, 10, -10, 50 }) CffWriter.Number(code, v);
        code.U8(12); code.U8(35);                     // flex
        code.U8(14);
        var interpreter = new Type2Charstring([], new CffPrivate([subr.ToArray()], 600, 400));
        interpreter.Run(code.ToArray());
        Assert.Equal(450, interpreter.Width);
        var contour = Assert.Single(interpreter.Contours);
        Assert.Equal(new Pt(10, 20), contour.Start);
        Assert.Equal(3, contour.Segments.Count);
        Assert.Equal(new Pt(110, 20), contour.Segments[0].End);
        Assert.Equal(new Pt(170, 20), contour.Segments[2].End);
    }

    [SystemFontFact("arial.ttf", "Arial.ttf")]
    public void Arial_as_otf_draws_like_arial() =>
        AssertDrawsAlike(TestFonts.SystemFont("arial.ttf", "Arial.ttf")!, f => OutlineConverter.TrueTypeToCff(f, default), "otf");

    [SystemFontFact("AdobeArabic-Regular.otf", "ApercuArabicPro-Regular.otf")]
    public void An_arabic_otf_as_ttf_draws_like_the_original() =>
        AssertDrawsAlike(TestFonts.SystemFont("AdobeArabic-Regular.otf", "ApercuArabicPro-Regular.otf")!, f => OutlineConverter.CffToTrueType(f, default), "ttf", "العربية Arabic 123");

    [SystemFontFact("ArbFONTS-NotoKufiArabic-VariableFont_wght.ttf")]
    public void A_variable_font_is_not_turned_into_an_otf_but_still_becomes_woff2()
    {
        var font = SfntFont.Read(File.ReadAllBytes(TestFonts.SystemFont("ArbFONTS-NotoKufiArabic-VariableFont_wght.ttf")!));
        Assert.Contains("Variable", Assert.Throws<FontFailure>(() => OutlineConverter.TrueTypeToCff(font, default)).Message);
        TestFonts.AssertEquivalent(font, Woff2.Decode(Woff2.Encode(font)), "DSIG");
    }

    private static void AssertDrawsAlike(string path, Func<SfntFont, SfntFont> convert, string extension, string text = "DropFlow Ag&@ 0123")
    {
        using var dir = new TestSupport.TempDir();
        var converted = convert(SfntFont.Read(File.ReadAllBytes(path)));
        var output = dir.Combine("converted." + extension);
        var bytes = converted.Write();
        TestFonts.AssertChecksums(bytes);
        File.WriteAllBytes(output, bytes);

        using var expected = Render(path, text);
        using var actual = Render(output, text);
        Assert.Equal(expected.Width, actual.Width);   // same advances
        Assert.Equal(expected.Height, actual.Height); // same vertical metrics
        // Hinting differs between the formats, so edges are anti-aliased a little differently. The yardstick: the same original text moved
        // by one pixel. The converted font must draw closer to the original than that (measured: about 0.05 against 0.11).
        using var shifted = (MagickImage)expected.Clone();
        shifted.Roll(1, 0);
        var onePixel = expected.Compare(shifted, ErrorMetric.RootMeanSquared);
        var difference = expected.Compare(actual, ErrorMetric.RootMeanSquared);
        Assert.True(difference < onePixel * 0.6, $"the converted font draws differently (RMSE {difference:F4}, one-pixel shift {onePixel:F4})");
    }

    private static MagickImage Render(string fontPath, string text) =>
        new("label:" + text, new MagickReadSettings { Font = fontPath, FontPointsize = 120, BackgroundColor = MagickColors.White, FillColor = MagickColors.Black });

    private static HashSet<(int, int, bool)> Points(Glyph g) =>
        g.X.Select((x, i) => (x, g.Y[i], g.OnCurvePoints[i])).ToHashSet();

    private static double Area(double[] xs, double[] ys)
    {
        var area = 0.0;
        for (var i = 0; i < xs.Length; i++)
        {
            var j = (i + 1) % xs.Length;
            area += xs[i] * ys[j] - xs[j] * ys[i];
        }
        return area / 2;
    }

    private static Pt Cubic(Pt p0, Pt p1, Pt p2, Pt p3, double t)
    {
        var u = 1 - t;
        return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
    }

    private static void AssertClose(Pt expected, Pt actual)
    {
        Assert.True(Math.Abs(expected.X - actual.X) < 1e-4 && Math.Abs(expected.Y - actual.Y) < 1e-4, $"expected {expected}, got {actual}");
    }
}
