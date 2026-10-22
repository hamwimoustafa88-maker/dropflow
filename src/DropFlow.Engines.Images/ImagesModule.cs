using DropFlow.Core.Operations;

namespace DropFlow.Engines.Images;

/// <summary>
/// Image conversions and tools built on Magick.NET (in-process, no external engine). Implements <c>convert:*</c> plus
/// img.compress, img.metadata, img.strip-gps, img.crop, img.resize, img.edit, img.rotate (JPEG: lossless), img.annotate, img.background, img.redact, img.collage.
/// (img.to-pdf → PDF engine; img.qr/img.icons → archive/utility engine; img.ocr/remove-bg/redact-auto → AI engine.)
/// </summary>
public static class ImagesModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) =>
    [
        new FormatConverter(),
        new CompressTool(),
        new MetadataTool(),
        new StripGpsTool(),
        new CropTool(),
        new ResizeTool(),
        new EditTool(),
        new RotateTool(),
        new AnnotateTool(),
        new BackgroundTool(),
        new RedactTool(),
        new CollageTool(),
    ];
}
