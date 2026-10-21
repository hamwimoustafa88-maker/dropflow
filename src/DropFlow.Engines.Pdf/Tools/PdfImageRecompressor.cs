using System.IO.Compression;
using DropFlow.Engines.Pdf.Infrastructure;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// Shrinks the pictures inside a PDF while leaving text and vectors untouched. JPEG pictures and plain 8-bit RGB/gray Flate pictures
/// are decoded, scaled down when they carry more detail than a full-page picture could ever show at <c>maxDpi</c>, and re-encoded
/// as JPEG. A picture is only replaced when the new data is at least 10 % smaller. Anything unusual (masks, palettes, CMYK, 16-bit,
/// JPEG 2000, CCITT) is left exactly as it was.
/// </summary>
internal static class PdfImageRecompressor
{
    public sealed record Result(int ImagesFound, int ImagesRecompressed, long BytesSaved);

    public static Result Recompress(string inputPath, string outputPath, int quality, double maxDpi, CancellationToken ct)
    {
        using var document = PdfSharpSupport.OpenForModify(inputPath);
        var seen = new HashSet<int>();
        int found = 0, changed = 0;
        long saved = 0;

        foreach (var page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var longSide = Math.Max(page.Width.Point, page.Height.Point);
            var limitPixels = Math.Max(256, (int)(longSide * maxDpi / 72.0));

            var xobjects = page.Resources?.Elements.GetDictionary("/XObject");
            if (xobjects is null) continue;

            foreach (var item in xobjects.Elements.Values)
            {
                if (item is not PdfReference reference || reference.Value is not PdfDictionary image) continue;
                if (image.Elements.GetName("/Subtype") != "/Image") continue;
                if (!seen.Add(reference.ObjectNumber)) continue;
                found++;

                var before = image.Stream?.Value?.LongLength ?? 0;
                if (TryRecompress(image, quality, limitPixels))
                {
                    changed++;
                    saved += before - image.Stream!.Value.LongLength;
                }
            }
        }

        PdfSharpSupport.ApplyDefaultOptions(document);
        document.Save(outputPath);
        return new Result(found, changed, saved);
    }

    private static bool TryRecompress(PdfDictionary image, int quality, int limitPixels)
    {
        var stream = image.Stream;
        if (stream?.Value is null || stream.Value.Length < 4096) return false; // not worth the quality loss

        var elements = image.Elements;
        if (elements.GetBoolean("/ImageMask") || elements.ContainsKey("/Mask") || elements.ContainsKey("/Decode")) return false;

        var width = elements.GetInteger("/Width");
        var height = elements.GetInteger("/Height");
        if (width < 16 || height < 16) return false;

        var components = ComponentCount(image);
        if (components is not (1 or 3)) return false;

        byte[]? jpeg;
        try
        {
            using var pixels = Decode(image, width, height, components);
            if (pixels is null) return false;

            var scale = Math.Min(1.0, (double)limitPixels / Math.Max(width, height));
            if (scale < 0.98)
            {
                var newW = Math.Max(8, (uint)Math.Round(width * scale));
                var newH = Math.Max(8, (uint)Math.Round(height * scale));
                pixels.Resize(newW, newH);
            }

            pixels.Format = MagickFormat.Jpeg;
            pixels.Quality = (uint)quality;
            jpeg = pixels.ToByteArray();
            if (jpeg.Length > stream.Value.Length * 0.9) return false;

            elements.SetInteger("/Width", (int)pixels.Width);
            elements.SetInteger("/Height", (int)pixels.Height);
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return false; // keep the original picture rather than fail the whole file
        }

        stream.Value = jpeg;
        elements.SetName("/Filter", "/DCTDecode");
        elements.Remove("/DecodeParms");
        elements.SetInteger("/BitsPerComponent", 8);
        return true;
    }

    /// <summary>1 for DeviceGray, 3 for DeviceRGB (or an ICC profile with 1 or 3 channels), otherwise 0 (unsupported).</summary>
    private static int ComponentCount(PdfDictionary image)
    {
        var space = image.Elements["/ColorSpace"];
        if (space is PdfReference r) space = r.Value;
        switch (space)
        {
            case PdfName name:
                return name.Value switch { "/DeviceGray" => 1, "/DeviceRGB" => 3, _ => 0 };
            case PdfArray array when array.Elements.Count == 2 && (array.Elements[0] as PdfName)?.Value == "/ICCBased":
                var profile = array.Elements[1] is PdfReference pr ? pr.Value as PdfDictionary : array.Elements[1] as PdfDictionary;
                return profile?.Elements.GetInteger("/N") switch { 1 => 1, 3 => 3, _ => 0 };
            default:
                return 0;
        }
    }

    private static MagickImage? Decode(PdfDictionary image, int width, int height, int components)
    {
        var filters = FilterNames(image);
        var data = image.Stream!.Value;

        if (filters.SequenceEqual(["/DCTDecode"]))
        {
            var info = new MagickImageInfo(data);
            if (info.ColorSpace == ColorSpace.CMYK) return null;
            var jpeg = new MagickImage(data);
            if ((jpeg.ChannelCount is >= 3 ? 3 : 1) != components) { jpeg.Dispose(); return null; }
            return jpeg;
        }

        if (filters.SequenceEqual(["/FlateDecode"]) && image.Elements.GetInteger("/BitsPerComponent") == 8)
        {
            var raw = Inflate(data);
            raw = UndoPredictor(raw, image, width, components);
            if (raw is null || raw.Length != (long)width * height * components) return null;

            var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, components == 3 ? "RGB" : "I");
            var pixels = new MagickImage();
            pixels.ReadPixels(raw, settings);
            return pixels;
        }

        return null;
    }

    private static List<string> FilterNames(PdfDictionary image)
    {
        var filter = image.Elements["/Filter"];
        if (filter is PdfReference r) filter = r.Value;
        return filter switch
        {
            PdfName name => [name.Value],
            PdfArray array => array.Elements.Select(e => (e as PdfName)?.Value ?? "?").ToList(),
            _ => [],
        };
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Reverses the PNG row predictors (/Predictor 10..15) that Flate image streams commonly use. Returns null for anything else.</summary>
    internal static byte[]? UndoPredictor(byte[] data, PdfDictionary image, int width, int components)
    {
        var parms = image.Elements["/DecodeParms"];
        if (parms is PdfReference r) parms = r.Value;
        if (parms is PdfArray array && array.Elements.Count == 1) parms = array.Elements[0] is PdfReference ar ? ar.Value : array.Elements[0];
        if (parms is not PdfDictionary dict) return data;

        var predictor = dict.Elements.ContainsKey("/Predictor") ? dict.Elements.GetInteger("/Predictor") : 1;
        if (predictor <= 1) return data;
        if (predictor < 10) return null; // TIFF predictor 2 is not handled

        var columns = dict.Elements.ContainsKey("/Columns") ? dict.Elements.GetInteger("/Columns") : 1;
        var colors = dict.Elements.ContainsKey("/Colors") ? dict.Elements.GetInteger("/Colors") : 1;
        if (columns != width || colors != components) return null;
        return UndoPngPredictor(data, columns * colors, colors);
    }

    internal static byte[]? UndoPngPredictor(byte[] data, int rowBytes, int bytesPerPixel)
    {
        var stride = rowBytes + 1;
        if (data.Length % stride != 0) return null;
        var rows = data.Length / stride;
        var output = new byte[rows * rowBytes];

        for (var y = 0; y < rows; y++)
        {
            var filter = data[y * stride];
            var src = y * stride + 1;
            var dst = y * rowBytes;
            var prev = dst - rowBytes;
            for (var x = 0; x < rowBytes; x++)
            {
                int left = x >= bytesPerPixel ? output[dst + x - bytesPerPixel] : 0;
                int up = y > 0 ? output[prev + x] : 0;
                int upLeft = y > 0 && x >= bytesPerPixel ? output[prev + x - bytesPerPixel] : 0;
                var value = data[src + x];
                output[dst + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + ((left + up) >> 1)),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => value,
                };
                if (filter > 4) return null;
            }
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
