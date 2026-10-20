using DropFlow.Core.Catalog;

namespace DropFlow.Core.Tests;

public class CatalogTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    [Fact]
    public void Embedded_catalog_loads_all_categories_tools_and_actions()
    {
        Assert.Equal(10, Catalog.Categories.Count);
        Assert.True(Catalog.Tools.Count >= 26);
        Assert.Equal(7, Catalog.Actions.Count);
    }

    [Theory]
    [InlineData("png", "image")]
    [InlineData("HEIC", "image")]
    [InlineData(".jpg", "image")]
    [InlineData("gif", "image")]      // also a video input; images win
    [InlineData("mp3", "audio")]
    [InlineData("mp4", "video")]
    [InlineData("pdf", "pdf")]
    [InlineData("docx", "document")]
    [InlineData("pptx", "slides")]
    [InlineData("xlsx", "spreadsheet")]
    [InlineData("epub", "epub")]
    [InlineData("srt", "subtitle")]
    [InlineData("zip", "archive")]
    public void Extensions_map_to_the_expected_category(string ext, string expected) =>
        Assert.Equal(expected, Catalog.CategoryForExtension(ext));

    [Theory]
    [InlineData("photo.JPEG", "jpg")]
    [InlineData("a.jfif", "jpg")]
    [InlineData("scan.tif", "tiff")]
    [InlineData("img.HEIF", "heic")]
    [InlineData("track.aif", "aiff")]
    [InlineData("page.htm", "html")]
    [InlineData("readme.markdown", "md")]
    [InlineData("backup.tar.gz", "tgz")]
    [InlineData("backup.TAR.GZ", "tgz")]
    [InlineData("plain.png", "png")]
    public void Alternative_spellings_normalise_to_the_canonical_extension(string path, string expected) =>
        Assert.Equal(expected, ToolCatalog.ExtensionOf(path));

    [Theory]
    [InlineData("jpeg", "image")]
    [InlineData("TIF", "image")]
    [InlineData("heif", "image")]
    [InlineData("htm", "document")]
    public void Aliases_resolve_to_the_right_category(string ext, string category) =>
        Assert.Equal(category, Catalog.CategoryForExtension(ext));

    [Fact]
    public void Unknown_extension_has_no_category() => Assert.Null(Catalog.CategoryForExtension("xyz"));

    [Fact]
    public void Extension_of_ignores_case_and_dot() => Assert.Equal("png", ToolCatalog.ExtensionOf(@"C:\a\Photo.PNG"));

    [Fact]
    public void Tool_lookup_finds_tools_and_actions()
    {
        Assert.Equal("Compress images", Catalog.Tool("img.compress")?.En);
        Assert.Equal("ضغط الصور", Catalog.Tool("img.compress")?.Name("ar"));
        Assert.NotNull(Catalog.Tool("act.copy"));
        Assert.True(Catalog.Tool("pdf.merge")?.Multi);
        Assert.Null(Catalog.Tool("nope"));
    }
}
