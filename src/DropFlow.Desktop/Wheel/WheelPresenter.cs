// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Localization;
using DropFlow.Core.Session;
using DropFlow.Core.Wheel;
using DropFlow.Overlay;

namespace DropFlow.Desktop.Wheel;

/// <summary>Maps Core's <see cref="WheelSession"/> onto the Avalonia wheel window (same contract as the Windows presenter).</summary>
public sealed class WheelPresenter(WheelWindow window, ILocalizer loc) : IWheelView
{
    public WheelSession? Session { get; set; }

    /// <summary>Set just before opening the wheel for known files (click mode): the wheel then takes focus.</summary>
    public bool NextInteractive { get; set; }

    public void Show(WheelKind kind, int x, int y)
    {
        var interactive = NextInteractive;
        NextInteractive = false;
        window.ShowAt(x, y, interactive);
        if (interactive && Session is not null) window.SetFiles(Session.Files);
    }

    public void SetItems(WheelKind kind, IReadOnlyList<Wedge> wedges, int fileCount)
    {
        window.HideAddList();
        var items = wedges.Select(ToItem).ToList();
        window.Wheel.SetItems(items, loc.IsRightToLeft);
        var hint = fileCount == 0 ? loc["wheel.drop"] : fileCount == 1 ? loc["wheel.file"] : loc.Format("wheel.files", fileCount);
        var nothing = items.Count == 0 && fileCount > 0;
        window.Wheel.SetCenter(nothing ? loc["wheel.nothing"] : loc["wheel." + Key(kind)], nothing ? "" : hint);
    }

    public void ShowAddList(WheelKind kind, IReadOnlyList<Wedge> addable)
    {
        var groups = Enum.GetValues<WheelKind>()
            .Select(k => new WheelItemGroup(loc["wheel.add.group." + Key(k)], addable.Where(w => w.Wheel == k).Select(ToItem).ToList()))
            .ToList();
        window.ShowAddList(loc["wheel.add.title." + Key(kind)], loc["wheel.add.hint"], loc["wheel.add.back"], groups, loc.IsRightToLeft);
    }

    public void Hide() => window.HideWheel();

    public void OnItemContext(int index)
    {
        if (Session is not { } session || !session.CanUnpin(index)) return;
        window.ShowItemMenu(loc["wheel.unpin"], loc.IsRightToLeft, () => session.Unpin(index));
    }

    public WheelItem ToItem(Wedge w) => new(
        w.Id,
        w.IsAdd ? loc["wheel.add"] : w.Label,
        w.State == WedgeState.NeedsEngine ? WheelItemState.NeedsEngine : WheelItemState.Ready,
        w.NeedsOptions,
        w.IsAdd ? WheelItemRole.Add : w.Pinned ? WheelItemRole.Pinned : WheelItemRole.Normal);

    private static string Key(WheelKind kind) => kind.ToString().ToLowerInvariant();
}
