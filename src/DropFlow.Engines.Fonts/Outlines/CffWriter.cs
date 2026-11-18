// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Outlines;

/// <summary>What a name-keyed CFF table needs besides the outlines.</summary>
internal sealed record CffInfo(string PostScriptName, string? FullName, string? FamilyName, int UnitsPerEm, (int XMin, int YMin, int XMax, int YMax) FontBBox);

/// <summary>
/// Writes a name-keyed CFF table (Adobe Technical Note #5176) with Type 2 charstrings (#5177): Header, Name INDEX, Top DICT INDEX,
/// String INDEX, an empty Global Subr INDEX, a format 0 charset, the CharStrings INDEX and a Private DICT. No subroutines and no hints.
/// Coordinates that are not whole numbers are written as 16.16 fixed-point values, so the curves are kept exactly.
/// </summary>
internal static class CffWriter
{
    private const int StandardStrings = 391; // SIDs below this are the standard strings; ".notdef" is SID 0

    public static byte[] Write(CffInfo info, IReadOnlyList<string> glyphNames, IReadOnlyList<(List<CubicContour> Contours, int Width)> glyphs)
    {
        var widths = glyphs.Select(g => g.Width).ToList();
        var defaultWidth = widths.GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var nominalWidth = defaultWidth;
        var charStrings = glyphs.Select(g => Charstring(g.Contours, g.Width == defaultWidth ? null : g.Width - nominalWidth)).ToList();

        var strings = new List<string>();
        int Sid(string s)
        {
            var index = strings.IndexOf(s);
            if (index < 0) { strings.Add(s); index = strings.Count - 1; }
            return StandardStrings + index;
        }

        var charset = new BigEndianWriter();
        charset.U8(0);
        for (var g = 1; g < glyphNames.Count; g++) charset.U16((ushort)Sid(glyphNames[g]));

        var fullSid = info.FullName is { Length: > 0 } full ? Sid(full) : -1;
        var familySid = info.FamilyName is { Length: > 0 } family ? Sid(family) : -1;

        var privateDict = new BigEndianWriter();
        DictInt(privateDict, defaultWidth); privateDict.U8(20);
        DictInt(privateDict, nominalWidth); privateDict.U8(21);
        var privateBytes = privateDict.ToArray();

        var name = Index([Encoding.ASCII.GetBytes(info.PostScriptName)]);
        var stringIndex = Index(strings.Select(Encoding.Latin1.GetBytes).ToList());
        var globalSubrs = Index([]);
        var charsetBytes = charset.ToArray();
        var charStringIndex = Index(charStrings);

        // The Top DICT holds offsets to data that follows it; written with fixed five-byte integers, its size does not depend on them.
        byte[] TopDict(int charsetOffset, int charStringsOffset, int privateOffset)
        {
            var w = new BigEndianWriter();
            if (fullSid >= 0) { DictInt(w, fullSid); w.U8(2); }
            if (familySid >= 0) { DictInt(w, familySid); w.U8(3); }
            if (info.UnitsPerEm != 1000)
            {
                var scale = 1.0 / info.UnitsPerEm;
                foreach (var v in new[] { scale, 0, 0, scale, 0, 0 }) DictReal(w, v);
                w.U8(12); w.U8(7);
            }
            foreach (var v in new[] { info.FontBBox.XMin, info.FontBBox.YMin, info.FontBBox.XMax, info.FontBBox.YMax }) DictInt(w, v);
            w.U8(5);
            DictFixed(w, charsetOffset); w.U8(15);
            DictFixed(w, charStringsOffset); w.U8(17);
            DictFixed(w, privateBytes.Length); DictFixed(w, privateOffset); w.U8(18);
            return w.ToArray();
        }

        var topSize = Index([TopDict(0, 0, 0)]).Length;
        var header = new byte[] { 1, 0, 4, 4 };
        var charsetOffset = header.Length + name.Length + topSize + stringIndex.Length + globalSubrs.Length;
        var charStringsOffset = charsetOffset + charsetBytes.Length;
        var privateOffset = charStringsOffset + charStringIndex.Length;
        var top = Index([TopDict(charsetOffset, charStringsOffset, privateOffset)]);

        var output = new BigEndianWriter();
        output.Bytes(header);
        output.Bytes(name);
        output.Bytes(top);
        output.Bytes(stringIndex);
        output.Bytes(globalSubrs);
        output.Bytes(charsetBytes);
        output.Bytes(charStringIndex);
        output.Bytes(privateBytes);
        return output.ToArray();
    }

    /// <summary>One Type 2 charstring: optional width, then rmoveto / rlineto / rrcurveto per contour, endchar.</summary>
    internal static byte[] Charstring(IReadOnlyList<CubicContour> contours, double? width)
    {
        var w = new BigEndianWriter();
        var pendingWidth = width;
        var current = new Pt(0, 0);
        foreach (var contour in contours)
        {
            if (pendingWidth is { } pw) { Number(w, pw); pendingWidth = null; }
            var start = Quantize(contour.Start);
            Number(w, start.X - current.X);
            Number(w, start.Y - current.Y);
            w.U8(21); // rmoveto
            current = start;

            var k = 0;
            var segments = contour.Segments;
            while (k < segments.Count)
            {
                var isLine = segments[k].IsLine;
                var arguments = 0;
                // Up to 24 lines or 8 curves per operator keep the stack within the 48 operands Type 2 allows.
                while (k < segments.Count && segments[k].IsLine == isLine && arguments + (isLine ? 2 : 6) <= 48)
                {
                    var s = segments[k++];
                    if (isLine)
                    {
                        var end = Quantize(s.End);
                        Number(w, end.X - current.X); Number(w, end.Y - current.Y);
                        current = end;
                    }
                    else
                    {
                        var c1 = Quantize(s.C1); var c2 = Quantize(s.C2); var end = Quantize(s.End);
                        Number(w, c1.X - current.X); Number(w, c1.Y - current.Y);
                        Number(w, c2.X - c1.X); Number(w, c2.Y - c1.Y);
                        Number(w, end.X - c2.X); Number(w, end.Y - c2.Y);
                        current = end;
                    }
                    arguments += isLine ? 2 : 6;
                }
                w.U8((byte)(isLine ? 5 : 8)); // rlineto / rrcurveto
            }
        }
        if (pendingWidth is { } width2) Number(w, width2);
        w.U8(14); // endchar
        return w.ToArray();
    }

    /// <summary>Positions are kept on the 1/65536 grid of 16.16 numbers, so every delta between them is exact.</summary>
    private static Pt Quantize(Pt p) => new(Math.Round(p.X * 65536) / 65536, Math.Round(p.Y * 65536) / 65536);

    /// <summary>A charstring operand: compact integer forms, or 255 + a 16.16 fixed number.</summary>
    internal static void Number(BigEndianWriter w, double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) <= 32767)
        {
            var v = (int)value;
            if (v is >= -107 and <= 107) w.U8((byte)(v + 139));
            else if (v is >= 108 and <= 1131) { w.U8((byte)(((v - 108) >> 8) + 247)); w.U8((byte)((v - 108) & 0xFF)); }
            else if (v is >= -1131 and <= -108) { w.U8((byte)(((-v - 108) >> 8) + 251)); w.U8((byte)((-v - 108) & 0xFF)); }
            else { w.U8(28); w.I16((short)v); }
            return;
        }
        if (Math.Abs(value) >= 32768) throw new FontFailure("A glyph coordinate is too large for CFF.");
        w.U8(255);
        w.U32(unchecked((uint)(int)Math.Round(value * 65536)));
    }

    private static void DictInt(BigEndianWriter w, int v)
    {
        if (v is >= -107 and <= 107) w.U8((byte)(v + 139));
        else if (v is >= 108 and <= 1131) { w.U8((byte)(((v - 108) >> 8) + 247)); w.U8((byte)((v - 108) & 0xFF)); }
        else if (v is >= -1131 and <= -108) { w.U8((byte)(((-v - 108) >> 8) + 251)); w.U8((byte)((-v - 108) & 0xFF)); }
        else if (v is >= short.MinValue and <= short.MaxValue) { w.U8(28); w.I16((short)v); }
        else DictFixed(w, v);
    }

    private static void DictFixed(BigEndianWriter w, int v)
    {
        w.U8(29);
        w.U32(unchecked((uint)v));
    }

    /// <summary>A real operand: nibbles of the decimal text, 0xF terminated.</summary>
    private static void DictReal(BigEndianWriter w, double value)
    {
        var text = value.ToString("0.##########E+0", CultureInfo.InvariantCulture);
        if (value == 0) text = "0";
        var nibbles = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '0' and <= '9') nibbles.Add(c - '0');
            else if (c == '.') nibbles.Add(0xA);
            else if (c == '-') nibbles.Add(0xE);
            else if (c == 'E')
            {
                if (i + 1 < text.Length && text[i + 1] == '-') { nibbles.Add(0xC); i++; }
                else { nibbles.Add(0xB); if (i + 1 < text.Length && text[i + 1] == '+') i++; }
            }
        }
        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
        w.U8(30);
        for (var i = 0; i < nibbles.Count; i += 2) w.U8((byte)((nibbles[i] << 4) | nibbles[i + 1]));
    }

    internal static byte[] Index(IReadOnlyList<byte[]> items)
    {
        var w = new BigEndianWriter();
        w.U16((ushort)items.Count);
        if (items.Count == 0) return w.ToArray();
        var last = 1 + items.Sum(i => (long)i.Length);
        var offSize = last <= 0xFF ? 1 : last <= 0xFFFF ? 2 : last <= 0xFFFFFF ? 3 : 4;
        w.U8((byte)offSize);
        long offset = 1;
        void Offset(long value)
        {
            for (var k = offSize - 1; k >= 0; k--) w.U8((byte)(value >> (8 * k)));
        }
        Offset(offset);
        foreach (var item in items)
        {
            offset += item.Length;
            Offset(offset);
        }
        foreach (var item in items) w.Bytes(item);
        return w.ToArray();
    }
}
