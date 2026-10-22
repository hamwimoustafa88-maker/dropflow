// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// Shows a preview picture scaled to fit, over a checkerboard (so transparency is visible), with an <see cref="Overlay"/> canvas in
/// the picture's own pixel coordinates for handles and shapes, a "hold to see the original" button and a busy badge.
/// The picture area is always left-to-right: a photo must never be mirrored in the Arabic layout.
/// </summary>
internal sealed class PreviewHost : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Grid _surface = new();
    private readonly Viewbox _box;
    private readonly Button _compare;
    private readonly Border _busy;
    private BitmapSource? _rendered;
    private bool _holding;

    public PreviewHost(UiKit ui)
    {
        ClipToBounds = true;
        Background = ui.Theme.Wedge;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        Overlay = new Canvas { Background = Brushes.Transparent, ClipToBounds = false };
        _surface.Children.Add(new Border { Background = Checkerboard() });
        _surface.Children.Add(_image);
        _surface.Children.Add(Overlay);
        _box = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = _surface, Margin = new Thickness(14), FlowDirection = FlowDirection.LeftToRight };
        Children.Add(_box);
        _box.SizeChanged += (_, _) => DisplayScaleChanged?.Invoke();

        _compare = new Button
        {
            Content = ui.Loc["editor.holdOriginal"], Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = ui.Theme.Panel, Foreground = ui.Theme.Text, BorderBrush = ui.Theme.PanelBorder, Visibility = Visibility.Collapsed,
            ToolTip = ui.Loc["editor.holdOriginal.tip"],
        };
        _compare.PreviewMouseLeftButtonDown += (_, e) => { Hold(true); e.Handled = true; _compare.CaptureMouse(); };
        _compare.PreviewMouseLeftButtonUp += (_, e) => { Hold(false); e.Handled = true; _compare.ReleaseMouseCapture(); };
        _compare.LostMouseCapture += (_, _) => Hold(false);
        _compare.PreviewKeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Hold(true); e.Handled = true; } };
        _compare.PreviewKeyUp += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Hold(false); e.Handled = true; } };
        Children.Add(_compare);

        _busy = new Border
        {
            Background = ui.Theme.Panel, BorderBrush = ui.Theme.PanelBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = ui.Loc["editor.rendering"], Foreground = ui.Theme.TextMuted, FontSize = 12 },
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_busy);
    }

    /// <summary>Shapes and handles go here, in preview pixels (0..<see cref="PixelWidth"/>).</summary>
    public Canvas Overlay { get; }

    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    /// <summary>The unedited preview, shown while "hold to see the original" is pressed. Null hides the button.</summary>
    public BitmapSource? Original
    {
        get => _original;
        set
        {
            _original = value;
            _compare.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    private BitmapSource? _original;

    public bool IsHoldingOriginal => _holding;

    /// <summary>Screen pixels per preview pixel (for strokes and handles that keep their size on screen).</summary>
    public double DisplayScale => PixelWidth > 0 && PixelHeight > 0 && _box.ActualWidth > 0
        ? Math.Min(_box.ActualWidth / PixelWidth, _box.ActualHeight / PixelHeight)
        : 1;

    /// <summary>Raised when the shown size changes (editors redraw handles).</summary>
    public event Action? DisplayScaleChanged;

    public void ShowBusy(bool busy) => _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    public void SetImage(BitmapSource bitmap)
    {
        _rendered = bitmap;
        if (!_holding) _image.Source = bitmap;
        if (PixelWidth != bitmap.PixelWidth || PixelHeight != bitmap.PixelHeight)
        {
            PixelWidth = bitmap.PixelWidth;
            PixelHeight = bitmap.PixelHeight;
            _surface.Width = Overlay.Width = PixelWidth;
            _surface.Height = Overlay.Height = PixelHeight;
            _image.Width = PixelWidth;
            _image.Height = PixelHeight;
            Dispatcher.BeginInvoke(() => DisplayScaleChanged?.Invoke(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Hold(bool on)
    {
        if (_holding == on) return;
        _holding = on;
        _image.Source = on && _original is not null ? _original : _rendered;
        Overlay.Visibility = on ? Visibility.Hidden : Visibility.Visible;
    }

    private static Brush Checkerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new SolidColorBrush(Color.FromRgb(0xDD, 0xE0, 0xE8));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
