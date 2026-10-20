using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.compress</c> — make images smaller while keeping the format.
/// Options: <c>mode</c> preset|percent|targetKb · <c>preset</c> smallest|balanced|best · <c>percent</c> (size reduction, 5-95) ·
/// <c>targetKb</c> · <c>maxWidth</c> (0 = keep) · <c>stripMetadata</c>.
/// Lossy formats search for the best quality that meets the target and then shrink the picture in steps if they must.
/// If the result would not be smaller, nothing is written and a note says so.
/// </summary>
internal sealed class CompressTool() : ImageTool("img.compress")
{
    private static readonly IReadOnlySet<string> Supported = new HashSet<string> { "jpg", "png", "webp", "heic", "avif", "tiff", "bmp" };
    private static readonly double[] Scales = [1.0, 0.85, 0.7, 0.55, 0.4];

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

        if (encoded.Length >= originalSize && ext != "bmp")
        {
            note = "Already optimal — the original was kept (the result would not be smaller).";
            return [];
        }
        if (target is { } t && encoded.Length > t)
            note = $"Could not reach the target size; smallest achievable is {encoded.Length / 1024} KB.";

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

        byte[]? best = null;
        foreach (var scale in Scales)
        {
            ct.ThrowIfCancellationRequested();
            using var scaled = (MagickImage)image.Clone();
            if (scale < 1) scaled.Resize((uint)Math.Max(1, scaled.Width * scale), 0);

            if (lossy)
            {
                // Largest quality whose output fits: binary search over 10..95.
                uint lo = 10, hi = 95;
                byte[]? fit = null;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var data = Render(scaled, mid, false);
                    if (data.Length <= target) { fit = data; lo = mid + 1; }
                    else { if (mid == 0) break; hi = mid - 1; }
                }
                if (fit is not null) return fit;
                best = Render(scaled, 10, false);
            }
            else
            {
                var lossless = Render(scaled, null, false);
                if (lossless.Length <= target) return lossless;
                var reduced = Render(scaled, null, true);
                if (reduced.Length <= target) return reduced;
                best = reduced;
            }
        }
        return best ?? Render(image, 10, false);
    }
}
