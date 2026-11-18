// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Web;

/// <summary>
/// Embedded OpenType (W3C Member Submission "Embedded OpenType (EOT) File Format", version 0x00020001): a little-endian header that repeats
/// the font's OS/2 classification and its names, followed by the font file itself. Written uncompressed (no MicroType Express, no XOR
/// obfuscation, no subsetting), which every EOT reader accepts.
/// </summary>
internal static class Eot
{
    public const uint Version = 0x00020001;
    public const ushort Magic = 0x504C;
    private const byte DefaultCharset = 1;

    /// <param name="font">The parsed font (for its OS/2, head and name tables).</param>
    /// <param name="fontData">The font file stored in the EOT, exactly as given.</param>
    public static byte[] Write(SfntFont font, byte[] fontData)
    {
        var os2 = font.Table("OS/2");
        var head = font.Require("head");
        var w = new LittleEndianWriter();
        w.U32(0); // EOTSize, filled in below
        w.U32((uint)fontData.Length);
        w.U32(Version);
        w.U32(0); // Flags: not subset, not compressed, not obfuscated

        Span<byte> panose = stackalloc byte[10];
        panose.Clear();
        if (os2 is { Length: >= 42 }) os2.AsSpan(32, 10).CopyTo(panose);
        w.Bytes(panose);
        w.U8(DefaultCharset);
        w.U8((byte)(os2 is { Length: >= 64 } && (BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62)) & 1) != 0 ? 1 : 0));
        w.U32(os2 is { Length: >= 6 } ? BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4)) : 400u);
        w.U16(os2 is { Length: >= 10 } ? BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(8)) : (ushort)0);
        w.U16(Magic);
        for (var i = 0; i < 4; i++) w.U32(os2 is { Length: >= 58 } ? BinaryPrimitives.ReadUInt32BigEndian(os2.AsSpan(42 + i * 4)) : 0);
        var version = os2 is { Length: >= 2 } ? BinaryPrimitives.ReadUInt16BigEndian(os2) : 0;
        for (var i = 0; i < 2; i++) w.U32(version >= 1 && os2!.Length >= 86 ? BinaryPrimitives.ReadUInt32BigEndian(os2.AsSpan(78 + i * 4)) : 0);
        w.U32(BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)));
        for (var i = 0; i < 4; i++) w.U32(0); // Reserved1-4

        w.U16(0); // Padding1
        Name(w, NameTable.Get(font, NameTable.Family));
        w.U16(0); // Padding2
        Name(w, NameTable.Get(font, NameTable.Subfamily));
        w.U16(0); // Padding3
        Name(w, NameTable.Get(font, NameTable.Version));
        w.U16(0); // Padding4
        Name(w, NameTable.Get(font, NameTable.FullName));
        w.U16(0); // Padding5
        w.U16(0); // RootStringSize: no root string, the font is not restricted to sites
        w.Bytes(fontData);

        var output = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)output.Length);
        return output;
    }

    private static void Name(LittleEndianWriter w, string? value)
    {
        var bytes = Encoding.Unicode.GetBytes(value ?? "");
        w.U16((ushort)bytes.Length);
        w.Bytes(bytes);
    }

    /// <summary>The header fields a reader checks, for tests and diagnostics.</summary>
    internal sealed record Header(uint EotSize, uint FontDataSize, uint Version, uint Flags, byte[] Panose, byte Italic, uint Weight, ushort FsType,
        ushort Magic, uint[] UnicodeRange, uint[] CodePageRange, uint CheckSumAdjustment, string Family, string Style, string VersionName, string FullName, int FontDataOffset);

    internal static Header Read(ReadOnlySpan<byte> data)
    {
        var r = new LittleEndianReader(data);
        var eotSize = r.U32();
        var fontDataSize = r.U32();
        var version = r.U32();
        var flags = r.U32();
        var panose = r.Bytes(10).ToArray();
        r.U8(); // charset
        var italic = r.U8();
        var weight = r.U32();
        var fsType = r.U16();
        var magic = r.U16();
        var unicode = new[] { r.U32(), r.U32(), r.U32(), r.U32() };
        var codePages = new[] { r.U32(), r.U32() };
        var adjustment = r.U32();
        for (var i = 0; i < 4; i++) r.U32();
        r.U16();
        var family = r.Utf16(r.U16());
        r.U16();
        var style = r.Utf16(r.U16());
        r.U16();
        var versionName = r.Utf16(r.U16());
        r.U16();
        var full = r.Utf16(r.U16());
        if (version >= 0x00020001)
        {
            r.U16();
            r.Bytes(r.U16()); // root string
        }
        if (version >= 0x00020002)
        {
            r.U32(); // RootStringCheckSum
            r.U32(); // EUDCCodePage
            r.U16();
            r.Bytes(r.U16()); // signature
            r.U32(); // EUDCFlags
            r.Bytes((int)r.U32()); // EUDC font data
        }
        return new Header(eotSize, fontDataSize, version, flags, panose, italic, weight, fsType, magic, unicode, codePages, adjustment, family, style, versionName, full, r.Position);
    }

    private sealed class LittleEndianWriter
    {
        private readonly MemoryStream _stream = new();
        public void U8(byte v) => _stream.WriteByte(v);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _stream.Write(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _stream.Write(b); }
        public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);
        public byte[] ToArray() => _stream.ToArray();
    }

    private ref struct LittleEndianReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public int Position { get; private set; }

        public byte U8() { Need(1); return _data[Position++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]); Position += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]); Position += 4; return v; }
        public ReadOnlySpan<byte> Bytes(int n) { Need(n); var s = _data.Slice(Position, n); Position += n; return s; }
        public string Utf16(int n) => Encoding.Unicode.GetString(Bytes(n));

        private readonly void Need(int n)
        {
            if (n < 0 || Position + n > _data.Length) throw new FontFailure("The EOT file is cut short.");
        }
    }
}
