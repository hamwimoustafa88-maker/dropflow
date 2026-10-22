// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.AI.Ocr;
using DropFlow.App.Views.Editors;
using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.App.Services;

/// <summary>
/// Face and text finding for the redact editor, on top of the AI engine that auto-redaction uses. It never downloads anything: faces
/// are offered only when the face model is already installed, text only when an OCR engine and language are present.
/// </summary>
internal sealed class RedactDetection(ModelManager models, IEngineProvider engines) : IRedactDetectors, IDisposable
{
    private const double PaddingPercent = 10;
    private readonly Lazy<OcrService> _ocr = new(() => new OcrService(engines));
    private readonly Lazy<OnnxSessionCache> _sessions = new(() => new OnnxSessionCache());

    public bool FacesAvailable => models.IsInstalled(RedactAutoConverter.FaceModelId);

    public async Task<bool> TextAvailableAsync()
    {
        var plan = await _ocr.Value.PlanAsync(new OcrRequest(), CancellationToken.None).ConfigureAwait(false);
        return plan.Plan is not null;
    }

    public Task<IReadOnlyList<Box>> FacesAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<Box>>(() =>
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            var faces = new FaceDetector(_sessions.Value).Detect(image, models.PathOf(RedactAutoConverter.FaceModelId), cancellationToken: cancellationToken);
            return [.. faces
                .Select(f => RedactionRegions.PadAndClamp(new OcrRect(f.X, f.Y, f.Width, f.Height), PaddingPercent, (int)image.Width, (int)image.Height))
                .OfType<PixelRect>()
                .Select(ToBox)];
        }, cancellationToken);

    public async Task<IReadOnlyList<Box>> TextAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _ocr.Value.RecognizeAsync(path, new OcrRequest(), cancellationToken).ConfigureAwait(false);
        return [.. RedactionRegions.TextBoxes(result)
            .Select(r => RedactionRegions.PadAndClamp(r, PaddingPercent, result.ImageWidth, result.ImageHeight))
            .OfType<PixelRect>()
            .Select(ToBox)];
    }

    private static Box ToBox(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    public void Dispose()
    {
        if (_sessions.IsValueCreated) _sessions.Value.Dispose();
    }
}
