// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Outlines;

/// <summary>
/// TrueType outlines ('glyf', quadratic) to CFF outlines ('CFF ', cubic) and back. Glyph order, metrics, character map and layout tables
/// are kept, so the converted font shapes text exactly like the original; only the hinting is dropped (TrueType instructions mean nothing
/// to a CFF rasterizer and the other way round).
/// </summary>
internal static class OutlineConverter
{
    /// <summary>Tables that only make sense next to TrueType outlines.</summary>
    private static readonly string[] TrueTypeOnly = ["glyf", "loca", "fpgm", "prep", "cvt ", "hdmx", "LTSH", "VDMX", "gvar", "cvar"];

    /// <summary>Tables that only make sense next to CFF outlines.</summary>
    private static readonly string[] CffOnly = ["CFF ", "CFF2", "VORG"];

    public static SfntFont TrueTypeToCff(SfntFont source, CancellationToken ct)
    {
        if (!source.HasTrueTypeOutlines) throw new FontFailure("The font has no TrueType outlines to convert.");
        if (source.IsVariable) throw new FontFailure("Variable fonts cannot be converted to OTF without losing their variations; convert to WOFF or WOFF2 instead.");

        var glyphs = GlyfTable.Read(source);
        var count = glyphs.Count;
        var advances = Advances(source, count);
        var outlines = new List<(List<CubicContour>, int)>(count);
        for (var g = 0; g < count; g++)
        {
            if ((g & 255) == 0) ct.ThrowIfCancellationRequested();
            outlines.Add((ContourMath.FromTrueType(ContourMath.TrueTypeContours(glyphs, g)), advances[g]));
        }

        var head = source.Require("head");
        var info = new CffInfo(
            PostScriptName(source),
            NameTable.Get(source, NameTable.FullName),
            NameTable.Get(source, NameTable.Family),
            BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18)),
            (BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(36)), BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(38)),
             BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(40)), BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(42))));

        var font = source.Clone();
        font.Flavor = SfntFont.CffFlavor;
        foreach (var tag in TrueTypeOnly) font.Tables.Remove(tag);
        font.Tables.Remove("DSIG");
        font.Tables["CFF "] = CffWriter.Write(info, GlyphNames(source, count), outlines);

        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)count);
        font.Tables["maxp"] = maxp;
        font.Tables["post"] = PostVersion3(source);
        return font;
    }

    public static SfntFont CffToTrueType(SfntFont source, CancellationToken ct)
    {
        if (source.HasCff2Outlines) throw new FontFailure("CFF2 (variable PostScript) outlines cannot be converted to TTF.");
        if (!source.HasCffOutlines) throw new FontFailure("The font has no CFF outlines to convert.");

        var cff = CffFont.Parse(source.Require("CFF "));
        var count = cff.CharStrings.Length;
        if (source.Table("maxp") is not null && source.NumGlyphs != count) throw new FontFailure("The font's glyph count does not match its CFF data.");
        var head = (byte[])source.Require("head").Clone();
        var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));
        var tolerance = Math.Max(0.5, unitsPerEm / 1000.0);

        var records = new List<byte[]>(count);
        int maxPoints = 0, maxContours = 0;
        short xMin = short.MaxValue, yMin = short.MaxValue, xMax = short.MinValue, yMax = short.MinValue;
        var lsbs = new short[count];
        for (var g = 0; g < count; g++)
        {
            if ((g & 255) == 0) ct.ThrowIfCancellationRequested();
            var (contours, _) = cff.Draw(g);
            var points = ContourMath.ToTrueType(contours, tolerance);
            if (points.Count == 0)
            {
                records.Add([]);
                continue;
            }

            var all = points.SelectMany(p => p).ToArray();
            var ends = new ushort[points.Count];
            var total = -1;
            for (var c = 0; c < points.Count; c++) { total += points[c].Length; ends[c] = (ushort)total; }
            var glyph = new Glyph
            {
                Kind = GlyphKind.Simple,
                EndPoints = ends,
                OnCurvePoints = all.Select(p => p.OnCurve).ToArray(),
                X = all.Select(p => p.X).ToArray(),
                Y = all.Select(p => p.Y).ToArray(),
            };
            (glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax) = glyph.PointBounds();
            lsbs[g] = glyph.XMin;
            xMin = Math.Min(xMin, glyph.XMin); yMin = Math.Min(yMin, glyph.YMin);
            xMax = Math.Max(xMax, glyph.XMax); yMax = Math.Max(yMax, glyph.YMax);
            maxPoints = Math.Max(maxPoints, all.Length);
            maxContours = Math.Max(maxContours, points.Count);
            records.Add(glyph.Encode());
        }

        var size = records.Sum(r => (long)SfntFont.Pad4(r.Length));
        var locaFormat = size / 2 <= ushort.MaxValue ? 0 : 1;
        var (glyf, loca) = GlyfTable.Build(records, locaFormat);

        if (xMin <= xMax)
        {
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), xMin);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), yMin);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), xMax);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), yMax);
        }
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), (short)locaFormat);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(52), 0);

        var font = source.Clone();
        font.Flavor = SfntFont.TrueTypeFlavor;
        foreach (var tag in CffOnly) font.Tables.Remove(tag);
        font.Tables.Remove("DSIG");
        font.Tables["glyf"] = glyf;
        font.Tables["loca"] = loca;
        font.Tables["head"] = head;
        font.Tables["maxp"] = MaxpVersion1(count, maxPoints, maxContours);
        font.Tables["post"] = PostVersion3(source);
        font.Tables["hmtx"] = SideBearings(source, count, lsbs);
        return font;
    }

    // ---- tables -------------------------------------------------------------------------------------------------------

    private static int[] Advances(SfntFont font, int count)
    {
        var hhea = font.Require("hhea");
        var hmtx = font.Require("hmtx");
        int metrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        if (metrics < 1 || hmtx.Length < metrics * 4) throw new FontFailure("The font's horizontal metrics are damaged.");
        var advances = new int[count];
        for (var g = 0; g < count; g++)
            advances[g] = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(Math.Min(g, metrics - 1) * 4));
        return advances;
    }

    /// <summary>hmtx with the same advances and each left side bearing set to the new glyph's xMin (what TrueType rasterizers expect).</summary>
    private static byte[] SideBearings(SfntFont font, int count, short[] lsbs)
    {
        var hhea = font.Require("hhea");
        var hmtx = (byte[])font.Require("hmtx").Clone();
        int metrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        if (hmtx.Length < metrics * 4 + (count - metrics) * 2) throw new FontFailure("The font's horizontal metrics are damaged.");
        for (var g = 0; g < count; g++)
        {
            var offset = g < metrics ? g * 4 + 2 : metrics * 4 + (g - metrics) * 2;
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(offset), lsbs[g]);
        }
        return hmtx;
    }

    private static byte[] MaxpVersion1(int numGlyphs, int maxPoints, int maxContours)
    {
        var w = new BigEndianWriter();
        w.U32(0x00010000);
        w.U16((ushort)numGlyphs);
        w.U16((ushort)maxPoints);
        w.U16((ushort)maxContours);
        w.U16(0); // maxCompositePoints
        w.U16(0); // maxCompositeContours
        w.U16(1); // maxZones: no twilight zone (there are no instructions)
        for (var i = 0; i < 6; i++) w.U16(0); // twilight points, storage, function defs, instruction defs, stack, instruction size
        w.U16(0); // maxComponentElements
        w.U16(0); // maxComponentDepth
        return w.ToArray();
    }

    /// <summary>post version 3.0: the same metrics, no glyph names (a CFF table carries its own; a TTF made here needs none).</summary>
    private static byte[] PostVersion3(SfntFont font)
    {
        var post = new byte[32];
        if (font.Table("post") is { Length: >= 32 } original) original.AsSpan(0, 32).CopyTo(post);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        return post;
    }

    private static string PostScriptName(SfntFont font)
    {
        var name = NameTable.Get(font, NameTable.PostScriptName) ?? NameTable.Get(font, NameTable.FullName) ?? "DropFlowFont";
        var clean = new string(name.Where(c => c is > ' ' and < (char)127 and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%')).ToArray());
        if (clean.Length == 0) clean = "DropFlowFont";
        return clean.Length > 63 ? clean[..63] : clean;
    }

    /// <summary>
    /// Glyph names for the CFF charset: ".notdef" for glyph 0, "uniXXXX" / "uXXXXX" (the Adobe Glyph List convention) for glyphs the
    /// character map reaches, and "glyphN" for the rest.
    /// </summary>
    internal static string[] GlyphNames(SfntFont font, int count)
    {
        var names = new string[count];
        var codes = CharacterMap.FirstCodePoints(font, count);
        var used = new HashSet<string>(StringComparer.Ordinal) { ".notdef" };
        names[0] = ".notdef";
        for (var g = 1; g < count; g++)
        {
            var name = codes[g] is { } cp
                ? cp <= 0xFFFF ? "uni" + cp.ToString("X4", CultureInfo.InvariantCulture) : "u" + cp.ToString("X5", CultureInfo.InvariantCulture)
                : "glyph" + g.ToString(CultureInfo.InvariantCulture);
            if (!used.Add(name)) name = "glyph" + g.ToString(CultureInfo.InvariantCulture);
            used.Add(name);
            names[g] = name;
        }
        return names;
    }
}

/// <summary>Reads the Unicode character map ('cmap' formats 4 and 12) backwards: the first code point of each glyph.</summary>
internal static class CharacterMap
{
    public static int?[] FirstCodePoints(SfntFont font, int count)
    {
        var result = new int?[count];
        var cmap = font.Table("cmap");
        if (cmap is null || cmap.Length < 4) return result;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2));
        int? best = null;
        var bestScore = -1;
        for (var i = 0; i < tables && 4 + i * 8 + 8 <= cmap.Length; i++)
        {
            var platform = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(4 + i * 8));
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(6 + i * 8));
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(8 + i * 8));
            if (offset < 0 || offset + 2 > cmap.Length) continue;
            var format = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(offset));
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) or (0, 4, 12) or (0, 6, 12) => 3,
                (3, 1, 4) or (0, 3, 4) or (0, 1, 4) or (0, 0, 4) => 2,
                (3, 1, 12) => 2,
                _ => -1,
            };
            if (score > bestScore) { bestScore = score; best = offset; }
        }
        if (best is not { } start) return result;

        try
        {
            var format = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(start));
            if (format == 4) Format4(cmap, start, result);
            else if (format == 12) Format12(cmap, start, result);
        }
        catch (ArgumentOutOfRangeException)
        {
            // a damaged map only costs the glyph names
        }
        return result;
    }

    private static void Set(int?[] result, int glyph, int code)
    {
        if (glyph > 0 && glyph < result.Length && (result[glyph] is null || code < result[glyph])) result[glyph] = code;
    }

    private static void Format4(byte[] cmap, int start, int?[] result)
    {
        var s = cmap.AsSpan(start);
        int segCount = BinaryPrimitives.ReadUInt16BigEndian(s[6..]) / 2;
        var ends = 14;
        var starts = ends + segCount * 2 + 2;
        var deltas = starts + segCount * 2;
        var rangeOffsets = deltas + segCount * 2;
        for (var i = 0; i < segCount; i++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(s[(ends + i * 2)..]);
            int first = BinaryPrimitives.ReadUInt16BigEndian(s[(starts + i * 2)..]);
            int delta = BinaryPrimitives.ReadInt16BigEndian(s[(deltas + i * 2)..]);
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(s[(rangeOffsets + i * 2)..]);
            if (first == 0xFFFF) continue;
            for (var code = first; code <= end; code++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    var at = rangeOffsets + i * 2 + rangeOffset + (code - first) * 2;
                    if (at + 2 > s.Length) break;
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(s[at..]);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                Set(result, glyph, code);
            }
        }
    }

    private static void Format12(byte[] cmap, int start, int?[] result)
    {
        var s = cmap.AsSpan(start);
        var groups = BinaryPrimitives.ReadUInt32BigEndian(s[12..]);
        for (var i = 0L; i < groups && 16 + i * 12 + 12 <= s.Length; i++)
        {
            var g = s[(int)(16 + i * 12)..];
            var first = BinaryPrimitives.ReadUInt32BigEndian(g);
            var last = BinaryPrimitives.ReadUInt32BigEndian(g[4..]);
            var glyph = BinaryPrimitives.ReadUInt32BigEndian(g[8..]);
            for (var code = first; code <= last && code <= 0x10FFFF; code++, glyph++)
            {
                if (glyph >= result.Length) break;
                Set(result, (int)glyph, (int)code);
            }
        }
    }
}
