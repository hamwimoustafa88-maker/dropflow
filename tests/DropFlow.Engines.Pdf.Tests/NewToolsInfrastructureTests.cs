using System.Text;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>The small pieces the crop, sign, redact and compare tools are built from, tested on their own.</summary>
public class NewToolsInfrastructureTests
{
    // ---- NumberList -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("10, 20,30.5 ,5", 10, 20, 30.5, 5)]
    [InlineData("10;20;30.5;5", 10, 20, 30.5, 5)]
    [InlineData("10 20 30.5 5", 10, 20, 30.5, 5)]
    [InlineData("10%, 20 %,30.5%,5%", 10, 20, 30.5, 5)]
    [InlineData("10mm,20mm,30.5mm,5mm", 10, 20, 30.5, 5)]
    [InlineData("١٠،٢٠،٣٠٫٥،٥", 10, 20, 30.5, 5)]
    public void Number_lists_accept_separators_units_and_arabic_digits(string text, double a, double b, double c, double d)
    {
        Assert.True(NumberList.TryParse(text, 4, out var values));
        Assert.Equal([a, b, c, d], values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("1,2,three,4")]
    [InlineData("1,2,NaN,4")]
    [InlineData("1,2,,4,")]
    public void Number_lists_reject_the_wrong_count_or_text(string text) =>
        Assert.False(NumberList.TryParse(text, 4, out _));

    // ---- PageGeometry ---------------------------------------------------------------------------------------------------

    private static PageGeometry GeometryOf(int rotate, double[]? crop = null, double[]? media = null)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnitPt.FromPoint(612);
        page.Height = XUnitPt.FromPoint(792);
        if (media is not null) page.MediaBox = new PdfRectangle(new XPoint(media[0], media[1]), new XPoint(media[2], media[3]));
        if (crop is not null) page.CropBox = new PdfRectangle(new XPoint(crop[0], crop[1]), new XPoint(crop[2], crop[3]));
        page.Rotate = rotate;
        return PageGeometry.Of(page);
    }

    [Theory]
    // rotation, visual size, then the user-space point of the displayed top-left corner and of the displayed bottom-right corner
    [InlineData(0, 612, 792, 0, 792, 612, 0)]
    [InlineData(90, 792, 612, 0, 0, 612, 792)]
    [InlineData(180, 612, 792, 612, 0, 0, 792)]
    [InlineData(270, 792, 612, 612, 792, 0, 0)]
    public void Visual_corners_map_to_the_right_user_space_corners_for_every_rotation(int rotate, double vw, double vh, double tlX, double tlY, double brX, double brY)
    {
        var g = GeometryOf(rotate);

        Assert.Equal((vw, vh), (g.VisualWidth, g.VisualHeight));
        Assert.Equal((tlX, tlY), g.VisualToUser(0, 0));
        Assert.Equal((brX, brY), g.VisualToUser(vw, vh));
    }

    [Fact]
    public void A_visual_rectangle_becomes_a_normalised_user_rectangle()
    {
        var g = GeometryOf(90);

        // 50 pt from the displayed left, 20 pt from the displayed top, 100 wide, 40 tall: on a page rotated 90 degrees clockwise the
        // displayed left edge is the bottom of the sheet and the displayed top edge is its left.
        Assert.Equal((20, 50, 60, 150), g.UserRect(50, 20, 100, 40));
    }

    [Fact]
    public void The_crop_box_defines_the_visible_area_and_is_clipped_to_the_media_box()
    {
        var cropped = GeometryOf(0, crop: [50, 100, 450, 700]);
        Assert.Equal((50, 100, 450, 700), (cropped.X1, cropped.Y1, cropped.X2, cropped.Y2));
        Assert.Equal((400, 600), (cropped.VisualWidth, cropped.VisualHeight));

        var overhanging = GeometryOf(0, crop: [-50, -50, 400, 900]);
        Assert.Equal((0, 0, 400, 792), (overhanging.X1, overhanging.Y1, overhanging.X2, overhanging.Y2));

        var outside = GeometryOf(0, crop: [1000, 1000, 1200, 1200]);
        Assert.Equal((0, 0, 612, 792), (outside.X1, outside.Y1, outside.X2, outside.Y2));
    }

    [Fact]
    public void Graphics_matrix_puts_visual_points_where_pdfsharp_draws_them()
    {
        var g = GeometryOf(0);
        var m = g.VisualToGraphics();

        // PDFsharp draws with y down from the top of an unrotated page: the displayed point (10, 20) is (10, 20) there.
        Assert.Equal((10.0, 20.0), (Math.Round(10 * m.M11 + 20 * m.M21 + m.OffsetX, 6), Math.Round(10 * m.M12 + 20 * m.M22 + m.OffsetY, 6)));

        var rotated = GeometryOf(90).VisualToGraphics();
        // displayed (0,0) is the bottom-left of the sheet: x = 0, y = 792 in PDFsharp's y-down space
        Assert.Equal((0.0, 792.0), (rotated.OffsetX, rotated.OffsetY));
    }

    // ---- PixelOps -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Boxes_become_pixel_rectangles_rounded_outwards_clipped_and_flipped()
    {
        // a 100 x 200 pt page drawn as 200 x 400 pixels (2 px per point); y is up in points and down in pixels
        var rect = PixelOps.ToPixels(new VisualBox(10.2, 20.1, 30.4, 40.6), 100, 200, 200, 400);
        Assert.Equal(new PixelOps.PixelRect(20, 318, 61, 360), rect);   // x 20.4..60.8 -> 20..61;  y 400-81.2..400-40.2 -> 318..360

        var clipped = PixelOps.ToPixels(new VisualBox(-10, -10, 500, 500), 100, 200, 200, 400);
        Assert.Equal(new PixelOps.PixelRect(0, 0, 200, 400), clipped);

        Assert.True(PixelOps.ToPixels(new VisualBox(300, 300, 400, 400), 100, 200, 200, 400).IsEmpty);
    }

    [Fact]
    public void Fill_black_touches_only_the_rectangle_and_mosaic_averages_blocks()
    {
        const int w = 8, h = 8;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var white = ((i % w) + (i / w)) % 2 == 0;   // checkerboard
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (byte)(white ? 255 : 0);
            pixels[i * 4 + 3] = 255;
        }

        var mosaic = (byte[])pixels.Clone();
        PixelOps.Mosaic(mosaic, w, new PixelOps.PixelRect(0, 0, 4, 4), cell: 4);
        Assert.All(Enumerable.Range(0, 16).Select(i => mosaic[((i / 4) * w + i % 4) * 4]), v => Assert.InRange(v, 126, 129));
        Assert.Equal(pixels[(0 * w + 4) * 4], mosaic[(0 * w + 4) * 4]);    // outside the rectangle: untouched

        var filled = (byte[])pixels.Clone();
        PixelOps.FillBlack(filled, w, new PixelOps.PixelRect(2, 2, 6, 6));
        Assert.Equal(0, filled[(3 * w + 3) * 4]);
        Assert.Equal(255, filled[(3 * w + 3) * 4 + 3]);
        Assert.Equal(pixels[(0 * w + 0) * 4], filled[0]);
    }

    // ---- PdfScrubber ----------------------------------------------------------------------------------------------------

    private static byte[] PdfsharpFile(string title, string producerNote)
    {
        PdfSharpSupport.EnsureFonts();
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        doc.Info.Author = "Author (with) \\ parentheses";
        doc.Info.Subject = "Subject";
        doc.Info.Keywords = "k1, k2";
        doc.Info.Elements.SetString("/Producer", producerNote);
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page)) gfx.DrawString("Body text", new XFont("Arial", 14), XBrushes.Black, new XPoint(72, 100));
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void The_scrubber_blanks_info_xmp_and_generator_stamps_without_changing_the_length_or_breaking_the_file()
    {
        var original = PdfsharpFile("Top secret (draft)", "Some Word Processor 9");
        Assert.True(RawPdf.Contains(original, "Top secret"));
        Assert.True(RawPdf.Contains(original, "Some Word Processor"));
        Assert.True(RawPdf.Contains(original, "xmpmeta"));

        var scrubbed = PdfScrubber.RemoveDocumentMetadata(original);

        Assert.Equal(original.Length, scrubbed.Length);
        foreach (var leak in new[] { "Top secret", "Some Word Processor", "Author (with)", "xmpmeta", "/Producer", "/Creator", "/Title", "/Author", "/Subject", "/Keywords", "/CreationDate", "PDFsharp" })
            Assert.False(RawPdf.Contains(scrubbed, leak), $"'{leak}' survived");

        using var pig = Pig.Open(scrubbed);
        Assert.Equal(1, pig.NumberOfPages);
        Assert.Contains("Body text", pig.GetPage(1).Text);
        Assert.True(string.IsNullOrEmpty(pig.Information.Title));
        Assert.True(string.IsNullOrEmpty(pig.Information.Producer));
        var file = Path.Combine(Path.GetTempPath(), "dropflow-scrub-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(file, scrubbed);
            Assert.NotEmpty(PixelKit.RenderPage(file, 1, 36).Ink(128));   // PDFium opens and draws it as well
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_scrubber_leaves_files_it_does_not_recognise_alone()
    {
        var garbage = Encoding.Latin1.GetBytes("not a pdf at all, no trailer here /Producer(x) % Created with nothing");
        Assert.Equal(garbage, PdfScrubber.RemoveDocumentMetadata(garbage));

        var noTrailer = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj\n<</Producer(keep me)>>\nendobj\n%%EOF");
        Assert.Equal(noTrailer, PdfScrubber.RemoveDocumentMetadata(noTrailer));
    }

    // ---- PageTextIndex normalisation ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("Ａlice", "alice")]                       // full-width A
    [InlineData("ﬁnance", "finance")]                    // fi ligature
    [InlineData("ﻻ", "لا")]                    // lam-alef ligature (presentation form) -> lam, alef
    [InlineData("ﺎﻣﺣﺍ", "امحا")] // joined presentation forms -> base letters
    [InlineData("أحمد", "احمد")] // hamza above alef -> alef
    [InlineData("إبراهيم", "ابراهيم")]
    [InlineData("مُحَمّد", "محمد")]          // harakat removed
    [InlineData("مــحمد", "محمد")]                // tatweel removed
    [InlineData("١٢٣ ۴۵", "123 45")]                                       // Arabic-Indic and Persian digits
    [InlineData("مصطفى", "مصطفي")]                // alef maqsura -> yeh
    [InlineData("a b​c", "a bc")]                                                          // no-break space; zero-width space dropped
    public void Normalise_folds_case_compatibility_forms_and_arabic_variants(string raw, string expected) =>
        Assert.Equal(expected, PageTextIndex.Normalise(raw));

    [Fact]
    public void Terms_are_normalised_like_the_page_text_with_collapsed_spaces()
    {
        Assert.Equal("john smith", PageTextIndex.NormaliseTerm("  John   SMITH \t"));
        Assert.Equal("", PageTextIndex.NormaliseTerm("  ًـ  "));
    }

    [Fact]
    public void A_lone_surrogate_does_not_break_normalisation()
    {
        Assert.Equal("a\uD800b", PageTextIndex.Normalise("A\uD800B"));
    }

    // ---- ReserveSet -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Reserved_sets_share_one_number_and_clean_up_unless_committed()
    {
        using var dir = new TempDir();
        dir.WriteText("x vs y (compare).txt", "taken");
        var input = dir.Combine("x vs y.pdf");
        string[] set;
        using (var ctx = new JobContext(new DropFlow.Core.Operations.ConversionJob([input], "pdf.compare"), new RecordingProgress(), CancellationToken.None))
        {
            set = [.. ctx.ReserveSet(input, "compare", "pdf", "txt")];
            Assert.Equal([dir.Combine("x vs y (compare 2).pdf"), dir.Combine("x vs y (compare 2).txt")], set);
            Assert.All(set, p => Assert.True(File.Exists(p)));
        }

        Assert.All(set, p => Assert.False(File.Exists(p)));                         // not committed: removed again
        Assert.True(File.Exists(dir.Combine("x vs y (compare).txt")));              // someone else's file is never touched
    }
}
