// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace DropFlow.Engines.Fonts.Sfnt;

internal enum GlyphKind { Empty, Simple, Composite }

/// <summary>
/// One glyph of a 'glyf' table (OpenType specification, "glyf - Glyph Data"). Simple glyphs keep absolute point coordinates; composite
/// glyphs keep their component records as raw bytes, because nothing here changes them.
/// </summary>
internal sealed class Glyph
{
    public const byte OnCurve = 0x01;
    public const byte XShort = 0x02;
    public const byte YShort = 0x04;
    public const byte Repeat = 0x08;
    public const byte XSameOrPositive = 0x10;
    public const byte YSameOrPositive = 0x20;
    public const byte OverlapSimple = 0x40;

    public const ushort ArgsAreWords = 0x0001;
    public const ushort HaveScale = 0x0008;
    public const ushort MoreComponents = 0x0020;
    public const ushort HaveXYScale = 0x0040;
    public const ushort HaveTwoByTwo = 0x0080;
    public const ushort HaveInstructions = 0x0100;

    public static readonly Glyph EmptyGlyph = new() { Kind = GlyphKind.Empty };

    public GlyphKind Kind { get; init; }
    public short XMin { get; set; }
    public short YMin { get; set; }
    public short XMax { get; set; }
    public short YMax { get; set; }

    /// <summary>Index of the last point of each contour (simple glyphs).</summary>
    public ushort[] EndPoints { get; init; } = [];
    public bool[] OnCurvePoints { get; init; } = [];
    public int[] X { get; init; } = [];
    public int[] Y { get; init; } = [];

    /// <summary>The OVERLAP_SIMPLE flag of the first point (variable fonts use it to ask for non-zero filling).</summary>
    public bool Overlap { get; init; }

    /// <summary>Component records of a composite glyph, exactly as stored (without trailing instructions).</summary>
    public byte[] Components { get; init; } = [];

    public byte[] Instructions { get; init; } = [];

    /// <summary>True when a composite glyph carries instructions (WE_HAVE_INSTRUCTIONS on a component).</summary>
    public bool CompositeHasInstructions { get; init; }

    public int PointCount => X.Length;

    /// <summary>The bounding box of the points (zero for no points).</summary>
    public (short XMin, short YMin, short XMax, short YMax) PointBounds()
    {
        if (X.Length == 0) return (0, 0, 0, 0);
        return ((short)X.Min(), (short)Y.Min(), (short)X.Max(), (short)Y.Max());
    }

    // ---- reading --------------------------------------------------------------------------------------------------------

    /// <summary>Parses one glyph record. <paramref name="data"/> is exactly the bytes loca gives for it (possibly with padding).</summary>
    public static Glyph Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return EmptyGlyph;
        if (data.Length < 10) throw new FontFailure("A glyph record is too short.");
        var contours = BinaryPrimitives.ReadInt16BigEndian(data);
        var xMin = BinaryPrimitives.ReadInt16BigEndian(data[2..]);
        var yMin = BinaryPrimitives.ReadInt16BigEndian(data[4..]);
        var xMax = BinaryPrimitives.ReadInt16BigEndian(data[6..]);
        var yMax = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
        return contours >= 0 ? ParseSimple(data, contours, xMin, yMin, xMax, yMax) : ParseComposite(data, xMin, yMin, xMax, yMax);
    }

    private static Glyph ParseSimple(ReadOnlySpan<byte> data, int contours, short xMin, short yMin, short xMax, short yMax)
    {
        var reader = new SpanReader(data, 10);
        var ends = new ushort[contours];
        for (var i = 0; i < contours; i++) ends[i] = reader.U16();
        var points = contours == 0 ? 0 : ends[^1] + 1;
        for (var i = 1; i < contours; i++)
            if (ends[i] < ends[i - 1]) throw new FontFailure("A glyph's contour end points are out of order.");
        var instructions = reader.Bytes(reader.U16()).ToArray();

        var flags = new byte[points];
        for (var i = 0; i < points;)
        {
            var flag = reader.U8();
            flags[i++] = flag;
            if ((flag & Repeat) != 0)
            {
                var count = reader.U8();
                for (var r = 0; r < count; r++)
                {
                    if (i >= points) throw new FontFailure("A glyph's flags repeat past its last point.");
                    flags[i++] = flag;
                }
            }
        }

        var x = new int[points];
        var y = new int[points];
        var value = 0;
        for (var i = 0; i < points; i++)
        {
            var f = flags[i];
            if ((f & XShort) != 0) value += (f & XSameOrPositive) != 0 ? reader.U8() : -reader.U8();
            else if ((f & XSameOrPositive) == 0) value += reader.I16();
            x[i] = value;
        }
        value = 0;
        for (var i = 0; i < points; i++)
        {
            var f = flags[i];
            if ((f & YShort) != 0) value += (f & YSameOrPositive) != 0 ? reader.U8() : -reader.U8();
            else if ((f & YSameOrPositive) == 0) value += reader.I16();
            y[i] = value;
        }

        return new Glyph
        {
            Kind = contours == 0 ? GlyphKind.Empty : GlyphKind.Simple,
            XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
            EndPoints = ends,
            OnCurvePoints = flags.Select(f => (f & OnCurve) != 0).ToArray(),
            X = x,
            Y = y,
            Overlap = points > 0 && (flags[0] & OverlapSimple) != 0,
            Instructions = instructions,
        };
    }

    private static Glyph ParseComposite(ReadOnlySpan<byte> data, short xMin, short yMin, short xMax, short yMax)
    {
        var start = 10;
        var length = CompositeLength(data[start..], out var hasInstructions);
        var components = data.Slice(start, length).ToArray();
        byte[] instructions = [];
        if (hasInstructions)
        {
            var reader = new SpanReader(data, start + length);
            instructions = reader.Bytes(reader.U16()).ToArray();
        }

        return new Glyph
        {
            Kind = GlyphKind.Composite,
            XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
            Components = components,
            CompositeHasInstructions = hasInstructions,
            Instructions = instructions,
        };
    }

    /// <summary>Length in bytes of a run of component records; tells whether any of them asks for instructions.</summary>
    public static int CompositeLength(ReadOnlySpan<byte> data, out bool hasInstructions)
    {
        var position = 0;
        hasInstructions = false;
        while (true)
        {
            if (position + 4 > data.Length) throw new FontFailure("A composite glyph is cut short.");
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            position += 4; // flags + glyph index
            position += (flags & ArgsAreWords) != 0 ? 4 : 2;
            if ((flags & HaveScale) != 0) position += 2;
            else if ((flags & HaveXYScale) != 0) position += 4;
            else if ((flags & HaveTwoByTwo) != 0) position += 8;
            if ((flags & HaveInstructions) != 0) hasInstructions = true;
            if (position > data.Length) throw new FontFailure("A composite glyph is cut short.");
            if ((flags & MoreComponents) == 0) return position;
        }
    }

    /// <summary>The glyph indices a composite glyph refers to.</summary>
    public IEnumerable<int> ComponentGlyphs()
    {
        var position = 0;
        while (Kind == GlyphKind.Composite && position + 4 <= Components.Length)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(Components.AsSpan(position));
            yield return BinaryPrimitives.ReadUInt16BigEndian(Components.AsSpan(position + 2));
            position += 4 + ((flags & ArgsAreWords) != 0 ? 4 : 2);
            if ((flags & HaveScale) != 0) position += 2;
            else if ((flags & HaveXYScale) != 0) position += 4;
            else if ((flags & HaveTwoByTwo) != 0) position += 8;
            if ((flags & MoreComponents) == 0) yield break;
        }
    }

    // ---- writing --------------------------------------------------------------------------------------------------------

    /// <summary>The glyph record in the 'glyf' format (not padded). Empty glyphs have no bytes.</summary>
    public byte[] Encode()
    {
        if (Kind == GlyphKind.Empty) return [];
        var w = new BigEndianWriter();
        if (Kind == GlyphKind.Composite)
        {
            w.I16(-1);
            w.I16(XMin); w.I16(YMin); w.I16(XMax); w.I16(YMax);
            w.Bytes(Components);
            if (CompositeHasInstructions)
            {
                w.U16((ushort)Instructions.Length);
                w.Bytes(Instructions);
            }
            return w.ToArray();
        }

        w.I16((short)EndPoints.Length);
        w.I16(XMin); w.I16(YMin); w.I16(XMax); w.I16(YMax);
        foreach (var end in EndPoints) w.U16(end);
        w.U16((ushort)Instructions.Length);
        w.Bytes(Instructions);

        var points = X.Length;
        var flags = new byte[points];
        var xs = new BigEndianWriter();
        var ys = new BigEndianWriter();
        int px = 0, py = 0;
        for (var i = 0; i < points; i++)
        {
            var flag = OnCurvePoints[i] ? OnCurve : (byte)0;
            if (i == 0 && Overlap) flag |= OverlapSimple;
            var dx = X[i] - px;
            var dy = Y[i] - py;
            px = X[i];
            py = Y[i];

            if (dx == 0) flag |= XSameOrPositive;
            else if (Math.Abs(dx) < 256) { flag |= XShort; if (dx > 0) flag |= XSameOrPositive; xs.U8((byte)Math.Abs(dx)); }
            else xs.I16(checked((short)dx));

            if (dy == 0) flag |= YSameOrPositive;
            else if (Math.Abs(dy) < 256) { flag |= YShort; if (dy > 0) flag |= YSameOrPositive; ys.U8((byte)Math.Abs(dy)); }
            else ys.I16(checked((short)dy));
            flags[i] = flag;
        }

        for (var i = 0; i < points;)
        {
            var run = 1;
            while (i + run < points && flags[i + run] == flags[i] && run < 256) run++;
            if (run > 1)
            {
                w.U8((byte)(flags[i] | Repeat));
                w.U8((byte)(run - 1));
            }
            else
            {
                w.U8(flags[i]);
            }
            i += run;
        }
        w.Bytes(xs.ToArray());
        w.Bytes(ys.ToArray());
        return w.ToArray();
    }
}

/// <summary>Reads and writes a whole 'glyf' + 'loca' pair.</summary>
internal static class GlyfTable
{
    /// <summary>head.indexToLocFormat: 0 for 16-bit offsets (divided by two), 1 for 32-bit offsets.</summary>
    public static int LocaFormat(SfntFont font)
    {
        var head = font.Require("head");
        if (head.Length < 54) throw new FontFailure("The font's 'head' table is too short.");
        return BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50));
    }

    public static List<Glyph> Read(SfntFont font)
    {
        var glyf = font.Require("glyf");
        var loca = font.Require("loca");
        var count = font.NumGlyphs;
        var offsets = ReadLoca(loca, count, LocaFormat(font));
        var glyphs = new List<Glyph>(count);
        for (var i = 0; i < count; i++)
        {
            var start = offsets[i];
            var end = offsets[i + 1];
            if (end < start || end > glyf.Length) throw new FontFailure($"Glyph {i} points outside the 'glyf' table.");
            glyphs.Add(Glyph.Parse(glyf.AsSpan(start, end - start)));
        }
        return glyphs;
    }

    public static int[] ReadLoca(byte[] loca, int numGlyphs, int format)
    {
        var offsets = new int[numGlyphs + 1];
        var size = format == 0 ? 2 : 4;
        if (loca.Length < (numGlyphs + 1) * size) throw new FontFailure("The font's 'loca' table is too short.");
        for (var i = 0; i <= numGlyphs; i++)
            offsets[i] = format == 0
                ? BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(i * 2)) * 2
                : checked((int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(i * 4)));
        return offsets;
    }

    /// <summary>
    /// Builds 'glyf' and 'loca' from encoded glyph records. Every record is padded to four bytes; the short loca format is used only when
    /// <paramref name="format"/> asks for it, and then only if the table fits.
    /// </summary>
    public static (byte[] Glyf, byte[] Loca) Build(IReadOnlyList<byte[]> records, int format)
    {
        var glyf = new MemoryStream();
        var offsets = new int[records.Count + 1];
        for (var i = 0; i < records.Count; i++)
        {
            offsets[i] = (int)glyf.Length;
            glyf.Write(records[i]);
            var pad = SfntFont.Pad4(records[i].Length) - records[i].Length;
            for (var p = 0; p < pad; p++) glyf.WriteByte(0);
        }
        offsets[records.Count] = (int)glyf.Length;
        if (format == 0 && offsets[^1] / 2 > ushort.MaxValue)
            throw new FontFailure("The glyph data is too large for the font's short 'loca' format.");

        var loca = new byte[(records.Count + 1) * (format == 0 ? 2 : 4)];
        for (var i = 0; i < offsets.Length; i++)
        {
            if (format == 0) BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2), (ushort)(offsets[i] / 2));
            else BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)offsets[i]);
        }
        return (glyf.ToArray(), loca);
    }
}

/// <summary>A bounds-checked big-endian cursor over a span. Every read past the end is a <see cref="FontFailure"/>.</summary>
internal ref struct SpanReader(ReadOnlySpan<byte> data, int position = 0)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; } = position;

    public readonly int Remaining => _data.Length - Position;

    public byte U8()
    {
        Need(1);
        return _data[Position++];
    }

    public ushort U16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data[Position..]);
        Position += 2;
        return v;
    }

    public short I16() => unchecked((short)U16());

    public uint U32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0) throw new FontFailure("The font data is damaged (negative length).");
        Need(count);
        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    private readonly void Need(int count)
    {
        if (Position < 0 || Position + count > _data.Length) throw new FontFailure("The font data ends too early.");
    }
}

/// <summary>A growable big-endian byte buffer.</summary>
internal sealed class BigEndianWriter
{
    private readonly MemoryStream _stream = new();

    public int Length => (int)_stream.Length;

    public void U8(byte value) => _stream.WriteByte(value);

    public void U16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void I16(short value) => U16(unchecked((ushort)value));

    public void U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void Pad4()
    {
        while ((_stream.Length & 3) != 0) _stream.WriteByte(0);
    }

    public byte[] ToArray() => _stream.ToArray();
}
