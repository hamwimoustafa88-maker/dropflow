using ImageMagick;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>Encodes pixels rendered by PDFium into JPEG or PNG files (Magick.NET).</summary>
internal static class RasterEncoder
{
    public static byte[] ToJpeg(RenderedPage page, int quality, double? dpi = null) => Encode(page, MagickFormat.Jpeg, quality, dpi);

    public static byte[] ToPng(RenderedPage page, double? dpi = null) => Encode(page, MagickFormat.Png, 0, dpi);

    private static byte[] Encode(RenderedPage page, MagickFormat format, int quality, double? dpi)
    {
        var settings = new PixelReadSettings((uint)page.Width, (uint)page.Height, StorageType.Char, PixelMapping.BGRA);
        using var image = new MagickImage();
        image.ReadPixels(page.Bgra, settings);
        image.HasAlpha = false; // pages are drawn on white; an alpha channel would only make the file bigger
        image.Density = new Density(dpi ?? page.Dpi, DensityUnit.PixelsPerInch);
        image.Format = format;
        if (format == MagickFormat.Jpeg)
        {
            image.Quality = (uint)Math.Clamp(quality, 1, 100);
        }
        return image.ToByteArray();
    }
}
