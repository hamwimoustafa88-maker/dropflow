using System.Globalization;
using DropFlow.Core.Operations;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.background</c> — place the picture on a solid colour, gradient or another image.
/// Options: <c>background</c> "#rrggbb" | "gradient:#a:#b" | "image:C:\path.jpg" · <c>aspect</c> original|1:1|4:3|3:2|16:9|9:16|4:5 ·
/// <c>padding</c> % of the picture (0-40) · <c>radius</c> % of the shorter side (0-50) · <c>shadow</c> bool.
/// </summary>
internal sealed class BackgroundTool() : ImageTool("img.background")
{
    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("background", ToolOptionKind.Text, "#ffffff"),
        new("aspect", ToolOptionKind.Choice, "original", ["original", "1:1", "4:3", "3:2", "16:9", "9:16", "4:5"]),
        new("padding", ToolOptionKind.Number, 8d, Min: 0, Max: 40),
        new("radius", ToolOptionKind.Number, 0d, Min: 0, Max: 50),
        new("shadow", ToolOptionKind.Bool, false),
    ];

    /// <summary>Canvas size that holds the padded picture at the requested aspect ratio (pure).</summary>
    public static (uint W, uint H) CanvasSize(uint width, uint height, double paddingPercent, string aspect)
    {
        var pad = 1 + 2 * Math.Clamp(paddingPercent, 0, 40) / 100;
        double w = width * pad, h = height * pad;
        if (aspect != "original" && aspect.Contains(':'))
        {
            var p = aspect.Split(':');
            var ratio = double.Parse(p[0], CultureInfo.InvariantCulture) / double.Parse(p[1], CultureInfo.InvariantCulture);
            var cw = Math.Max(w, h * ratio);
            (w, h) = (cw, cw / ratio);
        }
        return ((uint)Math.Round(w), (uint)Math.Round(h));
    }

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        using var image = ImageIo.Read(input);
        var (cw, ch) = CanvasSize(image.Width, image.Height, job.GetDouble("padding", 8), job.GetString("aspect", "original")!);

        using var canvas = MakeBackground(job.GetString("background", "#ffffff")!, cw, ch);

        var radius = Math.Clamp(job.GetDouble("radius"), 0, 50) / 100 * Math.Min(image.Width, image.Height);
        if (radius > 0) Round(image, radius);

        var x = (int)((cw - image.Width) / 2);
        var y = (int)((ch - image.Height) / 2);

        if (job.GetBool("shadow"))
        {
            using var shadow = new MagickImage(MagickColors.Black, image.Width, image.Height);
            shadow.Alpha(AlphaAction.Set);
            shadow.Composite(image, CompositeOperator.CopyAlpha);   // silhouette of the picture
            shadow.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.45);
            shadow.Blur(0, Math.Max(4, Math.Min(cw, ch) / 60.0));
            canvas.Composite(shadow, x + (int)(cw / 120), y + (int)(ch / 70), CompositeOperator.Over);
        }
        canvas.Composite(image, x, y, CompositeOperator.Over);
        return [ImageIo.Save(canvas, ImageIo.OutputFor(input, "png", "background", job.OutputDirectory), "png")];
    }

    private static MagickImage MakeBackground(string spec, uint w, uint h)
    {
        if (spec.StartsWith("gradient:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = spec.Split(':', 3);
            var a = ImageIo.Color(parts.ElementAtOrDefault(1), "#4338ca");
            var b = ImageIo.Color(parts.ElementAtOrDefault(2), "#0e7490");
            return new MagickImage($"gradient:{a.ToHexString()}-{b.ToHexString()}", new MagickReadSettings { Width = w, Height = h });
        }
        if (spec.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
        {
            var path = spec["image:".Length..];
            var bg = new MagickImage(path);
            bg.Resize(new MagickGeometry(w, h) { FillArea = true });
            bg.Crop(new MagickGeometry(w, h), Gravity.Center);
            bg.ResetPage();
            return bg;
        }
        return new MagickImage(ImageIo.Color(spec, "#ffffff"), w, h);
    }

    internal static void Round(MagickImage image, double radius)
    {
        using var mask = new MagickImage(MagickColors.Transparent, image.Width, image.Height);
        new Drawables().FillColor(MagickColors.White).StrokeColor(MagickColors.White)
            .RoundRectangle(0, 0, image.Width - 1, image.Height - 1, radius * 2, radius * 2).Draw(mask);
        image.Alpha(AlphaAction.Set);
        image.Composite(mask, CompositeOperator.DstIn);
    }
}

/// <summary>
/// <c>img.redact</c> — cover private areas for good. Options: <c>regions</c> "x,y,w,h;x,y,w,h" (pixels) ·
/// <c>style</c> solid|blur|pixelate · <c>color</c> (solid). The result has ALL metadata removed, because an EXIF thumbnail would
/// otherwise still show the redacted area.
/// </summary>
internal sealed class RedactTool() : ImageTool("img.redact")
{
    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("regions", ToolOptionKind.Text, "", Required: true),
        new("style", ToolOptionKind.Choice, "solid", ["solid", "blur", "pixelate"]),
        new("color", ToolOptionKind.Text, "#000000"),
    ];

    public static IReadOnlyList<(int X, int Y, int W, int H)> ParseRegions(string? text)
    {
        var result = new List<(int, int, int, int)>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = part.Split(',', StringSplitOptions.TrimEntries);
            if (n.Length != 4 || !n.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException($"Invalid region '{part}'. Use x,y,width,height.");
            var v = n.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (v[2] <= 0 || v[3] <= 0) throw new ArgumentException($"Region '{part}' must have a positive size.");
            result.Add((v[0], v[1], v[2], v[3]));
        }
        return result;
    }

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var regions = ParseRegions(job.GetString("regions"));
        if (regions.Count == 0)
        {
            note = "No area selected.";
            return [];
        }

        using var image = ImageIo.Read(input);
        var style = job.GetString("style", "solid");
        var color = ImageIo.Color(job.GetString("color"), "#000000");

        foreach (var (rx, ry, rw, rh) in regions)
        {
            var x = Math.Clamp(rx, 0, (int)image.Width - 1);
            var y = Math.Clamp(ry, 0, (int)image.Height - 1);
            var w = Math.Min(rw, (int)image.Width - x);
            var h = Math.Min(rh, (int)image.Height - y);
            if (w <= 0 || h <= 0) continue;

            switch (style)
            {
                case "blur":
                {
                    using var area = (MagickImage)image.Clone();
                    area.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
                    area.ResetPage();
                    area.Blur(0, Math.Max(w, h) / 5.0);
                    area.Blur(0, Math.Max(w, h) / 5.0);
                    image.Composite(area, x, y, CompositeOperator.Over);
                    break;
                }
                case "pixelate":
                {
                    using var area = (MagickImage)image.Clone();
                    area.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
                    area.ResetPage();
                    area.Scale(new MagickGeometry(Math.Max(2u, (uint)w / 12), Math.Max(2u, (uint)h / 12)) { IgnoreAspectRatio = true });
                    area.Scale(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
                    image.Composite(area, x, y, CompositeOperator.Over);
                    break;
                }
                default:
                    new Drawables().FillColor(color).StrokeColor(color).Rectangle(x, y, x + w - 1, y + h - 1).Draw(image);
                    break;
            }
        }

        image.Strip();   // no EXIF thumbnail that still shows the covered area
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "redacted", job.OutputDirectory), ext, 95)];
    }
}
