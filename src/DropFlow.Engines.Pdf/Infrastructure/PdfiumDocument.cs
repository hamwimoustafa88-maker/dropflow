using System.Runtime.InteropServices;
using DropFlow.Engines.Common;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>A page drawn by PDFium: 32-bit BGRA pixels, rows top to bottom.</summary>
internal sealed record RenderedPage(int Width, int Height, byte[] Bgra, double Dpi, bool WasDownscaled);

/// <summary>
/// A PDF opened with PDFium. The native PDFium library comes from PDFtoImage's packages (bblanchon/pdfium-binaries builds for
/// win-x64/x86/arm64, linux-x64/arm64 and osx-x64/arm64), so this works on Windows, macOS and Linux. Pages are drawn by PDFtoImage;
/// page count, sizes and the owner's permission flags are read through a few direct PDFium calls (PDFtoImage does not expose them).
/// PDFium is not thread-safe, so every call into it takes the process-wide <see cref="NativeLocks.Pdfium"/>. The file is read into
/// memory first (no handle on the user's file, and Arabic/Unicode paths never go through PDFium's ANSI path API).
/// </summary>
internal sealed class PdfiumDocument : IDisposable
{
    private static readonly object Gate = NativeLocks.Pdfium;
    private static bool _initialised;

    /// <summary>Upper bound for one rendered page (width x height). Larger pages are rendered at a lower DPI instead of exhausting memory.</summary>
    public const long MaxPixels = 80_000_000;

    private readonly byte[] _bytes;
    private readonly string? _password;
    private readonly IntPtr _document;
    private GCHandle _pin;
    private bool _disposed;

    private PdfiumDocument(byte[] bytes, string? password, IntPtr document, GCHandle pin, int pageCount, uint permissions)
    {
        _bytes = bytes;
        _password = password;
        _document = document;
        _pin = pin;
        PageCount = pageCount;
        Permissions = permissions;
    }

    public int PageCount { get; }

    /// <summary>The /P permission bits seen by a user who opened the file (0xFFFFFFFF for files that are not encrypted).</summary>
    private uint Permissions { get; }

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
                var doc = Native.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
                if (doc == IntPtr.Zero)
                {
                    var error = (ulong)Native.FPDF_GetLastError().Value;
                    pin.Free();
                    throw new ConversionFailure(error == 4 // FPDF_ERR_PASSWORD
                        ? $"{name} is protected with a password. Unlock it first (Unlock PDF), then try again."
                        : $"{name} is not a valid PDF or is damaged (PDFium error {error}). Try Repair PDF first.");
                }

                var count = Native.FPDF_GetPageCount(doc);
                var permissions = unchecked((uint)Native.FPDF_GetDocPermissions(doc).Value);
                return new PdfiumDocument(bytes, password, doc, pin, count, permissions);
            }
        }
        catch
        {
            if (pin.IsAllocated) pin.Free();
            throw;
        }
    }

    /// <summary>False when the owner of an encrypted PDF forbade inserting, rotating or deleting pages (permission bit 11 of the /P entry).</summary>
    public bool AllowsAssembly => (Permissions & 0x400u) != 0;

    /// <summary>False when the owner of an encrypted PDF forbade copying text and graphics out of it (permission bit 5).</summary>
    public bool AllowsExtraction => (Permissions & 0x10u) != 0;

    /// <summary>Page size in PDF points (1/72 inch), after the page's own rotation.</summary>
    public (double Width, double Height) PageSize(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= PageCount) throw new ConversionFailure($"Page {index + 1} could not be read.");
        lock (Gate)
        {
            if (Native.FPDF_GetPageSizeByIndex(_document, index, out var width, out var height) == 0)
                throw new ConversionFailure($"Page {index + 1} could not be read.");
            return (width, height);
        }
    }

    /// <summary>Renders a page onto a white background at the requested DPI (reduced if the bitmap would exceed <see cref="MaxPixels"/>).</summary>
    public RenderedPage Render(int index, double dpi)
    {
        var (wPts, hPts) = PageSize(index);
        var (width, height, effectiveDpi, reduced) = FitBitmap(wPts, hPts, dpi);
        var options = new RenderOptions(
            Width: width, Height: height, WithAnnotations: true, WithFormFill: false, WithAspectRatio: false,
            Rotation: PdfRotation.Rotate0, AntiAliasing: PdfAntiAliasing.All, BackgroundColor: SKColors.White);

        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            throw new ConversionFailure("Drawing PDF pages is supported on Windows, macOS and Linux only.");

        lock (Gate)
        {
            SKBitmap bitmap;
            try
            {
#pragma warning disable CA1416 // guarded above: Windows, Linux and macOS are exactly the platforms PDFtoImage supports here
                bitmap = Conversion.ToImage(_bytes, page: index, password: _password, options: options);
#pragma warning restore CA1416
            }
            catch (PdfException ex)
            {
                throw new ConversionFailure($"Page {index + 1} could not be drawn ({ex.Message}).");
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException)
            {
                throw new ConversionFailure("Not enough memory to draw the page. Try a lower DPI.");
            }

            using (bitmap)
                return new RenderedPage(bitmap.Width, bitmap.Height, ToBgra(bitmap), effectiveDpi, reduced);
        }
    }

    /// <summary>Tight BGRA rows (no padding), whatever the bitmap's own byte order.</summary>
    private static byte[] ToBgra(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height, rowBytes = bitmap.RowBytes;
        var source = bitmap.GetPixelSpan();
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            source.Slice(y * rowBytes, width * 4).CopyTo(pixels.AsSpan(y * width * 4, width * 4));

        if (bitmap.ColorType == SKColorType.Rgba8888)
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        else if (bitmap.ColorType != SKColorType.Bgra8888)
            throw new ConversionFailure($"PDFium returned an unexpected pixel format ({bitmap.ColorType}).");
        return pixels;
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
            Native.FPDF_CloseDocument(_document);
        }
        if (_pin.IsAllocated) _pin.Free();
    }

    private static void Initialise()
    {
        if (_initialised) return;
        Native.FPDF_InitLibrary(); // PDFium ignores a second initialisation, so PDFtoImage's own call later is harmless
        _initialised = true;
    }

    /// <summary>The handful of PDFium C functions DropFlow calls itself (public PDFium API, <c>fpdfview.h</c>).</summary>
    private static class Native
    {
        private const string Library = "pdfium";

        [DllImport(Library)]
        public static extern void FPDF_InitLibrary();

        [DllImport(Library)]
        public static extern IntPtr FPDF_LoadMemDocument(IntPtr dataBuffer, int size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

        [DllImport(Library)]
        public static extern void FPDF_CloseDocument(IntPtr document);

        // "unsigned long" is 32-bit on Windows and 64-bit on macOS/Linux: CULong matches both.
        [DllImport(Library)]
        public static extern CULong FPDF_GetLastError();

        [DllImport(Library)]
        public static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(Library)]
        public static extern CULong FPDF_GetDocPermissions(IntPtr document);

        [DllImport(Library)]
        public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int pageIndex, out double width, out double height);
    }
}
