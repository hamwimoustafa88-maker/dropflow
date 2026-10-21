using System.Text;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace DropFlow.Engines.Pdf.Tests;

public class CompareToolTests
{
    /// <summary>A document whose page i shows a fixed heading and one line of body text (pages[i]) in 24 pt.</summary>
    private static string Doc(TempDir dir, string name, params string[] pages) => Doc(dir, name, 612, 792, 0, pages);

    private static string Doc(TempDir dir, string name, double width, double height, int rotate, params string[] pages)
    {
        var b = new PdfBuilder();
        foreach (var body in pages)
            b.AddPage(width, height, rotate).Text("Quarterly report", 72, 100, 14).Text(body, 72, 200, 24);
        return b.Save(dir.Combine(name));
    }

    private static string ReportOf(string pdfOutput) => File.ReadAllText(Path.ChangeExtension(pdfOutput, "txt"), Encoding.UTF8);

    private static (string Pdf, string Txt) Outputs(ConversionResult result)
    {
        Assert.Equal(2, result.Outputs.Count);
        return (result.Outputs.Single(o => o.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)), result.Outputs.Single(o => o.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Counts pure-red highlight pixels, and returns the smallest and largest x of them.</summary>
    private static (int Count, int MinX, int MaxX, int Width) Red(string pdf, int page)
    {
        var (w, h, rgb) = PixelKit.RenderRgb(pdf, page, 72);
        int count = 0, min = int.MaxValue, max = -1;
        for (var i = 0; i < w * h; i++)
        {
            if (rgb[i * 3] > 190 && rgb[i * 3 + 1] < 90 && rgb[i * 3 + 2] < 90)
            {
                count++;
                var x = i % w;
                min = Math.Min(min, x);
                max = Math.Max(max, x);
            }
        }
        return (count, min, max, w);
    }

    // ---- identical, changed, extra page, size ---------------------------------------------------------------------------

    [Fact]
    public async Task Identical_files_say_so_in_the_report_and_in_the_pdf_and_show_no_red()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "first.pdf", "Alpha beta gamma", "Second page");
        var b = Doc(dir, "second.pdf", "Alpha beta gamma", "Second page");

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var (pdf, txt) = Outputs(result);
        Assert.Equal(dir.Combine("first vs second (compare).pdf"), pdf);
        Assert.Equal(dir.Combine("first vs second (compare).txt"), txt);
        var report = ReportOf(pdf);
        Assert.Contains("identical", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Page 1: identical", report);
        Assert.Contains("Page 2: identical", report);
        Assert.Equal(2, Kit.PageCount(pdf));
        Assert.All(Kit.PageTexts(pdf), text => Assert.Contains("identical", text, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, Red(pdf, 1).Count);
        Assert.Equal(0, Red(pdf, 2).Count);
    }

    [Fact]
    public async Task One_changed_word_is_highlighted_in_red_only_in_the_difference_panel_and_listed_in_the_report()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "The quick brown fox jumps", "Unchanged page");
        var b = Doc(dir, "b.pdf", "The quick green fox jumps", "Unchanged page");

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var (pdf, _) = Outputs(result);
        var report = ReportOf(pdf);
        Assert.Contains("DIFFERENT", report);
        Assert.Matches(@"Page 1: (0\.\d\d|<0\.01|[1-9]\d*\.\d\d) % of pixels changed", report);
        Assert.Contains("Page 2: identical", report);
        Assert.Contains("removed (1): brown", report);
        Assert.Contains("added (1): green", report);

        var page1 = Red(pdf, 1);
        Assert.True(page1.Count > 20, "the changed word is painted red");
        Assert.True(page1.MinX > page1.Width * 0.6, "red only in the right-hand (differences) panel");
        Assert.Equal(0, Red(pdf, 2).Count);
        Assert.Contains("brown", Kit.PageTexts(pdf)[0]);   // the sheet's header names the words that went away and the ones that came
        Assert.Contains("green", Kit.PageTexts(pdf)[0]);
    }

    [Fact]
    public async Task The_percentage_grows_with_the_amount_of_change()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "short");
        var small = Doc(dir, "small.pdf", "shorT");
        var big = Doc(dir, "big.pdf", "something completely different and much longer than before");

        var smallPct = Percent(ReportOf(Outputs(await Kit.Ok(Kit.RunAsync("pdf.compare", [a, small]))).Pdf));
        var bigPct = Percent(ReportOf(Outputs(await Kit.Ok(Kit.RunAsync("pdf.compare", [a, big]))).Pdf));

        Assert.InRange(smallPct, 0.001, 1);
        Assert.True(bigPct > smallPct * 3, $"{bigPct} vs {smallPct}");
    }

    private static double Percent(string report)
    {
        var match = System.Text.RegularExpressions.Regex.Match(report, @"Page 1: ([\d.]+) % of pixels changed");
        Assert.True(match.Success, report);
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData(2, 3, "only in B")]
    [InlineData(3, 2, "only in A")]
    public async Task A_page_count_difference_shows_the_extra_pages_as_only_in_one_file(int pagesA, int pagesB, string expected)
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", [.. Enumerable.Range(1, pagesA).Select(i => $"Page text {i}")]);
        var b = Doc(dir, "b.pdf", [.. Enumerable.Range(1, pagesB).Select(i => $"Page text {i}")]);

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var (pdf, _) = Outputs(result);
        var report = ReportOf(pdf);
        Assert.Equal(3, Kit.PageCount(pdf));
        Assert.Contains($"A: a.pdf ({pagesA} pages", report);
        Assert.Contains($"B: b.pdf ({pagesB} pages", report);
        Assert.Contains($"Page 3: {expected}", report);
        Assert.Contains("Page 1: identical", report);
        Assert.Contains("DIFFERENT", report);
        Assert.Contains(expected, Kit.PageTexts(pdf)[2]);
        Assert.DoesNotContain("identical", Kit.PageTexts(pdf)[2], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_different_page_size_is_reported_with_both_sizes_and_still_compared()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "letter.pdf", "Same words here");
        var b = Doc(dir, "a4.pdf", 595.276, 841.89, 0, "Same words here");

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var (pdf, _) = Outputs(result);
        var report = ReportOf(pdf);
        Assert.Contains("page size differs", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("215.9 x 279.4 mm", report);
        Assert.Contains("210 x 297 mm", report);
        Assert.Contains("DIFFERENT", report);
        Assert.Equal(1, Kit.PageCount(pdf));
        Assert.Contains("page size differs", Kit.PageTexts(pdf)[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotated_pages_are_compared_as_displayed()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", 612, 792, 90, "Rotated body");
        var b = Doc(dir, "b.pdf", 612, 792, 90, "Rotated body");
        var c = Doc(dir, "c.pdf", 612, 792, 0, "Rotated body");

        var same = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));
        var different = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, c]));

        Assert.Contains("Page 1: identical", ReportOf(Outputs(same).Pdf));
        Assert.Contains("page size differs", ReportOf(Outputs(different).Pdf), StringComparison.OrdinalIgnoreCase);
    }

    // ---- sensitivity, dpi -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sensitivity_decides_whether_a_faint_difference_counts()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "Plain page");
        var bBuilder = new PdfBuilder();
        bBuilder.AddPage().Text("Quarterly report", 72, 100, 14).Text("Plain page", 72, 200, 24).FillRect(300, 500, 200, 100, 235, 235, 235);
        var b = bBuilder.Save(dir.Combine("b.pdf"));   // a pale grey box: 20 levels away from white

        var relaxed = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));                                  // default sensitivity 30
        var strict = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b], Kit.Opts(("sensitivity", 90))));

        Assert.Contains("Page 1: identical", ReportOf(Outputs(relaxed).Pdf));
        Assert.Contains("sensitivity 30", ReportOf(Outputs(relaxed).Pdf));
        Assert.Matches(@"Page 1: [\d.<]+ % of pixels changed", ReportOf(Outputs(strict).Pdf));
        Assert.True(Red(Outputs(strict).Pdf, 1).Count > 0);
        Assert.Equal(0, Red(Outputs(relaxed).Pdf, 1).Count);
    }

    [Theory]
    [InlineData(50, 425)]
    [InlineData(100, 850)]
    [InlineData(150, 1275)]
    public async Task Dpi_sets_the_resolution_of_the_page_pictures(int dpi, int expectedWidth)
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var b = Doc(dir, "b.pdf", "Two");

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b], Kit.Opts(("dpi", dpi))));

        using var doc = Pig.Open(Outputs(result).Pdf);
        var widths = doc.GetPage(1).GetImages().Select(i => i.WidthInSamples).ToList();
        Assert.Equal(3, widths.Count);                    // A, B and the differences
        Assert.All(widths, w => Assert.Equal(expectedWidth, w));
    }

    [Theory]
    [InlineData("sensitivity", 0)]
    [InlineData("sensitivity", 101)]
    [InlineData("dpi", 10)]
    [InlineData("dpi", 1000)]
    public async Task Out_of_range_options_are_rejected(string key, double value)
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var b = Doc(dir, "b.pdf", "Two");

        var result = await Kit.RunAsync("pdf.compare", [a, b], Kit.Opts((key, value)));

        Assert.False(result.Success);
        Assert.Equal(["a.pdf", "b.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- the text part of the report ------------------------------------------------------------------------------------

    [Fact]
    public async Task Long_word_lists_are_cut_with_a_count_of_the_rest()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "x");
        var bBuilder = new PdfBuilder();
        var page = bBuilder.AddPage();
        for (var line = 0; line < 8; line++)
            page.Text(string.Join(" ", Enumerable.Range(0, 5).Select(w => $"word{line}x{w}")), 72, 150 + line * 30, 12);
        var b = bBuilder.Save(dir.Combine("b.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var report = ReportOf(Outputs(result).Pdf);
        Assert.Contains("added (40):", report);
        Assert.Matches(@"\(\+\d+ more\)", report);
    }

    [Fact]
    public async Task Arabic_text_is_compared_in_logical_order_and_names_survive_in_the_report()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات")).FullName;
        var ba = new PdfBuilder();
        ba.AddPage().Arabic("المبلغ سري للغاية", 500, 200, 24);
        var a = ba.Save(Path.Combine(folder, "عقد أ.pdf"));
        var bb = new PdfBuilder();
        bb.AddPage().Arabic("المبلغ علني جدا", 500, 200, 24);
        var b = bb.Save(Path.Combine(folder, "عقد ب.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var (pdf, txt) = Outputs(result);
        Assert.Equal(Path.Combine(folder, "عقد أ vs عقد ب (compare).pdf"), pdf);
        Assert.Equal(Path.Combine(folder, "عقد أ vs عقد ب (compare).txt"), txt);
        var report = File.ReadAllText(txt, Encoding.UTF8);
        Assert.Contains("عقد أ.pdf", report);
        Assert.Contains("عقد ب.pdf", report);
        Assert.Contains("removed (2): سري, للغاية", report);
        Assert.Contains("added (2): علني, جدا", report);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(txt).Take(3).ToArray());   // UTF-8 with BOM so Notepad shows Arabic correctly
    }

    // ---- common rules ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exactly_two_pdfs_are_required()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var b = Doc(dir, "b.pdf", "Two");
        var c = Doc(dir, "c.pdf", "Three");

        var one = await Kit.RunAsync("pdf.compare", [a]);
        var three = await Kit.RunAsync("pdf.compare", [a, b, c]);

        Assert.False(one.Success);
        Assert.Contains("exactly two", one.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(three.Success);
        Assert.Contains("exactly two", three.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task The_second_file_must_be_a_pdf_too()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var png = Samples.Png(dir.Combine("pic.png"));

        var result = await Kit.RunAsync("pdf.compare", [a, png]);

        Assert.False(result.Success);
        Assert.Contains("pic.png", result.Error);
        Assert.Equal(["a.pdf", "pic.png"], Kit.FilesIn(dir.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_corrupt_file_in_either_position_fails_cleanly(int badIndex)
    {
        using var dir = new TempDir();
        var good = Doc(dir, "good.pdf", "One");
        var bad = dir.WriteText("bad.pdf", "%PDF-1.4 junk");
        string[] inputs = badIndex == 0 ? [bad, good] : [good, bad];

        var result = await Kit.RunAsync("pdf.compare", inputs);

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["bad.pdf", "good.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_password_protected_file_says_so()
    {
        using var dir = new TempDir();
        var good = Doc(dir, "good.pdf", "One");
        var b = new PdfBuilder().Encrypt("user-secret");
        b.AddPage().Text("Secret", 72, 100);
        var locked = b.Save(dir.Combine("locked.pdf"));

        var result = await Kit.RunAsync("pdf.compare", [good, locked]);

        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["good.pdf", "locked.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_file_whose_owner_forbids_copying_text_is_refused()
    {
        using var dir = new TempDir();
        var good = Doc(dir, "good.pdf", "One");
        var b = new PdfBuilder().Encrypt("", "boss-password", allowAssemble: true, allowExtract: false);
        b.AddPage().Text("Restricted", 72, 100);
        var restricted = b.Save(dir.Combine("owner.pdf"));

        var result = await Kit.RunAsync("pdf.compare", [good, restricted]);

        Assert.False(result.Success);
        Assert.Contains("does not allow its text to be copied", result.Error);
        Assert.Equal(["good.pdf", "owner.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Never_overwrites_and_the_pair_always_shares_one_number()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var b = Doc(dir, "b.pdf", "Two");
        var originalA = File.ReadAllBytes(a);
        dir.WriteText("a vs b (compare).txt", "somebody else's file");   // only the .txt name is taken

        var first = Outputs(await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b])));
        var second = Outputs(await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b])));

        Assert.Equal(dir.Combine("a vs b (compare 2).pdf"), first.Pdf);
        Assert.Equal(dir.Combine("a vs b (compare 2).txt"), first.Txt);
        Assert.Equal(dir.Combine("a vs b (compare 3).pdf"), second.Pdf);
        Assert.Equal(dir.Combine("a vs b (compare 3).txt"), second.Txt);
        Assert.Equal("somebody else's file", File.ReadAllText(dir.Combine("a vs b (compare).txt")));
        Assert.Equal(originalA, File.ReadAllBytes(a));
    }

    [Fact]
    public async Task Honours_the_output_directory()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var b = Doc(dir, "b.pdf", "Two");

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b], outputDirectory: outDir.Path));

        Assert.All(result.Outputs, o => Assert.Equal(outDir.Path, Path.GetDirectoryName(o)));
        Assert.Equal(["a.pdf", "b.pdf"], Kit.FilesIn(dir.Path));
        Assert.Equal(2, Kit.FilesIn(outDir.Path).Length);
    }

    [Fact]
    public async Task Honours_cancellation_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var pages = Enumerable.Range(1, 8).Select(i => $"Page {i}").ToArray();
        var a = Doc(dir, "a.pdf", pages);
        var b = Doc(dir, "b.pdf", pages);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.RunAsync("pdf.compare", [a, b], ct: cts.Token, progress: new CancelOnProgress(cts, after: 3)));

        Assert.Equal(["a.pdf", "b.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Reports_progress_up_to_one()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One", "Two");
        var b = Doc(dir, "b.pdf", "One", "Two");
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b], progress: progress));

        Assert.Equal(1, progress.Reports.Last().Fraction);
        Assert.True(progress.Reports.Count >= 3);
    }

    [Fact]
    public async Task The_output_page_is_a_wide_sheet_with_three_panels()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "One");
        var b = Doc(dir, "b.pdf", "Two");

        var result = await Kit.Ok(Kit.RunAsync("pdf.compare", [a, b]));

        var (w, h) = PdfProbe.PdfiumSize(Outputs(result).Pdf);
        Assert.True(w > h, "landscape");
        var page = PixelKit.RenderPage(Outputs(result).Pdf, 1, 72);
        // dark text pixels exist in all three thirds below the header: the two renders and the (faded) difference picture
        var thirds = Enumerable.Range(0, 3).Select(t => page.Ink(200).Count(p => p.X >= t * page.Width / 3 && p.X < (t + 1) * page.Width / 3 && p.Y > page.Height * 0.12)).ToArray();
        Assert.All(thirds, count => Assert.True(count > 5, string.Join(",", thirds)));
    }

    [Theory]
    [InlineData("pdf.compare", "pdf", true)]
    [InlineData("pdf.compare", "PDF", true)]
    [InlineData("pdf.compare", "png", false)]
    [InlineData("pdf.compare", "docx", false)]
    public void Supports_only_pdf(string operation, string ext, bool expected) =>
        Assert.Equal(expected, Kit.Converters.Any(c => c.Supports(operation, ext)));

    [Fact]
    public void Declares_its_options_and_needs_no_engine()
    {
        var converter = Kit.Converter("pdf.compare", "pdf");
        var options = converter.OptionsFor("pdf.compare").ToDictionary(o => o.Key);

        Assert.Equal(EngineId.None, converter.EngineFor("pdf.compare"));
        Assert.Equal(["sensitivity", "dpi"], options.Keys.ToArray());
        Assert.Equal(30.0, Convert.ToDouble(options["sensitivity"].Default));
        Assert.Equal((1.0, 100.0), (options["sensitivity"].Min, options["sensitivity"].Max));
        Assert.Equal(100.0, Convert.ToDouble(options["dpi"].Default));
    }
}
