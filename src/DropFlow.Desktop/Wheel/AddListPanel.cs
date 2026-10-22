// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DropFlow.Desktop.Views;
using DropFlow.Overlay;

namespace DropFlow.Desktop.Wheel;

/// <summary>"+ Add": the list of everything else that applies, grouped Convert / Tools / Actions, shown in the wheel's place.</summary>
public sealed class AddListPanel : Border
{
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Button _back = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private Button? _first;

    public AddListPanel()
    {
        Width = 360;
        MaxHeight = 380;
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(18, 16);
        BorderThickness = new Thickness(1);
        var scroller = new ScrollViewer { Content = _rows, MaxHeight = 230 };
        Child = new StackPanel { Children = { _title, _hint, scroller, _back } };
        _back.Click += (_, _) => Back?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Back?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
    }

    public event EventHandler<string>? Picked;
    public event EventHandler? Back;

    public void SetContent(string title, string hint, string back, IReadOnlyList<WheelItemGroup> groups, bool rightToLeft, Palette palette)
    {
        FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Background = palette.Panel;
        BorderBrush = palette.PanelBorder;
        _title.Text = title;
        _title.Foreground = palette.Text;
        _hint.Text = hint;
        _hint.Foreground = palette.TextMuted;
        _back.Content = back;
        _rows.Children.Clear();
        _first = null;
        foreach (var group in groups.Where(g => g.Items.Count > 0))
        {
            _rows.Children.Add(new TextBlock { Text = group.Title, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = palette.Highlight, Margin = new Thickness(4, 8, 4, 2) });
            foreach (var item in group.Items)
            {
                var row = new Button
                {
                    Content = item.State == WheelItemState.NeedsEngine ? "⭳ " + item.Label : item.Label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    Foreground = palette.Text,
                    Padding = new Thickness(8, 6),
                };
                var id = item.Id;
                row.Click += (_, _) => Picked?.Invoke(this, id);
                _first ??= row;
                _rows.Children.Add(row);
            }
        }
    }

    public void FocusList() => _first?.Focus();
}
