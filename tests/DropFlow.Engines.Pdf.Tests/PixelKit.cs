using DropFlow.Engines.Pdf.Infrastructure;
using ImageMagick;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>A grayscale picture for simple "where is the ink" assertions.</summary>
internal sealed record Gray(int Width, int Height, byte[] Pixels)
{
    public byte At(int x, int y) => Pixels[y * Width + x];

    /// <summary>Pixels darker than the threshold, as (x, y) in image coordinates (y grows downwards).</summary>
    public List<(int X, int Y)> Ink(int threshold = 200)
    {
        var result = new List<(int, int)>();
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (Pixels[y * Width + x] < threshold)
                    result.Add((x, y));
        return result;
    }

    public (int MinX, int MinY, int MaxX, int MaxY)? InkBounds(int threshold = 200)
    {
        var ink = Ink(threshold);
        if (ink.Count == 0) return null;
        return (ink.Min(p => p.X), ink.Min(p => p.Y), ink.Max(p => p.X), ink.Max(p => p.Y));
    }

    public byte Darkest => Pixels.Length == 0 ? (byte)255 : Pixels.Min();
}

internal static class PixelKit
{
    /// <summary>Renders a PDF page with PDFium (a different engine than the PDFsharp that wrote the file).</summary>
    public static Gray RenderPage(string pdf, int pageNumber, double dpi = 36)
    {
        using var doc = PdfiumDocument.Open(pdf);
        var page = doc.Render(pageNumber - 1, dpi);
        var gray = new byte[page.Width * page.Height];
        for (var i = 0; i < gray.Length; i++)
        {
            int b = page.Bgra[i * 4], g = page.Bgra[i * 4 + 1], r = page.Bgra[i * 4 + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }
        return new Gray(page.Width, page.Height, gray);
    }

    /// <summary>Renders a PDF page with PDFium and returns R,G,B bytes (no alpha) with the page size.</summary>
    public static (int Width, int Height, byte[] Rgb) RenderRgb(string pdf, int pageNumber, double dpi = 72)
    {
        using var doc = PdfiumDocument.Open(pdf);
        var page = doc.Render(pageNumber - 1, dpi);
        var rgb = new byte[page.Width * page.Height * 3];
        for (var i = 0; i < page.Width * page.Height; i++)
        {
            rgb[i * 3] = page.Bgra[i * 4 + 2];
            rgb[i * 3 + 1] = page.Bgra[i * 4 + 1];
            rgb[i * 3 + 2] = page.Bgra[i * 4];
        }
        return (page.Width, page.Height, rgb);
    }

    /// <summary>Decodes any picture Magick can read into R,G,B bytes.</summary>
    public static (int Width, int Height, byte[] Rgb) DecodeRgb(string imagePath)
    {
        using var image = new MagickImage(imagePath);
        image.HasAlpha = false;
        return ((int)image.Width, (int)image.Height, image.ToByteArray(MagickFormat.Rgb));
    }

    /// <summary>Decodes a JPEG/PNG written by DropFlow into grayscale pixels.</summary>
    public static Gray Decode(string imagePath)
    {
        using var image = new MagickImage(imagePath);
        image.ColorSpace = ColorSpace.Gray;
        var bytes = image.ToByteArray(MagickFormat.Gray);
        return new Gray((int)image.Width, (int)image.Height, bytes);
    }

    /// <summary>Image size read straight from the file header (no decoder involved).</summary>
    public static (int Width, int Height) HeaderSize(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length > 24 && data[0] == 0x89 && data[1] == 0x50)
        {
            return (ReadBe32(data, 16), ReadBe32(data, 20));
        }

        if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                var marker = data[i + 1];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((data[i + 7] << 8) | data[i + 8], (data[i + 5] << 8) | data[i + 6]);
                i += 2 + ((data[i + 2] << 8) | data[i + 3]);
            }
        }

        throw new InvalidDataException($"{path} is not a PNG or JPEG file.");
    }

    public static string Signature(string path)
    {
        using var s = File.OpenRead(path);
        var head = new byte[4];
        s.ReadExactly(head);
        return head switch
        {
            [0x89, 0x50, 0x4E, 0x47] => "png",
            [0xFF, 0xD8, 0xFF, _] => "jpg",
            [0x25, 0x50, 0x44, 0x46] => "pdf",
            [0x50, 0x4B, 3, 4] => "zip",
            _ => "unknown",
        };
    }

    /// <summary>Correlation of x and y over the ink pixels: negative means the ink rises to the right (a counter-clockwise diagonal).</summary>
    public static double Correlation(IReadOnlyList<(int X, int Y)> points)
    {
        var n = points.Count;
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in points)
        {
            sxy += (x - mx) * (y - my);
            sxx += (x - mx) * (x - mx);
            syy += (y - my) * (y - my);
        }
        return n < 2 || sxx == 0 || syy == 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    private static int ReadBe32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
}
