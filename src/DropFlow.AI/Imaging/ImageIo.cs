using ImageMagick;

namespace DropFlow.AI.Imaging;

/// <summary>An expected failure in the image/ONNX tools (unreadable image, damaged model). Converters turn it into a failed result.</summary>
public sealed class ImageToolException(string message, Exception? inner = null) : Exception(message, inner);

internal static class ImageIo
{
    /// <summary>Decodes an image, applies EXIF rotation and returns pixels as 8-bit sRGB-like RGBA (first frame only).</summary>
    public static MagickImage LoadUpright(string path)
    {
        try
        {
            var settings = new MagickReadSettings();
            if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)) settings.Density = new Density(200);
            var image = new MagickImage(path, settings);
            image.AutoOrient();
            image.Depth = 8;
            return image;
        }
        catch (MagickException ex)
        {
            throw new ImageToolException($"The image could not be read: {ex.Message}", ex);
        }
    }

    public static byte[] GetBytes(IMagickImage<byte> image, PixelMapping mapping) =>
        image.GetPixels().ToByteArray(0, 0, image.Width, image.Height, mapping)
        ?? throw new ImageToolException("The image has no pixel data.");

    /// <summary>One byte per pixel (intensity), row by row.</summary>
    public static byte[] GetGray(IMagickImage<byte> image) =>
        image.GetPixels().ToByteArray(0, 0, image.Width, image.Height, "I")
        ?? throw new ImageToolException("The image has no pixel data.");

    /// <summary>Builds an image from interleaved 8-bit pixels (<paramref name="mapping"/> says which channels).</summary>
    public static MagickImage FromBytes(byte[] pixels, int width, int height, PixelMapping mapping) =>
        new(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, mapping));

    /// <summary>Single-channel image from 8-bit gray values.</summary>
    public static MagickImage FromGray(byte[] gray, int width, int height) =>
        new(gray, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "I"));

    /// <summary>Extension (no dot) and Magick format for the output of an editing tool: keep common formats, otherwise PNG.</summary>
    public static (string Extension, MagickFormat Format) OutputFor(string inputExtension) =>
        inputExtension.TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => ("jpg", MagickFormat.Jpeg),
            "webp" => ("webp", MagickFormat.WebP),
            "bmp" => ("bmp", MagickFormat.Bmp),
            "tif" or "tiff" => ("tiff", MagickFormat.Tiff),
            _ => ("png", MagickFormat.Png),
        };
}
