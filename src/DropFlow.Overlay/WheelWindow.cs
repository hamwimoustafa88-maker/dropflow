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

        // #01000000 is almost invisible but still hit-testable; a fully transparent layered window gets no OLE drops.
        _root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), AllowDrop = true };
        _root.Children.Add(Wheel);
        Content = _root;
        AllowDrop = true;

        _root.DragEnter += OnDragEnter;
        _root.DragOver += OnDragOver;
        _root.DragLeave += OnDragLeave;
        _root.Drop += OnDrop;
        Wheel.Cancelled += (_, _) => DropCancelled?.Invoke(this, EventArgs.Empty);
    }

    public WheelControl Wheel { get; }

    public bool IsWheelVisible { get; private set; }

    /// <summary>Files Windows delivered when the drag entered the wheel (empty until then).</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>The drag entered the wheel carrying these files: the host should fill the wedges.</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesEntered;

    /// <summary>The user released the drag on a wedge.</summary>
    public event EventHandler<(IReadOnlyList<string> Files, int Index)>? ItemDropped;

    /// <summary>The user released the drag off the wedges, left the wheel, or pressed Esc.</summary>
    public event EventHandler? DropCancelled;

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

    /// <summary>Shows the wheel centred under <paramref name="point"/> (physical screen pixels).</summary>
    public string ShowAt(PixelPoint point)
    {
        Prepare();
        _files = [];
        Wheel.ClearHover();
        WindowNative.SetClickThrough(_hwnd, false);
        var (x, y, size, dpi) = WindowNative.ShowTopmostCentered(_hwnd, point, SizeDips);
        IsWheelVisible = true;
        Wheel.PlayOpenAnimation();
        return $"wheel at {x},{y} size {size}px dpi {dpi} rect {WindowNative.Describe(_hwnd)}";
    }

    /// <summary>Busy mode: the wheel only shows progress and lets clicks pass through to whatever is underneath.</summary>
    public void EnterBusyMode() => WindowNative.SetClickThrough(_hwnd, true);

    public void HideWheel()
    {
        if (!_prepared || !IsWheelVisible) return;
        WindowNative.HideNoActivate(_hwnd);
        IsWheelVisible = false;
        Wheel.SetProgress(null);
        Wheel.ClearHover();
    }

    // OLE asks for the drop effect on every DragOver; None shows the "not allowed" cursor, which tells the user the spot is dead.
    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e))
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
        var idx = Wheel.SetPointer(e.GetPosition(Wheel));
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
        var idx = Wheel.WedgeAt(e.GetPosition(Wheel));
        if (!HasFiles(e) || idx < 0 || _files.Length == 0)
        {
            DropCancelled?.Invoke(this, EventArgs.Empty);
            return;
        }
        ItemDropped?.Invoke(this, (_files, idx));
    }
}
