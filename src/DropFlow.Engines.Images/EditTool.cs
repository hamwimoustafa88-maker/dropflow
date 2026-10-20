using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.edit</c> — photo adjustments in one pass.
/// Options: <c>brightness</c> -100..100 · <c>contrast</c> -100..100 · <c>saturation</c> 0..200 (100 = unchanged) · <c>sharpen</c> 0..100 ·
/// <c>blur</c> 0..20 · <c>filter</c> none|grayscale|sepia|invert · <c>rotate</c> 0|90|180|270 · <c>flipH</c> · <c>flipV</c>.
/// </summary>
internal sealed class EditTool() : ImageTool("img.edit")
{
    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("brightness", ToolOptionKind.Number, 0d, Min: -100, Max: 100),
        new("contrast", ToolOptionKind.Number, 0d, Min: -100, Max: 100),
        new("saturation", ToolOptionKind.Number, 100d, Min: 0, Max: 200),
        new("sharpen", ToolOptionKind.Number, 0d, Min: 0, Max: 100),
        new("blur", ToolOptionKind.Number, 0d, Min: 0, Max: 20),
        new("filter", ToolOptionKind.Choice, "none", ["none", "grayscale", "sepia", "invert"]),
        new("rotate", ToolOptionKind.Choice, "0", ["0", "90", "180", "270"]),
        new("flipH", ToolOptionKind.Bool, false),
        new("flipV", ToolOptionKind.Bool, false),
    ];

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        using var image = ImageIo.Read(input);

        var brightness = job.GetDouble("brightness");
        var contrast = job.GetDouble("contrast");
        var saturation = job.GetDouble("saturation", 100);
        var sharpen = job.GetDouble("sharpen");
        var blur = job.GetDouble("blur");
        var filter = job.GetString("filter", "none");
        var rotate = job.GetInt("rotate");
        var flipH = job.GetBool("flipH");
        var flipV = job.GetBool("flipV");

        var nothing = brightness == 0 && contrast == 0 && saturation == 100 && sharpen == 0 && blur == 0 && filter == "none" && rotate == 0 && !flipH && !flipV;
        if (nothing)
        {
            note = "No adjustment chosen.";
            return [];
        }

        if (brightness != 0 || contrast != 0) image.BrightnessContrast(new Percentage(brightness), new Percentage(contrast));
        if (saturation != 100) image.Modulate(new Percentage(100), new Percentage(Math.Clamp(saturation, 0, 200)), new Percentage(100));
        if (sharpen > 0) image.Sharpen(0, sharpen / 25.0);
        if (blur > 0) image.Blur(0, blur);
        switch (filter)
        {
            case "grayscale": image.Grayscale(); break;
            case "sepia": image.SepiaTone(); break;
            case "invert": image.Negate(); break;
        }
        if (rotate is 90 or 180 or 270) image.Rotate(rotate);
        if (flipH) image.Flop();
        if (flipV) image.Flip();

        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "edited", job.OutputDirectory), ext, 92)];
    }
}
