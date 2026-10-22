using DropFlow.Core.Operations;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.edit</c> — photo adjustments in one pass (see <see cref="PhotoAdjust"/> for every option and its range):
/// light (exposure, brightness, contrast, highlights, shadows, whites, blacks, gamma), colour (temperature, tint, saturation,
/// vibrance, hue), detail (clarity, sharpen, noise, blur), effects (vignette, grain, fade), <c>filter</c> none|grayscale|sepia|invert,
/// <c>rotate</c> 0|90|180|270, <c>flipH</c>, <c>flipV</c>. The visual editor previews with the same <see cref="PhotoEditor"/>.
/// When only turning/mirroring is asked for a JPEG, the picture is not re-encoded: only its EXIF orientation changes (lossless,
/// see <see cref="JpegExif"/>), and the note <c>note.img.rotatedLossless</c> says so.
/// </summary>
internal sealed class EditTool() : ImageTool("img.edit")
{
    /// <summary>
    /// Writes a copy of a JPEG that only declares a new orientation (no re-encode). Returns null when the file is not a JPEG or its EXIF block
    /// cannot take the tag, in which case the caller turns the pixels instead.
    /// </summary>
    internal static string? TryLosslessJpeg(string input, PhotoOrientation change, string suffix, string? outputDirectory)
    {
        if (DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input) != "jpg") return null;
        var bytes = File.ReadAllBytes(input);
        var turned = PhotoOrientation.FromExif(JpegExif.ReadOrientation(bytes)).Then(change);
        if (JpegExif.WithOrientation(bytes, turned.ToExif()) is not { } patched) return null;
        return ImageIo.SaveBytes(patched, ImageIo.OutputFor(input, "jpg", suffix, outputDirectory));
    }

    public override IReadOnlyList<ToolOption> OptionsFor(string operation)
    {
        var list = new List<ToolOption>();
        foreach (var key in PhotoAdjust.LightKeys.Concat(PhotoAdjust.ColorKeys).Concat(PhotoAdjust.DetailKeys).Concat(PhotoAdjust.EffectKeys))
        {
            var (min, max, neutral) = PhotoAdjust.RangeOf(key);
            list.Add(new ToolOption(key, ToolOptionKind.Number, neutral, Min: min, Max: max));
        }
        list.Add(new("filter", ToolOptionKind.Choice, "none", ["none", "grayscale", "sepia", "invert"]));
        list.Add(new("rotate", ToolOptionKind.Choice, "0", ["0", "90", "180", "270"]));
        list.Add(new("flipH", ToolOptionKind.Bool, false));
        list.Add(new("flipV", ToolOptionKind.Bool, false));
        return list;
    }

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var adjust = PhotoAdjust.FromOptions(job);
        if (adjust.IsIdentity)
        {
            note = "No adjustment chosen.";
            return [];
        }

        var geometryOnly = (adjust with { Rotate = 0, FlipH = false, FlipV = false }).IsIdentity;
        if (geometryOnly && TryLosslessJpeg(input, PhotoOrientation.Edit(adjust.Rotate is 90 or 180 or 270 ? adjust.Rotate : 0, adjust.FlipH, adjust.FlipV), "edited", job.OutputDirectory) is { } lossless)
        {
            note = "note.img.rotatedLossless";
            return [lossless];
        }

        using var image = ImageIo.Read(input);
        PhotoEditor.Apply(image, adjust, 1, ct);
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "edited", job.OutputDirectory), ext, 92)];
    }
}
