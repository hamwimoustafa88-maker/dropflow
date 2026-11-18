// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace DropFlow.Engines.Fonts.Sfnt;

/// <summary>An expected, user-facing failure (damaged font, collection, unsupported outlines). Becomes <c>ConversionResult.Fail</c>.</summary>
internal sealed class FontFailure(string message) : Exception(message);

/// <summary>
/// An OpenType font as its "sfnt" flavor plus its tables (OpenType specification, "Font file" chapter). Tables are kept as raw bytes; the
/// writer lays them out in tag order, pads each one to four bytes, fills in every table checksum and the head table's checkSumAdjustment.
/// </summary>
internal sealed class SfntFont
{
    public const uint TrueTypeFlavor = 0x00010000;
    public const uint CffFlavor = 0x4F54544F;     // 'OTTO'
    public const uint AppleTrueTypeFlavor = 0x74727565; // 'true'
    public const uint CollectionTag = 0x74746366; // 'ttcf'

    private const uint ChecksumMagic = 0xB1B0AFBA;

    public SfntFont(uint flavor, IDictionary<string, byte[]>? tables = null)
    {
        Flavor = flavor;
        Tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        if (tables is not null)
            foreach (var (tag, data) in tables) Tables[tag] = data;
    }

    /// <summary>0x00010000 (TrueType outlines), 'OTTO' (CFF outlines) or 'true' (old Apple TrueType).</summary>
    public uint Flavor { get; set; }

    /// <summary>Tables by their four-character tag (sorted ordinally, which is the order the specification requires).</summary>
    public SortedDictionary<string, byte[]> Tables { get; }

    public bool HasTrueTypeOutlines => Tables.ContainsKey("glyf") && Tables.ContainsKey("loca");

    public bool HasCffOutlines => Tables.ContainsKey("CFF ");

    public bool HasCff2Outlines => Tables.ContainsKey("CFF2");

    /// <summary>True for a variable font (an 'fvar' table): outline conversion would lose the variations.</summary>
    public bool IsVariable => Tables.ContainsKey("fvar");

    public byte[]? Table(string tag) => Tables.GetValueOrDefault(tag);

    public byte[] Require(string tag) =>
        Tables.GetValueOrDefault(tag) ?? throw new FontFailure($"The font has no '{tag.TrimEnd()}' table.");

    public int NumGlyphs
    {
        get
        {
            var maxp = Require("maxp");
            if (maxp.Length < 6) throw new FontFailure("The font's 'maxp' table is too short.");
            return BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
        }
    }

    // ---- reading --------------------------------------------------------------------------------------------------------

    /// <summary>Parses an sfnt file (TrueType or CFF-flavored OpenType). Throws <see cref="FontFailure"/> for anything else.</summary>
    public static SfntFont Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new FontFailure("The file is too short to be a font.");
        var flavor = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (flavor == CollectionTag) throw new FontFailure("Font collections (.ttc) are not supported: extract one font first.");
        if (flavor is not (TrueTypeFlavor or CffFlavor or AppleTrueTypeFlavor))
            throw new FontFailure("The file is not a TrueType or OpenType font.");

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        if (numTables == 0 || 12 + numTables * 16 > data.Length) throw new FontFailure("The font's table directory is damaged.");

        var font = new SfntFont(flavor);
        for (var i = 0; i < numTables; i++)
        {
            var record = data.Slice(12 + i * 16, 16);
            var tag = Tag(record);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (offset > (uint)data.Length || length > (uint)data.Length - offset)
                throw new FontFailure($"The font's '{tag.TrimEnd()}' table points outside the file.");
            if (!font.Tables.TryAdd(tag, data.Slice((int)offset, (int)length).ToArray()))
                throw new FontFailure($"The font lists the '{tag.TrimEnd()}' table twice.");
        }

        if (!font.Tables.ContainsKey("head") && !font.Tables.ContainsKey("bhed"))
            throw new FontFailure("The font has no 'head' table.");
        return font;
    }

    // ---- writing --------------------------------------------------------------------------------------------------------

    /// <summary>Size of the file <see cref="Write"/> produces: header, directory and every table padded to four bytes.</summary>
    public long SfntSize => 12 + 16L * Tables.Count + Tables.Values.Sum(t => (long)Pad4(t.Length));

    /// <summary>
    /// Writes the font: tables in tag order, each four-byte aligned and zero padded, correct checksums, and head.checkSumAdjustment computed
    /// over the whole file (with the adjustment itself taken as zero, as the specification requires). The head table in <see cref="Tables"/>
    /// is updated with the new adjustment.
    /// </summary>
    public byte[] Write()
    {
        var count = Tables.Count;
        var output = new byte[SfntSize];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, Flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        var (searchRange, entrySelector, rangeShift) = SearchParameters(count, 16);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], rangeShift);

        var offset = 12 + 16 * count;
        var headOffset = -1;
        var index = 0;
        foreach (var (tag, source) in Tables)
        {
            var data = source;
            if (tag == "head" && data.Length >= 12)
            {
                data = (byte[])source.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
                headOffset = offset;
            }

            var record = span.Slice(12 + index * 16, 16);
            Encoding.ASCII.GetBytes(tag, record);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            data.CopyTo(span[offset..]);
            offset += Pad4(data.Length);
            index++;
        }

        if (headOffset >= 0)
        {
            var adjustment = unchecked(ChecksumMagic - Checksum(output));
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], adjustment);
            var head = (byte[])Tables["head"].Clone();
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), adjustment);
            Tables["head"] = head;
        }

        return output;
    }

    /// <summary>
    /// The checksum a table directory records for <paramref name="tag"/>: the sum of the big-endian 32-bit words of the zero-padded table,
    /// with head.checkSumAdjustment counted as zero.
    /// </summary>
    public static uint TableChecksum(string tag, ReadOnlySpan<byte> data)
    {
        if (tag != "head" || data.Length < 12) return Checksum(data);
        var copy = data.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(8), 0);
        return Checksum(copy);
    }

    /// <summary>Sum of the big-endian 32-bit words of <paramref name="data"/>, the last one zero padded.</summary>
    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var whole = data.Length & ~3;
        for (var i = 0; i < whole; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        if (whole < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            last.Clear();
            data[whole..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }
        return sum;
    }

    /// <summary>searchRange, entrySelector and rangeShift for a binary-search header of <paramref name="count"/> entries of <paramref name="size"/> bytes.</summary>
    public static (ushort SearchRange, ushort EntrySelector, ushort RangeShift) SearchParameters(int count, int size)
    {
        var power = 1;
        var log = 0;
        while (power * 2 <= count) { power *= 2; log++; }
        return ((ushort)(power * size), (ushort)log, (ushort)(count * size - power * size));
    }

    public static int Pad4(int length) => (length + 3) & ~3;

    public static string Tag(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes[..4]);

    public static uint TagValue(string tag) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.Latin1.GetBytes(tag));

    public static string TagOf(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>A deep copy (table arrays are cloned).</summary>
    public SfntFont Clone() => new(Flavor, Tables.ToDictionary(t => t.Key, t => (byte[])t.Value.Clone()));
}
