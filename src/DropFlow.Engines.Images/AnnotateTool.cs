using System.Text.Json;
using DropFlow.Core.Operations;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Images;

/// <summary>
/// One drawing instruction produced by the editor. With option <c>units</c> = <c>px</c> (default) coordinates, <c>Stroke</c> and
/// <c>Size</c> are image pixels; with <c>relative</c> X/W/X2 are fractions of the width, Y/H/Y2 fractions of the height, and
/// <c>Stroke</c>/<c>Size</c> fractions of the longer side, so one list fits pictures of any size.
/// </summary>
/// <param name="Points">Pen strokes: x0,y0,x1,y1,… (same units as X/Y).</param>
/// <param name="Zoom">Magnifier enlargement (lens radius = W × Zoom).</param>
public sealed record AnnotationItem(
    string Type,
    double X = 0, double Y = 0, double W = 0, double H = 0,
    double X2 = 0, double Y2 = 0,
    string? Color = null, double Stroke = 4, bool Fill = false,
    string? Text = null, double Size = 32, int N = 0,
    string? Path = null,
    double[]? Points = null, double Zoom = 2);

/// <summary>
/// <c>img.annotate</c> — draws shapes on a photo from a JSON list in option <c>items</c>:
/// <c>rect</c>/<c>ellipse</c>/<c>highlight</c> (x,y,w,h), <c>line</c>/<c>arrow</c> (x,y → x2,y2), <c>text</c> (Latin text; x,y,size),
/// <c>number</c> (numbered callout circle at x,y), <c>pen</c> (freehand <c>points</c>), <c>magnify</c> (circle of radius w around x,y
/// shown <c>zoom</c> times larger at x2,y2), <c>image</c> (overlay PNG from <c>path</c> at x,y, stretched to w×h when given — the editor
/// uses this for all text, because ImageMagick cannot shape Arabic). The visual editor builds the list; this converter only renders it.
/// </summary>
internal sealed class AnnotateTool() : ImageTool("img.annotate")
{
    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("items", ToolOptionKind.Text, "[]", Required: true),
        new("units", ToolOptionKind.Choice, "px", ["px", "relative"]),
    ];

    public static IReadOnlyList<AnnotationItem> Parse(string? json) => AnnotationRenderer.Parse(json);

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var items = Parse(job.GetString("items"));
        if (items.Count == 0)
        {
            note = "Nothing to draw.";
            return [];
        }

        using var image = ImageIo.Read(input);
        AnnotationRenderer.Draw(image, items, job.GetString("units", "px") == "relative", ct);
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "annotated", job.OutputDirectory), ext, 92)];
    }
}

/// <summary>Renders annotation items onto a picture (shared by the converter and anything that wants a faithful preview).</summary>
public static class AnnotationRenderer
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions Write = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static IReadOnlyList<AnnotationItem> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AnnotationItem>>(json, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The annotation list is not valid JSON: " + ex.Message);
        }
    }

    public static string ToJson(IEnumerable<AnnotationItem> items) => JsonSerializer.Serialize(items, Write);

    /// <summary>Converts relative items to pixels of a width × height picture.</summary>
    public static AnnotationItem ToPixels(AnnotationItem it, double width, double height)
    {
        var l = Math.Max(width, height);
        return it with
        {
            X = it.X * width, Y = it.Y * height, W = it.W * width, H = it.H * height, X2 = it.X2 * width, Y2 = it.Y2 * height,
            Stroke = it.Stroke * l, Size = it.Size * l,
            Points = it.Points?.Select((v, i) => i % 2 == 0 ? v * width : v * height).ToArray(),
        };
    }

    public static void Draw(MagickImage image, IEnumerable<AnnotationItem> items, bool relative, CancellationToken ct = default)
    {
        ImageIo.EnsureColor(image);
        foreach (var raw in items)
        {
            ct.ThrowIfCancellationRequested();
            Draw(image, relative ? ToPixels(raw, image.Width, image.Height) : raw);
        }
    }

    private static void Draw(MagickImage image, AnnotationItem it)
    {
        var color = ImageIo.Color(it.Color, "#ef4444");
        var stroke = Math.Max(1, it.Stroke);
        var d = new Drawables().StrokeColor(color).StrokeWidth(stroke);
        d = it.Fill ? d.FillColor(color) : d.FillColor(MagickColors.Transparent);

        switch (it.Type.ToLowerInvariant())
        {
            case "rect":
                d.Rectangle(it.X, it.Y, it.X + it.W, it.Y + it.H).Draw(image);
                break;
            case "ellipse":
                d.Ellipse(it.X + it.W / 2, it.Y + it.H / 2, it.W / 2, it.H / 2, 0, 360).Draw(image);
                break;
            case "highlight":
            {
                var marker = new MagickColor(color.R, color.G, color.B, 110);
                new Drawables().StrokeColor(MagickColors.Transparent).FillColor(marker).Rectangle(it.X, it.Y, it.X + it.W, it.Y + it.H).Draw(image);
                break;
            }
            case "line":
                d.StrokeLineCap(LineCap.Round).Line(it.X, it.Y, it.X2, it.Y2).Draw(image);
                break;
            case "arrow":
            {
                // the shaft stops inside the head so a thick line never pokes past the tip
                var angle = Math.Atan2(it.Y2 - it.Y, it.X2 - it.X);
                var back = Math.Max(14, stroke * 4) * 0.6;
                d.StrokeLineCap(LineCap.Round).Line(it.X, it.Y, it.X2 - back * Math.Cos(angle), it.Y2 - back * Math.Sin(angle)).Draw(image);
            }
                new Drawables().StrokeColor(color).StrokeWidth(1).FillColor(color).Polygon(ArrowHead(it, stroke)).Draw(image);
                break;
            case "pen":
                if (it.Points is { Length: >= 4 } pts)
                {
                    var points = Enumerable.Range(0, pts.Length / 2).Select(i => new PointD(pts[i * 2], pts[i * 2 + 1])).ToArray();
                    new Drawables().StrokeColor(color).StrokeWidth(stroke).FillColor(MagickColors.Transparent)
                        .StrokeLineCap(LineCap.Round).StrokeLineJoin(LineJoin.Round).Polyline(points).Draw(image);
                }
                break;
            case "text":
                if (string.IsNullOrEmpty(it.Text)) break;
                TextDrawables().FontPointSize(it.Size).StrokeColor(MagickColors.Transparent).FillColor(color)
                    .TextAlignment(TextAlignment.Left).Text(it.X, it.Y + it.Size, it.Text).Draw(image);
                break;
            case "number":
                var r = Math.Max(it.Size, 16) / 1.6;
                new Drawables().StrokeColor(MagickColors.White).StrokeWidth(Math.Max(2, r / 8)).FillColor(color).Circle(it.X, it.Y, it.X + r, it.Y).Draw(image);
                TextDrawables().FontPointSize(r * 1.3).StrokeColor(MagickColors.Transparent).FillColor(MagickColors.White)
                    .TextAlignment(TextAlignment.Center).Text(it.X, it.Y + r * 0.45, it.N.ToString(System.Globalization.CultureInfo.InvariantCulture)).Draw(image);
                break;
            case "magnify":
                Magnify(image, it, color, stroke);
                break;
            case "image":
                if (it.Path is { } p && File.Exists(p))
                {
                    using var overlay = new MagickImage(p);
                    if (it.W >= 1 && it.H >= 1) overlay.Resize(new MagickGeometry((uint)Math.Round(it.W), (uint)Math.Round(it.H)) { IgnoreAspectRatio = true });
                    image.Composite(overlay, (int)Math.Round(it.X), (int)Math.Round(it.Y), CompositeOperator.Over);
                }
                break;
            default:
                throw new ArgumentException($"Unknown annotation type '{it.Type}'.");
        }
    }

    /// <summary>A round lens: the circle of radius W around (X,Y), enlarged Zoom times, drawn centred on (X2,Y2) with a border.</summary>
    private static void Magnify(MagickImage image, AnnotationItem it, MagickColor color, double stroke)
    {
        var r = Math.Max(2, it.W);
        var zoom = Math.Clamp(it.Zoom, 1, 8);
        var x0 = (int)Math.Floor(it.X - r);
        var y0 = (int)Math.Floor(it.Y - r);
        var size = (uint)Math.Ceiling(2 * r);
        using var lens = (MagickImage)image.Clone();
        lens.Crop(new MagickGeometry(x0, y0, size, size));
        lens.ResetPage();
        if (lens.Width < 2 || lens.Height < 2) return;
        var lensSize = (uint)Math.Round(2 * r * zoom);
        lens.Resize(new MagickGeometry(lensSize, lensSize) { IgnoreAspectRatio = true });

        using var mask = new MagickImage(MagickColors.Transparent, lensSize, lensSize);
        new Drawables().FillColor(MagickColors.White).Circle(lensSize / 2.0, lensSize / 2.0, lensSize / 2.0, 0.5).Draw(mask);
        lens.Alpha(AlphaAction.Set);
        lens.Composite(mask, CompositeOperator.DstIn);

        var left = (int)Math.Round(it.X2 - lensSize / 2.0);
        var top = (int)Math.Round(it.Y2 - lensSize / 2.0);
        image.Composite(lens, left, top, CompositeOperator.Over);
        new Drawables().StrokeColor(color).StrokeWidth(stroke).FillColor(MagickColors.Transparent)
            .Circle(it.X2, it.Y2, it.X2 + lensSize / 2.0, it.Y2).Draw(image);
    }

    /// <summary>
    /// Drawables for text. Magick.NET finds a default font by itself on Windows only, so elsewhere an installed TrueType file is named
    /// explicitly (Arial on macOS, DejaVu Sans or similar on Linux).
    /// </summary>
    private static Drawables TextDrawables()
    {
        var drawables = new Drawables();
        if (!OperatingSystem.IsWindows() && DropFlow.Engines.Common.FontFiles.DefaultSans() is { } font) drawables.Font(font);
        return drawables;
    }

    private static PointD[] ArrowHead(AnnotationItem it, double stroke)
    {
        var angle = Math.Atan2(it.Y2 - it.Y, it.X2 - it.X);
        var len = Math.Max(14, stroke * 4);
        PointD P(double a, double l) => new(it.X2 - l * Math.Cos(angle + a), it.Y2 - l * Math.Sin(angle + a));
        return [new PointD(it.X2, it.Y2), P(Math.PI / 7, len), P(-Math.PI / 7, len)];
    }
}
