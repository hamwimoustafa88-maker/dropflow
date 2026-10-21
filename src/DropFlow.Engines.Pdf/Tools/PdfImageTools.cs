using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>Pages as pictures: <c>pdf.to-images</c> plus <c>convert:jpg</c> / <c>convert:png</c> on a PDF. Rendered by PDFium, encoded by Magick.NET.</summary>
internal static class PdfImageTools
{
    public const int DefaultDpi = 300;
    public const int DefaultQuality = 90;

    public static readonly IReadOnlyList<ToolOption> Options =
    [
        new("format", ToolOptionKind.Choice, "jpg", ["jpg", "png"], Required: true),
        new("dpi", ToolOptionKind.Number, DefaultDpi, Min: 36, Max: 600),
        new("quality", ToolOptionKind.Number, DefaultQuality, Min: 10, Max: 100),
        new("pages", ToolOptionKind.Range, ""),
    ];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.to-images", "pdf", ctx => Task.FromResult(ToImages(ctx, null)), Options);
        yield return OperationSpec.For("convert:jpg", "pdf", ctx => Task.FromResult(ToImages(ctx, "jpg")));
        yield return OperationSpec.For("convert:png", "pdf", ctx => Task.FromResult(ToImages(ctx, "png")));
    }

    private static ConversionResult ToImages(JobContext ctx, string? forcedFormat)
    {
        var isConvert = forcedFormat is not null;
        var format = (forcedFormat ?? ctx.Job.GetString("format", "jpg") ?? "jpg").ToLowerInvariant();
        if (format == "jpeg") format = "jpg";
        if (format is not ("jpg" or "png")) return ctx.Fail("Choose JPG or PNG.");

        var dpi = isConvert ? DefaultDpi : ctx.Job.GetDouble("dpi", DefaultDpi);
        var quality = isConvert ? DefaultQuality : ctx.Job.GetInt("quality", DefaultQuality);
        if (dpi < 36 || dpi > 600) return ctx.Fail("DPI must be between 36 and 600.");

        return RenderToImages(ctx, ctx.Input, ctx.Input, format, dpi, quality, isConvert ? null : ctx.Job.GetString("pages"),
            isConvert ? "converted" : "images", isConvert ? "converted" : "image");
    }

    /// <summary>
    /// Draws the pages of <paramref name="pdfPath"/> into picture files named after <paramref name="nameSource"/> (the file the user
    /// dropped, which may differ from the PDF when LibreOffice produced it). One page gives a file, several give a folder.
    /// </summary>
    internal static ConversionResult RenderToImages(
        JobContext ctx, string pdfPath, string nameSource, string format, double dpi, int quality, string? pageSpec, string folderSuffix, string fileSuffix)
    {
        using var document = PdfiumDocument.Open(pdfPath);
        if (document.PageCount < 1) return ctx.Fail("The PDF has no pages.");
        if (!PageSpec.TryParse(pageSpec, document.PageCount, out var pages, out var error, allowBlank: true))
            return ctx.Fail(error);

        string? folder = null;
        string? single = null;
        if (pages.Count == 1) single = ctx.ReserveFile(nameSource, format, fileSuffix);
        else folder = ctx.ReserveFolder(nameSource, folderSuffix);

        var stem = Path.GetFileNameWithoutExtension(nameSource);
        var width = document.PageCount.ToString().Length;
        var reduced = false;
        for (var i = 0; i < pages.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / pages.Count, $"Page {pages[i].Page}");

            var rendered = document.Render(pages[i].Page - 1, dpi);
            reduced |= rendered.WasDownscaled;
            var bytes = format == "png" ? RasterEncoder.ToPng(rendered) : RasterEncoder.ToJpeg(rendered, quality);

            var path = single ?? ctx.ReserveInFolder(folder!, $"{stem} - {pages[i].Page.ToString().PadLeft(width, '0')}.{format}");
            File.WriteAllBytes(path, bytes);
        }

        ctx.Report(1, reduced ? "Very large pages were drawn at a lower DPI to stay within memory limits." : null);
        return ctx.Ok(single ?? folder!);
    }
}
