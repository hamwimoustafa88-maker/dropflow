using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DropFlow.AI.Imaging;

/// <summary>A detected face in pixels of the upright source image.</summary>
public sealed record FaceBox(double X, double Y, double Width, double Height, double Score);

/// <summary>
/// Pure maths for UltraFace RFB-320 (Linzaer, MIT): 320x240 input stretched without letterboxing and normalised as (x - 127) / 128;
/// raw SSD heads out (2-class confidences and box deltas against 4420 fixed priors). Softmax, prior decoding and NMS happen here.
/// </summary>
public static class UltraFaceMath
{
    public const int InputWidth = 320;
    public const int InputHeight = 240;
    public const float CenterVariance = 0.1f;
    public const float SizeVariance = 0.2f;
    public const int PriorCount = 4420;

    private static readonly int[] FeatureWidths = [40, 20, 10, 5];
    private static readonly int[] FeatureHeights = [30, 15, 8, 4];
    private static readonly int[][] MinBoxes = [[10, 16, 24], [32, 48], [64, 96], [128, 192, 256]];

    private static readonly Lazy<float[]> PriorsLazy = new(BuildPriors);

    /// <summary>The 4420 SSD priors as (cx, cy, w, h) in 0..1, row-major per feature map (outer loop over rows, as upstream).</summary>
    public static float[] Priors => PriorsLazy.Value;

    private static float[] BuildPriors()
    {
        var priors = new List<float>(PriorCount * 4);
        for (var level = 0; level < FeatureWidths.Length; level++)
            for (var j = 0; j < FeatureHeights[level]; j++)
                for (var i = 0; i < FeatureWidths[level]; i++)
                    foreach (var minBox in MinBoxes[level])
                    {
                        priors.Add(Clamp01((i + 0.5f) / FeatureWidths[level]));
                        priors.Add(Clamp01((j + 0.5f) / FeatureHeights[level]));
                        priors.Add(Clamp01((float)minBox / InputWidth));
                        priors.Add(Clamp01((float)minBox / InputHeight));
                    }
        return priors.ToArray();
    }

    /// <summary>Interleaved RGB at 320x240 to a planar NCHW float tensor, (x - 127) / 128.</summary>
    public static float[] ToTensor(ReadOnlySpan<byte> rgb)
    {
        const int pixels = InputWidth * InputHeight;
        if (rgb.Length != pixels * 3) throw new ArgumentException($"Expected {pixels * 3} bytes for 320x240 RGB.", nameof(rgb));
        var tensor = new float[3 * pixels];
        for (var i = 0; i < pixels; i++)
            for (var c = 0; c < 3; c++)
                tensor[c * pixels + i] = (rgb[i * 3 + c] - 127f) / 128f;
        return tensor;
    }

    /// <summary>
    /// Raw heads to faces. <paramref name="confidences"/> holds [N,2] (background, face) logits, <paramref name="locations"/> [N,4] deltas.
    /// Boxes are mapped from the stretched 320x240 frame back to <paramref name="imageWidth"/> x <paramref name="imageHeight"/>.
    /// </summary>
    public static IReadOnlyList<FaceBox> Decode(
        ReadOnlySpan<float> confidences, ReadOnlySpan<float> locations,
        int imageWidth, int imageHeight, double scoreThreshold = 0.5, double iouThreshold = 0.3)
    {
        var n = confidences.Length / 2;
        if (confidences.Length != n * 2 || locations.Length != n * 4)
            throw new ArgumentException("Confidences must be [N,2] and locations [N,4].");
        if (n != PriorCount)
            throw new ArgumentException($"Expected {PriorCount} priors but the model returned {n}.");

        var priors = Priors;
        var candidates = new List<FaceBox>();
        for (var i = 0; i < n; i++)
        {
            // Softmax over (background, face).
            var bg = confidences[i * 2];
            var face = confidences[i * 2 + 1];
            var score = 1.0 / (1.0 + Math.Exp(bg - face));
            if (score < scoreThreshold) continue;

            var pcx = priors[i * 4]; var pcy = priors[i * 4 + 1]; var pw = priors[i * 4 + 2]; var ph = priors[i * 4 + 3];
            var cx = locations[i * 4] * CenterVariance * pw + pcx;
            var cy = locations[i * 4 + 1] * CenterVariance * ph + pcy;
            var w = Math.Exp(locations[i * 4 + 2] * SizeVariance) * pw;
            var h = Math.Exp(locations[i * 4 + 3] * SizeVariance) * ph;

            var x1 = Math.Clamp(cx - w / 2, 0, 1) * imageWidth;
            var y1 = Math.Clamp(cy - h / 2, 0, 1) * imageHeight;
            var x2 = Math.Clamp(cx + w / 2, 0, 1) * imageWidth;
            var y2 = Math.Clamp(cy + h / 2, 0, 1) * imageHeight;
            if (x2 - x1 < 1 || y2 - y1 < 1) continue;
            candidates.Add(new FaceBox(x1, y1, x2 - x1, y2 - y1, score));
        }
        return NonMaxSuppression(candidates, iouThreshold);
    }

    /// <summary>Greedy NMS: keeps the best box, drops those overlapping it by more than <paramref name="iouThreshold"/>, repeats.</summary>
    public static IReadOnlyList<FaceBox> NonMaxSuppression(IEnumerable<FaceBox> boxes, double iouThreshold)
    {
        var remaining = boxes.OrderByDescending(b => b.Score).ToList();
        var kept = new List<FaceBox>();
        while (remaining.Count > 0)
        {
            var best = remaining[0];
            kept.Add(best);
            remaining = remaining.Skip(1).Where(b => IoU(best, b) <= iouThreshold).ToList();
        }
        return kept;
    }

    public static double IoU(FaceBox a, FaceBox b)
    {
        var ix = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
}

/// <summary>Runs UltraFace through ONNX Runtime.</summary>
public sealed class FaceDetector(OnnxSessionCache sessions)
{
    public IReadOnlyList<FaceBox> Detect(IMagickImage<byte> source, string modelPath, double scoreThreshold = 0.5, CancellationToken cancellationToken = default)
    {
        var width = (int)source.Width;
        var height = (int)source.Height;

        byte[] rgb;
        using (var small = source.Clone())
        {
            small.BackgroundColor = MagickColors.White;
            small.Alpha(AlphaAction.Remove);
            small.Resize(new MagickGeometry(UltraFaceMath.InputWidth, UltraFaceMath.InputHeight) { IgnoreAspectRatio = true });
            rgb = ImageIo.GetBytes(small, PixelMapping.RGB);
        }
        cancellationToken.ThrowIfCancellationRequested();

        return sessions.Run(modelPath, session =>
        {
            var input = session.InputMetadata.First();
            var tensor = new DenseTensor<float>(UltraFaceMath.ToTensor(rgb), [1, 3, UltraFaceMath.InputHeight, UltraFaceMath.InputWidth]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(input.Key, tensor)]);

            // Find the two heads by their last dimension rather than by name: [1,N,2] confidences and [1,N,4] locations.
            var outputs = results.Select(r => r.AsTensor<float>()).ToList();
            var conf = outputs.FirstOrDefault(t => t.Dimensions.Length >= 1 && t.Dimensions[^1] == 2)
                ?? throw new ImageToolException("The face model has no [N,2] confidence output.");
            var loc = outputs.FirstOrDefault(t => t.Dimensions.Length >= 1 && t.Dimensions[^1] == 4)
                ?? throw new ImageToolException("The face model has no [N,4] box output.");
            try
            {
                return UltraFaceMath.Decode(conf.ToArray(), loc.ToArray(), width, height, scoreThreshold);
            }
            catch (ArgumentException ex)
            {
                throw new ImageToolException("The face model is not UltraFace RFB-320: " + ex.Message, ex);
            }
        });
    }
}
