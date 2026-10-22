// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DropFlow.App.Services;
using ImageMagick;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// Crop (img.crop): the area outside the box is shaded, the four corners resize it, dragging inside moves it, an aspect ratio can be
/// locked, and the exact box can be typed in pixels of the original picture. Fills the converter's x, y, width, height and aspect.
/// </summary>
internal sealed class CropWindow : VisualEditorWindow
{
    public static readonly string[] Aspects = ["free", "1:1", "16:9", "9:16", "4:3", "3:4"];

    private readonly TextBox _x = NumberBox();
    private readonly TextBox _y = NumberBox();
    private readonly TextBox _w = NumberBox();
    private readonly TextBox _h = NumberBox();
    private readonly TextBlock _size;
    private readonly Action<string> _selectAspect;
    private PreviewSource? _source;
    private string _aspect = "free";
    private Box _box;              // original pixels
    private Box _dragStart;
    private Point _dragFrom;
    private BoxHandle _dragging = BoxHandle.None;
    private bool _syncing;
    private bool _newBox;

    public CropWindow(UiKit ui, IReadOnlyList<string> files) : base(ui, ui.Loc["editor.crop.title"], files)
    {
        Side.Children.Add(EditorControls.Label(ui, ui.Loc["editor.crop.aspect"]));
        var (aspects, select) = EditorControls.Segmented(ui, Aspects.Select(a => (a == "free" ? ui.Loc["editor.crop.free"] : a, a)), _aspect, OnAspect);
        _selectAspect = select;
        Side.Children.Add(aspects);

        Side.Children.Add(EditorControls.Label(ui, ui.Loc["editor.crop.exact"]));
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Cell(string key, TextBox box, int row, int col)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = ui.Text(ui.Loc[key], 12, muted: true, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetRow(label, row); Grid.SetColumn(label, col);
            Grid.SetRow(box, row); Grid.SetColumn(box, col + 1);
            box.Margin = new Thickness(0, 2, 10, 2);
            box.TextChanged += (_, _) => OnTyped();
            grid.Children.Add(label);
            grid.Children.Add(box);
        }
        Cell("editor.crop.x", _x, 0, 0);
        Cell("editor.crop.y", _y, 0, 2);
        Cell("editor.crop.width", _w, 1, 0);
        Cell("editor.crop.height", _h, 1, 2);
        Side.Children.Add(grid);

        _size = ui.Text("", 12, muted: true);
        Side.Children.Add(_size);
        var reset = ui.Secondary(ui.Loc["editor.crop.reset"]);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 8, 0, 0);
        reset.Click += (_, _) => SetBox(BoxGeometry.Initial(OrigW, OrigH, BoxGeometry.Ratio(_aspect)));
        Side.Children.Add(reset);
        Side.Children.Add(ui.Text(ui.Loc["editor.crop.hint"], 12, muted: true));

        Preview.Overlay.Cursor = Cursors.Cross;
        Preview.Overlay.MouseLeftButtonDown += OnDown;
        Preview.Overlay.MouseMove += OnMove;
        Preview.Overlay.MouseLeftButtonUp += (_, _) => { _dragging = BoxHandle.None; _newBox = false; Preview.Overlay.ReleaseMouseCapture(); };
        Preview.DisplayScaleChanged += Redraw;
        Loaded += async (_, _) => await LoadAsync();
    }

    public override bool IsReady => _source is not null;

    private double OrigW => _source?.OriginalWidth ?? 1;
    private double OrigH => _source?.OriginalHeight ?? 1;
    private double Scale => _source?.Scale ?? 1;

    internal Box CropBox => _box;

    internal void ChooseAspect(string aspect) { _selectAspect(aspect); OnAspect(aspect); }

    private static TextBox NumberBox() => new() { MinHeight = 28, Padding = new Thickness(4, 3, 4, 3), FlowDirection = FlowDirection.LeftToRight };

    private async Task LoadAsync()
    {
        try
        {
            var path = Files[0];
            _source = await Task.Run(() => PreviewImage.Load(path));
            Preview.SetImage(PreviewImage.ToBitmap(_source.Image));
            SetBox(BoxGeometry.Initial(OrigW, OrigH, null));
        }
        catch (Exception ex) when (ex is MagickException or System.IO.IOException or UnauthorizedAccessException)
        {
            FileLog.Error("crop editor preview", ex);
            Message.Text = Ui.Loc["editor.previewFailed"];
        }
    }

    private void OnAspect(string aspect)
    {
        _aspect = aspect;
        if (_source is null) return;
        SetBox(BoxGeometry.Fit(_box, BoxGeometry.Ratio(aspect), OrigW, OrigH));
    }

    private void SetBox(Box box)
    {
        _box = box;
        _syncing = true;
        var (x, y, w, h) = box.ToPixels(OrigW, OrigH);
        _x.Text = x.ToString(CultureInfo.InvariantCulture);
        _y.Text = y.ToString(CultureInfo.InvariantCulture);
        _w.Text = w.ToString(CultureInfo.InvariantCulture);
        _h.Text = h.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
        _size.Text = Ui.Loc.Format("editor.crop.size", w, h);
        Redraw();
    }

    private void OnTyped()
    {
        if (_syncing || _source is null) return;
        if (!EditorControls.TryNumber(_x.Text, out var x) || !EditorControls.TryNumber(_y.Text, out var y)
            || !EditorControls.TryNumber(_w.Text, out var w) || !EditorControls.TryNumber(_h.Text, out var h) || w < 1 || h < 1)
            return;
        _box = BoxGeometry.Clamp(new Box(x, y, w, h), OrigW, OrigH);
        _size.Text = Ui.Loc.Format("editor.crop.size", (int)Math.Round(_box.W), (int)Math.Round(_box.H));
        Redraw();
    }

    private Point Original(MouseEventArgs e)
    {
        var p = e.GetPosition(Preview.Overlay);
        return new Point(p.X / Scale, p.Y / Scale);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_source is null) return;
        var p = Original(e);
        var grab = BoxOverlay.HandleSize / Math.Max(0.01, Preview.DisplayScale) / Scale;
        _dragging = BoxGeometry.HitTest(_box, p.X, p.Y, grab);
        if (_dragging == BoxHandle.None)
        {
            // start a new box from here
            _box = new Box(p.X, p.Y, 0, 0);
            _dragging = BoxHandle.BottomRight;
            _newBox = true;
        }
        _dragStart = _box;
        _dragFrom = p;
        Preview.Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_source is null) return;
        var p = Original(e);
        if (_dragging == BoxHandle.None)
        {
            var grab = BoxOverlay.HandleSize / Math.Max(0.01, Preview.DisplayScale) / Scale;
            Preview.Overlay.Cursor = BoxGeometry.HitTest(_box, p.X, p.Y, grab) switch
            {
                BoxHandle.TopLeft or BoxHandle.BottomRight => Cursors.SizeNWSE,
                BoxHandle.TopRight or BoxHandle.BottomLeft => Cursors.SizeNESW,
                BoxHandle.Move => Cursors.SizeAll,
                _ => Cursors.Cross,
            };
            return;
        }
        double dx = p.X - _dragFrom.X, dy = p.Y - _dragFrom.Y;
        if (_newBox) _dragging = dx < 0 ? (dy < 0 ? BoxHandle.TopLeft : BoxHandle.BottomLeft) : (dy < 0 ? BoxHandle.TopRight : BoxHandle.BottomRight);
        SetBox(BoxGeometry.Drag(_dragStart, _dragging, dx, dy, BoxGeometry.Ratio(_aspect), OrigW, OrigH));
    }

    private void Redraw()
    {
        var canvas = Preview.Overlay;
        canvas.Children.Clear();
        if (_source is null || Preview.PixelWidth == 0) return;
        var shown = _box.Scaled(Scale);
        BoxOverlay.Shade(canvas, shown, Preview.PixelWidth, Preview.PixelHeight);
        BoxOverlay.Frame(canvas, shown, Preview.DisplayScale, Ui.Theme.Accent, thirds: true, handles: true);
    }

    protected override IReadOnlyDictionary<string, object?>? BuildOptions()
    {
        if (_source is null) return null;
        var (x, y, w, h) = _box.ToPixels(OrigW, OrigH);
        if (x == 0 && y == 0 && w == (int)OrigW && h == (int)OrigH)
        {
            Message.Text = Ui.Loc["editor.nothingChanged"];
            return null;
        }
        return new Dictionary<string, object?>
        {
            ["aspect"] = _aspect == "free" ? "free" : _aspect,
            ["x"] = (double)x, ["y"] = (double)y, ["width"] = (double)w, ["height"] = (double)h,
        };
    }

    protected override void OnClosedCleanup() => _source?.Dispose();
}
