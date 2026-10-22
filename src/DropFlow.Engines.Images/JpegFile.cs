using System.Text;

namespace DropFlow.Engines.Images;

/// <summary>One marker segment before the first scan. <see cref="Payload"/> excludes the marker and the length bytes; null for stand-alone markers.</summary>
internal sealed record JpegSegment(byte Marker, byte[]? Payload)
{
    public const byte App0 = 0xE0, App1 = 0xE1, App2 = 0xE2, App13 = 0xED, Sos = 0xDA, Eoi = 0xD9;

    public static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    public static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    public static readonly byte[] XmpExtensionHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    public static readonly byte[] PhotoshopHeader = Encoding.ASCII.GetBytes("Photoshop 3.0\0");
    public static readonly byte[] MpfHeader = "MPF\0"u8.ToArray();

    public bool IsExif => Marker == App1 && StartsWith(ExifHeader);
    public bool IsXmp => Marker == App1 && StartsWith(XmpHeader);
    public bool IsXmpExtension => Marker == App1 && StartsWith(XmpExtensionHeader);
    public bool IsPhotoshop => Marker == App13 && StartsWith(PhotoshopHeader);
    public bool IsMpf => Marker == App2 && StartsWith(MpfHeader);
    public bool IsApp0 => Marker == App0;

    /// <summary>Start-of-frame markers (C0–CF except DHT C4, JPG C8 and DAC CC).</summary>
    public bool IsStartOfFrame => Marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    public bool StartsWith(byte[] header) => Payload is { } p && p.AsSpan().StartsWith(header);

    /// <summary>The bytes after <paramref name="header"/>.</summary>
    public byte[] Body(byte[] header) => Payload![header.Length..];
}

/// <summary>
/// A JPEG split into its header segments, the compressed image data (first SOS marker up to and including the EOI of the
/// primary image) and anything stored after it (MPF secondary pictures, vendor trailers). The compressed data is never
/// decoded or changed: <see cref="ToBytes"/> writes it back byte for byte, so editing metadata cannot alter a single pixel.
/// </summary>
internal sealed class JpegFile
{
    /// <summary>Largest payload a segment can carry: the 16-bit length field counts itself.</summary>
    public const int MaxPayload = 65535 - 2;

    public List<JpegSegment> Segments { get; }
    public byte[] ImageData { get; }
    public byte[] Trailer { get; set; }

    private JpegFile(List<JpegSegment> segments, byte[] imageData, byte[] trailer)
    {
        Segments = segments;
        ImageData = imageData;
        Trailer = trailer;
    }

    public static bool LooksLikeJpeg(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <exception cref="InvalidDataException">The bytes are not a complete JPEG.</exception>
    public static JpegFile Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            throw new InvalidDataException("This is not a JPEG file (no start-of-image marker).");

        var segments = new List<JpegSegment>();
        var pos = 2;
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("The JPEG ends before its image data (the file is damaged or truncated).");
            if (data[pos] != 0xFF) throw new InvalidDataException($"The JPEG is damaged: expected a marker at byte {pos:N0}.");
            while (pos < data.Length && data[pos] == 0xFF) pos++; // fill bytes
            if (pos >= data.Length) throw new InvalidDataException("The JPEG ends before its image data (the file is damaged or truncated).");

            var marker = data[pos++];
            if (marker == JpegSegment.Sos)
            {
                var start = pos - 2;
                var end = EndOfImage(data, start);
                if (!segments.Any(s => s.IsStartOfFrame))
                    throw new InvalidDataException("The JPEG is damaged: it has no frame header.");
                return new JpegFile(segments, data[start..end], data[end..]);
            }
            if (marker == JpegSegment.Eoi || marker == 0x00)
                throw new InvalidDataException("The JPEG is damaged: it has no image data.");
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                segments.Add(new JpegSegment(marker, null));
                continue;
            }

            if (pos + 2 > data.Length) throw new InvalidDataException("The JPEG is truncated inside a header segment.");
            var length = (data[pos] << 8) | data[pos + 1];
            if (length < 2 || pos + length > data.Length) throw new InvalidDataException("The JPEG is truncated inside a header segment.");
            segments.Add(new JpegSegment(marker, data[(pos + 2)..(pos + length)]));
            pos += length;
        }
    }

    /// <summary>
    /// Walks the scans from the first SOS marker (at <paramref name="sos"/>) and returns the index just after the primary
    /// image's EOI marker. Entropy-coded bytes never contain FF followed by anything but 00 or a restart marker, so the first
    /// real marker FFD9 is the end of the picture.
    /// </summary>
    private static int EndOfImage(byte[] data, int sos)
    {
        var pos = sos;
        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF) { pos++; continue; }
            var marker = data[pos + 1];
            if (marker == JpegSegment.Eoi) return pos + 2;
            if (marker is 0x00 or 0xFF or 0x01 or >= 0xD0 and <= 0xD7) { pos += marker == 0xFF ? 1 : 2; continue; }

            // A marker with a length (SOS, DHT, DQT, DRI, COM, APPn between progressive scans): skip its header.
            if (pos + 4 > data.Length) break;
            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (length < 2) throw new InvalidDataException("The JPEG image data is damaged.");
            pos += 2 + length;
        }
        throw new InvalidDataException("The JPEG is truncated: its image data has no end marker.");
    }

    /// <summary>Width and height from the frame header, or null when there is none.</summary>
    public (int Width, int Height)? Dimensions
    {
        get
        {
            var sof = Segments.FirstOrDefault(s => s.IsStartOfFrame);
            if (sof?.Payload is not { Length: >= 5 } p) return null;
            return ((p[3] << 8) | p[4], (p[1] << 8) | p[2]);
        }
    }

    /// <exception cref="InvalidDataException">A segment is larger than a JPEG segment can hold.</exception>
    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        foreach (var segment in Segments)
        {
            output.WriteByte(0xFF);
            output.WriteByte(segment.Marker);
            if (segment.Payload is not { } payload) continue;
            if (payload.Length > MaxPayload)
                throw new InvalidDataException($"A metadata block is {payload.Length:N0} bytes; a JPEG segment holds at most {MaxPayload:N0} bytes (64 KB). Shorten the text.");
            var length = payload.Length + 2;
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)length);
            output.Write(payload);
        }
        output.Write(ImageData);
        output.Write(Trailer);
        return output.ToArray();
    }

    /// <summary>
    /// Replaces the segments matching <paramref name="match"/> with one segment holding <paramref name="payload"/>
    /// (or removes them when it is null). A new segment goes after the APP0/EXIF/XMP/Photoshop block at the start of the file.
    /// </summary>
    public void Replace(Func<JpegSegment, bool> match, byte marker, byte[]? payload, Func<JpegSegment, bool>[] insertAfter)
    {
        var first = Segments.FindIndex(s => match(s));
        Segments.RemoveAll(s => match(s));
        if (payload is null) return;
        if (payload.Length > MaxPayload)
            throw new InvalidDataException($"The metadata block is {payload.Length:N0} bytes; a JPEG segment holds at most {MaxPayload:N0} bytes (64 KB). Shorten the text.");

        var segment = new JpegSegment(marker, payload);
        if (first >= 0)
        {
            Segments.Insert(Math.Min(first, Segments.Count), segment);
            return;
        }

        // Leading APP0 run (JFIF/JFXX), then the preferred neighbours.
        var at = 0;
        while (at < Segments.Count && Segments[at].IsApp0) at++;
        for (var i = 0; i < Segments.Count; i++)
            if (insertAfter.Any(f => f(Segments[i]))) at = Math.Max(at, i + 1);
        Segments.Insert(at, segment);
    }
}
