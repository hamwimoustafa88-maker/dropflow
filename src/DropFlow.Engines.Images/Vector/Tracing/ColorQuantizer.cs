// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>
/// Colour reduction for tracing: pixels are first grouped into a 32x32x32 histogram (the downsampled palette: at most 32,768
/// weighted entries whatever the picture size), then weighted k-means with k-means++ seeding picks the colours. A fixed seed makes
/// the result repeatable.
/// </summary>
internal static class ColorQuantizer
{
    private const int Bits = 5;
    private const int Levels = 1 << Bits;

    /// <summary>Up to <paramref name="k"/> representative colours, most common first.</summary>
    public static Rgb[] Palette(IReadOnlyList<(byte R, byte G, byte B)> pixels, int k, int seed = 1)
    {
        var bins = new Bin[Levels * Levels * Levels];
        foreach (var (r, g, b) in pixels) bins[BinOf(r, g, b)].Add(r, g, b);
        return Palette(bins, k, seed);
    }

    /// <summary>Same as <see cref="Palette(IReadOnlyList{ValueTuple{byte, byte, byte}}, int, int)"/> for RGBA bytes; pixels with alpha below 128 are ignored.</summary>
    public static Rgb[] Palette(byte[] rgba, int k, int seed = 1)
    {
        var bins = new Bin[Levels * Levels * Levels];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
            if (rgba[i + 3] >= 128) bins[BinOf(rgba[i], rgba[i + 1], rgba[i + 2])].Add(rgba[i], rgba[i + 1], rgba[i + 2]);
        return Palette(bins, k, seed);
    }

    /// <summary>Index of the palette colour nearest to (r, g, b) in RGB distance.</summary>
    public static int Nearest(Rgb[] palette, byte r, byte g, byte b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            int dr = palette[i].R - r, dg = palette[i].G - g, db = palette[i].B - b;
            var d = dr * dr + dg * dg + db * db;
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    private static int BinOf(byte r, byte g, byte b) => ((r >> (8 - Bits)) << (2 * Bits)) | ((g >> (8 - Bits)) << Bits) | (b >> (8 - Bits));

    private struct Bin
    {
        public long Count;
        public double SumR, SumG, SumB;

        public void Add(byte r, byte g, byte b)
        {
            Count++;
            SumR += r; SumG += g; SumB += b;
        }
    }

    private readonly record struct Point(double R, double G, double B, double Weight)
    {
        public double DistanceSquared(double r, double g, double b) => (R - r) * (R - r) + (G - g) * (G - g) + (B - b) * (B - b);
    }

    private static Rgb[] Palette(Bin[] bins, int k, int seed)
    {
        var points = bins.Where(b => b.Count > 0).Select(b => new Point(b.SumR / b.Count, b.SumG / b.Count, b.SumB / b.Count, b.Count)).ToList();
        if (points.Count == 0) return [];
        k = Math.Clamp(k, 1, points.Count);

        var centres = Seed(points, k, new Random(seed));
        k = centres.Length;
        var assignment = new int[points.Count];
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var changed = false;
            for (var i = 0; i < points.Count; i++)
            {
                var nearest = NearestCentre(centres, points[i]);
                if (nearest == assignment[i]) continue;
                assignment[i] = nearest;
                changed = true;
            }

            var sums = new (double R, double G, double B, double W)[k];
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                ref var s = ref sums[assignment[i]];
                s.R += p.R * p.Weight; s.G += p.G * p.Weight; s.B += p.B * p.Weight; s.W += p.Weight;
            }
            for (var c = 0; c < k; c++)
            {
                if (sums[c].W > 0)
                {
                    centres[c] = new Point(sums[c].R / sums[c].W, sums[c].G / sums[c].W, sums[c].B / sums[c].W, sums[c].W);
                }
                else
                {
                    // An empty cluster restarts at the point worst served by the others.
                    var far = Enumerable.Range(0, points.Count).MaxBy(i => points[i].Weight * Dist(centres, points[i]));
                    centres[c] = points[far];
                    changed = true;
                }
            }
            if (!changed && iteration > 0) break;
        }

        return centres
            .OrderByDescending(c => c.Weight)
            .Select(c => new Rgb(ToByte(c.R), ToByte(c.G), ToByte(c.B)))
            .Distinct()
            .ToArray();
    }

    /// <summary>k-means++: each new centre is drawn with probability proportional to its weighted squared distance from the chosen ones.</summary>
    private static Point[] Seed(List<Point> points, int k, Random random)
    {
        var centres = new List<Point>(k);
        var totalWeight = points.Sum(p => p.Weight);
        centres.Add(Pick(points, points.Select(p => p.Weight).ToArray(), totalWeight, random));

        var distances = points.Select(p => p.Weight * p.DistanceSquared(centres[0].R, centres[0].G, centres[0].B)).ToArray();
        while (centres.Count < k)
        {
            var sum = distances.Sum();
            if (sum <= 0) break;
            var next = Pick(points, distances, sum, random);
            centres.Add(next);
            for (var i = 0; i < points.Count; i++)
                distances[i] = Math.Min(distances[i], points[i].Weight * points[i].DistanceSquared(next.R, next.G, next.B));
        }
        return centres.ToArray();
    }

    private static Point Pick(List<Point> points, double[] weights, double total, Random random)
    {
        var target = random.NextDouble() * total;
        for (var i = 0; i < points.Count; i++)
        {
            target -= weights[i];
            if (target <= 0) return points[i];
        }
        return points[^1];
    }

    private static int NearestCentre(Point[] centres, Point p)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var c = 0; c < centres.Length; c++)
        {
            var d = p.DistanceSquared(centres[c].R, centres[c].G, centres[c].B);
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        return best;
    }

    private static double Dist(Point[] centres, Point p) => centres.Min(c => p.DistanceSquared(c.R, c.G, c.B));

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
