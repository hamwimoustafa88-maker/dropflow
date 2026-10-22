// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images;

/// <summary>
/// How a stored picture is shown: first mirrored left-right (when <see cref="Mirror"/>), then turned <see cref="Rotation"/> degrees clockwise.
/// The eight EXIF orientation values are exactly these eight combinations, so turning or flipping a photo is arithmetic on this value.
/// </summary>
internal readonly record struct PhotoOrientation(bool Mirror, int Rotation)
{
    public static readonly PhotoOrientation Identity = new(false, 0);

    /// <summary>EXIF 1..8 (anything else is treated as 1, "as stored").</summary>
    public static PhotoOrientation FromExif(int value) => value switch
    {
        2 => new(true, 0),
        3 => new(false, 180),
        4 => new(true, 180),    // mirror top-bottom = mirror left-right, then half a turn
        5 => new(true, 270),    // transpose
        6 => new(false, 90),
        7 => new(true, 90),     // transverse
        8 => new(false, 270),
        _ => Identity,
    };

    public int ToExif() => (Mirror, Rotation) switch
    {
        (false, 0) => 1,
        (true, 0) => 2,
        (false, 180) => 3,
        (true, 180) => 4,
        (true, 270) => 5,
        (false, 90) => 6,
        (true, 90) => 7,
        _ => 8,
    };

    /// <summary>This orientation followed by <paramref name="next"/>. A mirror after a turn equals the mirror before the opposite turn.</summary>
    public PhotoOrientation Then(PhotoOrientation next) =>
        next.Mirror ? new(!Mirror, Norm(next.Rotation - Rotation)) : new(Mirror, Norm(Rotation + next.Rotation));

    /// <summary>What <c>img.edit</c> does, in its order: turn clockwise, then mirror left-right, then mirror top-bottom.</summary>
    public static PhotoOrientation Edit(int clockwise, bool flipH, bool flipV)
    {
        var o = new PhotoOrientation(false, Norm(clockwise));
        if (flipH) o = o.Then(new PhotoOrientation(true, 0));
        if (flipV) o = o.Then(new PhotoOrientation(true, 180));
        return o;
    }

    private static int Norm(int degrees) => ((degrees % 360) + 360) % 360;
}

/// <summary>
/// Reads and rewrites the EXIF orientation tag of a JPEG <b>without touching the compressed picture</b>, which is how a JPEG is turned without any
/// loss. If the file has an EXIF block with an orientation tag, those two bytes are changed in place; if it has no EXIF block at all, a minimal one
/// holding only the orientation is inserted. A file whose EXIF block lacks the tag (rare) returns null, and the caller re-encodes instead.
/// </summary>
internal static class JpegExif
{
    private const ushort OrientationTag = 0x0112;

    /// <summary>The orientation (1..8) the file declares, or 1 when it declares none or cannot be read.</summary>
    public static int ReadOrientation(ReadOnlySpan<byte> jpeg)
    {
        var at = FindOrientationValue(jpeg, out var bigEndian, out _);
        if (at < 0) return 1;
        var value = bigEndian ? jpeg[at] << 8 | jpeg[at + 1] : jpeg[at + 1] << 8 | jpeg[at];
        return value is >= 1 and <= 8 ? value : 1;
    }

    /// <summary>A copy of <paramref name="jpeg"/> declaring <paramref name="orientation"/>, or null when that cannot be done losslessly.</summary>
    public static byte[]? WithOrientation(byte[] jpeg, int orientation)
    {
        if (orientation is < 1 or > 8 || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var at = FindOrientationValue(jpeg, out var bigEndian, out var hasExif);
        if (at >= 0)
        {
            var copy = (byte[])jpeg.Clone();
            if (bigEndian) { copy[at] = 0; copy[at + 1] = (byte)orientation; }
            else { copy[at] = (byte)orientation; copy[at + 1] = 0; }
            return copy;
        }
        if (hasExif || !TryWalk(jpeg, out _)) return null;

        // No EXIF at all: insert APP1 "Exif\0\0" + a big-endian TIFF header + one IFD with the single orientation entry.
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,                         // APP1, length 34 (includes these two bytes)
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,   // TIFF header, IFD0 at offset 8
            0x00, 0x01,                                     // one entry
            0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1, 0x00, (byte)orientation, 0, 0,   // Orientation, SHORT, count 1, value
            0, 0, 0, 0,                                     // no next IFD
        ];
        var insertAt = 2;
        if (jpeg.Length > 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0) insertAt = 4 + (jpeg[4] << 8 | jpeg[5]);   // keep JFIF first
        if (insertAt > jpeg.Length) return null;
        return [.. jpeg.AsSpan(0, insertAt), .. app1, .. jpeg.AsSpan(insertAt)];
    }

    /// <summary>Everything from the first segment that is not APPn/COM: quantisation tables, frame header and the compressed scan.</summary>
    public static ReadOnlySpan<byte> ImageData(ReadOnlySpan<byte> jpeg)
    {
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (!(marker is >= 0xE0 and <= 0xEF || marker == 0xFE)) return jpeg[pos..];
            pos += 2 + (jpeg[pos + 2] << 8 | jpeg[pos + 3]);
        }
        return jpeg[Math.Min(pos, jpeg.Length)..];
    }

    private static bool TryWalk(ReadOnlySpan<byte> jpeg, out int scanStart)
    {
        scanStart = -1;
        var pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return false;
            var marker = jpeg[pos + 1];
            if (marker == 0xDA) { scanStart = pos; return true; }
            var length = jpeg[pos + 2] << 8 | jpeg[pos + 3];
            if (length < 2 || pos + 2 + length > jpeg.Length) return false;
            pos += 2 + length;
        }
        return false;
    }

    private static int U16(ReadOnlySpan<byte> b, int at, bool bigEndian) => bigEndian ? b[at] << 8 | b[at + 1] : b[at + 1] << 8 | b[at];

    private static long U32(ReadOnlySpan<byte> b, int at, bool bigEndian) => bigEndian
        ? (long)b[at] << 24 | (long)b[at + 1] << 16 | (long)b[at + 2] << 8 | b[at + 3]
        : (long)b[at + 3] << 24 | (long)b[at + 2] << 16 | (long)b[at + 1] << 8 | b[at];

    /// <summary>Byte offset of the orientation value inside the EXIF IFD0, or -1. <paramref name="hasExif"/> tells whether an EXIF block exists.</summary>
    private static int FindOrientationValue(ReadOnlySpan<byte> jpeg, out bool bigEndian, out bool hasExif)
    {
        bigEndian = false;
        hasExif = false;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return -1;

        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker is 0xDA or 0xD9) return -1;
            var length = jpeg[pos + 2] << 8 | jpeg[pos + 3];
            if (length < 2 || pos + 2 + length > jpeg.Length) return -1;
            var data = pos + 4;
            var end = pos + 2 + length;
            if (marker == 0xE1 && end - data >= 14 && jpeg.Slice(data, 6).SequenceEqual("Exif\0\0"u8))
            {
                hasExif = true;
                var tiff = data + 6;
                var be = jpeg[tiff] == 'M' && jpeg[tiff + 1] == 'M';
                bigEndian = be;
                if (!be && !(jpeg[tiff] == 'I' && jpeg[tiff + 1] == 'I')) return -1;
                if (U16(jpeg, tiff + 2, be) != 42) return -1;
                var ifd = U32(jpeg, tiff + 4, be);
                if (ifd < 8 || tiff + ifd + 2 > end) return -1;
                var ifdAt = tiff + (int)ifd;
                var count = U16(jpeg, ifdAt, be);
                for (var i = 0; i < count; i++)
                {
                    var entry = ifdAt + 2 + i * 12;
                    if (entry + 12 > end) return -1;
                    if (U16(jpeg, entry, be) == OrientationTag) return U16(jpeg, entry + 2, be) == 3 ? entry + 8 : -1;   // must be a SHORT stored inline
                }
                return -1;
            }
            pos = end;
        }
        return -1;
    }
}
