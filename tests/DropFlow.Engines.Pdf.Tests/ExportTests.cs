using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DropFlow.Engines.Pdf.Export;
using DropFlow.TestSupport;
using ImageMagick;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DropFlow.Engines.Pdf.Tests;

public class DocxExportTests
{
    private static string Article(TempDir dir, string? title = null)
    {
        var b = new PdfBuilder();
        if (title is not null) b.Title(title);
        b.AddPage()
            .Text("Annual Report", 72, 90, 24, bold: true)
            .Text("Introduction", 72, 140, 16, bold: true)
            .Text("This is the first line of a paragraph", 72, 170)
            .Text("that continues on the second line.", 72, 185)
            .Text("• Apples", 72, 230)
            .Text("• Oranges", 72, 245)
            .Text("1. First step", 72, 290)
            .Text("2. Second step", 72, 305)
            .Table([["Name", "Qty", "Price"], ["Apple", "3", "1.50"], ["Pear", "10", "2.00"]], [72, 250, 400], 360);
        return b.Save(dir.Combine("article.pdf"));
    }

    internal static List<string> Validate(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return new OpenXmlValidator(FileFormatVersions.Office2016).Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
    }

    [Fact]
    public async Task The_document_is_valid_and_has_real_headings_lists_and_a_table()
    {
        using var dir = new TempDir();
        var pdf = Article(dir, "My Annual Report");

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [pdf]));

        var path = result.Outputs[0];
        Assert.Equal(dir.Combine("article (converted).docx"), path);
        Assert.Empty(Validate(path));

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var paragraphs = body.Elements<W.Paragraph>().ToList();

        string StyleOf(W.Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        Assert.Equal("Heading1", StyleOf(paragraphs[0]));
        Assert.Equal("Annual Report", paragraphs[0].InnerText);
        Assert.Equal("Heading2", StyleOf(paragraphs[1]));
        Assert.Equal("This is the first line of a paragraph that continues on the second line.", paragraphs[2].InnerText);

        var bullets = paragraphs.Where(p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value == 1).ToList();
        Assert.Equal(["Apples", "Oranges"], bullets.Select(p => p.InnerText));
        var numbered = paragraphs.Where(p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value == 2).ToList();
        Assert.Equal(["First step", "Second step"], numbered.Select(p => p.InnerText));

        var table = Assert.Single(body.Elements<W.Table>());
        var rows = table.Elements<W.TableRow>().Select(r => r.Elements<W.TableCell>().Select(c => c.InnerText).ToArray()).ToList();
        Assert.Equal(["Name", "Qty", "Price"], rows[0]);
        Assert.Equal(["Pear", "10", "2.00"], rows[2]);

        Assert.Equal("My Annual Report", doc.PackageProperties.Title);
        Assert.Contains("layout", doc.PackageProperties.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_file_is_readable_without_the_open_xml_sdk_too()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [pdf]));

        using var zip = ZipFile.OpenRead(result.Outputs[0]);
        Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
        Assert.Contains(zip.Entries, e => e.FullName == "word/document.xml");
        Assert.Contains(zip.Entries, e => e.FullName == "word/styles.xml");
        Assert.Contains(zip.Entries, e => e.FullName == "word/numbering.xml");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var xml = XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
        var text = string.Concat(xml.Descendants(w + "t").Select(t => t.Value));
        Assert.Contains("Annual Report", text);
        Assert.Contains("Pear", text);
    }

    [Fact]
    public async Task Page_size_follows_the_pdf_and_later_pages_start_on_a_new_page()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage(595, 842).Text("One", 72, 100);
        b.AddPage(595, 842).Text("Two", 72, 100);
        var pdf = b.Save(dir.Combine("two.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [pdf]));

        using var doc = WordprocessingDocument.Open(result.Outputs[0], false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var size = body.Elements<W.SectionProperties>().Single().GetFirstChild<W.PageSize>()!;
        Assert.Equal((11900u, 16840u), (size.Width!.Value, size.Height!.Value));
        var paragraphs = body.Elements<W.Paragraph>().ToList();
        Assert.Null(paragraphs[0].ParagraphProperties?.PageBreakBefore);
        Assert.NotNull(paragraphs[1].ParagraphProperties?.PageBreakBefore);
        Assert.Empty(Validate(result.Outputs[0]));
    }

    [Fact]
    public async Task Arabic_text_becomes_right_to_left_paragraphs_in_logical_order()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        page.Arabic("مرحبا بالعالم", 540, 100, 16);
        page.Arabic("هذا مستند عربي للتجربة", 540, 140, 16);
        var pdf = b.Save(dir.Combine("ar.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [pdf]));

        using var doc = WordprocessingDocument.Open(result.Outputs[0], false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var paragraphs = body.Elements<W.Paragraph>().Where(p => p.InnerText.Length > 0).ToList();
        Assert.Equal(["مرحبا بالعالم", "هذا مستند عربي للتجربة"], paragraphs.Select(p => p.InnerText));
        Assert.All(paragraphs, p =>
        {
            Assert.NotNull(p.ParagraphProperties?.BiDi);
            Assert.NotNull(p.Descendants<W.RightToLeftText>().FirstOrDefault());
        });
        Assert.NotNull(body.Elements<W.SectionProperties>().Single().GetFirstChild<W.BiDi>());
        Assert.Empty(Validate(result.Outputs[0]));
    }

    [Fact]
    public async Task A_pdf_without_a_text_layer_is_reported_and_nothing_is_written()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage();
        var pdf = b.Save(dir.Combine("blank.pdf"));

        var result = await Kit.RunAsync("convert:docx", [pdf]);

        Assert.False(result.Success);
        Assert.Contains("OCR", result.Error);
        Assert.Equal(["blank.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task The_user_is_told_that_the_layout_may_change()
    {
        using var dir = new TempDir();
        var pdf = Article(dir);
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("convert:docx", [pdf], progress: progress));

        Assert.Contains("Layout", progress.LastMessage);
    }

    [Fact]
    public async Task Unicode_names_do_not_get_in_the_way()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات")).FullName;
        var b = new PdfBuilder();
        b.AddPage().Text("Hello", 72, 100);
        var pdf = b.Save(Path.Combine(folder, "تقرير.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [pdf]));

        Assert.Equal(Path.Combine(folder, "تقرير (converted).docx"), result.Outputs[0]);
        Assert.Empty(Validate(result.Outputs[0]));
    }
}

public class XlsxExportTests
{
    [Fact]
    public async Task A_table_becomes_a_sheet_with_numeric_cells()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage()
            .Text("Price list", 72, 90, 20, bold: true)
            .Table([["Name", "Qty", "Price"], ["Apple", "3", "1.50"], ["Pear", "10", "2,000.25"], ["Code", "007", "n/a"]], [72, 250, 400], 140);
        var pdf = b.Save(dir.Combine("prices.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:xlsx", [pdf]));

        Assert.Equal(dir.Combine("prices (converted).xlsx"), result.Outputs[0]);
        var sheets = XlsxProbe.Read(result.Outputs[0], out _);
        var sheet = Assert.Single(sheets);
        Assert.Equal("Page 1", sheet.Key);
        var cells = sheet.Value;
        Assert.Equal(new XlsxProbe.Cell("Name", false), cells["A1"]);
        Assert.Equal(new XlsxProbe.Cell("Apple", false), cells["A2"]);
        Assert.Equal(new XlsxProbe.Cell("3", true), cells["B2"]);
        Assert.Equal(new XlsxProbe.Cell("1.5", true), cells["C2"]);
        Assert.Equal(new XlsxProbe.Cell("2000.25", true), cells["C3"]);
        Assert.Equal(new XlsxProbe.Cell("007", false), cells["B4"]); // identifiers keep their leading zeros
        Assert.Equal(new XlsxProbe.Cell("n/a", false), cells["C4"]);
    }

    [Fact]
    public async Task Each_table_gets_its_own_sheet_named_after_its_page()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage()
            .Table([["A", "B"], ["1", "2"]], [72, 250], 100)
            .Text("A paragraph in between the two tables", 72, 200)
            .Table([["X", "Y"], ["3", "4"]], [72, 250], 300);
        b.AddPage().Table([["P", "Q"], ["5", "6"]], [72, 250], 100);
        var pdf = b.Save(dir.Combine("tables.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:xlsx", [pdf]));

        var sheets = XlsxProbe.Read(result.Outputs[0], out _);
        Assert.Equal(["Page 1 Table 1", "Page 1 Table 2", "Page 2"], sheets.Keys);
        Assert.Equal("X", sheets["Page 1 Table 2"]["A1"].Text);
        Assert.Equal("5", sheets["Page 2"]["A2"].Text);
    }

    [Fact]
    public async Task Without_tables_each_page_is_a_sheet_with_one_row_per_line()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("First line", 72, 100).Text("Second line", 72, 120);
        b.AddPage().Text("Page two", 72, 100);
        var pdf = b.Save(dir.Combine("text.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:xlsx", [pdf]));

        var sheets = XlsxProbe.Read(result.Outputs[0], out _);
        Assert.Equal(["Page 1", "Page 2"], sheets.Keys);
        Assert.Equal("First line", sheets["Page 1"]["A1"].Text);
        Assert.Equal("Second line", sheets["Page 1"]["A2"].Text);
        Assert.Equal("Page two", sheets["Page 2"]["A1"].Text);
    }

    [Fact]
    public async Task Arabic_tables_keep_logical_column_order_and_mark_the_sheet_right_to_left()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        var page = b.AddPage();
        page.Arabic("الاسم", 540, 100, 14).Arabic("الكمية", 300, 100, 14);
        page.Arabic("تفاح", 540, 120, 14).Arabic("٣", 300, 120, 14);
        var pdf = b.Save(dir.Combine("ar.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:xlsx", [pdf]));

        var sheets = XlsxProbe.Read(result.Outputs[0], out var rtl);
        var sheet = sheets["Page 1"];
        Assert.Equal("الاسم", sheet["A1"].Text);
        Assert.Equal("الكمية", sheet["B1"].Text);
        Assert.Equal("تفاح", sheet["A2"].Text);
        Assert.True(rtl["Page 1"]);
    }

    [Theory]
    [InlineData("12", true)]
    [InlineData("-3.5", true)]
    [InlineData("1,234.50", true)]
    [InlineData("0.5", true)]
    [InlineData("0", true)]
    [InlineData("1,5", false)]       // could be 1.5 in many locales: leave as text
    [InlineData("007", false)]
    [InlineData("+49 30 1234", false)]
    [InlineData("1/2", false)]
    [InlineData("12345678901234567890", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("1.2.3", false)]
    public void Number_detection_is_conservative(string text, bool expected)
    {
        Assert.Equal(expected, XlsxWriter.TryNumber(text, out _));
    }

    [Fact]
    public async Task A_pdf_without_text_reports_it()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage();
        var pdf = b.Save(dir.Combine("blank.pdf"));

        var result = await Kit.RunAsync("convert:xlsx", [pdf]);

        Assert.False(result.Success);
        Assert.Equal(["blank.pdf"], Kit.FilesIn(dir.Path));
    }
}

public class PptxExportTests
{
    private static string Deck(TempDir dir)
    {
        var b = new PdfBuilder();
        b.AddPage().Text("Slide one title", 72, 100, 28, bold: true).Text("Body text of page one", 72, 160);
        b.AddPage().Text("Second page", 72, 100, 20);
        return b.Save(dir.Combine("deck.pdf"));
    }

    private static List<string> Validate(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        return new OpenXmlValidator(FileFormatVersions.Office2016).Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
    }

    [Fact]
    public async Task Every_page_becomes_a_slide_with_the_page_picture_and_searchable_text()
    {
        using var dir = new TempDir();
        var pdf = Deck(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]));

        var path = result.Outputs[0];
        Assert.Equal(dir.Combine("deck (converted).pptx"), path);
        Assert.Empty(Validate(path));

        using var doc = PresentationDocument.Open(path, false);
        var presentation = doc.PresentationPart!.Presentation!;
        var size = presentation.SlideSize!;
        Assert.Equal((7772400, 10058400), (size.Cx!.Value, size.Cy!.Value)); // 612 x 792 pt
        var slides = doc.PresentationPart.SlideParts.ToList();
        Assert.Equal(2, slides.Count);

        var first = slides[0];
        var picture = Assert.Single(first.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Picture>());
        var image = (ImagePart)first.GetPartById(picture.BlipFill!.Blip!.Embed!.Value!);
        Assert.Equal("image/jpeg", image.ContentType);

        var texts = first.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text).ToList();
        Assert.Equal(["Slide one title", "Body text of page one"], texts);
        // The text is there for search and copy, but invisible on top of the picture.
        Assert.All(first.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Alpha>(), a => Assert.Equal(0, a.Val!.Value));
        Assert.Equal(2, first.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Alpha>().Count());
    }

    [Fact]
    public async Task The_slide_picture_really_shows_the_page()
    {
        using var dir = new TempDir();
        var pdf = Deck(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]));

        using var zip = ZipFile.OpenRead(result.Outputs[0]);
        var media = zip.Entries.Where(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)).OrderBy(e => e.FullName).ToList();
        Assert.Equal(2, media.Count);
        using var stream = media[0].Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        using var image = new MagickImage(ms.ToArray());
        Assert.Equal((uint)(612 * PptxWriter.PictureDpi / 72), image.Width);
        Assert.Equal((uint)(792 * PptxWriter.PictureDpi / 72), image.Height);
        image.ColorSpace = ColorSpace.Gray;
        var gray = image.ToByteArray(MagickFormat.Gray);
        Assert.Contains(gray, p => p < 100); // dark text pixels are present
        Assert.True(gray[10] > 250); // white paper
    }

    [Fact]
    public async Task Text_boxes_sit_where_the_text_is_on_the_page()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("Hello", 144, 200, 20); // 2 inches from the left, baseline 200 pt from the top
        var pdf = b.Save(dir.Combine("pos.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]));

        using var doc = PresentationDocument.Open(result.Outputs[0], false);
        var shape = doc.PresentationPart!.SlideParts.Single().Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Shape>().Single();
        var offset = shape.ShapeProperties!.Transform2D!.Offset!;
        Assert.InRange(offset.X!.Value, 144 * 12700 - 2000, 144 * 12700 + 20000);
        // top of the box = baseline - 0.8 * font size = 184 pt
        Assert.InRange(offset.Y!.Value, 183 * 12700, 185 * 12700);
    }

    [Fact]
    public async Task Rotated_pages_become_landscape_slides()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage(612, 792, rotate: 90).Text("Sideways", 72, 100);
        var pdf = b.Save(dir.Combine("rot.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]));

        using var doc = PresentationDocument.Open(result.Outputs[0], false);
        var size = doc.PresentationPart!.Presentation!.SlideSize!;
        Assert.Equal((10058400, 7772400), (size.Cx!.Value, size.Cy!.Value));
    }

    [Fact]
    public async Task A_scanned_pdf_without_text_still_makes_picture_slides()
    {
        using var dir = new TempDir();
        var photo = dir.Combine("scan.png");
        using (var image = new MagickImage(MagickColors.White, 600, 800)) image.Write(photo, MagickFormat.Png24);
        var b = new PdfBuilder();
        b.AddPage().Image(photo, 0, 0, 612, 792);
        var pdf = b.Save(dir.Combine("scan.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]));

        using var doc = PresentationDocument.Open(result.Outputs[0], false);
        var slide = Assert.Single(doc.PresentationPart!.SlideParts);
        Assert.Empty(slide.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Shape>());
        Assert.Single(slide.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Picture>());
    }

    [Fact]
    public async Task Arabic_text_boxes_are_right_to_left()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Arabic("مرحبا بالعالم", 540, 100, 16);
        var pdf = b.Save(dir.Combine("ar.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]));

        using var doc = PresentationDocument.Open(result.Outputs[0], false);
        var slide = doc.PresentationPart!.SlideParts.Single().Slide!;
        Assert.Equal("مرحبا بالعالم", slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Single().Text);
        Assert.Equal(true, slide.Descendants<DocumentFormat.OpenXml.Drawing.ParagraphProperties>().Single().RightToLeft?.Value);
        Assert.Empty(Validate(result.Outputs[0]));
    }

    [Theory]
    [InlineData(612, 792, 7772400, 10058400)]
    [InlineData(10, 10, 914400, 914400)]            // smaller than PowerPoint allows: enlarged
    [InlineData(5000, 2500, 51206400, 25603200)]    // bigger than 56 inches: shrunk, aspect kept
    public void Slide_sizes_stay_inside_the_limits_powerpoint_accepts(double w, double h, long cx, long cy)
    {
        var size = PptxWriter.SlideSize(w, h);
        Assert.InRange(size.Cx, cx - 1, cx + 1);
        Assert.InRange(size.Cy, cy - 1, cy + 1);
    }

    [Fact]
    public async Task Cancellation_leaves_no_file_behind()
    {
        using var dir = new TempDir();
        var pdf = Deck(dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:pptx", [pdf], ct: cts.Token));

        Assert.Equal(["deck.pdf"], Kit.FilesIn(dir.Path));
    }
}

public class EpubExportTests
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";

    private static string Book(TempDir dir)
    {
        var b = new PdfBuilder().Title("Field Guide");
        var p1 = b.AddPage();
        p1.Text("Chapter One", 72, 90, 24, bold: true).Text("The first chapter body text.", 72, 130);
        var p2 = b.AddPage();
        p2.Text("Chapter Two", 72, 90, 24, bold: true).Text("The second chapter body text with <markup> & symbols.", 72, 130);
        return b.Save(dir.Combine("guide.pdf"));
    }

    /// <summary>Checks the container rules from the EPUB specification and returns the parsed package document.</summary>
    private static XDocument ValidateStructure(string epub)
    {
        var bytes = File.ReadAllBytes(epub);

        // 1. The first local file header is "mimetype", stored (method 0), with the exact content.
        Assert.Equal([0x50, 0x4B, 0x03, 0x04], bytes.Take(4));
        Assert.Equal((byte)0, bytes[8]); // compression method: stored
        Assert.Equal((byte)0, bytes[9]);
        var nameLength = bytes[26] | (bytes[27] << 8);
        var extraLength = bytes[28] | (bytes[29] << 8);
        Assert.Equal(0, extraLength);
        Assert.Equal("mimetype", System.Text.Encoding.ASCII.GetString(bytes, 30, nameLength));
        Assert.Equal("application/epub+zip", System.Text.Encoding.ASCII.GetString(bytes, 30 + nameLength, 20));

        using var zip = ZipFile.OpenRead(epub);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(zip.Entries.Count, zip.Entries.Select(e => e.FullName).Distinct().Count());

        // 2. container.xml points at the package document.
        var container = XDocument.Load(zip.GetEntry("META-INF/container.xml")!.Open());
        var rootfile = container.Descendants(Container + "rootfile").Single();
        Assert.Equal("application/oebps-package+xml", rootfile.Attribute("media-type")!.Value);
        var opfPath = rootfile.Attribute("full-path")!.Value;
        var package = XDocument.Load(zip.GetEntry(opfPath)!.Open());
        var baseDir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";

        // 3. The package: version, identifier, title, language, modified date.
        var root = package.Root!;
        Assert.Equal("3.0", root.Attribute("version")!.Value);
        var identifierId = root.Attribute("unique-identifier")!.Value;
        Assert.Contains(root.Descendants(Dc + "identifier"), e => e.Attribute("id")?.Value == identifierId && e.Value.StartsWith("urn:uuid:", StringComparison.Ordinal));
        Assert.NotEmpty(root.Descendants(Dc + "title").Single().Value);
        Assert.NotEmpty(root.Descendants(Dc + "language").Single().Value);
        var modified = root.Descendants(Opf + "meta").Single(m => m.Attribute("property")?.Value == "dcterms:modified").Value;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", modified);

        // 4. Every manifest item exists in the zip and every XML/XHTML file is well-formed.
        var items = root.Descendants(Opf + "item").ToList();
        foreach (var item in items)
        {
            var entry = zip.GetEntry(baseDir + item.Attribute("href")!.Value);
            Assert.NotNull(entry);
            if (item.Attribute("media-type")!.Value == "application/xhtml+xml")
            {
                var xhtml = XDocument.Load(entry.Open());
                Assert.Equal(Xhtml + "html", xhtml.Root!.Name);
            }
        }
        Assert.Equal(items.Count, items.Select(i => i.Attribute("id")!.Value).Distinct().Count());

        // 5. Exactly one nav document, and every spine item refers to a manifest item.
        Assert.Single(items, i => i.Attribute("properties")?.Value == "nav");
        var ids = items.Select(i => i.Attribute("id")!.Value).ToHashSet();
        foreach (var itemref in root.Descendants(Opf + "itemref")) Assert.Contains(itemref.Attribute("idref")!.Value, ids);
        return package;
    }

    [Fact]
    public async Task The_book_follows_the_epub_3_container_and_package_rules()
    {
        using var dir = new TempDir();
        var pdf = Book(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]));

        Assert.Equal(dir.Combine("guide (converted).epub"), result.Outputs[0]);
        var package = ValidateStructure(result.Outputs[0]);
        Assert.Equal("Field Guide", package.Descendants(Dc + "title").Single().Value);
        Assert.Equal("en", package.Descendants(Dc + "language").Single().Value);
    }

    [Fact]
    public async Task Chapters_follow_the_top_level_headings_and_the_toc_links_to_them()
    {
        using var dir = new TempDir();
        var pdf = Book(dir);

        var result = await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]));

        using var zip = ZipFile.OpenRead(result.Outputs[0]);
        var nav = XDocument.Load(zip.GetEntry("OEBPS/nav.xhtml")!.Open());
        var links = nav.Descendants(Xhtml + "a").ToList();
        Assert.Equal(["Chapter One", "Chapter Two"], links.Select(a => a.Value));
        Assert.All(links, a => Assert.NotNull(zip.GetEntry("OEBPS/" + a.Attribute("href")!.Value)));

        var chapter2 = XDocument.Load(zip.GetEntry("OEBPS/chapter2.xhtml")!.Open());
        Assert.Equal("Chapter Two", chapter2.Descendants(Xhtml + "h1").Single().Value);
        Assert.Contains("<markup> & symbols", chapter2.Descendants(Xhtml + "p").Single().Value); // escaped in the file, intact when parsed
    }

    [Fact]
    public async Task A_pdf_without_headings_is_cut_every_ten_pages()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("plain.pdf"), 12, n => $"Plain page {n}");

        var result = await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]));

        var package = ValidateStructure(result.Outputs[0]);
        var chapters = package.Descendants(Opf + "itemref").Where(i => i.Attribute("idref")!.Value.StartsWith("chapter", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, chapters.Count);
        using var zip = ZipFile.OpenRead(result.Outputs[0]);
        var nav = XDocument.Load(zip.GetEntry("OEBPS/nav.xhtml")!.Open());
        Assert.Equal(["Pages 1-10", "Pages 11-12"], nav.Descendants(Xhtml + "a").Select(a => a.Value));
    }

    [Fact]
    public async Task Arabic_books_are_marked_right_to_left()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Arabic("مرحبا بالعالم", 540, 100, 16);
        var pdf = b.Save(dir.Combine("ar.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]));

        var package = ValidateStructure(result.Outputs[0]);
        Assert.Equal("ar", package.Descendants(Dc + "language").Single().Value);
        Assert.Equal("rtl", package.Descendants(Opf + "spine").Single().Attribute("page-progression-direction")!.Value);
        using var zip = ZipFile.OpenRead(result.Outputs[0]);
        var chapter = XDocument.Load(zip.GetEntry("OEBPS/chapter1.xhtml")!.Open());
        Assert.Equal("rtl", chapter.Root!.Attribute("dir")!.Value);
        Assert.Equal("مرحبا بالعالم", chapter.Descendants(Xhtml + "p").Single().Value);
    }

    [Fact]
    public async Task A_pdf_without_a_text_layer_is_reported()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage();
        var pdf = b.Save(dir.Combine("blank.pdf"));

        var result = await Kit.RunAsync("convert:epub", [pdf]);

        Assert.False(result.Success);
        Assert.Equal(["blank.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task The_epub_can_be_converted_twice_without_overwriting()
    {
        using var dir = new TempDir();
        var pdf = Book(dir);

        var first = await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]));
        var second = await Kit.Ok(Kit.RunAsync("convert:epub", [pdf]));

        Assert.NotEqual(first.Outputs[0], second.Outputs[0]);
        Assert.Equal(dir.Combine("guide (converted 2).epub"), second.Outputs[0]);
    }
}
