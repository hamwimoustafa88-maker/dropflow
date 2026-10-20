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
/// <c>img.metadata</c> — edit or remove embedded metadata.
/// Options: <c>mode</c> edit|strip · <c>artist</c> · <c>copyright</c> · <c>description</c>.
/// "strip" removes EXIF (including GPS and the embedded thumbnail), IPTC and XMP but keeps the colour profile so colours do not shift.
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
    ];

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input);
        using var image = ImageIo.Read(input);

        if (job.GetString("mode", "edit") == "strip")
        {
            foreach (var name in MetadataFormats.PrivateProfiles) image.RemoveProfile(name);
            return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "cleaned", job.OutputDirectory), ext, 92)];
        }

        var exif = image.GetExifProfile() ?? new ExifProfile();
        var changed = false;
        void Set(string key, ExifTag<string> tag)
        {
            var value = job.GetString(key);
            if (string.IsNullOrWhiteSpace(value)) return;
            exif.SetValue(tag, value.Trim());
            changed = true;
        }
        Set("artist", ExifTag.Artist);
        Set("copyright", ExifTag.Copyright);
        Set("description", ExifTag.ImageDescription);

        if (!changed)
        {
            note = "Nothing to change: fill at least one field.";
            return [];
        }
        image.SetProfile(exif);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "edited", job.OutputDirectory), ext, 92)];
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
