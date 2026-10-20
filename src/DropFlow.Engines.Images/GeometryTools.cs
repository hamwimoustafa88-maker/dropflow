using System.Globalization;
using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.crop</c> — crop to an exact rectangle or to an aspect ratio (centred).
/// Options: <c>x</c>,<c>y</c>,<c>width</c>,<c>height</c> (pixels; used when width and height are &gt; 0) · <c>aspect</c> free|1:1|4:3|3:2|16:9|9:16|3:4|2:3|4:5.
/// </summary>
internal sealed class CropTool() : ImageTool("img.crop")
{
    public static readonly string[] Aspects = ["free", "1:1", "4:3", "3:2", "16:9", "9:16", "3:4", "2:3", "4:5"];

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("aspect", ToolOptionKind.Choice, "1:1", Aspects),
        new("x", ToolOptionKind.Number, 0d, Min: 0),
        new("y", ToolOptionKind.Number, 0d, Min: 0),
        new("width", ToolOptionKind.Number, 0d, Min: 0),
        new("height", ToolOptionKind.Number, 0d, Min: 0),
    ];

    /// <summary>Largest centred rectangle with the given ratio ("16:9") inside a width×height picture.</summary>
    public static (int X, int Y, int W, int H) CenteredRatio(int width, int height, string ratio)
    {
        var parts = ratio.Split(':');
        var rw = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var rh = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var target = rw / rh;
        var current = (double)width / height;

        int w, h;
        if (current > target) { h = height; w = (int)Math.Round(height * target); }
        else { w = width; h = (int)Math.Round(width / target); }
        return ((width - w) / 2, (height - h) / 2, Math.Max(1, w), Math.Max(1, h));
    }

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        using var image = ImageIo.Read(input);
        int W = (int)image.Width, H = (int)image.Height;

        int x, y, w, h;
        var rectW = job.GetInt("width");
        var rectH = job.GetInt("height");
        if (rectW > 0 && rectH > 0)
        {
            x = Math.Clamp(job.GetInt("x"), 0, W - 1);
            y = Math.Clamp(job.GetInt("y"), 0, H - 1);
            w = Math.Min(rectW, W - x);
            h = Math.Min(rectH, H - y);
        }
        else
        {
            var aspect = job.GetString("aspect", "1:1")!;
            if (aspect == "free") throw new ArgumentException("Choose an aspect ratio or enter a size to crop to.");
            (x, y, w, h) = CenteredRatio(W, H, aspect);
        }

        image.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
        image.ResetPage();
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "cropped", job.OutputDirectory), ext, 92)];
    }
}

/// <summary>
/// <c>img.resize</c> — Options: <c>percent</c> (1-400) or <c>width</c>/<c>height</c> in pixels; <c>keepAspect</c> (default true);
/// <c>noUpscale</c> (default true: never enlarge a picture past its original size when a pixel size is given).
/// </summary>
internal sealed class ResizeTool() : ImageTool("img.resize")
{
    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("percent", ToolOptionKind.Number, 50d, Min: 1, Max: 400),
        new("width", ToolOptionKind.Number, 0d, Min: 0),
        new("height", ToolOptionKind.Number, 0d, Min: 0),
        new("keepAspect", ToolOptionKind.Bool, true),
        new("noUpscale", ToolOptionKind.Bool, true),
    ];

    /// <summary>Target size for the given options (pure, tested without images).</summary>
    public static (uint W, uint H) TargetSize(uint width, uint height, double percent, int w, int h, bool keepAspect, bool noUpscale)
    {
        if (w <= 0 && h <= 0)
        {
            var p = Math.Clamp(percent, 1, 400) / 100;
            return ((uint)Math.Max(1, Math.Round(width * p)), (uint)Math.Max(1, Math.Round(height * p)));
        }

        double tw = w > 0 ? w : width * (double)h / height;
        double th = h > 0 ? h : height * (double)w / width;
        if (keepAspect && w > 0 && h > 0)
        {
            var scale = Math.Min(tw / width, th / height);       // fit inside the box
            tw = width * scale;
            th = height * scale;
        }
        else if (keepAspect)
        {
            // one side given: the other follows (already computed above)
        }

        if (noUpscale && (tw > width || th > height))
        {
            var down = Math.Min((double)width / tw, (double)height / th);
            if (down < 1 || (tw > width && th > height)) { tw = width; th = height; }
        }
        return ((uint)Math.Max(1, Math.Round(tw)), (uint)Math.Max(1, Math.Round(th)));
    }

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        using var image = ImageIo.Read(input);
        var (tw, th) = TargetSize(image.Width, image.Height, job.GetDouble("percent", 50), job.GetInt("width"), job.GetInt("height"),
            job.GetBool("keepAspect", true), job.GetBool("noUpscale", true));

        if (tw == image.Width && th == image.Height)
        {
            note = "The picture is already that size.";
            return [];
        }
        image.Resize(new MagickGeometry(tw, th) { IgnoreAspectRatio = true });
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "resized", job.OutputDirectory), ext, 92)];
    }
}
