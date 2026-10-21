using DropFlow.TestSupport;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

public class PageToolsTests
{
    private static string Sample(TempDir dir, string name, int pages, string prefix) =>
        Samples.Pdf(dir.Combine(name), pages, n => $"{prefix}{n}");

    // ---- merge ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Merge_joins_the_files_in_the_given_order()
    {
        using var dir = new TempDir();
        var a = Sample(dir, "a.pdf", 2, "A");
        var b = Sample(dir, "b.pdf", 3, "B");
        var before = File.ReadAllBytes(a);

        var result = await Kit.Ok(Kit.RunAsync("pdf.merge", [a, b]));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("a (merged).pdf"), output);
        Assert.Equal(["A1", "A2", "B1", "B2", "B3"], Kit.PageTexts(output));
        Assert.Equal(before, File.ReadAllBytes(a)); // inputs are never touched
    }

    [Fact]
    public async Task Merge_never_overwrites_an_existing_result()
    {
        using var dir = new TempDir();
        var a = Sample(dir, "a.pdf", 1, "A");
        var b = Sample(dir, "b.pdf", 1, "B");

        var first = (await Kit.Ok(Kit.RunAsync("pdf.merge", [a, b]))).Outputs[0];
        var second = (await Kit.Ok(Kit.RunAsync("pdf.merge", [a, b]))).Outputs[0];

        Assert.NotEqual(first, second);
        Assert.Equal(dir.Combine("a (merged 2).pdf"), second);
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task Parallel_jobs_in_one_folder_never_write_the_same_file()
    {
        using var dir = new TempDir();
        var a = Sample(dir, "a.pdf", 1, "A");
        var b = Sample(dir, "b.pdf", 1, "B");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Kit.RunAsync("pdf.merge", [a, b]))));

        Assert.All(results, r => Assert.True(r.Success, r.Error));
        var outputs = results.Select(r => r.Outputs[0]).ToList();
        Assert.Equal(8, outputs.Distinct().Count());
        Assert.All(outputs, o => Assert.Equal(["A1", "B1"], Kit.PageTexts(o)));
    }

    [Fact]
    public async Task Merge_needs_two_files()
    {
        using var dir = new TempDir();
        var a = Sample(dir, "a.pdf", 1, "A");

        var result = await Kit.RunAsync("pdf.merge", [a]);

        Assert.False(result.Success);
        Assert.Contains("two", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Merge_with_a_corrupt_file_fails_cleanly_and_leaves_no_partial_output()
    {
        using var dir = new TempDir();
        var a = Sample(dir, "a.pdf", 1, "A");
        var bad = dir.WriteText("bad.pdf", "this is not a pdf at all");

        var result = await Kit.RunAsync("pdf.merge", [a, bad]);

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Equal(["a.pdf", "bad.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Merge_handles_unicode_paths()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات عربية")).FullName;
        var a = Samples.Pdf(Path.Combine(folder, "تقرير ١.pdf"), 1, n => "One");
        var b = Samples.Pdf(Path.Combine(folder, "تقرير ٢.pdf"), 1, n => "Two");

        var result = await Kit.Ok(Kit.RunAsync("pdf.merge", [a, b]));

        Assert.Equal(Path.Combine(folder, "تقرير ١ (merged).pdf"), result.Outputs[0]);
        Assert.Equal(["One", "Two"], Kit.PageTexts(result.Outputs[0]));
    }

    [Fact]
    public async Task Merge_honours_cancellation_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var a = Sample(dir, "a.pdf", 1, "A");
        var b = Sample(dir, "b.pdf", 1, "B");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("pdf.merge", [a, b], ct: cts.Token));

        Assert.Equal(["a.pdf", "b.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Merge_writes_into_the_requested_output_directory()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var a = Sample(dir, "a.pdf", 1, "A");
        var b = Sample(dir, "b.pdf", 1, "B");

        var result = await Kit.Ok(Kit.RunAsync("pdf.merge", [a, b], outputDirectory: outDir.Path));

        Assert.Equal(outDir.Combine("a (merged).pdf"), result.Outputs[0]);
        Assert.Equal(["a.pdf", "b.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- split ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Split_writes_each_page_into_a_pages_folder()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "book.pdf", 3, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.split", [input]));

        var folder = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("book (pages)"), folder);
        Assert.Equal(["book - 1.pdf", "book - 2.pdf", "book - 3.pdf"], Kit.FilesIn(folder));
        Assert.Equal(["P2"], Kit.PageTexts(Path.Combine(folder, "book - 2.pdf")));
    }

    [Fact]
    public async Task Split_pads_numbers_so_files_sort_naturally()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "big.pdf", 12, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.split", [input]));

        var files = Kit.FilesIn(result.Outputs[0]);
        Assert.Equal(12, files.Length);
        Assert.Equal("big - 01.pdf", files[0]);
        Assert.Equal("big - 12.pdf", files[^1]);
    }

    [Fact]
    public async Task Split_second_run_uses_a_new_folder()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "book.pdf", 2, "P");

        await Kit.Ok(Kit.RunAsync("pdf.split", [input]));
        var second = await Kit.Ok(Kit.RunAsync("pdf.split", [input]));

        Assert.Equal(dir.Combine("book (pages 2)"), second.Outputs[0]);
    }

    // ---- rotate ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rotate_turns_only_the_selected_pages()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "r.pdf", 3, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.rotate", [input], Kit.Opts(("degrees", 90), ("pages", "2"))));

        using var doc = PdfDocument.Open(result.Outputs[0]);
        Assert.Equal([0, 90, 0], doc.GetPages().Select(p => (int)p.Rotation.Value));
        Assert.Equal(dir.Combine("r (rotated).pdf"), result.Outputs[0]);
    }

    [Fact]
    public async Task Rotate_defaults_to_every_page_and_accumulates_with_existing_rotation()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "r.pdf", 2, "P");
        var once = (await Kit.Ok(Kit.RunAsync("pdf.rotate", [input], Kit.Opts(("degrees", "270"))))).Outputs[0];

        var twice = (await Kit.Ok(Kit.RunAsync("pdf.rotate", [once], Kit.Opts(("degrees", "180"))))).Outputs[0];

        using var doc = PdfDocument.Open(twice);
        Assert.Equal([90, 90], doc.GetPages().Select(p => (int)p.Rotation.Value)); // 270 + 180 = 450 -> 90
    }

    [Theory]
    [InlineData(45)]
    [InlineData(0)]
    public async Task Rotate_rejects_angles_that_are_not_quarter_turns(int degrees)
    {
        using var dir = new TempDir();
        var input = Sample(dir, "r.pdf", 1, "P");

        var result = await Kit.RunAsync("pdf.rotate", [input], Kit.Opts(("degrees", degrees)));

        Assert.False(result.Success);
        Assert.Equal(["r.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Rotate_reports_a_page_that_does_not_exist()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "r.pdf", 2, "P");

        var result = await Kit.RunAsync("pdf.rotate", [input], Kit.Opts(("degrees", 90), ("pages", "5")));

        Assert.False(result.Success);
        Assert.Contains("Page 5", result.Error);
    }

    // ---- organize -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Organize_reorders_duplicates_and_drops_pages()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "o.pdf", 5, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.organize", [input], Kit.Opts(("pages", "3,1,2,2-4"))));

        Assert.Equal(["P3", "P1", "P2", "P2", "P3", "P4"], Kit.PageTexts(result.Outputs[0]));
        Assert.Equal(dir.Combine("o (organized).pdf"), result.Outputs[0]);
    }

    [Fact]
    public async Task Organize_can_rotate_individual_occurrences_independently()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "o.pdf", 2, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.organize", [input], Kit.Opts(("pages", "1,1r90,2r180"))));

        using var doc = PdfDocument.Open(result.Outputs[0]);
        var pages = doc.GetPages().ToList();
        Assert.Equal(["P1", "P1", "P2"], pages.Select(p => p.Text.Trim()));
        Assert.Equal([0, 90, 180], pages.Select(p => (int)p.Rotation.Value));
    }

    [Fact]
    public async Task Organize_requires_a_page_list()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "o.pdf", 2, "P");

        var result = await Kit.RunAsync("pdf.organize", [input], Kit.Opts(("pages", "")));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Organize_accepts_arabic_indic_digits()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "o.pdf", 3, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.organize", [input], Kit.Opts(("pages", "٣،١"))));

        Assert.Equal(["P3", "P1"], Kit.PageTexts(result.Outputs[0]));
    }

    // ---- metadata -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Metadata_sets_the_given_fields_including_arabic_text()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "m.pdf", 1, "P");

        var result = await Kit.Ok(Kit.RunAsync("pdf.metadata", [input], Kit.Opts(
            ("title", "تقرير سنوي"), ("author", "Moustafa"), ("subject", "Finance"), ("keywords", "a, b"))));

        using var doc = PdfDocument.Open(result.Outputs[0]);
        Assert.Equal("تقرير سنوي", doc.Information.Title);
        Assert.Equal("Moustafa", doc.Information.Author);
        Assert.Equal("Finance", doc.Information.Subject);
        Assert.Equal("a, b", doc.Information.Keywords);
    }

    [Fact]
    public async Task Metadata_leaves_unspecified_fields_alone()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "m.pdf", 1, "P");
        var first = (await Kit.Ok(Kit.RunAsync("pdf.metadata", [input], Kit.Opts(("title", "T"), ("author", "A"))))).Outputs[0];

        var second = (await Kit.Ok(Kit.RunAsync("pdf.metadata", [first], Kit.Opts(("title", "T2"), ("author", ""))))).Outputs[0];

        using var doc = PdfDocument.Open(second);
        Assert.Equal("T2", doc.Information.Title);
        Assert.Equal("A", doc.Information.Author);
    }

    [Fact]
    public async Task Metadata_strip_removes_descriptive_fields_and_xmp()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "m.pdf", 1, "P");
        var tagged = (await Kit.Ok(Kit.RunAsync("pdf.metadata", [input], Kit.Opts(
            ("title", "Secret"), ("author", "Me"), ("subject", "S"), ("keywords", "k"))))).Outputs[0];

        var stripped = (await Kit.Ok(Kit.RunAsync("pdf.metadata", [tagged], Kit.Opts(("strip", true))))).Outputs[0];

        using var doc = PdfDocument.Open(stripped);
        Assert.True(string.IsNullOrEmpty(doc.Information.Title));
        Assert.True(string.IsNullOrEmpty(doc.Information.Author));
        Assert.True(string.IsNullOrEmpty(doc.Information.Subject));
        Assert.True(string.IsNullOrEmpty(doc.Information.Keywords));
        Assert.DoesNotContain("Secret", File.ReadAllText(stripped, System.Text.Encoding.Latin1));
        Assert.Equal(["P1"], Kit.PageTexts(stripped));
    }

    [Fact]
    public async Task Metadata_strip_then_set_keeps_only_the_new_values()
    {
        using var dir = new TempDir();
        var input = Sample(dir, "m.pdf", 1, "P");
        var tagged = (await Kit.Ok(Kit.RunAsync("pdf.metadata", [input], Kit.Opts(("title", "Old"), ("author", "Old"))))).Outputs[0];

        var result = (await Kit.Ok(Kit.RunAsync("pdf.metadata", [tagged], Kit.Opts(("strip", true), ("title", "New"))))).Outputs[0];

        using var doc = PdfDocument.Open(result);
        Assert.Equal("New", doc.Information.Title);
        Assert.True(string.IsNullOrEmpty(doc.Information.Author));
    }

    // ---- shared ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf.split")]
    [InlineData("pdf.rotate")]
    [InlineData("pdf.metadata")]
    [InlineData("pdf.organize")]
    public async Task Corrupt_input_fails_with_a_clear_message_and_leaves_no_output(string operation)
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("broken.pdf", "%PDF-1.4\nnot really");

        var result = await Kit.RunAsync(operation, [bad], Kit.Opts(("pages", "1"), ("degrees", 90)));

        Assert.False(result.Success);
        Assert.Contains("broken.pdf", result.Error);
        Assert.Equal(["broken.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Missing_input_fails_cleanly()
    {
        using var dir = new TempDir();

        var result = await Kit.RunAsync("pdf.split", [dir.Combine("nope.pdf")]);

        Assert.False(result.Success);
        Assert.Contains("nope.pdf", result.Error);
    }
}
