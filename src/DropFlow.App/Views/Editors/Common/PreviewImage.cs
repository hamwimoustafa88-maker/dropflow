// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace DropFlow.App.Views.Editors;

/// <summary>A preview-sized copy of a picture: at most <see cref="PreviewImage.MaxSide"/> pixels on the long side.</summary>
/// <param name="Scale">Preview width / original width (1 when the picture is already small).</param>
internal sealed record PreviewSource(MagickImage Image, double Scale, uint OriginalWidth, uint OriginalHeight) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>Loading preview copies and turning Magick.NET pictures into WPF bitmaps.</summary>
internal static class PreviewImage
{
    public const int MaxSide = 1400;

    /// <summary>Reads the first frame upright (EXIF orientation applied, like the engines do) and shrinks it for previewing.</summary>
    public static PreviewSource Load(string path, int maxSide = MaxSide)
    {
        var settings = new MagickReadSettings();
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) settings.Density = new Density(192);
        settings.FrameIndex = 0;
        settings.FrameCount = 1;
        var image = new MagickImage(path, settings);
        image.AutoOrient();
        return Shrink(image, maxSide);
    }

    public static PreviewSource Shrink(MagickImage image, int maxSide = MaxSide)
    {
        uint w = image.Width, h = image.Height;
        var longSide = Math.Max(w, h);
        var scale = 1.0;
        if (longSide > maxSide)
        {
            scale = (double)maxSide / longSide;
            image.Resize((uint)Math.Max(1, Math.Round(w * scale)), (uint)Math.Max(1, Math.Round(h * scale)));
            scale = (double)image.Width / w;
        }
        if (image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray) image.ColorSpace = ColorSpace.sRGB;
        return new PreviewSource(image, scale, w, h);
    }

    /// <summary>A frozen (thread-safe) BGRA bitmap of the picture.</summary>
    public static BitmapSource ToBitmap(MagickImage image)
    {
        var w = (int)image.Width;
        var h = (int)image.Height;
        var bytes = image.GetPixels().ToByteArray(PixelMapping.BGRA) ?? throw new InvalidDataException("The picture has no pixels.");
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource FromPng(byte[] png)
    {
        using var image = new MagickImage(png);
        return ToBitmap(image);
    }

    /// <summary>Encodes a WPF bitmap as PNG bytes (used for text overlays and snapshots).</summary>
    public static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
