// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace DropFlow.App.Views.Editors;

/// <summary>A rectangle in picture pixels.</summary>
internal readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>Whole pixels, never outside a width × height picture and at least 1×1.</summary>
    public (int X, int Y, int W, int H) ToPixels(double width, double height)
    {
        var x = (int)Math.Clamp(Math.Round(X), 0, Math.Max(0, width - 1));
        var y = (int)Math.Clamp(Math.Round(Y), 0, Math.Max(0, height - 1));
        var w = (int)Math.Clamp(Math.Round(W), 1, width - x);
        var h = (int)Math.Clamp(Math.Round(H), 1, height - y);
        return (x, y, w, h);
    }

    public Box Scaled(double factor) => new(X * factor, Y * factor, W * factor, H * factor);
}

internal enum BoxHandle { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Moving and resizing a selection box with the mouse: four corner handles, an optional locked aspect ratio, and the box always
/// stays inside the picture. Pure, so crop and redact share it and it is unit-tested.
/// </summary>
internal static class BoxGeometry
{
    /// <summary>"16:9" → 16/9; "free", "original" or anything unparsable → null (no lock).</summary>
    public static double? Ratio(string aspect)
    {
        var parts = aspect.Split(':');
        if (parts.Length != 2) return null;
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0
            ? a / b
            : null;
    }

    /// <summary>The largest centred box with the ratio (the whole picture when free).</summary>
    public static Box Initial(double width, double height, double? ratio)
    {
        if (ratio is not { } r) return new Box(0, 0, width, height);
        double w = width, h = width / r;
        if (h > height) { h = height; w = height * r; }
        return new Box((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>Re-shapes a box to a new ratio around its centre, as large as possible without growing past the old box's area, inside the picture.</summary>
    public static Box Fit(Box box, double? ratio, double width, double height)
    {
        if (ratio is not { } r) return Clamp(box, width, height);
        var area = Math.Max(1, box.W * box.H);
        var w = Math.Sqrt(area * r);
        var h = w / r;
        var cx = box.X + box.W / 2;
        var cy = box.Y + box.H / 2;
        if (w > width) { w = width; h = w / r; }
        if (h > height) { h = height; w = h * r; }
        return Clamp(new Box(cx - w / 2, cy - h / 2, w, h), width, height);
    }

    /// <summary>Keeps the size (shrinking only if larger than the picture) and slides the box back inside.</summary>
    public static Box Clamp(Box box, double width, double height)
    {
        var w = Math.Min(box.W, width);
        var h = Math.Min(box.H, height);
        return new Box(Math.Clamp(box.X, 0, width - w), Math.Clamp(box.Y, 0, height - h), w, h);
    }

    /// <summary>The handle under (x, y): a corner within <paramref name="grab"/> pixels, the inside (move), or none.</summary>
    public static BoxHandle HitTest(Box box, double x, double y, double grab)
    {
        bool Near(double px, double py) => Math.Abs(x - px) <= grab && Math.Abs(y - py) <= grab;
        if (Near(box.X, box.Y)) return BoxHandle.TopLeft;
        if (Near(box.Right, box.Y)) return BoxHandle.TopRight;
        if (Near(box.X, box.Bottom)) return BoxHandle.BottomLeft;
        if (Near(box.Right, box.Bottom)) return BoxHandle.BottomRight;
        return box.Contains(x, y) ? BoxHandle.Move : BoxHandle.None;
    }

    /// <summary>The box after dragging <paramref name="handle"/> by (dx, dy) from <paramref name="start"/>.</summary>
    public static Box Drag(Box start, BoxHandle handle, double dx, double dy, double? ratio, double width, double height, double minSize = 8)
    {
        if (handle == BoxHandle.Move)
            return Clamp(start with { X = start.X + dx, Y = start.Y + dy }, width, height);
        if (handle == BoxHandle.None) return start;

        // the corner opposite the handle stays put
        var left = handle is BoxHandle.TopLeft or BoxHandle.BottomLeft;
        var top = handle is BoxHandle.TopLeft or BoxHandle.TopRight;
        var ax = left ? start.Right : start.X;
        var ay = top ? start.Bottom : start.Y;
        var px = (left ? start.X : start.Right) + dx;
        var py = (top ? start.Y : start.Bottom) + dy;

        // room from the anchor to the picture edge in the dragging direction
        var roomX = left ? ax : width - ax;
        var roomY = top ? ay : height - ay;
        var w = Math.Clamp(left ? ax - px : px - ax, minSize, Math.Max(minSize, roomX));
        var h = Math.Clamp(top ? ay - py : py - ay, minSize, Math.Max(minSize, roomY));

        if (ratio is { } r)
        {
            // follow whichever side moved more, then fit both inside the room
            if (w / r >= h) h = w / r; else w = h * r;
            if (w > roomX) { w = roomX; h = w / r; }
            if (h > roomY) { h = roomY; w = h * r; }
        }
        return new Box(left ? ax - w : ax, top ? ay - h : ay, w, h);
    }

    /// <summary>A new box dragged out from (x0, y0) to (x1, y1), inside the picture.</summary>
    public static Box FromPoints(double x0, double y0, double x1, double y1, double width, double height)
    {
        x0 = Math.Clamp(x0, 0, width); x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height); y1 = Math.Clamp(y1, 0, height);
        return new Box(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }
}
