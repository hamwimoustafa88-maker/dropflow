using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Books;
using DropFlow.TestSupport;
using ImageMagick;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>A test that needs WinRAR's command-line Rar.exe to make a real CBR; skipped, visibly, without it.</summary>
public sealed class RarFactAttribute : FactAttribute
{
    public static readonly string RarExe = @"C:\Program Files\WinRAR\Rar.exe";

    public RarFactAttribute()
    {
        if (!File.Exists(RarExe)) Skip = "WinRAR's Rar.exe is not installed (it is needed to create a real CBR file).";
    }
}

/// <summary>Comic books (cbz/cbr/cb7) to PDF, EPUB and CBZ, and PDF to CBZ; outputs read back with PdfPig, zip, XML and Magick.NET.</summary>
public sealed class ComicConversionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("page2.png", "page10.png", -1)]
    [InlineData("Page3.png", "page2.png", 1)]
    [InlineData("ch1/p9.jpg", "ch2/p1.jpg", -1)]
    [InlineData("001.jpg", "1.jpg", 1)]
    [InlineData("a", "a", 0)]
    public void Pages_are_sorted_in_natural_order(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ComicTools.NaturalCompare(a, b)));

    [Fact]
    public async Task Cbz_to_pdf_has_one_page_per_picture_in_natural_order()
    {
        var cbz = BookFixtures.Cbz(_dir.Combine("قصة مصورة.cbz"));
        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [cbz]));
        Assert.Equal(_dir.Combine("قصة مصورة (converted).pdf"), result.Outputs[0]);
        using var pdf = PdfDocument.Open(result.Outputs[0]);
        // Page n is 10 * n pixels wide (72 dpi -> the same number of points).
        Assert.Equal([10, 20, 30, 100], pdf.GetPages().Select(p => (int)Math.Round(p.Width)));
        Assert.Equal("Test Series 7", pdf.Information.Title);
        Assert.DoesNotContain("/R2L", File.ReadAllText(result.Outputs[0], Encoding.Latin1));
    }

    [Fact]
    public async Task A_manga_reads_right_to_left_in_pdf_and_epub()
    {
        var cbz = BookFixtures.Cbz(_dir.Combine("manga.cbz"), manga: true);
        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [cbz]))).Outputs[0];
        Assert.Contains("/R2L", File.ReadAllText(pdf, Encoding.Latin1));

        var epub = (await Kit.Ok(Kit.RunAsync("convert:epub", [cbz]))).Outputs[0];
        using var zip = ZipFile.OpenRead(epub);
        var opf = XDocument.Parse(BookConversionTests.Read(zip, "OEBPS/content.opf"));
        Assert.Equal("rtl", opf.Descendants().Single(e => e.Name.LocalName == "spine").Attribute("page-progression-direction")!.Value);
    }

    [Fact]
    public async Task Cbz_to_epub_is_fixed_layout_with_the_pages_in_order()
    {
        var cbz = BookFixtures.Cbz(_dir.Combine("comic.cbz"));
        var epub = (await Kit.Ok(Kit.RunAsync("convert:epub", [cbz]))).Outputs[0];
        using var zip = ZipFile.OpenRead(epub);
        BookConversionTests.AssertEpubContainer(zip);
        var opf = XDocument.Parse(BookConversionTests.Read(zip, "OEBPS/content.opf"));
        Assert.Contains(opf.Descendants().Where(e => e.Name.LocalName == "meta"), m => m.Attribute("property")?.Value == "rendition:layout" && m.Value == "pre-paginated");
        Assert.Equal("ltr", opf.Descendants().Single(e => e.Name.LocalName == "spine").Attribute("page-progression-direction")!.Value);
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(i => i.Attribute("idref")!.Value).ToList();
        Assert.Equal(4, spine.Count);
        var widths = new List<int>();
        foreach (var id in spine)
        {
            var href = opf.Descendants().Single(e => e.Name.LocalName == "item" && e.Attribute("id")!.Value == id).Attribute("href")!.Value;
            var page = XDocument.Parse(BookConversionTests.Read(zip, "OEBPS/" + href));
            var img = page.Descendants().Single(e => e.Name.LocalName == "img");
            using var stream = zip.GetEntry("OEBPS/" + img.Attribute("src")!.Value)!.Open();
            using var picture = new MagickImage(stream);
            widths.Add((int)picture.Width);
            Assert.Equal(picture.Width.ToString(), img.Attribute("width")!.Value);
            Assert.Contains(page.Descendants().Where(e => e.Name.LocalName == "meta"), m => m.Attribute("name")?.Value == "viewport");
        }
        Assert.Equal([10, 20, 30, 100], widths);
    }

    [Fact]
    public async Task Pdf_to_cbz_draws_every_page_as_a_numbered_jpeg()
    {
        var pdf = Samples.Pdf(_dir.Combine("doc.pdf"), pages: 3);
        var cbz = (await Kit.Ok(Kit.RunAsync("convert:cbz", [pdf]))).Outputs[0];
        Assert.Equal(_dir.Combine("doc (converted).cbz"), cbz);
        using var zip = ZipFile.OpenRead(cbz);
        Assert.Equal(["001.jpg", "002.jpg", "003.jpg", "ComicInfo.xml"], zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        using (var stream = zip.GetEntry("001.jpg")!.Open())
        using (var page = new MagickImage(stream))
        {
            Assert.Equal(MagickFormat.Jpeg, page.Format);
            Assert.InRange((int)page.Width, 1650, 1750); // 612 pt at 200 dpi
        }
        Assert.Equal("3", XDocument.Parse(BookConversionTests.Read(zip, "ComicInfo.xml")).Root!.Element("PageCount")!.Value);

        // ...and back to a PDF with three pages.
        var back = (await Kit.Ok(Kit.RunAsync("convert:pdf", [cbz]))).Outputs[0];
        Assert.Equal(3, Kit.PageCount(back));
    }

    [RarFact]
    public async Task A_real_cbr_made_by_winrar_converts_to_cbz_and_pdf()
    {
        var folder = BookFixtures.ComicFolder(_dir.Combine("src"));
        var cbr = _dir.Combine("comic.cbr");
        Run(RarFactAttribute.RarExe, ["a", "-r", "-ep1", "-m5", "-idq", cbr, Path.Combine(folder, "*")], folder);
        Assert.True(File.Exists(cbr));

        var cbz = (await Kit.Ok(Kit.RunAsync("convert:cbz", [cbr]))).Outputs[0];
        using (var zip = ZipFile.OpenRead(cbz))
        {
            var pages = zip.Entries.Where(e => e.FullName.EndsWith(".png")).OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
            Assert.Equal(["001.png", "002.png", "003.png", "004.png"], pages.Select(p => p.FullName));
            var widths = pages.Select(p => { using var s = p.Open(); using var img = new MagickImage(s); return (int)img.Width; });
            Assert.Equal([10, 20, 30, 100], widths);
        }
        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [cbr]))).Outputs[0];
        Assert.Equal(4, Kit.PageCount(pdf));
    }

    [RarFact]
    public async Task A_password_protected_cbr_is_refused()
    {
        var folder = BookFixtures.ComicFolder(_dir.Combine("src"));
        var cbr = _dir.Combine("locked.cbr");
        Run(RarFactAttribute.RarExe, ["a", "-r", "-ep1", "-hpsecret", "-idq", cbr, Path.Combine(folder, "*")], folder);
        var result = await Kit.RunAsync("convert:pdf", [cbr]);
        Assert.False(result.Success);
        Assert.Contains("locked.cbr", result.Error);
    }

    [EngineFact(EngineId.SevenZip)]
    public async Task A_real_cb7_made_by_7zip_converts_to_pdf_in_order()
    {
        var folder = BookFixtures.ComicFolder(_dir.Combine("src"));
        var cb7 = _dir.Combine("comic.cb7");
        var sevenZip = TestSupport.Engines.Locator.Locate(EngineId.SevenZip)!;
        Run(sevenZip, ["a", "-t7z", "-ms=on", cb7, Path.Combine(folder, "pages")], folder);
        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [cb7]))).Outputs[0];
        using var document = PdfDocument.Open(pdf);
        Assert.Equal([10, 20, 30, 100], document.GetPages().Select(p => (int)Math.Round(p.Width)));
    }

    [Fact]
    public async Task A_cbr_that_is_really_a_zip_is_read_by_its_content()
    {
        var cbr = BookFixtures.Cbz(_dir.Combine("renamed.cbr"));
        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [cbr]))).Outputs[0];
        Assert.Equal(4, Kit.PageCount(pdf));
    }

    [Fact]
    public async Task An_archive_without_pictures_or_a_broken_file_fails_by_name()
    {
        var empty = _dir.Combine("empty.cbz");
        using (var zip = new ZipArchive(File.Create(empty), ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        var result = await Kit.RunAsync("convert:pdf", [empty]);
        Assert.False(result.Success);
        Assert.Contains("no pictures", result.Error);

        var broken = _dir.WriteText("broken.cb7", "not an archive");
        result = await Kit.RunAsync("convert:epub", [broken]);
        Assert.False(result.Success);
        Assert.Contains("broken.cb7", result.Error);
        Assert.Equal(["broken.cb7", "empty.cbz"], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task Cancellation_leaves_nothing_behind()
    {
        var cbz = BookFixtures.Cbz(_dir.Combine("comic.cbz"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:pdf", [cbz], ct: cts.Token));
        Assert.Equal(["comic.cbz"], Kit.FilesIn(_dir.Path));
    }

    private static void Run(string exe, string[] arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(exe)} exited with {process.ExitCode}");
    }
}
