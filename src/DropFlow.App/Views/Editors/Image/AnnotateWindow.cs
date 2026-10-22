// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DropFlow.App.Services;
using DropFlow.Engines.Images;
using ImageMagick;
using TextAlignment = System.Windows.TextAlignment;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// Annotate (img.annotate): arrows, lines, boxes, ellipses, highlighter, freehand pen, text (Arabic and English, typeset by Windows
/// and saved as picture overlays so it looks exactly as on screen), numbered callouts, a magnifier and pictures from a file or the
/// clipboard. Select to move or delete, Ctrl+Z to undo. The result is the converter's <c>items</c> list in relative units.
/// </summary>
internal sealed class AnnotateWindow : VisualEditorWindow
{
    public static readonly string[] Tools = ["select", "arrow", "line", "rect", "ellipse", "highlight", "pen", "text", "number", "magnify"];

    private static readonly FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI, Tahoma");

    private readonly TextBox _text = new() { MinHeight = 56, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4) };
    private readonly string _overlayDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DropFlow", "annotate", Guid.NewGuid().ToString("N"));
    private readonly Action<string> _selectTool;
    private PreviewSource? _source;
    private BitmapSource? _bitmap;
    private AnnotationDocument? _doc;
    private string _tool = "arrow";
    private string _color = "#ef4444";
    private double _strokeLevel = 4;     // 1..20, relative to the picture
    private double _sizeLevel = 5;       // text/number size, % of the long side
    private double _zoom = 2;
    private bool _fill;
    private int _selected = -1;
    private int _drawing = -1;
    private Point _from;
    private AnnotationShape? _dragStart;
    private bool _moved;

    public AnnotateWindow(UiKit ui, IReadOnlyList<string> files) : base(ui, ui.Loc["editor.annotate.title"], files)
    {
        var (tools, select) = EditorControls.Segmented(ui, Tools.Select(t => (ui.Loc["editor.annotate.tool." + t], t)), _tool, t => { _tool = t; UpdateCursor(); });
        _selectTool = select;
        AddTool(tools);

        Side.Children.Add(EditorControls.Label(ui, ui.Loc["editor.annotate.color"]));
        Side.Children.Add(EditorControls.ColorPicker(ui, _color, c => { _color = c; RestyleSelected(s => s with { Color = c }); }).Panel);
        Side.Children.Add(EditorControls.SliderRow(ui, ui.Loc["editor.annotate.thickness"], 1, 20, _strokeLevel, 4, v => { _strokeLevel = Math.Round(v); RestyleSelected(s => s.Type is "text" or "number" or "image" ? s : s with { Stroke = StrokePx }); }).Row);
        Side.Children.Add(EditorControls.SliderRow(ui, ui.Loc["editor.annotate.textSize"], 2, 20, _sizeLevel, 5, v => { _sizeLevel = Math.Round(v); RestyleSelected(s => s.Type is "text" or "number" ? Measure(s with { Size = SizePx }) : s); }).Row);
        Side.Children.Add(EditorControls.Check(ui, ui.Loc["editor.annotate.fill"], _fill, v => { _fill = v; RestyleSelected(s => s.Type is "rect" or "ellipse" or "text" ? Measure(s with { Fill = v }) : s); }));

        Side.Children.Add(EditorControls.Label(ui, ui.Loc["editor.annotate.text"]));
        _text.Text = ui.Loc["editor.annotate.sampleText"];
        _text.TextChanged += (_, _) => RestyleSelected(s => s.Type == "text" ? Measure(s with { Text = _text.Text }) : s, undoStep: false);
        Side.Children.Add(_text);
        Side.Children.Add(EditorControls.SliderRow(ui, ui.Loc["editor.annotate.zoom"], 1.5, 4, _zoom, 2, v => { _zoom = Math.Round(v * 4) / 4; RestyleSelected(s => s.Type == "magnify" ? s with { Zoom = _zoom } : s); }, "0.##").Row);

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (key, act) in new (string, Action)[]
        {
            ("editor.annotate.addPicture", AddPictureFromFile), ("editor.annotate.paste", PastePicture),
            ("editor.undo", OnUndo), ("editor.delete", OnDeleteKey), ("editor.clear", () => { _doc?.Clear(); _selected = -1; Redraw(); }),
        })
        {
            var b = ui.Secondary(ui.Loc[key]);
            b.MinWidth = 0;
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Click += (_, _) => act();
            actions.Children.Add(b);
        }
        Side.Children.Add(actions);
        Side.Children.Add(ui.Text(ui.Loc["editor.annotate.hint"], 12, muted: true));

        Preview.Overlay.MouseLeftButtonDown += OnDown;
        Preview.Overlay.MouseMove += OnMove;
        Preview.Overlay.MouseLeftButtonUp += OnUp;
        Preview.DisplayScaleChanged += Redraw;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { PastePicture(); e.Handled = true; }
        };
        Loaded += async (_, _) => await LoadAsync();
        UpdateCursor();
    }

    public override bool IsReady => _doc is not null;

    internal AnnotationDocument? Document => _doc;

    internal void ChooseTool(string tool) { _selectTool(tool); _tool = tool; }

    /// <summary>Test and snapshot hook: adds a shape (preview pixels) as if it had been drawn.</summary>
    internal void AddShape(AnnotationShape shape) { _doc?.Add(Measure(shape)); Redraw(); }

    private double Scale => _source?.Scale ?? 1;
    private double LongSide => _doc?.LongSide ?? 1000;
    private double StrokePx => Math.Max(1, _strokeLevel * LongSide / 400);
    private double SizePx => _sizeLevel / 100 * LongSide;

    private async Task LoadAsync()
    {
        try
        {
            var path = Files[0];
            _source = await Task.Run(() => PreviewImage.Load(path));
            _bitmap = PreviewImage.ToBitmap(_source.Image);
            Preview.SetImage(_bitmap);
            _doc = new AnnotationDocument(_bitmap.PixelWidth, _bitmap.PixelHeight);
            Redraw();
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("annotate editor preview", ex);
            Message.Text = Ui.Loc["editor.previewFailed"];
        }
    }

    private void UpdateCursor() => Preview.Overlay.Cursor = _tool switch
    {
        "select" => Cursors.Arrow,
        "text" => Cursors.IBeam,
        _ => Cursors.Cross,
    };

    // ------------------------------------------------------------------------------------------------------------ mouse

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_doc is null) return;
        var p = e.GetPosition(Preview.Overlay);
        _from = p;
        _moved = false;
        var tol = 6 / Math.Max(0.01, Preview.DisplayScale);
        var hit = _doc.HitTest(p.X, p.Y, tol, Scale);

        if (_tool == "select" || (hit >= 0 && hit == _selected))
        {
            _selected = hit;
            if (hit >= 0)
            {
                _doc.Checkpoint();
                _dragStart = _doc.Shapes[hit];
                Preview.Overlay.CaptureMouse();
                SyncSideToSelection();
            }
            Redraw();
            return;
        }

        var shape = _tool switch
        {
            "text" => Measure(new AnnotationShape("text", p.X, p.Y, p.X, p.Y, _color, StrokePx, _fill, string.IsNullOrWhiteSpace(_text.Text) ? Ui.Loc["editor.annotate.sampleText"] : _text.Text, SizePx)),
            "number" => new AnnotationShape("number", p.X, p.Y, p.X, p.Y, _color, StrokePx, Size: SizePx, N: _doc.NextNumber),
            "magnify" => new AnnotationShape("magnify", p.X, p.Y, p.X, p.Y, _color, Math.Max(1, StrokePx / 2), Size: LongSide * 0.06, Zoom: _zoom),
            "pen" => new AnnotationShape("pen", p.X, p.Y, p.X, p.Y, _color, StrokePx, Points: [p.X, p.Y]),
            _ => new AnnotationShape(_tool, p.X, p.Y, p.X, p.Y, _color, StrokePx, _fill && _tool is "rect" or "ellipse"),
        };
        _selected = _drawing = _doc.Add(shape);
        if (_tool is "text" or "number") _drawing = -1;      // placed with one click
        else Preview.Overlay.CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_doc is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Preview.Overlay);
        p = new Point(Math.Clamp(p.X, 0, _doc.Width), Math.Clamp(p.Y, 0, _doc.Height));
        if (Math.Abs(p.X - _from.X) + Math.Abs(p.Y - _from.Y) > 2) _moved = true;

        if (_drawing >= 0)
        {
            var s = _doc.Shapes[_drawing];
            s = s.Type == "pen"
                ? s with { X2 = p.X, Y2 = p.Y, Points = [.. s.Points ?? [], p.X, p.Y] }
                : s with { X2 = p.X, Y2 = p.Y };
            _doc.Replace(_drawing, s);
            Redraw();
        }
        else if (_dragStart is not null && _selected >= 0)
        {
            _doc.Replace(_selected, _dragStart.Moved(p.X - _from.X, p.Y - _from.Y));
            Redraw();
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        Preview.Overlay.ReleaseMouseCapture();
        if (_doc is not null && _drawing >= 0)
        {
            var s = _doc.Shapes[_drawing];
            var tiny = s.Type switch
            {
                "pen" => (s.Points?.Length ?? 0) < 4,
                "magnify" => false,
                _ => Math.Abs(s.X2 - s.X) + Math.Abs(s.Y2 - s.Y) < 4,
            };
            if (s.Type == "magnify" && !_moved)
            {
                // a click: put the lens beside the spot, towards the middle of the picture
                var dx = s.X < _doc.Width / 2 ? 1 : -1;
                _doc.Replace(_drawing, s with { X2 = s.X + dx * s.Size * (s.Zoom + 1.4), Y2 = s.Y });
            }
            else if (tiny)
            {
                _doc.Undo();
                _selected = -1;
            }
        }
        else if (_doc is not null && _dragStart is not null && !_moved)
        {
            _doc.DiscardCheckpoint();
        }
        _drawing = -1;
        _dragStart = null;
        Redraw();
    }

    private void SyncSideToSelection()
    {
        if (_doc is null || _selected < 0) return;
        var s = _doc.Shapes[_selected];
        if (s.Type == "text" && s.Text is not null && _text.Text != s.Text)
        {
            var keep = _selected;
            _text.Text = s.Text;
            _selected = keep;
        }
    }

    private void RestyleSelected(Func<AnnotationShape, AnnotationShape> change, bool undoStep = true)
    {
        if (_doc is null || _selected < 0 || _selected >= _doc.Shapes.Count) return;
        var before = _doc.Shapes[_selected];
        var after = change(before);
        if (after == before) return;
        if (undoStep) _doc.Checkpoint();
        _doc.Replace(_selected, after);
        Redraw();
    }

    protected override void OnUndo()
    {
        if (_doc?.Undo() == true)
        {
            if (_selected >= _doc.Shapes.Count) _selected = -1;
            Redraw();
        }
    }

    protected override void OnDeleteKey()
    {
        if (_doc is null || _selected < 0) return;
        _doc.Remove(_selected);
        _selected = -1;
        Redraw();
    }

    // ------------------------------------------------------------------------------------------------------------ pictures

    private void AddPictureFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Ui.Loc["editor.annotate.addPicture"],
            Filter = Ui.Loc["editor.pictureFilter"] + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
        };
        if (dialog.ShowDialog(this) == true) AddPicture(dialog.FileName);
    }

    private void PastePicture()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                Directory.CreateDirectory(_overlayDir);
                var path = System.IO.Path.Combine(_overlayDir, $"pasted-{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, PreviewImage.ToPng(image));
                AddPicture(path);
            }
            else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } list && list[0] is { } file)
            {
                AddPicture(file);
            }
            else
            {
                Message.Text = Ui.Loc["editor.annotate.noPicture"];
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("annotate paste", ex);
            Message.Text = Ui.Loc["editor.annotate.noPicture"];
        }
    }

    private void AddPicture(string path)
    {
        if (_doc is null) return;
        try
        {
            var info = new MagickImageInfo(path);
            var w = _doc.Width * 0.3;
            var h = w * info.Height / Math.Max(1, info.Width);
            var x = (_doc.Width - w) / 2;
            var y = (_doc.Height - h) / 2;
            _selected = _doc.Add(new AnnotationShape("image", x, y, x + w, y + h, _color, 0, ImagePath: path));
            _tool = "select";
            _selectTool("select");
            UpdateCursor();
            Redraw();
        }
        catch (MagickException ex)
        {
            FileLog.Error("annotate picture", ex);
            Message.Text = Ui.Loc["editor.annotate.noPicture"];
        }
    }

    // ------------------------------------------------------------------------------------------------------------ text

    private FormattedText Format(AnnotationShape s, double size)
    {
        var rtl = AnnotationDocument.IsRightToLeft(s.Text);
        var fill = s.Fill ? ContrastOn(s.Color) : EditorControls.Brush(s.Color);
        return new FormattedText(s.Text ?? "", rtl ? CultureInfo.GetCultureInfo("ar") : CultureInfo.InvariantCulture,
            rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, new Typeface(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Math.Max(4, size), fill, 1.0) { TextAlignment = TextAlignment.Left };
    }

    private static Brush ContrastOn(string hex)
    {
        var c = EditorControls.ColorOf(hex);
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150 ? Brushes.Black : Brushes.White;
    }

    /// <summary>Sets X2/Y2 of a text shape from its typeset size (with padding for the optional background).</summary>
    private AnnotationShape Measure(AnnotationShape s)
    {
        if (s.Type != "text") return s;
        var ft = Format(s, s.Size);
        var pad = s.Fill ? s.Size * 0.35 : s.Size * 0.08;
        return s with { X2 = s.X + ft.WidthIncludingTrailingWhitespace + 2 * pad, Y2 = s.Y + ft.Height + 2 * pad };
    }

    /// <summary>Draws the text into a transparent PNG at the full-size resolution and returns its path.</summary>
    private string RenderText(AnnotationShape s)
    {
        Directory.CreateDirectory(_overlayDir);
        var factor = 1 / Scale;                                       // preview pixels -> full-size pixels
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) DrawText(dc, s, factor);
        var w = (int)Math.Ceiling(s.Width * factor);
        var h = (int)Math.Ceiling(s.Height * factor);
        var bitmap = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var path = System.IO.Path.Combine(_overlayDir, $"text-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, PreviewImage.ToPng(bitmap));
        return path;
    }

    private void DrawText(DrawingContext dc, AnnotationShape s, double factor)
    {
        var size = s.Size * factor;
        var pad = s.Fill ? size * 0.35 : size * 0.08;
        var ft = Format(s, size);
        var w = ft.WidthIncludingTrailingWhitespace + 2 * pad;
        var h = ft.Height + 2 * pad;
        if (s.Fill) dc.DrawRoundedRectangle(EditorControls.Brush(s.Color), null, new Rect(0, 0, w, h), size * 0.3, size * 0.3);
        // right-to-left text is laid out from its right edge
        dc.DrawText(ft, new Point(AnnotationDocument.IsRightToLeft(s.Text) ? w - pad : pad, pad));
    }

    // ------------------------------------------------------------------------------------------------------------ drawing

    private void Redraw()
    {
        var canvas = Preview.Overlay;
        canvas.Children.Clear();
        if (_doc is null || Preview.PixelWidth == 0) return;
        for (var i = 0; i < _doc.Shapes.Count; i++) Draw(canvas, _doc.Shapes[i]);
        if (_selected >= 0 && _selected < _doc.Shapes.Count) DrawSelection(canvas, _doc.Shapes[_selected]);
    }

    private void Draw(Canvas canvas, AnnotationShape s)
    {
        var brush = EditorControls.Brush(s.Color);
        switch (s.Type)
        {
            case "rect":
                canvas.Children.Add(At(new Rectangle { Width = s.Width, Height = s.Height, Stroke = brush, StrokeThickness = s.Stroke, Fill = s.Fill ? brush : null }, s.Left, s.Top));
                break;
            case "ellipse":
                canvas.Children.Add(At(new Ellipse { Width = s.Width, Height = s.Height, Stroke = brush, StrokeThickness = s.Stroke, Fill = s.Fill ? brush : null }, s.Left, s.Top));
                break;
            case "highlight":
            {
                var c = EditorControls.ColorOf(s.Color);
                canvas.Children.Add(At(new Rectangle { Width = s.Width, Height = s.Height, Fill = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B)) }, s.Left, s.Top));
                break;
            }
            case "line":
                canvas.Children.Add(new Line { X1 = s.X, Y1 = s.Y, X2 = s.X2, Y2 = s.Y2, Stroke = brush, StrokeThickness = s.Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case "arrow":
            {
                // the same geometry as the engine: head length max(14 full-size pixels, 4 × stroke), shaft stops inside the head
                var angle = Math.Atan2(s.Y2 - s.Y, s.X2 - s.X);
                var len = Math.Max(14 * Scale, s.Stroke * 4);
                var back = len * 0.6;
                canvas.Children.Add(new Line { X1 = s.X, Y1 = s.Y, X2 = s.X2 - back * Math.Cos(angle), Y2 = s.Y2 - back * Math.Sin(angle), Stroke = brush, StrokeThickness = s.Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                Point P(double a) => new(s.X2 - len * Math.Cos(angle + a), s.Y2 - len * Math.Sin(angle + a));
                canvas.Children.Add(new Polygon { Points = [new Point(s.X2, s.Y2), P(Math.PI / 7), P(-Math.PI / 7)], Fill = brush, Stroke = brush, StrokeThickness = 1 });
                break;
            }
            case "pen" when s.Points is { Length: >= 2 } pts:
            {
                var line = new Polyline { Stroke = brush, StrokeThickness = s.Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                for (var i = 0; i + 1 < pts.Length; i += 2) line.Points.Add(new Point(pts[i], pts[i + 1]));
                canvas.Children.Add(line);
                break;
            }
            case "number":
            {
                var r = s.NumberRadius(Scale);
                canvas.Children.Add(At(new Ellipse { Width = 2 * r, Height = 2 * r, Fill = brush, Stroke = Brushes.White, StrokeThickness = Math.Max(2 * Scale, r / 8) }, s.X - r, s.Y - r));
                var label = new TextBlock { Text = s.N.ToString(CultureInfo.InvariantCulture), Foreground = Brushes.White, FontSize = r * 1.3, FontWeight = FontWeights.Bold, Width = 2 * r, TextAlignment = TextAlignment.Center };
                canvas.Children.Add(At(label, s.X - r, s.Y - r * 0.95));
                break;
            }
            case "magnify" when _bitmap is not null:
            {
                var lens = s.Size * s.Zoom;
                var view = new Rect(s.X - s.Size, s.Y - s.Size, 2 * s.Size, 2 * s.Size);
                var fill = new ImageBrush(_bitmap) { Viewbox = view, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                canvas.Children.Add(At(new Ellipse { Width = 2 * lens, Height = 2 * lens, Fill = fill, Stroke = brush, StrokeThickness = s.Stroke }, s.X2 - lens, s.Y2 - lens));
                break;
            }
            case "image" when s.ImagePath is not null:
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(s.ImagePath);
                    image.EndInit();
                    canvas.Children.Add(At(new Image { Source = image, Width = s.Width, Height = s.Height, Stretch = Stretch.Fill }, s.Left, s.Top));
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
                {
                    FileLog.Error("annotate overlay preview", ex);
                }
                break;
            }
            case "text":
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) DrawText(dc, s, 1);
                var host = new Border { Width = s.Width, Height = s.Height, Background = new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top } };
                canvas.Children.Add(At(host, s.Left, s.Top));
                break;
            }
        }
    }

    private void DrawSelection(Canvas canvas, AnnotationShape s)
    {
        var px = 1 / Math.Max(0.01, Preview.DisplayScale);
        Rect bounds = s.Type switch
        {
            "number" => new Rect(s.X - s.NumberRadius(Scale), s.Y - s.NumberRadius(Scale), 2 * s.NumberRadius(Scale), 2 * s.NumberRadius(Scale)),
            "magnify" => new Rect(s.X2 - s.Size * s.Zoom, s.Y2 - s.Size * s.Zoom, 2 * s.Size * s.Zoom, 2 * s.Size * s.Zoom),
            "pen" when s.Points is { Length: >= 2 } p => new Rect(new Point(p.Where((_, i) => i % 2 == 0).Min(), p.Where((_, i) => i % 2 == 1).Min()),
                new Point(p.Where((_, i) => i % 2 == 0).Max(), p.Where((_, i) => i % 2 == 1).Max())),
            _ => new Rect(s.Left, s.Top, s.Width, s.Height),
        };
        bounds.Inflate(s.Stroke / 2 + 4 * px, s.Stroke / 2 + 4 * px);
        canvas.Children.Add(At(new Rectangle { Width = bounds.Width, Height = bounds.Height, Stroke = Ui.Theme.Accent, StrokeThickness = 1.5 * px, StrokeDashArray = [4, 3], IsHitTestVisible = false }, bounds.X, bounds.Y));
    }

    private static UIElement At(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        if (element is FrameworkElement fe) fe.IsHitTestVisible = false;
        return element;
    }

    // ------------------------------------------------------------------------------------------------------------ result

    protected override IReadOnlyDictionary<string, object?>? BuildOptions()
    {
        if (_doc is null) return null;
        if (_doc.Shapes.Count == 0)
        {
            Message.Text = Ui.Loc["editor.annotate.empty"];
            return null;
        }
        var items = _doc.ToItems(RenderText);
        return new Dictionary<string, object?> { ["items"] = AnnotationRenderer.ToJson(items), ["units"] = "relative" };
    }

    protected override void OnClosedCleanup() => _source?.Dispose();
}
