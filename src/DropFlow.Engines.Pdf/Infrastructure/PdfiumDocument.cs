using System.Runtime.InteropServices;
using PDFiumCore;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>A page drawn by PDFium: 32-bit BGRA pixels, rows top to bottom.</summary>
internal sealed record RenderedPage(int Width, int Height, byte[] Bgra, double Dpi, bool WasDownscaled);

/// <summary>
/// A PDF opened with PDFium for rendering. PDFium is not thread-safe, so every call into it takes one process-wide lock. The file is
/// read into memory first (no handle on the user's file, and Arabic/Unicode paths never go through PDFium's ANSI path API).
/// </summary>
internal sealed class PdfiumDocument : IDisposable
{
    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>Upper bound for one rendered page (width x height). Larger pages are rendered at a lower DPI instead of exhausting memory.</summary>
    public const long MaxPixels = 80_000_000;

    private readonly FpdfDocumentT _document;
    private readonly GCHandle _pin;
    private bool _disposed;

    private PdfiumDocument(FpdfDocumentT document, GCHandle pin, int pageCount)
    {
        _document = document;
        _pin = pin;
        PageCount = pageCount;
    }

    public int PageCount { get; }

    public static PdfiumDocument Open(string path, string? password = null)
    {
        var name = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            lock (Gate)
            {
                Initialise();
                var doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
                if (doc is null)
                {
                    var error = fpdfview.FPDF_GetLastError();
                    pin.Free();
                    throw new ConversionFailure(error == 4 // FPDF_ERR_PASSWORD
                        ? $"{name} is protected with a password. Unlock it first (Unlock PDF), then try again."
                        : $"{name} is not a valid PDF or is damaged (PDFium error {error}). Try Repair PDF first.");
                }

                var count = fpdfview.FPDF_GetPageCount(doc);
                return new PdfiumDocument(doc, pin, count);
            }
        }
        catch
        {
            if (pin.IsAllocated) pin.Free();
            throw;
        }
    }

    /// <summary>False when the owner of an encrypted PDF forbade inserting, rotating or deleting pages (permission bit 11 of the /P entry).</summary>
    public bool AllowsAssembly
    {
        get
        {
            lock (Gate) return (fpdfview.FPDF_GetDocPermissions(_document) & 0x400UL) != 0; // 0xFFFFFFFF for files that are not encrypted
        }
    }

    /// <summary>False when the owner of an encrypted PDF forbade copying text and graphics out of it (permission bit 5).</summary>
    public bool AllowsExtraction
    {
        get
        {
            lock (Gate) return (fpdfview.FPDF_GetDocPermissions(_document) & 0x10UL) != 0;
        }
    }

    /// <summary>Page size in PDF points (1/72 inch), after the page's own rotation.</summary>
    public (double Width, double Height) PageSize(int index)
    {
        lock (Gate)
        {
            var page = fpdfview.FPDF_LoadPage(_document, index);
            if (page is null) throw new ConversionFailure($"Page {index + 1} could not be read.");
            try
            {
                return (fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page));
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Renders a page onto a white background at the requested DPI (reduced if the bitmap would exceed <see cref="MaxPixels"/>).</summary>
    public RenderedPage Render(int index, double dpi)
    {
        lock (Gate)
        {
            var page = fpdfview.FPDF_LoadPage(_document, index);
            if (page is null) throw new ConversionFailure($"Page {index + 1} could not be read.");
            try
            {
                double wPts = fpdfview.FPDF_GetPageWidthF(page), hPts = fpdfview.FPDF_GetPageHeightF(page);
                var (width, height, effectiveDpi, reduced) = FitBitmap(wPts, hPts, dpi);

                var bitmap = fpdfview.FPDFBitmapCreate(width, height, 1); // 1 = has alpha (BGRA)
                if (bitmap is null) throw new ConversionFailure("Not enough memory to draw the page. Try a lower DPI.");
                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    const int renderAnnotations = 0x01;
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, renderAnnotations);

                    var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                    var scan0 = fpdfview.FPDFBitmapGetBuffer(bitmap);
                    var pixels = new byte[width * height * 4];
                    for (var y = 0; y < height; y++)
                        Marshal.Copy(scan0 + y * stride, pixels, y * width * 4, width * 4);
                    return new RenderedPage(width, height, pixels, effectiveDpi, reduced);
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public static (int Width, int Height, double Dpi, bool Reduced) FitBitmap(double widthPoints, double heightPoints, double dpi)
    {
        dpi = Math.Clamp(dpi, 10, 1200);
        var scale = dpi / 72.0;
        var width = Math.Max(1, (int)Math.Round(widthPoints * scale));
        var height = Math.Max(1, (int)Math.Round(heightPoints * scale));
        var reduced = false;
        if ((long)width * height > MaxPixels)
        {
            var shrink = Math.Sqrt((double)MaxPixels / ((double)width * height));
            width = Math.Max(1, (int)(width * shrink));
            height = Math.Max(1, (int)(height * shrink));
            dpi *= shrink;
            reduced = true;
        }
        return (width, height, dpi, reduced);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (Gate)
        {
            fpdfview.FPDF_CloseDocument(_document);
        }
        if (_pin.IsAllocated) _pin.Free();
    }

    private static void Initialise()
    {
        if (_initialised) return;
        fpdfview.FPDF_InitLibrary();
        _initialised = true;
    }
}
