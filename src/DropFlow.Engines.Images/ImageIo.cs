using DropFlow.Core.Output;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>Shared reading/writing rules for every image tool (orientation, transparency, atomic output).</summary>
internal static class ImageIo
{
    /// <summary>Extensions (canonical, no dot) the catalog lists as image inputs.</summary>
    public static readonly IReadOnlySet<string> Inputs =
        new HashSet<string> { "jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "svg", "ico" };

    /// <summary>Formats Magick.NET can write that are in the catalog's image outputs (pdf is handled by the PDF engine).</summary>
    public static readonly IReadOnlySet<string> Writable =
        new HashSet<string> { "jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "ico" };

    public static readonly IReadOnlySet<string> Lossy = new HashSet<string> { "jpg", "webp", "heic", "avif" };

    public static MagickFormat FormatOf(string ext) => ext switch
    {
        "jpg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "heic" => MagickFormat.Heic,
        "tiff" => MagickFormat.Tiff,
        "avif" => MagickFormat.Avif,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Ico,
        _ => throw new NotSupportedException($"Unsupported image format '{ext}'."),
    };

    /// <summary>Reads the first frame, applies the EXIF orientation, and rasterises SVG at a useful size.</summary>
    public static MagickImage Read(string path)
    {
        var settings = new MagickReadSettings();
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            settings.Density = new Density(192);

        var image = new MagickImage(path, settings);
        image.AutoOrient();
        return image;
    }

    /// <summary>
    /// Grayscale files (many black-and-white photos, and any flat white/black PNG) load as a one-channel image, and compositing a
    /// coloured overlay onto one silently loses the colour. Call this before drawing colour onto a picture.
    /// </summary>
    public static void EnsureColor(MagickImage image)
    {
        if (image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray) image.ColorSpace = ColorSpace.sRGB;
        if (image.ColorType is not (ColorType.TrueColor or ColorType.TrueColorAlpha))
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
    }

    /// <summary>Replaces transparency with a solid colour (needed before saving to formats without alpha).</summary>
    public static void Flatten(MagickImage image, IMagickColor<byte>? background = null)
    {
        if (image.HasAlpha) image.ColorAlpha(background ?? MagickColors.White);
    }

    /// <summary>
    /// Writes to "<paramref name="finalPath"/>.part" and renames on success, so a crash or cancel never leaves a half-written file
    /// that looks like a result. Returns the final path.
    /// </summary>
    public static string Save(MagickImage image, string finalPath, string ext, uint? quality = null)
    {
        var temp = finalPath + ".part";
        try
        {
            image.Format = FormatOf(ext);
            if (quality is { } q && Lossy.Contains(ext)) image.Quality = q;
            if (ext == "ico" && (image.Width > 256 || image.Height > 256)) image.Resize(new MagickGeometry(256, 256));
            if (ext is "jpg" or "bmp") Flatten(image);

            using (var stream = File.Create(temp)) image.Write(stream);
            File.Move(temp, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    /// <summary>Writes already-encoded bytes with the same ".part then rename" safety as <see cref="Save"/>.</summary>
    public static string SaveBytes(byte[] data, string finalPath)
    {
        var temp = finalPath + ".part";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    /// <summary>The extension edited copies are saved in: the input's own when DropFlow can write it, otherwise png (svg).</summary>
    public static string EditedExtension(string input)
    {
        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input);
        return Writable.Contains(ext) ? ext : "png";
    }

    /// <summary>Parses "#rrggbb", "#rrggbbaa" or a CSS colour name; falls back to <paramref name="fallback"/> for empty/invalid text.</summary>
    public static MagickColor Color(string? text, string fallback)
    {
        foreach (var candidate in new[] { text, fallback })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return new MagickColor(candidate.Trim()); }
            catch (ArgumentException) { /* try the fallback */ }
            catch (MagickException) { /* try the fallback */ }
        }
        return new MagickColor("#000000");
    }

    public static string OutputFor(string input, string ext, string suffix, string? outputDirectory) =>
        new OutputPathResolver().ForFile(input, ext, suffix, outputDirectory);

    /// <summary>Encodes to memory (used to measure size while searching for a quality that hits a target).</summary>
    public static byte[] Encode(MagickImage image, string ext, uint? quality)
    {
        using var copy = (MagickImage)image.Clone();
        copy.Format = FormatOf(ext);
        if (quality is { } q && Lossy.Contains(ext)) copy.Quality = q;
        if (ext is "jpg" or "bmp") Flatten(copy);
        return copy.ToByteArray();
    }
}
