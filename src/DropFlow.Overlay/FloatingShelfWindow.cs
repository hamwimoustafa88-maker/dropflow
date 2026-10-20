using DropFlow.Core.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DropFlow.Overlay;

/// <summary>
/// "Float": a small always-on-top shelf pinned to a screen edge. Drop files on it to park them, then drag them from it into any other
/// app. Strings: <c>shelf.title</c>, <c>shelf.clear</c>, <c>shelf.empty</c>, <c>shelf.remove</c>.
/// </summary>
public sealed class FloatingShelfWindow : Window
{
    private const double Thickness = 132;
    private readonly ILocalizer _loc;
    private readonly WheelTheme _theme;
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty;
    private Point _dragStart;

    public FloatingShelfWindow(ShelfModel model, ILocalizer loc, WheelTheme? theme = null)
    {
        Model = model;
        _loc = loc;
        _theme = theme ?? WheelTheme.FromSystem();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = loc["shelf.title"];
        AllowDrop = true;
        FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12;

        var title = new TextBlock { Text = loc["shelf.title"], FontWeight = FontWeights.SemiBold, Foreground = _theme.Text, VerticalAlignment = VerticalAlignment.Center };
        var clear = new Button { Content = "✕", ToolTip = loc["shelf.clear"], Width = 24, Height = 24, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = _theme.TextMuted };
        clear.Click += (_, _) => Model.Clear();
        var header = new DockPanel { Margin = new Thickness(10, 8, 6, 4) };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(title);

        _empty = new TextBlock { Text = loc["shelf.empty"], Foreground = _theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6, 12, 12), TextAlignment = TextAlignment.Center };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(6, 0, 6, 8) };

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(scroll);

        Content = new Border
        {
            Margin = new Thickness(10),
            CornerRadius = new CornerRadius(14),
            Background = _theme.Panel,
            BorderBrush = _theme.PanelBorder,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.3 },
            Child = body,
        };

        Model.Changed += (_, _) => Dispatcher.BeginInvoke(Rebuild);
        Rebuild();
        PinTo(ShelfEdge.Right);
    }

    public ShelfModel Model { get; }

    public ShelfEdge Edge { get; private set; } = ShelfEdge.Right;

    public void PinTo(ShelfEdge edge)
    {
        Edge = edge;
        var wa = SystemParameters.WorkArea;
        var b = ShelfDocking.Compute(new ShelfDocking.Bounds(wa.Left, wa.Top, wa.Width, wa.Height), edge, Thickness + 20, 360);
        Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        Rebuild();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Model.Add(files);
        e.Handled = true;
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        if (Model.Items.Count == 0)
        {
            _list.Children.Add(_empty);
            return;
        }
        foreach (var path in Model.Items) _list.Children.Add(BuildItem(path));
    }

    private UIElement BuildItem(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var badge = new Border
        {
            Background = _theme.Accent, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = ext.Length == 0 ? "DIR" : ext, Foreground = _theme.TextOnAccent, FontSize = 10, FontWeight = FontWeights.Bold },
        };
        var name = new TextBlock { Text = Path.GetFileName(path), Foreground = _theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = path };
        var remove = new Button { Content = "✕", ToolTip = _loc["shelf.remove"], Width = 20, Height = 20, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = _theme.TextMuted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        remove.Click += (_, _) => Model.Remove(path);

        var grid = new Grid();
        grid.Children.Add(new StackPanel { Children = { badge, name } });
        grid.Children.Add(remove);

        var card = new Border { Background = _theme.Wedge, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), Child = grid, Cursor = Cursors.Hand };
        card.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(null) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            // Drag the real file out to any other app (Explorer, a browser upload box, a chat window…).
            DragDrop.DoDragDrop(card, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
        };
        return card;
    }
}
