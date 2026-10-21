using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>Text out of a PDF: <c>pdf.extract-text</c> (TXT and Markdown), <c>convert:txt</c>, <c>convert:md</c> and <c>convert:html</c>.</summary>
internal static class PdfTextTools
{
    public static readonly IReadOnlyList<ToolOption> ExtractOptions =
    [
        new("format", ToolOptionKind.Choice, "both", ["both", "txt", "md"], Required: true),
    ];

    private const string NoTextMessage =
        "This PDF has no text layer (it is probably a scan). Run OCR on it first, then extract the text.";

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.extract-text", "pdf", ctx => Task.FromResult(Extract(ctx)), ExtractOptions);
        yield return OperationSpec.For("convert:txt", "pdf", ctx => Task.FromResult(ConvertTo(ctx, "txt")));
        yield return OperationSpec.For("convert:md", "pdf", ctx => Task.FromResult(ConvertTo(ctx, "md")));
        yield return OperationSpec.For("convert:html", "pdf", ctx => Task.FromResult(ConvertTo(ctx, "html")));
    }

    private static ConversionResult Extract(JobContext ctx)
    {
        var format = (ctx.Job.GetString("format", "both") ?? "both").ToLowerInvariant();
        if (format is not ("both" or "txt" or "md")) return ctx.Fail($"Unknown format '{format}'. Use txt, md or both.");

        var document = PdfTextExtractor.Read(ctx.Input, ctx.Ct, f => ctx.Report(f * 0.9));
        if (!document.HasText) return ctx.Fail(NoTextMessage);

        var outputs = new List<string>();
        if (format is "both" or "txt") outputs.Add(WriteText(ctx, "txt", "text", PlainTextWriter.Write(document), bom: true));
        if (format is "both" or "md") outputs.Add(WriteText(ctx, "md", "text", MarkdownWriter.Write(document), bom: false));
        ReportOutcome(ctx, document);
        return ctx.Ok([.. outputs]);
    }

    private static ConversionResult ConvertTo(JobContext ctx, string target)
    {
        var document = PdfTextExtractor.Read(ctx.Input, ctx.Ct, f => ctx.Report(f * 0.9));
        if (!document.HasText) return ctx.Fail(NoTextMessage);

        var content = target switch
        {
            "txt" => PlainTextWriter.Write(document),
            "md" => MarkdownWriter.Write(document),
            _ => HtmlWriter.Write(document, Path.GetFileNameWithoutExtension(ctx.Input), Language(document)),
        };
        var path = WriteText(ctx, target, "converted", content, bom: target == "txt");
        ReportOutcome(ctx, document);
        return ctx.Ok(path);
    }

    private static string WriteText(JobContext ctx, string extension, string suffix, string content, bool bom)
    {
        var path = ctx.ReserveFile(ctx.Input, extension, suffix);
        File.WriteAllText(path, content, new UTF8Encoding(bom));
        return path;
    }

    private static void ReportOutcome(JobContext ctx, PdfTextDocument document)
    {
        ctx.Report(1, document.SkippedRotatedCharacters > 0
            ? $"{document.SkippedRotatedCharacters} characters of rotated (vertical) text were not included."
            : null);
    }

    /// <summary>Document language for HTML/EPUB metadata: Arabic or Hebrew when those scripts dominate, English otherwise.</summary>
    internal static string Language(PdfTextDocument document)
    {
        var sample = string.Concat(document.Pages.SelectMany(p => p.Lines).Take(200).Select(l => l.Text));
        var arabic = sample.Count(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﻿');
        var hebrew = sample.Count(c => c is >= '֐' and <= '׿');
        var latin = sample.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (arabic > latin && arabic >= hebrew) return "ar";
        if (hebrew > latin) return "he";
        return "en";
    }
}
