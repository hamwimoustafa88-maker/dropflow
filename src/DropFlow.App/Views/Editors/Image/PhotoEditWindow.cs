// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using DropFlow.App.Services;
using DropFlow.Engines.Images;
using DropFlow.Overlay;
using ImageMagick;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// Edit photo (img.edit): grouped sliders with a live preview rendered by the same <see cref="PhotoEditor"/> the converter uses.
/// Saturation is shown as -100..100 around zero like every other slider (the option itself is 0..200 with 100 = unchanged).
/// </summary>
internal sealed class PhotoEditWindow : VisualEditorWindow
{
    private readonly PreviewScheduler<System.Windows.Media.Imaging.BitmapSource> _renders = new();
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly Action<string> _selectFilter;
    private PreviewSource? _source;
    private PhotoAdjust _adjust = PhotoAdjust.None;
    private bool _loading;

    public PhotoEditWindow(UiKit ui, IReadOnlyList<string> files) : base(ui, ui.Loc["editor.edit.title"], files)
    {
        AddGroup("editor.group.light", PhotoAdjust.LightKeys, expanded: true);
        AddGroup("editor.group.color", PhotoAdjust.ColorKeys, expanded: true);
        AddGroup("editor.group.detail", PhotoAdjust.DetailKeys, expanded: false);
        AddGroup("editor.group.effects", PhotoAdjust.EffectKeys, expanded: false);

        var filters = new[] { "none", "grayscale", "sepia", "invert" }.Select(f => (ui.Loc["opt.img.edit.filter." + f], f));
        var (filterPanel, selectFilter) = EditorControls.Segmented(ui, filters, "none", f => { _adjust = _adjust with { Filter = f }; Render(); });
        _selectFilter = selectFilter;
        Side.Children.Add(EditorControls.Group(ui, ui.Loc["opt.img.edit.filter"], filterPanel, expanded: false));

        var turn = new WrapPanel();
        foreach (var (key, act) in new (string, Action)[]
        {
            ("editor.rotateLeft", () => _adjust = _adjust with { Rotate = (_adjust.Rotate + 270) % 360 }),
            ("editor.rotateRight", () => _adjust = _adjust with { Rotate = (_adjust.Rotate + 90) % 360 }),
            ("editor.flipH", () => _adjust = _adjust with { FlipH = !_adjust.FlipH }),
            ("editor.flipV", () => _adjust = _adjust with { FlipV = !_adjust.FlipV }),
        })
        {
            var b = ui.Secondary(ui.Loc[key]);
            b.MinWidth = 0;
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Click += (_, _) => { act(); Render(); };
            turn.Children.Add(b);
        }
        Side.Children.Add(EditorControls.Group(ui, ui.Loc["editor.group.turn"], turn, expanded: false));

        var reset = ui.Secondary(ui.Loc["editor.resetAll"]);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Click += (_, _) => Apply(PhotoAdjust.None);
        Side.Children.Add(reset);

        _renders.BusyChanged += () => Preview.ShowBusy(_renders.IsBusy);
        Loaded += async (_, _) => await LoadAsync();
    }

    public override bool IsReady => _source is not null && _renders.Applied > 0 && !_renders.IsBusy;

    internal PhotoAdjust Adjust => _adjust;

    /// <summary>Test and snapshot hook: moves one slider (in the slider's units, saturation -100..100).</summary>
    internal void SetSlider(string key, double value) => _sliders[key].Value = value;

    private void AddGroup(string titleKey, IEnumerable<string> keys, bool expanded)
    {
        var panel = new StackPanel();
        foreach (var key in keys)
        {
            var (min, max, neutral) = PhotoAdjust.RangeOf(key);
            if (key == "saturation") (min, max, neutral) = (-100, 100, 0);
            var (row, slider) = EditorControls.SliderRow(Ui, Label(key), min, max, neutral, neutral, v => OnSlider(key, v));
            _sliders[key] = slider;
            panel.Children.Add(row);
        }
        Side.Children.Add(EditorControls.Group(Ui, Ui.Loc[titleKey], panel, expanded));
    }

    private string Label(string key)
    {
        var text = Ui.Loc["opt.img.edit." + key];
        return text == "opt.img.edit." + key ? key : text;
    }

    private void OnSlider(string key, double value)
    {
        if (_loading) return;
        value = Math.Round(value);
        _adjust = _adjust.With(key, key == "saturation" ? 100 + value : value);
        Render();
    }

    private void Apply(PhotoAdjust adjust)
    {
        _loading = true;
        _adjust = adjust;
        foreach (var (key, slider) in _sliders) slider.Value = key == "saturation" ? adjust.Saturation - 100 : adjust.Get(key);
        _selectFilter(adjust.Filter);
        _loading = false;
        Render();
    }

    private async Task LoadAsync()
    {
        try
        {
            var path = Files[0];
            _source = await Task.Run(() => PreviewImage.Load(path));
            var original = PreviewImage.ToBitmap(_source.Image);
            Preview.Original = original;
            Preview.SetImage(original);
            Render();
        }
        catch (Exception ex) when (ex is MagickException or System.IO.IOException or UnauthorizedAccessException)
        {
            FileLog.Error("photo editor preview", ex);
            Message.Text = Ui.Loc["editor.previewFailed"];
        }
    }

    private void Render()
    {
        if (_source is not { } source) return;
        var adjust = _adjust;
        _ = _renders.RequestAsync(ct =>
        {
            MagickImage copy;
            lock (source) { ct.ThrowIfCancellationRequested(); copy = (MagickImage)source.Image.Clone(); }
            using (copy)
            {
                PhotoEditor.Apply(copy, adjust, source.Scale, ct);
                return PreviewImage.ToBitmap(copy);
            }
        }, Preview.SetImage, ex => { FileLog.Error("photo editor render", ex); Message.Text = Ui.Loc["editor.previewFailed"]; });
    }

    protected override IReadOnlyDictionary<string, object?>? BuildOptions()
    {
        var options = _adjust.ToOptions();
        if (options.Count == 0)
        {
            Message.Text = Ui.Loc["editor.nothingChanged"];
            return null;
        }
        return options;
    }

    protected override void OnUndo() => Apply(PhotoAdjust.None);

    protected override void OnClosedCleanup()
    {
        _renders.Cancel();
        if (_source is { } s) lock (s) s.Dispose();
    }
}
