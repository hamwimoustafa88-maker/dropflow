using DropFlow.Core.Catalog;

namespace DropFlow.Core.Tests;

public class CatalogTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    [Fact]
    public void Embedded_catalog_loads_all_categories_tools_and_actions()
    {
        Assert.Equal(13, Catalog.Categories.Count);
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
    [InlineData("CR2", "image")]      // camera RAW
    [InlineData("psd", "image")]
    [InlineData("svgz", "image")]
    [InlineData("dcm", "image")]
    [InlineData("json", "data")]      // the data family wins over spreadsheets for JSON
    [InlineData("xml", "data")]
    [InlineData("yaml", "data")]
    [InlineData("vcf", "data")]
    [InlineData("ics", "data")]
    [InlineData("csv", "spreadsheet")]  // a CSV file is still a spreadsheet
    [InlineData("tsv", "spreadsheet")]
    [InlineData("fb2", "epub")]
    [InlineData("mobi", "epub")]
    [InlineData("azw3", "epub")]
    [InlineData("htmlz", "epub")]
    [InlineData("txtz", "epub")]
    [InlineData("cbz", "comic")]
    [InlineData("CBR", "comic")]
    [InlineData("cb7", "comic")]
    [InlineData("ttf", "font")]
    [InlineData("otf", "font")]
    [InlineData("woff", "font")]
    [InlineData("woff2", "font")]
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
    [InlineData("m31.fit", "fits")]
    [InlineData("m31.FTS", "fits")]
    [InlineData("chest.dicom", "dcm")]
    [InlineData("config.yml", "yaml")]
    [InlineData("events.ndjson", "jsonl")]
    public void Alternative_spellings_normalise_to_the_canonical_extension(string path, string expected) =>
        Assert.Equal(expected, ToolCatalog.ExtensionOf(path));

    [Theory]
    [InlineData("jpeg", "image")]
    [InlineData("TIF", "image")]
    [InlineData("heif", "image")]
    [InlineData("htm", "document")]
    [InlineData("yml", "data")]
    [InlineData("NDJSON", "data")]
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

    [Fact]
    public void Every_category_takes_part_in_the_extension_lookup()
    {
        // A category missing from the priority list would never be chosen for its files.
        foreach (var category in Catalog.Categories)
            Assert.Contains(category.Inputs, input => Catalog.CategoryForExtension(input) == category.Id);
    }
}
