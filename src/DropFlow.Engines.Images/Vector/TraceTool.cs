// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Images.Vector.Tracing;

namespace DropFlow.Engines.Images.Vector;

/// <summary>
/// Raster to vector: <c>img.trace</c> (options <c>mode</c> color|bw, <c>colors</c> 2-16, <c>detail</c> low|medium|high; writes
/// "name (traced).svg") and the plain conversions <c>convert:svg</c> / <c>convert:svgz</c> from any raster picture, which trace with
/// the defaults (colour, 8 colours, medium detail) without asking. SVG and SVGZ inputs are already vectors (see <see cref="SvgzConverter"/>).
/// </summary>
internal sealed class TraceTool() : ImageTool("img.trace", "convert:svg", "convert:svgz")
{
    public override bool Supports(string operation, string inputExtension) =>
        Operations.Contains(operation) && ImageIo.Inputs.Contains(inputExtension) && !ImageIo.Vector.Contains(inputExtension);

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) => operation == "img.trace"
        ?
        [
            new("mode", ToolOptionKind.Choice, "color", ["color", "bw"]),
            new("colors", ToolOptionKind.Number, 8d, Min: TraceOptions.MinColors, Max: TraceOptions.MaxColors),
            new("detail", ToolOptionKind.Choice, "medium", ["low", "medium", "high"]),
        ]
        : [];

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var options = job.Operation == "img.trace"
            ? new TraceOptions(job.GetString("mode", "color") ?? "color", job.GetInt("colors", 8), job.GetString("detail", "medium") ?? "medium")
            : new TraceOptions();

        using var image = ImageIo.Read(input);
        var svg = Tracer.Trace(image, options, ct);
        var bytes = new UTF8Encoding(false).GetBytes(svg);

        var ext = job.Operation == "convert:svgz" ? "svgz" : "svg";
        if (ext == "svgz") bytes = SvgzConverter.Gzip(bytes);
        ct.ThrowIfCancellationRequested();
        var output = ImageIo.OutputFor(input, ext, job.Operation == "img.trace" ? "traced" : "converted", job.OutputDirectory);
        return [ImageIo.SaveBytes(bytes, output)];
    }
}
