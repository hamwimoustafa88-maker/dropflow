using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Books;
using DropFlow.Engines.Pdf.Office;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>FB2, MOBI/AZW3, HTMLZ and TXTZ to and from the other e-book formats, read back with independent parsers (XML, zip, HTML).</summary>
public sealed class BookConversionTests : IDisposable
{
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace L = "http://www.w3.org/1999/xlink";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- every pair the catalog promises ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_ebook_pair_has_exactly_one_converter_and_the_right_engine()
    {
        var converters = PdfModule.Create(new NoEngines());
        string[] inputs = ["epub", "fb2", "mobi", "azw3", "htmlz", "txtz"];
        string[] inProcess = ["html", "md", "txt", "epub", "fb2", "htmlz", "txtz"];
        foreach (var input in inputs)
        {
            foreach (var target in inProcess.Where(t => t != input))
            {
                var owners = converters.Where(c => c.Supports($"convert:{target}", input)).ToList();
                Assert.True(owners.Count == 1, $"{input} -> {target}: {owners.Count} converters");
                Assert.Equal(EngineId.None, owners[0].EngineFor($"convert:{target}"));
            }
            foreach (var target in new[] { "pdf", "docx", "odt", "rtf" })
            {
                var owner = Assert.Single(converters, c => c.Supports($"convert:{target}", input));
                Assert.Equal(EngineId.LibreOffice, owner.EngineFor($"convert:{target}"));
            }
            Assert.DoesNotContain(converters, c => c.Supports($"convert:{input}", input));
        }
        Assert.DoesNotContain(converters, c => c.Supports("convert:mobi", "epub"));
        Assert.DoesNotContain(converters, c => c.Supports("convert:azw3", "epub"));
    }

    // ---- FB2 --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Arabic_fb2_to_epub_keeps_text_order_pictures_and_the_right_to_left_direction()
    {
        var fb2 = BookFixtures.Fb2Arabic(_dir.Combine("كتاب.fb2"));
        var result = await Kit.Ok(Kit.RunAsync("convert:epub", [fb2]));
        Assert.Equal(_dir.Combine("كتاب (converted).epub"), result.Outputs[0]);

        using var zip = ZipFile.OpenRead(result.Outputs[0]);
        AssertEpubContainer(zip);
        var opf = XDocument.Parse(Read(zip, "OEBPS/content.opf"));
        Assert.Equal("ar", opf.Descendants().Single(e => e.Name.LocalName == "language").Value);
        Assert.Equal(BookFixtures.ArabicTitle, opf.Descendants().Single(e => e.Name.LocalName == "title").Value);
        Assert.Equal("أحمد شوقي", opf.Descendants().Single(e => e.Name.LocalName == "creator").Value);
        Assert.Equal("rtl", opf.Descendants().Single(e => e.Name.LocalName == "spine").Attribute("page-progression-direction")!.Value);
        Assert.Contains(opf.Descendants().Where(e => e.Name.LocalName == "item"), i => i.Attribute("media-type")!.Value == "image/png");

        var chapters = zip.Entries.Where(e => e.FullName.StartsWith("OEBPS/chapter", StringComparison.Ordinal)).OrderBy(e => e.FullName).ToList();
        Assert.True(chapters.Count >= 3, "two chapters, the title page and the notes");
        var all = new StringBuilder();
        foreach (var chapter in chapters)
        {
            var xhtml = XDocument.Parse(Read(zip, chapter.FullName)); // well-formed XML
            Assert.Equal("rtl", xhtml.Root!.Attribute("dir")!.Value);
            all.Append(xhtml.Root.Value);
        }
        var text = all.ToString();
        Assert.True(text.IndexOf(BookFixtures.ArabicChapter, StringComparison.Ordinal) < text.IndexOf(BookFixtures.ArabicText, StringComparison.Ordinal));
        Assert.True(text.IndexOf(BookFixtures.ArabicText, StringComparison.Ordinal) < text.IndexOf("نهاية الكتاب", StringComparison.Ordinal));
        Assert.Contains("بيت أول", text);
        Assert.Contains("حاشية", text);
        Assert.NotNull(zip.GetEntry("OEBPS/images/image1.png"));
    }

    [Fact]
    public async Task Fb2_to_html_md_and_txt()
    {
        var fb2 = BookFixtures.Fb2Arabic(_dir.Combine("book.fb2"));
        var html = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [fb2]))).Outputs[0]);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal("rtl", doc.DocumentElement.GetAttribute("dir"));
        Assert.Equal("ar", doc.DocumentElement.GetAttribute("lang"));
        Assert.Contains(BookFixtures.ArabicChapter, doc.QuerySelectorAll("h1").Select(h => h.TextContent));
        Assert.StartsWith("data:image/png;base64,", doc.QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal("#n1", doc.QuerySelectorAll("a[href]").Single().GetAttribute("href"));
        Assert.NotNull(doc.GetElementById("n1"));

        var md = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:md", [fb2]))).Outputs[0]);
        Assert.Contains("# " + BookFixtures.ArabicChapter, md);
        Assert.Contains("**مهم**", md);
        Assert.DoesNotContain("data:image", md);

        var txt = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:txt", [fb2]))).Outputs[0]);
        Assert.Contains(BookFixtures.ArabicText, txt);
        Assert.DoesNotContain("**", txt);
    }

    [Fact]
    public async Task A_windows_1251_fb2_is_decoded()
    {
        var fb2 = BookFixtures.Fb2Cyrillic(_dir.Combine("ru.fb2"));
        var txt = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:txt", [fb2]))).Outputs[0]);
        Assert.Contains("Глава первая", txt);
        Assert.Contains("Привет, мир!", txt);
    }

    [Fact]
    public async Task Epub_to_fb2_is_valid_fictionbook_with_sections_pictures_and_safe_links()
    {
        var epub = EpubFixture.Write(_dir.Combine("book.epub"));
        var result = await Kit.Ok(Kit.RunAsync("convert:fb2", [epub]));
        var document = XDocument.Load(result.Outputs[0]);
        var root = document.Root!;
        Assert.Equal(Fb + "FictionBook", root.Name);
        var info = root.Element(Fb + "description")!.Element(Fb + "title-info")!;
        Assert.Equal("Sample Book", info.Element(Fb + "book-title")!.Value);
        Assert.Equal("en", info.Element(Fb + "lang")!.Value);
        Assert.Equal("An", info.Element(Fb + "author")!.Element(Fb + "first-name")!.Value);
        Assert.Equal("Author", info.Element(Fb + "author")!.Element(Fb + "last-name")!.Value);
        Assert.NotNull(info.Element(Fb + "genre"));
        Assert.NotNull(root.Element(Fb + "description")!.Element(Fb + "document-info")!.Element(Fb + "id"));

        var sections = root.Element(Fb + "body")!.Elements(Fb + "section").ToList();
        Assert.Equal(["Chapter One", "Chapter Two"], sections.Select(s => s.Element(Fb + "title")!.Value));
        Assert.All(sections, s => Assert.Equal("title", s.Elements().First().Name.LocalName)); // the title comes first
        Assert.Contains(sections[1].Elements(Fb + "subtitle"), s => s.Value == "Section");
        Assert.Contains(sections[0].Descendants(Fb + "strong"), s => s.Value == "bold");
        Assert.Contains(sections[1].Descendants(Fb + "table"), t => t.Descendants(Fb + "th").Any());

        var binary = Assert.Single(root.Elements(Fb + "binary"));
        Assert.Equal("image/png", binary.Attribute("content-type")!.Value);
        var image = Assert.Single(root.Descendants(Fb + "image"));
        Assert.Equal("#" + binary.Attribute("id")!.Value, image.Attribute(L + "href")!.Value);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], Convert.FromBase64String(binary.Value).Take(4));

        var xml = File.ReadAllText(result.Outputs[0]);
        Assert.DoesNotContain("javascript", xml);
        Assert.DoesNotContain("alert", xml);
        Assert.Contains("bad link", xml); // the text stays
        // Paragraphs never contain block elements or line breaks.
        Assert.DoesNotContain(root.Descendants(Fb + "p"), p => p.Elements().Any(e => e.Name.LocalName is "p" or "section" or "br"));
    }

    [Fact]
    public async Task An_fb2_written_here_reads_back_with_its_text_and_picture()
    {
        var epub = EpubFixture.Write(_dir.Combine("book.epub"));
        var fb2 = (await Kit.Ok(Kit.RunAsync("convert:fb2", [epub]))).Outputs[0];
        var html = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [fb2]))).Outputs[0]);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Contains("Chapter Two", doc.Body!.TextContent);
        Assert.Contains("مرحبا بالعالم", doc.Body.TextContent);
        Assert.StartsWith("data:image/png", doc.QuerySelector("img")!.GetAttribute("src"));
    }

    [Fact]
    public async Task A_damaged_fb2_fails_by_name()
    {
        var bad = _dir.WriteText("bad.fb2", "<FictionBook><body><p>unclosed</body>");
        var result = await Kit.RunAsync("convert:epub", [bad]);
        Assert.False(result.Success);
        Assert.Contains("bad.fb2", result.Error);
        Assert.Equal(["bad.fb2"], Kit.FilesIn(_dir.Path));
    }

    // ---- MOBI / AZW3 --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, 4096)]       // PalmDOC
    [InlineData(2, 64)]         // PalmDOC, many small records: trailing entries on each
    [InlineData(1, 4096)]       // no compression
    [InlineData(17480, 4096)]   // HUFF/CDIC
    public async Task Mobi_to_html_reads_text_metadata_pictures_and_links(int compression, int recordSize)
    {
        var mobi = BookFixtures.Mobi(_dir.Combine("book.mobi"), (BookFixtures.MobiCompression)compression, recordSize: recordSize);
        var html = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [mobi]))).Outputs[0]);
        var doc = new HtmlParser().ParseDocument(html);

        Assert.Equal(BookFixtures.ArabicTitle, doc.Title);                    // EXTH 503
        Assert.Equal("أحمد شوقي", doc.QuerySelector("meta[name=author]")!.GetAttribute("content"));
        Assert.Equal("ar", doc.DocumentElement.GetAttribute("lang"));        // EXTH 524
        Assert.Equal("rtl", doc.DocumentElement.GetAttribute("dir"));
        Assert.Equal([BookFixtures.ArabicChapter, "Chapter Two"], doc.QuerySelectorAll("h1").Select(h => h.TextContent));
        Assert.Contains(BookFixtures.ArabicText, doc.Body!.TextContent);
        Assert.Contains("Second chapter text. Second chapter text.", doc.Body.TextContent);
        Assert.DoesNotContain("AA", doc.Body.TextContent);                   // trailing entries are not text
        Assert.Equal(2, doc.QuerySelectorAll("section").Length);              // split at mbp:pagebreak

        var img = doc.QuerySelector("img")!;
        Assert.StartsWith("data:image/png;base64,", img.GetAttribute("src"));
        var link = doc.QuerySelectorAll("a[href]").Single(a => a.TextContent == "go to chapter two");
        var target = doc.GetElementById(link.GetAttribute("href")![1..]);
        Assert.NotNull(target);
        Assert.True(target!.CompareDocumentPosition(doc.QuerySelectorAll("h1")[1]).HasFlag(AngleSharp.Dom.DocumentPositions.Following));
    }

    [Fact]
    public async Task Mobi_to_epub_and_txt()
    {
        var mobi = BookFixtures.Mobi(_dir.Combine("book.mobi"));
        var epub = (await Kit.Ok(Kit.RunAsync("convert:epub", [mobi]))).Outputs[0];
        using (var zip = ZipFile.OpenRead(epub))
        {
            AssertEpubContainer(zip);
            Assert.Equal("rtl", XDocument.Parse(Read(zip, "OEBPS/content.opf")).Descendants().Single(e => e.Name.LocalName == "spine").Attribute("page-progression-direction")!.Value);
            var chapter2 = XDocument.Parse(Read(zip, "OEBPS/chapter2.xhtml"));
            Assert.Contains("Chapter Two", chapter2.Root!.Value);
            // The link into chapter two now points at the other file.
            var chapter1 = Read(zip, "OEBPS/chapter1.xhtml");
            Assert.Contains("href=\"chapter2.xhtml#filepos", chapter1);
        }
        var txt = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:txt", [mobi]))).Outputs[0]);
        Assert.Contains(BookFixtures.ArabicText, txt);
    }

    [Fact]
    public async Task Azw3_kf8_text_is_read_from_its_first_flow_with_pictures()
    {
        var azw3 = BookFixtures.Azw3(_dir.Combine("book.azw3"));
        var html = File.ReadAllText((await Kit.Ok(Kit.RunAsync("convert:html", [azw3]))).Outputs[0]);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal("KF8 Book", doc.Title);
        Assert.Equal("rtl", doc.DocumentElement.GetAttribute("dir"));
        Assert.Equal([BookFixtures.ArabicChapter, "Chapter Two"], doc.QuerySelectorAll("h1").Select(h => h.TextContent));
        Assert.Equal(2, doc.QuerySelectorAll("section").Length);
        Assert.StartsWith("data:image/png;base64,", doc.QuerySelector("img")!.GetAttribute("src"));
        Assert.DoesNotContain("CSS-FLOW-MARKER", html);  // the stylesheet flow is not text
        Assert.DoesNotContain("kindle:", html);
    }

    [Fact]
    public async Task A_drm_protected_mobi_is_refused_and_nothing_is_written()
    {
        var mobi = BookFixtures.Mobi(_dir.Combine("locked.mobi"), encryption: 2);
        var result = await Kit.RunAsync("convert:epub", [mobi]);
        Assert.False(result.Success);
        Assert.Contains("DRM", result.Error);
        Assert.Equal(["locked.mobi"], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task A_file_that_is_not_a_mobi_is_reported()
    {
        var bad = _dir.WriteText("fake.azw3", new string('x', 400));
        var result = await Kit.RunAsync("convert:html", [bad]);
        Assert.False(result.Success);
        Assert.Contains("fake.azw3", result.Error);
    }

    [Theory]
    [InlineData("0001", 1)]
    [InlineData("000A", 10)]
    [InlineData("000V", 31)]
    [InlineData("0010", 32)]
    [InlineData("00a1", 321)]
    public void Kf8_resource_numbers_are_base_32(string digits, int expected) => Assert.Equal(expected, Mobi.Base32(digits));

    [Fact]
    public void Trailing_entries_are_measured_from_the_end()
    {
        byte[] record = [.. "text"u8, 0x01, 0x00, 0x00, 0x01, 0xAA, 0xBB, 0x83]; // multibyte: 1 + 1 bytes; one entry of 3 bytes
        Assert.Equal(2 + 3, Mobi.TrailingSize(record, 0b11));
        Assert.Equal(3, Mobi.TrailingSize(record, 0b10));
        Assert.Equal(0, Mobi.TrailingSize(record, 0));
    }

    [Fact]
    public void PalmDoc_decompression_handles_every_code()
    {
        var text = Encoding.UTF8.GetBytes("Hello Hello Hello world, عربي عربي. abc abc abc abc");
        using var output = new MemoryStream();
        PalmDoc.Decompress(BookFixtures.PalmDocCompress(text), output);
        Assert.Equal(text, output.ToArray());
    }

    // ---- HTMLZ / TXTZ -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Htmlz_is_read_with_its_metadata_and_pictures_and_split_into_chapters()
    {
        var htmlz = BookFixtures.Htmlz(_dir.Combine("book.htmlz"));
        var epub = (await Kit.Ok(Kit.RunAsync("convert:epub", [htmlz]))).Outputs[0];
        using var zip = ZipFile.OpenRead(epub);
        var opf = XDocument.Parse(Read(zip, "OEBPS/content.opf"));
        Assert.Equal("Zipped HTML Book", opf.Descendants().Single(e => e.Name.LocalName == "title").Value);
        Assert.Equal("ltr", opf.Descendants().Single(e => e.Name.LocalName == "spine").Attribute("page-progression-direction")!.Value);
        Assert.Contains("First", Read(zip, "OEBPS/chapter1.xhtml"));
        Assert.Contains("Second", Read(zip, "OEBPS/chapter2.xhtml"));
        Assert.NotNull(zip.GetEntry("OEBPS/images/image1.png"));
        Assert.DoesNotContain(zip.Entries, e => Read(zip, e.FullName, binary: true).Contains("alert(1)"));
    }

    [Fact]
    public async Task Arabic_txtz_becomes_htmlz_and_back_with_pictures_and_metadata()
    {
        var txtz = BookFixtures.Txtz(_dir.Combine("book.txtz"));
        var htmlz = (await Kit.Ok(Kit.RunAsync("convert:htmlz", [txtz]))).Outputs[0];
        using (var zip = ZipFile.OpenRead(htmlz))
        {
            var index = new HtmlParser().ParseDocument(Read(zip, "index.html"));
            Assert.Equal("rtl", index.DocumentElement.GetAttribute("dir"));
            Assert.Equal(BookFixtures.ArabicTitle, index.Title);
            var src = index.QuerySelector("img")!.GetAttribute("src")!;
            Assert.StartsWith("images/", src);
            Assert.NotNull(zip.GetEntry(src));
            var metadata = XDocument.Parse(Read(zip, "metadata.opf"));
            Assert.Equal("ar", metadata.Descendants().Single(e => e.Name.LocalName == "language").Value);
        }

        var back = (await Kit.Ok(Kit.RunAsync("convert:txtz", [htmlz]))).Outputs[0];
        using (var zip = ZipFile.OpenRead(back))
        {
            var text = Read(zip, "index.txt");
            Assert.Contains("# " + BookFixtures.ArabicChapter, text);
            Assert.Contains(BookFixtures.ArabicText, text);
            Assert.Contains("](images/image1.png)", text);
            Assert.NotNull(zip.GetEntry("images/image1.png"));
            Assert.Equal(BookFixtures.ArabicTitle, XDocument.Parse(Read(zip, "metadata.opf")).Descendants().Single(e => e.Name.LocalName == "title").Value);
        }
    }

    [Fact]
    public void An_arabic_book_page_for_the_browser_engine_reads_right_to_left()
    {
        var page = DocumentHtml.Build(BookFixtures.Fb2Arabic(_dir.Combine("print.fb2")));
        var doc = new HtmlParser().ParseDocument(page);
        Assert.Equal("rtl", doc.DocumentElement.GetAttribute("dir"));
        Assert.Contains(BookFixtures.ArabicText, doc.Body!.TextContent);
        foreach (var ext in new[] { "fb2", "mobi", "azw3", "htmlz", "txtz" }) Assert.True(DocumentHtml.Supports(ext), ext);
    }

    [EngineFact(EngineId.LibreOffice)]
    public async Task Arabic_fb2_to_docx_through_libreoffice_keeps_the_text()
    {
        var fb2 = BookFixtures.Fb2Arabic(_dir.Combine("book.fb2"));
        var converters = PdfModule.Create(TestSupport.Engines.Locator);
        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [fb2], converters: converters));
        using var docx = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(result.Outputs[0], false);
        var text = docx.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains(BookFixtures.ArabicText, text);
        Assert.Contains(BookFixtures.ArabicChapter, text);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    internal static void AssertEpubContainer(ZipArchive zip)
    {
        var first = zip.Entries[0];
        Assert.Equal("mimetype", first.FullName);
        Assert.Equal(first.Length, first.CompressedLength); // stored
        Assert.Equal("application/epub+zip", Read(zip, "mimetype"));
        var container = XDocument.Parse(Read(zip, "META-INF/container.xml"));
        var rootfile = container.Descendants().Single(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opf = XDocument.Parse(Read(zip, rootfile));
        var folder = rootfile.Contains('/') ? rootfile[..(rootfile.LastIndexOf('/') + 1)] : "";
        foreach (var item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
            Assert.True(zip.GetEntry(folder + item.Attribute("href")!.Value) is not null, $"manifest item {item.Attribute("href")!.Value} is missing");
        Assert.Contains(opf.Descendants().Where(e => e.Name.LocalName == "item"), i => i.Attribute("properties")?.Value == "nav");
        Assert.NotEmpty(opf.Descendants().Where(e => e.Name.LocalName == "itemref"));
    }

    internal static string Read(ZipArchive zip, string name, bool binary = false)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, binary ? Encoding.Latin1 : Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
