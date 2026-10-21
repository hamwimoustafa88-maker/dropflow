using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// <c>img.to-pdf</c> (many pictures into one PDF) and <c>convert:pdf</c> for a single picture. One page per picture, sized to the picture.
/// JPEG files are embedded untouched; every other format is decoded with Magick.NET and embedded losslessly (PNG) or, for photos that
/// came from a lossy format without transparency, as JPEG.
/// </summary>
internal static class ImageToPdfTools
{
    /// <summary>
    /// Extensions this module can turn into PDF pages. HEIC (phone photos) is decoded by Magick.NET's bundled libheif - nothing to
    /// install; it is verified against a real HEVC HEIC file (tests/samples/heic).
    /// </summary>
    public static readonly string[] Inputs = ["jpg", "jpeg", "png", "bmp", "gif", "tif", "tiff", "webp", "ico", "avif", "svg", "heic"];

    public static readonly IReadOnlyList<ToolOption> Options =
    [
        new("pageSize", ToolOptionKind.Choice, "image", ["image", "a4", "letter"]),
    ];

    /// <summary>PDF viewers refuse pages larger than 200 inches.</summary>
    private const double MaxPageSide = 14400;
    private const double FixedPageMargin = 36;

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return new OperationSpec("img.to-pdf", Inputs, ctx => Task.FromResult(Build(ctx, group: true)), Options);
        yield return new OperationSpec("convert:pdf", Inputs, ctx => Task.FromResult(Build(ctx, group: false)));
    }

    private static ConversionResult Build(JobContext ctx, bool group)
    {
        var inputs = ctx.Job.Inputs;
        foreach (var input in inputs)
        {
            var ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
            if (!Inputs.Contains(ext)) return ctx.Fail($"{Path.GetFileName(input)} is not a picture format DropFlow can put in a PDF.");
        }

        var pageSize = (group ? ctx.Job.GetString("pageSize", "image") : "image")?.ToLowerInvariant() ?? "image";
        if (pageSize is not ("image" or "a4" or "letter")) return ctx.Fail($"Unknown page size '{pageSize}'.");

        using var document = new PdfDocument();
        PdfSharpSupport.ApplyDefaultOptions(document);
        var images = new List<XImage>();
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report((double)i / inputs.Count, Path.GetFileName(inputs[i]));
                foreach (var frame in LoadFrames(inputs[i]))
                {
                    images.Add(frame.Image);
                    AddPage(document, frame, pageSize);
                }
            }

            if (document.PageCount == 0) return ctx.Fail("There were no pictures to add.");
            var path = group ? ctx.ReserveGroupFile("pdf", "converted") : ctx.ReserveFile(ctx.Input, "pdf", "converted");
            document.Save(path);
            ctx.Report(1);
            return ctx.Ok(path);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    private static void AddPage(PdfDocument document, PictureFrame frame, string pageSize)
    {
        var page = document.AddPage();
        double imageW = frame.WidthPoints, imageH = frame.HeightPoints;

        if (pageSize == "image")
        {
            page.Width = XUnitPt.FromPoint(imageW);
            page.Height = XUnitPt.FromPoint(imageH);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(frame.Image, 0, 0, imageW, imageH);
            return;
        }

        var (w, h) = pageSize == "a4" ? (595.276, 841.89) : (612.0, 792.0);
        if (imageW > imageH) (w, h) = (h, w); // landscape pictures get landscape pages
        page.Width = XUnitPt.FromPoint(w);
        page.Height = XUnitPt.FromPoint(h);

        var scale = Math.Min((w - 2 * FixedPageMargin) / imageW, (h - 2 * FixedPageMargin) / imageH);
        var drawW = imageW * scale;
        var drawH = imageH * scale;
        using var fixedGfx = XGraphics.FromPdfPage(page);
        fixedGfx.DrawImage(frame.Image, (w - drawW) / 2, (h - drawH) / 2, drawW, drawH);
    }

    private sealed record PictureFrame(XImage Image, double WidthPoints, double HeightPoints);

    private static IEnumerable<PictureFrame> LoadFrames(string path)
    {
        var name = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);

        // ICO has no signature Magick can sniff, so the extension tells it what to expect.
        var settings = new MagickReadSettings();
        if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase)) settings.Format = MagickFormat.Ico;

        MagickImageInfo info;
        try
        {
            info = new MagickImageInfo(bytes, settings);
        }
        catch (MagickException ex)
        {
            throw new ConversionFailure($"{name} could not be read as a picture ({ex.Message}).");
        }

        var upright = info.Orientation is Orientation.Undefined or Orientation.TopLeft;
        var cmyk = info.ColorSpace == ColorSpace.CMYK;

        // Fast path: a plain JPEG goes into the PDF byte for byte.
        if (info.Format == MagickFormat.Jpeg && upright && !cmyk)
        {
            var (w, h) = PointSize(info.Width, info.Height, info.Density ?? new Density(0.0, 0.0));
            yield return new PictureFrame(XImage.FromStream(new MemoryStream(bytes)), w, h);
            yield break;
        }

        MagickImageCollection frames;
        try
        {
            frames = info.Format == MagickFormat.Tiff
                ? new MagickImageCollection(bytes, settings) // scans are often multi-page TIFFs
                : new MagickImageCollection([new MagickImage(bytes, settings)]);
        }
        catch (MagickException ex)
        {
            throw new ConversionFailure($"{name} could not be decoded ({ex.Message}).");
        }

        using (frames)
        {
            foreach (var frame in frames)
            {
                frame.AutoOrient();
                if (frame.ColorSpace == ColorSpace.CMYK)
                {
                    // Use the picture's own profile when it has one, otherwise a standard print profile, so the colours stay plausible.
                    if (frame.GetColorProfile() is null) frame.TransformColorSpace(ColorProfiles.CoatedFOGRA39, ColorProfiles.SRGB);
                    else frame.TransformColorSpace(ColorProfiles.SRGB);
                }
                frame.Depth = 8;

                var (w, h) = PointSize(frame.Width, frame.Height, frame.Density);
                var lossySource = info.Format is MagickFormat.Jpeg or MagickFormat.WebP or MagickFormat.Avif or MagickFormat.Heic or MagickFormat.Heif;
                var asJpeg = lossySource && !frame.HasAlpha;

                // PDFsharp only imports plain 8-bit RGB / RGBA PNGs, so force that layout (Magick would otherwise pick gray or palette).
                frame.Format = asJpeg ? MagickFormat.Jpeg : frame.HasAlpha ? MagickFormat.Png32 : MagickFormat.Png24;
                if (asJpeg) frame.Quality = 92;
                var encoded = frame.ToByteArray();
                yield return new PictureFrame(XImage.FromStream(new MemoryStream(encoded)), w, h);
            }
        }
    }

    /// <summary>Page size in points: 72 pixels per inch unless the file declares a believable resolution.</summary>
    internal static (double Width, double Height) PointSize(uint widthPx, uint heightPx, Density density)
    {
        double dpiX = 72, dpiY = 72;
        if (density.X > 0 && density.Y > 0)
        {
            var factor = density.Units == DensityUnit.PixelsPerCentimeter ? 2.54 : 1.0;
            var x = density.X * factor;
            var y = density.Y * factor;
            if (x is >= 36 and <= 2400 && y is >= 36 and <= 2400) (dpiX, dpiY) = (x, y);
        }

        var w = widthPx * 72.0 / dpiX;
        var h = heightPx * 72.0 / dpiY;
        var shrink = Math.Min(1.0, MaxPageSide / Math.Max(w, h));
        return (Math.Max(1, w * shrink), Math.Max(1, h * shrink));
    }
}
