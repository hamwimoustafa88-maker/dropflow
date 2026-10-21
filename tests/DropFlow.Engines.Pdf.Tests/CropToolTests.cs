using DropFlow.Core.Operations;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

public class CropToolTests
{
    // 25.4 mm is exactly 72 points, so pixel positions at 72 DPI can be compared without rounding doubts.
    private const double Inch = 25.4;
    private const double Pt = 72;

    /// <summary>
    /// 612 x 792 pages with the word "Hello1" (24 pt) around the middle of the sheet (baseline 396 pt from the top, 250 pt from the left),
    /// so the ink stays inside every margin used below whichever way the page is rotated.
    /// </summary>
    private static string TextPage(TempDir dir, string name = "doc.pdf", int rotate = 0, int pages = 1)
    {
        var b = new PdfBuilder();
        for (var i = 0; i < pages; i++) b.AddPage(612, 792, rotate).Text($"Hello{i + 1}", 250, 396, 24);
        return b.Save(dir.Combine(name));
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) Ink(string pdf, int page = 1) =>
        PixelKit.RenderPage(pdf, page, 72).InkBounds(128)!.Value;

    // ---- margins --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Margins_set_the_crop_box_and_two_other_readers_agree_on_the_new_page_size()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf],
            Kit.Opts(("mode", "margins"), ("top", 2 * Inch), ("right", Inch / 2), ("bottom", Inch), ("left", Inch))));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("doc (cropped).pdf"), output);
        var box = PdfProbe.CropBox(output);
        Assert.Equal((72, 72, 576, 648), (Math.Round(box.Left, 2), Math.Round(box.Bottom, 2), Math.Round(box.Right, 2), Math.Round(box.Top, 2)));
        var pdfium = PdfProbe.PdfiumSize(output);
        Assert.Equal((504, 576), (Math.Round(pdfium.Width, 2), Math.Round(pdfium.Height, 2)));
        Assert.Equal(pdfium, PdfProbe.PdfPigSize(output));
        Assert.Equal((0, 0, 612, 792), PdfProbe.MediaBox(output)); // non-destructive: the media box is untouched
    }

    [Fact]
    public async Task Margins_keep_every_pixel_of_content_and_shift_it_by_exactly_the_left_and_top_margin()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);
        var before = Ink(pdf);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf],
            Kit.Opts(("top", 2 * Inch), ("right", Inch / 2), ("bottom", Inch), ("left", Inch))));

        var after = Ink(result.Outputs[0]);
        Assert.InRange(after.MinX, before.MinX - 72 - 1, before.MinX - 72 + 1);
        Assert.InRange(after.MaxX, before.MaxX - 72 - 1, before.MaxX - 72 + 1);
        Assert.InRange(after.MinY, before.MinY - 144 - 1, before.MinY - 144 + 1);
        Assert.InRange(after.MaxY, before.MaxY - 144 - 1, before.MaxY - 144 + 1);
        Assert.Equal(["Hello1"], Kit.PageTexts(result.Outputs[0])); // the content stream still holds everything
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Margins_are_measured_on_the_page_as_displayed_for_every_rotation(int rotate)
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir, rotate: rotate);
        var before = Ink(pdf);
        var (w, h) = PdfProbe.PdfiumSize(pdf);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", 2 * Inch), ("left", Inch))));

        var size = PdfProbe.PdfiumSize(result.Outputs[0]);
        Assert.Equal((w - 72, h - 144), (Math.Round(size.Width, 2), Math.Round(size.Height, 2)));
        var after = Ink(result.Outputs[0]);
        Assert.InRange(after.MinX, before.MinX - 72 - 1, before.MinX - 72 + 1);
        Assert.InRange(after.MinY, before.MinY - 144 - 1, before.MinY - 144 + 1);
    }

    [Fact]
    public async Task Cropping_again_works_on_what_the_reader_already_sees()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);
        var first = (await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", Inch), ("right", Inch), ("bottom", Inch), ("left", Inch))))).Outputs[0];

        var second = (await Kit.Ok(Kit.RunAsync("pdf.crop", [first], Kit.Opts(("left", Inch))))).Outputs[0];

        Assert.Equal(dir.Combine("doc (cropped) (cropped).pdf"), second);
        var box = PdfProbe.CropBox(second);
        Assert.Equal((144, 72, 540, 720), (Math.Round(box.Left, 2), Math.Round(box.Bottom, 2), Math.Round(box.Right, 2), Math.Round(box.Top, 2)));
    }

    [Fact]
    public async Task A_media_box_that_does_not_start_at_zero_is_handled()
    {
        using var dir = new TempDir();
        var shifted = PdfProbe.WithBoxes(TextPage(dir, "plain.pdf"), dir.Combine("shifted.pdf"), media: [100, 200, 712, 992]);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [shifted], Kit.Opts(("top", Inch), ("left", 2 * Inch))));

        var box = PdfProbe.CropBox(result.Outputs[0]);
        Assert.Equal((244, 200, 712, 920), (Math.Round(box.Left, 2), Math.Round(box.Bottom, 2), Math.Round(box.Right, 2), Math.Round(box.Top, 2)));
    }

    [Fact]
    public async Task The_pages_option_crops_only_those_pages()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir, pages: 3);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", Inch), ("pages", "2"))));

        Assert.Equal((612, 792), PdfProbe.PdfiumSize(result.Outputs[0], 1));
        Assert.Equal((612, 720), PdfProbe.PdfiumSize(result.Outputs[0], 2));
        Assert.Equal((612, 792), PdfProbe.PdfiumSize(result.Outputs[0], 3));
    }

    [Fact]
    public async Task Margins_need_at_least_one_amount()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);

        var result = await Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "margins")));

        Assert.False(result.Success);
        Assert.Contains("margin", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Theory]
    [InlineData("top", 500.0)]       // taller than the whole page (792 pt = 279.4 mm)
    [InlineData("left", 216.0)]      // exactly the page width (612 pt = 215.9 mm) or more leaves nothing
    [InlineData("bottom", -5.0)]
    public async Task Margins_that_leave_nothing_or_are_negative_fail_and_write_nothing(string side, double mm)
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);

        var result = await Kit.RunAsync("pdf.crop", [pdf], Kit.Opts((side, mm)));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error!);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- box ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Box_mode_keeps_the_rectangle_measured_from_the_top_left_of_the_displayed_page(int rotate)
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir, rotate: rotate);
        var before = Ink(pdf);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "box"), ("box", "25.4, 50.8, 127, 101.6"))));

        var size = PdfProbe.PdfiumSize(result.Outputs[0]);
        Assert.Equal((360, 288), (Math.Round(size.Width, 2), Math.Round(size.Height, 2)));
        var after = Ink(result.Outputs[0]);
        Assert.InRange(after.MinX, before.MinX - 72 - 1, before.MinX - 72 + 1);
        Assert.InRange(after.MinY, before.MinY - 144 - 1, before.MinY - 144 + 1);
    }

    [Fact]
    public async Task Box_mode_sets_the_expected_crop_box_in_pdf_coordinates()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "box"), ("box", "25.4,50.8,127,101.6"))));

        var box = PdfProbe.CropBox(result.Outputs[0]);
        Assert.Equal((72, 360, 432, 648), (Math.Round(box.Left, 2), Math.Round(box.Bottom, 2), Math.Round(box.Right, 2), Math.Round(box.Top, 2)));
    }

    [Theory]
    [InlineData("10,10")]
    [InlineData("a,b,c,d")]
    [InlineData("10,10,0,50")]
    [InlineData("10,10,-5,50")]
    [InlineData("200,10,50,50")]    // starts beyond the right edge
    [InlineData("10,10,300,50")]    // 10 + 300 mm is wider than the 215.9 mm page
    [InlineData("")]
    public async Task Box_mode_rejects_boxes_that_are_malformed_empty_or_outside_the_page(string box)
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);

        var result = await Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "box"), ("box", box)));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- auto -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task Auto_trims_the_white_border_and_leaves_the_padding(int rotate)
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir, rotate: rotate);
        var before = Ink(pdf);

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto"), ("padding", 5))));

        var padding = 5 * 72 / 25.4;
        var after = PixelKit.RenderPage(result.Outputs[0], 1, 72);
        var ink = after.InkBounds(200)!.Value;
        Assert.InRange(ink.MinX, padding - 3, padding + 3);
        Assert.InRange(ink.MinY, padding - 3, padding + 3);
        Assert.InRange(after.Width - 1 - ink.MaxX, padding - 3, padding + 3);
        Assert.InRange(after.Height - 1 - ink.MaxY, padding - 3, padding + 3);
        var inkWidth = before.MaxX - before.MinX;
        Assert.InRange(after.Width, inkWidth + 2 * padding - 4, inkWidth + 2 * padding + 4);
    }

    [Fact]
    public async Task Auto_padding_option_changes_the_border()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);

        var small = (await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto"), ("padding", 0))))).Outputs[0];
        var large = (await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto"), ("padding", 20))))).Outputs[0];

        var (smallW, _) = PdfProbe.PdfiumSize(small);
        var (largeW, _) = PdfProbe.PdfiumSize(large);
        Assert.InRange(largeW - smallW, 2 * 20 * 72 / 25.4 - 4, 2 * 20 * 72 / 25.4 + 4);
    }

    [Fact]
    public async Task Auto_leaves_blank_pages_alone_and_says_so()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("Hello", 100, 300, 24);
        b.AddPage();
        var pdf = b.Save(dir.Combine("mixed.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto"))));

        Assert.True(PdfProbe.PdfiumSize(result.Outputs[0], 1).Width < 300);
        Assert.Equal((612, 792), PdfProbe.PdfiumSize(result.Outputs[0], 2));
        Assert.Contains("2", result.Note);
        Assert.Contains("blank", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auto_fails_when_there_is_nothing_to_crop_on_any_page()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage();
        var pdf = b.Save(dir.Combine("blank.pdf"));

        var result = await Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto")));

        Assert.False(result.Success);
        Assert.Contains("blank", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["blank.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- common rules ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Never_overwrites_an_existing_result_or_touches_the_input()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir);
        var original = File.ReadAllBytes(pdf);

        var first = (await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", Inch))))).Outputs[0];
        var second = (await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", Inch))))).Outputs[0];

        Assert.Equal(dir.Combine("doc (cropped).pdf"), first);
        Assert.Equal(dir.Combine("doc (cropped 2).pdf"), second);
        Assert.Equal(original, File.ReadAllBytes(pdf));
    }

    [Fact]
    public async Task Works_with_arabic_file_and_folder_names_and_honours_the_output_directory()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات عربية")).FullName;
        var pdf = TextPage(dir, "x.pdf");
        var arabic = Path.Combine(folder, "تقرير نهائي.pdf");
        File.Move(pdf, arabic);

        var beside = (await Kit.Ok(Kit.RunAsync("pdf.crop", [arabic], Kit.Opts(("top", Inch))))).Outputs[0];
        var elsewhere = (await Kit.Ok(Kit.RunAsync("pdf.crop", [arabic], Kit.Opts(("top", Inch)), outputDirectory: outDir.Path))).Outputs[0];

        Assert.Equal(Path.Combine(folder, "تقرير نهائي (cropped).pdf"), beside);
        Assert.Equal(Path.Combine(outDir.Path, "تقرير نهائي (cropped).pdf"), elsewhere);
        Assert.Equal((612, 720), PdfProbe.PdfiumSize(elsewhere));
    }

    [Fact]
    public async Task Honours_cancellation_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir, pages: 6);
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnProgress(cts, after: 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto")), ct: cts.Token, progress: progress));

        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Reports_progress_up_to_one()
    {
        using var dir = new TempDir();
        var pdf = TextPage(dir, pages: 3);
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("mode", "auto")), progress: progress));

        Assert.Equal(1, progress.Reports.Last().Fraction);
    }

    [Fact]
    public async Task A_corrupt_file_fails_cleanly()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("bad.pdf", "%PDF-1.4 junk");

        var result = await Kit.RunAsync("pdf.crop", [bad], Kit.Opts(("top", 10)));

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["bad.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_password_protected_file_says_so()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Encrypt("user-secret");
        b.AddPage().Text("Secret", 72, 100);
        var pdf = b.Save(dir.Combine("locked.pdf"));

        var result = await Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", 10)));

        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["locked.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_file_with_only_an_owner_password_is_refused_not_quietly_unprotected()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Encrypt("", "boss-password", allowAssemble: false, allowExtract: false);
        b.AddPage().Text("Restricted", 72, 100);
        var pdf = b.Save(dir.Combine("owner.pdf"));

        var result = await Kit.RunAsync("pdf.crop", [pdf], Kit.Opts(("top", 10)));

        Assert.False(result.Success);
        Assert.Equal(["owner.pdf"], Kit.FilesIn(dir.Path));
    }

    [Theory]
    [InlineData("pdf.crop", "pdf", true)]
    [InlineData("pdf.crop", "PDF", true)]
    [InlineData("pdf.crop", "png", false)]
    [InlineData("pdf.crop", "docx", false)]
    public void Supports_only_pdf(string operation, string ext, bool expected) =>
        Assert.Equal(expected, Kit.Converters.Any(c => c.Supports(operation, ext)));

    [Fact]
    public void Declares_its_options_and_needs_no_engine()
    {
        var converter = Kit.Converter("pdf.crop", "pdf");
        var options = converter.OptionsFor("pdf.crop").ToDictionary(o => o.Key);

        Assert.Equal(EngineId.None, converter.EngineFor("pdf.crop"));
        Assert.Equal(["mode", "top", "right", "bottom", "left", "box", "pages", "padding"], options.Keys.ToArray());
        Assert.Equal(["margins", "box", "auto"], options["mode"].Choices);
        Assert.Equal("margins", options["mode"].Default);
        Assert.Equal(ToolOptionKind.Range, options["pages"].Kind);
        Assert.Equal(5.0, Convert.ToDouble(options["padding"].Default));
    }
}

/// <summary>Cancels the job once the converter has reported progress a given number of times (a cancel in the middle of the work).</summary>
internal sealed class CancelOnProgress(CancellationTokenSource source, int after) : IProgress<JobProgress>
{
    private int _count;

    public void Report(JobProgress value)
    {
        if (++_count >= after) source.Cancel();
    }
}
