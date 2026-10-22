using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>Formats that can carry EXIF/XMP metadata.</summary>
internal static class MetadataFormats
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string> { "jpg", "png", "webp", "tiff", "heic", "avif" };
    public static readonly string[] PrivateProfiles = ["exif", "iptc", "xmp", "8bim"];
}

/// <summary>
/// <c>img.metadata</c> — edit or remove embedded metadata (engine: <see cref="ImageMetadata"/>).
/// Options: <c>mode</c> edit|strip · <c>artist</c> · <c>copyright</c> · <c>description</c> · <c>changes</c> (JSON from
/// <see cref="ImageMetadata.ToJson"/>, written by the visual metadata editor; applied after the three simple fields).
/// "strip" removes EXIF (including GPS and the embedded thumbnail), IPTC and XMP but keeps the colour profile so colours do not shift.
/// JPEG is rewritten losslessly (only metadata segments change; the compressed pixels are copied byte for byte).
/// </summary>
internal sealed class MetadataTool() : ImageTool("img.metadata")
{
    public override bool Supports(string operation, string inputExtension) =>
        Operations.Contains(operation) && MetadataFormats.Supported.Contains(inputExtension);

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("mode", ToolOptionKind.Choice, "edit", ["edit", "strip"]),
        new("artist", ToolOptionKind.Text, ""),
        new("copyright", ToolOptionKind.Text, ""),
        new("description", ToolOptionKind.Text, ""),
        new("changes", ToolOptionKind.Text, ""),
    ];

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input);

        if (job.GetString("mode", "edit") == "strip")
            return [Write(input, ImageMetadata.RemoveAll(), ImageIo.OutputFor(input, ext, "cleaned", job.OutputDirectory), ct)];

        var changes = new List<MetadataChange>();
        void Simple(string option, string exifKey)
        {
            var value = job.GetString(option);
            if (!string.IsNullOrWhiteSpace(value)) changes.Add(new MetadataChange(MetadataGroup.Exif, exifKey, value.Trim()));
        }
        Simple("artist", "Artist");
        Simple("copyright", "Copyright");
        Simple("description", "ImageDescription");
        changes.AddRange(ImageMetadata.ParseChanges(job.GetString("changes")));

        if (changes.Count == 0)
        {
            note = "Nothing to change: fill at least one field.";
            return [];
        }
        return [Write(input, changes, ImageIo.OutputFor(input, ext, "edited", job.OutputDirectory), ct)];
    }

    private static string Write(string input, IReadOnlyList<MetadataChange> changes, string output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ImageMetadata.Apply(input, changes, output);
        return output;
    }
}

/// <summary>
/// <c>img.strip-gps</c> — remove location data before sharing a photo, keeping the other EXIF fields (camera, date…).
/// Removes every EXIF "GPS*" tag, and the XMP block when it mentions GPS. If there is no location data nothing is written.
/// </summary>
internal sealed class StripGpsTool() : ImageTool("img.strip-gps")
{
    public override bool Supports(string operation, string inputExtension) =>
        Operations.Contains(operation) && MetadataFormats.Supported.Contains(inputExtension);

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input);
        using var image = ImageIo.Read(input);

        var removed = 0;
        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            foreach (var tag in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal)).Select(v => v.Tag).ToList())
            {
                exif.RemoveValue(tag);
                removed++;
            }
            if (removed > 0) image.SetProfile(exif);
        }

        var xmp = image.GetXmpProfile();
        if (xmp is not null && System.Text.Encoding.UTF8.GetString(xmp.ToByteArray()).Contains("GPS", StringComparison.OrdinalIgnoreCase))
        {
            image.RemoveProfile("xmp");
            removed++;
        }

        if (removed == 0)
        {
            note = "No location data was found.";
            return [];
        }
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "no location", job.OutputDirectory), ext, 95)];
    }
}
