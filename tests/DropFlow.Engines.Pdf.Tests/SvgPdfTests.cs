using System.IO.Compression;
using System.Text;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Office;
using DropFlow.Engines.Pdf.Rendering;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

public class SvgPdfTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();

    private sealed class NoEngines : IEngineProvider { public string? Locate(EngineId engine) => null; }

    private sealed class FakeRenderer : IHtmlPdfRenderer
    {
        public HtmlPdfOptions? Options;
        public string? Html;

        public Task<string?> PrintAsync(string htmlPath, string outputPath, HtmlPdfOptions options, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            (Options, Html) = (options, File.ReadAllText(htmlPath));
            File.WriteAllText(outputPath, "%PDF-1.7");
            return Task.FromResult<string?>(null);
        }
    }

    private static byte[] Svg(string attributes) => Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" {attributes}><rect width=\"10\" height=\"10\"/></svg>");

    [Theory]
    [InlineData("width=\"300\" height=\"150\"", 300, 150)]
    [InlineData("width=\"300px\" height=\"72pt\"", 300, 96)]
    [InlineData("width=\"1in\" height=\"25.4mm\"", 96, 96)]
    [InlineData("viewBox=\"0 0 640 480\"", 640, 480)]
    [InlineData("width=\"200\" viewBox=\"0 0 100 50\"", 200, 100)]
    [InlineData("width=\"100%\" height=\"100%\" viewBox=\"0,0,40,30\"", 40, 30)]
    [InlineData("", 800, 600)]
    public void The_page_is_the_drawings_size(string attributes, double width, double height)
    {
        var (w, h) = SvgPdf.Size(Svg(attributes));
        Assert.Equal(width, w, 3);
        Assert.Equal(height, h, 3);
    }

    [Fact]
    public void A_broken_file_gets_the_default_page() => Assert.Equal((800d, 600d), SvgPdf.Size("<svg width="u8.ToArray()));

    [Fact]
    public void Svgz_is_unpacked_before_it_is_shown()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.svgz");
        using (var gz = new GZipStream(File.Create(path), CompressionLevel.Optimal)) gz.Write(Svg("width=\"20\" height=\"10\""));

        Assert.Equal(Svg("width=\"20\" height=\"10\""), SvgPdf.Read(path));
    }

    [Fact]
    public async Task With_a_browser_engine_svg_becomes_a_page_of_its_own_size_without_margins_and_without_scripts()
    {
        using var dir = new TempDir();
        var input = dir.WriteText("شعار.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"480\" height=\"240\"><script>alert(1)</script><text x=\"10\" y=\"40\">DropFlow</text></svg>");
        var renderer = new FakeRenderer();
        var converters = PdfModule.Create(new NoEngines(), renderer);

        var owners = converters.Where(c => c.Supports("convert:pdf", "svg")).ToList();
        var result = await Assert.Single(owners).RunAsync(new ConversionJob([input], "convert:pdf"), NoProgress, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(dir.Combine("شعار (converted).pdf"), Assert.Single(result.Outputs));
        Assert.Equal(5.0, renderer.Options!.PageWidth!.Value, 3);       // 480 px / 96
        Assert.Equal(2.5, renderer.Options.PageHeight!.Value, 3);
        Assert.True(renderer.Options.NoMargins);
        Assert.False(renderer.Options.AllowOnline);
        // The drawing is an <img> (its scripts never run); the page itself carries no script.
        Assert.Contains("<img alt=\"\" src=\"data:image/svg+xml;base64,", renderer.Html);
        Assert.DoesNotContain("<script", renderer.Html);
        Assert.Contains("@page{size:480px 240px;margin:0}", renderer.Html);
    }

    [Fact]
    public void Without_a_browser_engine_svg_keeps_the_picture_path_and_many_svgs_still_join_into_one_pdf()
    {
        var plain = PdfModule.Create(new NoEngines());
        Assert.Single(plain, c => c.Supports("convert:pdf", "svg"));

        var withBrowser = PdfModule.Create(new NoEngines(), new FakeRenderer());
        Assert.Single(withBrowser, c => c.Supports("convert:pdf", "svgz"));
        Assert.Single(withBrowser, c => c.Supports("img.to-pdf", "svg"));
        Assert.Single(withBrowser, c => c.Supports("convert:pdf", "png"));
    }
    [ChromiumFact]
    public async Task Headless_chrome_also_prints_svg_as_vectors_at_the_drawings_size()
    {
        using var dir = new TempDir();
        var input = dir.WriteText("d.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"480\" height=\"240\"><rect x=\"10\" y=\"10\" width=\"200\" height=\"100\" fill=\"#5b4fd0\"/><text x=\"20\" y=\"200\" font-family=\"Arial\" font-size=\"30\">Vector 42</text></svg>");
        var converter = PdfModule.Create(new NoEngines(), new ChromiumHtmlPdfRenderer()).Single(c => c.Supports("convert:pdf", "svg"));

        var result = await converter.RunAsync(new ConversionJob([input], "convert:pdf"), NoProgress, default);

        Assert.True(result.Success, result.Error);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(Assert.Single(result.Outputs));
        var page = Assert.Single(pdf.GetPages());
        Assert.InRange(page.Width, 358, 362);
        Assert.InRange(page.Height, 178, 182);
        Assert.Contains("Vector 42", page.Text);
        Assert.Empty(page.GetImages());
    }
}
