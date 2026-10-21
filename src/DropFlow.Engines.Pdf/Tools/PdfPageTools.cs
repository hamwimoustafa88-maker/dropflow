using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>Merge, split, rotate, organize and metadata: page-level PDF work done in-process with PDFsharp.</summary>
internal static class PdfPageTools
{
    public static readonly IReadOnlyList<ToolOption> RotateOptions =
    [
        new("degrees", ToolOptionKind.Choice, "90", ["90", "180", "270"], Required: true),
        new("pages", ToolOptionKind.Range, ""),
    ];

    public static readonly IReadOnlyList<ToolOption> OrganizeOptions =
    [
        new("pages", ToolOptionKind.Range, "", Required: true),
    ];

    public static readonly IReadOnlyList<ToolOption> MetadataOptions =
    [
        new("title", ToolOptionKind.Text),
        new("author", ToolOptionKind.Text),
        new("subject", ToolOptionKind.Text),
        new("keywords", ToolOptionKind.Text),
        new("strip", ToolOptionKind.Bool, false),
    ];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.merge", "pdf", Merge);
        yield return OperationSpec.For("pdf.split", "pdf", Split);
        yield return OperationSpec.For("pdf.rotate", "pdf", Rotate, RotateOptions);
        yield return OperationSpec.For("pdf.organize", "pdf", Organize, OrganizeOptions);
        yield return OperationSpec.For("pdf.metadata", "pdf", Metadata, MetadataOptions);
    }

    private static Task<ConversionResult> Merge(JobContext ctx)
    {
        var inputs = ctx.Job.Inputs;
        if (inputs.Count < 2) return Task.FromResult(ctx.Fail("Select at least two PDF files to merge."));
        var notPdf = inputs.FirstOrDefault(i => !string.Equals(Path.GetExtension(i), ".pdf", StringComparison.OrdinalIgnoreCase));
        if (notPdf is not null) return Task.FromResult(ctx.Fail($"Only PDF files can be merged: {Path.GetFileName(notPdf)}"));

        using var output = new PdfDocument();
        PdfSharpSupport.ApplyDefaultOptions(output);
        for (var i = 0; i < inputs.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / inputs.Count, Path.GetFileName(inputs[i]));
            using var source = PdfSharpSupport.OpenForImport(inputs[i]);
            foreach (var page in source.Pages) output.AddPage(page);
        }

        var path = ctx.ReserveGroupFile("pdf", "merged");
        output.Save(path);
        ctx.Report(1);
        return Task.FromResult(ctx.Ok(path));
    }

    private static Task<ConversionResult> Split(JobContext ctx)
    {
        using var source = PdfSharpSupport.OpenForImport(ctx.Input);
        var count = source.PageCount;
        if (count < 1) return Task.FromResult(ctx.Fail("The PDF has no pages."));

        var folder = ctx.ReserveFolder(ctx.Input, "pages");
        var stem = Path.GetFileNameWithoutExtension(ctx.Input);
        var width = count.ToString().Length;
        for (var i = 0; i < count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / count, $"Page {i + 1}");
            using var single = new PdfDocument();
            PdfSharpSupport.ApplyDefaultOptions(single);
            single.AddPage(source.Pages[i]);
            var path = ctx.ReserveInFolder(folder, $"{stem} - {(i + 1).ToString().PadLeft(width, '0')}.pdf");
            single.Save(path);
        }

        ctx.Report(1);
        return Task.FromResult(ctx.Ok(folder));
    }

    private static Task<ConversionResult> Rotate(JobContext ctx)
    {
        var degrees = ctx.Job.GetInt("degrees", 90);
        if (degrees is not (90 or 180 or 270))
            return Task.FromResult(ctx.Fail("Rotation must be 90, 180 or 270 degrees."));

        using var doc = PdfSharpSupport.OpenForModify(ctx.Input);
        if (!PageSpec.TryParse(ctx.Job.GetString("pages"), doc.PageCount, out var pages, out var error, allowBlank: true))
            return Task.FromResult(ctx.Fail(error));

        foreach (var page in pages.Select(p => p.Page).Distinct())
            PdfSharpSupport.RotateBy(doc.Pages[page - 1], degrees);

        var path = ctx.ReserveFile(ctx.Input, "pdf", "rotated");
        doc.Save(path);
        return Task.FromResult(ctx.Ok(path));
    }

    private static Task<ConversionResult> Organize(JobContext ctx)
    {
        using var source = PdfSharpSupport.OpenForImport(ctx.Input);
        if (!PageSpec.TryParse(ctx.Job.GetString("pages"), source.PageCount, out var pages, out var error))
            return Task.FromResult(ctx.Fail(error));

        using var output = new PdfDocument();
        PdfSharpSupport.ApplyDefaultOptions(output);
        var firstUse = new HashSet<int>();
        foreach (var reference in pages)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            // A page used twice must be an independent copy, otherwise rotating one occurrence would rotate both.
            var added = firstUse.Add(reference.Page)
                ? output.AddPage(source.Pages[reference.Page - 1])
                : output.AddPage(ReopenPage(ctx.Input, reference.Page));
            if (reference.Rotation != 0) PdfSharpSupport.RotateBy(added, reference.Rotation);
        }

        var path = ctx.ReserveFile(ctx.Input, "pdf", "organized");
        output.Save(path);
        return Task.FromResult(ctx.Ok(path));
    }

    // Imports a page from a freshly opened copy of the source so repeated pages do not share one PdfPage object.
    private static PdfPage ReopenPage(string path, int page)
    {
        var copy = PdfSharpSupport.OpenForImport(path);
        return copy.Pages[page - 1];
    }

    private static Task<ConversionResult> Metadata(JobContext ctx)
    {
        using var doc = PdfSharpSupport.OpenForModify(ctx.Input);
        var info = doc.Info;

        if (ctx.Job.GetBool("strip"))
        {
            info.Title = "";
            info.Author = "";
            info.Subject = "";
            info.Keywords = "";
            info.Creator = "";
            foreach (var key in info.Elements.Keys.ToList())
            {
                if (key is "/Title" or "/Author" or "/Subject" or "/Keywords" or "/Creator" or "/CreationDate" or "/ModDate")
                    info.Elements.Remove(key);
            }
            doc.Internals.Catalog.Elements.Remove("/Metadata"); // XMP packet, which duplicates the Info dictionary
        }

        Apply(ctx.Job.GetString("title"), v => info.Title = v);
        Apply(ctx.Job.GetString("author"), v => info.Author = v);
        Apply(ctx.Job.GetString("subject"), v => info.Subject = v);
        Apply(ctx.Job.GetString("keywords"), v => info.Keywords = v);

        var path = ctx.ReserveFile(ctx.Input, "pdf", "edited");
        doc.Save(path);
        return Task.FromResult(ctx.Ok(path));

        static void Apply(string? value, Action<string> set)
        {
            if (!string.IsNullOrWhiteSpace(value)) set(value.Trim());
        }
    }
}
