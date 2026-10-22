using System.Globalization;
using System.Text;

namespace DropFlow.Engines.Archive.Images;

/// <summary>An 8-bit colour with alpha, parsed from the "#rrggbb" / "#rrggbbaa" strings of <see cref="QrStyle"/>.</summary>
internal readonly record struct QrRgba(byte R, byte G, byte B, byte A)
{
    public static readonly QrRgba White = new(255, 255, 255, 255);
    public static readonly QrRgba Transparent = new(0, 0, 0, 0);

    public bool IsOpaque => A == 255;

    /// <summary>"#rrggbb" (alpha goes in a separate opacity attribute: SVG 1.1 has no 8-digit colours).</summary>
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public string Opacity => QrPath.Num(A / 255.0);

    /// <exception cref="ArgumentException">Not a colour.</exception>
    public static QrRgba Parse(string? value, string what)
    {
        var text = (value ?? "").Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return Transparent;
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length is 3 or 4) text = string.Concat(text.Select(c => new string(c, 2)));
        if (text.Length is not (6 or 8) || !text.All(char.IsAsciiHexDigit))
            throw new ArgumentException($"'{value}' is not a colour ({what}). Use #rrggbb, #rrggbbaa or transparent.", what);

        byte At(int i) => byte.Parse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new QrRgba(At(0), At(2), At(4), text.Length == 8 ? At(6) : (byte)255);
    }

    public static QrRgba Lerp(QrRgba a, QrRgba b, double t)
    {
        static byte Mix(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return new QrRgba(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t), Mix(a.A, b.A, t));
    }
}

/// <summary>An axis-aligned box in module units.</summary>
internal readonly record struct QrBox(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    public QrBox Inflate(double by) => new(X - by, Y - by, W + by * 2, H + by * 2);

    public bool Intersects(QrBox other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

internal enum QrPathOp { Move, Line, Arc, Close }

/// <summary>One step of a path. <see cref="QrPathOp.Arc"/> is a clockwise circular arc of at most a quarter turn, radius <see cref="Radius"/>, ending at (X, Y).</summary>
internal readonly record struct QrPathCommand(QrPathOp Op, double X = 0, double Y = 0, double Radius = 0);

/// <summary>
/// Closed outlines in module units, built from three primitives: rectangles with four separate corner radii, circles and polygons.
/// Every outline runs clockwise (on screen, y down), so a non-zero fill unites overlapping pieces and an even-odd fill turns nested
/// outlines into rings. The SVG writer and the PNG renderer both draw these exact commands.
/// </summary>
internal sealed class QrPath
{
    private readonly List<QrPathCommand> _commands = [];
    private (double X, double Y) _at;

    public IReadOnlyList<QrPathCommand> Commands => _commands;
    public bool IsEmpty => _commands.Count == 0;

    public void Rect(double x, double y, double w, double h) => RoundRect(x, y, w, h, 0, 0, 0, 0);

    public void RoundRect(double x, double y, double w, double h, double r) => RoundRect(x, y, w, h, r, r, r, r);

    /// <summary>Corner radii: top left, top right, bottom right, bottom left (each limited to half the shorter side).</summary>
    public void RoundRect(double x, double y, double w, double h, double tl, double tr, double br, double bl)
    {
        if (w <= 0 || h <= 0) return;
        var limit = Math.Min(w, h) / 2;
        tl = Math.Clamp(tl, 0, limit);
        tr = Math.Clamp(tr, 0, limit);
        br = Math.Clamp(br, 0, limit);
        bl = Math.Clamp(bl, 0, limit);

        Move(x + tl, y);
        Line(x + w - tr, y);
        Arc(tr, x + w, y + tr);
        Line(x + w, y + h - br);
        Arc(br, x + w - br, y + h);
        Line(x + bl, y + h);
        Arc(bl, x, y + h - bl);
        Line(x, y + tl);
        Arc(tl, x + tl, y);
        Close();
    }

    public void Circle(double cx, double cy, double r)
    {
        if (r <= 0) return;
        Move(cx, cy - r);
        Arc(r, cx + r, cy);
        Arc(r, cx, cy + r);
        Arc(r, cx - r, cy);
        Arc(r, cx, cy - r);
        Close();
    }

    /// <summary>Points in clockwise order.</summary>
    public void Polygon(params ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 3) return;
        Move(points[0].X, points[0].Y);
        foreach (var (x, y) in points[1..]) Line(x, y);
        Close();
    }

    private void Move(double x, double y)
    {
        _commands.Add(new QrPathCommand(QrPathOp.Move, x, y));
        _at = (x, y);
    }

    private void Line(double x, double y)
    {
        if (Same(_at, (x, y))) return;
        _commands.Add(new QrPathCommand(QrPathOp.Line, x, y));
        _at = (x, y);
    }

    private void Arc(double r, double x, double y)
    {
        if (r <= 0 || Same(_at, (x, y))) return;
        _commands.Add(new QrPathCommand(QrPathOp.Arc, x, y, r));
        _at = (x, y);
    }

    private void Close() => _commands.Add(new QrPathCommand(QrPathOp.Close));

    private static bool Same((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    /// <summary>SVG path data ("M x y L x y A r r 0 0 1 x y Z").</summary>
    public string ToSvgData()
    {
        var d = new StringBuilder(_commands.Count * 12);
        foreach (var c in _commands)
        {
            switch (c.Op)
            {
                case QrPathOp.Move: d.Append('M').Append(Num(c.X)).Append(' ').Append(Num(c.Y)); break;
                case QrPathOp.Line: d.Append('L').Append(Num(c.X)).Append(' ').Append(Num(c.Y)); break;
                case QrPathOp.Arc: d.Append('A').Append(Num(c.Radius)).Append(' ').Append(Num(c.Radius)).Append(" 0 0 1 ").Append(Num(c.X)).Append(' ').Append(Num(c.Y)); break;
                default: d.Append('Z'); break;
            }
        }
        return d.ToString();
    }

    public static string Num(double value)
    {
        var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}

/// <summary>A solid colour, or a linear gradient from (X1, Y1) to (X2, Y2) in module units (padded beyond both ends).</summary>
internal sealed record QrPaint(QrRgba From, QrRgba? To = null, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0)
{
    public bool IsGradient => To is not null;

    public QrRgba At(double x, double y)
    {
        if (To is not { } to) return From;
        double dx = X2 - X1, dy = Y2 - Y1;
        var length = dx * dx + dy * dy;
        var t = length <= 0 ? 0 : Math.Clamp(((x - X1) * dx + (y - Y1) * dy) / length, 0, 1);
        return QrRgba.Lerp(From, to, t);
    }
}

internal sealed record QrLayer(QrPath Path, bool EvenOdd, QrPaint Paint);

/// <summary>A whole code laid out in module units: the quiet zone is included, so (0, 0) is the picture's top-left corner.</summary>
internal sealed class QrLayout
{
    private const double PlatePadding = 0.6;

    public required double Units { get; init; }
    public required QrRgba Background { get; init; }
    /// <summary>Drawn in order: modules, eyes, then the logo plate.</summary>
    public required IReadOnlyList<QrLayer> Layers { get; init; }
    /// <summary>Where the logo goes, or null without a logo.</summary>
    public QrBox? LogoBox { get; init; }

    /// <summary>The logo fitted inside <see cref="LogoBox"/> with its proportions kept, centred.</summary>
    public QrBox FitLogo(double width, double height)
    {
        var box = LogoBox ?? throw new InvalidOperationException("This layout has no logo.");
        var aspect = width / Math.Max(1, height);
        var (w, h) = aspect >= 1 ? (box.W, box.W / aspect) : (box.H * aspect, box.H);
        return new QrBox(box.X + (box.W - w) / 2, box.Y + (box.H - h) / 2, w, h);
    }

    public static bool IsFinder(int n, int row, int col) =>
        (row < 8 && col < 8) || (row < 8 && col >= n - 8) || (row >= n - 8 && col < 8);

    public static QrLayout Build(bool[,] matrix, QrStyle style, int quiet, QrRgba foreground, QrRgba? foreground2, QrRgba? eyeColor, QrRgba background)
    {
        var n = matrix.GetLength(0);
        var units = n + quiet * 2.0;

        QrBox? logo = null, clear = null;
        QrPath? plate = null;
        if (style.HasLogo)
        {
            var scale = double.IsFinite(style.LogoScale) ? Math.Clamp(style.LogoScale, 0.1, 0.3) : 0.22;
            var side = n * scale;
            logo = new QrBox(quiet + (n - side) / 2, quiet + (n - side) / 2, side, side);
            clear = logo;
            if (style.LogoPlate)
            {
                var box = logo.Value.Inflate(PlatePadding);
                clear = box;
                plate = new QrPath();
                plate.RoundRect(box.X, box.Y, box.W, box.H, box.W * 0.2);
            }
        }

        bool Keep(int row, int col)
        {
            if (row < 0 || col < 0 || row >= n || col >= n || !matrix[row, col] || IsFinder(n, row, col)) return false;
            return clear is not { } c || !c.Intersects(new QrBox(col + quiet + 0.1, row + quiet + 0.1, 0.8, 0.8));
        }

        var modules = style.Modules == QrModuleShape.Bars ? BarModules(n, quiet, Keep) : CellModules(n, quiet, style.Modules, Keep);
        var eyes = Eyes(n, quiet, style.Eyes);

        var modulePaint = foreground2 is { } end ? Gradient(foreground, end, style.GradientAngle, quiet, n) : new QrPaint(foreground);
        var eyePaint = eyeColor is { } eye ? new QrPaint(eye) : modulePaint;

        var layers = new List<QrLayer> { new(modules, false, modulePaint), new(eyes, true, eyePaint) };
        if (plate is not null) layers.Add(new QrLayer(plate, false, new QrPaint(background.A == 0 ? QrRgba.White : background)));

        return new QrLayout { Units = units, Background = background, Layers = layers, LogoBox = logo };
    }

    private static QrPaint Gradient(QrRgba from, QrRgba to, double angleDegrees, int quiet, int n)
    {
        var angle = (double.IsFinite(angleDegrees) ? angleDegrees : 45) * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var centre = quiet + n / 2.0;
        var half = n / 2.0 * (Math.Abs(cos) + Math.Abs(sin));   // reaches the far corners of the code whatever the angle
        return new QrPaint(from, to, centre - cos * half, centre - sin * half, centre + cos * half, centre + sin * half);
    }

    private static QrPath CellModules(int n, int quiet, QrModuleShape shape, Func<int, int, bool> keep)
    {
        var path = new QrPath();
        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
            {
                if (!keep(row, col)) continue;
                double x = col + quiet, y = row + quiet;
                switch (shape)
                {
                    case QrModuleShape.Rounded:
                        path.RoundRect(x + 0.04, y + 0.04, 0.92, 0.92, 0.3);
                        break;
                    case QrModuleShape.Dots:
                        path.Circle(x + 0.5, y + 0.5, 0.42);
                        break;
                    case QrModuleShape.Diamond:
                        path.Polygon((x + 0.5, y), (x + 1, y + 0.5), (x + 0.5, y + 1), (x, y + 0.5));
                        break;
                    case QrModuleShape.Fluid:
                    {
                        // A corner is rounded only when neither side next to it touches another module, so neighbours merge into blobs.
                        bool up = keep(row - 1, col), down = keep(row + 1, col), left = keep(row, col - 1), right = keep(row, col + 1);
                        const double r = 0.5;
                        path.RoundRect(x, y, 1, 1, !up && !left ? r : 0, !up && !right ? r : 0, !down && !right ? r : 0, !down && !left ? r : 0);
                        break;
                    }
                    default:
                        path.Rect(x, y, 1, 1);
                        break;
                }
            }
        }
        return path;
    }

    /// <summary>Each vertical run of modules becomes one capsule.</summary>
    private static QrPath BarModules(int n, int quiet, Func<int, int, bool> keep)
    {
        var path = new QrPath();
        for (var col = 0; col < n; col++)
        {
            var row = 0;
            while (row < n)
            {
                if (!keep(row, col)) { row++; continue; }
                var start = row;
                while (row < n && keep(row, col)) row++;
                path.RoundRect(col + quiet + 0.1, start + quiet + 0.05, 0.8, row - start - 0.1, 0.4);
            }
        }
        return path;
    }

    /// <summary>The three finder patterns: a 7x7 frame one module thick around a 3x3 ball, as nested outlines for an even-odd fill.</summary>
    private static QrPath Eyes(int n, int quiet, QrEyeShape shape)
    {
        var path = new QrPath();
        (int Row, int Col, bool TopLeft)[] origins = [(0, 0, true), (0, n - 7, false), (n - 7, 0, false)];
        foreach (var (row, col, topLeft) in origins)
        {
            double x = col + quiet, y = row + quiet;
            switch (shape)
            {
                case QrEyeShape.Rounded:
                    path.RoundRect(x, y, 7, 7, 2);
                    path.RoundRect(x + 1, y + 1, 5, 5, 1);
                    path.RoundRect(x + 2, y + 2, 3, 3, 0.9);
                    break;
                case QrEyeShape.Circle:
                    path.Circle(x + 3.5, y + 3.5, 3.5);
                    path.Circle(x + 3.5, y + 3.5, 2.5);
                    path.Circle(x + 3.5, y + 3.5, 1.5);
                    break;
                case QrEyeShape.Leaf:
                    // The top-left eye rounds its top-left and bottom-right corners; the other two are its mirror images.
                    Leaf(path, x, y, 7, 3, topLeft);
                    Leaf(path, x + 1, y + 1, 5, 2, topLeft);
                    Leaf(path, x + 2, y + 2, 3, 1.2, topLeft);
                    break;
                default:
                    path.Rect(x, y, 7, 7);
                    path.Rect(x + 1, y + 1, 5, 5);
                    path.Rect(x + 2, y + 2, 3, 3);
                    break;
            }
        }
        return path;
    }

    private static void Leaf(QrPath path, double x, double y, double side, double r, bool mainDiagonal)
    {
        if (mainDiagonal) path.RoundRect(x, y, side, side, r, 0, r, 0);
        else path.RoundRect(x, y, side, side, 0, r, 0, r);
    }
}
