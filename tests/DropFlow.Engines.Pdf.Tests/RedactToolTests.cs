using System.Text;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace DropFlow.Engines.Pdf.Tests;

public class RedactToolTests
{
    private const int Red = 200, Green = 30, Blue = 30;

    /// <summary>
    /// Three pages with metadata that names "Alice". Page 1: "Secret Alice account 4111" and a public line; page 2: nothing sensitive;
    /// page 3: "alice lives in Cairo" (lower case). With <paramref name="red"/> every page has a flat red background so colour can be checked.
    /// </summary>
    private static string Doc(TempDir dir, string name = "doc.pdf", int rotate = 0, bool red = false, int extraPages = 0)
    {
        var b = new PdfBuilder().Title("Alice's confidential report").Meta("Alice Author", "About Alice", "alice, secret");
        var texts = new List<string> { "Secret Alice account 4111", "Nothing to hide here", "alice lives in Cairo" };
        for (var i = 0; i < extraPages; i++) texts.Add($"Filler page {i + 4}");
        for (var i = 0; i < texts.Count; i++)
        {
            var page = b.AddPage(612, 792, rotate);
            if (red) page.FillRect(0, 0, 612, 792, Red, Green, Blue);
            page.Text(texts[i], 72, 300, 20);                              // baseline 300 pt from the top: glyphs span about 285..305
            if (i == 0) page.Text("Public paragraph one", 72, 500, 14);
        }
        return b.Save(dir.Combine(name));
    }

    private static List<string> AllText(string pdf)
    {
        using var doc = Pig.Open(pdf);
        return doc.GetPages().Select(p => p.Text + " " + string.Concat(p.Letters.Select(l => l.Value))).ToList();
    }

    /// <summary>Where PdfPig finds a word on the original page (reader-visible points, y up) and the page size.</summary>
    private static (double X0, double Y0, double X1, double Y1, double W, double H) WordBox(string pdf, int page, string contains)
    {
        // Found through the letters (content-stream order is reading order for every rotation) and their own corner points.
        using var doc = Pig.Open(pdf);
        var p = doc.GetPage(page);
        var letters = p.Letters.ToList();
        var at = string.Concat(letters.Select(l => l.Value)).IndexOf(contains, StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0, $"'{contains}' is on the page");
        var corners = letters.Skip(at).Take(contains.Length)
            .SelectMany(l => new[] { l.BoundingBox.BottomLeft, l.BoundingBox.BottomRight, l.BoundingBox.TopLeft, l.BoundingBox.TopRight })
            .ToList();
        return (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y), p.Width, p.Height);
    }

    /// <summary>Every pixel inside the box (shrunk by <paramref name="inset"/> points) of the page drawn at 72 DPI is darker than 10.</summary>
    private static void AssertBlack(string pdf, int page, (double X0, double Y0, double X1, double Y1, double W, double H) box, double inset = 2)
    {
        var gray = PixelKit.RenderPage(pdf, page, 72);
        var (x0, x1) = ((int)Math.Ceiling(box.X0 + inset), (int)Math.Floor(box.X1 - inset));
        var (y0, y1) = ((int)Math.Ceiling(box.H - box.Y1 + inset), (int)Math.Floor(box.H - box.Y0 - inset));
        Assert.True(x1 > x0 && y1 > y0, "the box is big enough to test");
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                Assert.True(gray.At(x, y) < 10, $"pixel ({x},{y}) of page {page} should be black but is {gray.At(x, y)}");
    }

    private static (byte R, byte G, byte B) Pixel(string pdf, int page, int x, int y, double dpi = 72)
    {
        var (w, _, rgb) = PixelKit.RenderRgb(pdf, page, dpi);
        var i = (y * w + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    private static void AssertRed((byte R, byte G, byte B) p)
    {
        Assert.InRange(p.R, Red - 3, Red + 3);
        Assert.InRange(p.G, Green - 3, Green + 3);
        Assert.InRange(p.B, Blue - 3, Blue + 3);
    }

    // ---- terms ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redacted_word_is_gone_from_every_page_text_layer_and_untouched_pages_keep_their_text()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("doc (redacted).pdf"), output);
        Assert.Equal(3, Kit.PageCount(output));
        var all = AllText(output);
        Assert.All(all, text => Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("", Kit.PageTexts(output)[0]);                          // rasterized: no text at all on page 1
        Assert.Equal("Nothing to hide here", Kit.PageTexts(output)[1]);       // page 2 had no match: copied as it was
        Assert.Equal("", Kit.PageTexts(output)[2]);
    }

    [Fact]
    public async Task The_redacted_word_cannot_be_found_anywhere_in_the_file_bytes_even_inside_streams()
    {
        using var dir = new TempDir();
        // Samples.Pdf writes plain, uncompressed Helvetica text, so the word is easy to find in the input: a real positive control.
        var pdf = Samples.Pdf(dir.Combine("plain.pdf"), 3, n => n == 2 ? "Harmless filler" : "Confidential42 payroll");
        Assert.True(RawPdf.Contains(File.ReadAllBytes(pdf), "Confidential42"), "the search method must find the word in the input");

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "confidential42"))));

        var bytes = File.ReadAllBytes(result.Outputs[0]);
        Assert.False(RawPdf.Contains(bytes, "Confidential42"), "word found in the output bytes");
        Assert.False(RawPdf.Contains(bytes, "payroll"), "the rest of the redacted page is gone too");
        Assert.True(RawPdf.Contains(bytes, "Harmless filler"), "the untouched page keeps its content");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task The_area_under_a_found_word_is_black_and_the_rest_of_the_page_keeps_its_colour(int rotate)
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, red: true, rotate: rotate);
        var box = WordBox(pdf, 1, "Alice");

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))));

        AssertBlack(result.Outputs[0], 1, box);
        AssertRed(Pixel(result.Outputs[0], 1, 20, 20));                                   // corner
        var (w, h, _) = PixelKit.RenderRgb(result.Outputs[0], 1, 72);
        AssertRed(Pixel(result.Outputs[0], 1, w - 20, h - 20));
        AssertRed(Pixel(result.Outputs[0], 2, 20, 20));                                   // the untouched page
    }

    [Fact]
    public async Task Terms_are_case_insensitive_phrases_and_found_inside_longer_words()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("ALICE and the Account", 72, 200, 20).Text("Malice", 72, 300, 20).Text("nothing here", 72, 400, 20);
        // (three separate lines, so the second term can only match "alice" inside "Malice")
        var pdf = b.Save(dir.Combine("mixed.pdf"));
        var phrase = WordBox(pdf, 1, "ALICE");
        var account = WordBox(pdf, 1, "Account");

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "alice and the account, ALICE"))));

        Assert.Equal("", Kit.PageTexts(result.Outputs[0])[0]);
        AssertBlack(result.Outputs[0], 1, phrase);
        AssertBlack(result.Outputs[0], 1, account);
        var malice = WordBox(pdf, 1, "Malice");
        AssertBlack(result.Outputs[0], 1, (malice.X0 + 14, malice.Y0, malice.X1, malice.Y1, malice.W, malice.H)); // "alice" inside "Malice"
    }

    [Fact]
    public async Task A_phrase_that_wraps_onto_a_second_line_is_covered_on_both_lines()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("John", 72, 200, 20).Text("Smith", 72, 224, 20).Text("Elsewhere", 300, 500, 20);
        var pdf = b.Save(dir.Combine("wrap.pdf"));
        var john = WordBox(pdf, 1, "John");
        var smith = WordBox(pdf, 1, "Smith");

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "john smith"))));

        AssertBlack(result.Outputs[0], 1, john);
        AssertBlack(result.Outputs[0], 1, smith);
    }

    [Fact]
    public async Task An_arabic_term_is_found_and_only_that_word_is_covered()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Arabic("المبلغ سري للغاية", 500, 200, 28);
        var pdf = b.Save(dir.Combine("arabic.pdf"));
        var before = PixelKit.RenderPage(pdf, 1, 100);
        var words = Words(before, 3);
        Assert.Equal(3, words.Count);                                         // three words, left to right: "للغاية", "سري", "المبلغ"
        var bounds = before.InkBounds(128)!.Value;

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "سري"))));

        Assert.DoesNotContain("سري", AllText(result.Outputs[0])[0]);
        Assert.Equal("", Kit.PageTexts(result.Outputs[0])[0]);
        var after = PixelKit.RenderPage(result.Outputs[0], 1, 100);
        var middle = words[1];
        for (var y = bounds.MinY + 4; y <= bounds.MaxY - 4; y++)
            for (var x = middle.From + 3; x <= middle.To - 3; x++)
                Assert.True(after.At(x, y) < 10, $"pixel ({x},{y}) in the redacted word should be black");
        foreach (var other in new[] { words[0], words[2] })                   // the neighbours are still readable pictures of letters
        {
            var region = Enumerable.Range(other.From + 3, other.To - other.From - 5).SelectMany(x => Enumerable.Range(bounds.MinY, bounds.MaxY - bounds.MinY + 1).Select(y => after.At(x, y))).ToList();
            Assert.Contains(region, v => v > 200);
            Assert.Contains(region, v => v < 100);
        }
    }

    [Fact]
    public async Task Arabic_matching_ignores_diacritics_alef_variants_and_digit_shapes()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Arabic("احمد رقم ١٢٣", 500, 200, 28).Arabic("مرحبا بالعالم", 500, 300, 28);
        var pdf = b.Save(dir.Combine("arabic2.pdf"));

        // The term is typed with a hamza on the alef and with western digits; the page has a plain alef and Arabic-Indic digits.
        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "أحمد، 123"))));

        Assert.Equal("", Kit.PageTexts(result.Outputs[0])[0]);
        Assert.DoesNotContain("not found", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_term_with_no_match_is_reported_when_other_redactions_succeed()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice, Zorglub"), ("regions", "2:0,90,100,10"))));

        Assert.Contains("Zorglub", result.Note);
        Assert.Contains("not found", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'Alice'", result.Note!.Split("Not found")[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redacting_nothing_fails_and_does_not_write_an_identical_copy()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Zorglub")));

        Assert.False(result.Success);
        Assert.Contains("Zorglub", result.Error);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_scanned_page_has_no_text_to_search_and_the_error_says_to_use_regions()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("scan.png"), 200, 100);
        var b = new PdfBuilder();
        b.AddPage().Image(png, 50, 50, 400, 200);
        var pdf = b.Save(dir.Combine("scan.pdf"));

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice")));

        Assert.False(result.Success);
        Assert.Contains("regions", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["scan.pdf", "scan.png"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_cropped_page_maps_the_word_to_the_right_pixels()
    {
        using var dir = new TempDir();
        var cropped = PdfProbe.WithBoxes(Doc(dir, "plain.pdf", red: true), dir.Combine("cropped.pdf"), crop: [30, 400, 480, 700]);
        var box = WordBox(cropped, 1, "Alice");

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [cropped], Kit.Opts(("terms", "Alice"))));

        Assert.Equal((450, 300), PdfProbe.PdfiumSize(result.Outputs[0]));
        AssertBlack(result.Outputs[0], 1, box);
        AssertRed(Pixel(result.Outputs[0], 1, 440, 290));
    }

    [Fact]
    public async Task A_media_box_that_does_not_start_at_zero_maps_the_word_to_the_right_pixels()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        page.FillRect(0, 0, 612, 792, Red, Green, Blue);
        page.Text("Secret Alice account", 300, 300, 20);   // user space y = 492, inside the shifted media box below
        var shifted = PdfProbe.WithBoxes(b.Save(dir.Combine("plain.pdf")), dir.Combine("shifted.pdf"), media: [100, 200, 712, 992]);
        var box = WordBox(shifted, 1, "Alice");

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [shifted], Kit.Opts(("terms", "Alice"))));

        Assert.Equal((612, 792), PdfProbe.PdfiumSize(result.Outputs[0]));
        AssertBlack(result.Outputs[0], 1, box);
        AssertRed(Pixel(result.Outputs[0], 1, 20, 400));   // user space (120, 592): inside the red rectangle and inside the shifted media box
    }

    // ---- regions --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_region_is_burned_in_as_percent_of_the_page_measured_from_the_top_left()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, red: true);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:10,20,30,5"))));

        // 10 % of 612 = 61.2 .. 244.8 pt; 20 % of 792 = 158.4 .. 198 pt
        var page = PixelKit.RenderPage(result.Outputs[0], 1, 72);
        for (var y = 160; y <= 196; y++)
            for (var x = 63; x <= 243; x++)
                Assert.True(page.At(x, y) < 10, $"({x},{y}) is inside the region");
        AssertRed(Pixel(result.Outputs[0], 1, 55, 178));      // just left of the region
        AssertRed(Pixel(result.Outputs[0], 1, 250, 178));     // just right
        AssertRed(Pixel(result.Outputs[0], 1, 150, 150));     // just above
        AssertRed(Pixel(result.Outputs[0], 1, 150, 202));     // just below
        Assert.Equal("", Kit.PageTexts(result.Outputs[0])[0]);
        Assert.Equal("Nothing to hide here", Kit.PageTexts(result.Outputs[0])[1]);
    }

    [Fact]
    public async Task Several_entries_and_page_ranges_each_apply_to_their_own_pages()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, red: true, extraPages: 1);   // four pages

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:10,20,30,5; 2-3:0,90,100,10"))));

        Assert.Equal(4, Kit.PageCount(result.Outputs[0]));
        Assert.Equal(["", "", "", "Filler page 4"], Kit.PageTexts(result.Outputs[0]));
        var p1 = PixelKit.RenderPage(result.Outputs[0], 1, 72);
        Assert.True(p1.At(150, 178) < 10);
        foreach (var page in new[] { 2, 3 })
        {
            var g = PixelKit.RenderPage(result.Outputs[0], page, 72);
            Assert.True(g.At(300, 720) < 10);       // the band at 90..100 % of the height
            Assert.True(g.At(5, 790 - 1) < 10);
            AssertRed(Pixel(result.Outputs[0], page, 300, 600));
        }
        AssertRed(Pixel(result.Outputs[0], 4, 300, 720));
    }

    [Fact]
    public async Task Regions_accept_arabic_digits_percent_signs_and_an_entry_without_pages_means_all_pages()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, red: true);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "٠,٩٠,١٠٠%,١٠"))));

        Assert.Equal(["", "", ""], Kit.PageTexts(result.Outputs[0]));
        Assert.All(new[] { 1, 2, 3 }, p => Assert.True(PixelKit.RenderPage(result.Outputs[0], p, 72).At(300, 740) < 10));
    }

    [Fact]
    public async Task A_region_that_runs_past_the_page_edge_is_cut_at_the_edge()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, red: true);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:90,90,50,50"))));

        var page = PixelKit.RenderPage(result.Outputs[0], 1, 72);
        Assert.True(page.At(610, 790) < 10);
        Assert.True(page.At(560, 730) < 10);
        AssertRed(Pixel(result.Outputs[0], 1, 540, 700));
    }

    [Theory]
    [InlineData("1:10,20,30")]
    [InlineData("1:10,20,30,5,6")]
    [InlineData("x:1,2,3,4")]
    [InlineData("9:0,0,10,10")]
    [InlineData("1:0,0,0,10")]
    [InlineData("1:-5,0,10,10")]
    [InlineData("1:100,0,10,10")]
    [InlineData("1:a,b,c,d")]
    public async Task Malformed_regions_are_rejected_and_nothing_is_written(string regions)
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", regions)));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error!);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task At_least_one_of_regions_or_terms_is_required()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "  "), ("terms", "")));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- style and resolution -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Solid_is_pure_black_and_blur_destroys_the_letters_without_painting_black()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);
        var region = "1:5,34,60,12";  // x 30.6..397.8 pt, y 269.3..364.3 pt: covers the sentence whose baseline is at 300

        var solid = (await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", region), ("style", "solid"))))).Outputs[0];
        var blur = (await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", region), ("style", "blur"))))).Outputs[0];

        var original = PixelKit.RenderPage(pdf, 1, 72);
        var solidPage = PixelKit.RenderPage(solid, 1, 72);
        var blurPage = PixelKit.RenderPage(blur, 1, 72);
        var (x0, x1, y0, y1) = (33, 395, 272, 362);
        List<byte> Region(Gray page) => Enumerable.Range(y0, y1 - y0).SelectMany(y => Enumerable.Range(x0, x1 - x0).Select(x => page.At(x, y))).ToList();
        var originalInk = Region(original);
        var solidRegion = Region(solidPage);
        var blurRegion = Region(blurPage);

        Assert.Contains(originalInk, v => v < 60);                  // the original has black letters there
        Assert.All(solidRegion, v => Assert.True(v < 10));
        Assert.DoesNotContain(blurRegion, v => v < 60);              // no letter survives as dark ink
        Assert.Contains(blurRegion, v => v < 245);                   // but the area is not simply white either
        Assert.NotEqual(solidRegion, blurRegion);
        Assert.Equal("", Kit.PageTexts(blur)[0]);
    }

    [Theory]
    [InlineData(100, 850)]
    [InlineData(200, 1700)]
    [InlineData(400, 3400)]
    public async Task Dpi_sets_the_resolution_of_the_rebuilt_page(int dpi, int expectedWidth)
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:0,0,10,10"), ("dpi", dpi))));

        using var doc = Pig.Open(result.Outputs[0]);
        var image = Assert.Single(doc.GetPage(1).GetImages());
        Assert.Equal(expectedWidth, image.WidthInSamples);
        Assert.Equal((612, 792), PdfProbe.PdfiumSize(result.Outputs[0]));   // the page itself keeps its size
    }

    [Fact]
    public async Task The_default_resolution_is_200_dpi()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:0,0,10,10"))));

        using var doc = Pig.Open(result.Outputs[0]);
        Assert.Equal(1700, Assert.Single(doc.GetPage(1).GetImages()).WidthInSamples);
    }

    [Theory]
    [InlineData("dpi", 50)]
    [InlineData("dpi", 500)]
    [InlineData("style", "pixelate")]
    public async Task Invalid_dpi_or_style_is_rejected(string key, object value)
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:0,0,10,10"), (key, value)));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- metadata and what else could leak ------------------------------------------------------------------------------

    [Fact]
    public async Task Document_properties_and_xmp_are_stripped_from_the_output()
    {
        using var dir = new TempDir();
        var withMeta = PdfProbe.WithXmp(Doc(dir, "plain.pdf"), dir.Combine("meta.pdf"),
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>Alice XMP title</dc:title></x:xmpmeta>");
        var inputBytes = File.ReadAllBytes(withMeta);
        Assert.True(RawPdf.Contains(inputBytes, "Alice XMP title"), "the input really carries the XMP packet");
        Assert.True(RawPdf.Contains(inputBytes, "confidential report"));

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [withMeta], Kit.Opts(("terms", "Alice"))));

        using (var doc = Pig.Open(result.Outputs[0]))
        {
            var info = doc.Information;
            Assert.True(string.IsNullOrEmpty(info.Title));
            Assert.True(string.IsNullOrEmpty(info.Author));
            Assert.True(string.IsNullOrEmpty(info.Subject));
            Assert.True(string.IsNullOrEmpty(info.Keywords));
            Assert.True(string.IsNullOrEmpty(info.Producer));
            Assert.True(string.IsNullOrEmpty(info.Creator));
        }

        var bytes = File.ReadAllBytes(result.Outputs[0]);
        foreach (var leak in new[] { "confidential report", "Alice Author", "About Alice", "Alice XMP title", "xmpmeta", "/Producer", "/Title", "/Author", "PDFsharp" })
            Assert.False(RawPdf.Contains(bytes, leak), $"'{leak}' is still in the file");
    }

    [Fact]
    public async Task Untouched_pages_render_exactly_as_before()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, red: true);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))));

        Assert.Equal(PixelKit.RenderPage(pdf, 2, 72).Pixels, PixelKit.RenderPage(result.Outputs[0], 2, 72).Pixels);
    }

    [Fact]
    public async Task Rotated_untouched_pages_stay_rotated()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, rotate: 90);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))));

        Assert.Equal((792, 612), PdfProbe.PdfiumSize(result.Outputs[0], 2));
        Assert.Equal((792, 612), PdfProbe.PdfiumSize(result.Outputs[0], 1));
    }

    [Fact]
    public async Task The_note_says_how_many_pages_were_rasterized_and_that_their_text_is_gone()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))));

        Assert.Contains("2 pages", result.Note);
        Assert.Contains("rasterized", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200 DPI", result.Note);
        Assert.Contains("no longer selectable", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ---- common rules ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Never_overwrites_an_existing_result_or_touches_the_input()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);
        var original = File.ReadAllBytes(pdf);

        var first = (await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))))).Outputs[0];
        var second = (await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice"))))).Outputs[0];

        Assert.Equal(dir.Combine("doc (redacted).pdf"), first);
        Assert.Equal(dir.Combine("doc (redacted 2).pdf"), second);
        Assert.Equal(original, File.ReadAllBytes(pdf));
    }

    [Fact]
    public async Task Works_with_arabic_file_and_folder_names_and_honours_the_output_directory()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات سرية")).FullName;
        var arabic = Path.Combine(folder, "كشف حساب.pdf");
        File.Move(Doc(dir, "x.pdf"), arabic);

        var beside = (await Kit.Ok(Kit.RunAsync("pdf.redact", [arabic], Kit.Opts(("terms", "Alice"))))).Outputs[0];
        var elsewhere = (await Kit.Ok(Kit.RunAsync("pdf.redact", [arabic], Kit.Opts(("terms", "Alice")), outputDirectory: outDir.Path))).Outputs[0];

        Assert.Equal(Path.Combine(folder, "كشف حساب (redacted).pdf"), beside);
        Assert.Equal(Path.Combine(outDir.Path, "كشف حساب (redacted).pdf"), elsewhere);
        Assert.Equal("", Kit.PageTexts(elsewhere)[0]);
    }

    [Fact]
    public async Task Honours_cancellation_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir, extraPages: 5);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "all:0,0,100,10")), ct: cts.Token, progress: new CancelOnProgress(cts, after: 2)));

        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Reports_progress_up_to_one()
    {
        using var dir = new TempDir();
        var pdf = Doc(dir);
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice")), progress: progress));

        Assert.Equal(1, progress.Reports.Last().Fraction);
        Assert.True(progress.Reports.Count >= 3);
    }

    [Fact]
    public async Task A_corrupt_file_fails_cleanly()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("bad.pdf", "%PDF-1.4 junk");

        var result = await Kit.RunAsync("pdf.redact", [bad], Kit.Opts(("terms", "Alice")));

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["bad.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_password_protected_file_says_so()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Encrypt("user-secret");
        b.AddPage().Text("Secret Alice", 72, 100);
        var pdf = b.Save(dir.Combine("locked.pdf"));

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice")));

        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["locked.pdf"], Kit.FilesIn(dir.Path));
    }

    private static string Restricted(TempDir dir, bool allowAssemble, bool allowExtract)
    {
        var b = new PdfBuilder().Encrypt("", "boss-password", allowAssemble, allowExtract);
        b.AddPage().Text("Restricted Alice", 72, 100);
        return b.Save(dir.Combine("owner.pdf"));
    }

    [Fact]
    public async Task A_file_that_forbids_copying_pages_is_refused()
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, allowAssemble: false, allowExtract: true);

        var result = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice")));

        Assert.False(result.Success);
        Assert.Contains("owner password", result.Error);
        Assert.Equal(["owner.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_file_that_forbids_copying_text_cannot_be_searched_but_regions_still_work()
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, allowAssemble: true, allowExtract: false);

        var byTerm = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("terms", "Alice")));
        var byRegion = await Kit.RunAsync("pdf.redact", [pdf], Kit.Opts(("regions", "1:0,0,100,50")));

        Assert.False(byTerm.Success);
        Assert.Contains("does not allow its text to be copied", byTerm.Error);
        Assert.True(byRegion.Success, byRegion.Error);
        Assert.Equal("", Kit.PageTexts(byRegion.Outputs[0])[0]);
    }

    [Theory]
    [InlineData("pdf.redact", "pdf", true)]
    [InlineData("pdf.redact", "PDF", true)]
    [InlineData("pdf.redact", "png", false)]
    [InlineData("pdf.redact", "docx", false)]
    public void Supports_only_pdf(string operation, string ext, bool expected) =>
        Assert.Equal(expected, Kit.Converters.Any(c => c.Supports(operation, ext)));

    [Fact]
    public void Declares_its_options_and_needs_no_engine()
    {
        var converter = Kit.Converter("pdf.redact", "pdf");
        var options = converter.OptionsFor("pdf.redact").ToDictionary(o => o.Key);

        Assert.Equal(EngineId.None, converter.EngineFor("pdf.redact"));
        Assert.Equal(["regions", "terms", "dpi", "style"], options.Keys.ToArray());
        Assert.Equal(200.0, Convert.ToDouble(options["dpi"].Default));
        Assert.Equal((100.0, 400.0), (options["dpi"].Min, options["dpi"].Max));
        Assert.Equal(["solid", "blur"], options["style"].Choices);
        Assert.Equal("solid", options["style"].Default);
        Assert.Equal(ToolOptionKind.Text, options["regions"].Kind);
        Assert.Equal(ToolOptionKind.Text, options["terms"].Kind);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>Runs of ink columns separated by at least <paramref name="gap"/> empty columns (one run per word of a single line).</summary>
    /// <summary>
    /// The ink split into <paramref name="count"/> words at the widest gaps between ink columns. Gaps between letters depend on the font
    /// (Arial on Windows and macOS, DejaVu Sans on Linux), the space between words is always wider.
    /// </summary>
    private static List<(int From, int To)> Words(Gray page, int count)
    {
        var runs = Clusters(page, gap: 1);
        if (runs.Count < count) return runs;
        var cuts = Enumerable.Range(1, runs.Count - 1).OrderByDescending(i => runs[i].From - runs[i - 1].To).Take(count - 1).Order().ToList();
        var words = new List<(int From, int To)>();
        var start = 0;
        foreach (var cut in cuts)
        {
            words.Add((runs[start].From, runs[cut - 1].To));
            start = cut;
        }
        words.Add((runs[start].From, runs[^1].To));
        return words;
    }

    private static List<(int From, int To)> Clusters(Gray page, int gap = 6)
    {
        var inkColumns = new bool[page.Width];
        foreach (var (x, _) in page.Ink(128)) inkColumns[x] = true;

        var runs = new List<(int, int)>();
        int? start = null, last = null;
        for (var x = 0; x < page.Width; x++)
        {
            if (!inkColumns[x]) continue;
            if (start is not null && x - last!.Value > gap)
            {
                runs.Add((start.Value, last.Value));
                start = null;
            }
            start ??= x;
            last = x;
        }
        if (start is not null) runs.Add((start.Value, last!.Value));
        return runs;
    }
}
