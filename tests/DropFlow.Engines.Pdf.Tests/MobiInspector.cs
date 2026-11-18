using System.Buffers.Binary;
using System.Text;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// Opens a Mobipocket file with nothing from the product: the Palm database, record 0 (PalmDOC + MOBI header), the EXTH records, the
/// trailing multibyte entries and its own PalmDOC decompressor, written here from the format description (MobileRead wiki, "MOBI").
/// Every check that a written MOBI is well-formed reads it through this class, so a mistake shared by the product writer and the product
/// reader cannot hide.
/// </summary>
internal sealed class MobiInspector
{
    public const int PalmHeaderSize = 78;

    private MobiInspector(byte[] file) => File = file;

    public byte[] File { get; }
    public string Name { get; private set; } = "";
    public string Type { get; private set; } = "";
    public string Creator { get; private set; } = "";
    public List<int> Offsets { get; } = [];
    public List<byte[]> Records { get; } = [];

    // PalmDOC header
    public int Compression { get; private set; }
    public uint TextLength { get; private set; }
    public int TextRecordCount { get; private set; }
    public int RecordSize { get; private set; }
    public int Encryption { get; private set; }

    // MOBI header (offsets into record 0)
    public string Magic { get; private set; } = "";
    public uint HeaderLength { get; private set; }
    public uint MobiType { get; private set; }
    public uint TextEncoding { get; private set; }
    public uint FileVersion { get; private set; }
    public uint FirstNonBook { get; private set; }
    public string FullName { get; private set; } = "";
    public uint Locale { get; private set; }
    public uint MinVersion { get; private set; }
    public uint FirstImage { get; private set; }
    public uint ExthFlags { get; private set; }
    public int FirstContent { get; private set; }
    public int LastContent { get; private set; }
    public uint FcisIndex { get; private set; }
    public uint FlisIndex { get; private set; }
    public uint ExtraFlags { get; private set; }

    public Dictionary<int, List<byte[]>> Exth { get; } = [];

    /// <summary>The decompressed text of each text record (trailing entries removed).</summary>
    public List<byte[]> Chunks { get; } = [];

    /// <summary>The multibyte overlap of each text record (the bytes of a character that continues in the next record).</summary>
    public List<byte[]> Overlaps { get; } = [];

    public byte[] Text => Chunks.SelectMany(c => c).ToArray();

    public string ExthText(int type) => Encoding.UTF8.GetString(Exth[type].Single());

    public uint ExthNumber(int type) => BinaryPrimitives.ReadUInt32BigEndian(Exth[type].Single());

    public static MobiInspector Open(string path)
    {
        var inspector = new MobiInspector(System.IO.File.ReadAllBytes(path));
        inspector.Parse();
        return inspector;
    }

    private void Parse()
    {
        var f = File;
        Assert.True(f.Length > PalmHeaderSize, "shorter than a Palm database header");
        var nameEnd = Array.IndexOf(f, (byte)0, 0, 32);
        Assert.True(nameEnd > 0, "the database name must be NUL-terminated within 32 bytes");
        Name = Encoding.ASCII.GetString(f, 0, nameEnd);
        Assert.All(Name, c => Assert.InRange(c, ' ', '~'));
        Type = Encoding.ASCII.GetString(f, 60, 4);
        Creator = Encoding.ASCII.GetString(f, 64, 4);
        int count = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(76));
        Assert.True(count > 0);
        var listEnd = PalmHeaderSize + count * 8;
        for (var i = 0; i < count; i++) Offsets.Add((int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(PalmHeaderSize + i * 8)));
        Assert.True(Offsets[0] >= listEnd, "record 0 starts inside the record list");
        for (var i = 0; i < count; i++)
        {
            Assert.True(Offsets[i] < f.Length, $"record {i} starts outside the file");
            if (i > 0) Assert.True(Offsets[i] > Offsets[i - 1], $"record {i} does not start after record {i - 1}");
            var end = i + 1 < count ? Offsets[i + 1] : f.Length;
            Records.Add(f.AsSpan(Offsets[i], end - Offsets[i]).ToArray());
        }

        var r0 = Records[0];
        Compression = BinaryPrimitives.ReadUInt16BigEndian(r0);
        TextLength = BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(4));
        TextRecordCount = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(8));
        RecordSize = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(10));
        Encryption = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(12));
        Magic = Encoding.ASCII.GetString(r0, 16, 4);
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(at));
        HeaderLength = U32(20);
        MobiType = U32(24);
        TextEncoding = U32(28);
        FileVersion = U32(36);
        FirstNonBook = U32(80);
        var nameOffset = (int)U32(84);
        var nameLength = (int)U32(88);
        Assert.True(nameOffset + nameLength <= r0.Length, "the full name lies outside record 0");
        FullName = Encoding.UTF8.GetString(r0, nameOffset, nameLength);
        Locale = U32(92);
        MinVersion = U32(104);
        FirstImage = U32(108);
        ExthFlags = U32(128);
        FirstContent = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(192));
        LastContent = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(194));
        FcisIndex = U32(200);
        FlisIndex = U32(208);
        ExtraFlags = U32(240);

        var exth = 16 + (int)HeaderLength;
        if ((ExthFlags & 0x40) != 0)
        {
            Assert.Equal("EXTH", Encoding.ASCII.GetString(r0, exth, 4));
            var exthLength = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(exth + 4));
            var items = BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(exth + 8));
            var at = exth + 12;
            for (var i = 0; i < items; i++)
            {
                var type = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(at));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(at + 4));
                Assert.True(length >= 8 && at + length <= exth + exthLength, $"EXTH record {type} runs past the EXTH block");
                if (!Exth.TryGetValue(type, out var list)) Exth[type] = list = [];
                list.Add(r0.AsSpan(at + 8, length - 8).ToArray());
                at += length;
            }
            Assert.Equal(exth + exthLength, at);
            Assert.True(nameOffset >= exth + exthLength, "the full name overlaps the EXTH block");
        }

        for (var i = 1; i <= TextRecordCount && i < Records.Count; i++)
        {
            var record = Records[i];
            var overlap = 0;
            if ((ExtraFlags & 1) != 0) overlap = record[^1] & 3;
            var trailing = (ExtraFlags & 1) != 0 ? overlap + 1 : 0;
            Overlaps.Add(record.AsSpan(record.Length - trailing, overlap).ToArray());
            var content = record.AsSpan(0, record.Length - trailing);
            Chunks.Add(Compression == 2 ? Inflate(content) : content.ToArray());
        }
    }

    /// <summary>PalmDOC LZ77, decoded byte by byte (the product's decoder is not used).</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i++];
            if (c == 0 || (c >= 0x09 && c <= 0x7F))
            {
                output.Add(c);
            }
            else if (c <= 0x08)
            {
                Assert.True(i + c <= data.Length, "a literal run is cut short");
                for (var k = 0; k < c; k++) output.Add(data[i + k]);
                i += c;
            }
            else if (c >= 0xC0)
            {
                output.Add((byte)' ');
                output.Add((byte)(c ^ 0x80));
            }
            else
            {
                Assert.True(i < data.Length, "a back reference is cut short");
                var pair = (c << 8) | data[i++];
                var distance = (pair >> 3) & 0x7FF;
                var length = (pair & 7) + 3;
                Assert.InRange(distance, 1, output.Count);
                // A copy that overlaps the bytes it produces is legal LZ77, but old Mobipocket readers mishandle it; the writer avoids it.
                Assert.True(distance >= length, $"a back reference of {length} bytes overlaps itself (distance {distance})");
                var from = output.Count - distance;
                for (var k = 0; k < length; k++) output.Add(output[from + k]);
            }
        }
        return output.ToArray();
    }
}
