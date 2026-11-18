// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace DropFlow.TestSupport;

/// <summary>
/// Picture formats no library here can write (Magick.NET only reads camera RAW and DICOM), built byte by byte from the public
/// specifications so tests need no binary fixtures. Both are the smallest files the readers accept, not what a camera or a scanner writes.
/// </summary>
public static class PictureSamples
{
    /// <summary>
    /// A DNG 1.4 file laid out like a camera's: IFD0 is an 8x8 grey preview (what a plain TIFF reader shows) and its SubIFD holds the
    /// photo itself, uncompressed 8-bit "linear raw" RGB (PhotometricInterpretation 34892): the left half <paramref name="left"/>, the
    /// right half <paramref name="right"/>. The colour matrix says the camera sees sRGB primaries (D65). A reader that really develops
    /// RAW returns <paramref name="width"/> x <paramref name="height"/>; one that treats the file as a TIFF returns the 8x8 preview.
    /// </summary>
    public static string Dng(string path, int width = 48, int height = 32, (byte R, byte G, byte B)? left = null, (byte R, byte G, byte B)? right = null)
    {
        var l = left ?? (200, 30, 30);
        var r = right ?? (30, 30, 200);
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = x < width / 2 ? l : r;
                var i = (y * width + x) * 3;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (c.R, c.G, c.B);
            }
        const int previewSide = 8;
        var preview = Enumerable.Repeat((byte)128, previewSide * previewSide * 3).ToArray();

        var main = new List<Tag>
        {
            new(254, 4, 1, U32(1)),                       // NewSubfileType: reduced-resolution preview
            new(256, 4, 1, U32(previewSide)),
            new(257, 4, 1, U32(previewSide)),
            new(258, 3, 3, U16(8, 8, 8)),
            new(259, 3, 1, U16(1)),
            new(262, 3, 1, U16(2)),                       // RGB
            new(271, 2, 9, Ascii("DropFlow")),            // Make
            new(272, 2, 5, Ascii("Test")),                // Model
            new(273, 4, 1, U32(0)),                       // StripOffsets (patched)
            new(274, 3, 1, U16(1)),
            new(277, 3, 1, U16(3)),
            new(278, 4, 1, U32(previewSide)),
            new(279, 4, 1, U32((uint)preview.Length)),
            new(284, 3, 1, U16(1)),
            new(330, 4, 1, U32(0)),                       // SubIFDs (patched)
            new(50706, 1, 4, [1, 4, 0, 0]),               // DNGVersion
            new(50707, 1, 4, [1, 1, 0, 0]),               // DNGBackwardVersion
            new(50708, 2, 14, Ascii("DropFlow Test")),    // UniqueCameraModel
            new(50721, 10, 9, SRational(3.2406, -1.5372, -0.4986, -0.9689, 1.8758, 0.0415, 0.0557, -0.2040, 1.0570)), // ColorMatrix1 (XYZ to sRGB)
            new(50728, 5, 3, Rational(1, 1, 1)),          // AsShotNeutral
            new(50778, 3, 1, U16(21)),                    // CalibrationIlluminant1: D65
        };
        var raw = new List<Tag>
        {
            new(254, 4, 1, U32(0)),                       // the main image
            new(256, 4, 1, U32((uint)width)),
            new(257, 4, 1, U32((uint)height)),
            new(258, 3, 3, U16(8, 8, 8)),
            new(259, 3, 1, U16(1)),
            new(262, 3, 1, U16(34892)),                   // LinearRaw
            new(273, 4, 1, U32(0)),                       // StripOffsets (patched)
            new(277, 3, 1, U16(3)),
            new(278, 4, 1, U32((uint)height)),
            new(279, 4, 1, U32((uint)pixels.Length)),
            new(284, 3, 1, U16(1)),
            new(50717, 3, 3, U16(255, 255, 255)),         // WhiteLevel
        };

        const int mainOffset = 8;
        var rawOffset = mainOffset + IfdSize(main);
        var previewOffset = rawOffset + IfdSize(raw);
        var pixelsOffset = previewOffset + preview.Length;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("II"u8); w.Write((ushort)42); w.Write((uint)mainOffset);
        WriteIfd(w, main, mainOffset, new() { [273] = (uint)previewOffset, [330] = (uint)rawOffset });
        WriteIfd(w, raw, rawOffset, new() { [273] = (uint)pixelsOffset });
        w.Write(preview);
        w.Write(pixels);
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private sealed record Tag(ushort Id, ushort Type, uint Count, byte[] Data);

    private static int IfdSize(List<Tag> tags) =>
        2 + tags.Count * 12 + 4 + tags.Where(t => t.Data.Length > 4).Sum(t => t.Data.Length + t.Data.Length % 2);

    /// <summary>One IFD (no next IFD) followed by the values that do not fit in their entry.</summary>
    private static void WriteIfd(BinaryWriter w, List<Tag> tags, int offset, Dictionary<ushort, uint> patches)
    {
        var extraAt = offset + 2 + tags.Count * 12 + 4;
        var extra = new List<byte>();
        w.Write((ushort)tags.Count);
        foreach (var tag in tags)
        {
            w.Write(tag.Id); w.Write(tag.Type); w.Write(tag.Count);
            if (patches.TryGetValue(tag.Id, out var value)) w.Write(value);
            else if (tag.Data.Length > 4)
            {
                w.Write((uint)(extraAt + extra.Count));
                extra.AddRange(tag.Data);
                if (extra.Count % 2 == 1) extra.Add(0);
            }
            else { var inline = new byte[4]; tag.Data.CopyTo(inline, 0); w.Write(inline); }
        }
        w.Write(0u);
        w.Write(extra.ToArray());
    }

    /// <summary>
    /// A DICOM Part 10 file (explicit VR little endian, Secondary Capture) with an 8-bit MONOCHROME2 picture: the left half
    /// <paramref name="leftGray"/>, the right half <paramref name="rightGray"/>.
    /// </summary>
    public static string Dicom(string path, int width = 48, int height = 32, byte leftGray = 40, byte rightGray = 220)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = x < width / 2 ? leftGray : rightGray;

        const string secondaryCapture = "1.2.840.10008.5.1.4.1.1.7";
        using var meta = new MemoryStream();
        using (var m = new BinaryWriter(meta, Encoding.ASCII, leaveOpen: true))
        {
            Element(m, 0x0002, 0x0001, "OB", [0, 1]);
            Element(m, 0x0002, 0x0002, "UI", Ascii(secondaryCapture, pad: false));
            Element(m, 0x0002, 0x0003, "UI", Ascii("1.2.3.4", pad: false));
            Element(m, 0x0002, 0x0010, "UI", Ascii("1.2.840.10008.1.2.1", pad: false)); // explicit VR little endian
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new byte[128]);
        w.Write("DICM"u8);
        Element(w, 0x0002, 0x0000, "UL", U32((uint)meta.Length));
        w.Write(meta.ToArray());
        Element(w, 0x0008, 0x0016, "UI", Ascii(secondaryCapture, pad: false));
        Element(w, 0x0008, 0x0018, "UI", Ascii("1.2.3.4", pad: false));
        Element(w, 0x0028, 0x0002, "US", U16(1));                          // samples per pixel
        Element(w, 0x0028, 0x0004, "CS", Ascii("MONOCHROME2", pad: false));
        Element(w, 0x0028, 0x0010, "US", U16((ushort)height));             // rows
        Element(w, 0x0028, 0x0011, "US", U16((ushort)width));              // columns
        Element(w, 0x0028, 0x0100, "US", U16(8));                          // bits allocated
        Element(w, 0x0028, 0x0101, "US", U16(8));                          // bits stored
        Element(w, 0x0028, 0x0102, "US", U16(7));                          // high bit
        Element(w, 0x0028, 0x0103, "US", U16(0));                          // unsigned
        Element(w, 0x7FE0, 0x0010, "OB", pixels);
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void Element(BinaryWriter w, ushort group, ushort element, string vr, byte[] value)
    {
        if (value.Length % 2 == 1) value = [.. value, (byte)(vr is "UI" or "OB" ? 0 : ' ')];
        w.Write(group); w.Write(element);
        w.Write(Encoding.ASCII.GetBytes(vr));
        if (vr is "OB" or "OW") { w.Write((ushort)0); w.Write((uint)value.Length); }
        else w.Write((ushort)value.Length);
        w.Write(value);
    }

    private static byte[] U16(params ushort[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] U32(params uint[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] Ascii(string s, bool pad = true) => Encoding.ASCII.GetBytes(pad ? s + "\0" : s);

    private static byte[] SRational(params double[] values) =>
        values.SelectMany(v => BitConverter.GetBytes((int)Math.Round(v * 10000)).Concat(BitConverter.GetBytes(10000))).ToArray();

    private static byte[] Rational(params double[] values) =>
        values.SelectMany(v => BitConverter.GetBytes((uint)Math.Round(v * 10000)).Concat(BitConverter.GetBytes(10000u))).ToArray();
}
