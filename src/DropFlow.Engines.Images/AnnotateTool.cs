using System.Text.Json;
using DropFlow.Core.Operations;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Images;

/// <summary>One drawing instruction produced by the editor (coordinates are in image pixels).</summary>
public sealed record AnnotationItem(
    string Type,
    double X = 0, double Y = 0, double W = 0, double H = 0,
    double X2 = 0, double Y2 = 0,
    string? Color = null, double Stroke = 4, bool Fill = false,
    string? Text = null, double Size = 32, int N = 0,
    string? Path = null);

/// <summary>
/// <c>img.annotate</c> — draws shapes on a photo from a JSON list in option <c>items</c>:
/// <c>rect</c>/<c>ellipse</c> (x,y,w,h), <c>line</c>/<c>arrow</c> (x,y → x2,y2), <c>text</c> (Latin text; x,y,size), <c>number</c>
/// (numbered callout circle at x,y), <c>image</c> (overlay PNG from <c>path</c> at x,y — the editor uses this for Arabic text, which
/// ImageMagick cannot shape). The visual editor builds the list; this converter only renders it.
/// </summary>
internal sealed class AnnotateTool() : ImageTool("img.annotate")
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) => [new("items", ToolOptionKind.Text, "[]", Required: true)];

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
        ImageIo.EnsureColor(image);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            Draw(image, item);
        }
        var ext = ImageIo.EditedExtension(input);
        return [ImageIo.Save(image, ImageIo.OutputFor(input, ext, "annotated", job.OutputDirectory), ext, 92)];
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
            case "line":
                d.Line(it.X, it.Y, it.X2, it.Y2).Draw(image);
                break;
            case "arrow":
                d.Line(it.X, it.Y, it.X2, it.Y2).Draw(image);
                new Drawables().StrokeColor(color).StrokeWidth(1).FillColor(color).Polygon(ArrowHead(it, stroke)).Draw(image);
                break;
            case "text":
                if (string.IsNullOrEmpty(it.Text)) break;
                new Drawables().FontPointSize(it.Size).StrokeColor(MagickColors.Transparent).FillColor(color)
                    .TextAlignment(TextAlignment.Left).Text(it.X, it.Y + it.Size, it.Text).Draw(image);
                break;
            case "number":
                var r = Math.Max(it.Size, 16) / 1.6;
                new Drawables().StrokeColor(MagickColors.White).StrokeWidth(2).FillColor(color).Circle(it.X, it.Y, it.X + r, it.Y).Draw(image);
                new Drawables().FontPointSize(r * 1.3).StrokeColor(MagickColors.Transparent).FillColor(MagickColors.White)
                    .TextAlignment(TextAlignment.Center).Text(it.X, it.Y + r * 0.45, it.N.ToString()).Draw(image);
                break;
            case "image":
                if (it.Path is { } p && File.Exists(p))
                {
                    using var overlay = new MagickImage(p);
                    image.Composite(overlay, (int)it.X, (int)it.Y, CompositeOperator.Over);
                }
                break;
            default:
                throw new ArgumentException($"Unknown annotation type '{it.Type}'.");
        }
    }

    private static PointD[] ArrowHead(AnnotationItem it, double stroke)
    {
        var angle = Math.Atan2(it.Y2 - it.Y, it.X2 - it.X);
        var len = Math.Max(14, stroke * 4);
        PointD P(double a, double l) => new(it.X2 - l * Math.Cos(angle + a), it.Y2 - l * Math.Sin(angle + a));
        return [new PointD(it.X2, it.Y2), P(Math.PI / 7, len), P(-Math.PI / 7, len)];
    }
}
