using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DropFlow.Core.History;
using DropFlow.Core.Operations;
using DropFlow.Core.Settings;
using DropFlow.Engines.Common;

namespace DropFlow.App.Views;

/// <summary>"Installing FFmpeg…" with the winget output scrolling by and a Cancel button.</summary>
internal sealed class InstallWindow : Window
{
    private readonly TextBlock _line;
    private readonly CancellationTokenSource _cts = new();

    public InstallWindow(UiKit ui, EngineId engine)
    {
        ui.Style(this, 440, ui.Loc.Format("engine.installing", ui.Loc["engine.name." + engine]));
        Topmost = true;
        _line = ui.Text("", 11, muted: true);
        var cancel = ui.Secondary(ui.Loc["dialog.cancel"]);
        cancel.Click += (_, _) => _cts.Cancel();
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        stack.Children.Add(ui.Text(Title, 16, weight: FontWeights.SemiBold));
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 6, 0, 10), Foreground = ui.Theme.Accent, Background = ui.Theme.Wedge, BorderThickness = new Thickness(0) });
        stack.Children.Add(_line);
        stack.Children.Add(cancel);
        Content = stack;
    }

    public CancellationToken Token => _cts.Token;

    public void Log(string line)
    {
        if (!string.IsNullOrWhiteSpace(line)) _line.Text = line.Trim();
    }
}

/// <summary>What DropFlow did recently, with "Open folder" and "Undo".</summary>
internal sealed class HistoryWindow : Window
{
    private readonly UiKit _ui;
    private readonly HistoryStore _history;
    private readonly IFileDisposer _disposer;
    private readonly StackPanel _list = new();

    public HistoryWindow(UiKit ui, HistoryStore history, IFileDisposer disposer)
    {
        _ui = ui;
        _history = history;
        _disposer = disposer;
        ui.Style(this, 560, ui.Loc["history.title"]);
        SizeToContent = SizeToContent.Manual;
        Height = 520;
        ResizeMode = ResizeMode.CanResize;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(18) };
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var entries = _history.Entries;
        if (entries.Count == 0)
        {
            _list.Children.Add(_ui.Text(_ui.Loc["history.empty"], muted: true));
            return;
        }

        foreach (var e in entries.Take(100))
        {
            var row = new StackPanel();
            var name = e.Inputs.Count > 0 ? Path.GetFileName(e.Inputs[0]) + (e.Inputs.Count > 1 ? $" +{e.Inputs.Count - 1}" : "") : "";
            row.Children.Add(_ui.Text(name, 14, weight: FontWeights.SemiBold, wrap: false));
            var status = e.Undone ? _ui.Loc["history.undone"] : e.Success ? e.Operation : $"{_ui.Loc["history.failed"]}: {e.Error}";
            row.Children.Add(_ui.Text($"{status} · {e.Time.LocalDateTime:g}", 12, muted: true));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            if (e.Success && e.Outputs.Count > 0)
            {
                var open = _ui.Secondary(_ui.Loc["history.open"]);
                open.Margin = new Thickness(0, 0, 8, 0);
                open.Click += (_, _) => Explorer.Reveal(e.Outputs[0]);
                buttons.Children.Add(open);
                if (!e.Undone)
                {
                    var undo = _ui.Secondary(_ui.Loc["history.undo"]);
                    undo.Click += (_, _) => { _history.Undo(e.Id, _disposer); Rebuild(); };
                    buttons.Children.Add(undo);
                }
            }
            row.Children.Add(buttons);
            _list.Children.Add(_ui.Card(row, 12));
        }
    }
}

internal static class Explorer
{
    /// <summary>Opens Explorer with the file selected (or the folder opened).</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", ["\"" + path + "\""]) { UseShellExecute = true });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* nothing to open */ }
    }
}

/// <summary>Settings: language, wheel keys, startup, right-click menu, shortcut, engines.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly UiKit _ui;
    private readonly Func<AppSettings, IReadOnlyList<string>> _apply;
    private readonly Func<EngineId, Task<bool>> _install;
    private readonly Func<EngineId, bool> _isInstalled;
    private readonly ComboBox _language = new();
    private readonly ComboBox[] _keys = [new(), new(), new()];
    private readonly CheckBox _startup;
    private readonly CheckBox _menu;
    private readonly TextBox _hotkey = new() { MinHeight = 32, Padding = new Thickness(6, 5, 6, 5) };
    private readonly TextBlock _message;

    public SettingsWindow(UiKit ui, AppSettings current, bool startupOn, bool menuOn, Func<AppSettings, bool, bool, IReadOnlyList<string>> apply,
        IReadOnlyList<EngineId> engines, Func<EngineId, bool> isInstalled, Func<EngineId, Task<bool>> install, string version)
    {
        _ui = ui;
        _apply = s => apply(s, _startup!.IsChecked == true, _menu!.IsChecked == true);
        _install = install;
        _isInstalled = isInstalled;
        ui.Style(this, 520, ui.Loc["settings.title"]);
        SizeToContent = SizeToContent.Manual;
        Height = 720;

        var root = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };

        // language
        root.Children.Add(ui.SectionTitle(ui.Loc["settings.language"]));
        foreach (var (tag, key) in new[] { ("system", "settings.language.system"), ("ar", "settings.language.ar"), ("en", "settings.language.en") })
            _language.Items.Add(new ComboBoxItem { Content = ui.Loc[key], Tag = tag });
        _language.SelectedItem = _language.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current.Language) ?? _language.Items[0];
        _language.MinHeight = 32;
        var langStack = new StackPanel { Children = { _language, ui.Text(ui.Loc["settings.restartNote"], 11, muted: true) } };
        root.Children.Add(ui.Card(langStack));

        // keys
        root.Children.Add(ui.SectionTitle(ui.Loc["settings.keys"]));
        var keysPanel = new StackPanel();
        keysPanel.Children.Add(ui.Text(ui.Loc["settings.keys.hint"], 12, muted: true));
        var wheels = new[] { ("settings.key.convert", current.Keys.Convert), ("settings.key.tools", current.Keys.Tools), ("settings.key.actions", current.Keys.Actions) };
        for (var i = 0; i < 3; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var label = ui.Text(ui.Loc[wheels[i].Item1], 14, wrap: false);
            label.Width = 120;
            row.Children.Add(label);
            foreach (var k in Enum.GetValues<TriggerKey>()) _keys[i].Items.Add(new ComboBoxItem { Content = k.ToString(), Tag = k });
            _keys[i].SelectedItem = _keys[i].Items.OfType<ComboBoxItem>().First(c => (TriggerKey)c.Tag == wheels[i].Item2);
            _keys[i].MinHeight = 30;
            row.Children.Add(_keys[i]);
            keysPanel.Children.Add(row);
        }
        root.Children.Add(ui.Card(keysPanel));

        // system
        _startup = new CheckBox { Content = ui.Loc["settings.startup"], IsChecked = startupOn, Foreground = ui.Theme.Text, Margin = new Thickness(0, 2, 0, 8) };
        _menu = new CheckBox { Content = ui.Loc["settings.contextMenu"], IsChecked = menuOn, Foreground = ui.Theme.Text, Margin = new Thickness(0, 0, 0, 8) };
        _hotkey.Text = current.GlobalHotkey;
        var sys = new StackPanel { Children = { _startup, _menu, ui.Text(ui.Loc["settings.hotkey"], 12, muted: true), _hotkey } };
        root.Children.Add(ui.Card(sys));

        // engines
        root.Children.Add(ui.SectionTitle(ui.Loc["settings.engines"]));
        var eng = new StackPanel { Children = { ui.Text(ui.Loc["settings.engines.hint"], 12, muted: true) } };
        foreach (var e in engines) eng.Children.Add(EngineRow(e));
        root.Children.Add(ui.Card(eng));

        // footer
        _message = ui.Text("", 12);
        var save = ui.Primary(ui.Loc["dialog.save"]);
        save.Click += (_, _) => Save(current);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(_message);
        root.Children.Add(save);
        root.Children.Add(ui.Text($"{ui.Loc.Format("settings.version", version)} · {ui.Loc["settings.license"]}", 11, muted: true));

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement EngineRow(EngineId engine)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var name = _ui.Text(_ui.Loc["engine.name." + engine], 14, wrap: false);
        var status = _ui.Text(_ui.Loc[_isInstalled(engine) ? "settings.engine.installed" : "settings.engine.missing"], 12, muted: true, wrap: false);
        status.Margin = new Thickness(10, 3, 0, 0);
        var button = _ui.Secondary(_ui.Loc["settings.engine.install"]);
        button.MinWidth = 70;
        button.Visibility = _isInstalled(engine) ? Visibility.Collapsed : Visibility.Visible;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var ok = await _install(engine);
            button.IsEnabled = true;
            status.Text = _ui.Loc[ok ? "settings.engine.installed" : "settings.engine.missing"];
            button.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        };
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(name);
        row.Children.Add(status);
        return row;
    }

    private void Save(AppSettings current)
    {
        var updated = current with
        {
            Language = (string)((ComboBoxItem)_language.SelectedItem).Tag,
            Keys = new WheelKeys
            {
                Convert = (TriggerKey)((ComboBoxItem)_keys[0].SelectedItem).Tag,
                Tools = (TriggerKey)((ComboBoxItem)_keys[1].SelectedItem).Tag,
                Actions = (TriggerKey)((ComboBoxItem)_keys[2].SelectedItem).Tag,
            },
            GlobalHotkey = _hotkey.Text.Trim(),
        };
        var errors = _apply(updated);
        _message.Foreground = errors.Count == 0 ? _ui.Theme.Highlight : System.Windows.Media.Brushes.IndianRed;
        _message.Text = errors.Count == 0
            ? _ui.Loc["settings.saved"]
            : string.Join("  ", errors.Select(e => _ui.Loc["settings." + e]));
    }
}
