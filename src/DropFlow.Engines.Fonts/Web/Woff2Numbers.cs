// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Web;

/// <summary>The variable-length numbers of WOFF 2.0 (W3C Recommendation, section 4 "Data types": UIntBase128 and 255UInt16).</summary>
internal static class Woff2Numbers
{
    /// <summary>UIntBase128: big-endian groups of seven bits, the high bit set on every byte but the last; at most five bytes, no leading zero group.</summary>
    public static uint ReadBase128(ref SpanReader reader)
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = reader.U8();
            if (i == 0 && b == 0x80) throw new FontFailure("The WOFF2 file is damaged (a number starts with a zero group).");
            if ((value & 0xFE000000) != 0) throw new FontFailure("The WOFF2 file is damaged (a number is too large).");
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }
        throw new FontFailure("The WOFF2 file is damaged (a number is longer than five bytes).");
    }

    public static void WriteBase128(BigEndianWriter writer, uint value)
    {
        Span<byte> groups = stackalloc byte[5];
        var count = 0;
        do
        {
            groups[count++] = (byte)(value & 0x7F);
            value >>= 7;
        } while (value != 0);
        for (var i = count - 1; i >= 0; i--) writer.U8((byte)(groups[i] | (i > 0 ? 0x80 : 0)));
    }

    private const byte WordCode = 253;
    private const byte OneMoreByteCode2 = 254;
    private const byte OneMoreByteCode1 = 255;
    private const int LowestUCode = 253;

    /// <summary>255UInt16: one byte for 0..252, a marker plus one byte for 253..761, a marker plus two bytes otherwise.</summary>
    public static ushort Read255UInt16(ref SpanReader reader)
    {
        var code = reader.U8();
        return code switch
        {
            WordCode => reader.U16(),
            OneMoreByteCode1 => (ushort)(reader.U8() + LowestUCode),
            OneMoreByteCode2 => (ushort)(reader.U8() + LowestUCode * 2),
            _ => code,
        };
    }

    public static void Write255UInt16(BigEndianWriter writer, int value)
    {
        if (value is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        if (value < LowestUCode)
        {
            writer.U8((byte)value);
        }
        else if (value < LowestUCode * 2)
        {
            writer.U8(OneMoreByteCode1);
            writer.U8((byte)(value - LowestUCode));
        }
        else if (value < LowestUCode * 3)
        {
            writer.U8(OneMoreByteCode2);
            writer.U8((byte)(value - LowestUCode * 2));
        }
        else
        {
            writer.U8(WordCode);
            writer.U16((ushort)value);
        }
    }
}
