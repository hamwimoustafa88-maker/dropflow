// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>
/// Turns one closed pixel-edge loop into cubic Béziers:
/// <list type="number">
/// <item>corners are the loop vertices where the direction over a window of <c>window</c> steps on either side turns by more than
/// the corner angle (non-maximum suppression keeps one vertex per corner); a corner stays exactly on its pixel corner;</item>
/// <item>between corners the curve follows the midpoints of the pixel edges, which removes the staircase;</item>
/// <item>each run between corners is fitted with <see cref="BezierFitter"/>; a loop without corners (a circle) is cut in two with a
/// shared smooth tangent.</item>
/// </list>
/// </summary>
internal static class ContourFitter
{
    public static List<CubicBezier> Fit(IReadOnlyList<(int X, int Y)> loop, int window, double cornerDegrees, double maxError)
    {
        var n = loop.Count;
        var vertices = loop.Select(p => new Vec(p.X, p.Y)).ToArray();
        if (n < 4) return Polygon(vertices);

        var k = Math.Max(1, Math.Min(window, n / 4));
        var corners = Corners(vertices, k, cornerDegrees);
        if (corners.Count == n) return Polygon(vertices);   // every vertex is a corner: a pixel-sized shape

        var mid = new Vec[n];
        for (var i = 0; i < n; i++) mid[i] = (vertices[i] + vertices[(i + 1) % n]) * 0.5;

        var curves = new List<CubicBezier>();
        if (corners.Count == 0)
        {
            // Smooth closed loop: two halves joined with tangents taken across the join.
            var half = n / 2;
            FitSmoothRun(mid, 0, half, k, maxError, curves);
            FitSmoothRun(mid, half, n, k, maxError, curves);
            return curves;
        }

        for (var c = 0; c < corners.Count; c++)
        {
            var a = corners[c];
            var b = corners[(c + 1) % corners.Count];
            var span = (b - a + n) % n;
            if (span == 0) span = n;   // a single corner: the run goes all the way round
            var points = new List<Vec>(span + 1) { vertices[a] };
            for (var j = 0; j < span; j++) points.Add(mid[(a + j) % n]);
            points.Add(vertices[b]);

            var reach = Math.Min(k, points.Count - 1);
            var startTangent = (points[reach] - points[0]).Normalized();
            var endTangent = (points[^(reach + 1)] - points[^1]).Normalized();
            curves.AddRange(BezierFitter.Fit(points, startTangent, endTangent, maxError));
        }
        return curves;
    }

    /// <summary>Corner vertices, in loop order.</summary>
    internal static List<int> Corners(Vec[] v, int k, double cornerDegrees)
    {
        var n = v.Length;
        var turn = new double[n];
        for (var i = 0; i < n; i++)
        {
            var before = v[i] - v[(i - k + n) % n];
            var after = v[(i + k) % n] - v[i];
            var cos = before.Normalized().Dot(after.Normalized());
            turn[i] = Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
        }

        var corners = new List<int>();
        var suppress = Math.Max(0, k - 1);
        for (var i = 0; i < n; i++)
        {
            if (turn[i] < cornerDegrees) continue;
            var isMax = true;
            for (var j = 1; j <= suppress && isMax; j++)
            {
                var prev = turn[(i - j + n) % n];
                var next = turn[(i + j) % n];
                if (prev >= turn[i] || next > turn[i]) isMax = false;   // ties go to the first vertex
            }
            if (isMax) corners.Add(i);
        }
        return corners;
    }

    private static void FitSmoothRun(Vec[] mid, int from, int to, int k, double maxError, List<CubicBezier> curves)
    {
        var n = mid.Length;
        var points = new List<Vec>(to - from + 1);
        for (var i = from; i <= to; i++) points.Add(mid[i % n]);
        var startTangent = (mid[(from + k) % n] - mid[(from - k + n) % n]).Normalized();
        var endTangent = (mid[(to - k + n) % n] - mid[(to + k) % n]).Normalized();
        curves.AddRange(BezierFitter.Fit(points, startTangent, endTangent, maxError));
    }

    private static List<CubicBezier> Polygon(Vec[] v) =>
        v.Select((p, i) =>
        {
            var q = v[(i + 1) % v.Length];
            return new CubicBezier(p, p + (q - p) * (1 / 3.0), p + (q - p) * (2 / 3.0), q);
        }).ToList();
}
