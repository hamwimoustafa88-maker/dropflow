using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// An owner password with no user password lets anybody open the file, but its permission flags still say what may be done with it.
/// DropFlow must not use page-copying tools to quietly produce an unprotected copy of such a file.
/// </summary>
public class OwnerRestrictionTests
{
    private static string Restricted(TempDir dir, string name, bool allowAssemble, bool allowExtract)
    {
        var b = new PdfBuilder().Encrypt("", "boss-password", allowAssemble, allowExtract);
        b.AddPage().Text("Restricted text", 72, 100);
        return b.Save(dir.Combine(name));
    }

    [Theory]
    [InlineData("pdf.split")]
    [InlineData("pdf.organize")]
    public async Task Page_copying_tools_respect_a_ban_on_assembling_pages(string operation)
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, "locked.pdf", allowAssemble: false, allowExtract: true);

        var result = await Kit.RunAsync(operation, [pdf], Kit.Opts(("pages", "1")));

        Assert.False(result.Success);
        Assert.Contains("owner password", result.Error);
        Assert.Contains("Unlock", result.Error);
        Assert.Equal(["locked.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Merge_refuses_a_file_that_forbids_assembling_pages()
    {
        using var dir = new TempDir();
        var locked = Restricted(dir, "locked.pdf", allowAssemble: false, allowExtract: true);
        var open = Samples.Pdf(dir.Combine("open.pdf"), 1);

        var result = await Kit.RunAsync("pdf.merge", [open, locked]);

        Assert.False(result.Success);
        Assert.Contains("locked.pdf", result.Error);
        Assert.Equal(["locked.pdf", "open.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Page_copying_works_when_the_owner_allowed_it()
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, "ok.pdf", allowAssemble: true, allowExtract: true);

        var result = await Kit.Ok(Kit.RunAsync("pdf.split", [pdf]));

        Assert.Equal(["Restricted text"], Kit.PageTexts(Directory.GetFiles(result.Outputs[0]).Single()));
    }

    [Theory]
    [InlineData("convert:txt")]
    [InlineData("convert:md")]
    [InlineData("convert:docx")]
    [InlineData("pdf.extract-text")]
    public async Task Text_extraction_respects_a_ban_on_copying_content(string operation)
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, "nocopy.pdf", allowAssemble: true, allowExtract: false);

        var result = await Kit.RunAsync(operation, [pdf]);

        Assert.False(result.Success);
        Assert.Contains("does not allow its text to be copied", result.Error);
        Assert.Equal(["nocopy.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Showing_the_pages_as_pictures_is_still_possible()
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, "view.pdf", allowAssemble: false, allowExtract: false);

        var result = await Kit.Ok(Kit.RunAsync("convert:png", [pdf]));

        Assert.True(File.Exists(result.Outputs[0]));
    }

    [Fact]
    public async Task Editing_tools_that_pdfsharp_cannot_apply_report_the_protection()
    {
        using var dir = new TempDir();
        var pdf = Restricted(dir, "locked.pdf", allowAssemble: true, allowExtract: true);

        var result = await Kit.RunAsync("pdf.rotate", [pdf], Kit.Opts(("degrees", 90)));

        Assert.False(result.Success);
        Assert.Contains("password", result.Error);
        Assert.Equal(["locked.pdf"], Kit.FilesIn(dir.Path));
    }
}
