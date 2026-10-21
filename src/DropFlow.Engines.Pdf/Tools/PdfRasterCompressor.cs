using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// "Extreme" compression: every page is drawn with PDFium, saved as a JPEG and the PDF is rebuilt from those pictures at the original
/// page size. Very small, but text, links and bookmarks are lost (the pages become pictures).
/// </summary>
internal static class PdfRasterCompressor
{
    public const double Dpi = 100;
    public const int Quality = 55;

    public static void Rebuild(string inputPath, string outputPath, CancellationToken ct, Action<double>? progress = null)
    {
        using var source = PdfiumDocument.Open(inputPath);
        if (source.PageCount < 1) throw new ConversionFailure("The PDF has no pages.");

        using var output = new PdfDocument();
        PdfSharpSupport.ApplyDefaultOptions(output);
        var images = new List<XImage>();
        try
        {
            for (var i = 0; i < source.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((double)i / source.PageCount);

                var (widthPoints, heightPoints) = source.PageSize(i);
                var rendered = source.Render(i, Dpi);
                var jpeg = RasterEncoder.ToJpeg(rendered, Quality, Dpi);

                var page = output.AddPage();
                page.Width = XUnitPt.FromPoint(widthPoints);
                page.Height = XUnitPt.FromPoint(heightPoints);
                var image = XImage.FromStream(new MemoryStream(jpeg));
                images.Add(image);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, 0, 0, widthPoints, heightPoints);
            }

            output.Save(outputPath);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }
}
