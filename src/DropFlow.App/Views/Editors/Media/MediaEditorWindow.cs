// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Core.Operations;
using DropFlow.Core.Runner;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Numbers inside translated sentences. A length uses the language's unit ("3.5 s", "3.5 ث"). Everything else is left to the bidi
/// algorithm: checked by rendering, an Arabic line shows Arabic-Indic digits with the sign and unit in reading order, and WPF ignores
/// the Unicode embedding controls (LRE/LRI) anyway, so none are inserted.
/// </summary>
internal static class MediaText
{
    public static string Len(Core.Localization.ILocalizer loc, double seconds)
    {
        var text = MediaTimeText.Length(seconds);
        return text.EndsWith(" s", StringComparison.Ordinal) ? text[..^2] + " " + loc["media.unit.seconds"] : text;
    }
}

/// <summary>Everything an editor window gets from the runner: the tool, its declared options, the files, and a scratch folder for previews.</summary>
internal sealed record MediaEditorContext(UiKit Ui, IEngineProvider Engines, string Operation, string Title, IReadOnlyList<string> Files, IReadOnlyList<ToolOption> Options, string Scratch)
{
    public bool Declares(string key) => Options.Any(o => o.Key == key);
}

/// <summary>
/// The frame every single-file media editor shares: title and file facts, the picture (video) with an overlay layer, transport
/// (previous frame, play/pause, next frame, time), the timeline, the editor's own controls, and a footer with the problem line and
/// Cancel / Run. Keys: Space plays or pauses, Left/Right step a frame (with Shift a second), Home/End jump; editors add their own.
/// The window never runs anything: Run completes <see cref="Completion"/> with the option values and closes.
/// </summary>
internal abstract class MediaEditorWindow : Window
{
    private readonly TaskCompletionSource<OptionsEdit?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _message;
    private readonly Button _run;
    private readonly TextBlock _time;
    private readonly Button _play;
    private bool _finished;

    protected MediaEditorWindow(MediaEditorContext context, MediaSession media, string runKey, double stripHeight = 64, double laneHeight = 0, bool showStage = true)
    {
        Context = context;
        Ui = context.Ui;
        Media = media;
        Loc = context.Ui.Loc;
        var ui = Ui;
        var hasStage = showStage && media.HasVideo;

        ui.Style(this, hasStage ? 1000 : 900, context.Title);
        ResizeMode = ResizeMode.CanResize;
        if (hasStage)
        {
            SizeToContent = SizeToContent.Manual;
            Height = 800;
        }
        MinWidth = 660;
        MinHeight = hasStage ? 560 : 360;
        ShowInTaskbar = true;

        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions[1].Height = hasStage ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

        // header (the facts are technical and read left to right in both languages)
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(ui.Text(Path.GetFileName(media.Path), 16, weight: FontWeights.SemiBold, wrap: false));
        var facts = ui.Text(Facts(media), 12, muted: true, wrap: false);
        facts.FlowDirection = FlowDirection.LeftToRight;
        facts.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(facts);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // picture
        Stage = new Grid { Background = Brushes.Black, FlowDirection = FlowDirection.LeftToRight, ClipToBounds = true };
        Overlay = new MediaStageOverlay(media);
        if (hasStage)
        {
            Stage.Children.Add(media.Player);
            Stage.Children.Add(media.Still);
            Stage.Children.Add(Overlay);
            var stageBorder = new Border { CornerRadius = new CornerRadius(12), Background = Brushes.Black, Child = Stage, ClipToBounds = true, MinHeight = 200, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(stageBorder, 1);
            root.Children.Add(stageBorder);
        }
        else
        {
            // Audio has no picture, but the player has to be in the window to play.
            var host = new Grid { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
            host.Children.Add(media.Player);
            Grid.SetRow(host, 1);
            root.Children.Add(host);
        }

        // transport (time runs left to right in every language)
        var transport = new DockPanel { FlowDirection = FlowDirection.LeftToRight, LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(IconButton("", "media.prevFrame", () => media.Step(-1)));
        _play = IconButton("", "media.play", TogglePlay);
        buttons.Children.Add(_play);
        buttons.Children.Add(IconButton("", "media.nextFrame", () => media.Step(1)));
        _time = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Foreground = ui.Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(_time);
        DockPanel.SetDock(buttons, Dock.Left);
        transport.Children.Add(buttons);
        TransportExtras = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = ui.Flow };
        DockPanel.SetDock(TransportExtras, Dock.Right);
        transport.Children.Add(TransportExtras);
        Grid.SetRow(transport, 2);
        root.Children.Add(transport);

        // timeline
        Timeline = new MediaTimelineView(media, ui.Theme, hasStage ? stripHeight : Math.Max(stripHeight, 110), laneHeight) { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(Timeline, 3);
        root.Children.Add(Timeline);

        // the editor's controls
        Panel = new StackPanel();
        var card = ui.Card(Panel, 12);
        card.Margin = new Thickness(0, 0, 0, 8);
        var scroller = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = hasStage ? 260 : double.PositiveInfinity };
        Grid.SetRow(scroller, 4);
        root.Children.Add(scroller);

        // footer
        var footer = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = ui.Secondary(Loc["dialog.cancel"]);
        cancel.Click += (_, _) => Close();
        _run = ui.Primary(Loc[runKey]);
        _run.IsDefault = false;
        _run.Click += (_, _) => Run();
        cancel.Margin = new Thickness(8, 0, 0, 0);
        _run.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(cancel);
        actions.Children.Add(_run);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        _message = ui.Text("", 12, muted: true);
        _message.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_message);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        Content = root;

        media.Changed += OnMediaChanged;
        PreviewKeyDown += OnPreviewKey;
        Loaded += (_, _) => { media.LoadStrip(hasStage ? 14 : 10, 900); OnMediaChanged(); Refresh(); Activate(); };
        Closed += (_, _) =>
        {
            media.Changed -= OnMediaChanged;
            media.Dispose();
            if (!_finished) _done.TrySetResult(null);
            OnClosedEditor();
        };
    }

    protected MediaEditorContext Context { get; }
    protected UiKit Ui { get; }
    protected Core.Localization.ILocalizer Loc { get; }
    protected MediaSession Media { get; }
    protected MediaTimelineView Timeline { get; }
    protected Grid Stage { get; }
    protected MediaStageOverlay Overlay { get; }
    protected StackPanel Panel { get; }
    protected StackPanel TransportExtras { get; }

    /// <summary>The option values when Run was pressed, or null when the window was closed without running.</summary>
    public Task<OptionsEdit?> Completion => _done.Task;

    /// <summary>A string key that says why Run is not possible yet, or null.</summary>
    protected abstract string? Problem { get; }

    /// <summary>The option values for the tool (only asked when <see cref="Problem"/> is null).</summary>
    protected abstract Dictionary<string, object?> Values();

    /// <summary>A short helpful line shown when there is no problem (the new length, what will be produced...).</summary>
    protected virtual string? Note => null;

    /// <summary>What Space plays: by default from the playhead to the end.</summary>
    protected virtual void TogglePlay() => Media.TogglePlay();

    /// <summary>Editor keys (after the shared ones). Return true when handled.</summary>
    protected virtual bool OnKey(Key key, ModifierKeys modifiers) => false;

    protected virtual void OnClosedEditor() { }

    // ------------------------------------------------------------------------------------------------------------------ test hooks

    internal MediaSession Session => Media;
    internal string? CurrentProblem => Problem;
    internal Dictionary<string, object?>? CurrentValues => Problem is null ? Values() : null;
    internal void PressRun() => Run();
    internal string MessageText => _message.Text;

    // ------------------------------------------------------------------------------------------------------------------ shared behaviour

    /// <summary>Re-checks the state: enables Run, shows the problem or the note, redraws the timeline and the picture overlay.</summary>
    protected void Refresh()
    {
        var problem = Problem;
        _run.IsEnabled = problem is null;
        _message.Text = problem is not null ? Loc[problem] : Note ?? "";
        _message.Foreground = problem is not null ? Brushes.IndianRed : Ui.Theme.TextMuted;
        Timeline.InvalidateVisual();
        Overlay.InvalidateVisual();
    }

    private void Run()
    {
        if (Problem is not null) { Refresh(); return; }
        _finished = true;
        Media.Pause();
        _done.TrySetResult(new OptionsEdit(Values(), ResultFiles));
        Close();
    }

    /// <summary>A changed file order (join, merge); null keeps the selection.</summary>
    protected virtual IReadOnlyList<string>? ResultFiles => null;

    private void OnMediaChanged()
    {
        _time.Text = $"{MediaTimeText.Clock(Media.Current)} / {MediaTimeText.Clock(Media.Duration)}";
        ((TextBlock)_play.Content).Text = Media.IsPlaying ? "" : "";
        _play.ToolTip = Loc[Media.IsPlaying ? "media.pause" : "media.play"];
        _play.IsEnabled = Media.CanPlay;
        if (Media.UsesStills && string.IsNullOrEmpty(_message.Text)) _message.Text = Loc["media.stillsOnly"];
        OnPlayheadMoved();
    }

    /// <summary>Called whenever the time changes (playback, scrubbing).</summary>
    protected virtual void OnPlayheadMoved() { }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        // A focused field, slider, list or button keeps its own keys (typing, arrows, Space to press); only Escape still closes.
        if (Keyboard.FocusedElement is TextBox or ComboBox or Slider or ButtonBase or ComboBoxItem)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            return;
        }
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: if (shift) Media.Seek(Media.Current - 1); else Media.Step(-1); break;
            case Key.Right: if (shift) Media.Seek(Media.Current + 1); else Media.Step(1); break;
            case Key.Home: Media.Seek(0); break;
            case Key.End: Media.Seek(Media.Duration); break;
            case Key.Escape: Close(); break;
            default:
                if (!OnKey(e.Key, Keyboard.Modifiers)) return;
                break;
        }
        e.Handled = true;
    }

    private static string Facts(MediaSession media)
    {
        var parts = new List<string> { MediaTimeText.Length(media.Duration) };
        if (media.HasVideo && media.DisplayWidth > 0) parts.Add($"{media.DisplayWidth}×{media.DisplayHeight}");
        if (media.Info.Video?.CodecName is { } v) parts.Add(v);
        if (media.Info.Audio is { } a)
        {
            parts.Add(a.CodecName ?? "audio");
            if (a.Channels is { } ch) parts.Add(ch == 1 ? "mono" : ch == 2 ? "stereo" : ch.ToString(CultureInfo.InvariantCulture) + " ch");
        }
        return string.Join("  ·  ", parts);
    }

    // ------------------------------------------------------------------------------------------------------------------ small builders

    protected Button IconButton(string glyph, string tooltipKey, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15 },
            Width = 36, Height = 32, Margin = new Thickness(0, 0, 6, 0), ToolTip = Loc[tooltipKey],
            Background = Ui.Theme.Wedge, Foreground = Ui.Theme.Text, BorderBrush = Ui.Theme.PanelBorder, BorderThickness = new Thickness(1),
            Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, Loc[tooltipKey]);
        b.Click += (_, _) => click();
        return b;
    }

    protected Button SmallButton(string textKey, Action click, bool primary = false)
    {
        var b = primary ? Ui.Primary(Loc[textKey]) : Ui.Secondary(Loc[textKey]);
        b.MinWidth = 0;
        b.MinHeight = 30;
        b.Padding = new Thickness(10, 2, 10, 2);
        b.Margin = new Thickness(0, 0, 6, 0);
        b.Focusable = false;
        b.Click += (_, _) => click();
        return b;
    }

    protected CheckBox Check(string textKey, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = Loc[textKey], IsChecked = value, Foreground = Ui.Theme.Text, Margin = new Thickness(0, 4, 16, 4), VerticalAlignment = VerticalAlignment.Center };
        c.Click += (_, _) => { changed(c.IsChecked == true); Refresh(); };
        return c;
    }

    protected RadioButton Radio(string group, string textKey, bool value, Action changed)
    {
        var r = new RadioButton { GroupName = group, Content = Loc[textKey], IsChecked = value, Foreground = Ui.Theme.Text, Margin = new Thickness(0, 4, 16, 4) };
        r.Checked += (_, _) => { changed(); Refresh(); };
        return r;
    }

    /// <summary>A labelled slider with a value readout that always reads left to right.</summary>
    protected (FrameworkElement Row, Slider Slider) LabeledSlider(string labelKey, double min, double max, double value, double tick, Func<double, string> format, Action<double> changed)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = tick, IsSnapToTickEnabled = tick > 0, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        var readout = new TextBlock { Text = format(value), Width = 86, Foreground = Ui.Theme.Text, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, TextAlignment = Loc.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left };
        slider.ValueChanged += (_, e) => { readout.Text = format(e.NewValue); changed(e.NewValue); Refresh(); };
        var label = Ui.Text(Loc[labelKey], 13, wrap: false);
        label.Width = 170;
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(readout, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(readout);
        row.Children.Add(slider);
        return (row, slider);
    }

    protected static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    protected TextBlock Label(string key, double width = 0)
    {
        var t = Ui.Text(Loc[key], 13, wrap: false);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(0, 0, 8, 0);
        if (width > 0) t.Width = width;
        return t;
    }
}

/// <summary>A time typed by hand: shows a clock, reads seconds or clock times (Arabic digits too), commits on Enter or leaving the field.</summary>
internal sealed class MediaTimeBox : TextBox
{
    private readonly Action<double> _committed;
    private double _value;

    public MediaTimeBox(double value, Action<double> committed)
    {
        _committed = committed;
        FlowDirection = FlowDirection.LeftToRight;
        FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Width = 96;
        MinHeight = 28;
        Padding = new Thickness(4, 2, 4, 2);
        VerticalContentAlignment = VerticalAlignment.Center;
        Value = value;
        LostKeyboardFocus += (_, _) => Commit();
        KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
    }

    public double Value
    {
        get => _value;
        set { _value = value; if (!IsKeyboardFocusWithin) Text = MediaTimeText.Clock(value); }
    }

    /// <summary>Reads what was typed; an unreadable time is put back to the last good value.</summary>
    private void Commit()
    {
        if (MediaTimeText.TryParse(Text, out var seconds)) { _value = seconds; _committed(seconds); }
        Text = MediaTimeText.Clock(_value);
    }

    /// <summary>Test hook: types text and commits it as Enter would.</summary>
    internal void Type(string text)
    {
        Text = text;
        Commit();
    }
}

/// <summary>
/// The layer over the picture where crop and redact draw their boxes. Converts between screen points and pixels of the displayed
/// picture (the video is letterboxed inside the stage), and hands the pointer to the editor in picture pixels.
/// </summary>
internal sealed class MediaStageOverlay : FrameworkElement
{
    private readonly MediaSession _media;

    public MediaStageOverlay(MediaSession media)
    {
        _media = media;
        FlowDirection = FlowDirection.LeftToRight;
        Cursor = Cursors.Cross;
    }

    /// <summary>Draws in screen coordinates; use <see cref="ToScreen"/> for picture boxes.</summary>
    public Action<DrawingContext>? Draw { get; set; }
    public Action<Point>? Pressed { get; set; }
    public Action<Point>? Dragged { get; set; }
    public Action<Point>? Released { get; set; }
    public Func<Point, Cursor?>? CursorAt { get; set; }

    /// <summary>Where the picture sits inside the stage (uniform fit, centred).</summary>
    public Rect PictureRect
    {
        get
        {
            double fw = Math.Max(1, _media.DisplayWidth), fh = Math.Max(1, _media.DisplayHeight);
            var scale = Math.Min(ActualWidth / fw, ActualHeight / fh);
            if (double.IsNaN(scale) || scale <= 0) return Rect.Empty;
            var w = fw * scale;
            var h = fh * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }

    /// <summary>Screen pixels per picture pixel.</summary>
    public double Scale => PictureRect.IsEmpty ? 1 : PictureRect.Width / Math.Max(1, _media.DisplayWidth);

    public Point ToPicture(Point screen)
    {
        var r = PictureRect;
        return r.IsEmpty ? new Point() : new Point((screen.X - r.X) / Scale, (screen.Y - r.Y) / Scale);
    }

    public Rect ToScreen(PixelBox box)
    {
        var r = PictureRect;
        return r.IsEmpty ? Rect.Empty : new Rect(r.X + box.X * Scale, r.Y + box.Y * Scale, box.W * Scale, box.H * Scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        Draw?.Invoke(dc);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Pressed is null) return;
        Pressed(ToPicture(e.GetPosition(this)));
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = ToPicture(e.GetPosition(this));
        if (IsMouseCaptured) { Dragged?.Invoke(p); InvalidateVisual(); }
        else if (CursorAt?.Invoke(p) is { } c) Cursor = c;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Released?.Invoke(ToPicture(e.GetPosition(this)));
        InvalidateVisual();
    }
}
