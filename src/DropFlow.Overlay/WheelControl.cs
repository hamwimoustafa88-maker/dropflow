using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DropFlow.Overlay;

/// <summary>
/// The radial wheel. Draws wedges, a hub with a label and a progress ring, and handles hover/click/keyboard. During an OLE drag the
/// mouse events are not delivered, so the host feeds the pointer through <see cref="SetPointer"/> instead.
/// </summary>
public sealed class WheelControl : FrameworkElement
{
    public const double Outer = 170;
    public const double Inner = 64;
    private const double Padding = 16;
    private const double GapDegrees = 3.2;
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private WheelGeometry? _geometry;
    private WheelTheme _theme = WheelTheme.FromSystem();

    public WheelControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Width = Height = (Outer + Padding) * 2;
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
    }

    // ----- state ------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<WheelItem> Items { get; private set; } = [];

    /// <summary>Wedge under the pointer (or keyboard selection); -1 when none.</summary>
    public int HoverIndex { get; private set; } = -1;

    public string CenterText { get; private set; } = "";
    public string CenterHint { get; private set; } = "";

    /// <summary>0..1 shows a progress ring in the hub; null hides it; negative shows a busy spinner position of 0.</summary>
    public double? Progress { get; private set; }

    public bool IsMirrored { get; private set; }

    public WheelTheme Theme
    {
        get => _theme;
        set { _theme = value; InvalidateVisual(); }
    }

    public event EventHandler<WheelItemEventArgs>? ItemInvoked;

    /// <summary>Right-click, the Menu key, Shift+F10 or Delete on a wedge.</summary>
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

    /// <summary>Pointer position relative to this control (used for drag-over, where mouse events do not arrive).</summary>
    public int SetPointer(Point position)
    {
        var idx = _geometry?.HitTest(position.X - ActualWidth / 2, position.Y - ActualHeight / 2) ?? -1;
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

    /// <summary>The wedge a drop at <paramref name="position"/> lands on, or -1.</summary>
    public int WedgeAt(Point position) => _geometry?.HitTest(position.X - ActualWidth / 2, position.Y - ActualHeight / 2) ?? -1;

    public void Invoke(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        ItemInvoked?.Invoke(this, new WheelItemEventArgs(index, Items[index]));
    }

    // ----- input ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole square of the control is one hit target, drawn or not. During an OLE drag WPF re-targets the drag whenever the element under the
    /// pointer changes; if petals, the gaps between them, the hub and the empty corners alternated with the window background, every crossing would
    /// be a DragLeave/DragEnter pair and a flicker of the "no drop" cursor. Which wedge is meant is decided by <see cref="WheelGeometry"/>, not by WPF.
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetPointer(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Invoke(WedgeAt(e.GetPosition(this)));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var index = WedgeAt(e.GetPosition(this));
        if (index < 0) return;
        Select(index);
        RequestContext(index);
        e.Handled = true;
    }

    /// <summary>Asks the host for the wedge's menu ("Remove from wheel" on a pinned item).</summary>
    public void RequestContext(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        ItemContextRequested?.Invoke(this, new WheelItemEventArgs(index, Items[index]));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_geometry is null && e.Key != Key.Escape) return;

        // The Menu key, Shift+F10 and Delete open the wedge's menu, the same as a right-click.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Apps or Key.Delete || (key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            RequestContext(HoverIndex);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Right or Key.Down:
                Select(_geometry!.Move(HoverIndex, +1)); e.Handled = true; break;
            case Key.Left or Key.Up:
                Select(_geometry!.Move(HoverIndex, -1)); e.Handled = true; break;
            case Key.Enter or Key.Space:
                Invoke(HoverIndex); e.Handled = true; break;
            case Key.Escape:
                Cancelled?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
        }
    }

    private void Select(int index)
    {
        HoverIndex = index;
        InvalidateVisual();
    }

    // ----- animation --------------------------------------------------------------------------------------------------------

    /// <summary>Pops the wheel open (skipped when Windows animations are turned off).</summary>
    public void PlayOpenAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            RenderTransform = Transform.Identity;
            return;
        }

        var scale = new ScaleTransform(0.86, 0.86);
        RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    // ----- drawing ----------------------------------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var t = _theme;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // card behind the wheel
        dc.DrawEllipse(t.Panel, new Pen(t.PanelBorder, 1), c, Outer + 9, Outer + 9);

        if (_geometry is { } g)
        {
            for (var i = 0; i < Items.Count; i++)
                DrawWedge(dc, g, i, c, dpi);
        }
        else
        {
            // empty wheel (waiting for files): a calm ring
            dc.DrawEllipse(null, new Pen(t.Wedge, Outer - Inner), c, (Outer + Inner) / 2, (Outer + Inner) / 2);
        }

        DrawHub(dc, c, dpi);
    }

    private void DrawWedge(DrawingContext dc, WheelGeometry g, int i, Point c, double dpi)
    {
        var t = _theme;
        var selected = i == HoverIndex;
        var item = Items[i];
        var (start, end) = g.Span(i, Items.Count == 1 ? 0.01 : GapDegrees);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            Point P(double r, double a) { var (x, y) = WheelGeometry.OnCircle(r, a); return new Point(c.X + x, c.Y + y); }
            var sweep = (end - start + 360) % 360;
            if (Items.Count == 1) sweep = 359.9;
            var large = sweep > 180;

            ctx.BeginFigure(P(Outer, start), true, true);
            ctx.ArcTo(P(Outer, start + sweep), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(Inner, start + sweep), true, false);
            ctx.ArcTo(P(Inner, start), new Size(Inner, Inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(selected ? t.Accent : t.Wedge, null, geometry);
        if (item.Role == WheelItemRole.Add && !selected)
            dc.DrawGeometry(null, new Pen(t.Highlight, 1.5) { DashStyle = DashStyles.Dash }, geometry);

        var (text, origin, _) = LayoutLabel(i, dpi);
        dc.DrawText(text, new Point(c.X + origin.X, c.Y + origin.Y));
    }

    /// <summary>
    /// The label of wedge <paramref name="i"/> and its top-left corner relative to the wheel centre. The "+ Add" slot must always be
    /// readable: when its words do not fit a narrow wedge (a full wheel) they go on two lines ("+" over "Add"), and only when even that
    /// does not fit does it show a large "+" — never an ellipsis.
    /// </summary>
    internal (FormattedText Text, Point Origin, double Size) LayoutLabel(int i, double dpi)
    {
        var t = _theme;
        var g = _geometry ?? throw new InvalidOperationException("No items.");
        var item = Items[i];
        var selected = i == HoverIndex;
        var brush = selected ? t.TextOnAccent : item.Role == WheelItemRole.Add ? t.Highlight : t.Text;
        var flow = IsMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var chord = 2 * ((Inner + Outer) / 2) * Math.Sin(Math.Min(Math.PI / 2, Math.PI / Items.Count)) - 14;
        var maxWidth = Math.Max(48, chord);

        var label = item.Label + (item.NeedsOptions ? " …" : "");
        if (item.State == WheelItemState.NeedsEngine) label = "⭳ " + label;
        if (item.Role == WheelItemRole.Add && !Fits(label, 14, maxWidth, LabelMaxHeight, flow, dpi))
        {
            var space = label.IndexOf(' ');
            var stacked = space > 0 ? label[..space] + "\n" + label[(space + 1)..] : label;
            label = Fits(stacked, 14, maxWidth, LabelMaxHeight, flow, dpi) ? stacked : "+";
        }

        var size = item.Role == WheelItemRole.Add && label == "+" ? 26 : 14;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, flow, LabelFace, size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = LabelMaxHeight,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var (lx, ly) = g.LabelCenter(i);
        return (text, new Point(lx - text.Width / 2, ly - text.Height / 2), size);
    }

    internal const double LabelMaxHeight = 38;

    /// <summary>True when the text, laid out without wrapping, fits the box (so nothing is wrapped away or trimmed).</summary>
    internal static bool Fits(string label, double size, double maxWidth, double maxHeight, FlowDirection flow, double dpi)
    {
        var plain = new FormattedText(label, CultureInfo.CurrentUICulture, flow, LabelFace, size, Brushes.Black, dpi);
        return plain.WidthIncludingTrailingWhitespace <= maxWidth && plain.Height <= maxHeight;
    }

    private void DrawHub(DrawingContext dc, Point c, double dpi)
    {
        var t = _theme;
        var r = Inner - 8;
        dc.DrawEllipse(t.Hub, new Pen(t.PanelBorder, 1), c, r, r);

        if (Progress is { } p)
        {
            var track = new Pen(t.Wedge, 5);
            dc.DrawEllipse(null, track, c, r - 4, r - 4);
            var sweep = Math.Clamp(p, 0.001, 1) * 359.9;
            var pen = new Pen(t.Highlight, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                Point P(double a) { var (x, y) = WheelGeometry.OnCircle(r - 4, a); return new Point(c.X + x, c.Y + y); }
                ctx.BeginFigure(P(0), false, false);
                ctx.ArcTo(P(sweep), new Size(r - 4, r - 4), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, pen, arc);
        }

        if (CenterText.Length == 0) return;
        var main = new FormattedText(CenterText, CultureInfo.CurrentUICulture,
            IsMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, LabelFace, 13, t.Text, dpi)
        {
            MaxTextWidth = (r - 8) * 2,
            MaxTextHeight = 36,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var hint = CenterHint.Length == 0 ? null : new FormattedText(CenterHint, CultureInfo.CurrentUICulture,
            IsMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, LabelFace, 11, t.TextMuted, dpi)
        {
            MaxTextWidth = (r - 8) * 2,
            MaxTextHeight = 16,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var total = main.Height + (hint?.Height ?? 0);
        dc.DrawText(main, new Point(c.X - main.Width / 2, c.Y - total / 2));
        if (hint is not null) dc.DrawText(hint, new Point(c.X - hint.Width / 2, c.Y - total / 2 + main.Height));
    }
}
