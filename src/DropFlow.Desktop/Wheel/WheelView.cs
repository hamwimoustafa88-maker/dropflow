// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DropFlow.Desktop.Views;
using DropFlow.Overlay;

namespace DropFlow.Desktop.Wheel;

/// <summary>
/// The radial wheel drawn with Avalonia: wedges from <see cref="WheelGeometry"/> (shared with the Windows overlay), a hub with the
/// wheel's name, file count and a progress ring. Hover, click, arrow keys, Enter and Esc work; during a drag the window feeds the
/// pointer through <see cref="SetPointer"/>.
/// </summary>
public sealed class WheelView : Control
{
    public const double Outer = 170;
    public const double Inner = 64;
    private const double Padding = 16;
    private const double GapDegrees = 3.2;
    private const double LabelMaxHeight = 38;

    private WheelGeometry? _geometry;
    private Palette _palette = Palette.Current;
    private Typeface _face = new(Palette.Font, FontStyle.Normal, FontWeight.SemiBold);

    public WheelView()
    {
        Focusable = true;
        Width = Height = (Outer + Padding) * 2;
        ClipToBounds = false;
    }

    public IReadOnlyList<WheelItem> Items { get; private set; } = [];
    public int HoverIndex { get; private set; } = -1;
    public string CenterText { get; private set; } = "";
    public string CenterHint { get; private set; } = "";
    public double? Progress { get; private set; }
    public bool IsMirrored { get; private set; }

    public Palette Palette
    {
        get => _palette;
        set { _palette = value; InvalidateVisual(); }
    }

    public event EventHandler<WheelItemEventArgs>? ItemInvoked;
    public event EventHandler<WheelItemEventArgs>? ItemContextRequested;
    public event EventHandler? Cancelled;

    public void SetItems(IReadOnlyList<WheelItem> items, bool mirrored, int selected = -1)
    {
        Items = items;
        IsMirrored = mirrored;
        _geometry = items.Count == 0 ? null : new WheelGeometry(items.Count, Outer, Inner, mirrored);
        HoverIndex = selected >= 0 && selected < items.Count ? selected : -1;
        InvalidateVisual();
    }

    public void SetCenter(string text, string hint = "")
    {
        CenterText = text;
        CenterHint = hint;
        InvalidateVisual();
    }

    public void SetProgress(double? progress)
    {
        Progress = progress;
        InvalidateVisual();
    }

    /// <summary>Pointer position relative to this control; returns the wedge under it (or -1).</summary>
    public int SetPointer(Point position)
    {
        var idx = WedgeAt(position);
        if (idx != HoverIndex)
        {
            HoverIndex = idx;
            InvalidateVisual();
        }
        return idx;
    }

    public void ClearHover()
    {
        if (HoverIndex == -1) return;
        HoverIndex = -1;
        InvalidateVisual();
    }

    public int WedgeAt(Point position) =>
        _geometry?.HitTest(position.X - Bounds.Width / 2, position.Y - Bounds.Height / 2) ?? -1;

    public void Invoke(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        ItemInvoked?.Invoke(this, new WheelItemEventArgs(index, Items[index]));
    }

    // ------------------------------------------------------------------------------------------------------------------- input

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetPointer(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHover();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var index = WedgeAt(e.GetPosition(this));
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            if (index >= 0) ItemContextRequested?.Invoke(this, new WheelItemEventArgs(index, Items[index]));
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            Invoke(index);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (_geometry is null) return;
        switch (e.Key)
        {
            case Key.Right or Key.Down: Select(_geometry.Move(HoverIndex, +1)); e.Handled = true; break;
            case Key.Left or Key.Up: Select(_geometry.Move(HoverIndex, -1)); e.Handled = true; break;
            case Key.Enter or Key.Space: Invoke(HoverIndex); e.Handled = true; break;
            case Key.Delete or Key.Apps:
                if (HoverIndex >= 0) ItemContextRequested?.Invoke(this, new WheelItemEventArgs(HoverIndex, Items[HoverIndex]));
                e.Handled = true;
                break;
        }
    }

    private void Select(int index)
    {
        HoverIndex = index;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        var p = _palette;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(p.Panel, new Pen(p.PanelBorder, 1), c, Outer + 9, Outer + 9);

        if (_geometry is { } g)
        {
            for (var i = 0; i < Items.Count; i++) DrawWedge(context, g, i, c);
        }
        else
        {
            var mid = (Outer + Inner) / 2;
            context.DrawEllipse(null, new Pen(p.Wedge, Outer - Inner), c, mid, mid);
        }
        DrawHub(context, c);
    }

    private void DrawWedge(DrawingContext dc, WheelGeometry g, int i, Point c)
    {
        var p = _palette;
        var item = Items[i];
        var selected = i == HoverIndex;
        var (start, end) = g.Span(i, Items.Count == 1 ? 0.01 : GapDegrees);
        var sweep = Items.Count == 1 ? 359.9 : (end - start + 360) % 360;
        var large = sweep > 180;

        Point P(double r, double a) { var (x, y) = WheelGeometry.OnCircle(r, a); return new Point(c.X + x, c.Y + y); }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(P(Outer, start), true);
            ctx.ArcTo(P(Outer, start + sweep), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise);
            ctx.LineTo(P(Inner, start + sweep));
            ctx.ArcTo(P(Inner, start), new Size(Inner, Inner), 0, large, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(selected ? p.Accent : p.Wedge, null, geometry);
        if (item.Role == WheelItemRole.Add && !selected)
            dc.DrawGeometry(null, new Pen(p.Highlight, 1.5, new DashStyle([4, 3], 0)), geometry);

        var brush = selected ? p.TextOnAccent : item.Role == WheelItemRole.Add ? p.Highlight : p.Text;
        var chord = 2 * ((Inner + Outer) / 2) * Math.Sin(Math.Min(Math.PI / 2, Math.PI / Items.Count)) - 14;
        var maxWidth = Math.Max(48, chord);
        var label = item.Label + (item.NeedsOptions ? " …" : "");
        if (item.State == WheelItemState.NeedsEngine) label = "⭳ " + label;
        var size = 14.0;
        if (item.Role == WheelItemRole.Add && !Fits(label, size, maxWidth))
        {
            var space = label.IndexOf(' ');
            var stacked = space > 0 ? label[..space] + "\n" + label[(space + 1)..] : label;
            label = Fits(stacked, size, maxWidth) ? stacked : "+";
            if (label == "+") size = 26;
        }

        var text = Text(label, size, brush, maxWidth, LabelMaxHeight);
        var (lx, ly) = g.LabelCenter(i);
        DrawCentered(dc, text, c.X + lx, c.Y + ly, maxWidth);
    }

    /// <summary>Centred text is laid out inside its MaxTextWidth box, so the box (not the ink) is centred on the point.</summary>
    private static void DrawCentered(DrawingContext dc, FormattedText text, double cx, double top, double maxWidth, bool topIsCenter = true) =>
        dc.DrawText(text, new Point(cx - maxWidth / 2, topIsCenter ? top - text.Height / 2 : top));

    private bool Fits(string label, double size, double maxWidth)
    {
        var plain = new FormattedText(label, CultureInfo.CurrentUICulture, Flow, _face, size, Brushes.Black);
        return plain.WidthIncludingTrailingWhitespace <= maxWidth && plain.Height <= LabelMaxHeight;
    }

    private FlowDirection Flow => IsMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private FormattedText Text(string s, double size, IBrush brush, double maxWidth, double maxHeight) =>
        new(s, CultureInfo.CurrentUICulture, Flow, _face, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = maxHeight,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private void DrawHub(DrawingContext dc, Point c)
    {
        var p = _palette;
        var r = Inner - 8;
        dc.DrawEllipse(p.Hub, new Pen(p.PanelBorder, 1), c, r, r);

        if (Progress is { } progress)
        {
            dc.DrawEllipse(null, new Pen(p.Wedge, 5), c, r - 4, r - 4);
            var sweep = Math.Clamp(progress, 0.001, 1) * 359.9;
            Point P(double a) { var (x, y) = WheelGeometry.OnCircle(r - 4, a); return new Point(c.X + x, c.Y + y); }
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(P(0), false);
                ctx.ArcTo(P(sweep), new Size(r - 4, r - 4), 0, sweep > 180, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(p.Highlight, 5, lineCap: PenLineCap.Round), arc);
        }

        if (CenterText.Length == 0) return;
        var width = (r - 8) * 2;
        var main = Text(CenterText, 13, p.Text, width, 36);
        var hint = CenterHint.Length == 0 ? null : Text(CenterHint, 11, p.TextMuted, width, 16);
        var total = main.Height + (hint?.Height ?? 0);
        DrawCentered(dc, main, c.X, c.Y - total / 2, width, topIsCenter: false);
        if (hint is not null) DrawCentered(dc, hint, c.X, c.Y - total / 2 + main.Height, width, topIsCenter: false);
    }
}
