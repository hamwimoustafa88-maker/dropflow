using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;

namespace DropFlow.App.Views;

/// <summary>What the arrange window needs: the categories to choose from, the items of a wheel, and where to keep the result.</summary>
internal sealed record WheelEditorContext(
    IReadOnlyList<(string Id, string Label)> Categories,
    Func<WheelKind, string, IReadOnlyList<Wedge>> Items,
    Func<WheelLayouts> Layouts,
    Action<WheelLayouts> Save,
    int FrontMax);

/// <summary>
/// Settings → Arrange the wheels: for one kind of file and one wheel, the items in order with the line between the front and the back of the
/// wheel. Drag a row, or use Alt+Up / Alt+Down, to move it; Space or Hide takes it off the wheel (it stays in "+ Add"); Reset restores
/// DropFlow's order. Every change is saved at once.
/// </summary>
internal sealed class WheelEditorWindow : Window
{
    private readonly UiKit _ui;
    private readonly WheelEditorContext _ctx;
    private readonly ComboBox _category = new() { MinHeight = 32 };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
    private readonly ListBox _visible = new() { MinHeight = 260, MaxHeight = 380, AllowDrop = true };
    private readonly ListBox _hidden = new() { MinHeight = 60, MaxHeight = 140 };
    private readonly TextBlock _summary;
    private WheelKind _kind = WheelKind.Convert;
    private IReadOnlyList<Wedge> _items = [];
    private Point _dragStart;

    public WheelEditorWindow(UiKit ui, WheelEditorContext ctx)
    {
        _ui = ui;
        _ctx = ctx;
        ui.Style(this, 520, ui.Loc["arrange.title"]);
        SizeToContent = SizeToContent.Height;

        foreach (var (id, label) in ctx.Categories) _category.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        _category.SelectedIndex = 0;
        _category.SelectionChanged += (_, _) => Reload();
        AutomationName(_category, ui.Loc["arrange.category"]);

        foreach (var kind in Enum.GetValues<WheelKind>())
        {
            var tab = ui.Secondary(ui.Loc["wheel." + kind.ToString().ToLowerInvariant()]);
            tab.Margin = new Thickness(0, 0, 8, 0);
            tab.Tag = kind;
            tab.Click += (_, _) => { _kind = kind; Reload(); };
            _tabs.Children.Add(tab);
        }

        _summary = ui.Text("", 12, muted: true);
        AutomationName(_visible, ui.Loc["arrange.onWheel"]);
        AutomationName(_hidden, ui.Loc["arrange.hidden"]);
        _visible.PreviewKeyDown += OnVisibleKey;
        _hidden.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space && _hidden.SelectedItem is ListBoxItem { Tag: string id }) { Toggle(id); e.Handled = true; } };
        _visible.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(_visible);
        _visible.PreviewMouseMove += OnDragMove;
        _visible.Drop += OnDrop;

        var up = ui.Secondary("↑");
        up.Click += (_, _) => MoveSelected(-1);
        AutomationName(up, ui.Loc["arrange.up"]);
        var down = ui.Secondary("↓");
        down.Click += (_, _) => MoveSelected(+1);
        AutomationName(down, ui.Loc["arrange.down"]);
        var hide = ui.Secondary(ui.Loc["arrange.hide"]);
        hide.Click += (_, _) => { if (_visible.SelectedItem is ListBoxItem { Tag: string id }) Toggle(id); };
        var show = ui.Secondary(ui.Loc["arrange.show"]);
        show.Click += (_, _) => { if (_hidden.SelectedItem is ListBoxItem { Tag: string id }) Toggle(id); };
        var reset = ui.Secondary(ui.Loc["arrange.reset"]);
        reset.Click += (_, _) => { Save(WheelLayout.Default); };
        foreach (var b in new[] { up, down, hide }) b.Margin = new Thickness(0, 0, 8, 0);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(22, 16, 22, 18),
                Children =
                {
                    ui.Text(ui.Loc["arrange.hint"], 12, muted: true),
                    ui.SectionTitle(ui.Loc["arrange.category"]),
                    _category,
                    _tabs,
                    _summary,
                    _visible,
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8), Children = { up, down, hide, reset } },
                    ui.SectionTitle(ui.Loc["arrange.hidden"]),
                    _hidden,
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { show } },
                },
            },
        };
        Reload();
    }

    private static void AutomationName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);

    private string Category => (string)((ComboBoxItem)_category.SelectedItem).Tag;

    private WheelLayout Layout => _ctx.Layouts().For(_kind, Category);

    private void Reload(string? select = null)
    {
        foreach (Button tab in _tabs.Children)
            tab.FontWeight = (WheelKind)tab.Tag == _kind ? FontWeights.Bold : FontWeights.Normal;
        _items = _ctx.Items(_kind, Category);
        var (visible, hidden) = WheelLayoutEditor.View(_items, Layout);
        var front = Math.Min(visible.Count, _ctx.FrontMax);
        _summary.Text = visible.Count > _ctx.FrontMax
            ? _ui.Loc.Format("arrange.summary.back", front, visible.Count - front)
            : _ui.Loc.Format("arrange.summary.front", visible.Count);

        _visible.Items.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            var w = visible[i];
            var face = i < _ctx.FrontMax ? _ui.Loc["arrange.front"] : _ui.Loc["arrange.back"];
            var row = new ListBoxItem
            {
                Tag = w.Id,
                Content = $"{i + 1}. {w.Label}  ·  {face}",
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0, i == _ctx.FrontMax ? 2 : 0, 0, 0),
                BorderBrush = _ui.Theme.Accent,
            };
            AutomationName(row, $"{w.Label}, {face}, {i + 1}/{visible.Count}");
            _visible.Items.Add(row);
        }
        _hidden.Items.Clear();
        foreach (var w in hidden) _hidden.Items.Add(new ListBoxItem { Tag = w.Id, Content = w.Label, Padding = new Thickness(8, 6, 8, 6) });
        if (select is not null)
        {
            var again = _visible.Items.OfType<ListBoxItem>().FirstOrDefault(r => (string)r.Tag == select);
            if (again is not null) { _visible.SelectedItem = again; again.Focus(); }
        }
    }

    private void Save(WheelLayout layout, string? select = null)
    {
        _ctx.Save(_ctx.Layouts().With(_kind, Category, layout));
        Reload(select);
    }

    private void MoveSelected(int delta)
    {
        if (_visible.SelectedItem is not ListBoxItem { Tag: string id }) return;
        Save(WheelLayoutEditor.Move(_items, Layout, id, delta), id);
    }

    private void Toggle(string id) => Save(WheelLayoutEditor.ToggleHidden(_items, Layout, id));

    private void OnVisibleKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
        {
            MoveSelected(key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (key == Key.Space && _visible.SelectedItem is ListBoxItem { Tag: string id })
        {
            Toggle(id);
            e.Handled = true;
        }
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _visible.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var p = e.GetPosition(_visible);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(_visible, new DataObject("dropflow/wheel-item", id), DragDropEffects.Move);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("dropflow/wheel-item") is not string id) return;
        var target = _visible.Items.Count;
        for (var i = 0; i < _visible.Items.Count; i++)
        {
            var row = (ListBoxItem)_visible.Items[i];
            var y = e.GetPosition(row).Y;
            if (y < row.ActualHeight / 2) { target = i; break; }
        }
        var from = _visible.Items.OfType<ListBoxItem>().ToList().FindIndex(r => (string)r.Tag == id);
        if (from >= 0 && from < target) target--;
        Save(WheelLayoutEditor.MoveTo(_items, Layout, id, target), id);
    }
}
