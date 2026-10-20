using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DropFlow.App.Views;

/// <summary>
/// The small card in the corner that shows progress while a batch runs and the result afterwards (with "Open folder" / "Undo").
/// Never takes focus. One toast is reused for every batch.
/// </summary>
internal sealed class ToastWindow : Window
{
    private readonly UiKit _ui;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly StackPanel _actions;
    private readonly DispatcherTimer _hideTimer = new();

    public ToastWindow(UiKit ui)
    {
        _ui = ui;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        FlowDirection = ui.Flow;
        FontFamily = ui.Font;
        FontSize = 13;
        Focusable = false;

        _title = ui.Text("", 14, weight: FontWeights.SemiBold);
        _detail = ui.Text("", 12, muted: true);
        _progress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Foreground = ui.Theme.Accent, Background = ui.Theme.Wedge, BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 8) };
        _actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_detail);
        stack.Children.Add(_progress);
        stack.Children.Add(_actions);

        Content = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = ui.Theme.Panel,
            BorderBrush = ui.Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.28 },
            Child = stack,
        };

        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        SourceInitialized += (_, _) => Overlay.NoActivate.Apply(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Shows or updates the toast. <paramref name="progress"/> null hides the bar; negative = indeterminate.</summary>
    public void Present(string title, string? detail, double? progress, IReadOnlyList<(string Label, Action Run)>? actions = null, TimeSpan? autoHide = null)
    {
        _hideTimer.Stop();
        _title.Text = title;
        _detail.Text = detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = progress is < 0;
        if (progress is >= 0) _progress.Value = progress.Value;

        _actions.Children.Clear();
        foreach (var (label, run) in actions ?? [])
        {
            var b = _ui.Secondary(label);
            b.Margin = new Thickness(_ui.Loc.IsRightToLeft ? 0 : 0, 0, 8, 0);
            b.MinWidth = 70;
            b.Click += (_, _) => run();
            _actions.Children.Add(b);
        }
        _actions.Visibility = _actions.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var wa = SystemParameters.WorkArea;
        Left = _ui.Loc.IsRightToLeft ? wa.Left + 12 : wa.Right - Width - 12;
        // The window is auto-sized and may not be shown yet, so measure the content to know the real height.
        ((FrameworkElement)Content).Measure(new Size(Width, double.PositiveInfinity));
        Top = wa.Bottom - ((FrameworkElement)Content).DesiredSize.Height - 12;
        if (!IsVisible) Show();

        if (autoHide is { } t)
        {
            _hideTimer.Interval = t;
            _hideTimer.Start();
        }
    }
}

/// <summary>Marks a window as a tool window that never takes focus.</summary>
internal static class Overlay
{
    public static class NoActivate
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint Get(nint h, int i);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint Set(nint h, int i, nint v);

        public static void Apply(nint hwnd) => Set(hwnd, GWL_EXSTYLE, (nint)((long)Get(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }
}
