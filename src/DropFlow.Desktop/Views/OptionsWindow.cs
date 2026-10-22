// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DropFlow.Core.Operations;
using DropFlow.Desktop.App;
using DropFlow.Overlay;

namespace DropFlow.Desktop.Views;

/// <summary>
/// The options form generated from a tool's <see cref="ToolOption"/> list — the Avalonia port of the Windows ToolOptionsDialog, with
/// the same kinds (Choice, Number, Range, Text, Password, Bool, Folder), labels (<c>opt.{operation}.{key}</c>, <c>opt.{key}</c>) and
/// validation (<see cref="OptionForm"/>, which also reads Arabic-Indic digits).
/// </summary>
public sealed class OptionsWindow : Window
{
    private readonly string _operation;
    private readonly IReadOnlyList<ToolOption> _options;
    private readonly DesktopLocalizer _loc;
    private readonly Dictionary<string, Func<string?>> _readers = [];
    private readonly Dictionary<string, TextBlock> _errors = [];
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, object?>?> _result = new();

    public OptionsWindow(string title, string operation, IReadOnlyList<ToolOption> options, DesktopLocalizer loc)
    {
        _operation = operation;
        _options = options;
        _loc = loc;
        this.Styled(loc, title);
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(22, 18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var option in options) panel.Children.Add(BuildField(option));

        var run = Kit.Primary(loc["dialog.run"]);
        run.IsDefault = true;
        run.Click += (_, _) => Confirm();
        var cancel = Kit.Secondary(loc["dialog.cancel"]);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        panel.Children.Add(Kit.ButtonRow(cancel, run));
        Content = new ScrollViewer { Content = panel, MaxHeight = 640 };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>Shows the form and completes with the values (or null when the user cancelled or closed it).</summary>
    public Task<IReadOnlyDictionary<string, object?>?> AskAsync()
    {
        Show();
        Activate();
        return _result.Task;
    }

    private Control BuildField(ToolOption option)
    {
        var p = Palette.Current;
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var label = _loc.OptionLabel(_operation, option);
        group.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = p.TextMuted, Margin = new Thickness(0, 0, 0, 4) });
        var initial = OptionForm.ToText(option.Default);

        switch (option.Kind)
        {
            case ToolOptionKind.Bool:
            {
                var box = new CheckBox { IsChecked = option.Default is true, Content = label };
                group.Children.Clear();
                group.Children.Add(box);
                _readers[option.Key] = () => box.IsChecked == true ? "true" : "false";
                break;
            }
            case ToolOptionKind.Choice when option.Choices is { Count: > 0 and <= 4 } choices:
            {
                var row = new WrapPanel();
                var selected = initial.Length > 0 ? initial : choices[0];
                foreach (var choice in choices)
                    row.Children.Add(new RadioButton
                    {
                        GroupName = _operation + "." + option.Key,
                        Content = _loc.ChoiceLabel(_operation, option, choice),
                        IsChecked = choice == selected,
                        Tag = choice,
                        Margin = new Thickness(0, 0, 14, 0),
                    });
                group.Children.Add(row);
                _readers[option.Key] = () => row.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
                break;
            }
            case ToolOptionKind.Choice:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var choice in option.Choices ?? []) combo.Items.Add(new ComboBoxItem { Content = _loc.ChoiceLabel(_operation, option, choice), Tag = choice });
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == initial) ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
                group.Children.Add(combo);
                _readers[option.Key] = () => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
                break;
            }
            case ToolOptionKind.Password:
            {
                var box = new TextBox { PasswordChar = '•', RevealPassword = false };
                group.Children.Add(box);
                _readers[option.Key] = () => box.Text;
                break;
            }
            case ToolOptionKind.Number or ToolOptionKind.Range when option.Min is { } min && option.Max is { } max && max - min <= 1000:
            {
                var text = new TextBox { Text = initial, Width = 74, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    Value = OptionForm.TryParseNumber(initial, out var d) ? d : min,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                    IsSnapToTickEnabled = max - min <= 100,
                    TickFrequency = 1,
                };
                var syncing = false;
                slider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != RangeBase.ValueProperty || syncing) return;
                    syncing = true;
                    text.Text = Math.Round(slider.Value).ToString(CultureInfo.InvariantCulture);
                    syncing = false;
                };
                text.TextChanged += (_, _) =>
                {
                    if (syncing || !OptionForm.TryParseNumber(text.Text, out var v)) return;
                    syncing = true;
                    slider.Value = Math.Clamp(v, min, max);
                    syncing = false;
                };
                var dock = new DockPanel();
                DockPanel.SetDock(text, Dock.Right);
                dock.Children.Add(text);
                dock.Children.Add(slider);
                group.Children.Add(dock);
                _readers[option.Key] = () => text.Text;
                break;
            }
            case ToolOptionKind.Folder:
            {
                var text = new TextBox { Text = initial };
                var browse = new Button { Content = "…", Width = 40, Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += async (_, _) =>
                {
                    var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = label, AllowMultiple = false });
                    if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) text.Text = path;
                };
                var dock = new DockPanel();
                DockPanel.SetDock(browse, Dock.Right);
                dock.Children.Add(browse);
                dock.Children.Add(text);
                group.Children.Add(dock);
                _readers[option.Key] = () => text.Text;
                break;
            }
            default:
            {
                var text = new TextBox { Text = initial };
                group.Children.Add(text);
                _readers[option.Key] = () => text.Text;
                break;
            }
        }

        var error = new TextBlock { Foreground = p.Error, FontSize = 12, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        _errors[option.Key] = error;
        group.Children.Add(error);
        return group;
    }

    private void Confirm()
    {
        var raw = _readers.ToDictionary(kv => kv.Key, kv => kv.Value());
        var (values, errors) = OptionForm.Validate(_options, raw);
        foreach (var block in _errors.Values) block.IsVisible = false;
        foreach (var error in errors)
        {
            var block = _errors[error.Key];
            block.Text = _loc["err." + error.Code];
            block.IsVisible = true;
        }
        if (errors.Count > 0) return;
        _result.TrySetResult(values);
        Close();
    }
}
