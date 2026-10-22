// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Split (vid.split): add cuts at the playhead (S) or make 2-50 equal clips, drag a cut to move it, click a clip and press Delete to
/// leave it out (Delete on a selected cut removes the cut). The kept clips are saved as separate files or joined into one video.
/// </summary>
internal sealed class MediaSplitEditor : MediaEditorWindow
{
    private readonly MediaSplitModel _model;
    private readonly TextBox _count;
    private int _selectedClip;
    private int _selectedPoint = -1;
    private bool _dragging;
    private bool _join;
    private bool _reencode;

    public MediaSplitEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.split.run", stripHeight: 72)
    {
        _model = new MediaSplitModel(media.Duration);
        Timeline.Overlay = DrawClips;
        Timeline.Pressed = OnPressed;
        Timeline.Dragged = (t, _) => { if (_dragging && _selectedPoint >= 0) { _model.MovePoint(_selectedPoint, t); Refresh(); } };
        Timeline.Released = (_, _) => _dragging = false;

        Panel.Children.Add(Row(
            SmallButton("media.split.addCut", AddCutAtPlayhead, primary: true),
            SmallButton("media.split.toggleClip", () => { _model.ToggleRemoved(_selectedClip); Refresh(); }),
            SmallButton("media.split.deleteCut", DeleteSelectedCut)));

        _count = new TextBox { Text = "2", Width = 56, MinHeight = 28, FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
        Panel.Children.Add(Row(Label("media.split.equal"), _count, new Border { Width = 8 }, SmallButton("media.split.makeEqual", MakeEqual)));

        Panel.Children.Add(Row(
            Radio("split-output", "media.split.saveClips", true, () => _join = false),
            Radio("split-output", "media.split.joinRest", false, () => _join = true)));
        if (context.Declares("reencode")) Panel.Children.Add(Check("media.split.exact", false, v => _reencode = v));
        Panel.Children.Add(Ui.Text(Loc["media.split.keys"], 12, muted: true));
    }

    internal MediaSplitModel Model => _model;
    internal void SelectClip(int index) { _selectedClip = index; _selectedPoint = -1; Refresh(); }
    internal void SetJoin(bool join) { _join = join; Refresh(); }

    private void AddCutAtPlayhead()
    {
        var index = _model.AddPoint(Media.Current);
        if (index >= 0) { _selectedPoint = index; _selectedClip = index + 1; }
        Refresh();
    }

    private void DeleteSelectedCut()
    {
        if (_selectedPoint < 0 || _selectedPoint >= _model.Points.Count) return;
        _model.RemovePoint(_selectedPoint);
        _selectedClip = Math.Min(_selectedPoint, _model.Clips.Count - 1);
        _selectedPoint = -1;
        Refresh();
    }

    private void MakeEqual()
    {
        if (!MediaTimeText.TryParse(_count.Text, out var n)) { _count.Text = "2"; return; }
        _model.MakeEqual((int)Math.Round(n));
        _count.Text = (_model.Clips.Count).ToString(CultureInfo.InvariantCulture);
        _selectedPoint = -1;
        _selectedClip = 0;
        Refresh();
    }

    private bool OnPressed(double t, Point p, MouseButtonEventArgs e)
    {
        var near = _model.PointNear(t, Timeline.SecondsPer(7));
        if (near >= 0)
        {
            _selectedPoint = near;
            _dragging = true;
            Refresh();
            return true;
        }
        _selectedPoint = -1;
        _selectedClip = _model.ClipAt(t);
        if (e.ClickCount == 2) _model.ToggleRemoved(_selectedClip);
        Refresh();
        return false; // keep scrubbing
    }

    protected override bool OnKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.S: AddCutAtPlayhead(); return true;
            case Key.Delete or Key.Back:
                if (_selectedPoint >= 0) DeleteSelectedCut();
                else { _model.ToggleRemoved(_selectedClip); Refresh(); }
                return true;
            default: return false;
        }
    }

    private void DrawClips(DrawingContext dc)
    {
        var strip = Timeline.StripRect;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var removedFill = new SolidColorBrush(Color.FromArgb(0xC8, 0x14, 0x14, 0x18));
        var hatch = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xF0, 0x55, 0x55)), 1.5);
        foreach (var (number, span, removed) in _model.Clips)
        {
            var x1 = Timeline.XAt(span.Start);
            var x2 = Timeline.XAt(span.End);
            var cell = new Rect(x1, strip.Top, Math.Max(0, x2 - x1), strip.Height);
            if (removed)
            {
                dc.DrawRectangle(removedFill, null, cell);
                dc.PushClip(new RectangleGeometry(cell));
                for (var x = x1 - strip.Height; x < x2; x += 10) dc.DrawLine(hatch, new Point(x, strip.Bottom), new Point(x + strip.Height, strip.Top));
                dc.Pop();
            }
            if (number - 1 == _selectedClip && _selectedPoint < 0)
                dc.DrawRectangle(null, new Pen(Ui.Theme.Highlight, 2.5), new Rect(cell.X + 1.5, cell.Y + 1.5, Math.Max(0, cell.Width - 3), cell.Height - 3));

            var label = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, Brushes.White, dpi);
            if (label.Width + 10 < cell.Width)
            {
                var badge = new Rect(cell.X + 4, cell.Y + 4, label.Width + 8, label.Height + 2);
                dc.DrawRoundedRectangle(removed ? Brushes.IndianRed : new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), null, badge, 4, 4);
                dc.DrawText(label, new Point(badge.X + 4, badge.Y + 1));
            }
        }
        for (var i = 0; i < _model.Points.Count; i++)
        {
            var x = Timeline.XAt(_model.Points[i]);
            var selected = i == _selectedPoint;
            dc.DrawRectangle(selected ? Ui.Theme.Highlight : Brushes.White, null, new Rect(x - (selected ? 1.5 : 1), strip.Top - 4, selected ? 3 : 2, strip.Height + 8));
            var diamond = new StreamGeometry();
            using (var g = diamond.Open())
            {
                g.BeginFigure(new Point(x, strip.Bottom + 1), true, true);
                g.LineTo(new Point(x + 5, strip.Bottom + 6), true, false);
                g.LineTo(new Point(x, strip.Bottom + 11), true, false);
                g.LineTo(new Point(x - 5, strip.Bottom + 6), true, false);
            }
            diamond.Freeze();
            dc.DrawGeometry(selected ? Ui.Theme.Highlight : Brushes.White, new Pen(Brushes.Black, 0.6), diamond);
        }
    }

    protected override string? Problem => _model.Problem(_join);

    protected override string? Note
    {
        get
        {
            var kept = _model.Clips.Count - _model.RemovedCount;
            var text = Loc.Format("media.split.summary", _model.Clips.Count, _model.RemovedCount);
            return _join ? text + " " + Loc.Format("media.split.joinNote", kept) : text;
        }
    }

    protected override Dictionary<string, object?> Values() => _model.ToOptions(_reencode, _join);
}
