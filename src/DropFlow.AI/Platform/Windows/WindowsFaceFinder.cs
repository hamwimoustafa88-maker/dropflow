using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Windows.Graphics.Imaging;
using WinFaceDetector = Windows.Media.FaceAnalysis.FaceDetector;

namespace DropFlow.AI.Imaging;

/// <summary>
/// Face detection built into Windows 10/11 (<c>Windows.Media.FaceAnalysis.FaceDetector</c>): on-device, no model to download. It finds
/// frontal and slightly turned faces; it gives no confidence, so every box has score 1. Large pictures are scaled down to
/// <see cref="MaxSide"/> pixels on the longest side for speed and the boxes are mapped back to the original size.
/// </summary>
public sealed class WindowsFaceFinder : IFaceFinder
{
    /// <summary>Longest side handed to the detector.</summary>
    public const int MaxSide = 2048;

    public string Name => "Windows face detection";

    /// <summary>False on Windows editions without the face-analysis component (some N/KN and Server images).</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return WinFaceDetector.IsSupported;
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or PlatformNotSupportedException or InvalidCastException)
            {
                return false;
            }
        }
    }

    public string? Unavailable() => IsSupported ? null : "Windows face detection is not available on this PC.";

    public async Task<IReadOnlyList<FaceBox>> FindAsync(IMagickImage<byte> image, CancellationToken cancellationToken)
    {
        int width = (int)image.Width, height = (int)image.Height;
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        byte[] bgra;
        int w, h;
        using (var small = image.Clone())
        {
            small.BackgroundColor = MagickColors.White;
            small.Alpha(AlphaAction.Remove);
            if (scale < 1) small.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale))) { IgnoreAspectRatio = true });
            w = (int)small.Width;
            h = (int)small.Height;
            bgra = ImageIo.GetBytes(small, PixelMapping.BGRA);
        }
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var detector = await WinFaceDetector.CreateAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var format = WinFaceDetector.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8)
                ? BitmapPixelFormat.Gray8
                : WinFaceDetector.GetSupportedBitmapPixelFormats().First();

            using var source = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
            using var input = SoftwareBitmap.Convert(source, format);
            var found = await detector.DetectFacesAsync(input).AsTask(cancellationToken).ConfigureAwait(false);

            var sx = (double)width / w;
            var sy = (double)height / h;
            return found
                .Select(f => new FaceBox(f.FaceBox.X * sx, f.FaceBox.Y * sy, f.FaceBox.Width * sx, f.FaceBox.Height * sy, 1.0))
                .Where(b => b.Width >= 1 && b.Height >= 1)
                .ToList();
        }
        catch (COMException ex)
        {
            throw new ImageToolException("Windows face detection failed: " + ex.Message, ex);
        }
    }
}
