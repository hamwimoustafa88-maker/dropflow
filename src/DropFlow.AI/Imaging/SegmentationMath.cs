using ImageMagick;

namespace DropFlow.AI.Imaging;

public enum InputScaling
{
    /// <summary>pixel / 255.</summary>
    DivideBy255,

    /// <summary>pixel / (largest value in this image), as the U²-Net reference code does.</summary>
    DivideByMax,
}

/// <summary>
/// How to feed one background-removal model and read its answer, taken from its model card. Everything here is data; the maths
/// that uses it lives in <see cref="SegmentationMath"/> and is tested without any model.
/// </summary>
/// <param name="ModelId">Catalog id.</param>
/// <param name="Width">Network input size; used when the ONNX file does not declare a fixed one.</param>
/// <param name="ApplySigmoid">True when the model outputs logits.</param>
/// <param name="MinMaxNormalize">True when the output must be stretched to 0..1 (U²-Net's reference code does this).</param>
public sealed record BackgroundModelSpec(
    string ModelId,
    int Width,
    int Height,
    float[] Mean,
    float[] Std,
    InputScaling Scaling,
    bool ApplySigmoid,
    bool MinMaxNormalize,
    FilterType InputFilter)
{
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>U²-Net-P (rembg reference): 320x320, /max, ImageNet mean/std, probability output stretched to 0..1.</summary>
    public static BackgroundModelSpec U2NetP { get; } =
        new("u2netp", 320, 320, ImageNetMean, ImageNetStd, InputScaling.DivideByMax, ApplySigmoid: false, MinMaxNormalize: true, FilterType.Lanczos);

    /// <summary>BiRefNet-lite (onnx-community card): 1024x1024, /255, ImageNet mean/std, logits (sigmoid).</summary>
    public static BackgroundModelSpec BiRefNetLite { get; } =
        new("birefnet-lite", 1024, 1024, ImageNetMean, ImageNetStd, InputScaling.DivideBy255, ApplySigmoid: true, MinMaxNormalize: false, FilterType.Triangle);

    /// <summary>Generic spec for tests and unknown U²-Net style files.</summary>
    public static BackgroundModelSpec? For(string modelId) => modelId switch
    {
        "u2netp" => U2NetP,
        "birefnet-lite" => BiRefNetLite,
        _ => null,
    };

    /// <summary>Models in order of preference when the user leaves the choice on "auto".</summary>
    public static IReadOnlyList<string> AutoPreference { get; } = ["birefnet-lite", "u2netp"];
}

/// <summary>Pure pre- and post-processing for segmentation networks. No model, no files.</summary>
public static class SegmentationMath
{
    /// <summary>
    /// Interleaved 8-bit RGB (already resized to the network size) to a planar float tensor laid out NCHW (batch of one).
    /// </summary>
    public static float[] ToTensor(ReadOnlySpan<byte> rgb, int width, int height, BackgroundModelSpec spec)
    {
        var pixels = width * height;
        if (rgb.Length != pixels * 3) throw new ArgumentException($"Expected {pixels * 3} bytes for {width}x{height} RGB but got {rgb.Length}.", nameof(rgb));

        float divisor = 255f;
        if (spec.Scaling == InputScaling.DivideByMax)
        {
            byte max = 0;
            foreach (var b in rgb) if (b > max) max = b;
            divisor = Math.Max(max, (byte)1);
        }

        var tensor = new float[3 * pixels];
        for (var i = 0; i < pixels; i++)
        {
            for (var c = 0; c < 3; c++)
                tensor[c * pixels + i] = (rgb[i * 3 + c] / divisor - spec.Mean[c]) / spec.Std[c];
        }
        return tensor;
    }

    public static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    /// <summary>
    /// Network output to an 8-bit mask (255 = keep). Applies the sigmoid and/or min-max stretch the spec asks for, clamps to 0..1,
    /// and, when <paramref name="hardThreshold"/> is above zero, cuts the mask into hard 0/255 edges at that level.
    /// </summary>
    public static byte[] ToMask(ReadOnlySpan<float> prediction, BackgroundModelSpec spec, double hardThreshold = 0)
    {
        var values = new float[prediction.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = spec.ApplySigmoid ? Sigmoid(prediction[i]) : prediction[i];

        if (spec.MinMaxNormalize && values.Length > 0)
        {
            var min = values.Min();
            var max = values.Max();
            var range = max - min;
            for (var i = 0; i < values.Length; i++)
                values[i] = range > 1e-6f ? (values[i] - min) / range : Math.Clamp(values[i], 0f, 1f);
        }

        var mask = new byte[values.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            var v = Math.Clamp(values[i], 0f, 1f);
            mask[i] = hardThreshold > 0
                ? (v >= hardThreshold ? (byte)255 : (byte)0)
                : (byte)MathF.Round(v * 255f);
        }
        return mask;
    }

    /// <summary>Cuts an 8-bit mask into hard 0/255 values at <paramref name="level"/> (0..1). Done after scaling so the edge is crisp at full size.</summary>
    public static void Threshold(Span<byte> mask, double level)
    {
        var cut = level * 255.0;
        for (var i = 0; i < mask.Length; i++) mask[i] = mask[i] >= cut ? (byte)255 : (byte)0;
    }

    /// <summary>
    /// Multiplies the alpha of interleaved RGBA pixels by the mask (same width and height). Multiplying instead of replacing keeps
    /// areas that were already transparent in the source transparent.
    /// </summary>
    public static void ApplyAlpha(Span<byte> rgba, ReadOnlySpan<byte> mask)
    {
        if (rgba.Length != mask.Length * 4) throw new ArgumentException("Mask size does not match the image.", nameof(mask));
        for (var i = 0; i < mask.Length; i++)
        {
            var a = rgba[i * 4 + 3];
            rgba[i * 4 + 3] = (byte)((a * mask[i] + 127) / 255);
        }
    }

    /// <summary>Blends interleaved RGBA pixels over an opaque colour and drops the alpha: the result is RGB.</summary>
    public static byte[] FlattenOnto(ReadOnlySpan<byte> rgba, byte r, byte g, byte b)
    {
        var pixels = rgba.Length / 4;
        var rgb = new byte[pixels * 3];
        for (var i = 0; i < pixels; i++)
        {
            var a = rgba[i * 4 + 3];
            rgb[i * 3] = (byte)((rgba[i * 4] * a + r * (255 - a) + 127) / 255);
            rgb[i * 3 + 1] = (byte)((rgba[i * 4 + 1] * a + g * (255 - a) + 127) / 255);
            rgb[i * 3 + 2] = (byte)((rgba[i * 4 + 2] * a + b * (255 - a) + 127) / 255);
        }
        return rgb;
    }
}
