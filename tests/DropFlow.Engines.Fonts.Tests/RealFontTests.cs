using DropFlow.Engines.Fonts.Sfnt;
using DropFlow.Engines.Fonts.Web;
using ImageMagick;

namespace DropFlow.Engines.Fonts.Tests;

/// <summary>
/// Round trips on real fonts, and the engine's readers against web fonts made by other tools. Fonts are read from where they are
/// installed (they are not redistributable); each test is skipped, visibly, when its font is missing.
/// </summary>
public sealed class RealFontTests
{
    [SystemFontFact("arial.ttf", "Arial.ttf")]
    public void Arial_survives_woff_and_woff2_round_trips()
    {
        var bytes = File.ReadAllBytes(TestFonts.SystemFont("arial.ttf", "Arial.ttf")!);
        var font = SfntFont.Read(bytes);

        var woff = Woff.Encode(font);
        var fromWoff = Woff.Decode(woff);
        TestFonts.AssertEquivalent(font, fromWoff);

        var woff2 = Woff2.Encode(font);
        var fromWoff2 = Woff2.Decode(woff2);
        TestFonts.AssertEquivalent(font, fromWoff2, "DSIG");
        TestFonts.AssertChecksums(fromWoff2.Write());

        Assert.True(woff.Length < bytes.Length, "WOFF is smaller than the TTF");
        Assert.True(woff2.Length < woff.Length, "WOFF2 is smaller than WOFF");
    }

    [SystemFontFact("AdobeArabic-Regular.otf", "ApercuArabicPro-Regular.otf", "AA-WATEEN.otf")]
    public void A_cff_font_survives_woff_and_woff2_round_trips()
    {
        var bytes = File.ReadAllBytes(TestFonts.SystemFont("AdobeArabic-Regular.otf", "ApercuArabicPro-Regular.otf", "AA-WATEEN.otf")!);
        var font = SfntFont.Read(bytes);
        Assert.True(font.HasCffOutlines);
        TestFonts.AssertEquivalent(font, Woff.Decode(Woff.Encode(font)));
        var fromWoff2 = Woff2.Decode(Woff2.Encode(font));
        TestFonts.AssertEquivalent(font, fromWoff2);
        // CFF data is not transformed: head is kept unchanged apart from the adjustment.
        Assert.Equal(font.Tables["CFF "], fromWoff2.Tables["CFF "]);
    }

    [WebFontSetFact("glyphicons-halflings-regular")]
    public void Third_party_woff2_with_transformed_glyphs_decodes_to_the_original_ttf_glyphs() =>
        AssertWebFontsMatchTtf("glyphicons-halflings-regular");

    [WebFontSetFact("fa-solid-900")]
    public void Font_awesome_woff_and_woff2_decode_to_its_ttf() => AssertWebFontsMatchTtf("fa-solid-900");

    private static void AssertWebFontsMatchTtf(string stem)
    {
        var path = TestFonts.WebFontSet(stem)!;
        var ttf = SfntFont.Read(File.ReadAllBytes(path + ".ttf"));

        var woff2 = Woff2.Decode(File.ReadAllBytes(path + ".woff2"));
        AssertSameGlyphs(ttf, woff2);
        TestFonts.AssertChecksums(woff2.Clone().Write());
        foreach (var tag in new[] { "cmap", "hhea", "hmtx", "maxp", "name", "OS/2", "post" })
            if (ttf.Tables.ContainsKey(tag) && woff2.Tables.ContainsKey(tag)) Assert.True(ttf.Tables[tag].AsSpan().SequenceEqual(woff2.Tables[tag]), $"{stem}.woff2: '{tag}' differs");

        if (File.Exists(path + ".woff"))
        {
            var woff = Woff.Decode(File.ReadAllBytes(path + ".woff"));
            AssertSameGlyphs(ttf, woff);
        }
    }

    private static void AssertSameGlyphs(SfntFont expected, SfntFont actual)
    {
        var a = GlyfTable.Read(expected);
        var b = GlyfTable.Read(actual);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++) TestFonts.AssertSameGlyph(a[i], b[i], i, pointBoundsAllowed: true);
    }

    [WebFontSetFact("fa-solid-900")]
    public void Eot_header_matches_one_made_by_another_tool()
    {
        var path = TestFonts.WebFontSet("fa-solid-900")!;
        if (!File.Exists(path + ".eot")) return;
        var theirs = Eot.Read(File.ReadAllBytes(path + ".eot"));
        var ttfBytes = File.ReadAllBytes(path + ".ttf");
        var ours = Eot.Read(Eot.Write(SfntFont.Read(ttfBytes), ttfBytes));
        Assert.Equal(theirs.Panose, ours.Panose);
        Assert.Equal(theirs.Weight, ours.Weight);
        Assert.Equal(theirs.Italic, ours.Italic);
        Assert.Equal(theirs.FsType, ours.FsType);
        Assert.Equal(theirs.UnicodeRange, ours.UnicodeRange);
        Assert.Equal(theirs.CodePageRange, ours.CodePageRange);
        Assert.Equal(theirs.Family, ours.Family);
        Assert.Equal(theirs.Style, ours.Style);
        Assert.Equal(theirs.FullName, ours.FullName);
        Assert.Equal(0x504C, theirs.Magic);
    }

    /// <summary>FreeType (inside ImageMagick) draws the same text pixel for pixel with the original font and with the round-tripped one.</summary>
    [SystemFontFact("arial.ttf", "Arial.ttf")]
    public void FreeType_draws_the_round_tripped_font_exactly_like_the_original()
    {
        using var dir = new TestSupport.TempDir();
        var original = TestFonts.SystemFont("arial.ttf", "Arial.ttf")!;
        var bytes = File.ReadAllBytes(original);
        var viaWoff2 = dir.Combine("via-woff2.ttf");
        File.WriteAllBytes(viaWoff2, Woff2.Decode(Woff2.Encode(SfntFont.Read(bytes))).Write());
        var viaWoff = dir.Combine("via-woff.ttf");
        File.WriteAllBytes(viaWoff, Woff.Decode(Woff.Encode(SfntFont.Read(bytes))).Write());

        using var expected = Render(original, "DropFlow Ag&@ 0123");
        using var fromWoff2 = Render(viaWoff2, "DropFlow Ag&@ 0123");
        using var fromWoff = Render(viaWoff, "DropFlow Ag&@ 0123");
        Assert.True(expected.Width > 100, "something was drawn");
        // Control: FreeType really reads the file it is given (a different font draws different pixels, a broken one is refused).
        var other = TestFonts.SystemFont("cour.ttf", "times.ttf", "tahoma.ttf");
        if (other is not null)
        {
            using var different = Render(other, "DropFlow Ag&@ 0123");
            Assert.True(different.Width != expected.Width || expected.Compare(different, ErrorMetric.Absolute) > 0);
        }
        var broken = dir.Combine("broken.ttf");
        File.WriteAllBytes(broken, bytes.AsSpan(0, 2000).ToArray());
        Assert.ThrowsAny<MagickException>(() => Render(broken, "DropFlow").Dispose());
        Assert.Equal(0, expected.Compare(fromWoff2, ErrorMetric.Absolute));
        Assert.Equal(0, expected.Compare(fromWoff, ErrorMetric.Absolute));
    }

    internal static MagickImage Render(string fontPath, string text)
    {
        var settings = new MagickReadSettings
        {
            Font = fontPath,
            FontPointsize = 48,
            BackgroundColor = MagickColors.White,
            FillColor = MagickColors.Black,
        };
        var image = new MagickImage("label:" + text, settings);
        return image;
    }
}
