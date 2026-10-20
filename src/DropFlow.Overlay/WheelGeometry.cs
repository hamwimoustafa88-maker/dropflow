namespace DropFlow.Overlay;

/// <summary>
/// Pure maths of a radial wheel (no WPF types, so it is unit-tested). Angles are degrees measured clockwise from "up".
/// Wedge 0 is centred on "up"; the rest follow clockwise (counter-clockwise when <see cref="Mirrored"/>, used for right-to-left UIs
/// so the wheel reads in the same direction as the text).
/// </summary>
public sealed class WheelGeometry
{
    public WheelGeometry(int count, double outerRadius, double innerRadius, bool mirrored = false)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (innerRadius <= 0 || outerRadius <= innerRadius) throw new ArgumentException("Need 0 < inner < outer radius.");
        Count = count;
        OuterRadius = outerRadius;
        InnerRadius = innerRadius;
        Mirrored = mirrored;
    }

    public int Count { get; }
    public double OuterRadius { get; }
    public double InnerRadius { get; }
    public bool Mirrored { get; }

    /// <summary>Angular width of one wedge.</summary>
    public double Step => 360.0 / Count;

    /// <summary>Angle of the wedge's centre line, clockwise from up, in [0,360).</summary>
    public double CenterAngle(int index)
    {
        var a = index * Step;
        return Normalize(Mirrored ? -a : a);
    }

    /// <summary>Start and end angles of a wedge (start &lt; end unless it wraps through 0).</summary>
    public (double Start, double End) Span(int index, double gapDegrees = 0)
    {
        var c = CenterAngle(index);
        var half = Step / 2 - gapDegrees / 2;
        return (Normalize(c - half), Normalize(c + half));
    }

    /// <summary>Point on a circle of the given radius at an angle, relative to the wheel centre (x right, y down).</summary>
    public static (double X, double Y) OnCircle(double radius, double angleDegrees)
    {
        var rad = angleDegrees * Math.PI / 180;
        return (radius * Math.Sin(rad), -radius * Math.Cos(rad));
    }

    /// <summary>Where to draw the label of a wedge (centre of the ring segment).</summary>
    public (double X, double Y) LabelCenter(int index) => OnCircle((InnerRadius + OuterRadius) / 2, CenterAngle(index));

    /// <summary>
    /// Wedge under a point given relative to the wheel centre, or -1 when the point is in the hub or outside the wheel.
    /// <paramref name="outerSlack"/> lets a drag that overshoots the rim still select the nearest wedge.
    /// </summary>
    public int HitTest(double dx, double dy, double outerSlack = 0.15)
    {
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < InnerRadius || r > OuterRadius * (1 + outerSlack)) return -1;

        var angle = Normalize(Math.Atan2(dx, -dy) * 180 / Math.PI);
        if (Mirrored) angle = Normalize(-angle);
        return (int)Math.Floor((angle + Step / 2) / Step) % Count;
    }

    /// <summary>Next wedge in a direction (+1 clockwise on screen, -1 counter-clockwise), wrapping around.</summary>
    public int Move(int current, int direction)
    {
        if (current < 0) return direction >= 0 ? 0 : Count - 1;
        var screenStep = Mirrored ? -direction : direction;
        return ((current + screenStep) % Count + Count) % Count;
    }

    private static double Normalize(double degrees)
    {
        var a = degrees % 360;
        return a < 0 ? a + 360 : a;
    }
}
