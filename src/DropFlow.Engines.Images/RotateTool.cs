// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.rotate</c> — turn pictures 90, 180 or 270 degrees clockwise. Option: <c>rotate</c> (90 | 180 | 270, default 90).
/// JPEG photos are turned <b>losslessly</b>: only the EXIF orientation tag changes, the compressed picture is copied byte for byte
/// (note <c>note.img.rotatedLossless</c>). Other formats are turned pixel by pixel (PNG/TIFF/BMP stay lossless; WebP/HEIC/AVIF are re-encoded at quality 92).
/// Output: <c>name (rotated).ext</c>.
/// </summary>
internal sealed class RotateTool() : ImageTool("img.rotate")
{
    public static readonly string[] Angles = ["90", "180", "270"];

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("rotate", ToolOptionKind.Choice, "90", Angles),
    ];

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var text = job.GetString("rotate", "90") ?? "90";
        if (!Angles.Contains(text)) throw new ArgumentException($"Turn by 90, 180 or 270 degrees, not '{text}'.");
        var degrees = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        if (EditTool.TryLosslessJpeg(input, PhotoOrientation.Edit(degrees, false, false), "rotated", job.OutputDirectory) is { } lossless)
        {
            note = "note.img.rotatedLossless";
            return [lossless];
        }

        ct.ThrowIfCancellationRequested();
        using var image = ImageIo.Read(input);
        image.Rotate(degrees);
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "rotated", job.OutputDirectory), ext, 92)];
    }
}
