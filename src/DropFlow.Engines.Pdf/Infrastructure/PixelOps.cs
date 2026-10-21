using ImageMagick;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>
/// An axis-aligned rectangle on a page as the reader sees it: points, origin bottom-left, y up (the same space PdfPig reports letters in).
/// </summary>
internal readonly record struct VisualBox(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;

    public double Height => Y1 - Y0;

    public VisualBox Union(VisualBox other) => new(Math.Min(X0, other.X0), Math.Min(Y0, other.Y0), Math.Max(X1, other.X1), Math.Max(Y1, other.Y1));

    public VisualBox Expand(double by) => new(X0 - by, Y0 - by, X1 + by, Y1 + by);
}

/// <summary>Plain operations on the 32-bit BGRA pixels PDFium produces: burn a rectangle, pixelate it, encode a page picture.</summary>
internal static class PixelOps
{
    /// <summary>Pixel rectangle [X0, X1) x [Y0, Y1), already clipped to the bitmap.</summary>
    public readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
    {
        public int Width => X1 - X0;

        public int Height => Y1 - Y0;

        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>Maps a box in page points (y up) to bitmap pixels (y down), rounding outwards so nothing at the edge is left uncovered.</summary>
    public static PixelRect ToPixels(VisualBox box, double pageWidth, double pageHeight, int bitmapWidth, int bitmapHeight)
    {
        var x0 = (int)Math.Floor(box.X0 / pageWidth * bitmapWidth);
        var x1 = (int)Math.Ceiling(box.X1 / pageWidth * bitmapWidth);
        var y0 = (int)Math.Floor((pageHeight - box.Y1) / pageHeight * bitmapHeight);
        var y1 = (int)Math.Ceiling((pageHeight - box.Y0) / pageHeight * bitmapHeight);
        return new PixelRect(Math.Clamp(x0, 0, bitmapWidth), Math.Clamp(y0, 0, bitmapHeight), Math.Clamp(x1, 0, bitmapWidth), Math.Clamp(y1, 0, bitmapHeight));
    }

    public static void FillBlack(byte[] bgra, int width, PixelRect rect)
    {
        for (var y = rect.Y0; y < rect.Y1; y++)
        {
            var row = (y * width + rect.X0) * 4;
            for (var x = 0; x < rect.Width; x++)
            {
                bgra[row++] = 0;
                bgra[row++] = 0;
                bgra[row++] = 0;
                bgra[row++] = 255;
            }
        }
    }

    /// <summary>
    /// Replaces the rectangle with flat blocks of its average colour. Blocks are large compared with the text inside (about 60 % of the
    /// box height), so the average of a letter is a grey smudge: nothing about the shapes survives, unlike a Gaussian blur.
    /// </summary>
    public static void Mosaic(byte[] bgra, int width, PixelRect rect, int cell)
    {
        cell = Math.Max(2, cell);
        for (var by = rect.Y0; by < rect.Y1; by += cell)
        {
            for (var bx = rect.X0; bx < rect.X1; bx += cell)
            {
                var (ex, ey) = (Math.Min(bx + cell, rect.X1), Math.Min(by + cell, rect.Y1));
                long b = 0, g = 0, r = 0;
                var count = 0;
                for (var y = by; y < ey; y++)
                {
                    for (var x = bx; x < ex; x++)
                    {
                        var i = (y * width + x) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2];
                        count++;
                    }
                }

                if (count == 0) continue;
                var (ab, ag, ar) = ((byte)(b / count), (byte)(g / count), (byte)(r / count));
                for (var y = by; y < ey; y++)
                {
                    for (var x = bx; x < ex; x++)
                    {
                        var i = (y * width + x) * 4;
                        bgra[i] = ab; bgra[i + 1] = ag; bgra[i + 2] = ar; bgra[i + 3] = 255;
                    }
                }
            }
        }
    }

    /// <summary>Encodes BGRA pixels as a plain 8-bit RGB PNG (the layout PDFsharp imports reliably).</summary>
    public static byte[] ToPng(byte[] bgra, int width, int height)
    {
        using var image = Load(bgra, width, height);
        return image.ToByteArray(MagickFormat.Png24);
    }

    public static byte[] ToJpeg(byte[] bgra, int width, int height, int quality)
    {
        using var image = Load(bgra, width, height);
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private static MagickImage Load(byte[] bgra, int width, int height)
    {
        var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        image.HasAlpha = false;
        return image;
    }
}
