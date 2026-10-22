// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.App.Services;
using DropFlow.Engines.Images;
using ImageMagick;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// Add background (img.background): solid colours, gradients (two colours and an angle), the picture itself blurred, padding, rounded
/// corners, a soft shadow and the canvas shape, with a live preview from the engine's <see cref="BackgroundComposer"/>.
/// </summary>
internal sealed class BackgroundWindow : VisualEditorWindow
{
    /// <summary>Gradient presets: DropFlow's indigo→cyan first, then a few calm pairs.</summary>
    public static readonly (string A, string B)[] Gradients =
    [
        ("#4338ca", "#0e7490"), ("#f97316", "#ec4899"), ("#0ea5e9", "#22c55e"), ("#a855f7", "#6366f1"),
        ("#111827", "#4b5563"), ("#fde68a", "#fca5a5"), ("#e0f2fe", "#c7d2fe"), ("#064e3b", "#10b981"),
    ];

    public static readonly string[] Aspects = ["original", "1:1", "4:3", "3:2", "16:9", "9:16", "4:5", "3:4"];

    private readonly PreviewScheduler<BitmapSource> _renders = new();
    private readonly StackPanel _solidPanel = new();
    private readonly StackPanel _gradientPanel = new();
    private PreviewSource? _source;
    private string _kind = "gradient";
    private string _solid = "#ffffff";
    private string _a = Gradients[0].A;
    private string _b = Gradients[0].B;
    private double _angle = 135;
    private string _aspect = "original";
    private double _padding = 8;
    private double _radius = 4;
    private bool _shadow = true;

    public BackgroundWindow(UiKit ui, IReadOnlyList<string> files) : base(ui, ui.Loc["editor.background.title"], files)
    {
        Side.Children.Add(EditorControls.Label(ui, ui.Loc["editor.background.kind"]));
        var kinds = new[] { "gradient", "solid", "blur" }.Select(k => (ui.Loc["editor.background.kind." + k], k));
        Side.Children.Add(EditorControls.Segmented(ui, kinds, _kind, k => { _kind = k; ShowKind(); Render(); }).Panel);

        // gradient: presets, two colours, angle
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        Action<string>? setA = null, setB = null;
        foreach (var (a, b) in Gradients)
        {
            var chip = new Border
            {
                Width = 40, Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand,
                Background = new LinearGradientBrush(EditorControls.ColorOf(a), EditorControls.ColorOf(b), 45), ToolTip = $"{a} → {b}",
                BorderBrush = ui.Theme.PanelBorder, BorderThickness = new Thickness(1),
            };
            chip.MouseLeftButtonUp += (_, _) => { _a = a; _b = b; setA?.Invoke(a); setB?.Invoke(b); Render(); };
            presets.Children.Add(chip);
        }
        _gradientPanel.Children.Add(presets);
        _gradientPanel.Children.Add(EditorControls.Label(ui, ui.Loc["editor.background.from"]));
        var (pickA, sa) = EditorControls.ColorPicker(ui, _a, c => { _a = c; Render(); });
        _gradientPanel.Children.Add(pickA);
        _gradientPanel.Children.Add(EditorControls.Label(ui, ui.Loc["editor.background.to"]));
        var (pickB, sb) = EditorControls.ColorPicker(ui, _b, c => { _b = c; Render(); });
        _gradientPanel.Children.Add(pickB);
        (setA, setB) = (sa, sb);
        _gradientPanel.Children.Add(EditorControls.SliderRow(ui, ui.Loc["editor.background.angle"], 0, 360, _angle, 135, v => { _angle = Math.Round(v); Render(); }).Row);
        Side.Children.Add(_gradientPanel);

        _solidPanel.Children.Add(EditorControls.ColorPicker(ui, _solid, c => { _solid = c; Render(); }).Panel);
        Side.Children.Add(_solidPanel);

        Side.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.background.aspect"]));
        var aspects = Aspects.Select(a => (a == "original" ? ui.Loc["opt.img.background.aspect.original"] : a, a));
        Side.Children.Add(EditorControls.Segmented(ui, aspects, _aspect, a => { _aspect = a; Render(); }).Panel);

        Side.Children.Add(EditorControls.SliderRow(ui, ui.Loc["opt.img.background.padding"], 0, 40, _padding, 8, v => { _padding = Math.Round(v); Render(); }).Row);
        Side.Children.Add(EditorControls.SliderRow(ui, ui.Loc["opt.img.background.radius"], 0, 50, _radius, 0, v => { _radius = Math.Round(v); Render(); }).Row);
        Side.Children.Add(EditorControls.Check(ui, ui.Loc["opt.img.background.shadow"], _shadow, v => { _shadow = v; Render(); }));

        ShowKind();
        _renders.BusyChanged += () => Preview.ShowBusy(_renders.IsBusy);
        Loaded += async (_, _) => await LoadAsync();
    }

    public override bool IsReady => _source is not null && _renders.Applied > 0 && !_renders.IsBusy;

    /// <summary>The value of the converter's <c>background</c> option for the current choice.</summary>
    internal string BackgroundSpec => _kind switch
    {
        "solid" => _solid,
        "blur" => "blur",
        _ => string.Create(CultureInfo.InvariantCulture, $"gradient:{_a}:{_b}:{_angle}"),
    };

    internal void ChooseKind(string kind) { _kind = kind; ShowKind(); Render(); }

    private void ShowKind()
    {
        _gradientPanel.Visibility = _kind == "gradient" ? Visibility.Visible : Visibility.Collapsed;
        _solidPanel.Visibility = _kind == "solid" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadAsync()
    {
        try
        {
            var path = Files[0];
            // a smaller copy is plenty: the result is the picture on a bigger canvas
            _source = await Task.Run(() => PreviewImage.Load(path, 900));
            Preview.SetImage(PreviewImage.ToBitmap(_source.Image));
            Render();
        }
        catch (Exception ex) when (ex is MagickException or System.IO.IOException or UnauthorizedAccessException)
        {
            FileLog.Error("background editor preview", ex);
            Message.Text = Ui.Loc["editor.previewFailed"];
        }
    }

    private void Render()
    {
        if (_source is not { } source) return;
        var (spec, aspect, padding, radius, shadow) = (BackgroundSpec, _aspect, _padding, _radius, _shadow);
        _ = _renders.RequestAsync(ct =>
        {
            MagickImage copy;
            lock (source) { ct.ThrowIfCancellationRequested(); copy = (MagickImage)source.Image.Clone(); }
            using (copy)
            using (var canvas = BackgroundComposer.Compose(copy, spec, aspect, padding, radius, shadow))
                return PreviewImage.ToBitmap(canvas);
        }, Preview.SetImage, ex => FileLog.Error("background preview", ex));
    }

    protected override IReadOnlyDictionary<string, object?>? BuildOptions() => new Dictionary<string, object?>
    {
        ["background"] = BackgroundSpec,
        ["aspect"] = _aspect,
        ["padding"] = _padding,
        ["radius"] = _radius,
        ["shadow"] = _shadow,
    };

    protected override void OnClosedCleanup()
    {
        _renders.Cancel();
        if (_source is { } s) lock (s) s.Dispose();
    }
}
