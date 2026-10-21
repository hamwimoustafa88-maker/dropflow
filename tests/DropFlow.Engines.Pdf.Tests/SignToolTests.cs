using System.Globalization;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Pdf.Tests;

public class SignToolTests
{
    private const int Dpi = 72; // one pixel per point keeps the arithmetic obvious

    private static string BlankPages(TempDir dir, string name = "doc.pdf", int pages = 1, int rotate = 0, bool grey = false)
    {
        var b = new PdfBuilder();
        for (var i = 0; i < pages; i++)
        {
            var page = b.AddPage(612, 792, rotate);
            if (grey) page.FillRect(0, 0, 612, 792, 200, 200, 200);
        }
        return b.Save(dir.Combine(name));
    }

    private static Gray Render(string pdf, int page = 1) => PixelKit.RenderPage(pdf, page, Dpi);

    private static (int MinX, int MinY, int MaxX, int MaxY) Ink(string pdf, int page = 1) =>
        Render(pdf, page).InkBounds(128)!.Value;

    /// <summary>A 200 x 80 PNG that is fully transparent except for an opaque black block in the middle (x 60..140, y 20..60).</summary>
    private static string TransparentSignaturePng(TempDir dir, string name = "sig.png")
    {
        using var image = new MagickImage(MagickColors.Transparent, 200, 80);
        image.Draw(new DrawableFillColor(MagickColors.Black), new DrawableRectangle(60, 20, 139, 59));
        var path = dir.Combine(name);
        File.WriteAllBytes(path, image.ToByteArray(MagickFormat.Png32));
        return path;
    }

    // ---- typed name -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_typed_name_lands_bottom_right_of_the_last_page_by_default_and_nowhere_else()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, pages: 3);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "text"), ("text", "Ahmed Salem"))));

        Assert.Equal(dir.Combine("doc (signed).pdf"), Assert.Single(result.Outputs));
        Assert.Empty(Render(result.Outputs[0], 1).Ink(200));
        Assert.Empty(Render(result.Outputs[0], 2).Ink(200));
        var ink = Ink(result.Outputs[0], 3);
        Assert.InRange(ink.MinX, 436, 475);       // block is 45 mm (127.6 pt) wide, right edge 36 pt from the page edge
        Assert.InRange(ink.MaxX, 555, 578);
        Assert.InRange(ink.MaxY, 735, 758);       // bottom edge 36 pt above the bottom of the page
        Assert.InRange(ink.MinY, 690, 750);
        Assert.InRange(ink.MaxX - ink.MinX, 90, 135);
    }

    [Theory]
    [InlineData("bottom-right", 0.6, 1.0, 0.85, 1.0)]
    [InlineData("bottom-left", 0.0, 0.4, 0.85, 1.0)]
    [InlineData("bottom-center", 0.35, 0.65, 0.85, 1.0)]
    [InlineData("top-right", 0.6, 1.0, 0.0, 0.15)]
    [InlineData("top-left", 0.0, 0.4, 0.0, 0.15)]
    [InlineData("top-center", 0.35, 0.65, 0.0, 0.15)]
    public async Task The_position_option_chooses_the_corner(string position, double minX, double maxX, double minY, double maxY)
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("position", position))));

        var page = Render(result.Outputs[0]);
        var ink = page.Ink(128);
        Assert.NotEmpty(ink);
        var (cx, cy) = (ink.Average(p => p.X) / page.Width, ink.Average(p => p.Y) / page.Height);
        Assert.InRange(cx, minX, maxX);
        Assert.InRange(cy, minY, maxY);
    }

    [Fact]
    public async Task X_and_y_percentages_place_the_top_left_corner_of_the_signature()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("x", 50), ("y", 50))));

        var ink = Ink(result.Outputs[0]);
        Assert.InRange(ink.MinX, 300, 330);   // 50 % of 612
        Assert.InRange(ink.MinY, 390, 420);   // 50 % of 792
        Assert.InRange(ink.MaxX, 400, 450);
    }

    [Fact]
    public async Task A_single_override_keeps_the_other_axis_from_the_position()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("position", "bottom-right"), ("x", 10))));

        var ink = Ink(result.Outputs[0]);
        Assert.InRange(ink.MinX, 55, 90);     // 10 % of 612 = 61
        Assert.InRange(ink.MaxY, 735, 758);   // still at the bottom
    }

    [Fact]
    public async Task A_signature_that_would_leave_the_page_is_pulled_back_inside()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("x", 100), ("y", 100))));

        var page = Render(result.Outputs[0]);
        var ink = page.InkBounds(128)!.Value;
        Assert.True(ink.MaxX < page.Width && ink.MaxY < page.Height);
        Assert.InRange(ink.MaxX, page.Width - 15, page.Width - 1);
    }

    [Fact]
    public async Task Width_option_scales_the_signature()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var narrow = Ink((await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("width", 30))))).Outputs[0]);
        var wide = Ink((await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("width", 60))))).Outputs[0]);

        var ratio = (double)(wide.MaxX - wide.MinX) / (narrow.MaxX - narrow.MinX);
        Assert.InRange(ratio, 1.8, 2.2);
    }

    [Fact]
    public async Task The_pages_option_signs_exactly_the_requested_pages()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, pages: 4);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("pages", "1,3"))));

        var inked = Enumerable.Range(1, 4).Where(p => Render(result.Outputs[0], p).Ink(200).Count > 0).ToArray();
        Assert.Equal([1, 3], inked);
    }

    [Fact]
    public async Task Pages_can_be_all_and_the_input_page_count_is_kept()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, pages: 3);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("pages", "all"))));

        Assert.Equal(3, Kit.PageCount(result.Outputs[0]));
        Assert.All(Enumerable.Range(1, 3), p => Assert.NotEmpty(Render(result.Outputs[0], p).Ink(200)));
    }

    [Fact]
    public async Task A_page_that_does_not_exist_is_an_error_and_nothing_is_written()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, pages: 2);

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("pages", "5")));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task The_existing_page_content_is_kept()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("Original body", 72, 100);
        var pdf = b.Save(dir.Combine("body.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"))));

        Assert.Contains("Original body", Kit.PageTexts(result.Outputs[0])[0]);
    }

    [Fact]
    public async Task An_arabic_name_is_shaped_and_drawn()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "أحمد سالم"))));

        Assert.NotEmpty(Render(result.Outputs[0]).Ink(128));
        Assert.Contains(ArabicShaper.ToVisual("أحمد سالم"), Kit.PageTexts(result.Outputs[0])[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stamp_date_writes_todays_date_under_the_signature()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        var before = DateTime.Now;

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("stampDate", true))));

        var text = Kit.PageTexts(result.Outputs[0])[0];
        var accepted = new[] { before, DateTime.Now }.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct();
        Assert.Contains(accepted, d => text.Contains(d, StringComparison.Ordinal));

        var without = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"))));
        Assert.DoesNotContain(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), Kit.PageTexts(without.Outputs[0])[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_result_says_it_is_not_a_digital_signature()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"))));

        Assert.Contains("not a digital signature", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ---- picture --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_transparent_png_keeps_the_page_visible_around_the_ink_and_paints_no_white_box()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, grey: true);
        var sig = TransparentSignaturePng(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", sig), ("width", 25.4 * 127.5 / 72))));

        // The block is 127.5 pt x 51 pt with its right edge at 576 and its bottom at 756: x 448.5..576, y 705..756.
        var page = Render(result.Outputs[0]);
        Assert.InRange((int)page.At(455, 710), 195, 205);      // transparent corner of the signature: the grey page shows through
        Assert.InRange((int)page.At(570, 750), 195, 205);
        Assert.InRange((int)page.At(512, 731), 0, 40);         // the opaque block of the picture
        Assert.InRange((int)page.At(100, 100), 195, 205);      // far away: untouched
        Assert.DoesNotContain(page.Pixels, p => p > 230);      // no white anywhere on the grey page
    }

    [Fact]
    public async Task A_picture_with_a_path_in_quotes_and_an_arabic_folder_name_works()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        var folder = Directory.CreateDirectory(dir.Combine("توقيعات")).FullName;
        var sig = TransparentSignaturePng(dir, "x.png");
        var arabic = Path.Combine(folder, "توقيعي.png");
        File.Move(sig, arabic);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", $"\"{arabic}\""))));

        Assert.NotEmpty(Render(result.Outputs[0]).Ink(128));
    }

    [Fact]
    public async Task A_jpeg_signature_is_placed_too()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        using var image = new MagickImage(MagickColors.White, 200, 80);
        image.Draw(new DrawableFillColor(MagickColors.Black), new DrawableRectangle(60, 20, 139, 59));
        var jpg = dir.Combine("sig.jpg");
        File.WriteAllBytes(jpg, image.ToByteArray(MagickFormat.Jpeg));

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", jpg))));

        var ink = Ink(result.Outputs[0]);
        Assert.InRange(ink.MinX, 480, 520);
        Assert.InRange(ink.MaxY, 725, 756);
    }

    [Fact]
    public async Task A_very_large_picture_is_embedded_at_a_sensible_size()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        using var image = new MagickImage(MagickColors.Transparent, 4000, 1500);
        image.Draw(new DrawableFillColor(MagickColors.Black), new DrawableRectangle(1000, 400, 3000, 1100));
        var big = dir.Combine("big.png");
        File.WriteAllBytes(big, image.ToByteArray(MagickFormat.Png32));

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", big))));

        Assert.NotEmpty(Render(result.Outputs[0]).Ink(128));
        Assert.True(new FileInfo(result.Outputs[0]).Length < 400_000, "the picture is scaled down before it is embedded");
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\definitely\\not\\here.png")]
    public async Task A_missing_picture_is_an_error(string path)
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", path)));

        Assert.False(result.Success);
        Assert.Contains("image", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_an_error()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        var notImage = dir.WriteText("notes.png", "this is text, not a PNG");

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", notImage)));

        Assert.False(result.Success);
        Assert.Contains("notes.png", result.Error);
        Assert.Equal(["doc.pdf", "notes.png"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_scriptable_format_renamed_to_png_is_refused()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        var svg = dir.WriteText("sig.png", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\"><rect width=\"20\" height=\"20\"/></svg>");

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", svg)));

        Assert.False(result.Success);
        Assert.Contains("sig.png", result.Error);
        Assert.Equal(["doc.pdf", "sig.png"], Kit.FilesIn(dir.Path));
    }

    // ---- geometry: rotation, crop box, media box ------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task On_a_rotated_page_the_signature_is_bottom_right_as_displayed_and_upright(int rotate)
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, rotate: rotate);
        var sig = TransparentSignaturePng(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "image"), ("image", sig))));

        var page = Render(result.Outputs[0]);
        var ink = page.InkBounds(128)!.Value;
        Assert.InRange((ink.MinX + ink.MaxX) / 2.0, page.Width * 0.65, page.Width);
        Assert.InRange((ink.MinY + ink.MaxY) / 2.0, page.Height * 0.85, page.Height);
        // The picture's opaque block is 80 x 40: it must still be wider than tall once the page is displayed.
        Assert.True(ink.MaxX - ink.MinX > 1.5 * (ink.MaxY - ink.MinY), "the picture is displayed upright");
    }

    [Fact]
    public async Task A_cropped_page_is_signed_inside_what_the_reader_sees()
    {
        using var dir = new TempDir();
        var cropped = PdfProbe.WithBoxes(BlankPages(dir, "plain.pdf"), dir.Combine("cropped.pdf"), crop: [50, 100, 450, 700]);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [cropped], Kit.Opts(("text", "Ahmed Salem"), ("width", 30))));

        var page = Render(result.Outputs[0]);
        Assert.Equal((400, 600), (page.Width, page.Height));
        var ink = page.InkBounds(128)!.Value;
        Assert.InRange(ink.MaxX, 355, 366);   // 400 - 36 margin
        Assert.InRange(ink.MaxY, 555, 566);   // 600 - 36 margin
    }

    [Fact]
    public async Task A_media_box_that_does_not_start_at_zero_is_signed_in_the_right_place()
    {
        using var dir = new TempDir();
        var shifted = PdfProbe.WithBoxes(BlankPages(dir, "plain.pdf"), dir.Combine("shifted.pdf"), media: [100, 200, 712, 992]);

        var result = await Kit.Ok(Kit.RunAsync("pdf.sign", [shifted], Kit.Opts(("text", "Ahmed Salem"))));

        var page = Render(result.Outputs[0]);
        Assert.Equal((612, 792), (page.Width, page.Height));
        var ink = page.InkBounds(128)!.Value;
        Assert.InRange(ink.MaxX, 555, 578);
        Assert.InRange(ink.MaxY, 735, 758);
    }

    // ---- common rules ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kind", "stamp")]
    [InlineData("position", "middle")]
    [InlineData("x", 101)]
    [InlineData("y", -1)]
    [InlineData("width", 0)]
    public async Task Invalid_options_are_rejected_before_anything_is_written(string key, object value)
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), (key, value)));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_typed_signature_needs_text()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("kind", "text"), ("text", "   ")));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_result_or_touches_the_input()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir);
        var original = File.ReadAllBytes(pdf);

        var first = (await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"))))).Outputs[0];
        var second = (await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"))))).Outputs[0];

        Assert.Equal(dir.Combine("doc (signed).pdf"), first);
        Assert.Equal(dir.Combine("doc (signed 2).pdf"), second);
        Assert.Equal(original, File.ReadAllBytes(pdf));
    }

    [Fact]
    public async Task Works_with_arabic_file_and_folder_names_and_honours_the_output_directory()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات عربية")).FullName;
        var arabic = Path.Combine(folder, "عقد العمل.pdf");
        File.Move(BlankPages(dir, "x.pdf"), arabic);

        var beside = (await Kit.Ok(Kit.RunAsync("pdf.sign", [arabic], Kit.Opts(("text", "Ahmed"))))).Outputs[0];
        var elsewhere = (await Kit.Ok(Kit.RunAsync("pdf.sign", [arabic], Kit.Opts(("text", "Ahmed")), outputDirectory: outDir.Path))).Outputs[0];

        Assert.Equal(Path.Combine(folder, "عقد العمل (signed).pdf"), beside);
        Assert.Equal(Path.Combine(outDir.Path, "عقد العمل (signed).pdf"), elsewhere);
    }

    [Fact]
    public async Task Honours_cancellation_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, pages: 8);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("pages", "all")), ct: cts.Token, progress: new CancelOnProgress(cts, after: 2)));

        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Reports_progress_up_to_one()
    {
        using var dir = new TempDir();
        var pdf = BlankPages(dir, pages: 3);
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem"), ("pages", "all")), progress: progress));

        Assert.Equal(1, progress.Reports.Last().Fraction);
    }

    [Fact]
    public async Task A_corrupt_file_fails_cleanly()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("bad.pdf", "%PDF-1.4 junk");

        var result = await Kit.RunAsync("pdf.sign", [bad], Kit.Opts(("text", "Ahmed Salem")));

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

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem")));

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

        var result = await Kit.RunAsync("pdf.sign", [pdf], Kit.Opts(("text", "Ahmed Salem")));

        Assert.False(result.Success);
        Assert.Equal(["owner.pdf"], Kit.FilesIn(dir.Path));
    }

    [Theory]
    [InlineData("pdf.sign", "pdf", true)]
    [InlineData("pdf.sign", "PDF", true)]
    [InlineData("pdf.sign", "png", false)]
    [InlineData("pdf.sign", "docx", false)]
    public void Supports_only_pdf(string operation, string ext, bool expected) =>
        Assert.Equal(expected, Kit.Converters.Any(c => c.Supports(operation, ext)));

    [Fact]
    public void Declares_its_options_and_needs_no_engine()
    {
        var converter = Kit.Converter("pdf.sign", "pdf");
        var options = converter.OptionsFor("pdf.sign").ToDictionary(o => o.Key);

        Assert.Equal(EngineId.None, converter.EngineFor("pdf.sign"));
        Assert.Equal(["kind", "text", "image", "position", "x", "y", "width", "pages", "stampDate"], options.Keys.ToArray());
        Assert.Equal(["text", "image"], options["kind"].Choices);
        Assert.Equal("bottom-right", options["position"].Default);
        Assert.Null(options["x"].Default);
        Assert.Equal(45.0, Convert.ToDouble(options["width"].Default));
        Assert.Equal("last", options["pages"].Default);
        Assert.Equal(ToolOptionKind.Bool, options["stampDate"].Kind);
    }
}
