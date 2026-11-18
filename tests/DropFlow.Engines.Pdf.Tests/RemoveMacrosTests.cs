using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DropFlow.TestSupport;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// <c>doc.remove-macros</c>: macro-enabled Word, Excel and PowerPoint files (and their templates) become plain DOCX/XLSX/PPTX without the
/// VBA project, Excel 4.0 macro sheets or macro key bindings. Results are checked as zip entries and XML (not through the SDK that
/// wrote them), with ClosedXML for the workbook, and with the Open XML validator: no error the source did not already have.
/// </summary>
public sealed class RemoveMacrosTests : IDisposable
{
    private const string Op = "doc.remove-macros";
    private static readonly byte[] FakeVba = Encoding.ASCII.GetBytes("not a real VBA project, only bytes the tool must drop");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ------------------------------------------------------------------ fixtures (written with the SDK, the way Office stores them)

    private string Docm(string name, WordprocessingDocumentType type = WordprocessingDocumentType.MacroEnabledDocument)
    {
        var path = _dir.Combine(name);
        using var doc = WordprocessingDocument.Create(path, type);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(
            new W.Paragraph(new W.Run(new W.Text("مرحبا — a document with macros"))),
            new W.SectionProperties()));
        var vba = main.AddNewPart<VbaProjectPart>();
        using (var s = new MemoryStream(FakeVba)) vba.FeedData(s);
        var vbaData = vba.AddNewPart<VbaDataPart>();
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes("<wne:vbaSuppData xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\"/>"))) vbaData.FeedData(s);
        var keys = main.AddNewPart<CustomizationPart>();
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes("<wne:tcg xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\"><wne:keymaps><wne:keymap wne:kcmPrimary=\"0441\"><wne:macro wne:macroName=\"PROJECT.MODULE1.RUN\"/></wne:keymap></wne:keymaps></wne:tcg>"))) keys.FeedData(s);
        return path;
    }

    private string Xlsm(string name, SpreadsheetDocumentType type = SpreadsheetDocumentType.MacroEnabledWorkbook, bool onlyMacroSheet = false)
    {
        var path = _dir.Combine(name);
        using var doc = SpreadsheetDocument.Create(path, type);
        var wb = doc.AddWorkbookPart();
        var sheets = new S.Sheets();
        uint id = 1;

        if (!onlyMacroSheet)
        {
            var data = wb.AddNewPart<WorksheetPart>();
            data.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text("مرحبا")) },
                new S.Cell { CellReference = "B1", CellValue = new S.CellValue(42) }) { RowIndex = 1 }));
            sheets.Append(new S.Sheet { Name = "Data", SheetId = id++, Id = wb.GetIdOfPart(data) });
        }

        var macro = wb.AddNewPart<MacroSheetPart>();
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(
            "<xm:macrosheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\">" +
            "<sheetData><row r=\"1\"><c r=\"A1\"><f>EXEC(\"calc.exe\")</f></c></row><row r=\"2\"><c r=\"A2\"><f>HALT()</f></c></row></sheetData></xm:macrosheet>")))
        {
            macro.FeedData(s);
        }
        sheets.Append(new S.Sheet { Name = "Macro1", SheetId = id++, Id = wb.GetIdOfPart(macro), State = S.SheetStateValues.Hidden });

        if (!onlyMacroSheet)
        {
            var last = wb.AddNewPart<WorksheetPart>();
            last.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell { CellReference = "A1", CellValue = new S.CellValue(7) }) { RowIndex = 1 }));
            sheets.Append(new S.Sheet { Name = "Last", SheetId = id++, Id = wb.GetIdOfPart(last) });
        }

        var names = new S.DefinedNames(
            new S.DefinedName("'Macro1'!$A$1") { Name = "Auto_Open" },
            new S.DefinedName("Macro1!$A$1:$A$2") { Name = "Steps" });
        if (!onlyMacroSheet)
        {
            names.Append(new S.DefinedName("Data!$B$1") { Name = "Answer" });
            names.Append(new S.DefinedName("Last!$A$1") { Name = "Local", LocalSheetId = 2 });   // the third sheet, "Last"
        }
        wb.Workbook = new S.Workbook(
            new S.BookViews(new S.WorkbookView { ActiveTab = onlyMacroSheet ? 0u : 2u }),
            sheets,
            names);

        var vba = wb.AddNewPart<VbaProjectPart>();
        using (var s = new MemoryStream(FakeVba)) vba.FeedData(s);
        return path;
    }

    private async Task<string> Pptm(string name, PresentationDocumentType type = PresentationDocumentType.MacroEnabledPresentation)
    {
        // A real presentation from DropFlow's own PDF to PPTX conversion, then made macro-enabled the way PowerPoint stores it.
        var pdf = Samples.Pdf(_dir.Combine("slides.pdf"), pages: 2, text: i => $"Slide {i}");
        var pptx = (await Kit.Ok(Kit.RunAsync("convert:pptx", [pdf]))).Outputs.Single();
        var path = _dir.Combine(name);
        File.Copy(pptx, path);
        using var doc = PresentationDocument.Open(path, true);
        var vba = doc.PresentationPart!.AddNewPart<VbaProjectPart>();
        using (var s = new MemoryStream(FakeVba)) vba.FeedData(s);
        var slide = doc.PresentationPart.SlideParts.First();
        var props = slide.Slide.Descendants<DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties>().First();
        props.Append(new DocumentFormat.OpenXml.Drawing.HyperlinkOnClick { Id = "", Action = "ppaction://macro?name=Module1.Run" });
        doc.ChangeDocumentType(type);
        return path;
    }

    // ------------------------------------------------------------------ checks

    private static Dictionary<string, byte[]> Entries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        });
    }

    private static XDocument Xml(Dictionary<string, byte[]> entries, string name) => XDocument.Parse(Encoding.UTF8.GetString(entries[name]).TrimStart('﻿'));

    private static void AssertNoMacroLeft(string output, string mainPart, string expectedContentType)
    {
        var entries = Entries(output);
        Assert.DoesNotContain(entries.Keys, k => k.Contains("vbaProject", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries.Keys, k => k.Contains("vbaData", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries.Keys, k => k.Contains("macrosheets", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries.Keys, k => k.Contains("customizations", StringComparison.OrdinalIgnoreCase));
        var types = Encoding.UTF8.GetString(entries["[Content_Types].xml"]);
        Assert.DoesNotContain("macroEnabled", types, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbaProject", types, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("template", types, StringComparison.OrdinalIgnoreCase);
        // The main part keeps its name (whatever the source called it) and gets the macro-free type.
        var target = Xml(entries, "_rels/.rels").Descendants()
            .Single(e => ((string?)e.Attribute("Type") ?? "").EndsWith("/officeDocument", StringComparison.Ordinal)).Attribute("Target")!.Value.TrimStart('/');
        Assert.StartsWith(mainPart[..mainPart.IndexOf('/')], target, StringComparison.Ordinal);
        Assert.Contains($"PartName=\"/{target}\" ContentType=\"{expectedContentType}\"", types, StringComparison.Ordinal);
        foreach (var rels in entries.Keys.Where(k => k.EndsWith(".rels", StringComparison.Ordinal)))
            Assert.DoesNotContain("vbaProject", Encoding.UTF8.GetString(entries[rels]), StringComparison.OrdinalIgnoreCase);
        foreach (var bytes in entries.Values)
            Assert.False(bytes.AsSpan().IndexOf(FakeVba) >= 0, "the VBA bytes are still in the package");
    }

    /// <summary>Validator errors of the output that the source did not have (by element path and description).</summary>
    private static List<string> NewValidationErrors(string source, string output)
    {
        static HashSet<string> Errors(OpenXmlPackage package) =>
            new OpenXmlValidator(FileFormatVersions.Office2019).Validate(package)
                .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToHashSet();

        HashSet<string> before;
        var copy = source + ".validate" + Path.GetExtension(source);
        File.Copy(source, copy);
        using (var pkg = OpenAny(copy)) before = Errors(pkg);
        File.Delete(copy);
        using var after = OpenAny(output);
        return Errors(after).Where(e => !before.Contains(e)).ToList();

        static OpenXmlPackage OpenAny(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docm" or ".dotm" or ".docx" => WordprocessingDocument.Open(path, false),
            ".xlsm" or ".xltm" or ".xlsx" => SpreadsheetDocument.Open(path, false),
            _ => PresentationDocument.Open(path, false),
        };
    }

    // ------------------------------------------------------------------ Word

    [Theory]
    [InlineData("تقرير.docm", WordprocessingDocumentType.MacroEnabledDocument)]
    [InlineData("letter.dotm", WordprocessingDocumentType.MacroEnabledTemplate)]
    public async Task Word_files_become_a_docx_without_vba_or_macro_keys(string name, WordprocessingDocumentType type)
    {
        var input = Docm(name, type);
        var before = File.ReadAllBytes(input);

        var result = await Kit.Ok(Kit.RunAsync(Op, [input]));

        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.GetFileNameWithoutExtension(name) + " (no macros).docx", Path.GetFileName(output));
        Assert.Null(result.Note);
        AssertNoMacroLeft(output, "word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        var text = string.Concat(Xml(Entries(output), "word/document.xml").Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
        Assert.Equal("مرحبا — a document with macros", text);
        Assert.Empty(NewValidationErrors(input, output));
        Assert.Equal(before, File.ReadAllBytes(input));                    // the original keeps its macros
    }

    // ------------------------------------------------------------------ Excel

    [Theory]
    [InlineData("book.xlsm", SpreadsheetDocumentType.MacroEnabledWorkbook)]
    [InlineData("form.xltm", SpreadsheetDocumentType.MacroEnabledTemplate)]
    public async Task Excel_files_lose_vba_and_excel4_macro_sheets_and_keep_their_data(string name, SpreadsheetDocumentType type)
    {
        var input = Xlsm(name, type);

        var result = await Kit.Ok(Kit.RunAsync(Op, [input]));

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(" (no macros).xlsx", output);
        AssertNoMacroLeft(output, "xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        Assert.Empty(NewValidationErrors(input, output));

        using var book = new XLWorkbook(output);                            // a different reader than the one that wrote it
        Assert.Equal(["Data", "Last"], book.Worksheets.Select(w => w.Name));
        Assert.Equal("مرحبا", book.Worksheet("Data").Cell("A1").GetString());
        Assert.Equal(42, book.Worksheet("Data").Cell("B1").GetDouble());
        Assert.Equal(["Answer"], book.DefinedNames.Select(n => n.Name));     // Auto_Open and Steps pointed at the macro sheet

        var workbook = Xml(Entries(output), "xl/workbook.xml");
        var local = workbook.Descendants().Single(e => e.Name.LocalName == "definedName" && (string?)e.Attribute("name") == "Local");
        Assert.Equal("1", (string?)local.Attribute("localSheetId"));       // "Last" moved from index 2 to 1
        Assert.Equal("1", (string?)workbook.Descendants().Single(e => e.Name.LocalName == "workbookView").Attribute("activeTab"));
    }

    [Fact]
    public async Task A_workbook_that_is_only_a_macro_sheet_is_refused_and_leaves_nothing()
    {
        var input = Xlsm("only.xlsm", onlyMacroSheet: true);
        var result = await Kit.RunAsync(Op, [input]);
        Assert.False(result.Success);
        Assert.Contains("macro", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["only.xlsm"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    // ------------------------------------------------------------------ PowerPoint

    [Theory]
    [InlineData("deck.pptm", PresentationDocumentType.MacroEnabledPresentation)]
    [InlineData("theme.potm", PresentationDocumentType.MacroEnabledTemplate)]
    [InlineData("show.ppsm", PresentationDocumentType.MacroEnabledSlideshow)]
    public async Task PowerPoint_files_become_a_pptx_without_vba_or_macro_actions(string name, PresentationDocumentType type)
    {
        var input = await Pptm(name, type);

        var result = await Kit.Ok(Kit.RunAsync(Op, [input]));

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith(" (no macros).pptx", output);
        AssertNoMacroLeft(output, "ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
        var entries = Entries(output);
        Assert.Equal(2, entries.Keys.Count(k => k.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && k.EndsWith(".xml", StringComparison.Ordinal)));
        Assert.DoesNotContain(entries.Where(e => e.Key.StartsWith("ppt/slides/", StringComparison.Ordinal)), e => Encoding.UTF8.GetString(e.Value).Contains("ppaction://macro", StringComparison.Ordinal));
        Assert.Empty(NewValidationErrors(input, output));
    }

    // ------------------------------------------------------------------ edge cases

    [Fact]
    public async Task A_macro_enabled_file_without_macros_is_still_saved_plain_with_a_note()
    {
        var path = _dir.Combine("clean.docm");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.MacroEnabledDocument))
        {
            doc.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("plain")))));
        }
        var result = await Kit.Ok(Kit.RunAsync(Op, [path]));
        Assert.Equal("note.doc.noMacros", result.Note);
        AssertNoMacroLeft(result.Outputs.Single(), "word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
    }

    [Fact]
    public async Task Never_overwrites_and_honours_the_output_folder()
    {
        var input = Docm("a.docm");
        File.WriteAllText(_dir.Combine("a (no macros).docx"), "keep me");
        var first = await Kit.Ok(Kit.RunAsync(Op, [input]));
        Assert.Equal("a (no macros 2).docx", Path.GetFileName(first.Outputs.Single()));
        Assert.Equal("keep me", File.ReadAllText(_dir.Combine("a (no macros).docx")));

        var outDir = _dir.Combine("out folder");
        var second = await Kit.Ok(Kit.RunAsync(Op, [input], outputDirectory: outDir));
        Assert.Equal(outDir, Path.GetDirectoryName(second.Outputs.Single()));
    }

    [Fact]
    public async Task A_damaged_or_password_protected_file_fails_cleanly_and_leaves_nothing()
    {
        var input = _dir.Combine("broken.xlsm");
        File.WriteAllBytes(input, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]); // an OLE file, as encrypted Office files are
        var result = await Kit.RunAsync(Op, [input]);
        Assert.False(result.Success);
        Assert.Contains("password", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["broken.xlsm"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancellation_leaves_no_output()
    {
        var input = Docm("c.docm");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync(Op, [input], ct: cts.Token));
        Assert.Equal(["c.docm"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Content_types_written_the_way_excel_writes_them_are_fixed_without_touching_other_entries()
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = XDocument.Parse(
            $"<Types xmlns=\"{ns}\">" +
            "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.ms-excel.sheet.macroEnabled.main+xml\"/>" +
            "<Override PartName=\"/xl/printerSettings/printerSettings1.bin\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings\"/>" +
            "</Types>");

        Office.MacroFreeContentTypes.Fix(types, "/xl/workbook.xml", ["[Content_Types].xml", "xl/workbook.xml", "xl/printerSettings/printerSettings1.bin"]);

        var text = types.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        Assert.Contains("PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"", text, StringComparison.Ordinal);
        Assert.Contains("printerSettings1.bin\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings\"", text, StringComparison.Ordinal);
        // The only .bin left has its own Override, so the VBA project Default types nothing any more and goes.
        Assert.DoesNotContain("vbaProject", text, StringComparison.Ordinal);
        Assert.Equal(4, types.Root!.Elements().Count());

        // A .bin part without an Override still depends on the Default: it is left alone rather than left without a type.
        var odd = XDocument.Parse($"<Types xmlns=\"{ns}\"><Default Extension=\"bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/></Types>");
        Office.MacroFreeContentTypes.Fix(odd, "/xl/workbook.xml", ["xl/other.bin"]);
        Assert.Single(odd.Root!.Elements());
    }

    [Theory]
    [InlineData("docm", true)]
    [InlineData("dotm", true)]
    [InlineData("xlsm", true)]
    [InlineData("xltm", true)]
    [InlineData("pptm", true)]
    [InlineData("potm", true)]
    [InlineData("ppsm", true)]
    [InlineData("docx", false)]
    [InlineData("xlsx", false)]
    [InlineData("pptx", false)]
    [InlineData("pdf", false)]
    public void Supports_only_macro_enabled_office_files(string ext, bool expected) =>
        Assert.Equal(expected, Kit.Converters.Any(c => c.Supports(Op, ext)));
}
