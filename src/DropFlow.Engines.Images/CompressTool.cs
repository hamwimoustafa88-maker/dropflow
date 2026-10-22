using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.compress</c> — make images smaller while keeping the format.
/// Options: <c>mode</c> preset|percent|targetKb · <c>preset</c> smallest|balanced|best · <c>percent</c> (size reduction, 5-95) ·
/// <c>targetKb</c> · <c>maxWidth</c> (0 = keep) · <c>stripMetadata</c>.
/// Lossy formats search for the best quality that meets the target and then shrink the picture in steps if they must (<see cref="QualitySearch"/>).
/// A result that would not be smaller than the original is never kept: nothing is written and the note <c>note.img.alreadySmall</c> says so.
/// </summary>
internal sealed class CompressTool() : ImageTool("img.compress")
{
    private static readonly IReadOnlySet<string> Supported = new HashSet<string> { "jpg", "png", "webp", "heic", "avif", "tiff", "bmp" };

    public override bool Supports(string operation, string inputExtension) =>
        Operations.Contains(operation) && Supported.Contains(inputExtension);

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("mode", ToolOptionKind.Choice, "preset", ["preset", "percent", "targetKb"]),
        new("preset", ToolOptionKind.Choice, "balanced", ["smallest", "balanced", "best"]),
        new("percent", ToolOptionKind.Number, 50d, Min: 5, Max: 95),
        new("targetKb", ToolOptionKind.Number, 500d, Min: 5),
        new("maxWidth", ToolOptionKind.Number, 0d, Min: 0),
        new("stripMetadata", ToolOptionKind.Bool, false),
    ];

    private static uint PresetQuality(string? preset) => preset switch { "smallest" => 40u, "best" => 85u, _ => 70u };

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input);
        var outExt = ext == "bmp" ? "png" : ext;          // BMP has no compression: PNG is the honest equivalent
        var originalSize = new FileInfo(input).Length;

        var mode = job.GetString("mode", "preset");
        long? target = mode switch
        {
            "percent" => (long)(originalSize * (1 - Math.Clamp(job.GetDouble("percent", 50), 5, 95) / 100)),
            "targetKb" => (long)(Math.Max(1, job.GetDouble("targetKb", 500)) * 1024),
            _ => null,
        };

        using var image = ImageIo.Read(input);
        if (job.GetBool("stripMetadata")) image.Strip();
        var maxWidth = (uint)Math.Max(0, job.GetInt("maxWidth"));
        if (maxWidth > 0 && image.Width > maxWidth) image.Resize(maxWidth, 0);

        var encoded = Encode(image, outExt, mode == "preset" ? PresetQuality(job.GetString("preset")) : null, target, ct);

        // Never keep a "compressed" copy that is not smaller than the original (BMP to PNG included).
        if (encoded.Length >= originalSize)
        {
            note = Core.Localization.NoteText.Key("note.img.alreadySmall");
            return [];
        }
        if (target is { } t && encoded.Length > t)
            note = Core.Localization.NoteText.Key("note.img.targetNotReached", encoded.Length / 1024);

        var output = ImageIo.OutputFor(input, outExt, "compressed", job.OutputDirectory);
        return [ImageIo.SaveBytes(encoded, output)];
    }

    private static byte[] Encode(MagickImage image, string ext, uint? fixedQuality, long? target, CancellationToken ct)
    {
        var lossy = ImageIo.Lossy.Contains(ext);
        byte[] Render(MagickImage img, uint? quality, bool reduceColors)
        {
            using var copy = (MagickImage)img.Clone();
            copy.Format = ImageIo.FormatOf(ext);
            if (ext == "png")
            {
                copy.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                copy.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
                if (reduceColors) copy.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.No });
            }
            else if (ext == "tiff")
            {
                copy.Settings.Compression = CompressionMethod.Zip;
            }
            else if (lossy && quality is { } q)
            {
                copy.Quality = q;
            }
            if (ext == "jpg") ImageIo.Flatten(copy);
            return copy.ToByteArray();
        }

        // No target: one pass at the chosen quality (PNG/TIFF just get best lossless settings).
        if (target is null)
            return Render(image, fixedQuality ?? 75u, reduceColors: fixedQuality is 40u);

        // One scaled copy per round, reused by every quality step of that round.
        MagickImage? scaled = null;
        var scaledFor = double.NaN;
        MagickImage At(double scale)
        {
            if (scale == scaledFor && scaled is not null) return scaled;
            scaled?.Dispose();
            scaled = (MagickImage)image.Clone();
            if (scale < 1) scaled.Resize((uint)Math.Max(1, Math.Round(image.Width * scale)), 0);
            scaledFor = scale;
            return scaled;
        }
        try
        {
            return lossy
                ? QualitySearch.Lossy((scale, quality) => Render(At(scale), quality, false), target.Value, ct).Data
                : QualitySearch.Lossless((scale, reduced) => Render(At(scale), null, reduced), target.Value, ct).Data;
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}

/// <summary>
/// The search behind "compress to a target size": for lossy formats, a binary search on quality (10..95) in <see cref="QualitySteps"/> steps for the
/// highest quality that fits; if none fits, the picture is shrunk by <see cref="ShrinkFactor"/> and the search repeats, at most <see cref="MaxRounds"/>
/// rounds. Lossless formats try full colour, then 256 colours, per round. Returns the fitting result, or the smallest one seen.
/// The renderer is a parameter so the strategy is tested without encoding pictures.
/// </summary>
internal static class QualitySearch
{
    public const int QualitySteps = 7;
    public const int MaxRounds = 8;
    public const double ShrinkFactor = 0.8;
    private const uint MinQuality = 10, MaxQuality = 95;

    public static (byte[] Data, bool Fits) Lossy(Func<double, uint, byte[]> render, long target, CancellationToken ct)
    {
        byte[]? smallest = null;
        var scale = 1.0;
        for (var round = 0; round < MaxRounds; round++, scale *= ShrinkFactor)
        {
            uint lo = MinQuality, hi = MaxQuality;
            byte[]? fit = null;
            for (var step = 0; step < QualitySteps && lo <= hi; step++)
            {
                ct.ThrowIfCancellationRequested();
                var mid = (lo + hi) / 2;
                var data = render(scale, mid);
                if (smallest is null || data.Length < smallest.Length) smallest = data;
                if (data.Length <= target) { fit = data; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (fit is not null) return (fit, true);
        }
        return (smallest!, false);
    }

    public static (byte[] Data, bool Fits) Lossless(Func<double, bool, byte[]> render, long target, CancellationToken ct)
    {
        byte[]? smallest = null;
        var scale = 1.0;
        for (var round = 0; round < MaxRounds; round++, scale *= ShrinkFactor)
        {
            foreach (var reduced in new[] { false, true })
            {
                ct.ThrowIfCancellationRequested();
                var data = render(scale, reduced);
                if (smallest is null || data.Length < smallest.Length) smallest = data;
                if (data.Length <= target) return (data, true);
            }
        }
        return (smallest!, false);
    }
}
