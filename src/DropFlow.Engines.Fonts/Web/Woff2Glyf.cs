// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Web;

/// <summary>
/// The WOFF 2.0 'glyf' / 'loca' transform (section 5.1 "Transformed glyf table format") and the 'hmtx' transform (section 5.4).
/// The transformed glyf table splits the glyph data into seven streams (contour counts, point counts, flags, coordinates as
/// "triplets", composite records, explicit bounding boxes, instructions) that compress better; loca is not stored at all.
/// </summary>
internal static class Woff2Glyf
{
    private const int HeaderSize = 36; // reserved, optionFlags, numGlyphs, indexFormat and seven stream sizes

    // ---- decoding ------------------------------------------------------------------------------------------------------

    /// <summary>Rebuilds glyph records from a transformed glyf table. Returns the records and the loca format the table asks for.</summary>
    public static (List<Glyph> Glyphs, int IndexFormat) Reconstruct(ReadOnlySpan<byte> data)
    {
        var header = new SpanReader(data);
        header.U16(); // reserved (the first half of the old 32-bit version field)
        var optionFlags = header.U16();
        int numGlyphs = header.U16();
        int indexFormat = header.U16();
        var sizes = new uint[7];
        for (var i = 0; i < 7; i++) sizes[i] = header.U32();

        long position = HeaderSize;
        var streams = new int[8];
        for (var i = 0; i < 7; i++)
        {
            streams[i] = (int)position;
            position += sizes[i];
            if (position > data.Length) throw new FontFailure("The WOFF2 glyph data is damaged (a stream is longer than the table).");
        }
        streams[7] = (int)position;

        var nContourStream = data.Slice(streams[0], (int)sizes[0]);
        var nPoints = new SpanReader(data[..streams[2]], streams[1]);
        var flags = new SpanReader(data[..streams[3]], streams[2]);
        var glyphStream = new SpanReader(data[..streams[4]], streams[3]);
        var composite = new SpanReader(data[..streams[5]], streams[4]);
        var bboxStart = streams[5];
        var bboxBitmapLength = 4 * ((numGlyphs + 31) / 32);
        if (sizes[5] < bboxBitmapLength) throw new FontFailure("The WOFF2 bounding box stream is too short.");
        var bboxBitmap = data.Slice(bboxStart, bboxBitmapLength);
        var bbox = new SpanReader(data[..streams[6]], bboxStart + bboxBitmapLength);
        var instructions = new SpanReader(data[..streams[7]], streams[6]);

        ReadOnlySpan<byte> overlapBitmap = default;
        if ((optionFlags & 1) != 0)
        {
            var overlapLength = (numGlyphs + 7) / 8;
            if (streams[7] + overlapLength > data.Length) throw new FontFailure("The WOFF2 overlap bitmap is cut short.");
            overlapBitmap = data.Slice(streams[7], overlapLength);
        }

        if (nContourStream.Length < numGlyphs * 2) throw new FontFailure("The WOFF2 contour count stream is too short.");

        var glyphs = new List<Glyph>(numGlyphs);
        for (var g = 0; g < numGlyphs; g++)
        {
            var contours = BinaryPrimitives.ReadInt16BigEndian(nContourStream[(g * 2)..]);
            var hasBbox = (bboxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;

            if (contours == 0)
            {
                if (hasBbox) throw new FontFailure($"The WOFF2 glyph {g} is empty but has a bounding box.");
                glyphs.Add(Glyph.EmptyGlyph);
                continue;
            }

            if (contours == -1)
            {
                if (!hasBbox) throw new FontFailure($"The WOFF2 composite glyph {g} has no bounding box.");
                var start = composite.Position;
                var rest = data[start..streams[5]];
                var length = Glyph.CompositeLength(rest, out var hasInstructions);
                var components = composite.Bytes(length).ToArray();
                byte[] code = [];
                if (hasInstructions)
                {
                    var count = Woff2Numbers.Read255UInt16(ref glyphStream);
                    code = instructions.Bytes(count).ToArray();
                }
                glyphs.Add(new Glyph
                {
                    Kind = GlyphKind.Composite,
                    XMin = bbox.I16(), YMin = bbox.I16(), XMax = bbox.I16(), YMax = bbox.I16(),
                    Components = components,
                    CompositeHasInstructions = hasInstructions,
                    Instructions = code,
                });
                continue;
            }

            if (contours < 0) throw new FontFailure($"The WOFF2 glyph {g} has an invalid contour count.");

            var ends = new ushort[contours];
            var total = 0;
            for (var c = 0; c < contours; c++)
            {
                total += Woff2Numbers.Read255UInt16(ref nPoints);
                if (total > ushort.MaxValue) throw new FontFailure($"The WOFF2 glyph {g} has too many points.");
                ends[c] = (ushort)(total - 1);
            }
            if (total == 0) throw new FontFailure($"The WOFF2 glyph {g} has contours without points.");

            var onCurve = new bool[total];
            var xs = new int[total];
            var ys = new int[total];
            int x = 0, y = 0;
            for (var p = 0; p < total; p++)
            {
                var flag = flags.U8();
                onCurve[p] = (flag & 0x80) == 0;
                var (dx, dy) = DecodeTriplet(flag & 0x7F, ref glyphStream);
                x += dx;
                y += dy;
                xs[p] = x;
                ys[p] = y;
            }

            var instructionLength = Woff2Numbers.Read255UInt16(ref glyphStream);
            var program = instructions.Bytes(instructionLength).ToArray();
            var overlap = !overlapBitmap.IsEmpty && (overlapBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;

            var glyph = new Glyph
            {
                Kind = GlyphKind.Simple,
                EndPoints = ends,
                OnCurvePoints = onCurve,
                X = xs,
                Y = ys,
                Instructions = program,
                Overlap = overlap,
            };
            if (hasBbox)
            {
                glyph.XMin = bbox.I16(); glyph.YMin = bbox.I16(); glyph.XMax = bbox.I16(); glyph.YMax = bbox.I16();
            }
            else
            {
                (glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax) = glyph.PointBounds();
            }
            glyphs.Add(glyph);
        }

        return (glyphs, indexFormat);
    }

    /// <summary>One point delta (section 5.2 "Decoding of variable-length X and Y coordinates", the 128-entry triplet table).</summary>
    internal static (int Dx, int Dy) DecodeTriplet(int index, ref SpanReader stream)
    {
        // Bit 0 of the index is the sign of x (set = positive), bit 1 the sign of y; for the first two groups only one coordinate moves,
        // and bit 0 is its sign.
        static int Signed(int bit, int value) => bit != 0 ? value : -value;

        if (index < 10)
            return (0, Signed(index & 1, ((index & 14) << 7) + stream.U8()));
        if (index < 20)
            return (Signed(index & 1, (((index - 10) & 14) << 7) + stream.U8()), 0);
        if (index < 84)
        {
            var b0 = index - 20;
            var b1 = stream.U8();
            return (Signed(index & 1, 1 + (b0 & 0x30) + (b1 >> 4)), Signed(index & 2, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
        }
        if (index < 120)
        {
            var b0 = index - 84;
            var b1 = stream.U8();
            var b2 = stream.U8();
            return (Signed(index & 1, 1 + ((b0 / 12) << 8) + b1), Signed(index & 2, 1 + (((b0 % 12) >> 2) << 8) + b2));
        }
        if (index < 124)
        {
            var b1 = stream.U8();
            var b2 = stream.U8();
            var b3 = stream.U8();
            return (Signed(index & 1, (b1 << 4) + (b2 >> 4)), Signed(index & 2, ((b2 & 0x0F) << 8) + b3));
        }
        {
            var b1 = stream.U8();
            var b2 = stream.U8();
            var b3 = stream.U8();
            var b4 = stream.U8();
            return (Signed(index & 1, (b1 << 8) + b2), Signed(index & 2, (b3 << 8) + b4));
        }
    }

    /// <summary>
    /// Rebuilds 'hmtx' from its transformed form: advance widths always, left side bearings either stored or taken from the glyphs' xMin.
    /// </summary>
    public static byte[] ReconstructHmtx(ReadOnlySpan<byte> data, int numGlyphs, int numberOfHMetrics, IReadOnlyList<Glyph> glyphs)
    {
        if (numberOfHMetrics < 1 || numberOfHMetrics > numGlyphs) throw new FontFailure("The font's horizontal metrics are damaged.");
        var reader = new SpanReader(data);
        var flags = reader.U8();
        var advances = new ushort[numberOfHMetrics];
        for (var i = 0; i < numberOfHMetrics; i++) advances[i] = reader.U16();
        var lsbs = new short[numGlyphs];
        for (var i = 0; i < numberOfHMetrics; i++) lsbs[i] = (flags & 1) == 0 ? reader.I16() : XMinOf(glyphs, i);
        for (var i = numberOfHMetrics; i < numGlyphs; i++) lsbs[i] = (flags & 2) == 0 ? reader.I16() : XMinOf(glyphs, i);

        var w = new BigEndianWriter();
        for (var i = 0; i < numberOfHMetrics; i++) { w.U16(advances[i]); w.I16(lsbs[i]); }
        for (var i = numberOfHMetrics; i < numGlyphs; i++) w.I16(lsbs[i]);
        return w.ToArray();
    }

    private static short XMinOf(IReadOnlyList<Glyph> glyphs, int index) =>
        index < glyphs.Count && glyphs[index].Kind != GlyphKind.Empty ? glyphs[index].XMin : (short)0;

    // ---- encoding ------------------------------------------------------------------------------------------------------

    /// <summary>The transformed glyf table for <paramref name="glyphs"/>.</summary>
    public static byte[] Transform(IReadOnlyList<Glyph> glyphs, int indexFormat)
    {
        var count = glyphs.Count;
        var nContours = new BigEndianWriter();
        var nPoints = new BigEndianWriter();
        var flagStream = new BigEndianWriter();
        var glyphStream = new BigEndianWriter();
        var compositeStream = new BigEndianWriter();
        var bboxBitmap = new byte[4 * ((count + 31) / 32)];
        var bboxStream = new BigEndianWriter();
        var instructionStream = new BigEndianWriter();
        var overlapBitmap = new byte[(count + 7) / 8];
        var anyOverlap = false;

        for (var g = 0; g < count; g++)
        {
            var glyph = glyphs[g];
            switch (glyph.Kind)
            {
                case GlyphKind.Empty:
                    nContours.I16(0);
                    break;

                case GlyphKind.Composite:
                    nContours.I16(-1);
                    compositeStream.Bytes(glyph.Components);
                    bboxBitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                    bboxStream.I16(glyph.XMin); bboxStream.I16(glyph.YMin); bboxStream.I16(glyph.XMax); bboxStream.I16(glyph.YMax);
                    if (glyph.CompositeHasInstructions)
                    {
                        Woff2Numbers.Write255UInt16(glyphStream, glyph.Instructions.Length);
                        instructionStream.Bytes(glyph.Instructions);
                    }
                    break;

                default:
                    nContours.I16((short)glyph.EndPoints.Length);
                    var previous = -1;
                    foreach (var end in glyph.EndPoints)
                    {
                        Woff2Numbers.Write255UInt16(nPoints, end - previous);
                        previous = end;
                    }

                    int x = 0, y = 0;
                    for (var p = 0; p < glyph.PointCount; p++)
                    {
                        EncodeTriplet(glyph.OnCurvePoints[p], glyph.X[p] - x, glyph.Y[p] - y, flagStream, glyphStream);
                        x = glyph.X[p];
                        y = glyph.Y[p];
                    }
                    Woff2Numbers.Write255UInt16(glyphStream, glyph.Instructions.Length);
                    instructionStream.Bytes(glyph.Instructions);

                    if (glyph.PointBounds() != (glyph.XMin, glyph.YMin, glyph.XMax, glyph.YMax))
                    {
                        bboxBitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                        bboxStream.I16(glyph.XMin); bboxStream.I16(glyph.YMin); bboxStream.I16(glyph.XMax); bboxStream.I16(glyph.YMax);
                    }
                    if (glyph.Overlap)
                    {
                        overlapBitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                        anyOverlap = true;
                    }
                    break;
            }
        }

        var bbox = new BigEndianWriter();
        bbox.Bytes(bboxBitmap);
        bbox.Bytes(bboxStream.ToArray());

        var parts = new[] { nContours.ToArray(), nPoints.ToArray(), flagStream.ToArray(), glyphStream.ToArray(), compositeStream.ToArray(), bbox.ToArray(), instructionStream.ToArray() };
        var w = new BigEndianWriter();
        w.U16(0); // reserved
        w.U16((ushort)(anyOverlap ? 1 : 0));
        w.U16((ushort)count);
        w.U16((ushort)indexFormat);
        foreach (var part in parts) w.U32((uint)part.Length);
        foreach (var part in parts) w.Bytes(part);
        if (anyOverlap) w.Bytes(overlapBitmap);
        return w.ToArray();
    }

    /// <summary>Writes the flag byte and the coordinate bytes of one point delta with the shortest triplet that holds it.</summary>
    internal static void EncodeTriplet(bool onCurve, int dx, int dy, BigEndianWriter flags, BigEndianWriter stream)
    {
        var offCurve = onCurve ? 0 : 0x80;
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        var xSign = dx < 0 ? 0 : 1;
        var ySign = dy < 0 ? 0 : 2;

        if (dx == 0 && ay < 1280)
        {
            flags.U8((byte)(offCurve + ((ay >> 8) << 1) + (dy < 0 ? 0 : 1)));
            stream.U8((byte)(ay & 0xFF));
        }
        else if (dy == 0 && ax < 1280)
        {
            flags.U8((byte)(offCurve + 10 + ((ax >> 8) << 1) + xSign));
            stream.U8((byte)(ax & 0xFF));
        }
        else if (ax is >= 1 and <= 64 && ay is >= 1 and <= 64)
        {
            var bx = ax - 1;
            var by = ay - 1;
            flags.U8((byte)(offCurve + 20 + (bx & 0x30) + ((by & 0x30) >> 2) + xSign + ySign));
            stream.U8((byte)(((bx & 0x0F) << 4) | (by & 0x0F)));
        }
        else if (ax is >= 1 and <= 768 && ay is >= 1 and <= 768)
        {
            var bx = ax - 1;
            var by = ay - 1;
            flags.U8((byte)(offCurve + 84 + 12 * (bx >> 8) + ((by >> 8) << 2) + xSign + ySign));
            stream.U8((byte)(bx & 0xFF));
            stream.U8((byte)(by & 0xFF));
        }
        else if (ax < 4096 && ay < 4096)
        {
            flags.U8((byte)(offCurve + 120 + xSign + ySign));
            stream.U8((byte)(ax >> 4));
            stream.U8((byte)(((ax & 0x0F) << 4) | (ay >> 8)));
            stream.U8((byte)(ay & 0xFF));
        }
        else
        {
            if (ax > ushort.MaxValue || ay > ushort.MaxValue) throw new FontFailure("A glyph point is too far from the previous one.");
            flags.U8((byte)(offCurve + 124 + xSign + ySign));
            stream.U8((byte)(ax >> 8));
            stream.U8((byte)(ax & 0xFF));
            stream.U8((byte)(ay >> 8));
            stream.U8((byte)(ay & 0xFF));
        }
    }

    /// <summary>
    /// The transformed hmtx table, or null when no side bearing can be left out (then the table is stored as it is). A bearing can be left
    /// out when every glyph in its group has lsb == xMin (empty glyphs count as xMin 0).
    /// </summary>
    public static byte[]? TransformHmtx(byte[] hmtx, int numGlyphs, int numberOfHMetrics, IReadOnlyList<Glyph> glyphs)
    {
        if (numberOfHMetrics < 1 || numberOfHMetrics > numGlyphs) return null;
        if (hmtx.Length < numberOfHMetrics * 4 + (numGlyphs - numberOfHMetrics) * 2) return null;
        var advances = new ushort[numberOfHMetrics];
        var lsbs = new short[numGlyphs];
        for (var i = 0; i < numberOfHMetrics; i++)
        {
            advances[i] = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(i * 4));
            lsbs[i] = BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(i * 4 + 2));
        }
        for (var i = numberOfHMetrics; i < numGlyphs; i++)
            lsbs[i] = BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(numberOfHMetrics * 4 + (i - numberOfHMetrics) * 2));

        var proportional = Enumerable.Range(0, numberOfHMetrics).All(i => lsbs[i] == XMinOf(glyphs, i));
        var monospaced = Enumerable.Range(numberOfHMetrics, numGlyphs - numberOfHMetrics).All(i => lsbs[i] == XMinOf(glyphs, i));
        if (!proportional && !monospaced) return null;

        var w = new BigEndianWriter();
        w.U8((byte)((proportional ? 1 : 0) | (monospaced ? 2 : 0)));
        foreach (var advance in advances) w.U16(advance);
        if (!proportional) for (var i = 0; i < numberOfHMetrics; i++) w.I16(lsbs[i]);
        if (!monospaced) for (var i = numberOfHMetrics; i < numGlyphs; i++) w.I16(lsbs[i]);
        return w.ToArray();
    }
}
