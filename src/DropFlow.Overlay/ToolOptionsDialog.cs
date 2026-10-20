using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;

namespace DropFlow.Overlay;

/// <summary>
/// A small dialog generated from a tool's <see cref="ToolOption"/> list, so adding a tool never needs new UI.
/// Strings come from <see cref="ILocalizer"/> (<c>opt.{operation}.{key}</c>, <c>opt.{key}</c>, <c>dialog.run</c>, <c>dialog.cancel</c>,
/// <c>err.{code}</c>); validation is <see cref="OptionForm"/>.
/// </summary>
public sealed class ToolOptionsDialog : Window
{
    private readonly string _operation;
    private readonly IReadOnlyList<ToolOption> _options;
    private readonly ILocalizer _loc;
    private readonly WheelTheme _theme;
    private readonly Dictionary<string, Func<string?>> _readers = [];
    private readonly Dictionary<string, TextBlock> _errors = [];

    public ToolOptionsDialog(string title, string operation, IReadOnlyList<ToolOption> options, ILocalizer loc, WheelTheme? theme = null)
    {
        _operation = operation;
        _options = options;
        _loc = loc;
        _theme = theme ?? WheelTheme.FromSystem();

        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Background = _theme.Hub;
        Foreground = _theme.Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        foreach (var option in options) panel.Children.Add(BuildField(option));
        panel.Children.Add(BuildButtons());
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; }
        };
        Loaded += (_, _) => Activate();
    }

    /// <summary>The validated values after the user pressed Run; null when cancelled.</summary>
    public IReadOnlyDictionary<string, object?>? Result { get; private set; }

    /// <summary>Shows the dialog and returns the chosen values, or null when the user cancelled.</summary>
    public static IReadOnlyDictionary<string, object?>? Ask(string title, string operation, IReadOnlyList<ToolOption> options, ILocalizer loc)
    {
        var dialog = new ToolOptionsDialog(title, operation, options, loc);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private string Label(ToolOption option)
    {
        foreach (var key in OptionForm.LabelKeys(_operation, option))
        {
            var text = _loc[key];
            if (text != key) return text;
        }
        return option.Key;
    }

    private UIElement BuildField(ToolOption option)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        group.Children.Add(new TextBlock { Text = Label(option), Foreground = _theme.TextMuted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });

        var initial = OptionForm.ToText(option.Default);
        switch (option.Kind)
        {
            case ToolOptionKind.Bool:
            {
                var box = new CheckBox { IsChecked = option.Default is true, Content = Label(option), Foreground = _theme.Text };
                group.Children.Clear();                                    // the checkbox carries its own label
                group.Children.Add(box);
                _readers[option.Key] = () => box.IsChecked == true ? "true" : "false";
                break;
            }
            case ToolOptionKind.Choice when option.Choices is { Count: > 0 and <= 4 } choices:
            {
                var row = new UniformGrid { Rows = 1 };
                var selected = initial.Length > 0 ? initial : choices[0];
                foreach (var choice in choices)
                {
                    var b = new RadioButton { GroupName = option.Key, Content = ChoiceText(option, choice), IsChecked = choice == selected, Tag = choice, Margin = new Thickness(0, 0, 10, 0), Foreground = _theme.Text };
                    row.Children.Add(b);
                }
                group.Children.Add(row);
                _readers[option.Key] = () => row.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
                break;
            }
            case ToolOptionKind.Choice:
            {
                var combo = new ComboBox { MinHeight = 32 };
                foreach (var choice in option.Choices ?? []) combo.Items.Add(new ComboBoxItem { Content = ChoiceText(option, choice), Tag = choice });
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == initial) ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
                group.Children.Add(combo);
                _readers[option.Key] = () => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
                break;
            }
            case ToolOptionKind.Password:
            {
                var pw = new PasswordBox { MinHeight = 32, Padding = new Thickness(6, 5, 6, 5) };
                group.Children.Add(pw);
                _readers[option.Key] = () => pw.Password;
                break;
            }
            case ToolOptionKind.Number when option.Min is { } min && option.Max is { } max && max - min <= 1000:
            {
                var text = new TextBox { Text = initial, MinHeight = 32, Width = 70, Padding = new Thickness(6, 5, 6, 5), VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Minimum = min, Maximum = max, Value = double.TryParse(initial, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : min, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), IsMoveToPointEnabled = true };
                var syncing = false;
                slider.ValueChanged += (_, _) => { if (syncing) return; syncing = true; text.Text = Math.Round(slider.Value).ToString(CultureInfo.InvariantCulture); syncing = false; };
                text.TextChanged += (_, _) => { if (syncing) return; if (OptionForm.TryParseNumber(text.Text, out var v)) { syncing = true; slider.Value = Math.Clamp(v, min, max); syncing = false; } };
                var grid = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(text, Dock.Right);
                grid.Children.Add(text);
                grid.Children.Add(slider);
                group.Children.Add(grid);
                _readers[option.Key] = () => text.Text;
                break;
            }
            case ToolOptionKind.Folder:
            {
                var row = new DockPanel();
                var browse = new Button { Content = "…", Width = 36, Margin = new Thickness(6, 0, 0, 0) };
                var text = new TextBox { Text = initial, MinHeight = 32, Padding = new Thickness(6, 5, 6, 5) };
                browse.Click += (_, _) =>
                {
                    var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Label(option) };
                    if (dlg.ShowDialog(this) == true) text.Text = dlg.FolderName;
                };
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
                row.Children.Add(text);
                group.Children.Add(row);
                _readers[option.Key] = () => text.Text;
                break;
            }
            default:
            {
                var text = new TextBox { Text = initial, MinHeight = 32, Padding = new Thickness(6, 5, 6, 5) };
                group.Children.Add(text);
                _readers[option.Key] = () => text.Text;
                break;
            }
        }

        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        _errors[option.Key] = error;
        group.Children.Add(error);
        return group;
    }

    private string ChoiceText(ToolOption option, string choice)
    {
        foreach (var key in new[] { $"opt.{_operation}.{option.Key}.{choice}", $"opt.{option.Key}.{choice}" })
        {
            var text = _loc[key];
            if (text != key) return text;
        }
        return choice;
    }

    private UIElement BuildButtons()
    {
        var run = new Button { Content = _loc["dialog.run"], IsDefault = true, MinWidth = 96, MinHeight = 34, Margin = new Thickness(8, 0, 0, 0), Background = _theme.Accent, Foreground = _theme.TextOnAccent, BorderThickness = new Thickness(0) };
        var cancel = new Button { Content = _loc["dialog.cancel"], IsCancel = true, MinWidth = 96, MinHeight = 34 };
        run.Click += (_, _) => Confirm();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(cancel);
        row.Children.Add(run);
        return row;
    }

    /// <summary>What the user entered, by option key (internal so tests can drive the dialog without a visible window).</summary>
    internal IReadOnlyDictionary<string, string?> ReadRaw() => _readers.ToDictionary(kv => kv.Key, kv => kv.Value());

    /// <summary>Validates and, when everything is fine, stores <see cref="Result"/> and closes with OK.</summary>
    internal bool TryConfirm()
    {
        var (values, errors) = OptionForm.Validate(_options, ReadRaw());
        foreach (var (key, block) in _errors) { block.Visibility = Visibility.Collapsed; block.Text = ""; }
        foreach (var error in errors)
        {
            var block = _errors[error.Key];
            block.Text = _loc["err." + error.Code];
            block.Visibility = Visibility.Visible;
        }
        if (errors.Count > 0) return false;
        Result = values;
        return true;
    }

    private void Confirm()
    {
        if (TryConfirm()) DialogResult = true;
    }
}
