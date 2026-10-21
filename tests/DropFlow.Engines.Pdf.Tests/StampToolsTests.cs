using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

public class StampToolsTests
{
    // ---- watermark ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Watermark_adds_the_text_to_every_page_and_keeps_the_original_text()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 3, n => $"Body{n}");

        var result = await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "CONFIDENTIAL"))));

        Assert.Equal(dir.Combine("doc (watermarked).pdf"), result.Outputs[0]);
        var texts = Kit.PageTexts(result.Outputs[0]);
        Assert.Equal(3, texts.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains($"Body{i + 1}", texts[i]);
            Assert.Contains("CONFIDENTIAL", texts[i]);
        }
    }

    [Fact]
    public async Task Watermark_is_drawn_in_the_middle_of_the_page_on_a_rising_diagonal()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => " ");

        var result = await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "DRAFT"), ("opacity", 1), ("angle", 45), ("color", "#000000"))));

        var page = PixelKit.RenderPage(result.Outputs[0], 1, dpi: 36);
        var ink = page.Ink(128);
        Assert.NotEmpty(ink);
        var (cx, cy) = (ink.Average(p => p.X), ink.Average(p => p.Y));
        Assert.InRange(cx, page.Width * 0.4, page.Width * 0.6);
        Assert.InRange(cy, page.Height * 0.4, page.Height * 0.6);
        Assert.True(PixelKit.Correlation(ink) < -0.5, "45 degrees counter-clockwise rises from bottom-left to top-right");
    }

    [Fact]
    public async Task Watermark_angle_zero_is_horizontal()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => " ");

        var result = await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "FLAT"), ("opacity", 1), ("angle", 0), ("color", "#000000"))));

        var bounds = PixelKit.RenderPage(result.Outputs[0], 1, 36).InkBounds(128)!.Value;
        Assert.True(bounds.MaxX - bounds.MinX > 3 * (bounds.MaxY - bounds.MinY), "wide and short");
    }

    [Fact]
    public async Task Watermark_opacity_controls_how_dark_the_stamp_is()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => " ");

        var faint = (await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "SAMPLE"), ("opacity", 0.2), ("color", "#000000"))))).Outputs[0];
        var strong = (await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "SAMPLE"), ("opacity", 1), ("color", "#000000"))))).Outputs[0];

        var faintDark = PixelKit.RenderPage(faint, 1, 72).Darkest;
        var strongDark = PixelKit.RenderPage(strong, 1, 72).Darkest;
        Assert.InRange(strongDark, 0, 30);
        Assert.InRange(faintDark, 180, 230); // 20 % black on white is about 204
    }

    [Fact]
    public async Task Watermark_respects_the_pages_option()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 3, n => $"Body{n}");

        var result = await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "ONLY2"), ("pages", "2"))));

        var texts = Kit.PageTexts(result.Outputs[0]);
        Assert.DoesNotContain("ONLY2", texts[0]);
        Assert.Contains("ONLY2", texts[1]);
        Assert.DoesNotContain("ONLY2", texts[2]);
    }

    [Fact]
    public async Task Watermark_lands_in_the_middle_of_a_rotated_page_too()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => " ");
        var rotated = (await Kit.Ok(Kit.RunAsync("pdf.rotate", [pdf], Kit.Opts(("degrees", 90))))).Outputs[0];

        var result = await Kit.Ok(Kit.RunAsync("pdf.watermark", [rotated], Kit.Opts(("text", "ROTATED"), ("opacity", 1), ("angle", 45), ("color", "#000000"))));

        var page = PixelKit.RenderPage(result.Outputs[0], 1, 36);
        Assert.True(page.Width > page.Height, "the page is landscape after rotation");
        var ink = page.Ink(128);
        Assert.NotEmpty(ink);
        Assert.InRange(ink.Average(p => p.X), page.Width * 0.4, page.Width * 0.6);
        Assert.InRange(ink.Average(p => p.Y), page.Height * 0.4, page.Height * 0.6);
        Assert.True(PixelKit.Correlation(ink) < -0.5, "still rises to the right as the reader sees it");
    }

    [Fact]
    public async Task Watermark_can_carry_arabic_text()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => " ");

        var result = await Kit.Ok(Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "سري للغاية"), ("opacity", 1), ("color", "#000000"))));

        // The text is drawn as joined presentation forms in visual order; PdfPig reads those code points back.
        var text = Kit.PageTexts(result.Outputs[0])[0];
        Assert.Contains(ArabicShaper.ToVisual("سري للغاية"), text, StringComparison.Ordinal);
        Assert.DoesNotContain("س", text, StringComparison.Ordinal); // no unjoined base letters: sin appears as its initial form
        Assert.NotEmpty(PixelKit.RenderPage(result.Outputs[0], 1, 36).Ink(128));
    }

    [Fact]
    public async Task Watermark_requires_text()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1);

        var result = await Kit.RunAsync("pdf.watermark", [pdf], Kit.Opts(("text", "  ")));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- page numbers ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_numbers_default_to_bottom_center_starting_at_one()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 3, n => $"Body{n}");

        var result = await Kit.Ok(Kit.RunAsync("pdf.page-numbers", [pdf]));

        Assert.Equal(dir.Combine("doc (numbered).pdf"), result.Outputs[0]);
        using var doc = PdfDocument.Open(result.Outputs[0]);
        for (var i = 1; i <= 3; i++)
        {
            var page = doc.GetPage(i);
            var number = page.Letters.Where(l => l.Value == i.ToString() && l.StartBaseLine.Y < 60).ToList();
            var letter = Assert.Single(number);
            Assert.InRange(letter.StartBaseLine.X, 290, 320); // centre of a 612 pt page
            Assert.InRange(letter.StartBaseLine.Y, 20, 40);   // 28 pt above the bottom edge
        }
    }

    [Theory]
    [InlineData("bottom-left", 20, 60, 20, 40)]
    [InlineData("bottom-right", 540, 600, 20, 40)]
    [InlineData("top-center", 290, 320, 740, 770)]
    [InlineData("top-left", 20, 60, 740, 770)]
    [InlineData("top-right", 540, 600, 740, 770)]
    public async Task Page_numbers_follow_the_chosen_position(string position, double minX, double maxX, double minY, double maxY)
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => "x");

        var result = await Kit.Ok(Kit.RunAsync("pdf.page-numbers", [pdf], Kit.Opts(("position", position), ("format", "7"))));

        using var doc = PdfDocument.Open(result.Outputs[0]);
        var letter = Assert.Single(doc.GetPage(1).Letters, l => l.Value == "7");
        Assert.InRange(letter.StartBaseLine.X, minX, maxX);
        Assert.InRange(letter.StartBaseLine.Y, minY, maxY);
    }

    [Fact]
    public async Task Page_numbers_support_start_value_and_format_placeholders()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 3, n => $"Body{n}");

        var result = await Kit.Ok(Kit.RunAsync("pdf.page-numbers", [pdf], Kit.Opts(("start", 5), ("format", "Page {n} of {total}"))));

        var texts = Kit.PageTexts(result.Outputs[0]);
        Assert.Contains("Page 5 of 3", texts[0].Replace("\n", " "));
        Assert.Contains("Page 7 of 3", texts[2].Replace("\n", " "));
    }

    [Fact]
    public async Task Page_numbers_can_use_arabic_indic_digits()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 2, n => $"Body{n}");

        var result = await Kit.Ok(Kit.RunAsync("pdf.page-numbers", [pdf], Kit.Opts(("numerals", "arabic-indic"))));

        var texts = Kit.PageTexts(result.Outputs[0]);
        Assert.Contains("١", texts[0]);
        Assert.Contains("٢", texts[1]);
    }

    [Fact]
    public async Task Page_numbers_appear_at_the_reader_visible_bottom_of_a_rotated_page()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1, _ => " ");
        var rotated = (await Kit.Ok(Kit.RunAsync("pdf.rotate", [pdf], Kit.Opts(("degrees", 90))))).Outputs[0];

        var result = await Kit.Ok(Kit.RunAsync("pdf.page-numbers", [rotated], Kit.Opts(("format", "88"), ("size", 14))));

        var page = PixelKit.RenderPage(result.Outputs[0], 1, 72);
        var bounds = page.InkBounds(128)!.Value;
        Assert.InRange((bounds.MinX + bounds.MaxX) / 2.0, page.Width * 0.45, page.Width * 0.55);
        Assert.True(bounds.MinY > page.Height * 0.9, "at the bottom as the reader sees it");
        Assert.True(bounds.MaxX - bounds.MinX > bounds.MaxY - bounds.MinY, "text runs horizontally");
    }

    [Fact]
    public async Task Page_numbers_reject_an_unknown_position()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("doc.pdf"), 1);

        var result = await Kit.RunAsync("pdf.page-numbers", [pdf], Kit.Opts(("position", "middle")));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Theory]
    [InlineData("pdf.watermark")]
    [InlineData("pdf.page-numbers")]
    public async Task Stamping_a_corrupt_file_fails_cleanly(string operation)
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("bad.pdf", "%PDF-1.4 junk");

        var result = await Kit.RunAsync(operation, [bad], Kit.Opts(("text", "X")));

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["bad.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public void FormatLabel_replaces_placeholders_and_converts_digits()
    {
        Assert.Equal("3 / 9", Tools.PdfStampTools.FormatLabel("{n} / {total}", 3, 9, false));
        Assert.Equal("٣ / ٩", Tools.PdfStampTools.FormatLabel("{n} / {total}", 3, 9, true));
    }
}
