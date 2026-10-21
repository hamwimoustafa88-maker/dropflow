using ImageMagick;

namespace DropFlow.AI;

/// <summary>
/// The image extensions the AI image tools accept: the catalog's image inputs that this build of Magick.NET reports it can read.
/// A format the library cannot read is never offered, so Supports stays truthful on every machine. Animated formats use their
/// first frame.
/// </summary>
internal static class ImageInputs
{
    private static readonly Lazy<IReadOnlySet<string>> SupportedLazy = new(Compute);

    public static IReadOnlySet<string> Supported => SupportedLazy.Value;

    internal static MagickFormat? FormatOf(string extension) => extension switch
    {
        "jpg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "heic" => MagickFormat.Heic,
        "tiff" => MagickFormat.Tiff,
        "avif" => MagickFormat.Avif,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "svg" => MagickFormat.Svg,
        "ico" => MagickFormat.Ico,
        _ => null,
    };

    private static IReadOnlySet<string> Compute()
    {
        var readable = MagickNET.SupportedFormats.Where(f => f.SupportsReading).Select(f => f.Format).ToHashSet();
        return AiCatalog.Inputs("image")
            .Where(ext => FormatOf(ext) is { } format && readable.Contains(format))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
