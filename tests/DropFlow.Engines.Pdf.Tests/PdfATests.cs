using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// <c>convert:pdfa</c> (PDF/A-2b by rebuilding the pages as pictures, in-process). These tests need no validator: they look at the
/// produced bytes with PdfPig, PDFium and a raw scan. The veraPDF test below runs the real validator when DROPFLOW_VERAPDF points to
/// <c>verapdf.bat</c> and is skipped (visibly) otherwise.
/// </summary>
public class PdfATests
{
    private static string Source(TempDir dir, string name = "report.pdf", int pages = 2)
    {
        var b = new PdfBuilder().Title("Quarterly report").Meta("Ada Lovelace", "Numbers & <figures>", "q1, q2");
        for (var i = 1; i <= pages; i++)
        {
            var page = b.AddPage(i == 2 ? 595.276 : 612, i == 2 ? 841.89 : 792);
            page.Text($"Hello PDF/A page {i}", 72, 100, 24);
            page.Arabic("مرحبا بالعالم", 540, 160);
            page.FillRect(72, 200, 200, 80, 200, 30, 30);
        }
        return b.Save(dir.Combine(name));
    }

    private static string Raw(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Produces_a_new_file_beside_the_original_and_says_text_is_not_selectable()
    {
        using var dir = new TempDir();
        var pdf = Source(dir);
        var before = Sha(pdf);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [pdf]));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("report (PDFA).pdf"), output);
        Assert.Equal(before, Sha(pdf)); // the original is never touched
        Assert.Contains("PDF/A-2b", result.Note);
        Assert.Contains("text is not selectable", result.Note);
        Assert.Contains("converted to images", result.Note);
    }

    [Fact]
    public async Task Pages_keep_their_count_and_size_and_are_one_picture_without_text()
    {
        using var dir = new TempDir();
        var pdf = Source(dir, pages: 2);

        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [pdf]));
        var output = result.Outputs[0];

        Assert.Equal(2, Kit.PageCount(output));
        Assert.Equal(PdfProbe.PdfiumSize(pdf, 1), PdfProbe.PdfiumSize(output, 1));
        Assert.Equal(PdfProbe.PdfiumSize(pdf, 2), PdfProbe.PdfiumSize(output, 2));
        using var doc = Pig.Open(output);
        foreach (var page in doc.GetPages())
        {
            Assert.Equal("", page.Text.Trim());
            Assert.Single(page.GetImages());
        }

        // The pictures show what the pages showed: the red block exists on the rebuilt page.
        using var rendered = PdfiumDocument.Open(output);
        var pixels = rendered.Render(0, 72);
        var x = 150; var y = 240; // inside the red rectangle (points = pixels at 72 DPI)
        var i = (y * pixels.Width + x) * 4;
        Assert.True(pixels.Bgra[i + 2] > 150 && pixels.Bgra[i + 1] < 100 && pixels.Bgra[i] < 100, "the red block is missing");
    }

    [Fact]
    public async Task Declares_pdfa_2b_in_xmp_and_keeps_the_title_author_subject_and_keywords()
    {
        using var dir = new TempDir();
        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [Source(dir)]));

        using var doc = Pig.Open(result.Outputs[0]);
        Assert.True(doc.TryGetXmpMetadata(out var xmp));
        var xml = xmp!.GetXDocument()!; // parses, so the "&" and "<" of the subject were escaped
        string Value(string local) => xml.Descendants().First(e => e.Name.LocalName == local).Value.Trim();
        var part = xml.Descendants().Single(e => e.Name.LocalName == "part");
        Assert.Equal("http://www.aiim.org/pdfa/ns/id/", part.Name.NamespaceName);
        Assert.Equal("2", part.Value);
        Assert.Equal("B", Value("conformance"));
        Assert.Equal("Quarterly report", Value("title"));
        Assert.Equal("Ada Lovelace", Value("creator"));
        Assert.Equal("Numbers & <figures>", Value("description"));
        Assert.Equal("q1, q2", Value("Keywords"));

        Assert.Equal("Quarterly report", doc.Information.Title);
        Assert.Equal("Ada Lovelace", doc.Information.Author);
    }

    [Fact]
    public async Task Has_an_output_intent_with_a_real_srgb_icc_profile()
    {
        using var dir = new TempDir();
        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [Source(dir)]));
        var raw = Raw(result.Outputs[0]);

        Assert.Contains("/OutputIntents", raw);
        Assert.Contains("/GTS_PDFA1", raw);
        Assert.Contains("/DestOutputProfile", raw);

        using var doc = PdfSharpSupport.OpenForModify(result.Outputs[0]);
        var intents = doc.Internals.Catalog.Elements.GetArray("/OutputIntents")!;
        var intent = intents.Elements[0] as PdfSharp.Pdf.PdfDictionary ?? (PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)intents.Elements[0]).Value;
        var profile = intent.Elements.GetDictionary("/DestOutputProfile")!.Stream!.UnfilteredValue;
        Assert.Equal("acsp", Encoding.ASCII.GetString(profile, 36, 4)); // ICC signature
        Assert.Equal("RGB ", Encoding.ASCII.GetString(profile, 16, 4));  // colour space
        Assert.Equal("mntr", Encoding.ASCII.GetString(profile, 12, 4));  // monitor profile
        Assert.Equal(3, intent.Elements.GetDictionary("/DestOutputProfile")!.Elements.GetInteger("/N"));
    }

    [Fact]
    public async Task Contains_nothing_PDFA_forbids()
    {
        using var dir = new TempDir();
        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [Source(dir)]));
        var raw = Raw(result.Outputs[0]);

        Assert.StartsWith("%PDF-1.7", raw);
        Assert.Contains("/ID", raw);
        foreach (var forbidden in new[] { "/Encrypt", "/JavaScript", "/JS ", "/OpenAction", "/Launch", "/AcroForm", "/Annots", "/URI", "/SMask", "/Font", "/DeviceGray", "/DeviceCMYK", "/EmbeddedFile" })
            Assert.DoesNotContain(forbidden, raw);
        Assert.Contains("/DeviceRGB", raw);
    }

    [Fact]
    public async Task Black_and_white_pages_are_still_rgb_pictures()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage().Text("Only black text", 72, 100);
        var pdf = b.Save(dir.Combine("bw.pdf"));

        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [pdf]));

        var raw = Raw(result.Outputs[0]);
        Assert.Contains("/DeviceRGB", raw);
        Assert.DoesNotContain("/DeviceGray", raw);
    }

    [Fact]
    public async Task A_password_protected_file_is_refused_with_a_clear_message_and_nothing_is_written()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Encrypt("user-secret");
        b.AddPage().Text("Secret", 72, 100);
        var locked = b.Save(dir.Combine("locked.pdf"));

        var result = await Kit.RunAsync("convert:pdfa", [locked]);

        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["locked.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_file_whose_owner_forbids_copying_is_refused()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder().Encrypt("", "boss-password", allowAssemble: true, allowExtract: false);
        b.AddPage().Text("Restricted", 72, 100);
        var restricted = b.Save(dir.Combine("owner.pdf"));

        var result = await Kit.RunAsync("convert:pdfa", [restricted]);

        Assert.False(result.Success);
        Assert.Contains("Unlock", result.Error);
        Assert.Equal(["owner.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_damaged_file_is_refused_with_a_clear_message()
    {
        using var dir = new TempDir();
        var junk = dir.WriteText("broken.pdf", "this is not a pdf at all");

        var result = await Kit.RunAsync("convert:pdfa", [junk]);

        Assert.False(result.Success);
        Assert.Contains("not a valid PDF", result.Error);
        Assert.Equal(["broken.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Never_overwrites_an_earlier_result()
    {
        using var dir = new TempDir();
        var pdf = Source(dir, pages: 1);

        var first = await Kit.Ok(Kit.RunAsync("convert:pdfa", [pdf]));
        var firstHash = Sha(first.Outputs[0]);
        var second = await Kit.Ok(Kit.RunAsync("convert:pdfa", [pdf]));

        Assert.NotEqual(first.Outputs[0], second.Outputs[0]);
        Assert.True(File.Exists(first.Outputs[0]));
        Assert.Equal(firstHash, Sha(first.Outputs[0]));
    }

    [Fact]
    public async Task Works_with_an_arabic_file_name_and_an_output_directory()
    {
        using var dir = new TempDir();
        var pdf = Source(dir, "تقرير الربع الأول.pdf", pages: 1);
        var outDir = Path.Combine(dir.Path, "out folder");

        var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [pdf], outputDirectory: outDir));

        Assert.Equal(Path.Combine(outDir, "تقرير الربع الأول (PDFA).pdf"), result.Outputs[0]);
        Assert.Equal(1, Kit.PageCount(result.Outputs[0]));
    }

    [Fact]
    public async Task Cancellation_leaves_no_partial_file()
    {
        using var dir = new TempDir();
        var pdf = Source(dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:pdfa", [pdf], ct: cts.Token));

        Assert.Equal(["report.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public void Supports_only_pdf_input_needs_no_engine_and_asks_no_options()
    {
        var converter = Kit.Converter("convert:pdfa", "pdf");
        Assert.True(converter.Supports("convert:pdfa", "PDF"));
        Assert.False(converter.Supports("convert:pdfa", "png"));
        Assert.Equal(EngineId.None, converter.EngineFor("convert:pdfa"));
        Assert.Empty(converter.OptionsFor("convert:pdfa"));
    }

    [Fact]
    public void The_self_check_catches_a_file_that_is_not_pdfa()
    {
        using var dir = new TempDir();
        var plain = Source(dir, pages: 1); // an ordinary PDF: no XMP, no output intent, no file id marker for PDF/A

        var problems = Tools.PdfATools.SelfCheck(plain, 1);

        Assert.Contains(problems, p => p.StartsWith("XMP does not declare PDF/A-2b"));
        Assert.Contains("no output intent profile", problems);
    }

    /// <summary>Runs veraPDF (PDF/A-2b profile) over the result when DROPFLOW_VERAPDF is set; otherwise skipped, visibly.</summary>
    [VeraPdfFact]
    public async Task Passes_the_veraPDF_PDFA_2b_profile()
    {
        using var dir = new TempDir();
        var inputs = new List<string> { Source(dir, "two pages.pdf", pages: 2) };
        var one = new PdfBuilder();
        one.AddPage(300, 400, rotate: 90).Text("Rotated", 40, 60, 20);
        inputs.Add(one.Save(dir.Combine("rotated.pdf")));
        inputs.Add(Samples.Pdf(dir.Combine("simple.pdf"), 3));

        // Arabic title/author (UTF-16 in Info, UTF-8 in XMP), a picture, a very wide page and a metadata value full of XML characters.
        var rich = new PdfBuilder().Title("تقرير الربع الأول").Meta("مصطفى", "R&D <draft> \"quoted\" (parens) \\ slash", "ا, ب");
        rich.AddPage(1190, 842).Image(Samples.Png(dir.Combine("pic.png"), 120, 80), 100, 100, 360, 240);
        rich.AddPage().Arabic("نص عربي", 500, 100);
        inputs.Add(rich.Save(dir.Combine("rich.pdf")));

        foreach (var input in inputs)
        {
            var result = await Kit.Ok(Kit.RunAsync("convert:pdfa", [input]));
            var (passed, report) = VeraPdfFactAttribute.Validate(result.Outputs[0]);
            Assert.True(passed, report);
        }
    }
}

/// <summary>A fact that runs only when the environment variable DROPFLOW_VERAPDF names an existing verapdf.bat (or verapdf).</summary>
public sealed class VeraPdfFactAttribute : FactAttribute
{
    public const string Variable = "DROPFLOW_VERAPDF";

    public VeraPdfFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Skip = $"veraPDF not found: set {Variable} to the path of verapdf.bat to run the PDF/A validation test (https://verapdf.org/software/).";
    }

    public static (bool Passed, string Report) Validate(string pdf)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable(Variable)!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "--flavour", "2b", "--format", "text", "--verbose", pdf }) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(true); return (false, "veraPDF did not finish in 120 seconds"); }
        var report = output.Result + error.Result;
        return (process.ExitCode == 0 && report.TrimStart().StartsWith("PASS", StringComparison.Ordinal), report);
    }
}
