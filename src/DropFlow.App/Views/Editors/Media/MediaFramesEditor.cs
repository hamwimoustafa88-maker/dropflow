// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Save frames (vid.snapshot): step to a frame and press Add (or Enter / A); each chosen frame is listed with its picture and time
/// (click it to go back to it, ✕ to drop it) and marked on the timeline. Optionally also a frame every N seconds.
/// </summary>
internal sealed class MediaFramesEditor : MediaEditorWindow
{
    private readonly MediaFrameList _frames;
    private readonly WrapPanel _list = new();
    private readonly TextBox _every;
    private readonly Dictionary<double, Task<System.Windows.Media.Imaging.BitmapSource?>> _pictures = [];
    private double _everySeconds;

    public MediaFramesEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.frames.run")
    {
        _frames = new MediaFrameList(media.Duration, media.FrameStep);
        TransportExtras.Children.Add(SmallButton("media.frames.add", AddAtPlayhead, primary: true));

        _every = new TextBox { Text = "0", Width = 56, MinHeight = 28, FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
        _every.TextChanged += (_, _) => { _everySeconds = MediaTimeText.TryParse(_every.Text, out var s) ? s : 0; Refresh(); };
        if (context.Declares("every")) Panel.Children.Add(Row(Label("media.frames.every"), _every));
        Panel.Children.Add(_list);
        Panel.Children.Add(Ui.Text(Loc["media.frames.keys"], 12, muted: true));
    }

    internal MediaFrameList Frames => _frames;

    internal void AddAtPlayhead()
    {
        var t = Media.Current;
        if (!_frames.Add(t)) return;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        for (var i = 0; i < _frames.Times.Count; i++)
        {
            var index = i;
            var time = _frames.Times[i];
            var picture = new Image { Width = 128, Height = 72, Stretch = Stretch.Uniform };
            if (Media.HasVideo) _ = LoadAsync(picture, time);
            var remove = new Button { Content = "✕", Width = 24, Height = 24, Padding = new Thickness(0), Background = Ui.Theme.Wedge, Foreground = Ui.Theme.Text, BorderThickness = new Thickness(0), Focusable = false, ToolTip = Loc["media.frames.remove"] };
            System.Windows.Automation.AutomationProperties.SetName(remove, Loc["media.frames.remove"]);
            remove.Click += (_, _) => { _frames.Remove(index); Rebuild(); };
            var caption = new DockPanel { FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(remove, Dock.Right);
            caption.Children.Add(remove);
            caption.Children.Add(new TextBlock { Text = MediaTimeText.Clock(time), Foreground = Ui.Theme.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var card = new Border
            {
                Background = Ui.Theme.Wedge, BorderBrush = Ui.Theme.PanelBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6), Margin = new Thickness(0, 6, 8, 0), Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4), Child = picture }, caption } },
            };
            card.MouseLeftButtonUp += (_, _) => Media.Seek(time);
            _list.Children.Add(card);
        }
        Timeline.Markers = [.. _frames.Times];
        Refresh();
    }

    private async Task LoadAsync(Image target, double time)
    {
        try
        {
            if (!_pictures.TryGetValue(time, out var load)) _pictures[time] = load = Media.FrameAsync(time, 72);
            if (await load is { } frame) await target.Dispatcher.InvokeAsync(() => target.Source = frame);
        }
        catch (OperationCanceledException) { /* closed */ }
    }

    protected override bool OnKey(Key key, ModifierKeys modifiers)
    {
        if (key is Key.Enter or Key.A) { AddAtPlayhead(); return true; }
        return false;
    }

    protected override string? Problem => _frames.Times.Count == 0 && _everySeconds <= 0 ? "media.frames.none" : null;

    protected override string? Note => _everySeconds > 0
        ? Loc.Format("media.frames.noteEvery", _frames.Times.Count, _everySeconds.ToString("0.##", CultureInfo.InvariantCulture))
        : Loc.Format("media.frames.note", _frames.Times.Count);

    protected override Dictionary<string, object?> Values() => _frames.ToOptions(_everySeconds);
}
