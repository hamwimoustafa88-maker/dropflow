// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Web;

/// <summary>
/// WOFF 1.0 (W3C Recommendation, "WOFF File Format 1.0"): a 44-byte header, a directory of 20-byte entries sorted by tag, then every table
/// compressed on its own with zlib (RFC 1950) when that makes it smaller, each starting on a four-byte boundary. Extended metadata and
/// private data are optional and dropped on decoding (they are not part of the font).
/// </summary>
internal static class Woff
{
    public const uint Signature = 0x774F4646; // 'wOFF'
    private const int HeaderSize = 44;
    private const int EntrySize = 20;

    public static SfntFont Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(data) != Signature)
            throw new FontFailure("The file is not a WOFF font.");
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (flavor == SfntFont.CollectionTag) throw new FontFailure("WOFF font collections are not supported.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (length > data.Length) throw new FontFailure("The WOFF file is cut short.");
        if (numTables == 0 || HeaderSize + numTables * EntrySize > data.Length) throw new FontFailure("The WOFF table directory is damaged.");

        var font = new SfntFont(flavor);
        for (var i = 0; i < numTables; i++)
        {
            var entry = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            var tag = SfntFont.Tag(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var compLength = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var origLength = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (offset > (uint)data.Length || compLength > (uint)data.Length - offset)
                throw new FontFailure($"The WOFF table '{tag.TrimEnd()}' points outside the file.");
            if (compLength > origLength) throw new FontFailure($"The WOFF table '{tag.TrimEnd()}' is damaged (compressed size is larger than the table).");

            var stored = data.Slice((int)offset, (int)compLength);
            var table = compLength == origLength ? stored.ToArray() : Inflate(stored, (int)origLength, tag);
            if (!font.Tables.TryAdd(tag, table)) throw new FontFailure($"The WOFF file lists the '{tag.TrimEnd()}' table twice.");
        }
        return font;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> stored, int length, string tag)
    {
        var output = new byte[length];
        try
        {
            using var z = new ZLibStream(new MemoryStream(stored.ToArray()), CompressionMode.Decompress);
            var read = 0;
            while (read < length)
            {
                var n = z.Read(output, read, length - read);
                if (n == 0) break;
                read += n;
            }
            if (read != length || z.ReadByte() != -1)
                throw new FontFailure($"The WOFF table '{tag.TrimEnd()}' does not decompress to its declared size.");
        }
        catch (InvalidDataException ex)
        {
            throw new FontFailure($"The WOFF table '{tag.TrimEnd()}' is damaged ({ex.Message}).");
        }
        return output;
    }

    /// <summary>
    /// Encodes <paramref name="font"/>. The font is first written as an sfnt (so head.checkSumAdjustment and the checksums the WOFF directory
    /// records are those of the font a decoder rebuilds).
    /// </summary>
    public static byte[] Encode(SfntFont font, CancellationToken ct = default)
    {
        font = font.Clone();
        font.Write(); // fixes head.checkSumAdjustment in the head table
        var tables = font.Tables.ToList();

        var stored = new List<(string Tag, byte[] Data, int OrigLength, uint Checksum)>();
        foreach (var (tag, data) in tables)
        {
            ct.ThrowIfCancellationRequested();
            var compressed = Deflate(data);
            stored.Add((tag, compressed.Length < data.Length ? compressed : data, data.Length, SfntFont.TableChecksum(tag, data)));
        }

        var offset = HeaderSize + EntrySize * stored.Count;
        var total = offset + stored.Sum(s => SfntFont.Pad4(s.Data.Length));
        var output = new byte[total];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, Signature);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], font.Flavor);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)stored.Count);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], (uint)font.SfntSize);
        Version(font, out var major, out var minor);
        BinaryPrimitives.WriteUInt16BigEndian(span[20..], major);
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], minor);
        // metaOffset, metaLength, metaOrigLength, privOffset, privLength stay zero: no extended metadata, no private data.

        for (var i = 0; i < stored.Count; i++)
        {
            var (tag, data, origLength, checksum) = stored[i];
            var entry = span.Slice(HeaderSize + i * EntrySize, EntrySize);
            Encoding.ASCII.GetBytes(tag, entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)origLength);
            BinaryPrimitives.WriteUInt32BigEndian(entry[16..], checksum);
            data.CopyTo(span[offset..]);
            offset += SfntFont.Pad4(data.Length);
        }
        return output;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return buffer.ToArray();
    }

    /// <summary>The font's own version (head.fontRevision, a 16.16 number) as the WOFF major.minor version, as the specification suggests.</summary>
    internal static void Version(SfntFont font, out ushort major, out ushort minor)
    {
        major = 1;
        minor = 0;
        if (font.Table("head") is { Length: >= 8 } head)
        {
            major = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
            minor = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
        }
    }
}
