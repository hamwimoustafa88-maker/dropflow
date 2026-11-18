// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>
/// Least-squares cubic Bézier fitting after Philip J. Schneider, "An Algorithm for Automatically Fitting Digitized Curves"
/// (Graphics Gems, 1990), written from the published description:
/// <list type="number">
/// <item>parameterise the points by chord length;</item>
/// <item>with the end points and end tangent directions fixed, solve the 2x2 normal equations for the two tangent lengths that
/// minimise the squared distance between the points and the curve at their parameters;</item>
/// <item>if the worst point is close, improve the parameters with Newton-Raphson steps (the nearest point on the curve) and refit;</item>
/// <item>otherwise split at the worst point, with a shared tangent there, and fit both halves.</item>
/// </list>
/// </summary>
internal static class BezierFitter
{
    private const int MaxNewtonRounds = 12;
    private const int MaxDepth = 32;

    /// <summary>
    /// Fits the points with as few cubics as possible so that no point is further than <paramref name="maxError"/> from the curve.
    /// <paramref name="startTangent"/> points from the first point into the curve; <paramref name="endTangent"/> points from the last
    /// point back into the curve. The first and last points are kept exactly.
    /// </summary>
    public static List<CubicBezier> Fit(IReadOnlyList<Vec> points, Vec startTangent, Vec endTangent, double maxError)
    {
        var result = new List<CubicBezier>();
        if (points.Count < 2) return result;
        FitRange(points, 0, points.Count - 1, startTangent.Normalized(), endTangent.Normalized(), maxError * maxError, result, 0);
        return result;
    }

    private static void FitRange(IReadOnlyList<Vec> d, int first, int last, Vec tangent1, Vec tangent2, double errorSq, List<CubicBezier> output, int depth)
    {
        var count = last - first + 1;
        if (count == 2)
        {
            var third = (d[last] - d[first]).Length / 3;
            output.Add(new CubicBezier(d[first], d[first] + tangent1 * third, d[last] + tangent2 * third, d[last]));
            return;
        }

        var u = ChordLengths(d, first, last);
        var curve = Generate(d, first, last, u, tangent1, tangent2);
        var (error, split) = MaxError(d, first, last, curve, u);
        if (error <= errorSq)
        {
            output.Add(curve);
            return;
        }

        // Better parameters (each point's nearest spot on the curve) are often all that is missing. Keep refining while it helps.
        var best = (Curve: curve, Error: error, Split: split);
        for (var round = 0; round < MaxNewtonRounds; round++)
        {
            u = Reparameterise(d, first, curve, u);
            curve = Generate(d, first, last, u, tangent1, tangent2);
            (error, split) = MaxError(d, first, last, curve, u);
            if (error <= errorSq)
            {
                output.Add(curve);
                return;
            }
            if (error < best.Error * 0.999) best = (curve, error, split);
            else if (round >= 2) break;
        }
        (curve, error, split) = best;

        if (depth >= MaxDepth)
        {
            output.Add(curve);
            return;
        }

        split = Math.Clamp(split, first + 1, last - 1);
        var centre = (d[split - 1] - d[split + 1]).Normalized();
        if (centre.LengthSquared == 0) centre = (d[split - 1] - d[split]).Normalized();
        FitRange(d, first, split, tangent1, centre, errorSq, output, depth + 1);
        FitRange(d, split, last, -centre, tangent2, errorSq, output, depth + 1);
    }

    /// <summary>The cubic with fixed ends and end directions whose two tangent lengths minimise the summed squared error.</summary>
    private static CubicBezier Generate(IReadOnlyList<Vec> d, int first, int last, double[] u, Vec tangent1, Vec tangent2)
    {
        Vec p0 = d[first], p3 = d[last];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (var i = 0; i < u.Length; i++)
        {
            var t = u[i];
            var s = 1 - t;
            double b0 = s * s * s, b1 = 3 * s * s * t, b2 = 3 * s * t * t, b3 = t * t * t;
            var a0 = tangent1 * b1;
            var a1 = tangent2 * b2;
            c00 += a0.Dot(a0);
            c01 += a0.Dot(a1);
            c11 += a1.Dot(a1);
            var rest = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += a0.Dot(rest);
            x1 += a1.Dot(rest);
        }

        var det = c00 * c11 - c01 * c01;
        double alpha1 = 0, alpha2 = 0;
        if (Math.Abs(det) > 1e-12)
        {
            alpha1 = (x0 * c11 - x1 * c01) / det;
            alpha2 = (c00 * x1 - c01 * x0) / det;
        }

        // A negative or vanishing length means the least-squares answer is degenerate: fall back to a third of the chord (the
        // Wu-Barsky heuristic), which gives a well-behaved curve the splitting step can then refine.
        var chord = (p3 - p0).Length;
        var epsilon = 1e-6 * chord;
        if (alpha1 < epsilon || alpha2 < epsilon || alpha1 > 3 * chord || alpha2 > 3 * chord) alpha1 = alpha2 = chord / 3;

        return new CubicBezier(p0, p0 + tangent1 * alpha1, p3 + tangent2 * alpha2, p3);
    }

    private static double[] ChordLengths(IReadOnlyList<Vec> d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (var i = 1; i < u.Length; i++) u[i] = u[i - 1] + (d[first + i] - d[first + i - 1]).Length;
        var total = u[^1];
        if (total <= 0)
        {
            for (var i = 0; i < u.Length; i++) u[i] = (double)i / (u.Length - 1);
            return u;
        }
        for (var i = 1; i < u.Length; i++) u[i] /= total;
        return u;
    }

    /// <summary>Largest squared distance between a point and the curve at that point's parameter, and where it is.</summary>
    private static (double Error, int Index) MaxError(IReadOnlyList<Vec> d, int first, int last, CubicBezier curve, double[] u)
    {
        var worst = 0.0;
        var index = (first + last) / 2;
        for (var i = 1; i < u.Length - 1; i++)
        {
            var e = (curve.At(u[i]) - d[first + i]).LengthSquared;
            if (e < worst) continue;
            worst = e;
            index = first + i;
        }
        return (worst, index);
    }

    /// <summary>One Newton-Raphson step per point towards the parameter of the nearest point on the curve.</summary>
    private static double[] Reparameterise(IReadOnlyList<Vec> d, int first, CubicBezier curve, double[] u)
    {
        var result = new double[u.Length];
        for (var i = 0; i < u.Length; i++)
        {
            var t = u[i];
            var diff = curve.At(t) - d[first + i];
            var d1 = curve.Derivative(t);
            var d2 = curve.SecondDerivative(t);
            var numerator = diff.Dot(d1);
            var denominator = d1.Dot(d1) + diff.Dot(d2);
            result[i] = Math.Abs(denominator) < 1e-12 ? t : Math.Clamp(t - numerator / denominator, 0, 1);
        }
        result[0] = 0;
        result[^1] = 1;
        return result;
    }
}
