// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Crop (vid.crop): a box over the picture, moved and resized with the mouse (handles on the corners and sides), shaped by an aspect
/// preset or free, or typed in pixels. Scrub the timeline to check the box against any moment of the video.
/// </summary>
internal sealed class MediaCropEditor : MediaEditorWindow
{
    private readonly MediaCropModel _model;
    private readonly ComboBox _aspect = new() { MinHeight = 28, MinWidth = 120 };
    private readonly TextBox _x, _y, _w, _h;
    private BoxHandle _handle;
    private Point _from;
    private PixelBox _startBox;
    private bool _syncing;

    public MediaCropEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.crop.run")
    {
        _model = new MediaCropModel(Math.Max(2, media.DisplayWidth), Math.Max(2, media.DisplayHeight));

        foreach (var a in MediaCropModel.Aspects)
            _aspect.Items.Add(new ComboBoxItem { Content = a == "free" ? Loc["media.crop.free"] : a, Tag = a });
        _aspect.SelectedIndex = Array.IndexOf(MediaCropModel.Aspects, _model.Aspect);
        _aspect.SelectionChanged += (_, _) =>
        {
            if (_syncing || _aspect.SelectedItem is not ComboBoxItem { Tag: string a }) return;
            _model.SetAspect(a);
            Sync();
        };

        _x = NumberBox(); _y = NumberBox(); _w = NumberBox(); _h = NumberBox();
        Panel.Children.Add(Row(Label("media.crop.aspect"), _aspect, new Border { Width = 18 },
            Label("media.crop.x"), _x, Label("media.crop.y"), _y, Label("media.crop.w"), _w, Label("media.crop.h"), _h));
        Panel.Children.Add(Row(SmallButton("media.crop.reset", () => { _model.SetAspect("free"); _model.SetPixels(0, 0, _model.FrameWidth, _model.FrameHeight); Sync(); })));
        Panel.Children.Add(Ui.Text(Loc["media.crop.hint"], 12, muted: true));

        Overlay.Draw = DrawBox;
        Overlay.CursorAt = p => CursorFor(BoxMath.HitTest(_model.Box, p.X, p.Y, 10 / Overlay.Scale));
        Overlay.Pressed = p =>
        {
            _handle = BoxMath.HitTest(_model.Box, p.X, p.Y, 10 / Overlay.Scale);
            _from = p;
            _startBox = _model.Box;
        };
        Overlay.Dragged = p =>
        {
            if (_handle == BoxHandle.None) return;
            _model.Drag(_startBox, _handle, p.X - _from.X, p.Y - _from.Y);
            Sync();
        };
        Overlay.Released = _ => _handle = BoxHandle.None;
        Sync();
    }

    internal MediaCropModel Model => _model;
    internal void ChooseAspect(string aspect) => _aspect.SelectedIndex = Array.IndexOf(MediaCropModel.Aspects, aspect);
    internal void TypePixels(string x, string y, string w, string h) { _x.Text = x; _y.Text = y; _w.Text = w; _h.Text = h; CommitPixels(); }

    private TextBox NumberBox()
    {
        var box = new TextBox { Width = 64, MinHeight = 28, FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 10, 0) };
        box.LostKeyboardFocus += (_, _) => CommitPixels();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitPixels(); e.Handled = true; } };
        return box;
    }

    private void CommitPixels()
    {
        if (_syncing) return;
        static double? Read(TextBox t) => MediaTimeText.TryParse(t.Text, out var v) ? v : null;   // plain numbers, Arabic digits too
        if (Read(_x) is { } x && Read(_y) is { } y && Read(_w) is { } w && Read(_h) is { } h) _model.SetPixels(x, y, w, h);
        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        var (x, y, w, h) = _model.EvenPixels();
        _x.Text = x.ToString(CultureInfo.InvariantCulture);
        _y.Text = y.ToString(CultureInfo.InvariantCulture);
        _w.Text = w.ToString(CultureInfo.InvariantCulture);
        _h.Text = h.ToString(CultureInfo.InvariantCulture);
        _aspect.SelectedIndex = Array.IndexOf(MediaCropModel.Aspects, _model.Aspect);
        _syncing = false;
        Refresh();
    }

    private static Cursor CursorFor(BoxHandle h) => h switch
    {
        BoxHandle.Move => Cursors.SizeAll,
        BoxHandle.Left or BoxHandle.Right => Cursors.SizeWE,
        BoxHandle.Top or BoxHandle.Bottom => Cursors.SizeNS,
        BoxHandle.TopLeft or BoxHandle.BottomRight => Cursors.SizeNWSE,
        BoxHandle.TopRight or BoxHandle.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private void DrawBox(DrawingContext dc)
    {
        var picture = Overlay.PictureRect;
        if (picture.IsEmpty) return;
        var box = Overlay.ToScreen(_model.Box);
        var shade = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        dc.DrawRectangle(shade, null, new Rect(picture.Left, picture.Top, picture.Width, Math.Max(0, box.Top - picture.Top)));
        dc.DrawRectangle(shade, null, new Rect(picture.Left, box.Bottom, picture.Width, Math.Max(0, picture.Bottom - box.Bottom)));
        dc.DrawRectangle(shade, null, new Rect(picture.Left, box.Top, Math.Max(0, box.Left - picture.Left), box.Height));
        dc.DrawRectangle(shade, null, new Rect(box.Right, box.Top, Math.Max(0, picture.Right - box.Right), box.Height));

        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1);
        for (var i = 1; i < 3; i++)
        {
            dc.DrawLine(thirds, new Point(box.Left + box.Width * i / 3, box.Top), new Point(box.Left + box.Width * i / 3, box.Bottom));
            dc.DrawLine(thirds, new Point(box.Left, box.Top + box.Height * i / 3), new Point(box.Right, box.Top + box.Height * i / 3));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 2), box);
        foreach (var (hx, hy) in new[] { (box.Left, box.Top), (box.Right, box.Top), (box.Left, box.Bottom), (box.Right, box.Bottom),
                                         (box.Left + box.Width / 2, box.Top), (box.Left + box.Width / 2, box.Bottom), (box.Left, box.Top + box.Height / 2), (box.Right, box.Top + box.Height / 2) })
            dc.DrawRectangle(Brushes.White, new Pen(Ui.Theme.Accent, 1), new Rect(hx - 5, hy - 5, 10, 10));
    }

    protected override string? Problem => _model.IsWholeFrame ? "media.crop.nothing" : null;

    protected override string? Note
    {
        get
        {
            var (_, _, w, h) = _model.EvenPixels();
            return Loc.Format("media.crop.result", $"{w} × {h}");
        }
    }

    protected override Dictionary<string, object?> Values() => _model.ToOptions();
}
