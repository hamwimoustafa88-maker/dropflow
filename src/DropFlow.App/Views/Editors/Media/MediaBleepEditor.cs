// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Bleep (aud.bleep): the lane above the waveform holds the parts to beep over. Drag on an empty part of the lane to create one, drag it
/// to move it, drag its edges to resize it, click to select and Delete to remove; "Add at playhead" (B) adds half a second. A/B buttons
/// play the selected part (with a little around it) as it is now and with the beep, rendered by FFmpeg into the scratch folder.
/// </summary>
internal sealed class MediaBleepEditor : MediaEditorWindow
{
    private const double LaneHeight = 34;
    private readonly MediaBleepModel _model;
    private readonly MediaPreviewPlayer _preview = new();
    private readonly Button _playOriginal;
    private readonly Button _playBleeped;
    private double _frequency = 1000;
    private double _volume = 0.35;

    private enum Drag { None, Create, Move, Start, End }
    private Drag _drag;
    private double _anchor;

    public MediaBleepEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.bleep.run", stripHeight: 110, laneHeight: LaneHeight)
    {
        _model = new MediaBleepModel(media.Duration);
        Timeline.Overlay = DrawRanges;
        Timeline.Pressed = OnPressed;
        Timeline.Dragged = OnDragged;
        Timeline.Released = (_, _) => { _drag = Drag.None; Refresh(); };

        Panel.Children.Add(Row(
            SmallButton("media.bleep.addAtPlayhead", () => { _model.AddAt(Media.Current); Refresh(); }, primary: true),
            SmallButton("media.bleep.delete", () => { _model.Delete(_model.Selected); Refresh(); })));

        _playOriginal = SmallButton("media.bleep.playOriginal", () => _ = PreviewAsync(false));
        _playBleeped = SmallButton("media.bleep.playBleeped", () => _ = PreviewAsync(true));
        Panel.Children.Add(Row(Label("media.bleep.listen"), _playOriginal, _playBleeped));
        _preview.Changed += UpdatePreviewButtons;
        _preview.Failed += () => Dispatcher.BeginInvoke(() => { Refresh(); });

        Panel.Children.Add(LabeledSlider("media.bleep.frequency", 200, 4000, _frequency, 50, v => v.ToString("0", CultureInfo.InvariantCulture) + " Hz", v => _frequency = v).Row);
        Panel.Children.Add(LabeledSlider("media.bleep.volume", 0.05, 1, _volume, 0.05, v => (v * 100).ToString("0", CultureInfo.InvariantCulture) + " %", v => _volume = v).Row);
        Panel.Children.Add(Ui.Text(Loc["media.bleep.hint"], 12, muted: true));
    }

    internal MediaBleepModel Model => _model;
    internal void AddRange(double start, double end) { _model.Add(start, end); Refresh(); }

    private bool OnPressed(double t, Point p, MouseButtonEventArgs e)
    {
        if (p.Y > LaneHeight)
        {
            // On the waveform: select the range under the pointer (if any) and keep scrubbing.
            var under = _model.HitTest(t, 0);
            _model.Selected = under.Index;
            Refresh();
            return false;
        }
        var (index, part) = _model.HitTest(t, Timeline.SecondsPer(6));
        if (index >= 0)
        {
            _model.Selected = index;
            _drag = part switch { BleepPart.StartEdge => Drag.Start, BleepPart.EndEdge => Drag.End, _ => Drag.Move };
            _anchor = t - _model.Ranges[index].Start;
        }
        else
        {
            _drag = Drag.Create;
            _anchor = t;
            _model.Add(t, t + Timeline.SecondsPer(4));
        }
        Refresh();
        return true;
    }

    private void OnDragged(double t, Point p)
    {
        var i = _model.Selected;
        if (i < 0) return;
        var r = _model.Ranges[i];
        switch (_drag)
        {
            case Drag.Create: _model.Replace(i, Math.Min(_anchor, t), Math.Max(_anchor, t)); break;
            case Drag.Move: _model.Move(i, t - _anchor); break;
            case Drag.Start: _model.Replace(i, Math.Min(t, r.End - MediaEditMath.MinSpan), r.End); break;
            case Drag.End: _model.Replace(i, r.Start, Math.Max(t, r.Start + MediaEditMath.MinSpan)); break;
        }
        Refresh();
    }

    protected override bool OnKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Delete or Key.Back: _model.Delete(_model.Selected); Refresh(); return true;
            case Key.B: _model.AddAt(Media.Current); Refresh(); return true;
            default: return false;
        }
    }

    /// <summary>The selected range with 1.5 s around it, or six seconds from the playhead.</summary>
    private (double Start, double Length) PreviewWindow()
    {
        if (_model.Selected >= 0)
        {
            var r = _model.Ranges[_model.Selected];
            var start = Math.Max(0, r.Start - 1.5);
            return (start, Math.Min(Media.Duration - start, r.End + 1.5 - start));
        }
        var from = Math.Min(Media.Current, Math.Max(0, Media.Duration - 6));
        return (from, Math.Min(6, Media.Duration - from));
    }

    private async Task PreviewAsync(bool bleeped)
    {
        Media.Pause();
        var (start, length) = PreviewWindow();
        var ranges = _model.Ranges.Select(r => (r.Start, r.End)).ToList();
        var channels = Media.Info.Audio?.Channels ?? 2;
        await _preview.ToggleAsync(bleeped ? "b" : "a", ct =>
            MediaPreview.BleepPreviewAsync(Media.Engines, Media.Path, ranges, _frequency, _volume, channels, start, length, bleeped, Media.Scratch, ct));
    }

    private void UpdatePreviewButtons()
    {
        _playOriginal.Content = Loc[_preview.Playing == "a" ? "media.stop" : "media.bleep.playOriginal"];
        _playBleeped.Content = Loc[_preview.Playing == "b" ? "media.stop" : "media.bleep.playBleeped"];
    }

    protected override void OnClosedEditor() => _preview.Dispose();

    private void DrawRanges(DrawingContext dc)
    {
        var lane = Timeline.LaneRect;
        var strip = Timeline.StripRect;
        var fill = new SolidColorBrush(Color.FromArgb(0xD8, 0xF0, 0x7A, 0x3A));
        var selectedFill = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
        var onStrip = new SolidColorBrush(Color.FromArgb(0x48, 0xF5, 0x9E, 0x0B));
        for (var i = 0; i < _model.Ranges.Count; i++)
        {
            var r = _model.Ranges[i];
            var x1 = Timeline.XAt(r.Start);
            var x2 = Math.Max(x1 + 3, Timeline.XAt(r.End));
            dc.DrawRectangle(onStrip, null, new Rect(x1, strip.Top, x2 - x1, strip.Height));
            var block = new Rect(x1, lane.Top + 4, x2 - x1, lane.Height - 8);
            var selected = i == _model.Selected;
            dc.DrawRoundedRectangle(selected ? selectedFill : fill, selected ? new Pen(Brushes.White, 1.5) : null, block, 4, 4);
            if (selected)
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(x1, block.Top + 4, 2, block.Height - 8));
                dc.DrawRectangle(Brushes.White, null, new Rect(x2 - 2, block.Top + 4, 2, block.Height - 8));
            }
        }
        if (_model.Ranges.Count == 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var hint = new FormattedText(Loc["media.bleep.laneHint"], CultureInfo.CurrentUICulture, Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Ui.Theme.TextMuted, dpi);
            dc.DrawText(hint, new Point(lane.Left + (lane.Width - hint.Width) / 2 + (Loc.IsRightToLeft ? hint.Width : 0), lane.Top + (lane.Height - hint.Height) / 2));
        }
    }

    protected override string? Problem => _model.Ranges.Count == 0 ? "media.bleep.none" : null;

    protected override string? Note => Loc.Format("media.bleep.note", _model.Ranges.Count, MediaText.Len(Loc, _model.Ranges.Sum(r => r.Length)));

    protected override Dictionary<string, object?> Values() => _model.ToOptions(_frequency, _volume);
}
