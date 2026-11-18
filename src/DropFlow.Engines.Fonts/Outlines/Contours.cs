// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Outlines;

internal readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>A straight line (<see cref="IsLine"/>) or a cubic Bézier curve ending at <see cref="End"/>.</summary>
internal readonly record struct Segment(bool IsLine, Pt C1, Pt C2, Pt End)
{
    public static Segment Line(Pt end) => new(true, end, end, end);
    public static Segment Curve(Pt c1, Pt c2, Pt end) => new(false, c1, c2, end);
}

/// <summary>A closed contour of lines and cubic curves (the way CFF charstrings describe outlines). The closing line back to <see cref="Start"/> is implied.</summary>
internal sealed class CubicContour(Pt start)
{
    public Pt Start { get; } = start;
    public List<Segment> Segments { get; } = [];
}

/// <summary>Conversions between TrueType contours (quadratic, on/off-curve points) and cubic contours, and the direction rules of each format.</summary>
internal static class ContourMath
{
    /// <summary>
    /// TrueType points to cubic contours. Quadratic curves become cubic curves exactly (control points at two thirds), implied on-curve
    /// points (between two off-curve points) are restored, and the direction is reversed: TrueType outer contours run clockwise, PostScript
    /// (CFF) ones counter-clockwise.
    /// </summary>
    public static List<CubicContour> FromTrueType(IReadOnlyList<(Pt Point, bool OnCurve)[]> contours)
    {
        var result = new List<CubicContour>();
        foreach (var raw in contours)
        {
            if (raw.Length == 0) continue;
            var points = raw.Reverse().ToArray();
            var n = points.Length;
            var first = Array.FindIndex(points, p => p.OnCurve);

            // The walk: from an on-curve start point through every other point and back to the start. When every point is off-curve the
            // start is the implied on-curve point between the last and the first one.
            Pt start;
            List<(Pt Point, bool OnCurve)> walk;
            if (first < 0)
            {
                start = Mid(points[n - 1].Point, points[0].Point);
                walk = [.. points];
            }
            else
            {
                start = points[first].Point;
                walk = [.. points.Skip(first + 1), .. points.Take(first)];
            }
            walk.Add((start, true));

            var contour = new CubicContour(start);
            var current = start;
            Pt? control = null;
            foreach (var (point, on) in walk)
            {
                if (on)
                {
                    if (control is { } c) contour.Segments.Add(QuadToCubic(current, c, point));
                    else if (point != current) contour.Segments.Add(Segment.Line(point));
                    current = point;
                    control = null;
                }
                else
                {
                    if (control is { } c)
                    {
                        var implied = Mid(c, point);
                        contour.Segments.Add(QuadToCubic(current, c, implied));
                        current = implied;
                    }
                    control = point;
                }
            }

            // The closing line is implied in CFF: drop a final straight line that only returns to the start.
            if (contour.Segments.Count > 0 && contour.Segments[^1] is { IsLine: true } closing && closing.End == start) contour.Segments.RemoveAt(contour.Segments.Count - 1);
            result.Add(contour);
        }
        return result;
    }

    private static Segment QuadToCubic(Pt p0, Pt q, Pt p2) => Segment.Curve(p0 + (q - p0) * (2.0 / 3), p2 + (q - p2) * (2.0 / 3), p2);

    private static Pt Mid(Pt a, Pt b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>
    /// Cubic contours to TrueType points: each cubic curve is split into the fewest equal pieces whose quadratic approximation stays within
    /// <paramref name="tolerance"/> font units, and the direction is reversed (counter-clockwise PostScript to clockwise TrueType).
    /// Coordinates are rounded to whole units, as 'glyf' stores them.
    /// </summary>
    public static List<(int X, int Y, bool OnCurve)[]> ToTrueType(IReadOnlyList<CubicContour> contours, double tolerance)
    {
        var result = new List<(int, int, bool)[]>();
        foreach (var contour in contours)
        {
            var points = new List<(int X, int Y, bool On)> { (R(contour.Start.X), R(contour.Start.Y), true) };
            var current = contour.Start;
            foreach (var s in contour.Segments)
            {
                if (s.IsLine)
                {
                    points.Add((R(s.End.X), R(s.End.Y), true));
                }
                else
                {
                    foreach (var (control, end) in CubicToQuads(current, s.C1, s.C2, s.End, tolerance))
                    {
                        points.Add((R(control.X), R(control.Y), false));
                        points.Add((R(end.X), R(end.Y), true));
                    }
                }
                current = s.End;
            }

            // Remove the explicit return to the start point and repeated on-curve points (zero-length lines).
            if (points.Count > 1 && points[^1].On && (points[^1].X, points[^1].Y) == (points[0].X, points[0].Y)) points.RemoveAt(points.Count - 1);
            var cleaned = new List<(int X, int Y, bool On)>();
            foreach (var p in points)
            {
                if (cleaned.Count > 0 && p.On && cleaned[^1].On && (cleaned[^1].X, cleaned[^1].Y) == (p.X, p.Y)) continue;
                cleaned.Add(p);
            }
            if (cleaned.Count < 2) continue; // a contour that is only a point draws nothing

            cleaned.Reverse();
            // Start on the point that was the contour's start (now last) so the first point is on-curve.
            var startIndex = cleaned.Count - 1;
            var rotated = cleaned.Skip(startIndex).Concat(cleaned.Take(startIndex)).ToArray();
            result.Add(rotated);
        }
        return result;
    }

    /// <summary>The quadratic pieces (control, end) of one cubic curve.</summary>
    internal static IEnumerable<(Pt Control, Pt End)> CubicToQuads(Pt p0, Pt p1, Pt p2, Pt p3, double tolerance)
    {
        // With the single-control approximation Q = (3(P1 + P2) - (P0 + P3)) / 4 the largest distance from the cubic is
        // sqrt(3)/36 * |P3 - 3 P2 + 3 P1 - P0|; splitting into n equal parameter pieces divides that third difference by n^3.
        var third = (p3 - p2 * 3 + p1 * 3 - p0).Length;
        var n = Math.Max(1, (int)Math.Ceiling(Math.Cbrt(Math.Sqrt(3) / 36 * third / Math.Max(tolerance, 1e-6))));
        n = Math.Min(n, 64);
        for (var i = 0; i < n; i++)
        {
            var (a, b, c, d) = SubCurve(p0, p1, p2, p3, (double)i / n, (double)(i + 1) / n);
            var control = new Pt((3 * (b.X + c.X) - (a.X + d.X)) / 4, (3 * (b.Y + c.Y) - (a.Y + d.Y)) / 4);
            yield return (control, d);
        }
    }

    /// <summary>The part of a cubic between parameters <paramref name="t0"/> and <paramref name="t1"/>, as a cubic.</summary>
    private static (Pt, Pt, Pt, Pt) SubCurve(Pt p0, Pt p1, Pt p2, Pt p3, double t0, double t1)
    {
        Pt At(double t)
        {
            var u = 1 - t;
            return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }

        Pt Derivative(double t)
        {
            var u = 1 - t;
            return (p1 - p0) * (3 * u * u) + (p2 - p1) * (6 * u * t) + (p3 - p2) * (3 * t * t);
        }

        var a = At(t0);
        var d = At(t1);
        var scale = (t1 - t0) / 3;
        return (a, a + Derivative(t0) * scale, d - Derivative(t1) * scale, d);
    }

    private static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    // ---- TrueType glyphs to point lists (composites flattened) --------------------------------------------------------------

    /// <summary>The contours of glyph <paramref name="index"/> with composites resolved (offsets, point matching, scales, 2x2 matrices).</summary>
    public static List<(Pt Point, bool OnCurve)[]> TrueTypeContours(IReadOnlyList<Glyph> glyphs, int index, int depth = 0)
    {
        if (depth > 16) throw new FontFailure("A composite glyph refers to itself.");
        var glyph = glyphs[index];
        var result = new List<(Pt, bool)[]>();
        if (glyph.Kind == GlyphKind.Simple)
        {
            var start = 0;
            foreach (var end in glyph.EndPoints)
            {
                var contour = new (Pt, bool)[end - start + 1];
                for (var i = start; i <= end; i++) contour[i - start] = (new Pt(glyph.X[i], glyph.Y[i]), glyph.OnCurvePoints[i]);
                result.Add(contour);
                start = end + 1;
            }
            return result;
        }
        if (glyph.Kind != GlyphKind.Composite) return result;

        var data = glyph.Components;
        var position = 0;
        while (position + 4 <= data.Length)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position));
            int child = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2));
            position += 4;
            int arg1, arg2;
            var xyValues = (flags & 0x0002) != 0;
            if ((flags & Glyph.ArgsAreWords) != 0)
            {
                arg1 = xyValues ? BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(position)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position));
                arg2 = xyValues ? BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(position + 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2));
                position += 4;
            }
            else
            {
                arg1 = xyValues ? (sbyte)data[position] : data[position];
                arg2 = xyValues ? (sbyte)data[position + 1] : data[position + 1];
                position += 2;
            }

            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & Glyph.HaveScale) != 0) { a = d = F2Dot14(data, position); position += 2; }
            else if ((flags & Glyph.HaveXYScale) != 0) { a = F2Dot14(data, position); d = F2Dot14(data, position + 2); position += 4; }
            else if ((flags & Glyph.HaveTwoByTwo) != 0)
            {
                a = F2Dot14(data, position); b = F2Dot14(data, position + 2); c = F2Dot14(data, position + 4); d = F2Dot14(data, position + 6);
                position += 8;
            }

            if (child >= glyphs.Count) throw new FontFailure($"Composite glyph {index} refers to a missing glyph.");
            var parts = TrueTypeContours(glyphs, child, depth + 1);
            var transformed = parts.Select(contour => contour.Select(p => (new Pt(p.Point.X * a + p.Point.Y * c, p.Point.X * b + p.Point.Y * d), p.OnCurve)).ToArray()).ToList();

            Pt offset;
            if (xyValues)
            {
                offset = new Pt(arg1, arg2);
                // SCALED_COMPONENT_OFFSET (0x0800) asks for the offset to be transformed too (Apple's convention).
                if ((flags & 0x0800) != 0 && (flags & 0x1000) == 0) offset = new Pt(arg1 * a + arg2 * c, arg1 * b + arg2 * d);
            }
            else
            {
                // Point matching: point arg1 of the glyph built so far meets point arg2 of the new component.
                var existing = result.SelectMany(x => x).ToList();
                var incoming = transformed.SelectMany(x => x).ToList();
                if (arg1 >= existing.Count || arg2 >= incoming.Count) throw new FontFailure($"Composite glyph {index} matches points that do not exist.");
                offset = existing[arg1].Item1 - incoming[arg2].Item1;
            }

            foreach (var contour in transformed) result.Add(contour.Select(p => (p.Item1 + offset, p.OnCurve)).ToArray());
            if ((flags & Glyph.MoreComponents) == 0) break;
        }
        return result;
    }

    private static double F2Dot14(byte[] data, int position) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(position)) / 16384.0;
}
