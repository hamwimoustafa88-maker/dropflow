// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.Core.Settings;

namespace DropFlow.App.Views;

/// <summary>What the welcome guide needs from the host.</summary>
/// <param name="Apply">Saves the preferences page: (language "system"|"ar"|"en", start with Windows).</param>
internal sealed record WelcomeContext(
    string PracticePhoto, WheelKeys Keys, string Language, bool StartupOn, string ClipboardHotkey, bool Paused, Action<string, bool> Apply);

/// <summary>
/// The first-run guide (also in the tray menu): what DropFlow is, a practice round with a real file to drag (the Shift/Ctrl/Alt keycaps
/// light up while held and a tick appears when the wheel really opens), the main preferences, and where to find things afterwards.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    public const int PageCount = 4;

    private readonly UiKit _ui;
    private readonly WelcomeContext _ctx;
    private readonly ContentControl _page = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _skip;
    private readonly Dictionary<TriggerKey, Border> _caps = [];
    private readonly DispatcherTimer _keys;
    private readonly ComboBox _language = new() { MinHeight = 32, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _startup;
    private TextBlock? _status;
    private bool? _forcedShift, _forcedCtrl, _forcedAlt;
    private Point _dragFrom;
    private bool _pressed;

    public WelcomeWindow(UiKit ui, WelcomeContext ctx)
    {
        _ui = ui;
        _ctx = ctx;
        ui.Style(this, 660, ui.Loc["welcome.title"]);
        SizeToContent = SizeToContent.Manual;
        Height = 600;

        _back = ui.Secondary(ui.Loc["welcome.back"]);
        _skip = ui.Secondary(ui.Loc["welcome.skip"]);
        _next = ui.Primary(ui.Loc["welcome.next"]);
        _back.Click += (_, _) => GoTo(Page - 1);
        _skip.Click += (_, _) => Close();
        _next.Click += (_, _) => { if (Page == PageCount - 1) Finish(); else GoTo(Page + 1); };

        foreach (var (tag, key) in new[] { ("system", "settings.language.system"), ("ar", "settings.language.ar"), ("en", "settings.language.en") })
            _language.Items.Add(new ComboBoxItem { Content = ui.Loc[key], Tag = tag });
        _language.SelectedItem = _language.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == ctx.Language) ?? _language.Items[0];
        _startup = new CheckBox { Content = ui.Loc["welcome.p3.startup"], IsChecked = ctx.StartupOn, Foreground = ui.Theme.Text, Margin = new Thickness(0, 14, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { _skip, _back, _next }) { b.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(b); }
        var footer = new DockPanel { Margin = new Thickness(28, 12, 28, 18) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_dots);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        _page.Margin = new Thickness(32, 26, 32, 0);
        root.Children.Add(_page);
        Content = root;

        _keys = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
        _keys.Tick += (_, _) => RefreshKeys();
        Closed += (_, _) => _keys.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        GoTo(0);
    }

    public int Page { get; private set; }

    /// <summary>True once the wheel has opened while this guide was showing.</summary>
    public bool WheelOpened { get; private set; }

    /// <summary>Called by the host when the wheel appears.</summary>
    public void MarkWheelOpened()
    {
        WheelOpened = true;
        UpdateStatus();
    }

    /// <summary>Snapshot and test hook: shows keys as held without touching the keyboard.</summary>
    internal void ShowKeys(bool shift, bool ctrl, bool alt)
    {
        (_forcedShift, _forcedCtrl, _forcedAlt) = (shift, ctrl, alt);
        RefreshKeys();
    }

    internal bool IsLit(TriggerKey key) => _caps.TryGetValue(key, out var cap) && cap.Background == _ui.Theme.Accent;

    public void GoTo(int page)
    {
        Page = Math.Clamp(page, 0, PageCount - 1);
        _caps.Clear();
        _status = null;
        _page.Content = Page switch
        {
            0 => Intro(),
            1 => Practice(),
            2 => Preferences(),
            _ => Done(),
        };
        _back.Visibility = Page == 0 ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = Page == PageCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = _ui.Loc[Page == PageCount - 1 ? "welcome.finish" : "welcome.next"];
        _dots.Children.Clear();
        for (var i = 0; i < PageCount; i++)
            _dots.Children.Add(new Border { Width = i == Page ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0), Background = i == Page ? _ui.Theme.Accent : _ui.Theme.PanelBorder });
        if (Page is 0 or 1) _keys.Start(); else _keys.Stop();
        RefreshKeys();
    }

    private void Finish()
    {
        ApplyPreferences();
        Close();
    }

    private void ApplyPreferences() =>
        _ctx.Apply((string)((ComboBoxItem)_language.SelectedItem).Tag, _startup.IsChecked == true);

    // ------------------------------------------------------------------------------------------------------------ pages

    private TextBlock Heading(string key)
    {
        var t = _ui.Text(_ui.Loc[key], 26, weight: FontWeights.Bold);
        t.Margin = new Thickness(0, 0, 0, 10);
        return t;
    }

    private UIElement Intro()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("welcome.p1.title"));
        panel.Children.Add(_ui.Text(_ui.Loc["welcome.p1.text"], 15, muted: true));
        panel.Children.Add(KeyRow(withLabels: true));
        return panel;
    }

    private UIElement Practice()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("welcome.p2.title"));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(PhotoTile());

        var steps = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
        var convertKey = KeyName(_ctx.Keys.Convert);
        string[] lines = [_ui.Loc["welcome.p2.step1"], _ui.Loc.Format("welcome.p2.step2", convertKey), _ui.Loc["welcome.p2.step3"]];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var number = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = _ui.Theme.Wedge, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentUICulture), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = _ui.Theme.Accent, FontWeight = FontWeights.SemiBold },
            };
            line.Children.Add(number);
            line.Children.Add(_ui.Text(lines[i], 14));
            steps.Children.Add(line);
        }
        Grid.SetColumn(steps, 1);
        row.Children.Add(steps);
        panel.Children.Add(row);

        panel.Children.Add(KeyRow(withLabels: false));
        var hint = _ui.Text(_ui.Loc["welcome.p2.keysHint"], 12, muted: true);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(hint);
        _status = _ui.Text("", 15, weight: FontWeights.SemiBold);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_status);
        UpdateStatus();
        return panel;
    }

    private UIElement PhotoTile()
    {
        var image = new Image { Stretch = Stretch.UniformToFill, Height = 166 };
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 500;
            bitmap.UriSource = new Uri(_ctx.PracticePhoto);
            bitmap.EndInit();
            image.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            Services.FileLog.Error("welcome: practice photo", ex);
        }
        image.FlowDirection = FlowDirection.LeftToRight;

        var caption = _ui.Text(_ui.Loc["welcome.p2.photo"] + " · " + Path.GetFileName(_ctx.PracticePhoto), 12, muted: true, wrap: false);
        caption.Margin = new Thickness(10, 6, 10, 8);
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var tile = new Border
        {
            CornerRadius = new CornerRadius(12), BorderBrush = _ui.Theme.PanelBorder, BorderThickness = new Thickness(1), Background = _ui.Theme.Panel,
            Cursor = Cursors.Hand, ClipToBounds = true, ToolTip = _ui.Loc["welcome.p2.step1"],
            Child = new StackPanel { Children = { new Border { CornerRadius = new CornerRadius(11, 11, 0, 0), ClipToBounds = true, Child = image }, caption } },
        };
        tile.PreviewMouseLeftButtonDown += (_, e) => { _pressed = true; _dragFrom = e.GetPosition(this); };
        tile.PreviewMouseLeftButtonUp += (_, _) => _pressed = false;
        tile.PreviewMouseMove += (_, e) =>
        {
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragFrom.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = false;
            if (!File.Exists(_ctx.PracticePhoto)) return;
            DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { _ctx.PracticePhoto }), DragDropEffects.Copy);
        };
        return tile;
    }

    private void UpdateStatus()
    {
        if (_status is null) return;
        if (WheelOpened)
        {
            _status.Text = _ui.Loc["welcome.p2.opened"];
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        }
        else
        {
            _status.Text = _ui.Loc[_ctx.Paused ? "welcome.p2.paused" : "welcome.p2.waiting"];
            _status.Foreground = _ui.Theme.TextMuted;
        }
    }

    private UIElement Preferences()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("welcome.p3.title"));
        panel.Children.Add(_ui.SectionTitle(_ui.Loc["welcome.p3.language"]));
        Detach(_language);
        panel.Children.Add(_language);
        panel.Children.Add(_ui.Text(_ui.Loc["welcome.p3.languageNote"], 12, muted: true));
        Detach(_startup);
        panel.Children.Add(_startup);
        return panel;
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
    }

    private UIElement Done()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("welcome.p4.title"));
        panel.Children.Add(_ui.Text(_ui.Loc["welcome.p4.text"], 15, muted: true));
        foreach (var tip in new[] { _ui.Loc["welcome.p4.tip1"], _ui.Loc.Format("welcome.p4.tip2", _ctx.ClipboardHotkey), _ui.Loc["welcome.p4.tip3"] })
        {
            var t = _ui.Text("•  " + tip, 14);
            t.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(t);
        }
        return panel;
    }

    // ------------------------------------------------------------------------------------------------------------ keycaps

    private static string KeyName(TriggerKey key) => key.ToString();

    private UIElement KeyRow(bool withLabels)
    {
        var row = new UniformGrid3 { Margin = new Thickness(0, 22, 0, 6) };
        foreach (var (key, label) in new[] { (_ctx.Keys.Convert, "welcome.key.convert"), (_ctx.Keys.Tools, "welcome.key.tools"), (_ctx.Keys.Actions, "welcome.key.actions") })
        {
            var cap = new Border
            {
                MinWidth = 110, Height = 56, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1, 1, 1, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock { Text = KeyName(key), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight },
            };
            _caps[key] = cap;
            var cell = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
            cell.Children.Add(cap);
            if (withLabels)
            {
                var text = _ui.Text(_ui.Loc[label], 13, muted: true);
                text.TextAlignment = TextAlignment.Center;
                text.Margin = new Thickness(0, 8, 0, 0);
                cell.Children.Add(text);
            }
            row.Children.Add(cell);
        }
        return row;
    }

    private void RefreshKeys()
    {
        var shift = _forcedShift ?? Down(0x10);
        var ctrl = _forcedCtrl ?? Down(0x11);
        var alt = _forcedAlt ?? Down(0x12);
        foreach (var (key, cap) in _caps)
        {
            var lit = key switch { TriggerKey.Shift => shift, TriggerKey.Ctrl => ctrl, _ => alt };
            cap.Background = lit ? _ui.Theme.Accent : _ui.Theme.Panel;
            cap.BorderBrush = lit ? _ui.Theme.Accent : _ui.Theme.PanelBorder;
            if (cap.Child is TextBlock t) t.Foreground = lit ? _ui.Theme.TextOnAccent : _ui.Theme.Text;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Three equal columns that follow the window's direction.</summary>
    private sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid3() { Rows = 1; Columns = 3; }
    }
}
