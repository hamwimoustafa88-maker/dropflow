// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.Engines.Media.Preview;
using DropFlow.Overlay;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>Which range handle the user is dragging.</summary>
internal enum RangeHandle { None, Start, End }

/// <summary>
/// The whole file as a strip (thumbnails for video, the waveform for audio) with the playhead, optional draggable range handles that dim
/// the parts outside the range, optional markers, an optional lane above the strip, and hooks so an editor can draw and take the pointer
/// (split cuts, bleep ranges). Clicking and dragging elsewhere scrubs. Time always runs left to right, also in Arabic.
/// </summary>
internal sealed class MediaTimelineView : FrameworkElement
{
    private const double Pad = 10;          // room for a handle at either end
    private const double HandleGrab = 9;    // pixels around a handle that grab it
    private const double TicksHeight = 16;

    private readonly MediaSession _media;
    private readonly WheelTheme _theme;
    private readonly Typeface _font = new("Segoe UI");
    private RangeHandle _dragging;
    private bool _scrubbing;
    private bool _hooked;
    private MediaSpan? _range;

    public MediaTimelineView(MediaSession media, WheelTheme theme, double stripHeight = 64, double laneHeight = 0)
    {
        _media = media;
        _theme = theme;
        StripHeight = stripHeight;
        LaneHeight = laneHeight;
        Height = laneHeight + stripHeight + TicksHeight;
        FlowDirection = FlowDirection.LeftToRight;
        Focusable = false;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        media.Changed += InvalidateVisual;
        media.StripChanged += InvalidateVisual;
    }

    public double StripHeight { get; }
    public double LaneHeight { get; }

    /// <summary>The selected range with handles; null hides the handles and the dimming.</summary>
    public MediaSpan? Range
    {
        get => _range;
        set { _range = value; InvalidateVisual(); }
    }

    /// <summary>Small ticks over the strip (saved frames, for example).</summary>
    public IReadOnlyList<double> Markers { get; set; } = [];

    /// <summary>A handle moved: the editor clamps and stores it, then sets <see cref="Range"/> back.</summary>
    public event Action<RangeHandle, double>? RangeDragged;

    /// <summary>Drawn over the strip and lane before the playhead. Use <see cref="XAt"/>, <see cref="StripRect"/>, <see cref="LaneRect"/>.</summary>
    public Action<DrawingContext>? Overlay { get; set; }

    /// <summary>Pointer pressed at a time; return true to take the drag (then <see cref="Dragged"/> and <see cref="Released"/> follow).</summary>
    public Func<double, Point, MouseButtonEventArgs, bool>? Pressed { get; set; }
    public Action<double, Point>? Dragged { get; set; }
    public Action<double, Point>? Released { get; set; }

    public Rect LaneRect => new(Pad, 0, Math.Max(1, ActualWidth - 2 * Pad), LaneHeight);
    public Rect StripRect => new(Pad, LaneHeight, Math.Max(1, ActualWidth - 2 * Pad), StripHeight);

    public double XAt(double seconds) => Pad + Math.Clamp(seconds / _media.Duration, 0, 1) * Math.Max(1, ActualWidth - 2 * Pad);

    public double TimeAt(double x) => Math.Clamp((x - Pad) / Math.Max(1, ActualWidth - 2 * Pad), 0, 1) * _media.Duration;

    /// <summary>Seconds covered by <paramref name="pixels"/> on screen (for grab tolerances).</summary>
    public double SecondsPer(double pixels) => pixels / Math.Max(1, ActualWidth - 2 * Pad) * _media.Duration;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        var strip = StripRect;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable everywhere

        if (LaneHeight > 0) dc.DrawRoundedRectangle(_theme.Wedge, null, LaneRect, 6, 6);

        dc.PushClip(new RectangleGeometry(strip, 8, 8));
        dc.DrawRectangle(_theme.IsDark ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x3A)), null, strip);
        if (_media.HasVideo) DrawThumbnails(dc, strip);
        else DrawWave(dc, strip, _media.Waveform, WaveColor);
        dc.Pop();

        if (_range is { } r)
        {
            var dim = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
            var x1 = XAt(r.Start);
            var x2 = XAt(r.End);
            if (x1 > strip.Left) dc.DrawRectangle(dim, null, new Rect(strip.Left, strip.Top, x1 - strip.Left, strip.Height));
            if (x2 < strip.Right) dc.DrawRectangle(dim, null, new Rect(x2, strip.Top, strip.Right - x2, strip.Height));
            var edge = new Pen(_theme.Highlight, 2);
            dc.DrawRectangle(null, edge, new Rect(x1, strip.Top + 1, Math.Max(0, x2 - x1), strip.Height - 2));
            DrawHandle(dc, x1, strip);
            DrawHandle(dc, x2, strip);
        }

        foreach (var m in Markers)
        {
            var x = XAt(m);
            dc.DrawRectangle(_theme.Highlight, null, new Rect(x - 1, strip.Top, 2, strip.Height));
            dc.DrawEllipse(_theme.Highlight, null, new Point(x, strip.Top + 4), 4, 4);
        }

        Overlay?.Invoke(dc);
        DrawTicks(dc, strip);

        // playhead
        var px = XAt(_media.Current);
        var head = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), null, new Rect(px - 1.5, 0, 3, strip.Bottom));
        dc.DrawRectangle(head, null, new Rect(px - 0.75, 0, 1.5, strip.Bottom));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(px - 6, 0), true, true);
            g.LineTo(new Point(px + 6, 0), true, false);
            g.LineTo(new Point(px, 7), true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(head, new Pen(Brushes.Black, 0.5), tri);
    }

    private void DrawHandle(DrawingContext dc, double x, Rect strip)
    {
        var handle = new Rect(x - 5, strip.Top - 2, 10, strip.Height + 4);
        dc.DrawRoundedRectangle(_theme.Highlight, new Pen(Brushes.White, 1), handle, 3, 3);
        var grip = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), 1);
        for (var i = -1; i <= 1; i++) dc.DrawLine(grip, new Point(x - 1.5, strip.Top + strip.Height / 2 + i * 4), new Point(x + 1.5, strip.Top + strip.Height / 2 + i * 4));
    }

    private void DrawThumbnails(DrawingContext dc, Rect strip)
    {
        var thumbs = _media.Thumbnails;
        if (thumbs.Length == 0) return;
        var slot = strip.Width / thumbs.Length;
        for (var i = 0; i < thumbs.Length; i++)
        {
            var cell = new Rect(strip.Left + i * slot, strip.Top, slot, strip.Height);
            if (thumbs[i] is not BitmapSource image) continue;
            var scale = Math.Max(slot / image.PixelWidth, strip.Height / image.PixelHeight);
            var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
            dc.PushClip(new RectangleGeometry(cell));
            dc.DrawImage(image, new Rect(cell.Left + (slot - size.Width) / 2, cell.Top + (strip.Height - size.Height) / 2, size.Width, size.Height));
            dc.Pop();
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null, new Rect(cell.Right - 1, cell.Top, 1, cell.Height));
        }
    }

    /// <summary>The light indigo of DropFlow's dark theme: it reads well on the dark strip in both themes.</summary>
    public static readonly Color WaveColor = Color.FromRgb(0x8B, 0x95, 0xFB);

    /// <summary>Mirrored bars, scaled so the loudest bar nearly fills the strip (quiet recordings are still visible).</summary>
    public static void DrawWave(DrawingContext dc, Rect rect, float[] peaks, Color color)
    {
        if (peaks.Length == 0) return;
        var max = Math.Max(0.02f, peaks.Max());
        var brush = new SolidColorBrush(Color.FromArgb(0xE6, color.R, color.G, color.B));
        brush.Freeze();
        var step = rect.Width / peaks.Length;
        var mid = rect.Top + rect.Height / 2;
        for (var i = 0; i < peaks.Length; i++)
        {
            var h = Math.Max(1.2, peaks[i] / max * rect.Height * 0.92);
            dc.DrawRectangle(brush, null, new Rect(rect.Left + i * step, mid - h / 2, Math.Max(0.8, step * 0.7), h));
        }
    }

    private static readonly double[] TickSteps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    /// <summary>A round tick step (1 s, 5 s, 1 min...) that leaves at least <paramref name="minPixels"/> between labels.</summary>
    internal static double TickStep(double duration, double width, double minPixels = 90)
    {
        foreach (var step in TickSteps)
            if (step / Math.Max(duration, 0.001) * width >= minPixels) return step;
        return TickSteps[^1];
    }

    private void DrawTicks(DrawingContext dc, Rect strip)
    {
        var muted = _theme.TextMuted;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var step = TickStep(_media.Duration, strip.Width);
        for (var t = 0.0; t <= _media.Duration + 1e-6; t += step)
        {
            var x = XAt(t);
            dc.DrawRectangle(muted, null, new Rect(x - 0.5, strip.Bottom + 1, 1, 4));
            var text = new FormattedText(MediaTimeText.Length(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _font, 10, muted, dpi);
            var tx = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            dc.DrawText(text, new Point(tx, strip.Bottom + 4));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ pointer

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var t = TimeAt(p.X);
        _dragging = HandleAt(p);
        _hooked = false;
        _scrubbing = false;
        if (_dragging == RangeHandle.None)
        {
            if (Pressed?.Invoke(t, p, e) == true) _hooked = true;
            else { _scrubbing = true; _media.Seek(t); }
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            Cursor = HandleAt(p) != RangeHandle.None ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        var t = TimeAt(p.X);
        if (_dragging != RangeHandle.None) { RangeDragged?.Invoke(_dragging, t); _media.Seek(t); }
        else if (_hooked) Dragged?.Invoke(t, p);
        else if (_scrubbing) _media.Seek(t);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (_hooked) Released?.Invoke(TimeAt(p.X), p);
        _dragging = RangeHandle.None;
        _hooked = false;
        _scrubbing = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    private RangeHandle HandleAt(Point p)
    {
        if (_range is not { } r || p.Y < LaneHeight - 2) return RangeHandle.None;
        var ds = Math.Abs(p.X - XAt(r.Start));
        var de = Math.Abs(p.X - XAt(r.End));
        if (ds > HandleGrab && de > HandleGrab) return RangeHandle.None;
        return ds <= de ? RangeHandle.Start : RangeHandle.End;
    }
}
