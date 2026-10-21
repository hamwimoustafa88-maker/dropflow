using System.Globalization;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>Text stamped on every page: watermark and page numbers (PDFsharp drawing, Windows fonts).</summary>
internal static class PdfStampTools
{
    public static readonly string[] Positions =
        ["bottom-center", "bottom-right", "bottom-left", "top-center", "top-right", "top-left"];

    public static readonly IReadOnlyList<ToolOption> WatermarkOptions =
    [
        new("text", ToolOptionKind.Text, "CONFIDENTIAL", Required: true),
        new("opacity", ToolOptionKind.Number, 0.25, Min: 0.05, Max: 1),
        new("angle", ToolOptionKind.Number, 45, Min: -180, Max: 180),
        new("size", ToolOptionKind.Number, 0, Min: 0, Max: 400),
        new("color", ToolOptionKind.Text, "#808080"),
        new("pages", ToolOptionKind.Range, ""),
    ];

    public static readonly IReadOnlyList<ToolOption> PageNumberOptions =
    [
        new("position", ToolOptionKind.Choice, "bottom-center", Positions),
        new("start", ToolOptionKind.Number, 1, Min: 0, Max: 100000),
        new("format", ToolOptionKind.Text, "{n}"),
        new("numerals", ToolOptionKind.Choice, "western", ["western", "arabic-indic"]),
        new("size", ToolOptionKind.Number, 11, Min: 6, Max: 48),
    ];

    private static readonly string[] FontFamilies = ["Arial", "Tahoma", "Segoe UI", "Verdana"];
    private static string? _family;

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.watermark", "pdf", ctx => Task.FromResult(Watermark(ctx)), WatermarkOptions);
        yield return OperationSpec.For("pdf.page-numbers", "pdf", ctx => Task.FromResult(PageNumbers(ctx)), PageNumberOptions);
    }

    // ---- watermark ------------------------------------------------------------------------------------------------------

    private static ConversionResult Watermark(JobContext ctx)
    {
        var text = ctx.Job.GetString("text")?.Trim();
        if (string.IsNullOrEmpty(text)) return ctx.Fail("Enter the watermark text.");
        var opacity = ctx.Job.GetDouble("opacity", 0.25);
        if (opacity > 1) opacity /= 100; // accept 25 as "25 %"
        opacity = Math.Clamp(opacity, 0.05, 1);
        var angle = ctx.Job.GetDouble("angle", 45);
        var requestedSize = ctx.Job.GetDouble("size", 0);
        var color = ParseColor(ctx.Job.GetString("color"), XColor.FromArgb(128, 128, 128));

        PdfSharpSupport.EnsureFonts();
        using var document = PdfSharpSupport.OpenForModify(ctx.Input);
        if (!PageSpec.TryParse(ctx.Job.GetString("pages"), document.PageCount, out var pages, out var error, allowBlank: true))
            return ctx.Fail(error);

        var visual = ArabicShaper.ToVisual(text);
        var brush = new XSolidBrush(XColor.FromArgb((int)Math.Round(opacity * 255), color.R, color.G, color.B));
        var radians = angle * Math.PI / 180;
        var targets = pages.Select(p => p.Page).Distinct().ToList();

        for (var i = 0; i < targets.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / targets.Count);
            var page = document.Pages[targets[i] - 1];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            WithVisualSpace(gfx, page, (vw, vh) =>
            {
                var size = requestedSize > 0 ? requestedSize : FitWatermarkSize(gfx, visual, vw, vh, radians);
                var font = CreateFont(size, bold: true);
                gfx.TranslateTransform(vw / 2, vh / 2);
                gfx.RotateTransform(-angle); // PDFsharp rotates clockwise; users expect counter-clockwise
                gfx.DrawString(visual, font, brush, new XPoint(0, 0), XStringFormats.Center);
            });
        }

        var path = ctx.ReserveFile(ctx.Input, "pdf", "watermarked");
        document.Save(path);
        ctx.Report(1);
        return ctx.Ok(path);
    }

    private static double FitWatermarkSize(XGraphics gfx, string text, double vw, double vh, double radians)
    {
        // Longest line that fits in the page along the chosen direction, then scale the font so the text is 75 % of it.
        var cos = Math.Max(Math.Abs(Math.Cos(radians)), 1e-6);
        var sin = Math.Max(Math.Abs(Math.Sin(radians)), 1e-6);
        var available = Math.Min(vw / cos, vh / sin) * 0.75;
        var width100 = gfx.MeasureString(text, CreateFont(100, bold: true)).Width;
        if (width100 <= 0) return 48;
        return Math.Clamp(available / width100 * 100, 8, 250);
    }

    // ---- page numbers ---------------------------------------------------------------------------------------------------

    private static ConversionResult PageNumbers(JobContext ctx)
    {
        var position = (ctx.Job.GetString("position", "bottom-center") ?? "bottom-center").ToLowerInvariant();
        if (!Positions.Contains(position)) return ctx.Fail($"Unknown position '{position}'.");
        var start = ctx.Job.GetInt("start", 1);
        var format = ctx.Job.GetString("format");
        if (string.IsNullOrWhiteSpace(format)) format = "{n}";
        var arabicDigits = string.Equals(ctx.Job.GetString("numerals"), "arabic-indic", StringComparison.OrdinalIgnoreCase);
        var size = ctx.Job.GetDouble("size", 11);

        PdfSharpSupport.EnsureFonts();
        using var document = PdfSharpSupport.OpenForModify(ctx.Input);
        var total = document.PageCount;
        var font = CreateFont(size, bold: false);
        var brush = XBrushes.Black;
        const double margin = 28;

        for (var i = 0; i < total; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / total);
            var label = FormatLabel(format, start + i, total, arabicDigits);
            var visual = ArabicShaper.ToVisual(label);
            var page = document.Pages[i];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            WithVisualSpace(gfx, page, (vw, vh) =>
            {
                var textWidth = gfx.MeasureString(visual, font).Width;
                var x = position.EndsWith("left", StringComparison.Ordinal) ? margin
                    : position.EndsWith("right", StringComparison.Ordinal) ? vw - margin - textWidth
                    : (vw - textWidth) / 2;
                var y = position.StartsWith("top", StringComparison.Ordinal) ? margin + size : vh - margin;
                gfx.DrawString(visual, font, brush, new XPoint(x, y), XStringFormats.BaseLineLeft);
            });
        }

        var path = ctx.ReserveFile(ctx.Input, "pdf", "numbered");
        document.Save(path);
        ctx.Report(1);
        return ctx.Ok(path);
    }

    internal static string FormatLabel(string format, int number, int total, bool arabicIndicDigits)
    {
        var label = format
            .Replace("{n}", number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{total}", total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (!arabicIndicDigits) return label;
        return string.Concat(label.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c));
    }

    // ---- shared ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="draw"/> in "visual" coordinates: origin at the top-left of the page as the reader sees it, even when the
    /// page carries a /Rotate entry (PDFsharp draws in the unrotated page space).
    /// </summary>
    private static void WithVisualSpace(XGraphics gfx, PdfPage page, Action<double, double> draw)
    {
        double w = page.Width.Point, h = page.Height.Point;
        var rotate = ((page.Rotate % 360) + 360) % 360;
        var (vw, vh) = rotate is 90 or 270 ? (h, w) : (w, h);

        var state = gfx.Save();
        gfx.TranslateTransform(w / 2, h / 2);
        if (rotate != 0) gfx.RotateTransform(-rotate);
        gfx.TranslateTransform(-vw / 2, -vh / 2);
        draw(vw, vh);
        gfx.Restore(state);
    }

    private static XFont CreateFont(double size, bool bold)
    {
        var style = bold ? XFontStyleEx.Bold : XFontStyleEx.Regular;
        if (_family is not null) return new XFont(_family, size, style);

        Exception? last = null;
        foreach (var family in FontFamilies)
        {
            try
            {
                var font = new XFont(family, size, style);
                _family = family;
                return font;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new ConversionFailure($"No usable font was found on this computer ({last?.Message}).");
    }

    internal static XColor ParseColor(string? text, XColor fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return fallback;
        return XColor.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}
