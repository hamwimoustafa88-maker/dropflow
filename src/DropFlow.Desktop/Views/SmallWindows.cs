// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DropFlow.Core.Catalog;
using DropFlow.Core.History;
using DropFlow.Core.Operations;
using DropFlow.Desktop.App;
using DropFlow.Overlay;

namespace DropFlow.Desktop.Views;

/// <summary>The welcome hint: how to open the wheel on this desktop, and (on a Mac) how to open an app Apple has not notarized.</summary>
public sealed class WelcomeWindow : Window
{
    public WelcomeWindow(DesktopLocalizer loc, IReadOnlyList<string> paragraphKeys)
    {
        this.Styled(loc, loc["welcome.title"]);
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new Image { Source = Kit.IconBitmap(96), Width = 48, Height = 48 });
        header.Children.Add(new TextBlock { Text = loc["welcome.title"], FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel { Margin = new Thickness(24, 20) };
        body.Children.Add(header);
        foreach (var key in paragraphKeys) body.Children.Add(Kit.Paragraph(loc[key]));
        var ok = Kit.Primary(loc["welcome.ok"]);
        ok.IsDefault = true;
        ok.Click += (_, _) => Close();
        body.Children.Add(Kit.ButtonRow(ok));
        Content = body;
    }
}

/// <summary>What DropFlow made, newest first, with "Open folder" and "Undo" (moves DropFlow's outputs to the trash).</summary>
public sealed class HistoryWindow : Window
{
    private readonly DesktopLocalizer _loc;
    private readonly HistoryStore _history;
    private readonly IFileDisposer _disposer;
    private readonly Action<string> _reveal;
    private readonly Func<string, string> _toolName;
    private readonly StackPanel _list = new() { Spacing = 6 };

    public HistoryWindow(DesktopLocalizer loc, HistoryStore history, IFileDisposer disposer, Action<string> reveal, Func<string, string> toolName)
    {
        _loc = loc;
        _history = history;
        _disposer = disposer;
        _reveal = reveal;
        _toolName = toolName;
        this.Styled(loc, loc["history.title"]);
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var clear = Kit.Secondary(loc["history.clear"]);
        clear.Click += (_, _) => { _history.Clear(); Refresh(); };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(clear, Dock.Right);
        top.Children.Add(clear);
        top.Children.Add(new TextBlock { Text = loc["history.title"], FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var root = new DockPanel { Margin = new Thickness(20, 16) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = _list });
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        var p = Palette.Current;
        _list.Children.Clear();
        var entries = _history.Entries;
        if (entries.Count == 0)
        {
            _list.Children.Add(Kit.Muted(_loc["history.empty"]));
            return;
        }
        foreach (var e in entries.Take(200))
        {
            var title = new TextBlock { Text = _toolName(e.Operation), FontWeight = FontWeight.SemiBold };
            var input = Path.GetFileName(e.Inputs.FirstOrDefault() ?? "") + (e.Inputs.Count > 1 ? $" (+{e.Inputs.Count - 1})" : "");
            var state = e.Undone ? _loc["history.undone"] : e.Success ? "" : _loc["history.failed"];
            var info = Kit.Muted($"{e.Time.ToLocalTime():g} · {input} {state}".Trim());
            var text = new StackPanel { Children = { title, info } };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (e.Outputs.Count > 0 && !e.Undone)
            {
                var open = Kit.Secondary(_loc["history.open"]);
                open.MinWidth = 0;
                var first = e.Outputs[0];
                open.Click += (_, _) => _reveal(first);
                var undo = Kit.Secondary(_loc["history.undo"]);
                undo.MinWidth = 0;
                var id = e.Id;
                undo.Click += (_, _) => { _history.Undo(id, _disposer); Refresh(); };
                buttons.Children.Add(open);
                buttons.Children.Add(undo);
            }

            var row = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            _list.Children.Add(new Border { Child = row, Background = p.Panel, BorderBrush = p.PanelBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8) });
        }
    }
}

/// <summary>The floating shelf: drop files on it to park them, drag them out to drop them anywhere else.</summary>
public sealed class ShelfWindow : Window
{
    private readonly DesktopLocalizer _loc;
    private readonly ShelfModel _shelf;
    private readonly StackPanel _items = new() { Spacing = 4 };

    public ShelfWindow(DesktopLocalizer loc, ShelfModel shelf)
    {
        _loc = loc;
        _shelf = shelf;
        this.Styled(loc, loc["shelf.title"]);
        Width = 300;
        Height = 380;
        Topmost = true;
        ShowInTaskbar = false;

        var clear = Kit.Secondary(loc["shelf.clear"]);
        clear.HorizontalAlignment = HorizontalAlignment.Stretch;
        clear.Click += (_, _) => _shelf.Clear();
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(clear, Dock.Bottom);
        root.Children.Add(clear);
        root.Children.Add(new ScrollViewer { Content = _items });

        var surface = new Border { Child = root, Background = Palette.Current.Hub };
        DragDrop.SetAllowDrop(surface, true);
        DragDrop.AddDragOverHandler(surface, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? Wheel.DropEffectPolicy.Choose(e.DragEffects) : DragDropEffects.None);
        DragDrop.AddDropHandler(surface, (_, e) =>
        {
            var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!);
            _shelf.Add(paths);
            e.DragEffects = Wheel.DropEffectPolicy.Choose(e.DragEffects);
        });
        Content = surface;
        _shelf.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var p = Palette.Current;
        _items.Children.Clear();
        if (_shelf.Items.Count == 0)
        {
            _items.Children.Add(Kit.Muted(_loc["shelf.empty"]));
            return;
        }
        _items.Children.Add(Kit.Muted(_loc["shelf.dragOut"]));
        foreach (var path in _shelf.Items)
        {
            var name = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button { Content = "✕", Padding = new Thickness(6, 2), Background = Brushes.Transparent };
            ToolTip.SetTip(remove, _loc["shelf.remove"]);
            var file = path;
            remove.Click += (_, _) => _shelf.Remove(file);
            var row = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(name);
            var chip = new Border { Child = row, Background = p.Wedge, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Cursor = new Cursor(StandardCursorType.Hand) };
            chip.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
                var item = await StorageProvider.TryGetFileFromPathAsync(file) as IStorageItem ?? await StorageProvider.TryGetFolderFromPathAsync(file);
                if (item is null) return;
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile(item));
                await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
            };
            _items.Children.Add(chip);
        }
    }
}

/// <summary>Names a history entry's operation: "PNG", "Compress PDF"…</summary>
public static class OperationNames
{
    public static string Of(ToolCatalog catalog, string operation, string language) =>
        Operation.IsConvert(operation) ? "→ " + Core.Wheel.WheelBuilder.FormatLabel(Operation.TargetOf(operation)) : catalog.Tool(operation)?.Name(language) ?? operation;
}
