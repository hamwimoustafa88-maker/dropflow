using DropFlow.Core.Output;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>Shared reading/writing rules for every image tool (orientation, transparency, atomic output).</summary>
internal static class ImageIo
{
    /// <summary>Camera RAW formats: decoded ("developed") by the LibRaw inside Magick.NET; nothing writes them.</summary>
    public static readonly IReadOnlySet<string> CameraRaw = new HashSet<string> { "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf" };

    /// <summary>
    /// Extensions (canonical, no dot) the catalog lists as image inputs. Every one is read by the Magick.NET build DropFlow ships
    /// (checked by a probe test). SVGZ is gzip-compressed SVG.
    /// </summary>
    public static readonly IReadOnlySet<string> Inputs =
        new HashSet<string>
        {
            "jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "svg", "ico",
            "svgz", "psd", "jxl", "jp2", "tga", "qoi", "dds", "pcx", "exr", "hdr", "dcm", "fits",
        }.Concat(CameraRaw).ToHashSet();

    /// <summary>Vector inputs: rasterised on reading.</summary>
    public static readonly IReadOnlySet<string> Vector = new HashSet<string> { "svg", "svgz" };

    /// <summary>
    /// Formats DropFlow can write that are in the catalog's image outputs (pdf is handled by the PDF engine, svg/svgz by the tracer).
    /// Magick.NET writes all of them except HEIC, which <see cref="Heic.HeicEncoder"/> makes with FFmpeg (Magick.NET's libheif has no encoder).
    /// </summary>
    public static readonly IReadOnlySet<string> Writable =
        new HashSet<string> { "jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "ico", "jxl", "jp2", "psd", "tga", "qoi" };

    public static readonly IReadOnlySet<string> Lossy = new HashSet<string> { "jpg", "webp", "heic", "avif", "jxl", "jp2" };

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
        "svg" => MagickFormat.Svg,
        "svgz" => MagickFormat.Svgz,
        "psd" => MagickFormat.Psd,
        "jxl" => MagickFormat.Jxl,
        "jp2" => MagickFormat.Jp2,
        "tga" => MagickFormat.Tga,
        "qoi" => MagickFormat.Qoi,
        "dds" => MagickFormat.Dds,
        "pcx" => MagickFormat.Pcx,
        "exr" => MagickFormat.Exr,
        "hdr" => MagickFormat.Hdr,
        "dcm" => MagickFormat.Dcm,
        "fits" => MagickFormat.Fits,
        "dng" => MagickFormat.Dng,
        "cr2" => MagickFormat.Cr2,
        "cr3" => MagickFormat.Cr3,
        "nef" => MagickFormat.Nef,
        "arw" => MagickFormat.Arw,
        "orf" => MagickFormat.Orf,
        "rw2" => MagickFormat.Rw2,
        "raf" => MagickFormat.Raf,
        _ => throw new NotSupportedException($"Unsupported image format '{ext}'."),
    };

    /// <summary>
    /// Formats whose content alone does not say what they are, so the extension must: Targa and ICO have no signature, SVGZ is just
    /// gzip, and camera RAW files are TIFF inside (sniffing them would decode the small embedded preview instead of the photo).
    /// </summary>
    private static readonly IReadOnlySet<string> NeedsFormatHint = new HashSet<string> { "tga", "ico", "svgz", "pcx", "dcm" }.Concat(CameraRaw).ToHashSet();

    /// <summary>Read settings for a file: the format hint where content sniffing is not enough, and a useful density for vectors.</summary>
    public static MagickReadSettings ReadSettings(string path)
    {
        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(path);
        var settings = new MagickReadSettings();
        if (NeedsFormatHint.Contains(ext)) settings.Format = FormatOf(ext);
        if (Vector.Contains(ext)) settings.Density = new Density(192);
        return settings;
    }

    /// <summary>
    /// Reads the first frame (for Photoshop files: the flattened composite), applies the EXIF orientation, rasterises SVG at a useful
    /// size, and turns linear-light pictures (Radiance HDR) into sRGB so they do not come out dark.
    /// </summary>
    public static MagickImage Read(string path)
    {
        var image = new MagickImage(path, ReadSettings(path));
        if (image.ColorSpace == ColorSpace.RGB) image.ColorSpace = ColorSpace.sRGB;
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
            PrepareFor(image, ext);
            if (quality is { } q && Lossy.Contains(ext)) image.Quality = q;
            if (ext == "ico" && (image.Width > 256 || image.Height > 256)) image.Resize(new MagickGeometry(256, 256));
            if (ext is "jpg" or "bmp") Flatten(image);

            if (ext == "heic") File.WriteAllBytes(temp, Heic.HeicEncoder.Encode(image, quality ?? 90));
            else using (var stream = File.Create(temp)) image.Write(stream);
            File.Move(temp, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// A few writers take the bit depth and palette of the source literally: a two-colour PNG reads as a 2-bit palette picture, and
    /// JPEG 2000 then stores 2-bit samples (red 220 became 170) and Photoshop writes a broken colour table. Give them plain 8-bit colour.
    /// </summary>
    private static void PrepareFor(MagickImage image, string ext)
    {
        if (ext is not ("jp2" or "psd" or "tga" or "qoi" or "jxl")) return;
        image.Depth = 8;
        if (image.ColorType is ColorType.Palette or ColorType.PaletteAlpha or ColorType.Bilevel or ColorType.Grayscale or ColorType.GrayscaleAlpha)
        {
            if (image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray) image.ColorSpace = ColorSpace.sRGB;
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
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
        if (ext == "heic") return Heic.HeicEncoder.Encode(image, quality ?? 90);
        using var copy = (MagickImage)image.Clone();
        copy.Format = FormatOf(ext);
        PrepareFor(copy, ext);
        if (quality is { } q && Lossy.Contains(ext)) copy.Quality = q;
        if (ext is "jpg" or "bmp") Flatten(copy);
        return copy.ToByteArray();
    }
}
