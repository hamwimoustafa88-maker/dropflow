// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Engines.Images;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// One annotation as the editor holds it, in preview pixels. Rect, ellipse, highlight, image and text use (X,Y)-(X2,Y2) as opposite
/// corners; line and arrow go from (X,Y) to (X2,Y2); number is centred on (X,Y); magnify shows the circle of radius Size around (X,Y)
/// enlarged Zoom times, centred on (X2,Y2); pen uses Points.
/// </summary>
internal sealed record AnnotationShape(
    string Type, double X, double Y, double X2, double Y2, string Color, double Stroke,
    bool Fill = false, string? Text = null, double Size = 0, int N = 0, double[]? Points = null, string? ImagePath = null, double Zoom = 2)
{
    public double Left => Math.Min(X, X2);
    public double Top => Math.Min(Y, Y2);
    public double Width => Math.Abs(X2 - X);
    public double Height => Math.Abs(Y2 - Y);

    public AnnotationShape Moved(double dx, double dy) => this with
    {
        X = X + dx, Y = Y + dy, X2 = Type == "magnify" ? X2 : X2 + dx, Y2 = Type == "magnify" ? Y2 : Y2 + dy,
        Points = Points?.Select((v, i) => v + (i % 2 == 0 ? dx : dy)).ToArray(),
    };

    /// <summary>Radius of a numbered callout, the same rule as the engine (<c>max(size, 16) / 1.6</c>, 16 measured in full-size pixels).</summary>
    public double NumberRadius(double scale) => Math.Max(Size, 16 * scale) / 1.6;
}

/// <summary>
/// The list of annotations with undo, hit-testing and export to the <c>img.annotate</c> items (relative units, so the same list fits
/// every picture of a batch). Pure, so it is unit-tested; the window only draws it and turns text into picture overlays.
/// </summary>
internal sealed class AnnotationDocument(double width, double height)
{
    private readonly List<AnnotationShape> _shapes = [];
    private readonly Stack<AnnotationShape[]> _undo = new();

    public double Width { get; } = width;
    public double Height { get; } = height;
    public double LongSide => Math.Max(Width, Height);

    public IReadOnlyList<AnnotationShape> Shapes => _shapes;

    public bool CanUndo => _undo.Count > 0;

    public int NextNumber => _shapes.Where(s => s.Type == "number").Select(s => s.N).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Remembers the current state so the next change can be undone (call once per user action).</summary>
    public void Checkpoint() => _undo.Push([.. _shapes]);

    public int Add(AnnotationShape shape)
    {
        Checkpoint();
        _shapes.Add(shape);
        return _shapes.Count - 1;
    }

    /// <summary>Replaces a shape without a new undo step (used while dragging; call <see cref="Checkpoint"/> when the drag starts).</summary>
    public void Replace(int index, AnnotationShape shape) => _shapes[index] = shape;

    public void Remove(int index)
    {
        if (index < 0 || index >= _shapes.Count) return;
        Checkpoint();
        _shapes.RemoveAt(index);
    }

    public void Clear()
    {
        if (_shapes.Count == 0) return;
        Checkpoint();
        _shapes.Clear();
    }

    /// <summary>Drops the last undo step without restoring it (a drag that turned out to be a click).</summary>
    public void DiscardCheckpoint()
    {
        if (_undo.Count > 0) _undo.Pop();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _shapes.Clear();
        _shapes.AddRange(_undo.Pop());
        return true;
    }

    /// <summary>The topmost shape under (x, y), or -1. <paramref name="tolerance"/> is in preview pixels.</summary>
    public int HitTest(double x, double y, double tolerance, double scale = 1)
    {
        for (var i = _shapes.Count - 1; i >= 0; i--)
            if (Hits(_shapes[i], x, y, tolerance, scale)) return i;
        return -1;
    }

    private static bool Hits(AnnotationShape s, double x, double y, double tol, double scale)
    {
        switch (s.Type)
        {
            case "line" or "arrow":
                return DistanceToSegment(x, y, s.X, s.Y, s.X2, s.Y2) <= tol + s.Stroke / 2;
            case "pen" when s.Points is { Length: >= 4 } p:
                for (var i = 0; i + 3 < p.Length; i += 2)
                    if (DistanceToSegment(x, y, p[i], p[i + 1], p[i + 2], p[i + 3]) <= tol + s.Stroke / 2) return true;
                return false;
            case "number":
                return Math.Sqrt((x - s.X) * (x - s.X) + (y - s.Y) * (y - s.Y)) <= s.NumberRadius(scale) + tol;
            case "magnify":
            {
                var lens = s.Size * s.Zoom;
                return Math.Sqrt((x - s.X2) * (x - s.X2) + (y - s.Y2) * (y - s.Y2)) <= lens + tol;
            }
            default:
                return x >= s.Left - tol && x <= s.Left + s.Width + tol && y >= s.Top - tol && y <= s.Top + s.Height + tol;
        }
    }

    public static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        var cx = ax + t * dx;
        var cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    /// <summary>
    /// The converter's items in relative units. Text becomes a picture overlay: <paramref name="renderText"/> writes a PNG for the shape
    /// and returns its path (the editor renders text with Windows' own typesetting so Arabic is shaped and ordered correctly).
    /// </summary>
    public IReadOnlyList<AnnotationItem> ToItems(Func<AnnotationShape, string> renderText)
    {
        double W = Width, H = Height, L = LongSide;
        var items = new List<AnnotationItem>();
        foreach (var s in _shapes)
        {
            switch (s.Type)
            {
                case "rect" or "ellipse" or "highlight":
                    items.Add(new AnnotationItem(s.Type, s.Left / W, s.Top / H, s.Width / W, s.Height / H, Color: s.Color, Stroke: s.Stroke / L, Fill: s.Fill));
                    break;
                case "line" or "arrow":
                    items.Add(new AnnotationItem(s.Type, s.X / W, s.Y / H, X2: s.X2 / W, Y2: s.Y2 / H, Color: s.Color, Stroke: s.Stroke / L));
                    break;
                case "pen" when s.Points is { Length: >= 4 } p:
                    items.Add(new AnnotationItem("pen", Color: s.Color, Stroke: s.Stroke / L, Points: [.. p.Select((v, i) => i % 2 == 0 ? v / W : v / H)]));
                    break;
                case "number":
                    items.Add(new AnnotationItem("number", s.X / W, s.Y / H, Color: s.Color, Size: s.Size / L, N: s.N));
                    break;
                case "magnify":
                    items.Add(new AnnotationItem("magnify", s.X / W, s.Y / H, s.Size / W, X2: s.X2 / W, Y2: s.Y2 / H, Color: s.Color, Stroke: s.Stroke / L, Zoom: s.Zoom));
                    break;
                case "image" when s.ImagePath is not null:
                    items.Add(new AnnotationItem("image", s.Left / W, s.Top / H, s.Width / W, s.Height / H, Path: s.ImagePath));
                    break;
                case "text" when !string.IsNullOrWhiteSpace(s.Text):
                    items.Add(new AnnotationItem("image", s.Left / W, s.Top / H, s.Width / W, s.Height / H, Path: renderText(s)));
                    break;
            }
        }
        return items;
    }

    /// <summary>True when the text holds right-to-left letters (Arabic, Hebrew…), so it is typeset right to left.</summary>
    public static bool IsRightToLeft(string? text) =>
        text is not null && text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿');
}
