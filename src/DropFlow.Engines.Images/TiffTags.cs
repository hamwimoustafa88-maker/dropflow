// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace DropFlow.Engines.Images;

/// <summary>
/// The text fields a TIFF keeps in its own first directory (IFD0): Artist, Copyright, ImageDescription, Make, Model, Software and
/// DateTime are TIFF tags (the same numbers EXIF uses), not an EXIF block. Magick.NET reads them but does not write Artist or
/// Copyright, so DropFlow edits IFD0 itself: the picture data is never moved, a new directory with the changed entries is appended and
/// the header points to it. Text is written as UTF-8 with a terminating NUL (what ExifTool and Windows do for non-Latin text).
/// </summary>
internal static class TiffTags
{
    public static readonly IReadOnlyDictionary<string, ushort> TextTags = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["ImageDescription"] = 270,
        ["Make"] = 271,
        ["Model"] = 272,
        ["Software"] = 305,
        ["DateTime"] = 306,
        ["Artist"] = 315,
        ["Copyright"] = 33432,
    };

    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    private const ushort AsciiType = 2;

    /// <summary>The text tags present in IFD0, by name ("Artist" → "Moustafa").</summary>
    public static Dictionary<string, string> Read(byte[] tiff)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryHeader(tiff, out var little, out var ifd)) return result;
        var (entries, _) = ReadDirectory(tiff, little, ifd);
        foreach (var (name, tag) in TextTags)
        {
            var entry = entries.FirstOrDefault(e => e.Tag == tag && e.Type == AsciiType);
            if (entry is null) continue;
            var bytes = entry.Count <= 4 ? entry.Value.AsSpan(0, (int)entry.Count) : Slice(tiff, Offset(entry.Value, little), entry.Count);
            var text = Decode(bytes);
            if (text.Length > 0) result[name] = text;
        }
        return result;
    }

    /// <summary>A copy of <paramref name="tiff"/> with these text tags set (a null value removes the tag).</summary>
    /// <exception cref="InvalidDataException">Not a classic TIFF (BigTIFF and damaged files are refused).</exception>
    public static byte[] Write(byte[] tiff, IReadOnlyDictionary<string, string?> changes)
    {
        if (!TryHeader(tiff, out var little, out var ifd))
            throw new InvalidDataException("The file is not a TIFF that DropFlow can edit (only classic TIFF, not BigTIFF).");
        var (entries, next) = ReadDirectory(tiff, little, ifd);

        var tags = changes.Keys.Select(k => TextTags.TryGetValue(k, out var t) ? t : throw new ArgumentException($"'{k}' is not a TIFF text tag.")).ToHashSet();
        var kept = entries.Where(e => !tags.Contains(e.Tag)).ToList();

        var output = new MemoryStream();
        output.Write(tiff);
        Align(output);
        foreach (var (name, value) in changes)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var data = Encoding.UTF8.GetBytes(value.Trim()).Append((byte)0).ToArray();
            var raw = new byte[4];
            if (data.Length <= 4) data.CopyTo(raw, 0);
            else
            {
                WriteU32(raw, (uint)output.Position, little);
                output.Write(data);
                Align(output);
            }
            kept.Add(new Entry(TextTags[name], AsciiType, (uint)data.Length, raw));
        }

        var directory = (uint)output.Position;
        var buffer = new byte[2 + kept.Count * 12 + 4];
        WriteU16(buffer.AsSpan(0), (ushort)kept.Count, little);
        var at = 2;
        foreach (var e in kept.OrderBy(e => e.Tag))
        {
            WriteU16(buffer.AsSpan(at), e.Tag, little);
            WriteU16(buffer.AsSpan(at + 2), e.Type, little);
            WriteU32(buffer.AsSpan(at + 4), e.Count, little);
            e.Value.CopyTo(buffer, at + 8);
            at += 12;
        }
        WriteU32(buffer.AsSpan(at), next, little);
        output.Write(buffer);
        if (output.Length > uint.MaxValue) throw new InvalidDataException("The TIFF would be larger than 4 GB.");

        var result = output.ToArray();
        WriteU32(result.AsSpan(4), directory, little);
        return result;
    }

    private static bool TryHeader(byte[] tiff, out bool little, out uint ifd)
    {
        little = false;
        ifd = 0;
        if (tiff.Length < 8) return false;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (!(tiff[0] == 'M' && tiff[1] == 'M')) return false;
        if (ReadU16(tiff, 2, little) != 42) return false; // 43 = BigTIFF
        ifd = ReadU32(tiff, 4, little);
        return ifd >= 8 && ifd + 2 <= tiff.Length;
    }

    private static (List<Entry> Entries, uint Next) ReadDirectory(byte[] tiff, bool little, uint ifd)
    {
        var count = ReadU16(tiff, (int)ifd, little);
        var end = ifd + 2 + count * 12u + 4;
        if (end > tiff.Length) throw new InvalidDataException("The TIFF directory is damaged.");
        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = (int)ifd + 2 + i * 12;
            entries.Add(new Entry(ReadU16(tiff, at, little), ReadU16(tiff, at + 2, little), ReadU32(tiff, at + 4, little), tiff[(at + 8)..(at + 12)]));
        }
        return (entries, ReadU32(tiff, (int)(ifd + 2 + count * 12u), little));
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        try { return new UTF8Encoding(false, true).GetString(bytes).Trim(); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes).Trim(); }
    }

    private static ReadOnlySpan<byte> Slice(byte[] tiff, uint offset, uint count) =>
        offset + (ulong)count <= (ulong)tiff.Length ? tiff.AsSpan((int)offset, (int)count) : [];

    private static uint Offset(byte[] raw, bool little) => ReadU32(raw, 0, little);

    private static void Align(MemoryStream s) { if (s.Position % 2 == 1) s.WriteByte(0); }

    private static ushort ReadU16(byte[] b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private static uint ReadU32(byte[] b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));

    private static void WriteU16(Span<byte> b, ushort v, bool little)
    {
        if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v);
    }

    private static void WriteU32(Span<byte> b, uint v, bool little)
    {
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v);
    }
}
