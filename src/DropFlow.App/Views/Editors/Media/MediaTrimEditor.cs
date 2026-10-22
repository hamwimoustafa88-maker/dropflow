// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Input;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Trim (vid.trim, aud.trim): drag the two handles (or type the times, or press I / O at the playhead) to choose the part to keep.
/// Space plays the selection. Video can cut exactly (re-encode); audio can also cut the silence at both ends.
/// </summary>
internal sealed class MediaTrimEditor : MediaEditorWindow
{
    private readonly MediaTrimModel _model;
    private readonly MediaTimeBox _start;
    private readonly MediaTimeBox _end;
    private readonly bool _video;
    private bool _reencode;
    private bool _autoSilence;

    public MediaTrimEditor(MediaEditorContext context, MediaSession media) : base(context, media, "media.trim.run")
    {
        _video = context.Operation == "vid.trim";
        _model = new MediaTrimModel(media.Duration);
        Timeline.Range = _model.Keep;
        Timeline.RangeDragged += (handle, t) =>
        {
            if (handle == RangeHandle.Start) _model.SetStart(t);
            else _model.SetEnd(t);
            Sync();
        };

        _start = new MediaTimeBox(0, t => { _model.SetStart(t); Sync(); });
        _end = new MediaTimeBox(media.Duration, t => { _model.SetEnd(t); Sync(); });
        Panel.Children.Add(Row(
            Label("media.start"), _start, SmallButton("media.setAtPlayhead", () => { _model.SetStart(Media.Current); Sync(); }),
            Spacer(),
            Label("media.end"), _end, SmallButton("media.setAtPlayhead", () => { _model.SetEnd(Media.Current); Sync(); })));

        if (_video && context.Declares("reencode"))
            Panel.Children.Add(Check("media.trim.exact", false, v => _reencode = v));
        if (!_video && context.Declares("autoSilence"))
            Panel.Children.Add(Check("media.trim.autoSilence", false, v => _autoSilence = v));
        Panel.Children.Add(Ui.Text(Loc["media.trim.keys"], 12, muted: true));

        TransportExtras.Children.Add(SmallButton("media.playSelection", PlaySelection));
    }

    internal MediaTrimModel Model => _model;
    internal MediaTimeBox StartBox => _start;
    internal MediaTimeBox EndBox => _end;

    private static System.Windows.FrameworkElement Spacer() => new System.Windows.Controls.Border { Width = 18 };

    private void Sync()
    {
        Timeline.Range = _model.Keep;
        _start.Value = _model.Keep.Start;
        _end.Value = _model.Keep.End;
        Refresh();
    }

    private void PlaySelection() => Media.TogglePlay(_model.Keep.Start, _model.Keep.End);

    /// <summary>Space plays inside the selection: from the playhead when it is inside, otherwise from the start of the selection.</summary>
    protected override void TogglePlay()
    {
        var from = _model.Keep.Contains(Media.Current) && Media.Current < _model.Keep.End - 0.05 ? Media.Current : _model.Keep.Start;
        Media.TogglePlay(from, _model.Keep.End);
    }

    protected override bool OnKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.I or Key.OemOpenBrackets: _model.SetStart(Media.Current); Sync(); return true;
            case Key.O or Key.OemCloseBrackets: _model.SetEnd(Media.Current); Sync(); return true;
            default: return false;
        }
    }

    protected override string? Problem => _model.IsWholeFile && !_autoSilence ? "media.trim.nothing" : null;

    protected override string? Note
    {
        get
        {
            var keeps = Loc.Format("media.trim.keeps", MediaText.Len(Loc, _model.Keep.Length), MediaText.Len(Loc, Media.Duration));
            return _video && !_reencode ? keeps + " " + Loc["media.trim.fastNote"] : keeps;
        }
    }

    protected override Dictionary<string, object?> Values()
    {
        var o = _model.ToOptions();
        if (_video && Context.Declares("reencode")) o["reencode"] = _reencode;
        if (!_video && Context.Declares("autoSilence")) o["autoSilence"] = _autoSilence;
        return o;
    }
}
