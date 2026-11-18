using System.Buffers.Binary;
using System.Text;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Tests;

/// <summary>
/// Builds a tiny but complete TrueType font in code (no binary fixture): .notdef, "A" (a triangle with an off-curve point), "B" (two
/// contours, coordinates far apart so every WOFF2 triplet size is used), "C" (a composite of A and B) and space (empty).
/// Written field by field from the OpenType specification, independently of the engine's own writer.
/// </summary>
internal static class TestFonts
{
    public const string Family = "DropFlow Test";

    /// <summary>Real fonts of this PC used when present (tests that need them are skipped elsewhere).</summary>
    public static string? SystemFont(params string[] names)
    {
        var folder = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts") : "/usr/share/fonts";
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>A third-party set of the same font as .ttf, .woff, .woff2 and .eot (Bootstrap's and Font Awesome's web fonts, if installed by some program).</summary>
    public static string? WebFontSet(string stem)
    {
        string[] folders =
        [
            @"C:\Program Files\Adobe\Adobe Dreamweaver 2021\Configuration\Shared\LiveEdit\ResponsiveAssets\bootstrap3\bundle\fonts",
            @"C:\Program Files\Maxon Cinema 4D 2024\resource\modules\licensing.module\html\assets\css\libs\fontawesome\webfonts",
            @"C:\Program Files\Maxon Cinema 4D 2023\resource\modules\licensing.module\html\assets\css\libs\fontawesome\webfonts",
        ];
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, stem);
            if (File.Exists(path + ".ttf") && File.Exists(path + ".woff2")) return path;
        }
        return null;
    }

    public static byte[] Tiny(bool longLoca = false)
    {
        var glyphs = new List<byte[]>
        {
            Simple([[(50, 0), (450, 0), (450, 700), (50, 700)]], [[true, true, true, true]]),                       // .notdef
            Simple([[(0, 0), (300, 600), (600, 0)]], [[true, false, true]], instructions: [0xB0, 0x01]),              // A
            Simple([[(10, 10), (20, 30), (85, 75), (900, 40), (1500, 1300), (6000, 5000)], [(100, 100), (120, 140), (100, 180)]],
                   [[true, true, false, true, false, true], [true, true, true]]),                                        // B
            Composite(),                                                                                             // C
            Array.Empty<byte>(),                                                                                     // space
        };
        int[] advances = [500, 600, 6100, 700, 250];
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var (glyf, loca) = GlyfAndLoca(glyphs, longLoca);
        tables["glyf"] = glyf;
        tables["loca"] = loca;
        tables["head"] = Head(longLoca);
        tables["hhea"] = Hhea(advances.Length);
        tables["maxp"] = Maxp(glyphs.Count);
        tables["hmtx"] = Hmtx(advances, [50, 0, 10, 0, 0]);
        tables["cmap"] = Cmap();
        tables["name"] = Name();
        tables["OS/2"] = Os2();
        tables["post"] = Post();
        return Assemble(0x00010000, tables);
    }

    // ---- glyphs -------------------------------------------------------------------------------------------------------

    private static byte[] Simple((int X, int Y)[][] contours, bool[][] onCurve, byte[]? instructions = null)
    {
        var points = contours.SelectMany(c => c).ToArray();
        var flags = onCurve.SelectMany(c => c).ToArray();
        var b = new Be();
        b.I16((short)contours.Length);
        b.I16((short)points.Min(p => p.X)); b.I16((short)points.Min(p => p.Y));
        b.I16((short)points.Max(p => p.X)); b.I16((short)points.Max(p => p.Y));
        var end = -1;
        foreach (var c in contours) { end += c.Length; b.U16((ushort)end); }
        b.U16((ushort)(instructions?.Length ?? 0));
        b.Bytes(instructions ?? []);
        // Plain encoding: every coordinate a 16-bit delta, no repeats (a different encoding from the engine's compact one).
        foreach (var f in flags) b.U8((byte)(f ? 1 : 0));
        int px = 0, py = 0;
        foreach (var p in points) { b.I16((short)(p.X - px)); px = p.X; }
        foreach (var p in points) { b.I16((short)(p.Y - py)); py = p.Y; }
        return b.ToArray();
    }

    private static byte[] Composite()
    {
        var b = new Be();
        b.I16(-1);
        b.I16(0); b.I16(0); b.I16(6100); b.I16(5000);
        b.U16(0x0001 | 0x0002 | 0x0020); b.U16(1); b.I16(0); b.I16(0);        // A at (0,0), words, more components
        b.U16(0x0001 | 0x0002 | 0x0008); b.U16(2); b.I16(100); b.I16(0); b.I16(0x4000); // B at (100,0), scale 1.0
        return b.ToArray();
    }

    private static (byte[] Glyf, byte[] Loca) GlyfAndLoca(List<byte[]> glyphs, bool longLoca)
    {
        var glyf = new MemoryStream();
        var loca = new Be();
        foreach (var g in glyphs)
        {
            if (longLoca) loca.U32((uint)glyf.Length); else loca.U16((ushort)(glyf.Length / 2));
            glyf.Write(g);
            if (glyf.Length % 2 == 1) glyf.WriteByte(0);
        }
        if (longLoca) loca.U32((uint)glyf.Length); else loca.U16((ushort)(glyf.Length / 2));
        return (glyf.ToArray(), loca.ToArray());
    }

    // ---- tables -------------------------------------------------------------------------------------------------------

    private static byte[] Head(bool longLoca)
    {
        var b = new Be();
        b.U32(0x00010000); b.U32(0x00010000); b.U32(0); b.U32(0x5F0F3CF5);
        b.U16(0x000B); b.U16(1000);
        b.U32(0); b.U32(0xE0000000); b.U32(0); b.U32(0xE0000000); // created, modified
        b.I16(0); b.I16(0); b.I16(6100); b.I16(5000);
        b.U16(0); b.U16(8); b.I16(2); b.I16((short)(longLoca ? 1 : 0)); b.I16(0);
        return b.ToArray();
    }

    private static byte[] Hhea(int metrics)
    {
        var b = new Be();
        b.U32(0x00010000); b.I16(800); b.I16(-200); b.I16(0); b.U16(6100);
        b.I16(0); b.I16(0); b.I16(6000); b.I16(1); b.I16(0); b.I16(0);
        b.I16(0); b.I16(0); b.I16(0); b.I16(0); b.I16(0); b.U16((ushort)metrics);
        return b.ToArray();
    }

    private static byte[] Maxp(int numGlyphs)
    {
        var b = new Be();
        b.U32(0x00010000); b.U16((ushort)numGlyphs);
        b.U16(9); b.U16(2); b.U16(12); b.U16(3); b.U16(2); b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(2); b.U16(2); b.U16(1);
        return b.ToArray();
    }

    private static byte[] Hmtx(int[] advances, int[] lsbs)
    {
        var b = new Be();
        for (var i = 0; i < advances.Length; i++) { b.U16((ushort)advances[i]); b.I16((short)lsbs[i]); }
        return b.ToArray();
    }

    private static byte[] Cmap()
    {
        // Format 4: segments 0x20 -> glyph 4, 0x41..0x43 -> glyphs 1..3, and the required 0xFFFF end segment.
        ushort[] ends = [0x20, 0x43, 0xFFFF];
        ushort[] starts = [0x20, 0x41, 0xFFFF];
        short[] deltas = [4 - 0x20, 1 - 0x41, 1];
        var seg = ends.Length;
        var sub = new Be();
        sub.U16(4); sub.U16((ushort)(16 + seg * 8)); sub.U16(0);
        sub.U16((ushort)(seg * 2)); sub.U16(4); sub.U16(1); sub.U16((ushort)(seg * 2 - 4));
        foreach (var e in ends) sub.U16(e);
        sub.U16(0);
        foreach (var s in starts) sub.U16(s);
        foreach (var d in deltas) sub.I16(d);
        foreach (var _ in ends) sub.U16(0);
        var b = new Be();
        b.U16(0); b.U16(1); b.U16(3); b.U16(1); b.U32(12);
        b.Bytes(sub.ToArray());
        return b.ToArray();
    }

    private static byte[] Name()
    {
        (int Id, string Value)[] names = [(1, Family), (2, "Regular"), (3, "DropFlow Test 1.0"), (4, Family + " Regular"), (5, "Version 1.000"), (6, "DropFlowTest-Regular")];
        var storage = new MemoryStream();
        var b = new Be();
        b.U16(0); b.U16((ushort)names.Length); b.U16((ushort)(6 + names.Length * 12));
        foreach (var (id, value) in names)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(value);
            b.U16(3); b.U16(1); b.U16(0x0409); b.U16((ushort)id); b.U16((ushort)bytes.Length); b.U16((ushort)storage.Length);
            storage.Write(bytes);
        }
        b.Bytes(storage.ToArray());
        return b.ToArray();
    }

    private static byte[] Os2()
    {
        var b = new Be();
        b.U16(4); b.I16(1500); b.U16(700); b.U16(5); b.U16(0x0008);         // version, avgWidth, weight 700, width, fsType: editable embedding
        for (var i = 0; i < 10; i++) b.I16(100);                             // sub/superscript, strikeout
        b.I16(0);                                                            // family class
        b.Bytes([2, 11, 6, 4, 2, 2, 2, 2, 2, 4]);                            // PANOSE
        b.U32(0x00000001); b.U32(0x40000000); b.U32(0); b.U32(0x00000010);  // unicode ranges
        b.Bytes("DFLW"u8);
        b.U16(0x0001 | 0x0020);                                              // fsSelection: italic + bold
        b.U16(0x20); b.U16(0x43);
        b.I16(800); b.I16(-200); b.I16(0); b.U16(1000); b.U16(200);
        b.U32(0x00000001); b.U32(0x80000000);                                // code pages
        b.I16(500); b.I16(700); b.U16(0); b.U16(0x20); b.U16(1);
        return b.ToArray();
    }

    private static byte[] Post()
    {
        var b = new Be();
        b.U32(0x00030000); b.U32(0); b.I16(-100); b.I16(50); b.U32(0); b.U32(0); b.U32(0); b.U32(0); b.U32(0);
        return b.ToArray();
    }

    /// <summary>An sfnt file with correct checksums, written by the test (not by the engine).</summary>
    public static byte[] Assemble(uint flavor, SortedDictionary<string, byte[]> tables)
    {
        var b = new Be();
        var n = tables.Count;
        var power = 1; var log = 0;
        while (power * 2 <= n) { power *= 2; log++; }
        b.U32(flavor); b.U16((ushort)n); b.U16((ushort)(power * 16)); b.U16((ushort)log); b.U16((ushort)(n * 16 - power * 16));
        var offset = 12 + 16 * n;
        foreach (var (tag, data) in tables)
        {
            b.Bytes(Encoding.ASCII.GetBytes(tag)); b.U32(Sum(data)); b.U32((uint)offset); b.U32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        foreach (var data in tables.Values)
        {
            b.Bytes(data);
            for (var i = data.Length; i % 4 != 0; i++) b.U8(0);
        }
        var file = b.ToArray();
        var headOffset = 12 + 16 * n + tables.TakeWhile(t => t.Key != "head").Sum(t => (t.Value.Length + 3) & ~3);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Sum(file)));
        return file;
    }

    public static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    /// <summary>Asserts every directory checksum and head.checkSumAdjustment of an sfnt file, recomputed here from the raw bytes.</summary>
    public static void AssertChecksums(byte[] file)
    {
        int n = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        var headOffset = -1;
        for (var i = 0; i < n; i++)
        {
            var rec = file.AsSpan(12 + i * 16);
            var tag = Encoding.ASCII.GetString(rec[..4]);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(rec[4..]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[12..]);
            Assert.True(offset % 4 == 0, $"{tag} is not four-byte aligned");
            var data = file.AsSpan(offset, length).ToArray();
            if (tag == "head") { headOffset = offset; BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0); }
            Assert.True(Sum(data) == checksum, $"checksum of {tag}");
        }
        Assert.True(headOffset > 0, "no head table");
        var copy = (byte[])file.Clone();
        var adjustment = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(headOffset + 8));
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(headOffset + 8), 0);
        Assert.Equal(unchecked(0xB1B0AFBA - Sum(copy)), adjustment);
    }

    /// <summary>
    /// The two fonts hold the same data: same flavor and tables, every table byte for byte except glyf/loca (compared glyph by glyph,
    /// points and instructions) and head (checkSumAdjustment and flags bit 11 may differ). <paramref name="ignore"/> lists tables one side may lack.
    /// </summary>
    public static void AssertEquivalent(SfntFont expected, SfntFont actual, params string[] ignore)
    {
        Assert.Equal(expected.Flavor, actual.Flavor);
        Assert.Equal(expected.Tables.Keys.Except(ignore), actual.Tables.Keys.Except(ignore));
        foreach (var (tag, data) in expected.Tables)
        {
            if (ignore.Contains(tag) || tag is "glyf" or "loca") continue;
            var other = actual.Tables[tag];
            if (tag == "head")
            {
                var a = (byte[])data.Clone(); var b = (byte[])other.Clone();
                foreach (var h in new[] { a, b }) { BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), 0); h[16] &= 0xF7; }
                Assert.Equal(a, b);
                continue;
            }
            Assert.True(data.AsSpan().SequenceEqual(other), $"table '{tag}' differs");
        }

        if (!expected.HasTrueTypeOutlines) return;
        var left = GlyfTable.Read(expected);
        var right = GlyfTable.Read(actual);
        Assert.Equal(left.Count, right.Count);
        for (var i = 0; i < left.Count; i++) AssertSameGlyph(left[i], right[i], i);
    }

    /// <param name="pointBoundsAllowed">
    /// A WOFF2 file made by another tool may leave out a simple glyph's stored bounding box; the specification then takes the bounds of the
    /// points, which is what <paramref name="b"/> must hold in that case.
    /// </param>
    public static void AssertSameGlyph(Glyph a, Glyph b, int index, bool pointBoundsAllowed = false)
    {
        Assert.True(a.Kind == b.Kind, $"glyph {index}: kind");
        if (a.Kind == GlyphKind.Empty) return;
        var sameBox = (a.XMin, a.YMin, a.XMax, a.YMax) == (b.XMin, b.YMin, b.XMax, b.YMax)
                      || (pointBoundsAllowed && a.Kind == GlyphKind.Simple && (b.XMin, b.YMin, b.XMax, b.YMax) == b.PointBounds());
        Assert.True(sameBox, $"glyph {index}: bounding box");
        Assert.True(a.EndPoints.SequenceEqual(b.EndPoints), $"glyph {index}: contours");
        Assert.True(a.OnCurvePoints.SequenceEqual(b.OnCurvePoints), $"glyph {index}: on-curve flags");
        Assert.True(a.X.SequenceEqual(b.X) && a.Y.SequenceEqual(b.Y), $"glyph {index}: points");
        Assert.True(a.Instructions.SequenceEqual(b.Instructions), $"glyph {index}: instructions");
        Assert.True(a.Components.SequenceEqual(b.Components), $"glyph {index}: components");
        Assert.True(a.Overlap == b.Overlap, $"glyph {index}: overlap flag");
    }

    private sealed class Be
    {
        private readonly MemoryStream _s = new();
        public void U8(byte v) => _s.WriteByte(v);
        public void U16(ushort v) { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
        public void I16(short v) => U16(unchecked((ushort)v));
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void Bytes(ReadOnlySpan<byte> b) => _s.Write(b);
        public byte[] ToArray() => _s.ToArray();
    }
}
