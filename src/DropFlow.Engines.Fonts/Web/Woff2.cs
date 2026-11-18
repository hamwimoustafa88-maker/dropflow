// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Web;

/// <summary>
/// WOFF 2.0 (W3C Recommendation, "WOFF File Format 2.0"): a 48-byte header, a compact table directory, then ONE Brotli stream holding
/// every table back to back. 'glyf' and 'loca' may be stored transformed (version 0) or as they are (version 3, the null transform);
/// 'hmtx' may be transformed (version 1). This writer transforms glyf/loca (and hmtx when it saves space); the reader accepts every form.
/// </summary>
internal static class Woff2
{
    public const uint Signature = 0x774F4632; // 'wOF2'
    private const int HeaderSize = 48;

    /// <summary>The 63 tags a directory entry can name by index (section 4.1 "Table directory format", the "known table tags").</summary>
    internal static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    private sealed record Entry(string Tag, int Transform, uint OrigLength, uint TransformLength, bool Transformed);

    // ---- decoding ------------------------------------------------------------------------------------------------------

    public static SfntFont Decode(ReadOnlySpan<byte> data, CancellationToken ct = default)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(data) != Signature)
            throw new FontFailure("The file is not a WOFF2 font.");
        var header = new SpanReader(data, 4);
        var flavor = header.U32();
        if (flavor == SfntFont.CollectionTag) throw new FontFailure("WOFF2 font collections are not supported.");
        var length = header.U32();
        int numTables = header.U16();
        header.U16(); // reserved
        header.U32(); // totalSfntSize: only a hint for buffer sizes
        var totalCompressedSize = header.U32();
        if (length > data.Length) throw new FontFailure("The WOFF2 file is cut short.");
        if (numTables == 0) throw new FontFailure("The WOFF2 file has no tables.");

        var directory = new SpanReader(data, HeaderSize);
        var entries = new List<Entry>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var flags = directory.U8();
            var index = flags & 0x3F;
            var tag = index == 63 ? SfntFont.TagOf(directory.U32()) : KnownTags[index];
            var transform = (flags >> 6) & 3;
            var origLength = Woff2Numbers.ReadBase128(ref directory);
            // glyf/loca: version 0 is the transform, 3 the null transform. hmtx: version 1 is the transform. Everything else: 0 = as stored.
            var transformed = tag is "glyf" or "loca" ? transform == 0 : tag == "hmtx" ? transform == 1 : false;
            if (!transformed && transform != 0 && !(tag is "glyf" or "loca" && transform == 3))
                throw new FontFailure($"The WOFF2 table '{tag.TrimEnd()}' uses an unknown transform ({transform}).");
            var transformLength = transformed ? Woff2Numbers.ReadBase128(ref directory) : origLength;
            entries.Add(new Entry(tag, transform, origLength, transformLength, transformed));
        }

        var compressedStart = directory.Position;
        if ((long)compressedStart + totalCompressedSize > data.Length) throw new FontFailure("The WOFF2 compressed data is cut short.");
        var total = entries.Sum(e => (long)e.TransformLength);
        if (total > 512L * 1024 * 1024) throw new FontFailure("The WOFF2 file declares an unreasonably large font.");
        var stream = Inflate(data.Slice(compressedStart, (int)totalCompressedSize), (int)total);
        ct.ThrowIfCancellationRequested();

        var tables = new Dictionary<string, (Entry Entry, int Offset)>(StringComparer.Ordinal);
        var offset = 0;
        foreach (var entry in entries)
        {
            if (!tables.TryAdd(entry.Tag, (entry, offset))) throw new FontFailure($"The WOFF2 file lists the '{entry.Tag.TrimEnd()}' table twice.");
            offset += (int)entry.TransformLength;
        }

        var glyfTransformed = tables.TryGetValue("glyf", out var glyfInfo) && glyfInfo.Entry.Transformed;
        var locaTransformed = tables.TryGetValue("loca", out var locaInfo) && locaInfo.Entry.Transformed;
        if (glyfTransformed != locaTransformed) throw new FontFailure("The WOFF2 file transforms only one of 'glyf' and 'loca'.");

        var font = new SfntFont(flavor);
        foreach (var (tag, (entry, start)) in tables)
            if (!entry.Transformed) font.Tables[tag] = stream.AsSpan(start, (int)entry.TransformLength).ToArray();

        List<Glyph>? glyphs = null;
        if (glyfTransformed)
        {
            if (locaInfo.Entry.TransformLength != 0) throw new FontFailure("The WOFF2 transformed 'loca' table must be empty.");
            var (decoded, indexFormat) = Woff2Glyf.Reconstruct(stream.AsSpan(glyfInfo.Offset, (int)glyfInfo.Entry.TransformLength));
            glyphs = decoded;
            if (font.Table("maxp") is not null && font.NumGlyphs != glyphs.Count)
                throw new FontFailure("The WOFF2 glyph count does not match the font's 'maxp' table.");
            // The rebuilt loca always has (numGlyphs + 1) entries; a declared origLength that differs (a padded original) is not an error.
            var (glyf, loca) = GlyfTable.Build(glyphs.Select(g => g.Encode()).ToList(), indexFormat);
            font.Tables["glyf"] = glyf;
            font.Tables["loca"] = loca;
            if (font.Table("head") is { Length: >= 54 } head && BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) != indexFormat)
            {
                head = (byte[])head.Clone();
                BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), (short)indexFormat);
                font.Tables["head"] = head;
            }
        }

        if (tables.TryGetValue("hmtx", out var hmtxInfo) && hmtxInfo.Entry.Transformed)
        {
            if (glyphs is null)
            {
                if (!font.HasTrueTypeOutlines) throw new FontFailure("The WOFF2 'hmtx' transform needs TrueType outlines.");
                glyphs = GlyfTable.Read(font);
            }
            var hhea = font.Require("hhea");
            int numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
            font.Tables["hmtx"] = Woff2Glyf.ReconstructHmtx(stream.AsSpan(hmtxInfo.Offset, (int)hmtxInfo.Entry.TransformLength), font.NumGlyphs, numberOfHMetrics, glyphs);
        }

        if (!font.Tables.ContainsKey("head")) throw new FontFailure("The WOFF2 font has no 'head' table.");
        return font;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int size)
    {
        var output = new byte[size];
        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(compressed, output, out var consumed, out var written);
        if (status != System.Buffers.OperationStatus.Done || written != size)
        {
            // A stream that produces more than declared reports DestinationTooSmall; a short one reports NeedMoreData.
            throw new FontFailure("The WOFF2 compressed data is damaged or does not have the size its directory declares.");
        }
        _ = consumed;
        return output;
    }

    // ---- encoding ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes <paramref name="font"/>. TrueType outlines are stored with the glyf/loca transform (the decoded glyph data is then equivalent,
    /// not byte-identical, so head.flags bit 11 is set and a 'DSIG' signature, which would no longer match, is dropped).
    /// </summary>
    public static byte[] Encode(SfntFont font, CancellationToken ct = default)
    {
        font = font.Clone();
        var transformGlyf = font.HasTrueTypeOutlines;
        List<Glyph>? glyphs = null;
        var indexFormat = 0;
        if (transformGlyf)
        {
            glyphs = GlyfTable.Read(font);
            indexFormat = GlyfTable.LocaFormat(font);
            font.Tables.Remove("DSIG");
            var head = (byte[])font.Require("head").Clone();
            var flags = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(16));
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(16), (ushort)(flags | 0x0800));
            font.Tables["head"] = head;
        }
        font.Write(); // head.checkSumAdjustment of the tables as they are stored
        ct.ThrowIfCancellationRequested();

        var stream = new MemoryStream();
        var directory = new BigEndianWriter();
        // The directory order is the order of the data in the stream; glyf must come before loca (tag order already does that).
        foreach (var (tag, data) in font.Tables)
        {
            var index = Array.IndexOf(KnownTags, tag);
            var flagIndex = index >= 0 ? index : 63;
            byte[] stored = data;
            var version = 0;
            var transformed = false;

            if (tag == "glyf" && transformGlyf)
            {
                stored = Woff2Glyf.Transform(glyphs!, indexFormat);
                transformed = true;
            }
            else if (tag == "loca" && transformGlyf)
            {
                stored = [];
                transformed = true;
            }
            else if (tag is "glyf" or "loca")
            {
                version = 3; // null transform
            }
            else if (tag == "hmtx" && transformGlyf && font.Table("hhea") is { Length: >= 36 } hhea)
            {
                var hmtx = Woff2Glyf.TransformHmtx(data, glyphs!.Count, BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34)), glyphs);
                if (hmtx is not null && hmtx.Length < data.Length)
                {
                    stored = hmtx;
                    version = 1;
                    transformed = true;
                }
            }

            directory.U8((byte)(flagIndex | (version << 6)));
            if (flagIndex == 63) directory.U32(SfntFont.TagValue(tag));
            Woff2Numbers.WriteBase128(directory, (uint)data.Length);
            if (transformed) Woff2Numbers.WriteBase128(directory, (uint)stored.Length);
            stream.Write(stored);
        }
        ct.ThrowIfCancellationRequested();

        var compressed = Deflate(stream.ToArray());
        var directoryBytes = directory.ToArray();
        var total = SfntFont.Pad4(HeaderSize + directoryBytes.Length + compressed.Length);
        var output = new byte[total];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, Signature);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], font.Flavor);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)font.Tables.Count);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], (uint)font.SfntSize);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], (uint)compressed.Length);
        Woff.Version(font, out var major, out var minor);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], major);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], minor);
        // metaOffset, metaLength, metaOrigLength, privOffset, privLength: zero.
        directoryBytes.CopyTo(span[HeaderSize..]);
        compressed.CopyTo(span[(HeaderSize + directoryBytes.Length)..]);
        return output;
    }

    private static byte[] Deflate(byte[] data)
    {
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        // Quality 11 (the best) with the largest window the format allows (24 bits), as font tables are small enough for it.
        if (!BrotliEncoder.TryCompress(data, output, out var written, quality: 11, window: 24))
            throw new FontFailure("The font could not be compressed.");
        return output.AsSpan(0, written).ToArray();
    }
}
