using System.Globalization;
using DropFlow.Engines.Common;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

// PDFtoImage is marked for Windows, Linux, macOS (and mobile/browser); RenderPagesAsync refuses any other platform first.
#pragma warning disable CA1416

namespace DropFlow.AI.Ocr;

/// <summary>
/// Page renderer backed by PDFium (through PDFtoImage, whose packages carry PDFium for Windows, macOS and Linux on x64 and arm64).
/// The default for <c>pdf.ocr</c> on macOS and Linux; usable on Windows too. Every PDFium call holds <see cref="NativeLocks.Pdfium"/>,
/// the lock the PDF engine uses, because PDFium must not run on two threads at once.
/// </summary>
public sealed class PdfiumPageRasterizer : IPageRasterizer
{
    /// <summary>Longest side of a rendered page; keeps a poster-sized page from needing gigabytes.</summary>
    public const int MaxPixelsPerSide = 9000;

    public async Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            throw new PdfRenderException("PDF pages can be drawn on Windows, macOS and Linux only.");

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfRenderException($"The PDF could not be opened: {ex.Message}", ex);
        }
        if (bytes.Length == 0) throw new PdfRenderException("The PDF is empty.");

        IList<System.Drawing.SizeF> sizes;
        lock (NativeLocks.Pdfium)
            sizes = Guard(() => Conversion.GetPageSizes(bytes, password: null).ToList());
        if (sizes.Count == 0) throw new PdfRenderException("The PDF has no pages.");

        Directory.CreateDirectory(outputDirectory);
        var paths = new List<string>();
        for (var i = 0; i < sizes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (width, height) = PixelSize(sizes[i].Width, sizes[i].Height, dpi);
            var options = new RenderOptions(Width: width, Height: height, WithAnnotations: true, WithAspectRatio: false, BackgroundColor: SKColors.White);
            var path = Path.Combine(outputDirectory, string.Create(CultureInfo.InvariantCulture, $"page-{i + 1:0000}.png"));
            var page = i;
            await Task.Run(() =>
            {
                lock (NativeLocks.Pdfium)
                    Guard(() => { Conversion.SavePng(path, bytes, page, password: null, options); return 0; });
            }, cancellationToken).ConfigureAwait(false);
            paths.Add(path);
            progress?.Report((i + 1.0) / sizes.Count);
        }
        return paths;
    }

    /// <summary>Pixels for a page of <paramref name="widthPoints"/> x <paramref name="heightPoints"/> (1/72 inch) at <paramref name="dpi"/>, capped at <see cref="MaxPixelsPerSide"/>.</summary>
    public static (int Width, int Height) PixelSize(double widthPoints, double heightPoints, int dpi)
    {
        var width = widthPoints * dpi / 72.0;
        var height = heightPoints * dpi / 72.0;
        var limit = Math.Max(width, height);
        if (limit > MaxPixelsPerSide) { width *= MaxPixelsPerSide / limit; height *= MaxPixelsPerSide / limit; }
        return ((int)Math.Max(1, Math.Round(width)), (int)Math.Max(1, Math.Round(height)));
    }

    private static T Guard<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (PdfPasswordProtectedException ex)
        {
            throw new PdfRenderException("The PDF is password-protected; unlock it first.", ex);
        }
        catch (PdfException ex)
        {
            throw new PdfRenderException("The PDF could not be opened (it may be damaged): " + ex.Message, ex);
        }
    }
}
