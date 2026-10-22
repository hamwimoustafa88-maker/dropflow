// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DropFlow.App.Services;
using DropFlow.Engines.Images;
using ImageMagick;

namespace DropFlow.App.Views.Editors;

/// <summary>What the redact editor can find by itself. Each part is optional: a missing one hides its button.</summary>
internal interface IRedactDetectors
{
    /// <summary>True when the face model is already on this PC (nothing is downloaded from here).</summary>
    bool FacesAvailable { get; }

    /// <summary>True when an OCR engine and language are available.</summary>
    Task<bool> TextAvailableAsync();

    /// <summary>Boxes in pixels of the upright original picture.</summary>
    Task<IReadOnlyList<Box>> FacesAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<Box>> TextAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Redact (img.redact): draw boxes, move and resize them, choose solid colour, pixelate or blur, and optionally let DropFlow find faces
/// and text. The preview runs the engine's own <see cref="RedactRenderer"/>, so what is shown is what gets saved.
/// </summary>
internal sealed class RedactWindow : VisualEditorWindow
{
    private readonly PreviewScheduler<BitmapSource> _renders = new();
    private readonly List<Box> _boxes = [];          // original pixels
    private readonly Stack<List<Box>> _undo = new();
    private readonly IRedactDetectors? _detectors;
    private readonly Button _faces;
    private readonly Button _text;
    private readonly TextBlock _count;
    private readonly TextBlock _detectLabel;
    private readonly CancellationTokenSource _closing = new();
    private PreviewSource? _source;
    private string _style = "solid";
    private string _color = "#000000";
    private int _selected = -1;
    private BoxHandle _dragging = BoxHandle.None;
    private Box _dragStart;
    private Point _dragFrom;
    private bool _newBox;

    public RedactWindow(UiKit ui, IReadOnlyList<string> files, IRedactDetectors? detectors) : base(ui, ui.Loc["editor.redact.title"], files)
    {
        _detectors = detectors;
        Side.Children.Add(ui.Text(ui.Loc["editor.redact.hint"], 12, muted: true));

        Side.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.redact.style"]));
        var styles = new[] { "solid", "pixelate", "blur" }.Select(s => (ui.Loc["opt.img.redact.style." + s], s));
        Side.Children.Add(EditorControls.Segmented(ui, styles, _style, s => { _style = s; Render(); }).Panel);

        Side.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.redact.color"]));
        Side.Children.Add(EditorControls.ColorPicker(ui, _color, c => { _color = c; Render(); }).Panel);

        _detectLabel = EditorControls.Label(ui, ui.Loc["editor.redact.detect"]);
        _detectLabel.Visibility = Visibility.Collapsed;
        Side.Children.Add(_detectLabel);
        _faces = ui.Secondary(ui.Loc["editor.redact.faces"]);
        _text = ui.Secondary(ui.Loc["editor.redact.text"]);
        _faces.Visibility = _text.Visibility = Visibility.Collapsed;
        _faces.Click += async (_, _) => await DetectAsync(_faces, (d, p, ct) => d.FacesAsync(p, ct));
        _text.Click += async (_, _) => await DetectAsync(_text, (d, p, ct) => d.TextAsync(p, ct));
        var detect = new WrapPanel { Children = { _faces, _text } };
        foreach (Button b in detect.Children) b.Margin = new Thickness(0, 0, 8, 8);
        Side.Children.Add(detect);

        _count = ui.Text("", 12, muted: true);
        Side.Children.Add(_count);
        var row = new WrapPanel();
        foreach (var (key, act) in new (string, Action)[] { ("editor.delete", DeleteSelected), ("editor.undo", OnUndo), ("editor.clear", () => Change(() => _boxes.Clear())) })
        {
            var b = ui.Secondary(ui.Loc[key]);
            b.MinWidth = 0;
            b.Margin = new Thickness(0, 6, 8, 0);
            b.Click += (_, _) => act();
            row.Children.Add(b);
        }
        Side.Children.Add(row);

        Preview.Overlay.Cursor = Cursors.Cross;
        Preview.Overlay.MouseLeftButtonDown += OnDown;
        Preview.Overlay.MouseMove += OnMove;
        Preview.Overlay.MouseLeftButtonUp += (_, _) => EndDrag();
        Preview.DisplayScaleChanged += Redraw;
        _renders.BusyChanged += () => Preview.ShowBusy(_renders.IsBusy);
        Loaded += async (_, _) => await LoadAsync();
    }

    public override bool IsReady => _source is not null && !_renders.IsBusy;

    internal IReadOnlyList<Box> Boxes => _boxes;

    internal bool FacesButtonVisible => _faces.Visibility == Visibility.Visible;

    internal bool TextButtonVisible => _text.Visibility == Visibility.Visible;

    /// <summary>Test and snapshot hook: adds a box in original pixels.</summary>
    internal void AddBox(Box box) => Change(() => { _boxes.Add(box); _selected = _boxes.Count - 1; });

    private double OrigW => _source?.OriginalWidth ?? 1;
    private double OrigH => _source?.OriginalHeight ?? 1;
    private double Scale => _source?.Scale ?? 1;

    private async Task LoadAsync()
    {
        try
        {
            var path = Files[0];
            _source = await Task.Run(() => PreviewImage.Load(path));
            var original = PreviewImage.ToBitmap(_source.Image);
            Preview.Original = original;
            Preview.SetImage(original);
            Redraw();
            Render();
        }
        catch (Exception ex) when (ex is MagickException or System.IO.IOException or UnauthorizedAccessException)
        {
            FileLog.Error("redact editor preview", ex);
            Message.Text = Ui.Loc["editor.previewFailed"];
            return;
        }

        if (_detectors is null) return;
        if (_detectors.FacesAvailable) _faces.Visibility = _detectLabel.Visibility = Visibility.Visible;
        try
        {
            if (await _detectors.TextAvailableAsync()) _text.Visibility = _detectLabel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            FileLog.Error("redact: OCR check", ex);
        }
    }

    private async Task DetectAsync(Button button, Func<IRedactDetectors, string, CancellationToken, Task<IReadOnlyList<Box>>> find)
    {
        if (_detectors is null || _source is null) return;
        button.IsEnabled = false;
        Message.Text = "";
        try
        {
            var found = await find(_detectors, Files[0], _closing.Token);
            if (found.Count == 0) Message.Text = Ui.Loc["editor.redact.noneFound"];
            else Change(() => _boxes.AddRange(found.Select(b => BoxGeometry.Clamp(b, OrigW, OrigH))));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // the AI and OCR engines report many kinds of failure; none of them may take the editor down
            FileLog.Error("redact: detection", ex);
            Message.Text = Ui.Loc.Format("editor.redact.detectFailed", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Change(Action change)
    {
        _undo.Push([.. _boxes]);
        change();
        if (_selected >= _boxes.Count) _selected = _boxes.Count - 1;
        Redraw();
        Render();
    }

    protected override void OnUndo()
    {
        if (_undo.Count == 0) return;
        _boxes.Clear();
        _boxes.AddRange(_undo.Pop());
        _selected = Math.Min(_selected, _boxes.Count - 1);
        Redraw();
        Render();
    }

    protected override void OnDeleteKey() => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _boxes.Count) return;
        var index = _selected;
        Change(() => { _boxes.RemoveAt(index); _selected = -1; });
    }

    private Point Original(MouseEventArgs e)
    {
        var p = e.GetPosition(Preview.Overlay);
        return new Point(p.X / Scale, p.Y / Scale);
    }

    private double Grab => BoxOverlay.HandleSize / Math.Max(0.01, Preview.DisplayScale) / Scale;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_source is null) return;
        var p = Original(e);
        _dragFrom = p;
        // the selected box first (its handles), then any box under the pointer (topmost first)
        if (_selected >= 0 && BoxGeometry.HitTest(_boxes[_selected], p.X, p.Y, Grab) is not BoxHandle.None and var h)
        {
            _dragging = h;
        }
        else
        {
            _selected = -1;
            for (var i = _boxes.Count - 1; i >= 0; i--)
                if (_boxes[i].Contains(p.X, p.Y)) { _selected = i; break; }
            if (_selected >= 0) _dragging = BoxHandle.Move;
            else
            {
                _undo.Push([.. _boxes]);
                _boxes.Add(new Box(p.X, p.Y, 0, 0));
                _selected = _boxes.Count - 1;
                _dragging = BoxHandle.BottomRight;
                _newBox = true;
            }
        }
        if (!_newBox) _undo.Push([.. _boxes]);
        _dragStart = _boxes[_selected];
        Preview.Overlay.CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_source is null) return;
        var p = Original(e);
        if (_dragging == BoxHandle.None || _selected < 0)
        {
            var over = _selected >= 0 ? BoxGeometry.HitTest(_boxes[_selected], p.X, p.Y, Grab) : BoxHandle.None;
            if (over == BoxHandle.None && _boxes.Any(b => b.Contains(p.X, p.Y))) over = BoxHandle.Move;
            Preview.Overlay.Cursor = over switch
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
        _boxes[_selected] = BoxGeometry.Drag(_dragStart, _dragging, dx, dy, null, OrigW, OrigH, minSize: 4);
        Redraw();
        Render();
    }

    private void EndDrag()
    {
        if (_dragging == BoxHandle.None) return;
        Preview.Overlay.ReleaseMouseCapture();
        // a click without a drag does not leave a tiny box behind
        if (_newBox && _selected >= 0 && _boxes[_selected] is { W: < 6 } or { H: < 6 })
        {
            _boxes.RemoveAt(_selected);
            _selected = -1;
            _undo.Pop();
        }
        _dragging = BoxHandle.None;
        _newBox = false;
        Redraw();
        Render();
    }

    private void Redraw()
    {
        var canvas = Preview.Overlay;
        canvas.Children.Clear();
        _count.Text = Ui.Loc.Format("editor.redact.count", _boxes.Count);
        if (_source is null || Preview.PixelWidth == 0) return;
        var px = 1 / Math.Max(0.01, Preview.DisplayScale);
        for (var i = 0; i < _boxes.Count; i++)
        {
            var shown = _boxes[i].Scaled(Scale);
            if (i == _selected)
            {
                BoxOverlay.Frame(canvas, shown, Preview.DisplayScale, Ui.Theme.Accent, thirds: false, handles: true);
                continue;
            }
            var r = new Rectangle { Width = shown.W, Height = shown.H, Stroke = Ui.Theme.Highlight, StrokeThickness = 1.5 * px, StrokeDashArray = [4, 3], IsHitTestVisible = false };
            Canvas.SetLeft(r, shown.X);
            Canvas.SetTop(r, shown.Y);
            canvas.Children.Add(r);
        }
    }

    private void Render()
    {
        if (_source is not { } source) return;
        var regions = _boxes.Select(b => b.Scaled(source.Scale).ToPixels(source.Image.Width, source.Image.Height)).ToList();
        var style = _style;
        var color = _color;
        _ = _renders.RequestAsync(ct =>
        {
            MagickImage copy;
            lock (source) { ct.ThrowIfCancellationRequested(); copy = (MagickImage)source.Image.Clone(); }
            using (copy)
            {
                RedactRenderer.Apply(copy, regions, style, RedactRenderer.ColorOf(color));
                return PreviewImage.ToBitmap(copy);
            }
        }, Preview.SetImage, ex => FileLog.Error("redact preview", ex));
    }

    protected override IReadOnlyDictionary<string, object?>? BuildOptions()
    {
        if (_boxes.Count == 0)
        {
            Message.Text = Ui.Loc["editor.redact.empty"];
            return null;
        }
        var regions = string.Join(";", _boxes.Select(b =>
        {
            var (x, y, w, h) = b.ToPixels(OrigW, OrigH);
            return string.Create(CultureInfo.InvariantCulture, $"{x},{y},{w},{h}");
        }));
        return new Dictionary<string, object?> { ["regions"] = regions, ["style"] = _style, ["color"] = _color };
    }

    protected override void OnClosedCleanup()
    {
        _closing.Cancel();
        _renders.Cancel();
        if (_source is { } s) lock (s) s.Dispose();
    }
}
