using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Office;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

public class EpubReadingTests
{
    [Fact]
    public async Task Epub_to_html_is_one_clean_self_contained_page_in_reading_order()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("book.epub"));

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [epub]));

        Assert.Equal(dir.Combine("book (converted).html"), result.Outputs[0]);
        var html = File.ReadAllText(result.Outputs[0]);
        var doc = new HtmlParser().ParseDocument(html);

        Assert.Equal("Sample Book", doc.Title);
        Assert.Equal("en", doc.DocumentElement.GetAttribute("lang"));
        Assert.Equal(["Chapter One", "Chapter Two"], doc.QuerySelectorAll("h1").Select(e => e.TextContent));
        Assert.Equal(["ch1", "ch2"], doc.QuerySelectorAll("section").Select(e => e.Id));
        Assert.DoesNotContain("NAVTEXT", html);     // the navigation document is not a chapter
        Assert.DoesNotContain("NOTES", html);       // linear="no" items are not part of the main reading order
        Assert.True(html.IndexOf("Chapter One", StringComparison.Ordinal) < html.IndexOf("Chapter Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Untrusted_content_is_stripped_from_the_html()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("book.epub"));

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [epub]));

        var html = File.ReadAllText(result.Outputs[0]);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("alert(", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("evil", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("color:blue", html);
        Assert.DoesNotContain("color:red", html);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Empty(doc.Body!.QuerySelectorAll("style")); // only the wrapper page's own stylesheet remains, in <head>
        Assert.Contains("bad link", doc.Body!.TextContent); // the text stays, the dangerous link goes
    }

    [Fact]
    public async Task Links_between_chapters_become_anchors_and_pictures_are_inlined()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("book.epub"));

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [epub]));

        var doc = new HtmlParser().ParseDocument(File.ReadAllText(result.Outputs[0]));
        var link = doc.QuerySelectorAll("a").Single(a => a.TextContent == "link to chapter two");
        Assert.Equal("#sec", link.GetAttribute("href"));
        Assert.NotNull(doc.GetElementById("sec"));

        var img = doc.QuerySelector("img")!;
        var src = img.GetAttribute("src")!;
        Assert.StartsWith("data:image/png;base64,", src);
        var bytes = Convert.FromBase64String(src["data:image/png;base64,".Length..]);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes.Take(4)); // a real PNG, found despite the %20 in its path
        Assert.Equal("A cover picture", img.GetAttribute("alt"));
        Assert.Contains("Missing picture", doc.Body!.TextContent); // a picture missing from the zip is replaced by its alt text
    }

    [Fact]
    public async Task Epub_to_markdown_keeps_structure_and_drops_pictures()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("book.epub"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [epub]));

        var md = File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n");
        Assert.Contains("# Chapter One", md);
        Assert.Contains("# Chapter Two", md);
        Assert.Contains("## Section", md);
        Assert.Contains("**bold**", md);
        Assert.Contains("[link to chapter two](#sec)", md);
        Assert.Contains("- one", md);
        Assert.Contains("| A | B |", md);
        Assert.Contains("مرحبا بالعالم", md);
        Assert.DoesNotContain("data:image", md);
        Assert.DoesNotContain("NAVTEXT", md);
    }

    [Fact]
    public async Task Epub_to_text_is_plain_readable_text()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("book.epub"));

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [epub]));

        var text = File.ReadAllText(result.Outputs[0]).TrimStart('﻿').Replace("\r\n", "\n");
        Assert.StartsWith("Chapter One\n\nFirst bold and italic text with a link to chapter two and bad link.", text);
        Assert.Contains("- one\n- two", text);
        Assert.Contains("A\tB\n1\t2", text);
        Assert.DoesNotContain("**", text);
    }

    [Fact]
    public async Task An_arabic_epub_is_marked_arabic()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("ar.epub"), title: "كتاب", language: "ar-SA");

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [epub]));

        var doc = new HtmlParser().ParseDocument(File.ReadAllText(result.Outputs[0]));
        Assert.Equal("ar", doc.DocumentElement.GetAttribute("lang"));
        Assert.Equal("كتاب", doc.Title);
    }

    [Fact]
    public async Task A_drm_protected_epub_is_refused_clearly()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("locked.epub"), drm: true);

        var result = await Kit.RunAsync("convert:md", [epub]);

        Assert.False(result.Success);
        Assert.Contains("DRM", result.Error);
        Assert.Equal(["locked.epub"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_corrupt_epub_is_reported_by_name()
    {
        using var dir = new TempDir();
        var epub = dir.WriteText("bad.epub", "not a zip file");

        var result = await Kit.RunAsync("convert:html", [epub]);

        Assert.False(result.Success);
        Assert.Contains("bad.epub", result.Error);
        Assert.Equal(["bad.epub"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_zip_that_is_not_an_epub_is_reported()
    {
        using var dir = new TempDir();
        var path = dir.Combine("other.epub");
        using (var stream = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("hello.txt");

        var result = await Kit.RunAsync("convert:html", [path]);

        Assert.False(result.Success);
        Assert.Contains("not a valid EPUB", result.Error);
    }

    [Fact]
    public async Task An_epub_written_by_this_module_can_be_read_back()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Title("Round Trip");
        b.AddPage().Text("Chapter A", 72, 90, 24, bold: true).Text("Alpha text here.", 72, 130);
        b.AddPage().Text("Chapter B", 72, 90, 24, bold: true).Text("Beta text here.", 72, 130);
        var pdf = b.Save(dir.Combine("rt.pdf"));
        var epub = (await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]))).Outputs[0];

        var md = (await Kit.Ok(Kit.RunAsync("convert:md", [epub]))).Outputs[0];

        var text = File.ReadAllText(md).Replace("\r\n", "\n");
        Assert.Contains("# Chapter A", text);
        Assert.Contains("Alpha text here.", text);
        Assert.Contains("# Chapter B", text);
        Assert.True(text.IndexOf("Alpha", StringComparison.Ordinal) < text.IndexOf("Beta", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "OPS/a.xhtml", "OPS/a.xhtml")]
    [InlineData("OPS/text/", "../images/p.png", "OPS/images/p.png")]
    [InlineData("OPS/text/", "ch2.xhtml", "OPS/text/ch2.xhtml")]
    [InlineData("OPS/", "images/cover%20pic.png", "OPS/images/cover pic.png")]
    [InlineData("OPS/text/", "/root.png", "root.png")]
    [InlineData("OPS/", "./a/./b.png", "OPS/a/b.png")]
    [InlineData("OPS/", "../../up.png", "up.png")]
    public void Paths_inside_the_zip_are_resolved_like_the_epub_spec_says(string baseDir, string href, string expected)
    {
        Assert.Equal(expected, EpubReader.Resolve(baseDir, href));
    }
}
