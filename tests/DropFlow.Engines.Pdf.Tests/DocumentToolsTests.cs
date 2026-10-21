using System.Text;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DropFlow.TestSupport;
using static DropFlow.Engines.Pdf.Tests.DocxFixture;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DropFlow.Engines.Pdf.Tests;

public class DocxReadingTests
{
    private static string RichBody() =>
        P("Project Plan", "Title") +
        P("Goals", "Heading2") +
        "<w:p>" + R("Plain, ") + R("bold", bold: true) + R(" and ") + R("italic", italic: true) + R(" and ") + "<w:hyperlink r:id=\"rId10\">" + R("a link") + "</w:hyperlink>" + R(".") + "</w:p>" +
        Item("Bullet one", 1) + Item("Nested bullet", 1, 1) +
        Item("First", 2) + Item("Second", 2) + Item("Sub a", 2, 1) +
        Table(["Name", "Qty"], ["Apple", "3"]) +
        "<w:p><w:r><w:t>Line one</w:t><w:br/><w:t>Line two</w:t></w:r></w:p>" +
        P("مرحبا بالعالم", bidi: true) +
        "<w:p>" + R("Use ") + R("x = 1", font: "Consolas") + R(" here.") + "</w:p>" +
        P("Styled", "BulletStyle") +
        "<w:p/>" + // an empty paragraph, as Word writes between sections
        "<w:p><w:r><w:t>Gone</w:t></w:r><w:del w:id=\"1\" w:author=\"x\"><w:r><w:delText>deleted words</w:delText></w:r></w:del></w:p>";

    private const string ExpectedMarkdown =
        "# Project Plan\n\n" +
        "## Goals\n\n" +
        "Plain, **bold** and *italic* and [a link](https://example.com/page).\n\n" +
        "- Bullet one\n    - Nested bullet\n\n" +
        "1. First\n2. Second\n    1. Sub a\n\n" +
        "| Name | Qty |\n| --- | --- |\n| Apple | 3 |\n\n" +
        "Line one  \nLine two\n\n" +
        "مرحبا بالعالم\n\n" +
        "Use `x = 1` here.\n\n" +
        "- Styled\n\n" +
        "Gone\n";

    [Fact]
    public async Task Docx_to_markdown_keeps_headings_formatting_links_lists_and_tables()
    {
        using var dir = new TempDir();
        var docx = Write(dir.Combine("plan.docx"), RichBody());

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [docx]));

        Assert.Equal(dir.Combine("plan (converted).md"), result.Outputs[0]);
        Assert.Equal(ExpectedMarkdown, File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Docx_to_txt_is_readable_plain_text_with_a_byte_order_mark()
    {
        using var dir = new TempDir();
        var docx = Write(dir.Combine("plan.docx"), RichBody());

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [docx]));

        var bytes = File.ReadAllBytes(result.Outputs[0]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Replace("\r\n", "\n");
        Assert.StartsWith("Project Plan\n\nGoals\n\nPlain, bold and italic and a link.\n\n- Bullet one\n    - Nested bullet\n\n1. First\n2. Second\n    1. Sub a\n\nName\tQty\nApple\t3\n\nLine one\nLine two\n\nمرحبا بالعالم", text);
        Assert.DoesNotContain("deleted words", text);
    }

    [Fact]
    public async Task Docx_to_html_builds_nested_lists_tables_and_inline_formatting()
    {
        using var dir = new TempDir();
        var docx = Write(dir.Combine("plan.docx"), RichBody(), title: "My plan");

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [docx]));

        var doc = new HtmlParser().ParseDocument(File.ReadAllText(result.Outputs[0]));
        Assert.Equal("My plan", doc.Title);
        Assert.Equal("Project Plan", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Goals", doc.QuerySelector("h2")!.TextContent);
        Assert.Equal("bold", doc.QuerySelector("strong")!.TextContent);
        Assert.Equal("italic", doc.QuerySelector("em")!.TextContent);
        var link = doc.QuerySelector("a")!;
        Assert.Equal("https://example.com/page", link.GetAttribute("href"));
        Assert.Equal("a link", link.TextContent);
        Assert.Equal("Nested bullet", doc.QuerySelector("ul > li > ul > li")!.TextContent);
        Assert.Equal(["First", "Second"], doc.QuerySelector("ol")!.Children.Select(e => e.FirstChild!.TextContent));
        Assert.Equal("Sub a", doc.QuerySelector("ol > li > ol > li")!.TextContent);
        Assert.Equal(["Name", "Qty"], doc.QuerySelectorAll("thead th").Select(e => e.TextContent));
        Assert.Equal(["Apple", "3"], doc.QuerySelectorAll("tbody td").Select(e => e.TextContent));
        Assert.Equal(1, doc.QuerySelectorAll("p br").Length);
        Assert.Equal("x = 1", doc.QuerySelector("code")!.TextContent);
        Assert.All(doc.QuerySelectorAll("p"), p => Assert.Equal("auto", p.GetAttribute("dir")));
    }

    [Fact]
    public async Task Style_based_headings_outline_levels_and_numbering_through_styles_are_understood()
    {
        using var dir = new TempDir();
        var body = "<w:p><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr>" + R("Outline two") + "</w:p>" + P("Styled item", "BulletStyle");
        var docx = Write(dir.Combine("o.docx"), body);

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [docx]));

        Assert.Equal("## Outline two\n\n- Styled item\n", File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Markdown_special_characters_in_word_text_are_escaped()
    {
        using var dir = new TempDir();
        var docx = Write(dir.Combine("e.docx"), P("2 * 3 = 6, see [1], file_name, #tag, a|b, <html>"));

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [docx]));

        Assert.Equal("2 \\* 3 = 6, see \\[1\\], file\\_name, #tag, a\\|b, \\<html\\>\n", File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task A_docx_made_by_this_module_can_be_read_back()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("Heading here", 72, 90, 24, bold: true).Text("Body text line.", 72, 130);
        var pdf = b.Save(dir.Combine("src.pdf"));
        var docx = (await Kit.Ok(Kit.RunAsync("convert:docx", [pdf]))).Outputs[0];

        var md = (await Kit.Ok(Kit.RunAsync("convert:md", [docx]))).Outputs[0];

        Assert.Equal("# Heading here\n\nBody text line.\n", File.ReadAllText(md).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task A_corrupt_docx_is_reported_by_name()
    {
        using var dir = new TempDir();
        var docx = dir.WriteText("bad.docx", "not a zip");

        var result = await Kit.RunAsync("convert:md", [docx]);

        Assert.False(result.Success);
        Assert.Contains("bad.docx", result.Error);
        Assert.Equal(["bad.docx"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task An_empty_document_gives_an_empty_text_file()
    {
        using var dir = new TempDir();
        var docx = Write(dir.Combine("empty.docx"), "<w:p/>");

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [docx]));

        Assert.Equal("", File.ReadAllText(result.Outputs[0]).Trim());
    }
}

public class MarkdownAndTextConversionTests
{
    private const string Sample =
        "# Guide\n\nSome **bold**, *italic* and `code` with a [link](https://example.com/x).\n\n" +
        "- one\n- two\n    - nested\n\n1. first\n2. second\n\n" +
        "| A | B |\n|---|---|\n| 1 | 2 |\n\n```python\nprint('hi')\n```\n\n> quoted\n\n---\n\nمرحبا بالعالم\n";

    [Fact]
    public async Task Markdown_to_html_renders_everything_and_escapes_raw_html_and_dangerous_links()
    {
        using var dir = new TempDir();
        var md = dir.WriteText("note.md", Sample + "\n<script>alert(1)</script>\n\n[bad](javascript:alert(1)) ![pic](images/a.png)\n\n- [x] done\n- [ ] todo\n");

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [md]));

        var html = File.ReadAllText(result.Outputs[0]);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal("Guide", doc.Title);
        Assert.Equal("Guide", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("bold", doc.QuerySelector("strong")!.TextContent);
        Assert.Equal("https://example.com/x", doc.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("nested", doc.QuerySelector("ul ul li")!.TextContent);
        Assert.Equal(2, doc.QuerySelectorAll("ol li").Length);
        Assert.Equal(["A", "B"], doc.QuerySelectorAll("th").Select(e => e.TextContent));
        Assert.Contains("language-python", doc.QuerySelector("pre code")!.ClassName);
        Assert.NotNull(doc.QuerySelector("blockquote"));
        Assert.NotNull(doc.QuerySelector("hr"));
        Assert.Equal("images/a.png", doc.QuerySelector("img")!.GetAttribute("src"));
        Assert.Empty(doc.QuerySelectorAll("script"));        // raw HTML is shown as text, never executed
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.NotNull(doc.QuerySelector("input[type=checkbox]"));
        Assert.Contains("unicode-bidi:plaintext", html);     // each Arabic block picks its own direction
    }

    [Fact]
    public async Task Markdown_title_comes_from_the_first_heading_or_the_file_name()
    {
        using var dir = new TempDir();
        var withHeading = dir.WriteText("a.md", "text\n\n# Real title\n");
        var without = dir.WriteText("b.md", "just text");

        var a = new HtmlParser().ParseDocument(File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [withHeading]))).Outputs[0]));
        var b = new HtmlParser().ParseDocument(File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [without]))).Outputs[0]));

        Assert.Equal("Real title", a.Title);
        Assert.Equal("b", b.Title);
    }

    [Fact]
    public async Task Arabic_markdown_is_marked_arabic_in_html()
    {
        using var dir = new TempDir();
        var md = dir.WriteText("ar.md", "# عنوان\n\nنص عربي طويل للتجربة\n");

        var result = await Kit.Ok(Kit.RunAsync("convert:html", [md]));

        var doc = new HtmlParser().ParseDocument(File.ReadAllText(result.Outputs[0]));
        Assert.Equal("ar", doc.DocumentElement.GetAttribute("lang"));
        Assert.Equal("عنوان", doc.Title);
    }

    [Fact]
    public async Task Markdown_to_docx_is_a_valid_document_with_real_structure()
    {
        using var dir = new TempDir();
        var md = dir.WriteText("note.md", Sample);

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [md]));

        var path = result.Outputs[0];
        Assert.Equal(dir.Combine("note (converted).docx"), path);
        using var doc = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.Empty(errors);

        var body = doc.MainDocumentPart!.Document!.Body!;
        var paragraphs = body.Elements<W.Paragraph>().ToList();
        string? Style(W.Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        Assert.Equal("Heading1", Style(paragraphs[0]));
        Assert.Equal("Guide", paragraphs[0].InnerText);

        var intro = paragraphs[1];
        Assert.Contains(intro.Descendants<W.Run>(), r => r.RunProperties?.Bold is not null && r.InnerText == "bold");
        Assert.Contains(intro.Descendants<W.Run>(), r => r.RunProperties?.Italic is not null && r.InnerText == "italic");
        Assert.Contains(intro.Descendants<W.Run>(), r => r.RunProperties?.RunFonts?.Ascii?.Value == "Consolas" && r.InnerText == "code");
        var hyperlink = Assert.Single(intro.Descendants<W.Hyperlink>());
        var relationship = doc.MainDocumentPart.HyperlinkRelationships.Single(r => r.Id == hyperlink.Id!.Value);
        Assert.Equal("https://example.com/x", relationship.Uri.ToString());

        var items = paragraphs.Where(p => p.ParagraphProperties?.NumberingProperties is not null).ToList();
        Assert.Equal(["one", "two", "nested", "first", "second"], items.Select(p => p.InnerText));
        Assert.Equal([0, 0, 1], items.Take(3).Select(p => p.ParagraphProperties!.NumberingProperties!.NumberingLevelReference!.Val!.Value));
        var bulletId = items[0].ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        var orderedId = items[3].ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        Assert.NotEqual(bulletId, orderedId);

        var table = Assert.Single(body.Elements<W.Table>());
        Assert.Equal(["A", "B"], table.Elements<W.TableRow>().First().Elements<W.TableCell>().Select(c => c.InnerText));
        Assert.Contains(paragraphs, p => Style(p) == "Code" && p.InnerText == "print('hi')");
        Assert.Contains(paragraphs, p => Style(p) == "Quote");
        var arabic = paragraphs.Single(p => p.InnerText == "مرحبا بالعالم");
        Assert.NotNull(arabic.ParagraphProperties?.BiDi);
    }

    [Fact]
    public async Task Markdown_survives_a_round_trip_through_docx()
    {
        using var dir = new TempDir();
        var original = "# Title\n\nSome **bold**, *italic* and [a link](https://example.com/x).\n\n- one\n- two\n    - nested\n\n1. first\n2. second\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\nمرحبا بالعالم\n";
        var md = dir.WriteText("rt.md", original);

        var docx = (await Kit.Ok(Kit.RunAsync("convert:docx", [md]))).Outputs[0];
        var back = (await Kit.Ok(Kit.RunAsync("convert:md", [docx]))).Outputs[0];

        Assert.Equal(original, File.ReadAllText(back).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Ordered_lists_keep_their_start_number_and_restart_after_a_gap()
    {
        using var dir = new TempDir();
        var md = dir.WriteText("n.md", "5. five\n6. six\n\ntext\n\n1. one again\n");

        var docx = (await Kit.Ok(Kit.RunAsync("convert:docx", [md]))).Outputs[0];
        var back = (await Kit.Ok(Kit.RunAsync("convert:md", [docx]))).Outputs[0];

        Assert.Equal("5. five\n6. six\n\ntext\n\n1. one again\n", File.ReadAllText(back).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Markdown_to_txt_flattens_formatting_but_keeps_structure()
    {
        using var dir = new TempDir();
        var md = dir.WriteText("note.md", Sample);

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [md]));

        var text = File.ReadAllText(result.Outputs[0]).TrimStart('﻿').Replace("\r\n", "\n");
        Assert.StartsWith("Guide\n\nSome bold, italic and code with a link.\n\n- one\n- two\n    - nested\n\n1. first\n2. second\n\nA\tB\n1\t2\n\nprint('hi')\n\nquoted\n\n----------\n\nمرحبا بالعالم", text);
    }

    [Fact]
    public async Task Text_to_docx_keeps_paragraphs_line_breaks_and_arabic_direction()
    {
        using var dir = new TempDir();
        var txt = dir.WriteText("note.txt", "First paragraph\nstill the first\n\nSecond paragraph\n\nمرحبا بالعالم\n");

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [txt]));

        using var doc = WordprocessingDocument.Open(result.Outputs[0], false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2016).Validate(doc));
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ToList();
        Assert.Equal(3, paragraphs.Count);
        Assert.Equal("First paragraphstill the first", paragraphs[0].InnerText);
        Assert.Single(paragraphs[0].Descendants<W.Break>());
        Assert.Null(paragraphs[1].ParagraphProperties?.BiDi);
        Assert.NotNull(paragraphs[2].ParagraphProperties?.BiDi);
        Assert.Equal("note", doc.PackageProperties.Title);
    }

    [Fact]
    public async Task Text_in_windows_1256_is_decoded_before_converting()
    {
        using var dir = new TempDir();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var txt = dir.Combine("old.txt");
        File.WriteAllBytes(txt, Encoding.GetEncoding(1256).GetBytes("مرحبا بالعالم\nسطر ثان"));

        var md = (await Kit.Ok(Kit.RunAsync("convert:md", [txt]))).Outputs[0];

        Assert.Equal("مرحبا بالعالم  \nسطر ثان\n", File.ReadAllText(md).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Text_to_html_and_markdown_escape_their_syntax()
    {
        using var dir = new TempDir();
        var txt = dir.WriteText("raw.txt", "a < b & c > d\n* not a list\n# not a heading\n");

        var html = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [txt]))).Outputs[0]);
        var md = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:md", [txt]))).Outputs[0]).Replace("\r\n", "\n");

        Assert.Contains("a &lt; b &amp; c &gt; d<br>* not a list<br># not a heading", html);
        Assert.Equal("a \\< b & c \\> d  \n\\* not a list  \n\\# not a heading\n", md);
    }

    // ---- html -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Html_to_markdown_converts_structure_and_drops_scripts_and_dangerous_links()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("page.html", """
            <!DOCTYPE html><html><head><title>T</title><style>p{color:red}</style><script>var x=1;</script></head>
            <body><h1>Heading</h1><p>Some <strong>bold</strong> and <em>italic</em> and <a href="https://example.com">a link</a> and <a href="javascript:alert(1)">bad</a>.</p>
            <ul><li>one</li><li>two</li></ul><ol><li>first</li><li>second</li></ol>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table><pre><code>x = 1</code></pre></body></html>
            """);

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [html]));

        var md = File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n");
        Assert.Contains("# Heading", md);
        Assert.Contains("**bold**", md);
        Assert.Contains("[a link](https://example.com)", md);
        Assert.Contains("- one", md);
        Assert.Contains("1. first", md);
        Assert.Contains("| A | B |", md);
        Assert.Contains("x = 1", md);
        Assert.DoesNotContain("var x", md);
        Assert.DoesNotContain("color:red", md);
        Assert.DoesNotContain("javascript:", md);
        Assert.Contains("bad", md); // the text of the dangerous link stays
    }

    [Fact]
    public async Task Html_with_a_declared_legacy_charset_is_decoded_correctly()
    {
        using var dir = new TempDir();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = dir.Combine("legacy.html");
        File.WriteAllBytes(path, Encoding.GetEncoding(1256).GetBytes("<html><head><meta charset=\"windows-1256\"></head><body><p>مرحبا بالعالم</p></body></html>"));

        var md = (await Kit.Ok(Kit.RunAsync("convert:md", [path]))).Outputs[0];

        Assert.Contains("مرحبا بالعالم", File.ReadAllText(md));
    }

    [Fact]
    public async Task Html_to_txt_gives_plain_text()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("page.html", "<h1>Title</h1><p>Hello <b>world</b></p><ul><li>a</li><li>b</li></ul>");

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [html]));

        Assert.Equal("Title\n\nHello world\n\n- a\n- b", File.ReadAllText(result.Outputs[0]).TrimStart('﻿').Replace("\r\n", "\n").Trim());
    }

    // ---- housekeeping ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unicode_names_collisions_and_cancellation()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملاحظات")).FullName;
        var md = Path.Combine(folder, "ملاحظة.md");
        File.WriteAllText(md, "# عنوان\n");

        var first = await Kit.Ok(Kit.RunAsync("convert:html", [md]));
        var second = await Kit.Ok(Kit.RunAsync("convert:html", [md]));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Equal(Path.Combine(folder, "ملاحظة (converted).html"), first.Outputs[0]);
        Assert.Equal(Path.Combine(folder, "ملاحظة (converted 2).html"), second.Outputs[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:docx", [md], ct: cts.Token));
        Assert.Equal(["ملاحظة (converted 2).html", "ملاحظة (converted).html", "ملاحظة.md"], Kit.FilesIn(folder));
    }

    [Theory]
    [InlineData("convert:docx", "txt", true)]
    [InlineData("convert:docx", "md", true)]
    [InlineData("convert:docx", "docx", false)]
    [InlineData("convert:docx", "html", false)]  // html -> docx needs LibreOffice (not claimed by the managed converter)
    [InlineData("convert:html", "docx", true)]
    [InlineData("convert:html", "md", true)]
    [InlineData("convert:html", "html", false)]
    [InlineData("convert:md", "html", true)]
    [InlineData("convert:md", "txt", true)]
    [InlineData("convert:md", "md", false)]
    [InlineData("convert:txt", "md", true)]
    [InlineData("convert:txt", "html", true)]
    [InlineData("convert:txt", "txt", false)]
    [InlineData("convert:txt", "docx", true)]
    public void Supports_covers_exactly_the_managed_pairs(string operation, string ext, bool expected)
    {
        Assert.Equal(expected, Kit.Converters.Where(c => c.EngineFor(operation) == DropFlow.Core.Operations.EngineId.None).Any(c => c.Supports(operation, ext)));
    }
}
