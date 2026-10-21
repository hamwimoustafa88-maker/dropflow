using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DropFlow.App.Services;
using DropFlow.Core.App;
using DropFlow.Core.Operations;
using DropFlow.Engines.Media;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Overlay;

namespace DropFlow.App.Views;

/// <summary>
/// DropFlow Encoder: pick a preset (or tune the settings), see what will be produced, and encode the files one after another.
/// All the encoding decisions live in <see cref="EncoderPlanner"/>; this window only collects settings and shows progress.
/// </summary>
internal sealed class EncoderWindow : Window
{
    private sealed record Row(string Path, TextBlock Status, ProgressBar Bar);

    private static readonly int[] Heights = [2160, 1440, 1080, 720, 480, 360];
    private static readonly int[] AudioBitrates = [64, 96, 128, 160, 192, 256, 320];

    private readonly UiKit _ui;
    private readonly IEngineProvider _engines;
    private readonly IReadOnlyList<string> _files;
    private readonly Action<string, ConversionResult> _onEncoded;
    private readonly List<Row> _rows = [];

    private readonly ComboBox _preset = new();
    private readonly ComboBox _container = new();
    private readonly ComboBox _codec = new();
    private readonly ComboBox _hardware = new();
    private readonly ComboBox _rate = new();
    private readonly ComboBox _height = new();
    private readonly ComboBox _audioCodec = new();
    private readonly ComboBox _audioBitrate = new();
    private readonly TextBox _rateValue = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox _trimStart = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox _trimEnd = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
    private readonly CheckBox _audioOnly;
    private readonly CheckBox _removeAudio;
    private readonly TextBlock _source;
    private readonly TextBlock _summary;
    private readonly TextBlock _message;
    private readonly TextBlock _presetNote;
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _folder;
    private readonly StackPanel _settings = new();
    private readonly ComboBoxItem _customPreset;

    private EncoderSettings _base = EncoderPresets.All[0].Settings;
    private MediaInfo? _info;
    private bool _loading;
    private int _planVersion;
    private CancellationTokenSource? _cts;
    private string? _lastOutput;

    public EncoderWindow(UiKit ui, IEngineProvider engines, IReadOnlyList<string> files, Action<string, ConversionResult> onEncoded)
    {
        _ui = ui;
        _engines = engines;
        _files = files;
        _onEncoded = onEncoded;
        ui.Style(this, 640, ui.Loc["encoder.title"]);
        SizeToContent = SizeToContent.Manual;
        Height = 780;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 520;
        MinHeight = 480;

        var root = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };

        // source
        _source = ui.Text(files.Count == 1 ? Path.GetFileName(files[0]) : ui.Loc.Format("result.files", files.Count), 13, muted: true);
        root.Children.Add(_source);

        // preset
        root.Children.Add(ui.SectionTitle(ui.Loc["encoder.preset"]));
        foreach (var p in EncoderPresets.All)
            _preset.Items.Add(new ComboBoxItem { Content = ui.Loc["encoder.preset." + p.Id], Tag = p });
        _customPreset = new ComboBoxItem { Content = ui.Loc["encoder.custom"], Tag = null };
        _preset.Items.Add(_customPreset);
        _preset.MinHeight = 32;
        _presetNote = ui.Text("", 12, muted: true);
        root.Children.Add(ui.Card(new StackPanel { Children = { _preset, _presetNote } }));

        // settings
        root.Children.Add(ui.SectionTitle(ui.Loc["encoder.settings"]));
        _audioOnly = new CheckBox { Content = ui.Loc["encoder.audioOnly"], Foreground = ui.Theme.Text, Margin = new Thickness(0, 4, 0, 4) };
        _removeAudio = new CheckBox { Content = ui.Loc["encoder.removeAudio"], Foreground = ui.Theme.Text, Margin = new Thickness(0, 4, 0, 4) };

        Fill(_container, [("MP4", ContainerFormat.Mp4), ("MKV", ContainerFormat.Mkv), ("WebM", ContainerFormat.Webm), ("MOV", ContainerFormat.Mov), ("GIF", ContainerFormat.Gif)]);
        Fill(_codec, [("H.264", VideoCodec.H264), ("H.265 (HEVC)", VideoCodec.H265), ("AV1", VideoCodec.Av1), ("VP9", VideoCodec.Vp9)]);
        Fill(_hardware, [(ui.Loc["encoder.hardware.auto"], HardwarePreference.Auto), (ui.Loc["encoder.hardware.none"], HardwarePreference.None)]);
        Fill(_rate, [(ui.Loc["encoder.rate.crf"], RateControl.Crf), (ui.Loc["encoder.rate.bitrate"], RateControl.Bitrate), (ui.Loc["encoder.rate.targetSize"], RateControl.TargetSize)]);
        _height.Items.Add(new ComboBoxItem { Content = ui.Loc["encoder.maxHeight.keep"], Tag = null });
        foreach (var h in Heights) _height.Items.Add(new ComboBoxItem { Content = $"{h}p", Tag = h });
        Fill(_audioCodec, [("AAC", AudioCodec.Aac), ("Opus", AudioCodec.Opus), ("MP3", AudioCodec.Mp3), ("FLAC", AudioCodec.Flac)]);
        foreach (var b in AudioBitrates) _audioBitrate.Items.Add(new ComboBoxItem { Content = b.ToString(CultureInfo.InvariantCulture), Tag = b });

        _settings.Children.Add(_audioOnly);
        _settings.Children.Add(Labeled(ui.Loc["encoder.container"], _container));
        _settings.Children.Add(Labeled(ui.Loc["encoder.videoCodec"], _codec));
        _settings.Children.Add(Labeled(ui.Loc["encoder.hardware"], _hardware));
        _settings.Children.Add(Labeled(ui.Loc["encoder.rate"], _rate));
        _settings.Children.Add(Labeled("", _rateValue));
        _settings.Children.Add(Labeled(ui.Loc["encoder.maxHeight"], _height));
        _settings.Children.Add(Labeled(ui.Loc["encoder.audioCodec"], _audioCodec));
        _settings.Children.Add(Labeled(ui.Loc["encoder.audioBitrate"], _audioBitrate));
        _settings.Children.Add(_removeAudio);
        _settings.Children.Add(Labeled(ui.Loc["encoder.trimStart"], _trimStart));
        _settings.Children.Add(Labeled(ui.Loc["encoder.trimEnd"], _trimEnd));
        root.Children.Add(ui.Card(_settings));

        // plan preview
        _summary = ui.Text("", 13);
        _message = ui.Text("", 12);
        _message.Foreground = System.Windows.Media.Brushes.IndianRed;
        root.Children.Add(ui.Card(new StackPanel { Children = { _summary, _message } }));

        // queue
        root.Children.Add(ui.SectionTitle(ui.Loc["encoder.queue"]));
        foreach (var file in files) root.Children.Add(MakeRow(file));

        // buttons
        _start = ui.Primary(ui.Loc["encoder.start"]);
        _stop = ui.Secondary(ui.Loc["encoder.stop"]);
        _folder = ui.Secondary(ui.Loc["encoder.openFolder"]);
        _stop.Visibility = Visibility.Collapsed;
        _folder.Visibility = Visibility.Collapsed;
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _folder.Click += (_, _) => { if (_lastOutput is not null) Explorer.Reveal(_lastOutput); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in new[] { _folder, _stop, _start }) { b.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(b); }
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _preset.SelectionChanged += (_, _) => OnPresetChanged();
        foreach (var combo in new[] { _container, _codec, _hardware, _height, _audioCodec, _audioBitrate })
            combo.SelectionChanged += (_, _) => OnSettingChanged();
        _rate.SelectionChanged += (_, _) => { if (!_loading) ShowDefaultRateValue(); OnSettingChanged(); };
        foreach (var box in new[] { _rateValue, _trimStart, _trimEnd }) box.TextChanged += (_, _) => OnSettingChanged();
        _audioOnly.Click += (_, _) => OnSettingChanged();
        _removeAudio.Click += (_, _) => OnSettingChanged();

        Closing += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) => await ProbeAsync();

        _preset.SelectedIndex = 0; // applies the first preset and fills every control
    }

    // ------------------------------------------------------------------------------------------------------------------ test hooks

    /// <summary>What the controls currently describe (null, with <see cref="ErrorText"/> set, when an input is invalid).</summary>
    internal EncoderSettings? CurrentSettings => ReadSettings();

    internal string ErrorText => _message.Text;

    internal void ChoosePreset(string id) =>
        _preset.SelectedItem = _preset.Items.OfType<ComboBoxItem>().First(i => i.Tag is EncoderPreset p && p.Id == id);

    internal void TypeTrimStart(string text) => _trimStart.Text = text;

    // ------------------------------------------------------------------------------------------------------------------ building

    private static void Fill<T>(ComboBox combo, (string Label, T Value)[] items)
    {
        foreach (var (label, value) in items) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.MinHeight = 30;
    }

    private UIElement Labeled(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var text = _ui.Text(label, 13, wrap: false);
        text.Width = 190;
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private UIElement MakeRow(string file)
    {
        var name = _ui.Text(Path.GetFileName(file), 13, weight: FontWeights.SemiBold, wrap: false);
        var status = _ui.Text(_ui.Loc["encoder.status.waiting"], 12, muted: true, wrap: false);
        var bar = new ProgressBar { Height = 4, Maximum = 1, Margin = new Thickness(0, 2, 0, 0), Foreground = _ui.Theme.Accent, Background = _ui.Theme.Wedge, BorderThickness = new Thickness(0) };
        _rows.Add(new Row(file, status, bar));
        return _ui.Card(new StackPanel { Children = { name, status, bar } }, 10);
    }

    // ------------------------------------------------------------------------------------------------------------------ settings <-> controls

    private static T Selected<T>(ComboBox combo) => (T)((ComboBoxItem)combo.SelectedItem).Tag;

    private static void Select<T>(ComboBox combo, T value)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectedItem = item;
    }

    private static int DefaultCrf(VideoCodec codec) => codec switch { VideoCodec.H265 => 28, VideoCodec.Vp9 => 32, VideoCodec.Av1 => 30, _ => 23 };

    private void OnPresetChanged()
    {
        if (_loading || _preset.SelectedItem is not ComboBoxItem { Tag: EncoderPreset preset }) return;
        ApplySettings(preset.Settings);
        _presetNote.Text = _ui.Loc["encoder.preset." + preset.Id + ".desc"];
        _ = RefreshPlanAsync();
    }

    private void OnSettingChanged()
    {
        if (_loading) return;
        _loading = true;
        _preset.SelectedItem = _customPreset;
        _presetNote.Text = "";
        _loading = false;
        UpdateEnabled();
        _ = RefreshPlanAsync();
    }

    private void ApplySettings(EncoderSettings s)
    {
        _loading = true;
        try
        {
            _base = s;
            Select(_container, s.Container);
            Select(_codec, s.VideoCodec == VideoCodec.Copy ? VideoCodec.H264 : s.VideoCodec);
            Select(_hardware, s.Hardware == HardwarePreference.None ? HardwarePreference.None : HardwarePreference.Auto);
            Select(_rate, s.RateControl);
            _rateValue.Text = s.RateControl switch
            {
                RateControl.Bitrate => (s.VideoBitrateKbps ?? 4000).ToString(CultureInfo.InvariantCulture),
                RateControl.TargetSize => (s.TargetSizeMb ?? 25).ToString(CultureInfo.InvariantCulture),
                _ => (s.Crf ?? DefaultCrf(s.VideoCodec)).ToString(CultureInfo.InvariantCulture),
            };
            Select(_height, s.Canvas is null ? s.MaxHeight : null);
            Select(_audioCodec, s.AudioCodec == AudioCodec.Copy ? AudioCodec.Aac : s.AudioCodec);
            Select(_audioBitrate, AudioBitrates.OrderBy(b => Math.Abs(b - s.AudioBitrateKbps)).First());
            _audioOnly.IsChecked = s.ExtractAudioOnly;
            _removeAudio.IsChecked = s.RemoveAudio;
            _trimStart.Text = s.TrimStart is { } a ? TimeInput.Format(a) : "";
            _trimEnd.Text = s.TrimEnd is { } b ? TimeInput.Format(b) : "";
        }
        finally
        {
            _loading = false;
        }
        UpdateEnabled();
    }

    private void ShowDefaultRateValue()
    {
        _loading = true;
        _rateValue.Text = Selected<RateControl>(_rate) switch
        {
            RateControl.Bitrate => "4000",
            RateControl.TargetSize => "25",
            _ => DefaultCrf(Selected<VideoCodec>(_codec)).ToString(CultureInfo.InvariantCulture),
        };
        _loading = false;
    }

    private void UpdateEnabled()
    {
        var audioOnly = _audioOnly.IsChecked == true;
        foreach (UIElement e in new UIElement[] { _container, _codec, _hardware, _rate, _rateValue, _height })
            e.IsEnabled = !audioOnly;
        _removeAudio.IsEnabled = !audioOnly;
    }

    /// <summary>Builds the settings from the controls, or sets an error message and returns null.</summary>
    private EncoderSettings? ReadSettings()
    {
        _message.Text = "";
        // digits typed on an Arabic keyboard (٠-٩) are accepted too
        if (!TimeInput.TryParse(OptionForm.NormalizeDigits(_trimStart.Text), out var start) || !TimeInput.TryParse(OptionForm.NormalizeDigits(_trimEnd.Text), out var end))
        {
            _message.Text = _ui.Loc["encoder.invalidTime"];
            return null;
        }

        var rate = Selected<RateControl>(_rate);
        if (!OptionForm.TryParseNumber(_rateValue.Text, out var value) || value <= 0)
        {
            _message.Text = _ui.Loc["encoder.invalidNumber"];
            return null;
        }

        var height = Selected<int?>(_height);
        return _base with
        {
            Container = Selected<ContainerFormat>(_container),
            VideoCodec = Selected<VideoCodec>(_codec),
            Hardware = Selected<HardwarePreference>(_hardware),
            RateControl = rate,
            Crf = rate == RateControl.Crf ? (int)Math.Round(value) : null,
            VideoBitrateKbps = rate == RateControl.Bitrate ? (int)Math.Round(value) : null,
            TargetSizeMb = rate == RateControl.TargetSize ? value : null,
            MaxHeight = height,
            Canvas = height is null ? _base.Canvas : null,
            AudioCodec = Selected<AudioCodec>(_audioCodec),
            AudioBitrateKbps = Selected<int>(_audioBitrate),
            ExtractAudioOnly = _audioOnly.IsChecked == true,
            RemoveAudio = _removeAudio.IsChecked == true && _audioOnly.IsChecked != true,
            TrimStart = start,
            TrimEnd = end,
        };
    }

    // ------------------------------------------------------------------------------------------------------------------ probe and preview

    private async Task ProbeAsync()
    {
        try
        {
            _info = await MediaEncoder.ProbeAsync(_engines, _files[0]);
            var info = _info;
            var dur = info.DurationSeconds is { } d ? TimeInput.Format(TimeSpan.FromSeconds(d)) : "—";
            var size = info.SizeBytes is { } bytes ? $"{bytes / 1_000_000.0:0.#} MB" : "";
            var detail = info.Video is { } v
                ? $"{dur} · {v.DisplayWidth}×{v.DisplayHeight} · {v.CodecName} · {size}"
                : $"{dur} · {info.Audio?.CodecName} · {size}";
            _source.Text = _files.Count == 1 ? $"{Path.GetFileName(_files[0])}  —  {detail}" : $"{_ui.Loc.Format("result.files", _files.Count)}  —  {detail}";
            if (!info.HasVideo) _audioOnly.IsChecked = true;
        }
        catch (Exception ex) when (ex is MediaProbeException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _info = null;
            FileLog.Error("encoder probe", ex);
        }
        UpdateEnabled();
        await RefreshPlanAsync();
    }

    private async Task RefreshPlanAsync()
    {
        var version = ++_planVersion;
        var settings = ReadSettings();
        if (settings is null) { _summary.Text = ""; return; }
        if (_info is null) { _summary.Text = _ui.Loc["encoder.noProbe"]; return; }

        EncoderPlan plan;
        try
        {
            plan = await MediaEncoder.PlanAsync(_engines, _info, settings);
        }
        catch (Exception ex) when (ex is MediaProbeException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (version == _planVersion) _summary.Text = ex.Message;
            return;
        }
        if (version != _planVersion) return;

        if (!plan.Ok)
        {
            _summary.Text = "";
            _message.Text = plan.Error ?? "";
            return;
        }

        var parts = new List<string> { plan.OutputExtension.ToUpperInvariant() };
        if ((plan.VideoEncoder ?? plan.AudioEncoder) is { } encoder) parts.Add(encoder);
        if (plan.OutputWidth is { } w && plan.OutputHeight is { } h) parts.Add($"{w}×{h}");
        var bitrate = (plan.VideoBitrateKbps ?? 0) + (plan.AudioBitrateKbps ?? 0);
        parts.Add(plan.VideoBitrateKbps is not null && plan.OutputDurationSeconds is { } seconds && bitrate > 0
            ? _ui.Loc.Format("encoder.estimate", (bitrate * seconds / 8000.0).ToString("0.#", CultureInfo.InvariantCulture))
            : _ui.Loc["encoder.estimateUnknown"]);

        _summary.Text = _ui.Loc.Format("encoder.output", string.Join(" · ", parts));
        _message.Text = plan.Warnings.Count == 0 ? "" : string.Join("\n", plan.Warnings);
    }

    // ------------------------------------------------------------------------------------------------------------------ running

    private async Task StartAsync()
    {
        var settings = ReadSettings();
        if (settings is null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _settings.IsEnabled = false;
        _preset.IsEnabled = false;
        _start.Visibility = Visibility.Collapsed;
        _folder.Visibility = Visibility.Collapsed;
        _stop.Visibility = Visibility.Visible;

        foreach (var row in _rows)
        {
            row.Status.Text = _ui.Loc["encoder.status.waiting"];
            row.Bar.Value = 0;
        }

        foreach (var row in _rows)
        {
            if (token.IsCancellationRequested)
            {
                row.Status.Text = _ui.Loc["encoder.status.cancelled"];
                continue;
            }

            row.Status.Text = _ui.Loc.Format("encoder.status.running", 0);
            var progress = new Progress<JobProgress>(p =>
            {
                row.Bar.Value = p.Fraction;
                row.Status.Text = _ui.Loc.Format("encoder.status.running", (int)(p.Fraction * 100));
            });

            ConversionResult result;
            try
            {
                result = await MediaEncoder.RunAsync(_engines, row.Path, settings, progress, token);
            }
            catch (OperationCanceledException)
            {
                row.Status.Text = _ui.Loc["encoder.status.cancelled"];
                continue;
            }

            if (result.Success && result.Outputs.Count > 0)
            {
                _lastOutput = result.Outputs[0];
                row.Bar.Value = 1;
                var size = new FileInfo(_lastOutput).Length / 1_000_000.0;
                row.Status.Text = _ui.Loc.Format("encoder.status.done", $"{size:0.#} MB");
            }
            else
            {
                row.Status.Text = _ui.Loc.Format("encoder.status.failed", result.Error ?? "");
                FileLog.Error($"encoder failed for {row.Path}: {result.Error}");
            }
            _onEncoded(row.Path, result);
        }

        _cts.Dispose();
        _cts = null;
        _settings.IsEnabled = true;
        _preset.IsEnabled = true;
        _stop.Visibility = Visibility.Collapsed;
        _start.Visibility = Visibility.Visible;
        _folder.Visibility = _lastOutput is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
