using System.Globalization;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// <c>pdf.crop</c>: shows only part of each page. It sets the page's CropBox and nothing else, so the content stays in the file (the
/// crop can be undone by removing the box) and no text or picture is touched. Margins and boxes are measured on the page as it is
/// displayed - top is the top the reader sees even when the page carries /Rotate - and relative to what is visible now, so cropping a
/// cropped file works the way people expect. <c>auto</c> draws the page with PDFium at 72 DPI and keeps the bounding box of everything
/// that is not white, plus a padding.
/// </summary>
internal static class PdfCropTools
{
    private static readonly string[] Modes = ["margins", "box", "auto"];

    /// <summary>Anything smaller than this (about 0.35 mm) is not a page.</summary>
    private const double MinSide = 1;

    /// <summary>A pixel with any colour channel below this counts as content (255 is the white of an empty page).</summary>
    private const int InkThreshold = 250;

    public static readonly IReadOnlyList<ToolOption> Options =
    [
        new("mode", ToolOptionKind.Choice, "margins", Modes),
        new("top", ToolOptionKind.Number, 0, Min: 0, Max: 1000),
        new("right", ToolOptionKind.Number, 0, Min: 0, Max: 1000),
        new("bottom", ToolOptionKind.Number, 0, Min: 0, Max: 1000),
        new("left", ToolOptionKind.Number, 0, Min: 0, Max: 1000),
        new("box", ToolOptionKind.Text, ""),
        new("pages", ToolOptionKind.Range, ""),
        new("padding", ToolOptionKind.Number, 5, Min: 0, Max: 100),
    ];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.crop", "pdf", ctx => Task.FromResult(Crop(ctx)), Options);
    }

    private static ConversionResult Crop(JobContext ctx)
    {
        var mode = (ctx.Job.GetString("mode", "margins") ?? "margins").Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) return ctx.Fail($"Unknown crop mode '{mode}'. Use margins, box or auto.");

        double top = 0, right = 0, bottom = 0, left = 0, padding = 0;
        double[] box = [];
        switch (mode)
        {
            case "margins":
                (top, right, bottom, left) = (Mm(ctx, "top"), Mm(ctx, "right"), Mm(ctx, "bottom"), Mm(ctx, "left"));
                if (new[] { top, right, bottom, left }.Any(v => v < 0 || !double.IsFinite(v)))
                    return ctx.Fail("Margins cannot be negative.");
                if (top + right + bottom + left <= 0)
                    return ctx.Fail("Enter at least one margin to remove, for example top 10 (millimetres).");
                break;
            case "box":
                if (!NumberList.TryParse(ctx.Job.GetString("box"), 4, out box))
                    return ctx.Fail("Enter the box as x,y,width,height in millimetres from the top-left of the page, for example 10,10,150,200.");
                if (box[0] < 0 || box[1] < 0 || box[2] <= 0 || box[3] <= 0)
                    return ctx.Fail("The box needs x and y of 0 or more and a width and height above 0.");
                box = [.. box.Select(PageGeometry.MmToPoints)];
                break;
            default:
                padding = PageGeometry.MmToPoints(Math.Max(0, ctx.Job.GetDouble("padding", 5)));
                break;
        }

        using var document = PdfSharpSupport.OpenForModify(ctx.Input);
        if (!PageSpec.TryParse(ctx.Job.GetString("pages"), document.PageCount, out var refs, out var error, allowBlank: true))
            return ctx.Fail(error);
        var targets = refs.Select(r => r.Page).Distinct().ToList();

        using var pdfium = mode == "auto" ? PdfiumDocument.Open(ctx.Input) : null;
        var blank = new List<int>();
        for (var i = 0; i < targets.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / targets.Count, $"Page {targets[i]}");

            var page = document.Pages[targets[i] - 1];
            var geometry = PageGeometry.Of(page);
            double vw = geometry.VisualWidth, vh = geometry.VisualHeight;

            (double X, double Y, double W, double H)? visual;
            switch (mode)
            {
                case "margins":
                {
                    var (l, t, r, b) = (PageGeometry.MmToPoints(left), PageGeometry.MmToPoints(top), PageGeometry.MmToPoints(right), PageGeometry.MmToPoints(bottom));
                    visual = (l, t, vw - l - r, vh - t - b);
                    if (visual.Value.W < MinSide || visual.Value.H < MinSide)
                        return ctx.Fail($"Page {targets[i]}: these margins leave nothing of the page ({Size(vw, vh)}).");
                    break;
                }
                case "box":
                {
                    const double tolerance = 0.05;
                    if (box[0] + tolerance >= vw || box[1] + tolerance >= vh || box[0] + box[2] > vw + tolerance || box[1] + box[3] > vh + tolerance)
                        return ctx.Fail($"Page {targets[i]}: the box goes outside the page ({Size(vw, vh)}).");
                    visual = (box[0], box[1], Math.Min(box[2], vw - box[0]), Math.Min(box[3], vh - box[1]));
                    break;
                }
                default:
                    visual = FindContent(pdfium!.Render(targets[i] - 1, 72), vw, vh, padding);
                    break;
            }

            if (visual is null)
            {
                blank.Add(targets[i]);
                continue;
            }

            var (x1, y1, x2, y2) = geometry.UserRect(visual.Value.X, visual.Value.Y, visual.Value.W, visual.Value.H);
            (x1, y1) = (Math.Max(x1, geometry.X1), Math.Max(y1, geometry.Y1));
            (x2, y2) = (Math.Min(x2, geometry.X2), Math.Min(y2, geometry.Y2));
            if (x2 - x1 < MinSide || y2 - y1 < MinSide)
                return ctx.Fail($"Page {targets[i]}: the crop leaves nothing of the page.");

            page.CropBox = new PdfRectangle(new XPoint(Round(x1), Round(y1)), new XPoint(Round(x2), Round(y2)));
        }

        if (blank.Count == targets.Count)
            return ctx.Fail("Nothing to crop: every selected page is blank.");

        var path = ctx.ReserveFile(ctx.Input, "pdf", "cropped");
        document.Save(path);
        ctx.Report(1);
        return blank.Count == 0
            ? ctx.Ok(path)
            : ctx.OkWithNote($"Blank pages were left as they are: {string.Join(", ", blank)}.", path);
    }

    /// <summary>Bounding box of the non-white pixels, grown by <paramref name="padding"/> points and kept inside the page; null for a blank page.</summary>
    private static (double X, double Y, double W, double H)? FindContent(RenderedPage rendered, double pageWidth, double pageHeight, double padding)
    {
        int minX = rendered.Width, minY = rendered.Height, maxX = -1, maxY = -1;
        var pixels = rendered.Bgra;
        for (var y = 0; y < rendered.Height; y++)
        {
            var row = y * rendered.Width * 4;
            for (var x = 0; x < rendered.Width; x++)
            {
                var i = row + x * 4;
                if (pixels[i] >= InkThreshold && pixels[i + 1] >= InkThreshold && pixels[i + 2] >= InkThreshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return null;
        double scaleX = pageWidth / rendered.Width, scaleY = pageHeight / rendered.Height;
        var x0 = Math.Max(0, minX * scaleX - padding);
        var y0 = Math.Max(0, minY * scaleY - padding);
        var x1 = Math.Min(pageWidth, (maxX + 1) * scaleX + padding);
        var y1 = Math.Min(pageHeight, (maxY + 1) * scaleY + padding);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static double Mm(JobContext ctx, string key) => ctx.Job.GetDouble(key, 0);

    private static double Round(double points) => Math.Round(points, 3);

    private static string Size(double widthPoints, double heightPoints) =>
        string.Create(CultureInfo.InvariantCulture, $"{PageGeometry.PointsToMm(widthPoints):0.#} x {PageGeometry.PointsToMm(heightPoints):0.#} mm");
}
