// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Controls;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Speed (vid.speed): a 0.25x-4x slider (logarithmic, so 0.5x and 2x sit at the same distance from 1x) that snaps to common factors,
/// quick buttons, and a live preview: the player plays at the chosen speed. The new length is shown below.
/// </summary>
internal sealed class MediaSpeedEditor : MediaEditorWindow
{
    private readonly Slider _slider;
    private double _speed = 2;

    public MediaSpeedEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.speed.run")
    {
        var (row, slider) = LabeledSlider("media.speed.label", -2, 2, Math.Log2(_speed), 0, p => MediaSpeed.Label(MediaSpeed.Snap(Math.Pow(2, p))), p =>
        {
            _speed = MediaSpeed.Snap(Math.Pow(2, p));
            Media.Speed = _speed;
        });
        _slider = slider;
        _slider.SmallChange = 0.05;
        _slider.LargeChange = 0.25;
        Panel.Children.Add(row);

        var quick = Row();
        foreach (var s in new[] { 0.25, 0.5, 0.75, 1.25, 1.5, 2, 3, 4 })
        {
            var b = Ui.Secondary(MediaSpeed.Label(s));
            b.MinWidth = 0;
            b.MinHeight = 28;
            b.Padding = new System.Windows.Thickness(10, 2, 10, 2);
            b.Margin = new System.Windows.Thickness(0, 0, 6, 0);
            b.Focusable = false;
            b.Click += (_, _) => SetSpeed(s);
            quick.Children.Add(b);
        }
        Panel.Children.Add(quick);
        Panel.Children.Add(Ui.Text(Loc["media.speed.hint"], 12, muted: true));
        Media.Speed = _speed;
    }

    internal double Speed => _speed;

    internal void SetSpeed(double speed) => _slider.Value = Math.Log2(MediaSpeed.Snap(speed));

    protected override string? Problem => Math.Abs(_speed - 1) < 1e-9 ? "media.speed.same" : null;

    protected override string? Note => Loc.Format("media.speed.newLength", MediaText.Len(Loc, MediaSpeed.NewLength(Media.Duration, _speed)), MediaText.Len(Loc, Media.Duration));

    protected override Dictionary<string, object?> Values() => new() { ["speed"] = _speed };
}
