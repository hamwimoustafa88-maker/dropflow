using ImageMagick;

namespace DropFlow.AI.Ocr;

/// <param name="Path">PNG ready for an OCR engine (upright, opaque, at most the requested size).</param>
/// <param name="OriginalWidth">Size of the upright source image; engine rectangles are scaled back to this.</param>
public sealed record PreparedImage(string Path, int Width, int Height, int OriginalWidth, int OriginalHeight)
{
    public double ScaleToOriginal => Width == 0 ? 1 : (double)OriginalWidth / Width;
}

/// <summary>
/// Decodes any supported image with Magick.NET (so HEIC, WebP, AVIF... work with every engine), applies EXIF rotation, flattens
/// transparency onto white and writes a PNG that Windows OCR and Tesseract can both read from an ASCII path.
/// </summary>
public static class OcrImagePreparer
{
    public static PreparedImage Prepare(string input, string outputPng, int maxDimension = 8000)
    {
        try
        {
            var settings = new MagickReadSettings();
            if (string.Equals(System.IO.Path.GetExtension(input), ".svg", StringComparison.OrdinalIgnoreCase)) settings.Density = new Density(200);
            using var image = new MagickImage(input, settings);
            image.AutoOrient();
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }
            var originalWidth = (int)image.Width;
            var originalHeight = (int)image.Height;
            if (Math.Max(image.Width, image.Height) > (uint)maxDimension)
                image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension));
            image.Format = MagickFormat.Png;
            image.Write(outputPng);
            return new PreparedImage(outputPng, (int)image.Width, (int)image.Height, originalWidth, originalHeight);
        }
        catch (MagickException ex)
        {
            throw new OcrUnavailableException($"The image could not be read: {ex.Message}", ex);
        }
    }
}
