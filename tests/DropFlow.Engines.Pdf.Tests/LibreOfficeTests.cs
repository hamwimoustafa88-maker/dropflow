using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Office;
using DropFlow.TestSupport;
using static DropFlow.Engines.Pdf.Tests.DocxFixture;

namespace DropFlow.Engines.Pdf.Tests;

public class LibreOfficePlannerTests
{
    [Theory]
    // writer family
    [InlineData("doc", "docx", LoPre.None, null, "docx", "MS Word 2007 XML", LoPost.None)]
    [InlineData("odt", "pdf", LoPre.None, null, "pdf", "writer_pdf_Export", LoPost.None)]
    [InlineData("rtf", "odt", LoPre.None, null, "odt", "writer8", LoPost.None)]
    [InlineData("docx", "rtf", LoPre.None, null, "rtf", "Rich Text Format", LoPost.None)]
    [InlineData("doc", "txt", LoPre.None, null, "txt", "Text (encoded):UTF8", LoPost.None)]
    [InlineData("pages", "html", LoPre.None, null, "html", "HTML (StarWriter)", LoPost.None)]
    [InlineData("docx", "epub", LoPre.None, null, "epub", "EPUB", LoPost.None)]
    [InlineData("doc", "md", LoPre.None, null, "docx", "MS Word 2007 XML", LoPost.DocxToMarkdown)]
    [InlineData("odt", "png", LoPre.None, null, "pdf", "writer_pdf_Export", LoPost.PageImages)]
    [InlineData("rtf", "jpg", LoPre.None, null, "pdf", "writer_pdf_Export", LoPost.PageImages)]
    // inputs LibreOffice cannot open directly
    [InlineData("txt", "pdf", LoPre.TextToUtf8, null, "pdf", "writer_pdf_Export", LoPost.None)]
    [InlineData("html", "docx", LoPre.None, "HTML (StarWriter)", "docx", "MS Word 2007 XML", LoPost.None)]
    [InlineData("md", "pdf", LoPre.MarkdownToHtml, "HTML (StarWriter)", "pdf", "writer_pdf_Export", LoPost.None)]
    [InlineData("md", "epub", LoPre.MarkdownToHtml, "HTML (StarWriter)", "epub", "EPUB", LoPost.None)]
    [InlineData("epub", "pdf", LoPre.EpubToHtml, "HTML (StarWriter)", "pdf", "writer_pdf_Export", LoPost.None)]
    [InlineData("epub", "docx", LoPre.EpubToHtml, "HTML (StarWriter)", "docx", "MS Word 2007 XML", LoPost.None)]
    // slides
    [InlineData("pptx", "pdf", LoPre.None, null, "pdf", "impress_pdf_Export", LoPost.None)]
    [InlineData("ppt", "pptx", LoPre.None, null, "pptx", "Impress MS PowerPoint 2007 XML", LoPost.None)]
    [InlineData("key", "odp", LoPre.None, null, "odp", "impress8", LoPost.None)]
    [InlineData("odp", "jpg", LoPre.None, null, "pdf", "impress_pdf_Export", LoPost.PageImages)]
    // spreadsheets
    [InlineData("xlsx", "ods", LoPre.None, null, "ods", "calc8", LoPost.None)]
    [InlineData("xls", "xlsx", LoPre.None, null, "xlsx", "Calc MS Excel 2007 XML", LoPost.None)]
    [InlineData("numbers", "pdf", LoPre.None, null, "pdf", "calc_pdf_Export", LoPost.None)]
    [InlineData("xls", "csv", LoPre.None, null, "xlsx", "Calc MS Excel 2007 XML", LoPost.Tabular)]
    [InlineData("ods", "json", LoPre.None, null, "xlsx", "Calc MS Excel 2007 XML", LoPost.Tabular)]
    [InlineData("csv", "pdf", LoPre.TabularToXlsx, null, "pdf", "calc_pdf_Export", LoPost.None)]
    [InlineData("tsv", "ods", LoPre.TabularToXlsx, null, "ods", "calc8", LoPost.None)]
    [InlineData("json", "ods", LoPre.TabularToXlsx, null, "ods", "calc8", LoPost.None)]
    public void Plans_use_the_right_filters_and_steps(string input, string target, LoPre pre, string? inFilter, string produced, string? filter, LoPost post)
    {
        var plan = LibreOfficePlanner.Plan(input, target);

        Assert.NotNull(plan);
        Assert.Equal((pre, inFilter, produced, filter, post, target), (plan.Pre, plan.InputFilter, plan.ProducedExtension, plan.ExportFilter, plan.Post, plan.Target));
    }

    [Theory]
    [InlineData("docx", "docx")]      // never a format to itself
    [InlineData("docx", "txt")]       // done in-process
    [InlineData("docx", "md")]
    [InlineData("docx", "html")]
    [InlineData("md", "html")]
    [InlineData("html", "md")]
    [InlineData("txt", "docx")]
    [InlineData("epub", "html")]
    [InlineData("epub", "md")]
    [InlineData("epub", "txt")]
    [InlineData("csv", "xlsx")]       // done in-process
    [InlineData("xlsx", "csv")]
    [InlineData("json", "html")]
    [InlineData("xlsx", "xls")]       // not an offered output
    [InlineData("pptx", "docx")]      // wrong family
    [InlineData("pdf", "docx")]       // PDF is not a LibreOffice input here
    [InlineData("png", "pdf")]
    [InlineData("xlsx", "odp")]
    public void Pairs_that_are_managed_or_impossible_have_no_libreoffice_plan(string input, string target)
    {
        Assert.Null(LibreOfficePlanner.Plan(input, target));
    }

    [Fact]
    public void Every_catalog_conversion_in_this_modules_categories_is_supported_except_the_named_gaps()
    {
        var catalog = DropFlow.Core.Catalog.ToolCatalog.LoadEmbedded();
        var module = Kit.Converters;
        var unsupported = new List<string>();

        foreach (var id in new[] { "pdf", "document", "slides", "spreadsheet", "epub", "image" })
        {
            var category = catalog.Category(id)!;
            foreach (var input in category.Inputs)
            {
                foreach (var output in category.Outputs.Where(o => o != input))
                {
                    if (id == "image" && output != "pdf") continue; // other image outputs belong to the image module
                    if (!module.Any(c => c.Supports($"convert:{output}", input))) unsupported.Add($"{input}->{output}");
                }
            }
        }

        // pdfa is out of scope (no PDF/A engine); heic cannot be verified without a fixture. Anything else appearing here is a regression.
        Assert.Equal(["heic->pdf", "pdf->pdfa"], unsupported.OrderBy(u => u, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_listed_pair_is_unique_and_never_overlaps_the_managed_pairs()
    {
        var pairs = LibreOfficePlanner.Pairs;
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        Assert.All(pairs, p => Assert.False(ManagedPairs.Handles(p.Input, p.Target), $"{p.Input}->{p.Target} is managed"));
    }

    [Fact]
    public void Libreoffice_converters_declare_the_engine_and_managed_ones_do_not()
    {
        Assert.Equal(EngineId.LibreOffice, Kit.Converter("convert:docx", "doc").EngineFor("convert:docx"));
        Assert.Equal(EngineId.LibreOffice, Kit.Converter("convert:pdf", "docx").EngineFor("convert:pdf"));
        Assert.Equal(EngineId.LibreOffice, Kit.Converter("convert:jpg", "pptx").EngineFor("convert:jpg"));
        Assert.Equal(EngineId.LibreOffice, Kit.Converter("convert:pdf", "epub").EngineFor("convert:pdf"));

        Assert.Equal(EngineId.None, Kit.Converter("convert:docx", "pdf").EngineFor("convert:docx"));
        Assert.Equal(EngineId.None, Kit.Converter("convert:docx", "txt").EngineFor("convert:docx"));
        Assert.Equal(EngineId.None, Kit.Converter("convert:txt", "docx").EngineFor("convert:txt"));
        Assert.Equal(EngineId.None, Kit.Converter("convert:pdf", "png").EngineFor("convert:pdf"));
        Assert.Equal(EngineId.None, Kit.Converter("convert:html", "md").EngineFor("convert:html"));
        Assert.Equal(EngineId.None, Kit.Converter("convert:csv", "xlsx").EngineFor("convert:csv"));
        Assert.Equal(EngineId.None, Kit.Converter("convert:md", "epub").EngineFor("convert:md"));
    }
}

public class LibreOfficeArgumentTests
{
    [Fact]
    public void A_conversion_command_line_has_a_private_profile_filter_and_outdir()
    {
        var plan = LibreOfficePlanner.Plan("doc", "docx")!;

        var args = LibreOfficeArguments.Build(@"C:\work\in\input.doc", @"C:\work\out", @"C:\work\profile", plan);

        Assert.Equal(
        [
            "--headless", "--invisible", "--nologo", "--norestore", "--nolockcheck", "--nodefault",
            "-env:UserInstallation=file:///C:/work/profile",
            "--convert-to", "docx:MS Word 2007 XML",
            "--outdir", @"C:\work\out",
            @"C:\work\in\input.doc",
        ], args);
    }

    [Fact]
    public void An_import_filter_is_passed_as_one_argument_before_convert_to()
    {
        var plan = LibreOfficePlanner.Plan("html", "pdf")!;

        var args = LibreOfficeArguments.Build(@"C:\w\in\input.html", @"C:\w\out", @"C:\w\profile", plan);

        Assert.Equal("--infilter=HTML (StarWriter)", args.Single(a => a.StartsWith("--infilter", StringComparison.Ordinal)));
        Assert.True(args.ToList().IndexOf("--infilter=HTML (StarWriter)") < args.ToList().IndexOf("--convert-to"));
    }

    [Fact]
    public void The_profile_url_handles_spaces_and_arabic_folder_names()
    {
        var url = LibreOfficeArguments.ProfileUrl(@"C:\Users\مستخدم\my files\profile");

        Assert.StartsWith("file:///C:/Users/", url);
        Assert.Contains("my%20files", url);
        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("\\", url);
        Assert.Equal(@"C:\Users\مستخدم\my files\profile", new Uri(url).LocalPath);
    }

    [Fact]
    public void No_filter_means_just_the_extension()
    {
        var plan = new LibreOfficePlan(LoPre.None, null, "pdf", null, LoPost.None, "pdf");
        var args = LibreOfficeArguments.Build("in.x", "out", "prof", plan);
        Assert.Equal("pdf", args[args.ToList().IndexOf("--convert-to") + 1]);
    }
}

public class LibreOfficeConversionTests
{
    private static IReadOnlyList<IConverter> Module(FakeSoffice soffice) => FakeRunner.Module(soffice.Runner, EngineId.LibreOffice);

    [Fact]
    public async Task A_word_document_is_converted_and_published_beside_the_original()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("letter.doc", "OLE bytes pretend");
        var soffice = new FakeSoffice();

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [doc], converters: Module(soffice)));

        Assert.Equal(dir.Combine("letter (converted).docx"), result.Outputs[0]);
        Assert.StartsWith("# Converted heading", DocumentToolsHelper.DocxToMarkdown(result.Outputs[0]));
        Assert.Equal("docx:MS Word 2007 XML", soffice.ConvertTo(0));
        Assert.Equal(["letter (converted).docx", "letter.doc"], Kit.FilesIn(dir.Path));
        Assert.Equal("OLE bytes pretend", File.ReadAllText(doc)); // the original is untouched
        Assert.Equal("input.doc", Path.GetFileName(soffice.InputFile(0))); // LibreOffice sees a private ASCII-named copy
    }

    [Fact]
    public async Task Every_conversion_uses_its_own_profile_folder_that_is_removed_afterwards()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("a.doc", "x");
        var soffice = new FakeSoffice();

        await Kit.Ok(Kit.RunAsync("convert:docx", [doc], converters: Module(soffice)));
        await Kit.Ok(Kit.RunAsync("convert:docx", [doc], converters: Module(soffice)));

        Assert.Equal(2, soffice.ProfileDirectories.Distinct().Count());
        var profile = new Uri(soffice.ProfileDirectories[0]["-env:UserInstallation=".Length..]).LocalPath;
        Assert.False(Directory.Exists(profile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(profile)!)); // the whole scratch folder is gone
    }

    [Fact]
    public async Task Markdown_is_rendered_to_html_before_libreoffice_sees_it()
    {
        using var dir = new TempDir();
        var md = dir.WriteText("note.md", "# Title\n\nSome **bold** text\n");
        var soffice = new FakeSoffice();

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [md], converters: Module(soffice)));

        Assert.Equal(dir.Combine("note (converted).pdf"), result.Outputs[0]);
        Assert.Equal("input.html", Path.GetFileName(soffice.InputFile(0)));
        var html = Encoding.UTF8.GetString(soffice.InputSnapshots[0]["input.html"]);
        Assert.Contains("<h1 id=\"title\">Title</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("--infilter=HTML (StarWriter)", soffice.Runner.Calls[0].Arguments);
        Assert.Equal("pdf:writer_pdf_Export", soffice.ConvertTo(0));
    }

    [Fact]
    public async Task Plain_text_is_handed_over_as_utf8_with_a_bom_whatever_its_original_encoding()
    {
        using var dir = new TempDir();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var txt = dir.Combine("old.txt");
        File.WriteAllBytes(txt, Encoding.GetEncoding(1256).GetBytes("مرحبا بالعالم"));
        var soffice = new FakeSoffice();

        await Kit.Ok(Kit.RunAsync("convert:pdf", [txt], converters: Module(soffice)));

        var given = soffice.InputSnapshots[0]["input.txt"];
        Assert.Equal([0xEF, 0xBB, 0xBF], given.Take(3));
        Assert.Equal("مرحبا بالعالم", Encoding.UTF8.GetString(given, 3, given.Length - 3));
    }

    [Fact]
    public async Task Csv_is_turned_into_a_workbook_before_libreoffice_makes_an_ods()
    {
        using var dir = new TempDir();
        var csv = dir.WriteText("data.csv", "name;qty\nأحمد;3\n");
        var soffice = new FakeSoffice();

        var result = await Kit.Ok(Kit.RunAsync("convert:ods", [csv], converters: Module(soffice)));

        Assert.Equal(dir.Combine("data (converted).ods"), result.Outputs[0]);
        Assert.Equal("input.xlsx", Path.GetFileName(soffice.InputFile(0)));
        var xlsx = dir.Combine("seen.xlsx");
        File.WriteAllBytes(xlsx, soffice.InputSnapshots[0]["input.xlsx"]);
        var cells = XlsxProbe.Read(xlsx, out _).Single().Value;
        Assert.Equal("أحمد", cells["A2"].Text);
        Assert.Equal(new XlsxProbe.Cell("3", true), cells["B2"]);
        Assert.Equal("ods:calc8", soffice.ConvertTo(0));
    }

    [Fact]
    public async Task An_epub_is_joined_into_html_with_its_pictures_before_libreoffice_makes_a_pdf()
    {
        using var dir = new TempDir();
        var epub = EpubFixture.Write(dir.Combine("book.epub"));
        var soffice = new FakeSoffice();

        var result = await Kit.Ok(Kit.RunAsync("convert:pdf", [epub], converters: Module(soffice)));

        Assert.Equal(dir.Combine("book (converted).pdf"), result.Outputs[0]);
        var snapshot = soffice.InputSnapshots[0];
        var html = Encoding.UTF8.GetString(snapshot["input.html"]);
        Assert.Contains("Chapter One", html);
        Assert.Contains("Chapter Two", html);
        Assert.DoesNotContain("<script", html);
        Assert.Single(snapshot.Keys, k => k.StartsWith("images/", StringComparison.Ordinal)); // the cover picture was unpacked beside the html
        Assert.Contains("src=\"images/image1.png\"", html);
    }

    [Fact]
    public async Task A_document_to_png_draws_every_page_of_the_pdf_libreoffice_made()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("report.doc", "x");
        var soffice = new FakeSoffice(FakeSoffice.ThreePagePdf);

        var result = await Kit.Ok(Kit.RunAsync("convert:png", [doc], converters: Module(soffice)));

        var folder = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("report (converted)"), folder);
        Assert.Equal(["report - 1.png", "report - 2.png", "report - 3.png"], Kit.FilesIn(folder));
        Assert.Equal("png", PixelKit.Signature(Path.Combine(folder, "report - 2.png")));
        Assert.Equal("pdf:writer_pdf_Export", soffice.ConvertTo(0));
    }

    [Fact]
    public async Task A_one_page_slide_deck_to_jpg_is_a_single_file()
    {
        using var dir = new TempDir();
        var pptx = dir.WriteText("deck.pptx", "x");
        var soffice = new FakeSoffice();

        var result = await Kit.Ok(Kit.RunAsync("convert:jpg", [pptx], converters: Module(soffice)));

        Assert.Equal(dir.Combine("deck (converted).jpg"), result.Outputs[0]);
        Assert.Equal("jpg", PixelKit.Signature(result.Outputs[0]));
        Assert.Equal("pdf:impress_pdf_Export", soffice.ConvertTo(0));
    }

    [Fact]
    public async Task Doc_to_markdown_goes_through_docx_and_is_written_in_process()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("legacy.doc", "x");
        var soffice = new FakeSoffice();

        var result = await Kit.Ok(Kit.RunAsync("convert:md", [doc], converters: Module(soffice)));

        Assert.Equal(dir.Combine("legacy (converted).md"), result.Outputs[0]);
        Assert.Equal("# Converted heading\n\nSome body text.\n", File.ReadAllText(result.Outputs[0]).Replace("\r\n", "\n"));
        Assert.Equal("docx:MS Word 2007 XML", soffice.ConvertTo(0));
    }

    [Fact]
    public async Task An_old_workbook_to_csv_keeps_every_sheet_via_xlsx()
    {
        using var dir = new TempDir();
        var xls = dir.WriteText("old.xls", "x");
        var soffice = new FakeSoffice(FakeSoffice.TwoSheetWorkbook);

        var result = await Kit.Ok(Kit.RunAsync("convert:csv", [xls], converters: Module(soffice)));

        var folder = Assert.Single(result.Outputs);
        Assert.Equal(["old - First.csv", "old - Second.csv"], Kit.FilesIn(folder));
        Assert.Equal("xlsx:Calc MS Excel 2007 XML", soffice.ConvertTo(0));
    }

    [Fact]
    public async Task An_old_workbook_to_json_is_one_object_keyed_by_sheet()
    {
        using var dir = new TempDir();
        var xls = dir.WriteText("old.xls", "x");
        var soffice = new FakeSoffice(FakeSoffice.TwoSheetWorkbook);

        var result = await Kit.Ok(Kit.RunAsync("convert:json", [xls], converters: Module(soffice)));

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(result.Outputs[0]));
        Assert.Equal("v2", json.RootElement.GetProperty("Second")[0].GetProperty("h2").GetString());
    }

    [Fact]
    public async Task When_libreoffice_produces_nothing_the_job_fails_with_the_engine_message_and_no_output()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("broken.doc", "x");
        var soffice = new FakeSoffice(exitCode: 1, writeOutput: false);

        var result = await Kit.RunAsync("convert:docx", [doc], converters: Module(soffice));

        Assert.False(result.Success);
        Assert.Contains("broken.doc", result.Error);
        Assert.Contains("LibreOffice", result.Error);
        Assert.Contains("source file could not be loaded", result.Error);
        Assert.Equal(["broken.doc"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_zero_byte_result_counts_as_a_failure()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("x.doc", "x");
        var soffice = new FakeSoffice(_ => []);

        var result = await Kit.RunAsync("convert:docx", [doc], converters: Module(soffice));

        Assert.False(result.Success);
        Assert.Equal(["x.doc"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_missing_libreoffice_fails_with_a_message_that_names_it()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("a.doc", "x");

        var result = await Kit.RunAsync("convert:docx", [doc]); // the default module: nothing installed

        Assert.False(result.Success);
        Assert.Contains("LibreOffice", result.Error);
        Assert.Contains("not installed", result.Error);
        Assert.Equal(["a.doc"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_timeout_is_reported_as_such()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("slow.doc", "x");
        var runner = new FakeRunner((_, _) => new ProcessResult(-1, "", "", TimeSpan.FromMinutes(15), TimedOut: true, Cancelled: false));

        var result = await Kit.RunAsync("convert:docx", [doc], converters: FakeRunner.Module(runner, EngineId.LibreOffice));

        Assert.False(result.Success);
        Assert.Contains("too long", result.Error);
    }

    [Fact]
    public async Task Cancellation_stops_before_anything_is_published()
    {
        using var dir = new TempDir();
        var doc = dir.WriteText("a.doc", "x");
        var soffice = new FakeSoffice();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:docx", [doc], ct: cts.Token, converters: Module(soffice)));

        Assert.Equal(["a.doc"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Unicode_paths_and_name_collisions_are_handled()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("مستندات")).FullName;
        var doc = Path.Combine(folder, "رسالة.doc");
        File.WriteAllText(doc, "x");
        var soffice = new FakeSoffice();

        var first = await Kit.Ok(Kit.RunAsync("convert:docx", [doc], converters: Module(soffice)));
        var second = await Kit.Ok(Kit.RunAsync("convert:docx", [doc], converters: Module(soffice)));

        Assert.Equal(Path.Combine(folder, "رسالة (converted).docx"), first.Outputs[0]);
        Assert.Equal(Path.Combine(folder, "رسالة (converted 2).docx"), second.Outputs[0]);
    }

    [Fact]
    public async Task The_output_directory_option_is_honoured()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var doc = dir.WriteText("a.doc", "x");

        var result = await Kit.Ok(Kit.RunAsync("convert:docx", [doc], outputDirectory: outDir.Path, converters: Module(new FakeSoffice())));

        Assert.Equal(outDir.Combine("a (converted).docx"), result.Outputs[0]);
        Assert.Equal(["a.doc"], Kit.FilesIn(dir.Path));
    }

    // ---- real LibreOffice (skipped when it is not installed) ----------------------------------------------------------------

    [EngineFact(EngineId.LibreOffice)]
    public async Task Real_libreoffice_converts_a_docx_to_pdf_and_odt()
    {
        using var dir = new TempDir();
        var docx = Write(dir.Combine("real.docx"), P("Hello from Word", "Heading1") + P("Body paragraph."));
        var modules = PdfModule.Create(DropFlow.TestSupport.Engines.Locator);

        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [docx], converters: modules))).Outputs[0];
        var odt = (await Kit.Ok(Kit.RunAsync("convert:odt", [docx], converters: modules))).Outputs[0];

        Assert.Contains("Hello from Word", Kit.PageTexts(pdf)[0]);
        Assert.Equal("zip", PixelKit.Signature(odt));
    }

    [EngineFact(EngineId.LibreOffice)]
    public async Task Real_libreoffice_converts_txt_with_arabic_text_to_pdf()
    {
        using var dir = new TempDir();
        var txt = dir.WriteText("arabic.txt", "مرحبا بالعالم\n");
        var modules = PdfModule.Create(DropFlow.TestSupport.Engines.Locator);

        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [txt], converters: modules))).Outputs[0];

        Assert.True(Kit.PageCount(pdf) >= 1);
        Assert.NotEmpty(Kit.PageTexts(pdf)[0]);
    }

    [EngineFact(EngineId.LibreOffice)]
    public async Task Real_libreoffice_converts_csv_to_pdf_and_markdown_to_epub()
    {
        using var dir = new TempDir();
        var csv = dir.WriteText("t.csv", "a,b\n1,2\n");
        var md = dir.WriteText("n.md", "# Title\n\nText\n");
        var modules = PdfModule.Create(DropFlow.TestSupport.Engines.Locator);

        var pdf = (await Kit.Ok(Kit.RunAsync("convert:pdf", [csv], converters: modules))).Outputs[0];
        var epub = (await Kit.Ok(Kit.RunAsync("convert:epub", [md], converters: modules))).Outputs[0];

        Assert.Equal("pdf", PixelKit.Signature(pdf));
        Assert.Equal("zip", PixelKit.Signature(epub));
    }
}

internal static class DocumentToolsHelper
{
    /// <summary>Reads a .docx with the production reader and returns Markdown (test shorthand for "what is in this file").</summary>
    public static string DocxToMarkdown(string docx) => RichMarkdownWriter.Write(DocxReader.Read(docx));
}
