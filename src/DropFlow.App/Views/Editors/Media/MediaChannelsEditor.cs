// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Channels (aud.channels): mono or stereo, a level for each side (into the mono mix, or of each output side), and listening buttons
/// that play ten seconds from the playhead: only the left side, only the right side, or the result with these settings.
/// </summary>
internal sealed class MediaChannelsEditor : MediaEditorWindow
{
    private readonly int _source;
    private readonly MediaPreviewPlayer _preview = new();
    private readonly TextBlock _leftLabel;
    private readonly TextBlock _rightLabel;
    private readonly FrameworkElement _leftRow;
    private readonly FrameworkElement _rightRow;
    private readonly Button _soloLeft;
    private readonly Button _soloRight;
    private readonly Button _result;
    private string _target;
    private double _left;
    private double _right;

    public MediaChannelsEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.channels.run")
    {
        _source = media.Info.Audio?.Channels ?? 2;
        _target = _source == 1 ? "stereo" : "mono";
        Panel.Children.Add(Ui.Text(Loc.Format("media.channels.source", _source == 1 ? Loc["opt.channels.mono"] : _source == 2 ? Loc["opt.channels.stereo"] : _source.ToString(CultureInfo.InvariantCulture)), 13, muted: true));
        Panel.Children.Add(Row(
            Label("media.channels.target"),
            Radio("channels", "opt.channels.mono", _target == "mono", () => { _target = "mono"; Relabel(); }),
            Radio("channels", "opt.channels.stereo", _target == "stereo", () => { _target = "stereo"; Relabel(); })));

        string Db(double v) => (v > 0 ? "+" : "") + v.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        var (leftRow, _) = LabeledSlider("media.channels.left", -30, 12, 0, 0.5, Db, v => _left = v);
        var (rightRow, _) = LabeledSlider("media.channels.right", -30, 12, 0, 0.5, Db, v => _right = v);
        _leftRow = leftRow;
        _rightRow = rightRow;
        _leftLabel = (TextBlock)((DockPanel)leftRow).Children[0];
        _rightLabel = (TextBlock)((DockPanel)rightRow).Children[0];
        if (context.Declares("leftGain")) { Panel.Children.Add(leftRow); Panel.Children.Add(rightRow); }

        _soloLeft = SmallButton("media.channels.soloLeft", () => _ = ListenAsync("left"));
        _soloRight = SmallButton("media.channels.soloRight", () => _ = ListenAsync("right"));
        _result = SmallButton("media.channels.result", () => _ = ListenAsync("result"));
        var listen = Row(Label("media.bleep.listen"));
        if (_source >= 2) { listen.Children.Add(_soloLeft); listen.Children.Add(_soloRight); }
        listen.Children.Add(_result);
        Panel.Children.Add(listen);
        _preview.Changed += UpdateButtons;
        Relabel();
    }

    internal void SetTarget(string target) { _target = target; Relabel(); }
    internal void SetGains(double left, double right) { _left = left; _right = right; Refresh(); }

    private void Relabel()
    {
        var mix = _target == "mono";
        _leftLabel.Text = Loc[mix ? "media.channels.leftInMix" : "media.channels.left"];
        _rightLabel.Text = Loc[mix ? "media.channels.rightInMix" : "media.channels.right"];
        var sides = !(mix && _source == 1);
        _leftRow.Visibility = _rightRow.Visibility = sides ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private async Task ListenAsync(string what)
    {
        Media.Pause();
        var start = Math.Min(Media.Current, Math.Max(0, Media.Duration - 10));
        var length = Math.Min(10, Media.Duration - start);
        await _preview.ToggleAsync(what, ct => MediaPreview.ChannelsPreviewAsync(Media.Engines, Media.Path, what, _target, _left, _right, _source, start, length, Media.Scratch, ct));
    }

    private void UpdateButtons()
    {
        _soloLeft.Content = Loc[_preview.Playing == "left" ? "media.stop" : "media.channels.soloLeft"];
        _soloRight.Content = Loc[_preview.Playing == "right" ? "media.stop" : "media.channels.soloRight"];
        _result.Content = Loc[_preview.Playing == "result" ? "media.stop" : "media.channels.result"];
    }

    protected override void OnClosedEditor() => _preview.Dispose();

    private bool Gained => Math.Abs(_left) > 1e-9 || Math.Abs(_right) > 1e-9;

    protected override string? Problem => _target switch
    {
        "mono" when _source == 1 => "media.channels.alreadyMono",
        "stereo" when _source == 2 && !Gained => "media.channels.alreadyStereo",
        _ => null,
    };

    protected override Dictionary<string, object?> Values()
    {
        var o = new Dictionary<string, object?> { ["channels"] = _target };
        if (Context.Declares("leftGain")) { o["leftGain"] = Math.Round(_left, 1); o["rightGain"] = Math.Round(_right, 1); }
        return o;
    }
}
