// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DropFlow.App.Views.Editors;

/// <summary>Draws selection boxes on a <see cref="PreviewHost"/> overlay (coordinates already in preview pixels).</summary>
internal static class BoxOverlay
{
    /// <summary>Screen size of a corner handle, in device-independent pixels.</summary>
    public const double HandleSize = 12;

    /// <summary>Darkens everything outside <paramref name="box"/>.</summary>
    public static void Shade(Canvas canvas, Box box, double width, double height)
    {
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)),
            new RectangleGeometry(new Rect(box.X, box.Y, box.W, box.H)));
        canvas.Children.Add(new Path { Data = outside, Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), IsHitTestVisible = false });
    }

    /// <summary>The frame, optional rule-of-thirds lines and the four corner handles, all keeping their screen size.</summary>
    public static void Frame(Canvas canvas, Box box, double displayScale, Brush accent, bool thirds, bool handles)
    {
        var px = 1 / Math.Max(0.01, displayScale);
        var frame = new Rectangle { Width = Math.Max(0, box.W), Height = Math.Max(0, box.H), Stroke = Brushes.White, StrokeThickness = 1.5 * px, IsHitTestVisible = false };
        Canvas.SetLeft(frame, box.X);
        Canvas.SetTop(frame, box.Y);
        canvas.Children.Add(frame);
        var outline = new Rectangle { Width = Math.Max(0, box.W + 2 * px), Height = Math.Max(0, box.H + 2 * px), Stroke = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), StrokeThickness = px, IsHitTestVisible = false };
        Canvas.SetLeft(outline, box.X - px);
        Canvas.SetTop(outline, box.Y - px);
        canvas.Children.Add(outline);

        if (thirds)
        {
            for (var i = 1; i <= 2; i++)
            {
                canvas.Children.Add(Line(box.X + box.W * i / 3, box.Y, box.X + box.W * i / 3, box.Bottom, px));
                canvas.Children.Add(Line(box.X, box.Y + box.H * i / 3, box.Right, box.Y + box.H * i / 3, px));
            }
        }

        if (!handles) return;
        var size = HandleSize * px;
        foreach (var (x, y) in new[] { (box.X, box.Y), (box.Right, box.Y), (box.X, box.Bottom), (box.Right, box.Bottom) })
        {
            var h = new Rectangle { Width = size, Height = size, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1.5 * px, RadiusX = 2 * px, RadiusY = 2 * px, IsHitTestVisible = false };
            Canvas.SetLeft(h, x - size / 2);
            Canvas.SetTop(h, y - size / 2);
            canvas.Children.Add(h);
        }
    }

    private static Line Line(double x1, double y1, double x2, double y2, double px) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), StrokeThickness = px, IsHitTestVisible = false,
    };
}
