using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DropFlow.Overlay;

/// <summary>
/// The transparent, topmost, never-activating window that carries the wheel. It is an OLE drop target: Windows hands it the dragged
/// files when the drag enters, which is how DropFlow learns what is being dragged without touching the source app.
/// </summary>
public sealed class WheelWindow : Window
{
    public const double SizeDips = (WheelControl.Outer + 16) * 2 + 24;

    private readonly Grid _root;
    private nint _hwnd;
    private string[] _files = [];
    private bool _prepared;

    public WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;            // created off-screen; moved with SetWindowPos when shown
        Width = Height = SizeDips;
        Title = "DropFlow wheel";

        Wheel = new WheelControl { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AddList = new AddListPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

        // #01000000 is almost invisible but still hit-testable; a fully transparent layered window gets no OLE drops.
        _root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), AllowDrop = true };
        _root.Children.Add(Wheel);
        _root.Children.Add(AddList);
        Content = _root;
        AllowDrop = true;

        _root.DragEnter += OnDragEnter;
        _root.DragOver += OnDragOver;
        _root.DragLeave += OnDragLeave;
        _root.Drop += OnDrop;
        Wheel.Cancelled += (_, _) => DropCancelled?.Invoke(this, EventArgs.Empty);
        Wheel.ItemInvoked += (_, e) => ItemClicked?.Invoke(this, e.Index);
        Wheel.ItemContextRequested += (_, e) => ItemContextRequested?.Invoke(this, e.Index);
        Wheel.FlipRequested += (_, _) => FlipRequested?.Invoke(this, EventArgs.Empty);
        AddList.Picked += (_, id) => AddItemPicked?.Invoke(this, id);
        AddList.Back += (_, _) => AddListBack?.Invoke(this, EventArgs.Empty);
        Deactivated += (_, _) =>
        {
            // clicking anywhere else closes a click-mode wheel
            if (IsInteractive && IsWheelVisible) DropCancelled?.Invoke(this, EventArgs.Empty);
        };
    }

    public WheelControl Wheel { get; }

    /// <summary>
    /// Size factor of the whole wheel (Settings, 0.8-1.4). The content is scaled with a layout transform, so hit-testing (which uses
    /// positions relative to the wheel control) is unchanged.
    /// </summary>
    public double Scale
    {
        get => _scale;
        set
        {
            _scale = Math.Clamp(value, 0.8, 1.4);
            _root.LayoutTransform = Math.Abs(_scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(_scale, _scale);
            Width = Height = SizeDips * _scale;
        }
    }
    private double _scale = 1;

    /// <summary>The "+ Add" list, shown in the wheel's place.</summary>
    public AddListPanel AddList { get; }

    public bool IsAddListVisible => AddList.Visibility == Visibility.Visible;

    public bool IsWheelVisible { get; private set; }

    /// <summary>True when opened by click/keyboard (not by a drag): the window takes focus and closes when it loses it.</summary>
    public bool IsInteractive { get; private set; }

    /// <summary>Files Windows delivered when the drag entered the wheel (empty until then).</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>The drag entered the wheel carrying these files: the host should fill the wedges.</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesEntered;

    /// <summary>The user released the drag on a wedge.</summary>
    public event EventHandler<(IReadOnlyList<string> Files, int Index)>? ItemDropped;

    /// <summary>A wedge was clicked or chosen with the keyboard (interactive mode).</summary>
    public event EventHandler<int>? ItemClicked;

    /// <summary>The user released the drag off the wedges, left the wheel, or pressed Esc.</summary>
    public event EventHandler? DropCancelled;

    /// <summary>Right-click, the Menu key, Shift+F10 or Delete on a wedge (interactive mode).</summary>
    public event EventHandler<int>? ItemContextRequested;

    /// <summary>Tab, PageUp/PageDown or the mouse wheel asked to turn the wheel over.</summary>
    public event EventHandler? FlipRequested;

    /// <summary>A row of the "+ Add" list was chosen (its id).</summary>
    public event EventHandler<string>? AddItemPicked;

    /// <summary>Esc or Back in the "+ Add" list.</summary>
    public event EventHandler? AddListBack;

    /// <summary>The wheel has just appeared on screen (the welcome guide ticks its practice step with it).</summary>
    public event EventHandler? Shown;

    /// <summary>
    /// Replaces the wheel with the "+ Add" list and takes focus so the list works with the keyboard. Called from inside an OLE drop,
    /// so activation is deferred until the drop has finished.
    /// </summary>
    public void ShowAddList(string title, string hint, string backLabel, IReadOnlyList<WheelItemGroup> groups, bool rightToLeft)
    {
        AddList.Theme = Wheel.Theme;
        AddList.SetContent(title, hint, backLabel, groups, rightToLeft);
        Wheel.Visibility = Visibility.Hidden;
        AddList.Visibility = Visibility.Visible;
        MakeInteractive(AddList.FocusList);
    }

    /// <summary>Back from the "+ Add" list to the wheel (which stays interactive: click, arrows, Enter, Esc).</summary>
    public void HideAddList()
    {
        if (!IsAddListVisible) return;
        AddList.Visibility = Visibility.Collapsed;
        Wheel.Visibility = Visibility.Visible;
        MakeInteractive(() => { Wheel.Focus(); System.Windows.Input.Keyboard.Focus(Wheel); });
    }

    /// <summary>Shows a small menu with one command at the pointer (used for "Remove from wheel" on a pinned wedge).</summary>
    public void ShowItemMenu(string label, bool rightToLeft, Action run)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => run();
        var menu = new ContextMenu
        {
            FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            PlacementTarget = Wheel,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            Items = { item },
        };
        menu.Closed += (_, _) => { if (IsWheelVisible) Wheel.Focus(); };
        menu.IsOpen = true;
    }

    private void MakeInteractive(Action focus)
    {
        if (!_prepared || !IsWheelVisible) { focus(); return; }
        IsInteractive = true;
        WindowNative.SetNoActivate(_hwnd, false);
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsWheelVisible) return;
            Activate();
            focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Creates the native window once, off-screen and hidden. Call before the first <see cref="ShowAt"/>.</summary>
    public void Prepare()
    {
        if (_prepared) return;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.MakeNonActivating(_hwnd);
        Show();                                  // renders the content while off-screen
        WindowNative.HideNoActivate(_hwnd);
        _prepared = true;
    }

    /// <summary>
    /// Shows the wheel centred under <paramref name="point"/> (physical screen pixels). In the default drag mode the window never takes
    /// focus; with <paramref name="interactive"/> it does, so arrow keys, Enter and Esc work.
    /// </summary>
    public string ShowAt(PixelPoint point, bool interactive = false)
    {
        Prepare();
        _files = [];
        AddList.Visibility = Visibility.Collapsed;
        Wheel.Visibility = Visibility.Visible;
        Wheel.ClearHover();
        IsInteractive = interactive;
        WindowNative.SetClickThrough(_hwnd, false);
        WindowNative.SetNoActivate(_hwnd, !interactive);
        var (x, y, size, dpi) = WindowNative.ShowTopmostCentered(_hwnd, point, SizeDips * _scale);
        ScreenBounds = (x, y, size);
        IsWheelVisible = true;
        Wheel.PlayOpenAnimation();
        Shown?.Invoke(this, EventArgs.Empty);
        if (interactive)
        {
            Activate();
            Wheel.Focus();
            System.Windows.Input.Keyboard.Focus(Wheel);
        }
        return $"wheel at {x},{y} size {size}px dpi {dpi} rect {WindowNative.Describe(_hwnd)}";
    }

    /// <summary>Files for an interactive wheel (opened for known files rather than by a drag).</summary>
    public void SetFiles(IReadOnlyList<string> files) => _files = files.ToArray();

    /// <summary>Busy mode: the wheel only shows progress and lets clicks pass through to whatever is underneath.</summary>
    public void EnterBusyMode() => WindowNative.SetClickThrough(_hwnd, true);

    /// <summary>Where the wheel window is on screen, in physical pixels (valid while it is shown).</summary>
    public (int X, int Y, int Size) ScreenBounds { get; private set; }

    /// <summary>The wheel left the screen (dismissed, dropped on, or cancelled).</summary>
    public event EventHandler? Hidden;

    public void HideWheel()
    {
        if (!_prepared || !IsWheelVisible) return;
        WindowNative.HideNoActivate(_hwnd);
        IsWheelVisible = false;
        Hidden?.Invoke(this, EventArgs.Empty);
        IsInteractive = false;
        AddList.Visibility = Visibility.Collapsed;
        Wheel.Visibility = Visibility.Visible;
        Wheel.SetProgress(null);
        Wheel.ClearHover();
    }

    // OLE asks for the drop effect on every DragOver; None shows the "not allowed" cursor, which tells the user the spot is dead.
    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) || _link is not null;

    // A picture, link or download dragged out of a web browser: the wheel works on a placeholder named after the link; the host downloads
    // the real file only after a slice is picked (and only from the public internet).
    private static Uri? _link;
    private static readonly Dictionary<string, Uri> Links = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The web address behind a placeholder path the wheel received from a browser drag, or null for a real file.</summary>
    public static Uri? LinkFor(string path) => Links.GetValueOrDefault(path);

    private static string? ReadText(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format)) return null;
            return data.GetData(format) switch
            {
                string text => text,
                MemoryStream ms when format is "UniformResourceLocatorW" or "text/x-moz-url" => System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0'),
                MemoryStream ms => System.Text.Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\0'),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException or InvalidOperationException) { return null; }
    }

    private static Uri? ReadLink(IDataObject data) => Core.Sources.DropFormats.LinkFrom(
        html: ReadText(data, DataFormats.Html), uriList: ReadText(data, "text/uri-list"), mozUrl: ReadText(data, "text/x-moz-url"),
        urlW: ReadText(data, "UniformResourceLocatorW"), text: ReadText(data, DataFormats.UnicodeText));

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _link = e.Data.GetDataPresent(DataFormats.FileDrop) ? null : ReadLink(e.Data);
        if (_link is { } link && !IsAddListVisible)
        {
            var placeholder = Path.Combine(Path.GetTempPath(), "DropFlow", "links", Core.Sources.DropFormats.PlaceholderName(link));
            Links[placeholder] = link;
            if (!_files.SequenceEqual([placeholder]))
            {
                _files = [placeholder];
                FilesEntered?.Invoke(this, _files);
            }
            e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Link;
            e.Handled = true;
            return;
        }
        if (!HasFiles(e) || IsAddListVisible) // the "+ Add" list is not a drop target
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var files = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
        if (!files.SequenceEqual(_files))
        {
            _files = files;
            FilesEntered?.Invoke(this, files);
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var idx = IsAddListVisible ? -1 : Wheel.SetPointer(e.GetPosition(Wheel));
        e.Effects = HasFiles(e) && idx >= 0
            ? (_link is not null && !e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Link : DragDropEffects.Copy)
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        Wheel.ClearHover();
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (IsAddListVisible) return;
        var idx = Wheel.WedgeAt(e.GetPosition(Wheel));
        if (!HasFiles(e) || idx < 0 || _files.Length == 0)
        {
            DropCancelled?.Invoke(this, EventArgs.Empty);
            return;
        }
        ItemDropped?.Invoke(this, (_files, idx));
    }
}
