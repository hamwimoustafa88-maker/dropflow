// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DropFlow.App.Services;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Normalize (aud.normalize): measures the file as soon as the window opens (the same EBU R128 first pass the tool runs) and shows its
/// loudness, true peak and loudness range; three sliders (with presets for streaming, podcasts and broadcast) set the targets, and the
/// gain the tool will apply is shown before anything runs.
/// </summary>
internal sealed class MediaNormalizeEditor : MediaEditorWindow
{
    private readonly TextBlock _reading;
    private readonly ProgressBar _progress;
    private readonly Slider _target;
    private readonly CancellationTokenSource _measuring = new();
    private LoudnessReading? _measured;
    private bool _silent;
    private double _targetLufs = -16;
    private double _truePeak = -1.5;
    private double _lra = 11;

    public MediaNormalizeEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.normalize.run")
    {
        _reading = Ui.Text(Loc["media.normalize.measuring"], 14, weight: FontWeights.SemiBold);
        _progress = new ProgressBar { Height = 4, Maximum = 1, Foreground = Ui.Theme.Accent, Background = Ui.Theme.Wedge, BorderThickness = new Thickness(0), Margin = new Thickness(0, 2, 0, 8), FlowDirection = FlowDirection.LeftToRight };
        Panel.Children.Add(Label("media.normalize.now"));
        Panel.Children.Add(_reading);
        Panel.Children.Add(_progress);

        var presets = Row(Label("media.normalize.presets"));
        foreach (var (key, lufs) in new[] { ("media.normalize.streaming", -14.0), ("media.normalize.podcast", -16.0), ("media.normalize.broadcast", -23.0) })
            presets.Children.Add(SmallButton(key, () => _target!.Value = lufs));
        Panel.Children.Add(presets);

        (var targetRow, _target) = LabeledSlider("media.normalize.target", -40, -5, _targetLufs, 0.5, v => v.ToString("0.#", CultureInfo.InvariantCulture) + " LUFS", v => _targetLufs = v);
        Panel.Children.Add(targetRow);
        Panel.Children.Add(LabeledSlider("media.normalize.truePeak", -9, 0, _truePeak, 0.1, v => v.ToString("0.0", CultureInfo.InvariantCulture) + " dBTP", v => _truePeak = v).Row);
        Panel.Children.Add(LabeledSlider("media.normalize.lra", 1, 50, _lra, 1, v => v.ToString("0", CultureInfo.InvariantCulture) + " LU", v => _lra = v).Row);
        Panel.Children.Add(Ui.Text(Loc["media.normalize.hint"], 12, muted: true));

        Loaded += async (_, _) => await MeasureAsync();
    }

    internal LoudnessReading? Measured => _measured;
    internal Task Measurement { get; private set; } = Task.CompletedTask;
    internal void SetTarget(double lufs) => _target.Value = lufs;

    private Task MeasureAsync() => Measurement = MeasureCoreAsync();

    private async Task MeasureCoreAsync()
    {
        try
        {
            var progress = new Progress<double>(p => _progress.Value = p);
            _measured = await MediaPreview.MeasureLoudnessAsync(Media.Engines, Media.Path, Media.Duration, progress, _measuring.Token);
            _silent = _measured is null;
            _reading.Text = _measured is { } m
                ? Loc.Format("media.normalize.reading", Db(m.IntegratedLufs) + " LUFS", Db(m.TruePeakDb) + " dBTP", m.RangeLu.ToString("0.0", CultureInfo.InvariantCulture) + " LU")
                : Loc["media.normalize.silent"];
        }
        catch (OperationCanceledException) { return; }
        catch (MediaPreviewException ex)
        {
            FileLog.Info("loudness reading: " + ex.Message);
            _reading.Text = Loc["media.normalize.unreadable"];
        }
        _progress.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private static string Db(double value) => (value > 0 ? "+" : "") + value.ToString("0.0", CultureInfo.InvariantCulture);

    protected override void OnClosedEditor() => _measuring.Cancel();

    protected override string? Problem => _silent ? "media.normalize.silent" : null;

    protected override string? Note => _measured is { } m
        ? Loc.Format("media.normalize.gain", Db(_targetLufs - m.IntegratedLufs) + " dB")
        : null;

    protected override Dictionary<string, object?> Values() => new()
    {
        ["target"] = Math.Round(_targetLufs, 1),
        ["truePeak"] = Math.Round(_truePeak, 1),
        ["lra"] = Math.Round(_lra),
    };
}
