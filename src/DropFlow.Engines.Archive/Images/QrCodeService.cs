using ImageMagick;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using ZXing.Rendering;

namespace DropFlow.Engines.Archive.Images;

public enum QrErrorCorrection { L, M, Q, H }

/// <param name="Size">Width and height of the PNG in pixels.</param>
/// <param name="QuietZone">Blank border around the code, in QR modules (the QR standard asks for 4).</param>
public sealed record QrCreateOptions(QrErrorCorrection ErrorCorrection = QrErrorCorrection.M, int Size = 512, int QuietZone = 4);

/// <param name="Format">The barcode symbology, e.g. QR_CODE, EAN_13, CODE_128.</param>
public sealed record BarcodeReadResult(string Text, string Format);

/// <summary>The thing the text is too big for.</summary>
public sealed class QrTooLargeException(string message) : Exception(message);

/// <summary>QR creation and barcode reading on top of ZXing.Net and Magick.NET. In-process, offline, no files other than the ones given.</summary>
public static class QrCodeService
{
    public const int MinSize = 64;
    public const int MaxSize = 4096;

    /// <summary>Renders <paramref name="text"/> as a QR code PNG (two colours, exact square size).</summary>
    /// <exception cref="QrTooLargeException">The text does not fit in a QR code at the chosen error-correction level.</exception>
    public static byte[] CreatePng(string text, QrCreateOptions? options = null)
    {
        options ??= new QrCreateOptions();
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Nothing to encode.", nameof(text));

        var size = Math.Clamp(options.Size, MinSize, MaxSize);
        var encoding = new QrCodeEncodingOptions
        {
            Width = size,
            Height = size,
            Margin = Math.Clamp(options.QuietZone, 0, 16),
            ErrorCorrection = options.ErrorCorrection switch
            {
                QrErrorCorrection.L => ErrorCorrectionLevel.L,
                QrErrorCorrection.Q => ErrorCorrectionLevel.Q,
                QrErrorCorrection.H => ErrorCorrectionLevel.H,
                _ => ErrorCorrectionLevel.M,
            },
        };

        // Plain ASCII needs no character-set marker (and a few old scanners trip over it); anything else is UTF-8 with an ECI header.
        if (text.Any(c => c > 127)) encoding.CharacterSet = "UTF-8";

        var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = encoding };
        PixelData pixels;
        try
        {
            pixels = writer.Write(text);
        }
        catch (WriterException ex)
        {
            throw new QrTooLargeException($"The text is too long for a QR code at error-correction level {options.ErrorCorrection}: {ex.Message}");
        }

        using var image = new MagickImage();
        image.ReadPixels(pixels.Pixels, new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, PixelMapping.BGRA));
        image.Format = MagickFormat.Png8; // two colours: a palette PNG is a few KB
        return image.ToByteArray();
    }

    /// <summary>Finds every QR code and 1D/2D barcode in an image file. Returns an empty list when there is none.</summary>
    public static IReadOnlyList<BarcodeReadResult> Read(string imagePath, CancellationToken cancellationToken = default)
    {
        using var image = new MagickImage(imagePath);
        return Read(image, cancellationToken);
    }

    public static IReadOnlyList<BarcodeReadResult> Read(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        using var image = new MagickImage(imageBytes);
        return Read(image, cancellationToken);
    }

    private static IReadOnlyList<BarcodeReadResult> Read(MagickImage image, CancellationToken cancellationToken)
    {
        image.AutoOrient();
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove); // transparent PNG codes would otherwise be read as solid black

        // Try the picture as it is, then helped: tiny images enlarged, huge photos reduced.
        foreach (var scale in ScalesFor(image))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var candidate = (MagickImage)image.Clone();
            if (scale != 1.0) candidate.Resize(new Percentage(scale * 100));

            var found = Decode(candidate);
            if (found.Count > 0) return found;
        }

        return [];
    }

    private static IEnumerable<double> ScalesFor(MagickImage image)
    {
        yield return 1.0;
        var longest = Math.Max(image.Width, image.Height);
        if (longest < 400) { yield return 3.0; yield return 2.0; }
        else if (longest > 1600) { yield return 0.5; yield return 0.25; }
    }

    private static List<BarcodeReadResult> Decode(MagickImage image)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? [];
        if (rgb.Length < width * height * 3) return [];

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = { TryHarder = true, TryInverted = true, CharacterSet = "UTF-8" },
        };

        var source = new RGBLuminanceSource(rgb, width, height, RGBLuminanceSource.BitmapFormat.RGB24);
        Result[]? results = null;
        try { results = reader.DecodeMultiple(source); }
        catch (ReaderException) { /* multi-decode found nothing; try single below */ }

        if (results is null || results.Length == 0)
        {
            var single = reader.Decode(source);
            results = single is null ? [] : [single];
        }

        return results
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .Select(r => new BarcodeReadResult(r.Text, r.BarcodeFormat.ToString()))
            .DistinctBy(r => (r.Text, r.Format))
            .ToList();
    }
}
