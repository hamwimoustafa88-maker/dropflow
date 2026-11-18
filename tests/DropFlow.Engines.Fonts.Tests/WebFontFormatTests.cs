using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DropFlow.Engines.Fonts.Sfnt;
using DropFlow.Engines.Fonts.Web;

namespace DropFlow.Engines.Fonts.Tests;

/// <summary>The sfnt, WOFF 1.0, WOFF 2.0 and EOT layers on their own, checked against the specifications field by field.</summary>
public sealed class WebFontFormatTests
{
    [Fact]
    public void An_sfnt_is_rewritten_with_correct_checksums_and_the_same_tables()
    {
        var original = TestFonts.Tiny();
        TestFonts.AssertChecksums(original); // the test's own writer is right
        var font = SfntFont.Read(original);
        var written = font.Clone().Write();
        TestFonts.AssertChecksums(written);
        TestFonts.AssertEquivalent(font, SfntFont.Read(written));
    }

    [Fact]
    public void Woff_round_trip_restores_every_table_byte_for_byte()
    {
        var font = SfntFont.Read(TestFonts.Tiny());
        var woff = Woff.Encode(font);
        var decoded = Woff.Decode(woff);
        foreach (var (tag, data) in font.Tables)
        {
            if (tag == "head") continue;
            Assert.Equal(data, decoded.Tables[tag]);
        }
        TestFonts.AssertEquivalent(font, decoded);
        TestFonts.AssertChecksums(decoded.Write());
    }

    [Fact]
    public void Woff_header_and_directory_follow_the_specification()
    {
        var font = SfntFont.Read(TestFonts.Tiny());
        var woff = Woff.Encode(font);
        var s = woff.AsSpan();
        Assert.Equal("wOFF", Encoding.ASCII.GetString(s[..4]));
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(s[4..]));
        Assert.Equal((uint)woff.Length, BinaryPrimitives.ReadUInt32BigEndian(s[8..]));
        int n = BinaryPrimitives.ReadUInt16BigEndian(s[12..]);
        Assert.Equal(font.Tables.Count, n);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(s[14..]));
        Assert.Equal((uint)(12 + 16 * n + font.Tables.Values.Sum(t => (t.Length + 3) & ~3)), BinaryPrimitives.ReadUInt32BigEndian(s[16..]));
        Assert.Equal(0, woff.Length % 4);

        var previous = "";
        var normalized = font.Clone();
        normalized.Write();
        for (var i = 0; i < n; i++)
        {
            var e = s.Slice(44 + i * 20, 20);
            var tag = Encoding.ASCII.GetString(e[..4]);
            Assert.True(string.CompareOrdinal(previous, tag) < 0, "directory sorted by tag");
            previous = tag;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            var compLength = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            var origLength = BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(e[16..]);
            Assert.Equal(0u, offset % 4);
            Assert.True(compLength <= origLength);
            var table = normalized.Tables[tag];
            Assert.Equal((uint)table.Length, origLength);
            var copy = (byte[])table.Clone();
            if (tag == "head") BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(8), 0);
            Assert.Equal(TestFonts.Sum(copy), checksum);

            // A compressed table is a zlib stream another inflater can read.
            if (compLength < origLength)
            {
                using var z = new ZLibStream(new MemoryStream(s.Slice((int)offset, (int)compLength).ToArray()), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                z.CopyTo(inflated);
                Assert.Equal(table, inflated.ToArray());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Woff2_round_trip_rebuilds_the_same_glyphs_and_tables(bool longLoca)
    {
        var font = SfntFont.Read(TestFonts.Tiny(longLoca));
        var woff2 = Woff2.Encode(font);
        var decoded = Woff2.Decode(woff2);
        TestFonts.AssertEquivalent(font, decoded, "DSIG");
        var rebuilt = decoded.Write();
        TestFonts.AssertChecksums(rebuilt);
        Assert.Equal(longLoca ? 1 : 0, GlyfTable.LocaFormat(SfntFont.Read(rebuilt)));
    }

    [Fact]
    public void Woff2_stores_glyf_and_loca_transformed_and_sets_head_bit_11()
    {
        var woff2 = Woff2.Encode(SfntFont.Read(TestFonts.Tiny()));
        var s = woff2.AsSpan();
        Assert.Equal("wOF2", Encoding.ASCII.GetString(s[..4]));
        Assert.Equal((uint)woff2.Length, BinaryPrimitives.ReadUInt32BigEndian(s[8..]));
        Assert.Equal(0, woff2.Length % 4);
        int n = BinaryPrimitives.ReadUInt16BigEndian(s[12..]);

        var reader = new SpanReader(s, 48);
        var seen = new Dictionary<string, (int Version, uint? TransformLength)>();
        for (var i = 0; i < n; i++)
        {
            var flags = reader.U8();
            var tag = (flags & 0x3F) == 63 ? SfntFont.TagOf(reader.U32()) : Woff2.KnownTags[flags & 0x3F];
            var version = flags >> 6;
            Woff2Numbers.ReadBase128(ref reader);
            uint? transformLength = (tag is "glyf" or "loca" && version == 0) || (tag == "hmtx" && version == 1) ? Woff2Numbers.ReadBase128(ref reader) : null;
            seen[tag] = (version, transformLength);
        }
        Assert.Equal(0, seen["glyf"].Version);
        Assert.NotNull(seen["glyf"].TransformLength);
        Assert.Equal((0, (uint?)0), seen["loca"]);
        Assert.True(Woff2.KnownTags.Contains("OS/2") && seen.ContainsKey("OS/2"));

        var decoded = Woff2.Decode(woff2);
        Assert.Equal(0x0800, BinaryPrimitives.ReadUInt16BigEndian(decoded.Tables["head"].AsSpan(16)) & 0x0800);
    }

    [Fact]
    public void Woff2_reads_glyf_stored_with_the_null_transform()
    {
        // Build a WOFF2 by hand with glyf/loca at transform version 3 (stored as they are), as the specification allows.
        var font = SfntFont.Read(TestFonts.Tiny());
        var directory = new BigEndianWriter();
        var stream = new MemoryStream();
        foreach (var (tag, data) in font.Tables)
        {
            var index = Array.IndexOf(Woff2.KnownTags, tag);
            directory.U8((byte)(index | (tag is "glyf" or "loca" ? 3 << 6 : 0)));
            Woff2Numbers.WriteBase128(directory, (uint)data.Length);
            stream.Write(data);
        }
        var raw = stream.ToArray();
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        Assert.True(BrotliEncoder.TryCompress(raw, compressed, out var size));
        var dir = directory.ToArray();
        var file = new byte[(48 + dir.Length + size + 3) & ~3];
        BinaryPrimitives.WriteUInt32BigEndian(file, Woff2.Signature);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), font.Flavor);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)file.Length);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(12), (ushort)font.Tables.Count);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), (uint)size);
        dir.CopyTo(file, 48);
        compressed.AsSpan(0, size).CopyTo(file.AsSpan(48 + dir.Length));

        var decoded = Woff2.Decode(file);
        foreach (var (tag, data) in font.Tables) Assert.Equal(data, decoded.Tables[tag]);
    }

    [Fact]
    public void Every_triplet_size_round_trips()
    {
        int[] values = [0, 1, -1, 2, 15, 16, 17, 63, 64, 65, -64, -65, 255, 256, 257, -256, 511, 512, 767, 768, 769, -768, -769, 1023, 1024, 1279, 1280, -1280, 2000, 4095, 4096, -4096, 30000, -30000, 65535, -65535];
        foreach (var dx in values)
            foreach (var dy in values)
                foreach (var on in new[] { true, false })
                {
                    var flags = new BigEndianWriter();
                    var data = new BigEndianWriter();
                    Woff2Glyf.EncodeTriplet(on, dx, dy, flags, data);
                    var flag = flags.ToArray().Single();
                    var bytes = data.ToArray();
                    var expectedSize = (flag & 0x7F) switch { < 84 => 1, < 120 => 2, < 124 => 3, _ => 4 };
                    Assert.Equal(expectedSize, bytes.Length);
                    var reader = new SpanReader(bytes);
                    var (rx, ry) = Woff2Glyf.DecodeTriplet(flag & 0x7F, ref reader);
                    Assert.True((dx, dy) == (rx, ry), $"({dx},{dy}) came back as ({rx},{ry}) with flag {flag}");
                    Assert.Equal(on, (flag & 0x80) == 0);
                }
    }

    [Fact]
    public void The_specification_examples_of_the_triplet_table_decode_as_listed()
    {
        // Rows of the specification's table: index 0 = dy -(0..255), index 11 = dx +(0..255), index 20 = 4+4 bits, x-1 y-1,
        // index 84 = 8+8 bits with deltas 1, index 123 = 12+12 bits both positive, index 127 = 16+16 bits both positive.
        Assert.Equal((0, -7), Decode(0, 7));
        Assert.Equal((0, 263), Decode(3, 7));
        Assert.Equal((7, 0), Decode(11, 7));
        Assert.Equal((-1, -1), Decode(20, 0x00));
        Assert.Equal((16, 16), Decode(23, 0xFF));
        Assert.Equal((-1, -1), Decode(84, 0, 0));
        Assert.Equal((257, 257), Decode(84 + 12 + 4 + 3, 0, 0));
        Assert.Equal((0x123, 0x456), Decode(123, 0x12, 0x34, 0x56));
        Assert.Equal((0x1234, 0x5678), Decode(127, 0x12, 0x34, 0x56, 0x78));

        static (int, int) Decode(int index, params byte[] bytes)
        {
            var reader = new SpanReader(bytes);
            return Woff2Glyf.DecodeTriplet(index, ref reader);
        }
    }

    [Fact]
    public void Variable_length_numbers_round_trip_and_reject_padding()
    {
        foreach (var value in new uint[] { 0, 1, 127, 128, 16383, 16384, 0x0FFFFFFF, 0xFFFFFFFF })
        {
            var w = new BigEndianWriter();
            Woff2Numbers.WriteBase128(w, value);
            var r = new SpanReader(w.ToArray());
            Assert.Equal(value, Woff2Numbers.ReadBase128(ref r));
        }
        foreach (var value in new[] { 0, 252, 253, 505, 506, 758, 759, 761, 762, 65535 })
        {
            var w = new BigEndianWriter();
            Woff2Numbers.Write255UInt16(w, value);
            var r = new SpanReader(w.ToArray());
            Assert.Equal(value, Woff2Numbers.Read255UInt16(ref r));
        }
        Assert.Equal(1, Read255([253, 0, 1]));
        Assert.Equal(253 + 7, Read255([255, 7]));
        Assert.Equal(506 + 7, Read255([254, 7]));

        Assert.Throws<FontFailure>(() => ReadBase128([0x80, 0x01]));
        Assert.Throws<FontFailure>(() => ReadBase128([0x90, 0x80, 0x80, 0x80, 0x00])); // more than 32 bits

        static int Read255(byte[] b) { var r = new SpanReader(b); return Woff2Numbers.Read255UInt16(ref r); }
        static uint ReadBase128(byte[] b) { var r = new SpanReader(b); return Woff2Numbers.ReadBase128(ref r); }
    }

    [Fact]
    public void Eot_header_repeats_the_font_classification_and_names()
    {
        var bytes = TestFonts.Tiny();
        var font = SfntFont.Read(bytes);
        var eot = Eot.Write(font, bytes);
        var header = Eot.Read(eot);
        Assert.Equal((uint)eot.Length, header.EotSize);
        Assert.Equal((uint)bytes.Length, header.FontDataSize);
        Assert.Equal(0x00020001u, header.Version);
        Assert.Equal(0u, header.Flags);
        Assert.Equal(new byte[] { 2, 11, 6, 4, 2, 2, 2, 2, 2, 4 }, header.Panose);
        Assert.Equal(1, header.Italic);
        Assert.Equal(700u, header.Weight);
        Assert.Equal(0x0008, header.FsType);
        Assert.Equal(0x504C, header.Magic);
        Assert.Equal(new uint[] { 1, 0x40000000, 0, 0x10 }, header.UnicodeRange);
        Assert.Equal(new uint[] { 1, 0x80000000 }, header.CodePageRange);
        Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(font.Tables["head"].AsSpan(8)), header.CheckSumAdjustment);
        Assert.Equal(TestFonts.Family, header.Family);
        Assert.Equal("Regular", header.Style);
        Assert.Equal("Version 1.000", header.VersionName);
        Assert.Equal(TestFonts.Family + " Regular", header.FullName);
        Assert.Equal(bytes, eot.AsSpan(header.FontDataOffset).ToArray());
    }

    [Fact]
    public void Collections_and_other_files_are_refused_with_a_clear_message()
    {
        var ttc = new byte[64];
        "ttcf"u8.CopyTo(ttc);
        Assert.Contains("collection", Assert.Throws<FontFailure>(() => SfntFont.Read(ttc)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<FontFailure>(() => SfntFont.Read("%PDF-1.7 not a font"u8.ToArray()));
        Assert.Throws<FontFailure>(() => Woff.Decode("wOFF"u8.ToArray()));
        var truncated = Woff2.Encode(SfntFont.Read(TestFonts.Tiny()));
        Assert.Throws<FontFailure>(() => Woff2.Decode(truncated.AsSpan(0, truncated.Length / 2)));
        var damaged = (byte[])truncated.Clone();
        for (var i = 80; i < damaged.Length; i++) damaged[i] ^= 0x5A;
        Assert.Throws<FontFailure>(() => Woff2.Decode(damaged));
    }
}
