// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>A 2-D point or direction in picture coordinates (y points down).</summary>
internal readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator -(Vec a) => new(-a.X, -a.Y);
    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s);
    public static Vec operator *(double s, Vec a) => new(a.X * s, a.Y * s);

    public double Dot(Vec other) => X * other.X + Y * other.Y;

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double LengthSquared => X * X + Y * Y;

    /// <summary>Unit vector in the same direction, or zero for a zero vector.</summary>
    public Vec Normalized()
    {
        var length = Length;
        return length < 1e-12 ? new Vec(0, 0) : new Vec(X / length, Y / length);
    }
}

/// <summary>A cubic Bézier segment from <see cref="P0"/> to <see cref="P3"/> with control points <see cref="P1"/> and <see cref="P2"/>.</summary>
internal readonly record struct CubicBezier(Vec P0, Vec P1, Vec P2, Vec P3)
{
    /// <summary>The point at parameter <paramref name="t"/> (0..1), from the Bernstein form.</summary>
    public Vec At(double t)
    {
        var s = 1 - t;
        return P0 * (s * s * s) + P1 * (3 * s * s * t) + P2 * (3 * s * t * t) + P3 * (t * t * t);
    }

    /// <summary>First derivative at <paramref name="t"/>.</summary>
    public Vec Derivative(double t)
    {
        var s = 1 - t;
        return (P1 - P0) * (3 * s * s) + (P2 - P1) * (6 * s * t) + (P3 - P2) * (3 * t * t);
    }

    /// <summary>Second derivative at <paramref name="t"/>.</summary>
    public Vec SecondDerivative(double t) => (P2 - P1 * 2 + P0) * (6 * (1 - t)) + (P3 - P2 * 2 + P1) * (6 * t);

    /// <summary>True when both control points lie within <paramref name="tolerance"/> of the straight line P0-P3 (and between its ends).</summary>
    public bool IsNearlyStraight(double tolerance)
    {
        var chord = P3 - P0;
        var length = chord.Length;
        if (length < 1e-9) return (P1 - P0).Length <= tolerance && (P2 - P0).Length <= tolerance;
        var unit = chord * (1 / length);
        foreach (var p in new[] { P1, P2 })
        {
            var rel = p - P0;
            var along = rel.Dot(unit);
            var across = Math.Abs(rel.X * unit.Y - rel.Y * unit.X);
            if (across > tolerance || along < -tolerance || along > length + tolerance) return false;
        }
        return true;
    }
}
