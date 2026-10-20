using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DropFlow.Hooks;

namespace DropFlow.Spike;

public partial class WheelWindow : Window
{
    private nint _hwnd;

    public WheelWindow() => InitializeComponent();

    /// <summary>Raised on the UI thread with the dropped paths and the wheel that was open.</summary>
    public event Action<string[], string>? FilesDropped;

    public bool IsWheelVisible { get; private set; }

    /// <summary>Creates and renders the window off-screen once; after that visibility is controlled with SetWindowPos only.</summary>
    public void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.MakeNonActivating(_hwnd);
        Show(); // renders content while off-screen at (-32000,-32000)
        WindowNative.HideNoActivate(_hwnd);
    }

    public string ShowAt(ScreenPoint point, ModifierKey key)
    {
        TitleText.Text = key switch { ModifierKey.Shift => "Convert", ModifierKey.Ctrl => "Tools", _ => "Actions" };
        var (x, y, size, dpi) = WindowNative.ShowTopmostCentered(_hwnd, point, Width);
        IsWheelVisible = true;
        return $"wheel at {x},{y} size {size}px dpi {dpi} ({dpi / 96.0:P0}) actual rect {WindowNative.Describe(_hwnd)}";
    }

    public void HideWheel()
    {
        if (!IsWheelVisible) return;
        WindowNative.HideNoActivate(_hwnd);
        IsWheelVisible = false;
        SetHighlight(false);
    }

    private void SetHighlight(bool on)
    {
        Ring.StrokeThickness = on ? 8 : 4;
        Ring.Stroke = new SolidColorBrush(on ? Color.FromRgb(0x22, 0xD3, 0xEE) : Color.FromRgb(0x81, 0x8C, 0xF8));
        HintText.Text = on ? "release to drop" : "drop files here";
    }

    // OLE asks for the drop effect on every DragOver; without Copy the cursor shows "not allowed".
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetHighlight(true);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        SetHighlight(false);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            FilesDropped?.Invoke(files, TitleText.Text);
        HideWheel();
        e.Handled = true;
    }
}
