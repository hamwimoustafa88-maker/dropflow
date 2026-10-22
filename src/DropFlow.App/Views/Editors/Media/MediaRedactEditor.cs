// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Redact video over time (vid.redact): drag on the picture to draw a box, drag a box to move it, its edges to resize it. Each box has
/// its own row with "whole video" or a start and end (typed, set from the playhead, or dragged on the timeline while the box is selected).
/// Boxes are drawn solid where they will hide the picture at the playhead and as a dashed outline elsewhere. Blur or a solid colour.
/// <para>
/// The option format (key <c>boxes</c>) is documented on <see cref="MediaRedactModel.BoxesJson"/>. If the installed converter does not
/// declare <c>boxes</c> (the single-box version), one box is sent as pixels and drawing a second one is reported instead of being dropped.
/// </para>
/// </summary>
internal sealed class MediaRedactEditor : MediaEditorWindow
{
    private static readonly string[] Colors = ["black", "white", "gray", "red"];

    private readonly MediaRedactModel _model;
    private readonly StackPanel _rows = new();
    private readonly FrameworkElement _strengthRow;
    private readonly StackPanel _colorRow;
    private readonly RadioButton _blurRadio;
    private readonly RadioButton _solidRadio;
    private string _mode = "blur";
    private double _strength = 20;
    private string _color = "black";

    private enum Gesture { None, Create, Edit }
    private Gesture _gesture;
    private BoxHandle _handle;
    private Point _from;
    private PixelBox _startBox;
    private PixelBox? _drawing;

    public MediaRedactEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.redact.run")
    {
        _model = new MediaRedactModel(media.Duration, Math.Max(2, media.DisplayWidth), Math.Max(2, media.DisplayHeight));
        Overlay.Draw = DrawBoxes;
        Overlay.Pressed = OnPressed;
        Overlay.Dragged = OnDragged;
        Overlay.Released = OnReleased;
        Overlay.CursorAt = p =>
        {
            for (var i = _model.Boxes.Count - 1; i >= 0; i--)
                if (BoxMath.HitTest(_model.ToPixels(_model.Boxes[i]), p.X, p.Y, 8 / Overlay.Scale) is var h and not BoxHandle.None)
                    return h == BoxHandle.Move ? Cursors.SizeAll : Cursors.Cross;
            return Cursors.Cross;
        };
        Timeline.RangeDragged += OnRangeDragged;

        _blurRadio = Radio("redact-mode", "media.redact.blur", true, () => { _mode = "blur"; ShowModeControls(); });
        _solidRadio = Radio("redact-mode", "media.redact.solid", false, () => { _mode = "solid"; ShowModeControls(); });
        Panel.Children.Add(Row(_blurRadio, _solidRadio));
        var (strengthRow, _) = LabeledSlider("media.redact.strength", 2, 60, _strength, 1, v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture), v => _strength = v);
        _strengthRow = strengthRow;
        Panel.Children.Add(_strengthRow);
        _colorRow = Row(Label("media.redact.color"));
        foreach (var c in Colors)
        {
            var swatch = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 0), Focusable = false, ToolTip = Loc["media.color." + c],
                Background = (Brush)new BrushConverter().ConvertFromString(c)!, BorderBrush = Ui.Theme.PanelBorder, BorderThickness = new Thickness(2),
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, Loc["media.color." + c]);
            swatch.Click += (_, _) => { _color = c; HighlightColor(); Refresh(); };
            _colorRow.Children.Add(swatch);
        }
        Panel.Children.Add(_colorRow);
        Panel.Children.Add(_rows);
        Panel.Children.Add(Ui.Text(Loc["media.redact.hint"], 12, muted: true));
        ShowModeControls();
        HighlightColor();
    }

    internal MediaRedactModel Model => _model;
    internal void SetMode(string mode) { if (mode == "solid") _solidRadio.IsChecked = true; else _blurRadio.IsChecked = true; }
    internal void SetBoxTimes(int index, double? start, double? end) { _model.SetTimes(index, start, end); RebuildRows(); }

    /// <summary>Test hook: draws a box in picture pixels as a mouse drag would.</summary>
    internal void DrawBox(PixelBox box)
    {
        _model.Add(box);
        RebuildRows();
    }

    private void ShowModeControls()
    {
        _strengthRow.Visibility = _mode == "blur" ? Visibility.Visible : Visibility.Collapsed;
        _colorRow.Visibility = _mode == "solid" ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void HighlightColor()
    {
        foreach (var b in _colorRow.Children.OfType<Button>())
            b.BorderBrush = Equals(b.ToolTip, Loc["media.color." + _color]) ? Ui.Theme.Highlight : Ui.Theme.PanelBorder;
    }

    // ------------------------------------------------------------------------------------------------------------------ pointer on the picture

    private void OnPressed(Point p)
    {
        _from = p;
        var margin = 8 / Overlay.Scale;
        if (_model.Selected >= 0 && BoxMath.HitTest(_model.ToPixels(_model.Boxes[_model.Selected]), p.X, p.Y, margin) is var hs and not BoxHandle.None)
        {
            Begin(_model.Selected, hs);
            return;
        }
        for (var i = _model.Boxes.Count - 1; i >= 0; i--)
        {
            if (BoxMath.HitTest(_model.ToPixels(_model.Boxes[i]), p.X, p.Y, margin) is var h and not BoxHandle.None)
            {
                Begin(i, h);
                return;
            }
        }
        _gesture = Gesture.Create;
        _drawing = new PixelBox(Math.Clamp(p.X, 0, _model.FrameWidth), Math.Clamp(p.Y, 0, _model.FrameHeight), 0, 0);
    }

    private void Begin(int index, BoxHandle handle)
    {
        _model.Selected = index;
        _gesture = Gesture.Edit;
        _handle = handle;
        _startBox = _model.ToPixels(_model.Boxes[index]);
        SelectionChanged();
    }

    private void OnDragged(Point p)
    {
        if (_gesture == Gesture.Create && _drawing is not null)
        {
            var x1 = Math.Clamp(Math.Min(_from.X, p.X), 0, _model.FrameWidth);
            var y1 = Math.Clamp(Math.Min(_from.Y, p.Y), 0, _model.FrameHeight);
            var x2 = Math.Clamp(Math.Max(_from.X, p.X), 0, _model.FrameWidth);
            var y2 = Math.Clamp(Math.Max(_from.Y, p.Y), 0, _model.FrameHeight);
            _drawing = new PixelBox(x1, y1, x2 - x1, y2 - y1);
        }
        else if (_gesture == Gesture.Edit && _model.Selected >= 0)
        {
            _model.SetPixels(_model.Selected, BoxMath.Drag(_startBox, _handle, p.X - _from.X, p.Y - _from.Y, _model.FrameWidth, _model.FrameHeight));
        }
        Refresh();
    }

    private void OnReleased(Point p)
    {
        if (_gesture == Gesture.Create && _drawing is { } box && box.W * Overlay.Scale >= 8 && box.H * Overlay.Scale >= 8)
        {
            _model.Add(box with { W = Math.Max(box.W, BoxMath.MinSide), H = Math.Max(box.H, BoxMath.MinSide) });
            RebuildRows();
        }
        _gesture = Gesture.None;
        _drawing = null;
        Refresh();
    }

    // ------------------------------------------------------------------------------------------------------------------ time ranges

    private void OnRangeDragged(RangeHandle handle, double t)
    {
        if (_model.Selected < 0) return;
        var b = _model.Boxes[_model.Selected];
        var start = b.Start ?? 0;
        var end = b.End ?? Media.Duration;
        if (handle == RangeHandle.Start) start = Math.Min(t, end - MediaEditMath.MinSpan);
        else end = Math.Max(t, start + MediaEditMath.MinSpan);
        _model.SetTimes(_model.Selected, start, end);
        RebuildRows();
    }

    private void SelectionChanged()
    {
        if (_model.Selected >= 0 && _model.Selected < _model.Boxes.Count)
        {
            var b = _model.Boxes[_model.Selected];
            Timeline.Range = new MediaSpan(b.Start ?? 0, b.End ?? Media.Duration);
        }
        else
        {
            Timeline.Range = null;
        }
        foreach (var (row, i) in _rows.Children.OfType<Border>().Select((r, i) => (r, i)))
            row.BorderBrush = i == _model.Selected ? Ui.Theme.Highlight : Ui.Theme.PanelBorder;
        Refresh();
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        for (var i = 0; i < _model.Boxes.Count; i++)
        {
            var index = i;
            var box = _model.Boxes[i];
            var whole = new CheckBox { Content = Loc["media.redact.whole"], IsChecked = box.WholeVideo, Foreground = Ui.Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var start = new MediaTimeBox(box.Start ?? 0, t => { var b = _model.Boxes[index]; _model.SetTimes(index, t, b.End ?? Media.Duration); RebuildRows(); });
            var end = new MediaTimeBox(box.End ?? Media.Duration, t => { var b = _model.Boxes[index]; _model.SetTimes(index, b.Start ?? 0, t); RebuildRows(); });
            start.IsEnabled = end.IsEnabled = !box.WholeVideo;
            whole.Click += (_, _) =>
            {
                if (whole.IsChecked == true) _model.SetTimes(index, null, null);
                else _model.SetTimes(index, Media.Current, Media.Duration);
                RebuildRows();
            };
            var fromHere = SmallButton("media.redact.fromPlayhead", () => { var b = _model.Boxes[index]; _model.SetTimes(index, Media.Current, b.End ?? Media.Duration); RebuildRows(); });
            var toHere = SmallButton("media.redact.toPlayhead", () => { var b = _model.Boxes[index]; _model.SetTimes(index, b.Start ?? 0, Media.Current); RebuildRows(); });
            fromHere.IsEnabled = toHere.IsEnabled = !box.WholeVideo;
            var delete = SmallButton("media.redact.delete", () => { _model.Delete(index); RebuildRows(); });

            var line = Row(
                new TextBlock { Text = Loc.Format("media.redact.area", index + 1), Width = 70, Foreground = Ui.Theme.Text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                whole, Label("media.start"), start, fromHere, Label("media.end"), end, toHere, delete);
            var row = new Border { Child = line, BorderThickness = new Thickness(1.5), BorderBrush = Ui.Theme.PanelBorder, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 4, 0, 0), Background = Ui.Theme.Wedge };
            row.PreviewMouseLeftButtonDown += (_, _) => { _model.Selected = index; SelectionChanged(); };
            _rows.Children.Add(row);
        }
        SelectionChanged();
    }

    protected override bool OnKey(Key key, ModifierKeys modifiers)
    {
        if (key is Key.Delete or Key.Back && _model.Selected >= 0) { _model.Delete(_model.Selected); RebuildRows(); return true; }
        return false;
    }

    protected override void OnPlayheadMoved() => Overlay.InvalidateVisual();

    // ------------------------------------------------------------------------------------------------------------------ drawing

    private void DrawBoxes(DrawingContext dc)
    {
        var dashed = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1.5) { DashStyle = DashStyles.Dash };
        var solid = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.5);
        var fill = _mode == "solid"
            ? (Brush)new BrushConverter().ConvertFromString(_color)!
            : new SolidColorBrush(Color.FromArgb(0xD0, 0x80, 0x88, 0x98));
        for (var i = 0; i < _model.Boxes.Count; i++)
        {
            var b = _model.Boxes[i];
            var r = Overlay.ToScreen(_model.ToPixels(b));
            var visible = b.VisibleAt(Media.Current);
            if (visible)
            {
                dc.DrawRectangle(fill, null, r);
                if (_mode == "blur") DrawBlurPattern(dc, r);
            }
            // solid outline: hidden at the playhead; dashed: this area is hidden at other times only
            dc.DrawRectangle(null, i == _model.Selected ? new Pen(Ui.Theme.Highlight, 2.5) { DashStyle = visible ? DashStyles.Solid : DashStyles.Dash } : visible ? solid : dashed, r);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var label = new FormattedText((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, dpi);
            dc.DrawRoundedRectangle(i == _model.Selected ? Ui.Theme.Accent : new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), null, new Rect(r.X, r.Y - 18, label.Width + 10, 17), 4, 4);
            dc.DrawText(label, new Point(r.X + 5, r.Y - 18));
            if (i == _model.Selected)
                foreach (var (hx, hy) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Left, r.Bottom), (r.Right, r.Bottom) })
                    dc.DrawRectangle(Brushes.White, new Pen(Ui.Theme.Accent, 1), new Rect(hx - 4, hy - 4, 8, 8));
        }
        if (_drawing is { } d) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), new Pen(Brushes.White, 1.5), Overlay.ToScreen(d));
    }

    /// <summary>A coarse mosaic so "blur" reads as blur in the preview (the real blur is FFmpeg's boxblur).</summary>
    private static void DrawBlurPattern(DrawingContext dc, Rect r)
    {
        dc.PushClip(new RectangleGeometry(r));
        var light = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        const double cell = 12;
        for (var y = r.Top; y < r.Bottom; y += cell)
            for (var x = r.Left + ((int)((y - r.Top) / cell) % 2) * cell; x < r.Right; x += cell * 2)
                dc.DrawRectangle(light, null, new Rect(x, y, cell, cell));
        dc.Pop();
    }

    protected override string? Problem => _model.ToOptions(_mode, _strength, _color, Context.Declares("boxes")).Problem;

    protected override string? Note => Loc.Format("media.redact.note", _model.Boxes.Count);

    protected override Dictionary<string, object?> Values() => _model.ToOptions(_mode, _strength, _color, Context.Declares("boxes")).Options!;
}
