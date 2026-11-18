using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.Core.History;
using DropFlow.Core.Operations;

namespace DropFlow.App.Views;

/// <summary>What the Home window needs from the host.</summary>
internal sealed record HomeContext(
    HistoryStore History,
    IFileDisposer Disposer,
    Action<IReadOnlyList<string>> OpenWheel,
    Action<HistoryEntry> Retry,
    Func<string, string> OperationName);

/// <summary>
/// DropFlow's main window (tray → Open DropFlow, or a second launch): a drop zone that opens the wheel for the dropped files, an optional
/// list that collects files first (reorder with Alt+Up / Alt+Down, Delete removes, "Choose action" opens the wheel in that order), and the
/// recent conversions with a thumbnail and Open / Open with / Show in folder / Retry / Undo. Retry reuses the options of that run (never a
/// password: tools that need one ask again).
/// </summary>
internal sealed class HomeWindow : Window
{
    private static readonly HashSet<string> Thumbnailable = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".jxr" };

    private readonly UiKit _ui;
    private readonly HomeContext _ctx;
    private readonly StackPanel _recent = new();
    private readonly TextBox _search = new() { MinHeight = 32, Padding = new Thickness(8, 5, 8, 5) };
    private readonly CheckBox _collect;
    private readonly ListBox _collected = new() { MaxHeight = 180, Visibility = Visibility.Collapsed };
    private readonly Button _choose;

    public HomeWindow(UiKit ui, HomeContext ctx)
    {
        _ui = ui;
        _ctx = ctx;
        ui.Style(this, 640, ui.Loc["home.title"]);
        SizeToContent = SizeToContent.Manual;
        Height = 720;
        ResizeMode = ResizeMode.CanResize;

        var zoneText = ui.Text(ui.Loc["home.drop"], 18, weight: FontWeights.SemiBold);
        zoneText.HorizontalAlignment = HorizontalAlignment.Center;
        var zoneHint = ui.Text(ui.Loc["home.drop.hint"], 12, muted: true);
        zoneHint.HorizontalAlignment = HorizontalAlignment.Center;
        var zone = new Border
        {
            MinHeight = 140,
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(2),
            BorderBrush = ui.Theme.PanelBorder,
            Background = ui.Theme.Panel,
            AllowDrop = true,
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { zoneText, zoneHint } },
            Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(zone, ui.Loc["home.drop"]);
        zone.DragEnter += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; zone.BorderBrush = ui.Theme.Accent; e.Handled = true; };
        zone.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        zone.DragLeave += (_, _) => zone.BorderBrush = ui.Theme.PanelBorder;
        zone.Drop += (_, e) =>
        {
            zone.BorderBrush = ui.Theme.PanelBorder;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            if (_collect!.IsChecked == true) { foreach (var f in files) AddCollected(f); }
            else ctx.OpenWheel(files);
        };
        zone.MouseLeftButtonUp += (_, _) => PickFiles();

        _collect = new CheckBox { Content = ui.Loc["home.collect"], Foreground = ui.Theme.Text, Margin = new Thickness(0, 10, 0, 4) };
        _collect.Checked += (_, _) => _collected.Visibility = _choose!.Visibility = Visibility.Visible;
        _collect.Unchecked += (_, _) => _collected.Visibility = _choose!.Visibility = Visibility.Collapsed;
        _collected.PreviewKeyDown += OnCollectedKey;
        System.Windows.Automation.AutomationProperties.SetName(_collected, ui.Loc["home.collect"]);
        _choose = ui.Primary(ui.Loc["home.choose"]);
        _choose.Visibility = Visibility.Collapsed;
        _choose.HorizontalAlignment = HorizontalAlignment.Left;
        _choose.Margin = new Thickness(0, 6, 0, 0);
        _choose.Click += (_, _) =>
        {
            var files = _collected.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag).ToList();
            if (files.Count == 0) return;
            ctx.OpenWheel(files);
            _collected.Items.Clear();
        };

        System.Windows.Automation.AutomationProperties.SetName(_search, ui.Loc["home.search"]);
        _search.TextChanged += (_, _) => Rebuild();

        var root = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
        root.Children.Add(zone);
        root.Children.Add(_collect);
        root.Children.Add(_collected);
        root.Children.Add(_choose);
        root.Children.Add(ui.SectionTitle(ui.Loc["home.recent"]));
        root.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = { _search } });
        root.Children.Add(_recent);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Activated += (_, _) => Rebuild();
        Rebuild();
    }

    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        if (_collect.IsChecked == true) foreach (var f in dialog.FileNames) AddCollected(f);
        else _ctx.OpenWheel(dialog.FileNames);
    }

    private void AddCollected(string file)
    {
        if (_collected.Items.OfType<ListBoxItem>().Any(i => string.Equals((string)i.Tag, file, StringComparison.OrdinalIgnoreCase))) return;
        _collected.Items.Add(new ListBoxItem { Tag = file, Content = Path.GetFileName(file), ToolTip = file, Padding = new Thickness(8, 4, 8, 4) });
    }

    private void OnCollectedKey(object sender, KeyEventArgs e)
    {
        if (_collected.SelectedItem is not ListBoxItem item) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var i = _collected.Items.IndexOf(item);
        if (key == Key.Delete)
        {
            _collected.Items.Remove(item);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
        {
            var to = Math.Clamp(i + (key == Key.Up ? -1 : 1), 0, _collected.Items.Count - 1);
            _collected.Items.RemoveAt(i);
            _collected.Items.Insert(to, item);
            _collected.SelectedItem = item;
            item.Focus();
            e.Handled = true;
        }
    }

    private void Rebuild()
    {
        _recent.Children.Clear();
        var query = _search.Text.Trim();
        var entries = _ctx.History.Entries
            .Where(e => query.Length == 0 || e.Inputs.Concat(e.Outputs).Any(p => Path.GetFileName(p).Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Take(100)
            .ToList();
        if (entries.Count == 0)
        {
            _recent.Children.Add(_ui.Text(_ui.Loc["history.empty"], muted: true));
            return;
        }
        foreach (var e in entries) _recent.Children.Add(Card(e));
    }

    private Border Card(HistoryEntry e)
    {
        var ui = _ui;
        var output = e.Outputs.FirstOrDefault(p => File.Exists(p));
        var thumb = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(12), Background = ui.Theme.Wedge, Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true };
        var badge = ui.Text(Path.GetExtension(output ?? e.Inputs.FirstOrDefault() ?? "").TrimStart('.').ToUpperInvariant(), 12, weight: FontWeights.Bold, wrap: false);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        badge.VerticalAlignment = VerticalAlignment.Center;
        thumb.Child = badge;
        if (output is not null && Thumbnailable.Contains(Path.GetExtension(output))) _ = LoadThumbnailAsync(thumb, output);

        var name = e.Inputs.Count > 0 ? Path.GetFileName(e.Inputs[0]) + (e.Inputs.Count > 1 ? $" +{e.Inputs.Count - 1}" : "") : "";
        var status = e.Undone ? ui.Loc["history.undone"] : e.Success ? _ctx.OperationName(e.Operation) : $"{ui.Loc["history.failed"]}: {e.Error}";
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(ui.Text(name, 14, weight: FontWeights.SemiBold, wrap: false));
        text.Children.Add(ui.Text($"{status} · {e.Time.LocalDateTime:g}", 12, muted: true, wrap: false));

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Add(string key, Action run, string? accessibleName = null)
        {
            var b = ui.Secondary(ui.Loc[key]);
            b.Margin = new Thickness(0, 0, 8, 4);
            b.Click += (_, _) => run();
            System.Windows.Automation.AutomationProperties.SetName(b, $"{ui.Loc[key]} {accessibleName ?? name}");
            buttons.Children.Add(b);
            return b;
        }
        if (output is not null && !e.Undone)
        {
            Add("home.open", () => Start(output));
            Add("home.openWith", () => Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {output}"));
            Add("history.open", () => Explorer.Reveal(output));
        }
        if (e.Inputs.Count > 0 && e.Inputs.All(p => File.Exists(p) || Directory.Exists(p))) Add("home.retry", () => _ctx.Retry(e));
        if (e.Success && e.Outputs.Count > 0 && !e.Undone) Add("history.undo", () => { _ctx.History.Undo(e.Id, _ctx.Disposer); Rebuild(); });
        text.Children.Add(buttons);

        var row = new DockPanel();
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(thumb);
        row.Children.Add(text);
        var card = ui.Card(row, 12);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private static void Start(string file, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = args is null }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { /* nothing to open with */ }
    }

    /// <summary>Decodes a small preview off the UI thread (frozen, so it can cross threads); an unreadable file keeps its extension badge.</summary>
    private static async Task LoadThumbnailAsync(Border target, string path)
    {
        var image = await Task.Run(() =>
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = 128;
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                b.EndInit();
                b.Freeze();
                return (ImageSource?)b;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        });
        if (image is not null) target.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
    }
}
