using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

public class TextExtractionTests
{
    private static string Article(TempDir dir)
    {
        var b = new PdfBuilder();
        b.AddPage()
            .Text("Annual Report", 72, 90, 24, bold: true)
            .Text("Introduction", 72, 140, 16, bold: true)
            .Text("This is the first line of a paragraph", 72, 170)
            .Text("that continues on the second line.", 72, 185)
            .Text("Second paragraph.", 72, 230)
            .Text("• Apples", 72, 270)
            .Text("• Oranges", 72, 285)
            .Text("1. First step", 72, 320)
            .Text("2. Second step", 72, 335);
        return b.Save(dir.Combine("article.pdf"));
    }

    [Fact]
    public async Task Headings_paragraphs_and_lists_become_markdown()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        Assert.Equal(dir.Combine("article (converted).md"), result.Outputs[0]);
        var expected = string.Join("\n",
            "# Annual Report",
            "",
            "## Introduction",
            "",
            "This is the first line of a paragraph that continues on the second line.",
            "",
            "Second paragraph.",
            "",
            "- Apples",
            "- Oranges",
            "",
            "1. First step",
            "2. Second step",
            "");
        Assert.Equal(expected, File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Plain_text_keeps_one_block_per_paragraph()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        var text = File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n");
        Assert.StartsWith("Annual Report\n\nIntroduction\n\nThis is the first line of a paragraph that continues on the second line.\n\nSecond paragraph.", text);
        Assert.Contains("• Apples\n• Oranges", text);
        Assert.Contains("1. First step\n2. Second step", text);
    }

    [Fact]
    public async Task Extract_text_writes_txt_and_md_side_by_side_by_default()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.extract-text", [pdf]));

        Assert.Equal([dir.Combine("article (text).txt"), dir.Combine("article (text).md")], result.Outputs);
        Assert.StartsWith("# Annual Report", File.ReadAllText(result.Outputs[1]));
        Assert.StartsWith("Annual Report", File.ReadAllText(result.Outputs[0]));
    }

    [Theory]
    [InlineData("txt", "txt")]
    [InlineData("md", "md")]
    public async Task Extract_text_can_write_just_one_format(string format, string extension)
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.extract-text", [pdf], Kit.Opts(("format", format))));

        Assert.Equal([dir.Combine($"article (text).{extension}")], result.Outputs);
    }

    [Fact]
    public async Task Text_files_are_utf8_and_the_txt_has_a_byte_order_mark_for_windows_editors()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("pdf.extract-text", [pdf]));

        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(result.Outputs[0]).Take(3));
        Assert.NotEqual([0xEF, 0xBB, 0xBF], File.ReadAllBytes(result.Outputs[1]).Take(3));
    }

    [Fact]
    public async Task A_grid_of_cells_becomes_a_pipe_table()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage()
            .Text("Price list", 72, 90, 20, bold: true)
            .Table([["Name", "Qty", "Price"], ["Apple", "3", "1.50"], ["Pear", "10", "2.00"]], [72, 250, 400], 140);
        var pdf = b.Save(dir.Combine("table.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        var markdown = File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n");
        Assert.Contains("| Name | Qty | Price |\n| --- | --- | --- |\n| Apple | 3 | 1.50 |\n| Pear | 10 | 2.00 |", markdown);
        Assert.StartsWith("# Price list", markdown);
    }

    [Fact]
    public async Task Table_cells_are_tab_separated_in_plain_text()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Table([["A", "B"], ["1", "2"]], [72, 250], 100);
        var pdf = b.Save(dir.Combine("t.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        Assert.Equal("A\tB\n1\t2", File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public async Task Two_columns_of_prose_are_not_mistaken_for_a_table()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        for (var i = 0; i < 4; i++)
        {
            page.Text($"Left column sentence number {i} with plenty of words", 72, 100 + i * 15);
            page.Text($"Right column sentence number {i} with plenty of words", 330, 100 + i * 15);
        }
        var pdf = b.Save(dir.Combine("cols.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        Assert.DoesNotContain("| ---", File.ReadAllText(result.Outputs[0]));
    }

    [Fact]
    public async Task A_word_split_with_a_hyphen_at_the_line_end_is_rejoined()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("A long exam-", 72, 100).Text("ple of wrapping.", 72, 115);
        var pdf = b.Save(dir.Combine("h.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        Assert.Equal("A long example of wrapping.", File.ReadAllText(result.Outputs[0]).Trim());
    }

    [Fact]
    public async Task Markdown_syntax_characters_in_the_text_are_escaped()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("Use *stars* and [brackets] and snake_case", 72, 100);
        var pdf = b.Save(dir.Combine("e.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        Assert.Equal("Use \\*stars\\* and \\[brackets\\] and snake\\_case", File.ReadAllText(result.Outputs[0]).Trim());
    }

    [Fact]
    public async Task Each_page_follows_the_previous_one_with_a_rule_in_markdown()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("First page text", 72, 100);
        b.AddPage().Text("Second page text", 72, 100);
        var pdf = b.Save(dir.Combine("two.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        Assert.Equal("First page text\n\n---\n\nSecond page text", File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n").Trim());
    }

    // ---- Arabic ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_arabic_line_laid_out_in_visual_order_is_read_back_in_logical_order()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Arabic("مرحبا بالعالم", 540, 100, 16);
        var pdf = b.Save(dir.Combine("ar.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        Assert.Equal("مرحبا بالعالم", File.ReadAllText(result.Outputs[0]).Trim());
    }

    [Fact]
    public async Task Numbers_and_latin_words_inside_arabic_text_keep_their_place()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        page.Arabic("الصفحة 12 من 50", 540, 100, 16);
        page.Arabic("استخدم DropFlow اليوم", 540, 130, 16);
        var pdf = b.Save(dir.Combine("mixed.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        var lines = File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n").Trim().Split("\n\n");
        Assert.Equal(["الصفحة 12 من 50", "استخدم DropFlow اليوم"], lines);
    }

    [Fact]
    public async Task Arabic_paragraph_lines_are_joined_in_reading_order()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        page.Arabic("هذا هو السطر الأول من الفقرة", 540, 100, 14);
        page.Arabic("ويتبعه السطر الثاني", 540, 117, 14);
        var pdf = b.Save(dir.Combine("para.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        Assert.Equal("هذا هو السطر الأول من الفقرة ويتبعه السطر الثاني", File.ReadAllText(result.Outputs[0]).Trim());
    }

    [Fact]
    public async Task Arabic_table_columns_start_from_the_right()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        // Two columns: the first logical column is the right-hand one.
        page.Arabic("الاسم", 540, 100, 14).Arabic("الكمية", 300, 100, 14);
        page.Arabic("تفاح", 540, 120, 14).Arabic("٣", 300, 120, 14);
        var pdf = b.Save(dir.Combine("artable.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [pdf]));

        var markdown = File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n");
        Assert.Contains("| الاسم | الكمية |", markdown);
        Assert.Contains("| تفاح | ٣ |", markdown);
    }

    // ---- html -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Html_output_is_a_complete_page_with_real_elements()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [pdf]));

        var html = File.ReadAllText(result.Outputs[0]);
        Assert.StartsWith("<!DOCTYPE html>", html);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal("Annual Report", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Introduction", doc.QuerySelector("h2")!.TextContent);
        Assert.Equal(["Apples", "Oranges"], doc.QuerySelectorAll("ul li").Select(e => e.TextContent));
        Assert.Equal(["First step", "Second step"], doc.QuerySelectorAll("ol li").Select(e => e.TextContent));
        Assert.Equal("article", doc.Title);
        Assert.Equal("en", doc.DocumentElement.GetAttribute("lang"));
    }

    [Fact]
    public async Task Html_escapes_markup_in_the_text()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("1 < 2 & 3 > 2", 72, 100);
        var pdf = b.Save(dir.Combine("x.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [pdf]));

        Assert.Contains("1 &lt; 2 &amp; 3 &gt; 2", File.ReadAllText(result.Outputs[0]));
    }

    [Fact]
    public async Task Html_for_arabic_documents_is_marked_arabic()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Arabic("مرحبا بالعالم", 540, 100, 16);
        var pdf = b.Save(dir.Combine("ar.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [pdf]));

        var doc = new HtmlParser().ParseDocument(File.ReadAllText(result.Outputs[0]));
        Assert.Equal("ar", doc.DocumentElement.GetAttribute("lang"));
        Assert.Equal("مرحبا بالعالم", doc.QuerySelector("p")!.TextContent);
    }

    // ---- failures -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pdf_without_a_text_layer_says_so_and_writes_nothing()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage();
        var pdf = b.Save(dir.Combine("blank.pdf"));

        var result = await Kit.RunAsync("pdf.extract-text", [pdf]);

        Assert.False(result.Success);
        Assert.Contains("OCR", result.Error);
        Assert.Equal(["blank.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_password_protected_pdf_asks_for_unlock_first()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Encrypt("secret");
        b.AddPage().Text("Hidden", 72, 100);
        var pdf = b.Save(dir.Combine("locked.pdf"));

        var result = await Kit.RunAsync("convert:txt", [pdf]);

        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unlock", result.Error);
        Assert.Equal(["locked.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_corrupt_pdf_fails_cleanly()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("bad.pdf", "%PDF-1.4 garbage");

        var result = await Kit.RunAsync("convert:txt", [bad]);

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["bad.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task The_generated_sample_pdf_from_test_support_is_readable()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("sample.pdf"), 2, n => $"Page number {n}");

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        Assert.Equal("Page number 1\n\nPage number 2", File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public async Task Unicode_paths_and_existing_outputs_are_handled()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("مستندات")).FullName;
        var b = new PdfBuilder();
        b.AddPage().Text("Hello", 72, 100);
        var pdf = b.Save(Path.Combine(folder, "تقرير.pdf"));

        var first = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));
        var second = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        Assert.Equal(Path.Combine(folder, "تقرير (converted).txt"), first.Outputs[0]);
        Assert.Equal(Path.Combine(folder, "تقرير (converted 2).txt"), second.Outputs[0]);
    }

    [Fact]
    public async Task Cancellation_stops_before_anything_is_written()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:txt", [pdf], ct: cts.Token));

        Assert.Equal(["article.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Text_on_a_rotated_page_is_read_as_the_reader_sees_it()
    {
        using var dir = new TempDir();
        // The page is stored sideways (landscape content on a portrait sheet) and carries /Rotate 90 so viewers show it upright.
        var b = new PdfBuilder();
        var page = b.AddPage(612, 792, rotate: 90);
        page.TextAlongY("Scanned landscape page", 100, 400, 14);
        var pdf = b.Save(dir.Combine("sideways.pdf"));

        // Sanity check of the fixture with another reader: the rendered page is landscape and has ink.
        var rendered = PixelKit.RenderPage(pdf, 1, 36);
        Assert.True(rendered.Width > rendered.Height);
        var bounds = rendered.InkBounds()!.Value;
        Assert.True(bounds.MaxX - bounds.MinX > 2 * (bounds.MaxY - bounds.MinY), "the text reads horizontally on screen");

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        Assert.Equal("Scanned landscape page", File.ReadAllText(result.Outputs[0]).Trim());
    }
}
