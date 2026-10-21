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
        AddList.Picked += (_, id) => AddItemPicked?.Invoke(this, id);
        AddList.Back += (_, _) => AddListBack?.Invoke(this, EventArgs.Empty);
        Deactivated += (_, _) =>
        {
            // clicking anywhere else closes a click-mode wheel
            if (IsInteractive && IsWheelVisible) DropCancelled?.Invoke(this, EventArgs.Empty);
        };
    }

    public WheelControl Wheel { get; }

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

    /// <summary>A row of the "+ Add" list was chosen (its id).</summary>
    public event EventHandler<string>? AddItemPicked;

    /// <summary>Esc or Back in the "+ Add" list.</summary>
    public event EventHandler? AddListBack;

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
        var (x, y, size, dpi) = WindowNative.ShowTopmostCentered(_hwnd, point, SizeDips);
        IsWheelVisible = true;
        Wheel.PlayOpenAnimation();
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

    public void HideWheel()
    {
        if (!_prepared || !IsWheelVisible) return;
        WindowNative.HideNoActivate(_hwnd);
        IsWheelVisible = false;
        IsInteractive = false;
        AddList.Visibility = Visibility.Collapsed;
        Wheel.Visibility = Visibility.Visible;
        Wheel.SetProgress(null);
        Wheel.ClearHover();
    }

    // OLE asks for the drop effect on every DragOver; None shows the "not allowed" cursor, which tells the user the spot is dead.
    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
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
        e.Effects = HasFiles(e) && idx >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
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
