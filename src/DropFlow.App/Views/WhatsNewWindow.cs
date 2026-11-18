using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DropFlow.App.Views;

/// <summary>
/// "What's new" after an update: a few slides (a glyph, a title, one line), Back / Next (mirrored in Arabic), dots, Esc closes. Shown once
/// per version (<see cref="WhatsNew.ShouldShow"/>); reopened from the tray. Everything is in the app: nothing is downloaded.
/// </summary>
internal sealed class WhatsNewWindow : Window
{
    private readonly UiKit _ui;
    private readonly IReadOnlyList<WhatsNew.Slide> _slides;
    private readonly TextBlock _glyph = new() { FontSize = 54, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 6) };
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 10) };
    private readonly Button _back;
    private readonly Button _next;
    private int _index;

    public WhatsNewWindow(UiKit ui, string version, IReadOnlyList<WhatsNew.Slide> slides)
    {
        _ui = ui;
        _slides = slides;
        ui.Style(this, 460, ui.Loc.Format("whatsnew.title", version));
        _title = ui.Text("", 20, weight: FontWeights.Bold);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.TextAlignment = TextAlignment.Center;
        _text = ui.Text("", 14, muted: true);
        _text.TextAlignment = TextAlignment.Center;
        _text.TextWrapping = TextWrapping.Wrap;
        _title.TextWrapping = TextWrapping.Wrap;
        SizeToContent = SizeToContent.Height;
        _text.MinHeight = 44;
        _glyph.Foreground = ui.Theme.Accent;
        _back = ui.Secondary(ui.Loc["whatsnew.back"]);
        _next = ui.Primary(ui.Loc["whatsnew.next"]);
        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => { if (_index == _slides.Count - 1) Close(); else Go(_index + 1); };
        _next.Margin = new Thickness(8, 0, 0, 0);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            // the arrows follow the reading direction (in Arabic, Left is "next")
            var forward = ui.Loc.IsRightToLeft ? Key.Left : Key.Right;
            var backward = ui.Loc.IsRightToLeft ? Key.Right : Key.Left;
            if (e.Key == forward && _index < _slides.Count - 1) Go(_index + 1);
            if (e.Key == backward && _index > 0) Go(_index - 1);
        };
        Content = new Border { Child = new StackPanel
        {
            Margin = new Thickness(26, 18, 26, 20),
            Children =
            {
                _glyph, _title, _text, _dots,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { _back, _next } },
            },
        } };
        Go(0);
    }

    public int Index => _index;

    public void Go(int index)
    {
        _index = Math.Clamp(index, 0, _slides.Count - 1);
        var s = _slides[_index];
        _glyph.Text = s.Glyph;
        _title.Text = s.Title;
        _text.Text = s.Text;
        _back.IsEnabled = _index > 0;
        _next.Content = _ui.Loc[_index == _slides.Count - 1 ? "whatsnew.done" : "whatsnew.next"];
        _dots.Children.Clear();
        for (var i = 0; i < _slides.Count; i++)
            _dots.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(3, 0, 3, 0), Background = i == _index ? _ui.Theme.Accent : _ui.Theme.Wedge });
        System.Windows.Automation.AutomationProperties.SetName(_dots, $"{_index + 1} / {_slides.Count}");
    }
}

/// <summary>The slides of this version and when to show them.</summary>
internal static class WhatsNew
{
    public sealed record Slide(string Glyph, string Title, string Text);

    /// <summary>Keys of this version's slides in the strings files (whatsnew.{key}.title / .text).</summary>
    private static readonly (string Glyph, string Key)[] Current =
    [
        ("⟳", "flip"),
        ("◔", "ring"),
        ("⇢", "routes"),
        ("★", "presets"),
        ("⌂", "home"),
        ("🌐", "browser"),
    ];

    public static IReadOnlyList<Slide> Slides(Core.Localization.ILocalizer loc) =>
        [.. Current.Select(c => new Slide(c.Glyph, loc[$"whatsnew.{c.Key}.title"], loc[$"whatsnew.{c.Key}.text"]))];

    /// <summary>
    /// Show after an update (a different version than last seen, or none recorded: versions before this setting), never on a first install
    /// (the welcome guide shows then).
    /// </summary>
    public static bool ShouldShow(string currentVersion, string? lastSeen, bool firstRun) =>
        !firstRun && !string.Equals(lastSeen, currentVersion, StringComparison.OrdinalIgnoreCase);
}
