using DropFlow.AI.Models;
using ImageMagick;

namespace DropFlow.AI.Imaging;

/// <summary>
/// Something that finds faces for <c>img.redact-auto</c>. The default on Windows is the face detection built into Windows (nothing to
/// download), with the UltraFace ONNX model as the fallback; elsewhere it is the UltraFace model. Hosts may pass their own.
/// </summary>
public interface IFaceFinder
{
    /// <summary>Short name for logs and result notes.</summary>
    string Name { get; }

    /// <summary>
    /// Null when the finder can run now; otherwise a user-facing reason. A missing model is reported with
    /// <see cref="AiFailures.MissingModel"/> so the app can offer the download.
    /// </summary>
    string? Unavailable();

    /// <summary>Faces in pixels of <paramref name="image"/> (which is upright).</summary>
    Task<IReadOnlyList<FaceBox>> FindAsync(IMagickImage<byte> image, CancellationToken cancellationToken);
}

/// <summary>UltraFace RFB-320 through ONNX Runtime; needs the model from <see cref="ModelManager"/>.</summary>
public sealed class UltraFaceFinder(ModelManager models, FaceDetector detector) : IFaceFinder
{
    public const string ModelId = "ultraface-rfb-320";

    public string Name => "UltraFace (ONNX model)";

    public string? Unavailable() => models.IsInstalled(ModelId) ? null : AiFailures.MissingModel(models, ModelId);

    public Task<IReadOnlyList<FaceBox>> FindAsync(IMagickImage<byte> image, CancellationToken cancellationToken) =>
        Task.Run(() => detector.Detect(image, models.PathOf(ModelId), cancellationToken: cancellationToken), cancellationToken);
}

/// <summary>Uses the first finder that is available; when none is, reports the last one's reason (the model download, usually).</summary>
public sealed class FirstAvailableFaceFinder(params IFaceFinder[] finders) : IFaceFinder
{
    public IReadOnlyList<IFaceFinder> Finders { get; } = finders.Length > 0 ? finders : throw new ArgumentException("At least one finder is needed.", nameof(finders));

    /// <summary>The finder that would run now, or null when none can.</summary>
    public IFaceFinder? Current => Finders.FirstOrDefault(f => f.Unavailable() is null);

    public string Name => Current?.Name ?? Finders[0].Name;

    public string? Unavailable() => Current is null ? Finders[^1].Unavailable() : null;

    public Task<IReadOnlyList<FaceBox>> FindAsync(IMagickImage<byte> image, CancellationToken cancellationToken) =>
        (Current ?? throw new ImageToolException(Unavailable() ?? "No face detector is available.")).FindAsync(image, cancellationToken);
}
