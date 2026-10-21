using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DropFlow.Overlay;

/// <summary>
/// The second level of "+ Add": a compact, scrollable list that takes the wheel's place in the same overlay window. Rows are grouped
/// (Convert, Tools, Actions) under headers that cannot be selected. Mouse: click a row. Keyboard: Up/Down/Home/End/PageUp/PageDown
/// move, Enter or Space picks, Esc (or the Back button) returns to the wheel. Follows the reading direction it is given.
/// </summary>
public sealed class AddListPanel : Border
{
    public const double SizeDips = (WheelControl.Outer + 9) * 2;

    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
    private readonly Button _back = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0), MinHeight = 26 };
    private readonly ListBox _list = new()
    {
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        SelectionMode = SelectionMode.Single,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private WheelTheme _theme = WheelTheme.FromSystem();

    public AddListPanel()
    {
        Width = Height = SizeDips;
        CornerRadius = new CornerRadius(22);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(18, 16, 18, 14);
        System.Windows.Documents.TextElement.SetFontFamily(this, new FontFamily("Segoe UI Variable Text, Segoe UI, Tahoma"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        KeyboardNavigation.SetDirectionalNavigation(_list, KeyboardNavigationMode.Contained);

        DockPanel.SetDock(_back, Dock.Right);
        var header = new DockPanel { LastChildFill = true, Children = { _back, _title } };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_hint, Dock.Top);
        Child = new DockPanel { Children = { header, _hint, _list } };

        _back.Click += (_, _) => Back?.Invoke(this, EventArgs.Empty);
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { Tag: WheelItem } row)
            {
                Pick(Entries.ToList().IndexOf((WheelItem)row.Tag));
                e.Handled = true;
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        ApplyTheme();
    }

    /// <summary>A row was chosen (by id).</summary>
    public event EventHandler<string>? Picked;

    /// <summary>Esc or the Back button: return to the wheel.</summary>
    public event EventHandler? Back;

    public WheelTheme Theme
    {
        get => _theme;
        set { _theme = value; ApplyTheme(); }
    }

    /// <summary>The selectable rows, in the order shown.</summary>
    public IEnumerable<WheelItem> Entries => _list.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<WheelItem>();

    /// <summary>The group titles, in the order shown.</summary>
    public IEnumerable<string> Headers => _list.Items.OfType<ListBoxItem>().Where(i => i.Tag is null).Select(i => ((TextBlock)i.Content).Text);

    public string Title => _title.Text;

    /// <summary>Index (among <see cref="Entries"/>) of the highlighted row, or -1.</summary>
    public int SelectedEntry => _list.SelectedItem is ListBoxItem { Tag: WheelItem item } ? Entries.ToList().IndexOf(item) : -1;

    public void SetContent(string title, string hint, string backLabel, IReadOnlyList<WheelItemGroup> groups, bool rightToLeft)
    {
        FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _title.Text = title;
        _hint.Text = hint;
        _back.Content = backLabel;
        _list.Items.Clear();
        foreach (var group in groups.Where(g => g.Items.Count > 0))
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = group.Title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = _theme.TextMuted, Margin = new Thickness(0, 6, 0, 0) },
                Focusable = false,
                IsHitTestVisible = false,
            });
            foreach (var item in group.Items)
            {
                var label = item.Label + (item.NeedsOptions ? " …" : "");
                if (item.State == WheelItemState.NeedsEngine) label = "⭳ " + label;
                _list.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = label, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis },
                    Tag = item,
                    Style = RowStyle(),
                });
            }
        }
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is WheelItem);
    }

    /// <summary>Moves keyboard focus to the highlighted row so the arrow keys work at once.</summary>
    public void FocusList()
    {
        _list.UpdateLayout();
        if (_list.SelectedItem is ListBoxItem row) { row.Focus(); Keyboard.Focus(row); }
        else _list.Focus();
    }

    /// <summary>Highlights a row (index among <see cref="Entries"/>).</summary>
    public void Select(int entry)
    {
        var rows = _list.Items.OfType<ListBoxItem>().Where(i => i.Tag is WheelItem).ToList();
        if (entry < 0 || entry >= rows.Count) return;
        _list.SelectedItem = rows[entry];
        _list.ScrollIntoView(rows[entry]);
    }

    public void Pick(int entry)
    {
        var entries = Entries.ToList();
        if (entry < 0 || entry >= entries.Count) return;
        Picked?.Invoke(this, entries[entry].Id);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is Button && e.Key is Key.Enter or Key.Space) return; // the Back button handles its own keys
        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                Pick(SelectedEntry);
                e.Handled = true;
                break;
            case Key.Escape or Key.Back:
                Back?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void ApplyTheme()
    {
        Background = _theme.Panel;
        BorderBrush = _theme.PanelBorder;
        _title.Foreground = _theme.Text;
        _hint.Foreground = _theme.TextMuted;
        _back.Foreground = _theme.Text;
        _back.Background = _theme.Wedge;
        _back.Template = Flat(typeof(Button), 6);
        foreach (var row in _list.Items.OfType<ListBoxItem>())
        {
            if (row.Tag is null) ((TextBlock)row.Content).Foreground = _theme.TextMuted;
            else row.Style = RowStyle();
        }
    }

    private Style RowStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, Flat(typeof(ListBoxItem), 8)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, _theme.Text));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, _theme.WedgeHover));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, _theme.Accent));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, _theme.TextOnAccent));
        style.Triggers.Add(hover);
        style.Triggers.Add(selected); // after hover, so the highlighted row keeps the accent under the pointer
        return style;
    }

    /// <summary>A plain rounded template (no Windows chrome) that shows the control's Background, Padding and content.</summary>
    private static ControlTemplate Flat(Type type, double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(type) { VisualTree = border };
    }
}
