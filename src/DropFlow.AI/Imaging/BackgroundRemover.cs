using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DropFlow.AI.Imaging;

public enum BackgroundFill { Transparent, White, Black }

/// <summary>
/// The background-removal pipeline: resize to the network size, normalise per model card, run ONNX Runtime, turn the output into a
/// mask (sigmoid / min-max / threshold), scale the mask back to the photo and use it as the alpha channel.
/// </summary>
public sealed class BackgroundRemover(OnnxSessionCache sessions)
{
    /// <summary>Returns a new RGBA image the size of <paramref name="source"/>. The source is not modified.</summary>
    public MagickImage Remove(IMagickImage<byte> source, string modelPath, BackgroundModelSpec spec, double hardThreshold = 0, BackgroundFill fill = BackgroundFill.Transparent, CancellationToken cancellationToken = default)
    {
        var width = (int)source.Width;
        var height = (int)source.Height;

        // 1) Mask at network resolution.
        var (mask, maskWidth, maskHeight) = sessions.Run(modelPath, session => Predict(session, source, spec, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        // 2) Scale the mask up to the photo (smoothly, so edges stay soft even from a 320 px network).
        byte[] fullMask;
        if (maskWidth == width && maskHeight == height)
        {
            fullMask = mask;
        }
        else
        {
            using var maskImage = ImageIo.FromGray(mask, maskWidth, maskHeight);
            maskImage.FilterType = FilterType.Triangle;
            maskImage.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
            fullMask = ImageIo.GetGray(maskImage);
        }

        // Hard edges are cut at full size: cutting the small mask first would be blurred again by the scaling.
        if (hardThreshold > 0) SegmentationMath.Threshold(fullMask, hardThreshold);

        // 3) Mask becomes alpha.
        var rgba = ImageIo.GetBytes(source, PixelMapping.RGBA);
        SegmentationMath.ApplyAlpha(rgba, fullMask);

        if (fill == BackgroundFill.Transparent) return ImageIo.FromBytes(rgba, width, height, PixelMapping.RGBA);

        var value = fill == BackgroundFill.White ? (byte)255 : (byte)0;
        return ImageIo.FromBytes(SegmentationMath.FlattenOnto(rgba, value, value, value), width, height, PixelMapping.RGB);
    }

    private static (byte[] Mask, int Width, int Height) Predict(InferenceSession session, IMagickImage<byte> source, BackgroundModelSpec spec, CancellationToken ct)
    {
        var input = session.InputMetadata.First();
        var shape = input.Value.Dimensions; // e.g. [1,3,320,320] or [1,3,-1,-1]
        if (input.Value.ElementType != typeof(float))
            throw new ImageToolException($"The model expects {input.Value.ElementType.Name} input; only float32 models are supported.");

        var netHeight = shape.Length >= 4 && shape[2] > 0 ? shape[2] : spec.Height;
        var netWidth = shape.Length >= 4 && shape[3] > 0 ? shape[3] : spec.Width;

        byte[] rgb;
        using (var resized = source.Clone())
        {
            resized.FilterType = spec.InputFilter;
            resized.BackgroundColor = MagickColors.White;
            resized.Alpha(AlphaAction.Remove); // transparent pixels become the background colour (white) for the network
            resized.Resize(new MagickGeometry((uint)netWidth, (uint)netHeight) { IgnoreAspectRatio = true });
            rgb = ImageIo.GetBytes(resized, PixelMapping.RGB);
        }

        ct.ThrowIfCancellationRequested();
        var tensor = new DenseTensor<float>(SegmentationMath.ToTensor(rgb, netWidth, netHeight, spec), [1, 3, netHeight, netWidth]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(input.Key, tensor)]);

        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (dims.Length < 2) throw new ImageToolException("The model returned an unexpected output shape.");
        var outHeight = dims[^2];
        var outWidth = dims[^1];
        if (output.Length != outWidth * outHeight)
            throw new ImageToolException($"The model returned {output.Length} values for a {outWidth}x{outHeight} mask (expected a single channel).");

        return (SegmentationMath.ToMask(output.ToArray(), spec), outWidth, outHeight);
    }
}
