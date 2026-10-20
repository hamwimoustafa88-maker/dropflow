using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// <c>img.collage</c> (multi-file) — combine pictures into one.
/// Options: <c>layout</c> grid|row|column|featured · <c>columns</c> (grid; 0 = automatic) · <c>cell</c> px (default 600) ·
/// <c>spacing</c> px · <c>radius</c> px · <c>background</c> colour · <c>format</c> jpg|png.
/// Pictures fill their cell (centre-cropped). Order is the order of the inputs.
/// </summary>
internal sealed class CollageTool : IConverter
{
    public IReadOnlyCollection<string> Operations { get; } = ["img.collage"];

    public bool Supports(string operation, string inputExtension) =>
        operation == "img.collage" && ImageIo.Inputs.Contains(inputExtension);

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("layout", ToolOptionKind.Choice, "grid", ["grid", "row", "column", "featured"]),
        new("columns", ToolOptionKind.Number, 0d, Min: 0, Max: 12),
        new("cell", ToolOptionKind.Number, 600d, Min: 100, Max: 2000),
        new("spacing", ToolOptionKind.Number, 16d, Min: 0, Max: 200),
        new("radius", ToolOptionKind.Number, 0d, Min: 0, Max: 300),
        new("background", ToolOptionKind.Text, "#ffffff"),
        new("format", ToolOptionKind.Choice, "jpg", ["jpg", "png"]),
    ];

    public record Cell(int X, int Y, int W, int H);

    /// <summary>Cell rectangles and total canvas size for a layout (pure).</summary>
    public static (IReadOnlyList<Cell> Cells, int Width, int Height) Layout(string layout, int count, int columns, int cell, int spacing)
    {
        var cells = new List<Cell>();
        switch (layout)
        {
            case "row":
                for (var i = 0; i < count; i++) cells.Add(new Cell(spacing + i * (cell + spacing), spacing, cell, cell));
                return (cells, spacing + count * (cell + spacing), cell + 2 * spacing);
            case "column":
                for (var i = 0; i < count; i++) cells.Add(new Cell(spacing, spacing + i * (cell + spacing), cell, cell));
                return (cells, cell + 2 * spacing, spacing + count * (cell + spacing));
            case "featured" when count >= 3:
            {
                // One large picture on the left, the rest stacked on the right.
                var small = (cell * 2 - spacing * (count - 2)) / (count - 1);
                cells.Add(new Cell(spacing, spacing, cell * 2, cell * 2));
                for (var i = 1; i < count; i++)
                    cells.Add(new Cell(spacing * 2 + cell * 2, spacing + (i - 1) * (small + spacing), small, small));
                return (cells, spacing * 3 + cell * 2 + small, cell * 2 + 2 * spacing);
            }
            default:
            {
                var cols = columns > 0 ? Math.Min(columns, count) : (int)Math.Ceiling(Math.Sqrt(count));
                var rows = (int)Math.Ceiling(count / (double)cols);
                for (var i = 0; i < count; i++)
                    cells.Add(new Cell(spacing + i % cols * (cell + spacing), spacing + i / cols * (cell + spacing), cell, cell));
                return (cells, spacing + cols * (cell + spacing), spacing + rows * (cell + spacing));
            }
        }
    }

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (job.Inputs.Count < 2) return ConversionResult.Fail("Choose at least two pictures for a collage.");
                var cell = Math.Clamp(job.GetInt("cell", 600), 100, 2000);
                var spacing = Math.Clamp(job.GetInt("spacing", 16), 0, 200);
                var radius = Math.Clamp(job.GetInt("radius"), 0, 300);
                var ext = job.GetString("format", "jpg") == "png" ? "png" : "jpg";
                var (cells, width, height) = Layout(job.GetString("layout", "grid")!, job.Inputs.Count, job.GetInt("columns"), cell, spacing);

                using var canvas = new MagickImage(ImageIo.Color(job.GetString("background"), "#ffffff"), (uint)width, (uint)height);
                for (var i = 0; i < job.Inputs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(new JobProgress((double)i / job.Inputs.Count));
                    using var image = ImageIo.Read(job.Inputs[i]);
                    var c = cells[i];
                    image.Resize(new MagickGeometry((uint)c.W, (uint)c.H) { FillArea = true });
                    image.Crop(new MagickGeometry((uint)c.W, (uint)c.H), Gravity.Center);
                    image.ResetPage();
                    if (radius > 0) BackgroundTool.Round(image, radius);
                    canvas.Composite(image, c.X, c.Y, CompositeOperator.Over);
                }

                var output = new OutputPathResolver().ForGroup(job.Inputs, ext, "collage", job.OutputDirectory);
                ImageIo.Save(canvas, output, ext, 92);
                progress.Report(new JobProgress(1));
                return ConversionResult.Ok([output], started.Elapsed);
            }
            catch (MagickException ex)
            {
                return ConversionResult.Fail(ex.Message, started.Elapsed);
            }
        }, CancellationToken.None);
}
