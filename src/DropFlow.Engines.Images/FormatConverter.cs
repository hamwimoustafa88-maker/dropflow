using DropFlow.Core.Operations;

namespace DropFlow.Engines.Images;

/// <summary>
/// Image format conversion: <c>convert:jpg|png|webp|heic|tiff|avif|bmp|gif|ico</c>.
/// Options (all optional, UI never asks): <c>quality</c> 1-100 (lossy targets, default 90), <c>stripMetadata</c> bool.
/// Orientation is applied and transparency is flattened onto white when the target has no alpha channel.
/// </summary>
internal sealed class FormatConverter() : ImageTool(ImageIo.Writable.Select(Operation.Convert).ToArray())
{
    public override bool Supports(string operation, string inputExtension) =>
        base.Supports(operation, inputExtension) && Operation.TargetOf(operation) != inputExtension;

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var target = Operation.TargetOf(job.Operation);
        using var image = ImageIo.Read(input);
        if (job.GetBool("stripMetadata")) image.Strip();

        var output = ImageIo.OutputFor(input, target, "converted", job.OutputDirectory);
        ct.ThrowIfCancellationRequested();
        return [ImageIo.Save(image, output, target, (uint)Math.Clamp(job.GetInt("quality", 90), 1, 100))];
    }
}
