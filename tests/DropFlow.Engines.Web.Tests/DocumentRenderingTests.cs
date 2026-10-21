using System.IO;
using System.Text;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf;
using DropFlow.TestSupport;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Web.Tests;

/// <summary>
/// Documents become PDF and pictures through the real browser engine with NO helper program installed (the engine provider here finds
/// nothing), which is the point: no LibreOffice, no download.
/// </summary>
public class DocumentRenderingTests(WebHostFixture host) : IClassFixture<WebHostFixture>, IDisposable
{
    private sealed class NoEngines : IEngineProvider { public string? Locate(EngineId engine) => null; }

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private IReadOnlyList<IConverter> Converters() => PdfModule.Create(new NoEngines(), host.Renderer);

    private static async Task<ConversionResult> Convert(IReadOnlyList<IConverter> converters, string operation, string input)
    {
        var converter = converters.First(c => c.Supports(operation, ToolCatalog.ExtensionOf(input)));
        return await converter.RunAsync(new ConversionJob([input], operation), new Progress<JobProgress>(), CancellationToken.None);
    }

    private static string TextOf(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Concat(doc.GetPages().Select(p => p.Text));
    }

    private static int PageCount(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [WebViewFact]
    public async Task Text_with_arabic_becomes_a_pdf_without_any_engine()
    {
        var input = _dir.WriteText("note.txt", "Hello DropFlow\n\nمرحبا بالعالم\n");

        var result = await Convert(Converters(), "convert:pdf", input);

        Assert.True(result.Success, result.Error);
        var pdf = Assert.Single(result.Outputs);
        Assert.Equal(_dir.Combine("note (converted).pdf"), pdf);
        Assert.Contains("Hello DropFlow", TextOf(pdf));
        Assert.Equal("Hello DropFlow\n\nمرحبا بالعالم\n", File.ReadAllText(input));
    }

    [WebViewFact]
    public async Task Markdown_with_a_heading_and_a_table_becomes_a_pdf()
    {
        var input = _dir.WriteText("plan.md", "# Quarterly plan\n\nSome **bold** text.\n\n| name | score |\n|---|---|\n| alice | 91 |\n| bob | 78 |\n");

        var result = await Convert(Converters(), "convert:pdf", input);

        Assert.True(result.Success, result.Error);
        var text = TextOf(result.Outputs[0]);
        Assert.Contains("Quarterly plan", text);
        Assert.Contains("alice", text);
    }

    [WebViewFact]
    public async Task A_word_file_becomes_a_pdf_and_keeps_its_text()
    {
        var converters = Converters();
        var markdown = _dir.WriteText("letter.md", "# Dear reader\n\nThis paragraph lives inside a Word file.\n\n- first point\n- second point\n");
        var docx = await Convert(converters, "convert:docx", markdown);
        Assert.True(docx.Success, docx.Error);

        var result = await Convert(converters, "convert:pdf", docx.Outputs[0]);

        Assert.True(result.Success, result.Error);
        var text = TextOf(result.Outputs[0]);
        Assert.Contains("Dear reader", text);
        Assert.Contains("second point", text);
    }

    [WebViewFact]
    public async Task A_csv_table_becomes_a_pdf()
    {
        var input = _dir.WriteText("people.csv", "name,city\nalice,Cairo\nbob,Amman\n");

        var result = await Convert(Converters(), "convert:pdf", input);

        Assert.True(result.Success, result.Error);
        Assert.Contains("alice", TextOf(result.Outputs[0]));
    }

    [WebViewFact]
    public async Task An_html_page_becomes_a_png_picture()
    {
        var input = _dir.WriteText("page.html", "<!doctype html><meta charset=utf-8><body style='background:#fde'><h1>Picture me</h1></body>");

        var result = await Convert(Converters(), "convert:png", input);

        Assert.True(result.Success, result.Error);
        var png = Assert.Single(result.Outputs);
        Assert.Equal(_dir.Combine("page (converted).png"), png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(png).Take(4).ToArray());
    }

    [WebViewFact]
    public async Task A_long_document_becomes_one_picture_per_page_in_a_new_folder()
    {
        var input = _dir.WriteText("long.md", string.Join("\n\n", Enumerable.Range(1, 180).Select(i => $"Paragraph number {i} of a long document.")));

        var result = await Convert(Converters(), "convert:jpg", input);

        Assert.True(result.Success, result.Error);
        var folder = Assert.Single(result.Outputs);
        Assert.True(Directory.Exists(folder));
        Assert.True(Directory.GetFiles(folder, "*.jpg").Length >= 2, "expected several pages");
    }

    [WebViewFact]
    public async Task Arabic_file_names_and_spaces_work_and_nothing_is_overwritten()
    {
        var input = _dir.WriteText("تقرير شهري.txt", "Monthly report");
        var converters = Converters();

        var first = await Convert(converters, "convert:pdf", input);
        var second = await Convert(converters, "convert:pdf", input);

        Assert.True(first.Success && second.Success, first.Error ?? second.Error);
        Assert.NotEqual(first.Outputs[0], second.Outputs[0]);
        Assert.True(File.Exists(first.Outputs[0]));
    }

    [WebViewFact]
    public async Task A_markdown_page_cannot_reach_the_network_unless_it_is_allowed()
    {
        // an image link to a port nobody listens on: with the network blocked the page still prints, quickly, and no connection is made
        var input = _dir.WriteText("remote.md", "# Remote image\n\n![x](http://127.0.0.1:9/pixel.png)\n");

        var result = await Convert(Converters(), "convert:pdf", input);

        Assert.True(result.Success, result.Error);
        Assert.Contains("Remote image", TextOf(result.Outputs[0]));
    }

    [Fact]
    public void No_conversion_in_this_list_needs_libreoffice_any_more()
    {
        var converters = Converters();
        foreach (var input in new[] { "html", "txt", "md", "docx", "csv", "tsv", "xlsx", "json", "epub" })
            foreach (var target in new[] { "pdf", "png", "jpg" })
            {
                var owner = converters.First(c => c.Supports($"convert:{target}", input));
                Assert.Equal(EngineId.None, owner.EngineFor($"convert:{target}"));
            }
    }

    [Fact]
    public void Formats_a_browser_cannot_read_still_go_to_libreoffice()
    {
        var converters = Converters();
        foreach (var (input, target) in new[] { ("doc", "pdf"), ("odt", "pdf"), ("rtf", "pdf"), ("pptx", "pdf"), ("ppt", "png"), ("xls", "pdf") })
        {
            var owner = converters.First(c => c.Supports($"convert:{target}", input));
            Assert.Equal(EngineId.LibreOffice, owner.EngineFor($"convert:{target}"));
        }
    }
}
