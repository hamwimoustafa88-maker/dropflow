using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Rendering;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// SVG/SVGZ → a <b>vector</b> PDF through the app's browser engine: the drawing stays sharp at any zoom and its text stays text. The SVG is
/// shown as an &lt;img&gt; (a data: address), so it can run no script and load nothing from the network; the page is exactly the
/// drawing's size, with no margins. Without a browser engine (the CLI, tests) SVG → PDF stays the picture path in ImageToPdfTools.
/// No extra library: the SVG libraries for .NET depend on MS-PL code, which the GPL does not allow (docs/deferred.md E2).
/// </summary>
internal sealed class SvgPdf(IHtmlPdfRenderer renderer)
{
    public static readonly string[] Inputs = ["svg", "svgz"];

    /// <summary>Default size when the SVG names none (CSS pixels).</summary>
    private const double DefaultWidth = 800, DefaultHeight = 600;

    public IEnumerable<OperationSpec> Specs()
    {
        yield return new OperationSpec("convert:pdf", Inputs, RunAsync);
    }

    private async Task<ConversionResult> RunAsync(JobContext ctx)
    {
        byte[] svg;
        try { svg = Read(ctx.Input); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return ctx.Fail($"{Path.GetFileName(ctx.Input)} is not a readable SVG: {ex.Message}"); }

        var (width, height) = Size(svg);
        var html = ctx.TempFile("svg.html");
        File.WriteAllText(html, Page(svg, width, height), Encoding.UTF8);
        var pdf = ctx.TempFile("svg.pdf");
        var error = await renderer.PrintAsync(html, pdf, new HtmlPdfOptions(PageWidth: width / 96.0, PageHeight: height / 96.0, NoMargins: true),
            new Progress<JobProgress>(p => ctx.Report(0.1 + p.Fraction * 0.8, p.Message)), ctx.Ct).ConfigureAwait(false);
        if (error is not null) return ctx.Fail($"{Path.GetFileName(ctx.Input)}: {error}");
        var path = ctx.ReserveFile(ctx.Input, "pdf", "converted");
        File.Move(pdf, path, overwrite: true);
        ctx.Report(1);
        return ctx.Ok(path);
    }

    /// <summary>The SVG's bytes (SVGZ unpacked, at most 64 MB).</summary>
    internal static byte[] Read(string path)
    {
        using var file = File.OpenRead(path);
        var gz = file.ReadByte() == 0x1F && file.ReadByte() == 0x8B;
        file.Position = 0;
        using Stream source = gz ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > 64L * 1024 * 1024) throw new InvalidDataException("the drawing is larger than 64 MB");
        }
        return buffer.ToArray();
    }

    /// <summary>The page HTML: one image filling a page of the drawing's size.</summary>
    internal static string Page(byte[] svg, double width, double height)
    {
        var w = width.ToString("0.###", CultureInfo.InvariantCulture);
        var h = height.ToString("0.###", CultureInfo.InvariantCulture);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>"
            + $"@page{{size:{w}px {h}px;margin:0}}html,body{{margin:0;padding:0;background:transparent}}img{{display:block;width:{w}px;height:{h}px}}"
            + "</style></head><body><img alt=\"\" src=\"data:image/svg+xml;base64," + Convert.ToBase64String(svg) + "\"></body></html>";
    }

    /// <summary>The drawing's size in CSS pixels from width/height (px, pt, mm, cm, in) or the viewBox; 800×600 when it names none.</summary>
    internal static (double Width, double Height) Size(byte[] svg)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName != "svg") break;
                double? w = Length(reader.GetAttribute("width")), h = Length(reader.GetAttribute("height"));
                var box = reader.GetAttribute("viewBox")?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                var hasBox = box is { Length: 4 } && box[2] > 0 && box[3] > 0 && box.All(double.IsFinite);
                if (w is null && h is null && hasBox) (w, h) = (box![2], box[3]);
                else if (w is null && h is not null && hasBox) w = h * box![2] / box[3];
                else if (h is null && w is not null && hasBox) h = w * box![3] / box[2];
                return (Clamp(w ?? DefaultWidth), Clamp(h ?? DefaultHeight));
            }
        }
        catch (XmlException) { /* not well-formed: the browser decides; default page */ }
        return (DefaultWidth, DefaultHeight);
    }

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 1, 14400 * 96.0 / 72.0) : DefaultWidth;

    private static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimEnd().EndsWith('%')) return null;
        var t = text.Trim();
        var unit = new string(t.SkipWhile(c => char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E').ToArray()).Trim().ToLowerInvariant();
        var number = t[..(t.Length - unit.Length)].Trim();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return null;
        return unit switch
        {
            "" or "px" => v,
            "pt" => v * 96 / 72,
            "pc" => v * 16,
            "mm" => v * 96 / 25.4,
            "cm" => v * 96 / 2.54,
            "in" => v * 96,
            _ => null,
        };
    }
}
