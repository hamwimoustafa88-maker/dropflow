namespace DropFlow.Engines.Pdf.Office;

/// <summary>What DropFlow does before LibreOffice sees the file.</summary>
public enum LoPre
{
    None,
    /// <summary>Plain text is re-saved as UTF-8 with a byte order mark so LibreOffice cannot misread Arabic text as an ANSI code page.</summary>
    TextToUtf8,
    /// <summary>LibreOffice cannot open Markdown: it is rendered to HTML first.</summary>
    MarkdownToHtml,
    /// <summary>CSV, TSV and JSON are turned into a workbook first.</summary>
    TabularToXlsx,
    /// <summary>LibreOffice cannot open EPUB: the chapters are joined into one HTML file with its pictures in a folder.</summary>
    EpubToHtml,
}

/// <summary>What DropFlow does with the file LibreOffice produced.</summary>
public enum LoPost
{
    /// <summary>The produced file is the result.</summary>
    None,
    /// <summary>The produced PDF is drawn to PNG/JPG pages with PDFium (LibreOffice itself only exports the first page as a picture).</summary>
    PageImages,
    /// <summary>The produced workbook is converted to CSV, TSV, JSON or HTML in-process (all sheets, consistent with the managed converters).</summary>
    Tabular,
    /// <summary>The produced .docx is read and written as Markdown, which LibreOffice cannot export.</summary>
    DocxToMarkdown,
}

/// <param name="Pre">Preparation of the input.</param>
/// <param name="InputFilter">LibreOffice import filter (<c>--infilter</c>), or null to let LibreOffice decide.</param>
/// <param name="ProducedExtension">Extension of the file LibreOffice writes.</param>
/// <param name="ExportFilter">LibreOffice export filter after the colon of <c>--convert-to</c>, or null.</param>
/// <param name="Post">What happens to the produced file.</param>
/// <param name="Target">The extension the user asked for.</param>
public sealed record LibreOfficePlan(LoPre Pre, string? InputFilter, string ProducedExtension, string? ExportFilter, LoPost Post, string Target);

/// <summary>
/// The table of conversions that go through LibreOffice. A pair is listed only when LibreOffice (with DropFlow's pre/post steps) can do
/// it and no in-process converter already does. Pure and testable: no process is started here.
/// </summary>
public static class LibreOfficePlanner
{
    private static readonly string[] WriterInputs = ["docx", "doc", "odt", "rtf", "pages", "html", "txt"];
    private static readonly string[] ImpressInputs = ["pptx", "ppt", "odp", "key"];
    private static readonly string[] CalcInputs = ["xlsx", "xls", "ods", "numbers"];
    private static readonly string[] TabularInputs = ["csv", "tsv", "json"];

    public static readonly string[] AllInputs = [.. WriterInputs, "md", "epub", .. ImpressInputs, .. CalcInputs, .. TabularInputs];
    public static readonly string[] AllTargets = ["docx", "pdf", "txt", "md", "html", "epub", "odt", "rtf", "png", "jpg", "odp", "pptx", "xlsx", "ods", "csv", "tsv", "json"];

    /// <summary>Every (input, target) pair LibreOffice is used for.</summary>
    public static IReadOnlyList<(string Input, string Target)> Pairs { get; } =
        AllInputs.SelectMany(i => AllTargets.Select(t => (Input: i, Target: t))).Where(p => Plan(p.Input, p.Target) is not null).ToList();

    public static LibreOfficePlan? Plan(string inputExtension, string targetExtension)
    {
        var input = inputExtension.TrimStart('.').ToLowerInvariant();
        var target = targetExtension.TrimStart('.').ToLowerInvariant();
        if (input == target || ManagedPairs.Handles(input, target)) return null;

        if (WriterInputs.Contains(input) || input is "md" or "epub") return WriterPlan(input, target);
        if (ImpressInputs.Contains(input)) return ImpressPlan(target);
        if (CalcInputs.Contains(input) || TabularInputs.Contains(input)) return CalcPlan(input, target);
        return null;
    }

    private static LibreOfficePlan? WriterPlan(string input, string target)
    {
        var pre = input switch { "md" => LoPre.MarkdownToHtml, "epub" => LoPre.EpubToHtml, "txt" => LoPre.TextToUtf8, _ => LoPre.None };
        // HTML (and the HTML made from Markdown / EPUB) is opened as a Writer document, not Writer/Web, so every Writer export filter applies.
        var inFilter = input is "html" or "md" or "epub" ? "HTML (StarWriter)" : null;

        // The catalog offers EPUB books only these outputs through LibreOffice (txt, md and html are done in-process).
        if (input == "epub" && target is not ("pdf" or "docx" or "odt" or "rtf")) return null;

        return target switch
        {
            "pdf" => new(pre, inFilter, "pdf", "writer_pdf_Export", LoPost.None, "pdf"),
            "docx" => new(pre, inFilter, "docx", "MS Word 2007 XML", LoPost.None, "docx"),
            "odt" => new(pre, inFilter, "odt", "writer8", LoPost.None, "odt"),
            "rtf" => new(pre, inFilter, "rtf", "Rich Text Format", LoPost.None, "rtf"),
            "txt" => new(pre, inFilter, "txt", "Text (encoded):UTF8", LoPost.None, "txt"),
            "html" => new(pre, inFilter, "html", "HTML (StarWriter)", LoPost.None, "html"),
            "epub" => new(pre, inFilter, "epub", "EPUB", LoPost.None, "epub"),
            "md" => new(pre, inFilter, "docx", "MS Word 2007 XML", LoPost.DocxToMarkdown, "md"),
            "png" or "jpg" => new(pre, inFilter, "pdf", "writer_pdf_Export", LoPost.PageImages, target),
            _ => null,
        };
    }

    private static LibreOfficePlan? ImpressPlan(string target) => target switch
    {
        "pdf" => new(LoPre.None, null, "pdf", "impress_pdf_Export", LoPost.None, "pdf"),
        "odp" => new(LoPre.None, null, "odp", "impress8", LoPost.None, "odp"),
        "pptx" => new(LoPre.None, null, "pptx", "Impress MS PowerPoint 2007 XML", LoPost.None, "pptx"),
        "png" or "jpg" => new(LoPre.None, null, "pdf", "impress_pdf_Export", LoPost.PageImages, target),
        _ => null,
    };

    private static LibreOfficePlan? CalcPlan(string input, string target)
    {
        var pre = TabularInputs.Contains(input) ? LoPre.TabularToXlsx : LoPre.None;
        return target switch
        {
            "xlsx" => new(pre, null, "xlsx", "Calc MS Excel 2007 XML", LoPost.None, "xlsx"),
            "ods" => new(pre, null, "ods", "calc8", LoPost.None, "ods"),
            "pdf" => new(pre, null, "pdf", "calc_pdf_Export", LoPost.None, "pdf"),
            "csv" or "tsv" or "json" or "html" when CalcInputs.Contains(input) =>
                new(pre, null, "xlsx", "Calc MS Excel 2007 XML", LoPost.Tabular, target),
            _ => null,
        };
    }
}

/// <summary>Conversions the module does in-process, listed once so LibreOffice is never asked to do them as well.</summary>
internal static class ManagedPairs
{
    internal static readonly (string Input, string[] Targets)[] Documents =
    [
        ("txt", ["docx", "html", "md"]),
        ("md", ["docx", "html", "txt"]),
        ("docx", ["txt", "md", "html"]),
        ("html", ["md", "txt"]),
        ("epub", ["html", "md", "txt"]),
    ];

    private static readonly string[] Tabular = ["csv", "tsv", "xlsx", "json"];
    private static readonly string[] TabularTargets = ["csv", "tsv", "xlsx", "json", "html"];

    public static IEnumerable<(string Input, string Target)> DocumentPairs =>
        Documents.SelectMany(d => d.Targets.Select(t => (d.Input, Target: t)));

    public static bool Handles(string input, string target) =>
        Documents.Any(d => d.Input == input && d.Targets.Contains(target))
        || (Tabular.Contains(input) && TabularTargets.Contains(target) && input != target);
}
