// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.App.Services;
using DropFlow.Core.Operations;
using DropFlow.Core.Runner;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Preview;
using DropFlow.Overlay;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Join videos (vid.join) and merge audio (aud.merge): the files in the order they will be joined, each with a picture (or its waveform)
/// and its length. Drag a row to move it, or use the arrows (Alt+Up / Alt+Down on the selected row). The tool's choices (join mode,
/// output format) sit below. The result carries the new order.
/// </summary>
internal sealed class MediaJoinEditor : Window
{
    private readonly MediaEditorContext _context;
    private readonly UiKit _ui;
    private readonly MediaOrderModel _order;
    private readonly StackPanel _list = new();
    private readonly Dictionary<string, (ImageSource? Picture, float[]? Wave, double? Length)> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _choices = [];
    private readonly TextBlock _total;
    private readonly TaskCompletionSource<OptionsEdit?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _life = new();
    private readonly bool _video;
    private int _selected;
    private int _dragFrom = -1;
    private bool _finished;

    public MediaJoinEditor(MediaEditorContext context)
    {
        _context = context;
        _ui = context.Ui;
        _video = context.Operation == "vid.join";
        _order = new MediaOrderModel(context.Files);
        var loc = _ui.Loc;
        _ui.Style(this, 720, context.Title);
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        Height = 640;
        MinHeight = 420;
        MinWidth = 560;

        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(_ui.Text(loc[_video ? "media.join.title" : "media.merge.title"], 16, weight: FontWeights.SemiBold));
        header.Children.Add(_ui.Text(loc["media.join.hint"], 12, muted: true));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // footer
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = _ui.Secondary(loc["dialog.cancel"]);
        cancel.Click += (_, _) => Close();
        var run = _ui.Primary(loc[_video ? "media.join.run" : "media.merge.run"]);
        run.Click += (_, _) => Run();
        cancel.Margin = run.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(cancel);
        actions.Children.Add(run);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        _total = _ui.Text("", 12, muted: true);
        _total.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_total);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // the tool's own choices (mode / format)
        var settings = new StackPanel();
        foreach (var option in context.Options.Where(o => o.Kind == ToolOptionKind.Choice && o.Choices is not null))
        {
            var combo = new ComboBox { MinHeight = 30, MinWidth = 220 };
            foreach (var choice in option.Choices!)
                combo.Items.Add(new ComboBoxItem { Content = ChoiceLabel(option, choice), Tag = choice });
            combo.SelectedIndex = Math.Max(0, option.Choices!.ToList().IndexOf(option.Default?.ToString() ?? ""));
            _choices[option.Key] = combo;
            var label = _ui.Text(OptionLabel(option), 13, wrap: false);
            label.Width = 170;
            label.VerticalAlignment = VerticalAlignment.Center;
            settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), Children = { label, combo } });
        }
        if (settings.Children.Count > 0)
        {
            var card = _ui.Card(settings, 12);
            card.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(card, Dock.Bottom);
            root.Children.Add(card);
        }

        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        PreviewKeyDown += OnKey;
        Loaded += async (_, _) => { Rebuild(); await LoadFactsAsync(); };
        Closed += (_, _) => { _life.Cancel(); if (!_finished) _done.TrySetResult(null); };
        Rebuild();
    }

    public Task<OptionsEdit?> Completion => _done.Task;

    internal IReadOnlyList<string> Order => _order.Files;
    internal void MoveForTest(int from, int to) { _order.Move(from, to); Rebuild(); }
    internal void PressRun() => Run();
    internal Task Facts { get; private set; } = Task.CompletedTask;
    internal bool FactsLoaded => _order.Files.All(_facts.ContainsKey);

    private string OptionLabel(ToolOption option)
    {
        foreach (var key in OptionForm.LabelKeys(_context.Operation, option))
            if (_ui.Loc[key] != key) return _ui.Loc[key];
        return option.Key;
    }

    private string ChoiceLabel(ToolOption option, string choice)
    {
        foreach (var key in new[] { $"opt.{_context.Operation}.{option.Key}.{choice}", $"opt.{option.Key}.{choice}" })
            if (_ui.Loc[key] != key) return _ui.Loc[key];
        return choice.ToUpperInvariant();
    }

    private Task LoadFactsAsync() => Facts = LoadFactsCoreAsync();

    private async Task LoadFactsCoreAsync()
    {
        foreach (var file in _order.Files.ToList())
        {
            try
            {
                var info = await MediaPreview.ProbeAsync(_context.Engines, file, _life.Token);
                ImageSource? picture = null;
                float[]? wave = null;
                if (_video && info.HasVideo)
                {
                    var t = Math.Min(1, (info.DurationSeconds ?? 0) / 2);
                    if (await MediaPreview.FrameAsync(_context.Engines, file, t, 72, _life.Token) is { } png) picture = MediaSession.Decode(png);
                }
                else if (info.HasAudio)
                {
                    wave = await MediaPreview.WaveformAsync(_context.Engines, file, 60, _life.Token);
                }
                _facts[file] = (picture, wave, info.DurationSeconds);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is MediaProbeException or MediaPreviewException or IOException)
            {
                FileLog.Info($"join editor could not read {Path.GetFileName(file)}: {ex.Message}");
                _facts[file] = (null, null, null);
            }
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var files = _order.Files;
        for (var i = 0; i < files.Count; i++) _list.Children.Add(MakeRow(i, files[i]));
        var known = files.Select(f => _facts.TryGetValue(f, out var x) ? x.Length : null).ToList();
        _total.Text = known.All(l => l is not null)
            ? _ui.Loc.Format("media.join.total", files.Count, MediaText.Len(_ui.Loc, known.Sum(l => l!.Value)))
            : _ui.Loc.Format("result.files", files.Count);
    }

    private Border MakeRow(int index, string file)
    {
        var loc = _ui.Loc;
        _facts.TryGetValue(file, out var facts);
        FrameworkElement picture;
        if (facts.Wave is { } wave) picture = new MiniWave(wave, _ui.Theme) { Width = 96, Height = 54 };
        else picture = new Image { Source = facts.Picture, Width = 96, Height = 54, Stretch = Stretch.Uniform };
        var pictureBox = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), Width = 96, Height = 54, Child = picture, ClipToBounds = true, FlowDirection = FlowDirection.LeftToRight };

        var number = new TextBlock { Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 26, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = _ui.Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        name.Children.Add(_ui.Text(Path.GetFileName(file), 13, weight: FontWeights.SemiBold, wrap: false));
        name.Children.Add(_ui.Text(facts.Length is { } l ? MediaText.Len(loc, l) : loc["media.loading"], 12, muted: true, wrap: false));

        Button Arrow(string glyph, string tip, int delta)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 },
                Width = 32, Height = 30, Margin = new Thickness(4, 0, 0, 0), ToolTip = loc[tip], Focusable = false,
                Background = _ui.Theme.Wedge, Foreground = _ui.Theme.Text, BorderBrush = _ui.Theme.PanelBorder, BorderThickness = new Thickness(1),
                IsEnabled = index + delta >= 0 && index + delta < _order.Files.Count,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, loc[tip]);
            b.Click += (_, _) => { _order.Move(index, index + delta); _selected = index + delta; Rebuild(); };
            return b;
        }
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        arrows.Children.Add(Arrow("", "media.join.up", -1));
        arrows.Children.Add(Arrow("", "media.join.down", 1));
        var grip = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = _ui.Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 16 };

        var line = new DockPanel();
        DockPanel.SetDock(number, Dock.Left);
        DockPanel.SetDock(pictureBox, Dock.Left);
        DockPanel.SetDock(grip, Dock.Right);
        DockPanel.SetDock(arrows, Dock.Right);
        line.Children.Add(number);
        line.Children.Add(pictureBox);
        line.Children.Add(grip);
        line.Children.Add(arrows);
        line.Children.Add(name);

        var row = new Border
        {
            Child = line, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10),
            Background = _ui.Theme.Panel, BorderThickness = new Thickness(index == _selected ? 2 : 1),
            BorderBrush = index == _selected ? _ui.Theme.Highlight : _ui.Theme.PanelBorder, Cursor = Cursors.SizeNS, Tag = index,
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
            _selected = index;
            _dragFrom = index;
            row.CaptureMouse();
            e.Handled = true;
            Rebuild();
        };
        row.MouseMove += (_, e) =>
        {
            if (_dragFrom < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var target = RowIndexAt(e.GetPosition(_list).Y);
            if (target >= 0 && target != _dragFrom)
            {
                _order.Move(_dragFrom, target);
                _dragFrom = _selected = target;
                Rebuild();
                if (_list.Children[target] is Border moved) moved.CaptureMouse();
            }
        };
        row.MouseLeftButtonUp += (_, _) => { _dragFrom = -1; Mouse.Capture(null); };
        return row;
    }

    private static bool FindButton(DependencyObject d)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x))
            if (x is Button) return true;
        return false;
    }

    private int RowIndexAt(double y)
    {
        var top = 0.0;
        for (var i = 0; i < _list.Children.Count; i++)
        {
            var h = ((FrameworkElement)_list.Children[i]).ActualHeight + 6;
            if (y < top + h) return i;
            top += h;
        }
        return _list.Children.Count - 1;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Close(); e.Handled = true; return; }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || key is not (Key.Up or Key.Down)) return;
        var to = _selected + (key == Key.Up ? -1 : 1);
        if (to < 0 || to >= _order.Files.Count) return;
        _order.Move(_selected, to);
        _selected = to;
        Rebuild();
        e.Handled = true;
    }

    private void Run()
    {
        _finished = true;
        var values = new Dictionary<string, object?>();
        foreach (var (key, combo) in _choices) values[key] = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        _done.TrySetResult(new OptionsEdit(values, [.. _order.Files]));
        Close();
    }

    /// <summary>A small waveform for an audio row.</summary>
    private sealed class MiniWave(float[] peaks, WheelTheme theme) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) =>
            MediaTimelineView.DrawWave(dc, new Rect(4, 4, Math.Max(1, ActualWidth - 8), Math.Max(1, ActualHeight - 8)), peaks, theme.Highlight is SolidColorBrush b ? b.Color : Colors.Cyan);
    }
}
