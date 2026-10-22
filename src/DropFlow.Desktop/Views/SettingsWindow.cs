// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DropFlow.Core.Operations;
using DropFlow.Core.Settings;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Drag;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.Views;

/// <summary>One helper program as Settings lists it.</summary>
public sealed record EngineRow(EngineId Engine, string Name, bool Installed, bool Bundled, string? InstallCommand);

/// <summary>Everything the settings window shows and how it saves (built by the controller).</summary>
public sealed record SettingsContext(
    AppSettings Shared,
    DesktopSettings Desktop,
    HostSession Session,
    IReadOnlyList<EngineRow> Engines,
    Func<GnomeExtensionState> ExtensionState,
    Func<GnomeExtensionState> InstallExtension,
    Func<string, Task> CopyText,
    Func<AppSettings, DesktopSettings, IReadOnlyList<string>> Save,
    string Version);

public sealed class SettingsWindow : Window
{
    private readonly DesktopLocalizer _loc;
    private readonly SettingsContext _ctx;
    private readonly ComboBox _language = new() { MinWidth = 200 };
    private readonly ComboBox _output = new() { MinWidth = 200 };
    private readonly CheckBox _login = new();
    private readonly CheckBox _fileManagers = new();
    private readonly ComboBox[] _keys = [new() { MinWidth = 150 }, new() { MinWidth = 150 }, new() { MinWidth = 150 }];
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _extensionState = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(DesktopLocalizer loc, SettingsContext ctx)
    {
        _loc = loc;
        _ctx = ctx;
        this.Styled(loc, loc["settings.title"]);
        Width = 600;
        Height = 700;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(24, 18) };
        body.Children.Add(new TextBlock { Text = loc["settings.title"], FontSize = 20, FontWeight = FontWeight.SemiBold });

        // ---- general
        body.Children.Add(Kit.Heading(loc["settings.general"]));
        foreach (var (value, key) in new[] { ("system", "settings.language.system"), ("ar", "settings.language.ar"), ("en", "settings.language.en") })
            _language.Items.Add(new ComboBoxItem { Content = loc[key], Tag = value });
        _language.SelectedItem = _language.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == ctx.Shared.Language) ?? _language.Items[0];
        body.Children.Add(Row(loc["settings.language"], _language));
        body.Children.Add(Kit.Muted(loc["settings.restartNote"]));

        foreach (var location in Enum.GetValues<OutputLocation>())
            _output.Items.Add(new ComboBoxItem { Content = loc["settings.output." + location], Tag = location });
        _output.SelectedItem = _output.Items.OfType<ComboBoxItem>().First(i => (OutputLocation)i.Tag! == ctx.Desktop.Output);
        body.Children.Add(Row(loc["settings.output"], _output));
        body.Children.Add(Kit.Muted(loc["settings.output.hint"]));

        _login.Content = loc["settings.login"];
        _login.IsChecked = ctx.Desktop.StartAtLogin;
        body.Children.Add(_login);
        if (ctx.Session.IsLinux)
        {
            _fileManagers.Content = loc["settings.fileManagers"];
            _fileManagers.IsChecked = ctx.Desktop.FileManagerEntries;
            body.Children.Add(_fileManagers);
        }

        // ---- wheel keys
        body.Children.Add(Kit.Heading(loc["settings.keys"]));
        body.Children.Add(Kit.Muted(loc["settings.keys.hint"]));
        var current = new[] { ctx.Shared.Keys.Convert, ctx.Shared.Keys.Tools, ctx.Shared.Keys.Actions };
        var labels = new[] { "settings.key.convert", "settings.key.tools", "settings.key.actions" };
        for (var i = 0; i < 3; i++)
        {
            foreach (var key in Enum.GetValues<TriggerKey>())
                _keys[i].Items.Add(new ComboBoxItem { Content = ModifierMap.KeyName(key, ctx.Session.IsMac), Tag = key, FlowDirection = FlowDirection.LeftToRight });
            _keys[i].SelectedItem = _keys[i].Items.OfType<ComboBoxItem>().First(c => (TriggerKey)c.Tag! == current[i]);
            body.Children.Add(Row(loc[labels[i]], _keys[i]));
        }

        // ---- GNOME Shell extension
        if (ctx.ExtensionState() != GnomeExtensionState.NotNeeded)
        {
            body.Children.Add(Kit.Heading(loc["settings.extension"]));
            var install = Kit.Secondary(loc["settings.extension.install"]);
            install.Click += (_, _) => ShowExtension(ctx.InstallExtension());
            var row = new DockPanel();
            DockPanel.SetDock(install, Dock.Right);
            row.Children.Add(install);
            row.Children.Add(_extensionState);
            body.Children.Add(row);
            ShowExtension(ctx.ExtensionState());
        }

        // ---- helper programs
        body.Children.Add(Kit.Heading(loc["settings.engines.unix"]));
        body.Children.Add(Kit.Muted(loc["settings.engines.unixHint"]));
        foreach (var engine in ctx.Engines) body.Children.Add(EngineLine(engine));

        // ---- about
        body.Children.Add(Kit.Heading(loc["settings.about"]));
        body.Children.Add(Kit.Muted(loc.Format("settings.version", ctx.Version)));
        body.Children.Add(Kit.Muted(loc["settings.license"]));

        var save = Kit.Primary(loc["dialog.save"]);
        save.Click += (_, _) => Save();
        var close = Kit.Secondary(loc["dialog.close"]);
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Margin = new Thickness(24, 0, 24, 16) };
        footer.Children.Add(_status);
        footer.Children.Add(Kit.ButtonRow(close, save));

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
    }

    private static Control Row(string label, Control control)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4) };
        DockPanel.SetDock(control, Dock.Right);
        dock.Children.Add(control);
        dock.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return dock;
    }

    private Control EngineLine(EngineRow e)
    {
        var p = Palette.Current;
        var state = e.Bundled ? _loc["settings.engine.bundled"] : e.Installed ? _loc["settings.engine.installed"] : _loc["settings.engine.missing"];
        var head = new TextBlock { Text = $"{e.Name} — {state}", FontWeight = FontWeight.SemiBold, Foreground = e.Installed ? p.Text : p.TextMuted };
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4), Children = { head } };
        if (!e.Installed)
        {
            if (e.InstallCommand is { } command)
            {
                var code = new SelectableTextBlock { Text = command, FontFamily = new FontFamily("Menlo, DejaVu Sans Mono, Ubuntu Mono, monospace"), FontSize = 12, FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center };
                var copy = Kit.Secondary(_loc["settings.engine.copy"]);
                copy.MinWidth = 0;
                copy.Click += async (_, _) => { await _ctx.CopyText(command); _status.Text = _loc["settings.engine.copied"]; };
                var row = new DockPanel();
                DockPanel.SetDock(copy, Dock.Right);
                row.Children.Add(copy);
                row.Children.Add(new Border { Child = code, Background = p.Wedge, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4), Margin = new Thickness(0, 0, 8, 0) });
                panel.Children.Add(row);
            }
            else
            {
                panel.Children.Add(Kit.Muted(_loc["settings.engine.noCommand"]));
            }
        }
        return panel;
    }

    private void ShowExtension(GnomeExtensionState state) => _extensionState.Text = _loc["settings.extension." + state];

    private void Save()
    {
        var keys = _keys.Select(k => (TriggerKey)((ComboBoxItem)k.SelectedItem!).Tag!).ToArray();
        var shared = _ctx.Shared with
        {
            Language = (string)((ComboBoxItem)_language.SelectedItem!).Tag!,
            Keys = new WheelKeys { Convert = keys[0], Tools = keys[1], Actions = keys[2] },
        };
        var desktop = _ctx.Desktop with
        {
            Output = (OutputLocation)((ComboBoxItem)_output.SelectedItem!).Tag!,
            StartAtLogin = _login.IsChecked == true,
            FileManagerEntries = _ctx.Session.IsLinux ? _fileManagers.IsChecked == true : _ctx.Desktop.FileManagerEntries,
        };
        var errors = _ctx.Save(shared, desktop);
        var p = Palette.Current;
        _status.Foreground = errors.Count == 0 ? p.Highlight : p.Error;
        _status.Text = errors.Count == 0 ? _loc["settings.saved"] : string.Join(" ", errors.Select(e => _loc["settings." + e]));
    }
}
