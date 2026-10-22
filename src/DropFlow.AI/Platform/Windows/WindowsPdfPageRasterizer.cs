using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DropFlow.AI.Ocr;

/// <summary>Page renderer built into Windows (<c>Windows.Data.Pdf</c>). Works with Unicode paths; cannot open password-protected files.</summary>
public sealed class WindowsPdfPageRasterizer : IPageRasterizer
{
    /// <summary>Longest side of a rendered page; keeps a poster-sized page from needing gigabytes.</summary>
    public const int MaxPixelsPerSide = 9000;

    public async Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The stream is opened (and closed) by us. LoadFromFileAsync would keep the file open until the garbage collector ran,
        // which would stop the user from renaming or deleting their PDF while DropFlow is still running.
        IRandomAccessStreamWithContentType stream;
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(cancellationToken).ConfigureAwait(false);
            stream = await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            throw new PdfRenderException($"The PDF could not be opened: {ex.Message}", ex);
        }

        using (stream)
        {
            try
            {
                document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // Windows reports a wrong or missing password, and damage, as a generic COM failure.
                throw new PdfRenderException("Windows could not open the PDF (it may be password-protected or damaged).", ex);
            }
            return await RenderAllAsync(document, outputDirectory, dpi, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> RenderAllAsync(PdfDocument document, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (document.IsPasswordProtected) throw new PdfRenderException("The PDF is password-protected; unlock it first.");
        if (document.PageCount == 0) throw new PdfRenderException("The PDF has no pages.");

        Directory.CreateDirectory(outputDirectory);
        var paths = new List<string>();
        for (uint i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.GetPage(i);

            // page.Size is in 1/96 inch units.
            var scale = dpi / 96.0;
            var width = page.Size.Width * scale;
            var height = page.Size.Height * scale;
            var limit = Math.Max(width, height);
            if (limit > MaxPixelsPerSide) { width *= MaxPixelsPerSide / limit; height *= MaxPixelsPerSide / limit; }

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
                BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 },
            };
            using var stream = new InMemoryRandomAccessStream();
            try
            {
                await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (COMException ex)
            {
                throw new PdfRenderException($"Windows could not render page {i + 1}: {ex.Message}", ex);
            }

            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
                reader.ReadBytes(bytes);
            }
            var path = Path.Combine(outputDirectory, string.Create(CultureInfo.InvariantCulture, $"page-{i + 1:0000}.png"));
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            paths.Add(path);
            progress?.Report((i + 1.0) / document.PageCount);
        }
        return paths;
    }
}
